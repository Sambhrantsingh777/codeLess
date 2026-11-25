# Harness Spec Audit (M1-M8)

| Feature | Status | File:Line | Test Name |
|---------|--------|-----------|-----------|
| M1: Simple replacer | Done | src/codeLess.Core/EditReplacers.cs:33 | `EditReplacersTests.M1_Simple_Works` |
| M1: LineTrimmed replacer | Done | src/codeLess.Core/EditReplacers.cs:41 | `EditReplacersTests.M1_LineTrimmed_Works` |
| M1: BlockAnchor replacer | Done | src/codeLess.Core/EditReplacers.cs:51 | `EditReplacersTests.M1_BlockAnchor_PicksBest` |
| M1: WhitespaceNormalized replacer | Done | src/codeLess.Core/EditReplacers.cs:98 | `EditReplacersTests.M1_WhitespaceNormalized_Works` |
| M1: IndentationFlexible replacer | Done | src/codeLess.Core/EditReplacers.cs:116 | `EditReplacersTests.M1_IndentationFlexible_Works` |
| M1: EscapeNormalized replacer | Done | src/codeLess.Core/EditReplacers.cs:132 | `EditReplacersTests.M1_EscapeNormalized_Works` |
| M1: TrimmedBoundary replacer | Done | src/codeLess.Core/EditReplacers.cs:146 | `EditReplacersTests.M1_TrimmedBoundary_Works` |
| M1: ContextAware replacer | Done | src/codeLess.Core/EditReplacers.cs:157 | `EditReplacersTests.M1_ContextAware_Works` |
| M1: MultiOccurrence replacer | Done | src/codeLess.Core/EditReplacers.cs:176 | `EditReplacersTests.M1_Simple_Multi_ReplaceAll` |
| M1: ambiguity error, not-found error, same-strings error | Done | src/codeLess.Core/EditReplacers.cs:192 | `EditReplacersTests.M1_Ambiguous_Throws` |
| M1: replaceAll | Done | src/codeLess.Core/EditReplacers.cs:192 | `EditReplacersTests.M1_ReplaceAll_Works` |
| M1: CRLF preserved | Done | src/codeLess.Core/EditReplacers.cs:192 | `EditReplacersTests.M1_CRLF_Preserved` |
| M1: BlockAnchor picks best of two candidates | Done | src/codeLess.Core/EditReplacers.cs:51 | `EditReplacersTests.M1_BlockAnchor_PicksBest` |
| M1: tab vs space indentation | Done | src/codeLess.Core/EditReplacers.cs:116 | `EditReplacersTests.M1_IndentationFlexible_Works` |
| M1: escaped \n in model output | Done | src/codeLess.Core/EditReplacers.cs:132 | `EditReplacersTests.M1_EscapeNormalized_Works` |
| M1: multiedit atomic rollback | Done | src/LlmHarnessAndAgentEngine.cs:3377 | `EditReplacersIntegrationTests.Multiedit_AtomicRollback` |
| M2: read format | Done | src/LlmHarnessAndAgentEngine.cs:3052 | `M2M3M4MissingTests.ReadFormat_Test` |
| M2: footer | Done | src/LlmHarnessAndAgentEngine.cs:3052 | `M2M3M4MissingTests.ReadFormat_Footer` |
| M2: line cap | Done | src/LlmHarnessAndAgentEngine.cs:3052 | `M2M3M4MissingTests.ReadFormat_LineCap` |
| M2: offset/limit math | Done | src/LlmHarnessAndAgentEngine.cs:3052 | `M2M3M4MissingTests.ReadFormat_OffsetLimit` |
| M2: did-you-mean | Done | src/LlmHarnessAndAgentEngine.cs:3022 | `M2M3M4MissingTests.DidYouMean_Works` |
| M2: binary detection | Done | src/LlmHarnessAndAgentEngine.cs:3044 | `M2M3M4MissingTests.BinaryDetection_Works` |
| M2: read-before-edit | Done | src/LlmHarnessAndAgentEngine.cs:3423 | `M2M3M4MissingTests.ReadBeforeEdit_Enforced` |
| M2: modified-since-read | Done | src/LlmHarnessAndAgentEngine.cs:3429 | `M2M3M4MissingTests.ModifiedSinceRead_Enforced` |
| M2: staged edits counting as reads | Done | src/LlmHarnessAndAgentEngine.cs:3419 | `M2M3M4MissingTests.StagedEditsCountAsReads` |
| M3: glob semantics and truncation | Done | src/LlmHarnessAndAgentEngine.cs:3174 | `M2M3M4MissingTests.GlobTruncation_Works` |
| M3: grep output format and the 100 limit | Done | src/LlmHarnessAndAgentEngine.cs:3380 | `M2M3M4MissingTests.GrepOutput_FormatAndLimit` |
| M3: list limit | Done | src/LlmHarnessAndAgentEngine.cs:3197 | `M2M3M4MissingTests.ListLimit_Enforced` |
| M3: terminal 30k head+tail truncation | Done | src/LlmHarnessAndAgentEngine.cs:3300 | `M2M3M4MissingTests.TerminalTruncation_Works` |
| M4: tool-name repair (case, alias, invalid) | Done | src/LlmHarnessAndAgentEngine.cs:3005 | `AgentRobustnessM4Tests.ToolName_Repair_CaseInsensitive_And_Unknown` |
| M4: invalid JSON feedback | Done | src/LlmHarnessAndAgentEngine.cs:2961 | `AgentRobustnessM4Tests.ParseToolCalls_InvalidJson_SynthesizesError` |
| M4: doom loop with and without approval | Done | src/LlmHarnessAndAgentEngine.cs:2838 | `AgentRobustnessM4Tests.DoomLoop_TestsBothGrantedAndDenied` |
| M4: retry schedule with retry-after-ms / seconds / date / cap / 5 attempts / cancellation | Done | src/codeLess.Core/ApiRetryHandler.cs:10 | `ApiRetryHandlerTests.ApiRetry_BasicRetries`, `ApiRetryHandlerTests.ApiRetry_Cancellation_BeforeWait`, `ApiRetryHandlerTests.ApiRetry_Cancellation_DuringWait`, `ApiRetryHandlerTests.ApiRetry_Headers_Ms`, `ApiRetryHandlerTests.ApiRetry_Headers_Seconds`, `ApiRetryHandlerTests.ApiRetry_Headers_Date`, `ApiRetryHandlerTests.ApiRetry_Headers_DelayCap`, `ApiRetryHandlerTests.ApiRetry_Max5Attempts` |
| M5: instruction discovery (16KB) | Done | src/codeLess.Core/AgentContextBuilder.cs:10 | `AgentContextM5Tests.DiscoverInstructions_DedupesAndCaps` |
| M5: env block | Done | src/codeLess.Core/AgentContextBuilder.cs:10 | `AgentContextM5Tests.BuildEnvBlock_ContainsExpectedFields` |
| M5: per-family prompts | Done | src/codeLess.Core/AgentContextBuilder.cs:10 | `AgentContextM5Tests.GetProviderPrompt_SelectsFamily` |
| M5: pruning | Done | src/codeLess.Core/AgentContextBuilder.cs:10 | `AgentContextM5Tests.PruneHistory_ReplacesOldResults` |
| M5: compaction | Done | src/LlmHarnessAndAgentEngine.cs:141 | `AgentContextM5Tests.Compaction_TriggersOnThreshold` |
| M6: Plan/Build mode restrictions | Done | src/codeLess.Core/AgentPermissions.cs:15 | `M6EnginePermissionsTests.Engine_PlanMode_WriteFile_Denied_NothingStaged` |
| M6: Plan mode read-only bash | Done | src/codeLess.Core/AgentPermissions.cs:46 | `M6EnginePermissionsTests.Engine_PlanMode_GitStatus_RunsWithoutApproval` |
| M6: Plan mode non-read-only bash | Done | src/codeLess.Core/AgentPermissions.cs:57 | `M6EnginePermissionsTests.Engine_PlanMode_NpmInstall_Asks` |
| M6: Bash wildcards | Done | src/codeLess.Core/AgentPermissions.cs:60 | `M6EnginePermissionsTests.Engine_BashPattern_Deny_IsDenied` |
| M6: Restricted Mode stricter wins | Done | src/codeLess.Core/AgentPermissions.cs:13 | `M6EnginePermissionsTests.Engine_RestrictedMode_MakesAllowIntoAsk` |
| M6: todowrite / todoread tools | Done | src/LlmHarnessAndAgentEngine.cs:3111 | `M6EnginePermissionsTests.Todos_Format_RoundTrip_And_Event` |
| M6: Todos validation errors | Done | src/LlmHarnessAndAgentEngine.cs:3117 | `M6EnginePermissionsTests.Todos_Format_ValidationErrors` |
| Avalonia: Tab toggles Build/Plan | Done | src/codeLess.Avalonia/MainWindow.cs:1674 | `AvaloniaHeadlessTests.Avalonia_Tab_TogglesMode` |
| M6: Plan mode SEARCH/REPLACE block staging denied | Done | src/LlmHarnessAndAgentEngine.cs:3727 | `M6EnginePermissionsTests.Engine_PlanMode_SearchReplace_Denied_NothingStaged` |

| M7b: Diagnostics appended after edit | Done | src/LlmHarnessAndAgentEngine.cs:3140 | `DiagnosticsProviderM7bTests.Diagnostics_AppendsErrors_CapsAt20_IgnoresWarnings_AndDoesNotHang` |
| M7c-1: Native Tool Calling Core | Done | src/codeLess.Core/NativeToolCalling.cs:44 | `NativeToolCallingTests.ToolsTable_MatchesExecuteWorkspaceTool`, `NativeToolCallingTests.Builders_ReturnCorrectJsonShape`, `NativeToolCallingTests.OpenAiAccumulator_SingleCall_MixedText_SplitArgs`, `NativeToolCallingTests.AnthropicAccumulator_SingleCall_MixedText_SplitArgs`, `NativeToolCallingTests.HistorySerialisers_ShapeTests` |
| M7c-2: Engine Wiring (Native) | Done | src/LlmHarnessAndAgentEngine.cs:2303 | `NativeToolCallingEngineTests.Engine_Native_OpenAi_ExecutesAndMaintainsHistory`, `NativeToolCallingEngineTests.Engine_Native_Anthropic_ExecutesAndMaintainsHistory`, `NativeToolCallingEngineTests.Engine_Native_Fallback_On400`, `NativeToolCallingEngineTests.Engine_Native_SettingOff_NoToolsPayload`, `NativeToolCallingEngineTests.Engine_PlanMode_DeniesWriteFile` |
| M7d: webfetch tool (NetworkLedger, 5MB cap, HTML conversion, permissions) | Done | src/codeLess.Core/WebFetchTool.cs:21 | `WebFetchM7dTests.WebFetch_OfflineMode_BlockedWithStandardMessage_AndRecordedInLedger`, `WebFetchM7dTests.WebFetch_CapEnforced_RejectsOver5MB`, `WebFetchM7dTests.WebFetch_HtmlToText_And_HtmlToMarkdown_And_RecordsLedgerEntry`, `WebFetchM7dTests.WebFetch_PermissionAskByDefault_DeniedDoesNotFetch_AndAllowedInPlanModeWithApproval`, `WebFetchM7dTests.WebFetch_RefusesCrossHostRedirect_AndNonHttpScheme` |
| M7e: task subagent tool (read-only tools, depth 1, cancellation, no staging) | Done | src/LlmHarnessAndAgentEngine.cs:3619 | `TaskSubagentM7eTests.Task_ChildDeniedWriteFile_AndNothingIsStaged`, `TaskSubagentM7eTests.Task_NestedTask_IsRefused`, `TaskSubagentM7eTests.Task_StopDuringChild_CancelsPromptly`, `TaskSubagentM7eTests.Task_OutputComesBackToParent_AndReadOnlyToolsWork` |
| M7f: WinForms parity (Build/Plan toggle, Tab cycle, Todo list rendering) | Done | src/codeLess.Core/AgentUiParity.cs:18 | `WinFormsParityM7fTests.WinForms_BuildPlanMode_CyclesAndNormalizesCorrectly`, `WinFormsParityM7fTests.WinForms_TodoListFormatting_IconsAndStrikethrough_AndOnTodosChanged`, `WinFormsParityM7fTests.WinForms_MainFormAndFeedControl_WiredToBuildPlanToggleAndTodoRenderer` |
| M8: HARNESS_OPENCODE.md documentation and complete M1-M8 spec audit | Done | docs/HARNESS_OPENCODE.md:3 | `HarnessDocsAuditM8Tests.M8_OpencodeDoc_And_SpecAudit_AreCompleteAndValid` |
