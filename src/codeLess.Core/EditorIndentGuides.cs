using System;
using System.Collections.Generic;

namespace codeLess.Studio
{
    /// <summary>The active indent guide (VS Code highlights the guide of the block containing the caret).</summary>
    public struct ActiveIndentGuide
    {
        /// <summary>Visual column of the guide, -1 when there is none.</summary>
        public int Column;
        public int StartLine;
        public int EndLine;
        public bool IsValid { get { return Column >= 0 && EndLine >= StartLine; } }
        public static ActiveIndentGuide None { get { ActiveIndentGuide g = new ActiveIndentGuide(); g.Column = -1; g.StartLine = 0; g.EndLine = -1; return g; } }
    }

    /// <summary>
    /// Indent guide geometry (VS Code semantics): a line indented by N columns shows guides at columns 0, t, 2t, ...
    /// below N (t = indent size); blank lines take their guides from the surrounding lines; the active guide is the
    /// one of the block that contains the caret. Bounded scans, no whole-file work. STRICT C# 5.
    /// </summary>
    public static class EditorIndentGuides
    {
        /// <summary>Max lines scanned up/down for blank-line indents and the active block.</summary>
        public const int ScanLimit = 2000;

        /// <summary>Visual width of the leading whitespace; -1 for a blank (whitespace-only) line.</summary>
        public static int IndentColumns(string line, int tabSize)
        {
            if (string.IsNullOrEmpty(line)) return -1;
            int t = Math.Max(1, tabSize);
            int v = 0;
            for (int i = 0; i < line.Length; i++)
            {
                char c = line[i];
                if (c == ' ') v++;
                else if (c == '\t') v += t - (v % t);
                else return v;
            }
            return -1;
        }

        /// <summary>
        /// Indent used for guides: the line's own indent, or for blank lines VS Code's rule (between a shallower and a
        /// deeper block the blank line continues the deeper guides).
        /// </summary>
        public static int GuideIndent(IList<string> lines, int line, int tabSize, bool offSide)
        {
            if (lines == null || line < 0 || line >= lines.Count) return 0;
            int own = IndentColumns(lines[line], tabSize);
            if (own >= 0) return own;
            int above = -1, below = -1;
            int lo = Math.Max(0, line - ScanLimit), hi = Math.Min(lines.Count - 1, line + ScanLimit);
            for (int i = line - 1; i >= lo; i--) { above = IndentColumns(lines[i], tabSize); if (above >= 0) break; }
            for (int i = line + 1; i <= hi; i++) { below = IndentColumns(lines[i], tabSize); if (below >= 0) break; }
            if (above < 0 || below < 0) return 0;
            int t = Math.Max(1, tabSize);
            if (above < below) return Math.Min(below, above + t);
            if (above == below) return below;
            return offSide ? below : Math.Min(above, below + t);
        }

        /// <summary>Guide columns for a line (multiples of the indent size strictly below its guide indent).</summary>
        public static List<int> GuideColumns(IList<string> lines, int line, int tabSize, bool offSide)
        {
            List<int> cols = new List<int>();
            int indent = GuideIndent(lines, line, tabSize, offSide);
            int t = Math.Max(1, tabSize);
            for (int c = 0; c < indent; c += t) cols.Add(c);
            return cols;
        }

        /// <summary>The guide highlighted for a caret on <paramref name="caretLine"/>.</summary>
        public static ActiveIndentGuide FindActive(IList<string> lines, int caretLine, int tabSize, bool offSide)
        {
            ActiveIndentGuide g = ActiveIndentGuide.None;
            if (lines == null || caretLine < 0 || caretLine >= lines.Count) return g;
            int t = Math.Max(1, tabSize);
            int ci = GuideIndent(lines, caretLine, t, offSide);
            int lo = Math.Max(0, caretLine - ScanLimit), hi = Math.Min(lines.Count - 1, caretLine + ScanLimit);

            // caret on a block header: the next non-blank line is deeper -> the header's body is active
            int next = -1;
            for (int i = caretLine + 1; i <= hi; i++) { int ind = IndentColumns(lines[i], t); if (ind >= 0) { next = ind; break; } }
            bool ownBlank = IndentColumns(lines[caretLine], t) < 0;
            if (!ownBlank && next > ci)
            {
                int end = caretLine;
                for (int i = caretLine + 1; i <= hi; i++)
                {
                    int ind = IndentColumns(lines[i], t);
                    if (ind < 0) continue;
                    if (ind > ci) end = i; else break;
                }
                g.Column = ci;
                g.StartLine = caretLine + 1;
                g.EndLine = end;
                return g;
            }
            if (ci <= 0) return g;
            int guideCol = ((ci - 1) / t) * t;
            int start = caretLine, stop = caretLine;
            for (int i = caretLine - 1; i >= lo; i--)
            {
                int ind = IndentColumns(lines[i], t);
                if (ind < 0) { start = i; continue; }
                if (ind > guideCol) start = i; else break;
            }
            for (int i = caretLine + 1; i <= hi; i++)
            {
                int ind = IndentColumns(lines[i], t);
                if (ind < 0) { stop = i; continue; }
                if (ind > guideCol) stop = i; else break;
            }
            // trailing blank lines of a block do not belong to it
            while (stop > caretLine && IndentColumns(lines[stop], t) < 0) stop--;
            while (start < caretLine && IndentColumns(lines[start], t) < 0) start++;
            g.Column = guideCol;
            g.StartLine = start;
            g.EndLine = stop;
            return g;
        }
    }
}
