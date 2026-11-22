using System;
using System.Collections.Generic;

namespace codeLess.Studio
{
    /// <summary>Agent-trust hooks of the engine (verification gate, Best-of-N, per-turn checkpoints). Strict C# 5.</summary>
    public partial class LlmHarnessAndAgentEngine
    {
        /// <summary>When true, run_terminal is refused (Best-of-N candidates must not touch the real workspace).</summary>
        public bool DisableTerminalTool;

        /// <summary>Called on the agent thread after a terminal command was approved and before it runs.</summary>
        public Action<string> OnBeforeRunTerminal;

        /// <summary>
        /// A copy that shares the read-only workspace index (BM25 chunks, symbols, repo map, rules) and providers but has
        /// its own cancel flag, streaming handler and trace history, so several turns can run concurrently
        /// (Best-of-N) without mixing streamed text or racing on shared state.
        /// </summary>
        public LlmHarnessAndAgentEngine CreateIsolatedCopy()
        {
            LlmHarnessAndAgentEngine c = new LlmHarnessAndAgentEngine(null);
            c.Vendors = Vendors;
            c.Harnesses = Harnesses;
            c.IndexedChunks = IndexedChunks;
            c.WorkspaceSymbols = WorkspaceSymbols;
            c.WorkspaceSymbolMap = WorkspaceSymbolMap;
            c.docFreq = docFreq;
            c.avgChunkTokens = avgChunkTokens;
            c.ProjectRulesText = ProjectRulesText;
            c.CompactRepoMap = CompactRepoMap;
            c.LocalOllamaDetected = LocalOllamaDetected;
            c.ActiveGitBranch = ActiveGitBranch;
            c.EnableSseStreaming = EnableSseStreaming;
            c.CustomModelResponder = CustomModelResponder;
            c.OnRequestToolApproval = OnRequestToolApproval;
            c.MaxAgentIterations = MaxAgentIterations;
            c.AgentLoopTimeoutMs = AgentLoopTimeoutMs;
            c.HttpTimeoutMs = HttpTimeoutMs;
            c.DisableTerminalTool = true;
            return c;
        }

        /// <summary>A copy of <paramref name="v"/> (so a candidate can use another model without changing the user's selection).</summary>
        public static ByokVendor CloneVendor(ByokVendor v)
        {
            if (v == null) return null;
            ByokVendor c = new ByokVendor();
            c.Id = v.Id;
            c.Name = v.Name;
            c.Badge = v.Badge;
            c.BaseUrl = v.BaseUrl;
            c.ApiKey = v.ApiKey;
            c.SelectedModel = v.SelectedModel;
            c.DefaultHarnessId = v.DefaultHarnessId;
            c.Models = v.Models;
            c.EnvVarName = v.EnvVarName;
            return c;
        }
    }
}
