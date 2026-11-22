using System;
using System.Drawing;

namespace codeLess.Studio
{
    /// <summary>
    /// Input option toggles (find widget / Search view: Match Case, Match Whole Word, Use Regular Expression, Preserve
    /// Case, Find in Selection). VS Code inputOption.*: the glyph keeps the input foreground; ON = accent-tinted fill
    /// (focusBorder at ~40 %) plus a 1 px accent border; hover = a neutral fill. See docs/UI_GUIDELINES.md "Find widget".
    /// STRICT C# 5.
    /// </summary>
    public static partial class UiTokens
    {
        /// <summary>Toggle button size inside inputs (px) and its glyph size.</summary>
        public const int InputOptionSize = 20;
        public const int InputOptionIconSize = 16;

        public static Color InputOptionActiveBackground(StudioTheme t)
        {
            return Blend(InputBackground(t), t.AccentPrimary, IsLight(t) ? 0.22 : 0.40);
        }

        public static Color InputOptionActiveBorder(StudioTheme t)
        {
            return t.AccentPrimary;
        }

        public static Color InputOptionHoverBackground(StudioTheme t)
        {
            return Blend(InputBackground(t), t.FgPrimary, IsLight(t) ? 0.10 : 0.14);
        }

        /// <summary>Glyph color: the input foreground (ON keeps it too; VS Code inputOption.activeForeground).</summary>
        public static Color InputOptionForeground(StudioTheme t, bool on)
        {
            return on ? t.FgPrimary : Blend(InputBackground(t), t.FgPrimary, 0.88);
        }

        /// <summary>Find widget count label when the pattern is invalid (VS Code errorForeground).</summary>
        public static Color FindErrorForeground(StudioTheme t)
        {
            return IsLight(t) ? Color.FromArgb(0xA1, 0x26, 0x0D) : Color.FromArgb(0xF4, 0x87, 0x71);
        }
    }
}
