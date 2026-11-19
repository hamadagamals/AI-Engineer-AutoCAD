using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Security.Cryptography;

using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace AIEngineer.Revit
{
    [DataContract]
    internal sealed class SchoolDistributionReviewLevel
    {
        [DataMember(Name = "level_key", Order = 1)] public string LevelKey;
        [DataMember(Name = "revit_level_id", Order = 2)] public long RevitLevelId;
        [DataMember(Name = "review_view_name", Order = 3)] public string ReviewViewName;
    }

    [DataContract]
    internal sealed class SchoolDistributionReviewPoint
    {
        [DataMember(Name = "id", Order = 1)] public string Id;
        [DataMember(Name = "level_key", Order = 2)] public string LevelKey;
        [DataMember(Name = "kind", Order = 3)] public string Kind;
        [DataMember(Name = "x_mm", Order = 4)] public double Xmm;
        [DataMember(Name = "y_mm", Order = 5)] public double Ymm;
        [DataMember(Name = "verified_existing", Order = 6)] public bool VerifiedExisting;
    }

    [DataContract]
    internal sealed class SchoolDistributionReviewPreview
    {
        [DataMember(Name = "schema_version", Order = 1)] public int SchemaVersion;
        [DataMember(Name = "preview_id", Order = 2)] public string PreviewId;
        [DataMember(Name = "action", Order = 3)] public string Action;
        [DataMember(Name = "status", Order = 4)] public string Status;
        [DataMember(Name = "created_utc", Order = 5)] public string CreatedUtc;
        [DataMember(Name = "expires_utc", Order = 6)] public string ExpiresUtc;
        [DataMember(Name = "source_sha256", Order = 7)] public string SourceSha256;
        [DataMember(Name = "source_points_sha256", Order = 8)] public string SourcePointsSha256;
        [DataMember(Name = "levels", Order = 9)] public List<SchoolDistributionReviewLevel> Levels;
        [DataMember(Name = "points", Order = 10)] public List<SchoolDistributionReviewPoint> Points;
        [DataMember(Name = "preview_sha256", Order = 11)] public string PreviewSha256;
        [DataMember(Name = "provider_egress_performed", Order = 12)] public bool ProviderEgressPerformed;
        [DataMember(Name = "source_document_mutation_performed", Order = 13)] public bool SourceDocumentMutationPerformed;
    }

    [DataContract]
    internal sealed class SchoolDistributionReviewViewReport
    {
        [DataMember(Name = "level_key", Order = 1)] public string LevelKey;
        [DataMember(Name = "view_id", Order = 2)] public long ViewId;
        [DataMember(Name = "view_name", Order = 3)] public string ViewName;
        [DataMember(Name = "point_count", Order = 4)] public int PointCount;
        [DataMember(Name = "detail_curve_count", Order = 5)] public int DetailCurveCount;
    }

    [DataContract]
    internal sealed class SchoolDistributionReviewHandoffReport
    {
        [DataMember(Name = "schema_version", Order = 1)] public int SchemaVersion;
        [DataMember(Name = "request_id", Order = 2)] public string RequestId;
        [DataMember(Name = "preview_id", Order = 3)] public string PreviewId;
        [DataMember(Name = "preview_sha256", Order = 4)] public string PreviewSha256;
        [DataMember(Name = "source_sha256", Order = 5)] public string SourceSha256;
        [DataMember(Name = "output_path", Order = 6)] public string OutputPath;
        [DataMember(Name = "output_sha256", Order = 7)] public string OutputSha256;
        [DataMember(Name = "point_count", Order = 8)] public int PointCount;
        [DataMember(Name = "detail_curve_count", Order = 9)] public int DetailCurveCount;
        [DataMember(Name = "views", Order = 10)] public List<SchoolDistributionReviewViewReport> Views;
        [DataMember(Name = "transaction_committed", Order = 11)] public bool TransactionCommitted;
        [DataMember(Name = "saved_as_new_file", Order = 12)] public bool SavedAsNewFile;
        [DataMember(Name = "provider_egress_performed", Order = 13)] public bool ProviderEgressPerformed;
        [DataMember(Name = "document_mutation_performed", Order = 14)] public bool DocumentMutationPerformed;
    }

    internal static class SchoolDistributionReviewHandoff
    {
        private const int MaximumPoints = 1500;
        private const double CoordinateLimitMm = 1000000.0;

        public static string Execute(
            UIApplication uiApplication,
            string requestId,
            string previewPath,
            string outputPath)
        {
            Document document = uiApplication.ActiveUIDocument.Document;
            SchoolDistributionReviewPreview preview = ReadPreview(previewPath);
            Validate(preview);
            string output = Path.GetFullPath(outputPath);
            if (File.Exists(output))
            {
                throw new IOException("School distribution review output already exists.");
            }
            Directory.CreateDirectory(Path.GetDirectoryName(output));

            Dictionary<string, GraphicsStyle> styles = new Dictionary<string, GraphicsStyle>(StringComparer.Ordinal);
            List<SchoolDistributionReviewViewReport> viewReports = new List<SchoolDistributionReviewViewReport>();
            ViewPlan firstView = null;
            int totalCurves = 0;
            using (Transaction transaction = new Transaction(document, "AI-Engineer school distribution review overlay"))
            {
                transaction.Start();
                styles["LIGHT"] = EnsureLineStyle(document, "AI-REVIEW-LIGHT", new Color(255, 190, 0), 4);
                styles["LIGHT_VERIFIED"] = EnsureLineStyle(document, "AI-REVIEW-LIGHT-VERIFIED", new Color(0, 180, 80), 5);
                styles["DUPLEX"] = EnsureLineStyle(document, "AI-REVIEW-DUPLEX", new Color(0, 110, 220), 4);
                styles["DUPLEX_EXISTING"] = EnsureLineStyle(document, "AI-REVIEW-DUPLEX-EXISTING", new Color(0, 175, 225), 5);
                styles["GFI"] = EnsureLineStyle(document, "AI-REVIEW-GFI", new Color(190, 0, 220), 5);
                styles["GFI_EXISTING"] = EnsureLineStyle(document, "AI-REVIEW-GFI-EXISTING", new Color(0, 175, 225), 5);
                styles["DEDICATED"] = EnsureLineStyle(document, "AI-REVIEW-DEDICATED", new Color(230, 70, 40), 5);
                styles["PANEL"] = EnsureLineStyle(document, "AI-REVIEW-PANEL", new Color(220, 0, 0), 7);

                foreach (SchoolDistributionReviewLevel levelSpec in preview.Levels)
                {
                    Level level = document.GetElement(new ElementId(levelSpec.RevitLevelId)) as Level;
                    if (level == null)
                    {
                        transaction.RollBack();
                        throw new InvalidDataException("Review level no longer exists in the Revit document.");
                    }
                    ViewPlan reviewView = CreateReviewView(document, level, levelSpec.ReviewViewName);
                    if (firstView == null)
                    {
                        firstView = reviewView;
                    }
                    List<SchoolDistributionReviewPoint> levelPoints = preview.Points
                        .Where(item => string.Equals(item.LevelKey, levelSpec.LevelKey, StringComparison.Ordinal))
                        .ToList();
                    int viewCurves = 0;
                    foreach (SchoolDistributionReviewPoint point in levelPoints)
                    {
                        string styleKey = StyleKey(point);
                        viewCurves += DrawPoint(document, reviewView, point, level.Elevation, styles[styleKey]);
                    }
                    AddLegend(document, reviewView, levelPoints, level.Elevation);
                    viewReports.Add(new SchoolDistributionReviewViewReport
                    {
                        LevelKey = levelSpec.LevelKey,
                        ViewId = reviewView.Id.Value,
                        ViewName = reviewView.Name,
                        PointCount = levelPoints.Count,
                        DetailCurveCount = viewCurves
                    });
                    totalCurves += viewCurves;
                }
                if (transaction.Commit() != TransactionStatus.Committed)
                {
                    throw new InvalidOperationException("School distribution review transaction did not commit.");
                }
            }

            SaveAsOptions options = new SaveAsOptions();
            options.OverwriteExistingFile = false;
            document.SaveAs(output, options);
            if (!File.Exists(output))
            {
                throw new IOException("Revit did not create the school distribution review output.");
            }
            if (firstView != null)
            {
                uiApplication.ActiveUIDocument.ActiveView = firstView;
            }
            SchoolDistributionReviewHandoffReport report = new SchoolDistributionReviewHandoffReport
            {
                SchemaVersion = 1,
                RequestId = requestId,
                PreviewId = preview.PreviewId,
                PreviewSha256 = preview.PreviewSha256,
                SourceSha256 = preview.SourceSha256,
                OutputPath = output,
                OutputSha256 = Hash(output),
                PointCount = preview.Points.Count,
                DetailCurveCount = totalCurves,
                Views = viewReports,
                TransactionCommitted = true,
                SavedAsNewFile = true,
                ProviderEgressPerformed = false,
                DocumentMutationPerformed = true
            };
            return WriteReport(requestId, report);
        }

        private static ViewPlan CreateReviewView(Document document, Level level, string viewName)
        {
            if (new FilteredElementCollector(document).OfClass(typeof(View))
                .Cast<View>().Any(view => string.Equals(view.Name, viewName, StringComparison.Ordinal)))
            {
                throw new InvalidDataException("A school distribution review view name already exists.");
            }
            ViewPlan source = new FilteredElementCollector(document)
                .OfClass(typeof(ViewPlan)).Cast<ViewPlan>()
                .Where(view => !view.IsTemplate && view.GenLevel != null && view.GenLevel.Id.Value == level.Id.Value)
                .OrderBy(view => view.ViewType == ViewType.FloorPlan ? 0 : 1)
                .FirstOrDefault();
            ViewPlan review;
            if (source != null)
            {
                review = document.GetElement(source.Duplicate(ViewDuplicateOption.Duplicate)) as ViewPlan;
            }
            else
            {
                ViewFamilyType floorPlanType = new FilteredElementCollector(document)
                    .OfClass(typeof(ViewFamilyType)).Cast<ViewFamilyType>()
                    .FirstOrDefault(item => item.ViewFamily == ViewFamily.FloorPlan);
                if (floorPlanType == null)
                {
                    throw new InvalidDataException("No Revit floor-plan view type is available for the review.");
                }
                review = ViewPlan.Create(document, floorPlanType.Id, level.Id);
            }
            if (review == null)
            {
                throw new InvalidOperationException("Revit could not create a school distribution review view.");
            }
            review.Name = viewName;
            try
            {
                review.CropBoxActive = false;
                review.CropBoxVisible = false;
            }
            catch (Autodesk.Revit.Exceptions.InvalidOperationException)
            {
            }
            review.DetailLevel = ViewDetailLevel.Fine;
            return review;
        }

        private static GraphicsStyle EnsureLineStyle(
            Document document, string name, Color color, int lineWeight)
        {
            Category lines = document.Settings.Categories.get_Item(BuiltInCategory.OST_Lines);
            Category subcategory = null;
            foreach (Category candidate in lines.SubCategories)
            {
                if (string.Equals(candidate.Name, name, StringComparison.Ordinal))
                {
                    subcategory = candidate;
                    break;
                }
            }
            if (subcategory == null)
            {
                subcategory = document.Settings.Categories.NewSubcategory(lines, name);
            }
            subcategory.LineColor = color;
            subcategory.SetLineWeight(lineWeight, GraphicsStyleType.Projection);
            return subcategory.GetGraphicsStyle(GraphicsStyleType.Projection);
        }

        private static int DrawPoint(
            Document document,
            View view,
            SchoolDistributionReviewPoint point,
            double elevation,
            GraphicsStyle style)
        {
            XYZ center = new XYZ(
                UnitUtils.ConvertToInternalUnits(point.Xmm, UnitTypeId.Millimeters),
                UnitUtils.ConvertToInternalUnits(point.Ymm, UnitTypeId.Millimeters),
                elevation);
            if (string.Equals(point.Kind, "LIGHT", StringComparison.Ordinal))
            {
                return DrawSquare(document, view, center, 380.0, style, true);
            }
            if (string.Equals(point.Kind, "DUPLEX", StringComparison.Ordinal)
                || string.Equals(point.Kind, "DUPLEX_EXISTING", StringComparison.Ordinal))
            {
                double half = Feet(260.0);
                double tooth = Feet(130.0);
                int count = 0;
                count += DrawLine(document, view, center + new XYZ(-half, 0, 0), center + new XYZ(half, 0, 0), style);
                count += DrawLine(document, view, center + new XYZ(-tooth, -tooth, 0), center + new XYZ(-tooth, tooth, 0), style);
                count += DrawLine(document, view, center + new XYZ(tooth, -tooth, 0), center + new XYZ(tooth, tooth, 0), style);
                return count;
            }
            if (string.Equals(point.Kind, "GFI", StringComparison.Ordinal)
                || string.Equals(point.Kind, "GFI_EXISTING", StringComparison.Ordinal))
            {
                return DrawDiamond(document, view, center, 320.0, style, true);
            }
            if (string.Equals(point.Kind, "DEDICATED", StringComparison.Ordinal))
            {
                return DrawTriangle(document, view, center, 360.0, style);
            }
            return DrawSquare(document, view, center, 900.0, style, true);
        }

        private static int DrawSquare(
            Document document, View view, XYZ center, double halfMillimeters,
            GraphicsStyle style, bool diagonals)
        {
            double half = Feet(halfMillimeters);
            XYZ a = center + new XYZ(-half, -half, 0);
            XYZ b = center + new XYZ(half, -half, 0);
            XYZ c = center + new XYZ(half, half, 0);
            XYZ d = center + new XYZ(-half, half, 0);
            int count = DrawLine(document, view, a, b, style)
                + DrawLine(document, view, b, c, style)
                + DrawLine(document, view, c, d, style)
                + DrawLine(document, view, d, a, style);
            if (diagonals)
            {
                count += DrawLine(document, view, a, c, style);
                count += DrawLine(document, view, b, d, style);
            }
            return count;
        }

        private static int DrawDiamond(
            Document document, View view, XYZ center, double halfMillimeters,
            GraphicsStyle style, bool cross)
        {
            double half = Feet(halfMillimeters);
            XYZ a = center + new XYZ(0, -half, 0);
            XYZ b = center + new XYZ(half, 0, 0);
            XYZ c = center + new XYZ(0, half, 0);
            XYZ d = center + new XYZ(-half, 0, 0);
            int count = DrawLine(document, view, a, b, style)
                + DrawLine(document, view, b, c, style)
                + DrawLine(document, view, c, d, style)
                + DrawLine(document, view, d, a, style);
            if (cross)
            {
                count += DrawLine(document, view, a, c, style);
                count += DrawLine(document, view, b, d, style);
            }
            return count;
        }

        private static int DrawTriangle(
            Document document, View view, XYZ center, double halfMillimeters, GraphicsStyle style)
        {
            double half = Feet(halfMillimeters);
            XYZ a = center + new XYZ(0, half, 0);
            XYZ b = center + new XYZ(half, -half, 0);
            XYZ c = center + new XYZ(-half, -half, 0);
            return DrawLine(document, view, a, b, style)
                + DrawLine(document, view, b, c, style)
                + DrawLine(document, view, c, a, style);
        }

        private static int DrawLine(
            Document document, View view, XYZ start, XYZ end, GraphicsStyle style)
        {
            DetailCurve curve = document.Create.NewDetailCurve(view, Line.CreateBound(start, end));
            curve.LineStyle = style;
            return 1;
        }

        private static void AddLegend(
            Document document,
            View view,
            List<SchoolDistributionReviewPoint> points,
            double elevation)
        {
            if (points.Count == 0)
            {
                return;
            }
            TextNoteType textType = new FilteredElementCollector(document)
                .OfClass(typeof(TextNoteType)).Cast<TextNoteType>().FirstOrDefault();
            if (textType == null)
            {
                return;
            }
            double minimumX = points.Min(item => item.Xmm);
            double maximumY = points.Max(item => item.Ymm);
            XYZ location = new XYZ(
                UnitUtils.ConvertToInternalUnits(minimumX, UnitTypeId.Millimeters),
                UnitUtils.ConvertToInternalUnits(maximumY + 3000.0, UnitTypeId.Millimeters),
                elevation);
            string note = "AI-ENGINEER ELECTRICAL DISTRIBUTION REVIEW - NOT FOR CONSTRUCTION\n"
                + "Yellow square/X: lighting candidate | Green: verified sample | Blue: duplex/existing\n"
                + "Purple diamond: GFI | Orange triangle: dedicated point | Red large square: panel/zone guide\n"
                + "Review-only bounding-box coordinates; circuits, routes, protection and cable sizes are not assigned.";
            TextNote.Create(document, view.Id, location, note, textType.Id);
        }

        private static string StyleKey(SchoolDistributionReviewPoint point)
        {
            if (string.Equals(point.Kind, "LIGHT", StringComparison.Ordinal))
            {
                return point.VerifiedExisting ? "LIGHT_VERIFIED" : "LIGHT";
            }
            if (string.Equals(point.Kind, "DUPLEX_EXISTING", StringComparison.Ordinal))
            {
                return "DUPLEX_EXISTING";
            }
            if (string.Equals(point.Kind, "GFI_EXISTING", StringComparison.Ordinal))
            {
                return "GFI_EXISTING";
            }
            if (string.Equals(point.Kind, "MAIN_PANEL", StringComparison.Ordinal)
                || string.Equals(point.Kind, "ZONE_PANEL_AREA", StringComparison.Ordinal))
            {
                return "PANEL";
            }
            return point.Kind;
        }

        private static void Validate(SchoolDistributionReviewPreview preview)
        {
            DateTime expires;
            Guid previewId;
            if (preview == null || preview.SchemaVersion != 1
                || !Guid.TryParse(preview.PreviewId, out previewId)
                || !string.Equals(preview.Action, "create_school_distribution_review_views", StringComparison.Ordinal)
                || !string.Equals(preview.Status, "review_requested", StringComparison.Ordinal)
                || !DateTime.TryParse(preview.ExpiresUtc, null, System.Globalization.DateTimeStyles.RoundtripKind, out expires)
                || expires.ToUniversalTime() <= DateTime.UtcNow
                || string.IsNullOrWhiteSpace(preview.SourceSha256) || preview.SourceSha256.Length != 64
                || string.IsNullOrWhiteSpace(preview.SourcePointsSha256) || preview.SourcePointsSha256.Length != 64
                || string.IsNullOrWhiteSpace(preview.PreviewSha256) || preview.PreviewSha256.Length != 64
                || preview.ProviderEgressPerformed || preview.SourceDocumentMutationPerformed
                || preview.Levels == null || preview.Levels.Count < 1 || preview.Levels.Count > 4
                || preview.Points == null || preview.Points.Count < 1 || preview.Points.Count > MaximumPoints)
            {
                throw new InvalidDataException("School distribution review preview is invalid or expired.");
            }
            HashSet<string> levels = new HashSet<string>(StringComparer.Ordinal);
            foreach (SchoolDistributionReviewLevel level in preview.Levels)
            {
                if (level == null || string.IsNullOrWhiteSpace(level.LevelKey)
                    || level.LevelKey.Length > 80 || level.RevitLevelId <= 0
                    || string.IsNullOrWhiteSpace(level.ReviewViewName) || level.ReviewViewName.Length > 120
                    || !levels.Add(level.LevelKey))
                {
                    throw new InvalidDataException("School distribution review level binding is invalid.");
                }
            }
            HashSet<string> allowedKinds = new HashSet<string>(new[]
            {
                "LIGHT", "DUPLEX", "DUPLEX_EXISTING", "GFI", "GFI_EXISTING",
                "DEDICATED", "MAIN_PANEL", "ZONE_PANEL_AREA"
            }, StringComparer.Ordinal);
            HashSet<string> pointIds = new HashSet<string>(StringComparer.Ordinal);
            foreach (SchoolDistributionReviewPoint point in preview.Points)
            {
                if (point == null || string.IsNullOrWhiteSpace(point.Id) || point.Id.Length > 80
                    || !pointIds.Add(point.Id) || !levels.Contains(point.LevelKey)
                    || !allowedKinds.Contains(point.Kind)
                    || Double.IsNaN(point.Xmm) || Double.IsInfinity(point.Xmm)
                    || Double.IsNaN(point.Ymm) || Double.IsInfinity(point.Ymm)
                    || Math.Abs(point.Xmm) > CoordinateLimitMm || Math.Abs(point.Ymm) > CoordinateLimitMm)
                {
                    throw new InvalidDataException("School distribution review point is invalid.");
                }
            }
        }

        private static SchoolDistributionReviewPreview ReadPreview(string path)
        {
            if (!File.Exists(path) || new FileInfo(path).Length > 2000000)
            {
                throw new InvalidDataException("School distribution review preview is missing or oversized.");
            }
            DataContractJsonSerializer serializer = new DataContractJsonSerializer(typeof(SchoolDistributionReviewPreview));
            using (FileStream stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                return (SchoolDistributionReviewPreview)serializer.ReadObject(stream);
            }
        }

        private static string WriteReport(string requestId, SchoolDistributionReviewHandoffReport report)
        {
            string directory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "AI-Engineer", "revit", "school-distribution-review-handoffs");
            Directory.CreateDirectory(directory);
            string path = Path.Combine(directory, requestId + ".json");
            DataContractJsonSerializer serializer = new DataContractJsonSerializer(typeof(SchoolDistributionReviewHandoffReport));
            using (FileStream stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                serializer.WriteObject(stream, report);
                stream.Flush(true);
            }
            return "school-distribution-review-handoffs/" + requestId + ".json";
        }

        private static double Feet(double millimeters)
        {
            return UnitUtils.ConvertToInternalUnits(millimeters, UnitTypeId.Millimeters);
        }

        private static string Hash(string path)
        {
            using (SHA256 algorithm = SHA256.Create())
            using (FileStream stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            {
                return BitConverter.ToString(algorithm.ComputeHash(stream)).Replace("-", string.Empty);
            }
        }
    }
}
