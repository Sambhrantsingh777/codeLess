using System;
using System.Collections.Generic;

namespace codeLess.Studio
{
    /// <summary>
    /// VS Code-style input history (editor Find / Replace, Search view): Up/Down arrows in the box step through earlier
    /// searches; stepping Down past the newest entry restores what was being typed. Newest last, duplicates moved to the
    /// end, capped. Strict C# 5, not thread-safe (UI thread).
    /// </summary>
    public sealed class InputHistory
    {
        public const int DefaultCapacity = 50;
        private readonly List<string> items;
        private readonly int capacity;
        private int cursor = -1;     // -1 = not navigating (at the draft)
        private string draft;

        public InputHistory(IEnumerable<string> initial, int capacity)
        {
            this.capacity = Math.Max(1, capacity);
            items = new List<string>();
            if (initial != null)
                foreach (string s in initial) AddInternal(s);
        }

        public InputHistory() : this(null, DefaultCapacity) { }

        public List<string> Items { get { return new List<string>(items); } }
        public bool IsNavigating { get { return cursor >= 0; } }

        /// <summary>Records a committed search (Enter, Find Next, Replace, closing the widget). Returns true if it changed.</summary>
        public bool Add(string value)
        {
            cursor = -1;
            draft = null;
            return AddInternal(value);
        }

        private bool AddInternal(string value)
        {
            if (string.IsNullOrEmpty(value) || value.IndexOf('\n') >= 0 || value.Length > 1000) return false;
            int idx = items.IndexOf(value);
            if (idx == items.Count - 1 && idx >= 0) return false;
            if (idx >= 0) items.RemoveAt(idx);
            items.Add(value);
            while (items.Count > capacity) items.RemoveAt(0);
            return true;
        }

        /// <summary>Up arrow: the previous (older) entry, or null when there is nothing older.</summary>
        public string Previous(string current)
        {
            if (items.Count == 0) return null;
            if (cursor < 0)
            {
                draft = current ?? "";
                cursor = items.Count - 1;
                // Already showing the newest entry (e.g. right after Enter): go one further back.
                if (string.Equals(items[cursor], draft, StringComparison.Ordinal))
                {
                    if (cursor == 0) { cursor = -1; return null; }
                    cursor--;
                }
                return items[cursor];
            }
            if (cursor == 0) return null;
            cursor--;
            return items[cursor];
        }

        /// <summary>Down arrow: the next (newer) entry; past the newest, the text that was being typed. Null = nothing to do.</summary>
        public string Next(string current)
        {
            if (cursor < 0) return null;
            if (cursor < items.Count - 1)
            {
                cursor++;
                return items[cursor];
            }
            cursor = -1;
            string d = draft ?? "";
            draft = null;
            return d;
        }

        /// <summary>The user typed: leave history navigation (the next Up starts from the newest entry again).</summary>
        public void ResetNavigation()
        {
            cursor = -1;
            draft = null;
        }
    }

    /// <summary>Process-wide find / replace / search histories, persisted in settings.json (both UIs).</summary>
    public static class FindHistoryStore
    {
        private static InputHistory find, replace, search;
        private static readonly object gate = new object();

        public static InputHistory Find { get { EnsureLoaded(); return find; } }
        public static InputHistory Replace { get { EnsureLoaded(); return replace; } }
        public static InputHistory Search { get { EnsureLoaded(); return search; } }

        /// <summary>Tests: start empty and never touch settings.json.</summary>
        public static bool DisablePersistence;

        private static void EnsureLoaded()
        {
            lock (gate)
            {
                if (find != null) return;
                List<string> f = null, r = null, s = null;
                if (!DisablePersistence)
                {
                    try
                    {
                        IdePersistentSettings st = IdePersistentSettings.Load();
                        f = st.FindHistory; r = st.ReplaceHistory; s = st.SearchHistory;
                    }
                    catch { }
                }
                find = new InputHistory(f, InputHistory.DefaultCapacity);
                replace = new InputHistory(r, InputHistory.DefaultCapacity);
                search = new InputHistory(s, InputHistory.DefaultCapacity);
            }
        }

        /// <summary>Adds to <paramref name="h"/> and persists only the history fields when something changed.</summary>
        public static void Commit(InputHistory h, string value)
        {
            if (h == null || !h.Add(value) || DisablePersistence) return;
            try
            {
                IdePersistentSettings st = IdePersistentSettings.Load();
                st.FindHistory = Find.Items;
                st.ReplaceHistory = Replace.Items;
                st.SearchHistory = Search.Items;
                st.Save();
            }
            catch { }
        }

        /// <summary>Writes the current histories into <paramref name="st"/> before it is saved (no-op until first used).</summary>
        public static void CopyInto(IdePersistentSettings st)
        {
            if (st == null || DisablePersistence) return;
            lock (gate)
            {
                if (find == null) return;
                st.FindHistory = find.Items;
                st.ReplaceHistory = replace.Items;
                st.SearchHistory = search.Items;
            }
        }

        /// <summary>Tests: drop the in-memory histories.</summary>
        public static void ResetForTests()
        {
            lock (gate) { find = null; replace = null; search = null; }
        }
    }

    public partial class IdePersistentSettings
    {
        /// <summary>Editor Find widget history (newest last).</summary>
        public List<string> FindHistory { get; set; }
        /// <summary>Editor Replace history (newest last).</summary>
        public List<string> ReplaceHistory { get; set; }
        /// <summary>Search view (find in files) history (newest last).</summary>
        public List<string> SearchHistory { get; set; }
    }
}
