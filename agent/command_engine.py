"""Typed, permission-aware command registration and bounded execution."""

from __future__ import annotations

import json
import logging
import re
import threading
from dataclasses import dataclass
from datetime import UTC, datetime, timedelta
from typing import Any, Callable, Mapping

from agent.exceptions import (
    ApprovalRequiredError,
    CommandExecutionError,
    CommandRegistryError,
    ResourceNotFoundError,
    TaskCancelledError,
)
from agent.permissions import ApprovalManager, PermissionPolicy, RiskLevel

JsonObject = dict[str, Any]
PayloadValidator = Callable[[Mapping[str, Any]], JsonObject]
CommandHandler = Callable[[JsonObject, "CommandContext"], Any]


@dataclass(frozen=True, slots=True)
class CommandContext:
    """Cooperative cancellation and deadline context passed to handlers."""

    task_id: str
    cancellation_event: threading.Event
    deadline: datetime

    def raise_if_cancelled(self) -> None:
        """Abort cooperatively when cancellation has been requested."""
        if self.cancellation_event.is_set():
            raise TaskCancelledError(f"Task cancellation was requested: {self.task_id}")

    def raise_if_expired(self) -> None:
        """Abort cooperatively when the command deadline has passed."""
        if datetime.now(UTC) >= self.deadline:
            raise CommandExecutionError(f"Command deadline expired for task: {self.task_id}")


@dataclass(frozen=True, slots=True)
class CommandDefinition:
    """A typed command and its security metadata."""

    name: str
    description: str
    permission: str
    risk: RiskLevel
    validator: PayloadValidator
    handler: CommandHandler
    payload_contract: str = "{}"
    timeout_seconds: int = 30
    approval_ttl_seconds: int = 900

    def __post_init__(self) -> None:
        if re.fullmatch(r"[a-z][a-z0-9_]*(\.[a-z][a-z0-9_]*)+", self.name) is None:
            raise CommandRegistryError("Command name must use lowercase dotted identifiers.")
        if not self.description or not self.permission:
            raise CommandRegistryError("Command description and permission are required.")
        if not isinstance(self.risk, RiskLevel):
            raise CommandRegistryError("Command risk must be a RiskLevel value.")
        if not callable(self.validator) or not callable(self.handler):
            raise CommandRegistryError("Command validator and handler must be callable.")
        if (
            not isinstance(self.payload_contract, str)
            or not self.payload_contract.strip()
            or self.payload_contract != self.payload_contract.strip()
            or "\x00" in self.payload_contract
            or len(self.payload_contract.encode("utf-8")) > 4096
        ):
            raise CommandRegistryError(
                "Command payload contract must be trimmed non-empty UTF-8 text up to 4096 bytes."
            )
        if not 1 <= self.timeout_seconds <= 300:
            raise CommandRegistryError("Command timeout must be from 1 through 300 seconds.")
        if not 1 <= self.approval_ttl_seconds <= 86_400:
            raise CommandRegistryError("Approval lifetime must be from 1 through 86400 seconds.")


class CommandRegistry:
    """Register immutable command definitions without replacement."""

    def __init__(self) -> None:
        self._commands: dict[str, CommandDefinition] = {}

    def register(self, definition: CommandDefinition) -> None:
        """Register one unique command definition."""
        if definition.name in self._commands:
            raise CommandRegistryError(f"Command is already registered: {definition.name}")
        self._commands[definition.name] = definition

    def get(self, name: str) -> CommandDefinition:
        """Return a registered command or raise an actionable error."""
        try:
            return self._commands[name]
        except KeyError as exc:
            raise ResourceNotFoundError(f"Command is not registered: {name}") from exc

    def list_definitions(self) -> tuple[CommandDefinition, ...]:
        """Return definitions sorted by stable command name."""
        return tuple(self._commands[name] for name in sorted(self._commands))


class CommandEngine:
    """Validate, authorize, approve, execute, and bound registered commands."""

    def __init__(
        self,
        registry: CommandRegistry,
        permissions: PermissionPolicy,
        approvals: ApprovalManager,
        *,
        max_payload_bytes: int = 65_536,
        max_result_bytes: int = 1_048_576,
        logger: logging.Logger | None = None,
    ) -> None:
        if any(
            not isinstance(value, int) or isinstance(value, bool) or value <= 0
            for value in (max_payload_bytes, max_result_bytes)
        ):
            raise ValueError("Command payload and result limits must be greater than zero.")
        self.registry = registry
        self.permissions = permissions
        self.approvals = approvals
        self.max_payload_bytes = max_payload_bytes
        self.max_result_bytes = max_result_bytes
        self._logger = logger or logging.getLogger("ai_engineer.command_engine")

    def execute(
        self,
        name: str,
        payload: Mapping[str, Any],
        *,
        task_id: str,
        cancellation_event: threading.Event,
        approval_id: str | None = None,
    ) -> Any:
        """Execute one command after all validation and security gates."""
        definition = self.registry.get(name)
        self.permissions.authorize(definition.permission)
        validated = self.validate_payload(definition, payload)
        if self.permissions.requires_approval(definition.risk):
            if approval_id is None:
                approval = self.approvals.request(
                    task_id,
                    name,
                    ttl_seconds=definition.approval_ttl_seconds,
                )
                self._logger.info(
                    "command_approval_requested",
                    extra={"task_id": task_id, "command": name, "approval_id": approval.approval_id},
                )
                raise ApprovalRequiredError(
                    f"Human approval is required for command: {name}", approval.approval_id
                )
            self.approvals.consume(approval_id, task_id, name)

        context = CommandContext(
            task_id=task_id,
            cancellation_event=cancellation_event,
            deadline=datetime.now(UTC) + timedelta(seconds=definition.timeout_seconds),
        )
        context.raise_if_cancelled()
        self._logger.info("command_started", extra={"task_id": task_id, "command": name})
        result = definition.handler(validated, context)
        context.raise_if_cancelled()
        context.raise_if_expired()
        try:
            serialized = json.dumps(result, ensure_ascii=False)
        except (TypeError, ValueError) as exc:
            raise CommandExecutionError(f"Command returned non-JSON data: {name}") from exc
        if len(serialized.encode("utf-8")) > self.max_result_bytes:
            raise CommandExecutionError(f"Command result exceeded its byte limit: {name}")
        self._logger.info("command_completed", extra={"task_id": task_id, "command": name})
        return result

    def validate_submission(self, name: str, payload: Mapping[str, Any]) -> JsonObject:
        """Validate registration, permission, payload type, and size before queuing."""
        definition = self.registry.get(name)
        self.permissions.authorize(definition.permission)
        return self.validate_payload(definition, payload)

    def validate_payload(
        self, definition: CommandDefinition, payload: Mapping[str, Any]
    ) -> JsonObject:
        """Apply JSON size bounds and the command-specific validator."""
        if not isinstance(payload, Mapping):
            raise CommandExecutionError("Command payload must be a mapping.")
        try:
            raw = json.dumps(dict(payload), ensure_ascii=False)
        except (TypeError, ValueError) as exc:
            raise CommandExecutionError("Command payload must contain JSON-compatible data.") from exc
        if len(raw.encode("utf-8")) > self.max_payload_bytes:
            raise CommandExecutionError("Command payload exceeded its byte limit.")
        try:
            validated = definition.validator(dict(payload))
        except CommandExecutionError:
            raise
        except (TypeError, ValueError, KeyError) as exc:
            raise CommandExecutionError(
                f"Invalid payload for command {definition.name}: {exc}"
            ) from exc
        if not isinstance(validated, dict):
            raise CommandExecutionError("Command validator must return a dictionary.")
        return validated
