#if !NETFRAMEWORK
using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using System.Text;

namespace System.Web.Script.Serialization
{
    public class JavaScriptSerializer
    {
        public int MaxJsonLength { get; set; } = int.MaxValue;

        public T Deserialize<T>(string input)
        {
            if (string.IsNullOrWhiteSpace(input))
                return default(T);

            int idx = 0;
            object raw = ParseValue(input, ref idx);
            return (T)ConvertValue(raw, typeof(T));
        }

        public object DeserializeObject(string input)
        {
            if (string.IsNullOrWhiteSpace(input))
                return null;

            int idx = 0;
            return ParseValue(input, ref idx);
        }

        public string Serialize(object obj)
        {
            StringBuilder sb = new StringBuilder();
            WriteValue(sb, obj);
            return sb.ToString();
        }

        private static object ConvertValue(object raw, Type targetType)
        {
            if (raw == null) return null;
            if (targetType == typeof(object) || targetType.IsAssignableFrom(raw.GetType()))
                return raw;

            Type underlying = Nullable.GetUnderlyingType(targetType);
            if (underlying != null)
                return ConvertValue(raw, underlying);

            if (targetType == typeof(string))
                return Convert.ToString(raw, CultureInfo.InvariantCulture);
            if (targetType == typeof(int))
                return Convert.ToInt32(raw, CultureInfo.InvariantCulture);
            if (targetType == typeof(long))
                return Convert.ToInt64(raw, CultureInfo.InvariantCulture);
            if (targetType == typeof(float))
                return Convert.ToSingle(raw, CultureInfo.InvariantCulture);
            if (targetType == typeof(double))
                return Convert.ToDouble(raw, CultureInfo.InvariantCulture);
            if (targetType == typeof(bool))
                return Convert.ToBoolean(raw, CultureInfo.InvariantCulture);

            if (targetType.IsArray)
            {
                Type elemType = targetType.GetElementType();
                IList srcList = raw as IList;
                if (srcList != null)
                {
                    Array arr = Array.CreateInstance(elemType, srcList.Count);
                    for (int i = 0; i < srcList.Count; i++)
                        arr.SetValue(ConvertValue(srcList[i], elemType), i);
                    return arr;
                }
            }

            if (targetType.IsGenericType && targetType.GetGenericTypeDefinition() == typeof(List<>))
            {
                Type elemType = targetType.GetGenericArguments()[0];
                IList listInstance = (IList)Activator.CreateInstance(targetType);
                IList srcList = raw as IList;
                if (srcList != null)
                {
                    foreach (object item in srcList)
                        listInstance.Add(ConvertValue(item, elemType));
                }
                return listInstance;
            }

            var dict = raw as Dictionary<string, object>;
            if (dict != null)
            {
                if (targetType == typeof(Dictionary<string, object>))
                    return dict;

                if (targetType.IsGenericType && targetType.GetGenericTypeDefinition() == typeof(Dictionary<,>))
                {
                    Type valType = targetType.GetGenericArguments()[1];
                    IDictionary typedDict = (IDictionary)Activator.CreateInstance(targetType);
                    foreach (var kv in dict)
                        typedDict[kv.Key] = ConvertValue(kv.Value, valType);
                    return typedDict;
                }

                object instance = Activator.CreateInstance(targetType);
                foreach (PropertyInfo prop in targetType.GetProperties(BindingFlags.Public | BindingFlags.Instance))
                {
                    if (!prop.CanWrite) continue;
                    object val;
                    if (dict.TryGetValue(prop.Name, out val))
                    {
                        try { prop.SetValue(instance, ConvertValue(val, prop.PropertyType), null); } catch { }
                    }
                }
                foreach (FieldInfo field in targetType.GetFields(BindingFlags.Public | BindingFlags.Instance))
                {
                    object val;
                    if (dict.TryGetValue(field.Name, out val))
                    {
                        try { field.SetValue(instance, ConvertValue(val, field.FieldType)); } catch { }
                    }
                }
                return instance;
            }

            return Convert.ChangeType(raw, targetType, CultureInfo.InvariantCulture);
        }

        private static void SkipWhitespace(string s, ref int idx)
        {
            while (idx < s.Length)
            {
                char c = s[idx];
                if (c == ' ' || c == '\t' || c == '\r' || c == '\n')
                    idx++;
                else
                    break;
            }
        }

        private static object ParseValue(string s, ref int idx)
        {
            SkipWhitespace(s, ref idx);
            if (idx >= s.Length) return null;

            char c = s[idx];
            if (c == '{') return ParseObject(s, ref idx);
            if (c == '[') return ParseArray(s, ref idx);
            if (c == '"') return ParseString(s, ref idx);
            if (c == 't' || c == 'T')
            {
                if (idx + 4 <= s.Length && string.Compare(s, idx, "true", 0, 4, StringComparison.OrdinalIgnoreCase) == 0)
                {
                    idx += 4;
                    return true;
                }
            }
            if (c == 'f' || c == 'F')
            {
                if (idx + 5 <= s.Length && string.Compare(s, idx, "false", 0, 5, StringComparison.OrdinalIgnoreCase) == 0)
                {
                    idx += 5;
                    return false;
                }
            }
            if (c == 'n' || c == 'N')
            {
                if (idx + 4 <= s.Length && string.Compare(s, idx, "null", 0, 4, StringComparison.OrdinalIgnoreCase) == 0)
                {
                    idx += 4;
                    return null;
                }
            }
            return ParseNumber(s, ref idx);
        }

        private static Dictionary<string, object> ParseObject(string s, ref int idx)
        {
            var dict = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
            idx++; // skip '{'
            while (idx < s.Length)
            {
                SkipWhitespace(s, ref idx);
                if (idx < s.Length && s[idx] == '}')
                {
                    idx++;
                    return dict;
                }
                if (idx < s.Length && s[idx] == ',')
                {
                    idx++;
                    SkipWhitespace(s, ref idx);
                    if (idx < s.Length && s[idx] == '}')
                    {
                        idx++;
                        return dict;
                    }
                }
                string key = ParseString(s, ref idx);
                SkipWhitespace(s, ref idx);
                if (idx < s.Length && s[idx] == ':') idx++;
                object val = ParseValue(s, ref idx);
                if (key != null)
                    dict[key] = val;
            }
            return dict;
        }

        private static ArrayList ParseArray(string s, ref int idx)
        {
            var list = new ArrayList();
            idx++; // skip '['
            while (idx < s.Length)
            {
                SkipWhitespace(s, ref idx);
                if (idx < s.Length && s[idx] == ']')
                {
                    idx++;
                    return list;
                }
                if (idx < s.Length && s[idx] == ',')
                {
                    idx++;
                    SkipWhitespace(s, ref idx);
                    if (idx < s.Length && s[idx] == ']')
                    {
                        idx++;
                        return list;
                    }
                }
                object item = ParseValue(s, ref idx);
                list.Add(item);
            }
            return list;
        }

        private static string ParseString(string s, ref int idx)
        {
            SkipWhitespace(s, ref idx);
            if (idx >= s.Length || s[idx] != '"')
                return "";

            idx++; // skip opening '"'
            StringBuilder sb = new StringBuilder();
            while (idx < s.Length)
            {
                char c = s[idx++];
                if (c == '"')
                    return sb.ToString();
                if (c == '\\' && idx < s.Length)
                {
                    char esc = s[idx++];
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
                            if (idx + 4 <= s.Length)
                            {
                                string hex = s.Substring(idx, 4);
                                ushort code;
                                if (ushort.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out code))
                                    sb.Append((char)code);
                                idx += 4;
                            }
                            break;
                        default:
                            sb.Append(esc);
                            break;
                    }
                }
                else
                {
                    sb.Append(c);
                }
            }
            return sb.ToString();
        }

        private static object ParseNumber(string s, ref int idx)
        {
            int start = idx;
            bool isFloat = false;
            while (idx < s.Length)
            {
                char c = s[idx];
                if ((c >= '0' && c <= '9') || c == '-' || c == '+')
                {
                    idx++;
                }
                else if (c == '.' || c == 'e' || c == 'E')
                {
                    isFloat = true;
                    idx++;
                }
                else
                {
                    break;
                }
            }
            if (idx == start)
            {
                // Unrecognized token (e.g. unquoted identifier, Python "None"): always consume at least one
                // character so ParseObject/ParseArray loops make progress instead of spinning forever.
                idx++;
                return null;
            }
            string token = s.Substring(start, idx - start);
            if (isFloat)
            {
                double d;
                if (double.TryParse(token, NumberStyles.Float, CultureInfo.InvariantCulture, out d))
                    return d;
            }
            else
            {
                int i;
                if (int.TryParse(token, NumberStyles.Integer, CultureInfo.InvariantCulture, out i))
                    return i;
                long l;
                if (long.TryParse(token, NumberStyles.Integer, CultureInfo.InvariantCulture, out l))
                    return l;
            }
            return 0;
        }

        private static void WriteValue(StringBuilder sb, object obj)
        {
            if (obj == null)
            {
                sb.Append("null");
                return;
            }
            if (obj is string str)
            {
                WriteString(sb, str);
                return;
            }
            if (obj is bool b)
            {
                sb.Append(b ? "true" : "false");
                return;
            }
            if (obj is int || obj is long || obj is short || obj is byte || obj is uint || obj is ulong)
            {
                sb.Append(Convert.ToString(obj, CultureInfo.InvariantCulture));
                return;
            }
            if (obj is float || obj is double || obj is decimal)
            {
                sb.Append(Convert.ToString(obj, CultureInfo.InvariantCulture));
                return;
            }
            if (obj is IDictionary dict)
            {
                sb.Append('{');
                bool first = true;
                foreach (DictionaryEntry entry in dict)
                {
                    if (!first) sb.Append(',');
                    first = false;
                    WriteString(sb, Convert.ToString(entry.Key, CultureInfo.InvariantCulture));
                    sb.Append(':');
                    WriteValue(sb, entry.Value);
                }
                sb.Append('}');
                return;
            }
            if (obj is IEnumerable enumerable)
            {
                sb.Append('[');
                bool first = true;
                foreach (object item in enumerable)
                {
                    if (!first) sb.Append(',');
                    first = false;
                    WriteValue(sb, item);
                }
                sb.Append(']');
                return;
            }

            Type t = obj.GetType();
            sb.Append('{');
            bool firstProp = true;
            foreach (PropertyInfo prop in t.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                if (!prop.CanRead || prop.GetIndexParameters().Length > 0) continue;
                if (!firstProp) sb.Append(',');
                firstProp = false;
                WriteString(sb, prop.Name);
                sb.Append(':');
                WriteValue(sb, prop.GetValue(obj, null));
            }
            foreach (FieldInfo field in t.GetFields(BindingFlags.Public | BindingFlags.Instance))
            {
                if (!firstProp) sb.Append(',');
                firstProp = false;
                WriteString(sb, field.Name);
                sb.Append(':');
                WriteValue(sb, field.GetValue(obj));
            }
            sb.Append('}');
        }

        private static void WriteString(StringBuilder sb, string s)
        {
            sb.Append('"');
            if (!string.IsNullOrEmpty(s))
            {
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
                            if (c < 32)
                                sb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                            else
                                sb.Append(c);
                            break;
                    }
                }
            }
            sb.Append('"');
        }
    }
}
#endif
