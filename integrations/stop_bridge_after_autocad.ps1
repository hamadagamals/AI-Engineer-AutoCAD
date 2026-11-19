[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidateRange(1, [int]::MaxValue)]
    [int]$AutoCADPid,

    [Parameter(Mandatory = $true)]
    [ValidateRange(1, [int]::MaxValue)]
    [int]$BridgePid
)

$ErrorActionPreference = 'Stop'

try {
    Wait-Process -Id $AutoCADPid
} catch {
    # AutoCAD may have already exited before this monitor started.
}

$bridge = Get-Process -Id $BridgePid -ErrorAction SilentlyContinue
if ($null -ne $bridge -and $bridge.ProcessName -like 'python*') {
    Stop-Process -Id $BridgePid
}
