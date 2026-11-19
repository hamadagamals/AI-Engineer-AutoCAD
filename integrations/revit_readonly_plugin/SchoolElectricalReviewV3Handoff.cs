using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Security.Cryptography;

using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;
using Autodesk.Revit.UI;

namespace AIEngineer.Revit
{
    [DataContract]
    internal sealed class SchoolElectricalReviewV3Room
    {
        [DataMember(Name = "room_id", Order = 1)] public long RoomId;
        [DataMember(Name = "room_name", Order = 2)] public string RoomName;
        [DataMember(Name = "use", Order = 3)] public string Use;
        [DataMember(Name = "level_key", Order = 4)] public string LevelKey;
        [DataMember(Name = "level_id", Order = 5)] public long LevelId;
        [DataMember(Name = "luminaire_type", Order = 6)] public string LuminaireType;
        [DataMember(Name = "lighting_count", Order = 7)] public int LightingCount;
        [DataMember(Name = "target_lux", Order = 8)] public double TargetLux;
        [DataMember(Name = "mean_lux", Order = 9)] public double MeanLux;
        [DataMember(Name = "lighting_w", Order = 10)] public double LightingW;
        [DataMember(Name = "new_duplex_count", Order = 11)] public int NewDuplexCount;
        [DataMember(Name = "new_gfi_count", Order = 12)] public int NewGfiCount;
        [DataMember(Name = "dedicated_count", Order = 13)] public int DedicatedCount;
        [DataMember(Name = "lighting_panel_zone", Order = 14)] public string LightingPanelZone;
        [DataMember(Name = "power_panel_zone", Order = 15)] public string PowerPanelZone;
    }

    [DataContract]
    internal sealed class SchoolElectricalReviewV3Preview
    {
        [DataMember(Name = "schema_version", Order = 1)] public int SchemaVersion;
        [DataMember(Name = "preview_id", Order = 2)] public string PreviewId;
        [DataMember(Name = "action", Order = 3)] public string Action;
        [DataMember(Name = "status", Order = 4)] public string Status;
        [DataMember(Name = "expires_utc", Order = 5)] public string ExpiresUtc;
        [DataMember(Name = "source_sha256", Order = 6)] public string SourceSha256;
        [DataMember(Name = "source_schedule_sha256", Order = 7)] public string SourceScheduleSha256;
        [DataMember(Name = "algorithm_version", Order = 8)] public string AlgorithmVersion;
        [DataMember(Name = "lighting_boundary_clearance_mm", Order = 9)] public double LightingBoundaryClearanceMm;
        [DataMember(Name = "lighting_door_clearance_mm", Order = 10)] public double LightingDoorClearanceMm;
        [DataMember(Name = "power_door_clearance_mm", Order = 11)] public double PowerDoorClearanceMm;
        [DataMember(Name = "stair_shaft_clearance_mm", Order = 12)] public double StairShaftClearanceMm;
        [DataMember(Name = "rooms", Order = 13)] public List<SchoolElectricalReviewV3Room> Rooms;
        [DataMember(Name = "preview_sha256", Order = 14)] public string PreviewSha256;
        [DataMember(Name = "provider_egress_performed", Order = 15)] public bool ProviderEgressPerformed;
        [DataMember(Name = "source_document_mutation_performed", Order = 16)] public bool SourceDocumentMutationPerformed;
        [DataMember(Name = "stair_wall_review_candidate", Order = 17)] public SchoolStairWallCandidate StairWallCandidate;
        [DataMember(Name = "v5_expected_points", Order = 18)] public List<SchoolV5ExpectedPoint> V5ExpectedPoints;
    }

    [DataContract]
    internal sealed class SchoolV5ExpectedPoint
    {
        [DataMember(Name = "room_id", Order = 1)] public long RoomId;
        [DataMember(Name = "index", Order = 2)] public int Index;
        [DataMember(Name = "x_mm", Order = 3)] public double Xmm;
        [DataMember(Name = "y_mm", Order = 4)] public double Ymm;
    }

    [DataContract]
    internal sealed class SchoolStairWallCandidate
    {
        [DataMember(Name = "wall_id", Order = 1)] public long WallId;
        [DataMember(Name = "marked_x_mm", Order = 2)] public double MarkedXmm;
        [DataMember(Name = "marked_y_mm", Order = 3)] public double MarkedYmm;
        [DataMember(Name = "mounting_height_mm", Order = 4)] public double? MountingHeightMm;
        [DataMember(Name = "luminaire_count", Order = 5)] public int? LuminaireCount;
        [DataMember(Name = "status", Order = 6)] public string Status;
    }

    [DataContract]
    internal sealed class SchoolElectricalReviewV3RoomResult
    {
        [DataMember(Name = "room_id", Order = 1)] public long RoomId;
        [DataMember(Name = "room_name", Order = 2)] public string RoomName;
        [DataMember(Name = "level_key", Order = 3)] public string LevelKey;
        [DataMember(Name = "luminaire_type", Order = 4)] public string LuminaireType;
        [DataMember(Name = "lighting_required", Order = 5)] public int LightingRequired;
        [DataMember(Name = "lighting_placed", Order = 6)] public int LightingPlaced;
        [DataMember(Name = "new_duplex_placed", Order = 7)] public int NewDuplexPlaced;
        [DataMember(Name = "new_gfi_placed", Order = 8)] public int NewGfiPlaced;
        [DataMember(Name = "dedicated_placed", Order = 9)] public int DedicatedPlaced;
        [DataMember(Name = "minimum_lighting_spacing_mm", Order = 10)] public double MinimumLightingSpacingMm;
        [DataMember(Name = "lighting_boundary_clearance_used_mm", Order = 11)] public double LightingBoundaryClearanceUsedMm;
        [DataMember(Name = "maximum_coverage_distance_mm", Order = 12)] public double MaximumCoverageDistanceMm;
        [DataMember(Name = "actual_room_area_m2", Order = 13)] public double ActualRoomAreaM2;
        [DataMember(Name = "bbox_area_m2", Order = 14)] public double BoundingBoxAreaM2;
        [DataMember(Name = "lighting_rows", Order = 15)] public int LightingRows;
        [DataMember(Name = "mounting_host_review_count", Order = 16)] public int MountingHostReviewCount;
    }

    [DataContract]
    internal sealed class SchoolElectricalReviewV3Report
    {
        [DataMember(Name = "schema_version", Order = 1)] public int SchemaVersion;
        [DataMember(Name = "request_id", Order = 2)] public string RequestId;
        [DataMember(Name = "preview_id", Order = 3)] public string PreviewId;
        [DataMember(Name = "preview_sha256", Order = 4)] public string PreviewSha256;
        [DataMember(Name = "source_sha256", Order = 5)] public string SourceSha256;
        [DataMember(Name = "output_path", Order = 6)] public string OutputPath;
        [DataMember(Name = "output_sha256", Order = 7)] public string OutputSha256;
        [DataMember(Name = "room_count", Order = 8)] public int RoomCount;
        [DataMember(Name = "lighting_point_count", Order = 9)] public int LightingPointCount;
        [DataMember(Name = "new_duplex_count", Order = 10)] public int NewDuplexCount;
        [DataMember(Name = "new_gfi_count", Order = 11)] public int NewGfiCount;
        [DataMember(Name = "dedicated_count", Order = 12)] public int DedicatedCount;
        [DataMember(Name = "existing_outlet_count", Order = 13)] public int ExistingOutletCount;
        [DataMember(Name = "panel_candidate_count", Order = 14)] public int PanelCandidateCount;
        [DataMember(Name = "detail_curve_count", Order = 15)] public int DetailCurveCount;
        [DataMember(Name = "exact_duplicate_count", Order = 16)] public int ExactDuplicateCount;
        [DataMember(Name = "outside_room_count", Order = 17)] public int OutsideRoomCount;
        [DataMember(Name = "door_clearance_violation_count", Order = 18)] public int DoorClearanceViolationCount;
        [DataMember(Name = "stair_shaft_hit_count", Order = 19)] public int StairShaftHitCount;
        [DataMember(Name = "host_door_obstacle_count", Order = 20)] public int HostDoorObstacleCount;
        [DataMember(Name = "linked_door_obstacle_count", Order = 21)] public int LinkedDoorObstacleCount;
        [DataMember(Name = "views", Order = 22)] public List<SchoolDistributionReviewViewReport> Views;
        [DataMember(Name = "rooms", Order = 23)] public List<SchoolElectricalReviewV3RoomResult> Rooms;
        [DataMember(Name = "transaction_committed", Order = 24)] public bool TransactionCommitted;
        [DataMember(Name = "saved_as_new_file", Order = 25)] public bool SavedAsNewFile;
        [DataMember(Name = "provider_egress_performed", Order = 26)] public bool ProviderEgressPerformed;
        [DataMember(Name = "document_mutation_performed", Order = 27)] public bool DocumentMutationPerformed;
        [DataMember(Name = "mounting_host_review_count", Order = 28)] public int MountingHostReviewCount;
        [DataMember(Name = "stair_wall_review_marker_count", Order = 29)] public int StairWallReviewMarkerCount;
    }

    [DataContract]
    internal sealed class SchoolV5LayoutPoint
    {
        [DataMember(Name = "x_mm", Order = 1)] public double Xmm;
        [DataMember(Name = "y_mm", Order = 2)] public double Ymm;
    }

    [DataContract]
    internal sealed class SchoolV5LayoutRoom
    {
        [DataMember(Name = "room_id", Order = 1)] public long RoomId;
        [DataMember(Name = "room_name", Order = 2)] public string RoomName;
        [DataMember(Name = "requested_count", Order = 3)] public int RequestedCount;
        [DataMember(Name = "feasible", Order = 4)] public bool Feasible;
        [DataMember(Name = "error", Order = 5)] public string Error;
        [DataMember(Name = "minimum_spacing_mm", Order = 6)] public double MinimumSpacingMm;
        [DataMember(Name = "maximum_coverage_distance_mm", Order = 7)] public double MaximumCoverageDistanceMm;
        [DataMember(Name = "points", Order = 8)] public List<SchoolV5LayoutPoint> Points;
    }

    [DataContract]
    internal sealed class SchoolV5LayoutReport
    {
        [DataMember(Name = "schema_version", Order = 1)] public int SchemaVersion;
        [DataMember(Name = "request_id", Order = 2)] public string RequestId;
        [DataMember(Name = "preview_sha256", Order = 3)] public string PreviewSha256;
        [DataMember(Name = "source_sha256", Order = 4)] public string SourceSha256;
        [DataMember(Name = "room_count", Order = 5)] public int RoomCount;
        [DataMember(Name = "feasible_room_count", Order = 6)] public int FeasibleRoomCount;
        [DataMember(Name = "feasible_light_count", Order = 7)] public int FeasibleLightCount;
        [DataMember(Name = "rooms", Order = 8)] public List<SchoolV5LayoutRoom> Rooms;
        [DataMember(Name = "provider_egress_performed", Order = 9)] public bool ProviderEgressPerformed;
        [DataMember(Name = "document_mutation_performed", Order = 10)] public bool DocumentMutationPerformed;
    }

    internal static class SchoolElectricalReviewV3Handoff
    {
        private const int MaximumRooms = 128;
        private const int MaximumLights = 1400;
        private const int MaximumPowerPoints = 800;

        private sealed class Obstacle
        {
            public BoundingBoxXYZ Box;
            public string Kind;
            public bool FromLink;
        }

        private sealed class LayoutResult
        {
            public List<XYZ> Points;
            public double ClearanceMm;
            public double MaximumCoverageDistanceMm;
            public int Rows;
        }

        private sealed class ExistingOutlet
        {
            public XYZ Point;
            public string LevelKey;
            public bool IsGfi;
        }

        public static string Execute(
            UIApplication uiApplication, string requestId, string previewPath, string outputPath)
        {
            return ExecuteInternal(uiApplication, requestId, previewPath, outputPath, 3);
        }

        public static string ExecuteV4(
            UIApplication uiApplication, string requestId, string previewPath, string outputPath)
        {
            return ExecuteInternal(uiApplication, requestId, previewPath, outputPath, 4);
        }

        public static string ExecuteV5(
            UIApplication uiApplication, string requestId, string previewPath, string outputPath,
            string expectedPreviewSha256)
        {
            return ExecuteInternal(uiApplication, requestId, previewPath, outputPath, 5,
                expectedPreviewSha256);
        }

        public static string PreviewV5(Document document, string requestId, string previewPath)
        {
            SchoolElectricalReviewV3Preview preview = ReadPreview(previewPath);
            Validate(preview, 5);
            string actualSha = Hash(document.PathName);
            if (!string.Equals(actualSha, preview.SourceSha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("School V5 source RVT hash changed.");
            List<Obstacle> obstacles = CollectObstacles(document);
            List<SchoolV5LayoutRoom> results = new List<SchoolV5LayoutRoom>();
            foreach (SchoolElectricalReviewV3Room spec in preview.Rooms)
            {
                Room room = document.GetElement(new ElementId(spec.RoomId)) as Room;
                if (room == null || room.Area <= 0.0 || room.LevelId.Value != spec.LevelId)
                    throw new InvalidDataException("School V5 room identity or level changed.");
                SchoolV5LayoutRoom result = new SchoolV5LayoutRoom
                {
                    RoomId = spec.RoomId, RoomName = spec.RoomName,
                    RequestedCount = spec.LightingCount, Feasible = false,
                    Error = string.Empty, Points = new List<SchoolV5LayoutPoint>()
                };
                try
                {
                    LayoutResult layout = BuildLightingLayoutV4(room, spec.LightingCount, obstacles, preview);
                    result.Feasible = true;
                    result.MinimumSpacingMm = Math.Round(MinimumSpacingMm(layout.Points), 1);
                    result.MaximumCoverageDistanceMm = Math.Round(layout.MaximumCoverageDistanceMm, 1);
                    result.Points = layout.Points.Select(point => new SchoolV5LayoutPoint
                    {
                        Xmm = Math.Round(UnitUtils.ConvertFromInternalUnits(point.X, UnitTypeId.Millimeters), 1),
                        Ymm = Math.Round(UnitUtils.ConvertFromInternalUnits(point.Y, UnitTypeId.Millimeters), 1)
                    }).ToList();
                }
                catch (InvalidDataException exception) { result.Error = exception.Message; }
                results.Add(result);
            }
            SchoolV5LayoutReport report = new SchoolV5LayoutReport
            {
                SchemaVersion = 1, RequestId = requestId,
                PreviewSha256 = preview.PreviewSha256,
                SourceSha256 = actualSha,
                RoomCount = results.Count,
                FeasibleRoomCount = results.Count(item => item.Feasible),
                FeasibleLightCount = results.Where(item => item.Feasible)
                    .Sum(item => item.Points.Count),
                Rooms = results,
                ProviderEgressPerformed = false,
                DocumentMutationPerformed = false
            };
            string relative = "school-v5-layout-previews/" + requestId + ".json";
            string path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "AI-Engineer", "revit", "school-v5-layout-previews", requestId + ".json");
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            DataContractJsonSerializer serializer = new DataContractJsonSerializer(typeof(SchoolV5LayoutReport));
            using (FileStream stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            { serializer.WriteObject(stream, report); stream.Flush(true); }
            return relative;
        }

        private static string ExecuteInternal(
            UIApplication uiApplication, string requestId, string previewPath, string outputPath,
            int reviewVersion, string expectedPreviewSha256 = null)
        {
            Document document = uiApplication.ActiveUIDocument.Document;
            SchoolElectricalReviewV3Preview preview = ReadPreview(previewPath);
            Validate(preview, reviewVersion);
            if (reviewVersion == 5 && !string.Equals(preview.PreviewSha256,
                expectedPreviewSha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("School V5 approved preview SHA does not match the request.");
            Dictionary<long, List<SchoolV5ExpectedPoint>> expectedV5 = null;
            if (reviewVersion == 5)
            {
                if (preview.V5ExpectedPoints == null
                    || preview.V5ExpectedPoints.Count != preview.Rooms.Sum(item => item.LightingCount))
                    throw new InvalidDataException("School V5 expected point count is not bound to the preview.");
                expectedV5 = preview.V5ExpectedPoints.GroupBy(item => item.RoomId)
                    .ToDictionary(group => group.Key, group => group.OrderBy(item => item.Index).ToList());
                if (expectedV5.Count != preview.Rooms.Count || expectedV5.Any(pair =>
                    pair.Value.Select(item => item.Index).Distinct().Count() != pair.Value.Count))
                    throw new InvalidDataException("School V5 expected point identities are invalid.");
            }
            string output = Path.GetFullPath(outputPath);
            if (reviewVersion == 5 && !string.Equals(output,
                Path.Combine(Path.GetDirectoryName(document.PathName),
                    "School-Electrical-Review-V5-20261031.rvt"),
                StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("School V5 output must be the fixed new scratch RVT.");
            if (File.Exists(output)) throw new IOException("School electrical review output already exists.");
            Directory.CreateDirectory(Path.GetDirectoryName(output));

            List<Obstacle> obstacles = CollectObstacles(document);
            int hostDoors = obstacles.Count(item => item.Kind == "door" && !item.FromLink);
            int linkedDoors = obstacles.Count(item => item.Kind == "door" && item.FromLink);
            Dictionary<string, ViewPlan> lightingViews = new Dictionary<string, ViewPlan>(StringComparer.Ordinal);
            Dictionary<string, ViewPlan> powerViews = new Dictionary<string, ViewPlan>(StringComparer.Ordinal);
            Dictionary<string, int> viewPoints = new Dictionary<string, int>(StringComparer.Ordinal);
            Dictionary<string, int> viewCurves = new Dictionary<string, int>(StringComparer.Ordinal);
            List<SchoolElectricalReviewV3RoomResult> roomResults = new List<SchoolElectricalReviewV3RoomResult>();
            HashSet<string> lightingKeys = new HashSet<string>(StringComparer.Ordinal);
            HashSet<string> powerKeys = new HashSet<string>(StringComparer.Ordinal);
            int duplicateCount = 0;
            int outsideRoomCount = 0;
            int doorViolations = 0;
            int stairShaftHits = 0;
            int totalLights = 0;
            int totalNewDuplex = 0;
            int totalNewGfi = 0;
            int totalDedicated = 0;
            int totalExisting = 0;
            int totalPanels = 0;
            int totalCurves = 0;
            int totalHostReviewLights = 0;
            int stairWallReviewMarkers = 0;
            ViewPlan firstView = null;
            List<Ceiling> ceilings = reviewVersion == 5
                ? new FilteredElementCollector(document).OfCategory(BuiltInCategory.OST_Ceilings)
                    .WhereElementIsNotElementType().OfType<Ceiling>().ToList()
                : new List<Ceiling>();

            using (Transaction transaction = new Transaction(document, "AI-Engineer school electrical review V" + reviewVersion))
            {
                transaction.Start();
                Dictionary<string, GraphicsStyle> lightingStyles = CreateLightingStyles(document);
                Dictionary<string, GraphicsStyle> hostReviewStyles = reviewVersion == 5
                    ? new[] { "L1", "L2", "L3", "L4", "L5", "L6" }.ToDictionary(
                        type => type,
                        type => EnsureLineStyle(document, "AI-V5-HOST-REVIEW-" + type,
                            new Color(230, 90, 0), 7), StringComparer.Ordinal)
                    : null;
                GraphicsStyle duplexStyle = EnsureLineStyle(document, "AI-V3-POWER-DUPLEX", new Color(0, 102, 204), 5);
                GraphicsStyle gfiStyle = EnsureLineStyle(document, "AI-V3-POWER-GFI", new Color(180, 0, 180), 5);
                GraphicsStyle dedicatedStyle = EnsureLineStyle(document, "AI-V3-POWER-DEDICATED", new Color(220, 0, 0), 6);
                GraphicsStyle existingStyle = EnsureLineStyle(document, "AI-V3-POWER-EXISTING", new Color(0, 150, 0), 6);
                GraphicsStyle panelStyle = EnsureLineStyle(document, "AI-V3-POWER-PANEL", new Color(0, 80, 80), 8);

                foreach (IGrouping<string, SchoolElectricalReviewV3Room> group in preview.Rooms.GroupBy(item => item.LevelKey))
                {
                    SchoolElectricalReviewV3Room first = group.First();
                    Level level = document.GetElement(new ElementId(first.LevelId)) as Level;
                    if (level == null) throw new InvalidDataException("Electrical V3 level no longer exists.");
                    string suffix = CleanLevelName(group.Key);
                    ViewPlan lighting = CreateReviewView(document, level, "AI REVIEW V" + reviewVersion + " - ELECTRICAL - LIGHTING - " + suffix);
                    ViewPlan power = CreateReviewView(document, level, "AI REVIEW V" + reviewVersion + " - ELECTRICAL - POWER - " + suffix);
                    lightingViews[group.Key] = lighting;
                    powerViews[group.Key] = power;
                    viewPoints["L:" + group.Key] = 0;
                    viewCurves["L:" + group.Key] = 0;
                    viewPoints["P:" + group.Key] = 0;
                    viewCurves["P:" + group.Key] = 0;
                    if (firstView == null) firstView = lighting;
                    TryHide(lighting, BuiltInCategory.OST_LightingFixtures);
                    TryHide(power, BuiltInCategory.OST_ElectricalFixtures);
                }

                foreach (SchoolElectricalReviewV3Room spec in preview.Rooms)
                {
                    Room room = document.GetElement(new ElementId(spec.RoomId)) as Room;
                    if (room == null || room.Area <= 0.0 || room.LevelId.Value != spec.LevelId)
                    {
                        transaction.RollBack();
                        throw new InvalidDataException("Electrical V3 room identity or level no longer matches.");
                    }
                    LayoutResult lighting = reviewVersion >= 4
                        ? BuildLightingLayoutV4(room, spec.LightingCount, obstacles, preview)
                        : BuildLightingLayout(room, spec.LightingCount, obstacles, preview);
                    if (lighting.Points.Count != spec.LightingCount)
                    {
                        transaction.RollBack();
                        throw new InvalidDataException("Electrical V3 could not place every lighting point.");
                    }
                    if (reviewVersion == 5)
                    {
                        List<SchoolV5ExpectedPoint> expected;
                        if (!expectedV5.TryGetValue(spec.RoomId, out expected)
                            || expected.Count != lighting.Points.Count)
                            throw new InvalidDataException("School V5 expected room points are missing.");
                        for (int pointIndex = 0; pointIndex < expected.Count; pointIndex++)
                        {
                            XYZ actual = lighting.Points[pointIndex];
                            if (expected[pointIndex].Index != pointIndex
                                || Math.Abs(UnitUtils.ConvertFromInternalUnits(actual.X,
                                    UnitTypeId.Millimeters) - expected[pointIndex].Xmm) > 1.0
                                || Math.Abs(UnitUtils.ConvertFromInternalUnits(actual.Y,
                                    UnitTypeId.Millimeters) - expected[pointIndex].Ymm) > 1.0)
                                throw new InvalidDataException("School V5 layout differs from the reviewed coordinates.");
                        }
                    }
                    ViewPlan lightingView = lightingViews[spec.LevelKey];
                    GraphicsStyle lightStyle;
                    if (!lightingStyles.TryGetValue(spec.LuminaireType, out lightStyle))
                        throw new InvalidDataException("Electrical V3 luminaire type is unsupported.");
                    int roomHostReviewLights = 0;
                    foreach (XYZ point in lighting.Points)
                    {
                        XYZ roomPoint = new XYZ(point.X, point.Y, room.Level.Elevation + Feet(300.0));
                        if (!room.IsPointInRoom(roomPoint)) outsideRoomCount++;
                        if (DoorBlockedForRoom(point, room, obstacles, preview.LightingDoorClearanceMm)) doorViolations++;
                        if (BlockedAny(point, obstacles, new[] { "stair", "shaft" }, preview.StairShaftClearanceMm)) stairShaftHits++;
                        string key = PointKey(spec.LevelId, point);
                        if (!lightingKeys.Add(key)) duplicateCount++;
                        GraphicsStyle pointStyle = lightStyle;
                        if (reviewVersion == 5 && !ceilings.Any(ceiling =>
                            ceiling.LevelId.Value == spec.LevelId
                            && ContainsXY(ceiling.get_BoundingBox(null), point)))
                        {
                            pointStyle = hostReviewStyles[spec.LuminaireType];
                            roomHostReviewLights++;
                            totalHostReviewLights++;
                        }
                        int curves = DrawLightingSymbol(document, lightingView, point,
                            room.Level.Elevation, spec.LuminaireType, pointStyle);
                        totalCurves += curves;
                        totalLights++;
                        viewPoints["L:" + spec.LevelKey]++;
                        viewCurves["L:" + spec.LevelKey] += curves;
                    }

                    int requiredPower = spec.NewDuplexCount + spec.NewGfiCount + spec.DedicatedCount;
                    List<XYZ> powerPoints = BuildPowerLayout(room, requiredPower, obstacles, preview);
                    if (powerPoints.Count != requiredPower)
                    {
                        transaction.RollBack();
                        throw new InvalidDataException("Electrical V3 could not place every room power point.");
                    }
                    ViewPlan powerView = powerViews[spec.LevelKey];
                    int cursor = 0;
                    int duplexPlaced = 0;
                    int gfiPlaced = 0;
                    int dedicatedPlaced = 0;
                    for (int index = 0; index < powerPoints.Count; index++)
                    {
                        XYZ point = powerPoints[index];
                        XYZ roomPoint = new XYZ(point.X, point.Y, room.Level.Elevation + Feet(300.0));
                        if (!room.IsPointInRoom(roomPoint)) outsideRoomCount++;
                        if (DoorBlockedForRoom(point, room, obstacles, preview.PowerDoorClearanceMm)) doorViolations++;
                        if (BlockedAny(point, obstacles, new[] { "stair", "shaft" }, preview.StairShaftClearanceMm)) stairShaftHits++;
                        string key = PointKey(spec.LevelId, point);
                        if (!powerKeys.Add(key)) duplicateCount++;
                        int curves;
                        if (cursor < spec.NewDuplexCount)
                        {
                            curves = DrawDuplex(document, powerView, point, room.Level.Elevation, duplexStyle);
                            duplexPlaced++;
                            totalNewDuplex++;
                        }
                        else if (cursor < spec.NewDuplexCount + spec.NewGfiCount)
                        {
                            curves = DrawGfi(document, powerView, point, room.Level.Elevation, gfiStyle);
                            gfiPlaced++;
                            totalNewGfi++;
                        }
                        else
                        {
                            curves = DrawDedicated(document, powerView, point, room.Level.Elevation, dedicatedStyle);
                            dedicatedPlaced++;
                            totalDedicated++;
                        }
                        cursor++;
                        totalCurves += curves;
                        viewPoints["P:" + spec.LevelKey]++;
                        viewCurves["P:" + spec.LevelKey] += curves;
                    }

                    if (IsPanelRoom(spec.RoomName))
                    {
                        XYZ center = RoomCenter(room);
                        int curves = DrawPanel(document, powerView, center, room.Level.Elevation, panelStyle);
                        string panelName = spec.RoomName.StartsWith("ELECTRICAL", StringComparison.OrdinalIgnoreCase)
                            ? "MDB-L1 CANDIDATE" : "SMDB-L2 CANDIDATE";
                        AddText(document, powerView, center + new XYZ(Feet(600.0), Feet(400.0), 0), panelName);
                        totalPanels++;
                        totalCurves += curves;
                        viewPoints["P:" + spec.LevelKey]++;
                        viewCurves["P:" + spec.LevelKey] += curves;
                    }

                    roomResults.Add(new SchoolElectricalReviewV3RoomResult
                    {
                        RoomId = spec.RoomId,
                        RoomName = spec.RoomName,
                        LevelKey = spec.LevelKey,
                        LuminaireType = spec.LuminaireType,
                        LightingRequired = spec.LightingCount,
                        LightingPlaced = lighting.Points.Count,
                        NewDuplexPlaced = duplexPlaced,
                        NewGfiPlaced = gfiPlaced,
                        DedicatedPlaced = dedicatedPlaced,
                        MinimumLightingSpacingMm = Math.Round(MinimumSpacingMm(lighting.Points), 1),
                        LightingBoundaryClearanceUsedMm = lighting.ClearanceMm,
                        MaximumCoverageDistanceMm = Math.Round(lighting.MaximumCoverageDistanceMm, 1),
                        ActualRoomAreaM2 = Math.Round(UnitUtils.ConvertFromInternalUnits(room.Area, UnitTypeId.SquareMeters), 3),
                        BoundingBoxAreaM2 = Math.Round(UnitUtils.ConvertFromInternalUnits(
                            (room.get_BoundingBox(null).Max.X - room.get_BoundingBox(null).Min.X)
                            * (room.get_BoundingBox(null).Max.Y - room.get_BoundingBox(null).Min.Y), UnitTypeId.SquareMeters), 3),
                        LightingRows = lighting.Rows,
                        MountingHostReviewCount = roomHostReviewLights
                    });
                }

                if (reviewVersion == 5)
                {
                    Wall wall = document.GetElement(new ElementId(preview.StairWallCandidate.WallId)) as Wall;
                    Element stair = document.GetElement(new ElementId(194966));
                    XYZ marked = new XYZ(Feet(preview.StairWallCandidate.MarkedXmm),
                        Feet(preview.StairWallCandidate.MarkedYmm), 0);
                    BoundingBoxXYZ wallBox = wall == null ? null : wall.get_BoundingBox(null);
                    if (wallBox == null
                        || !ContainsXY(stair == null ? null : stair.get_BoundingBox(null), marked)
                        || marked.Y < wallBox.Min.Y || marked.Y > wallBox.Max.Y
                        || Math.Min(Math.Abs(marked.X - wallBox.Min.X),
                            Math.Abs(marked.X - wallBox.Max.X)) > Feet(1000.0))
                    {
                        transaction.RollBack();
                        throw new InvalidDataException("V5 stair wall review marker lost its source geometry.");
                    }
                    ViewPlan stairView = lightingViews["Level 1"];
                    GraphicsStyle stairStyle = EnsureLineStyle(document,
                        "AI-V5-STAIR-WALL-REVIEW", new Color(220, 0, 0), 8);
                    XYZ marker = new XYZ(wallBox.Min.X - Feet(180.0), marked.Y,
                        stairView.GenLevel.Elevation);
                    int markerCurves = DrawOctagon(document, stairView, marker, 320.0, stairStyle, true);
                    AddText(document, stairView, marker + new XYZ(Feet(500.0), Feet(450.0), 0),
                        "STAIR WALL LIGHT? VERIFY MOUNT HEIGHT / IES");
                    totalCurves += markerCurves;
                    viewCurves["L:Level 1"] += markerCurves;
                    stairWallReviewMarkers++;
                }

                foreach (ExistingOutlet outlet in CollectExistingOutlets(document, preview.Rooms))
                {
                    ViewPlan powerView;
                    if (!powerViews.TryGetValue(outlet.LevelKey, out powerView)) continue;
                    int curves = outlet.IsGfi
                        ? DrawGfi(document, powerView, outlet.Point, powerView.GenLevel.Elevation, existingStyle)
                        : DrawDuplex(document, powerView, outlet.Point, powerView.GenLevel.Elevation, existingStyle);
                    totalExisting++;
                    totalCurves += curves;
                    viewPoints["P:" + outlet.LevelKey]++;
                    viewCurves["P:" + outlet.LevelKey] += curves;
                }

                int expectedLights = preview.Rooms.Sum(item => item.LightingCount);
                int expectedDuplex = preview.Rooms.Sum(item => item.NewDuplexCount);
                int expectedGfi = preview.Rooms.Sum(item => item.NewGfiCount);
                int expectedDedicated = preview.Rooms.Sum(item => item.DedicatedCount);
                if (duplicateCount != 0 || outsideRoomCount != 0 || doorViolations != 0 || stairShaftHits != 0
                    || totalLights != expectedLights || totalNewDuplex != expectedDuplex
                    || totalNewGfi != expectedGfi || totalDedicated != expectedDedicated)
                {
                    transaction.RollBack();
                    throw new InvalidDataException("Electrical V3 quality checks rejected the generated layout.");
                }

                foreach (string levelKey in lightingViews.Keys)
                {
                    AddLightingLegend(document, lightingViews[levelKey], levelKey, preview.Rooms,
                        reviewVersion);
                    AddPowerLegend(document, powerViews[levelKey], levelKey, preview.Rooms,
                        totalExisting, totalPanels, reviewVersion);
                }
                if (transaction.Commit() != TransactionStatus.Committed)
                    throw new InvalidOperationException("Electrical V3 review transaction did not commit.");
            }

            SaveAsOptions options = new SaveAsOptions();
            options.OverwriteExistingFile = false;
            document.SaveAs(output, options);
            if (!File.Exists(output)) throw new IOException("Revit did not create the electrical V3 output.");
            if (firstView != null) uiApplication.ActiveUIDocument.ActiveView = firstView;

            List<SchoolDistributionReviewViewReport> views = new List<SchoolDistributionReviewViewReport>();
            foreach (KeyValuePair<string, ViewPlan> pair in lightingViews)
            {
                views.Add(new SchoolDistributionReviewViewReport
                {
                    LevelKey = "LIGHTING / " + pair.Key,
                    ViewId = pair.Value.Id.Value,
                    ViewName = pair.Value.Name,
                    PointCount = viewPoints["L:" + pair.Key],
                    DetailCurveCount = viewCurves["L:" + pair.Key]
                });
            }
            foreach (KeyValuePair<string, ViewPlan> pair in powerViews)
            {
                views.Add(new SchoolDistributionReviewViewReport
                {
                    LevelKey = "POWER / " + pair.Key,
                    ViewId = pair.Value.Id.Value,
                    ViewName = pair.Value.Name,
                    PointCount = viewPoints["P:" + pair.Key],
                    DetailCurveCount = viewCurves["P:" + pair.Key]
                });
            }
            SchoolElectricalReviewV3Report report = new SchoolElectricalReviewV3Report
            {
                SchemaVersion = 1,
                RequestId = requestId,
                PreviewId = preview.PreviewId,
                PreviewSha256 = preview.PreviewSha256,
                SourceSha256 = preview.SourceSha256,
                OutputPath = output,
                OutputSha256 = Hash(output),
                RoomCount = preview.Rooms.Count,
                LightingPointCount = totalLights,
                NewDuplexCount = totalNewDuplex,
                NewGfiCount = totalNewGfi,
                DedicatedCount = totalDedicated,
                ExistingOutletCount = totalExisting,
                PanelCandidateCount = totalPanels,
                DetailCurveCount = totalCurves,
                ExactDuplicateCount = duplicateCount,
                OutsideRoomCount = outsideRoomCount,
                DoorClearanceViolationCount = doorViolations,
                StairShaftHitCount = stairShaftHits,
                HostDoorObstacleCount = hostDoors,
                LinkedDoorObstacleCount = linkedDoors,
                Views = views,
                Rooms = roomResults,
                TransactionCommitted = true,
                SavedAsNewFile = true,
                ProviderEgressPerformed = false,
                DocumentMutationPerformed = true,
                MountingHostReviewCount = totalHostReviewLights,
                StairWallReviewMarkerCount = stairWallReviewMarkers
            };
            return WriteReport(requestId, report, reviewVersion);
        }

        private static LayoutResult BuildLightingLayout(
            Room room, int required, List<Obstacle> obstacles, SchoolElectricalReviewV3Preview preview)
        {
            BoundingBoxXYZ box = room.get_BoundingBox(null);
            if (box == null) throw new InvalidDataException("Electrical V3 room lacks a bounding box.");
            double width = box.Max.X - box.Min.X;
            double height = box.Max.Y - box.Min.Y;
            if (width <= Feet(300.0) || height <= Feet(300.0))
                throw new InvalidDataException("Electrical V3 room bounding box is too small.");
            IList<IList<BoundarySegment>> loops = room.GetBoundarySegments(new SpatialElementBoundaryOptions());
            if (loops == null) throw new InvalidDataException("Electrical V3 room has no boundary segments.");
            double[] clearances = new[] { preview.LightingBoundaryClearanceMm, 350.0, 250.0, 150.0 };
            foreach (double clearanceMm in clearances.Distinct().Where(value => value >= 0.0))
            {
                List<XYZ> best = null;
                double bestScore = Double.PositiveInfinity;
                int maximumRows = Math.Min(Math.Max(required, 8), 32);
                for (int rows = 1; rows <= maximumRows; rows++)
                {
                    int baseColumns = Math.Max(1, (int)Math.Ceiling((double)required / rows));
                    for (int columns = baseColumns; columns <= baseColumns + 10; columns++)
                    {
                        List<XYZ> valid = GridCandidates(room, box, loops, rows, columns, obstacles, clearanceMm, preview);
                        if (valid.Count < required) continue;
                        double score = Math.Abs(Math.Log(((double)columns / rows) / (width / height)))
                            + (valid.Count - required) * 0.08;
                        if (score < bestScore)
                        {
                            bestScore = score;
                            best = EvenlySelect(valid, required);
                        }
                    }
                }
                if (best != null && best.Count == required)
                    return new LayoutResult { Points = best, ClearanceMm = clearanceMm };
            }
            throw new InvalidDataException("Electrical V3 room geometry cannot support its lighting count: "
                + room.Name + " " + room.Number + " requires " + required + ".");
        }

        private static LayoutResult BuildLightingLayoutV4(
            Room room, int required, List<Obstacle> obstacles, SchoolElectricalReviewV3Preview preview)
        {
            BoundingBoxXYZ box = room.get_BoundingBox(null);
            if (box == null || required < 1) throw new InvalidDataException("V4 room or lighting count is invalid.");
            double width = box.Max.X - box.Min.X;
            double height = box.Max.Y - box.Min.Y;
            if (width <= Feet(300.0) || height <= Feet(300.0))
                throw new InvalidDataException("V4 room bounding box is too small.");
            IList<IList<BoundarySegment>> loops = room.GetBoundarySegments(new SpatialElementBoundaryOptions());
            if (loops == null) throw new InvalidDataException("V4 room has no boundary segments.");
            List<XYZ> coverageSamples = SampleRoomInterior(room, box, loops, obstacles, preview, 900.0);
            if (coverageSamples.Count == 0)
                throw new InvalidDataException("V4 room has no interior coverage samples: " + room.Name);

            int idealRows = Math.Max(1, (int)Math.Round(Math.Sqrt(required * height / width)));
            LayoutResult best = null;
            double bestScore = Double.PositiveInfinity;
            double[] clearances = new[] { preview.LightingBoundaryClearanceMm, 350.0, 250.0, 150.0 };
            foreach (double clearanceMm in clearances.Distinct().Where(value => value >= 0.0))
            {
                for (int rows = Math.Max(1, idealRows - 6); rows <= Math.Min(required, idealRows + 6); rows++)
                {
                    List<XYZ> points = BalancedRoomRows(room, box, loops, rows, required,
                        obstacles, clearanceMm, preview);
                    if (points == null || points.Count != required) continue;
                    double minimumSpacing = MinimumSpacingMm(points);
                    if (required > 1 && minimumSpacing < 600.0) continue;
                    double sum;
                    double maximum = CoverageDistanceMm(coverageSamples, points, out sum);
                    double score = maximum + 0.15 * sum / coverageSamples.Count
                        + (clearanceMm < preview.LightingBoundaryClearanceMm ? 100.0 : 0.0);
                    if (score < bestScore)
                    {
                        bestScore = score;
                        best = new LayoutResult
                        {
                            Points = points,
                            ClearanceMm = clearanceMm,
                            MaximumCoverageDistanceMm = maximum,
                            Rows = rows
                        };
                    }
                }
                if (best != null) break;
            }
            if (best == null)
                throw new InvalidDataException("V4 could not distribute every light in " + room.Name + " " + room.Number);
            double areaM2 = UnitUtils.ConvertFromInternalUnits(room.Area, UnitTypeId.SquareMeters);
            double coverageLimit = Math.Max(2500.0, 1.2 * Math.Sqrt(areaM2 / required) * 1000.0);
            if (best.MaximumCoverageDistanceMm > coverageLimit)
                throw new InvalidDataException("V4 coverage gap in " + room.Name + " " + room.Number
                    + ": " + Math.Round(best.MaximumCoverageDistanceMm) + " mm > "
                    + Math.Round(coverageLimit) + " mm with " + best.Rows + " rows.");
            return best;
        }

        private static List<XYZ> SampleRoomInterior(
            Room room, BoundingBoxXYZ box, IList<IList<BoundarySegment>> loops,
            List<Obstacle> obstacles, SchoolElectricalReviewV3Preview preview, double intervalMm)
        {
            double width = box.Max.X - box.Min.X;
            double height = box.Max.Y - box.Min.Y;
            int columns = Math.Max(1, (int)Math.Ceiling(width / Feet(intervalMm)));
            int rows = Math.Max(1, (int)Math.Ceiling(height / Feet(intervalMm)));
            double testZ = room.Level.Elevation + Feet(300.0);
            List<XYZ> samples = new List<XYZ>();
            for (int row = 0; row < rows; row++)
                for (int column = 0; column < columns; column++)
                {
                    XYZ point = new XYZ(box.Min.X + (column + 0.5) * width / columns,
                        box.Min.Y + (row + 0.5) * height / rows, room.Level.Elevation);
                    if (!room.IsPointInRoom(new XYZ(point.X, point.Y, testZ))) continue;
                    if (BoundaryDistanceMm(point, loops) < 150.0) continue;
                    if (BlockedAny(point, obstacles, new[] { "stair", "shaft" }, preview.StairShaftClearanceMm)) continue;
                    samples.Add(point);
                }
            return samples;
        }

        private static List<XYZ> BalancedRoomRows(
            Room room, BoundingBoxXYZ box, IList<IList<BoundarySegment>> loops,
            int rows, int required, List<Obstacle> obstacles, double clearanceMm,
            SchoolElectricalReviewV3Preview preview)
        {
            double width = box.Max.X - box.Min.X;
            double height = box.Max.Y - box.Min.Y;
            int columns = Math.Max(required, Math.Max(4, (int)Math.Ceiling(width / Feet(450.0))));
            if (columns > 400) throw new InvalidDataException("V4 room sampling exceeds 400 columns.");
            double testZ = room.Level.Elevation + Feet(300.0);
            List<List<XYZ>> segments = new List<List<XYZ>>();
            for (int row = 0; row < rows; row++)
            {
                double y = box.Min.Y + (row + 0.5) * height / rows;
                List<XYZ> current = new List<XYZ>();
                for (int column = 0; column < columns; column++)
                {
                    XYZ point = new XYZ(box.Min.X + (column + 0.5) * width / columns,
                        y, room.Level.Elevation);
                    bool valid = room.IsPointInRoom(new XYZ(point.X, point.Y, testZ))
                        && BoundaryDistanceMm(point, loops) >= clearanceMm
                        && !DoorBlockedForRoom(point, room, obstacles, preview.LightingDoorClearanceMm)
                        && !BlockedAny(point, obstacles, new[] { "stair", "shaft" }, preview.StairShaftClearanceMm);
                    if (valid) current.Add(point);
                    else if (current.Count > 0)
                    {
                        segments.Add(current);
                        current = new List<XYZ>();
                    }
                }
                if (current.Count > 0) segments.Add(current);
            }
            if (segments.Count == 0 || segments.Count > required) return null;
            int[] allocation = Enumerable.Repeat(1, segments.Count).ToArray();
            int remaining = required - segments.Count;
            int capacity = segments.Sum(segment => segment.Count - 1);
            if (capacity < remaining) return null;
            while (remaining > 0)
            {
                int best = -1;
                double score = Double.NegativeInfinity;
                for (int index = 0; index < segments.Count; index++)
                {
                    if (allocation[index] >= segments[index].Count) continue;
                    double candidate = (double)segments[index].Count / allocation[index];
                    if (candidate > score) { score = candidate; best = index; }
                }
                if (best < 0) return null;
                allocation[best]++;
                remaining--;
            }
            List<XYZ> points = new List<XYZ>();
            for (int index = 0; index < segments.Count; index++)
            {
                List<XYZ> segment = segments[index];
                for (int slot = 0; slot < allocation[index]; slot++)
                {
                    int sample = Math.Min(segment.Count - 1,
                        (int)Math.Floor((slot + 0.5) * segment.Count / allocation[index]));
                    points.Add(segment[sample]);
                }
            }
            return points;
        }

        private static double CoverageDistanceMm(List<XYZ> samples, List<XYZ> lights, out double totalMm)
        {
            double maximum = 0.0;
            totalMm = 0.0;
            foreach (XYZ sample in samples)
            {
                double nearest = Double.PositiveInfinity;
                foreach (XYZ light in lights)
                {
                    double dx = sample.X - light.X;
                    double dy = sample.Y - light.Y;
                    nearest = Math.Min(nearest, dx * dx + dy * dy);
                }
                double distance = UnitUtils.ConvertFromInternalUnits(Math.Sqrt(nearest), UnitTypeId.Millimeters);
                maximum = Math.Max(maximum, distance);
                totalMm += distance;
            }
            return maximum;
        }

        private static List<XYZ> GridCandidates(
            Room room, BoundingBoxXYZ box, IList<IList<BoundarySegment>> loops,
            int rows, int columns, List<Obstacle> obstacles,
            double boundaryClearanceMm, SchoolElectricalReviewV3Preview preview)
        {
            List<XYZ> points = new List<XYZ>();
            double dx = (box.Max.X - box.Min.X) / columns;
            double dy = (box.Max.Y - box.Min.Y) / rows;
            double testZ = room.Level.Elevation + Feet(300.0);
            for (int row = 0; row < rows; row++)
            {
                for (int column = 0; column < columns; column++)
                {
                    XYZ point = new XYZ(box.Min.X + (column + 0.5) * dx, box.Min.Y + (row + 0.5) * dy, room.Level.Elevation);
                    XYZ roomPoint = new XYZ(point.X, point.Y, testZ);
                    if (!room.IsPointInRoom(roomPoint)) continue;
                    if (BoundaryDistanceMm(roomPoint, loops) < boundaryClearanceMm) continue;
                    if (DoorBlockedForRoom(point, room, obstacles, preview.LightingDoorClearanceMm)) continue;
                    if (BlockedAny(point, obstacles, new[] { "stair", "shaft" }, preview.StairShaftClearanceMm)) continue;
                    points.Add(point);
                }
            }
            return points;
        }

        private static List<XYZ> BuildPowerLayout(
            Room room, int required, List<Obstacle> obstacles, SchoolElectricalReviewV3Preview preview)
        {
            if (required == 0) return new List<XYZ>();
            IList<IList<BoundarySegment>> loops = room.GetBoundarySegments(new SpatialElementBoundaryOptions());
            if (loops == null) throw new InvalidDataException("Electrical V3 room has no boundary segments.");
            List<XYZ> candidates = new List<XYZ>();
            double margin = Feet(350.0);
            double interval = Feet(500.0);
            double insideOffset = Feet(120.0);
            double testZ = room.Level.Elevation + Feet(300.0);
            foreach (IList<BoundarySegment> loop in loops)
            {
                foreach (BoundarySegment segment in loop)
                {
                    Curve curve = segment.GetCurve();
                    double length = curve.ApproximateLength;
                    if (length <= margin * 2.0) continue;
                    double usable = length - margin * 2.0;
                    int samples = Math.Max(1, (int)Math.Floor(usable / interval) + 1);
                    for (int sample = 0; sample < samples; sample++)
                    {
                        double distance = margin + (sample + 0.5) * usable / samples;
                        double parameter = Math.Max(0.01, Math.Min(0.99, distance / length));
                        XYZ boundary = curve.Evaluate(parameter, true);
                        XYZ tangent = curve.ComputeDerivatives(parameter, true).BasisX.Normalize();
                        XYZ normal = new XYZ(-tangent.Y, tangent.X, 0.0);
                        XYZ first = boundary + normal * insideOffset;
                        XYZ second = boundary - normal * insideOffset;
                        XYZ selected = null;
                        if (room.IsPointInRoom(new XYZ(first.X, first.Y, testZ))) selected = first;
                        else if (room.IsPointInRoom(new XYZ(second.X, second.Y, testZ))) selected = second;
                        if (selected == null) continue;
                        selected = new XYZ(selected.X, selected.Y, room.Level.Elevation);
                        if (DoorBlockedForRoom(selected, room, obstacles, preview.PowerDoorClearanceMm)) continue;
                        if (BlockedAny(selected, obstacles, new[] { "stair", "shaft" }, preview.StairShaftClearanceMm)) continue;
                        if (candidates.Any(item => PlanDistanceMm(item, selected) < 180.0)) continue;
                        candidates.Add(selected);
                    }
                }
            }
            if (candidates.Count < required)
                throw new InvalidDataException("Electrical V3 room perimeter cannot support its power count: "
                    + room.Name + " " + room.Number + " requires " + required
                    + " and produced " + candidates.Count + " candidates.");
            return EvenlySelect(candidates, required);
        }

        private static double BoundaryDistanceMm(XYZ point, IList<IList<BoundarySegment>> loops)
        {
            double minimum = Double.PositiveInfinity;
            if (loops == null) return 0.0;
            foreach (IList<BoundarySegment> loop in loops)
            {
                foreach (BoundarySegment segment in loop)
                {
                    Curve curve = segment.GetCurve();
                    XYZ planar = new XYZ(point.X, point.Y, curve.GetEndPoint(0).Z);
                    minimum = Math.Min(minimum, curve.Distance(planar));
                }
            }
            return UnitUtils.ConvertFromInternalUnits(minimum, UnitTypeId.Millimeters);
        }

        private static List<XYZ> EvenlySelect(List<XYZ> points, int count)
        {
            List<XYZ> ordered = points.ToList();
            if (ordered.Count == count) return ordered;
            List<XYZ> selected = new List<XYZ>();
            if (count == 1) { selected.Add(ordered[ordered.Count / 2]); return selected; }
            HashSet<int> indexes = new HashSet<int>();
            for (int index = 0; index < count; index++)
            {
                int chosen = (int)Math.Round(index * (ordered.Count - 1.0) / (count - 1.0));
                while (indexes.Contains(chosen) && chosen + 1 < ordered.Count) chosen++;
                indexes.Add(chosen);
                selected.Add(ordered[chosen]);
            }
            return selected;
        }

        private static List<Obstacle> CollectObstacles(Document document)
        {
            List<Obstacle> result = new List<Obstacle>();
            AddCategoryObstacles(document, null, BuiltInCategory.OST_Doors, "door", false, result);
            AddCategoryObstacles(document, null, BuiltInCategory.OST_Stairs, "stair", false, result);
            AddCategoryObstacles(document, null, BuiltInCategory.OST_StairsRuns, "stair", false, result);
            AddCategoryObstacles(document, null, BuiltInCategory.OST_StairsLandings, "stair", false, result);
            AddCategoryObstacles(document, null, BuiltInCategory.OST_ShaftOpening, "shaft", false, result);
            foreach (RevitLinkInstance link in new FilteredElementCollector(document)
                .OfClass(typeof(RevitLinkInstance)).Cast<RevitLinkInstance>())
            {
                Document linked = link.GetLinkDocument();
                if (linked == null) continue;
                Transform transform = link.GetTotalTransform();
                AddCategoryObstacles(linked, transform, BuiltInCategory.OST_Doors, "door", true, result);
                AddCategoryObstacles(linked, transform, BuiltInCategory.OST_Stairs, "stair", true, result);
                AddCategoryObstacles(linked, transform, BuiltInCategory.OST_StairsRuns, "stair", true, result);
                AddCategoryObstacles(linked, transform, BuiltInCategory.OST_StairsLandings, "stair", true, result);
                AddCategoryObstacles(linked, transform, BuiltInCategory.OST_ShaftOpening, "shaft", true, result);
            }
            return result;
        }

        private static void AddCategoryObstacles(
            Document source, Transform transform, BuiltInCategory category, string kind, bool fromLink,
            List<Obstacle> result)
        {
            foreach (Element element in new FilteredElementCollector(source)
                .OfCategory(category).WhereElementIsNotElementType())
            {
                BoundingBoxXYZ box = element.get_BoundingBox(null);
                if (box == null) continue;
                result.Add(new Obstacle
                {
                    Box = transform == null ? box : TransformBox(box, transform),
                    Kind = kind,
                    FromLink = fromLink
                });
            }
        }

        private static BoundingBoxXYZ TransformBox(BoundingBoxXYZ box, Transform transform)
        {
            List<XYZ> corners = new List<XYZ>();
            for (int x = 0; x < 2; x++)
                for (int y = 0; y < 2; y++)
                    for (int z = 0; z < 2; z++)
                        corners.Add(transform.OfPoint(new XYZ(
                            x == 0 ? box.Min.X : box.Max.X,
                            y == 0 ? box.Min.Y : box.Max.Y,
                            z == 0 ? box.Min.Z : box.Max.Z)));
            BoundingBoxXYZ transformed = new BoundingBoxXYZ();
            transformed.Min = new XYZ(corners.Min(item => item.X), corners.Min(item => item.Y), corners.Min(item => item.Z));
            transformed.Max = new XYZ(corners.Max(item => item.X), corners.Max(item => item.Y), corners.Max(item => item.Z));
            return transformed;
        }

        private static bool Blocked(XYZ point, List<Obstacle> obstacles, string kind, double clearanceMm)
        {
            double clearance = Feet(clearanceMm);
            double zTolerance = Feet(500.0);
            foreach (Obstacle obstacle in obstacles.Where(item => item.Kind == kind))
            {
                BoundingBoxXYZ box = obstacle.Box;
                if (point.Z < box.Min.Z - zTolerance || point.Z > box.Max.Z + zTolerance) continue;
                if (point.X >= box.Min.X - clearance && point.X <= box.Max.X + clearance
                    && point.Y >= box.Min.Y - clearance && point.Y <= box.Max.Y + clearance)
                    return true;
            }
            return false;
        }

        private static bool DoorBlockedForRoom(
            XYZ point, Room room, List<Obstacle> obstacles, double clearanceMm)
        {
            double clearance = Feet(clearanceMm);
            double zTolerance = Feet(500.0);
            IList<IList<BoundarySegment>> loops = null;
            foreach (Obstacle obstacle in obstacles.Where(item => item.Kind == "door"))
            {
                BoundingBoxXYZ box = obstacle.Box;
                if (point.Z < box.Min.Z - zTolerance || point.Z > box.Max.Z + zTolerance) continue;
                if (point.X < box.Min.X - clearance || point.X > box.Max.X + clearance
                    || point.Y < box.Min.Y - clearance || point.Y > box.Max.Y + clearance)
                    continue;
                if (loops == null)
                    loops = room.GetBoundarySegments(new SpatialElementBoundaryOptions());
                XYZ doorCenter = new XYZ(
                    (box.Min.X + box.Max.X) / 2.0,
                    (box.Min.Y + box.Max.Y) / 2.0,
                    room.Level.Elevation);
                if (BoundaryDistanceMm(doorCenter, loops) <= 750.0) return true;
            }
            return false;
        }

        private static bool BlockedAny(
            XYZ point, List<Obstacle> obstacles, IEnumerable<string> kinds, double clearanceMm)
        {
            foreach (string kind in kinds)
                if (Blocked(point, obstacles, kind, clearanceMm)) return true;
            return false;
        }

        private static List<ExistingOutlet> CollectExistingOutlets(
            Document document, List<SchoolElectricalReviewV3Room> rooms)
        {
            Dictionary<long, string> levels = rooms.GroupBy(item => item.LevelId)
                .ToDictionary(group => group.Key, group => group.First().LevelKey);
            List<ExistingOutlet> result = new List<ExistingOutlet>();
            foreach (FamilyInstance instance in new FilteredElementCollector(document)
                .OfCategory(BuiltInCategory.OST_ElectricalFixtures)
                .WhereElementIsNotElementType().OfType<FamilyInstance>())
            {
                LocationPoint location = instance.Location as LocationPoint;
                if (location == null) continue;
                string levelKey;
                if (!levels.TryGetValue(instance.LevelId.Value, out levelKey)) continue;
                string identity = ((instance.Symbol != null ? instance.Symbol.FamilyName : string.Empty)
                    + " " + instance.Name).ToUpperInvariant();
                result.Add(new ExistingOutlet
                {
                    Point = location.Point,
                    LevelKey = levelKey,
                    IsGfi = identity.Contains("GFI") || identity.Contains("GFCI")
                });
            }
            return result;
        }

        private static Dictionary<string, GraphicsStyle> CreateLightingStyles(Document document)
        {
            return new Dictionary<string, GraphicsStyle>(StringComparer.Ordinal)
            {
                { "L1", EnsureLineStyle(document, "AI-V3-LIGHT-L1-CLASSROOM", new Color(0, 110, 220), 6) },
                { "L2", EnsureLineStyle(document, "AI-V3-LIGHT-L2-CORRIDOR", new Color(0, 150, 80), 6) },
                { "L3", EnsureLineStyle(document, "AI-V3-LIGHT-L3-TOILET", new Color(190, 0, 160), 6) },
                { "L4", EnsureLineStyle(document, "AI-V3-LIGHT-L4-HIGHBAY", new Color(240, 120, 0), 7) },
                { "L5", EnsureLineStyle(document, "AI-V3-LIGHT-L5-WATERPROOF", new Color(190, 40, 40), 6) },
                { "L6", EnsureLineStyle(document, "AI-V3-LIGHT-L6-PLANT", new Color(100, 60, 180), 6) }
            };
        }

        private static int DrawLightingSymbol(
            Document document, View view, XYZ point, double elevation, string type, GraphicsStyle style)
        {
            XYZ center = new XYZ(point.X, point.Y, elevation);
            if (type == "L1") return DrawRectangleWithCenter(document, view, center, 300, 300, style, true);
            if (type == "L2") return DrawRectangleWithCenter(document, view, center, 600, 110, style, false);
            if (type == "L3") return DrawDiamond(document, view, center, 190, style);
            if (type == "L4") return DrawOctagon(document, view, center, 330, style, true);
            if (type == "L5") return DrawRectangleWithCenter(document, view, center, 600, 140, style, true);
            if (type == "L6") return DrawRectangleWithCenter(document, view, center, 300, 180, style, true);
            throw new InvalidDataException("Unsupported V3 lighting symbol type.");
        }

        private static int DrawRectangleWithCenter(
            Document document, View view, XYZ center, double halfXmm, double halfYmm,
            GraphicsStyle style, bool centerLine)
        {
            double x = Feet(halfXmm); double y = Feet(halfYmm);
            XYZ a = center + new XYZ(-x, -y, 0); XYZ b = center + new XYZ(x, -y, 0);
            XYZ c = center + new XYZ(x, y, 0); XYZ d = center + new XYZ(-x, y, 0);
            DrawLine(document, view, a, b, style); DrawLine(document, view, b, c, style);
            DrawLine(document, view, c, d, style); DrawLine(document, view, d, a, style);
            int count = 4;
            if (centerLine) { DrawLine(document, view, center + new XYZ(-x, 0, 0), center + new XYZ(x, 0, 0), style); count++; }
            return count;
        }

        private static int DrawDiamond(Document document, View view, XYZ center, double radiusMm, GraphicsStyle style)
        {
            double r = Feet(radiusMm);
            XYZ top = center + new XYZ(0, r, 0); XYZ right = center + new XYZ(r, 0, 0);
            XYZ bottom = center + new XYZ(0, -r, 0); XYZ left = center + new XYZ(-r, 0, 0);
            DrawLine(document, view, top, right, style); DrawLine(document, view, right, bottom, style);
            DrawLine(document, view, bottom, left, style); DrawLine(document, view, left, top, style);
            DrawLine(document, view, left, right, style);
            return 5;
        }

        private static int DrawOctagon(
            Document document, View view, XYZ center, double radiusMm, GraphicsStyle style, bool cross)
        {
            double r = Feet(radiusMm);
            List<XYZ> points = new List<XYZ>();
            for (int index = 0; index < 8; index++)
            {
                double angle = Math.PI * 2.0 * index / 8.0;
                points.Add(center + new XYZ(Math.Cos(angle) * r, Math.Sin(angle) * r, 0));
            }
            for (int index = 0; index < 8; index++)
                DrawLine(document, view, points[index], points[(index + 1) % 8], style);
            int count = 8;
            if (cross)
            {
                DrawLine(document, view, center + new XYZ(-r, 0, 0), center + new XYZ(r, 0, 0), style);
                DrawLine(document, view, center + new XYZ(0, -r, 0), center + new XYZ(0, r, 0), style);
                count += 2;
            }
            return count;
        }

        private static int DrawDuplex(Document document, View view, XYZ point, double elevation, GraphicsStyle style)
        {
            XYZ center = new XYZ(point.X, point.Y, elevation);
            int count = DrawOctagon(document, view, center, 130, style, false);
            double x = Feet(45.0); double y = Feet(65.0);
            DrawLine(document, view, center + new XYZ(-x, -y, 0), center + new XYZ(-x, y, 0), style);
            DrawLine(document, view, center + new XYZ(x, -y, 0), center + new XYZ(x, y, 0), style);
            return count + 2;
        }

        private static int DrawGfi(Document document, View view, XYZ point, double elevation, GraphicsStyle style)
        {
            XYZ center = new XYZ(point.X, point.Y, elevation);
            int count = DrawRectangleWithCenter(document, view, center, 140, 140, style, false);
            double r = Feet(110.0);
            DrawLine(document, view, center + new XYZ(-r, -r, 0), center + new XYZ(r, r, 0), style);
            DrawLine(document, view, center + new XYZ(-r, r, 0), center + new XYZ(r, -r, 0), style);
            return count + 2;
        }

        private static int DrawDedicated(Document document, View view, XYZ point, double elevation, GraphicsStyle style)
        {
            XYZ center = new XYZ(point.X, point.Y, elevation);
            double r = Feet(170.0);
            XYZ top = center + new XYZ(0, r, 0);
            XYZ left = center + new XYZ(-r, -r, 0);
            XYZ right = center + new XYZ(r, -r, 0);
            DrawLine(document, view, top, left, style); DrawLine(document, view, left, right, style);
            DrawLine(document, view, right, top, style); DrawLine(document, view, top, center, style);
            return 4;
        }

        private static int DrawPanel(Document document, View view, XYZ point, double elevation, GraphicsStyle style)
        {
            XYZ center = new XYZ(point.X, point.Y, elevation);
            double x = Feet(450.0); double y = Feet(300.0);
            XYZ a = center + new XYZ(-x, -y, 0); XYZ b = center + new XYZ(x, -y, 0);
            XYZ c = center + new XYZ(x, y, 0); XYZ d = center + new XYZ(-x, y, 0);
            DrawLine(document, view, a, b, style); DrawLine(document, view, b, c, style);
            DrawLine(document, view, c, d, style); DrawLine(document, view, d, a, style);
            DrawLine(document, view, a, c, style); DrawLine(document, view, b, d, style);
            return 6;
        }

        private static void DrawLine(Document document, View view, XYZ start, XYZ end, GraphicsStyle style)
        {
            DetailCurve curve = document.Create.NewDetailCurve(view, Line.CreateBound(start, end));
            curve.LineStyle = style;
        }

        private static void AddLightingLegend(
            Document document, View view, string levelKey, List<SchoolElectricalReviewV3Room> rooms,
            int reviewVersion)
        {
            List<SchoolElectricalReviewV3Room> levelRooms = rooms.Where(item => item.LevelKey == levelKey).ToList();
            string detail = string.Join(" | ", levelRooms.GroupBy(item => item.LuminaireType)
                .OrderBy(group => group.Key)
                .Select(group => group.Key + "=" + group.Sum(item => item.LightingCount)));
            string note = "AI-ENGINEER ELECTRICAL LIGHTING REVIEW V" + reviewVersion
                + " - NOT FOR CONSTRUCTION\n"
                + detail + "\n"
                + "L1 classrooms/offices; L2 corridors/storage; L3 toilets/service; L4 gym high-bay; "
                + "L5 kitchen/cafeteria waterproof; L6 electrical/mechanical.\n"
                + "Exact room containment and door/stair/shaft clearances checked. Existing lighting families hidden."
                + (reviewVersion == 5
                    ? "\nORANGE=NO SAME-LEVEL CEILING BBOX: MOUNTING REVIEW. RED STAIR MARK=PROPOSAL ONLY; HEIGHT/IES UNVERIFIED."
                    : string.Empty);
            AddText(document, view, LegendLocation(document, levelRooms, view.GenLevel.Elevation), note);
        }

        private static void AddPowerLegend(
            Document document, View view, string levelKey, List<SchoolElectricalReviewV3Room> rooms,
            int existingTotal, int panelTotal, int reviewVersion)
        {
            List<SchoolElectricalReviewV3Room> levelRooms = rooms.Where(item => item.LevelKey == levelKey).ToList();
            string note = "AI-ENGINEER ELECTRICAL POWER REVIEW V" + reviewVersion
                + " - NOT FOR CONSTRUCTION\n"
                + "Blue octagon=NEW DUPLEX " + levelRooms.Sum(item => item.NewDuplexCount)
                + " | Magenta square=NEW GFI " + levelRooms.Sum(item => item.NewGfiCount)
                + " | Red triangle=DEDICATED PLACEHOLDER " + levelRooms.Sum(item => item.DedicatedCount) + "\n"
                + "Green symbols=observed existing outlets (project total " + existingTotal + "). Panel candidates=" + panelTotal + ".\n"
                + "Wall-adjacent review points use room boundaries and door clearance. Equipment identity and final mounting remain held.";
            AddText(document, view, LegendLocation(document, levelRooms, view.GenLevel.Elevation), note);
        }

        private static XYZ LegendLocation(
            Document document, List<SchoolElectricalReviewV3Room> rooms, double elevation)
        {
            double minX = Double.PositiveInfinity;
            double maxY = Double.NegativeInfinity;
            foreach (SchoolElectricalReviewV3Room spec in rooms)
            {
                Room room = document.GetElement(new ElementId(spec.RoomId)) as Room;
                BoundingBoxXYZ box = room == null ? null : room.get_BoundingBox(null);
                if (box == null) continue;
                minX = Math.Min(minX, box.Min.X);
                maxY = Math.Max(maxY, box.Max.Y);
            }
            return new XYZ(minX, maxY + Feet(4500.0), elevation);
        }

        private static void AddText(Document document, View view, XYZ location, string text)
        {
            TextNoteType textType = new FilteredElementCollector(document)
                .OfClass(typeof(TextNoteType)).Cast<TextNoteType>().FirstOrDefault();
            if (textType != null) TextNote.Create(document, view.Id, location, text, textType.Id);
        }

        private static ViewPlan CreateReviewView(Document document, Level level, string viewName)
        {
            if (new FilteredElementCollector(document).OfClass(typeof(View)).Cast<View>()
                .Any(view => string.Equals(view.Name, viewName, StringComparison.Ordinal)))
                throw new InvalidDataException("Electrical V3 review view already exists.");
            ViewPlan source = new FilteredElementCollector(document).OfClass(typeof(ViewPlan)).Cast<ViewPlan>()
                .Where(view => !view.IsTemplate && view.GenLevel != null && view.GenLevel.Id.Value == level.Id.Value)
                .OrderBy(view => view.ViewType == ViewType.FloorPlan ? 0 : 1).FirstOrDefault();
            ViewPlan review;
            if (source != null) review = document.GetElement(source.Duplicate(ViewDuplicateOption.Duplicate)) as ViewPlan;
            else
            {
                ViewFamilyType type = new FilteredElementCollector(document).OfClass(typeof(ViewFamilyType))
                    .Cast<ViewFamilyType>().First(item => item.ViewFamily == ViewFamily.FloorPlan);
                review = ViewPlan.Create(document, type.Id, level.Id);
            }
            review.Name = viewName;
            if (review.ViewTemplateId != ElementId.InvalidElementId)
                review.ViewTemplateId = ElementId.InvalidElementId;
            try { review.CropBoxActive = false; review.CropBoxVisible = false; }
            catch (Autodesk.Revit.Exceptions.InvalidOperationException) { }
            review.DetailLevel = ViewDetailLevel.Fine;
            review.Discipline = ViewDiscipline.Electrical;
            if (review.Discipline != ViewDiscipline.Electrical)
                throw new InvalidOperationException("Revit review view is not in Electrical discipline.");
            return review;
        }

        private static void TryHide(View view, BuiltInCategory category)
        {
            try { view.SetCategoryHidden(new ElementId(category), true); }
            catch (Autodesk.Revit.Exceptions.ArgumentException) { }
        }

        private static bool ContainsXY(BoundingBoxXYZ box, XYZ point)
        {
            return box != null && point.X >= box.Min.X && point.X <= box.Max.X
                && point.Y >= box.Min.Y && point.Y <= box.Max.Y;
        }

        private static GraphicsStyle EnsureLineStyle(Document document, string name, Color color, int weight)
        {
            Category lines = document.Settings.Categories.get_Item(BuiltInCategory.OST_Lines);
            Category subcategory = null;
            foreach (Category candidate in lines.SubCategories)
                if (string.Equals(candidate.Name, name, StringComparison.Ordinal)) { subcategory = candidate; break; }
            if (subcategory == null) subcategory = document.Settings.Categories.NewSubcategory(lines, name);
            subcategory.LineColor = color;
            subcategory.SetLineWeight(weight, GraphicsStyleType.Projection);
            return subcategory.GetGraphicsStyle(GraphicsStyleType.Projection);
        }

        private static bool IsPanelRoom(string roomName)
        {
            return roomName.StartsWith("ELECTRICAL 2500", StringComparison.OrdinalIgnoreCase)
                || roomName.StartsWith("UTILITY 4016", StringComparison.OrdinalIgnoreCase);
        }

        private static XYZ RoomCenter(Room room)
        {
            BoundingBoxXYZ box = room.get_BoundingBox(null);
            return new XYZ((box.Min.X + box.Max.X) / 2.0, (box.Min.Y + box.Max.Y) / 2.0, room.Level.Elevation);
        }

        private static string PointKey(long levelId, XYZ point)
        {
            return levelId + ":" + Math.Round(point.X, 6) + ":" + Math.Round(point.Y, 6);
        }

        private static double PlanDistanceMm(XYZ first, XYZ second)
        {
            double distance = Math.Sqrt(Math.Pow(first.X - second.X, 2) + Math.Pow(first.Y - second.Y, 2));
            return UnitUtils.ConvertFromInternalUnits(distance, UnitTypeId.Millimeters);
        }

        private static double MinimumSpacingMm(List<XYZ> points)
        {
            if (points.Count < 2) return 0.0;
            double minimum = Double.PositiveInfinity;
            for (int first = 0; first < points.Count; first++)
                for (int second = first + 1; second < points.Count; second++)
                    minimum = Math.Min(minimum, PlanDistanceMm(points[first], points[second]));
            return minimum;
        }

        private static string CleanLevelName(string value)
        {
            return value.Replace(" (level review)", " CHECK").ToUpperInvariant();
        }

        private static void Validate(SchoolElectricalReviewV3Preview preview, int reviewVersion)
        {
            DateTime expires;
            Guid previewId;
            if (preview == null || preview.SchemaVersion != 1
                || !Guid.TryParse(preview.PreviewId, out previewId)
                || preview.Action != "create_school_electrical_review_v" + reviewVersion
                || preview.Status != "review_requested"
                || preview.AlgorithmVersion != (reviewVersion == 5
                    ? "balanced-room-rows-actual-area-host-review-v5"
                    : reviewVersion == 4 ? "balanced-room-rows-coverage-electrical-v4"
                    : "exact-room-boundary-door-clearance-electrical-v3")
                || !DateTime.TryParse(preview.ExpiresUtc, null, System.Globalization.DateTimeStyles.RoundtripKind, out expires)
                || expires.ToUniversalTime() <= DateTime.UtcNow
                || preview.LightingBoundaryClearanceMm < 0 || preview.LightingBoundaryClearanceMm > 1000
                || preview.LightingDoorClearanceMm < 300 || preview.LightingDoorClearanceMm > 2000
                || preview.PowerDoorClearanceMm < 300 || preview.PowerDoorClearanceMm > 2000
                || preview.StairShaftClearanceMm < 200 || preview.StairShaftClearanceMm > 2000
                || string.IsNullOrWhiteSpace(preview.SourceSha256) || preview.SourceSha256.Length != 64
                || string.IsNullOrWhiteSpace(preview.SourceScheduleSha256) || preview.SourceScheduleSha256.Length != 64
                || string.IsNullOrWhiteSpace(preview.PreviewSha256) || preview.PreviewSha256.Length != 64
                || preview.ProviderEgressPerformed || preview.SourceDocumentMutationPerformed
                || preview.Rooms == null || preview.Rooms.Count < 1 || preview.Rooms.Count > MaximumRooms
                || preview.Rooms.Sum(item => item.LightingCount) > MaximumLights
                || preview.Rooms.Sum(item => item.NewDuplexCount + item.NewGfiCount + item.DedicatedCount) > MaximumPowerPoints)
                throw new InvalidDataException("School electrical V3 preview is invalid or expired.");
            if (reviewVersion == 5 && (preview.StairWallCandidate == null
                || preview.StairWallCandidate.WallId != 194963
                || Math.Abs(preview.StairWallCandidate.MarkedXmm + 14972.5) > 0.1
                || Math.Abs(preview.StairWallCandidate.MarkedYmm + 5292.4) > 0.1
                || preview.StairWallCandidate.MountingHeightMm.HasValue
                || preview.StairWallCandidate.LuminaireCount.HasValue
                || preview.StairWallCandidate.Status != "needs_stair_geometry_and_photometric_review"))
                throw new InvalidDataException("School V5 stair review candidate changed.");
            HashSet<long> roomIds = new HashSet<long>();
            HashSet<string> types = new HashSet<string>(new[] { "L1", "L2", "L3", "L4", "L5", "L6" }, StringComparer.Ordinal);
            foreach (SchoolElectricalReviewV3Room room in preview.Rooms)
            {
                if (room == null || room.RoomId <= 0 || !roomIds.Add(room.RoomId)
                    || string.IsNullOrWhiteSpace(room.RoomName) || room.RoomName.Length > 120
                    || string.IsNullOrWhiteSpace(room.Use) || room.Use.Length > 80
                    || string.IsNullOrWhiteSpace(room.LevelKey) || room.LevelKey.Length > 80
                    || room.LevelId <= 0 || !types.Contains(room.LuminaireType)
                    || room.LightingCount < 1 || room.LightingCount > 100
                    || room.NewDuplexCount < 0 || room.NewDuplexCount > 20
                    || room.NewGfiCount < 0 || room.NewGfiCount > 20
                    || room.DedicatedCount < 0 || room.DedicatedCount > 20)
                    throw new InvalidDataException("School electrical V3 room schedule is invalid.");
            }
        }

        private static SchoolElectricalReviewV3Preview ReadPreview(string path)
        {
            if (!File.Exists(path) || new FileInfo(path).Length > 400000)
                throw new InvalidDataException("School electrical V3 preview is missing or oversized.");
            DataContractJsonSerializer serializer = new DataContractJsonSerializer(typeof(SchoolElectricalReviewV3Preview));
            using (FileStream stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
                return (SchoolElectricalReviewV3Preview)serializer.ReadObject(stream);
        }

        private static string WriteReport(string requestId, SchoolElectricalReviewV3Report report, int reviewVersion)
        {
            string directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "AI-Engineer", "revit", "school-electrical-review-v" + reviewVersion + "-handoffs");
            Directory.CreateDirectory(directory);
            string path = Path.Combine(directory, requestId + ".json");
            DataContractJsonSerializer serializer = new DataContractJsonSerializer(typeof(SchoolElectricalReviewV3Report));
            using (FileStream stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            { serializer.WriteObject(stream, report); stream.Flush(true); }
            return "school-electrical-review-v" + reviewVersion + "-handoffs/" + requestId + ".json";
        }

        private static double Feet(double millimeters)
        {
            return UnitUtils.ConvertToInternalUnits(millimeters, UnitTypeId.Millimeters);
        }

        private static string Hash(string path)
        {
            using (SHA256 algorithm = SHA256.Create())
            using (FileStream stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                return BitConverter.ToString(algorithm.ComputeHash(stream)).Replace("-", string.Empty);
        }
    }
}
