using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using codeLess.Studio;
using Xunit;

namespace codeLess.Avalonia.Tests
{
    /// <summary>
    /// QA #22: the agent's run_terminal never writes staged (unaccepted) edits into the user's files. With staged edits
    /// the command runs in a temporary copy of the workspace; the real files are untouched during and after the run.
    /// </summary>
    public class AgentTerminalSandboxTests
    {
        private static Dictionary<string, object> Cmd(string c) => new Dictionary<string, object> { { "command", c } };

        private static string Workspace(bool git)
        {
            string ws = AgentTrustTestEnv.NewDir("term");
            if (git) AgentTrustTestEnv.InitRepo(ws);
            File.WriteAllText(Path.Combine(ws, "a.txt"), "disk\n");
            if (git) AgentTrustTestEnv.Commit(ws, "init");
            return ws;
        }

        private static string[] OverlayDirs() => Directory.Exists(VerificationOverlay.OverlaysRoot) ? Directory.GetDirectories(VerificationOverlay.OverlaysRoot) : new string[0];

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void StagedEdits_AreVisibleToTheCommand_ButNeverWrittenToTheRealFiles(bool git)
        {
            if (OperatingSystem.IsWindows()) return;
            string ws = Workspace(git);
            try
            {
                string a = Path.Combine(ws, "a.txt");
                string b = Path.Combine(ws, "sub", "b.txt");
                var staged = new Dictionary<string, string> { { a, "staged\n" }, { b, "new file\n" } };
                var engine = new LlmHarnessAndAgentEngine(null);
                string runDir = null;
                engine.OnAgentCommandStarting = (c, dir) =>
                {
                    runDir = dir;
                    // A crash at this moment must leave the user's files exactly as they were.
                    Assert.Equal("disk\n", File.ReadAllText(a));
                    Assert.False(File.Exists(b));
                };
                var r = engine.ExecuteWorkspaceTool("run_terminal", Cmd("cat a.txt sub/b.txt; echo clobbered > a.txt; touch made.txt; pwd"), ws, null, null, staged);
                Assert.True(r.Success, r.Output);
                Assert.Contains("staged", r.Output);
                Assert.Contains("new file", r.Output);
                Assert.Contains("temporary copy", r.Output);
                Assert.NotEqual(Path.GetFullPath(ws), Path.GetFullPath(runDir));
                Assert.Equal("disk\n", File.ReadAllText(a));
                Assert.False(File.Exists(b));
                Assert.False(File.Exists(Path.Combine(ws, "made.txt")));
                Assert.False(Directory.Exists(runDir)); // the copy is deleted after the command
                Assert.Equal("staged\n", staged[a]); // the proposal itself is unchanged
            }
            finally { AgentTrustTestEnv.Delete(ws); }
        }

        [Fact]
        public void WithoutStagedEdits_TheCommandRunsInTheRealWorkspace()
        {
            if (OperatingSystem.IsWindows()) return;
            string ws = Workspace(false);
            try
            {
                var engine = new LlmHarnessAndAgentEngine(null);
                var r = engine.ExecuteWorkspaceTool("run_terminal", Cmd("echo hi > made.txt"), ws, null, null, new Dictionary<string, string>());
                Assert.True(r.Success, r.Output);
                Assert.Equal("hi", File.ReadAllText(Path.Combine(ws, "made.txt")).Trim());
                Assert.DoesNotContain("temporary copy", r.Output);
            }
            finally { AgentTrustTestEnv.Delete(ws); }
        }

        [Fact]
        public void CopyFailure_FallsBackToDiskFiles_WithoutWritingTheStagedEdits()
        {
            if (OperatingSystem.IsWindows()) return;
            string ws = Workspace(false);
            try
            {
                string a = Path.Combine(ws, "a.txt");
                var engine = new LlmHarnessAndAgentEngine(null);
                engine.TerminalOverlayOptions = new VerificationOverlayOptions { PreferGitWorktree = false, MaxCopyBytes = 1 };
                var r = engine.ExecuteWorkspaceTool("run_terminal", Cmd("cat a.txt"), ws, null, null, new Dictionary<string, string> { { a, "staged\n" } });
                Assert.Contains("could not be applied", r.Output);
                Assert.Contains("disk", r.Output);
                Assert.DoesNotContain("staged\n", File.ReadAllText(a));
            }
            finally { AgentTrustTestEnv.Delete(ws); }
        }

        [Fact]
        public void CommandCrash_LeavesRealFilesIntact_AndALeftoverCopyIsRemovedAtNextStart()
        {
            if (OperatingSystem.IsWindows()) return;
            string ws = Workspace(false);
            try
            {
                string a = Path.Combine(ws, "a.txt");
                var engine = new LlmHarnessAndAgentEngine(null);
                string runDir = null;
                engine.OnAgentCommandStarting = (c, dir) => { runDir = dir; throw new InvalidOperationException("simulated crash"); };
                var r = engine.ExecuteWorkspaceTool("run_terminal", Cmd("true"), ws, null, null, new Dictionary<string, string> { { a, "staged\n" } });
                Assert.False(r.Success);
                Assert.Equal("disk\n", File.ReadAllText(a));
                Assert.False(Directory.Exists(runDir));

                // A hard crash (process killed mid-command) leaves the copy behind with a marker of a dead process:
                // the next start removes it, and the user's files never contained the staged text.
                string err;
                var leftover = VerificationOverlay.Create(ws, new Dictionary<string, string> { { a, "staged\n" } }, null, new VerificationOverlayOptions { PreferGitWorktree = false }, out err);
                Assert.NotNull(leftover);
                string marker = Path.Combine(VerificationOverlay.OverlaysRoot, leftover.Id + ".json");
                File.WriteAllText(marker, "{\"pid\": 2147483000, \"repo\": \"\", \"workspace\": \"\", \"createdUtc\": \"" + DateTime.UtcNow.ToString("o") + "\"}");
                Assert.True(Directory.Exists(leftover.OverlayRoot));
                Assert.True(VerificationOverlay.CleanupStale(TimeSpan.FromHours(12)) >= 1);
                Assert.False(Directory.Exists(leftover.OverlayRoot));
                Assert.Equal("disk\n", File.ReadAllText(a));
            }
            finally { AgentTrustTestEnv.Delete(ws); }
        }

        [Fact]
        public void ToolOutputCap_KeepsTheTailWhereErrorsAre()
        {
            string output = "[note]\n" + new string('x', 5000) + "\nerror CS1002: ; expected";
            string trimmed = LlmHarnessAndAgentEngine.TrimToolOutputKeepingTail(output, 1600);
            Assert.True(trimmed.Length < 1700);
            Assert.StartsWith("[note]", trimmed);
            Assert.EndsWith("error CS1002: ; expected", trimmed);
            Assert.Equal("short", LlmHarnessAndAgentEngine.TrimToolOutputKeepingTail("short", 1600));
        }
    }
}
