using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using codeLess.Studio;
using Xunit;

namespace codeLess.Avalonia.Tests
{
    /// <summary>GIT-1 .. GIT-5 and AGT-1 (the agent's temporary copy is a private clone).</summary>
    [Collection("SequentialWorkbench")]
    public class EngineReviewGitTests : IDisposable
    {
        private readonly TempGitRepos repos = new TempGitRepos();
        private readonly string oldOverlays = VerificationOverlay.OverlaysRoot;

        public EngineReviewGitTests()
        {
            VerificationOverlay.OverlaysRoot = Path.Combine(repos.Root, "overlays");
            NetworkLedger.SetOfflineMode(false);
        }

        public void Dispose()
        {
            NetworkLedger.SetOfflineMode(false);
            VerificationOverlay.CopyOnWriteDisabled = false;
            VerificationOverlay.OverlaysRoot = oldOverlays;
            repos.Dispose();
        }

        private static string Git(string dir, params string[] a) => TempGitRepos.Git(dir, a);

        [Fact]
        public void GIT1_Sync_HonoursPullRebaseConfig_InsteadOfForcingAMerge()
        {
            if (!GitCli.IsAvailable()) return;
            string bare = repos.InitBare("r.git");
            string a = repos.Clone(bare, "a");
            TempGitRepos.Write(a, "f.txt", "1\n");
            TempGitRepos.Commit(a, "base");
            Git(a, "push", "-q", "origin", "main");
            string b = repos.Clone(bare, "b");
            TempGitRepos.Write(a, "a.txt", "from a\n");
            TempGitRepos.Commit(a, "a change");
            Git(a, "push", "-q", "origin", "main");
            TempGitRepos.Write(b, "b.txt", "from b\n");
            TempGitRepos.Commit(b, "b change");
            Git(b, "config", "pull.rebase", "true");

            var r = GitRemoteOps.Sync(b, false, CancellationToken.None, null);
            Assert.True(r.Success, r.Message + "\n" + r.Details);
            string merges = Git(b, "rev-list", "--merges", "--count", "HEAD").Trim();
            Assert.Equal("0", merges); // rebased, no merge commit
        }

        [Fact]
        public void GIT5_OfflineMode_ChecksThePushUrl_NotOnlyTheFetchUrl()
        {
            if (!GitCli.IsAvailable()) return;
            string bare = repos.InitBare("p.git");
            string a = repos.Clone(bare, "pa");
            TempGitRepos.Write(a, "f.txt", "1\n");
            TempGitRepos.Commit(a, "c");
            Git(a, "push", "-q", "-u", "origin", "main");
            TempGitRepos.Write(a, "g.txt", "2\n");
            TempGitRepos.Commit(a, "d");
            Git(a, "remote", "set-url", "--push", "origin", "https://codeless-push-leak.invalid/repo.git");
            NetworkLedger.SetOfflineMode(true);
            var r = GitRemoteOps.Push(a, CancellationToken.None, null);
            Assert.False(r.Success);
            Assert.Equal(GitErrorKind.Network, r.Error);
            Assert.Contains("Offline Mode", r.Message);
            // Fetch only talks to the (local) fetch URL: allowed.
            Assert.True(GitRemoteOps.Fetch(a, false, false, CancellationToken.None, null).Success);
        }

        [Fact]
        public void GIT3_DirtyBlameContents_UseTheFilesLineEndings_SoOnlyTheEditedLineIsUncommitted()
        {
            if (!GitCli.IsAvailable()) return;
            string a = repos.Init("crlf");
            File.WriteAllBytes(Path.Combine(a, "c.txt"), Encoding.UTF8.GetBytes("one\r\ntwo\r\nthree\r\n"));
            TempGitRepos.Commit(a, "c");
            byte[] contents = GitBlameService.EncodeLikeFile(Path.Combine(a, "c.txt"), "one\nTWO\nthree\n");
            Assert.Equal(Encoding.UTF8.GetBytes("one\r\nTWO\r\nthree\r\n"), contents);
            string tmp = Path.Combine(repos.Root, "blame.tmp");
            File.WriteAllBytes(tmp, contents);
            string porcelain = Git(a, "blame", "--porcelain", "--contents", tmp, "--", "c.txt");
            var lines = GitBlameParser.ParsePorcelain(porcelain);
            Assert.Equal(1, lines.Count(l => l.Commit.IsUncommitted));

            File.WriteAllBytes(Path.Combine(a, "l1.txt"), new byte[] { (byte)'c', (byte)'a', (byte)'f', 0xE9, (byte)'\n' });
            Assert.Equal(new byte[] { (byte)'c', (byte)'a', (byte)'f', 0xE9, (byte)'!', (byte)'\n' }, GitBlameService.EncodeLikeFile(Path.Combine(a, "l1.txt"), "café!\n"));
        }

        [Fact]
        public void GIT4_StagingAHunk_OfALatin1File_KeepsTheIndexBytes()
        {
            if (!GitCli.IsAvailable()) return;
            string a = repos.Init("latin1");
            byte[] orig = Encoding.Latin1.GetBytes("café 1\nl2\nl3\nl4\nl5\nl6\nl7\nl8\ncafé 9\n");
            File.WriteAllBytes(Path.Combine(a, "m.txt"), orig);
            TempGitRepos.Commit(a, "c");
            var editor = "café ONE\nl2\nl3\nl4\nl5\nl6\nl7\nl8\ncafé NINE\n".Split('\n').ToList();
            editor.RemoveAt(editor.Count - 1);
            File.WriteAllBytes(Path.Combine(a, "m.txt"), Encoding.Latin1.GetBytes(string.Join("\n", editor) + "\n"));
            var r = GitHunkStaging.StageFromEditor(a, "m.txt", editor, true, 0, 0, true);
            Assert.True(r.Success, r.Message);
            byte[] index = GitProcess.RunBytes(a, new[] { "cat-file", "blob", ":m.txt" }, 10000);
            Assert.Equal(Encoding.Latin1.GetBytes("café ONE\nl2\nl3\nl4\nl5\nl6\nl7\nl8\ncafé 9\n"), index);

            bool bin;
            Assert.Contains("café ONE", GitHunkStaging.ReadIndexContent(a, "m.txt", out bin));
            Assert.StartsWith("café 1\n", GitHunkStaging.ReadHeadContent(a, "m.txt"));
        }

        [Fact]
        public void GIT4_PartialStaging_IsRefusedForFilteredFiles()
        {
            if (!GitCli.IsAvailable()) return;
            string a = repos.Init("lfs");
            File.WriteAllText(Path.Combine(a, ".gitattributes"), "*.bin filter=lfs diff=lfs merge=lfs -text\n");
            Assert.True(GitHunkStaging.IsFilteredPath(a, "x.bin"));
            Assert.False(GitHunkStaging.IsFilteredPath(a, "x.txt"));
        }

        [Fact]
        public void GIT2_AGT1_Overlay_IsAPrivateClone_UserWorktreesAndRefsAreUntouched()
        {
            if (!GitCli.IsAvailable()) return;
            string ws = repos.Init("user");
            TempGitRepos.Write(ws, "a.txt", "a\n");
            TempGitRepos.Commit(ws, "init");
            // A user worktree whose folder is temporarily missing (external drive / renamed): must survive.
            string wt = Path.Combine(repos.Root, "user-wt");
            Git(ws, "worktree", "add", "-q", wt);
            Directory.Move(wt, wt + "-offline");
            string before = Git(ws, "worktree", "list", "--porcelain");

            string err;
            var ov = VerificationOverlay.Create(ws, new Dictionary<string, string> { { Path.Combine(ws, "a.txt"), "staged\n" } }, null, new VerificationOverlayOptions(), out err);
            Assert.True(ov != null, err);
            try
            {
                Assert.Equal("git-clone", ov.Strategy);
                Assert.Equal("", Git(ov.OverlayRoot, "remote").Trim());               // no remotes: push can't reach anything
                Git(ov.OverlayRoot, "branch", "agent-branch");
                Git(ov.OverlayRoot, "tag", "agent-tag");
                Git(ov.OverlayRoot, "stash", "-q", "-u");
                Assert.DoesNotContain("agent-branch", Git(ws, "branch", "--list"));
                Assert.Equal("", Git(ws, "tag", "--list").Trim());
                Assert.Equal("", Git(ws, "stash", "list").Trim());
                Assert.Equal("a\n", File.ReadAllText(Path.Combine(ws, "a.txt")));
            }
            finally { ov.Dispose(); }
            VerificationOverlay.CleanupStale(TimeSpan.FromHours(12));
            Assert.Equal(before, Git(ws, "worktree", "list", "--porcelain")); // never pruned
        }

        [Fact]
        public void GIT2_LegacyCodelessWorktreeEntries_AreRemovedByExactPathOnly()
        {
            if (!GitCli.IsAvailable()) return;
            string ws = repos.Init("legacy");
            TempGitRepos.Write(ws, "a.txt", "a\n");
            TempGitRepos.Commit(ws, "init");
            Directory.CreateDirectory(VerificationOverlay.OverlaysRoot);
            string legacy = Path.Combine(VerificationOverlay.OverlaysRoot, "v-legacy");
            Git(ws, "worktree", "add", "-q", "--detach", legacy);
            string userWt = Path.Combine(repos.Root, "mine");
            Git(ws, "worktree", "add", "-q", userWt);
            Directory.Delete(legacy, true);
            Directory.Move(userWt, userWt + "-gone");
            Assert.Equal(1, VerificationOverlay.RemoveCodelessWorktreeEntries(ws));
            string list = Git(ws, "worktree", "list", "--porcelain");
            Assert.DoesNotContain("v-legacy", list);
            Assert.Contains("mine", list);
        }

        [Fact]
        public void AGT1_SharedDependencyFolders_AreDeclared_AndPackageInstallsAreRefused()
        {
            if (!GitCli.IsAvailable() || Path.DirectorySeparatorChar == '\\') return;
            string ws = repos.Init("deps");
            TempGitRepos.Write(ws, ".gitignore", "node_modules/\n");
            TempGitRepos.Write(ws, "index.js", "1\n");
            TempGitRepos.Commit(ws, "init");
            Directory.CreateDirectory(Path.Combine(ws, "node_modules", "left-pad"));
            File.WriteAllText(Path.Combine(ws, "node_modules", "left-pad", "index.js"), "x");
            VerificationOverlay.CopyOnWriteDisabled = true; // simulate a file system without copy-on-write
            var engine = new LlmHarnessAndAgentEngine(new IdePersistentSettings());
            string marker = Path.Combine(ws, "node_modules", "installed.txt");
            int code; bool to;
            string outp = engine.RunAgentTerminalCommand("npm install lodash && touch node_modules/installed.txt", ws,
                new Dictionary<string, string> { { Path.Combine(ws, "index.js"), "2\n" } }, 120000, out code, out to);
            Assert.Contains("Not run", outp);
            Assert.Contains("node_modules", outp);
            Assert.False(File.Exists(marker));

            string ok = engine.RunAgentTerminalCommand("cat index.js", ws, new Dictionary<string, string> { { Path.Combine(ws, "index.js"), "2\n" } }, 120000, out code, out to);
            Assert.Contains("SHARED", ok);
            Assert.Contains("own git repository", ok);
            Assert.Contains("2", ok);
            Assert.True(File.Exists(Path.Combine(ws, "node_modules", "left-pad", "index.js")), "cleanup must not follow the link");

            VerificationOverlay.CopyOnWriteDisabled = false;
            if (LspText.IsMacOS())
            {
                string err;
                using (var ov = VerificationOverlay.Create(ws, new Dictionary<string, string>(), null, new VerificationOverlayOptions(), out err))
                {
                    Assert.True(ov != null, err);
                    Assert.Contains("node_modules", ov.ClonedFolders);                  // APFS clonefile: a private copy
                    File.WriteAllText(Path.Combine(ov.OverlayWorkspace, "node_modules", "written-in-copy.txt"), "x");
                }
                Assert.False(File.Exists(Path.Combine(ws, "node_modules", "written-in-copy.txt")));
            }
        }
    }
}
