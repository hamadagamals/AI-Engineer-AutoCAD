[CmdletBinding()]
param(
    [ValidateRange(0, 2035)]
    [int]$RevitYear = 0,
    [string]$RevitRoot = "",
    [string]$OutputDirectory = "",
    [switch]$AllInstalled,
    [switch]$Install
)

$ErrorActionPreference = "Stop"
$pluginRoot = Split-Path -Parent $MyInvocation.MyCommand.Path

$sourceFiles = @(
    "SimpleJson.cs",
    "RevitCompat.cs",
    "RevitDirectConfigAndVoice.cs",
    "RevitLvCalculationsTools.cs",
    "RevitLightingAndDialuxTools.cs",
    "RevitToolExecutor.cs",
    "DirectRevitAgent.cs",
    "RevitDockablePaneAndApp.cs"
) | ForEach-Object { Join-Path $pluginRoot $_ }

function Find-InstalledRevits {
    Get-ChildItem "C:\Program Files\Autodesk" -Directory -ErrorAction SilentlyContinue |
        Where-Object { $_.Name -match '^Revit (\d{4})$' } |
        ForEach-Object { [PSCustomObject]@{ Year = [int]$Matches[1]; Path = $_.FullName } } |
        Sort-Object Year -Descending
}

function Build-ForRevitVersion([int]$Year, [string]$RootPath, [string]$OutDir, [bool]$DoInstall) {
    $resolvedRevit = [System.IO.Path]::GetFullPath($RootPath)
    $revitApi = Join-Path $resolvedRevit "RevitAPI.dll"
    $revitApiUi = Join-Path $resolvedRevit "RevitAPIUI.dll"
    if (-not (Test-Path -LiteralPath $revitApi -PathType Leaf) -or -not (Test-Path -LiteralPath $revitApiUi -PathType Leaf)) {
        throw "Revit $Year SDK DLLs not found in: $resolvedRevit"
    }

    if ([string]::IsNullOrWhiteSpace($OutDir)) {
        $OutDir = Join-Path $env:LOCALAPPDATA "AI-Engineer\revit-direct\$Year"
    }
    $resolvedOut = [System.IO.Path]::GetFullPath($OutDir)
    New-Item -ItemType Directory -Path $resolvedOut -Force | Out-Null
    $assembly = Join-Path $resolvedOut "AIEngineer.Revit.Direct.dll"

    $defines = @()
    if ($Year -ge 2018) { $defines += "REVIT2018_OR_GREATER" }
    if ($Year -ge 2021) { $defines += "REVIT2021_OR_GREATER" }
    if ($Year -ge 2024) { $defines += "REVIT2024_OR_GREATER" }

    if ($Year -ge 2025) {
        # Revit 2025 / 2026+ runs on .NET 8.0-windows
        $dotnet = (Get-Command dotnet -ErrorAction SilentlyContinue).Source
        if ([string]::IsNullOrWhiteSpace($dotnet)) {
            throw "Revit $Year requires .NET 8 SDK (`dotnet` CLI) to compile net8.0-windows add-ins."
        }
        $tmpProjDir = Join-Path ([System.IO.Path]::GetTempPath()) ("ai_eng_revit_build_" + $Year + "_" + [Guid]::NewGuid().ToString("N"))
        New-Item -ItemType Directory -Path $tmpProjDir -Force | Out-Null
        try {
            $compileItems = ($sourceFiles | ForEach-Object { "    <Compile Include=`"$([System.Security.SecurityElement]::Escape($_))`" />" }) -join "`n"
            $defineStr = ($defines + @("NET8_0_OR_GREATER")) -join ";"
            $escOut = [System.Security.SecurityElement]::Escape($resolvedOut + "\")
            $escApi = [System.Security.SecurityElement]::Escape($revitApi)
            $escApiUi = [System.Security.SecurityElement]::Escape($revitApiUi)
            $csproj = @"
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net8.0-windows</TargetFramework>
    <UseWPF>true</UseWPF>
    <PlatformTarget>x64</PlatformTarget>
    <AssemblyName>AIEngineer.Revit.Direct</AssemblyName>
    <EnableDefaultCompileItems>false</EnableDefaultCompileItems>
    <DefineConstants>$defineStr</DefineConstants>
    <OutputPath>$escOut</OutputPath>
    <AppendTargetFrameworkToOutputPath>false</AppendTargetFrameworkToOutputPath>
  </PropertyGroup>
  <ItemGroup>
    <Reference Include="RevitAPI"><HintPath>$escApi</HintPath><Private>false</Private></Reference>
    <Reference Include="RevitAPIUI"><HintPath>$escApiUi</HintPath><Private>false</Private></Reference>
  </ItemGroup>
  <ItemGroup>
$compileItems
  </ItemGroup>
</Project>
"@
            $projFile = Join-Path $tmpProjDir "AIEngineer.Revit.Direct.csproj"
            [System.IO.File]::WriteAllText($projFile, $csproj, (New-Object System.Text.UTF8Encoding($false)))
            & $dotnet build $projFile -c Release --nologo
            if ($LASTEXITCODE -ne 0) { throw "dotnet build failed for Revit $Year." }
        } finally {
            Remove-Item -LiteralPath $tmpProjDir -Recurse -Force -ErrorAction SilentlyContinue
        }
    } else {
        # Revit 2015 - 2024 runs on .NET Framework 4.x (compiled cleanly via Windows csc.exe)
        $compiler = Join-Path $env:WINDIR "Microsoft.NET\Framework64\v4.0.30319\csc.exe"
        $fwOrder = if ($Year -ge 2021) { @('v4.8', 'v4.7.2', 'v4.7', 'v4.6.2') } else { @('v4.7.2', 'v4.8', 'v4.7', 'v4.6.2', 'v4.5.2') }
        $fwRef = $null
        foreach ($cand in $fwOrder) {
            $p = Join-Path ${env:ProgramFiles(x86)} "Reference Assemblies\Microsoft\Framework\.NETFramework\$cand"
            if (Test-Path -LiteralPath (Join-Path $p "PresentationFramework.dll") -PathType Leaf) { $fwRef = $p; break }
        }
        if ($null -eq $fwRef) {
            $wpfRuntime = Join-Path $env:WINDIR "Microsoft.NET\Framework64\v4.0.30319\WPF"
            if (Test-Path -LiteralPath (Join-Path $wpfRuntime "PresentationFramework.dll") -PathType Leaf) { $fwRef = $wpfRuntime }
        }
        if ($null -eq $fwRef) { throw "Required .NET Framework WPF assemblies missing for Revit $Year." }

        $defineArg = if ($defines.Count -gt 0) { "/define:" + ($defines -join ";") } else { "/define:REVIT_LEGACY" }
        $args = @(
            "/nologo", "/utf8output", "/codepage:65001", "/target:library", "/platform:x64", $defineArg, "/out:$assembly",
            "/reference:$revitApi", "/reference:$revitApiUi",
            "/reference:System.Net.Http.dll", "/reference:System.IO.Compression.dll", "/reference:System.IO.Compression.FileSystem.dll",
            "/reference:System.Xaml.dll",
            "/reference:$(Join-Path $fwRef 'PresentationCore.dll')",
            "/reference:$(Join-Path $fwRef 'PresentationFramework.dll')",
            "/reference:$(Join-Path $fwRef 'WindowsBase.dll')"
        ) + $sourceFiles

        & $compiler @args
        if ($LASTEXITCODE -ne 0) { throw "csc.exe compilation failed for Revit $Year (exit $LASTEXITCODE)." }
    }

    $installedAddin = ""
    if ($DoInstall) {
        $addinDir = Join-Path $env:APPDATA "Autodesk\Revit\Addins\$Year"
        New-Item -ItemType Directory -Path $addinDir -Force | Out-Null
        $template = Get-Content -LiteralPath (Join-Path $pluginRoot "AIEngineer.Revit.Direct.addin.template") -Raw
        $escaped = [System.Security.SecurityElement]::Escape($assembly)
        $installedAddin = Join-Path $addinDir "AIEngineer.Revit.Direct.addin"
        [System.IO.File]::WriteAllText($installedAddin, $template.Replace("{{ASSEMBLY_PATH}}", $escaped), (New-Object System.Text.UTF8Encoding($false)))
    }

    [PSCustomObject]@{
        revit_year = $Year
        runtime = if ($Year -ge 2025) { "net8.0-windows" } else { "net4x" }
        assembly = $assembly
        installed_addin = $installedAddin
    }
}

$targets = @()
if ($AllInstalled) {
    $targets = Find-InstalledRevits
    if ($targets.Count -eq 0) { throw "No Revit installations found in C:\Program Files\Autodesk." }
} elseif (-not [string]::IsNullOrWhiteSpace($RevitRoot)) {
    $y = if ($RevitYear -gt 0) { $RevitYear } elseif ($RevitRoot -match '(\d{4})') { [int]$Matches[1] } else { 2024 }
    $targets = @([PSCustomObject]@{ Year = $y; Path = $RevitRoot })
} elseif ($RevitYear -gt 0) {
    $targets = @([PSCustomObject]@{ Year = $RevitYear; Path = "C:\Program Files\Autodesk\Revit $RevitYear" })
} else {
    $detected = Find-InstalledRevits | Select-Object -First 1
    if ($null -eq $detected) { throw "No Revit installation auto-detected. Pass -RevitYear or -RevitRoot." }
    $targets = @($detected)
}

$built = @()
foreach ($t in $targets) {
    $out = if ($targets.Count -eq 1) { $OutputDirectory } else { "" }
    $built += Build-ForRevitVersion -Year $t.Year -RootPath $t.Path -OutDir $out -DoInstall $Install.IsPresent
}
$built | ConvertTo-Json -Depth 3
