[CmdletBinding()]
param(
    [int]$Port = 8765,
    [Parameter(Mandatory = $true)]
    [string]$DrawingPath,
    [string]$AutoCADRoot = 'C:\Program Files\Autodesk\AutoCAD 2023',
    [switch]$ManualPluginLoad,
    [switch]$UseConfiguredProvider,
    [switch]$WritableScratchAcceptance
)

$ErrorActionPreference = 'Stop'
$launcher = Join-Path $PSScriptRoot 'START-AI-ENGINEER-AUTOCAD-PREVIEW.ps1'
& $launcher -Port $Port -DrawingPath $DrawingPath -AutoCADYear 2023 `
    -AutoCADRoot $AutoCADRoot -ManualPluginLoad:$ManualPluginLoad `
    -UseConfiguredProvider:$UseConfiguredProvider `
    -WritableScratchAcceptance:$WritableScratchAcceptance
exit $LASTEXITCODE

