[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$WorkspaceRoot,
    [ValidateRange(2015, 2030)]
    [int]$AutoCADYear = 0,
    [string]$AutoCADRoot = "",
    [string]$OutputDirectory = ""
)

$ErrorActionPreference = "Stop"
$pluginRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$resolvedWorkspace = [System.IO.Path]::GetFullPath($WorkspaceRoot)
if (-not [System.IO.Path]::IsPathRooted($resolvedWorkspace)) {
    throw "WorkspaceRoot must resolve to an absolute path."
}
if ([string]::IsNullOrWhiteSpace($AutoCADRoot)) {
    if ($AutoCADYear -gt 0) {
        $AutoCADRoot = "C:\Program Files\Autodesk\AutoCAD $AutoCADYear"
    } else {
        # Auto-detect highest installed AutoCAD version (2015..2030)
        $detected = Get-ChildItem "C:\Program Files\Autodesk" -Directory -ErrorAction SilentlyContinue |
            Where-Object { $_.Name -match '^AutoCAD (\d{4})$' } |
            ForEach-Object { [PSCustomObject]@{ Year = [int]$Matches[1]; Path = $_.FullName } } |
            Sort-Object Year -Descending | Select-Object -First 1
        if ($null -ne $detected) {
            $AutoCADYear = $detected.Year
            $AutoCADRoot = $detected.Path
        } else {
            $AutoCADYear = 2021
            $AutoCADRoot = "C:\Program Files\Autodesk\AutoCAD 2021"
        }
    }
} elseif ($AutoCADYear -le 0) {
    if ($AutoCADRoot -match '(\d{4})') { $AutoCADYear = [int]$Matches[1] } else { $AutoCADYear = 2021 }
}
$resolvedAutoCAD = [System.IO.Path]::GetFullPath($AutoCADRoot)
if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
    $OutputDirectory = Join-Path $resolvedWorkspace ".ai-engineer\autocad\context\bridge"
}
$resolvedOutput = [System.IO.Path]::GetFullPath($OutputDirectory)

$compiler = Join-Path $env:WINDIR "Microsoft.NET\Framework64\v4.0.30319\csc.exe"
$frameworkCandidates = if ($AutoCADYear -ge 2022) { @('v4.8', 'v4.7.2', 'v4.7', 'v4.6.2', 'v4.5.2') } else { @('v4.7.2', 'v4.8', 'v4.7', 'v4.6.2', 'v4.5.2', 'v4.5') }
$frameworkReferences = $null
foreach ($candidate in $frameworkCandidates) {
    $candidateRoot = Join-Path ${env:ProgramFiles(x86)} "Reference Assemblies\Microsoft\Framework\.NETFramework\$candidate"
    if ((Test-Path -LiteralPath (Join-Path $candidateRoot 'PresentationCore.dll') -PathType Leaf) -and
        (Test-Path -LiteralPath (Join-Path $candidateRoot 'WindowsBase.dll') -PathType Leaf)) {
        $frameworkReferences = $candidateRoot
        break
    }
}
if ($null -eq $frameworkReferences) {
    $wpfRuntime = Join-Path $env:WINDIR "Microsoft.NET\Framework64\v4.0.30319\WPF"
    if ((Test-Path -LiteralPath (Join-Path $wpfRuntime 'PresentationCore.dll') -PathType Leaf) -and
        (Test-Path -LiteralPath (Join-Path $wpfRuntime 'WindowsBase.dll') -PathType Leaf)) {
        $frameworkReferences = $wpfRuntime
    }
}
if ($null -eq $frameworkReferences) {
    throw "Required .NET Framework reference assemblies for AutoCAD $AutoCADYear are missing."
}
$presentationCore = Join-Path $frameworkReferences "PresentationCore.dll"
$windowsBase = Join-Path $frameworkReferences "WindowsBase.dll"
$references = @("acmgd.dll", "acdbmgd.dll", "accoremgd.dll") | ForEach-Object {
    Join-Path $resolvedAutoCAD $_
}
$acWindows = Join-Path $resolvedAutoCAD "AcWindows.dll"
foreach ($required in @($compiler, $presentationCore, $windowsBase, $acWindows) + $references) {
    if (-not (Test-Path -LiteralPath $required -PathType Leaf)) {
        throw "Required AutoCAD $AutoCADYear bridge dependency is missing: $required"
    }
}

New-Item -ItemType Directory -Path $resolvedOutput -Force | Out-Null
$assembly = Join-Path $resolvedOutput "AIEngineer.AutoCAD.Context.dll"
& $compiler /nologo /utf8output /codepage:65001 /target:library /platform:x64 "/out:$assembly" `
    "/reference:$($references[0])" "/reference:$($references[1])" `
    "/reference:$($references[2])" `
    "/reference:$acWindows" `
    /reference:System.Web.Extensions.dll /reference:System.Net.Http.dll /reference:System.Security.dll /reference:Microsoft.CSharp.dll `
    /reference:System.Windows.Forms.dll /reference:System.Drawing.dll `
    /reference:System.IO.Compression.dll /reference:System.IO.Compression.FileSystem.dll /reference:System.Xml.dll `
    "/reference:$presentationCore" "/reference:$windowsBase" `
    (Join-Path $pluginRoot "ContextCommands.cs") `
    (Join-Path $pluginRoot "ConnectionReviewChannel.cs") `
    (Join-Path $pluginRoot "DrawingChangeTracker.cs") `
    (Join-Path $pluginRoot "OperatorCommandChannel.cs") `
    (Join-Path $pluginRoot "DrawingExecutionCommands.cs") `
    (Join-Path $pluginRoot "ExcelTableInterop.cs") `
    (Join-Path $pluginRoot "CadToolExecutor.cs") `
    (Join-Path $pluginRoot "CadSpatialIndex.cs") `
    (Join-Path $pluginRoot "CadMepAndTakeoffTools.cs") `
    (Join-Path $pluginRoot "CadPanelAndQaTools.cs") `
    (Join-Path $pluginRoot "CadPanelManufacturingTools.cs") `
    (Join-Path $pluginRoot "CadProjectLegendDb.cs") `
    (Join-Path $pluginRoot "CadGuidedInventoryTools.cs") `
    (Join-Path $pluginRoot "CadVisualCaptureTools.cs") `
    (Join-Path $pluginRoot "CadTextAndAttributesTools.cs") `
    (Join-Path $pluginRoot "CadGeometryQaAndLayoutTools.cs") `
    (Join-Path $pluginRoot "CadLightingAndDialuxTools.cs") `
    (Join-Path $pluginRoot "CadLvCalculationsTools.cs") `
    (Join-Path $pluginRoot "CadDistributionNetworkTools.cs") `
    (Join-Path $pluginRoot "VoiceAssistant.cs") `
    (Join-Path $pluginRoot "DirectCadAgent.cs") `
    (Join-Path $pluginRoot "PaletteCommands.cs")
if ($LASTEXITCODE -ne 0) {
    throw "AutoCAD context bridge compilation failed with exit code $LASTEXITCODE."
}

$settings = [ordered]@{
    workspace_root = $resolvedWorkspace
    autocad_year = $AutoCADYear
    autocad_root = $resolvedAutoCAD
    maximum_entities = 512
    maximum_layers = 128
    maximum_attributes_per_entity = 32
    maximum_points_per_entity = 4096
    maximum_text_bytes = 4096
    maximum_payload_bytes = 1048576
    maximum_nesting = 8
    maximum_duration_seconds = 30
    maximum_index_entities = 200000
    maximum_index_entities_per_chunk = 64
    maximum_index_chunks = 4096
    maximum_index_duration_seconds = 120
}
$settingsPath = Join-Path $resolvedOutput "settings.json"
$settings | ConvertTo-Json | Set-Content -LiteralPath $settingsPath -Encoding UTF8

# Also emit standalone dedicated Panel Manufacturing 2D/3D Studio DLL (`AIENGPANEL` / `PANELAI`)
$panelAssembly = Join-Path $resolvedOutput "AIEngineer.AutoCAD.PanelManufacturing.dll"
& $compiler /nologo /utf8output /codepage:65001 /define:PANEL_MFG_STANDALONE /target:library /platform:x64 "/out:$panelAssembly" `
    "/reference:$($references[0])" "/reference:$($references[1])" `
    "/reference:$($references[2])" `
    "/reference:$acWindows" `
    /reference:System.Web.Extensions.dll /reference:System.Net.Http.dll /reference:System.Security.dll /reference:Microsoft.CSharp.dll `
    /reference:System.Windows.Forms.dll /reference:System.Drawing.dll `
    /reference:System.IO.Compression.dll /reference:System.IO.Compression.FileSystem.dll /reference:System.Xml.dll `
    "/reference:$presentationCore" "/reference:$windowsBase" `
    (Join-Path $pluginRoot "DrawingExecutionCommands.cs") `
    (Join-Path $pluginRoot "ExcelTableInterop.cs") `
    (Join-Path $pluginRoot "CadToolExecutor.cs") `
    (Join-Path $pluginRoot "CadSpatialIndex.cs") `
    (Join-Path $pluginRoot "CadMepAndTakeoffTools.cs") `
    (Join-Path $pluginRoot "CadPanelAndQaTools.cs") `
    (Join-Path $pluginRoot "CadPanelManufacturingTools.cs") `
    (Join-Path $pluginRoot "CadProjectLegendDb.cs") `
    (Join-Path $pluginRoot "CadGuidedInventoryTools.cs") `
    (Join-Path $pluginRoot "CadVisualCaptureTools.cs") `
    (Join-Path $pluginRoot "CadTextAndAttributesTools.cs") `
    (Join-Path $pluginRoot "CadGeometryQaAndLayoutTools.cs") `
    (Join-Path $pluginRoot "CadLightingAndDialuxTools.cs") `
    (Join-Path $pluginRoot "CadLvCalculationsTools.cs") `
    (Join-Path $pluginRoot "CadDistributionNetworkTools.cs") `
    (Join-Path $pluginRoot "VoiceAssistant.cs") `
    (Join-Path $pluginRoot "DirectCadAgent.cs")
if ($LASTEXITCODE -ne 0) {
    throw "AutoCAD Panel Manufacturing plugin compilation failed with exit code $LASTEXITCODE."
}

Write-Output $assembly
