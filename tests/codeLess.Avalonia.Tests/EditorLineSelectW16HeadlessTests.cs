using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
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
    /// W16 headless tests for VS Code line-boundary movement and selection parity
    /// (Cmd+Shift+Right / Shift+End / cursorEndSelect and its entire family across mac and non-mac keymaps).
    /// Driven through the real key dispatch path.
    /// </summary>
    [Collection("SequentialWorkbench")]
    public class EditorLineSelectW16HeadlessTests : IDisposable
    {
        private readonly string tempDir;
        private readonly List<MainWindow> windows = new List<MainWindow>();
        private readonly bool? savedMacOverride = MacKeymap.ForceMacKeymapOverride;
        private readonly bool savedSmoothScroll = EditorScrollOptions.SmoothScrolling;
        private readonly bool savedSmoothCaret = EditorScrollOptions.SmoothCaretAnimation;
        private readonly int savedTabSize = AvaloniaVirtualEditorCanvas.DefaultDisplayTabSize;
        private readonly bool savedInsertSpaces = AvaloniaVirtualEditorCanvas.InsertSpacesSetting;

        public EditorLineSelectW16HeadlessTests()
        {
            tempDir = Path.Combine(Path.GetTempPath(), "codeless_w16_linesel_" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(tempDir);
            EditorScrollOptions.SmoothScrolling = false;
            EditorScrollOptions.SmoothCaretAnimation = false;
            AvaloniaVirtualEditorCanvas.DefaultDisplayTabSize = 4;
            AvaloniaVirtualEditorCanvas.InsertSpacesSetting = true;
        }

        public void Dispose()
        {
            MacKeymap.ForceMacKeymapOverride = savedMacOverride;
            EditorScrollOptions.SmoothScrolling = savedSmoothScroll;
            EditorScrollOptions.SmoothCaretAnimation = savedSmoothCaret;
            AvaloniaVirtualEditorCanvas.DefaultDisplayTabSize = savedTabSize;
            AvaloniaVirtualEditorCanvas.InsertSpacesSetting = savedInsertSpaces;
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

        /// <summary>
        /// Dispatches a real KeyDownEvent on the focused editor canvas (which bubbles to MainWindow if unhandled).
        /// </summary>
        private static void PressKey(AvaloniaVirtualEditorCanvas canvas, Key key, KeyModifiers modifiers)
        {
            canvas.RaiseEvent(new KeyEventArgs
            {
                RoutedEvent = InputElement.KeyDownEvent,
                Key = key,
                KeyModifiers = modifiers
            });
        }

        private static void PressLineEnd(AvaloniaVirtualEditorCanvas canvas, bool isMac, bool shift)
        {
            if (isMac)
            {
                PressKey(canvas, Key.Right, KeyModifiers.Meta | (shift ? KeyModifiers.Shift : KeyModifiers.None));
            }
            else
            {
                PressKey(canvas, Key.End, shift ? KeyModifiers.Shift : KeyModifiers.None);
            }
        }

        private static void PressLineHome(AvaloniaVirtualEditorCanvas canvas, bool isMac, bool shift)
        {
            if (isMac)
            {
                PressKey(canvas, Key.Left, KeyModifiers.Meta | (shift ? KeyModifiers.Shift : KeyModifiers.None));
            }
            else
            {
                PressKey(canvas, Key.Home, shift ? KeyModifiers.Shift : KeyModifiers.None);
            }
        }

        // ------------------------------------------------------------------
        // Cases 1, 2, 9, 10, 12: Plain ASCII, Trailing Spaces, Already at End,
        // Last Line of Document, and Empty Line (both Mac and Non-Mac keymaps)
        // ------------------------------------------------------------------
        [AvaloniaTheory]
        [InlineData(true)]
        [InlineData(false)]
        public void Cases1_2_9_10_12_Ascii_TrailingSpaces_AtEnd_LastLine_EmptyLine_MatchVsCode(bool isMac)
        {
            MacKeymap.ForceMacKeymapOverride = isMac;
            var (w, tab, canvas) = OpenEditor($"basic_{isMac}.cs", "");

            // Type known lines through real text input dispatch:
            // Line 0: "int count = 42;" (len 15)
            // Line 1: "int trail = 99;   " (len 18, 3 trailing spaces)
            // Line 2: "" (empty line, len 0)
            // Line 3: "return count + trail;" (last line of document, len 21)
            tab.Lines.Clear();
            tab.Lines.Add("");
            tab.CursorLine = 0;
            tab.CursorCol = 0;
            TypeText(canvas, "int count = 42;");
            Assert.Equal("int count = 42;", tab.Lines[0]);
            tab.Lines.Add("int trail = 99;   ");
            tab.Lines.Add("");
            tab.Lines.Add("return count + trail;");

            // Case 1: Plain ASCII line, caret mid-line (col 4 -> "count = 42;")
            tab.ClearSelection();
            tab.CursorLine = 0;
            tab.CursorCol = 4;
            PressLineEnd(canvas, isMac, shift: true);
            Assert.True(tab.HasSelection());
            Assert.Equal(0, tab.SelectionAnchorLine);
            Assert.Equal(4, tab.SelectionAnchorCol);
            Assert.Equal(0, tab.CursorLine);
            Assert.Equal(15, tab.CursorCol);
            Assert.Equal("count = 42;", tab.GetSelectedText());

            // Also verify SimulateShortcut(Key.Right, Control | Shift) on macOS selects to line end (not 1 word!)
            if (isMac)
            {
                tab.ClearSelection();
                tab.CursorLine = 0;
                tab.CursorCol = 4;
                w.SimulateShortcut(Key.Right, KeyModifiers.Control | KeyModifiers.Shift);
                Assert.True(tab.HasSelection());
                Assert.Equal(4, tab.SelectionAnchorCol);
                Assert.Equal(15, tab.CursorCol);
                Assert.Equal("count = 42;", tab.GetSelectedText());
            }

            // Case 9a: Caret already at line end with existing selection -> pressing Shift+LineEnd again preserves selection [4..15]
            PressLineEnd(canvas, isMac, shift: true);
            Assert.True(tab.HasSelection());
            Assert.Equal(0, tab.SelectionAnchorLine);
            Assert.Equal(4, tab.SelectionAnchorCol);
            Assert.Equal(0, tab.CursorLine);
            Assert.Equal(15, tab.CursorCol);

            // Case 9b: Caret already at line end with no selection -> Shift+LineEnd leaves selection empty and stays on line 0
            tab.ClearSelection();
            tab.CursorLine = 0;
            tab.CursorCol = 15;
            PressLineEnd(canvas, isMac, shift: true);
            Assert.False(tab.HasSelection());
            Assert.Equal(0, tab.CursorLine);
            Assert.Equal(15, tab.CursorCol);

            PressLineEnd(canvas, isMac, shift: false);
            Assert.False(tab.HasSelection());
            Assert.Equal(0, tab.CursorLine);
            Assert.Equal(15, tab.CursorCol);

            // Case 2: Line with trailing spaces -> must include all trailing spaces (col 4..18)
            tab.ClearSelection();
            tab.CursorLine = 1;
            tab.CursorCol = 4;
            PressLineEnd(canvas, isMac, shift: true);
            Assert.True(tab.HasSelection());
            Assert.Equal(1, tab.SelectionAnchorLine);
            Assert.Equal(4, tab.SelectionAnchorCol);
            Assert.Equal(1, tab.CursorLine);
            Assert.Equal(18, tab.CursorCol);
            Assert.Equal("trail = 99;   ", tab.GetSelectedText());

            // Case 12: Empty line -> caret stays at col 0, selection empty
            tab.ClearSelection();
            tab.CursorLine = 2;
            tab.CursorCol = 0;
            PressLineEnd(canvas, isMac, shift: true);
            Assert.False(tab.HasSelection());
            Assert.Equal(2, tab.CursorLine);
            Assert.Equal(0, tab.CursorCol);

            PressLineHome(canvas, isMac, shift: true);
            Assert.False(tab.HasSelection());
            Assert.Equal(2, tab.CursorLine);
            Assert.Equal(0, tab.CursorCol);

            // Case 10: Last line of the document
            tab.ClearSelection();
            tab.CursorLine = 3;
            tab.CursorCol = 7;
            PressLineEnd(canvas, isMac, shift: true);
            Assert.True(tab.HasSelection());
            Assert.Equal(3, tab.SelectionAnchorLine);
            Assert.Equal(7, tab.SelectionAnchorCol);
            Assert.Equal(3, tab.CursorLine);
            Assert.Equal(21, tab.CursorCol);
            Assert.Equal("count + trail;", tab.GetSelectedText());

            // Document boundary selection: cursorTopSelect (Cmd+Shift+Up / Ctrl+Shift+Home) and cursorBottomSelect (Cmd+Shift+Down / Ctrl+Shift+End)
            tab.ClearSelection();
            tab.CursorLine = 1;
            tab.CursorCol = 4;
            if (isMac)
                PressKey(canvas, Key.Up, KeyModifiers.Meta | KeyModifiers.Shift);
            else
                PressKey(canvas, Key.Home, KeyModifiers.Control | KeyModifiers.Shift);
            Assert.True(tab.HasSelection());
            Assert.Equal(1, tab.SelectionAnchorLine);
            Assert.Equal(4, tab.SelectionAnchorCol);
            Assert.Equal(0, tab.CursorLine);
            Assert.Equal(0, tab.CursorCol);

            tab.ClearSelection();
            tab.CursorLine = 1;
            tab.CursorCol = 4;
            if (isMac)
                PressKey(canvas, Key.Down, KeyModifiers.Meta | KeyModifiers.Shift);
            else
                PressKey(canvas, Key.End, KeyModifiers.Control | KeyModifiers.Shift);
            Assert.True(tab.HasSelection());
            Assert.Equal(1, tab.SelectionAnchorLine);
            Assert.Equal(4, tab.SelectionAnchorCol);
            Assert.Equal(3, tab.CursorLine);
            Assert.Equal(21, tab.CursorCol);
        }

        // ------------------------------------------------------------------
        // Case 3 & Case 4: Leading Indentation Home Toggle, All-Whitespace Line,
        // and Tabs
        // ------------------------------------------------------------------
        [AvaloniaTheory]
        [InlineData(true)]
        [InlineData(false)]
        public void Cases3_4_LeadingIndentationToggle_AllWhitespaceLine_AndTabs_MatchVsCode(bool isMac)
        {
            MacKeymap.ForceMacKeymapOverride = isMac;
            // Line 0: 4 leading spaces + "int x = 1;" (firstNonWs = 4, len = 14)
            // Line 1: 4 spaces only "    " (all whitespace, len = 4 -> VS Code firstNonWs target is col 0!)
            // Line 2: 2 leading tabs "\t\tint y = 2;" (firstNonWs = 2, len = 12)
            string content = "    int x = 1;\n    \n\t\tint y = 2;\n";
            var (_, tab, canvas) = OpenEditor($"indent_{isMac}.cs", content);

            // Case 3a: Caret at col 8 on "    int x = 1;"
            // 1st Shift+Home -> selects [4..8] ("int ")
            // 2nd Shift+Home -> toggles to col 0, preserving anchor at 8 -> selects [0..8] ("    int ")
            // 3rd Shift+Home -> toggles back to col 4, preserving anchor at 8 -> selects [4..8] ("int ")
            tab.ClearSelection();
            tab.CursorLine = 0;
            tab.CursorCol = 8;
            PressLineHome(canvas, isMac, shift: true);
            Assert.True(tab.HasSelection());
            Assert.Equal(8, tab.SelectionAnchorCol);
            Assert.Equal(4, tab.CursorCol);
            Assert.Equal("int ", tab.GetSelectedText());

            PressLineHome(canvas, isMac, shift: true);
            Assert.True(tab.HasSelection());
            Assert.Equal(8, tab.SelectionAnchorCol);
            Assert.Equal(0, tab.CursorCol);
            Assert.Equal("    int ", tab.GetSelectedText());

            PressLineHome(canvas, isMac, shift: true);
            Assert.True(tab.HasSelection());
            Assert.Equal(8, tab.SelectionAnchorCol);
            Assert.Equal(4, tab.CursorCol);
            Assert.Equal("int ", tab.GetSelectedText());

            // Case 3b: Caret inside leading whitespace at col 2 on "    int x = 1;"
            // 1st Home -> moves to firstNonWs (col 4)
            // 2nd Home -> moves to col 0
            tab.ClearSelection();
            tab.CursorLine = 0;
            tab.CursorCol = 2;
            PressLineHome(canvas, isMac, shift: false);
            Assert.False(tab.HasSelection());
            Assert.Equal(4, tab.CursorCol);

            PressLineHome(canvas, isMac, shift: false);
            Assert.False(tab.HasSelection());
            Assert.Equal(0, tab.CursorCol);

            // Case 3c: All-whitespace line ("    ", len 4)
            // In VS Code, getLineFirstNonWhitespaceColumn returns 0 for all-whitespace lines, so target is col 0 (NOT col 4!).
            tab.ClearSelection();
            tab.CursorLine = 1;
            tab.CursorCol = 3;
            PressLineHome(canvas, isMac, shift: true);
            Assert.True(tab.HasSelection());
            Assert.Equal(3, tab.SelectionAnchorCol);
            Assert.Equal(0, tab.CursorCol);
            Assert.Equal("   ", tab.GetSelectedText());

            // Case 4: Tabs ("\t\tint y = 2;", len = 12, firstNonWs = 2)
            tab.ClearSelection();
            tab.CursorLine = 2;
            tab.CursorCol = 6; // after "\t\tint "
            PressLineEnd(canvas, isMac, shift: true);
            Assert.True(tab.HasSelection());
            Assert.Equal(6, tab.SelectionAnchorCol);
            Assert.Equal(12, tab.CursorCol);
            Assert.Equal("y = 2;", tab.GetSelectedText());

            tab.ClearSelection();
            tab.CursorLine = 2;
            tab.CursorCol = 6;
            PressLineHome(canvas, isMac, shift: true);
            Assert.True(tab.HasSelection());
            Assert.Equal(6, tab.SelectionAnchorCol);
            Assert.Equal(2, tab.CursorCol);
            Assert.Equal("int ", tab.GetSelectedText());

            PressLineHome(canvas, isMac, shift: true);
            Assert.True(tab.HasSelection());
            Assert.Equal(6, tab.SelectionAnchorCol);
            Assert.Equal(0, tab.CursorCol);
            Assert.Equal("\t\tint ", tab.GetSelectedText());
        }

        // ------------------------------------------------------------------
        // Case 5: Emoji / Surrogate Pairs and CJK Characters
        // ------------------------------------------------------------------
        [AvaloniaTheory]
        [InlineData(true)]
        [InlineData(false)]
        public void Case5_SurrogatePairs_Emoji_AndCjk_SelectFullCodeUnitsWithoutSplitting(bool isMac)
        {
            MacKeymap.ForceMacKeymapOverride = isMac;
            // "  a😀b🚀c漢字;"
            // Indices:
            // 0..1: "  "
            // 2: 'a'
            // 3..4: "😀" (surrogate pair, 2 UTF-16 code units)
            // 5: 'b'
            // 6..7: "🚀" (surrogate pair, 2 UTF-16 code units)
            // 8: 'c'
            // 9: '漢'
            // 10: '字'
            // 11: ';'
            // Total length = 12 UTF-16 code units.
            string line = "  a\uD83D\uDE00b\uD83D\uDE80c\u6F22\u5B57;";
            Assert.Equal(12, line.Length);
            var (_, tab, canvas) = OpenEditor($"emoji_{isMac}.cs", line + "\n");

            tab.ClearSelection();
            tab.CursorLine = 0;
            tab.CursorCol = 3; // right before 😀
            PressLineEnd(canvas, isMac, shift: true);
            Assert.True(tab.HasSelection());
            Assert.Equal(3, tab.SelectionAnchorCol);
            Assert.Equal(12, tab.CursorCol);
            Assert.Equal("\uD83D\uDE00b\uD83D\uDE80c\u6F22\u5B57;", tab.GetSelectedText());

            // Stepping Shift+Right and Shift+Left across surrogate pairs never lands on low surrogate (col 4 or col 7)
            tab.ClearSelection();
            tab.CursorLine = 0;
            tab.CursorCol = 3;
            PressKey(canvas, Key.Right, KeyModifiers.Shift);
            Assert.Equal(5, tab.CursorCol); // stepped over both UTF-16 units of 😀
            Assert.Equal("\uD83D\uDE00", tab.GetSelectedText());

            PressKey(canvas, Key.Left, KeyModifiers.Shift);
            Assert.False(tab.HasSelection());
            Assert.Equal(3, tab.CursorCol);
        }

        // ------------------------------------------------------------------
        // Case 6: Word Wrap ON — Visual Row First, Then Model Line End/Start;
        // plus macOS Emacs Ctrl+E / Ctrl+Shift+E / Ctrl+A / Ctrl+Shift+A
        // ------------------------------------------------------------------
        [AvaloniaTheory]
        [InlineData(true)]
        [InlineData(false)]
        public void Case6_WordWrapOn_VisualRowFirstThenModelLine_AndMacCtrlESskipsWrap(bool isMac)
        {
            MacKeymap.ForceMacKeymapOverride = isMac;
            string longLine = "    var total = " + string.Join(" + ", Enumerable.Range(0, 35).Select(i => "item" + i)) + ";";
            var (w, tab, canvas) = OpenEditor($"wrap_{isMac}.cs", longLine + "\nnextLine();\n", width: 650, height: 500);

            tab.WordWrap = true;
            RenderCanvas(canvas, 650, 500);
            var rows = canvas.LastFrameRows.Where(r => r.DocLine == 0).ToList();
            Assert.True(rows.Count >= 3, "Expected at least 3 wrapped rows, got " + rows.Count);

            // Start on row 0 at col 8:
            // 1st Shift+LineEnd -> stops at visual wrap boundary rows[0].End with row-end affinity (drawn on row 0)
            tab.ClearSelection();
            tab.CursorLine = 0;
            tab.CursorCol = 8;

            PressLineEnd(canvas, isMac, shift: true);
            Assert.True(tab.HasSelection());
            Assert.Equal(8, tab.SelectionAnchorCol);
            Assert.Equal(rows[0].End, tab.CursorCol);
            RenderCanvas(canvas, 650, 500);
            Assert.Equal(0, canvas.CaretRect.Y, 1);

            // 2nd Shift+LineEnd -> advances to model line end (longLine.Length), preserving anchor at 8
            PressLineEnd(canvas, isMac, shift: true);
            Assert.True(tab.HasSelection());
            Assert.Equal(8, tab.SelectionAnchorCol);
            Assert.Equal(longLine.Length, tab.CursorCol);

            // Also verify starting from middle of wrapped row 1:
            // 1st Shift+LineEnd -> stops at visual wrap boundary rows[1].End (drawn on row 1)
            // 2nd Shift+LineEnd -> advances to model line end (longLine.Length)
            int midRow1ForEnd = (rows[1].Start + rows[1].End) / 2;
            tab.ClearSelection();
            tab.CursorLine = 0;
            tab.CursorCol = midRow1ForEnd;
            PressLineEnd(canvas, isMac, shift: true);
            Assert.True(tab.HasSelection());
            Assert.Equal(midRow1ForEnd, tab.SelectionAnchorCol);
            Assert.Equal(rows[1].End, tab.CursorCol);
            RenderCanvas(canvas, 650, 500);
            Assert.Equal(1 * canvas.LineHeight, canvas.CaretRect.Y, 1);

            PressLineEnd(canvas, isMac, shift: true);
            Assert.True(tab.HasSelection());
            Assert.Equal(midRow1ForEnd, tab.SelectionAnchorCol);
            Assert.Equal(longLine.Length, tab.CursorCol);

            // Now test Shift+Home from the middle of wrapped row 1:
            int midRow1 = (rows[1].Start + rows[1].End) / 2;
            tab.ClearSelection();
            tab.CursorLine = 0;
            tab.CursorCol = midRow1;

            // 1st Shift+Home -> stops at rows[1].Start (visual row start)
            PressLineHome(canvas, isMac, shift: true);
            Assert.True(tab.HasSelection());
            Assert.Equal(midRow1, tab.SelectionAnchorCol);
            Assert.Equal(rows[1].Start, tab.CursorCol);

            // 2nd Shift+Home -> moves to firstNonWs (col 4), preserving anchor at midRow1
            PressLineHome(canvas, isMac, shift: true);
            Assert.Equal(midRow1, tab.SelectionAnchorCol);
            Assert.Equal(4, tab.CursorCol);

            // 3rd Shift+Home -> moves to model line start (col 0), preserving anchor at midRow1
            PressLineHome(canvas, isMac, shift: true);
            Assert.Equal(midRow1, tab.SelectionAnchorCol);
            Assert.Equal(0, tab.CursorCol);

            // On macOS, Ctrl+E / Ctrl+Shift+E (cursorLineEnd / cursorLineEndSelect) and
            // Ctrl+A / Ctrl+Shift+A (cursorLineStart / cursorLineStartSelect) are model-line commands:
            // they skip visual wrap boundaries and indentation toggle!
            if (isMac)
            {
                tab.ClearSelection();
                tab.CursorLine = 0;
                tab.CursorCol = 8;
                PressKey(canvas, Key.E, KeyModifiers.Control | KeyModifiers.Shift);
                Assert.True(tab.HasSelection());
                Assert.Equal(8, tab.SelectionAnchorCol);
                Assert.Equal(longLine.Length, tab.CursorCol);

                tab.ClearSelection();
                tab.CursorLine = 0;
                tab.CursorCol = midRow1;
                PressKey(canvas, Key.A, KeyModifiers.Control | KeyModifiers.Shift);
                Assert.True(tab.HasSelection());
                Assert.Equal(midRow1, tab.SelectionAnchorCol);
                Assert.Equal(0, tab.CursorCol);
            }
        }

        // ------------------------------------------------------------------
        // Case 7: Existing Selection (Forward and Backward Anchor Preservation)
        // ------------------------------------------------------------------
        [AvaloniaTheory]
        [InlineData(true)]
        [InlineData(false)]
        public void Case7_ExistingSelection_ForwardAndBackward_PreservesAnchor(bool isMac)
        {
            MacKeymap.ForceMacKeymapOverride = isMac;
            var (_, tab, canvas) = OpenEditor($"anchor_{isMac}.cs", "0123456789abcdef\n");

            // Forward selection [2..6] (anchor = 2, caret = 6) -> Shift+End extends to [2..16]
            tab.SelectionAnchorLine = 0;
            tab.SelectionAnchorCol = 2;
            tab.CursorLine = 0;
            tab.CursorCol = 6;
            PressLineEnd(canvas, isMac, shift: true);
            Assert.True(tab.HasSelection());
            Assert.Equal(2, tab.SelectionAnchorCol);
            Assert.Equal(16, tab.CursorCol);

            // Forward selection [2..6] (anchor = 2, caret = 6) -> Shift+Home flips active end to 0 while keeping anchor = 2
            tab.SelectionAnchorLine = 0;
            tab.SelectionAnchorCol = 2;
            tab.CursorLine = 0;
            tab.CursorCol = 6;
            PressLineHome(canvas, isMac, shift: true);
            Assert.True(tab.HasSelection());
            Assert.Equal(2, tab.SelectionAnchorCol);
            Assert.Equal(0, tab.CursorCol);
            Assert.Equal("01", tab.GetSelectedText());

            // Backward selection [2..6] (anchor = 6, caret = 2) -> Shift+End flips active end to 16 while keeping anchor = 6
            tab.SelectionAnchorLine = 0;
            tab.SelectionAnchorCol = 6;
            tab.CursorLine = 0;
            tab.CursorCol = 2;
            PressLineEnd(canvas, isMac, shift: true);
            Assert.True(tab.HasSelection());
            Assert.Equal(6, tab.SelectionAnchorCol);
            Assert.Equal(16, tab.CursorCol);
            Assert.Equal("6789abcdef", tab.GetSelectedText());

            // Backward selection [2..6] (anchor = 6, caret = 2) -> Shift+Home extends to [0..6] keeping anchor = 6
            tab.SelectionAnchorLine = 0;
            tab.SelectionAnchorCol = 6;
            tab.CursorLine = 0;
            tab.CursorCol = 2;
            PressLineHome(canvas, isMac, shift: true);
            Assert.True(tab.HasSelection());
            Assert.Equal(6, tab.SelectionAnchorCol);
            Assert.Equal(0, tab.CursorCol);
            Assert.Equal("012345", tab.GetSelectedText());
        }

        // ------------------------------------------------------------------
        // Case 8: Multi-Cursor (Different Lines + Same-Line Overlapping Merge)
        // ------------------------------------------------------------------
        [AvaloniaTheory]
        [InlineData(true)]
        [InlineData(false)]
        public void Case8_MultiCursor_DifferentLines_WrappedLines_AndSameLineMerge_MatchVsCode(bool isMac)
        {
            MacKeymap.ForceMacKeymapOverride = isMac;
            string content = "  first line\n    second line here\n      third!\n";
            var (_, tab, canvas) = OpenEditor($"multi_{isMac}.cs", content);

            // 8a: Multi-cursor on 3 different lines: each selects to its own line end
            tab.ClearSelection();
            tab.CursorLine = 0;
            tab.CursorCol = 2;
            canvas.ExtraCursors.Clear();
            canvas.ExtraCursors.Add(new ExtraCursorCaret { Line = 1, Col = 4, AnchorLine = -1, AnchorCol = -1 });
            canvas.ExtraCursors.Add(new ExtraCursorCaret { Line = 2, Col = 6, AnchorLine = -1, AnchorCol = -1 });

            PressLineEnd(canvas, isMac, shift: true);
            Assert.True(tab.HasSelection());
            Assert.Equal(2, tab.SelectionAnchorCol);
            Assert.Equal(12, tab.CursorCol);
            Assert.Equal(2, canvas.ExtraCursors.Count);
            Assert.Equal(4, canvas.ExtraCursors[0].AnchorCol);
            Assert.Equal(20, canvas.ExtraCursors[0].Col);
            Assert.Equal(6, canvas.ExtraCursors[1].AnchorCol);
            Assert.Equal(12, canvas.ExtraCursors[1].Col);

            // 8b: Two cursors on the SAME line ("    second line here", len 20) at col 4 and col 11:
            // When both run Cmd+Shift+Right / Shift+End, cursor 0 selects [4..20] and cursor 1 selects [11..20].
            // VS Code merges overlapping selections into the union [4..20]!
            tab.ClearSelection();
            canvas.ExtraCursors.Clear();
            tab.CursorLine = 1;
            tab.CursorCol = 4;
            canvas.ExtraCursors.Add(new ExtraCursorCaret { Line = 1, Col = 11, AnchorLine = -1, AnchorCol = -1 });

            PressLineEnd(canvas, isMac, shift: true);
            Assert.Empty(canvas.ExtraCursors);
            Assert.True(tab.HasSelection());
            Assert.Equal(1, tab.SelectionAnchorLine);
            Assert.Equal(4, tab.SelectionAnchorCol);
            Assert.Equal(1, tab.CursorLine);
            Assert.Equal(20, tab.CursorCol);
            Assert.Equal("second line here", tab.GetSelectedText());

            // Repeat when primary cursor is at col 11 and secondary cursor is at col 4:
            // Union must still be [4..20] on the surviving primary cursor!
            tab.ClearSelection();
            canvas.ExtraCursors.Clear();
            tab.CursorLine = 1;
            tab.CursorCol = 11;
            canvas.ExtraCursors.Add(new ExtraCursorCaret { Line = 1, Col = 4, AnchorLine = -1, AnchorCol = -1 });

            PressLineEnd(canvas, isMac, shift: true);
            Assert.Empty(canvas.ExtraCursors);
            Assert.True(tab.HasSelection());
            Assert.Equal(1, tab.SelectionAnchorLine);
            Assert.Equal(4, tab.SelectionAnchorCol);
            Assert.Equal(1, tab.CursorLine);
            Assert.Equal(20, tab.CursorCol);

            // 8c: Multi-cursor with WordWrap ON on two different long lines:
            // Both primary cursor and secondary cursor must stop at their visual wrap row end on 1st press,
            // and advance to their model line end on 2nd press!
            string long0 = "    var a = " + string.Join(" + ", Enumerable.Range(0, 30).Select(i => "alpha" + i)) + ";";
            string long1 = "    var b = " + string.Join(" + ", Enumerable.Range(0, 30).Select(i => "bravo" + i)) + ";";
            tab.Lines.Clear();
            tab.Lines.Add(long0);
            tab.Lines.Add(long1);
            tab.WordWrap = true;
            RenderCanvas(canvas, 650, 500);
            var wrapRows0 = canvas.LastFrameRows.Where(r => r.DocLine == 0).ToList();
            var wrapRows1 = canvas.LastFrameRows.Where(r => r.DocLine == 1).ToList();
            Assert.True(wrapRows0.Count >= 2);
            Assert.True(wrapRows1.Count >= 2);

            tab.ClearSelection();
            tab.CursorLine = 0;
            tab.CursorCol = 6;
            canvas.ExtraCursors.Clear();
            canvas.ExtraCursors.Add(new ExtraCursorCaret { Line = 1, Col = 6, AnchorLine = -1, AnchorCol = -1 });

            PressLineEnd(canvas, isMac, shift: true);
            Assert.Equal(wrapRows0[0].End, tab.CursorCol);
            Assert.Single(canvas.ExtraCursors);
            Assert.Equal(wrapRows1[0].End, canvas.ExtraCursors[0].Col);

            PressLineEnd(canvas, isMac, shift: true);
            Assert.Equal(long0.Length, tab.CursorCol);
            Assert.Single(canvas.ExtraCursors);
            Assert.Equal(long1.Length, canvas.ExtraCursors[0].Col);
        }

        // ------------------------------------------------------------------
        // Case 11: Folded Region
        // ------------------------------------------------------------------
        [AvaloniaTheory]
        [InlineData(true)]
        [InlineData(false)]
        public void Case11_FoldedRegion_SelectsToFoldHeaderLineEnd_WithoutUnfolding(bool isMac)
        {
            MacKeymap.ForceMacKeymapOverride = isMac;
            string code =
                "class FoldSample {\n" +
                "    void Method() {\n" +
                "        int hidden1 = 1;\n" +
                "        int hidden2 = 2;\n" +
                "    }\n" +
                "    int after = 3;\n" +
                "}\n";
            var (_, tab, canvas) = OpenEditor($"fold_{isMac}.cs", code);

            // Fold Method() at line 1 (folds lines 2..4)
            tab.CursorLine = 1;
            tab.CursorCol = 4;
            canvas.FoldAtLine(1);
            Assert.True(tab.FoldedBlockRanges.ContainsKey(1));

            // Caret at col 4 on folded header line 1 ("    void Method() {", len 19):
            // Shift+LineEnd selects [4..19] on line 1 and keeps line 1 folded!
            PressLineEnd(canvas, isMac, shift: true);
            Assert.True(tab.FoldedBlockRanges.ContainsKey(1));
            Assert.True(tab.HasSelection());
            Assert.Equal(1, tab.SelectionAnchorLine);
            Assert.Equal(4, tab.SelectionAnchorCol);
            Assert.Equal(1, tab.CursorLine);
            Assert.Equal(19, tab.CursorCol);
            Assert.Equal("void Method() {", tab.GetSelectedText());
        }

        // ------------------------------------------------------------------
        // Keybinding Registry & Command Dispatch Parity
        // ------------------------------------------------------------------
        [AvaloniaFact]
        public void KeybindingRegistry_HasAllLineAndWordSelectCommands_WithVsCodeBindings()
        {
            DefaultCommands.Register();

            var expected = new Dictionary<string, (string mac, string win)>
            {
                ["cursorHome"] = ("cmd+left", "home"),
                ["cursorHomeSelect"] = ("cmd+shift+left", "shift+home"),
                ["cursorEnd"] = ("cmd+right", "end"),
                ["cursorEndSelect"] = ("cmd+shift+right", "shift+end"),
                ["cursorLineStart"] = ("ctrl+a", ""),
                ["cursorLineStartSelect"] = ("ctrl+shift+a", ""),
                ["cursorLineEnd"] = ("ctrl+e", ""),
                ["cursorLineEndSelect"] = ("ctrl+shift+e", ""),
                ["cursorTop"] = ("cmd+up", "ctrl+home"),
                ["cursorTopSelect"] = ("cmd+shift+up", "ctrl+shift+home"),
                ["cursorBottom"] = ("cmd+down", "ctrl+end"),
                ["cursorBottomSelect"] = ("cmd+shift+down", "ctrl+shift+end"),
                ["cursorWordLeft"] = ("alt+left", "ctrl+left"),
                ["cursorWordLeftSelect"] = ("shift+alt+left", "ctrl+shift+left"),
                ["cursorWordEndRight"] = ("alt+right", "ctrl+right"),
                ["cursorWordEndRightSelect"] = ("shift+alt+right", "ctrl+shift+right")
            };

            var macKm = new KeybindingManager(isMac: true);
            var winKm = new KeybindingManager(isMac: false);

            foreach (var kv in expected)
            {
                string cmdId = kv.Key;
                var cmd = CommandRegistry.GetCommand(cmdId);
                Assert.NotNull(cmd);
                Assert.Equal(kv.Value.mac, cmd.DefaultKeyMac);
                Assert.Equal(kv.Value.win, cmd.DefaultKeyWin);

                var macEntry = macKm.Defaults.FirstOrDefault(b => b.Command == cmdId);
                Assert.NotNull(macEntry);
                Assert.Equal(kv.Value.mac, macEntry.Key);

                var winEntry = winKm.Defaults.FirstOrDefault(b => b.Command == cmdId);
                if (string.IsNullOrEmpty(kv.Value.win))
                {
                    Assert.Null(winEntry);
                }
                else
                {
                    Assert.NotNull(winEntry);
                    Assert.Equal(kv.Value.win, winEntry.Key);
                }
            }

            // Also verify KeybindingManager.Dispatch matches KeyStringParser output ("shift+cmd+right")
            // and executes the bound commands on MainWindow's active editor canvas.
            var (w, tab, _) = OpenEditor("km_dispatch.cs", "    alpha beta gamma;\nsecond line\n");
            w.KeybindingManager.SetPlatform(isMac: true);

            tab.ClearSelection();
            tab.CursorLine = 0;
            tab.CursorCol = 10; // before "beta"
            string parsedCmdShiftRight = KeyStringParser.FromKeyName(Key.Right.ToString(), (int)(KeyModifiers.Meta | KeyModifiers.Shift));
            Assert.Equal("shift+cmd+right", parsedCmdShiftRight);
            Assert.True(w.KeybindingManager.ExecuteCommand("cursorEndSelect"));
            Assert.Equal(10, tab.SelectionAnchorCol);
            Assert.Equal(21, tab.CursorCol);
            Assert.Equal("beta gamma;", tab.GetSelectedText());

            Assert.True(w.KeybindingManager.ExecuteCommand("cursorHomeSelect"));
            Assert.Equal(10, tab.SelectionAnchorCol);
            Assert.Equal(4, tab.CursorCol);
            Assert.Equal("alpha ", tab.GetSelectedText());

            Assert.True(w.KeybindingManager.ExecuteCommand("cursorLineStartSelect"));
            Assert.Equal(10, tab.SelectionAnchorCol);
            Assert.Equal(0, tab.CursorCol);
            Assert.Equal("    alpha ", tab.GetSelectedText());

            Assert.True(w.KeybindingManager.ExecuteCommand("cursorLineEndSelect"));
            Assert.Equal(10, tab.SelectionAnchorCol);
            Assert.Equal(21, tab.CursorCol);

            tab.ClearSelection();
            tab.CursorLine = 0;
            tab.CursorCol = 4;
            Assert.True(w.KeybindingManager.ExecuteCommand("cursorWordEndRightSelect"));
            Assert.Equal(4, tab.SelectionAnchorCol);
            Assert.Equal(9, tab.CursorCol);
            Assert.Equal("alpha", tab.GetSelectedText());

            Assert.True(w.KeybindingManager.ExecuteCommand("cursorWordLeftSelect"));
            Assert.False(tab.HasSelection());
            Assert.Equal(4, tab.CursorCol);
        }
    }
}
