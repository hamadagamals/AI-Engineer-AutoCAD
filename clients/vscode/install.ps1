param([switch]$Confirm)

$ErrorActionPreference = 'Stop'
if ($env:OS -ne 'Windows_NT') { throw 'The AI-Engineer VS Code extension installer requires Windows.' }
if (-not $Confirm) { throw 'Re-run with -Confirm after reviewing the destination.' }

$source = Split-Path -Parent $MyInvocation.MyCommand.Path
$vsix = Join-Path $source 'ai-engineer-operator-0.1.6.vsix'
$extensionId = 'ai-engineer-local.ai-engineer-operator'
$code = Get-Command code -ErrorAction SilentlyContinue
if (-not $code) { throw 'VS Code CLI command "code" was not found in PATH.' }
if (-not (Test-Path $vsix)) { throw "VSIX package was not found: $vsix" }

Write-Host "Installing $extensionId via VS Code CLI..."
& $code.Source --install-extension $vsix --force
if ($LASTEXITCODE -ne 0) { throw 'VS Code CLI reported extension installation failure.' }

$installed = @(& $code.Source --list-extensions 2>&1)
if ($LASTEXITCODE -ne 0) { throw 'Could not verify installed VS Code extensions.' }
if (-not ($installed | Where-Object { $_.Trim().ToLowerInvariant() -eq $extensionId })) {
    throw "VS Code did not report $extensionId after installation."
}

Write-Host "AI-Engineer Operator installed and verified: $extensionId" -ForegroundColor Green
Write-Host 'Restart VS Code after starting AI-Engineer operator mode from the same environment.'
