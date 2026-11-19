using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;

using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Electrical;

namespace AIEngineer.Revit
{
    [DataContract]
    internal sealed class ProtectionParameterReport
    {
        [DataMember(Name = "source", Order = 1)] public string Source;
        [DataMember(Name = "name", Order = 2)] public string Name;
        [DataMember(Name = "parameter_id", Order = 3)] public long ParameterId;
        [DataMember(Name = "storage_type", Order = 4)] public string StorageType;
        [DataMember(Name = "value_display", Order = 5)] public string ValueDisplay;
    }

    [DataContract]
    internal sealed class LightingConnectorReport
    {
        [DataMember(Name = "connector_id", Order = 1)] public int ConnectorId;
        [DataMember(Name = "domain", Order = 2)] public string Domain;
        [DataMember(Name = "connector_type", Order = 3)] public string ConnectorType;
        [DataMember(Name = "electrical_system_type", Order = 4)] public string ElectricalSystemType;
        [DataMember(Name = "mep_system_id", Order = 5)] public long MepSystemId;
    }

    [DataContract]
    internal sealed class LightingCircuitReport
    {
        [DataMember(Name = "system_id", Order = 1)] public long SystemId;
        [DataMember(Name = "system_type", Order = 2)] public string SystemType;
        [DataMember(Name = "circuit_number", Order = 3)] public string CircuitNumber;
        [DataMember(Name = "panel_name", Order = 4)] public string PanelName;
        [DataMember(Name = "load_name", Order = 5)] public string LoadName;
        [DataMember(Name = "voltage_v", Order = 6)] public double Voltage;
        [DataMember(Name = "apparent_load_va", Order = 7)] public double ApparentLoad;
        [DataMember(Name = "true_load_w", Order = 8)] public double TrueLoad;
        [DataMember(Name = "power_factor", Order = 9)] public double PowerFactor;
        [DataMember(Name = "poles", Order = 10)] public int Poles;
        [DataMember(Name = "rating_a", Order = 11)] public double Rating;
        [DataMember(Name = "frame_a", Order = 12)] public double Frame;
        [DataMember(Name = "apparent_current_a", Order = 13)] public double ApparentCurrent;
        [DataMember(Name = "true_current_a", Order = 14)] public double TrueCurrent;
        [DataMember(Name = "voltage_drop_percent", Order = 15)] public double VoltageDrop;
        [DataMember(Name = "phase_label", Order = 16)] public string PhaseLabel;
        [DataMember(Name = "balanced_load", Order = 17)] public bool BalancedLoad;
        [DataMember(Name = "start_slot", Order = 18)] public int StartSlot;
        [DataMember(Name = "wire_size", Order = 19)] public string WireSize;
        [DataMember(Name = "hot_conductors", Order = 20)] public int HotConductors;
        [DataMember(Name = "neutral_conductors", Order = 21)] public int NeutralConductors;
        [DataMember(Name = "ground_conductors", Order = 22)] public int GroundConductors;
        [DataMember(Name = "apparent_current_phase_a", Order = 23)] public double ApparentCurrentPhaseA;
        [DataMember(Name = "apparent_current_phase_b", Order = 24)] public double ApparentCurrentPhaseB;
        [DataMember(Name = "apparent_current_phase_c", Order = 25)] public double ApparentCurrentPhaseC;
        [DataMember(Name = "panel", Order = 26)] public LightingPanelReport Panel;
        [DataMember(Name = "length_mm", Order = 27)] public double Length;
        [DataMember(Name = "runs", Order = 28)] public int Runs;
        [DataMember(Name = "ways", Order = 29)] public int Ways;
        [DataMember(Name = "circuit_notes", Order = 30)] public string CircuitNotes;
        [DataMember(Name = "wire_type", Order = 31)] public LightingWireTypeReport WireType;
        [DataMember(Name = "protection_parameters", Order = 32)]
        public List<ProtectionParameterReport> ProtectionParameters;
    }

    [DataContract]
    internal sealed class LightingWireTypeReport
    {
        [DataMember(Name = "element_id", Order = 1)] public long ElementId;
        [DataMember(Name = "name", Order = 2)] public string Name;
        [DataMember(Name = "material", Order = 3)] public string Material;
        [DataMember(Name = "temperature_rating", Order = 4)] public string TemperatureRating;
        [DataMember(Name = "insulation", Order = 5)] public string Insulation;
        [DataMember(Name = "conduit", Order = 6)] public string Conduit;
        [DataMember(Name = "maximum_size", Order = 7)] public string MaximumSize;
        [DataMember(Name = "maximum_size_ampacity_a", Order = 8)] public long MaximumSizeAmpacity;
        [DataMember(Name = "neutral_required", Order = 9)] public bool NeutralRequired;
        [DataMember(Name = "neutral_mode", Order = 10)] public string NeutralMode;
    }

    [DataContract]
    internal sealed class UpstreamPanelReport
    {
        [DataMember(Name = "element_id", Order = 1)] public long ElementId;
        [DataMember(Name = "family_name", Order = 2)] public string FamilyName;
        [DataMember(Name = "type_name", Order = 3)] public string TypeName;
        [DataMember(Name = "mcb_rating_a", Order = 4)] public double? McbRating;
        [DataMember(Name = "short_circuit_rating_display", Order = 5)] public string ShortCircuitRating;
        [DataMember(Name = "protection_parameters", Order = 6)]
        public List<ProtectionParameterReport> ProtectionParameters;
    }

    [DataContract]
    internal sealed class PanelIncomingSystemReport
    {
        [DataMember(Name = "system_id", Order = 1)] public long SystemId;
        [DataMember(Name = "system_type", Order = 2)] public string SystemType;
        [DataMember(Name = "circuit_number", Order = 3)] public string CircuitNumber;
        [DataMember(Name = "panel_name", Order = 4)] public string PanelName;
        [DataMember(Name = "load_name", Order = 5)] public string LoadName;
        [DataMember(Name = "voltage_v", Order = 6)] public double Voltage;
        [DataMember(Name = "apparent_load_va", Order = 7)] public double ApparentLoad;
        [DataMember(Name = "true_load_w", Order = 8)] public double TrueLoad;
        [DataMember(Name = "power_factor", Order = 9)] public double PowerFactor;
        [DataMember(Name = "poles", Order = 10)] public int Poles;
        [DataMember(Name = "rating_a", Order = 11)] public double Rating;
        [DataMember(Name = "frame_a", Order = 12)] public double Frame;
        [DataMember(Name = "apparent_current_a", Order = 13)] public double ApparentCurrent;
        [DataMember(Name = "voltage_drop_percent", Order = 14)] public double VoltageDrop;
        [DataMember(Name = "length_mm", Order = 15)] public double Length;
        [DataMember(Name = "wire_size", Order = 16)] public string WireSize;
        [DataMember(Name = "upstream_panel", Order = 17)] public UpstreamPanelReport UpstreamPanel;
        [DataMember(Name = "protection_parameters", Order = 18)]
        public List<ProtectionParameterReport> ProtectionParameters;
    }

    [DataContract]
    internal sealed class LightingPanelReport
    {
        [DataMember(Name = "element_id", Order = 1)] public long ElementId;
        [DataMember(Name = "family_name", Order = 2)] public string FamilyName;
        [DataMember(Name = "type_name", Order = 3)] public string TypeName;
        [DataMember(Name = "mcb_rating_a", Order = 4)] public double? McbRating;
        [DataMember(Name = "voltage_v", Order = 5)] public double? Voltage;
        [DataMember(Name = "number_of_phases", Order = 6)] public int? NumberOfPhases;
        [DataMember(Name = "number_of_wires", Order = 7)] public int? NumberOfWires;
        [DataMember(Name = "total_connected_current_a", Order = 8)] public double? TotalConnectedCurrent;
        [DataMember(Name = "total_demand_current_a", Order = 9)] public double? TotalDemandCurrent;
        [DataMember(Name = "supply_from", Order = 10)] public string SupplyFrom;
        [DataMember(Name = "mains_type", Order = 11)] public string MainsType;
        [DataMember(Name = "current_phase_a", Order = 12)] public double? CurrentPhaseA;
        [DataMember(Name = "current_phase_b", Order = 13)] public double? CurrentPhaseB;
        [DataMember(Name = "current_phase_c", Order = 14)] public double? CurrentPhaseC;
        [DataMember(Name = "short_circuit_rating_display", Order = 15)] public string ShortCircuitRating;
        [DataMember(Name = "protection_parameters", Order = 16)]
        public List<ProtectionParameterReport> ProtectionParameters;
        [DataMember(Name = "incoming_systems", Order = 17)]
        public List<PanelIncomingSystemReport> IncomingSystems;
    }

    [DataContract]
    internal sealed class LightingFixtureCircuitReport
    {
        [DataMember(Name = "element_id", Order = 1)] public long ElementId;
        [DataMember(Name = "family_name", Order = 2)] public string FamilyName;
        [DataMember(Name = "type_name", Order = 3)] public string TypeName;
        [DataMember(Name = "mark", Order = 4)] public string Mark;
        [DataMember(Name = "circuit_count", Order = 5)] public int CircuitCount;
        [DataMember(Name = "circuits", Order = 6)] public List<LightingCircuitReport> Circuits;
        [DataMember(Name = "connector_count", Order = 7)] public int ConnectorCount;
        [DataMember(Name = "connectors", Order = 8)] public List<LightingConnectorReport> Connectors;
        [DataMember(Name = "voltage_v", Order = 9)] public double? Voltage;
        [DataMember(Name = "apparent_load_va", Order = 10)] public double? ApparentLoad;
    }

    [DataContract]
    internal sealed class LightingCircuitQueryReport
    {
        [DataMember(Name = "schema_version", Order = 1)] public int SchemaVersion;
        [DataMember(Name = "request_id", Order = 2)] public string RequestId;
        [DataMember(Name = "document_title", Order = 3)] public string DocumentTitle;
        [DataMember(Name = "requested_element_id", Order = 4)] public long RequestedElementId;
        [DataMember(Name = "total_fixture_count", Order = 5)] public int TotalFixtureCount;
        [DataMember(Name = "returned_fixture_count", Order = 6)] public int ReturnedFixtureCount;
        [DataMember(Name = "truncated", Order = 7)] public bool Truncated;
        [DataMember(Name = "fixtures", Order = 8)] public List<LightingFixtureCircuitReport> Fixtures;
        [DataMember(Name = "provider_egress_performed", Order = 9)] public bool ProviderEgressPerformed;
        [DataMember(Name = "document_mutation_performed", Order = 10)] public bool DocumentMutationPerformed;
        [DataMember(Name = "start_after_element_id", Order = 11)] public long StartAfterElementId;
        [DataMember(Name = "next_start_after_element_id", Order = 12)] public long NextStartAfterElementId;
    }

    internal static class ReadOnlyLightingCircuitQuery
    {
        public static string Capture(
            Document document, string requestId, long requestedElementId,
            long startAfterElementId, int maximum)
        {
            List<FamilyInstance> found = new List<FamilyInstance>();
            foreach (Element element in new FilteredElementCollector(document)
                .OfCategory(BuiltInCategory.OST_LightingFixtures)
                .WhereElementIsNotElementType())
            {
                FamilyInstance fixture = element as FamilyInstance;
                if (fixture == null) continue;
                if (requestedElementId > 0 && fixture.Id.Value != requestedElementId) continue;
                found.Add(fixture);
            }
            found.Sort(delegate(FamilyInstance left, FamilyInstance right)
            {
                return left.Id.Value.CompareTo(right.Id.Value);
            });
            if (requestedElementId > 0 && found.Count != 1)
            {
                throw new InvalidDataException("The exact Revit lighting fixture was not found.");
            }
            List<LightingFixtureCircuitReport> fixtures = new List<LightingFixtureCircuitReport>();
            for (int index = 0; index < found.Count && fixtures.Count < maximum; index++)
            {
                if (requestedElementId == 0 && found[index].Id.Value <= startAfterElementId) continue;
                fixtures.Add(Fixture(found[index]));
            }
            int remaining = 0;
            for (int index = 0; index < found.Count; index++)
            {
                if (requestedElementId > 0 || found[index].Id.Value > startAfterElementId) remaining++;
            }
            bool truncated = remaining > fixtures.Count;
            LightingCircuitQueryReport report = new LightingCircuitQueryReport
            {
                SchemaVersion = 1,
                RequestId = requestId,
                DocumentTitle = Bounded(document.Title),
                RequestedElementId = requestedElementId,
                TotalFixtureCount = found.Count,
                ReturnedFixtureCount = fixtures.Count,
                Truncated = truncated,
                Fixtures = fixtures,
                ProviderEgressPerformed = false,
                DocumentMutationPerformed = false,
                StartAfterElementId = startAfterElementId,
                NextStartAfterElementId = truncated && fixtures.Count > 0
                    ? fixtures[fixtures.Count - 1].ElementId : 0
            };
            return Write(requestId, report);
        }

        private static LightingFixtureCircuitReport Fixture(FamilyInstance fixture)
        {
            List<LightingCircuitReport> circuits = new List<LightingCircuitReport>();
            List<LightingConnectorReport> connectors = new List<LightingConnectorReport>();
            if (fixture.MEPModel != null)
            {
                ISet<ElectricalSystem> systems = fixture.MEPModel.GetElectricalSystems();
                if (systems != null)
                {
                    foreach (ElectricalSystem system in systems)
                    {
                        if (system != null) circuits.Add(Circuit(system));
                    }
                }
                ConnectorManager manager = fixture.MEPModel.ConnectorManager;
                if (manager != null && manager.Connectors != null)
                {
                    foreach (Connector connector in manager.Connectors)
                    {
                        if (connector != null) connectors.Add(ConnectorValue(connector));
                    }
                }
            }
            circuits.Sort(delegate(LightingCircuitReport left, LightingCircuitReport right)
            {
                return left.SystemId.CompareTo(right.SystemId);
            });
            FamilySymbol symbol = fixture.Symbol;
            Parameter mark = fixture.get_Parameter(BuiltInParameter.ALL_MODEL_MARK);
            return new LightingFixtureCircuitReport
            {
                ElementId = fixture.Id.Value,
                FamilyName = Bounded(symbol == null || symbol.Family == null ? string.Empty : symbol.Family.Name),
                TypeName = Bounded(symbol == null ? string.Empty : symbol.Name),
                Mark = Bounded(mark == null ? string.Empty : mark.AsString()),
                CircuitCount = circuits.Count,
                Circuits = circuits,
                ConnectorCount = connectors.Count,
                Connectors = connectors,
                Voltage = ParameterMeasurement(
                    fixture.get_Parameter(BuiltInParameter.RBS_ELEC_VOLTAGE), UnitTypeId.Volts),
                ApparentLoad = ParameterMeasurement(
                    fixture.get_Parameter(BuiltInParameter.RBS_ELEC_APPARENT_LOAD), UnitTypeId.VoltAmperes)
            };
        }

        private static LightingConnectorReport ConnectorValue(Connector connector)
        {
            long systemId = -1;
            string electricalType = string.Empty;
            if (connector.Domain == Domain.DomainElectrical)
            {
                electricalType = connector.ElectricalSystemType.ToString();
                MEPSystem system = connector.MEPSystem;
                if (system != null) systemId = system.Id.Value;
            }
            return new LightingConnectorReport
            {
                ConnectorId = connector.Id,
                Domain = connector.Domain.ToString(),
                ConnectorType = connector.ConnectorType.ToString(),
                ElectricalSystemType = Bounded(electricalType),
                MepSystemId = systemId
            };
        }

        internal static LightingCircuitReport Circuit(ElectricalSystem system)
        {
            return new LightingCircuitReport
            {
                SystemId = system.Id.Value,
                SystemType = Bounded(system.SystemType.ToString()),
                CircuitNumber = Bounded(system.CircuitNumber),
                PanelName = Bounded(system.PanelName),
                LoadName = Bounded(system.LoadName),
                Voltage = Measurement(system.Voltage, UnitTypeId.Volts),
                ApparentLoad = Measurement(system.ApparentLoad, UnitTypeId.VoltAmperes),
                TrueLoad = Measurement(system.TrueLoad, UnitTypeId.Watts),
                PowerFactor = Math.Round(system.PowerFactor, 6),
                Poles = system.PolesNumber,
                Rating = Measurement(system.Rating, UnitTypeId.Amperes),
                Frame = Measurement(system.Frame, UnitTypeId.Amperes),
                ApparentCurrent = Measurement(system.ApparentCurrent, UnitTypeId.Amperes),
                TrueCurrent = Measurement(system.TrueCurrent, UnitTypeId.Amperes),
                VoltageDrop = Math.Round(system.VoltageDrop, 6),
                PhaseLabel = Bounded(system.PhaseLabel),
                BalancedLoad = system.BalancedLoad,
                StartSlot = system.StartSlot,
                WireSize = Bounded(system.WireSizeString),
                HotConductors = system.HotConductorsNumber,
                NeutralConductors = system.NeutralConductorsNumber,
                GroundConductors = system.GroundConductorsNumber,
                ApparentCurrentPhaseA = Measurement(system.ApparentCurrentPhaseA, UnitTypeId.Amperes),
                ApparentCurrentPhaseB = Measurement(system.ApparentCurrentPhaseB, UnitTypeId.Amperes),
                ApparentCurrentPhaseC = Measurement(system.ApparentCurrentPhaseC, UnitTypeId.Amperes),
                Panel = Panel(system.BaseEquipment),
                Length = Measurement(system.Length, UnitTypeId.Millimeters),
                Runs = system.RunsNumber,
                Ways = system.Ways,
                CircuitNotes = ParameterText(
                    system.get_Parameter(BuiltInParameter.RBS_ELEC_CIRCUIT_NOTES_PARAM)),
                WireType = WireType(system.WireType),
                ProtectionParameters = ProtectionParameters(system)
            };
        }

        private static LightingWireTypeReport WireType(WireType wire)
        {
            if (wire == null) return null;
            WireSize maximum = wire.MaxSize;
            return new LightingWireTypeReport
            {
                ElementId = wire.Id.Value,
                Name = Bounded(wire.Name),
                Material = Bounded(wire.WireMaterial == null ? string.Empty : wire.WireMaterial.Name),
                TemperatureRating = Bounded(
                    wire.TemperatureRating == null ? string.Empty : wire.TemperatureRating.Name),
                Insulation = Bounded(wire.Insulation == null ? string.Empty : wire.Insulation.Name),
                Conduit = Bounded(wire.Conduit == null ? string.Empty : wire.Conduit.Name),
                MaximumSize = Bounded(maximum == null ? string.Empty : maximum.Size),
                MaximumSizeAmpacity = maximum == null ? 0 : maximum.Ampacity,
                NeutralRequired = wire.NeutralRequired,
                NeutralMode = Bounded(wire.NeutralSize.ToString())
            };
        }

        private static LightingPanelReport Panel(FamilyInstance panel)
        {
            if (panel == null) return null;
            FamilySymbol symbol = panel.Symbol;
            return new LightingPanelReport
            {
                ElementId = panel.Id.Value,
                FamilyName = Bounded(symbol == null || symbol.Family == null ? string.Empty : symbol.Family.Name),
                TypeName = Bounded(symbol == null ? string.Empty : symbol.Name),
                McbRating = ParameterMeasurement(
                    panel.get_Parameter(BuiltInParameter.RBS_ELEC_PANEL_MCB_RATING_PARAM), UnitTypeId.Amperes),
                Voltage = ParameterMeasurement(
                    panel.get_Parameter(BuiltInParameter.RBS_ELEC_VOLTAGE), UnitTypeId.Volts),
                NumberOfPhases = ParameterInteger(
                    panel.get_Parameter(BuiltInParameter.RBS_ELEC_PANEL_NUMPHASES_PARAM)),
                NumberOfWires = ParameterInteger(
                    panel.get_Parameter(BuiltInParameter.RBS_ELEC_PANEL_NUMWIRES_PARAM)),
                TotalConnectedCurrent = ParameterMeasurement(
                    panel.get_Parameter(BuiltInParameter.RBS_ELEC_PANEL_TOTAL_CONNECTED_CURRENT_PARAM),
                    UnitTypeId.Amperes),
                TotalDemandCurrent = ParameterMeasurement(
                    panel.get_Parameter(BuiltInParameter.RBS_ELEC_PANEL_TOTAL_DEMAND_CURRENT_PARAM),
                    UnitTypeId.Amperes),
                SupplyFrom = ParameterText(
                    panel.get_Parameter(BuiltInParameter.RBS_ELEC_PANEL_SUPPLY_FROM_PARAM)),
                MainsType = ParameterText(
                    panel.get_Parameter(BuiltInParameter.RBS_ELEC_PANEL_MAINSTYPE_PARAM)),
                CurrentPhaseA = ParameterMeasurement(
                    panel.get_Parameter(BuiltInParameter.RBS_ELEC_PANEL_CURRENT_PHASEA_PARAM),
                    UnitTypeId.Amperes),
                CurrentPhaseB = ParameterMeasurement(
                    panel.get_Parameter(BuiltInParameter.RBS_ELEC_PANEL_CURRENT_PHASEB_PARAM),
                    UnitTypeId.Amperes),
                CurrentPhaseC = ParameterMeasurement(
                    panel.get_Parameter(BuiltInParameter.RBS_ELEC_PANEL_CURRENT_PHASEC_PARAM),
                    UnitTypeId.Amperes),
                ShortCircuitRating = ParameterText(
                    panel.get_Parameter(BuiltInParameter.RBS_ELEC_SHORT_CIRCUIT_RATING)),
                ProtectionParameters = ProtectionParameters(panel),
                IncomingSystems = IncomingSystems(panel)
            };
        }

        private static List<PanelIncomingSystemReport> IncomingSystems(FamilyInstance panel)
        {
            List<PanelIncomingSystemReport> values = new List<PanelIncomingSystemReport>();
            if (panel.MEPModel == null) return values;
            ISet<ElectricalSystem> systems = panel.MEPModel.GetElectricalSystems();
            if (systems == null) return values;
            foreach (ElectricalSystem system in systems)
            {
                if (system == null || system.BaseEquipment == null) continue;
                if (system.BaseEquipment.Id == panel.Id || !ContainsElement(system, panel.Id)) continue;
                values.Add(new PanelIncomingSystemReport
                {
                    SystemId = system.Id.Value,
                    SystemType = Bounded(system.SystemType.ToString()),
                    CircuitNumber = Bounded(system.CircuitNumber),
                    PanelName = Bounded(system.PanelName),
                    LoadName = Bounded(system.LoadName),
                    Voltage = Measurement(system.Voltage, UnitTypeId.Volts),
                    ApparentLoad = Measurement(system.ApparentLoad, UnitTypeId.VoltAmperes),
                    TrueLoad = Measurement(system.TrueLoad, UnitTypeId.Watts),
                    PowerFactor = Math.Round(system.PowerFactor, 6),
                    Poles = system.PolesNumber,
                    Rating = Measurement(system.Rating, UnitTypeId.Amperes),
                    Frame = Measurement(system.Frame, UnitTypeId.Amperes),
                    ApparentCurrent = Measurement(system.ApparentCurrent, UnitTypeId.Amperes),
                    VoltageDrop = Math.Round(system.VoltageDrop, 6),
                    Length = Measurement(system.Length, UnitTypeId.Millimeters),
                    WireSize = Bounded(system.WireSizeString),
                    UpstreamPanel = UpstreamPanel(system.BaseEquipment),
                    ProtectionParameters = ProtectionParameters(system)
                });
            }
            values.Sort(delegate(PanelIncomingSystemReport left, PanelIncomingSystemReport right)
            {
                return left.SystemId.CompareTo(right.SystemId);
            });
            if (values.Count > 8) values.RemoveRange(8, values.Count - 8);
            return values;
        }

        private static bool ContainsElement(ElectricalSystem system, ElementId elementId)
        {
            ElementSet elements = system.Elements;
            if (elements == null) return false;
            foreach (Element element in elements)
            {
                if (element != null && element.Id == elementId) return true;
            }
            return false;
        }

        private static UpstreamPanelReport UpstreamPanel(FamilyInstance panel)
        {
            if (panel == null) return null;
            FamilySymbol symbol = panel.Symbol;
            return new UpstreamPanelReport
            {
                ElementId = panel.Id.Value,
                FamilyName = Bounded(symbol == null || symbol.Family == null ? string.Empty : symbol.Family.Name),
                TypeName = Bounded(symbol == null ? string.Empty : symbol.Name),
                McbRating = ParameterMeasurement(
                    panel.get_Parameter(BuiltInParameter.RBS_ELEC_PANEL_MCB_RATING_PARAM), UnitTypeId.Amperes),
                ShortCircuitRating = ParameterText(
                    panel.get_Parameter(BuiltInParameter.RBS_ELEC_SHORT_CIRCUIT_RATING)),
                ProtectionParameters = ProtectionParameters(panel)
            };
        }

        private static List<ProtectionParameterReport> ProtectionParameters(Element element)
        {
            List<ProtectionParameterReport> values = new List<ProtectionParameterReport>();
            HashSet<string> seen = new HashSet<string>(StringComparer.Ordinal);
            AddProtectionParameters(values, seen, element, "instance");
            ElementId typeId = element.GetTypeId();
            if (typeId != null && typeId != ElementId.InvalidElementId)
            {
                Element type = element.Document.GetElement(typeId);
                if (type != null) AddProtectionParameters(values, seen, type, "type");
            }
            values.Sort(delegate(ProtectionParameterReport left, ProtectionParameterReport right)
            {
                int source = string.CompareOrdinal(left.Source, right.Source);
                if (source != 0) return source;
                int name = string.CompareOrdinal(left.Name, right.Name);
                return name != 0 ? name : left.ParameterId.CompareTo(right.ParameterId);
            });
            if (values.Count > 32) values.RemoveRange(32, values.Count - 32);
            return values;
        }

        private static void AddProtectionParameters(
            List<ProtectionParameterReport> values,
            HashSet<string> seen,
            Element element,
            string source)
        {
            foreach (Parameter parameter in element.Parameters)
            {
                if (parameter == null || parameter.Definition == null || !parameter.HasValue) continue;
                string name = parameter.Definition.Name ?? string.Empty;
                if (!IsProtectionParameter(name)) continue;
                string value = ParameterDisplay(parameter);
                if (string.IsNullOrWhiteSpace(value)) continue;
                long parameterId = parameter.Id.Value;
                string key = source + "|" + parameterId.ToString(CultureInfo.InvariantCulture)
                    + "|" + name;
                if (!seen.Add(key)) continue;
                values.Add(new ProtectionParameterReport
                {
                    Source = source,
                    Name = Bounded(name),
                    ParameterId = parameterId,
                    StorageType = Bounded(parameter.StorageType.ToString()),
                    ValueDisplay = Bounded(value)
                });
            }
        }

        private static bool IsProtectionParameter(string name)
        {
            string normalized = (name ?? string.Empty).Trim().ToLowerInvariant();
            string[] terms = {
                "rating", "frame", "breaker", "trip", "curve", "protection", "protective",
                "short circuit", "fault current", "interrupt", "aic", "kaic", "mcb", "mccb", "fuse"
            };
            foreach (string term in terms)
            {
                if (normalized.Contains(term)) return true;
            }
            return false;
        }

        private static string ParameterDisplay(Parameter parameter)
        {
            string value = parameter.AsValueString();
            if (string.IsNullOrWhiteSpace(value) && parameter.StorageType == StorageType.String)
                value = parameter.AsString();
            if (string.IsNullOrWhiteSpace(value) && parameter.StorageType == StorageType.Integer)
                value = parameter.AsInteger().ToString(CultureInfo.InvariantCulture);
            if (string.IsNullOrWhiteSpace(value) && parameter.StorageType == StorageType.ElementId)
                value = parameter.AsElementId().Value.ToString(CultureInfo.InvariantCulture);
            return Bounded(value);
        }

        private static double Measurement(double value, ForgeTypeId unit)
        {
            double converted = UnitUtils.ConvertFromInternalUnits(value, unit);
            if (double.IsNaN(converted) || double.IsInfinity(converted))
                throw new InvalidDataException("Revit returned a non-finite electrical measurement.");
            return Math.Round(converted, 6);
        }

        private static double? ParameterMeasurement(Parameter parameter, ForgeTypeId unit)
        {
            if (parameter == null || !parameter.HasValue || parameter.StorageType != StorageType.Double)
                return null;
            return Measurement(parameter.AsDouble(), unit);
        }

        private static int? ParameterInteger(Parameter parameter)
        {
            if (parameter == null || !parameter.HasValue || parameter.StorageType != StorageType.Integer)
                return null;
            return parameter.AsInteger();
        }

        private static string ParameterText(Parameter parameter)
        {
            if (parameter == null || !parameter.HasValue) return string.Empty;
            string value = parameter.AsString();
            if (string.IsNullOrWhiteSpace(value)) value = parameter.AsValueString();
            return Bounded(value);
        }

        private static string Bounded(string value)
        {
            if (string.IsNullOrEmpty(value)) return string.Empty;
            return value.Length <= 260 ? value : value.Substring(0, 260);
        }

        private static string Write(string requestId, LightingCircuitQueryReport report)
        {
            string root = Path.Combine(Environment.GetFolderPath(
                Environment.SpecialFolder.LocalApplicationData), "AI-Engineer", "revit", "lighting-circuits");
            Directory.CreateDirectory(root);
            string fileName = requestId + ".json";
            string destination = Path.Combine(root, fileName);
            DataContractJsonSerializer serializer = new DataContractJsonSerializer(typeof(LightingCircuitQueryReport));
            using (FileStream stream = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                serializer.WriteObject(stream, report);
                stream.Flush(true);
            }
            return "lighting-circuits/" + fileName;
        }
    }
}
