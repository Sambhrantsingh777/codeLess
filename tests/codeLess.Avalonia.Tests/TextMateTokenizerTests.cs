using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using codeLess.Studio;
using Xunit;
using Xunit.Abstractions;

namespace codeLess.Avalonia.Tests
{
    public class TextMateTokenizerTests
    {
        private readonly ITestOutputHelper output;
        public TextMateTokenizerTests(ITestOutputHelper output) { this.output = output; }

        // ------------------------------------------------------------------ Oniguruma translation
        [Theory]
        [InlineData(@"\h+", "[0-9a-fA-F]+")]
        [InlineData(@"[\h_]", "[0-9a-fA-F_]")]
        [InlineData(@"a*+b", "(?>a*)b")]
        [InlineData(@"(ab)++", "(?>(ab)+)")]
        [InlineData(@"[a-z]?+", "(?>[a-z]?)")]
        [InlineData(@"x{2}+", "(?:x{2})+")]
        [InlineData(@"[[:alpha:]_]", @"[\p{L}\p{M}_]")]
        [InlineData(@"[^[:space:]]", @"[^\s]")]
        [InlineData(@"\x{41}", @"\u0041")]
        [InlineData(@"(?m).", "(?s).")]
        [InlineData(@"[a[bc]]", "[abc]")]
        [InlineData(@"\p{Alnum}", @"[\p{L}\p{M}\p{Nd}]")]
        public void Translator_ConvertsOnigurumaConstructs(string onig, string expected)
        {
            Assert.Equal(expected, OnigurumaTranslator.Translate(onig, true, true));
        }

        [Fact]
        public void Translator_ExtendedModeStripsCommentsAndWhitespace()
        {
            string net = OnigurumaTranslator.Translate("(?x)  a   # letter a\n  [ b]  # class keeps space\n c", true, true);
            Assert.Equal("a[ b]c", net);
            Assert.Matches(new Regex(net), "a c");
        }

        [Fact]
        public void Translator_AnchorsCanBeDisabled_AndUnsupportedConstructsAreRejected()
        {
            Assert.Equal(@"(?!)x", OnigurumaTranslator.Translate(@"\Gx", true, false));
            Assert.Equal(@"\Gx", OnigurumaTranslator.Translate(@"\Gx", true, true));
            Assert.Equal(@"(?!)x", OnigurumaTranslator.Translate(@"\Ax", false, true));
            Assert.Null(OnigurumaTranslator.Translate(@"foo\Kbar", true, true));
            Assert.Null(OnigurumaTranslator.Translate(@"(?~abc)", true, true));
            // a rule with an untranslatable regex never matches (instead of throwing)
            Assert.Null(new TmRegex(@"a\Kb").Get(true, true));
        }

        [Fact]
        public void Translator_ClassAlgebra_NamedGroupsAndCalls()
        {
            // intersection, nested negated union, multi-code-point \x{...}
            var inter = new Regex(OnigurumaTranslator.Translate(@"^[a-z&&[^aeiou]]+$", true, true));
            Assert.Matches(inter, "bcd");
            Assert.DoesNotMatch(inter, "bad");
            var union = new Regex(OnigurumaTranslator.Translate(@"^[x[^a-z]]+$", true, true));
            Assert.Matches(union, "x1X");
            Assert.DoesNotMatch(union, "xa");
            var multi = new Regex(OnigurumaTranslator.Translate(@"^[\x{41 42}]+$", true, true));
            Assert.Matches(multi, "ABBA");
            // Oniguruma numbers named and plain groups left to right (unlike .NET)
            var named = new Regex(OnigurumaTranslator.Translate(@"(a)(?<x>b)(c)(?<y>d)", true, true));
            var m = named.Match("abcd");
            Assert.Equal("b", m.Groups[2].Value);
            Assert.Equal("d", m.Groups[4].Value);
            // subexpression calls are inlined (recursion to a fixed depth): nested generics
            var gen = new Regex("^" + OnigurumaTranslator.Translate(@"(?<t>\w+(?:<\g<t>(?:,\s*\g<t>)*>)?)", true, true) + "$");
            Assert.Matches(gen, "Dictionary<string, List<int>>");
            Assert.DoesNotMatch(gen, "List<>");
            // conditionals
            var cond = new Regex(OnigurumaTranslator.Translate(@"^(?<q>')?x(?(<q>)')$", true, true));
            Assert.Matches(cond, "'x'");
            Assert.Matches(cond, "x");
            Assert.DoesNotMatch(cond, "'x");
        }

        [Fact]
        public void TmRegex_PossessiveBehavesAtomically()
        {
            var rx = new TmRegex("\"[^\"]*+\"").Get(false, false);
            Assert.True(rx.IsMatch("x = \"abc\";"));
            var atomic = new TmRegex("a*+a").Get(false, false);
            Assert.False(atomic.IsMatch("aaaa")); // possessive a*+ leaves nothing for the final a
        }

        // ------------------------------------------------------------------ small grammars
        private const string MiniGrammar = @"{
  // comments and trailing commas are tolerated
  ""scopeName"": ""source.mini"",
  ""patterns"": [ { ""include"": ""#comment"" }, { ""include"": ""#string"" }, { ""include"": ""#block"" },
                  { ""match"": ""\\b(if|else|return)\\b"", ""name"": ""keyword.control.mini"" },
                  { ""match"": ""\\b(def)\\s+([A-Za-z_]\\w*)"", ""captures"": { ""1"": { ""name"": ""storage.type.mini"" }, ""2"": { ""name"": ""entity.name.function.mini"" } } },
                  { ""match"": ""\\b\\d+\\b"", ""name"": ""constant.numeric.mini"" },
                  { ""begin"": ""<<(\\w+)"", ""end"": ""^\\1$"", ""name"": ""string.unquoted.heredoc.mini"" },
                  { ""begin"": ""^>"", ""while"": ""^>"", ""name"": ""markup.quote.mini"", ""patterns"": [ { ""include"": ""$self"" } ] }, ],
  ""repository"": {
    ""comment"": { ""patterns"": [ { ""begin"": ""/\\*"", ""end"": ""\\*/"", ""name"": ""comment.block.mini"" },
                                 { ""match"": ""//.*$"", ""name"": ""comment.line.mini"" } ] },
    ""string"": { ""begin"": ""\"""", ""end"": ""\"""", ""name"": ""string.quoted.double.mini"",
                ""patterns"": [ { ""match"": ""\\\\."", ""name"": ""constant.character.escape.mini"" },
                               { ""begin"": ""\\$\\{"", ""end"": ""\\}"", ""name"": ""meta.interpolation.mini"", ""patterns"": [ { ""include"": ""$self"" } ] } ] },
    ""block"": { ""begin"": ""\\{"", ""end"": ""\\}"", ""name"": ""meta.block.mini"", ""patterns"": [ { ""include"": ""$self"" } ] },
  },
}";

        private static TmCompiledGrammar Mini()
        {
            var raw = TextMateGrammarLoader.LoadText(MiniGrammar, "mini");
            Assert.NotNull(raw);
            return new TmCompiledGrammar(raw, null);
        }

        private static string ScopeAt(TmLineTokens t, int col)
        {
            for (int i = 0; i < t.Count; i++)
                if (col >= t.Starts[i] && col < t.EndOf(i)) return t.Scopes[i].ToString();
            return null;
        }

        [Fact]
        public void Tokenizer_MatchCapturesIncludesAndRepository()
        {
            var g = Mini();
            var r = g.TokenizeLine("def foo() return 42 // done", null);
            Assert.Equal("source.mini storage.type.mini", ScopeAt(r.Tokens, 0));
            Assert.Equal("source.mini entity.name.function.mini", ScopeAt(r.Tokens, 5));
            Assert.Equal("source.mini keyword.control.mini", ScopeAt(r.Tokens, 11));
            Assert.Equal("source.mini constant.numeric.mini", ScopeAt(r.Tokens, 18));
            Assert.Equal("source.mini comment.line.mini", ScopeAt(r.Tokens, 22));
            Assert.Equal("source.mini", ScopeAt(r.Tokens, 3));
        }

        [Fact]
        public void Tokenizer_NestedBeginEnd_WithSelfInclude_AcrossLines()
        {
            var g = Mini();
            var l1 = g.TokenizeLine("x = \"a\\n ${ { 1 } } b", null);
            Assert.Equal("source.mini string.quoted.double.mini constant.character.escape.mini", ScopeAt(l1.Tokens, 6));
            Assert.Equal("source.mini string.quoted.double.mini meta.interpolation.mini meta.block.mini constant.numeric.mini", ScopeAt(l1.Tokens, 14));
            Assert.Equal("source.mini string.quoted.double.mini", ScopeAt(l1.Tokens, 20));
            // the string is still open: the next line continues inside it
            var l2 = g.TokenizeLine("still string\" if", l1.EndState);
            Assert.Equal("source.mini string.quoted.double.mini", ScopeAt(l2.Tokens, 2));
            Assert.Equal("source.mini keyword.control.mini", ScopeAt(l2.Tokens, 15));
            Assert.Equal(1, l2.EndState.Depth); // back at the root
        }

        [Fact]
        public void Tokenizer_BlockCommentSpansLines_AndEndBackReferences()
        {
            var g = Mini();
            var a = g.TokenizeLine("/* start", null);
            var b = g.TokenizeLine("middle if 1", a.EndState);
            Assert.Equal("source.mini comment.block.mini", ScopeAt(b.Tokens, 7));
            var c = g.TokenizeLine("end */ if", b.EndState);
            Assert.Equal("source.mini keyword.control.mini", ScopeAt(c.Tokens, 7));

            var h1 = g.TokenizeLine("cat <<EOF", null);
            var h2 = g.TokenizeLine("if 1 return", h1.EndState);
            Assert.Equal("source.mini string.unquoted.heredoc.mini", ScopeAt(h2.Tokens, 0));
            var h3 = g.TokenizeLine("EOF", h2.EndState);
            var h4 = g.TokenizeLine("if", h3.EndState);
            Assert.Equal("source.mini keyword.control.mini", ScopeAt(h4.Tokens, 0));
        }

        [Fact]
        public void Tokenizer_WhileRuleContinuesOnlyWhileConditionMatches()
        {
            var g = Mini();
            var a = g.TokenizeLine("> quoted if", null);
            Assert.Equal("source.mini markup.quote.mini keyword.control.mini", ScopeAt(a.Tokens, 9));
            var b = g.TokenizeLine("> still", a.EndState);
            Assert.Equal("source.mini markup.quote.mini", ScopeAt(b.Tokens, 3));
            var c = g.TokenizeLine("plain if", b.EndState);
            Assert.Equal("source.mini", ScopeAt(c.Tokens, 1));
            Assert.Equal("source.mini keyword.control.mini", ScopeAt(c.Tokens, 6));
        }

        [Fact]
        public void Tokenizer_EmptyMatchesDoNotLoopForever_AndBudgetStopsPathologicalLines()
        {
            string grammar = @"{ ""scopeName"": ""source.loop"", ""patterns"": [ { ""match"": ""(?=x)"", ""name"": ""empty"" },
                                { ""begin"": ""(?=y)"", ""end"": ""(?=y)"", ""name"": ""zero.block"" } ] }";
            var g = new TmCompiledGrammar(TextMateGrammarLoader.LoadText(grammar, "loop"), null);
            var r = g.TokenizeLine("abc x y x y", null);
            Assert.NotNull(r.Tokens);
            Assert.True(r.Tokens.Count >= 1);

            // catastrophic regex: the per-line budget / regex timeout keeps the tokenizer responsive
            string bad = @"{ ""scopeName"": ""source.bad"", ""patterns"": [ { ""match"": ""(a+)+$"", ""name"": ""bad"" } ] }";
            var gb = new TmCompiledGrammar(TextMateGrammarLoader.LoadText(bad, "bad"), null);
            gb.LineTimeBudgetMs = 50;
            var sw = Stopwatch.StartNew();
            gb.TokenizeLine(new string('a', 40) + "!", null);
            Assert.True(sw.ElapsedMilliseconds < 3000, "tokenizer did not stop: " + sw.ElapsedMilliseconds + " ms");
        }

        [Fact]
        public void PlistGrammar_IsParsed()
        {
            string plist = @"<?xml version=""1.0"" encoding=""UTF-8""?>
<!DOCTYPE plist PUBLIC ""-//Apple//DTD PLIST 1.0//EN"" ""http://www.apple.com/DTDs/PropertyList-1.0.dtd"">
<plist version=""1.0""><dict>
  <key>scopeName</key><string>source.pl</string>
  <key>fileTypes</key><array><string>pl</string></array>
  <key>patterns</key>
  <array>
    <dict><key>match</key><string>\b(foo)\b</string><key>name</key><string>keyword.other.pl</string></dict>
    <dict><key>begin</key><string>&lt;!--</string><key>end</key><string>--&gt;</string><key>name</key><string>comment.block.pl</string></dict>
  </array>
</dict></plist>";
            var raw = TextMateGrammarLoader.LoadText(plist, "x.tmLanguage");
            Assert.Equal("source.pl", raw.ScopeName);
            Assert.Contains("pl", raw.FileTypes);
            var g = new TmCompiledGrammar(raw, null);
            var r = g.TokenizeLine("x foo <!-- c --> foo", null);
            Assert.Equal("source.pl keyword.other.pl", ScopeAt(r.Tokens, 2));
            Assert.Equal("source.pl comment.block.pl", ScopeAt(r.Tokens, 8));
        }

        [Fact]
        public void ExternalGrammarInclude_ResolvesThroughLookup()
        {
            string outer = @"{ ""scopeName"": ""source.outer"", ""patterns"": [ { ""begin"": ""```mini"", ""end"": ""```"", ""name"": ""meta.embedded"", ""patterns"": [ { ""include"": ""source.mini"" } ] } ] }";
            var mini = TextMateGrammarLoader.LoadText(MiniGrammar, "mini");
            var g = new TmCompiledGrammar(TextMateGrammarLoader.LoadText(outer, "outer"), s => s == "source.mini" ? mini : null);
            var r = g.TokenizeLine("```mini if 1 ``` if", null);
            Assert.Equal("source.outer meta.embedded keyword.control.mini", ScopeAt(r.Tokens, 8));
            Assert.Equal("source.outer", ScopeAt(r.Tokens, 18));
        }

        // ------------------------------------------------------------------ incremental document tokenization
        [Fact]
        public void Document_IncrementalRetokenization_ConvergesAfterEditedLine()
        {
            var g = Mini();
            var lines = new List<string>();
            for (int i = 0; i < 2000; i++) lines.Add(i % 10 == 0 ? "def f" + i + "() { return " + i + " }" : "  x = \"s\" // c " + i);
            var doc = new TextMateDocument(g);
            doc.Sync(lines);
            Assert.True(doc.EnsureValid(lines, lines.Count - 1, 0));
            long baseCount = doc.ProcessedLineCount;
            Assert.Equal(2000, baseCount);

            // editing one line in the middle re-processes just that line (state converges immediately)
            lines[1000] = lines[1000] + " if";
            doc.Sync(lines);
            Assert.Equal(1000, doc.ValidCount);
            doc.EnsureValid(lines, lines.Count - 1, 0);
            Assert.Equal(1, doc.ProcessedLineCount - baseCount);

            // opening a block comment changes every following state ...
            long before = doc.ProcessedLineCount;
            lines[500] = "/* open";
            doc.Sync(lines);
            doc.EnsureValid(lines, lines.Count - 1, 0);
            Assert.Equal(1500, doc.ProcessedLineCount - before);
            bool guess;
            Assert.Equal("source.mini comment.block.mini", ScopeAt(doc.GetTokens(lines, 1999, 1999, 0, out guess), 3));

            // ... and closing it again converges right after the edit
            before = doc.ProcessedLineCount;
            lines[500] = "/* open */";
            doc.Sync(lines);
            doc.EnsureValid(lines, lines.Count - 1, 0);
            Assert.True(doc.ProcessedLineCount - before <= 1500);
            Assert.Equal("source.mini comment.line.mini", ScopeAt(doc.GetTokens(lines, 1999, 1999, 0, out guess), 12));

            // inserting / deleting lines keeps the unchanged suffix (convergence after the insertion point)
            before = doc.ProcessedLineCount;
            lines.Insert(700, "  y = 1");
            doc.Sync(lines);
            doc.EnsureValid(lines, lines.Count - 1, 0);
            lines.RemoveAt(1200);
            doc.Sync(lines);
            doc.EnsureValid(lines, lines.Count - 1, 0);
            Assert.True(doc.ProcessedLineCount - before < 10, "processed " + (doc.ProcessedLineCount - before));
            Assert.Equal(2000, doc.LineCount);
        }

        [Fact]
        public void Document_DeadlineLimitsWork_AndGuessesUnreachedLines()
        {
            var g = Mini();
            var lines = new List<string>();
            lines.Add("/* everything below is a comment");
            for (int i = 0; i < 50000; i++) lines.Add("if " + i);
            var doc = new TextMateDocument(g);
            doc.Sync(lines);
            bool guess;
            // a deadline in the past only allows a few lines; far lines get a guessed tokenization (initial state)
            var t = doc.GetTokens(lines, 40000, 40000, Stopwatch.GetTimestamp() - 1, out guess);
            Assert.True(guess);
            Assert.Equal("source.mini keyword.control.mini", ScopeAt(t, 0));
            Assert.True(doc.ValidCount < 40000);
            doc.EnsureValid(lines, lines.Count - 1, 0);
            t = doc.GetTokens(lines, 40000, 40000, 0, out guess);
            Assert.False(guess);
            Assert.Equal("source.mini comment.block.mini", ScopeAt(t, 0));
        }

        // ------------------------------------------------------------------ theme mapping
        [Fact]
        public void Theme_MapsScopesToStudioThemeRoles()
        {
            var theme = StudioTheme.GetById(StudioTheme.DefaultThemeId);
            var tm = TextMateTheme.For(theme);
            var root = new TmScopeStack(null, "source.ts");
            Assert.Equal(theme.TokenComment, tm.Resolve(root.Push("comment.line.double-slash.ts")).Foreground);
            Assert.True(tm.Resolve(root.Push("comment.block.ts")).Italic);
            Assert.Equal(theme.TokenString, tm.Resolve(root.Push("string.quoted.double.ts")).Foreground);
            Assert.Equal(theme.TokenControl, tm.Resolve(root.Push("keyword.control.flow.ts")).Foreground);
            Assert.Equal(theme.TokenKeyword, tm.Resolve(root.Push("storage.type.ts")).Foreground);
            Assert.Equal(theme.TokenFunction, tm.Resolve(root.Push("meta.function-call.ts entity.name.function.ts")).Foreground);
            Assert.Equal(theme.TokenType, tm.Resolve(root.Push("entity.name.type.class.ts")).Foreground);
            Assert.Equal(theme.TokenNumber, tm.Resolve(root.Push("constant.numeric.decimal.ts")).Foreground);
            Assert.Equal(theme.FgPrimary, tm.Resolve(root.Push("variable.other.readwrite.ts")).Foreground);
            // deeper scope wins: an escape inside a string uses the escape color
            Assert.Equal(theme.TokenNumber, tm.Resolve(root.Push("string.quoted.double.ts").Push("constant.character.escape.ts")).Foreground);
        }

        [Fact]
        public void Theme_VsixTokenColorsWin_WithParentSelectorsAndFontStyle()
        {
            var theme = StudioTheme.GetById(StudioTheme.DefaultThemeId);
            var custom = new StudioTheme { Id = "vsix-test", Name = "t", BgEditor = theme.BgEditor, FgPrimary = Color.FromArgb(200, 200, 200) };
            custom.VsixTokenColors = TmJson.Parse(@"[
                { ""settings"": { ""foreground"": ""#ffffff"" } },
                { ""scope"": ""comment"", ""settings"": { ""foreground"": ""#6A9955"", ""fontStyle"": ""italic"" } },
                { ""scope"": [""keyword"", ""storage.type""], ""settings"": { ""foreground"": ""#569CD6"" } },
                { ""scope"": ""source.ts keyword.control"", ""settings"": { ""foreground"": ""#C586C0"", ""fontStyle"": """" } },
                { ""scope"": ""string"", ""settings"": { ""foreground"": ""#CE9178AA"" } } ]") as List<object>;
            var tm = TextMateTheme.For(custom);
            var ts = new TmScopeStack(null, "source.ts");
            var cs = new TmScopeStack(null, "source.cs");
            Assert.Equal(Color.FromArgb(0x6A, 0x99, 0x55), tm.Resolve(ts.Push("comment.line")).Foreground);
            Assert.True(tm.Resolve(ts.Push("comment.line")).Italic);
            Assert.Equal(Color.FromArgb(0xC5, 0x86, 0xC0), tm.Resolve(ts.Push("keyword.control.flow.ts")).Foreground);
            Assert.Equal(Color.FromArgb(0x56, 0x9C, 0xD6), tm.Resolve(cs.Push("keyword.control.cs")).Foreground);
            Assert.Equal(Color.FromArgb(0xCE, 0x91, 0x78), tm.Resolve(ts.Push("string.quoted")).Foreground);
            Assert.Equal(custom.FgPrimary, tm.Resolve(ts.Push("variable.other")).Foreground);
        }

        // ------------------------------------------------------------------ real bundled grammars
        private static List<string> Lines(string s) { return s.Replace("\r\n", "\n").Split('\n').ToList(); }

        private static string ScopeOfText(TmCompiledGrammar g, List<string> lines, string needle, int occurrence = 0)
        {
            TmStateStack st = null;
            int seen = 0;
            for (int i = 0; i < lines.Count; i++)
            {
                var r = g.TokenizeLine(lines[i], i == 0 ? null : st);
                int idx = -1, from = 0;
                while ((idx = lines[i].IndexOf(needle, from, StringComparison.Ordinal)) >= 0)
                {
                    if (seen++ == occurrence) return ScopeAt(r.Tokens, idx);
                    from = idx + 1;
                }
                st = r.EndState;
            }
            return null;
        }

        [Fact]
        public void BundledGrammars_AllLoadAndTokenizeWithoutErrors()
        {
            var reg = TextMateGrammarRegistry.Shared;
            var scopes = reg.RegisteredScopes();
            Assert.True(scopes.Count >= 18, "bundled grammars: " + scopes.Count);
            string sample = "// comment\nfunction foo(a, b) { return \"x\" + 1; }\nclass X { }\n# heading\nselect * from t where a = 'b';\n";
            TextMateDiagnostics.Clear();
            foreach (string scope in scopes)
            {
                var g = reg.GetCompiled(scope, true);
                Assert.True(g != null, scope + ": " + reg.GetLoadError(scope));
                TmStateStack st = null;
                var ls = Lines(sample);
                for (int i = 0; i < ls.Count; i++) st = g.TokenizeLine(ls[i], i == 0 ? null : st).EndState;
            }
            var errs = TextMateDiagnostics.Snapshot();
            foreach (string e in errs) output.WriteLine(e);
            output.WriteLine("untranslatable regexes hit: " + errs.Count);
            Assert.True(errs.Count <= 10, "too many untranslatable regexes: " + errs.Count);
        }

        [Fact]
        public void BundledTypeScript_TokenizesRealCode()
        {
            var g = TextMateGrammarRegistry.Shared.GetCompiled("source.ts", true);
            var src = Lines(@"import { readFile } from 'fs';
/** Doc comment */
export class Store<T> extends Base implements IStore {
    private items: Map<string, T> = new Map();
    async load(path: string): Promise<number> {
        const text = `count: ${this.items.size + 1}`;
        if (path.length > 0) { return 42; }
        return /ab+c/.test(text) ? 1 : 0;
    }
}");
            Assert.StartsWith("source.ts meta.import.ts keyword.control.import.ts", ScopeOfText(g, src, "import"));
            Assert.Contains("string.quoted.single.ts", ScopeOfText(g, src, "'fs'"));
            Assert.Contains("comment.block.documentation", ScopeOfText(g, src, "Doc comment"));
            Assert.Contains("storage.type.class.ts", ScopeOfText(g, src, "class"));
            Assert.Contains("entity.name.type.class.ts", ScopeOfText(g, src, "Store"));
            Assert.Contains("storage.modifier.ts", ScopeOfText(g, src, "private"));
            Assert.Contains("entity.name.function.ts", ScopeOfText(g, src, "load"));
            Assert.Contains("string.template.ts", ScopeOfText(g, src, "count:"));
            Assert.Contains("meta.template.expression.ts", ScopeOfText(g, src, "items", 1));
            Assert.Contains("keyword.control.conditional.ts", ScopeOfText(g, src, "if"));
            Assert.Contains("constant.numeric.decimal.ts", ScopeOfText(g, src, "42"));
            Assert.Contains("string.regexp.ts", ScopeOfText(g, src, "ab+c"));
        }

        [Fact]
        public void BundledGrammars_CSharpGoRustYamlCssMarkdownShell()
        {
            var reg = TextMateGrammarRegistry.Shared;
            var cs = reg.GetCompiled("source.cs", true);
            var csSrc = Lines("namespace A.B\n{\n    public sealed class Foo : IBar\n    {\n        // note\n        private readonly string name = @\"c:\\x\";\n        public int Count(int x) => x * 2;\n    }\n}");
            Assert.Contains("namespace.cs", ScopeOfText(cs, csSrc, "namespace"));
            Assert.Contains("entity.name.type.class.cs", ScopeOfText(cs, csSrc, "Foo"));
            Assert.Contains("comment.line.double-slash.cs", ScopeOfText(cs, csSrc, "note"));
            Assert.Contains("string.quoted.double.cs", ScopeOfText(cs, csSrc, "c:"));
            Assert.Contains("entity.name.function.cs", ScopeOfText(cs, csSrc, "Count"));

            var go = reg.GetCompiled("source.go", true);
            var goSrc = Lines("package main\n\nimport \"fmt\"\n\nfunc main() {\n\ts := `raw\nstring`\n\tfmt.Println(s, 3)\n}");
            Assert.Contains("keyword", ScopeOfText(go, goSrc, "package"));
            Assert.Contains("string.quoted.raw.go", ScopeOfText(go, goSrc, "string`"));
            Assert.Contains("entity.name.function", ScopeOfText(go, goSrc, "main", 1));

            var rs = reg.GetCompiled("source.rust", true);
            var rsSrc = Lines("fn longest<'a>(x: &'a str) -> &'a str {\n    let n: u32 = 5; // c\n    x\n}");
            Assert.Contains("keyword.other.fn.rust", ScopeOfText(rs, rsSrc, "fn"));
            Assert.Contains("entity.name.function.rust", ScopeOfText(rs, rsSrc, "longest"));
            Assert.Contains("comment.line.double-slash.rust", ScopeOfText(rs, rsSrc, "// c"));

            var yaml = reg.GetCompiled("source.yaml", true);
            var ySrc = Lines("name: build\non:\n  push:\n    branches: [main]\n# comment\nlist:\n  - 'quoted'\n  - 12");
            Assert.Contains("entity.name.tag.yaml", ScopeOfText(yaml, ySrc, "name"));
            Assert.Contains("comment.line.number-sign.yaml", ScopeOfText(yaml, ySrc, "comment"));
            Assert.Contains("string.quoted.single.yaml", ScopeOfText(yaml, ySrc, "quoted"));

            var css = reg.GetCompiled("source.css", true);
            var cSrc = Lines(".card > a:hover {\n  color: #fff;\n  margin: 10px auto; /* x */\n}");
            Assert.Contains("entity.other.attribute-name.class.css", ScopeOfText(css, cSrc, "card"));
            Assert.Contains("support.type.property-name.css", ScopeOfText(css, cSrc, "color"));
            Assert.Contains("constant.numeric", ScopeOfText(css, cSrc, "10"));
            Assert.Contains("comment.block.css", ScopeOfText(css, cSrc, " x "));

            var md = reg.GetCompiled("text.html.markdown", true);
            var mSrc = Lines("# Title\n\nSome **bold** and `code`.\n\n```ts\nlet a = 0;\nconst x = 1;\n```\n- item");
            Assert.Contains("markup.heading", ScopeOfText(md, mSrc, "Title"));
            Assert.Contains("markup.bold", ScopeOfText(md, mSrc, "bold"));
            Assert.Contains("markup.inline.raw", ScopeOfText(md, mSrc, "code"));
            // fenced TypeScript is highlighted through the embedded source.ts grammar (as in VS Code, the begin/while
            // rule of the fence consumes the first code line, so check the second one)
            Assert.Contains("storage.type.ts", ScopeOfText(md, mSrc, "const"));

            var sh = reg.GetCompiled("source.shell", true);
            var sSrc = Lines("#!/bin/bash\nfor f in *.txt; do\n  echo \"$f\" # print\ndone");
            Assert.Contains("keyword.control", ScopeOfText(sh, sSrc, "for"));
            Assert.Contains("comment.line.number-sign.shell", ScopeOfText(sh, sSrc, "print"));
        }

        [Fact]
        public void Registry_MapsFilesToGrammars_AndVsixContributionsOverride()
        {
            var reg = TextMateGrammarRegistry.Shared;
            Assert.Equal("source.ts", reg.GetScopeForFile("/x/y/app.ts"));
            Assert.Equal("source.tsx", reg.GetScopeForFile("C:\\proj\\App.tsx"));
            Assert.Equal("source.makefile", reg.GetScopeForFile("/repo/Makefile"));
            Assert.Equal("source.ruby", reg.GetScopeForFile("/repo/Gemfile"));
            Assert.Null(reg.GetScopeForFile("/repo/notes.txt"));

            string dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "codeLess_vsix_tm_" + Guid.NewGuid().ToString("N").Substring(0, 8));
            System.IO.Directory.CreateDirectory(System.IO.Path.Combine(dir, "syntaxes"));
            try
            {
                System.IO.File.WriteAllText(System.IO.Path.Combine(dir, "syntaxes", "mini.tmLanguage.json"), MiniGrammar);
                var contributes = TmJson.Parse(@"{ ""languages"": [ { ""id"": ""minilang"", ""extensions"": [ "".mini"" ], ""filenames"": [ ""Minifile"" ] } ],
                    ""grammars"": [ { ""language"": ""minilang"", ""scopeName"": ""source.mini"", ""path"": ""./syntaxes/mini.tmLanguage.json"" },
                                    { ""scopeName"": ""evil"", ""path"": ""../../outside.json"" } ] }") as Dictionary<string, object>;
                int n = reg.RegisterVsixContributions(dir, contributes);
                Assert.Equal(1, n); // the path escaping the extension folder is ignored
                Assert.Equal("source.mini", reg.GetScopeForFile("/a/b.mini"));
                Assert.Equal("source.mini", reg.GetScopeForFile("/a/Minifile"));
                var g = reg.GetCompiled("source.mini", true);
                Assert.Equal("source.mini keyword.control.mini", ScopeAt(g.TokenizeLine("if", null).Tokens, 0));
            }
            finally { try { System.IO.Directory.Delete(dir, true); } catch { } }
        }

        // ------------------------------------------------------------------ throughput
        private static List<string> MakeTypeScriptFile(int lineCount)
        {
            string[] block =
            {
                "/**",
                " * Service documentation with `code` and {@link Thing}.",
                " */",
                "export class OrderService<T extends Entity> implements IService {",
                "    private readonly cache = new Map<string, T>();",
                "    constructor(private http: HttpClient, @Inject(TOKEN) cfg: Config) {}",
                "",
                "    async fetchAll(filter?: Partial<T>, limit = 100): Promise<T[]> {",
                "        const url = `${this.base}/orders?limit=${limit}&q=${encodeURIComponent(JSON.stringify(filter))}`;",
                "        // fetch and cache",
                "        const res = await this.http.get<T[]>(url, { headers: { 'X-Id': String(Math.random()) } });",
                "        for (const item of res) { if (item.id > 0 && !this.cache.has(item.key)) this.cache.set(item.key, item); }",
                "        return res.filter(x => /^[a-z]+\\d*$/i.test(x.name)).map((x, i) => ({ ...x, index: i * 2.5e3 }));",
                "    }",
                "}",
                ""
            };
            var lines = new List<string>(lineCount);
            while (lines.Count < lineCount) lines.AddRange(block);
            return lines.Take(lineCount).ToList();
        }

        [Fact]
        public void Throughput_20kLineTypeScriptAndCSharp()
        {
            var reg = TextMateGrammarRegistry.Shared;
            var ts = reg.GetCompiled("source.ts", true);
            var lines = MakeTypeScriptFile(20000);
            var doc = new TextMateDocument(ts);
            doc.Sync(lines);
            doc.EnsureValid(lines, 200, 0); // warm-up: compile the regexes in use
            var sw = Stopwatch.StartNew();
            doc.EnsureValid(lines, lines.Count - 1, 0);
            sw.Stop();
            double tsMs = sw.Elapsed.TotalMilliseconds;
            output.WriteLine(string.Format("TypeScript 20k lines: {0:F0} ms ({1:F0} lines/s, {2} timeouts)", tsMs, 19800 / (tsMs / 1000.0), TextMateDiagnostics.TimeoutCount));

            // single-line edit cost after the full pass
            lines[10000] = lines[10000] + " // edited";
            sw.Restart();
            doc.Sync(lines);
            doc.EnsureValid(lines, lines.Count - 1, 0);
            sw.Stop();
            output.WriteLine(string.Format("TypeScript single-line edit at line 10000: {0:F2} ms", sw.Elapsed.TotalMilliseconds));
            Assert.True(sw.Elapsed.TotalMilliseconds < 200);

            var cs = reg.GetCompiled("source.cs", true);
            var csLines = new List<string>();
            var csSample = System.IO.File.Exists(SamplePath()) ? System.IO.File.ReadAllLines(SamplePath()).ToList() : MakeTypeScriptFile(400);
            while (csLines.Count < 20000) csLines.AddRange(csSample);
            csLines = csLines.Take(20000).ToList();
            var csDoc = new TextMateDocument(cs);
            csDoc.Sync(csLines);
            csDoc.EnsureValid(csLines, 200, 0);
            sw.Restart();
            csDoc.EnsureValid(csLines, csLines.Count - 1, 0);
            sw.Stop();
            output.WriteLine(string.Format("C# 20k lines: {0:F0} ms ({1:F0} lines/s)", sw.Elapsed.TotalMilliseconds, 19800 / (sw.Elapsed.TotalMilliseconds / 1000.0)));
            Assert.True(tsMs < 30000, "TypeScript tokenization far too slow: " + tsMs);
        }

        private static string SamplePath()
        {
            string dir = AppContext.BaseDirectory;
            for (int i = 0; i < 8 && dir != null; i++)
            {
                string p = System.IO.Path.Combine(dir, "src", "codeLess.Core", "LineStateCache.cs");
                if (System.IO.File.Exists(p)) return System.IO.Path.Combine(dir, "src", "codeLess.Core", "TextMateTokenizer.cs");
                dir = System.IO.Path.GetDirectoryName(dir);
            }
            return "";
        }
    }
}
