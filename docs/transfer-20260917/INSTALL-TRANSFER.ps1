[CmdletBinding()]
param([Parameter(Mandatory = $true)][string]$Destination)

$ErrorActionPreference = 'Stop'
$bundleRoot = [IO.Path]::GetFullPath($PSScriptRoot)
$destinationPath = [IO.Path]::GetFullPath($Destination)
if (Test-Path -LiteralPath $destinationPath) { throw 'Destination already exists. Choose a new short directory; old copies are never overwritten.' }
if ($destinationPath.Length -gt 80) { throw 'Use a short installation path such as C:\AI-Engineer-r50.' }
if ($destinationPath.TrimEnd('\') -eq [IO.Path]::GetPathRoot($destinationPath).TrimEnd('\')) {
    throw 'A drive root cannot be the installation destination.'
}
$manifestPath = Join-Path $bundleRoot 'TRANSFER-MANIFEST.json'
$manifest = Get-Content -LiteralPath $manifestPath -Raw -Encoding UTF8 | ConvertFrom-Json
if ($manifest.schema_version -ne 1) { throw 'Unsupported transfer manifest.' }
foreach ($entry in $manifest.files.PSObject.Properties) {
    $relative = $entry.Name.Replace('/', '\')
    if ([IO.Path]::IsPathRooted($relative) -or ($relative.Split('\') -contains '..')) { throw 'Invalid transfer entry path.' }
    $filePath = [IO.Path]::GetFullPath((Join-Path $bundleRoot $relative))
    if (-not $filePath.StartsWith($bundleRoot.TrimEnd('\') + '\', [StringComparison]::OrdinalIgnoreCase)) { throw 'Transfer entry escapes bundle root.' }
    if (-not (Test-Path -LiteralPath $filePath -PathType Leaf)) { throw "Missing transfer file: $relative" }
    if ((Get-Item -LiteralPath $filePath).Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Linked transfer files are unsupported.' }
    $actual = (Get-FileHash -LiteralPath $filePath -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($actual -cne $entry.Value) { throw "Transfer checksum mismatch: $relative" }
}
$archive = Join-Path $bundleRoot 'AI-Engineer-portable-r50.zip'
$pythonCandidates = @(
    (Join-Path $env:LOCALAPPDATA 'Programs\Python\Python313\python.exe')
    (Get-ItemProperty -LiteralPath 'Registry::HKEY_CURRENT_USER\Software\Python\PythonCore\3.13\InstallPath' -ErrorAction SilentlyContinue).ExecutablePath
    (Get-ItemProperty -LiteralPath 'Registry::HKEY_LOCAL_MACHINE\Software\Python\PythonCore\3.13\InstallPath' -ErrorAction SilentlyContinue).ExecutablePath
    (Get-Command python.exe -All -ErrorAction SilentlyContinue | Where-Object { $_.Source -notlike '*\PyManager\*' } | Select-Object -ExpandProperty Source)
) | Where-Object { $_ -and (Test-Path -LiteralPath $_ -PathType Leaf) } | Select-Object -Unique
$pythonExecutable = $null
foreach ($candidate in $pythonCandidates) {
    & $candidate -c "import sys; raise SystemExit(0 if sys.version_info[:2] == (3, 13) else 1)"
    if ($LASTEXITCODE -eq 0) { $pythonExecutable = $candidate; break }
}
if (-not $pythonExecutable) { throw 'Python 3.13 was not found. Install Python 3.13 and rerun this installer.' }
& $pythonExecutable $archive install $archive $destinationPath --confirm
if ($LASTEXITCODE -ne 0) { throw 'Portable installation failed. Do not continue.' }
Copy-Item -LiteralPath (Join-Path $bundleRoot 'tests') -Destination $destinationPath -Recurse -ErrorAction Stop
$reportsPath = Join-Path $destinationPath 'workspace\reports'
New-Item -ItemType Directory -Path $reportsPath -Force | Out-Null
Copy-Item -LiteralPath (Join-Path $bundleRoot 'project-assets\hutchison-ports-panel-ga-20260914') -Destination $reportsPath -Recurse -ErrorAction Stop
$reviewPath = Join-Path $destinationPath 'workspace\review-drawings'
New-Item -ItemType Directory -Path $reviewPath -Force | Out-Null
$sourceDrawing = Join-Path $reportsPath 'hutchison-ports-panel-ga-20260914\HUTCHISON_PANEL_GA_PREVIEW_R03.dwg'
$reviewDrawing = Join-Path $reviewPath 'HUTCHISON_PANEL_GA_PREVIEW_R03.dwg'
Copy-Item -LiteralPath $sourceDrawing -Destination $reviewDrawing -ErrorAction Stop
if ((Get-FileHash -LiteralPath $sourceDrawing).Hash -ne (Get-FileHash -LiteralPath $reviewDrawing).Hash) { throw 'Review drawing checksum mismatch.' }
Copy-Item -LiteralPath (Join-Path $bundleRoot 'START-HERE-AR.md') -Destination (Join-Path $destinationPath 'START-HERE-AR.md') -ErrorAction Stop
Copy-Item -LiteralPath $manifestPath -Destination (Join-Path $destinationPath 'TRANSFER-MANIFEST.json') -ErrorAction Stop
Write-Host "Installed current source, tests and panel assets at $destinationPath"
Write-Host 'Read START-HERE-AR.md before running. Dependencies, provider keys and Autodesk licences are not included.'
