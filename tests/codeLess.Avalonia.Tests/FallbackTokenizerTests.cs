using System.Linq;
using codeLess.Studio;
using Xunit;

namespace codeLess.Avalonia.Tests
{
    /// <summary>QA #19: the fallback tokenizer (no TextMate grammar / large files) knows each language's comment syntax.</summary>
    public class FallbackTokenizerTests
    {
        private static string Kind(string line, string lang, string text)
        {
            int at = line.IndexOf(text, System.StringComparison.Ordinal);
            var t = FallbackTokenizer.Tokenize(line, lang).FirstOrDefault(x => at >= x.Start && at < x.Start + x.Length);
            return t == null ? "none" : t.Kind.ToString();
        }

        [Fact]
        public void CSharp_PreprocessorDirectivesAreKeywords_NotComments()
        {
            Assert.Equal("Keyword", Kind("    #region Helpers", "cs", "#region"));
            Assert.NotEqual("Comment", Kind("    #region Helpers", "cs", "Helpers"));
            Assert.Equal("Keyword", Kind("#if DEBUG", "csharp", "#if"));
            Assert.NotEqual("Comment", Kind("#if DEBUG", "csharp", "DEBUG"));
            Assert.Equal("Comment", Kind("int x = 1; // note", "cs", "// note"));
            Assert.Equal("Comment", Kind("int /* inline */ x = 1;", "cs", "inline"));
            Assert.NotEqual("Comment", Kind("int /* inline */ x = 1;", "cs", "x = 1"));
            Assert.Equal("String", Kind("var s = \"// not a comment\";", "cs", "not a comment"));
            Assert.Equal("Keyword", Kind("#include <stdio.h>", "c", "#include"));
        }

        [Fact]
        public void Css_HashIsAnIdOrAColor_AndOnlyBlockCommentsExist()
        {
            const string line = "#main .x { color: #1e1e1e; background: url(//cdn/x.png); } /* done */";
            Assert.Equal("Type", Kind(line, "css", "#main"));
            Assert.Equal("Number", Kind(line, "css", "#1e1e1e"));
            Assert.NotEqual("Comment", Kind(line, "css", "//cdn"));
            Assert.Equal("Comment", Kind(line, "css", "done"));
            Assert.Equal("Comment", Kind("$x: 1; // scss comment", "scss", "scss comment"));
        }

        [Fact]
        public void Python_Shell_Yaml_UseHash_AndPythonFloorDivisionIsCode()
        {
            Assert.NotEqual("Comment", Kind("half = total // 2", "py", "// 2"));
            Assert.Equal("Comment", Kind("x = 1  # note", "py", "# note"));
            Assert.Equal("String", Kind("s = \"#not\"  # real", "py", "#not"));
            Assert.Equal("Comment", Kind("s = \"#not\"  # real", "py", "# real"));
            Assert.Equal("Comment", Kind("echo hi # c", "sh", "# c"));
            Assert.Equal("Comment", Kind("key: v # c", "yaml", "# c"));
        }

        [Fact]
        public void OtherFamilies_AndPlainText()
        {
            Assert.Equal("Comment", Kind("SELECT 1 -- why", "sql", "-- why"));
            Assert.Equal("Comment", Kind("local x = 1 -- lua", "lua", "-- lua"));
            Assert.Equal("Comment", Kind("<p>a</p><!-- note -->", "html", "note"));
            Assert.NotEqual("Comment", Kind("<p>don't</p>", "html", "p>"));
            Assert.Equal("none", Kind("#hashtag and // slashes", "txt", "#hashtag") == "Comment" ? "Comment" : "none");
            Assert.DoesNotContain(FallbackTokenizer.Tokenize("#hashtag and // slashes", "txt"), t => t.Kind == CodeTokenKind.Comment);
        }

        [Fact]
        public void MinimapCommentDetection_FollowsTheLanguage()
        {
            Assert.False(FallbackTokenizer.StartsWithLineComment("#region X", "cs"));
            Assert.True(FallbackTokenizer.StartsWithLineComment("// x", "cs"));
            Assert.True(FallbackTokenizer.StartsWithLineComment("# x", "py"));
            Assert.False(FallbackTokenizer.StartsWithLineComment("#main { }", "css"));
            Assert.True(FallbackTokenizer.StartsWithLineComment("    -- x", 4, "sql"));
        }

        [Fact]
        public void AvaloniaFallbackSpans_CoverTheLine_AndUseTheLanguage()
        {
            var theme = StudioTheme.GetById(StudioTheme.DefaultThemeId);
            const string line = "#region Helpers // tail";
            var spans = codeLess.AvaloniaApp.AvaloniaVirtualEditorCanvas.TokenizeLineForTheme(line, theme, "cs");
            Assert.Equal(line, string.Concat(spans.Select(s => s.Text)));
            var region = spans.First(s => s.Text == "#region");
            Assert.False(region.IsItalic);
            Assert.True(spans.Last().IsItalic); // the // comment
        }
    }
}
