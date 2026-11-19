using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;

using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Electrical;

namespace AIEngineer.Revit
{
    [DataContract]
    internal sealed class ElectricalSystemCategoryCount
    {
        [DataMember(Name = "category_key", Order = 1)] public string CategoryKey;
        [DataMember(Name = "count", Order = 2)] public int Count;
    }

    [DataContract]
    internal sealed class ElectricalSystemEntry
    {
        [DataMember(Name = "circuit", Order = 1)] public LightingCircuitReport Circuit;
        [DataMember(Name = "load_classifications", Order = 2)] public string LoadClassifications;
        [DataMember(Name = "member_count", Order = 3)] public int MemberCount;
        [DataMember(Name = "member_category_counts", Order = 4)]
        public List<ElectricalSystemCategoryCount> MemberCategoryCounts;
    }

    [DataContract]
    internal sealed class ElectricalSystemQueryReport
    {
        [DataMember(Name = "schema_version", Order = 1)] public int SchemaVersion;
        [DataMember(Name = "request_id", Order = 2)] public string RequestId;
        [DataMember(Name = "document_title", Order = 3)] public string DocumentTitle;
        [DataMember(Name = "total_system_count", Order = 4)] public int TotalSystemCount;
        [DataMember(Name = "returned_system_count", Order = 5)] public int ReturnedSystemCount;
        [DataMember(Name = "truncated", Order = 6)] public bool Truncated;
        [DataMember(Name = "systems", Order = 7)] public List<ElectricalSystemEntry> Systems;
        [DataMember(Name = "provider_egress_performed", Order = 8)] public bool ProviderEgressPerformed;
        [DataMember(Name = "document_mutation_performed", Order = 9)] public bool DocumentMutationPerformed;
        [DataMember(Name = "start_after_element_id", Order = 10)] public long StartAfterElementId;
        [DataMember(Name = "next_start_after_element_id", Order = 11)] public long NextStartAfterElementId;
    }

    internal static class ReadOnlyElectricalSystemQuery
    {
        public static string Capture(Document document, string requestId, long startAfterElementId, int maximum)
        {
            List<ElectricalSystem> found = new List<ElectricalSystem>();
            foreach (Element element in new FilteredElementCollector(document).OfClass(typeof(ElectricalSystem)))
            {
                ElectricalSystem system = element as ElectricalSystem;
                if (system != null) found.Add(system);
            }
            found.Sort(delegate(ElectricalSystem left, ElectricalSystem right)
            {
                return left.Id.Value.CompareTo(right.Id.Value);
            });

            List<ElectricalSystemEntry> systems = new List<ElectricalSystemEntry>();
            for (int index = 0; index < found.Count && systems.Count < maximum; index++)
            {
                if (found[index].Id.Value <= startAfterElementId) continue;
                systems.Add(Entry(found[index]));
            }
            int remaining = 0;
            foreach (ElectricalSystem system in found)
            {
                if (system.Id.Value > startAfterElementId) remaining++;
            }
            bool truncated = remaining > systems.Count;
            ElectricalSystemQueryReport report = new ElectricalSystemQueryReport
            {
                SchemaVersion = 1,
                RequestId = requestId,
                DocumentTitle = Bounded(document.Title),
                TotalSystemCount = found.Count,
                ReturnedSystemCount = systems.Count,
                Truncated = truncated,
                Systems = systems,
                ProviderEgressPerformed = false,
                DocumentMutationPerformed = false,
                StartAfterElementId = startAfterElementId,
                NextStartAfterElementId = truncated && systems.Count > 0
                    ? systems[systems.Count - 1].Circuit.SystemId : 0
            };
            return Write(requestId, report);
        }

        private static ElectricalSystemEntry Entry(ElectricalSystem system)
        {
            Dictionary<string, int> counts = new Dictionary<string, int>(StringComparer.Ordinal);
            int memberCount = 0;
            ElementSet members = system.Elements;
            if (members != null)
            {
                foreach (Element member in members)
                {
                    if (member == null) continue;
                    memberCount++;
                    string key = CategoryKey(member.Category);
                    int count;
                    counts.TryGetValue(key, out count);
                    counts[key] = count + 1;
                }
            }
            List<ElectricalSystemCategoryCount> categoryCounts = new List<ElectricalSystemCategoryCount>();
            foreach (KeyValuePair<string, int> pair in counts)
            {
                categoryCounts.Add(new ElectricalSystemCategoryCount
                {
                    CategoryKey = pair.Key,
                    Count = pair.Value
                });
            }
            categoryCounts.Sort(delegate(ElectricalSystemCategoryCount left, ElectricalSystemCategoryCount right)
            {
                return string.CompareOrdinal(left.CategoryKey, right.CategoryKey);
            });
            return new ElectricalSystemEntry
            {
                Circuit = CircuitReport(system),
                LoadClassifications = Bounded(system.LoadClassificationAbbreviations),
                MemberCount = memberCount,
                MemberCategoryCounts = categoryCounts
            };
        }

        private static LightingCircuitReport CircuitReport(ElectricalSystem system)
        {
            if (system.SystemType == ElectricalSystemType.PowerCircuit)
                return ReadOnlyLightingCircuitQuery.Circuit(system);
            return new LightingCircuitReport
            {
                SystemId = system.Id.Value,
                SystemType = Bounded(system.SystemType.ToString()),
                CircuitNumber = string.Empty,
                PanelName = string.Empty,
                LoadName = string.Empty,
                PhaseLabel = string.Empty,
                WireSize = string.Empty,
                CircuitNotes = string.Empty,
                ProtectionParameters = new List<ProtectionParameterReport>()
            };
        }

        private static string CategoryKey(Category category)
        {
            if (category == null) return "uncategorized";
            long id = category.Id.Value;
            if (id == (long)BuiltInCategory.OST_LightingFixtures) return "lighting_fixtures";
            if (id == (long)BuiltInCategory.OST_LightingDevices) return "lighting_devices";
            if (id == (long)BuiltInCategory.OST_ElectricalFixtures) return "electrical_fixtures";
            if (id == (long)BuiltInCategory.OST_ElectricalEquipment) return "electrical_equipment";
            if (id == (long)BuiltInCategory.OST_MechanicalEquipment) return "mechanical_equipment";
            if (id == (long)BuiltInCategory.OST_PlumbingFixtures) return "plumbing_fixtures";
            if (id == (long)BuiltInCategory.OST_SpecialityEquipment) return "speciality_equipment";
            return "other";
        }

        private static string Bounded(string value)
        {
            if (string.IsNullOrEmpty(value)) return string.Empty;
            return value.Length <= 260 ? value : value.Substring(0, 260);
        }

        private static string Write(string requestId, ElectricalSystemQueryReport report)
        {
            string root = Path.Combine(Environment.GetFolderPath(
                Environment.SpecialFolder.LocalApplicationData), "AI-Engineer", "revit", "electrical-systems");
            Directory.CreateDirectory(root);
            string fileName = requestId + ".json";
            string destination = Path.Combine(root, fileName);
            DataContractJsonSerializer serializer = new DataContractJsonSerializer(typeof(ElectricalSystemQueryReport));
            using (FileStream stream = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                serializer.WriteObject(stream, report);
                stream.Flush(true);
            }
            return "electrical-systems/" + fileName;
        }
    }
}
