using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Security.Cryptography;

using Autodesk.Revit.DB;

namespace AIEngineer.Revit
{
    [DataContract]
    internal sealed class HostedFaceBinding
    {
        [DataMember(Name = "kind", Order = 1)] public string Kind;
        [DataMember(Name = "link_instance_id", Order = 2)] public long LinkInstanceId;
        [DataMember(Name = "linked_element_id", Order = 3)] public long LinkedElementId;
        [DataMember(Name = "linked_document_title", Order = 4)] public string LinkedDocumentTitle;
        [DataMember(Name = "linked_category_key", Order = 5)] public string LinkedCategoryKey;
        [DataMember(Name = "face_stable_reference", Order = 6)] public string FaceStableReference;
    }

    [DataContract]
    internal sealed class HostedPlacementPoint
    {
        [DataMember(Name = "x_mm", Order = 1)] public double X;
        [DataMember(Name = "y_mm", Order = 2)] public double Y;
        [DataMember(Name = "z_mm", Order = 3)] public double Z;
        [DataMember(Name = "rotation_degrees", Order = 4)] public double RotationDegrees;
        [DataMember(Name = "host_face", Order = 5)] public HostedFaceBinding HostFace;
    }

    [DataContract]
    internal sealed class HostedPlacementPreview
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
        [DataMember(Name = "reference_element_id", Order = 13)] public long ReferenceElementId;
        [DataMember(Name = "placements", Order = 15)] public List<HostedPlacementPoint> Placements;
        [DataMember(Name = "status", Order = 16)] public string Status;
        [DataMember(Name = "execution_allowed", Order = 17)] public bool ExecutionAllowed;
        [DataMember(Name = "provider_egress_performed", Order = 18)] public bool ProviderEgressPerformed;
        [DataMember(Name = "document_mutation_performed", Order = 19)] public bool DocumentMutationPerformed;
        [DataMember(Name = "preview_sha256", Order = 20)] public string PreviewSha256;
    }

    internal static class HostedFamilyPlacementHandoff
    {
        private const double MinimumCenterClearanceMm = 250.0;

        public static string Execute(Document document, string requestId, string previewPath, string outputPath)
        {
            HostedPlacementPreview preview = Read<HostedPlacementPreview>(previewPath);
            Validate(preview, true);
            string output = ModelLineHandoff.ValidateNewOutput(document.PathName, outputPath);
            HostedPlacementPoint placement = preview.Placements[0];
            FamilySymbol symbol = document.GetElement(new ElementId(preview.SymbolId)) as FamilySymbol;
            FamilyInstance exemplar = document.GetElement(new ElementId(preview.ReferenceElementId)) as FamilyInstance;
            Reference exemplarHost = exemplar == null ? null : exemplar.HostFace;
            Reference hostReference;
            PlanarFace linkedFace;
            Transform linkTransform;
            ResolveHost(document, placement.HostFace, out hostReference, out linkedFace, out linkTransform);
            if (symbol == null || symbol.Family == null || exemplar == null || exemplarHost == null
                || !string.Equals(symbol.Family.Name, preview.FamilyName, StringComparison.Ordinal)
                || !string.Equals(symbol.Name, preview.TypeName, StringComparison.Ordinal)
                || symbol.Family.FamilyPlacementType != FamilyPlacementType.WorkPlaneBased
                || exemplar.Symbol.Id.Value != symbol.Id.Value
                || !string.Equals(exemplarHost.ConvertToStableRepresentation(document),
                    placement.HostFace.FaceStableReference, StringComparison.Ordinal))
            {
                throw new InvalidDataException("Revit hosted family identity no longer matches the preview.");
            }
            XYZ point = Point(placement);
            VerifyPointOnFace(point, linkedFace, linkTransform);
            VerifyClearance(document, placement.HostFace.FaceStableReference, point);
            FamilyInstance instance;
            using (Transaction transaction = new Transaction(document, "AI-Engineer approved hosted family placement"))
            {
                transaction.Start();
                if (!symbol.IsActive)
                {
                    symbol.Activate();
                    document.Regenerate();
                }
                XYZ normal = linkTransform.OfVector(linkedFace.FaceNormal).Normalize();
                XYZ basis = Math.Abs(normal.DotProduct(XYZ.BasisX)) < 0.9 ? XYZ.BasisX : XYZ.BasisY;
                XYZ direction = (basis - normal.Multiply(basis.DotProduct(normal))).Normalize();
                instance = document.Create.NewFamilyInstance(hostReference, point, direction, symbol);
                document.Regenerate();
                LocationPoint location = instance == null ? null : instance.Location as LocationPoint;
                Reference createdHost = instance == null ? null : instance.HostFace;
                if (instance == null || location == null || createdHost == null
                    || instance.Symbol.Id.Value != symbol.Id.Value
                    || location.Point.DistanceTo(point) > Tolerance()
                    || !string.Equals(createdHost.ConvertToStableRepresentation(document),
                        placement.HostFace.FaceStableReference, StringComparison.Ordinal))
                {
                    transaction.RollBack();
                    throw new InvalidDataException("Revit did not create the exact approved hosted family instance.");
                }
                if (transaction.Commit() != TransactionStatus.Committed)
                {
                    throw new InvalidOperationException("Revit hosted family transaction did not commit.");
                }
            }
            SaveAsOptions options = new SaveAsOptions { OverwriteExistingFile = false };
            document.SaveAs(output, options);
            if (!File.Exists(output)) throw new IOException("Revit Save As did not create the hosted output.");
            FamilyPlacementHandoffReport report = new FamilyPlacementHandoffReport
            {
                SchemaVersion = 1, RequestId = requestId, PreviewId = preview.PreviewId,
                PreviewSha256 = preview.PreviewSha256, SourceSha256 = preview.SourceSha256,
                OutputPath = output, OutputSha256 = Hash(output), CreatedFamilyInstances = 1,
                CreatedElementIds = new List<long> { instance.Id.Value }, TransactionCommitted = true,
                SavedAsNewFile = true, ProviderEgressPerformed = false, DocumentMutationPerformed = true
            };
            return Write("hosted-family-handoffs", requestId, report, typeof(FamilyPlacementHandoffReport));
        }

        public static string Verify(Document document, string requestId, string handoffRequestId, string previewPath)
        {
            FamilyPlacementHandoffReport handoff = Read<FamilyPlacementHandoffReport>(Path.Combine(
                BridgeRoot(), "hosted-family-handoffs", handoffRequestId + ".json"));
            HostedPlacementPreview preview = Read<HostedPlacementPreview>(previewPath);
            Validate(preview, false);
            HostedPlacementPoint placement = preview.Placements[0];
            Reference ignoredReference;
            PlanarFace linkedFace;
            Transform linkTransform;
            ResolveHost(document, placement.HostFace, out ignoredReference, out linkedFace, out linkTransform);
            string current = Path.GetFullPath(document.PathName);
            if (!handoff.SavedAsNewFile || !string.Equals(current, handoff.OutputPath, StringComparison.OrdinalIgnoreCase)
                || !string.Equals(Hash(current), handoff.OutputSha256, StringComparison.OrdinalIgnoreCase)
                || handoff.CreatedElementIds == null || handoff.CreatedElementIds.Count != 1
                || !string.Equals(handoff.PreviewId, preview.PreviewId, StringComparison.Ordinal)
                || !string.Equals(handoff.PreviewSha256, preview.PreviewSha256, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException("Reopened hosted family handoff identity does not match its evidence.");
            }
            FamilyInstance instance = document.GetElement(new ElementId(handoff.CreatedElementIds[0])) as FamilyInstance;
            LocationPoint location = instance == null ? null : instance.Location as LocationPoint;
            Reference host = instance == null ? null : instance.HostFace;
            if (instance == null || location == null || host == null
                || instance.Symbol.Id.Value != preview.SymbolId
                || location.Point.DistanceTo(Point(placement)) > Tolerance()
                || !string.Equals(host.ConvertToStableRepresentation(document),
                    placement.HostFace.FaceStableReference, StringComparison.Ordinal))
            {
                throw new InvalidDataException("Reopened hosted family instance does not match the preview.");
            }
            VerifyPointOnFace(location.Point, linkedFace, linkTransform);
            FamilyPlacementVerificationReport report = new FamilyPlacementVerificationReport
            {
                SchemaVersion = 1, RequestId = requestId, HandoffRequestId = handoffRequestId,
                OutputSha256 = handoff.OutputSha256, VerifiedFamilyInstances = 1,
                VerifiedElementIds = handoff.CreatedElementIds, PlacementMatchesPreview = true,
                ProviderEgressPerformed = false, DocumentMutationPerformed = false
            };
            return Write("hosted-family-verifications", requestId, report, typeof(FamilyPlacementVerificationReport));
        }

        private static void Validate(HostedPlacementPreview preview, bool requireUnexpired)
        {
            DateTime expires;
            HostedPlacementPoint point = preview != null && preview.Placements != null
                && preview.Placements.Count == 1 ? preview.Placements[0] : null;
            HostedFaceBinding host = point == null ? null : point.HostFace;
            if (preview == null || preview.SchemaVersion != 1 || preview.SymbolId <= 0
                || preview.ReferenceElementId <= 0 || preview.Placements == null || preview.Placements.Count != 1
                || point == null || Math.Abs(point.RotationDegrees) > 1e-9 || host == null
                || host.LinkInstanceId <= 0 || host.LinkedElementId <= 0
                || !string.Equals(host.Kind, "linked_face", StringComparison.Ordinal)
                || !string.Equals(host.LinkedCategoryKey, "ceilings", StringComparison.Ordinal)
                || string.IsNullOrWhiteSpace(host.LinkedDocumentTitle)
                || string.IsNullOrWhiteSpace(host.FaceStableReference) || host.FaceStableReference.Length > 4096
                || preview.ExecutionAllowed || preview.ProviderEgressPerformed || preview.DocumentMutationPerformed
                || !string.Equals(preview.Action, "place_family_instances", StringComparison.Ordinal)
                || !string.Equals(preview.Status, "review_required", StringComparison.Ordinal)
                || !string.Equals(preview.PlacementType, "WorkPlaneBased", StringComparison.Ordinal)
                || !string.Equals(preview.HostRequirement, "work_plane_or_face", StringComparison.Ordinal)
                || string.IsNullOrWhiteSpace(preview.PreviewId) || string.IsNullOrWhiteSpace(preview.PreviewSha256)
                || !DateTime.TryParse(preview.ExpiresUtc, null,
                    System.Globalization.DateTimeStyles.RoundtripKind, out expires)
                || (requireUnexpired && expires.ToUniversalTime() <= DateTime.UtcNow))
            {
                throw new InvalidDataException("Approved hosted Revit preview is invalid or expired.");
            }
            Point(point);
        }

        private static void ResolveHost(Document document, HostedFaceBinding binding,
            out Reference hostReference, out PlanarFace face, out Transform transform)
        {
            hostReference = Reference.ParseFromStableRepresentation(document, binding.FaceStableReference);
            RevitLinkInstance link = document.GetElement(hostReference.ElementId) as RevitLinkInstance;
            Document linked = link == null ? null : link.GetLinkDocument();
            Reference inLink = link == null ? null : hostReference.CreateReferenceInLink();
            Element element = linked == null || inLink == null ? null : linked.GetElement(inLink.ElementId);
            if (element == null && linked != null && inLink != null
                && inLink.LinkedElementId != ElementId.InvalidElementId)
            {
                element = linked.GetElement(inLink.LinkedElementId);
            }
            face = element == null || inLink == null ? null : element.GetGeometryObjectFromReference(inLink) as PlanarFace;
            if (link == null || linked == null || inLink == null || element == null || face == null
                || link.Id.Value != binding.LinkInstanceId || element.Id.Value != binding.LinkedElementId
                || !string.Equals(linked.Title, binding.LinkedDocumentTitle, StringComparison.Ordinal)
                || element.Category == null
                || element.Category.Id.Value != (long)BuiltInCategory.OST_Ceilings)
            {
                throw new InvalidDataException("The exact linked ceiling host is missing or changed.");
            }
            transform = link.GetTotalTransform();
        }

        private static void VerifyPointOnFace(XYZ hostPoint, PlanarFace face, Transform transform)
        {
            XYZ linkedPoint = transform.Inverse.OfPoint(hostPoint);
            IntersectionResult projection = face.Project(linkedPoint);
            if (projection == null || !face.IsInside(projection.UVPoint)
                || transform.OfPoint(projection.XYZPoint).DistanceTo(hostPoint) > Tolerance())
            {
                throw new InvalidDataException("Approved hosted point is not on the exact linked ceiling face.");
            }
        }

        private static void VerifyClearance(Document document, string stable, XYZ point)
        {
            double minimum = UnitUtils.ConvertToInternalUnits(MinimumCenterClearanceMm, UnitTypeId.Millimeters);
            foreach (Element element in new FilteredElementCollector(document)
                .OfCategory(BuiltInCategory.OST_LightingFixtures).WhereElementIsNotElementType())
            {
                FamilyInstance fixture = element as FamilyInstance;
                LocationPoint location = fixture == null ? null : fixture.Location as LocationPoint;
                Reference host = fixture == null ? null : fixture.HostFace;
                if (location != null && host != null
                    && string.Equals(host.ConvertToStableRepresentation(document), stable, StringComparison.Ordinal)
                    && location.Point.DistanceTo(point) < minimum)
                {
                    throw new InvalidDataException("Hosted placement is below the 250 mm centre-clearance gate.");
                }
            }
        }

        private static XYZ Point(HostedPlacementPoint value)
        {
            if (value == null || Double.IsNaN(value.X) || Double.IsInfinity(value.X)
                || Double.IsNaN(value.Y) || Double.IsInfinity(value.Y)
                || Double.IsNaN(value.Z) || Double.IsInfinity(value.Z)
                || Math.Abs(value.X) > 1000000.0 || Math.Abs(value.Y) > 1000000.0
                || Math.Abs(value.Z) > 1000000.0)
            {
                throw new InvalidDataException("Hosted family placement point is invalid.");
            }
            return new XYZ(UnitUtils.ConvertToInternalUnits(value.X, UnitTypeId.Millimeters),
                UnitUtils.ConvertToInternalUnits(value.Y, UnitTypeId.Millimeters),
                UnitUtils.ConvertToInternalUnits(value.Z, UnitTypeId.Millimeters));
        }

        private static double Tolerance() { return UnitUtils.ConvertToInternalUnits(0.01, UnitTypeId.Millimeters); }
        private static T Read<T>(string path)
        {
            if (!File.Exists(path) || new FileInfo(path).Length > 65536)
                throw new InvalidDataException("Hosted Revit evidence is missing or oversized.");
            DataContractJsonSerializer serializer = new DataContractJsonSerializer(typeof(T));
            using (FileStream stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
                return (T)serializer.ReadObject(stream);
        }
        private static string Write(string folder, string requestId, object value, Type type)
        {
            string directory = Path.Combine(BridgeRoot(), folder); Directory.CreateDirectory(directory);
            string path = Path.Combine(directory, requestId + ".json");
            DataContractJsonSerializer serializer = new DataContractJsonSerializer(type);
            using (FileStream stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            { serializer.WriteObject(stream, value); stream.Flush(true); }
            return folder + "/" + requestId + ".json";
        }
        private static string BridgeRoot() { return Path.Combine(Environment.GetFolderPath(
            Environment.SpecialFolder.LocalApplicationData), "AI-Engineer", "revit"); }
        private static string Hash(string path)
        {
            using (SHA256 algorithm = SHA256.Create())
            using (FileStream stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                return BitConverter.ToString(algorithm.ComputeHash(stream)).Replace("-", string.Empty);
        }
    }
}
