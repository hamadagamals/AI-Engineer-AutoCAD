using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Security.Cryptography;
using System.Text.RegularExpressions;

using Autodesk.Revit.ApplicationServices;
using Autodesk.Revit.DB;

namespace AIEngineer.Revit
{
    [DataContract]
    internal sealed class FamilyFileConnectorSummary
    {
        [DataMember(Name = "domain", Order = 1)] public string Domain;
        [DataMember(Name = "system_classification", Order = 2)] public string SystemClassification;
        [DataMember(Name = "shape", Order = 3)] public string Shape;
        [DataMember(Name = "is_primary", Order = 4)] public bool IsPrimary;
    }

    [DataContract]
    internal sealed class FamilyFileParameterSummary
    {
        [DataMember(Name = "name", Order = 1)] public string Name;
        [DataMember(Name = "storage_type", Order = 2)] public string StorageType;
        [DataMember(Name = "is_instance", Order = 3)] public bool IsInstance;
        [DataMember(Name = "formula", Order = 4)] public string Formula;
        [DataMember(Name = "value", Order = 5)] public string Value;
    }

    [DataContract]
    internal sealed class FamilyFileTypeSummary
    {
        [DataMember(Name = "name", Order = 1)] public string Name;
        [DataMember(Name = "parameters", Order = 2)] public List<FamilyFileParameterSummary> Parameters;
    }

    [DataContract]
    internal sealed class FamilyFileSummary
    {
        [DataMember(Name = "file_sha256", Order = 1)] public string FileSha256;
        [DataMember(Name = "format", Order = 2)] public string Format;
        [DataMember(Name = "in_memory_upgrade", Order = 3)] public bool InMemoryUpgrade;
        [DataMember(Name = "family_name", Order = 4)] public string FamilyName;
        [DataMember(Name = "category", Order = 5)] public string Category;
        [DataMember(Name = "family_placement_type", Order = 6)] public string FamilyPlacementType;
        [DataMember(Name = "host_requirement", Order = 7)] public string HostRequirement;
        [DataMember(Name = "connectors", Order = 8)] public List<FamilyFileConnectorSummary> Connectors;
        [DataMember(Name = "types", Order = 9)] public List<FamilyFileTypeSummary> Types;
    }

    [DataContract]
    internal sealed class FamilyFileQueryReport
    {
        [DataMember(Name = "schema_version", Order = 1)] public int SchemaVersion;
        [DataMember(Name = "request_id", Order = 2)] public string RequestId;
        [DataMember(Name = "document_title", Order = 3)] public string DocumentTitle;
        [DataMember(Name = "files", Order = 4)] public List<FamilyFileSummary> Files;
        [DataMember(Name = "provider_egress_performed", Order = 5)] public bool ProviderEgressPerformed;
        [DataMember(Name = "document_mutation_performed", Order = 6)] public bool DocumentMutationPerformed;
    }

    internal static class ReadOnlyFamilyFileQuery
    {
        private static readonly Regex IntakeName = new Regex(
            "^[0-9A-F]{64}\\.rfa$", RegexOptions.CultureInvariant);
        private static readonly string[] ParameterTerms = new[]
        {
            "volt", "watt", "lumen", "power", "load", "frequency", "phase", "pole",
            "photometric", "ies", "light source", "lamp", "apparent", "power factor",
            "manufacturer", "model", "catalog", "type mark", "description", "emergency"
        };

        public static string Capture(
            Application application, string activeDocumentTitle, string requestId,
            IList<string> familyFiles)
        {
            if (familyFiles == null || familyFiles.Count < 1 || familyFiles.Count > 8)
            {
                throw new InvalidDataException("Revit family-file query count is invalid.");
            }
            string root = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "AI-Engineer", "revit");
            string intake = Path.Combine(root, "family-intake");
            List<FamilyFileSummary> values = new List<FamilyFileSummary>();
            foreach (string name in familyFiles)
            {
                if (string.IsNullOrWhiteSpace(name) || !IntakeName.IsMatch(name))
                {
                    throw new InvalidDataException("Revit family-file intake identity is invalid.");
                }
                string path = Path.GetFullPath(Path.Combine(intake, name));
                if (!path.StartsWith(Path.GetFullPath(intake) + Path.DirectorySeparatorChar,
                        StringComparison.OrdinalIgnoreCase)
                    || !File.Exists(path)
                    || !string.Equals(HashFile(path) + ".rfa", name, StringComparison.Ordinal))
                {
                    throw new InvalidDataException("Revit family-file intake hash is invalid.");
                }
                values.Add(CaptureOne(application, path, name.Substring(0, 64)));
            }
            FamilyFileQueryReport report = new FamilyFileQueryReport
            {
                SchemaVersion = 1,
                RequestId = requestId,
                DocumentTitle = Bounded(activeDocumentTitle),
                Files = values,
                ProviderEgressPerformed = false,
                DocumentMutationPerformed = false
            };
            string directory = Path.Combine(root, "family-files");
            Directory.CreateDirectory(directory);
            string fileName = requestId + ".json";
            string output = Path.Combine(directory, fileName);
            DataContractJsonSerializer serializer = new DataContractJsonSerializer(typeof(FamilyFileQueryReport));
            using (FileStream stream = new FileStream(output, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                serializer.WriteObject(stream, report);
                stream.Flush(true);
            }
            return "family-files/" + fileName;
        }

        private static FamilyFileSummary CaptureOne(Application application, string path, string hash)
        {
            string format = BasicFileInfo.Extract(path).Format;
            Document document = null;
            try
            {
                document = application.OpenDocumentFile(path);
                if (!document.IsFamilyDocument || document.OwnerFamily == null)
                {
                    throw new InvalidDataException("The staged Revit file is not a loadable family document.");
                }
                Family family = document.OwnerFamily;
                FamilyPlacementType placement = family.FamilyPlacementType;
                List<FamilyFileConnectorSummary> connectors = new FilteredElementCollector(document)
                    .OfClass(typeof(ConnectorElement))
                    .Cast<ConnectorElement>()
                    .Take(16)
                    .Select(connector => new FamilyFileConnectorSummary
                    {
                        Domain = connector.Domain.ToString(),
                        SystemClassification = connector.SystemClassification.ToString(),
                        Shape = connector.Shape.ToString(),
                        IsPrimary = connector.IsPrimary
                    }).ToList();
                FamilyManager manager = document.FamilyManager;
                List<FamilyParameter> parameters = manager.Parameters
                    .Cast<FamilyParameter>()
                    .Where(IsRelevant)
                    .Take(32)
                    .ToList();
                List<FamilyFileTypeSummary> types = manager.Types
                    .Cast<FamilyType>()
                    .Take(32)
                    .Select(type => new FamilyFileTypeSummary
                    {
                        Name = Bounded(type.Name),
                        Parameters = parameters.Select(parameter => new FamilyFileParameterSummary
                        {
                            Name = Bounded(parameter.Definition == null ? string.Empty : parameter.Definition.Name),
                            StorageType = parameter.StorageType.ToString(),
                            IsInstance = parameter.IsInstance,
                            Formula = Bounded(parameter.Formula),
                            Value = Bounded(Value(type, parameter))
                        }).ToList()
                    }).ToList();
                return new FamilyFileSummary
                {
                    FileSha256 = hash,
                    Format = Bounded(format),
                    InMemoryUpgrade = !string.Equals(format, "2024", StringComparison.Ordinal),
                    FamilyName = Bounded(family.Name),
                    Category = family.FamilyCategory == null ? string.Empty : Bounded(family.FamilyCategory.Name),
                    FamilyPlacementType = placement.ToString(),
                    HostRequirement = HostRequirement(placement),
                    Connectors = connectors,
                    Types = types
                };
            }
            finally
            {
                if (document != null && document.IsValidObject)
                {
                    document.Close(false);
                }
            }
        }

        private static bool IsRelevant(FamilyParameter parameter)
        {
            string name = parameter.Definition == null ? string.Empty : parameter.Definition.Name;
            return ParameterTerms.Any(term => name.IndexOf(term, StringComparison.OrdinalIgnoreCase) >= 0);
        }

        private static string Value(FamilyType type, FamilyParameter parameter)
        {
            if (!type.HasValue(parameter)) return string.Empty;
            string value = type.AsValueString(parameter);
            if (!string.IsNullOrWhiteSpace(value)) return value;
            value = type.AsString(parameter);
            if (!string.IsNullOrWhiteSpace(value)) return value;
            int? integer = type.AsInteger(parameter);
            if (integer.HasValue) return integer.Value.ToString(System.Globalization.CultureInfo.InvariantCulture);
            double? number = type.AsDouble(parameter);
            if (number.HasValue) return number.Value.ToString("R", System.Globalization.CultureInfo.InvariantCulture);
            ElementId id = type.AsElementId(parameter);
            return id == null || id == ElementId.InvalidElementId ? string.Empty : id.Value.ToString();
        }

        private static string HostRequirement(FamilyPlacementType placement)
        {
            switch (placement)
            {
                case FamilyPlacementType.OneLevelBased: return "level";
                case FamilyPlacementType.OneLevelBasedHosted: return "hosted_element_unresolved";
                case FamilyPlacementType.TwoLevelsBased: return "two_levels";
                case FamilyPlacementType.WorkPlaneBased: return "work_plane_or_face";
                case FamilyPlacementType.ViewBased: return "view";
                case FamilyPlacementType.CurveBased: return "curve";
                case FamilyPlacementType.CurveBasedDetail: return "detail_curve";
                default: return "unsupported_or_unresolved";
            }
        }

        private static string HashFile(string path)
        {
            using (SHA256 algorithm = SHA256.Create())
            using (FileStream stream = File.OpenRead(path))
            {
                return BitConverter.ToString(algorithm.ComputeHash(stream)).Replace("-", string.Empty);
            }
        }

        private static string Bounded(string value)
        {
            if (string.IsNullOrEmpty(value)) return string.Empty;
            return value.Length <= 260 ? value : value.Substring(0, 260);
        }
    }
}
