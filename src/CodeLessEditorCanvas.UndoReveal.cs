using System;

namespace codeLess.Studio
{
    /// <summary>
    /// VS Code after Undo / Redo (Windows): the restored selection (EditorUndoReveal.cs picks it) is revealed through the
    /// stable RevealRange API — centered when it was off-screen, no scroll when already visible, animated; folds expand. Every undo / redo
    /// source (Ctrl+Z, Ctrl+Y, Ctrl+Shift+Z, Edit menu, command palette) goes through <see cref="ApplyUndoRedo"/>. C# 5.
    /// </summary>
    public partial class CodeLessVirtualCanvas
    {
        /// <summary>Undo (or redo) on the current buffer with the shared notifications; false when there was nothing to do.</summary>
        public bool ApplyUndoRedo(bool redo)
        {
            EditorTabBuffer buf = owner.CurrentBuffer;
            if (buf == null) return false;
            // the snapshot restores the carets / selections recorded with the step (VS Code, QA #29)
            if (!(redo ? buf.Redo() : buf.Undo())) return false;
            owner.NotifyBufferContentChanged();
            owner.DismissAutocomplete();
            RevealCaretAfterUndoRedo();
            owner.UpdateCursorStatusLabel();
            Invalidate();
            return true;
        }

        /// <summary>Reveals the restored selection: centered when off-screen, no scroll when visible, animated when smooth scrolling is on.</summary>
        public void RevealCaretAfterUndoRedo()
        {
            EditorTabBuffer buf = owner.CurrentBuffer;
            if (buf == null || buf.Lines == null || buf.Lines.Count == 0) return;
            buf.ClampCursor();
            int anchorLine = buf.HasSelection() ? buf.SelectionAnchorLine : buf.CursorLine;
            int anchorCol = buf.HasSelection() ? buf.SelectionAnchorCol : buf.CursorCol;
            // the caret (active end) is revealed last so it wins when the selection is taller than the viewport
            RevealRange(anchorLine, anchorCol, buf.CursorLine, buf.CursorCol, true, true);
            RevealPosition(buf.CursorLine, buf.CursorCol, true, true);
        }
    }
}
