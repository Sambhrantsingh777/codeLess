using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace codeLess.Studio
{
    /// <summary>Limits for building a verification copy (keeps disk use and time bounded).</summary>
    public sealed class VerificationOverlayOptions
    {
        /// <summary>Max bytes copied (dirty files in git mode, the whole tree in copy mode). Default 300 MB.</summary>
        public long MaxCopyBytes = 300L * 1024 * 1024;
        /// <summary>Max files copied in copy mode. Default 40 000.</summary>
        public int MaxCopyFiles = 40000;
        /// <summary>Skip single files larger than this in copy mode. Default 64 MB.</summary>
        public long MaxSingleFileBytes = 64L * 1024 * 1024;
        /// <summary>
        /// Use a private git clone (own refs, stash, config and hooks; objects shared read-only via alternates) when the
        /// workspace is in a git repository with at least one commit. (Name kept for compatibility: no linked worktree is
        /// created in the user's repository any more.)
        /// </summary>
        public bool PreferGitWorktree = true;
        /// <summary>
        /// Give the copy its own copy-on-write clone of node_modules / .venv / ... (APFS clonefile on macOS, reflink on
        /// Linux) so installs in the copy can't change the real folders. Falls back to a shared link when the file system
        /// can't clone.
        /// </summary>
        public bool CloneDependencyFolders = true;
        public CancellationToken Cancellation = CancellationToken.None;
    }

    /// <summary>
    /// A temporary copy of the workspace with the staged (not yet accepted) agent edits applied, so a build/test can
    /// run without touching the user's files.
    ///  - git repository: a private clone ("git clone --shared --no-checkout", detached checkout of HEAD with hooks and
    ///    LFS smudge disabled, remotes removed). It has its own refs, stash, config and hooks, so git commands in the copy
    ///    (commit, branch -f, tag, stash, push) can't change the user's repository; nothing is registered in the user's
    ///    repository (no linked worktree, no "git worktree prune"). Then the user's uncommitted changes (modified / added
    ///    / untracked-not-ignored / deleted files) are copied over, then the staged edits are written.
    ///  - otherwise: the workspace tree is copied (APFS clones on macOS via File.Copy), skipping build outputs and VCS
    ///    folders, within <see cref="VerificationOverlayOptions"/> limits.
    /// Dependency folders that are ignored and expensive to recreate (node_modules, .venv, venv, Pods, ...) get a
    /// copy-on-write clone (macOS APFS clonefile, Linux reflink) when the file system supports it; otherwise they are
    /// linked (symlink on Unix, junction on Windows) and reported in <see cref="SharedFolders"/> - writes there reach the
    /// real folder, so the agent is told and package installs are refused in the copy. Links are removed without
    /// following them. Overlays live under ~/.codeless/verify-overlays with a marker file; <see cref="CleanupStale"/>
    /// removes overlays left by a crashed process at the next start.
    /// </summary>
    public sealed class VerificationOverlay : IDisposable
    {
        public string Id;
        /// <summary>Root of the copy (the git worktree root in git mode).</summary>
        public string OverlayRoot;
        /// <summary>The workspace folder inside the copy (differs from OverlayRoot when the workspace is a repo subfolder).</summary>
        public string OverlayWorkspace;
        public string WorkspaceRoot;
        public string RepoRoot;
        /// <summary>"git-clone" (private clone of the repository) or "copy".</summary>
        public string Strategy = "";
        public int CopiedFiles;
        public long CopiedBytes;
        /// <summary>Dependency folders linked to the real ones (shared: writes reach the user's folder). Same as <see cref="SharedFolders"/>.</summary>
        public readonly List<string> LinkedFolders = new List<string>();
        /// <summary>Dependency folders that got a private copy-on-write clone (isolated).</summary>
        public readonly List<string> ClonedFolders = new List<string>();
        /// <summary>Dependency folders shared with the real workspace (could not be cloned on this file system).</summary>
        public List<string> SharedFolders { get { return LinkedFolders; } }
        public readonly List<string> Notes = new List<string>();
        public long CreateMs;
        private bool disposed;

        public static readonly string[] LinkedDependencyFolders = new string[] { "node_modules", ".venv", "venv", "Pods", "bower_components", ".yarn" };
        public static readonly string[] SkippedCopyFolders = new string[] { ".git", ".hg", ".svn", "bin", "obj", "target", "dist", "build", "out", ".next", ".nuxt", "__pycache__", ".pytest_cache", ".mypy_cache", ".tox", ".gradle", ".idea", ".vs", "coverage", ".codeless-verify" };

        private static string rootOverride;

        /// <summary>Folder holding the overlays (tests point this at a temp folder).</summary>
        public static string OverlaysRoot
        {
            get
            {
                if (!string.IsNullOrEmpty(rootOverride)) return rootOverride;
                return Path.Combine(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codeless"), "verify-overlays");
            }
            set { rootOverride = value; }
        }

        public string MapToOverlay(string workspacePath)
        {
            return MapPath(workspacePath, WorkspaceRoot, OverlayWorkspace);
        }

        public string MapToWorkspace(string overlayPath)
        {
            return MapPath(overlayPath, OverlayWorkspace, WorkspaceRoot);
        }

        private static string MapPath(string path, string fromRoot, string toRoot)
        {
            if (string.IsNullOrEmpty(path) || string.IsNullOrEmpty(fromRoot) || string.IsNullOrEmpty(toRoot)) return path;
            string full;
            try { full = Path.GetFullPath(path); } catch { return path; }
            string from = TrimSep(SafeFull(fromRoot));
            if (string.Equals(full, from, StringComparison.OrdinalIgnoreCase)) return toRoot;
            if (full.Length > from.Length && full.StartsWith(from, StringComparison.OrdinalIgnoreCase) && (full[from.Length] == '/' || full[from.Length] == '\\'))
                return Path.Combine(toRoot, full.Substring(from.Length + 1));
            // macOS: temp/home paths may be reported through /private.
            if (full.StartsWith("/private/", StringComparison.Ordinal) && !from.StartsWith("/private/", StringComparison.Ordinal))
                return MapPath(full.Substring("/private".Length), fromRoot, toRoot);
            return path;
        }

        private static string SafeFull(string p)
        {
            try { return Path.GetFullPath(p); } catch { return p; }
        }

        private static string TrimSep(string p)
        {
            return (p ?? "").TrimEnd('/', '\\');
        }

        /// <summary>
        /// Builds the overlay. <paramref name="stagedFiles"/> maps absolute workspace paths to the proposed text;
        /// <paramref name="deletedFiles"/> lists absolute workspace paths to delete in the copy. Returns null and sets
        /// <paramref name="error"/> when the copy can't be built (limits exceeded, git failure, cancelled).
        /// </summary>
        public static VerificationOverlay Create(string workspaceRoot, IDictionary<string, string> stagedFiles, IEnumerable<string> deletedFiles,
            VerificationOverlayOptions options, out string error)
        {
            error = null;
            if (options == null) options = new VerificationOverlayOptions();
            if (string.IsNullOrEmpty(workspaceRoot) || !Directory.Exists(workspaceRoot))
            {
                error = "No workspace folder is open.";
                return null;
            }
            Stopwatch sw = Stopwatch.StartNew();
            VerificationOverlay ov = new VerificationOverlay();
            ov.WorkspaceRoot = TrimSep(SafeFull(workspaceRoot));
            ov.Id = "v" + DateTime.UtcNow.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture) + "-" + Guid.NewGuid().ToString("N").Substring(0, 8);
            string root = OverlaysRoot;
            try { Directory.CreateDirectory(root); }
            catch (Exception ex) { error = "Can't create the verification folder: " + ex.Message; return null; }
            ov.OverlayRoot = Path.Combine(root, ov.Id);

            try
            {
                string repo = options.PreferGitWorktree && GitCli.IsAvailable() ? GitCli.FindRepoRoot(ov.WorkspaceRoot) : null;
                bool built = false;
                if (!string.IsNullOrEmpty(repo))
                {
                    ov.RepoRoot = TrimSep(SafeFull(repo));
                    WriteMarker(ov);
                    built = ov.TryBuildGitWorktree(options, out error);
                    if (!built && error == null)
                    {
                        ov.Notes.Add("git worktree unavailable; used a filtered copy instead.");
                        SafeDeleteTree(ov.OverlayRoot);
                    }
                    else if (!built)
                    {
                        ov.Dispose();
                        return null;
                    }
                }
                if (!built)
                {
                    ov.RepoRoot = null;
                    WriteMarker(ov);
                    if (!ov.TryBuildCopy(options, out error))
                    {
                        ov.Dispose();
                        return null;
                    }
                }
                options.Cancellation.ThrowIfCancellationRequested();
                ov.cloneDependencies = options.CloneDependencyFolders;
                ov.LinkDependencyFolders();
                if (ov.LinkedFolders.Count > 0)
                    ov.Notes.Add("Shared with your real workspace (not copied): " + string.Join(", ", ov.LinkedFolders.ToArray()) +
                                 ". Tools that write there (installs, caches) change your real folders.");
                ov.ApplyStaged(stagedFiles, deletedFiles);
            }
            catch (OperationCanceledException)
            {
                error = "Cancelled.";
                ov.Dispose();
                return null;
            }
            catch (Exception ex)
            {
                error = "Couldn't prepare the verification copy: " + ex.Message;
                ov.Dispose();
                return null;
            }
            ov.CreateMs = sw.ElapsedMilliseconds;
            return ov;
        }

        private bool TryBuildGitWorktree(VerificationOverlayOptions options, out string error)
        {
            error = null;
            GitCommandResult head = GitProcess.Run(RepoRoot, new string[] { "rev-parse", "--verify", "-q", "HEAD" }, 15000, options.Cancellation, null, null);
            if (!head.Success || string.IsNullOrWhiteSpace(head.Stdout)) return false; // no commits yet: copy mode
            string sha = head.Stdout.Trim();
            string nullHooks = Path.DirectorySeparatorChar == '\\' ? "NUL" : "/dev/null";
            // A private clone, not "git worktree add": a linked worktree shares refs, the stash, config and hooks with the
            // user's repository, so "git stash" / "git branch -f" / "git tag" in the copy would change the real repo.
            GitCommandResult clone = GitProcess.Run(RepoRoot, new string[]
            {
                "-c", "core.hooksPath=" + nullHooks,
                "clone", "--shared", "--no-checkout", "--quiet", "--", RepoRoot, OverlayRoot
            }, 10 * 60 * 1000, options.Cancellation, null, null);
            if (clone.Cancelled) throw new OperationCanceledException();
            if (!clone.Success || !Directory.Exists(Path.Combine(OverlayRoot, ".git")))
            {
                Notes.Add("git clone of the repository failed: " + FirstLine(clone.Stderr));
                SafeDeleteTree(OverlayRoot);
                return false;
            }
            GitCommandResult co = GitProcess.Run(OverlayRoot, new string[]
            {
                "-c", "core.hooksPath=" + nullHooks,
                "-c", "filter.lfs.smudge=", "-c", "filter.lfs.process=", "-c", "filter.lfs.required=false",
                "-c", "advice.detachedHead=false",
                "checkout", "--quiet", "--detach", "--force", sha
            }, 10 * 60 * 1000, options.Cancellation, null, null);
            if (co.Cancelled) throw new OperationCanceledException();
            if (!co.Success)
            {
                Notes.Add("checkout in the verification copy failed: " + FirstLine(co.Stderr));
                SafeDeleteTree(OverlayRoot);
                return false;
            }
            // No remotes: "git push" / "git fetch" in the copy can't reach the user's repository (or anything else via it).
            GitCommandResult remotes = GitProcess.Run(OverlayRoot, new string[] { "remote" }, 15000, options.Cancellation, null, null);
            if (remotes.Success)
                foreach (string rn in remotes.Stdout.Replace("\r", "").Split('\n'))
                    if (rn.Trim().Length > 0) GitProcess.Run(OverlayRoot, new string[] { "remote", "remove", rn.Trim() }, 15000, options.Cancellation, null, null);
            Strategy = "git-clone";
            string sub = RelativeTo(RepoRoot, WorkspaceRoot);
            OverlayWorkspace = string.IsNullOrEmpty(sub) ? OverlayRoot : Path.Combine(OverlayRoot, sub);

            // Bring over the user's uncommitted work so the verification sees exactly what accepting would produce.
            GitCommandResult st = GitProcess.Run(RepoRoot, new string[] { "status", "--porcelain=v1", "-z", "--untracked-files=all", "--ignore-submodules=all" }, 60000, options.Cancellation, null, null);
            if (st.Cancelled) throw new OperationCanceledException();
            if (!st.Success)
            {
                error = "git status failed: " + FirstLine(st.Stderr);
                return false;
            }
            foreach (GitStatusEntry e in ParsePorcelainZ(st.Stdout))
            {
                options.Cancellation.ThrowIfCancellationRequested();
                string src = Path.Combine(RepoRoot, e.Path.Replace('/', Path.DirectorySeparatorChar));
                string dst = Path.Combine(OverlayRoot, e.Path.Replace('/', Path.DirectorySeparatorChar));
                if (!string.IsNullOrEmpty(e.OrigPath))
                {
                    string old = Path.Combine(OverlayRoot, e.OrigPath.Replace('/', Path.DirectorySeparatorChar));
                    try { if (File.Exists(old)) File.Delete(old); } catch { }
                }
                if (File.Exists(src))
                {
                    long len = new FileInfo(src).Length;
                    if (CopiedBytes + len > options.MaxCopyBytes)
                    {
                        error = "Your uncommitted changes exceed the verification copy limit (" + (options.MaxCopyBytes / (1024 * 1024)) + " MB).";
                        return false;
                    }
                    CopyFile(src, dst);
                    CopiedFiles++;
                    CopiedBytes += len;
                }
                else if (Directory.Exists(src))
                {
                    // Untracked nested repository or submodule: not copied (documented limitation).
                    continue;
                }
                else
                {
                    try { if (File.Exists(dst)) File.Delete(dst); } catch { }
                }
            }
            if (File.Exists(Path.Combine(RepoRoot, ".gitmodules")))
                Notes.Add("Git submodules are not populated in the verification copy.");
            return true;
        }

        private bool TryBuildCopy(VerificationOverlayOptions options, out string error)
        {
            error = null;
            Strategy = "copy";
            Directory.CreateDirectory(OverlayRoot);
            OverlayWorkspace = OverlayRoot;
            HashSet<string> skip = new HashSet<string>(SkippedCopyFolders, StringComparer.OrdinalIgnoreCase);
            foreach (string d in LinkedDependencyFolders) skip.Add(d);
            Stack<string> stack = new Stack<string>();
            stack.Push(WorkspaceRoot);
            int skippedLarge = 0;
            while (stack.Count > 0)
            {
                options.Cancellation.ThrowIfCancellationRequested();
                string dir = stack.Pop();
                string[] files, dirs;
                try { files = Directory.GetFiles(dir); dirs = Directory.GetDirectories(dir); }
                catch { continue; }
                string rel = RelativeTo(WorkspaceRoot, dir);
                string targetDir = string.IsNullOrEmpty(rel) ? OverlayRoot : Path.Combine(OverlayRoot, rel);
                Directory.CreateDirectory(targetDir);
                foreach (string f in files)
                {
                    FileInfo fi;
                    try { fi = new FileInfo(f); } catch { continue; }
                    if (LlmHarnessAndAgentEngine.IsSymlinkOrJunction(f, fi.Attributes)) continue;
                    if (fi.Length > options.MaxSingleFileBytes) { skippedLarge++; continue; }
                    if (CopiedFiles + 1 > options.MaxCopyFiles || CopiedBytes + fi.Length > options.MaxCopyBytes)
                    {
                        error = "The workspace is too large to copy for verification (limit " + options.MaxCopyFiles + " files / " +
                                (options.MaxCopyBytes / (1024 * 1024)) + " MB). Use a git repository (verification then uses a git worktree) or set a narrower verification command.";
                        return false;
                    }
                    CopyFile(f, Path.Combine(targetDir, fi.Name));
                    CopiedFiles++;
                    CopiedBytes += fi.Length;
                }
                foreach (string d in dirs)
                {
                    string name = Path.GetFileName(d);
                    if (skip.Contains(name)) continue;
                    try { if (LlmHarnessAndAgentEngine.IsSymlinkOrJunction(d, new DirectoryInfo(d).Attributes)) continue; } catch { continue; }
                    stack.Push(d);
                }
            }
            if (skippedLarge > 0) Notes.Add(skippedLarge + " file(s) over " + (options.MaxSingleFileBytes / (1024 * 1024)) + " MB were not copied.");
            return true;
        }

        /// <summary>Links ignored dependency folders (node_modules, .venv, ...) from the workspace, up to 3 levels deep.</summary>
        private bool cloneDependencies = true;

        private void LinkDependencyFolders()
        {
            HashSet<string> deps = new HashSet<string>(LinkedDependencyFolders, StringComparer.OrdinalIgnoreCase);
            HashSet<string> noDescend = new HashSet<string>(SkippedCopyFolders, StringComparer.OrdinalIgnoreCase);
            Queue<KeyValuePair<string, int>> q = new Queue<KeyValuePair<string, int>>();
            q.Enqueue(new KeyValuePair<string, int>(WorkspaceRoot, 0));
            int visited = 0;
            while (q.Count > 0 && visited < 4000)
            {
                KeyValuePair<string, int> cur = q.Dequeue();
                visited++;
                string[] dirs;
                try { dirs = Directory.GetDirectories(cur.Key); } catch { continue; }
                foreach (string d in dirs)
                {
                    string name = Path.GetFileName(d);
                    bool isLink;
                    try { isLink = LlmHarnessAndAgentEngine.IsSymlinkOrJunction(d, new DirectoryInfo(d).Attributes); } catch { continue; }
                    if (deps.Contains(name))
                    {
                        string rel = RelativeTo(WorkspaceRoot, d);
                        string target = Path.Combine(OverlayWorkspace, rel);
                        if (Directory.Exists(target) || File.Exists(target)) continue; // tracked in git: already there
                        string parent = Path.GetDirectoryName(target);
                        if (!Directory.Exists(parent)) continue;
                        if (cloneDependencies && !isLink && TryCloneDirectoryCopyOnWrite(d, target))
                        {
                            ClonedFolders.Add(rel.Replace('\\', '/'));
                            continue;
                        }
                        if (CreateDirectoryLink(target, d)) LinkedFolders.Add(rel.Replace('\\', '/'));
                        continue;
                    }
                    if (isLink || noDescend.Contains(name) || cur.Value >= 2) continue;
                    q.Enqueue(new KeyValuePair<string, int>(d, cur.Value + 1));
                }
            }
        }

        private void ApplyStaged(IDictionary<string, string> stagedFiles, IEnumerable<string> deletedFiles)
        {
            if (stagedFiles != null)
            {
                foreach (KeyValuePair<string, string> kv in stagedFiles)
                {
                    if (!LlmHarnessAndAgentEngine.IsPathInsideWorkspace(WorkspaceRoot, kv.Key))
                        throw new InvalidOperationException("Staged file outside the workspace: " + kv.Key);
                    string dst = MapToOverlay(kv.Key);
                    if (string.Equals(dst, kv.Key, StringComparison.OrdinalIgnoreCase))
                        throw new InvalidOperationException("Could not map " + kv.Key + " into the verification copy.");
                    AgentChangeSetApplier.WriteTextLikeExisting(dst, kv.Value);
                }
            }
            if (deletedFiles != null)
            {
                foreach (string del in deletedFiles)
                {
                    string dst = MapToOverlay(del);
                    if (string.Equals(dst, del, StringComparison.OrdinalIgnoreCase)) continue;
                    try { if (File.Exists(dst)) File.Delete(dst); } catch { }
                }
            }
        }

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            try { SafeDeleteTree(OverlayRoot); } catch { }
            // Nothing to unregister: the copy is a standalone clone. (Never "git worktree prune" the user's repository - it
            // would also drop the metadata of the user's own worktrees whose folder is temporarily missing.)
            try { string m = MarkerPath(Id); if (File.Exists(m)) File.Delete(m); } catch { }
        }

        // ---------------------------------------------------------------- crash recovery

        private static string MarkerPath(string id)
        {
            return Path.Combine(OverlaysRoot, id + ".json");
        }

        private static void WriteMarker(VerificationOverlay ov)
        {
            Dictionary<string, object> m = new Dictionary<string, object>();
            m["pid"] = CurrentPid();
            m["repo"] = ov.RepoRoot ?? "";
            m["workspace"] = ov.WorkspaceRoot ?? "";
            m["createdUtc"] = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture);
            File.WriteAllText(MarkerPath(ov.Id), DapJson.Serialize(m), new UTF8Encoding(false));
        }

        private static int CurrentPid()
        {
            try { using (Process p = Process.GetCurrentProcess()) return p.Id; } catch { return 0; }
        }

        private static bool IsProcessAlive(int pid)
        {
            if (pid <= 0) return false;
            try
            {
                using (Process p = Process.GetProcessById(pid)) return !p.HasExited;
            }
            catch { return false; }
        }

        /// <summary>
        /// Removes overlays whose owning process is gone (crash, kill) or that are older than <paramref name="maxAge"/>,
        /// prunes their git worktree entries and returns how many were removed. Safe to call at startup on a worker thread.
        /// </summary>
        public static int CleanupStale(TimeSpan maxAge)
        {
            int removed = 0;
            string root = OverlaysRoot;
            if (!Directory.Exists(root)) return 0;
            HashSet<string> repos = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            HashSet<string> keep = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            string[] markers;
            try { markers = Directory.GetFiles(root, "*.json"); } catch { return 0; }
            foreach (string marker in markers)
            {
                string id = Path.GetFileNameWithoutExtension(marker);
                int pid = 0;
                string repo = null;
                DateTime created = DateTime.MinValue;
                try
                {
                    string err;
                    object o = DapJson.TryParse(File.ReadAllText(marker), out err);
                    Dictionary<string, object> d = o as Dictionary<string, object>;
                    if (d != null)
                    {
                        if (d.ContainsKey("pid") && d["pid"] != null) pid = Convert.ToInt32(d["pid"], CultureInfo.InvariantCulture);
                        repo = DapJson.Str(d, "repo");
                        DateTime.TryParse(DapJson.Str(d, "createdUtc") ?? "", CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out created);
                    }
                }
                catch { }
                bool fresh = created != DateTime.MinValue && DateTime.UtcNow - created < maxAge;
                if (IsProcessAlive(pid) && fresh) { keep.Add(id); continue; }
                string dir = Path.Combine(root, id);
                try { SafeDeleteTree(dir); } catch { }
                try { File.Delete(marker); } catch { }
                if (!string.IsNullOrEmpty(repo)) repos.Add(repo);
                removed++;
            }
            // Folders without a marker (crash between mkdir and marker write).
            try
            {
                foreach (string dir in Directory.GetDirectories(root))
                {
                    string id = Path.GetFileName(dir);
                    if (keep.Contains(id) || File.Exists(MarkerPath(id))) continue;
                    DateTime mt;
                    try { mt = Directory.GetLastWriteTimeUtc(dir); } catch { mt = DateTime.MinValue; }
                    if (DateTime.UtcNow - mt < TimeSpan.FromMinutes(10)) continue;
                    try { SafeDeleteTree(dir); removed++; } catch { }
                }
            }
            catch { }
            foreach (string repo in repos)
            {
                if (!Directory.Exists(repo)) continue;
                try { RemoveCodelessWorktreeEntries(repo); } catch { }
            }
            return removed;
        }

        /// <summary>
        /// Older codeLess versions registered verification copies as linked worktrees. Removes exactly those entries (admin
        /// folders whose worktree path lies under <see cref="OverlaysRoot"/>) and nothing else - the user's own worktrees,
        /// even ones whose folder is currently missing, are never touched.
        /// </summary>
        public static int RemoveCodelessWorktreeEntries(string repo)
        {
            int n = 0;
            string common = WorkspaceIgnoreRules.ResolveGitCommonDir(repo);
            if (common == null) return 0;
            string wtDir = Path.Combine(common, "worktrees");
            if (!Directory.Exists(wtDir)) return 0;
            string overlays = TrimSep(SafeFull(OverlaysRoot));
            foreach (string admin in Directory.GetDirectories(wtDir))
            {
                try
                {
                    string gitdirFile = Path.Combine(admin, "gitdir");
                    if (!File.Exists(gitdirFile)) continue;
                    string wtGit = File.ReadAllText(gitdirFile).Trim();       // <worktree>/.git
                    string wtPath = TrimSep(SafeFull(Path.GetDirectoryName(wtGit) ?? ""));
                    if (wtPath.Length <= overlays.Length || !wtPath.StartsWith(overlays + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) &&
                        !wtPath.StartsWith(overlays + "/", StringComparison.OrdinalIgnoreCase)) continue;
                    if (Directory.Exists(wtPath)) SafeDeleteTree(wtPath);
                    SafeDeleteTree(admin);
                    n++;
                }
                catch { }
            }
            return n;
        }

        // ---------------------------------------------------------------- dependency folders

        [DllImport("libc", EntryPoint = "clonefile", SetLastError = true)]
        private static extern int clonefile_native(byte[] src, byte[] dst, uint flags);

        /// <summary>Test hook / kill switch for copy-on-write cloning.</summary>
        public static bool CopyOnWriteDisabled;

        /// <summary>
        /// Private copy-on-write clone of a directory tree: APFS clonefile(2) on macOS (one call, no data copied), reflink
        /// "cp --reflink=always" on Linux (btrfs / XFS / bcachefs). False (nothing left behind) when the file system can't.
        /// </summary>
        public static bool TryCloneDirectoryCopyOnWrite(string src, string dst)
        {
            if (CopyOnWriteDisabled || Path.DirectorySeparatorChar == '\\') return false;
            try
            {
                if (RuntimeInformationIsMac())
                {
                    if (clonefile_native(CStr(src), CStr(dst), 0x0001 /* CLONE_NOFOLLOW */) == 0) return true;
                    if (Directory.Exists(dst)) SafeDeleteTree(dst);
                    return false;
                }
                ProcessStartInfo psi = new ProcessStartInfo("cp", GitCli.JoinArgs(new string[] { "-a", "--reflink=always", "--", src, dst }));
                psi.UseShellExecute = false;
                psi.CreateNoWindow = true;
                psi.RedirectStandardOutput = true;
                psi.RedirectStandardError = true;
                using (Process p = Process.Start(psi))
                {
                    if (p == null) return false;
                    p.StandardOutput.ReadToEndAsync();
                    p.StandardError.ReadToEndAsync();
                    if (!p.WaitForExit(120000)) { LanguageServerManager.KillProcessTree(p); SafeDeleteTree(dst); return false; }
                    if (p.ExitCode == 0 && Directory.Exists(dst)) return true;
                }
            }
            catch { }
            try { if (Directory.Exists(dst)) SafeDeleteTree(dst); } catch { }
            return false;
        }

        private static bool RuntimeInformationIsMac()
        {
            return LspText.IsMacOS();
        }

        private static readonly System.Text.RegularExpressions.Regex DependencyMutationRx = new System.Text.RegularExpressions.Regex(
            @"(^|[;&|(]\s*|\s)(npm|pnpm|yarn|bun)\s+(i|install|add|remove|rm|uninstall|un|ci|update|up|upgrade|link|unlink|prune|dedupe)\b" +
            @"|\bpip[0-9.]*\s+(install|uninstall)\b|\bpython[0-9.]*\s+-m\s+pip\s+(install|uninstall)\b" +
            @"|\b(poetry|uv|pipenv)\s+(add|remove|install|sync|update|lock|pip)\b|\bpod\s+(install|update|deintegrate)\b" +
            @"|\brm\s+-[a-zA-Z]*[rR][a-zA-Z]*\s+[^;&|]*(node_modules|\.venv|venv|Pods|bower_components|\.yarn)",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant);

        /// <summary>
        /// True when <paramref name="command"/> would install / remove packages or delete a dependency folder while this
        /// copy shares such folders with the real workspace (the change would land in the user's real environment).
        /// </summary>
        public bool WouldModifySharedDependencies(string command)
        {
            if (LinkedFolders.Count == 0 || string.IsNullOrEmpty(command)) return false;
            return DependencyMutationRx.IsMatch(command);
        }

        // ---------------------------------------------------------------- file system helpers

        public static string RelativeTo(string root, string full)
        {
            string r = TrimSep(SafeFull(root));
            string f = TrimSep(SafeFull(full));
            if (string.Equals(r, f, StringComparison.OrdinalIgnoreCase)) return "";
            if (f.Length > r.Length && f.StartsWith(r, StringComparison.OrdinalIgnoreCase) && (f[r.Length] == '/' || f[r.Length] == '\\'))
                return f.Substring(r.Length + 1);
            return f;
        }

        private static string FirstLine(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            string t = s.Trim();
            int nl = t.IndexOf('\n');
            return nl >= 0 ? t.Substring(0, nl).Trim() : t;
        }

        private static void CopyFile(string src, string dst)
        {
            string dir = Path.GetDirectoryName(dst);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir)) Directory.CreateDirectory(dir);
            File.Copy(src, dst, true);
            try
            {
                FileAttributes a = File.GetAttributes(dst);
                if ((a & FileAttributes.ReadOnly) != 0) File.SetAttributes(dst, a & ~FileAttributes.ReadOnly);
            }
            catch { }
        }

        [DllImport("libc", EntryPoint = "symlink", SetLastError = true)]
        private static extern int symlink_native(byte[] target, byte[] linkpath);

        [DllImport("libc", EntryPoint = "unlink", SetLastError = true)]
        private static extern int unlink_native(byte[] path);

        private static byte[] CStr(string s)
        {
            return Encoding.UTF8.GetBytes(s + "\0");
        }

        /// <summary>Symlink (Unix) or junction (Windows, no admin rights needed) from <paramref name="link"/> to <paramref name="target"/>.</summary>
        public static bool CreateDirectoryLink(string link, string target)
        {
            try
            {
                if (Path.DirectorySeparatorChar == '\\')
                {
                    ProcessStartInfo psi = new ProcessStartInfo("cmd.exe", "/d /c mklink /J " + LanguageServerRegistry.CaretEscapeForCmd(LanguageServerRegistry.QuoteArgAlways(link)) + " " +
                        LanguageServerRegistry.CaretEscapeForCmd(LanguageServerRegistry.QuoteArgAlways(target)));
                    psi.UseShellExecute = false;
                    psi.CreateNoWindow = true;
                    psi.RedirectStandardOutput = true;
                    psi.RedirectStandardError = true;
                    using (Process p = Process.Start(psi))
                    {
                        if (p == null) return false;
                        p.StandardOutput.ReadToEnd();
                        p.WaitForExit(10000);
                    }
                    return Directory.Exists(link);
                }
                return symlink_native(CStr(target), CStr(link)) == 0;
            }
            catch { return false; }
        }

        public static bool IsLink(string path)
        {
            try
            {
                FileAttributes a = File.GetAttributes(path);
                return (a & FileAttributes.ReparsePoint) != 0;
            }
            catch { return false; }
        }

        /// <summary>Removes a link itself, never its target.</summary>
        private static void DeleteLink(string path)
        {
            if (Path.DirectorySeparatorChar == '\\')
            {
                try { Directory.Delete(path, false); return; } catch { }
                try { File.Delete(path); } catch { }
                return;
            }
            try { if (unlink_native(CStr(path)) == 0) return; } catch { }
            try { File.Delete(path); } catch { }
        }

        /// <summary>
        /// Recursive delete that removes symlinks / junctions without descending into them (Directory.Delete(true)
        /// could follow a junction on some platforms and wipe the user's node_modules) and clears read-only flags.
        /// </summary>
        public static void SafeDeleteTree(string dir)
        {
            if (string.IsNullOrEmpty(dir)) return;
            if (IsLink(dir)) { DeleteLink(dir); return; }
            if (!Directory.Exists(dir)) return;
            string[] entries;
            try { entries = Directory.GetFileSystemEntries(dir); } catch { entries = new string[0]; }
            foreach (string e in entries)
            {
                if (IsLink(e)) { DeleteLink(e); continue; }
                if (Directory.Exists(e)) { SafeDeleteTree(e); continue; }
                try
                {
                    FileAttributes a = File.GetAttributes(e);
                    if ((a & FileAttributes.ReadOnly) != 0) File.SetAttributes(e, a & ~FileAttributes.ReadOnly);
                    File.Delete(e);
                }
                catch { }
            }
            try { Directory.Delete(dir, false); } catch { }
        }

        // ---------------------------------------------------------------- git porcelain parsing

        public sealed class GitStatusEntry
        {
            public char X;
            public char Y;
            public string Path;
            public string OrigPath;
        }

        /// <summary>Parses "git status --porcelain=v1 -z" output (renames/copies carry the original path in the next field).</summary>
        public static List<GitStatusEntry> ParsePorcelainZ(string output)
        {
            List<GitStatusEntry> res = new List<GitStatusEntry>();
            if (string.IsNullOrEmpty(output)) return res;
            string[] parts = output.Split('\0');
            for (int i = 0; i < parts.Length; i++)
            {
                string p = parts[i];
                if (p.Length < 4) continue;
                GitStatusEntry e = new GitStatusEntry();
                e.X = p[0];
                e.Y = p[1];
                e.Path = p.Substring(3);
                if ((e.X == 'R' || e.X == 'C') && i + 1 < parts.Length)
                {
                    e.OrigPath = parts[i + 1];
                    i++;
                    if (e.X == 'C') e.OrigPath = null; // copy: the source still exists
                }
                res.Add(e);
            }
            return res;
        }
    }
}
