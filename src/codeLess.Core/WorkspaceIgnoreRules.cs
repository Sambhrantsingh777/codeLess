using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

namespace codeLess.Studio
{
    /// <summary>One parsed .gitignore file (patterns are relative to BaseDir). Strict C# 5.</summary>
    public sealed class GitIgnoreFile
    {
        private sealed class Rule
        {
            public bool Negate;
            public bool DirOnly;
            public int Kind;          // 0 = basename literal, 1 = basename suffix ("*.log"), 2 = regex on relative path, 3 = regex on basename
            public string Literal;
            public Regex Rx;
        }

        private readonly List<Rule> rules = new List<Rule>();
        private static readonly bool IgnoreCase = DetectCaseInsensitiveFs();

        public string BaseDir { get; private set; }
        public int RuleCount { get { return rules.Count; } }

        public GitIgnoreFile(string baseDir, IEnumerable<string> lines)
        {
            BaseDir = baseDir;
            if (lines == null) return;
            foreach (string l in lines) AddPattern(l);
        }

        public static GitIgnoreFile Load(string baseDir, string gitignorePath)
        {
            try
            {
                if (!File.Exists(gitignorePath)) return null;
                FileInfo fi = new FileInfo(gitignorePath);
                if (fi.Length > 1024 * 1024) return null;
                GitIgnoreFile f = new GitIgnoreFile(baseDir, File.ReadAllLines(gitignorePath));
                return f.RuleCount > 0 ? f : null;
            }
            catch { return null; }
        }

        private static bool DetectCaseInsensitiveFs()
        {
            if (Path.DirectorySeparatorChar == '\\') return true;
            try { return Directory.Exists("/System/Library/CoreServices"); } catch { return false; }
        }

        private StringComparison Cmp { get { return IgnoreCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal; } }

        public void AddPattern(string line)
        {
            if (line == null) return;
            string p = line;
            // Trailing spaces are ignored unless escaped.
            while (p.Length > 0 && (p[p.Length - 1] == ' ' || p[p.Length - 1] == '\t' || p[p.Length - 1] == '\r'))
            {
                if (p.Length >= 2 && p[p.Length - 2] == '\\') { p = p.Substring(0, p.Length - 2) + " "; break; }
                p = p.Substring(0, p.Length - 1);
            }
            if (p.Length == 0 || p[0] == '#') return;
            Rule r = new Rule();
            if (p[0] == '!') { r.Negate = true; p = p.Substring(1); }
            else if (p.StartsWith("\\!") || p.StartsWith("\\#")) p = p.Substring(1);
            if (p.EndsWith("/")) { r.DirOnly = true; p = p.TrimEnd('/'); }
            if (p.Length == 0) return;
            bool anchored = p.IndexOf('/') >= 0;
            if (p.StartsWith("/")) p = p.Substring(1);
            if (p.StartsWith("**/")) { anchored = p.IndexOf('/', 3) >= 0; if (!anchored) p = p.Substring(3); }
            bool hasWildcard = p.IndexOfAny(new[] { '*', '?', '[', '\\' }) >= 0;
            if (!anchored && !hasWildcard)
            {
                r.Kind = 0; r.Literal = p;
            }
            else if (!anchored && p.StartsWith("*") && p.IndexOfAny(new[] { '*', '?', '[', '\\' }, 1) < 0)
            {
                r.Kind = 1; r.Literal = p.Substring(1);
            }
            else
            {
                RegexOptions opts = RegexOptions.CultureInvariant | (IgnoreCase ? RegexOptions.IgnoreCase : RegexOptions.None);
                string body = GlobToRegex(p);
                if (anchored) { r.Kind = 2; r.Rx = new Regex("^" + body + "$", opts); }
                else { r.Kind = 3; r.Rx = new Regex("^" + body + "$", opts); }
            }
            rules.Add(r);
        }

        /// <summary>gitignore glob -> regex body: ** spans directories, * and ? stay within one path segment.</summary>
        public static string GlobToRegex(string glob)
        {
            StringBuilder sb = new StringBuilder();
            int i = 0;
            while (i < glob.Length)
            {
                char c = glob[i];
                if (c == '*')
                {
                    bool dbl = i + 1 < glob.Length && glob[i + 1] == '*';
                    if (dbl)
                    {
                        bool atStart = i == 0 || glob[i - 1] == '/';
                        bool slashAfter = i + 2 < glob.Length && glob[i + 2] == '/';
                        bool atEnd = i + 2 == glob.Length;
                        if (atStart && slashAfter) { sb.Append("(?:.*/)?"); i += 3; continue; }
                        if (atStart && atEnd) { sb.Append(".*"); i += 2; continue; }
                        sb.Append(".*"); i += 2; continue;
                    }
                    sb.Append("[^/]*"); i++; continue;
                }
                if (c == '?') { sb.Append("[^/]"); i++; continue; }
                if (c == '[')
                {
                    int close = glob.IndexOf(']', i + 1);
                    if (close > i + 1)
                    {
                        string cls = glob.Substring(i + 1, close - i - 1);
                        if (cls.StartsWith("!")) cls = "^" + cls.Substring(1);
                        sb.Append('[').Append(cls.Replace("\\", "\\\\")).Append(']');
                        i = close + 1;
                        continue;
                    }
                }
                if (c == '\\' && i + 1 < glob.Length) { sb.Append(Regex.Escape(glob[i + 1].ToString())); i += 2; continue; }
                sb.Append(Regex.Escape(c.ToString()));
                i++;
            }
            return sb.ToString();
        }

        /// <summary>
        /// Returns 1 if the last matching rule ignores the path, -1 if it re-includes it (negation), 0 if no rule matches.
        /// relPath uses '/' and is relative to BaseDir.
        /// </summary>
        public int Match(string relPath, string name, bool isDir)
        {
            for (int i = rules.Count - 1; i >= 0; i--)
            {
                Rule r = rules[i];
                if (r.DirOnly && !isDir) continue;
                bool m;
                switch (r.Kind)
                {
                    case 0: m = string.Equals(name, r.Literal, Cmp); break;
                    case 1: m = name.EndsWith(r.Literal, Cmp); break;
                    case 2: m = r.Rx.IsMatch(relPath); break;
                    default: m = r.Rx.IsMatch(name); break;
                }
                if (m) return r.Negate ? -1 : 1;
            }
            return 0;
        }
    }

    /// <summary>
    /// What indexing, Quick Open and Find in Files skip in a workspace: default/user excluded folder names
    /// (node_modules, .git, dist, build, ...) plus .gitignore files (root, nested, and .git/info/exclude), like VS Code's
    /// files.exclude + search.useIgnoreFiles. The Explorer still shows everything (dimmed). Thread-safe; strict C# 5.
    /// </summary>
    public sealed class WorkspaceIgnoreRules
    {
        public const int DefaultMaxIndexedFiles = 200000;
        public const int MaxWalkDepth = 64;

        private readonly string root;
        private readonly HashSet<string> excludedNames;
        private readonly bool useGitIgnore;
        private readonly Dictionary<string, GitIgnoreFile> gitignoreCache = new Dictionary<string, GitIgnoreFile>(StringComparer.Ordinal);
        private readonly Dictionary<string, bool> nugetCache = new Dictionary<string, bool>(StringComparer.Ordinal);
        private readonly object cacheLock = new object();
        private GitIgnoreFile infoExclude;

        public string Root { get { return root; } }
        public bool UsesGitIgnore { get { return useGitIgnore; } }

        public WorkspaceIgnoreRules(string workspaceRoot, IEnumerable<string> excludedFolderNames, bool respectGitIgnore)
        {
            root = string.IsNullOrEmpty(workspaceRoot) ? "" : Path.GetFullPath(workspaceRoot).TrimEnd('/', '\\');
            excludedNames = new HashSet<string>(excludedFolderNames ?? WorkspaceSearchEngine.DefaultExcludedFolders, StringComparer.OrdinalIgnoreCase);
            excludedNames.Add(".git");
            useGitIgnore = respectGitIgnore;
            if (useGitIgnore && root.Length > 0)
                infoExclude = GitIgnoreFile.Load(root, Path.Combine(Path.Combine(root, ".git"), Path.Combine("info", "exclude")));
        }

        private GitIgnoreFile GetGitIgnore(string dir)
        {
            lock (cacheLock)
            {
                GitIgnoreFile f;
                if (gitignoreCache.TryGetValue(dir, out f)) return f;
            }
            GitIgnoreFile loaded = GitIgnoreFile.Load(dir, Path.Combine(dir, ".gitignore"));
            lock (cacheLock) { gitignoreCache[dir] = loaded; }
            return loaded;
        }

        /// <summary>Forget cached .gitignore contents (call when a .gitignore file changes).</summary>
        public void InvalidateGitIgnoreCache()
        {
            lock (cacheLock) { gitignoreCache.Clear(); }
        }

        /// <summary>
        /// "packages" is in the default exclude list for NuGet's packages folder, but in JS monorepos packages/ holds
        /// the source. It is only excluded when it looks like a NuGet folder (repositories.config or *.nupkg inside).
        /// </summary>
        private bool IsExcludedName(string dir, string name)
        {
            if (!excludedNames.Contains(name)) return false;
            if (!string.Equals(name, "packages", StringComparison.OrdinalIgnoreCase)) return true;
            lock (cacheLock)
            {
                bool v;
                if (nugetCache.TryGetValue(dir, out v)) return v;
            }
            bool nuget = false;
            try
            {
                if (File.Exists(Path.Combine(dir, "repositories.config"))) nuget = true;
                else
                {
                    int checkedDirs = 0;
                    foreach (string sub in Directory.EnumerateDirectories(dir))
                    {
                        if (++checkedDirs > 8) break;
                        foreach (string f in Directory.EnumerateFiles(sub, "*.nupkg")) { nuget = true; break; }
                        if (nuget) break;
                    }
                }
            }
            catch { }
            lock (cacheLock) { nugetCache[dir] = nuget; }
            return nuget;
        }

        /// <summary>
        /// gitignore verdict for one entry given the chain of directories from the root down to its parent
        /// (nearest .gitignore wins; within a file the last matching rule wins).
        /// </summary>
        private bool IsGitIgnored(string fullPath, string name, bool isDir)
        {
            if (!useGitIgnore || root.Length == 0) return false;
            string parent = Path.GetDirectoryName(fullPath);
            if (parent == null) return false;
            // Walk from the entry's directory up to the root; the first file with a verdict decides.
            string dir = parent;
            while (dir != null && dir.Length >= root.Length)
            {
                GitIgnoreFile gi = GetGitIgnore(dir);
                if (gi != null)
                {
                    string rel = RelativeTo(dir, fullPath);
                    int v = gi.Match(rel, name, isDir);
                    if (v != 0) return v > 0;
                }
                if (dir.Length == root.Length) break;
                dir = Path.GetDirectoryName(dir);
            }
            if (infoExclude != null)
            {
                int v = infoExclude.Match(RelativeTo(root, fullPath), name, isDir);
                if (v != 0) return v > 0;
            }
            return false;
        }

        private static string RelativeTo(string dir, string fullPath)
        {
            if (fullPath.Length <= dir.Length + 1) return Path.GetFileName(fullPath);
            return fullPath.Substring(dir.Length + 1).Replace('\\', '/');
        }

        /// <summary>True when the walker should not descend into this directory.</summary>
        public bool IsExcludedDirectory(string fullDir)
        {
            string name = Path.GetFileName(fullDir.TrimEnd('/', '\\'));
            if (string.IsNullOrEmpty(name)) return false;
            if (IsExcludedName(fullDir, name)) return true;
            return IsGitIgnored(fullDir, name, true);
        }

        /// <summary>True for files that indexing / search skip (binary extension or gitignored).</summary>
        public bool IsIgnoredFile(string fullPath)
        {
            string name = Path.GetFileName(fullPath);
            if (string.IsNullOrEmpty(name)) return true;
            string ext = Path.GetExtension(name);
            if (!string.IsNullOrEmpty(ext) && WorkspaceSearchEngine.BinaryExtensions.Contains(ext)) return true;
            return IsGitIgnored(fullPath, name, false);
        }

        /// <summary>
        /// True if the path or any ancestor directory below the root is excluded (used to drop file-watcher events for
        /// node_modules / build output without touching the index).
        /// </summary>
        public bool IsPathExcluded(string fullPath, bool isDirectory)
        {
            if (string.IsNullOrEmpty(fullPath) || root.Length == 0) return false;
            if (!fullPath.StartsWith(root, StringComparison.OrdinalIgnoreCase) || fullPath.Length <= root.Length) return false;
            string rel = fullPath.Substring(root.Length + 1);
            string[] parts = rel.Split('/', '\\');
            string cur = root;
            for (int i = 0; i < parts.Length; i++)
            {
                if (parts[i].Length == 0) continue;
                cur = cur + Path.DirectorySeparatorChar + parts[i];
                bool last = i == parts.Length - 1;
                if (!last || isDirectory)
                {
                    if (IsExcludedDirectory(cur)) return true;
                }
                else if (IsIgnoredFile(cur)) return true;
            }
            return false;
        }

        /// <summary>
        /// Breadth-first walk honouring excluded names and .gitignore. Directories are pruned (never enumerated), symlinked
        /// directories are not followed. Stops at maxFiles (capped = true) or on cancellation.
        /// </summary>
        public List<string> EnumerateFiles(IEnumerable<string> roots, int maxFiles, CancellationToken ct, out bool capped)
        {
            capped = false;
            List<string> results = new List<string>(Math.Min(maxFiles, 4096));
            Queue<KeyValuePair<string, int>> queue = new Queue<KeyValuePair<string, int>>();
            HashSet<string> seenRoots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (roots != null)
            {
                foreach (string r in roots)
                {
                    if (string.IsNullOrEmpty(r) || !Directory.Exists(r)) continue;
                    string full = Path.GetFullPath(r).TrimEnd('/', '\\');
                    if (seenRoots.Add(full)) queue.Enqueue(new KeyValuePair<string, int>(full, 0));
                }
            }
            while (queue.Count > 0)
            {
                if (ct.IsCancellationRequested) break;
                KeyValuePair<string, int> item = queue.Dequeue();
                List<string> subdirs = null;
                try
                {
                    DirectoryInfo di = new DirectoryInfo(item.Key);
                    foreach (FileSystemInfo fsi in di.EnumerateFileSystemInfos())
                    {
                        string full = fsi.FullName;
                        if ((fsi.Attributes & FileAttributes.Directory) == FileAttributes.Directory)
                        {
                            if ((fsi.Attributes & FileAttributes.ReparsePoint) == FileAttributes.ReparsePoint) continue;
                            if (item.Value + 1 > MaxWalkDepth || IsExcludedDirectory(full)) continue;
                            if (subdirs == null) subdirs = new List<string>();
                            subdirs.Add(full);
                        }
                        else
                        {
                            if (IsIgnoredFile(full)) continue;
                            if (results.Count >= maxFiles) { capped = true; return results; }
                            results.Add(full);
                        }
                    }
                }
                catch { }
                if (subdirs != null)
                {
                    subdirs.Sort(StringComparer.OrdinalIgnoreCase);
                    foreach (string s in subdirs) queue.Enqueue(new KeyValuePair<string, int>(s, item.Value + 1));
                }
            }
            return results;
        }

        public List<string> EnumerateFiles(IEnumerable<string> roots, int maxFiles, CancellationToken ct)
        {
            bool capped;
            return EnumerateFiles(roots, maxFiles, ct, out capped);
        }
    }
}
