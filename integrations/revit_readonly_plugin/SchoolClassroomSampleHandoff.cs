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
    internal sealed class ClassroomSampleSource
    {
        [DataMember(Name = "model_sha256", Order = 1)] public string ModelSha256;
        [DataMember(Name = "document_title", Order = 2)] public string DocumentTitle;
    }

    [DataContract]
    internal sealed class ClassroomSampleFamily
    {
        [DataMember(Name = "sha256", Order = 1)] public string Sha256;
        [DataMember(Name = "family_name", Order = 2)] public string FamilyName;
        [DataMember(Name = "type_name", Order = 3)] public string TypeName;
        [DataMember(Name = "placement_type", Order = 4)] public string PlacementType;
        [DataMember(Name = "voltage_v", Order = 5)] public double VoltageV;
        [DataMember(Name = "apparent_load_va", Order = 6)] public double ApparentLoadVa;
        [DataMember(Name = "active_power_w", Order = 7)] public double ActivePowerW;
    }

    [DataContract]
    internal sealed class ClassroomSamplePhotometry
    {
        [DataMember(Name = "sha256", Order = 1)] public string Sha256;
        [DataMember(Name = "lumens", Order = 2)] public double Lumens;
        [DataMember(Name = "input_w", Order = 3)] public double InputW;
        [DataMember(Name = "cct_k", Order = 4)] public int CctK;
    }

    [DataContract]
    internal sealed class ClassroomSampleRoom
    {
        [DataMember(Name = "element_id", Order = 1)] public long ElementId;
        [DataMember(Name = "name", Order = 2)] public string Name;
        [DataMember(Name = "level_id", Order = 3)] public long LevelId;
        [DataMember(Name = "level_name", Order = 4)] public string LevelName;
    }

    [DataContract]
    internal sealed class ClassroomSampleCeiling
    {
        [DataMember(Name = "element_id", Order = 1)] public long ElementId;
        [DataMember(Name = "name", Order = 2)] public string Name;
        [DataMember(Name = "underside_z_mm", Order = 3)] public double UndersideZmm;
    }

    [DataContract]
    internal sealed class ClassroomSamplePoint
    {
        [DataMember(Name = "index", Order = 1)] public int Index;
        [DataMember(Name = "point_id", Order = 2)] public string PointId;
        [DataMember(Name = "x_mm", Order = 3)] public double Xmm;
        [DataMember(Name = "y_mm", Order = 4)] public double Ymm;
        [DataMember(Name = "z_mm", Order = 5)] public double Zmm;
        [DataMember(Name = "rotation_degrees", Order = 6)] public double RotationDegrees;
    }

    [DataContract]
    internal sealed class ClassroomSamplePlacement
    {
        [DataMember(Name = "placement_count", Order = 1)] public int PlacementCount;
        [DataMember(Name = "mounting_z_mm", Order = 2)] public double MountingZmm;
        [DataMember(Name = "points", Order = 3)] public List<ClassroomSamplePoint> Points;
    }

    [DataContract]
    internal sealed class ClassroomSampleReviewScope
    {
        [DataMember(Name = "sample_only", Order = 1)] public bool SampleOnly;
        [DataMember(Name = "bulk_distribution_authorized", Order = 2)] public bool BulkDistributionAuthorized;
        [DataMember(Name = "claim_of_code_compliance", Order = 3)] public bool ClaimOfCodeCompliance;
    }

    [DataContract]
    internal sealed class ClassroomSamplePreview
    {
        [DataMember(Name = "schema_version", Order = 1)] public int SchemaVersion;
        [DataMember(Name = "preview_id", Order = 2)] public string PreviewId;
        [DataMember(Name = "expires_utc", Order = 3)] public string ExpiresUtc;
        [DataMember(Name = "action", Order = 4)] public string Action;
        [DataMember(Name = "status", Order = 5)] public string Status;
        [DataMember(Name = "execution_allowed", Order = 6)] public bool ExecutionAllowed;
        [DataMember(Name = "provider_egress_performed", Order = 7)] public bool ProviderEgressPerformed;
        [DataMember(Name = "document_mutation_performed", Order = 8)] public bool DocumentMutationPerformed;
        [DataMember(Name = "source", Order = 9)] public ClassroomSampleSource Source;
        [DataMember(Name = "room", Order = 10)] public ClassroomSampleRoom Room;
        [DataMember(Name = "ceiling", Order = 11)] public ClassroomSampleCeiling Ceiling;
        [DataMember(Name = "family", Order = 12)] public ClassroomSampleFamily Family;
        [DataMember(Name = "photometry", Order = 13)] public ClassroomSamplePhotometry Photometry;
        [DataMember(Name = "placement", Order = 14)] public ClassroomSamplePlacement Placement;
        [DataMember(Name = "review_scope", Order = 15)] public ClassroomSampleReviewScope ReviewScope;
        [DataMember(Name = "preview_sha256", Order = 16)] public string PreviewSha256;
    }

    [DataContract]
    internal sealed class ClassroomSampleReport
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
        [DataMember(Name = "created_family_instances", Order = 10)] public int CreatedFamilyInstances;
        [DataMember(Name = "created_element_ids", Order = 11)] public List<long> CreatedElementIds;
        [DataMember(Name = "connector_counts", Order = 12)] public List<int> ConnectorCounts;
        [DataMember(Name = "preserved_l4_instances", Order = 13)] public int PreservedL4Instances;
        [DataMember(Name = "apparent_load_va", Order = 14)] public double ApparentLoadVa;
        [DataMember(Name = "wattage_comments", Order = 15)] public string WattageComments;
        [DataMember(Name = "photometric_web_file", Order = 16)] public string PhotometricWebFile;
        [DataMember(Name = "transaction_committed", Order = 17)] public bool TransactionCommitted;
        [DataMember(Name = "saved_as_new_file", Order = 18)] public bool SavedAsNewFile;
        [DataMember(Name = "provider_egress_performed", Order = 19)] public bool ProviderEgressPerformed;
        [DataMember(Name = "document_mutation_performed", Order = 20)] public bool DocumentMutationPerformed;
    }

    [DataContract]
    internal sealed class ClassroomSampleVerificationReport
    {
        [DataMember(Name = "schema_version", Order = 1)] public int SchemaVersion;
        [DataMember(Name = "request_id", Order = 2)] public string RequestId;
        [DataMember(Name = "handoff_request_id", Order = 3)] public string HandoffRequestId;
        [DataMember(Name = "output_sha256", Order = 4)] public string OutputSha256;
        [DataMember(Name = "verified_family_instances", Order = 5)] public int VerifiedFamilyInstances;
        [DataMember(Name = "verified_element_ids", Order = 6)] public List<long> VerifiedElementIds;
        [DataMember(Name = "placement_matches_preview", Order = 7)] public bool PlacementMatchesPreview;
        [DataMember(Name = "connector_counts", Order = 8)] public List<int> ConnectorCounts;
        [DataMember(Name = "preserved_l4_instances", Order = 9)] public int PreservedL4Instances;
        [DataMember(Name = "parameters_match_preview", Order = 10)] public bool ParametersMatchPreview;
        [DataMember(Name = "provider_egress_performed", Order = 11)] public bool ProviderEgressPerformed;
        [DataMember(Name = "document_mutation_performed", Order = 12)] public bool DocumentMutationPerformed;
    }

    internal static class SchoolClassroomSampleHandoff
    {
        private const string ApprovedFingerprint = "0293A7A6A70095DF2C2845754998C1A555C4C9315D48FB50B7E4BF2E9939B116";
        private const string ApprovedFamilyHash = "7AEC687A27A089F4F607B78BEF730B9D1B58C65CE0529E7730EC2699E826DAC2";
        private const string ApprovedIesHash = "7970BE1E07E695A158B20ACB80878EF70A4B9D103462FFC705F7AAB58F83D18D";
        private const int ExactCount = 8;
        private const int PreservedL4Count = 45;

        public static string Execute(Document document, string requestId, string previewPath, string outputPath, string familyRelativePath, string iesRelativePath)
        {
            ClassroomSamplePreview preview = Read<ClassroomSamplePreview>(previewPath);
            Validate(preview, true);
            string output = ModelLineHandoff.ValidateNewOutput(document.PathName, outputPath);
            if (!string.Equals(Hash(document.PathName), preview.Source.ModelSha256, StringComparison.OrdinalIgnoreCase)
                || !string.Equals(document.Title, preview.Source.DocumentTitle, StringComparison.Ordinal))
                throw new InvalidDataException("Classroom sample source identity changed.");
            string familyPath = ResolveIntake(familyRelativePath, ".rfa");
            string iesPath = ResolveIntake(iesRelativePath, ".ies");
            if (!string.Equals(Hash(familyPath), ApprovedFamilyHash, StringComparison.OrdinalIgnoreCase)
                || !string.Equals(Hash(iesPath), ApprovedIesHash, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Classroom sample family or IES hash changed.");
            Level level = document.GetElement(new ElementId(preview.Room.LevelId)) as Level;
            Element room = document.GetElement(new ElementId(preview.Room.ElementId));
            Element ceiling = document.GetElement(new ElementId(preview.Ceiling.ElementId));
            if (level == null || room == null || ceiling == null
                || !string.Equals(level.Name, preview.Room.LevelName, StringComparison.Ordinal)
                || !string.Equals(room.Name, preview.Room.Name, StringComparison.Ordinal))
                throw new InvalidDataException("Classroom sample room, ceiling, or level identity changed.");
            List<long> preservedIds = L4Instances(document).Select(x => x.Id.Value).OrderBy(x => x).ToList();
            if (preservedIds.Count != PreservedL4Count)
                throw new InvalidDataException("The verified L4 fixture set changed before classroom placement.");

            string installedIes = InstallIes(document, iesPath);
            FamilySymbol symbol = LoadExactFamilySymbol(document, familyPath, preview.Family.FamilyName, preview.Family.TypeName);
            List<FamilyInstance> created = new List<FamilyInstance>();
            using (Transaction transaction = new Transaction(document, "AI-Engineer approved CLASSROOM 1500 sample"))
            {
                transaction.Start();
                if (!symbol.IsActive) { symbol.Activate(); document.Regenerate(); }
                SetApprovedParameters(symbol, installedIes, preview);
                double relativeZ = Mm(preview.Placement.MountingZmm) - level.Elevation;
                foreach (ClassroomSamplePoint point in preview.Placement.Points)
                {
                    FamilyInstance instance = document.Create.NewFamilyInstance(
                        new XYZ(Mm(point.Xmm), Mm(point.Ymm), relativeZ), symbol, level, StructuralType.NonStructural);
                    Parameter mark = instance.get_Parameter(BuiltInParameter.ALL_MODEL_MARK);
                    if (mark == null || mark.IsReadOnly || !mark.Set("AIENG-" + point.PointId))
                    {
                        transaction.RollBack();
                        throw new InvalidDataException("Classroom sample instance mark could not be bound.");
                    }
                    created.Add(instance);
                }
                document.Regenerate();
                for (int index = 0; index < ExactCount; index++) VerifyInstance(created[index], preview.Placement.Points[index], preview, symbol.Id.Value);
                VerifyParameters(symbol, preview);
                if (transaction.Commit() != TransactionStatus.Committed)
                    throw new InvalidOperationException("Classroom sample transaction did not commit.");
            }
            if (!preservedIds.SequenceEqual(L4Instances(document).Select(x => x.Id.Value).OrderBy(x => x)))
                throw new InvalidDataException("L4 fixtures changed during classroom placement.");
            document.SaveAs(output, new SaveAsOptions { OverwriteExistingFile = false });
            if (!File.Exists(output)) throw new IOException("Classroom sample Save As did not create the approved output.");
            ClassroomSampleReport report = new ClassroomSampleReport
            {
                SchemaVersion = 1, RequestId = requestId, PreviewId = preview.PreviewId,
                PreviewSha256 = preview.PreviewSha256, SourceSha256 = preview.Source.ModelSha256,
                OutputPath = output, OutputSha256 = Hash(output), FamilyName = preview.Family.FamilyName,
                TypeName = preview.Family.TypeName, CreatedFamilyInstances = created.Count,
                CreatedElementIds = created.Select(x => x.Id.Value).ToList(),
                ConnectorCounts = created.Select(ConnectorCount).ToList(), PreservedL4Instances = PreservedL4Count,
                ApparentLoadVa = ReadLoad(symbol), WattageComments = Required(symbol, "Wattage Comments").AsString(),
                PhotometricWebFile = Path.GetFileName(Required(symbol, "Photometric Web File").AsString()),
                TransactionCommitted = true, SavedAsNewFile = true,
                ProviderEgressPerformed = false, DocumentMutationPerformed = true
            };
            return Write("school-classroom-sample-handoffs", requestId, report, typeof(ClassroomSampleReport));
        }

        public static string Verify(Document document, string requestId, string handoffRequestId, string previewPath)
        {
            ClassroomSamplePreview preview = Read<ClassroomSamplePreview>(previewPath);
            Validate(preview, false);
            ClassroomSampleReport handoff = Read<ClassroomSampleReport>(Path.Combine(BridgeRoot(), "school-classroom-sample-handoffs", handoffRequestId + ".json"));
            string current = Path.GetFullPath(document.PathName);
            if (!string.Equals(current, handoff.OutputPath, StringComparison.OrdinalIgnoreCase)
                || !string.Equals(Hash(current), handoff.OutputSha256, StringComparison.OrdinalIgnoreCase)
                || handoff.CreatedElementIds == null || handoff.CreatedElementIds.Count != ExactCount)
                throw new InvalidDataException("Reopened classroom sample output identity changed.");
            List<int> connectors = new List<int>();
            for (int index = 0; index < ExactCount; index++)
            {
                FamilyInstance instance = document.GetElement(new ElementId(handoff.CreatedElementIds[index])) as FamilyInstance;
                VerifyInstance(instance, preview.Placement.Points[index], preview, -1);
                connectors.Add(ConnectorCount(instance));
            }
            FamilySymbol symbol = ExactSymbol(document, preview);
            VerifyParameters(symbol, preview);
            int preserved = L4Instances(document).Count;
            if (connectors.Any(x => x < 1) || preserved != PreservedL4Count)
                throw new InvalidDataException("Reopened classroom sample connector or L4 preservation check failed.");
            ClassroomSampleVerificationReport report = new ClassroomSampleVerificationReport
            {
                SchemaVersion = 1, RequestId = requestId, HandoffRequestId = handoffRequestId,
                OutputSha256 = handoff.OutputSha256, VerifiedFamilyInstances = ExactCount,
                VerifiedElementIds = handoff.CreatedElementIds, PlacementMatchesPreview = true,
                ConnectorCounts = connectors, PreservedL4Instances = preserved, ParametersMatchPreview = true,
                ProviderEgressPerformed = false, DocumentMutationPerformed = false
            };
            return Write("school-classroom-sample-verifications", requestId, report, typeof(ClassroomSampleVerificationReport));
        }

        private static void Validate(ClassroomSamplePreview preview, bool requireUnexpired)
        {
            DateTime expires;
            if (preview == null || preview.SchemaVersion != 1 || preview.Source == null || preview.Room == null
                || preview.Ceiling == null || preview.Family == null || preview.Photometry == null
                || preview.Placement == null || preview.Placement.Points == null || preview.ReviewScope == null
                || preview.Placement.PlacementCount != ExactCount || preview.Placement.Points.Count != ExactCount
                || preview.ExecutionAllowed || preview.ProviderEgressPerformed || preview.DocumentMutationPerformed
                || !preview.ReviewScope.SampleOnly || preview.ReviewScope.BulkDistributionAuthorized || preview.ReviewScope.ClaimOfCodeCompliance
                || !string.Equals(preview.Action, "load_l1_family_place_classroom1500_sample_save_as", StringComparison.Ordinal)
                || !string.Equals(preview.Status, "review_required", StringComparison.Ordinal)
                || !string.Equals(preview.PreviewSha256, ApprovedFingerprint, StringComparison.Ordinal)
                || !string.Equals(preview.Family.Sha256, ApprovedFamilyHash, StringComparison.Ordinal)
                || !string.Equals(preview.Photometry.Sha256, ApprovedIesHash, StringComparison.Ordinal)
                || !string.Equals(preview.Family.PlacementType, "OneLevelBased", StringComparison.Ordinal)
                || preview.Room.ElementId != 221093 || preview.Room.LevelId != 30 || preview.Ceiling.ElementId != 278813
                || Math.Abs(preview.Placement.MountingZmm - 2743.2) > 0.001
                || Math.Abs(preview.Family.ApparentLoadVa - 31.0) > 0.001
                || Math.Abs(preview.Family.ActivePowerW - 31.0) > 0.001
                || Math.Abs(preview.Photometry.Lumens - 4310.8) > 0.001
                || !DateTime.TryParse(preview.ExpiresUtc, null, System.Globalization.DateTimeStyles.RoundtripKind, out expires)
                || (requireUnexpired && expires.ToUniversalTime() <= DateTime.UtcNow))
                throw new InvalidDataException("Approved classroom sample preview is invalid or expired.");
            HashSet<string> ids = new HashSet<string>();
            for (int index = 0; index < ExactCount; index++)
            {
                ClassroomSamplePoint point = preview.Placement.Points[index];
                if (point == null || point.Index != index + 1 || string.IsNullOrWhiteSpace(point.PointId)
                    || !ids.Add(point.PointId) || !point.PointId.StartsWith("L1-CLASSROOM-1500-", StringComparison.Ordinal)
                    || Math.Abs(point.Zmm - 2743.2) > 0.001 || Math.Abs(point.RotationDegrees) > 0.001
                    || InvalidCoordinate(point.Xmm) || InvalidCoordinate(point.Ymm))
                    throw new InvalidDataException("Approved classroom sample point contract is invalid.");
            }
        }

        private static FamilySymbol LoadExactFamilySymbol(Document target, string path, string familyName, string typeName)
        {
            Document familyDocument = target.Application.OpenDocumentFile(path);
            Family loaded;
            try
            {
                if (!familyDocument.IsFamilyDocument) throw new InvalidDataException("Classroom sample source is not an RFA family.");
                FamilyManager manager = familyDocument.FamilyManager;
                FamilyType exact = manager.Types.Cast<FamilyType>().SingleOrDefault(x => string.Equals(x.Name, typeName, StringComparison.Ordinal));
                if (exact == null)
                {
                    List<FamilyType> types = manager.Types.Cast<FamilyType>().ToList();
                    FamilyType source = types.SingleOrDefault(x => string.Equals(x.Name, "Type Catalog", StringComparison.Ordinal));
                    if (source == null && types.Count == 1) source = types[0];
                    if (source == null) throw new InvalidDataException("Classroom sample exact family type is unavailable.");
                    using (Transaction transaction = new Transaction(familyDocument, "AI-Engineer bind approved classroom type"))
                    {
                        transaction.Start(); manager.CurrentType = source; manager.RenameCurrentType(typeName); transaction.Commit();
                    }
                }
                loaded = familyDocument.LoadFamily(target, new SchoolL4FamilyLoadOptions());
            }
            finally { if (familyDocument.IsValidObject) familyDocument.Close(false); }
            if (loaded == null || !string.Equals(loaded.Name, familyName, StringComparison.Ordinal))
                throw new InvalidDataException("Classroom sample exact family failed to load.");
            return ExactSymbol(target, familyName, typeName);
        }

        private static FamilySymbol ExactSymbol(Document document, ClassroomSamplePreview preview)
        {
            return ExactSymbol(document, preview.Family.FamilyName, preview.Family.TypeName);
        }

        private static FamilySymbol ExactSymbol(Document document, string familyName, string typeName)
        {
            List<FamilySymbol> symbols = new FilteredElementCollector(document).OfClass(typeof(FamilySymbol)).Cast<FamilySymbol>()
                .Where(x => x.Family != null && string.Equals(x.Family.Name, familyName, StringComparison.Ordinal)
                    && string.Equals(x.Name, typeName, StringComparison.Ordinal)).ToList();
            if (symbols.Count != 1 || symbols[0].Family.FamilyPlacementType != FamilyPlacementType.OneLevelBased)
                throw new InvalidDataException("Classroom sample exact OneLevelBased type is missing or ambiguous.");
            return symbols[0];
        }

        private static void SetApprovedParameters(FamilySymbol symbol, string installedIes, ClassroomSamplePreview preview)
        {
            Parameter load = Required(symbol, "Apparent Load");
            Parameter wattage = Required(symbol, "Wattage Comments");
            Parameter photometric = Required(symbol, "Photometric Web File");
            if (Math.Abs(ReadLoad(symbol) - 19.0) > 0.01 || !string.Equals(wattage.AsString(), "19W", StringComparison.Ordinal))
                throw new InvalidDataException("Classroom sample starting type parameters changed.");
            if (!load.Set(UnitUtils.ConvertToInternalUnits(preview.Family.ApparentLoadVa, load.GetUnitTypeId()))
                || !wattage.Set("31W") || !photometric.Set(installedIes))
                throw new InvalidOperationException("Approved classroom sample parameters could not be set.");
        }

        private static void VerifyParameters(FamilySymbol symbol, ClassroomSamplePreview preview)
        {
            string wattage = Required(symbol, "Wattage Comments").AsString();
            string photometric = Path.GetFileName(Required(symbol, "Photometric Web File").AsString());
            if (Math.Abs(ReadLoad(symbol) - preview.Family.ApparentLoadVa) > 0.01
                || !string.Equals(wattage, "31W", StringComparison.Ordinal)
                || !string.Equals(photometric, "RC132V G6 29_36_43S_830_40 PSD W60L60 OC_6.ies", StringComparison.Ordinal))
                throw new InvalidDataException("Classroom sample type parameters do not match the preview.");
        }

        private static void VerifyInstance(FamilyInstance instance, ClassroomSamplePoint point, ClassroomSamplePreview preview, long symbolId)
        {
            LocationPoint location = instance == null ? null : instance.Location as LocationPoint;
            Parameter mark = instance == null ? null : instance.get_Parameter(BuiltInParameter.ALL_MODEL_MARK);
            if (instance == null || location == null || instance.Symbol == null || instance.Symbol.Family == null
                || (symbolId > 0 && instance.Symbol.Id.Value != symbolId)
                || !string.Equals(instance.Symbol.Family.Name, preview.Family.FamilyName, StringComparison.Ordinal)
                || !string.Equals(instance.Symbol.Name, preview.Family.TypeName, StringComparison.Ordinal)
                || instance.LevelId.Value != preview.Room.LevelId
                || Math.Abs(FromFeet(location.Point.X) - point.Xmm) > 0.1
                || Math.Abs(FromFeet(location.Point.Y) - point.Ymm) > 0.1
                || Math.Abs(FromFeet(location.Point.Z) - point.Zmm) > 0.1
                || mark == null || !string.Equals(mark.AsString(), "AIENG-" + point.PointId, StringComparison.Ordinal))
                throw new InvalidDataException("Classroom sample instance does not match " + point.PointId + ".");
        }

        private static List<FamilyInstance> L4Instances(Document document)
        {
            return new FilteredElementCollector(document).OfClass(typeof(FamilyInstance)).Cast<FamilyInstance>()
                .Where(x => { Parameter mark = x.get_Parameter(BuiltInParameter.ALL_MODEL_MARK); return mark != null && (mark.AsString() ?? string.Empty).StartsWith("AIENG-L4-GYM-", StringComparison.Ordinal); }).ToList();
        }

        private static string InstallIes(Document document, string source)
        {
            string directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                "Autodesk", "RVT " + document.Application.VersionNumber, "IES");
            if (!Directory.Exists(directory)) throw new DirectoryNotFoundException("Revit IES directory is unavailable.");
            string destination = Path.Combine(directory, Path.GetFileName(source));
            if (File.Exists(destination))
            {
                if (!string.Equals(Hash(destination), ApprovedIesHash, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("Existing Revit IES differs from approved classroom evidence.");
            }
            else
            {
                File.Copy(source, destination, false);
                if (!string.Equals(Hash(destination), ApprovedIesHash, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("Copied classroom IES failed hash verification.");
            }
            return destination;
        }

        private static Parameter Required(Element element, string name)
        {
            Parameter parameter = element.LookupParameter(name);
            if (parameter == null || parameter.IsReadOnly) throw new InvalidDataException("Required writable classroom parameter is unavailable: " + name);
            return parameter;
        }

        private static double ReadLoad(FamilySymbol symbol)
        {
            Parameter load = Required(symbol, "Apparent Load");
            return Math.Round(UnitUtils.ConvertFromInternalUnits(load.AsDouble(), load.GetUnitTypeId()), 3);
        }

        private static int ConnectorCount(FamilyInstance instance)
        {
            try { return instance != null && instance.MEPModel != null && instance.MEPModel.ConnectorManager != null ? instance.MEPModel.ConnectorManager.Connectors.Size : 0; }
            catch { return 0; }
        }

        private static string ResolveIntake(string relative, string extension)
        {
            string intake = Path.GetFullPath(Path.Combine(BridgeRoot(), "school-classroom-intake"));
            string path = Path.GetFullPath(Path.Combine(intake, relative ?? string.Empty));
            if (!path.StartsWith(intake + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                || !File.Exists(path) || !string.Equals(Path.GetExtension(path), extension, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Classroom sample staged file path is invalid.");
            return path;
        }

        private static bool InvalidCoordinate(double value) { return Double.IsNaN(value) || Double.IsInfinity(value) || Math.Abs(value) > 1000000.0; }
        private static double Mm(double value) { return UnitUtils.ConvertToInternalUnits(value, UnitTypeId.Millimeters); }
        private static double FromFeet(double value) { return UnitUtils.ConvertFromInternalUnits(value, UnitTypeId.Millimeters); }
        private static T Read<T>(string path)
        {
            if (!File.Exists(path) || new FileInfo(path).Length > 131072) throw new InvalidDataException("Classroom sample evidence is missing or oversized.");
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
