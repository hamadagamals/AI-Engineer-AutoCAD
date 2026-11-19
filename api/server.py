"""Secure localhost-only FastAPI application and Uvicorn entry point."""

from __future__ import annotations

import argparse
from collections import deque
import logging
import os
import sys
from contextlib import asynccontextmanager
from dataclasses import dataclass, field
from pathlib import Path
from typing import AsyncIterator, Mapping

import uvicorn
from fastapi import FastAPI, Request
from fastapi.exceptions import RequestValidationError
from fastapi.responses import JSONResponse
from starlette.middleware.trustedhost import TrustedHostMiddleware
from starlette.types import ASGIApp, Message, Receive, Scope, Send

from agent.app import AppStatus, Application
from agent.provider import ProviderAdapter
from agent.copilot_provider import CopilotProviderAdapter
from agent.manufacturer_provider import ManufacturerProviderAdapter
from agent.exceptions import (
    AIEngineerError,
    ApiConfigurationError,
    ApprovalError,
    CommandExecutionError,
    DeveloperAgentError,
    PermissionDeniedError,
    ResourceNotFoundError,
    TaskEngineError,
)
from agent.version import __version__
from api.routes import (
    BearerAuthenticator,
    protected_router,
    public_router,
    validate_api_token,
)


@dataclass(frozen=True, slots=True)
class ApiServerConfig:
    """Validated server settings that cannot expose the API beyond localhost."""

    token: str = field(repr=False)
    host: str = "127.0.0.1"
    port: int = 8765
    max_request_body_bytes: int = 1_048_576

    def __post_init__(self) -> None:
        validate_api_token(self.token)
        if self.host != "127.0.0.1":
            raise ApiConfigurationError("API host must be exactly 127.0.0.1.")
        if not isinstance(self.port, int) or isinstance(self.port, bool) or not 1024 <= self.port <= 65_535:
            raise ApiConfigurationError("API port must be an integer from 1024 through 65535.")
        if (
            not isinstance(self.max_request_body_bytes, int)
            or isinstance(self.max_request_body_bytes, bool)
            or not 1_024 <= self.max_request_body_bytes <= 10_485_760
        ):
            raise ApiConfigurationError(
                "API request body limit must be from 1024 through 10485760 bytes."
            )

    @classmethod
    def from_environment(
        cls, environment: Mapping[str, str] | None = None
    ) -> ApiServerConfig:
        """Load security settings from environment variables, never repository data."""
        values = os.environ if environment is None else environment
        token = values.get("AI_ENGINEER_API_TOKEN")
        if token is None:
            raise ApiConfigurationError(
                "AI_ENGINEER_API_TOKEN is required and must not be stored in the repository."
            )
        host = values.get("AI_ENGINEER_API_HOST", "127.0.0.1")
        try:
            port = int(values.get("AI_ENGINEER_API_PORT", "8765"))
            body_limit = int(
                values.get("AI_ENGINEER_API_MAX_BODY_BYTES", "1048576")
            )
        except ValueError as exc:
            raise ApiConfigurationError("API port and body limit must be integers.") from exc
        return cls(token=token, host=host, port=port, max_request_body_bytes=body_limit)


class RequestBodyLimitMiddleware:
    """Count streamed request bytes and fail before oversized bodies reach routes."""

    def __init__(self, app: ASGIApp, max_bytes: int) -> None:
        self.app = app
        self.max_bytes = max_bytes

    async def __call__(self, scope: Scope, receive: Receive, send: Send) -> None:
        if scope["type"] != "http":
            await self.app(scope, receive, send)
            return
        for name, value in scope.get("headers", []):
            if name.lower() == b"content-length":
                try:
                    declared_size = int(value)
                except ValueError:
                    declared_size = 0
                if declared_size > self.max_bytes:
                    await _send_body_too_large(scope, receive, send)
                    return

        total = 0
        buffered: deque[Message] = deque()
        while True:
            message = await receive()
            if message["type"] == "http.request":
                total += len(message.get("body", b""))
                if total > self.max_bytes:
                    await _send_body_too_large(scope, receive, send)
                    return
                buffered.append(message)
                if not message.get("more_body", False):
                    break
            else:
                buffered.append(message)
                break

        async def replay_receive() -> Message:
            if buffered:
                return buffered.popleft()
            return await receive()

        await self.app(scope, replay_receive, send)


class SecurityHeadersMiddleware:
    """Add conservative headers to every local API response."""

    def __init__(self, app: ASGIApp) -> None:
        self.app = app

    async def __call__(self, scope: Scope, receive: Receive, send: Send) -> None:
        async def secure_send(message: Message) -> None:
            if message["type"] == "http.response.start":
                headers = list(message.get("headers", []))
                headers.extend(
                    [
                        (b"cache-control", b"no-store"),
                        (b"x-content-type-options", b"nosniff"),
                        (b"x-frame-options", b"DENY"),
                        (b"referrer-policy", b"no-referrer"),
                    ]
                )
                message["headers"] = headers
            await send(message)

        await self.app(scope, receive, secure_send)


class BearerAuthenticationMiddleware:
    """Reject non-health HTTP requests before body parsing or routing."""

    def __init__(self, app: ASGIApp, authenticator: BearerAuthenticator) -> None:
        self.app = app
        self._authenticator = authenticator

    async def __call__(self, scope: Scope, receive: Receive, send: Send) -> None:
        if scope["type"] != "http" or scope.get("path") == "/health":
            await self.app(scope, receive, send)
            return
        values = [
            value
            for name, value in scope.get("headers", [])
            if name.lower() == b"authorization"
        ]
        valid = len(values) == 1 and self._authenticator.matches_bytes(values[0])
        if not valid:
            response = JSONResponse(
                status_code=401,
                content={"detail": "Authentication required."},
                headers={"WWW-Authenticate": "Bearer"},
            )
            await response(scope, receive, send)
            return
        await self.app(scope, receive, send)


async def _send_body_too_large(scope: Scope, receive: Receive, send: Send) -> None:
    response = JSONResponse(
        status_code=413,
        content={"detail": "Request body exceeds the configured byte limit."},
        headers={"Connection": "close"},
    )
    await response(scope, receive, send)


def create_app(
    *,
    token: str,
    project_root: Path | None = None,
    max_request_body_bytes: int = 1_048_576,
    developer_provider: ProviderAdapter | None = None,
    copilot_provider: CopilotProviderAdapter | None = None,
    manufacturer_provider: ManufacturerProviderAdapter | None = None,
    workspace_roots: tuple[Path, ...] | None = None,
    backup_root: Path | None = None,
) -> FastAPI:
    """Create a protected application with no public docs or schema endpoints."""
    validated_token = validate_api_token(token)
    if (
        not isinstance(max_request_body_bytes, int)
        or isinstance(max_request_body_bytes, bool)
        or not 1_024 <= max_request_body_bytes <= 10_485_760
    ):
        raise ApiConfigurationError("Invalid API request body limit.")
    application = Application(
        project_root,
        developer_provider=developer_provider,
        copilot_provider=copilot_provider,
        manufacturer_provider=manufacturer_provider,
        workspace_roots=workspace_roots,
        backup_root=backup_root,
    )
    authenticator = BearerAuthenticator(validated_token)

    @asynccontextmanager
    async def lifespan(_: FastAPI) -> AsyncIterator[None]:
        started_here = application.status is AppStatus.CREATED
        if started_here:
            application.start()
        try:
            yield
        finally:
            if started_here and application.status is AppStatus.RUNNING:
                application.stop()

    api = FastAPI(
        title="AI-Engineer Local API",
        version=__version__,
        docs_url=None,
        redoc_url=None,
        openapi_url=None,
        lifespan=lifespan,
    )
    api.state.application = application
    api.add_middleware(RequestBodyLimitMiddleware, max_bytes=max_request_body_bytes)
    api.add_middleware(
        BearerAuthenticationMiddleware, authenticator=authenticator
    )
    api.add_middleware(SecurityHeadersMiddleware)
    api.add_middleware(
        TrustedHostMiddleware,
        allowed_hosts=["127.0.0.1", "localhost"],
        www_redirect=False,
    )
    api.include_router(public_router(application))
    api.include_router(protected_router(application, authenticator))
    _register_exception_handlers(api)
    return api


def _register_exception_handlers(api: FastAPI) -> None:
    @api.exception_handler(RequestValidationError)
    async def validation_error(
        request: Request, error: RequestValidationError
    ) -> JSONResponse:
        errors = [
            {
                "location": list(item.get("loc", ())),
                "message": item.get("msg", "Invalid value."),
                "type": item.get("type", "validation_error"),
            }
            for item in error.errors()
        ]
        return JSONResponse(status_code=422, content={"detail": errors})

    @api.exception_handler(ResourceNotFoundError)
    async def not_found(request: Request, error: ResourceNotFoundError) -> JSONResponse:
        return JSONResponse(status_code=404, content={"detail": str(error)})

    @api.exception_handler(PermissionDeniedError)
    async def permission_denied(
        request: Request, error: PermissionDeniedError
    ) -> JSONResponse:
        return JSONResponse(status_code=403, content={"detail": str(error)})

    @api.exception_handler(ApprovalError)
    @api.exception_handler(TaskEngineError)
    @api.exception_handler(DeveloperAgentError)
    async def conflict(request: Request, error: AIEngineerError) -> JSONResponse:
        return JSONResponse(status_code=409, content={"detail": str(error)})

    @api.exception_handler(CommandExecutionError)
    async def invalid_command(
        request: Request, error: CommandExecutionError
    ) -> JSONResponse:
        return JSONResponse(status_code=422, content={"detail": str(error)})

    @api.exception_handler(ApiConfigurationError)
    async def unavailable(
        request: Request, error: ApiConfigurationError
    ) -> JSONResponse:
        return JSONResponse(status_code=503, content={"detail": "Service is not initialized."})

    @api.exception_handler(AIEngineerError)
    async def project_error(request: Request, error: AIEngineerError) -> JSONResponse:
        return JSONResponse(status_code=400, content={"detail": str(error)})

    @api.exception_handler(Exception)
    async def unexpected(request: Request, error: Exception) -> JSONResponse:
        logging.getLogger("ai_engineer.api").error(
            "api_request_failed_unexpectedly",
            extra={"path": request.url.path, "error_type": type(error).__name__},
        )
        return JSONResponse(status_code=500, content={"detail": "Internal server error."})


def run_server(
    config: ApiServerConfig,
    *,
    project_root: Path | None = None,
    developer_provider: ProviderAdapter | None = None,
    copilot_provider: CopilotProviderAdapter | None = None,
    manufacturer_provider: ManufacturerProviderAdapter | None = None,
    workspace_roots: tuple[Path, ...] | None = None,
    backup_root: Path | None = None,
) -> None:
    """Run one non-proxied localhost worker with access logging disabled."""
    uvicorn.run(
        create_app(
            token=config.token,
            project_root=project_root,
            max_request_body_bytes=config.max_request_body_bytes,
            developer_provider=developer_provider,
            copilot_provider=copilot_provider,
            manufacturer_provider=manufacturer_provider,
            workspace_roots=workspace_roots,
            backup_root=backup_root,
        ),
        host=config.host,
        port=config.port,
        workers=1,
        proxy_headers=False,
        access_log=False,
        server_header=False,
    )


def main(argv: list[str] | None = None) -> int:
    """Validate configuration or run the protected local server."""
    parser = argparse.ArgumentParser(description="AI-Engineer protected local API")
    parser.add_argument(
        "--check",
        action="store_true",
        help="validate environment configuration without starting the server",
    )
    arguments = parser.parse_args(argv)
    try:
        config = ApiServerConfig.from_environment()
        if arguments.check:
            create_app(
                token=config.token,
                max_request_body_bytes=config.max_request_body_bytes,
            )
            print(f"AI-Engineer {__version__}: local API configuration is valid.")
            return 0
        run_server(config)
    except ApiConfigurationError as exc:
        print(f"AI-Engineer API configuration failed: {exc}", file=sys.stderr)
        return 1
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
