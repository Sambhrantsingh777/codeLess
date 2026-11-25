# codeLess Agent Harness — opencode Architecture & Reference

## 1. Attribution & License Notice

Portions of the codeLess autonomous agent harness are derived from **[opencode](https://github.com/sst/opencode)** (`packages/opencode/src/`), created by Anomaly Innovations / SST and released under the **MIT License**:

```text
MIT License
Copyright (c) 2025 Anomaly Innovations (sst/opencode)

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.
```

All ported components in `src/codeLess.Core/` and `src/` are written in strict C# 5 targeting both `.NET Framework 4.8` (WinForms) and `.NET 8.0` (Avalonia) with zero external NuGet dependencies in the core harness.

---

## 2. File & Function Mapping (`sst/opencode` → `codeLess`)

| Milestone | `sst/opencode` Source | `codeLess` File | Key Types & Methods |
|-----------|----------------------|-----------------|---------------------|
| **M1: 9-Stage Fuzzy Edit Replacer** | `src/tool/edit.ts` (`replace`, 9 `Replacer` generators, `levenshtein`) | `src/codeLess.Core/EditReplacers.cs` | `EditReplacers.ApplyReplace`, `SimpleReplacer`, `LineTrimmedReplacer`, `BlockAnchorReplacer`, `WhitespaceNormalizedReplacer`, `IndentationFlexibleReplacer`, `EscapeNormalizedReplacer`, `TrimmedBoundaryReplacer`, `ContextAwareReplacer`, `MultiOccurrenceReplacer`, `Levenshtein` |
| **M1: Atomic Multi-Edit** | `src/tool/multiedit.ts` | `src/LlmHarnessAndAgentEngine.cs` | `ExecuteWorkspaceTool` (`toolName == "multiedit"`) — applies sequential edits on an in-memory snapshot and rolls back atomically if any edit fails |
| **M2: Read Safety & File Time Guard** | `src/file/time.ts` (`FileTime.read`, `FileTime.assert`) | `src/codeLess.Core/FileTimeTracker.cs` | `FileTimeTracker.RecordRead`, `FileTimeTracker.RecordStagedWrite`, `FileTimeTracker.AssertCanModify` |
| **M2: Binary File Detection** | `src/tool/read.ts` (`isBinaryFile`) | `src/codeLess.Core/BinaryDetector.cs` | `BinaryDetector.IsBinaryFile` (extension blocklist + first 4096-byte NUL/non-printable ratio > 30%) |
| **M2: Missing File Suggestions** | `src/tool/read.ts` | `src/codeLess.Core/MissingFileSuggestions.cs` | `MissingFileSuggestions.GetSuggestions` (up to 3 case-insensitive substring/Levenshtein matches in parent directory) |
| **M3: Search & Bash Truncation** | `src/tool/glob.ts`, `src/tool/grep.ts`, `src/tool/ls.ts`, `src/tool/bash.ts` | `src/LlmHarnessAndAgentEngine.cs` | `ExecuteWorkspaceTool` (`glob` 100-entry cap + mtime sort, `grep` 100-match cap + 2000-char line cap, `list_directory` tree + 100 cap, `run_terminal` 30,000-char head+tail truncation) |
| **M4: Tool-Name Repair & Doom-Loop Detection** | `src/session/prompt.ts` (`repairToolCall`, `DOOM_LOOP_THRESHOLD = 3`) | `src/LlmHarnessAndAgentEngine.cs` | `ParseToolCalls` (malformed `<tool_call>` JSON feedback), `ExecuteWorkspaceTool` (case-insensitive tool name repair + aliases), `RunAutonomousAgentLoopCoreAsync` (`doomLoopHistory` 3x identical call/args/output check) |
| **M4: Exponential Retry & `retry-after`** | `src/session/retry.ts` | `src/codeLess.Core/ApiRetryHandler.cs` | `ApiRetryHandler.ExecuteWithRetriesAsync`, `ComputeDelayMs` (`retry-after-ms`, `retry-after` seconds/HTTP-date, `2000 * 2^(attempt-1)` capped at 30s, max 5 attempts) |
| **M5: Context, Rules Discovery & Compaction** | `src/session/instruction.ts`, `src/session/prompt.ts`, `src/session/compaction.ts`, `src/prompt/*.txt` | `src/codeLess.Core/AgentContextBuilder.cs`, `src/LlmHarnessAndAgentEngine.cs` | `AgentContextBuilder.DiscoverInstructions` (`.codelessrules`, `AGENTS.md`, `CLAUDE.md`, `CONTEXT.md`, 16 KB cap), `BuildEnvBlock`, `GetProviderPrompt`, `PruneHistory`, `RunAutonomousAgentLoopCoreAsync` (compaction at `ContextWindow - MaxOutputTokens`) |
| **M6: Plan / Build Modes, Permissions & Todos** | `src/agent/agent.ts`, `src/permission/index.ts`, `src/tool/todo.ts` | `src/codeLess.Core/AgentPermissions.cs`, `src/codeLess.Core/AgentUiParity.cs`, `src/LlmHarnessAndAgentEngine.cs` | `AgentPermissions.EvaluateTool`, `AgentUiParity`, `todowrite` / `todoread`, `OnTodosChanged` |
| **M7b: Post-Edit Diagnostics Loop** | `src/tool/edit.ts`, `src/tool/write.ts`, `src/lsp/index.ts` | `src/codeLess.Core/IDiagnosticsProvider.cs`, `src/codeLess.Core/LspDiagnosticsProvider.cs`, `src/LlmHarnessAndAgentEngine.cs` | `IDiagnosticsProvider.GetDiagnosticsAsync`, `LspDiagnosticsProvider`, post-edit `<file_diagnostics>` injection (up to 20 errors, 1500ms timeout) |
| **M7c: Native Function / Tool Calling** | `src/session/llm.ts`, `src/session/message-v2.ts` | `src/codeLess.Core/NativeToolCalling.cs`, `src/LlmHarnessAndAgentEngine.cs` | `NativeToolCalling.BuildOpenAiToolsPayload`, `BuildAnthropicToolsPayload`, `OpenAiToolCallAccumulator`, `AnthropicToolCallAccumulator`, `SerialiseOpenAiHistory`, `SerialiseAnthropicHistory`, automatic HTTP 400 text-mode fallback per `vendor:model` |
| **M7d: `webfetch` Tool** | `src/tool/webfetch.ts` | `src/codeLess.Core/WebFetchTool.cs`, `src/codeLess.Core/NetworkLedgerHttp.cs` | `WebFetchTool.Execute`, `ConvertHtmlToText`, `ConvertHtmlToMarkdown`, `LedgerHttp.Send` |
| **M7e: `task` Subagent Tool** | `src/tool/task.ts` | `src/LlmHarnessAndAgentEngine.cs` | `ExecuteWorkspaceTool` (`toolName == "task"`), `SubagentDepth` (max depth 1), `GetAvailableToolsForCurrentDepth` (read-only tools only, nested `task` refused) |
| **M7f: WinForms & Avalonia UI Parity** | `packages/opencode/src/cli/cmd/tui/` | `src/codeLess.Core/AgentUiParity.cs`, `src/CodeLessMainForm.cs`, `src/CoreModelsAndThemes.cs`, `src/codeLess.Avalonia/MainWindow.cs` | Build/Plan mode dropdown & `Tab` key cycling, `AgentTodos` task list rendering (`☐`, `◐`, `☑`, `☒` with strikethrough) |
| **M9a: MCP Servers (Local Stdio)** | `src/mcp/index.ts` | `src/codeLess.Core/McpStdioClient.cs`, `src/codeLess.Core/AgentPermissions.cs`, `src/LlmHarnessAndAgentEngine.cs` | `McpConfigLoader.LoadMergedConfig` (`codeless.mcp`), `McpStdioClient` (`initialize`, `notifications/initialized`, `tools/list`, `tools/call`), `McpServerManager` (lazy start, `<server>_<tool>` exposure, `StopAll` on close/workspace change) |

---

## 3. What Differs from `sst/opencode` and Why

1. **Staged Diffs Never Write Directly to Disk (`StagedFileContents`)**:
   - In `opencode` (a CLI agent), `edit`, `write`, and `multiedit` write directly to disk.
   - In `codeLess` (an interactive IDE), `write_file`, `edit_file`, `multiedit`, and `<<<<<<< SEARCH ... >>>>>>> REPLACE` blocks stage their changes in `AgentExecutionTurnResult.StagedFileContents` and render inline diff hunks in the editor (`Accept` / `Reject` per hunk or changeset).
   - `read_file`, `edit_file`, `multiedit`, `grep`, and `FileTimeTracker` read through `StagedFileContents` first so subsequent tool calls in the same turn observe previously staged edits seamlessly.
   - Subagents (`task`) run against a throwaway staging dictionary that is cleared on completion so a subagent can never stage or mutate files.
2. **Interactive Approval Gate (`OnRequestToolApproval`)**:
   - When `AgentPermissions.EvaluateTool` returns `"ask"`, the engine invokes `OnRequestToolApproval(toolName, detailJson)` to prompt the user in the IDE before running the tool.
   - Doom-loop detection (3 identical consecutive tool calls with identical output) also routes through `OnRequestToolApproval("doom_loop", ...)` so the user can either stop the loop or explicitly allow continuation.
3. **Workspace Trust & Restricted Mode**:
   - `IdePersistentSettings.IsWorkspaceTrusted(workspaceRoot)` checks whether the current workspace has been trusted by the user.
   - In an untrusted workspace (`restricted: true`), `AgentPermissions.EvaluateTool` upgrades every `"allow"` permission to `"ask"`, ensuring no tool executes silently in untrusted repositories (`stricter wins` between `"allow" < "ask" < "deny"`).
4. **Network Privacy Ledger & Offline Mode (`NetworkLedger` / `LedgerHttp`)**:
   - Every outbound HTTP request made by the harness (LLM provider calls and `webfetch`) is routed through `NetworkLedger` / `LedgerHttp.Send`.
   - When `NetworkLedger.OfflineMode` is enabled, all non-loopback requests are blocked before any socket connection is opened and recorded as `BlockedOffline` in the Network Activity ledger; local loopback models (`127.0.0.1`, `localhost`, `[::1]`, e.g. Ollama) continue to work.
   - `webfetch` additionally sets `AllowCrossHostRedirects = false` so a redirect cannot jump across hosts without being re-evaluated.

---

## 4. Tool Reference Table

| Tool Name | Aliases | Read-Only | Plan Mode | Parameters | Description |
|-----------|---------|-----------|-----------|------------|-------------|
| `read_file` | `read` | Yes | Allowed | `path` (string, req), `offset` (int, opt, 1-indexed), `limit` (int, opt, default 2000) | Reads a workspace file (or staged buffer) with `00001\| ` line numbers, 2000-char line cap, 50 KB byte cap, binary file guard, and "Did you mean?" suggestions. |
| `write_file` | `write` | No | **Denied** | `path` (string, req), `content` (string, req) | Stages a new file or full-file replacement in `StagedFileContents`. Requires prior `read_file` if overwriting an existing disk file. |
| `edit_file` | `edit`, `patch` | No | **Denied** | `path` (string, req), `oldString` (string, req), `newString` (string, req), `replaceAll` (bool, opt) | Applies a surgical replacement using the 9-stage `EditReplacers` cascade and stages the result. Enforces read-before-edit and mtime stability via `FileTimeTracker`. |
| `multiedit` | — | No | **Denied** | `path` (string, req), `edits` (array of `{oldString, newString, replaceAll?}`, req) | Applies multiple `EditReplacers` edits sequentially to a single file; rolls back atomically if any edit fails. |
| `list_directory` | `ls`, `list` | Yes | Allowed | `path` (string, opt), `ignore` (array of globs, opt) | Lists workspace directory contents as an indented tree, skipping default ignored directories (`node_modules`, `.git`, `bin`, `obj`, etc.), capped at 100 entries. |
| `glob` | — | Yes | Allowed | `pattern` (string, req), `path` (string, opt) | Matches workspace files by glob (`*`, `**`, `?`, `{a,b}`), sorted newest-first by mtime, capped at 100 files. |
| `grep` | `search_workspace`, `search`, `ripgrep`, `rg` | Yes | Allowed | `pattern` (string, req, regex), `path` (string, opt), `include` (string, opt, glob) | Searches workspace files (and staged content) by regex, grouped by file (`Line N: text`), sorted newest-first, capped at 100 matches. |
| `run_terminal` | `run_command`, `bash`, `sh`, `cmd`, `shell` | No | Read-only allowlist (`ls`, `cat`, `head`, `tail`, `rg`, `grep`, `find`, `git status/diff/log/show`, `pwd`, `wc`, `tree`); others `"ask"` | `command` (string, req), `workdir` (string, opt), `timeout` (int, opt, ms, max 120000), `description` (string, opt) | Executes a shell command in the workspace and captures stdout/stderr with 30,000-character head+tail truncation. |
| `todowrite` | — | No | Allowed | `todos` (array of `{id, content, status: pending\|in_progress\|completed\|cancelled, priority: high\|medium\|low}`, req) | Replaces the session's structured task list (`AgentTodos`) and fires `OnTodosChanged` to update the UI. |
| `todoread` | — | Yes | Allowed | *(none)* | Returns the current `AgentTodos` JSON array. |
| `webfetch` | — | Yes | `"ask"` (default) | `url` (string, req, `http://` or `https://`), `format` (`markdown`\|`text`\|`html`, default `markdown`), `timeout` (int, opt, seconds, max 120) | Fetches a URL via `LedgerHttp.Send` / `NetworkLedger` (honouring Offline Mode, 5 MB cap, no cross-host redirects) and converts HTML to Markdown or plain text. |
| `task` | — | Yes | Allowed | `description` (string, req), `prompt` (string, req) | Runs a depth-1 read-only child `LlmHarnessAndAgentEngine` subagent (max 8 iterations, no write/edit/bash/nested-task tools, no file staging) and returns its final answer. |

---

## 5. Settings Reference

All agent harness settings persist in `IdePersistentSettings` (`settings.json`):

| Setting Key (`settings.json`) | C# Property (`IdePersistentSettings`) | Default | Description |
|-------------------------------|---------------------------------------|---------|-------------|
| `codeless.agent.nativeToolCalling` | `NativeToolCalling` | `true` | Enables native provider tool-calling payloads (`tools` array + `tool_choice: "auto"`, SSE `tool_calls` / `tool_use` delta accumulation, structured `tool_result` history). Automatically falls back to `<tool_call>` text mode per `vendor:model` on HTTP 400 tool-unsupported errors. |
| `RequireAgentToolApproval` | `RequireAgentToolApproval` | `true` | Global base permission level: when `true`, tools default to `"ask"` (prompting via `OnRequestToolApproval`); when `false`, tools default to `"allow"` (subject to per-tool overrides and Workspace Trust). |
| `agent.permission.edit` | `AgentPermissionEdit` | `"allow"` | Permission override (`"allow"`, `"ask"`, or `"deny"`) for `write_file`, `edit_file`, and `multiedit` in Build mode (always `"deny"` in Plan mode). |
| `agent.permission.webfetch` | `AgentPermissionWebfetch` | `"ask"` | Permission override (`"allow"`, `"ask"`, or `"deny"`) for `webfetch`. Defaults to `"ask"` so outbound documentation fetches require approval unless explicitly set to `"allow"`. |
| `agent.permission.bash` | `AgentPermissionBash` | `{}` | Dictionary of glob patterns (e.g. `{"git status*": "allow", "rm *": "deny"}`) matched by longest-pattern-wins against `run_terminal` commands, merged with `MergeStricter`. |
| `codeless.mcp` | `McpServers` | `{}` | Local stdio MCP server definitions (`{ "<name>": { "type": "local", "command": ["cmd", "arg"], "environment": {...}, "enabled": true } }`) merged across user `settings.json` and workspace `.vscode/settings.json` (workspace wins). |
