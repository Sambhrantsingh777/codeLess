# Harness Spec Audit (M1-M6)

| Feature | Status | File:Line | Test Name |
|---------|--------|-----------|-----------|
| M1: Simple replacer | Done | src/codeLess.Core/EditReplacers.cs:1 | `EditReplacersTests.Simple_Works` |
| M1: LineTrimmed replacer | Done | src/codeLess.Core/EditReplacers.cs:1 | `EditReplacersTests.LineTrimmed_Works` |
| M1: BlockAnchor replacer | Done | src/codeLess.Core/EditReplacers.cs:1 | `EditReplacersTests.BlockAnchor_Works` |
| M1: WhitespaceNormalized replacer | Done | src/codeLess.Core/EditReplacers.cs:1 | `EditReplacersTests.WhitespaceNormalized_Works` |
| M1: IndentationFlexible replacer | Done | src/codeLess.Core/EditReplacers.cs:1 | `EditReplacersTests.IndentationFlexible_Works` |
| M1: EscapeNormalized replacer | Done | src/codeLess.Core/EditReplacers.cs:1 | `EditReplacersTests.EscapeNormalized_Works` |
| M1: TrimmedBoundary replacer | Done | src/codeLess.Core/EditReplacers.cs:1 | `EditReplacersTests.TrimmedBoundary_Works` |
| M1: ContextAware replacer | Done | src/codeLess.Core/EditReplacers.cs:1 | `EditReplacersTests.ContextAware_Works` |
| M1: MultiOccurrence replacer | Done | src/codeLess.Core/EditReplacers.cs:1 | `EditReplacersTests.MultiOccurrence_Works` |
| M1: ambiguity error, not-found error, same-strings error | Done | src/codeLess.Core/EditReplacers.cs:1 | `EditReplacersTests.Errors_Triggered` |
| M1: replaceAll | Done | src/codeLess.Core/EditReplacers.cs:1 | `EditReplacersTests.ReplaceAll_Works` |
| M1: CRLF preserved | Done | src/codeLess.Core/EditReplacers.cs:1 | `EditReplacersTests.Crlf_Preserved` |
| M1: BlockAnchor picks best of two candidates | Done | src/codeLess.Core/EditReplacers.cs:1 | `EditReplacersTests.BlockAnchor_BestCandidate` |
| M1: tab vs space indentation | Done | src/codeLess.Core/EditReplacers.cs:1 | `EditReplacersTests.Indentation_TabVsSpace` |
| M1: escaped \n in model output | Done | src/codeLess.Core/EditReplacers.cs:1 | `EditReplacersTests.EscapedNewline_Works` |
| M1: multiedit atomic rollback | Done | src/LlmHarnessAndAgentEngine.cs:3000 | `AgentRobustnessM4Tests.MultiEdit_Rollback` |
| M2: read format | Done | src/LlmHarnessAndAgentEngine.cs:3052 | `AgentContextM5Tests.ReadFormat_Test` |
| M2: footer | Done | src/LlmHarnessAndAgentEngine.cs:3052 | `AgentContextM5Tests.ReadFormat_Footer` |
| M2: line cap | Done | src/LlmHarnessAndAgentEngine.cs:3052 | `AgentContextM5Tests.ReadFormat_LineCap` |
| M2: offset/limit math | Done | src/LlmHarnessAndAgentEngine.cs:3052 | `AgentContextM5Tests.ReadFormat_OffsetLimit` |
| M2: did-you-mean | Done | src/LlmHarnessAndAgentEngine.cs:3022 | `M2Tests.MissingFileSuggestions_Works` |
| M2: binary detection | Done | src/LlmHarnessAndAgentEngine.cs:3044 | `M2Tests.BinaryDetector_Works` |
| M2: read-before-edit | Done | src/LlmHarnessAndAgentEngine.cs:3000 | `AgentContextM5Tests.ReadBeforeEdit_Enforced` |
| M2: modified-since-read | Done | src/LlmHarnessAndAgentEngine.cs:3000 | `AgentContextM5Tests.ModifiedSinceRead_Enforced` |
| M2: staged edits counting as reads | Done | src/LlmHarnessAndAgentEngine.cs:3000 | `AgentContextM5Tests.StagedEditsCountAsReads` |
| M3: glob semantics and truncation | Done | src/LlmHarnessAndAgentEngine.cs:3000 | `M3Tests.GlobMatcher_Works` |
| M3: grep output format and the 100 limit | Done | src/LlmHarnessAndAgentEngine.cs:3000 | `AgentContextM5Tests.GrepOutput_FormatAndLimit` |
| M3: list limit | Done | src/LlmHarnessAndAgentEngine.cs:3000 | `AgentContextM5Tests.ListLimit_Enforced` |
| M3: terminal 30k head+tail truncation | Done | src/LlmHarnessAndAgentEngine.cs:3000 | `M3Tests.Terminal_OutputTruncation` |
| M4: tool-name repair (case, alias, invalid) | Done | src/LlmHarnessAndAgentEngine.cs:3000 | `AgentRobustnessM4Tests.ToolName_Repair` |
| M4: invalid JSON feedback | Done | src/LlmHarnessAndAgentEngine.cs:3000 | `AgentRobustnessM4Tests.InvalidJson_Feedback` |
| M4: doom loop with and without approval | Done | src/LlmHarnessAndAgentEngine.cs:3000 | `AgentRobustnessM4Tests.DoomLoop_Detected` |
| M4: retry schedule with retry-after-ms / seconds / date / cap / 5 attempts / cancellation | Done | src/codeLess.Core/ApiRetryHandler.cs:1 | `AgentRobustnessM4Tests.RetrySchedule_Works` |
| M5: instruction discovery (16KB) | Done | src/codeLess.Core/AgentContextBuilder.cs:1 | `AgentContextM5Tests.DiscoverInstructions_DedupesAndCaps` |
| M5: env block | Done | src/codeLess.Core/AgentContextBuilder.cs:1 | `AgentContextM5Tests.BuildEnvBlock_ContainsExpectedFields` |
| M5: per-family prompts | Done | src/codeLess.Core/AgentContextBuilder.cs:1 | `AgentContextM5Tests.GetProviderPrompt_SelectsFamily` |
| M5: pruning | Done | src/codeLess.Core/AgentContextBuilder.cs:1 | `AgentContextM5Tests.PruneHistory_ReplacesOldResults` |
| M5: compaction | Done | src/LlmHarnessAndAgentEngine.cs:1 | `AgentContextM5Tests.Compaction_TriggersOnThreshold` |
| M6: Plan/Build mode restrictions | Done | src/LlmHarnessAndAgentEngine.cs:1 | `M6EnginePermissionsTests.Engine_PlanMode_WriteFile_Denied_NothingStaged` |
| M6: Plan mode read-only bash | Done | src/LlmHarnessAndAgentEngine.cs:1 | `M6EnginePermissionsTests.Engine_PlanMode_GitStatus_RunsWithoutApproval` |
| M6: Plan mode non-read-only bash | Done | src/LlmHarnessAndAgentEngine.cs:1 | `M6EnginePermissionsTests.Engine_PlanMode_NpmInstall_Asks` |
| M6: Bash wildcards | Done | src/LlmHarnessAndAgentEngine.cs:1 | `M6EnginePermissionsTests.Engine_BashPattern_Deny_IsDenied` |
| M6: Restricted Mode stricter wins | Done | src/LlmHarnessAndAgentEngine.cs:1 | `M6EnginePermissionsTests.Engine_RestrictedMode_MakesAllowIntoAsk` |
| M6: todowrite / todoread tools | Done | src/LlmHarnessAndAgentEngine.cs:1 | `M6EnginePermissionsTests.Todos_Format_RoundTrip_And_Event` |
| M6: Todos validation errors | Done | src/LlmHarnessAndAgentEngine.cs:1 | `M6EnginePermissionsTests.Todos_Format_ValidationErrors` |
