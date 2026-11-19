param(
    [int]$Port = 8765,
    [ValidateSet('Ask', 'Free', 'Economy', 'Quality')]
    [string]$SearchMode = 'Ask'
)

$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot

function Fail([string]$Message) {
    Write-Host "`nERROR: $Message" -ForegroundColor Red
    exit 1
}

function New-SessionToken {
    $bytes = New-Object byte[] 32
    $rng = [Security.Cryptography.RandomNumberGenerator]::Create()
    try { $rng.GetBytes($bytes) } finally { $rng.Dispose() }
    return [Convert]::ToBase64String($bytes)
}

if ($env:OS -ne 'Windows_NT') { Fail 'This launcher requires Windows.' }
if ($Port -lt 1024 -or $Port -gt 65535) { Fail 'Port must be from 1024 through 65535.' }
$listener = Get-NetTCPConnection -LocalPort $Port -State Listen -ErrorAction SilentlyContinue |
    Select-Object -First 1
if ($listener) { Fail "Port $Port is already in use; stop the existing bridge first." }

$autocadRoot = 'C:\Program Files\Autodesk\AutoCAD 2021'
$acad = Join-Path $autocadRoot 'acad.exe'
if (-not (Test-Path -LiteralPath $acad -PathType Leaf)) {
    Fail 'AutoCAD 2021 was not found at the reviewed installation path.'
}

Write-Host "`nAI-Engineer official-product discovery" -ForegroundColor Cyan
Write-Host 'Available paths: Free manual verification, Economy AI search, or Quality AI search.'
Write-Host 'AI-Engineer independently re-fetches and hashes candidate product pages.'
Write-Host 'No product approval, download, catalog registration, or drawing mutation is authorized.'
if ($SearchMode -eq 'Ask') {
    $choice = (Read-Host 'Choose FREE, ECONOMY, or QUALITY').Trim().ToUpperInvariant()
    if ($choice -eq 'FREE') { $SearchMode = 'Free' }
    elseif ($choice -eq 'ECONOMY') { $SearchMode = 'Economy' }
    elseif ($choice -eq 'QUALITY') { $SearchMode = 'Quality' }
    else { Fail 'Search mode must be FREE, ECONOMY, or QUALITY.' }
}

$plainKey = $null
if ($SearchMode -ne 'Free') {
    Write-Host 'The reviewed request will be sent to OpenAI for official-domain web search.'
    Write-Host 'The API key stays only in the hidden API process and is removed before AutoCAD starts.'
    $confirm = (Read-Host 'Type YES to permit this one-session paid provider egress').Trim()
    if ($confirm.ToUpperInvariant() -ne 'YES' -and $confirm -cne 'نعم') {
        Fail 'Provider egress was not approved.'
    }
    $secure = Read-Host 'OpenAI API key' -AsSecureString
    $ptr = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($secure)
    try { $plainKey = [Runtime.InteropServices.Marshal]::PtrToStringBSTR($ptr) }
    finally { [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($ptr) }
    if ([string]::IsNullOrWhiteSpace($plainKey) -or $plainKey.Length -lt 20) {
        Fail 'The OpenAI API key is missing or too short.'
    }
}

$requestOutput = python -m integrations.build_m23_discovery_live_acceptance $PSScriptRoot
if ($LASTEXITCODE -ne 0) { Fail 'The reviewed discovery request could not be built.' }
$requestPath = [IO.Path]::GetFullPath(($requestOutput | Select-Object -Last 1))
if (-not (Test-Path -LiteralPath $requestPath -PathType Leaf)) {
    Fail 'The reviewed discovery request was not created.'
}

$buildOutput = & powershell -NoProfile -ExecutionPolicy Bypass -File `
    (Join-Path $PSScriptRoot 'integrations\autocad_context_plugin\build.ps1') `
    -WorkspaceRoot $PSScriptRoot
if ($LASTEXITCODE -ne 0) { Fail 'AutoCAD discovery bridge build failed.' }
$bridge = [IO.Path]::GetFullPath(($buildOutput | Select-Object -Last 1))

$env:AI_ENGINEER_API_TOKEN = New-SessionToken
$env:AI_ENGINEER_API_HOST = '127.0.0.1'
$env:AI_ENGINEER_API_PORT = [string]$Port
$env:AI_ENGINEER_PROVIDER = 'none'
if ($SearchMode -ne 'Free') {
    $env:AI_ENGINEER_OPENAI_ENABLE = '1'
    $env:AI_ENGINEER_OPENAI_EGRESS_CONFIRMED = '1'
    $env:AI_ENGINEER_MANUFACTURER_DISCOVERY_ENABLE = '1'
    $env:AI_ENGINEER_MANUFACTURER_SAVED_FALLBACK = '1'
    $env:AI_ENGINEER_OPENAI_REASONING_MODE = 'standard'
    if ($SearchMode -eq 'Economy') {
        $env:AI_ENGINEER_OPENAI_MODEL = 'gpt-5.6-luna'
        $env:AI_ENGINEER_OPENAI_REASONING_EFFORT = 'low'
    } else {
        $env:AI_ENGINEER_OPENAI_MODEL = 'gpt-5.6'
        $env:AI_ENGINEER_OPENAI_REASONING_EFFORT = 'high'
    }
    $env:OPENAI_API_KEY = $plainKey
}
$plainKey = $null
$env:AI_ENGINEER_AUTOCAD_BRIDGE = $bridge
$env:AI_ENGINEER_MANUFACTURER_DISCOVERY_REQUEST = $requestPath

$api = $null
try {
    python launcher.py operator --check --allow-project-root
    if ($LASTEXITCODE -ne 0) { Fail 'The local API configuration failed.' }
    $api = Start-Process python -ArgumentList @(
        'launcher.py','operator','--allow-project-root'
    ) -WorkingDirectory $PSScriptRoot -WindowStyle Hidden -PassThru
    $ready = $false
    for ($attempt = 0; $attempt -lt 30; $attempt++) {
        if ($api.HasExited) { Fail "The local API exited with code $($api.ExitCode)." }
        try {
            $health = Invoke-RestMethod -Uri "http://127.0.0.1:$Port/health" -TimeoutSec 1
            if ($health.status -eq 'ok') { $ready = $true; break }
        } catch {}
        Start-Sleep -Milliseconds 500
    }
    if (-not $ready) { Fail 'The local API did not become ready.' }

    Remove-Item Env:OPENAI_API_KEY -ErrorAction SilentlyContinue
    $script = Join-Path $PSScriptRoot 'integrations\autocad_context_plugin\live_manufacturer_palette.scr'
    $acadProcess = Start-Process -FilePath $acad -ArgumentList @('/nologo','/b',$script) -PassThru
    Write-Host "`nAutoCAD opened in $SearchMode product-discovery mode." -ForegroundColor Green
    if ($SearchMode -eq 'Free') {
        Write-Host 'Open Product Discovery and click Verify free candidate file.'
    } else {
        Write-Host 'Open Product Discovery and click Search official sources.'
    }
    Write-Host 'Choose one candidate for separate review and confirm every action flag remains false.'
    Wait-Process -Id $acadProcess.Id
}
finally {
    Remove-Item Env:OPENAI_API_KEY -ErrorAction SilentlyContinue
    Remove-Item Env:AI_ENGINEER_OPENAI_ENABLE -ErrorAction SilentlyContinue
    Remove-Item Env:AI_ENGINEER_OPENAI_EGRESS_CONFIRMED -ErrorAction SilentlyContinue
    Remove-Item Env:AI_ENGINEER_MANUFACTURER_DISCOVERY_ENABLE -ErrorAction SilentlyContinue
    Remove-Item Env:AI_ENGINEER_MANUFACTURER_SAVED_FALLBACK -ErrorAction SilentlyContinue
    Remove-Item Env:AI_ENGINEER_OPENAI_MODEL -ErrorAction SilentlyContinue
    Remove-Item Env:AI_ENGINEER_OPENAI_REASONING_EFFORT -ErrorAction SilentlyContinue
    Remove-Item Env:AI_ENGINEER_OPENAI_REASONING_MODE -ErrorAction SilentlyContinue
    Remove-Item Env:AI_ENGINEER_PROVIDER -ErrorAction SilentlyContinue
    if ($null -ne $api -and -not $api.HasExited) { Stop-Process -Id $api.Id }
}
