using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace codeLess.Studio
{
    public sealed class SnippetTabStop
    {
        public int Index;
        public int Offset;
        public int Length;
    }

    /// <summary>Result of expanding a snippet body: text to insert plus where the caret / selection goes.</summary>
    public sealed class SnippetExpansion
    {
        public string Text = "";
        /// <summary>Offset in <see cref="Text"/> of the first tab stop ($1, else $0, else the end).</summary>
        public int CaretOffset;
        /// <summary>Length of the first tab stop's placeholder text (selected after insertion).</summary>
        public int SelectionLength;
        public List<SnippetTabStop> TabStops = new List<SnippetTabStop>();
    }

    /// <summary>
    /// VS Code snippets for both editors (built-in and contributed by VSIX extensions): language matching (VS Code
    /// language ids and file extensions), TextMate snippet syntax ($1, ${1:default}, ${1|a,b|}, $0, variables,
    /// escapes), indentation of continuation lines and tab expansion of an exact prefix before the caret. Strict C# 5.
    /// </summary>
    public static class SnippetEngine
    {
        private static readonly Dictionary<string, string> LanguageAliases = BuildAliases();

        private static Dictionary<string, string> BuildAliases()
        {
            Dictionary<string, string> m = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            Action<string, string[]> add = delegate (string id, string[] aliases)
            {
                m[id] = id;
                foreach (string a in aliases) m[a] = id;
            };
            add("csharp", new[] { "cs", "c#", "csx" });
            add("javascript", new[] { "js", "mjs", "cjs" });
            add("javascriptreact", new[] { "jsx" });
            add("typescript", new[] { "ts", "mts", "cts" });
            add("typescriptreact", new[] { "tsx" });
            add("python", new[] { "py", "pyw", "pyi" });
            add("rust", new[] { "rs" });
            add("go", new[] { "golang" });
            add("cpp", new[] { "c++", "cc", "cxx", "hpp", "hh", "hxx" });
            add("c", new[] { "h" });
            add("java", new string[0]);
            add("kotlin", new[] { "kt", "kts" });
            add("ruby", new[] { "rb" });
            add("php", new string[0]);
            add("html", new[] { "htm", "xhtml" });
            add("css", new string[0]);
            add("scss", new string[0]);
            add("less", new string[0]);
            add("json", new[] { "jsonc" });
            add("markdown", new[] { "md", "mdx" });
            add("shellscript", new[] { "sh", "bash", "zsh", "shell" });
            add("powershell", new[] { "ps1", "psm1", "pwsh" });
            add("yaml", new[] { "yml" });
            add("xml", new[] { "xaml", "csproj", "props", "targets" });
            add("sql", new string[0]);
            add("swift", new string[0]);
            add("lua", new string[0]);
            add("dart", new string[0]);
            add("plaintext", new[] { "txt", "text" });
            return m;
        }

        /// <summary>VS Code language id for an id, alias or file extension ("cs" -> "csharp").</summary>
        public static string NormalizeLanguage(string lang)
        {
            if (string.IsNullOrEmpty(lang)) return "";
            string l = lang.Trim().TrimStart('.').ToLowerInvariant();
            string id;
            return LanguageAliases.TryGetValue(l, out id) ? id : l;
        }

        /// <summary>A snippet without a language (global snippet file) applies everywhere; otherwise one of its comma-separated ids must match.</summary>
        public static bool AppliesTo(CodeSnippetItem s, string bufferLanguage)
        {
            if (s == null) return false;
            if (string.IsNullOrEmpty(s.Language) || s.Language.Trim() == "*") return true;
            string target = NormalizeLanguage(bufferLanguage);
            foreach (string part in s.Language.Split(','))
                if (NormalizeLanguage(part) == target) return true;
            return false;
        }

        public static List<CodeSnippetItem> ForLanguage(IEnumerable<CodeSnippetItem> all, string bufferLanguage)
        {
            List<CodeSnippetItem> r = new List<CodeSnippetItem>();
            if (all == null) return r;
            foreach (CodeSnippetItem s in all.ToList())
                if (AppliesTo(s, bufferLanguage)) r.Add(s);
            return r;
        }

        /// <summary>
        /// Expands a TextMate/VS Code snippet body. <paramref name="baseIndent"/> prefixes continuation lines,
        /// <paramref name="indentUnit"/> replaces tab characters (the editor's tab size / insert-spaces setting).
        /// </summary>
        public static SnippetExpansion Expand(string body, string baseIndent, string indentUnit, IDictionary<string, string> variables)
        {
            string norm = (body ?? "").Replace("\r\n", "\n").Replace("\r", "\n");
            Dictionary<int, int> stopOffsets = new Dictionary<int, int>();
            Dictionary<int, int> stopLengths = new Dictionary<int, int>();
            StringBuilder sb = new StringBuilder();
            int i = 0;
            ParseInto(norm, ref i, sb, stopOffsets, stopLengths, variables, '\0');

            // Indentation: tabs -> the editor's unit, continuation lines get the caret line's indent.
            string unit = indentUnit ?? "\t";
            string indent = baseIndent ?? "";
            StringBuilder outSb = new StringBuilder();
            int[] map = new int[sb.Length + 1];
            for (int k = 0; k < sb.Length; k++)
            {
                map[k] = outSb.Length;
                char ch = sb[k];
                if (ch == '\t') outSb.Append(unit);
                else if (ch == '\n') { outSb.Append('\n'); outSb.Append(indent); }
                else outSb.Append(ch);
            }
            map[sb.Length] = outSb.Length;

            SnippetExpansion r = new SnippetExpansion();
            r.Text = outSb.ToString();
            List<int> posKeys = new List<int>();
            foreach (int n in stopOffsets.Keys) if (n > 0) posKeys.Add(n);
            posKeys.Sort();
            foreach (int n in posKeys)
            {
                int st = stopOffsets[n];
                int en = st + stopLengths[n];
                int mappedStart = map[Math.Min(st, sb.Length)];
                int mappedLen = map[Math.Min(en, sb.Length)] - mappedStart;
                r.TabStops.Add(new SnippetTabStop { Index = n, Offset = mappedStart, Length = mappedLen });
            }
            if (stopOffsets.ContainsKey(0))
            {
                int st0 = stopOffsets[0];
                int en0 = st0 + stopLengths[0];
                int mappedStart0 = map[Math.Min(st0, sb.Length)];
                int mappedLen0 = map[Math.Min(en0, sb.Length)] - mappedStart0;
                r.TabStops.Add(new SnippetTabStop { Index = 0, Offset = mappedStart0, Length = mappedLen0 });
            }
            else if (r.TabStops.Count > 0)
            {
                r.TabStops.Add(new SnippetTabStop { Index = 0, Offset = r.Text.Length, Length = 0 });
            }
            if (r.TabStops.Count > 0)
            {
                r.CaretOffset = r.TabStops[0].Offset;
                r.SelectionLength = r.TabStops[0].Length;
            }
            else r.CaretOffset = r.Text.Length;
            return r;
        }

        private static void ParseInto(string s, ref int i, StringBuilder sb, Dictionary<int, int> offsets, Dictionary<int, int> lengths,
            IDictionary<string, string> vars, char stopAt)
        {
            while (i < s.Length)
            {
                char c = s[i];
                if (stopAt != '\0' && c == stopAt) return;
                if (c == '\\' && i + 1 < s.Length && (s[i + 1] == '$' || s[i + 1] == '}' || s[i + 1] == '\\' || s[i + 1] == ',' || s[i + 1] == '|'))
                {
                    sb.Append(s[i + 1]);
                    i += 2;
                    continue;
                }
                if (c != '$' || i + 1 >= s.Length) { sb.Append(c); i++; continue; }
                char nx = s[i + 1];
                if (nx >= '0' && nx <= '9')
                {
                    int j = i + 1;
                    while (j < s.Length && s[j] >= '0' && s[j] <= '9') j++;
                    int n;
                    if (!int.TryParse(s.Substring(i + 1, j - i - 1), NumberStyles.None, CultureInfo.InvariantCulture, out n))
                    {
                        sb.Append(s, i, j - i); // overflowing tab stop number: literal text
                        i = j;
                        continue;
                    }
                    Mark(offsets, lengths, n, sb.Length, 0);
                    i = j;
                    continue;
                }
                if (nx == '{')
                {
                    int j = i + 2;
                    int kEnd = j;
                    while (kEnd < s.Length && s[kEnd] >= '0' && s[kEnd] <= '9') kEnd++;
                    int parsedN = 0;
                    bool validN = kEnd > j && int.TryParse(s.Substring(j, kEnd - j), NumberStyles.None, CultureInfo.InvariantCulture, out parsedN);
                    if (kEnd > j && !validN)
                    {
                        sb.Append("${");
                        i = j;
                        continue;
                    }
                    if (validN)
                    {
                        int k = kEnd;
                        int n = parsedN;
                        int start = sb.Length;
                        if (k < s.Length && s[k] == ':')
                        {
                            i = k + 1;
                            ParseInto(s, ref i, sb, offsets, lengths, vars, '}');
                            if (i < s.Length) i++; // '}'
                        }
                        else if (k < s.Length && s[k] == '|')
                        {
                            int close = s.IndexOf("|}", k + 1, StringComparison.Ordinal);
                            string choices = close > k ? s.Substring(k + 1, close - k - 1) : "";
                            string firstChoice = SplitChoices(choices).FirstOrDefault() ?? "";
                            sb.Append(firstChoice);
                            i = close > k ? close + 2 : s.Length;
                        }
                        else
                        {
                            i = (k < s.Length && s[k] == '}') ? k + 1 : k;
                        }
                        Mark(offsets, lengths, n, start, sb.Length - start);
                        continue;
                    }
                    if (j < s.Length && (char.IsLetter(s[j]) || s[j] == '_'))
                    {
                        int k = j;
                        while (k < s.Length && (char.IsLetterOrDigit(s[k]) || s[k] == '_')) k++;
                        string name = s.Substring(j, k - j);
                        string val = null;
                        bool known = vars != null && vars.TryGetValue(name, out val) && val != null;
                        if (k < s.Length && s[k] == ':')
                        {
                            i = k + 1;
                            StringBuilder def = new StringBuilder();
                            ParseInto(s, ref i, def, new Dictionary<int, int>(), new Dictionary<int, int>(), vars, '}');
                            if (i < s.Length) i++;
                            sb.Append(known && val.Length > 0 ? val : def.ToString());
                        }
                        else
                        {
                            int close = s.IndexOf('}', k);
                            i = close >= 0 ? close + 1 : s.Length;
                            sb.Append(known ? val : "");
                        }
                        continue;
                    }
                }
                if (char.IsLetter(nx) || nx == '_')
                {
                    int j = i + 1;
                    while (j < s.Length && (char.IsLetterOrDigit(s[j]) || s[j] == '_')) j++;
                    string name = s.Substring(i + 1, j - i - 1);
                    string val;
                    if (vars != null && vars.TryGetValue(name, out val) && val != null) sb.Append(val);
                    i = j;
                    continue;
                }
                sb.Append(c);
                i++;
            }
        }

        private static IEnumerable<string> SplitChoices(string choices)
        {
            StringBuilder cur = new StringBuilder();
            for (int k = 0; k < choices.Length; k++)
            {
                if (choices[k] == '\\' && k + 1 < choices.Length) { cur.Append(choices[++k]); continue; }
                if (choices[k] == ',') { yield return cur.ToString(); cur.Length = 0; continue; }
                cur.Append(choices[k]);
            }
            yield return cur.ToString();
        }

        private static void Mark(Dictionary<int, int> offsets, Dictionary<int, int> lengths, int n, int offset, int length)
        {
            if (offsets.ContainsKey(n)) return; // the first occurrence of a tab stop wins
            offsets[n] = offset;
            lengths[n] = length;
        }

        /// <summary>Standard snippet variables for a buffer (file names, selection, current line, date).</summary>
        public static Dictionary<string, string> VariablesFor(EditorTabBuffer buf)
        {
            Dictionary<string, string> v = new Dictionary<string, string>(StringComparer.Ordinal);
            DateTime now = DateTime.Now;
            string file = buf != null ? (buf.FileName ?? "") : "";
            v["TM_FILENAME"] = file;
            v["TM_FILENAME_BASE"] = Path.GetFileNameWithoutExtension(file);
            v["TM_FILEPATH"] = buf != null ? (buf.FilePath ?? "") : "";
            v["TM_DIRECTORY"] = buf != null && !string.IsNullOrEmpty(buf.FilePath) ? (Path.GetDirectoryName(buf.FilePath) ?? "") : "";
            v["TM_SELECTED_TEXT"] = buf != null && buf.HasSelection() ? buf.GetSelectedText() : "";
            v["TM_CURRENT_LINE"] = buf != null && buf.Lines != null && buf.CursorLine >= 0 && buf.CursorLine < buf.Lines.Count ? buf.Lines[buf.CursorLine] : "";
            v["TM_LINE_NUMBER"] = buf != null ? (buf.CursorLine + 1).ToString(CultureInfo.InvariantCulture) : "1";
            v["CURRENT_YEAR"] = now.Year.ToString(CultureInfo.InvariantCulture);
            v["CURRENT_YEAR_SHORT"] = (now.Year % 100).ToString("00", CultureInfo.InvariantCulture);
            v["CURRENT_MONTH"] = now.Month.ToString("00", CultureInfo.InvariantCulture);
            v["CURRENT_DATE"] = now.Day.ToString("00", CultureInfo.InvariantCulture);
            v["CURRENT_HOUR"] = now.Hour.ToString("00", CultureInfo.InvariantCulture);
            v["CURRENT_MINUTE"] = now.Minute.ToString("00", CultureInfo.InvariantCulture);
            v["CURRENT_SECOND"] = now.Second.ToString("00", CultureInfo.InvariantCulture);
            return v;
        }

        public static string IndentUnit(int tabSize, bool insertSpaces)
        {
            return insertSpaces ? new string(' ', Math.Max(1, tabSize)) : "\t";
        }

        /// <summary>The identifier-like word ending at the caret (snippet prefixes may contain letters, digits, _ - . :).</summary>
        public static string WordBeforeCaret(string line, int col)
        {
            if (line == null) return "";
            col = Math.Max(0, Math.Min(line.Length, col));
            int s = col;
            while (s > 0 && (char.IsLetterOrDigit(line[s - 1]) || line[s - 1] == '_' || line[s - 1] == '-' || line[s - 1] == '.' || line[s - 1] == ':' || line[s - 1] == '!' || line[s - 1] == '@'))
                s--;
            return line.Substring(s, col - s);
        }

        /// <summary>
        /// Inserts <paramref name="snippet"/> at the caret, replacing <paramref name="replaceChars"/> characters before
        /// it (the typed prefix). Undoable; leaves the first tab stop selected. Call SyncContentFromLines afterwards
        /// through the editor's normal edit path.
        /// </summary>
        public static void InsertAtCaret(EditorTabBuffer buf, CodeSnippetItem snippet, int replaceChars, int tabSize, bool insertSpaces)
        {
            if (buf == null || snippet == null || buf.Lines == null) return;
            buf.ClampCursor();
            buf.PushUndoSnapshot();
            if (buf.HasSelection()) buf.ClearSelection();
            string line = buf.Lines[buf.CursorLine] ?? "";
            int col = Math.Min(line.Length, buf.CursorCol);
            int start = Math.Max(0, col - Math.Max(0, replaceChars));
            int ws = 0;
            while (ws < line.Length && (line[ws] == ' ' || line[ws] == '\t')) ws++;
            string baseIndent = line.Substring(0, ws);
            SnippetExpansion ex = Expand(snippet.Body, baseIndent, IndentUnit(tabSize, insertSpaces), VariablesFor(buf));
            string before = line.Substring(0, start);
            string after = line.Substring(col);
            string[] parts = (before + ex.Text + after).Split('\n');
            buf.Lines[buf.CursorLine] = parts[0];
            for (int k = 1; k < parts.Length; k++) buf.Lines.Insert(buf.CursorLine + k, parts[k]);

            // Caret / selection at the first tab stop (offsets are relative to the inserted text).
            int startLine = buf.CursorLine;
            int absStart = before.Length + ex.CaretOffset;
            int absEnd = absStart + ex.SelectionLength;
            int sl, sc, el, ec;
            OffsetToLineCol(parts, absStart, out sl, out sc);
            OffsetToLineCol(parts, absEnd, out el, out ec);
            if (ex.SelectionLength > 0)
            {
                buf.SelectionAnchorLine = startLine + sl;
                buf.SelectionAnchorCol = sc;
                buf.CursorLine = startLine + el;
                buf.CursorCol = ec;
            }
            else
            {
                buf.ClearSelection();
                buf.CursorLine = startLine + sl;
                buf.CursorCol = sc;
            }

            if (ex.TabStops.Count > 1)
            {
                List<SnippetStopEntry> entries = new List<SnippetStopEntry>();
                foreach (SnippetTabStop ts in ex.TabStops)
                {
                    int tsl, tsc, tel, tec;
                    OffsetToLineCol(parts, before.Length + ts.Offset, out tsl, out tsc);
                    OffsetToLineCol(parts, before.Length + ts.Offset + ts.Length, out tel, out tec);
                    int docLine = startLine + tsl;
                    int lineLen = (docLine >= 0 && docLine < buf.Lines.Count) ? buf.Lines[docLine].Length : 0;
                    entries.Add(new SnippetStopEntry
                    {
                        Index = ts.Index,
                        Line = docLine,
                        StartCol = tsc,
                        EndCol = (tel == tsl) ? tec : tsc,
                        InitialLineLength = lineLen
                    });
                }
                SetActiveSession(buf, new ActiveSnippetSession { Stops = entries, CurrentIndex = 0 });
            }
            else
            {
                EndSession(buf);
            }
        }

        private sealed class SnippetStopEntry
        {
            public int Index;
            public int Line;
            public int StartCol;
            public int EndCol;
            public int InitialLineLength;
        }

        private sealed class ActiveSnippetSession
        {
            public List<SnippetStopEntry> Stops = new List<SnippetStopEntry>();
            public int CurrentIndex;
        }

        private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<EditorTabBuffer, ActiveSnippetSession> sessions
            = new System.Runtime.CompilerServices.ConditionalWeakTable<EditorTabBuffer, ActiveSnippetSession>();

        private static void SetActiveSession(EditorTabBuffer buf, ActiveSnippetSession s)
        {
            if (buf == null) return;
            sessions.Remove(buf);
            if (s != null) sessions.Add(buf, s);
        }

        public static bool HasActiveSession(EditorTabBuffer buf)
        {
            ActiveSnippetSession s;
            return buf != null && sessions.TryGetValue(buf, out s) && s != null && s.Stops.Count > 1;
        }

        public static void EndSession(EditorTabBuffer buf)
        {
            if (buf == null) return;
            sessions.Remove(buf);
        }

        public static void StartSessionFromBufferOffsets(EditorTabBuffer buf, int baseOffset, List<int[]> orderedStops)
        {
            if (buf == null || orderedStops == null || orderedStops.Count <= 1 || buf.Lines == null)
            {
                EndSession(buf);
                return;
            }
            List<SnippetStopEntry> entries = new List<SnippetStopEntry>();
            foreach (int[] st in orderedStops)
            {
                int sl, sc, el, ec;
                BufferOffsetToLineCol(buf, baseOffset + st[0], out sl, out sc);
                BufferOffsetToLineCol(buf, baseOffset + st[0] + st[1], out el, out ec);
                int lineLen = (sl >= 0 && sl < buf.Lines.Count) ? buf.Lines[sl].Length : 0;
                entries.Add(new SnippetStopEntry
                {
                    Index = st.Length > 2 ? st[2] : 0,
                    Line = sl,
                    StartCol = sc,
                    EndCol = (el == sl) ? ec : sc,
                    InitialLineLength = lineLen
                });
            }
            SetActiveSession(buf, new ActiveSnippetSession { Stops = entries, CurrentIndex = 0 });
        }

        private static void BufferOffsetToLineCol(EditorTabBuffer buf, int offset, out int line, out int col)
        {
            int rem = Math.Max(0, offset);
            for (int i = 0; i < buf.Lines.Count; i++)
            {
                int len = buf.Lines[i].Length;
                if (rem <= len || i == buf.Lines.Count - 1)
                {
                    line = i;
                    col = Math.Min(rem, len);
                    return;
                }
                rem -= len + 1;
            }
            line = Math.Max(0, buf.Lines.Count - 1);
            col = buf.Lines.Count > 0 ? buf.Lines[line].Length : 0;
        }

        /// <summary>
        /// Advances (Tab) or retreats (Shift+Tab) across active snippet tab stops ($1 -> $2 -> $0).
        /// Reaching $0 places the caret and ends the snippet session. Returns true when handled.
        /// </summary>
        public static bool TryJumpStop(EditorTabBuffer buf, bool backwards)
        {
            ActiveSnippetSession s;
            if (buf == null || buf.Lines == null || !sessions.TryGetValue(buf, out s) || s == null || s.Stops.Count <= 1)
                return false;

            int curIdx = s.CurrentIndex;
            if (curIdx >= 0 && curIdx < s.Stops.Count)
            {
                SnippetStopEntry cur = s.Stops[curIdx];
                if (cur.Line >= 0 && cur.Line < buf.Lines.Count)
                {
                    int curLen = buf.Lines[cur.Line].Length;
                    int delta = curLen - cur.InitialLineLength;
                    if (delta != 0)
                    {
                        cur.EndCol = Math.Max(cur.StartCol, cur.EndCol + delta);
                        cur.InitialLineLength = curLen;
                        for (int k = 0; k < s.Stops.Count; k++)
                        {
                            if (k == curIdx) continue;
                            SnippetStopEntry other = s.Stops[k];
                            if (other.Line == cur.Line)
                            {
                                if (other.StartCol >= cur.StartCol)
                                {
                                    other.StartCol = Math.Max(0, other.StartCol + delta);
                                    other.EndCol = Math.Max(other.StartCol, other.EndCol + delta);
                                }
                                other.InitialLineLength = curLen;
                            }
                        }
                    }
                }
            }

            int nextIdx = backwards ? curIdx - 1 : curIdx + 1;
            if (nextIdx < 0 || nextIdx >= s.Stops.Count)
            {
                if (!backwards) EndSession(buf);
                return false;
            }

            s.CurrentIndex = nextIdx;
            SnippetStopEntry target = s.Stops[nextIdx];
            int line = Math.Max(0, Math.Min(buf.Lines.Count - 1, target.Line));
            int maxCol = buf.Lines[line].Length;
            int sc = Math.Max(0, Math.Min(maxCol, target.StartCol));
            int ec = Math.Max(sc, Math.Min(maxCol, target.EndCol));
            target.InitialLineLength = maxCol;

            if (ec > sc)
            {
                buf.SelectionAnchorLine = line;
                buf.SelectionAnchorCol = sc;
                buf.CursorLine = line;
                buf.CursorCol = ec;
            }
            else
            {
                buf.ClearSelection();
                buf.CursorLine = line;
                buf.CursorCol = sc;
            }

            if (target.Index == 0 || nextIdx == s.Stops.Count - 1 && !backwards && target.Index == 0)
            {
                EndSession(buf);
            }
            return true;
        }

        private static void OffsetToLineCol(string[] parts, int offset, out int line, out int col)
        {
            int acc = 0;
            for (int k = 0; k < parts.Length; k++)
            {
                if (offset <= acc + parts[k].Length) { line = k; col = offset - acc; return; }
                acc += parts[k].Length + 1;
            }
            line = parts.Length - 1;
            col = parts[parts.Length - 1].Length;
        }

        /// <summary>
        /// Tab completion of snippets: when the word before the caret is exactly a snippet prefix for the buffer's
        /// language (and nothing is selected), expands it and returns true.
        /// </summary>
        public static bool TryExpandPrefixAtCaret(EditorTabBuffer buf, IEnumerable<CodeSnippetItem> snippets, int tabSize, bool insertSpaces)
        {
            if (buf == null || buf.Lines == null || buf.HasSelection() || snippets == null) return false;
            buf.ClampCursor();
            string word = WordBeforeCaret(buf.Lines[buf.CursorLine], buf.CursorCol);
            if (word.Length == 0) return false;
            CodeSnippetItem hit = ForLanguage(snippets, buf.Language).FirstOrDefault(s => string.Equals(s.Prefix, word, StringComparison.Ordinal));
            if (hit == null) return false;
            InsertAtCaret(buf, hit, word.Length, tabSize, insertSpaces);
            return true;
        }
    }
}
