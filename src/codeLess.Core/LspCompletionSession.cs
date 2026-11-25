using System;
using System.Collections.Generic;

namespace codeLess.Studio
{
    /// <summary>
    /// UI-independent state of one completion popup: when to (re)request, client-side filtering while the user keeps
    /// typing, selection. Both editors (WinForms and Avalonia) drive their popup from this. C# 5 only.
    /// </summary>
    public sealed class LspCompletionSession
    {
        public string FilePath;
        public int Line;
        /// <summary>Column where the identifier being completed starts.</summary>
        public int StartCol;
        public bool IsIncomplete;
        public List<LspCompletionItem> AllItems = new List<LspCompletionItem>();
        public List<LspCompletionItem> Visible = new List<LspCompletionItem>();
        public int SelectedIndex;
        public string Prefix = "";
        public const int MaxVisible = 200;
        /// <summary>
        /// Path / module completion: what counts as part of the segment being completed (file names contain '-' and
        /// '.'). Null = identifier characters. Set together with <see cref="ImportContext"/>.
        /// </summary>
        public Func<char, bool> SegmentCharPredicate;
        /// <summary>Non-null when this list completes an import path / module (<see cref="ImportCompletionEngine"/>).</summary>
        public ImportCompletionContext ImportContext;

        public LspCompletionItem Selected
        {
            get { return SelectedIndex >= 0 && SelectedIndex < Visible.Count ? Visible[SelectedIndex] : null; }
        }

        /// <summary>
        /// Decides whether a typed character should open completion: identifier characters (after at least
        /// <paramref name="minWordLength"/> of them) or one of the server's trigger characters.
        /// </summary>
        public static bool ShouldTrigger(string lineText, int caretCol, string typed, IList<string> triggerCharacters, int minWordLength, out string triggerCharacter)
        {
            triggerCharacter = null;
            if (string.IsNullOrEmpty(typed)) return false;
            if (triggerCharacters != null)
            {
                foreach (string t in triggerCharacters)
                {
                    if (string.IsNullOrEmpty(t)) continue;
                    if (typed.EndsWith(t, StringComparison.Ordinal) || EndsWithAt(lineText, caretCol, t))
                    {
                        if (IsInsideLineComment(lineText, caretCol)) return false;
                        triggerCharacter = t;
                        return true;
                    }
                }
            }
            char last = typed[typed.Length - 1];
            if (!LspEditing.IsWordChar(last) || char.IsDigit(last) && LspEditing.WordStart(lineText, caretCol) == caretCol - 1) return false;
            int ws = LspEditing.WordStart(lineText, caretCol);
            if (caretCol - ws < Math.Max(1, minWordLength)) return false;
            if (ws < lineText.Length && char.IsDigit(lineText[ws])) return false; // numbers
            if (IsInsideLineComment(lineText, caretCol)) return false;
            return true;
        }

        private static bool EndsWithAt(string line, int col, string t)
        {
            if (line == null || col < t.Length || col > line.Length) return false;
            return string.CompareOrdinal(line, col - t.Length, t, 0, t.Length) == 0;
        }

        private static bool IsInsideLineComment(string line, int col)
        {
            if (string.IsNullOrEmpty(line)) return false;
            string before = line.Substring(0, Math.Min(col, line.Length));
            string t = before.TrimStart();
            return t.StartsWith("//", StringComparison.Ordinal) || t.StartsWith("#", StringComparison.Ordinal) && !t.StartsWith("#include", StringComparison.Ordinal) && !t.StartsWith("#import", StringComparison.Ordinal);
        }

        public void SetItems(LspCompletionList list)
        {
            AllItems = list != null ? list.Items : new List<LspCompletionItem>();
            IsIncomplete = list != null && list.IsIncomplete;
        }

        /// <summary>
        /// Re-filters for the current caret. Returns false when the session should close (caret left the word,
        /// moved to another line, or nothing matches).
        /// </summary>
        public bool Refilter(string lineText, int caretLine, int caretCol)
        {
            if (caretLine != Line || lineText == null || caretCol < StartCol || caretCol > lineText.Length) return false;
            for (int i = StartCol; i < caretCol; i++)
                if (SegmentCharPredicate != null ? !SegmentCharPredicate(lineText[i]) : !LspEditing.IsWordChar(lineText[i])) return false;
            string prefix = lineText.Substring(StartCol, caretCol - StartCol);
            LspCompletionItem previous = Selected;
            Prefix = prefix;
            Visible = LspCompletionList.FilterAndSort(AllItems, prefix, MaxVisible);
            if (Visible.Count == 0) return false;
            // Single exact match of what is already typed: nothing left to complete.
            if (Visible.Count == 1 && string.Equals(Visible[0].Label, prefix, StringComparison.Ordinal) && prefix.Length > 0 && !Visible[0].IsSnippet) return false;
            SelectedIndex = 0;
            if (prefix.Length == 0)
            {
                for (int i = 0; i < Visible.Count; i++) if (Visible[i].Preselect) { SelectedIndex = i; break; }
            }
            else if (previous != null)
            {
                int idx = Visible.IndexOf(previous);
                if (idx == 0) SelectedIndex = 0;
            }
            return true;
        }

        public void MoveSelection(int delta)
        {
            if (Visible.Count == 0) return;
            int n = SelectedIndex + delta;
            if (n < 0) n = delta < -1 ? 0 : Visible.Count - 1;
            else if (n >= Visible.Count) n = delta > 1 ? Visible.Count - 1 : 0;
            SelectedIndex = n;
        }

        /// <summary>Text shown on the right of an item (detail / label description).</summary>
        public static string RightText(LspCompletionItem it)
        {
            if (it == null) return "";
            if (!string.IsNullOrEmpty(it.LabelDescription)) return it.LabelDescription;
            if (!string.IsNullOrEmpty(it.Detail))
            {
                string d = it.Detail.Replace('\n', ' ');
                return d.Length > 48 ? d.Substring(0, 47) + "…" : d;
            }
            return LspCompletionItem.KindName(it.Kind);
        }

        public static string LeftText(LspCompletionItem it)
        {
            if (it == null) return "";
            return it.Label + (string.IsNullOrEmpty(it.LabelDetail) ? "" : it.LabelDetail);
        }

        /// <summary>
        /// True when <paramref name="ch"/> should commit the currently selected suggestion before being inserted
        /// (VS Code: editor.acceptSuggestionOnCommitCharacter + item.commitCharacters / completionProvider.allCommitCharacters).
        /// </summary>
        public bool IsCommitCharacter(char ch, IList<string> serverCommitCharacters)
        {
            if (!EditorNicetyOptions.AcceptSuggestionOnCommitCharacter) return false;
            LspCompletionItem sel = Selected;
            if (sel == null) return false;
            string s = ch.ToString();
            if (sel.CommitCharacters != null && sel.CommitCharacters.Count > 0)
                return sel.CommitCharacters.Contains(s);
            if (serverCommitCharacters != null && serverCommitCharacters.Count > 0)
                return serverCommitCharacters.Contains(s);
            return false;
        }
    }
}
