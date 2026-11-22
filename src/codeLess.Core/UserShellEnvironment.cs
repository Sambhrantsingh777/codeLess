using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;

namespace codeLess.Studio
{
    /// <summary>
    /// macOS / Linux GUI apps launched from Finder, the Dock or a desktop launcher inherit a minimal environment (PATH is
    /// /usr/bin:/bin:/usr/sbin:/sbin), so tools that users install through their shell rc files (nvm, pyenv, asdf, rbenv,
    /// Homebrew prefixes, ~/.cargo/bin, GOPATH...) are missing for tasks, language servers and debug adapters. Like VS Code,
    /// resolve the user's interactive login-shell environment once at startup and apply it to this process so every child
    /// process inherits it. Skipped on Windows and when codeLess was started from a shell (the environment is already
    /// complete). C# 5 only (compiled by legacy csc.exe as part of codeLess.Core).
    /// </summary>
    public static class UserShellEnvironment
    {
        private static readonly ManualResetEvent done = new ManualResetEvent(false);
        private static int started;

        /// <summary>Variables applied from the shell (empty until resolved or when resolution was skipped).</summary>
        public static Dictionary<string, string> Applied = new Dictionary<string, string>(StringComparer.Ordinal);
        public static string Status = "not started";
        public static int TimeoutMs = 10000;

        // Never imported: they describe the resolver's own shell, not the user's session.
        private static readonly HashSet<string> Skip = new HashSet<string>(StringComparer.Ordinal)
        {
            "PWD", "OLDPWD", "SHLVL", "_", "PS1", "PS2", "TERM", "TERM_PROGRAM", "TERM_PROGRAM_VERSION", "TERM_SESSION_ID",
            "ITERM_SESSION_ID", "TMPDIR", "CODELESS_RESOLVING_ENVIRONMENT", "COLUMNS", "LINES", "ZDOTDIR_RESOLVING", "XPC_SERVICE_NAME"
        };

        /// <summary>True when this process already has a shell-initialised environment (started from a terminal).</summary>
        public static bool LaunchedFromShell()
        {
            return !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("SHLVL")) ||
                   !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("TERM_PROGRAM"));
        }

        /// <summary>Starts background resolution (idempotent). Call once at desktop app startup.</summary>
        public static void StartResolve()
        {
            if (Interlocked.Exchange(ref started, 1) == 1) return;
            if (Path.DirectorySeparatorChar == '\\' || LaunchedFromShell())
            {
                Status = Path.DirectorySeparatorChar == '\\' ? "skipped (Windows)" : "skipped (started from a shell)";
                done.Set();
                return;
            }
            Thread t = new Thread(delegate ()
            {
                try { Resolve(); }
                catch (Exception ex) { Status = "failed: " + ex.Message; }
                finally { done.Set(); }
            });
            t.IsBackground = true;
            t.Name = "codeLess shell environment";
            t.Start();
        }

        /// <summary>
        /// Waits (up to <paramref name="maxWaitMs"/>) for resolution started by <see cref="StartResolve"/>. Returns immediately
        /// when resolution was never started (tests, Windows) or is finished. Call before spawning user tools.
        /// </summary>
        public static bool WaitForResolution(int maxWaitMs)
        {
            if (Thread.VolatileRead(ref started) == 0) return true;
            return done.WaitOne(Math.Max(0, maxWaitMs));
        }

        private static void Resolve()
        {
            string status;
            Stopwatch sw = Stopwatch.StartNew();
            Dictionary<string, string> env = CaptureLoginShellEnvironment(TimeoutMs, out status);
            if (env == null) { Status = status; return; }
            Apply(env);
            Status = "resolved " + Applied.Count + " variables in " + sw.ElapsedMilliseconds + " ms (" + status + ")";
        }

        /// <summary>Runs the user's shell as an interactive login shell and returns its environment (null on failure).</summary>
        public static Dictionary<string, string> CaptureLoginShellEnvironment(int timeoutMs, out string status)
        {
            string shell = ConPtyTerminalSession.GetUserUnixShell();
            string leaf = Path.GetFileName(shell ?? "").ToLowerInvariant();
            string marker = "__CODELESS_ENV_" + Guid.NewGuid().ToString("N") + "__";
            string script = "printf '%s' '" + marker + "'; /usr/bin/env; printf '%s' '" + marker + "'";
            // -i loads .zshrc/.bashrc (where nvm/pyenv usually live), -l loads the profile files.
            string args = (leaf == "sh" || leaf == "dash") ? "-l -c " : "-i -l -c ";
            if (leaf == "tcsh" || leaf == "csh") args = "-c ";
            ProcessStartInfo psi = new ProcessStartInfo(shell, args + QuoteForSh(script));
            psi.UseShellExecute = false;
            psi.RedirectStandardOutput = true;
            psi.RedirectStandardError = true;
            psi.RedirectStandardInput = true;
            psi.CreateNoWindow = true;
            psi.WorkingDirectory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            psi.EnvironmentVariables["CODELESS_RESOLVING_ENVIRONMENT"] = "1";
            psi.EnvironmentVariables["TERM"] = "dumb"; // keep prompt/plugin frameworks quiet and non-interactive

            using (Process p = Process.Start(psi))
            {
                try { p.StandardInput.Close(); } catch { }
                StringBuilder outBuf = new StringBuilder();
                p.OutputDataReceived += delegate (object s, DataReceivedEventArgs e) { if (e.Data != null) lock (outBuf) outBuf.Append(e.Data).Append('\n'); };
                p.ErrorDataReceived += delegate (object s, DataReceivedEventArgs e) { };
                p.BeginOutputReadLine();
                p.BeginErrorReadLine();
                if (!p.WaitForExit(timeoutMs))
                {
                    try { p.Kill(); } catch { }
                    status = "timed out after " + timeoutMs + " ms (shell start-up files too slow)";
                    return null;
                }
                p.WaitForExit();
                string text;
                lock (outBuf) text = outBuf.ToString();
                Dictionary<string, string> env = ParseEnvBlock(text, marker);
                if (env == null || !env.ContainsKey("PATH"))
                {
                    status = "could not parse the shell environment";
                    return null;
                }
                status = leaf;
                return env;
            }
        }

        /// <summary>Parses `env` output printed between two markers. Values containing newlines are kept.</summary>
        public static Dictionary<string, string> ParseEnvBlock(string text, string marker)
        {
            if (string.IsNullOrEmpty(text) || string.IsNullOrEmpty(marker)) return null;
            int a = text.IndexOf(marker, StringComparison.Ordinal);
            if (a < 0) return null;
            int b = text.IndexOf(marker, a + marker.Length, StringComparison.Ordinal);
            if (b < 0) return null;
            string block = text.Substring(a + marker.Length, b - a - marker.Length);
            Dictionary<string, string> env = new Dictionary<string, string>(StringComparer.Ordinal);
            string lastKey = null;
            foreach (string raw in block.Split('\n'))
            {
                string line = raw.TrimEnd('\r');
                int eq = line.IndexOf('=');
                if (eq > 0 && IsValidName(line.Substring(0, eq)))
                {
                    lastKey = line.Substring(0, eq);
                    env[lastKey] = line.Substring(eq + 1);
                }
                else if (lastKey != null && line.Length > 0)
                {
                    env[lastKey] = env[lastKey] + "\n" + line; // continuation of a multi-line value
                }
            }
            return env;
        }

        private static bool IsValidName(string name)
        {
            if (name.Length == 0 || char.IsDigit(name[0])) return false;
            foreach (char c in name)
                if (!(char.IsLetterOrDigit(c) || c == '_')) return false;
            return true;
        }

        private static void Apply(Dictionary<string, string> env)
        {
            foreach (KeyValuePair<string, string> kv in env)
            {
                if (Skip.Contains(kv.Key)) continue;
                string current = Environment.GetEnvironmentVariable(kv.Key);
                if (string.Equals(current, kv.Value, StringComparison.Ordinal)) continue;
                try
                {
                    Environment.SetEnvironmentVariable(kv.Key, kv.Value);
                    Applied[kv.Key] = kv.Value;
                }
                catch { }
            }
            try { LanguageServerRegistry.ClearCache(); } catch { }
        }

        private static string QuoteForSh(string s)
        {
            // ProcessStartInfo argument string: wrap in double quotes and escape for the .NET argument parser.
            return "\"" + s.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
        }
    }
}
