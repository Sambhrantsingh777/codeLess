using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;

namespace codeLess.Studio
{
    /// <summary>Shell dialect used to build the activation command.</summary>
    public enum TerminalShellFlavor
    {
        Unknown,
        Posix,       // bash, zsh, sh, dash, ksh, Git Bash
        Fish,
        Csh,         // csh, tcsh
        PowerShell,  // powershell.exe, pwsh
        Cmd,
        Wsl,
        Python       // interactive Python REPL profile: never activated
    }

    /// <summary>
    /// Automatic Python virtual-environment activation for new integrated terminals, like VS Code's Python extension
    /// ("python.terminal.activateEnvironment"). Detects a venv (a directory containing pyvenv.cfg named .venv, venv,
    /// env or .env) in the terminal's folder or one of its parents up to the workspace root, and types the matching
    /// activate command into the shell once its first prompt is up. Shared by the WinForms and Avalonia UIs, so it is
    /// strictly C# 5 (compiled by the legacy csc for the Windows build).
    /// </summary>
    public static class PythonVenvActivator
    {
        /// <summary>Candidate folder names in VS Code's order of preference.</summary>
        public static readonly string[] CandidateFolderNames = new string[] { ".venv", "venv", "env", ".env" };

        /// <summary>Returns the full path of the first venv directly inside <paramref name="folder"/>, or null.</summary>
        public static string FindVenvInFolder(string folder)
        {
            if (string.IsNullOrEmpty(folder)) return null;
            try
            {
                if (!Directory.Exists(folder)) return null;
                foreach (string name in CandidateFolderNames)
                {
                    string dir = Path.Combine(folder, name);
                    if (Directory.Exists(dir) && File.Exists(Path.Combine(dir, "pyvenv.cfg")))
                        return Path.GetFullPath(dir);
                }
            }
            catch { }
            return null;
        }

        /// <summary>
        /// Finds the venv for a terminal starting in <paramref name="terminalCwd"/>: the nearest venv walking up from the
        /// cwd to <paramref name="workspaceRoot"/>. Returns null when there is no workspace, or the cwd is outside it.
        /// </summary>
        public static string FindWorkspaceVenv(string workspaceRoot, string terminalCwd)
        {
            if (string.IsNullOrEmpty(workspaceRoot)) return null;
            string root;
            try { root = Path.GetFullPath(workspaceRoot).TrimEnd('/', '\\'); }
            catch { return null; }
            if (root.Length == 0) root = workspaceRoot;

            string cwd = string.IsNullOrEmpty(terminalCwd) ? root : terminalCwd;
            try { cwd = Path.GetFullPath(cwd).TrimEnd('/', '\\'); }
            catch { return null; }
            if (!IsSameOrUnder(cwd, root)) return null;

            string cur = cwd;
            for (int guard = 0; guard < 64 && !string.IsNullOrEmpty(cur); guard++)
            {
                string found = FindVenvInFolder(cur);
                if (found != null) return found;
                if (PathsEqual(cur, root)) break;
                string parent;
                try { parent = Path.GetDirectoryName(cur); }
                catch { break; }
                if (string.IsNullOrEmpty(parent) || PathsEqual(parent, cur)) break;
                cur = parent.TrimEnd('/', '\\');
            }
            return null;
        }

        private static bool IsWindowsHost()
        {
            return Path.DirectorySeparatorChar == '\\';
        }

        private static StringComparison PathComparison
        {
            get { return IsWindowsHost() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal; }
        }

        public static bool PathsEqual(string a, string b)
        {
            if (a == null || b == null) return false;
            return string.Equals(a.TrimEnd('/', '\\'), b.TrimEnd('/', '\\'), PathComparison);
        }

        private static bool IsSameOrUnder(string path, string root)
        {
            if (PathsEqual(path, root)) return true;
            string r = root.TrimEnd('/', '\\');
            return path.StartsWith(r + "/", PathComparison) || path.StartsWith(r + "\\", PathComparison);
        }

        /// <summary>True when the inherited VIRTUAL_ENV already points at <paramref name="venvDir"/>.</summary>
        public static bool IsAlreadyActive(string venvDir, string inheritedVirtualEnv)
        {
            if (string.IsNullOrEmpty(venvDir) || string.IsNullOrEmpty(inheritedVirtualEnv)) return false;
            try
            {
                return PathsEqual(Path.GetFullPath(venvDir), Path.GetFullPath(inheritedVirtualEnv));
            }
            catch
            {
                return PathsEqual(venvDir, inheritedVirtualEnv);
            }
        }

        /// <summary>Classifies the shell from its executable (preferred) or the profile name / command line.</summary>
        public static TerminalShellFlavor DetectShellFlavor(string shellExecutableOrCommandLine, string shellName)
        {
            string exe = FirstToken(shellExecutableOrCommandLine);
            string leaf = "";
            try { leaf = Path.GetFileNameWithoutExtension(exe.Replace('\\', '/').Substring(exe.Replace('\\', '/').LastIndexOf('/') + 1)).ToLowerInvariant(); }
            catch { leaf = (exe ?? "").ToLowerInvariant(); }
            TerminalShellFlavor f = FlavorFromLeaf(leaf);
            if (f != TerminalShellFlavor.Unknown) return f;
            string low = ((shellName ?? "") + " " + (shellExecutableOrCommandLine ?? "")).ToLowerInvariant();
            if (low.Contains("python")) return TerminalShellFlavor.Python;
            if (low.Contains("wsl")) return TerminalShellFlavor.Wsl;
            if (low.Contains("pwsh") || low.Contains("powershell")) return TerminalShellFlavor.PowerShell;
            if (low.Contains("fish")) return TerminalShellFlavor.Fish;
            if (low.Contains("tcsh") || low.Contains("csh")) return TerminalShellFlavor.Csh;
            if (low.Contains("bash") || low.Contains("zsh") || low.Contains("git")) return TerminalShellFlavor.Posix;
            if (low.Contains("cmd") || low.Contains("command prompt")) return TerminalShellFlavor.Cmd;
            return TerminalShellFlavor.Unknown;
        }

        private static TerminalShellFlavor FlavorFromLeaf(string leaf)
        {
            if (string.IsNullOrEmpty(leaf)) return TerminalShellFlavor.Unknown;
            if (leaf.StartsWith("python") || leaf == "py" || leaf == "ipython") return TerminalShellFlavor.Python;
            if (leaf == "wsl" || leaf == "ubuntu" || leaf == "debian") return TerminalShellFlavor.Wsl;
            if (leaf == "pwsh" || leaf == "pwsh-preview" || leaf == "powershell") return TerminalShellFlavor.PowerShell;
            if (leaf == "cmd") return TerminalShellFlavor.Cmd;
            if (leaf == "fish") return TerminalShellFlavor.Fish;
            if (leaf == "csh" || leaf == "tcsh") return TerminalShellFlavor.Csh;
            if (leaf == "bash" || leaf == "zsh" || leaf == "sh" || leaf == "dash" || leaf == "ksh" || leaf == "mksh" || leaf == "git-bash")
                return TerminalShellFlavor.Posix;
            return TerminalShellFlavor.Unknown;
        }

        private static string FirstToken(string commandLine)
        {
            if (string.IsNullOrEmpty(commandLine)) return "";
            string s = commandLine.Trim();
            if (s.StartsWith("\""))
            {
                int close = s.IndexOf('"', 1);
                return close > 1 ? s.Substring(1, close - 1) : s.Trim('"');
            }
            // Unquoted Windows paths with spaces ("C:\Program Files\...\bash.exe --login") are not split at the space
            // when the text up to an .exe is a path.
            int exeIdx = s.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
            if (exeIdx > 0 && (exeIdx + 4 == s.Length || s[exeIdx + 4] == ' ')) return s.Substring(0, exeIdx + 4);
            int sp = s.IndexOf(' ');
            return sp > 0 ? s.Substring(0, sp) : s;
        }

        private static bool IsSafeUnquotedPosix(string s)
        {
            foreach (char c in s)
            {
                if (!(char.IsLetterOrDigit(c) || "/._-+@%:,=".IndexOf(c) >= 0)) return false;
            }
            return s.Length > 0;
        }

        /// <summary>Quotes a path for bash/zsh/sh/fish/csh: unquoted when safe (like VS Code), else single-quoted.</summary>
        public static string QuotePosix(string path)
        {
            if (IsSafeUnquotedPosix(path)) return path;
            return "'" + path.Replace("'", "'\\''") + "'";
        }

        private static string QuotePowerShell(string path)
        {
            return "'" + path.Replace("'", "''") + "'";
        }

        /// <summary>
        /// Builds the command that activates <paramref name="venvDir"/> in a shell of the given flavor, or null when the
        /// flavor is not supported or the venv has no matching activate script. Leading space keeps the line out of
        /// bash (HISTCONTROL=ignorespace) / fish / zsh (HIST_IGNORE_SPACE) history.
        /// </summary>
        public static string BuildActivationCommand(string venvDir, TerminalShellFlavor flavor, bool windowsHost)
        {
            if (string.IsNullOrEmpty(venvDir)) return null;
            string bin = Path.Combine(venvDir, "bin");
            string scripts = Path.Combine(venvDir, "Scripts");
            switch (flavor)
            {
                case TerminalShellFlavor.Posix:
                    {
                        string script = FirstExisting(Path.Combine(bin, "activate"), Path.Combine(scripts, "activate"));
                        if (script == null) return null;
                        string p = windowsHost ? ConPtyTerminalSession.ConvertWindowsPathToPosix(script, false) : script;
                        return " source " + QuotePosix(p);
                    }
                case TerminalShellFlavor.Wsl:
                    {
                        // A venv created by Windows Python (Scripts\) cannot run inside WSL; only a Linux venv (bin/) can.
                        string script = FirstExisting(Path.Combine(bin, "activate"));
                        if (script == null) return null;
                        string p = windowsHost ? ConPtyTerminalSession.ConvertWindowsPathToPosix(script, true) : script;
                        return " source " + QuotePosix(p);
                    }
                case TerminalShellFlavor.Fish:
                    {
                        string script = FirstExisting(Path.Combine(bin, "activate.fish"), Path.Combine(scripts, "activate.fish"));
                        if (script == null) return null;
                        string p = windowsHost ? ConPtyTerminalSession.ConvertWindowsPathToPosix(script, false) : script;
                        return " source " + QuotePosix(p);
                    }
                case TerminalShellFlavor.Csh:
                    {
                        string script = FirstExisting(Path.Combine(bin, "activate.csh"), Path.Combine(scripts, "activate.csh"));
                        if (script == null) return null;
                        string p = windowsHost ? ConPtyTerminalSession.ConvertWindowsPathToPosix(script, false) : script;
                        return " source " + QuotePosix(p);
                    }
                case TerminalShellFlavor.PowerShell:
                    {
                        string script = windowsHost
                            ? FirstExisting(Path.Combine(scripts, "Activate.ps1"), Path.Combine(bin, "Activate.ps1"))
                            : FirstExisting(Path.Combine(bin, "Activate.ps1"), Path.Combine(scripts, "Activate.ps1"));
                        if (script == null) return null;
                        return "& " + QuotePowerShell(script);
                    }
                case TerminalShellFlavor.Cmd:
                    {
                        string script = FirstExisting(Path.Combine(scripts, "activate.bat"));
                        if (script == null) return null;
                        return "\"" + script.Replace("\"", "") + "\"";
                    }
                default:
                    return null;
            }
        }

        private static string FirstExisting(params string[] paths)
        {
            foreach (string p in paths)
            {
                try { if (File.Exists(p)) return p; }
                catch { }
            }
            return null;
        }

        /// <summary>
        /// Full decision for a new terminal: returns the activation command, or null when nothing should be typed
        /// (no workspace, cwd outside it, no venv, already active via inherited VIRTUAL_ENV, unsupported shell).
        /// </summary>
        public static string ResolveActivationCommand(string workspaceRoot, string terminalCwd, string shellExecutableOrCommandLine,
            string shellName, string inheritedVirtualEnv, bool windowsHost)
        {
            TerminalShellFlavor flavor = DetectShellFlavor(shellExecutableOrCommandLine, shellName);
            if (flavor == TerminalShellFlavor.Unknown || flavor == TerminalShellFlavor.Python) return null;
            string selectedEnv = ResolveSelectedEnvironment(workspaceRoot);
            if (selectedEnv != null)
            {
                if (IsAlreadyActive(selectedEnv, inheritedVirtualEnv)) return null;
                if (IsCondaEnvironment(selectedEnv)) return BuildCondaActivationCommand(selectedEnv, flavor);
                return BuildActivationCommand(selectedEnv, flavor, windowsHost);
            }
            string venv = FindWorkspaceVenv(workspaceRoot, terminalCwd);
            if (venv == null) return null;
            if (IsAlreadyActive(venv, inheritedVirtualEnv)) return null;
            return BuildActivationCommand(venv, flavor, windowsHost);
        }

        /// <summary>
        /// Set by PythonInterpreterService: workspace root -> root of the user's selected interpreter environment
        /// (venv or conda prefix), or null to fall back to venv auto-detection.
        /// </summary>
        public static Func<string, string> SelectedEnvironmentOverride;

        private static string ResolveSelectedEnvironment(string workspaceRoot)
        {
            Func<string, string> f = SelectedEnvironmentOverride;
            if (f == null || string.IsNullOrEmpty(workspaceRoot)) return null;
            try
            {
                string env = f(workspaceRoot);
                return !string.IsNullOrEmpty(env) && Directory.Exists(env) ? env : null;
            }
            catch { return null; }
        }

        public static bool IsCondaEnvironment(string envDir)
        {
            try { return !File.Exists(Path.Combine(envDir, "pyvenv.cfg")) && Directory.Exists(Path.Combine(envDir, "conda-meta")); }
            catch { return false; }
        }

        /// <summary>"conda activate &lt;prefix&gt;" for shells where conda's shell hook is usually initialized.</summary>
        public static string BuildCondaActivationCommand(string prefix, TerminalShellFlavor flavor)
        {
            switch (flavor)
            {
                case TerminalShellFlavor.Posix:
                case TerminalShellFlavor.Fish:
                case TerminalShellFlavor.Csh:
                    return " conda activate " + QuotePosix(prefix);
                case TerminalShellFlavor.PowerShell:
                    return "conda activate " + QuotePowerShell(prefix);
                case TerminalShellFlavor.Cmd:
                    return "conda activate \"" + prefix.Replace("\"", "") + "\"";
                default:
                    return null;
            }
        }

        private static readonly object pendingLock = new object();
        private static readonly Dictionary<string, string> pendingBySessionId = new Dictionary<string, string>();

        /// <summary>
        /// Claims a scheduled-but-not-yet-sent activation for <paramref name="session"/> (null if none). Used when
        /// a command is about to be typed into a brand-new terminal (e.g. Run Python File): the caller sends the
        /// activation first so the command runs inside the venv, instead of the activation being dropped.
        /// </summary>
        public static string TakePendingActivation(ConPtyTerminalSession session)
        {
            if (session == null) return null;
            lock (pendingLock)
            {
                string cmd;
                if (!pendingBySessionId.TryGetValue(session.Id, out cmd)) return null;
                pendingBySessionId.Remove(session.Id);
                return cmd;
            }
        }

        /// <summary>
        /// Types the venv activation into a freshly started interactive session once its first prompt is up. Returns
        /// the scheduled command (null if nothing to do). The command is dropped if anything else is typed into the
        /// session first (user keystrokes, a Run command, the agent), so it can never interleave with other input;
        /// automation that wants it first claims it with <see cref="TakePendingActivation"/>.
        /// </summary>
        public static string ScheduleActivation(ConPtyTerminalSession session, string workspaceRoot)
        {
            if (session == null || !session.IsRunning) return null;
            // Restricted Mode: "source .venv/bin/activate" runs a script from the untrusted workspace - never automatically.
            if (WorkspaceTrust.IsRestricted(workspaceRoot)) return null;
            string shell = !string.IsNullOrEmpty(session.ShellExecutable) ? session.ShellExecutable : session.CommandLine;
            string cmd;
            try
            {
                cmd = ResolveActivationCommand(workspaceRoot, session.WorkingDirectory, shell, session.ShellName,
                    Environment.GetEnvironmentVariable("VIRTUAL_ENV"), ConPtyNative.IsWindowsPlatform());
            }
            catch { return null; }
            if (string.IsNullOrEmpty(cmd)) return null;

            int inputBaseline = session.UserInputCount;
            long lastOutputTick = 0;
            int outputEvents = 0;
            Action onOutput = delegate
            {
                Interlocked.Exchange(ref lastOutputTick, Environment.TickCount);
                Interlocked.Increment(ref outputEvents);
            };
            session.OutputReceived += onOutput;
            lock (pendingLock) { pendingBySessionId[session.Id] = cmd; }

            Thread t = new Thread(delegate ()
            {
                try
                {
                    int start = Environment.TickCount;
                    while (true)
                    {
                        Thread.Sleep(100);
                        if (!session.IsRunning) return;
                        if (session.UserInputCount != inputBaseline) return; // something else was typed first
                        lock (pendingLock) { if (!pendingBySessionId.ContainsKey(session.Id)) return; } // claimed
                        int elapsed = unchecked(Environment.TickCount - start);
                        int quietFor = unchecked(Environment.TickCount - (int)Interlocked.Read(ref lastOutputTick));
                        bool hadOutput = Interlocked.CompareExchange(ref outputEvents, 0, 0) > 0;
                        // Ready: a recognizable prompt that has settled briefly, or the shell has gone quiet after
                        // printing something (custom prompts: starship, powerlevel10k, oh-my-posh). Give up waiting
                        // after 8s and type anyway: the PTY buffers it for the shell's line editor.
                        bool ready = hadOutput && ((quietFor >= 150 && session.HasRenderedPrompt()) || quietFor >= 700);
                        if (ready || elapsed >= 8000) break;
                    }
                    bool stillPending;
                    lock (pendingLock) { stillPending = pendingBySessionId.Remove(session.Id); }
                    if (stillPending && session.IsRunning && session.UserInputCount == inputBaseline)
                        session.SendCommandLine(cmd);
                }
                catch { }
                finally
                {
                    lock (pendingLock) { pendingBySessionId.Remove(session.Id); }
                    try { session.OutputReceived -= onOutput; } catch { }
                }
            });
            t.IsBackground = true;
            t.Name = "VenvActivate_" + session.Id;
            t.Start();
            return cmd;
        }
    }
}
