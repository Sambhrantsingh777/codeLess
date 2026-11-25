using System;
using System.Collections.Generic;

namespace codeLess.Studio
{
    /// <summary>
    /// Settings for the daily-driver parity features (git sync / auto-fetch, commit history, save actions, auto save
    /// modes). Persisted in settings.json with the rest of IdePersistentSettings on both UIs. VS Code setting names are
    /// given in the comments; defaults match VS Code so behaviour is unchanged unless the user opts in.
    /// </summary>
    public partial class IdePersistentSettings
    {
        /// <summary>git.autofetch (default false).</summary>
        public bool GitAutoFetch { get; set; }
        /// <summary>git.autofetchPeriod in seconds (default 180; 0 = default).</summary>
        public int GitAutoFetchPeriodSeconds { get; set; }
        /// <summary>git.rebaseWhenSync (default false = merge).</summary>
        public bool GitRebaseWhenSync { get; set; }
        /// <summary>Recent commit messages, newest first (scm.inputHistory).</summary>
        public List<string> GitCommitMessageHistory { get; set; }

        /// <summary>editor.formatOnSave (default false). Uses the language server's formatter when one is running.</summary>
        public bool FormatOnSave { get; set; }
        /// <summary>files.trimTrailingWhitespace (default false).</summary>
        public bool TrimTrailingWhitespaceOnSave { get; set; }
        /// <summary>files.insertFinalNewline (default false).</summary>
        public bool InsertFinalNewlineOnSave { get; set; }
        /// <summary>files.trimFinalNewlines (default false).</summary>
        public bool TrimFinalNewlinesOnSave { get; set; }
        /// <summary>files.autoSave: "off" | "afterDelay" | "onFocusChange" | "onWindowChange". Empty = derive from AutoSave.</summary>
        public string AutoSaveMode { get; set; }
        /// <summary>files.autoSaveDelay in milliseconds (default 1000; 0 = default).</summary>
        public int AutoSaveDelayMs { get; set; }
        /// <summary>files.hotExit: "off" | "onExit" (default) | "onExitAndWindowClose".</summary>
        public string HotExit { get; set; }
        /// <summary>files.eol: "\n" | "\r\n" | "auto" (default "auto" = OS newline).</summary>
        public string FilesEol { get; set; }
        /// <summary>files.autoGuessEncoding (default false).</summary>
        public bool AutoGuessEncoding { get; set; }

        public const int DefaultAutoFetchPeriodSeconds = 180;
        public const int DefaultAutoSaveDelayMs = 1000;

        public const string AutoSaveOff = "off";
        public const string AutoSaveAfterDelay = "afterDelay";
        public const string AutoSaveOnFocusChange = "onFocusChange";
        public const string AutoSaveOnWindowChange = "onWindowChange";

        public const string HotExitOff = "off";
        public const string HotExitOnExit = "onExit";
        public const string HotExitOnExitAndWindowClose = "onExitAndWindowClose";

        public string EffectiveHotExit
        {
            get
            {
                string m = (HotExit ?? "").Trim();
                if (string.Equals(m, HotExitOff, StringComparison.OrdinalIgnoreCase)) return HotExitOff;
                if (string.Equals(m, HotExitOnExitAndWindowClose, StringComparison.OrdinalIgnoreCase)) return HotExitOnExitAndWindowClose;
                return HotExitOnExit;
            }
        }

        public void SetHotExitMode(string mode)
        {
            string m = (mode ?? "").Trim();
            if (string.Equals(m, HotExitOff, StringComparison.OrdinalIgnoreCase)) HotExit = HotExitOff;
            else if (string.Equals(m, HotExitOnExitAndWindowClose, StringComparison.OrdinalIgnoreCase)) HotExit = HotExitOnExitAndWindowClose;
            else HotExit = HotExitOnExit;
        }

        public string EffectiveNewFileEol
        {
            get
            {
                string e = FilesEol;
                if (e == "\n" || string.Equals(e, "LF", StringComparison.OrdinalIgnoreCase)) return "\n";
                if (e == "\r\n" || string.Equals(e, "CRLF", StringComparison.OrdinalIgnoreCase)) return "\r\n";
                return Environment.NewLine == "\n" ? "\n" : "\r\n";
            }
        }

        /// <summary>Effective files.autoSave value (the legacy AutoSave bool maps to afterDelay).</summary>
        public string EffectiveAutoSaveMode
        {
            get
            {
                // The legacy AutoSave flag (File > Auto Save, after-delay timers) is authoritative for afterDelay.
                if (AutoSave) return AutoSaveAfterDelay;
                string m = AutoSaveMode;
                if (m == AutoSaveOnFocusChange || m == AutoSaveOnWindowChange) return m;
                return AutoSaveOff;
            }
        }

        /// <summary>Sets the mode and keeps the legacy AutoSave flag (drives the existing after-delay timers) in sync.</summary>
        public void SetAutoSaveMode(string mode)
        {
            if (mode != AutoSaveAfterDelay && mode != AutoSaveOnFocusChange && mode != AutoSaveOnWindowChange) mode = AutoSaveOff;
            AutoSaveMode = mode;
            AutoSave = mode == AutoSaveAfterDelay;
        }

        public int EffectiveAutoSaveDelayMs { get { return AutoSaveDelayMs > 0 ? Math.Max(100, AutoSaveDelayMs) : DefaultAutoSaveDelayMs; } }
        public int EffectiveAutoFetchPeriodSeconds { get { return GitAutoFetchPeriodSeconds > 0 ? Math.Max(30, GitAutoFetchPeriodSeconds) : DefaultAutoFetchPeriodSeconds; } }

        public bool FocusChangeSavesFiles
        {
            get { string m = EffectiveAutoSaveMode; return m == AutoSaveOnFocusChange || m == AutoSaveOnWindowChange; }
        }

        public void CopyWorkbenchParitySettingsFrom(IdePersistentSettings other)
        {
            if (other == null) return;
            GitAutoFetch = other.GitAutoFetch;
            GitAutoFetchPeriodSeconds = other.GitAutoFetchPeriodSeconds;
            GitRebaseWhenSync = other.GitRebaseWhenSync;
            GitCommitMessageHistory = other.GitCommitMessageHistory;
            FormatOnSave = other.FormatOnSave;
            TrimTrailingWhitespaceOnSave = other.TrimTrailingWhitespaceOnSave;
            InsertFinalNewlineOnSave = other.InsertFinalNewlineOnSave;
            TrimFinalNewlinesOnSave = other.TrimFinalNewlinesOnSave;
            AutoSaveMode = other.AutoSaveMode;
            AutoSaveDelayMs = other.AutoSaveDelayMs;
            HotExit = other.HotExit;
            FilesEol = other.FilesEol;
            AutoGuessEncoding = other.AutoGuessEncoding;
            LargeFileThresholdMB = other.LargeFileThresholdMB;
            LargeFileLineThreshold = other.LargeFileLineThreshold;
            LargeFileEditLimitMB = other.LargeFileEditLimitMB;
            // Fields from other feature areas that the Avalonia settings loader did not copy before.
            DisableInlineGitBlame = other.DisableInlineGitBlame;
            ShowInlineGitBlame = other.ShowInlineGitBlame;
            FindHistory = other.FindHistory;
            ReplaceHistory = other.ReplaceHistory;
            SearchHistory = other.SearchHistory;
            HttpClientEnvironment = other.HttpClientEnvironment;
            HttpClientTimeoutSeconds = other.HttpClientTimeoutSeconds;
        }
    }

    /// <summary>VS Code Source Control groups.</summary>
    public enum ScmGroup { Merge = 0, Staged = 1, Changes = 2, Untracked = 3 }

    /// <summary>Maps porcelain v1 XY status codes to SCM groups (shared by both UIs; C# 5).</summary>
    public static class ScmGrouping
    {
        /// <summary>Unmerged states: DD AU UD UA DU AA UU.</summary>
        public static bool IsConflict(char x, char y)
        {
            if (x == 'U' || y == 'U') return true;
            if (x == 'A' && y == 'A') return true;
            if (x == 'D' && y == 'D') return true;
            return false;
        }

        public static bool IsUntracked(char x, char y) { return x == '?' && y == '?'; }

        /// <summary>Groups an entry appears in (a file can be both staged and changed, like VS Code).</summary>
        public static List<ScmGroup> GroupsFor(char x, char y)
        {
            List<ScmGroup> g = new List<ScmGroup>();
            if (IsConflict(x, y)) { g.Add(ScmGroup.Merge); return g; }
            if (IsUntracked(x, y)) { g.Add(ScmGroup.Untracked); return g; }
            if (x != ' ' && x != '?' && x != '!') g.Add(ScmGroup.Staged);
            if (y != ' ' && y != '?' && y != '!') g.Add(ScmGroup.Changes);
            return g;
        }

        public static string GroupTitle(ScmGroup g)
        {
            switch (g)
            {
                case ScmGroup.Merge: return "MERGE CHANGES";
                case ScmGroup.Staged: return "STAGED CHANGES";
                case ScmGroup.Untracked: return "UNTRACKED CHANGES";
                default: return "CHANGES";
            }
        }

        /// <summary>Letter shown next to a file in a group (VS Code decorations: M A D R C U !).</summary>
        public static char BadgeFor(ScmGroup g, char x, char y)
        {
            if (g == ScmGroup.Merge) return '!';
            if (g == ScmGroup.Untracked) return 'U';
            char c = g == ScmGroup.Staged ? x : y;
            if (c == ' ' || c == '?') c = 'M';
            return c;
        }

        /// <summary>Parses "XY path" / "XY old -> new" porcelain v1 lines (no -z) into (x, y, path).</summary>
        public static bool TryParsePorcelainLine(string line, out char x, out char y, out string path)
        {
            x = ' '; y = ' '; path = null;
            if (string.IsNullOrEmpty(line) || line.Length < 4) return false;
            x = line[0];
            y = line[1];
            string rest = line.Substring(3);
            int arrow = rest.IndexOf(" -> ", StringComparison.Ordinal);
            if (arrow >= 0) rest = rest.Substring(arrow + 4);
            if (rest.Length > 1 && rest[0] == '"' && rest[rest.Length - 1] == '"') rest = rest.Substring(1, rest.Length - 2);
            path = rest;
            return true;
        }
    }
}
