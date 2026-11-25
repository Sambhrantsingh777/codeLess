# Harness Spec Audit (M1-M5)

| Feature | Status | File:Line | Test Name |
|---------|--------|-----------|-----------|
| M1: EditReplacers port | Done | src/codeLess.Core/EditReplacers.cs:1 | `EditReplacersTests.*` |
| M2: read_file format | Done | src/LlmHarnessAndAgentEngine.cs:3052 | N/A |
| M2: Missing file suggestions | Done | src/LlmHarnessAndAgentEngine.cs:3022 | N/A |
| M2: Binary file detection | Done | src/LlmHarnessAndAgentEngine.cs:3044 | N/A |
| M2: FileTimeTracker logic | Done | src/LlmHarnessAndAgentEngine.cs:3040 | N/A |
| M3: glob (limits, format) | Incomplete | src/LlmHarnessAndAgentEngine.cs | N/A |
| M3: grep_search (limits, format) | Incomplete | src/LlmHarnessAndAgentEngine.cs:3203 | N/A |
| M3: list_dir (tree, limits) | Partial | src/LlmHarnessAndAgentEngine.cs:3087 | N/A |
| M3: run_terminal (timeout, 30k cut) | Done | src/LlmHarnessAndAgentEngine.cs | N/A |
| M4: Tool-name repair | Done | src/LlmHarnessAndAgentEngine.cs:3507 | N/A |
| M4: Doom loop detection | Incomplete | src/LlmHarnessAndAgentEngine.cs | N/A |
| M4: Retry schedules (HTTP codes) | Incomplete | src/codeLess.Core/ApiRetryHandler.cs | N/A |
| M5: Instruction discovery (16KB) | Done | src/codeLess.Core/AgentContextBuilder.cs:13 | `DiscoverInstructions_DedupesAndCaps` |
| M5: Env block | Done | src/codeLess.Core/AgentContextBuilder.cs:64 | `BuildEnvBlock_ContainsExpectedFields` |
| M5: Per-family prompts | Done | src/codeLess.Core/AgentContextBuilder.cs:91 | `GetProviderPrompt_SelectsFamily` |
| M5: Pruning | Done | src/codeLess.Core/AgentContextBuilder.cs:113 | `PruneHistory_ReplacesOldResults` |
| M5: Compaction | Done | src/LlmHarnessAndAgentEngine.cs:2744 | `Compaction_TriggersOnThreshold` |

