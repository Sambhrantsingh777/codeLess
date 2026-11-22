using System;
using System.Collections.Generic;
using System.IO;

namespace codeLess.Studio
{
    /// <summary>
    /// The agent's run_terminal executor (QA #22). Staged — not yet accepted — edits are never written into the user's
    /// files: when the turn has staged edits inside the workspace, the command runs in a temporary copy of the workspace
    /// with those edits applied (<see cref="VerificationOverlay"/>, the same copy the verification gate uses), and the
    /// copy is deleted afterwards (a copy left by a crash is removed at the next start). The user's files can't be left
    /// half-written by a crash or kill mid-command, and a command that rewrites files can't clobber them either.
    /// Without staged edits the command runs in the real workspace as before. Strict C# 5.
    /// </summary>
    public partial class LlmHarnessAndAgentEngine
    {
        /// <summary>Test hook: called with (command, working directory) right before the command starts.</summary>
        public Action<string, string> OnAgentCommandStarting;

        /// <summary>Limits for the temporary copy used by run_terminal (tests shrink them).</summary>
        public VerificationOverlayOptions TerminalOverlayOptions = new VerificationOverlayOptions();

        /// <summary>Staged edits that belong to the workspace (absolute path -> proposed text).</summary>
        public static Dictionary<string, string> StagedEditsInsideWorkspace(string workspaceRoot, IDictionary<string, string> stagedFiles)
        {
            Dictionary<string, string> inWs = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (stagedFiles == null || string.IsNullOrEmpty(workspaceRoot)) return inWs;
            foreach (KeyValuePair<string, string> kv in stagedFiles)
                if (kv.Key != null && kv.Value != null && IsPathInsideWorkspace(workspaceRoot, kv.Key)) inWs[kv.Key] = kv.Value;
            return inWs;
        }

        /// <summary>
        /// Runs an approved agent command. Returns the combined output (with a leading note when it ran in the copy or
        /// when the staged edits couldn't be applied).
        /// </summary>
        public string RunAgentTerminalCommand(string cmd, string workspaceRoot, IDictionary<string, string> stagedFiles, out int exitCode, out bool timedOut)
        {
            exitCode = 0;
            timedOut = false;
            Dictionary<string, string> staged = StagedEditsInsideWorkspace(workspaceRoot, stagedFiles);
            VerificationOverlay overlay = null;
            string note = "";
            string runDir = workspaceRoot;
            if (staged.Count > 0)
            {
                string err;
                overlay = VerificationOverlay.Create(workspaceRoot, staged, null, TerminalOverlayOptions ?? new VerificationOverlayOptions(), out err);
                if (overlay != null)
                {
                    runDir = overlay.OverlayWorkspace;
                    note = "[Ran in a temporary copy of the workspace with your " + staged.Count + " staged edit(s) applied. The user's files were not changed, and files this command creates or modifies in the copy are discarded: to change files, propose edits.]\n";
                }
                else
                {
                    note = "[Your staged edits could not be applied for this command (" + (err ?? "temporary copy failed") + "), so it ran against the files on disk, without your unaccepted edits.]\n";
                }
            }
            try
            {
                if (OnAgentCommandStarting != null) OnAgentCommandStarting(cmd, runDir);
                string output;
                if (overlay == null && OnExecuteTerminalCommandInConPty != null)
                {
                    output = OnExecuteTerminalCommandInConPty(cmd) ?? "";
                }
                else
                {
                    // Inside the copy: non-interactive, and no build servers that outlive the run (they would keep the
                    // copy's files open and block its deletion on Windows).
                    Dictionary<string, string> env = overlay != null ? VerificationRunner.VerificationEnvironment() : null;
                    if (env != null) env.Remove("CODELESS_VERIFY");
                    if (ConPtyNative.IsWindowsPlatform())
                        output = RunProcessCaptureWithExitCode("powershell.exe", "-NoProfile -NonInteractive -Command " + QuoteProcessArgument(cmd), runDir, AgentTerminalTimeoutMs, env, out exitCode, out timedOut);
                    else
                        output = RunProcessCaptureWithExitCode(ConPtyTerminalSession.GetUserUnixShell(), "-c " + QuoteProcessArgument(cmd), runDir, AgentTerminalTimeoutMs, env, out exitCode, out timedOut);
                }
                if (overlay != null && !string.IsNullOrEmpty(output))
                    output = output.Replace(overlay.OverlayWorkspace, workspaceRoot); // paths in errors point at the real files
                return note + (output ?? "");
            }
            finally
            {
                if (overlay != null) overlay.Dispose();
            }
        }
    }
}
