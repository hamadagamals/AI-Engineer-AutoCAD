using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Security.Cryptography;

using Autodesk.Revit.UI;

namespace AIEngineer.Revit
{
    internal sealed class ReadOnlyRequestExternalEventHandler : IExternalEventHandler
    {
        public void Execute(UIApplication application)
        {
            ReadOnlyRequestBridge.ProcessSafely(application);
        }

        public string GetName()
        {
            return "AI-Engineer Revit Read-Only Request";
        }
    }

    [DataContract]
    internal sealed class ScratchFamilyItem
    {
        [DataMember(Name = "design_id", Order = 1)] public string DesignId;
        [DataMember(Name = "rfa_relative_path", Order = 2)] public string RfaRelativePath;
        [DataMember(Name = "type_name", Order = 3)] public string TypeName;
        [DataMember(Name = "x_mm", Order = 4)] public double Xmm;
        [DataMember(Name = "y_mm", Order = 5)] public double Ymm;
        [DataMember(Name = "z_mm", Order = 6)] public double Zmm;
    }

    [DataContract]
    internal sealed class BridgeRequest
    {
        [DataMember(Name = "schema_version", Order = 1)] public int SchemaVersion;
        [DataMember(Name = "request_id", Order = 2)] public string RequestId;
        [DataMember(Name = "action", Order = 3)] public string Action;
        [DataMember(Name = "expected_document_title", Order = 4)] public string ExpectedDocumentTitle;
        [DataMember(Name = "provider_egress_permitted", Order = 5)] public bool ProviderEgressPermitted;
        [DataMember(Name = "document_mutation_permitted", Order = 6)] public bool DocumentMutationPermitted;
        [DataMember(Name = "category_key", Order = 7)] public string CategoryKey;
        [DataMember(Name = "max_elements", Order = 8)] public int MaxElements;
        [DataMember(Name = "approval_granted", Order = 9)] public bool ApprovalGranted;
        [DataMember(Name = "preview_id", Order = 10)] public string PreviewId;
        [DataMember(Name = "preview_sha256", Order = 11)] public string PreviewSha256;
        [DataMember(Name = "preview_file_sha256", Order = 12)] public string PreviewFileSha256;
        [DataMember(Name = "source_sha256", Order = 13)] public string SourceSha256;
        [DataMember(Name = "save_allowed", Order = 14)] public bool SaveAllowed;
        [DataMember(Name = "output_path", Order = 15)] public string OutputPath;
        [DataMember(Name = "handoff_request_id", Order = 16)] public string HandoffRequestId;
        [DataMember(Name = "host_filter", Order = 17)] public string HostFilter;
        [DataMember(Name = "reference_element_id", Order = 18)] public long ReferenceElementId;
        [DataMember(Name = "face_stable_reference", Order = 19)] public string FaceStableReference;
        [DataMember(Name = "start_after_element_id", Order = 20)] public long StartAfterElementId;
        [DataMember(Name = "family_files", Order = 21)] public List<string> FamilyFiles;
        [DataMember(Name = "scratch_family_items", Order = 22)] public List<ScratchFamilyItem> ScratchFamilyItems;
        [DataMember(Name = "ies_relative_path", Order = 23)] public string IesRelativePath;
        [DataMember(Name = "ies_sha256", Order = 24)] public string IesSha256;
        [DataMember(Name = "ies_relative_path_2", Order = 25)] public string IesRelativePath2;
        [DataMember(Name = "ies_sha256_2", Order = 26)] public string IesSha2562;
        [DataMember(Name = "school_spots", Order = 27)] public List<SchoolSpotInput> SchoolSpots;
    }

    [DataContract]
    internal sealed class BridgeResponse
    {
        [DataMember(Name = "schema_version", Order = 1)] public int SchemaVersion;
        [DataMember(Name = "request_id", Order = 2)] public string RequestId;
        [DataMember(Name = "action", Order = 3)] public string Action;
        [DataMember(Name = "status", Order = 4)] public string Status;
        [DataMember(Name = "document_title", Order = 5)] public string DocumentTitle;
        [DataMember(Name = "output_file", Order = 6)] public string OutputFile;
        [DataMember(Name = "provider_egress_performed", Order = 7)] public bool ProviderEgressPerformed;
        [DataMember(Name = "document_mutation_performed", Order = 8)] public bool DocumentMutationPerformed;
        [DataMember(Name = "error", Order = 9)] public string Error;
    }

    internal static class ReadOnlyRequestBridge
    {
        private const int MaximumRequestBytes = 65536;

        public static void ProcessSafely(UIApplication uiApplication)
        {
            try
            {
                TryProcess(uiApplication);
            }
            catch (Exception exception)
            {
                string root = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "AI-Engineer", "revit");
                Directory.CreateDirectory(root);
                string message = exception.GetType().Name + ": " + exception.Message;
                if (message.Length > 1000)
                {
                    message = message.Substring(0, 1000);
                }
                File.WriteAllText(
                    Path.Combine(root, "bridge-error.txt"),
                    message,
                    System.Text.Encoding.UTF8);
            }
        }

        public static void TryProcess(UIApplication uiApplication)
        {
            string root = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "AI-Engineer", "revit");
            string requests = Path.Combine(root, "requests");
            string pending = Path.Combine(requests, "pending.json");
            if (!File.Exists(pending))
            {
                return;
            }

            Directory.CreateDirectory(requests);
            BridgeRequest request = ReadRequest(pending);
            Guid requestId;
            if (request.SchemaVersion != 1
                || !Guid.TryParse(request.RequestId, out requestId)
                || !(string.Equals(request.Action, "inventory", StringComparison.Ordinal)
                    || string.Equals(request.Action, "category_query", StringComparison.Ordinal)
                    || string.Equals(request.Action, "host_face_query", StringComparison.Ordinal)
                    || string.Equals(request.Action, "element_visual_query", StringComparison.Ordinal)
                    || string.Equals(request.Action, "lighting_circuit_query", StringComparison.Ordinal)
                    || string.Equals(request.Action, "electrical_system_query", StringComparison.Ordinal)
                    || string.Equals(request.Action, "family_type_query", StringComparison.Ordinal)
                    || string.Equals(request.Action, "family_file_query", StringComparison.Ordinal)
                    || string.Equals(request.Action, "school_spot_query", StringComparison.Ordinal)
                    || string.Equals(request.Action, "school_lighting_door_audit", StringComparison.Ordinal)
                    || string.Equals(request.Action, "school_v5_layout_preview", StringComparison.Ordinal)
                    || string.Equals(request.Action, "school_v5_power_mark_query", StringComparison.Ordinal)
                    || string.Equals(request.Action, "school_v5_outlet_connector_query", StringComparison.Ordinal)
                    || string.Equals(request.Action, "execute_scratch_family_validation", StringComparison.Ordinal)
                    || string.Equals(request.Action, "execute_school_outlet_220v_isolated_test", StringComparison.Ordinal)
                    || string.Equals(request.Action, "execute_l1_parameter_update", StringComparison.Ordinal)
                    || string.Equals(request.Action, "execute_l2_l5_l6_parameter_update", StringComparison.Ordinal)
                    || string.Equals(request.Action, "execute_model_lines", StringComparison.Ordinal)
                    || string.Equals(request.Action, "execute_model_lines_save_as", StringComparison.Ordinal)
                    || string.Equals(request.Action, "verify_model_lines_handoff", StringComparison.Ordinal)
                    || string.Equals(request.Action, "execute_family_placement_save_as", StringComparison.Ordinal)
                    || string.Equals(request.Action, "verify_family_placement_handoff", StringComparison.Ordinal)
                    || string.Equals(request.Action, "execute_fixture_mark_correction_save_as", StringComparison.Ordinal)
                    || string.Equals(request.Action, "verify_fixture_mark_correction_handoff", StringComparison.Ordinal)
                    || string.Equals(request.Action, "execute_school_l4_batch1_save_as", StringComparison.Ordinal)
                    || string.Equals(request.Action, "verify_school_l4_batch1_handoff", StringComparison.Ordinal)
                    || string.Equals(request.Action, "execute_school_l4_batch2_save_as", StringComparison.Ordinal)
                    || string.Equals(request.Action, "verify_school_l4_batch2_handoff", StringComparison.Ordinal)
                    || string.Equals(request.Action, "execute_school_classroom_sample_save_as", StringComparison.Ordinal)
                    || string.Equals(request.Action, "verify_school_classroom_sample_handoff", StringComparison.Ordinal)
                    || string.Equals(request.Action, "execute_school_distribution_review_save_as", StringComparison.Ordinal)
                    || string.Equals(request.Action, "execute_school_lighting_review_v2_save_as", StringComparison.Ordinal)
                    || string.Equals(request.Action, "execute_school_electrical_review_v3_save_as", StringComparison.Ordinal)
                    || string.Equals(request.Action, "execute_school_electrical_review_v4_save_as", StringComparison.Ordinal)
                    || string.Equals(request.Action, "execute_school_electrical_review_v5_save_as", StringComparison.Ordinal)
                    || string.Equals(request.Action, "execute_hosted_family_placement_save_as", StringComparison.Ordinal)
                    || string.Equals(request.Action, "verify_hosted_family_placement_handoff", StringComparison.Ordinal)
                    || string.Equals(request.Action, "execute_circuit_assignment_save_as", StringComparison.Ordinal)
                    || string.Equals(request.Action, "verify_circuit_assignment_handoff", StringComparison.Ordinal))
                || string.IsNullOrWhiteSpace(request.ExpectedDocumentTitle)
                || request.ExpectedDocumentTitle.Length > 260
                || request.ProviderEgressPermitted)
            {
                throw new InvalidDataException("Revit bridge request is outside the read-only contract.");
            }

            string processing = Path.Combine(requests, "processing-" + request.RequestId + ".json");
            File.Move(pending, processing);
            BridgeResponse response = new BridgeResponse
            {
                SchemaVersion = 1,
                RequestId = request.RequestId,
                Action = request.Action,
                Status = "failed",
                DocumentTitle = string.Empty,
                OutputFile = string.Empty,
                ProviderEgressPerformed = false,
                DocumentMutationPerformed = false,
                Error = string.Empty
            };
            try
            {
                if (uiApplication.ActiveUIDocument == null)
                {
                    throw new InvalidOperationException("No active Revit document is available.");
                }
                string title = uiApplication.ActiveUIDocument.Document.Title;
                response.DocumentTitle = title;
                if (!string.Equals(title, request.ExpectedDocumentTitle, StringComparison.Ordinal))
                {
                    throw new InvalidOperationException("The active Revit document does not match the request.");
                }
                bool execution = string.Equals(request.Action, "execute_model_lines", StringComparison.Ordinal)
                    || string.Equals(request.Action, "execute_model_lines_save_as", StringComparison.Ordinal)
                    || string.Equals(request.Action, "execute_family_placement_save_as", StringComparison.Ordinal)
                    || string.Equals(request.Action, "execute_fixture_mark_correction_save_as", StringComparison.Ordinal)
                    || string.Equals(request.Action, "execute_school_l4_batch1_save_as", StringComparison.Ordinal)
                    || string.Equals(request.Action, "execute_school_l4_batch2_save_as", StringComparison.Ordinal)
                    || string.Equals(request.Action, "execute_school_classroom_sample_save_as", StringComparison.Ordinal)
                    || string.Equals(request.Action, "execute_school_distribution_review_save_as", StringComparison.Ordinal)
                    || string.Equals(request.Action, "execute_school_lighting_review_v2_save_as", StringComparison.Ordinal)
                    || string.Equals(request.Action, "execute_school_electrical_review_v3_save_as", StringComparison.Ordinal)
                    || string.Equals(request.Action, "execute_school_electrical_review_v4_save_as", StringComparison.Ordinal)
                    || string.Equals(request.Action, "execute_school_electrical_review_v5_save_as", StringComparison.Ordinal)
                    || string.Equals(request.Action, "execute_hosted_family_placement_save_as", StringComparison.Ordinal)
                    || string.Equals(request.Action, "execute_circuit_assignment_save_as", StringComparison.Ordinal);
                execution = execution || string.Equals(request.Action, "execute_scratch_family_validation", StringComparison.Ordinal);
                execution = execution || string.Equals(request.Action, "execute_school_outlet_220v_isolated_test", StringComparison.Ordinal);
                execution = execution || string.Equals(request.Action, "execute_l1_parameter_update", StringComparison.Ordinal);
                execution = execution || string.Equals(request.Action, "execute_l2_l5_l6_parameter_update", StringComparison.Ordinal);
                if (execution != request.DocumentMutationPermitted)
                {
                    throw new InvalidDataException("Revit mutation permission does not match the fixed action.");
                }
                if (string.Equals(request.Action, "inventory", StringComparison.Ordinal))
                {
                    ReadOnlyInventoryCommand.Capture(uiApplication);
                    response.OutputFile = "latest-inventory.json";
                }
                else if (string.Equals(request.Action, "category_query", StringComparison.Ordinal))
                {
                    if (string.IsNullOrWhiteSpace(request.CategoryKey)
                        || request.CategoryKey.Length > 64
                        || request.MaxElements < 1
                        || request.MaxElements > 32
                        || request.StartAfterElementId < 0
                        || !(string.Equals(request.HostFilter, "all", StringComparison.Ordinal)
                            || string.Equals(request.HostFilter, "hosted", StringComparison.Ordinal)
                            || string.Equals(request.HostFilter, "ceiling_hosted", StringComparison.Ordinal)))
                    {
                        throw new InvalidDataException("Revit category query bounds are invalid.");
                    }
                    response.OutputFile = ReadOnlyCategoryQuery.Capture(
                        uiApplication.ActiveUIDocument.Document,
                        request.RequestId,
                        request.CategoryKey,
                        request.MaxElements,
                        request.HostFilter,
                        request.StartAfterElementId);
                }
                else if (string.Equals(request.Action, "family_type_query", StringComparison.Ordinal))
                {
                    if (string.IsNullOrWhiteSpace(request.CategoryKey)
                        || request.CategoryKey.Length > 64
                        || request.MaxElements < 1
                        || request.MaxElements > 64)
                    {
                        throw new InvalidDataException("Revit family-type query bounds are invalid.");
                    }
                    response.OutputFile = ReadOnlyFamilyTypeQuery.Capture(
                        uiApplication.ActiveUIDocument.Document,
                        request.RequestId,
                        request.CategoryKey,
                        request.MaxElements);
                }
                else if (string.Equals(request.Action, "school_spot_query", StringComparison.Ordinal))
                {
                    if (request.SchoolSpots == null || request.SchoolSpots.Count < 1
                        || request.SchoolSpots.Count > 32
                        || string.IsNullOrWhiteSpace(request.SourceSha256)
                        || request.SourceSha256.Length != 64
                        || request.DocumentMutationPermitted)
                        throw new InvalidDataException("School spot query bounds or document hash are invalid.");
                    response.OutputFile = ReadOnlySchoolSpotQuery.Capture(
                        uiApplication.ActiveUIDocument.Document, request.RequestId,
                        request.SourceSha256, request.SchoolSpots);
                }
                else if (string.Equals(request.Action, "school_lighting_door_audit", StringComparison.Ordinal))
                {
                    if (string.IsNullOrWhiteSpace(request.SourceSha256)
                        || request.SourceSha256.Length != 64
                        || request.DocumentMutationPermitted)
                        throw new InvalidDataException("School lighting door audit bounds are invalid.");
                    response.OutputFile = ReadOnlySchoolLightingDoorAudit.Capture(
                        uiApplication.ActiveUIDocument.Document, request.RequestId,
                        request.SourceSha256);
                }
                else if (string.Equals(request.Action, "school_v5_layout_preview", StringComparison.Ordinal))
                {
                    Guid previewId;
                    if (!Guid.TryParse(request.PreviewId, out previewId)
                        || string.IsNullOrWhiteSpace(request.PreviewFileSha256)
                        || request.PreviewFileSha256.Length != 64
                        || string.IsNullOrWhiteSpace(request.SourceSha256)
                        || request.SourceSha256.Length != 64
                        || request.DocumentMutationPermitted)
                        throw new InvalidDataException("School V5 layout preview binding is invalid.");
                    string inputPath = Path.Combine(root, "review-inputs", previewId + ".json");
                    if (!File.Exists(inputPath)
                        || !string.Equals(HashFile(inputPath), request.PreviewFileSha256,
                            StringComparison.OrdinalIgnoreCase)
                        || !string.Equals(HashFile(uiApplication.ActiveUIDocument.Document.PathName),
                            request.SourceSha256, StringComparison.OrdinalIgnoreCase))
                        throw new InvalidDataException("School V5 preview input or source hash changed.");
                    response.OutputFile = SchoolElectricalReviewV3Handoff.PreviewV5(
                        uiApplication.ActiveUIDocument.Document, request.RequestId, inputPath);
                }
                else if (string.Equals(request.Action, "school_v5_power_mark_query", StringComparison.Ordinal))
                {
                    if (string.IsNullOrWhiteSpace(request.SourceSha256)
                        || request.SourceSha256.Length != 64
                        || request.MaxElements < 1 || request.MaxElements > 32
                        || request.StartAfterElementId < 0 || request.DocumentMutationPermitted)
                        throw new InvalidDataException("School V5 power mark query bounds are invalid.");
                    response.OutputFile = ReadOnlySchoolPowerMarks.Capture(
                        uiApplication.ActiveUIDocument.Document, request.RequestId,
                        request.SourceSha256, request.MaxElements, request.StartAfterElementId);
                }
                else if (string.Equals(request.Action, "school_v5_outlet_connector_query", StringComparison.Ordinal))
                {
                    if (string.IsNullOrWhiteSpace(request.SourceSha256)
                        || request.SourceSha256.Length != 64 || request.DocumentMutationPermitted)
                        throw new InvalidDataException("School V5 outlet connector query is invalid.");
                    response.OutputFile = ReadOnlySchoolOutletConnectors.Capture(
                        uiApplication.ActiveUIDocument.Document, request.RequestId,
                        request.SourceSha256);
                }
                else if (string.Equals(request.Action, "family_file_query", StringComparison.Ordinal))
                {
                    if (request.FamilyFiles == null
                        || request.FamilyFiles.Count < 1
                        || request.FamilyFiles.Count > 8)
                    {
                        throw new InvalidDataException("Revit family-file query bounds are invalid.");
                    }
                    response.OutputFile = ReadOnlyFamilyFileQuery.Capture(
                        uiApplication.Application,
                        title,
                        request.RequestId,
                        request.FamilyFiles);
                }
                else if (string.Equals(request.Action, "execute_scratch_family_validation", StringComparison.Ordinal))
                {
                    if (!request.ApprovalGranted || !request.SaveAllowed
                        || !string.Equals(request.PreviewSha256, "3F0ABF992B84973005619711766FDD5F5785DDC631D14DC87843DB42C7D28E7E", StringComparison.Ordinal)
                        || request.ScratchFamilyItems == null || request.ScratchFamilyItems.Count != 4)
                    {
                        throw new InvalidDataException("Scratch family validation approval is invalid.");
                    }
                    response.OutputFile = ScratchFamilyValidation.Execute(
                        uiApplication.Application, request.RequestId, request.ScratchFamilyItems);
                    response.DocumentMutationPerformed = true;
                }
                else if (string.Equals(request.Action, "execute_l1_parameter_update", StringComparison.Ordinal))
                {
                    if (!request.ApprovalGranted || !request.SaveAllowed
                        || !string.Equals(request.PreviewSha256, "310E62C9FBAD3B1AE50A06B3D9983AA10355AABD26718134E527C3EFACFB8FDA", StringComparison.Ordinal)
                        || string.IsNullOrWhiteSpace(request.SourceSha256) || request.SourceSha256.Length != 64
                        || string.IsNullOrWhiteSpace(request.IesRelativePath) || request.IesRelativePath.Length > 400
                        || string.IsNullOrWhiteSpace(request.IesSha256) || request.IesSha256.Length != 64)
                    {
                        throw new InvalidDataException("L1 parameter-update approval is invalid.");
                    }
                    response.OutputFile = ScratchL1ParameterUpdate.Execute(
                        uiApplication.ActiveUIDocument.Document, request.RequestId,
                        request.SourceSha256, request.IesRelativePath, request.IesSha256);
                    response.DocumentMutationPerformed = true;
                }
                else if (string.Equals(request.Action, "execute_l2_l5_l6_parameter_update", StringComparison.Ordinal))
                {
                    if (!request.ApprovalGranted || !request.SaveAllowed
                        || !string.Equals(request.PreviewSha256, "8359BB401FE1EC0221E116948B540AEC4118125E6A0BAB51B44672B22021781D", StringComparison.Ordinal)
                        || string.IsNullOrWhiteSpace(request.SourceSha256) || request.SourceSha256.Length != 64
                        || string.IsNullOrWhiteSpace(request.IesRelativePath) || request.IesRelativePath.Length > 400
                        || string.IsNullOrWhiteSpace(request.IesSha256) || request.IesSha256.Length != 64
                        || string.IsNullOrWhiteSpace(request.IesRelativePath2) || request.IesRelativePath2.Length > 400
                        || string.IsNullOrWhiteSpace(request.IesSha2562) || request.IesSha2562.Length != 64)
                    {
                        throw new InvalidDataException("L2/L5-L6 parameter-update approval is invalid.");
                    }
                    response.OutputFile = ScratchRemainingParameterUpdate.Execute(
                        uiApplication.ActiveUIDocument.Document, request.RequestId, request.SourceSha256,
                        request.IesRelativePath, request.IesSha256,
                        request.IesRelativePath2, request.IesSha2562);
                    response.DocumentMutationPerformed = true;
                }
                else if (string.Equals(request.Action, "host_face_query", StringComparison.Ordinal))
                {
                    if (request.ReferenceElementId <= 0
                        || string.IsNullOrWhiteSpace(request.FaceStableReference)
                        || request.FaceStableReference.Length > 4096
                        || request.MaxElements < 1
                        || request.MaxElements > 8)
                    {
                        throw new InvalidDataException("Revit host-face query bounds are invalid.");
                    }
                    response.OutputFile = ReadOnlyHostFaceQuery.Capture(
                        uiApplication.ActiveUIDocument.Document,
                        request.RequestId,
                        request.ReferenceElementId,
                        request.FaceStableReference,
                        request.MaxElements);
                }
                else if (string.Equals(request.Action, "element_visual_query", StringComparison.Ordinal))
                {
                    if (request.ReferenceElementId <= 0)
                    {
                        throw new InvalidDataException("Revit element visual query identity is invalid.");
                    }
                    response.OutputFile = ReadOnlyElementVisualQuery.CaptureAndFocus(
                        uiApplication, request.RequestId, request.ReferenceElementId);
                }
                else if (string.Equals(request.Action, "lighting_circuit_query", StringComparison.Ordinal))
                {
                    if (request.ReferenceElementId < 0
                        || request.StartAfterElementId < 0
                        || (request.ReferenceElementId > 0 && request.StartAfterElementId > 0)
                        || request.MaxElements < 1
                        || request.MaxElements > 32)
                    {
                        throw new InvalidDataException("Revit lighting circuit query bounds are invalid.");
                    }
                    response.OutputFile = ReadOnlyLightingCircuitQuery.Capture(
                        uiApplication.ActiveUIDocument.Document,
                        request.RequestId,
                        request.ReferenceElementId,
                        request.StartAfterElementId,
                        request.MaxElements);
                }
                else if (string.Equals(request.Action, "electrical_system_query", StringComparison.Ordinal))
                {
                    if (request.StartAfterElementId < 0
                        || request.MaxElements < 1
                        || request.MaxElements > 32)
                    {
                        throw new InvalidDataException("Revit electrical-system query bounds are invalid.");
                    }
                    response.OutputFile = ReadOnlyElectricalSystemQuery.Capture(
                        uiApplication.ActiveUIDocument.Document,
                        request.RequestId,
                        request.StartAfterElementId,
                        request.MaxElements);
                }
                else if (execution)
                {
                    if (!request.ApprovalGranted
                        || !Guid.TryParse(request.PreviewId, out requestId)
                        || string.IsNullOrWhiteSpace(request.PreviewSha256)
                        || string.IsNullOrWhiteSpace(request.PreviewFileSha256)
                        || string.IsNullOrWhiteSpace(request.SourceSha256))
                    {
                        throw new InvalidDataException("Approved Revit execution binding is invalid.");
                    }
                    string previewPath = Path.Combine(
                        root, "approved-previews", request.PreviewId + ".json");
                    if (!File.Exists(previewPath)
                        || !string.Equals(HashFile(previewPath), request.PreviewFileSha256, StringComparison.OrdinalIgnoreCase)
                        || !string.Equals(HashFile(uiApplication.ActiveUIDocument.Document.PathName), request.SourceSha256, StringComparison.OrdinalIgnoreCase))
                    {
                        throw new InvalidDataException("Approved Revit preview or source hash changed.");
                    }
                    if (string.Equals(request.Action, "execute_model_lines", StringComparison.Ordinal))
                    {
                        if (request.SaveAllowed)
                        {
                            throw new InvalidDataException("No-save Model Lines request cannot save.");
                        }
                        response.OutputFile = ModelLineExecution.Execute(
                            uiApplication.ActiveUIDocument.Document, request.RequestId, previewPath);
                    }
                    else if (string.Equals(request.Action, "execute_school_outlet_220v_isolated_test", StringComparison.Ordinal))
                    {
                        if (!request.SaveAllowed || string.IsNullOrWhiteSpace(request.OutputPath)
                            || !string.Equals(request.PreviewSha256, request.PreviewFileSha256,
                                StringComparison.OrdinalIgnoreCase))
                            throw new InvalidDataException("Isolated school outlet test lacks its preview or output binding.");
                        response.OutputFile = SchoolOutletIsolatedTest.Execute(
                            uiApplication, request.RequestId, previewPath,
                            request.PreviewSha256, request.OutputPath);
                        response.DocumentMutationPerformed = true;
                    }
                    else if (string.Equals(request.Action, "execute_model_lines_save_as", StringComparison.Ordinal))
                    {
                        if (!request.SaveAllowed || string.IsNullOrWhiteSpace(request.OutputPath))
                        {
                            throw new InvalidDataException("Save As request lacks its exact output binding.");
                        }
                        response.OutputFile = ModelLineHandoff.Execute(
                            uiApplication.ActiveUIDocument.Document, request.RequestId,
                            previewPath, request.OutputPath);
                    }
                    else if (string.Equals(request.Action, "execute_family_placement_save_as", StringComparison.Ordinal))
                    {
                        if (!request.SaveAllowed || string.IsNullOrWhiteSpace(request.OutputPath))
                        {
                            throw new InvalidDataException("Family Save As request lacks its exact output binding.");
                        }
                        response.OutputFile = FamilyPlacementHandoff.Execute(
                            uiApplication.ActiveUIDocument.Document, request.RequestId,
                            previewPath, request.OutputPath);
                    }
                    else if (string.Equals(request.Action, "execute_fixture_mark_correction_save_as", StringComparison.Ordinal))
                    {
                        if (!request.SaveAllowed || string.IsNullOrWhiteSpace(request.OutputPath))
                            throw new InvalidDataException("Fixture Mark correction Save As lacks exact output binding.");
                        response.OutputFile = FixtureMarkCorrectionHandoff.Execute(
                            uiApplication.ActiveUIDocument.Document, request.RequestId,
                            previewPath, request.PreviewSha256, request.OutputPath);
                    }
                    else if (string.Equals(request.Action, "execute_school_l4_batch1_save_as", StringComparison.Ordinal))
                    {
                        if (!request.SaveAllowed || string.IsNullOrWhiteSpace(request.OutputPath)
                            || request.FamilyFiles == null || request.FamilyFiles.Count != 1
                            || !string.Equals(request.PreviewSha256, "5E78A601B372501A07612B4B120B9E73878F7FEAD4718C4CC5F39F40EB6F273C", StringComparison.Ordinal))
                        {
                            throw new InvalidDataException("School L4 batch-1 binding is invalid.");
                        }
                        response.OutputFile = SchoolL4Batch1Handoff.Execute(
                            uiApplication.ActiveUIDocument.Document, request.RequestId,
                            previewPath, request.OutputPath, request.FamilyFiles[0]);
                    }
                    else if (string.Equals(request.Action, "execute_school_l4_batch2_save_as", StringComparison.Ordinal))
                    {
                        if (!request.SaveAllowed || string.IsNullOrWhiteSpace(request.OutputPath)
                            || !string.Equals(request.PreviewSha256, "565529255B384C229F43B446A1A7169037E07DF63AD0F17D744FF37F50943801", StringComparison.Ordinal))
                        {
                            throw new InvalidDataException("School L4 batch-2 binding is invalid.");
                        }
                        response.OutputFile = SchoolL4Batch2Handoff.Execute(
                            uiApplication.ActiveUIDocument.Document, request.RequestId,
                            previewPath, request.OutputPath);
                    }
                    else if (string.Equals(request.Action, "execute_school_classroom_sample_save_as", StringComparison.Ordinal))
                    {
                        if (!request.SaveAllowed || string.IsNullOrWhiteSpace(request.OutputPath)
                            || request.FamilyFiles == null || request.FamilyFiles.Count != 2
                            || !string.Equals(request.PreviewSha256, "0293A7A6A70095DF2C2845754998C1A555C4C9315D48FB50B7E4BF2E9939B116", StringComparison.Ordinal))
                        {
                            throw new InvalidDataException("School classroom sample binding is invalid.");
                        }
                        response.OutputFile = SchoolClassroomSampleHandoff.Execute(
                            uiApplication.ActiveUIDocument.Document, request.RequestId,
                            previewPath, request.OutputPath, request.FamilyFiles[0], request.FamilyFiles[1]);
                    }
                    else if (string.Equals(request.Action, "execute_school_distribution_review_save_as", StringComparison.Ordinal))
                    {
                        if (!request.SaveAllowed || string.IsNullOrWhiteSpace(request.OutputPath))
                        {
                            throw new InvalidDataException("School distribution review Save As request lacks its exact output binding.");
                        }
                        response.OutputFile = SchoolDistributionReviewHandoff.Execute(
                            uiApplication, request.RequestId, previewPath, request.OutputPath);
                    }
                    else if (string.Equals(request.Action, "execute_school_lighting_review_v2_save_as", StringComparison.Ordinal))
                    {
                        if (!request.SaveAllowed || string.IsNullOrWhiteSpace(request.OutputPath))
                        {
                            throw new InvalidDataException("School lighting V2 Save As request lacks its exact output binding.");
                        }
                        response.OutputFile = SchoolLightingReviewV2Handoff.Execute(
                            uiApplication, request.RequestId, previewPath, request.OutputPath);
                    }
                    else if (string.Equals(request.Action, "execute_school_electrical_review_v3_save_as", StringComparison.Ordinal))
                    {
                        if (!request.SaveAllowed || string.IsNullOrWhiteSpace(request.OutputPath))
                        {
                            throw new InvalidDataException("School electrical V3 Save As request lacks its exact output binding.");
                        }
                        response.OutputFile = SchoolElectricalReviewV3Handoff.Execute(
                            uiApplication, request.RequestId, previewPath, request.OutputPath);
                    }
                    else if (string.Equals(request.Action, "execute_school_electrical_review_v4_save_as", StringComparison.Ordinal))
                    {
                        if (!request.SaveAllowed || string.IsNullOrWhiteSpace(request.OutputPath))
                            throw new InvalidDataException("School electrical V4 Save As request lacks its exact output binding.");
                        response.OutputFile = SchoolElectricalReviewV3Handoff.ExecuteV4(
                            uiApplication, request.RequestId, previewPath, request.OutputPath);
                    }
                    else if (string.Equals(request.Action, "execute_school_electrical_review_v5_save_as", StringComparison.Ordinal))
                    {
                        if (!request.SaveAllowed || string.IsNullOrWhiteSpace(request.OutputPath))
                            throw new InvalidDataException("School electrical V5 Save As lacks its exact output binding.");
                        response.OutputFile = SchoolElectricalReviewV3Handoff.ExecuteV5(
                            uiApplication, request.RequestId, previewPath, request.OutputPath,
                            request.PreviewSha256);
                    }
                    else if (string.Equals(request.Action, "execute_hosted_family_placement_save_as", StringComparison.Ordinal))
                    {
                        if (!request.SaveAllowed || string.IsNullOrWhiteSpace(request.OutputPath))
                        {
                            throw new InvalidDataException("Hosted family Save As request lacks its exact output binding.");
                        }
                        response.OutputFile = HostedFamilyPlacementHandoff.Execute(
                            uiApplication.ActiveUIDocument.Document, request.RequestId,
                            previewPath, request.OutputPath);
                    }
                    else
                    {
                        if (!request.SaveAllowed || string.IsNullOrWhiteSpace(request.OutputPath))
                        {
                            throw new InvalidDataException("Circuit Save As request lacks its exact output binding.");
                        }
                        response.OutputFile = CircuitAssignmentHandoff.Execute(
                            uiApplication.ActiveUIDocument.Document, request.RequestId,
                            previewPath, request.OutputPath);
                    }
                    response.DocumentMutationPerformed = true;
                }
                else
                {
                    Guid handoffId;
                    Guid previewId;
                    if (request.ApprovalGranted || request.SaveAllowed
                        || !Guid.TryParse(request.HandoffRequestId, out handoffId)
                        || !Guid.TryParse(request.PreviewId, out previewId)
                        || string.IsNullOrWhiteSpace(request.PreviewFileSha256))
                    {
                        throw new InvalidDataException("Revit handoff verification binding is invalid.");
                    }
                    string previewPath = Path.Combine(
                        root, "approved-previews", request.PreviewId + ".json");
                    if (!File.Exists(previewPath)
                        || !string.Equals(HashFile(previewPath), request.PreviewFileSha256, StringComparison.OrdinalIgnoreCase))
                    {
                        throw new InvalidDataException("Revit handoff verification preview changed.");
                    }
                    if (string.Equals(request.Action, "verify_model_lines_handoff", StringComparison.Ordinal))
                    {
                        response.OutputFile = ModelLineHandoff.Verify(
                            uiApplication.ActiveUIDocument.Document, request.RequestId,
                            request.HandoffRequestId, previewPath);
                    }
                    else if (string.Equals(request.Action, "verify_family_placement_handoff", StringComparison.Ordinal))
                    {
                        response.OutputFile = FamilyPlacementHandoff.Verify(
                            uiApplication.ActiveUIDocument.Document, request.RequestId,
                            request.HandoffRequestId, previewPath);
                    }
                    else if (string.Equals(request.Action, "verify_fixture_mark_correction_handoff", StringComparison.Ordinal))
                    {
                        response.OutputFile = FixtureMarkCorrectionHandoff.Verify(
                            uiApplication.ActiveUIDocument.Document, request.RequestId,
                            request.HandoffRequestId, previewPath);
                    }
                    else if (string.Equals(request.Action, "verify_school_l4_batch1_handoff", StringComparison.Ordinal))
                    {
                        response.OutputFile = SchoolL4Batch1Handoff.Verify(
                            uiApplication.ActiveUIDocument.Document, request.RequestId,
                            request.HandoffRequestId, previewPath);
                    }
                    else if (string.Equals(request.Action, "verify_school_l4_batch2_handoff", StringComparison.Ordinal))
                    {
                        response.OutputFile = SchoolL4Batch2Handoff.Verify(
                            uiApplication.ActiveUIDocument.Document, request.RequestId,
                            request.HandoffRequestId, previewPath);
                    }
                    else if (string.Equals(request.Action, "verify_school_classroom_sample_handoff", StringComparison.Ordinal))
                    {
                        response.OutputFile = SchoolClassroomSampleHandoff.Verify(
                            uiApplication.ActiveUIDocument.Document, request.RequestId,
                            request.HandoffRequestId, previewPath);
                    }
                    else if (string.Equals(request.Action, "verify_hosted_family_placement_handoff", StringComparison.Ordinal))
                    {
                        response.OutputFile = HostedFamilyPlacementHandoff.Verify(
                            uiApplication.ActiveUIDocument.Document, request.RequestId,
                            request.HandoffRequestId, previewPath);
                    }
                    else
                    {
                        response.OutputFile = CircuitAssignmentHandoff.Verify(
                            uiApplication.ActiveUIDocument.Document, request.RequestId,
                            request.HandoffRequestId, previewPath);
                    }
                }
                response.Status = "completed";
            }
            catch (Exception exception)
            {
                response.Error = Bounded(exception.Message, 500);
            }
            finally
            {
                WriteResponse(root, response);
                File.Delete(processing);
            }
        }

        private static BridgeRequest ReadRequest(string path)
        {
            FileInfo info = new FileInfo(path);
            if (info.Length > MaximumRequestBytes)
            {
                throw new InvalidDataException("Revit bridge request exceeds 64 KiB.");
            }
            DataContractJsonSerializer serializer = new DataContractJsonSerializer(typeof(BridgeRequest));
            using (FileStream stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                return (BridgeRequest)serializer.ReadObject(stream);
            }
        }

        private static void WriteResponse(string root, BridgeResponse response)
        {
            string responses = Path.Combine(root, "responses");
            Directory.CreateDirectory(responses);
            string destination = Path.Combine(responses, response.RequestId + ".json");
            string temporary = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
            DataContractJsonSerializer serializer = new DataContractJsonSerializer(typeof(BridgeResponse));
            using (FileStream stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                serializer.WriteObject(stream, response);
                stream.Flush(true);
            }
            File.Move(temporary, destination);
        }

        private static string Bounded(string value, int maximumLength)
        {
            if (string.IsNullOrEmpty(value))
            {
                return string.Empty;
            }
            return value.Length <= maximumLength ? value : value.Substring(0, maximumLength);
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
