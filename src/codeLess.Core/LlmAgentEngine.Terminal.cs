using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

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

        /// <summary>What the model is told about the temporary copy - only claims that are actually true for this copy.</summary>
        public static string DescribeOverlayForModel(VerificationOverlay overlay, int stagedCount)
        {
            StringBuilder sb = new StringBuilder();
            sb.Append("[Ran in a temporary copy of the workspace with your ").Append(stagedCount).Append(" staged edit(s) applied. ");
            sb.Append("The user's files were not changed, and files this command creates or modifies in the copy are discarded: to change files, propose edits.");
            if (overlay.Strategy == "git-clone")
                sb.Append(" The copy has its own git repository without remotes: commits, branches, tags and stash there don't affect the user's repository.");
            if (overlay.ClonedFolders.Count > 0)
                sb.Append(" ").Append(string.Join(", ", overlay.ClonedFolders.ToArray())).Append(" are private copy-on-write copies.");
            if (overlay.SharedFolders.Count > 0)
                sb.Append(" WARNING: ").Append(string.Join(", ", overlay.SharedFolders.ToArray()))
                  .Append(" are SHARED with the user's real workspace (not copied): anything written there changes the user's real environment.");
            sb.Append("]\n");
            return sb.ToString();
        }

        /// <summary>
        /// Runs an approved agent command. Returns the combined output (with a leading note when it ran in the copy or
        /// when the staged edits couldn't be applied).
        /// </summary>
        public string RunAgentTerminalCommand(string cmd, string workspaceRoot, IDictionary<string, string> stagedFiles, int timeoutMs, out int exitCode, out bool timedOut)
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
                    note = DescribeOverlayForModel(overlay, staged.Count);
                    if (overlay.WouldModifySharedDependencies(cmd))
                    {
                        string shared = string.Join(", ", overlay.SharedFolders.ToArray());
                        overlay.Dispose();
                        exitCode = 1;
                        return "[Not run: this command installs or removes packages, but " + shared +
                               " in the temporary copy is shared with the user's real workspace (this file system can't make a private copy), " +
                               "so it would change the user's real environment while your edits are still unaccepted. Propose the edits " +
                               "(e.g. to package.json / requirements.txt) and let the user install after accepting.]\n";
                    }
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
                        output = RunProcessCaptureWithExitCode("powershell.exe", "-NoProfile -NonInteractive -Command " + QuoteProcessArgument(cmd), runDir, timeoutMs, env, out exitCode, out timedOut);
                    else
                        output = RunProcessCaptureWithExitCode(ConPtyTerminalSession.GetUserUnixShell(), "-c " + QuoteProcessArgument(cmd), runDir, timeoutMs, env, out exitCode, out timedOut);
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
