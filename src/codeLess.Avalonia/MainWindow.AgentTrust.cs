using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using codeLess.Studio;

namespace codeLess.AvaloniaApp
{
    /// <summary>
    /// Agent trust features on macOS/Linux: verification gate (temporary copy + build/test + problem matchers) with
    /// optional auto-repair, per-file change-set review wired to disk writes, "Accept only if verified", per-turn
    /// checkpoints with Undo this turn / Undo back to here / Redo, and Best-of-N across providers. Shared logic lives in
    /// codeLess.Core (AgentTrustCoordinator & co). Hooks: InitAgentTrust (constructor), PresentAgentTurn
    /// (SendChatMessageAsync), RefreshAgentTrustBanner (RefreshChangesetBanner), OnChangesetAcceptAll / RejectAll
    /// (review bar), ShowAgentTimelineModal (chat history button), ShowBestOfNModal (Arena).
    /// </summary>
    public partial class MainWindow
    {
        private AgentTrustCoordinator agentTrust;
        private UiPost agentTrustUi;
        private bool agentTrustClosed;
        private Button verifyModeButton;
        private Button bestOfNButton;
        private Button csAcceptVerifiedBtn;
        private AgentTurnCheckpointStore subscribedStore;
        private readonly HashSet<string> sessionTurnIds = new HashSet<string>(StringComparer.Ordinal);
        private bool acceptingInline;
        private bool suppressDiffDecision;

        /// <summary>Test hook: answers verification approval prompts without UI.</summary>
        public Func<VerificationApprovalRequest, VerificationApprovalDecision> AgentVerifyApprovalOverride { get; set; }
        /// <summary>Test hook: answers the undo / redo confirmation ("cancel", "skip" = keep conflicting files, "overwrite").</summary>
        public Func<TurnRestorePlan, string> AgentRestoreConfirmOverride { get; set; }

        public AgentTrustCoordinator AgentTrust => agentTrust;

        private void InitAgentTrust()
        {
            agentTrustUi = UiPost.CaptureCurrent();
            agentTrust = new AgentTrustCoordinator(settings) { RequestApproval = RequestVerifyApproval };
            Closed += (s, e) => { agentTrustClosed = true; agentTrust.CancelRun(); SubscribeStore(null); };
            AgentFeed.CustomCardBuilder = BuildAgentTrustCard;
            AgentFeed.RestoreCheckpointRequested += (item, index) => _ = RestoreToUserMessageAsync(item);
            PrimaryEditor.DiffAllDecisionRequested += OnEditorDiffAllDecision;
            AddAgentTrustComposerControls();
            AddAgentTrustBannerButton();
            // Verification copies left behind by a crash are removed at the next start.
            Task.Run(() => { try { VerificationOverlay.CleanupStale(TimeSpan.FromHours(12)); } catch { } });
        }

        private AgentTurnCheckpointStore CurrentTurnStore()
        {
            var store = agentTrust?.GetStore(workspaceRoot);
            SubscribeStore(store);
            return store;
        }

        private void SubscribeStore(AgentTurnCheckpointStore store)
        {
            if (ReferenceEquals(store, subscribedStore)) return;
            if (subscribedStore != null) subscribedStore.Changed -= OnTurnStoreChanged;
            subscribedStore = agentTrustClosed ? null : store;
            if (subscribedStore != null) subscribedStore.Changed += OnTurnStoreChanged;
        }

        private void OnTurnStoreChanged()
        {
            agentTrustUi?.Post(() => { if (!agentTrustClosed) SyncTurnCheckpointCards(); });
        }

        // ------------------------------------------------------------------ composer + banner controls

        private void AddAgentTrustComposerControls()
        {
            if (!agentModeButtons.TryGetValue("agent", out var anyMode) || anyMode.Parent is not StackPanel modeRow) return;
            verifyModeButton = new Button { FontSize = UiTokens.FontSmall, Padding = new Thickness(8, 2), MinHeight = 22, BorderThickness = new Thickness(0) };
            verifyModeButton.Classes.Add("flat");
            verifyModeButton.Name = "AgentVerifyModeButton";
            verifyModeButton.Click += (s, e) => ShowVerifyModeMenu(verifyModeButton);
            bestOfNButton = new Button { FontSize = UiTokens.FontSmall, Padding = new Thickness(8, 2), MinHeight = 22, BorderThickness = new Thickness(0) };
            bestOfNButton.Classes.Add("flat");
            bestOfNButton.Name = "AgentBestOfNButton";
            bestOfNButton.Click += (s, e) => RunBestOfNFromComposer();
            // Own row under the mode pills: on one line they overflowed (and clipped) the dock at its default width.
            var trustRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, Name = "AgentTrustComposerRow" };
            trustRow.Children.Add(verifyModeButton);
            trustRow.Children.Add(bestOfNButton);
            if (modeRow.Parent is Panel composerPanel)
            {
                int idx = composerPanel.Children.IndexOf(modeRow);
                composerPanel.Children.Insert(idx + 1, trustRow);
            }
            else
            {
                modeRow.Children.Add(trustRow);
            }
            UpdateAgentTrustComposer();
        }

        private void UpdateAgentTrustComposer()
        {
            if (verifyModeButton == null || currentTheme == null) return;
            var mode = settings.EffectiveAgentVerifyMode;
            var label = new IconLabel(mode == AgentVerifyMode.Off ? "check" : "check", mode == AgentVerifyMode.Off ? "Verify: Off" : (mode == AgentVerifyMode.Verify ? "Verify" : "Verify + Repair"), 12, UiTokens.FontSmall);
            label.Icon.FollowForeground = mode == AgentVerifyMode.Off;
            if (mode != AgentVerifyMode.Off) { label.Icon.IconColor = WorkbenchStyles.C(AgentTrustColors.Passed(currentTheme)); }
            verifyModeButton.Content = label;
            verifyModeButton.Foreground = WorkbenchStyles.B(mode == AgentVerifyMode.Off ? currentTheme.FgSecondary : currentTheme.FgPrimary);
            verifyModeButton.Background = Brushes.Transparent;
            ToolTip.SetTip(verifyModeButton, "After the agent proposes edits, build/test them in a temporary copy before you accept (your files stay untouched).");
            int n = settings.EffectiveAgentBestOfNCount;
            var bl = new IconLabel("arena", "Best of " + n, 12, UiTokens.FontSmall);
            bestOfNButton.Content = bl;
            bestOfNButton.Foreground = WorkbenchStyles.B(currentTheme.FgSecondary);
            bestOfNButton.Background = Brushes.Transparent;
            ToolTip.SetTip(bestOfNButton, "Run this request on " + n + " providers/models side by side, verify each, then adopt one.");
        }

        private void ShowVerifyModeMenu(Control anchor)
        {
            var menu = new ContextMenu();
            var items = new List<object>();
            foreach (var m in new[] { AgentVerifyMode.Off, AgentVerifyMode.Verify, AgentVerifyMode.VerifyAndRepair })
            {
                var mode = m;
                var mi = new MenuItem
                {
                    Header = IdePersistentSettings.AgentVerifyModeLabel(mode).Replace("Verify: Off", "Off") + (mode == AgentVerifyMode.VerifyAndRepair ? " (up to " + settings.EffectiveAgentRepairMaxAttempts + " attempts)" : ""),
                    ToggleType = MenuItemToggleType.Radio,
                    IsChecked = settings.EffectiveAgentVerifyMode == mode
                };
                mi.Click += (s, e) => SetAgentVerifyMode(mode);
                items.Add(mi);
            }
            items.Add(new Separator());
            var cmd = new MenuItem { Header = "Verification Command…" };
            cmd.Click += (s, e) => ShowVerificationSettingsModal();
            items.Add(cmd);
            var tl = new MenuItem { Header = "Agent Timeline (Undo / Redo)…" };
            tl.Click += (s, e) => ShowAgentTimelineModal();
            items.Add(tl);
            menu.ItemsSource = items;
            menu.Open(anchor);
        }

        public void SetAgentVerifyMode(AgentVerifyMode mode)
        {
            settings.SetAgentVerifyMode(mode);
            try { settings.Save(); } catch { }
            UpdateAgentTrustComposer();
            SetStatusMessage("Agent verification: " + IdePersistentSettings.AgentVerifyModeLabel(mode));
        }

        private void AddAgentTrustBannerButton()
        {
            if (multiFileChangesetBar?.Child is not Grid csGrid) return;
            csGrid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));
            csAcceptVerifiedBtn = new Button
            {
                Content = "Accept if Verified",
                FontSize = UiTokens.FontSmall,
                Padding = new Thickness(8, 2),
                MinHeight = 22,
                Margin = new Thickness(4, 0, 0, 0),
                IsVisible = false
            };
            csAcceptVerifiedBtn.Name = "AcceptIfVerifiedBtn";
            csAcceptVerifiedBtn.Click += (s, e) => { var cs = agentTrust.ActiveChangeSet; if (cs != null) AcceptAgentChangeSet(cs, onlyIfVerified: true); };
            Grid.SetColumn(csAcceptVerifiedBtn, csGrid.ColumnDefinitions.Count - 1);
            csGrid.Children.Add(csAcceptVerifiedBtn);
        }

        /// <summary>Review bar text for the active change set: pending files + latest verification badge.</summary>
        private void RefreshAgentTrustBanner()
        {
            if (agentTrust == null || multiFileChangesetBar == null || changesetSummaryText == null) return;
            var cs = agentTrust.ActiveChangeSet;
            if (csAcceptVerifiedBtn != null) csAcceptVerifiedBtn.IsVisible = false;
            if (cs == null) return;
            int inlineOnly = openTabs.Count(t => t.HasPendingDiff && cs.Find(t.FilePath) == null);
            int pending = cs.PendingCount + inlineOnly;
            if (pending == 0) return;
            string text = pending == 1 ? "1 file pending review" : pending + " files pending review";
            var v = cs.LatestVerification;
            if (v != null) text += " · " + v.Badge;
            else if (agentTrust.IsRunning) text += " · verifying…";
            changesetSummaryText.Text = text;
            ToolTip.SetTip(changesetSummaryText, v != null ? v.Summary : text);
            multiFileChangesetBar.IsVisible = true;
            if (csAcceptVerifiedBtn != null && currentTheme != null)
            {
                csAcceptVerifiedBtn.IsVisible = v != null;
                csAcceptVerifiedBtn.IsEnabled = cs.IsVerifiedPassing;
                WorkbenchStyles.StyleSecondaryButton(csAcceptVerifiedBtn, currentTheme);
                ToolTip.SetTip(csAcceptVerifiedBtn, cs.IsVerifiedPassing ? "Accept every pending file (verification passed for exactly these changes)." : "Available when the latest verification passed for the current proposals.");
            }
        }

        private void OnChangesetAcceptAll()
        {
            var cs = agentTrust?.ActiveChangeSet;
            if (cs != null) AcceptAgentChangeSet(cs, onlyIfVerified: false);
            if (PrimaryEditor.CurrentBuffer != null && PrimaryEditor.CurrentBuffer.HasPendingDiff) PrimaryEditor.AcceptAllPendingDiffs();
            RefreshChangesetBanner();
        }

        private void OnChangesetRejectAll()
        {
            var cs = agentTrust?.ActiveChangeSet;
            if (cs != null) RejectAgentChangeSetFiles(cs, cs.Files.Where(f => f.IsPending).ToList());
            if (PrimaryEditor.CurrentBuffer != null && PrimaryEditor.CurrentBuffer.HasPendingDiff) PrimaryEditor.RejectAllPendingDiffs();
            RefreshChangesetBanner();
        }

        private List<PaletteEntryItem> BuildAgentTrustPaletteCommands()
        {
            return new List<PaletteEntryItem>
            {
                new PaletteEntryItem { Category = "Agent", Title = "Verification: Off", Execute = () => SetAgentVerifyMode(AgentVerifyMode.Off) },
                new PaletteEntryItem { Category = "Agent", Title = "Verification: Verify Proposed Changes", Execute = () => SetAgentVerifyMode(AgentVerifyMode.Verify) },
                new PaletteEntryItem { Category = "Agent", Title = "Verification: Verify + Auto-Repair", Execute = () => SetAgentVerifyMode(AgentVerifyMode.VerifyAndRepair) },
                new PaletteEntryItem { Category = "Agent", Title = "Verification Command...", Execute = ShowVerificationSettingsModal },
                new PaletteEntryItem { Category = "Agent", Title = "Verify Pending Changes", Execute = () => { var cs = agentTrust.ActiveChangeSet; if (cs != null) _ = ReverifyAgentChangeSetAsync(cs, false); else SetStatusMessage("No pending agent changes."); } },
                new PaletteEntryItem { Category = "Agent", Title = "Timeline (Undo / Redo Turns)...", Execute = ShowAgentTimelineModal },
                new PaletteEntryItem { Category = "Agent", Title = "Redo Last Undone Turn", Execute = () => _ = RedoAgentTurnAsync() },
                new PaletteEntryItem { Category = "Agent", Title = "Best-of-N Arena...", Execute = ShowBestOfNModal },
            };
        }

        // ------------------------------------------------------------------ approval prompt

        /// <summary>Called on a worker thread; blocks until the user answers (10 minutes => deny).</summary>
        private VerificationApprovalDecision RequestVerifyApproval(VerificationApprovalRequest req)
        {
            if (AgentVerifyApprovalOverride != null) return AgentVerifyApprovalOverride(req);
            if (!AvaloniaTerminalEmulatorControl.IsDesktopRuntime) return VerificationApprovalDecision.Deny; // never unattended
            if (Dispatcher.UIThread.CheckAccess()) return VerificationApprovalDecision.Deny;
            var tcs = new TaskCompletionSource<VerificationApprovalDecision>(TaskCreationOptions.RunContinuationsAsynchronously);
            MainUi.Post(() =>
            {
                var stack = new StackPanel { Spacing = 10 };
                stack.Children.Add(new TextBlock { Text = req.Reason ?? "", TextWrapping = TextWrapping.Wrap, FontSize = UiTokens.FontUi, Foreground = WorkbenchStyles.B(currentTheme.FgPrimary) });
                stack.Children.Add(new TextBox { Text = req.CommandLine ?? "", IsReadOnly = true, TextWrapping = TextWrapping.Wrap, FontFamily = new FontFamily("Menlo, Consolas, DejaVu Sans Mono, monospace"), MaxHeight = 160 });
                stack.Children.Add(new TextBlock { Text = "Source: " + req.Source, FontSize = UiTokens.FontSmall, Foreground = WorkbenchStyles.B(currentTheme.FgSecondary) });
                if (req.BuildControlFiles.Count > 0)
                    stack.Children.Add(new TextBlock { Text = "Build files edited by the AI: " + string.Join(", ", req.BuildControlFiles), TextWrapping = TextWrapping.Wrap, FontSize = UiTokens.FontSmall, Foreground = WorkbenchStyles.B(AgentTrustColors.Warning(currentTheme)) });
                var row = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 8 };
                var deny = new Button { Content = "Don't Run", Padding = new Thickness(14, 4) };
                var once = new Button { Content = "Run Once", Padding = new Thickness(14, 4) };
                WorkbenchStyles.StyleSecondaryButton(deny, currentTheme);
                WorkbenchStyles.StylePrimaryButton(once, currentTheme);
                deny.Click += (s, e) => { tcs.TrySetResult(VerificationApprovalDecision.Deny); ModalHost.CloseModal(); };
                once.Click += (s, e) => { tcs.TrySetResult(VerificationApprovalDecision.AllowOnce); ModalHost.CloseModal(); };
                row.Children.Add(deny);
                if (req.CanAlwaysAllow)
                {
                    var always = new Button { Content = "Always Allow in This Workspace", Padding = new Thickness(14, 4) };
                    WorkbenchStyles.StyleSecondaryButton(always, currentTheme);
                    always.Click += (s, e) => { tcs.TrySetResult(VerificationApprovalDecision.AllowAlwaysForWorkspace); ModalHost.CloseModal(); };
                    row.Children.Add(always);
                }
                row.Children.Add(once);
                stack.Children.Add(row);
                Action onClosed = null;
                onClosed = () => { ModalHost.Closed -= onClosed; tcs.TrySetResult(VerificationApprovalDecision.Deny); };
                ModalHost.Closed += onClosed;
                ModalHost.ShowModal("agent-verify-approval", "Run Verification Command?", stack, 580);
                MainUi.Post(() => deny.Focus(), DispatcherPriority.Loaded);
            });
            return tcs.Task.Wait(TimeSpan.FromMinutes(10)) ? tcs.Task.Result : VerificationApprovalDecision.Deny;
        }

        // ------------------------------------------------------------------ turn presentation

        /// <summary>Snapshot of open-buffer texts (thread-safe base-text provider for change sets).</summary>
        private Func<string, string> SnapshotBufferTexts()
        {
            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var t in openTabs)
            {
                if (string.IsNullOrEmpty(t.FilePath) || t.IsLargeFileViewer) continue;
                try { map[Path.GetFullPath(t.FilePath)] = t.HasPendingDiff ? null : (t.Content ?? ""); } catch { }
            }
            return p =>
            {
                try { return map.TryGetValue(Path.GetFullPath(p), out var s) ? s : null; } catch { return null; }
            };
        }

        private AgentTurnRequest BuildAgentTurnRequest(string prompt, string mode, ByokVendor vendor, string harnessId, EditorTabBuffer tab,
            string ws, List<string> files, List<string> pills, string term)
        {
            return new AgentTurnRequest
            {
                // A detached copy of the editor: repair / Best-of-N turns run on worker threads.
                Engine = aiEngine, Mode = mode, Prompt = prompt, Vendor = vendor, HarnessId = harnessId, ActiveTab = AgentTurnInputs.SnapshotTab(tab),
                WorkspaceRoot = ws, WorkspaceFiles = files ?? new List<string>(), Pills = pills ?? new List<string>(), TerminalText = term ?? "",
                BaseTextProvider = SnapshotBufferTexts()
            };
        }

        private static string ShortLabel(string s, int max = 60)
        {
            s = (s ?? "").Replace('\n', ' ').Trim();
            return s.Length > max ? s.Substring(0, max - 1) + "…" : s;
        }

        /// <summary>
        /// After an agent turn: builds the change set, stages the active file inline (existing behaviour), adds one
        /// review card per file, a checkpoint card when terminal commands changed files, then verifies (and repairs)
        /// when the verification gate is on.
        /// </summary>
        private async Task PresentAgentTurnAsync(AgentExecutionTurnResult turn, AgentTurnRequest req, string turnId, List<TurnFileChange> terminalChanges)
        {
            if (!string.IsNullOrEmpty(turnId)) sessionTurnIds.Add(turnId);
            if (terminalChanges != null && terminalChanges.Count > 0)
            {
                CurrentTurnStore();
                SyncTurnCheckpointCards();
                RefreshExplorerTree();
                foreach (var c in terminalChanges) ReloadOpenTabIfChangedOnDisk(Path.Combine(workspaceRoot ?? "", c.Path.Replace('/', Path.DirectorySeparatorChar)));
            }
            var staged = turn?.StagedFileContents;
            if (staged == null || staged.Count == 0)
            {
                AgentFeed.AddMessage(new AvaloniaAgentFeedItem { Role = "system", Content = $"Completed in {turn?.ElapsedMs ?? 0} ms" });
                return;
            }
            var cs = agentTrust.CreateChangeSet(staged, req.WorkspaceRoot, req.BaseTextProvider, ShortLabel(req.Prompt), turnId);
            cs.ProviderLabel = turn.ProviderUsed;
            PresentChangeSet(cs, stageActiveInline: true);
            var mode = settings.EffectiveAgentVerifyMode;
            if (mode != AgentVerifyMode.Off && cs.Files.Count > 0)
                await RunAgentVerificationAsync(cs, req, mode);
        }

        private void PresentChangeSet(AgentChangeSet cs, bool stageActiveInline)
        {
            CurrentTurnStore();
            if (!string.IsNullOrEmpty(cs.TurnId)) sessionTurnIds.Add(cs.TurnId);
            foreach (var f in cs.Files)
            {
                // A newer proposal for the same file supersedes older pending ones.
                foreach (var older in agentTrust.ChangeSets.Where(c => !ReferenceEquals(c, cs)).ToList())
                {
                    var stale = older.Find(f.FullPath);
                    if (stale != null && stale.IsPending) { stale.Status = "Rejected"; SyncDiffCardStatuses(older); }
                }
                if (stageActiveInline && activeTab != null && !string.IsNullOrEmpty(activeTab.FilePath) && SamePath(activeTab.FilePath, f.FullPath))
                    RestageInline(f.ProposedText);
                AgentFeed.AddMessage(new AvaloniaAgentFeedItem
                {
                    Role = "diff_review",
                    ChangeSet = cs,
                    ChangeFile = f,
                    DiffFilePath = f.FullPath,
                    DiffAddedLines = f.Added,
                    DiffRemovedLines = f.Removed,
                    DiffStatus = f.Status,
                    DiffPreviewText = BuildUnifiedPreview(f, 60)
                });
            }
            RefreshChangesetBanner();
        }

        /// <summary>Replaces the active editor's inline proposal without it counting as the user's Reject.</summary>
        private void RestageInline(string proposedText)
        {
            suppressDiffDecision = true;
            try
            {
                if (PrimaryEditor.CurrentBuffer != null && PrimaryEditor.CurrentBuffer.HasPendingDiff) PrimaryEditor.RejectAllPendingDiffs();
            }
            finally { suppressDiffDecision = false; }
            PrimaryEditor.StageProposedDiffContent(proposedText);
        }

        private static bool SamePath(string a, string b)
        {
            if (string.IsNullOrEmpty(a) || string.IsNullOrEmpty(b)) return false;
            try { return string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase); } catch { return false; }
        }

        /// <summary>Unified-diff style preview ("@@", "-", "+", context) of a proposal, capped at <paramref name="maxLines"/>.</summary>
        public static string BuildUnifiedPreview(AgentChangeSetFile f, int maxLines)
        {
            var sb = new StringBuilder();
            int lines = 0;
            string[] newLines = AgentChangeSet.Normalize(f.ProposedText).Split('\n');
            foreach (var h in LlmHarnessAndAgentEngine.ComputeLineDiffHunks(f.BaseText, AgentChangeSet.Normalize(f.ProposedText)))
            {
                if (lines >= maxLines) break;
                sb.Append("@@ +").Append(h.StartLine + 1).Append(" @@\n");
                lines++;
                if (h.StartLine > 0 && h.StartLine - 1 < newLines.Length) { sb.Append(' ').Append(newLines[h.StartLine - 1]).Append('\n'); lines++; }
                foreach (var r in h.RemovedLines) { if (lines++ >= maxLines) break; sb.Append('-').Append(r).Append('\n'); }
                foreach (var a in h.AddedLines) { if (lines++ >= maxLines) break; sb.Append('+').Append(a).Append('\n'); }
            }
            if (lines >= maxLines) sb.Append("… (open the file to review the full diff)\n");
            return sb.ToString().TrimEnd('\n');
        }

        private void RefreshDiffCards(AgentChangeSet cs)
        {
            foreach (var f in cs.Files)
            {
                var item = AgentFeed.Items.FirstOrDefault(i => i.Role == "diff_review" && ReferenceEquals(i.ChangeFile, f));
                if (item == null)
                {
                    AgentFeed.Items.Add(new AvaloniaAgentFeedItem { Role = "diff_review", ChangeSet = cs, ChangeFile = f, DiffFilePath = f.FullPath });
                    item = AgentFeed.Items[AgentFeed.Items.Count - 1];
                }
                item.DiffAddedLines = f.Added;
                item.DiffRemovedLines = f.Removed;
                item.DiffStatus = f.Status;
                item.DiffPreviewText = BuildUnifiedPreview(f, 60);
            }
            AgentFeed.RebuildVisualFeed();
        }

        // ------------------------------------------------------------------ verification

        /// <summary>Verifies (and in Verify+Repair mode repairs) a change set; progress and the result show in a chat card.</summary>
        public async Task<AgentVerifiedTurnOutcome> RunAgentVerificationAsync(AgentChangeSet cs, AgentTurnRequest req, AgentVerifyMode mode)
        {
            var item = new AvaloniaAgentFeedItem { Role = "verification", ChangeSet = cs, IsRunning = true, ProgressText = "Preparing a temporary copy of the workspace…" };
            AgentFeed.AddMessage(item);
            var token = agentTrust.BeginRun();
            var ui = agentTrustUi;
            RefreshChangesetBanner();
            // Live progress only drives the "Verifying…" line; the final transcript is built from the outcome.
            Action<AgentVerifiedTurnEvent> progress = e => ui.Post(() =>
            {
                if (agentTrustClosed || !item.IsRunning || e.Kind == "stopped") return;
                item.ProgressText = e.Message;
                AgentFeed.RebuildVisualFeed();
            });
            var t = agentTrust.VerifyAndRepairAsync(cs, req, mode, progress, token); // snapshots proposals here, then runs on the pool
            AgentVerifiedTurnOutcome outcome = null;
            try
            {
                outcome = AvaloniaTerminalEmulatorControl.IsDesktopRuntime ? await t : t.GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                item.ProgressLog.Add("Verification failed: " + ex.Message);
            }
            finally
            {
                agentTrust.EndRun(token);
            }
            AgentTrustCoordinator.ApplyOutcome(cs, outcome, req?.BaseTextProvider);
            item.IsRunning = false;
            item.Verification = cs.LatestVerification;
            if (outcome != null && (outcome.RepairAttempts > 0 || outcome.StopReason == "model-error" || outcome.StopReason == "no-changes"))
            {
                for (int k = 1; k <= outcome.RepairAttempts || (k == 1 && outcome.RepairAttempts == 0); k++)
                {
                    var before = outcome.Verifications.Count >= k ? outcome.Verifications[k - 1] : null;
                    var after = outcome.Verifications.Count > k ? outcome.Verifications[k] : null;
                    string sent = before == null ? "the failure" : before.Errors.Count > 0 ? before.Errors.Count + " error(s)" : "the failure output";
                    item.ProgressLog.Add($"Repair attempt {k}: sent {sent} to the model → " + (after != null ? after.Badge : DescribeStop(outcome.StopReason)));
                }
                if (outcome.RepairAttempts > 0)
                    item.ProgressLog.Add(outcome.StopReason == "passed"
                        ? $"Auto-repair fixed it in {outcome.RepairAttempts} attempt(s)."
                        : $"Auto-repair stopped after {outcome.RepairAttempts} attempt(s) ({DescribeStop(outcome.StopReason)}).");
                if (outcome.RepairAttempts > 0) ApplyRepairedProposals(cs);
            }
            AgentFeed.RebuildVisualFeed();
            AgentFeed.ScrollToBottom();
            RefreshChangesetBanner();
            if (item.Verification != null) SetStatusMessage("Agent changes: " + item.Verification.Summary);
            return outcome;
        }

        private static string DescribeStop(string reason)
        {
            switch (reason)
            {
                case "max-attempts": return "attempt limit reached";
                case "no-changes": return "the model made no further changes";
                case "model-error": return "the model returned no usable edit";
                case "cancelled": return "cancelled";
                case "not-repairable": return "not fixable by editing code";
                default: return reason;
            }
        }

        /// <summary>Repair turns replaced proposals: refresh the diff cards and re-stage the active file inline.</summary>
        private void ApplyRepairedProposals(AgentChangeSet cs)
        {
            foreach (var f in cs.Files.Where(x => x.IsPending))
            {
                var tab = openTabs.FirstOrDefault(t => SamePath(t.FilePath, f.FullPath));
                if (tab != null && tab.HasPendingDiff && ReferenceEquals(PrimaryEditor.CurrentBuffer, tab))
                    RestageInline(f.ProposedText);
            }
            RefreshDiffCards(cs);
        }

        /// <summary>Re-runs verification for the pending proposals of <paramref name="cs"/> (no repair unless requested).</summary>
        public Task<AgentVerifiedTurnOutcome> ReverifyAgentChangeSetAsync(AgentChangeSet cs, bool repair)
        {
            var req = BuildAgentTurnRequest(cs.Label, currentAgentMode, ResolvePreferredVendor(), settings.ActiveHarnessId, activeTab, cs.WorkspaceRoot, workspaceFiles.ToList(), new List<string>(attachedContextPills), "");
            return RunAgentVerificationAsync(cs, req, repair ? AgentVerifyMode.VerifyAndRepair : AgentVerifyMode.Verify);
        }

        // ------------------------------------------------------------------ accept / reject

        public sealed class AgentAcceptSummary
        {
            public readonly List<string> Written = new List<string>();
            public readonly List<string> Conflicts = new List<string>();
            public readonly List<string> Errors = new List<string>();
            public string Refused = "";
        }

        /// <summary>Accepts every pending file of <paramref name="cs"/>; with <paramref name="onlyIfVerified"/> only when verification passed for exactly these proposals.</summary>
        public AgentAcceptSummary AcceptAgentChangeSet(AgentChangeSet cs, bool onlyIfVerified)
        {
            var pending = cs.Files.Where(f => f.IsPending).ToList();
            if (onlyIfVerified)
            {
                string why = null;
                if (!cs.IsVerifiedPassing) why = cs.LatestVerification == null ? "these changes haven't been verified" : "the latest verification didn't pass for these exact changes";
                else
                {
                    foreach (var f in pending)
                    {
                        var tab = openTabs.FirstOrDefault(t => SamePath(t.FilePath, f.FullPath));
                        if (tab != null && tab.HasPendingDiff && AgentChangeSet.Normalize(tab.Content).TrimEnd('\n') != AgentChangeSet.Normalize(f.ProposedText).TrimEnd('\n'))
                        { why = f.RelativePath + " was edited in the editor after verification"; break; }
                    }
                }
                if (why != null)
                {
                    var refused = new AgentAcceptSummary { Refused = "Not accepted: " + why + ". Re-run verification first." };
                    AgentFeed.AddMessage(new AvaloniaAgentFeedItem { Role = "system", Content = refused.Refused });
                    SetStatusMessage(refused.Refused);
                    return refused;
                }
            }
            return AcceptAgentChangeSetFiles(cs, pending, overwriteConflicts: false);
        }

        public AgentAcceptSummary AcceptAgentChangeSetFiles(AgentChangeSet cs, IList<AgentChangeSetFile> files, bool overwriteConflicts)
        {
            var summary = new AgentAcceptSummary();
            foreach (var f in files.Where(x => x.IsPending).ToList())
            {
                var tab = openTabs.FirstOrDefault(t => SamePath(t.FilePath, f.FullPath));
                if (tab != null && tab.HasPendingDiff)
                {
                    // Inline review in the editor: honour per-hunk decisions made there, then save.
                    if (!ReferenceEquals(PrimaryEditor.CurrentBuffer, tab)) ActivateTab(tab);
                    if (ReferenceEquals(PrimaryEditor.CurrentBuffer, tab))
                    {
                        PrimaryEditor.AcceptAllPendingDiffs(); // -> OnEditorDiffAllDecision saves + records
                        if (!f.IsPending) summary.Written.Add(f.FullPath);
                        else summary.Conflicts.Add(f.RelativePath + " changed on disk since the proposal (left unsaved in the editor)");
                        continue;
                    }
                }
                if (tab != null && tab.IsDirty && !overwriteConflicts)
                {
                    summary.Conflicts.Add(f.RelativePath + " has unsaved changes in the editor");
                    continue;
                }
                var res = agentTrust.Accept(cs, new[] { f }, overwriteConflicts);
                summary.Written.AddRange(res.Written);
                summary.Conflicts.AddRange(res.Conflicts.Select(c => c + " changed on disk since the proposal"));
                summary.Errors.AddRange(res.Errors);
                if (res.Notes.Count > 0)
                    AgentFeed.AddMessage(new AvaloniaAgentFeedItem { Role = "system", Content = string.Join(" ", res.Notes) });
                if (res.Written.Count > 0 && tab != null) ReloadOpenTabIfChangedOnDisk(f.FullPath);
            }
            AfterAgentWrites(cs, summary);
            return summary;
        }

        private void AfterAgentWrites(AgentChangeSet cs, AgentAcceptSummary summary)
        {
            if (summary.Written.Count > 0)
            {
                aiEngine?.NotifyChangesApplied(summary.Written);
                aiEngine?.NotifyChangesApplied(summary.Written);
                try { RefreshExplorerTree(); } catch { }
                try { RefreshGitStatusInBackground(); } catch { }
            }
            if (summary.Conflicts.Count > 0 || summary.Errors.Count > 0)
            {
                AgentFeed.AddMessage(new AvaloniaAgentFeedItem
                {
                    Role = "system",
                    Content = "Not applied: " + string.Join("; ", summary.Conflicts.Concat(summary.Errors)) + ". Review those files, then accept again."
                });
            }
            SyncDiffCardStatuses(cs);
            SyncTurnCheckpointCards();
            RefreshChangesetBanner();
            if (summary.Written.Count > 0) SetStatusMessage($"Accepted {summary.Written.Count} file(s) — Undo is available in the chat timeline.");
        }

        public void RejectAgentChangeSetFiles(AgentChangeSet cs, IList<AgentChangeSetFile> files)
        {
            foreach (var f in files.Where(x => x.IsPending).ToList())
            {
                var tab = openTabs.FirstOrDefault(t => SamePath(t.FilePath, f.FullPath));
                if (tab != null && tab.HasPendingDiff)
                {
                    if (!ReferenceEquals(PrimaryEditor.CurrentBuffer, tab)) ActivateTab(tab);
                    if (ReferenceEquals(PrimaryEditor.CurrentBuffer, tab)) PrimaryEditor.RejectAllPendingDiffs();
                }
                f.Status = "Rejected";
            }
            SyncDiffCardStatuses(cs);
            RefreshChangesetBanner();
        }

        private void SyncDiffCardStatuses(AgentChangeSet cs)
        {
            foreach (var item in AgentFeed.Items.Where(i => i.Role == "diff_review" && ReferenceEquals(i.ChangeSet, cs) && i.ChangeFile != null))
                item.DiffStatus = item.ChangeFile.Status;
            AgentFeed.RebuildVisualFeed();
        }

        /// <summary>Per-file card buttons (hooked from HandleAgentDiffDecision).</summary>
        private bool HandleAgentTrustDiffDecision(AvaloniaAgentFeedItem item, bool accepted)
        {
            if (item?.ChangeSet == null || item.ChangeFile == null) return false;
            if (accepted) AcceptAgentChangeSetFiles(item.ChangeSet, new[] { item.ChangeFile }, overwriteConflicts: false);
            else RejectAgentChangeSetFiles(item.ChangeSet, new[] { item.ChangeFile });
            item.DiffStatus = item.ChangeFile.Status;
            return true;
        }

        /// <summary>The editor's inline Accept All / Reject All on a file of a pending change set.</summary>
        private void OnEditorDiffAllDecision(EditorTabBuffer buf, bool accepted)
        {
            if (acceptingInline || suppressDiffDecision || buf == null || agentTrust == null) return;
            AgentChangeSet cs = null;
            AgentChangeSetFile f = null;
            foreach (var c in agentTrust.ChangeSets.ToList())
            {
                var hit = c.Find(buf.FilePath);
                if (hit != null && hit.IsPending) { cs = c; f = hit; }
            }
            if (cs == null) return;
            if (!accepted)
            {
                f.Status = "Rejected";
                SyncDiffCardStatuses(cs);
                RefreshChangesetBanner();
                return;
            }
            string nowHash = File.Exists(f.FullPath) ? AgentHash.FileSha1(f.FullPath) : null;
            if (nowHash != f.DiskHashAtStage)
            {
                AgentFeed.AddMessage(new AvaloniaAgentFeedItem { Role = "system", Content = $"{f.RelativePath} changed on disk since the proposal: the accepted text is in the editor, unsaved. Review it and save (Cmd/Ctrl+S) to keep it." });
                return;
            }
            acceptingInline = true;
            try
            {
                byte[] pre = File.Exists(f.FullPath) ? File.ReadAllBytes(f.FullPath) : null;
                if (SaveBufferToDisk(buf))
                {
                    byte[] post = File.Exists(f.FullPath) ? File.ReadAllBytes(f.FullPath) : null;
                    CurrentTurnStore()?.RecordFileWrite(cs.TurnId, cs.Label, f.FullPath, pre, post);
                    f.Status = "Accepted";
                }
            }
            finally { acceptingInline = false; }
            AfterAgentWrites(cs, new AgentAcceptSummary());
        }

        private sealed class AgentTrackedSave
        {
            public AgentChangeSet ChangeSet;
            public AgentChangeSetFile File;
            public byte[] Pre;
        }

        /// <summary>
        /// Save hook: a manual save of a file with a pending agent proposal (e.g. after per-hunk Accept in the editor)
        /// is recorded in that turn's checkpoint, so Undo covers it too.
        /// </summary>
        private object BeginAgentTrackedSave(EditorTabBuffer tab)
        {
            if (acceptingInline || agentTrust == null || tab == null || string.IsNullOrEmpty(tab.FilePath)) return null;
            foreach (var c in agentTrust.ChangeSets.ToList())
            {
                var f = c.Find(tab.FilePath);
                if (f == null || !f.IsPending) continue;
                try { return new AgentTrackedSave { ChangeSet = c, File = f, Pre = File.Exists(f.FullPath) ? File.ReadAllBytes(f.FullPath) : null }; }
                catch { return null; }
            }
            return null;
        }

        private void EndAgentTrackedSave(object token, EditorTabBuffer tab)
        {
            if (token is not AgentTrackedSave s) return;
            try
            {
                byte[] post = File.Exists(s.File.FullPath) ? File.ReadAllBytes(s.File.FullPath) : null;
                CurrentTurnStore()?.RecordFileWrite(s.ChangeSet.TurnId, s.ChangeSet.Label, s.File.FullPath, s.Pre, post);
                if (!tab.HasPendingDiff) s.File.Status = "Accepted";
                else s.File.DiskHashAtStage = post != null ? AgentHash.Sha1Hex(post) : null; // saved mid-review: not a conflict later
                SyncDiffCardStatuses(s.ChangeSet);
                SyncTurnCheckpointCards();
            }
            catch { }
        }

        // ------------------------------------------------------------------ per-turn checkpoints

        private void SyncTurnCheckpointCards()
        {
            var store = CurrentTurnStore();
            if (store == null) return;
            bool changed = false;
            foreach (var t in store.Turns)
            {
                if (!sessionTurnIds.Contains(t.Id)) continue;
                if (!AgentFeed.Items.Any(i => i.Role == "turn_checkpoint" && i.TurnId == t.Id))
                {
                    AgentFeed.Items.Add(new AvaloniaAgentFeedItem { Role = "turn_checkpoint", TurnId = t.Id });
                    changed = true;
                }
            }
            if (changed || AgentFeed.Items.Any(i => i.Role == "turn_checkpoint")) { AgentFeed.RebuildVisualFeed(); }
        }

        private List<string> DirtyBufferPaths()
        {
            return openTabs.Where(t => t.IsDirty && !string.IsNullOrEmpty(t.FilePath)).Select(t => t.FilePath).ToList();
        }

        /// <summary>Undo this turn (or back to here): confirmation lists the files and conflicts; never overwrites unsaved edits silently.</summary>
        public async Task<TurnRestoreResult> UndoAgentTurnAsync(string turnId, bool backToHere)
        {
            var store = CurrentTurnStore();
            var plan = store?.PlanUndo(turnId, backToHere, DirtyBufferPaths());
            if (plan == null) { SetStatusMessage("That turn was already undone or is no longer in the timeline."); return null; }
            return await ExecuteRestorePlanAsync(store, plan, backToHere ? "Undo Back to Here" : "Undo This Turn");
        }

        /// <summary>"Restore" on a user message (previously not wired on macOS/Linux): undo that turn and every later one.</summary>
        public async Task<TurnRestoreResult> RestoreToUserMessageAsync(AvaloniaAgentFeedItem userItem)
        {
            var store = CurrentTurnStore();
            string id = userItem?.TurnId;
            if (string.IsNullOrEmpty(id) || store?.FindTurn(id) == null)
            {
                SetStatusMessage("This message's turn didn't change any files (or nothing was accepted), so there is nothing to restore.");
                return null;
            }
            return await UndoAgentTurnAsync(id, true);
        }

        public async Task<TurnRestoreResult> RedoAgentTurnAsync()
        {
            var store = CurrentTurnStore();
            var plan = store?.PlanRedo(DirtyBufferPaths());
            if (plan == null) { SetStatusMessage("Nothing to redo."); return null; }
            return await ExecuteRestorePlanAsync(store, plan, "Redo");
        }

        private async Task<TurnRestoreResult> ExecuteRestorePlanAsync(AgentTurnCheckpointStore store, TurnRestorePlan plan, string title)
        {
            string decision = await ConfirmRestorePlanAsync(plan, title);
            if (decision == "cancel") return null;
            var result = store.Execute(plan, decision == "overwrite");
            foreach (var rel in result.Restored)
                ReloadOpenTabIfChangedOnDisk(Path.Combine(store.WorkspaceRoot, rel.Replace('/', Path.DirectorySeparatorChar)));
            try { RefreshExplorerTree(); } catch { }
            try { RefreshGitStatusInBackground(); } catch { }
            var msg = new StringBuilder();
            msg.Append(title).Append(": restored ").Append(result.Restored.Count).Append(" file(s)");
            if (result.Skipped.Count > 0) msg.Append(", kept ").Append(result.Skipped.Count).Append(" changed file(s) as they are (").Append(string.Join(", ", result.Skipped.Take(5))).Append(')');
            if (result.Errors.Count > 0) msg.Append(", errors: ").Append(string.Join("; ", result.Errors));
            if (result.Backups.Count > 0) msg.Append(". Your overwritten versions were backed up (Redo restores them).");
            AgentFeed.AddMessage(new AvaloniaAgentFeedItem { Role = "system", Content = msg.ToString() });
            SyncTurnCheckpointCards();
            SetStatusMessage(msg.ToString());
            return result;
        }

        private Task<string> ConfirmRestorePlanAsync(TurnRestorePlan plan, string title)
        {
            if (AgentRestoreConfirmOverride != null) return Task.FromResult(AgentRestoreConfirmOverride(plan));
            var actionable = plan.Actionable.ToList();
            if (actionable.Count == 0) return Task.FromResult("skip");
            if (!AvaloniaTerminalEmulatorControl.IsDesktopRuntime) return Task.FromResult("cancel");
            var tcs = new TaskCompletionSource<string>();
            var stack = new StackPanel { Spacing = 8 };
            stack.Children.Add(new TextBlock
            {
                Text = plan.IsRedo ? "Redo restores the files to how they were before the undo:" : "These files go back to how they were before the turn:",
                FontSize = UiTokens.FontUi, TextWrapping = TextWrapping.Wrap, Foreground = WorkbenchStyles.B(currentTheme.FgPrimary)
            });
            var list = new StackPanel { Spacing = 2 };
            foreach (var it in actionable.Take(40))
            {
                string verb = it.Unrestorable ? "can't restore (no backup)" : it.Action == "delete" ? "delete" : it.Action == "recreate" ? "recreate" : "restore";
                var row = new TextBlock
                {
                    Text = verb + "  " + it.Path + (it.Conflict ? "  — " + it.ConflictReason : ""),
                    FontFamily = new FontFamily("Menlo, Consolas, DejaVu Sans Mono, monospace"),
                    FontSize = UiTokens.FontSmall,
                    TextWrapping = TextWrapping.Wrap,
                    Foreground = WorkbenchStyles.B(it.Conflict || it.Unrestorable ? AgentTrustColors.Warning(currentTheme) : currentTheme.FgPrimary)
                };
                list.Children.Add(row);
            }
            if (actionable.Count > 40) list.Children.Add(new TextBlock { Text = "… and " + (actionable.Count - 40) + " more", FontSize = UiTokens.FontSmall, Foreground = WorkbenchStyles.B(currentTheme.FgSecondary) });
            stack.Children.Add(new ScrollViewer { Content = list, MaxHeight = 260 });
            if (plan.HasConflicts)
                stack.Children.Add(new TextBlock
                {
                    Text = "Files marked above changed after the turn (your edits or a later turn). They are kept as they are unless you choose Overwrite; overwritten versions are backed up.",
                    TextWrapping = TextWrapping.Wrap, FontSize = UiTokens.FontSmall, Foreground = WorkbenchStyles.B(currentTheme.FgSecondary)
                });
            var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 8 };
            var cancel = new Button { Content = "Cancel", Padding = new Thickness(14, 4) };
            WorkbenchStyles.StyleSecondaryButton(cancel, currentTheme);
            cancel.Click += (s, e) => { tcs.TrySetResult("cancel"); ModalHost.CloseModal(); };
            buttons.Children.Add(cancel);
            if (plan.HasConflicts)
            {
                var over = new Button { Content = "Overwrite My Changes", Padding = new Thickness(14, 4) };
                WorkbenchStyles.StyleSecondaryButton(over, currentTheme);
                over.Click += (s, e) => { tcs.TrySetResult("overwrite"); ModalHost.CloseModal(); };
                buttons.Children.Add(over);
            }
            var ok = new Button { Content = plan.HasConflicts ? "Restore Unchanged Files" : title, Padding = new Thickness(14, 4) };
            WorkbenchStyles.StylePrimaryButton(ok, currentTheme);
            ok.Click += (s, e) => { tcs.TrySetResult("skip"); ModalHost.CloseModal(); };
            buttons.Children.Add(ok);
            stack.Children.Add(buttons);
            Action onClosed = null;
            onClosed = () => { ModalHost.Closed -= onClosed; tcs.TrySetResult("cancel"); };
            ModalHost.Closed += onClosed;
            ModalHost.ShowModal("agent-restore", title + "?", stack, 600);
            return tcs.Task;
        }

        public void ShowAgentTimelineModal()
        {
            var store = CurrentTurnStore();
            var stack = new StackPanel { Spacing = 8 };
            if (store == null)
            {
                stack.Children.Add(new TextBlock { Text = "Open a folder to record agent checkpoints.", Foreground = WorkbenchStyles.B(currentTheme.FgSecondary) });
                ModalHost.ShowModal("agent-timeline", "Agent Timeline", stack, 600);
                return;
            }
            var turns = store.Turns;
            stack.Children.Add(new TextBlock
            {
                Text = turns.Count == 0 ? "No agent turns have changed files in this workspace yet." : "Every agent turn that changed files (accepted edits and its terminal commands). Stored on disk, so it survives restarts.",
                TextWrapping = TextWrapping.Wrap, FontSize = UiTokens.FontSmall, Foreground = WorkbenchStyles.B(currentTheme.FgSecondary)
            });
            if (store.CanRedo)
            {
                var redo = new Button { Content = "Redo Last Undo", Padding = new Thickness(10, 3), HorizontalAlignment = HorizontalAlignment.Left };
                WorkbenchStyles.StyleSecondaryButton(redo, currentTheme);
                redo.Click += async (s, e) => { ModalHost.CloseModal(); await RedoAgentTurnAsync(); };
                stack.Children.Add(redo);
            }
            var list = new StackPanel { Spacing = 6 };
            foreach (var t in Enumerable.Reverse(turns))
                list.Children.Add(BuildTurnCheckpointBody(store, t, closeModalFirst: true));
            stack.Children.Add(new ScrollViewer { Content = list, MaxHeight = 460 });
            ModalHost.ShowModal("agent-timeline", "Agent Timeline", stack, 640);
        }

        // ------------------------------------------------------------------ Best-of-N

        private void RunBestOfNFromComposer()
        {
            string prompt = (chatPromptInput?.Text ?? "").Trim();
            if (string.IsNullOrEmpty(prompt))
            {
                SetStatusMessage("Type a request in the chat box, then click Best of " + settings.EffectiveAgentBestOfNCount + ".");
                chatPromptInput?.Focus();
                return;
            }
            chatPromptInput.Text = "";
            chatProgressBar.StartProgress();
            _ = RunBestOfNWithProgressAsync(prompt);
        }

        private async Task RunBestOfNWithProgressAsync(string prompt)
        {
            try { await RunBestOfNAsync(prompt); }
            finally { chatProgressBar.StopProgress(); }
        }

        public void ShowBestOfNModal()
        {
            var stack = new StackPanel { Spacing = 10 };
            stack.Children.Add(new TextBlock
            {
                Text = "Runs the same request on " + settings.EffectiveAgentBestOfNCount + " providers/models at once, verifies every candidate in a temporary copy, and lets you adopt one. Nothing is written until you accept." +
                       (NetworkLedger.OfflineMode ? " Offline Mode: only local models are used." : ""),
                TextWrapping = TextWrapping.Wrap, FontSize = UiTokens.FontSmall, Foreground = WorkbenchStyles.B(currentTheme.FgSecondary)
            });
            var box = new TextBox { Watermark = "Describe the change…", AcceptsReturn = true, Height = 80, TextWrapping = TextWrapping.Wrap, Text = chatPromptInput?.Text ?? "" };
            stack.Children.Add(box);
            var row = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 8 };
            var go = new Button { Content = "Run Best of " + settings.EffectiveAgentBestOfNCount, Padding = new Thickness(14, 4) };
            WorkbenchStyles.StylePrimaryButton(go, currentTheme);
            go.Click += (s, e) =>
            {
                string p = (box.Text ?? "").Trim();
                if (p.Length == 0) return;
                ModalHost.CloseModal();
                if (!rightChatDockBorder.IsVisible) ToggleRightChatDock();
                if (chatPromptInput != null) chatPromptInput.Text = "";
                chatProgressBar.StartProgress();
                _ = RunBestOfNWithProgressAsync(p);
            };
            row.Children.Add(go);
            stack.Children.Add(row);
            ModalHost.ShowModal("arena", "Best-of-N Arena", stack, 600);
        }

        /// <summary>Runs <paramref name="prompt"/> on N candidates; the comparison card updates live.</summary>
        public async Task<BestOfNRun> RunBestOfNAsync(string prompt)
        {
            AgentFeed.AddMessage(new AvaloniaAgentFeedItem { Role = "user", Content = prompt, Timestamp = DateTime.Now });
            var pills = new List<string>(attachedContextPills);
            if (!string.IsNullOrEmpty(activeTab?.FileName) && !pills.Contains(activeTab.FileName)) pills.Add(activeTab.FileName);
            string mode = currentAgentMode == "ask" ? "composer" : currentAgentMode;
            var req = BuildAgentTurnRequest(prompt, mode, ResolvePreferredVendor(), settings.ActiveHarnessId, activeTab, workspaceRoot, workspaceFiles.ToList(), pills,
                activeTerminalSession?.Control?.GetVisibleScreenText() ?? "");
            var run = agentTrust.PlanBestOfN(req, settings.EffectiveAgentBestOfNCount);
            if (run.Specs.Count < 2)
            {
                AgentFeed.AddMessage(new AvaloniaAgentFeedItem
                {
                    Role = "error",
                    Content = "Best-of-N needs at least two providers or models. " + run.Note + " Configure another API key or a local model (Ollama / LM Studio) in Settings."
                });
                return run;
            }
            var item = new AvaloniaAgentFeedItem { Role = "bestofn", BestOfN = run, IsRunning = true, Content = prompt };
            AgentFeed.AddMessage(item);
            var token = agentTrust.BeginRun();
            var ui = agentTrustUi;
            Action<BestOfNCandidateResult> onUpdate = r => ui.Post(() =>
            {
                if (agentTrustClosed) return;
                lock (run) { if (!run.Results.Contains(r)) run.Results.Add(r); }
                AgentFeed.RebuildVisualFeed();
            });
            var t = Task.Run(async () =>
            {
                EnsureWorkspaceIndexed();
                return await agentTrust.RunBestOfNAsync(req, run, true, onUpdate, token).ConfigureAwait(false);
            });
            try
            {
                if (AvaloniaTerminalEmulatorControl.IsDesktopRuntime) await t; else t.GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                item.ProgressLog.Add("Best-of-N failed: " + ex.Message);
            }
            finally { agentTrust.EndRun(token); }
            item.IsRunning = false;
            AgentFeed.RebuildVisualFeed();
            AgentFeed.ScrollToBottom();
            var best = run.Results.FirstOrDefault(r => r.Recommended);
            SetStatusMessage(best != null ? "Best-of-N: recommended " + best.Spec.Label + " (" + best.VerificationLabel + ")" : "Best-of-N finished: no candidate passed verification.");
            return run;
        }

        /// <summary>Adopts a candidate: it becomes the reviewable change set (nothing is written).</summary>
        public AgentChangeSet AdoptBestOfNCandidate(AvaloniaAgentFeedItem item, BestOfNCandidateResult candidate)
        {
            var cs = agentTrust.Adopt(candidate);
            if (cs == null) return null;
            if (item != null) item.ProgressText = "Adopted: " + candidate.Spec.Label;
            AgentFeed.AddMessage(new AvaloniaAgentFeedItem { Role = "system", Content = "Adopted " + candidate.Spec.Label + " — review the changes below; nothing is written until you accept." });
            PresentChangeSet(cs, stageActiveInline: true);
            if (candidate.Verification != null)
                AgentFeed.AddMessage(new AvaloniaAgentFeedItem { Role = "verification", ChangeSet = cs, Verification = candidate.Verification });
            RefreshChangesetBanner();
            return cs;
        }

        // ------------------------------------------------------------------ settings modal

        public void ShowVerificationSettingsModal()
        {
            var stack = new StackPanel { Spacing = 8 };
            var resolved = !string.IsNullOrEmpty(workspaceRoot) ? VerificationCommandResolver.Resolve(workspaceRoot, settings.AgentVerifyCommand) : null;
            stack.Children.Add(WorkbenchStyles.SectionLabel("CURRENT WORKSPACE", currentTheme));
            stack.Children.Add(new TextBlock
            {
                Text = resolved != null ? resolved.CommandLine + "   (" + resolved.Source + ")" : "No command found. Add .codeless/verify to the workspace, set \"codeless.verifyCommand\" in .vscode/settings.json, or set a command below.",
                TextWrapping = TextWrapping.Wrap, FontSize = UiTokens.FontSmall,
                FontFamily = resolved != null ? new FontFamily("Menlo, Consolas, DejaVu Sans Mono, monospace") : FontFamily.Default,
                Foreground = WorkbenchStyles.B(currentTheme.FgPrimary)
            });
            stack.Children.Add(WorkbenchStyles.SectionLabel("USER COMMAND (USED WHEN THE WORKSPACE DOESN'T SET ONE)", currentTheme));
            var cmdBox = new TextBox { Text = settings.AgentVerifyCommand ?? "", Watermark = "e.g. dotnet build, npm test, pytest -q, cargo check" };
            stack.Children.Add(cmdBox);
            var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,8,90,24,Auto,8,90,24,Auto,8,90") };
            NumericUpDown Num(int value, int min, int max) => new NumericUpDown { Value = value, Minimum = min, Maximum = max, Increment = 1, FormatString = "0", Height = 26 };
            var timeout = Num(settings.EffectiveAgentVerifyTimeoutSeconds, 10, 3600);
            var attempts = Num(settings.EffectiveAgentRepairMaxAttempts, 1, 5);
            var bestN = Num(settings.EffectiveAgentBestOfNCount, 2, 4);
            void Add(Control c, int col) { Grid.SetColumn(c, col); grid.Children.Add(c); }
            TextBlock L(string s) => new TextBlock { Text = s, FontSize = UiTokens.FontSmall, VerticalAlignment = VerticalAlignment.Center, Foreground = WorkbenchStyles.B(currentTheme.FgSecondary) };
            Add(L("Timeout (s)"), 0); Add(timeout, 2); Add(L("Repair attempts"), 4); Add(attempts, 6); Add(L("Best-of-N"), 8); Add(bestN, 10);
            stack.Children.Add(grid);
            stack.Children.Add(new TextBlock
            {
                Text = "Verification runs in a private temporary git clone (or a filtered copy) with the AI's staged edits applied; your files are never written. Repository-chosen commands ask for approval the first time.",
                TextWrapping = TextWrapping.Wrap, FontSize = UiTokens.FontSmall, Foreground = WorkbenchStyles.B(currentTheme.FgSecondary)
            });
            var row = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 8 };
            var cancel = new Button { Content = "Cancel", Padding = new Thickness(14, 4) };
            var save = new Button { Content = "Save", Padding = new Thickness(14, 4) };
            WorkbenchStyles.StyleSecondaryButton(cancel, currentTheme);
            WorkbenchStyles.StylePrimaryButton(save, currentTheme);
            cancel.Click += (s, e) => ModalHost.CloseModal();
            save.Click += (s, e) =>
            {
                settings.AgentVerifyCommand = (cmdBox.Text ?? "").Trim();
                settings.AgentVerifyTimeoutSeconds = (int)(timeout.Value ?? 300);
                settings.AgentRepairMaxAttempts = (int)(attempts.Value ?? 2);
                settings.AgentBestOfNCount = (int)(bestN.Value ?? 2);
                try { settings.Save(); } catch { }
                UpdateAgentTrustComposer();
                ModalHost.CloseModal();
            };
            row.Children.Add(cancel);
            row.Children.Add(save);
            stack.Children.Add(row);
            ModalHost.ShowModal("agent-verify-settings", "Agent Verification", stack, 620);
        }

        // ------------------------------------------------------------------ cards

        private Control BuildAgentTrustCard(AvaloniaAgentFeedItem item)
        {
            switch (item.Role)
            {
                case "verification": return BuildVerificationCard(item);
                case "turn_checkpoint": return BuildTurnCheckpointCard(item);
                case "bestofn": return BuildBestOfNCard(item);
                case "diff_review": return item.ChangeFile != null ? BuildChangeFileCard(item) : null;
                default: return null;
            }
        }

        private Border Card(Control child)
        {
            return new Border
            {
                Child = child,
                Background = WorkbenchStyles.B(UiTokens.WidgetBackground(currentTheme)),
                BorderBrush = WorkbenchStyles.B(UiTokens.WidgetBorder(currentTheme)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(UiTokens.PopupRadius),
                Padding = new Thickness(10, 8)
            };
        }

        private Button SmallButton(string text, bool primary, Action onClick, string name = null)
        {
            var b = new Button { Content = text, FontSize = UiTokens.FontSmall, Padding = new Thickness(10, 2), MinHeight = 22 };
            if (name != null) b.Name = name;
            if (primary) WorkbenchStyles.StylePrimaryButton(b, currentTheme); else WorkbenchStyles.StyleSecondaryButton(b, currentTheme);
            b.Click += (s, e) => { onClick(); e.Handled = true; };
            return b;
        }

        private TextBlock Mono(string text, Color fg)
        {
            return new TextBlock { Text = text, FontFamily = new FontFamily("Cascadia Code, Consolas, Menlo, monospace"), FontSize = 11.5, Foreground = new SolidColorBrush(fg), TextWrapping = TextWrapping.Wrap };
        }

        private Control BuildVerificationCard(AvaloniaAgentFeedItem item)
        {
            var stack = new StackPanel { Spacing = 6 };
            var v = item.Verification;
            Color fgPrimary = WorkbenchStyles.C(currentTheme.FgPrimary);
            Color fgSecondary = WorkbenchStyles.C(currentTheme.FgSecondary);
            var head = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
            IconLabel title;
            if (item.IsRunning)
            {
                title = new IconLabel("run", "Verifying…", 14, UiTokens.FontUi);
                title.Icon.IconColor = WorkbenchStyles.C(currentTheme.AccentPrimary);
                title.Icon.FollowForeground = false;
            }
            else
            {
                string icon = v == null ? "info" : v.Passed ? "check" : (v.Status == VerificationStatus.Failed || v.Status == VerificationStatus.TimedOut) ? "error" : "warning";
                title = new IconLabel(icon, v?.Badge ?? "Verification didn't run", 14, UiTokens.FontUi);
                title.Icon.FollowForeground = false;
                title.Icon.IconColor = WorkbenchStyles.C(v != null ? AgentTrustColors.ForStatus(currentTheme, v.Status) : currentTheme.FgSecondary);
            }
            title.Label.FontWeight = FontWeight.SemiBold;
            title.Label.Foreground = new SolidColorBrush(fgPrimary);
            head.Children.Add(title);
            if (!item.IsRunning && v != null && (v.DurationMs > 0 || v.PrepareMs > 0))
            {
                var dur = new TextBlock { Text = VerificationResult.FormatDuration(v.DurationMs), FontSize = UiTokens.FontSmall, Foreground = new SolidColorBrush(fgSecondary), VerticalAlignment = VerticalAlignment.Center };
                Grid.SetColumn(dur, 1);
                head.Children.Add(dur);
            }
            stack.Children.Add(head);
            if (item.IsRunning)
                stack.Children.Add(new TextBlock { Text = item.ProgressText ?? "", FontSize = UiTokens.FontSmall, TextWrapping = TextWrapping.Wrap, Foreground = new SolidColorBrush(fgSecondary) });
            if (v != null && !string.IsNullOrEmpty(v.CommandLine))
                stack.Children.Add(new TextBlock { Text = v.CommandLine + "  ·  " + v.Source + (string.IsNullOrEmpty(v.Strategy) ? "" : "  ·  " + (v.Strategy == "git-clone" ? "temporary git clone" : "temporary copy")), FontSize = UiTokens.FontSmall, TextWrapping = TextWrapping.Wrap, Foreground = new SolidColorBrush(fgSecondary) });
            if (v != null && !string.IsNullOrEmpty(v.Message))
                stack.Children.Add(new TextBlock { Text = v.Message, FontSize = UiTokens.FontSmall, TextWrapping = TextWrapping.Wrap, Foreground = new SolidColorBrush(fgPrimary) });
            if (v != null && v.Errors.Count > 0)
            {
                var errs = new StackPanel { Spacing = 1 };
                foreach (var d in v.Errors.Take(8))
                {
                    var line = new Button
                    {
                        Content = Mono(VerificationResult.FormatDiagnostic(d), WorkbenchStyles.C(AgentTrustColors.Failed(currentTheme))),
                        Background = Brushes.Transparent, Padding = new Thickness(2, 0), HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Left
                    };
                    line.Classes.Add("flat");
                    var diag = d;
                    line.Click += (s, e) => OpenDiagnosticLocation(diag);
                    errs.Children.Add(line);
                }
                if (v.Errors.Count > 8) errs.Children.Add(Mono("… and " + (v.Errors.Count - 8) + " more", fgSecondary));
                stack.Children.Add(errs);
            }
            foreach (var log in item.ProgressLog.Where(l => !l.StartsWith("Verifying", StringComparison.Ordinal) && !l.StartsWith("✓", StringComparison.Ordinal) && !l.StartsWith("✗", StringComparison.Ordinal)))
                stack.Children.Add(new TextBlock { Text = log, FontSize = UiTokens.FontSmall, TextWrapping = TextWrapping.Wrap, Foreground = new SolidColorBrush(fgSecondary) });
            if (v != null && item.DetailsExpanded && !string.IsNullOrEmpty(v.OutputTail))
                stack.Children.Add(new Border { Background = WorkbenchStyles.B(currentTheme.BgEditor), CornerRadius = new CornerRadius(UiTokens.ToolbarRadius), Padding = new Thickness(6), Child = Mono(VerificationResult.TailLines(v.OutputTail, 40), fgPrimary) });
            foreach (var n in (v?.Notes ?? new List<string>()))
                stack.Children.Add(new TextBlock { Text = n, FontSize = UiTokens.FontSmall, FontStyle = FontStyle.Italic, TextWrapping = TextWrapping.Wrap, Foreground = new SolidColorBrush(fgSecondary) });

            var buttons = new WrapPanel { Orientation = Orientation.Horizontal };
            void AddBtn(Button b) { b.Margin = new Thickness(0, 2, 6, 0); buttons.Children.Add(b); }
            var cs = item.ChangeSet;
            if (item.IsRunning) AddBtn(SmallButton("Stop", false, () => agentTrust.CancelRun(), "VerifyStopBtn"));
            else if (cs != null && cs.HasPending)
            {
                if (cs.IsVerifiedPassing) AddBtn(SmallButton("Accept All (Verified)", true, () => AcceptAgentChangeSet(cs, onlyIfVerified: true), "AcceptVerifiedBtn"));
                if (v != null && v.IsRepairable) AddBtn(SmallButton("Fix with AI", !cs.IsVerifiedPassing, () => _ = ReverifyAgentChangeSetAsync(cs, repair: true)));
                AddBtn(SmallButton("Re-run", false, () => _ = ReverifyAgentChangeSetAsync(cs, repair: false)));
            }
            if (v != null && !string.IsNullOrEmpty(v.OutputTail))
                AddBtn(SmallButton(item.DetailsExpanded ? "Hide Output" : "Show Output", false, () => { item.DetailsExpanded = !item.DetailsExpanded; AgentFeed.RebuildVisualFeed(); }));
            if (v != null && (v.Status == VerificationStatus.NoCommand || v.Status == VerificationStatus.Denied))
                AddBtn(SmallButton("Set Command…", false, ShowVerificationSettingsModal));
            if (buttons.Children.Count > 0) stack.Children.Add(buttons);
            return Card(stack);
        }

        private void OpenDiagnosticLocation(DiagnosticItem d)
        {
            if (d == null || string.IsNullOrEmpty(d.FilePath) || !File.Exists(d.FilePath)) return;
            var tab = OpenFileInTab(d.FilePath);
            if (tab != null) PrimaryEditor.GoToLine(Math.Max(1, d.Line), Math.Max(1, d.Column));
        }

        private Control BuildChangeFileCard(AvaloniaAgentFeedItem item)
        {
            var f = item.ChangeFile;
            var stack = new StackPanel { Spacing = 6 };
            var head = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto") };
            head.Children.Add(new TextBlock
            {
                Text = (f.IsNewFile ? "New file: " : "") + f.RelativePath,
                FontWeight = FontWeight.SemiBold, FontSize = UiTokens.FontUi, TextTrimming = TextTrimming.CharacterEllipsis,
                Foreground = WorkbenchStyles.B(currentTheme.FgPrimary), VerticalAlignment = VerticalAlignment.Center
            });
            var stats = new TextBlock { Text = $"+{f.Added} −{f.Removed}", FontFamily = new FontFamily("Cascadia Code, Consolas, Menlo, monospace"), FontSize = 11, Margin = new Thickness(8, 0), VerticalAlignment = VerticalAlignment.Center, Foreground = WorkbenchStyles.B(currentTheme.FgSecondary) };
            Grid.SetColumn(stats, 1);
            head.Children.Add(stats);
            var status = new TextBlock { Text = f.Status, FontSize = UiTokens.FontSmall, VerticalAlignment = VerticalAlignment.Center, Foreground = WorkbenchStyles.B(f.Status == "Accepted" ? AgentTrustColors.Passed(currentTheme) : currentTheme.FgSecondary) };
            Grid.SetColumn(status, 2);
            head.Children.Add(status);
            stack.Children.Add(head);
            if (!string.IsNullOrEmpty(item.DiffPreviewText) && (f.IsPending || item.DetailsExpanded))
            {
                var lines = new StackPanel { Spacing = 0, Margin = new Thickness(6, 4) };
                bool light = UiTokens.IsLight(currentTheme);
                foreach (var raw in item.DiffPreviewText.Split('\n'))
                {
                    Color fg = WorkbenchStyles.C(currentTheme.FgPrimary);
                    IBrush bg = Brushes.Transparent;
                    if (raw.StartsWith("+")) { fg = light ? Color.FromRgb(21, 128, 61) : Color.FromRgb(115, 201, 145); bg = new SolidColorBrush(Color.FromArgb(36, 46, 160, 67)); }
                    else if (raw.StartsWith("-")) { fg = light ? Color.FromRgb(161, 38, 13) : Color.FromRgb(244, 135, 113); bg = new SolidColorBrush(Color.FromArgb(36, 200, 60, 40)); }
                    else if (raw.StartsWith("@@")) fg = WorkbenchStyles.C(currentTheme.FgSecondary);
                    lines.Children.Add(new Border { Background = bg, Child = new TextBlock { Text = raw, FontFamily = new FontFamily("Cascadia Code, Consolas, Menlo, monospace"), FontSize = 11.5, Foreground = new SolidColorBrush(fg), TextWrapping = TextWrapping.NoWrap } });
                }
                stack.Children.Add(new Border
                {
                    Background = WorkbenchStyles.B(currentTheme.BgEditor), BorderBrush = WorkbenchStyles.B(currentTheme.BorderSubtle), BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(UiTokens.ToolbarRadius), ClipToBounds = true,
                    Child = new ScrollViewer { Content = lines, MaxHeight = 240, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto }
                });
            }
            if (f.IsPending)
            {
                var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
                row.Children.Add(SmallButton("Accept", true, () => { HandleAgentTrustDiffDecision(item, true); }));
                row.Children.Add(SmallButton("Reject", false, () => { HandleAgentTrustDiffDecision(item, false); }));
                row.Children.Add(SmallButton(f.IsNewFile ? "Preview" : "Review in Editor", false, () => ReviewChangeFileInEditor(item)));
                stack.Children.Add(row);
            }
            return Card(stack);
        }

        /// <summary>Opens the file and stages the proposal as an inline diff (per-hunk Accept / Reject in the editor).</summary>
        private void ReviewChangeFileInEditor(AvaloniaAgentFeedItem item)
        {
            var f = item.ChangeFile;
            if (f == null || !f.IsPending) return;
            if (f.IsNewFile && !File.Exists(f.FullPath))
            {
                item.DetailsExpanded = true;
                AgentFeed.RebuildVisualFeed();
                return;
            }
            var tab = OpenFileInTab(f.FullPath);
            if (tab == null) return;
            if (!tab.HasPendingDiff)
            {
                if (tab.IsDirty) { SetStatusMessage(f.RelativePath + " has unsaved changes: save or revert them before reviewing the AI's proposal."); return; }
                PrimaryEditor.StageProposedDiffContent(f.ProposedText);
            }
            RefreshChangesetBanner();
        }

        private Control BuildTurnCheckpointCard(AvaloniaAgentFeedItem item)
        {
            var store = CurrentTurnStore();
            var t = store?.FindTurn(item.TurnId);
            if (t == null) return new Border { IsVisible = false };
            return Card(BuildTurnCheckpointBody(store, t, closeModalFirst: false));
        }

        private Control BuildTurnCheckpointBody(AgentTurnCheckpointStore store, AgentTurnRecord t, bool closeModalFirst)
        {
            var stack = new StackPanel { Spacing = 4 };
            var head = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
            var title = new IconLabel("history", (t.Undone ? "Undone · " : "Checkpoint · ") + (string.IsNullOrEmpty(t.Label) ? "agent turn" : ShortLabel(t.Label, 48)), 13, UiTokens.FontUi);
            title.Label.FontWeight = FontWeight.SemiBold;
            title.Label.Foreground = WorkbenchStyles.B(t.Undone ? currentTheme.FgSecondary : currentTheme.FgPrimary);
            if (t.Undone) title.Label.TextDecorations = TextDecorations.Strikethrough;
            head.Children.Add(title);
            var when = new TextBlock { Text = t.CreatedUtc.ToLocalTime().ToString("HH:mm"), FontSize = UiTokens.FontSmall, VerticalAlignment = VerticalAlignment.Center, Foreground = WorkbenchStyles.B(currentTheme.FgSecondary) };
            Grid.SetColumn(when, 1);
            head.Children.Add(when);
            stack.Children.Add(head);
            var desc = t.DescribeFiles();
            foreach (var d in desc.Take(6))
                stack.Children.Add(new TextBlock { Text = d, FontSize = UiTokens.FontSmall, TextTrimming = TextTrimming.CharacterEllipsis, Foreground = WorkbenchStyles.B(currentTheme.FgSecondary) });
            if (desc.Count > 6) stack.Children.Add(new TextBlock { Text = "… and " + (desc.Count - 6) + " more", FontSize = UiTokens.FontSmall, Foreground = WorkbenchStyles.B(currentTheme.FgSecondary) });
            var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Margin = new Thickness(0, 2, 0, 0) };
            string id = t.Id;
            if (!t.Undone)
            {
                row.Children.Add(SmallButton("Undo This Turn", false, async () => { if (closeModalFirst) ModalHost.CloseModal(); await UndoAgentTurnAsync(id, false); }));
                bool hasLater = store.Turns.SkipWhile(x => x.Id != id).Skip(1).Any(x => !x.Undone);
                if (hasLater) row.Children.Add(SmallButton("Undo Back to Here", false, async () => { if (closeModalFirst) ModalHost.CloseModal(); await UndoAgentTurnAsync(id, true); }));
            }
            else if (store.CanRedo)
            {
                row.Children.Add(SmallButton("Redo", false, async () => { if (closeModalFirst) ModalHost.CloseModal(); await RedoAgentTurnAsync(); }));
            }
            if (row.Children.Count > 0) stack.Children.Add(row);
            return stack;
        }

        private Control BuildBestOfNCard(AvaloniaAgentFeedItem item)
        {
            var run = item.BestOfN;
            var stack = new StackPanel { Spacing = 6 };
            var title = new IconLabel("arena", "Best of " + run.Specs.Count + (item.IsRunning ? " · running…" : ""), 14, UiTokens.FontUi);
            title.Label.FontWeight = FontWeight.SemiBold;
            title.Label.Foreground = WorkbenchStyles.B(currentTheme.FgPrimary);
            stack.Children.Add(title);
            if (!string.IsNullOrEmpty(run.Note))
                stack.Children.Add(new TextBlock { Text = run.Note, FontSize = UiTokens.FontSmall, TextWrapping = TextWrapping.Wrap, Foreground = WorkbenchStyles.B(currentTheme.FgSecondary) });
            List<BestOfNCandidateResult> results;
            lock (run) results = run.Results.ToList();
            bool adopted = !string.IsNullOrEmpty(item.ProgressText);
            foreach (var spec in run.Specs)
            {
                var r = results.FirstOrDefault(x => ReferenceEquals(x.Spec, spec));
                var rowGrid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), Margin = new Thickness(0, 2) };
                var info = new StackPanel { Spacing = 1 };
                var name = new TextBlock { Text = spec.Label + (r != null && r.Recommended ? "  · recommended" : ""), FontSize = UiTokens.FontUi, FontWeight = r != null && r.Recommended ? FontWeight.SemiBold : FontWeight.Normal, TextTrimming = TextTrimming.CharacterEllipsis, Foreground = WorkbenchStyles.B(currentTheme.FgPrimary) };
                info.Children.Add(name);
                string stateText;
                Color stateColor = WorkbenchStyles.C(currentTheme.FgSecondary);
                if (r == null || r.State == "pending" || r.State == "running") stateText = "running…";
                else if (r.State == "verifying") stateText = "verifying…";
                else
                {
                    stateText = !string.IsNullOrEmpty(r.Error) ? r.Error : r.VerificationLabel;
                    if (r.Verification != null) stateColor = WorkbenchStyles.C(AgentTrustColors.ForStatus(currentTheme, r.Verification.Status));
                }
                info.Children.Add(new TextBlock { Text = stateText, FontSize = UiTokens.FontSmall, TextWrapping = TextWrapping.Wrap, Foreground = new SolidColorBrush(stateColor) });
                if (r != null && r.Turn != null)
                    info.Children.Add(new TextBlock
                    {
                        Text = $"+{r.Added} −{r.Removed} · {r.FilesChanged} file(s) · {VerificationResult.FormatDuration(r.LatencyMs)} · {r.CostLabel}",
                        FontSize = UiTokens.FontSmall, Foreground = WorkbenchStyles.B(currentTheme.FgSecondary)
                    });
                rowGrid.Children.Add(info);
                if (r != null && r.HasEdits && !item.IsRunning && !adopted)
                {
                    var candidate = r;
                    var adopt = SmallButton("Adopt", r.Recommended, () => { AdoptBestOfNCandidate(item, candidate); AgentFeed.RebuildVisualFeed(); });
                    adopt.VerticalAlignment = VerticalAlignment.Center;
                    Grid.SetColumn(adopt, 1);
                    rowGrid.Children.Add(adopt);
                }
                stack.Children.Add(rowGrid);
            }
            if (adopted) stack.Children.Add(new TextBlock { Text = item.ProgressText, FontSize = UiTokens.FontSmall, Foreground = WorkbenchStyles.B(currentTheme.FgSecondary) });
            foreach (var log in item.ProgressLog) stack.Children.Add(new TextBlock { Text = log, FontSize = UiTokens.FontSmall, TextWrapping = TextWrapping.Wrap, Foreground = WorkbenchStyles.B(currentTheme.FgSecondary) });
            if (item.IsRunning) stack.Children.Add(SmallButton("Stop", false, () => agentTrust.CancelRun()));
            return Card(stack);
        }
    }
}
