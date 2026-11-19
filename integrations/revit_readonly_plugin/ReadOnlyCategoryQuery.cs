using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;

using Autodesk.Revit.DB;

namespace AIEngineer.Revit
{
    [DataContract]
    internal sealed class PointReport
    {
        [DataMember(Name = "x_mm", Order = 1)] public double X;
        [DataMember(Name = "y_mm", Order = 2)] public double Y;
        [DataMember(Name = "z_mm", Order = 3)] public double Z;
    }

    [DataContract]
    internal sealed class HostReport
    {
        [DataMember(Name = "kind", Order = 1)] public string Kind;
        [DataMember(Name = "element_id", Order = 2)] public long ElementId;
        [DataMember(Name = "category", Order = 3)] public string Category;
        [DataMember(Name = "name", Order = 4)] public string Name;
        [DataMember(Name = "face_stable_reference", Order = 5)] public string FaceStableReference;
        [DataMember(Name = "is_linked", Order = 6)] public bool IsLinked;
        [DataMember(Name = "linked_element_id", Order = 7)] public long LinkedElementId;
        [DataMember(Name = "linked_document_title", Order = 8)] public string LinkedDocumentTitle;
        [DataMember(Name = "linked_category", Order = 9)] public string LinkedCategory;
        [DataMember(Name = "linked_name", Order = 10)] public string LinkedName;
        [DataMember(Name = "category_key", Order = 11)] public string CategoryKey;
        [DataMember(Name = "linked_category_key", Order = 12)] public string LinkedCategoryKey;
        public long CategoryId;
        public long LinkedCategoryId;
    }

    [DataContract]
    internal sealed class ElementReport
    {
        [DataMember(Name = "element_id", Order = 1)] public long ElementId;
        [DataMember(Name = "category", Order = 2)] public string Category;
        [DataMember(Name = "name", Order = 3)] public string Name;
        [DataMember(Name = "type_name", Order = 4)] public string TypeName;
        [DataMember(Name = "family_name", Order = 5)] public string FamilyName;
        [DataMember(Name = "level_name", Order = 6)] public string LevelName;
        [DataMember(Name = "bbox_min", Order = 7)] public PointReport BoundingBoxMin;
        [DataMember(Name = "bbox_max", Order = 8)] public PointReport BoundingBoxMax;
        [DataMember(Name = "parameters", Order = 9)] public Dictionary<string, string> Parameters;
        [DataMember(Name = "type_id", Order = 10)] public long TypeId;
        [DataMember(Name = "level_id", Order = 11)] public long LevelId;
        [DataMember(Name = "location_point", Order = 12)] public PointReport LocationPoint;
        [DataMember(Name = "level_elevation_mm", Order = 13)] public double? LevelElevationMm;
        [DataMember(Name = "elevation_from_level_mm", Order = 14)] public double? ElevationFromLevelMm;
        [DataMember(Name = "host", Order = 15)] public HostReport Host;
        [DataMember(Name = "curve_length_mm", Order = 16)] public double? CurveLengthMm;
        [DataMember(Name = "diameter_mm", Order = 17)] public double? DiameterMm;
        [DataMember(Name = "width_mm", Order = 18)] public double? WidthMm;
        [DataMember(Name = "height_mm", Order = 19)] public double? HeightMm;
    }

    [DataContract]
    internal sealed class CategoryQueryReport
    {
        [DataMember(Name = "schema_version", Order = 1)] public int SchemaVersion;
        [DataMember(Name = "request_id", Order = 2)] public string RequestId;
        [DataMember(Name = "document_title", Order = 3)] public string DocumentTitle;
        [DataMember(Name = "category_key", Order = 4)] public string CategoryKey;
        [DataMember(Name = "total_count", Order = 5)] public int TotalCount;
        [DataMember(Name = "returned_count", Order = 6)] public int ReturnedCount;
        [DataMember(Name = "truncated", Order = 7)] public bool Truncated;
        [DataMember(Name = "elements", Order = 8)] public List<ElementReport> Elements;
        [DataMember(Name = "provider_egress_performed", Order = 9)] public bool ProviderEgressPerformed;
        [DataMember(Name = "document_mutation_performed", Order = 10)] public bool DocumentMutationPerformed;
        [DataMember(Name = "host_filter", Order = 11)] public string HostFilter;
        [DataMember(Name = "start_after_element_id", Order = 12)] public long StartAfterElementId;
        [DataMember(Name = "next_start_after_element_id", Order = 13)] public long NextStartAfterElementId;
    }

    internal static class ReadOnlyCategoryQuery
    {
        public static string Capture(
            Document document,
            string requestId,
            string categoryKey,
            int maximum,
            string hostFilter,
            long startAfterElementId)
        {
            BuiltInCategory category = ResolveCategory(categoryKey);
            IList<Element> found = new FilteredElementCollector(document)
                .OfCategory(category).WhereElementIsNotElementType().ToElements();
            List<Element> ordered = new List<Element>(found);
            ordered.Sort(delegate(Element left, Element right)
            {
                return left.Id.Value.CompareTo(right.Id.Value);
            });
            List<ElementReport> elements = new List<ElementReport>();
            int matchingCount = 0;
            int remainingCount = 0;
            foreach (Element element in ordered)
            {
                HostReport host = BuildHost(
                    document,
                    element as FamilyInstance,
                    string.Equals(hostFilter, "ceiling_hosted", StringComparison.Ordinal));
                if ((string.Equals(hostFilter, "hosted", StringComparison.Ordinal)
                        || string.Equals(hostFilter, "ceiling_hosted", StringComparison.Ordinal))
                    && host == null)
                {
                    continue;
                }
                if (string.Equals(hostFilter, "ceiling_hosted", StringComparison.Ordinal)
                    && host != null
                    && host.CategoryId != (long)BuiltInCategory.OST_Ceilings
                    && host.LinkedCategoryId != (long)BuiltInCategory.OST_Ceilings)
                {
                    continue;
                }
                matchingCount++;
                if (element.Id.Value <= startAfterElementId) continue;
                remainingCount++;
                if (elements.Count < maximum)
                {
                    elements.Add(BuildElement(document, element, host));
                }
            }
            CategoryQueryReport report = new CategoryQueryReport
            {
                SchemaVersion = 1,
                RequestId = requestId,
                DocumentTitle = Bounded(document.Title),
                CategoryKey = categoryKey,
                TotalCount = matchingCount,
                ReturnedCount = elements.Count,
                Truncated = remainingCount > elements.Count,
                Elements = elements,
                ProviderEgressPerformed = false,
                DocumentMutationPerformed = false,
                HostFilter = hostFilter,
                StartAfterElementId = startAfterElementId,
                NextStartAfterElementId = remainingCount > elements.Count && elements.Count > 0
                    ? elements[elements.Count - 1].ElementId : 0
            };
            string root = Path.Combine(Environment.GetFolderPath(
                Environment.SpecialFolder.LocalApplicationData), "AI-Engineer", "revit", "queries");
            Directory.CreateDirectory(root);
            string fileName = requestId + ".json";
            string destination = Path.Combine(root, fileName);
            DataContractJsonSerializer serializer = new DataContractJsonSerializer(
                typeof(CategoryQueryReport),
                new DataContractJsonSerializerSettings { UseSimpleDictionaryFormat = true });
            using (FileStream stream = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                serializer.WriteObject(stream, report);
                stream.Flush(true);
            }
            return "queries/" + fileName;
        }

        private static ElementReport BuildElement(
            Document document,
            Element element,
            HostReport host)
        {
            ElementType type = document.GetElement(element.GetTypeId()) as ElementType;
            Element level = element.LevelId == ElementId.InvalidElementId ? null : document.GetElement(element.LevelId);
            Level levelValue = level as Level;
            LocationPoint location = element.Location as LocationPoint;
            LocationCurve locationCurve = element.Location as LocationCurve;
            BoundingBoxXYZ box = element.get_BoundingBox(null);
            PointReport minimum = null;
            PointReport maximum = null;
            if (box != null)
            {
                minimum = Point(box.Transform.OfPoint(box.Min));
                maximum = Point(box.Transform.OfPoint(box.Max));
            }
            return new ElementReport
            {
                ElementId = element.Id.Value,
                Category = Bounded(element.Category == null ? string.Empty : element.Category.Name),
                Name = Bounded(element.Name),
                TypeName = Bounded(type == null ? string.Empty : type.Name),
                FamilyName = Bounded(type == null ? string.Empty : type.FamilyName),
                LevelName = Bounded(level == null ? string.Empty : level.Name),
                BoundingBoxMin = minimum,
                BoundingBoxMax = maximum,
                Parameters = ReadParameters(element, type),
                TypeId = type == null ? -1 : type.Id.Value,
                LevelId = level == null ? -1 : level.Id.Value,
                LocationPoint = location == null ? null : Point(location.Point),
                LevelElevationMm = levelValue == null ? (double?)null : Millimeters(levelValue.Elevation),
                ElevationFromLevelMm = ParameterMillimeters(
                    element.get_Parameter(BuiltInParameter.INSTANCE_ELEVATION_PARAM)),
                Host = host,
                CurveLengthMm = locationCurve == null || locationCurve.Curve == null
                    ? (double?)null : Millimeters(locationCurve.Curve.Length),
                DiameterMm = ParameterMillimeters(
                    element.get_Parameter(BuiltInParameter.RBS_CONDUIT_DIAMETER_PARAM)),
                WidthMm = ParameterMillimeters(
                    element.get_Parameter(BuiltInParameter.RBS_CABLETRAY_WIDTH_PARAM)),
                HeightMm = ParameterMillimeters(
                    element.get_Parameter(BuiltInParameter.RBS_CABLETRAY_HEIGHT_PARAM))
            };
        }

        private static HostReport BuildHost(
            Document document, FamilyInstance instance, bool requireResolvedLinkedHost)
        {
            if (instance == null) return null;
            Reference face = instance.HostFace;
            Element directHost = instance.Host;
            if (face == null && directHost == null) return null;

            Element localHost = face == null ? directHost : document.GetElement(face.ElementId);
            ElementId linkedId = face == null ? ElementId.InvalidElementId : face.LinkedElementId;
            long originalLinkedId = linkedId == ElementId.InvalidElementId ? -1 : linkedId.Value;
            RevitLinkInstance link = localHost as RevitLinkInstance;
            Reference referenceInLink = null;
            if (face != null && link != null)
            {
                referenceInLink = face.CreateReferenceInLink();
            }
            Document linkedDocument = link == null ? null : link.GetLinkDocument();
            long convertedElementId = referenceInLink == null
                || referenceInLink.ElementId == ElementId.InvalidElementId
                ? -1 : referenceInLink.ElementId.Value;
            long convertedLinkedId = referenceInLink == null
                || referenceInLink.LinkedElementId == ElementId.InvalidElementId
                ? -1 : referenceInLink.LinkedElementId.Value;
            Element linkedHost = null;
            if (linkedDocument != null && referenceInLink != null)
            {
                linkedId = referenceInLink.ElementId;
                linkedHost = linkedId == ElementId.InvalidElementId
                    ? null : linkedDocument.GetElement(linkedId);
                if (linkedHost == null)
                {
                    linkedId = referenceInLink.LinkedElementId;
                    linkedHost = linkedId == ElementId.InvalidElementId
                        ? null : linkedDocument.GetElement(linkedId);
                }
            }
            if (linkedDocument != null && linkedHost == null && face != null)
            {
                linkedId = face.LinkedElementId;
                linkedHost = linkedId == ElementId.InvalidElementId
                    ? null : linkedDocument.GetElement(linkedId);
            }
            if (link != null && linkedHost == null && requireResolvedLinkedHost)
            {
                throw new InvalidOperationException(
                    "A linked Revit face host could not be resolved through the supported API "
                    + "(link_document_loaded=" + (linkedDocument != null ? "true" : "false")
                    + ", original_linked_id=" + originalLinkedId
                    + ", converted_element_id=" + convertedElementId
                    + ", converted_linked_id=" + convertedLinkedId + ").");
            }
            bool linked = link != null;
            string stableReference = string.Empty;
            if (face != null)
            {
                stableReference = Bounded(face.ConvertToStableRepresentation(document), 4096);
            }
            return new HostReport
            {
                Kind = linked ? "linked_face" : face == null ? "element" : "face",
                ElementId = localHost == null ? -1 : localHost.Id.Value,
                Category = Bounded(localHost == null || localHost.Category == null
                    ? string.Empty : localHost.Category.Name),
                Name = Bounded(localHost == null ? string.Empty : localHost.Name),
                FaceStableReference = stableReference,
                IsLinked = linked,
                LinkedElementId = linkedHost == null ? originalLinkedId : linkedHost.Id.Value,
                LinkedDocumentTitle = Bounded(linkedDocument == null ? string.Empty : linkedDocument.Title),
                LinkedCategory = Bounded(linkedHost == null || linkedHost.Category == null
                    ? string.Empty : linkedHost.Category.Name),
                LinkedName = Bounded(linkedHost == null ? string.Empty : linkedHost.Name),
                CategoryKey = CategoryKey(localHost == null ? null : localHost.Category),
                LinkedCategoryKey = CategoryKey(linkedHost == null ? null : linkedHost.Category),
                CategoryId = localHost == null || localHost.Category == null
                    ? -1 : localHost.Category.Id.Value,
                LinkedCategoryId = linkedHost == null || linkedHost.Category == null
                    ? -1 : linkedHost.Category.Id.Value
            };
        }

        private static string CategoryKey(Category category)
        {
            if (category == null) return string.Empty;
            long value = category.Id.Value;
            if (value == (long)BuiltInCategory.OST_Ceilings) return "ceilings";
            if (value == (long)BuiltInCategory.OST_Floors) return "floors";
            if (value == (long)BuiltInCategory.OST_Walls) return "walls";
            if (value == (long)BuiltInCategory.OST_RvtLinks) return "revit_links";
            return "other";
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

        private static Dictionary<string, string> ReadParameters(Element element, ElementType type)
        {
            Dictionary<string, string> values = new Dictionary<string, string>(StringComparer.Ordinal);
            AddParameter(values, "mark", element, type, BuiltInParameter.ALL_MODEL_MARK);
            AddParameter(values, "type_mark", element, type, BuiltInParameter.ALL_MODEL_TYPE_MARK);
            AddParameter(values, "manufacturer", element, type, BuiltInParameter.ALL_MODEL_MANUFACTURER);
            AddParameter(values, "model", element, type, BuiltInParameter.ALL_MODEL_MODEL);
            AddParameter(values, "voltage", element, type, BuiltInParameter.RBS_ELEC_VOLTAGE);
            AddParameter(values, "wattage", element, type, BuiltInParameter.RBS_ELEC_TRUE_LOAD);
            AddParameter(values, "apparent_load", element, type, BuiltInParameter.RBS_ELEC_APPARENT_LOAD);
            AddParameter(values, "circuit_number", element, type, BuiltInParameter.RBS_ELEC_CIRCUIT_NUMBER);
            AddParameter(values, "panel", element, type, BuiltInParameter.RBS_ELEC_CIRCUIT_PANEL_PARAM);
            return values;
        }

        private static void AddParameter(
            Dictionary<string, string> values,
            string key,
            Element element,
            ElementType type,
            BuiltInParameter builtIn)
        {
            Parameter parameter = element.get_Parameter(builtIn);
            if (parameter == null && type != null) parameter = type.get_Parameter(builtIn);
            if (parameter == null || !parameter.HasValue) return;
            string value = parameter.AsValueString();
            if (string.IsNullOrWhiteSpace(value) && parameter.StorageType == StorageType.String)
            {
                value = parameter.AsString();
            }
            if (!string.IsNullOrWhiteSpace(value)) values[key] = Bounded(value);
        }

        private static PointReport Point(XYZ point)
        {
            return new PointReport
            {
                X = Math.Round(UnitUtils.ConvertFromInternalUnits(point.X, UnitTypeId.Millimeters), 3),
                Y = Math.Round(UnitUtils.ConvertFromInternalUnits(point.Y, UnitTypeId.Millimeters), 3),
                Z = Math.Round(UnitUtils.ConvertFromInternalUnits(point.Z, UnitTypeId.Millimeters), 3)
            };
        }

        private static BuiltInCategory ResolveCategory(string key)
        {
            switch (key)
            {
                case "walls": return BuiltInCategory.OST_Walls;
                case "floors": return BuiltInCategory.OST_Floors;
                case "ceilings": return BuiltInCategory.OST_Ceilings;
                case "doors": return BuiltInCategory.OST_Doors;
                case "windows": return BuiltInCategory.OST_Windows;
                case "rooms": return BuiltInCategory.OST_Rooms;
                case "mep_spaces": return BuiltInCategory.OST_MEPSpaces;
                case "lighting_fixtures": return BuiltInCategory.OST_LightingFixtures;
                case "lighting_devices": return BuiltInCategory.OST_LightingDevices;
                case "electrical_fixtures": return BuiltInCategory.OST_ElectricalFixtures;
                case "electrical_equipment": return BuiltInCategory.OST_ElectricalEquipment;
                case "conduit": return BuiltInCategory.OST_Conduit;
                case "cable_tray": return BuiltInCategory.OST_CableTray;
                case "conduit_fittings": return BuiltInCategory.OST_ConduitFitting;
                case "cable_tray_fittings": return BuiltInCategory.OST_CableTrayFitting;
                default: throw new InvalidDataException("Category is not allowlisted for Revit reading.");
            }
        }

        private static string Bounded(string value)
        {
            return Bounded(value, 260);
        }

        private static string Bounded(string value, int maximum)
        {
            if (string.IsNullOrEmpty(value)) return string.Empty;
            return value.Length <= maximum ? value : value.Substring(0, maximum);
        }
    }
}
