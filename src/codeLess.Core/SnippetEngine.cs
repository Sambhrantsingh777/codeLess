using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace codeLess.Studio
{
    /// <summary>Result of expanding a snippet body: text to insert plus where the caret / selection goes.</summary>
    public sealed class SnippetExpansion
    {
        public string Text = "";
        /// <summary>Offset in <see cref="Text"/> of the first tab stop ($1, else $0, else the end).</summary>
        public int CaretOffset;
        /// <summary>Length of the first tab stop's placeholder text (selected after insertion).</summary>
        public int SelectionLength;
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
            int first = -1;
            foreach (int n in stopOffsets.Keys) if (n > 0 && (first < 0 || n < first)) first = n;
            if (first < 0 && stopOffsets.ContainsKey(0)) first = 0;
            if (first >= 0)
            {
                int start = stopOffsets[first];
                int end = start + stopLengths[first];
                r.CaretOffset = map[Math.Min(start, sb.Length)];
                r.SelectionLength = map[Math.Min(end, sb.Length)] - r.CaretOffset;
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
                if (char.IsDigit(nx))
                {
                    int j = i + 1;
                    while (j < s.Length && char.IsDigit(s[j])) j++;
                    int n = int.Parse(s.Substring(i + 1, j - i - 1), CultureInfo.InvariantCulture);
                    Mark(offsets, lengths, n, sb.Length, 0);
                    i = j;
                    continue;
                }
                if (nx == '{')
                {
                    int j = i + 2;
                    if (j < s.Length && char.IsDigit(s[j]))
                    {
                        int k = j;
                        while (k < s.Length && char.IsDigit(s[k])) k++;
                        int n = int.Parse(s.Substring(j, k - j), CultureInfo.InvariantCulture);
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
