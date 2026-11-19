using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;

using Autodesk.Revit.DB;

namespace AIEngineer.Revit
{
    [DataContract]
    internal sealed class FamilyTypeSummary
    {
        [DataMember(Name = "symbol_id", Order = 1)] public long SymbolId;
        [DataMember(Name = "family_id", Order = 2)] public long FamilyId;
        [DataMember(Name = "category", Order = 3)] public string Category;
        [DataMember(Name = "family_name", Order = 4)] public string FamilyName;
        [DataMember(Name = "type_name", Order = 5)] public string TypeName;
        [DataMember(Name = "family_placement_type", Order = 6)] public string FamilyPlacementType;
        [DataMember(Name = "host_requirement", Order = 7)] public string HostRequirement;
        [DataMember(Name = "is_active", Order = 8)] public bool IsActive;
    }

    [DataContract]
    internal sealed class FamilyTypeQueryReport
    {
        [DataMember(Name = "schema_version", Order = 1)] public int SchemaVersion;
        [DataMember(Name = "request_id", Order = 2)] public string RequestId;
        [DataMember(Name = "document_title", Order = 3)] public string DocumentTitle;
        [DataMember(Name = "category_key", Order = 4)] public string CategoryKey;
        [DataMember(Name = "total_count", Order = 5)] public int TotalCount;
        [DataMember(Name = "returned_count", Order = 6)] public int ReturnedCount;
        [DataMember(Name = "truncated", Order = 7)] public bool Truncated;
        [DataMember(Name = "types", Order = 8)] public List<FamilyTypeSummary> Types;
        [DataMember(Name = "provider_egress_performed", Order = 9)] public bool ProviderEgressPerformed;
        [DataMember(Name = "document_mutation_performed", Order = 10)] public bool DocumentMutationPerformed;
    }

    internal static class ReadOnlyFamilyTypeQuery
    {
        private static readonly Dictionary<string, BuiltInCategory> Categories =
            new Dictionary<string, BuiltInCategory>(StringComparer.Ordinal)
            {
                { "lighting_fixtures", BuiltInCategory.OST_LightingFixtures },
                { "lighting_devices", BuiltInCategory.OST_LightingDevices },
                { "electrical_fixtures", BuiltInCategory.OST_ElectricalFixtures },
                { "electrical_equipment", BuiltInCategory.OST_ElectricalEquipment }
            };

        public static string Capture(
            Document document, string requestId, string categoryKey, int maximumElements)
        {
            BuiltInCategory category;
            if (!Categories.TryGetValue(categoryKey, out category)
                || maximumElements < 1 || maximumElements > 64)
            {
                throw new InvalidDataException("Revit family-type query bounds are invalid.");
            }
            List<FamilySymbol> symbols = new FilteredElementCollector(document)
                .OfCategory(category)
                .OfClass(typeof(FamilySymbol))
                .Cast<FamilySymbol>()
                .OrderBy(item => item.FamilyName, StringComparer.OrdinalIgnoreCase)
                .ThenBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();
            List<FamilyTypeSummary> values = symbols.Take(maximumElements).Select(item =>
            {
                Family family = item.Family;
                FamilyPlacementType placement = family.FamilyPlacementType;
                return new FamilyTypeSummary
                {
                    SymbolId = item.Id.Value,
                    FamilyId = family.Id.Value,
                    Category = item.Category == null ? string.Empty : Bounded(item.Category.Name),
                    FamilyName = Bounded(item.FamilyName),
                    TypeName = Bounded(item.Name),
                    FamilyPlacementType = placement.ToString(),
                    HostRequirement = HostRequirement(placement),
                    IsActive = item.IsActive
                };
            }).ToList();
            FamilyTypeQueryReport report = new FamilyTypeQueryReport
            {
                SchemaVersion = 1,
                RequestId = requestId,
                DocumentTitle = Bounded(document.Title),
                CategoryKey = categoryKey,
                TotalCount = symbols.Count,
                ReturnedCount = values.Count,
                Truncated = symbols.Count > values.Count,
                Types = values,
                ProviderEgressPerformed = false,
                DocumentMutationPerformed = false
            };
            string root = Path.Combine(Environment.GetFolderPath(
                Environment.SpecialFolder.LocalApplicationData), "AI-Engineer", "revit", "family-types");
            Directory.CreateDirectory(root);
            string name = requestId + ".json";
            string path = Path.Combine(root, name);
            DataContractJsonSerializer serializer = new DataContractJsonSerializer(typeof(FamilyTypeQueryReport));
            using (FileStream stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                serializer.WriteObject(stream, report);
                stream.Flush(true);
            }
            return "family-types/" + name;
        }

        private static string HostRequirement(FamilyPlacementType placement)
        {
            switch (placement)
            {
                case FamilyPlacementType.OneLevelBased:
                    return "level";
                case FamilyPlacementType.OneLevelBasedHosted:
                    return "hosted_element_unresolved";
                case FamilyPlacementType.TwoLevelsBased:
                    return "two_levels";
                case FamilyPlacementType.WorkPlaneBased:
                    return "work_plane_or_face";
                case FamilyPlacementType.ViewBased:
                    return "view";
                case FamilyPlacementType.CurveBased:
                    return "curve";
                case FamilyPlacementType.CurveBasedDetail:
                    return "detail_curve";
                default:
                    return "unsupported_or_unresolved";
            }
        }

        private static string Bounded(string value)
        {
            if (string.IsNullOrEmpty(value)) return string.Empty;
            return value.Length <= 260 ? value : value.Substring(0, 260);
        }
    }
}
