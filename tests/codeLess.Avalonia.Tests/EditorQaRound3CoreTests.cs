using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using codeLess.Studio;
using Xunit;
using Xunit.Abstractions;

namespace codeLess.Avalonia.Tests
{
    /// <summary>
    /// Shared-engine tests for QA round 3 (#28 scrollbar / overview ruler, #29 undo carets, #30 deleted-line zones,
    /// #31 word-based suggestions, #32 wrapped Home/End, #33 multi-cursor auto-close / Tab, #35 background analysis).
    /// Both editors (WinForms and Avalonia) use these models.
    /// </summary>
    [Collection("SequentialWorkbench")]
    public class EditorQaRound3CoreTests
    {
        private readonly ITestOutputHelper output;
        public EditorQaRound3CoreTests(ITestOutputHelper output) { this.output = output; }

        private static EditorTabBuffer NewBuf(string text, string lang = "cs")
        {
            var b = new EditorTabBuffer { FilePath = "/tmp/x." + lang, FileName = "x." + lang, Language = lang, Content = text, SavedContent = text };
            b.SyncLinesFromContent();
            return b;
        }

        // ------------------------------------------------------------------ #29 undo restores carets
        [Fact]
        public void Undo_RestoresSelectionsAndExtraCarets_Redo_RestoresTheStateBeforeUndo()
        {
            var buf = NewBuf("alpha\nbeta\ngamma\n");
            // primary selection "alpha", an extra selection "beta" and a plain extra caret on "gamma"
            buf.SelectionAnchorLine = 0; buf.SelectionAnchorCol = 0; buf.CursorLine = 0; buf.CursorCol = 5;
            buf.ExtraCarets.Add(new EditorCaret { Line = 1, Col = 4, AnchorLine = 1, AnchorCol = 0 });
            buf.ExtraCarets.Add(new EditorCaret { Line = 2, Col = 2 });
            buf.PushUndoSnapshot();
            buf.Lines[0] = "X"; buf.Lines[1] = "X"; buf.Lines[2] = "gaXmma";
            buf.ClearSelection(); buf.CursorCol = 1;
            buf.ExtraCarets[0] = new EditorCaret { Line = 1, Col = 1 };
            buf.ExtraCarets[1] = new EditorCaret { Line = 2, Col = 3 };
            buf.SyncContentFromLines();

            Assert.True(buf.Undo());
            Assert.Equal("alpha", buf.Lines[0]);
            Assert.Equal("alpha", buf.GetSelectedText());
            Assert.Equal(2, buf.ExtraCarets.Count);
            Assert.True(buf.ExtraCarets[0].HasSelection());
            Assert.Equal(1, buf.ExtraCarets[0].AnchorLine);
            Assert.Equal(0, buf.ExtraCarets[0].AnchorCol);
            Assert.Equal(4, buf.ExtraCarets[0].Col);
            Assert.False(buf.ExtraCarets[1].HasSelection());
            Assert.Equal(2, buf.ExtraCarets[1].Line);
            Assert.Equal(2, buf.ExtraCarets[1].Col);

            Assert.True(buf.Redo());
            Assert.Equal("X", buf.Lines[0]);
            Assert.False(buf.HasSelection());
            Assert.Equal(1, buf.CursorCol);
            Assert.Equal(2, buf.ExtraCarets.Count);
            Assert.Equal(3, buf.ExtraCarets[1].Col);
        }

        [Fact]
        public void Undo_UsesTheViewCaretProvider_AndClampsRestoredCarets()
        {
            var buf = NewBuf("one\ntwo\nthree");
            var viewCarets = new List<EditorCaret> { new EditorCaret { Line = 2, Col = 5 } };
            buf.ExtraCaretsForUndo = () => viewCarets;
            buf.CursorLine = 0; buf.CursorCol = 3;
            buf.PushUndoSnapshot();
            buf.Lines.RemoveAt(2);
            buf.SyncContentFromLines();
            viewCarets.Clear();
            Assert.True(buf.Undo());
            Assert.Single(buf.ExtraCarets);
            Assert.Equal(2, buf.ExtraCarets[0].Line);
            Assert.Equal(5, buf.ExtraCarets[0].Col);
            // carets outside the restored text are clamped; a caret on top of the primary is dropped
            var b2 = NewBuf("ab\ncd");
            b2.CursorLine = 1; b2.CursorCol = 2;
            b2.ExtraCaretsForUndo = () => new List<EditorCaret> { new EditorCaret { Line = 0, Col = 99, AnchorLine = 7, AnchorCol = 0 }, new EditorCaret { Line = 1, Col = 2 } };
            b2.PushUndoSnapshot();
            b2.Lines[0] = "abc"; b2.SyncContentFromLines();
            b2.ExtraCaretsForUndo = null;
            Assert.True(b2.Undo());
            Assert.Single(b2.ExtraCarets);
            Assert.Equal(2, b2.ExtraCarets[0].Col);
            Assert.Equal(1, b2.ExtraCarets[0].AnchorLine);
        }

        // ------------------------------------------------------------------ #35 background analysis
        private static string BigCSharp(int lines)
        {
            var sb = new StringBuilder();
            sb.Append("namespace Big {\n");
            int i = 1;
            while (i < lines - 2)
            {
                sb.Append("    public class C").Append(i).Append(" {\n"); i++;
                sb.Append("        public void M").Append(i).Append("(int a) { if (a > 0) { Run(a); } } // TODO tidy\n"); i++;
                sb.Append("    }\n"); i++;
            }
            sb.Append("}\n");
            return sb.ToString();
        }

        [Fact]
        public void BackgroundAnalysis_MatchesTheSynchronousResult_AndDropsStaleResults()
        {
            var buf = NewBuf(BigCSharp(3000));
            var expected = LlmHarnessAndAgentEngine.ComputeBufferAstAndDiagnostics(buf.Lines, buf.Language, buf.FilePath, buf.FileName);
            Assert.True(expected.Symbols.Count > 900);

            // queue the work and the UI posts manually so ordering is deterministic
            var work = new List<Action>();
            var ui = new List<Action>();
            BackgroundBufferAnalysis.RunOverride = a => work.Add(a);
            try
            {
                int applied = 0;
                Assert.False(BackgroundBufferAnalysis.Analyze(buf, a => ui.Add(a), b => applied++));
                Assert.Single(work);
                Assert.Empty(buf.Symbols); // nothing changed on the "UI thread" yet
                work[0]();
                Assert.Single(ui);
                ui[0]();
                Assert.Equal(1, applied);
                Assert.Equal(expected.Symbols.Count, buf.Symbols.Count);
                Assert.Equal(expected.Diagnostics.Count, buf.Diagnostics.Count);
                Assert.True(buf.IsAnalysisCurrent);

                // edited after the snapshot: the result is dropped
                work.Clear(); ui.Clear();
                BackgroundBufferAnalysis.Analyze(buf, a => ui.Add(a), b => applied++);
                buf.Lines[5] = "    public class Renamed {";
                buf.SyncContentFromLines();
                Assert.False(buf.IsAnalysisCurrent);
                work[0](); ui[0]();
                Assert.Equal(1, applied);

                // two requests in flight: only the newest one is applied, even if the older finishes last
                work.Clear(); ui.Clear();
                BackgroundBufferAnalysis.Analyze(buf, a => ui.Add(a), b => applied++);
                BackgroundBufferAnalysis.Analyze(buf, a => ui.Add(a), b => applied++);
                work[1](); work[0]();
                ui[0](); ui[1]();
                Assert.Equal(2, applied);
                Assert.True(buf.IsAnalysisCurrent);
                Assert.Contains(buf.Symbols, s => s.Name == "Renamed");

                // a language switch invalidates an in-flight result too
                work.Clear(); ui.Clear();
                BackgroundBufferAnalysis.Analyze(buf, a => ui.Add(a), b => applied++);
                buf.Language = "py";
                work[0](); ui[0]();
                Assert.Equal(2, applied);
                Assert.False(buf.IsAnalysisCurrent);
            }
            finally { BackgroundBufferAnalysis.RunOverride = null; }

            // small documents stay synchronous (cheaper than a thread hop)
            var small = NewBuf("class A {\n    void M() { }\n}\n");
            Assert.True(BackgroundBufferAnalysis.Analyze(small, a => throw new InvalidOperationException("no post for small docs"), null));
            Assert.Equal(2, small.Symbols.Count);
        }

        [Fact]
        public void BackgroundAnalysis_50kLines_UiThreadCostIsAFractionOfTheSynchronousPass()
        {
            var buf = NewBuf(BigCSharp(50000));
            Assert.True(buf.Lines.Count >= 49990);
            // warm up regexes / JIT
            LlmHarnessAndAgentEngine.AnalyzeBufferAstAndDiagnostics(buf);
            var sw = Stopwatch.StartNew();
            const int runs = 2;
            for (int i = 0; i < runs; i++) LlmHarnessAndAgentEngine.AnalyzeBufferAstAndDiagnostics(buf);
            double syncMs = sw.Elapsed.TotalMilliseconds / runs;

            double uiMs = 0;
            for (int i = 0; i < runs; i++)
            {
                var work = new List<Action>();
                var ui = new List<Action>();
                BackgroundBufferAnalysis.RunOverride = a => work.Add(a);
                try
                {
                    var t0 = Stopwatch.StartNew();
                    BackgroundBufferAnalysis.Analyze(buf, a => ui.Add(a), null);
                    double schedule = t0.Elapsed.TotalMilliseconds;
                    work[0]();                       // "background thread"
                    var t1 = Stopwatch.StartNew();
                    ui[0]();                         // apply on the "UI thread"
                    uiMs += schedule + t1.Elapsed.TotalMilliseconds;
                }
                finally { BackgroundBufferAnalysis.RunOverride = null; }
            }
            uiMs /= runs;
            output.WriteLine("50k lines: synchronous AST pass {0:F1} ms on the UI thread; background: {1:F2} ms UI-thread (snapshot + apply), compute {2:F1} ms off-thread",
                syncMs, uiMs, BackgroundBufferAnalysis.LastComputeMs);
            Console.WriteLine("[QA35] 50k lines: sync {0:F1} ms UI thread -> background {1:F2} ms UI thread (compute {2:F1} ms off-thread)", syncMs, uiMs, BackgroundBufferAnalysis.LastComputeMs);
            Assert.True(uiMs < syncMs / 4, "UI-thread cost " + uiMs + " ms vs sync " + syncMs + " ms");
            Assert.True(buf.IsAnalysisCurrent);
        }
        // ------------------------------------------------------------------ #32 Home / End on wrapped rows
        [Fact]
        public void WrappedHomeEnd_RowFirst_ThenLine_WithRowEndAffinity()
        {
            string line = "    alpha beta gamma delta epsilon zeta eta theta";
            var wl = EditorWordWrap.Compute(line, 20, 4, true);
            Assert.True(wl.RowCount >= 3);
            int r1s = wl.RowStarts[1], r1e = wl.RowEnd(1, line.Length);
            // caret in the middle of row 1: End -> end of row 1 (affinity), End again -> end of line
            int col = r1s + 2;
            int end1 = EditorWrapNavigation.End(wl, line.Length, col, false);
            Assert.Equal(r1e, end1);
            Assert.Equal(1, EditorWrapNavigation.CaretRow(wl, end1, true));  // drawn at the end of row 1
            Assert.Equal(2, EditorWrapNavigation.CaretRow(wl, end1, false)); // without affinity it would be row 2
            Assert.Equal(-1, EditorWrapNavigation.End(wl, line.Length, end1, true));
            // the same offset WITHOUT affinity is the start of row 2: End goes to the end of row 2
            int e2 = EditorWrapNavigation.End(wl, line.Length, end1, false);
            Assert.True(e2 == -1 ? wl.RowCount == 3 : e2 == wl.RowEnd(2, line.Length));
            // last row: plain End
            Assert.Equal(-1, EditorWrapNavigation.End(wl, line.Length, line.Length - 1, false));
            // Home: row start first, then the line rule
            Assert.Equal(r1s, EditorWrapNavigation.Home(wl, col, false));
            Assert.Equal(-1, EditorWrapNavigation.Home(wl, r1s, false));
            Assert.Equal(-1, EditorWrapNavigation.Home(wl, 6, false)); // first row
            // Home from the row-end affinity position goes to the start of THAT row
            Assert.Equal(r1s, EditorWrapNavigation.Home(wl, end1, true));
            // unwrapped line: never handled
            Assert.Equal(-1, EditorWrapNavigation.End(WrapLayout.Single, 10, 3, false));
            Assert.Equal(-1, EditorWrapNavigation.Home(WrapLayout.Single, 3, false));
        }

        // ------------------------------------------------------------------ #33 multi-caret typing (WinForms engine)
        private static EditorTabBuffer MultiBuf(string text, params int[] lineColPairs)
        {
            var b = NewBuf(text);
            b.CursorLine = lineColPairs[0]; b.CursorCol = lineColPairs[1];
            for (int i = 2; i < lineColPairs.Length; i += 2) b.ExtraCarets.Add(new EditorCaret { Line = lineColPairs[i], Col = lineColPairs[i + 1] });
            return b;
        }

        [Fact]
        public void MultiCaret_AutoClose_Overtype_PairDelete_AtEveryCaret()
        {
            var b = MultiBuf("a = f\nb = g\n", 0, 5, 1, 5);
            var auto = new List<long>();
            Assert.True(EditorMultiCaret.TypeChar(b, '(', b.FilePath, auto));
            Assert.Equal("a = f()", b.Lines[0]);
            Assert.Equal("b = g()", b.Lines[1]);
            Assert.Equal(6, b.CursorCol);
            Assert.Single(b.ExtraCarets);
            Assert.Equal(6, b.ExtraCarets[0].Col);
            Assert.Equal(2, auto.Count);
            // typing inside keeps the closers tracked (offsets shift), ")" steps over them at both carets
            EditorMultiCaret.TypeChar(b, 'x', b.FilePath, auto);
            Assert.Equal("a = f(x)", b.Lines[0]);
            Assert.False(EditorMultiCaret.TypeChar(b, ')', b.FilePath, auto));
            Assert.Equal("a = f(x)", b.Lines[0]);
            Assert.Equal("b = g(x)", b.Lines[1]);
            Assert.Equal(8, b.CursorCol);
            Assert.Equal(8, b.ExtraCarets[0].Col);
            // a ")" that was typed by the user (not auto-inserted) is not overtyped
            Assert.True(EditorMultiCaret.TypeChar(b, ')', b.FilePath, auto));
            Assert.Equal("a = f(x))", b.Lines[0]);

            // Backspace between empty auto-closed pairs deletes both characters at every caret
            var c = MultiBuf("x\ny\n", 0, 1, 1, 1);
            var auto2 = new List<long>();
            EditorMultiCaret.TypeChar(c, '[', c.FilePath, auto2);
            Assert.Equal("x[]", c.Lines[0]);
            EditorMultiCaret.Backspace(c, 4, c.FilePath, auto2);
            Assert.Equal("x", c.Lines[0]);
            Assert.Equal("y", c.Lines[1]);
            Assert.Equal(1, c.CursorCol);

            // VS Code: auto-close only when EVERY caret qualifies (one caret before a word -> plain "(" everywhere)
            var d = MultiBuf("f\nfoo\n", 0, 1, 1, 0);
            EditorMultiCaret.TypeChar(d, '(', d.FilePath, new List<long>());
            Assert.Equal("f(", d.Lines[0]);
            Assert.Equal("(foo", d.Lines[1]);

            // auto-surround when every caret has a selection
            var s = NewBuf("one two\nthree four\n");
            s.SelectionAnchorLine = 0; s.SelectionAnchorCol = 0; s.CursorLine = 0; s.CursorCol = 3;
            s.ExtraCarets.Add(new EditorCaret { AnchorLine = 1, AnchorCol = 0, Line = 1, Col = 5 });
            EditorMultiCaret.TypeChar(s, '"', s.FilePath, new List<long>());
            Assert.Equal("\"one\" two", s.Lines[0]);
            Assert.Equal("\"three\" four", s.Lines[1]);
            Assert.Equal("one", s.GetSelectedText());
            Assert.True(s.ExtraCarets[0].HasSelection());
        }

        [Fact]
        public void MultiCaret_Tab_InsertsEachCaretsOwnIndentation()
        {
            // carets at columns 1 and 2: with tab size 4 they need 3 and 2 spaces to reach column 4
            var b = MultiBuf("a\nbb\n", 0, 1, 1, 2);
            EditorMultiCaret.InsertTab(b, 4, true, null);
            Assert.Equal("a   ", b.Lines[0]);
            Assert.Equal("bb  ", b.Lines[1]);
            Assert.Equal(4, b.CursorCol);
            Assert.Equal(4, b.ExtraCarets[0].Col);
            // two carets on ONE line: each reaches its own tab stop, the later caret shifts with the earlier insert
            var c = MultiBuf("ab cd\n", 0, 1, 0, 4);
            EditorMultiCaret.InsertTab(c, 4, true, null);
            Assert.Equal("a   b c    d", c.Lines[0]);
            // hard tabs
            var t = MultiBuf("x\ny\n", 0, 1, 1, 0);
            EditorMultiCaret.InsertTab(t, 4, false, null);
            Assert.Equal("x\t", t.Lines[0]);
            Assert.Equal("\ty", t.Lines[1]);
        }

        // ------------------------------------------------------------------ #28 scrollbar / overview ruler models (both editors)
        [Fact]
        public void ScrollbarGeometry_VsCodeProportions_MinThumb_JumpAndDragMapping()
        {
            // 1000 rows, 100 visible, 600 px track; content = 999 + 100 rows (scroll beyond the last line)
            var g = EditorScrollbar.Vertical(0, 600, 1000, 100, 0);
            Assert.True(g.Visible);
            Assert.Equal(600.0 * 100 / 1099, g.ThumbLength, 6);
            Assert.Equal(0, g.ThumbStart, 6);
            var bottom = EditorScrollbar.Vertical(0, 600, 1000, 100, 999);
            Assert.Equal(600, bottom.ThumbEnd, 6);
            // tiny thumb is clamped to 20 px (VS Code minimum slider size)
            var huge = EditorScrollbar.Vertical(0, 600, 1000000, 30, 500000);
            Assert.Equal(EditorScrollbar.MinThumb, huge.ThumbLength, 6);
            Assert.Equal(500000, EditorScrollbar.ValueForThumbStart(huge, huge.ThumbStart), 0);
            // hit test + jump centres the slider on the click
            Assert.Equal(EditorScrollbar.HitThumb, EditorScrollbar.HitTest(g, 5));
            Assert.Equal(EditorScrollbar.HitAfter, EditorScrollbar.HitTest(g, 400));
            double v = EditorScrollbar.ValueForJump(g, 300);
            var j = EditorScrollbar.Vertical(0, 600, 1000, 100, v);
            Assert.Equal(300, j.ThumbStart + j.ThumbLength / 2, 1);
            // a single line scrolls nowhere: no slider
            Assert.False(EditorScrollbar.Vertical(0, 600, 1, 30, 0).Visible);
            // horizontal: hidden when everything fits, proportional otherwise
            Assert.False(EditorScrollbar.Horizontal(60, 800, 700, 800, 0).Visible);
            var hz = EditorScrollbar.Horizontal(60, 800, 3200, 800, 1200);
            Assert.True(hz.Visible);
            Assert.Equal(200, hz.ThumbLength, 6);
            Assert.Equal(60 + 600 * (1200.0 / 2400), hz.ThumbStart, 6);
            // marks use the slider scale: the rows on screen lie under the slider
            Assert.Equal(g.ThumbLength, 100 * g.PixelsPerRow, 6);
        }

        [Fact]
        public void VisualRowIndex_WrapFoldsAndDeletedZones()
        {
            var lines = new List<string> { "a", new string('x', 95), "b", "c", "d", "e" };
            var idx = new EditorVisualRowIndex();
            idx.Build(lines, 1, 0, 4, null, null, "");
            Assert.True(idx.IsIdentity);
            Assert.Equal(6, idx.TotalRows);
            // wrap at 40 columns: line 1 takes 3 rows (only long lines are wrapped)
            int wrapped = 0;
            idx.Build(lines, 1, 40, 4, s => { wrapped++; return EditorWordWrap.Compute(s, 40, 4, true).RowCount; }, null, "");
            Assert.Equal(1, wrapped);
            Assert.Equal(8, idx.TotalRows);
            Assert.Equal(4, idx.RowOfLine(2));
            int sub;
            Assert.Equal(1, idx.LineOfRow(3, out sub));
            Assert.Equal(2, sub);
            // fold lines 3..4 under header 2
            var folds = new Dictionary<int, int> { { 2, 4 } };
            idx.Build(lines, 1, 40, 4, s => EditorWordWrap.Compute(s, 40, 4, true).RowCount, folds, "2-4");
            Assert.Equal(6, idx.TotalRows);
            Assert.Equal(4, idx.RowOfLine(3));          // hidden line -> its header's row
            Assert.Equal(0, idx.RowsOfLine(3));
            Assert.Equal(5, idx.RowOfLine(5));
            Assert.Equal(5, idx.LineOfRow(5, out sub));
            // deleted zone of 2 rows above line 2 (inline diff review)
            idx.Build(lines, 1, 0, 4, null, null, "z", new Dictionary<int, int> { { 2, 2 } });
            Assert.Equal(8, idx.TotalRows);
            Assert.Equal(4, idx.RowOfLine(2));
            Assert.Equal(2, idx.LineOfRow(2, out sub));
            Assert.Equal(-2, sub);                      // inside the zone above line 2
            Assert.Equal(2, idx.ZoneRowsAbove(2));
            Assert.True(idx.Matches(lines, 1, 0, 4, "z"));
            Assert.False(idx.Matches(lines, 2, 0, 4, "z"));
        }

        [Fact]
        public void OverviewRuler_Lanes_AndGutterDeletedMarkers()
        {
            var buf = NewBuf("l0\nl1\nl2\nl3\nl4\nl5\nl6\nl7\nl8\nl9\n", "txt");
            // the base had two extra lines after l4 and l7 was different: a deletion and a modification
            buf.GitHeadContent = "l0\nl1\nl2\nl3\nl4\nx1\nx2\nl5\nl6\nOLD\nl8\nl9\n";
            buf.RefreshGutterDiff();
            Assert.Contains(4, buf.GutterDeletedAfterLine);      // a deletion is a red marker, not a "modified" line
            Assert.DoesNotContain(5, buf.GutterModifiedLines);
            Assert.Contains(7, buf.GutterModifiedLines);
            // pure deletion of the first line
            var b2 = NewBuf("b\nc\n", "txt");
            b2.GitHeadContent = "a\nb\nc\n";
            b2.RefreshGutterDiff();
            Assert.Contains(-1, b2.GutterDeletedAfterLine);
            Assert.Empty(b2.GutterModifiedLines);

            buf.Diagnostics.Add(new DiagnosticItem { Severity = "Error", Line = 3 });
            buf.Diagnostics.Add(new DiagnosticItem { Severity = "Info", Line = 9 });
            var idx = new EditorVisualRowIndex();
            idx.Build(buf.Lines, buf.EditVersion, 0, 4, null, null, "");
            var marks = EditorOverviewRuler.Collect(buf, idx, new[] { 1, 1, 6 }, new[] { 8 });
            Assert.Contains(marks, m => m.Kind == EditorOverviewRuler.KindDeleted && m.Row == 5 && m.Lane == EditorOverviewRuler.LaneLeft);
            Assert.Contains(marks, m => m.Kind == EditorOverviewRuler.KindModified && m.Row == 7 && m.Lane == EditorOverviewRuler.LaneLeft);
            Assert.Contains(marks, m => m.Kind == EditorOverviewRuler.KindError && m.Row == 2 && m.Lane == EditorOverviewRuler.LaneRight);
            Assert.Contains(marks, m => m.Kind == EditorOverviewRuler.KindInfo && m.Row == 8);
            Assert.Equal(2, marks.Count(m => m.Kind == EditorOverviewRuler.KindFindMatch)); // one per line
            Assert.Contains(marks, m => m.Kind == EditorOverviewRuler.KindCursor && m.Row == 8 && m.Lane == EditorOverviewRuler.LaneFull);
            // errors paint after warnings / infos (on top)
            int iErr = marks.FindIndex(m => m.Kind == EditorOverviewRuler.KindError), iInfo = marks.FindIndex(m => m.Kind == EditorOverviewRuler.KindInfo);
            Assert.True(iErr > iInfo);
            double x, w;
            EditorOverviewRuler.LaneBounds(EditorOverviewRuler.LaneRight, 13, out x, out w);
            Assert.True(x > 8 && x + w <= 13.0001);
            // colors exist for every kind on light and dark themes
            foreach (var t in StudioTheme.GetBuiltInThemes())
                for (int k = 1; k <= 11; k++) Assert.True(UiTokens.OverviewRulerMark(t, k).A > 0);
        }

        // ------------------------------------------------------------------ #31 word-based suggestions (both editors)
        [Fact]
        public void QuickSuggestions_OffInCommentsAndStrings_OnInCode_OffInMarkdown()
        {
            var b = NewBuf("var a = 1; // note here\nstring s = \"text here\";\n/* block\n   still comment */ int z;\nchar c = 'x';\n");
            Assert.True(WordBasedSuggestions.QuickSuggestionsAllowed(b, 0, 5));    // code
            Assert.False(WordBasedSuggestions.QuickSuggestionsAllowed(b, 0, 18));  // after //
            Assert.False(WordBasedSuggestions.QuickSuggestionsAllowed(b, 1, 16));  // inside "text here"
            Assert.True(WordBasedSuggestions.QuickSuggestionsAllowed(b, 1, 24));   // after the closing quote
            Assert.False(WordBasedSuggestions.QuickSuggestionsAllowed(b, 3, 5));   // second line of a block comment
            Assert.True(WordBasedSuggestions.QuickSuggestionsAllowed(b, 3, 23));   // after */
            Assert.Equal(EditorBracketCache.ContextString, EditorSyntaxSession.For(b).Brackets.ContextAt(b.Lines, 1, 12));
            // an unterminated string at the end of the line is still a string
            var u = NewBuf("x = \"abc\n");
            Assert.False(WordBasedSuggestions.QuickSuggestionsAllowed(u, 0, 8));
            // Python: '#' comments, triple-quoted strings
            var py = NewBuf("def f():\n    # todo\n    s = \"\"\"doc\n    more\"\"\"\n    return s\n", "py");
            Assert.False(WordBasedSuggestions.QuickSuggestionsAllowed(py, 1, 10));
            Assert.False(WordBasedSuggestions.QuickSuggestionsAllowed(py, 3, 6));
            Assert.True(WordBasedSuggestions.QuickSuggestionsAllowed(py, 4, 12));
            // Markdown: VS Code turns quick suggestions off
            var md = NewBuf("# Title\nsome words\n", "md");
            Assert.False(WordBasedSuggestions.QuickSuggestionsAllowed(md, 1, 4));
        }

        [Fact]
        public void WordSuggestions_MatchingDocuments_ExcludeCurrentWordAndNumbers_IncludeSnippets()
        {
            var cur = NewBuf("int counterValue = 42;\nint cou\n");
            var other = NewBuf("var countryName = 1;\n", "cs");
            other.FilePath = "/tmp/other.cs";
            var py = NewBuf("couch_potato = 3\n", "py");
            var snippets = new[] { new CodeSnippetItem { Prefix = "cw", Body = "Console.WriteLine($1);", Description = "Console.WriteLine", Language = "cs" } };
            var items = WordBasedSuggestions.Build(cur, new[] { cur, other, py }, 1, 7, snippets);
            var labels = items.Select(i => i.Label).ToList();
            Assert.Contains("counterValue", labels);
            Assert.Contains("countryName", labels);        // same language, other open document
            Assert.DoesNotContain("couch_potato", labels); // other language
            Assert.DoesNotContain("cou", labels);          // the word being typed
            Assert.DoesNotContain("42", labels);           // numbers
            Assert.Equal(labels.Count, labels.Distinct().Count());
            Assert.True(labels.IndexOf("counterValue") < labels.IndexOf("countryName")); // current document first
            var sn = items.Single(i => i.Label == "cw");
            Assert.True(sn.IsSnippet);
            Assert.Same(snippets[0], sn.Tag);
            Assert.Equal(WordBasedSuggestions.KindSnippet, sn.Kind);
            // prefix filter keeps only fuzzy matches; the client-side session ranks prefix matches first
            var filtered = WordBasedSuggestions.Build(cur, new[] { cur, other }, 1, 7, snippets, "cou");
            Assert.DoesNotContain(filtered, i => i.Label == "int");
            var session = new LspCompletionSession { Line = 1, StartCol = 4 };
            session.SetItems(new LspCompletionList { Items = filtered });
            Assert.True(session.Refilter(cur.Lines[1], 1, 7));
            Assert.StartsWith("cou", session.Visible[0].Label);
            // the word list of a document is cached per version (no rescan while nothing changed)
            var again = WordBasedSuggestions.Build(cur, new[] { cur }, 1, 7, null);
            Assert.Same(items.First(i => i.Label == "counterValue"), again.First(i => i.Label == "counterValue"));
        }
    }
}
