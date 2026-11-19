using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;

using Autodesk.Revit.DB;

namespace AIEngineer.Revit
{
    [DataContract]
    internal sealed class PreviewSegment
    {
        [DataMember(Name = "index", Order = 1)] public int Index;
        [DataMember(Name = "start_mm", Order = 2)] public double[] Start;
        [DataMember(Name = "end_mm", Order = 3)] public double[] End;
        [DataMember(Name = "length_mm", Order = 4)] public double Length;
    }

    [DataContract]
    internal sealed class ModelLinePreview
    {
        [DataMember(Name = "schema_version", Order = 1)] public int SchemaVersion;
        [DataMember(Name = "preview_id", Order = 2)] public string PreviewId;
        [DataMember(Name = "expires_utc", Order = 3)] public string ExpiresUtc;
        [DataMember(Name = "source_sha256", Order = 4)] public string SourceSha256;
        [DataMember(Name = "document_title", Order = 5)] public string DocumentTitle;
        [DataMember(Name = "action", Order = 6)] public string Action;
        [DataMember(Name = "segments", Order = 7)] public List<PreviewSegment> Segments;
        [DataMember(Name = "status", Order = 8)] public string Status;
        [DataMember(Name = "execution_allowed", Order = 9)] public bool ExecutionAllowed;
        [DataMember(Name = "save_allowed", Order = 10)] public bool SaveAllowed;
        [DataMember(Name = "preview_sha256", Order = 11)] public string PreviewSha256;
    }

    [DataContract]
    internal sealed class ModelLineExecutionReport
    {
        [DataMember(Name = "schema_version", Order = 1)] public int SchemaVersion;
        [DataMember(Name = "request_id", Order = 2)] public string RequestId;
        [DataMember(Name = "preview_id", Order = 3)] public string PreviewId;
        [DataMember(Name = "document_title", Order = 4)] public string DocumentTitle;
        [DataMember(Name = "created_model_lines", Order = 5)] public int CreatedModelLines;
        [DataMember(Name = "created_element_ids", Order = 6)] public List<long> CreatedElementIds;
        [DataMember(Name = "transaction_committed", Order = 7)] public bool TransactionCommitted;
        [DataMember(Name = "saved", Order = 8)] public bool Saved;
        [DataMember(Name = "provider_egress_performed", Order = 9)] public bool ProviderEgressPerformed;
        [DataMember(Name = "document_mutation_performed", Order = 10)] public bool DocumentMutationPerformed;
    }

    internal static class ModelLineExecution
    {
        public static string Execute(Document document, string requestId, string previewPath)
        {
            ModelLinePreview preview = ReadPreview(previewPath);
            if (preview.SchemaVersion != 1
                || preview.PreviewId == null
                || !string.Equals(preview.Action, "create_model_lines", StringComparison.Ordinal)
                || !string.Equals(preview.Status, "review_required", StringComparison.Ordinal)
                || preview.ExecutionAllowed
                || preview.SaveAllowed
                || preview.Segments == null
                || preview.Segments.Count < 1
                || preview.Segments.Count > 16)
            {
                throw new InvalidDataException("Approved Revit Model Lines preview is invalid.");
            }
            DateTime expires;
            if (!DateTime.TryParse(preview.ExpiresUtc, null, System.Globalization.DateTimeStyles.RoundtripKind, out expires)
                || expires.ToUniversalTime() <= DateTime.UtcNow)
            {
                throw new InvalidDataException("Approved Revit preview has expired.");
            }

            List<long> identifiers = new List<long>();
            using (Transaction transaction = new Transaction(document, "AI-Engineer approved Model Lines"))
            {
                transaction.Start();
                foreach (PreviewSegment segment in preview.Segments)
                {
                    XYZ start = Point(segment.Start);
                    XYZ end = Point(segment.End);
                    if (Math.Abs(start.Z - end.Z) > 0.00000328084 || start.DistanceTo(end) < 0.0328084)
                    {
                        throw new InvalidDataException("Model Line segment is outside approved geometry bounds.");
                    }
                    SketchPlane plane = SketchPlane.Create(
                        document, Plane.CreateByNormalAndOrigin(XYZ.BasisZ, start));
                    ModelCurve curve = document.Create.NewModelCurve(Line.CreateBound(start, end), plane);
                    identifiers.Add(curve.Id.Value);
                }
                if (identifiers.Count != preview.Segments.Count)
                {
                    transaction.RollBack();
                    throw new InvalidOperationException("Created Model Line count did not match preview.");
                }
                transaction.Commit();
            }

            ModelLineExecutionReport report = new ModelLineExecutionReport
            {
                SchemaVersion = 1,
                RequestId = requestId,
                PreviewId = preview.PreviewId,
                DocumentTitle = document.Title,
                CreatedModelLines = identifiers.Count,
                CreatedElementIds = identifiers,
                TransactionCommitted = true,
                Saved = false,
                ProviderEgressPerformed = false,
                DocumentMutationPerformed = true
            };
            string root = Path.Combine(Environment.GetFolderPath(
                Environment.SpecialFolder.LocalApplicationData), "AI-Engineer", "revit", "executions");
            Directory.CreateDirectory(root);
            string name = requestId + ".json";
            string output = Path.Combine(root, name);
            DataContractJsonSerializer serializer = new DataContractJsonSerializer(typeof(ModelLineExecutionReport));
            using (FileStream stream = new FileStream(output, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                serializer.WriteObject(stream, report);
                stream.Flush(true);
            }
            return "executions/" + name;
        }

        private static ModelLinePreview ReadPreview(string path)
        {
            DataContractJsonSerializer serializer = new DataContractJsonSerializer(typeof(ModelLinePreview));
            using (FileStream stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                return (ModelLinePreview)serializer.ReadObject(stream);
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
    }
}
