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

namespace codeLess.Avalonia.Tests
{
    public class UpdateCheckerTests
    {
        [Fact]
        public void VersionComparison_HandlesTagsAndFourPartVersions()
        {
            Assert.True(codeLess.Studio.UpdateChecker.IsNewerVersion("v3.2.3", "3.2.2.0"));
            Assert.True(codeLess.Studio.UpdateChecker.IsNewerVersion("3.10.0", "3.9.9"));
            Assert.False(codeLess.Studio.UpdateChecker.IsNewerVersion("v3.2.2", "3.2.2"));
            Assert.False(codeLess.Studio.UpdateChecker.IsNewerVersion("3.2.1", "3.2.2"));
            Assert.False(codeLess.Studio.UpdateChecker.IsNewerVersion("v3.3.0-beta", "garbage"));
            Assert.True(codeLess.Studio.UpdateChecker.IsNewerVersion("v4.0.0-rc1", "3.2.2"));
        }

        [Fact]
        public void PlatformAsset_MatchesReleaseArtifactNames()
        {
            string a = codeLess.Studio.UpdateChecker.GetPlatformAssetName();
            Assert.Contains(a, new[] { "codeLess-Setup.exe", "codeLess-mac-arm64.dmg", "codeLess-mac-x64.dmg", "codeLess-linux-x64.deb", "codeLess-linux-arm64.deb" });
        }
    }
}
