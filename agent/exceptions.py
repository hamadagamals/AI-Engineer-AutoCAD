"""Project-specific exception hierarchy."""


class AIEngineerError(Exception):
    """Base exception for actionable AI-Engineer failures."""


class ConfigurationError(AIEngineerError):
    """Raised when application configuration is missing or invalid."""


class ProjectConfigError(ConfigurationError):
    """Raised when a project's identity or workspace boundary is invalid."""


class StateError(AIEngineerError):
    """Raised when persistent application state cannot be read or written."""


class LifecycleError(AIEngineerError):
    """Raised when an invalid application lifecycle transition is requested."""


class FileManagerError(AIEngineerError):
    """Raised when a managed file operation cannot be completed safely."""


class PathPolicyError(FileManagerError):
    """Raised when a path violates configured workspace boundaries."""


class ConfirmationRequiredError(FileManagerError):
    """Raised when a destructive operation lacks explicit confirmation."""


class FileSizeLimitError(FileManagerError):
    """Raised when a file exceeds a configured operation limit."""


class ProjectScannerError(AIEngineerError):
    """Raised when a bounded project scan cannot be started safely."""


class TerminalError(AIEngineerError):
    """Raised when a bounded process cannot be prepared or executed."""


class CommandDeniedError(TerminalError):
    """Raised when a command violates the configured execution policy."""


class SystemInfoError(AIEngineerError):
    """Raised when bounded local system information cannot be collected."""


class ProcessManagerError(AIEngineerError):
    """Raised when a process operation cannot be performed safely."""


class GitManagerError(AIEngineerError):
    """Raised when a bounded, read-only Git operation cannot be completed safely."""


class PermissionDeniedError(AIEngineerError):
    """Raised when the active policy does not grant a required capability."""


class ApprovalError(AIEngineerError):
    """Raised when an approval record is invalid, expired, or unusable."""


class ApprovalRequiredError(ApprovalError):
    """Raised with a durable request identifier when human approval is required."""

    def __init__(self, message: str, approval_id: str) -> None:
        super().__init__(message)
        self.approval_id = approval_id


class ApprovalDeniedError(ApprovalError):
    """Raised when a human explicitly denied the requested action."""


class CommandRegistryError(AIEngineerError):
    """Raised for invalid or duplicate typed command definitions."""


class CommandExecutionError(AIEngineerError):
    """Raised when a registered command cannot produce a bounded JSON result."""


class RetryableCommandError(CommandExecutionError):
    """Explicitly marks a command failure as safe to retry within configured bounds."""


class TaskEngineError(AIEngineerError):
    """Raised for invalid task data or lifecycle operations."""


class TaskCancelledError(TaskEngineError):
    """Raised cooperatively when cancellation was requested for a running task."""


class ResourceNotFoundError(AIEngineerError):
    """Raised when a requested command, task, or approval does not exist."""


class ApiConfigurationError(AIEngineerError):
    """Raised when local API security configuration is unsafe or invalid."""


class ProviderError(AIEngineerError):
    """Raised when a developer provider fails or returns unsafe output."""


class DeveloperAgentError(AIEngineerError):
    """Raised for invalid developer-agent data or lifecycle operations."""


class VSCodeIntegrationError(AIEngineerError):
    """Raised when the supported VS Code CLI cannot be used safely."""


class AutoCADIntegrationError(AIEngineerError):
    """Raised when bounded AutoCAD 2021 integration cannot proceed safely."""


class RevitIntegrationError(AIEngineerError):
    """Raised when bounded Revit integration cannot proceed safely."""


class DrawingHandoffError(AutoCADIntegrationError):
    """Raised when a verified AutoCAD output cannot be handed off safely."""


class ElectricalModelError(AIEngineerError):
    """Raised when typed electrical project data is invalid or ambiguous."""


class QuantityTakeoffError(AIEngineerError):
    """Raised when a traceable quantity takeoff cannot be calculated safely."""


class LowVoltageCalculationError(AIEngineerError):
    """Raised when a deterministic low-voltage calculation is invalid or incomplete."""


class LoadAggregationError(AIEngineerError):
    """Raised when reviewed load aggregation or phase allocation is incomplete."""


class CableAmpacityError(AIEngineerError):
    """Raised when reviewed cable ampacity or derating inputs are incomplete."""


class ShortCircuitCalculationError(AIEngineerError):
    """Raised when a bounded short-circuit calculation cannot be reproduced safely."""


class CandidateAdequacyError(AIEngineerError):
    """Raised when a reviewed cable/protection candidate cannot be evaluated safely."""


class ProtectiveConductorSizingError(AIEngineerError):
    """Raised when reviewed PE/earthing sizing inputs are invalid or incomplete."""


class PanelSldContractError(AIEngineerError):
    """Raised when panel-schedule or SLD identities are incomplete or conflicting."""


class PanelScheduleError(AIEngineerError):
    """Raised when a deterministic panel schedule cannot be reproduced safely."""


class PanelSldExtractionError(AIEngineerError):
    """Raised when supported AutoCAD panel/SLD observations cannot be extracted safely."""


class CodeProfileError(AIEngineerError):
    """Raised when reviewed code-profile evidence is incomplete or inconsistent."""


class ComplianceEvidenceError(AIEngineerError):
    """Raised when deterministic compliance evidence is malformed or tampered with."""


class ManufacturerEvidenceError(AIEngineerError):
    """Raised when manufacturer evidence or asset provenance cannot be trusted."""


class VerifiedLessonError(AIEngineerError):
    """Raised when reviewed operational learning is invalid or would be unsafe."""


class DraftingContractError(AIEngineerError):
    """Raised when a previewed electrical-drafting contract is invalid."""


class AutoCADContextError(AutoCADIntegrationError):
    """Raised when bounded AutoCAD drawing context cannot be trusted or mapped."""


class AutoCADBridgeError(AutoCADIntegrationError):
    """Raised when the supported AutoCAD context bridge cannot be built safely."""


class CopilotContractError(AIEngineerError):
    """Raised when a local read-only copilot session contract is invalid."""


class CopilotProviderError(AIEngineerError):
    """Raised when a read-only copilot provider violates its bounded contract."""


class ElectricalSymbolCatalogError(AutoCADIntegrationError):
    """Raised when a versioned electrical symbol definition is unsafe or invalid."""


class PackagingError(AIEngineerError):
    """Raised when a portable package cannot be built or installed safely."""


class RecoveryError(AIEngineerError):
    """Raised when a runtime snapshot cannot be created or restored safely."""


class MigrationError(AIEngineerError):
    """Raised when persistent runtime data cannot be migrated safely."""
