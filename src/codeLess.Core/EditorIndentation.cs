using System;
using System.Collections.Generic;
using System.Text;

namespace codeLess.Studio
{
    /// <summary>Persisted "editor.detectIndentation" (default on, like VS Code).</summary>
    public partial class IdePersistentSettings
    {
        private bool detectIndentation = true;

        public bool DetectIndentation
        {
            get { return detectIndentation; }
            set { detectIndentation = value; }
        }
    }

    /// <summary>
    /// Indentation rules shared by both editors (QA #12): effective tab size / insert-spaces per buffer, VS Code's
    /// indentation guesser ("editor.detectIndentation"), .editorconfig on open, and the Tab / Shift+Tab / Enter /
    /// Backspace edits that depend on them. STRICT C# 5.
    /// </summary>
    public static class EditorIndentation
    {
        public static int EffectiveTabSize(EditorTabBuffer buf, int globalTabSize)
        {
            int t = (buf != null && buf.CustomTabSize.HasValue && buf.CustomTabSize.Value > 0) ? buf.CustomTabSize.Value : globalTabSize;
            return Math.Max(1, Math.Min(16, t <= 0 ? 4 : t));
        }

        public static bool EffectiveInsertSpaces(EditorTabBuffer buf, bool globalInsertSpaces)
        {
            return (buf != null && buf.CustomInsertSpaces.HasValue) ? buf.CustomInsertSpaces.Value : globalInsertSpaces;
        }

        /// <summary>Status bar text: "Spaces: 4" or "Tab Size: 4" (VS Code).</summary>
        public static string StatusText(int tabSize, bool insertSpaces)
        {
            return (insertSpaces ? "Spaces: " : "Tab Size: ") + tabSize.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        /// <summary>One indentation level.</summary>
        public static string Unit(int tabSize, bool insertSpaces)
        {
            return insertSpaces ? new string(' ', Math.Max(1, tabSize)) : "\t";
        }

        /// <summary>Whitespace for <paramref name="visualCols"/> columns of indentation in the buffer's style (tabs + remainder spaces).</summary>
        public static string Build(int visualCols, int tabSize, bool insertSpaces)
        {
            if (visualCols <= 0) return "";
            if (insertSpaces) return new string(' ', visualCols);
            int t = Math.Max(1, tabSize);
            return new string('\t', visualCols / t) + new string(' ', visualCols % t);
        }

        public static int LeadingWhitespaceLength(string line)
        {
            if (line == null) return 0;
            int i = 0;
            while (i < line.Length && (line[i] == ' ' || line[i] == '\t')) i++;
            return i;
        }

        /// <summary>Text a plain Tab inserts at <paramref name="col"/> (spaces up to the next tab stop of the caret's visual column, or a tab).</summary>
        public static string TabInsertText(string line, int col, int tabSize, bool insertSpaces)
        {
            if (!insertSpaces) return "\t";
            int t = Math.Max(1, tabSize);
            int v = EditorTextLayout.VisualColumn(line ?? "", col, t);
            return new string(' ', t - (v % t));
        }

        /// <summary>Indents one line by a level (normalizing to the next tab stop, like VS Code's shift). Returns the new line.</summary>
        public static string IndentLine(string line, int tabSize, bool insertSpaces)
        {
            line = line ?? "";
            int ws = LeadingWhitespaceLength(line);
            int t = Math.Max(1, tabSize);
            int cur = EditorTextLayout.VisualColumn(line, ws, t);
            int next = (cur / t + 1) * t;
            return Build(next, t, insertSpaces) + line.Substring(ws);
        }

        /// <summary>Outdents one line to the previous tab stop. Returns the new line (unchanged when not indented).</summary>
        public static string OutdentLine(string line, int tabSize, bool insertSpaces)
        {
            line = line ?? "";
            int ws = LeadingWhitespaceLength(line);
            if (ws == 0) return line;
            int t = Math.Max(1, tabSize);
            int cur = EditorTextLayout.VisualColumn(line, ws, t);
            int prev = cur % t == 0 ? cur - t : (cur / t) * t;
            if (prev < 0) prev = 0;
            // keep the line's own style when it is uniform (a tab-indented line stays tab-indented)
            bool tabs = line.IndexOf('\t', 0, ws) >= 0;
            bool spaces = line.IndexOf(' ', 0, ws) >= 0;
            bool useSpaces = tabs && !spaces ? false : (spaces && !tabs ? true : insertSpaces);
            return Build(prev, t, useSpaces) + line.Substring(ws);
        }

        /// <summary>
        /// Backspace with "editor.useTabStops": when only spaces precede the caret inside the indentation, delete back
        /// to the previous tab stop. Returns the number of characters to delete (1 when the rule does not apply).
        /// </summary>
        public static int BackspaceDeleteCount(string line, int col, int tabSize)
        {
            if (string.IsNullOrEmpty(line) || col <= 0 || col > line.Length) return 1;
            for (int i = 0; i < col; i++) if (line[i] != ' ') return 1;
            int t = Math.Max(1, tabSize);
            int rem = col % t;
            int n = rem == 0 ? t : rem;
            return Math.Max(1, Math.Min(col, n));
        }

        // ------------------------------------------------------------------ detection (VS Code guessIndentation)
        private struct SpacesDiffResult
        {
            public int SpacesDiff;
            public bool LooksLikeAlignment;
        }

        private static SpacesDiffResult SpacesDiff(string a, int aLength, string b, int bLength)
        {
            SpacesDiffResult r = new SpacesDiffResult();
            int i;
            for (i = 0; i < aLength && i < bLength; i++) if (a[i] != b[i]) break;
            int aSpaces = 0, aTabs = 0;
            for (int j = i; j < aLength; j++) { if (a[j] == ' ') aSpaces++; else aTabs++; }
            int bSpaces = 0, bTabs = 0;
            for (int j = i; j < bLength; j++) { if (b[j] == ' ') bSpaces++; else bTabs++; }
            if (aSpaces > 0 && aTabs > 0) return r;
            if (bSpaces > 0 && bTabs > 0) return r;
            int tabsDiff = Math.Abs(aTabs - bTabs);
            int spacesDiff = Math.Abs(aSpaces - bSpaces);
            if (tabsDiff == 0)
            {
                r.SpacesDiff = spacesDiff;
                if (spacesDiff > 0 && 0 <= bSpaces - 1 && bSpaces - 1 < a.Length && bSpaces < b.Length)
                {
                    if (b[bSpaces] != ' ' && a[bSpaces - 1] == ' ' && a.Length > 0 && a[a.Length - 1] == ',')
                        r.LooksLikeAlignment = true; // "const a = b + c,\n      d = 4;"
                }
                return r;
            }
            if (spacesDiff % tabsDiff == 0) r.SpacesDiff = spacesDiff / tabsDiff;
            return r;
        }

        /// <summary>Port of VS Code's guessIndentation (first 10,000 lines).</summary>
        public static void Guess(IList<string> lines, int defaultTabSize, bool defaultInsertSpaces, out int tabSize, out bool insertSpaces)
        {
            int evidence;
            Guess(lines, defaultTabSize, defaultInsertSpaces, out tabSize, out insertSpaces, out evidence);
        }

        /// <summary>As above; <paramref name="indentedLines"/> = lines that start with whitespace (0: nothing to go by).</summary>
        public static void Guess(IList<string> lines, int defaultTabSize, bool defaultInsertSpaces, out int tabSize, out bool insertSpaces, out int indentedLines)
        {
            int linesCount = lines == null ? 0 : Math.Min(lines.Count, 10000);
            int withTabs = 0, withSpaces = 0;
            string prevText = "";
            int prevIndent = 0;
            int[] allowed = new int[] { 2, 4, 6, 8, 3, 5, 7 };
            int[] spacesDiffCount = new int[9];
            for (int ln = 0; ln < linesCount; ln++)
            {
                string text = lines[ln] ?? "";
                if (text.Length > 65536) continue;
                bool hasContent = false;
                int indent = 0, spaces = 0, tabs = 0;
                for (int j = 0; j < text.Length; j++)
                {
                    char c = text[j];
                    if (c == '\t') tabs++;
                    else if (c == ' ') spaces++;
                    else { hasContent = true; indent = j; break; }
                }
                if (!hasContent) continue;
                if (tabs > 0) withTabs++;
                else if (spaces > 1) withSpaces++;
                SpacesDiffResult d = SpacesDiff(prevText, prevIndent, text, indent);
                if (d.LooksLikeAlignment && !(defaultInsertSpaces && defaultTabSize == d.SpacesDiff)) continue;
                if (d.SpacesDiff <= 8) spacesDiffCount[d.SpacesDiff]++;
                prevText = text;
                prevIndent = indent;
            }
            indentedLines = withTabs + withSpaces;
            insertSpaces = defaultInsertSpaces;
            if (withTabs != withSpaces) insertSpaces = withTabs < withSpaces;
            tabSize = defaultTabSize;
            if (insertSpaces)
            {
                double score = 0;
                foreach (int possible in allowed)
                {
                    int s = spacesDiffCount[possible];
                    if (s > score) { score = s; tabSize = possible; }
                }
                if (tabSize == 4 && spacesDiffCount[4] > 0 && spacesDiffCount[2] > 0 && spacesDiffCount[2] >= spacesDiffCount[4] / 2)
                    tabSize = 2;
            }
        }

        /// <summary>
        /// Applies indentation settings to a freshly opened buffer: .editorconfig wins (VS Code + EditorConfig), else
        /// the guess from the content when detection is on. Leaves the global settings in effect when nothing applies.
        /// </summary>
        public static void ApplyOnOpen(EditorTabBuffer buf, IdePersistentSettings settings)
        {
            if (buf == null) return;
            int gTab = settings != null && settings.TabSize > 0 ? settings.TabSize : 4;
            bool gSpaces = settings == null || settings.InsertSpaces;
            EditorConfigProperties ec = null;
            try { ec = string.IsNullOrEmpty(buf.FilePath) ? null : EditorConfigResolver.Resolve(buf.FilePath); }
            catch { ec = null; }
            buf.EditorConfig = ec;
            bool styleSet = false, sizeSet = false;
            if (ec != null)
            {
                if (ec.IndentStyle == "space") { buf.CustomInsertSpaces = true; styleSet = true; }
                else if (ec.IndentStyle == "tab") { buf.CustomInsertSpaces = false; styleSet = true; }
                int size = ec.EffectiveTabSize;
                if (size > 0) { buf.CustomTabSize = size; sizeSet = true; }
            }
            bool detect = settings == null || settings.DetectIndentation;
            if (detect && (!styleSet || !sizeSet) && buf.Lines != null && buf.Lines.Count > 1)
            {
                int t, evidence;
                bool s;
                Guess(buf.Lines, sizeSet ? buf.CustomTabSize.Value : gTab, styleSet ? buf.CustomInsertSpaces.Value : gSpaces, out t, out s, out evidence);
                if (evidence > 0)
                {
                    if (!styleSet) buf.CustomInsertSpaces = s;
                    if (!sizeSet && buf.CustomInsertSpaces == true && s) buf.CustomTabSize = t;
                }
            }
        }

        // ------------------------------------------------------------------ Enter
        /// <summary>True for a line ending (before the caret) with an opener that indents the next line.</summary>
        public static bool IncreasesIndent(string beforeCaret, bool indentBasedLanguage)
        {
            string t = (beforeCaret ?? "").TrimEnd();
            if (t.Length == 0) return false;
            char c = t[t.Length - 1];
            if (c == '{' || c == '(' || c == '[') return true;
            if (c == ':' && indentBasedLanguage) return true;
            return false;
        }

        private static readonly string[] DedentAfterKeywords = new string[] { "return", "pass", "raise", "break", "continue" };

        /// <summary>
        /// Enter at <paramref name="col"/> of <paramref name="line"/> (VS Code auto-indent): returns the lines that replace
        /// the current line (2, or 3 for "{|}"), and the caret's row (offset from the current line) and column. Keeps the
        /// indentation up to the caret, adds a level after an opener (and ":" in indentation-based languages), dedents
        /// after return / pass / raise / break / continue in those languages, and leaves an indentation-only line empty
        /// (trimAutoWhitespace). The text moved to the new line loses its leading blanks.
        /// </summary>
        public static string[] SplitForEnter(string line, int col, int tabSize, bool insertSpaces, bool indentBased, out int caretRow, out int caretCol)
        {
            string cur = line ?? "";
            col = Math.Max(0, Math.Min(cur.Length, col));
            string unit = Unit(tabSize, insertSpaces);
            int ws = LeadingWhitespaceLength(cur);
            string indent = cur.Substring(0, Math.Min(ws, col));
            string before = cur.Substring(0, col);
            string after = cur.Substring(col);
            caretRow = 1;
            if (before.Length > 0 && before.Trim().Length == 0 && after.Trim().Length == 0)
            {
                caretCol = indent.Length;
                return new string[] { "", indent };
            }
            if (IsBetweenBrackets(before, after))
            {
                caretCol = indent.Length + unit.Length;
                return new string[] { before.TrimEnd(' ', '\t'), indent + unit, indent + after.TrimStart(' ', '\t') };
            }
            string newIndent = indent;
            if (IncreasesIndent(before, indentBased)) newIndent = indent + unit;
            else if (indentBased)
            {
                string t = before.Trim();
                foreach (string kw in DedentAfterKeywords)
                {
                    if (t == kw || t.StartsWith(kw + " ", StringComparison.Ordinal) || t.StartsWith(kw + "(", StringComparison.Ordinal))
                    {
                        string o = OutdentLine(indent + "x", tabSize, insertSpaces);
                        newIndent = o.Substring(0, o.Length - 1);
                        break;
                    }
                }
            }
            caretCol = newIndent.Length;
            return new string[] { before, newIndent + after.TrimStart(' ', '\t') };
        }

        /// <summary>The closing bracket that matches the opener ending <paramref name="beforeCaret"/> when it starts <paramref name="afterCaret"/>: Enter splits "{|}" into three lines.</summary>
        public static bool IsBetweenBrackets(string beforeCaret, string afterCaret)
        {
            string b = (beforeCaret ?? "").TrimEnd();
            string a = (afterCaret ?? "").TrimStart();
            if (b.Length == 0 || a.Length == 0) return false;
            char o = b[b.Length - 1], c = a[0];
            return (o == '{' && c == '}') || (o == '(' && c == ')') || (o == '[' && c == ']');
        }
    }
}
