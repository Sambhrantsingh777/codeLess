# OpenCode Harness Research & Adaptation

## What OpenCode Does
OpenCode (sst/opencode) provides an AI agent harness focusing on robust file manipulation and code intelligence. Key features include:
- **Rich Tool Set**: `read`, `write`, `edit` (with advanced replacer cascades), `apply_patch`, `bash` (sandboxed/timeout), `glob`, `grep`, `task`, `webfetch`.
- **Replacer Cascade**: When applying `edit` (`oldString` to `newString`), OpenCode uses a cascade of string matching heuristics (Simple, LineTrimmed, BlockAnchor, WhitespaceNormalized, IndentationFlexible, EscapeNormalized, TrimmedBoundary, ContextAware, MultiOccurrence) to reliably apply edits even if the LLM's `oldString` isn't a perfect character-for-character match.
- **Safety & Permissions**: Doom-loop detection, read-before-write guarantees (via modified-since-read FileTime tracking), and fine-grained permissions per command.
- **Context Management**: Context compaction, token budgeting, tool output pruning, and `AGENTS.md` environment loading.
- **LSP Integration**: Post-edit diagnostics feedback loop.

## What codeLess Already Had
Before this integration, codeLess's `LlmHarnessAndAgentEngine` provided:
- Basic tools: `read_file`, `write_file`, `edit_file` (SEARCH/REPLACE patch adaptation), `list_dir`, `grep_search`, `ast_outline`, `get_diagnostics`, `run_terminal`.
- `NetworkLedger` and Offline Mode.
- Basic syntax gates and BM25 index.
- Trust boundaries and verification loops.

## What Was Adopted/Adapted and Why
1. **Tool Semantics**: Adopted OpenCode's pagination and truncation (`offset`/`limit` in `read`), file modified-since-read checks (`file/time.ts`), and exact `edit` replacer cascade (`packages/opencode/src/tool/edit.ts`). Why: Increases model success rate drastically when editing large files.
2. **Bash Execution Limits**: Adopted OpenCode's strict timeout (2m/10m) and output truncation (`packages/opencode/src/tool/bash.ts`). Why: Prevents agents from hanging on interactive commands.
3. **LSP Feedback Loop**: Implemented OpenCode's `<file_diagnostics>` post-edit injection. Why: Allows agents to immediately fix compilation errors caused by their edits.
4. **AGENTS.md / Context**: Adopted `session/system.ts` logic for environment building. Why: Standardization for multi-repo work.
5. **Robustness**: Adopted doom-loop detection and JSON repair. Why: Essential for autonomous loop stability.
