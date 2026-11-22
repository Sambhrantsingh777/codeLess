using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace codeLess.Studio
{
    /// <summary>Everything needed to run (or re-run) an agent turn, shared by both UIs.</summary>
    public sealed class AgentTurnRequest
    {
        public LlmHarnessAndAgentEngine Engine;
        public string Mode = "agent";
        public string Prompt = "";
        public ByokVendor Vendor;
        public string HarnessId = "auto";
        public EditorTabBuffer ActiveTab;
        public string WorkspaceRoot = "";
        public List<string> WorkspaceFiles = new List<string>();
        public List<string> Pills = new List<string>();
        public string TerminalText = "";
        /// <summary>Open-buffer text for a path (null when not open): the base proposals are diffed against.</summary>
        public Func<string, string> BaseTextProvider;
    }

    public sealed class BestOfNRun
    {
        public string Note = "";
        public readonly List<BestOfNCandidateSpec> Specs = new List<BestOfNCandidateSpec>();
        public List<BestOfNCandidateResult> Results = new List<BestOfNCandidateResult>();
    }

    /// <summary>
    /// UI-independent glue for the agent trust features (both UIs call this): per-turn change tracking, change sets,
    /// verification gate + repair loop, Best-of-N and accept/undo through the per-workspace checkpoint store.
    /// The UI supplies the approval prompt (called on worker threads; it must block until the user answers).
    /// </summary>
    public sealed class AgentTrustCoordinator
    {
        public IdePersistentSettings Settings;
        public Func<VerificationApprovalRequest, VerificationApprovalDecision> RequestApproval;
        /// <summary>Overrides the resolved verification command (tests).</summary>
        public VerificationCommand CommandOverride;
        public readonly List<AgentChangeSet> ChangeSets = new List<AgentChangeSet>();
        private readonly object gate = new object();
        private AgentTurnCheckpointStore store;
        private CancellationTokenSource running;

        public AgentTrustCoordinator(IdePersistentSettings settings)
        {
            Settings = settings;
        }

        public AgentVerifyMode Mode
        {
            get { return Settings != null ? Settings.EffectiveAgentVerifyMode : AgentVerifyMode.Off; }
        }

        /// <summary>The checkpoint store of <paramref name="workspaceRoot"/> (cached; null without a workspace).</summary>
        public AgentTurnCheckpointStore GetStore(string workspaceRoot)
        {
            if (string.IsNullOrEmpty(workspaceRoot) || !Directory.Exists(workspaceRoot)) return null;
            lock (gate)
            {
                string full;
                try { full = Path.GetFullPath(workspaceRoot).TrimEnd('\\', '/'); } catch { full = workspaceRoot; }
                if (store == null || !string.Equals(store.WorkspaceRoot, full, StringComparison.OrdinalIgnoreCase))
                    store = new AgentTurnCheckpointStore(full);
                return store;
            }
        }

        public AgentVerifier CreateVerifier()
        {
            AgentVerifier v = new AgentVerifier(Settings);
            v.RequestApproval = RequestApproval;
            v.CommandOverride = CommandOverride;
            return v;
        }

        /// <summary>New cancellation scope for a verification / repair / Best-of-N run (cancels the previous one).</summary>
        public CancellationToken BeginRun()
        {
            lock (gate)
            {
                if (running != null) { try { running.Cancel(); } catch { } }
                running = new CancellationTokenSource();
                return running.Token;
            }
        }

        public bool IsRunning
        {
            get { lock (gate) return running != null && !running.IsCancellationRequested; }
        }

        public void CancelRun()
        {
            lock (gate)
            {
                if (running != null) { try { running.Cancel(); } catch { } }
            }
        }

        public void EndRun(CancellationToken token)
        {
            lock (gate)
            {
                if (running != null && running.Token == token) { running.Dispose(); running = null; }
            }
        }

        // ------------------------------------------------------------------ per-turn tracking

        /// <summary>Starts tracking an agent turn: the engine snapshots the workspace before its first terminal command.</summary>
        public AgentTurnTracker BeginTurn(LlmHarnessAndAgentEngine engine, string workspaceRoot, string label)
        {
            AgentTurnTracker t = new AgentTurnTracker(GetStore(workspaceRoot), label);
            if (engine != null) engine.OnBeforeRunTerminal = delegate (string cmd) { t.EnsureSnapshot(); };
            return t;
        }

        /// <summary>Stops tracking and records what the turn's terminal commands changed.</summary>
        public List<TurnFileChange> EndTurn(LlmHarnessAndAgentEngine engine, AgentTurnTracker tracker)
        {
            if (engine != null) engine.OnBeforeRunTerminal = null;
            if (tracker == null) return new List<TurnFileChange>();
            try { return tracker.Complete(); } catch { return new List<TurnFileChange>(); }
        }

        // ------------------------------------------------------------------ change sets

        public AgentChangeSet CreateChangeSet(IDictionary<string, string> staged, string workspaceRoot, Func<string, string> baseTextProvider, string label, string turnId)
        {
            AgentChangeSet cs = AgentChangeSet.FromStaged(staged, workspaceRoot, baseTextProvider, label);
            cs.TurnId = string.IsNullOrEmpty(turnId) ? AgentTurnCheckpointStore.NewTurnId() : turnId;
            if (cs.Files.Count > 0) lock (gate) ChangeSets.Add(cs);
            return cs;
        }

        /// <summary>The newest change set that still has pending files.</summary>
        public AgentChangeSet ActiveChangeSet
        {
            get
            {
                lock (gate)
                {
                    for (int i = ChangeSets.Count - 1; i >= 0; i--) if (ChangeSets[i].HasPending) return ChangeSets[i];
                    return null;
                }
            }
        }

        public void Register(AgentChangeSet cs)
        {
            if (cs == null) return;
            lock (gate) if (!ChangeSets.Contains(cs)) ChangeSets.Add(cs);
        }

        /// <summary>Replaces proposals with <paramref name="staged"/> (after repair turns), adding new files.</summary>
        public static void UpdateProposals(AgentChangeSet cs, IDictionary<string, string> staged, Func<string, string> baseTextProvider)
        {
            if (cs == null || staged == null) return;
            foreach (KeyValuePair<string, string> kv in staged)
            {
                AgentChangeSetFile f = cs.Find(kv.Key);
                if (f == null)
                {
                    AgentChangeSet extra = AgentChangeSet.FromStaged(new Dictionary<string, string> { { kv.Key, kv.Value } }, cs.WorkspaceRoot, baseTextProvider, cs.Label);
                    cs.Files.AddRange(extra.Files);
                    continue;
                }
                if (!f.IsPending) continue;
                f.ProposedText = kv.Value ?? "";
                AgentChangeSet.RecountDiff(f);
            }
        }

        public AgentApplyResult Accept(AgentChangeSet cs, IEnumerable<AgentChangeSetFile> files, bool overwriteConflicts)
        {
            if (cs == null) return new AgentApplyResult();
            return AgentChangeSetApplier.Accept(cs, files ?? cs.Files, GetStore(cs.WorkspaceRoot), overwriteConflicts);
        }

        // ------------------------------------------------------------------ verification + repair

        /// <summary>Repair turns run on an isolated engine copy (terminal off) and patch on top of the staged proposal.</summary>
        public static Task<AgentExecutionTurnResult> RunRepairTurnAsync(AgentTurnRequest req, string prompt, Dictionary<string, string> baseStaged, CancellationToken ct)
        {
            LlmHarnessAndAgentEngine e = req.Engine.CreateIsolatedCopy();
            ct.Register(delegate () { e.CancelStreamingRequested = true; });
            EditorTabBuffer tab = req.ActiveTab;
            string staged;
            if (tab != null && !string.IsNullOrEmpty(tab.FilePath) && baseStaged != null && baseStaged.TryGetValue(tab.FilePath, out staged))
            {
                EditorTabBuffer clone = new EditorTabBuffer();
                clone.FilePath = tab.FilePath;
                clone.RelativePath = tab.RelativePath;
                clone.FileName = tab.FileName;
                clone.Language = tab.Language;
                clone.Content = AgentChangeSet.Normalize(staged);
                clone.SyncLinesFromContent();
                tab = clone;
            }
            string mode = req.Mode == "ask" ? "composer" : req.Mode;
            return e.RunAutonomousAgentLoopAsync(mode, prompt, req.Vendor, req.HarnessId, tab, req.WorkspaceRoot, req.WorkspaceFiles, req.Pills, "", baseStaged);
        }

        /// <summary>
        /// Verifies the pending proposals of <paramref name="cs"/> (and repairs them in Verify+Repair mode). Call it on
        /// the UI thread: the proposals are snapshotted synchronously and the change set is never touched from worker
        /// threads; apply the outcome with <see cref="ApplyOutcome"/> back on the UI thread. Never writes workspace files.
        /// </summary>
        public async Task<AgentVerifiedTurnOutcome> VerifyAndRepairAsync(AgentChangeSet cs, AgentTurnRequest req, AgentVerifyMode mode,
            Action<AgentVerifiedTurnEvent> progress, CancellationToken ct)
        {
            Dictionary<string, string> pending = cs.PendingProposals();
            string label = cs.Label;
            AgentVerifier verifier = CreateVerifier();
            List<string> sessionApprovals = new List<string>();
            AgentVerifiedTurnRunner runner = new AgentVerifiedTurnRunner();
            runner.Mode = mode;
            runner.MaxRepairAttempts = Settings != null ? Settings.EffectiveAgentRepairMaxAttempts : IdePersistentSettings.DefaultAgentRepairMaxAttempts;
            runner.WorkspaceRoot = cs.WorkspaceRoot;
            runner.Progress = progress;
            string ws = cs.WorkspaceRoot;
            runner.Verify = delegate (Dictionary<string, string> staged, CancellationToken token)
            {
                return verifier.Verify(ws, staged, sessionApprovals, token, null);
            };
            if (req != null && req.Engine != null)
                runner.RunRepairTurn = delegate (string prompt, Dictionary<string, string> baseStaged) { return RunRepairTurnAsync(req, prompt, baseStaged, ct); };
            AgentExecutionTurnResult seed = new AgentExecutionTurnResult();
            foreach (KeyValuePair<string, string> kv in pending) seed.StagedFileContents[kv.Key] = kv.Value;
            return await runner.RunAsync(seed, req != null ? req.Prompt : label, ct).ConfigureAwait(false);
        }

        /// <summary>Applies a verification outcome to its change set (UI thread): repaired proposals + every verification.</summary>
        public static void ApplyOutcome(AgentChangeSet cs, AgentVerifiedTurnOutcome outcome, Func<string, string> baseTextProvider)
        {
            if (cs == null || outcome == null) return;
            if (outcome.RepairAttempts > 0) UpdateProposals(cs, outcome.FinalStaged, baseTextProvider);
            // The final verification describes the final proposals; earlier ones are kept for the transcript.
            foreach (VerificationResult v in outcome.Verifications)
            {
                if (v == outcome.FinalVerification) cs.AddVerification(v);
                else cs.Verifications.Add(v);
            }
        }

        // ------------------------------------------------------------------ Best-of-N

        public BestOfNRun PlanBestOfN(AgentTurnRequest req, int n)
        {
            BestOfNRun run = new BestOfNRun();
            string note;
            LlmHarnessAndAgentEngine e = req.Engine;
            run.Specs.AddRange(BestOfNPlanner.Pick(e.Vendors, req.Vendor, req.HarnessId, n, NetworkLedger.OfflineMode, e.LocalOllamaDetected, e.Harnesses, out note));
            run.Note = note;
            return run;
        }

        /// <summary>Runs the planned candidates concurrently and verifies each (one verification at a time).</summary>
        public async Task<BestOfNRun> RunBestOfNAsync(AgentTurnRequest req, BestOfNRun run, bool verify, Action<BestOfNCandidateResult> onUpdate, CancellationToken ct)
        {
            AgentVerifier verifier = CreateVerifier();
            List<string> sessionApprovals = new List<string>();
            BestOfNOrchestrator orch = new BestOfNOrchestrator();
            orch.WorkspaceRoot = req.WorkspaceRoot;
            orch.BaseTextProvider = req.BaseTextProvider;
            orch.CandidateUpdated = onUpdate;
            orch.RunCandidate = delegate (BestOfNCandidateSpec spec, CancellationToken token)
            {
                LlmHarnessAndAgentEngine e = req.Engine.CreateIsolatedCopy();
                token.Register(delegate () { e.CancelStreamingRequested = true; });
                return e.RunAutonomousAgentLoopAsync(req.Mode == "ask" ? "composer" : req.Mode, req.Prompt, spec.Vendor, spec.HarnessId,
                    req.ActiveTab, req.WorkspaceRoot, req.WorkspaceFiles, req.Pills, req.TerminalText);
            };
            if (verify)
            {
                string ws = req.WorkspaceRoot;
                orch.Verify = delegate (Dictionary<string, string> staged, CancellationToken token) { return verifier.Verify(ws, staged, sessionApprovals, token, null); };
            }
            run.Results = await orch.RunAsync(run.Specs, req.Prompt, ct).ConfigureAwait(false);
            return run;
        }

        /// <summary>Makes a candidate the active change set (nothing is written; the user reviews it as usual).</summary>
        public AgentChangeSet Adopt(BestOfNCandidateResult candidate)
        {
            if (candidate == null || candidate.ChangeSet == null) return null;
            AgentChangeSet cs = candidate.ChangeSet;
            if (string.IsNullOrEmpty(cs.TurnId)) cs.TurnId = AgentTurnCheckpointStore.NewTurnId();
            Register(cs);
            return cs;
        }
    }
}
