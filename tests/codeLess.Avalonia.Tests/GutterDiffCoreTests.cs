using System.Collections.Generic;
using System.Linq;
using System.Text;
using codeLess.Studio;
using Xunit;

namespace codeLess.Avalonia.Tests
{
    /// <summary>
    /// v3.3.3 report: "git change colour sometimes marks every line as changed even if only 1 line is changed".
    /// Edits far apart made the span between them exceed the O(n*m) table limit, and the fallback painted every line
    /// in between as modified.
    /// </summary>
    public class GutterDiffCoreTests
    {
        private static EditorTabBuffer Buffer(string head, string current)
        {
            var b = new EditorTabBuffer { FilePath = "/tmp/x.cs", FileName = "x.cs", GitHeadContent = head, SavedContent = head, Content = current };
            b.SyncLinesFromContent();
            b.RefreshGutterDiff();
            return b;
        }

        private static string Lines(int n, System.Func<int, string> f)
        {
            var sb = new StringBuilder();
            for (int i = 0; i < n; i++) sb.Append(f(i)).Append('\n');
            return sb.ToString();
        }

        [Fact]
        public void Two_Edits_Far_Apart_Mark_Only_Those_Lines()
        {
            string head = Lines(3000, i => "line " + i);
            string cur = Lines(3000, i => i == 10 ? "line 10 edited" : i == 2900 ? "line 2900 edited" : "line " + i);
            var b = Buffer(head, cur);
            Assert.Equal(new[] { 10, 2900 }, b.GutterModifiedLines.OrderBy(x => x).ToArray());
            Assert.Empty(b.GutterAddedLines);
            Assert.Equal(2, b.ChangedLineCount);
        }

        [Fact]
        public void Far_Apart_Insert_And_Delete_Are_Reported_As_Such()
        {
            var headLines = Enumerable.Range(0, 2000).Select(i => "row " + i).ToList();
            var curLines = new List<string>(headLines);
            curLines.Insert(5, "inserted");      // added at 5
            curLines.RemoveAt(1801);              // "row 1800" deleted (shifted by the insert)
            var b = Buffer(string.Join("\n", headLines) + "\n", string.Join("\n", curLines) + "\n");
            Assert.Equal(new[] { 5 }, b.GutterAddedLines.ToArray());
            Assert.Empty(b.GutterModifiedLines);
            Assert.Single(b.GutterDeletedAfterLine);
            Assert.Equal(1800, b.GutterDeletedAfterLine.First()); // after "row 1799" (index 1800 with the insert)
        }

        [Fact]
        public void Crlf_Head_Against_Lf_Buffer_Is_Not_A_Change()
        {
            string head = Lines(1200, i => "x" + i).Replace("\n", "\r\n");
            string cur = Lines(1200, i => i == 600 ? "changed" : "x" + i);
            var b = Buffer(head, cur);
            Assert.Equal(new[] { 600 }, b.GutterModifiedLines.ToArray());
        }
    }
}
