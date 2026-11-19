using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Security.Cryptography;

using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;

namespace AIEngineer.Revit
{
    [DataContract]
    internal sealed class SchoolLightingRoomAudit
    {
        [DataMember(Name = "room_id", Order = 1)] public long RoomId;
        [DataMember(Name = "room_name", Order = 2)] public string RoomName;
        [DataMember(Name = "symbol_stroke_count", Order = 3)] public int StrokeCount;
        [DataMember(Name = "door_bbox_hit_strokes", Order = 4)] public int DoorHits;
        [DataMember(Name = "stair_bbox_hit_strokes", Order = 5)] public int StairHits;
        [DataMember(Name = "no_ceiling_bbox_strokes", Order = 6)] public int NoCeiling;
        [DataMember(Name = "boundary_crossing_strokes", Order = 7)] public int BoundaryCrossings;
        [DataMember(Name = "door_bbox_hit_example_curve_ids", Order = 8)] public List<long> DoorExamples;
        [DataMember(Name = "no_ceiling_bbox_example_curve_ids", Order = 9)] public List<long> NoCeilingExamples;
    }

    [DataContract]
    internal sealed class SchoolLightingDoorAuditReport
    {
        [DataMember(Name = "schema_version", Order = 1)] public int SchemaVersion;
        [DataMember(Name = "request_id", Order = 2)] public string RequestId;
        [DataMember(Name = "document_sha256", Order = 3)] public string DocumentSha256;
        [DataMember(Name = "view_id", Order = 4)] public long ViewId;
        [DataMember(Name = "view_name", Order = 5)] public string ViewName;
        [DataMember(Name = "symbol_stroke_count", Order = 6)] public int StrokeCount;
        [DataMember(Name = "unassigned_stroke_count", Order = 7)] public int UnassignedStrokeCount;
        [DataMember(Name = "rooms", Order = 8)] public List<SchoolLightingRoomAudit> Rooms;
        [DataMember(Name = "provider_egress_performed", Order = 9)] public bool ProviderEgressPerformed;
        [DataMember(Name = "document_mutation_performed", Order = 10)] public bool DocumentMutationPerformed;
    }

    internal static class ReadOnlySchoolLightingDoorAudit
    {
        private const string ViewName = "AI REVIEW V4 - ELECTRICAL - LIGHTING - LEVEL 1";

        public static string Capture(Document document, string requestId, string expectedSha256)
        {
            if (document.IsFamilyDocument || string.IsNullOrWhiteSpace(document.PathName))
                throw new InvalidDataException("School lighting audit needs an open V4 project.");
            string actualSha = Hash(document.PathName);
            if (!string.Equals(actualSha, expectedSha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("School lighting audit document hash changed.");
            ViewPlan view = new FilteredElementCollector(document).OfClass(typeof(ViewPlan))
                .Cast<ViewPlan>().FirstOrDefault(item => item.Name == ViewName);
            if (view == null || view.GenLevel == null || view.GenLevel.Id.Value != 30)
                throw new InvalidDataException("The V4 Level 1 lighting view is unavailable.");
            List<Room> rooms = new FilteredElementCollector(document)
                .OfCategory(BuiltInCategory.OST_Rooms).WhereElementIsNotElementType()
                .OfType<Room>().Where(item => item.Area > 0 && item.LevelId.Value == 30)
                .ToList();
            List<Element> doors = Collect(document, BuiltInCategory.OST_Doors)
                .Where(item => item.LevelId.Value == 30).ToList();
            List<Element> ceilings = Collect(document, BuiltInCategory.OST_Ceilings)
                .Where(item => item.LevelId.Value == 30).ToList();
            List<Element> stairs = Collect(document, BuiltInCategory.OST_Stairs)
                .Concat(Collect(document, BuiltInCategory.OST_StairsRuns))
                .Concat(Collect(document, BuiltInCategory.OST_StairsLandings)).ToList();
            List<DetailCurve> curves = new FilteredElementCollector(document, view.Id)
                .OfClass(typeof(CurveElement)).Cast<CurveElement>().OfType<DetailCurve>()
                .Where(item => item.LineStyle != null
                    && item.LineStyle.Name.StartsWith("AI-V3-LIGHT-", StringComparison.Ordinal))
                .ToList();
            if (curves.Count < 1 || curves.Count > 10000 || rooms.Count < 1 || rooms.Count > 128)
                throw new InvalidDataException("School lighting audit size is outside bounds.");
            Dictionary<long, SchoolLightingRoomAudit> byRoom = rooms.ToDictionary(
                room => room.Id.Value,
                room => new SchoolLightingRoomAudit
                {
                    RoomId = room.Id.Value, RoomName = room.Name,
                    DoorExamples = new List<long>(), NoCeilingExamples = new List<long>()
                });
            int unassigned = 0;
            double testZ = view.GenLevel.Elevation
                + UnitUtils.ConvertToInternalUnits(300.0, UnitTypeId.Millimeters);
            foreach (DetailCurve curve in curves)
            {
                Line line = curve.GeometryCurve as Line;
                if (line == null) throw new InvalidDataException("Lighting audit found a curved review symbol.");
                XYZ middle = line.Evaluate(0.5, true);
                XYZ test = new XYZ(middle.X, middle.Y, testZ);
                Room room = rooms.FirstOrDefault(item => WithinXY(item.get_BoundingBox(null), middle)
                    && item.IsPointInRoom(test));
                if (room == null) { unassigned++; continue; }
                SchoolLightingRoomAudit row = byRoom[room.Id.Value];
                row.StrokeCount++;
                XYZ[] samples = new[] { 0.0, 0.25, 0.5, 0.75, 1.0 }
                    .Select(fraction => line.Evaluate(fraction, true)).ToArray();
                if (samples.Any(sample => !room.IsPointInRoom(new XYZ(sample.X, sample.Y, testZ))))
                    row.BoundaryCrossings++;
                if (doors.Any(door => samples.Any(sample => WithinXY(door.get_BoundingBox(null), sample))))
                {
                    row.DoorHits++;
                    if (row.DoorExamples.Count < 8) row.DoorExamples.Add(curve.Id.Value);
                }
                if (stairs.Any(stair => samples.Any(sample => WithinXY(stair.get_BoundingBox(null), sample))))
                    row.StairHits++;
                if (!ceilings.Any(ceiling => WithinXY(ceiling.get_BoundingBox(null), middle)))
                {
                    row.NoCeiling++;
                    if (row.NoCeilingExamples.Count < 8) row.NoCeilingExamples.Add(curve.Id.Value);
                }
            }
            SchoolLightingDoorAuditReport report = new SchoolLightingDoorAuditReport
            {
                SchemaVersion = 1,
                RequestId = requestId,
                DocumentSha256 = actualSha,
                ViewId = view.Id.Value,
                ViewName = ViewName,
                StrokeCount = curves.Count,
                UnassignedStrokeCount = unassigned,
                Rooms = byRoom.Values.OrderBy(item => item.RoomName).ToList(),
                ProviderEgressPerformed = false,
                DocumentMutationPerformed = false
            };
            string relative = "school-lighting-door-audits/" + requestId + ".json";
            string path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "AI-Engineer", "revit", "school-lighting-door-audits", requestId + ".json");
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            DataContractJsonSerializer serializer = new DataContractJsonSerializer(typeof(SchoolLightingDoorAuditReport));
            using (FileStream stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            { serializer.WriteObject(stream, report); stream.Flush(true); }
            return relative;
        }

        private static List<Element> Collect(Document document, BuiltInCategory category)
        {
            return new FilteredElementCollector(document).OfCategory(category)
                .WhereElementIsNotElementType().ToElements().ToList();
        }

        private static bool WithinXY(BoundingBoxXYZ box, XYZ point)
        {
            return box != null && point.X >= box.Min.X && point.X <= box.Max.X
                && point.Y >= box.Min.Y && point.Y <= box.Max.Y;
        }

        private static string Hash(string path)
        {
            using (SHA256 sha = SHA256.Create())
            using (FileStream stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                return BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", string.Empty);
        }
    }
}
