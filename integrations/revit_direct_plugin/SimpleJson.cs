using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace AIEngineer.Revit.Direct
{
    /// <summary>
    /// Zero-dependency RFC-8259 JSON parser/serializer compatible with both
    /// .NET Framework 4.5-4.8 (Revit 2016-2024) and .NET 8 (Revit 2025-2026+).
    /// </summary>
    internal static class SimpleJson
    {
        private const int MaxDepth = 64;

        public static object Deserialize(string json)
        {
            if (string.IsNullOrWhiteSpace(json)) return null;
            int idx = 0;
            if (json.Length > 0 && json[0] == '\uFEFF') idx++;
            SkipWs(json, ref idx);
            if (idx >= json.Length) return null;
            object val = ParseValue(json, ref idx, 0);
            SkipWs(json, ref idx);
            if (idx < json.Length)
                throw new FormatException("Unexpected trailing token in JSON at index " + idx);
            return val;
        }

        public static Dictionary<string, object> DeserializeObject(string json)
        {
            return Deserialize(json) as Dictionary<string, object>;
        }

        public static string Serialize(object obj)
        {
            StringBuilder sb = new StringBuilder(256);
            WriteValue(sb, obj);
            return sb.ToString();
        }

        private static void WriteValue(StringBuilder sb, object val)
        {
            if (val == null) { sb.Append("null"); return; }
            if (val is string) { WriteString(sb, (string)val); return; }
            if (val is bool) { sb.Append(((bool)val) ? "true" : "false"); return; }
            if (val is int || val is long || val is short || val is byte || val is sbyte || val is ushort || val is uint || val is ulong)
            {
                sb.Append(Convert.ToString(val, CultureInfo.InvariantCulture));
                return;
            }
            if (val is double || val is float || val is decimal)
            {
                double d = Convert.ToDouble(val, CultureInfo.InvariantCulture);
                if (double.IsNaN(d) || double.IsInfinity(d)) sb.Append("0");
                else sb.Append(d.ToString("R", CultureInfo.InvariantCulture));
                return;
            }
            IDictionary dict = val as IDictionary;
            if (dict != null)
            {
                sb.Append('{');
                bool first = true;
                foreach (DictionaryEntry de in dict)
                {
                    if (!first) sb.Append(',');
                    first = false;
                    WriteString(sb, Convert.ToString(de.Key, CultureInfo.InvariantCulture) ?? "");
                    sb.Append(':');
                    WriteValue(sb, de.Value);
                }
                sb.Append('}');
                return;
            }
            IEnumerable seq = val as IEnumerable;
            if (seq != null)
            {
                sb.Append('[');
                bool first = true;
                foreach (object item in seq)
                {
                    if (!first) sb.Append(',');
                    first = false;
                    WriteValue(sb, item);
                }
                sb.Append(']');
                return;
            }
            WriteString(sb, Convert.ToString(val, CultureInfo.InvariantCulture) ?? "");
        }

        private static void WriteString(StringBuilder sb, string s)
        {
            sb.Append('"');
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\b': sb.Append("\\b"); break;
                    case '\f': sb.Append("\\f"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (c < 0x20)
                        {
                            sb.Append("\\u");
                            sb.Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                        }
                        else
                        {
                            sb.Append(c);
                        }
                        break;
                }
            }
            sb.Append('"');
        }

        private static void SkipWs(string s, ref int i)
        {
            while (i < s.Length)
            {
                char c = s[i];
                if (c == ' ' || c == '\t' || c == '\r' || c == '\n' || c == '\uFEFF') i++;
                else break;
            }
        }

        private static object ParseValue(string s, ref int i, int depth)
        {
            if (depth > MaxDepth)
                throw new FormatException("JSON maximum nesting depth exceeded.");
            SkipWs(s, ref i);
            if (i >= s.Length) throw new FormatException("Unexpected end of JSON input.");
            char c = s[i];
            if (c == '{') return ParseObject(s, ref i, depth + 1);
            if (c == '[') return ParseArray(s, ref i, depth + 1);
            if (c == '"') return ParseString(s, ref i);
            if (c == 't' && i + 4 <= s.Length && s.Substring(i, 4) == "true") { i += 4; return true; }
            if (c == 'f' && i + 5 <= s.Length && s.Substring(i, 5) == "false") { i += 5; return false; }
            if (c == 'n' && i + 4 <= s.Length && s.Substring(i, 4) == "null") { i += 4; return null; }
            if (c == '-' || (c >= '0' && c <= '9')) return ParseNumber(s, ref i);
            throw new FormatException("Unexpected JSON token at index " + i);
        }

        private static Dictionary<string, object> ParseObject(string s, ref int i, int depth)
        {
            if (depth > MaxDepth)
                throw new FormatException("JSON maximum nesting depth exceeded.");
            Dictionary<string, object> dict = new Dictionary<string, object>(StringComparer.Ordinal);
            i++; // '{'
            SkipWs(s, ref i);
            if (i < s.Length && s[i] == '}') { i++; return dict; }
            while (i < s.Length)
            {
                SkipWs(s, ref i);
                string key = ParseString(s, ref i);
                SkipWs(s, ref i);
                if (i >= s.Length || s[i] != ':') throw new FormatException("Expected ':' in JSON object at " + i);
                i++;
                object val = ParseValue(s, ref i, depth);
                dict[key] = val;
                SkipWs(s, ref i);
                if (i < s.Length && s[i] == ',') { i++; continue; }
                if (i < s.Length && s[i] == '}') { i++; return dict; }
                throw new FormatException("Expected ',' or '}' in JSON object at " + i);
            }
            throw new FormatException("Unterminated JSON object.");
        }

        private static object[] ParseArray(string s, ref int i, int depth)
        {
            if (depth > MaxDepth)
                throw new FormatException("JSON maximum nesting depth exceeded.");
            List<object> list = new List<object>();
            i++; // '['
            SkipWs(s, ref i);
            if (i < s.Length && s[i] == ']') { i++; return list.ToArray(); }
            while (i < s.Length)
            {
                list.Add(ParseValue(s, ref i, depth));
                SkipWs(s, ref i);
                if (i < s.Length && s[i] == ',') { i++; continue; }
                if (i < s.Length && s[i] == ']') { i++; return list.ToArray(); }
                throw new FormatException("Expected ',' or ']' in JSON array at " + i);
            }
            throw new FormatException("Unterminated JSON array.");
        }

        private static bool IsHexDigit(char c)
        {
            return (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F');
        }

        private static string ParseString(string s, ref int i)
        {
            if (i >= s.Length || s[i] != '"') throw new FormatException("Expected '\"' at " + i);
            i++;
            StringBuilder sb = new StringBuilder();
            while (i < s.Length)
            {
                char c = s[i++];
                if (c == '"') return sb.ToString();
                if (c < 0x20) throw new FormatException("Unescaped control character in JSON string.");
                if (c == '\\')
                {
                    if (i >= s.Length) throw new FormatException("Unterminated escape in JSON string.");
                    char esc = s[i++];
                    switch (esc)
                    {
                        case '"': sb.Append('"'); break;
                        case '\\': sb.Append('\\'); break;
                        case '/': sb.Append('/'); break;
                        case 'b': sb.Append('\b'); break;
                        case 'f': sb.Append('\f'); break;
                        case 'n': sb.Append('\n'); break;
                        case 'r': sb.Append('\r'); break;
                        case 't': sb.Append('\t'); break;
                        case 'u':
                            if (i + 4 > s.Length) throw new FormatException("Invalid unicode escape.");
                            string hex = s.Substring(i, 4);
                            for (int h = 0; h < 4; h++)
                            {
                                if (!IsHexDigit(hex[h])) throw new FormatException("Invalid hex digit in unicode escape.");
                            }
                            sb.Append((char)Convert.ToInt32(hex, 16));
                            i += 4;
                            break;
                        default:
                            throw new FormatException("Invalid escape character '\\" + esc + "' in JSON string.");
                    }
                }
                else
                {
                    sb.Append(c);
                }
            }
            throw new FormatException("Unterminated JSON string.");
        }

        private static object ParseNumber(string s, ref int i)
        {
            int start = i;
            bool isFloat = false;
            if (s[i] == '-') i++;
            while (i < s.Length && s[i] >= '0' && s[i] <= '9') i++;
            if (i < s.Length && s[i] == '.') { isFloat = true; i++; while (i < s.Length && s[i] >= '0' && s[i] <= '9') i++; }
            if (i < s.Length && (s[i] == 'e' || s[i] == 'E'))
            {
                isFloat = true;
                i++;
                if (i < s.Length && (s[i] == '+' || s[i] == '-')) i++;
                while (i < s.Length && s[i] >= '0' && s[i] <= '9') i++;
            }
            string numStr = s.Substring(start, i - start);
            if (!isFloat)
            {
                long lv;
                if (long.TryParse(numStr, NumberStyles.Integer, CultureInfo.InvariantCulture, out lv))
                {
                    if (lv >= int.MinValue && lv <= int.MaxValue) return (int)lv;
                    return lv;
                }
            }
            try
            {
                return double.Parse(numStr, NumberStyles.Float, CultureInfo.InvariantCulture);
            }
            catch (OverflowException ex)
            {
                throw new FormatException("JSON number overflow.", ex);
            }
        }
    }
}
