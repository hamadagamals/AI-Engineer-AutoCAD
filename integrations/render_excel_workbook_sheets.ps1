param(
    [Parameter(Mandatory = $true)]
    [string]$WorkbookPath,
    [Parameter(Mandatory = $true)]
    [string]$OutputDirectory
)

$ErrorActionPreference = 'Stop'
$workbookFullPath = [IO.Path]::GetFullPath($WorkbookPath)
$outputFullPath = [IO.Path]::GetFullPath($OutputDirectory)
$null = New-Item -ItemType Directory -Force -Path $outputFullPath
$excel = $null
$workbook = $null
try {
    $excel = New-Object -ComObject Excel.Application
    $excel.Visible = $false
    $excel.DisplayAlerts = $false
    $excel.ScreenUpdating = $true
    $workbook = $excel.Workbooks.Open($workbookFullPath, 0, $false)
    $rendered = @()
    $index = 1
    foreach ($sheet in $workbook.Worksheets) {
        $sheet.Activate()
        $range = $sheet.UsedRange
        $range.CopyPicture(1, 2) | Out-Null
        Start-Sleep -Milliseconds 150
        $width = [Math]::Max(600.0, [Math]::Min(2400.0, [double]$range.Width))
        $height = [Math]::Max(300.0, [Math]::Min(2400.0, [double]$range.Height))
        $chartObject = $sheet.ChartObjects().Add(0.0, 0.0, $width, $height)
        $chartObject.Chart.Paste() | Out-Null
        Start-Sleep -Milliseconds 100
        $safeName = ([string]$sheet.Name -replace '[^A-Za-z0-9_-]', '_')
        $path = Join-Path $outputFullPath ('{0:D2}-{1}.png' -f $index, $safeName)
        $exported = $chartObject.Chart.Export($path, 'PNG')
        if (-not $exported) {
            throw "Excel could not export sheet preview: $($sheet.Name)"
        }
        $chartObject.Delete()
        $rendered += $path
        $index++
    }
    $workbook.Close($false)
    $workbook = $null
    $excel.Quit()
    $rendered | ConvertTo-Json
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
