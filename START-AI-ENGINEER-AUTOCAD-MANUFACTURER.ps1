$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot

function New-SessionToken {
    $bytes = New-Object byte[] 32
    $rng = [Security.Cryptography.RandomNumberGenerator]::Create()
    try { $rng.GetBytes($bytes) } finally { $rng.Dispose() }
    return [Convert]::ToBase64String($bytes)
}

$port = 8765
$listener = Get-NetTCPConnection -LocalPort $port -State Listen -ErrorAction SilentlyContinue |
    Select-Object -First 1
if ($listener) { throw "Port $port is already in use; stop the existing bridge first." }

$autocadRoot = 'C:\Program Files\Autodesk\AutoCAD 2021'
$acad = Join-Path $autocadRoot 'acad.exe'
if (-not (Test-Path -LiteralPath $acad -PathType Leaf)) {
    throw 'AutoCAD 2021 was not found at the reviewed installation path.'
}
$request = Join-Path $PSScriptRoot '.ai-engineer\manufacturer\live-acceptance\legrand-004280\manufacturer-review-request.json'
if (-not (Test-Path -LiteralPath $request -PathType Leaf)) {
    throw 'The reviewed M23 manufacturer request is missing.'
}

$buildOutput = & powershell -NoProfile -ExecutionPolicy Bypass -File `
    (Join-Path $PSScriptRoot 'integrations\autocad_context_plugin\build.ps1') `
    -WorkspaceRoot $PSScriptRoot
if ($LASTEXITCODE -ne 0) { throw 'AutoCAD manufacturer bridge build failed.' }
$bridge = [IO.Path]::GetFullPath(($buildOutput | Select-Object -Last 1))

$env:AI_ENGINEER_API_TOKEN = New-SessionToken
$env:AI_ENGINEER_API_HOST = '127.0.0.1'
$env:AI_ENGINEER_API_PORT = [string]$port
$env:AI_ENGINEER_AUTOCAD_BRIDGE = $bridge
$env:AI_ENGINEER_MANUFACTURER_REVIEW_REQUEST = [IO.Path]::GetFullPath($request)

$api = Start-Process python -ArgumentList @(
    'launcher.py','operator','--allow-project-root'
) -WorkingDirectory $PSScriptRoot -WindowStyle Hidden -PassThru
try {
    $ready = $false
    for ($attempt = 0; $attempt -lt 20; $attempt++) {
        if ($api.HasExited) { throw "Local API exited with code $($api.ExitCode)." }
        try {
            $health = Invoke-RestMethod -Uri "http://127.0.0.1:$port/health" -TimeoutSec 1
            if ($health.status -eq 'ok') { $ready = $true; break }
        } catch {}
        Start-Sleep -Milliseconds 500
    }
    if (-not $ready) { throw 'Local API did not become ready.' }
    $script = Join-Path $PSScriptRoot 'integrations\autocad_context_plugin\live_manufacturer_palette.scr'
    $acadProcess = Start-Process -FilePath $acad -ArgumentList @('/nologo','/b',$script) -PassThru
    Write-Host 'AutoCAD opened with the exact Legrand manufacturer review preloaded.'
    Write-Host 'Open the Manufacturers tab and confirm Complete=true, three PASS findings, and all negative action flags.'
    Write-Host 'Close AutoCAD after the visual check; the temporary API will then stop.'
    Wait-Process -Id $acadProcess.Id
}
finally {
    if (-not $api.HasExited) { Stop-Process -Id $api.Id }
}
