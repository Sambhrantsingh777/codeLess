using System;
using System.Collections.Generic;

namespace codeLess.Studio
{
    /// <summary>A scope range from the language server (0-based, inclusive lines).</summary>
    public struct StickySymbolRange
    {
        public int StartLine;
        public int EndLine;
        public int NameLine;
        public string Name;
    }

    /// <summary>
    /// Sticky scroll: the headers of the scopes that enclose the first visible line, outermost first, at most
    /// <see cref="UiTokens.StickyScrollMaxLines"/>. Sources, in order of preference: language-server document symbols
    /// (when they still match the text), the string/comment-aware brace stack (brace languages), indentation
    /// (Python, YAML, ...), Markdown headings. STRICT C# 5.
    /// </summary>
    public static class EditorStickyScroll
    {
        /// <summary>Headers for a given first visible document line (all strictly above it).</summary>
        public static List<int> ComputeHeaders(IList<string> lines, int topLine, EditorBracketCache brackets, BracketLexProfile profile,
            IList<StickySymbolRange> symbols, int tabSize, int maxLines)
        {
            List<int> res = new List<int>();
            if (lines == null || topLine <= 0 || topLine >= lines.Count || maxLines <= 0) return res;
            if (profile == null) profile = brackets != null ? brackets.Profile : new BracketLexProfile();

            if (symbols != null && symbols.Count > 0)
            {
                List<int> fromSymbols = FromSymbols(lines, topLine, symbols);
                if (fromSymbols != null) return Cap(fromSymbols, maxLines);
            }
            if (profile.Markdown) return Cap(FromMarkdown(lines, topLine), maxLines);
            if (!profile.IndentBased && brackets != null && profile.Enabled)
            {
                BracketFrame f = brackets.GetStackAtLineStart(lines, topLine);
                List<int> heads = new List<int>();
                for (; f != null; f = f.Parent)
                {
                    if (f.Open != '{' && f.Open != '[') continue;
                    int h = HeaderLineForBrace(lines, f.Line, f.Col);
                    if (h < 0 || h >= topLine) continue;
                    if (heads.Count > 0 && heads[heads.Count - 1] == h) continue;
                    if (heads.Contains(h)) continue;
                    heads.Add(h);
                }
                heads.Reverse();
                // Allman style: when the top line is the block's own "{" line, the scope already started at the
                // header line above it (VS Code scopes start at the header), so that header sticks too
                BracketLineData topData = brackets.GetLine(lines, topLine, 0);
                if (topData != null && topData.Count > 0 && topData.IsOpener(0) && (topData.Chars[0] == '{' || topData.Chars[0] == '['))
                {
                    string tl = lines[topLine] ?? "";
                    int fnw = 0;
                    while (fnw < tl.Length && char.IsWhiteSpace(tl[fnw])) fnw++;
                    if (topData.Cols[0] == fnw)
                    {
                        int h = HeaderLineForBrace(lines, topLine, fnw);
                        if (h >= 0 && h < topLine && !heads.Contains(h)) heads.Add(h);
                    }
                }
                return Cap(heads, maxLines);
            }
            return Cap(FromIndentation(lines, topLine, tabSize), maxLines);
        }

        private static List<int> Cap(List<int> l, int max)
        {
            if (l.Count > max) l.RemoveRange(max, l.Count - max);
            return l;
        }

        /// <summary>Line shown for a block opened by a brace: the brace's own line, or (Allman style) the line above.</summary>
        public static int HeaderLineForBrace(IList<string> lines, int braceLine, int braceCol)
        {
            if (braceLine < 0 || braceLine >= lines.Count) return -1;
            string ln = lines[braceLine] ?? "";
            bool onlyBrace = true;
            for (int i = 0; i < ln.Length && i < braceCol; i++) if (!char.IsWhiteSpace(ln[i])) { onlyBrace = false; break; }
            if (!onlyBrace) return braceLine;
            for (int k = braceLine - 1; k >= 0 && k >= braceLine - 3; k--)
            {
                string prev = (lines[k] ?? "").Trim();
                if (prev.Length == 0) continue;
                if (prev.EndsWith(";", StringComparison.Ordinal) || prev.EndsWith("}", StringComparison.Ordinal) || prev.EndsWith("{", StringComparison.Ordinal)) return braceLine;
                return k;
            }
            return braceLine;
        }

        private static List<int> FromSymbols(IList<string> lines, int topLine, IList<StickySymbolRange> symbols)
        {
            List<StickySymbolRange> hits = new List<StickySymbolRange>();
            foreach (StickySymbolRange s in symbols)
            {
                if (s.NameLine < 0 || s.NameLine >= lines.Count) return null; // stale symbols
                if (!string.IsNullOrEmpty(s.Name) && (lines[s.NameLine] ?? "").IndexOf(s.Name, StringComparison.Ordinal) < 0) return null;
                if (s.StartLine < topLine && s.EndLine >= topLine && s.NameLine < topLine) hits.Add(s);
            }
            hits.Sort(delegate (StickySymbolRange a, StickySymbolRange b)
            {
                int c = a.StartLine.CompareTo(b.StartLine);
                return c != 0 ? c : b.EndLine.CompareTo(a.EndLine);
            });
            List<int> res = new List<int>();
            foreach (StickySymbolRange s in hits) if (!res.Contains(s.NameLine)) res.Add(s.NameLine);
            return res;
        }

        /// <summary>Indentation scopes: each shallower non-blank line above the top line encloses it.</summary>
        public static List<int> FromIndentation(IList<string> lines, int topLine, int tabSize)
        {
            List<int> res = new List<int>();
            int cur = -1;
            for (int i = topLine; i < lines.Count && i < topLine + 200; i++)
            {
                cur = EditorIndentGuides.IndentColumns(lines[i], tabSize);
                if (cur >= 0) break;
            }
            if (cur <= 0) return res;
            int lo = Math.Max(0, topLine - EditorIndentGuides.ScanLimit * 2);
            for (int i = topLine - 1; i >= lo; i--)
            {
                int ind = EditorIndentGuides.IndentColumns(lines[i], tabSize);
                if (ind < 0 || ind >= cur) continue;
                string t = (lines[i] ?? "").TrimStart();
                if (t.StartsWith("#", StringComparison.Ordinal) || t.StartsWith("//", StringComparison.Ordinal)) continue; // comments do not open scopes
                res.Add(i);
                cur = ind;
                if (cur == 0) break;
            }
            res.Reverse();
            return res;
        }

        /// <summary>Markdown: the heading chain above the top line (fenced code blocks are skipped).</summary>
        public static List<int> FromMarkdown(IList<string> lines, int topLine)
        {
            List<int> stack = new List<int>();
            List<int> levels = new List<int>();
            int from = Math.Max(0, topLine - 20000);
            bool inFence = false;
            for (int i = from; i < topLine; i++)
            {
                string t = (lines[i] ?? "").TrimStart();
                if (t.StartsWith("```", StringComparison.Ordinal) || t.StartsWith("~~~", StringComparison.Ordinal)) { inFence = !inFence; continue; }
                if (inFence || !t.StartsWith("#", StringComparison.Ordinal)) continue;
                int lvl = 0;
                while (lvl < t.Length && t[lvl] == '#') lvl++;
                if (lvl > 6 || (lvl < t.Length && t[lvl] != ' ' && t[lvl] != '\t')) continue;
                while (levels.Count > 0 && levels[levels.Count - 1] >= lvl) { levels.RemoveAt(levels.Count - 1); stack.RemoveAt(stack.Count - 1); }
                levels.Add(lvl);
                stack.Add(i);
            }
            return stack;
        }

        /// <summary>
        /// Final sticky lines for a viewport. The widget hides the first rows, so the scope is computed for the row just
        /// below it, iterating until stable (VS Code). <paramref name="docLineAtRow"/> maps a visual row of the
        /// viewport (0 = top) to its document line, honouring folding and word wrap; returns -1 past the end.
        /// </summary>
        public static List<int> ComputeForViewport(IList<string> lines, Func<int, int> docLineAtRow, EditorBracketCache brackets, BracketLexProfile profile,
            IList<StickySymbolRange> symbols, int tabSize, int maxLines)
        {
            List<int> result = new List<int>();
            if (lines == null || docLineAtRow == null) return result;
            int k = 0;
            for (int iter = 0; iter < maxLines + 2; iter++)
            {
                int top = docLineAtRow(k);
                if (top < 0) break;
                List<int> h = ComputeHeaders(lines, top, brackets, profile, symbols, tabSize, maxLines);
                result = h;
                if (h.Count == k) break;
                if (h.Count < k)
                {
                    // fewer scopes under a shorter widget: settle on the smaller set if it is stable there
                    int top2 = docLineAtRow(h.Count);
                    List<int> h2 = top2 < 0 ? new List<int>() : ComputeHeaders(lines, top2, brackets, profile, symbols, tabSize, maxLines);
                    result = h2.Count <= h.Count ? h2 : h;
                    break;
                }
                k = h.Count;
            }
            // never pin a line that is itself visible below the widget
            int firstUnder = docLineAtRow(result.Count);
            if (firstUnder >= 0) result.RemoveAll(delegate (int x) { return x >= firstUnder; });
            return result;
        }
    }
}
