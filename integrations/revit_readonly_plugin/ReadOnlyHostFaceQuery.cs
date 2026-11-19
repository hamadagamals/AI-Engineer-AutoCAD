using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;

using Autodesk.Revit.DB;

namespace AIEngineer.Revit
{
    [DataContract]
    internal sealed class HostFaceCandidate
    {
        [DataMember(Name = "x_mm", Order = 1)] public double X;
        [DataMember(Name = "y_mm", Order = 2)] public double Y;
        [DataMember(Name = "z_mm", Order = 3)] public double Z;
        [DataMember(Name = "normal_x", Order = 4)] public double NormalX;
        [DataMember(Name = "normal_y", Order = 5)] public double NormalY;
        [DataMember(Name = "normal_z", Order = 6)] public double NormalZ;
        [DataMember(Name = "nearest_fixture_clearance_mm", Order = 7)] public double Clearance;
    }

    [DataContract]
    internal sealed class HostFaceQueryReport
    {
        [DataMember(Name = "schema_version", Order = 1)] public int SchemaVersion;
        [DataMember(Name = "request_id", Order = 2)] public string RequestId;
        [DataMember(Name = "document_title", Order = 3)] public string DocumentTitle;
        [DataMember(Name = "reference_element_id", Order = 4)] public long ReferenceElementId;
        [DataMember(Name = "link_instance_id", Order = 5)] public long LinkInstanceId;
        [DataMember(Name = "linked_element_id", Order = 6)] public long LinkedElementId;
        [DataMember(Name = "linked_document_title", Order = 7)] public string LinkedDocumentTitle;
        [DataMember(Name = "linked_category_key", Order = 8)] public string LinkedCategoryKey;
        [DataMember(Name = "face_stable_reference", Order = 9)] public string FaceStableReference;
        [DataMember(Name = "face_area_mm2", Order = 10)] public double FaceArea;
        [DataMember(Name = "mesh_vertices", Order = 11)] public int MeshVertices;
        [DataMember(Name = "mesh_triangles", Order = 12)] public int MeshTriangles;
        [DataMember(Name = "candidate_count", Order = 13)] public int CandidateCount;
        [DataMember(Name = "candidates", Order = 14)] public List<HostFaceCandidate> Candidates;
        [DataMember(Name = "provider_egress_performed", Order = 15)] public bool ProviderEgressPerformed;
        [DataMember(Name = "document_mutation_performed", Order = 16)] public bool DocumentMutationPerformed;
    }

    internal static class ReadOnlyHostFaceQuery
    {
        private const int MaximumMeshVertices = 512;
        private const int MaximumMeshTriangles = 512;

        public static string Capture(
            Document document,
            string requestId,
            long referenceElementId,
            string expectedStableReference,
            int maximumCandidates)
        {
            FamilyInstance fixture = document.GetElement(new ElementId(referenceElementId)) as FamilyInstance;
            Reference hostReference = fixture == null ? null : fixture.HostFace;
            if (fixture == null || hostReference == null)
            {
                throw new InvalidDataException("The exact Revit reference fixture lacks a host face.");
            }
            string stable = hostReference.ConvertToStableRepresentation(document);
            if (!string.Equals(stable, expectedStableReference, StringComparison.Ordinal))
            {
                throw new InvalidDataException("The Revit host face no longer matches the requested reference.");
            }
            RevitLinkInstance link = document.GetElement(hostReference.ElementId) as RevitLinkInstance;
            Document linkedDocument = link == null ? null : link.GetLinkDocument();
            Reference referenceInLink = link == null ? null : hostReference.CreateReferenceInLink();
            if (link == null || linkedDocument == null || referenceInLink == null)
            {
                throw new InvalidDataException("The referenced Revit link is not loaded.");
            }
            Element linkedElement = linkedDocument.GetElement(referenceInLink.ElementId);
            if (linkedElement == null && referenceInLink.LinkedElementId != ElementId.InvalidElementId)
            {
                linkedElement = linkedDocument.GetElement(referenceInLink.LinkedElementId);
            }
            if (linkedElement == null || linkedElement.Category == null
                || linkedElement.Category.Id.Value != (long)BuiltInCategory.OST_Ceilings)
            {
                throw new InvalidDataException("The referenced linked host is not an exact ceiling element.");
            }
            Face face = linkedElement.GetGeometryObjectFromReference(referenceInLink) as Face;
            PlanarFace planar = face as PlanarFace;
            if (face == null || planar == null)
            {
                throw new InvalidDataException("The referenced ceiling face is unavailable or non-planar.");
            }
            Mesh mesh = face.Triangulate();
            if (mesh == null || mesh.Vertices.Count < 3 || mesh.NumTriangles < 1
                || mesh.Vertices.Count > MaximumMeshVertices || mesh.NumTriangles > MaximumMeshTriangles)
            {
                throw new InvalidDataException("The ceiling face mesh is empty or exceeds the bounded geometry contract.");
            }
            Transform transform = link.GetTotalTransform();
            List<XYZ> occupied = ExistingFixturePoints(document, stable);
            List<HostFaceCandidate> candidates = new List<HostFaceCandidate>();
            for (int index = 0; index < mesh.NumTriangles; index++)
            {
                MeshTriangle triangle = mesh.get_Triangle(index);
                XYZ linkedPoint = (triangle.get_Vertex(0) + triangle.get_Vertex(1) + triangle.get_Vertex(2)) / 3.0;
                IntersectionResult projection = face.Project(linkedPoint);
                if (projection == null || !face.IsInside(projection.UVPoint)) continue;
                XYZ point = transform.OfPoint(projection.XYZPoint);
                XYZ normal = transform.OfVector(face.ComputeNormal(projection.UVPoint)).Normalize();
                double clearance = NearestClearance(point, occupied);
                candidates.Add(new HostFaceCandidate
                {
                    X = Millimeters(point.X), Y = Millimeters(point.Y), Z = Millimeters(point.Z),
                    NormalX = Math.Round(normal.X, 6), NormalY = Math.Round(normal.Y, 6),
                    NormalZ = Math.Round(normal.Z, 6), Clearance = Millimeters(clearance)
                });
            }
            candidates.Sort((left, right) => right.Clearance.CompareTo(left.Clearance));
            List<HostFaceCandidate> bounded = new List<HostFaceCandidate>();
            foreach (HostFaceCandidate candidate in candidates)
            {
                if (bounded.Count >= maximumCandidates) break;
                bool duplicate = bounded.Exists(item => DistanceMillimeters(item, candidate) < 10.0);
                if (!duplicate) bounded.Add(candidate);
            }
            if (bounded.Count == 0)
            {
                throw new InvalidDataException("The ceiling face produced no bounded interior candidate.");
            }
            HostFaceQueryReport report = new HostFaceQueryReport
            {
                SchemaVersion = 1, RequestId = requestId, DocumentTitle = Bounded(document.Title),
                ReferenceElementId = fixture.Id.Value, LinkInstanceId = link.Id.Value,
                LinkedElementId = linkedElement.Id.Value, LinkedDocumentTitle = Bounded(linkedDocument.Title),
                LinkedCategoryKey = "ceilings", FaceStableReference = stable,
                FaceArea = Math.Round(UnitUtils.ConvertFromInternalUnits(face.Area, UnitTypeId.SquareMillimeters), 3),
                MeshVertices = mesh.Vertices.Count, MeshTriangles = mesh.NumTriangles,
                CandidateCount = bounded.Count, Candidates = bounded,
                ProviderEgressPerformed = false, DocumentMutationPerformed = false
            };
            return Write(requestId, report);
        }

        private static List<XYZ> ExistingFixturePoints(Document document, string stableReference)
        {
            List<XYZ> points = new List<XYZ>();
            foreach (Element element in new FilteredElementCollector(document)
                .OfCategory(BuiltInCategory.OST_LightingFixtures).WhereElementIsNotElementType())
            {
                FamilyInstance instance = element as FamilyInstance;
                LocationPoint location = instance == null ? null : instance.Location as LocationPoint;
                Reference host = instance == null ? null : instance.HostFace;
                if (location != null && host != null
                    && string.Equals(host.ConvertToStableRepresentation(document), stableReference, StringComparison.Ordinal))
                {
                    points.Add(location.Point);
                }
            }
            return points;
        }

        private static double NearestClearance(XYZ candidate, List<XYZ> occupied)
        {
            double clearance = double.MaxValue;
            foreach (XYZ point in occupied) clearance = Math.Min(clearance, candidate.DistanceTo(point));
            return clearance == double.MaxValue ? 0.0 : clearance;
        }

        private static double DistanceMillimeters(HostFaceCandidate left, HostFaceCandidate right)
        {
            double x = left.X - right.X, y = left.Y - right.Y, z = left.Z - right.Z;
            return Math.Sqrt(x * x + y * y + z * z);
        }

        private static double Millimeters(double value)
        {
            return Math.Round(UnitUtils.ConvertFromInternalUnits(value, UnitTypeId.Millimeters), 3);
        }

        private static string Bounded(string value)
        {
            if (string.IsNullOrEmpty(value)) return string.Empty;
            return value.Length <= 260 ? value : value.Substring(0, 260);
        }

        private static string Write(string requestId, HostFaceQueryReport report)
        {
            string root = Path.Combine(Environment.GetFolderPath(
                Environment.SpecialFolder.LocalApplicationData), "AI-Engineer", "revit", "face-queries");
            Directory.CreateDirectory(root);
            string fileName = requestId + ".json";
            string destination = Path.Combine(root, fileName);
            DataContractJsonSerializer serializer = new DataContractJsonSerializer(typeof(HostFaceQueryReport));
            using (FileStream stream = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                serializer.WriteObject(stream, report);
                stream.Flush(true);
            }
            return "face-queries/" + fileName;
        }
    }
}
