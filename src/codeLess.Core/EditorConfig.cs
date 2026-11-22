using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace codeLess.Studio
{
    /// <summary>Resolved .editorconfig properties for one file (null members = unspecified / "unset").</summary>
    public sealed class EditorConfigProperties
    {
        public string IndentStyle;          // "space" | "tab"
        public string IndentSize;           // number or "tab"
        public int TabWidth;                // 0 = unset
        public string EndOfLine;            // "lf" | "crlf" | "cr"
        public bool? InsertFinalNewline;
        public bool? TrimTrailingWhitespace;
        public string Charset;              // "utf-8" | "utf-8-bom" | "latin1" | "utf-16le" | "utf-16be"
        public readonly Dictionary<string, string> Raw = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        /// <summary>Indent width: indent_size, or tab_width when indent_size = tab / unset with tabs (spec defaults).</summary>
        public int EffectiveTabSize
        {
            get
            {
                int n;
                if (!string.IsNullOrEmpty(IndentSize) && IndentSize != "tab" && int.TryParse(IndentSize, NumberStyles.Integer, CultureInfo.InvariantCulture, out n) && n > 0)
                    return Math.Min(16, n);
                if (TabWidth > 0) return Math.Min(16, TabWidth);
                return 0;
            }
        }

        public bool IsEmpty
        {
            get { return IndentStyle == null && IndentSize == null && TabWidth == 0 && EndOfLine == null && InsertFinalNewline == null && TrimTrailingWhitespace == null && Charset == null; }
        }

        /// <summary>"\n", "\r\n", "\r" or null.</summary>
        public string EolSequence
        {
            get
            {
                if (EndOfLine == "lf") return "\n";
                if (EndOfLine == "crlf") return "\r\n";
                if (EndOfLine == "cr") return "\r";
                return null;
            }
        }
    }

    /// <summary>
    /// .editorconfig support for both editors (https://spec.editorconfig.org): files are read from the file's folder up
    /// to the first "root = true", nearer files and later sections win; globs support *, **, ?, [seq], [!seq], {a,b}
    /// and {n1..n2}. Parsed files are cached by path + timestamp. STRICT C# 5.
    /// </summary>
    public static class EditorConfigResolver
    {
        private sealed class Section
        {
            public string Glob;
            public Regex Matcher;
            public List<KeyValuePair<string, string>> Props = new List<KeyValuePair<string, string>>();
        }

        private sealed class ParsedFile
        {
            public DateTime Stamp;
            public bool Root;
            public List<Section> Sections = new List<Section>();
        }

        private static readonly Dictionary<string, ParsedFile> cache = new Dictionary<string, ParsedFile>(StringComparer.Ordinal);
        private static readonly object sync = new object();

        /// <summary>Properties for <paramref name="filePath"/>, or null when no .editorconfig applies.</summary>
        public static EditorConfigProperties Resolve(string filePath)
        {
            if (string.IsNullOrEmpty(filePath)) return null;
            string full;
            try { full = Path.GetFullPath(filePath); } catch { return null; }
            List<string> configs = new List<string>();
            List<ParsedFile> parsed = new List<ParsedFile>();
            string dir = Path.GetDirectoryName(full);
            int guard = 0;
            while (!string.IsNullOrEmpty(dir) && guard++ < 64)
            {
                string cfg = Path.Combine(dir, ".editorconfig");
                ParsedFile pf = Load(cfg);
                if (pf != null)
                {
                    configs.Add(dir);
                    parsed.Add(pf);
                    if (pf.Root) break;
                }
                string parent = Path.GetDirectoryName(dir);
                if (parent == dir) break;
                dir = parent;
            }
            if (parsed.Count == 0) return null;
            Dictionary<string, string> merged = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            // farthest first, so nearer files override
            for (int k = parsed.Count - 1; k >= 0; k--)
            {
                string rel = full.Substring(configs[k].Length).Replace('\\', '/');
                if (!rel.StartsWith("/")) rel = "/" + rel;
                foreach (Section s in parsed[k].Sections)
                {
                    if (s.Matcher == null || !s.Matcher.IsMatch(rel)) continue;
                    foreach (KeyValuePair<string, string> kv in s.Props) merged[kv.Key] = kv.Value;
                }
            }
            EditorConfigProperties r = new EditorConfigProperties();
            foreach (KeyValuePair<string, string> kv in merged)
            {
                string v = (kv.Value ?? "").Trim().ToLowerInvariant();
                r.Raw[kv.Key] = v;
                if (v == "unset") continue;
                switch (kv.Key.ToLowerInvariant())
                {
                    case "indent_style": if (v == "space" || v == "tab") r.IndentStyle = v; break;
                    case "indent_size": r.IndentSize = v; break;
                    case "tab_width":
                        {
                            int n;
                            if (int.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out n) && n > 0) r.TabWidth = n;
                            break;
                        }
                    case "end_of_line": if (v == "lf" || v == "crlf" || v == "cr") r.EndOfLine = v; break;
                    case "insert_final_newline": if (v == "true" || v == "false") r.InsertFinalNewline = v == "true"; break;
                    case "trim_trailing_whitespace": if (v == "true" || v == "false") r.TrimTrailingWhitespace = v == "true"; break;
                    case "charset": if (v == "utf-8" || v == "utf-8-bom" || v == "latin1" || v == "utf-16le" || v == "utf-16be") r.Charset = v; break;
                }
            }
            // spec: indent_size = tab -> tab_width; indent_style = tab and no indent_size -> tab_width
            if (r.IndentSize == "tab" && r.TabWidth == 0) r.IndentSize = null;
            return r.IsEmpty ? null : r;
        }

        private static ParsedFile Load(string path)
        {
            DateTime stamp;
            try
            {
                if (!File.Exists(path)) return null;
                stamp = File.GetLastWriteTimeUtc(path);
            }
            catch { return null; }
            lock (sync)
            {
                ParsedFile pf;
                if (cache.TryGetValue(path, out pf) && pf.Stamp == stamp) return pf;
            }
            string text;
            try { text = File.ReadAllText(path); } catch { return null; }
            ParsedFile res = Parse(text);
            res.Stamp = stamp;
            lock (sync) { cache[path] = res; }
            return res;
        }

        private static ParsedFile Parse(string text)
        {
            ParsedFile pf = new ParsedFile();
            Section cur = null;
            foreach (string raw in (text ?? "").Replace("\r\n", "\n").Split('\n'))
            {
                string line = raw.Trim();
                if (line.Length == 0 || line[0] == '#' || line[0] == ';') continue;
                if (line[0] == '[')
                {
                    int close = line.LastIndexOf(']');
                    if (close <= 0) { cur = null; continue; }
                    cur = new Section();
                    cur.Glob = line.Substring(1, close - 1);
                    try { cur.Matcher = new Regex(GlobToRegex(cur.Glob), RegexOptions.CultureInvariant); }
                    catch { cur.Matcher = null; }
                    pf.Sections.Add(cur);
                    continue;
                }
                int eq = line.IndexOf('=');
                if (eq < 0) eq = line.IndexOf(':');
                if (eq <= 0) continue;
                string key = line.Substring(0, eq).Trim().ToLowerInvariant();
                string val = line.Substring(eq + 1).Trim();
                if (cur == null)
                {
                    if (key == "root") pf.Root = string.Equals(val, "true", StringComparison.OrdinalIgnoreCase);
                    continue;
                }
                cur.Props.Add(new KeyValuePair<string, string>(key, val));
            }
            return pf;
        }

        /// <summary>Converts an EditorConfig glob to an anchored regex over "/relative/path" (exposed for tests).</summary>
        public static string GlobToRegex(string glob)
        {
            string g = glob ?? "";
            bool hasSlash = g.IndexOf('/') >= 0;
            StringBuilder sb = new StringBuilder("^");
            if (!hasSlash) sb.Append("(?:.*/)?");        // matches the file name in any folder
            else if (!g.StartsWith("/")) sb.Append("/");
            int i = 0;
            while (i < g.Length)
            {
                char c = g[i];
                if (c == '\\' && i + 1 < g.Length) { sb.Append(Regex.Escape(g[i + 1].ToString())); i += 2; continue; }
                if (c == '*')
                {
                    if (i + 1 < g.Length && g[i + 1] == '*')
                    {
                        if (i + 2 < g.Length && g[i + 2] == '/') { sb.Append("(?:.*/)?"); i += 3; continue; }
                        sb.Append(".*");
                        i += 2;
                        continue;
                    }
                    sb.Append("[^/]*");
                    i++;
                    continue;
                }
                if (c == '?') { sb.Append("[^/]"); i++; continue; }
                if (c == '[')
                {
                    int close = g.IndexOf(']', i + 1);
                    if (close < 0) { sb.Append("\\["); i++; continue; }
                    string set = g.Substring(i + 1, close - i - 1);
                    bool neg = set.StartsWith("!") || set.StartsWith("^");
                    if (neg) set = set.Substring(1);
                    sb.Append(neg ? "[^" : "[").Append(set.Replace("\\", "\\\\")).Append("]");
                    i = close + 1;
                    continue;
                }
                if (c == '{')
                {
                    int close = FindBraceClose(g, i);
                    if (close < 0) { sb.Append("\\{"); i++; continue; }
                    string inner = g.Substring(i + 1, close - i - 1);
                    Match range = Regex.Match(inner, "^(-?\\d+)\\.\\.(-?\\d+)$");
                    if (range.Success)
                    {
                        long a = long.Parse(range.Groups[1].Value, CultureInfo.InvariantCulture), b = long.Parse(range.Groups[2].Value, CultureInfo.InvariantCulture);
                        if (a > b) { long tmp = a; a = b; b = tmp; }
                        List<string> nums = new List<string>();
                        for (long v = a; v <= b && nums.Count < 1000; v++) nums.Add(v.ToString(CultureInfo.InvariantCulture));
                        sb.Append("(?:").Append(string.Join("|", nums.ToArray())).Append(")");
                        i = close + 1;
                        continue;
                    }
                    if (inner.IndexOf(',') < 0) { sb.Append("\\{").Append(Regex.Escape(inner)).Append("\\}"); i = close + 1; continue; }
                    sb.Append("(?:");
                    List<string> parts = SplitTopLevel(inner);
                    for (int k = 0; k < parts.Count; k++)
                    {
                        if (k > 0) sb.Append("|");
                        string sub = GlobToRegex("/" + parts[k]);
                        // strip the anchors / leading "/" of the recursive conversion
                        sub = sub.Substring(1);
                        if (sub.StartsWith("/")) sub = sub.Substring(1);
                        if (sub.EndsWith("$")) sub = sub.Substring(0, sub.Length - 1);
                        sb.Append(sub);
                    }
                    sb.Append(")");
                    i = close + 1;
                    continue;
                }
                sb.Append(Regex.Escape(c.ToString()));
                i++;
            }
            sb.Append("$");
            return sb.ToString();
        }

        private static int FindBraceClose(string g, int open)
        {
            int depth = 0;
            for (int i = open; i < g.Length; i++)
            {
                if (g[i] == '\\') { i++; continue; }
                if (g[i] == '{') depth++;
                else if (g[i] == '}') { depth--; if (depth == 0) return i; }
            }
            return -1;
        }

        private static List<string> SplitTopLevel(string s)
        {
            List<string> res = new List<string>();
            int depth = 0, start = 0;
            for (int i = 0; i < s.Length; i++)
            {
                if (s[i] == '\\') { i++; continue; }
                if (s[i] == '{') depth++;
                else if (s[i] == '}') depth--;
                else if (s[i] == ',' && depth == 0) { res.Add(s.Substring(start, i - start)); start = i + 1; }
            }
            res.Add(s.Substring(start));
            return res;
        }

        /// <summary>Encoding for an EditorConfig charset value (null = keep the file's encoding).</summary>
        public static Encoding EncodingFor(string charset, out bool bom)
        {
            bom = false;
            switch (charset)
            {
                case "utf-8": return new UTF8Encoding(false);
                case "utf-8-bom": bom = true; return new UTF8Encoding(true);
                case "utf-16le": bom = true; return new UnicodeEncoding(false, true);
                case "utf-16be": bom = true; return new UnicodeEncoding(true, true);
                case "latin1":
                    try { return Encoding.GetEncoding(28591); } catch { return null; }
            }
            return null;
        }

        /// <summary>
        /// Save-time EditorConfig rules: end_of_line, charset, and insert_final_newline / trim_trailing_whitespace
        /// overriding the user's save actions (EditorConfig wins over settings, like VS Code's EditorConfig extension).
        /// </summary>
        public static void ApplyBeforeSave(EditorTabBuffer buf, SaveActionOptions opts)
        {
            if (buf == null) return;
            EditorConfigProperties ec = buf.EditorConfig;
            if (ec == null && !string.IsNullOrEmpty(buf.FilePath))
            {
                try { ec = Resolve(buf.FilePath); } catch { ec = null; }
                buf.EditorConfig = ec;
            }
            if (ec == null) return;
            string eol = ec.EolSequence;
            if (eol != null) buf.EolSequence = eol;
            if (ec.Charset != null)
            {
                bool bom;
                Encoding enc = EncodingFor(ec.Charset, out bom);
                if (enc != null) { buf.FileEncoding = enc; buf.HasBom = bom; }
            }
            if (opts != null)
            {
                if (ec.TrimTrailingWhitespace.HasValue) opts.TrimTrailingWhitespace = ec.TrimTrailingWhitespace.Value;
                if (ec.InsertFinalNewline.HasValue)
                {
                    opts.InsertFinalNewline = ec.InsertFinalNewline.Value;
                    if (!ec.InsertFinalNewline.Value) opts.TrimFinalNewlines = false;
                }
            }
        }
    }

    public partial class EditorTabBuffer
    {
        /// <summary>.editorconfig properties resolved when the file was opened (null: none).</summary>
        public EditorConfigProperties EditorConfig;
    }
}
