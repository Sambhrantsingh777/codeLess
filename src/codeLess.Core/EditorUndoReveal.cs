using System;
using System.Collections.Generic;

namespace codeLess.Studio
{
    /// <summary>
    /// The text an undo / redo step changed, in line / column positions of the document before (Old*) and after (New*)
    /// the step. Start is shared; an empty range (end == start) is a pure deletion / insertion point.
    /// </summary>
    public struct EditorChangeSpan
    {
        public bool Changed;
        public int StartLine;
        public int StartCol;
        public int OldEndLine;
        public int OldEndCol;
        public int NewEndLine;
        public int NewEndCol;

        /// <summary>True when <paramref name="line"/> lies on a changed line of the new document (or the insertion point line).</summary>
        public bool NewContainsLine(int line)
        {
            return Changed && line >= StartLine && line <= Math.Max(StartLine, NewEndLine);
        }

        public bool OldContainsLine(int line)
        {
            return Changed && line >= StartLine && line <= Math.Max(StartLine, OldEndLine);
        }

        /// <summary>
        /// Minimal changed region between two line lists: common prefix / suffix of lines, then of characters on the first
        /// / last differing line. O(lines) with a reference-equality fast path (undo snapshots share unchanged strings).
        /// </summary>
        public static EditorChangeSpan Compute(IList<string> a, IList<string> b)
        {
            EditorChangeSpan s = new EditorChangeSpan();
            if (a == null || b == null) return s;
            int na = a.Count, nb = b.Count, max = Math.Min(na, nb);
            int top = 0;
            while (top < max && Same(a[top], b[top])) top++;
            if (top == na && top == nb) return s;
            int bot = 0;
            while (bot < max - top && Same(a[na - 1 - bot], b[nb - 1 - bot])) bot++;
            int aEnd = na - 1 - bot, bEnd = nb - 1 - bot;
            s.Changed = true;
            s.StartLine = top;
            if (aEnd >= top && bEnd >= top)
            {
                string a0 = a[top] ?? "", b0 = b[top] ?? "";
                int pre = 0, pmax = Math.Min(a0.Length, b0.Length);
                while (pre < pmax && a0[pre] == b0[pre]) pre++;
                string a1 = a[aEnd] ?? "", b1 = b[bEnd] ?? "";
                int suf = 0, smax = Math.Min(a1.Length, b1.Length);
                if (aEnd == top) smax = Math.Min(smax, a1.Length - pre);
                if (bEnd == top) smax = Math.Min(smax, b1.Length - pre);
                while (suf < smax && a1[a1.Length - 1 - suf] == b1[b1.Length - 1 - suf]) suf++;
                s.StartCol = pre;
                s.OldEndLine = aEnd; s.OldEndCol = a1.Length - suf;
                s.NewEndLine = bEnd; s.NewEndCol = b1.Length - suf;
            }
            else if (aEnd < top)
            {
                // whole lines inserted before old line `top`
                s.StartCol = 0;
                s.OldEndLine = top; s.OldEndCol = 0;
                s.NewEndLine = bEnd; s.NewEndCol = (b[bEnd] ?? "").Length;
            }
            else
            {
                // whole lines deleted: the new document has an insertion point at line `top`
                s.StartCol = 0;
                s.OldEndLine = aEnd; s.OldEndCol = (a[aEnd] ?? "").Length;
                s.NewEndLine = top; s.NewEndCol = 0;
                if (top >= nb && nb > 0) { s.StartLine = nb - 1; s.StartCol = (b[nb - 1] ?? "").Length; s.NewEndLine = s.StartLine; s.NewEndCol = s.StartCol; }
            }
            if (s.OldEndLine < s.StartLine || (s.OldEndLine == s.StartLine && s.OldEndCol < s.StartCol)) { s.OldEndLine = s.StartLine; s.OldEndCol = s.StartCol; }
            if (s.NewEndLine < s.StartLine || (s.NewEndLine == s.StartLine && s.NewEndCol < s.StartCol)) { s.NewEndLine = s.StartLine; s.NewEndCol = s.StartCol; }
            return s;
        }

        private static bool Same(string x, string y)
        {
            return ReferenceEquals(x, y) || string.Equals(x, y, StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// VS Code undo / redo selection: undo restores the selection from before the edit, redo the one after it, and the
    /// editor reveals it. Snapshots record the caret at the time they were taken, so the "after" caret of an edit is only
    /// right when the caret did not move between the edit and Undo, and edits that keep the caret elsewhere (Replace All,
    /// Format Document, AI edits) record a caret far from the change. A recorded caret that is not on the changed lines is
    /// therefore replaced by the change position: undo → start of the change, redo → end of the changed text. The
    /// counterpart step (pushed for redo / undo) gets the same correction, so redo lands on the edit even after the caret
    /// was moved away before undoing. C# 5.
    /// </summary>
    public partial class EditorTabBuffer
    {
        /// <summary>The region changed by the last Undo / Redo (Changed = false when it changed nothing).</summary>
        public EditorChangeSpan LastUndoRedoChange;

        /// <summary>Incremented by every Undo / Redo that changed text (views reveal the caret when it moves).</summary>
        public int UndoRedoRevision;

        internal void ApplyUndoRedoSelection(List<string> oldLines, BufferSnapshot counterpart, bool undo)
        {
            EditorChangeSpan ch = EditorChangeSpan.Compute(oldLines, Lines);
            LastUndoRedoChange = ch;
            if (!ch.Changed) return;
            UndoRedoRevision++;

            // the step pushed for the opposite direction lives in the old document's coordinates
            // (allow +/- 1 line so line duplication / insert-line-below at the caret line is recognized as touching the change)
            if (counterpart != null && !SnapshotTouches(counterpart, Math.Max(0, ch.StartLine - 1), Math.Max(ch.StartLine, ch.OldEndLine) + 1))
            {
                if (undo) { counterpart.CursorLine = ch.OldEndLine; counterpart.CursorCol = ch.OldEndCol; }   // redo: after the edit
                else { counterpart.CursorLine = ch.StartLine; counterpart.CursorCol = ch.StartCol; }        // undo again: before it
                counterpart.AnchorLine = -1;
                counterpart.AnchorCol = -1;
                counterpart.ExtraCarets = null;
            }

            if (!CaretsTouch(Math.Max(0, ch.StartLine - 1), Math.Max(ch.StartLine, ch.NewEndLine) + 1))
            {
                if (undo) { CursorLine = ch.StartLine; CursorCol = ch.StartCol; }
                else { CursorLine = ch.NewEndLine; CursorCol = ch.NewEndCol; }
                ClearSelection();
                if (ExtraCarets != null) ExtraCarets.Clear();
                ClampCursor();
            }
        }
        /// <summary>A caret (or its selection) of the snapshot lies on lines lo..hi.</summary>
        private static bool SnapshotTouches(BufferSnapshot s, int lo, int hi)
        {
            if (Touches(s.CursorLine, s.AnchorLine, lo, hi)) return true;
            if (s.ExtraCarets != null)
                foreach (EditorCaret c in s.ExtraCarets)
                    if (c != null && Touches(c.Line, c.AnchorLine, lo, hi)) return true;
            return false;
        }

        /// <summary>A current caret (primary or extra, or its selection) lies on lines lo..hi.</summary>
        private bool CaretsTouch(int lo, int hi)
        {
            if (Touches(CursorLine, HasSelection() ? SelectionAnchorLine : -1, lo, hi)) return true;
            if (ExtraCarets != null)
                foreach (EditorCaret c in ExtraCarets)
                    if (c != null && Touches(c.Line, c.AnchorLine, lo, hi)) return true;
            return false;
        }

        private static bool Touches(int line, int anchorLine, int lo, int hi)
        {
            int a = anchorLine >= 0 ? anchorLine : line;
            return Math.Max(line, a) >= lo && Math.Min(line, a) <= hi;
        }
    }
}
