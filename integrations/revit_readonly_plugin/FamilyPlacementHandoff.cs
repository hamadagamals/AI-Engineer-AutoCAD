using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Security.Cryptography;

using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Structure;

namespace AIEngineer.Revit
{
    [DataContract]
    internal sealed class FamilyPlacementPoint
    {
        [DataMember(Name = "x_mm", Order = 1)] public double X;
        [DataMember(Name = "y_mm", Order = 2)] public double Y;
        [DataMember(Name = "z_mm", Order = 3)] public double Z;
        [DataMember(Name = "rotation_degrees", Order = 4)] public double RotationDegrees;
    }

    [DataContract]
    internal sealed class FamilyPlacementPreview
    {
        [DataMember(Name = "schema_version", Order = 1)] public int SchemaVersion;
        [DataMember(Name = "preview_id", Order = 2)] public string PreviewId;
        [DataMember(Name = "expires_utc", Order = 3)] public string ExpiresUtc;
        [DataMember(Name = "source_sha256", Order = 4)] public string SourceSha256;
        [DataMember(Name = "action", Order = 5)] public string Action;
        [DataMember(Name = "family_name", Order = 6)] public string FamilyName;
        [DataMember(Name = "type_name", Order = 7)] public string TypeName;
        [DataMember(Name = "symbol_id", Order = 8)] public long SymbolId;
        [DataMember(Name = "family_placement_type", Order = 9)] public string PlacementType;
        [DataMember(Name = "host_requirement", Order = 10)] public string HostRequirement;
        [DataMember(Name = "level_name", Order = 11)] public string LevelName;
        [DataMember(Name = "level_id", Order = 12)] public long LevelId;
        [DataMember(Name = "reference_element_id", Order = 13)] public long ReferenceElementId;
        [DataMember(Name = "elevation_from_level_mm", Order = 14)] public double ElevationFromLevelMm;
        [DataMember(Name = "placements", Order = 15)] public List<FamilyPlacementPoint> Placements;
        [DataMember(Name = "status", Order = 16)] public string Status;
        [DataMember(Name = "execution_allowed", Order = 17)] public bool ExecutionAllowed;
        [DataMember(Name = "provider_egress_performed", Order = 18)] public bool ProviderEgressPerformed;
        [DataMember(Name = "document_mutation_performed", Order = 19)] public bool DocumentMutationPerformed;
        [DataMember(Name = "preview_sha256", Order = 20)] public string PreviewSha256;
    }

    [DataContract]
    internal sealed class FamilyPlacementHandoffReport
    {
        [DataMember(Name = "schema_version", Order = 1)] public int SchemaVersion;
        [DataMember(Name = "request_id", Order = 2)] public string RequestId;
        [DataMember(Name = "preview_id", Order = 3)] public string PreviewId;
        [DataMember(Name = "preview_sha256", Order = 4)] public string PreviewSha256;
        [DataMember(Name = "source_sha256", Order = 5)] public string SourceSha256;
        [DataMember(Name = "output_path", Order = 6)] public string OutputPath;
        [DataMember(Name = "output_sha256", Order = 7)] public string OutputSha256;
        [DataMember(Name = "created_family_instances", Order = 8)] public int CreatedFamilyInstances;
        [DataMember(Name = "created_element_ids", Order = 9)] public List<long> CreatedElementIds;
        [DataMember(Name = "transaction_committed", Order = 10)] public bool TransactionCommitted;
        [DataMember(Name = "saved_as_new_file", Order = 11)] public bool SavedAsNewFile;
        [DataMember(Name = "provider_egress_performed", Order = 12)] public bool ProviderEgressPerformed;
        [DataMember(Name = "document_mutation_performed", Order = 13)] public bool DocumentMutationPerformed;
    }

    [DataContract]
    internal sealed class FamilyPlacementVerificationReport
    {
        [DataMember(Name = "schema_version", Order = 1)] public int SchemaVersion;
        [DataMember(Name = "request_id", Order = 2)] public string RequestId;
        [DataMember(Name = "handoff_request_id", Order = 3)] public string HandoffRequestId;
        [DataMember(Name = "output_sha256", Order = 4)] public string OutputSha256;
        [DataMember(Name = "verified_family_instances", Order = 5)] public int VerifiedFamilyInstances;
        [DataMember(Name = "verified_element_ids", Order = 6)] public List<long> VerifiedElementIds;
        [DataMember(Name = "placement_matches_preview", Order = 7)] public bool PlacementMatchesPreview;
        [DataMember(Name = "provider_egress_performed", Order = 8)] public bool ProviderEgressPerformed;
        [DataMember(Name = "document_mutation_performed", Order = 9)] public bool DocumentMutationPerformed;
    }

    internal static class FamilyPlacementHandoff
    {
        private const int MaximumPlacements = 32;

        public static string Execute(Document document, string requestId, string previewPath, string outputPath)
        {
            FamilyPlacementPreview preview = Read<FamilyPlacementPreview>(previewPath);
            Validate(preview, true);
            string output = ModelLineHandoff.ValidateNewOutput(document.PathName, outputPath);
            FamilySymbol symbol = document.GetElement(new ElementId(preview.SymbolId)) as FamilySymbol;
            Level level = document.GetElement(new ElementId(preview.LevelId)) as Level;
            FamilyInstance reference = document.GetElement(
                new ElementId(preview.ReferenceElementId)) as FamilyInstance;
            LocationPoint referenceLocation = reference == null ? null : reference.Location as LocationPoint;
            if (symbol == null || level == null || symbol.Family == null
                || !string.Equals(symbol.Family.Name, preview.FamilyName, StringComparison.Ordinal)
                || !string.Equals(symbol.Name, preview.TypeName, StringComparison.Ordinal)
                || symbol.Family.FamilyPlacementType != FamilyPlacementType.OneLevelBased
                || !string.Equals(level.Name, preview.LevelName, StringComparison.Ordinal)
                || reference == null || referenceLocation == null
                || reference.Symbol.Id.Value != symbol.Id.Value
                || reference.LevelId.Value != level.Id.Value
                || Math.Abs(referenceLocation.Point.Z - Point(preview.Placements[0]).Z) > Tolerance()
                || Math.Abs(ElevationFromLevel(reference) - preview.ElevationFromLevelMm) > 0.01)
            {
                throw new InvalidDataException("Revit family placement identity no longer matches the preview.");
            }
            List<FamilyInstance> instances = new List<FamilyInstance>();
            using (Transaction transaction = new Transaction(document, "AI-Engineer approved family placement"))
            {
                transaction.Start();
                if (!symbol.IsActive)
                {
                    symbol.Activate();
                    document.Regenerate();
                }
                foreach (FamilyPlacementPoint placement in preview.Placements)
                {
                    FamilyInstance instance = document.Create.NewFamilyInstance(
                        PointWithElevation(placement, preview.ElevationFromLevelMm), symbol, level,
                        StructuralType.NonStructural);
                    instances.Add(instance);
                }
                document.Regenerate();
                for (int index = 0; index < instances.Count; index++)
                {
                    FamilyInstance instance = instances[index];
                    LocationPoint location = instance == null ? null : instance.Location as LocationPoint;
                    if (location == null)
                    {
                        transaction.RollBack();
                        throw new InvalidDataException("Revit family instance lacks a rotation point.");
                    }
                    double radians = preview.Placements[index].RotationDegrees * Math.PI / 180.0;
                    if (Math.Abs(radians) > 1e-12)
                    {
                        Line axis = Line.CreateBound(location.Point, location.Point + XYZ.BasisZ);
                        ElementTransformUtils.RotateElement(document, instance.Id, axis, radians);
                    }
                }
                document.Regenerate();
                for (int index = 0; index < instances.Count; index++)
                {
                    FamilyInstance instance = instances[index];
                    LocationPoint createdLocation = instance == null ? null : instance.Location as LocationPoint;
                    if (instance == null || createdLocation == null
                        || instance.Symbol.Id.Value != symbol.Id.Value
                        || instance.LevelId.Value != level.Id.Value
                        || createdLocation.Point.DistanceTo(Point(preview.Placements[index])) > Tolerance()
                        || Math.Abs(ElevationFromLevel(instance) - preview.ElevationFromLevelMm) > 0.01
                        || RotationDifferenceDegrees(
                            createdLocation.Rotation, preview.Placements[index].RotationDegrees) > 0.001)
                    {
                        transaction.RollBack();
                        throw new InvalidDataException("Revit did not create every exact approved family instance.");
                    }
                }
                if (transaction.Commit() != TransactionStatus.Committed)
                {
                    throw new InvalidOperationException("Revit family placement transaction did not commit.");
                }
            }
            SaveAsOptions options = new SaveAsOptions();
            options.OverwriteExistingFile = false;
            document.SaveAs(output, options);
            if (!File.Exists(output)) throw new IOException("Revit Save As did not create the approved output.");
            FamilyPlacementHandoffReport report = new FamilyPlacementHandoffReport
            {
                SchemaVersion = 1, RequestId = requestId, PreviewId = preview.PreviewId,
                PreviewSha256 = preview.PreviewSha256, SourceSha256 = preview.SourceSha256,
                OutputPath = output, OutputSha256 = Hash(output), CreatedFamilyInstances = instances.Count,
                CreatedElementIds = instances.ConvertAll(instance => instance.Id.Value), TransactionCommitted = true,
                SavedAsNewFile = true, ProviderEgressPerformed = false, DocumentMutationPerformed = true
            };
            return Write("family-handoffs", requestId, report, typeof(FamilyPlacementHandoffReport));
        }

        public static string Verify(Document document, string requestId, string handoffRequestId, string previewPath)
        {
            FamilyPlacementHandoffReport handoff = Read<FamilyPlacementHandoffReport>(Path.Combine(BridgeRoot(), "family-handoffs", handoffRequestId + ".json"));
            FamilyPlacementPreview preview = Read<FamilyPlacementPreview>(previewPath);
            Validate(preview, false);
            string current = Path.GetFullPath(document.PathName);
            if (!handoff.SavedAsNewFile || !string.Equals(current, handoff.OutputPath, StringComparison.OrdinalIgnoreCase)
                || !string.Equals(Hash(current), handoff.OutputSha256, StringComparison.OrdinalIgnoreCase)
                || handoff.CreatedElementIds == null
                || handoff.CreatedElementIds.Count != preview.Placements.Count
                || handoff.CreatedElementIds.Count < 1 || handoff.CreatedElementIds.Count > MaximumPlacements
                || !string.Equals(handoff.PreviewId, preview.PreviewId, StringComparison.Ordinal)
                || !string.Equals(handoff.PreviewSha256, preview.PreviewSha256, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException("Saved active family handoff identity does not match its evidence.");
            }
            for (int index = 0; index < handoff.CreatedElementIds.Count; index++)
            {
                FamilyInstance instance = document.GetElement(new ElementId(handoff.CreatedElementIds[index])) as FamilyInstance;
                LocationPoint location = instance == null ? null : instance.Location as LocationPoint;
                if (instance == null || location == null || instance.Symbol.Id.Value != preview.SymbolId
                    || instance.LevelId.Value != preview.LevelId
                    || location.Point.DistanceTo(Point(preview.Placements[index])) > Tolerance()
                    || Math.Abs(ElevationFromLevel(instance) - preview.ElevationFromLevelMm) > 0.01
                    || RotationDifferenceDegrees(
                        location.Rotation, preview.Placements[index].RotationDegrees) > 0.001)
                {
                    string actual = location == null ? "unavailable" : MillimeterPoint(location.Point);
                    long actualSymbol = instance == null ? -1 : instance.Symbol.Id.Value;
                    long actualLevel = instance == null ? -1 : instance.LevelId.Value;
                    throw new InvalidDataException(
                        "Saved active family instance at index " + (index + 1)
                        + " does not match the preview. Actual point mm="
                        + actual + "; symbol=" + actualSymbol + "; level=" + actualLevel + ".");
                }
            }
            FamilyPlacementVerificationReport report = new FamilyPlacementVerificationReport
            {
                SchemaVersion = 1, RequestId = requestId, HandoffRequestId = handoffRequestId,
                OutputSha256 = handoff.OutputSha256, VerifiedFamilyInstances = handoff.CreatedElementIds.Count,
                VerifiedElementIds = handoff.CreatedElementIds, PlacementMatchesPreview = true,
                ProviderEgressPerformed = false, DocumentMutationPerformed = false
            };
            return Write("family-verifications", requestId, report, typeof(FamilyPlacementVerificationReport));
        }

        private static void Validate(FamilyPlacementPreview preview, bool requireUnexpired)
        {
            DateTime expires;
            if (preview == null || preview.SchemaVersion != 1 || preview.SymbolId <= 0 || preview.LevelId <= 0
                || preview.ReferenceElementId <= 0 || Double.IsNaN(preview.ElevationFromLevelMm)
                || Double.IsInfinity(preview.ElevationFromLevelMm)
                || Math.Abs(preview.ElevationFromLevelMm) > 1000000.0
                || preview.Placements == null || preview.Placements.Count < 1
                || preview.Placements.Count > MaximumPlacements || preview.ExecutionAllowed
                || preview.ProviderEgressPerformed || preview.DocumentMutationPerformed
                || !string.Equals(preview.Action, "place_family_instances", StringComparison.Ordinal)
                || !string.Equals(preview.Status, "review_required", StringComparison.Ordinal)
                || !string.Equals(preview.PlacementType, "OneLevelBased", StringComparison.Ordinal)
                || !string.Equals(preview.HostRequirement, "level", StringComparison.Ordinal)
                || string.IsNullOrWhiteSpace(preview.PreviewId) || string.IsNullOrWhiteSpace(preview.PreviewSha256)
                || !DateTime.TryParse(preview.ExpiresUtc, null, System.Globalization.DateTimeStyles.RoundtripKind, out expires)
                || (requireUnexpired && expires.ToUniversalTime() <= DateTime.UtcNow))
            {
                throw new InvalidDataException("Approved Revit family placement preview is invalid or expired.");
            }
            double expectedZ = Point(preview.Placements[0]).Z;
            foreach (FamilyPlacementPoint placement in preview.Placements)
            {
                XYZ point = Point(placement);
                if (Math.Abs(point.Z - expectedZ) > Tolerance()
                    || Double.IsNaN(placement.RotationDegrees)
                    || Double.IsInfinity(placement.RotationDegrees)
                    || placement.RotationDegrees < 0.0 || placement.RotationDegrees >= 360.0)
                {
                    throw new InvalidDataException(
                        "OneLevelBased placements require one physical elevation and normalized rotations.");
                }
            }
        }

        private static XYZ Point(FamilyPlacementPoint value)
        {
            if (value == null || Double.IsNaN(value.X) || Double.IsInfinity(value.X)
                || Double.IsNaN(value.Y) || Double.IsInfinity(value.Y)
                || Double.IsNaN(value.Z) || Double.IsInfinity(value.Z)
                || Math.Abs(value.X) > 1000000.0 || Math.Abs(value.Y) > 1000000.0
                || Math.Abs(value.Z) > 1000000.0)
            {
                throw new InvalidDataException("Family placement point is invalid.");
            }
            return new XYZ(UnitUtils.ConvertToInternalUnits(value.X, UnitTypeId.Millimeters), UnitUtils.ConvertToInternalUnits(value.Y, UnitTypeId.Millimeters), UnitUtils.ConvertToInternalUnits(value.Z, UnitTypeId.Millimeters));
        }
        private static XYZ PointWithElevation(FamilyPlacementPoint value, double elevationFromLevelMm)
        {
            XYZ physical = Point(value);
            return new XYZ(
                physical.X,
                physical.Y,
                UnitUtils.ConvertToInternalUnits(elevationFromLevelMm, UnitTypeId.Millimeters));
        }
        private static double ElevationFromLevel(FamilyInstance instance)
        {
            Parameter parameter = instance.get_Parameter(BuiltInParameter.INSTANCE_ELEVATION_PARAM);
            if (parameter == null || !parameter.HasValue || parameter.StorageType != StorageType.Double)
            {
                throw new InvalidDataException("Revit family instance lacks Elevation from Level.");
            }
            return UnitUtils.ConvertFromInternalUnits(parameter.AsDouble(), UnitTypeId.Millimeters);
        }
        private static double Tolerance() { return UnitUtils.ConvertToInternalUnits(0.01, UnitTypeId.Millimeters); }
        private static double RotationDifferenceDegrees(double actualRadians, double expectedDegrees)
        {
            double actual = actualRadians * 180.0 / Math.PI;
            actual = ((actual % 360.0) + 360.0) % 360.0;
            double difference = Math.Abs(actual - expectedDegrees);
            return Math.Min(difference, 360.0 - difference);
        }
        private static string MillimeterPoint(XYZ value)
        {
            return string.Format(
                System.Globalization.CultureInfo.InvariantCulture,
                "({0:F3},{1:F3},{2:F3})",
                UnitUtils.ConvertFromInternalUnits(value.X, UnitTypeId.Millimeters),
                UnitUtils.ConvertFromInternalUnits(value.Y, UnitTypeId.Millimeters),
                UnitUtils.ConvertFromInternalUnits(value.Z, UnitTypeId.Millimeters));
        }
        private static T Read<T>(string path)
        {
            if (!File.Exists(path) || new FileInfo(path).Length > 65536) throw new InvalidDataException("Revit family handoff evidence is missing or oversized.");
            DataContractJsonSerializer serializer = new DataContractJsonSerializer(typeof(T));
            using (FileStream stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read)) return (T)serializer.ReadObject(stream);
        }
        private static string Write(string folder, string requestId, object value, Type type)
        {
            string directory = Path.Combine(BridgeRoot(), folder); Directory.CreateDirectory(directory);
            string path = Path.Combine(directory, requestId + ".json");
            DataContractJsonSerializer serializer = new DataContractJsonSerializer(type);
            using (FileStream stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { serializer.WriteObject(stream, value); stream.Flush(true); }
            return folder + "/" + requestId + ".json";
        }
        private static string BridgeRoot() { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AI-Engineer", "revit"); }
        private static string Hash(string path)
        {
            using (SHA256 algorithm = SHA256.Create()) using (FileStream stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite)) return BitConverter.ToString(algorithm.ComputeHash(stream)).Replace("-", string.Empty);
        }
    }
}
