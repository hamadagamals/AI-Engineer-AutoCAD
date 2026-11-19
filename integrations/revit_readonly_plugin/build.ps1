[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$WorkspaceRoot,
    [ValidateSet(2024)]
    [int]$RevitYear = 2024,
    [string]$RevitRoot = "",
    [string]$OutputDirectory = "",
    [switch]$Install
)

$ErrorActionPreference = "Stop"
$pluginRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$resolvedWorkspace = [System.IO.Path]::GetFullPath($WorkspaceRoot)
if (-not [System.IO.Path]::IsPathRooted($resolvedWorkspace)) {
    throw "WorkspaceRoot must resolve to an absolute path."
}
if ([string]::IsNullOrWhiteSpace($RevitRoot)) {
    $RevitRoot = "C:\Program Files\Autodesk\Revit $RevitYear"
}
$resolvedRevit = [System.IO.Path]::GetFullPath($RevitRoot)
if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
    $OutputDirectory = Join-Path $resolvedWorkspace ".ai-engineer\revit\$RevitYear\bridge"
}
$resolvedOutput = [System.IO.Path]::GetFullPath($OutputDirectory)
$compiler = Join-Path $env:WINDIR "Microsoft.NET\Framework64\v4.0.30319\csc.exe"
$frameworkRoot = Join-Path ${env:ProgramFiles(x86)} "Reference Assemblies\Microsoft\Framework\.NETFramework\v4.8"
$runtimeSerialization = Join-Path $frameworkRoot "System.Runtime.Serialization.dll"
$revitApi = Join-Path $resolvedRevit "RevitAPI.dll"
$revitApiUi = Join-Path $resolvedRevit "RevitAPIUI.dll"
foreach ($required in @($compiler, $runtimeSerialization, $revitApi, $revitApiUi)) {
    if (-not (Test-Path -LiteralPath $required -PathType Leaf)) {
        throw "Required Revit $RevitYear bridge dependency is missing: $required"
    }
}

New-Item -ItemType Directory -Path $resolvedOutput -Force | Out-Null
$assembly = Join-Path $resolvedOutput "AIEngineer.Revit.ReadOnly.dll"
& $compiler /nologo /target:library /platform:x64 "/out:$assembly" `
    "/reference:$revitApi" "/reference:$revitApiUi" `
    (Join-Path $pluginRoot "ReadOnlyInventoryCommand.cs") `
    (Join-Path $pluginRoot "ReadOnlyRequestBridge.cs") `
    (Join-Path $pluginRoot "ReadOnlyCategoryQuery.cs") `
    (Join-Path $pluginRoot "ReadOnlyHostFaceQuery.cs") `
    (Join-Path $pluginRoot "ReadOnlyElementVisualQuery.cs") `
    (Join-Path $pluginRoot "ReadOnlyLightingCircuitQuery.cs") `
    (Join-Path $pluginRoot "ReadOnlyElectricalSystemQuery.cs") `
    (Join-Path $pluginRoot "ReadOnlyFamilyTypeQuery.cs") `
    (Join-Path $pluginRoot "ReadOnlyFamilyFileQuery.cs") `
    (Join-Path $pluginRoot "ReadOnlySchoolSpotQuery.cs") `
    (Join-Path $pluginRoot "ReadOnlySchoolPowerMarks.cs") `
    (Join-Path $pluginRoot "ReadOnlySchoolOutletConnectors.cs") `
    (Join-Path $pluginRoot "SchoolOutletIsolatedTest.cs") `
    (Join-Path $pluginRoot "ReadOnlySchoolLightingDoorAudit.cs") `
    (Join-Path $pluginRoot "ScratchFamilyValidation.cs") `
    (Join-Path $pluginRoot "ScratchL1ParameterUpdate.cs") `
    (Join-Path $pluginRoot "ScratchRemainingParameterUpdate.cs") `
    (Join-Path $pluginRoot "ModelLineExecution.cs") `
    (Join-Path $pluginRoot "ModelLineHandoff.cs") `
    (Join-Path $pluginRoot "FamilyPlacementHandoff.cs") `
    (Join-Path $pluginRoot "FixtureMarkCorrectionHandoff.cs") `
    (Join-Path $pluginRoot "SchoolL4Batch1Handoff.cs") `
    (Join-Path $pluginRoot "SchoolL4Batch2Handoff.cs") `
    (Join-Path $pluginRoot "SchoolClassroomSampleHandoff.cs") `
    (Join-Path $pluginRoot "SchoolDistributionReviewHandoff.cs") `
    (Join-Path $pluginRoot "SchoolLightingReviewV2Handoff.cs") `
    (Join-Path $pluginRoot "SchoolElectricalReviewV3Handoff.cs") `
    (Join-Path $pluginRoot "HostedFamilyPlacementHandoff.cs") `
    (Join-Path $pluginRoot "CircuitAssignmentHandoff.cs")
if ($LASTEXITCODE -ne 0) {
    throw "Revit read-only bridge compilation failed with exit code $LASTEXITCODE."
}

function Write-Manifest([string]$AssemblyPath, [string]$Destination) {
    $template = Get-Content -LiteralPath (Join-Path $pluginRoot "AIEngineer.Revit.addin.template") -Raw
    $escapedAssembly = [System.Security.SecurityElement]::Escape($AssemblyPath)
    $content = $template.Replace("{{ASSEMBLY_PATH}}", $escapedAssembly)
    [System.IO.File]::WriteAllText($Destination, $content, [System.Text.UTF8Encoding]::new($false))
}

$manifest = Join-Path $resolvedOutput "AIEngineer.Revit.addin"
Write-Manifest $assembly $manifest
$installed = $false
if ($Install) {
    $installRoot = Join-Path $env:LOCALAPPDATA "AI-Engineer\revit\$RevitYear\bridge"
    $addinRoot = Join-Path $env:APPDATA "Autodesk\Revit\Addins\$RevitYear"
    New-Item -ItemType Directory -Path $installRoot -Force | Out-Null
    New-Item -ItemType Directory -Path $addinRoot -Force | Out-Null
    $installedAssembly = Join-Path $installRoot "AIEngineer.Revit.ReadOnly.dll"
    Copy-Item -LiteralPath $assembly -Destination $installedAssembly -Force
    Write-Manifest $installedAssembly (Join-Path $addinRoot "AIEngineer.Revit.addin")
    $installed = $true
}

[ordered]@{
    schema_version = 1
    revit_year = $RevitYear
    revit_root = $resolvedRevit
    assembly = $assembly
    manifest = $manifest
    installed = $installed
    transaction_mode = "ReadOnly commands plus approved Model Lines, family placement, school lighting samples, school distribution review views, lighting-circuit, and isolated family-validation new-file handoffs"
    mutation_capability = "approved_model_lines_new_workspace_rvt; approved_reference_bound_rotated_same_type_family_batch_new_workspace_rvt; approved_eight_fixture_marks_new_workspace_rvt; approved_school_l4_exact_family_batch1_new_workspace_rvt; approved_school_l4_exact_family_batch2_new_workspace_rvt; approved_classroom1500_l1_sample_new_workspace_rvt; approved_school_distribution_review_views_new_workspace_rvt; approved_single_linked_ceiling_family_new_workspace_rvt; approved_single_lighting_circuit_assignment_new_workspace_rvt; approved_four_family_isolated_validation_rvt; approved_l1_parameter_update_new_scratch_rvt; approved_l2_l5_l6_parameter_update_new_scratch_rvt"
    provider_egress_performed = $false
    document_mutation_performed = $false
} | ConvertTo-Json
