using System;
using System.IO;
using System.Linq;
using System.Text;
using Avalonia;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Media.Imaging;
using codeLess.AvaloniaApp;
using codeLess.Studio;
using Xunit;

namespace codeLess.Avalonia.Tests
{
    /// <summary>
    /// Avalonia editor, QA round 3: VS Code scrollbar + overview ruler (#28), undo restoring carets (#29), inline diff
    /// "deleted lines" zone (#30), word-based suggestions (#31), multi-caret auto-closing (#33 parity).
    /// </summary>
    [Collection("SequentialWorkbench")]
    public class EditorQaRound3HeadlessTests : IDisposable
    {
        private readonly string dir;
        private readonly string settingsPath;
        private readonly string settingsBackup;
        private readonly bool hadSettings;
        private readonly int savedTab = 4;
        private readonly bool savedSpaces = AvaloniaVirtualEditorCanvas.InsertSpacesSetting;

        public EditorQaRound3HeadlessTests()
        {
            settingsPath = IdePersistentSettings.GetSettingsFilePath();
            hadSettings = File.Exists(settingsPath);
            settingsBackup = hadSettings ? File.ReadAllText(settingsPath) : null;
            try { if (hadSettings) File.Delete(settingsPath); } catch { }
            dir = Path.Combine(Path.GetTempPath(), "codeLess_QaR3_" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(dir);
        }

        public void Dispose()
        {
            
            AvaloniaVirtualEditorCanvas.InsertSpacesSetting = savedSpaces;
            try
            {
                if (hadSettings) File.WriteAllText(settingsPath, settingsBackup);
                else if (File.Exists(settingsPath)) File.Delete(settingsPath);
            }
            catch { }
            try { Directory.Delete(dir, true); } catch { }
        }

        private (MainWindow, EditorTabBuffer) Open(string name, string content)
        {
            string file = Path.Combine(dir, name);
            File.WriteAllText(file, content);
            TestTrust.Trust(dir); // a Restricted Mode banner would change the editor height these layout tests assert on
            var w = new MainWindow(dir);
            w.Show();
            var tab = w.OpenFileInTab(file, preview: false);
            return (w, tab);
        }

        private static void Close(MainWindow w)
        {
            w.SuppressUnsavedPrompts = true;
            w.Close();
        }

        private static void RenderOnce(AvaloniaVirtualEditorCanvas canvas, double w = 900, double h = 600)
        {
            canvas.Measure(new Size(w, h));
            canvas.Arrange(new Rect(0, 0, w, h));
            var target = new RenderTargetBitmap(new PixelSize(10, 10));
            using (var ctx = target.CreateDrawingContext())
                canvas.Render(ctx);
        }

        private static void Type(AvaloniaVirtualEditorCanvas canvas, string text)
        {
            foreach (char c in text)
                canvas.RaiseEvent(new TextInputEventArgs { RoutedEvent = InputElement.TextInputEvent, Text = c.ToString() });
        }

        private static string Lines(int n, Func<int, string> f)
        {
            var sb = new StringBuilder();
            for (int i = 0; i < n; i++) sb.Append(f(i)).Append('\n');
            return sb.ToString();
        }

        // ------------------------------------------------------------------ #28 scrollbar
        [AvaloniaFact]
        public void VerticalScrollbar_ReservedAtTheRightEdge_ProportionalThumb_JumpPageDrag_FoldAndWrapAware()
        {
            var (w, tab) = Open("big.cs", Lines(300, i => "int v" + i + " = " + i + ";"));
            try
            {
                var canvas = w.PrimaryEditor.Canvas;
                RenderOnce(canvas, 900, 600);
                var g = canvas.VerticalScrollbar;
                Assert.True(g.Visible);
                Assert.False(canvas.IsScrollbarSliderShown); // auto-hide: no pointer over the editor, no recent scroll
                Assert.Equal(900 - EditorScrollbar.VerticalWidth, canvas.VerticalScrollbarRect.X);
                int total = canvas.TotalVisualRows;
                Assert.Equal(tab.Lines.Count, total);
                int vis = (int)(600 / canvas.LineHeight);
                Assert.Equal(600.0 * vis / (total - 1 + vis), g.ThumbLength, 3);
                Assert.Equal(0, g.ThumbStart, 3);
                // the text area stops before the minimap and the scrollbar (no clicks / text under them)
                Assert.False(canvas.TryHitTestText(new Point(895, 20), out _, out _));

                // click on the track near the bottom: jump (VS Code default), the slider centres on the click
                Assert.True(canvas.ClickScrollbarAt(new Point(893, 590)));
                RenderOnce(canvas, 900, 600);
                Assert.True(tab.ScrollTopLine > 250, "top " + tab.ScrollTopLine);
                Assert.InRange(canvas.VerticalScrollbar.ThumbStart + canvas.VerticalScrollbar.ThumbLength / 2, 560, 600);
                // Alt+click above the slider: one page up
                int before = tab.ScrollTopLine;
                Assert.True(canvas.ClickScrollbarAt(new Point(893, 5), alt: true));
                Assert.Equal(before - (vis - 1), tab.ScrollTopLine);
                // drag the slider to the top
                RenderOnce(canvas, 900, 600);
                canvas.DragVerticalSliderTo(0);
                Assert.Equal(0, tab.ScrollTopLine);
                RenderOnce(canvas, 900, 600);
                Assert.True(canvas.IsScrollbarSliderShown); // shown right after scrolling

                // folding hides rows: the scroll range follows
                tab.FoldedBlockRanges[10] = 109;
                RenderOnce(canvas, 900, 600);
                Assert.Equal(total - 99, canvas.TotalVisualRows);
                canvas.ScrollToVisualRow(11);
                Assert.Equal(110, tab.ScrollTopLine); // row 11 is the first line after the fold
                tab.FoldedBlockRanges.Clear();

                // word wrap: rows, not lines
                tab.Lines[5] = "var s = \"" + new string('x', 400) + "\";";
                tab.SyncContentFromLines();
                w.SimulateShortcut(Key.Z, KeyModifiers.Alt);
                Assert.True(tab.WordWrap);
                RenderOnce(canvas, 900, 600);
                int wrapRows = canvas.TotalVisualRows - (tab.Lines.Count - 1);
                Assert.True(wrapRows > 3, "wrapped rows: " + wrapRows);
                canvas.ScrollToVisualRow(7);
                Assert.Equal(5, tab.ScrollTopLine);
                Assert.Equal(2, tab.ScrollTopWrapRow); // inside the wrapped line
            }
            finally { Close(w); }
        }

        [AvaloniaFact]
        public void OverviewRuler_ShowsProblems_FindMatches_SourceControl_Conflicts_Cursors_InTheirLanes()
        {
            string text = Lines(200, i => i == 120 ? "<<<<<<< HEAD" : i == 122 ? "=======" : i == 124 ? ">>>>>>> theirs" : "line " + i);
            var (w, tab) = Open("r.txt", text);
            try
            {
                var canvas = w.PrimaryEditor.Canvas;
                tab.Diagnostics.Add(new DiagnosticItem { Severity = "Error", Line = 150, Message = "boom" });
                tab.Diagnostics.Add(new DiagnosticItem { Severity = "Warning", Line = 40, Message = "hmm" });
                tab.GutterAddedLines.Add(60); tab.GutterAddedLines.Add(61); tab.GutterAddedLines.Add(62);
                tab.GutterModifiedLines.Add(80);
                tab.GutterDeletedAfterLine.Add(90);
                tab.CursorLine = 30;
                canvas.ExtraCursors.Add(new ExtraCursorCaret { Line = 170, Col = 0, AnchorLine = -1, AnchorCol = -1 });
                w.PrimaryEditor.ShowFindBar(false);
                w.PrimaryEditor.SetFindQuery("line 19");
                RenderOnce(canvas, 900, 600);
                var marks = canvas.LastOverviewRulerMarks;
                Assert.Contains(marks, m => m.Kind == EditorOverviewRuler.KindError && m.Row == 149 && m.Lane == EditorOverviewRuler.LaneRight);
                Assert.Contains(marks, m => m.Kind == EditorOverviewRuler.KindWarning && m.Row == 39 && m.Lane == EditorOverviewRuler.LaneRight);
                Assert.Contains(marks, m => m.Kind == EditorOverviewRuler.KindAdded && m.Row == 60 && m.RowCount == 3 && m.Lane == EditorOverviewRuler.LaneLeft);
                Assert.Contains(marks, m => m.Kind == EditorOverviewRuler.KindModified && m.Row == 80);
                Assert.Contains(marks, m => m.Kind == EditorOverviewRuler.KindDeleted && m.Row == 91);
                Assert.Contains(marks, m => m.Kind == EditorOverviewRuler.KindConflictCurrent && m.Row == 120 && m.Lane == EditorOverviewRuler.LaneFull);
                Assert.Contains(marks, m => m.Kind == EditorOverviewRuler.KindConflictIncoming && m.Row == 122);
                Assert.Contains(marks, m => m.Kind == EditorOverviewRuler.KindCursor && m.Row == 30);
                Assert.Contains(marks, m => m.Kind == EditorOverviewRuler.KindCursor && m.Row == 170);
                var finds = marks.Where(m => m.Kind == EditorOverviewRuler.KindFindMatch).Select(m => m.Row).ToList();
                Assert.Contains(19, finds);
                Assert.Contains(190, finds);
                Assert.All(marks.Where(m => m.Kind == EditorOverviewRuler.KindFindMatch), m => Assert.Equal(EditorOverviewRuler.LaneCenter, m.Lane));
            }
            finally { Close(w); }
        }

        [AvaloniaFact]
        public void HorizontalScrollbar_CoversTheWidestLineOfTheDocument_HiddenWhileWrapping()
        {
            // the widest line is far below the first screen (the old wheel range only measured 400 lines from the top)
            var (w, tab) = Open("h.cs", Lines(900, i => i == 700 ? "var q = \"" + new string('y', 600) + "\";" : "x" + i));
            try
            {
                var canvas = w.PrimaryEditor.Canvas;
                RenderOnce(canvas, 900, 600);
                Assert.True(canvas.HorizontalScrollbar.Visible);
                var r = canvas.HorizontalScrollbarRect;
                Assert.Equal(600 - EditorScrollbar.HorizontalHeight, r.Y);
                Assert.True(r.Right <= 900 - EditorScrollbar.VerticalWidth);
                Assert.True(canvas.ClickScrollbarAt(new Point(r.Right - 2, r.Y + 6)));
                Assert.True(tab.ScrollLeftPx > 600 * canvas.CharWidth * 0.5, "left " + tab.ScrollLeftPx);
                w.SimulateShortcut(Key.Z, KeyModifiers.Alt); // wrap
                RenderOnce(canvas, 900, 600);
                Assert.False(canvas.HorizontalScrollbar.Visible);
                Assert.Equal(0, tab.ScrollLeftPx);
            }
            finally { Close(w); }
        }

        // ------------------------------------------------------------------ #30 deleted-lines zone
        [AvaloniaFact]
        public void InlineDiff_ShowsRemovedLinesAsReadOnlyRows_HitTestCaretScrollAndRejectAreCorrect()
        {
            var (w, tab) = Open("d.txt", "keep1\nold1\nold2\nold3\nkeep2\nkeep3\n");
            try
            {
                var canvas = w.PrimaryEditor.Canvas;
                w.PrimaryEditor.StageProposedDiffContent("keep1\nnew1\nkeep2\nkeep3\n");
                RenderOnce(canvas, 900, 600);
                var rows = canvas.LastFrameRows.ToList();
                // keep1, [old1 old2 old3 deleted], new1, keep2, keep3, ""
                Assert.False(rows[0].IsDeleted);
                Assert.Equal(new[] { "old1", "old2", "old3" }, rows.Skip(1).Take(3).Select(r => r.DeletedText).ToArray());
                Assert.All(rows.Skip(1).Take(3), r => { Assert.True(r.IsDeleted); Assert.Equal(1, r.DocLine); });
                Assert.Equal(1, rows[4].DocLine);
                Assert.False(rows[4].IsDeleted);
                Assert.Equal("keep1\nnew1\nkeep2\nkeep3\n", string.Join("\n", tab.Lines)); // not part of the buffer
                Assert.Equal(tab.Lines.Count + 3, canvas.TotalVisualRows);

                // clicking a deleted row puts the caret at the start of the line below the zone
                double lh = canvas.LineHeight;
                canvas.ClickAtForTest(new Point(AvaloniaVirtualEditorCanvas.GutterWidth + 40, lh * 2.5));
                Assert.Equal(1, tab.CursorLine);
                Assert.Equal(0, tab.CursorCol);
                // Down from keep1 skips the removed rows
                tab.CursorLine = 0; tab.CursorCol = 2;
                w.SimulateShortcut(Key.Down, KeyModifiers.None);
                Assert.Equal(1, tab.CursorLine);
                w.SimulateShortcut(Key.Up, KeyModifiers.None);
                Assert.Equal(0, tab.CursorLine);

                // scrolling steps through the zone row by row
                tab.ScrollTopLine = 1; tab.ScrollTopWrapRow = 0;
                canvas.ScrollByRowsForTest(-1);
                RenderOnce(canvas, 900, 600);
                Assert.True(canvas.LastFrameRows[0].IsDeleted);
                Assert.Equal("old3", canvas.LastFrameRows[0].DeletedText);
                canvas.ScrollByRowsForTest(-3);
                RenderOnce(canvas, 900, 600);
                Assert.Equal(0, tab.ScrollTopLine);
                Assert.False(canvas.LastFrameRows[0].IsDeleted);

                // reject brings the lines back into the buffer and the zone disappears
                w.PrimaryEditor.RejectDiffHunkAtLine(1);
                RenderOnce(canvas, 900, 600);
                Assert.DoesNotContain(canvas.LastFrameRows, r => r.IsDeleted);
                Assert.Equal("keep1\nold1\nold2\nold3\nkeep2\nkeep3\n", tab.Content);
            }
            finally { Close(w); }
        }

        [AvaloniaFact]
        public void InlineDiff_DeletedZone_ClickMapsToAnchor_EndOfFileZone_WrapsLongRemovedLines()
        {
            string longOld = "old " + string.Join(" ", Enumerable.Range(0, 80).Select(i => "word" + i));
            var (w, tab) = Open("e.txt", "a\n" + longOld + "\nb\nc\nlast1\nlast2\n");
            try
            {
                var canvas = w.PrimaryEditor.Canvas;
                w.PrimaryEditor.StageProposedDiffContent("a\nb\nc\n");
                RenderOnce(canvas, 700, 600);
                var rows = canvas.LastFrameRows.ToList();
                Assert.Equal(longOld, rows[1].DeletedText);
                // click on the deleted row: caret on line 1 ("b") column 0, never inside removed text
                double lh = canvas.LineHeight;
                canvas.ClickAtForTest(new Point(AvaloniaVirtualEditorCanvas.GutterWidth + 60, lh * 1.5));
                Assert.Equal(1, tab.CursorLine);
                Assert.Equal(0, tab.CursorCol);
                // removed lines before the final (empty) line sit above it
                Assert.Contains(rows, r => r.IsDeleted && !r.DeletedAfterLast && r.DeletedText == "last1" && r.DocLine == 3);
                // word wrap: the long removed line takes several rows
                w.SimulateShortcut(Key.Z, KeyModifiers.Alt);
                RenderOnce(canvas, 700, 600);
                int longRows = canvas.LastFrameRows.Count(r => r.IsDeleted && r.DeletedText == longOld);
                Assert.True(longRows >= 3, "rows " + longRows);
                Assert.True(canvas.TotalVisualRows >= tab.Lines.Count + longRows);
            }
            finally { Close(w); }
        }

        [AvaloniaFact]
        public void InlineDiff_DeletionAtTheVeryEnd_ShowsAfterTheLastLine_ClickGoesToItsEnd()
        {
            var (w, tab) = Open("f.txt", "x\ny\nz");
            try
            {
                var canvas = w.PrimaryEditor.Canvas;
                w.PrimaryEditor.StageProposedDiffContent("x");
                RenderOnce(canvas, 700, 600);
                var rows = canvas.LastFrameRows.ToList();
                Assert.Equal("x", tab.Content);
                Assert.Equal(3, rows.Count);
                Assert.False(rows[0].IsDeleted);
                Assert.True(rows[1].IsDeleted && rows[1].DeletedAfterLast && rows[1].DeletedText == "y");
                Assert.True(rows[2].IsDeleted && rows[2].DeletedText == "z");
                canvas.ClickAtForTest(new Point(AvaloniaVirtualEditorCanvas.GutterWidth + 60, canvas.LineHeight * 2.5));
                Assert.Equal(0, tab.CursorLine);
                Assert.Equal(1, tab.CursorCol);
                // the overview ruler marks the pending change
                Assert.Contains(canvas.LastOverviewRulerMarks, m => m.Kind == EditorOverviewRuler.KindProposed);
            }
            finally { Close(w); }
        }

        // ------------------------------------------------------------------ #33 multi-caret auto-closing (parity with WinForms engine)
        [AvaloniaFact]
        public void MultiCaret_AutoClosedPairs_AreOvertypedAndDeletedAtEveryCaret()
        {
            var (w, tab) = Open("m.cs", "a = f\nb = g\n");
            try
            {
                var canvas = w.PrimaryEditor.Canvas;
                tab.CursorLine = 0; tab.CursorCol = 5;
                canvas.ExtraCursors.Add(new ExtraCursorCaret { Line = 1, Col = 5, AnchorLine = -1, AnchorCol = -1 });
                Type(canvas, "(x)");
                Assert.Equal("a = f(x)", tab.Lines[0]); // was "a = f(x))": the auto-inserted closer was not overtyped
                Assert.Equal("b = g(x)", tab.Lines[1]);
                Assert.Equal(8, tab.CursorCol);
                Assert.Single(canvas.ExtraCursors);
                Type(canvas, "[");
                Assert.Equal("a = f(x)[]", tab.Lines[0]);
                w.SimulateShortcut(Key.Back, KeyModifiers.None);
                Assert.Equal("a = f(x)", tab.Lines[0]);
                Assert.Equal("b = g(x)", tab.Lines[1]);
                // one undo step per typed group; undo brings back both carets
                canvas.PerformUndoRedo(redo: false);
                Assert.Single(canvas.ExtraCursors);
            }
            finally { Close(w); }
        }

        // ------------------------------------------------------------------ #31 word-based suggestions (no language server)
        [AvaloniaFact]
        public void WordSuggestions_OpenWhileTyping_NotInCommentsOrStrings_EnterAccepts_SnippetsExpand()
        {
            var (w, tab) = Open("ws.cs", "class Demo {\n    int foobarCount = 1;\n    \n}\n");
            try
            {
                var canvas = w.PrimaryEditor.Canvas;
                var binding = w.PrimaryLspBinding;
                Assert.NotNull(binding); // the suggest widget exists even without language servers
                tab.CursorLine = 2; tab.CursorCol = 4;
                Type(canvas, "foo");
                Assert.True(binding.IsCompletionVisible);
                Assert.Equal("foobarCount", binding.Session.Selected.Label);
                w.SimulateShortcut(Key.Enter, KeyModifiers.None);
                Assert.Equal("    foobarCount", tab.Lines[2]);
                Assert.False(binding.IsCompletionVisible);

                // not inside a comment or a string (VS Code quickSuggestions comments/strings off)
                Type(canvas, " // foo");
                Assert.False(binding.IsCompletionVisible);
                tab.Lines[2] = "    var s = \"\";";
                tab.SyncContentFromLines();
                tab.CursorCol = 13;
                Type(canvas, "foo");
                Assert.False(binding.IsCompletionVisible);
                // ...but Ctrl+Space works there
                w.SimulateShortcut(Key.Space, KeyModifiers.Control);
                Assert.True(binding.IsCompletionVisible);
                w.SimulateShortcut(Key.Escape, KeyModifiers.None);
                Assert.False(binding.IsCompletionVisible);

                // snippets are in the same list and expand with the snippet engine
                tab.Lines[2] = "    ";
                tab.SyncContentFromLines();
                tab.CursorCol = 4;
                Type(canvas, "tryc");
                Assert.True(binding.IsCompletionVisible);
                Assert.Equal("tryc", binding.Session.Selected.Label);
                Assert.True(binding.Session.Selected.IsSnippet);
                w.SimulateShortcut(Key.Tab, KeyModifiers.None);
                Assert.StartsWith("    try {", tab.Lines[2]);
                Assert.Contains(tab.Lines, l => l.Contains("catch (Exception ex)"));
            }
            finally { Close(w); }
        }

        [AvaloniaFact]
        public void WordSuggestions_UseOpenDocumentsOfTheSameLanguageOnly_AndBackspaceDoesNotOpen()
        {
            var (w, tab) = Open("a.cs", "class A {\n    \n}\n");
            try
            {
                string other = Path.Combine(dir, "b.cs");
                File.WriteAllText(other, "class B { int sharedHelperValue; }\n");
                string py = Path.Combine(dir, "c.py");
                File.WriteAllText(py, "sharedPythonOnly = 1\n");
                w.OpenFileInTab(other, preview: false);
                w.OpenFileInTab(py, preview: false);
                var tabA = w.OpenTabs.First(t => t.FileName == "a.cs");
                w.ActivateTab(tabA);
                var canvas = w.PrimaryEditor.Canvas;
                var binding = w.PrimaryLspBinding;
                tabA.CursorLine = 1; tabA.CursorCol = 4;
                Type(canvas, "shared");
                Assert.True(binding.IsCompletionVisible);
                var labels = binding.Session.Visible.Select(i => i.Label).ToList();
                Assert.Contains("sharedHelperValue", labels);
                Assert.DoesNotContain("sharedPythonOnly", labels);
                w.SimulateShortcut(Key.Escape, KeyModifiers.None);
                w.SimulateShortcut(Key.Back, KeyModifiers.None);
                Assert.False(binding.IsCompletionVisible); // deleting never opens the list (VS Code)
            }
            finally { Close(w); }
        }

        // ------------------------------------------------------------------ #28 scrollbars of other owner-drawn surfaces
        [AvaloniaFact]
        public void ExplorerTreeAndTerminal_HaveTheSameVsCodeScrollbar_DragAndJump()
        {
            for (int i = 0; i < 120; i++) File.WriteAllText(Path.Combine(dir, "file" + i.ToString("D3") + ".txt"), "x");
            var w = new MainWindow(dir);
            w.Show();
            try
            {
                var tree = w.ExplorerTree;
                tree.Measure(new Size(260, 300));
                tree.Arrange(new Rect(0, 0, 260, 300));
                var target = new RenderTargetBitmap(new PixelSize(10, 10));
                using (var ctx = target.CreateDrawingContext()) tree.Render(ctx);
                Assert.True(tree.Scrollbar.IsVisible);
                Assert.Equal(260 - SlimScrollbar.DefaultWidth, tree.Scrollbar.Bounds.X);
                // a click low on the track jumps there (VS Code), the row under the slider is not selected
                Assert.True(tree.Scrollbar.Press(new Point(255, 290), out double off));
                tree.Scrollbar.Release();
                Assert.True(off > 1000, "offset " + off);

                // terminal scrollback: the bar sits in the right padding (no column is hidden) and jumps to the top
                var term = new AvaloniaTerminalEmulatorControl();
                for (int i = 0; i < 300; i++) term.FallbackBuffer.FeedText("line " + i + "\r\n");
                term.RenderGridForTest(800, 300);
                Assert.True(term.ScrollbackScrollbar.IsVisible);
                Assert.True(term.ScrollbackScrollbar.Bounds.X >= 800 - 14 - 4);
                Assert.Equal(0, term.ScrollbackOffset);
                Assert.True(term.ClickScrollbackScrollbarAt(new Point(795, 2)));
                Assert.Equal(term.FallbackBuffer.ScrollbackCount, term.ScrollbackOffset);
                Assert.True(term.ClickScrollbackScrollbarAt(new Point(795, 298)));
                Assert.Equal(0, term.ScrollbackOffset);
            }
            finally { Close(w); }
        }
    }
}
