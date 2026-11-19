"""Console entry point for the AI-Engineer foundation."""

from __future__ import annotations

import sys

from agent.app import Application
from agent.exceptions import AIEngineerError
from agent.version import __version__


def main() -> int:
    """Start and cleanly stop the foundation, returning a process exit code."""
    application: Application | None = None
    try:
        application = Application()
        application.start()
        print(f"AI-Engineer {__version__}: foundation initialized successfully.")
        application.stop()
    except AIEngineerError as exc:
        print(f"AI-Engineer failed: {exc}", file=sys.stderr)
        if application is not None and application.status.value == "running":
            try:
                application.stop()
            except AIEngineerError as stop_error:
                print(f"AI-Engineer cleanup failed: {stop_error}", file=sys.stderr)
        return 1
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
