using System;
using System.Collections.Generic;

namespace codeLess.Studio
{
    /// <summary>
    /// Most-recently-used order of the editors of one editor group (VS Code "recently used editors in group"):
    /// activation order, most recent first. Both UIs keep one per group. Strict C# 5.
    /// </summary>
    public sealed class EditorMruList<T> where T : class
    {
        private readonly List<T> items = new List<T>();

        public int Count { get { return items.Count; } }

        /// <summary>Marks an editor as the most recently used (call on every activation).</summary>
        public void Touch(T item)
        {
            if (item == null) return;
            int idx = IndexOfRef(item);
            if (idx == 0) return;
            if (idx > 0) items.RemoveAt(idx);
            items.Insert(0, item);
        }

        public void Remove(T item)
        {
            int idx = IndexOfRef(item);
            if (idx >= 0) items.RemoveAt(idx);
        }

        public void Clear() { items.Clear(); }

        private int IndexOfRef(T item)
        {
            if (item == null) return -1;
            for (int i = 0; i < items.Count; i++) if (ReferenceEquals(items[i], item)) return i;
            return -1;
        }

        private static bool ContainsRef(IList<T> list, T item)
        {
            if (list == null) return false;
            for (int i = 0; i < list.Count; i++) if (ReferenceEquals(list[i], item)) return true;
            return false;
        }

        /// <summary>
        /// The open editors in MRU order: editors that were activated first (most recent first), then editors never
        /// activated in tab-strip order. Entries that are no longer open are pruned.
        /// </summary>
        public List<T> Ordered(IList<T> openEditors)
        {
            List<T> result = new List<T>();
            if (openEditors == null) return result;
            for (int i = items.Count - 1; i >= 0; i--)
                if (!ContainsRef(openEditors, items[i])) items.RemoveAt(i);
            foreach (T it in items) result.Add(it);
            foreach (T it in openEditors)
                if (it != null && !ContainsRef(result, it)) result.Add(it);
            return result;
        }

        /// <summary>The most recently used open editor other than <paramref name="exclude"/> (null when none):
        /// the editor VS Code activates after the active one is closed (workbench.editor.focusRecentEditorAfterClose).</summary>
        public T MostRecentOther(IList<T> openEditors, T exclude)
        {
            foreach (T it in Ordered(openEditors))
                if (!ReferenceEquals(it, exclude)) return it;
            return null;
        }
    }

    /// <summary>
    /// State of the Ctrl+Tab "recently used editors" quick pick (VS Code workbench.action.quickOpen{Previous|Least}RecentlyUsedEditorInGroup):
    /// opens with the previous editor selected (Ctrl+Tab) or the least recently used one (Ctrl+Shift+Tab); further
    /// Ctrl+Tab / Ctrl+Shift+Tab presses move the selection; releasing Ctrl opens the selection; Escape cancels.
    /// </summary>
    public sealed class EditorSwitcherSession<T> where T : class
    {
        private List<T> items = new List<T>();

        public bool IsOpen { get; private set; }
        public int SelectedIndex { get; private set; }
        public List<T> Items { get { return items; } }

        public T Selected
        {
            get { return IsOpen && SelectedIndex >= 0 && SelectedIndex < items.Count ? items[SelectedIndex] : null; }
        }

        /// <summary>Opens the picker over <paramref name="mruOrdered"/> (index 0 = active editor). False when there is nothing to pick.</summary>
        public bool Begin(IList<T> mruOrdered, bool backwards)
        {
            items = mruOrdered != null ? new List<T>(mruOrdered) : new List<T>();
            if (items.Count == 0) { IsOpen = false; return false; }
            IsOpen = true;
            if (items.Count == 1) SelectedIndex = 0;
            else SelectedIndex = backwards ? items.Count - 1 : 1;
            return true;
        }

        /// <summary>Moves the selection (wraps like VS Code's quick pick).</summary>
        public void Move(int delta)
        {
            if (!IsOpen || items.Count == 0) return;
            int n = (SelectedIndex + delta) % items.Count;
            if (n < 0) n += items.Count;
            SelectedIndex = n;
        }

        public void Select(int index)
        {
            if (!IsOpen || index < 0 || index >= items.Count) return;
            SelectedIndex = index;
        }

        /// <summary>Closes the picker and returns the editor to open (null when cancelled / empty).</summary>
        public T Commit()
        {
            T sel = Selected;
            IsOpen = false;
            items = new List<T>();
            return sel;
        }

        public void Cancel()
        {
            IsOpen = false;
            items = new List<T>();
        }
    }
}
