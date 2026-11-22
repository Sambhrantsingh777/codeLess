using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace codeLess.Studio
{
    /// <summary>Progress of a verified turn, raised on worker threads (UIs marshal to their thread).</summary>
    public sealed class AgentVerifiedTurnEvent
    {
        /// <summary>"verify-start", "verify-done", "repair-start", "repair-done", "stopped".</summary>
        public string Kind = "";
        /// <summary>0 = the original proposal, 1.. = repair attempts.</summary>
        public int Attempt;
        public AgentExecutionTurnResult Turn;
        public VerificationResult Verification;
        public string Message = "";
    }

    public sealed class AgentVerifiedTurnOutcome
    {
        public AgentExecutionTurnResult InitialTurn;
        /// <summary>Proposed contents after all repair turns (what the user reviews).</summary>
        public Dictionary<string, string> FinalStaged = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        public readonly List<VerificationResult> Verifications = new List<VerificationResult>();
        public readonly List<AgentExecutionTurnResult> RepairTurns = new List<AgentExecutionTurnResult>();
        /// <summary>"off", "no-edits", "passed", "failed", "max-attempts", "no-changes", "model-error", "cancelled", "not-repairable".</summary>
        public string StopReason = "";

        public VerificationResult FinalVerification
        {
            get { return Verifications.Count > 0 ? Verifications[Verifications.Count - 1] : null; }
        }

        public int RepairAttempts { get { return RepairTurns.Count; } }
    }

    /// <summary>
    /// Verification gate + automatic repair loop. After a turn proposes edits, the staged contents are verified (in a
    /// temporary copy, see <see cref="AgentVerifier"/>); in Verify+Repair mode a failure is sent back to the model with
    /// the diagnostics and the staged files, for at most <see cref="MaxRepairAttempts"/> turns. The loop stops on
    /// success, when the model makes no further change, on model errors, on cancellation, or when the failure isn't
    /// something code can fix (timeout, missing tool, denied). It never accepts anything: the result stays staged.
    /// </summary>
    public sealed class AgentVerifiedTurnRunner
    {
        public AgentVerifyMode Mode = AgentVerifyMode.Verify;
        public int MaxRepairAttempts = IdePersistentSettings.DefaultAgentRepairMaxAttempts;
        public string WorkspaceRoot = "";
        /// <summary>Runs a repair turn: (prompt, staged contents to patch on top of) -> turn result.</summary>
        public Func<string, Dictionary<string, string>, Task<AgentExecutionTurnResult>> RunRepairTurn;
        /// <summary>Verifies staged contents (blocking; called on a worker thread).</summary>
        public Func<Dictionary<string, string>, CancellationToken, VerificationResult> Verify;
        public Action<AgentVerifiedTurnEvent> Progress;

        private void Raise(string kind, int attempt, AgentExecutionTurnResult turn, VerificationResult v, string msg)
        {
            Action<AgentVerifiedTurnEvent> h = Progress;
            if (h == null) return;
            AgentVerifiedTurnEvent e = new AgentVerifiedTurnEvent();
            e.Kind = kind;
            e.Attempt = attempt;
            e.Turn = turn;
            e.Verification = v;
            e.Message = msg ?? "";
            try { h(e); } catch { }
        }

        public async Task<AgentVerifiedTurnOutcome> RunAsync(AgentExecutionTurnResult initialTurn, string instruction, CancellationToken ct)
        {
            AgentVerifiedTurnOutcome outcome = new AgentVerifiedTurnOutcome();
            outcome.InitialTurn = initialTurn;
            if (initialTurn != null && initialTurn.StagedFileContents != null)
                foreach (KeyValuePair<string, string> kv in initialTurn.StagedFileContents) outcome.FinalStaged[kv.Key] = kv.Value;
            if (Mode == AgentVerifyMode.Off || Verify == null) { outcome.StopReason = "off"; return outcome; }
            if (outcome.FinalStaged.Count == 0) { outcome.StopReason = "no-edits"; return outcome; }

            int attempt = 0;
            while (true)
            {
                if (ct.IsCancellationRequested) { outcome.StopReason = "cancelled"; break; }
                Raise("verify-start", attempt, null, null, attempt == 0 ? "Verifying the proposed changes…" : "Verifying repair attempt " + attempt + "…");
                Dictionary<string, string> toVerify = new Dictionary<string, string>(outcome.FinalStaged, StringComparer.OrdinalIgnoreCase);
                VerificationResult v;
                try
                {
                    v = await Task.Run(delegate () { return Verify(toVerify, ct); }).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    v = new VerificationResult();
                    v.Status = VerificationStatus.Error;
                    v.Message = ex.Message;
                }
                if (v == null) { v = new VerificationResult(); v.Status = VerificationStatus.Error; v.Message = "No result."; }
                v.Attempt = attempt;
                outcome.Verifications.Add(v);
                Raise("verify-done", attempt, null, v, v.Summary);

                if (v.Passed) { outcome.StopReason = "passed"; break; }
                if (v.Status == VerificationStatus.Cancelled || ct.IsCancellationRequested) { outcome.StopReason = "cancelled"; break; }
                if (!v.IsRepairable) { outcome.StopReason = "not-repairable"; break; }
                if (Mode != AgentVerifyMode.VerifyAndRepair || RunRepairTurn == null) { outcome.StopReason = "failed"; break; }
                if (attempt >= MaxRepairAttempts) { outcome.StopReason = "max-attempts"; break; }

                attempt++;
                Raise("repair-start", attempt, null, v, "Repair attempt " + attempt + " of " + MaxRepairAttempts + ": sending " + (v.Errors.Count > 0 ? v.Errors.Count + " error(s)" : "the failure output") + " to the model…");
                string prompt = BuildRepairPrompt(instruction, v, outcome.FinalStaged, WorkspaceRoot);
                AgentExecutionTurnResult repair = null;
                string repairError = null;
                try
                {
                    repair = await RunRepairTurn(prompt, new Dictionary<string, string>(outcome.FinalStaged, StringComparer.OrdinalIgnoreCase)).ConfigureAwait(false);
                }
                catch (Exception ex) { repairError = ex.Message; }
                if (repair != null) outcome.RepairTurns.Add(repair);
                if (ct.IsCancellationRequested) { Raise("repair-done", attempt, repair, null, "Cancelled."); outcome.StopReason = "cancelled"; break; }
                if (repair == null || repair.StagedFileContents == null || repair.StagedFileContents.Count == 0)
                {
                    Raise("repair-done", attempt, repair, null, "The model returned no usable edit" + (repairError != null ? ": " + repairError : (repair != null && IsErrorReply(repair.AssistantReply) ? ": " + FirstLine(repair.AssistantReply) : ".")));
                    outcome.StopReason = "model-error";
                    break;
                }
                if (SameContents(repair.StagedFileContents, outcome.FinalStaged))
                {
                    Raise("repair-done", attempt, repair, null, "The model made no further changes.");
                    outcome.StopReason = "no-changes";
                    break;
                }
                outcome.FinalStaged = new Dictionary<string, string>(repair.StagedFileContents, StringComparer.OrdinalIgnoreCase);
                Raise("repair-done", attempt, repair, null, "Repair attempt " + attempt + " changed " + CountChanged(repair.StagedFileContents, toVerify) + " file(s).");
            }
            Raise("stopped", attempt, null, outcome.FinalVerification, outcome.StopReason);
            return outcome;
        }

        private static bool IsErrorReply(string reply)
        {
            string r = (reply ?? "").TrimStart();
            return r.StartsWith("Vendor API Error", StringComparison.OrdinalIgnoreCase) || r.StartsWith("Error:", StringComparison.OrdinalIgnoreCase) ||
                   r.StartsWith("No AI provider", StringComparison.OrdinalIgnoreCase) || r.IndexOf("Offline Mode", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static string FirstLine(string s)
        {
            string t = (s ?? "").Trim();
            int nl = t.IndexOf('\n');
            t = nl >= 0 ? t.Substring(0, nl) : t;
            return t.Length > 160 ? t.Substring(0, 160) + "…" : t;
        }

        public static bool SameContents(IDictionary<string, string> a, IDictionary<string, string> b)
        {
            if (a == null || b == null) return a == b;
            if (a.Count != b.Count) return false;
            foreach (KeyValuePair<string, string> kv in a)
            {
                string other;
                if (!b.TryGetValue(kv.Key, out other)) return false;
                if (AgentChangeSet.Normalize(other) != AgentChangeSet.Normalize(kv.Value)) return false;
            }
            return true;
        }

        private static int CountChanged(IDictionary<string, string> now, IDictionary<string, string> before)
        {
            int n = 0;
            foreach (KeyValuePair<string, string> kv in now)
            {
                string b;
                if (!before.TryGetValue(kv.Key, out b) || AgentChangeSet.Normalize(b) != AgentChangeSet.Normalize(kv.Value)) n++;
            }
            return n;
        }

        /// <summary>The repair instruction: diagnostics, output tail and the staged versions of the files involved.</summary>
        public static string BuildRepairPrompt(string instruction, VerificationResult v, IDictionary<string, string> staged, string workspaceRoot)
        {
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("Your proposed changes are staged (not applied yet). They were verified in a temporary copy of the workspace by running `" +
                          (v != null ? v.CommandLine : "") + "`, and verification FAILED (" + (v != null ? v.Badge : "") + ").");
            if (v != null && v.Errors.Count > 0)
            {
                sb.AppendLine("<verification_errors>");
                foreach (string l in v.FormatErrorLines(25)) sb.AppendLine(l);
                sb.AppendLine("</verification_errors>");
            }
            if (v != null && !string.IsNullOrEmpty(v.OutputTail))
            {
                string tail = VerificationResult.TailLines(v.OutputTail, 40);
                if (tail.Length > 3000) tail = tail.Substring(tail.Length - 3000);
                sb.AppendLine("<verification_output_tail>");
                sb.AppendLine(tail);
                sb.AppendLine("</verification_output_tail>");
            }

            // Files to show: staged files mentioned by errors first, then the rest (bounded).
            List<string> order = new List<string>();
            if (staged != null)
            {
                HashSet<string> mentioned = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                if (v != null)
                    foreach (DiagnosticItem d in v.Errors)
                    {
                        string rel = (d.RelativePath ?? "").Replace('\\', '/');
                        if (rel.Length > 0) mentioned.Add(rel);
                    }
                foreach (string k in staged.Keys)
                    if (mentioned.Contains(LlmHarnessAndAgentEngine.GetRelativePath(workspaceRoot, k).Replace('\\', '/'))) order.Add(k);
                foreach (string k in staged.Keys) if (!order.Contains(k)) order.Add(k);
            }
            int budget = 14000;
            if (order.Count > 0)
            {
                sb.AppendLine("<staged_files>");
                foreach (string k in order)
                {
                    if (budget <= 0) break;
                    string rel = LlmHarnessAndAgentEngine.GetRelativePath(workspaceRoot, k).Replace('\\', '/');
                    string[] lines = AgentChangeSet.Normalize(staged[k]).Split('\n');
                    StringBuilder fb = new StringBuilder();
                    int max = Math.Min(lines.Length, 400);
                    for (int i = 0; i < max; i++) fb.Append(i + 1).Append(": ").AppendLine(lines[i]);
                    if (lines.Length > max) fb.AppendLine("... (" + (lines.Length - max) + " more lines)");
                    string block = fb.ToString();
                    if (block.Length > budget) block = block.Substring(0, budget) + "\n... (truncated)";
                    budget -= block.Length;
                    sb.AppendLine("### File: " + rel);
                    sb.Append(block);
                }
                sb.AppendLine("</staged_files>");
            }
            sb.AppendLine("Fix the failure with SEARCH/REPLACE blocks (### File: path) whose SEARCH text matches the staged versions above exactly (without the line-number prefixes).");
            sb.AppendLine("Keep the intended change; do not revert it and do not touch unrelated code.");
            sb.AppendLine("Original request: " + (instruction ?? ""));
            return sb.ToString();
        }
    }
}
