using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Web.Script.Serialization;

namespace codeLess.Studio
{
    /// <summary>Per-folder Workspace Trust decision.</summary>
    public enum WorkspaceTrustDecision
    {
        /// <summary>Never asked: Restricted Mode until the user decides.</summary>
        None,
        /// <summary>The folder (or a parent folder) is trusted: every feature is enabled.</summary>
        Trusted,
        /// <summary>The user chose Restricted Mode for this folder (don't ask again).</summary>
        Restricted
    }

    /// <summary>
    /// VS Code-style Workspace Trust. A folder is trusted when it, or one of its parent folders, is in the trusted list.
    /// In Restricted Mode (anything not trusted) codeLess never runs code from the workspace on its own: no workspace-local
    /// language servers / interpreters (node_modules/.bin, venvs), no language servers that execute project code by design
    /// (rust-analyzer build scripts, jdtls Gradle/Maven import, MSBuild for C#), no tasks, no debugging, no agent
    /// verification commands, no venv auto-activation in new terminals and no git (repository config such as
    /// core.fsmonitor / hooks / filters runs commands). Global language servers from PATH still run.
    /// Decisions persist in workspace-trust.json next to settings.json. Thread-safe; strict C# 5.
    /// </summary>
    public static class WorkspaceTrust
    {
        private static readonly object gate = new object();
        private static List<string> trusted = new List<string>();      // display paths
        private static List<string> restricted = new List<string>();
        private static bool enabled = true;
        private static bool loaded;
        private static string loadedFrom;
        private static DateTime loadedStamp = DateTime.MinValue;
        private static DateTime lastStatUtc = DateTime.MinValue;
        private static string storePathOverride;

        /// <summary>Raised (on the calling thread) after any trust change, including changes made by another codeLess process.</summary>
        public static event Action TrustChanged;

        public const string StoreFileName = "workspace-trust.json";
        public const string LearnMoreUrl = "https://code.visualstudio.com/docs/editor/workspace-trust";

        /// <summary>Tests / isolated profiles: where decisions are stored (null = next to settings.json).</summary>
        public static string StorePathOverride
        {
            get { return storePathOverride; }
            set
            {
                lock (gate)
                {
                    storePathOverride = value;
                    loaded = false;
                    trusted = new List<string>();
                    restricted = new List<string>();
                    enabled = true;
                }
            }
        }

        public static string StorePath
        {
            get
            {
                string o = storePathOverride;
                if (!string.IsNullOrEmpty(o)) return o;
                try { return Path.Combine(Path.GetDirectoryName(IdePersistentSettings.GetSettingsFilePath()), StoreFileName); }
                catch { return Path.Combine(Path.GetTempPath(), "codeLess-" + StoreFileName); }
            }
        }

        /// <summary>security.workspace.trust.enabled: when false every folder is trusted (no prompt, no Restricted Mode).</summary>
        public static bool Enabled
        {
            get { EnsureLoaded(); lock (gate) { return enabled; } }
            set
            {
                EnsureLoaded();
                lock (gate)
                {
                    if (enabled == value) return;
                    enabled = value;
                    SaveLocked();
                }
                Raise();
            }
        }

        // ------------------------------------------------------------------ queries

        /// <summary>
        /// True when codeLess may run code from <paramref name="path"/> (a workspace root, repository, project root or
        /// file): the path is inside a trusted folder, or trust is disabled. An empty path (nothing workspace-specific,
        /// e.g. "git --version") is trusted.
        /// </summary>
        public static bool IsTrusted(string path)
        {
            if (string.IsNullOrEmpty(path)) return true;
            EnsureLoaded();
            string key = Key(path);
            lock (gate)
            {
                if (!enabled) return true;
                foreach (string t in trusted) if (IsSameOrUnder(key, Key(t))) return true;
            }
            return false;
        }

        /// <summary>Restricted Mode is active for this folder (not trusted).</summary>
        public static bool IsRestricted(string path)
        {
            return !string.IsNullOrEmpty(path) && !IsTrusted(path);
        }

        /// <summary>Trusted (directly or via a parent), Restricted (the user said "don't trust") or None (never asked).</summary>
        public static WorkspaceTrustDecision GetDecision(string folder)
        {
            if (string.IsNullOrEmpty(folder)) return WorkspaceTrustDecision.Trusted;
            if (IsTrusted(folder)) return WorkspaceTrustDecision.Trusted;
            string key = Key(folder);
            lock (gate)
            {
                foreach (string r in restricted) if (string.Equals(key, Key(r), StringComparison.Ordinal)) return WorkspaceTrustDecision.Restricted;
            }
            return WorkspaceTrustDecision.None;
        }

        /// <summary>The first-open prompt must be shown for this folder (trust enabled, no decision yet).</summary>
        public static bool NeedsPrompt(string folder)
        {
            return !string.IsNullOrEmpty(folder) && Directory.Exists(folder) && GetDecision(folder) == WorkspaceTrustDecision.None;
        }

        /// <summary>The trusted folder that covers <paramref name="path"/> (itself or a parent), or null.</summary>
        public static string TrustedRootFor(string path)
        {
            if (string.IsNullOrEmpty(path)) return null;
            EnsureLoaded();
            string key = Key(path);
            lock (gate)
            {
                foreach (string t in trusted) if (IsSameOrUnder(key, Key(t))) return t;
            }
            return null;
        }

        public static List<string> GetTrustedFolders()
        {
            EnsureLoaded();
            lock (gate) { return new List<string>(trusted); }
        }

        public static List<string> GetRestrictedFolders()
        {
            EnsureLoaded();
            lock (gate) { return new List<string>(restricted); }
        }

        // ------------------------------------------------------------------ changes

        /// <summary>Trusts <paramref name="folder"/> (and everything below it). Features are enabled at once via <see cref="TrustChanged"/>.</summary>
        public static void Trust(string folder)
        {
            if (string.IsNullOrEmpty(folder)) return;
            EnsureLoaded();
            string full = Display(folder);
            string key = Key(full);
            lock (gate)
            {
                bool covered = false;
                foreach (string t in trusted) if (IsSameOrUnder(key, Key(t))) { covered = true; break; }
                if (!covered)
                {
                    trusted.RemoveAll(delegate (string t) { return IsSameOrUnder(Key(t), key); }); // children are now redundant
                    trusted.Add(full);
                }
                restricted.RemoveAll(delegate (string r) { return IsSameOrUnder(Key(r), key); });
                SaveLocked();
            }
            Raise();
        }

        /// <summary>Remembers "Don't Trust": the folder stays in Restricted Mode and the prompt isn't shown again.</summary>
        public static void Restrict(string folder)
        {
            if (string.IsNullOrEmpty(folder)) return;
            EnsureLoaded();
            string full = Display(folder);
            string key = Key(full);
            lock (gate)
            {
                trusted.RemoveAll(delegate (string t) { return string.Equals(Key(t), key, StringComparison.Ordinal); });
                bool known = false;
                foreach (string r in restricted) if (string.Equals(Key(r), key, StringComparison.Ordinal)) { known = true; break; }
                if (!known) restricted.Add(full);
                SaveLocked();
            }
            Raise();
        }

        /// <summary>Removes a trusted (or restricted) entry; the folder will be asked about again next time it is opened.</summary>
        public static void Revoke(string folder)
        {
            if (string.IsNullOrEmpty(folder)) return;
            EnsureLoaded();
            string key = Key(folder);
            lock (gate)
            {
                trusted.RemoveAll(delegate (string t) { return string.Equals(Key(t), key, StringComparison.Ordinal); });
                restricted.RemoveAll(delegate (string r) { return string.Equals(Key(r), key, StringComparison.Ordinal); });
                SaveLocked();
            }
            Raise();
        }

        /// <summary>Applies the first-open prompt's answer.</summary>
        public static void ApplyPromptAnswer(string folder, bool trust, bool trustParent)
        {
            if (!trust) { Restrict(folder); return; }
            string target = folder;
            if (trustParent)
            {
                try
                {
                    string parent = Path.GetDirectoryName(Path.GetFullPath(folder).TrimEnd('/', '\\'));
                    if (!string.IsNullOrEmpty(parent) && !IsFileSystemRoot(parent)) target = parent;
                }
                catch { }
            }
            Trust(target);
        }

        /// <summary>The parent folder offered by "Trust the authors of all files in the parent folder" (null for a drive / file system root).</summary>
        public static string ParentFolderForPrompt(string folder)
        {
            try
            {
                string parent = Path.GetDirectoryName(Path.GetFullPath(folder).TrimEnd('/', '\\'));
                if (string.IsNullOrEmpty(parent) || IsFileSystemRoot(parent)) return null;
                return parent;
            }
            catch { return null; }
        }

        private static bool IsFileSystemRoot(string dir)
        {
            try
            {
                string full = Path.GetFullPath(dir);
                string root = Path.GetPathRoot(full);
                if (string.Equals(full.TrimEnd('/', '\\'), (root ?? "").TrimEnd('/', '\\'), StringComparison.OrdinalIgnoreCase)) return true;
                string home = LanguageServerRegistry.HomeDir();
                // Trusting the home folder (or /Users, /home) would trust everything the user ever downloads.
                if (!string.IsNullOrEmpty(home) && string.Equals(Key(full), Key(home), StringComparison.Ordinal)) return true;
                string homeParent = string.IsNullOrEmpty(home) ? null : Path.GetDirectoryName(home.TrimEnd('/', '\\'));
                if (!string.IsNullOrEmpty(homeParent) && string.Equals(Key(full), Key(homeParent), StringComparison.Ordinal)) return true;
            }
            catch { }
            return false;
        }

        // ------------------------------------------------------------------ messages

        /// <summary>"Tasks are disabled in Restricted Mode. Trust this folder to enable them (Manage Workspace Trust)."</summary>
        public static string RestrictedMessage(string featurePlural)
        {
            return featurePlural + " " + (featurePlural.EndsWith("s", StringComparison.Ordinal) && !featurePlural.EndsWith("ss", StringComparison.Ordinal) ? "are" : "is") +
                   " disabled in Restricted Mode. Trust this folder to enable " + (featurePlural.EndsWith("s", StringComparison.Ordinal) ? "them" : "it") +
                   " (Manage Workspace Trust).";
        }

        public const string PromptTitle = "Do you trust the authors of the files in this folder?";
        public const string PromptBody =
            "codeLess provides features that may automatically execute files in this folder: language servers and interpreters " +
            "installed in the project (node_modules, virtual environments), tasks, debugging, verification commands and git. " +
            "If you don't trust the authors of these files, continue in Restricted Mode: you can browse and edit code safely, " +
            "and global language servers still work.";
        public const string BannerText =
            "Restricted Mode is intended for safe code browsing. Trust this folder to enable all features.";

        // ------------------------------------------------------------------ persistence

        private static void EnsureLoaded()
        {
            string path = StorePath;
            DateTime now = DateTime.UtcNow;
            lock (gate)
            {
                bool samePath = string.Equals(loadedFrom, path, StringComparison.Ordinal);
                if (loaded && samePath && (now - lastStatUtc).TotalMilliseconds < 1500) return;
                lastStatUtc = now;
                DateTime stamp = DateTime.MinValue;
                try { if (File.Exists(path)) stamp = File.GetLastWriteTimeUtc(path); } catch { }
                if (loaded && samePath && stamp == loadedStamp) return;
                bool hadData = loaded && samePath;
                List<string> oldTrusted = trusted;
                bool oldEnabled = enabled;
                LoadLocked(path);
                loaded = true;
                loadedFrom = path;
                loadedStamp = stamp;
                if (!hadData) return;
                // Another process (second window) changed the decisions: let the UI refresh.
                if (oldEnabled == enabled && SameList(oldTrusted, trusted)) return;
            }
            Raise();
        }

        private static bool SameList(List<string> a, List<string> b)
        {
            if (a.Count != b.Count) return false;
            for (int i = 0; i < a.Count; i++) if (!string.Equals(a[i], b[i], StringComparison.Ordinal)) return false;
            return true;
        }

        private static void LoadLocked(string path)
        {
            trusted = new List<string>();
            restricted = new List<string>();
            enabled = true;
            try
            {
                if (!File.Exists(path)) return;
                Dictionary<string, object> root = new JavaScriptSerializer().DeserializeObject(File.ReadAllText(path)) as Dictionary<string, object>;
                if (root == null) return;
                object en;
                if (root.TryGetValue("enabled", out en) && en is bool) enabled = (bool)en;
                trusted = ReadList(root, "trusted");
                restricted = ReadList(root, "restricted");
            }
            catch { }
        }

        private static List<string> ReadList(Dictionary<string, object> root, string name)
        {
            List<string> list = new List<string>();
            object v;
            if (!root.TryGetValue(name, out v) || v == null) return list;
            System.Collections.IEnumerable e = v as System.Collections.IEnumerable;
            if (e == null || v is string) return list;
            foreach (object o in e)
            {
                string s = o as string;
                if (!string.IsNullOrEmpty(s)) list.Add(s);
            }
            return list;
        }

        private static void SaveLocked()
        {
            string path = StorePath;
            try
            {
                Dictionary<string, object> root = new Dictionary<string, object>();
                root["enabled"] = enabled;
                root["trusted"] = trusted.ToArray();
                root["restricted"] = restricted.ToArray();
                string json = new JavaScriptSerializer().Serialize(root);
                string dir = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                string tmp = path + ".tmp";
                File.WriteAllText(tmp, json, new UTF8Encoding(false));
                if (File.Exists(path))
                {
                    try { File.Replace(tmp, path, null); }
                    catch { File.Copy(tmp, path, true); try { File.Delete(tmp); } catch { } }
                }
                else File.Move(tmp, path);
                loadedFrom = path;
                try { loadedStamp = File.GetLastWriteTimeUtc(path); } catch { }
                lastStatUtc = DateTime.UtcNow;
                loaded = true;
            }
            catch { }
        }

        private static void Raise()
        {
            Action h = TrustChanged;
            if (h == null) return;
            foreach (Delegate d in h.GetInvocationList())
            {
                try { ((Action)d)(); } catch { }
            }
        }

        // ------------------------------------------------------------------ paths

        private static string Display(string path)
        {
            try { return Path.GetFullPath(path).TrimEnd('/', '\\'); } catch { return path; }
        }

        /// <summary>Comparison key: full path, '/' separators, case-folded on Windows / macOS, /private aliases folded.</summary>
        private static string Key(string path)
        {
            return LspText.PathKey(path);
        }

        private static bool IsSameOrUnder(string key, string parentKey)
        {
            if (string.IsNullOrEmpty(parentKey)) return false;
            if (string.Equals(key, parentKey, StringComparison.Ordinal)) return true;
            string p = parentKey.EndsWith("/", StringComparison.Ordinal) ? parentKey : parentKey + "/";
            return key.StartsWith(p, StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// Environment / gate applied to every git child process: Offline Mode keeps git-lfs from downloading objects on
    /// checkout, and Restricted Mode doesn't run git in an untrusted repository at all (its .git/config can name commands -
    /// core.fsmonitor, hooks, filter drivers - that git would execute). Strict C# 5.
    /// </summary>
    public static class GitSafety
    {
        public static void Apply(ProcessStartInfo psi, string repo)
        {
            if (psi == null) return;
            try
            {
                if (NetworkLedger.OfflineMode) psi.EnvironmentVariables["GIT_LFS_SKIP_SMUDGE"] = "1";
            }
            catch { }
        }

        /// <summary>
        /// Null when git may run in <paramref name="workingDir"/>; else why not (Restricted Mode). Commands that don't read a
        /// repository's configuration (--version, clone into a new folder, init) are always allowed.
        /// </summary>
        public static string BlockReason(string workingDir, string args)
        {
            if (string.IsNullOrEmpty(workingDir)) return null;
            string a = (args ?? "").TrimStart();
            if (a.StartsWith("--version", StringComparison.Ordinal) || a.StartsWith("version", StringComparison.Ordinal) ||
                a.StartsWith("clone ", StringComparison.Ordinal) || a.StartsWith("init", StringComparison.Ordinal))
                return null;
            if (WorkspaceTrust.IsTrusted(workingDir)) return null;
            // codeLess's own verification copies (private clones) are only ever created from trusted workspaces.
            try
            {
                string overlays = LspText.PathKey(VerificationOverlay.OverlaysRoot) + "/";
                if (LspText.PathKey(workingDir).StartsWith(overlays, StringComparison.Ordinal)) return null;
            }
            catch { }
            return WorkspaceTrust.RestrictedMessage("Git");
        }

        public static string BlockReason(string workingDir, string[] args)
        {
            return BlockReason(workingDir, args != null ? string.Join(" ", args) : "");
        }

        /// <summary>True for "git" / "git.exe" / a path ending in git(.exe).</summary>
        public static bool IsGit(string fileName)
        {
            if (string.IsNullOrEmpty(fileName)) return false;
            string n = Path.GetFileName(fileName).ToLowerInvariant();
            return n == "git" || n == "git.exe";
        }
    }
}
