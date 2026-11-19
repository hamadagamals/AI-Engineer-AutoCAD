using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Security.Cryptography;

using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;

namespace AIEngineer.Revit
{
    [DataContract]
    internal sealed class SchoolPowerMark
    {
        [DataMember(Name = "first_curve_id", Order = 1)] public long FirstCurveId;
        [DataMember(Name = "curve_ids", Order = 2)] public List<long> CurveIds;
        [DataMember(Name = "kind", Order = 3)] public string Kind;
        [DataMember(Name = "view_id", Order = 4)] public long ViewId;
        [DataMember(Name = "level_id", Order = 5)] public long LevelId;
        [DataMember(Name = "x_mm", Order = 6)] public double Xmm;
        [DataMember(Name = "y_mm", Order = 7)] public double Ymm;
        [DataMember(Name = "room_ids", Order = 8)] public List<long> RoomIds;
    }

    [DataContract]
    internal sealed class SchoolPowerMarkReport
    {
        [DataMember(Name = "schema_version", Order = 1)] public int SchemaVersion;
        [DataMember(Name = "request_id", Order = 2)] public string RequestId;
        [DataMember(Name = "document_title", Order = 3)] public string DocumentTitle;
        [DataMember(Name = "document_sha256", Order = 4)] public string DocumentSha256;
        [DataMember(Name = "total_count", Order = 5)] public int TotalCount;
        [DataMember(Name = "returned_count", Order = 6)] public int ReturnedCount;
        [DataMember(Name = "start_after_element_id", Order = 7)] public long StartAfterElementId;
        [DataMember(Name = "next_start_after_element_id", Order = 8)] public long NextStartAfterElementId;
        [DataMember(Name = "truncated", Order = 9)] public bool Truncated;
        [DataMember(Name = "counts_by_kind", Order = 10)] public Dictionary<string, int> CountsByKind;
        [DataMember(Name = "marks", Order = 11)] public List<SchoolPowerMark> Marks;
        [DataMember(Name = "provider_egress_performed", Order = 12)] public bool ProviderEgressPerformed;
        [DataMember(Name = "document_mutation_performed", Order = 13)] public bool DocumentMutationPerformed;
    }

    internal static class ReadOnlySchoolPowerMarks
    {
        private const string Prefix = "AI REVIEW V5 - ELECTRICAL - POWER - ";
        private static readonly Dictionary<string, int> Segments = new Dictionary<string, int>
        {
            { "AI-V3-POWER-DUPLEX", 10 },
            { "AI-V3-POWER-GFI", 6 },
            { "AI-V3-POWER-DEDICATED", 4 },
            { "AI-V3-POWER-PANEL", 6 }
        };
        private static readonly Dictionary<string, int> Expected = new Dictionary<string, int>
        {
            { "DUPLEX", 426 }, { "GFI", 21 }, { "DEDICATED", 78 }, { "PANEL", 2 }
        };

        public static string Capture(Document document, string requestId, string expectedSha,
            int maximum, long startAfter)
        {
            if (document.IsFamilyDocument || string.IsNullOrWhiteSpace(document.PathName)
                || maximum < 1 || maximum > 32 || startAfter < 0
                || !string.Equals(Hash(document.PathName), expectedSha, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("School V5 power mark request or source hash is invalid.");
            List<ViewPlan> views = new FilteredElementCollector(document).OfClass(typeof(ViewPlan))
                .Cast<ViewPlan>().Where(view => !view.IsTemplate && view.Name.StartsWith(Prefix, StringComparison.Ordinal))
                .OrderBy(view => view.Id.Value).ToList();
            if (views.Count != 3)
                throw new InvalidDataException("School V5 power views: expected 3, found " + views.Count + ".");
            List<Room> rooms = new FilteredElementCollector(document)
                .OfCategory(BuiltInCategory.OST_Rooms).WhereElementIsNotElementType()
                .OfType<Room>().Where(room => room.Area > 0).ToList();
            List<SchoolPowerMark> marks = new List<SchoolPowerMark>();
            foreach (ViewPlan view in views)
            {
                if (view.GenLevel == null)
                    throw new InvalidDataException("School power review view has no level.");
                List<DetailCurve> lines = new FilteredElementCollector(document, view.Id)
                    .OfClass(typeof(CurveElement)).Cast<CurveElement>().OfType<DetailCurve>()
                    .Where(line => line.LineStyle != null && Segments.ContainsKey(line.LineStyle.Name)).ToList();
                foreach (KeyValuePair<string, int> entry in Segments)
                {
                    List<DetailCurve> selected = lines.Where(line => line.LineStyle.Name == entry.Key)
                        .OrderBy(line => line.Id.Value).ToList();
                    if (selected.Count % entry.Value != 0)
                        throw new InvalidDataException("School power marker segments are incomplete: view="
                            + view.Name + "; style=" + entry.Key + "; curves=" + selected.Count + ".");
                    for (int offset = 0; offset < selected.Count; offset += entry.Value)
                    {
                        List<DetailCurve> group = selected.Skip(offset).Take(entry.Value).ToList();
                        string kind = entry.Key.Substring("AI-V3-POWER-".Length);
                        List<XYZ> endpoints = new List<XYZ>();
                        foreach (DetailCurve curve in group)
                        {
                            Line line = curve.GeometryCurve as Line;
                            if (line == null) throw new InvalidDataException("School power marker has a non-line segment.");
                            endpoints.Add(line.GetEndPoint(0));
                            endpoints.Add(line.GetEndPoint(1));
                        }
                        double minX = endpoints.Min(point => point.X), maxX = endpoints.Max(point => point.X);
                        double minY = endpoints.Min(point => point.Y), maxY = endpoints.Max(point => point.Y);
                        double expectedWidth = kind == "PANEL" ? 900 : kind == "DUPLEX" ? 260 : kind == "GFI" ? 280 : 340;
                        double expectedHeight = kind == "PANEL" ? 600 : expectedWidth;
                        if (Math.Abs(Mm(maxX - minX) - expectedWidth) > 0.1
                            || Math.Abs(Mm(maxY - minY) - expectedHeight) > 0.1)
                            throw new InvalidDataException("School power marker geometry differs: view="
                                + view.Name + "; style=" + entry.Key + "; first curve=" + group[0].Id.Value + ".");
                        XYZ center = new XYZ((minX + maxX) / 2.0, (minY + maxY) / 2.0,
                            view.GenLevel.Elevation + UnitUtils.ConvertToInternalUnits(300, UnitTypeId.Millimeters));
                        List<long> roomIds = rooms.Where(room => room.LevelId.Value == view.GenLevel.Id.Value
                            && room.IsPointInRoom(center)).Select(room => room.Id.Value).ToList();
                        if (kind != "PANEL" && roomIds.Count != 1)
                            throw new InvalidDataException("School power marker lacks one room: view="
                                + view.Name + "; kind=" + kind + "; first curve=" + group[0].Id.Value + ".");
                        marks.Add(new SchoolPowerMark
                        {
                            FirstCurveId = group[0].Id.Value,
                            CurveIds = group.Select(curve => curve.Id.Value).ToList(),
                            Kind = kind, ViewId = view.Id.Value, LevelId = view.GenLevel.Id.Value,
                            Xmm = Math.Round(Mm(center.X), 1), Ymm = Math.Round(Mm(center.Y), 1),
                            RoomIds = roomIds
                        });
                    }
                }
            }
            Dictionary<string, int> counts = marks.GroupBy(mark => mark.Kind)
                .ToDictionary(group => group.Key, group => group.Count());
            if (Expected.Any(pair => !counts.ContainsKey(pair.Key) || counts[pair.Key] != pair.Value)
                || marks.Count != 527 || marks.Select(mark => mark.FirstCurveId).Distinct().Count() != marks.Count)
                throw new InvalidDataException("School V5 power marker totals differ: "
                    + string.Join(",", counts.Select(pair => pair.Key + "=" + pair.Value)) + ".");
            marks = marks.OrderBy(mark => mark.FirstCurveId).ToList();
            List<SchoolPowerMark> page = marks.Where(mark => mark.FirstCurveId > startAfter)
                .Take(maximum).ToList();
            bool more = page.Count > 0 && marks.Any(mark => mark.FirstCurveId > page[page.Count - 1].FirstCurveId);
            SchoolPowerMarkReport report = new SchoolPowerMarkReport
            {
                SchemaVersion = 1, RequestId = requestId, DocumentTitle = document.Title,
                DocumentSha256 = expectedSha, TotalCount = marks.Count, ReturnedCount = page.Count,
                StartAfterElementId = startAfter,
                NextStartAfterElementId = more ? page[page.Count - 1].FirstCurveId : 0,
                Truncated = more, CountsByKind = counts, Marks = page,
                ProviderEgressPerformed = false, DocumentMutationPerformed = false
            };
            string directory = Path.Combine(Environment.GetFolderPath(
                Environment.SpecialFolder.LocalApplicationData), "AI-Engineer", "revit", "school-power-marks");
            Directory.CreateDirectory(directory);
            string file = Path.Combine(directory, requestId + ".json");
            DataContractJsonSerializer serializer = new DataContractJsonSerializer(
                typeof(SchoolPowerMarkReport),
                new DataContractJsonSerializerSettings { UseSimpleDictionaryFormat = true });
            using (FileStream stream = new FileStream(file, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            { serializer.WriteObject(stream, report); stream.Flush(true); }
            return "school-power-marks/" + requestId + ".json";
        }

        private static double Mm(double value)
        { return UnitUtils.ConvertFromInternalUnits(value, UnitTypeId.Millimeters); }
        private static string Hash(string path)
        {
            using (SHA256 sha = SHA256.Create())
            using (FileStream stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                return BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", string.Empty);
        }
    }
}
