[CmdletBinding()]
param(
    [int]$Port = 8765,
    [string]$DrawingPath = '',
    [ValidateSet(2021, 2023)]
    [int]$AutoCADYear = 2021,
    [string]$AutoCADRoot = '',
    [switch]$ManualPluginLoad,
    [switch]$UseConfiguredProvider,
    [switch]$WritableScratchAcceptance
)

$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot

function Fail([string]$Message) {
    Write-Host "ERROR: $Message" -ForegroundColor Red
    exit 1
}

function New-SessionToken {
    $bytes = New-Object byte[] 32
    $rng = [Security.Cryptography.RandomNumberGenerator]::Create()
    try { $rng.GetBytes($bytes) } finally { $rng.Dispose() }
    return [Convert]::ToBase64String($bytes)
}

function Get-ReviewedAutoCADDocument([object]$Automation, [string]$Drawing) {
    if ($null -eq $Automation) { return $null }
    try {
        $candidate = $Automation.ActiveDocument
        if ($null -eq $candidate) { return $null }
        $fullName = [string]$candidate.FullName
        if ([string]::IsNullOrWhiteSpace($fullName)) { return $null }
        if (-not [System.IO.Path]::GetFullPath($fullName).Equals(
            $Drawing, [StringComparison]::OrdinalIgnoreCase)) { return $null }
        if ([int]$candidate.GetVariable('CMDACTIVE') -ne 0) { return $null }
        return $candidate
    } catch {
        return $null
    }
}

if ($env:OS -ne 'Windows_NT') { Fail 'This launcher requires Windows.' }
if ($Port -lt 1024 -or $Port -gt 65535) { Fail 'Port must be from 1024 through 65535.' }
if (Get-Process -Name acad -ErrorAction SilentlyContinue) {
    Fail 'Close the existing AutoCAD process before starting isolated preview acceptance.'
}
if (Get-NetTCPConnection -LocalPort $Port -State Listen -ErrorAction SilentlyContinue) {
    Fail "Port $Port is already in use."
}

$workspace = (Resolve-Path '.\workspace').Path
$operatorPathProbe = Join-Path $workspace (
    '.ai-engineer\autocad\operator-channel\documents\' + ('a' * 36) +
    '.json.' + ('b' * 32) + '.tmp'
)
if ($operatorPathProbe.Length -ge 260) {
    Fail "The installed workspace path is too long for the AutoCAD $AutoCADYear .NET operator channel; install AI-Engineer in a shorter directory."
}
$resolvedAutoCADRoot = if ([string]::IsNullOrWhiteSpace($AutoCADRoot)) {
    "C:\Program Files\Autodesk\AutoCAD $AutoCADYear"
} else {
    [System.IO.Path]::GetFullPath($AutoCADRoot)
}
$autoCADProgId = if ($AutoCADYear -eq 2023) {
    'AutoCAD.Application.24.2'
} else {
    'AutoCAD.Application.24'
}
$build = Join-Path $PSScriptRoot 'integrations\autocad_context_plugin\build.ps1'
$bridge = Join-Path $workspace '.ai-engineer\autocad\context\bridge\AIEngineer.AutoCAD.Context.dll'
$buildOutput = @()
$buildExitCode = 1
for ($buildAttempt = 1; $buildAttempt -le 2; $buildAttempt++) {
    $buildOutput = @(& powershell.exe -NoProfile -ExecutionPolicy Bypass -File $build `
        -WorkspaceRoot $workspace -AutoCADYear $AutoCADYear -AutoCADRoot $resolvedAutoCADRoot 2>&1)
    $buildExitCode = $LASTEXITCODE
    if ($buildExitCode -eq 0 -and (Test-Path -LiteralPath $bridge -PathType Leaf)) { break }
    if ($buildAttempt -lt 2) { Start-Sleep -Seconds 2 }
}
if ($buildExitCode -ne 0 -or -not (Test-Path -LiteralPath $bridge -PathType Leaf)) {
    $buildDiagnostic = ($buildOutput | ForEach-Object { [string]$_ }) -join [Environment]::NewLine
    if (-not [string]::IsNullOrWhiteSpace($buildDiagnostic)) {
        Write-Host $buildDiagnostic -ForegroundColor DarkYellow
    }
    Fail "AutoCAD bridge build failed after two attempts (exit $buildExitCode)."
}

$source = if ([string]::IsNullOrWhiteSpace($DrawingPath)) {
    Join-Path $workspace 'autocad-acceptance\Electrical Power Sample.dwg'
} else {
    [System.IO.Path]::GetFullPath($DrawingPath)
}
if (-not (Test-Path -LiteralPath $source -PathType Leaf)) {
    Fail 'The reviewed AutoCAD acceptance source is missing.'
}
$workspacePrefix = $workspace.TrimEnd([System.IO.Path]::DirectorySeparatorChar) +
    [System.IO.Path]::DirectorySeparatorChar
if (-not $source.StartsWith($workspacePrefix, [StringComparison]::OrdinalIgnoreCase)) {
    Fail 'The preview drawing source must be inside the configured workspace.'
}
$scratchRunsRoot = Join-Path $workspace '.ai-engineer\autocad\runs'
$scratchRunsPrefix = $scratchRunsRoot.TrimEnd([System.IO.Path]::DirectorySeparatorChar) +
    [System.IO.Path]::DirectorySeparatorChar
if ($WritableScratchAcceptance -and
    (-not $source.StartsWith($scratchRunsPrefix, [StringComparison]::OrdinalIgnoreCase) -or
     -not [System.IO.Path]::GetFileName($source).Equals(
        'source.dwg', [StringComparison]::OrdinalIgnoreCase))) {
    Fail 'Writable Scratch acceptance requires an exact source.dwg beneath the managed AutoCAD runs directory.'
}
if ($WritableScratchAcceptance) {
    $sourceItem = Get-Item -LiteralPath $source
    if (($sourceItem.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) {
        Fail 'Writable Scratch acceptance rejects a reparse-point drawing.'
    }
    $cursor = $sourceItem.Directory
    while ($null -ne $cursor -and -not $cursor.FullName.Equals(
        $scratchRunsRoot, [StringComparison]::OrdinalIgnoreCase)) {
        if (($cursor.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) {
            Fail 'Writable Scratch acceptance rejects a reparse-point directory.'
        }
        $cursor = $cursor.Parent
    }
    if ($null -eq $cursor) {
        Fail 'Writable Scratch acceptance could not prove managed-run containment.'
    }
}
$previewRoot = Join-Path $workspace 'autocad-preview'
New-Item -ItemType Directory -Path $previewRoot -Force | Out-Null
$copyTimestamp = Get-Date -Format 'yyyyMMdd-HHmmss'
$copyLabel = if ($WritableScratchAcceptance) { 'Scratch Acceptance' } else { 'Read Only' }
$drawing = Join-Path $previewRoot (
    [System.IO.Path]::GetFileNameWithoutExtension($source) +
    " - $copyLabel $copyTimestamp" +
    [System.IO.Path]::GetExtension($source)
)
if (Test-Path -LiteralPath $drawing) {
    Fail 'The disposable preview drawing already exists; retain it as evidence or remove it explicitly.'
}
Copy-Item -LiteralPath $source -Destination $drawing
if ((Get-FileHash -LiteralPath $source -Algorithm SHA256).Hash -ne
    (Get-FileHash -LiteralPath $drawing -Algorithm SHA256).Hash) {
    Fail 'The disposable drawing hash differs from its reviewed source.'
}
if ($WritableScratchAcceptance) {
    (Get-Item -LiteralPath $drawing).IsReadOnly = $false
} else {
    (Get-Item -LiteralPath $drawing).IsReadOnly = $true
}

$env:AI_ENGINEER_API_TOKEN = New-SessionToken
$env:AI_ENGINEER_API_HOST = '127.0.0.1'
$env:AI_ENGINEER_API_PORT = [string]$Port
if ($UseConfiguredProvider) {
    if ([string]::IsNullOrWhiteSpace($env:AI_ENGINEER_PROVIDER) -or
        $env:AI_ENGINEER_PROVIDER -eq 'none') {
        Fail 'UseConfiguredProvider requires one explicitly configured provider mode.'
    }
} else {
    $env:AI_ENGINEER_PROVIDER = 'none'
}
$env:AI_ENGINEER_OPERATOR_ROOTS = $workspace
$env:AI_ENGINEER_AUTOCAD_BRIDGE = [string]$bridge
$startupLogPath = Join-Path $workspace '.ai-engineer\autocad\context\bridge\startup-markers.log'
if (Test-Path -LiteralPath $startupLogPath) { Remove-Item -LiteralPath $startupLogPath -Force }
$env:AI_ENGINEER_AUTOCAD_STARTUP_LOG = $startupLogPath
$python = (Get-Command python.exe -ErrorAction Stop).Source
$bridgeProcess = Start-Process -FilePath $python -PassThru -WindowStyle Hidden `
    -WorkingDirectory $PSScriptRoot -ArgumentList @('launcher.py', 'operator')

$healthy = $false
for ($attempt = 0; $attempt -lt 15; $attempt++) {
    if ($bridgeProcess.HasExited) { Fail 'The local bridge exited during startup.' }
    try {
        $health = Invoke-RestMethod -Uri "http://127.0.0.1:$Port/health" -TimeoutSec 1
        if ($health.status -eq 'ok') { $healthy = $true; break }
    } catch {}
    Start-Sleep -Seconds 1
}
if (-not $healthy) {
    if (-not $bridgeProcess.HasExited) { Stop-Process -Id $bridgeProcess.Id }
    Fail 'The local bridge did not become healthy.'
}

$acad = Join-Path $resolvedAutoCADRoot 'acad.exe'
if (-not (Test-Path -LiteralPath $acad -PathType Leaf)) { Fail "AutoCAD $AutoCADYear was not found at $resolvedAutoCADRoot." }
$script = Join-Path $PSScriptRoot 'integrations\autocad_context_plugin\live_drafting_preview.scr'
$pulseErrorPath = Join-Path ([System.IO.Path]::GetDirectoryName($bridge)) 'operator-pulse-error.json'
if (Test-Path -LiteralPath $pulseErrorPath) { Remove-Item -LiteralPath $pulseErrorPath -Force }
$statusStartedAt = [DateTime]::UtcNow.AddSeconds(-1)
if ($ManualPluginLoad) {
    $acadProcess = Start-Process -FilePath $acad -PassThru -ArgumentList @('"' + $drawing + '"')
} else {
    $acadProcess = Start-Process -FilePath $acad -PassThru `
        -ArgumentList @('"' + $drawing + '"', '/b', '"' + $script + '"')

    $automation = $null
    $automationReady = $false
    for ($attempt = 0; $attempt -lt 45; $attempt++) {
        if ($acadProcess.HasExited) { break }
        Start-Sleep -Seconds 1
        try {
            $automation = [Runtime.InteropServices.Marshal]::GetActiveObject($autoCADProgId)
            $active = Get-ReviewedAutoCADDocument $automation $drawing
            if ($null -ne $active) {
                $automationReady = $true
                break
            }
        } catch {}
    }
    if (-not $automationReady) {
        if (-not $acadProcess.HasExited) { Stop-Process -Id $acadProcess.Id }
        if (-not $bridgeProcess.HasExited) { Stop-Process -Id $bridgeProcess.Id }
        Fail 'AutoCAD did not expose the reviewed preview drawing after plugin startup.'
    }
    Start-Sleep -Seconds 2
    $active = $null
    for ($attempt = 0; $attempt -lt 10; $attempt++) {
        try {
            $automation = [Runtime.InteropServices.Marshal]::GetActiveObject($autoCADProgId)
            $active = Get-ReviewedAutoCADDocument $automation $drawing
            if ($null -ne $active) { break }
        } catch {}
        Start-Sleep -Milliseconds 500
    }
    if ($null -eq $active) {
        if (-not $acadProcess.HasExited) { Stop-Process -Id $acadProcess.Id }
        if (-not $bridgeProcess.HasExited) { Stop-Process -Id $bridgeProcess.Id }
        Fail 'AutoCAD lost the reviewed preview document before DBMOD verification.'
    }
    $previewDbmod = [int]$active.GetVariable('DBMOD')
    if (($previewDbmod -band (1 -bor 4 -bor 32)) -ne 0) {
        $active.Close($false)
        $active = $automation.Documents.Open($drawing, $true)
        Start-Sleep -Seconds 1
        $previewDbmod = [int]$active.GetVariable('DBMOD')
    }
    if (($previewDbmod -band (1 -bor 4 -bor 32)) -ne 0) {
        if (-not $acadProcess.HasExited) { Stop-Process -Id $acadProcess.Id }
        if (-not $bridgeProcess.HasExited) { Stop-Process -Id $bridgeProcess.Id }
        Fail "AutoCAD preview retained unsupported DBMOD bits: $previewDbmod"
    }
    $statusRoot = Join-Path $workspace '.ai-engineer\autocad\operator-channel\documents'
    $expectedDrawingName = [System.IO.Path]::GetFileName($drawing)
    $expectedDrawingSha = (Get-FileHash -LiteralPath $drawing -Algorithm SHA256).Hash.ToLowerInvariant()
    $operatorStatusReady = $false
    for ($attempt = 0; $attempt -lt 20; $attempt++) {
        Start-Sleep -Milliseconds 250
        if (-not (Test-Path -LiteralPath $statusRoot -PathType Container)) { continue }
        foreach ($statusFile in Get-ChildItem -LiteralPath $statusRoot -Filter '*.json' -File) {
            if ($statusFile.LastWriteTimeUtc -lt $statusStartedAt) { continue }
            try {
                $status = Get-Content -LiteralPath $statusFile.FullName -Raw | ConvertFrom-Json
                if ($status.drawing_name -eq $expectedDrawingName -and
                    $status.source_drawing_sha256 -eq $expectedDrawingSha -and
                    $status.read_only_channel_ready -eq $true -and
                    $status.drawing_mutation_performed -eq $false) {
                    $operatorStatusReady = $true
                    break
                }
            } catch {}
        }
        if ($operatorStatusReady) { break }
    }
    if (-not $operatorStatusReady) {
        $pulseError = if (Test-Path -LiteralPath $pulseErrorPath -PathType Leaf) {
            try {
                $value = Get-Content -LiteralPath $pulseErrorPath -Raw | ConvertFrom-Json
                "$($value.error_type): $($value.message)"
            } catch { 'invalid diagnostic' }
        } else { 'no diagnostic returned' }
        $startupMarkers = if (Test-Path -LiteralPath $startupLogPath -PathType Leaf) {
            (Get-Content -LiteralPath $startupLogPath) -join ','
        } else { 'no startup markers returned' }
        if (-not $acadProcess.HasExited) { Stop-Process -Id $acadProcess.Id }
        if (-not $bridgeProcess.HasExited) { Stop-Process -Id $bridgeProcess.Id }
        Fail "AutoCAD did not publish the exact read-only operator status after startup: $pulseError; $startupMarkers"
    }
}
$monitor = Join-Path $PSScriptRoot 'integrations\stop_bridge_after_autocad.ps1'
Start-Process powershell.exe -WindowStyle Hidden -ArgumentList @(
    '-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', $monitor,
    '-AutoCADPid', [string]$acadProcess.Id,
    '-BridgePid', [string]$bridgeProcess.Id
) | Out-Null

Write-Host 'AutoCAD preview acceptance started.' -ForegroundColor Green
Write-Host 'Select the intended objects, run AIENGCONTEXT, then open the required AI-Engineer tab.'
Write-Host 'Takeoff, reviewed CSV, and LV calculations remain read-only.'
if ($WritableScratchAcceptance) {
    Write-Host "Disposable writable Scratch acceptance drawing: $drawing" -ForegroundColor Yellow
} else {
    Write-Host "Disposable read-only drawing: $drawing"
}
Write-Host "Bridge PID: $($bridgeProcess.Id)"
if (-not $ManualPluginLoad) {
    Write-Host "Preview DBMOD: $previewDbmod (clean or presentation-only)"
    Write-Host 'Operator status pulse: verified'
}
if ($ManualPluginLoad) {
    Write-Host "Manual NETLOAD bridge: $bridge" -ForegroundColor Yellow
    Write-Host 'After NETLOAD, run AIENGPALETTE.' -ForegroundColor Yellow
}
