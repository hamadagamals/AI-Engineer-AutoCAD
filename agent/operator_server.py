"""Operator-mode server bootstrap with explicit workspace and provider opt-in."""

from __future__ import annotations

import argparse
import json
import sys
from pathlib import Path

from agent.exceptions import AIEngineerError
from agent.operator_runtime import (
    OperatorRuntimeConfig,
    build_manufacturer_provider,
    build_operator_providers,
)
from agent.version import __version__
from api.server import ApiServerConfig, create_app, run_server


def main(argv: list[str] | None = None, *, project_root: Path | None = None) -> int:
    """Validate or run the localhost operator bridge without widening network scope."""
    parser = argparse.ArgumentParser(description="AI-Engineer local operator bridge")
    parser.add_argument("--check", action="store_true")
    parser.add_argument(
        "--allow-project-root",
        action="store_true",
        help="explicitly add the AI-Engineer repository root to allowed workspace roots",
    )
    arguments = parser.parse_args(argv)
    root = (project_root or Path(__file__).resolve().parents[1]).resolve(strict=True)
    try:
        api_config = ApiServerConfig.from_environment()
        runtime = OperatorRuntimeConfig.from_environment(
            root, allow_project_root=arguments.allow_project_root
        )
        developer_provider, copilot_provider = build_operator_providers()
        manufacturer_provider = build_manufacturer_provider(project_root=root)
        if arguments.check:
            create_app(
                token=api_config.token,
                project_root=root,
                max_request_body_bytes=api_config.max_request_body_bytes,
                developer_provider=developer_provider,
                copilot_provider=copilot_provider,
                manufacturer_provider=manufacturer_provider,
                workspace_roots=runtime.workspace_roots,
                backup_root=runtime.backup_root,
            )
            print(
                json.dumps(
                    {
                        "status": "ok",
                        "version": __version__,
                        "host": api_config.host,
                        "port": api_config.port,
                        "provider_mode": runtime.provider_mode,
                        "ollama_enabled": runtime.ollama_enabled,
                        "codex_enabled": runtime.codex_enabled,
                        "openai_enabled": runtime.openai_enabled,
                        "gemini_enabled": runtime.gemini_enabled,
                        "manufacturer_discovery_configured": manufacturer_provider is not None,
                        "workspace_root_count": len(runtime.workspace_roots),
                        "project_root_allowed": root in runtime.workspace_roots,
                        "backup_is_external": True,
                    },
                    sort_keys=True,
                )
            )
            return 0
        run_server(
            api_config,
            project_root=root,
            developer_provider=developer_provider,
            copilot_provider=copilot_provider,
            manufacturer_provider=manufacturer_provider,
            workspace_roots=runtime.workspace_roots,
            backup_root=runtime.backup_root,
        )
    except (AIEngineerError, OSError) as exc:
        print(f"AI-Engineer operator bridge failed: {exc}", file=sys.stderr)
        return 1
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
