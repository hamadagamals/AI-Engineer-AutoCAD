"""Safe local operating-system and project services."""

from core.file_manager import FileManager, FileOperationResult
from core.git_manager import GitCommit, GitManager, GitStatus, GitStatusEntry
from core.project_scanner import ProjectScanner, ProjectScanResult, ScanLimits
from core.process_manager import ProcessInfo, ProcessManager, ProcessListResult, TerminationResult
from core.system_info import DiskSnapshot, SystemInfo, SystemSnapshot
from core.terminal_manager import CommandPolicy, CommandResult, TerminalManager

__all__ = [
    "FileManager",
    "FileOperationResult",
    "GitCommit",
    "GitManager",
    "GitStatus",
    "GitStatusEntry",
    "ProjectScanner",
    "ProjectScanResult",
    "ScanLimits",
    "CommandPolicy",
    "CommandResult",
    "TerminalManager",
    "DiskSnapshot",
    "SystemInfo",
    "SystemSnapshot",
    "ProcessInfo",
    "ProcessListResult",
    "ProcessManager",
    "TerminationResult",
]
