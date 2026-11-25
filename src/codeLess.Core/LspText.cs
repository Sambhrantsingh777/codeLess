using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace codeLess.Studio
{
    /// <summary>
    /// Position / text utilities for LSP. Editor columns in codeLess are UTF-16 code-unit indices into the line
    /// string (the same unit LSP uses by default), but servers may negotiate "utf-8" or "utf-32", so all mapping goes
    /// through here. Never splits a surrogate pair; clamps out-of-range positions. C# 5 only.
    /// </summary>
    public static class LspText
    {
        public const string EncodingUtf16 = "utf-16";
        public const string EncodingUtf8 = "utf-8";
        public const string EncodingUtf32 = "utf-32";

        /// <summary>Editor column (UTF-16 index) -> LSP character offset in the negotiated encoding.</summary>
        public static int ToLspCharacter(string line, int charIndex, string encoding)
        {
            if (line == null) line = "";
            if (charIndex < 0) charIndex = 0;
            if (charIndex > line.Length) charIndex = line.Length;
            // Never point into the middle of a surrogate pair.
            if (charIndex > 0 && charIndex < line.Length && char.IsLowSurrogate(line[charIndex]) && char.IsHighSurrogate(line[charIndex - 1]))
                charIndex--;
            if (encoding == null || encoding == EncodingUtf16) return charIndex;
            int units = 0;
            for (int i = 0; i < charIndex; i++)
            {
                char c = line[i];
                if (char.IsHighSurrogate(c) && i + 1 < line.Length && char.IsLowSurrogate(line[i + 1]))
                {
                    units += encoding == EncodingUtf8 ? 4 : 1;
                    i++;
                    continue;
                }
                if (encoding == EncodingUtf8) units += c < 0x80 ? 1 : (c < 0x800 ? 2 : 3);
                else units += 1;
            }
            return units;
        }

        /// <summary>LSP character offset -> editor column (UTF-16 index), clamped to the line.</summary>
        public static int FromLspCharacter(string line, int lspCharacter, string encoding)
        {
            if (line == null) line = "";
            if (lspCharacter <= 0) return 0;
            if (encoding == null || encoding == EncodingUtf16)
            {
                int idx = Math.Min(lspCharacter, line.Length);
                if (idx > 0 && idx < line.Length && char.IsLowSurrogate(line[idx]) && char.IsHighSurrogate(line[idx - 1])) idx--;
                return idx;
            }
            int units = 0;
            int i = 0;
            while (i < line.Length)
            {
                char c = line[i];
                int w;
                int len = 1;
                if (char.IsHighSurrogate(c) && i + 1 < line.Length && char.IsLowSurrogate(line[i + 1]))
                {
                    w = encoding == EncodingUtf8 ? 4 : 1;
                    len = 2;
                }
                else w = encoding == EncodingUtf8 ? (c < 0x80 ? 1 : (c < 0x800 ? 2 : 3)) : 1;
                if (units + w > lspCharacter) break;
                units += w;
                i += len;
                if (units >= lspCharacter) break;
            }
            return i;
        }

        // ---------------------------------------------------------------- lines and offsets

        /// <summary>Start offset of every line. Recognises \r\n, \n and \r as line breaks (LSP spec).</summary>
        public static List<int> LineStarts(string text)
        {
            List<int> starts = new List<int>();
            starts.Add(0);
            if (string.IsNullOrEmpty(text)) return starts;
            for (int i = 0; i < text.Length; i++)
            {
                char c = text[i];
                if (c == '\r')
                {
                    if (i + 1 < text.Length && text[i + 1] == '\n') i++;
                    starts.Add(i + 1);
                }
                else if (c == '\n') starts.Add(i + 1);
            }
            return starts;
        }

        /// <summary>Text of line <paramref name="line"/> without its terminator.</summary>
        public static string LineText(string text, List<int> starts, int line)
        {
            if (line < 0 || line >= starts.Count) return "";
            int s = starts[line];
            int e = line + 1 < starts.Count ? starts[line + 1] : text.Length;
            while (e > s && (text[e - 1] == '\n' || text[e - 1] == '\r')) e--;
            return text.Substring(s, e - s);
        }

        /// <summary>(line, LSP character) -> absolute UTF-16 offset in <paramref name="text"/>, clamped.</summary>
        public static int OffsetOf(string text, List<int> starts, int line, int lspCharacter, string encoding)
        {
            if (line < 0) return 0;
            if (line >= starts.Count) return text.Length;
            string lt = LineText(text, starts, line);
            return starts[line] + FromLspCharacter(lt, lspCharacter, encoding);
        }

        /// <summary>Absolute offset -> (line, LSP character).</summary>
        public static void PositionOf(string text, List<int> starts, int offset, string encoding, out int line, out int character)
        {
            if (offset < 0) offset = 0;
            if (offset > text.Length) offset = text.Length;
            int lo = 0, hi = starts.Count - 1;
            while (lo < hi)
            {
                int mid = (lo + hi + 1) / 2;
                if (starts[mid] <= offset) lo = mid; else hi = mid - 1;
            }
            line = lo;
            string lt = LineText(text, starts, line);
            int col = Math.Min(offset - starts[line], lt.Length);
            character = ToLspCharacter(lt, col, encoding);
        }

        // ---------------------------------------------------------------- incremental sync

        /// <summary>
        /// Computes a single replacement that turns <paramref name="oldText"/> into <paramref name="newText"/>
        /// (common prefix / suffix), expressed as an LSP range in <paramref name="oldText"/>. Returns false when the
        /// texts are identical. Never splits a surrogate pair or a CRLF.
        /// </summary>
        public static bool ComputeIncrementalChange(string oldText, string newText, string encoding,
            out int startLine, out int startChar, out int endLine, out int endChar, out string insertText)
        {
            oldText = oldText ?? "";
            newText = newText ?? "";
            startLine = startChar = endLine = endChar = 0;
            insertText = "";
            if (string.Equals(oldText, newText, StringComparison.Ordinal)) return false;
            int max = Math.Min(oldText.Length, newText.Length);
            int prefix = 0;
            while (prefix < max && oldText[prefix] == newText[prefix]) prefix++;
            // Back off so we never cut through a surrogate pair or between \r and \n.
            while (prefix > 0 && (char.IsHighSurrogate(oldText[prefix - 1]) || (oldText[prefix - 1] == '\r' && prefix < oldText.Length && oldText[prefix] == '\n')))
                prefix--;
            int suffix = 0;
            int maxSuffix = Math.Min(oldText.Length - prefix, newText.Length - prefix);
            while (suffix < maxSuffix && oldText[oldText.Length - 1 - suffix] == newText[newText.Length - 1 - suffix]) suffix++;
            while (suffix > 0)
            {
                int oi = oldText.Length - suffix; // first char of the kept suffix
                bool splitsPair = oi > 0 && char.IsLowSurrogate(oldText[oi]) && char.IsHighSurrogate(oldText[oi - 1]);
                bool splitsCrlf = oi > 0 && oldText[oi] == '\n' && oldText[oi - 1] == '\r';
                if (!splitsPair && !splitsCrlf) break;
                suffix--;
            }
            List<int> starts = LineStarts(oldText);
            PositionOf(oldText, starts, prefix, encoding, out startLine, out startChar);
            PositionOf(oldText, starts, oldText.Length - suffix, encoding, out endLine, out endChar);
            insertText = newText.Substring(prefix, newText.Length - suffix - prefix);
            return true;
        }

        /// <summary>Applies an incremental change (as a server would) — used by tests and by the fake server.</summary>
        public static string ApplyChange(string text, int startLine, int startChar, int endLine, int endChar, string newText, string encoding)
        {
            List<int> starts = LineStarts(text);
            int s = OffsetOf(text, starts, startLine, startChar, encoding);
            int e = OffsetOf(text, starts, endLine, endChar, encoding);
            if (e < s) { int t = s; s = e; e = t; }
            return text.Substring(0, s) + (newText ?? "") + text.Substring(e);
        }

        // ---------------------------------------------------------------- text edits

        /// <summary>
        /// Applies LSP TextEdits (ranges relative to the original text) and returns the new text. Edits are applied
        /// from the end so earlier offsets stay valid; inserts at the same position keep their array order.
        /// Overlapping edits are rejected (returns null) as the spec forbids them.
        /// </summary>
        public static string ApplyTextEdits(string text, IList<LspTextEdit> edits, string encoding)
        {
            text = text ?? "";
            if (edits == null || edits.Count == 0) return text;
            List<int> starts = LineStarts(text);
            List<KeyValuePair<int, int>> spans = new List<KeyValuePair<int, int>>();
            List<int> order = new List<int>();
            for (int i = 0; i < edits.Count; i++)
            {
                LspRange r = edits[i].Range;
                int s = OffsetOf(text, starts, r.StartLine, r.StartCharacter, encoding);
                int e = OffsetOf(text, starts, r.EndLine, r.EndCharacter, encoding);
                if (e < s) { int t = s; s = e; e = t; }
                spans.Add(new KeyValuePair<int, int>(s, e));
                order.Add(i);
            }
            order.Sort(delegate (int a, int b)
            {
                int c = spans[a].Key.CompareTo(spans[b].Key);
                if (c != 0) return c;
                c = spans[a].Value.CompareTo(spans[b].Value);
                return c != 0 ? c : a.CompareTo(b);
            });
            for (int k = 1; k < order.Count; k++)
            {
                if (spans[order[k]].Key < spans[order[k - 1]].Value) return null; // overlap
            }
            StringBuilder sb = new StringBuilder(text.Length + 64);
            int pos = 0;
            for (int k = 0; k < order.Count; k++)
            {
                KeyValuePair<int, int> sp = spans[order[k]];
                sb.Append(text, pos, sp.Key - pos);
                sb.Append(edits[order[k]].NewText ?? "");
                pos = sp.Value;
            }
            sb.Append(text, pos, text.Length - pos);
            return sb.ToString();
        }

        /// <summary>Normalises line endings of inserted text to the document's EOL.</summary>
        public static string NormalizeEol(string s, string eol)
        {
            if (string.IsNullOrEmpty(s)) return s ?? "";
            string lf = s.Replace("\r\n", "\n").Replace('\r', '\n');
            return eol == "\n" ? lf : lf.Replace("\n", eol);
        }

        // ---------------------------------------------------------------- URIs

        /// <summary>Absolute file path -> file:// URI (percent-encoded the way VS Code does it).</summary>
        public static string PathToUri(string path)
        {
            if (string.IsNullOrEmpty(path)) return "";
            string p = path.Replace('\\', '/');
            StringBuilder sb = new StringBuilder("file://");
            if (p.StartsWith("//", StringComparison.Ordinal))
            {
                // UNC: //server/share/x -> file://server/share/x
                p = p.Substring(2);
            }
            else if (!p.StartsWith("/", StringComparison.Ordinal))
            {
                sb.Append('/');
            }
            byte[] bytes = Encoding.UTF8.GetBytes(p);
            for (int i = 0; i < bytes.Length; i++)
            {
                byte b = bytes[i];
                char c = (char)b;
                bool keep = (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9') ||
                            c == '-' || c == '.' || c == '_' || c == '~' || c == '/' ||
                            (c == ':' && i == 1 && p.Length > 1 && char.IsLetter(p[0])) ||
                            c == '!' || c == '$' || c == '&' || c == '\'' || c == '(' || c == ')' || c == '*' || c == '+' || c == ',' || c == ';' || c == '=' || c == '@';
                if (keep) sb.Append(c);
                else sb.Append('%').Append(b.ToString("X2", CultureInfo.InvariantCulture));
            }
            return sb.ToString();
        }

        /// <summary>file:// URI -> local path (null for other schemes).</summary>
        public static string UriToPath(string uri)
        {
            if (string.IsNullOrEmpty(uri)) return null;
            if (!uri.StartsWith("file:", StringComparison.OrdinalIgnoreCase)) return null;
            string rest = uri.Substring(5);
            string host = "";
            if (rest.StartsWith("//", StringComparison.Ordinal))
            {
                rest = rest.Substring(2);
                int slash = rest.IndexOf('/');
                if (slash < 0) { host = rest; rest = "/"; }
                else { host = rest.Substring(0, slash); rest = rest.Substring(slash); }
            }
            int q = rest.IndexOfAny(new char[] { '?', '#' });
            if (q >= 0) rest = rest.Substring(0, q);
            string decoded = PercentDecode(rest);
            bool windowsDrive = decoded.Length >= 3 && decoded[0] == '/' && char.IsLetter(decoded[1]) && decoded[2] == ':';
            if (windowsDrive) decoded = decoded.Substring(1);
            if (host.Length > 0 && !string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase))
                decoded = "//" + host + decoded;
            if (Path.DirectorySeparatorChar == '\\' || windowsDrive) decoded = decoded.Replace('/', '\\');
            return decoded;
        }

        private static string PercentDecode(string s)
        {
            if (s.IndexOf('%') < 0) return s;
            List<byte> bytes = new List<byte>(s.Length);
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                if (c == '%' && i + 2 < s.Length)
                {
                    int hv;
                    if (int.TryParse(s.Substring(i + 1, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out hv))
                    {
                        bytes.Add((byte)hv);
                        i += 2;
                        continue;
                    }
                }
                byte[] enc = Encoding.UTF8.GetBytes(c.ToString());
                if (char.IsHighSurrogate(c) && i + 1 < s.Length)
                {
                    enc = Encoding.UTF8.GetBytes(s.Substring(i, 2));
                    i++;
                }
                bytes.AddRange(enc);
            }
            return Encoding.UTF8.GetString(bytes.ToArray());
        }

        /// <summary>Key for comparing paths (case-insensitive on Windows and macOS, like the rest of codeLess).</summary>
        public static string PathKey(string path)
        {
            if (string.IsNullOrEmpty(path)) return "";
            string full;
            try { full = Path.GetFullPath(path); } catch { full = path; }
            full = full.Replace('\\', '/').TrimEnd('/');
            // macOS: /var, /tmp and /etc are symlinks into /private; servers that canonicalize (clangd) report the
            // /private form for files the client opened via the short form. Both must map to the same document.
            if (IsMacOS() && full.StartsWith("/private/", StringComparison.OrdinalIgnoreCase))
            {
                string rest = full.Substring(8);
                if (rest.StartsWith("/var/", StringComparison.OrdinalIgnoreCase) || rest.StartsWith("/tmp/", StringComparison.OrdinalIgnoreCase) ||
                    rest.StartsWith("/etc/", StringComparison.OrdinalIgnoreCase) || rest == "/var" || rest == "/tmp")
                    full = rest;
            }
            bool caseSensitive = Path.DirectorySeparatorChar == '/' && !IsMacOS();
            return caseSensitive ? full : full.ToLowerInvariant();
        }

        private static int macCache = -1;
        public static bool IsMacOS()
        {
            if (macCache < 0)
            {
                bool mac = false;
                try
                {
                    if (Path.DirectorySeparatorChar == '/' && Directory.Exists("/System/Library/CoreServices") && File.Exists("/usr/bin/sw_vers")) mac = true;
                }
                catch { }
                macCache = mac ? 1 : 0;
            }
            return macCache == 1;
        }

        public static bool IsWindows()
        {
            return Path.DirectorySeparatorChar == '\\';
        }

        // ---------------------------------------------------------------- snippets

        /// <summary>
        /// Expands an LSP snippet to plain text: placeholders keep their default text, choices take the first
        /// option, variables use their default (or a known value). <paramref name="caretOffset"/> is the offset of
        /// the first tab stop ($1, else $0, else the end); <paramref name="selectionLength"/> covers its placeholder.
        /// </summary>
        public static string ExpandSnippet(string snippet, out int caretOffset, out int selectionLength)
        {
            List<int[]> orderedStops;
            return ExpandSnippetWithStops(snippet, out caretOffset, out selectionLength, out orderedStops);
        }

        public static string ExpandSnippetWithStops(string snippet, out int caretOffset, out int selectionLength, out List<int[]> orderedStops)
        {
            caretOffset = -1;
            selectionLength = 0;
            orderedStops = new List<int[]>();
            if (string.IsNullOrEmpty(snippet)) { caretOffset = 0; return ""; }
            Dictionary<int, int[]> stops = new Dictionary<int, int[]>();
            StringBuilder sb = new StringBuilder(snippet.Length);
            int i = 0;
            ExpandSnippetInto(snippet, ref i, sb, stops, '\0');
            List<int> positiveKeys = new List<int>();
            foreach (int k in stops.Keys)
            {
                if (k > 0) positiveKeys.Add(k);
            }
            positiveKeys.Sort();
            foreach (int k in positiveKeys)
            {
                int[] st = stops[k];
                orderedStops.Add(new int[] { st[0], st[1], k });
            }
            if (stops.ContainsKey(0))
            {
                int[] st0 = stops[0];
                orderedStops.Add(new int[] { st0[0], st0[1], 0 });
            }
            else if (orderedStops.Count > 0)
            {
                orderedStops.Add(new int[] { sb.Length, 0, 0 });
            }
            if (orderedStops.Count > 0)
            {
                caretOffset = orderedStops[0][0];
                selectionLength = orderedStops[0][1];
            }
            else caretOffset = sb.Length;
            return sb.ToString();
        }

        private static void ExpandSnippetInto(string s, ref int i, StringBuilder sb, Dictionary<int, int[]> stops, char terminator)
        {
            while (i < s.Length)
            {
                char c = s[i];
                if (terminator != '\0' && c == terminator) return;
                if (c == '\\' && i + 1 < s.Length && (s[i + 1] == '$' || s[i + 1] == '}' || s[i + 1] == '\\' || s[i + 1] == ',' || s[i + 1] == '|'))
                {
                    sb.Append(s[i + 1]);
                    i += 2;
                    continue;
                }
                if (c == '$' && i + 1 < s.Length)
                {
                    char n = s[i + 1];
                    if (IsAsciiDigit(n))
                    {
                        int j = i + 1;
                        while (j < s.Length && IsAsciiDigit(s[j])) j++;
                        int idx;
                        if (!int.TryParse(s.Substring(i + 1, j - i - 1), NumberStyles.None, CultureInfo.InvariantCulture, out idx))
                        {
                            sb.Append(s, i, j - i); // absurd tab stop number: keep it as literal text
                            i = j;
                            continue;
                        }
                        if (!stops.ContainsKey(idx)) stops[idx] = new int[] { sb.Length, 0 };
                        i = j;
                        continue;
                    }
                    if (n == '{')
                    {
                        int j = i + 2;
                        int parsedIdx = 0;
                        int kEnd = j;
                        while (kEnd < s.Length && IsAsciiDigit(s[kEnd])) kEnd++;
                        bool validIdx = kEnd > j && int.TryParse(s.Substring(j, kEnd - j), NumberStyles.None, CultureInfo.InvariantCulture, out parsedIdx);
                        if (kEnd > j && !validIdx)
                        {
                            sb.Append("${"); // overflowing tab stop number: literal text, keep scanning after "${"
                            i = j;
                            continue;
                        }
                        if (validIdx)
                        {
                            int k = kEnd;
                            int idx = parsedIdx;
                            int start = sb.Length;
                            if (k < s.Length && s[k] == ':')
                            {
                                i = k + 1;
                                ExpandSnippetInto(s, ref i, sb, stops, '}');
                                if (i < s.Length) i++; // '}'
                            }
                            else if (k < s.Length && s[k] == '|')
                            {
                                int end = s.IndexOf("|}", k + 1, StringComparison.Ordinal);
                                string choices = end > k ? s.Substring(k + 1, end - k - 1) : "";
                                string first = SplitFirstChoice(choices);
                                sb.Append(first);
                                i = end > k ? end + 2 : s.Length;
                            }
                            else
                            {
                                i = k < s.Length && s[k] == '}' ? k + 1 : k;
                            }
                            if (!stops.ContainsKey(idx)) stops[idx] = new int[] { start, sb.Length - start };
                            continue;
                        }
                        if (j < s.Length && (char.IsLetter(s[j]) || s[j] == '_'))
                        {
                            int k = j;
                            while (k < s.Length && (char.IsLetterOrDigit(s[k]) || s[k] == '_')) k++;
                            string name = s.Substring(j, k - j);
                            if (k < s.Length && s[k] == ':')
                            {
                                i = k + 1;
                                int before = sb.Length;
                                ExpandSnippetInto(s, ref i, sb, stops, '}');
                                if (i < s.Length) i++;
                                string known = SnippetVariable(name);
                                if (known != null) { sb.Length = before; sb.Append(known); }
                            }
                            else
                            {
                                // ${VAR} or ${VAR/regex/format/} (transform ignored)
                                int close = s.IndexOf('}', k);
                                i = close >= 0 ? close + 1 : s.Length;
                                sb.Append(SnippetVariable(name) ?? "");
                            }
                            continue;
                        }
                    }
                    if (char.IsLetter(n) || n == '_')
                    {
                        int k = i + 1;
                        while (k < s.Length && (char.IsLetterOrDigit(s[k]) || s[k] == '_')) k++;
                        sb.Append(SnippetVariable(s.Substring(i + 1, k - i - 1)) ?? "");
                        i = k;
                        continue;
                    }
                }
                sb.Append(c);
                i++;
            }
        }

        private static bool IsAsciiDigit(char c)
        {
            return c >= '0' && c <= '9';
        }

        private static string SplitFirstChoice(string choices)
        {
            StringBuilder sb = new StringBuilder();
            for (int i = 0; i < choices.Length; i++)
            {
                char c = choices[i];
                if (c == '\\' && i + 1 < choices.Length) { sb.Append(choices[i + 1]); i++; continue; }
                if (c == ',') break;
                sb.Append(c);
            }
            return sb.ToString();
        }

        private static string SnippetVariable(string name)
        {
            switch (name)
            {
                case "CURRENT_YEAR": return DateTime.Now.Year.ToString(CultureInfo.InvariantCulture);
                case "CURRENT_MONTH": return DateTime.Now.Month.ToString("00", CultureInfo.InvariantCulture);
                case "CURRENT_DATE": return DateTime.Now.Day.ToString("00", CultureInfo.InvariantCulture);
                case "TM_SELECTED_TEXT": return "";
                default: return null;
            }
        }

        // ---------------------------------------------------------------- markdown

        /// <summary>
        /// Renders LSP markdown (hover / documentation) as readable plain text: code fences keep their content,
        /// headings / emphasis / inline-code markers and link targets are dropped, HTML entities decoded.
        /// </summary>
        public static string MarkdownToPlainText(string md)
        {
            if (string.IsNullOrEmpty(md)) return "";
            string[] lines = md.Replace("\r\n", "\n").Split('\n');
            StringBuilder sb = new StringBuilder(md.Length);
            bool inFence = false;
            bool lastBlank = true;
            foreach (string raw in lines)
            {
                string line = raw;
                string t = line.TrimStart();
                if (t.StartsWith("```", StringComparison.Ordinal) || t.StartsWith("~~~", StringComparison.Ordinal))
                {
                    inFence = !inFence;
                    continue;
                }
                if (inFence)
                {
                    sb.Append(line).Append('\n');
                    lastBlank = false;
                    continue;
                }
                if (t.Length == 0)
                {
                    if (!lastBlank) sb.Append('\n');
                    lastBlank = true;
                    continue;
                }
                if (t == "---" || t == "***" || t == "___" || t == "- - -")
                {
                    if (!lastBlank) sb.Append('\n');
                    lastBlank = true;
                    continue;
                }
                while (t.StartsWith("#", StringComparison.Ordinal)) t = t.Substring(1);
                if (t.Length != line.TrimStart().Length) t = t.TrimStart();
                else t = line.TrimEnd();
                if (t.StartsWith("> ", StringComparison.Ordinal)) t = t.Substring(2);
                t = StripInlineMarkdown(t);
                sb.Append(t).Append('\n');
                lastBlank = false;
            }
            return sb.ToString().Trim('\n', ' ');
        }

        private static string StripInlineMarkdown(string s)
        {
            StringBuilder sb = new StringBuilder(s.Length);
            int i = 0;
            while (i < s.Length)
            {
                char c = s[i];
                if (c == '\\' && i + 1 < s.Length && "\\`*_{}[]()#+-.!|<>~".IndexOf(s[i + 1]) >= 0)
                {
                    sb.Append(s[i + 1]);
                    i += 2;
                    continue;
                }
                if (c == '`')
                {
                    int ticks = 0;
                    while (i + ticks < s.Length && s[i + ticks] == '`') ticks++;
                    string fence = new string('`', ticks);
                    int end = s.IndexOf(fence, i + ticks, StringComparison.Ordinal);
                    if (end > 0)
                    {
                        sb.Append(s, i + ticks, end - i - ticks);
                        i = end + ticks;
                        continue;
                    }
                    sb.Append(fence);
                    i += ticks;
                    continue;
                }
                if (c == '[' || (c == '!' && i + 1 < s.Length && s[i + 1] == '['))
                {
                    int open = c == '!' ? i + 1 : i;
                    int close = s.IndexOf(']', open + 1);
                    if (close > open && close + 1 < s.Length && s[close + 1] == '(')
                    {
                        int paren = s.IndexOf(')', close + 2);
                        if (paren > close)
                        {
                            sb.Append(StripInlineMarkdown(s.Substring(open + 1, close - open - 1)));
                            i = paren + 1;
                            continue;
                        }
                    }
                }
                if ((c == '*' || c == '_') && i + 1 < s.Length && s[i + 1] == c)
                {
                    // **bold** / __bold__ (but keep __init__-style identifiers intact)
                    bool wordBefore = i > 0 && char.IsLetterOrDigit(s[i - 1]);
                    if (c == '*' || !wordBefore)
                    {
                        int end = s.IndexOf(new string(c, 2), i + 2, StringComparison.Ordinal);
                        bool wordAfterEnd = end > 0 && end + 2 < s.Length && char.IsLetterOrDigit(s[end + 2]);
                        if (end > i + 2 && (c == '*' || !wordAfterEnd))
                        {
                            sb.Append(StripInlineMarkdown(s.Substring(i + 2, end - i - 2)));
                            i = end + 2;
                            continue;
                        }
                    }
                }
                if (c == '*' && i + 1 < s.Length && s[i + 1] != ' ' && (i == 0 || !char.IsLetterOrDigit(s[i - 1])))
                {
                    int end = s.IndexOf('*', i + 1);
                    if (end > i + 1 && s[end - 1] != ' ')
                    {
                        sb.Append(s, i + 1, end - i - 1);
                        i = end + 1;
                        continue;
                    }
                }
                if (c == '&')
                {
                    int semi = s.IndexOf(';', i);
                    if (semi > i && semi - i <= 8)
                    {
                        string ent = s.Substring(i, semi - i + 1);
                        string rep = null;
                        switch (ent)
                        {
                            case "&nbsp;": rep = " "; break;
                            case "&lt;": rep = "<"; break;
                            case "&gt;": rep = ">"; break;
                            case "&amp;": rep = "&"; break;
                            case "&quot;": rep = "\""; break;
                            case "&#39;": rep = "'"; break;
                        }
                        if (rep != null) { sb.Append(rep); i = semi + 1; continue; }
                    }
                }
                if (c == '<' && (StartsWithAt(s, i, "<br>") || StartsWithAt(s, i, "<br/>") || StartsWithAt(s, i, "<br />")))
                {
                    int gt = s.IndexOf('>', i);
                    sb.Append(' ');
                    i = gt + 1;
                    continue;
                }
                sb.Append(c);
                i++;
            }
            return sb.ToString();
        }

        private static bool StartsWithAt(string s, int i, string what)
        {
            return string.CompareOrdinal(s, i, what, 0, what.Length) == 0;
        }
    }
}
