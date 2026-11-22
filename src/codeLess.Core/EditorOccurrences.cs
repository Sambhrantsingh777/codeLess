using System;
using System.Collections.Generic;

namespace codeLess.Studio
{
    public struct OccurrenceMatch
    {
        public int Line;
        public int Col;
        public int Length;
        public OccurrenceMatch(int line, int col, int length) { Line = line; Col = col; Length = length; }
    }

    /// <summary>
    /// Word / selection occurrence helpers: the word under the caret, whole-word matching (identifier boundaries,
    /// case-sensitive like VS Code's word highlighter), visible-range search for highlighting (no whole-file work per
    /// keystroke) and whole-document search for "Change All Occurrences". STRICT C# 5.
    /// </summary>
    public static class EditorOccurrences
    {
        /// <summary>Longest line scanned for highlights (minified lines are skipped).</summary>
        public const int MaxLineLengthForHighlight = 10000;
        /// <summary>Most matches returned by a document search.</summary>
        public const int MaxDocumentMatches = 10000;

        public static bool IsWordChar(char c)
        {
            return char.IsLetterOrDigit(c) || c == '_' || c == '$' || char.GetUnicodeCategory(c) == System.Globalization.UnicodeCategory.NonSpacingMark;
        }

        /// <summary>The identifier at / just before the caret, or null (caret on whitespace or punctuation).</summary>
        public static string GetWordAt(string line, int col, out int start, out int end)
        {
            start = end = -1;
            if (string.IsNullOrEmpty(line)) return null;
            int c = Math.Max(0, Math.Min(line.Length, col));
            int s = c, e = c;
            if ((c >= line.Length || !IsWordChar(line[c])) && c > 0 && IsWordChar(line[c - 1])) { s = c - 1; e = c; }
            else if (c >= line.Length || !IsWordChar(line[c])) return null;
            while (s > 0 && IsWordChar(line[s - 1])) s--;
            while (e < line.Length && IsWordChar(line[e])) e++;
            if (e <= s) return null;
            string w = line.Substring(s, e - s);
            bool hasLetter = false;
            foreach (char ch in w) if (char.IsLetter(ch) || ch == '_' || ch == '$') { hasLetter = true; break; }
            if (!hasLetter) return null; // pure numbers are not highlighted
            start = s;
            end = e;
            return w;
        }

        public static bool IsWholeWordAt(string line, int idx, int len)
        {
            if (idx > 0 && IsWordChar(line[idx - 1])) return false;
            int after = idx + len;
            if (after < line.Length && IsWordChar(line[after])) return false;
            return true;
        }

        /// <summary>All occurrences of <paramref name="needle"/> on one line.</summary>
        public static void FindInLine(string line, int lineIndex, string needle, bool wholeWord, bool matchCase, List<OccurrenceMatch> into)
        {
            if (string.IsNullOrEmpty(line) || string.IsNullOrEmpty(needle) || line.Length < needle.Length) return;
            StringComparison cmp = matchCase ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
            int pos = 0;
            while (pos <= line.Length - needle.Length)
            {
                int idx = line.IndexOf(needle, pos, cmp);
                if (idx < 0) break;
                if (!wholeWord || IsWholeWordAt(line, idx, needle.Length))
                {
                    into.Add(new OccurrenceMatch(lineIndex, idx, needle.Length));
                    pos = idx + needle.Length;
                }
                else pos = idx + 1;
            }
        }

        /// <summary>Occurrences within [firstLine, lastLine] only (used for painting the viewport).</summary>
        public static List<OccurrenceMatch> FindInRange(IList<string> lines, string needle, bool wholeWord, bool matchCase, int firstLine, int lastLine)
        {
            List<OccurrenceMatch> res = new List<OccurrenceMatch>();
            if (lines == null || string.IsNullOrEmpty(needle)) return res;
            int a = Math.Max(0, firstLine), b = Math.Min(lines.Count - 1, lastLine);
            for (int l = a; l <= b; l++)
            {
                string ln = lines[l];
                if (ln == null || ln.Length > MaxLineLengthForHighlight) continue;
                FindInLine(ln, l, needle, wholeWord, matchCase, res);
            }
            return res;
        }

        /// <summary>Whole-document search (capped at <see cref="MaxDocumentMatches"/>).</summary>
        public static List<OccurrenceMatch> FindAll(IList<string> lines, string needle, bool wholeWord, bool matchCase)
        {
            List<OccurrenceMatch> res = new List<OccurrenceMatch>();
            if (lines == null || string.IsNullOrEmpty(needle)) return res;
            for (int l = 0; l < lines.Count && res.Count < MaxDocumentMatches; l++)
                FindInLine(lines[l], l, needle, wholeWord, matchCase, res);
            if (res.Count > MaxDocumentMatches) res.RemoveRange(MaxDocumentMatches, res.Count - MaxDocumentMatches);
            return res;
        }

        /// <summary>
        /// What to highlight for a caret / selection: the selected text when the selection is on one line (substring
        /// match, VS Code "selection highlight"), else the word under the caret (whole word). Null = nothing.
        /// </summary>
        public static string GetHighlightTarget(IList<string> lines, int caretLine, int caretCol, bool hasSelection, int selStartLine, int selStartCol, int selEndLine, int selEndCol, out bool wholeWord)
        {
            wholeWord = true;
            if (lines == null || caretLine < 0 || caretLine >= lines.Count) return null;
            if (hasSelection)
            {
                if (selStartLine != selEndLine || selStartLine < 0 || selStartLine >= lines.Count) return null;
                string ln = lines[selStartLine] ?? "";
                int a = Math.Max(0, Math.Min(ln.Length, Math.Min(selStartCol, selEndCol)));
                int b = Math.Max(0, Math.Min(ln.Length, Math.Max(selStartCol, selEndCol)));
                if (b - a < 1 || b - a > 200) return null;
                string sel = ln.Substring(a, b - a);
                if (sel.Trim().Length == 0) return null;
                wholeWord = false;
                return sel;
            }
            int s, e;
            return GetWordAt(lines[caretLine], caretCol, out s, out e);
        }
    }
}
