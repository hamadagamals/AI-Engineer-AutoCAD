using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Security.Cryptography;

using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Electrical;

namespace AIEngineer.Revit
{
    [DataContract]
    internal sealed class CircuitPreviewFixture
    {
        [DataMember(Name = "family_name", Order = 1)] public string FamilyName;
        [DataMember(Name = "type_name", Order = 2)] public string TypeName;
        [DataMember(Name = "mark", Order = 3)] public string Mark;
        [DataMember(Name = "connectors", Order = 4)] public List<LightingConnectorReport> Connectors;
    }

    [DataContract]
    internal sealed class CircuitPreviewChecks
    {
        [DataMember(Name = "fixture_unassigned", Order = 1)] public bool FixtureUnassigned;
        [DataMember(Name = "same_family_and_type", Order = 2)] public bool SameFamilyAndType;
        [DataMember(Name = "connector_signature_match", Order = 3)] public bool ConnectorSignatureMatch;
        [DataMember(Name = "power_circuit_connector_present", Order = 4)] public bool PowerCircuitConnectorPresent;
        [DataMember(Name = "target_membership_confirmed_on_reference", Order = 5)] public bool TargetMembershipConfirmed;
    }

    [DataContract]
    internal sealed class CircuitAssignmentPreview
    {
        [DataMember(Name = "schema_version", Order = 1)] public int SchemaVersion;
        [DataMember(Name = "preview_id", Order = 2)] public string PreviewId;
        [DataMember(Name = "expires_utc", Order = 3)] public string ExpiresUtc;
        [DataMember(Name = "action", Order = 4)] public string Action;
        [DataMember(Name = "document_title", Order = 5)] public string DocumentTitle;
        [DataMember(Name = "source_sha256", Order = 6)] public string SourceSha256;
        [DataMember(Name = "fixture_element_id", Order = 7)] public long FixtureElementId;
        [DataMember(Name = "reference_element_id", Order = 8)] public long ReferenceElementId;
        [DataMember(Name = "target_system_id", Order = 9)] public long TargetSystemId;
        [DataMember(Name = "target_system", Order = 10)] public LightingCircuitReport TargetSystem;
        [DataMember(Name = "fixture", Order = 11)] public CircuitPreviewFixture Fixture;
        [DataMember(Name = "reference", Order = 12)] public CircuitPreviewFixture Reference;
        [DataMember(Name = "checks", Order = 13)] public CircuitPreviewChecks Checks;
        [DataMember(Name = "unresolved_reviews", Order = 14)] public List<string> UnresolvedReviews;
        [DataMember(Name = "status", Order = 15)] public string Status;
        [DataMember(Name = "execution_allowed", Order = 16)] public bool ExecutionAllowed;
        [DataMember(Name = "provider_egress_performed", Order = 17)] public bool ProviderEgressPerformed;
        [DataMember(Name = "document_mutation_performed", Order = 18)] public bool DocumentMutationPerformed;
        [DataMember(Name = "preview_sha256", Order = 19)] public string PreviewSha256;
    }

    [DataContract]
    internal sealed class CircuitAssignmentHandoffReport
    {
        [DataMember(Name = "schema_version", Order = 1)] public int SchemaVersion;
        [DataMember(Name = "request_id", Order = 2)] public string RequestId;
        [DataMember(Name = "preview_id", Order = 3)] public string PreviewId;
        [DataMember(Name = "preview_sha256", Order = 4)] public string PreviewSha256;
        [DataMember(Name = "source_sha256", Order = 5)] public string SourceSha256;
        [DataMember(Name = "output_path", Order = 6)] public string OutputPath;
        [DataMember(Name = "output_sha256", Order = 7)] public string OutputSha256;
        [DataMember(Name = "fixture_element_id", Order = 8)] public long FixtureElementId;
        [DataMember(Name = "target_system_id", Order = 9)] public long TargetSystemId;
        [DataMember(Name = "panel_name", Order = 10)] public string PanelName;
        [DataMember(Name = "circuit_number", Order = 11)] public string CircuitNumber;
        [DataMember(Name = "assignment_verified", Order = 12)] public bool AssignmentVerified;
        [DataMember(Name = "transaction_committed", Order = 13)] public bool TransactionCommitted;
        [DataMember(Name = "saved_as_new_file", Order = 14)] public bool SavedAsNewFile;
        [DataMember(Name = "provider_egress_performed", Order = 15)] public bool ProviderEgressPerformed;
        [DataMember(Name = "document_mutation_performed", Order = 16)] public bool DocumentMutationPerformed;
    }

    [DataContract]
    internal sealed class CircuitAssignmentVerificationReport
    {
        [DataMember(Name = "schema_version", Order = 1)] public int SchemaVersion;
        [DataMember(Name = "request_id", Order = 2)] public string RequestId;
        [DataMember(Name = "handoff_request_id", Order = 3)] public string HandoffRequestId;
        [DataMember(Name = "output_sha256", Order = 4)] public string OutputSha256;
        [DataMember(Name = "fixture_element_id", Order = 5)] public long FixtureElementId;
        [DataMember(Name = "target_system_id", Order = 6)] public long TargetSystemId;
        [DataMember(Name = "assignment_matches_preview", Order = 7)] public bool AssignmentMatchesPreview;
        [DataMember(Name = "provider_egress_performed", Order = 8)] public bool ProviderEgressPerformed;
        [DataMember(Name = "document_mutation_performed", Order = 9)] public bool DocumentMutationPerformed;
    }

    internal static class CircuitAssignmentHandoff
    {
        public static string Execute(Document document, string requestId, string previewPath, string outputPath)
        {
            CircuitAssignmentPreview preview = Read<CircuitAssignmentPreview>(previewPath);
            ValidatePreview(preview, true);
            string output = ModelLineHandoff.ValidateNewOutput(document.PathName, outputPath);
            FamilyInstance fixture;
            ElectricalSystem system;
            Resolve(document, preview, true, out fixture, out system);
            ElementSet elements = new ElementSet();
            elements.Insert(fixture);
            using (Transaction transaction = new Transaction(document, "AI-Engineer approved circuit assignment"))
            {
                transaction.Start();
                if (!system.AddToCircuit(elements))
                {
                    transaction.RollBack();
                    throw new InvalidOperationException("Revit rejected the approved fixture circuit assignment.");
                }
                document.Regenerate();
                if (!HasSystem(fixture, preview.TargetSystemId))
                {
                    transaction.RollBack();
                    throw new InvalidDataException("Revit did not create the exact approved circuit membership.");
                }
                if (transaction.Commit() != TransactionStatus.Committed)
                {
                    throw new InvalidOperationException("Revit circuit assignment transaction did not commit.");
                }
            }
            SaveAsOptions options = new SaveAsOptions { OverwriteExistingFile = false };
            document.SaveAs(output, options);
            if (!File.Exists(output)) throw new IOException("Revit Save As did not create the circuit output.");
            CircuitAssignmentHandoffReport report = new CircuitAssignmentHandoffReport
            {
                SchemaVersion = 1,
                RequestId = requestId,
                PreviewId = preview.PreviewId,
                PreviewSha256 = preview.PreviewSha256,
                SourceSha256 = preview.SourceSha256,
                OutputPath = output,
                OutputSha256 = Hash(output),
                FixtureElementId = preview.FixtureElementId,
                TargetSystemId = preview.TargetSystemId,
                PanelName = system.PanelName,
                CircuitNumber = system.CircuitNumber,
                AssignmentVerified = true,
                TransactionCommitted = true,
                SavedAsNewFile = true,
                ProviderEgressPerformed = false,
                DocumentMutationPerformed = true
            };
            return Write("circuit-assignment-handoffs", requestId, report, typeof(CircuitAssignmentHandoffReport));
        }

        public static string Verify(Document document, string requestId, string handoffRequestId, string previewPath)
        {
            CircuitAssignmentHandoffReport handoff = Read<CircuitAssignmentHandoffReport>(Path.Combine(
                BridgeRoot(), "circuit-assignment-handoffs", handoffRequestId + ".json"));
            CircuitAssignmentPreview preview = Read<CircuitAssignmentPreview>(previewPath);
            ValidatePreview(preview, false);
            string current = Path.GetFullPath(document.PathName);
            if (!handoff.SavedAsNewFile
                || !string.Equals(current, handoff.OutputPath, StringComparison.OrdinalIgnoreCase)
                || !string.Equals(Hash(current), handoff.OutputSha256, StringComparison.OrdinalIgnoreCase)
                || handoff.FixtureElementId != preview.FixtureElementId
                || handoff.TargetSystemId != preview.TargetSystemId
                || !string.Equals(handoff.PreviewId, preview.PreviewId, StringComparison.Ordinal)
                || !string.Equals(handoff.PreviewSha256, preview.PreviewSha256, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException("Reopened circuit handoff identity does not match its evidence.");
            }
            FamilyInstance fixture;
            ElectricalSystem system;
            Resolve(document, preview, false, out fixture, out system);
            CircuitAssignmentVerificationReport report = new CircuitAssignmentVerificationReport
            {
                SchemaVersion = 1,
                RequestId = requestId,
                HandoffRequestId = handoffRequestId,
                OutputSha256 = handoff.OutputSha256,
                FixtureElementId = preview.FixtureElementId,
                TargetSystemId = preview.TargetSystemId,
                AssignmentMatchesPreview = true,
                ProviderEgressPerformed = false,
                DocumentMutationPerformed = false
            };
            return Write("circuit-assignment-verifications", requestId, report,
                typeof(CircuitAssignmentVerificationReport));
        }

        private static void Resolve(Document document, CircuitAssignmentPreview preview,
            bool requireUnassigned, out FamilyInstance fixture, out ElectricalSystem system)
        {
            fixture = document.GetElement(new ElementId(preview.FixtureElementId)) as FamilyInstance;
            FamilyInstance reference = document.GetElement(new ElementId(preview.ReferenceElementId)) as FamilyInstance;
            system = document.GetElement(new ElementId(preview.TargetSystemId)) as ElectricalSystem;
            FamilySymbol fixtureSymbol = fixture == null ? null : fixture.Symbol;
            FamilySymbol referenceSymbol = reference == null ? null : reference.Symbol;
            if (fixture == null || reference == null || system == null || fixtureSymbol == null
                || referenceSymbol == null || fixtureSymbol.Family == null || referenceSymbol.Family == null
                || !string.Equals(fixtureSymbol.Family.Name, preview.Fixture.FamilyName, StringComparison.Ordinal)
                || !string.Equals(fixtureSymbol.Name, preview.Fixture.TypeName, StringComparison.Ordinal)
                || !string.Equals(referenceSymbol.Family.Name, preview.Reference.FamilyName, StringComparison.Ordinal)
                || !string.Equals(referenceSymbol.Name, preview.Reference.TypeName, StringComparison.Ordinal)
                || fixtureSymbol.Id.Value != referenceSymbol.Id.Value
                || !HasSystem(reference, preview.TargetSystemId)
                || !string.Equals(system.SystemType.ToString(), "PowerCircuit", StringComparison.Ordinal)
                || !string.Equals(system.CircuitNumber, preview.TargetSystem.CircuitNumber, StringComparison.Ordinal)
                || !string.Equals(system.PanelName, preview.TargetSystem.PanelName, StringComparison.Ordinal)
                || !ConnectorsMatch(fixture, reference, preview)
                || (requireUnassigned && SystemCount(fixture) != 0)
                || (!requireUnassigned && !HasSystem(fixture, preview.TargetSystemId)))
            {
                throw new InvalidDataException("Revit circuit identities or compatibility changed from the preview.");
            }
        }

        private static bool ConnectorsMatch(FamilyInstance fixture, FamilyInstance reference,
            CircuitAssignmentPreview preview)
        {
            List<string> fixtureLive = ConnectorSignature(fixture);
            List<string> referenceLive = ConnectorSignature(reference);
            List<string> fixturePreview = ConnectorSignature(preview.Fixture.Connectors);
            List<string> referencePreview = ConnectorSignature(preview.Reference.Connectors);
            return fixtureLive.Count > 0
                && fixtureLive.Contains("1|DomainElectrical|End|PowerCircuit")
                && Equal(fixtureLive, referenceLive)
                && Equal(fixtureLive, fixturePreview)
                && Equal(fixturePreview, referencePreview);
        }

        private static List<string> ConnectorSignature(FamilyInstance fixture)
        {
            List<string> values = new List<string>();
            ConnectorManager manager = fixture.MEPModel == null ? null : fixture.MEPModel.ConnectorManager;
            if (manager != null && manager.Connectors != null)
            {
                foreach (Connector connector in manager.Connectors)
                {
                    if (connector != null) values.Add(connector.Id + "|" + connector.Domain + "|"
                        + connector.ConnectorType + "|" + (connector.Domain == Domain.DomainElectrical
                            ? connector.ElectricalSystemType.ToString() : string.Empty));
                }
            }
            values.Sort(StringComparer.Ordinal);
            return values;
        }

        private static List<string> ConnectorSignature(List<LightingConnectorReport> connectors)
        {
            List<string> values = new List<string>();
            if (connectors != null)
            {
                foreach (LightingConnectorReport connector in connectors)
                    values.Add(connector.ConnectorId + "|" + connector.Domain + "|"
                        + connector.ConnectorType + "|" + connector.ElectricalSystemType);
            }
            values.Sort(StringComparer.Ordinal);
            return values;
        }

        private static bool Equal(List<string> left, List<string> right)
        {
            if (left.Count != right.Count) return false;
            for (int index = 0; index < left.Count; index++)
                if (!string.Equals(left[index], right[index], StringComparison.Ordinal)) return false;
            return true;
        }

        private static int SystemCount(FamilyInstance fixture)
        {
            ISet<ElectricalSystem> systems = fixture.MEPModel == null
                ? null : fixture.MEPModel.GetElectricalSystems();
            return systems == null ? 0 : systems.Count;
        }

        private static bool HasSystem(FamilyInstance fixture, long systemId)
        {
            ISet<ElectricalSystem> systems = fixture.MEPModel == null
                ? null : fixture.MEPModel.GetElectricalSystems();
            if (systems == null) return false;
            foreach (ElectricalSystem system in systems)
                if (system != null && system.Id.Value == systemId) return true;
            return false;
        }

        private static void ValidatePreview(CircuitAssignmentPreview preview, bool requireUnexpired)
        {
            DateTime expires;
            CircuitPreviewChecks checks = preview == null ? null : preview.Checks;
            if (preview == null || preview.SchemaVersion != 1 || preview.FixtureElementId <= 0
                || preview.ReferenceElementId <= 0 || preview.TargetSystemId <= 0
                || preview.Fixture == null || preview.Reference == null || preview.TargetSystem == null
                || preview.TargetSystem.SystemId != preview.TargetSystemId
                || checks == null || !checks.FixtureUnassigned || !checks.SameFamilyAndType
                || !checks.ConnectorSignatureMatch || !checks.PowerCircuitConnectorPresent
                || !checks.TargetMembershipConfirmed || preview.ExecutionAllowed
                || preview.ProviderEgressPerformed || preview.DocumentMutationPerformed
                || !string.Equals(preview.Action,
                    "add_existing_fixture_to_existing_electrical_system", StringComparison.Ordinal)
                || !string.Equals(preview.Status, "review_required", StringComparison.Ordinal)
                || string.IsNullOrWhiteSpace(preview.PreviewId) || string.IsNullOrWhiteSpace(preview.PreviewSha256)
                || !DateTime.TryParse(preview.ExpiresUtc, null,
                    System.Globalization.DateTimeStyles.RoundtripKind, out expires)
                || (requireUnexpired && expires.ToUniversalTime() <= DateTime.UtcNow))
            {
                throw new InvalidDataException("Approved Revit circuit preview is invalid or expired.");
            }
        }

        private static T Read<T>(string path)
        {
            if (!File.Exists(path) || new FileInfo(path).Length > 65536)
                throw new InvalidDataException("Revit circuit evidence is missing or oversized.");
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
            {
                serializer.WriteObject(stream, value);
                stream.Flush(true);
            }
            return folder + "/" + requestId + ".json";
        }

        private static string BridgeRoot()
        {
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "AI-Engineer", "revit");
        }

        private static string Hash(string path)
        {
            using (SHA256 algorithm = SHA256.Create())
            using (FileStream stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                return BitConverter.ToString(algorithm.ComputeHash(stream)).Replace("-", string.Empty);
        }
    }
}
