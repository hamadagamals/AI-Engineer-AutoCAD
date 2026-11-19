using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Security.Cryptography;

using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Electrical;

namespace AIEngineer.Revit
{
    [DataContract]
    internal sealed class SchoolOutletElectricalValue
    {
        [DataMember(Name = "source", Order = 1)] public string Source;
        [DataMember(Name = "name", Order = 2)] public string Name;
        [DataMember(Name = "value", Order = 3)] public string Value;
    }

    [DataContract]
    internal sealed class SchoolOutletConnector
    {
        [DataMember(Name = "id", Order = 1)] public int Id;
        [DataMember(Name = "domain", Order = 2)] public string Domain;
        [DataMember(Name = "electrical_system_type", Order = 3)] public string ElectricalSystemType;
        [DataMember(Name = "assigned_system_id", Order = 4)] public long AssignedSystemId;
    }

    [DataContract]
    internal sealed class SchoolOutletElectricalRecord
    {
        [DataMember(Name = "element_id", Order = 1)] public long ElementId;
        [DataMember(Name = "family_name", Order = 2)] public string FamilyName;
        [DataMember(Name = "type_name", Order = 3)] public string TypeName;
        [DataMember(Name = "host_id", Order = 4)] public long HostId;
        [DataMember(Name = "host_category", Order = 5)] public string HostCategory;
        [DataMember(Name = "voltage_v", Order = 6)] public double? Voltage;
        [DataMember(Name = "apparent_load_va", Order = 7)] public double? ApparentLoad;
        [DataMember(Name = "connectors", Order = 8)] public List<SchoolOutletConnector> Connectors;
        [DataMember(Name = "electrical_parameters", Order = 9)] public List<SchoolOutletElectricalValue> Parameters;
    }

    [DataContract]
    internal sealed class SchoolOutletElectricalReport
    {
        [DataMember(Name = "schema_version", Order = 1)] public int SchemaVersion;
        [DataMember(Name = "request_id", Order = 2)] public string RequestId;
        [DataMember(Name = "document_title", Order = 3)] public string DocumentTitle;
        [DataMember(Name = "document_sha256", Order = 4)] public string DocumentSha256;
        [DataMember(Name = "total_count", Order = 5)] public int TotalCount;
        [DataMember(Name = "outlets", Order = 6)] public List<SchoolOutletElectricalRecord> Outlets;
        [DataMember(Name = "provider_egress_performed", Order = 7)] public bool ProviderEgressPerformed;
        [DataMember(Name = "document_mutation_performed", Order = 8)] public bool DocumentMutationPerformed;
    }

    internal static class ReadOnlySchoolOutletConnectors
    {
        public static string Capture(Document document, string requestId, string expectedSha)
        {
            if (document.IsFamilyDocument || string.IsNullOrWhiteSpace(document.PathName)
                || !string.Equals(Hash(document.PathName), expectedSha, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("School outlet connector source hash changed.");
            List<FamilyInstance> instances = new FilteredElementCollector(document)
                .OfCategory(BuiltInCategory.OST_ElectricalFixtures).WhereElementIsNotElementType()
                .OfClass(typeof(FamilyInstance)).Cast<FamilyInstance>()
                .OrderBy(item => item.Id.Value).ToList();
            if (instances.Count != 16)
                throw new InvalidDataException("School V5 physical outlet count differs from 16.");
            List<SchoolOutletElectricalRecord> records = instances.Select(Record).ToList();
            SchoolOutletElectricalReport report = new SchoolOutletElectricalReport
            {
                SchemaVersion = 1, RequestId = requestId, DocumentTitle = document.Title,
                DocumentSha256 = expectedSha, TotalCount = records.Count, Outlets = records,
                ProviderEgressPerformed = false, DocumentMutationPerformed = false
            };
            string directory = Path.Combine(Environment.GetFolderPath(
                Environment.SpecialFolder.LocalApplicationData), "AI-Engineer", "revit", "school-outlet-connectors");
            Directory.CreateDirectory(directory);
            string file = Path.Combine(directory, requestId + ".json");
            DataContractJsonSerializer serializer = new DataContractJsonSerializer(typeof(SchoolOutletElectricalReport));
            using (FileStream stream = new FileStream(file, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            { serializer.WriteObject(stream, report); stream.Flush(true); }
            return "school-outlet-connectors/" + requestId + ".json";
        }

        private static SchoolOutletElectricalRecord Record(FamilyInstance instance)
        {
            FamilySymbol symbol = instance.Symbol;
            Element host = instance.Host;
            List<SchoolOutletConnector> connectors = new List<SchoolOutletConnector>();
            ConnectorManager manager = instance.MEPModel == null ? null : instance.MEPModel.ConnectorManager;
            if (manager != null)
                foreach (Connector connector in manager.Connectors)
                {
                    if (connector == null) continue;
                    MEPSystem system = connector.Domain == Domain.DomainElectrical ? connector.MEPSystem : null;
                    connectors.Add(new SchoolOutletConnector
                    {
                        Id = connector.Id, Domain = connector.Domain.ToString(),
                        ElectricalSystemType = connector.Domain == Domain.DomainElectrical
                            ? connector.ElectricalSystemType.ToString() : string.Empty,
                        AssignedSystemId = system == null ? -1 : system.Id.Value
                    });
                }
            List<SchoolOutletElectricalValue> parameters = new List<SchoolOutletElectricalValue>();
            AddParameters(parameters, instance, "instance");
            if (symbol != null) AddParameters(parameters, symbol, "type");
            return new SchoolOutletElectricalRecord
            {
                ElementId = instance.Id.Value,
                FamilyName = symbol == null || symbol.Family == null ? string.Empty : symbol.Family.Name,
                TypeName = symbol == null ? string.Empty : symbol.Name,
                HostId = host == null ? -1 : host.Id.Value,
                HostCategory = host == null || host.Category == null ? string.Empty : host.Category.Name,
                Voltage = Measurement(instance.get_Parameter(BuiltInParameter.RBS_ELEC_VOLTAGE), UnitTypeId.Volts),
                ApparentLoad = Measurement(instance.get_Parameter(BuiltInParameter.RBS_ELEC_APPARENT_LOAD), UnitTypeId.VoltAmperes),
                Connectors = connectors, Parameters = parameters
            };
        }

        private static double? Measurement(Parameter parameter, ForgeTypeId unit)
        {
            if (parameter == null || !parameter.HasValue || parameter.StorageType != StorageType.Double)
                return null;
            double value = UnitUtils.ConvertFromInternalUnits(parameter.AsDouble(), unit);
            return double.IsNaN(value) || double.IsInfinity(value) ? (double?)null : Math.Round(value, 3);
        }

        private static void AddParameters(List<SchoolOutletElectricalValue> values, Element owner, string source)
        {
            foreach (Parameter parameter in owner.Parameters)
            {
                string name = parameter.Definition == null ? string.Empty : parameter.Definition.Name;
                if (name.IndexOf("volt", StringComparison.OrdinalIgnoreCase) < 0
                    && name.IndexOf("load", StringComparison.OrdinalIgnoreCase) < 0
                    && name.IndexOf("power", StringComparison.OrdinalIgnoreCase) < 0
                    && name.IndexOf("phase", StringComparison.OrdinalIgnoreCase) < 0)
                    continue;
                if (values.Count >= 32) break;
                string display = parameter.AsValueString();
                if (string.IsNullOrWhiteSpace(display) && parameter.StorageType == StorageType.String)
                    display = parameter.AsString();
                values.Add(new SchoolOutletElectricalValue
                { Source = source, Name = name, Value = display ?? string.Empty });
            }
        }

        private static string Hash(string path)
        {
            using (SHA256 sha = SHA256.Create())
            using (FileStream stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                return BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", string.Empty);
        }
    }
}
