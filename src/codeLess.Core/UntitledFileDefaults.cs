using System;
using System.Collections.Generic;
using System.IO;

namespace codeLess.Studio
{
    /// <summary>
    /// Defaults for untitled editors (File → New Text File), shared by both UIs:
    /// the language / save extension follows the workspace's main language instead of a hard-coded ".cs", and an
    /// untitled editor that is empty never asks to be saved (VS Code). Strict C# 5.
    /// </summary>
    public static class UntitledFileDefaults
    {
        /// <summary>Source extensions that can define a workspace's main language (data/docs files don't).</summary>
        private static readonly HashSet<string> CodeExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "py", "cs", "ts", "tsx", "js", "jsx", "mjs", "go", "rs", "java", "kt", "swift", "c", "cc", "cpp", "h", "hpp",
            "m", "mm", "rb", "php", "dart", "scala", "lua", "sh", "ps1", "vue", "svelte", "html", "css", "scss", "sql",
            "r", "jl", "ex", "exs", "erl", "hs", "fs", "vb", "zig", "nim", "pl", "groovy", "clj", "elm", "ml"
        };

        /// <summary>Fallback when no code file decides it (a docs or config-only folder).</summary>
        private static readonly HashSet<string> SecondaryExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "md", "json", "yaml", "yml", "toml", "xml", "txt"
        };

        private static readonly HashSet<string> SkipDirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ".git", ".hg", ".svn", "node_modules", "bin", "obj", "dist", "build", "out", "target", ".venv", "venv", "env",
            "__pycache__", ".idea", ".vscode", ".next", ".nuxt", "coverage", "Pods", "DerivedData", ".gradle", ".tox",
            ".mypy_cache", ".pytest_cache", "vendor", ".cache", ".turbo", "site-packages"
        };

        private static readonly object cacheGate = new object();
        private static string cachedRoot;
        private static string cachedExt;
        private static DateTime cachedAtUtc;

        /// <summary>Max files examined when no file list is supplied (keeps it instant on huge trees).</summary>
        public const int MaxScannedFiles = 4000;

        /// <summary>
        /// The workspace's main language as a file extension without the dot ("py", "ts", ...), from
        /// <paramref name="knownFiles"/> when given (the UI's file index) or a bounded scan; "txt" when nothing decides.
        /// </summary>
        public static string DetectWorkspaceExtension(string workspaceRoot, IEnumerable<string> knownFiles)
        {
            if (string.IsNullOrEmpty(workspaceRoot) || !Directory.Exists(workspaceRoot)) return "txt";
            lock (cacheGate)
            {
                if (cachedRoot != null && string.Equals(cachedRoot, workspaceRoot, StringComparison.OrdinalIgnoreCase) &&
                    (DateTime.UtcNow - cachedAtUtc).TotalSeconds < 60)
                    return cachedExt;
            }
            Dictionary<string, int> counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            int seen = 0;
            if (knownFiles != null)
            {
                foreach (string f in knownFiles)
                {
                    if (seen++ >= 50000) break;
                    Count(counts, f);
                }
            }
            if (seen == 0) ScanBounded(workspaceRoot, counts);
            string best = Pick(counts, CodeExtensions) ?? Pick(counts, SecondaryExtensions) ?? "txt";
            lock (cacheGate)
            {
                cachedRoot = workspaceRoot;
                cachedExt = best;
                cachedAtUtc = DateTime.UtcNow;
            }
            return best;
        }

        /// <summary>Forget the cached detection (folder changed / tests).</summary>
        public static void ResetCache()
        {
            lock (cacheGate) { cachedRoot = null; cachedExt = null; }
        }

        private static void Count(Dictionary<string, int> counts, string path)
        {
            if (string.IsNullOrEmpty(path)) return;
            string ext;
            try { ext = Path.GetExtension(path); } catch { return; }
            if (string.IsNullOrEmpty(ext) || ext.Length < 2) return;
            ext = ext.Substring(1).ToLowerInvariant();
            if (ext == "d.ts") return;
            int n;
            counts.TryGetValue(ext, out n);
            counts[ext] = n + 1;
        }

        private static string Pick(Dictionary<string, int> counts, HashSet<string> allowed)
        {
            string best = null;
            int bestN = 0;
            foreach (KeyValuePair<string, int> kv in counts)
            {
                if (!allowed.Contains(kv.Key)) continue;
                // Ties: alphabetical, so the answer is stable.
                if (kv.Value > bestN || (kv.Value == bestN && best != null && string.CompareOrdinal(kv.Key, best) < 0))
                {
                    best = kv.Key;
                    bestN = kv.Value;
                }
            }
            if (best == "mjs") best = "js";
            if (best == "cc") best = "cpp";
            return best;
        }

        private static void ScanBounded(string root, Dictionary<string, int> counts)
        {
            int files = 0;
            Queue<KeyValuePair<string, int>> q = new Queue<KeyValuePair<string, int>>();
            q.Enqueue(new KeyValuePair<string, int>(root, 0));
            while (q.Count > 0 && files < MaxScannedFiles)
            {
                KeyValuePair<string, int> cur = q.Dequeue();
                string[] entries;
                try { entries = Directory.GetFiles(cur.Key); } catch { continue; }
                foreach (string f in entries)
                {
                    Count(counts, f);
                    if (++files >= MaxScannedFiles) break;
                }
                if (cur.Value >= 6) continue;
                string[] dirs;
                try { dirs = Directory.GetDirectories(cur.Key); } catch { continue; }
                Array.Sort(dirs, StringComparer.OrdinalIgnoreCase);
                foreach (string d in dirs)
                {
                    string name = Path.GetFileName(d);
                    if (string.IsNullOrEmpty(name) || SkipDirs.Contains(name) || name.StartsWith(".", StringComparison.Ordinal)) continue;
                    q.Enqueue(new KeyValuePair<string, int>(d, cur.Value + 1));
                }
            }
        }

        /// <summary>"Untitled-N" names that don't clash with open editors (VS Code shows no extension).</summary>
        public static string NextUntitledName(IEnumerable<string> openFileNames)
        {
            HashSet<string> open = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (openFileNames != null)
                foreach (string n in openFileNames)
                    if (!string.IsNullOrEmpty(n))
                    {
                        string bare = n;
                        try { bare = Path.GetFileNameWithoutExtension(n); } catch { }
                        open.Add(bare);
                    }
            int i = 1;
            while (open.Contains("Untitled-" + i)) i++;
            return "Untitled-" + i;
        }

        /// <summary>
        /// File name the Save As dialog proposes for an untitled editor: its name plus the extension of its language
        /// mode (when the user picked one) or else the workspace's main language.
        /// </summary>
        public static string SuggestSaveFileName(EditorTabBuffer tab, string workspaceRoot, IEnumerable<string> knownFiles)
        {
            string name = tab == null || string.IsNullOrEmpty(tab.FileName) ? "Untitled-1" : tab.FileName;
            string existingExt = "";
            try { existingExt = Path.GetExtension(name); } catch { }
            if (!string.IsNullOrEmpty(existingExt) && !IsUntitledLike(tab)) return name;
            string bare = name;
            try { bare = Path.GetFileNameWithoutExtension(name); } catch { }
            string ext = tab != null ? NormalizeLanguageExtension(tab.Language) : null;
            if (string.IsNullOrEmpty(ext)) ext = DetectWorkspaceExtension(workspaceRoot, knownFiles);
            return bare + "." + ext;
        }

        /// <summary>Language ids that are not extensions map to one; empty for plain text when nothing better is known.</summary>
        public static string NormalizeLanguageExtension(string language)
        {
            if (string.IsNullOrEmpty(language)) return null;
            string l = language.Trim().TrimStart('.').ToLowerInvariant();
            switch (l)
            {
                case "plaintext": case "text": return "txt";
                case "csharp": return "cs";
                case "python": return "py";
                case "typescript": return "ts";
                case "javascript": return "js";
                case "typescriptreact": return "tsx";
                case "javascriptreact": return "jsx";
                case "markdown": return "md";
                case "shellscript": case "bash": return "sh";
                case "rust": return "rs";
                case "golang": return "go";
                case "powershell": return "ps1";
                case "ruby": return "rb";
            }
            return l;
        }

        public static bool IsUntitledLike(EditorTabBuffer tab)
        {
            return tab != null && (tab.IsUntitled || string.IsNullOrEmpty(tab.FilePath));
        }

        /// <summary>An untitled editor with no text: closing it never asks to save (VS Code).</summary>
        public static bool IsEmptyUntitled(EditorTabBuffer tab)
        {
            if (!IsUntitledLike(tab) || tab.IsLargeFileViewer) return false;
            List<string> lines = tab.Lines;
            if (lines != null && lines.Count > 0)
                return lines.Count == 1 && string.IsNullOrEmpty(lines[0]);
            return string.IsNullOrEmpty(tab.Content);
        }

        /// <summary>Whether closing / quitting must offer to save this editor.</summary>
        public static bool NeedsSavePrompt(EditorTabBuffer tab)
        {
            return tab != null && tab.IsDirty && !IsEmptyUntitled(tab);
        }
    }
}
