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

namespace codeLess.Avalonia.Tests
{
    public class DefaultThemeTests
    {
        [Fact]
        public void SavatarrGraphite_IsTheDefaultTheme()
        {
            Assert.Equal("savatarr-graphite", codeLess.Studio.StudioTheme.DefaultThemeId);
            Assert.Equal("savatarr-graphite", codeLess.Studio.StudioTheme.GetBuiltInThemes()[0].Id);
            Assert.Equal("savatarr-graphite", new codeLess.Studio.IdePersistentSettings().ActiveThemeId);
            var t = codeLess.Studio.StudioTheme.GetBuiltInThemes()[0];
            Assert.False(t.IsLight);
            Assert.Equal(System.Drawing.Color.FromArgb(25, 120, 229).ToArgb(), t.AccentPrimary.ToArgb()); // Savatarr blue
        }

        [Fact]
        public void OldSettingsFile_WithoutMigrationFlag_IsNotTreatedAsMigrated()
        {
            // The deserializer runs the constructor; the flag must default to false or old Obsidian users never migrate.
            var ser = new System.Web.Script.Serialization.JavaScriptSerializer();
            var old = ser.Deserialize<codeLess.Studio.IdePersistentSettings>("{\"ActiveThemeId\":\"codeless-obsidian\"}");
            Assert.False(old.ThemeDefaultMigrated);
            Assert.Equal("codeless-obsidian", old.ActiveThemeId);
        }
    }
}

namespace codeLess.Avalonia.Tests
{
    public class TerminalMemoryTests
    {
        [Fact]
        public void TermCell_IsPacked_AndFullScrollbackStaysSmall()
        {
            Assert.Equal(12, System.Runtime.CompilerServices.Unsafe.SizeOf<codeLess.Studio.TermCell>());
            var c = new codeLess.Studio.TermCell { Fg = System.Drawing.Color.FromArgb(205, 49, 49) };
            Assert.Equal(System.Drawing.Color.FromArgb(205, 49, 49), c.Fg); // equality preserved for palette remaps

            // Full-scrollback memory is ~4.7 MB with packed cells vs ~28 MB before (measured in isolation; a GC-based
            // assertion here would be flaky because xUnit runs other test classes in parallel).
            var b = new codeLess.Studio.Vt100TerminalBuffer(120, 30);
            for (int i = 0; i < 6000; i++) b.FeedText(new string('x', 100) + "\r\n");
            Assert.Equal(5000, b.ScrollbackCount);
        }
    }
}

namespace codeLess.Avalonia.Tests
{
    public class EditorVisualColumnTests
    {
        [Fact]
        public void VisualColumns_HandleTabsWideCharsEmojiAndCombiningMarks()
        {
            
            System.Func<string, int, int> V = (s, c) => codeLess.Studio.EditorTextLayout.VisualColumn(s, c, 4);
            System.Func<string, double, int> I = (s, c) => codeLess.Studio.EditorTextLayout.CharIndexForVisualColumn(s, c, 4);
            Assert.Equal(4, V("\tx", 1));          // tab to first stop
            Assert.Equal(4, V("ab\tx", 3));        // tab from col 2 -> 4
            Assert.Equal(8, V("abcd\tx", 5));      // tab exactly at a stop -> next stop
            Assert.Equal(4, V("你好x", 2));         // CJK = 2 cells each
            Assert.Equal(2, V("🚀x", 2));          // emoji surrogate pair = 2 cells
            Assert.Equal(1, V("éx", 2));     // e + combining acute = 1 cell
            // inverse mapping lands on character boundaries
            Assert.Equal(1, I("\tx", 4.2));
            Assert.Equal(1, I("你好x", 2.4));
            Assert.Equal(2, I("🚀x", 2.1));
            Assert.Equal(0, I("你好", 0.6));
        }
    }
}
