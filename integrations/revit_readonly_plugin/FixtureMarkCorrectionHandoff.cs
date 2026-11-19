using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Security.Cryptography;

using Autodesk.Revit.DB;

namespace AIEngineer.Revit
{
    [DataContract]
    internal sealed class FixtureMarkCorrectionItem
    {
        [DataMember(Name = "element_id", Order = 1)] public long ElementId;
        [DataMember(Name = "old_mark", Order = 2)] public string OldMark;
        [DataMember(Name = "new_mark", Order = 3)] public string NewMark;
        [DataMember(Name = "type_id", Order = 4)] public long TypeId;
        [DataMember(Name = "level_id", Order = 5)] public long LevelId;
        [DataMember(Name = "x_mm", Order = 6)] public double X;
        [DataMember(Name = "y_mm", Order = 7)] public double Y;
        [DataMember(Name = "z_mm", Order = 8)] public double Z;
    }

    [DataContract]
    internal sealed class FixtureMarkCorrectionPreview
    {
        [DataMember(Name = "schema_version", Order = 1)] public int SchemaVersion;
        [DataMember(Name = "preview_id", Order = 2)] public string PreviewId;
        [DataMember(Name = "expires_utc", Order = 3)] public string ExpiresUtc;
        [DataMember(Name = "source_file", Order = 4)] public string SourceFile;
        [DataMember(Name = "source_sha256", Order = 5)] public string SourceSha256;
        [DataMember(Name = "document_title", Order = 6)] public string DocumentTitle;
        [DataMember(Name = "action", Order = 7)] public string Action;
        [DataMember(Name = "items", Order = 8)] public List<FixtureMarkCorrectionItem> Items;
        [DataMember(Name = "status", Order = 9)] public string Status;
        [DataMember(Name = "execution_allowed", Order = 10)] public bool ExecutionAllowed;
        [DataMember(Name = "provider_egress_performed", Order = 11)] public bool ProviderEgressPerformed;
        [DataMember(Name = "document_mutation_performed", Order = 12)] public bool DocumentMutationPerformed;
        [DataMember(Name = "preview_sha256", Order = 13)] public string PreviewSha256;
    }

    [DataContract]
    internal sealed class FixtureMarkCorrectionReport
    {
        [DataMember(Name = "schema_version", Order = 1)] public int SchemaVersion;
        [DataMember(Name = "request_id", Order = 2)] public string RequestId;
        [DataMember(Name = "preview_id", Order = 3)] public string PreviewId;
        [DataMember(Name = "preview_sha256", Order = 4)] public string PreviewSha256;
        [DataMember(Name = "source_sha256", Order = 5)] public string SourceSha256;
        [DataMember(Name = "output_path", Order = 6)] public string OutputPath;
        [DataMember(Name = "output_sha256", Order = 7)] public string OutputSha256;
        [DataMember(Name = "corrected_element_ids", Order = 8)] public List<long> CorrectedElementIds;
        [DataMember(Name = "corrected_count", Order = 9)] public int CorrectedCount;
        [DataMember(Name = "transaction_committed", Order = 10)] public bool TransactionCommitted;
        [DataMember(Name = "saved_as_new_file", Order = 11)] public bool SavedAsNewFile;
        [DataMember(Name = "provider_egress_performed", Order = 12)] public bool ProviderEgressPerformed;
        [DataMember(Name = "document_mutation_performed", Order = 13)] public bool DocumentMutationPerformed;
    }

    internal static class FixtureMarkCorrectionHandoff
    {
        public static string Execute(Document document, string requestId, string previewPath,
            string expectedPreviewSha, string outputPath)
        {
            FixtureMarkCorrectionPreview preview = Read<FixtureMarkCorrectionPreview>(previewPath);
            Validate(preview, expectedPreviewSha, true);
            if (!string.Equals(document.Title, preview.DocumentTitle, StringComparison.Ordinal)
                || !string.Equals(Path.GetFileName(document.PathName), preview.SourceFile, StringComparison.OrdinalIgnoreCase)
                || !string.Equals(Hash(document.PathName), preview.SourceSha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Fixture Mark source no longer matches the approved preview.");
            string output = ModelLineHandoff.ValidateNewOutput(document.PathName, outputPath);
            CheckInstances(document, preview, false);
            using (Transaction transaction = new Transaction(document, "AI-Engineer approved fixture Mark correction"))
            {
                transaction.Start();
                foreach (FixtureMarkCorrectionItem item in preview.Items)
                {
                    FamilyInstance instance = (FamilyInstance)document.GetElement(new ElementId(item.ElementId));
                    Parameter mark = instance.get_Parameter(BuiltInParameter.ALL_MODEL_MARK);
                    if (mark == null || mark.IsReadOnly || mark.StorageType != StorageType.String
                        || !mark.Set(item.NewMark))
                    {
                        transaction.RollBack();
                        throw new InvalidDataException("An approved fixture Mark is not writable.");
                    }
                }
                document.Regenerate();
                try { CheckInstances(document, preview, true); }
                catch { transaction.RollBack(); throw; }
                if (transaction.Commit() != TransactionStatus.Committed)
                    throw new InvalidOperationException("Fixture Mark correction transaction did not commit.");
            }
            SaveAsOptions options = new SaveAsOptions();
            options.OverwriteExistingFile = false;
            document.SaveAs(output, options);
            if (!File.Exists(output)) throw new IOException("Fixture Mark Save As did not create an output.");
            return Write("fixture-mark-handoffs", requestId, new FixtureMarkCorrectionReport
            {
                SchemaVersion = 1, RequestId = requestId, PreviewId = preview.PreviewId,
                PreviewSha256 = preview.PreviewSha256, SourceSha256 = preview.SourceSha256,
                OutputPath = output, OutputSha256 = Hash(output),
                CorrectedElementIds = preview.Items.ConvertAll(item => item.ElementId),
                CorrectedCount = preview.Items.Count, TransactionCommitted = true,
                SavedAsNewFile = true, ProviderEgressPerformed = false, DocumentMutationPerformed = true
            }, typeof(FixtureMarkCorrectionReport));
        }

        public static string Verify(Document document, string requestId, string handoffId, string previewPath)
        {
            FixtureMarkCorrectionPreview preview = Read<FixtureMarkCorrectionPreview>(previewPath);
            FixtureMarkCorrectionReport handoff = Read<FixtureMarkCorrectionReport>(
                Path.Combine(BridgeRoot(), "fixture-mark-handoffs", handoffId + ".json"));
            Validate(preview, handoff.PreviewSha256, false);
            if (!handoff.SavedAsNewFile || !handoff.TransactionCommitted
                || !string.Equals(Path.GetFullPath(document.PathName), handoff.OutputPath, StringComparison.OrdinalIgnoreCase)
                || !string.Equals(Hash(document.PathName), handoff.OutputSha256, StringComparison.OrdinalIgnoreCase)
                || !string.Equals(handoff.PreviewId, preview.PreviewId, StringComparison.Ordinal)
                || handoff.CorrectedCount != preview.Items.Count
                || handoff.CorrectedElementIds == null
                || handoff.CorrectedElementIds.Count != preview.Items.Count)
                throw new InvalidDataException("Saved fixture Mark handoff identity changed.");
            for (int index = 0; index < preview.Items.Count; index++)
                if (handoff.CorrectedElementIds[index] != preview.Items[index].ElementId)
                    throw new InvalidDataException("Saved fixture Mark element list changed.");
            CheckInstances(document, preview, true);
            return Write("fixture-mark-verifications", requestId, new FixtureMarkCorrectionReport
            {
                SchemaVersion = 1, RequestId = requestId, PreviewId = preview.PreviewId,
                PreviewSha256 = preview.PreviewSha256, SourceSha256 = preview.SourceSha256,
                OutputPath = handoff.OutputPath, OutputSha256 = handoff.OutputSha256,
                CorrectedElementIds = preview.Items.ConvertAll(item => item.ElementId),
                CorrectedCount = preview.Items.Count, TransactionCommitted = true,
                SavedAsNewFile = true, ProviderEgressPerformed = false, DocumentMutationPerformed = false
            }, typeof(FixtureMarkCorrectionReport));
        }

        private static void Validate(FixtureMarkCorrectionPreview preview, string expectedSha, bool unexpired)
        {
            DateTime expires;
            Guid id;
            if (preview == null || preview.SchemaVersion != 1 || !Guid.TryParse(preview.PreviewId, out id)
                || !DateTime.TryParse(preview.ExpiresUtc, null,
                    System.Globalization.DateTimeStyles.RoundtripKind, out expires)
                || (unexpired && expires.ToUniversalTime() <= DateTime.UtcNow)
                || preview.Items == null || preview.Items.Count != 8
                || !string.Equals(preview.Action, "correct_fixture_marks", StringComparison.Ordinal)
                || !string.Equals(preview.Status, "review_required", StringComparison.Ordinal)
                || preview.ExecutionAllowed || preview.ProviderEgressPerformed || preview.DocumentMutationPerformed
                || !string.Equals(preview.PreviewSha256, expectedSha, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Fixture Mark correction preview is invalid or expired.");
            HashSet<long> ids = new HashSet<long>();
            HashSet<string> newMarks = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (int index = 0; index < preview.Items.Count; index++)
            {
                FixtureMarkCorrectionItem item = preview.Items[index];
                if (item == null || item.ElementId != 457165 + index
                    || item.TypeId != 447405 || item.LevelId != 30
                    || !string.Equals(item.OldMark,
                        "AIENG-L1-CLASSROOM-1500-" + (index + 9).ToString("D2"), StringComparison.Ordinal)
                    || !string.Equals(item.NewMark,
                        "AIENG-L1-CLASSROOM-1501-" + (index + 1).ToString("D2"), StringComparison.Ordinal)
                    || !ids.Add(item.ElementId) || string.IsNullOrWhiteSpace(item.OldMark)
                    || string.IsNullOrWhiteSpace(item.NewMark) || item.NewMark.Length > 80
                    || !item.NewMark.StartsWith("AIENG-", StringComparison.Ordinal)
                    || string.Equals(item.OldMark, item.NewMark, StringComparison.OrdinalIgnoreCase)
                    || !newMarks.Add(item.NewMark)
                    || Double.IsNaN(item.X) || Double.IsInfinity(item.X) || Math.Abs(item.X) > 1000000
                    || Double.IsNaN(item.Y) || Double.IsInfinity(item.Y) || Math.Abs(item.Y) > 1000000
                    || Double.IsNaN(item.Z) || Double.IsInfinity(item.Z) || Math.Abs(item.Z) > 1000000)
                    throw new InvalidDataException("Fixture Mark correction item is invalid.");
            }
        }

        private static void CheckInstances(Document document, FixtureMarkCorrectionPreview preview, bool corrected)
        {
            HashSet<long> selected = new HashSet<long>();
            HashSet<string> targets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (FixtureMarkCorrectionItem item in preview.Items)
            {
                selected.Add(item.ElementId);
                targets.Add(item.NewMark);
                FamilyInstance instance = document.GetElement(new ElementId(item.ElementId)) as FamilyInstance;
                LocationPoint location = instance == null ? null : instance.Location as LocationPoint;
                Parameter mark = instance == null ? null : instance.get_Parameter(BuiltInParameter.ALL_MODEL_MARK);
                XYZ point = location == null ? null : location.Point;
                if (instance == null || instance.Category == null
                    || instance.Category.Id.Value != (long)BuiltInCategory.OST_LightingFixtures
                    || instance.Symbol.Id.Value != item.TypeId || instance.LevelId.Value != item.LevelId
                    || point == null || mark == null || mark.StorageType != StorageType.String
                    || !string.Equals(mark.AsString(), corrected ? item.NewMark : item.OldMark, StringComparison.Ordinal)
                    || Math.Abs(Millimeters(point.X) - item.X) > 0.01
                    || Math.Abs(Millimeters(point.Y) - item.Y) > 0.01
                    || Math.Abs(Millimeters(point.Z) - item.Z) > 0.01)
                    throw new InvalidDataException("Fixture identity, Mark or location differs from preview.");
            }
            foreach (FamilyInstance instance in new FilteredElementCollector(document)
                .OfCategory(BuiltInCategory.OST_LightingFixtures).OfClass(typeof(FamilyInstance)))
            {
                if (selected.Contains(instance.Id.Value)) continue;
                Parameter mark = instance.get_Parameter(BuiltInParameter.ALL_MODEL_MARK);
                if (mark != null && targets.Contains(mark.AsString() ?? string.Empty))
                    throw new InvalidDataException("Proposed fixture Mark already exists elsewhere in the model.");
            }
        }

        private static double Millimeters(double internalValue)
        { return UnitUtils.ConvertFromInternalUnits(internalValue, UnitTypeId.Millimeters); }
        private static T Read<T>(string path)
        {
            if (!File.Exists(path) || new FileInfo(path).Length > 65536)
                throw new InvalidDataException("Fixture Mark evidence is missing or oversized.");
            DataContractJsonSerializer serializer = new DataContractJsonSerializer(typeof(T));
            using (FileStream stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
                return (T)serializer.ReadObject(stream);
        }
        private static string Write(string folder, string requestId, object value, Type type)
        {
            string directory = Path.Combine(BridgeRoot(), folder);
            Directory.CreateDirectory(directory);
            string path = Path.Combine(directory, requestId + ".json");
            DataContractJsonSerializer serializer = new DataContractJsonSerializer(type);
            using (FileStream stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            { serializer.WriteObject(stream, value); stream.Flush(true); }
            return folder + "/" + requestId + ".json";
        }
        private static string BridgeRoot()
        { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AI-Engineer", "revit"); }
        private static string Hash(string path)
        {
            using (SHA256 algorithm = SHA256.Create())
            using (FileStream stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                return BitConverter.ToString(algorithm.ComputeHash(stream)).Replace("-", string.Empty);
        }
    }
}
