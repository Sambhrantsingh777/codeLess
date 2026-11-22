using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using codeLess.Studio;

namespace codeLess.AvaloniaApp
{
    /// <summary>
    /// "Terminal" settings (VS Code terminal.integrated.cursorStyle, cursorStyleInactive, cursorBlinking, smoothScrolling,
    /// fontSize, lineHeight, fontFamily, minimumContrastRatio).
    /// </summary>
    public static class TerminalFeelSettingsPanel
    {
        public sealed class Result
        {
            public CheckBox BlinkBox, SmoothBox;
            public ComboBox StyleCombo, InactiveCombo;
            public TextBox FontSizeBox, LineHeightBox, FontFamilyBox, ContrastBox;
            public Action<IdePersistentSettings> Apply;
        }

        public static readonly string[] StyleNames = { "Block", "Line", "Underline" };
        public static readonly string[] InactiveNames = { "Outline", "Block", "Line", "Underline", "None" };

        public static Result Build(IdePersistentSettings s, Color textPrimary)
        {
            var fg = new SolidColorBrush(textPrimary);
            CheckBox Chk(string text, bool value, string tip)
            {
                var c = new CheckBox { Content = text, IsChecked = value, Foreground = fg, FontSize = UiTokens.FontUi };
                ToolTip.SetTip(c, tip);
                return c;
            }
            TextBox Box(string text, double width, string watermark = null) =>
                new TextBox { Text = text, Width = width, HorizontalAlignment = HorizontalAlignment.Left, Watermark = watermark };
            var blink = Chk("Terminal cursor blinking", s.TerminalCursorBlinking, "terminal.integrated.cursorBlinking");
            var smooth = Chk("Terminal smooth scrolling", s.TerminalSmoothScrolling, "terminal.integrated.smoothScrolling");
            var style = new ComboBox
            {
                ItemsSource = StyleNames.ToList(),
                SelectedIndex = Math.Max(0, Array.IndexOf(TerminalCursorStyle.ActiveStyles, TerminalCursorStyle.NormalizeActive(s.TerminalCursorStyle))),
                HorizontalAlignment = HorizontalAlignment.Left,
                MinWidth = 180
            };
            var inactive = new ComboBox
            {
                ItemsSource = InactiveNames.ToList(),
                SelectedIndex = Math.Max(0, Array.IndexOf(TerminalCursorStyle.InactiveStyles, TerminalCursorStyle.NormalizeInactive(s.TerminalCursorStyleInactive))),
                HorizontalAlignment = HorizontalAlignment.Left,
                MinWidth = 180
            };
            string defSize = TerminalOptions.FormatNumber(TerminalOptions.DefaultFontSize(MacKeymap.IsMacOS));
            var size = Box(s.TerminalFontSize > 0 ? TerminalOptions.FormatNumber(s.TerminalFontSize) : "", 90, defSize);
            var lineHeight = Box(TerminalOptions.FormatNumber(TerminalOptions.ClampLineHeight(s.TerminalLineHeight)), 90);
            var family = Box(s.TerminalFontFamily ?? "", 260, "editor font (Menlo / Consolas)");
            var contrast = Box(TerminalOptions.FormatNumber(TerminalOptions.ClampContrast(s.TerminalMinimumContrastRatio)), 90);
            return new Result
            {
                BlinkBox = blink,
                SmoothBox = smooth,
                StyleCombo = style,
                InactiveCombo = inactive,
                FontSizeBox = size,
                LineHeightBox = lineHeight,
                FontFamilyBox = family,
                ContrastBox = contrast,
                Apply = st =>
                {
                    st.TerminalCursorBlinking = blink.IsChecked == true;
                    st.TerminalSmoothScrolling = smooth.IsChecked == true;
                    int i = style.SelectedIndex;
                    st.TerminalCursorStyle = i >= 0 && i < TerminalCursorStyle.ActiveStyles.Length ? TerminalCursorStyle.ActiveStyles[i] : TerminalCursorStyle.Block;
                    int j = inactive.SelectedIndex;
                    st.TerminalCursorStyleInactive = j >= 0 && j < TerminalCursorStyle.InactiveStyles.Length ? TerminalCursorStyle.InactiveStyles[j] : TerminalCursorStyle.Outline;
                    double fs = TerminalOptions.ParseNumber(size.Text, st.TerminalFontSize);
                    st.TerminalFontSize = fs >= 6 && fs <= 100 ? fs : 0;
                    st.TerminalLineHeight = TerminalOptions.ClampLineHeight(TerminalOptions.ParseNumber(lineHeight.Text, st.TerminalLineHeight));
                    st.TerminalFontFamily = (family.Text ?? "").Trim();
                    st.TerminalMinimumContrastRatio = TerminalOptions.ClampContrast(TerminalOptions.ParseNumber(contrast.Text, st.TerminalMinimumContrastRatio));
                    // the window applies them (EditorNicetyOptions.ApplyFrom -> TerminalOptions, then the terminals)
                }
            };
        }
    }
}
