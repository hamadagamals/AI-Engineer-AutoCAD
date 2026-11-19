using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Security.Cryptography;

using Autodesk.Revit.ApplicationServices;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Electrical;
using Autodesk.Revit.UI;

namespace AIEngineer.Revit
{
    [DataContract]
    internal sealed class SchoolOutletTestPreview
    {
        [DataMember(Name = "schema_version", Order = 1)] public int SchemaVersion;
        [DataMember(Name = "preview_id", Order = 2)] public string PreviewId;
        [DataMember(Name = "expires_utc", Order = 3)] public string ExpiresUtc;
        [DataMember(Name = "action", Order = 4)] public string Action;
        [DataMember(Name = "source_sha256", Order = 5)] public string SourceSha256;
        [DataMember(Name = "family_sha256", Order = 6)] public string FamilySha256;
        [DataMember(Name = "output_path", Order = 7)] public string OutputPath;
        [DataMember(Name = "family_path", Order = 8)] public string FamilyPath;
        [DataMember(Name = "type_name", Order = 9)] public string TypeName;
        [DataMember(Name = "requested_voltage_v", Order = 10)] public double RequestedVoltage;
        [DataMember(Name = "requested_load_va", Order = 11)] public double RequestedLoad;
        [DataMember(Name = "template_path", Order = 12)] public string TemplatePath;
        [DataMember(Name = "template_sha256", Order = 13)] public string TemplateSha256;
    }

    [DataContract]
    internal sealed class SchoolOutletTestReport
    {
        [DataMember(Name = "schema_version", Order = 1)] public int SchemaVersion;
        [DataMember(Name = "request_id", Order = 2)] public string RequestId;
        [DataMember(Name = "source_sha256", Order = 3)] public string SourceSha256;
        [DataMember(Name = "family_sha256", Order = 4)] public string FamilySha256;
        [DataMember(Name = "output_path", Order = 5)] public string OutputPath;
        [DataMember(Name = "output_sha256", Order = 6)] public string OutputSha256;
        [DataMember(Name = "family_name", Order = 7)] public string FamilyName;
        [DataMember(Name = "type_name", Order = 8)] public string TypeName;
        [DataMember(Name = "placement_type", Order = 9)] public string PlacementType;
        [DataMember(Name = "instance_id", Order = 10)] public long InstanceId;
        [DataMember(Name = "wall_id", Order = 11)] public long WallId;
        [DataMember(Name = "host_face_present", Order = 12)] public bool HostFacePresent;
        [DataMember(Name = "electrical_connector_count", Order = 13)] public int ElectricalConnectorCount;
        [DataMember(Name = "electrical_system_types", Order = 14)] public List<string> ElectricalSystemTypes;
        [DataMember(Name = "voltage_parameter_display", Order = 15)] public string VoltageParameterDisplay;
        [DataMember(Name = "load_parameter_display", Order = 16)] public string LoadParameterDisplay;
        [DataMember(Name = "revit_voltage_v", Order = 17)] public double? RevitVoltage;
        [DataMember(Name = "revit_apparent_load_va", Order = 18)] public double? RevitApparentLoad;
        [DataMember(Name = "source_document_mutation_performed", Order = 19)] public bool SourceMutation;
        [DataMember(Name = "new_document_saved", Order = 20)] public bool NewDocumentSaved;
        [DataMember(Name = "provider_egress_performed", Order = 21)] public bool ProviderEgressPerformed;
        [DataMember(Name = "circuit_created", Order = 22)] public bool CircuitCreated;
        [DataMember(Name = "circuit_id", Order = 23)] public long CircuitId;
        [DataMember(Name = "circuit_voltage_v", Order = 24)] public double? CircuitVoltage;
        [DataMember(Name = "circuit_apparent_load_va", Order = 25)] public double? CircuitApparentLoad;
        [DataMember(Name = "circuit_error", Order = 26)] public string CircuitError;
    }

    internal static class SchoolOutletIsolatedTest
    {
        private const string Action = "execute_school_outlet_220v_isolated_test";
        private const string FamilyHash = "3DF6502CDD532D4A95C57ED564AB748C8F715CEF49F3C1753BC235512443B0D0";
        private const string TemplateHash = "70EBDD11E857D45597E726C83F5C62FF06B9E392FB8AC958C8FCDC0CDF90CA11";

        public static string Execute(UIApplication uiapp, string requestId, string previewPath,
            string expectedPreviewHash, string requestedOutput)
        {
            if (!string.Equals(Hash(previewPath), expectedPreviewHash, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("School outlet test preview hash changed.");
            SchoolOutletTestPreview preview;
            DataContractJsonSerializer serializer = new DataContractJsonSerializer(typeof(SchoolOutletTestPreview));
            using (FileStream stream = new FileStream(previewPath, FileMode.Open, FileAccess.Read, FileShare.Read))
                preview = serializer.ReadObject(stream) as SchoolOutletTestPreview;
            Document source = uiapp.ActiveUIDocument.Document;
            DateTime expiry;
            Guid previewId;
            if (preview == null || preview.SchemaVersion != 1
                || !Guid.TryParse(preview.PreviewId, out previewId)
                || !DateTime.TryParse(preview.ExpiresUtc, out expiry)
                || expiry.ToUniversalTime() < DateTime.UtcNow
                || !string.Equals(preview.Action, Action, StringComparison.Ordinal)
                || !string.Equals(preview.SourceSha256, Hash(source.PathName), StringComparison.OrdinalIgnoreCase)
                || !string.Equals(preview.FamilySha256, FamilyHash, StringComparison.OrdinalIgnoreCase)
                || !string.Equals(preview.FamilySha256, Hash(preview.FamilyPath), StringComparison.OrdinalIgnoreCase)
                || !string.Equals(preview.TemplateSha256, TemplateHash, StringComparison.OrdinalIgnoreCase)
                || !string.Equals(preview.TemplateSha256, Hash(preview.TemplatePath), StringComparison.OrdinalIgnoreCase)
                || !string.Equals(preview.TypeName, "Standard", StringComparison.Ordinal)
                || preview.RequestedVoltage != 220 || preview.RequestedLoad != 180)
                throw new InvalidDataException("School outlet test preview identity or family changed.");
            string output = Path.GetFullPath(requestedOutput);
            string sourceDirectory = Path.GetFullPath(Path.GetDirectoryName(source.PathName));
            if (!string.Equals(output, Path.GetFullPath(preview.OutputPath), StringComparison.OrdinalIgnoreCase)
                || !string.Equals(Path.GetDirectoryName(output), sourceDirectory,
                    StringComparison.OrdinalIgnoreCase)
                || !string.Equals(Path.GetExtension(output), ".rvt", StringComparison.OrdinalIgnoreCase)
                || File.Exists(output))
                throw new InvalidDataException("School outlet isolated output path is invalid or exists.");

            Document test = null;
            try
            {
                test = uiapp.Application.NewProjectDocument(preview.TemplatePath);
                Family family;
                FamilySymbol symbol = null;
                bool loaded = test.LoadFamily(preview.FamilyPath, out family);
                if (family == null)
                    family = new FilteredElementCollector(test).OfClass(typeof(Family))
                        .Cast<Family>().FirstOrDefault(item =>
                            string.Equals(item.Name, "M_Receptacle - 220V", StringComparison.OrdinalIgnoreCase));
                bool symbolLoaded = false;
                if (family == null)
                {
                    symbolLoaded = test.LoadFamilySymbol(preview.FamilyPath, "Standard", out symbol);
                    if (symbol != null) family = symbol.Family;
                }
                if (family == null)
                {
                    Document familyDocument = uiapp.Application.OpenDocumentFile(preview.FamilyPath);
                    try
                    {
                        if (!familyDocument.IsFamilyDocument)
                            throw new InvalidDataException("Outlet candidate did not open as a Revit family document.");
                        family = familyDocument.LoadFamily(test);
                    }
                    finally
                    {
                        familyDocument.Close(false);
                    }
                }
                if (family == null)
                {
                    string existing = string.Join(",", new FilteredElementCollector(test)
                        .OfClass(typeof(Family)).Cast<Family>().Select(item => item.Name).Take(12));
                    throw new InvalidDataException("School 220 V family unavailable in isolated test; LoadFamily="
                        + loaded + "; LoadFamilySymbol=" + symbolLoaded
                        + "; IsModifiable=" + test.IsModifiable + "; IsReadOnly=" + test.IsReadOnly
                        + "; existing=" + existing + ".");
                }
                if (symbol == null)
                    symbol = family.GetFamilySymbolIds()
                        .Select(id => test.GetElement(id) as FamilySymbol)
                        .FirstOrDefault(item => item != null && item.Name == "Standard");
                if (symbol == null || family.FamilyPlacementType != FamilyPlacementType.WorkPlaneBased)
                    throw new InvalidDataException("School 220 V family type or placement mode differs.");
                Wall wall;
                FamilyInstance instance;
                using (Transaction transaction = new Transaction(test, "AI-Engineer isolated 220 V outlet test"))
                {
                    transaction.Start();
                    Level level = new FilteredElementCollector(test).OfClass(typeof(Level))
                        .Cast<Level>().OrderBy(item => item.Elevation).FirstOrDefault();
                    if (level == null) level = Level.Create(test, 0);
                    double length = UnitUtils.ConvertToInternalUnits(4000, UnitTypeId.Millimeters);
                    wall = Wall.Create(test, Line.CreateBound(XYZ.Zero, new XYZ(length, 0, 0)), level.Id, false);
                    if (!symbol.IsActive) symbol.Activate();
                    test.Regenerate();
                    Options options = new Options { ComputeReferences = true, DetailLevel = ViewDetailLevel.Fine };
                    PlanarFace face = wall.get_Geometry(options).OfType<Solid>()
                        .Where(solid => solid.Volume > 0).SelectMany(solid => solid.Faces.Cast<Face>())
                        .OfType<PlanarFace>()
                        .Where(item => Math.Abs(item.FaceNormal.Y) > 0.99)
                        .OrderByDescending(item => item.Area).FirstOrDefault();
                    if (face == null || face.Reference == null)
                        throw new InvalidDataException("Isolated wall has no usable host face.");
                    XYZ location = new XYZ(length / 2, face.Origin.Y,
                        UnitUtils.ConvertToInternalUnits(450, UnitTypeId.Millimeters));
                    instance = test.Create.NewFamilyInstance(face, location, XYZ.BasisZ, symbol);
                    test.Regenerate();
                    if (instance == null) throw new InvalidDataException("Isolated outlet placement failed.");
                    transaction.Commit();
                }
                List<Connector> connectors = new List<Connector>();
                ConnectorManager manager = instance.MEPModel == null ? null : instance.MEPModel.ConnectorManager;
                if (manager != null)
                    foreach (Connector connector in manager.Connectors)
                        if (connector != null && connector.Domain == Domain.DomainElectrical)
                            connectors.Add(connector);
                if (connectors.Count != 1 || instance.HostFace == null)
                    throw new InvalidDataException("Isolated outlet lacks one electrical connector or a host face.");
                ElectricalSystem circuit = null;
                string circuitError = string.Empty;
                using (Transaction circuitTransaction = new Transaction(test, "AI-Engineer isolated outlet circuit test"))
                {
                    circuitTransaction.Start();
                    try
                    {
                        circuit = ElectricalSystem.Create(test, new List<ElementId> { instance.Id },
                            ElectricalSystemType.PowerCircuit);
                        if (circuit == null)
                            throw new InvalidDataException("Revit returned no electrical circuit.");
                        test.Regenerate();
                        circuitTransaction.Commit();
                    }
                    catch (Exception exception)
                    {
                        if (circuitTransaction.GetStatus() == TransactionStatus.Started)
                            circuitTransaction.RollBack();
                        circuit = null;
                        circuitError = exception.GetType().Name + ": " + exception.Message;
                        if (circuitError.Length > 320) circuitError = circuitError.Substring(0, 320);
                    }
                }
                SchoolOutletTestReport report = new SchoolOutletTestReport
                {
                    SchemaVersion = 1, RequestId = requestId,
                    SourceSha256 = preview.SourceSha256, FamilySha256 = preview.FamilySha256,
                    OutputPath = output, FamilyName = family.Name, TypeName = symbol.Name,
                    PlacementType = family.FamilyPlacementType.ToString(),
                    InstanceId = instance.Id.Value, WallId = wall.Id.Value,
                    HostFacePresent = instance.HostFace != null,
                    ElectricalConnectorCount = connectors.Count,
                    ElectricalSystemTypes = connectors.Select(item => item.ElectricalSystemType.ToString()).ToList(),
                    VoltageParameterDisplay = ParameterDisplay(symbol.LookupParameter("Switch Voltage")),
                    LoadParameterDisplay = ParameterDisplay(symbol.LookupParameter("Load")),
                    RevitVoltage = Measurement(instance.get_Parameter(BuiltInParameter.RBS_ELEC_VOLTAGE), UnitTypeId.Volts),
                    RevitApparentLoad = Measurement(instance.get_Parameter(BuiltInParameter.RBS_ELEC_APPARENT_LOAD), UnitTypeId.VoltAmperes),
                    SourceMutation = false, NewDocumentSaved = false, ProviderEgressPerformed = false,
                    CircuitCreated = circuit != null, CircuitId = circuit == null ? -1 : circuit.Id.Value,
                    CircuitVoltage = circuit == null ? (double?)null
                        : Math.Round(UnitUtils.ConvertFromInternalUnits(circuit.Voltage, UnitTypeId.Volts), 3),
                    CircuitApparentLoad = circuit == null ? (double?)null
                        : Math.Round(UnitUtils.ConvertFromInternalUnits(circuit.ApparentLoad, UnitTypeId.VoltAmperes), 3),
                    CircuitError = circuitError
                };
                SaveAsOptions save = new SaveAsOptions { OverwriteExistingFile = false };
                test.SaveAs(output, save);
                report.NewDocumentSaved = true;
                report.OutputSha256 = Hash(output);
                if (!string.Equals(Hash(source.PathName), preview.SourceSha256, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("Source RVT changed during isolated test.");
                return Write(requestId, report);
            }
            finally
            {
                if (test != null) test.Close(false);
            }
        }

        private static string ParameterDisplay(Parameter parameter)
        { return parameter == null ? string.Empty : parameter.AsValueString() ?? parameter.AsString() ?? string.Empty; }

        private static double? Measurement(Parameter parameter, ForgeTypeId unit)
        {
            if (parameter == null || !parameter.HasValue || parameter.StorageType != StorageType.Double) return null;
            return Math.Round(UnitUtils.ConvertFromInternalUnits(parameter.AsDouble(), unit), 3);
        }

        private static string Write(string requestId, SchoolOutletTestReport report)
        {
            string directory = Path.Combine(Environment.GetFolderPath(
                Environment.SpecialFolder.LocalApplicationData), "AI-Engineer", "revit", "outlet-tests");
            Directory.CreateDirectory(directory);
            string file = Path.Combine(directory, requestId + ".json");
            DataContractJsonSerializer serializer = new DataContractJsonSerializer(typeof(SchoolOutletTestReport));
            using (FileStream stream = new FileStream(file, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            { serializer.WriteObject(stream, report); stream.Flush(true); }
            return "outlet-tests/" + requestId + ".json";
        }

        private static string Hash(string path)
        {
            using (SHA256 sha = SHA256.Create())
            using (FileStream stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                return BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", string.Empty);
        }
    }
}
