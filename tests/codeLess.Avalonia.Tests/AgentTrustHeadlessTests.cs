using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using codeLess.AvaloniaApp;
using codeLess.Studio;
using Xunit;

namespace codeLess.Avalonia.Tests
{
    /// <summary>Chat / review UI wiring of the agent trust features (verification, repair, accept, undo, Best-of-N).</summary>
    [Collection("SequentialWorkbench")]
    public class AgentTrustHeadlessTests : IDisposable
    {
        // Opening MainWindow records recent workspaces in the user's real settings file; restore it afterwards.
        private readonly string settingsFilePath = IdePersistentSettings.GetSettingsFilePath();
        private readonly string settingsBackup;

        public AgentTrustHeadlessTests()
        {
            AgentTrustTestEnv.Init();
            settingsBackup = File.Exists(settingsFilePath) ? File.ReadAllText(settingsFilePath) : null;
        }

        public void Dispose()
        {
            try
            {
                if (settingsBackup != null) File.WriteAllText(settingsFilePath, settingsBackup);
                else if (File.Exists(settingsFilePath)) File.Delete(settingsFilePath);
            }
            catch { }
        }

        private const string Calc = "public class Calculator\n{\n    public int Add(int a, int b) { return a + b; }\n}\n";

        private static string Workspace(bool git, string verifyScript)
        {
            string ws = AgentTrustTestEnv.NewDir("ui");
            if (git) AgentTrustTestEnv.InitRepo(ws);
            File.WriteAllText(Path.Combine(ws, "Calculator.cs"), Calc);
            File.WriteAllText(Path.Combine(ws, "Other.cs"), "public class Other\n{\n    public int V = 1;\n}\n");
            if (verifyScript != null)
            {
                Directory.CreateDirectory(Path.Combine(ws, ".codeless"));
                File.WriteAllText(Path.Combine(ws, ".codeless", "verify"), verifyScript + "\n");
            }
            if (git) AgentTrustTestEnv.Commit(ws, "init");
            return ws;
        }

        private static MainWindow Open(string ws, AgentVerifyMode mode)
        {
            var w = new MainWindow(ws);
            w.SuppressUnsavedPrompts = true;
            w.Show();
            w.AgentTrust.Settings.SetAgentVerifyMode(mode); // in memory only (never saved by the test)
            w.AgentTrust.Settings.RequireAgentToolApproval = true;
            return w;
        }

        private static void CloseWindow(MainWindow w)
        {
            w.AgentTrust.Settings.SetAgentVerifyMode(AgentVerifyMode.Off);
            w.HarnessEngine.CustomModelResponder = null;
            w.Close();
        }

        private static string Patch(string file, string search, string replace)
        {
            return "### File: " + file + "\n<<<<<<< SEARCH\n" + search + "\n=======\n" + replace + "\n>>>>>>> REPLACE";
        }

        [AvaloniaFact]
        public async Task VerifyMode_ShowsPassedResult_AcceptIfVerifiedWrites_AndTurnCanBeUndoneAndRedone()
        {
            if (OperatingSystem.IsWindows()) return;
            string ws = Workspace(true, "grep -q 'checked(a + b)' Calculator.cs");
            try
            {
                var w = Open(ws, AgentVerifyMode.Verify);
                var approvals = new List<VerificationApprovalRequest>();
                w.AgentVerifyApprovalOverride = r => { approvals.Add(r); return VerificationApprovalDecision.AllowOnce; };
                var tab = w.OpenFileInTab(Path.Combine(ws, "Calculator.cs"));
                w.HarnessEngine.CustomModelResponder = (sys, msgs) => Task.FromResult(Patch("Calculator.cs", "    public int Add(int a, int b) { return a + b; }", "    public int Add(int a, int b) { return checked(a + b); }"));

                await w.SendChatMessageAsync("Use checked arithmetic in Add");

                var verification = w.AgentFeed.Items.Last(i => i.Role == "verification");
                Assert.False(verification.IsRunning);
                Assert.True(verification.Verification.Passed, verification.Verification.Summary);
                Assert.Equal("✓ verification passed", verification.Verification.Badge);
                Assert.Single(approvals); // repository-chosen command (.codeless/verify): asked once
                var card = w.AgentFeed.Items.Single(i => i.Role == "diff_review");
                Assert.NotNull(card.ChangeFile);
                Assert.Contains("+    public int Add(int a, int b) { return checked(a + b); }", card.DiffPreviewText);
                Assert.True(tab.HasPendingDiff);                         // active file still staged inline
                Assert.Equal(Calc, File.ReadAllText(Path.Combine(ws, "Calculator.cs"))); // nothing written yet
                var cs = w.AgentTrust.ActiveChangeSet;
                Assert.True(cs.IsVerifiedPassing);
                var banner = FindByName<Button>(w, "AcceptIfVerifiedBtn");
                Assert.NotNull(banner);
                Assert.True(banner.IsVisible && banner.IsEnabled);

                var summary = w.AcceptAgentChangeSet(cs, onlyIfVerified: true);
                Assert.Single(summary.Written);
                Assert.Contains("checked(a + b)", File.ReadAllText(Path.Combine(ws, "Calculator.cs")));
                Assert.False(tab.IsDirty);
                Assert.False(cs.HasPending);
                var cp = w.AgentFeed.Items.Single(i => i.Role == "turn_checkpoint");
                var store = w.AgentTrust.GetStore(ws);
                Assert.Equal(cs.TurnId, cp.TurnId);
                Assert.Single(store.FindTurn(cp.TurnId).Files);

                TurnRestorePlan seen = null;
                w.AgentRestoreConfirmOverride = p => { seen = p; return "skip"; };
                var undo = await w.UndoAgentTurnAsync(cp.TurnId, false);
                Assert.True(undo.Completed);
                Assert.Equal("restore", seen.Items.Single().Action);
                Assert.Equal(Calc, File.ReadAllText(Path.Combine(ws, "Calculator.cs")));
                Assert.Equal(Calc.Replace("\r\n", "\n"), tab.Content.Replace("\r\n", "\n")); // clean editor follows the restore
                Assert.True(store.FindTurn(cp.TurnId).Undone);

                var redo = await w.RedoAgentTurnAsync();
                Assert.True(redo.Completed);
                Assert.Contains("checked(a + b)", File.ReadAllText(Path.Combine(ws, "Calculator.cs")));

                // "Restore" on the user message = undo back to that turn.
                var userMsg = w.AgentFeed.Items.First(i => i.Role == "user");
                Assert.Equal(cp.TurnId, userMsg.TurnId);
                Assert.True((await w.RestoreToUserMessageAsync(userMsg)).Completed);
                Assert.Equal(Calc, File.ReadAllText(Path.Combine(ws, "Calculator.cs")));
                CloseWindow(w);
            }
            finally { AgentTrustTestEnv.Delete(ws); }
        }

        private static T FindByName<T>(Control root, string name) where T : Control
        {
            foreach (var c in global::Avalonia.VisualTree.VisualExtensions.GetVisualDescendants(root))
                if (c is T t && t.Name == name) return t;
            return null;
        }

        [AvaloniaFact]
        public async Task VerifyAndRepair_SendsErrorsBack_UntilTheBuildPasses_AndRestagesTheEditor()
        {
            if (OperatingSystem.IsWindows()) return;
            string ws = Workspace(true, "if grep -q BROKEN Calculator.cs; then echo 'Calculator.cs(3,40): error CS1002: ; expected'; exit 1; fi");
            try
            {
                var w = Open(ws, AgentVerifyMode.VerifyAndRepair);
                w.AgentVerifyApprovalOverride = r => VerificationApprovalDecision.AllowOnce;
                var tab = w.OpenFileInTab(Path.Combine(ws, "Calculator.cs"));
                string repairPrompt = null;
                w.HarnessEngine.CustomModelResponder = (sys, msgs) =>
                {
                    string last = msgs.Last()["content"];
                    if (last.Contains("<verification_errors>"))
                    {
                        repairPrompt = last;
                        return Task.FromResult(Patch("Calculator.cs", "    public int Add(int a, int b) { return a + b + BROKEN; }", "    public int Add(int a, int b) { return a + b + 0; }"));
                    }
                    return Task.FromResult(Patch("Calculator.cs", "    public int Add(int a, int b) { return a + b; }", "    public int Add(int a, int b) { return a + b + BROKEN; }"));
                };

                await w.SendChatMessageAsync("Change Add");

                var verification = w.AgentFeed.Items.Last(i => i.Role == "verification");
                Assert.True(verification.Verification.Passed, verification.Verification.Summary);
                Assert.Equal(1, verification.Verification.Attempt);
                Assert.Contains(verification.ProgressLog, l => l.Contains("Repair attempt 1"));
                Assert.Contains(verification.ProgressLog, l => l.Contains("Auto-repair fixed it"));
                Assert.NotNull(repairPrompt);
                Assert.Contains("CS1002", repairPrompt);
                Assert.Contains("BROKEN", repairPrompt); // staged text is shown to the model
                var cs = w.AgentTrust.ActiveChangeSet;
                Assert.Equal(2, cs.Verifications.Count);
                Assert.Equal(VerificationStatus.Failed, cs.Verifications[0].Status);
                Assert.Contains("a + b + 0", cs.Files[0].ProposedText);
                Assert.Contains("a + b + 0", tab.Content);        // editor re-staged with the repaired proposal
                Assert.True(tab.HasPendingDiff);
                Assert.Equal(Calc, File.ReadAllText(Path.Combine(ws, "Calculator.cs")));
                Assert.Contains("a + b + 0", w.AgentFeed.Items.Single(i => i.Role == "diff_review").DiffPreviewText);
                CloseWindow(w);
            }
            finally { AgentTrustTestEnv.Delete(ws); }
        }

        [AvaloniaFact]
        public async Task ProposalForAFileThatIsNotOpen_AcceptWritesIt_ConflictsAndUnverifiedAcceptAreRefused()
        {
            string ws = Workspace(false, null);
            try
            {
                var w = Open(ws, AgentVerifyMode.Off);
                w.OpenFileInTab(Path.Combine(ws, "Calculator.cs"));
                w.HarnessEngine.CustomModelResponder = (sys, msgs) => Task.FromResult(
                    Patch("Other.cs", "    public int V = 1;", "    public int V = 2;") + "\n" + Patch("New/Created.cs", "", "public class Created { }"));
                await w.SendChatMessageAsync("Bump V and add a class");

                var cs = w.AgentTrust.ActiveChangeSet;
                Assert.Equal(2, cs.Files.Count);
                Assert.Null(cs.LatestVerification);
                var refused = w.AcceptAgentChangeSet(cs, onlyIfVerified: true);
                Assert.Contains("haven't been verified", refused.Refused);
                Assert.False(File.Exists(Path.Combine(ws, "New", "Created.cs")));

                // The user edits Other.cs on disk after the proposal: accepting must not clobber it.
                File.WriteAllText(Path.Combine(ws, "Other.cs"), "// user's own change\n");
                var other = cs.Files.Single(f => f.RelativePath.EndsWith("Other.cs"));
                var card = w.AgentFeed.Items.Single(i => i.Role == "diff_review" && ReferenceEquals(i.ChangeFile, other));
                var r1 = w.AcceptAgentChangeSetFiles(cs, new[] { other }, false);
                Assert.Single(r1.Conflicts);
                Assert.Equal("// user's own change\n", File.ReadAllText(Path.Combine(ws, "Other.cs")));
                Assert.Contains(w.AgentFeed.Items, i => i.Role == "system" && i.Content.Contains("Not applied"));

                var created = cs.Files.Single(f => f.IsNewFile);
                var r2 = w.AcceptAgentChangeSetFiles(cs, new[] { created }, false);
                Assert.Single(r2.Written);
                Assert.Equal("public class Created { }", File.ReadAllText(Path.Combine(ws, "New", "Created.cs")));
                Assert.Equal("Accepted", w.AgentFeed.Items.Single(i => ReferenceEquals(i.ChangeFile, created)).DiffStatus);

                w.RejectAgentChangeSetFiles(cs, new[] { other });
                Assert.Equal("Rejected", card.DiffStatus);
                Assert.False(cs.HasPending);
                CloseWindow(w);
            }
            finally { AgentTrustTestEnv.Delete(ws); }
        }

        [AvaloniaFact]
        public async Task TerminalCommandOfATurn_IsCheckpointed_AndUndoRestoresDeletedAndCreatedFiles_WithConflictPrompt()
        {
            if (OperatingSystem.IsWindows()) return;
            string ws = Workspace(true, null);
            try
            {
                File.WriteAllText(Path.Combine(ws, "old.txt"), "keep me\n");
                AgentTrustTestEnv.Commit(ws, "old");
                var w = Open(ws, AgentVerifyMode.Off);
                w.AgentToolApprovalOverride = (t, d) => true;
                int calls = 0;
                w.HarnessEngine.CustomModelResponder = (sys, msgs) =>
                {
                    calls++;
                    return Task.FromResult(calls == 1
                        ? "<tool_call>{\"tool\": \"run_terminal\", \"args\": {\"command\": \"rm old.txt && echo hi > made.txt\"}}</tool_call>"
                        : "Done: removed old.txt and created made.txt.");
                };
                await w.SendChatMessageAsync("clean up");
                Assert.False(File.Exists(Path.Combine(ws, "old.txt")));
                Assert.True(File.Exists(Path.Combine(ws, "made.txt")));
                var cp = w.AgentFeed.Items.Single(i => i.Role == "turn_checkpoint");
                var rec = w.AgentTrust.GetStore(ws).FindTurn(cp.TurnId);
                Assert.Contains("deleted old.txt", rec.DescribeFiles());
                Assert.Contains("created made.txt", rec.DescribeFiles());

                // The user edits made.txt afterwards: undo keeps it unless they choose to overwrite.
                File.WriteAllText(Path.Combine(ws, "made.txt"), "user edit\n");
                TurnRestorePlan plan = null;
                w.AgentRestoreConfirmOverride = p => { plan = p; return "skip"; };
                var r = await w.UndoAgentTurnAsync(cp.TurnId, false);
                Assert.True(plan.HasConflicts);
                Assert.Equal("keep me\n", File.ReadAllText(Path.Combine(ws, "old.txt")));
                Assert.Equal("user edit\n", File.ReadAllText(Path.Combine(ws, "made.txt")));
                Assert.False(r.Completed);

                w.AgentRestoreConfirmOverride = p => "cancel";
                Assert.Null(await w.UndoAgentTurnAsync(cp.TurnId, false));
                Assert.True(File.Exists(Path.Combine(ws, "made.txt")));

                w.AgentRestoreConfirmOverride = p => "overwrite";
                var r2 = await w.UndoAgentTurnAsync(cp.TurnId, false);
                Assert.True(r2.Completed);
                Assert.False(File.Exists(Path.Combine(ws, "made.txt")));
                Assert.Contains(w.AgentFeed.Items, i => i.Role == "system" && i.Content.Contains("backed up"));
                CloseWindow(w);
            }
            finally { AgentTrustTestEnv.Delete(ws); }
        }

        [AvaloniaFact]
        public async Task BestOfN_ComparesTwoLocalProviders_AndAdoptingStagesTheWinnerWithoutWriting()
        {
            if (OperatingSystem.IsWindows()) return;
            string ws = Workspace(false, null);
            using (var server = new FakeOpenAiServer(model => model == "good-model"
                ? Patch("Calculator.cs", "    public int Add(int a, int b) { return a + b; }", "    public int Add(int a, int b) { return checked(a + b); }")
                : Patch("Calculator.cs", "    public int Add(int a, int b) { return a + b; }", "    public int Add(int a, int b) { return a - b; }")))
            {
                try
                {
                    var w = Open(ws, AgentVerifyMode.Off);
                    var tab = w.OpenFileInTab(Path.Combine(ws, "Calculator.cs"));
                    w.HarnessEngine.CustomModelResponder = null;
                    w.HarnessEngine.EnableSseStreaming = false;
                    var savedVendors = w.HarnessEngine.Vendors;
                    w.HarnessEngine.Vendors = new List<ByokVendor>
                    {
                        AgentTrustBestOfNTests.LocalVendor("custom", "Local Good", server.BaseUrl, "good-model"),
                        AgentTrustBestOfNTests.LocalVendor("openai", "Local Bad", server.BaseUrl, "bad-model")
                    };
                    w.AgentTrust.CommandOverride = VerificationCommandResolver.Custom("grep -q 'checked(a + b)' Calculator.cs", "user setting", false);
                    w.SetAgentMode("edit");
                    var run = await w.RunBestOfNAsync("Use checked arithmetic");
                    w.HarnessEngine.Vendors = savedVendors;

                    var card = w.AgentFeed.Items.Single(i => i.Role == "bestofn");
                    Assert.False(card.IsRunning);
                    Assert.Equal(2, run.Results.Count);
                    Assert.True(run.Results[0].Recommended);
                    Assert.Equal("good-model", run.Results[0].Spec.Vendor.SelectedModel);
                    Assert.True(run.Results[0].Passed);
                    Assert.Equal(VerificationStatus.Failed, run.Results[1].Verification.Status);
                    Assert.Equal(Calc, File.ReadAllText(Path.Combine(ws, "Calculator.cs")));
                    Assert.False(tab.HasPendingDiff);

                    var cs = w.AdoptBestOfNCandidate(card, run.Results[0]);
                    Assert.True(cs.IsVerifiedPassing);
                    Assert.Same(cs, w.AgentTrust.ActiveChangeSet);
                    Assert.True(tab.HasPendingDiff);
                    Assert.Contains("checked(a + b)", tab.Content);
                    Assert.Contains(w.AgentFeed.Items, i => i.Role == "verification" && ReferenceEquals(i.ChangeSet, cs) && i.Verification.Passed);
                    Assert.Equal(Calc, File.ReadAllText(Path.Combine(ws, "Calculator.cs")));
                    w.SetAgentMode("agent");
                    CloseWindow(w);
                }
                finally { AgentTrustTestEnv.Delete(ws); }
            }
        }

        [AvaloniaFact]
        public async Task ManualSaveAfterPerHunkAccept_IsRecordedInTheTurnCheckpoint_AndUndoable()
        {
            string ws = Workspace(false, null);
            try
            {
                var w = Open(ws, AgentVerifyMode.Off);
                var tab = w.OpenFileInTab(Path.Combine(ws, "Calculator.cs"));
                w.HarnessEngine.CustomModelResponder = (sys, msgs) => Task.FromResult(Patch("Calculator.cs", "    public int Add(int a, int b) { return a + b; }", "    public int Add(int a, int b) { return b + a; }"));
                await w.SendChatMessageAsync("swap operands");
                Assert.True(tab.HasPendingDiff);
                int line = tab.ProposedNewLines.Min();
                w.PrimaryEditor.AcceptDiffHunkAtLine(line);     // per-hunk accept in the editor
                Assert.False(tab.HasPendingDiff);
                Assert.True(w.SaveActiveTab());                  // the user saves manually
                var cs = w.AgentTrust.ChangeSets.Last();
                Assert.Equal("Accepted", cs.Files[0].Status);
                var store = w.AgentTrust.GetStore(ws);
                Assert.NotNull(store.FindTurn(cs.TurnId));
                w.AgentRestoreConfirmOverride = p => "skip";
                Assert.True((await w.UndoAgentTurnAsync(cs.TurnId, false)).Completed);
                Assert.Equal(Calc, File.ReadAllText(Path.Combine(ws, "Calculator.cs")));
                CloseWindow(w);
            }
            finally { AgentTrustTestEnv.Delete(ws); }
        }

        [AvaloniaFact]
        public void ComposerControls_TimelineAndArena_OpenAndReflectSettings()
        {
            string ws = Workspace(false, null);
            try
            {
                var w = Open(ws, AgentVerifyMode.Off);
                var verifyBtn = FindByName<Button>(w, "AgentVerifyModeButton");
                var bestBtn = FindByName<Button>(w, "AgentBestOfNButton");
                Assert.NotNull(verifyBtn);
                Assert.NotNull(bestBtn);
                Assert.Contains("Verify: Off", ((IconLabel)verifyBtn.Content).Label.Text);
                w.AgentTrust.Settings.SetAgentVerifyMode(AgentVerifyMode.VerifyAndRepair);
                w.GetType().GetMethod("UpdateAgentTrustComposer", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance).Invoke(w, null);
                Assert.Contains("Verify + Repair", ((IconLabel)verifyBtn.Content).Label.Text);
                Assert.Contains("Best of 2", ((IconLabel)bestBtn.Content).Label.Text);

                w.ShowAgentTimelineModal();
                Assert.Equal("agent-timeline", w.ActiveModalId);
                w.ModalHost.CloseModal();
                w.ShowMultiHarnessArenaModal();
                Assert.Equal("arena", w.ActiveModalId);
                w.ModalHost.CloseModal();
                w.ShowVerificationSettingsModal();
                Assert.Equal("agent-verify-settings", w.ActiveModalId);
                w.ModalHost.CloseModal();

                // No provider configured for Best-of-N => explanatory error card, nothing runs.
                foreach (var v in w.HarnessEngine.Vendors) v.ApiKey = "";
                w.HarnessEngine.LocalOllamaDetected = false;
                var run = w.RunBestOfNAsync("anything").GetAwaiter().GetResult();
                Assert.Empty(run.Results);
                Assert.Contains(w.AgentFeed.Items, i => i.Role == "error" && i.Content.Contains("Best-of-N needs"));
                CloseWindow(w);
            }
            finally { AgentTrustTestEnv.Delete(ws); }
        }
    }
}
