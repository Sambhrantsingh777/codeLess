using System;
using System.Diagnostics;
using System.IO;
using codeLess.Studio;
using Xunit;

namespace codeLess.Avalonia.Tests
{
    public class GitBranchWorktreeTests
    {
        private static void Git(string dir, string args)
        {
            var psi = new ProcessStartInfo("git", args) { WorkingDirectory = dir, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
            psi.Environment["GIT_TERMINAL_PROMPT"] = "0";
            using var p = Process.Start(psi);
            p.WaitForExit(20000);
            Assert.True(p.ExitCode == 0, "git " + args + ": " + p.StandardError.ReadToEnd());
        }

        [Fact]
        public void GetGitBranch_ReportsTheLinkedWorktreesOwnBranch_NotTheParentRepos()
        {
            string root = Path.Combine(Path.GetTempPath(), "codeLess_wt_" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(root);
            try
            {
                Git(root, "init -q -b main");
                Git(root, "-c user.email=t@t -c user.name=t commit -q --allow-empty -m init");
                string wt = Path.Combine(root, "nested", "feature-wt");
                Git(root, "worktree add -q -b feature/x \"" + wt + "\"");
                Assert.True(File.Exists(Path.Combine(wt, ".git")), "a linked worktree has a .git file");
                Assert.Equal("main", LlmHarnessAndAgentEngine.GetGitBranch(root));
                Assert.Equal("feature/x", LlmHarnessAndAgentEngine.GetGitBranch(wt));
                Directory.CreateDirectory(Path.Combine(wt, "src"));
                Assert.Equal("feature/x", LlmHarnessAndAgentEngine.GetGitBranch(Path.Combine(wt, "src")));
            }
            finally
            {
                try { Git(root, "worktree prune"); } catch { }
                try { Directory.Delete(root, true); } catch { }
            }
        }
    }
}
