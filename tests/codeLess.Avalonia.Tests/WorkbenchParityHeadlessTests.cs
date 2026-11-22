using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using codeLess.AvaloniaApp;
using codeLess.Studio;
using Xunit;

namespace codeLess.Avalonia.Tests
{
    /// <summary>Avalonia wiring of the git / tasks / save-action parity features (headless, local repos only).</summary>
    [Collection("SequentialWorkbench")]
    public class WorkbenchParityHeadlessTests : IDisposable
    {
        private readonly string settingsPath;
        private readonly string settingsBackup;
        private readonly bool hadSettings;
        private readonly TempGitRepos repos = new TempGitRepos();
        private MainWindow current;

        public WorkbenchParityHeadlessTests()
        {
            settingsPath = IdePersistentSettings.GetSettingsFilePath();
            hadSettings = File.Exists(settingsPath);
            settingsBackup = hadSettings ? File.ReadAllText(settingsPath) : null;
        }

        public void Dispose()
        {
            MacKeymap.ForceMacKeymapOverride = null;
            TaskProblemStore.ClearAll();
            try
            {
                if (hadSettings) File.WriteAllText(settingsPath, settingsBackup);
                else if (File.Exists(settingsPath)) File.Delete(settingsPath);
            }
            catch { }
            repos.Dispose();
        }

        private bool Pump(Func<bool> cond, int ms = 20000)
        {
            var sw = Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < ms)
            {
                Dispatcher.UIThread.RunJobs();
                current?.FlushDevToolsDebounce();
                if (cond()) return true;
                Thread.Sleep(15);
            }
            Dispatcher.UIThread.RunJobs();
            return cond();
        }

        private MainWindow Open(string dir)
        {
            MacKeymap.ForceMacKeymapOverride = true;
            var w = new MainWindow(dir) { Width = 1400, Height = 900 };
            w.Show();
            Dispatcher.UIThread.RunJobs();
            current = w;
            return w;
        }

        private static void Close(MainWindow w)
        {
            w.SuppressUnsavedPrompts = true;
            w.Close();
            Dispatcher.UIThread.RunJobs();
        }

        [AvaloniaFact]
        public void BranchPicker_ListsBranches_ChecksOutAndShowsSyncCounts()
        {
            string bare = repos.InitBare("remote.git");
            string a = repos.Clone(bare, "a");
            TempGitRepos.Write(a, "readme.md", "hi\n");
            TempGitRepos.Commit(a, "init");
            TempGitRepos.Git(a, "push", "-q", "-u", "origin", "main");
            TempGitRepos.Git(a, "branch", "feature/login");
            TempGitRepos.Git(a, "tag", "v1.0");
            string b = repos.Clone(bare, "b");
            // Remote gets a new commit B doesn't have yet.
            TempGitRepos.Write(a, "x.txt", "x\n");
            TempGitRepos.Commit(a, "remote work");
            TempGitRepos.Git(a, "push", "-q");

            var w = Open(a);
            try
            {
                w.OpenBranchPicker();
                Assert.True(Pump(() => w.LastBranchPickerEntries.Any(e => e.Title == "feature/login")));
                var titles = w.LastBranchPickerEntries.Select(e => e.Title).ToList();
                Assert.Contains("main", titles);
                Assert.Contains("origin/main", titles);
                Assert.Contains("v1.0", titles);
                Assert.Contains("Create new branch...", titles);
                Assert.True(w.CommandPalette.IsVisible);
                Assert.StartsWith("★", w.LastBranchPickerEntries.First(e => e.Title == "main").Category);

                w.LastBranchPickerEntries.First(e => e.Title == "feature/login").Execute();
                Assert.True(Pump(() => !w.IsGitOperationRunning && w.CurrentBranchStatus?.Branch == "feature/login"), w.LastGitOperationMessage);
                Assert.Contains("feature/login", w.StatusBarBranchText);
                Assert.False(w.CurrentBranchStatus.HasUpstream);
                Assert.Contains("Publish", ((global::Avalonia.Controls.TextBlock)w.StatusSyncButton.Content).Text);

                // Detached HEAD at a tag is shown with its short hash.
                w.OpenBranchPicker();
                Assert.True(Pump(() => w.LastBranchPickerEntries.Any(e => e.Title == "v1.0")));
                w.LastBranchPickerEntries.First(e => e.Title == "v1.0").Execute();
                Assert.True(Pump(() => !w.IsGitOperationRunning && w.CurrentBranchStatus != null && w.CurrentBranchStatus.IsDetached));
                Assert.Contains(GitCli.ShortHash(w.CurrentBranchStatus.Oid), w.StatusBarBranchText);

                // Create a branch from HEAD through the same path as the prompt.
                w.CreateBranch("from detached");
                Assert.True(Pump(() => !w.IsGitOperationRunning && w.CurrentBranchStatus?.Branch == "from-detached"));
            }
            finally { Close(w); }

            // B: fetch shows 1 incoming, Sync pulls it, push publishes a local commit.
            var wb = Open(b);
            try
            {
                wb.GitFetch();
                Assert.True(Pump(() => !wb.IsGitOperationRunning && wb.CurrentBranchStatus != null && wb.CurrentBranchStatus.Behind == 1), wb.LastGitOperationMessage);
                Assert.Contains("1↓ 0↑", ((global::Avalonia.Controls.TextBlock)wb.StatusSyncButton.Content).Text);
                TempGitRepos.Write(b, "local.txt", "l\n");
                TempGitRepos.Commit(b, "local");
                wb.RefreshBranchStatusAsync();
                Assert.True(Pump(() => wb.CurrentBranchStatus != null && wb.CurrentBranchStatus.Ahead == 1));
                wb.GitPush();
                Assert.True(Pump(() => !wb.IsGitOperationRunning && wb.LastGitOperationResult != null));
                Assert.Equal(GitErrorKind.NonFastForward, wb.LastGitOperationResult.Error);
                wb.CurrentSettings.GitRebaseWhenSync = true;
                wb.GitSync();
                Assert.True(Pump(() => !wb.IsGitOperationRunning && wb.LastGitOperationResult != null && wb.LastGitOperationResult.Success), wb.LastGitOperationMessage);
                Assert.True(Pump(() => wb.CurrentBranchStatus != null && wb.CurrentBranchStatus.Ahead == 0 && wb.CurrentBranchStatus.Behind == 0));
                Assert.Contains("local", TempGitRepos.Git(bare, "log", "--format=%s", "main"));
            }
            finally { Close(wb); }
        }

        [AvaloniaFact]
        public void ConflictLens_AcceptIncoming_ResolvesAndScmShowsMergeGroup()
        {
            string a = repos.Init("conflict");
            TempGitRepos.Write(a, "f.txt", "one\ntwo\nthree\n");
            TempGitRepos.Commit(a, "base");
            TempGitRepos.Git(a, "checkout", "-q", "-b", "feature");
            TempGitRepos.Write(a, "f.txt", "one\nTWO-feature\nthree\n");
            TempGitRepos.Commit(a, "feature");
            TempGitRepos.Git(a, "checkout", "-q", "main");
            TempGitRepos.Write(a, "f.txt", "one\nTWO-main\nthree\n");
            TempGitRepos.Commit(a, "main");
            GitProcess.Run(a, new[] { "merge", "feature" }, 30000, CancellationToken.None, null, null);

            var w = Open(a);
            try
            {
                w.RefreshGitStatus();
                Assert.True(Pump(() => w.ScmMergeList.Children.Count == 1));
                Assert.Equal(1, w.MergeConflictFileCount);
                Assert.True(w.ScmMergeList.IsVisible);

                string path = Path.Combine(a, "f.txt");
                var tab = w.OpenFileInTab(path, preview: false);
                var blocks = ConflictDecorations.Rescan(tab);
                Assert.Single(blocks);
                int start = blocks[0].StartLine;
                Assert.Equal(ConflictLineKind.CurrentHeader, ConflictDecorations.KindAt(tab, start));
                Assert.Equal(ConflictLineKind.Incoming, ConflictDecorations.KindAt(tab, blocks[0].SeparatorLine + 1));

                var ann = w.PrimaryEditor.LineAnnotationProvider(tab, start);
                Assert.NotNull(ann);
                Assert.Equal(4, ann.Segments.Length);
                Assert.Equal("Accept Incoming Change", ann.GetSegmentParts()[1].Text);

                // Next Conflict moves the caret to the block.
                tab.CursorLine = 0;
                w.GoToNextConflict(true);
                Assert.Equal(start, tab.CursorLine);

                Assert.True(w.ClickConflictLensSegment(tab, start, 1));
                Assert.Equal("one\nTWO-feature\nthree\n", tab.Content.EndsWith("\n") ? tab.Content : tab.Content + "\n");
                Assert.True(tab.IsDirty);
                Assert.Empty(ConflictDecorations.Blocks(tab));

                // Undo restores the conflict; Accept Both keeps both sides.
                tab.Undo();
                Assert.Single(ConflictDecorations.Rescan(tab));
                Assert.True(w.AcceptConflictAtCursor(ConflictResolution.Both));
                Assert.Contains("TWO-main\nTWO-feature", tab.Content);

                // Save and stage = resolved.
                w.SaveActiveTab();
                var entry = w.GitEntries.First(e => e.FullPath.EndsWith("f.txt"));
                w.StageResolvedFile(entry);
                Assert.True(Pump(() => w.MergeConflictFileCount == 0 && w.ScmStagedList.Children.Count == 1));
            }
            finally { Close(w); }
        }

        [AvaloniaFact]
        public void GutterHunk_PopupStageAndRevert_AndStageSelectedRanges()
        {
            string a = repos.Init("hunks");
            string content = string.Join("\n", Enumerable.Range(1, 20).Select(i => "line " + i)) + "\n";
            TempGitRepos.Write(a, "f.txt", content);
            TempGitRepos.Commit(a, "base");
            var w = Open(a);
            try
            {
                var tab = w.OpenFileInTab(Path.Combine(a, "f.txt"), preview: false);
                tab.Lines[1] = "line 2 changed";
                tab.Lines[15] = "line 16 changed";
                w.PrimaryEditor.Canvas.CommitExternalEdit();
                Assert.True(AvaloniaEditorControl.HasGutterDiffMarker(tab, 1));

                w.ShowHunkPopupAtLine(15);
                Assert.True(Pump(() => w.HunkPopup != null && w.HunkPopup.IsVisible && w.HunkPopupHunk != null));
                Assert.Equal(15, w.HunkPopupHunk.NewStart);

                // Stage only the second change.
                w.StageChangeAt(tab, 15, 15, true);
                Assert.True(Pump(() => w.LastGitOperationResult != null && w.LastGitOperationResult.Success), w.LastGitOperationMessage);
                string index = TempGitRepos.Git(a, "show", ":f.txt");
                Assert.Contains("line 16 changed", index);
                Assert.DoesNotContain("line 2 changed", index);

                // Revert the first change in the editor (undoable).
                tab.CursorLine = 1;
                w.RevertChangeAtCursor();
                Assert.True(Pump(() => tab.Lines[1] == "line 2"));
                Assert.Equal("line 16 changed", tab.Lines[15]);

                // The staged change now offers "Unstage Change" from the gutter.
                w.HideHunkPopup();
                w.ShowHunkPopupAtLine(15);
                Assert.True(Pump(() => w.HunkPopup.IsVisible && w.HunkPopupIsStaged));
                w.UnstageChangeAtLine(tab, 15);
                Assert.True(Pump(() => !TempGitRepos.Git(a, "show", ":f.txt").Contains("line 16 changed")));
                w.StageChangeAt(tab, 15, 15, true);
                Assert.True(Pump(() => TempGitRepos.Git(a, "show", ":f.txt").Contains("line 16 changed")));

                // Stage Selected Ranges on a new line.
                tab.Lines.Insert(5, "inserted");
                w.PrimaryEditor.Canvas.CommitExternalEdit();
                tab.CursorLine = 5;
                tab.ClearSelection();
                w.StageSelectedRanges();
                Assert.True(Pump(() => TempGitRepos.Git(a, "show", ":f.txt").Contains("inserted")));
            }
            finally { Close(w); }
        }

        [AvaloniaFact]
        public void RunBuildTask_Shortcut_RunsMakeTarget_AndProblemsAppear()
        {
            if (!File.Exists("/usr/bin/make") && !File.Exists("/usr/local/bin/make")) return;
            string ws = repos.Path_("taskws");
            Directory.CreateDirectory(ws);
            File.WriteAllText(Path.Combine(ws, "main.c"), "int main(void) {\n  return 0\n}\n");
            File.WriteAllText(Path.Combine(ws, "util.c"), "void f(void) {}\n");
            File.WriteAllText(Path.Combine(ws, "Makefile"), "all:\n\t@echo 'main.c:2:11: error: expected semicolon'\n\t@echo 'util.c:1:6: warning: unused function'\n\t@exit 2\nclean:\n\t@echo clean\n");
            var w = Open(ws);
            try
            {
                var tab = w.OpenFileInTab(Path.Combine(ws, "main.c"), preview: false);
                Assert.True(w.SimulateShortcut(Key.B, KeyModifiers.Meta | KeyModifiers.Shift));
                Assert.True(Pump(() => w.LastTaskExecution != null && !w.LastTaskExecution.IsRunning && w.LastTaskExecution.EndedUtc != default(DateTime)));
                var ex = w.LastTaskExecution;
                Assert.Equal("all", ex.Task.Label);
                Assert.Equal("make", ex.Task.Source);
                Assert.Equal(2, ex.ExitCode);
                Assert.Contains("expected semicolon", ex.Transcript);
                Assert.True(Pump(() => tab.Diagnostics.Any(d => d.IsTaskProblem && d.Line == 2 && d.Column == 11)));
                // util.c is not open: listed in the Problems panel as an extra item.
                Assert.True(Pump(() => w.ProblemsPanelTaskItems.Any(d => d.FilePath.EndsWith("util.c") && d.Severity == "Warning")));
                Assert.True(w.TerminalSessions.Any(t => t.Title == "Task - all"), string.Join(",", w.TerminalSessions.Select(t => t.Title)));

                // Rerun after the fix clears the old problems.
                File.WriteAllText(Path.Combine(ws, "Makefile"), "all:\n\t@echo ok\n");
                w.RerunLastTask();
                Assert.True(Pump(() => w.LastTaskExecution != ex && w.LastTaskExecution.EndedUtc != default(DateTime)));
                Assert.Equal(0, w.LastTaskExecution.ExitCode);
                Assert.True(Pump(() => !tab.Diagnostics.Any(d => d.IsTaskProblem) && w.ProblemsPanelTaskItems.Count == 0));
                Assert.Equal(1, w.TerminalSessions.Count(t => t.Title == "Task - all")); // terminal reused

                // Run Task picker lists detected tasks; Terminate stops a long-running one.
                w.OpenRunTaskPicker();
                Assert.Contains(w.LastTaskPickerEntries, e => e.Title == "all" && e.Category == "make");
                var sleeper = new TaskDefinition { Label = "sleepy", Command = "sleep 30" };
                var running = w.RunTask(sleeper);
                Assert.True(running.IsRunning);
                w.TerminateTask();
                Assert.True(Pump(() => !running.IsRunning));
                Assert.True(running.WasTerminated);
            }
            finally { Close(w); }
        }

        /// <summary>
        /// Offline Mode blocks network remotes (recorded in the ledger) while local bare remotes keep working. Runs as an
        /// AvaloniaFact so ledger events raised to open windows are processed inside the headless session.
        /// </summary>
        [AvaloniaFact]
        public void OfflineMode_BlocksNetworkRemotes_LocalRemotesStillWork()
        {
            string bare = repos.InitBare("ledger-remote.git");
            string a = repos.Clone(bare, "ledger-a");
            TempGitRepos.Write(a, "f.txt", "x\n");
            TempGitRepos.Commit(a, "init");
            TempGitRepos.Git(a, "push", "-q", "-u", "origin", "main");
            TempGitRepos.Git(a, "remote", "add", "web", "https://example.invalid/never/contacted.git");
            try
            {
                NetworkLedger.SetOfflineMode(true);
                Dispatcher.UIThread.RunJobs();
                Assert.True(GitRemoteOps.Fetch(a, false, false, CancellationToken.None, null).Success); // local bare remote
                var blocked = GitRemoteOps.Publish(a, "web", CancellationToken.None, null);
                Assert.False(blocked.Success);
                Assert.Equal(GitErrorKind.Network, blocked.Error);
                Assert.Contains("Offline Mode", blocked.Message);
                Assert.Contains(NetworkLedger.Snapshot(), e => e.Blocked && e.Feature == "Git" && e.Url.Contains("example.invalid"));
            }
            finally
            {
                NetworkLedger.SetOfflineMode(false);
                Dispatcher.UIThread.RunJobs();
            }
        }

        [AvaloniaFact]
        public void FormatOnSave_UsesTheRunningLanguageServer()
        {
            string line = LanguageServerManagerTests.FakeServerCommandLine(out bool available);
            if (!available) return; // no python3
            string ws = repos.Path_("fmtws");
            Directory.CreateDirectory(ws);
            string main = Path.Combine(ws, "main.py");
            File.WriteAllText(main, "x = 1\n");
            MainWindow.LanguageServersEnabledOverride = true;
            MainWindow.LanguageServerSettingsOverride = () => LanguageServerUserSettings.Parse("python = " + line, false);
            MainWindow w = null;
            try
            {
                w = Open(ws);
                var tab = w.OpenFileInTab(main, preview: false);
                Assert.True(Pump(() => w.LanguageServerStatusText == "{ } fake_lsp_server"));
                w.CurrentSettings.FormatOnSave = true;
                tab.Lines[0] = "x = 2   ";
                w.PrimaryEditor.Canvas.CommitExternalEdit();
                Assert.True(w.SaveActiveTab());
                Assert.True(Pump(() => File.ReadAllText(main) == "x = 2\n"), "disk: [" + File.ReadAllText(main) + "]");
                Assert.False(tab.IsDirty);
            }
            finally
            {
                if (w != null) Close(w);
                MainWindow.LanguageServersEnabledOverride = null;
                MainWindow.LanguageServerSettingsOverride = null;
            }
        }

        [AvaloniaFact]
        public void SaveActions_TrimAndFinalNewline_AndAutoSaveAfterDelay()
        {
            string ws = repos.Path_("savews");
            Directory.CreateDirectory(ws);
            string md = Path.Combine(ws, "notes.md");
            string py = Path.Combine(ws, "app.py");
            File.WriteAllText(md, "title  \nnext line\n");
            File.WriteAllText(py, "x = 1\n");
            var w = Open(ws);
            try
            {
                // Defaults: nothing changes.
                var tab = w.OpenFileInTab(py, preview: false);
                tab.Lines[0] = "x = 2   ";
                w.PrimaryEditor.Canvas.CommitExternalEdit();
                w.SaveActiveTab();
                Assert.Equal("x = 2   \n", File.ReadAllText(py));

                w.CurrentSettings.TrimTrailingWhitespaceOnSave = true;
                w.CurrentSettings.InsertFinalNewlineOnSave = true;
                tab.Lines.Clear();
                tab.Lines.AddRange(new[] { "s = \"\"\"keep   ", "end\"\"\"   ", "y = 3  " });
                tab.HasFinalNewline = false;
                w.PrimaryEditor.Canvas.CommitExternalEdit();
                w.SaveActiveTab();
                Assert.Equal("s = \"\"\"keep   \nend\"\"\"\ny = 3\n", File.ReadAllText(py));

                var mdTab = w.OpenFileInTab(md, preview: false);
                mdTab.Lines[1] = "next line   ";
                w.PrimaryEditor.Canvas.CommitExternalEdit();
                w.SaveActiveTab();
                Assert.Equal("title  \nnext line\n", File.ReadAllText(md));

                // Auto save afterDelay: edits are saved once the delay passes (flushed here; timers don't tick headless).
                w.CurrentSettings.SetAutoSaveMode("afterDelay");
                mdTab.Lines[0] = "changed title";
                w.PrimaryEditor.Canvas.CommitExternalEdit();
                w.RefreshTabStrip();
                w.FlushPendingAutoSave();
                Assert.True(Pump(() => File.ReadAllText(md).StartsWith("changed title")));

                // onFocusChange: switching editors saves the one being left.
                w.CurrentSettings.SetAutoSaveMode("onFocusChange");
                mdTab.Lines[0] = "focus change";
                w.PrimaryEditor.Canvas.CommitExternalEdit();
                w.ActivateTab(tab);
                Assert.StartsWith("focus change", File.ReadAllText(md));
            }
            finally { Close(w); }
        }

        [AvaloniaFact]
        public void ScmView_GroupsAmendAndHistory()
        {
            string a = repos.Init("scm");
            TempGitRepos.Write(a, "tracked.txt", "1\n");
            TempGitRepos.Write(a, "staged.txt", "s\n");
            TempGitRepos.Commit(a, "first message");
            TempGitRepos.Write(a, "tracked.txt", "2\n");
            TempGitRepos.Write(a, "staged.txt", "s2\n");
            TempGitRepos.Git(a, "add", "staged.txt");
            TempGitRepos.Write(a, "new.txt", "n\n");
            var w = Open(a);
            try
            {
                w.RefreshGitStatus();
                Assert.True(Pump(() => w.ScmStagedList.Children.Count == 1 && w.ScmChangesList.Children.Count == 1 && w.ScmUntrackedList.Children.Count == 1),
                    w.ScmStagedList.Children.Count + "/" + w.ScmChangesList.Children.Count + "/" + w.ScmUntrackedList.Children.Count + " entries: " + string.Join(";", w.GitEntries.Select(e => e.IndexStatus + "" + e.WorkingTreeStatus + " " + e.RelativePath)));
                Assert.False(w.ScmMergeList.IsVisible);

                // Amend: prefilled with the last message, replaces the commit instead of adding one.
                w.ScmAmendCheckBox.IsChecked = true;
                Assert.True(Pump(() => w.ScmCommitInput.Text == "first message"), "text=[" + w.ScmCommitInput.Text + "]");
                w.ScmCommitInput.Text = "amended message";
                w.SwitchSidebarView("git");
                Dispatcher.UIThread.RunJobs();
                w.ScmCommitInput.Focus();
                Dispatcher.UIThread.RunJobs();
                Assert.True(w.ScmCommitInput.IsFocused);
                Assert.True(w.SimulateShortcut(Key.Enter, KeyModifiers.Meta));
                Assert.True(Pump(() => !w.IsGitOperationRunning && w.LastGitOperationResult != null && w.LastGitOperationResult.Success), w.LastGitOperationMessage);
                Assert.Equal("1", TempGitRepos.Git(a, "rev-list", "--count", "HEAD").Trim());
                Assert.Equal("amended message", GitRemoteOps.GetLastCommitMessage(a));
                Assert.False(w.ScmAmendCheckBox.IsChecked == true);
                Assert.Equal("amended message", w.CurrentSettings.GitCommitMessageHistory[0]);
            }
            finally { Close(w); }
        }
    }
}
