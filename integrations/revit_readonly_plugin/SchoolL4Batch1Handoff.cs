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
    internal sealed class SchoolL4Source
    {
        [DataMember(Name = "model_sha256", Order = 1)] public string ModelSha256;
        [DataMember(Name = "document_title", Order = 2)] public string DocumentTitle;
    }

    [DataContract]
    internal sealed class SchoolL4Family
    {
        [DataMember(Name = "sha256", Order = 1)] public string Sha256;
        [DataMember(Name = "family_name", Order = 2)] public string FamilyName;
        [DataMember(Name = "type_name", Order = 3)] public string TypeName;
        [DataMember(Name = "placement_type", Order = 4)] public string PlacementType;
        [DataMember(Name = "host_requirement", Order = 5)] public string HostRequirement;
    }

    [DataContract]
    internal sealed class SchoolL4Room
    {
        [DataMember(Name = "level_id", Order = 1)] public long LevelId;
        [DataMember(Name = "level_name", Order = 2)] public string LevelName;
        [DataMember(Name = "mounting_z_mm", Order = 3)] public double MountingZmm;
    }

    [DataContract]
    internal sealed class SchoolL4Point
    {
        [DataMember(Name = "index", Order = 1)] public int Index;
        [DataMember(Name = "point_id", Order = 2)] public string PointId;
        [DataMember(Name = "x_mm", Order = 3)] public double Xmm;
        [DataMember(Name = "y_mm", Order = 4)] public double Ymm;
        [DataMember(Name = "z_mm", Order = 5)] public double Zmm;
        [DataMember(Name = "rotation_degrees", Order = 6)] public double RotationDegrees;
    }

    [DataContract]
    internal sealed class SchoolL4Batch
    {
        [DataMember(Name = "batch_number", Order = 1)] public int BatchNumber;
        [DataMember(Name = "batch_count", Order = 2)] public int BatchCount;
        [DataMember(Name = "placement_count", Order = 3)] public int PlacementCount;
        [DataMember(Name = "placements", Order = 4)] public List<SchoolL4Point> Placements;
        [DataMember(Name = "remaining_point_ids_for_batch_2", Order = 5)] public List<string> RemainingPointIds;
    }

    [DataContract]
    internal sealed class SchoolL4Preview
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
        [DataMember(Name = "family", Order = 10)] public SchoolL4Family Family;
        [DataMember(Name = "room", Order = 11)] public SchoolL4Room Room;
        [DataMember(Name = "batch", Order = 12)] public SchoolL4Batch Batch;
        [DataMember(Name = "preview_sha256", Order = 13)] public string PreviewSha256;
    }

    [DataContract]
    internal sealed class SchoolL4Report
    {
        [DataMember(Name = "schema_version", Order = 1)] public int SchemaVersion;
        [DataMember(Name = "request_id", Order = 2)] public string RequestId;
        [DataMember(Name = "preview_id", Order = 3)] public string PreviewId;
        [DataMember(Name = "preview_sha256", Order = 4)] public string PreviewSha256;
        [DataMember(Name = "source_sha256", Order = 5)] public string SourceSha256;
        [DataMember(Name = "output_path", Order = 6)] public string OutputPath;
        [DataMember(Name = "output_sha256", Order = 7)] public string OutputSha256;
        [DataMember(Name = "family_name", Order = 8)] public string FamilyName;
        [DataMember(Name = "type_name", Order = 9)] public string TypeName;
        [DataMember(Name = "level_id", Order = 10)] public long LevelId;
        [DataMember(Name = "mounting_z_mm", Order = 11)] public double MountingZmm;
        [DataMember(Name = "created_family_instances", Order = 12)] public int CreatedFamilyInstances;
        [DataMember(Name = "created_element_ids", Order = 13)] public List<long> CreatedElementIds;
        [DataMember(Name = "point_ids", Order = 14)] public List<string> PointIds;
        [DataMember(Name = "connector_counts", Order = 15)] public List<int> ConnectorCounts;
        [DataMember(Name = "transaction_committed", Order = 16)] public bool TransactionCommitted;
        [DataMember(Name = "saved_as_new_file", Order = 17)] public bool SavedAsNewFile;
        [DataMember(Name = "provider_egress_performed", Order = 18)] public bool ProviderEgressPerformed;
        [DataMember(Name = "document_mutation_performed", Order = 19)] public bool DocumentMutationPerformed;
    }

    [DataContract]
    internal sealed class SchoolL4VerificationReport
    {
        [DataMember(Name = "schema_version", Order = 1)] public int SchemaVersion;
        [DataMember(Name = "request_id", Order = 2)] public string RequestId;
        [DataMember(Name = "handoff_request_id", Order = 3)] public string HandoffRequestId;
        [DataMember(Name = "output_sha256", Order = 4)] public string OutputSha256;
        [DataMember(Name = "verified_family_instances", Order = 5)] public int VerifiedFamilyInstances;
        [DataMember(Name = "verified_element_ids", Order = 6)] public List<long> VerifiedElementIds;
        [DataMember(Name = "placement_matches_preview", Order = 7)] public bool PlacementMatchesPreview;
        [DataMember(Name = "connector_counts", Order = 8)] public List<int> ConnectorCounts;
        [DataMember(Name = "provider_egress_performed", Order = 9)] public bool ProviderEgressPerformed;
        [DataMember(Name = "document_mutation_performed", Order = 10)] public bool DocumentMutationPerformed;
    }

    internal sealed class SchoolL4FamilyLoadOptions : IFamilyLoadOptions
    {
        public bool OnFamilyFound(bool familyInUse, out bool overwriteParameterValues)
        {
            overwriteParameterValues = false;
            return false;
        }

        public bool OnSharedFamilyFound(Family sharedFamily, bool familyInUse, out FamilySource source, out bool overwriteParameterValues)
        {
            source = FamilySource.Family;
            overwriteParameterValues = false;
            return false;
        }
    }

    internal static class SchoolL4Batch1Handoff
    {
        private const string ApprovedFingerprint = "5E78A601B372501A07612B4B120B9E73878F7FEAD4718C4CC5F39F40EB6F273C";
        private const string ApprovedFamilyHash = "404CCB847E33841D36200D31080A2BB661B1AA941AEFBF170C372055172A139F";
        private const int ExactCount = 23;

        public static string Execute(Document document, string requestId, string previewPath, string outputPath, string stagedFamilyRelativePath)
        {
            SchoolL4Preview preview = Read<SchoolL4Preview>(previewPath);
            Validate(preview, true);
            string output = ModelLineHandoff.ValidateNewOutput(document.PathName, outputPath);
            if (!string.Equals(Hash(document.PathName), preview.Source.ModelSha256, StringComparison.OrdinalIgnoreCase)
                || !string.Equals(document.Title, preview.Source.DocumentTitle, StringComparison.Ordinal))
                throw new InvalidDataException("School L4 source identity changed.");
            string familyPath = ResolveStagedFamily(stagedFamilyRelativePath);
            if (!string.Equals(Hash(familyPath), ApprovedFamilyHash, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("School L4 staged family hash changed.");
            Level level = document.GetElement(new ElementId(preview.Room.LevelId)) as Level;
            if (level == null || !string.Equals(level.Name, preview.Room.LevelName, StringComparison.Ordinal))
                throw new InvalidDataException("School L4 level identity changed.");

            FamilySymbol symbol = LoadExactFamilySymbol(document, familyPath, preview.Family.FamilyName, preview.Family.TypeName);
            List<FamilyInstance> instances = new List<FamilyInstance>();
            using (Transaction transaction = new Transaction(document, "AI-Engineer approved school L4 batch 1"))
            {
                transaction.Start();
                if (!symbol.IsActive) { symbol.Activate(); document.Regenerate(); }
                double relativeZ = UnitUtils.ConvertToInternalUnits(preview.Room.MountingZmm, UnitTypeId.Millimeters) - level.Elevation;
                foreach (SchoolL4Point point in preview.Batch.Placements)
                {
                    FamilyInstance instance = document.Create.NewFamilyInstance(
                        new XYZ(Mm(point.Xmm), Mm(point.Ymm), relativeZ), symbol, level, StructuralType.NonStructural);
                    Parameter mark = instance.get_Parameter(BuiltInParameter.ALL_MODEL_MARK);
                    if (mark == null || mark.IsReadOnly || !mark.Set("AIENG-" + point.PointId))
                    {
                        transaction.RollBack();
                        throw new InvalidDataException("School L4 instance mark could not be bound.");
                    }
                    instances.Add(instance);
                }
                document.Regenerate();
                for (int index = 0; index < instances.Count; index++) VerifyInstance(instances[index], preview.Batch.Placements[index], preview, symbol.Id.Value);
                if (transaction.Commit() != TransactionStatus.Committed)
                    throw new InvalidOperationException("School L4 placement transaction did not commit.");
            }
            document.SaveAs(output, new SaveAsOptions { OverwriteExistingFile = false });
            if (!File.Exists(output)) throw new IOException("School L4 Save As did not create the approved output.");
            SchoolL4Report report = new SchoolL4Report
            {
                SchemaVersion = 1, RequestId = requestId, PreviewId = preview.PreviewId,
                PreviewSha256 = preview.PreviewSha256, SourceSha256 = preview.Source.ModelSha256,
                OutputPath = output, OutputSha256 = Hash(output), FamilyName = preview.Family.FamilyName,
                TypeName = preview.Family.TypeName, LevelId = preview.Room.LevelId,
                MountingZmm = preview.Room.MountingZmm, CreatedFamilyInstances = instances.Count,
                CreatedElementIds = instances.Select(x => x.Id.Value).ToList(),
                PointIds = preview.Batch.Placements.Select(x => x.PointId).ToList(),
                ConnectorCounts = instances.Select(ConnectorCount).ToList(), TransactionCommitted = true,
                SavedAsNewFile = true, ProviderEgressPerformed = false, DocumentMutationPerformed = true
            };
            return Write("school-l4-handoffs", requestId, report, typeof(SchoolL4Report));
        }

        public static string Verify(Document document, string requestId, string handoffRequestId, string previewPath)
        {
            SchoolL4Preview preview = Read<SchoolL4Preview>(previewPath);
            Validate(preview, false);
            SchoolL4Report handoff = Read<SchoolL4Report>(Path.Combine(BridgeRoot(), "school-l4-handoffs", handoffRequestId + ".json"));
            string current = Path.GetFullPath(document.PathName);
            if (!string.Equals(current, handoff.OutputPath, StringComparison.OrdinalIgnoreCase)
                || !string.Equals(Hash(current), handoff.OutputSha256, StringComparison.OrdinalIgnoreCase)
                || handoff.CreatedElementIds == null || handoff.CreatedElementIds.Count != ExactCount)
                throw new InvalidDataException("Reopened school L4 output identity changed.");
            List<int> connectors = new List<int>();
            for (int index = 0; index < ExactCount; index++)
            {
                FamilyInstance instance = document.GetElement(new ElementId(handoff.CreatedElementIds[index])) as FamilyInstance;
                VerifyInstance(instance, preview.Batch.Placements[index], preview, -1);
                connectors.Add(ConnectorCount(instance));
            }
            if (connectors.Any(x => x < 1)) throw new InvalidDataException("Reopened school L4 fixture lacks an electrical connector.");
            SchoolL4VerificationReport report = new SchoolL4VerificationReport
            {
                SchemaVersion = 1, RequestId = requestId, HandoffRequestId = handoffRequestId,
                OutputSha256 = handoff.OutputSha256, VerifiedFamilyInstances = ExactCount,
                VerifiedElementIds = handoff.CreatedElementIds, PlacementMatchesPreview = true,
                ConnectorCounts = connectors, ProviderEgressPerformed = false, DocumentMutationPerformed = false
            };
            return Write("school-l4-verifications", requestId, report, typeof(SchoolL4VerificationReport));
        }

        private static void Validate(SchoolL4Preview preview, bool requireUnexpired)
        {
            DateTime expires;
            if (preview == null || preview.SchemaVersion != 1 || preview.Source == null || preview.Family == null
                || preview.Room == null || preview.Batch == null || preview.Batch.Placements == null
                || preview.Batch.Placements.Count != ExactCount || preview.Batch.PlacementCount != ExactCount
                || preview.Batch.BatchNumber != 1 || preview.Batch.BatchCount != 2
                || preview.Batch.RemainingPointIds == null || preview.Batch.RemainingPointIds.Count != 22
                || preview.ExecutionAllowed || preview.ProviderEgressPerformed || preview.DocumentMutationPerformed
                || !string.Equals(preview.Action, "load_exact_family_and_place_level_based_instances_save_as", StringComparison.Ordinal)
                || !string.Equals(preview.Status, "review_required", StringComparison.Ordinal)
                || !string.Equals(preview.PreviewSha256, ApprovedFingerprint, StringComparison.Ordinal)
                || !string.Equals(preview.Family.Sha256, ApprovedFamilyHash, StringComparison.Ordinal)
                || !string.Equals(preview.Family.PlacementType, "OneLevelBased", StringComparison.Ordinal)
                || !string.Equals(preview.Family.HostRequirement, "level", StringComparison.Ordinal)
                || preview.Room.LevelId <= 0 || Math.Abs(preview.Room.MountingZmm - 9500.0) > 0.001
                || !DateTime.TryParse(preview.ExpiresUtc, null, System.Globalization.DateTimeStyles.RoundtripKind, out expires)
                || (requireUnexpired && expires.ToUniversalTime() <= DateTime.UtcNow))
                throw new InvalidDataException("Approved school L4 batch-1 preview is invalid or expired.");
            HashSet<string> ids = new HashSet<string>();
            for (int index = 0; index < preview.Batch.Placements.Count; index++)
            {
                SchoolL4Point point = preview.Batch.Placements[index];
                if (point == null || point.Index != index + 1 || string.IsNullOrWhiteSpace(point.PointId)
                    || !ids.Add(point.PointId) || Math.Abs(point.Zmm - 9500.0) > 0.001
                    || Math.Abs(point.RotationDegrees) > 0.001 || InvalidCoordinate(point.Xmm) || InvalidCoordinate(point.Ymm))
                    throw new InvalidDataException("Approved school L4 point contract is invalid.");
            }
        }

        private static FamilySymbol LoadExactFamilySymbol(Document target, string path, string familyName, string typeName)
        {
            Document familyDocument = target.Application.OpenDocumentFile(path);
            Family loaded;
            try
            {
                if (!familyDocument.IsFamilyDocument) throw new InvalidDataException("School L4 source is not an RFA family.");
                FamilyManager manager = familyDocument.FamilyManager;
                FamilyType exact = manager.Types.Cast<FamilyType>().SingleOrDefault(x => string.Equals(x.Name, typeName, StringComparison.Ordinal));
                if (exact == null)
                {
                    List<FamilyType> types = manager.Types.Cast<FamilyType>().ToList();
                    FamilyType source = types.SingleOrDefault(x => string.Equals(x.Name, "Type Catalog", StringComparison.Ordinal));
                    if (source == null && types.Count == 1) source = types[0];
                    if (source == null) throw new InvalidDataException("School L4 exact family type is unavailable.");
                    using (Transaction transaction = new Transaction(familyDocument, "AI-Engineer bind approved L4 type"))
                    {
                        transaction.Start(); manager.CurrentType = source; manager.RenameCurrentType(typeName); transaction.Commit();
                    }
                }
                loaded = familyDocument.LoadFamily(target, new SchoolL4FamilyLoadOptions());
            }
            finally { if (familyDocument.IsValidObject) familyDocument.Close(false); }
            if (loaded == null || !string.Equals(loaded.Name, familyName, StringComparison.Ordinal))
                throw new InvalidDataException("School L4 exact family failed to load.");
            FamilySymbol symbol = loaded.GetFamilySymbolIds().Select(x => target.GetElement(x) as FamilySymbol)
                .SingleOrDefault(x => x != null && string.Equals(x.Name, typeName, StringComparison.Ordinal));
            if (symbol == null || symbol.Family.FamilyPlacementType != FamilyPlacementType.OneLevelBased)
                throw new InvalidDataException("School L4 exact OneLevelBased type is missing after load.");
            return symbol;
        }

        private static void VerifyInstance(FamilyInstance instance, SchoolL4Point point, SchoolL4Preview preview, long expectedSymbolId)
        {
            LocationPoint location = instance == null ? null : instance.Location as LocationPoint;
            Parameter mark = instance == null ? null : instance.get_Parameter(BuiltInParameter.ALL_MODEL_MARK);
            if (instance == null || location == null || instance.Symbol == null || instance.Symbol.Family == null
                || (expectedSymbolId > 0 && instance.Symbol.Id.Value != expectedSymbolId)
                || !string.Equals(instance.Symbol.Family.Name, preview.Family.FamilyName, StringComparison.Ordinal)
                || !string.Equals(instance.Symbol.Name, preview.Family.TypeName, StringComparison.Ordinal)
                || instance.LevelId.Value != preview.Room.LevelId
                || Math.Abs(FromFeet(location.Point.X) - point.Xmm) > 0.1
                || Math.Abs(FromFeet(location.Point.Y) - point.Ymm) > 0.1
                || Math.Abs(FromFeet(location.Point.Z) - point.Zmm) > 0.1
                || mark == null || !string.Equals(mark.AsString(), "AIENG-" + point.PointId, StringComparison.Ordinal))
                throw new InvalidDataException("School L4 created instance does not match point " + point.PointId + ".");
        }

        private static int ConnectorCount(FamilyInstance instance)
        {
            try { return instance != null && instance.MEPModel != null && instance.MEPModel.ConnectorManager != null ? instance.MEPModel.ConnectorManager.Connectors.Size : 0; }
            catch { return 0; }
        }

        private static string ResolveStagedFamily(string relative)
        {
            string intake = Path.GetFullPath(Path.Combine(BridgeRoot(), "school-l4-intake"));
            string path = Path.GetFullPath(Path.Combine(intake, relative ?? string.Empty));
            if (!path.StartsWith(intake + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) || !File.Exists(path))
                throw new InvalidDataException("School L4 staged family path is invalid.");
            return path;
        }

        private static bool InvalidCoordinate(double value) { return Double.IsNaN(value) || Double.IsInfinity(value) || Math.Abs(value) > 1000000.0; }
        private static double Mm(double value) { return UnitUtils.ConvertToInternalUnits(value, UnitTypeId.Millimeters); }
        private static double FromFeet(double value) { return UnitUtils.ConvertFromInternalUnits(value, UnitTypeId.Millimeters); }
        private static T Read<T>(string path)
        {
            if (!File.Exists(path) || new FileInfo(path).Length > 131072) throw new InvalidDataException("School L4 evidence is missing or oversized.");
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
            using (FileStream stream = new FileStream(
                path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                return BitConverter.ToString(algorithm.ComputeHash(stream)).Replace("-", string.Empty);
        }
    }
}
