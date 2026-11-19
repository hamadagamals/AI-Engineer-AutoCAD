using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Security.Cryptography;

using Autodesk.Revit.ApplicationServices;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Structure;

namespace AIEngineer.Revit
{
    internal sealed class ScratchFamilyLoadOptions : IFamilyLoadOptions
    {
        public bool OnFamilyFound(bool familyInUse, out bool overwriteParameterValues)
        {
            overwriteParameterValues = false;
            return true;
        }

        public bool OnSharedFamilyFound(
            Family sharedFamily, bool familyInUse, out FamilySource source,
            out bool overwriteParameterValues)
        {
            source = FamilySource.Family;
            overwriteParameterValues = false;
            return true;
        }
    }

    [DataContract]
    internal sealed class ScratchParameterResult
    {
        [DataMember(Name = "name", Order = 1)] public string Name;
        [DataMember(Name = "value", Order = 2)] public string Value;
    }

    [DataContract]
    internal sealed class ScratchFamilyResult
    {
        [DataMember(Name = "design_id", Order = 1)] public string DesignId;
        [DataMember(Name = "element_id", Order = 2)] public long ElementId;
        [DataMember(Name = "family_name", Order = 3)] public string FamilyName;
        [DataMember(Name = "type_name", Order = 4)] public string TypeName;
        [DataMember(Name = "category", Order = 5)] public string Category;
        [DataMember(Name = "connector_count", Order = 6)] public int ConnectorCount;
        [DataMember(Name = "x_mm", Order = 7)] public double Xmm;
        [DataMember(Name = "y_mm", Order = 8)] public double Ymm;
        [DataMember(Name = "z_mm", Order = 9)] public double Zmm;
        [DataMember(Name = "bounding_box_present", Order = 10)] public bool BoundingBoxPresent;
        [DataMember(Name = "geometry_object_count", Order = 11)] public int GeometryObjectCount;
        [DataMember(Name = "parameters", Order = 12)] public List<ScratchParameterResult> Parameters;
    }

    [DataContract]
    internal sealed class ScratchFamilyReport
    {
        [DataMember(Name = "schema_version", Order = 1)] public int SchemaVersion;
        [DataMember(Name = "request_id", Order = 2)] public string RequestId;
        [DataMember(Name = "output_rvt", Order = 3)] public string OutputRvt;
        [DataMember(Name = "output_sha256", Order = 4)] public string OutputSha256;
        [DataMember(Name = "source_document_mutated", Order = 5)] public bool SourceDocumentMutated;
        [DataMember(Name = "reopened_verified", Order = 6)] public bool ReopenedVerified;
        [DataMember(Name = "instances", Order = 7)] public List<ScratchFamilyResult> Instances;
    }

    internal static class ScratchFamilyValidation
    {
        private const double MillimetresPerFoot = 304.8;

        public static string Execute(Application application, string requestId, IList<ScratchFamilyItem> items)
        {
            string root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AI-Engineer", "revit");
            string intake = Path.GetFullPath(Path.Combine(root, "scratch-family-intake"));
            string outputDirectory = Path.Combine(root, "scratch-output");
            Directory.CreateDirectory(outputDirectory);
            string output = Path.Combine(outputDirectory, "School-Lighting-Family-Validation-" + requestId + ".rvt");
            if (File.Exists(output)) throw new IOException("Scratch output already exists.");
            string upgradedRoot = Path.Combine(root, "scratch-family-upgraded", requestId);
            Directory.CreateDirectory(upgradedRoot);
            Document scratch = application.NewProjectDocument(UnitSystem.Metric);
            List<ScratchFamilyResult> results = new List<ScratchFamilyResult>();
            try
            {
                List<string> upgradedPaths = new List<string>();
                foreach (ScratchFamilyItem item in items)
                {
                    Validate(item);
                    string sourcePath = Path.GetFullPath(Path.Combine(intake, item.RfaRelativePath));
                    if (!sourcePath.StartsWith(intake + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) || !File.Exists(sourcePath))
                        throw new InvalidDataException("Scratch family path is invalid.");
                    string folderHash = new DirectoryInfo(Path.GetDirectoryName(sourcePath)).Name;
                    if (!string.Equals(HashFile(sourcePath), folderHash, StringComparison.Ordinal))
                        throw new InvalidDataException("Scratch family hash is invalid.");
                    upgradedPaths.Add(UpgradeFamilyCopy(
                        application, sourcePath, upgradedRoot, folderHash, item.TypeName));
                }
                List<FamilySymbol> loadedSymbols = new List<FamilySymbol>();
                for (int index = 0; index < items.Count; index++)
                    loadedSymbols.Add(LoadExactFamilySymbol(
                        scratch, upgradedPaths[index], items[index].TypeName));
                Level level;
                using (Transaction transaction = new Transaction(scratch, "AI-Engineer scratch lighting family validation"))
                {
                    transaction.Start();
                    level = Level.Create(scratch, 0.0);
                    level.Name = "AI-Engineer Validation Level";
                    for (int index = 0; index < items.Count; index++)
                    {
                        ScratchFamilyItem item = items[index];
                        FamilySymbol symbol = loadedSymbols[index];
                        if (!symbol.IsActive)
                        {
                            symbol.Activate();
                            scratch.Regenerate();
                        }
                        FamilyInstance instance = scratch.Create.NewFamilyInstance(
                            new XYZ(item.Xmm / MillimetresPerFoot, item.Ymm / MillimetresPerFoot, item.Zmm / MillimetresPerFoot),
                            symbol, level, StructuralType.NonStructural);
                        Parameter mark = instance.get_Parameter(BuiltInParameter.ALL_MODEL_MARK);
                        if (mark != null && !mark.IsReadOnly) mark.Set("AIENG-" + item.DesignId);
                    }
                    transaction.Commit();
                }
                foreach (ScratchFamilyItem item in items)
                {
                    FamilyInstance instance = FindByMark(scratch, "AIENG-" + item.DesignId);
                    results.Add(Inspect(instance, item.DesignId));
                }
                scratch.SaveAs(output, new SaveAsOptions { OverwriteExistingFile = false });
            }
            finally { if (scratch.IsValidObject) scratch.Close(false); }

            Document reopened = application.OpenDocumentFile(output);
            bool verified;
            try
            {
                HashSet<string> marks = new HashSet<string>(new FilteredElementCollector(reopened).OfClass(typeof(FamilyInstance))
                    .Cast<FamilyInstance>().Select(x => x.get_Parameter(BuiltInParameter.ALL_MODEL_MARK))
                    .Where(x => x != null).Select(x => x.AsString()).Where(x => x != null));
                verified = items.All(x => marks.Contains("AIENG-" + x.DesignId));
                foreach (ScratchFamilyItem item in items)
                {
                    ScratchFamilyResult reopenedResult = Inspect(FindByMark(reopened, "AIENG-" + item.DesignId), item.DesignId);
                    ScratchFamilyResult original = results.Single(x => x.DesignId == item.DesignId);
                    verified = verified
                        && reopenedResult.TypeName == original.TypeName
                        && reopenedResult.FamilyName == original.FamilyName
                        && reopenedResult.Category == "Lighting Fixtures"
                        && reopenedResult.ConnectorCount == original.ConnectorCount
                        && reopenedResult.ConnectorCount > 0
                        && reopenedResult.BoundingBoxPresent
                        && reopenedResult.GeometryObjectCount > 0
                        && Math.Abs(reopenedResult.Xmm - item.Xmm) < 0.1
                        && Math.Abs(reopenedResult.Ymm - item.Ymm) < 0.1
                        && Math.Abs(reopenedResult.Zmm - item.Zmm) < 0.1;
                }
            }
            finally { reopened.Close(false); }
            if (!verified) throw new InvalidDataException("Reopened scratch verification failed.");
            ScratchFamilyReport report = new ScratchFamilyReport { SchemaVersion = 1, RequestId = requestId,
                OutputRvt = output, OutputSha256 = HashFile(output), SourceDocumentMutated = false,
                ReopenedVerified = true, Instances = results };
            string reportDirectory = Path.Combine(root, "scratch-reports");
            Directory.CreateDirectory(reportDirectory);
            string reportPath = Path.Combine(reportDirectory, requestId + ".json");
            using (FileStream stream = new FileStream(reportPath, FileMode.CreateNew))
                new DataContractJsonSerializer(typeof(ScratchFamilyReport)).WriteObject(stream, report);
            return "scratch-reports/" + requestId + ".json";
        }

        private static string UpgradeFamilyCopy(
            Application application, string sourcePath, string upgradedRoot, string sourceHash,
            string exactTypeName)
        {
            string directory = Path.Combine(upgradedRoot, sourceHash);
            Directory.CreateDirectory(directory);
            string destination = Path.Combine(directory, Path.GetFileName(sourcePath));
            if (File.Exists(destination)) throw new IOException("Upgraded scratch family already exists.");
            Document family = application.OpenDocumentFile(sourcePath);
            try
            {
                if (!family.IsFamilyDocument)
                    throw new InvalidDataException("Scratch family source is not a Revit family document.");
                FamilyManager manager = family.FamilyManager;
                List<FamilyType> types = manager.Types.Cast<FamilyType>().ToList();
                FamilyType exact = types.SingleOrDefault(
                    x => string.Equals(x.Name, exactTypeName, StringComparison.Ordinal));
                if (exact == null)
                {
                    List<FamilyType> catalogTypes = types.Where(
                        x => string.Equals(x.Name, "Type Catalog", StringComparison.Ordinal)).ToList();
                    FamilyType sourceType = catalogTypes.Count == 1
                        ? catalogTypes[0]
                        : (types.Count == 1 ? types[0] : null);
                    if (sourceType == null)
                        throw new InvalidDataException(
                            "Scratch family lacks an unambiguous exact or Type Catalog source type: "
                            + string.Join(" | ", types.Select(x => x.Name)));
                    using (Transaction transaction = new Transaction(
                        family, "AI-Engineer bind exact scratch family type"))
                    {
                        transaction.Start();
                        manager.CurrentType = sourceType;
                        manager.RenameCurrentType(exactTypeName);
                        transaction.Commit();
                    }
                }
                family.SaveAs(destination, new SaveAsOptions { OverwriteExistingFile = false });
            }
            finally
            {
                if (family.IsValidObject) family.Close(false);
            }
            string sourceCatalog = Path.ChangeExtension(sourcePath, ".txt");
            if (File.Exists(sourceCatalog))
                File.Copy(sourceCatalog, Path.ChangeExtension(destination, ".txt"), false);
            return destination;
        }

        private static FamilySymbol LoadExactFamilySymbol(
            Document document, string familyPath, string exactTypeName)
        {
            Document familyDocument = document.Application.OpenDocumentFile(familyPath);
            Family family;
            try
            {
                if (!familyDocument.IsFamilyDocument)
                    throw new InvalidOperationException("Scratch source is not a family document.");
                family = familyDocument.LoadFamily(document, new ScratchFamilyLoadOptions());
                if (family == null)
                    throw new InvalidOperationException("Exact scratch family could not be loaded.");
            }
            finally
            {
                if (familyDocument.IsValidObject) familyDocument.Close(false);
            }
            FamilySymbol symbol = family.GetFamilySymbolIds()
                .Select(x => document.GetElement(x) as FamilySymbol)
                .SingleOrDefault(x => x != null
                    && string.Equals(x.Name, exactTypeName, StringComparison.Ordinal));
            if (symbol == null)
                throw new InvalidOperationException("Exact scratch family type is missing after load.");
            return symbol;
        }

        private static FamilyInstance FindByMark(Document document, string expectedMark)
        {
            FamilyInstance instance = new FilteredElementCollector(document).OfClass(typeof(FamilyInstance))
                .Cast<FamilyInstance>().SingleOrDefault(x =>
                {
                    Parameter mark = x.get_Parameter(BuiltInParameter.ALL_MODEL_MARK);
                    return mark != null && string.Equals(mark.AsString(), expectedMark, StringComparison.Ordinal);
                });
            if (instance == null) throw new InvalidDataException("Scratch family instance mark is missing: " + expectedMark);
            return instance;
        }

        private static ScratchFamilyResult Inspect(FamilyInstance instance, string designId)
        {
            LocationPoint location = instance.Location as LocationPoint;
            if (location == null) throw new InvalidDataException("Scratch family instance has no point location.");
            FamilySymbol symbol = instance.Symbol;
            BoundingBoxXYZ bounds = instance.get_BoundingBox(null);
            GeometryElement geometry = instance.get_Geometry(new Options { IncludeNonVisibleObjects = false });
            int geometryCount = geometry == null ? 0 : geometry.Cast<GeometryObject>().Count();
            int connectors = instance.MEPModel == null ? 0 : instance.MEPModel.ConnectorManager.Connectors.Size;
            return new ScratchFamilyResult
            {
                DesignId = designId,
                ElementId = instance.Id.Value,
                FamilyName = symbol.FamilyName,
                TypeName = symbol.Name,
                Category = instance.Category == null ? string.Empty : instance.Category.Name,
                ConnectorCount = connectors,
                Xmm = Math.Round(location.Point.X * MillimetresPerFoot, 3),
                Ymm = Math.Round(location.Point.Y * MillimetresPerFoot, 3),
                Zmm = Math.Round(location.Point.Z * MillimetresPerFoot, 3),
                BoundingBoxPresent = bounds != null,
                GeometryObjectCount = geometryCount,
                Parameters = ReadParameters(symbol)
            };
        }

        private static List<ScratchParameterResult> ReadParameters(FamilySymbol symbol)
        {
            string[] names = { "Manufacturer", "Model", "Description", "Voltage", "Voltage (V)",
                "Apparent Load", "Wattage Comments", "Photometric Web File" };
            List<ScratchParameterResult> values = new List<ScratchParameterResult>();
            foreach (string name in names)
            {
                Parameter parameter = symbol.LookupParameter(name);
                if (parameter == null) continue;
                string value = parameter.AsValueString();
                if (string.IsNullOrWhiteSpace(value)) value = parameter.AsString();
                values.Add(new ScratchParameterResult { Name = name, Value = value ?? string.Empty });
            }
            return values;
        }

        private static void Validate(ScratchFamilyItem item)
        {
            if (item == null || string.IsNullOrWhiteSpace(item.DesignId) || item.DesignId.Length > 32
                || string.IsNullOrWhiteSpace(item.RfaRelativePath) || item.RfaRelativePath.Length > 400
                || string.IsNullOrWhiteSpace(item.TypeName) || item.TypeName.Length > 260
                || Math.Abs(item.Xmm) > 100000 || Math.Abs(item.Ymm) > 100000 || item.Zmm < 0 || item.Zmm > 20000)
                throw new InvalidDataException("Scratch family item is invalid.");
        }

        private static string HashFile(string path)
        {
            using (SHA256 algorithm = SHA256.Create()) using (FileStream stream = File.OpenRead(path))
                return BitConverter.ToString(algorithm.ComputeHash(stream)).Replace("-", string.Empty);
        }
    }
}
