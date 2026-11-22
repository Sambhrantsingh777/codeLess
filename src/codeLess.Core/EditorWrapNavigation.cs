using System;

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
    }
}
