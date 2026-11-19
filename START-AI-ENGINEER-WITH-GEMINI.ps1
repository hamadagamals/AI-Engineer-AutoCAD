[CmdletBinding()]
param(
    [string]$DrawingPath = '',
    [ValidateSet(2021, 2023)]
    [int]$AutoCADYear = 2021,
    [string]$AutoCADRoot = '',
    [switch]$ManualPluginLoad
)

$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot

function Step([string]$Message) { Write-Host "`n=== $Message ===" -ForegroundColor Cyan }
function Fail([string]$Message) { Write-Host "`nERROR: $Message" -ForegroundColor Red; exit 1 }
if ($env:OS -ne 'Windows_NT') { Fail 'This launcher must be run on Windows.' }
$port = if ([string]::IsNullOrWhiteSpace($env:AI_ENGINEER_API_PORT)) { 8766 } else { [int]$env:AI_ENGINEER_API_PORT }
if (Get-Command Get-NetTCPConnection -ErrorAction SilentlyContinue) {
    if (Get-NetTCPConnection -LocalPort $port -State Listen -ErrorAction SilentlyContinue | Select-Object -First 1) {
        Fail "Port $port is already in use. Close the previous Operator Bridge first."
    }
}

Step 'Gemini provider privacy confirmation'
Write-Host 'Only the bounded request/context approved by AI-Engineer can leave this computer.'
Write-Host 'Gemini remains reasoning-only; local approvals and AutoCAD safety gates stay active.'
$confirm = Read-Host 'Type YES to enable Gemini API data egress for this session'
if ($confirm -cne 'YES') { Fail 'Gemini provider was not enabled.' }

Step 'Reading Gemini API key securely'
$secure = Read-Host 'Gemini API key' -AsSecureString
$ptr = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($secure)
try { $plainKey = [Runtime.InteropServices.Marshal]::PtrToStringBSTR($ptr) }
finally { [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($ptr) }
if ([string]::IsNullOrWhiteSpace($plainKey) -or $plainKey.Length -lt 20) {
    Fail 'The Gemini API key is missing or too short.'
}

Step 'Preparing one-session secure environment'
$bytes = New-Object byte[] 32
$rng = [Security.Cryptography.RandomNumberGenerator]::Create()
try { $rng.GetBytes($bytes) } finally { $rng.Dispose() }
$env:AI_ENGINEER_API_TOKEN = [Convert]::ToBase64String($bytes)
$env:AI_ENGINEER_API_HOST = '127.0.0.1'
$env:AI_ENGINEER_API_PORT = [string]$port
$env:GEMINI_API_KEY = $plainKey
$plainKey = $null
$env:AI_ENGINEER_PROVIDER = 'gemini'
$env:AI_ENGINEER_GEMINI_ENABLE = '1'
$env:AI_ENGINEER_GEMINI_EGRESS_CONFIRMED = '1'
$env:AI_ENGINEER_GEMINI_MODEL = 'gemini-3.5-flash-lite'

Step 'Checking AI-Engineer Operator Bridge with Gemini enabled'
python launcher.py operator --check --allow-project-root
if ($LASTEXITCODE -ne 0) {
    Remove-Item Env:GEMINI_API_KEY -ErrorAction SilentlyContinue
    Fail 'Operator Bridge provider check failed.'
}

Step 'Starting AutoCAD with the configured Gemini provider'
$previewArguments = @{
    Port = $port
    UseConfiguredProvider = $true
    AutoCADYear = $AutoCADYear
    ManualPluginLoad = $ManualPluginLoad
}
if (-not [string]::IsNullOrWhiteSpace($AutoCADRoot)) {
    $previewArguments.AutoCADRoot = $AutoCADRoot
}
if (-not [string]::IsNullOrWhiteSpace($DrawingPath)) {
    $previewArguments.DrawingPath = $DrawingPath
}
try {
    & (Join-Path $PSScriptRoot 'START-AI-ENGINEER-AUTOCAD-PREVIEW.ps1') @previewArguments
    if ($LASTEXITCODE -ne 0) { Fail 'AutoCAD Gemini startup failed.' }
}
finally {
    @(
        'GEMINI_API_KEY', 'AI_ENGINEER_PROVIDER', 'AI_ENGINEER_GEMINI_ENABLE',
        'AI_ENGINEER_GEMINI_EGRESS_CONFIRMED', 'AI_ENGINEER_GEMINI_MODEL'
    ) | ForEach-Object { Remove-Item "Env:$_" -ErrorAction SilentlyContinue }
}

Step 'Starting isolated VS Code Operator'
$code = Get-Command code -ErrorAction SilentlyContinue
if (-not $code) { Fail 'VS Code CLI command "code" was not found in PATH.' }
$vsix = Join-Path $PSScriptRoot 'clients\vscode\ai-engineer-operator-0.1.6.vsix'
if (-not (Test-Path -LiteralPath $vsix -PathType Leaf)) {
    Fail 'The reviewed AI-Engineer VS Code extension package is missing.'
}
$vscodeRuntime = Join-Path $PSScriptRoot 'workspace\.ai-engineer\vscode-gemini'
$vscodeProfile = Join-Path $vscodeRuntime 'profile'
$vscodeExtensions = Join-Path $vscodeRuntime 'extensions'
New-Item -ItemType Directory -Path $vscodeProfile -Force | Out-Null
New-Item -ItemType Directory -Path $vscodeExtensions -Force | Out-Null
& $code.Source --user-data-dir $vscodeProfile --extensions-dir $vscodeExtensions `
    --install-extension $vsix --force
if ($LASTEXITCODE -ne 0) { Fail 'The isolated AI-Engineer VS Code extension installation failed.' }
& $code.Source --new-window --user-data-dir $vscodeProfile --extensions-dir $vscodeExtensions `
    $PSScriptRoot
if ($LASTEXITCODE -ne 0) { Fail 'The isolated AI-Engineer VS Code window could not be opened.' }

Write-Host "`nSUCCESS" -ForegroundColor Green
Write-Host 'AutoCAD is running with AI-Engineer provider gemini.'
Write-Host 'An isolated VS Code Operator window is connected to the same local bridge.' -ForegroundColor Green
Write-Host 'If Gemini Code Assist MCP is installed, open Gemini Agent mode and run /mcp then /tools.' -ForegroundColor Green
Write-Host 'The Gemini key was inherited only by the local bridge and was not written to the project.' -ForegroundColor Green
