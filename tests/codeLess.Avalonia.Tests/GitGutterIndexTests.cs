using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Avalonia.Headless.XUnit;
using codeLess.AvaloniaApp;
using codeLess.Studio;
using Xunit;

namespace codeLess.Avalonia.Tests
{
    /// <summary>QA #9: the editor's git gutter compares the working tree with the index (VS Code quick diff).</summary>
    public class GitGutterIndexCoreTests : IDisposable
    {
        private readonly TempGitRepos repos = new TempGitRepos();
        public void Dispose() => repos.Dispose();

        internal static string Setup(TempGitRepos repos, string name, out string file)
        {
            string repo = repos.Init(name);
            file = Path.Combine(repo, "a.txt");
            File.WriteAllText(file, "l1\nl2\nl3\nl4\nl5\nl6\nl7\n");
            TempGitRepos.Git(repo, "add", "-A");
            TempGitRepos.Git(repo, "commit", "-q", "-m", "init");
            File.WriteAllText(file, "L1-staged\nl2\nl3\nl4\nl5\nl6\nl7\n");
            TempGitRepos.Git(repo, "add", "a.txt");                       // stage the first change
            File.WriteAllText(file, "L1-staged\nl2\nl3\nl4\nl5\nl6\nL7-unstaged\n"); // a second, unstaged change
            return repo;
        }

        [Fact]
        public void Baseline_IsTheIndex_SoStagedHunksLeaveTheGutter()
        {
            string repo = Setup(repos, "gutter", out string file);
            string baseline = LlmHarnessAndAgentEngine.GetGitQuickDiffBaseline(repo, file);
            Assert.Equal("L1-staged\nl2\nl3\nl4\nl5\nl6\nl7\n", baseline.Replace("\r\n", "\n"));
            Assert.Equal("l1\nl2\nl3\nl4\nl5\nl6\nl7\n", LlmHarnessAndAgentEngine.GetGitHeadFileContent(repo, file).Replace("\r\n", "\n"));

            string text = File.ReadAllText(file);
            var buf = new EditorTabBuffer { FilePath = file, Content = text, SavedContent = text, GitHeadContent = baseline };
            buf.SyncLinesFromContent();
            buf.RefreshGutterDiff();
            Assert.DoesNotContain(0, buf.GutterModifiedLines);  // staged: only in Source Control
            Assert.Contains(6, buf.GutterModifiedLines);        // unstaged: in the gutter

            // Untracked files have no quick-diff baseline.
            string untracked = Path.Combine(repo, "new.txt");
            File.WriteAllText(untracked, "x\n");
            Assert.Null(LlmHarnessAndAgentEngine.GetGitQuickDiffBaseline(repo, untracked));
        }

        [Fact]
        public void ConflictedFile_FallsBackToHead()
        {
            string repo = repos.Init("conflict");
            string f = Path.Combine(repo, "c.txt");
            File.WriteAllText(f, "base\n");
            TempGitRepos.Git(repo, "add", "-A"); TempGitRepos.Git(repo, "commit", "-q", "-m", "base");
            TempGitRepos.Git(repo, "checkout", "-q", "-b", "other");
            File.WriteAllText(f, "other\n");
            TempGitRepos.Git(repo, "commit", "-q", "-am", "other");
            TempGitRepos.Git(repo, "checkout", "-q", "main");
            File.WriteAllText(f, "mine\n");
            TempGitRepos.Git(repo, "commit", "-q", "-am", "mine");
            GitProcess.Run(repo, new[] { "merge", "other" }, 60000, System.Threading.CancellationToken.None, null, null); // conflicts
            Assert.Equal("mine\n", LlmHarnessAndAgentEngine.GetGitQuickDiffBaseline(repo, f).Replace("\r\n", "\n"));
        }
    }

    /// <summary>QA #10: the same merge-conflict / staging context-menu entries on both editors.</summary>
    public class EditorScmContextItemsTests
    {
        [Fact]
        public void ConflictUnderCaret_And_ChangesVsIndex_DecideTheEntries()
        {
            var conflict = new EditorTabBuffer { FilePath = "/r/a.txt", Content = "x\n<<<<<<< HEAD\nmine\n=======\ntheirs\n>>>>>>> other\ny\n" };
            conflict.SyncLinesFromContent();
            conflict.CursorLine = 2;
            var ids = EditorScmContextItems.Build(conflict).Where(e => !e.IsSeparator).Select(e => e.CommandId).ToList();
            Assert.Equal(new[] { "par_accept_current", "par_accept_incoming", "par_accept_both", "par_compare" }, ids);
            conflict.CursorLine = 0;
            Assert.Empty(EditorScmContextItems.Build(conflict).Where(e => !e.IsSeparator));

            var changed = new EditorTabBuffer { FilePath = "/r/b.txt", Content = "a\nB\n", SavedContent = "a\nB\n", GitHeadContent = "a\nb\n" };
            changed.SyncLinesFromContent();
            changed.RefreshGutterDiff();
            ids = EditorScmContextItems.Build(changed).Where(e => !e.IsSeparator).Select(e => e.CommandId).ToList();
            Assert.Equal(new[] { "par_stage_change", "par_stage_ranges", "par_revert_change" }, ids);

            // Not in a repository (no index baseline): unsaved edits are not "changes" to stage.
            var plain = new EditorTabBuffer { FilePath = "/tmp/c.txt", Content = "new\n", SavedContent = "old\n" };
            plain.SyncLinesFromContent();
            plain.RefreshGutterDiff();
            Assert.Empty(EditorScmContextItems.Build(plain));
        }
    }

    [Collection("SequentialWorkbench")]
    public class GitGutterIndexHeadlessTests : IDisposable
    {
        private readonly string settingsPath = IdePersistentSettings.GetSettingsFilePath();
        private readonly string settingsBackup;
        private readonly TempGitRepos repos = new TempGitRepos();

        public GitGutterIndexHeadlessTests() { settingsBackup = File.Exists(settingsPath) ? File.ReadAllText(settingsPath) : null; }

        public void Dispose()
        {
            try { if (settingsBackup != null) File.WriteAllText(settingsPath, settingsBackup); else if (File.Exists(settingsPath)) File.Delete(settingsPath); } catch { }
            repos.Dispose();
        }

        [AvaloniaFact]
        public void OpenFile_GutterShowsOnlyUnstagedChanges_AndStagingTheRestClearsIt()
        {
            string repo = GitGutterIndexCoreTests.Setup(repos, "gutterui", out string file);
            var w = new MainWindow(repo) { SuppressUnsavedPrompts = true };
            w.Show();
            try
            {
                var tab = w.OpenFileInTab(file, preview: false);
                Assert.DoesNotContain(0, tab.GutterModifiedLines);
                Assert.Contains(6, tab.GutterModifiedLines);

                // Stage the remaining hunk the way the gutter popup does, then refresh: the gutter is clean.
                string rel = GitCli.RepoRelativePath(repo, file);
                string index = GitHunkStaging.ReadIndexContent(repo, rel, out bool bin);
                var hunks = GitHunkDiff.ComputeHunks(index, tab.Lines, tab.HasFinalNewline);
                var h = GitHunkDiff.HunkAtLine(hunks, 6);
                Assert.NotNull(h);
                Assert.True(GitHunkStaging.StageHunks(repo, rel, index, new List<GitDiffHunk> { h }).Success);
                w.RefreshGitStatus();
                Assert.Empty(tab.GutterModifiedLines);
                Assert.Empty(tab.GutterAddedLines);
            }
            finally { w.Close(); }
        }
    }
}
