using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using codeLess.Studio;

namespace codeLess.AvaloniaApp
{
    /// <summary>
    /// Terminals follow File → Open Folder like VS Code (which starts fresh terminals in the newly opened folder):
    /// idle shells restart in the new folder with a fresh environment (the old workspace's venv is gone and the new
    /// one is activated); shells running a foreground job (dev server, test run, vim) are left untouched.
    /// </summary>
    public partial class MainWindow
    {
        /// <summary>Test hook: overrides "is this terminal idle?" (headless tests have no live PTY).</summary>
        public Func<TerminalSessionEntry, bool> TerminalIdleOverride;

        /// <summary>Folder the most recent terminal follow targeted (tests).</summary>
        public string LastTerminalFollowTarget { get; private set; }

        private void FollowWorkspaceInTerminals(string root)
        {
            if (string.IsNullOrEmpty(root) || !Directory.Exists(root)) return;
            LastTerminalFollowTarget = root;
            int busy = 0;
            StringComparison cmp = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            string normNew = LspText.PathKey(root); // folds macOS /private/var vs /var
            foreach (var t in terminalSessions.ToList())
            {
                if (t?.Control == null || t.TaskLabel != null) continue; // task terminals belong to their task
                var session = t.Control.Session;
                bool idle;
                string cur;
                if (TerminalIdleOverride != null)
                {
                    idle = TerminalIdleOverride(t);
                    cur = t.Control.WorkingDirectory;
                }
                else
                {
                    if (session == null || session.HasExited) continue;
                    idle = session.IsShellForegroundIdle();
                    cur = session.GetLiveWorkingDirectory();
                }
                if (string.Equals(LspText.PathKey(cur ?? ""), normNew, cmp)) continue;
                if (!idle) { busy++; continue; }
                RestartTerminalIn(t, root);
            }
            if (busy > 0)
                SetStatusMessage(busy == 1
                    ? "A terminal is still running a command, so it stayed in the previous folder."
                    : $"{busy} terminals are still running commands, so they stayed in the previous folder.");
        }

        private void RestartTerminalIn(TerminalSessionEntry t, string dir)
        {
            var ctrl = t.Control;
            bool started;
            try { started = ctrl.StartShell(dir, string.IsNullOrEmpty(t.ShellCommand) ? null : t.ShellCommand); }
            catch { started = false; }
            if (!started)
            {
                // Headless / no PTY: keep the synthetic prompt in sync with the folder.
                string leaf = Path.GetFileName(dir.TrimEnd('\\', '/'));
                ctrl.ClearTerminal();
                ctrl.SeedInitialPrompt($"\x1b[1;34m{t.Title}\x1b[0m \x1b[32m➜\x1b[0m \x1b[1;36m{leaf}\x1b[0m $ ");
                return;
            }
            if (settings == null || settings.ActivatePythonEnvironmentInTerminal)
            {
                try { PythonVenvActivator.ScheduleActivation(ctrl.Session, dir); } catch { }
            }
        }
    }
}
