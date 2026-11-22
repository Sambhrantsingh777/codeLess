using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace codeLess.Studio
{
    /// <summary>
    /// Small, platform-neutral git command-line helpers shared by the Windows and Avalonia UIs (C# 5 only).
    /// All methods are synchronous and must be called from a background thread by the UI layers.
    /// </summary>
    public static class GitCli
    {
        private static readonly object availabilityGate = new object();
        private static int availability = -1; // -1 unknown, 0 missing, 1 present

        /// <summary>Quotes one argument so ProcessStartInfo.Arguments round-trips it on Windows, macOS and Linux.</summary>
        public static string QuoteArg(string arg)
        {
            if (arg == null) return "\"\"";
            if (arg.Length > 0 && arg.IndexOfAny(new char[] { ' ', '\t', '"', '\n', '\r', '\'' }) < 0) return arg;
            StringBuilder sb = new StringBuilder();
            sb.Append('"');
            int backslashes = 0;
            for (int i = 0; i < arg.Length; i++)
            {
                char c = arg[i];
                if (c == '\\') { backslashes++; continue; }
                if (c == '"')
                {
                    sb.Append('\\', backslashes * 2 + 1);
                    sb.Append('"');
                }
                else
                {
                    sb.Append('\\', backslashes);
                    sb.Append(c);
                }
                backslashes = 0;
            }
            sb.Append('\\', backslashes * 2);
            sb.Append('"');
            return sb.ToString();
        }

        public static string JoinArgs(string[] args)
        {
            StringBuilder sb = new StringBuilder();
            for (int i = 0; i < args.Length; i++)
            {
                if (i > 0) sb.Append(' ');
                sb.Append(QuoteArg(args[i]));
            }
            return sb.ToString();
        }

        /// <summary>Runs git with the given arguments in <paramref name="repo"/> and returns raw stdout ("" on failure).</summary>
        public static string Run(string repo, int timeoutMs, out int exitCode, params string[] args)
        {
            if (GitSafety.BlockReason(repo, args) != null) { exitCode = -1; return ""; } // Restricted Mode
            List<string> all = new List<string>();
            all.Add("--no-optional-locks");
            all.Add("-c");
            all.Add("core.quotepath=false");
            all.Add("-c");
            all.Add("color.ui=false");
            all.AddRange(args);
            return LlmHarnessAndAgentEngine.RunProcessStdoutRaw("git", JoinArgs(all.ToArray()), repo, timeoutMs, out exitCode);
        }

        /// <summary>True when a git executable can be started. Cached after the first probe.</summary>
        public static bool IsAvailable()
        {
            lock (availabilityGate)
            {
                if (availability >= 0) return availability == 1;
            }
            int exit;
            string outp = LlmHarnessAndAgentEngine.RunProcessStdoutRaw("git", "--version", null, 8000, out exit);
            bool ok = exit == 0 && outp != null && outp.IndexOf("git", StringComparison.OrdinalIgnoreCase) >= 0;
            lock (availabilityGate) availability = ok ? 1 : 0;
            return ok;
        }

        public const string GitMissingHint = "git was not found on PATH. Install Git (https://git-scm.com) to use blame, history and the git graph.";

        /// <summary>Walks up from a file or folder to the folder containing ".git" (directory or worktree file).</summary>
        public static string FindRepoRoot(string path)
        {
            if (string.IsNullOrEmpty(path)) return null;
            try
            {
                string cur = Directory.Exists(path) ? Path.GetFullPath(path) : Path.GetDirectoryName(Path.GetFullPath(path));
                for (int depth = 0; depth < 128 && !string.IsNullOrEmpty(cur); depth++)
                {
                    string dotGit = Path.Combine(cur, ".git");
                    if (Directory.Exists(dotGit) || File.Exists(dotGit)) return cur;
                    DirectoryInfo parent = Directory.GetParent(cur);
                    cur = parent != null ? parent.FullName : null;
                }
            }
            catch { }
            return null;
        }

        /// <summary>Repository-relative path with forward slashes, or null when the file is outside the repo.</summary>
        public static string RepoRelativePath(string repoRoot, string filePath)
        {
            if (string.IsNullOrEmpty(repoRoot) || string.IsNullOrEmpty(filePath)) return null;
            try
            {
                string root = Path.GetFullPath(repoRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                string full = Path.GetFullPath(filePath);
                if (!full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) return null;
                return full.Substring(root.Length + 1).Replace('\\', '/');
            }
            catch { return null; }
        }

        /// <summary>Resolves the git directory (handles "gitdir:" files used by worktrees and submodules).</summary>
        public static string ResolveGitDir(string repoRoot)
        {
            try
            {
                string dotGit = Path.Combine(repoRoot, ".git");
                if (Directory.Exists(dotGit)) return dotGit;
                if (File.Exists(dotGit))
                {
                    string txt = File.ReadAllText(dotGit).Trim();
                    if (txt.StartsWith("gitdir:", StringComparison.OrdinalIgnoreCase))
                    {
                        string target = txt.Substring(7).Trim();
                        return Path.IsPathRooted(target) ? target : Path.GetFullPath(Path.Combine(repoRoot, target));
                    }
                }
            }
            catch { }
            return null;
        }

        /// <summary>
        /// Cheap signature of HEAD (no process): HEAD contents + the commit the current branch points to. Changes after
        /// commit / checkout / reset / pull, which is what invalidates cached blame and history.
        /// </summary>
        public static string ReadHeadSignature(string repoRoot)
        {
            try
            {
                string gitDir = ResolveGitDir(repoRoot);
                if (gitDir == null) return "";
                string headPath = Path.Combine(gitDir, "HEAD");
                if (!File.Exists(headPath)) return "";
                string head = File.ReadAllText(headPath).Trim();
                if (!head.StartsWith("ref:", StringComparison.Ordinal)) return head;
                string refName = head.Substring(4).Trim();
                string commonDir = gitDir;
                string commonFile = Path.Combine(gitDir, "commondir");
                if (File.Exists(commonFile))
                {
                    string c = File.ReadAllText(commonFile).Trim();
                    commonDir = Path.IsPathRooted(c) ? c : Path.GetFullPath(Path.Combine(gitDir, c));
                }
                string[] candidates = new string[] { Path.Combine(gitDir, refName.Replace('/', Path.DirectorySeparatorChar)), Path.Combine(commonDir, refName.Replace('/', Path.DirectorySeparatorChar)) };
                foreach (string cand in candidates)
                {
                    if (File.Exists(cand)) return head + "@" + File.ReadAllText(cand).Trim();
                }
                string packed = Path.Combine(commonDir, "packed-refs");
                if (File.Exists(packed))
                {
                    foreach (string line in File.ReadAllLines(packed))
                    {
                        if (line.EndsWith(" " + refName, StringComparison.Ordinal)) return head + "@" + line.Substring(0, line.IndexOf(' '));
                    }
                }
                return head;
            }
            catch { return ""; }
        }

        public static string ShortHash(string hash)
        {
            if (string.IsNullOrEmpty(hash)) return "";
            return hash.Length > 8 ? hash.Substring(0, 8) : hash;
        }

        public static bool IsZeroHash(string hash)
        {
            if (string.IsNullOrEmpty(hash)) return false;
            for (int i = 0; i < hash.Length; i++) if (hash[i] != '0') return false;
            return true;
        }

        /// <summary>"just now", "5 minutes ago", "3 days ago", "2 years ago" (GitLens style).</summary>
        public static string FormatRelativeTime(DateTime utcTime, DateTime utcNow)
        {
            TimeSpan d = utcNow - utcTime;
            if (d.TotalSeconds < 0) d = TimeSpan.Zero;
            if (d.TotalSeconds < 45) return "just now";
            if (d.TotalMinutes < 1.5) return "1 minute ago";
            if (d.TotalMinutes < 60) return ((int)Math.Round(d.TotalMinutes)).ToString(CultureInfo.InvariantCulture) + " minutes ago";
            if (d.TotalHours < 1.5) return "1 hour ago";
            if (d.TotalHours < 24) return ((int)Math.Round(d.TotalHours)).ToString(CultureInfo.InvariantCulture) + " hours ago";
            if (d.TotalDays < 2) return "yesterday";
            if (d.TotalDays < 7) return ((int)Math.Floor(d.TotalDays)).ToString(CultureInfo.InvariantCulture) + " days ago";
            if (d.TotalDays < 14) return "1 week ago";
            if (d.TotalDays < 30) return ((int)Math.Floor(d.TotalDays / 7)).ToString(CultureInfo.InvariantCulture) + " weeks ago";
            if (d.TotalDays < 45) return "1 month ago";
            if (d.TotalDays < 365) return ((int)Math.Max(2, Math.Round(d.TotalDays / 30.4))).ToString(CultureInfo.InvariantCulture) + " months ago";
            if (d.TotalDays < 545) return "1 year ago";
            return ((int)Math.Max(2, Math.Round(d.TotalDays / 365.25))).ToString(CultureInfo.InvariantCulture) + " years ago";
        }

        public static DateTime FromUnixSeconds(long seconds)
        {
            return new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddSeconds(seconds);
        }

        private static readonly Dictionary<string, string> userNameCache = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        /// <summary>git config user.name for the repository (cached; "" when unset).</summary>
        public static string GetUserName(string repoRoot)
        {
            if (string.IsNullOrEmpty(repoRoot)) return "";
            lock (userNameCache)
            {
                string cached;
                if (userNameCache.TryGetValue(repoRoot, out cached)) return cached;
            }
            int exit;
            string name = (Run(repoRoot, 5000, out exit, "config", "user.name") ?? "").Trim();
            if (exit != 0) name = "";
            lock (userNameCache) userNameCache[repoRoot] = name;
            return name;
        }

        /// <summary>Decodes "git show rev:path" output; returns null for binary content.</summary>
        public static string ShowFileAtRevision(string repoRoot, string revision, string repoRelPath, out bool isBinary)
        {
            isBinary = false;
            if (string.IsNullOrEmpty(revision) || string.IsNullOrEmpty(repoRelPath)) return "";
            int exit;
            string outp = Run(repoRoot, 15000, out exit, "show", revision + ":" + repoRelPath);
            if (exit != 0) return null;
            if (outp.IndexOf('\0') >= 0) { isBinary = true; return null; }
            return outp.Replace("\r\n", "\n").Replace("\r", "\n");
        }
    }
}
