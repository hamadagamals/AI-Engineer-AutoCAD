[CmdletBinding()]
param(
    [string]$AutoCADRoot = "C:\Program Files\Autodesk\AutoCAD 2021",
    [string]$PluginDll = ""
)

$ErrorActionPreference = "Stop"
$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$repoRoot = Split-Path -Parent $scriptDir

if ([string]::IsNullOrWhiteSpace($PluginDll)) {
    $PluginDll = Join-Path $repoRoot "workspace\.ai-engineer\autocad\context\bridge\AIEngineer.AutoCAD.Context.dll"
}
$PluginDll = [System.IO.Path]::GetFullPath($PluginDll)
if (-not (Test-Path -LiteralPath $PluginDll -PathType Leaf)) {
    throw "Plugin DLL not found: $PluginDll"
}

$accore = Join-Path $AutoCADRoot "accoreconsole.exe"
if (-not (Test-Path -LiteralPath $accore -PathType Leaf)) {
    throw "accoreconsole.exe not found under $AutoCADRoot"
}

$compiler = Join-Path $env:WINDIR "Microsoft.NET\Framework64\v4.0.30319\csc.exe"
if (-not (Test-Path -LiteralPath $compiler -PathType Leaf)) {
    throw "C# compiler not found: $compiler"
}

$harnessCs = Join-Path $repoRoot "tests\autocad_accore_harness\AcCoreRegressionVerifier.cs"
if (-not (Test-Path -LiteralPath $harnessCs -PathType Leaf)) {
    throw "Harness source not found: $harnessCs"
}

$runId = [System.Guid]::NewGuid().ToString("N").Substring(0, 8)
$harnessDll = Join-Path $env:TEMP ("AcCoreReg_" + $runId + ".dll")
$scrPath = Join-Path $env:TEMP ("accore_reg_" + $runId + ".scr")

$prevEnv = $env:AI_ENG_PLUGIN_DLL
$env:AI_ENG_PLUGIN_DLL = $PluginDll

try {
    & $compiler /nologo /utf8output /target:library /platform:x64 "/out:$harnessDll" `
        "/reference:$(Join-Path $AutoCADRoot 'acdbmgd.dll')" `
        "/reference:$(Join-Path $AutoCADRoot 'acmgd.dll')" `
        "/reference:$(Join-Path $AutoCADRoot 'accoremgd.dll')" `
        /reference:System.Web.Extensions.dll `
        $harnessCs
    if ($LASTEXITCODE -ne 0) {
        throw "Harness compilation failed with exit code $LASTEXITCODE"
    }

    $scrContent = @"
_NETLOAD "$harnessDll"
ACCOREREGRESS
_QUIT _Y
"@
    Set-Content -LiteralPath $scrPath -Value $scrContent -Encoding ASCII

    $rawLines = & $accore /s $scrPath /l en-US
    $exitCode = $LASTEXITCODE
    $cleanLines = $rawLines | ForEach-Object { $_ -replace "\0", "" }
    $cleanText = $cleanLines -join "`n"

    $cleanLines | Select-String -Pattern "AUTOCAD_TOOLS|ROUTER_COUNTS|JIT_PHASE2_OK|TEST1A|TEST1C|TEST2|TEST3|BENCHMARK_30C_300F_MS|ACCORE_REGRESSION_" | ForEach-Object {
        Write-Host $_.Line
    }

    if ($exitCode -ne 0) {
        throw "accoreconsole.exe exited with code $exitCode"
    }
    if ($cleanText -notmatch "ACCORE_REGRESSION_PASS") {
        Write-Host $cleanText
        throw "AutoCAD regression suite did not emit ACCORE_REGRESSION_PASS"
    }
}
finally {
    $env:AI_ENG_PLUGIN_DLL = $prevEnv
    Remove-Item -LiteralPath $harnessDll, $scrPath -Force -ErrorAction SilentlyContinue
}

