using System.Collections.Generic;

namespace codeLess.Studio
{
    /// <summary>One editor context-menu entry; a null <see cref="CommandId"/> is a separator.</summary>
    public sealed class EditorContextEntry
    {
        public string Label;
        public string CommandId;

        public bool IsSeparator { get { return CommandId == null; } }
    }

    /// <summary>
    /// Merge-conflict and change-staging entries of the editor context menu, decided once for both UIs (QA #10):
    /// conflict under the caret -> Accept Current / Incoming / Both, Compare; file with changes vs the index ->
    /// Stage Change, Stage Selected Ranges, Revert Change. Command ids are the shared "par_*" ids. Strict C# 5.
    /// </summary>
    public static class EditorScmContextItems
    {
        public static List<EditorContextEntry> Build(EditorTabBuffer tab)
        {
            List<EditorContextEntry> items = new List<EditorContextEntry>();
            if (tab == null || tab.Lines == null) return items;
            if (GitConflictParser.BlockAt(ConflictDecorations.Blocks(tab), tab.CursorLine) != null)
            {
                Add(items, "Accept Current Change", "par_accept_current");
                Add(items, "Accept Incoming Change", "par_accept_incoming");
                Add(items, "Accept Both Changes", "par_accept_both");
                Add(items, "Compare Changes", "par_compare");
                Add(items, null, null);
            }
            if (!string.IsNullOrEmpty(tab.FilePath) && tab.GitHeadContent != null && tab.ChangedLineCount > 0)
            {
                Add(items, "Stage Change", "par_stage_change");
                Add(items, "Stage Selected Ranges", "par_stage_ranges");
                Add(items, "Revert Change", "par_revert_change");
                Add(items, null, null);
            }
            return items;
        }

        private static void Add(List<EditorContextEntry> items, string label, string id)
        {
            EditorContextEntry e = new EditorContextEntry();
            e.Label = label;
            e.CommandId = id;
            items.Add(e);
        }
    }
}
