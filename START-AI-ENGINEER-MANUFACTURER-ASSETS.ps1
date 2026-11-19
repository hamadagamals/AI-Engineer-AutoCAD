[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot
$temporaryKey = $false
if ([string]::IsNullOrWhiteSpace($env:OPENAI_API_KEY)) {
    $secure = Read-Host 'OpenAI API key (process memory only)' -AsSecureString
    $pointer = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($secure)
    try {
        $env:OPENAI_API_KEY = [Runtime.InteropServices.Marshal]::PtrToStringBSTR($pointer)
        $temporaryKey = $true
    } finally {
        [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($pointer)
    }
}
try {
    python.exe -m integrations.manufacturer_asset_console
    exit $LASTEXITCODE
} finally {
    if ($temporaryKey) { Remove-Item Env:\OPENAI_API_KEY -ErrorAction SilentlyContinue }
}
