[CmdletBinding()]
param([string]$SettingsPath = (Join-Path $env:USERPROFILE '.gemini\settings.json'))

$ErrorActionPreference = 'Stop'
$projectRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$serverPath = Join-Path $projectRoot 'integrations\gemini_mcp_server.py'
if (-not (Test-Path -LiteralPath $serverPath -PathType Leaf)) {
    throw "AI-Engineer Gemini MCP server is missing: $serverPath"
}
$python = (Get-Command python -ErrorAction Stop).Source
$settingsFile = [IO.Path]::GetFullPath($SettingsPath)
$settingsDirectory = Split-Path -Parent $settingsFile
New-Item -ItemType Directory -Path $settingsDirectory -Force | Out-Null

$settings = [pscustomobject]@{}
if (Test-Path -LiteralPath $settingsFile -PathType Leaf) {
    $raw = Get-Content -LiteralPath $settingsFile -Raw -Encoding UTF8
    if (-not [string]::IsNullOrWhiteSpace($raw)) {
        $settings = $raw | ConvertFrom-Json
    }
    $stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
    Copy-Item -LiteralPath $settingsFile -Destination "$settingsFile.$stamp.bak" -ErrorAction Stop
}
if (-not $settings.PSObject.Properties['mcpServers']) {
    $settings | Add-Member -MemberType NoteProperty -Name 'mcpServers' -Value ([pscustomobject]@{})
}
if ($null -eq $settings.mcpServers -or $settings.mcpServers -is [System.Array] -or $settings.mcpServers -is [string]) {
    throw 'Existing Gemini mcpServers setting is not an object.'
}
$settings.mcpServers.PSObject.Properties.Remove('ai-engineer-autocad')
$entry = [pscustomobject]@{
    command = $python
    args = @('-u', $serverPath)
}
$settings.mcpServers | Add-Member -MemberType NoteProperty -Name 'ai-engineer-autocad' -Value $entry
$temporary = "$settingsFile.tmp"
$settings | ConvertTo-Json -Depth 20 | Set-Content -LiteralPath $temporary -Encoding UTF8
Move-Item -LiteralPath $temporary -Destination $settingsFile -Force
Write-Host "Gemini MCP configuration installed: $settingsFile" -ForegroundColor Green
Write-Host 'Reload VS Code, enable Gemini Agent mode, then run /mcp and /tools.' -ForegroundColor Cyan
