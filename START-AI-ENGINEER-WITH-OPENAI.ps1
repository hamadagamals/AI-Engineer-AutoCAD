$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot

function Step([string]$Message) {
    Write-Host "`n=== $Message ===" -ForegroundColor Cyan
}

function Fail([string]$Message) {
    Write-Host "`nERROR: $Message" -ForegroundColor Red
    exit 1
}

function Wait-BridgeReady([Diagnostics.Process]$Process, [int]$Port) {
    for ($attempt = 0; $attempt -lt 15; $attempt++) {
        if ($Process.HasExited) { Fail "The Operator Bridge exited during startup with code $($Process.ExitCode)." }
        try {
            $health = Invoke-RestMethod -Uri "http://127.0.0.1:$Port/health" -Method Get -TimeoutSec 1
            if ($health.status -eq 'ok') { return $health }
        } catch {}
        Start-Sleep -Seconds 1
    }
    Fail "The Operator Bridge did not become healthy on 127.0.0.1:$Port. Review the bridge window for the exact error."
}

if ($env:OS -ne 'Windows_NT') {
    Fail 'This launcher must be run on Windows.'
}

$runningCode = Get-Process -Name Code -ErrorAction SilentlyContinue
if ($runningCode) {
    Write-Host 'VS Code is currently running.' -ForegroundColor Yellow
    Write-Host 'Close ALL VS Code windows, then run this launcher again.' -ForegroundColor Yellow
    exit 2
}

$port = if ([string]::IsNullOrWhiteSpace($env:AI_ENGINEER_API_PORT)) { '8765' } else { $env:AI_ENGINEER_API_PORT }
try {
    $portNumber = [int]$port
} catch {
    Fail 'AI_ENGINEER_API_PORT must be a valid integer.'
}

# Do not stop an unknown process automatically. Require the previous bridge to be closed.
$listener = $null
if (Get-Command Get-NetTCPConnection -ErrorAction SilentlyContinue) {
    $listener = Get-NetTCPConnection -LocalPort $portNumber -State Listen -ErrorAction SilentlyContinue | Select-Object -First 1
}
if ($listener) {
    Write-Host "Port $portNumber is already in use." -ForegroundColor Yellow
    Write-Host 'Close the previous AI-Engineer Operator Bridge PowerShell window, then run this launcher again.' -ForegroundColor Yellow
    exit 3
}

Step 'OpenAI provider privacy confirmation'
Write-Host 'AI-Engineer will use the OpenAI API only for the reviewed provider requests allowed by the local policy.'
Write-Host 'The API key will be kept only in the Operator Bridge process environment and will not be written to project files.'
Write-Host 'Sensitive local actions remain behind AI-Engineer approval gates.'
$confirm = Read-Host 'Type YES to enable OpenAI API data egress for this session'
if ($confirm -cne 'YES') {
    Fail 'OpenAI provider was not enabled.'
}

Step 'Reading OpenAI API key securely'
$secure = Read-Host 'OpenAI API key' -AsSecureString
$ptr = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($secure)
try {
    $plainKey = [Runtime.InteropServices.Marshal]::PtrToStringBSTR($ptr)
}
finally {
    [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($ptr)
}
if ([string]::IsNullOrWhiteSpace($plainKey) -or $plainKey.Length -lt 20) {
    Fail 'The OpenAI API key is missing or too short.'
}

Step 'Preparing one-session secure environment'
$bytes = New-Object byte[] 32
$rng = [Security.Cryptography.RandomNumberGenerator]::Create()
try { $rng.GetBytes($bytes) } finally { $rng.Dispose() }
$env:AI_ENGINEER_API_TOKEN = [Convert]::ToBase64String($bytes)
$env:AI_ENGINEER_API_HOST = '127.0.0.1'
$env:AI_ENGINEER_API_PORT = [string]$portNumber
$env:OPENAI_API_KEY = $plainKey
$plainKey = $null
$env:AI_ENGINEER_PROVIDER = 'openai'
$env:AI_ENGINEER_OPENAI_ENABLE = '1'
$env:AI_ENGINEER_OPENAI_EGRESS_CONFIRMED = '1'
$env:AI_ENGINEER_OPENAI_MODEL = 'gpt-5.6'
$env:AI_ENGINEER_OPENAI_REASONING_EFFORT = 'high'
$env:AI_ENGINEER_OPENAI_REASONING_MODE = 'standard'

Step 'Checking AI-Engineer Operator Bridge with OpenAI enabled'
python launcher.py operator --check --allow-project-root
if ($LASTEXITCODE -ne 0) {
    Remove-Item Env:OPENAI_API_KEY -ErrorAction SilentlyContinue
    Fail 'Operator Bridge provider check failed.'
}

Step 'Starting AI-Engineer Operator Bridge'
$project = $PSScriptRoot.Replace("'", "''")
$bridgeCommand = "Set-Location '$project'; Write-Host 'AI-Engineer Operator Bridge + OpenAI - keep this window open' -ForegroundColor Green; python launcher.py operator --allow-project-root"
# Start-Process inherits the current environment. The secret is not put on the command line.
$bridge = Start-Process powershell -PassThru -ArgumentList '-NoExit','-NoProfile','-ExecutionPolicy','Bypass','-Command',$bridgeCommand
$bridgeHealth = Wait-BridgeReady -Process $bridge -Port $portNumber
Write-Host "Operator Bridge healthy: $($bridgeHealth.version)" -ForegroundColor Green

# Do not expose the OpenAI key to VS Code or its extensions. The bridge already inherited it.
Remove-Item Env:OPENAI_API_KEY -ErrorAction SilentlyContinue
Remove-Item Env:AI_ENGINEER_PROVIDER -ErrorAction SilentlyContinue
Remove-Item Env:AI_ENGINEER_OPENAI_ENABLE -ErrorAction SilentlyContinue
Remove-Item Env:AI_ENGINEER_OPENAI_EGRESS_CONFIRMED -ErrorAction SilentlyContinue
Remove-Item Env:AI_ENGINEER_OPENAI_MODEL -ErrorAction SilentlyContinue
Remove-Item Env:AI_ENGINEER_OPENAI_REASONING_EFFORT -ErrorAction SilentlyContinue
Remove-Item Env:AI_ENGINEER_OPENAI_REASONING_MODE -ErrorAction SilentlyContinue

Step 'Starting VS Code with local bridge credentials only'
$code = Get-Command code -ErrorAction SilentlyContinue
if (-not $code) { Fail 'VS Code CLI command "code" was not found in PATH.' }
& $code.Source --new-window '.'
if ($LASTEXITCODE -ne 0) { Fail 'VS Code could not be opened.' }

Write-Host "`nSUCCESS" -ForegroundColor Green
Write-Host 'AI-Engineer Operator Bridge is running with the OpenAI provider enabled.'
Write-Host 'VS Code receives only the local API token, not the OpenAI API key.' -ForegroundColor Green
Write-Host 'Keep the Operator Bridge PowerShell window open while using AI-Engineer.'
Write-Host 'In the AI-Engineer side panel, press Refresh and confirm provider openai.'
