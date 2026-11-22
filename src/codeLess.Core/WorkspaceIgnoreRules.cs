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
        /// <summary>True if any rule needs the path relative to BaseDir (others only look at the entry name).</summary>
        public bool HasAnchoredRules { get; private set; }

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
                if (anchored) { r.Kind = 2; r.Rx = new Regex("^" + body + "$", opts); HasAnchoredRules = true; }
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
        /// <summary>.gitignore files of the folders between the workspace root (exclusive) and the repository root (inclusive), nearest first.</summary>
        private GitIgnoreFile[] ancestorIgnores = new GitIgnoreFile[0];

        public string Root { get { return root; } }
        public bool UsesGitIgnore { get { return useGitIgnore; } }

        public WorkspaceIgnoreRules(string workspaceRoot, IEnumerable<string> excludedFolderNames, bool respectGitIgnore)
        {
            root = string.IsNullOrEmpty(workspaceRoot) ? "" : Path.GetFullPath(workspaceRoot).TrimEnd('/', '\\');
            excludedNames = new HashSet<string>(excludedFolderNames ?? WorkspaceSearchEngine.DefaultExcludedFolders, StringComparer.OrdinalIgnoreCase);
            excludedNames.Add(".git");
            useGitIgnore = respectGitIgnore;
            if (useGitIgnore && root.Length > 0)
            {
                // The opened folder may be a subfolder of a repository (or a linked worktree): like git / ripgrep, honour
                // the .gitignore files of the folders above it up to the repository root and the repository's info/exclude.
                string repoRoot = null;
                try { repoRoot = GitCli.FindRepoRoot(root); } catch { }
                if (string.IsNullOrEmpty(repoRoot)) repoRoot = root;
                repoRoot = Path.GetFullPath(repoRoot).TrimEnd('/', '\\');
                string commonDir = ResolveGitCommonDir(repoRoot);
                if (commonDir != null)
                    infoExclude = GitIgnoreFile.Load(repoRoot, Path.Combine(Path.Combine(commonDir, "info"), "exclude"));
                List<GitIgnoreFile> anc = new List<GitIgnoreFile>();
                if (repoRoot.Length < root.Length && root.StartsWith(repoRoot, StringComparison.OrdinalIgnoreCase))
                {
                    string d = Path.GetDirectoryName(root);
                    while (d != null && d.Length >= repoRoot.Length)
                    {
                        GitIgnoreFile gi = GitIgnoreFile.Load(d, Path.Combine(d, ".gitignore"));
                        if (gi != null) anc.Add(gi);
                        if (d.Length == repoRoot.Length) break;
                        d = Path.GetDirectoryName(d);
                    }
                }
                ancestorIgnores = anc.ToArray();
            }
        }

        /// <summary>The repository's common git dir (handles ".git" files of linked worktrees / submodules and commondir).</summary>
        public static string ResolveGitCommonDir(string repoRoot)
        {
            try
            {
                string dotGit = Path.Combine(repoRoot, ".git");
                string gitDir = null;
                if (Directory.Exists(dotGit)) gitDir = dotGit;
                else if (File.Exists(dotGit))
                {
                    string first = File.ReadAllText(dotGit).Trim();
                    if (first.StartsWith("gitdir:", StringComparison.OrdinalIgnoreCase))
                    {
                        string g = first.Substring(7).Trim();
                        gitDir = Path.IsPathRooted(g) ? g : Path.GetFullPath(Path.Combine(repoRoot, g));
                    }
                }
                if (gitDir == null || !Directory.Exists(gitDir)) return null;
                string commonFile = Path.Combine(gitDir, "commondir");
                if (File.Exists(commonFile))
                {
                    string c = File.ReadAllText(commonFile).Trim();
                    string common = Path.IsPathRooted(c) ? c : Path.GetFullPath(Path.Combine(gitDir, c));
                    if (Directory.Exists(common)) return common;
                }
                return gitDir;
            }
            catch { return null; }
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
            foreach (GitIgnoreFile gi in ancestorIgnores)
            {
                int v = gi.Match(RelativeTo(gi.BaseDir, fullPath), name, isDir);
                if (v != 0) return v > 0;
            }
            if (infoExclude != null)
            {
                int v = infoExclude.Match(RelativeTo(infoExclude.BaseDir, fullPath), name, isDir);
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
            Queue<WalkItem> queue = new Queue<WalkItem>();
            HashSet<string> seenRoots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (roots != null)
            {
                foreach (string r in roots)
                {
                    if (string.IsNullOrEmpty(r) || !Directory.Exists(r)) continue;
                    string full = Path.GetFullPath(r).TrimEnd('/', '\\');
                    if (seenRoots.Add(full)) queue.Enqueue(new WalkItem(full, 0, BuildChain(full)));
                }
            }
            while (queue.Count > 0)
            {
                if (ct.IsCancellationRequested) break;
                WalkItem item = queue.Dequeue();
                List<string> subdirs = null;
                GitIgnoreFile[] chain = item.Chain;
                try
                {
                    DirectoryInfo di = new DirectoryInfo(item.Dir);
                    foreach (FileSystemInfo fsi in di.EnumerateFileSystemInfos())
                    {
                        string full = fsi.FullName;
                        if ((fsi.Attributes & FileAttributes.Directory) == FileAttributes.Directory)
                        {
                            // Only symlinks / junctions are skipped: OneDrive Files-On-Demand folders are reparse points too.
                            if (LlmHarnessAndAgentEngine.IsSymlinkOrJunction(full, fsi.Attributes)) continue;
                            if (item.Depth + 1 > MaxWalkDepth) continue;
                            string dname = fsi.Name;
                            if (IsExcludedName(full, dname) || IsIgnoredByChain(chain, full, dname, true)) continue;
                            if (subdirs == null) subdirs = new List<string>();
                            subdirs.Add(full);
                        }
                        else
                        {
                            string fname = fsi.Name;
                            string ext = Path.GetExtension(fname);
                            if (!string.IsNullOrEmpty(ext) && WorkspaceSearchEngine.BinaryExtensions.Contains(ext)) continue;
                            if (IsIgnoredByChain(chain, full, fname, false)) continue;
                            if (results.Count >= maxFiles) { capped = true; return results; }
                            results.Add(full);
                        }
                    }
                }
                catch { }
                if (subdirs != null)
                {
                    subdirs.Sort(StringComparer.OrdinalIgnoreCase);
                    foreach (string sd in subdirs) queue.Enqueue(new WalkItem(sd, item.Depth + 1, ExtendChain(chain, sd)));
                }
            }
            return results;
        }

        private sealed class WalkItem
        {
            public readonly string Dir;
            public readonly int Depth;
            public readonly GitIgnoreFile[] Chain;
            public WalkItem(string dir, int depth, GitIgnoreFile[] chain) { Dir = dir; Depth = depth; Chain = chain; }
        }

        private static readonly GitIgnoreFile[] EmptyChain = new GitIgnoreFile[0];

        /// <summary>.gitignore files that apply to entries of dir, nearest first (dir itself, then ancestors up to the root).</summary>
        private GitIgnoreFile[] BuildChain(string dir)
        {
            if (!useGitIgnore || root.Length == 0) return EmptyChain;
            List<GitIgnoreFile> list = new List<GitIgnoreFile>();
            string d = dir;
            while (d != null && d.Length >= root.Length)
            {
                GitIgnoreFile gi = GetGitIgnore(d);
                if (gi != null) list.Add(gi);
                if (d.Length == root.Length) break;
                d = Path.GetDirectoryName(d);
            }
            list.AddRange(ancestorIgnores);
            if (infoExclude != null) list.Add(infoExclude);
            return list.ToArray();
        }

        private GitIgnoreFile[] ExtendChain(GitIgnoreFile[] parentChain, string dir)
        {
            if (!useGitIgnore || root.Length == 0) return EmptyChain;
            GitIgnoreFile own = GetGitIgnore(dir);
            if (own == null) return parentChain;
            GitIgnoreFile[] c = new GitIgnoreFile[parentChain.Length + 1];
            c[0] = own;
            Array.Copy(parentChain, 0, c, 1, parentChain.Length);
            return c;
        }

        private static bool IsIgnoredByChain(GitIgnoreFile[] chain, string fullPath, string name, bool isDir)
        {
            for (int i = 0; i < chain.Length; i++)
            {
                GitIgnoreFile gi = chain[i];
                string rel = gi.HasAnchoredRules ? RelativeTo(gi.BaseDir, fullPath) : name;
                int v = gi.Match(rel, name, isDir);
                if (v != 0) return v > 0;
            }
            return false;
        }

        public List<string> EnumerateFiles(IEnumerable<string> roots, int maxFiles, CancellationToken ct)
        {
            bool capped;
            return EnumerateFiles(roots, maxFiles, ct, out capped);
        }
    }
}
