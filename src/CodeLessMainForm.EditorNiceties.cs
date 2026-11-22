using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;

namespace codeLess.Studio
{
    /// <summary>Command palette and settings wiring for the editor niceties on Windows. STRICT C# 5.</summary>
    public partial class CodeLessMainForm
    {
        /// <summary>Palette commands "nicety_*" (see <see cref="EditorNicetiesPaletteWinForms"/>). True when handled.</summary>
        private bool ExecuteEditorNicetyCommand(string cmdId)
        {
            if (string.IsNullOrEmpty(cmdId) || !cmdId.StartsWith("nicety_", StringComparison.Ordinal)) return false;
            if (cmdId == "nicety_change_all")
            {
                if (editorControl != null) editorControl.SelectAllOccurrences();
                return true;
            }
            switch (cmdId)
            {
                case "nicety_sticky": EditorNicetyOptions.StickyScroll = !EditorNicetyOptions.StickyScroll; break;
                case "nicety_bracket_colors": EditorNicetyOptions.BracketPairColorization = !EditorNicetyOptions.BracketPairColorization; break;
                case "nicety_bracket_guides": EditorNicetyOptions.BracketPairGuides = !EditorNicetyOptions.BracketPairGuides; break;
                case "nicety_indent_guides": EditorNicetyOptions.IndentGuides = !EditorNicetyOptions.IndentGuides; break;
                case "nicety_occurrences": EditorNicetyOptions.HighlightOccurrences = !EditorNicetyOptions.HighlightOccurrences; break;
                case "nicety_textmate": EditorNicetyOptions.TextMateHighlighting = !EditorNicetyOptions.TextMateHighlighting; break;
                case "nicety_autoclose": EditorNicetyOptions.AutoCloseTags = !EditorNicetyOptions.AutoCloseTags; break;
                case "nicety_linked": EditorNicetyOptions.LinkedTagEditing = !EditorNicetyOptions.LinkedTagEditing; break;
                case "nicety_emmet": EditorNicetyOptions.EmmetOnTab = !EditorNicetyOptions.EmmetOnTab; break;
                case "nicety_smooth_scroll": EditorScrollOptions.SmoothScrolling = !EditorScrollOptions.SmoothScrolling; break;
                case "nicety_smooth_caret": EditorScrollOptions.SmoothCaretAnimation = !EditorScrollOptions.SmoothCaretAnimation; break;
                case "nicety_term_blink": TerminalOptions.CursorBlinking = !TerminalOptions.CursorBlinking; break;
                case "nicety_term_cursor_style": TerminalOptions.CycleCursorStyle(); break;
                case "nicety_term_smooth": TerminalOptions.SmoothScrolling = !TerminalOptions.SmoothScrolling; break;
                case "nicety_cursor_blinking":
                    {
                        int bi = Array.IndexOf(CaretBlink.Styles, CaretBlink.Normalize(EditorScrollOptions.CursorBlinking));
                        EditorScrollOptions.CursorBlinking = CaretBlink.Styles[(bi + 1) % CaretBlink.Styles.Length];
                        break;
                    }
                default: return false;
            }
            if (settings != null)
            {
                EditorNicetyOptions.CopyTo(settings);
                try { settings.Save(); } catch { }
            }
            if (editorControl != null) { editorControl.Canvas.ApplyCaretSettings(); editorControl.Invalidate(true); }
            if (splitEditorControl != null) { splitEditorControl.Canvas.ApplyCaretSettings(); splitEditorControl.Invalidate(true); }
            if (conPtyTerminalControl != null) conPtyTerminalControl.ApplyTerminalOptions();
            if (splitConPtyTerminalControl != null) splitConPtyTerminalControl.ApplyTerminalOptions();
            return true;
        }
    }

    /// <summary>Palette entries (category, title, shortcut, command id) shown by the WinForms command palette.</summary>
    public static class EditorNicetiesPaletteWinForms
    {
        public static readonly string[][] Commands = new string[][]
        {
            new[] { "Edit", "Edit: Change All Occurrences", "Ctrl+F2", "nicety_change_all" },
            new[] { "View", "View: Toggle Sticky Scroll", "", "nicety_sticky" },
            new[] { "View", "View: Toggle Bracket Pair Colorization", "", "nicety_bracket_colors" },
            new[] { "View", "View: Toggle Bracket Pair Guides", "", "nicety_bracket_guides" },
            new[] { "View", "View: Toggle Indent Guides", "", "nicety_indent_guides" },
            new[] { "View", "View: Toggle Occurrence Highlighting", "", "nicety_occurrences" },
            new[] { "View", "View: Toggle TextMate Syntax Highlighting", "", "nicety_textmate" },
            new[] { "Edit", "Edit: Toggle Auto Close Tags", "", "nicety_autoclose" },
            new[] { "Edit", "Edit: Toggle Linked Tag Editing", "", "nicety_linked" },
            new[] { "Edit", "Edit: Toggle Emmet Expansion on Tab", "", "nicety_emmet" },
            new[] { "View", "View: Toggle Smooth Scrolling", "", "nicety_smooth_scroll" },
            new[] { "View", "View: Toggle Smooth Caret Animation", "", "nicety_smooth_caret" },
            new[] { "View", "View: Cycle Cursor Blinking Style", "", "nicety_cursor_blinking" },
            new[] { "Terminal", "Terminal: Toggle Cursor Blinking", "", "nicety_term_blink" },
            new[] { "Terminal", "Terminal: Cycle Cursor Style (Block / Line / Underline)", "", "nicety_term_cursor_style" },
            new[] { "Terminal", "Terminal: Toggle Smooth Scrolling", "", "nicety_term_smooth" },
        };
    }

    /// <summary>"Editor" check boxes of the WinForms Settings dialog (uses the dialog's own check box factory).</summary>
    public static class EditorNicetiesSettingsWinForms
    {
        public static List<CheckBox> AddTo(IdePersistentSettings s, Func<string, bool, int, int, CheckBox> addCheck, ref int y)
        {
            List<CheckBox> boxes = new List<CheckBox>();
            boxes.Add(addCheck("Bracket pair colorization", s.EditorBracketPairColorization, 24, y));
            boxes.Add(addCheck("Bracket pair guides (active pair)", s.EditorBracketPairGuides, 380, y));
            y += 24;
            boxes.Add(addCheck("Indent guides", s.EditorIndentGuides, 24, y));
            boxes.Add(addCheck("Sticky scroll", s.EditorStickyScroll, 380, y));
            y += 24;
            boxes.Add(addCheck("Highlight occurrences of the word at the caret", s.EditorHighlightOccurrences, 24, y));
            boxes.Add(addCheck("TextMate grammars (bundled + VSIX)", s.EditorTextMateHighlighting, 380, y));
            y += 24;
            boxes.Add(addCheck("Auto close HTML / XML / JSX tags", s.EditorAutoCloseTags, 24, y));
            boxes.Add(addCheck("Linked tag rename", s.EditorLinkedTagEditing, 380, y));
            y += 24;
            boxes.Add(addCheck("Emmet abbreviations on Tab", s.EditorEmmetOnTab, 24, y));
            return boxes;
        }

        public static void Apply(List<CheckBox> boxes, IdePersistentSettings s)
        {
            if (boxes == null || boxes.Count < 9 || s == null) return;
            s.EditorBracketPairColorization = boxes[0].Checked;
            s.EditorBracketPairGuides = boxes[1].Checked;
            s.EditorIndentGuides = boxes[2].Checked;
            s.EditorStickyScroll = boxes[3].Checked;
            s.EditorHighlightOccurrences = boxes[4].Checked;
            s.EditorTextMateHighlighting = boxes[5].Checked;
            s.EditorAutoCloseTags = boxes[6].Checked;
            s.EditorLinkedTagEditing = boxes[7].Checked;
            s.EditorEmmetOnTab = boxes[8].Checked;
            EditorNicetyOptions.ApplyFrom(s);
        }
    }

    /// <summary>
    /// Scrolling / caret block of the WinForms Settings dialog (VS Code editor.smoothScrolling,
    /// editor.cursorSmoothCaretAnimation, editor.cursorBlinking, editor.mouseWheelScrollSensitivity).
    /// </summary>
    public sealed class EditorSmoothnessSettingsBlockWin
    {
        public static readonly string[] BlinkNames = new string[] { "Blink", "Smooth (fade)", "Phase", "Expand", "Solid (no blinking)" };
        public CheckBox SmoothScrollBox, SmoothCaretBox;
        public CodeLessDropdown BlinkCombo;
        public CodeLessDarkInputBox SensitivityInput;

        public static EditorSmoothnessSettingsBlockWin Build(Panel body, IdePersistentSettings s, StudioTheme theme, Func<string, bool, int, int, CheckBox> addCheck, ref int y)
        {
            EditorSmoothnessSettingsBlockWin b = new EditorSmoothnessSettingsBlockWin();
            b.SmoothScrollBox = addCheck("Smooth scrolling", s.EditorSmoothScrolling, 24, y);
            b.SmoothCaretBox = addCheck("Smooth caret animation", s.EditorSmoothCaretAnimation, 380, y);
            y += 30;
            Label lbl = new Label { Location = new Point(24, y + 4), Size = new Size(210, 20), Text = "Cursor blinking:", Font = new Font("Segoe UI", 8.8f), ForeColor = theme.FgSecondary, UseMnemonic = false };
            body.Controls.Add(lbl);
            b.BlinkCombo = new CodeLessDropdown { Location = new Point(240, y), Size = new Size(180, 26), DropDownStyle = ComboBoxStyle.DropDownList, Font = new Font("Segoe UI", 9.0f) };
            b.BlinkCombo.ApplyTheme(theme);
            foreach (string n in BlinkNames) b.BlinkCombo.Items.Add(n);
            b.BlinkCombo.SelectedIndex = Math.Max(0, Array.IndexOf(CaretBlink.Styles, CaretBlink.Normalize(s.EditorCursorBlinking)));
            body.Controls.Add(b.BlinkCombo);
            y += 32;
            Label lbl2 = new Label { Location = new Point(24, y + 4), Size = new Size(210, 20), Text = "Mouse wheel scroll sensitivity:", Font = new Font("Segoe UI", 8.8f), ForeColor = theme.FgSecondary, UseMnemonic = false };
            body.Controls.Add(lbl2);
            b.SensitivityInput = new CodeLessDarkInputBox
            {
                Location = new Point(240, y),
                Size = new Size(90, 26),
                Text = s.EditorMouseWheelScrollSensitivity.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture),
                Font = new Font("Consolas", 8.8f)
            };
            b.SensitivityInput.ApplyTheme(theme);
            body.Controls.Add(b.SensitivityInput);
            y += 32;
            return b;
        }

        public void Apply(IdePersistentSettings s)
        {
            s.EditorSmoothScrolling = SmoothScrollBox.Checked;
            s.EditorSmoothCaretAnimation = SmoothCaretBox.Checked;
            int i = BlinkCombo.SelectedIndex;
            s.EditorCursorBlinking = i >= 0 && i < CaretBlink.Styles.Length ? CaretBlink.Styles[i] : CaretBlink.Smooth;
            double v;
            string t = (SensitivityInput.Text ?? "").Trim().Replace(',', '.');
            if (double.TryParse(t, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out v))
                s.EditorMouseWheelScrollSensitivity = EditorScrollOptions.ClampSensitivity(v, 1.0);
            // ApplySettingsToEditors pushes them into EditorScrollOptions after saving
        }
    }

    /// <summary>
    /// Terminal block of the WinForms Settings dialog (VS Code terminal.integrated.cursorStyle, cursorStyleInactive,
    /// cursorBlinking, smoothScrolling, fontSize, lineHeight, fontFamily, minimumContrastRatio). STRICT C# 5.
    /// </summary>
    public sealed class TerminalFeelSettingsBlockWin
    {
        public static readonly string[] StyleNames = new string[] { "Block", "Line", "Underline" };
        public static readonly string[] InactiveNames = new string[] { "Outline", "Block", "Line", "Underline", "None" };
        public CheckBox BlinkBox, SmoothBox;
        public CodeLessDropdown StyleCombo, InactiveCombo;
        public CodeLessDarkInputBox FontSizeInput, LineHeightInput, FontFamilyInput, ContrastInput;

        public static TerminalFeelSettingsBlockWin Build(Panel body, IdePersistentSettings s, StudioTheme theme, Func<string, bool, int, int, CheckBox> addCheck, ref int y)
        {
            TerminalFeelSettingsBlockWin b = new TerminalFeelSettingsBlockWin();
            b.BlinkBox = addCheck("Terminal cursor blinking", s.TerminalCursorBlinking, 24, y);
            b.SmoothBox = addCheck("Terminal smooth scrolling", s.TerminalSmoothScrolling, 380, y);
            y += 30;
            b.StyleCombo = AddCombo(body, theme, "Terminal cursor style:", StyleNames, Array.IndexOf(TerminalCursorStyle.ActiveStyles, TerminalCursorStyle.NormalizeActive(s.TerminalCursorStyle)), ref y);
            b.InactiveCombo = AddCombo(body, theme, "Terminal cursor (unfocused):", InactiveNames, Array.IndexOf(TerminalCursorStyle.InactiveStyles, TerminalCursorStyle.NormalizeInactive(s.TerminalCursorStyleInactive)), ref y);
            b.FontSizeInput = AddInput(body, theme, "Terminal font size (px, empty = 14):", s.TerminalFontSize > 0 ? TerminalOptions.FormatNumber(s.TerminalFontSize) : "", 90, ref y);
            b.LineHeightInput = AddInput(body, theme, "Terminal line height:", TerminalOptions.FormatNumber(TerminalOptions.ClampLineHeight(s.TerminalLineHeight)), 90, ref y);
            b.FontFamilyInput = AddInput(body, theme, "Terminal font family (empty = Consolas):", s.TerminalFontFamily ?? "", 260, ref y);
            b.ContrastInput = AddInput(body, theme, "Terminal minimum contrast ratio:", TerminalOptions.FormatNumber(TerminalOptions.ClampContrast(s.TerminalMinimumContrastRatio)), 90, ref y);
            return b;
        }

        private static CodeLessDropdown AddCombo(Panel body, StudioTheme theme, string label, string[] items, int index, ref int y)
        {
            Label lbl = new Label { Location = new Point(24, y + 4), Size = new Size(210, 20), Text = label, Font = new Font("Segoe UI", 8.8f), ForeColor = theme.FgSecondary, UseMnemonic = false };
            body.Controls.Add(lbl);
            CodeLessDropdown combo = new CodeLessDropdown { Location = new Point(240, y), Size = new Size(180, 26), DropDownStyle = ComboBoxStyle.DropDownList, Font = new Font("Segoe UI", 9.0f) };
            combo.ApplyTheme(theme);
            foreach (string n in items) combo.Items.Add(n);
            combo.SelectedIndex = Math.Max(0, index);
            body.Controls.Add(combo);
            y += 32;
            return combo;
        }

        private static CodeLessDarkInputBox AddInput(Panel body, StudioTheme theme, string label, string text, int width, ref int y)
        {
            Label lbl = new Label { Location = new Point(24, y + 4), Size = new Size(210, 20), Text = label, Font = new Font("Segoe UI", 8.8f), ForeColor = theme.FgSecondary, UseMnemonic = false };
            body.Controls.Add(lbl);
            CodeLessDarkInputBox box = new CodeLessDarkInputBox { Location = new Point(240, y), Size = new Size(width, 26), Text = text, Font = new Font("Consolas", 8.8f) };
            box.ApplyTheme(theme);
            body.Controls.Add(box);
            y += 32;
            return box;
        }

        public void Apply(IdePersistentSettings s)
        {
            s.TerminalCursorBlinking = BlinkBox.Checked;
            s.TerminalSmoothScrolling = SmoothBox.Checked;
            int i = StyleCombo.SelectedIndex;
            s.TerminalCursorStyle = i >= 0 && i < TerminalCursorStyle.ActiveStyles.Length ? TerminalCursorStyle.ActiveStyles[i] : TerminalCursorStyle.Block;
            int j = InactiveCombo.SelectedIndex;
            s.TerminalCursorStyleInactive = j >= 0 && j < TerminalCursorStyle.InactiveStyles.Length ? TerminalCursorStyle.InactiveStyles[j] : TerminalCursorStyle.Outline;
            double fs = TerminalOptions.ParseNumber(FontSizeInput.Text, s.TerminalFontSize);
            s.TerminalFontSize = fs >= 6 && fs <= 100 ? fs : 0;
            s.TerminalLineHeight = TerminalOptions.ClampLineHeight(TerminalOptions.ParseNumber(LineHeightInput.Text, s.TerminalLineHeight));
            s.TerminalFontFamily = (FontFamilyInput.Text ?? "").Trim();
            s.TerminalMinimumContrastRatio = TerminalOptions.ClampContrast(TerminalOptions.ParseNumber(ContrastInput.Text, s.TerminalMinimumContrastRatio));
            // ApplySettingsToEditors pushes them into TerminalOptions and the terminals after saving
        }
    }
}
