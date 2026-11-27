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
    /// v3.3.1 macOS report: "In big JSON in .py files, text is shown as variables despite being in quotes." The editor
    /// tokenized only the rendered slice of a line (horizontal virtualization / word-wrap rows), so a slice that started
    /// inside a string flipped the quote parity. Also: multi-line strings / comments on every line they span.
    /// </summary>
    public class TokenizationSliceCoreTests
    {
        private readonly ITestOutputHelper output;
        public TokenizationSliceCoreTests(ITestOutputHelper output) { this.output = output; }

        private static CodeTokenKind KindAt(List<CodeToken> toks, int col)
        {
            var t = toks.FirstOrDefault(x => col >= x.Start && col < x.Start + x.Length);
            return t == null ? CodeTokenKind.Text : t.Kind;
        }

        private static string BigPythonDictLine(int entries)
        {
            var sb = new StringBuilder("DATA = {");
            for (int i = 0; i < entries; i++)
                sb.Append("\"key_" + i + "\": \"some value with words and, commas " + i + "\", ");
            sb.Append("\"end\": 1}");
            return sb.ToString();
        }

        [Fact]
        public void SliceStartingInsideAString_KeepsStringColor_WhenTokenizedFromLineStart()
        {
            string line = BigPythonDictLine(40);
            int col = line.IndexOf("words and", 600, StringComparison.Ordinal);
            Assert.True(col > 0);
            int sliceStart = col - 3;      // inside the quoted value
            string slice = line.Substring(sliceStart, 200);

            // the old behaviour (tokenize the slice): the words inside the quotes become plain identifiers
            var sliceToks = FallbackTokenizer.Tokenize(slice, "py");
            Assert.NotEqual(CodeTokenKind.String, KindAt(sliceToks, col - sliceStart));

            // the fix: whole-line tokens cut to the slice
            var full = FallbackTokenizer.Tokenize(line, "py");
            Assert.Equal(CodeTokenKind.String, KindAt(full, col));
            var theme = StudioTheme.GetById(StudioTheme.DefaultThemeId);
            var spans = FallbackTokenStyles.SliceSpans(full, line.Length, sliceStart, sliceStart + 200, theme, false);
            Assert.Equal(sliceStart, spans[0].Start);
            Assert.Equal(200, spans.Sum(s => s.Length));
            for (int k = 1; k < spans.Count; k++) Assert.Equal(spans[k - 1].Start + spans[k - 1].Length, spans[k].Start);
            var at = spans.First(s => col >= s.Start && col < s.Start + s.Length);
            Assert.Equal(theme.TokenString, at.Color);
            // a key starting after the slice start is a string too, the ':' between is not
            int key = line.IndexOf("\"key_", col, StringComparison.Ordinal);
            Assert.Equal(CodeTokenKind.String, KindAt(full, key + 2));
            Assert.NotEqual(CodeTokenKind.String, KindAt(full, line.IndexOf(':', key)));
        }

        [Fact]
        public void PythonTripleQuotedJsonBlock_IsStringOnEveryLine()
        {
            var lines = new List<string>
            {
                "payload = \"\"\"",
                "{",
                "  \"name\": \"demo\", \"count\": 3, \"note\": \"it's # not a comment\"",
                "}",
                "\"\"\"",
                "x = name  # real comment",
                "s = '''one''' + '''two",
                "  still two'''",
                "y = 1"
            };
            var cache = new FallbackTokenCache("py");
            cache.Sync(lines);
            bool guess;
            Func<int, List<CodeToken>> T = i => cache.GetTokens(lines, i, 0, out guess);
            Assert.Equal(CodeTokenKind.String, KindAt(T(0), 10));
            Assert.Equal(CodeTokenKind.String, KindAt(T(1), 0));
            Assert.Equal(CodeTokenKind.String, KindAt(T(2), lines[2].IndexOf("count")));
            Assert.Equal(CodeTokenKind.String, KindAt(T(2), lines[2].IndexOf("# not")));
            Assert.Equal(CodeTokenKind.String, KindAt(T(4), 1));
            Assert.Equal(CodeTokenKind.Text, KindAt(T(5), lines[5].IndexOf("name")));
            Assert.Equal(CodeTokenKind.Comment, KindAt(T(5), lines[5].IndexOf("# real")));
            Assert.Equal(CodeTokenKind.String, KindAt(T(6), lines[6].IndexOf("two")));
            Assert.Equal(CodeTokenKind.String, KindAt(T(7), 4));
            Assert.Equal(CodeTokenKind.Number, KindAt(T(8), 4));

            // closing the block on another line re-colors the following lines (incremental)
            lines[3] = "}\"\"\"";
            lines[4] = "z = 2";
            cache.Sync(lines);
            Assert.Equal(CodeTokenKind.Number, KindAt(T(4), 4));
            Assert.Equal(CodeTokenKind.Comment, KindAt(T(5), lines[5].IndexOf("# real")));
        }

        [Fact]
        public void MultiLineConstructs_CSharpJsGoCssSqlMarkup()
        {
            Func<string, string[], int, int, CodeTokenKind> K = (lang, ls, line, col) =>
            {
                var lines = ls.ToList();
                var c = new FallbackTokenCache(lang);
                c.Sync(lines);
                bool g;
                return KindAt(c.GetTokens(lines, line, 0, out g), col);
            };
            var cs = new[] { "var a = @\"first", "second \"\" still", "end\"; int x = 1;", "var r = \"\"\"", "  { \"json\": true }", "  \"\"\"; return 2;", "/* block", " int notCode = 3;", "*/ int code = 4;" };
            Assert.Equal(CodeTokenKind.String, K("cs", cs, 1, 0));
            Assert.Equal(CodeTokenKind.String, K("cs", cs, 1, 12));
            Assert.Equal(CodeTokenKind.String, K("cs", cs, 2, 0));
            Assert.NotEqual(CodeTokenKind.String, K("cs", cs, 2, cs[2].IndexOf("int")));
            Assert.Equal(CodeTokenKind.String, K("cs", cs, 4, 4));
            Assert.NotEqual(CodeTokenKind.String, K("cs", cs, 5, cs[5].IndexOf("return")));
            Assert.Equal(CodeTokenKind.Comment, K("cs", cs, 7, 2));
            Assert.NotEqual(CodeTokenKind.Comment, K("cs", cs, 8, cs[8].IndexOf("code")));

            var js = new[] { "const t = `line one", "  ${value} two", "`; const n = 5;" };
            Assert.Equal(CodeTokenKind.String, K("js", js, 1, 4));
            Assert.Equal(CodeTokenKind.Number, K("js", js, 2, js[2].IndexOf("5")));
            var go = new[] { "s := `raw \\", "still raw`", "n := 7" };
            Assert.Equal(CodeTokenKind.String, K("go", go, 1, 2));
            Assert.Equal(CodeTokenKind.Number, K("go", go, 2, 5));
            var css = new[] { "a { /* note", "color: red; */ color: blue; }" };
            Assert.Equal(CodeTokenKind.Comment, K("css", css, 1, 2));
            Assert.NotEqual(CodeTokenKind.Comment, K("css", css, 1, css[1].IndexOf("blue")));
            var sql = new[] { "SELECT 1 /* multi", "line */ FROM t" };
            Assert.Equal(CodeTokenKind.Comment, K("sql", sql, 1, 0));
            var html = new[] { "<p>x</p> <!-- start", "<b>commented</b>", "--> <i>y</i>" };
            Assert.Equal(CodeTokenKind.Comment, K("html", html, 1, 3));
            Assert.NotEqual(CodeTokenKind.Comment, K("html", html, 2, 6));
            // empty lines inside a block stay inside it
            var gap = new[] { "/*", "", "inside", "*/ x" };
            Assert.Equal(CodeTokenKind.Comment, K("cs", gap, 2, 1));
        }

        [Fact]
        public void TokenCache_KeyedByLineReference_AndStatePassIsAllocationFree()
        {
            var lines = new List<string> { "a = \"x\"", "b = 2" };
            var c = new FallbackTokenCache("py");
            c.Sync(lines);
            bool g;
            var t1 = c.GetTokens(lines, 0, 0, out g);
            var t2 = c.GetTokens(lines, 0, 0, out g);
            Assert.Same(t1, t2);
            long before = c.ProcessedLineCount;
            c.GetTokens(lines, 1, 0, out g);
            c.GetTokens(lines, 1, 0, out g);
            Assert.True(c.ProcessedLineCount - before <= 2, "line 1: one state scan + one tokenization, then cached");
            lines[0] = "a = \"y\"";
            c.Sync(lines);
            Assert.NotSame(t1, c.GetTokens(lines, 0, 0, out g));
        }

        [Fact]
        public void OneMegabyteSingleLineJson_TokenizesOnce_AndSlicesInMicroseconds()
        {
            var sb = new StringBuilder("[");
            int i = 0;
            while (sb.Length < 1024 * 1024) { sb.Append("{\"id\":" + i + ",\"name\":\"item \\\"" + i + "\\\" with spaces\",\"tags\":[\"a\",\"b\"],\"ok\":true},"); i++; }
            sb.Append("{}]");
            string line = sb.ToString();
            var lines = new List<string> { line };
            var session = new EditorSyntaxSession();
            var theme = StudioTheme.GetById(StudioTheme.DefaultThemeId);
            bool g;
            // Warm up FallbackTokenizer JIT on a tiny line before measuring the 1 MB line.
            new EditorSyntaxSession().GetFallbackSpans(new List<string> { "{\"a\":1}" }, 0, "json", theme, 0, 7, false, 0, out g);
            var sw = Stopwatch.StartNew();
            var first = session.GetFallbackSpans(lines, 0, "json", theme, 700000, 700200, false, 0, out g);
            long firstMs = sw.ElapsedMilliseconds;
            // Warm up cached-slice path once before measuring 200 cached slices.
            session.GetFallbackSpans(lines, 0, "json", theme, 0, 200, false, 0, out g);
            sw.Restart();
            for (int k = 0; k < 200; k++) session.GetFallbackSpans(lines, 0, "json", theme, 1000 * k, 1000 * k + 200, false, 0, out g);
            double perFrameMs = sw.Elapsed.TotalMilliseconds / 200;
            output.WriteLine("1 MB JSON line: first tokenization " + firstMs + " ms, cached slice " + perFrameMs.ToString("0.000") + " ms");
            Assert.True(firstMs < 1500, "first tokenization " + firstMs + " ms");
            Assert.True(perFrameMs < 2, "per frame " + perFrameMs + " ms");
            Assert.Equal(200, first.Sum(s => s.Length));
            // every "name" value in the slice is string colored even though the slice starts mid-line
            int nameAt = line.IndexOf("with spaces", 700000, StringComparison.Ordinal);
            var spans = session.GetFallbackSpans(lines, 0, "json", theme, nameAt - 5, nameAt + 20, false, 0, out g);
            Assert.Equal(theme.TokenString, spans.First(s => nameAt >= s.Start && nameAt < s.Start + s.Length).Color);
        }

        // ------------------------------------------------------------------ bundled Python / JSON grammars
        private static string ScopeAt(TmLineTokens t, int col)
        {
            for (int i = 0; i < t.Count; i++)
                if (col >= t.Starts[i] && col < t.EndOf(i)) return t.Scopes[i].ToString();
            return null;
        }

        [Fact]
        public void BundledPythonAndJsonGrammars_HighlightLikeVsCode()
        {
            var reg = TextMateGrammarRegistry.Shared;
            Assert.Equal("source.python", reg.GetScopeForFile("/x/app.py"));
            Assert.Equal("source.json", reg.GetScopeForFile("/x/package.json"));
            TextMateDiagnostics.Clear();
            var py = reg.GetCompiled("source.python", true);
            Assert.NotNull(py);
            string[] src = { "import json", "def load(path: str) -> dict:", "    \"\"\"Doc", "    {\"a\": 1}\"\"\"", "    data = {\"key\": \"value\", 'n': 42}  # note", "    return data" };
            TmStateStack st = null;
            var res = new List<TmLineResult>();
            for (int i = 0; i < src.Length; i++) { var r = py.TokenizeLine(src[i], i == 0 ? null : st); res.Add(r); st = r.EndState; }
            Assert.Contains("keyword.control.import", ScopeAt(res[0].Tokens, 0));
            Assert.Contains("entity.name.function", ScopeAt(res[1].Tokens, 4));
            Assert.Contains("string.quoted.docstring", ScopeAt(res[3].Tokens, 6));
            Assert.Contains("string.quoted", ScopeAt(res[4].Tokens, src[4].IndexOf("value")));
            Assert.Contains("constant.numeric", ScopeAt(res[4].Tokens, src[4].IndexOf("42")));
            Assert.Contains("comment.line", ScopeAt(res[4].Tokens, src[4].IndexOf("note")));
            Assert.Contains("keyword.control.flow", ScopeAt(res[5].Tokens, 4));

            var json = reg.GetCompiled("source.json", true);
            Assert.NotNull(json);
            var j = json.TokenizeLine("{\"name\": \"codeLess\", \"n\": 3, \"ok\": true, \"x\": null}", null);
            Assert.Contains("support.type.property-name.json", ScopeAt(j.Tokens, 2));
            Assert.Contains("string.quoted.double.json", ScopeAt(j.Tokens, 11));
            Assert.Contains("constant.numeric", ScopeAt(j.Tokens, 26));
            Assert.Contains("constant.language", ScopeAt(j.Tokens, 35));
            foreach (string e in TextMateDiagnostics.Snapshot()) output.WriteLine(e);
        }
    }
}
