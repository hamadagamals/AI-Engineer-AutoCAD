using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;

using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Electrical;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Events;

namespace AIEngineer.Revit
{
    [DataContract]
    internal sealed class StartupReport
    {
        [DataMember(Name = "schema_version", Order = 1)] public int SchemaVersion;
        [DataMember(Name = "loaded_utc", Order = 2)] public string LoadedUtc;
        [DataMember(Name = "revit_version", Order = 3)] public string RevitVersion;
        [DataMember(Name = "addin_loaded", Order = 4)] public bool AddinLoaded;
        [DataMember(Name = "command_name", Order = 5)] public string CommandName;
        [DataMember(Name = "transaction_mode", Order = 6)] public string TransactionMode;
        [DataMember(Name = "provider_egress_performed", Order = 7)] public bool ProviderEgressPerformed;
        [DataMember(Name = "document_mutation_performed", Order = 8)] public bool DocumentMutationPerformed;
        [DataMember(Name = "startup_status", Order = 9)] public string StartupStatus;
        [DataMember(Name = "error", Order = 10)] public string Error;
    }

    public sealed class ReadOnlyApplication : IExternalApplication
    {
        private UIControlledApplication controlledApplication;
        private bool autoCapturePending;
        private ExternalEvent requestEvent;
        private FileSystemWatcher requestWatcher;
        private DateTime nextRequestRecoveryUtc = DateTime.MinValue;

        public Result OnStartup(UIControlledApplication application)
        {
            StartupReport report = new StartupReport
            {
                SchemaVersion = 1,
                LoadedUtc = DateTime.UtcNow.ToString("O"),
                RevitVersion = application.ControlledApplication.VersionNumber,
                AddinLoaded = true,
                CommandName = "AIENGINEER_REVIT_READONLY_INVENTORY",
                TransactionMode = "ReadOnly",
                ProviderEgressPerformed = false,
                DocumentMutationPerformed = false,
                StartupStatus = "loading",
                Error = string.Empty
            };
            WriteStartupReport(report);
            try
            {
                RibbonPanel panel = application.CreateRibbonPanel("AI-Engineer");
                PushButtonData button = new PushButtonData(
                    "AIEngineerReadOnlyInventory",
                    "Read-Only\nInventory",
                    Assembly.GetExecutingAssembly().Location,
                    "AIEngineer.Revit.ReadOnlyInventoryCommand");
                button.ToolTip = "Export bounded model counts without modifying the Revit document.";
                panel.AddItem(button);
                controlledApplication = application;
                application.Idling += OnIdling;
                application.DialogBoxShowing += OnDialogBoxShowing;
                requestEvent = ExternalEvent.Create(new ReadOnlyRequestExternalEventHandler());
                string requestDirectory = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "AI-Engineer", "revit", "requests");
                Directory.CreateDirectory(requestDirectory);
                requestWatcher = new FileSystemWatcher(requestDirectory, "pending.json");
                requestWatcher.NotifyFilter = NotifyFilters.FileName | NotifyFilters.CreationTime;
                requestWatcher.Created += OnRequestAvailable;
                requestWatcher.Renamed += OnRequestRenamed;
                requestWatcher.EnableRaisingEvents = true;
                if (File.Exists(Path.Combine(requestDirectory, "pending.json")))
                {
                    requestEvent.Raise();
                }
                if (string.Equals(
                    Environment.GetEnvironmentVariable("AI_ENGINEER_REVIT_READONLY_AUTOCAPTURE"),
                    "1",
                    StringComparison.Ordinal))
                {
                    autoCapturePending = true;
                }
                report.StartupStatus = "ready";
                WriteStartupReport(report);
                return Result.Succeeded;
            }
            catch (Exception exception)
            {
                report.StartupStatus = "ribbon_failed";
                string detail = exception.ToString();
                report.Error = detail.Length <= 2000 ? detail : detail.Substring(0, 2000);
                WriteStartupReport(report);
                return Result.Failed;
            }
        }

        public Result OnShutdown(UIControlledApplication application)
        {
            if (controlledApplication != null)
            {
                controlledApplication.Idling -= OnIdling;
                controlledApplication.DialogBoxShowing -= OnDialogBoxShowing;
            }
            if (requestWatcher != null)
            {
                requestWatcher.Dispose();
            }
            if (requestEvent != null)
            {
                requestEvent.Dispose();
            }
            return Result.Succeeded;
        }

        private void OnRequestAvailable(object sender, FileSystemEventArgs eventArgs)
        {
            if (requestEvent != null)
            {
                requestEvent.Raise();
            }
        }

        private void OnRequestRenamed(object sender, RenamedEventArgs eventArgs)
        {
            OnRequestAvailable(sender, eventArgs);
        }

        private void OnIdling(object sender, IdlingEventArgs eventArgs)
        {
            UIApplication uiApplication = sender as UIApplication;
            if (uiApplication == null)
            {
                return;
            }
            if (autoCapturePending && uiApplication.ActiveUIDocument != null)
            {
                autoCapturePending = false;
                ReadOnlyInventoryCommand.Capture(uiApplication);
            }
            if (requestEvent != null && DateTime.UtcNow >= nextRequestRecoveryUtc)
            {
                nextRequestRecoveryUtc = DateTime.UtcNow.AddSeconds(1);
                string pending = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "AI-Engineer", "revit", "requests", "pending.json");
                if (File.Exists(pending))
                {
                    requestEvent.Raise();
                }
            }
        }

        private void OnDialogBoxShowing(object sender, DialogBoxShowingEventArgs eventArgs)
        {
            TaskDialogShowingEventArgs taskDialog = eventArgs as TaskDialogShowingEventArgs;
            if (taskDialog != null
                && string.Equals(
                    taskDialog.DialogId,
                    "TaskDialog_Views_Related_To_Analytical_Changed",
                    StringComparison.Ordinal))
            {
                taskDialog.OverrideResult((int)TaskDialogResult.Close);
            }
        }

        private static void WriteStartupReport(StartupReport report)
        {
            string root = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "AI-Engineer",
                "revit");
            Directory.CreateDirectory(root);
            string destination = Path.Combine(root, "startup.json");
            string temporary = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
            DataContractJsonSerializer serializer = new DataContractJsonSerializer(typeof(StartupReport));
            using (FileStream stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                serializer.WriteObject(stream, report);
                stream.Flush(true);
            }
            if (File.Exists(destination))
            {
                File.Replace(temporary, destination, null);
            }
            else
            {
                File.Move(temporary, destination);
            }
        }
    }

    [DataContract]
    internal sealed class InventoryReport
    {
        [DataMember(Name = "schema_version", Order = 1)] public int SchemaVersion;
        [DataMember(Name = "captured_utc", Order = 2)] public string CapturedUtc;
        [DataMember(Name = "revit_version", Order = 3)] public string RevitVersion;
        [DataMember(Name = "command_name", Order = 4)] public string CommandName;
        [DataMember(Name = "status", Order = 5)] public string Status;
        [DataMember(Name = "document_present", Order = 6)] public bool DocumentPresent;
        [DataMember(Name = "document_title", Order = 7)] public string DocumentTitle;
        [DataMember(Name = "is_family_document", Order = 8)] public bool IsFamilyDocument;
        [DataMember(Name = "is_workshared", Order = 9)] public bool IsWorkshared;
        [DataMember(Name = "active_view_name", Order = 10)] public string ActiveViewName;
        [DataMember(Name = "active_view_type", Order = 11)] public string ActiveViewType;
        [DataMember(Name = "counts", Order = 12)] public Dictionary<string, int> Counts;
        [DataMember(Name = "provider_egress_performed", Order = 13)] public bool ProviderEgressPerformed;
        [DataMember(Name = "document_mutation_performed", Order = 14)] public bool DocumentMutationPerformed;
        [DataMember(Name = "limitations", Order = 15)] public string[] Limitations;
    }

    [Transaction(TransactionMode.ReadOnly)]
    [Regeneration(RegenerationOption.Manual)]
    public sealed class ReadOnlyInventoryCommand : IExternalCommand
    {
        private const string CommandName = "AIENGINEER_REVIT_READONLY_INVENTORY";

        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            try
            {
                UIApplication uiApplication = commandData.Application;
                InventoryReport report = Capture(uiApplication);
                string output = GetLatestInventoryPath();
                TaskDialog.Show(
                    "AI-Engineer Revit",
                    report.DocumentPresent
                        ? "Read-only inventory completed.\n\n" + output
                        : "Revit bridge is loaded. Open a model and run the command again.\n\n" + output);
                return Result.Succeeded;
            }
            catch (Exception exception)
            {
                message = "AI-Engineer read-only Revit inventory failed: " + exception.Message;
                return Result.Failed;
            }
        }

        internal static InventoryReport Capture(UIApplication uiApplication)
        {
            UIDocument uiDocument = uiApplication.ActiveUIDocument;
            Document document = uiDocument == null ? null : uiDocument.Document;
            InventoryReport report = BuildReport(uiApplication, document);
            WriteReport(report);
            return report;
        }

        private static InventoryReport BuildReport(UIApplication uiApplication, Document document)
        {
            Dictionary<string, int> counts = new Dictionary<string, int>(StringComparer.Ordinal)
            {
                { "walls", CountCategory(document, BuiltInCategory.OST_Walls) },
                { "floors", CountCategory(document, BuiltInCategory.OST_Floors) },
                { "ceilings", CountCategory(document, BuiltInCategory.OST_Ceilings) },
                { "doors", CountCategory(document, BuiltInCategory.OST_Doors) },
                { "windows", CountCategory(document, BuiltInCategory.OST_Windows) },
                { "rooms", CountCategory(document, BuiltInCategory.OST_Rooms) },
                { "mep_spaces", CountCategory(document, BuiltInCategory.OST_MEPSpaces) },
                { "lighting_fixtures", CountCategory(document, BuiltInCategory.OST_LightingFixtures) },
                { "lighting_devices", CountCategory(document, BuiltInCategory.OST_LightingDevices) },
                { "electrical_fixtures", CountCategory(document, BuiltInCategory.OST_ElectricalFixtures) },
                { "electrical_equipment", CountCategory(document, BuiltInCategory.OST_ElectricalEquipment) },
                { "conduit", CountCategory(document, BuiltInCategory.OST_Conduit) },
                { "cable_tray", CountCategory(document, BuiltInCategory.OST_CableTray) },
                { "sheets", CountCategory(document, BuiltInCategory.OST_Sheets) },
                { "schedules", CountCategory(document, BuiltInCategory.OST_Schedules) },
                { "families", CountClass(document, typeof(Family)) },
                { "electrical_systems", CountClass(document, typeof(ElectricalSystem)) },
            };
            View activeView = document == null ? null : document.ActiveView;
            return new InventoryReport
            {
                SchemaVersion = 1,
                CapturedUtc = DateTime.UtcNow.ToString("O"),
                RevitVersion = uiApplication.Application.VersionNumber,
                CommandName = CommandName,
                Status = document == null ? "no_active_document" : "captured",
                DocumentPresent = document != null,
                DocumentTitle = document == null ? string.Empty : Bounded(document.Title, 260),
                IsFamilyDocument = document != null && document.IsFamilyDocument,
                IsWorkshared = document != null && document.IsWorkshared,
                ActiveViewName = activeView == null ? string.Empty : Bounded(activeView.Name, 260),
                ActiveViewType = activeView == null ? string.Empty : activeView.ViewType.ToString(),
                Counts = counts,
                ProviderEgressPerformed = false,
                DocumentMutationPerformed = false,
                Limitations = new[]
                {
                    "Top-level category counts only; linked-model contents are not expanded.",
                    "Every category count is cross-checked with an independent ElementCategoryFilter collector.",
                    "No geometry, parameters, circuits, or element identities are exported in this first gate.",
                    "The command uses TransactionMode.ReadOnly and cannot start a Revit transaction."
                }
            };
        }

        private static int CountCategory(Document document, BuiltInCategory category)
        {
            if (document == null)
            {
                return 0;
            }
            int primary = new FilteredElementCollector(document)
                .OfCategory(category)
                .WhereElementIsNotElementType()
                .GetElementCount();
            int verification = new FilteredElementCollector(document)
                .WherePasses(new ElementCategoryFilter(category))
                .WhereElementIsNotElementType()
                .ToElementIds()
                .Count;
            if (primary != verification)
            {
                throw new InvalidOperationException(
                    "Independent Revit category counts disagree for " + category + ".");
            }
            return primary;
        }

        private static int CountClass(Document document, Type type)
        {
            return document == null
                ? 0
                : new FilteredElementCollector(document).OfClass(type).GetElementCount();
        }

        private static string Bounded(string value, int maximumLength)
        {
            if (string.IsNullOrEmpty(value))
            {
                return string.Empty;
            }
            return value.Length <= maximumLength ? value : value.Substring(0, maximumLength);
        }

        private static string WriteReport(InventoryReport report)
        {
            string root = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "AI-Engineer",
                "revit");
            string runs = Path.Combine(root, "runs");
            Directory.CreateDirectory(runs);
            string stamp = DateTime.UtcNow.ToString("yyyyMMddTHHmmssfffZ");
            string runPath = Path.Combine(runs, "inventory-" + stamp + "-" + Guid.NewGuid().ToString("N") + ".json");
            WriteJson(runPath, report, FileMode.CreateNew);

            string latest = Path.Combine(root, "latest-inventory.json");
            string temporary = latest + "." + Guid.NewGuid().ToString("N") + ".tmp";
            WriteJson(temporary, report, FileMode.CreateNew);
            if (File.Exists(latest))
            {
                File.Replace(temporary, latest, null);
            }
            else
            {
                File.Move(temporary, latest);
            }
            return runPath;
        }

        private static string GetLatestInventoryPath()
        {
            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "AI-Engineer",
                "revit",
                "latest-inventory.json");
        }

        private static void WriteJson(string path, InventoryReport report, FileMode mode)
        {
            DataContractJsonSerializer serializer = new DataContractJsonSerializer(
                typeof(InventoryReport),
                new DataContractJsonSerializerSettings
                {
                    UseSimpleDictionaryFormat = true
                });
            using (FileStream stream = new FileStream(path, mode, FileAccess.Write, FileShare.None))
            {
                serializer.WriteObject(stream, report);
                stream.Flush(true);
            }
        }
    }
}
