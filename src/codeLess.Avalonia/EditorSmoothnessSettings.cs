using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using codeLess.Studio;

namespace codeLess.AvaloniaApp
{
    /// <summary>
    /// "Editor" settings for scrolling and the caret (VS Code editor.smoothScrolling, editor.cursorSmoothCaretAnimation,
    /// editor.cursorBlinking, editor.mouseWheelScrollSensitivity / fastScrollSensitivity).
    /// </summary>
    public static class EditorSmoothnessSettingsPanel
    {
        public sealed class Result
        {
            public CheckBox[] Boxes;
            public ComboBox BlinkCombo;
            public TextBox SensitivityBox;
            public TextBox FastSensitivityBox;
            public Action<IdePersistentSettings> Apply;
        }

        /// <summary>Display names of <see cref="CaretBlink.Styles"/> (same order).</summary>
        public static readonly string[] BlinkNames = { "Blink", "Smooth (fade)", "Phase", "Expand", "Solid (no blinking)" };

        public static Result Build(IdePersistentSettings s, Color textPrimary)
        {
            var fg = new SolidColorBrush(textPrimary);
            CheckBox Chk(string text, bool value, string tip)
            {
                var c = new CheckBox { Content = text, IsChecked = value, Foreground = fg, FontSize = UiTokens.FontUi };
                ToolTip.SetTip(c, tip);
                return c;
            }
            var smoothScroll = Chk("Smooth scrolling", s.EditorSmoothScrolling, "editor.smoothScrolling");
            var smoothCaret = Chk("Smooth caret animation", s.EditorSmoothCaretAnimation, "editor.cursorSmoothCaretAnimation");
            int idx = Array.IndexOf(CaretBlink.Styles, CaretBlink.Normalize(s.EditorCursorBlinking));
            var blink = new ComboBox { ItemsSource = BlinkNames.ToList(), SelectedIndex = Math.Max(0, idx), HorizontalAlignment = HorizontalAlignment.Left, MinWidth = 180 };
            var sens = new TextBox { Text = FormatNumber(s.EditorMouseWheelScrollSensitivity), Width = 90, HorizontalAlignment = HorizontalAlignment.Left };
            var fast = new TextBox { Text = FormatNumber(s.EditorFastScrollSensitivity), Width = 90, HorizontalAlignment = HorizontalAlignment.Left };
            return new Result
            {
                Boxes = new[] { smoothScroll, smoothCaret },
                BlinkCombo = blink,
                SensitivityBox = sens,
                FastSensitivityBox = fast,
                Apply = st =>
                {
                    st.EditorSmoothScrolling = smoothScroll.IsChecked == true;
                    st.EditorSmoothCaretAnimation = smoothCaret.IsChecked == true;
                    int i = blink.SelectedIndex;
                    st.EditorCursorBlinking = i >= 0 && i < CaretBlink.Styles.Length ? CaretBlink.Styles[i] : CaretBlink.Smooth;
                    st.EditorMouseWheelScrollSensitivity = ParseSensitivity(sens.Text, st.EditorMouseWheelScrollSensitivity, 1.0);
                    st.EditorFastScrollSensitivity = ParseSensitivity(fast.Text, st.EditorFastScrollSensitivity, 5.0);
                    // the window applies them (EditorNicetyOptions.ApplyFrom -> EditorScrollOptions) after saving
                }
            };
        }

        private static string FormatNumber(double v) => v.ToString("0.##", CultureInfo.InvariantCulture);

        public static double ParseSensitivity(string text, double current, double fallback)
        {
            if (!double.TryParse((text ?? "").Trim().Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out double v)) return current;
            return EditorScrollOptions.ClampSensitivity(v, fallback);
        }
    }
}
