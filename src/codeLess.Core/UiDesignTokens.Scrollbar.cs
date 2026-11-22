using System.Drawing;

namespace codeLess.Studio
{
    /// <summary>
    /// Editor scrollbar, overview ruler and inline-diff colors (QA #28 / #30), both editors. Values are VS Code's
    /// defaults (scrollbarSlider.*, editorOverviewRuler.*, diffEditor.removed*), so the ruler reads the same.
    /// STRICT C# 5.
    /// </summary>
    public static partial class UiTokens
    {
        /// <summary>scrollbarSlider.background: dark #79797966, light #64646466.</summary>
        public static Color ScrollbarSlider(StudioTheme t)
        {
            return IsLight(t) ? Color.FromArgb(0x66, 0x64, 0x64, 0x64) : Color.FromArgb(0x66, 0x79, 0x79, 0x79);
        }

        /// <summary>scrollbarSlider.hoverBackground: #646464B3.</summary>
        public static Color ScrollbarSliderHover(StudioTheme t)
        {
            return Color.FromArgb(0xB3, 0x64, 0x64, 0x64);
        }

        /// <summary>scrollbarSlider.activeBackground (dragging): dark #BFBFBF66, light #00000099.</summary>
        public static Color ScrollbarSliderActive(StudioTheme t)
        {
            return IsLight(t) ? Color.FromArgb(0x99, 0, 0, 0) : Color.FromArgb(0x66, 0xBF, 0xBF, 0xBF);
        }

        /// <summary>editorOverviewRuler.border: #7F7F7F4D (1 px on the ruler's left edge).</summary>
        public static Color OverviewRulerBorder(StudioTheme t)
        {
            return Color.FromArgb(0x4D, 0x7F, 0x7F, 0x7F);
        }

        /// <summary>Color of an overview-ruler mark kind (see <see cref="EditorOverviewRuler"/>).</summary>
        public static Color OverviewRulerMark(StudioTheme t, int kind)
        {
            bool light = IsLight(t);
            switch (kind)
            {
                case EditorOverviewRuler.KindError: return Color.FromArgb(0xB3, 0xFF, 0x12, 0x12);          // rgba(255,18,18,0.7)
                case EditorOverviewRuler.KindWarning: return light ? Color.FromArgb(0xBF, 0x88, 0x03) : Color.FromArgb(0xCC, 0xA7, 0x00);
                case EditorOverviewRuler.KindInfo: return light ? Color.FromArgb(0x1A, 0x85, 0xFF) : Color.FromArgb(0x37, 0x94, 0xFF);
                case EditorOverviewRuler.KindFindMatch: return Color.FromArgb(0x7E, 0xD1, 0x86, 0x16);      // #d186167e
                case EditorOverviewRuler.KindAdded: return light ? Color.FromArgb(0x99, 0x48, 0x98, 0x5D) : Color.FromArgb(0x99, 0x48, 0x7E, 0x02);
                case EditorOverviewRuler.KindModified: return light ? Color.FromArgb(0x99, 0x20, 0x90, 0xD3) : Color.FromArgb(0x99, 0x1B, 0x81, 0xA8);
                case EditorOverviewRuler.KindDeleted: return Color.FromArgb(0x99, 0xF1, 0x4C, 0x4C);
                case EditorOverviewRuler.KindCursor: return WithAlpha(t.AccentPrimary, 0xCC);              // our caret color
                case EditorOverviewRuler.KindConflictCurrent: return Color.FromArgb(0x80, 0x40, 0xC8, 0xAE);
                case EditorOverviewRuler.KindConflictIncoming: return Color.FromArgb(0x80, 0x40, 0xA6, 0xFF);
                case EditorOverviewRuler.KindProposed: return WithAlpha(t.AccentPrimary, 0x99);
            }
            return WithAlpha(t.FgSecondary, 0x80);
        }

        /// <summary>Minimum height (px) of a ruler mark so single lines stay visible in long files.</summary>
        public const int OverviewRulerMinMark = 3;
        public const int OverviewRulerCursorHeight = 2;

        /// <summary>Background of the read-only "deleted lines" zone of inline diff review (VS Code diffEditor.removedLineBackground).</summary>
        public static Color DiffRemovedLineBackground(StudioTheme t)
        {
            return IsLight(t) ? Color.FromArgb(0x33, 0xFF, 0x00, 0x00) : Color.FromArgb(0x33, 0xFF, 0x00, 0x00);
        }

        /// <summary>Gutter of the deleted zone (VS Code diffEditorGutter.removedLineBackground, a little stronger).</summary>
        public static Color DiffRemovedGutterBackground(StudioTheme t)
        {
            return IsLight(t) ? Color.FromArgb(0x4D, 0xFF, 0x00, 0x00) : Color.FromArgb(0x4D, 0xFF, 0x00, 0x00);
        }

        /// <summary>Text of deleted lines: the normal foreground, dimmed slightly (they are not part of the document).</summary>
        public static Color DiffRemovedText(StudioTheme t)
        {
            return Blend(t.BgEditor, t.FgPrimary, IsLight(t) ? 0.78 : 0.72);
        }
    }
}
