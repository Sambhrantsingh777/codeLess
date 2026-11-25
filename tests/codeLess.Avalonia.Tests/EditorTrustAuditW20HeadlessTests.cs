using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Avalonia;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using codeLess.AvaloniaApp;
using codeLess.Core;
using codeLess.Studio;
using Xunit;

namespace codeLess.Avalonia.Tests
{
    /// <summary>
    /// W20 Trust Audit — Area A (`editor-core`): 44 concrete VS Code reference cases (A01..A44)
    /// across all 8 sub-areas (typing/auto-closing/auto-indent, Tab/Shift+Tab/Backspace/deleteWord/deleteLines,
    /// undo/redo grouping & caret restore, cut/copy/paste & multi-cursor paste distribution,
    /// multi-cursor/line selection/move/copy line, comment toggle & find/replace,
    /// Go to Line & word/subword navigation, and scroll/reveal/IME/emoji/CRLF/long lines/large files).
    /// Driven through real key, mouse, and command dispatch across macOS and non-mac keymaps.
    /// </summary>
    [Collection("SequentialWorkbench")]
    public class EditorTrustAuditW20HeadlessTests : IDisposable
    {
        private readonly string tempDir;
        private readonly List<MainWindow> windows = new List<MainWindow>();
        private readonly bool? savedMacOverride = MacKeymap.ForceMacKeymapOverride;
        private readonly bool savedSmoothScroll = EditorScrollOptions.SmoothScrolling;
        private readonly bool savedSmoothCaret = EditorScrollOptions.SmoothCaretAnimation;
        private readonly int savedTabSize = AvaloniaVirtualEditorCanvas.DefaultDisplayTabSize;
        private readonly bool savedInsertSpaces = AvaloniaVirtualEditorCanvas.InsertSpacesSetting;
        private readonly int savedSurroundingLines = EditorNicetyOptions.CursorSurroundingLines;
        private readonly string savedWordSeparators = EditorWordNavigation.WordSeparators;

        public EditorTrustAuditW20HeadlessTests()
        {
            tempDir = Path.Combine(Path.GetTempPath(), "codeless_w20_editor_" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(tempDir);
            EditorScrollOptions.SmoothScrolling = false;
            EditorScrollOptions.SmoothCaretAnimation = false;
            AvaloniaVirtualEditorCanvas.DefaultDisplayTabSize = 4;
            AvaloniaVirtualEditorCanvas.InsertSpacesSetting = true;
            EditorNicetyOptions.CursorSurroundingLines = 0;
            EditorWordNavigation.WordSeparators = EditorWordNavigation.DefaultWordSeparators;
        }

        public void Dispose()
        {
            MacKeymap.ForceMacKeymapOverride = savedMacOverride;
            EditorScrollOptions.SmoothScrolling = savedSmoothScroll;
            EditorScrollOptions.SmoothCaretAnimation = savedSmoothCaret;
            AvaloniaVirtualEditorCanvas.DefaultDisplayTabSize = savedTabSize;
            AvaloniaVirtualEditorCanvas.InsertSpacesSetting = savedInsertSpaces;
            EditorNicetyOptions.CursorSurroundingLines = savedSurroundingLines;
            EditorWordNavigation.WordSeparators = savedWordSeparators;
            foreach (var w in windows)
            {
                try { w.SuppressUnsavedPrompts = true; w.Close(); } catch { }
            }
            windows.Clear();
            Dispatcher.UIThread.RunJobs();
            try { if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true); } catch { }
        }

        private (MainWindow win, EditorTabBuffer tab, AvaloniaVirtualEditorCanvas canvas) OpenEditor(
            string fileName,
            string content,
            double width = 900,
            double height = 600)
        {
            string path = Path.Combine(tempDir, fileName);
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            File.WriteAllText(path, content);
            var w = new MainWindow(tempDir) { Width = 1280, Height = 800, SuppressUnsavedPrompts = true };
            windows.Add(w);
            w.Show();
            var tab = w.OpenFileInTab(path, preview: false);
            var canvas = w.PrimaryEditor.Canvas;
            canvas.FontSizePx = 13;
            RenderCanvas(canvas, width, height);
            Dispatcher.UIThread.RunJobs();
            return (w, tab, canvas);
        }

        private static void RenderCanvas(AvaloniaVirtualEditorCanvas canvas, double w = 900, double h = 600)
        {
            canvas.Measure(new Size(w, h));
            canvas.Arrange(new Rect(0, 0, w, h));
            using (var bmp = new RenderTargetBitmap(new PixelSize(10, 10)))
            using (var ctx = bmp.CreateDrawingContext())
            {
                canvas.Render(ctx);
            }
        }

        private static void TypeText(AvaloniaVirtualEditorCanvas canvas, string text)
        {
            foreach (char ch in text)
            {
                canvas.RaiseEvent(new TextInputEventArgs
                {
                    RoutedEvent = InputElement.TextInputEvent,
                    Text = ch.ToString()
                });
            }
        }

        private static void PressKey(AvaloniaVirtualEditorCanvas canvas, Key key, KeyModifiers modifiers = KeyModifiers.None)
        {
            canvas.RaiseEvent(new KeyEventArgs
            {
                RoutedEvent = InputElement.KeyDownEvent,
                Key = key,
                KeyModifiers = modifiers
            });
        }

        // =====================================================================
        // Sub-Area 1: Typing, Auto-Closing Pairs, Overtype & Auto-Indent (A01-A06)
        // =====================================================================

        [AvaloniaFact]
        public void A01_A02_A03_AutoClosing_Overtype_And_AutoSurround_MatchVsCode()
        {
            var (_, tab, canvas) = OpenEditor("autoclose.cs", "call\nword\nsurround_me\n");

            // A01: Auto-close '(' at EOL -> "call(|)"
            tab.CursorLine = 0;
            tab.CursorCol = 4;
            TypeText(canvas, "(");
            Assert.Equal("call()", tab.Lines[0]);
            Assert.Equal(5, tab.CursorCol);

            // A02: Overtype ')' skips over auto-inserted ')' without duplicating it
            TypeText(canvas, ")");
            Assert.Equal("call()", tab.Lines[0]);
            Assert.Equal(6, tab.CursorCol);

            // A01: Apostrophe after word character ("don't") does NOT auto-close
            tab.Lines[1] = "don";
            tab.CursorLine = 1;
            tab.CursorCol = 3;
            TypeText(canvas, "'");
            Assert.Equal("don'", tab.Lines[1]);
            Assert.Equal(4, tab.CursorCol);

            // A03: Auto-surround selection with quotes and brackets, keeping wrapped text selected
            tab.CursorLine = 2;
            tab.SelectionStartLine = 2;
            tab.SelectionStartCol = 0;
            tab.CursorCol = 11;
            TypeText(canvas, "\"");
            Assert.Equal("\"surround_me\"", tab.Lines[2]);
            Assert.True(tab.HasSelection());
            Assert.Equal("surround_me", tab.GetSelectedText());
        }

        [AvaloniaFact]
        public void A04_A05_A06_AutoIndent_BracketSplit_PythonColonDedent_And_ClosingBraceReindent()
        {
            // A04 + A06: C# '{|}' Enter split and '}' closing re-indent
            var (_, csTab, csCanvas) = OpenEditor("indent.cs", "    if (ok) {}\n    if (ok)\n    {\n        \n");
            csTab.CursorLine = 0;
            csTab.CursorCol = 13; // inside "{|}"
            PressKey(csCanvas, Key.Enter);
            Assert.Equal("    if (ok) {", csTab.Lines[0]);
            Assert.Equal("        ", csTab.Lines[1]);
            Assert.Equal("    }", csTab.Lines[2]);
            Assert.Equal(1, csTab.CursorLine);
            Assert.Equal(8, csTab.CursorCol);

            // A06: Typing '}' on indented blank line re-indents to opener's indentation (4 spaces)
            csTab.CursorLine = 5;
            csTab.CursorCol = 8;
            TypeText(csCanvas, "}");
            Assert.Equal("    }", csTab.Lines[5]);
            Assert.Equal(5, csTab.CursorCol);

            // A05: Python ':' indents and 'return' dedents on Enter
            var (_, pyTab, pyCanvas) = OpenEditor("sample.py", "def compute(x):\n");
            pyTab.CursorLine = 0;
            pyTab.CursorCol = pyTab.Lines[0].Length;
            PressKey(pyCanvas, Key.Enter);
            Assert.Equal(1, pyTab.CursorLine);
            Assert.Equal(4, pyTab.CursorCol);
            Assert.Equal("    ", pyTab.Lines[1]);

            TypeText(pyCanvas, "return x");
            PressKey(pyCanvas, Key.Enter);
            Assert.Equal(2, pyTab.CursorLine);
            Assert.Equal(0, pyTab.CursorCol);
            Assert.Equal("", pyTab.Lines[2]);
        }

        // =====================================================================
        // Sub-Area 2: Tab/Shift+Tab, Backspace, Delete Word & Delete Line (A07-A12)
        // =====================================================================

        [AvaloniaFact]
        public void A07_A08_TabIndentOutdent_BlankLineSkip_Col0Exclusion_And_BackspaceTabStop()
        {
            var (_, tab, canvas) = OpenEditor("tab.cs", "alpha\n\nbeta\ngamma");

            // A07: Multi-line selection ending at col 0 of line 3 ("gamma") indents lines 0..2, skipping blank line 1
            tab.SelectionStartLine = 0;
            tab.SelectionStartCol = 0;
            tab.CursorLine = 3;
            tab.CursorCol = 0;
            PressKey(canvas, Key.Tab);
            Assert.Equal("    alpha", tab.Lines[0]);
            Assert.Equal("", tab.Lines[1]);
            Assert.Equal("    beta", tab.Lines[2]);
            Assert.Equal("gamma", tab.Lines[3]);

            // Shift+Tab outdents back
            PressKey(canvas, Key.Tab, KeyModifiers.Shift);
            Assert.Equal("alpha", tab.Lines[0]);
            Assert.Equal("beta", tab.Lines[2]);

            // A08: Backspace inside leading indentation deletes back to previous tab stop (4 spaces)
            tab.Lines[0] = "        code";
            tab.ClearSelection();
            tab.CursorLine = 0;
            tab.CursorCol = 8;
            PressKey(canvas, Key.Back);
            Assert.Equal("    code", tab.Lines[0]);
            Assert.Equal(4, tab.CursorCol);

            // A08: Backspace inside auto-closed pair '(|)' deletes both '(' and ')'
            tab.CursorCol = tab.Lines[0].Length;
            TypeText(canvas, "(");
            Assert.Equal("    code()", tab.Lines[0]);
            PressKey(canvas, Key.Back);
            Assert.Equal("    code", tab.Lines[0]);
        }

        [AvaloniaFact]
        public void A09_A10_A11_A12_DeleteWord_DeleteSubword_DeleteToBoundary_And_DeleteLines_MultiCursor()
        {
            var (_, tab, canvas) = OpenEditor("delete.cs", "    firstWord secondWord\n    thirdWord fourthWord\n    camelCaseIdentifier\n    lineZero\n    lineOne\n    lineTwo");

            // A09: DeleteWordLeft & DeleteWordRight across multiple cursors
            MacKeymap.ForceMacKeymapOverride = true;
            tab.CursorLine = 0;
            tab.CursorCol = tab.Lines[0].Length;
            canvas.ExtraCursors.Clear();
            canvas.ExtraCursors.Add(new ExtraCursorCaret { Line = 1, Col = tab.Lines[1].Length, AnchorLine = -1, AnchorCol = -1 });
            PressKey(canvas, Key.Back, KeyModifiers.Alt);
            Assert.Equal("    firstWord ", tab.Lines[0]);
            Assert.Equal("    thirdWord ", tab.Lines[1]);

            tab.CursorLine = 0;
            tab.CursorCol = 4;
            canvas.ExtraCursors.Clear();
            canvas.ExtraCursors.Add(new ExtraCursorCaret { Line = 1, Col = 4, AnchorLine = -1, AnchorCol = -1 });
            PressKey(canvas, Key.Delete, KeyModifiers.Alt);
            Assert.Equal("     ", tab.Lines[0]);
            Assert.Equal("     ", tab.Lines[1]);

            // A10: DeleteSubwordLeft / DeleteSubwordRight on camelCaseIdentifier
            canvas.ExtraCursors.Clear();
            tab.CursorLine = 2;
            tab.CursorCol = tab.Lines[2].Length;
            PressKey(canvas, Key.Back, KeyModifiers.Control | KeyModifiers.Alt);
            Assert.Equal("    camelCase", tab.Lines[2]);
            tab.CursorCol = 4;
            PressKey(canvas, Key.Delete, KeyModifiers.Control | KeyModifiers.Alt);
            Assert.Equal("    Case", tab.Lines[2]);

            // A11: DeleteToLineBoundary (Cmd+Backspace) across multiple cursors keeps indentation on first press, then deletes to col 0
            tab.Lines[0] = "    alpha beta";
            tab.Lines[1] = "    gamma delta";
            tab.CursorLine = 0;
            tab.CursorCol = tab.Lines[0].Length;
            canvas.ExtraCursors.Clear();
            canvas.ExtraCursors.Add(new ExtraCursorCaret { Line = 1, Col = tab.Lines[1].Length, AnchorLine = -1, AnchorCol = -1 });
            PressKey(canvas, Key.Back, KeyModifiers.Meta);
            Assert.Equal("    ", tab.Lines[0]);
            Assert.Equal("    ", tab.Lines[1]);
            Assert.Equal(4, tab.CursorCol);
            Assert.Equal(4, canvas.ExtraCursors[0].Col);

            // Second Cmd+Backspace deletes the indentation to col 0
            PressKey(canvas, Key.Back, KeyModifiers.Meta);
            Assert.Equal("", tab.Lines[0]);
            Assert.Equal("", tab.Lines[1]);
            Assert.Equal(0, tab.CursorCol);
            Assert.Equal(0, canvas.ExtraCursors[0].Col);

            // A12: DeleteActiveLine (Cmd+Shift+K) with multiple cursors deletes all touched lines (lines 3 and 5)
            canvas.ExtraCursors.Clear();
            tab.CursorLine = 3; // "    lineZero"
            tab.CursorCol = 6;
            canvas.ExtraCursors.Add(new ExtraCursorCaret { Line = 5, Col = 6, AnchorLine = -1, AnchorCol = -1 }); // "    lineTwo"
            PressKey(canvas, Key.K, KeyModifiers.Meta | KeyModifiers.Shift);
            Assert.Equal(4, tab.Lines.Count);
            Assert.Equal("    lineOne", tab.Lines[3]);
            Assert.Equal(3, tab.CursorLine);
            Assert.Equal(6, tab.CursorCol);
        }

        // =====================================================================
        // Sub-Area 3: Undo/Redo Grouping & Caret/Selection Restore (A13-A17)
        // =====================================================================

        [AvaloniaFact]
        public void A13_A14_A15_A16_A17_UndoRedo_Coalescing_MultiCaretRestore_DirtyTracking_And_NoOpCommentUndo()
        {
            var (_, tab, canvas) = OpenEditor("undo.cs", "start\nsecond\n");
            MacKeymap.ForceMacKeymapOverride = true;

            // A13 + A16: Consecutive typed characters coalesce into one undo step; cursor move breaks undo group; Undo back to saved clears IsDirty
            tab.CursorLine = 0;
            tab.CursorCol = 5;
            TypeText(canvas, "abc");
            Assert.True(tab.IsDirty);
            Assert.Equal("startabc", tab.Lines[0]);

            // Move cursor left and type 'X' -> new undo group
            PressKey(canvas, Key.Left);
            TypeText(canvas, "X");
            Assert.Equal("startabXc", tab.Lines[0]);

            // First Cmd+Z undoes only 'X' and restores caret to col 7
            PressKey(canvas, Key.Z, KeyModifiers.Meta);
            Assert.Equal("startabc", tab.Lines[0]);
            Assert.Equal(7, tab.CursorCol);
            Assert.True(tab.IsDirty);

            // Second Cmd+Z undoes "abc", restores caret to col 5, and clears IsDirty!
            PressKey(canvas, Key.Z, KeyModifiers.Meta);
            Assert.Equal("start", tab.Lines[0]);
            Assert.Equal(5, tab.CursorCol);
            Assert.False(tab.IsDirty);

            // Redo (Cmd+Shift+Z) restores "startabc" and marks dirty again
            PressKey(canvas, Key.Z, KeyModifiers.Meta | KeyModifiers.Shift);
            Assert.Equal("startabc", tab.Lines[0]);
            Assert.True(tab.IsDirty);

            // Undo back to clean state
            PressKey(canvas, Key.Z, KeyModifiers.Meta);
            Assert.False(tab.IsDirty);

            // A14 + A15: Multi-cursor edit undo/redo restores all carets and selections
            tab.SelectionStartLine = 0;
            tab.SelectionStartCol = 0;
            tab.CursorLine = 0;
            tab.CursorCol = 5;
            canvas.ExtraCursors.Clear();
            canvas.ExtraCursors.Add(new ExtraCursorCaret { Line = 1, AnchorLine = 1, AnchorCol = 0, Col = 6 });
            TypeText(canvas, "Z");
            Assert.Equal("Z", tab.Lines[0]);
            Assert.Equal("Z", tab.Lines[1]);

            PressKey(canvas, Key.Z, KeyModifiers.Meta);
            Assert.Equal("start", tab.Lines[0]);
            Assert.Equal("second", tab.Lines[1]);
            Assert.Single(canvas.ExtraCursors);
            Assert.Equal("start", tab.GetSelectedText());
            Assert.Equal(0, canvas.ExtraCursors[0].AnchorCol);
            Assert.Equal(6, canvas.ExtraCursors[0].Col);

            // A17: ToggleLineComment / ToggleBlockComment on plain .txt file does not push a no-op undo snapshot
            var (_, txtTab, txtCanvas) = OpenEditor("notes.txt", "plain text");
            txtTab.CursorLine = 0;
            txtTab.CursorCol = 5;
            TypeText(txtCanvas, "!");
            Assert.Equal("plain! text", txtTab.Lines[0]);
            txtCanvas.ToggleLineComment();
            txtCanvas.ToggleBlockComment();
            // A single Undo must immediately undo "!" without being blocked by no-op comment snapshots
            PressKey(txtCanvas, Key.Z, KeyModifiers.Meta);
            Assert.Equal("plain text", txtTab.Lines[0]);
        }

        // =====================================================================
        // Sub-Area 4: Cut, Copy, Paste & Multi-Cursor Paste Distribution (A18-A22)
        // =====================================================================

        [AvaloniaFact]
        public void A18_A19_A20_A21_A22_Clipboard_WholeLineCopyCutPaste_And_MultiCursorPasteDistribution()
        {
            var (_, tab, canvas) = OpenEditor("clip.cs", "first line\nsecond line\nthird line");

            // A18: Whole-line Copy with empty selection copies line + '\n', and Paste inserts above CursorLine preserving CursorCol
            tab.ClearSelection();
            canvas.ExtraCursors.Clear();
            tab.CursorLine = 0;
            tab.CursorCol = 3;
            string copiedLine = canvas.GetCopyText(tab);
            Assert.Equal("first line\n", copiedLine);

            tab.CursorLine = 1;
            tab.CursorCol = 4;
            canvas.PasteText(copiedLine);
            Assert.Equal(4, tab.Lines.Count);
            Assert.Equal("first line", tab.Lines[1]);
            Assert.Equal("second line", tab.Lines[2]);
            Assert.Equal(2, tab.CursorLine);
            Assert.Equal(4, tab.CursorCol);
            Assert.False(tab.HasSelection());

            // A19: Whole-line Cut with empty selection removes current line and subsequent Paste inserts it above caret line
            tab.CursorLine = 1; // "first line"
            tab.CursorCol = 2;
            canvas.CutSelectionsOrLines(tab);
            Assert.Equal(3, tab.Lines.Count);
            Assert.Equal("second line", tab.Lines[1]);
            tab.CursorLine = 2; // "third line"
            tab.CursorCol = 5;
            canvas.PasteText("first line\n");
            Assert.Equal(4, tab.Lines.Count);
            Assert.Equal("first line", tab.Lines[2]);
            Assert.Equal("third line", tab.Lines[3]);
            Assert.Equal(3, tab.CursorLine);
            Assert.Equal(5, tab.CursorCol);

            // A20 + A21: Multi-cursor Copy joins N selections with '\n' and Paste distributes 1 line per caret (even when last line is empty!)
            tab.Lines = new List<string> { "A=", "B=", "C=" };
            tab.SyncContentFromLines();
            tab.ClearSelection();
            tab.CursorLine = 0;
            tab.CursorCol = 2;
            canvas.ExtraCursors.Clear();
            canvas.ExtraCursors.Add(new ExtraCursorCaret { Line = 1, Col = 2, AnchorLine = -1, AnchorCol = -1 });
            canvas.ExtraCursors.Add(new ExtraCursorCaret { Line = 2, Col = 2, AnchorLine = -1, AnchorCol = -1 });
            canvas.PasteText("10\n20\n"); // 3 lines where the 3rd is empty: ["10", "20", ""]
            Assert.Equal("A=10", tab.Lines[0]);
            Assert.Equal("B=20", tab.Lines[1]);
            Assert.Equal("C=", tab.Lines[2]);

            // A21: Multi-cursor Paste of 3 non-empty lines ("x\ny\nz") onto 3 carets distributes 1 per caret
            tab.Lines = new List<string> { "A=", "B=", "C=" };
            tab.SyncContentFromLines();
            tab.CursorLine = 0;
            tab.CursorCol = 2;
            canvas.ExtraCursors.Clear();
            canvas.ExtraCursors.Add(new ExtraCursorCaret { Line = 1, Col = 2, AnchorLine = -1, AnchorCol = -1 });
            canvas.ExtraCursors.Add(new ExtraCursorCaret { Line = 2, Col = 2, AnchorLine = -1, AnchorCol = -1 });
            canvas.PasteText("x\ny\nz");
            Assert.Equal("A=x", tab.Lines[0]);
            Assert.Equal("B=y", tab.Lines[1]);
            Assert.Equal("C=z", tab.Lines[2]);

            // A22: Multi-cursor Paste of M != N lines (2 lines onto 3 carets) pastes full text at every caret
            tab.Lines = new List<string> { "A=", "B=", "C=" };
            tab.SyncContentFromLines();
            tab.CursorLine = 0;
            tab.CursorCol = 2;
            canvas.ExtraCursors.Clear();
            canvas.ExtraCursors.Add(new ExtraCursorCaret { Line = 1, Col = 2, AnchorLine = -1, AnchorCol = -1 });
            canvas.ExtraCursors.Add(new ExtraCursorCaret { Line = 2, Col = 2, AnchorLine = -1, AnchorCol = -1 });
            canvas.PasteText("p\nq");
            Assert.Equal(6, tab.Lines.Count);
            Assert.Equal("A=p", tab.Lines[0]);
            Assert.Equal("q", tab.Lines[1]);
            Assert.Equal("B=p", tab.Lines[2]);
            Assert.Equal("q", tab.Lines[3]);
            Assert.Equal("C=p", tab.Lines[4]);
            Assert.Equal("q", tab.Lines[5]);
        }

        // =====================================================================
        // Sub-Area 5: Multi-Cursor, Line Selection & Move/Copy Line (A23-A30)
        // =====================================================================

        [AvaloniaFact]
        public void A23_A24_A25_A26_A27_MultiCursor_CmdD_BackwardsSelection_BoxSelect_LineEnds_And_CursorUndo()
        {
            var (_, tab, canvas) = OpenEditor("multi.cs", "foo bar foo\nfoo_extra foo\nthird foo");
            MacKeymap.ForceMacKeymapOverride = true;

            // A23: Cmd+D from empty selection selects whole word "foo" and skips "foo_extra"
            tab.ClearSelection();
            canvas.ExtraCursors.Clear();
            tab.CursorLine = 0;
            tab.CursorCol = 1;
            PressKey(canvas, Key.D, KeyModifiers.Meta);
            Assert.Equal("foo", tab.GetSelectedText());
            Assert.Empty(canvas.ExtraCursors);

            PressKey(canvas, Key.D, KeyModifiers.Meta);
            Assert.Single(canvas.ExtraCursors);
            Assert.Equal(0, canvas.ExtraCursors[0].Line);
            Assert.Equal(8, canvas.ExtraCursors[0].AnchorCol);
            Assert.Equal(11, canvas.ExtraCursors[0].Col);

            PressKey(canvas, Key.D, KeyModifiers.Meta);
            Assert.Equal(2, canvas.ExtraCursors.Count);
            // Skips "foo_extra" at (1, 0) and selects "foo" at (1, 10)
            Assert.Equal(1, canvas.ExtraCursors[1].Line);
            Assert.Equal(10, canvas.ExtraCursors[1].AnchorCol);
            Assert.Equal(13, canvas.ExtraCursors[1].Col);

            // A27: Cursor Undo (Cmd+U) pops the last added cursor
            PressKey(canvas, Key.U, KeyModifiers.Meta);
            Assert.Single(canvas.ExtraCursors);

            // A23 (Backwards selection bug fix): Cmd+D when primary selection is backwards (AnchorCol=3, CursorCol=0)
            // must add the NEXT occurrence at (0, 8)..(0, 11), NOT re-select (0, 0)..(0, 3)!
            canvas.ExtraCursors.Clear();
            tab.SelectionStartLine = 0;
            tab.SelectionStartCol = 3;
            tab.CursorLine = 0;
            tab.CursorCol = 0;
            PressKey(canvas, Key.D, KeyModifiers.Meta);
            Assert.Single(canvas.ExtraCursors);
            Assert.Equal(0, canvas.ExtraCursors[0].Line);
            Assert.Equal(8, canvas.ExtraCursors[0].AnchorCol);
            Assert.Equal(11, canvas.ExtraCursors[0].Col);

            // A24: Cmd+Shift+L (SelectAllFindOccurrences) selects all 4 whole-word "foo" occurrences
            canvas.ExtraCursors.Clear();
            tab.ClearSelection();
            tab.CursorLine = 0;
            tab.CursorCol = 1;
            PressKey(canvas, Key.L, KeyModifiers.Meta | KeyModifiers.Shift);
            Assert.Equal(3, canvas.ExtraCursors.Count); // 1 primary + 3 extra = 4 occurrences

            // A26: Column/Box selection via ApplyColumnBoxSelection (active end is on line 2: "thi", extras on lines 0 and 1: "foo")
            canvas.ApplyColumnBoxSelection(0, 0, 2, 3);
            Assert.Equal(2, canvas.ExtraCursors.Count);
            Assert.Equal("thi", tab.GetSelectedText());
            Assert.Equal(0, canvas.ExtraCursors[0].Line);
            Assert.Equal(0, canvas.ExtraCursors[0].AnchorCol);
            Assert.Equal(3, canvas.ExtraCursors[0].Col);

            // A27: Add Cursors to Line Ends (Shift+Alt+I) on a 3-line selection
            tab.SelectionStartLine = 0;
            tab.SelectionStartCol = 0;
            tab.CursorLine = 2;
            tab.CursorCol = 5;
            PressKey(canvas, Key.I, KeyModifiers.Shift | KeyModifiers.Alt);
            Assert.Equal(2, canvas.ExtraCursors.Count);
            Assert.Equal(tab.Lines[0].Length, tab.CursorCol);
            Assert.Equal(tab.Lines[1].Length, canvas.ExtraCursors[0].Col);
            Assert.Equal(tab.Lines[2].Length, canvas.ExtraCursors[1].Col);
        }

        [AvaloniaFact]
        public void A28_A29_A30_SelectCurrentLine_MoveLine_And_CopyLine_MatchVsCode()
        {
            var (_, tab, canvas) = OpenEditor("lines.cs", "line0\nline1\nline2\nline3");
            MacKeymap.ForceMacKeymapOverride = true;

            // A28: Cmd+L with partial selection on line 1 (cols 1..4) expands to full line 1: [1, 0)..[2, 0)
            tab.SelectionStartLine = 1;
            tab.SelectionStartCol = 1;
            tab.CursorLine = 1;
            tab.CursorCol = 4;
            PressKey(canvas, Key.L, KeyModifiers.Meta);
            Assert.Equal(1, tab.SelectionStartLine);
            Assert.Equal(0, tab.SelectionStartCol);
            Assert.Equal(2, tab.CursorLine);
            Assert.Equal(0, tab.CursorCol);

            // Pressing Cmd+L again expands to include line 2: [1, 0)..[3, 0)
            PressKey(canvas, Key.L, KeyModifiers.Meta);
            Assert.Equal(1, tab.SelectionStartLine);
            Assert.Equal(0, tab.SelectionStartCol);
            Assert.Equal(3, tab.CursorLine);
            Assert.Equal(0, tab.CursorCol);

            // Cmd+U (Cursor Undo) restores selection back to [1, 0)..[2, 0)
            PressKey(canvas, Key.U, KeyModifiers.Meta);
            Assert.Equal(1, tab.SelectionStartLine);
            Assert.Equal(2, tab.CursorLine);
            Assert.Equal(0, tab.CursorCol);

            // A29: Alt+Down with [1, 0)..[2, 0) moves ONLY line 1 below line 2 (excludes line 2 at col 0)
            PressKey(canvas, Key.Down, KeyModifiers.Alt);
            Assert.Equal("line0", tab.Lines[0]);
            Assert.Equal("line2", tab.Lines[1]);
            Assert.Equal("line1", tab.Lines[2]);
            Assert.Equal("line3", tab.Lines[3]);

            // Alt+Up moves line 1 back up
            PressKey(canvas, Key.Up, KeyModifiers.Alt);
            Assert.Equal("line1", tab.Lines[1]);
            Assert.Equal("line2", tab.Lines[2]);

            // A30: Shift+Alt+Down duplicates only line 1 below itself and moves selection to the copy
            PressKey(canvas, Key.Down, KeyModifiers.Shift | KeyModifiers.Alt);
            Assert.Equal(5, tab.Lines.Count);
            Assert.Equal("line1", tab.Lines[1]);
            Assert.Equal("line1", tab.Lines[2]);
            Assert.Equal(2, tab.SelectionStartLine);
            Assert.Equal(3, tab.CursorLine);
        }

        // =====================================================================
        // Sub-Area 6: Comment Toggle & Find/Replace (A31-A36)
        // =====================================================================

        [AvaloniaFact]
        public void A31_A32_A33_A34_A35_A36_Comments_And_FindReplace_Regex_InSelection_PreserveCase_SingleUndo()
        {
            var (win, tab, canvas) = OpenEditor("find.cs", "    int foo = 1;\n        int FOOBAR = 2;\n    int foo = 3;\n");
            MacKeymap.ForceMacKeymapOverride = true;

            // A31: ToggleLineComment (Cmd+/) comments at smallest indentation (col 4) and shifts carets
            tab.SelectionStartLine = 0;
            tab.SelectionStartCol = 4;
            tab.CursorLine = 1;
            tab.CursorCol = 12;
            PressKey(canvas, Key.OemQuestion, KeyModifiers.Meta);
            Assert.Equal("    // int foo = 1;", tab.Lines[0]);
            Assert.Equal("    //     int FOOBAR = 2;", tab.Lines[1]);
            PressKey(canvas, Key.OemQuestion, KeyModifiers.Meta);
            Assert.Equal("    int foo = 1;", tab.Lines[0]);
            Assert.Equal("        int FOOBAR = 2;", tab.Lines[1]);

            // A32: ToggleBlockComment (Shift+Alt+A) wraps and unwraps selection in /* ... */
            tab.SelectionStartLine = 0;
            tab.SelectionStartCol = 4;
            tab.CursorLine = 0;
            tab.CursorCol = 11; // "int foo"
            PressKey(canvas, Key.A, KeyModifiers.Shift | KeyModifiers.Alt);
            Assert.Equal("    /* int foo */ = 1;", tab.Lines[0]);
            PressKey(canvas, Key.A, KeyModifiers.Shift | KeyModifiers.Alt);
            Assert.Equal("    int foo = 1;", tab.Lines[0]);

            // A33: Find Match Case, Whole Word, and Invalid Regex error reporting
            var ed = win.PrimaryEditor;
            ed.ShowFindBar(includeReplace: true);
            ed.SetFindQuery("foo", matchCase: true, wholeWord: true, useRegex: false);
            Assert.Equal(2, ed.FindMatches.Count); // lines 0 and 2, excludes FOOBAR

            ed.SetFindQuery("[unclosed", matchCase: false, wholeWord: false, useRegex: true);
            Assert.NotNull(ed.FindErrorText);
            Assert.Equal("Invalid regex", ed.FindCountText);

            // A34: Find in Selection restricts matches to lines 0..1
            tab.SelectionStartLine = 0;
            tab.SelectionStartCol = 0;
            tab.CursorLine = 2;
            tab.CursorCol = 0; // col 0 of line 2 excludes line 2!
            ed.ToggleFindOption(FindOptionToggles.InSelection);
            ed.SetFindQuery("foo", matchCase: false, wholeWord: false, useRegex: false);
            Assert.Equal(2, ed.FindMatches.Count); // line 0 ("foo") and line 1 ("FOO" in FOOBAR), excluding line 2!
            ed.ToggleFindOption(FindOptionToggles.InSelection); // turn off InSelection

            // A35: Preserve Case replacement ("foo" -> "bar", "FOOBAR" -> "BARBAR")
            ed.ToggleFindOption(FindOptionToggles.PreserveCase);
            ed.SetFindQuery("foo", matchCase: false, wholeWord: false, useRegex: false);
            ed.ReplaceAllMatches("bar");
            Assert.Equal("    int bar = 1;", tab.Lines[0]);
            Assert.Equal("        int BARBAR = 2;", tab.Lines[1]);
            Assert.Equal("    int bar = 3;", tab.Lines[2]);

            // A36: Replace All is a single Undo step!
            PressKey(canvas, Key.Z, KeyModifiers.Meta);
            Assert.Equal("    int foo = 1;", tab.Lines[0]);
            Assert.Equal("        int FOOBAR = 2;", tab.Lines[1]);
            Assert.Equal("    int foo = 3;", tab.Lines[2]);

            // A36: Regex capture group replacement ($1) as a single Undo step
            ed.ToggleFindOption(FindOptionToggles.PreserveCase); // turn off PreserveCase
            ed.SetFindQuery(@"int (\w+) = (\d+);", matchCase: true, wholeWord: false, useRegex: true);
            Assert.Equal(3, ed.FindMatches.Count);
            ed.ReplaceAllMatches("var $1 = $20;");
            Assert.Equal("    var foo = 10;", tab.Lines[0]);
            Assert.Equal("        var FOOBAR = 20;", tab.Lines[1]);
            Assert.Equal("    var foo = 30;", tab.Lines[2]);
            PressKey(canvas, Key.Z, KeyModifiers.Meta);
            Assert.Equal("    int foo = 1;", tab.Lines[0]);
        }

        // =====================================================================
        // Sub-Area 7: Go to Line & Word/Subword Navigation (A37-A40)
        // =====================================================================

        [AvaloniaFact]
        public void A37_A38_A39_A40_GoToLine_WordAndSubwordNav_BothKeymaps_Transpose_Join_And_Sort()
        {
            var (win, tab, canvas) = OpenEditor("nav.cs",
                "void M() {\n    int a = 1;\n    int hidden = 2;\n}\nxmlHttpRequest snake_case_id\nab\nc\na\nb");

            // A37: Fold block at line 0..3, then GoToLine(3, 9) unfolds the region and places caret at (2, 8)
            canvas.FoldAtLine(0);
            Assert.True(tab.FoldedBlockRanges.ContainsKey(0));
            win.PrimaryEditor.GoToLine(3, 9);
            Assert.False(tab.FoldedBlockRanges.ContainsKey(0));
            Assert.Equal(2, tab.CursorLine);
            Assert.Equal(8, tab.CursorCol);

            // A38 + A39: Word and Subword navigation on "xmlHttpRequest snake_case_id" (line 4) under mac and non-mac keymaps
            foreach (bool isMac in new[] { true, false })
            {
                MacKeymap.ForceMacKeymapOverride = isMac;
                tab.ClearSelection();
                tab.CursorLine = 4;
                tab.CursorCol = 0;

                // Subword right across "xmlHttpRequest": 0 -> 3 ("xml") -> 7 ("Http") -> 14 ("Request")
                PressKey(canvas, Key.Right, KeyModifiers.Control | KeyModifiers.Alt);
                Assert.Equal(3, tab.CursorCol);
                PressKey(canvas, Key.Right, KeyModifiers.Control | KeyModifiers.Alt);
                Assert.Equal(7, tab.CursorCol);
                PressKey(canvas, Key.Right, KeyModifiers.Control | KeyModifiers.Alt);
                Assert.Equal(14, tab.CursorCol);

                // Word right to end of "snake_case_id"
                PressKey(canvas, Key.Right, isMac ? KeyModifiers.Alt : KeyModifiers.Control);
                Assert.Equal(28, tab.CursorCol);
                // Word left back to start of "snake_case_id"
                PressKey(canvas, Key.Left, isMac ? KeyModifiers.Alt : KeyModifiers.Control);
                Assert.Equal(15, tab.CursorCol);
            }

            // A40: TransposeLetters (Ctrl+T), JoinLines (Ctrl+J), and SortLinesAscending / SortLinesDescending
            tab.CursorLine = 5; // "ab"
            tab.CursorCol = 1;
            canvas.TransposeLetters();
            Assert.Equal("ba", tab.Lines[5]);

            tab.SelectionStartLine = 6; // "c", "a", "b"
            tab.SelectionStartCol = 0;
            tab.CursorLine = 8;
            tab.CursorCol = 1;
            canvas.SortLinesAscending();
            Assert.Equal("a", tab.Lines[6]);
            Assert.Equal("b", tab.Lines[7]);
            Assert.Equal("c", tab.Lines[8]);

            canvas.SortLinesDescending();
            Assert.Equal("c", tab.Lines[6]);
            Assert.Equal("b", tab.Lines[7]);
            Assert.Equal("a", tab.Lines[8]);

            tab.ClearSelection();
            tab.CursorLine = 6;
            tab.CursorCol = 0;
            canvas.JoinLines();
            Assert.Equal("c b", tab.Lines[6]);
        }

        // =====================================================================
        // Sub-Area 8: Scroll/Reveal, IME, Emoji, CRLF, Long Lines & Large Files (A41-A44)
        // =====================================================================

        [AvaloniaFact]
        public void A41_A42_A43_A44_ScrollReveal_ImeComposition_EmojiSurrogates_CrlfPreservation_LongLines_And_LargeFilePolicy()
        {
            var lines = Enumerable.Range(0, 100).Select(i => "line " + i).ToList();
            var (win, tab, canvas) = OpenEditor("scroll.cs", string.Join("\n", lines), width: 800, height: 200);

            // A41: CursorSurroundingLines keeps margin rows visible above/below caret
            EditorNicetyOptions.CursorSurroundingLines = 3;
            tab.ScrollTopLine = 20;
            tab.CursorLine = 21;
            tab.CursorCol = 0;
            canvas.EnsureCursorVisible(animate: false);
            Assert.True(tab.ScrollTopLine <= 18, $"Expected ScrollTopLine <= 18 with surroundingLines=3, got {tab.ScrollTopLine}");

            // A42: IME composition (SetImePreedit) prevents Enter/Backspace/Arrows from mutating the buffer while composing
            tab.CursorLine = 0;
            tab.CursorCol = 4;
            string beforeIme = tab.Lines[0];
            canvas.SetImePreedit("かんじ");
            Assert.True(canvas.IsImeComposing);
            PressKey(canvas, Key.Enter);
            PressKey(canvas, Key.Back);
            PressKey(canvas, Key.Left);
            Assert.Equal(beforeIme, tab.Lines[0]);
            Assert.Equal(4, tab.CursorCol);
            canvas.SetImePreedit(null);
            Assert.False(canvas.IsImeComposing);

            // A42: Emoji surrogate pair ("a😀b") moves and deletes as a single 2-UTF16-unit grapheme
            tab.Lines[0] = "a\uD83D\uDE00b";
            tab.CursorLine = 0;
            tab.CursorCol = 1;
            PressKey(canvas, Key.Right);
            Assert.Equal(3, tab.CursorCol);
            PressKey(canvas, Key.Back);
            Assert.Equal("ab", tab.Lines[0]);
            Assert.Equal(1, tab.CursorCol);

            // A43: CRLF file preserves EolSequence ("\r\n") and HasFinalNewline across load, edit, and save
            string crlfPath = Path.Combine(tempDir, "crlf_test.cs");
            File.WriteAllBytes(crlfPath, Encoding.UTF8.GetBytes("alpha\r\nbeta\r\n"));
            var crlfTab = win.OpenFileInTab(crlfPath, preview: false);
            Assert.Equal("\r\n", crlfTab.EolSequence);
            Assert.True(crlfTab.HasFinalNewline);
            crlfTab.CursorLine = 0;
            crlfTab.CursorCol = 5;
            TypeText(win.PrimaryEditor.Canvas, "1");
            win.SaveActiveTab();
            string savedRaw = Encoding.UTF8.GetString(File.ReadAllBytes(crlfPath));
            Assert.Equal("alpha1\r\nbeta\r\n", savedRaw);

            // A44: Very long line (> 20,000 chars) renders and edits without freezing; >= 50 MB triggers LargeFilePolicy / LargeFileViewer
            string longLine = new string('x', 25000);
            crlfTab.Lines[0] = longLine;
            crlfTab.CursorLine = 0;
            crlfTab.CursorCol = 24990;
            RenderCanvas(win.PrimaryEditor.Canvas, 800, 200);
            Assert.Equal(25000, crlfTab.Lines[0].Length);

            string hugePath = Path.Combine(tempDir, "huge_52mb.log");
            using (var fs = new FileStream(hugePath, FileMode.Create, FileAccess.Write, FileShare.ReadWrite))
            {
                byte[] header = Encoding.UTF8.GetBytes(string.Concat(Enumerable.Repeat("2026-11-26 INFO sample log line\n", 300)));
                fs.Write(header, 0, header.Length);
                fs.SetLength(52L * 1024 * 1024);
            }
            Assert.True(LargeFilePolicy.ShouldUseViewer(hugePath, null));
            var hugeTab = win.OpenFileInTab(hugePath, preview: false);
            Assert.NotNull(hugeTab);
            Assert.True(hugeTab.IsLargeFileViewer);
        }
    }
}
