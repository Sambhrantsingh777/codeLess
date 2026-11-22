using System.Collections.Generic;
using System.Linq;
using codeLess.Studio;
using Xunit;

namespace codeLess.Avalonia.Tests
{
    /// <summary>
    /// Shared (WinForms + Avalonia) undo / redo selection: the changed span and the caret chosen for edits whose recorded
    /// caret is elsewhere (Replace All, Format Document, AI edits) or when the caret moved before undoing.
    /// </summary>
    public class EditorUndoRevealCoreTests
    {
        private static EditorTabBuffer Buffer(int lines)
        {
            var b = new EditorTabBuffer { FilePath = "/tmp/undo-reveal.txt", FileName = "undo-reveal.txt" };
            b.Lines = Enumerable.Range(0, lines).Select(i => "line " + i).ToList();
            return b;
        }

        [Fact]
        public void ChangeSpan_CoversInsertDeleteSplitAndJoin()
        {
            var s = EditorChangeSpan.Compute(new List<string> { "abcd" }, new List<string> { "abXYcd" });
            Assert.True(s.Changed);
            Assert.Equal((0, 2, 0, 2, 0, 4), (s.StartLine, s.StartCol, s.OldEndLine, s.OldEndCol, s.NewEndLine, s.NewEndCol));

            s = EditorChangeSpan.Compute(new List<string> { "abcd" }, new List<string> { "ab", "cd" }); // Enter
            Assert.Equal((0, 2, 0, 2, 1, 0), (s.StartLine, s.StartCol, s.OldEndLine, s.OldEndCol, s.NewEndLine, s.NewEndCol));

            s = EditorChangeSpan.Compute(new List<string> { "x", "y" }, new List<string> { "x", "NEW", "y" }); // line inserted
            Assert.Equal(1, s.StartLine);
            Assert.Equal((1, 3), (s.NewEndLine, s.NewEndCol));

            s = EditorChangeSpan.Compute(new List<string> { "x", "DEL", "y" }, new List<string> { "x", "y" }); // line deleted
            Assert.Equal(1, s.StartLine);
            Assert.Equal((1, 3), (s.OldEndLine, s.OldEndCol));
            Assert.Equal((1, 0), (s.NewEndLine, s.NewEndCol));

            s = EditorChangeSpan.Compute(new List<string> { "a", "b", "c" }, new List<string> { "a", "b" }); // last line deleted
            Assert.True(s.Changed);
            Assert.Equal(1, s.StartLine);
            Assert.Equal(1, s.StartCol);

            Assert.False(EditorChangeSpan.Compute(new List<string> { "same" }, new List<string> { "same" }).Changed);
        }

        [Fact]
        public void ReplaceAllStyleEdit_WithTheCaretElsewhere_UndoAndRedoLandOnTheChange()
        {
            var b = Buffer(1000);
            b.CursorLine = 0; b.CursorCol = 0;
            b.PushUndoSnapshot();                    // Replace All / Format Document: one undo step, caret stays at the top
            for (int i = 600; i <= 700; i += 50) b.Lines[i] = "LINE " + i;
            b.MarkLinesChanged();

            Assert.True(b.Undo());
            Assert.Equal("line 600", b.Lines[600]);
            Assert.Equal(600, b.CursorLine);         // recorded caret (line 0) is not on the change: start of the change
            Assert.Equal(0, b.CursorCol);
            Assert.True(b.LastUndoRedoChange.Changed);
            Assert.Equal(700, b.LastUndoRedoChange.NewEndLine);

            b.CursorLine = 3; b.CursorCol = 0;
            Assert.True(b.Redo());
            Assert.Equal("LINE 700", b.Lines[700]);
            Assert.InRange(b.CursorLine, 600, 700);  // after the change
            Assert.Equal(700, b.CursorLine);
            Assert.Equal(4, b.CursorCol);            // end of the changed text on its last line ("LINE" vs "line")
        }

        [Fact]
        public void TypingUndo_KeepsTheRecordedSelection_WhenItIsOnTheChange()
        {
            var b = Buffer(50);
            b.CursorLine = 20; b.CursorCol = 2;
            b.SelectionAnchorLine = 20; b.SelectionAnchorCol = 0;
            b.PushUndoSnapshot();
            b.Lines[20] = "Xne 20";
            b.CursorCol = 1; b.ClearSelection();
            b.MarkLinesChanged();
            Assert.True(b.Undo());
            Assert.Equal(20, b.CursorLine);
            Assert.Equal(2, b.CursorCol);
            Assert.Equal("li", b.GetSelectedText());
            Assert.True(b.Redo());
            Assert.Equal(1, b.CursorCol);            // caret recorded at undo time (it had not moved)
            Assert.False(b.HasSelection());
        }
    }
}
