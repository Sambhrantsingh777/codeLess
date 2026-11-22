using System;
using System.Drawing;

namespace codeLess.Studio
{
    /// <summary>
    /// Editor decoration tokens (bracket pair colors, guides, word highlights, sticky scroll). See docs/UI_GUIDELINES.md
    /// "Editor decorations". Values follow VS Code's defaults, derived from the theme roles where VS Code does too.
    /// STRICT C# 5.
    /// </summary>
    public static partial class UiTokens
    {
        /// <summary>Most sticky-scroll header lines shown at once (VS Code editor.stickyScroll.maxLineCount).</summary>
        public const int StickyScrollMaxLines = 5;
        /// <summary>Delay before the word under the caret is highlighted (VS Code: 250 ms).</summary>
        public const int OccurrenceHighlightDelayMs = 250;

        // VS Code editorBracketHighlight.foreground1..3 (dark: Gold, Orchid, LightSkyBlue-ish; light: blue, green, brown).
        private static readonly Color[] BracketDark = { Color.FromArgb(0xFF, 0xD7, 0x00), Color.FromArgb(0xDA, 0x70, 0xD6), Color.FromArgb(0x17, 0x9F, 0xFF) };
        private static readonly Color[] BracketLight = { Color.FromArgb(0x04, 0x31, 0xFA), Color.FromArgb(0x31, 0x93, 0x31), Color.FromArgb(0x7B, 0x38, 0x14) };

        /// <summary>Rotating bracket pair color for nesting level <paramref name="level"/> (0-based).</summary>
        public static Color BracketPairColor(StudioTheme t, int level)
        {
            Color[] set = IsLight(t) ? BracketLight : BracketDark;
            int i = level < 0 ? 0 : level % set.Length;
            return set[i];
        }

        /// <summary>Unmatched closing bracket (VS Code editorBracketHighlight.unexpectedBracket.foreground).</summary>
        public static Color BracketUnexpected(StudioTheme t)
        {
            return IsLight(t) ? Color.FromArgb(0xE5, 0x14, 0x00) : Color.FromArgb(0xFF, 0x12, 0x12);
        }

        /// <summary>Active bracket pair guide: the pair's color, softened toward the editor background.</summary>
        public static Color BracketPairGuide(StudioTheme t, int level)
        {
            return Blend(t.BgEditor, BracketPairColor(t, level), IsLight(t) ? 0.55 : 0.60);
        }

        /// <summary>Background behind other occurrences of the word under the caret (neutral, like VS Code).</summary>
        public static Color WordHighlightBackground(StudioTheme t)
        {
            return Blend(t.BgEditor, t.FgPrimary, IsLight(t) ? 0.10 : 0.16);
        }

        /// <summary>Thin outline of a word highlight (keeps it visible on the current-line highlight).</summary>
        public static Color WordHighlightBorder(StudioTheme t)
        {
            return Blend(t.BgEditor, t.FgPrimary, IsLight(t) ? 0.22 : 0.28);
        }

        /// <summary>Sticky scroll widget background (the editor background, as in VS Code).</summary>
        public static Color StickyScrollBackground(StudioTheme t)
        {
            return t.BgEditor;
        }

        public static Color StickyScrollHover(StudioTheme t)
        {
            return Hover(t, t.BgEditor);
        }

        /// <summary>Drop shadow under the sticky lines (VS Code scrollbar.shadow).</summary>
        public static Color StickyScrollShadow(StudioTheme t)
        {
            return Color.FromArgb(IsLight(t) ? 40 : 110, 0, 0, 0);
        }

        public const int StickyScrollShadowHeight = 4;

        /// <summary>Matching bracket box (both brackets of the pair at the caret).</summary>
        public static Color BracketMatchBorder(StudioTheme t)
        {
            return Blend(t.BgEditor, t.FgPrimary, IsLight(t) ? 0.45 : 0.50);
        }

        public static Color BracketMatchBackground(StudioTheme t)
        {
            return Blend(t.BgEditor, t.FgPrimary, IsLight(t) ? 0.08 : 0.12);
        }
    }
}
