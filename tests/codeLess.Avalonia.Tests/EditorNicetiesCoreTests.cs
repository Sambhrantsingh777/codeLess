using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using codeLess.Studio;
using Xunit;
using Xunit.Abstractions;

namespace codeLess.Avalonia.Tests
{
    public class EditorNicetiesCoreTests
    {
        private readonly ITestOutputHelper output;
        public EditorNicetiesCoreTests(ITestOutputHelper output) { this.output = output; }

        private static List<string> L(string s) { return s.Replace("\r\n", "\n").Split('\n').ToList(); }

        private static EditorBracketCache Brackets(string ext, List<string> lines)
        {
            var c = new EditorBracketCache(BracketLexProfile.ForExtension(ext, "x" + ext));
            c.Sync(lines);
            return c;
        }

        // ------------------------------------------------------------------ bracket pairs
        [Fact]
        public void Brackets_LevelsRotate_AndStringsCommentsAreIgnored()
        {
            var lines = L("f(a[b{c}]) // ( [ {\n\"(\" + '[' + `{` /* ] */ g(1)");
            var c = Brackets(".js", lines);
            var d0 = c.GetLine(lines, 0, 0);
            Assert.Equal(new[] { 1, 3, 5, 7, 8, 9 }, d0.Cols.Take(d0.Count).ToArray());
            Assert.Equal(new[] { 0, 1, 2, 2, 1, 0 }, d0.Levels.Take(d0.Count).ToArray());
            var d1 = c.GetLine(lines, 1, 0);
            Assert.Equal(2, d1.Count); // only g( ... )
            Assert.Equal(lines[1].IndexOf("g(") + 1, d1.Cols[0]);
        }

        [Fact]
        public void Brackets_UnexpectedClosers_AndMultiLineComments()
        {
            var lines = L("a) {\n  /* { ( [\n  still */ }\n]");
            var c = Brackets(".cs", lines);
            var d0 = c.GetLine(lines, 0, 0);
            Assert.Equal(-1, d0.Levels[0]); // stray ')'
            Assert.Equal(0, d0.Levels[1]);
            Assert.Equal(0, c.GetLine(lines, 1, 0).Count);
            var d2 = c.GetLine(lines, 2, 0);
            Assert.Equal(1, d2.Count);
            Assert.Equal(0, d2.Levels[0]); // closes the '{' of line 0
            Assert.Equal(-1, c.GetLine(lines, 3, 0).Levels[0]);
        }

        [Fact]
        public void Brackets_TemplateExpressions_PythonTripleQuotes_CSharpVerbatim_RustLifetimes()
        {
            var js = L("const s = `a ${ fn(x[0]) } {b}`; [1]");
            var cj = Brackets(".ts", js);
            var dj = cj.GetLine(js, 0, 0);
            // fn( x[ 0 ] ) inside ${} count; {b} inside the template does not; [1] after it does
            Assert.Equal(6, dj.Count);
            Assert.Equal(0, dj.Levels[0]);
            Assert.Equal(1, dj.Levels[1]);

            var py = L("x = \"\"\"( [\n]\"\"\" + f(1)");
            var cp = Brackets(".py", py);
            Assert.Equal(0, cp.GetLine(py, 0, 0).Count);
            Assert.Equal(2, cp.GetLine(py, 1, 0).Count);

            var cs = L("var p = @\"c:\\(x\n\"\"]\"\" \" + f();");
            var cc = Brackets(".cs", cs);
            Assert.Equal(0, cc.GetLine(cs, 0, 0).Count);
            Assert.Equal(2, cc.GetLine(cs, 1, 0).Count);

            var rs = L("fn f<'a>(x: &'a str) -> char { '{' }");
            var cr = Brackets(".rs", rs);
            var dr = cr.GetLine(rs, 0, 0);
            Assert.Equal(4, dr.Count); // ( ) { } -- the '{' char literal is not a bracket
            Assert.True(dr.Levels.Take(dr.Count).All(l => l == 0));
        }

        [Fact]
        public void Brackets_EnclosingPair_AndMatchAtCaret()
        {
            var lines = L("class A {\n    void F() {\n        g(\"}\");\n    }\n}");
            var c = Brackets(".cs", lines);
            var p = c.FindEnclosingPair(lines, 2, 8);
            Assert.True(p.IsValid);
            Assert.Equal(1, p.OpenLine);
            Assert.Equal(3, p.CloseLine);
            Assert.Equal(1, p.Level);
            var outer = c.FindEnclosingPair(lines, 1, 2);
            Assert.Equal(0, outer.OpenLine);
            Assert.Equal(4, outer.CloseLine);
            Assert.True(c.TryFindMatchAt(lines, 0, 9, out BracketPair m)); // caret after '{'
            Assert.Equal(4, m.CloseLine);
            Assert.True(c.TryFindMatchAt(lines, 4, 0, out m)); // caret before '}'
            Assert.Equal(0, m.OpenLine);
            Assert.Equal(8, m.OpenCol);
            Assert.False(c.TryFindMatchAt(lines, 2, 12, out m)); // brace inside a string
        }

        [Fact]
        public void Brackets_IncrementalEditConverges_AndLargeFileIsFast()
        {
            var lines = new List<string>();
            for (int i = 0; i < 20000; i++) lines.Add(i % 4 == 0 ? "void F" + i + "() {" : (i % 4 == 3 ? "}" : "    x(\"(\" + y[" + i + "]);"));
            var c = Brackets(".cs", lines);
            var sw = Stopwatch.StartNew();
            Assert.True(c.EnsureValid(lines, lines.Count - 1, 0));
            output.WriteLine("bracket lexing 20k lines: " + sw.ElapsedMilliseconds + " ms");
            Assert.True(sw.ElapsedMilliseconds < 2000);
            long before = c.ProcessedLineCount;
            lines[10001] = lines[10001] + " // edit";
            c.Sync(lines);
            c.EnsureValid(lines, lines.Count - 1, 0);
            Assert.Equal(1, c.ProcessedLineCount - before);
            // an unclosed block comment re-lexes the rest; closing it converges again
            lines[5000] = "/*";
            c.Sync(lines);
            c.EnsureValid(lines, lines.Count - 1, 0);
            Assert.Equal(0, c.GetLine(lines, 19999, 0).Count);
            lines[5000] = "/**/";
            c.Sync(lines);
            Assert.Equal(1, c.GetLine(lines, 19999, 0).Count);
        }

        [Fact]
        public void BracketColors_SplitSpansAtBrackets()
        {
            var theme = StudioTheme.GetById(StudioTheme.DefaultThemeId);
            var lines = L("f(x[1])");
            var c = Brackets(".js", lines);
            var spans = new List<StyledSpan> { new StyledSpan(0, 7, System.Drawing.Color.White, false, false) };
            var res = EditorSyntaxSession.ApplyBracketColors(spans, c.GetLine(lines, 0, 0), theme);
            Assert.Equal(7, res.Sum(s => s.Length));
            Assert.Equal(UiTokens.BracketPairColor(theme, 0), res.Single(s => s.Start == 1).Color);
            Assert.Equal(UiTokens.BracketPairColor(theme, 1), res.Single(s => s.Start == 3).Color);
            Assert.Equal(UiTokens.BracketPairColor(theme, 0), res.Single(s => s.Start == 6).Color);
            Assert.NotEqual(UiTokens.BracketPairColor(theme, 0), UiTokens.BracketPairColor(theme, 1));
            Assert.Equal(UiTokens.BracketPairColor(theme, 0), UiTokens.BracketPairColor(theme, 3)); // 3 rotating colors
        }

        // ------------------------------------------------------------------ indent guides
        [Fact]
        public void IndentGuides_ColumnsBlankLinesAndActiveBlock()
        {
            var lines = L("class A {\n    void F() {\n        x();\n\n        y();\n    }\n}");
            Assert.Empty(EditorIndentGuides.GuideColumns(lines, 0, 4, false));
            Assert.Equal(new[] { 0 }, EditorIndentGuides.GuideColumns(lines, 1, 4, false));
            Assert.Equal(new[] { 0, 4 }, EditorIndentGuides.GuideColumns(lines, 2, 4, false));
            Assert.Equal(new[] { 0, 4 }, EditorIndentGuides.GuideColumns(lines, 3, 4, false)); // blank inside the block
            // caret inside the method body: the guide at column 4 from line 2 to 4 is active
            var a = EditorIndentGuides.FindActive(lines, 2, 4, false);
            Assert.Equal(4, a.Column);
            Assert.Equal(2, a.StartLine);
            Assert.Equal(4, a.EndLine);
            // caret on a header: its body is active
            var h = EditorIndentGuides.FindActive(lines, 1, 4, false);
            Assert.Equal(4, h.Column);
            Assert.Equal(2, h.StartLine);
            Assert.Equal(4, h.EndLine);
            // tabs count to the next tab stop
            var tabs = L("if x:\n\tif y:\n\t\tz()");
            Assert.Equal(new[] { 0, 4 }, EditorIndentGuides.GuideColumns(tabs, 2, 4, true));
            Assert.Equal(5, EditorIndentGuides.IndentColumns("  \t x", 4));
        }

        // ------------------------------------------------------------------ sticky scroll
        [Fact]
        public void StickyScroll_BraceScopes_AllmanHeaders_AndStringsIgnored()
        {
            var lines = L("namespace N\n{\n    class C\n    {\n        string s = \"{\";\n        void M()\n        {\n            a();\n            b();\n        }\n    }\n}");
            var c = Brackets(".cs", lines);
            var h = EditorStickyScroll.ComputeHeaders(lines, 8, c, c.Profile, null, 4, 5);
            Assert.Equal(new[] { 0, 2, 5 }, h.ToArray()); // Allman braces pin the header line above them
            Assert.Empty(EditorStickyScroll.ComputeHeaders(lines, 0, c, c.Profile, null, 4, 5));
            // top line = the Allman "{" of the namespace / class: their headers already stick
            Assert.Equal(new[] { 0 }, EditorStickyScroll.ComputeHeaders(lines, 1, c, c.Profile, null, 4, 5).ToArray());
            Assert.Equal(new[] { 0, 2 }, EditorStickyScroll.ComputeHeaders(lines, 3, c, c.Profile, null, 4, 5).ToArray());
            Assert.Equal(new[] { 0 }, EditorStickyScroll.ComputeHeaders(lines, 2, c, c.Profile, null, 4, 1).ToArray()); // max lines
        }

        [Fact]
        public void StickyScroll_IndentationLanguages_Markdown_AndLspSymbols()
        {
            var py = L("class A:\n    def f(self):\n        # note\n        x = 1\n        y = 2\n    def g(self):\n        pass");
            var cp = Brackets(".py", py);
            Assert.Equal(new[] { 0, 1 }, EditorStickyScroll.ComputeHeaders(py, 4, cp, cp.Profile, null, 4, 5).ToArray());
            Assert.Equal(new[] { 0, 5 }, EditorStickyScroll.ComputeHeaders(py, 6, cp, cp.Profile, null, 4, 5).ToArray());

            var md = L("# A\ntext\n## B\n```\n# not a heading\n```\n### C\nbody\n## D\nmore");
            var cm = Brackets(".md", md);
            Assert.Equal(new[] { 0, 2, 6 }, EditorStickyScroll.ComputeHeaders(md, 7, cm, cm.Profile, null, 4, 5).ToArray());
            Assert.Equal(new[] { 0, 8 }, EditorStickyScroll.ComputeHeaders(md, 9, cm, cm.Profile, null, 4, 5).ToArray());

            var ts = L("export class Store {\n  load() {\n    return 1;\n  }\n}");
            var ct = Brackets(".ts", ts);
            var syms = new List<StickySymbolRange>
            {
                new StickySymbolRange { StartLine = 0, EndLine = 4, NameLine = 0, Name = "Store" },
                new StickySymbolRange { StartLine = 1, EndLine = 3, NameLine = 1, Name = "load" }
            };
            Assert.Equal(new[] { 0, 1 }, EditorStickyScroll.ComputeHeaders(ts, 2, ct, ct.Profile, syms, 2, 5).ToArray());
            // stale symbols (name no longer on its line) fall back to the brace structure
            syms[1] = new StickySymbolRange { StartLine = 1, EndLine = 3, NameLine = 1, Name = "renamed" };
            Assert.Equal(new[] { 0, 1 }, EditorStickyScroll.ComputeHeaders(ts, 2, ct, ct.Profile, syms, 2, 5).ToArray());
        }

        [Fact]
        public void StickyScroll_ViewportRespectsFoldsAndTheWidgetHeight()
        {
            var lines = new List<string> { "class A {", "  void f() {" };
            for (int i = 0; i < 40; i++) lines.Add("    x" + i + "();");
            lines.Add("  }");
            lines.Add("  void g() {");
            for (int i = 0; i < 40; i++) lines.Add("    y" + i + "();");
            lines.Add("  }");
            lines.Add("}");
            var c = Brackets(".js", lines);
            // top row = line 1 ("void f() {"): the class header sticks, then the widget covers row 0 so the line
            // under it (line 2) is inside f -> both headers
            Func<int, int> rows = r => r + 1 < lines.Count ? r + 1 : -1;
            var st = EditorStickyScroll.ComputeForViewport(lines, rows, c, c.Profile, null, 4, 5);
            Assert.Equal(new[] { 0, 1 }, st.ToArray());
            // f (lines 1..42) folded: rows skip the hidden body; the viewport starts at "}" of f... use a fold map
            var visible = new List<int> { 0, 1 };
            for (int i = 43; i < lines.Count; i++) visible.Add(i);
            Func<int, int> folded = r => r + 2 < visible.Count ? visible[r + 2] : -1; // first row = line 43 ("void g")
            var st2 = EditorStickyScroll.ComputeForViewport(lines, folded, c, c.Profile, null, 4, 5);
            Assert.Equal(new[] { 0, 43 }, st2.ToArray());
            Assert.DoesNotContain(1, st2); // the folded function is not a scope of the visible lines
        }

        // ------------------------------------------------------------------ occurrences
        [Fact]
        public void Occurrences_WordAtCaret_WholeWordMatching_AndSelectionTarget()
        {
            int s, e;
            Assert.Equal("count", EditorOccurrences.GetWordAt("x = count + 1;", 6, out s, out e));
            Assert.Equal(4, s);
            Assert.Equal("count", EditorOccurrences.GetWordAt("x = count + 1;", 9, out s, out e)); // caret right after the word
            Assert.Null(EditorOccurrences.GetWordAt("x = 42;", 5, out s, out e)); // numbers are not highlighted
            Assert.Null(EditorOccurrences.GetWordAt("a  +  b", 3, out s, out e));
            var m = new List<OccurrenceMatch>();
            EditorOccurrences.FindInLine("count counter recount count_ count", 0, "count", true, true, m);
            Assert.Equal(new[] { 0, 29 }, m.Select(x => x.Col).ToArray());
            m.Clear();
            EditorOccurrences.FindInLine("aaaa", 0, "aa", false, true, m);
            Assert.Equal(2, m.Count); // non-overlapping
            bool whole;
            var lines = L("int foo = 1;\nfoo += foo;");
            Assert.Equal("foo", EditorOccurrences.GetHighlightTarget(lines, 1, 1, false, 0, 0, 0, 0, out whole));
            Assert.True(whole);
            Assert.Equal("= 1", EditorOccurrences.GetHighlightTarget(lines, 0, 11, true, 0, 8, 0, 11, out whole));
            Assert.False(whole);
            Assert.Null(EditorOccurrences.GetHighlightTarget(lines, 1, 1, true, 0, 4, 1, 3, out whole)); // multi-line selection
            Assert.Equal(3, EditorOccurrences.FindAll(lines, "foo", true, true).Count);
            Assert.Single(EditorOccurrences.FindInRange(lines, "foo", true, true, 0, 0));
            // CJK / emoji neighbours do not break whole-word detection
            m.Clear();
            EditorOccurrences.FindInLine("名前 x 😀x x", 0, "x", true, true, m);
            Assert.Equal(3, m.Count);
        }

        // ------------------------------------------------------------------ tags
        [Fact]
        public void Tags_AutoCloseOnGreaterThan()
        {
            Assert.Equal("</div>", EditorTagEditing.GetAutoCloseText(L("  <div class=\"a>b\">"), 0, 19, TagLanguageMode.Html));
            Assert.Null(EditorTagEditing.GetAutoCloseText(L("<br>"), 0, 4, TagLanguageMode.Html)); // void element
            Assert.Equal("</br>", EditorTagEditing.GetAutoCloseText(L("<br>"), 0, 4, TagLanguageMode.Xml));
            Assert.Null(EditorTagEditing.GetAutoCloseText(L("<img />"), 0, 7, TagLanguageMode.Jsx));
            Assert.Null(EditorTagEditing.GetAutoCloseText(L("</p>"), 0, 4, TagLanguageMode.Html));
            Assert.Null(EditorTagEditing.GetAutoCloseText(L("<!-- x -->"), 0, 10, TagLanguageMode.Html));
            Assert.Null(EditorTagEditing.GetAutoCloseText(L("<p></p>"), 0, 3, TagLanguageMode.Html)); // already closed
            // multi-line start tag
            Assert.Equal("</section>", EditorTagEditing.GetAutoCloseText(L("<section\n  id=\"x\"\n  class=\"y\">"), 2, 12, TagLanguageMode.Html));
            // JSX: components, fragments, attribute expressions; comparisons and generics are not tags
            Assert.Equal("</Foo.Bar>", EditorTagEditing.GetAutoCloseText(L("return <Foo.Bar onClick={() => a > b}>"), 0, 38, TagLanguageMode.Jsx));
            Assert.Equal("</>", EditorTagEditing.GetAutoCloseText(L("  <>"), 0, 4, TagLanguageMode.Jsx));
            Assert.Null(EditorTagEditing.GetAutoCloseText(L("if (a <b>"), 0, 9, TagLanguageMode.Jsx));
            Assert.Null(EditorTagEditing.GetAutoCloseText(L("const m: Map<string>"), 0, 20, TagLanguageMode.Jsx));
        }

        [Fact]
        public void Tags_CloseTagCompletionOnSlash()
        {
            var lines = L("<ul>\n  <li><b>x</b>\n  </");
            Assert.Equal("li>", EditorTagEditing.GetCloseTagCompletion(lines, 2, 4, TagLanguageMode.Html));
            var l2 = L("<div><br><img src=x></");
            Assert.Equal("div>", EditorTagEditing.GetCloseTagCompletion(l2, 0, l2[0].Length, TagLanguageMode.Html));
            Assert.Null(EditorTagEditing.GetCloseTagCompletion(L("a </"), 0, 4, TagLanguageMode.Html));
        }

        [Fact]
        public void Tags_LinkedRename_OpenAndCloseTags_NestedSameName()
        {
            var lines = L("<div>\n  <div>inner</div>\n</div>");
            Assert.True(EditorTagEditing.TryFindLinkedTag(lines, 0, 2, TagLanguageMode.Html, out TagInfo self, out TagInfo partner));
            Assert.Equal(2, partner.Line);
            Assert.True(EditorTagEditing.TryFindLinkedTag(lines, 1, 16, TagLanguageMode.Html, out self, out partner));
            Assert.True(self.IsClose);
            Assert.Equal(1, partner.Line);
            Assert.Equal(3, partner.NameStart);

            // typing in the start tag name renames the end tag
            var le = EditorTagEditing.BeginLinkedEdit(lines, 0, 4, TagLanguageMode.Html);
            Assert.NotNull(le);
            lines[0] = "<divx>";
            Assert.Equal(0, EditorTagEditing.CompleteLinkedEdit(lines, le, 0, 5));
            Assert.Equal("</divx>", lines[2]);
            // deleting the whole name and typing a new one keeps them linked (empty name state)
            le = EditorTagEditing.BeginLinkedEdit(lines, 0, 5, TagLanguageMode.Html);
            lines[0] = "<>";
            EditorTagEditing.CompleteLinkedEdit(lines, le, 0, 1);
            Assert.Equal("</>", lines[2]);
            le = EditorTagEditing.BeginLinkedEdit(lines, 0, 1, TagLanguageMode.Html);
            lines[0] = "<p>";
            EditorTagEditing.CompleteLinkedEdit(lines, le, 0, 2);
            Assert.Equal("</p>", lines[2]);
            Assert.Equal("  <div>inner</div>", lines[1]); // the nested pair is untouched

            // editing the end tag renames the start tag on the same line, shifting the caret
            var one = L("<span>x</span>");
            le = EditorTagEditing.BeginLinkedEdit(one, 0, 13, TagLanguageMode.Html);
            one[0] = "<span>x</spanx>";
            int delta = EditorTagEditing.CompleteLinkedEdit(one, le, 0, 14);
            Assert.Equal("<spanx>x</spanx>", one[0]);
            Assert.Equal(1, delta);
            // typing a space after the name (starting attributes) does not rename
            var sp = L("<a>x</a>");
            le = EditorTagEditing.BeginLinkedEdit(sp, 0, 2, TagLanguageMode.Html);
            sp[0] = "<a >x</a>";
            Assert.Equal(int.MinValue, EditorTagEditing.CompleteLinkedEdit(sp, le, 0, 3));
            Assert.Equal("<a >x</a>", sp[0]);
            // self-closing and void elements have no partner
            Assert.False(EditorTagEditing.TryFindLinkedTag(L("<br><x/>"), 0, 2, TagLanguageMode.Html, out self, out partner));
            Assert.False(EditorTagEditing.TryFindLinkedTag(L("<br><x/>"), 0, 6, TagLanguageMode.Xml, out self, out partner));
            // script contents are not scanned for tags
            var scr = L("<div><script>if (a<b && c>d) {}</script></div>");
            Assert.True(EditorTagEditing.TryFindLinkedTag(scr, 0, 2, TagLanguageMode.Html, out self, out partner));
            Assert.Equal(scr[0].LastIndexOf("</div>") + 2, partner.NameStart);
        }

        // ------------------------------------------------------------------ Emmet
        private static string Expand(string line, EmmetSyntax syntax, bool inRule, out int caret)
        {
            int start;
            string exp;
            caret = -1;
            if (!EmmetExpander.TryExpand(line, line.Length, syntax, "  ", inRule, out start, out exp, out caret)) return null;
            return line.Substring(0, start) + exp;
        }

        [Fact]
        public void Emmet_HtmlAbbreviations()
        {
            int c;
            Assert.Equal("<div class=\"card\">\n  <ul>\n    <li></li>\n    <li></li>\n    <li></li>\n  </ul>\n</div>", Expand("div.card>ul>li*3", EmmetSyntax.Html, false, out c));
            Assert.Equal("<a href=\"\"></a>", Expand("a[href]", EmmetSyntax.Html, false, out c));
            Assert.Equal(9, c); // caret inside href=""
            Assert.Equal("<p>text</p>", Expand("p{text}", EmmetSyntax.Html, false, out c));
            Assert.Equal("<ul>\n  <li class=\"item1\"></li>\n  <li class=\"item2\"></li>\n</ul>", Expand("ul>li.item$*2", EmmetSyntax.Html, false, out c));
            Assert.Equal("<div id=\"main\" class=\"a b\"></div>", Expand("#main.a.b", EmmetSyntax.Html, false, out c));
            Assert.Equal("<input type=\"email\" name=\"q\">", Expand("input[type=email name=q]", EmmetSyntax.Html, false, out c));
            Assert.Equal("<header>\n  <nav></nav>\n</header>\n<main></main>", Expand("(header>nav)+main", EmmetSyntax.Html, false, out c));
            Assert.Equal("<div>\n  <p></p>\n</div>\n<span></span>", Expand("div>p^span", EmmetSyntax.Html, false, out c));
            Assert.Equal("<table>\n  <tr class=\"r\">\n    <td class=\"c\"></td>\n  </tr>\n</table>", Expand("table>.r>.c", EmmetSyntax.Html, false, out c));
            Assert.Equal("<h3>02</h3>\n<h2>03</h2>", Expand("h$@-2{$$@2}*2", EmmetSyntax.Html, false, out c));
            Assert.Contains("<!DOCTYPE html>", Expand("!", EmmetSyntax.Html, false, out c));
            // indentation of the current line is applied to every expanded line
            Assert.Equal("    <ul>\n      <li></li>\n    </ul>", Expand("    ul>li", EmmetSyntax.Html, false, out c));
        }

        [Fact]
        public void Emmet_JsxUsesClassNameAndSelfClosingVoids()
        {
            int c;
            Assert.Equal("  <div className=\"row\">\n    <img src=\"\" alt=\"\" />\n  </div>", Expand("  div.row>img", EmmetSyntax.Jsx, false, out c));
            Assert.Equal("return (<label htmlFor=\"x\"></label>", Expand("return (label[for=x]", EmmetSyntax.Jsx, false, out c));
            Assert.Equal("<MyButton></MyButton>", Expand("MyButton", EmmetSyntax.Jsx, false, out c));
        }

        [Fact]
        public void Emmet_LeavesTabAloneWhenNothingMatches()
        {
            int c;
            Assert.Null(Expand("hello world", EmmetSyntax.Html, false, out c));   // not a tag
            Assert.Null(Expand("this.props", EmmetSyntax.Jsx, false, out c));     // member access
            Assert.Null(Expand("const x = div", EmmetSyntax.Jsx, false, out c));  // JS expression context
            Assert.Null(Expand("<div", EmmetSyntax.Html, false, out c));          // inside a tag
            Assert.Null(Expand("<a href=\"x\" title", EmmetSyntax.Html, false, out c));
            Assert.Null(Expand("end.", EmmetSyntax.Html, false, out c));
            Assert.Null(Expand("", EmmetSyntax.Html, false, out c));
            Assert.Null(Expand("x !", EmmetSyntax.Html, false, out c));
            int st; string exp; int car;
            Assert.False(EmmetExpander.TryExpand("divxyz", 3, EmmetSyntax.Html, "  ", false, out st, out exp, out car)); // caret inside a word
        }

        [Fact]
        public void Emmet_CssProperties()
        {
            int c;
            Assert.Equal("  margin: 10px;", Expand("  m10", EmmetSyntax.Css, true, out c));
            Assert.Equal("margin: 10px 20px;", Expand("m10-20", EmmetSyntax.Css, true, out c));
            Assert.Equal("margin: -10px;", Expand("m-10", EmmetSyntax.Css, true, out c));
            Assert.Equal("width: 100%;", Expand("w100p", EmmetSyntax.Css, true, out c));
            Assert.Equal("line-height: 1.5;", Expand("lh1.5", EmmetSyntax.Css, true, out c));
            Assert.Equal("z-index: 10;", Expand("z10", EmmetSyntax.Css, true, out c));
            Assert.Equal("color: #fff;", Expand("c#fff", EmmetSyntax.Css, true, out c));
            Assert.Equal("background-color: #000;", Expand("bgc#0", EmmetSyntax.Css, true, out c));
            Assert.Equal("padding: 0;", Expand("p0", EmmetSyntax.Css, true, out c));
            Assert.Equal("display: flex;", Expand("df", EmmetSyntax.Css, true, out c));
            Assert.Equal("position: absolute;", Expand("pos:a", EmmetSyntax.Css, true, out c));
            Assert.Equal("justify-content: space-between;", Expand("jcsb", EmmetSyntax.Css, true, out c));
            Assert.Equal("font-size: 2rem;", Expand("fz2r", EmmetSyntax.Css, true, out c));
            Assert.Equal("margin: ;", Expand("m", EmmetSyntax.Css, true, out c));
            Assert.Equal(8, c); // caret before ';'
            Assert.Null(Expand("m10", EmmetSyntax.Css, false, out c));   // outside a rule: selector context
            Assert.Null(Expand("color: m10", EmmetSyntax.Css, true, out c));
            Assert.Null(Expand("xyz10", EmmetSyntax.Css, true, out c));
            Assert.True(EmmetExpander.IsInsideCssRule(L(".a {\n  m10"), 1, 5));
            Assert.False(EmmetExpander.IsInsideCssRule(L(".a { }\n/* { */ m10"), 1, 11));
        }
    }
}
