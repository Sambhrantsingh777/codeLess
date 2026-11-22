using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace codeLess.Studio
{
    /// <summary>
    /// Agent trust features on Windows (same behaviour as the Avalonia UI, shared logic in codeLess.Core):
    /// verification gate in a private temporary git clone / copy with optional auto-repair, a review panel in the chat dock
    /// (per-file Accept / Reject, Accept All, Accept if Verified, Verify, Fix with AI, Stop), per-turn checkpoints with an
    /// Agent Timeline (Undo This Turn / Undo Back to Here / Redo, confirmation listing files and conflicts) and the
    /// Best-of-N Arena. Strict C# 5 (legacy csc).
    /// Hooks: InitAgentTrustWin (chat dock), BeginAgentTurnWin / PresentAgentTurnWin (agent + Ctrl+K turns),
    /// AddAgentTrustMenuItems (Help menu), HandleTurnRestoreRequestWin (user message "Restore"), OpenBestOfNDialogWin (Arena).
    /// </summary>
    public partial class CodeLessMainForm
    {
        private AgentTrustCoordinator agentTrustWin;
        private Panel agentReviewPanel;
        private Label agentReviewTitle;
        private ListView agentReviewList;
        private Button arAcceptAll, arAcceptVerified, arRejectAll, arAcceptFile, arRejectFile, arVerify, arFix, arStop;

        /// <summary>Test hook: answers verification approval prompts without UI.</summary>
        public Func<VerificationApprovalRequest, VerificationApprovalDecision> AgentVerifyApprovalOverride { get; set; }
        /// <summary>Test hook: "cancel", "skip" (keep conflicting files) or "overwrite" for undo / redo confirmations.</summary>
        public Func<TurnRestorePlan, string> AgentRestoreConfirmOverride { get; set; }

        public AgentTrustCoordinator AgentTrust { get { return agentTrustWin; } }

        private Button verifyChipWin;

        private void InitAgentTrustWin(Panel chatTopBar, Control leftOf)
        {
            agentTrustWin = new AgentTrustCoordinator(settings);
            agentTrustWin.RequestApproval = RequestVerifyApprovalWin;
            FormClosed += (s, e) => { try { agentTrustWin.CancelRun(); } catch { } };
            BuildAgentReviewPanel();
            if (chatTopBar != null && leftOf != null)
            {
                // "Verify: Off ▾" chip in the chat header (Avalonia has it in the composer): mode + command.
                verifyChipWin = new Button { FlatStyle = FlatStyle.Flat, UseMnemonic = false, Cursor = Cursors.Hand, Height = 22, Tag = "chip", Font = new Font("Segoe UI", UiTokens.Pt(UiTokens.FontSmall)) };
                verifyChipWin.FlatAppearance.BorderSize = 0;
                verifyChipWin.Click += (s, e) => ShowVerifyMenuWin(verifyChipWin);
                chatTopBar.Controls.Add(verifyChipWin);
                EventHandler place = (s, e) =>
                {
                    verifyChipWin.Width = TextRenderer.MeasureText(verifyChipWin.Text ?? "", verifyChipWin.Font).Width + 16;
                    verifyChipWin.Location = new Point(Math.Max(60, leftOf.Left - verifyChipWin.Width - 6), 3);
                };
                chatTopBar.Resize += place;
                UpdateVerifyChipWin();
                place(null, EventArgs.Empty);
            }
            ThreadPool.QueueUserWorkItem(delegate (object st) { try { VerificationOverlay.CleanupStale(TimeSpan.FromHours(12)); } catch { } });
        }

        private void BuildAgentReviewPanel()
        {
            agentReviewPanel = new Panel { Dock = DockStyle.Bottom, Height = 156, Visible = false, Padding = new Padding(8, 4, 8, 4) };
            agentReviewTitle = new Label { Dock = DockStyle.Top, Height = 22, AutoEllipsis = true, TextAlign = ContentAlignment.MiddleLeft, Font = new Font("Segoe UI", UiTokens.Pt(UiTokens.FontUi), FontStyle.Bold) };
            agentReviewList = new ListView
            {
                Dock = DockStyle.Fill,
                View = View.Details,
                FullRowSelect = true,
                HideSelection = false,
                HeaderStyle = ColumnHeaderStyle.None,
                BorderStyle = BorderStyle.None,
                MultiSelect = true,
                Font = new Font("Segoe UI", UiTokens.Pt(UiTokens.FontSmall))
            };
            agentReviewList.Columns.Add("File", 200);
            agentReviewList.Columns.Add("Changes", 70);
            agentReviewList.Columns.Add("Status", 70);
            agentReviewList.Resize += (s, e) => { int w = Math.Max(80, agentReviewList.ClientSize.Width - 150); agentReviewList.Columns[0].Width = w; };
            agentReviewList.SelectedIndexChanged += (s, e) => UpdateAgentReviewButtons();
            agentReviewList.DoubleClick += (s, e) => ReviewSelectedAgentFileWin();

            FlowLayoutPanel buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 58, FlowDirection = FlowDirection.LeftToRight, WrapContents = true, Padding = new Padding(0, 2, 0, 0) };
            arAcceptAll = ReviewButton("Accept All", true, () => { AgentChangeSet cs = agentTrustWin.ActiveChangeSet; if (cs != null) AcceptAgentChangeSetWin(cs, false); });
            arAcceptVerified = ReviewButton("Accept if Verified", false, () => { AgentChangeSet cs = agentTrustWin.ActiveChangeSet; if (cs != null) AcceptAgentChangeSetWin(cs, true); });
            arRejectAll = ReviewButton("Reject All", false, () => { AgentChangeSet cs = agentTrustWin.ActiveChangeSet; if (cs != null) RejectAgentFilesWin(cs, cs.Files.Where(f => f.IsPending).ToList()); });
            arAcceptFile = ReviewButton("Accept File", false, () => { AgentChangeSet cs = agentTrustWin.ActiveChangeSet; if (cs != null) AcceptAgentFilesWin(cs, SelectedAgentFiles(), false); });
            arRejectFile = ReviewButton("Reject File", false, () => { AgentChangeSet cs = agentTrustWin.ActiveChangeSet; if (cs != null) RejectAgentFilesWin(cs, SelectedAgentFiles()); });
            arVerify = ReviewButton("Verify", false, () => { AgentChangeSet cs = agentTrustWin.ActiveChangeSet; if (cs != null) StartAgentVerificationWin(cs, BuildAgentTurnRequestWin(cs.Label, currentAgentMode, null), AgentVerifyMode.Verify); });
            arFix = ReviewButton("Fix with AI", false, () => { AgentChangeSet cs = agentTrustWin.ActiveChangeSet; if (cs != null) StartAgentVerificationWin(cs, BuildAgentTurnRequestWin(cs.Label, currentAgentMode, null), AgentVerifyMode.VerifyAndRepair); });
            arStop = ReviewButton("Stop", false, () => agentTrustWin.CancelRun());
            Button timeline = ReviewButton("Timeline…", false, () => OpenAgentTimelineDialogWin());
            buttons.Controls.AddRange(new Control[] { arAcceptAll, arAcceptVerified, arRejectAll, arAcceptFile, arRejectFile, arVerify, arFix, arStop, timeline });

            agentReviewPanel.Controls.Add(agentReviewList);
            agentReviewPanel.Controls.Add(buttons);
            agentReviewPanel.Controls.Add(agentReviewTitle);
            rightDock.Controls.Add(agentReviewPanel);
            // Directly above the multi-file changeset bar (docking order = reverse z-order).
            rightDock.Controls.SetChildIndex(agentReviewPanel, rightDock.Controls.GetChildIndex(multiFileChangesetBar));
            ApplyAgentReviewTheme();
        }

        private Button ReviewButton(string text, bool primary, Action onClick)
        {
            Button b = CreateHeaderButton(text, new Point(0, 0), Math.Max(64, TextRenderer.MeasureText(text, new Font("Segoe UI", 8.5f)).Width + 18), primary);
            b.Height = 24;
            b.Margin = new Padding(0, 2, 4, 2);
            b.Click += (s, e) => onClick();
            return b;
        }

        private void ApplyAgentReviewTheme()
        {
            if (agentReviewPanel == null || activeTheme == null) return;
            agentReviewPanel.BackColor = UiTokens.WidgetBackground(activeTheme);
            agentReviewList.BackColor = UiTokens.WidgetBackground(activeTheme);
            agentReviewList.ForeColor = activeTheme.FgPrimary;
            ApplyThemeToControlTree(agentReviewPanel, activeTheme);
            UpdateVerifyChipWin();
            RefreshAgentReviewPanel();
        }

        private List<AgentChangeSetFile> SelectedAgentFiles()
        {
            List<AgentChangeSetFile> res = new List<AgentChangeSetFile>();
            foreach (ListViewItem it in agentReviewList.SelectedItems)
            {
                AgentChangeSetFile f = it.Tag as AgentChangeSetFile;
                if (f != null && f.IsPending) res.Add(f);
            }
            return res;
        }

        public void RefreshAgentReviewPanel()
        {
            if (agentReviewPanel == null || agentTrustWin == null) return;
            if (InvokeRequired) { BeginInvoke(new Action(RefreshAgentReviewPanel)); return; }
            AgentChangeSet cs = agentTrustWin.ActiveChangeSet;
            bool running = agentTrustWin.IsRunning;
            if (cs == null && !running)
            {
                agentReviewPanel.Visible = false;
                return;
            }
            agentReviewPanel.Visible = true;
            VerificationResult v = cs != null ? cs.LatestVerification : null;
            string title = cs == null ? "Agent run in progress" : (cs.PendingCount == 1 ? "1 file pending review" : cs.PendingCount + " files pending review");
            if (running) title += " · verifying…";
            else if (v != null) title += " · " + v.Badge;
            agentReviewTitle.Text = title;
            agentReviewTitle.ForeColor = v != null && !running ? AgentTrustColors.ForStatus(activeTheme, v.Status) : activeTheme.FgPrimary;
            if (v != null && v.Status == VerificationStatus.NotRun) agentReviewTitle.ForeColor = activeTheme.FgPrimary;
            agentReviewList.BeginUpdate();
            agentReviewList.Items.Clear();
            if (cs != null)
            {
                foreach (AgentChangeSetFile f in cs.Files)
                {
                    ListViewItem it = new ListViewItem((f.IsNewFile ? "(new) " : "") + f.RelativePath);
                    it.SubItems.Add("+" + f.Added + " −" + f.Removed);
                    it.SubItems.Add(f.Status);
                    it.Tag = f;
                    if (!f.IsPending) it.ForeColor = activeTheme.FgSecondary;
                    agentReviewList.Items.Add(it);
                }
                if (v != null && v.Errors.Count > 0)
                {
                    foreach (string line in v.FormatErrorLines(5))
                    {
                        ListViewItem err = new ListViewItem(line);
                        err.ForeColor = AgentTrustColors.Failed(activeTheme);
                        err.Tag = null;
                        agentReviewList.Items.Add(err);
                    }
                }
            }
            agentReviewList.EndUpdate();
            UpdateAgentReviewButtons();
        }

        private void UpdateAgentReviewButtons()
        {
            if (agentReviewPanel == null) return;
            AgentChangeSet cs = agentTrustWin.ActiveChangeSet;
            bool running = agentTrustWin.IsRunning;
            bool pending = cs != null && cs.HasPending;
            VerificationResult v = cs != null ? cs.LatestVerification : null;
            arAcceptAll.Enabled = pending && !running;
            arAcceptVerified.Enabled = pending && !running && cs.IsVerifiedPassing;
            arRejectAll.Enabled = pending && !running;
            bool sel = SelectedAgentFiles().Count > 0;
            arAcceptFile.Enabled = sel && !running;
            arRejectFile.Enabled = sel && !running;
            arVerify.Enabled = pending && !running;
            arFix.Enabled = pending && !running && v != null && v.IsRepairable;
            arStop.Enabled = running;
        }

        // ------------------------------------------------------------------ approval

        private VerificationApprovalDecision RequestVerifyApprovalWin(VerificationApprovalRequest req)
        {
            if (AgentVerifyApprovalOverride != null) return AgentVerifyApprovalOverride(req);
            if (!IsHandleCreated || IsDisposed) return VerificationApprovalDecision.Deny;
            if (!InvokeRequired) return VerificationApprovalDecision.Deny; // never block the UI thread waiting for itself
            VerificationApprovalDecision decision = VerificationApprovalDecision.Deny;
            try
            {
                Invoke(new Action(() =>
                {
                    using (AgentApprovalDialogWin dlg = new AgentApprovalDialogWin(req, activeTheme))
                    {
                        dlg.ShowDialog(this);
                        decision = dlg.Decision;
                    }
                }));
            }
            catch { decision = VerificationApprovalDecision.Deny; }
            return decision;
        }

        // ------------------------------------------------------------------ turns

        private Func<string, string> SnapshotBufferTextsWin()
        {
            Dictionary<string, string> map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (EditorTabBuffer t in openTabs)
            {
                if (string.IsNullOrEmpty(t.FilePath) || t.IsLargeFileViewer) continue;
                try { map[Path.GetFullPath(t.FilePath)] = t.PreDiffContent != null ? null : (t.Content ?? ""); } catch { }
            }
            return p =>
            {
                try { string s; return map.TryGetValue(Path.GetFullPath(p), out s) ? s : null; } catch { return null; }
            };
        }

        private AgentTurnRequest BuildAgentTurnRequestWin(string prompt, string mode, ByokVendor vendor)
        {
            if (vendor == null)
            {
                int vendorIdx = quickVendorCombo != null ? Math.Max(0, quickVendorCombo.SelectedIndex) : 0;
                vendor = vendorIdx < aiEngine.Vendors.Count ? aiEngine.Vendors[vendorIdx] : aiEngine.Vendors.FirstOrDefault();
            }
            AgentTurnRequest req = new AgentTurnRequest();
            req.Engine = aiEngine;
            req.Mode = mode;
            req.Prompt = prompt;
            req.Vendor = vendor;
            req.HarnessId = settings.ActiveHarnessId;
            req.ActiveTab = AgentTurnInputs.SnapshotTab(activeTab); // repair / Best-of-N turns run on worker threads
            req.WorkspaceRoot = workspaceRoot;
            req.WorkspaceFiles = workspaceFiles != null ? workspaceFiles.ToList() : new List<string>();
            req.Pills = attachedContextPills != null ? attachedContextPills.ToList() : new List<string>();
            req.BaseTextProvider = SnapshotBufferTextsWin();
            return req;
        }

        /// <summary>Starts tracking a turn (snapshot before its first terminal command).</summary>
        private AgentTurnTracker BeginAgentTurnWin(string prompt)
        {
            return agentTrustWin.BeginTurn(aiEngine, workspaceRoot, prompt);
        }

        private Task<List<TurnFileChange>> EndAgentTurnWin(AgentTurnTracker tracker)
        {
            LlmHarnessAndAgentEngine engine = aiEngine;
            AgentTrustCoordinator coord = agentTrustWin;
            return Task.Run(() => coord.EndTurn(engine, tracker));
        }

        /// <summary>
        /// After a turn: change set (base texts captured before inline staging), inline staging of existing files in
        /// the editor, the review panel, a checkpoint note when terminal commands changed files, then verification.
        /// </summary>
        private void PresentAgentTurnWin(AgentExecutionTurnResult turn, AgentTurnRequest req, string turnId, List<TurnFileChange> terminalChanges)
        {
            if (terminalChanges != null && terminalChanges.Count > 0)
            {
                AgentTurnRecord rec = agentTrustWin.GetStore(workspaceRoot) != null ? agentTrustWin.GetStore(workspaceRoot).FindTurn(turnId) : null;
                string files = rec != null ? string.Join(", ", rec.DescribeFiles().Take(6).ToArray()) : terminalChanges.Count + " file(s)";
                if (agentFeedControl != null)
                    agentFeedControl.AddMessage(false, "Checkpoint", "", "The agent's commands changed files: " + files + ".\nUse **Timeline…** in the review panel (or **Restore** on your message) to undo this turn.");
                foreach (TurnFileChange c in terminalChanges)
                {
                    EditorTabBuffer t = FindOpenTabWin(Path.Combine(workspaceRoot ?? "", c.Path.Replace('/', Path.DirectorySeparatorChar)));
                    if (t != null) ReloadTabFromDiskWin(t);
                }
                AfterExplorerFileOperation(workspaceRoot);
            }
            if (turn == null || turn.StagedFileContents == null || turn.StagedFileContents.Count == 0) { RefreshAgentReviewPanel(); return; }
            Func<string, string> baseText = req != null && req.BaseTextProvider != null ? req.BaseTextProvider : SnapshotBufferTextsWin();
            AgentChangeSet cs = agentTrustWin.CreateChangeSet(turn.StagedFileContents, workspaceRoot, baseText, ShortLabelWin(req != null ? req.Prompt : ""), turnId);
            cs.ProviderLabel = turn.ProviderUsed;
            // A newer proposal for a file supersedes older pending ones.
            foreach (AgentChangeSet older in agentTrustWin.ChangeSets.Where(c => !ReferenceEquals(c, cs)).ToList())
                foreach (AgentChangeSetFile f in cs.Files)
                {
                    AgentChangeSetFile stale = older.Find(f.FullPath);
                    if (stale != null && stale.IsPending) stale.Status = "Rejected";
                }
            ApplyStagedTurnEdits(turn, cs.Label);
            RefreshAgentReviewPanel();
            AgentVerifyMode mode = settings.EffectiveAgentVerifyMode;
            if (mode != AgentVerifyMode.Off && cs.Files.Count > 0)
                StartAgentVerificationWin(cs, req, mode);
        }

        private static string ShortLabelWin(string s)
        {
            s = (s ?? "").Replace('\n', ' ').Trim();
            return s.Length > 60 ? s.Substring(0, 59) + "…" : s;
        }

        private EditorTabBuffer FindOpenTabWin(string path)
        {
            if (string.IsNullOrEmpty(path)) return null;
            string full;
            try { full = Path.GetFullPath(path); } catch { full = path; }
            return openTabs.FirstOrDefault(t => !string.IsNullOrEmpty(t.FilePath) && string.Equals(SafeFullPathWin(t.FilePath), full, StringComparison.OrdinalIgnoreCase));
        }

        private static string SafeFullPathWin(string p)
        {
            try { return Path.GetFullPath(p); } catch { return p; }
        }

        private static bool IsInlineStagedWin(EditorTabBuffer t)
        {
            return t != null && (t.PreDiffContent != null || (t.DiffHunks != null && t.DiffHunks.Any(h => h.IsPending)));
        }

        /// <summary>A clean editor follows the file on disk (after accept / undo); dirty editors are left alone.</summary>
        private void ReloadTabFromDiskWin(EditorTabBuffer tab)
        {
            if (tab == null || tab.IsDirty || string.IsNullOrEmpty(tab.FilePath)) return;
            if (AutoSavePolicy.IsUnderReview(tab)) return; // an inline proposal is being reviewed against this text
            if (!File.Exists(tab.FilePath)) { tab.IsDeletedOnDisk = true; RefreshTabStrip(); return; }
            try
            {
                string eol; System.Text.Encoding enc; bool bom; bool finalNl;
                string content = EditorTabBuffer.ReadFilePreservingMetadata(tab.FilePath, out eol, out enc, out bom, out finalNl);
                tab.Content = content;
                tab.SavedContent = content;
                tab.EolSequence = eol;
                tab.FileEncoding = enc;
                tab.HasBom = bom;
                tab.HasFinalNewline = finalNl;
                tab.IsDeletedOnDisk = false;
                tab.SyncLinesFromContent();
                tab.IsDirty = false;
                tab.RefreshGutterDiff();
                if (ReferenceEquals(editorControl.CurrentBuffer, tab)) editorControl.LoadBuffer(tab);
                RefreshTabStrip();
            }
            catch { }
        }

        // ------------------------------------------------------------------ verification

        private void StartAgentVerificationWin(AgentChangeSet cs, AgentTurnRequest req, AgentVerifyMode mode)
        {
            if (cs == null) return;
            CancellationToken token = agentTrustWin.BeginRun(); // cancels a verification still running for an older proposal
            CodeLessAgentFeedControl.AgentMessageCard card = agentFeedControl != null ? agentFeedControl.BeginStreamingMessage("Verification", "") : null;
            StringBuilder log = new StringBuilder();
            RefreshAgentReviewPanel();
            Action<AgentVerifiedTurnEvent> progress = e =>
            {
                if (e.Kind == "stopped" || string.IsNullOrEmpty(e.Message)) return;
                lock (log) log.AppendLine("- " + e.Message);
                string text;
                lock (log) text = log.ToString();
                Action ui = () =>
                {
                    if (agentFeedControl != null && card != null) agentFeedControl.UpdateStreamingMessage(card, text);
                };
                try { if (IsHandleCreated && !IsDisposed) BeginInvoke(ui); } catch { }
            };
            AgentTrustCoordinator coord = agentTrustWin;
            Task<AgentVerifiedTurnOutcome> t = coord.VerifyAndRepairAsync(cs, req, mode, progress, token); // snapshot on the UI thread
            t.ContinueWith(done =>
            {
                Action finish = () =>
                {
                    agentTrustWin.EndRun(token);
                    AgentVerifiedTurnOutcome outcome = done.Status == TaskStatus.RanToCompletion ? done.Result : null;
                    AgentTrustCoordinator.ApplyOutcome(cs, outcome, req != null ? req.BaseTextProvider : null);
                    StringBuilder md = new StringBuilder();
                    VerificationResult v = cs.LatestVerification;
                    md.AppendLine(v != null ? v.ToMarkdown() : "Verification didn't complete" + (done.Exception != null ? ": " + done.Exception.GetBaseException().Message : "."));
                    if (outcome != null && outcome.RepairAttempts > 0)
                    {
                        md.AppendLine();
                        md.AppendLine(outcome.StopReason == "passed"
                            ? "Auto-repair fixed it in " + outcome.RepairAttempts + " attempt(s)."
                            : "Auto-repair stopped after " + outcome.RepairAttempts + " attempt(s) (" + outcome.StopReason + ").");
                        RestageRepairedFilesWin(cs);
                    }
                    if (v != null && v.Passed) md.AppendLine("Click **Accept if Verified** in the review panel to apply exactly these changes.");
                    if (agentFeedControl != null && card != null) agentFeedControl.EndStreamingMessage(card, md.ToString().TrimEnd(), null, null, v != null ? v.Badge : "");
                    if (v != null) statusLeftLabel.Text = "Agent changes: " + v.Summary;
                    RefreshAgentReviewPanel();
                };
                try { if (IsHandleCreated && !IsDisposed) BeginInvoke(finish); } catch { }
            });
        }

        /// <summary>Repair turns replaced proposals: re-stage inline editors with the repaired text.</summary>
        private void RestageRepairedFilesWin(AgentChangeSet cs)
        {
            foreach (AgentChangeSetFile f in cs.Files.Where(x => x.IsPending))
            {
                EditorTabBuffer tab = FindOpenTabWin(f.FullPath);
                if (tab == null || !IsInlineStagedWin(tab)) continue;
                if (ReferenceEquals(editorControl.CurrentBuffer, tab))
                    editorControl.StageProposedContent(f.ProposedText, cs.Label);
                else
                {
                    InlineDiffReview.Stage(tab, AgentChangeSet.Normalize(f.ProposedText));
                }
            }
            RefreshChangesetReviewBar();
            RefreshTabStrip();
        }

        // ------------------------------------------------------------------ accept / reject

        public AgentApplyResult AcceptAgentChangeSetWin(AgentChangeSet cs, bool onlyIfVerified)
        {
            List<AgentChangeSetFile> pending = cs.Files.Where(f => f.IsPending).ToList();
            if (onlyIfVerified)
            {
                string why = null;
                if (!cs.IsVerifiedPassing) why = cs.LatestVerification == null ? "these changes haven't been verified" : "the latest verification didn't pass for these exact changes";
                else
                    foreach (AgentChangeSetFile f in pending)
                    {
                        EditorTabBuffer tab = FindOpenTabWin(f.FullPath);
                        if (tab != null && IsInlineStagedWin(tab) && AgentChangeSet.Normalize(tab.Content).TrimEnd('\n') != AgentChangeSet.Normalize(f.ProposedText).TrimEnd('\n'))
                        { why = f.RelativePath + " was edited in the editor after verification"; break; }
                    }
                if (why != null)
                {
                    AgentApplyResult refused = new AgentApplyResult();
                    refused.Errors.Add("Not accepted: " + why + ". Re-run verification first.");
                    if (agentFeedControl != null) agentFeedControl.AddMessage(false, "Review", "", refused.Errors[0]);
                    statusLeftLabel.Text = refused.Errors[0];
                    return refused;
                }
            }
            return AcceptAgentFilesWin(cs, pending, false);
        }

        public AgentApplyResult AcceptAgentFilesWin(AgentChangeSet cs, List<AgentChangeSetFile> files, bool overwriteConflicts)
        {
            AgentApplyResult total = new AgentApplyResult();
            AgentTurnCheckpointStore store = agentTrustWin.GetStore(cs.WorkspaceRoot);
            foreach (AgentChangeSetFile f in files.Where(x => x.IsPending).ToList())
            {
                EditorTabBuffer tab = FindOpenTabWin(f.FullPath);
                if (tab != null && IsInlineStagedWin(tab))
                {
                    string nowHash = File.Exists(f.FullPath) ? AgentHash.FileSha1(f.FullPath) : null;
                    if (!overwriteConflicts && nowHash != f.DiskHashAtStage) { total.Conflicts.Add(f.RelativePath + " changed on disk since the proposal"); continue; }
                    byte[] pre = File.Exists(f.FullPath) ? File.ReadAllBytes(f.FullPath) : null;
                    // Per-hunk decisions made in the editor are kept: the buffer is what gets saved.
                    agentDecisionInProgress = true;
                    try
                    {
                        if (ReferenceEquals(editorControl.CurrentBuffer, tab)) editorControl.AcceptAllHunks();
                        else InlineDiffReview.AcceptAll(tab);
                    }
                    finally { agentDecisionInProgress = false; }
                    bool saved;
                    agentSaveInProgress = true;
                    try { saved = SaveTabBuffer(tab); }
                    finally { agentSaveInProgress = false; }
                    if (saved)
                    {
                        byte[] post = File.Exists(f.FullPath) ? File.ReadAllBytes(f.FullPath) : null;
                        if (store != null) store.RecordFileWrite(cs.TurnId, cs.Label, f.FullPath, pre, post);
                        f.Status = "Accepted";
                        total.Written.Add(f.FullPath);
                    }
                    else total.Errors.Add(f.RelativePath + ": save failed");
                    continue;
                }
                if (tab != null && tab.IsDirty && !overwriteConflicts) { total.Conflicts.Add(f.RelativePath + " has unsaved changes in the editor"); continue; }
                AgentApplyResult r = agentTrustWin.Accept(cs, new AgentChangeSetFile[] { f }, overwriteConflicts);
                total.Written.AddRange(r.Written);
                foreach (string c in r.Conflicts) total.Conflicts.Add(c + " changed on disk since the proposal");
                total.Errors.AddRange(r.Errors);
                if (r.Notes.Count > 0 && agentFeedControl != null) agentFeedControl.AddMessage(false, "Review", "", string.Join(" ", r.Notes.ToArray()));
                if (r.Written.Count > 0 && tab != null) ReloadTabFromDiskWin(tab);
            }
            if (total.Written.Count > 0)
            {
                AfterExplorerFileOperation(workspaceRoot);
                statusLeftLabel.Text = "Accepted " + total.Written.Count + " file(s) — undo from the Agent Timeline.";
            }
            if ((total.Conflicts.Count > 0 || total.Errors.Count > 0) && agentFeedControl != null)
                agentFeedControl.AddMessage(false, "Review", "", "Not applied: " + string.Join("; ", total.Conflicts.Concat(total.Errors).ToArray()) + ". Review those files, then accept again.");
            RefreshChangesetReviewBar();
            RefreshTabStrip();
            RefreshAgentReviewPanel();
            return total;
        }

        public void RejectAgentFilesWin(AgentChangeSet cs, List<AgentChangeSetFile> files)
        {
            foreach (AgentChangeSetFile f in files.Where(x => x.IsPending).ToList())
            {
                EditorTabBuffer tab = FindOpenTabWin(f.FullPath);
                if (tab != null && IsInlineStagedWin(tab))
                {
                    agentDecisionInProgress = true;
                    try
                    {
                        if (ReferenceEquals(editorControl.CurrentBuffer, tab)) editorControl.RejectAllHunks();
                        else InlineDiffReview.RejectAll(tab);
                    }
                    finally { agentDecisionInProgress = false; }
                }
                f.Status = "Rejected";
            }
            RefreshChangesetReviewBar();
            RefreshTabStrip();
            RefreshAgentReviewPanel();
        }

        private void ReviewSelectedAgentFileWin()
        {
            List<AgentChangeSetFile> sel = SelectedAgentFiles();
            if (sel.Count == 0) return;
            AgentChangeSetFile f = sel[0];
            if (f.IsNewFile && !File.Exists(f.FullPath))
            {
                if (agentFeedControl != null) agentFeedControl.AddMessage(false, "Proposed new file", "", "### " + f.RelativePath + "\n```\n" + f.ProposedText + "\n```");
                return;
            }
            EditorTabBuffer tab = OpenFileInTab(f.FullPath);
            if (tab == null) return;
            if (!IsInlineStagedWin(tab))
            {
                if (tab.IsDirty) { statusLeftLabel.Text = f.RelativePath + " has unsaved changes: save or revert them first."; return; }
                editorControl.StageProposedContent(f.ProposedText, "Agent proposal");
                RefreshChangesetReviewBar();
            }
        }

        private bool agentDecisionInProgress;

        /// <summary>
        /// Accept All / Reject All in the editor banner (or Ctrl+Enter / Ctrl+Backspace) on a file with a pending agent
        /// proposal: accepting writes the reviewed buffer and records it in the turn checkpoint, rejecting marks the file
        /// rejected. Non-agent inline proposals (Ctrl+K, code-block apply) stay in the buffer, dirty, like VS Code.
        /// </summary>
        private void OnEditorDiffAllDecisionWin(EditorTabBuffer buf, bool accepted)
        {
            if (agentDecisionInProgress || agentSaveInProgress || agentTrustWin == null || buf == null || string.IsNullOrEmpty(buf.FilePath)) return;
            AgentChangeSet cs = null;
            AgentChangeSetFile f = null;
            foreach (AgentChangeSet c in agentTrustWin.ChangeSets.ToList())
            {
                AgentChangeSetFile hit = c.Find(buf.FilePath);
                if (hit != null && hit.IsPending) { cs = c; f = hit; }
            }
            if (cs == null) return;
            if (!accepted)
            {
                f.Status = "Rejected";
            }
            else
            {
                string nowHash = File.Exists(f.FullPath) ? AgentHash.FileSha1(f.FullPath) : null;
                if (nowHash != f.DiskHashAtStage)
                {
                    string msg = f.RelativePath + " changed on disk since the proposal: the accepted text is in the editor, unsaved. Review it and save (Ctrl+S) to keep it.";
                    if (agentFeedControl != null) agentFeedControl.AddMessage(false, "Review", "", msg);
                    statusLeftLabel.Text = msg;
                }
                else
                {
                    byte[] pre = File.Exists(f.FullPath) ? File.ReadAllBytes(f.FullPath) : null;
                    bool saved;
                    agentSaveInProgress = true;
                    try { saved = SaveTabBuffer(buf); }
                    finally { agentSaveInProgress = false; }
                    if (saved)
                    {
                        byte[] post = File.Exists(f.FullPath) ? File.ReadAllBytes(f.FullPath) : null;
                        AgentTurnCheckpointStore store = agentTrustWin.GetStore(cs.WorkspaceRoot);
                        if (store != null) store.RecordFileWrite(cs.TurnId, cs.Label, f.FullPath, pre, post);
                        f.Status = "Accepted";
                        statusLeftLabel.Text = "Accepted " + f.RelativePath + " — undo from the Agent Timeline.";
                    }
                }
            }
            RefreshChangesetReviewBar();
            RefreshTabStrip();
            RefreshAgentReviewPanel();
        }

        private sealed class AgentTrackedSaveWin
        {
            public AgentChangeSet ChangeSet;
            public AgentChangeSetFile File;
            public byte[] Pre;
        }

        private bool agentSaveInProgress;

        /// <summary>Save hook: manual saves of files with a pending agent proposal are recorded in that turn's checkpoint.</summary>
        private object BeginAgentTrackedSaveWin(EditorTabBuffer buf)
        {
            if (agentSaveInProgress || agentTrustWin == null || buf == null || string.IsNullOrEmpty(buf.FilePath)) return null;
            foreach (AgentChangeSet c in agentTrustWin.ChangeSets.ToList())
            {
                AgentChangeSetFile f = c.Find(buf.FilePath);
                if (f == null || !f.IsPending) continue;
                try
                {
                    AgentTrackedSaveWin s = new AgentTrackedSaveWin();
                    s.ChangeSet = c;
                    s.File = f;
                    s.Pre = File.Exists(f.FullPath) ? File.ReadAllBytes(f.FullPath) : null;
                    return s;
                }
                catch { return null; }
            }
            return null;
        }

        private void EndAgentTrackedSaveWin(object token, EditorTabBuffer buf)
        {
            AgentTrackedSaveWin s = token as AgentTrackedSaveWin;
            if (s == null) return;
            try
            {
                byte[] post = File.Exists(s.File.FullPath) ? File.ReadAllBytes(s.File.FullPath) : null;
                AgentTurnCheckpointStore store = agentTrustWin.GetStore(s.ChangeSet.WorkspaceRoot);
                if (store != null) store.RecordFileWrite(s.ChangeSet.TurnId, s.ChangeSet.Label, s.File.FullPath, s.Pre, post);
                if (!IsInlineStagedWin(buf)) s.File.Status = "Accepted";
                else s.File.DiskHashAtStage = post != null ? AgentHash.Sha1Hex(post) : null; // saved mid-review: not a conflict later
                RefreshAgentReviewPanel();
            }
            catch { }
        }

        // ------------------------------------------------------------------ undo / redo

        /// <summary>The "Restore" button on a user message: undo that turn and everything after it.</summary>
        private bool HandleTurnRestoreRequestWin(string cpId)
        {
            if (string.IsNullOrEmpty(cpId) || !cpId.StartsWith("turn-", StringComparison.Ordinal)) return false;
            AgentTurnCheckpointStore store = agentTrustWin.GetStore(workspaceRoot);
            if (store == null || store.FindTurn(cpId) == null)
            {
                statusLeftLabel.Text = "This turn didn't change any files (or they were not accepted), so there is nothing to restore.";
                return true;
            }
            UndoAgentTurnWin(cpId, true);
            return true;
        }

        private List<string> DirtyBufferPathsWin()
        {
            return openTabs.Where(t => t.IsDirty && !string.IsNullOrEmpty(t.FilePath)).Select(t => t.FilePath).ToList();
        }

        public TurnRestoreResult UndoAgentTurnWin(string turnId, bool backToHere)
        {
            AgentTurnCheckpointStore store = agentTrustWin.GetStore(workspaceRoot);
            TurnRestorePlan plan = store != null ? store.PlanUndo(turnId, backToHere, DirtyBufferPathsWin()) : null;
            if (plan == null) { statusLeftLabel.Text = "That turn was already undone or is no longer in the timeline."; return null; }
            return ExecuteRestorePlanWin(store, plan, backToHere ? "Undo Back to Here" : "Undo This Turn");
        }

        public TurnRestoreResult RedoAgentTurnWin()
        {
            AgentTurnCheckpointStore store = agentTrustWin.GetStore(workspaceRoot);
            TurnRestorePlan plan = store != null ? store.PlanRedo(DirtyBufferPathsWin()) : null;
            if (plan == null) { statusLeftLabel.Text = "Nothing to redo."; return null; }
            return ExecuteRestorePlanWin(store, plan, "Redo");
        }

        private TurnRestoreResult ExecuteRestorePlanWin(AgentTurnCheckpointStore store, TurnRestorePlan plan, string title)
        {
            string decision;
            if (AgentRestoreConfirmOverride != null) decision = AgentRestoreConfirmOverride(plan);
            else if (!plan.Actionable.Any()) decision = "skip";
            else
            {
                using (AgentRestoreConfirmDialogWin dlg = new AgentRestoreConfirmDialogWin(plan, title, activeTheme))
                {
                    dlg.ShowDialog(this);
                    decision = dlg.Decision;
                }
            }
            if (decision == "cancel") return null;
            TurnRestoreResult result = store.Execute(plan, decision == "overwrite");
            foreach (string rel in result.Restored)
            {
                EditorTabBuffer t = FindOpenTabWin(Path.Combine(store.WorkspaceRoot, rel.Replace('/', Path.DirectorySeparatorChar)));
                if (t != null) ReloadTabFromDiskWin(t);
            }
            AfterExplorerFileOperation(workspaceRoot);
            StringBuilder msg = new StringBuilder();
            msg.Append(title).Append(": restored ").Append(result.Restored.Count).Append(" file(s)");
            if (result.Skipped.Count > 0) msg.Append(", kept ").Append(result.Skipped.Count).Append(" changed file(s) as they are (").Append(string.Join(", ", result.Skipped.Take(5).ToArray())).Append(")");
            if (result.Errors.Count > 0) msg.Append(", errors: ").Append(string.Join("; ", result.Errors.ToArray()));
            if (result.Backups.Count > 0) msg.Append(". Your overwritten versions were backed up (Redo restores them).");
            if (agentFeedControl != null) agentFeedControl.AddMessage(false, "Timeline", "", msg.ToString());
            statusLeftLabel.Text = msg.ToString();
            RefreshTabStrip();
            return result;
        }

        public void OpenAgentTimelineDialogWin()
        {
            AgentTurnCheckpointStore store = agentTrustWin.GetStore(workspaceRoot);
            if (store == null) { statusLeftLabel.Text = "Open a folder to record agent checkpoints."; return; }
            using (AgentTimelineDialogWin dlg = new AgentTimelineDialogWin(this, store, activeTheme))
                dlg.ShowDialog(this);
        }

        /// <summary>Palette entries: category, title, shortcut, command id.</summary>
        public static readonly string[][] AgentTrustPaletteCommands = new string[][]
        {
            new string[] { "Agent", "Agent: Verification Off", "", "agt_verify_off" },
            new string[] { "Agent", "Agent: Verify Proposed Changes", "", "agt_verify_on" },
            new string[] { "Agent", "Agent: Verify + Auto-Repair", "", "agt_verify_repair" },
            new string[] { "Agent", "Agent: Verification Command...", "", "agt_verify_settings" },
            new string[] { "Agent", "Agent: Verify Pending Changes", "", "agt_verify_now" },
            new string[] { "Agent", "Agent: Timeline (Undo / Redo Turns)...", "", "agt_timeline" },
            new string[] { "Agent", "Agent: Redo Last Undone Turn", "", "agt_redo" }
        };

        private bool ExecuteAgentTrustCommandWin(string id)
        {
            switch (id)
            {
                case "agt_verify_off": SetAgentVerifyModeWin(AgentVerifyMode.Off); return true;
                case "agt_verify_on": SetAgentVerifyModeWin(AgentVerifyMode.Verify); return true;
                case "agt_verify_repair": SetAgentVerifyModeWin(AgentVerifyMode.VerifyAndRepair); return true;
                case "agt_verify_settings": OpenVerificationSettingsWin(); return true;
                case "agt_verify_now":
                    {
                        AgentChangeSet cs = agentTrustWin.ActiveChangeSet;
                        if (cs != null) StartAgentVerificationWin(cs, BuildAgentTurnRequestWin(cs.Label, currentAgentMode, null), AgentVerifyMode.Verify);
                        else statusLeftLabel.Text = "No pending agent changes.";
                        return true;
                    }
                case "agt_timeline": OpenAgentTimelineDialogWin(); return true;
                case "agt_redo": RedoAgentTurnWin(); return true;
            }
            return false;
        }

        // ------------------------------------------------------------------ menu + Best-of-N

        private void AddAgentTrustMenuItems(ToolStripMenuItem menu)
        {
            ToolStripMenuItem verify = new ToolStripMenuItem("Agent Verification");
            ToolStripMenuItem off = CreateMenuItem("Off", "", () => SetAgentVerifyModeWin(AgentVerifyMode.Off));
            ToolStripMenuItem on = CreateMenuItem("Verify Proposed Changes", "", () => SetAgentVerifyModeWin(AgentVerifyMode.Verify));
            ToolStripMenuItem repair = CreateMenuItem("Verify + Auto-Repair", "", () => SetAgentVerifyModeWin(AgentVerifyMode.VerifyAndRepair));
            verify.DropDownOpening += (s, e) =>
            {
                AgentVerifyMode m = settings.EffectiveAgentVerifyMode;
                off.Checked = m == AgentVerifyMode.Off;
                on.Checked = m == AgentVerifyMode.Verify;
                repair.Checked = m == AgentVerifyMode.VerifyAndRepair;
            };
            verify.DropDownItems.Add(off);
            verify.DropDownItems.Add(on);
            verify.DropDownItems.Add(repair);
            verify.DropDownItems.Add(new ToolStripSeparator());
            verify.DropDownItems.Add(CreateMenuItem("Verification Command...", "", () => OpenVerificationSettingsWin()));
            menu.DropDownItems.Add(verify);
            menu.DropDownItems.Add(CreateMenuItem("Agent Timeline (Undo / Redo Turns)...", "", () => OpenAgentTimelineDialogWin()));
        }

        private void UpdateVerifyChipWin()
        {
            if (verifyChipWin == null || activeTheme == null) return;
            AgentVerifyMode m = settings.EffectiveAgentVerifyMode;
            verifyChipWin.Text = (m == AgentVerifyMode.Off ? "Verify: Off" : m == AgentVerifyMode.Verify ? "✓ Verify" : "✓ Verify + Repair") + " ▾";
            verifyChipWin.BackColor = verifyChipWin.Parent != null ? verifyChipWin.Parent.BackColor : activeTheme.BgSidebar;
            verifyChipWin.ForeColor = m == AgentVerifyMode.Off ? activeTheme.FgSecondary : AgentTrustColors.Passed(activeTheme);
            verifyChipWin.Width = TextRenderer.MeasureText(verifyChipWin.Text, verifyChipWin.Font).Width + 16;
        }

        private void ShowVerifyMenuWin(Control anchor)
        {
            ContextMenuStrip menu = CreateAutoDisposingContextMenu();
            AgentVerifyMode m = settings.EffectiveAgentVerifyMode;
            ToolStripMenuItem off = new ToolStripMenuItem("Off", null, (s, e) => SetAgentVerifyModeWin(AgentVerifyMode.Off)) { Checked = m == AgentVerifyMode.Off };
            ToolStripMenuItem on = new ToolStripMenuItem("Verify Proposed Changes", null, (s, e) => SetAgentVerifyModeWin(AgentVerifyMode.Verify)) { Checked = m == AgentVerifyMode.Verify };
            ToolStripMenuItem rep = new ToolStripMenuItem("Verify + Auto-Repair (up to " + settings.EffectiveAgentRepairMaxAttempts + " attempts)", null, (s, e) => SetAgentVerifyModeWin(AgentVerifyMode.VerifyAndRepair)) { Checked = m == AgentVerifyMode.VerifyAndRepair };
            menu.Items.Add(off);
            menu.Items.Add(on);
            menu.Items.Add(rep);
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("Verification Command...", null, (s, e) => OpenVerificationSettingsWin());
            menu.Items.Add("Agent Timeline (Undo / Redo Turns)...", null, (s, e) => OpenAgentTimelineDialogWin());
            menu.Items.Add("Best-of-N Arena...", null, (s, e) => OpenBestOfNDialogWin());
            menu.Show(anchor, new Point(0, anchor.Height));
        }

        public void SetAgentVerifyModeWin(AgentVerifyMode mode)
        {
            settings.SetAgentVerifyMode(mode);
            settings.Save();
            UpdateVerifyChipWin();
            statusLeftLabel.Text = "Agent verification: " + IdePersistentSettings.AgentVerifyModeLabel(mode);
        }

        private void OpenVerificationSettingsWin()
        {
            VerificationCommand resolved = !string.IsNullOrEmpty(workspaceRoot) ? VerificationCommandResolver.Resolve(workspaceRoot, settings.AgentVerifyCommand) : null;
            using (Form f = new Form())
            {
                f.Text = "Agent Verification";
                f.FormBorderStyle = FormBorderStyle.FixedDialog;
                f.StartPosition = FormStartPosition.CenterParent;
                f.MinimizeBox = false;
                f.MaximizeBox = false;
                f.ClientSize = new Size(560, 236);
                f.BackColor = UiTokens.WidgetBackground(activeTheme);
                f.ForeColor = activeTheme.FgPrimary;
                f.Font = new Font("Segoe UI", UiTokens.Pt(UiTokens.FontUi));
                Label cur = new Label { Left = 12, Top = 10, Width = 536, Height = 40, Text = "Current workspace: " + (resolved != null ? resolved.CommandLine + "  (" + resolved.Source + ")" : "no command found (.codeless/verify, \"codeless.verifyCommand\" or the setting below)") };
                Label l1 = new Label { Left = 12, Top = 56, Width = 536, Text = "User command (used when the workspace doesn't set one):" };
                TextBox cmd = new TextBox { Left = 12, Top = 78, Width = 536, Text = settings.AgentVerifyCommand ?? "" };
                Label l2 = new Label { Left = 12, Top = 112, Width = 120, Text = "Timeout (s):" };
                NumericUpDown timeout = new NumericUpDown { Left = 132, Top = 110, Width = 70, Minimum = 10, Maximum = 3600, Value = settings.EffectiveAgentVerifyTimeoutSeconds };
                Label l3 = new Label { Left = 220, Top = 112, Width = 110, Text = "Repair attempts:" };
                NumericUpDown attempts = new NumericUpDown { Left = 332, Top = 110, Width = 50, Minimum = 1, Maximum = 5, Value = settings.EffectiveAgentRepairMaxAttempts };
                Label l4 = new Label { Left = 400, Top = 112, Width = 80, Text = "Best-of-N:" };
                NumericUpDown bestN = new NumericUpDown { Left = 482, Top = 110, Width = 50, Minimum = 2, Maximum = 4, Value = settings.EffectiveAgentBestOfNCount };
                Label note = new Label { Left = 12, Top = 142, Width = 536, Height = 46, ForeColor = activeTheme.FgSecondary, Text = "Verification runs in a private temporary git clone (or a filtered copy) with the AI's staged edits applied; your files are never written. Repository-chosen commands ask for approval the first time." };
                Button save = CreateHeaderButton("Save", new Point(388, 198), 76, true);
                Button cancel = CreateHeaderButton("Cancel", new Point(472, 198), 76, false);
                save.DialogResult = DialogResult.OK;
                cancel.DialogResult = DialogResult.Cancel;
                f.AcceptButton = save;
                f.CancelButton = cancel;
                f.Controls.AddRange(new Control[] { cur, l1, cmd, l2, timeout, l3, attempts, l4, bestN, note, save, cancel });
                ApplyThemeToControlTree(f, activeTheme);
                if (f.ShowDialog(this) == DialogResult.OK)
                {
                    settings.AgentVerifyCommand = (cmd.Text ?? "").Trim();
                    settings.AgentVerifyTimeoutSeconds = (int)timeout.Value;
                    settings.AgentRepairMaxAttempts = (int)attempts.Value;
                    settings.AgentBestOfNCount = (int)bestN.Value;
                    settings.Save();
                }
            }
        }

        public void OpenBestOfNDialogWin()
        {
            using (BestOfNDialogWin dlg = new BestOfNDialogWin(this, activeTheme, promptInputBox != null ? promptInputBox.Text : ""))
                dlg.ShowDialog(this);
        }

        /// <summary>Used by the Best-of-N dialog: request built from the current editor / workspace state.</summary>
        internal AgentTurnRequest BuildBestOfNRequestWin(string prompt)
        {
            string mode = currentAgentMode == "ask" ? "composer" : currentAgentMode;
            return BuildAgentTurnRequestWin(prompt, mode, null);
        }

        /// <summary>Adopts a candidate: it becomes the reviewable change set (nothing is written).</summary>
        internal void AdoptBestOfNCandidateWin(BestOfNCandidateResult r)
        {
            AgentChangeSet cs = agentTrustWin.Adopt(r);
            if (cs == null) return;
            AgentExecutionTurnResult fake = new AgentExecutionTurnResult();
            foreach (KeyValuePair<string, string> kv in cs.PendingProposals()) fake.StagedFileContents[kv.Key] = kv.Value;
            ApplyStagedTurnEdits(fake, "[Best-of-N: " + r.Spec.Label + "]");
            if (agentFeedControl != null)
                agentFeedControl.AddMessage(false, "Best-of-N", r.VerificationLabel, "Adopted **" + r.Spec.Label + "** (" + r.VerificationLabel + ", +" + r.Added + " −" + r.Removed + "). Review it in the panel; nothing is written until you accept.");
            RefreshAgentReviewPanel();
        }
    }

    /// <summary>Approval prompt for verification commands (Don't Run / Run Once / Always Allow in This Workspace).</summary>
    public class AgentApprovalDialogWin : Form
    {
        public VerificationApprovalDecision Decision = VerificationApprovalDecision.Deny;

        public AgentApprovalDialogWin(VerificationApprovalRequest req, StudioTheme theme)
        {
            Text = "Run Verification Command?";
            FormBorderStyle = FormBorderStyle.FixedDialog;
            StartPosition = FormStartPosition.CenterParent;
            MinimizeBox = false;
            MaximizeBox = false;
            ClientSize = new Size(560, 250);
            BackColor = UiTokens.WidgetBackground(theme);
            ForeColor = theme.FgPrimary;
            Font = new Font("Segoe UI", UiTokens.Pt(UiTokens.FontUi));
            Label reason = new Label { Left = 12, Top = 10, Width = 536, Height = 48, Text = req.Reason ?? "" };
            TextBox cmd = new TextBox { Left = 12, Top = 62, Width = 536, Height = 60, Multiline = true, ReadOnly = true, Text = req.CommandLine ?? "", Font = new Font("Consolas", 9f), BackColor = theme.BgEditor, ForeColor = theme.FgPrimary };
            Label src = new Label { Left = 12, Top = 128, Width = 536, Height = 18, ForeColor = theme.FgSecondary, Text = "Source: " + req.Source };
            Label files = new Label { Left = 12, Top = 148, Width = 536, Height = 40, ForeColor = AgentTrustColors.Warning(theme), Text = req.BuildControlFiles.Count > 0 ? "Build files edited by the AI: " + string.Join(", ", req.BuildControlFiles.ToArray()) : "" };
            Button deny = MakeButton("Don't Run", false, theme);
            Button once = MakeButton("Run Once", true, theme);
            Button always = MakeButton("Always Allow in This Workspace", false, theme);
            deny.Click += (s, e) => { Decision = VerificationApprovalDecision.Deny; Close(); };
            once.Click += (s, e) => { Decision = VerificationApprovalDecision.AllowOnce; Close(); };
            always.Click += (s, e) => { Decision = VerificationApprovalDecision.AllowAlwaysForWorkspace; Close(); };
            once.SetBounds(448, 208, 100, 28);
            always.SetBounds(222, 208, 220, 28);
            deny.SetBounds(once.Left - (req.CanAlwaysAllow ? 332 : 106), 208, 100, 28);
            always.Visible = req.CanAlwaysAllow;
            Controls.AddRange(new Control[] { reason, cmd, src, files, deny, once, always });
            CancelButton = deny;
            Shown += (s, e) => deny.Focus(); // default = safe choice
        }

        internal static Button MakeButton(string text, bool primary, StudioTheme theme)
        {
            Button b = new Button { Text = text, FlatStyle = FlatStyle.Flat, UseMnemonic = false, Cursor = Cursors.Hand };
            b.FlatAppearance.BorderSize = primary ? 0 : 1;
            b.FlatAppearance.BorderColor = theme.BorderSubtle;
            b.BackColor = primary ? UiTokens.ButtonPrimaryBackground(theme) : UiTokens.ButtonSecondaryBackground(theme);
            b.ForeColor = primary ? UiTokens.ButtonPrimaryForeground(theme) : theme.FgPrimary;
            return b;
        }
    }

    /// <summary>Undo / redo confirmation: lists the files (restore / delete / recreate) and conflicts with reasons.</summary>
    public class AgentRestoreConfirmDialogWin : Form
    {
        public string Decision = "cancel";

        public AgentRestoreConfirmDialogWin(TurnRestorePlan plan, string title, StudioTheme theme)
        {
            Text = title + "?";
            FormBorderStyle = FormBorderStyle.FixedDialog;
            StartPosition = FormStartPosition.CenterParent;
            MinimizeBox = false;
            MaximizeBox = false;
            ClientSize = new Size(600, 360);
            BackColor = UiTokens.WidgetBackground(theme);
            ForeColor = theme.FgPrimary;
            Font = new Font("Segoe UI", UiTokens.Pt(UiTokens.FontUi));
            Label head = new Label { Left = 12, Top = 10, Width = 576, Height = 20, Text = plan.IsRedo ? "Redo restores the files to how they were before the undo:" : "These files go back to how they were before the turn:" };
            ListBox list = new ListBox { Left = 12, Top = 34, Width = 576, Height = 220, BorderStyle = BorderStyle.FixedSingle, BackColor = theme.BgEditor, ForeColor = theme.FgPrimary, Font = new Font("Consolas", 9f), HorizontalScrollbar = true };
            foreach (TurnRestoreItem it in plan.Actionable)
            {
                string verb = it.Unrestorable ? "can't restore (no backup)" : it.Action;
                list.Items.Add(verb + "  " + it.Path + (it.Conflict ? "  — " + it.ConflictReason : ""));
            }
            Label note = new Label { Left = 12, Top = 260, Width = 576, Height = 48, ForeColor = theme.FgSecondary, Text = plan.HasConflicts ? "Some files changed after the turn (your edits or a later turn). They are kept as they are unless you choose Overwrite; overwritten versions are backed up." : "" };
            Button cancel = AgentApprovalDialogWin.MakeButton("Cancel", false, theme);
            Button over = AgentApprovalDialogWin.MakeButton("Overwrite My Changes", false, theme);
            Button ok = AgentApprovalDialogWin.MakeButton(plan.HasConflicts ? "Restore Unchanged Files" : title, true, theme);
            ok.SetBounds(418, 318, 170, 30);
            over.SetBounds(252, 318, 160, 30);
            cancel.SetBounds(plan.HasConflicts ? 146 : 312, 318, 100, 30);
            over.Visible = plan.HasConflicts;
            cancel.Click += (s, e) => { Decision = "cancel"; Close(); };
            over.Click += (s, e) => { Decision = "overwrite"; Close(); };
            ok.Click += (s, e) => { Decision = "skip"; Close(); };
            Controls.AddRange(new Control[] { head, list, note, cancel, over, ok });
            CancelButton = cancel;
        }
    }

    /// <summary>Agent Timeline: every turn that changed files, with Undo This Turn / Undo Back to Here / Redo.</summary>
    public class AgentTimelineDialogWin : Form
    {
        private readonly CodeLessMainForm owner;
        private readonly AgentTurnCheckpointStore store;
        private readonly ListView list;
        private readonly Button undoBtn, backBtn, redoBtn;

        public AgentTimelineDialogWin(CodeLessMainForm owner, AgentTurnCheckpointStore store, StudioTheme theme)
        {
            this.owner = owner;
            this.store = store;
            Text = "Agent Timeline";
            FormBorderStyle = FormBorderStyle.Sizable;
            StartPosition = FormStartPosition.CenterParent;
            MinimizeBox = false;
            ClientSize = new Size(680, 420);
            BackColor = UiTokens.WidgetBackground(theme);
            ForeColor = theme.FgPrimary;
            Font = new Font("Segoe UI", UiTokens.Pt(UiTokens.FontUi));
            Label head = new Label { Dock = DockStyle.Top, Height = 34, Padding = new Padding(10, 8, 10, 0), ForeColor = theme.FgSecondary, Text = "Every agent turn that changed files (accepted edits and its terminal commands). Stored on disk, so it survives restarts." };
            list = new ListView { Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true, HideSelection = false, MultiSelect = false, BorderStyle = BorderStyle.None, BackColor = theme.BgEditor, ForeColor = theme.FgPrimary };
            list.Columns.Add("Time", 70);
            list.Columns.Add("Turn", 230);
            list.Columns.Add("Files", 300);
            list.Columns.Add("State", 70);
            FlowLayoutPanel buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 42, FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(8, 6, 8, 6) };
            Button close = AgentApprovalDialogWin.MakeButton("Close", false, theme);
            undoBtn = AgentApprovalDialogWin.MakeButton("Undo This Turn", true, theme);
            backBtn = AgentApprovalDialogWin.MakeButton("Undo Back to Here", false, theme);
            redoBtn = AgentApprovalDialogWin.MakeButton("Redo Last Undo", false, theme);
            foreach (Button b in new Button[] { close, undoBtn, backBtn, redoBtn }) { b.Height = 28; b.Width = Math.Max(90, TextRenderer.MeasureText(b.Text, Font).Width + 24); }
            close.Click += (s, e) => Close();
            undoBtn.Click += (s, e) => Run(false);
            backBtn.Click += (s, e) => Run(true);
            redoBtn.Click += (s, e) => { owner.RedoAgentTurnWin(); Reload(); };
            buttons.Controls.AddRange(new Control[] { close, undoBtn, backBtn, redoBtn });
            list.SelectedIndexChanged += (s, e) => UpdateButtons();
            Controls.Add(list);
            Controls.Add(buttons);
            Controls.Add(head);
            Reload();
        }

        private void Reload()
        {
            list.BeginUpdate();
            list.Items.Clear();
            List<AgentTurnRecord> turns = store.Turns;
            for (int i = turns.Count - 1; i >= 0; i--)
            {
                AgentTurnRecord t = turns[i];
                ListViewItem it = new ListViewItem(t.CreatedUtc.ToLocalTime().ToString("HH:mm"));
                it.SubItems.Add(t.Label);
                it.SubItems.Add(string.Join(", ", t.DescribeFiles().Take(4).ToArray()) + (t.Files.Count > 4 ? " …" : ""));
                it.SubItems.Add(t.Undone ? "undone" : "");
                it.Tag = t;
                list.Items.Add(it);
            }
            list.EndUpdate();
            UpdateButtons();
        }

        private void UpdateButtons()
        {
            AgentTurnRecord t = list.SelectedItems.Count > 0 ? list.SelectedItems[0].Tag as AgentTurnRecord : null;
            undoBtn.Enabled = t != null && !t.Undone;
            backBtn.Enabled = t != null && !t.Undone;
            redoBtn.Enabled = store.CanRedo;
        }

        private void Run(bool backToHere)
        {
            AgentTurnRecord t = list.SelectedItems.Count > 0 ? list.SelectedItems[0].Tag as AgentTurnRecord : null;
            if (t == null) return;
            owner.UndoAgentTurnWin(t.Id, backToHere);
            Reload();
        }
    }

    /// <summary>Best-of-N Arena: the same request on N providers/models at once, each verified; adopt one.</summary>
    public class BestOfNDialogWin : Form
    {
        private readonly CodeLessMainForm owner;
        private readonly TextBox prompt;
        private readonly ListView list;
        private readonly Label note;
        private readonly Button runBtn, adoptBtn, stopBtn;
        private BestOfNRun run;
        private CancellationTokenSource cts;
        private bool running;

        public BestOfNDialogWin(CodeLessMainForm owner, StudioTheme theme, string initialPrompt)
        {
            this.owner = owner;
            Text = "Best-of-N Arena";
            StartPosition = FormStartPosition.CenterParent;
            MinimizeBox = false;
            ClientSize = new Size(760, 440);
            BackColor = UiTokens.WidgetBackground(theme);
            ForeColor = theme.FgPrimary;
            Font = new Font("Segoe UI", UiTokens.Pt(UiTokens.FontUi));
            int n = owner.AgentTrust.Settings != null ? owner.AgentTrust.Settings.EffectiveAgentBestOfNCount : 2;
            Label head = new Label { Left = 12, Top = 10, Width = 736, Height = 34, ForeColor = theme.FgSecondary, Text = "Runs the request on " + n + " providers/models at once, verifies each candidate in a temporary copy, and lets you adopt one. Nothing is written until you accept." + (NetworkLedger.OfflineMode ? " Offline Mode: only local models are used." : "") };
            prompt = new TextBox { Left = 12, Top = 48, Width = 640, Height = 54, Multiline = true, Text = initialPrompt ?? "", BackColor = theme.BgEditor, ForeColor = theme.FgPrimary };
            runBtn = AgentApprovalDialogWin.MakeButton("Run Best of " + n, true, theme);
            runBtn.SetBounds(660, 48, 88, 28);
            stopBtn = AgentApprovalDialogWin.MakeButton("Stop", false, theme);
            stopBtn.SetBounds(660, 80, 88, 24);
            list = new ListView { Left = 12, Top = 112, Width = 736, Height = 260, View = View.Details, FullRowSelect = true, HideSelection = false, MultiSelect = false, BackColor = theme.BgEditor, ForeColor = theme.FgPrimary, BorderStyle = BorderStyle.FixedSingle };
            list.Columns.Add("Candidate", 250);
            list.Columns.Add("Result", 170);
            list.Columns.Add("Diff", 110);
            list.Columns.Add("Latency", 80);
            list.Columns.Add("Cost", 100);
            note = new Label { Left = 12, Top = 378, Width = 560, Height = 40, ForeColor = theme.FgSecondary };
            adoptBtn = AgentApprovalDialogWin.MakeButton("Adopt Selected", true, theme);
            adoptBtn.SetBounds(608, 392, 140, 30);
            runBtn.Click += (s, e) => Start();
            stopBtn.Click += (s, e) => { if (cts != null) cts.Cancel(); };
            adoptBtn.Click += (s, e) => Adopt();
            list.SelectedIndexChanged += (s, e) => UpdateButtons();
            list.DoubleClick += (s, e) => Adopt();
            FormClosing += (s, e) => { if (cts != null) cts.Cancel(); };
            Controls.AddRange(new Control[] { head, prompt, runBtn, stopBtn, list, note, adoptBtn });
            UpdateButtons();
        }

        private void UpdateButtons()
        {
            runBtn.Enabled = !running;
            stopBtn.Enabled = running;
            BestOfNCandidateResult r = list.SelectedItems.Count > 0 ? list.SelectedItems[0].Tag as BestOfNCandidateResult : null;
            adoptBtn.Enabled = !running && r != null && r.HasEdits;
        }

        private void Start()
        {
            string p = (prompt.Text ?? "").Trim();
            if (p.Length == 0 || running) return;
            AgentTurnRequest req = owner.BuildBestOfNRequestWin(p);
            AgentTrustCoordinator coord = owner.AgentTrust;
            run = coord.PlanBestOfN(req, coord.Settings != null ? coord.Settings.EffectiveAgentBestOfNCount : 2);
            if (run.Specs.Count < 2)
            {
                note.Text = "Best-of-N needs at least two providers or models. " + run.Note;
                return;
            }
            note.Text = run.Note;
            running = true;
            cts = new CancellationTokenSource();
            Render();
            UpdateButtons();
            CancellationToken token = cts.Token;
            BestOfNRun r0 = run;
            Action<BestOfNCandidateResult> onUpdate = r =>
            {
                lock (r0) { if (!r0.Results.Contains(r)) r0.Results.Add(r); }
                try { if (IsHandleCreated && !IsDisposed) BeginInvoke(new Action(Render)); } catch { }
            };
            Task<BestOfNRun> t = Task.Run(() => coord.RunBestOfNAsync(req, r0, true, onUpdate, token));
            t.ContinueWith(done =>
            {
                Action fin = () =>
                {
                    running = false;
                    if (done.Exception != null) note.Text = "Best-of-N failed: " + done.Exception.GetBaseException().Message;
                    Render();
                    UpdateButtons();
                };
                try { if (IsHandleCreated && !IsDisposed) BeginInvoke(fin); } catch { }
            });
        }

        private void Render()
        {
            if (run == null) return;
            List<BestOfNCandidateResult> results;
            lock (run) results = run.Results.ToList();
            list.BeginUpdate();
            list.Items.Clear();
            foreach (BestOfNCandidateSpec spec in run.Specs)
            {
                BestOfNCandidateResult r = results.FirstOrDefault(x => ReferenceEquals(x.Spec, spec));
                ListViewItem it = new ListViewItem(spec.Label + (r != null && r.Recommended ? "  (recommended)" : ""));
                string state = r == null || r.State == "pending" || r.State == "running" ? "running…" : r.State == "verifying" ? "verifying…" : (!string.IsNullOrEmpty(r.Error) ? r.Error : r.VerificationLabel);
                it.SubItems.Add(state);
                it.SubItems.Add(r != null && r.Turn != null ? "+" + r.Added + " −" + r.Removed + " · " + r.FilesChanged + " file(s)" : "");
                it.SubItems.Add(r != null && r.Turn != null ? VerificationResult.FormatDuration(r.LatencyMs) : "");
                it.SubItems.Add(r != null && r.Turn != null ? r.CostLabel : "");
                it.Tag = r;
                list.Items.Add(it);
            }
            list.EndUpdate();
            UpdateButtons();
        }

        private void Adopt()
        {
            BestOfNCandidateResult r = list.SelectedItems.Count > 0 ? list.SelectedItems[0].Tag as BestOfNCandidateResult : null;
            if (r == null || !r.HasEdits || running) return;
            owner.AdoptBestOfNCandidateWin(r);
            Close();
        }
    }
}
