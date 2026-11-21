using System.Linq;
using codeLess.Studio;
using Xunit;

namespace codeLess.Avalonia.Tests
{
    public class TerminalBufferRegressionTests
    {
        [Fact]
        public void ShrinkingWidth_RewrapsInsteadOfTruncating_AndWideningKeepsHistory()
        {
            var b = new Vt100TerminalBuffer(80, 10);
            string longLine = "BEGIN-" + new string('x', 50) + "-END";
            b.FeedText("$ echo hi\r\n" + longLine + "\r\n$ ");
            b.Resize(30, 10);
            string text = b.GetScrollbackAndScreenText(500).Replace("\r", "").Replace("\n", "");
            Assert.Contains("BEGIN-", text);
            Assert.Contains("-END", text);
            Assert.Contains(longLine, text);       // all characters survive, now split across rows
            Assert.Equal(2, b.CursorCol);         // cursor stays after "$ "

            b.Resize(100, 10);
            Assert.Contains("-END", b.GetScrollbackAndScreenText(500));
        }

        [Fact]
        public void ShrinkingWidth_WithManyRows_PushesOverflowToScrollbackAndKeepsCursorVisible()
        {
            var b = new Vt100TerminalBuffer(60, 6);
            for (int i = 0; i < 5; i++) b.FeedText("line" + i + new string('.', 40) + "\r\n");
            b.FeedText("$ ");
            b.Resize(20, 6);
            Assert.InRange(b.CursorRow, 0, 5);
            string all = b.GetScrollbackAndScreenText(500).Replace("\r", "").Replace("\n", "");
            for (int i = 0; i < 5; i++) Assert.Contains("line" + i, all);
        }
    }
}
