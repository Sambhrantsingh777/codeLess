using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace codeLess.Studio
{
    /// <summary>
    /// Small, dependency-free JSON / JSONC reader and writer used by the Debug Adapter Protocol client and the
    /// launch.json parser. Objects parse to Dictionary&lt;string, object&gt; (ordinal keys, insertion order kept by
    /// the writer), arrays to List&lt;object&gt;, numbers to long or double, plus string / bool / null. JSONC extras
    /// (// and /* */ comments, trailing commas) are accepted on read. Strict C# 5: compiled by the legacy Windows csc.
    /// </summary>
    public static class DapJson
    {
        public static object Parse(string text)
        {
            if (text == null) throw new FormatException("JSON text is null");
            int i = 0;
            object v = ParseValue(text, ref i);
            SkipWs(text, ref i);
            if (i < text.Length) throw new FormatException("Unexpected trailing characters at offset " + i);
            return v;
        }

        public static Dictionary<string, object> ParseObject(string text)
        {
            Dictionary<string, object> d = Parse(text) as Dictionary<string, object>;
            if (d == null) throw new FormatException("JSON value is not an object");
            return d;
        }

        /// <summary>Parses without throwing; returns null on malformed input.</summary>
        public static object TryParse(string text, out string error)
        {
            error = null;
            try { return Parse(text); }
            catch (Exception ex) { error = ex.Message; return null; }
        }

        private static void SkipWs(string s, ref int i)
        {
            while (i < s.Length)
            {
                char c = s[i];
                if (c == ' ' || c == '\t' || c == '\r' || c == '\n' || c == '\uFEFF') { i++; continue; }
                if (c == '/' && i + 1 < s.Length)
                {
                    if (s[i + 1] == '/')
                    {
                        i += 2;
                        while (i < s.Length && s[i] != '\n') i++;
                        continue;
                    }
                    if (s[i + 1] == '*')
                    {
                        int end = s.IndexOf("*/", i + 2, StringComparison.Ordinal);
                        i = end < 0 ? s.Length : end + 2;
                        continue;
                    }
                }
                break;
            }
        }

        private static object ParseValue(string s, ref int i)
        {
            SkipWs(s, ref i);
            if (i >= s.Length) throw new FormatException("Unexpected end of JSON");
            char c = s[i];
            if (c == '{') return ParseObj(s, ref i);
            if (c == '[') return ParseArr(s, ref i);
            if (c == '"') return ParseStr(s, ref i);
            if (Match(s, i, "true")) { i += 4; return true; }
            if (Match(s, i, "false")) { i += 5; return false; }
            if (Match(s, i, "null")) { i += 4; return null; }
            if (c == '-' || (c >= '0' && c <= '9')) return ParseNum(s, ref i);
            throw new FormatException("Unexpected character '" + c + "' at offset " + i);
        }

        private static bool Match(string s, int i, string word)
        {
            return i + word.Length <= s.Length && string.CompareOrdinal(s, i, word, 0, word.Length) == 0;
        }

        private static Dictionary<string, object> ParseObj(string s, ref int i)
        {
            Dictionary<string, object> d = new Dictionary<string, object>(StringComparer.Ordinal);
            i++;
            while (true)
            {
                SkipWs(s, ref i);
                if (i >= s.Length) throw new FormatException("Unterminated object");
                if (s[i] == '}') { i++; return d; }
                if (s[i] != '"') throw new FormatException("Expected property name at offset " + i);
                string key = ParseStr(s, ref i);
                SkipWs(s, ref i);
                if (i >= s.Length || s[i] != ':') throw new FormatException("Expected ':' at offset " + i);
                i++;
                object v = ParseValue(s, ref i);
                d[key] = v;
                SkipWs(s, ref i);
                if (i >= s.Length) throw new FormatException("Unterminated object");
                if (s[i] == ',') { i++; continue; } // trailing comma tolerated: the loop sees '}' next
                if (s[i] == '}') { i++; return d; }
                throw new FormatException("Expected ',' or '}' at offset " + i);
            }
        }

        private static List<object> ParseArr(string s, ref int i)
        {
            List<object> list = new List<object>();
            i++;
            while (true)
            {
                SkipWs(s, ref i);
                if (i >= s.Length) throw new FormatException("Unterminated array");
                if (s[i] == ']') { i++; return list; }
                list.Add(ParseValue(s, ref i));
                SkipWs(s, ref i);
                if (i >= s.Length) throw new FormatException("Unterminated array");
                if (s[i] == ',') { i++; continue; }
                if (s[i] == ']') { i++; return list; }
                throw new FormatException("Expected ',' or ']' at offset " + i);
            }
        }

        private static string ParseStr(string s, ref int i)
        {
            i++; // opening quote
            StringBuilder sb = null;
            int runStart = i;
            while (i < s.Length)
            {
                char c = s[i];
                if (c == '"')
                {
                    string tail = s.Substring(runStart, i - runStart);
                    i++;
                    if (sb == null) return tail;
                    sb.Append(tail);
                    return sb.ToString();
                }
                if (c == '\\')
                {
                    if (sb == null) sb = new StringBuilder();
                    sb.Append(s, runStart, i - runStart);
                    i++;
                    if (i >= s.Length) break;
                    char e = s[i++];
                    switch (e)
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
                            {
                                if (i + 4 > s.Length) throw new FormatException("Bad \\u escape");
                                int code = int.Parse(s.Substring(i, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
                                sb.Append((char)code);
                                i += 4;
                                break;
                            }
                        default: sb.Append(e); break;
                    }
                    runStart = i;
                    continue;
                }
                i++;
            }
            throw new FormatException("Unterminated string");
        }

        private static object ParseNum(string s, ref int i)
        {
            int start = i;
            bool isFloat = false;
            if (s[i] == '-') i++;
            while (i < s.Length)
            {
                char c = s[i];
                if (c >= '0' && c <= '9') { i++; continue; }
                if (c == '.' || c == 'e' || c == 'E' || c == '+' || c == '-') { isFloat = true; i++; continue; }
                break;
            }
            string tok = s.Substring(start, i - start);
            if (!isFloat)
            {
                long l;
                if (long.TryParse(tok, NumberStyles.Integer, CultureInfo.InvariantCulture, out l)) return l;
            }
            double d;
            if (double.TryParse(tok, NumberStyles.Float, CultureInfo.InvariantCulture, out d)) return d;
            throw new FormatException("Bad number '" + tok + "'");
        }

        // ---- Writer ----

        public static string Serialize(object value)
        {
            StringBuilder sb = new StringBuilder();
            Write(sb, value, false, 0);
            return sb.ToString();
        }

        public static string SerializeIndented(object value)
        {
            StringBuilder sb = new StringBuilder();
            Write(sb, value, true, 0);
            return sb.ToString();
        }

        private static void NewLine(StringBuilder sb, bool indent, int depth)
        {
            if (!indent) return;
            sb.Append('\n');
            sb.Append(' ', depth * 4);
        }

        private static void Write(StringBuilder sb, object v, bool indent, int depth)
        {
            if (v == null) { sb.Append("null"); return; }
            string str = v as string;
            if (str != null) { WriteString(sb, str); return; }
            if (v is bool) { sb.Append((bool)v ? "true" : "false"); return; }
            if (v is int || v is long || v is short || v is byte || v is uint || v is ulong || v is ushort || v is sbyte)
            {
                sb.Append(Convert.ToString(v, CultureInfo.InvariantCulture));
                return;
            }
            if (v is double || v is float || v is decimal)
            {
                double d = Convert.ToDouble(v, CultureInfo.InvariantCulture);
                if (double.IsNaN(d) || double.IsInfinity(d)) { sb.Append("null"); return; }
                sb.Append(d.ToString("R", CultureInfo.InvariantCulture));
                return;
            }
            IDictionary dict = v as IDictionary;
            if (dict != null)
            {
                sb.Append('{');
                bool first = true;
                foreach (DictionaryEntry kv in dict)
                {
                    if (!first) sb.Append(',');
                    first = false;
                    NewLine(sb, indent, depth + 1);
                    WriteString(sb, Convert.ToString(kv.Key, CultureInfo.InvariantCulture));
                    sb.Append(indent ? ": " : ":");
                    Write(sb, kv.Value, indent, depth + 1);
                }
                if (!first) NewLine(sb, indent, depth);
                sb.Append('}');
                return;
            }
            IEnumerable en = v as IEnumerable;
            if (en != null)
            {
                sb.Append('[');
                bool first = true;
                foreach (object item in en)
                {
                    if (!first) sb.Append(',');
                    first = false;
                    NewLine(sb, indent, depth + 1);
                    Write(sb, item, indent, depth + 1);
                }
                if (!first) NewLine(sb, indent, depth);
                sb.Append(']');
                return;
            }
            WriteString(sb, Convert.ToString(v, CultureInfo.InvariantCulture));
        }

        public static void WriteString(StringBuilder sb, string s)
        {
            sb.Append('"');
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    case '\b': sb.Append("\\b"); break;
                    case '\f': sb.Append("\\f"); break;
                    default:
                        if (c < 0x20) sb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                        else sb.Append(c);
                        break;
                }
            }
            sb.Append('"');
        }

        // ---- Typed accessors over parsed values ----

        public static Dictionary<string, object> Obj(object o, string key)
        {
            Dictionary<string, object> d = o as Dictionary<string, object>;
            if (d == null || key == null) return null;
            object v;
            return d.TryGetValue(key, out v) ? v as Dictionary<string, object> : null;
        }

        public static List<object> Arr(object o, string key)
        {
            Dictionary<string, object> d = o as Dictionary<string, object>;
            if (d == null) return null;
            object v;
            return d.TryGetValue(key, out v) ? v as List<object> : null;
        }

        public static string Str(object o, string key)
        {
            Dictionary<string, object> d = o as Dictionary<string, object>;
            if (d == null) return null;
            object v;
            if (!d.TryGetValue(key, out v) || v == null) return null;
            string s = v as string;
            if (s != null) return s;
            if (v is bool) return (bool)v ? "true" : "false";
            if (v is long || v is double) return Convert.ToString(v, CultureInfo.InvariantCulture);
            return null;
        }

        public static long Long(object o, string key, long fallback)
        {
            Dictionary<string, object> d = o as Dictionary<string, object>;
            if (d == null) return fallback;
            object v;
            if (!d.TryGetValue(key, out v) || v == null) return fallback;
            if (v is long) return (long)v;
            if (v is double) return (long)(double)v;
            if (v is int) return (int)v;
            long parsed;
            string s = v as string;
            if (s != null && long.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out parsed)) return parsed;
            return fallback;
        }

        public static int Int(object o, string key, int fallback)
        {
            long l = Long(o, key, fallback);
            if (l > int.MaxValue || l < int.MinValue) return fallback;
            return (int)l;
        }

        public static bool Bool(object o, string key, bool fallback)
        {
            Dictionary<string, object> d = o as Dictionary<string, object>;
            if (d == null) return fallback;
            object v;
            if (!d.TryGetValue(key, out v) || v == null) return fallback;
            if (v is bool) return (bool)v;
            string s = v as string;
            if (s != null) { bool b; if (bool.TryParse(s, out b)) return b; }
            return fallback;
        }

        public static bool Has(object o, string key)
        {
            Dictionary<string, object> d = o as Dictionary<string, object>;
            return d != null && d.ContainsKey(key);
        }

        /// <summary>Deep copy of a parsed JSON tree (dictionaries and lists are cloned, leaves shared).</summary>
        public static object DeepClone(object v)
        {
            Dictionary<string, object> d = v as Dictionary<string, object>;
            if (d != null)
            {
                Dictionary<string, object> c = new Dictionary<string, object>(StringComparer.Ordinal);
                foreach (KeyValuePair<string, object> kv in d) c[kv.Key] = DeepClone(kv.Value);
                return c;
            }
            List<object> l = v as List<object>;
            if (l != null)
            {
                List<object> c = new List<object>(l.Count);
                foreach (object item in l) c.Add(DeepClone(item));
                return c;
            }
            return v;
        }

        /// <summary>Convenience builder: Dict("a", 1, "b", "x") -> {"a":1,"b":"x"}; null values are skipped.</summary>
        public static Dictionary<string, object> Dict(params object[] keyValues)
        {
            Dictionary<string, object> d = new Dictionary<string, object>(StringComparer.Ordinal);
            for (int i = 0; i + 1 < keyValues.Length; i += 2)
            {
                if (keyValues[i + 1] == null) continue;
                d[(string)keyValues[i]] = keyValues[i + 1];
            }
            return d;
        }
    }
}
