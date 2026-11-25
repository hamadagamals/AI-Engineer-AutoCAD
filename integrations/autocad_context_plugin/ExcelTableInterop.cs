using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows.Forms;
using System.Xml;
using Autodesk.AutoCAD.ApplicationServices;

namespace AIEngineer.AutoCAD
{
    internal sealed class ExcelSheetData
    {
        public string Title = "";
        public string SheetName = "Sheet1";
        public List<string> AvailableSheets = new List<string>();
        public List<string> Headers = new List<string>();
        public List<List<string>> Rows = new List<List<string>>();
        public List<string> FooterRow = null;
    }

    internal static class ExcelTableInterop
    {
        private static readonly Encoding Utf8NoBom = new UTF8Encoding(false);
        private static readonly Encoding Utf8Bom = new UTF8Encoding(true);

        public static bool HasArabic(string text)
        {
            if (string.IsNullOrEmpty(text)) return false;
            for (int i = 0; i < text.Length; i++)
            {
                char c = text[i];
                if (c >= '\u0600' && c <= '\u06FF') return true;
                if (c >= '\u0750' && c <= '\u077F') return true;
                if (c >= '\u08A0' && c <= '\u08FF') return true;
                if (c >= '\uFB50' && c <= '\uFEFF') return true;
            }
            return false;
        }

        public static bool IsSpreadsheetArabic(ExcelSheetData data)
        {
            if (data == null) return false;
            if (HasArabic(data.Title) || HasArabic(data.SheetName)) return true;
            if (data.Headers != null)
            {
                for (int i = 0; i < data.Headers.Count; i++)
                    if (HasArabic(data.Headers[i])) return true;
            }
            if (data.Rows != null)
            {
                int checkRows = Math.Min(25, data.Rows.Count);
                for (int r = 0; r < checkRows; r++)
                {
                    List<string> row = data.Rows[r];
                    if (row == null) continue;
                    for (int c = 0; c < row.Count; c++)
                        if (HasArabic(row[c])) return true;
                }
            }
            return false;
        }

        public static string ResolveDefaultExportPath(Document doc, string tag, string extension)
        {
            string ext = string.IsNullOrWhiteSpace(extension) ? ".xlsx" : extension.Trim().ToLowerInvariant();
            if (!ext.StartsWith(".")) ext = "." + ext;

            string dir = null;
            string dwgBase = "Drawing";
            try
            {
                if (doc != null && !string.IsNullOrWhiteSpace(doc.Name))
                {
                    string bn = Path.GetFileNameWithoutExtension(doc.Name);
                    if (!string.IsNullOrWhiteSpace(bn)) dwgBase = SanitizeFilePart(bn);
                    string full = doc.Name;
                    if (Path.IsPathRooted(full) && File.Exists(full))
                    {
                        string d = Path.GetDirectoryName(full);
                        if (!string.IsNullOrEmpty(d) && Directory.Exists(d)) dir = d;
                    }
                }
            }
            catch { }

            if (string.IsNullOrEmpty(dir))
            {
                string desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
                if (!string.IsNullOrEmpty(desktop) && Directory.Exists(desktop))
                    dir = Path.Combine(desktop, "AI-Engineer-Exports");
                else
                    dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "AI-Engineer-Exports");
            }

            Directory.CreateDirectory(dir);
            string safeTag = string.IsNullOrWhiteSpace(tag) ? "table" : SanitizeFilePart(tag);
            string fileName = dwgBase + "_" + safeTag + "_" + DateTime.Now.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture) + ext;
            return Path.Combine(dir, fileName);
        }

        public static string BrowseSaveSpreadsheetPath(string defaultFileName, string initialDir)
        {
            string chosen = null;
            try
            {
                using (SaveFileDialog dlg = new SaveFileDialog())
                {
                    dlg.Title = "حفظ الجدول إلى ملف Excel (Save Table as Excel)";
                    dlg.Filter = "Excel Workbook (*.xlsx)|*.xlsx|CSV (UTF-8) (*.csv)|*.csv";
                    dlg.DefaultExt = "xlsx";
                    dlg.AddExtension = true;
                    if (!string.IsNullOrWhiteSpace(defaultFileName)) dlg.FileName = defaultFileName;
                    if (!string.IsNullOrWhiteSpace(initialDir) && Directory.Exists(initialDir)) dlg.InitialDirectory = initialDir;
                    if (dlg.ShowDialog() == DialogResult.OK) chosen = dlg.FileName;
                }
            }
            catch { }
            return chosen;
        }

        public static string BrowseOpenSpreadsheetPath(string initialDir)
        {
            string chosen = null;
            try
            {
                using (OpenFileDialog dlg = new OpenFileDialog())
                {
                    dlg.Title = "اختر ملف Excel لاستيراد الجدول إلى أوتوكاد (Select Excel/CSV File)";
                    dlg.Filter = "Excel & CSV Files (*.xlsx;*.xlsm;*.csv;*.tsv)|*.xlsx;*.xlsm;*.csv;*.tsv|Excel Workbook (*.xlsx;*.xlsm)|*.xlsx;*.xlsm|CSV Files (*.csv;*.tsv)|*.csv;*.tsv|All Files (*.*)|*.*";
                    dlg.CheckFileExists = true;
                    dlg.Multiselect = false;
                    if (!string.IsNullOrWhiteSpace(initialDir) && Directory.Exists(initialDir)) dlg.InitialDirectory = initialDir;
                    if (dlg.ShowDialog() == DialogResult.OK) chosen = dlg.FileName;
                }
            }
            catch { }
            return chosen;
        }

        internal static string ResolveWritableExportDirectory()
        {
            string[] bases = new string[]
            {
                Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
                Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                Path.GetTempPath()
            };
            for (int i = 0; i < bases.Length; i++)
            {
                string b = bases[i];
                if (string.IsNullOrWhiteSpace(b)) continue;
                try
                {
                    if (!Directory.Exists(b)) continue;
                    string candidate = Path.Combine(b, "AI-Engineer-Exports");
                    Directory.CreateDirectory(candidate);
                    return candidate;
                }
                catch { }
            }
            string fallback = Path.Combine(Path.GetTempPath(), "AI-Engineer-Exports");
            Directory.CreateDirectory(fallback);
            return fallback;
        }

        internal static bool IsProtectedSystemDirectoryPath(string fullPath)
        {
            if (string.IsNullOrWhiteSpace(fullPath)) return false;
            string[] blockedRoots = new string[]
            {
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                Environment.GetFolderPath(Environment.SpecialFolder.Windows)
            };
            for (int i = 0; i < blockedRoots.Length; i++)
            {
                string root = blockedRoots[i];
                if (string.IsNullOrWhiteSpace(root)) continue;
                string normRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
                if (fullPath.StartsWith(normRoot, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(fullPath.TrimEnd(Path.DirectorySeparatorChar), normRoot.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            return false;
        }

        internal static string ResolveSafeOutputPath(string targetPath, string[] allowedExtensions, string defaultExt, string fallbackStem)
        {
            string trimmed = (targetPath ?? "").Trim();
            string fallback = string.IsNullOrWhiteSpace(fallbackStem) ? "output" : fallbackStem;
            string safeDefaultExt = string.IsNullOrWhiteSpace(defaultExt) ? ".xlsx" : defaultExt;
            bool redirect = !Path.IsPathRooted(trimmed);
            string fullPath = redirect ? "" : Path.GetFullPath(trimmed);
            if (!redirect && IsProtectedSystemDirectoryPath(fullPath))
                redirect = true;

            if (redirect)
            {
                string safeName = SanitizeFilePart(Path.GetFileNameWithoutExtension(trimmed));
                if (string.IsNullOrWhiteSpace(safeName)) safeName = fallback;
                string rawExt = Path.GetExtension(trimmed).ToLowerInvariant();
                string chosenExt = safeDefaultExt;
                if (allowedExtensions != null)
                {
                    for (int i = 0; i < allowedExtensions.Length; i++)
                    {
                        if (string.Equals(rawExt, allowedExtensions[i], StringComparison.OrdinalIgnoreCase))
                        {
                            chosenExt = allowedExtensions[i];
                            break;
                        }
                    }
                }
                fullPath = Path.Combine(ResolveWritableExportDirectory(), safeName + chosenExt);
            }

            string ext = Path.GetExtension(fullPath).ToLowerInvariant();
            bool validExt = false;
            if (allowedExtensions != null)
            {
                for (int i = 0; i < allowedExtensions.Length; i++)
                {
                    if (string.Equals(ext, allowedExtensions[i], StringComparison.OrdinalIgnoreCase))
                    {
                        validExt = true;
                        break;
                    }
                }
            }
            if (!validExt) fullPath += safeDefaultExt;
            return fullPath;
        }

        public static string WriteSpreadsheet(string targetPath, ExcelSheetData data)
        {
            if (string.IsNullOrWhiteSpace(targetPath))
                throw new ArgumentException("Output Excel file path is required.");
            if (data == null)
                throw new ArgumentNullException("data");

            string fullPath = ResolveSafeOutputPath(targetPath, new string[] { ".xlsx", ".csv" }, ".xlsx", "table");

            string dir = Path.GetDirectoryName(fullPath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

            string ext = Path.GetExtension(fullPath).ToLowerInvariant();

            string finalPath = fullPath;
            string tmpPath = fullPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                if (ext == ".csv")
                    WriteCsvFile(tmpPath, data);
                else
                    WriteXlsxFile(tmpPath, data);

                try
                {
                    if (File.Exists(finalPath)) File.Delete(finalPath);
                    File.Move(tmpPath, finalPath);
                }
                catch (IOException)
                {
                    // File likely locked/open in Excel — write cleanly with timestamp suffix instead of failing
                    string stem = Path.GetFileNameWithoutExtension(fullPath);
                    finalPath = Path.Combine(
                        dir ?? ".",
                        stem + "_" + DateTime.Now.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture) + ext);
                    File.Move(tmpPath, finalPath);
                }
            }
            finally
            {
                try { if (File.Exists(tmpPath)) File.Delete(tmpPath); } catch { }
            }
            return finalPath;
        }

        public static string WriteWorkbook(string targetPath, List<ExcelSheetData> sheets)
        {
            if (string.IsNullOrWhiteSpace(targetPath))
                throw new ArgumentException("Output Excel file path is required.");
            if (sheets == null || sheets.Count == 0)
                throw new ArgumentException("At least one worksheet is required.");

            string fullPath = ResolveSafeOutputPath(targetPath, new string[] { ".xlsx" }, ".xlsx", "workbook");
            string dir = Path.GetDirectoryName(fullPath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

            string finalPath = fullPath;
            string tmpPath = fullPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                WriteXlsxWorkbook(tmpPath, sheets);
                try
                {
                    if (File.Exists(finalPath)) File.Delete(finalPath);
                    File.Move(tmpPath, finalPath);
                }
                catch (IOException)
                {
                    string stem = Path.GetFileNameWithoutExtension(fullPath);
                    finalPath = Path.Combine(
                        dir ?? ".",
                        stem + "_" + DateTime.Now.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture) + ".xlsx");
                    File.Move(tmpPath, finalPath);
                }
            }
            finally
            {
                try { if (File.Exists(tmpPath)) File.Delete(tmpPath); } catch { }
            }
            return finalPath;
        }

        public static bool TryOpenWithShell(string filePath)
        {
            if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath)) return false;
            try
            {
                ProcessStartInfo psi = new ProcessStartInfo(filePath);
                psi.UseShellExecute = true;
                Process.Start(psi);
                return true;
            }
            catch
            {
                return false;
            }
        }

        public static ExcelSheetData ReadSpreadsheet(string filePath, string requestedSheetName, int maxRows, int maxCols)
        {
            if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
                throw new FileNotFoundException("Spreadsheet file not found: " + (filePath ?? ""), filePath);

            string ext = Path.GetExtension(filePath).ToLowerInvariant();
            if (ext == ".csv" || ext == ".tsv" || ext == ".txt")
                return ReadCsvFile(filePath, maxRows, maxCols);
            if (ext == ".xlsx" || ext == ".xlsm")
                return ReadXlsxFile(filePath, requestedSheetName, maxRows, maxCols);

            throw new NotSupportedException("Unsupported spreadsheet format '" + ext + "'. Supported formats: .xlsx, .xlsm, .csv, .tsv.");
        }

        // ==================== OOXML (.xlsx) Writer ====================

        private static void WriteCsvFile(string path, ExcelSheetData data)
        {
            StringBuilder sb = new StringBuilder();
            if (!string.IsNullOrWhiteSpace(data.Title))
                sb.AppendLine(EscapeCsv(data.Title));
            if (data.Headers != null && data.Headers.Count > 0)
            {
                for (int c = 0; c < data.Headers.Count; c++)
                {
                    if (c > 0) sb.Append(',');
                    sb.Append(EscapeCsv(data.Headers[c] ?? ""));
                }
                sb.AppendLine();
            }
            if (data.Rows != null)
            {
                for (int r = 0; r < data.Rows.Count; r++)
                {
                    List<string> row = data.Rows[r];
                    if (row == null) continue;
                    for (int c = 0; c < row.Count; c++)
                    {
                        if (c > 0) sb.Append(',');
                        sb.Append(EscapeCsv(row[c] ?? ""));
                    }
                    sb.AppendLine();
                }
            }
            if (data.FooterRow != null && data.FooterRow.Count > 0)
            {
                for (int c = 0; c < data.FooterRow.Count; c++)
                {
                    if (c > 0) sb.Append(',');
                    sb.Append(EscapeCsv(data.FooterRow[c] ?? ""));
                }
                sb.AppendLine();
            }
            File.WriteAllText(path, sb.ToString(), Utf8Bom);
        }

        private static void WriteXlsxFile(string path, ExcelSheetData data)
        {
            int colCount = data.Headers != null ? data.Headers.Count : 0;
            if (data.Rows != null)
            {
                for (int r = 0; r < data.Rows.Count; r++)
                    if (data.Rows[r] != null && data.Rows[r].Count > colCount) colCount = data.Rows[r].Count;
            }
            if (data.FooterRow != null && data.FooterRow.Count > colCount) colCount = data.FooterRow.Count;
            if (colCount <= 0) colCount = 1;

            double[] colWidths = new double[colCount];
            for (int c = 0; c < colCount; c++) colWidths[c] = 12.0;

            Action<int, string> trackWidth = delegate(int colIdx, string val)
            {
                if (colIdx < 0 || colIdx >= colCount || string.IsNullOrEmpty(val)) return;
                int maxLine = 0;
                string[] lines = val.Split(new char[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
                for (int li = 0; li < lines.Length; li++)
                    if (lines[li].Length > maxLine) maxLine = lines[li].Length;
                double w = Math.Min(60.0, Math.Max(10.0, maxLine * 1.25 + 4.0));
                if (w > colWidths[colIdx]) colWidths[colIdx] = w;
            };

            if (data.Headers != null)
                for (int c = 0; c < data.Headers.Count; c++) trackWidth(c, data.Headers[c]);
            if (data.Rows != null)
                for (int r = 0; r < data.Rows.Count; r++)
                    if (data.Rows[r] != null)
                        for (int c = 0; c < data.Rows[r].Count; c++) trackWidth(c, data.Rows[r][c]);
            if (data.FooterRow != null)
                for (int c = 0; c < data.FooterRow.Count; c++) trackWidth(c, data.FooterRow[c]);

            string safeSheetName = SanitizeSheetName(string.IsNullOrWhiteSpace(data.SheetName) ? "AI-Engineer" : data.SheetName);
            bool hasTitle = !string.IsNullOrWhiteSpace(data.Title);
            bool hasHeaders = data.Headers != null && data.Headers.Count > 0;
            bool isRtl = IsSpreadsheetArabic(data);

            using (FileStream fs = new FileStream(path, FileMode.Create, FileAccess.ReadWrite, FileShare.None))
            using (ZipArchive zip = new ZipArchive(fs, ZipArchiveMode.Create))
            {
                WriteZipEntry(zip, "[Content_Types].xml",
                    "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
                    "<Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\">" +
                    "<Default Extension=\"rels\" ContentType=\"application/vnd.openxmlformats-package.relationships+xml\"/>" +
                    "<Default Extension=\"xml\" ContentType=\"application/xml\"/>" +
                    "<Override PartName=\"/xl/workbook.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml\"/>" +
                    "<Override PartName=\"/xl/worksheets/sheet1.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml\"/>" +
                    "<Override PartName=\"/xl/styles.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.styles+xml\"/>" +
                    "</Types>");

                WriteZipEntry(zip, "_rels/.rels",
                    "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
                    "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">" +
                    "<Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument\" Target=\"xl/workbook.xml\"/>" +
                    "</Relationships>");

                WriteZipEntry(zip, "xl/workbook.xml",
                    "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
                    "<workbook xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\" xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\">" +
                    "<sheets><sheet name=\"" + XmlEsc(safeSheetName) + "\" sheetId=\"1\" r:id=\"rId1\"/></sheets>" +
                    "</workbook>");

                WriteZipEntry(zip, "xl/_rels/workbook.xml.rels",
                    "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
                    "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">" +
                    "<Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet\" Target=\"worksheets/sheet1.xml\"/>" +
                    "<Relationship Id=\"rId2\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/styles\" Target=\"styles.xml\"/>" +
                    "</Relationships>");

                // Styles: 0=default, 1=Title, 2=Header, 3=TextCell, 4=NumericCell, 5=Footer
                WriteZipEntry(zip, "xl/styles.xml",
                    "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
                    "<styleSheet xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\">" +
                    "<fonts count=\"4\">" +
                    "<font><sz val=\"11\"/><name val=\"Arial\"/></font>" +
                    "<font><b/><sz val=\"13\"/><color rgb=\"FFFFFFFF\"/><name val=\"Arial\"/></font>" +
                    "<font><b/><sz val=\"11\"/><color rgb=\"FF1F4E78\"/><name val=\"Arial\"/></font>" +
                    "<font><b/><sz val=\"11\"/><color rgb=\"FF000000\"/><name val=\"Arial\"/></font>" +
                    "</fonts>" +
                    "<fills count=\"5\">" +
                    "<fill><patternFill patternType=\"none\"/></fill>" +
                    "<fill><patternFill patternType=\"gray125\"/></fill>" +
                    "<fill><patternFill patternType=\"solid\"><fgColor rgb=\"FF1F4E78\"/></patternFill></fill>" +
                    "<fill><patternFill patternType=\"solid\"><fgColor rgb=\"FFD9E1F2\"/></patternFill></fill>" +
                    "<fill><patternFill patternType=\"solid\"><fgColor rgb=\"FFF2F2F2\"/></patternFill></fill>" +
                    "</fills>" +
                    "<borders count=\"2\">" +
                    "<border><left/><right/><top/><bottom/><diagonal/></border>" +
                    "<border><left style=\"thin\"><color rgb=\"FFB4B4B4\"/></left><right style=\"thin\"><color rgb=\"FFB4B4B4\"/></right><top style=\"thin\"><color rgb=\"FFB4B4B4\"/></top><bottom style=\"thin\"><color rgb=\"FFB4B4B4\"/></bottom><diagonal/></border>" +
                    "</borders>" +
                    "<cellStyleXfs count=\"1\"><xf numFmtId=\"0\" fontId=\"0\" fillId=\"0\" borderId=\"0\"/></cellStyleXfs>" +
                    "<cellXfs count=\"6\">" +
                    "<xf numFmtId=\"0\" fontId=\"0\" fillId=\"0\" borderId=\"0\" xfId=\"0\"/>" +
                    "<xf numFmtId=\"0\" fontId=\"1\" fillId=\"2\" borderId=\"1\" xfId=\"0\" applyFont=\"1\" applyFill=\"1\" applyBorder=\"1\" applyAlignment=\"1\"><alignment horizontal=\"center\" vertical=\"center\"/></xf>" +
                    "<xf numFmtId=\"0\" fontId=\"2\" fillId=\"3\" borderId=\"1\" xfId=\"0\" applyFont=\"1\" applyFill=\"1\" applyBorder=\"1\" applyAlignment=\"1\"><alignment horizontal=\"center\" vertical=\"center\" wrapText=\"1\"/></xf>" +
                    "<xf numFmtId=\"0\" fontId=\"0\" fillId=\"0\" borderId=\"1\" xfId=\"0\" applyFont=\"1\" applyBorder=\"1\" applyAlignment=\"1\"><alignment horizontal=\"center\" vertical=\"center\" wrapText=\"1\"/></xf>" +
                    "<xf numFmtId=\"0\" fontId=\"0\" fillId=\"0\" borderId=\"1\" xfId=\"0\" applyFont=\"1\" applyBorder=\"1\" applyAlignment=\"1\"><alignment horizontal=\"center\" vertical=\"center\"/></xf>" +
                    "<xf numFmtId=\"0\" fontId=\"3\" fillId=\"4\" borderId=\"1\" xfId=\"0\" applyFont=\"1\" applyFill=\"1\" applyBorder=\"1\" applyAlignment=\"1\"><alignment horizontal=\"center\" vertical=\"center\"/></xf>" +
                    "</cellXfs>" +
                    "<cellStyles count=\"1\"><cellStyle name=\"Normal\" xfId=\"0\" builtinId=\"0\"/></cellStyles>" +
                    "</styleSheet>");

                StringBuilder ws = new StringBuilder();
                ws.Append("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>");
                ws.Append("<worksheet xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\">");
                int freezeRows = (hasTitle ? 1 : 0) + (hasHeaders ? 1 : 0);
                ws.Append("<sheetViews><sheetView workbookViewId=\"0\"").Append(isRtl ? " rightToLeft=\"1\"" : "").Append(">");
                if (freezeRows > 0)
                {
                    ws.Append("<pane ySplit=\"").Append(freezeRows.ToString(CultureInfo.InvariantCulture))
                      .Append("\" topLeftCell=\"A").Append((freezeRows + 1).ToString(CultureInfo.InvariantCulture))
                      .Append("\" activePane=\"bottomLeft\" state=\"frozen\"/>");
                }
                ws.Append("</sheetView></sheetViews>");

                ws.Append("<cols>");
                for (int c = 0; c < colCount; c++)
                {
                    ws.Append("<col min=\"").Append((c + 1).ToString(CultureInfo.InvariantCulture))
                      .Append("\" max=\"").Append((c + 1).ToString(CultureInfo.InvariantCulture))
                      .Append("\" width=\"").Append(colWidths[c].ToString("0.00", CultureInfo.InvariantCulture))
                      .Append("\" customWidth=\"1\"/>");
                }
                ws.Append("</cols><sheetData>");

                int rowNum = 1;
                if (hasTitle)
                {
                    ws.Append("<row r=\"").Append(rowNum.ToString(CultureInfo.InvariantCulture)).Append("\" ht=\"28\" customHeight=\"1\">");
                    for (int c = 0; c < colCount; c++)
                    {
                        string cellRef = ColIndexToLetter(c) + rowNum.ToString(CultureInfo.InvariantCulture);
                        if (c == 0)
                            AppendInlineCell(ws, cellRef, 1, data.Title);
                        else
                            ws.Append("<c r=\"").Append(cellRef).Append("\" s=\"1\"/>");
                    }
                    ws.Append("</row>");
                    rowNum++;
                }

                if (hasHeaders)
                {
                    ws.Append("<row r=\"").Append(rowNum.ToString(CultureInfo.InvariantCulture)).Append("\" ht=\"23\" customHeight=\"1\">");
                    for (int c = 0; c < colCount; c++)
                    {
                        string cellRef = ColIndexToLetter(c) + rowNum.ToString(CultureInfo.InvariantCulture);
                        string hVal = (c < data.Headers.Count && data.Headers[c] != null) ? data.Headers[c] : "";
                        AppendInlineCell(ws, cellRef, 2, hVal);
                    }
                    ws.Append("</row>");
                    rowNum++;
                }

                if (data.Rows != null)
                {
                    for (int r = 0; r < data.Rows.Count; r++)
                    {
                        List<string> row = data.Rows[r];
                        ws.Append("<row r=\"").Append(rowNum.ToString(CultureInfo.InvariantCulture)).Append("\">");
                        for (int c = 0; c < colCount; c++)
                        {
                            string cellRef = ColIndexToLetter(c) + rowNum.ToString(CultureInfo.InvariantCulture);
                            string val = (row != null && c < row.Count && row[c] != null) ? row[c] : "";
                            AppendAutoTypedCell(ws, cellRef, 3, 4, val);
                        }
                        ws.Append("</row>");
                        rowNum++;
                    }
                }

                if (data.FooterRow != null && data.FooterRow.Count > 0)
                {
                    ws.Append("<row r=\"").Append(rowNum.ToString(CultureInfo.InvariantCulture)).Append("\" ht=\"22\" customHeight=\"1\">");
                    for (int c = 0; c < colCount; c++)
                    {
                        string cellRef = ColIndexToLetter(c) + rowNum.ToString(CultureInfo.InvariantCulture);
                        string val = (c < data.FooterRow.Count && data.FooterRow[c] != null) ? data.FooterRow[c] : "";
                        AppendAutoTypedCell(ws, cellRef, 5, 5, val);
                    }
                    ws.Append("</row>");
                }

                ws.Append("</sheetData>");
                if (hasTitle && colCount > 1)
                {
                    ws.Append("<mergeCells count=\"1\"><mergeCell ref=\"A1:")
                      .Append(ColIndexToLetter(colCount - 1)).Append("1\"/></mergeCells>");
                }
                ws.Append("</worksheet>");
                WriteZipEntry(zip, "xl/worksheets/sheet1.xml", ws.ToString());
            }
        }

        private static string BuildWorksheetXml(ExcelSheetData data)
        {
            int colCount = data.Headers != null ? data.Headers.Count : 0;
            if (data.Rows != null)
            {
                for (int r = 0; r < data.Rows.Count; r++)
                    if (data.Rows[r] != null && data.Rows[r].Count > colCount) colCount = data.Rows[r].Count;
            }
            if (data.FooterRow != null && data.FooterRow.Count > colCount) colCount = data.FooterRow.Count;
            if (colCount <= 0) colCount = 1;

            double[] colWidths = new double[colCount];
            for (int c = 0; c < colCount; c++) colWidths[c] = 12.0;

            Action<int, string> trackWidth = delegate(int colIdx, string val)
            {
                if (colIdx < 0 || colIdx >= colCount || string.IsNullOrEmpty(val)) return;
                int maxLine = 0;
                string[] lines = val.Split(new char[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
                for (int li = 0; li < lines.Length; li++)
                    if (lines[li].Length > maxLine) maxLine = lines[li].Length;
                double w = Math.Min(60.0, Math.Max(10.0, maxLine * 1.25 + 4.0));
                if (w > colWidths[colIdx]) colWidths[colIdx] = w;
            };

            if (data.Headers != null)
                for (int c = 0; c < data.Headers.Count; c++) trackWidth(c, data.Headers[c]);
            if (data.Rows != null)
                for (int r = 0; r < data.Rows.Count; r++)
                    if (data.Rows[r] != null)
                        for (int c = 0; c < data.Rows[r].Count; c++) trackWidth(c, data.Rows[r][c]);
            if (data.FooterRow != null)
                for (int c = 0; c < data.FooterRow.Count; c++) trackWidth(c, data.FooterRow[c]);

            bool hasTitle = !string.IsNullOrWhiteSpace(data.Title);
            bool hasHeaders = data.Headers != null && data.Headers.Count > 0;
            bool isRtl = IsSpreadsheetArabic(data);

            StringBuilder ws = new StringBuilder();
            ws.Append("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>");
            ws.Append("<worksheet xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\">");
            int freezeRows = (hasTitle ? 1 : 0) + (hasHeaders ? 1 : 0);
            ws.Append("<sheetViews><sheetView workbookViewId=\"0\"").Append(isRtl ? " rightToLeft=\"1\"" : "").Append(">");
            if (freezeRows > 0)
            {
                ws.Append("<pane ySplit=\"").Append(freezeRows.ToString(CultureInfo.InvariantCulture))
                  .Append("\" topLeftCell=\"A").Append((freezeRows + 1).ToString(CultureInfo.InvariantCulture))
                  .Append("\" activePane=\"bottomLeft\" state=\"frozen\"/>");
            }
            ws.Append("</sheetView></sheetViews>");

            ws.Append("<cols>");
            for (int c = 0; c < colCount; c++)
            {
                ws.Append("<col min=\"").Append((c + 1).ToString(CultureInfo.InvariantCulture))
                  .Append("\" max=\"").Append((c + 1).ToString(CultureInfo.InvariantCulture))
                  .Append("\" width=\"").Append(colWidths[c].ToString("0.00", CultureInfo.InvariantCulture))
                  .Append("\" customWidth=\"1\"/>");
            }
            ws.Append("</cols><sheetData>");

            int rowNum = 1;
            if (hasTitle)
            {
                ws.Append("<row r=\"").Append(rowNum.ToString(CultureInfo.InvariantCulture)).Append("\" ht=\"28\" customHeight=\"1\">");
                for (int c = 0; c < colCount; c++)
                {
                    string cellRef = ColIndexToLetter(c) + rowNum.ToString(CultureInfo.InvariantCulture);
                    if (c == 0)
                        AppendInlineCell(ws, cellRef, 1, data.Title);
                    else
                        ws.Append("<c r=\"").Append(cellRef).Append("\" s=\"1\"/>");
                }
                ws.Append("</row>");
                rowNum++;
            }

            if (hasHeaders)
            {
                ws.Append("<row r=\"").Append(rowNum.ToString(CultureInfo.InvariantCulture)).Append("\" ht=\"23\" customHeight=\"1\">");
                for (int c = 0; c < colCount; c++)
                {
                    string cellRef = ColIndexToLetter(c) + rowNum.ToString(CultureInfo.InvariantCulture);
                    string hVal = (c < data.Headers.Count && data.Headers[c] != null) ? data.Headers[c] : "";
                    AppendInlineCell(ws, cellRef, 2, hVal);
                }
                ws.Append("</row>");
                rowNum++;
            }

            if (data.Rows != null)
            {
                for (int r = 0; r < data.Rows.Count; r++)
                {
                    List<string> row = data.Rows[r];
                    ws.Append("<row r=\"").Append(rowNum.ToString(CultureInfo.InvariantCulture)).Append("\">");
                    for (int c = 0; c < colCount; c++)
                    {
                        string cellRef = ColIndexToLetter(c) + rowNum.ToString(CultureInfo.InvariantCulture);
                        string val = (row != null && c < row.Count && row[c] != null) ? row[c] : "";
                        AppendAutoTypedCell(ws, cellRef, 3, 4, val);
                    }
                    ws.Append("</row>");
                    rowNum++;
                }
            }

            if (data.FooterRow != null && data.FooterRow.Count > 0)
            {
                ws.Append("<row r=\"").Append(rowNum.ToString(CultureInfo.InvariantCulture)).Append("\" ht=\"22\" customHeight=\"1\">");
                for (int c = 0; c < colCount; c++)
                {
                    string cellRef = ColIndexToLetter(c) + rowNum.ToString(CultureInfo.InvariantCulture);
                    string val = (c < data.FooterRow.Count && data.FooterRow[c] != null) ? data.FooterRow[c] : "";
                    AppendAutoTypedCell(ws, cellRef, 5, 5, val);
                }
                ws.Append("</row>");
            }

            ws.Append("</sheetData>");
            if (hasTitle && colCount > 1)
            {
                ws.Append("<mergeCells count=\"1\"><mergeCell ref=\"A1:")
                  .Append(ColIndexToLetter(colCount - 1)).Append("1\"/></mergeCells>");
            }
            ws.Append("</worksheet>");
            return ws.ToString();
        }

        private static void WriteXlsxWorkbook(string path, List<ExcelSheetData> sheets)
        {
            if (sheets == null || sheets.Count == 0)
                throw new ArgumentException("At least one worksheet is required.");

            HashSet<string> usedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            List<string> sheetNames = new List<string>(sheets.Count);
            for (int i = 0; i < sheets.Count; i++)
            {
                ExcelSheetData sd = sheets[i] ?? new ExcelSheetData();
                string raw = SanitizeSheetName(string.IsNullOrWhiteSpace(sd.SheetName) ? ("Sheet" + (i + 1).ToString(CultureInfo.InvariantCulture)) : sd.SheetName);
                string cand = raw;
                int suffix = 2;
                while (!usedNames.Add(cand))
                {
                    string suf = "_" + suffix.ToString(CultureInfo.InvariantCulture);
                    cand = (raw.Length + suf.Length > 31 ? raw.Substring(0, 31 - suf.Length) : raw) + suf;
                    suffix++;
                }
                sheetNames.Add(cand);
            }

            StringBuilder ctOverrides = new StringBuilder();
            StringBuilder wbSheets = new StringBuilder();
            StringBuilder wbRels = new StringBuilder();
            for (int i = 0; i < sheets.Count; i++)
            {
                int sid = i + 1;
                ctOverrides.Append("<Override PartName=\"/xl/worksheets/sheet").Append(sid.ToString(CultureInfo.InvariantCulture))
                    .Append(".xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml\"/>");
                wbSheets.Append("<sheet name=\"").Append(XmlEsc(sheetNames[i])).Append("\" sheetId=\"")
                    .Append(sid.ToString(CultureInfo.InvariantCulture)).Append("\" r:id=\"rId")
                    .Append(sid.ToString(CultureInfo.InvariantCulture)).Append("\"/>");
                wbRels.Append("<Relationship Id=\"rId").Append(sid.ToString(CultureInfo.InvariantCulture))
                    .Append("\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet\" Target=\"worksheets/sheet")
                    .Append(sid.ToString(CultureInfo.InvariantCulture)).Append(".xml\"/>");
            }
            int styleRelId = sheets.Count + 1;
            wbRels.Append("<Relationship Id=\"rId").Append(styleRelId.ToString(CultureInfo.InvariantCulture))
                .Append("\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/styles\" Target=\"styles.xml\"/>");

            using (FileStream fs = new FileStream(path, FileMode.Create, FileAccess.ReadWrite, FileShare.None))
            using (ZipArchive zip = new ZipArchive(fs, ZipArchiveMode.Create))
            {
                WriteZipEntry(zip, "[Content_Types].xml",
                    "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
                    "<Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\">" +
                    "<Default Extension=\"rels\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.relationships+xml\"/>" +
                    "<Default Extension=\"xml\" ContentType=\"application/xml\"/>" +
                    "<Override PartName=\"/xl/workbook.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml\"/>" +
                    ctOverrides.ToString() +
                    "<Override PartName=\"/xl/styles.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.styles+xml\"/>" +
                    "</Types>");

                WriteZipEntry(zip, "_rels/.rels",
                    "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
                    "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">" +
                    "<Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument\" Target=\"xl/workbook.xml\"/>" +
                    "</Relationships>");

                WriteZipEntry(zip, "xl/workbook.xml",
                    "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
                    "<workbook xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\" xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\">" +
                    "<sheets>" + wbSheets.ToString() + "</sheets>" +
                    "</workbook>");

                WriteZipEntry(zip, "xl/_rels/workbook.xml.rels",
                    "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
                    "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">" +
                    wbRels.ToString() +
                    "</Relationships>");

                WriteZipEntry(zip, "xl/styles.xml",
                    "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
                    "<styleSheet xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\">" +
                    "<fonts count=\"4\">" +
                    "<font><sz val=\"11\"/><name val=\"Arial\"/></font>" +
                    "<font><b/><sz val=\"13\"/><color rgb=\"FFFFFFFF\"/><name val=\"Arial\"/></font>" +
                    "<font><b/><sz val=\"11\"/><color rgb=\"FF1F4E78\"/><name val=\"Arial\"/></font>" +
                    "<font><b/><sz val=\"11\"/><color rgb=\"FF000000\"/><name val=\"Arial\"/></font>" +
                    "</fonts>" +
                    "<fills count=\"5\">" +
                    "<fill><patternFill patternType=\"none\"/></fill>" +
                    "<fill><patternFill patternType=\"gray125\"/></fill>" +
                    "<fill><patternFill patternType=\"solid\"><fgColor rgb=\"FF1F4E78\"/></patternFill></fill>" +
                    "<fill><patternFill patternType=\"solid\"><fgColor rgb=\"FFD9E1F2\"/></patternFill></fill>" +
                    "<fill><patternFill patternType=\"solid\"><fgColor rgb=\"FFF2F2F2\"/></patternFill></fill>" +
                    "</fills>" +
                    "<borders count=\"2\">" +
                    "<border><left/><right/><top/><bottom/><diagonal/></border>" +
                    "<border><left style=\"thin\"><color rgb=\"FFB4B4B4\"/></left><right style=\"thin\"><color rgb=\"FFB4B4B4\"/></right><top style=\"thin\"><color rgb=\"FFB4B4B4\"/></top><bottom style=\"thin\"><color rgb=\"FFB4B4B4\"/></bottom><diagonal/></border>" +
                    "</borders>" +
                    "<cellStyleXfs count=\"1\"><xf numFmtId=\"0\" fontId=\"0\" fillId=\"0\" borderId=\"0\"/></cellStyleXfs>" +
                    "<cellXfs count=\"6\">" +
                    "<xf numFmtId=\"0\" fontId=\"0\" fillId=\"0\" borderId=\"0\" xfId=\"0\"/>" +
                    "<xf numFmtId=\"0\" fontId=\"1\" fillId=\"2\" borderId=\"1\" xfId=\"0\" applyFont=\"1\" applyFill=\"1\" applyBorder=\"1\" applyAlignment=\"1\"><alignment horizontal=\"center\" vertical=\"center\"/></xf>" +
                    "<xf numFmtId=\"0\" fontId=\"2\" fillId=\"3\" borderId=\"1\" xfId=\"0\" applyFont=\"1\" applyFill=\"1\" applyBorder=\"1\" applyAlignment=\"1\"><alignment horizontal=\"center\" vertical=\"center\" wrapText=\"1\"/></xf>" +
                    "<xf numFmtId=\"0\" fontId=\"0\" fillId=\"0\" borderId=\"1\" xfId=\"0\" applyFont=\"1\" applyBorder=\"1\" applyAlignment=\"1\"><alignment horizontal=\"center\" vertical=\"center\" wrapText=\"1\"/></xf>" +
                    "<xf numFmtId=\"0\" fontId=\"0\" fillId=\"0\" borderId=\"1\" xfId=\"0\" applyFont=\"1\" applyBorder=\"1\" applyAlignment=\"1\"><alignment horizontal=\"center\" vertical=\"center\"/></xf>" +
                    "<xf numFmtId=\"0\" fontId=\"3\" fillId=\"4\" borderId=\"1\" xfId=\"0\" applyFont=\"1\" applyFill=\"1\" applyBorder=\"1\" applyAlignment=\"1\"><alignment horizontal=\"center\" vertical=\"center\"/></xf>" +
                    "</cellXfs>" +
                    "<cellStyles count=\"1\"><cellStyle name=\"Normal\" xfId=\"0\" builtinId=\"0\"/></cellStyles>" +
                    "</styleSheet>");

                for (int i = 0; i < sheets.Count; i++)
                {
                    ExcelSheetData sd = sheets[i] ?? new ExcelSheetData();
                    string xml = BuildWorksheetXml(sd);
                    WriteZipEntry(zip, "xl/worksheets/sheet" + (i + 1).ToString(CultureInfo.InvariantCulture) + ".xml", xml);
                }
            }
        }

        private static void AppendInlineCell(StringBuilder ws, string cellRef, int styleId, string text)
        {
            ws.Append("<c r=\"").Append(cellRef).Append("\" s=\"").Append(styleId.ToString(CultureInfo.InvariantCulture))
              .Append("\" t=\"inlineStr\"><is><t xml:space=\"preserve\">")
              .Append(XmlEsc(text ?? ""))
              .Append("</t></is></c>");
        }

        private static void AppendAutoTypedCell(StringBuilder ws, string cellRef, int textStyle, int numStyle, string text)
        {
            string trimmed = (text ?? "").Trim();
            double numVal;
            if (!string.IsNullOrEmpty(trimmed) &&
                !(trimmed.Length > 1 && trimmed[0] == '0' && trimmed[1] != '.') &&
                double.TryParse(trimmed, NumberStyles.Float, CultureInfo.InvariantCulture, out numVal) &&
                !double.IsNaN(numVal) && !double.IsInfinity(numVal))
            {
                ws.Append("<c r=\"").Append(cellRef).Append("\" s=\"").Append(numStyle.ToString(CultureInfo.InvariantCulture))
                  .Append("\" t=\"n\"><v>").Append(numVal.ToString("G15", CultureInfo.InvariantCulture))
                  .Append("</v></c>");
            }
            else
            {
                AppendInlineCell(ws, cellRef, textStyle, text ?? "");
            }
        }

        private static void WriteZipEntry(ZipArchive zip, string entryName, string content)
        {
            ZipArchiveEntry entry = zip.CreateEntry(entryName, CompressionLevel.Optimal);
            using (Stream s = entry.Open())
            {
                byte[] bytes = Utf8NoBom.GetBytes(content);
                s.Write(bytes, 0, bytes.Length);
            }
        }

        // ==================== OOXML (.xlsx) & CSV Reader ====================

        private static ExcelSheetData ReadCsvFile(string filePath, int maxRows, int maxCols)
        {
            byte[] rawBytes = File.ReadAllBytes(filePath);
            string text;
            if (rawBytes.Length >= 3 && rawBytes[0] == 0xEF && rawBytes[1] == 0xBB && rawBytes[2] == 0xBF)
            {
                text = Utf8NoBom.GetString(rawBytes, 3, rawBytes.Length - 3);
            }
            else
            {
                try
                {
                    Encoding strictUtf8 = new UTF8Encoding(false, true);
                    text = strictUtf8.GetString(rawBytes);
                }
                catch
                {
                    text = Encoding.GetEncoding(1256).GetString(rawBytes);
                }
            }

            char delim = ',';
            string[] previewLines = text.Split(new char[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            if (previewLines.Length > 0)
            {
                string first = previewLines[0];
                int commas = CountCharOutsideQuotes(first, ',');
                int semis = CountCharOutsideQuotes(first, ';');
                int tabs = CountCharOutsideQuotes(first, '\t');
                if (tabs > commas && tabs >= semis) delim = '\t';
                else if (semis > commas) delim = ';';
            }

            List<List<string>> allRows = ParseDelimitedText(text, delim, maxRows + 5, maxCols);
            ExcelSheetData sheet = new ExcelSheetData();
            sheet.SheetName = Path.GetFileNameWithoutExtension(filePath);
            sheet.AvailableSheets.Add(sheet.SheetName);
            PopulateSheetFromMatrix(sheet, allRows, maxRows, maxCols);
            return sheet;
        }

        private static ExcelSheetData ReadXlsxFile(string filePath, string requestedSheetName, int maxRows, int maxCols)
        {
            ExcelSheetData result = new ExcelSheetData();
            using (FileStream fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            using (ZipArchive zip = new ZipArchive(fs, ZipArchiveMode.Read))
            {
                Dictionary<string, string> relTargets = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                ZipArchiveEntry relsEntry = FindZipEntryIgnoreCase(zip, "xl/_rels/workbook.xml.rels");
                if (relsEntry != null)
                {
                    XmlDocument relDoc = LoadXmlFromEntry(relsEntry);
                    XmlNodeList nodes = relDoc.GetElementsByTagName("Relationship");
                    for (int i = 0; i < nodes.Count; i++)
                    {
                        XmlElement el = nodes[i] as XmlElement;
                        if (el == null) continue;
                        string id = el.GetAttribute("Id");
                        string target = el.GetAttribute("Target");
                        if (!string.IsNullOrEmpty(id) && !string.IsNullOrEmpty(target))
                        {
                            string norm = target.Replace('\\', '/').TrimStart('/');
                            if (!norm.StartsWith("xl/", StringComparison.OrdinalIgnoreCase))
                                norm = "xl/" + norm;
                            relTargets[id] = norm;
                        }
                    }
                }

                List<string> sheetNames = new List<string>();
                List<string> sheetPaths = new List<string>();
                ZipArchiveEntry wbEntry = FindZipEntryIgnoreCase(zip, "xl/workbook.xml");
                if (wbEntry != null)
                {
                    XmlDocument wbDoc = LoadXmlFromEntry(wbEntry);
                    XmlNodeList sheets = wbDoc.GetElementsByTagName("sheet");
                    for (int i = 0; i < sheets.Count; i++)
                    {
                        XmlElement el = sheets[i] as XmlElement;
                        if (el == null) continue;
                        string sName = el.GetAttribute("name");
                        string rId = el.GetAttribute("id", "http://schemas.openxmlformats.org/officeDocument/2006/relationships");
                        if (string.IsNullOrEmpty(rId)) rId = el.GetAttribute("r:id");
                        string targetPath;
                        if (!string.IsNullOrEmpty(rId) && relTargets.TryGetValue(rId, out targetPath))
                        {
                            sheetNames.Add(string.IsNullOrEmpty(sName) ? ("Sheet" + (i + 1)) : sName);
                            sheetPaths.Add(targetPath);
                        }
                    }
                }

                if (sheetPaths.Count == 0)
                {
                    // Fallback: look for xl/worksheets/sheet1.xml directly
                    ZipArchiveEntry fallbackWs = FindZipEntryIgnoreCase(zip, "xl/worksheets/sheet1.xml");
                    if (fallbackWs == null)
                        throw new InvalidOperationException("No worksheets found inside Excel workbook.");
                    sheetNames.Add("Sheet1");
                    sheetPaths.Add(fallbackWs.FullName);
                }

                result.AvailableSheets.AddRange(sheetNames);
                int chosenIdx = 0;
                if (!string.IsNullOrWhiteSpace(requestedSheetName))
                {
                    for (int i = 0; i < sheetNames.Count; i++)
                    {
                        if (string.Equals(sheetNames[i], requestedSheetName.Trim(), StringComparison.OrdinalIgnoreCase))
                        {
                            chosenIdx = i;
                            break;
                        }
                    }
                }
                result.SheetName = sheetNames[chosenIdx];

                List<string> sharedStrings = ReadSharedStrings(FindZipEntryIgnoreCase(zip, "xl/sharedStrings.xml"));
                ZipArchiveEntry wsEntry = FindZipEntryIgnoreCase(zip, sheetPaths[chosenIdx]);
                if (wsEntry == null)
                    throw new FileNotFoundException("Worksheet XML entry not found: " + sheetPaths[chosenIdx]);

                List<List<string>> matrix = ParseWorksheetMatrix(wsEntry, sharedStrings, maxRows + 5, maxCols);
                PopulateSheetFromMatrix(result, matrix, maxRows, maxCols);
            }
            return result;
        }

        private static List<string> ReadSharedStrings(ZipArchiveEntry entry)
        {
            List<string> list = new List<string>();
            if (entry == null) return list;
            XmlDocument doc = LoadXmlFromEntry(entry);
            XmlNodeList sis = doc.GetElementsByTagName("si");
            for (int i = 0; i < sis.Count; i++)
            {
                XmlElement si = sis[i] as XmlElement;
                if (si == null) { list.Add(""); continue; }
                XmlNodeList tNodes = si.GetElementsByTagName("t");
                if (tNodes.Count == 0) { list.Add(""); continue; }
                if (tNodes.Count == 1) { list.Add(tNodes[0].InnerText ?? ""); continue; }
                StringBuilder sb = new StringBuilder();
                for (int ti = 0; ti < tNodes.Count; ti++)
                {
                    XmlNode parent = tNodes[ti].ParentNode;
                    if (parent != null && string.Equals(parent.LocalName, "rPh", StringComparison.OrdinalIgnoreCase))
                        continue; // Skip phonetic ruby text
                    sb.Append(tNodes[ti].InnerText);
                }
                list.Add(sb.ToString());
            }
            return list;
        }

        private static List<List<string>> ParseWorksheetMatrix(
            ZipArchiveEntry wsEntry, List<string> sharedStrings, int maxRows, int maxCols)
        {
            List<List<string>> matrix = new List<List<string>>();
            XmlDocument doc = LoadXmlFromEntry(wsEntry);
            XmlNodeList rowNodes = doc.GetElementsByTagName("row");

            int maxColFound = 0;
            List<Dictionary<int, string>> sparseRows = new List<Dictionary<int, string>>();
            for (int ri = 0; ri < rowNodes.Count && sparseRows.Count < maxRows; ri++)
            {
                XmlElement rowEl = rowNodes[ri] as XmlElement;
                if (rowEl == null) continue;
                Dictionary<int, string> rowDict = new Dictionary<int, string>();
                bool anyNonEmpty = false;
                int sequentialCol = 0;

                XmlNodeList cells = rowEl.ChildNodes;
                for (int ci = 0; ci < cells.Count; ci++)
                {
                    XmlElement cellEl = cells[ci] as XmlElement;
                    if (cellEl == null || !string.Equals(cellEl.LocalName, "c", StringComparison.Ordinal)) continue;

                    string rAttr = cellEl.GetAttribute("r");
                    int colIdx = !string.IsNullOrEmpty(rAttr) ? ParseCellColumnIndex(rAttr) : sequentialCol;
                    sequentialCol = colIdx + 1;
                    if (colIdx < 0 || colIdx >= maxCols) continue;

                    string val = ExtractCellText(cellEl, sharedStrings);
                    if (!string.IsNullOrWhiteSpace(val)) anyNonEmpty = true;
                    rowDict[colIdx] = val;
                    if (colIdx + 1 > maxColFound) maxColFound = colIdx + 1;
                }

                if (anyNonEmpty) sparseRows.Add(rowDict);
            }

            if (maxColFound <= 0) return matrix;
            for (int ri = 0; ri < sparseRows.Count; ri++)
            {
                Dictionary<int, string> d = sparseRows[ri];
                List<string> dense = new List<string>(maxColFound);
                for (int c = 0; c < maxColFound; c++)
                {
                    string v;
                    dense.Add(d.TryGetValue(c, out v) ? v : "");
                }
                matrix.Add(dense);
            }
            return matrix;
        }

        private static string ExtractCellText(XmlElement cellEl, List<string> sharedStrings)
        {
            string cellType = cellEl.GetAttribute("t");
            if (string.Equals(cellType, "inlineStr", StringComparison.OrdinalIgnoreCase))
            {
                XmlNodeList tNodes = cellEl.GetElementsByTagName("t");
                if (tNodes.Count == 0) return "";
                StringBuilder sb = new StringBuilder();
                for (int i = 0; i < tNodes.Count; i++) sb.Append(tNodes[i].InnerText);
                return sb.ToString().Trim();
            }

            string rawV = "";
            XmlNodeList vNodes = cellEl.GetElementsByTagName("v");
            if (vNodes.Count > 0) rawV = vNodes[0].InnerText ?? "";

            if (string.Equals(cellType, "s", StringComparison.OrdinalIgnoreCase))
            {
                int idx;
                if (int.TryParse(rawV.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out idx) &&
                    idx >= 0 && idx < sharedStrings.Count)
                {
                    return (sharedStrings[idx] ?? "").Trim();
                }
                return rawV.Trim();
            }
            if (string.Equals(cellType, "b", StringComparison.OrdinalIgnoreCase))
                return rawV.Trim() == "1" ? "TRUE" : "FALSE";

            return rawV.Trim();
        }

        private static void PopulateSheetFromMatrix(ExcelSheetData sheet, List<List<string>> matrix, int maxRows, int maxCols)
        {
            if (matrix == null || matrix.Count == 0) return;

            // Trim trailing all-empty columns cleanly
            int colCount = 0;
            for (int r = 0; r < matrix.Count; r++)
            {
                for (int c = matrix[r].Count - 1; c >= 0; c--)
                {
                    if (!string.IsNullOrWhiteSpace(matrix[r][c]))
                    {
                        if (c + 1 > colCount) colCount = c + 1;
                        break;
                    }
                }
            }
            colCount = Math.Min(maxCols, Math.Max(1, colCount));

            int dataStartRow = 0;
            // Detect optional single-cell banner title row above a multi-column header row
            if (matrix.Count >= 2 && colCount >= 2)
            {
                int firstNonEmpty = CountNonEmptyInRow(matrix[0], colCount);
                int secondNonEmpty = CountNonEmptyInRow(matrix[1], colCount);
                if (firstNonEmpty == 1 && secondNonEmpty >= 2)
                {
                    for (int c = 0; c < colCount; c++)
                    {
                        if (!string.IsNullOrWhiteSpace(matrix[0][c]))
                        {
                            sheet.Title = matrix[0][c].Trim();
                            break;
                        }
                    }
                    dataStartRow = 1;
                }
            }

            if (dataStartRow < matrix.Count)
            {
                List<string> hdr = matrix[dataStartRow];
                for (int c = 0; c < colCount; c++)
                    sheet.Headers.Add((c < hdr.Count && hdr[c] != null) ? hdr[c].Trim() : ("Col " + (c + 1)));
                dataStartRow++;
            }

            for (int r = dataStartRow; r < matrix.Count && sheet.Rows.Count < maxRows; r++)
            {
                List<string> src = matrix[r];
                List<string> row = new List<string>(colCount);
                bool any = false;
                for (int c = 0; c < colCount; c++)
                {
                    string v = (c < src.Count && src[c] != null) ? src[c].Trim() : "";
                    if (!string.IsNullOrEmpty(v)) any = true;
                    row.Add(v);
                }
                if (any) sheet.Rows.Add(row);
            }
        }

        private static int CountNonEmptyInRow(List<string> row, int maxCols)
        {
            int cnt = 0;
            int lim = Math.Min(maxCols, row.Count);
            for (int c = 0; c < lim; c++)
                if (!string.IsNullOrWhiteSpace(row[c])) cnt++;
            return cnt;
        }

        private static int ParseCellColumnIndex(string cellRef)
        {
            int col = 0;
            for (int i = 0; i < cellRef.Length; i++)
            {
                char ch = cellRef[i];
                if (ch >= 'A' && ch <= 'Z') col = col * 26 + (ch - 'A' + 1);
                else if (ch >= 'a' && ch <= 'z') col = col * 26 + (ch - 'a' + 1);
                else break;
            }
            return col - 1;
        }

        private static string ColIndexToLetter(int index)
        {
            StringBuilder sb = new StringBuilder();
            int n = index + 1;
            while (n > 0)
            {
                int rem = (n - 1) % 26;
                sb.Insert(0, (char)('A' + rem));
                n = (n - 1) / 26;
            }
            return sb.ToString();
        }

        private static ZipArchiveEntry FindZipEntryIgnoreCase(ZipArchive zip, string fullName)
        {
            string target = (fullName ?? "").Replace('\\', '/').TrimStart('/');
            for (int i = 0; i < zip.Entries.Count; i++)
            {
                ZipArchiveEntry e = zip.Entries[i];
                if (string.Equals(e.FullName.Replace('\\', '/'), target, StringComparison.OrdinalIgnoreCase))
                    return e;
            }
            return null;
        }

        private static XmlDocument LoadXmlFromEntry(ZipArchiveEntry entry)
        {
            if (entry.Length > 50L * 1024L * 1024L)
                throw new InvalidDataException("Spreadsheet XML entry exceeds 50 MB safety limit.");
            XmlDocument doc = new XmlDocument();
            doc.XmlResolver = null;
            using (Stream s = entry.Open())
            {
                XmlReaderSettings settings = new XmlReaderSettings();
                settings.DtdProcessing = DtdProcessing.Prohibit;
                settings.XmlResolver = null;
                using (XmlReader xr = XmlReader.Create(s, settings))
                {
                    doc.Load(xr);
                }
            }
            return doc;
        }

        private static int CountCharOutsideQuotes(string line, char target)
        {
            int cnt = 0;
            bool inQ = false;
            for (int i = 0; i < line.Length; i++)
            {
                char c = line[i];
                if (c == '"') inQ = !inQ;
                else if (!inQ && c == target) cnt++;
            }
            return cnt;
        }

        private static List<List<string>> ParseDelimitedText(string text, char delim, int maxRows, int maxCols)
        {
            List<List<string>> rows = new List<List<string>>();
            List<string> curRow = new List<string>();
            StringBuilder cell = new StringBuilder();
            bool inQuotes = false;

            for (int i = 0; i < text.Length; i++)
            {
                char c = text[i];
                if (inQuotes)
                {
                    if (c == '"')
                    {
                        if (i + 1 < text.Length && text[i + 1] == '"')
                        {
                            cell.Append('"');
                            i++;
                        }
                        else
                        {
                            inQuotes = false;
                        }
                    }
                    else
                    {
                        cell.Append(c);
                    }
                }
                else
                {
                    if (c == '"')
                    {
                        inQuotes = true;
                    }
                    else if (c == delim)
                    {
                        if (curRow.Count < maxCols) curRow.Add(cell.ToString().Trim());
                        cell.Length = 0;
                    }
                    else if (c == '\r' || c == '\n')
                    {
                        if (c == '\r' && i + 1 < text.Length && text[i + 1] == '\n') i++;
                        if (curRow.Count < maxCols) curRow.Add(cell.ToString().Trim());
                        cell.Length = 0;
                        bool any = false;
                        for (int k = 0; k < curRow.Count; k++)
                            if (!string.IsNullOrEmpty(curRow[k])) { any = true; break; }
                        if (any)
                        {
                            rows.Add(curRow);
                            if (rows.Count >= maxRows) break;
                        }
                        curRow = new List<string>();
                    }
                    else
                    {
                        cell.Append(c);
                    }
                }
            }
            if (rows.Count < maxRows && (cell.Length > 0 || curRow.Count > 0))
            {
                if (curRow.Count < maxCols) curRow.Add(cell.ToString().Trim());
                bool any = false;
                for (int k = 0; k < curRow.Count; k++)
                    if (!string.IsNullOrEmpty(curRow[k])) { any = true; break; }
                if (any) rows.Add(curRow);
            }
            return rows;
        }

        private static string EscapeCsv(string val)
        {
            if (val == null) return "";
            if (val.IndexOf(',') >= 0 || val.IndexOf('"') >= 0 || val.IndexOf('\n') >= 0 || val.IndexOf('\r') >= 0)
                return "\"" + val.Replace("\"", "\"\"") + "\"";
            return val;
        }

        private static string XmlEsc(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            StringBuilder sb = new StringBuilder(s.Length);
            for (int i = 0; i < s.Length; i++)
            {
                char ch = s[i];
                if (ch == '&') sb.Append("&amp;");
                else if (ch == '<') sb.Append("&lt;");
                else if (ch == '>') sb.Append("&gt;");
                else if (ch == '"') sb.Append("&quot;");
                else if (ch == '\'') sb.Append("&apos;");
                else if (ch == '\t' || ch == '\n' || ch == '\r' || ch >= ' ') sb.Append(ch);
            }
            return sb.ToString();
        }

        private static string SanitizeFilePart(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return "export";
            string cleaned = Regex.Replace(s.Trim(), @"[\\/:*?""<>|\s]+", "_");
            cleaned = cleaned.Trim('_');
            if (string.IsNullOrEmpty(cleaned)) return "export";
            return cleaned.Length > 48 ? cleaned.Substring(0, 48) : cleaned;
        }

        private static string SanitizeSheetName(string s)
        {
            string cleaned = Regex.Replace((s ?? "Sheet1").Trim(), @"[\\/?*\[\]:]+", "_");
            if (string.IsNullOrEmpty(cleaned)) cleaned = "Sheet1";
            return cleaned.Length > 31 ? cleaned.Substring(0, 31) : cleaned;
        }
    }
}
