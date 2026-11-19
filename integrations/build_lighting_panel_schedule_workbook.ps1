param(
    [Parameter(Mandatory = $true)]
    [string]$EvidencePath,
    [Parameter(Mandatory = $true)]
    [string]$OutputPath,
    [Parameter(Mandatory = $true)]
    [string]$PreviewPdfPath
)

$ErrorActionPreference = 'Stop'

function Set-CellValue {
    param($Sheet, [int]$Row, [int]$Column, $Value)
    if ($null -ne $Value) {
        if ($Value -is [byte] -or $Value -is [int16] -or $Value -is [int32] -or
            $Value -is [int64] -or $Value -is [single] -or $Value -is [double] -or
            $Value -is [decimal]) {
            $Sheet.Cells.Item($Row, $Column).Value2 = [double]$Value
        } elseif ($Value -is [bool]) {
            $Sheet.Cells.Item($Row, $Column).Value2 = [bool]$Value
        } else {
            $Sheet.Cells.Item($Row, $Column).Value2 = [string]$Value
        }
    }
}

function Set-HeaderStyle {
    param($Range)
    $Range.Font.Bold = $true
    $Range.Font.Color = 0xFFFFFF
    $Range.Interior.Color = 0x73613A
    $Range.HorizontalAlignment = -4108
    $Range.VerticalAlignment = -4108
    $Range.WrapText = $true
}

function Set-TitleStyle {
    param($Range)
    $Range.Font.Bold = $true
    $Range.Font.Size = 16
    $Range.Font.Color = 0xFFFFFF
    $Range.Interior.Color = 0x472211
    $Range.HorizontalAlignment = -4108
    $Range.VerticalAlignment = -4108
}

function Finish-Sheet {
    param($Sheet, [string]$PrintArea)
    $Sheet.Activate()
    $Sheet.Application.ActiveWindow.DisplayGridlines = $false
    $Sheet.PageSetup.Orientation = 2
    $Sheet.PageSetup.Zoom = $false
    $Sheet.PageSetup.FitToPagesWide = 1
    $Sheet.PageSetup.FitToPagesTall = $false
    $Sheet.PageSetup.CenterHorizontally = $true
    $Sheet.PageSetup.LeftMargin = $Sheet.Application.InchesToPoints(0.25)
    $Sheet.PageSetup.RightMargin = $Sheet.Application.InchesToPoints(0.25)
    $Sheet.PageSetup.TopMargin = $Sheet.Application.InchesToPoints(0.35)
    $Sheet.PageSetup.BottomMargin = $Sheet.Application.InchesToPoints(0.35)
    $Sheet.PageSetup.PrintArea = $PrintArea
}

$evidenceFullPath = [IO.Path]::GetFullPath($EvidencePath)
$outputFullPath = [IO.Path]::GetFullPath($OutputPath)
$previewFullPath = [IO.Path]::GetFullPath($PreviewPdfPath)
$null = New-Item -ItemType Directory -Force -Path ([IO.Path]::GetDirectoryName($outputFullPath))
$null = New-Item -ItemType Directory -Force -Path ([IO.Path]::GetDirectoryName($previewFullPath))
$evidence = Get-Content -LiteralPath $evidenceFullPath -Raw -Encoding UTF8 | ConvertFrom-Json

$excel = $null
$workbook = $null
try {
    $excel = New-Object -ComObject Excel.Application
    $excel.Visible = $false
    $excel.DisplayAlerts = $false
    $excel.ScreenUpdating = $false
    $excel.EnableEvents = $false

    $workbook = $excel.Workbooks.Add()
    while ($workbook.Worksheets.Count -gt 1) {
        $workbook.Worksheets.Item($workbook.Worksheets.Count).Delete()
    }
    $summary = $workbook.Worksheets.Item(1)
    $summary.Name = 'Summary'

    $summary.Range('A1:H1').Merge()
    $summary.Range('A1').Value2 = 'Lighting Panel Schedules - Draft / Review Only'
    Set-TitleStyle $summary.Range('A1:H1')
    $summary.Rows.Item(1).RowHeight = 28
    $summary.Range('A3:B8').Font.Name = 'Arial'
    $summary.Range('A3:A8').Font.Bold = $true
    $summary.Range('A3:A8').Interior.Color = 0xE9E2D0
    $summary.Range('A3:B8').Borders.Item(9).LineStyle = 1
    $summary.Cells.Item(3, 1).Value2 = 'Source drawing SHA-256'
    $summary.Cells.Item(3, 2).Value2 = $evidence.source.source_drawing_sha256
    $summary.Cells.Item(4, 1).Value2 = 'Drawing index ID'
    $summary.Cells.Item(4, 2).Value2 = $evidence.source.drawing_index_id
    $summary.Cells.Item(5, 1).Value2 = 'Indexed entities'
    $summary.Cells.Item(5, 2).Value2 = [double]$evidence.source.entity_count
    $summary.Cells.Item(6, 1).Value2 = 'System assumption'
    $summary.Cells.Item(6, 2).Value2 = '400/230 V, 3 Phase, 4 Wire'
    $summary.Cells.Item(7, 1).Value2 = 'Circuit voltage'
    $summary.Cells.Item(7, 2).Value2 = 230.0
    $summary.Cells.Item(8, 1).Value2 = 'Evidence result SHA-256'
    $summary.Cells.Item(8, 2).Value2 = $evidence.result_sha256
    $summary.Range('B3:B4').NumberFormat = '@'
    $summary.Range('B8').NumberFormat = '@'

    $summaryHeaders = @('Panel', 'Schedule rows', 'Observed circuits', 'Blank rows', 'Connected W', 'Connected VA', 'Demand VA', 'Review rows')
    for ($column = 1; $column -le $summaryHeaders.Count; $column++) {
        $summary.Cells.Item(11, $column).Value2 = $summaryHeaders[$column - 1]
    }
    Set-HeaderStyle $summary.Range('A11:H11')

    $panelIndex = 0
    foreach ($panel in $evidence.schedule_panels) {
        $sheet = $workbook.Worksheets.Add([Type]::Missing, $workbook.Worksheets.Item($workbook.Worksheets.Count))
        $sheet.Name = [string]$panel.panel
        $sheet.Range('A1:N1').Merge()
        $sheet.Range('A1').Value2 = "Panel Schedule - $($panel.panel)"
        Set-TitleStyle $sheet.Range('A1:N1')
        $sheet.Rows.Item(1).RowHeight = 28

        $sheet.Cells.Item(2, 1).Value2 = 'System'
        $sheet.Cells.Item(2, 2).Value2 = [string]$panel.system
        $sheet.Cells.Item(3, 1).Value2 = 'Circuit voltage (V L-N)'
        $sheet.Cells.Item(3, 2).Value2 = 230.0
        $sheet.Cells.Item(4, 1).Value2 = 'Frequency (Hz)'
        $sheet.Cells.Item(5, 1).Value2 = 'Rated panel ways'
        $sheet.Cells.Item(2, 4).Value2 = 'Row basis'
        $sheet.Cells.Item(2, 5).Value2 = [string]$panel.schedule_row_basis
        $sheet.Range('A2:A5').Font.Bold = $true
        $sheet.Range('A2:A5').Interior.Color = 0xE9E2D0
        $sheet.Range('D2').Font.Bold = $true
        $sheet.Range('D2').Interior.Color = 0xE9E2D0
        $sheet.Range('E2:N3').Merge()
        $sheet.Range('E2:N3').WrapText = $true
        $sheet.Range('E2:N3').VerticalAlignment = -4108

        $headers = @('Way', 'Description', 'Phase', 'Load qty', 'Connected W', 'Connected VA', 'PF', 'Current A', 'Demand factor', 'Demand VA', 'Status', 'Membership', 'Unresolved loads', 'Notes')
        for ($column = 1; $column -le $headers.Count; $column++) {
            $sheet.Cells.Item(7, $column).Value2 = $headers[$column - 1]
        }
        Set-HeaderStyle $sheet.Range('A7:N7')
        $sheet.Rows.Item(7).RowHeight = 32

        $row = 8
        foreach ($way in $panel.ways) {
            $sheet.Cells.Item($row, 1).Value2 = [double]$way.way
            if ($way.observed_in_lighting_drawing) {
                Set-CellValue $sheet $row 2 $way.description
                Set-CellValue $sheet $row 4 (($evidence.circuits | Where-Object { $_.panel -eq $panel.panel -and $_.circuit -eq ([string]$way.way) }).classified_load_count)
                Set-CellValue $sheet $row 5 $way.connected_w
                Set-CellValue $sheet $row 6 $way.connected_va
                if ($null -ne $way.connected_w -and $null -ne $way.connected_va) {
                    $sheet.Cells.Item($row, 7).Formula = "=IF(OR(E$row="""",F$row=""""),"""",E$row/F$row)"
                    $sheet.Cells.Item($row, 8).Formula = "=IF(F$row="""","""",F$row/`$B`$3)"
                    $sheet.Cells.Item($row, 9).Value2 = 1.0
                    $sheet.Cells.Item($row, 10).Formula = "=IF(F$row="""","""",F$row*I$row)"
                }
                Set-CellValue $sheet $row 11 $way.status
                Set-CellValue $sheet $row 12 $way.membership_status
                Set-CellValue $sheet $row 13 $way.unresolved_load_count
                $note = $null
                if ($null -ne $way.known_partial_w -and $null -eq $way.connected_w) {
                    $note = "Known partial only: $($way.known_partial_w) W / $($way.known_partial_va) VA; complete total withheld."
                }
                Set-CellValue $sheet $row 14 $note
                if ($way.status -eq 'REVIEW-CONFLICT') {
                    $sheet.Range("A$row:N$row").Interior.Color = 0xD9C2F4
                } elseif ($way.status -eq 'REVIEW-INCOMPLETE-LOADS') {
                    $sheet.Range("A$row:N$row").Interior.Color = 0xB4E6FF
                } else {
                    $sheet.Range("A$row:N$row").Interior.Color = 0xCCF2FF
                }
            }
            $row++
        }
        $lastRow = $row - 1
        $sheet.Range("A7:N$lastRow").Font.Name = 'Arial'
        $sheet.Range("A7:N$lastRow").Font.Size = 9
        $sheet.Range("A7:N$lastRow").Borders.Item(9).LineStyle = 1
        $sheet.Range("A8:A$lastRow").NumberFormat = '0'
        $sheet.Range("D8:D$lastRow").NumberFormat = '0'
        $sheet.Range("E8:F$lastRow").NumberFormat = '0.0'
        $sheet.Range("G8:G$lastRow").NumberFormat = '0.000'
        $sheet.Range("H8:H$lastRow").NumberFormat = '0.000'
        $sheet.Range("I8:I$lastRow").NumberFormat = '0.00'
        $sheet.Range("J8:J$lastRow").NumberFormat = '0.0'
        $sheet.Range("M8:M$lastRow").NumberFormat = '0'
        $sheet.Range("B8:N$lastRow").WrapText = $true
        $sheet.Range("A8:A$lastRow").HorizontalAlignment = -4108
        $sheet.Range("C8:M$lastRow").HorizontalAlignment = -4108
        $sheet.Columns.Item('A').ColumnWidth = 7
        $sheet.Columns.Item('B').ColumnWidth = 35
        $sheet.Columns.Item('C').ColumnWidth = 9
        $sheet.Columns.Item('D').ColumnWidth = 10
        $sheet.Columns.Item('E').ColumnWidth = 12
        $sheet.Columns.Item('F').ColumnWidth = 12
        $sheet.Columns.Item('G').ColumnWidth = 9
        $sheet.Columns.Item('H').ColumnWidth = 11
        $sheet.Columns.Item('I').ColumnWidth = 12
        $sheet.Columns.Item('J').ColumnWidth = 12
        $sheet.Columns.Item('K').ColumnWidth = 26
        $sheet.Columns.Item('L').ColumnWidth = 38
        $sheet.Columns.Item('M').ColumnWidth = 12
        $sheet.Columns.Item('N').ColumnWidth = 35
        $sheet.Range("A8:N$lastRow").EntireRow.AutoFit()
        $sheet.Activate()
        $excel.ActiveWindow.SplitRow = 7
        $excel.ActiveWindow.FreezePanes = $true
        $sheet.Range("A7:N$lastRow").AutoFilter() | Out-Null
        Finish-Sheet $sheet "`$A`$1:`$N`$$lastRow"

        $summaryRow = 12 + $panelIndex
        $summary.Cells.Item($summaryRow, 1).Value2 = [string]$panel.panel
        $summary.Cells.Item($summaryRow, 2).Value2 = [double]$panel.ways.Count
        $summary.Cells.Item($summaryRow, 3).Formula = "=COUNTIF('$($panel.panel)'!K8:K$lastRow,""<>"")"
        $summary.Cells.Item($summaryRow, 4).Formula = "=B$summaryRow-C$summaryRow"
        $summary.Cells.Item($summaryRow, 5).Formula = "=SUM('$($panel.panel)'!E8:E$lastRow)"
        $summary.Cells.Item($summaryRow, 6).Formula = "=SUM('$($panel.panel)'!F8:F$lastRow)"
        $summary.Cells.Item($summaryRow, 7).Formula = "=SUM('$($panel.panel)'!J8:J$lastRow)"
        $summary.Cells.Item($summaryRow, 8).Formula = "=COUNTIF('$($panel.panel)'!K8:K$lastRow,""REVIEW*"")"
        $panelIndex++
    }

    $summaryLastRow = 11 + $panelIndex
    $summary.Range("A11:H$summaryLastRow").Borders.Item(9).LineStyle = 1
    $summary.Range("B12:D$summaryLastRow").NumberFormat = '0'
    $summary.Range("E12:G$summaryLastRow").NumberFormat = '0.0'
    $summary.Range("H12:H$summaryLastRow").NumberFormat = '0'
    $summary.Range("A12:H$summaryLastRow").Font.Name = 'Arial'
    $summary.Columns.Item('A').ColumnWidth = 15
    $summary.Columns.Item('B').ColumnWidth = 16
    $summary.Columns.Item('C').ColumnWidth = 18
    $summary.Columns.Item('D').ColumnWidth = 14
    $summary.Columns.Item('E').ColumnWidth = 15
    $summary.Columns.Item('F').ColumnWidth = 15
    $summary.Columns.Item('G').ColumnWidth = 15
    $summary.Columns.Item('H').ColumnWidth = 14
    $summary.Columns.Item('B').WrapText = $true
    Finish-Sheet $summary "`$A`$1:`$H`$$summaryLastRow"

    $evidenceSheet = $workbook.Worksheets.Add([Type]::Missing, $workbook.Worksheets.Item($workbook.Worksheets.Count))
    $evidenceSheet.Name = 'Circuit Evidence'
    $evidenceSheet.Range('A1:M1').Merge()
    $evidenceSheet.Range('A1').Value2 = 'Observed Circuit Candidate Evidence'
    Set-TitleStyle $evidenceSheet.Range('A1:M1')
    $evidenceHeaders = @('Panel', 'Circuit', 'Occurrences', 'Equivalent duplicate', 'Conflict', 'Loads', 'Unresolved', 'Connected W', 'Connected VA', 'Current A', 'Known partial W', 'Known partial VA', 'Membership status')
    for ($column = 1; $column -le $evidenceHeaders.Count; $column++) {
        $evidenceSheet.Cells.Item(3, $column).Value2 = $evidenceHeaders[$column - 1]
    }
    Set-HeaderStyle $evidenceSheet.Range('A3:M3')
    $row = 4
    foreach ($circuit in $evidence.circuits) {
        $values = @($circuit.panel, $circuit.circuit, $circuit.candidate_occurrence_count, $circuit.duplicate_equivalent, $circuit.occurrence_conflict, $circuit.classified_load_count, $circuit.unresolved_load_count, $circuit.provisional_connected_w, $circuit.provisional_connected_va, $circuit.provisional_current_a_at_230v, $circuit.known_partial_w, $circuit.known_partial_va, $circuit.membership_status)
        for ($column = 1; $column -le $values.Count; $column++) { Set-CellValue $evidenceSheet $row $column $values[$column - 1] }
        $row++
    }
    $evidenceLastRow = $row - 1
    $evidenceSheet.Range("A3:M$evidenceLastRow").Borders.Item(9).LineStyle = 1
    $evidenceSheet.Range("H4:L$evidenceLastRow").NumberFormat = '0.000'
    $evidenceSheet.Range("A3:M$evidenceLastRow").Font.Name = 'Arial'
    $evidenceSheet.Range("A3:M$evidenceLastRow").AutoFilter() | Out-Null
    $evidenceSheet.Columns.AutoFit() | Out-Null
    $evidenceSheet.Columns.Item('M').ColumnWidth = 46
    Finish-Sheet $evidenceSheet "`$A`$1:`$M`$$evidenceLastRow"

    $review = $workbook.Worksheets.Add([Type]::Missing, $workbook.Worksheets.Item($workbook.Worksheets.Count))
    $review.Name = 'Review Items'
    $review.Range('A1:G1').Merge()
    $review.Range('A1').Value2 = 'Items Requiring Engineer Review'
    Set-TitleStyle $review.Range('A1:G1')
    $reviewHeaders = @('Panel', 'Circuit/ref', 'Issue', 'Known partial W', 'Known partial VA', 'Unresolved loads', 'Action')
    for ($column = 1; $column -le $reviewHeaders.Count; $column++) { $review.Cells.Item(3, $column).Value2 = $reviewHeaders[$column - 1] }
    Set-HeaderStyle $review.Range('A3:G3')
    $row = 4
    foreach ($item in $evidence.review_items) {
        $review.Cells.Item($row, 1).Value2 = [string]$item.panel
        $review.Cells.Item($row, 2).Value2 = [string]$item.reference
        $review.Cells.Item($row, 3).Value2 = [string]$item.reason
        $review.Cells.Item($row, 7).Value2 = 'Confirm the valid numeric circuit identity.'
        $row++
    }
    foreach ($circuit in $evidence.circuits) {
        if ($circuit.occurrence_conflict -or ($null -ne $circuit.unresolved_load_count -and $circuit.unresolved_load_count -gt 0)) {
            $review.Cells.Item($row, 1).Value2 = [string]$circuit.panel
            $review.Cells.Item($row, 2).Value2 = [string]$circuit.circuit
            $review.Cells.Item($row, 3).Value2 = if ($circuit.occurrence_conflict) { 'Repeated drawing occurrences have different load signatures.' } else { 'One or more load types lack an approved complete W/PF basis.' }
            Set-CellValue $review $row 4 $circuit.known_partial_w
            Set-CellValue $review $row 5 $circuit.known_partial_va
            Set-CellValue $review $row 6 $circuit.unresolved_load_count
            $review.Cells.Item($row, 7).Value2 = if ($circuit.occurrence_conflict) { 'Review the two occurrences; do not sum automatically.' } else { 'Bind the unresolved block type to legend/manufacturer data.' }
            $row++
        }
    }
    $reviewLastRow = $row - 1
    $review.Range("A3:G$reviewLastRow").Borders.Item(9).LineStyle = 1
    $review.Range("A4:G$reviewLastRow").Interior.Color = 0xD9C2F4
    $review.Range("D4:F$reviewLastRow").NumberFormat = '0.000'
    $review.Range("A3:G$reviewLastRow").WrapText = $true
    $review.Range("A3:G$reviewLastRow").Font.Name = 'Arial'
    $review.Columns.Item('A').ColumnWidth = 14
    $review.Columns.Item('B').ColumnWidth = 13
    $review.Columns.Item('C').ColumnWidth = 50
    $review.Columns.Item('D').ColumnWidth = 16
    $review.Columns.Item('E').ColumnWidth = 16
    $review.Columns.Item('F').ColumnWidth = 16
    $review.Columns.Item('G').ColumnWidth = 45
    $review.Range("A4:G$reviewLastRow").EntireRow.AutoFit()
    Finish-Sheet $review "`$A`$1:`$G`$$reviewLastRow"

    $basis = $workbook.Worksheets.Add([Type]::Missing, $workbook.Worksheets.Item($workbook.Worksheets.Count))
    $basis.Name = 'Load Basis'
    $basis.Range('A1:G1').Merge()
    $basis.Range('A1').Value2 = 'Provisional Load and Power-Factor Basis'
    Set-TitleStyle $basis.Range('A1:G1')
    $basisHeaders = @('Match', 'Description', 'Unit W', 'PF', 'Source class', 'Confidence', 'Source / note')
    for ($column = 1; $column -le $basisHeaders.Count; $column++) { $basis.Cells.Item(3, $column).Value2 = $basisHeaders[$column - 1] }
    Set-HeaderStyle $basis.Range('A3:G3')
    $basisRows = @(
        @('REGTANGULAR SPOT_L01', 'Rectangular lighting fixture', 35, 0.90, 'ESTIMATED', 'MEDIUM', 'Drawing legend/code interpretation; review required'),
        @('SPOT Toilet / SPOT_L03', 'Toilet spot lighting fixture', 28, 0.90, 'ESTIMATED', 'MEDIUM', 'Functional legend match; review required'),
        @('PENDANT _L05', 'Pendant lighting fixture', 75, 0.90, 'ESTIMATED', 'LOW', 'Functional pendant estimate due drawing code conflict'),
        @('Propeller Fan NEW', 'Extract fan', 40, 0.80, 'ESTIMATED-EXTERNAL-BASIS', 'LOW', 'https://www.panasonic.com/ae/business/life-solutions/indoor-air-quality/wall-mount-ventilation-fans/metallic-series.specs.html'),
        @('2X28W', '2 x 28 W lighting fixture', 56, 0.90, 'DRAWING-NAME', 'HIGH', 'Wattage embedded in drawing block name'),
        @('LED Tapes', 'LED tape segment', $null, $null, 'UNRESOLVED-LENGTH', 'LOW', 'Length and W/m are not bound'),
        @('OUTLET SPOT / Downlight-SIMES', 'Other lighting types', $null, $null, 'UNRESOLVED', 'LOW', 'Exact legend/manufacturer binding required'),
        @('LED PF', 'Provisional LED driver PF', $null, 0.90, 'ESTIMATED-EXTERNAL-BASIS', 'MEDIUM', 'https://www.signify.com/api/assets/v1/file/Signify/content/CertaDrive_40W_350mA_115V_230V/Datasheet_CertaDrive_40W_350mA_115V_230V.pdf')
    )
    $row = 4
    foreach ($basisRow in $basisRows) {
        for ($column = 1; $column -le 7; $column++) { Set-CellValue $basis $row $column $basisRow[$column - 1] }
        $row++
    }
    $basisLastRow = $row - 1
    $basis.Range("A3:G$basisLastRow").Borders.Item(9).LineStyle = 1
    $basis.Range("C4:C$basisLastRow").NumberFormat = '0.0'
    $basis.Range("D4:D$basisLastRow").NumberFormat = '0.00'
    $basis.Range("A3:G$basisLastRow").WrapText = $true
    $basis.Range("A3:G$basisLastRow").Font.Name = 'Arial'
    $basis.Columns.Item('A').ColumnWidth = 28
    $basis.Columns.Item('B').ColumnWidth = 36
    $basis.Columns.Item('C').ColumnWidth = 12
    $basis.Columns.Item('D').ColumnWidth = 10
    $basis.Columns.Item('E').ColumnWidth = 28
    $basis.Columns.Item('F').ColumnWidth = 14
    $basis.Columns.Item('G').ColumnWidth = 75
    $basis.Range("A4:G$basisLastRow").EntireRow.AutoFit()
    Finish-Sheet $basis "`$A`$1:`$G`$$basisLastRow"

    $summary.Activate()
    $excel.ActiveWindow.SplitRow = 11
    $excel.ActiveWindow.FreezePanes = $true
    $excel.CalculateFullRebuild()
    $workbook.SaveAs($outputFullPath, 51)
    $workbook.ExportAsFixedFormat(0, $previewFullPath, 0, $true, $false)
    $workbook.Close($true)
    $workbook = $null
    $excel.Quit()
    [pscustomobject]@{
        output = $outputFullPath
        preview = $previewFullPath
        worksheets = 14
        panels = $evidence.schedule_panels.Count
        result_sha256 = $evidence.result_sha256
    } | ConvertTo-Json -Compress
}
catch {
    Write-Error ("Workbook build failed at {0}: {1}" -f $_.InvocationInfo.PositionMessage, $_.Exception.Message)
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
