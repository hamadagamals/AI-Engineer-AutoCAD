using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Security.Cryptography;

using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Structure;

namespace AIEngineer.Revit
{
    [DataContract]
    internal sealed class SchoolL4Predecessor
    {
        [DataMember(Name = "preview_sha256", Order = 1)] public string PreviewSha256;
        [DataMember(Name = "handoff_sha256", Order = 2)] public string HandoffSha256;
        [DataMember(Name = "verification_sha256", Order = 3)] public string VerificationSha256;
        [DataMember(Name = "existing_instance_count", Order = 4)] public int ExistingInstanceCount;
        [DataMember(Name = "existing_element_ids", Order = 5)] public List<long> ExistingElementIds;
        [DataMember(Name = "existing_point_ids", Order = 6)] public List<string> ExistingPointIds;
    }

    [DataContract]
    internal sealed class SchoolL4Batch2
    {
        [DataMember(Name = "batch_number", Order = 1)] public int BatchNumber;
        [DataMember(Name = "batch_count", Order = 2)] public int BatchCount;
        [DataMember(Name = "placement_count", Order = 3)] public int PlacementCount;
        [DataMember(Name = "placements", Order = 4)] public List<SchoolL4Point> Placements;
        [DataMember(Name = "resulting_total_instance_count", Order = 5)] public int ResultingTotal;
    }

    [DataContract]
    internal sealed class SchoolL4Batch2Preview
    {
        [DataMember(Name = "schema_version", Order = 1)] public int SchemaVersion;
        [DataMember(Name = "preview_id", Order = 2)] public string PreviewId;
        [DataMember(Name = "expires_utc", Order = 3)] public string ExpiresUtc;
        [DataMember(Name = "action", Order = 4)] public string Action;
        [DataMember(Name = "status", Order = 5)] public string Status;
        [DataMember(Name = "execution_allowed", Order = 6)] public bool ExecutionAllowed;
        [DataMember(Name = "provider_egress_performed", Order = 7)] public bool ProviderEgressPerformed;
        [DataMember(Name = "document_mutation_performed", Order = 8)] public bool DocumentMutationPerformed;
        [DataMember(Name = "source", Order = 9)] public SchoolL4Source Source;
        [DataMember(Name = "predecessor", Order = 10)] public SchoolL4Predecessor Predecessor;
        [DataMember(Name = "family", Order = 11)] public SchoolL4Family Family;
        [DataMember(Name = "room", Order = 12)] public SchoolL4Room Room;
        [DataMember(Name = "batch", Order = 13)] public SchoolL4Batch2 Batch;
        [DataMember(Name = "preview_sha256", Order = 14)] public string PreviewSha256;
    }

    [DataContract]
    internal sealed class SchoolL4Batch2Report
    {
        [DataMember(Name = "schema_version", Order = 1)] public int SchemaVersion;
        [DataMember(Name = "request_id", Order = 2)] public string RequestId;
        [DataMember(Name = "preview_id", Order = 3)] public string PreviewId;
        [DataMember(Name = "preview_sha256", Order = 4)] public string PreviewSha256;
        [DataMember(Name = "source_sha256", Order = 5)] public string SourceSha256;
        [DataMember(Name = "output_path", Order = 6)] public string OutputPath;
        [DataMember(Name = "output_sha256", Order = 7)] public string OutputSha256;
        [DataMember(Name = "existing_element_ids", Order = 8)] public List<long> ExistingElementIds;
        [DataMember(Name = "created_element_ids", Order = 9)] public List<long> CreatedElementIds;
        [DataMember(Name = "created_point_ids", Order = 10)] public List<string> CreatedPointIds;
        [DataMember(Name = "created_family_instances", Order = 11)] public int CreatedCount;
        [DataMember(Name = "verified_total_instances", Order = 12)] public int TotalCount;
        [DataMember(Name = "connector_counts", Order = 13)] public List<int> ConnectorCounts;
        [DataMember(Name = "transaction_committed", Order = 14)] public bool TransactionCommitted;
        [DataMember(Name = "saved_as_new_file", Order = 15)] public bool SavedAsNewFile;
        [DataMember(Name = "provider_egress_performed", Order = 16)] public bool ProviderEgressPerformed;
        [DataMember(Name = "document_mutation_performed", Order = 17)] public bool DocumentMutationPerformed;
    }

    [DataContract]
    internal sealed class SchoolL4Batch2VerificationReport
    {
        [DataMember(Name = "schema_version", Order = 1)] public int SchemaVersion;
        [DataMember(Name = "request_id", Order = 2)] public string RequestId;
        [DataMember(Name = "handoff_request_id", Order = 3)] public string HandoffRequestId;
        [DataMember(Name = "output_sha256", Order = 4)] public string OutputSha256;
        [DataMember(Name = "verified_total_instances", Order = 5)] public int TotalCount;
        [DataMember(Name = "verified_element_ids", Order = 6)] public List<long> VerifiedElementIds;
        [DataMember(Name = "placement_matches_preview", Order = 7)] public bool PlacementMatchesPreview;
        [DataMember(Name = "connector_counts", Order = 8)] public List<int> ConnectorCounts;
        [DataMember(Name = "provider_egress_performed", Order = 9)] public bool ProviderEgressPerformed;
        [DataMember(Name = "document_mutation_performed", Order = 10)] public bool DocumentMutationPerformed;
    }

    internal static class SchoolL4Batch2Handoff
    {
        private const string ApprovedFingerprint = "565529255B384C229F43B446A1A7169037E07DF63AD0F17D744FF37F50943801";
        private const string ApprovedSourceHash = "941551BE095D7CC743924ACDFAE335E8CB549D648DB1C9ABDECADF669BE41A1A";
        private const int ExistingCount = 23;
        private const int AddedCount = 22;
        private const int TotalCount = 45;

        public static string Execute(Document document, string requestId, string previewPath, string outputPath)
        {
            SchoolL4Batch2Preview preview = Read<SchoolL4Batch2Preview>(previewPath);
            Validate(preview, true);
            string output = ModelLineHandoff.ValidateNewOutput(document.PathName, outputPath);
            if (!string.Equals(document.Title, preview.Source.DocumentTitle, StringComparison.Ordinal)
                || !string.Equals(Hash(document.PathName), ApprovedSourceHash, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("School L4 batch-2 source identity changed.");
            Level level = document.GetElement(new ElementId(preview.Room.LevelId)) as Level;
            if (level == null || !string.Equals(level.Name, preview.Room.LevelName, StringComparison.Ordinal))
                throw new InvalidDataException("School L4 batch-2 level identity changed.");
            FamilySymbol symbol = ExactSymbol(document, preview);
            List<FamilyInstance> existing = VerifyExisting(document, preview, symbol.Id.Value);
            List<FamilyInstance> created = new List<FamilyInstance>();
            using (Transaction transaction = new Transaction(document, "AI-Engineer approved school L4 batch 2"))
            {
                transaction.Start();
                if (!symbol.IsActive) { symbol.Activate(); document.Regenerate(); }
                double relativeZ = Mm(preview.Room.MountingZmm) - level.Elevation;
                foreach (SchoolL4Point point in preview.Batch.Placements)
                {
                    if (FindByMark(document, "AIENG-" + point.PointId) != null)
                    {
                        transaction.RollBack();
                        throw new InvalidDataException("School L4 batch-2 point already exists: " + point.PointId);
                    }
                    FamilyInstance instance = document.Create.NewFamilyInstance(
                        new XYZ(Mm(point.Xmm), Mm(point.Ymm), relativeZ), symbol, level, StructuralType.NonStructural);
                    Parameter mark = instance.get_Parameter(BuiltInParameter.ALL_MODEL_MARK);
                    if (mark == null || mark.IsReadOnly || !mark.Set("AIENG-" + point.PointId))
                    {
                        transaction.RollBack();
                        throw new InvalidDataException("School L4 batch-2 mark could not be bound.");
                    }
                    created.Add(instance);
                }
                document.Regenerate();
                for (int index = 0; index < created.Count; index++) VerifyNew(created[index], preview.Batch.Placements[index], preview, symbol.Id.Value);
                if (existing.Count + created.Count != TotalCount)
                {
                    transaction.RollBack();
                    throw new InvalidDataException("School L4 batch-2 total is not 45.");
                }
                if (transaction.Commit() != TransactionStatus.Committed)
                    throw new InvalidOperationException("School L4 batch-2 transaction did not commit.");
            }
            List<FamilyInstance> all = existing.Concat(created).ToList();
            if (all.Any(x => ConnectorCount(x) < 1)) throw new InvalidDataException("School L4 fixture lacks an electrical connector.");
            document.SaveAs(output, new SaveAsOptions { OverwriteExistingFile = false });
            if (!File.Exists(output)) throw new IOException("School L4 batch-2 Save As did not create the output.");
            SchoolL4Batch2Report report = new SchoolL4Batch2Report
            {
                SchemaVersion = 1, RequestId = requestId, PreviewId = preview.PreviewId,
                PreviewSha256 = preview.PreviewSha256, SourceSha256 = ApprovedSourceHash,
                OutputPath = output, OutputSha256 = Hash(output),
                ExistingElementIds = existing.Select(x => x.Id.Value).ToList(),
                CreatedElementIds = created.Select(x => x.Id.Value).ToList(),
                CreatedPointIds = preview.Batch.Placements.Select(x => x.PointId).ToList(),
                CreatedCount = created.Count, TotalCount = all.Count,
                ConnectorCounts = all.Select(ConnectorCount).ToList(), TransactionCommitted = true,
                SavedAsNewFile = true, ProviderEgressPerformed = false, DocumentMutationPerformed = true
            };
            return Write("school-l4-batch2-handoffs", requestId, report, typeof(SchoolL4Batch2Report));
        }

        public static string Verify(Document document, string requestId, string handoffRequestId, string previewPath)
        {
            SchoolL4Batch2Preview preview = Read<SchoolL4Batch2Preview>(previewPath);
            Validate(preview, false);
            SchoolL4Batch2Report handoff = Read<SchoolL4Batch2Report>(Path.Combine(BridgeRoot(), "school-l4-batch2-handoffs", handoffRequestId + ".json"));
            string current = Path.GetFullPath(document.PathName);
            if (!string.Equals(current, handoff.OutputPath, StringComparison.OrdinalIgnoreCase)
                || !string.Equals(Hash(current), handoff.OutputSha256, StringComparison.OrdinalIgnoreCase)
                || handoff.ExistingElementIds == null || handoff.ExistingElementIds.Count != ExistingCount
                || handoff.CreatedElementIds == null || handoff.CreatedElementIds.Count != AddedCount)
                throw new InvalidDataException("Reopened school L4 batch-2 output identity changed.");
            FamilySymbol symbol = ExactSymbol(document, preview);
            List<FamilyInstance> existing = VerifyExisting(document, preview, symbol.Id.Value);
            List<FamilyInstance> created = new List<FamilyInstance>();
            for (int index = 0; index < AddedCount; index++)
            {
                FamilyInstance instance = document.GetElement(new ElementId(handoff.CreatedElementIds[index])) as FamilyInstance;
                VerifyNew(instance, preview.Batch.Placements[index], preview, symbol.Id.Value);
                created.Add(instance);
            }
            List<FamilyInstance> all = existing.Concat(created).ToList();
            List<int> connectors = all.Select(ConnectorCount).ToList();
            if (all.Count != TotalCount || connectors.Any(x => x < 1))
                throw new InvalidDataException("Reopened school L4 batch-2 total or connector verification failed.");
            SchoolL4Batch2VerificationReport report = new SchoolL4Batch2VerificationReport
            {
                SchemaVersion = 1, RequestId = requestId, HandoffRequestId = handoffRequestId,
                OutputSha256 = handoff.OutputSha256, TotalCount = TotalCount,
                VerifiedElementIds = all.Select(x => x.Id.Value).ToList(), PlacementMatchesPreview = true,
                ConnectorCounts = connectors, ProviderEgressPerformed = false, DocumentMutationPerformed = false
            };
            return Write("school-l4-batch2-verifications", requestId, report, typeof(SchoolL4Batch2VerificationReport));
        }

        private static void Validate(SchoolL4Batch2Preview preview, bool requireUnexpired)
        {
            DateTime expires;
            if (preview == null || preview.SchemaVersion != 1 || preview.Source == null || preview.Predecessor == null
                || preview.Family == null || preview.Room == null || preview.Batch == null || preview.Batch.Placements == null
                || preview.Predecessor.ExistingInstanceCount != ExistingCount
                || preview.Predecessor.ExistingElementIds == null || preview.Predecessor.ExistingElementIds.Count != ExistingCount
                || preview.Predecessor.ExistingPointIds == null || preview.Predecessor.ExistingPointIds.Count != ExistingCount
                || preview.Batch.BatchNumber != 2 || preview.Batch.BatchCount != 2
                || preview.Batch.PlacementCount != AddedCount || preview.Batch.Placements.Count != AddedCount
                || preview.Batch.ResultingTotal != TotalCount || preview.ExecutionAllowed
                || preview.ProviderEgressPerformed || preview.DocumentMutationPerformed
                || !string.Equals(preview.Action, "place_remaining_exact_loaded_family_instances_save_as", StringComparison.Ordinal)
                || !string.Equals(preview.Status, "review_required", StringComparison.Ordinal)
                || !string.Equals(preview.PreviewSha256, ApprovedFingerprint, StringComparison.Ordinal)
                || !string.Equals(preview.Source.ModelSha256, ApprovedSourceHash, StringComparison.Ordinal)
                || !string.Equals(preview.Family.PlacementType, "OneLevelBased", StringComparison.Ordinal)
                || !string.Equals(preview.Family.HostRequirement, "level", StringComparison.Ordinal)
                || Math.Abs(preview.Room.MountingZmm - 9500.0) > 0.001
                || !DateTime.TryParse(preview.ExpiresUtc, null, System.Globalization.DateTimeStyles.RoundtripKind, out expires)
                || (requireUnexpired && expires.ToUniversalTime() <= DateTime.UtcNow))
                throw new InvalidDataException("Approved school L4 batch-2 preview is invalid or expired.");
            HashSet<string> allPoints = new HashSet<string>(preview.Predecessor.ExistingPointIds);
            for (int index = 0; index < AddedCount; index++)
            {
                SchoolL4Point point = preview.Batch.Placements[index];
                if (point == null || point.Index != index + 1 || string.IsNullOrWhiteSpace(point.PointId)
                    || !allPoints.Add(point.PointId) || Math.Abs(point.Zmm - 9500.0) > 0.001
                    || Math.Abs(point.RotationDegrees) > 0.001)
                    throw new InvalidDataException("School L4 batch-2 point contract is invalid.");
            }
            if (allPoints.Count != TotalCount) throw new InvalidDataException("School L4 point partition is not complete.");
        }

        private static FamilySymbol ExactSymbol(Document document, SchoolL4Batch2Preview preview)
        {
            List<FamilySymbol> symbols = new FilteredElementCollector(document).OfClass(typeof(FamilySymbol))
                .Cast<FamilySymbol>().Where(x => x.Family != null
                    && string.Equals(x.Family.Name, preview.Family.FamilyName, StringComparison.Ordinal)
                    && string.Equals(x.Name, preview.Family.TypeName, StringComparison.Ordinal)).ToList();
            if (symbols.Count != 1 || symbols[0].Family.FamilyPlacementType != FamilyPlacementType.OneLevelBased)
                throw new InvalidDataException("School L4 exact loaded family type is unavailable or ambiguous.");
            return symbols[0];
        }

        private static List<FamilyInstance> VerifyExisting(Document document, SchoolL4Batch2Preview preview, long symbolId)
        {
            List<FamilyInstance> result = new List<FamilyInstance>();
            for (int index = 0; index < ExistingCount; index++)
            {
                FamilyInstance instance = document.GetElement(new ElementId(preview.Predecessor.ExistingElementIds[index])) as FamilyInstance;
                string mark = "AIENG-" + preview.Predecessor.ExistingPointIds[index];
                Parameter parameter = instance == null ? null : instance.get_Parameter(BuiltInParameter.ALL_MODEL_MARK);
                LocationPoint location = instance == null ? null : instance.Location as LocationPoint;
                if (instance == null || location == null || instance.Symbol.Id.Value != symbolId
                    || instance.LevelId.Value != preview.Room.LevelId
                    || Math.Abs(FromFeet(location.Point.Z) - preview.Room.MountingZmm) > 0.1
                    || parameter == null || !string.Equals(parameter.AsString(), mark, StringComparison.Ordinal)
                    || ConnectorCount(instance) < 1)
                    throw new InvalidDataException("Existing school L4 batch-1 evidence changed: " + mark);
                result.Add(instance);
            }
            return result;
        }

        private static void VerifyNew(FamilyInstance instance, SchoolL4Point point, SchoolL4Batch2Preview preview, long symbolId)
        {
            LocationPoint location = instance == null ? null : instance.Location as LocationPoint;
            Parameter mark = instance == null ? null : instance.get_Parameter(BuiltInParameter.ALL_MODEL_MARK);
            if (instance == null || location == null || instance.Symbol.Id.Value != symbolId
                || instance.LevelId.Value != preview.Room.LevelId
                || Math.Abs(FromFeet(location.Point.X) - point.Xmm) > 0.1
                || Math.Abs(FromFeet(location.Point.Y) - point.Ymm) > 0.1
                || Math.Abs(FromFeet(location.Point.Z) - point.Zmm) > 0.1
                || mark == null || !string.Equals(mark.AsString(), "AIENG-" + point.PointId, StringComparison.Ordinal))
                throw new InvalidDataException("School L4 batch-2 instance mismatch: " + point.PointId);
        }

        private static FamilyInstance FindByMark(Document document, string mark)
        {
            return new FilteredElementCollector(document).OfClass(typeof(FamilyInstance)).Cast<FamilyInstance>()
                .FirstOrDefault(x => { Parameter p = x.get_Parameter(BuiltInParameter.ALL_MODEL_MARK); return p != null && string.Equals(p.AsString(), mark, StringComparison.Ordinal); });
        }
        private static int ConnectorCount(FamilyInstance instance)
        {
            try { return instance != null && instance.MEPModel != null && instance.MEPModel.ConnectorManager != null ? instance.MEPModel.ConnectorManager.Connectors.Size : 0; }
            catch { return 0; }
        }
        private static double Mm(double value) { return UnitUtils.ConvertToInternalUnits(value, UnitTypeId.Millimeters); }
        private static double FromFeet(double value) { return UnitUtils.ConvertFromInternalUnits(value, UnitTypeId.Millimeters); }
        private static T Read<T>(string path)
        {
            if (!File.Exists(path) || new FileInfo(path).Length > 131072) throw new InvalidDataException("School L4 batch-2 evidence is missing or oversized.");
            using (FileStream stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
                return (T)new DataContractJsonSerializer(typeof(T)).ReadObject(stream);
        }
        private static string Write(string folder, string requestId, object value, Type type)
        {
            string directory = Path.Combine(BridgeRoot(), folder); Directory.CreateDirectory(directory);
            string path = Path.Combine(directory, requestId + ".json");
            using (FileStream stream = new FileStream(path, FileMode.CreateNew)) new DataContractJsonSerializer(type).WriteObject(stream, value);
            return folder + "/" + requestId + ".json";
        }
        private static string BridgeRoot() { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AI-Engineer", "revit"); }
        private static string Hash(string path)
        {
            using (SHA256 algorithm = SHA256.Create())
            using (FileStream stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                return BitConverter.ToString(algorithm.ComputeHash(stream)).Replace("-", string.Empty);
        }
    }
}
