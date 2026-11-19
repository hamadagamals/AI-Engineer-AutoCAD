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
    internal sealed class ScratchRemainingTargetReport
    {
        [DataMember(Name = "design_id", Order = 1)] public string DesignId;
        [DataMember(Name = "family_name", Order = 2)] public string FamilyName;
        [DataMember(Name = "type_name", Order = 3)] public string TypeName;
        [DataMember(Name = "apparent_load_va", Order = 4)] public double ApparentLoadVa;
        [DataMember(Name = "wattage_comments", Order = 5)] public string WattageComments;
        [DataMember(Name = "photometric_web_file", Order = 6)] public string PhotometricWebFile;
        [DataMember(Name = "ies_sha256", Order = 7)] public string IesSha256;
        [DataMember(Name = "connector_count", Order = 8)] public int ConnectorCount;
    }

    [DataContract]
    internal sealed class ScratchRemainingParameterReport
    {
        [DataMember(Name = "schema_version", Order = 1)] public int SchemaVersion;
        [DataMember(Name = "request_id", Order = 2)] public string RequestId;
        [DataMember(Name = "source_sha256_before", Order = 3)] public string SourceSha256Before;
        [DataMember(Name = "source_sha256_after", Order = 4)] public string SourceSha256After;
        [DataMember(Name = "source_document_mutated", Order = 5)] public bool SourceDocumentMutated;
        [DataMember(Name = "output_rvt", Order = 6)] public string OutputRvt;
        [DataMember(Name = "output_sha256", Order = 7)] public string OutputSha256;
        [DataMember(Name = "targets", Order = 8)] public List<ScratchRemainingTargetReport> Targets;
        [DataMember(Name = "l1_preserved", Order = 9)] public bool L1Preserved;
        [DataMember(Name = "reopened_verified", Order = 10)] public bool ReopenedVerified;
    }

    internal static class ScratchRemainingParameterUpdate
    {
        private const string L1Name = "RC132V G6 29_36_43S_830_40 PSD W60L60 OC";
        private const string L2Name = "BN126C LED25_41_64S_840 PSU L1200";
        private const string L5Name = "WT120C 40S_840 PSU L1200";
        private const string L1Ies = "RC132V G6 29_36_43S_830_40 PSD W60L60 OC_6.ies";
        private const string L2Ies = "BN126C LED25_41_64S_840 PSU L1200_2.ies";
        private const string L5Ies = "WT120C 40S_840 PSU L1200_0.ies";

        public static string Execute(Document activeSource, string requestId, string expectedSourceHash,
            string l2RelativePath, string l2Hash, string l5RelativePath, string l5Hash)
        {
            if (activeSource == null || string.IsNullOrWhiteSpace(activeSource.PathName))
                throw new InvalidOperationException("Active remaining-parameter source must be a saved RVT.");
            string source = Path.GetFullPath(activeSource.PathName);
            string sourceBefore = HashFile(source);
            bool sourceModifiedBefore = activeSource.IsModified;
            if (!string.Equals(sourceBefore, expectedSourceHash, StringComparison.Ordinal))
                throw new InvalidDataException("Active remaining-parameter source hash differs from approval.");

            string root = Path.Combine(Environment.GetFolderPath(
                Environment.SpecialFolder.LocalApplicationData), "AI-Engineer", "revit");
            string intake = Path.GetFullPath(Path.Combine(root, "scratch-parameter-intake"));
            string l2Path = ResolveExactIes(intake, l2RelativePath, l2Hash);
            string l5Path = ResolveExactIes(intake, l5RelativePath, l5Hash);
            string revitIesDirectory = Path.Combine(Environment.GetFolderPath(
                Environment.SpecialFolder.CommonApplicationData), "Autodesk",
                "RVT " + activeSource.Application.VersionNumber, "IES");
            if (!Directory.Exists(revitIesDirectory))
                throw new DirectoryNotFoundException("Configured Revit IES directory is unavailable.");
            string l2RevitIes = Path.Combine(revitIesDirectory, Path.GetFileName(l2Path));
            string l5RevitIes = Path.Combine(revitIesDirectory, Path.GetFileName(l5Path));
            CopyExact(l2Path, l2RevitIes, l2Hash);
            CopyExact(l5Path, l5RevitIes, l5Hash);

            string workingDirectory = Path.Combine(root, "scratch-parameter-working", requestId);
            Directory.CreateDirectory(workingDirectory);
            string workingPath = Path.Combine(workingDirectory, "source-copy.rvt");
            if (File.Exists(workingPath)) throw new IOException("Remaining scratch working copy already exists.");
            File.Copy(source, workingPath, false);
            if (!string.Equals(HashFile(workingPath), sourceBefore, StringComparison.Ordinal))
                throw new InvalidDataException("Remaining scratch working copy hash differs from source.");

            string outputDirectory = Path.Combine(root, "scratch-output");
            Directory.CreateDirectory(outputDirectory);
            CopyExact(l2Path, Path.Combine(outputDirectory, Path.GetFileName(l2Path)), l2Hash);
            CopyExact(l5Path, Path.Combine(outputDirectory, Path.GetFileName(l5Path)), l5Hash);
            string output = Path.Combine(outputDirectory,
                "School-Lighting-L1-L2-L5L6-Parameter-Validation-" + requestId + ".rvt");
            if (File.Exists(output)) throw new IOException("Remaining scratch output already exists.");

            Document working = activeSource.Application.OpenDocumentFile(workingPath);
            try
            {
                FamilySymbol l1 = FindSymbol(working, L1Name);
                FamilySymbol l2 = FindSymbol(working, L2Name);
                FamilySymbol l5 = FindSymbol(working, L5Name);
                VerifyValues(l1, 31, "31W", L1Ies, "L1 starting values");
                VerifyValues(l2, 19, "19W", "WT210C LED39S_840 PMMA PSU L1500.ies", "L2 starting values");
                VerifyValues(l5, 19, "19W", "WT198C LED18S840 PSU PCO L600.ies", "L5-L6 starting values");
                using (Transaction transaction = new Transaction(
                    working, "AI-Engineer approved L2 and L5-L6 parameter update"))
                {
                    transaction.Start();
                    if (!Required(l2, "Photometric Web File").Set(l2RevitIes)
                        || !Required(l5, "Apparent Load").Set(UnitUtils.ConvertToInternalUnits(
                            30.0, Required(l5, "Apparent Load").GetUnitTypeId()))
                        || !Required(l5, "Wattage Comments").Set("30W")
                        || !Required(l5, "Photometric Web File").Set(l5RevitIes))
                        throw new InvalidOperationException("Approved remaining parameters could not be set.");
                    transaction.Commit();
                }
                working.SaveAs(output, new SaveAsOptions { OverwriteExistingFile = false });
            }
            finally { if (working.IsValidObject) working.Close(false); }

            ScratchRemainingParameterReport report = Verify(
                activeSource.Application, output, requestId, l2Hash, l5Hash);
            string sourceAfter = HashFile(source);
            if (!string.Equals(sourceBefore, sourceAfter, StringComparison.Ordinal)
                || activeSource.IsModified != sourceModifiedBefore)
                throw new InvalidDataException("Active remaining-parameter source changed during isolated execution.");
            report.SourceSha256Before = sourceBefore;
            report.SourceSha256After = sourceAfter;
            report.SourceDocumentMutated = false;
            string reportDirectory = Path.Combine(root, "scratch-reports");
            Directory.CreateDirectory(reportDirectory);
            string reportPath = Path.Combine(reportDirectory, requestId + ".json");
            using (FileStream stream = new FileStream(reportPath, FileMode.CreateNew))
                new DataContractJsonSerializer(typeof(ScratchRemainingParameterReport)).WriteObject(stream, report);
            return "scratch-reports/" + requestId + ".json";
        }

        private static ScratchRemainingParameterReport Verify(
            Autodesk.Revit.ApplicationServices.Application application, string output,
            string requestId, string l2Hash, string l5Hash)
        {
            Document reopened = application.OpenDocumentFile(output);
            try
            {
                FamilySymbol l1 = FindSymbol(reopened, L1Name);
                FamilySymbol l2 = FindSymbol(reopened, L2Name);
                FamilySymbol l5 = FindSymbol(reopened, L5Name);
                VerifyValues(l1, 31, "31W", L1Ies, "Reopened L1 preservation");
                VerifyValues(l2, 19, "19W", L2Ies, "Reopened L2 verification");
                VerifyValues(l5, 30, "30W", L5Ies, "Reopened L5-L6 verification");
                List<ScratchRemainingTargetReport> targets = new List<ScratchRemainingTargetReport>
                {
                    BuildTarget(reopened, l2, "L2", l2Hash),
                    BuildTarget(reopened, l5, "L5-L6", l5Hash)
                };
                if (targets.Any(item => item.ConnectorCount != 1))
                    throw new InvalidDataException("A remaining-parameter target connector count differs from approval.");
                return new ScratchRemainingParameterReport
                {
                    SchemaVersion = 1, RequestId = requestId, OutputRvt = output,
                    OutputSha256 = HashFile(output), Targets = targets,
                    L1Preserved = true, ReopenedVerified = true
                };
            }
            finally { reopened.Close(false); }
        }

        private static ScratchRemainingTargetReport BuildTarget(
            Document document, FamilySymbol symbol, string designId, string iesHash)
        {
            FamilyInstance instance = new FilteredElementCollector(document)
                .OfClass(typeof(FamilyInstance)).Cast<FamilyInstance>()
                .SingleOrDefault(item => item.Symbol != null && item.Symbol.Id == symbol.Id);
            int connectors = instance == null || instance.MEPModel == null
                ? 0 : instance.MEPModel.ConnectorManager.Connectors.Size;
            Parameter load = Required(symbol, "Apparent Load");
            string photometric = Required(symbol, "Photometric Web File").AsString();
            return new ScratchRemainingTargetReport
            {
                DesignId = designId, FamilyName = symbol.FamilyName, TypeName = symbol.Name,
                ApparentLoadVa = Math.Round(UnitUtils.ConvertFromInternalUnits(
                    load.AsDouble(), load.GetUnitTypeId()), 3),
                WattageComments = Required(symbol, "Wattage Comments").AsString(),
                PhotometricWebFile = string.IsNullOrWhiteSpace(photometric)
                    ? photometric : Path.GetFileName(photometric),
                IesSha256 = iesHash, ConnectorCount = connectors
            };
        }

        private static void VerifyValues(
            FamilySymbol symbol, double expectedLoad, string expectedWatts,
            string expectedIes, string label)
        {
            Parameter load = Required(symbol, "Apparent Load");
            double actualLoad = UnitUtils.ConvertFromInternalUnits(load.AsDouble(), load.GetUnitTypeId());
            string watts = Required(symbol, "Wattage Comments").AsString();
            string photometric = Required(symbol, "Photometric Web File").AsString();
            string file = string.IsNullOrWhiteSpace(photometric) ? photometric : Path.GetFileName(photometric);
            if (Math.Abs(actualLoad - expectedLoad) > 0.01
                || !string.Equals(watts, expectedWatts, StringComparison.Ordinal)
                || !string.Equals(file, expectedIes, StringComparison.Ordinal))
                throw new InvalidDataException(label + " differ: load=" + Math.Round(actualLoad, 3)
                    + "; wattage=" + (watts ?? "<null>") + "; photometric=" + (photometric ?? "<null>"));
        }

        private static FamilySymbol FindSymbol(Document document, string name)
        {
            FamilySymbol symbol = new FilteredElementCollector(document)
                .OfClass(typeof(FamilySymbol)).Cast<FamilySymbol>()
                .SingleOrDefault(item => string.Equals(item.FamilyName, name, StringComparison.Ordinal)
                    && string.Equals(item.Name, name, StringComparison.Ordinal));
            if (symbol == null) throw new InvalidDataException("Approved family/type is missing: " + name);
            return symbol;
        }

        private static Parameter Required(Element element, string name)
        {
            Parameter parameter = element.LookupParameter(name);
            if (parameter == null || parameter.IsReadOnly)
                throw new InvalidDataException("Required writable remaining parameter is unavailable: " + name);
            return parameter;
        }

        private static string ResolveExactIes(string intake, string relative, string expectedHash)
        {
            string path = Path.GetFullPath(Path.Combine(intake, relative));
            if (!path.StartsWith(intake + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                || !File.Exists(path) || !string.Equals(Path.GetExtension(path), ".ies", StringComparison.OrdinalIgnoreCase)
                || !string.Equals(HashFile(path), expectedHash, StringComparison.Ordinal))
                throw new InvalidDataException("Approved remaining IES evidence is invalid.");
            return path;
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
                    throw new InvalidDataException("Existing remaining IES differs from approved evidence.");
                return;
            }
            File.Copy(source, destination, false);
            if (!string.Equals(HashFile(destination), expectedHash, StringComparison.Ordinal))
                throw new InvalidDataException("Copied remaining IES differs from approved evidence.");
        }
    }
}
