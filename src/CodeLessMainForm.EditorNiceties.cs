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
                default: return false;
            }
            if (settings != null)
            {
                EditorNicetyOptions.CopyTo(settings);
                try { settings.Save(); } catch { }
            }
            if (editorControl != null) editorControl.Invalidate(true);
            if (splitEditorControl != null) splitEditorControl.Invalidate(true);
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
}
