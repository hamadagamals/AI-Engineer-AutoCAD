using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;

namespace AIEngineer.AutoCAD
{
    internal static partial class CadToolExecutor
    {
        private static List<ObjectId> CollectMatchingBlockReferenceIds(
            Document doc,
            Database db,
            BlockTableRecord ms,
            Transaction tr,
            Dictionary<string, object> args,
            Dictionary<string, bool> layerHiddenFlags)
        {
            string blockFilter = GetStr(args, "block_name");
            string layerFilter = GetStr(args, "layer_filter");
            bool selectedOnly = GetBool(args, "selected_only", false);
            List<string> explicitHandles = ExtractStringListArg(args, "handles");
            double[] bbox = GetDoubleArray(args, "bbox");

            HashSet<string> targetHandles = new HashSet<string>(explicitHandles, StringComparer.OrdinalIgnoreCase);
            if (selectedOnly || (targetHandles.Count == 0 && string.IsNullOrWhiteSpace(blockFilter)))
            {
                List<string> sel = GetSelectedHandleStrings(doc);
                for (int i = 0; i < sel.Count; i++) targetHandles.Add(sel[i]);
            }

            List<ObjectId> ids = new List<ObjectId>();
            foreach (ObjectId id in ms)
            {
                if (id.IsErased) continue;
                Entity ent = tr.GetObject(id, OpenMode.ForRead) as Entity;
                if (ent == null || ent.IsErased || IsAiAnnotationLayer(ent.Layer)) continue;
                BlockReference br = ent as BlockReference;
                if (br == null) continue;

                string hStr = ent.Handle.ToString();
                if ((selectedOnly || explicitHandles.Count > 0) && targetHandles.Count > 0 && !targetHandles.Contains(hStr))
                    continue;
                if (selectedOnly && targetHandles.Count == 0)
                    continue;

                string bName = ResolveBlockName(tr, br);
                if (IsAnonymousBlockName(bName) || IsSystemBlockName(bName)) continue;

                if (!string.IsNullOrWhiteSpace(blockFilter) &&
                    !string.Equals(bName, blockFilter, StringComparison.OrdinalIgnoreCase) &&
                    !MatchesPatternOrSubstring(bName, blockFilter))
                    continue;

                if (!string.IsNullOrWhiteSpace(layerFilter) && !MatchesPatternOrSubstring(ent.Layer ?? "", layerFilter))
                    continue;

                if (bbox != null && bbox.Length >= 4)
                {
                    try
                    {
                        Extents3d ex = ent.GeometricExtents;
                        double cx = (ex.MinPoint.X + ex.MaxPoint.X) * 0.5;
                        double cy = (ex.MinPoint.Y + ex.MaxPoint.Y) * 0.5;
                        if (cx < bbox[0] || cx > bbox[2] || cy < bbox[1] || cy > bbox[3]) continue;
                    }
                    catch { continue; }
                }

                ids.Add(id);
            }
            return ids;
        }

        private static Dictionary<string, object> ManageAttributes(Document doc, Dictionary<string, object> args)
        {
            Database db = doc.Database;
            string action = (GetStr(args, "action") ?? "extract").Trim().ToLowerInvariant();
            string filePath = GetStr(args, "file_path") ?? GetStr(args, "excel_path");
            bool browse = GetBool(args, "browse", false);
            bool exportExcel = GetBool(args, "export_excel", action == "extract");
            bool openFile = GetBool(args, "open_file", false);

            if (action == "extract")
            {
                List<string> allTags = new List<string>();
                List<Dictionary<string, string>> rowsDict = new List<Dictionary<string, string>>();
                List<object> preview = new List<object>();

                using (DocumentLock docLock = doc.LockDocument())
                using (Transaction tr = db.TransactionManager.StartTransaction())
                {
                    BlockTable bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
                    BlockTableRecord ms = (BlockTableRecord)tr.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForRead);
                    Dictionary<string, bool> layerHiddenFlags = BuildLayerHiddenMap(db, tr);
                    List<ObjectId> brIds = CollectMatchingBlockReferenceIds(doc, db, ms, tr, args, layerHiddenFlags);

                    for (int i = 0; i < brIds.Count; i++)
                    {
                        BlockReference br = tr.GetObject(brIds[i], OpenMode.ForRead) as BlockReference;
                        if (br == null || br.AttributeCollection == null || br.AttributeCollection.Count == 0) continue;

                        Dictionary<string, string> rowMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                        rowMap["BlockHandle"] = br.Handle.ToString();
                        rowMap["BlockName"] = ResolveBlockName(tr, br);
                        rowMap["X"] = Round(br.Position.X).ToString(CultureInfo.InvariantCulture);
                        rowMap["Y"] = Round(br.Position.Y).ToString(CultureInfo.InvariantCulture);

                        Dictionary<string, string> attrMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                        foreach (ObjectId aid in br.AttributeCollection)
                        {
                            AttributeReference att = tr.GetObject(aid, OpenMode.ForRead) as AttributeReference;
                            if (att == null || att.IsErased) continue;
                            string tag = (att.Tag ?? "").Trim();
                            if (string.IsNullOrEmpty(tag)) continue;
                            string val = CleanCadTextValue(att.TextString);
                            rowMap[tag] = val;
                            attrMap[tag] = val;
                            if (!allTags.Contains(tag)) allTags.Add(tag);
                        }
                        if (attrMap.Count == 0) continue;
                        rowsDict.Add(rowMap);
                        if (preview.Count < 40)
                        {
                            preview.Add(new Dictionary<string, object>
                            {
                                { "handle", rowMap["BlockHandle"] },
                                { "block_name", rowMap["BlockName"] },
                                { "attributes", attrMap }
                            });
                        }
                    }
                    tr.Commit();
                }

                if (rowsDict.Count == 0)
                    return Err("لم يتم العثور على بلوكات تحتوي على Attributes مطابقة للمعايير.");

                ExcelSheetData sheet = new ExcelSheetData
                {
                    Title = "جدول خصائص البلوكات (BLOCK ATTRIBUTES)",
                    SheetName = "Block_Attributes",
                    Headers = new List<string> { "BlockHandle", "BlockName", "X", "Y" }
                };
                for (int t = 0; t < allTags.Count; t++) sheet.Headers.Add(allTags[t]);

                for (int r = 0; r < rowsDict.Count; r++)
                {
                    List<string> rowVals = new List<string>();
                    for (int c = 0; c < sheet.Headers.Count; c++)
                    {
                        string v;
                        rowsDict[r].TryGetValue(sheet.Headers[c], out v);
                        rowVals.Add(v ?? "");
                    }
                    sheet.Rows.Add(rowVals);
                }

                Dictionary<string, object> res = new Dictionary<string, object>
                {
                    { "action", "extract" },
                    { "blocks_with_attributes", rowsDict.Count },
                    { "attribute_tags", allTags },
                    { "rows", preview }
                };

                if (exportExcel)
                {
                    if (string.IsNullOrWhiteSpace(filePath))
                        filePath = ExcelTableInterop.ResolveDefaultExportPath(doc, "block_attributes", ".xlsx");
                    if (browse)
                    {
                        string picked = ExcelTableInterop.BrowseSaveSpreadsheetPath(Path.GetFileName(filePath), Path.GetDirectoryName(filePath));
                        if (!string.IsNullOrWhiteSpace(picked)) filePath = picked;
                    }
                    string saved = ExcelTableInterop.WriteSpreadsheet(filePath, sheet);
                    res["excel_exported"] = true;
                    res["excel_path"] = saved;
                    if (openFile) ExcelTableInterop.TryOpenWithShell(saved);
                }
                return Ok(res);
            }
            else if (action == "sync_from_excel")
            {
                if (browse || string.IsNullOrWhiteSpace(filePath))
                {
                    string initDir = null;
                    try { if (!string.IsNullOrWhiteSpace(doc.Name) && Path.IsPathRooted(doc.Name)) initDir = Path.GetDirectoryName(doc.Name); }
                    catch { }
                    string picked = ExcelTableInterop.BrowseOpenSpreadsheetPath(initDir);
                    if (string.IsNullOrWhiteSpace(picked))
                        return Err("تم إلغاء اختيار ملف Excel لمزامنة الـ Attributes.");
                    filePath = picked;
                }
                if (!File.Exists(filePath))
                    return Err("ملف Excel غير موجود: " + filePath);

                ExcelSheetData xl = ExcelTableInterop.ReadSpreadsheet(filePath, GetStr(args, "sheet_name"), 2000, 100);
                List<string> hdrs = xl.Headers ?? new List<string>();
                int handleCol = -1;
                for (int c = 0; c < hdrs.Count; c++)
                {
                    string h = (hdrs[c] ?? "").Trim();
                    if (string.Equals(h, "BlockHandle", StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(h, "Handle", StringComparison.OrdinalIgnoreCase))
                    {
                        handleCol = c;
                        break;
                    }
                }
                if (handleCol < 0)
                    return Err("يجب أن يحتوي جدول Excel على عمود باسم 'BlockHandle' أو 'Handle' لمطابقة البلوكات.");

                Dictionary<string, Dictionary<string, string>> updatesByHandle = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
                for (int r = 0; r < xl.Rows.Count; r++)
                {
                    List<string> row = xl.Rows[r];
                    if (row == null || handleCol >= row.Count) continue;
                    string hVal = (row[handleCol] ?? "").Trim();
                    if (string.IsNullOrEmpty(hVal)) continue;

                    Dictionary<string, string> tagVals = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    for (int c = 0; c < hdrs.Count && c < row.Count; c++)
                    {
                        if (c == handleCol) continue;
                        string colName = (hdrs[c] ?? "").Trim();
                        if (string.IsNullOrEmpty(colName) ||
                            string.Equals(colName, "BlockName", StringComparison.OrdinalIgnoreCase) ||
                            string.Equals(colName, "X", StringComparison.OrdinalIgnoreCase) ||
                            string.Equals(colName, "Y", StringComparison.OrdinalIgnoreCase))
                            continue;
                        tagVals[colName] = row[c] ?? "";
                    }
                    updatesByHandle[hVal] = tagVals;
                }

                Dictionary<ObjectId, ModifiedEntitySnapshot> snapById = new Dictionary<ObjectId, ModifiedEntitySnapshot>();
                int updatedBlocks = 0;
                int updatedAttrs = 0;
                int missingHandles = 0;

                using (DocumentLock docLock = doc.LockDocument())
                {
                    using (Transaction tr = db.TransactionManager.StartTransaction())
                    {
                        foreach (KeyValuePair<string, Dictionary<string, string>> kv in updatesByHandle)
                        {
                            ObjectId brId = ResolveHandle(db, kv.Key);
                            if (brId.IsNull || brId.IsErased) { missingHandles++; continue; }
                            BlockReference br = tr.GetObject(brId, OpenMode.ForRead) as BlockReference;
                            if (br == null || br.IsErased || br.AttributeCollection == null) { missingHandles++; continue; }

                            bool blkTouched = false;
                            foreach (ObjectId aid in br.AttributeCollection)
                            {
                                AttributeReference att = tr.GetObject(aid, OpenMode.ForRead) as AttributeReference;
                                if (att == null || att.IsErased) continue;
                                string tag = (att.Tag ?? "").Trim();
                                string newVal;
                                if (!kv.Value.TryGetValue(tag, out newVal)) continue;
                                if (string.Equals(att.TextString ?? "", newVal ?? "", StringComparison.Ordinal)) continue;

                                CaptureEntitySnapshot(att, snapById);
                                att.UpgradeOpen();
                                att.TextString = newVal ?? "";
                                att.RecordGraphicsModified(true);
                                updatedAttrs++;
                                blkTouched = true;
                            }
                            if (blkTouched)
                            {
                                try { br.RecordGraphicsModified(true); } catch { }
                                updatedBlocks++;
                            }
                        }
                        tr.Commit();
                    }

                    if (snapById.Count > 0)
                    {
                        CadUndoEntry undo = new CadUndoEntry();
                        undo.ToolName = "manage_attributes";
                        undo.Summary = "Synced attributes from Excel (" + updatedAttrs.ToString(CultureInfo.InvariantCulture) + " tags)";
                        undo.ModifiedSnapshots.AddRange(snapById.Values);
                        PushUndoEntry(doc, undo);
                    }
                    doc.Editor.Regen();
                    doc.Editor.UpdateScreen();
                }

                return Ok(new Dictionary<string, object>
                {
                    { "action", "sync_from_excel" },
                    { "file_path", filePath },
                    { "updated_blocks", updatedBlocks },
                    { "updated_attributes", updatedAttrs },
                    { "missing_handles", missingHandles },
                    { "undo_history_steps", GetUndoHistoryCount(doc) }
                });
            }
            else if (action == "update")
            {
                string singleTag = GetStr(args, "tag");
                string singleValue = GetStr(args, "value");
                Dictionary<string, string> bulkTagMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                if (!string.IsNullOrWhiteSpace(singleTag) && singleValue != null)
                    bulkTagMap[singleTag.Trim()] = singleValue;

                object rawAttrMap;
                if (args.TryGetValue("attributes", out rawAttrMap) && rawAttrMap is Dictionary<string, object>)
                {
                    foreach (KeyValuePair<string, object> kv in (Dictionary<string, object>)rawAttrMap)
                        bulkTagMap[kv.Key.Trim()] = kv.Value != null ? Convert.ToString(kv.Value, CultureInfo.InvariantCulture) : "";
                }

                object rawUpdates;
                if (args.TryGetValue("updates", out rawUpdates) && rawUpdates is System.Collections.IEnumerable && !(rawUpdates is string))
                {
                    foreach (object item in (System.Collections.IEnumerable)rawUpdates)
                    {
                        Dictionary<string, object> d = item as Dictionary<string, object>;
                        if (d == null) continue;
                        string tKey = GetStr(d, "tag");
                        string tVal = GetStr(d, "value");
                        if (!string.IsNullOrWhiteSpace(tKey) && tVal != null)
                            bulkTagMap[tKey.Trim()] = tVal;
                    }
                }

                if (bulkTagMap.Count == 0)
                    return Err("يرجى تحديد tag و value أو updates لتحديث قيم البلوكات.");

                Dictionary<ObjectId, ModifiedEntitySnapshot> snapById = new Dictionary<ObjectId, ModifiedEntitySnapshot>();
                int updatedBlocks = 0;
                int updatedAttrs = 0;

                using (DocumentLock docLock = doc.LockDocument())
                {
                    using (Transaction tr = db.TransactionManager.StartTransaction())
                    {
                        BlockTable bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
                        BlockTableRecord ms = (BlockTableRecord)tr.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForRead);
                        Dictionary<string, bool> layerHiddenFlags = BuildLayerHiddenMap(db, tr);
                        List<ObjectId> brIds = CollectMatchingBlockReferenceIds(doc, db, ms, tr, args, layerHiddenFlags);

                        for (int i = 0; i < brIds.Count; i++)
                        {
                            BlockReference br = tr.GetObject(brIds[i], OpenMode.ForRead) as BlockReference;
                            if (br == null || br.AttributeCollection == null) continue;
                            bool touched = false;
                            foreach (ObjectId aid in br.AttributeCollection)
                            {
                                AttributeReference att = tr.GetObject(aid, OpenMode.ForRead) as AttributeReference;
                                if (att == null || att.IsErased) continue;
                                string tag = (att.Tag ?? "").Trim();
                                string nv;
                                if (!bulkTagMap.TryGetValue(tag, out nv)) continue;
                                CaptureEntitySnapshot(att, snapById);
                                att.UpgradeOpen();
                                att.TextString = nv;
                                att.RecordGraphicsModified(true);
                                updatedAttrs++;
                                touched = true;
                            }
                            if (touched) updatedBlocks++;
                        }
                        tr.Commit();
                    }
                    if (snapById.Count > 0)
                    {
                        CadUndoEntry undo = new CadUndoEntry();
                        undo.ToolName = "manage_attributes";
                        undo.Summary = "Updated " + updatedAttrs.ToString(CultureInfo.InvariantCulture) + " attribute(s)";
                        undo.ModifiedSnapshots.AddRange(snapById.Values);
                        PushUndoEntry(doc, undo);
                    }
                    doc.Editor.Regen();
                    doc.Editor.UpdateScreen();
                }

                return Ok(new Dictionary<string, object>
                {
                    { "action", "update" },
                    { "updated_blocks", updatedBlocks },
                    { "updated_attributes", updatedAttrs },
                    { "undo_history_steps", GetUndoHistoryCount(doc) }
                });
            }

            return Err("Unknown manage_attributes action '" + action + "'. Use 'extract', 'update', or 'sync_from_excel'.");
        }

        private static Dictionary<string, object> TextTools(Document doc, Dictionary<string, object> args)
        {
            Database db = doc.Database;
            string action = (GetStr(args, "action") ?? "find_replace").Trim().ToLowerInvariant();
            string layerFilter = GetStr(args, "layer_filter");
            bool selectedOnly = GetBool(args, "selected_only", false);
            double[] bbox = GetDoubleArray(args, "bbox");
            List<string> explicitHandles = ExtractStringListArg(args, "handles");
            HashSet<string> targetSet = new HashSet<string>(explicitHandles, StringComparer.OrdinalIgnoreCase);
            if (selectedOnly)
            {
                List<string> sel = GetSelectedHandleStrings(doc);
                for (int i = 0; i < sel.Count; i++) targetSet.Add(sel[i]);
            }

            if (action == "find_replace")
            {
                string findText = GetStr(args, "find");
                if (string.IsNullOrEmpty(findText))
                    return Err("'find' text pattern is required for text_tools(action='find_replace').");
                string replaceText = GetStr(args, "replace") ?? "";
                bool useRegex = GetBool(args, "use_regex", false);
                bool matchCase = GetBool(args, "match_case", false);
                bool includeAttributes = GetBool(args, "include_attributes", true);
                bool previewOnly = GetBool(args, "preview_only", false);

                RegexOptions rxOpts = matchCase ? RegexOptions.None : RegexOptions.IgnoreCase;
                Regex rx = useRegex
                    ? new Regex(findText, rxOpts)
                    : new Regex(Regex.Escape(findText), rxOpts);

                Dictionary<ObjectId, ModifiedEntitySnapshot> snapById = new Dictionary<ObjectId, ModifiedEntitySnapshot>();
                List<ObjectId> matchedEntityIds = new List<ObjectId>();
                List<object> samples = new List<object>();

                using (DocumentLock docLock = doc.LockDocument())
                {
                    using (Transaction tr = db.TransactionManager.StartTransaction())
                    {
                        BlockTable bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
                        BlockTableRecord ms = (BlockTableRecord)tr.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForRead);
                        foreach (ObjectId id in ms)
                        {
                            if (id.IsErased) continue;
                            Entity ent = tr.GetObject(id, OpenMode.ForRead) as Entity;
                            if (ent == null || ent.IsErased || IsAiAnnotationLayer(ent.Layer)) continue;
                            if ((selectedOnly || explicitHandles.Count > 0) && !targetSet.Contains(ent.Handle.ToString())) continue;
                            if (!string.IsNullOrWhiteSpace(layerFilter) && !MatchesPatternOrSubstring(ent.Layer ?? "", layerFilter)) continue;

                            if (bbox != null && bbox.Length >= 4)
                            {
                                try
                                {
                                    Extents3d ex = ent.GeometricExtents;
                                    double cx = (ex.MinPoint.X + ex.MaxPoint.X) * 0.5;
                                    double cy = (ex.MinPoint.Y + ex.MaxPoint.Y) * 0.5;
                                    if (cx < bbox[0] || cx > bbox[2] || cy < bbox[1] || cy > bbox[3]) continue;
                                }
                                catch { continue; }
                            }

                            if (ent is DBText)
                            {
                                DBText dt = (DBText)ent;
                                string oldVal = dt.TextString ?? "";
                                if (rx.IsMatch(oldVal))
                                {
                                    string newVal = rx.Replace(oldVal, replaceText);
                                    matchedEntityIds.Add(id);
                                    if (samples.Count < 25)
                                        samples.Add(new Dictionary<string, object> { { "handle", ent.Handle.ToString() }, { "old", oldVal }, { "new", newVal } });
                                    if (!previewOnly && newVal != oldVal)
                                    {
                                        CaptureEntitySnapshot(dt, snapById);
                                        dt.UpgradeOpen();
                                        dt.TextString = newVal;
                                        dt.RecordGraphicsModified(true);
                                    }
                                }
                            }
                            else if (ent is MText)
                            {
                                MText mt = (MText)ent;
                                string plain = ExtractEntityTextString(mt);
                                string rawCont = mt.Contents ?? "";
                                if (rx.IsMatch(rawCont) || rx.IsMatch(plain))
                                {
                                    string newVal = rx.IsMatch(rawCont) ? rx.Replace(rawCont, replaceText) : rx.Replace(plain, replaceText);
                                    matchedEntityIds.Add(id);
                                    if (samples.Count < 25)
                                        samples.Add(new Dictionary<string, object> { { "handle", ent.Handle.ToString() }, { "old", plain }, { "new", CleanCadTextValue(newVal) } });
                                    if (!previewOnly)
                                    {
                                        CaptureEntitySnapshot(mt, snapById);
                                        mt.UpgradeOpen();
                                        mt.Contents = newVal;
                                        mt.RecordGraphicsModified(true);
                                    }
                                }
                            }
                            else if (includeAttributes && ent is BlockReference)
                            {
                                BlockReference br = (BlockReference)ent;
                                if (br.AttributeCollection != null)
                                {
                                    foreach (ObjectId aid in br.AttributeCollection)
                                    {
                                        AttributeReference att = tr.GetObject(aid, OpenMode.ForRead) as AttributeReference;
                                        if (att == null || att.IsErased) continue;
                                        string oldVal = att.TextString ?? "";
                                        if (rx.IsMatch(oldVal))
                                        {
                                            string newVal = rx.Replace(oldVal, replaceText);
                                            matchedEntityIds.Add(id);
                                            if (samples.Count < 25)
                                                samples.Add(new Dictionary<string, object> { { "handle", att.Handle.ToString() }, { "tag", att.Tag }, { "old", oldVal }, { "new", newVal } });
                                            if (!previewOnly && newVal != oldVal)
                                            {
                                                CaptureEntitySnapshot(att, snapById);
                                                att.UpgradeOpen();
                                                att.TextString = newVal;
                                                att.RecordGraphicsModified(true);
                                            }
                                        }
                                    }
                                }
                            }
                        }
                        tr.Commit();
                    }

                    if (!previewOnly && snapById.Count > 0)
                    {
                        CadUndoEntry undo = new CadUndoEntry();
                        undo.ToolName = "text_tools";
                        undo.Summary = "Find/Replace '" + findText + "' -> '" + replaceText + "' (" + snapById.Count.ToString(CultureInfo.InvariantCulture) + ")";
                        undo.ModifiedSnapshots.AddRange(snapById.Values);
                        PushUndoEntry(doc, undo);
                    }
                    doc.Editor.Regen();
                    doc.Editor.UpdateScreen();
                }

                return Ok(new Dictionary<string, object>
                {
                    { "action", "find_replace" },
                    { "preview_only", previewOnly },
                    { "matched_count", matchedEntityIds.Count },
                    { "modified_count", snapById.Count },
                    { "samples", samples },
                    { "undo_history_steps", GetUndoHistoryCount(doc) }
                });
            }
            else if (action == "auto_number")
            {
                string targetType = (GetStr(args, "target_type") ?? "auto").Trim().ToLowerInvariant();
                string prefix = GetStr(args, "prefix") ?? "";
                string suffix = GetStr(args, "suffix") ?? "";
                int startNum = GetInt(args, "start", 1);
                int stepNum = Math.Max(1, GetInt(args, "step", 1));
                int padDigits = Math.Max(1, Math.Min(6, GetInt(args, "pad_digits", 1)));
                string numberMode = (GetStr(args, "mode") ?? "replace").Trim().ToLowerInvariant();
                string attrTag = GetStr(args, "attribute_tag");
                string sortOrder = NormalizeCoordSortOrder(GetStr(args, "sort_order") ?? "left_to_right");
                if (string.IsNullOrEmpty(sortOrder)) sortOrder = "left_to_right";
                string blockFilter = GetStr(args, "block_name");

                List<CoordTargetItem> items = new List<CoordTargetItem>();
                List<double> ws = new List<double>();
                List<double> hs = new List<double>();
                Dictionary<ObjectId, ModifiedEntitySnapshot> snapById = new Dictionary<ObjectId, ModifiedEntitySnapshot>();
                List<ObjectId> createdIds = new List<ObjectId>();

                using (DocumentLock docLock = doc.LockDocument())
                {
                    using (Transaction tr = db.TransactionManager.StartTransaction())
                    {
                        BlockTable bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
                        BlockTableRecord ms = (BlockTableRecord)tr.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForWrite);
                        foreach (ObjectId id in ms)
                        {
                            if (id.IsErased) continue;
                            Entity ent = tr.GetObject(id, OpenMode.ForRead) as Entity;
                            if (ent == null || ent.IsErased || IsAiAnnotationLayer(ent.Layer)) continue;
                            if ((selectedOnly || explicitHandles.Count > 0) && !targetSet.Contains(ent.Handle.ToString())) continue;
                            if (!string.IsNullOrWhiteSpace(layerFilter) && !MatchesPatternOrSubstring(ent.Layer ?? "", layerFilter)) continue;

                            bool isText = ent is DBText || ent is MText;
                            bool isBlk = ent is BlockReference;
                            if (targetType == "texts" && !isText) continue;
                            if (targetType == "blocks" && !isBlk) continue;
                            if (targetType == "auto" && !isText && !isBlk) continue;

                            if (isBlk)
                            {
                                string bn = ResolveBlockName(tr, (BlockReference)ent);
                                if (IsAnonymousBlockName(bn) || IsSystemBlockName(bn)) continue;
                                if (!string.IsNullOrWhiteSpace(blockFilter) && !MatchesPatternOrSubstring(bn, blockFilter)) continue;
                            }

                            Extents3d ex;
                            try { ex = ent.GeometricExtents; }
                            catch { continue; }
                            if (!IsFiniteBounds(ex)) continue;
                            if (bbox != null && bbox.Length >= 4)
                            {
                                double cx0 = (ex.MinPoint.X + ex.MaxPoint.X) * 0.5;
                                double cy0 = (ex.MinPoint.Y + ex.MaxPoint.Y) * 0.5;
                                if (cx0 < bbox[0] || cx0 > bbox[2] || cy0 < bbox[1] || cy0 > bbox[3]) continue;
                            }

                            ws.Add(Math.Max(1e-6, ex.MaxPoint.X - ex.MinPoint.X));
                            hs.Add(Math.Max(1e-6, ex.MaxPoint.Y - ex.MinPoint.Y));
                            items.Add(new CoordTargetItem
                            {
                                Id = id,
                                Handle = ent.Handle.ToString(),
                                ArrowX = (ex.MinPoint.X + ex.MaxPoint.X) * 0.5,
                                ArrowY = (ex.MinPoint.Y + ex.MaxPoint.Y) * 0.5,
                                Bounds = ex
                            });
                        }

                        if (items.Count == 0)
                        {
                            tr.Commit();
                            return Err("لم يتم العثور على نصوص أو بلوكات للترقيم التلقائي.");
                        }

                        ws.Sort();
                        hs.Sort();
                        double medW = ws[ws.Count / 2];
                        double medH = hs[hs.Count / 2];
                        SortCoordTargets(items, sortOrder, medW, medH);

                        ObjectId unicodeStyleId = EnsureUnicodeTextStyle(db, tr);
                        string numFmt = new string('0', padDigits);
                        double unitsPerCm = Convert.ToDouble(GetUnitsInfo(db)["units_per_cm"], CultureInfo.InvariantCulture);
                        double labelH = Math.Max(0.4 * unitsPerCm, Math.Min(medH * 0.55, 5.0 * unitsPerCm));
                        string labelLayer = EnsureLayerSafe(db, tr, "_AI_AUTO_NUMBERS", 2, "Continuous");

                        for (int i = 0; i < items.Count; i++)
                        {
                            int nVal = startNum + i * stepNum;
                            string token = prefix + nVal.ToString(numFmt, CultureInfo.InvariantCulture) + suffix;
                            Entity ent = tr.GetObject(items[i].Id, OpenMode.ForRead) as Entity;
                            if (ent is DBText)
                            {
                                DBText dt = (DBText)ent;
                                CaptureEntitySnapshot(dt, snapById);
                                dt.UpgradeOpen();
                                string old = dt.TextString ?? "";
                                if (numberMode == "prepend") dt.TextString = token + " " + old;
                                else if (numberMode == "append") dt.TextString = old + " " + token;
                                else dt.TextString = token;
                                dt.RecordGraphicsModified(true);
                            }
                            else if (ent is MText)
                            {
                                MText mt = (MText)ent;
                                CaptureEntitySnapshot(mt, snapById);
                                mt.UpgradeOpen();
                                string old = ExtractEntityTextString(mt);
                                if (numberMode == "prepend") mt.Contents = token + " " + old;
                                else if (numberMode == "append") mt.Contents = old + " " + token;
                                else mt.Contents = token;
                                mt.RecordGraphicsModified(true);
                            }
                            else if (ent is BlockReference)
                            {
                                BlockReference br = (BlockReference)ent;
                                bool filledAttr = false;
                                if (!string.IsNullOrWhiteSpace(attrTag) && br.AttributeCollection != null)
                                {
                                    foreach (ObjectId aid in br.AttributeCollection)
                                    {
                                        AttributeReference att = tr.GetObject(aid, OpenMode.ForRead) as AttributeReference;
                                        if (att != null && !att.IsErased && string.Equals((att.Tag ?? "").Trim(), attrTag.Trim(), StringComparison.OrdinalIgnoreCase))
                                        {
                                            CaptureEntitySnapshot(att, snapById);
                                            att.UpgradeOpen();
                                            att.TextString = token;
                                            att.RecordGraphicsModified(true);
                                            filledAttr = true;
                                        }
                                    }
                                }
                                if (!filledAttr)
                                {
                                    MText tagMt = new MText();
                                    tagMt.SetDatabaseDefaults(db);
                                    tagMt.Layer = labelLayer;
                                    tagMt.ColorIndex = 2;
                                    tagMt.TextStyleId = unicodeStyleId;
                                    tagMt.TextHeight = labelH;
                                    tagMt.Attachment = AttachmentPoint.BottomCenter;
                                    tagMt.Location = new Point3d(items[i].ArrowX, items[i].Bounds.MaxPoint.Y + labelH * 0.4, 0.0);
                                    tagMt.Contents = EscapeMTextPlain(token);
                                    ms.AppendEntity(tagMt);
                                    tr.AddNewlyCreatedDBObject(tagMt, true);
                                    createdIds.Add(tagMt.ObjectId);
                                }
                            }
                        }

                        db.UpdateExt(true);
                        tr.Commit();
                    }

                    if (snapById.Count > 0 || createdIds.Count > 0)
                    {
                        CadUndoEntry undo = new CadUndoEntry();
                        undo.ToolName = "text_tools";
                        undo.Summary = "Auto-numbered " + items.Count.ToString(CultureInfo.InvariantCulture) + " items (" + sortOrder + ")";
                        undo.ModifiedSnapshots.AddRange(snapById.Values);
                        undo.CreatedIds.AddRange(createdIds);
                        PushUndoEntry(doc, undo);
                    }
                    doc.Editor.Regen();
                    doc.Editor.UpdateScreen();
                }

                return Ok(new Dictionary<string, object>
                {
                    { "action", "auto_number" },
                    { "numbered_count", items.Count },
                    { "sort_order", sortOrder },
                    { "modified_count", snapById.Count },
                    { "created_labels_count", createdIds.Count },
                    { "undo_history_steps", GetUndoHistoryCount(doc) }
                });
            }
            else if (action == "standardize")
            {
                double? reqH = GetNullableDouble(args, "text_height");
                int? reqColor = GetNullableInt(args, "color_index");
                bool useUnicode = GetBool(args, "use_unicode_style", true);

                Dictionary<ObjectId, ModifiedEntitySnapshot> snapById = new Dictionary<ObjectId, ModifiedEntitySnapshot>();
                using (DocumentLock docLock = doc.LockDocument())
                {
                    using (Transaction tr = db.TransactionManager.StartTransaction())
                    {
                        ObjectId unicodeStyleId = useUnicode ? EnsureUnicodeTextStyle(db, tr) : ObjectId.Null;
                        BlockTable bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
                        BlockTableRecord ms = (BlockTableRecord)tr.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForRead);
                        foreach (ObjectId id in ms)
                        {
                            if (id.IsErased) continue;
                            Entity ent = tr.GetObject(id, OpenMode.ForRead) as Entity;
                            if (ent == null || ent.IsErased || IsAiAnnotationLayer(ent.Layer)) continue;
                            if (!(ent is DBText) && !(ent is MText)) continue;
                            if ((selectedOnly || explicitHandles.Count > 0) && !targetSet.Contains(ent.Handle.ToString())) continue;
                            if (!string.IsNullOrWhiteSpace(layerFilter) && !MatchesPatternOrSubstring(ent.Layer ?? "", layerFilter)) continue;

                            CaptureEntitySnapshot(ent, snapById);
                            ent.UpgradeOpen();
                            if (reqColor.HasValue) ent.ColorIndex = ClampColor(reqColor.Value);
                            if (ent is DBText)
                            {
                                DBText dt = (DBText)ent;
                                if (useUnicode && !unicodeStyleId.IsNull) dt.TextStyleId = unicodeStyleId;
                                if (reqH.HasValue && reqH.Value > 1e-6) dt.Height = reqH.Value;
                            }
                            else if (ent is MText)
                            {
                                MText mt = (MText)ent;
                                if (useUnicode && !unicodeStyleId.IsNull) mt.TextStyleId = unicodeStyleId;
                                if (reqH.HasValue && reqH.Value > 1e-6) mt.TextHeight = reqH.Value;
                            }
                            ent.RecordGraphicsModified(true);
                        }
                        tr.Commit();
                    }
                    if (snapById.Count > 0)
                    {
                        CadUndoEntry undo = new CadUndoEntry();
                        undo.ToolName = "text_tools";
                        undo.Summary = "Standardized " + snapById.Count.ToString(CultureInfo.InvariantCulture) + " texts";
                        undo.ModifiedSnapshots.AddRange(snapById.Values);
                        PushUndoEntry(doc, undo);
                    }
                    doc.Editor.Regen();
                    doc.Editor.UpdateScreen();
                }

                return Ok(new Dictionary<string, object>
                {
                    { "action", "standardize" },
                    { "standardized_count", snapById.Count },
                    { "undo_history_steps", GetUndoHistoryCount(doc) }
                });
            }

            return Err("Unknown text_tools action '" + action + "'. Use 'find_replace', 'auto_number', or 'standardize'.");
        }
    }
}
