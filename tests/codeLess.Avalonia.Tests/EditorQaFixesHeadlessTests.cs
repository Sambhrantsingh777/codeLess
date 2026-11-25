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
    /// Avalonia editor behaviour for the QA fixes: real soft wrap (#11), Tab / indentation settings and detection
    /// (#12), Allman folding through the UI (#13), and VS Code editing behaviours found while fixing them
    /// (auto-closing pairs, selection collapse, sticky Up/Down column, Enter between brackets).
    /// </summary>
    [Collection("SequentialWorkbench")]
    public class EditorQaFixesHeadlessTests : IDisposable
    {
        private readonly string dir;
        private readonly int savedTab = 4;
        private readonly bool savedSpaces = AvaloniaVirtualEditorCanvas.InsertSpacesSetting;
        private readonly string settingsPath;
        private readonly string settingsBackup;
        private readonly bool hadSettings;

        public EditorQaFixesHeadlessTests()
        {
            // default settings (tab size 4, insert spaces) regardless of the machine's saved preferences
            settingsPath = IdePersistentSettings.GetSettingsFilePath();
            hadSettings = File.Exists(settingsPath);
            settingsBackup = hadSettings ? File.ReadAllText(settingsPath) : null;
            try { if (hadSettings) File.Delete(settingsPath); } catch { }
            dir = Path.Combine(Path.GetTempPath(), "codeLess_QaEd_" + Guid.NewGuid().ToString("N").Substring(0, 8));
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

        private static void Type(AvaloniaVirtualEditorCanvas canvas, string text)
        {
            foreach (char c in text)
                canvas.RaiseEvent(new TextInputEventArgs { RoutedEvent = InputElement.TextInputEvent, Text = c.ToString() });
        }

        private static void RenderOnce(AvaloniaVirtualEditorCanvas canvas, double w = 1000, double h = 600)
        {
            canvas.Measure(new Size(w, h));
            canvas.Arrange(new Rect(0, 0, w, h));
            var target = new RenderTargetBitmap(new PixelSize(10, 10));
            using (var ctx = target.CreateDrawingContext())
                canvas.Render(ctx);
        }

        private (MainWindow, EditorTabBuffer) Open(string name, string content)
        {
            string file = Path.Combine(dir, name);
            Directory.CreateDirectory(Path.GetDirectoryName(file));
            File.WriteAllText(file, content);
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

        // ------------------------------------------------------------------ #11 word wrap
        [AvaloniaFact]
        public void WordWrap_WrapsAtViewport_GutterOnce_RowNavigation_HomeEnd_HitTest_NoHorizontalScroll()
        {
            string longLine = "    var total = " + string.Join(" + ", Enumerable.Range(0, 60).Select(i => "value" + i)) + ";";
            var (w, tab) = Open("Wrap.cs", "class A\n" + longLine + "\nend();\n");
            var canvas = w.PrimaryEditor.Canvas;
            canvas.FontSizePx = 13;
            Assert.False(tab.WordWrap);
            RenderOnce(canvas, 700, 500);
            Assert.All(canvas.LastFrameRows, r => Assert.True(r.IsLastRow && r.Row == 0));

            w.SimulateShortcut(Key.Z, KeyModifiers.Alt); // View > Word Wrap
            Assert.True(tab.WordWrap);
            RenderOnce(canvas, 700, 500);
            var rows1 = canvas.LastFrameRows.Where(r => r.DocLine == 1).ToList();
            Assert.True(rows1.Count >= 4, "rows: " + rows1.Count);
            Assert.Equal(0, rows1[0].StartVcol);
            Assert.All(rows1.Skip(1), r => Assert.Equal(4, r.StartVcol)); // wrapped indent "same"
            Assert.Equal(longLine.Length, rows1.Last().End);
            for (int i = 1; i < rows1.Count; i++) Assert.Equal(rows1[i - 1].End, rows1[i].Start);
            Assert.Equal(2, canvas.LastFrameRows[rows1.Count + 1].DocLine); // the next line follows its last row
            int rowCols = canvas.WrapColumns;
            foreach (var r in rows1)
                Assert.True(r.StartVcol + longLine.Substring(r.Start, r.End - r.Start).TrimEnd().Length <= rowCols);

            // Down moves by visual row inside the wrapped line, keeping the column
            tab.CursorLine = 1; tab.CursorCol = 10;
            w.SimulateShortcut(Key.Down, KeyModifiers.None);
            Assert.Equal(1, tab.CursorLine);
            Assert.InRange(tab.CursorCol, rows1[1].Start, rows1[1].End);
            w.SimulateShortcut(Key.Up, KeyModifiers.None);
            Assert.Equal(1, tab.CursorLine);
            Assert.Equal(10, tab.CursorCol);
            for (int i = 0; i < rows1.Count; i++) w.SimulateShortcut(Key.Down, KeyModifiers.None);
            Assert.Equal(2, tab.CursorLine);

            // End: end of the visual row first, then end of the line; Home: row start, then first non-blank, then column 0
            tab.CursorLine = 1; tab.CursorCol = rows1[1].Start + 3;
            w.SimulateShortcut(Key.End, KeyModifiers.None);
            Assert.Equal(rows1[1].End, tab.CursorCol);
            RenderOnce(canvas, 700, 500);
            int rowIdx = canvas.LastFrameRows.ToList().FindIndex(r => r.DocLine == 1 && r.Row == 1);
            Assert.Equal(rowIdx * canvas.LineHeight, canvas.CaretRect.Y, 1); // caret drawn at the end of that row
            w.SimulateShortcut(Key.End, KeyModifiers.None);
            Assert.Equal(longLine.Length, tab.CursorCol);
            w.SimulateShortcut(Key.Home, KeyModifiers.None);
            Assert.Equal(rows1.Last().Start, tab.CursorCol);
            w.SimulateShortcut(Key.Home, KeyModifiers.None);
            Assert.Equal(4, tab.CursorCol);
            w.SimulateShortcut(Key.Home, KeyModifiers.None);
            Assert.Equal(0, tab.CursorCol);

            // hit-testing a continuation row maps to the right character
            Assert.True(canvas.TryHitTestText(new Point(AvaloniaVirtualEditorCanvas.GutterWidth + 8 + canvas.CharWidth * 4.5, canvas.LineHeight * 2.5), out int hl, out int hc));
            Assert.Equal(1, hl);
            Assert.Equal(rows1[1].Start, hc);

            // typing at the end of the long line never scrolls horizontally while wrapping
            tab.CursorCol = longLine.Length;
            Type(canvas, " + more");
            Assert.Equal(0, tab.ScrollLeftPx);
            RenderOnce(canvas, 700, 500);

            // a wider viewport re-wraps into fewer rows; only lines not laid out before are computed
            RenderOnce(canvas, 1400, 500);
            int wideRows = canvas.LastFrameRows.Count(r => r.DocLine == 1);
            Assert.True(wideRows < rows1.Count, wideRows + " vs " + rows1.Count);
            long computed = canvas.WrapComputations;
            RenderOnce(canvas, 1400, 500);
            Assert.Equal(computed, canvas.WrapComputations);

            w.SimulateShortcut(Key.Z, KeyModifiers.Alt);
            Assert.False(tab.WordWrap);
            RenderOnce(canvas, 700, 500);
            Assert.Equal(1, canvas.LastFrameRows.Count(r => r.DocLine == 1));
            Close(w);
        }

        [AvaloniaFact]
        public void WordWrap_SettingAppliesToOpenEditors_AndScrollsByRows()
        {
            var sb = new StringBuilder();
            for (int i = 0; i < 40; i++) sb.Append("line ").Append(i).Append(' ').Append(new string('x', 200)).Append('\n');
            var (w, tab) = Open("Many.txt", sb.ToString());
            var canvas = w.PrimaryEditor.Canvas;
            var s = new StudioSettings { WordWrap = true, TabSize = 4, InsertSpaces = true };
            w.GetType().GetMethod("ApplyEditorIndentAndWrapSettings", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance).Invoke(w, new object[] { s });
            Assert.True(tab.WordWrap);
            RenderOnce(canvas, 800, 400);
            int rowsOfLine0 = canvas.LastFrameRows.Count(r => r.DocLine == 0);
            Assert.True(rowsOfLine0 > 1);
            canvas.ScrollLines(1); // one visual row, not one document line
            RenderOnce(canvas, 800, 400);
            Assert.Equal(0, canvas.LastFrameRows[0].DocLine);
            Assert.Equal(1, canvas.LastFrameRows[0].Row);
            // caret below the viewport scrolls into view by rows
            tab.CursorLine = 30; tab.CursorCol = 0;
            canvas.EnsureCursorVisible();
            RenderOnce(canvas, 800, 400);
            Assert.Contains(canvas.LastFrameRows, r => r.DocLine == 30);
            Close(w);
        }

        // ------------------------------------------------------------------ #12 tab / indentation
        [AvaloniaFact]
        public void Tab_HonoursDetectedIndentation_Settings_AndEditorConfig()
        {
            // 2-space JavaScript: detected on open, shown in the status bar
            var (w, tab) = Open("two.js", "function f() {\n  if (a) {\n    b();\n  }\n}\n\n");
            var canvas = w.PrimaryEditor.Canvas;
            Assert.Equal(2, tab.CustomTabSize);
            Assert.True(tab.CustomInsertSpaces);
            Assert.Equal("Spaces: 2", w.StatusIndentationText);
            tab.CursorLine = 5; tab.CursorCol = 0;
            w.SimulateShortcut(Key.Tab, KeyModifiers.None);
            Assert.Equal("  ", tab.Lines[5]);
            Type(canvas, "x");
            w.SimulateShortcut(Key.Tab, KeyModifiers.None);
            Assert.Equal("  x ", tab.Lines[5]); // to the next tab stop of the caret's column
            // multi-line selection: Tab indents, Shift+Tab outdents by one 2-space level
            tab.SelectionStartLine = 1; tab.SelectionStartCol = 0; tab.CursorLine = 3; tab.CursorCol = 3;
            w.SimulateShortcut(Key.Tab, KeyModifiers.None);
            Assert.Equal("    if (a) {", tab.Lines[1]);
            Assert.Equal("      b();", tab.Lines[2]);
            Assert.Equal("    }", tab.Lines[3]);
            w.SimulateShortcut(Key.Tab, KeyModifiers.Shift);
            Assert.Equal("  if (a) {", tab.Lines[1]);
            Assert.Equal("    b();", tab.Lines[2]);
            // Enter after "{" indents one level; between "{}" splits into three lines
            tab.ClearSelection();
            tab.CursorLine = 1; tab.CursorCol = tab.Lines[1].Length;
            w.SimulateShortcut(Key.Enter, KeyModifiers.None);
            Assert.Equal("    ", tab.Lines[2]);
            Assert.Equal(4, tab.CursorCol);
            // Backspace in spaces-only indentation goes back one tab stop
            w.SimulateShortcut(Key.Back, KeyModifiers.None);
            Assert.Equal("  ", tab.Lines[2]);
            Type(canvas, "g({");
            Assert.Equal("  g({})", tab.Lines[2]); // auto-closed pairs
            w.SimulateShortcut(Key.Enter, KeyModifiers.None);
            Assert.Equal("  g({", tab.Lines[2]);
            Assert.Equal("    ", tab.Lines[3]);
            Assert.Equal("  })", tab.Lines[4]);
            Assert.Equal(3, tab.CursorLine);
            Close(w);

            // tab-indented Go file: Tab inserts a tab character; status "Tab Size: 4"
            var (w2, t2) = Open("tabs.go", "package m\n\nfunc f() {\n\tx()\n\tif a {\n\t\ty()\n\t}\n}\n");
            Assert.False(t2.CustomInsertSpaces);
            Assert.Equal("Tab Size: 4", w2.StatusIndentationText);
            t2.CursorLine = 1; t2.CursorCol = 0;
            w2.SimulateShortcut(Key.Tab, KeyModifiers.None);
            Assert.Equal("\t", t2.Lines[1]);
            // switching the file to spaces from the status bar picker
            w2.SetActiveIndentation(true, 3);
            Assert.Equal("Spaces: 3", w2.StatusIndentationText);
            t2.CursorLine = 0; t2.CursorCol = 0;
            w2.SimulateShortcut(Key.Tab, KeyModifiers.None);
            Assert.Equal("   package m", t2.Lines[0]);
            w2.ConvertActiveIndentation(true);
            Assert.Equal("   x()", t2.Lines[3]);
            Close(w2);

            // .editorconfig beats detection and settings
            File.WriteAllText(Path.Combine(dir, ".editorconfig"), "root = true\n[*.py]\nindent_style = space\nindent_size = 3\n");
            var (w3, t3) = Open("ec.py", "def f():\n\tpass\n");
            Assert.Equal(3, t3.CustomTabSize);
            Assert.True(t3.CustomInsertSpaces);
            t3.CursorLine = 1; t3.CursorCol = t3.Lines[1].Length;
            w3.SimulateShortcut(Key.Enter, KeyModifiers.None);
            Assert.Equal("", t3.Lines[2]); // Python: dedent after "pass"
            t3.CursorLine = 0; t3.CursorCol = t3.Lines[0].Length;
            w3.SimulateShortcut(Key.Enter, KeyModifiers.None);
            Assert.Equal("   ", t3.Lines[1]); // indent after ":" uses the .editorconfig size
            Close(w3);

            // no evidence in the file: the global settings apply (tabs)
            var (w4, t4) = Open("plain.txt", "alpha\nbeta\n");
            AvaloniaVirtualEditorCanvas.InsertSpacesSetting = false;
            t4.CursorLine = 0; t4.CursorCol = 0;
            w4.SimulateShortcut(Key.Tab, KeyModifiers.None);
            Assert.Equal("\talpha", t4.Lines[0]);
            Close(w4);
        }

        // ------------------------------------------------------------------ #13 folding through the UI
        [AvaloniaFact]
        public void Folding_AllmanHeader_HidesBlock_CaretSkipsIt_StaysAttachedAfterEdits()
        {
            var (w, tab) = Open("Allman.cs",
                "namespace N\n{\n    class C\n    {\n        void F()\n        {\n            a();\n            b();\n        }\n\n        void G()\n        {\n            c();\n        }\n    }\n}\n");
            var canvas = w.PrimaryEditor.Canvas;
            Assert.Equal(7, canvas.GetFoldEndLine(4));
            canvas.FoldAtLine(4);
            Assert.Equal(7, tab.FoldedBlockRanges[4]);
            RenderOnce(canvas);
            var docs = canvas.LastFrameRows.Select(r => r.DocLine).ToList();
            Assert.DoesNotContain(5, docs);
            Assert.DoesNotContain(7, docs);
            Assert.Contains(8, docs); // "}" stays visible
            // Down from the header skips the hidden lines
            tab.CursorLine = 4; tab.CursorCol = 8;
            w.SimulateShortcut(Key.Down, KeyModifiers.None);
            Assert.Equal(8, tab.CursorLine);
            w.SimulateShortcut(Key.Up, KeyModifiers.None);
            Assert.Equal(4, tab.CursorLine);
            // a line inserted above shifts the fold with its header
            tab.CursorLine = 0; tab.CursorCol = 0;
            w.SimulateShortcut(Key.Enter, KeyModifiers.Control | KeyModifiers.Shift);
            Assert.True(tab.FoldedBlockRanges.ContainsKey(5));
            Assert.Equal(8, tab.FoldedBlockRanges[5]);
            // moving the caret into the folded block (Go to Line) expands it, like VS Code
            w.PrimaryEditor.GoToLine(8, 1);
            Assert.False(tab.FoldedBlockRanges.ContainsKey(5));
            // Fold All / Unfold All
            canvas.FoldAll();
            Assert.True(tab.FoldedBlockRanges.ContainsKey(1)); // "namespace N" (line 1 after the inserted line)
            canvas.UnfoldAll();
            Assert.Empty(tab.FoldedBlockRanges);
            Close(w);
        }

        [AvaloniaFact]
        public void Folding_SessionModelMatchesPureComputation_OnRealisticFile()
        {
            var sb = new StringBuilder();
            sb.Append("using System;\nusing System.Collections.Generic;\nusing System.Linq;\n\nnamespace Acme.Orders\n{\n");
            sb.Append("    /// <summary>Validates, prices and dispatches incoming orders.</summary>\n");
            sb.Append("    public sealed class OrderPipeline : IOrderPipeline\n    {\n");
            sb.Append("        private readonly Dictionary<string, List<Order>> pending = new Dictionary<string, List<Order>>();\n\n");
            sb.Append("        public IReadOnlyList<Order> Process(IEnumerable<Order> orders, decimal discount)\n        {\n");
            sb.Append("            var accepted = new List<Order>();\n");
            sb.Append("            foreach (var order in orders.Where(o => o.Lines.Count > 0))\n            {\n");
            for (int i = 0; i < 14; i++)
                sb.Append("                if (order.Lines[" + i + "].Price > Limits[(int)order.Kind]) { accepted.Add(order); Log($\"accepted {order.Id} ({Math.Round(order.Total * discount, 2)})\"); }\n");
            sb.Append("                pending[order.Customer] = new List<Order> { order }; // \"(not a bracket)\" in strings and comments: ) ] }\n");
            sb.Append("            }\n            return accepted;\n        }\n    }\n}\n");
            var (w, tab) = Open("OrderPipeline.cs", sb.ToString());
            var canvas = w.PrimaryEditor.Canvas;
            RenderOnce(canvas);
            canvas.CompleteBackgroundTokenization();
            tab.ScrollTopLine = 17; tab.CursorLine = 20; tab.CursorCol = 30;
            canvas.FlushOccurrenceHighlight();
            RenderOnce(canvas, 1100, 700);
            tab.WordWrap = true; tab.ScrollTopLine = 14;
            RenderOnce(canvas, 1100, 700);
            tab.WordWrap = false; tab.ScrollTopLine = 0; tab.CursorLine = 11; tab.CursorCol = 8;
            canvas.FoldAtLine(14);
            RenderOnce(canvas, 1100, 700);
            Assert.Contains(7, canvas.LastFrameFoldChevronLines);
            Assert.Contains(14, canvas.LastFrameFoldChevronLines);
            var frameModel = EditorFoldingModel.For(tab);
            Assert.True(frameModel.IsFoldable(7), string.Join(",", frameModel.Ranges.Select(r => r.Start + "-" + r.End)));
            var pure = EditorFoldingModel.ComputeFor(tab.Lines, tab.FilePath, 4);
            Assert.Contains(pure, r => r.Start == 7);
            foreach (var r in pure) Assert.Equal(r.End, canvas.GetFoldEndLine(r.Start));
            Close(w);
        }

        // ------------------------------------------------------------------ VS Code editing behaviours
        [AvaloniaFact]
        public void Editing_AutoClosingOvertypeSurround_SelectionCollapse_StickyColumn()
        {
            var (w, tab) = Open("e.ts", "\nlet value = 1;\nx\nlet another = 2;\n");
            var canvas = w.PrimaryEditor.Canvas;
            tab.CursorLine = 0; tab.CursorCol = 0;
            Type(canvas, "f(");
            Assert.Equal("f()", tab.Lines[0]);
            Type(canvas, "a)");                     // overtypes the auto-inserted ")"
            Assert.Equal("f(a)", tab.Lines[0]);
            Assert.Equal(4, tab.CursorCol);
            Type(canvas, ")");                      // a user-typed ")" is inserted
            Assert.Equal("f(a))", tab.Lines[0]);
            Type(canvas, " \"");
            Assert.Equal("f(a)) \"\"", tab.Lines[0]);
            w.SimulateShortcut(Key.Back, KeyModifiers.None); // deletes the empty auto-closed pair
            Assert.Equal("f(a)) ", tab.Lines[0]);
            Type(canvas, "don't");                  // no auto-closed quote after a word character
            Assert.Equal("f(a)) don't", tab.Lines[0]);
            // auto-surround the selection
            tab.SelectionStartLine = 1; tab.SelectionStartCol = 4; tab.CursorLine = 1; tab.CursorCol = 9;
            Type(canvas, "[");
            Assert.Equal("let [value] = 1;", tab.Lines[1]);
            Assert.True(tab.HasSelection());
            Assert.Equal("value", tab.GetSelectedText());
            // Left / Right collapse the selection to its start / end without moving further
            w.SimulateShortcut(Key.Left, KeyModifiers.None);
            Assert.False(tab.HasSelection());
            Assert.Equal(5, tab.CursorCol);
            tab.SelectionStartLine = 1; tab.SelectionStartCol = 5; tab.CursorLine = 1; tab.CursorCol = 10;
            w.SimulateShortcut(Key.Right, KeyModifiers.None);
            Assert.Equal(10, tab.CursorCol);
            // Up / Down keep the desired column across a short line
            tab.CursorLine = 1; tab.CursorCol = 12;
            w.SimulateShortcut(Key.Down, KeyModifiers.None);
            Assert.Equal(2, tab.CursorLine);
            Assert.Equal(1, tab.CursorCol);
            w.SimulateShortcut(Key.Down, KeyModifiers.None);
            Assert.Equal(3, tab.CursorLine);
            Assert.Equal(12, tab.CursorCol);
            // Up on the first line goes to column 0, Down on the last line to its end
            tab.CursorLine = 0; tab.CursorCol = 3;
            w.SimulateShortcut(Key.Up, KeyModifiers.None);
            Assert.Equal(0, tab.CursorCol);
            Close(w);
        }

        [AvaloniaFact]
        public void Editing_MultiCursorClipboard_WholeLinePaste_UndoRestoresCarets_CtrlDWholeWord_BoxSelectionWithTabs()
        {
            var (w, tab) = Open("m.cs", "int a = item;\nint b = items;\nint c = item;\n\tabc\n    abc\n");
            var canvas = w.PrimaryEditor.Canvas;
            // Cmd/Ctrl+D from a caret: whole-word matches only ("items" is skipped), wraps to the start
            tab.CursorLine = 0; tab.CursorCol = 9;
            w.SimulateShortcut(Key.D, MacKeymap.PrimaryModifier);
            Assert.Equal("item", tab.GetSelectedText());
            w.SimulateShortcut(Key.D, MacKeymap.PrimaryModifier);
            Assert.Single(canvas.ExtraCursors);
            Assert.Equal(2, canvas.ExtraCursors[0].Line);
            // copy of several selections: one per line in document order
            Assert.Equal("item\nitem", canvas.GetCopyText(tab));
            // typing replaces every selection; undo restores the text AND both selections (VS Code, QA #29)
            Type(canvas, "row");
            Assert.Equal("int a = row;", tab.Lines[0]);
            Assert.Equal("int c = row;", tab.Lines[2]);
            canvas.PerformUndoRedo(redo: false);
            Assert.Equal("int a = item;", tab.Lines[0]);
            Assert.Equal("item", tab.GetSelectedText());
            Assert.Single(canvas.ExtraCursors);
            Assert.Equal(2, canvas.ExtraCursors[0].Line);
            Assert.True(canvas.ExtraCursors[0].HasSelection);
            Assert.Equal("item\nitem", canvas.GetCopyText(tab));
            canvas.ResetExtraCursors();
            // copy without a selection copies the line; pasting it goes above the caret line (VS Code)
            tab.ClearSelection();
            tab.CursorLine = 1; tab.CursorCol = 3;
            string line = canvas.GetCopyText(tab);
            Assert.Equal("int b = items;\n", line);
            tab.CursorLine = 0; tab.CursorCol = 5;
            canvas.PasteText(line.Replace("\n", "\r\n")); // CRLF from the clipboard is normalized
            Assert.Equal("int b = items;", tab.Lines[0]);
            Assert.Equal("int a = item;", tab.Lines[1]);
            Assert.Equal(1, tab.CursorLine);
            Assert.Equal(5, tab.CursorCol);
            // box selection measures visual columns: with a 4-cell tab, "\tabc" column 5 is the "a" under "    abc"
            w.SetActiveIndentation(true, 4);
            canvas.ApplyColumnBoxSelection(5, 4, 4, 2);
            Assert.Equal(4, tab.CursorLine);                 // the primary caret is the active end
            Assert.Equal("a", tab.GetSelectedText());
            Assert.Equal(1, tab.SelectionStartCol);
            Assert.Single(canvas.ExtraCursors);
            var ec = canvas.ExtraCursors[0];
            Assert.Equal(5, ec.Line);
            Assert.Equal(4, ec.AnchorCol);
            Assert.Equal(5, ec.Col);
            Close(w);
        }

        [AvaloniaFact]
        public void Comments_ToggleWithKeyboard_InCSharpAndHtml()
        {
            var (w, tab) = Open("k.cs", "class A\n{\n    int x;\n}\n");
            tab.CursorLine = 2; tab.CursorCol = 6;
            w.SimulateShortcut(Key.OemQuestion, MacKeymap.PrimaryModifier);
            Assert.Equal("    // int x;", tab.Lines[2]);
            Assert.Equal(9, tab.CursorCol);
            w.SimulateShortcut(Key.OemQuestion, MacKeymap.PrimaryModifier);
            Assert.Equal("    int x;", tab.Lines[2]);
            Close(w);
            var (w2, t2) = Open("k.html", "<div>\n  <p>x</p>\n</div>\n");
            t2.CursorLine = 1; t2.CursorCol = 4;
            w2.SimulateShortcut(Key.OemQuestion, MacKeymap.PrimaryModifier);
            Assert.Equal("  <!-- <p>x</p> -->", t2.Lines[1]);
            Close(w2);
        }
    }
}
