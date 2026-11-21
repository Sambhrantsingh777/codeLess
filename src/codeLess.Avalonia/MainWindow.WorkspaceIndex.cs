using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using codeLess.Studio;

namespace codeLess.AvaloniaApp
{
    /// <summary>
    /// Big-monorepo support: the Quick Open / indexing file list is built with WorkspaceIgnoreRules (excluded folder names
    /// + .gitignore, no depth limit, 200k cap) and kept current incrementally from file-watcher events instead of
    /// re-walking the whole tree; events inside ignored folders (node_modules, dist, ...) never touch the index.
    /// </summary>
    public partial class MainWindow
    {
        public const int MaxIndexedWorkspaceFiles = WorkspaceIgnoreRules.DefaultMaxIndexedFiles;
        private const int MaxQueuedStructuralChanges = 5000;

        private volatile WorkspaceIgnoreRules workspaceIgnoreRules;
        private readonly List<string> fsStructuralPaths = new List<string>();
        private readonly List<string> fsExcludedStructuralPaths = new List<string>();
        private bool fsStructuralOverflow;

        /// <summary>True when the last workspace walk stopped at MaxIndexedWorkspaceFiles.</summary>
        public bool WorkspaceFileListCapped { get; private set; }
        public int PerfIncrementalIndexUpdates;

        private static readonly string[] ExtraIndexExcludedNames = { "DerivedData", "Pods", ".gradle", ".nuxt", ".svelte-kit", "coverage", ".turbo", ".cache" };

        /// <summary>Exclusion rules for the current workspace (built on the UI thread; thread-safe to use afterwards).</summary>
        private WorkspaceIgnoreRules CreateWorkspaceIgnoreRules(string root)
        {
            var names = new HashSet<string>(GetExcludedFolderSet(root), StringComparer.OrdinalIgnoreCase);
            foreach (string n in ExtraIndexExcludedNames) names.Add(n);
            var rules = new WorkspaceIgnoreRules(root, names, WorkspaceSearchEngine.RespectGitIgnore);
            workspaceIgnoreRules = rules;
            return rules;
        }

        /// <summary>Background-safe walk used by workspace open and full re-index.</summary>
        /// <summary>Above this many indexed files Quick Open searches a QuickOpenFileIndex instead of materialising entries.</summary>
        public const int QuickOpenInlineFileLimit = 5000;
        private QuickOpenFileIndex quickOpenIndex;
        private long quickOpenIndexKey;

        /// <summary>Lower-cased Quick Open index over workspaceFiles, rebuilt only when the list changed.</summary>
        public QuickOpenFileIndex GetQuickOpenIndex()
        {
            long key = workspaceFiles.Count;
            unchecked
            {
                key = key * 31 + (workspaceRoot ?? "").GetHashCode();
                for (int i = 0; i < workspaceFiles.Count; i += 997) key = key * 31 + workspaceFiles[i].GetHashCode();
                if (workspaceFiles.Count > 0) key = key * 31 + workspaceFiles[workspaceFiles.Count - 1].GetHashCode();
            }
            if (quickOpenIndex == null || key != quickOpenIndexKey)
            {
                quickOpenIndex = new QuickOpenFileIndex(workspaceRoot, workspaceFiles.ToList());
                quickOpenIndexKey = key;
            }
            return quickOpenIndex;
        }

        private static List<string> CollectIndexableFiles(WorkspaceIgnoreRules rules, IEnumerable<string> roots, CancellationToken ct, out bool capped)
        {
            return rules.EnumerateFiles(roots, MaxIndexedWorkspaceFiles, ct, out capped);
        }

        private bool IsPathExcludedFromIndex(string fullPath)
        {
            var rules = workspaceIgnoreRules;
            if (rules == null) return false;
            try
            {
                bool isDir = Directory.Exists(fullPath);
                return rules.IsPathExcluded(fullPath, isDir);
            }
            catch { return false; }
        }

        /// <summary>Called under fsChangeLock for created / deleted / renamed paths.</summary>
        private void RecordStructuralFsChange(string fullPath, bool excludedFromIndex)
        {
            var list = excludedFromIndex ? fsExcludedStructuralPaths : fsStructuralPaths;
            if (list.Count < MaxQueuedStructuralChanges) list.Add(fullPath);
            else if (!excludedFromIndex) fsStructuralOverflow = true;
        }

        /// <summary>
        /// Applies queued create/delete events to workspaceFiles without a full walk. Returns false (caller falls back to a
        /// full background re-index) when the queue overflowed or a .gitignore changed.
        /// </summary>
        private bool ApplyIncrementalIndexChanges()
        {
            List<string> paths;
            bool overflow;
            lock (fsChangeLock)
            {
                paths = fsStructuralPaths.ToList();
                overflow = fsStructuralOverflow;
                fsStructuralPaths.Clear();
                fsStructuralOverflow = false;
            }
            var rules = workspaceIgnoreRules;
            if (overflow || rules == null) return false;
            if (paths.Any(p => string.Equals(Path.GetFileName(p), ".gitignore", StringComparison.OrdinalIgnoreCase)))
            {
                rules.InvalidateGitIgnoreCache();
                return false;
            }
            if (paths.Count == 0) return true;

            var set = new HashSet<string>(workspaceFiles, PathKeyComparer);
            bool changed = false;
            string sep = Path.DirectorySeparatorChar.ToString();
            foreach (string p in paths.Distinct(PathKeyComparer))
            {
                if (File.Exists(p))
                {
                    if (!set.Contains(p) && set.Count < MaxIndexedWorkspaceFiles && !rules.IsPathExcluded(p, false))
                    {
                        set.Add(p); workspaceFiles.Add(p); changed = true;
                    }
                }
                else if (Directory.Exists(p))
                {
                    if (rules.IsPathExcluded(p, true)) continue;
                    // A whole folder appeared (git checkout, unzip, mkdir -p + files): walk just that folder.
                    int room = MaxIndexedWorkspaceFiles - set.Count;
                    if (room <= 0) continue;
                    foreach (string f in rules.EnumerateFiles(new[] { p }, room, CancellationToken.None))
                    {
                        if (set.Add(f)) { workspaceFiles.Add(f); changed = true; }
                    }
                }
                else
                {
                    // Deleted file or folder: drop it and everything below it.
                    string prefix = p.TrimEnd('/', '\\') + sep;
                    int removed = workspaceFiles.RemoveAll(f => string.Equals(f, p, PathKeyComparison) || f.StartsWith(prefix, PathKeyComparison));
                    if (removed > 0)
                    {
                        set.RemoveWhere(f => string.Equals(f, p, PathKeyComparison) || f.StartsWith(prefix, PathKeyComparison));
                        changed = true;
                    }
                }
            }
            if (changed) PerfIncrementalIndexUpdates++;
            return true;
        }

        /// <summary>
        /// Changes inside ignored folders only matter to the Explorer, and only when their parent folder is currently
        /// expanded (visible). A burst inside a collapsed node_modules costs nothing.
        /// </summary>
        private bool ExcludedChangesAffectVisibleTree()
        {
            List<string> paths;
            lock (fsChangeLock)
            {
                paths = fsExcludedStructuralPaths.ToList();
                fsExcludedStructuralPaths.Clear();
            }
            if (paths.Count == 0) return false;
            var expanded = new HashSet<string>(PathKeyComparer);
            CollectExpandedDirectoryPaths(ExplorerTree.Nodes, expanded);
            foreach (string p in paths)
            {
                string parent = Path.GetDirectoryName(p);
                if (parent != null && expanded.Contains(parent)) return true;
            }
            return false;
        }

        private static void CollectExpandedDirectoryPaths(IEnumerable<ExplorerTreeNode> nodes, HashSet<string> into)
        {
            if (nodes == null) return;
            foreach (var n in nodes)
            {
                if (n == null || !n.IsDirectory || !n.IsExpanded) continue;
                if (n.Tag is string path) into.Add(path.TrimEnd('/', '\\'));
                CollectExpandedDirectoryPaths(n.Nodes, into);
            }
        }
    }
}
