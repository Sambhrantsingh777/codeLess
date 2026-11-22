using System;
using System.Collections.Generic;
using System.IO;

namespace codeLess.Studio
{
    /// <summary>
    /// Multi-root workspaces (File → Add Folder to Workspace / Remove Folder from Workspace), shared by both UIs:
    /// the extra folders are remembered per primary folder, like VS Code's untitled workspace, and removing a folder
    /// (also the primary one) yields the new folder set. Strict C# 5.
    /// </summary>
    public static class WorkspaceFolders
    {
        private const char Sep = '\t';

        private static string Key(string path)
        {
            if (string.IsNullOrEmpty(path)) return "";
            string p;
            try { p = Path.GetFullPath(path); } catch { p = path; }
            return p.TrimEnd('\\', '/');
        }

        private static bool Same(string a, string b)
        {
            return string.Equals(Key(a), Key(b), StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>The extra folders remembered for <paramref name="primary"/> (existing folders only).</summary>
        public static List<string> Load(IdePersistentSettings s, string primary)
        {
            List<string> result = new List<string>();
            if (s == null || s.WorkspaceExtraFolders == null || string.IsNullOrEmpty(primary)) return result;
            foreach (string entry in s.WorkspaceExtraFolders)
            {
                if (string.IsNullOrEmpty(entry)) continue;
                int i = entry.IndexOf(Sep);
                if (i <= 0) continue;
                string root = entry.Substring(0, i), extra = entry.Substring(i + 1);
                if (!Same(root, primary) || Same(extra, primary)) continue;
                bool exists = false;
                try { exists = Directory.Exists(extra); } catch { }
                if (exists && !result.Exists(x => Same(x, extra))) result.Add(extra);
            }
            return result;
        }

        /// <summary>Replaces what is remembered for <paramref name="primary"/> (an empty list forgets it).</summary>
        public static void Store(IdePersistentSettings s, string primary, IEnumerable<string> extras)
        {
            if (s == null || string.IsNullOrEmpty(primary)) return;
            List<string> kept = new List<string>();
            if (s.WorkspaceExtraFolders != null)
                foreach (string entry in s.WorkspaceExtraFolders)
                {
                    int i = entry == null ? -1 : entry.IndexOf(Sep);
                    if (i > 0 && Same(entry.Substring(0, i), primary)) continue;
                    if (entry != null) kept.Add(entry);
                }
            if (extras != null)
                foreach (string e in extras)
                    if (!string.IsNullOrEmpty(e) && !Same(e, primary)) kept.Add(Key(primary) + Sep + Key(e));
            // bounded: the oldest workspaces' entries go first
            while (kept.Count > 200) kept.RemoveAt(0);
            s.WorkspaceExtraFolders = kept;
        }

        /// <summary>
        /// Removing <paramref name="removed"/> from (primary + extras). Removing the primary folder promotes the first extra
        /// (VS Code keeps the remaining folders open); returns false when the folder isn't part of the workspace.
        /// <paramref name="newPrimary"/> is null when nothing is left.
        /// </summary>
        public static bool Remove(string primary, IList<string> extras, string removed, out string newPrimary, out List<string> newExtras)
        {
            newPrimary = primary;
            newExtras = new List<string>(extras ?? new string[0]);
            if (string.IsNullOrEmpty(removed)) return false;
            if (Same(removed, primary))
            {
                if (newExtras.Count == 0) { newPrimary = null; return true; }
                newPrimary = newExtras[0];
                newExtras.RemoveAt(0);
                return true;
            }
            int idx = newExtras.FindIndex(x => Same(x, removed));
            if (idx < 0) return false;
            newExtras.RemoveAt(idx);
            return true;
        }
    }

    public partial class IdePersistentSettings
    {
        /// <summary>Extra workspace folders per primary folder: "primary\textra" entries.</summary>
        public List<string> WorkspaceExtraFolders { get; set; }
    }
}
