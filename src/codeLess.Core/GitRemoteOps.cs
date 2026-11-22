using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;

namespace codeLess.Studio
{
    /// <summary>Result of one git invocation (stdout / stderr kept apart so porcelain output can be parsed safely).</summary>
    public sealed class GitCommandResult
    {
        public int ExitCode = -1;
        public string Stdout = "";
        public string Stderr = "";
        public bool TimedOut;
        public bool Cancelled;
        /// <summary>Set when the git executable could not be started.</summary>
        public string StartError;

        public bool Success { get { return ExitCode == 0 && !TimedOut && !Cancelled && StartError == null; } }

        public string Combined
        {
            get
            {
                if (StartError != null) return StartError;
                string o = (Stdout ?? "").Trim();
                string e = (Stderr ?? "").Trim();
                if (o.Length == 0) return e;
                if (e.Length == 0) return o;
                return o + "\n" + e;
            }
        }
    }

    /// <summary>
    /// Runs the system git without a shell. Uses the user's credential helpers / SSH agent as configured, never prompts:
    /// GIT_TERMINAL_PROMPT=0 and a closed stdin make git fail fast instead of hanging on a TTY prompt. Supports
    /// cancellation (kills the process), a timeout, optional stdin text and live progress lines from stderr
    /// (git --progress writes "Receiving objects: 42% (..)\r"). Synchronous: call from a background thread. C# 5.
    /// </summary>
    public static class GitProcess
    {
        public static GitCommandResult Run(string repo, params string[] args)
        {
            return Run(repo, args, 60000, CancellationToken.None, null, null);
        }

        public static GitCommandResult Run(string repo, string[] args, int timeoutMs, CancellationToken ct, string stdinText, Action<string> progress)
        {
            GitCommandResult res = new GitCommandResult();
            List<string> all = new List<string>();
            all.Add("-c");
            all.Add("core.quotepath=false");
            all.Add("-c");
            all.Add("color.ui=false");
            all.AddRange(args);
            Process p = null;
            try
            {
                ProcessStartInfo psi = new ProcessStartInfo("git", GitCli.JoinArgs(all.ToArray()));
                psi.UseShellExecute = false;
                psi.CreateNoWindow = true;
                psi.RedirectStandardOutput = true;
                psi.RedirectStandardError = true;
                psi.RedirectStandardInput = true;
                psi.StandardOutputEncoding = new UTF8Encoding(false);
                psi.StandardErrorEncoding = new UTF8Encoding(false);
                if (!string.IsNullOrEmpty(repo) && Directory.Exists(repo)) psi.WorkingDirectory = repo;
                psi.EnvironmentVariables["GIT_TERMINAL_PROMPT"] = "0";
                // English messages so failures can be classified; LC_CTYPE is left alone so hooks keep their UTF-8 locale.
                psi.EnvironmentVariables["LC_MESSAGES"] = "C";
                psi.EnvironmentVariables["LANGUAGE"] = "C";
                // Keep GUI credential helpers (Git Credential Manager, osxkeychain) working; only TTY prompts are disabled.
                p = Process.Start(psi);
                if (p == null) { res.StartError = "git could not be started."; return res; }
            }
            catch (Exception ex)
            {
                res.StartError = "git is not available: " + ex.Message;
                return res;
            }

            StringBuilder outSb = new StringBuilder();
            StringBuilder errSb = new StringBuilder();
            Process proc = p;
            Thread outThread = new Thread(delegate ()
            {
                try
                {
                    char[] buf = new char[8192];
                    int n;
                    while ((n = proc.StandardOutput.Read(buf, 0, buf.Length)) > 0) lock (outSb) outSb.Append(buf, 0, n);
                }
                catch { }
            });
            Thread errThread = new Thread(delegate ()
            {
                try
                {
                    char[] buf = new char[2048];
                    int n;
                    StringBuilder line = new StringBuilder();
                    while ((n = proc.StandardError.Read(buf, 0, buf.Length)) > 0)
                    {
                        lock (errSb) errSb.Append(buf, 0, n);
                        if (progress == null) continue;
                        for (int i = 0; i < n; i++)
                        {
                            char c = buf[i];
                            if (c == '\r' || c == '\n')
                            {
                                if (line.Length > 0) { try { progress(line.ToString()); } catch { } line.Length = 0; }
                            }
                            else line.Append(c);
                        }
                    }
                }
                catch { }
            });
            outThread.IsBackground = true;
            errThread.IsBackground = true;
            outThread.Start();
            errThread.Start();
            try
            {
                if (stdinText != null)
                {
                    byte[] bytes = new UTF8Encoding(false).GetBytes(stdinText);
                    p.StandardInput.BaseStream.Write(bytes, 0, bytes.Length);
                    p.StandardInput.BaseStream.Flush();
                }
                p.StandardInput.Close();
            }
            catch { }

            int waited = 0;
            int limit = timeoutMs <= 0 ? int.MaxValue : timeoutMs;
            while (!p.WaitForExit(100))
            {
                waited += 100;
                if (ct.IsCancellationRequested) { res.Cancelled = true; break; }
                if (waited >= limit) { res.TimedOut = true; break; }
            }
            if (res.Cancelled || res.TimedOut)
            {
                KillTree(p);
                try { p.WaitForExit(3000); } catch { }
            }
            outThread.Join(3000);
            errThread.Join(3000);
            try { if (p.HasExited) res.ExitCode = p.ExitCode; } catch { }
            if (res.Cancelled || res.TimedOut) res.ExitCode = -1;
            lock (outSb) res.Stdout = outSb.ToString();
            lock (errSb) res.Stderr = errSb.ToString();
            try { p.Dispose(); } catch { }
            return res;
        }

        public static void KillTree(Process p)
        {
            if (p == null) return;
            try
            {
#if NETCOREAPP
                p.Kill(true);
#else
                if (Path.DirectorySeparatorChar == '\\')
                {
                    // taskkill /T also ends git-remote-https / ssh children.
                    try
                    {
                        ProcessStartInfo k = new ProcessStartInfo("taskkill", "/PID " + p.Id.ToString(CultureInfo.InvariantCulture) + " /T /F");
                        k.CreateNoWindow = true;
                        k.UseShellExecute = false;
                        Process kp = Process.Start(k);
                        if (kp != null) kp.WaitForExit(3000);
                    }
                    catch { }
                }
                if (!p.HasExited) p.Kill();
#endif
            }
            catch { }
        }
    }

    /// <summary>Branch header of "git status --porcelain=v2 --branch" plus in-progress operation detection.</summary>
    public sealed class GitBranchStatus
    {
        /// <summary>Current branch name; null when HEAD is detached.</summary>
        public string Branch;
        /// <summary>Commit id of HEAD; null on an unborn branch ("(initial)").</summary>
        public string Oid;
        public bool IsDetached;
        public bool IsUnborn;
        /// <summary>Upstream ref like "origin/main"; null when the branch has no upstream.</summary>
        public string Upstream;
        public int Ahead;
        public int Behind;
        /// <summary>True when the upstream is configured but gone (branch.ab missing although branch.upstream exists).</summary>
        public bool UpstreamGone;
        /// <summary>"Merging", "Rebasing", "Cherry-picking", "Reverting" or null.</summary>
        public string Operation;

        public bool HasUpstream { get { return !string.IsNullOrEmpty(Upstream); } }

        /// <summary>Status bar label: "main", "main*"-free like VS Code; detached shows the short hash.</summary>
        public string DisplayName
        {
            get
            {
                string n = IsDetached ? (string.IsNullOrEmpty(Oid) ? "(detached)" : GitCli.ShortHash(Oid)) : (Branch ?? "");
                if (!string.IsNullOrEmpty(Operation)) n += " (" + Operation + ")";
                return n;
            }
        }

        /// <summary>"↓2 ↑1" style sync text ("" when in sync or without upstream).</summary>
        public string SyncText
        {
            get
            {
                if (!HasUpstream) return "";
                if (Ahead == 0 && Behind == 0) return "";
                StringBuilder sb = new StringBuilder();
                sb.Append(Behind.ToString(CultureInfo.InvariantCulture)).Append("↓ ");
                sb.Append(Ahead.ToString(CultureInfo.InvariantCulture)).Append('↑');
                return sb.ToString();
            }
        }

        public static GitBranchStatus Parse(string porcelainV2)
        {
            GitBranchStatus s = new GitBranchStatus();
            if (string.IsNullOrEmpty(porcelainV2)) return s;
            bool sawAb = false;
            string[] lines = porcelainV2.Replace("\r", "").Split(new char[] { '\n', '\0' });
            foreach (string raw in lines)
            {
                if (!raw.StartsWith("# branch.", StringComparison.Ordinal)) continue;
                string rest = raw.Substring(9);
                int sp = rest.IndexOf(' ');
                if (sp < 0) continue;
                string key = rest.Substring(0, sp);
                string val = rest.Substring(sp + 1).Trim();
                if (key == "oid")
                {
                    if (val == "(initial)") { s.IsUnborn = true; s.Oid = null; }
                    else s.Oid = val;
                }
                else if (key == "head")
                {
                    if (val == "(detached)") { s.IsDetached = true; s.Branch = null; }
                    else s.Branch = val;
                }
                else if (key == "upstream") s.Upstream = val;
                else if (key == "ab")
                {
                    sawAb = true;
                    string[] parts = val.Split(' ');
                    foreach (string part in parts)
                    {
                        int n;
                        if (part.Length > 1 && part[0] == '+' && int.TryParse(part.Substring(1), NumberStyles.Integer, CultureInfo.InvariantCulture, out n)) s.Ahead = n;
                        else if (part.Length > 1 && part[0] == '-' && int.TryParse(part.Substring(1), NumberStyles.Integer, CultureInfo.InvariantCulture, out n)) s.Behind = n;
                    }
                }
            }
            if (s.HasUpstream && !sawAb) s.UpstreamGone = true;
            return s;
        }

        /// <summary>Detects merge / rebase / cherry-pick / revert in progress from marker files in the git dir.</summary>
        public static string DetectOperation(string repoRoot)
        {
            string gitDir = GitCli.ResolveGitDir(repoRoot);
            if (gitDir == null) return null;
            try
            {
                if (Directory.Exists(Path.Combine(gitDir, "rebase-merge")) || Directory.Exists(Path.Combine(gitDir, "rebase-apply"))) return "Rebasing";
                if (File.Exists(Path.Combine(gitDir, "MERGE_HEAD"))) return "Merging";
                if (File.Exists(Path.Combine(gitDir, "CHERRY_PICK_HEAD"))) return "Cherry-picking";
                if (File.Exists(Path.Combine(gitDir, "REVERT_HEAD"))) return "Reverting";
            }
            catch { }
            return null;
        }
    }

    public enum GitRefKind { Local = 0, Remote = 1, Tag = 2 }

    /// <summary>One entry of the branch picker (local branch, remote-tracking branch or tag).</summary>
    public sealed class GitRefInfo
    {
        public GitRefKind Kind;
        /// <summary>"refs/heads/main", "refs/remotes/origin/dev", "refs/tags/v1".</summary>
        public string FullName = "";
        /// <summary>"main", "origin/dev", "v1".</summary>
        public string ShortName = "";
        public string Hash = "";
        public string Upstream = "";
        public bool IsHead;
        public string RelativeDate = "";
        public string Subject = "";

        /// <summary>For remote refs: "origin". Empty otherwise.</summary>
        public string RemoteName
        {
            get
            {
                if (Kind != GitRefKind.Remote) return "";
                int s = ShortName.IndexOf('/');
                return s > 0 ? ShortName.Substring(0, s) : "";
            }
        }

        /// <summary>For remote refs: the branch part ("dev" of "origin/dev"). Short name otherwise.</summary>
        public string BranchPart
        {
            get
            {
                if (Kind != GitRefKind.Remote) return ShortName;
                int s = ShortName.IndexOf('/');
                return s > 0 ? ShortName.Substring(s + 1) : ShortName;
            }
        }

        public override string ToString() { return ShortName; }
    }

    public enum GitErrorKind
    {
        None = 0,
        NonFastForward,
        AuthenticationFailed,
        NoUpstream,
        NoRemote,
        Conflicts,
        LocalChangesWouldBeOverwritten,
        DivergentBranches,
        Network,
        InvalidBranchName,
        BranchExists,
        Cancelled,
        TimedOut,
        GitMissing,
        Other
    }

    /// <summary>Outcome of a remote / branch operation with a user-facing message.</summary>
    public sealed class GitOperationResult
    {
        public bool Success;
        public GitErrorKind Error;
        /// <summary>Short message for the status bar / notification.</summary>
        public string Message = "";
        /// <summary>Raw git output (for the Output panel).</summary>
        public string Details = "";

        public static GitOperationResult Ok(string message, string details)
        {
            GitOperationResult r = new GitOperationResult();
            r.Success = true;
            r.Message = message ?? "";
            r.Details = details ?? "";
            return r;
        }
    }

    /// <summary>Classifies git failures into actionable messages (VS Code wording where it exists).</summary>
    public static class GitErrorClassifier
    {
        public const string AuthGuidance =
            "Git could not authenticate with the remote. codeLess never asks for or stores passwords: authenticate once in the integrated terminal " +
            "(for example run 'git fetch' there) so your credential helper or SSH agent remembers you, then try again.";

        public static GitErrorKind Classify(GitCommandResult r)
        {
            if (r == null) return GitErrorKind.Other;
            if (r.Cancelled) return GitErrorKind.Cancelled;
            if (r.TimedOut) return GitErrorKind.TimedOut;
            if (r.StartError != null) return GitErrorKind.GitMissing;
            if (r.ExitCode == 0) return GitErrorKind.None;
            return ClassifyText(r.Combined);
        }

        public static GitErrorKind ClassifyText(string text)
        {
            string t = text ?? "";
            if (Has(t, "Authentication failed") || Has(t, "could not read Username") || Has(t, "could not read Password") ||
                Has(t, "terminal prompts disabled") || Has(t, "Permission denied (publickey") || Has(t, "Host key verification failed") ||
                Has(t, "HTTP Basic: Access denied") || Has(t, "Invalid username or password") || Has(t, "The requested URL returned error: 403") ||
                Has(t, "The requested URL returned error: 401") || Has(t, "SSO") && Has(t, "authoriz") || Has(t, "Permission to ") && Has(t, " denied"))
                return GitErrorKind.AuthenticationFailed;
            if ((Has(t, "[rejected]") || Has(t, "Updates were rejected")) && (Has(t, "non-fast-forward") || Has(t, "fetch first") || Has(t, "behind its remote")))
                return GitErrorKind.NonFastForward;
            if (Has(t, "has no upstream branch") || Has(t, "There is no tracking information for the current branch") || Has(t, "no upstream configured"))
                return GitErrorKind.NoUpstream;
            if (Has(t, "No configured push destination") || Has(t, "No remote repository specified") || Has(t, "does not appear to be a git repository"))
                return GitErrorKind.NoRemote;
            if (Has(t, "would be overwritten by") || Has(t, "Please commit your changes or stash them") || Has(t, "You have unstaged changes") ||
                Has(t, "Your index contains uncommitted changes") || Has(t, "cannot pull with rebase"))
                return GitErrorKind.LocalChangesWouldBeOverwritten;
            if (Has(t, "CONFLICT (") || Has(t, "Automatic merge failed") || Has(t, "could not apply") || Has(t, "Resolve all conflicts") || Has(t, "unmerged files"))
                return GitErrorKind.Conflicts;
            if (Has(t, "divergent branches") || Has(t, "Need to specify how to reconcile"))
                return GitErrorKind.DivergentBranches;
            if (Has(t, "is not a valid branch name") || Has(t, "not a valid ref name"))
                return GitErrorKind.InvalidBranchName;
            if (Has(t, "already exists"))
                return GitErrorKind.BranchExists;
            if (Has(t, "Could not resolve host") || Has(t, "Connection timed out") || Has(t, "Failed to connect") || Has(t, "Connection refused") ||
                Has(t, "Network is unreachable") || Has(t, "unable to access"))
                return GitErrorKind.Network;
            return GitErrorKind.Other;
        }

        private static bool Has(string t, string s) { return t.IndexOf(s, StringComparison.OrdinalIgnoreCase) >= 0; }

        public static string Describe(GitErrorKind kind, string operation, string rawOutput)
        {
            string op = operation ?? "Git";
            switch (kind)
            {
                case GitErrorKind.NonFastForward:
                    return "Can't push refs to remote. Try running 'Pull' first to integrate your changes.";
                case GitErrorKind.AuthenticationFailed:
                    return AuthGuidance;
                case GitErrorKind.NoUpstream:
                    return "The current branch has no upstream branch. Use 'Publish Branch' to push it and set the upstream.";
                case GitErrorKind.NoRemote:
                    return "This repository has no remotes configured. Add one with 'git remote add origin <url>'.";
                case GitErrorKind.Conflicts:
                    return op + " stopped because of merge conflicts. Resolve them (Accept Current / Incoming / Both), stage the files and continue.";
                case GitErrorKind.LocalChangesWouldBeOverwritten:
                    return "Your local changes would be overwritten. Commit or stash them before running " + op + ".";
                case GitErrorKind.DivergentBranches:
                    return "Your branch and its upstream have diverged. Choose merge or rebase for Sync in Settings (git.rebaseWhenSync) and try again.";
                case GitErrorKind.Network:
                    return op + " failed: the remote could not be reached (" + FirstLine(rawOutput) + ").";
                case GitErrorKind.InvalidBranchName:
                    return "That is not a valid branch name.";
                case GitErrorKind.BranchExists:
                    return "A branch with that name already exists.";
                case GitErrorKind.Cancelled:
                    return op + " cancelled.";
                case GitErrorKind.TimedOut:
                    return op + " timed out.";
                case GitErrorKind.GitMissing:
                    return GitCli.GitMissingHint;
            }
            string first = FirstLine(rawOutput);
            return op + " failed" + (first.Length > 0 ? ": " + first : ".");
        }

        public static string FirstLine(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            foreach (string l in s.Replace("\r", "\n").Split('\n'))
            {
                string t = l.Trim();
                if (t.Length == 0) continue;
                if (t.StartsWith("hint:", StringComparison.OrdinalIgnoreCase)) continue;
                return t.Length > 220 ? t.Substring(0, 220) + "…" : t;
            }
            return "";
        }

        public static GitOperationResult ToResult(GitCommandResult r, string operation, string successMessage)
        {
            GitOperationResult res = new GitOperationResult();
            res.Details = r != null ? r.Combined : "";
            res.Error = Classify(r);
            res.Success = res.Error == GitErrorKind.None;
            res.Message = res.Success ? (successMessage ?? (operation + " completed.")) : Describe(res.Error, operation, res.Details);
            return res;
        }
    }

    /// <summary>
    /// Branch and remote operations behind the status bar branch picker and the Sync / Fetch / Pull / Push / Publish
    /// commands (shared by both UIs). Every method is synchronous; UIs run them on a background thread with a
    /// CancellationToken wired to a Cancel button.
    /// </summary>
    public static class GitRemoteOps
    {
        public const int NetworkTimeoutMs = 10 * 60 * 1000;
        public const int LocalTimeoutMs = 60 * 1000;

        public static GitBranchStatus GetBranchStatus(string repo)
        {
            GitCommandResult r = GitProcess.Run(repo, new string[] { "--no-optional-locks", "status", "--porcelain=v2", "--branch", "--untracked-files=no", "--ignore-submodules=all" }, 20000, CancellationToken.None, null, null);
            GitBranchStatus s = GitBranchStatus.Parse(r.Success ? r.Stdout : "");
            s.Operation = GitBranchStatus.DetectOperation(repo);
            return s;
        }

        public const string RefFormat = "%(refname)%00%(objectname:short)%00%(upstream:short)%00%(HEAD)%00%(committerdate:relative)%00%(contents:subject)";

        public static List<GitRefInfo> ParseRefs(string forEachRefOutput)
        {
            List<GitRefInfo> list = new List<GitRefInfo>();
            if (string.IsNullOrEmpty(forEachRefOutput)) return list;
            foreach (string raw in forEachRefOutput.Replace("\r", "").Split('\n'))
            {
                if (raw.Length == 0) continue;
                string[] f = raw.Split('\0');
                if (f.Length < 2) continue;
                string full = f[0];
                GitRefInfo info = new GitRefInfo();
                info.FullName = full;
                if (full.StartsWith("refs/heads/", StringComparison.Ordinal)) { info.Kind = GitRefKind.Local; info.ShortName = full.Substring(11); }
                else if (full.StartsWith("refs/remotes/", StringComparison.Ordinal))
                {
                    info.Kind = GitRefKind.Remote;
                    info.ShortName = full.Substring(13);
                    if (info.ShortName.EndsWith("/HEAD", StringComparison.Ordinal)) continue; // origin/HEAD symref
                }
                else if (full.StartsWith("refs/tags/", StringComparison.Ordinal)) { info.Kind = GitRefKind.Tag; info.ShortName = full.Substring(10); }
                else continue;
                info.Hash = f.Length > 1 ? f[1] : "";
                info.Upstream = f.Length > 2 ? f[2] : "";
                info.IsHead = f.Length > 3 && f[3].Trim() == "*";
                info.RelativeDate = f.Length > 4 ? f[4] : "";
                info.Subject = f.Length > 5 ? f[5] : "";
                list.Add(info);
            }
            return list;
        }

        /// <summary>Local branches, remote branches and tags, most recently committed first.</summary>
        public static List<GitRefInfo> ListRefs(string repo, bool includeTags)
        {
            List<string> args = new List<string>();
            args.Add("for-each-ref");
            args.Add("--sort=-committerdate");
            args.Add("--format=" + RefFormat);
            args.Add("refs/heads");
            args.Add("refs/remotes");
            if (includeTags) args.Add("refs/tags");
            GitCommandResult r = GitProcess.Run(repo, args.ToArray(), 20000, CancellationToken.None, null, null);
            return r.Success ? ParseRefs(r.Stdout) : new List<GitRefInfo>();
        }

        public static List<string> ListRemotes(string repo)
        {
            List<string> list = new List<string>();
            GitCommandResult r = GitProcess.Run(repo, new string[] { "remote" }, 10000, CancellationToken.None, null, null);
            if (!r.Success) return list;
            foreach (string l in r.Stdout.Replace("\r", "").Split('\n'))
            {
                string t = l.Trim();
                if (t.Length > 0) list.Add(t);
            }
            return list;
        }

        /// <summary>The remote Publish uses: "origin" when present, otherwise the only remote, otherwise null (ask).</summary>
        public static string PickPublishRemote(List<string> remotes)
        {
            if (remotes == null || remotes.Count == 0) return null;
            foreach (string r in remotes) if (r == "origin") return r;
            return remotes.Count == 1 ? remotes[0] : null;
        }

        /// <summary>Validates a branch name with git's own rules (git check-ref-format --branch).</summary>
        public static bool IsValidBranchName(string repo, string name)
        {
            if (string.IsNullOrWhiteSpace(name) || name.IndexOf(' ') >= 0 || name.StartsWith("-", StringComparison.Ordinal)) return false;
            GitCommandResult r = GitProcess.Run(repo, new string[] { "check-ref-format", "--branch", name }, 10000, CancellationToken.None, null, null);
            return r.Success;
        }

        /// <summary>Normalizes user input into a branch name the way VS Code does (spaces become dashes).</summary>
        public static string SanitizeBranchName(string input)
        {
            string s = (input ?? "").Trim();
            StringBuilder sb = new StringBuilder();
            foreach (char c in s)
            {
                if (char.IsWhiteSpace(c)) sb.Append('-');
                else if (c == '~' || c == '^' || c == ':' || c == '?' || c == '*' || c == '[' || c == '\\') sb.Append('-');
                else sb.Append(c);
            }
            return sb.ToString();
        }

        /// <summary>Checks out a local branch, creates a tracking branch for a remote one, or detaches at a tag.</summary>
        public static GitOperationResult Checkout(string repo, GitRefInfo target, List<GitRefInfo> allRefs, CancellationToken ct)
        {
            if (target == null) return GitErrorClassifier.ToResult(new GitCommandResult { ExitCode = 1, Stderr = "Nothing selected." }, "Checkout", null);
            GitCommandResult r;
            string label = target.ShortName;
            if (target.Kind == GitRefKind.Local)
            {
                r = GitProcess.Run(repo, new string[] { "checkout", "-q", target.ShortName, "--" }, LocalTimeoutMs, ct, null, null);
            }
            else if (target.Kind == GitRefKind.Remote)
            {
                string local = target.BranchPart;
                bool exists = false;
                if (allRefs != null)
                    foreach (GitRefInfo g in allRefs)
                        if (g.Kind == GitRefKind.Local && g.ShortName == local) { exists = true; break; }
                if (exists)
                    r = GitProcess.Run(repo, new string[] { "checkout", "-q", local, "--" }, LocalTimeoutMs, ct, null, null);
                else
                    r = GitProcess.Run(repo, new string[] { "checkout", "-q", "-b", local, "--track", target.ShortName }, LocalTimeoutMs, ct, null, null);
                label = local;
            }
            else
            {
                r = GitProcess.Run(repo, new string[] { "checkout", "-q", "--detach", target.ShortName, "--" }, LocalTimeoutMs, ct, null, null);
            }
            return GitErrorClassifier.ToResult(r, "Checkout", "Switched to " + label + ".");
        }

        /// <summary>Creates a branch from HEAD (or <paramref name="startPoint"/>) and checks it out.</summary>
        public static GitOperationResult CreateBranch(string repo, string name, string startPoint, CancellationToken ct)
        {
            string clean = SanitizeBranchName(name);
            if (!IsValidBranchName(repo, clean))
            {
                GitOperationResult bad = new GitOperationResult();
                bad.Error = GitErrorKind.InvalidBranchName;
                bad.Message = "'" + clean + "' is not a valid branch name.";
                return bad;
            }
            List<string> args = new List<string>();
            args.Add("checkout");
            args.Add("-q");
            args.Add("-b");
            args.Add(clean);
            if (!string.IsNullOrEmpty(startPoint)) args.Add(startPoint);
            GitCommandResult r = GitProcess.Run(repo, args.ToArray(), LocalTimeoutMs, ct, null, null);
            return GitErrorClassifier.ToResult(r, "Create Branch", "Created and switched to " + clean + ".");
        }

        // ---- Network ledger: git talks to remotes, so every remote operation is checked (Offline Mode) and recorded. ----

        /// <summary>URL of a remote ("" when unknown).</summary>
        public static string RemoteUrl(string repo, string remote)
        {
            if (string.IsNullOrEmpty(remote)) return "";
            GitCommandResult r = GitProcess.Run(repo, new string[] { "remote", "get-url", remote }, 10000, CancellationToken.None, null, null);
            return r.Success ? r.Stdout.Trim() : "";
        }

        /// <summary>The remote the current branch tracks, else origin / the only remote.</summary>
        public static string DefaultRemote(string repo)
        {
            GitBranchStatus st = GetBranchStatus(repo);
            if (!string.IsNullOrEmpty(st.Branch))
            {
                GitCommandResult r = GitProcess.Run(repo, new string[] { "config", "--get", "branch." + st.Branch + ".remote" }, 10000, CancellationToken.None, null, null);
                if (r.Success && r.Stdout.Trim().Length > 0 && r.Stdout.Trim() != ".") return r.Stdout.Trim();
            }
            return PickPublishRemote(ListRemotes(repo));
        }

        /// <summary>True for remotes on the local file system (paths, file:// URLs): no network involved.</summary>
        public static bool IsLocalRemoteUrl(string url)
        {
            if (string.IsNullOrEmpty(url)) return true;
            string u = url.Trim();
            if (u.StartsWith("file://", StringComparison.OrdinalIgnoreCase)) return true;
            if (u.StartsWith("/", StringComparison.Ordinal) || u.StartsWith(".", StringComparison.Ordinal) || u.StartsWith("\\", StringComparison.Ordinal) || u.StartsWith("~", StringComparison.Ordinal)) return true;
            if (u.Length > 2 && char.IsLetter(u[0]) && u[1] == ':' && (u[2] == '\\' || u[2] == '/')) return true;
            if (u.IndexOf("://", StringComparison.Ordinal) >= 0 || u.StartsWith("ext::", StringComparison.Ordinal)) return false;
            int colon = u.IndexOf(':');
            int slash = u.IndexOf('/');
            if (colon > 0 && (slash < 0 || colon < slash)) return false; // scp-like git@host:path
            return true;
        }

        /// <summary>URL for the ledger: credentials in the user-info part are removed, then the usual redaction applies.</summary>
        public static string LedgerUrl(string url)
        {
            string u = (url ?? "").Trim();
            int scheme = u.IndexOf("://", StringComparison.Ordinal);
            if (scheme > 0)
            {
                int at = u.IndexOf('@', scheme + 3);
                int slash = u.IndexOf('/', scheme + 3);
                if (at > 0 && (slash < 0 || at < slash)) u = u.Substring(0, scheme + 3) + u.Substring(at + 1);
                return NetworkLedger.RedactUrl(u);
            }
            int colon = u.IndexOf(':');
            if (colon > 0) return "ssh://" + u.Substring(0, colon) + "/" + u.Substring(colon + 1); // git@host:path
            return u;
        }

        private static GitOperationResult Guarded(string repo, string remote, bool allRemotes, string op, Func<GitOperationResult> body)
        {
            List<string> names = new List<string>();
            if (allRemotes) names.AddRange(ListRemotes(repo));
            else
            {
                string n = string.IsNullOrEmpty(remote) ? DefaultRemote(repo) : remote;
                if (!string.IsNullOrEmpty(n)) names.Add(n);
            }
            List<string> urls = new List<string>();
            foreach (string n in names)
            {
                string url = RemoteUrl(repo, n);
                if (!IsLocalRemoteUrl(url)) urls.Add(LedgerUrl(url));
            }
            foreach (string u in urls)
            {
                string reason;
                if (!NetworkLedger.Check("Git", "GIT", u, out reason))
                {
                    GitOperationResult blocked = new GitOperationResult();
                    blocked.Error = GitErrorKind.Network;
                    blocked.Message = op + ": " + reason;
                    return blocked;
                }
            }
            Stopwatch sw = Stopwatch.StartNew();
            GitOperationResult res = body();
            foreach (string u in urls)
                NetworkLedger.Record("Git", "GIT", u, 0, 0, res.Success ? 200 : 0, "git " + op.ToLowerInvariant() + (res.Success ? "" : " failed: " + res.Error), sw.ElapsedMilliseconds);
            return res;
        }

        public static GitOperationResult Fetch(string repo, bool allRemotes, bool prune, CancellationToken ct, Action<string> progress)
        {
            return Guarded(repo, null, allRemotes, "Fetch", delegate { return FetchCore(repo, allRemotes, prune, ct, progress); });
        }

        private static GitOperationResult FetchCore(string repo, bool allRemotes, bool prune, CancellationToken ct, Action<string> progress)
        {
            List<string> args = new List<string>();
            args.Add("fetch");
            args.Add("--progress");
            if (allRemotes) args.Add("--all");
            if (prune) args.Add("--prune");
            GitCommandResult r = GitProcess.Run(repo, args.ToArray(), NetworkTimeoutMs, ct, null, progress);
            return GitErrorClassifier.ToResult(r, "Fetch", "Fetched.");
        }

        /// <summary>True when the user configured pull.rebase themselves (then plain "git pull" honours it).</summary>
        public static bool HasPullRebaseConfig(string repo)
        {
            GitCommandResult r = GitProcess.Run(repo, new string[] { "config", "--get", "pull.rebase" }, 10000, CancellationToken.None, null, null);
            return r.Success && r.Stdout.Trim().Length > 0;
        }

        public static string[] BuildPullArgs(bool rebase, bool userConfiguredPullRebase)
        {
            List<string> args = new List<string>();
            args.Add("pull");
            args.Add("--progress");
            if (!userConfiguredPullRebase) args.Add(rebase ? "--rebase" : "--no-rebase");
            return args.ToArray();
        }

        public static GitOperationResult Pull(string repo, bool rebase, CancellationToken ct, Action<string> progress)
        {
            return Guarded(repo, null, false, "Pull", delegate { return PullCore(repo, rebase, ct, progress); });
        }

        private static GitOperationResult PullCore(string repo, bool rebase, CancellationToken ct, Action<string> progress)
        {
            GitCommandResult r = GitProcess.Run(repo, BuildPullArgs(rebase, HasPullRebaseConfig(repo)), NetworkTimeoutMs, ct, null, progress);
            return GitErrorClassifier.ToResult(r, "Pull", "Pulled.");
        }

        public static GitOperationResult Push(string repo, CancellationToken ct, Action<string> progress)
        {
            return Guarded(repo, null, false, "Push", delegate { return PushCore(repo, ct, progress); });
        }

        private static GitOperationResult PushCore(string repo, CancellationToken ct, Action<string> progress)
        {
            GitBranchStatus st = GetBranchStatus(repo);
            if (st.IsDetached)
            {
                GitOperationResult d = new GitOperationResult();
                d.Error = GitErrorKind.Other;
                d.Message = "Can't push: HEAD is detached. Create a branch first.";
                return d;
            }
            if (!st.HasUpstream)
            {
                GitOperationResult nu = new GitOperationResult();
                nu.Error = GitErrorKind.NoUpstream;
                nu.Message = GitErrorClassifier.Describe(GitErrorKind.NoUpstream, "Push", "");
                return nu;
            }
            GitCommandResult r = GitProcess.Run(repo, new string[] { "push", "--progress" }, NetworkTimeoutMs, ct, null, progress);
            return GitErrorClassifier.ToResult(r, "Push", "Pushed.");
        }

        /// <summary>git push -u &lt;remote&gt; &lt;branch&gt; (VS Code "Publish Branch").</summary>
        public static GitOperationResult Publish(string repo, string remote, CancellationToken ct, Action<string> progress)
        {
            string target = string.IsNullOrEmpty(remote) ? PickPublishRemote(ListRemotes(repo)) : remote;
            return Guarded(repo, target, false, "Publish", delegate { return PublishCore(repo, target, ct, progress); });
        }

        private static GitOperationResult PublishCore(string repo, string remote, CancellationToken ct, Action<string> progress)
        {
            GitBranchStatus st = GetBranchStatus(repo);
            if (st.IsDetached || string.IsNullOrEmpty(st.Branch))
            {
                GitOperationResult d = new GitOperationResult();
                d.Error = GitErrorKind.Other;
                d.Message = "Can't publish: HEAD is detached. Create a branch first.";
                return d;
            }
            if (string.IsNullOrEmpty(remote)) remote = PickPublishRemote(ListRemotes(repo));
            if (string.IsNullOrEmpty(remote))
            {
                GitOperationResult nr = new GitOperationResult();
                nr.Error = GitErrorKind.NoRemote;
                nr.Message = GitErrorClassifier.Describe(GitErrorKind.NoRemote, "Publish", "");
                return nr;
            }
            GitCommandResult r = GitProcess.Run(repo, new string[] { "push", "--progress", "-u", remote, st.Branch }, NetworkTimeoutMs, ct, null, progress);
            return GitErrorClassifier.ToResult(r, "Publish Branch", "Published " + st.Branch + " to " + remote + ".");
        }

        /// <summary>
        /// VS Code "Sync Changes": pull (rebase or merge per git.rebaseWhenSync) and then push when there are outgoing
        /// commits. A branch without upstream is published instead.
        /// </summary>
        public static GitOperationResult Sync(string repo, bool rebase, CancellationToken ct, Action<string> progress)
        {
            GitBranchStatus st0 = GetBranchStatus(repo);
            if (!st0.IsDetached && !st0.HasUpstream) return Publish(repo, null, ct, progress);
            return Guarded(repo, null, false, "Sync", delegate { return SyncCore(repo, rebase, ct, progress); });
        }

        private static GitOperationResult SyncCore(string repo, bool rebase, CancellationToken ct, Action<string> progress)
        {
            GitBranchStatus st = GetBranchStatus(repo);
            if (st.IsDetached)
            {
                GitOperationResult d = new GitOperationResult();
                d.Error = GitErrorKind.Other;
                d.Message = "Can't sync: HEAD is detached.";
                return d;
            }
            if (!st.HasUpstream) return PublishCore(repo, PickPublishRemote(ListRemotes(repo)), ct, progress);
            StringBuilder details = new StringBuilder();
            GitCommandResult pull = GitProcess.Run(repo, BuildPullArgs(rebase, false), NetworkTimeoutMs, ct, null, progress);
            details.AppendLine(pull.Combined);
            if (!pull.Success)
            {
                GitOperationResult pr = GitErrorClassifier.ToResult(pull, "Sync", null);
                pr.Details = details.ToString();
                return pr;
            }
            GitBranchStatus after = GetBranchStatus(repo);
            if (after.Ahead > 0)
            {
                GitCommandResult push = GitProcess.Run(repo, new string[] { "push", "--progress" }, NetworkTimeoutMs, ct, null, progress);
                details.AppendLine(push.Combined);
                if (!push.Success)
                {
                    GitOperationResult ps = GitErrorClassifier.ToResult(push, "Sync", null);
                    ps.Details = details.ToString();
                    return ps;
                }
            }
            return GitOperationResult.Ok("Synced with " + (after.Upstream ?? "upstream") + ".", details.ToString());
        }

        /// <summary>Last commit message (for "Commit (Amend)" prefill). "" on an unborn branch.</summary>
        public static string GetLastCommitMessage(string repo)
        {
            GitCommandResult r = GitProcess.Run(repo, new string[] { "log", "-1", "--format=%B" }, 10000, CancellationToken.None, null, null);
            return r.Success ? r.Stdout.TrimEnd('\r', '\n', ' ') : "";
        }

        /// <summary>git commit [--amend] -F file. An empty message with amend keeps the previous message (--no-edit).</summary>
        public static GitOperationResult Commit(string repo, string message, bool amend, bool stageAllFirst, CancellationToken ct)
        {
            if (stageAllFirst)
            {
                GitCommandResult add = GitProcess.Run(repo, new string[] { "add", "-A" }, LocalTimeoutMs, ct, null, null);
                if (!add.Success) return GitErrorClassifier.ToResult(add, "Commit", null);
            }
            List<string> args = new List<string>();
            args.Add("commit");
            if (amend) args.Add("--amend");
            string msgFile = null;
            if (string.IsNullOrEmpty(message))
            {
                if (!amend)
                {
                    GitOperationResult e = new GitOperationResult();
                    e.Error = GitErrorKind.Other;
                    e.Message = "Please enter a commit message.";
                    return e;
                }
                args.Add("--no-edit");
            }
            else
            {
                msgFile = Path.Combine(Path.GetTempPath(), "codeless-commit-" + Guid.NewGuid().ToString("N") + ".txt");
                File.WriteAllText(msgFile, message + "\n", new UTF8Encoding(false));
                args.Add("-F");
                args.Add(msgFile);
            }
            // Generous timeout: pre-commit hooks and GPG signing must not be killed half way (index.lock).
            GitCommandResult r = GitProcess.Run(repo, args.ToArray(), 5 * 60 * 1000, ct, null, null);
            if (msgFile != null) { try { File.Delete(msgFile); } catch { } }
            return GitErrorClassifier.ToResult(r, amend ? "Commit (Amend)" : "Commit", amend ? "Amended the last commit." : "Committed.");
        }
    }

    /// <summary>Commit message history for the SCM input (Up / Down like VS Code's scm.inputHistory).</summary>
    public sealed class CommitMessageHistory
    {
        public const int MaxEntries = 30;
        private readonly List<string> entries;
        private int index = -1;
        private string draft = "";

        /// <summary>Wraps the persisted list (newest first). The list instance is modified in place.</summary>
        public CommitMessageHistory(List<string> persisted)
        {
            entries = persisted ?? new List<string>();
        }

        public List<string> Entries { get { return entries; } }

        public static void Add(List<string> list, string message)
        {
            if (list == null) return;
            string m = (message ?? "").Trim();
            if (m.Length == 0) return;
            list.RemoveAll(delegate (string x) { return string.Equals(x, m, StringComparison.Ordinal); });
            list.Insert(0, m);
            while (list.Count > MaxEntries) list.RemoveAt(list.Count - 1);
        }

        public void Add(string message) { Add(entries, message); Reset(); }

        public void Reset() { index = -1; draft = ""; }

        /// <summary>Older message (Up). Returns null when there is nothing older.</summary>
        public string Previous(string currentText)
        {
            if (entries.Count == 0) return null;
            if (index == -1) draft = currentText ?? "";
            if (index + 1 >= entries.Count) return null;
            index++;
            return entries[index];
        }

        /// <summary>Newer message (Down); returns the draft when leaving the history. Null when already at the draft.</summary>
        public string Next()
        {
            if (index < 0) return null;
            index--;
            return index < 0 ? draft : entries[index];
        }

        public bool IsBrowsing { get { return index >= 0; } }
    }
}
