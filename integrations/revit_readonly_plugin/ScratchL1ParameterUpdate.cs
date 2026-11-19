using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Security.Cryptography;

using Autodesk.Revit.DB;

namespace AIEngineer.Revit
{
    [DataContract]
    internal sealed class ScratchL1ParameterReport
    {
        [DataMember(Name = "schema_version", Order = 1)] public int SchemaVersion;
        [DataMember(Name = "request_id", Order = 2)] public string RequestId;
        [DataMember(Name = "source_sha256_before", Order = 3)] public string SourceSha256Before;
        [DataMember(Name = "source_sha256_after", Order = 4)] public string SourceSha256After;
        [DataMember(Name = "source_document_mutated", Order = 5)] public bool SourceDocumentMutated;
        [DataMember(Name = "output_rvt", Order = 6)] public string OutputRvt;
        [DataMember(Name = "output_sha256", Order = 7)] public string OutputSha256;
        [DataMember(Name = "ies_sha256", Order = 8)] public string IesSha256;
        [DataMember(Name = "family_name", Order = 9)] public string FamilyName;
        [DataMember(Name = "type_name", Order = 10)] public string TypeName;
        [DataMember(Name = "apparent_load_va", Order = 11)] public double ApparentLoadVa;
        [DataMember(Name = "wattage_comments", Order = 12)] public string WattageComments;
        [DataMember(Name = "photometric_web_file", Order = 13)] public string PhotometricWebFile;
        [DataMember(Name = "connector_count", Order = 14)] public int ConnectorCount;
        [DataMember(Name = "reopened_verified", Order = 15)] public bool ReopenedVerified;
    }

    internal static class ScratchL1ParameterUpdate
    {
        private const string FamilyName = "RC132V G6 29_36_43S_830_40 PSD W60L60 OC";
        private const string TypeName = "RC132V G6 29_36_43S_830_40 PSD W60L60 OC";

        public static string Execute(
            Document activeSource, string requestId, string expectedSourceHash,
            string iesRelativePath, string expectedIesHash)
        {
            if (activeSource == null || string.IsNullOrWhiteSpace(activeSource.PathName))
                throw new InvalidOperationException("Active scratch source must be a saved RVT.");
            string source = Path.GetFullPath(activeSource.PathName);
            string sourceBefore = HashFile(source);
            bool sourceModifiedBefore = activeSource.IsModified;
            if (!string.Equals(sourceBefore, expectedSourceHash, StringComparison.Ordinal))
                throw new InvalidDataException("Active scratch source hash differs from approval.");

            string root = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "AI-Engineer", "revit");
            string intake = Path.GetFullPath(Path.Combine(root, "scratch-parameter-intake"));
            string iesPath = Path.GetFullPath(Path.Combine(intake, iesRelativePath));
            if (!iesPath.StartsWith(intake + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                || !File.Exists(iesPath)
                || !string.Equals(Path.GetExtension(iesPath), ".ies", StringComparison.OrdinalIgnoreCase)
                || !string.Equals(HashFile(iesPath), expectedIesHash, StringComparison.Ordinal))
                throw new InvalidDataException("Approved L1 IES evidence is invalid.");

            string workingDirectory = Path.Combine(root, "scratch-parameter-working", requestId);
            Directory.CreateDirectory(workingDirectory);
            string workingPath = Path.Combine(workingDirectory, "source-copy.rvt");
            if (File.Exists(workingPath)) throw new IOException("Scratch working copy already exists.");
            File.Copy(source, workingPath, false);
            if (!string.Equals(HashFile(workingPath), sourceBefore, StringComparison.Ordinal))
                throw new InvalidDataException("Scratch working copy hash differs from source.");
            CopyExact(iesPath, Path.Combine(workingDirectory, Path.GetFileName(iesPath)), expectedIesHash);

            string outputDirectory = Path.Combine(root, "scratch-output");
            Directory.CreateDirectory(outputDirectory);
            CopyExact(iesPath, Path.Combine(outputDirectory, Path.GetFileName(iesPath)), expectedIesHash);
            string revitIesDirectory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                "Autodesk", "RVT " + activeSource.Application.VersionNumber, "IES");
            if (!Directory.Exists(revitIesDirectory))
                throw new DirectoryNotFoundException("Configured Revit IES directory is unavailable.");
            string revitIesPath = Path.Combine(revitIesDirectory, Path.GetFileName(iesPath));
            CopyExact(iesPath, revitIesPath, expectedIesHash);
            string output = Path.Combine(
                outputDirectory, "School-Lighting-L1-Parameter-Validation-" + requestId + ".rvt");
            if (File.Exists(output)) throw new IOException("L1 scratch output already exists.");

            Document working = activeSource.Application.OpenDocumentFile(workingPath);
            try
            {
                FamilySymbol symbol = FindSymbol(working);
                Parameter load = Required(symbol, "Apparent Load");
                Parameter wattage = Required(symbol, "Wattage Comments");
                Parameter photometric = Required(symbol, "Photometric Web File");
                double oldLoad = UnitUtils.ConvertFromInternalUnits(load.AsDouble(), load.GetUnitTypeId());
                if (Math.Abs(oldLoad - 19.0) > 0.01
                    || !string.Equals(wattage.AsString(), "19W", StringComparison.Ordinal)
                    || !string.Equals(photometric.AsString(), "ES-M-PS-12S-827-PSD-MB-ALL-ALL.ies", StringComparison.Ordinal))
                    throw new InvalidDataException("L1 starting parameter values differ from approval.");
                using (Transaction transaction = new Transaction(working, "AI-Engineer approved L1 parameter update"))
                {
                    transaction.Start();
                    if (!load.Set(UnitUtils.ConvertToInternalUnits(31.0, load.GetUnitTypeId()))
                        || !wattage.Set("31W")
                        || !photometric.Set(revitIesPath))
                        throw new InvalidOperationException("One or more approved L1 parameters could not be set.");
                    transaction.Commit();
                }
                working.SaveAs(output, new SaveAsOptions { OverwriteExistingFile = false });
            }
            finally
            {
                if (working.IsValidObject) working.Close(false);
            }

            ScratchL1ParameterReport report = Verify(activeSource.Application, output, requestId);
            string sourceAfter = HashFile(source);
            bool sourceMutated = !string.Equals(sourceBefore, sourceAfter, StringComparison.Ordinal)
                || activeSource.IsModified != sourceModifiedBefore;
            if (sourceMutated) throw new InvalidDataException("Active scratch source changed during isolated execution.");
            report.SourceSha256Before = sourceBefore;
            report.SourceSha256After = sourceAfter;
            report.SourceDocumentMutated = false;
            report.IesSha256 = HashFile(iesPath);

            string reportDirectory = Path.Combine(root, "scratch-reports");
            Directory.CreateDirectory(reportDirectory);
            string reportPath = Path.Combine(reportDirectory, requestId + ".json");
            using (FileStream stream = new FileStream(reportPath, FileMode.CreateNew))
                new DataContractJsonSerializer(typeof(ScratchL1ParameterReport)).WriteObject(stream, report);
            return "scratch-reports/" + requestId + ".json";
        }

        private static ScratchL1ParameterReport Verify(
            Autodesk.Revit.ApplicationServices.Application application, string output, string requestId)
        {
            Document reopened = application.OpenDocumentFile(output);
            try
            {
                FamilySymbol symbol = FindSymbol(reopened);
                Parameter load = Required(symbol, "Apparent Load");
                double loadVa = UnitUtils.ConvertFromInternalUnits(load.AsDouble(), load.GetUnitTypeId());
                string wattage = Required(symbol, "Wattage Comments").AsString();
                string photometric = Required(symbol, "Photometric Web File").AsString();
                FamilyInstance instance = new FilteredElementCollector(reopened)
                    .OfClass(typeof(FamilyInstance)).Cast<FamilyInstance>()
                    .SingleOrDefault(x => x.Symbol != null && x.Symbol.Id == symbol.Id);
                int connectors = instance == null || instance.MEPModel == null
                    ? 0 : instance.MEPModel.ConnectorManager.Connectors.Size;
                string photometricFile = string.IsNullOrWhiteSpace(photometric)
                    ? photometric : Path.GetFileName(photometric);
                bool verified = Math.Abs(loadVa - 31.0) <= 0.01
                    && string.Equals(wattage, "31W", StringComparison.Ordinal)
                    && string.Equals(photometricFile,
                        "RC132V G6 29_36_43S_830_40 PSD W60L60 OC_6.ies", StringComparison.Ordinal)
                    && connectors == 1;
                if (!verified)
                    throw new InvalidDataException(
                        "Reopened L1 parameter verification failed: load="
                        + Math.Round(loadVa, 3).ToString(System.Globalization.CultureInfo.InvariantCulture)
                        + "; wattage=" + (wattage ?? "<null>")
                        + "; photometric=" + (photometric ?? "<null>")
                        + "; connectors=" + connectors.ToString(System.Globalization.CultureInfo.InvariantCulture));
                return new ScratchL1ParameterReport
                {
                    SchemaVersion = 1,
                    RequestId = requestId,
                    OutputRvt = output,
                    OutputSha256 = HashFile(output),
                    FamilyName = FamilyName,
                    TypeName = TypeName,
                    ApparentLoadVa = Math.Round(loadVa, 3),
                    WattageComments = wattage,
                    PhotometricWebFile = photometricFile,
                    ConnectorCount = connectors,
                    ReopenedVerified = true
                };
            }
            finally { reopened.Close(false); }
        }

        private static FamilySymbol FindSymbol(Document document)
        {
            FamilySymbol symbol = new FilteredElementCollector(document)
                .OfClass(typeof(FamilySymbol)).Cast<FamilySymbol>()
                .SingleOrDefault(x => string.Equals(x.FamilyName, FamilyName, StringComparison.Ordinal)
                    && string.Equals(x.Name, TypeName, StringComparison.Ordinal));
            if (symbol == null) throw new InvalidDataException("Approved L1 family/type is missing.");
            return symbol;
        }

        private static Parameter Required(Element element, string name)
        {
            Parameter parameter = element.LookupParameter(name);
            if (parameter == null || parameter.IsReadOnly)
                throw new InvalidDataException("Required writable L1 parameter is unavailable: " + name);
            return parameter;
        }

        private static string HashFile(string path)
        {
            using (SHA256 algorithm = SHA256.Create())
            using (FileStream stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                return BitConverter.ToString(algorithm.ComputeHash(stream)).Replace("-", string.Empty);
        }

        private static void CopyExact(string source, string destination, string expectedHash)
        {
            if (File.Exists(destination))
            {
                if (!string.Equals(HashFile(destination), expectedHash, StringComparison.Ordinal))
                    throw new InvalidDataException("Existing scratch IES differs from approved evidence.");
                return;
            }
            File.Copy(source, destination, false);
            if (!string.Equals(HashFile(destination), expectedHash, StringComparison.Ordinal))
                throw new InvalidDataException("Copied scratch IES differs from approved evidence.");
        }
    }
}
