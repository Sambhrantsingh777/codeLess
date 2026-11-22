using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace codeLess.Studio
{
    /// <summary>
    /// Daily-driver parity with VS Code on Windows (same features as the Avalonia UI): status bar branch picker and
    /// sync item (ahead/behind, Fetch / Pull / Push / Publish / Sync with progress + cancel), merge-conflict decorations
    /// and Accept Current / Incoming / Both / Compare lenses, gutter-bar Stage / Revert Change menu, Stage Selected
    /// Ranges, grouped Source Control list with amend and message history, tasks with problem matchers, save
    /// participants and auto-save modes. Shared logic in codeLess.Core. Strict C# 5 (legacy csc).
    /// Hooks: InitWorkbenchParity, HandleParityKey, ExecuteParityCommand, ParityPaletteCommands, AddParityGitControls,
    /// PopulateGroupedGitList, LayoutParitySyncItem, ApplySaveParticipantsWin, AppendTaskProblemsWin.
    /// </summary>
    public partial class CodeLessMainForm
    {
        private sealed class GitListRow
        {
            public GitFileStatus Item;
            public ScmGroup Group;
        }

        private sealed class RunningTaskWin
        {
            public TaskExecution Exec;
            public TerminalSessionState Terminal;
        }

        private Label statusSyncItem;
        private CancellationTokenSource winGitOpCts;
        private string winGitOpTitle;
        private GitBranchStatus winBranchStatus;
        private bool winBranchRefreshRunning, winBranchRefreshPending;
        private System.Windows.Forms.Timer winAutoFetchTimer;
        private System.Windows.Forms.Timer winParityPollTimer;
        private EditorTabBuffer winPolledTab;
        private CheckBox gitAmendCheck;
        private CommitMessageHistory winCommitHistory;
        private readonly List<GitListRow> gitListRows = new List<GitListRow>();
        private readonly List<RunningTaskWin> runningTasksWin = new List<RunningTaskWin>();
        private TaskDefinition lastRunTaskWin;
        private int autoSaveDepthWin;
        private readonly Dictionary<string, EditorLineAnnotation> winConflictLens = new Dictionary<string, EditorLineAnnotation>(StringComparer.Ordinal);

        /// <summary>Palette entries: category, title, shortcut, command id.</summary>
        public static readonly string[][] ParityPaletteCommands = new string[][]
        {
            new string[] { "Git", "Git: Checkout to...", "", "par_checkout" },
            new string[] { "Git", "Git: Create Branch...", "", "par_create_branch" },
            new string[] { "Git", "Git: Fetch", "", "par_fetch" },
            new string[] { "Git", "Git: Fetch (Prune)", "", "par_fetch_prune" },
            new string[] { "Git", "Git: Pull", "", "par_pull" },
            new string[] { "Git", "Git: Pull (Rebase)", "", "par_pull_rebase" },
            new string[] { "Git", "Git: Push", "", "par_push" },
            new string[] { "Git", "Git: Sync", "", "par_sync" },
            new string[] { "Git", "Git: Publish Branch...", "", "par_publish" },
            new string[] { "Git", "Git: Cancel Running Operation", "", "par_cancel" },
            new string[] { "Git", "Git: Commit (Amend)", "", "par_amend" },
            new string[] { "Git", "Git: Stage Selected Ranges", "", "par_stage_ranges" },
            new string[] { "Git", "Git: Stage Change at Cursor", "", "par_stage_change" },
            new string[] { "Git", "Git: Revert Change at Cursor", "", "par_revert_change" },
            new string[] { "Merge Conflict", "Merge Conflict: Accept Current", "", "par_accept_current" },
            new string[] { "Merge Conflict", "Merge Conflict: Accept Incoming", "", "par_accept_incoming" },
            new string[] { "Merge Conflict", "Merge Conflict: Accept Both", "", "par_accept_both" },
            new string[] { "Merge Conflict", "Merge Conflict: Accept All Current", "", "par_accept_all_current" },
            new string[] { "Merge Conflict", "Merge Conflict: Accept All Incoming", "", "par_accept_all_incoming" },
            new string[] { "Merge Conflict", "Merge Conflict: Compare Current Conflict", "", "par_compare" },
            new string[] { "Merge Conflict", "Merge Conflict: Next Conflict", "", "par_next_conflict" },
            new string[] { "Merge Conflict", "Merge Conflict: Previous Conflict", "", "par_prev_conflict" },
            new string[] { "Tasks", "Tasks: Run Task...", "", "par_run_task" },
            new string[] { "Tasks", "Tasks: Run Build Task", "Ctrl+Shift+B", "par_run_build" },
            new string[] { "Tasks", "Tasks: Run Test Task", "", "par_run_test" },
            new string[] { "Tasks", "Tasks: Rerun Last Task", "", "par_rerun_task" },
            new string[] { "Tasks", "Tasks: Terminate Task", "", "par_terminate_task" },
            new string[] { "Tasks", "Tasks: Configure Tasks...", "", "par_configure_tasks" }
        };

        private void ParityPost(Action a)
        {
            try { if (!IsDisposed && IsHandleCreated) BeginInvoke(a); } catch { }
        }

        private void ParityStatus(string msg)
        {
            if (statusLeftLabel != null) statusLeftLabel.Text = msg ?? "";
        }

        private void InitWorkbenchParity()
        {
            if (settings.GitCommitMessageHistory == null) settings.GitCommitMessageHistory = new List<string>();
            winCommitHistory = new CommitMessageHistory(settings.GitCommitMessageHistory);

            if (statusBar != null)
            {
                statusSyncItem = new Label
                {
                    AutoSize = true,
                    Visible = false,
                    Font = StatusItemFont,
                    Cursor = Cursors.Hand,
                    UseMnemonic = false,
                    BackColor = Color.Transparent,
                    Padding = new Padding(4, 3, 4, 0),
                    Text = "⟳"
                };
                statusSyncItem.MouseEnter += delegate { statusSyncItem.BackColor = Color.FromArgb(45, 255, 255, 255); };
                statusSyncItem.MouseLeave += delegate { statusSyncItem.BackColor = Color.Transparent; };
                statusSyncItem.Click += delegate { OnSyncClickedWin(); };
                statusBar.Controls.Add(statusSyncItem);
            }

            foreach (CodeLessEditorControl ed in new CodeLessEditorControl[] { editorControl, splitEditorControl })
            {
                if (ed == null) continue;
                CodeLessEditorControl editor = ed;
                editor.Canvas.GutterDiffClicked += delegate (EditorTabBuffer buf, int line, Rectangle r) { ShowHunkMenuWin(editor, buf, line, r); };
            }

            Action onProblems = delegate { ParityPost(ApplyTaskProblemsWin); };
            TaskProblemStore.Changed += onProblems;
            this.FormClosed += delegate
            {
                TaskProblemStore.Changed -= onProblems;
                foreach (RunningTaskWin rt in runningTasksWin.ToList()) { try { if (rt.Exec.Terminate != null) rt.Exec.Terminate(); } catch { } }
            };
            this.Deactivate += delegate { if (settings.FocusChangeSavesFiles) SaveAllDirtyFileBuffersWin(true); };

            winAutoFetchTimer = new System.Windows.Forms.Timer();
            winAutoFetchTimer.Tick += delegate
            {
                if (settings.GitAutoFetch && winGitOpCts == null && !NetworkLedger.OfflineMode)
                    RunGitOperationWin("Auto Fetch", delegate (string repo, CancellationToken ct, Action<string> p) { return GitRemoteOps.Fetch(repo, false, false, ct, null); }, null, true);
            };
            winParityPollTimer = new System.Windows.Forms.Timer { Interval = 400 };
            winParityPollTimer.Tick += delegate { ParityPoll(); };
            winParityPollTimer.Start();
            ApplyParitySettingsWin();
        }

        public void ApplyParitySettingsWin()
        {
            if (winAutoFetchTimer == null) return;
            winAutoFetchTimer.Stop();
            if (settings.GitAutoFetch)
            {
                winAutoFetchTimer.Interval = settings.EffectiveAutoFetchPeriodSeconds * 1000;
                winAutoFetchTimer.Start();
            }
            if (autoSaveTimer != null) autoSaveTimer.Interval = settings.EffectiveAutoSaveDelayMs;
        }

        /// <summary>files.autoSave = onFocusChange: the editor being left is saved when another tab becomes active.</summary>
        private void ParityPoll()
        {
            EditorTabBuffer cur = activeTab;
            if (ReferenceEquals(cur, winPolledTab)) return;
            EditorTabBuffer leaving = winPolledTab;
            winPolledTab = cur;
            if (leaving == null || settings.EffectiveAutoSaveMode != IdePersistentSettings.AutoSaveOnFocusChange) return;
            if (leaving.IsDirty && !leaving.IsUntitled && !string.IsNullOrEmpty(leaving.FilePath) && File.Exists(leaving.FilePath) && openTabs.Contains(leaving))
            {
                autoSaveDepthWin++;
                try { SaveTabBuffer(leaving, true); } finally { autoSaveDepthWin--; }
            }
        }

        private string ParityRepoWin()
        {
            string r = null;
            if (activeTab != null && !string.IsNullOrEmpty(activeTab.FilePath)) r = GitCli.FindRepoRoot(activeTab.FilePath);
            if (r == null && !string.IsNullOrEmpty(workspaceRoot)) r = GitCli.FindRepoRoot(workspaceRoot);
            return r;
        }

        // ------------------------------------------------------------------------------------------------
        // Status bar
        // ------------------------------------------------------------------------------------------------

        /// <summary>LayoutStatusBarItems hook: places the sync item right of the branch label.</summary>
        private int LayoutParitySyncItem(int leftX)
        {
            if (statusSyncItem == null || !statusSyncItem.Visible) return leftX;
            statusSyncItem.ForeColor = statusLeftLabel != null ? statusLeftLabel.ForeColor : Color.White;
            statusSyncItem.Location = new Point(leftX - 4, 1);
            return statusSyncItem.Right + 6;
        }

        public void RefreshBranchStatusWin()
        {
            string repo = ParityRepoWin();
            if (repo == null) { winBranchStatus = null; UpdateSyncItemWin(); return; }
            if (winBranchRefreshRunning) { winBranchRefreshPending = true; return; }
            winBranchRefreshRunning = true;
            Task.Run(delegate
            {
                GitBranchStatus st = null;
                try { st = GitRemoteOps.GetBranchStatus(repo); } catch { }
                ParityPost(delegate
                {
                    winBranchRefreshRunning = false;
                    winBranchStatus = st;
                    UpdateSyncItemWin();
                    if (winBranchRefreshPending) { winBranchRefreshPending = false; RefreshBranchStatusWin(); }
                });
            });
        }

        private void UpdateSyncItemWin()
        {
            GitBranchStatus st = winBranchStatus;
            if (statusSyncItem == null) return;
            if (winGitOpCts != null)
            {
                statusSyncItem.Visible = true;
                statusSyncItem.Text = "⟳ " + (winGitOpTitle ?? "Git") + "…";
                statusBarToolTip.SetToolTip(statusSyncItem, winGitOpTitle + " in progress — click to cancel");
            }
            else if (st == null || st.IsDetached || st.Branch == null)
            {
                statusSyncItem.Visible = false;
            }
            else if (!st.HasUpstream)
            {
                statusSyncItem.Visible = true;
                statusSyncItem.Text = "☁ Publish";
                statusBarToolTip.SetToolTip(statusSyncItem, "Publish Branch (push " + st.Branch + " and set its upstream)");
            }
            else
            {
                statusSyncItem.Visible = true;
                string counts = st.SyncText;
                statusSyncItem.Text = counts.Length == 0 ? "⟳" : "⟳ " + counts;
                statusBarToolTip.SetToolTip(statusSyncItem, "Synchronize Changes with " + st.Upstream);
            }
            string curText = statusLeftLabel != null ? (statusLeftLabel.Text ?? "") : "";
            // The label doubles as the status message area: only replace the "branch  •  folder" text, not a message.
            if (st != null && (st.Branch != null || st.IsDetached) && statusLeftLabel != null && !string.IsNullOrEmpty(workspaceRoot) && (curText.Length == 0 || curText.Contains("  •  ")))
            {
                statusLeftLabel.Text = st.DisplayName + "  •  " + Path.GetFileName(workspaceRoot.TrimEnd('\\', '/'));
                if (gitBranchLabel != null && st.IsDetached) { gitBranchLabel.Text = "HEAD detached at " + GitCli.ShortHash(st.Oid); gitBranchLabel.Invalidate(); }
            }
            LayoutStatusBarItems();
        }

        private void OnSyncClickedWin()
        {
            if (winGitOpCts != null) { CancelGitOperationWin(); return; }
            if (winBranchStatus != null && !winBranchStatus.HasUpstream) GitPublishWin();
            else RunGitOperationWin("Sync", delegate (string repo, CancellationToken ct, Action<string> p) { return GitRemoteOps.Sync(repo, settings.GitRebaseWhenSync, ct, p); }, null, false);
        }

        // ------------------------------------------------------------------------------------------------
        // Remote operations
        // ------------------------------------------------------------------------------------------------

        public bool RunGitOperationWin(string title, Func<string, CancellationToken, Action<string>, GitOperationResult> op, Action<GitOperationResult> after, bool quiet)
        {
            string repo = ParityRepoWin();
            if (repo == null) { ParityStatus("No git repository found for the open folder."); return false; }
            if (winGitOpCts != null) { if (!quiet) ParityStatus("Git: '" + winGitOpTitle + "' is still running (click the sync item to cancel)."); return false; }
            CancellationTokenSource cts = new CancellationTokenSource();
            winGitOpCts = cts;
            winGitOpTitle = title;
            UpdateSyncItemWin();
            if (!quiet) ParityStatus("Git: " + title + "…");
            int lastTick = 0;
            Action<string> progress = delegate (string line)
            {
                int now = Environment.TickCount;
                if (quiet || unchecked(now - lastTick) < 250) return;
                lastTick = now;
                ParityPost(delegate { if (winGitOpCts == cts) ParityStatus("Git " + title + ": " + (line ?? "").Trim()); });
            };
            Task.Run(delegate
            {
                GitOperationResult res;
                try { res = op(repo, cts.Token, progress); }
                catch (Exception ex) { res = new GitOperationResult(); res.Error = GitErrorKind.Other; res.Message = title + " failed: " + ex.Message; }
                ParityPost(delegate
                {
                    if (winGitOpCts == cts) { winGitOpCts = null; winGitOpTitle = null; }
                    cts.Dispose();
                    if (!string.IsNullOrWhiteSpace(res.Details)) AppendOutputLog("[Git " + title + "] " + res.Details.Trim());
                    if (res.Success) { if (!quiet) ParityStatus(res.Message); }
                    else if (!quiet || res.Error == GitErrorKind.AuthenticationFailed) ReportGitFailureWin(title, res);
                    else AppendOutputLog("[Git " + title + "] " + res.Message);
                    if (after != null) after(res);
                    RefreshGitSidebarStatus();
                    RefreshBranchStatusWin();
                });
            });
            return true;
        }

        private void ReportGitFailureWin(string title, GitOperationResult res)
        {
            ParityStatus(res.Message);
            if (SuppressUnsavedPrompts || res.Error == GitErrorKind.Cancelled) return;
            if (res.Error == GitErrorKind.AuthenticationFailed)
            {
                if (MessageBox.Show(this, res.Message + "\n\nOpen the integrated terminal now?", "Git: Authentication Required", MessageBoxButtons.YesNo, MessageBoxIcon.Information) == DialogResult.Yes)
                {
                    if (!bottomDockPanel.Visible) ToggleBottomDockVisibility();
                    SwitchBottomDockTab("terminal");
                }
                return;
            }
            if (res.Error == GitErrorKind.NonFastForward)
            {
                if (MessageBox.Show(this, res.Message + "\n\nPull now?", "Git: Push Rejected", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) == DialogResult.Yes)
                    ExecuteParityCommand("par_pull");
                return;
            }
            if (res.Error == GitErrorKind.NoUpstream)
            {
                if (MessageBox.Show(this, res.Message + "\n\nPublish the branch now?", "Git: No Upstream Branch", MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes)
                    GitPublishWin();
                return;
            }
            MessageBox.Show(this, res.Message + (string.IsNullOrWhiteSpace(res.Details) ? "" : "\n\n" + GitErrorClassifier.FirstLine(res.Details)), "Git: " + title, MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }

        public void CancelGitOperationWin()
        {
            CancellationTokenSource c = winGitOpCts;
            if (c == null) { ParityStatus("No git operation is running."); return; }
            try { c.Cancel(); } catch { }
            ParityStatus("Git: cancelling " + winGitOpTitle + "…");
        }

        private void GitPublishWin()
        {
            string repo = ParityRepoWin();
            if (repo == null) { ParityStatus("No git repository found for the open folder."); return; }
            List<string> remotes = GitRemoteOps.ListRemotes(repo);
            string pick = GitRemoteOps.PickPublishRemote(remotes);
            if (pick == null && remotes.Count > 1)
            {
                ContextMenuStrip menu = CreateAutoDisposingContextMenu();
                foreach (string r in remotes)
                {
                    string remote = r;
                    menu.Items.Add(CreateMenuItem("Publish to " + remote, "", delegate
                    {
                        RunGitOperationWin("Publish Branch", delegate (string rp, CancellationToken ct, Action<string> p) { return GitRemoteOps.Publish(rp, remote, ct, p); }, null, false);
                    }));
                }
                menu.Show(statusLeftLabel, new Point(0, 0), ToolStripDropDownDirection.AboveRight);
                return;
            }
            RunGitOperationWin("Publish Branch", delegate (string rp, CancellationToken ct, Action<string> p) { return GitRemoteOps.Publish(rp, pick, ct, p); }, null, false);
        }

        // ------------------------------------------------------------------------------------------------
        // Branch picker
        // ------------------------------------------------------------------------------------------------

        public void OpenBranchPickerWin()
        {
            string repo = ParityRepoWin();
            if (repo == null) { leftSidebar.Visible = true; SwitchSidebarView("git"); RefreshGitSidebarStatus(); return; }
            ParityStatus("Loading branches…");
            Task.Run(delegate
            {
                List<GitRefInfo> refs;
                try { refs = GitRemoteOps.ListRefs(repo, true); } catch { refs = new List<GitRefInfo>(); }
                GitBranchStatus st = null;
                try { st = GitRemoteOps.GetBranchStatus(repo); } catch { }
                ParityPost(delegate { ShowBranchMenuWin(refs, st); });
            });
        }

        private void ShowBranchMenuWin(List<GitRefInfo> refs, GitBranchStatus st)
        {
            ParityStatus("");
            ContextMenuStrip menu = CreateAutoDisposingContextMenu();
            menu.Items.Add(CreateMenuItem("+ Create new branch...", "", delegate { PromptCreateBranchWin(null); }));
            ToolStripMenuItem from = new ToolStripMenuItem("+ Create new branch from...");
            foreach (GitRefInfo r in refs.Take(60))
            {
                string start = r.ShortName;
                from.DropDownItems.Add(CreateMenuItem(r.ShortName, "", delegate { PromptCreateBranchWin(start); }));
            }
            menu.Items.Add(from);
            if (st != null && st.IsDetached)
            {
                ToolStripMenuItem det = new ToolStripMenuItem("HEAD (detached at " + GitCli.ShortHash(st.Oid) + ")");
                det.Enabled = false;
                menu.Items.Add(det);
            }
            AddRefSection(menu, "Branches", refs.Where(r => r.Kind == GitRefKind.Local).ToList(), refs, 80);
            AddRefSection(menu, "Remote branches", refs.Where(r => r.Kind == GitRefKind.Remote).ToList(), refs, 60);
            AddRefSection(menu, "Tags", refs.Where(r => r.Kind == GitRefKind.Tag).ToList(), refs, 40);
            menu.Show(statusLeftLabel, new Point(0, 0), ToolStripDropDownDirection.AboveRight);
        }

        private void AddRefSection(ContextMenuStrip menu, string title, List<GitRefInfo> items, List<GitRefInfo> all, int max)
        {
            if (items.Count == 0) return;
            menu.Items.Add(new ToolStripSeparator());
            ToolStripMenuItem hdr = new ToolStripMenuItem(title);
            hdr.Enabled = false;
            menu.Items.Add(hdr);
            foreach (GitRefInfo r in items.Take(max))
            {
                GitRefInfo target = r;
                ToolStripMenuItem mi = CreateMenuItem("   " + r.ShortName + (string.IsNullOrEmpty(r.RelativeDate) ? "" : "   · " + r.RelativeDate), "", delegate { CheckoutRefWin(target, all); });
                mi.Checked = r.IsHead;
                menu.Items.Add(mi);
            }
        }

        private void CheckoutRefWin(GitRefInfo target, List<GitRefInfo> refs)
        {
            if (target.IsHead) { ParityStatus("Already on " + target.ShortName + "."); return; }
            RunGitOperationWin("Checkout " + target.ShortName, delegate (string repo, CancellationToken ct, Action<string> p) { return GitRemoteOps.Checkout(repo, target, refs, ct); }, null, false);
        }

        private void PromptCreateBranchWin(string startPoint)
        {
            using (PromptInputDialog dlg = new PromptInputDialog("Git: Create Branch", startPoint == null ? "New branch name (created from HEAD):" : "New branch name (from " + startPoint + "):", "", activeTheme))
            {
                if (dlg.ShowDialog(this) != DialogResult.OK || string.IsNullOrWhiteSpace(dlg.InputValue)) return;
                string name = dlg.InputValue;
                RunGitOperationWin("Create Branch", delegate (string repo, CancellationToken ct, Action<string> p) { return GitRemoteOps.CreateBranch(repo, name, startPoint, ct); }, null, false);
            }
        }

        // ------------------------------------------------------------------------------------------------
        // Merge conflicts
        // ------------------------------------------------------------------------------------------------

        /// <summary>DevProvideAnnotation hook: conflict lens on "&lt;&lt;&lt;&lt;&lt;&lt;&lt;" lines.</summary>
        private EditorLineAnnotation ProvideParityAnnotationWin(EditorTabBuffer buf, int line)
        {
            if (buf == null || buf.Lines == null || line < 0 || line >= buf.Lines.Count) return null;
            string l = buf.Lines[line];
            if (l == null || l.Length < 7 || l[0] != '<') return null;
            if (ConflictDecorations.KindAt(buf, line) != ConflictLineKind.CurrentHeader) return null;
            ConflictBlock block = GitConflictParser.BlockAt(ConflictDecorations.Blocks(buf), line);
            if (block == null) return null;
            string key = (buf.FilePath ?? buf.FileName ?? "") + "|" + block.StartLine + "|" + block.SeparatorLine + "|" + block.EndLine;
            EditorLineAnnotation ann;
            if (!winConflictLens.TryGetValue(key, out ann))
            {
                if (winConflictLens.Count > 200) winConflictLens.Clear();
                ann = new EditorLineAnnotation();
                ann.Kind = EditorLineAnnotation.KindCodeLens;
                ann.IsAction = true;
                ann.Segments = GitConflictParser.LensSegments();
                ann.Text = string.Join(EditorLineAnnotation.SegmentSeparator, ann.Segments);
                ann.Tag = block;
                winConflictLens[key] = ann;
            }
            return ann;
        }

        /// <summary>DevAnnotationClicked hook. Returns true when the click was a conflict lens.</summary>
        private bool HandleParityAnnotationClickWin(CodeLessVirtualCanvas canvas, EditorTabBuffer buf, EditorLineAnnotation ann)
        {
            ConflictBlock b = ann != null ? ann.Tag as ConflictBlock : null;
            if (b == null) return false;
            if (ann.SegmentIndex < 0) return true;
            if (ann.SegmentIndex == 3) { CompareConflictWin(buf, b.StartLine); return true; }
            ConflictResolution? how = GitConflictParser.ResolutionForSegment(ann.SegmentIndex);
            if (how.HasValue) ResolveConflictWin(buf, b.StartLine, how.Value);
            return true;
        }

        public bool ResolveConflictWin(EditorTabBuffer buf, int line, ConflictResolution how)
        {
            if (buf == null) return false;
            ConflictBlock block = GitConflictParser.BlockAt(ConflictDecorations.Rescan(buf), line);
            if (block == null) { ParityStatus("No merge conflict at the cursor."); return false; }
            if (!ConflictDecorations.ResolveInBuffer(buf, block, how)) return false;
            winConflictLens.Clear();
            AfterExternalBufferEditWin(buf);
            int left = ConflictDecorations.Blocks(buf).Count;
            ParityStatus(left == 0 ? "All conflicts resolved in " + buf.FileName + ". Save and stage the file to mark it resolved." : left + " conflict(s) left in " + buf.FileName + ".");
            return true;
        }

        private void AfterExternalBufferEditWin(EditorTabBuffer buf)
        {
            buf.IsDirty = true;
            foreach (CodeLessEditorControl ed in new CodeLessEditorControl[] { editorControl, splitEditorControl })
            {
                if (ed == null || !ReferenceEquals(ed.CurrentBuffer, buf)) continue;
                ed.NotifyBufferContentChanged();
                ed.Canvas.Invalidate();
            }
            RefreshTabStrip();
        }

        private void AcceptAllConflictsWin(ConflictResolution how)
        {
            EditorTabBuffer buf = activeTab;
            if (buf == null) return;
            int n = 0;
            while (n < 10000)
            {
                List<ConflictBlock> blocks = ConflictDecorations.Rescan(buf);
                if (blocks.Count == 0) break;
                if (!ConflictDecorations.ResolveInBuffer(buf, blocks[blocks.Count - 1], how)) break;
                n++;
            }
            winConflictLens.Clear();
            if (n > 0) AfterExternalBufferEditWin(buf);
            ParityStatus(n == 0 ? "No merge conflicts in this file." : "Resolved " + n + " conflict(s).");
        }

        private void GoToConflictWin(bool forward)
        {
            EditorTabBuffer buf = activeTab;
            if (buf == null) return;
            List<ConflictBlock> blocks = ConflictDecorations.Rescan(buf);
            ConflictBlock b = forward ? GitConflictParser.Next(blocks, buf.CursorLine) : GitConflictParser.Previous(blocks, buf.CursorLine);
            if (b == null) { ParityStatus("No merge conflicts in this file."); return; }
            editorControl.JumpToLine(b.StartLine + 1);
            ParityStatus("Conflict " + (blocks.IndexOf(b) + 1) + " of " + blocks.Count);
        }

        private void CompareConflictWin(EditorTabBuffer buf, int line)
        {
            if (buf == null) return;
            ConflictBlock b = GitConflictParser.BlockAt(ConflictDecorations.Rescan(buf), line);
            if (b == null) { ParityStatus("No merge conflict at the cursor."); return; }
            string cur = string.Join("\n", GitConflictParser.CurrentLines(buf.Lines, b).ToArray());
            string inc = string.Join("\n", GitConflictParser.IncomingLines(buf.Lines, b).ToArray());
            string title = buf.FileName + ": Current (" + (b.CurrentLabel.Length > 0 ? b.CurrentLabel : "ours") + ") ↔ Incoming (" + (b.IncomingLabel.Length > 0 ? b.IncomingLabel : "theirs") + ")";
            WinDiffView dv = new WinDiffView(title, cur, inc, "", activeTheme);
            WinAuxDocument d = new WinAuxDocument { Id = "conflict:" + buf.FilePath + ":" + b.StartLine, Title = buf.FileName + " (Compare)", Tooltip = title, Content = dv };
            d.ApplyTheme = delegate (StudioTheme th) { dv.ApplyTheme(th); };
            DevOpen(d, false);
        }

        // ------------------------------------------------------------------------------------------------
        // Gutter hunks
        // ------------------------------------------------------------------------------------------------

        private void ShowHunkMenuWin(CodeLessEditorControl editor, EditorTabBuffer buf, int line, Rectangle bar)
        {
            if (buf == null || string.IsNullOrEmpty(buf.FilePath)) return;
            List<string> lines = new List<string>(buf.Lines);
            bool finalNl = buf.HasFinalNewline;
            string path = buf.FilePath;
            Task.Run(delegate
            {
                string repo = GitCli.FindRepoRoot(path);
                string rel = repo != null ? GitCli.RepoRelativePath(repo, path) : null;
                string index = null;
                bool bin = false;
                List<GitDiffHunk> hunks = new List<GitDiffHunk>();
                if (rel != null)
                {
                    index = GitHunkStaging.ReadIndexContent(repo, rel, out bin);
                    if (index != null) hunks = GitHunkDiff.ComputeHunks(index, lines, finalNl);
                }
                ParityPost(delegate
                {
                    if (rel == null) { ParityStatus("This file is not inside a git repository."); return; }
                    if (bin) { ParityStatus("Binary file: changes can't be staged by hunk."); return; }
                    GitDiffHunk h = index == null ? null : GitHunkDiff.HunkAtLine(hunks, line);
                    if (h == null && index != null)
                    {
                        // Already staged: offer "Unstage Change" for the HEAD-to-index hunk under this line.
                        string indexText = index;
                        Task.Run(delegate
                        {
                            string head = GitHunkStaging.ReadHeadContent(repo, rel);
                            GitDiffHunk sh = GitHunkStaging.FindStagedHunkAtEditorLine(head, indexText, lines, finalNl, line);
                            ParityPost(delegate
                            {
                                if (sh == null) { ParityStatus("No change at this line."); return; }
                                ContextMenuStrip um = CreateAutoDisposingContextMenu();
                                um.Items.Add(CreateMenuItem("Unstage Change  (" + sh.Summary + ")", "", delegate
                                {
                                    Task.Run(delegate
                                    {
                                        GitOperationResult ur = GitHunkStaging.UnstageHunk(repo, rel, head, indexText, sh);
                                        ParityPost(delegate { AfterIndexChangeWin(ur); });
                                    });
                                }));
                                um.Items.Add(CreateMenuItem("Open Diff (Index ↔ Working Tree)", "", delegate { OpenIndexDiffWin(buf, indexText); }));
                                um.Show(editor.Canvas, new Point(bar.Right + 2, bar.Bottom));
                            });
                        });
                        return;
                    }
                    ContextMenuStrip menu = CreateAutoDisposingContextMenu();
                    string idx = index ?? "";
                    menu.Items.Add(CreateMenuItem(h == null ? "Stage File (untracked)" : "Stage Change  (" + h.Summary + ")", "", delegate { StageHunkWin(repo, rel, index, h); }));
                    if (h != null) menu.Items.Add(CreateMenuItem("Revert Change", "", delegate { RevertHunkWin(buf, h); }));
                    menu.Items.Add(CreateMenuItem("Open Diff (Index ↔ Working Tree)", "", delegate { OpenIndexDiffWin(buf, idx); }));
                    menu.Show(editor.Canvas, new Point(bar.Right + 2, bar.Bottom));
                });
            });
        }

        private void StageHunkWin(string repo, string rel, string index, GitDiffHunk h)
        {
            Task.Run(delegate
            {
                GitOperationResult res;
                if (h == null)
                {
                    GitCommandResult add = GitProcess.Run(repo, new string[] { "add", "--", rel }, 30000, CancellationToken.None, null, null);
                    res = GitErrorClassifier.ToResult(add, "Stage", "Staged " + Path.GetFileName(rel) + ".");
                }
                else
                {
                    List<GitDiffHunk> list = new List<GitDiffHunk>();
                    list.Add(h);
                    res = GitHunkStaging.StageHunks(repo, rel, index, list);
                }
                ParityPost(delegate { AfterIndexChangeWin(res); });
            });
        }

        private void AfterIndexChangeWin(GitOperationResult res)
        {
            ParityStatus(res.Message);
            if (!res.Success && !string.IsNullOrEmpty(res.Details)) AppendOutputLog("[Git Stage] " + res.Details);
            RefreshGitSidebarStatus();
        }

        private void RevertHunkWin(EditorTabBuffer buf, GitDiffHunk h)
        {
            if (!GitHunkEditor.RevertInBuffer(buf, h)) { ParityStatus("The change moved; click the gutter bar again."); return; }
            AfterExternalBufferEditWin(buf);
            ParityStatus("Reverted change (" + h.Summary + "). Undo with Ctrl+Z.");
        }

        private void OpenIndexDiffWin(EditorTabBuffer buf, string index)
        {
            string text = string.Join("\n", buf.Lines.ToArray());
            WinDiffView dv = new WinDiffView(buf.FileName + "  ·  Index ↔ Working Tree", (index ?? "").Replace("\r\n", "\n"), text, "", activeTheme);
            WinAuxDocument d = new WinAuxDocument { Id = "scmdiff:" + buf.FilePath, Title = buf.FileName + " (Working Tree)", Tooltip = buf.FilePath, Content = dv };
            d.ApplyTheme = delegate (StudioTheme th) { dv.ApplyTheme(th); };
            DevOpen(d, false);
        }

        private void StageChangeAtWin(EditorTabBuffer buf, int from, int to, bool wholeHunk)
        {
            if (buf == null || string.IsNullOrEmpty(buf.FilePath)) return;
            List<string> lines = new List<string>(buf.Lines);
            bool finalNl = buf.HasFinalNewline;
            string path = buf.FilePath;
            Task.Run(delegate
            {
                GitOperationResult res;
                string repo = GitCli.FindRepoRoot(path);
                string rel = repo != null ? GitCli.RepoRelativePath(repo, path) : null;
                if (rel == null) { res = new GitOperationResult(); res.Error = GitErrorKind.Other; res.Message = "This file is not inside a git repository."; }
                else res = GitHunkStaging.StageFromEditor(repo, rel, lines, finalNl, from, to, wholeHunk);
                ParityPost(delegate { AfterIndexChangeWin(res); });
            });
        }

        private void StageSelectedRangesWin()
        {
            EditorTabBuffer buf = activeTab;
            if (buf == null) return;
            int from = buf.CursorLine, to = buf.CursorLine;
            if (buf.HasSelection())
            {
                int sl, sc, el, ec;
                buf.GetNormalizedSelection(out sl, out sc, out el, out ec);
                from = sl; to = el;
                if (ec == 0 && el > sl) to = el - 1;
            }
            StageChangeAtWin(buf, from, to, false);
        }

        private void RevertChangeAtCursorWin()
        {
            EditorTabBuffer buf = activeTab;
            if (buf == null || string.IsNullOrEmpty(buf.FilePath)) return;
            List<string> lines = new List<string>(buf.Lines);
            bool finalNl = buf.HasFinalNewline;
            string path = buf.FilePath;
            int line = buf.CursorLine;
            Task.Run(delegate
            {
                GitDiffHunk h = null;
                string repo = GitCli.FindRepoRoot(path);
                string rel = repo != null ? GitCli.RepoRelativePath(repo, path) : null;
                if (rel != null)
                {
                    bool bin;
                    string index = GitHunkStaging.ReadIndexContent(repo, rel, out bin);
                    if (index != null) h = GitHunkDiff.HunkAtLine(GitHunkDiff.ComputeHunks(index, lines, finalNl), line);
                }
                ParityPost(delegate
                {
                    if (h == null) { ParityStatus("No unstaged change at the cursor."); return; }
                    RevertHunkWin(buf, h);
                });
            });
        }

        // ------------------------------------------------------------------------------------------------
        // Source Control list: groups, amend, history
        // ------------------------------------------------------------------------------------------------

        /// <summary>BuildGitSidebarPanel hook: amend checkbox and commit-message history keys.</summary>
        private void AddParityGitControls(Panel gitBtnRow)
        {
            gitBtnRow.Height += 24;
            gitAmendCheck = new CheckBox
            {
                Text = "Amend last commit",
                Location = new Point(0, 58),
                Size = new Size(200, 22),
                FlatStyle = FlatStyle.Flat,
                Font = new Font("Segoe UI", 8.6f),
                UseMnemonic = false,
                Cursor = Cursors.Hand
            };
            gitAmendCheck.CheckedChanged += delegate
            {
                if (!gitAmendCheck.Checked || gitCommitInput == null || !string.IsNullOrWhiteSpace(gitCommitInput.Text)) return;
                string repo = ParityRepoWin();
                if (repo == null) return;
                Task.Run(delegate
                {
                    string last = GitRemoteOps.GetLastCommitMessage(repo);
                    ParityPost(delegate { if (gitAmendCheck.Checked && string.IsNullOrWhiteSpace(gitCommitInput.Text)) gitCommitInput.Text = last; });
                });
            };
            gitBtnRow.Controls.Add(gitAmendCheck);
            if (gitCommitInput != null)
            {
                gitCommitInput.KeyDown += delegate (object s, KeyEventArgs e)
                {
                    if (e.Modifiers != Keys.None || winCommitHistory == null) return;
                    if (e.KeyCode == Keys.Up)
                    {
                        string prev = winCommitHistory.Previous(gitCommitInput.Text);
                        if (prev != null) { gitCommitInput.Text = prev; e.SuppressKeyPress = true; e.Handled = true; }
                    }
                    else if (e.KeyCode == Keys.Down && winCommitHistory.IsBrowsing)
                    {
                        string next = winCommitHistory.Next();
                        if (next != null) { gitCommitInput.Text = next; e.SuppressKeyPress = true; e.Handled = true; }
                    }
                };
            }
        }

        /// <summary>performCommit hook: records history; handles "Amend" (returns true when it took over).</summary>
        private bool TryParityCommitWin(string msg)
        {
            if (!string.IsNullOrWhiteSpace(msg) && winCommitHistory != null) winCommitHistory.Add(msg);
            if (gitAmendCheck == null || !gitAmendCheck.Checked) return false;
            if (gitCommitInput != null) gitCommitInput.Clear();
            RunGitOperationWin("Commit (Amend)", delegate (string repo, CancellationToken ct, Action<string> p) { return GitRemoteOps.Commit(repo, msg, true, false, ct); },
                delegate (GitOperationResult res) { if (res.Success && gitAmendCheck != null) gitAmendCheck.Checked = false; }, false);
            return true;
        }

        private GitFileStatus GitListItemAt(int idx)
        {
            if (idx < 0 || idx >= gitListRows.Count) return null;
            return gitListRows[idx].Item;
        }

        private ScmGroup GitListGroupAt(int idx)
        {
            if (idx < 0 || idx >= gitListRows.Count) return ScmGroup.Changes;
            return gitListRows[idx].Group;
        }

        /// <summary>RefreshGitSidebarStatus hook: VS Code groups (merge / staged / changes / untracked) with headers.</summary>
        private void PopulateGroupedGitList(List<GitFileStatus> items)
        {
            gitListRows.Clear();
            ScmGroup[] order = new ScmGroup[] { ScmGroup.Merge, ScmGroup.Staged, ScmGroup.Changes, ScmGroup.Untracked };
            foreach (ScmGroup g in order)
            {
                List<GitFileStatus> inGroup = new List<GitFileStatus>();
                foreach (GitFileStatus it in items)
                {
                    char x = it.IndexStatus, y = it.WorkTreeStatus;
                    if (x == ' ' && y == ' ' && !string.IsNullOrEmpty(it.StatusCode))
                    {
                        // Older status source without X/Y: derive from the trimmed code.
                        if (it.StatusCode == "??") { x = '?'; y = '?'; }
                        else if (it.IsStaged) x = it.StatusCode[0];
                        else y = it.StatusCode[0];
                    }
                    if (ScmGrouping.GroupsFor(x, y).Contains(g)) inGroup.Add(it);
                }
                if (inGroup.Count == 0) continue;
                int conflicts = 0;
                if (g == ScmGroup.Merge) foreach (GitFileStatus it in inGroup) conflicts += CountConflictsInFileWin(it.FilePath);
                gitStatusList.Items.Add("▾ " + ScmGrouping.GroupTitle(g) + " (" + inGroup.Count + (conflicts > 0 ? ", " + conflicts + " conflict" + (conflicts == 1 ? "" : "s") : "") + ")");
                gitListRows.Add(new GitListRow { Item = null, Group = g });
                foreach (GitFileStatus it in inGroup)
                {
                    char badge = ScmGrouping.BadgeFor(g, it.IndexStatus == ' ' && it.StatusCode == "??" ? '?' : it.IndexStatus, it.WorkTreeStatus);
                    string suffix = (it.AddedLines > 0 || it.RemovedLines > 0) && g != ScmGroup.Merge ? string.Format("  (+{0} -{1})", it.AddedLines, it.RemovedLines) : "";
                    if (g == ScmGroup.Merge)
                    {
                        int n = CountConflictsInFileWin(it.FilePath);
                        if (n > 0) suffix = "  (" + n + " conflict" + (n == 1 ? "" : "s") + ")";
                    }
                    gitStatusList.Items.Add("   [" + badge + "] " + it.RelativePath + suffix);
                    gitListRows.Add(new GitListRow { Item = it, Group = g });
                }
            }
        }

        /// <summary>Staged rows: HEAD vs index; changed rows: index vs working tree (side-by-side LineDiff view).</summary>
        private bool OpenScmDiffWin(GitFileStatus item, ScmGroup g)
        {
            if (item == null || (g != ScmGroup.Staged && g != ScmGroup.Changes)) return false;
            string repo = !string.IsNullOrEmpty(item.RepoRoot) ? item.RepoRoot : GitCli.FindRepoRoot(item.FilePath);
            string rel = !string.IsNullOrEmpty(item.RepoRelativePath) ? item.RepoRelativePath : (repo != null ? GitCli.RepoRelativePath(repo, item.FilePath) : null);
            if (repo == null || rel == null) return false;
            EditorTabBuffer open = openTabs.FirstOrDefault(t => string.Equals(t.FilePath, item.FilePath, StringComparison.OrdinalIgnoreCase));
            string bufferText = open != null ? string.Join("\n", open.Lines.ToArray()) : null;
            string name = Path.GetFileName(item.FilePath);
            string path = item.FilePath;
            Task.Run(delegate
            {
                string left, right;
                bool bin;
                if (g == ScmGroup.Staged)
                {
                    left = GitHunkStaging.ReadHeadContent(repo, rel);
                    right = GitHunkStaging.ReadIndexContent(repo, rel, out bin) ?? "";
                }
                else
                {
                    left = GitHunkStaging.ReadIndexContent(repo, rel, out bin) ?? GitHunkStaging.ReadHeadContent(repo, rel);
                    try { right = bufferText ?? (File.Exists(path) ? File.ReadAllText(path) : ""); } catch { right = ""; }
                }
                ParityPost(delegate
                {
                    WinDiffView dv = new WinDiffView(rel + "  ·  " + (g == ScmGroup.Staged ? "HEAD ↔ Index" : "Index ↔ Working Tree"), (left ?? "").Replace("\r\n", "\n"), (right ?? "").Replace("\r\n", "\n"), "", activeTheme);
                    WinAuxDocument d = new WinAuxDocument { Id = "scm:" + g + ":" + path, Title = name + (g == ScmGroup.Staged ? " (Index)" : " (Working Tree)"), Tooltip = path, Content = dv };
                    d.ApplyTheme = delegate (StudioTheme th) { dv.ApplyTheme(th); };
                    DevOpen(d, false);
                });
            });
            return true;
        }

        private static int CountConflictsInFileWin(string path)
        {
            try
            {
                FileInfo fi = new FileInfo(path);
                if (!fi.Exists || fi.Length > 4 * 1024 * 1024) return 0;
                return GitConflictParser.CountConflicts(File.ReadAllText(path));
            }
            catch { return 0; }
        }

        // ------------------------------------------------------------------------------------------------
        // Tasks
        // ------------------------------------------------------------------------------------------------

        private List<TaskDefinition> LoadWorkspaceTasksWin()
        {
            if (string.IsNullOrEmpty(workspaceRoot)) return new List<TaskDefinition>();
            string err;
            List<TaskDefinition> tasks = TaskConfiguration.LoadAll(workspaceRoot, out err);
            if (err != null) { ParityStatus(err); AppendOutputLog("[Tasks] " + err); }
            return tasks.Where(t => !t.Hide).ToList();
        }

        private void ShowTaskMenuWin(List<TaskDefinition> tasks, string hint)
        {
            ContextMenuStrip menu = CreateAutoDisposingContextMenu();
            ToolStripMenuItem hdr = new ToolStripMenuItem(hint);
            hdr.Enabled = false;
            menu.Items.Add(hdr);
            menu.Items.Add(new ToolStripSeparator());
            foreach (TaskDefinition t in tasks.Take(80))
            {
                TaskDefinition task = t;
                string grp = task.Group == "build" ? "  [build]" : task.Group == "test" ? "  [test]" : "";
                menu.Items.Add(CreateMenuItem(task.DisplayLabel + grp, "", delegate { RunTaskWin(task); }));
            }
            if (tasks.Count == 0)
            {
                ToolStripMenuItem none = new ToolStripMenuItem("No tasks found (add .vscode/tasks.json, package.json, Makefile...)");
                none.Enabled = false;
                menu.Items.Add(none);
            }
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(CreateMenuItem("Configure Tasks...", "", ConfigureTasksWin));
            Control anchor = editorControl != null && editorControl.Visible ? (Control)editorControl : this;
            menu.Show(anchor, new Point(Math.Max(0, anchor.Width / 2 - 160), 8));
        }

        private void RunGroupTaskWin(string group)
        {
            List<TaskDefinition> tasks = LoadWorkspaceTasksWin();
            TaskDefinition def = TaskConfiguration.FindDefault(tasks, group);
            if (def != null) { RunTaskWin(def); return; }
            List<TaskDefinition> inGroup = TaskConfiguration.InGroup(tasks, group);
            if (inGroup.Count > 0) { ShowTaskMenuWin(inGroup, "Select the " + group + " task to run"); return; }
            ShowTaskMenuWin(tasks, "No " + group + " task found. Select a task to run");
        }

        private void ConfigureTasksWin()
        {
            string path = TaskConfiguration.TasksJsonPath(workspaceRoot);
            if (path == null) { ParityStatus("Open a folder first."); return; }
            if (!File.Exists(path))
            {
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(path));
                    File.WriteAllText(path, "{\n  // See https://code.visualstudio.com/docs/editor/tasks for the tasks.json format\n  \"version\": \"2.0.0\",\n  \"tasks\": [\n    {\n      \"label\": \"build\",\n      \"type\": \"shell\",\n      \"command\": \"echo Hello\",\n      \"group\": { \"kind\": \"build\", \"isDefault\": true },\n      \"problemMatcher\": []\n    }\n  ]\n}\n");
                }
                catch (Exception ex) { ParityStatus("Could not create tasks.json: " + ex.Message); return; }
            }
            OpenFileInTab(path);
        }

        public TaskExecution RunTaskWin(TaskDefinition task)
        {
            if (task == null) return null;
            if (string.IsNullOrEmpty(workspaceRoot)) { ParityStatus("Open a folder to run tasks."); return null; }
            SaveAllDirtyFileBuffersWin(false);
            if (task.DependsOn.Count > 0)
            {
                List<TaskDefinition> all = LoadWorkspaceTasksWin();
                List<TaskDefinition> deps = new List<TaskDefinition>();
                foreach (string l in task.DependsOn)
                {
                    TaskDefinition d = TaskConfiguration.FindByLabel(all, l);
                    if (d != null && d.Label != task.Label && d.DependsOn.Count == 0) deps.Add(d);
                }
                RunDependencyChainWin(deps, 0, task);
                return null;
            }
            return StartTaskWin(task);
        }

        private void RunDependencyChainWin(List<TaskDefinition> deps, int index, TaskDefinition final)
        {
            if (index >= deps.Count) { if (!string.IsNullOrEmpty(final.Command)) StartTaskWin(final); return; }
            TaskExecution ex = StartTaskWin(deps[index]);
            if (ex == null) { RunDependencyChainWin(deps, index + 1, final); return; }
            ex.Exited += delegate (TaskExecution e)
            {
                ParityPost(delegate
                {
                    if (e.ExitCode == 0) RunDependencyChainWin(deps, index + 1, final);
                    else ParityStatus("Task '" + final.Label + "' not started: dependency '" + deps[index].Label + "' failed.");
                });
            };
        }

        private TaskExecution StartTaskWin(TaskDefinition task)
        {
            lastRunTaskWin = task;
            foreach (RunningTaskWin r in runningTasksWin.Where(x => x.Exec.Task.DisplayLabel == task.DisplayLabel).ToList()) TerminateRunningTaskWin(r);
            DebugVariableContext ctx = new DebugVariableContext();
            ctx.WorkspaceFolder = workspaceRoot;
            ctx.File = activeTab != null ? activeTab.FilePath : null;
            ctx.LineNumber = activeTab != null ? activeTab.CursorLine + 1 : 0;
            ctx.SelectedText = activeTab != null && activeTab.HasSelection() ? activeTab.GetSelectedText() : "";
            PythonInterpreterInfo py = PythonInterpreterService.GetSelected(workspaceRoot);
            ctx.PythonInterpreter = py != null ? py.Path : null;
            bool windows = ConPtyNative.IsWindowsPlatform();
            TaskExecution exec = TaskExecution.Prepare(task, ctx, windows);
            RunningTaskWin rt = new RunningTaskWin();
            rt.Exec = exec;
            runningTasksWin.Add(rt);
            exec.MarkStarted();
            exec.Exited += delegate (TaskExecution e) { ParityPost(delegate { OnTaskExitedWin(rt); }); };
            ParityStatus("Running task '" + task.Label + "'…");
            AppendOutputLog("[Tasks] " + task.DisplayLabel + ": " + exec.CommandLine + "  (cwd " + exec.Cwd + ")");

            string termCmd = TaskConfiguration.BuildTerminalCommandLine(exec.CommandLine, exec.Env, windows, windows ? null : ConPtyTerminalSession.GetUserUnixShell());
            ConPtyTerminalSession pty = new ConPtyTerminalSession("Task", termCmd, exec.Cwd, 100, 14, activeTheme != null && activeTheme.IsLight);
            pty.RawOutputTap = exec.OnOutputBytes;
            try { pty.Buffer.ProcessText("\x1b[1m> Executing task: " + exec.CommandLine + " <\x1b[0m\r\n\r\n"); } catch { }
            pty.ProcessExited += delegate (int code) { exec.OnExited(code); };
            exec.Terminate = delegate { exec.WasTerminated = true; try { pty.Dispose(); } catch { } exec.OnExited(-1); };
            if (pty.HasExited) exec.OnExited(pty.ExitCode);
            rt.Terminal = AttachTaskTerminalWin(task, pty, exec.Cwd);
            return exec;
        }

        /// <summary>Shows the task's PTY in a terminal named after the task, reusing a finished one ("shared" panel).</summary>
        private TerminalSessionState AttachTaskTerminalWin(TaskDefinition task, ConPtyTerminalSession pty, string cwd)
        {
            if (bottomDockPanel != null && !bottomDockPanel.Visible) ToggleBottomDockVisibility();
            SwitchBottomDockTab("terminal");
            int idx = -1;
            if (task.Panel != "new")
            {
                for (int i = 0; i < terminalSessions.Count; i++)
                {
                    TerminalSessionState s = terminalSessions[i];
                    if (s.TaskLabel == task.DisplayLabel && !runningTasksWin.Any(r => ReferenceEquals(r.Terminal, s) && r.Exec.IsRunning)) { idx = i; break; }
                }
            }
            TerminalSessionState state;
            if (idx >= 0)
            {
                state = terminalSessions[idx];
                ConPtyTerminalSession old = state.ConPtySession;
                state.ConPtySession = pty;
                state.WorkingDir = cwd;
                if (old != null && !ReferenceEquals(old, pty)) { try { old.Dispose(); } catch { } }
                SwitchTerminalSession(idx);
            }
            else
            {
                state = new TerminalSessionState { Title = "Task - " + task.Label, WorkingDir = cwd, Transcript = "", ConPtySession = pty, TaskLabel = task.DisplayLabel };
                terminalSessions.Add(state);
                if (terminalSessionCombo != null)
                {
                    terminalSessionCombo.Items.Add(state.Title);
                    terminalSessionCombo.SelectedIndex = terminalSessions.Count - 1;
                }
                SwitchTerminalSession(terminalSessions.Count - 1);
            }
            RefreshTerminalTabsListUi();
            return state;
        }

        private void OnTaskExitedWin(RunningTaskWin rt)
        {
            runningTasksWin.Remove(rt);
            TaskExecution ex = rt.Exec;
            int errs = ex.ErrorCount, warns = ex.WarningCount;
            string probs = ex.Matchers.HasMatchers ? "  (" + errs + " error" + (errs == 1 ? "" : "s") + ", " + warns + " warning" + (warns == 1 ? "" : "s") + ")" : "";
            if (ex.WasTerminated) ParityStatus("Task '" + ex.Task.Label + "' terminated.");
            else if (ex.ExitCode == 0) ParityStatus("Task '" + ex.Task.Label + "' finished" + probs + ".");
            else ParityStatus("Task '" + ex.Task.Label + "' failed with exit code " + ex.ExitCode + probs + ".");
            try
            {
                if (rt.Terminal != null && rt.Terminal.ConPtySession != null && !ex.WasTerminated)
                    rt.Terminal.ConPtySession.Buffer.ProcessText("\r\n\x1b[90m *  Terminal will be reused by tasks.\x1b[0m\r\n");
                if (conPtyTerminalControl != null) conPtyTerminalControl.Invalidate();
            }
            catch { }
            ApplyTaskProblemsWin();
        }

        private void TerminateTaskWin()
        {
            List<RunningTaskWin> live = runningTasksWin.Where(r => r.Exec.IsRunning).ToList();
            if (live.Count == 0) { ParityStatus("No task is running."); return; }
            if (live.Count == 1) { TerminateRunningTaskWin(live[0]); return; }
            ContextMenuStrip menu = CreateAutoDisposingContextMenu();
            menu.Items.Add(CreateMenuItem("Terminate all running tasks", "", delegate { foreach (RunningTaskWin r in live) TerminateRunningTaskWin(r); }));
            foreach (RunningTaskWin r in live)
            {
                RunningTaskWin target = r;
                menu.Items.Add(CreateMenuItem(r.Exec.Task.DisplayLabel, "", delegate { TerminateRunningTaskWin(target); }));
            }
            menu.Show(editorControl, new Point(Math.Max(0, editorControl.Width / 2 - 160), 8));
        }

        private void TerminateRunningTaskWin(RunningTaskWin rt)
        {
            try { if (rt.Exec.Terminate != null) rt.Exec.Terminate(); } catch { }
            runningTasksWin.Remove(rt);
        }

        /// <summary>Re-merges task problems into open buffers and refreshes the Problems panel.</summary>
        private void ApplyTaskProblemsWin()
        {
            foreach (EditorTabBuffer t in openTabs)
            {
                if (t == null || string.IsNullOrEmpty(t.FilePath) || t.IsLargeFileViewer) continue;
                LspDiagnosticsStore.MergeInto(t);
            }
            RefreshOutlineAndProblems(activeTab);
            if (editorControl != null) editorControl.Invalidate(true);
            if (splitEditorControl != null && isSplitEditorActive) splitEditorControl.Invalidate(true);
        }

        /// <summary>RefreshOutlineAndProblems hook: task problems of files that are not open.</summary>
        private void AppendTaskProblemsWin(EditorTabBuffer active)
        {
            HashSet<string> open = new HashSet<string>(StringComparer.Ordinal);
            foreach (EditorTabBuffer t in openTabs) if (t != null && !string.IsNullOrEmpty(t.FilePath)) open.Add(LspText.PathKey(t.FilePath));
            foreach (DiagnosticItem d in TaskProblemStore.All())
            {
                if (open.Contains(LspText.PathKey(d.FilePath))) continue;
                WorkspaceDiagnostics.Add(d);
            }
        }

        // ------------------------------------------------------------------------------------------------
        // Save participants
        // ------------------------------------------------------------------------------------------------

        /// <summary>SaveTabBuffer hook (before writing): trim trailing whitespace / final newlines.</summary>
        private void ApplySaveParticipantsWin(EditorTabBuffer buf, bool backgroundSave)
        {
            if (buf == null || buf.IsLargeFileViewer) return;
            SaveActionOptions opts = SaveActionOptions.FromSettings(settings, backgroundSave || autoSaveDepthWin > 0);
            if (!opts.Any) return;
            if (SaveActions.ApplyToBuffer(buf, opts))
            {
                if (editorControl != null && ReferenceEquals(editorControl.CurrentBuffer, buf)) editorControl.Canvas.Invalidate();
            }
        }

        /// <summary>Explicit user save (Ctrl+S, menu, palette): editor.formatOnSave through the language server first.</summary>
        private bool SaveTabBufferUser(EditorTabBuffer buf)
        {
            if (buf != null && settings.FormatOnSave && lspController != null && !buf.IsLargeFileViewer && !string.IsNullOrEmpty(buf.FilePath) && lspController.IsActiveFor(buf))
            {
                FormatThenSaveWin(buf);
                return true;
            }
            return SaveTabBuffer(buf);
        }

        private async void FormatThenSaveWin(EditorTabBuffer buf)
        {
            string path = lspController.PathFor(buf);
            string before = LspEditing.BufferText(buf);
            List<LspTextEdit> edits = null;
            try
            {
                int tab = buf.CustomTabSize.HasValue && buf.CustomTabSize.Value > 0 ? buf.CustomTabSize.Value : (settings.TabSize > 0 ? settings.TabSize : 4);
                bool spaces = buf.CustomInsertSpaces.HasValue ? buf.CustomInsertSpaces.Value : settings.InsertSpaces;
                using (CancellationTokenSource cts = new CancellationTokenSource(1500))
                {
                    Task<List<LspTextEdit>> req = lspController.Manager.RequestFormattingAsync(path, null, tab, spaces, cts.Token);
                    Task done = await Task.WhenAny(req, Task.Delay(1600));
                    if (done == req) edits = req.Result;
                }
            }
            catch { edits = null; }
            if (edits != null && edits.Count > 0 && LspEditing.BufferText(buf) == before && LspEditing.ApplyEditsToBuffer(buf, edits, true))
            {
                if (editorControl != null && ReferenceEquals(editorControl.CurrentBuffer, buf)) { editorControl.NotifyBufferContentChanged(); editorControl.Canvas.Invalidate(); }
            }
            SaveTabBuffer(buf);
        }

        private void SaveAllDirtyFileBuffersWin(bool autoSave)
        {
            foreach (EditorTabBuffer t in openTabs.ToList())
            {
                if (t == null || !t.IsDirty || t.IsUntitled || t.IsLargeFileViewer || string.IsNullOrEmpty(t.FilePath) || !File.Exists(t.FilePath)) continue;
                if (autoSave) { autoSaveDepthWin++; try { SaveTabBuffer(t, true); } finally { autoSaveDepthWin--; } }
                else SaveTabBuffer(t);
            }
        }

        // ------------------------------------------------------------------------------------------------
        // Keys and palette commands
        // ------------------------------------------------------------------------------------------------

        /// <summary>OnKeyDown hook (before the generic shortcuts).</summary>
        private bool HandleParityKey(KeyEventArgs e)
        {
            if (e.Control && e.Shift && !e.Alt && e.KeyCode == Keys.B)
            {
                e.SuppressKeyPress = true;
                RunGroupTaskWin("build");
                return true;
            }
            return false;
        }

        public bool ExecuteParityCommand(string cmdId)
        {
            if (string.IsNullOrEmpty(cmdId) || !cmdId.StartsWith("par_", StringComparison.Ordinal)) return false;
            EditorTabBuffer buf = activeTab;
            switch (cmdId)
            {
                case "par_checkout": OpenBranchPickerWin(); break;
                case "par_create_branch": PromptCreateBranchWin(null); break;
                case "par_fetch": RunGitOperationWin("Fetch", delegate (string r, CancellationToken ct, Action<string> p) { return GitRemoteOps.Fetch(r, false, false, ct, p); }, null, false); break;
                case "par_fetch_prune": RunGitOperationWin("Fetch (Prune)", delegate (string r, CancellationToken ct, Action<string> p) { return GitRemoteOps.Fetch(r, false, true, ct, p); }, null, false); break;
                case "par_pull": RunGitOperationWin("Pull", delegate (string r, CancellationToken ct, Action<string> p) { return GitRemoteOps.Pull(r, settings.GitRebaseWhenSync, ct, p); }, null, false); break;
                case "par_pull_rebase": RunGitOperationWin("Pull (Rebase)", delegate (string r, CancellationToken ct, Action<string> p) { return GitRemoteOps.Pull(r, true, ct, p); }, null, false); break;
                case "par_push": RunGitOperationWin("Push", delegate (string r, CancellationToken ct, Action<string> p) { return GitRemoteOps.Push(r, ct, p); }, null, false); break;
                case "par_sync": RunGitOperationWin("Sync", delegate (string r, CancellationToken ct, Action<string> p) { return GitRemoteOps.Sync(r, settings.GitRebaseWhenSync, ct, p); }, null, false); break;
                case "par_publish": GitPublishWin(); break;
                case "par_cancel": CancelGitOperationWin(); break;
                case "par_amend":
                    leftSidebar.Visible = true;
                    SwitchSidebarView("git");
                    if (gitAmendCheck != null) gitAmendCheck.Checked = true;
                    if (gitCommitInput != null) gitCommitInput.Focus();
                    break;
                case "par_stage_ranges": StageSelectedRangesWin(); break;
                case "par_stage_change": if (buf != null) StageChangeAtWin(buf, buf.CursorLine, buf.CursorLine, true); break;
                case "par_revert_change": RevertChangeAtCursorWin(); break;
                case "par_accept_current": if (buf != null) ResolveConflictWin(buf, buf.CursorLine, ConflictResolution.Current); break;
                case "par_accept_incoming": if (buf != null) ResolveConflictWin(buf, buf.CursorLine, ConflictResolution.Incoming); break;
                case "par_accept_both": if (buf != null) ResolveConflictWin(buf, buf.CursorLine, ConflictResolution.Both); break;
                case "par_accept_all_current": AcceptAllConflictsWin(ConflictResolution.Current); break;
                case "par_accept_all_incoming": AcceptAllConflictsWin(ConflictResolution.Incoming); break;
                case "par_compare": if (buf != null) CompareConflictWin(buf, buf.CursorLine); break;
                case "par_next_conflict": GoToConflictWin(true); break;
                case "par_prev_conflict": GoToConflictWin(false); break;
                case "par_run_task": ShowTaskMenuWin(LoadWorkspaceTasksWin(), "Select the task to run"); break;
                case "par_run_build": RunGroupTaskWin("build"); break;
                case "par_run_test": RunGroupTaskWin("test"); break;
                case "par_rerun_task": if (lastRunTaskWin != null) RunTaskWin(lastRunTaskWin); else ParityStatus("No task has been run yet."); break;
                case "par_terminate_task": TerminateTaskWin(); break;
                case "par_configure_tasks": ConfigureTasksWin(); break;
                default: return false;
            }
            return true;
        }
    }

    /// <summary>Settings dialog block: auto save mode, save actions and git sync options (VS Code names in tooltips).</summary>
    public sealed class ParitySettingsBlockWin
    {
        public static readonly string[] AutoSaveModes = new string[] { IdePersistentSettings.AutoSaveOff, IdePersistentSettings.AutoSaveAfterDelay, IdePersistentSettings.AutoSaveOnFocusChange, IdePersistentSettings.AutoSaveOnWindowChange };
        public CodeLessDropdown AutoSaveModeCombo;
        public CheckBox FormatOnSaveBox, TrimWhitespaceBox, FinalNewlineBox, TrimFinalNewlinesBox, AutoFetchBox, RebaseWhenSyncBox;
        private readonly string origMode;
        private readonly bool origFormat, origTrim, origFinal, origTrimFinal, origFetch, origRebase;

        private ParitySettingsBlockWin(IdePersistentSettings s)
        {
            origMode = s.AutoSaveMode;
            origFormat = s.FormatOnSave;
            origTrim = s.TrimTrailingWhitespaceOnSave;
            origFinal = s.InsertFinalNewlineOnSave;
            origTrimFinal = s.TrimFinalNewlinesOnSave;
            origFetch = s.GitAutoFetch;
            origRebase = s.GitRebaseWhenSync;
        }

        public static ParitySettingsBlockWin Build(Panel body, IdePersistentSettings s, StudioTheme theme, ref int y)
        {
            ParitySettingsBlockWin b = new ParitySettingsBlockWin(s);
            ToolTip tips = new ToolTip();
            Label lbl = new Label { Location = new Point(24, y + 4), Size = new Size(210, 20), Text = "Auto Save (files.autoSave):", Font = new Font("Segoe UI", 8.8f), ForeColor = theme.FgSecondary, UseMnemonic = false };
            body.Controls.Add(lbl);
            b.AutoSaveModeCombo = new CodeLessDropdown { Location = new Point(240, y), Size = new Size(180, 26), DropDownStyle = ComboBoxStyle.DropDownList, Font = new Font("Segoe UI", 9.0f) };
            b.AutoSaveModeCombo.ApplyTheme(theme);
            foreach (string m in AutoSaveModes) b.AutoSaveModeCombo.Items.Add(m);
            b.AutoSaveModeCombo.SelectedIndex = Math.Max(0, Array.IndexOf(AutoSaveModes, s.EffectiveAutoSaveMode));
            body.Controls.Add(b.AutoSaveModeCombo);
            y += 30;
            b.FormatOnSaveBox = Check(body, tips, theme, "Format on save (language server)", s.FormatOnSave, "editor.formatOnSave", 24, y);
            b.TrimWhitespaceBox = Check(body, tips, theme, "Trim trailing whitespace on save", s.TrimTrailingWhitespaceOnSave, "files.trimTrailingWhitespace", 380, y);
            y += 24;
            b.FinalNewlineBox = Check(body, tips, theme, "Insert final newline on save", s.InsertFinalNewlineOnSave, "files.insertFinalNewline", 24, y);
            b.TrimFinalNewlinesBox = Check(body, tips, theme, "Trim final newlines on save", s.TrimFinalNewlinesOnSave, "files.trimFinalNewlines", 380, y);
            y += 24;
            b.AutoFetchBox = Check(body, tips, theme, "Git: auto fetch periodically", s.GitAutoFetch, "git.autofetch", 24, y);
            b.RebaseWhenSyncBox = Check(body, tips, theme, "Git: rebase when syncing", s.GitRebaseWhenSync, "git.rebaseWhenSync", 380, y);
            y += 28;
            return b;
        }

        private static CheckBox Check(Panel body, ToolTip tips, StudioTheme theme, string text, bool value, string vscodeName, int left, int top)
        {
            CheckBox cb = new CheckBox
            {
                Location = new Point(left, top),
                Size = new Size(340, 22),
                Text = text,
                Checked = value,
                FlatStyle = FlatStyle.Flat,
                Font = new Font("Segoe UI", 8.8f),
                ForeColor = theme.FgPrimary,
                UseMnemonic = false,
                Cursor = Cursors.Hand
            };
            tips.SetToolTip(cb, vscodeName);
            body.Controls.Add(cb);
            return cb;
        }

        public void Apply(IdePersistentSettings s)
        {
            int mi = AutoSaveModeCombo.SelectedIndex;
            s.SetAutoSaveMode(mi >= 0 && mi < AutoSaveModes.Length ? AutoSaveModes[mi] : IdePersistentSettings.AutoSaveOff);
            s.FormatOnSave = FormatOnSaveBox.Checked;
            s.TrimTrailingWhitespaceOnSave = TrimWhitespaceBox.Checked;
            s.InsertFinalNewlineOnSave = FinalNewlineBox.Checked;
            s.TrimFinalNewlinesOnSave = TrimFinalNewlinesBox.Checked;
            s.GitAutoFetch = AutoFetchBox.Checked;
            s.GitRebaseWhenSync = RebaseWhenSyncBox.Checked;
        }

        public void Revert(IdePersistentSettings s)
        {
            s.AutoSaveMode = origMode;
            s.FormatOnSave = origFormat;
            s.TrimTrailingWhitespaceOnSave = origTrim;
            s.InsertFinalNewlineOnSave = origFinal;
            s.TrimFinalNewlinesOnSave = origTrimFinal;
            s.GitAutoFetch = origFetch;
            s.GitRebaseWhenSync = origRebase;
        }
    }
}
