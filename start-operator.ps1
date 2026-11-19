param(
    [switch]$EnableOpenAI,
    [switch]$EnableGemini,
    [switch]$AllowProjectRoot = $true
)

$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot

if ([string]::IsNullOrWhiteSpace($env:AI_ENGINEER_API_TOKEN) -or $env:AI_ENGINEER_API_TOKEN.Length -lt 32) {
    $bytes = New-Object byte[] 32
    $rng = [Security.Cryptography.RandomNumberGenerator]::Create()
    try { $rng.GetBytes($bytes) } finally { $rng.Dispose() }
    $env:AI_ENGINEER_API_TOKEN = [Convert]::ToBase64String($bytes)
}

if ($EnableOpenAI) {
    $env:AI_ENGINEER_PROVIDER = 'openai'
    $env:AI_ENGINEER_OPENAI_ENABLE = '1'
    $env:AI_ENGINEER_OPENAI_EGRESS_CONFIRMED = '1'
    if ([string]::IsNullOrWhiteSpace($env:OPENAI_API_KEY)) {
        $secure = Read-Host 'OpenAI API key (kept only in this process environment)' -AsSecureString
        $ptr = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($secure)
        try {
            $env:OPENAI_API_KEY = [Runtime.InteropServices.Marshal]::PtrToStringBSTR($ptr)
        }
        finally {
            [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($ptr)
        }
    }
}

if ($EnableGemini) {
    if ($EnableOpenAI) { throw 'Choose only one cloud provider per bridge session.' }
    $env:AI_ENGINEER_PROVIDER = 'gemini'
    $env:AI_ENGINEER_GEMINI_ENABLE = '1'
    $env:AI_ENGINEER_GEMINI_EGRESS_CONFIRMED = '1'
    if ([string]::IsNullOrWhiteSpace($env:GEMINI_API_KEY)) {
        $secure = Read-Host 'Gemini API key (kept only in this process environment)' -AsSecureString
        $ptr = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($secure)
        try { $env:GEMINI_API_KEY = [Runtime.InteropServices.Marshal]::PtrToStringBSTR($ptr) }
        finally { [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($ptr) }
    }
}

$argsList = @('launcher.py', 'operator')
if ($AllowProjectRoot) { $argsList += '--allow-project-root' }
python @argsList
