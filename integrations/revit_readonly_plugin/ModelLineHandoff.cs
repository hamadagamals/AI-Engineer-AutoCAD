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
    internal sealed class ModelLineHandoffReport
    {
        [DataMember(Name = "schema_version", Order = 1)] public int SchemaVersion;
        [DataMember(Name = "request_id", Order = 2)] public string RequestId;
        [DataMember(Name = "preview_id", Order = 3)] public string PreviewId;
        [DataMember(Name = "preview_sha256", Order = 4)] public string PreviewSha256;
        [DataMember(Name = "source_sha256", Order = 5)] public string SourceSha256;
        [DataMember(Name = "output_path", Order = 6)] public string OutputPath;
        [DataMember(Name = "output_sha256", Order = 7)] public string OutputSha256;
        [DataMember(Name = "created_model_lines", Order = 8)] public int CreatedModelLines;
        [DataMember(Name = "created_element_ids", Order = 9)] public List<long> CreatedElementIds;
        [DataMember(Name = "transaction_committed", Order = 10)] public bool TransactionCommitted;
        [DataMember(Name = "saved_as_new_file", Order = 11)] public bool SavedAsNewFile;
        [DataMember(Name = "provider_egress_performed", Order = 12)] public bool ProviderEgressPerformed;
        [DataMember(Name = "document_mutation_performed", Order = 13)] public bool DocumentMutationPerformed;
    }

    [DataContract]
    internal sealed class ModelLineVerificationReport
    {
        [DataMember(Name = "schema_version", Order = 1)] public int SchemaVersion;
        [DataMember(Name = "request_id", Order = 2)] public string RequestId;
        [DataMember(Name = "handoff_request_id", Order = 3)] public string HandoffRequestId;
        [DataMember(Name = "output_sha256", Order = 4)] public string OutputSha256;
        [DataMember(Name = "verified_model_lines", Order = 5)] public int VerifiedModelLines;
        [DataMember(Name = "verified_element_ids", Order = 6)] public List<long> VerifiedElementIds;
        [DataMember(Name = "geometry_matches_preview", Order = 7)] public bool GeometryMatchesPreview;
        [DataMember(Name = "provider_egress_performed", Order = 8)] public bool ProviderEgressPerformed;
        [DataMember(Name = "document_mutation_performed", Order = 9)] public bool DocumentMutationPerformed;
    }

    internal static class ModelLineHandoff
    {
        public static string Execute(
            Document document, string requestId, string previewPath, string outputPath)
        {
            ModelLinePreview preview = ReadJson<ModelLinePreview>(previewPath);
            ValidatePreview(preview, true);
            string output = ValidateNewOutput(document.PathName, outputPath);
            string executionRelative = ModelLineExecution.Execute(document, requestId, previewPath);
            ModelLineExecutionReport execution = ReadJson<ModelLineExecutionReport>(Path.Combine(
                BridgeRoot(), executionRelative.Replace('/', Path.DirectorySeparatorChar)));
            if (execution.CreatedModelLines != 4 || execution.CreatedElementIds == null
                || execution.CreatedElementIds.Count != 4 || !execution.TransactionCommitted)
            {
                throw new InvalidDataException("Model Line execution evidence is invalid.");
            }
            SaveAsOptions options = new SaveAsOptions();
            options.OverwriteExistingFile = false;
            document.SaveAs(output, options);
            if (!File.Exists(output))
            {
                throw new IOException("Revit Save As did not create the approved output.");
            }
            ModelLineHandoffReport report = new ModelLineHandoffReport
            {
                SchemaVersion = 1,
                RequestId = requestId,
                PreviewId = preview.PreviewId,
                PreviewSha256 = preview.PreviewSha256,
                SourceSha256 = preview.SourceSha256,
                OutputPath = output,
                OutputSha256 = HashFile(output),
                CreatedModelLines = 4,
                CreatedElementIds = execution.CreatedElementIds,
                TransactionCommitted = true,
                SavedAsNewFile = true,
                ProviderEgressPerformed = false,
                DocumentMutationPerformed = true
            };
            return WriteJson("handoffs", requestId, report, typeof(ModelLineHandoffReport));
        }

        public static string Verify(
            Document document, string requestId, string handoffRequestId, string previewPath)
        {
            ModelLineHandoffReport handoff = ReadJson<ModelLineHandoffReport>(Path.Combine(
                BridgeRoot(), "handoffs", handoffRequestId + ".json"));
            ModelLinePreview preview = ReadJson<ModelLinePreview>(previewPath);
            ValidatePreview(preview, false);
            string current = Path.GetFullPath(document.PathName);
            if (!handoff.SavedAsNewFile
                || !string.Equals(current, handoff.OutputPath, StringComparison.OrdinalIgnoreCase)
                || !string.Equals(HashFile(current), handoff.OutputSha256, StringComparison.OrdinalIgnoreCase)
                || !string.Equals(preview.PreviewId, handoff.PreviewId, StringComparison.Ordinal)
                || !string.Equals(preview.PreviewSha256, handoff.PreviewSha256, StringComparison.OrdinalIgnoreCase)
                || handoff.CreatedElementIds == null
                || handoff.CreatedElementIds.Count != preview.Segments.Count)
            {
                throw new InvalidDataException("Reopened Revit handoff identity does not match its evidence.");
            }
            List<long> verified = new List<long>();
            for (int index = 0; index < handoff.CreatedElementIds.Count; index++)
            {
                long identifier = handoff.CreatedElementIds[index];
                ModelCurve curve = document.GetElement(new ElementId(identifier)) as ModelCurve;
                Line line = curve == null ? null : curve.GeometryCurve as Line;
                PreviewSegment segment = preview.Segments[index];
                if (line == null || !SameSegment(
                    line.GetEndPoint(0), line.GetEndPoint(1),
                    Point(segment.Start), Point(segment.End)))
                {
                    throw new InvalidDataException("Reopened Model Line geometry does not match the preview.");
                }
                verified.Add(identifier);
            }
            ModelLineVerificationReport report = new ModelLineVerificationReport
            {
                SchemaVersion = 1,
                RequestId = requestId,
                HandoffRequestId = handoffRequestId,
                OutputSha256 = handoff.OutputSha256,
                VerifiedModelLines = verified.Count,
                VerifiedElementIds = verified,
                GeometryMatchesPreview = true,
                ProviderEgressPerformed = false,
                DocumentMutationPerformed = false
            };
            return WriteJson("verifications", requestId, report, typeof(ModelLineVerificationReport));
        }

        internal static string ValidateNewOutput(string sourcePath, string outputPath)
        {
            if (string.IsNullOrWhiteSpace(sourcePath) || string.IsNullOrWhiteSpace(outputPath))
            {
                throw new InvalidDataException("Revit source and output paths are required.");
            }
            string source = Path.GetFullPath(sourcePath);
            string output = Path.GetFullPath(outputPath);
            DirectoryInfo workspace = new FileInfo(source).Directory;
            while (workspace != null
                && !string.Equals(workspace.Name, "workspace", StringComparison.OrdinalIgnoreCase))
            {
                workspace = workspace.Parent;
            }
            if (workspace == null)
            {
                throw new InvalidDataException("Active Revit source is not inside a workspace directory.");
            }
            string prefix = workspace.FullName.TrimEnd(Path.DirectorySeparatorChar)
                + Path.DirectorySeparatorChar;
            if (!output.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
                || string.Equals(output, source, StringComparison.OrdinalIgnoreCase)
                || !string.Equals(Path.GetExtension(output), ".rvt", StringComparison.OrdinalIgnoreCase)
                || File.Exists(output) || Directory.Exists(output))
            {
                throw new InvalidDataException("Approved Revit output must be a new workspace RVT.");
            }
            DirectoryInfo parent = new FileInfo(output).Directory;
            if (parent == null || !parent.Exists)
            {
                throw new InvalidDataException("Approved Revit output directory must already exist.");
            }
            for (DirectoryInfo item = parent; item != null; item = item.Parent)
            {
                if ((item.Attributes & FileAttributes.ReparsePoint) != 0)
                {
                    throw new InvalidDataException("Approved Revit output path contains a reparse point.");
                }
                if (string.Equals(item.FullName, workspace.FullName, StringComparison.OrdinalIgnoreCase))
                {
                    return output;
                }
            }
            throw new InvalidDataException("Approved Revit output escaped the workspace directory.");
        }

        private static void ValidatePreview(ModelLinePreview preview, bool requireUnexpired)
        {
            DateTime expires;
            if (preview == null || preview.SchemaVersion != 1 || preview.Segments == null
                || preview.Segments.Count != 4 || preview.ExecutionAllowed || preview.SaveAllowed
                || !string.Equals(preview.Action, "create_model_lines", StringComparison.Ordinal)
                || !DateTime.TryParse(preview.ExpiresUtc, null,
                    System.Globalization.DateTimeStyles.RoundtripKind, out expires)
                || (requireUnexpired && expires.ToUniversalTime() <= DateTime.UtcNow))
            {
                throw new InvalidDataException("Approved Revit handoff preview is invalid or expired.");
            }
        }

        private static T ReadJson<T>(string path)
        {
            if (!File.Exists(path) || new FileInfo(path).Length > 65536)
            {
                throw new InvalidDataException("Revit handoff evidence is missing or oversized.");
            }
            DataContractJsonSerializer serializer = new DataContractJsonSerializer(typeof(T));
            using (FileStream stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                return (T)serializer.ReadObject(stream);
            }
        }

        private static XYZ Point(double[] value)
        {
            if (value == null || value.Length != 3)
            {
                throw new InvalidDataException("Model Line point is invalid.");
            }
            return new XYZ(
                UnitUtils.ConvertToInternalUnits(value[0], UnitTypeId.Millimeters),
                UnitUtils.ConvertToInternalUnits(value[1], UnitTypeId.Millimeters),
                UnitUtils.ConvertToInternalUnits(value[2], UnitTypeId.Millimeters));
        }

        private static bool SameSegment(XYZ a, XYZ b, XYZ expectedA, XYZ expectedB)
        {
            double tolerance = UnitUtils.ConvertToInternalUnits(0.01, UnitTypeId.Millimeters);
            return (a.DistanceTo(expectedA) <= tolerance && b.DistanceTo(expectedB) <= tolerance)
                || (a.DistanceTo(expectedB) <= tolerance && b.DistanceTo(expectedA) <= tolerance);
        }

        private static string WriteJson(string folder, string requestId, object value, Type type)
        {
            string directory = Path.Combine(BridgeRoot(), folder);
            Directory.CreateDirectory(directory);
            string name = requestId + ".json";
            string path = Path.Combine(directory, name);
            DataContractJsonSerializer serializer = new DataContractJsonSerializer(type);
            using (FileStream stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                serializer.WriteObject(stream, value);
                stream.Flush(true);
            }
            return folder + "/" + name;
        }

        private static string BridgeRoot()
        {
            return Path.Combine(Environment.GetFolderPath(
                Environment.SpecialFolder.LocalApplicationData), "AI-Engineer", "revit");
        }

        private static string HashFile(string path)
        {
            using (SHA256 algorithm = SHA256.Create())
            using (FileStream stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            {
                return BitConverter.ToString(algorithm.ComputeHash(stream)).Replace("-", string.Empty);
            }
        }
    }
}
