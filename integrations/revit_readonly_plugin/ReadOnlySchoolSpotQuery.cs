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
    internal sealed class SchoolSpotInput
    {
        [DataMember(Name = "spot_id", Order = 1)] public string SpotId;
        [DataMember(Name = "x_mm", Order = 2)] public double Xmm;
        [DataMember(Name = "y_mm", Order = 3)] public double Ymm;
        [DataMember(Name = "level_id", Order = 4)] public long LevelId;
    }

    [DataContract]
    internal sealed class SchoolSpotResult
    {
        [DataMember(Name = "spot_id", Order = 1)] public string SpotId;
        [DataMember(Name = "x_mm", Order = 2)] public double Xmm;
        [DataMember(Name = "y_mm", Order = 3)] public double Ymm;
        [DataMember(Name = "room_ids", Order = 4)] public List<long> RoomIds;
        [DataMember(Name = "room_names", Order = 5)] public List<string> RoomNames;
        [DataMember(Name = "room_bbox_candidate_ids", Order = 6)] public List<long> RoomBoundingBoxCandidateIds;
        [DataMember(Name = "ceiling_bbox_ids", Order = 7)] public List<long> CeilingBoundingBoxIds;
        [DataMember(Name = "stair_bbox_ids", Order = 8)] public List<long> StairBoundingBoxIds;
        [DataMember(Name = "shaft_bbox_ids", Order = 9)] public List<long> ShaftBoundingBoxIds;
        [DataMember(Name = "door_bbox_ids_within_500mm", Order = 10)] public List<long> DoorBoundingBoxIds;
        [DataMember(Name = "nearest_room_light_stroke_mm", Order = 11)] public double? NearestRoomLightStrokeMm;
        [DataMember(Name = "nearest_room_light_type", Order = 12)] public string NearestRoomLightType;
        [DataMember(Name = "nearest_room_light_curve_id", Order = 13)] public long? NearestRoomLightCurveId;
        [DataMember(Name = "nearby_room_light_stroke_count_3m", Order = 14)] public int NearbyRoomLightStrokeCount;
        [DataMember(Name = "nearby_room_light_stroke_boundary_crossings_3m", Order = 15)]
        public int NearbyRoomLightStrokeBoundaryCrossings;
    }

    [DataContract]
    internal sealed class SchoolSpotReport
    {
        [DataMember(Name = "schema_version", Order = 1)] public int SchemaVersion;
        [DataMember(Name = "request_id", Order = 2)] public string RequestId;
        [DataMember(Name = "document_title", Order = 3)] public string DocumentTitle;
        [DataMember(Name = "document_sha256", Order = 4)] public string DocumentSha256;
        [DataMember(Name = "spots", Order = 5)] public List<SchoolSpotResult> Spots;
        [DataMember(Name = "provider_egress_performed", Order = 6)] public bool ProviderEgressPerformed;
        [DataMember(Name = "document_mutation_performed", Order = 7)] public bool DocumentMutationPerformed;
    }

    internal static class ReadOnlySchoolSpotQuery
    {
        public static string Capture(Document document, string requestId,
            string expectedSha256, List<SchoolSpotInput> spots)
        {
            if (document.IsFamilyDocument || string.IsNullOrWhiteSpace(document.PathName)
                || spots == null || spots.Count < 1 || spots.Count > 32)
                throw new InvalidDataException("School spot query document or point count is invalid.");
            string actualSha256 = Hash(document.PathName);
            if (!string.Equals(actualSha256, expectedSha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("School spot query document hash changed.");
            HashSet<string> spotIds = new HashSet<string>(StringComparer.Ordinal);
            foreach (SchoolSpotInput spot in spots)
            {
                if (spot == null || string.IsNullOrWhiteSpace(spot.SpotId) || spot.SpotId.Length > 32
                    || !spotIds.Add(spot.SpotId) || spot.LevelId != 30
                    || double.IsNaN(spot.Xmm) || double.IsNaN(spot.Ymm)
                    || double.IsInfinity(spot.Xmm) || double.IsInfinity(spot.Ymm)
                    || Math.Abs(spot.Xmm) > 200000 || Math.Abs(spot.Ymm) > 200000
                    || !(document.GetElement(new ElementId(spot.LevelId)) is Level))
                    throw new InvalidDataException(
                        "School spot query supports only the reviewed Level 1 plan.");
            }
            List<Room> rooms = new FilteredElementCollector(document)
                .OfCategory(BuiltInCategory.OST_Rooms).WhereElementIsNotElementType()
                .OfType<Room>().Where(room => room.Area > 0).ToList();
            List<Element> ceilings = Collect(document, BuiltInCategory.OST_Ceilings);
            List<Element> stairs = Collect(document, BuiltInCategory.OST_Stairs)
                .Concat(Collect(document, BuiltInCategory.OST_StairsRuns))
                .Concat(Collect(document, BuiltInCategory.OST_StairsLandings)).ToList();
            List<Element> shafts = Collect(document, BuiltInCategory.OST_ShaftOpening);
            List<Element> doors = Collect(document, BuiltInCategory.OST_Doors);
            Dictionary<long, List<DetailCurve>> lightCurvesByLevel =
                new Dictionary<long, List<DetailCurve>>();
            foreach (long levelId in spots.Select(spot => spot.LevelId).Distinct())
            {
                string[] reviewNames = {
                    "AI REVIEW V5 - ELECTRICAL - LIGHTING - LEVEL 1",
                    "AI REVIEW V4 - ELECTRICAL - LIGHTING - LEVEL 1"
                };
                List<ViewPlan> reviewViews = new FilteredElementCollector(document)
                    .OfClass(typeof(ViewPlan)).Cast<ViewPlan>().ToList();
                ViewPlan view = reviewNames.Select(name => reviewViews.FirstOrDefault(item => item.Name == name))
                    .FirstOrDefault(item => item != null);
                if (view == null)
                    throw new InvalidDataException("School spot query lighting review view is unavailable.");
                lightCurvesByLevel[levelId] = new FilteredElementCollector(document, view.Id)
                    .OfClass(typeof(CurveElement)).Cast<CurveElement>().OfType<DetailCurve>()
                    .Where(item => item.LineStyle != null
                        && item.LineStyle.Name.StartsWith("AI-V3-LIGHT-", StringComparison.Ordinal))
                    .ToList();
            }
            List<SchoolSpotResult> results = new List<SchoolSpotResult>();
            foreach (SchoolSpotInput spot in spots)
            {
                Level level = document.GetElement(new ElementId(spot.LevelId)) as Level;
                double x = UnitUtils.ConvertToInternalUnits(spot.Xmm, UnitTypeId.Millimeters);
                double y = UnitUtils.ConvertToInternalUnits(spot.Ymm, UnitTypeId.Millimeters);
                XYZ roomTest = new XYZ(x, y, level.Elevation
                    + UnitUtils.ConvertToInternalUnits(300.0, UnitTypeId.Millimeters));
                List<Room> matched = rooms.Where(room => room.LevelId.Value == spot.LevelId
                    && room.IsPointInRoom(roomTest)).ToList();
                DetailCurve nearestCurve = null;
                double nearestDistance = Double.PositiveInfinity;
                int nearbyStrokeCount = 0;
                int nearbyBoundaryCrossings = 0;
                if (matched.Count > 0)
                    foreach (DetailCurve curve in lightCurvesByLevel[spot.LevelId])
                    {
                        Line line = curve.GeometryCurve as Line;
                        if (line == null) continue;
                        XYZ midpoint = (line.GetEndPoint(0) + line.GetEndPoint(1)) / 2.0;
                        XYZ midpointTest = new XYZ(midpoint.X, midpoint.Y, roomTest.Z);
                        if (!matched.Any(room => room.IsPointInRoom(midpointTest))) continue;
                        double distance = Math.Sqrt(Math.Pow(midpoint.X - x, 2)
                            + Math.Pow(midpoint.Y - y, 2));
                        if (distance <= UnitUtils.ConvertToInternalUnits(
                            3000.0, UnitTypeId.Millimeters))
                        {
                            nearbyStrokeCount++;
                            if (new[] { 0.0, 0.25, 0.5, 0.75, 1.0 }.Any(fraction =>
                            {
                                XYZ sample = line.Evaluate(fraction, true);
                                XYZ test = new XYZ(sample.X, sample.Y, roomTest.Z);
                                return !matched.Any(room => room.IsPointInRoom(test));
                            })) nearbyBoundaryCrossings++;
                        }
                        if (distance < nearestDistance)
                        { nearestDistance = distance; nearestCurve = curve; }
                    }
                results.Add(new SchoolSpotResult
                {
                    SpotId = spot.SpotId,
                    Xmm = spot.Xmm,
                    Ymm = spot.Ymm,
                    RoomIds = matched.Select(room => room.Id.Value).ToList(),
                    RoomNames = matched.Select(room => room.Name).ToList(),
                    RoomBoundingBoxCandidateIds = rooms.Where(room => room.LevelId.Value == spot.LevelId
                        && ContainsXY(room.get_BoundingBox(null), x, y, 0.0))
                        .Select(room => room.Id.Value).ToList(),
                    CeilingBoundingBoxIds = ContainingIds(
                        ceilings.Where(item => item.LevelId.Value == spot.LevelId), x, y, 0.0),
                    StairBoundingBoxIds = ContainingIds(stairs, x, y, 0.0),
                    ShaftBoundingBoxIds = ContainingIds(shafts, x, y, 0.0),
                    DoorBoundingBoxIds = ContainingIds(
                        doors.Where(item => item.LevelId.Value == spot.LevelId), x, y, 500.0),
                    NearestRoomLightStrokeMm = nearestCurve == null ? (double?)null
                        : Math.Round(UnitUtils.ConvertFromInternalUnits(
                            nearestDistance, UnitTypeId.Millimeters), 1),
                    NearestRoomLightType = nearestCurve == null ? null
                        : nearestCurve.LineStyle.Name.Substring("AI-V3-LIGHT-".Length),
                    NearestRoomLightCurveId = nearestCurve == null ? (long?)null
                        : nearestCurve.Id.Value,
                    NearbyRoomLightStrokeCount = nearbyStrokeCount,
                    NearbyRoomLightStrokeBoundaryCrossings = nearbyBoundaryCrossings
                });
            }
            SchoolSpotReport report = new SchoolSpotReport
            {
                SchemaVersion = 1,
                RequestId = requestId,
                DocumentTitle = document.Title,
                DocumentSha256 = actualSha256,
                Spots = results,
                ProviderEgressPerformed = false,
                DocumentMutationPerformed = false
            };
            string relative = "school-spot-queries/" + requestId + ".json";
            string path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "AI-Engineer", "revit", "school-spot-queries", requestId + ".json");
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            DataContractJsonSerializer serializer = new DataContractJsonSerializer(typeof(SchoolSpotReport));
            using (FileStream stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            { serializer.WriteObject(stream, report); stream.Flush(true); }
            return relative;
        }

        private static List<Element> Collect(Document document, BuiltInCategory category)
        {
            return new FilteredElementCollector(document).OfCategory(category)
                .WhereElementIsNotElementType().ToElements().ToList();
        }

        private static List<long> ContainingIds(IEnumerable<Element> elements, double x, double y, double marginMm)
        {
            return elements.Where(element => ContainsXY(element.get_BoundingBox(null), x, y, marginMm))
                .Select(element => element.Id.Value).ToList();
        }

        private static bool ContainsXY(BoundingBoxXYZ box, double x, double y, double marginMm)
        {
            if (box == null) return false;
            double margin = UnitUtils.ConvertToInternalUnits(marginMm, UnitTypeId.Millimeters);
            return x >= box.Min.X - margin && x <= box.Max.X + margin
                && y >= box.Min.Y - margin && y <= box.Max.Y + margin;
        }

        private static string Hash(string path)
        {
            using (SHA256 sha = SHA256.Create())
            using (FileStream stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                return BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", string.Empty);
        }
    }
}
