using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;

using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace AIEngineer.Revit
{
    [DataContract]
    internal sealed class ViewFilterMatch
    {
        [DataMember(Name = "filter_id", Order = 1)] public long FilterId;
        [DataMember(Name = "filter_name", Order = 2)] public string FilterName;
        [DataMember(Name = "visible", Order = 3)] public bool Visible;
        [DataMember(Name = "projection_line_color", Order = 4)] public List<int> ProjectionLineColor;
    }

    [DataContract]
    internal sealed class ElementVisualQueryReport
    {
        [DataMember(Name = "schema_version", Order = 1)] public int SchemaVersion;
        [DataMember(Name = "request_id", Order = 2)] public string RequestId;
        [DataMember(Name = "document_title", Order = 3)] public string DocumentTitle;
        [DataMember(Name = "element_id", Order = 4)] public long ElementId;
        [DataMember(Name = "category_key", Order = 5)] public string CategoryKey;
        [DataMember(Name = "family_name", Order = 6)] public string FamilyName;
        [DataMember(Name = "type_name", Order = 7)] public string TypeName;
        [DataMember(Name = "active_view_id", Order = 8)] public long ActiveViewId;
        [DataMember(Name = "active_view_name", Order = 9)] public string ActiveViewName;
        [DataMember(Name = "active_view_type", Order = 10)] public string ActiveViewType;
        [DataMember(Name = "temporary_hide_isolate_was_active", Order = 11)] public bool TemporaryHideIsolateWasActive;
        [DataMember(Name = "visible_in_view", Order = 12)] public bool VisibleInView;
        [DataMember(Name = "model_bounding_box_mm", Order = 13)] public List<double> ModelBoundingBox;
        [DataMember(Name = "view_bounding_box_mm", Order = 14)] public List<double> ViewBoundingBox;
        [DataMember(Name = "image_file", Order = 15)] public string ImageFile;
        [DataMember(Name = "facing_orientation", Order = 16)] public List<double> FacingOrientation;
        [DataMember(Name = "hand_orientation", Order = 17)] public List<double> HandOrientation;
        [DataMember(Name = "mirrored", Order = 18)] public bool Mirrored;
        [DataMember(Name = "facing_flipped", Order = 19)] public bool FacingFlipped;
        [DataMember(Name = "hand_flipped", Order = 20)] public bool HandFlipped;
        [DataMember(Name = "hidden_in_view", Order = 21)] public bool HiddenInView;
        [DataMember(Name = "category_hidden_in_view", Order = 22)] public bool CategoryHiddenInView;
        [DataMember(Name = "visible_geometry_objects", Order = 23)] public int VisibleGeometryObjects;
        [DataMember(Name = "location_point_mm", Order = 24)] public List<double> LocationPoint;
        [DataMember(Name = "elevation_from_level_mm", Order = 25)] public double? ElevationFromLevel;
        [DataMember(Name = "matching_view_filters", Order = 26)] public List<ViewFilterMatch> MatchingViewFilters;
        [DataMember(Name = "element_projection_line_color", Order = 27)] public List<int> ElementProjectionLineColor;
        [DataMember(Name = "provider_egress_performed", Order = 28)] public bool ProviderEgressPerformed;
        [DataMember(Name = "document_mutation_performed", Order = 29)] public bool DocumentMutationPerformed;
    }

    internal static class ReadOnlyElementVisualQuery
    {
        public static string CaptureAndFocus(UIApplication application, string requestId, long elementId)
        {
            UIDocument uiDocument = application.ActiveUIDocument;
            Document document = uiDocument.Document;
            FamilyInstance instance = document.GetElement(new ElementId(elementId)) as FamilyInstance;
            if (instance == null || instance.Category == null
                || instance.Category.Id.Value != (long)BuiltInCategory.OST_LightingFixtures
                || instance.Symbol == null || instance.Symbol.Family == null)
            {
                throw new InvalidDataException("The exact Revit element is not a lighting fixture instance.");
            }
            View view = FindLightingView(document, instance) ?? document.ActiveView;
            if (view == null || view.IsTemplate)
            {
                throw new InvalidDataException("No usable Revit view can display the exact lighting fixture.");
            }
            if (uiDocument.ActiveView.Id.Value != view.Id.Value)
            {
                uiDocument.ActiveView = view;
            }
            bool temporary = view.IsTemporaryHideIsolateActive();
            if (temporary)
            {
                view.DisableTemporaryViewMode(TemporaryViewMode.TemporaryHideIsolate);
            }
            uiDocument.Selection.SetElementIds(new List<ElementId> { instance.Id });
            uiDocument.ShowElements(instance.Id);
            BoundingBoxXYZ modelBox = instance.get_BoundingBox(null);
            BoundingBoxXYZ viewBox = instance.get_BoundingBox(view);
            ZoomToElement(uiDocument, view, viewBox ?? modelBox);
            uiDocument.RefreshActiveView();
            LocationPoint location = instance.Location as LocationPoint;
            ElementVisualQueryReport report = new ElementVisualQueryReport
            {
                SchemaVersion = 1,
                RequestId = requestId,
                DocumentTitle = Bounded(document.Title),
                ElementId = instance.Id.Value,
                CategoryKey = "lighting_fixtures",
                FamilyName = Bounded(instance.Symbol.Family.Name),
                TypeName = Bounded(instance.Symbol.Name),
                ActiveViewId = view.Id.Value,
                ActiveViewName = Bounded(view.Name),
                ActiveViewType = view.ViewType.ToString(),
                TemporaryHideIsolateWasActive = temporary,
                VisibleInView = viewBox != null,
                ModelBoundingBox = Box(modelBox),
                ViewBoundingBox = Box(viewBox),
                ImageFile = ExportVisibleRegion(document, requestId),
                FacingOrientation = Vector(instance.FacingOrientation),
                HandOrientation = Vector(instance.HandOrientation),
                Mirrored = instance.Mirrored,
                FacingFlipped = instance.FacingFlipped,
                HandFlipped = instance.HandFlipped,
                HiddenInView = instance.IsHidden(view),
                CategoryHiddenInView = view.GetCategoryHidden(instance.Category.Id),
                VisibleGeometryObjects = CountVisibleGeometry(instance, view),
                LocationPoint = location == null ? new List<double>() : Point(location.Point),
                ElevationFromLevel = ParameterMillimeters(
                    instance.get_Parameter(BuiltInParameter.INSTANCE_ELEVATION_PARAM)),
                MatchingViewFilters = MatchingFilters(document, view, instance),
                ElementProjectionLineColor = ColorValue(
                    view.GetElementOverrides(instance.Id).ProjectionLineColor),
                ProviderEgressPerformed = false,
                DocumentMutationPerformed = false
            };
            return Write(requestId, report);
        }

        private static List<ViewFilterMatch> MatchingFilters(
            Document document, View view, Element element)
        {
            List<ViewFilterMatch> matches = new List<ViewFilterMatch>();
            foreach (ElementId filterId in view.GetFilters())
            {
                Element filter = document.GetElement(filterId);
                bool matched = false;
                ParameterFilterElement parameterFilter = filter as ParameterFilterElement;
                SelectionFilterElement selectionFilter = filter as SelectionFilterElement;
                if (parameterFilter != null)
                {
                    ElementFilter rule = parameterFilter.GetElementFilter();
                    matched = element.Category != null
                        && parameterFilter.GetCategories().Contains(element.Category.Id)
                        && rule != null
                        && rule.PassesFilter(document, element.Id);
                }
                else if (selectionFilter != null)
                {
                    matched = selectionFilter.GetElementIds().Contains(element.Id);
                }
                if (!matched) continue;
                OverrideGraphicSettings overrides = view.GetFilterOverrides(filterId);
                matches.Add(new ViewFilterMatch
                {
                    FilterId = filterId.Value,
                    FilterName = Bounded(filter == null ? string.Empty : filter.Name),
                    Visible = view.GetFilterVisibility(filterId),
                    ProjectionLineColor = ColorValue(overrides.ProjectionLineColor)
                });
                if (matches.Count >= 32) break;
            }
            return matches;
        }

        private static List<int> ColorValue(Color color)
        {
            if (color == null || !color.IsValid) return new List<int>();
            return new List<int> { color.Red, color.Green, color.Blue };
        }

        private static void ZoomToElement(UIDocument uiDocument, View view, BoundingBoxXYZ box)
        {
            if (box == null) return;
            double margin = UnitUtils.ConvertToInternalUnits(600.0, UnitTypeId.Millimeters);
            XYZ lowerLeft = new XYZ(box.Min.X - margin, box.Min.Y - margin, box.Min.Z);
            XYZ upperRight = new XYZ(box.Max.X + margin, box.Max.Y + margin, box.Max.Z);
            foreach (UIView uiView in uiDocument.GetOpenUIViews())
            {
                if (uiView.ViewId.Value == view.Id.Value)
                {
                    uiView.ZoomAndCenterRectangle(lowerLeft, upperRight);
                    return;
                }
            }
        }

        private static List<double> Vector(XYZ value)
        {
            return new List<double>
            {
                Math.Round(value.X, 6), Math.Round(value.Y, 6), Math.Round(value.Z, 6)
            };
        }

        private static int CountVisibleGeometry(Element element, View view)
        {
            Options options = new Options
            {
                View = view,
                IncludeNonVisibleObjects = false,
                ComputeReferences = false
            };
            GeometryElement geometry = element.get_Geometry(options);
            return CountGeometry(geometry, 0);
        }

        private static int CountGeometry(GeometryElement geometry, int depth)
        {
            if (geometry == null || depth > 4) return 0;
            int count = 0;
            foreach (GeometryObject item in geometry)
            {
                GeometryInstance instance = item as GeometryInstance;
                if (instance != null)
                {
                    count += CountGeometry(instance.GetInstanceGeometry(), depth + 1);
                }
                else
                {
                    Solid solid = item as Solid;
                    if (solid == null || solid.Faces.Size > 0 || solid.Edges.Size > 0) count++;
                }
            }
            return count;
        }

        private static string ExportVisibleRegion(Document document, string requestId)
        {
            string root = Path.Combine(Environment.GetFolderPath(
                Environment.SpecialFolder.LocalApplicationData), "AI-Engineer", "revit", "visual-exports");
            Directory.CreateDirectory(root);
            string prefix = Path.Combine(root, requestId);
            ExportOnce(document, prefix);
            string[] files = Directory.GetFiles(root, requestId + "*.png");
            if (files.Length != 1)
            {
                throw new IOException("Revit did not export exactly one visual evidence image.");
            }
            return "visual-exports/" + Path.GetFileName(files[0]);
        }

        private static void ExportOnce(Document document, string prefix)
        {
            ImageExportOptions options = new ImageExportOptions
            {
                ExportRange = ExportRange.VisibleRegionOfCurrentView,
                FilePath = prefix,
                HLRandWFViewsFileType = ImageFileType.PNG,
                ShadowViewsFileType = ImageFileType.PNG,
                ImageResolution = ImageResolution.DPI_150,
                ZoomType = ZoomFitType.FitToPage,
                PixelSize = 1600,
                FitDirection = FitDirectionType.Horizontal
            };
            document.ExportImage(options);
        }

        private static View FindLightingView(Document document, Element element)
        {
            View fallback = null;
            foreach (Element candidate in new FilteredElementCollector(document).OfClass(typeof(View)))
            {
                View view = candidate as View;
                if (view == null || view.IsTemplate || !view.CanBePrinted) continue;
                BoundingBoxXYZ box;
                try { box = element.get_BoundingBox(view); }
                catch (Autodesk.Revit.Exceptions.ArgumentException) { continue; }
                if (box == null) continue;
                if (string.Equals(view.Name, "L2 - Lighting", StringComparison.Ordinal)) return view;
                if (fallback == null && (view.ViewType == ViewType.CeilingPlan
                    || view.ViewType == ViewType.FloorPlan)) fallback = view;
            }
            return fallback;
        }

        private static List<double> Box(BoundingBoxXYZ value)
        {
            if (value == null) return new List<double>();
            return new List<double>
            {
                Millimeters(value.Min.X), Millimeters(value.Min.Y), Millimeters(value.Min.Z),
                Millimeters(value.Max.X), Millimeters(value.Max.Y), Millimeters(value.Max.Z)
            };
        }

        private static List<double> Point(XYZ value)
        {
            return new List<double>
            {
                Millimeters(value.X), Millimeters(value.Y), Millimeters(value.Z)
            };
        }

        private static double? ParameterMillimeters(Parameter parameter)
        {
            if (parameter == null || !parameter.HasValue || parameter.StorageType != StorageType.Double)
            {
                return null;
            }
            return Millimeters(parameter.AsDouble());
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

        private static string Write(string requestId, ElementVisualQueryReport report)
        {
            string root = Path.Combine(Environment.GetFolderPath(
                Environment.SpecialFolder.LocalApplicationData), "AI-Engineer", "revit", "visual-queries");
            Directory.CreateDirectory(root);
            string fileName = requestId + ".json";
            string destination = Path.Combine(root, fileName);
            DataContractJsonSerializer serializer = new DataContractJsonSerializer(typeof(ElementVisualQueryReport));
            using (FileStream stream = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                serializer.WriteObject(stream, report);
                stream.Flush(true);
            }
            return "visual-queries/" + fileName;
        }
    }
}
