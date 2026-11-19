"""Atomic JSON persistence for application state."""

from __future__ import annotations

import json
import os
import tempfile
from copy import deepcopy
from pathlib import Path
from typing import Any, Mapping

from agent.exceptions import StateError


class StateStore:
    """Read and atomically replace a JSON object stored on disk."""

    def __init__(self, path: Path) -> None:
        self.path = path

    def load(self) -> dict[str, Any]:
        """Return state from disk, requiring a JSON object at the root."""
        try:
            value = json.loads(self.path.read_text(encoding="utf-8"))
        except FileNotFoundError as exc:
            raise StateError(f"State file does not exist: {self.path}") from exc
        except (OSError, UnicodeError, json.JSONDecodeError) as exc:
            raise StateError(f"Cannot read valid JSON state from {self.path}: {exc}") from exc
        if not isinstance(value, dict):
            raise StateError(f"State root must be a JSON object: {self.path}")
        return value

    def save(self, state: Mapping[str, Any]) -> None:
        """Persist a copy of *state* using flush, fsync, and atomic replacement."""
        snapshot = deepcopy(dict(state))
        try:
            serialized = json.dumps(snapshot, indent=2, ensure_ascii=False) + "\n"
        except (TypeError, ValueError) as exc:
            raise StateError(f"State contains non-JSON data: {exc}") from exc

        self.path.parent.mkdir(parents=True, exist_ok=True)
        temporary_path: Path | None = None
        try:
            descriptor, name = tempfile.mkstemp(
                dir=self.path.parent, prefix=f".{self.path.name}.", suffix=".tmp"
            )
            temporary_path = Path(name)
            with os.fdopen(descriptor, "w", encoding="utf-8", newline="\n") as handle:
                handle.write(serialized)
                handle.flush()
                os.fsync(handle.fileno())
            os.replace(temporary_path, self.path)
        except OSError as exc:
            raise StateError(f"Cannot atomically write state to {self.path}: {exc}") from exc
        finally:
            if temporary_path is not None and temporary_path.exists():
                temporary_path.unlink()
