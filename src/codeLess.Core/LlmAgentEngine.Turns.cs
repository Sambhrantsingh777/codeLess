using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace codeLess.Studio
{
    /// <summary>
    /// One agent turn per engine (QA #24). A turn owns the engine's per-turn state — the Stop flag
    /// (<see cref="CancelStreamingRequested"/>), the streaming handler (<see cref="OnStreamingChunk"/>) and the
    /// checkpoint hook (<see cref="OnBeforeRunTerminal"/>) — so a second turn on the same engine is refused with a
    /// clear reply instead of resetting the first turn's Stop, mixing its streamed text or stealing its checkpoint.
    /// Concurrent turns use separate engines (<see cref="CreateIsolatedCopy"/>, e.g. Best-of-N candidates).
    /// One-shot calls outside a turn (inline AI, commit messages, …) never raise the turn's streaming events and are
    /// not cancelled by its Stop. Strict C# 5.
    /// </summary>
    public partial class LlmHarnessAndAgentEngine
    {
        public const string BusyReply = "Another agent request is still running in this chat. Wait for it to finish (or press Stop), then send again.";
        public const string BusyProvider = "Busy";

        private int turnGate;
        private static readonly AsyncLocal<LlmHarnessAndAgentEngine> currentTurnEngine = new AsyncLocal<LlmHarnessAndAgentEngine>();

        /// <summary>True while an agent turn runs on this engine.</summary>
        public bool IsTurnRunning { get { return Volatile.Read(ref turnGate) != 0; } }

        /// <summary>True when the calling code runs inside a turn of this engine.</summary>
        private bool InTurnOfThisEngine { get { return ReferenceEquals(currentTurnEngine.Value, this); } }

        public static bool IsBusyResult(AgentExecutionTurnResult r)
        {
            return r != null && r.ProviderUsed == BusyProvider;
        }

        /// <summary>Runs one agent turn (refused with <see cref="BusyReply"/> while another turn runs on this engine).</summary>
        public async Task<AgentExecutionTurnResult> RunAutonomousAgentLoopAsync(
            string mode,
            string userInstruction,
            ByokVendor preferredVendor,
            string preferredHarnessId,
            EditorTabBuffer activeTab,
            string workspaceRoot,
            List<string> workspaceFiles,
            List<string> contextPills,
            string recentTerminalOutput = "",
            Dictionary<string, string> baseStagedFiles = null)
        {
            if (Interlocked.CompareExchange(ref turnGate, 1, 0) != 0)
            {
                AgentExecutionTurnResult busy = new AgentExecutionTurnResult();
                busy.AssistantReply = BusyReply;
                busy.ProviderUsed = BusyProvider;
                return busy;
            }
            try
            {
                currentTurnEngine.Value = this; // flows into this turn's provider calls and tools only
                CancelStreamingRequested = false;
                return await RunAutonomousAgentLoopCoreAsync(mode, userInstruction, preferredVendor, preferredHarnessId, activeTab, workspaceRoot,
                    workspaceFiles, contextPills, recentTerminalOutput, baseStagedFiles).ConfigureAwait(false);
            }
            finally
            {
                Interlocked.Exchange(ref turnGate, 0);
            }
        }

        /// <summary>A copy of the trace history that is safe to enumerate while a turn records into it.</summary>
        public List<HarnessTraceEntry> GetTraceHistorySnapshot()
        {
            lock (TraceHistory) return new List<HarnessTraceEntry>(TraceHistory);
        }
    }
}
