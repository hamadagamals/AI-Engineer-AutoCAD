param(
    [Parameter(Mandatory=$true)]
    [ValidateSet('local','codex','hybrid')]
    [string]$Provider
)

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

function New-SessionToken {
    $bytes = New-Object byte[] 32
    $rng = [Security.Cryptography.RandomNumberGenerator]::Create()
    try { $rng.GetBytes($bytes) } finally { $rng.Dispose() }
    return [Convert]::ToBase64String($bytes)
}

function Clear-ProviderEnvironment {
    @(
        'AI_ENGINEER_PROVIDER',
        'AI_ENGINEER_OLLAMA_ENABLE',
        'AI_ENGINEER_OLLAMA_URL',
        'AI_ENGINEER_OLLAMA_MODEL',
        'AI_ENGINEER_CODEX_ENABLE',
        'AI_ENGINEER_CODEX_EGRESS_CONFIRMED',
        'AI_ENGINEER_CODEX_COMMAND',
        'AI_ENGINEER_OPENAI_ENABLE',
        'AI_ENGINEER_OPENAI_EGRESS_CONFIRMED',
        'OPENAI_API_KEY',
        'AI_ENGINEER_GEMINI_ENABLE',
        'AI_ENGINEER_GEMINI_EGRESS_CONFIRMED',
        'AI_ENGINEER_GEMINI_MODEL',
        'GEMINI_API_KEY'
    ) | ForEach-Object { Remove-Item "Env:$_" -ErrorAction SilentlyContinue }
}

if ($env:OS -ne 'Windows_NT') { Fail 'This launcher must be run on Windows.' }

$runningCode = Get-Process -Name Code -ErrorAction SilentlyContinue
if ($runningCode) {
    Write-Host 'VS Code is currently running.' -ForegroundColor Yellow
    Write-Host 'Close ALL VS Code windows, then run this launcher again.' -ForegroundColor Yellow
    exit 2
}

$python = Get-Command python -ErrorAction SilentlyContinue
if (-not $python) { Fail 'Python was not found in PATH.' }
$code = Get-Command code -ErrorAction SilentlyContinue
if (-not $code) { Fail 'VS Code CLI command "code" was not found in PATH.' }

$port = if ([string]::IsNullOrWhiteSpace($env:AI_ENGINEER_API_PORT)) { '8765' } else { $env:AI_ENGINEER_API_PORT }
try { $portNumber = [int]$port } catch { Fail 'AI_ENGINEER_API_PORT must be a valid integer.' }
if (Get-Command Get-NetTCPConnection -ErrorAction SilentlyContinue) {
    $listener = Get-NetTCPConnection -LocalPort $portNumber -State Listen -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($listener) {
        Write-Host "Port $portNumber is already in use." -ForegroundColor Yellow
        Write-Host 'Close the previous AI-Engineer Operator Bridge window, then run this launcher again.' -ForegroundColor Yellow
        exit 3
    }
}

Clear-ProviderEnvironment

if ($Provider -in @('local','hybrid')) {
    Step 'Checking local Ollama runtime'
    $ollama = Get-Command ollama -ErrorAction SilentlyContinue
    if (-not $ollama) {
        Write-Host 'Ollama is not installed or not on PATH.' -ForegroundColor Yellow
        Write-Host 'Install Ollama for Windows from the official Ollama site, then run this launcher again.'
        Write-Host 'No AI-Engineer files were changed.'
        exit 4
    }

    $serverReady = $false
    try {
        $null = Invoke-RestMethod -Uri 'http://127.0.0.1:11434/api/tags' -Method Get -TimeoutSec 3
        $serverReady = $true
    } catch { $serverReady = $false }
    if (-not $serverReady) {
        Write-Host 'Starting the local Ollama server...'
        $ollamaServer = Start-Process -FilePath $ollama.Source -ArgumentList 'serve' -PassThru -WindowStyle Minimized
        Start-Sleep -Seconds 3
        if ($ollamaServer.HasExited) { Fail 'Ollama server exited during startup.' }
        try {
            $null = Invoke-RestMethod -Uri 'http://127.0.0.1:11434/api/tags' -Method Get -TimeoutSec 5
        } catch { Fail 'Ollama did not become available on 127.0.0.1:11434.' }
    }

    $model = 'qwen2.5-coder:7b'
    $models = @(& $ollama.Source list 2>&1)
    if ($LASTEXITCODE -ne 0) {
        $models | ForEach-Object { Write-Host $_ }
        Fail 'Could not read the local Ollama model list.'
    }
    if (-not (($models -join "`n") -match [regex]::Escape($model))) {
        Write-Host "Local model $model is not installed." -ForegroundColor Yellow
        Write-Host 'The model download is several GB and may take time.' -ForegroundColor Yellow
        $pull = Read-Host "Type PULL to download $model now, or anything else to stop"
        if ($pull -cne 'PULL') { Fail 'Local model installation was cancelled.' }
        & $ollama.Source pull $model
        if ($LASTEXITCODE -ne 0) { Fail 'Ollama model download failed.' }
    }
    $env:AI_ENGINEER_OLLAMA_ENABLE = '1'
    $env:AI_ENGINEER_OLLAMA_URL = 'http://127.0.0.1:11434'
    Step 'Warming local AI model'
    $warmBody = @{
        model = $model
        prompt = 'Reply exactly READY and nothing else.'
        stream = $false
        keep_alive = '10m'
        options = @{ num_ctx = 4096; temperature = 0 }
    } | ConvertTo-Json -Depth 4 -Compress
    try {
        $warm = Invoke-RestMethod -Uri 'http://127.0.0.1:11434/api/generate' -Method Post -ContentType 'application/json' -Body $warmBody -TimeoutSec 240
    } catch {
        Write-Host $_.Exception.Message -ForegroundColor Yellow
        Fail 'The local model did not complete its warm-up request within 240 seconds.'
    }
    if (-not $warm -or [string]::IsNullOrWhiteSpace([string]$warm.response)) { Fail 'The local model returned no warm-up response.' }
    Write-Host "Local model ready: $model" -ForegroundColor Green
    $env:AI_ENGINEER_OLLAMA_MODEL = $model
    $env:AI_ENGINEER_OLLAMA_TIMEOUT_SECONDS = '240'
    $env:AI_ENGINEER_OLLAMA_CONTEXT_TOKENS = '4096'
}

if ($Provider -in @('codex','hybrid')) {
    Step 'Checking Codex CLI'
    $codex = Get-Command codex -ErrorAction SilentlyContinue
    if (-not $codex) {
        Write-Host 'Codex CLI was not found on PATH.' -ForegroundColor Yellow
        Write-Host 'Install/sign in to the Codex CLI with your ChatGPT account, then run this launcher again.'
        exit 5
    }
    $codexCommand = $codex.Source
    if ($codexCommand.EndsWith('.ps1', [StringComparison]::OrdinalIgnoreCase)) {
        $cmdSibling = [IO.Path]::ChangeExtension($codexCommand, '.cmd')
        if (Test-Path $cmdSibling) { $codexCommand = $cmdSibling }
    }
    if (-not ($codexCommand -match '(?i)codex(?:\.exe|\.cmd)?$')) {
        Fail 'Codex resolved to an unsupported launcher. A codex.exe or codex.cmd launcher is required.'
    }
    Write-Host "Codex CLI found: $codexCommand"
    Write-Host 'Codex will be used only as a reasoning provider. AI-Engineer remains the local executor.'
    Write-Host 'Provider calls may send the bounded reviewed request/context to OpenAI.' -ForegroundColor Yellow
    $confirm = Read-Host 'Type YES to allow Codex data egress for this session'
    if ($confirm -cne 'YES') { Fail 'Codex provider was not enabled.' }
    $env:AI_ENGINEER_CODEX_ENABLE = '1'
    $env:AI_ENGINEER_CODEX_EGRESS_CONFIRMED = '1'
    $env:AI_ENGINEER_CODEX_COMMAND = $codexCommand
}

switch ($Provider) {
    'local'  { $env:AI_ENGINEER_PROVIDER = 'ollama' }
    'codex'  { $env:AI_ENGINEER_PROVIDER = 'codex' }
    'hybrid' { $env:AI_ENGINEER_PROVIDER = 'hybrid' }
}

Step 'Preparing one-session local bridge credentials'
$env:AI_ENGINEER_API_TOKEN = New-SessionToken
$env:AI_ENGINEER_API_HOST = '127.0.0.1'
$env:AI_ENGINEER_API_PORT = [string]$portNumber

Step "Checking AI-Engineer provider: $Provider"
python launcher.py operator --check --allow-project-root
if ($LASTEXITCODE -ne 0) { Fail 'AI-Engineer provider configuration check failed.' }

Step 'Starting AI-Engineer Operator Bridge'
$project = $PSScriptRoot.Replace("'", "''")
$bridgeTitle = switch ($Provider) {
    'local'  { 'AI-Engineer Operator + Local AI' }
    'codex'  { 'AI-Engineer Operator + Codex' }
    'hybrid' { 'AI-Engineer Operator + Hybrid Local/Codex' }
}
$bridgeCommand = "Set-Location '$project'; Write-Host '$bridgeTitle - keep this window open' -ForegroundColor Green; python launcher.py operator --allow-project-root"
$bridge = Start-Process powershell -PassThru -ArgumentList '-NoExit','-NoProfile','-ExecutionPolicy','Bypass','-Command',$bridgeCommand
$bridgeHealth = Wait-BridgeReady -Process $bridge -Port $portNumber
Write-Host "Operator Bridge healthy: $($bridgeHealth.version)" -ForegroundColor Green

# The bridge already inherited all provider settings. Keep VS Code limited to local bridge credentials.
Clear-ProviderEnvironment

Step 'Starting VS Code'
& $code.Source --new-window '.'
if ($LASTEXITCODE -ne 0) { Fail 'VS Code could not be opened.' }

Write-Host "`nSUCCESS" -ForegroundColor Green
Write-Host "AI-Engineer is running in $Provider provider mode."
Write-Host 'Keep the Operator Bridge PowerShell window open.'
Write-Host 'In the AI-Engineer side panel press Refresh and verify the provider name.' -ForegroundColor Green
Write-Host 'High/destructive actions still require AI-Engineer approval.' -ForegroundColor Yellow
