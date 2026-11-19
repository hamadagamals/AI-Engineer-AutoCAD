using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Web.Script.Serialization;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.Runtime;

[assembly: ExtensionApplication(typeof(AIEngineer.AutoCAD.ContextExtension))]
[assembly: CommandClass(typeof(AIEngineer.AutoCAD.ContextCommands))]

namespace AIEngineer.AutoCAD
{
    internal sealed class ContextEvidence
    {
        public readonly string ContextId, Sha256, SourceMode;
        public readonly int EntityCount, DatabaseModified;
        public readonly double DurationMilliseconds;
        public readonly bool Truncated;

        public ContextEvidence(string contextId, string sha256, string sourceMode, int entityCount,
            int databaseModified, double durationMilliseconds, bool truncated)
        {
            ContextId = contextId; Sha256 = sha256; SourceMode = sourceMode;
            EntityCount = entityCount; DatabaseModified = databaseModified;
            DurationMilliseconds = durationMilliseconds; Truncated = truncated;
        }
    }

    internal static class ContextEvidenceStore
    {
        private static readonly object Sync = new object();
        private static ContextEvidence _latest;

        public static void Set(ContextEvidence value)
        { lock (Sync) _latest = value; }

        public static ContextEvidence Get()
        { lock (Sync) return _latest; }
    }

    public sealed class ContextExtension : IExtensionApplication
    {
        public void Initialize()
        {
            ConnectionReviewChannel.Start();
            OperatorCommandChannel.Start();
            OperatorCommandChannel.TryPublishNow();
        }
        public void Terminate()
        {
            OperatorCommandChannel.Stop();
            ConnectionReviewChannel.Stop();
            DrawingChangeTracker.Stop();
        }
    }

    public sealed class ContextCommands
    {
        [CommandMethod("AIENGOPERATORPULSE", CommandFlags.NoHistory)]
        public void PublishOperatorStatus()
        {
            Document document = Application.DocumentManager.MdiActiveDocument;
            if (document == null) return;
            if (OperatorCommandChannel.TryPublishNow())
                document.Editor.WriteMessage(
                    "\nAI-Engineer read-only operator status published.\n");
            else
                document.Editor.WriteMessage(
                    "\nAI-Engineer operator status failed safely.\n");
        }

        [CommandMethod("AIENGCONTEXT", CommandFlags.UsePickSet | CommandFlags.Redraw)]
        public void ExportExplicitSelection()
        {
            Document document = Application.DocumentManager.MdiActiveDocument;
            if (document == null) return;
            Editor editor = document.Editor;
            PromptSelectionResult selected = editor.SelectImplied();
            if (selected.Status != PromptStatus.OK || selected.Value == null || selected.Value.Count == 0)
            {
                selected = editor.GetSelection();
            }
            if (selected.Status != PromptStatus.OK || selected.Value == null || selected.Value.Count == 0)
            {
                editor.WriteMessage("\nAI-Engineer: explicit selection was cancelled or empty.");
                return;
            }
            Export(document, "selection", selected.Value.GetObjectIds());
        }

        [CommandMethod("AIENGDRAWINGCONTEXT", CommandFlags.Redraw)]
        public void ExportActiveDrawing()
        {
            Document document = Application.DocumentManager.MdiActiveDocument;
            if (document == null) return;
            ExportActiveDrawingForChannel(document);
        }

        internal static ContextEvidence ExportActiveDrawingForChannel(
            Document document, Func<bool> cancellationRequested = null)
        {
            List<ObjectId> identifiers = new List<ObjectId>();
            using (Transaction transaction = document.Database.TransactionManager.StartOpenCloseTransaction())
            {
                BlockTableRecord space = transaction.GetObject(
                    document.Database.CurrentSpaceId, OpenMode.ForRead, false) as BlockTableRecord;
                if (space != null)
                {
                    foreach (ObjectId identifier in space) identifiers.Add(identifier);
                }
                transaction.Commit();
            }
            return Export(document, "drawing", identifiers.ToArray(), cancellationRequested);
        }

        internal static ContextEvidence ExportActiveDrawingIndexForChannel(
            Document document, Func<bool> cancellationRequested = null)
        {
            Settings settings = Settings.Load();
            Stopwatch stopwatch = Stopwatch.StartNew();
            int databaseModifiedBefore = GetDbmod();
            List<ObjectId> identifiers = new List<ObjectId>();
            using (Transaction transaction = document.Database.TransactionManager.StartOpenCloseTransaction())
            {
                BlockTable blocks = transaction.GetObject(
                    document.Database.BlockTableId, OpenMode.ForRead, false) as BlockTable;
                if (blocks == null)
                    throw new InvalidOperationException("The drawing block table is unavailable.");
                foreach (ObjectId blockId in blocks)
                {
                    BlockTableRecord space = transaction.GetObject(
                        blockId, OpenMode.ForRead, false) as BlockTableRecord;
                    if (space == null || !space.IsLayout) continue;
                    foreach (ObjectId identifier in space) identifiers.Add(identifier);
                }
                transaction.Commit();
            }
            identifiers.Sort(delegate(ObjectId left, ObjectId right)
            { return left.Handle.Value.CompareTo(right.Handle.Value); });
            if (identifiers.Count > settings.MaximumIndexEntities)
                throw new InvalidOperationException(
                    "Drawing entity count exceeds the configured complete-index limit.");
            List<ContextEvidence> completed = new List<ContextEvidence>();
            try
            {
                int offset = 0;
                while (offset < identifiers.Count)
                {
                    if (cancellationRequested != null && cancellationRequested())
                        throw new OperationCanceledException(
                            "AutoCAD drawing-index cancellation was requested.");
                    if (stopwatch.Elapsed.TotalSeconds > settings.MaximumIndexDurationSeconds)
                        throw new InvalidOperationException(
                            "Drawing index exceeded the configured duration limit.");
                    if (completed.Count >= settings.MaximumIndexChunks)
                        throw new InvalidOperationException(
                            "Drawing chunk count exceeds the configured complete-index limit.");
                    int count = Math.Min(
                        settings.MaximumIndexEntitiesPerChunk, identifiers.Count - offset);
                    ContextEvidence evidence = null;
                    while (count >= 1)
                    {
                        ObjectId[] chunk = identifiers.GetRange(offset, count).ToArray();
                        evidence = Export(document, "drawing", chunk, cancellationRequested);
                        if (evidence != null && evidence.EntityCount == count && !evidence.Truncated)
                            break;
                        if (evidence != null) DeleteIndexContext(settings, evidence);
                        evidence = null;
                        if (count == 1)
                            throw new InvalidOperationException(
                                "One drawing entity exceeds the complete context limits.");
                        count = Math.Max(1, count / 2);
                    }
                    completed.Add(evidence);
                    offset += count;
                }

                int databaseModifiedAfter = GetDbmod();
                if (databaseModifiedAfter != databaseModifiedBefore)
                    throw new InvalidOperationException(
                        "DBMOD changed during read-only drawing indexing.");
                string indexId = Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture);
                ArrayList chunks = new ArrayList();
                for (int ordinal = 0; ordinal < completed.Count; ordinal++)
                {
                    ContextEvidence evidence = completed[ordinal];
                    chunks.Add(new Dictionary<string, object>
                    {
                        {"ordinal", ordinal},
                        {"context_id", evidence.ContextId},
                        {"context_sha256", evidence.Sha256},
                        {"entity_count", evidence.EntityCount}
                    });
                }
                string identitySource;
                Dictionary<string, object> manifest = new Dictionary<string, object>
                {
                    {"schema_version", 1},
                    {"index_version", "autocad.drawing_index.v1"},
                    {"index_id", indexId},
                    {"captured_at", DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture)},
                    {"document", new Dictionary<string, object>
                        {
                            {"document_id", GetDocumentId(document, out identitySource)},
                            {"identity_source", identitySource},
                            {"name", SafeText(Path.GetFileName(document.Name), settings.MaximumTextBytes)},
                            {"source_drawing_sha256", FileSha256(document.Name)},
                            {"drawing_units", (int)document.Database.Insunits},
                            {"database_modified_before", databaseModifiedBefore},
                            {"database_modified_after", databaseModifiedAfter}
                        }
                    },
                    {"scope", new Dictionary<string, object>
                        {
                            {"model_space", true}, {"paper_space", true},
                            {"nested_block_definitions", false}, {"external_references", false}
                        }
                    },
                    {"limits", new Dictionary<string, object>
                        {
                            {"maximum_entities_per_chunk", settings.MaximumIndexEntitiesPerChunk},
                            {"maximum_total_entities", settings.MaximumIndexEntities},
                            {"maximum_chunks", settings.MaximumIndexChunks},
                            {"maximum_duration_seconds", settings.MaximumIndexDurationSeconds}
                        }
                    },
                    {"complete", true}, {"chunk_count", completed.Count},
                    {"entity_count", identifiers.Count}, {"chunks", chunks},
                    {"drawing_mutation_performed", false}
                };
                JavaScriptSerializer serializer = new JavaScriptSerializer();
                serializer.MaxJsonLength = settings.MaximumPayloadBytes;
                byte[] content = Encoding.UTF8.GetBytes(serializer.Serialize(manifest));
                if (content.Length > settings.MaximumPayloadBytes)
                    throw new InvalidOperationException("Drawing-index manifest exceeds the payload limit.");
                string inbox = Path.GetFullPath(Path.Combine(
                    settings.WorkspaceRoot, ".ai-engineer", "autocad", "drawing-index", "inbox"));
                EnsureContained(inbox, settings.WorkspaceRoot);
                Directory.CreateDirectory(inbox);
                WriteBytesAtomic(Path.Combine(inbox, indexId + ".json"), content);
                string digest;
                using (SHA256 sha = SHA256.Create()) digest = ToHex(sha.ComputeHash(content));
                DrawingChangeTracker.SetBaseline(
                    document, indexId, digest, FileSha256(document.Name), identifiers);
                stopwatch.Stop();
                ContextEvidence result = new ContextEvidence(
                    indexId, digest, "drawing_index", identifiers.Count,
                    databaseModifiedAfter, stopwatch.Elapsed.TotalMilliseconds, false);
                ContextEvidenceStore.Set(result);
                document.Editor.WriteMessage("\nAIENG_INDEX_ID=" + indexId);
                document.Editor.WriteMessage("\nAIENG_INDEX_SHA256=" + digest);
                document.Editor.WriteMessage("\nAIENG_INDEX_CHUNKS=" + completed.Count.ToString(CultureInfo.InvariantCulture));
                document.Editor.WriteMessage("\nAIENG_INDEX_ENTITIES=" + identifiers.Count.ToString(CultureInfo.InvariantCulture));
                document.Editor.WriteMessage("\nAIENG_INDEX_DBMOD=" + databaseModifiedAfter.ToString(CultureInfo.InvariantCulture));
                document.Editor.WriteMessage("\nAIENG_INDEX_DURATION_MS=" +
                    stopwatch.Elapsed.TotalMilliseconds.ToString("F3", CultureInfo.InvariantCulture));
                document.Editor.WriteMessage("\nAIENG_INDEX_DONE=1\n");
                return result;
            }
            catch
            {
                CleanupIndexContexts(settings, completed);
                throw;
            }
        }

        internal static ContextEvidence ExportActiveDrawingDeltaForChannel(
            Document document, Func<bool> cancellationRequested = null)
        {
            Settings settings = Settings.Load();
            Stopwatch stopwatch = Stopwatch.StartNew();
            int databaseModifiedBefore = GetDbmod();
            DrawingDeltaSnapshot snapshot = DrawingChangeTracker.Snapshot(document);
            int candidateCount = snapshot.AddedHandles.Length +
                snapshot.ModifiedHandles.Length + snapshot.RemovedHandles.Length;
            if (candidateCount > 4096)
                throw new InvalidOperationException(
                    "Drawing delta exceeds 4096 candidates; run a complete index.");
            List<ObjectId> identifiers = new List<ObjectId>();
            foreach (string handle in snapshot.AddedHandles)
                identifiers.Add(document.Database.GetObjectId(
                    false, new Handle(Convert.ToInt64(handle, 16)), 0));
            foreach (string handle in snapshot.ModifiedHandles)
                identifiers.Add(document.Database.GetObjectId(
                    false, new Handle(Convert.ToInt64(handle, 16)), 0));
            identifiers.Sort(delegate(ObjectId left, ObjectId right)
            { return left.Handle.Value.CompareTo(right.Handle.Value); });
            List<ContextEvidence> completed = new List<ContextEvidence>();
            try
            {
                int offset = 0;
                while (offset < identifiers.Count)
                {
                    if (cancellationRequested != null && cancellationRequested())
                        throw new OperationCanceledException(
                            "AutoCAD drawing-delta cancellation was requested.");
                    if (stopwatch.Elapsed.TotalSeconds > settings.MaximumIndexDurationSeconds)
                        throw new InvalidOperationException(
                            "Drawing delta exceeded the configured duration limit.");
                    int count = Math.Min(
                        settings.MaximumIndexEntitiesPerChunk, identifiers.Count - offset);
                    ContextEvidence evidence = null;
                    while (count >= 1)
                    {
                        evidence = Export(document, "drawing",
                            identifiers.GetRange(offset, count).ToArray(), cancellationRequested);
                        if (evidence != null && evidence.EntityCount == count && !evidence.Truncated)
                            break;
                        if (evidence != null) DeleteIndexContext(settings, evidence);
                        evidence = null;
                        if (count == 1)
                            throw new InvalidOperationException(
                                "One changed entity exceeds the complete context limits.");
                        count = Math.Max(1, count / 2);
                    }
                    completed.Add(evidence); offset += count;
                }
                int databaseModifiedAfter = GetDbmod();
                if (databaseModifiedAfter != databaseModifiedBefore)
                    throw new InvalidOperationException(
                        "DBMOD changed during read-only drawing-delta capture.");
                string identitySource;
                string deltaId = Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture);
                ArrayList chunks = new ArrayList();
                for (int ordinal = 0; ordinal < completed.Count; ordinal++)
                {
                    ContextEvidence evidence = completed[ordinal];
                    chunks.Add(new Dictionary<string, object>
                    {
                        {"ordinal", ordinal}, {"context_id", evidence.ContextId},
                        {"context_sha256", evidence.Sha256},
                        {"entity_count", evidence.EntityCount}
                    });
                }
                Dictionary<string, object> manifest = new Dictionary<string, object>
                {
                    {"schema_version", 1},
                    {"delta_version", "autocad.drawing_delta.v1"},
                    {"delta_id", deltaId},
                    {"captured_at", DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture)},
                    {"document", new Dictionary<string, object>
                        {
                            {"document_id", GetDocumentId(document, out identitySource)},
                            {"identity_source", identitySource},
                            {"name", SafeText(Path.GetFileName(document.Name), settings.MaximumTextBytes)},
                            {"drawing_units", (int)document.Database.Insunits},
                            {"baseline_source_drawing_sha256", snapshot.BaselineSourceSha256},
                            {"current_source_drawing_sha256", FileSha256(document.Name)},
                            {"database_modified_before", databaseModifiedBefore},
                            {"database_modified_after", databaseModifiedAfter}
                        }
                    },
                    {"baseline", new Dictionary<string, object>
                        {
                            {"index_id", snapshot.BaselineIndexId},
                            {"index_sha256", snapshot.BaselineIndexSha256},
                            {"entity_count", snapshot.BaselineEntityCount}
                        }
                    },
                    {"complete_in_session_journal", true}, {"one_use", true},
                    {"maximum_candidates", 4096},
                    {"added_object_ids", snapshot.AddedHandles},
                    {"modified_object_ids", snapshot.ModifiedHandles},
                    {"removed_object_ids", snapshot.RemovedHandles},
                    {"candidate_count", candidateCount},
                    {"context_entity_count", identifiers.Count},
                    {"chunk_count", completed.Count}, {"chunks", chunks},
                    {"drawing_mutation_performed", false}
                };
                JavaScriptSerializer serializer = new JavaScriptSerializer();
                serializer.MaxJsonLength = settings.MaximumPayloadBytes;
                byte[] content = Encoding.UTF8.GetBytes(serializer.Serialize(manifest));
                if (content.Length > settings.MaximumPayloadBytes)
                    throw new InvalidOperationException("Drawing-delta manifest exceeds the payload limit.");
                string inbox = Path.GetFullPath(Path.Combine(
                    settings.WorkspaceRoot, ".ai-engineer", "autocad", "drawing-delta", "inbox"));
                EnsureContained(inbox, settings.WorkspaceRoot); Directory.CreateDirectory(inbox);
                WriteBytesAtomic(Path.Combine(inbox, deltaId + ".json"), content);
                string digest;
                using (SHA256 sha = SHA256.Create()) digest = ToHex(sha.ComputeHash(content));
                DrawingChangeTracker.Complete(document);
                stopwatch.Stop();
                return new ContextEvidence(
                    deltaId, digest, "drawing_delta", candidateCount,
                    databaseModifiedAfter, stopwatch.Elapsed.TotalMilliseconds, false);
            }
            catch
            {
                CleanupIndexContexts(settings, completed); throw;
            }
        }

        [CommandMethod("AIENGDEVICETAGCONTEXT", CommandFlags.UsePickSet | CommandFlags.Redraw)]
        public void ExportDeviceAndTagSelection()
        {
            Document document = Application.DocumentManager.MdiActiveDocument;
            if (document == null) return;
            Editor editor = document.Editor;
            PromptSelectionResult selected = editor.SelectImplied();
            if (selected.Status != PromptStatus.OK || selected.Value == null || selected.Value.Count == 0)
                selected = editor.GetSelection();
            if (selected.Status != PromptStatus.OK || selected.Value == null || selected.Value.Count == 0)
            {
                editor.WriteMessage("\nAI-Engineer: device/tag selection was cancelled or empty.");
                return;
            }
            List<ObjectId> identifiers = new List<ObjectId>();
            using (Transaction transaction = document.Database.TransactionManager.StartOpenCloseTransaction())
            {
                foreach (ObjectId identifier in selected.Value.GetObjectIds())
                {
                    if (identifier.IsNull || identifier.IsErased || !identifier.IsValid) continue;
                    Entity entity = transaction.GetObject(identifier, OpenMode.ForRead, false) as Entity;
                    if (entity is BlockReference || entity is DBText || entity is MText)
                        identifiers.Add(identifier);
                }
                transaction.Commit();
            }
            if (identifiers.Count == 0)
            {
                editor.WriteMessage("\nAI-Engineer: selection contains no supported blocks or text tags.");
                return;
            }
            Export(document, "selection", identifiers.ToArray());
        }

        [CommandMethod("AIENGROUTECONTEXT", CommandFlags.UsePickSet | CommandFlags.Redraw)]
        public void ExportRouteSelection()
        {
            Document document = Application.DocumentManager.MdiActiveDocument;
            if (document == null) return;
            Editor editor = document.Editor;
            PromptSelectionResult selected = editor.SelectImplied();
            if (selected.Status != PromptStatus.OK || selected.Value == null || selected.Value.Count == 0)
                selected = editor.GetSelection();
            if (selected.Status != PromptStatus.OK || selected.Value == null || selected.Value.Count == 0)
            {
                editor.WriteMessage("\nAI-Engineer: route selection was cancelled or empty.");
                return;
            }
            PromptEntityOptions referenceOptions = new PromptEntityOptions(
                "\nSelect one representative LINE or lightweight POLYLINE on the intended route layer: ");
            referenceOptions.SetRejectMessage("\nAI-Engineer: select a LINE or lightweight POLYLINE.");
            referenceOptions.AddAllowedClass(typeof(Line), false);
            referenceOptions.AddAllowedClass(typeof(Autodesk.AutoCAD.DatabaseServices.Polyline), false);
            PromptEntityResult referenceResult = editor.GetEntity(referenceOptions);
            if (referenceResult.Status != PromptStatus.OK)
            {
                editor.WriteMessage("\nAI-Engineer: route-layer reference was cancelled.");
                return;
            }
            List<ObjectId> identifiers = new List<ObjectId>();
            using (Transaction transaction = document.Database.TransactionManager.StartOpenCloseTransaction())
            {
                Entity reference = transaction.GetObject(
                    referenceResult.ObjectId, OpenMode.ForRead, false) as Entity;
                if (reference == null) return;
                ObjectId routeLayerId = reference.LayerId;
                foreach (ObjectId identifier in selected.Value.GetObjectIds())
                {
                    if (identifier.IsNull || identifier.IsErased || !identifier.IsValid) continue;
                    Entity entity = transaction.GetObject(identifier, OpenMode.ForRead, false) as Entity;
                    if (entity != null && entity.LayerId == routeLayerId &&
                        (entity is Line || entity is Autodesk.AutoCAD.DatabaseServices.Polyline))
                        identifiers.Add(identifier);
                }
                transaction.Commit();
            }
            if (identifiers.Count == 0)
            {
                editor.WriteMessage("\nAI-Engineer: selection contains no supported routes on the chosen layer.");
                return;
            }
            Export(document, "selection", identifiers.ToArray());
        }

        [CommandMethod("AIENGCONNECTIONANCHOR", CommandFlags.Redraw)]
        public void CaptureConnectionAnchor()
        {
            Document document = Application.DocumentManager.MdiActiveDocument;
            if (document == null) return;
            Editor editor = document.Editor;
            int commandStartDbmod = GetDbmod();
            if ((commandStartDbmod & ~(8 | 16)) != 0)
            {
                editor.WriteMessage(
                    "\nAI-Engineer: connection-anchor capture requires clean or presentation-only DBMOD (8/16); current DBMOD=" +
                    commandStartDbmod.ToString(CultureInfo.InvariantCulture) + ".");
                return;
            }
            PromptEntityOptions deviceOptions = new PromptEntityOptions(
                "\nSelect the electrical device block for this connection: ");
            deviceOptions.SetRejectMessage("\nAI-Engineer: select a block reference.");
            deviceOptions.AddAllowedClass(typeof(BlockReference), false);
            PromptEntityResult deviceResult = editor.GetEntity(deviceOptions);
            if (deviceResult.Status != PromptStatus.OK)
            {
                editor.WriteMessage("\nAI-Engineer: connection-anchor device selection was cancelled.");
                return;
            }

            PromptEntityOptions routeOptions = new PromptEntityOptions(
                "\nSelect the exact LINE or lightweight POLYLINE route: ");
            routeOptions.SetRejectMessage("\nAI-Engineer: select a LINE or lightweight POLYLINE.");
            routeOptions.AddAllowedClass(typeof(Line), false);
            routeOptions.AddAllowedClass(typeof(Autodesk.AutoCAD.DatabaseServices.Polyline), false);
            PromptEntityResult routeResult = editor.GetEntity(routeOptions);
            if (routeResult.Status != PromptStatus.OK)
            {
                editor.WriteMessage("\nAI-Engineer: connection-anchor route selection was cancelled.");
                return;
            }

            PromptPointOptions endpointOptions = new PromptPointOptions(
                "\nClick near the intended connection end of the selected route: ");
            PromptPointResult endpointResult = editor.GetPoint(endpointOptions);
            if (endpointResult.Status != PromptStatus.OK)
            {
                editor.WriteMessage("\nAI-Engineer: route-endpoint pick was cancelled.");
                return;
            }

            PromptStringOptions terminalOptions = new PromptStringOptions(
                "\nEnter the reviewed terminal name <CIRCUIT>: ");
            terminalOptions.AllowSpaces = false;
            terminalOptions.DefaultValue = "CIRCUIT";
            terminalOptions.UseDefaultValue = true;
            PromptResult terminalResult = editor.GetString(terminalOptions);
            if (terminalResult.Status != PromptStatus.OK || String.IsNullOrWhiteSpace(terminalResult.StringResult))
            {
                editor.WriteMessage("\nAI-Engineer: terminal name was cancelled or empty.");
                return;
            }

            try
            {
                Settings settings = Settings.Load();
                int databaseModifiedBefore = commandStartDbmod;
                Dictionary<string, object> selection;
                using (Transaction transaction = document.Database.TransactionManager.StartOpenCloseTransaction())
                {
                    BlockReference device = transaction.GetObject(
                        deviceResult.ObjectId, OpenMode.ForRead, false) as BlockReference;
                    Entity route = transaction.GetObject(
                        routeResult.ObjectId, OpenMode.ForRead, false) as Entity;
                    if (device == null || route == null)
                        throw new InvalidOperationException("The reviewed device or route is unavailable.");
                    ObjectId definitionId = device.IsDynamicBlock
                        ? device.DynamicBlockTableRecord : device.BlockTableRecord;
                    BlockTableRecord definition = transaction.GetObject(
                        definitionId, OpenMode.ForRead, false) as BlockTableRecord;
                    Point3d start, end;
                    string routeType;
                    Line line = route as Line;
                    Autodesk.AutoCAD.DatabaseServices.Polyline polyline =
                        route as Autodesk.AutoCAD.DatabaseServices.Polyline;
                    if (line != null)
                    {
                        routeType = "LINE"; start = line.StartPoint; end = line.EndPoint;
                    }
                    else if (polyline != null && polyline.NumberOfVertices >= 2)
                    {
                        routeType = "LWPOLYLINE";
                        start = polyline.GetPoint3dAt(0);
                        end = polyline.GetPoint3dAt(polyline.NumberOfVertices - 1);
                    }
                    else
                        throw new InvalidOperationException("The reviewed route has no supported endpoints.");
                    double startDistance = endpointResult.Value.DistanceTo(start);
                    double endDistance = endpointResult.Value.DistanceTo(end);
                    if (Math.Abs(startDistance - endDistance) <= 0.000000001)
                        throw new InvalidOperationException(
                            "The endpoint pick is equidistant from both ends; pick closer to one end.");
                    bool useStart = startDistance < endDistance;
                    selection = new Dictionary<string, object>
                    {
                        {"device_object_id", device.Handle.ToString()},
                        {"block_name", SafeText(definition == null ? "" : definition.Name, settings.MaximumTextBytes)},
                        {"route_object_id", route.Handle.ToString()},
                        {"route_entity_type", routeType},
                        {"endpoint", useStart ? "start" : "end"},
                        {"world_position", Point(useStart ? start : end)},
                        {"endpoint_pick_position", Point(endpointResult.Value)},
                        {"endpoint_pick_distance", useStart ? startDistance : endDistance},
                        {"alternate_endpoint_distance", useStart ? endDistance : startDistance},
                        {"endpoint_selection_method", "engineer_pick_nearest_endpoint"},
                        {"terminal_name", SafeText(terminalResult.StringResult.Trim(), 64)}
                    };
                    transaction.Commit();
                }
                int databaseModifiedAfter = GetDbmod();
                if (databaseModifiedAfter != databaseModifiedBefore)
                    throw new InvalidOperationException(
                        "DBMOD changed during read-only anchor capture from " +
                        databaseModifiedBefore.ToString(CultureInfo.InvariantCulture) + " to " +
                        databaseModifiedAfter.ToString(CultureInfo.InvariantCulture) + ".");
                if ((databaseModifiedAfter & ~(8 | 16)) != 0)
                    throw new InvalidOperationException(
                        "Connection-anchor capture ended with unsupported DBMOD bits.");
                string identitySource;
                string captureId = Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture);
                Dictionary<string, object> payload = new Dictionary<string, object>
                {
                    {"schema_version", 1},
                    {"capture_version", "autocad.connection_anchor_capture.v1"},
                    {"capture_id", captureId},
                    {"captured_at", DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture)},
                    {"document", new Dictionary<string, object>
                        {
                            {"document_id", GetDocumentId(document, out identitySource)},
                            {"identity_source", identitySource},
                            {"name", SafeText(Path.GetFileName(document.Name), settings.MaximumTextBytes)},
                            {"drawing_units", (int)document.Database.Insunits},
                            {"layout", SafeText(LayoutManager.Current.CurrentLayout, settings.MaximumTextBytes)},
                            {"database_modified_before", databaseModifiedBefore},
                            {"database_modified_after", databaseModifiedAfter}
                        }
                    },
                    {"selection", selection},
                    {"drawing_mutation_performed", false}
                };
                JavaScriptSerializer serializer = new JavaScriptSerializer();
                byte[] content = Encoding.UTF8.GetBytes(serializer.Serialize(payload));
                if (content.Length > settings.MaximumPayloadBytes)
                    throw new InvalidOperationException("Connection-anchor capture exceeds the payload limit.");
                string inbox = Path.GetFullPath(Path.Combine(
                    settings.WorkspaceRoot, ".ai-engineer", "autocad", "connection-anchors", "inbox"));
                EnsureContained(inbox, settings.WorkspaceRoot);
                Directory.CreateDirectory(inbox);
                string target = Path.Combine(inbox, captureId + ".json");
                string temporary = Path.Combine(inbox, "." + captureId + ".tmp");
                try
                {
                    using (FileStream stream = new FileStream(
                        temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096,
                        FileOptions.WriteThrough))
                    {
                        stream.Write(content, 0, content.Length);
                        stream.Flush(true);
                    }
                    if (File.Exists(target)) throw new IOException("Anchor-capture target already exists.");
                    File.Move(temporary, target);
                }
                finally
                {
                    if (File.Exists(temporary)) File.Delete(temporary);
                }
                string digest;
                using (SHA256 sha = SHA256.Create()) digest = ToHex(sha.ComputeHash(content));
                editor.WriteMessage("\nAIENG_ANCHOR_CAPTURE_ID=" + captureId);
                editor.WriteMessage("\nAIENG_ANCHOR_CAPTURE_SHA256=" + digest);
                editor.WriteMessage("\nAIENG_ANCHOR_DBMOD=" + databaseModifiedAfter.ToString(CultureInfo.InvariantCulture));
                editor.WriteMessage("\nAIENG_ANCHOR_DONE=1\n");
            }
            catch (System.Exception error)
            {
                editor.WriteMessage("\nAI-Engineer connection-anchor capture failed: " + SafeMessage(error) + "\n");
            }
        }

        internal static ContextEvidence Export(
            Document document, string sourceMode, ObjectId[] identifiers,
            Func<bool> cancellationRequested = null)
        {
            try
            {
                Settings settings = Settings.Load();
                Stopwatch stopwatch = Stopwatch.StartNew();
                ArrayList entities = new ArrayList();
                Dictionary<string, Dictionary<string, object>> layers =
                    new Dictionary<string, Dictionary<string, object>>(StringComparer.OrdinalIgnoreCase);
                List<string> reasons = new List<string>();
                bool truncated = identifiers.Length > settings.MaximumEntities;
                if (truncated) reasons.Add("maximum_entities");

                using (Transaction transaction = document.Database.TransactionManager.StartOpenCloseTransaction())
                {
                    int count = Math.Min(identifiers.Length, settings.MaximumEntities);
                    for (int index = 0; index < count; index++)
                    {
                        if (cancellationRequested != null && cancellationRequested())
                            throw new OperationCanceledException(
                                "AutoCAD drawing-context cancellation was requested.");
                        if (stopwatch.Elapsed.TotalSeconds > settings.MaximumDurationSeconds)
                        {
                            truncated = true;
                            AddReason(reasons, "maximum_duration_seconds");
                            break;
                        }
                        ObjectId identifier = identifiers[index];
                        if (identifier.IsNull || identifier.IsErased || !identifier.IsValid) continue;
                        Entity entity = transaction.GetObject(identifier, OpenMode.ForRead, false) as Entity;
                        if (entity == null) continue;
                        entities.Add(ExtractEntity(entity, transaction, document, settings, reasons, ref truncated));
                        AddLayer(entity.LayerId, transaction, layers, settings, reasons, ref truncated);
                    }
                    transaction.Commit();
                }

                string contextId = Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture);
                Dictionary<string, object> payload = CreatePayload(
                    document, contextId, sourceMode, settings, layers, entities, reasons, truncated);
                JavaScriptSerializer serializer = new JavaScriptSerializer();
                serializer.MaxJsonLength = 33554432;
                byte[] content = Encoding.UTF8.GetBytes(serializer.Serialize(payload));
                while (content.Length > settings.MaximumPayloadBytes && entities.Count > 0)
                {
                    entities.RemoveAt(entities.Count - 1);
                    AddReason(reasons, "maximum_payload_bytes");
                    payload["truncated"] = true;
                    payload["truncation_reasons"] = reasons.ToArray();
                    content = Encoding.UTF8.GetBytes(serializer.Serialize(payload));
                }
                if (content.Length > settings.MaximumPayloadBytes)
                    throw new InvalidOperationException("Context metadata exceeds the configured payload limit.");

                string inbox = Path.GetFullPath(Path.Combine(
                    settings.WorkspaceRoot, ".ai-engineer", "autocad", "context", "inbox"));
                EnsureContained(inbox, settings.WorkspaceRoot);
                Directory.CreateDirectory(inbox);
                string target = Path.Combine(inbox, contextId + ".json");
                string temporary = Path.Combine(inbox, "." + contextId + ".tmp");
                try
                {
                    using (FileStream stream = new FileStream(
                        temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096,
                        FileOptions.WriteThrough))
                    {
                        stream.Write(content, 0, content.Length);
                        stream.Flush(true);
                    }
                    if (File.Exists(target)) throw new IOException("Context target already exists.");
                    File.Move(temporary, target);
                }
                finally
                {
                    if (File.Exists(temporary)) File.Delete(temporary);
                }

                string digest;
                using (SHA256 sha = SHA256.Create())
                    digest = ToHex(sha.ComputeHash(content));
                int databaseModified = GetDbmod();
                stopwatch.Stop();
                ContextEvidence evidence = new ContextEvidence(
                    contextId, digest, sourceMode, entities.Count, databaseModified,
                    stopwatch.Elapsed.TotalMilliseconds, truncated);
                ContextEvidenceStore.Set(evidence);
                document.Editor.WriteMessage("\nAIENG_CONTEXT_ID=" + contextId);
                document.Editor.WriteMessage("\nAIENG_CONTEXT_SHA256=" + digest);
                document.Editor.WriteMessage("\nAIENG_CONTEXT_ENTITIES=" + entities.Count.ToString(CultureInfo.InvariantCulture));
                document.Editor.WriteMessage("\nAIENG_CONTEXT_DBMOD=" + databaseModified.ToString(CultureInfo.InvariantCulture));
                document.Editor.WriteMessage("\nAIENG_CONTEXT_DURATION_MS=" +
                    stopwatch.Elapsed.TotalMilliseconds.ToString("F3", CultureInfo.InvariantCulture));
                document.Editor.WriteMessage("\nAIENG_CONTEXT_DONE=1\n");
                return evidence;
            }
            catch (System.Exception error)
            {
                document.Editor.WriteMessage("\nAI-Engineer context export failed: " + SafeMessage(error) + "\n");
                if (cancellationRequested != null) throw;
                return null;
            }
        }

        private static Dictionary<string, object> CreatePayload(
            Document document, string contextId, string sourceMode, Settings settings,
            Dictionary<string, Dictionary<string, object>> layerMap, ArrayList entities,
            List<string> reasons, bool truncated)
        {
            ArrayList layers = new ArrayList();
            foreach (KeyValuePair<string, Dictionary<string, object>> item in layerMap)
                layers.Add(item.Value);
            Database database = document.Database;
            string layout = SafeText(LayoutManager.Current.CurrentLayout, settings.MaximumTextBytes);
            string identitySource;
            string documentId = GetDocumentId(document, out identitySource);
            return new Dictionary<string, object>
            {
                {"schema_version", 1},
                {"context_id", contextId},
                {"captured_at", DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture)},
                {"source_mode", sourceMode},
                {"privacy", new Dictionary<string, object>
                    {
                        {"classification", "project_confidential"},
                        {"provider_egress_allowed", false}
                    }
                },
                {"document", new Dictionary<string, object>
                    {
                        {"document_id", documentId},
                        {"identity_source", identitySource},
                        {"name", SafeText(Path.GetFileName(document.Name), settings.MaximumTextBytes)},
                        {"drawing_units", (int)database.Insunits},
                        {"layout", layout},
                        {"database_modified", GetDbmod()}
                    }
                },
                {"limits", settings.ToDictionary()},
                {"truncated", truncated},
                {"truncation_reasons", reasons.ToArray()},
                {"layers", layers},
                {"entities", entities}
            };
        }

        private static Dictionary<string, object> ExtractEntity(
            Entity entity, Transaction transaction, Document document, Settings settings,
            List<string> reasons, ref bool truncated)
        {
            string type = "UNSUPPORTED";
            string original = entity.GetType().FullName ?? entity.GetType().Name;
            ArrayList points = new ArrayList();
            object radius = null, rotation = null, scale = null, block = null, text = null;
            ArrayList attributes = new ArrayList();

            Line line = entity as Line;
            Autodesk.AutoCAD.DatabaseServices.Polyline polyline = entity as Autodesk.AutoCAD.DatabaseServices.Polyline;
            Arc arc = entity as Arc;
            Circle circle = entity as Circle;
            BlockReference reference = entity as BlockReference;
            DBText dbText = entity as DBText;
            MText mText = entity as MText;
            Dimension dimension = entity as Dimension;
            if (line != null)
            {
                type = "LINE"; points.Add(Point(line.StartPoint)); points.Add(Point(line.EndPoint));
            }
            else if (polyline != null)
            {
                type = "LWPOLYLINE";
                int count = Math.Min(polyline.NumberOfVertices, settings.MaximumPointsPerEntity);
                for (int index = 0; index < count; index++)
                    points.Add(Point(polyline.GetPoint3dAt(index)));
                if (polyline.NumberOfVertices > count)
                { truncated = true; AddReason(reasons, "maximum_points_per_entity"); }
            }
            else if (arc != null)
            {
                type = "ARC"; points.Add(Point(arc.Center)); points.Add(Point(arc.StartPoint));
                points.Add(Point(arc.EndPoint)); radius = arc.Radius;
            }
            else if (circle != null)
            {
                type = "CIRCLE"; points.Add(Point(circle.Center)); radius = circle.Radius;
            }
            else if (reference != null)
            {
                type = "INSERT"; points.Add(Point(reference.Position));
                rotation = reference.Rotation * 180.0 / Math.PI;
                scale = new double[] { reference.ScaleFactors.X, reference.ScaleFactors.Y, reference.ScaleFactors.Z };
                ObjectId definitionId = reference.IsDynamicBlock ? reference.DynamicBlockTableRecord : reference.BlockTableRecord;
                BlockTableRecord definition = transaction.GetObject(definitionId, OpenMode.ForRead, false) as BlockTableRecord;
                block = SafeText(definition == null ? "" : definition.Name, settings.MaximumTextBytes);
                int count = 0;
                foreach (ObjectId attributeId in reference.AttributeCollection)
                {
                    if (count >= settings.MaximumAttributesPerEntity)
                    { truncated = true; AddReason(reasons, "maximum_attributes_per_entity"); break; }
                    AttributeReference attribute = transaction.GetObject(attributeId, OpenMode.ForRead, false) as AttributeReference;
                    if (attribute == null) continue;
                    attributes.Add(new Dictionary<string, object>
                    {
                        {"tag", SafeText(attribute.Tag, settings.MaximumTextBytes)},
                        {"value", SafeText(attribute.TextString, settings.MaximumTextBytes)}
                    });
                    count++;
                }
            }
            else if (dbText != null)
            { type = "TEXT"; points.Add(Point(dbText.Position)); text = SafeText(dbText.TextString, settings.MaximumTextBytes); }
            else if (mText != null)
            { type = "MTEXT"; points.Add(Point(mText.Location)); text = SafeText(mText.Contents, settings.MaximumTextBytes); }
            else if (dimension != null)
            { type = "DIMENSION"; points.Add(Point(dimension.TextPosition)); text = SafeText(dimension.DimensionText, settings.MaximumTextBytes); }

            return new Dictionary<string, object>
            {
                {"object_id", entity.Handle.ToString()}, {"entity_type", type},
                {"original_type", SafeText(original, settings.MaximumTextBytes)},
                {"layer", SafeText(entity.Layer, settings.MaximumTextBytes)},
                {"layout", EntityLayoutName(entity, transaction, settings)},
                {"visible", entity.Visible}, {"points", points}, {"radius", radius},
                {"rotation_degrees", rotation}, {"scale", scale}, {"block_name", block},
                {"text", text}, {"attributes", attributes}
            };
        }

        private static void AddLayer(
            ObjectId identifier, Transaction transaction,
            Dictionary<string, Dictionary<string, object>> layers, Settings settings,
            List<string> reasons, ref bool truncated)
        {
            LayerTableRecord layer = transaction.GetObject(identifier, OpenMode.ForRead, false) as LayerTableRecord;
            if (layer == null || layers.ContainsKey(layer.Name)) return;
            if (layers.Count >= settings.MaximumLayers)
            { truncated = true; AddReason(reasons, "maximum_layers"); return; }
            string linetype = "";
            LinetypeTableRecord record = transaction.GetObject(layer.LinetypeObjectId, OpenMode.ForRead, false) as LinetypeTableRecord;
            if (record != null) linetype = record.Name;
            layers.Add(layer.Name, new Dictionary<string, object>
            {
                {"name", SafeText(layer.Name, settings.MaximumTextBytes)},
                {"color_index", (int)layer.Color.ColorIndex},
                {"linetype", SafeText(linetype, settings.MaximumTextBytes)},
                {"is_off", layer.IsOff}, {"is_frozen", layer.IsFrozen}, {"is_locked", layer.IsLocked}
            });
        }

        private static double[] Point(Point3d point)
        { return new double[] { point.X, point.Y, point.Z }; }

        private static string EntityLayoutName(
            Entity entity, Transaction transaction, Settings settings)
        {
            BlockTableRecord owner = transaction.GetObject(
                entity.OwnerId, OpenMode.ForRead, false) as BlockTableRecord;
            if (owner == null || owner.LayoutId.IsNull)
                return SafeText(LayoutManager.Current.CurrentLayout, settings.MaximumTextBytes);
            Layout layout = transaction.GetObject(
                owner.LayoutId, OpenMode.ForRead, false) as Layout;
            return SafeText(
                layout == null ? LayoutManager.Current.CurrentLayout : layout.LayoutName,
                settings.MaximumTextBytes);
        }

        private static int GetDbmod()
        {
            object value = Application.GetSystemVariable("DBMOD");
            return Convert.ToInt32(value, CultureInfo.InvariantCulture);
        }

        private static string GetDocumentId(Document document, out string identitySource)
        {
            Guid fingerprint;
            if (Guid.TryParse(document.Database.FingerprintGuid, out fingerprint) && fingerprint != Guid.Empty)
            {
                identitySource = "fingerprint_guid";
                return fingerprint.ToString("D").ToLowerInvariant();
            }
            string normalizedPath = Path.GetFullPath(document.Name).ToUpperInvariant();
            byte[] digest;
            using (SHA256 sha = SHA256.Create())
                digest = sha.ComputeHash(Encoding.UTF8.GetBytes(normalizedPath));
            byte[] identifier = new byte[16];
            Array.Copy(digest, identifier, identifier.Length);
            identitySource = "path_sha256";
            return new Guid(identifier).ToString("D").ToLowerInvariant();
        }

        private static string SafeText(string value, int maximumBytes)
        {
            if (String.IsNullOrEmpty(value)) return "";
            StringBuilder cleaned = new StringBuilder(value.Length);
            foreach (char character in value)
                cleaned.Append(Char.IsControl(character) ? ' ' : character);
            string result = cleaned.ToString();
            while (Encoding.UTF8.GetByteCount(result) > maximumBytes && result.Length > 0)
                result = result.Substring(0, result.Length - 1);
            return result;
        }

        private static string SafeMessage(System.Exception error)
        { return SafeText(error.GetType().Name + ": " + error.Message, 512); }

        private static void AddReason(List<string> reasons, string reason)
        { if (!reasons.Contains(reason)) reasons.Add(reason); }

        private static string ToHex(byte[] bytes)
        {
            StringBuilder value = new StringBuilder(bytes.Length * 2);
            foreach (byte item in bytes) value.Append(item.ToString("x2", CultureInfo.InvariantCulture));
            return value.ToString();
        }

        private static string FileSha256(string path)
        {
            using (FileStream stream = new FileStream(
                path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            using (SHA256 sha = SHA256.Create()) return ToHex(sha.ComputeHash(stream));
        }

        private static void WriteBytesAtomic(string path, byte[] content)
        {
            string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                using (FileStream stream = new FileStream(
                    temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096,
                    FileOptions.WriteThrough))
                {
                    stream.Write(content, 0, content.Length);
                    stream.Flush(true);
                }
                if (File.Exists(path)) throw new IOException("Atomic target already exists.");
                File.Move(temporary, path);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }

        private static void CleanupIndexContexts(
            Settings settings, List<ContextEvidence> completed)
        {
            foreach (ContextEvidence evidence in completed)
                DeleteIndexContext(settings, evidence);
        }

        private static void DeleteIndexContext(Settings settings, ContextEvidence evidence)
        {
            string inbox = Path.GetFullPath(Path.Combine(
                settings.WorkspaceRoot, ".ai-engineer", "autocad", "context", "inbox"));
            EnsureContained(inbox, settings.WorkspaceRoot);
            string path = Path.Combine(inbox, evidence.ContextId + ".json");
            if (File.Exists(path) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) == 0)
                File.Delete(path);
        }

        private static void EnsureContained(string path, string root)
        {
            string canonicalRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            string canonicalPath = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (!canonicalPath.StartsWith(canonicalRoot, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Context inbox escapes the configured workspace root.");
        }

        private sealed class Settings
        {
            public string WorkspaceRoot;
            public int MaximumEntities, MaximumLayers, MaximumAttributesPerEntity;
            public int MaximumPointsPerEntity, MaximumTextBytes, MaximumPayloadBytes;
            public int MaximumNesting, MaximumDurationSeconds;
            public int MaximumIndexEntities, MaximumIndexEntitiesPerChunk;
            public int MaximumIndexChunks, MaximumIndexDurationSeconds;

            public static Settings Load()
            {
                string assembly = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
                string path = Path.GetFullPath(Path.Combine(assembly, "settings.json"));
                JavaScriptSerializer serializer = new JavaScriptSerializer();
                Dictionary<string, object> document = serializer.Deserialize<Dictionary<string, object>>(File.ReadAllText(path, Encoding.UTF8));
                Settings settings = new Settings();
                settings.WorkspaceRoot = RequiredString(document, "workspace_root");
                if (!Path.IsPathRooted(settings.WorkspaceRoot)) throw new InvalidOperationException("workspace_root must be absolute.");
                settings.WorkspaceRoot = Path.GetFullPath(settings.WorkspaceRoot);
                settings.MaximumEntities = RequiredInt(document, "maximum_entities", 1, 2000);
                settings.MaximumLayers = RequiredInt(document, "maximum_layers", 1, 512);
                settings.MaximumAttributesPerEntity = RequiredInt(document, "maximum_attributes_per_entity", 0, 128);
                settings.MaximumPointsPerEntity = RequiredInt(document, "maximum_points_per_entity", 2, 4096);
                settings.MaximumTextBytes = RequiredInt(document, "maximum_text_bytes", 1, 16384);
                settings.MaximumPayloadBytes = RequiredInt(document, "maximum_payload_bytes", 1024, 4194304);
                settings.MaximumNesting = RequiredInt(document, "maximum_nesting", 4, 16);
                settings.MaximumDurationSeconds = RequiredInt(document, "maximum_duration_seconds", 1, 120);
                settings.MaximumIndexEntities = OptionalInt(
                    document, "maximum_index_entities", 200000, 1, 200000);
                settings.MaximumIndexEntitiesPerChunk = OptionalInt(
                    document, "maximum_index_entities_per_chunk", 64, 1, 512);
                settings.MaximumIndexChunks = OptionalInt(
                    document, "maximum_index_chunks", 4096, 1, 4096);
                settings.MaximumIndexDurationSeconds = OptionalInt(
                    document, "maximum_index_duration_seconds", 120, 1, 120);
                return settings;
            }

            public Dictionary<string, object> ToDictionary()
            {
                return new Dictionary<string, object>
                {
                    {"maximum_entities", MaximumEntities}, {"maximum_layers", MaximumLayers},
                    {"maximum_attributes_per_entity", MaximumAttributesPerEntity},
                    {"maximum_points_per_entity", MaximumPointsPerEntity}, {"maximum_text_bytes", MaximumTextBytes},
                    {"maximum_payload_bytes", MaximumPayloadBytes}, {"maximum_nesting", MaximumNesting},
                    {"maximum_duration_seconds", MaximumDurationSeconds}
                };
            }

            private static string RequiredString(Dictionary<string, object> value, string name)
            {
                object raw; if (!value.TryGetValue(name, out raw) || !(raw is string) || String.IsNullOrWhiteSpace((string)raw))
                    throw new InvalidOperationException(name + " is required.");
                return (string)raw;
            }

            private static int RequiredInt(Dictionary<string, object> value, string name, int minimum, int maximum)
            {
                object raw; if (!value.TryGetValue(name, out raw)) throw new InvalidOperationException(name + " is required.");
                int parsed = Convert.ToInt32(raw, CultureInfo.InvariantCulture);
                if (parsed < minimum || parsed > maximum) throw new InvalidOperationException(name + " is out of range.");
                return parsed;
            }

            private static int OptionalInt(
                Dictionary<string, object> value, string name, int fallback,
                int minimum, int maximum)
            {
                object raw;
                if (!value.TryGetValue(name, out raw)) return fallback;
                int parsed = Convert.ToInt32(raw, CultureInfo.InvariantCulture);
                if (parsed < minimum || parsed > maximum)
                    throw new InvalidOperationException(name + " is out of range.");
                return parsed;
            }
        }
    }
}
