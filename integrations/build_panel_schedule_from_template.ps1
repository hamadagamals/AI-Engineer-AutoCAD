param(
    [Parameter(Mandatory = $true)]
    [string]$ProjectRoot,
    [Parameter(Mandatory = $true)]
    [string]$RequestPath,
    [Parameter(Mandatory = $true)]
    [string]$OutputPath,
    [Parameter(Mandatory = $true)]
    [string]$ReportPath
)

$ErrorActionPreference = 'Stop'

function Get-FullPath {
    param([string]$Path, [string]$BasePath)
    if ([IO.Path]::IsPathRooted($Path)) {
        return [IO.Path]::GetFullPath($Path)
    }
    return [IO.Path]::GetFullPath((Join-Path $BasePath $Path))
}

function Assert-OutputOutsideTemplate {
    param([string]$TemplatePath, [string]$OutputPath)
    if ([string]::Equals($TemplatePath, $OutputPath, [StringComparison]::OrdinalIgnoreCase)) {
        throw 'OutputPath must not overwrite the registered template.'
    }
    if (Test-Path -LiteralPath $OutputPath) {
        throw "OutputPath already exists and will not be overwritten: $OutputPath"
    }
}

function Test-SheetName {
    param([string]$Name, [int]$Limit)
    if ([string]::IsNullOrWhiteSpace($Name)) { throw 'Worksheet names cannot be blank.' }
    if ($Name.Length -gt $Limit) { throw "Worksheet name exceeds $Limit characters: $Name" }
    if ($Name -match '[\\/\*\?\:\[\]]') { throw "Worksheet name contains an invalid character: $Name" }
    if ($Name.StartsWith("'") -or $Name.EndsWith("'")) { throw "Worksheet name cannot start or end with an apostrophe: $Name" }
}

function Get-FormulaCount {
    param($Sheet)
    try {
        return [int]$Sheet.UsedRange.SpecialCells(-4123).Count
    }
    catch {
        return 0
    }
}

function Get-FormulaErrorCount {
    param($Sheet)
    try {
        return [int]$Sheet.UsedRange.SpecialCells(-4123, 16).Count
    }
    catch {
        return 0
    }
}

$root = [IO.Path]::GetFullPath($ProjectRoot)
$requestFullPath = Get-FullPath $RequestPath $root
$outputFullPath = Get-FullPath $OutputPath $root
$reportFullPath = Get-FullPath $ReportPath $root
$manifestPath = Join-Path $root 'data\templates\panel-schedules\panels-sch-8-station.template.json'
$manifest = Get-Content -LiteralPath $manifestPath -Raw -Encoding UTF8 | ConvertFrom-Json
$templatePath = Join-Path ([IO.Path]::GetDirectoryName($manifestPath)) ([string]$manifest.workbook_file)
$request = Get-Content -LiteralPath $requestFullPath -Raw -Encoding UTF8 | ConvertFrom-Json

if ([string]$request.template_id -ne [string]$manifest.template_id) {
    throw 'Generation request template_id does not match the registered template.'
}
$templateHash = (Get-FileHash -Algorithm SHA256 -LiteralPath $templatePath).Hash.ToLowerInvariant()
if ($templateHash -ne ([string]$manifest.workbook_sha256).ToLowerInvariant()) {
    throw 'Registered template SHA-256 does not match its manifest.'
}
if (-not $request.panels -or $request.panels.Count -lt 1) {
    throw 'Generation request must contain at least one panel.'
}
Assert-OutputOutsideTemplate $templatePath $outputFullPath

$registeredSheets = @{}
foreach ($profile in $manifest.sheets) { $registeredSheets[[string]$profile.name] = $profile }
$seenNames = @{}
foreach ($panel in $request.panels) {
    $sheetName = [string]$panel.output_sheet_name
    Test-SheetName $sheetName ([int]$manifest.sheet_name_limit)
    $folded = $sheetName.ToUpperInvariant()
    if ($seenNames.ContainsKey($folded)) { throw "Duplicate output worksheet name: $sheetName" }
    $seenNames[$folded] = $true
    if (-not $registeredSheets.ContainsKey([string]$panel.exemplar_sheet)) {
        throw "Unknown exemplar worksheet: $($panel.exemplar_sheet)"
    }
}

$null = New-Item -ItemType Directory -Force -Path ([IO.Path]::GetDirectoryName($outputFullPath))
$null = New-Item -ItemType Directory -Force -Path ([IO.Path]::GetDirectoryName($reportFullPath))
[IO.File]::Copy($templatePath, $outputFullPath, $false)

$excel = $null
$workbook = $null
$sourceHashAfter = $null
try {
    $excel = New-Object -ComObject Excel.Application
    $excel.Visible = $false
    $excel.DisplayAlerts = $false
    $excel.ScreenUpdating = $false
    $excel.EnableEvents = $false
    $excel.AskToUpdateLinks = $false

    $workbook = $excel.Workbooks.Open($outputFullPath, 0, $false)
    $originalSheetNames = @()
    foreach ($sheet in $workbook.Worksheets) { $originalSheetNames += [string]$sheet.Name }

    $temporarySheets = @()
    $index = 1
    foreach ($panel in $request.panels) {
        $sourceSheet = $workbook.Worksheets.Item([string]$panel.exemplar_sheet)
        $sourceSheet.Copy([Type]::Missing, $workbook.Worksheets.Item($workbook.Worksheets.Count))
        $copy = $workbook.ActiveSheet
        $temporaryName = '__AIENG_' + $index.ToString('D3')
        $copy.Name = $temporaryName
        $temporarySheets += [pscustomobject]@{ TemporaryName = $temporaryName; Panel = $panel }
        $index++
    }

    foreach ($name in $originalSheetNames) { $workbook.Worksheets.Item($name).Delete() }

    $sheetResults = @()
    foreach ($item in $temporarySheets) {
        $panel = $item.Panel
        $sheet = $workbook.Worksheets.Item([string]$item.TemporaryName)
        $sheet.Name = [string]$panel.output_sheet_name
        $panelNameCell = [string]$manifest.panel_name_cell
        if ($sheet.Range($panelNameCell).HasFormula) {
            throw "Panel name cell unexpectedly contains a formula: $($sheet.Name)!$panelNameCell"
        }
        $sheet.Range($panelNameCell).Value2 = [string]$panel.panel_id

        if ($null -ne $panel.values) {
            foreach ($property in $panel.values.PSObject.Properties) {
                $cell = $sheet.Range([string]$property.Name)
                if ($cell.HasFormula) { throw "Request cannot overwrite formula cell: $($sheet.Name)!$($property.Name)" }
                $cell.Value2 = $property.Value
            }
        }
        if ($null -ne $panel.estimated_cells) {
            foreach ($address in $panel.estimated_cells) {
                $cell = $sheet.Range([string]$address)
                if ($cell.HasFormula) { throw "Estimated styling cannot be applied to formula cell: $($sheet.Name)!$address" }
                $cell.Interior.Color = 0x0000FF
                $cell.Font.Color = 0xFFFFFF
            }
        }
        $sheetResults += [pscustomobject]@{
            name = [string]$sheet.Name
            exemplar = [string]$panel.exemplar_sheet
            used_range = [string]$sheet.UsedRange.Address($false, $false)
            formula_count = Get-FormulaCount $sheet
        }
    }

    $excel.CalculateFullRebuild()
    $formulaErrorCount = 0
    foreach ($sheet in $workbook.Worksheets) { $formulaErrorCount += Get-FormulaErrorCount $sheet }
    if ($formulaErrorCount -ne 0) { throw "Generated workbook contains $formulaErrorCount formula error cell(s)." }

    $workbook.Save()
    $workbook.Close($true)
    $workbook = $null
    $excel.Quit()
    $sourceHashAfter = (Get-FileHash -Algorithm SHA256 -LiteralPath $templatePath).Hash.ToLowerInvariant()
    if ($sourceHashAfter -ne $templateHash) { throw 'Registered template changed during generation.' }

    $report = [ordered]@{
        schema_version = '1.0'
        complete = $true
        template_id = [string]$manifest.template_id
        template_sha256 = $templateHash
        template_unchanged = $true
        request_path = $requestFullPath
        output_path = $outputFullPath
        output_sha256 = (Get-FileHash -Algorithm SHA256 -LiteralPath $outputFullPath).Hash.ToLowerInvariant()
        sheet_count = [int]$sheetResults.Count
        formula_error_count = 0
        estimated_cell_fill = '#FF0000'
        formula_totals_recolored = $false
        sheets = $sheetResults
    }
    $reportJson = $report | ConvertTo-Json -Depth 8
    $reportTemporaryPath = $reportFullPath + '.tmp'
    [IO.File]::WriteAllText($reportTemporaryPath, $reportJson, [Text.UTF8Encoding]::new($false))
    Move-Item -LiteralPath $reportTemporaryPath -Destination $reportFullPath -Force
    $report | ConvertTo-Json -Depth 8 -Compress
}
catch {
    if (Test-Path -LiteralPath $outputFullPath) {
        try { Remove-Item -LiteralPath $outputFullPath -Force } catch {}
    }
    throw
}
finally {
    if ($null -ne $workbook) {
        try { $workbook.Close($false) } catch {}
        [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($workbook)
    }
    if ($null -ne $excel) {
        try { $excel.Quit() } catch {}
        [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($excel)
    }
    [GC]::Collect()
    [GC]::WaitForPendingFinalizers()
}
