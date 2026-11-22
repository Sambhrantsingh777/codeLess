using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace codeLess.Studio
{
    /// <summary>One contender: a provider/model plus a harness profile.</summary>
    public sealed class BestOfNCandidateSpec
    {
        public ByokVendor Vendor;
        public string HarnessId = "auto";
        public string Label = "";
    }

    public sealed class BestOfNCandidateResult
    {
        public BestOfNCandidateSpec Spec;
        public AgentExecutionTurnResult Turn;
        public VerificationResult Verification;
        public AgentChangeSet ChangeSet;
        public long LatencyMs;
        public int FilesChanged;
        public int Added;
        public int Removed;
        /// <summary>Approximate cost in USD; negative when unknown.</summary>
        public double EstimatedCostUsd = -1;
        public string Error = "";
        /// <summary>"pending", "running", "verifying", "done", "failed", "cancelled".</summary>
        public string State = "pending";
        public bool Recommended;

        public bool HasEdits { get { return FilesChanged > 0; } }
        public bool Passed { get { return Verification != null && Verification.Passed; } }

        public string CostLabel
        {
            get
            {
                if (EstimatedCostUsd < 0) return "cost n/a";
                if (EstimatedCostUsd == 0) return "free (local)";
                return EstimatedCostUsd < 0.01 ? "≈ <$0.01" : "≈ $" + EstimatedCostUsd.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture);
            }
        }

        public string VerificationLabel
        {
            get
            {
                if (!string.IsNullOrEmpty(Error) && Turn == null) return "error";
                if (!HasEdits) return "no edits";
                return Verification != null ? Verification.Badge : "not verified";
            }
        }
    }

    /// <summary>Picks Best-of-N contenders from the configured providers (Offline Mode: local endpoints only).</summary>
    public static class BestOfNPlanner
    {
        public static bool IsConfigured(ByokVendor v, bool localOllamaDetected)
        {
            if (v == null) return false;
            if (v.Id == "ollama") return localOllamaDetected || !string.IsNullOrWhiteSpace(v.ApiKey);
            return !string.IsNullOrWhiteSpace(v.ApiKey) && v.ApiKey.Trim().Length >= 5;
        }

        /// <summary>
        /// The active provider first, then other configured providers; when there are fewer providers than
        /// <paramref name="n"/>, the remaining slots use another model of the same provider or another harness.
        /// Under Offline Mode only loopback providers qualify. <paramref name="note"/> explains any shortfall.
        /// </summary>
        public static List<BestOfNCandidateSpec> Pick(IList<ByokVendor> vendors, ByokVendor active, string activeHarnessId, int n,
            bool offline, bool localOllamaDetected, IList<LlmHarnessProfile> harnesses, out string note)
        {
            note = "";
            List<BestOfNCandidateSpec> res = new List<BestOfNCandidateSpec>();
            n = Math.Max(2, Math.Min(4, n));
            List<ByokVendor> pool = new List<ByokVendor>();
            if (active != null) pool.Add(active);
            if (vendors != null) foreach (ByokVendor v in vendors) if (v != null && !pool.Contains(v)) pool.Add(v);
            List<ByokVendor> usable = new List<ByokVendor>();
            foreach (ByokVendor v in pool)
            {
                if (!IsConfigured(v, localOllamaDetected)) continue;
                if (offline && !NetworkLedger.IsLoopback(LlmHarnessAndAgentEngine.GetVendorEndpoint(v))) continue;
                usable.Add(v);
            }
            if (usable.Count == 0)
            {
                note = offline ? "Offline Mode is on and no local model (Ollama / LM Studio / custom 127.0.0.1 endpoint) is configured."
                               : "No AI provider is configured.";
                return res;
            }
            foreach (ByokVendor v in usable)
            {
                if (res.Count >= n) break;
                BestOfNCandidateSpec s = new BestOfNCandidateSpec();
                s.Vendor = LlmHarnessAndAgentEngine.CloneVendor(v);
                s.HarnessId = v == active && !string.IsNullOrEmpty(activeHarnessId) ? activeHarnessId : "auto";
                s.Label = (v.Name ?? v.Id) + " · " + v.SelectedModel;
                res.Add(s);
            }
            // Fill: other models of the configured providers, then other harnesses.
            foreach (ByokVendor v in usable)
            {
                if (res.Count >= n || v.Models == null) break;
                foreach (string m in v.Models)
                {
                    if (res.Count >= n) break;
                    if (string.IsNullOrEmpty(m) || res.Any(r => r.Vendor.Id == v.Id && r.Vendor.SelectedModel == m)) continue;
                    BestOfNCandidateSpec s = new BestOfNCandidateSpec();
                    s.Vendor = LlmHarnessAndAgentEngine.CloneVendor(v);
                    s.Vendor.SelectedModel = m;
                    s.Label = (v.Name ?? v.Id) + " · " + m;
                    res.Add(s);
                }
            }
            if (res.Count < n && harnesses != null)
            {
                ByokVendor v = usable[0];
                foreach (LlmHarnessProfile h in harnesses)
                {
                    if (res.Count >= n) break;
                    if (res.Any(r => r.Vendor.Id == v.Id && r.HarnessId == h.Id)) continue;
                    BestOfNCandidateSpec s = new BestOfNCandidateSpec();
                    s.Vendor = LlmHarnessAndAgentEngine.CloneVendor(v);
                    s.HarnessId = h.Id;
                    s.Label = (v.Name ?? v.Id) + " · " + v.SelectedModel + " · " + h.Name;
                    res.Add(s);
                }
            }
            if (res.Count < n) note = "Only " + res.Count + " candidate(s) available.";
            else if (usable.Count < res.Count) note = offline ? "Offline Mode: candidates use local models only." : "Fewer providers than candidates: extra candidates use other models or harnesses.";
            return res;
        }
    }

    /// <summary>
    /// Rough price table (USD per 1M input / output tokens) for cost estimates in the Best-of-N comparison.
    /// Token counts are estimates (chars / 4); local endpoints are free. Unknown models show "cost n/a".
    /// </summary>
    public static class AgentModelPricing
    {
        private static readonly string[][] Table = new string[][]
        {
            new string[] { "gpt-4o-mini", "0.15", "0.60" },
            new string[] { "gpt-4o", "2.50", "10.00" },
            new string[] { "gpt-4.1-mini", "0.40", "1.60" },
            new string[] { "gpt-4.1", "2.00", "8.00" },
            new string[] { "o3-mini", "1.10", "4.40" },
            new string[] { "claude-3-5-haiku", "0.80", "4.00" },
            new string[] { "claude-3.7-sonnet", "3.00", "15.00" },
            new string[] { "claude-3-7-sonnet", "3.00", "15.00" },
            new string[] { "claude-sonnet-4", "3.00", "15.00" },
            new string[] { "deepseek-chat", "0.27", "1.10" },
            new string[] { "deepseek-reasoner", "0.55", "2.19" },
            new string[] { "gemini-2.5-flash", "0.30", "2.50" },
            new string[] { "gemini-2.5-pro", "1.25", "10.00" },
            new string[] { "codestral", "0.30", "0.90" }
        };

        public static bool TryEstimate(ByokVendor v, int inputTokens, int outputTokens, out double usd)
        {
            usd = -1;
            if (v == null) return false;
            if (v.Id == "ollama" || NetworkLedger.IsLoopback(LlmHarnessAndAgentEngine.GetVendorEndpoint(v))) { usd = 0; return true; }
            string m = (v.SelectedModel ?? "").ToLowerInvariant();
            int slash = m.LastIndexOf('/');
            if (slash >= 0) m = m.Substring(slash + 1);
            foreach (string[] row in Table)
            {
                if (m.IndexOf(row[0], StringComparison.Ordinal) < 0) continue;
                double pin = double.Parse(row[1], System.Globalization.CultureInfo.InvariantCulture);
                double pout = double.Parse(row[2], System.Globalization.CultureInfo.InvariantCulture);
                usd = (inputTokens * pin + outputTokens * pout) / 1000000.0;
                return true;
            }
            return false;
        }
    }

    /// <summary>
    /// Runs the same request on several candidates concurrently (each with an isolated engine copy, terminal tool off),
    /// verifies every candidate's staged edits with the gate (verifications run one at a time by default to keep CPU and
    /// memory low), and ranks them: passed first, then smaller diffs, then faster. Nothing is applied; the user adopts
    /// one candidate, which becomes a normal reviewable change set.
    /// </summary>
    public sealed class BestOfNOrchestrator
    {
        /// <summary>Runs one candidate turn (the UI builds the engine copy / context).</summary>
        public Func<BestOfNCandidateSpec, CancellationToken, Task<AgentExecutionTurnResult>> RunCandidate;
        /// <summary>Verifies staged contents (blocking). Null = skip verification.</summary>
        public Func<Dictionary<string, string>, CancellationToken, VerificationResult> Verify;
        public string WorkspaceRoot = "";
        public Func<string, string> BaseTextProvider;
        public int MaxParallelVerifications = 1;
        /// <summary>Raised when a candidate changes state (worker threads).</summary>
        public Action<BestOfNCandidateResult> CandidateUpdated;

        public async Task<List<BestOfNCandidateResult>> RunAsync(List<BestOfNCandidateSpec> specs, string label, CancellationToken ct)
        {
            List<BestOfNCandidateResult> results = new List<BestOfNCandidateResult>();
            if (specs == null || specs.Count == 0 || RunCandidate == null) return results;
            SemaphoreSlim verifyGate = new SemaphoreSlim(Math.Max(1, MaxParallelVerifications));
            List<Task> tasks = new List<Task>();
            foreach (BestOfNCandidateSpec spec in specs)
            {
                BestOfNCandidateResult r = new BestOfNCandidateResult();
                r.Spec = spec;
                results.Add(r);
                tasks.Add(RunOneAsync(r, label, verifyGate, ct));
            }
            await Task.WhenAll(tasks.ToArray()).ConfigureAwait(false);
            Rank(results);
            return results;
        }

        private void Update(BestOfNCandidateResult r, string state)
        {
            r.State = state;
            Action<BestOfNCandidateResult> h = CandidateUpdated;
            if (h != null) { try { h(r); } catch { } }
        }

        private async Task RunOneAsync(BestOfNCandidateResult r, string label, SemaphoreSlim verifyGate, CancellationToken ct)
        {
            Stopwatch sw = Stopwatch.StartNew();
            Update(r, "running");
            try
            {
                BestOfNCandidateSpec spec = r.Spec;
                r.Turn = await Task.Run(delegate () { return RunCandidate(spec, ct); }).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                r.Error = ex.Message;
            }
            sw.Stop();
            r.LatencyMs = sw.ElapsedMilliseconds;
            if (r.Turn != null)
            {
                r.ChangeSet = AgentChangeSet.FromStaged(r.Turn.StagedFileContents, WorkspaceRoot, BaseTextProvider, label);
                r.ChangeSet.Origin = "best-of-n";
                r.ChangeSet.ProviderLabel = r.Spec.Label;
                r.FilesChanged = r.ChangeSet.Files.Count;
                r.Added = r.ChangeSet.TotalAdded;
                r.Removed = r.ChangeSet.TotalRemoved;
                int inTok = r.Turn.EstimatedInputTokens * Math.Max(1, r.Turn.Iterations);
                int outTok = LlmHarnessAndAgentEngine.EstimateTokens(r.Turn.AssistantReply ?? "");
                double usd;
                if (AgentModelPricing.TryEstimate(r.Spec.Vendor, inTok, outTok, out usd)) r.EstimatedCostUsd = usd;
                if (r.FilesChanged == 0 && LooksLikeError(r.Turn.AssistantReply)) r.Error = FirstLine(r.Turn.AssistantReply);
            }
            if (ct.IsCancellationRequested) { Update(r, "cancelled"); return; }
            if (r.Turn == null || r.FilesChanged == 0 || Verify == null)
            {
                Update(r, r.Turn == null || !string.IsNullOrEmpty(r.Error) ? "failed" : "done");
                return;
            }
            Update(r, "verifying");
            bool acquired = false;
            try
            {
                await verifyGate.WaitAsync(ct).ConfigureAwait(false);
                acquired = true;
            }
            catch (OperationCanceledException) { }
            if (!acquired || ct.IsCancellationRequested)
            {
                if (acquired) verifyGate.Release();
                Update(r, "cancelled");
                return;
            }
            try
            {
                Dictionary<string, string> staged = r.ChangeSet.PendingProposals();
                r.Verification = await Task.Run(delegate () { return Verify(staged, ct); }).ConfigureAwait(false);
                if (r.Verification != null) r.ChangeSet.AddVerification(r.Verification);
            }
            catch (Exception ex)
            {
                r.Verification = new VerificationResult();
                r.Verification.Status = VerificationStatus.Error;
                r.Verification.Message = ex.Message;
            }
            finally
            {
                verifyGate.Release();
            }
            Update(r, ct.IsCancellationRequested ? "cancelled" : "done");
        }

        private static bool LooksLikeError(string reply)
        {
            string s = (reply ?? "").TrimStart();
            return s.StartsWith("Vendor API Error", StringComparison.OrdinalIgnoreCase) || s.StartsWith("Error", StringComparison.OrdinalIgnoreCase) ||
                   s.StartsWith("No AI provider", StringComparison.OrdinalIgnoreCase) || s.IndexOf("Offline Mode", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static string FirstLine(string s)
        {
            string t = (s ?? "").Trim();
            int nl = t.IndexOf('\n');
            t = nl >= 0 ? t.Substring(0, nl) : t;
            return t.Length > 160 ? t.Substring(0, 160) + "…" : t;
        }

        /// <summary>Passed first, then candidates with edits, then smaller diffs, then lower latency.</summary>
        public static void Rank(List<BestOfNCandidateResult> results)
        {
            results.Sort(delegate (BestOfNCandidateResult a, BestOfNCandidateResult b)
            {
                int sa = Score(a), sb = Score(b);
                if (sa != sb) return sb.CompareTo(sa);
                int da = a.Added + a.Removed, db = b.Added + b.Removed;
                if (da != db) return da.CompareTo(db);
                return a.LatencyMs.CompareTo(b.LatencyMs);
            });
            foreach (BestOfNCandidateResult r in results) r.Recommended = false;
            if (results.Count > 0 && results[0].HasEdits && (results[0].Passed || results[0].Verification == null)) results[0].Recommended = true;
        }

        private static int Score(BestOfNCandidateResult r)
        {
            if (!r.HasEdits) return 0;
            if (r.Passed) return 3;
            if (r.Verification == null || r.Verification.Status == VerificationStatus.NoCommand || r.Verification.Status == VerificationStatus.Denied) return 2;
            return 1;
        }
    }
}
