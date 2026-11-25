using System;
using System.Collections.Generic;

namespace codeLess.Studio
{
    /// <summary>
    /// Home / End on soft-wrapped lines, shared by both editors (QA #32). VS Code (cursorHome / cursorEnd) works on
    /// the visual row first: End goes to the end of the caret's row and keeps the caret drawn there ("affinity" to the
    /// row end, since the same offset is also the start of the next row); End again goes to the end of the line.
    /// Home on a continuation row goes to the row start; at a row start (or on the first row) the usual
    /// first-non-blank / column 0 toggle applies. STRICT C# 5.
    /// </summary>
    public static class EditorWrapNavigation
    {
        /// <summary>
        /// Returns the 0-based column of the first character that is neither space (' ') nor tab ('\t').
        /// Matches VS Code's <c>getLineFirstNonWhitespaceColumn(n) || 1</c> (in 1-based columns):
        /// if the line is empty or consists solely of spaces/tabs, returns 0 so Home moves to column 0.
        /// </summary>
        public static int FirstNonWhitespaceColumn(string line)
        {
            if (string.IsNullOrEmpty(line)) return 0;
            for (int i = 0; i < line.Length; i++)
            {
                char ch = line[i];
                if (ch != ' ' && ch != '\t')
                    return i;
            }
            return 0;
        }

        /// <summary>
        /// Target column for Home on a wrapped line, or -1 when the plain (line) Home applies.
        /// <paramref name="caretAtRowEnd"/>: the caret is drawn at the end of the previous row (affinity).
        /// </summary>
        public static int Home(WrapLayout wl, int col, bool caretAtRowEnd)
        {
            if (wl == null || wl.RowCount <= 1) return -1;
            int r = wl.RowOf(col, caretAtRowEnd);
            if (r == 0 || col == wl.RowStarts[r]) return -1;
            return wl.RowStarts[r];
        }

        /// <summary>
        /// Target column for End on a wrapped line, or -1 when the plain (line) End applies. When a column is returned
        /// the caret must be drawn at the end of its row (set the affinity).
        /// </summary>
        public static int End(WrapLayout wl, int lineLength, int col, bool caretAtRowEnd)
        {
            if (wl == null || wl.RowCount <= 1) return -1;
            int r = wl.RowOf(Math.Min(col, lineLength), caretAtRowEnd);
            if (r >= wl.RowCount - 1) return -1;
            int end = wl.RowEnd(r, lineLength);
            if (col == end && caretAtRowEnd) return -1; // already at the row end: End again goes to the line end
            return end;
        }

        /// <summary>The visual row the caret is drawn on (the previous row at a soft break when it has the end affinity).</summary>
        public static int CaretRow(WrapLayout wl, int col, bool caretAtRowEnd)
        {
            if (wl == null || wl.RowCount <= 1) return 0;
            return wl.RowOf(col, caretAtRowEnd);
        }

        public struct NormalizedCaret
        {
            public int Line;
            public int Col;
            public int AnchorLine;
            public int AnchorCol;
            public bool RowEndAffinity;
        }

        private struct CaretSortEntry
        {
            public int OriginalIndex;
            public int StartLine;
            public int StartCol;
            public int EndLine;
            public int EndCol;
            public bool IsEmpty;
            public bool IsRtl;
            public bool RowEndAffinity;
        }

        private static int ComparePos(int lineA, int colA, int lineB, int colB)
        {
            if (lineA != lineB) return lineA < lineB ? -1 : 1;
            if (colA != colB) return colA < colB ? -1 : 1;
            return 0;
        }

        /// <summary>
        /// Normalizes and merges overlapping or colliding cursors/selections in <paramref name="carets"/>,
        /// matching VS Code's <c>Cursors.normalize()</c> (<c>_reduceSelections</c>).
        /// Index 0 (primary cursor) always survives when merged with any secondary cursor.
        /// </summary>
        public static List<NormalizedCaret> NormalizeAndMergeCarets(List<NormalizedCaret> carets)
        {
            if (carets == null || carets.Count <= 1)
                return carets ?? new List<NormalizedCaret>();

            List<CaretSortEntry> entries = new List<CaretSortEntry>(carets.Count);
            for (int i = 0; i < carets.Count; i++)
            {
                NormalizedCaret c = carets[i];
                bool hasSel = c.AnchorLine >= 0 && c.AnchorCol >= 0 && (c.AnchorLine != c.Line || c.AnchorCol != c.Col);
                CaretSortEntry e = new CaretSortEntry();
                e.OriginalIndex = i;
                e.RowEndAffinity = c.RowEndAffinity;
                if (!hasSel)
                {
                    e.StartLine = c.Line;
                    e.StartCol = c.Col;
                    e.EndLine = c.Line;
                    e.EndCol = c.Col;
                    e.IsEmpty = true;
                    e.IsRtl = false;
                }
                else if (ComparePos(c.AnchorLine, c.AnchorCol, c.Line, c.Col) <= 0)
                {
                    e.StartLine = c.AnchorLine;
                    e.StartCol = c.AnchorCol;
                    e.EndLine = c.Line;
                    e.EndCol = c.Col;
                    e.IsEmpty = false;
                    e.IsRtl = false;
                }
                else
                {
                    e.StartLine = c.Line;
                    e.StartCol = c.Col;
                    e.EndLine = c.AnchorLine;
                    e.EndCol = c.AnchorCol;
                    e.IsEmpty = false;
                    e.IsRtl = true;
                }
                entries.Add(e);
            }

            entries.Sort(delegate(CaretSortEntry x, CaretSortEntry y)
            {
                int cmp = ComparePos(x.StartLine, x.StartCol, y.StartLine, y.StartCol);
                if (cmp != 0) return cmp;
                cmp = ComparePos(x.EndLine, x.EndCol, y.EndLine, y.EndCol);
                if (cmp != 0) return cmp;
                return x.OriginalIndex.CompareTo(y.OriginalIndex);
            });

            for (int s = 0; s < entries.Count - 1; s++)
            {
                CaretSortEntry a = entries[s];
                CaretSortEntry o = entries[s + 1];
                int cmpStartToEnd = ComparePos(o.StartLine, o.StartCol, a.EndLine, a.EndCol);
                bool overlaps = (a.IsEmpty || o.IsEmpty) ? (cmpStartToEnd <= 0) : (cmpStartToEnd < 0);
                if (overlaps)
                {
                    int uStartLine = a.StartLine;
                    int uStartCol = a.StartCol;
                    int uEndLine = a.EndLine;
                    int uEndCol = a.EndCol;
                    if (ComparePos(o.EndLine, o.EndCol, uEndLine, uEndCol) > 0)
                    {
                        uEndLine = o.EndLine;
                        uEndCol = o.EndCol;
                    }

                    bool keepA = a.OriginalIndex < o.OriginalIndex;
                    bool keptRtl;
                    if (a.IsEmpty && !o.IsEmpty) keptRtl = o.IsRtl;
                    else if (o.IsEmpty && !a.IsEmpty) keptRtl = a.IsRtl;
                    else if (a.IsRtl == o.IsRtl) keptRtl = a.IsRtl;
                    else keptRtl = keepA ? a.IsRtl : o.IsRtl;

                    CaretSortEntry merged = new CaretSortEntry();
                    merged.OriginalIndex = keepA ? a.OriginalIndex : o.OriginalIndex;
                    merged.StartLine = uStartLine;
                    merged.StartCol = uStartCol;
                    merged.EndLine = uEndLine;
                    merged.EndCol = uEndCol;
                    merged.IsEmpty = (uStartLine == uEndLine && uStartCol == uEndCol);
                    merged.IsRtl = merged.IsEmpty ? false : keptRtl;
                    merged.RowEndAffinity = keepA ? a.RowEndAffinity : o.RowEndAffinity;

                    entries[s] = merged;
                    entries.RemoveAt(s + 1);
                    s--;
                }
            }

            entries.Sort(delegate(CaretSortEntry x, CaretSortEntry y)
            {
                return x.OriginalIndex.CompareTo(y.OriginalIndex);
            });

            List<NormalizedCaret> result = new List<NormalizedCaret>(entries.Count);
            for (int i = 0; i < entries.Count; i++)
            {
                CaretSortEntry e = entries[i];
                NormalizedCaret nc = new NormalizedCaret();
                nc.RowEndAffinity = e.RowEndAffinity;
                if (e.IsEmpty)
                {
                    nc.Line = e.StartLine;
                    nc.Col = e.StartCol;
                    nc.AnchorLine = -1;
                    nc.AnchorCol = -1;
                }
                else if (e.IsRtl)
                {
                    nc.Line = e.StartLine;
                    nc.Col = e.StartCol;
                    nc.AnchorLine = e.EndLine;
                    nc.AnchorCol = e.EndCol;
                }
                else
                {
                    nc.Line = e.EndLine;
                    nc.Col = e.EndCol;
                    nc.AnchorLine = e.StartLine;
                    nc.AnchorCol = e.StartCol;
                }
                result.Add(nc);
            }
            return result;
        }
    }
}
