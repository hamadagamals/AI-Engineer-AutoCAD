using System;
using System.Collections.Generic;
using System.Globalization;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Electrical;

namespace AIEngineer.Revit.Direct
{
    /// <summary>
    /// Version-agnostic Revit API compatibility layer supporting Revit 2015 through Revit 2026+
    /// (.NET Framework 4.x and .NET 8.0-windows).
    /// Compile-time symbols set automatically by build.ps1:
    ///   - REVIT2018_OR_GREATER (ElectricalSystem.Create)
    ///   - REVIT2021_OR_GREATER (legacy macro kept for compat)
    ///   - REVIT2024_OR_GREATER (ElementId.Value / new ElementId(long))
    /// </summary>
    internal static class RevitCompat
    {
        public const double FeetToMetersFactor = 0.3048;
        public const double MetersToFeetFactor = 1.0 / 0.3048;
        public const double SqFeetToSqMetersFactor = 0.09290304;
        public const double SqMetersToSqFeetFactor = 1.0 / 0.09290304;

        public static double FeetToMeters(double feet) { return feet * FeetToMetersFactor; }
        public static double MetersToFeet(double meters) { return meters * MetersToFeetFactor; }
        public static double SqFeetToSqMeters(double sqFeet) { return sqFeet * SqFeetToSqMetersFactor; }

        public static long GetIdValue(ElementId id)
        {
            if (id == null) return -1L;
#if REVIT2024_OR_GREATER
            return id.Value;
#else
            return (long)id.IntegerValue;
#endif
        }

        public static ElementId CreateId(long value)
        {
#if REVIT2024_OR_GREATER
            return new ElementId(value);
#else
            if (value < int.MinValue || value > int.MaxValue) return ElementId.InvalidElementId;
            return new ElementId((int)value);
#endif
        }

        public static bool IsValidId(ElementId id)
        {
            return id != null && id != ElementId.InvalidElementId && GetIdValue(id) > 0;
        }

        public static ElectricalSystem CreatePowerCircuit(Document doc, IList<ElementId> elementIds)
        {
            if (doc == null || elementIds == null || elementIds.Count == 0)
                throw new ArgumentException("At least one valid electrical element ID is required.");
#if REVIT2018_OR_GREATER || REVIT2021_OR_GREATER
            return ElectricalSystem.Create(doc, elementIds, ElectricalSystemType.PowerCircuit);
#else
            return doc.Create.NewElectricalSystem(elementIds, ElectricalSystemType.PowerCircuit);
#endif
        }

        public static string ReadParamString(Element el, BuiltInParameter bip)
        {
            if (el == null) return "";
            try
            {
                Parameter p = el.get_Parameter(bip);
                if (p == null || !p.HasValue) return "";
                string s = p.AsString();
                if (!string.IsNullOrEmpty(s)) return s;
                string vs = p.AsValueString();
                return vs ?? "";
            }
            catch { return ""; }
        }

        public static string ReadParamStringByName(Element el, string name)
        {
            if (el == null || string.IsNullOrWhiteSpace(name)) return "";
            try
            {
                Parameter p = el.LookupParameter(name);
                if (p == null || !p.HasValue) return "";
                string s = p.AsString();
                if (!string.IsNullOrEmpty(s)) return s;
                return p.AsValueString() ?? "";
            }
            catch { return ""; }
        }

        public static double ReadParamDouble(Element el, BuiltInParameter bip, double fallback)
        {
            if (el == null) return fallback;
            try
            {
                Parameter p = el.get_Parameter(bip);
                if (p == null || !p.HasValue) return fallback;
                if (p.StorageType == StorageType.Double) return p.AsDouble();
                if (p.StorageType == StorageType.Integer) return (double)p.AsInteger();
                double parsed;
                string rawText = p.AsString();
                if (string.IsNullOrWhiteSpace(rawText)) rawText = p.AsValueString();
                if (double.TryParse(rawText ?? "", NumberStyles.Float, CultureInfo.InvariantCulture, out parsed))
                    return parsed;
            }
            catch { }
            return fallback;
        }

        public static bool SetParameterValue(Element el, string paramName, object rawValue, out string oldValue, out string error)
        {
            return SetParameterValue(el, paramName, rawValue, true, out oldValue, out error);
        }

        public static bool SetParameterValue(Element el, string paramName, object rawValue, bool convertMetricAlias, out string oldValue, out string error)
        {
            oldValue = "";
            error = null;
            if (el == null) { error = "Element is null."; return false; }
            if (string.IsNullOrWhiteSpace(paramName)) { error = "Parameter name is empty."; return false; }
            string trimmedName = paramName.Trim();
            string low = trimmedName.ToLowerInvariant();
            Parameter p = el.LookupParameter(trimmedName);
            if (p == null)
            {
                if (low == "mark" || low == "الرمز") p = el.get_Parameter(BuiltInParameter.ALL_MODEL_MARK);
                else if (low == "comments" || low == "ملاحظات") p = el.get_Parameter(BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS);
                else if (low == "panel" || low == "panel_name" || low == "اللوحة") p = el.get_Parameter(BuiltInParameter.RBS_ELEC_PANEL_NAME);
                else if (low == "elevation" || low == "elevation_m" || low == "height" || low == "height_m" || low == "offset_m" || low == "الارتفاع")
                    p = el.get_Parameter(BuiltInParameter.INSTANCE_ELEVATION_PARAM);
            }
            if (p == null) { error = "Parameter not found: " + paramName; return false; }
            if (p.IsReadOnly) { error = "Parameter is read-only: " + paramName; return false; }

            try
            {
                if (p.StorageType == StorageType.String)
                {
                    oldValue = p.AsString() ?? "";
                    p.Set(Convert.ToString(rawValue, CultureInfo.InvariantCulture) ?? "");
                    return true;
                }
                if (p.StorageType == StorageType.Integer)
                {
                    oldValue = p.AsInteger().ToString(CultureInfo.InvariantCulture);
                    p.Set(Convert.ToInt32(rawValue, CultureInfo.InvariantCulture));
                    return true;
                }
                if (p.StorageType == StorageType.Double)
                {
                    oldValue = p.AsDouble().ToString("R", CultureInfo.InvariantCulture);
                    double num = Convert.ToDouble(rawValue, CultureInfo.InvariantCulture);
                    if (convertMetricAlias && (low == "elevation" || low == "elevation_m" || low == "offset_m" || low == "height" || low == "height_m" || low == "الارتفاع"))
                        num = MetersToFeet(num);
                    p.Set(num);
                    return true;
                }
                if (p.StorageType == StorageType.ElementId)
                {
                    oldValue = GetIdValue(p.AsElementId()).ToString(CultureInfo.InvariantCulture);
                    p.Set(CreateId(Convert.ToInt64(rawValue, CultureInfo.InvariantCulture)));
                    return true;
                }
                error = "Unsupported parameter storage type: " + p.StorageType;
                return false;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
        }
    }
}
