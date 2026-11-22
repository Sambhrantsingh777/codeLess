using System;
using codeLess.Studio;

namespace codeLess.AvaloniaApp
{
    /// <summary>
    /// VS Code after Undo / Redo: the restored selection (EditorUndoReveal.cs picks it) is revealed — centered when it was
    /// off-screen, no scroll when it is already visible (VerticalRevealType.CenterIfOutsideViewport), animated when smooth
    /// scrolling is on; a collapsed region hiding it is expanded. Uses the stable RevealRange API.
    /// </summary>
    public partial class AvaloniaVirtualEditorCanvas
    {
        internal void RevealCaretAfterUndoRedo(EditorTabBuffer b)
        {
            if (b == null || b.Lines == null || b.Lines.Count == 0) return;
            b.ClampCursor();
            int anchorLine = b.HasSelection() ? b.SelectionAnchorLine : b.CursorLine;
            int anchorCol = b.HasSelection() ? b.SelectionAnchorCol : b.CursorCol;
            // the caret (active end) is revealed last so it wins when the selection is taller than the viewport
            RevealRange(anchorLine, anchorCol, b.CursorLine, b.CursorCol, animate: true, center: true);
            RevealPosition(b.CursorLine, b.CursorCol, animate: true, center: true);
        }
    }
}
