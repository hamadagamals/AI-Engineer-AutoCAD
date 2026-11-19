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
    internal sealed class SchoolLightingReviewV2Room
    {
        [DataMember(Name = "room_id", Order = 1)] public long RoomId;
        [DataMember(Name = "room_name", Order = 2)] public string RoomName;
        [DataMember(Name = "level_key", Order = 3)] public string LevelKey;
        [DataMember(Name = "level_id", Order = 4)] public long LevelId;
        [DataMember(Name = "luminaire_type", Order = 5)] public string LuminaireType;
        [DataMember(Name = "required_count", Order = 6)] public int RequiredCount;
    }

    [DataContract]
    internal sealed class SchoolLightingReviewV2Preview
    {
        [DataMember(Name = "schema_version", Order = 1)] public int SchemaVersion;
        [DataMember(Name = "preview_id", Order = 2)] public string PreviewId;
        [DataMember(Name = "action", Order = 3)] public string Action;
        [DataMember(Name = "status", Order = 4)] public string Status;
        [DataMember(Name = "expires_utc", Order = 5)] public string ExpiresUtc;
        [DataMember(Name = "source_sha256", Order = 6)] public string SourceSha256;
        [DataMember(Name = "source_schedule_sha256", Order = 7)] public string SourceScheduleSha256;
        [DataMember(Name = "algorithm_version", Order = 8)] public string AlgorithmVersion;
        [DataMember(Name = "boundary_clearance_mm", Order = 9)] public double BoundaryClearanceMm;
        [DataMember(Name = "obstacle_clearance_mm", Order = 10)] public double ObstacleClearanceMm;
        [DataMember(Name = "rooms", Order = 11)] public List<SchoolLightingReviewV2Room> Rooms;
        [DataMember(Name = "preview_sha256", Order = 12)] public string PreviewSha256;
        [DataMember(Name = "provider_egress_performed", Order = 13)] public bool ProviderEgressPerformed;
        [DataMember(Name = "source_document_mutation_performed", Order = 14)] public bool SourceDocumentMutationPerformed;
    }

    [DataContract]
    internal sealed class SchoolLightingReviewV2RoomResult
    {
        [DataMember(Name = "room_id", Order = 1)] public long RoomId;
        [DataMember(Name = "room_name", Order = 2)] public string RoomName;
        [DataMember(Name = "level_key", Order = 3)] public string LevelKey;
        [DataMember(Name = "required_count", Order = 4)] public int RequiredCount;
        [DataMember(Name = "placed_count", Order = 5)] public int PlacedCount;
        [DataMember(Name = "minimum_spacing_mm", Order = 6)] public double MinimumSpacingMm;
        [DataMember(Name = "boundary_clearance_used_mm", Order = 7)] public double BoundaryClearanceUsedMm;
    }

    [DataContract]
    internal sealed class SchoolLightingReviewV2Report
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
        [DataMember(Name = "detail_curve_count", Order = 10)] public int DetailCurveCount;
        [DataMember(Name = "exact_duplicate_count", Order = 11)] public int ExactDuplicateCount;
        [DataMember(Name = "outside_room_count", Order = 12)] public int OutsideRoomCount;
        [DataMember(Name = "obstacle_hit_count", Order = 13)] public int ObstacleHitCount;
        [DataMember(Name = "views", Order = 14)] public List<SchoolDistributionReviewViewReport> Views;
        [DataMember(Name = "rooms", Order = 15)] public List<SchoolLightingReviewV2RoomResult> Rooms;
        [DataMember(Name = "transaction_committed", Order = 16)] public bool TransactionCommitted;
        [DataMember(Name = "saved_as_new_file", Order = 17)] public bool SavedAsNewFile;
        [DataMember(Name = "provider_egress_performed", Order = 18)] public bool ProviderEgressPerformed;
        [DataMember(Name = "document_mutation_performed", Order = 19)] public bool DocumentMutationPerformed;
    }

    internal static class SchoolLightingReviewV2Handoff
    {
        private const int MaximumRooms = 128;
        private const int MaximumLights = 1400;

        private sealed class LayoutResult
        {
            public List<XYZ> Points;
            public double ClearanceMm;
        }

        public static string Execute(
            UIApplication uiApplication, string requestId, string previewPath, string outputPath)
        {
            Document document = uiApplication.ActiveUIDocument.Document;
            SchoolLightingReviewV2Preview preview = ReadPreview(previewPath);
            Validate(preview);
            string output = Path.GetFullPath(outputPath);
            if (File.Exists(output)) throw new IOException("School lighting V2 review output already exists.");
            Directory.CreateDirectory(Path.GetDirectoryName(output));

            List<BoundingBoxXYZ> obstacles = CollectObstacles(document);
            Dictionary<string, ViewPlan> reviewViews = new Dictionary<string, ViewPlan>(StringComparer.Ordinal);
            Dictionary<string, int> viewPointCounts = new Dictionary<string, int>(StringComparer.Ordinal);
            Dictionary<string, int> viewCurveCounts = new Dictionary<string, int>(StringComparer.Ordinal);
            List<SchoolLightingReviewV2RoomResult> roomResults = new List<SchoolLightingReviewV2RoomResult>();
            HashSet<string> exactPoints = new HashSet<string>(StringComparer.Ordinal);
            int duplicateCount = 0;
            int outsideRoomCount = 0;
            int obstacleHitCount = 0;
            int totalLights = 0;
            int totalCurves = 0;
            ViewPlan firstView = null;

            using (Transaction transaction = new Transaction(document, "AI-Engineer school lighting review V2"))
            {
                transaction.Start();
                GraphicsStyle lightStyle = EnsureLineStyle(
                    document, "AI-REVIEW-V2-LIGHT", new Color(255, 175, 0), 5);

                foreach (IGrouping<string, SchoolLightingReviewV2Room> group in
                    preview.Rooms.GroupBy(item => item.LevelKey))
                {
                    SchoolLightingReviewV2Room first = group.First();
                    Level level = document.GetElement(new ElementId(first.LevelId)) as Level;
                    if (level == null) throw new InvalidDataException("Lighting V2 level no longer exists.");
                    string viewName = "AI REVIEW V2 - LIGHTING - " + CleanLevelName(group.Key);
                    ViewPlan reviewView = CreateReviewView(document, level, viewName);
                    reviewViews[group.Key] = reviewView;
                    viewPointCounts[group.Key] = 0;
                    viewCurveCounts[group.Key] = 0;
                    if (firstView == null) firstView = reviewView;
                    try { reviewView.SetCategoryHidden(new ElementId(BuiltInCategory.OST_LightingFixtures), true); }
                    catch (Autodesk.Revit.Exceptions.ArgumentException) { }
                }

                foreach (SchoolLightingReviewV2Room spec in preview.Rooms)
                {
                    Room room = document.GetElement(new ElementId(spec.RoomId)) as Room;
                    if (room == null || room.Area <= 0.0 || room.LevelId.Value != spec.LevelId)
                    {
                        transaction.RollBack();
                        throw new InvalidDataException("Lighting V2 room identity or level no longer matches.");
                    }
                    LayoutResult layout = BuildRoomLayout(
                        room, spec.RequiredCount, obstacles,
                        preview.BoundaryClearanceMm, preview.ObstacleClearanceMm);
                    if (layout.Points.Count != spec.RequiredCount)
                    {
                        transaction.RollBack();
                        throw new InvalidDataException("Lighting V2 could not place every required room point.");
                    }
                    ViewPlan view = reviewViews[spec.LevelKey];
                    foreach (XYZ point in layout.Points)
                    {
                        if (!room.IsPointInRoom(new XYZ(point.X, point.Y, room.Level.Elevation + Feet(300.0))))
                            outsideRoomCount++;
                        if (InsideObstacle(point, obstacles, preview.ObstacleClearanceMm)) obstacleHitCount++;
                        string key = spec.LevelId + ":" + Math.Round(point.X, 6) + ":" + Math.Round(point.Y, 6);
                        if (!exactPoints.Add(key)) duplicateCount++;
                        int curves = DrawRectangle(document, view, point, room.Level.Elevation, lightStyle);
                        totalCurves += curves;
                        totalLights++;
                        viewPointCounts[spec.LevelKey] += 1;
                        viewCurveCounts[spec.LevelKey] += curves;
                    }
                    roomResults.Add(new SchoolLightingReviewV2RoomResult
                    {
                        RoomId = spec.RoomId,
                        RoomName = spec.RoomName,
                        LevelKey = spec.LevelKey,
                        RequiredCount = spec.RequiredCount,
                        PlacedCount = layout.Points.Count,
                        MinimumSpacingMm = Math.Round(MinimumSpacingMm(layout.Points), 1),
                        BoundaryClearanceUsedMm = layout.ClearanceMm
                    });
                }
                if (duplicateCount != 0 || outsideRoomCount != 0 || obstacleHitCount != 0
                    || totalLights != preview.Rooms.Sum(item => item.RequiredCount))
                {
                    transaction.RollBack();
                    throw new InvalidDataException("Lighting V2 quality checks rejected the generated layout.");
                }
                foreach (KeyValuePair<string, ViewPlan> pair in reviewViews)
                {
                    AddLegend(document, pair.Value, pair.Key, roomResults, pair.Value.GenLevel.Elevation);
                }
                if (transaction.Commit() != TransactionStatus.Committed)
                    throw new InvalidOperationException("Lighting V2 review transaction did not commit.");
            }

            SaveAsOptions options = new SaveAsOptions();
            options.OverwriteExistingFile = false;
            document.SaveAs(output, options);
            if (!File.Exists(output)) throw new IOException("Revit did not create the lighting V2 output.");
            if (firstView != null) uiApplication.ActiveUIDocument.ActiveView = firstView;

            List<SchoolDistributionReviewViewReport> views = reviewViews.Select(pair =>
                new SchoolDistributionReviewViewReport
                {
                    LevelKey = pair.Key,
                    ViewId = pair.Value.Id.Value,
                    ViewName = pair.Value.Name,
                    PointCount = viewPointCounts[pair.Key],
                    DetailCurveCount = viewCurveCounts[pair.Key]
                }).ToList();
            SchoolLightingReviewV2Report report = new SchoolLightingReviewV2Report
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
                DetailCurveCount = totalCurves,
                ExactDuplicateCount = duplicateCount,
                OutsideRoomCount = outsideRoomCount,
                ObstacleHitCount = obstacleHitCount,
                Views = views,
                Rooms = roomResults,
                TransactionCommitted = true,
                SavedAsNewFile = true,
                ProviderEgressPerformed = false,
                DocumentMutationPerformed = true
            };
            return WriteReport(requestId, report);
        }

        private static LayoutResult BuildRoomLayout(
            Room room, int required, List<BoundingBoxXYZ> obstacles,
            double requestedClearanceMm, double obstacleClearanceMm)
        {
            BoundingBoxXYZ box = room.get_BoundingBox(null);
            if (box == null) throw new InvalidDataException("Lighting V2 room lacks a bounding box.");
            double width = box.Max.X - box.Min.X;
            double height = box.Max.Y - box.Min.Y;
            if (width <= Feet(300.0) || height <= Feet(300.0))
                throw new InvalidDataException("Lighting V2 room bounding box is too small.");

            double[] clearances = new[] { requestedClearanceMm, 300.0, 200.0, 100.0 };
            foreach (double clearanceMm in clearances.Distinct().Where(value => value >= 0.0))
            {
                List<XYZ> best = null;
                double bestScore = Double.PositiveInfinity;
                int maximumRows = Math.Min(Math.Max(required, 8), 30);
                for (int rows = 1; rows <= maximumRows; rows++)
                {
                    int baseColumns = Math.Max(1, (int)Math.Ceiling((double)required / rows));
                    for (int columns = baseColumns; columns <= baseColumns + 8; columns++)
                    {
                        List<XYZ> valid = GridCandidates(
                            room, box, rows, columns, obstacles, clearanceMm, obstacleClearanceMm);
                        if (valid.Count < required) continue;
                        double gridAspect = (double)columns / rows;
                        double roomAspect = width / height;
                        double score = Math.Abs(Math.Log(gridAspect / roomAspect))
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
            throw new InvalidDataException("Lighting V2 room geometry cannot support its required count.");
        }

        private static List<XYZ> GridCandidates(
            Room room, BoundingBoxXYZ box, int rows, int columns,
            List<BoundingBoxXYZ> obstacles, double boundaryClearanceMm, double obstacleClearanceMm)
        {
            List<XYZ> points = new List<XYZ>();
            double dx = (box.Max.X - box.Min.X) / columns;
            double dy = (box.Max.Y - box.Min.Y) / rows;
            double testZ = room.Level.Elevation + Feet(300.0);
            IList<IList<BoundarySegment>> loops = room.GetBoundarySegments(new SpatialElementBoundaryOptions());
            for (int row = 0; row < rows; row++)
            {
                for (int column = 0; column < columns; column++)
                {
                    XYZ point = new XYZ(box.Min.X + (column + 0.5) * dx, box.Min.Y + (row + 0.5) * dy, room.Level.Elevation);
                    XYZ roomPoint = new XYZ(point.X, point.Y, testZ);
                    if (!room.IsPointInRoom(roomPoint)) continue;
                    if (BoundaryDistanceMm(roomPoint, loops) < boundaryClearanceMm) continue;
                    if (InsideObstacle(point, obstacles, obstacleClearanceMm)) continue;
                    points.Add(point);
                }
            }
            return points;
        }

        private static double BoundaryDistanceMm(XYZ point, IList<IList<BoundarySegment>> loops)
        {
            double minimum = Double.PositiveInfinity;
            if (loops == null) return 0.0;
            foreach (IList<BoundarySegment> loop in loops)
                foreach (BoundarySegment segment in loop)
                    minimum = Math.Min(minimum, segment.GetCurve().Distance(point));
            return UnitUtils.ConvertFromInternalUnits(minimum, UnitTypeId.Millimeters);
        }

        private static List<XYZ> EvenlySelect(List<XYZ> points, int count)
        {
            List<XYZ> ordered = points.OrderBy(point => point.Y).ThenBy(point => point.X).ToList();
            if (ordered.Count == count) return ordered;
            List<XYZ> selected = new List<XYZ>();
            if (count == 1)
            {
                selected.Add(ordered[ordered.Count / 2]);
                return selected;
            }
            HashSet<int> indexes = new HashSet<int>();
            for (int index = 0; index < count; index++)
            {
                int selectedIndex = (int)Math.Round(index * (ordered.Count - 1.0) / (count - 1.0));
                while (indexes.Contains(selectedIndex) && selectedIndex + 1 < ordered.Count) selectedIndex++;
                indexes.Add(selectedIndex);
                selected.Add(ordered[selectedIndex]);
            }
            return selected;
        }

        private static List<BoundingBoxXYZ> CollectObstacles(Document document)
        {
            List<BoundingBoxXYZ> result = new List<BoundingBoxXYZ>();
            BuiltInCategory[] categories = new[]
            {
                BuiltInCategory.OST_Stairs,
                BuiltInCategory.OST_StairsRuns,
                BuiltInCategory.OST_StairsLandings,
                BuiltInCategory.OST_ShaftOpening
            };
            foreach (BuiltInCategory category in categories)
            {
                foreach (Element element in new FilteredElementCollector(document)
                    .OfCategory(category).WhereElementIsNotElementType())
                {
                    BoundingBoxXYZ box = element.get_BoundingBox(null);
                    if (box != null) result.Add(box);
                }
            }
            return result;
        }

        private static bool InsideObstacle(XYZ point, List<BoundingBoxXYZ> obstacles, double clearanceMm)
        {
            double clearance = Feet(clearanceMm);
            foreach (BoundingBoxXYZ box in obstacles)
            {
                if (point.X >= box.Min.X - clearance && point.X <= box.Max.X + clearance
                    && point.Y >= box.Min.Y - clearance && point.Y <= box.Max.Y + clearance)
                    return true;
            }
            return false;
        }

        private static double MinimumSpacingMm(List<XYZ> points)
        {
            if (points.Count < 2) return 0.0;
            double minimum = Double.PositiveInfinity;
            for (int first = 0; first < points.Count; first++)
                for (int second = first + 1; second < points.Count; second++)
                    minimum = Math.Min(minimum, points[first].DistanceTo(points[second]));
            return UnitUtils.ConvertFromInternalUnits(minimum, UnitTypeId.Millimeters);
        }

        private static int DrawRectangle(Document document, View view, XYZ point, double elevation, GraphicsStyle style)
        {
            double halfX = Feet(220.0);
            double halfY = Feet(140.0);
            XYZ center = new XYZ(point.X, point.Y, elevation);
            XYZ a = center + new XYZ(-halfX, -halfY, 0);
            XYZ b = center + new XYZ(halfX, -halfY, 0);
            XYZ c = center + new XYZ(halfX, halfY, 0);
            XYZ d = center + new XYZ(-halfX, halfY, 0);
            DrawLine(document, view, a, b, style);
            DrawLine(document, view, b, c, style);
            DrawLine(document, view, c, d, style);
            DrawLine(document, view, d, a, style);
            return 4;
        }

        private static void DrawLine(Document document, View view, XYZ start, XYZ end, GraphicsStyle style)
        {
            DetailCurve curve = document.Create.NewDetailCurve(view, Line.CreateBound(start, end));
            curve.LineStyle = style;
        }

        private static void AddLegend(
            Document document, View view, string levelKey,
            List<SchoolLightingReviewV2RoomResult> rooms, double elevation)
        {
            List<SchoolLightingReviewV2RoomResult> levelRooms = rooms
                .Where(item => string.Equals(item.LevelKey, levelKey, StringComparison.Ordinal)).ToList();
            TextNoteType textType = new FilteredElementCollector(document)
                .OfClass(typeof(TextNoteType)).Cast<TextNoteType>().FirstOrDefault();
            if (textType == null || levelRooms.Count == 0) return;
            Room firstRoom = document.GetElement(new ElementId(levelRooms[0].RoomId)) as Room;
            BoundingBoxXYZ box = firstRoom.get_BoundingBox(null);
            XYZ location = new XYZ(box.Min.X, box.Max.Y + Feet(2500.0), elevation);
            int count = levelRooms.Sum(item => item.PlacedCount);
            string note = "AI-ENGINEER LIGHTING REVIEW V2 - NOT FOR CONSTRUCTION\n"
                + count + " rectangular light candidates; exact room boundaries checked.\n"
                + "Stairs, landings, shafts and boundary clearances excluded. Existing lighting families hidden in this view.";
            TextNote.Create(document, view.Id, location, note, textType.Id);
        }

        private static ViewPlan CreateReviewView(Document document, Level level, string viewName)
        {
            if (new FilteredElementCollector(document).OfClass(typeof(View)).Cast<View>()
                .Any(view => string.Equals(view.Name, viewName, StringComparison.Ordinal)))
                throw new InvalidDataException("Lighting V2 review view already exists.");
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
            try { review.CropBoxActive = false; review.CropBoxVisible = false; }
            catch (Autodesk.Revit.Exceptions.InvalidOperationException) { }
            review.DetailLevel = ViewDetailLevel.Fine;
            return review;
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

        private static string CleanLevelName(string value)
        {
            return value.Replace(" (level review)", " CHECK").ToUpperInvariant();
        }

        private static void Validate(SchoolLightingReviewV2Preview preview)
        {
            DateTime expires;
            Guid previewId;
            if (preview == null || preview.SchemaVersion != 1
                || !Guid.TryParse(preview.PreviewId, out previewId)
                || !string.Equals(preview.Action, "create_school_lighting_review_v2", StringComparison.Ordinal)
                || !string.Equals(preview.Status, "review_requested", StringComparison.Ordinal)
                || !string.Equals(preview.AlgorithmVersion, "room-boundary-grid-v2", StringComparison.Ordinal)
                || !DateTime.TryParse(preview.ExpiresUtc, null, System.Globalization.DateTimeStyles.RoundtripKind, out expires)
                || expires.ToUniversalTime() <= DateTime.UtcNow
                || preview.BoundaryClearanceMm < 0.0 || preview.BoundaryClearanceMm > 1000.0
                || preview.ObstacleClearanceMm < 0.0 || preview.ObstacleClearanceMm > 2000.0
                || string.IsNullOrWhiteSpace(preview.SourceSha256) || preview.SourceSha256.Length != 64
                || string.IsNullOrWhiteSpace(preview.SourceScheduleSha256) || preview.SourceScheduleSha256.Length != 64
                || string.IsNullOrWhiteSpace(preview.PreviewSha256) || preview.PreviewSha256.Length != 64
                || preview.ProviderEgressPerformed || preview.SourceDocumentMutationPerformed
                || preview.Rooms == null || preview.Rooms.Count < 1 || preview.Rooms.Count > MaximumRooms
                || preview.Rooms.Sum(item => item.RequiredCount) > MaximumLights)
                throw new InvalidDataException("School lighting V2 preview is invalid or expired.");
            HashSet<long> roomIds = new HashSet<long>();
            foreach (SchoolLightingReviewV2Room room in preview.Rooms)
            {
                if (room == null || room.RoomId <= 0 || !roomIds.Add(room.RoomId)
                    || string.IsNullOrWhiteSpace(room.RoomName) || room.RoomName.Length > 120
                    || string.IsNullOrWhiteSpace(room.LevelKey) || room.LevelKey.Length > 80
                    || room.LevelId <= 0 || string.IsNullOrWhiteSpace(room.LuminaireType)
                    || room.RequiredCount < 1 || room.RequiredCount > 100)
                    throw new InvalidDataException("School lighting V2 room schedule is invalid.");
            }
        }

        private static SchoolLightingReviewV2Preview ReadPreview(string path)
        {
            if (!File.Exists(path) || new FileInfo(path).Length > 250000)
                throw new InvalidDataException("School lighting V2 preview is missing or oversized.");
            DataContractJsonSerializer serializer = new DataContractJsonSerializer(typeof(SchoolLightingReviewV2Preview));
            using (FileStream stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
                return (SchoolLightingReviewV2Preview)serializer.ReadObject(stream);
        }

        private static string WriteReport(string requestId, SchoolLightingReviewV2Report report)
        {
            string directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "AI-Engineer", "revit", "school-lighting-review-v2-handoffs");
            Directory.CreateDirectory(directory);
            string path = Path.Combine(directory, requestId + ".json");
            DataContractJsonSerializer serializer = new DataContractJsonSerializer(typeof(SchoolLightingReviewV2Report));
            using (FileStream stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            { serializer.WriteObject(stream, report); stream.Flush(true); }
            return "school-lighting-review-v2-handoffs/" + requestId + ".json";
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
