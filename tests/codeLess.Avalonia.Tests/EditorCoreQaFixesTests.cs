using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using codeLess.Studio;
using Xunit;

namespace codeLess.Avalonia.Tests
{
    /// <summary>
    /// Shared-engine tests for the QA editor fixes (both UIs use these models): soft wrap (#11), indentation / Tab /
    /// .editorconfig (#12), folding (#13 / #16), lazy text + delta undo (#15), large-file thresholds (#17) and the
    /// visual column model (#20).
    /// </summary>
    public class EditorCoreQaFixesTests
    {
        // ------------------------------------------------------------------ #20 visual columns
        [Fact]
        public void VisualColumns_UseRealTabSize_WideAndCombiningCharacters()
        {
            Assert.Equal(4, EditorTextLayout.VisualColumn("\tx", 1, 4));
            Assert.Equal(2, EditorTextLayout.VisualColumn("\tx", 1, 2));
            Assert.Equal(8, EditorTextLayout.VisualColumn("ab\tx", 3, 8));
            Assert.Equal(4, EditorTextLayout.VisualColumn("ab\tx", 3, 4)); // tab stop, not +4
            Assert.Equal(4, EditorTextLayout.VisualColumn("日本x", 2, 4)); // CJK = 2 cells each
            string emoji = "a\U0001F600b";
            Assert.Equal(3, EditorTextLayout.VisualColumn(emoji, 3, 4)); // surrogate pair = 1 glyph, 2 cells
            Assert.Equal(1, EditorTextLayout.VisualColumn("e\u0301x", 2, 4)); // e + combining acute = 1 cell
            // inverse mapping never lands inside a surrogate pair or before a combining mark
            Assert.Equal(1, EditorTextLayout.CharIndexForVisualColumn(emoji, 1.4, 4));
            Assert.Equal(3, EditorTextLayout.CharIndexForVisualColumn(emoji, 2.6, 4));
            Assert.Equal(2, EditorTextLayout.CharIndexForVisualColumn("e\u0301x", 1.0, 4));
            Assert.Equal(1, EditorTextLayout.CharIndexForVisualColumn("\tx", 3.0, 4));
            Assert.Equal(0, EditorTextLayout.CharIndexForVisualColumn("\tx", 1.9, 4));
        }

        // ------------------------------------------------------------------ #11 soft wrap
        [Fact]
        public void WordWrap_BreaksAtWordBoundaries_KeepsIndent_AndHangsSpaces()
        {
            string line = "    return alpha + beta + gamma + delta + epsilon;";
            var w = EditorWordWrap.Compute(line, 20, 4, true);
            Assert.True(w.RowCount >= 3);
            Assert.Equal(4, w.Indent); // "same" wrapping indent
            for (int r = 1; r < w.RowCount; r++)
            {
                int s = w.RowStarts[r];
                Assert.NotEqual(' ', line[s]);              // rows never start with the hanging space
                Assert.Equal(' ', line[s - 1]);             // broke after a space (word boundary)
                int end = w.RowEnd(r, line.Length);
                string rowText = line.Substring(s, end - s).TrimEnd();
                Assert.True(4 + rowText.Length <= 20, "row too wide: '" + rowText + "'");
            }
            // a long word with no break opportunity is split at the edge
            var w2 = EditorWordWrap.Compute(new string('x', 50), 20, 4, true);
            Assert.Equal(new[] { 0, 20, 40 }, w2.RowStarts);
            // break before "(" and after "." / "/"
            var w3 = EditorWordWrap.Compute("aaaaaaaaaaaaaaaa.bbbbbbbbbbbbbbbb(cccccccccc)", 20, 4, false);
            Assert.Equal(17, w3.RowStarts[1]);
            // CJK breaks between ideographs; wide characters count 2 cells
            var w4 = EditorWordWrap.Compute(new string('日', 15), 10, 4, false);
            Assert.Equal(new[] { 0, 5, 10 }, w4.RowStarts);
            // short lines are a single row, caret affinity at a soft break
            Assert.Same(WrapLayout.Single, EditorWordWrap.Compute("short", 80, 4, true));
            Assert.Equal(1, w2.RowOf(20, false));
            Assert.Equal(0, w2.RowOf(20, true));
        }

        [Fact]
        public void WrapCache_RewrapsOnlyNewLines_AndResetsOnWidthChange()
        {
            var cache = new WrapLayoutCache();
            cache.Configure(30, 4, true);
            string a = new string('a', 100), b = "x " + new string('b', 100);
            cache.Get(a); cache.Get(b); cache.Get(a);
            Assert.Equal(2, cache.Computations);
            cache.Configure(30, 4, true);
            cache.Get(a);
            Assert.Equal(2, cache.Computations);
            cache.Configure(40, 4, true);
            Assert.Equal(3, cache.Get(a).RowCount);
            Assert.Equal(3, cache.Computations);
        }

        // ------------------------------------------------------------------ #13 folding
        private static List<FoldRange> Folds(string file, params string[] lines) => EditorFoldingModel.ComputeFor(lines.ToList(), file, 4);

        private static int EndOf(List<FoldRange> r, int start) { var f = r.FirstOrDefault(x => x.Start == start); return f.End > f.Start ? f.End : -1; }

        [Fact]
        public void Folding_AllmanHeaderFoldsItsBlock_KAndRKeepsClosingLineVisible()
        {
            var r = Folds("a.cs",
                "namespace N",      // 0
                "{",                // 1
                "    class C",      // 2
                "    {",            // 3
                "        void F()", // 4
                "        {",        // 5
                "            x();", // 6
                "        }",        // 7
                "    }",            // 8
                "}");               // 9
            Assert.Equal(8, EndOf(r, 0));  // the header line, not the "{" line, owns the block; "}" stays visible
            Assert.Equal(7, EndOf(r, 2));
            Assert.Equal(6, EndOf(r, 4));
            Assert.Equal(-1, EndOf(r, 1));
            Assert.Equal(-1, EndOf(r, 5));
            var k = Folds("a.js", "function f() {", "  if (a) {", "    b();", "  } else {", "    c();", "  }", "}");
            Assert.Equal(5, EndOf(k, 0));
            Assert.Equal(2, EndOf(k, 1)); // "} else {" stays visible ...
            Assert.Equal(4, EndOf(k, 3)); // ... and folds its own block
            // braces in strings / comments do not count
            var s = Folds("a.cs", "void F()", "{", "    var s = \"{ not a block\";", "    // }", "}");
            Assert.Equal(3, EndOf(s, 0));
        }

        [Fact]
        public void Folding_AllmanClassAfterDocComment_AndGenericFields()
        {
            var r = Folds("p.cs",
                "namespace Acme.Orders",                                   // 0
                "{",                                                       // 1
                "    /// <summary>Validates orders.</summary>",           // 2
                "    public sealed class OrderPipeline : IOrderPipeline",  // 3
                "    {",                                                   // 4
                "        private readonly Dictionary<string, List<Order>> pending = new Dictionary<string, List<Order>>();", // 5
                "",                                                        // 6
                "        public IReadOnlyList<Order> Process(IEnumerable<Order> orders, decimal discount)", // 7
                "        {",                                               // 8
                "            return null;",                                // 9
                "        }",                                               // 10
                "    }",                                                   // 11
                "}");                                                      // 12
            Assert.Equal(11, EndOf(r, 0));
            Assert.Equal(10, EndOf(r, 3));
            Assert.Equal(9, EndOf(r, 7));
        }

        [Fact]
        public void Folding_RegionsCommentsImports_PythonYamlMarkdownMarkup()
        {
            var cs = Folds("a.cs", "using System;", "using System.IO;", "#region Fields", "int a;", "int b;", "#endregion", "/* multi", " line */", "x();");
            Assert.Equal(1, EndOf(cs, 0));        // usings
            Assert.Equal(5, EndOf(cs, 2));        // #region .. #endregion (the end marker folds too)
            Assert.Equal(7, EndOf(cs, 6));        // block comment
            var ts = Folds("a.ts", "// #region helpers", "const a = 1;", "// #endregion");
            Assert.Equal(2, EndOf(ts, 0));
            var py = Folds("a.py", "def f():", "    x = 1", "", "    if x:", "        y()", "", "z = 2", "#region r", "a = 1", "#endregion");
            Assert.Equal(4, EndOf(py, 0));        // trailing blank lines are not part of the block
            Assert.Equal(4, EndOf(py, 3));
            Assert.Equal(9, EndOf(py, 7));
            var yaml = Folds("a.yaml", "root:", "  child:", "    leaf: 1", "  other: 2", "top: 3");
            Assert.Equal(3, EndOf(yaml, 0));
            Assert.Equal(2, EndOf(yaml, 1));
            var md = Folds("a.md", "# Title", "text", "## Sub", "more", "", "# Next", "```", "code", "```");
            Assert.Equal(3, EndOf(md, 2));
            Assert.Equal(3, EndOf(md, 0)); // "# Title" ends before "# Next" (the blank line is trimmed)
            Assert.Equal(8, EndOf(md, 5));
            Assert.Equal(8, EndOf(md, 6));
            var html = Folds("a.html", "<div>", "  <p>", "    hi", "  </p>", "  <br>", "  <!-- a", "  b -->", "</div>");
            Assert.Equal(6, EndOf(html, 0));      // closing tag line stays visible
            Assert.Equal(2, EndOf(html, 1));
            Assert.Equal(6, EndOf(html, 5));      // multi-line comment
            var xml = Folds("a.csproj", "<Project>", "  <ItemGroup>", "    <A Include=\"x\" />", "  </ItemGroup>", "</Project>");
            Assert.Equal(3, EndOf(xml, 0));
            Assert.Equal(2, EndOf(xml, 1));
        }

        [Fact]
        public void Folding_ModelIsCachedPerVersion_AndFoldsFollowEdits()
        {
            var buf = new EditorTabBuffer { FilePath = "/tmp/fold_" + Guid.NewGuid().ToString("N") + ".cs", Content = "void A()\n{\n    a();\n}\nvoid B()\n{\n    b();\n}\n" };
            buf.SyncLinesFromContent();
            var m = EditorFoldingModel.For(buf);
            Assert.Equal(6, EditorFoldingModel.GetFoldEnd(buf, 4, 4));
            long n = m.ComputationCount;
            EditorFoldingModel.GetFoldEnd(buf, 0, 4);
            Assert.Equal(n, m.ComputationCount); // same version: no recomputation
            buf.FoldedBlockRanges[4] = 6;
            // insert two lines above the folded block: the fold moves with its header
            buf.Lines.InsertRange(0, new[] { "// header", "" });
            buf.SyncContentFromLines(false);
            Assert.True(buf.FoldedBlockRanges.ContainsKey(6));
            Assert.Equal(8, buf.FoldedBlockRanges[6]);
            // typing on the header line keeps it folded
            buf.Lines[6] = "void Bee()";
            buf.SyncContentFromLines(false);
            Assert.True(buf.FoldedBlockRanges.ContainsKey(6));
            // removing the header's block (the range disappears) expands it after the recomputation
            buf.Lines[6] = "int x;";
            buf.Lines[7] = "int y;";
            buf.Lines[9] = "int z;";
            buf.SyncContentFromLines(false);
            EditorFoldingModel.GetFoldEnd(buf, 0, 4);
            Assert.False(buf.FoldedBlockRanges.ContainsKey(6));
        }

        // ------------------------------------------------------------------ #12 indentation
        [Fact]
        public void Indentation_GuessMatchesVsCode_AndIndentOutdentNormalizeToTabStops()
        {
            int t; bool sp;
            EditorIndentation.Guess(new[] { "a {", "  b {", "    c", "  }", "}" }, 4, true, out t, out sp);
            Assert.True(sp); Assert.Equal(2, t);
            EditorIndentation.Guess(new[] { "a {", "\tb {", "\t\tc", "\t}", "}" }, 4, true, out t, out sp);
            Assert.False(sp); Assert.Equal(4, t);
            EditorIndentation.Guess(new[] { "a:", "    b:", "        c", "    d" }, 2, true, out t, out sp);
            Assert.True(sp); Assert.Equal(4, t);
            Assert.Equal("    x", EditorIndentation.IndentLine("   x", 4, true));    // 3 -> next stop 4
            Assert.Equal("\tx", EditorIndentation.IndentLine("x", 4, false));
            Assert.Equal("  x", EditorIndentation.IndentLine("x", 2, true));
            Assert.Equal("    x", EditorIndentation.OutdentLine("      x", 4, true)); // 6 -> previous stop 4
            Assert.Equal("x", EditorIndentation.OutdentLine("\tx", 4, true));        // tab-indented line stays tab style
            Assert.Equal("\tx", EditorIndentation.OutdentLine("\t\tx", 4, true));
            Assert.Equal("  ", EditorIndentation.TabInsertText("ab", 2, 4, true));    // to the next tab stop
            Assert.Equal("\t", EditorIndentation.TabInsertText("ab", 2, 4, false));
            Assert.Equal(4, EditorIndentation.BackspaceDeleteCount("        x", 8, 4)); // useTabStops
            Assert.Equal(2, EditorIndentation.BackspaceDeleteCount("      x", 6, 4));
            Assert.Equal(1, EditorIndentation.BackspaceDeleteCount("  a x", 4, 4));
            Assert.Equal("Spaces: 2", EditorIndentation.StatusText(2, true));
            Assert.Equal("Tab Size: 8", EditorIndentation.StatusText(8, false));
        }

        [Fact]
        public void EditorConfig_GlobsSectionsAndRoot_ApplyOnOpenAndSave()
        {
            string root = Path.Combine(Path.GetTempPath(), "codeLess_ec_" + Guid.NewGuid().ToString("N").Substring(0, 8));
            string sub = Path.Combine(root, "src", "lib");
            Directory.CreateDirectory(sub);
            try
            {
                File.WriteAllText(Path.Combine(root, ".editorconfig"),
                    "root = true\n[*]\nindent_style = space\nindent_size = 4\nend_of_line = lf\ninsert_final_newline = true\n" +
                    "[*.{js,ts}]\nindent_size = 2\n[Makefile]\nindent_style = tab\n[src/**.py]\nindent_size = 3\ntrim_trailing_whitespace = false\n" +
                    "[*.go]\nindent_style = tab\nindent_size = tab\ntab_width = 8\ncharset = utf-8-bom\n[test{1..3}.txt]\nindent_size = 7\n");
                File.WriteAllText(Path.Combine(sub, ".editorconfig"), "[*.ts]\nindent_size = 6\n");
                Assert.Equal(2, EditorConfigResolver.Resolve(Path.Combine(root, "a.js")).EffectiveTabSize);
                Assert.Equal(6, EditorConfigResolver.Resolve(Path.Combine(sub, "b.ts")).EffectiveTabSize); // nearer file wins
                Assert.Equal("tab", EditorConfigResolver.Resolve(Path.Combine(root, "Makefile")).IndentStyle);
                var py = EditorConfigResolver.Resolve(Path.Combine(sub, "x.py"));
                Assert.Equal(3, py.EffectiveTabSize);
                Assert.False(py.TrimTrailingWhitespace.Value);
                var go = EditorConfigResolver.Resolve(Path.Combine(root, "m.go"));
                Assert.Equal(8, go.EffectiveTabSize);
                Assert.Equal("utf-8-bom", go.Charset);
                Assert.Equal(7, EditorConfigResolver.Resolve(Path.Combine(root, "test2.txt")).EffectiveTabSize);
                Assert.Equal(4, EditorConfigResolver.Resolve(Path.Combine(root, "test9.txt")).EffectiveTabSize);

                // on open: .editorconfig beats detection; on save: eol / charset / save actions
                string goFile = Path.Combine(root, "m.go");
                File.WriteAllText(goFile, "package m\n\nfunc f() {\n    x()\n}");
                var buf = new EditorTabBuffer { FilePath = goFile, Content = File.ReadAllText(goFile), EolSequence = "\r\n" };
                buf.SyncLinesFromContent();
                EditorIndentation.ApplyOnOpen(buf, new IdePersistentSettings());
                Assert.False(buf.CustomInsertSpaces.Value);
                Assert.Equal(8, buf.CustomTabSize.Value);
                var opts = new SaveActionOptions();
                EditorConfigResolver.ApplyBeforeSave(buf, opts);
                Assert.Equal("\n", buf.EolSequence);
                Assert.True(buf.HasBom);
                Assert.True(opts.InsertFinalNewline);

                // no .editorconfig rule for size -> detection from content
                string plain = Path.Combine(Path.GetTempPath(), "codeLess_detect_" + Guid.NewGuid().ToString("N").Substring(0, 6) + ".js");
                File.WriteAllText(plain, "function f() {\n  if (a) {\n    b();\n  }\n}\n");
                var b2 = new EditorTabBuffer { FilePath = plain, Content = File.ReadAllText(plain) };
                b2.SyncLinesFromContent();
                EditorIndentation.ApplyOnOpen(b2, new IdePersistentSettings());
                Assert.True(b2.CustomInsertSpaces.Value);
                Assert.Equal(2, b2.CustomTabSize.Value);
                File.Delete(plain);
            }
            finally { try { Directory.Delete(root, true); } catch { } }
        }

        [Fact]
        public void AutoClosing_FollowsVsCodeRules()
        {
            Assert.True(EditorAutoClosing.ShouldAutoClose("foo", 3, '(', "a.cs"));
            Assert.False(EditorAutoClosing.ShouldAutoClose("foo bar", 4, '(', "a.cs"));    // before a word
            Assert.True(EditorAutoClosing.ShouldAutoClose("f(x)", 3, '[', "a.cs"));        // before ')'
            Assert.False(EditorAutoClosing.ShouldAutoClose("don", 3, '\'', "a.js"));      // after a word char
            Assert.True(EditorAutoClosing.ShouldAutoClose("x = ", 4, '"', "a.js"));
            Assert.False(EditorAutoClosing.ShouldAutoClose("x = \"ab", 7, '"', "a.js"));  // closing an open string
            Assert.False(EditorAutoClosing.ShouldAutoClose("x", 1, '\'', "a.md"));
            Assert.False(EditorAutoClosing.ShouldAutoClose("x = ", 4, '`', "a.cs"));      // no backtick pair in C#
            Assert.True(EditorAutoClosing.ShouldOvertype("()", 1, ')', true));
            Assert.False(EditorAutoClosing.ShouldOvertype("()", 1, ')', false));          // typed by the user: insert
            Assert.True(EditorAutoClosing.ShouldOvertype("\"ab\"", 3, '"', false));        // closing the string
            Assert.True(EditorAutoClosing.ShouldDeletePair("f()", 2, "a.cs"));
            Assert.False(EditorAutoClosing.ShouldDeletePair("f(x)", 2, "a.cs"));
        }

        // ------------------------------------------------------------------ #15 lazy text, dirty, delta undo
        [Fact]
        public void LazyContent_DirtyTracking_AndDeltaUndoStayCorrect()
        {
            var sb = new StringBuilder();
            for (int i = 0; i < 20000; i++) sb.Append("line ").Append(i).Append('\n');
            string text = sb.ToString();
            var buf = new EditorTabBuffer { FilePath = "/tmp/x.txt", Content = text, SavedContent = text };
            buf.SyncLinesFromContent();
            Assert.False(buf.IsDirty);
            buf.CursorLine = 100; buf.CursorCol = 0;
            buf.PushUndoSnapshot(coalesceTyping: true);
            buf.InsertTextAtCursor("x");
            Assert.False(buf.IsContentMaterialized);      // no full-text rebuild per keystroke
            Assert.True(buf.IsDirty);
            Assert.StartsWith("line 0\nline 1\n", buf.Content);
            Assert.True(buf.IsContentMaterialized);
            // typing it away again makes the document clean (content-based, no version counter)
            buf.PerformBackspace();
            Assert.False(buf.IsDirty);
            // 300 separate undo steps on a 20,000-line file stay small and all undo back to the original
            for (int k = 0; k < 300; k++)
            {
                buf.CursorLine = k; buf.CursorCol = 0;
                buf.PushUndoSnapshot();
                buf.InsertTextAtCursor("#");
            }
            long bytes = buf.UndoStack.Sum(s => (long)(s.IsCompacted ? 0 : 1));
            Assert.True(bytes <= 1, "only the top undo entry holds a full line array");
            int steps = 0;
            while (buf.Undo()) steps++;
            Assert.Equal(300, steps);
            Assert.Equal(text, buf.Content);
            Assert.False(buf.IsDirty);
            while (buf.Redo()) { }
            Assert.Equal(300, buf.Lines.Take(300).Count(l => l.StartsWith("#")));
            Assert.True(buf.IsDirty);
        }

        // ------------------------------------------------------------------ comment toggling (found while fixing #12)
        private static EditorTabBuffer Buf(string path, string text)
        {
            var b = new EditorTabBuffer { FilePath = path, Content = text };
            b.SyncLinesFromContent();
            return b;
        }

        [Fact]
        public void Comments_LineCommentAtMinIndent_PerLanguage_BlockFallbacks()
        {
            var b = Buf("/tmp/c.cs", "    if (a)\n\n        b();");
            b.SelectionAnchorLine = 0; b.SelectionAnchorCol = 0; b.CursorLine = 2; b.CursorCol = 12;
            EditorComments.ToggleLineComments(b, new List<int[]> { new[] { 0, 2 } });
            Assert.Equal("    // if (a)", b.Lines[0]);
            Assert.Equal("", b.Lines[1]);                 // blank lines untouched
            Assert.Equal("    //     b();", b.Lines[2]);  // at the smallest indentation
            Assert.Equal(15, b.CursorCol);                 // caret follows the text
            EditorComments.ToggleLineComments(b, new List<int[]> { new[] { 0, 2 } });
            Assert.Equal("    if (a)", b.Lines[0]);
            Assert.Equal("        b();", b.Lines[2]);
            var py = Buf("/tmp/c.py", "x = 1");
            EditorComments.ToggleLineComments(py, new List<int[]> { new[] { 0, 0 } });
            Assert.Equal("# x = 1", py.Lines[0]);
            var sql = Buf("/tmp/c.sql", "select 1");
            EditorComments.ToggleLineComments(sql, new List<int[]> { new[] { 0, 0 } });
            Assert.Equal("-- select 1", sql.Lines[0]);
            // HTML has no line comments: the lines are wrapped in one block comment, and unwrapped again
            var html = Buf("/tmp/c.html", "  <p>a</p>\n  <p>b</p>");
            EditorComments.ToggleLineComments(html, new List<int[]> { new[] { 0, 1 } });
            Assert.Equal("  <!-- <p>a</p>", html.Lines[0]);
            Assert.Equal("  <p>b</p> -->", html.Lines[1]);
            EditorComments.ToggleLineComments(html, new List<int[]> { new[] { 0, 1 } });
            Assert.Equal("  <p>a</p>", html.Lines[0]);
            Assert.Equal("  <p>b</p>", html.Lines[1]);
            // block comment on a selection, and back
            var js = Buf("/tmp/c.js", "let a = b + c;");
            js.SelectionAnchorLine = 0; js.SelectionAnchorCol = 8; js.CursorLine = 0; js.CursorCol = 13;
            EditorComments.ToggleBlockComment(js);
            Assert.Equal("let a = /* b + c */;", js.Lines[0]);
            Assert.Equal("b + c", js.GetSelectedText());
            EditorComments.ToggleBlockComment(js);
            Assert.Equal("let a = b + c;", js.Lines[0]);
            Assert.Equal("b + c", js.GetSelectedText());
            // empty selection: an empty block with the caret inside
            var css = Buf("/tmp/c.css", "a {}");
            css.CursorCol = 3;
            EditorComments.ToggleBlockComment(css);
            Assert.Equal("a {/*  */}", css.Lines[0]);
            Assert.Equal(6, css.CursorCol);
        }

        // ------------------------------------------------------------------ #17 large-file threshold
        [Fact]
        public void LargeFileThreshold_OrdinaryFilesKeepFullFeatures()
        {
            var sb = new StringBuilder();
            for (int i = 0; i < 12000; i++) sb.Append("    public int Field").Append(i).Append(" = ").Append(i).Append(";\n");
            var buf = new EditorTabBuffer { FilePath = "/tmp/big.cs", Content = sb.ToString() };
            buf.SyncLinesFromContent();
            Assert.True(buf.Content.Length > 180 * 1024); // was "large" (>2,500 lines / 180 KB) before
            Assert.False(buf.IsLargeFile);
            Assert.True(EditorLargeFileThresholds.LineThreshold >= 100000);
        }
    }
}
