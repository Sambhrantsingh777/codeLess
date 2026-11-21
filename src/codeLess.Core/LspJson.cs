using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace codeLess.Studio
{
    /// <summary>
    /// Small, strict JSON reader / writer for the Language Server Protocol client. Deliberately independent of
    /// JavaScriptSerializer so both builds (legacy .NET Framework and the .NET 8 shim) see identical data:
    /// objects are case-sensitive <c>Dictionary&lt;string, object&gt;</c>, arrays are <c>List&lt;object&gt;</c>,
    /// integers are <c>long</c>, other numbers <c>double</c>. No size limit (completion lists can be several MB).
    /// C# 5 only (compiled by csc.exe for Windows).
    /// </summary>
    public static class LspJson
    {
        public static object Parse(string json)
        {
            if (json == null) return null;
            int i = 0;
            object v = ParseValue(json, ref i);
            return v;
        }

        public static string Serialize(object value)
        {
            StringBuilder sb = new StringBuilder(256);
            Write(sb, value, 0);
            return sb.ToString();
        }

        // ---------------------------------------------------------------- accessors (null-safe)

        public static object Get(object obj, string key)
        {
            Dictionary<string, object> d = obj as Dictionary<string, object>;
            if (d == null || key == null) return null;
            object v;
            return d.TryGetValue(key, out v) ? v : null;
        }

        public static object Path(object obj, params string[] keys)
        {
            object cur = obj;
            for (int i = 0; i < keys.Length && cur != null; i++) cur = Get(cur, keys[i]);
            return cur;
        }

        public static Dictionary<string, object> GetObject(object obj, string key)
        {
            return Get(obj, key) as Dictionary<string, object>;
        }

        public static List<object> GetList(object obj, string key)
        {
            return Get(obj, key) as List<object>;
        }

        public static string GetString(object obj, string key)
        {
            object v = Get(obj, key);
            if (v == null) return null;
            string s = v as string;
            if (s != null) return s;
            if (v is long || v is double || v is int || v is bool) return Convert.ToString(v, CultureInfo.InvariantCulture);
            return null;
        }

        public static int GetInt(object obj, string key, int fallback)
        {
            return ToInt(Get(obj, key), fallback);
        }

        public static bool GetBool(object obj, string key, bool fallback)
        {
            object v = Get(obj, key);
            if (v is bool) return (bool)v;
            return fallback;
        }

        public static bool Has(object obj, string key)
        {
            Dictionary<string, object> d = obj as Dictionary<string, object>;
            return d != null && key != null && d.ContainsKey(key);
        }

        public static int ToInt(object v, int fallback)
        {
            if (v == null) return fallback;
            if (v is long) { long l = (long)v; return l > int.MaxValue ? int.MaxValue : (l < int.MinValue ? int.MinValue : (int)l); }
            if (v is int) return (int)v;
            if (v is double) { double d = (double)v; if (double.IsNaN(d)) return fallback; return d > int.MaxValue ? int.MaxValue : (d < int.MinValue ? int.MinValue : (int)d); }
            string s = v as string;
            int r;
            if (s != null && int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out r)) return r;
            return fallback;
        }

        /// <summary>Convenience builder: Obj("a", 1, "b", "x") -> {"a":1,"b":"x"}.</summary>
        public static Dictionary<string, object> Obj(params object[] keyValues)
        {
            Dictionary<string, object> d = new Dictionary<string, object>(StringComparer.Ordinal);
            for (int i = 0; i + 1 < keyValues.Length; i += 2)
                d[(string)keyValues[i]] = keyValues[i + 1];
            return d;
        }

        public static List<object> Arr(params object[] items)
        {
            return new List<object>(items);
        }

        // ---------------------------------------------------------------- parser

        private static void SkipWs(string s, ref int i)
        {
            while (i < s.Length)
            {
                char c = s[i];
                if (c == ' ' || c == '\t' || c == '\r' || c == '\n') i++;
                else break;
            }
        }

        private static object ParseValue(string s, ref int i)
        {
            SkipWs(s, ref i);
            if (i >= s.Length) throw new FormatException("Unexpected end of JSON");
            char c = s[i];
            switch (c)
            {
                case '{': return ParseObject(s, ref i);
                case '[': return ParseArray(s, ref i);
                case '"': return ParseString(s, ref i);
                case 't': Expect(s, ref i, "true"); return true;
                case 'f': Expect(s, ref i, "false"); return false;
                case 'n': Expect(s, ref i, "null"); return null;
                default: return ParseNumber(s, ref i);
            }
        }

        private static void Expect(string s, ref int i, string word)
        {
            if (string.CompareOrdinal(s, i, word, 0, word.Length) != 0) throw new FormatException("Invalid JSON literal at " + i);
            i += word.Length;
        }

        private static Dictionary<string, object> ParseObject(string s, ref int i)
        {
            Dictionary<string, object> d = new Dictionary<string, object>(StringComparer.Ordinal);
            i++; // {
            SkipWs(s, ref i);
            if (i < s.Length && s[i] == '}') { i++; return d; }
            while (true)
            {
                SkipWs(s, ref i);
                if (i >= s.Length || s[i] != '"') throw new FormatException("Expected property name at " + i);
                string key = ParseString(s, ref i);
                SkipWs(s, ref i);
                if (i >= s.Length || s[i] != ':') throw new FormatException("Expected ':' at " + i);
                i++;
                object v = ParseValue(s, ref i);
                d[key] = v;
                SkipWs(s, ref i);
                if (i >= s.Length) throw new FormatException("Unterminated object");
                if (s[i] == ',') { i++; continue; }
                if (s[i] == '}') { i++; return d; }
                throw new FormatException("Expected ',' or '}' at " + i);
            }
        }

        private static List<object> ParseArray(string s, ref int i)
        {
            List<object> list = new List<object>();
            i++; // [
            SkipWs(s, ref i);
            if (i < s.Length && s[i] == ']') { i++; return list; }
            while (true)
            {
                list.Add(ParseValue(s, ref i));
                SkipWs(s, ref i);
                if (i >= s.Length) throw new FormatException("Unterminated array");
                if (s[i] == ',') { i++; continue; }
                if (s[i] == ']') { i++; return list; }
                throw new FormatException("Expected ',' or ']' at " + i);
            }
        }

        private static string ParseString(string s, ref int i)
        {
            i++; // opening quote
            int start = i;
            // Fast path: no escapes.
            while (i < s.Length)
            {
                char c = s[i];
                if (c == '"') { string fast = s.Substring(start, i - start); i++; return fast; }
                if (c == '\\') break;
                i++;
            }
            StringBuilder sb = new StringBuilder(s, start, i - start, Math.Max(16, (i - start) * 2));
            while (i < s.Length)
            {
                char c = s[i++];
                if (c == '"') return sb.ToString();
                if (c != '\\') { sb.Append(c); continue; }
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
                        if (i + 4 > s.Length) throw new FormatException("Bad \\u escape");
                        int code = int.Parse(s.Substring(i, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
                        sb.Append((char)code);
                        i += 4;
                        break;
                    default: sb.Append(e); break;
                }
            }
            throw new FormatException("Unterminated string");
        }

        private static object ParseNumber(string s, ref int i)
        {
            int start = i;
            bool isFloat = false;
            if (i < s.Length && (s[i] == '-' || s[i] == '+')) i++;
            while (i < s.Length)
            {
                char c = s[i];
                if (c >= '0' && c <= '9') { i++; continue; }
                if (c == '.' || c == 'e' || c == 'E' || ((c == '-' || c == '+') && (s[i - 1] == 'e' || s[i - 1] == 'E'))) { isFloat = true; i++; continue; }
                break;
            }
            if (i == start) throw new FormatException("Unexpected character '" + s[i] + "' at " + i);
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

        // ---------------------------------------------------------------- writer

        private static void Write(StringBuilder sb, object v, int depth)
        {
            if (depth > 200) throw new InvalidOperationException("JSON nesting too deep");
            if (v == null) { sb.Append("null"); return; }
            string s = v as string;
            if (s != null) { WriteString(sb, s); return; }
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
                if (d == Math.Floor(d) && Math.Abs(d) < 1e15) sb.Append(((long)d).ToString(CultureInfo.InvariantCulture));
                else sb.Append(d.ToString("R", CultureInfo.InvariantCulture));
                return;
            }
            if (v is char) { WriteString(sb, v.ToString()); return; }
            IDictionary dict = v as IDictionary;
            if (dict != null)
            {
                sb.Append('{');
                bool first = true;
                foreach (DictionaryEntry e in dict)
                {
                    if (!first) sb.Append(',');
                    first = false;
                    WriteString(sb, Convert.ToString(e.Key, CultureInfo.InvariantCulture));
                    sb.Append(':');
                    Write(sb, e.Value, depth + 1);
                }
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
                    Write(sb, item, depth + 1);
                }
                sb.Append(']');
                return;
            }
            // Plain objects: public fields and readable properties.
            sb.Append('{');
            bool f = true;
            Type t = v.GetType();
            foreach (System.Reflection.FieldInfo fi in t.GetFields(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance))
            {
                if (!f) sb.Append(',');
                f = false;
                WriteString(sb, fi.Name);
                sb.Append(':');
                Write(sb, fi.GetValue(v), depth + 1);
            }
            foreach (System.Reflection.PropertyInfo pi in t.GetProperties(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance))
            {
                if (!pi.CanRead || pi.GetIndexParameters().Length > 0) continue;
                if (!f) sb.Append(',');
                f = false;
                WriteString(sb, pi.Name);
                sb.Append(':');
                Write(sb, pi.GetValue(v, null), depth + 1);
            }
            sb.Append('}');
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
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    case '\b': sb.Append("\\b"); break;
                    case '\f': sb.Append("\\f"); break;
                    default:
                        if (c < 0x20 || c == '\u2028' || c == '\u2029')
                            sb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                        else
                            sb.Append(c);
                        break;
                }
            }
            sb.Append('"');
        }
    }
}
