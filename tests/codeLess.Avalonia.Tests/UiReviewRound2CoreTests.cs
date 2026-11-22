using System.Collections.Generic;
using codeLess.Studio;
using Xunit;

namespace codeLess.Avalonia.Tests
{
    /// <summary>UI review round 2, shared (both UIs) logic: word-highlight colors, keyword detection, git graph split.</summary>
    public class UiReviewRound2CoreTests
    {
        [Fact]
        public void WordHighlight_IsVsCodesNeutralGrey_OnEveryTheme()
        {
            foreach (var t in StudioTheme.GetAllBuiltInThemes())
            {
                var bg = UiTokens.WordHighlightBackground(t);
                Assert.True(bg.R == bg.G && bg.G == bg.B, t.Id + ": word highlight must be neutral, was " + bg);
                Assert.Equal(t.IsLight ? 0x40 : 0xB8, (int)bg.A);
                Assert.Equal(0, (int)UiTokens.WordHighlightBorder(t).A);
                var sel = UiTokens.SelectionHighlightBackground(t);
                Assert.Equal(t.IsLight ? 0x80 : 0x26, (int)sel.A);
            }
        }

        private static EditorTabBuffer Buf(string path, string language, params string[] lines)
        {
            var b = new EditorTabBuffer { FilePath = path, FileName = System.IO.Path.GetFileName(path), Language = language };
            b.Lines = new List<string>(lines);
            return b;
        }

        [Fact]
        public void HighlightTarget_SkipsKeywords_WithTheFallbackTokenizer()
        {
            var b = Buf("/tmp/none/a.cs", "csharp", "using System;", "if (count > 0) return count;");
            bool whole;
            b.CursorLine = 0; b.CursorCol = 2;
            Assert.Null(EditorOccurrences.GetHighlightTarget(b, out whole));
            b.CursorCol = 8;
            Assert.Equal("System", EditorOccurrences.GetHighlightTarget(b, out whole));
            b.CursorLine = 1; b.CursorCol = 1;
            Assert.Null(EditorOccurrences.GetHighlightTarget(b, out whole));
            b.CursorCol = 6;
            Assert.Equal("count", EditorOccurrences.GetHighlightTarget(b, out whole));
            Assert.True(whole);
            b.CursorCol = 3; // '(' -> punctuation
            Assert.Null(EditorOccurrences.GetHighlightTarget(b, out whole));
        }

        [Fact]
        public void KeywordScopes()
        {
            Assert.True(EditorOccurrences.IsKeywordScope("keyword.control.if.cs"));
            Assert.True(EditorOccurrences.IsKeywordScope("keyword.other.using.cs"));
            Assert.True(EditorOccurrences.IsKeywordScope("storage.modifier.public.cs"));
            Assert.True(EditorOccurrences.IsKeywordScope("storage.type.function.ts"));
            Assert.True(EditorOccurrences.IsKeywordScope("constant.language.boolean.true.cs"));
            Assert.False(EditorOccurrences.IsKeywordScope("variable.other.readwrite.cs"));
            Assert.False(EditorOccurrences.IsKeywordScope("entity.name.type.class.cs"));
            Assert.False(EditorOccurrences.IsKeywordScope(null));
        }

        [Fact]
        public void GitGraphSplit_Thresholds()
        {
            Assert.True(GitGraphSplitLayout.ShouldStack(420));
            Assert.False(GitGraphSplitLayout.ShouldStack(600));
            Assert.Equal(GitGraphSplitLayout.DetailsMinWidth, GitGraphSplitLayout.InitialDetailsWidth(500));
            Assert.Equal(GitGraphSplitLayout.DetailsMaxInitialWidth, GitGraphSplitLayout.InitialDetailsWidth(2000));
            Assert.True(600 - GitGraphSplitLayout.InitialDetailsWidth(600) - 4 >= GitGraphSplitLayout.ListMinWidth);
            Assert.Equal(GitGraphSplitLayout.DetailsMinHeight, GitGraphSplitLayout.InitialDetailsHeight(100));
        }
    }
}
