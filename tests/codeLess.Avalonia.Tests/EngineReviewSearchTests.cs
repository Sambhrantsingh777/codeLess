using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using codeLess.Studio;
using Xunit;

namespace codeLess.Avalonia.Tests
{
    /// <summary>ENGINE_REVIEW WS-1: Find-in-Files Replace All must replace exactly what the (line based) search previewed.</summary>
    public class EngineReviewSearchTests : IDisposable
    {
        private readonly string root;

        public EngineReviewSearchTests()
        {
            root = Path.Combine(Path.GetTempPath(), "cl-ws1-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(root);
        }

        public void Dispose()
        {
            try { Directory.Delete(root, true); } catch { }
        }

        private string WriteBytes(string name, byte[] bytes)
        {
            string p = Path.Combine(root, name);
            File.WriteAllBytes(p, bytes);
            return p;
        }

        private int SearchCount(string file, string query, WorkspaceSearchOptions o)
        {
            var r = WorkspaceSearchEngine.Search(root, new[] { file }, query, o, CancellationToken.None, null);
            return r.Matches.Count;
        }

        private static WorkspaceSearchOptions Rx(bool regex = true, bool caseSensitive = false, bool wholeWord = false)
        {
            return new WorkspaceSearchOptions { UseRegex = regex, CaseSensitive = caseSensitive, WholeWord = wholeWord };
        }

        [Fact]
        public void WS1_ReplaceAll_WithWhitespaceClass_DoesNotJoinLines()
        {
            string f = WriteBytes("a.txt", Encoding.UTF8.GetBytes("foo   \nbar\n"));
            var o = Rx();
            Assert.Equal(1, SearchCount(f, @"foo\s*", o));
            var updated = WorkspaceSearchEngine.ReplaceInFiles(new[] { f }, @"foo\s*", "X", o);
            Assert.Single(updated);
            Assert.Equal("X\nbar\n", File.ReadAllText(f));
        }

        [Theory]
        [InlineData("a b\nc d\n", @"\s", "_", "a_b\nc_d\n")]
        [InlineData("a;\nb;\n", @"[^;]*;", "Z", "Z\nZ\n")]
        [InlineData("ab\ncd\n", @"\W*b", "!", "a!\ncd\n")]
        [InlineData("x end\ny\n", @"end$", "E", "x E\ny\n")]
        [InlineData("x end\ny\n", @"^y", "Y", "x end\nY\n")]
        [InlineData("foo\nbar\n", @"o$\n?b", "-", "foo\nbar\n")]
        [InlineData("ab\nab\n", @"(?<=a)b", "B", "aB\naB\n")]
        [InlineData("ab\nab\n", @"a(?=b)", "A", "Ab\nAb\n")]
        [InlineData("a\nb\n", @"(?<=a\n)b", "B", "a\nb\n")]
        [InlineData("key = 1\nkey2 = 2\n", @"(\w+) = (\d)", "$2=$1", "1=key\n2=key2\n")]
        public void WS1_ReplaceCount_EqualsSearchCount_AndOnlyChangesPreviewedText(string content, string pattern, string rep, string expected)
        {
            string f = WriteBytes("t.txt", Encoding.UTF8.GetBytes(content));
            var o = Rx();
            int searched = SearchCount(f, pattern, o);
            string err;
            Regex rx = WorkspaceSearchEngine.BuildReplaceRegex(pattern, o, out err);
            Assert.NotNull(rx);
            int n;
            string next = WorkspaceSearchEngine.ReplaceInText(content, rx, WorkspaceSearchEngine.PrepareReplacementText(rep, true), out n);
            Assert.Equal(searched, n);
            Assert.Equal(expected, next);
            Assert.Equal(content.Count(c => c == '\n'), next.Count(c => c == '\n'));
        }

        [Fact]
        public void WS1_Crlf_File_KeepsCrlf_AndDollarDoesNotEatCarriageReturn()
        {
            string f = WriteBytes("crlf.txt", Encoding.UTF8.GetBytes("one  \r\ntwo  \r\n"));
            var o = Rx();
            Assert.Equal(2, SearchCount(f, @"\s+$", o));
            WorkspaceSearchEngine.ReplaceInFiles(new[] { f }, @"\s+$", "", o);
            Assert.Equal("one\r\ntwo\r\n", Encoding.UTF8.GetString(File.ReadAllBytes(f)));
        }

        [Fact]
        public void WS1_ZeroLengthMatches_AreNotReplaced_LikeSearch()
        {
            var o = Rx();
            string err;
            Regex rx = WorkspaceSearchEngine.BuildReplaceRegex("x*", o, out err);
            int n;
            string next = WorkspaceSearchEngine.ReplaceInText("axb\nc\n", rx, "Y", out n);
            Assert.Equal(1, n);
            Assert.Equal("aYb\nc\n", next);
        }

        [Fact]
        public void WS1_PlainText_DollarStaysLiteral()
        {
            var o = Rx(regex: false);
            string err;
            Regex rx = WorkspaceSearchEngine.BuildReplaceRegex("a.b", o, out err);
            int n;
            string next = WorkspaceSearchEngine.ReplaceInText("a.b axb\n", rx, WorkspaceSearchEngine.PrepareReplacementText("$1$&", false), out n);
            Assert.Equal(1, n);
            Assert.Equal("$1$& axb\n", next);
        }

        [Fact]
        public void WS1_ExpandReplacementAt_UsesLineContextForLookbehind()
        {
            var rx = new Regex(@"(?<=a)(b)");
            Assert.Equal("[b]", WorkspaceSearchEngine.ExpandReplacementAt("abab", rx, 3, 1, "[$1]", true));
            Assert.Equal("[$1]", WorkspaceSearchEngine.ExpandReplacementAt("abab", rx, 3, 1, "[$1]", false));
        }
    }
}
