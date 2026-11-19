[CmdletBinding()]
param(
    [int]$Port = 8765,
    [string]$AutoCADRoot = 'C:\Program Files\Autodesk\AutoCAD 2023'
)

$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot
$reports = Join-Path $PSScriptRoot 'workspace\reports'
New-Item -ItemType Directory -Path $reports -Force | Out-Null
$stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
$report = Join-Path $reports "autocad-2023-readiness-$stamp.json"

$output = & python.exe launcher.py autocad-readiness --autocad-year 2023 `
    --autocad-root $AutoCADRoot --port $Port
if ($LASTEXITCODE -ne 0) {
    throw 'AI-Engineer AutoCAD 2023 readiness command failed.'
}
$value = $output | ConvertFrom-Json
$output | Set-Content -LiteralPath $report -Encoding UTF8
Write-Host "Readiness report: $report"
if (-not $value.complete) {
    Write-Host 'AutoCAD 2023 readiness is incomplete. Send this JSON report for adaptation.' -ForegroundColor Yellow
    exit 2
}
Write-Host 'AutoCAD 2023 readiness: PASS. No provider egress or drawing mutation was performed.' -ForegroundColor Green

