using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Text;

namespace codeLess.Studio
{
    /// <summary>
    /// Raw TextMate grammar rule as written in a .tmLanguage.json / .tmLanguage (plist) file.
    /// Compiled lazily by <see cref="TextMateTokenizer"/>. STRICT C# 5 (shared with the Windows build).
    /// </summary>
    public sealed class TmRawRule
    {
        public string Name;
        public string ContentName;
        public string Match;
        public string Begin;
        public string End;
        public string While;
        public string Include;
        public bool ApplyEndPatternLast;
        public Dictionary<int, TmRawRule> Captures;
        public Dictionary<int, TmRawRule> BeginCaptures;
        public Dictionary<int, TmRawRule> EndCaptures;
        public Dictionary<int, TmRawRule> WhileCaptures;
        public List<TmRawRule> Patterns;
        public Dictionary<string, TmRawRule> Repository;
        /// <summary>Enclosing rule (for nested repository lookup).</summary>
        public TmRawRule Parent;
        /// <summary>Grammar that defined this rule.</summary>
        public TmRawGrammar Grammar;
    }

    /// <summary>A parsed (not yet compiled) TextMate grammar.</summary>
    public sealed class TmRawGrammar
    {
        public string ScopeName = "";
        public string Name = "";
        public List<string> FileTypes = new List<string>();
        public string FirstLineMatch;
        /// <summary>Root rule: its <see cref="TmRawRule.Patterns"/> and <see cref="TmRawRule.Repository"/> are the grammar's.</summary>
        public TmRawRule Root = new TmRawRule();
        /// <summary>Source file (diagnostics only).</summary>
        public string SourcePath;

        public TmRawRule LookupRepository(string name)
        {
            if (Root.Repository == null || string.IsNullOrEmpty(name)) return null;
            TmRawRule r;
            return Root.Repository.TryGetValue(name, out r) ? r : null;
        }
    }

    /// <summary>
    /// Loads TextMate grammars from JSON (comments and trailing commas tolerated), XML plist, or gzip-compressed variants.
    /// </summary>
    public static class TextMateGrammarLoader
    {
        public static TmRawGrammar LoadFile(string path)
        {
            if (string.IsNullOrEmpty(path) || !File.Exists(path)) return null;
            string text;
            if (path.EndsWith(".gz", StringComparison.OrdinalIgnoreCase))
            {
                using (FileStream fs = File.OpenRead(path))
                    text = ReadGzipText(fs);
            }
            else text = File.ReadAllText(path);
            TmRawGrammar g = LoadText(text, path);
            if (g != null) g.SourcePath = path;
            return g;
        }

        public static string ReadGzipText(Stream s)
        {
            using (GZipStream gz = new GZipStream(s, CompressionMode.Decompress))
            using (StreamReader sr = new StreamReader(gz, Encoding.UTF8))
                return sr.ReadToEnd();
        }

        /// <summary>Parses grammar text; the format (JSON or plist XML) is detected from the content.</summary>
        public static TmRawGrammar LoadText(string text, string pathHint)
        {
            if (string.IsNullOrEmpty(text)) return null;
            int i = 0;
            while (i < text.Length && (char.IsWhiteSpace(text[i]) || text[i] == '﻿')) i++;
            object root;
            if (i < text.Length && text[i] == '<') root = TmPlist.Parse(text);
            else root = TmJson.Parse(text);
            Dictionary<string, object> d = root as Dictionary<string, object>;
            if (d == null) return null;
            return FromDictionary(d);
        }

        public static TmRawGrammar FromDictionary(Dictionary<string, object> d)
        {
            TmRawGrammar g = new TmRawGrammar();
            g.ScopeName = GetString(d, "scopeName") ?? "";
            g.Name = GetString(d, "name") ?? "";
            g.FirstLineMatch = GetString(d, "firstLineMatch");
            IList ft = Get(d, "fileTypes") as IList;
            if (ft != null)
                foreach (object o in ft)
                    if (o != null) g.FileTypes.Add(Convert.ToString(o, CultureInfo.InvariantCulture));
            g.Root = new TmRawRule();
            g.Root.Grammar = g;
            g.Root.Patterns = ParsePatterns(Get(d, "patterns") as IList, g.Root, g);
            g.Root.Repository = ParseRepository(Get(d, "repository") as Dictionary<string, object>, g.Root, g);
            return g;
        }

        private static List<TmRawRule> ParsePatterns(IList list, TmRawRule parent, TmRawGrammar g)
        {
            if (list == null) return null;
            List<TmRawRule> res = new List<TmRawRule>(list.Count);
            foreach (object o in list)
            {
                Dictionary<string, object> rd = o as Dictionary<string, object>;
                if (rd == null) continue;
                res.Add(ParseRule(rd, parent, g));
            }
            return res;
        }

        private static Dictionary<string, TmRawRule> ParseRepository(Dictionary<string, object> repo, TmRawRule parent, TmRawGrammar g)
        {
            if (repo == null) return null;
            Dictionary<string, TmRawRule> res = new Dictionary<string, TmRawRule>(StringComparer.Ordinal);
            foreach (KeyValuePair<string, object> kv in repo)
            {
                Dictionary<string, object> rd = kv.Value as Dictionary<string, object>;
                if (rd == null) continue;
                res[kv.Key] = ParseRule(rd, parent, g);
            }
            return res;
        }

        private static TmRawRule ParseRule(Dictionary<string, object> d, TmRawRule parent, TmRawGrammar g)
        {
            TmRawRule r = new TmRawRule();
            r.Parent = parent;
            r.Grammar = g;
            r.Name = GetString(d, "name");
            r.ContentName = GetString(d, "contentName");
            r.Match = GetString(d, "match");
            r.Begin = GetString(d, "begin");
            r.End = GetString(d, "end");
            r.While = GetString(d, "while");
            r.Include = GetString(d, "include");
            object aep = Get(d, "applyEndPatternLast");
            r.ApplyEndPatternLast = aep != null && (aep is bool ? (bool)aep : (Convert.ToString(aep, CultureInfo.InvariantCulture) == "1" || string.Equals(Convert.ToString(aep, CultureInfo.InvariantCulture), "true", StringComparison.OrdinalIgnoreCase)));
            r.Captures = ParseCaptures(Get(d, "captures"), r, g);
            r.BeginCaptures = ParseCaptures(Get(d, "beginCaptures"), r, g);
            r.EndCaptures = ParseCaptures(Get(d, "endCaptures"), r, g);
            r.WhileCaptures = ParseCaptures(Get(d, "whileCaptures"), r, g);
            r.Repository = ParseRepository(Get(d, "repository") as Dictionary<string, object>, r, g);
            r.Patterns = ParsePatterns(Get(d, "patterns") as IList, r, g);
            // "captures" doubles as begin/end captures when those are missing (TextMate semantics).
            if (r.Begin != null && r.Captures != null)
            {
                if (r.BeginCaptures == null) r.BeginCaptures = r.Captures;
                if (r.End != null && r.EndCaptures == null) r.EndCaptures = r.Captures;
                if (r.While != null && r.WhileCaptures == null) r.WhileCaptures = r.Captures;
            }
            return r;
        }

        private static Dictionary<int, TmRawRule> ParseCaptures(object o, TmRawRule parent, TmRawGrammar g)
        {
            Dictionary<int, TmRawRule> res = null;
            Dictionary<string, object> d = o as Dictionary<string, object>;
            if (d != null)
            {
                foreach (KeyValuePair<string, object> kv in d)
                {
                    int idx;
                    if (!int.TryParse(kv.Key, NumberStyles.Integer, CultureInfo.InvariantCulture, out idx) || idx < 0 || idx > 999) continue;
                    Dictionary<string, object> rd = kv.Value as Dictionary<string, object>;
                    if (rd == null) continue;
                    if (res == null) res = new Dictionary<int, TmRawRule>();
                    res[idx] = ParseRule(rd, parent, g);
                }
                return res;
            }
            IList l = o as IList; // some converted grammars use an array
            if (l != null)
            {
                for (int i = 0; i < l.Count; i++)
                {
                    Dictionary<string, object> rd = l[i] as Dictionary<string, object>;
                    if (rd == null) continue;
                    if (res == null) res = new Dictionary<int, TmRawRule>();
                    res[i] = ParseRule(rd, parent, g);
                }
            }
            return res;
        }

        private static object Get(Dictionary<string, object> d, string key)
        {
            object o;
            return d != null && d.TryGetValue(key, out o) ? o : null;
        }

        private static string GetString(Dictionary<string, object> d, string key)
        {
            object o = Get(d, key);
            if (o == null) return null;
            string s = o as string;
            return s ?? Convert.ToString(o, CultureInfo.InvariantCulture);
        }
    }

    /// <summary>
    /// Small, fast JSON reader for grammars and themes: tolerates // and /* */ comments and trailing commas (VS Code's
    /// JSONC). Objects become Dictionary&lt;string, object&gt;, arrays List&lt;object&gt;, numbers double.
    /// </summary>
    public static class TmJson
    {
        public static object Parse(string s)
        {
            if (s == null) return null;
            int i = 0;
            SkipWs(s, ref i);
            object v = ParseValue(s, ref i, 0);
            return v;
        }

        private static void SkipWs(string s, ref int i)
        {
            while (i < s.Length)
            {
                char c = s[i];
                if (c == ' ' || c == '\t' || c == '\r' || c == '\n' || c == '﻿') { i++; continue; }
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
                        int e = s.IndexOf("*/", i + 2, StringComparison.Ordinal);
                        i = e < 0 ? s.Length : e + 2;
                        continue;
                    }
                }
                break;
            }
        }

        private static object ParseValue(string s, ref int i, int depth)
        {
            if (depth > 200) throw new FormatException("JSON nested too deeply");
            SkipWs(s, ref i);
            if (i >= s.Length) throw new FormatException("Unexpected end of JSON");
            char c = s[i];
            if (c == '{')
            {
                i++;
                Dictionary<string, object> d = new Dictionary<string, object>(StringComparer.Ordinal);
                while (true)
                {
                    SkipWs(s, ref i);
                    if (i >= s.Length) throw new FormatException("Unterminated object");
                    if (s[i] == '}') { i++; return d; }
                    if (s[i] == ',') { i++; continue; }
                    string key = ParseString(s, ref i);
                    SkipWs(s, ref i);
                    if (i >= s.Length || s[i] != ':') throw new FormatException("Expected ':' at " + i);
                    i++;
                    object v = ParseValue(s, ref i, depth + 1);
                    d[key] = v;
                }
            }
            if (c == '[')
            {
                i++;
                List<object> l = new List<object>();
                while (true)
                {
                    SkipWs(s, ref i);
                    if (i >= s.Length) throw new FormatException("Unterminated array");
                    if (s[i] == ']') { i++; return l; }
                    if (s[i] == ',') { i++; continue; }
                    l.Add(ParseValue(s, ref i, depth + 1));
                }
            }
            if (c == '"' || c == '\'') return ParseString(s, ref i);
            if (c == 't' && string.CompareOrdinal(s, i, "true", 0, 4) == 0) { i += 4; return true; }
            if (c == 'f' && string.CompareOrdinal(s, i, "false", 0, 5) == 0) { i += 5; return false; }
            if (c == 'n' && string.CompareOrdinal(s, i, "null", 0, 4) == 0) { i += 4; return null; }
            int start = i;
            while (i < s.Length && "+-0123456789.eE".IndexOf(s[i]) >= 0) i++;
            if (i == start) throw new FormatException("Unexpected character '" + c + "' at " + i);
            double dv;
            double.TryParse(s.Substring(start, i - start), NumberStyles.Float, CultureInfo.InvariantCulture, out dv);
            return dv;
        }

        private static string ParseString(string s, ref int i)
        {
            char q = s[i];
            if (q != '"' && q != '\'') throw new FormatException("Expected string at " + i);
            i++;
            StringBuilder sb = null;
            int runStart = i;
            while (i < s.Length)
            {
                char c = s[i];
                if (c == q)
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
                    char e = s[i];
                    switch (e)
                    {
                        case 'n': sb.Append('\n'); break;
                        case 't': sb.Append('\t'); break;
                        case 'r': sb.Append('\r'); break;
                        case 'b': sb.Append('\b'); break;
                        case 'f': sb.Append('\f'); break;
                        case 'u':
                            if (i + 4 < s.Length)
                            {
                                int cp;
                                if (int.TryParse(s.Substring(i + 1, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out cp))
                                {
                                    sb.Append((char)cp);
                                    i += 4;
                                }
                            }
                            break;
                        default: sb.Append(e); break;
                    }
                    i++;
                    runStart = i;
                    continue;
                }
                i++;
            }
            throw new FormatException("Unterminated string");
        }
    }

    /// <summary>Minimal XML property-list reader (dict, array, key, string, integer, real, true, false) for .tmLanguage / .tmTheme.</summary>
    public static class TmPlist
    {
        public static object Parse(string xml)
        {
            int i = 0;
            // find <plist ...> then its first value element
            while (true)
            {
                string tag = NextTag(xml, ref i);
                if (tag == null) return null;
                if (tag.StartsWith("plist", StringComparison.Ordinal)) break;
                if (tag == "dict" || tag == "array") { i = BackToTag(xml, i); break; }
            }
            return ParseValue(xml, ref i, 0);
        }

        // Returns the element name of the next start tag (attributes stripped; "x/" for self-closing); skips
        // declarations, comments and end tags. i ends just after '>'.
        private static string NextTag(string s, ref int i)
        {
            while (i < s.Length)
            {
                int lt = s.IndexOf('<', i);
                if (lt < 0) { i = s.Length; return null; }
                if (string.CompareOrdinal(s, lt, "<!--", 0, 4) == 0)
                {
                    int e = s.IndexOf("-->", lt + 4, StringComparison.Ordinal);
                    i = e < 0 ? s.Length : e + 3;
                    continue;
                }
                if (lt + 1 < s.Length && (s[lt + 1] == '?' || s[lt + 1] == '!'))
                {
                    int e = s.IndexOf('>', lt + 1);
                    i = e < 0 ? s.Length : e + 1;
                    continue;
                }
                int gt = s.IndexOf('>', lt + 1);
                if (gt < 0) { i = s.Length; return null; }
                string inner = s.Substring(lt + 1, gt - lt - 1).Trim();
                i = gt + 1;
                return inner;
            }
            return null;
        }

        private static int BackToTag(string s, int afterGt)
        {
            int lt = s.LastIndexOf('<', Math.Max(0, afterGt - 1));
            return lt < 0 ? 0 : lt;
        }

        private static string ElementName(string inner)
        {
            int sp = 0;
            while (sp < inner.Length && !char.IsWhiteSpace(inner[sp]) && inner[sp] != '/') sp++;
            return inner.Substring(0, sp);
        }

        private static object ParseValue(string s, ref int i, int depth)
        {
            if (depth > 200) throw new FormatException("plist nested too deeply");
            string tag;
            while (true)
            {
                tag = NextTag(s, ref i);
                if (tag == null) return null;
                if (tag.StartsWith("/", StringComparison.Ordinal)) return EndMarker; // end of container
                break;
            }
            bool selfClosing = tag.EndsWith("/", StringComparison.Ordinal);
            string name = ElementName(tag);
            switch (name)
            {
                case "dict":
                    {
                        Dictionary<string, object> d = new Dictionary<string, object>(StringComparer.Ordinal);
                        if (selfClosing) return d;
                        while (true)
                        {
                            object k = ParseValue(s, ref i, depth + 1);
                            if (ReferenceEquals(k, EndMarker) || k == null) return d;
                            KeyHolder kh = k as KeyHolder;
                            string key = kh != null ? kh.Key : Convert.ToString(k, CultureInfo.InvariantCulture);
                            object v = ParseValue(s, ref i, depth + 1);
                            if (ReferenceEquals(v, EndMarker)) { d[key] = null; return d; }
                            d[key] = v;
                        }
                    }
                case "array":
                    {
                        List<object> l = new List<object>();
                        if (selfClosing) return l;
                        while (true)
                        {
                            object v = ParseValue(s, ref i, depth + 1);
                            if (ReferenceEquals(v, EndMarker) || (v == null && i >= s.Length)) return l;
                            l.Add(v);
                        }
                    }
                case "key":
                    {
                        KeyHolder kh = new KeyHolder();
                        kh.Key = selfClosing ? "" : ReadText(s, ref i, "key");
                        return kh;
                    }
                case "string":
                    return selfClosing ? "" : ReadText(s, ref i, "string");
                case "integer":
                case "real":
                    {
                        string t = selfClosing ? "0" : ReadText(s, ref i, name);
                        double dv;
                        double.TryParse(t.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out dv);
                        return dv;
                    }
                case "true": if (!selfClosing) ReadText(s, ref i, "true"); return true;
                case "false": if (!selfClosing) ReadText(s, ref i, "false"); return false;
                default:
                    // date / data / unknown: keep the text
                    return selfClosing ? "" : ReadText(s, ref i, name);
            }
        }

        private static readonly object EndMarker = new object();

        private sealed class KeyHolder { public string Key; }

        private static string ReadText(string s, ref int i, string name)
        {
            string close = "</" + name;
            int e = s.IndexOf(close, i, StringComparison.Ordinal);
            if (e < 0) { string rest = s.Substring(i); i = s.Length; return Unescape(rest); }
            string raw = s.Substring(i, e - i);
            int gt = s.IndexOf('>', e);
            i = gt < 0 ? s.Length : gt + 1;
            return Unescape(raw);
        }

        public static string Unescape(string raw)
        {
            if (raw.IndexOf('&') < 0 && raw.IndexOf("<![CDATA[", StringComparison.Ordinal) < 0) return raw;
            StringBuilder sb = new StringBuilder(raw.Length);
            int i = 0;
            while (i < raw.Length)
            {
                if (string.CompareOrdinal(raw, i, "<![CDATA[", 0, 9) == 0)
                {
                    int e = raw.IndexOf("]]>", i + 9, StringComparison.Ordinal);
                    if (e < 0) { sb.Append(raw, i + 9, raw.Length - i - 9); break; }
                    sb.Append(raw, i + 9, e - i - 9);
                    i = e + 3;
                    continue;
                }
                char c = raw[i];
                if (c == '&')
                {
                    int semi = raw.IndexOf(';', i + 1);
                    if (semi > i && semi - i <= 10)
                    {
                        string ent = raw.Substring(i + 1, semi - i - 1);
                        string rep = null;
                        if (ent == "lt") rep = "<";
                        else if (ent == "gt") rep = ">";
                        else if (ent == "amp") rep = "&";
                        else if (ent == "quot") rep = "\"";
                        else if (ent == "apos") rep = "'";
                        else if (ent.Length > 1 && ent[0] == '#')
                        {
                            int cp;
                            bool ok = ent[1] == 'x' || ent[1] == 'X'
                                ? int.TryParse(ent.Substring(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out cp)
                                : int.TryParse(ent.Substring(1), NumberStyles.Integer, CultureInfo.InvariantCulture, out cp);
                            if (ok && cp >= 0 && cp <= 0x10FFFF) rep = char.ConvertFromUtf32(cp);
                        }
                        if (rep != null) { sb.Append(rep); i = semi + 1; continue; }
                    }
                }
                sb.Append(c);
                i++;
            }
            return sb.ToString();
        }
    }
}
