using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using codeLess.Studio;

namespace codeLess.AvaloniaApp
{
    /// <summary>
    /// Git daily-driver parity with VS Code on macOS/Linux: status bar branch picker + sync (ahead/behind), Fetch / Pull /
    /// Push / Publish / Sync with progress and cancel, merge-conflict decorations with Accept Current / Incoming / Both /
    /// Compare lenses and Next/Previous Conflict, gutter-bar Stage / Revert Change popup, Stage Selected Ranges, and the
    /// grouped Source Control view (merge / staged / changes / untracked) with Ctrl/Cmd+Enter, amend and message history.
    /// Shared logic lives in codeLess.Core (GitRemoteOps, GitConflictParser, GitHunkDiff). Hooks: InitializeWorkbenchParity,
    /// HandleParityShortcut, ProvideParityAnnotation, HandleParityAnnotationClick, RebuildScmGroupsUi.
    /// </summary>
    public partial class MainWindow
    {
        private UiPost parityUi;
        private Button statusSyncBtn;
        private CancellationTokenSource gitOpCts;
        private string gitOpTitle;
        private GitBranchStatus branchStatus;
        private string branchStatusRepo;
        private bool branchStatusRefreshRunning, branchStatusRefreshPending;
        private DispatcherTimer autoFetchTimer;
        private TextBlock scmMergeHeader, scmUntrackedHeader;
        private StackPanel scmMergeListStack, scmUntrackedListStack;
        private CheckBox scmAmendCheck;
        private CommitMessageHistory commitHistory;
        private Border hunkPopup;
        private GitDiffHunk hunkPopupHunk;
        private EditorTabBuffer hunkPopupBuffer;
        private readonly Dictionary<string, EditorLineAnnotation> conflictLensCache = new Dictionary<string, EditorLineAnnotation>(StringComparer.Ordinal);

        /// <summary>Last branch status read for the status bar (tests / automation).</summary>
        public GitBranchStatus CurrentBranchStatus => branchStatus;
        public Button StatusSyncButton => statusSyncBtn;
        public bool IsGitOperationRunning => gitOpCts != null;
        public string LastGitOperationMessage { get; private set; } = "";
        public GitOperationResult LastGitOperationResult { get; private set; }
        public List<PaletteEntryItem> LastBranchPickerEntries { get; private set; } = new List<PaletteEntryItem>();
        public Border HunkPopup => hunkPopup;
        public GitDiffHunk HunkPopupHunk => hunkPopupHunk;
        public CheckBox ScmAmendCheckBox => scmAmendCheck;
        public StackPanel ScmMergeList => scmMergeListStack;
        public StackPanel ScmStagedList => scmStagedListStack;
        public StackPanel ScmChangesList => scmChangesListStack;
        public StackPanel ScmUntrackedList => scmUntrackedListStack;
        public TextBox ScmCommitInput => scmCommitInput;

        private bool parityWindowClosed;

        /// <summary>Posts background results to the UI thread; dropped once the window has closed.</summary>
        private void PostUi(Action a)
        {
            var ui = parityUi;
            if (ui == null || parityWindowClosed) return;
            ui.Post(() => { if (!parityWindowClosed) a(); });
        }

        private void InitializeWorkbenchParity()
        {
            parityUi = UiPost.CaptureCurrent();
            Closed += (s, e) => { parityWindowClosed = true; try { gitOpCts?.Cancel(); } catch { } autoFetchTimer?.Stop(); };
            if (settings.GitCommitMessageHistory == null) settings.GitCommitMessageHistory = new List<string>();
            commitHistory = new CommitMessageHistory(settings.GitCommitMessageHistory);

            foreach (var ed in new[] { PrimaryEditor, SplitEditor })
            {
                var editor = ed;
                editor.GutterDiffClicked += (buf, line, rect) => ShowHunkPopup(editor, buf, line, rect);
                editor.CaretMoved += buf => { if (hunkPopup != null && hunkPopup.IsVisible && !ReferenceEquals(buf, hunkPopupBuffer)) HideHunkPopup(); };
            }

            // Extra SCM groups (merge conflicts first, untracked last) around the existing staged / changes lists.
            if (scmStagedHeader?.Parent is StackPanel lists)
            {
                scmMergeHeader = new TextBlock { Text = "▾ MERGE CHANGES (0)", FontSize = 10.5, FontWeight = FontWeight.Bold, IsVisible = false };
                scmMergeListStack = new StackPanel { Spacing = 2, IsVisible = false };
                scmUntrackedHeader = new TextBlock { Text = "▾ UNTRACKED CHANGES (0)", FontSize = 10.5, FontWeight = FontWeight.Bold, Margin = new Thickness(0, 6, 0, 0), IsVisible = false };
                scmUntrackedListStack = new StackPanel { Spacing = 2, IsVisible = false };
                lists.Children.Insert(0, scmMergeListStack);
                lists.Children.Insert(0, scmMergeHeader);
                lists.Children.Add(scmUntrackedHeader);
                lists.Children.Add(scmUntrackedListStack);
            }
            if (scmCommitInput?.Parent is StackPanel top)
            {
                scmAmendCheck = new CheckBox { Content = "Amend last commit", FontSize = 11 };
                ToolTip.SetTip(scmAmendCheck, "Commit (Amend): replace the last commit. An empty message keeps the previous one.");
                scmAmendCheck.IsCheckedChanged += (s, e) => OnAmendToggled();
                top.Children.Add(scmAmendCheck);
                scmCommitInput.AddHandler(KeyDownEvent, OnCommitInputKeyDown, Avalonia.Interactivity.RoutingStrategies.Tunnel);
                UpdateCommitWatermark();
            }

            autoFetchTimer = new DispatcherTimer();
            autoFetchTimer.Tick += (s, e) => RunAutoFetch();
            ApplyGitParitySettings();
            InitializeTasksAndSaveParity();
        }

        /// <summary>Re-applies settings that drive timers (auto fetch / auto save).</summary>
        public void ApplyGitParitySettings()
        {
            if (autoFetchTimer == null) return;
            autoFetchTimer.Stop();
            if (settings.GitAutoFetch)
            {
                autoFetchTimer.Interval = TimeSpan.FromSeconds(settings.EffectiveAutoFetchPeriodSeconds);
                autoFetchTimer.Start();
            }
        }

        private string ParityRepo()
        {
            string r = null;
            if (activeTab != null && !string.IsNullOrEmpty(activeTab.FilePath)) r = GitCli.FindRepoRoot(activeTab.FilePath);
            if (r == null && !string.IsNullOrEmpty(workspaceRoot)) r = GitCli.FindRepoRoot(workspaceRoot);
            return r;
        }

        private bool IsMacUi => MacKeymap.IsMacOS;

        // ------------------------------------------------------------------------------------------------
        // Status bar: branch + sync
        // ------------------------------------------------------------------------------------------------

        /// <summary>Called after every git status refresh: reads branch / upstream / ahead-behind in the background.</summary>
        public void RefreshBranchStatusAsync()
        {
            if (parityUi == null) return;
            string repo = ParityRepo();
            if (repo == null)
            {
                branchStatus = null;
                branchStatusRepo = null;
                UpdateSyncStatusItem();
                return;
            }
            if (branchStatusRefreshRunning) { branchStatusRefreshPending = true; return; }
            branchStatusRefreshRunning = true;
            Task.Run(() =>
            {
                GitBranchStatus st = null;
                try { st = GitRemoteOps.GetBranchStatus(repo); } catch { }
                PostUi(() =>
                {
                    branchStatusRefreshRunning = false;
                    branchStatus = st;
                    branchStatusRepo = repo;
                    UpdateSyncStatusItem();
                    if (branchStatusRefreshPending) { branchStatusRefreshPending = false; RefreshBranchStatusAsync(); }
                });
            });
        }

        private void UpdateSyncStatusItem()
        {
            if (statusBranchBtn == null) return;
            var st = branchStatus;
            if (st != null && (st.Branch != null || st.IsDetached))
            {
                SetStatusBranch(st.DisplayName);
                ToolTip.SetTip(statusBranchBtn, (st.IsDetached ? "Detached HEAD at " + GitCli.ShortHash(st.Oid) : st.Branch) + " — Checkout Branch/Tag...");
            }
            UpdateCommitWatermark();
            if (statusSyncBtn == null) return;
            if (gitOpCts != null)
            {
                statusSyncBtn.IsVisible = true;
                statusSyncBtn.Content = SyncLabel("⟳ " + (gitOpTitle ?? "Git") + "…");
                ToolTip.SetTip(statusSyncBtn, gitOpTitle + " in progress — click to cancel");
                return;
            }
            if (st == null || st.IsDetached || st.Branch == null)
            {
                statusSyncBtn.IsVisible = false;
                return;
            }
            statusSyncBtn.IsVisible = true;
            if (!st.HasUpstream)
            {
                statusSyncBtn.Content = SyncLabel("☁ Publish");
                ToolTip.SetTip(statusSyncBtn, "Publish Branch (push " + st.Branch + " and set its upstream)");
            }
            else
            {
                string counts = st.SyncText;
                statusSyncBtn.Content = SyncLabel(counts.Length == 0 ? "⟳" : "⟳ " + counts);
                ToolTip.SetTip(statusSyncBtn, counts.Length == 0
                    ? "Synchronize Changes with " + st.Upstream
                    : "Synchronize Changes: pull " + st.Behind + " and push " + st.Ahead + " commit(s) with " + st.Upstream);
            }
        }

        private static Control SyncLabel(string text) =>
            new LiteralTextBlock(text) { FontSize = UiTokens.FontSmall, VerticalAlignment = VerticalAlignment.Center };

        private void UpdateCommitWatermark()
        {
            if (scmCommitInput == null) return;
            string branch = branchStatus?.Branch ?? (branchStatus != null && branchStatus.IsDetached ? GitCli.ShortHash(branchStatus.Oid) : null);
            string keys = IsMacUi ? "⌘Enter" : "Ctrl+Enter";
            bool amend = scmAmendCheck?.IsChecked == true;
            // Long branch names (worktrees) are shortened so the placeholder fits the two-line box instead of being cut off.
            string shown = branch != null && branch.Length > 22 ? branch.Substring(0, 21) + "…" : branch;
            scmCommitInput.Watermark = "Message (" + keys + " to " + (amend ? "amend" : "commit") + (string.IsNullOrEmpty(shown) ? "" : " on '" + shown + "'") + ")";
            ToolTip.SetTip(scmCommitInput, string.IsNullOrEmpty(branch) ? null : "Commit on '" + branch + "'");
            // The banner used a HEAD-file read that falls back to "main" in linked worktrees; prefer git's own answer.
            if (scmBranchBanner != null && !string.IsNullOrEmpty(branchStatus?.Branch))
                scmBranchBanner.Text = "Branch: " + branchStatus.Branch + " (" + currentGitEntries.Count + " changed)";
        }

        private void OnSyncStatusClicked()
        {
            if (gitOpCts != null) { CancelGitOperation(); return; }
            if (branchStatus != null && !branchStatus.HasUpstream) GitPublishBranch();
            else GitSync();
        }

        // ------------------------------------------------------------------------------------------------
        // Remote operations (async, progress, cancel)
        // ------------------------------------------------------------------------------------------------

        /// <summary>Runs a git operation off the UI thread with progress in the status bar and a cancel button.</summary>
        public bool RunGitOperation(string title, Func<string, CancellationToken, Action<string>, GitOperationResult> op, Action<GitOperationResult> after = null, bool quiet = false)
        {
            string repo = ParityRepo();
            if (repo == null) { SetStatusMessage("No git repository found for the open folder."); return false; }
            if (gitOpCts != null) { if (!quiet) SetStatusMessage("Git: '" + gitOpTitle + "' is still running (click the status bar item to cancel)."); return false; }
            var cts = new CancellationTokenSource();
            gitOpCts = cts;
            gitOpTitle = title;
            UpdateSyncStatusItem();
            if (!quiet) SetStatusMessage("Git: " + title + "…");
            DateTime lastProgress = DateTime.MinValue;
            Action<string> progress = line =>
            {
                if (quiet || (DateTime.UtcNow - lastProgress).TotalMilliseconds < 200) return;
                lastProgress = DateTime.UtcNow;
                PostUi(() => { if (gitOpCts == cts) SetStatusMessage("Git " + title + ": " + line.Trim()); });
            };
            Task.Run(() =>
            {
                GitOperationResult res;
                try { res = op(repo, cts.Token, progress); }
                catch (Exception ex) { res = new GitOperationResult { Error = GitErrorKind.Other, Message = title + " failed: " + ex.Message }; }
                PostUi(() =>
                {
                    if (gitOpCts == cts) { gitOpCts = null; gitOpTitle = null; }
                    cts.Dispose();
                    LastGitOperationResult = res;
                    LastGitOperationMessage = res.Message ?? "";
                    if (!string.IsNullOrWhiteSpace(res.Details)) AppendOutputLog("[Git " + title + "] " + res.Details.Trim());
                    if (res.Success) { if (!quiet) SetStatusMessage(res.Message); }
                    else if (!quiet || res.Error == GitErrorKind.AuthenticationFailed) ReportGitOperationFailure(title, res);
                    else AppendOutputLog("[Git " + title + "] " + res.Message);
                    after?.Invoke(res);
                    RefreshGitStatusInBackground();
                    RefreshBranchStatusAsync();
                    RefreshOpenBuffersFromDiskAfterGit();
                });
            });
            return true;
        }

        private void ReportGitOperationFailure(string title, GitOperationResult res)
        {
            SetStatusMessage(res.Message);
            if (!AvaloniaTerminalEmulatorControl.IsDesktopRuntime || res.Error == GitErrorKind.Cancelled) return;
            switch (res.Error)
            {
                case GitErrorKind.AuthenticationFailed:
                    ShowConfirmModal("Git: Authentication Required", res.Message, "Open Terminal", () => { SwitchBottomTab("terminal"); EnsureInitialTerminalSession(); });
                    break;
                case GitErrorKind.NonFastForward:
                    ShowConfirmModal("Git: Push Rejected", res.Message, "Pull", GitPull);
                    break;
                case GitErrorKind.NoUpstream:
                    ShowConfirmModal("Git: No Upstream Branch", res.Message, "Publish Branch", GitPublishBranch);
                    break;
                default:
                    ShowMessageModal("Git: " + title, res.Message + (string.IsNullOrWhiteSpace(res.Details) ? "" : "\n\n" + GitErrorClassifier.FirstLine(res.Details)));
                    break;
            }
        }

        /// <summary>After pull / checkout, reload clean editors whose files changed on disk.</summary>
        private void RefreshOpenBuffersFromDiskAfterGit()
        {
            foreach (var t in openTabs.ToList())
            {
                if (t == null || t.IsDirty || string.IsNullOrEmpty(t.FilePath)) continue;
                try { ReloadOpenTabIfChangedOnDisk(t.FilePath); } catch { }
            }
        }

        public void CancelGitOperation()
        {
            var c = gitOpCts;
            if (c == null) { SetStatusMessage("No git operation is running."); return; }
            try { c.Cancel(); } catch { }
            SetStatusMessage("Git: cancelling " + gitOpTitle + "…");
        }

        public void GitFetch() => RunGitOperation("Fetch", (repo, ct, p) => GitRemoteOps.Fetch(repo, false, false, ct, p));
        public void GitFetchPrune() => RunGitOperation("Fetch (Prune)", (repo, ct, p) => GitRemoteOps.Fetch(repo, false, true, ct, p));
        public void GitFetchAll() => RunGitOperation("Fetch From All Remotes", (repo, ct, p) => GitRemoteOps.Fetch(repo, true, false, ct, p));
        public void GitPull() => RunGitOperation("Pull", (repo, ct, p) => GitRemoteOps.Pull(repo, settings.GitRebaseWhenSync, ct, p));
        public void GitPullRebase() => RunGitOperation("Pull (Rebase)", (repo, ct, p) => GitRemoteOps.Pull(repo, true, ct, p));
        public void GitPush() => RunGitOperation("Push", (repo, ct, p) => GitRemoteOps.Push(repo, ct, p));
        public void GitSync() => RunGitOperation("Sync", (repo, ct, p) => GitRemoteOps.Sync(repo, settings.GitRebaseWhenSync, ct, p));

        public void GitPublishBranch()
        {
            string repo = ParityRepo();
            if (repo == null) { SetStatusMessage("No git repository found for the open folder."); return; }
            var remotes = GitRemoteOps.ListRemotes(repo);
            string pick = GitRemoteOps.PickPublishRemote(remotes);
            if (pick == null && remotes.Count > 1)
            {
                var entries = remotes.Select(r => new PaletteEntryItem
                {
                    Category = "Publish to",
                    Title = r,
                    Execute = () => RunGitOperation("Publish Branch", (rp, ct, p) => GitRemoteOps.Publish(rp, r, ct, p))
                }).ToList();
                CommandPalette.Open(">", entries, currentTheme?.Id);
                return;
            }
            RunGitOperation("Publish Branch", (rp, ct, p) => GitRemoteOps.Publish(rp, pick, ct, p));
        }

        private void RunAutoFetch()
        {
            if (!settings.GitAutoFetch || gitOpCts != null || NetworkLedger.OfflineMode) return;
            RunGitOperation("Auto Fetch", (repo, ct, p) => GitRemoteOps.Fetch(repo, false, false, ct, null), null, quiet: true);
        }

        // ------------------------------------------------------------------------------------------------
        // Branch picker
        // ------------------------------------------------------------------------------------------------

        /// <summary>Status bar branch click / "Git: Checkout to...": local + remote branches, tags, create branch.</summary>
        public void OpenBranchPicker()
        {
            string repo = ParityRepo();
            if (repo == null) { SwitchSidebarView("git"); SetStatusMessage("No git repository found for the open folder."); return; }
            SetStatusMessage("Loading branches…");
            Task.Run(() =>
            {
                List<GitRefInfo> refs;
                try { refs = GitRemoteOps.ListRefs(repo, true); } catch { refs = new List<GitRefInfo>(); }
                GitBranchStatus st = null;
                try { st = GitRemoteOps.GetBranchStatus(repo); } catch { }
                PostUi(() => ShowBranchPicker(repo, refs, st));
            });
        }

        private void ShowBranchPicker(string repo, List<GitRefInfo> refs, GitBranchStatus st)
        {
            var entries = new List<PaletteEntryItem>
            {
                new PaletteEntryItem { Category = "+", Title = "Create new branch...", Execute = () => PromptCreateBranch(null) },
                new PaletteEntryItem { Category = "+", Title = "Create new branch from...", Execute = () => PickStartPointForNewBranch(refs) }
            };
            if (st != null && st.IsDetached)
                entries.Add(new PaletteEntryItem { Category = "HEAD", Title = "(detached at " + GitCli.ShortHash(st.Oid) + ")", stringSubtitleOrPath = "Current", Execute = () => { } });
            foreach (var r in refs.Where(x => x.Kind == GitRefKind.Local))
                entries.Add(MakeRefEntry(repo, r, refs, r.IsHead ? "Current branch" : "Branch"));
            foreach (var r in refs.Where(x => x.Kind == GitRefKind.Remote))
                entries.Add(MakeRefEntry(repo, r, refs, "Remote branch"));
            foreach (var r in refs.Where(x => x.Kind == GitRefKind.Tag))
                entries.Add(MakeRefEntry(repo, r, refs, "Tag"));
            LastBranchPickerEntries = entries;
            SetStatusMessage("");
            CommandPalette.Open(">", entries, currentTheme?.Id);
        }

        private PaletteEntryItem MakeRefEntry(string repo, GitRefInfo r, List<GitRefInfo> refs, string kind)
        {
            string detail = (string.IsNullOrEmpty(r.Hash) ? "" : r.Hash) + (string.IsNullOrEmpty(r.RelativeDate) ? "" : "  ·  " + r.RelativeDate) + (string.IsNullOrEmpty(r.Subject) ? "" : "  ·  " + r.Subject);
            return new PaletteEntryItem
            {
                Id = "ref:" + r.FullName,
                Category = (r.IsHead ? "★ " : "") + kind,
                Title = r.ShortName,
                stringSubtitleOrPath = detail,
                Execute = () => CheckoutRef(r, refs)
            };
        }

        public void CheckoutRef(GitRefInfo target, List<GitRefInfo> refs)
        {
            if (target == null) return;
            if (target.IsHead) { SetStatusMessage("Already on " + target.ShortName + "."); return; }
            RunGitOperation("Checkout " + target.ShortName, (repo, ct, p) => GitRemoteOps.Checkout(repo, target, refs, ct));
        }

        private void PickStartPointForNewBranch(List<GitRefInfo> refs)
        {
            var entries = refs.Select(r => new PaletteEntryItem
            {
                Category = r.Kind == GitRefKind.Local ? "Branch" : r.Kind == GitRefKind.Remote ? "Remote branch" : "Tag",
                Title = r.ShortName,
                stringSubtitleOrPath = r.Hash,
                Execute = () => PromptCreateBranch(r.ShortName)
            }).ToList();
            CommandPalette.Open(">", entries, currentTheme?.Id);
        }

        public void PromptCreateBranch(string startPoint)
        {
            var view = AvaloniaDialogBuilder.BuildPromptInputView(
                startPoint == null ? "New branch name (created from HEAD):" : "New branch name (from " + startPoint + "):",
                "",
                currentTheme,
                name =>
                {
                    ModalHost.CloseModal();
                    CreateBranch(name, startPoint);
                },
                () => ModalHost.CloseModal(),
                out var tb);
            ModalHost.ShowModal("create-branch", "Git: Create Branch", view, 440);
            tb.Focus();
        }

        public void CreateBranch(string name, string startPoint = null)
        {
            if (string.IsNullOrWhiteSpace(name)) return;
            RunGitOperation("Create Branch", (repo, ct, p) => GitRemoteOps.CreateBranch(repo, name, startPoint, ct));
        }

        // ------------------------------------------------------------------------------------------------
        // Merge conflicts
        // ------------------------------------------------------------------------------------------------

        /// <summary>Annotation hook: the conflict lens on "&lt;&lt;&lt;&lt;&lt;&lt;&lt;" lines takes precedence over blame.</summary>
        private EditorLineAnnotation ProvideParityAnnotation(EditorTabBuffer buf, int line)
        {
            if (buf == null || buf.Lines == null || line < 0 || line >= buf.Lines.Count) return null;
            string l = buf.Lines[line];
            if (l == null || l.Length < 7 || l[0] != '<') return null;
            if (ConflictDecorations.KindAt(buf, line) != ConflictLineKind.CurrentHeader) return null;
            var block = GitConflictParser.BlockAt(ConflictDecorations.Blocks(buf), line);
            if (block == null) return null;
            string key = (buf.FilePath ?? buf.FileName ?? "") + "|" + block.StartLine + "|" + block.SeparatorLine + "|" + block.EndLine;
            if (!conflictLensCache.TryGetValue(key, out var ann))
            {
                if (conflictLensCache.Count > 200) conflictLensCache.Clear();
                ann = new EditorLineAnnotation
                {
                    Kind = EditorLineAnnotation.KindCodeLens,
                    IsAction = true,
                    Text = string.Join(EditorLineAnnotation.SegmentSeparator, GitConflictParser.LensSegments()),
                    Segments = GitConflictParser.LensSegments(),
                    Tag = block
                };
                conflictLensCache[key] = ann;
            }
            return ann;
        }

        private bool HandleParityAnnotationClick(AvaloniaEditorControl editor, EditorTabBuffer buf, EditorLineAnnotation ann, int line, Rect rect)
        {
            if (ann == null || !(ann.Tag is ConflictBlock)) return false;
            int seg = ann.SegmentIndex;
            if (seg < 0) return true;
            if (seg == 3) { CompareConflictAt(buf, line); return true; }
            var how = GitConflictParser.ResolutionForSegment(seg);
            if (how.HasValue) ResolveConflictAt(editor, buf, line, how.Value);
            return true;
        }

        public bool ResolveConflictAt(AvaloniaEditorControl editor, EditorTabBuffer buf, int line, ConflictResolution how)
        {
            if (buf == null) return false;
            var block = GitConflictParser.BlockAt(ConflictDecorations.Rescan(buf), line);
            if (block == null) { SetStatusMessage("No merge conflict at the cursor."); return false; }
            if (!ConflictDecorations.ResolveInBuffer(buf, block, how)) return false;
            conflictLensCache.Clear();
            editor = editor ?? PrimaryEditor;
            if (ReferenceEquals(editor.CurrentBuffer, buf)) editor.Canvas.CommitExternalEdit();
            else buf.IsDirty = true;
            editor.Canvas.InvalidateVisual();
            RefreshTabStrip();
            int left = ConflictDecorations.Blocks(buf).Count;
            SetStatusMessage(left == 0 ? "All conflicts resolved in " + buf.FileName + ". Save and stage the file to mark it resolved." : left + " conflict(s) left in " + buf.FileName + ".");
            return true;
        }

        /// <summary>
        /// Test / automation hook: clicks a segment of the conflict lens on <paramref name="line"/> through the same
        /// provider and click path the editor canvas uses.
        /// </summary>
        public bool ClickConflictLensSegment(EditorTabBuffer buf, int line, int segment)
        {
            var ann = PrimaryEditor.LineAnnotationProvider?.Invoke(buf, line);
            var parts = ann?.GetSegmentParts();
            if (parts == null || segment < 0 || segment >= parts.Length) return false;
            OnLineAnnotationClicked(PrimaryEditor, buf, parts[segment], line, new Rect(0, 0, 10, 10));
            return true;
        }

        public bool AcceptConflictAtCursor(ConflictResolution how) => activeTab != null && ResolveConflictAt(PrimaryEditor, activeTab, activeTab.CursorLine, how);

        public void AcceptAllConflicts(ConflictResolution how)
        {
            var buf = activeTab;
            if (buf == null) return;
            int n = 0;
            while (n < 10000)
            {
                var blocks = ConflictDecorations.Rescan(buf);
                if (blocks.Count == 0) break;
                if (!ConflictDecorations.ResolveInBuffer(buf, blocks[blocks.Count - 1], how)) break;
                n++;
            }
            conflictLensCache.Clear();
            PrimaryEditor.Canvas.CommitExternalEdit();
            RefreshTabStrip();
            SetStatusMessage(n == 0 ? "No merge conflicts in this file." : "Resolved " + n + " conflict(s).");
        }

        public void GoToNextConflict(bool forward)
        {
            var buf = activeTab;
            if (buf == null) return;
            var blocks = ConflictDecorations.Rescan(buf);
            var b = forward ? GitConflictParser.Next(blocks, buf.CursorLine) : GitConflictParser.Previous(blocks, buf.CursorLine);
            if (b == null) { SetStatusMessage("No merge conflicts in this file."); return; }
            PrimaryEditor.GoToLine(b.StartLine + 1);
            SetStatusMessage("Conflict " + (blocks.IndexOf(b) + 1) + " of " + blocks.Count);
        }

        public void CompareConflictAt(EditorTabBuffer buf, int line)
        {
            if (buf == null) return;
            var b = GitConflictParser.BlockAt(ConflictDecorations.Rescan(buf), line);
            if (b == null) { SetStatusMessage("No merge conflict at the cursor."); return; }
            string cur = string.Join("\n", GitConflictParser.CurrentLines(buf.Lines, b));
            string inc = string.Join("\n", GitConflictParser.IncomingLines(buf.Lines, b));
            string title = buf.FileName + ": Current (" + (b.CurrentLabel.Length > 0 ? b.CurrentLabel : "ours") + ") ↔ Incoming (" + (b.IncomingLabel.Length > 0 ? b.IncomingLabel : "theirs") + ")";
            var dv = new DiffView(title, cur, inc, "");
            OpenAuxDocument(new AuxPaneDocument { Id = "conflict:" + buf.FilePath + ":" + b.StartLine, Title = buf.FileName + " (Compare)", Tooltip = title, Content = dv, ApplyTheme = dv.ApplyTheme });
        }

        // ------------------------------------------------------------------------------------------------
        // Gutter hunks: Stage / Revert Change, Stage Selected Ranges
        // ------------------------------------------------------------------------------------------------

        private sealed class HunkContext
        {
            public string Repo, Rel, Index;
            public bool Binary, Untracked;
            public string Head;
            public List<GitDiffHunk> Hunks = new List<GitDiffHunk>();
        }

        private void ComputeHunksAsync(EditorTabBuffer buf, Action<HunkContext> done)
        {
            if (buf == null || string.IsNullOrEmpty(buf.FilePath)) { done(null); return; }
            var lines = new List<string>(buf.Lines ?? new List<string>());
            bool finalNl = buf.HasFinalNewline;
            string path = buf.FilePath;
            Task.Run(() =>
            {
                HunkContext ctx = null;
                try
                {
                    string repo = GitCli.FindRepoRoot(path);
                    string rel = repo != null ? GitCli.RepoRelativePath(repo, path) : null;
                    if (rel != null)
                    {
                        ctx = new HunkContext { Repo = repo, Rel = rel };
                        ctx.Index = GitHunkStaging.ReadIndexContent(repo, rel, out ctx.Binary);
                        if (ctx.Index == null && !ctx.Binary) ctx.Untracked = true;
                        else if (ctx.Index != null) ctx.Hunks = GitHunkDiff.ComputeHunks(ctx.Index, lines, finalNl);
                    }
                }
                catch { ctx = null; }
                PostUi(() => done(ctx));
            });
        }

        public void ShowHunkPopup(AvaloniaEditorControl editor, EditorTabBuffer buf, int line, Rect anchor)
        {
            ComputeHunksAsync(buf, ctx =>
            {
                if (ctx == null) { SetStatusMessage("This file is not inside a git repository."); return; }
                if (ctx.Binary) { SetStatusMessage("Binary file: changes can't be staged by hunk."); return; }
                GitDiffHunk h = ctx.Untracked ? null : GitHunkDiff.HunkAtLine(ctx.Hunks, line);
                if (h == null && !ctx.Untracked)
                {
                    // Already staged: offer "Unstage Change" for the HEAD-to-index hunk under this line.
                    var lines = new List<string>(buf.Lines);
                    bool finalNl = buf.HasFinalNewline;
                    Task.Run(() =>
                    {
                        GitDiffHunk sh = null;
                        try
                        {
                            ctx.Head = GitHunkStaging.ReadHeadContent(ctx.Repo, ctx.Rel);
                            sh = GitHunkStaging.FindStagedHunkAtEditorLine(ctx.Head, ctx.Index, lines, finalNl, line);
                        }
                        catch { sh = null; }
                        PostUi(() =>
                        {
                            if (sh == null) { SetStatusMessage("No change at this line."); return; }
                            BuildHunkPopup(editor, buf, ctx, sh, anchor, staged: true);
                        });
                    });
                    return;
                }
                BuildHunkPopup(editor, buf, ctx, h, anchor);
            });
        }

        /// <summary>Test / command entry: popup for the change at a line of the active editor.</summary>
        public void ShowHunkPopupAtLine(int line) => ShowHunkPopup(PrimaryEditor, activeTab, line, new Rect(0, 0, 6, 18));

        /// <summary>True when the popup shows an already staged change (Unstage instead of Stage / Revert).</summary>
        public bool HunkPopupIsStaged { get; private set; }

        private void BuildHunkPopup(AvaloniaEditorControl editor, EditorTabBuffer buf, HunkContext ctx, GitDiffHunk h, Rect anchor, bool staged = false)
        {
            HunkPopupIsStaged = staged;
            if (devToolsOverlay == null) return;
            var t = currentTheme;
            if (hunkPopup == null)
            {
                hunkPopup = new Border { CornerRadius = new CornerRadius(6), BorderThickness = new Thickness(1), Padding = new Thickness(10, 8), MaxWidth = 640, MinWidth = 320 };
                devToolsOverlay.Children.Add(hunkPopup);
            }
            hunkPopupHunk = h;
            hunkPopupBuffer = buf;
            hunkPopup.Background = AvaloniaColorHelper.ToBrush(t.BgElevated);
            hunkPopup.BorderBrush = AvaloniaColorHelper.ToBrush(t.BorderSubtle);
            hunkPopup.BoxShadow = new BoxShadows(new BoxShadow { Blur = 16, OffsetY = 4, Color = Color.FromArgb(90, 0, 0, 0) });
            var fg = AvaloniaColorHelper.ToBrush(t.FgPrimary);
            var sp = new StackPanel { Spacing = 6 };
            var head = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
            head.Children.Add(new TextBlock { Text = h == null ? buf.FileName + " (untracked)" : buf.FileName + "  ·  " + (staged ? "staged change " : "change ") + h.Summary, FontWeight = FontWeight.SemiBold, Foreground = fg, VerticalAlignment = VerticalAlignment.Center });
            var close = new Button { Content = "✕", FontSize = 10, Padding = new Thickness(6, 1) };
            close.Click += (s, e) => HideHunkPopup();
            Grid.SetColumn(close, 1);
            head.Children.Add(close);
            sp.Children.Add(head);
            if (h != null)
            {
                var preview = new StackPanel();
                int shown = 0;
                foreach (var l in h.Lines)
                {
                    if (shown++ >= 14) { preview.Children.Add(new TextBlock { Text = "…", Foreground = fg }); break; }
                    bool add = l.Op == '+';
                    preview.Children.Add(new Border
                    {
                        Background = new SolidColorBrush(add ? Color.FromArgb(60, 46, 160, 67) : Color.FromArgb(60, 241, 76, 76)),
                        Child = new TextBlock
                        {
                            Text = (add ? "+ " : "- ") + GitHunkDiff.StripMarker(l.Text),
                            FontFamily = new FontFamily(DevToolsFonts.MonoFamily),
                            FontSize = 12,
                            Foreground = fg,
                            TextTrimming = TextTrimming.CharacterEllipsis
                        }
                    });
                }
                sp.Children.Add(preview);
            }
            var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Margin = new Thickness(0, 2, 0, 0) };
            if (staged)
            {
                var unstage = new Button { Content = "Unstage Change", FontSize = 11, Padding = new Thickness(8, 2) };
                unstage.Click += (s, e) => { HideHunkPopup(); UnstageHunk(ctx, h); };
                actions.Children.Add(unstage);
            }
            else
            {
                var stage = new Button { Content = h == null ? "Stage File" : "Stage Change", FontSize = 11, Padding = new Thickness(8, 2) };
                stage.Click += (s, e) => { HideHunkPopup(); StageHunk(buf, ctx, h); };
                actions.Children.Add(stage);
            }
            if (h != null && !staged)
            {
                var revert = new Button { Content = "Revert Change", FontSize = 11, Padding = new Thickness(8, 2) };
                revert.Click += (s, e) => { HideHunkPopup(); RevertHunk(editor, buf, h); };
                actions.Children.Add(revert);
            }
            var diff = new Button { Content = "Open Diff", FontSize = 11, Padding = new Thickness(8, 2) };
            diff.Click += (s, e) => { HideHunkPopup(); OpenIndexDiff(buf, ctx.Index ?? ""); };
            actions.Children.Add(diff);
            sp.Children.Add(actions);
            hunkPopup.Child = sp;
            var origin = editor.Canvas.TranslatePoint(new Point(anchor.Right + 4, anchor.Bottom + 2), devToolsOverlay) ?? new Point(anchor.Right, anchor.Bottom);
            double maxX = Math.Max(0, devToolsOverlay.Bounds.Width - 660);
            Canvas.SetLeft(hunkPopup, Math.Max(8, Math.Min(origin.X, maxX)));
            double top = origin.Y;
            if (devToolsOverlay.Bounds.Height > 0 && top + 260 > devToolsOverlay.Bounds.Height) top = Math.Max(8, origin.Y - anchor.Height - 270);
            Canvas.SetTop(hunkPopup, top);
            hunkPopup.IsVisible = true;
        }

        public void HideHunkPopup()
        {
            if (hunkPopup != null) hunkPopup.IsVisible = false;
        }

        private void StageHunk(EditorTabBuffer buf, HunkContext ctx, GitDiffHunk h)
        {
            if (ctx == null) return;
            var hunks = h == null ? null : new List<GitDiffHunk> { h };
            Task.Run(() =>
            {
                GitOperationResult res;
                if (h == null)
                {
                    var add = GitProcess.Run(ctx.Repo, new[] { "add", "--", ctx.Rel }, 30000, CancellationToken.None, null, null);
                    res = GitErrorClassifier.ToResult(add, "Stage", "Staged " + Path.GetFileName(ctx.Rel) + ".");
                }
                else res = GitHunkStaging.StageHunks(ctx.Repo, ctx.Rel, ctx.Index, hunks);
                PostUi(() => AfterIndexChange(res));
            });
        }

        private void UnstageHunk(HunkContext ctx, GitDiffHunk h)
        {
            Task.Run(() =>
            {
                var res = GitHunkStaging.UnstageHunk(ctx.Repo, ctx.Rel, ctx.Head ?? GitHunkStaging.ReadHeadContent(ctx.Repo, ctx.Rel), ctx.Index, h);
                PostUi(() => AfterIndexChange(res));
            });
        }

        /// <summary>Test / command entry: unstages the staged change shown for <paramref name="line"/> (if any).</summary>
        public void UnstageChangeAtLine(EditorTabBuffer buf, int line)
        {
            if (buf == null || string.IsNullOrEmpty(buf.FilePath)) return;
            var lines = new List<string>(buf.Lines);
            bool finalNl = buf.HasFinalNewline;
            string path = buf.FilePath;
            Task.Run(() =>
            {
                GitOperationResult res;
                string repo = GitCli.FindRepoRoot(path);
                string rel = repo != null ? GitCli.RepoRelativePath(repo, path) : null;
                if (rel == null) res = new GitOperationResult { Error = GitErrorKind.Other, Message = "This file is not inside a git repository." };
                else
                {
                    string index = GitHunkStaging.ReadIndexContent(repo, rel, out bool bin);
                    string head = GitHunkStaging.ReadHeadContent(repo, rel);
                    var sh = GitHunkStaging.FindStagedHunkAtEditorLine(head, index, lines, finalNl, line);
                    res = sh == null ? new GitOperationResult { Error = GitErrorKind.Other, Message = "No staged change at the cursor." } : GitHunkStaging.UnstageHunk(repo, rel, head, index, sh);
                }
                PostUi(() => AfterIndexChange(res));
            });
        }

        /// <summary>
        /// Re-reads the git gutter baseline (the index, like VS Code's quick diff) of every open file after a git status
        /// refresh, so a staged hunk leaves the gutter and a commit / checkout updates it. Off the UI thread on desktop.
        /// </summary>
        private void RefreshGutterBaselines()
        {
            string root = workspaceRoot;
            var tabs = openTabs.Where(t => !string.IsNullOrEmpty(t.FilePath) && !t.IsLargeFile && !t.IsLargeFileViewer && File.Exists(t.FilePath)).ToList();
            if (tabs.Count == 0 || string.IsNullOrEmpty(root)) return;
            Action<Dictionary<EditorTabBuffer, string>> apply = map =>
            {
                bool changed = false;
                foreach (var kv in map)
                {
                    if (!openTabs.Contains(kv.Key) || string.Equals(kv.Key.GitHeadContent, kv.Value, StringComparison.Ordinal)) continue;
                    kv.Key.GitHeadContent = kv.Value;
                    kv.Key.RefreshGutterDiff();
                    changed = true;
                }
                if (changed) { PrimaryEditor?.Canvas?.InvalidateVisual(); SplitEditor?.Canvas?.InvalidateVisual(); }
            };
            Func<Dictionary<EditorTabBuffer, string>> read = () =>
            {
                var map = new Dictionary<EditorTabBuffer, string>();
                foreach (var t in tabs)
                {
                    try { map[t] = LlmHarnessAndAgentEngine.GetGitQuickDiffBaseline(root, t.FilePath); } catch { }
                }
                return map;
            };
            if (!AvaloniaTerminalEmulatorControl.IsDesktopRuntime) { apply(read()); return; }
            _ = Task.Run(() =>
            {
                var map = read();
                Dispatcher.UIThread.Post(() => { if (string.Equals(root, workspaceRoot, PathKeyComparison)) apply(map); });
            });
        }

        private void AfterIndexChange(GitOperationResult res)
        {
            LastGitOperationResult = res;
            LastGitOperationMessage = res.Message ?? "";
            SetStatusMessage(res.Message);
            if (!res.Success && !string.IsNullOrEmpty(res.Details)) AppendOutputLog("[Git Stage] " + res.Details);
            RefreshGitStatusInBackground();
        }

        private void RevertHunk(AvaloniaEditorControl editor, EditorTabBuffer buf, GitDiffHunk h)
        {
            if (!GitHunkEditor.RevertInBuffer(buf, h)) { SetStatusMessage("The change moved; click the gutter bar again."); return; }
            editor = editor ?? PrimaryEditor;
            if (ReferenceEquals(editor.CurrentBuffer, buf)) editor.Canvas.CommitExternalEdit();
            else buf.IsDirty = true;
            editor.Canvas.InvalidateVisual();
            RefreshTabStrip();
            SetStatusMessage("Reverted change (" + h.Summary + "). Undo with " + (IsMacUi ? "⌘Z" : "Ctrl+Z") + ".");
        }

        private void OpenIndexDiff(EditorTabBuffer buf, string index)
        {
            string text = string.Join("\n", buf.Lines ?? new List<string>());
            var dv = new DiffView(buf.FileName + "  ·  Index ↔ Working Tree", (index ?? "").Replace("\r\n", "\n"), text, "");
            OpenAuxDocument(new AuxPaneDocument { Id = "scmdiff:" + buf.FilePath, Title = buf.FileName + " (Working Tree)", Tooltip = buf.FilePath, Content = dv, ApplyTheme = dv.ApplyTheme });
        }

        /// <summary>"Git: Stage Change" at a line (whole hunk) or "Git: Stage Selected Ranges" (selection / caret line).</summary>
        public void StageChangeAt(EditorTabBuffer buf, int fromLine, int toLine, bool wholeHunk)
        {
            if (buf == null || string.IsNullOrEmpty(buf.FilePath)) return;
            string path = buf.FilePath;
            var lines = new List<string>(buf.Lines);
            bool finalNl = buf.HasFinalNewline;
            Task.Run(() =>
            {
                GitOperationResult res;
                string repo = GitCli.FindRepoRoot(path);
                string rel = repo != null ? GitCli.RepoRelativePath(repo, path) : null;
                if (rel == null) res = new GitOperationResult { Error = GitErrorKind.Other, Message = "This file is not inside a git repository." };
                else res = GitHunkStaging.StageFromEditor(repo, rel, lines, finalNl, fromLine, toLine, wholeHunk);
                PostUi(() => AfterIndexChange(res));
            });
        }

        public void StageSelectedRanges()
        {
            var buf = activeTab;
            if (buf == null) return;
            int from = buf.CursorLine, to = buf.CursorLine;
            if (buf.HasSelection())
            {
                buf.GetNormalizedSelection(out int sl, out int sc, out int el, out int ec);
                from = sl; to = el;
                if (ec == 0 && el > sl) to = el - 1;
            }
            StageChangeAt(buf, from, to, false);
        }

        public void RevertChangeAtCursor()
        {
            var buf = activeTab;
            if (buf == null) return;
            int line = buf.CursorLine;
            ComputeHunksAsync(buf, ctx =>
            {
                var h = ctx == null ? null : GitHunkDiff.HunkAtLine(ctx.Hunks, line);
                if (h == null) { SetStatusMessage("No unstaged change at the cursor."); return; }
                RevertHunk(PrimaryEditor, buf, h);
            });
        }

        // ------------------------------------------------------------------------------------------------
        // Source Control view: groups, commit (Ctrl/Cmd+Enter), amend, history, diff on click
        // ------------------------------------------------------------------------------------------------

        /// <summary>Hook at the top of RebuildScmListsUi: draws the VS Code groups. Returns false to fall back.</summary>
        private bool RebuildScmGroupsUi()
        {
            if (scmMergeListStack == null || scmUntrackedListStack == null || currentTheme == null) return false;
            scmMergeListStack.Children.Clear();
            scmStagedListStack.Children.Clear();
            scmChangesListStack.Children.Clear();
            scmUntrackedListStack.Children.Clear();

            bool isLight = currentTheme.IsLight || AvaloniaColorHelper.IsLightColor(currentTheme.SidebarBg);
            Color green = isLight ? Color.FromRgb(0, 113, 0) : Color.FromRgb(74, 222, 128);
            Color amber = isLight ? Color.FromRgb(137, 85, 3) : Color.FromRgb(226, 192, 141);
            Color red = isLight ? Color.FromRgb(173, 15, 15) : Color.FromRgb(248, 113, 113);
            Color muted = AvaloniaColorHelper.ToAvColor(currentTheme.TextSecondary);

            int merge = 0, staged = 0, changes = 0, untracked = 0, conflictTotal = 0;
            foreach (var e in currentGitEntries)
            {
                foreach (var g in ScmGrouping.GroupsFor(e.IndexStatus, e.WorkingTreeStatus))
                {
                    switch (g)
                    {
                        case ScmGroup.Merge:
                            int n = CountConflictsInFile(e.FullPath);
                            conflictTotal += n;
                            scmMergeListStack.Children.Add(BuildScmRow(e, g, red, muted, n));
                            merge++;
                            break;
                        case ScmGroup.Staged: scmStagedListStack.Children.Add(BuildScmRow(e, g, green, muted, 0)); staged++; break;
                        case ScmGroup.Changes: scmChangesListStack.Children.Add(BuildScmRow(e, g, amber, muted, 0)); changes++; break;
                        default: scmUntrackedListStack.Children.Add(BuildScmRow(e, g, green, muted, 0)); untracked++; break;
                    }
                }
            }
            scmMergeHeader.Text = "▾ MERGE CHANGES (" + merge + (conflictTotal > 0 ? ", " + conflictTotal + " conflict" + (conflictTotal == 1 ? "" : "s") : "") + ")";
            scmStagedHeader.Text = "▾ STAGED CHANGES (" + staged + ")";
            scmChangesHeader.Text = "▾ CHANGES (" + changes + ")";
            scmUntrackedHeader.Text = "▾ UNTRACKED CHANGES (" + untracked + ")";
            scmMergeHeader.IsVisible = scmMergeListStack.IsVisible = merge > 0;
            scmUntrackedHeader.IsVisible = scmUntrackedListStack.IsVisible = untracked > 0;
            scmStagedHeader.IsVisible = scmStagedListStack.IsVisible = staged > 0 || (merge == 0 && changes == 0 && untracked == 0);
            return true;
        }

        public int MergeConflictFileCount => currentGitEntries.Count(e => ScmGrouping.IsConflict(e.IndexStatus, e.WorkingTreeStatus));

        private static int CountConflictsInFile(string path)
        {
            try
            {
                var fi = new FileInfo(path);
                if (!fi.Exists || fi.Length > 4 * 1024 * 1024) return 0;
                return GitConflictParser.CountConflicts(File.ReadAllText(path));
            }
            catch { return 0; }
        }

        private Control BuildScmRow(GitStatusEntry e, ScmGroup g, Color color, Color muted, int conflicts)
        {
            // VS Code layout: name + folder (one ellipsized line), hover-only vector actions, status letter pinned right.
            var captured = e;
            char badge = ScmGrouping.BadgeFor(g, e.IndexStatus, e.WorkingTreeStatus);
            string label = ScmLabel(e);
            string dir = Path.GetDirectoryName((e.RepoRelativePath ?? e.RelativePath ?? "").TrimEnd('/'))?.Replace('\\', '/') ?? "";
            var row = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto"), Height = UiTokens.ListRowHeight, Background = Brushes.Transparent };
            var text = new TextBlock { TextTrimming = TextTrimming.CharacterEllipsis, TextWrapping = TextWrapping.NoWrap, VerticalAlignment = VerticalAlignment.Center, FontSize = UiTokens.FontUi };
            text.Inlines.Add(new Avalonia.Controls.Documents.Run(label) { Foreground = new SolidColorBrush(color) });
            if (dir.Length > 0) text.Inlines.Add(new Avalonia.Controls.Documents.Run("  " + dir) { Foreground = new SolidColorBrush(muted), FontSize = UiTokens.FontSmall });
            if (conflicts > 0) text.Inlines.Add(new Avalonia.Controls.Documents.Run("  " + conflicts + " conflict" + (conflicts == 1 ? "" : "s")) { Foreground = new SolidColorBrush(color), FontSize = UiTokens.FontSmall });
            var open = new Button
            {
                Content = text,
                Background = Brushes.Transparent,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Stretch,
                VerticalContentAlignment = VerticalAlignment.Center,
                Padding = new Thickness(8, 0, 4, 0),
                MinHeight = 0
            };
            ToolTip.SetTip(open, (e.RepoRelativePath ?? e.RelativePath) + (g == ScmGroup.Merge ? " — open to resolve conflicts" : g == ScmGroup.Untracked ? " — open file" : " — open changes"));
            open.Click += (s, ev) => OpenScmEntry(captured, g);
            row.Children.Add(open);

            var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 0, VerticalAlignment = VerticalAlignment.Center, IsVisible = false, Name = "ScmRowActions" };
            void Add(string icon, string tip, Action a) => actions.Children.Add(WorkbenchStyles.IconButton(icon, tip, a, currentTheme, 22, 14));
            Add("go-to-file", "Open File", () => { if (File.Exists(captured.FullPath)) OpenFileInTab(captured.FullPath, preview: false); });
            switch (g)
            {
                case ScmGroup.Staged:
                    Add("minus", "Unstage Changes", () => ExecuteGitUnstageFile(captured));
                    break;
                case ScmGroup.Merge:
                    Add("plus", "Stage Changes (mark as resolved)", () => StageResolvedFile(captured));
                    break;
                default:
                    Add(g == ScmGroup.Untracked ? "trash" : "discard", g == ScmGroup.Untracked ? "Delete File" : "Discard Changes", () => ConfirmAndDiscardGitFile(captured));
                    Add("plus", "Stage Changes", () => ExecuteGitStageFile(captured));
                    break;
            }
            Grid.SetColumn(actions, 1);
            row.Children.Add(actions);

            var badgeText = new LiteralTextBlock(badge.ToString())
            {
                FontSize = UiTokens.FontSmall,
                FontWeight = FontWeight.SemiBold,
                Foreground = new SolidColorBrush(color),
                VerticalAlignment = VerticalAlignment.Center,
                TextAlignment = TextAlignment.Center,
                Width = 16,
                Margin = new Thickness(2, 0, 10, 0)
            };
            Grid.SetColumn(badgeText, 2);
            row.Children.Add(badgeText);

            var hover = new SolidColorBrush(AvaloniaColorHelper.ToAvColor(UiTokens.Hover(currentTheme, currentTheme.BgSidebar)));
            row.PointerEntered += (s, ev) => { actions.IsVisible = true; row.Background = hover; };
            row.PointerExited += (s, ev) => { actions.IsVisible = row.IsKeyboardFocusWithin; row.Background = Brushes.Transparent; };
            row.GotFocus += (s, ev) => actions.IsVisible = true;
            row.LostFocus += (s, ev) => { if (!row.IsKeyboardFocusWithin && !row.IsPointerOver) actions.IsVisible = false; };
            return row;
        }

        /// <summary>Merge group "+": refuses while markers remain (VS Code asks; we explain), otherwise git add.</summary>
        public void StageResolvedFile(GitStatusEntry e)
        {
            var open = openTabs.FirstOrDefault(t => string.Equals(t.FilePath, e.FullPath, PathKeyComparison));
            if (open != null && open.IsDirty) SaveBufferToDisk(open);
            int left = CountConflictsInFile(e.FullPath);
            if (left > 0 && AvaloniaTerminalEmulatorControl.IsDesktopRuntime)
            {
                ShowConfirmModal("Stage File With Conflicts?", Path.GetFileName(e.FullPath) + " still contains " + left + " merge conflict marker block(s). Stage it anyway?", "Stage", () => ExecuteGitStageFile(e));
                return;
            }
            ExecuteGitStageFile(e);
        }

        private void OpenScmEntry(GitStatusEntry e, ScmGroup g)
        {
            if (g == ScmGroup.Merge || g == ScmGroup.Untracked || !File.Exists(e.FullPath) && g != ScmGroup.Staged)
            {
                if (File.Exists(e.FullPath))
                {
                    OpenFileInTab(e.FullPath, preview: false);
                    if (g == ScmGroup.Merge) GoToNextConflict(true);
                }
                return;
            }
            string repo = !string.IsNullOrEmpty(e.RepoRoot) ? e.RepoRoot : GitCli.FindRepoRoot(e.FullPath);
            string rel = !string.IsNullOrEmpty(e.RepoRelativePath) ? e.RepoRelativePath : GitCli.RepoRelativePath(repo, e.FullPath);
            if (repo == null || rel == null) { OpenGitFileDiffInEditor(e.FullPath); return; }
            var buffer = openTabs.FirstOrDefault(t => string.Equals(t.FilePath, e.FullPath, PathKeyComparison));
            string bufferText = buffer != null ? string.Join("\n", buffer.Lines) : null;
            string name = Path.GetFileName(e.FullPath);
            Task.Run(() =>
            {
                string left, right, title;
                if (g == ScmGroup.Staged)
                {
                    left = ShowRevision(repo, "HEAD:" + rel);
                    right = ShowRevision(repo, ":" + rel);
                    title = name + " (Index)";
                }
                else
                {
                    left = ShowRevision(repo, ":" + rel) ?? ShowRevision(repo, "HEAD:" + rel);
                    try { right = bufferText ?? (File.Exists(e.FullPath) ? File.ReadAllText(e.FullPath) : ""); } catch { right = ""; }
                    title = name + " (Working Tree)";
                }
                PostUi(() =>
                {
                    var dv = new DiffView(rel + "  ·  " + (g == ScmGroup.Staged ? "HEAD ↔ Index" : "Index ↔ Working Tree"), (left ?? "").Replace("\r\n", "\n"), (right ?? "").Replace("\r\n", "\n"), "");
                    OpenAuxDocument(new AuxPaneDocument { Id = "scm:" + g + ":" + e.FullPath, Title = title, Tooltip = e.FullPath, Content = dv, ApplyTheme = dv.ApplyTheme });
                });
            });
        }

        private static string ShowRevision(string repo, string spec)
        {
            var r = GitProcess.Run(repo, new[] { "show", spec }, 20000, CancellationToken.None, null, null);
            return r.Success ? r.Stdout : null;
        }

        private void OnAmendToggled()
        {
            UpdateCommitWatermark();
            if (scmAmendCheck?.IsChecked != true || !string.IsNullOrWhiteSpace(scmCommitInput.Text)) return;
            string repo = ParityRepo();
            if (repo == null) return;
            Task.Run(() =>
            {
                string last = GitRemoteOps.GetLastCommitMessage(repo);
                PostUi(() => { if (scmAmendCheck.IsChecked == true && string.IsNullOrWhiteSpace(scmCommitInput.Text)) scmCommitInput.Text = last; });
            });
        }

        private void OnCommitInputKeyDown(object sender, KeyEventArgs e)
        {
            if (e.KeyModifiers != KeyModifiers.None) return;
            string text = scmCommitInput.Text ?? "";
            int caret = scmCommitInput.CaretIndex;
            if (e.Key == Key.Up && text.LastIndexOf('\n', Math.Max(0, Math.Min(text.Length, caret) - 1)) < 0 || e.Key == Key.Up && text.Length == 0)
            {
                string prev = commitHistory?.Previous(text);
                if (prev != null) { scmCommitInput.Text = prev; scmCommitInput.CaretIndex = 0; e.Handled = true; }
            }
            else if (e.Key == Key.Down && commitHistory != null && commitHistory.IsBrowsing && text.IndexOf('\n', Math.Min(text.Length, caret)) < 0)
            {
                string next = commitHistory.Next();
                if (next != null) { scmCommitInput.Text = next; scmCommitInput.CaretIndex = next.Length; e.Handled = true; }
            }
        }

        /// <summary>Hook at the start of ExecuteGitCommit: records history and handles amend. Returns true when handled.</summary>
        private bool TryCommitWithParityOptions(string msg, out bool result)
        {
            result = false;
            if (!string.IsNullOrWhiteSpace(msg)) { commitHistory?.Add(msg); }
            if (scmAmendCheck?.IsChecked != true) return false;
            string repo = ParityRepo();
            if (repo == null) { SetStatusMessage("No git repository found for this workspace."); return true; }
            bool anyStaged = currentGitEntries.Any(e => e.IsStaged);
            scmCommitInput.Text = "";
            RunGitOperation("Commit (Amend)", (r, ct, p) => GitRemoteOps.Commit(r, msg, true, false, ct), res =>
            {
                if (res.Success && scmAmendCheck != null) scmAmendCheck.IsChecked = false;
            });
            result = true;
            return true;
        }

        public void CommitFromScmInput()
        {
            string msg = (scmCommitInput?.Text ?? "").Trim();
            if (scmAmendCheck?.IsChecked == true) { TryCommitWithParityOptions(msg, out _); return; }
            ExecuteGitCommit();
        }

        // ------------------------------------------------------------------------------------------------
        // Shortcuts and palette
        // ------------------------------------------------------------------------------------------------

        private bool HandleParityShortcut(Key key, KeyModifiers modifiers)
        {
            bool primary = MacKeymap.IsPrimary(modifiers);
            bool shift = modifiers.HasFlag(KeyModifiers.Shift);
            bool alt = modifiers.HasFlag(KeyModifiers.Alt);
            if (primary && !shift && !alt && key == Key.Enter && scmCommitInput != null && (scmCommitInput.IsFocused || scmCommitInput.IsKeyboardFocusWithin))
            {
                CommitFromScmInput();
                return true;
            }
            if (key == Key.Escape && hunkPopup != null && hunkPopup.IsVisible) { HideHunkPopup(); return true; }
            return false;
        }

        private List<PaletteEntryItem> BuildParityPaletteCommands()
        {
            return new List<PaletteEntryItem>
            {
                new PaletteEntryItem { Category = "Git", Title = "Checkout to...", Execute = OpenBranchPicker },
                new PaletteEntryItem { Category = "Git", Title = "Create Branch...", Execute = () => PromptCreateBranch(null) },
                new PaletteEntryItem { Category = "Git", Title = "Fetch", Execute = GitFetch },
                new PaletteEntryItem { Category = "Git", Title = "Fetch (Prune)", Execute = GitFetchPrune },
                new PaletteEntryItem { Category = "Git", Title = "Fetch From All Remotes", Execute = GitFetchAll },
                new PaletteEntryItem { Category = "Git", Title = "Pull", Execute = GitPull },
                new PaletteEntryItem { Category = "Git", Title = "Pull (Rebase)", Execute = GitPullRebase },
                new PaletteEntryItem { Category = "Git", Title = "Push", Execute = GitPush },
                new PaletteEntryItem { Category = "Git", Title = "Sync", Execute = GitSync },
                new PaletteEntryItem { Category = "Git", Title = "Publish Branch...", Execute = GitPublishBranch },
                new PaletteEntryItem { Category = "Git", Title = "Cancel Running Operation", Execute = CancelGitOperation },
                new PaletteEntryItem { Category = "Git", Title = "Commit", Execute = CommitFromScmInput },
                new PaletteEntryItem { Category = "Git", Title = "Commit (Amend)", Execute = () => { if (scmAmendCheck != null) scmAmendCheck.IsChecked = true; SwitchSidebarView("git"); scmCommitInput?.Focus(); } },
                new PaletteEntryItem { Category = "Git", Title = "Stage Selected Ranges", Execute = StageSelectedRanges },
                new PaletteEntryItem { Category = "Git", Title = "Stage Change at Cursor", Execute = () => { if (activeTab != null) StageChangeAt(activeTab, activeTab.CursorLine, activeTab.CursorLine, true); } },
                new PaletteEntryItem { Category = "Git", Title = "Revert Change at Cursor", Execute = RevertChangeAtCursor },
                new PaletteEntryItem { Category = "Git", Title = "Unstage Change at Cursor", Execute = () => { if (activeTab != null) UnstageChangeAtLine(activeTab, activeTab.CursorLine); } },
                new PaletteEntryItem { Category = "Merge Conflict", Title = "Accept Current", Execute = () => AcceptConflictAtCursor(ConflictResolution.Current) },
                new PaletteEntryItem { Category = "Merge Conflict", Title = "Accept Incoming", Execute = () => AcceptConflictAtCursor(ConflictResolution.Incoming) },
                new PaletteEntryItem { Category = "Merge Conflict", Title = "Accept Both", Execute = () => AcceptConflictAtCursor(ConflictResolution.Both) },
                new PaletteEntryItem { Category = "Merge Conflict", Title = "Accept All Current", Execute = () => AcceptAllConflicts(ConflictResolution.Current) },
                new PaletteEntryItem { Category = "Merge Conflict", Title = "Accept All Incoming", Execute = () => AcceptAllConflicts(ConflictResolution.Incoming) },
                new PaletteEntryItem { Category = "Merge Conflict", Title = "Accept All Both", Execute = () => AcceptAllConflicts(ConflictResolution.Both) },
                new PaletteEntryItem { Category = "Merge Conflict", Title = "Compare Current Conflict", Execute = () => { if (activeTab != null) CompareConflictAt(activeTab, activeTab.CursorLine); } },
                new PaletteEntryItem { Category = "Merge Conflict", Title = "Next Conflict", Execute = () => GoToNextConflict(true) },
                new PaletteEntryItem { Category = "Merge Conflict", Title = "Previous Conflict", Execute = () => GoToNextConflict(false) },
            };
        }

        /// <summary>Editor context-menu items added when they apply (conflict under the caret, file in a repo).</summary>
        private List<object> BuildParityEditorContextItems(EditorTabBuffer tab)
        {
            // Same entries as the Windows editor (EditorScmContextItems, shared).
            var items = new List<object>();
            foreach (var entry in EditorScmContextItems.Build(tab))
            {
                if (entry.IsSeparator) { items.Add(new Separator()); continue; }
                string id = entry.CommandId;
                items.Add(MakeCtxItem(entry.Label, () => ExecuteScmContextCommand(tab, id)));
            }
            return items;
        }

        private void ExecuteScmContextCommand(EditorTabBuffer tab, string id)
        {
            switch (id)
            {
                case "par_accept_current": AcceptConflictAtCursor(ConflictResolution.Current); break;
                case "par_accept_incoming": AcceptConflictAtCursor(ConflictResolution.Incoming); break;
                case "par_accept_both": AcceptConflictAtCursor(ConflictResolution.Both); break;
                case "par_compare": CompareConflictAt(tab, tab.CursorLine); break;
                case "par_stage_change": StageChangeAt(tab, tab.CursorLine, tab.CursorLine, true); break;
                case "par_stage_ranges": StageSelectedRanges(); break;
                case "par_revert_change": RevertChangeAtCursor(); break;
            }
        }
    }
}
