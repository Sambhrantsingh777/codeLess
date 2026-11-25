# codeLess v3.4.0

A major feature and performance release bringing full VS Code multi-window/multi-instance parity, a complete `sst/opencode`-derived autonomous agent harness (M1–M8), data-driven keybindings and settings (`settings.json` / `keybindings.json`), 100% audited editor and workbench parity, and a 74%–99% reduction in in-process managed memory usage.

## Highlights in v3.4.0

### 1. Multi-Window & Multi-Instance Parity (`W1`)
- **Single-instance IPC forwarding (`ApplicationSession` / `NamedPipeIpcTransport` / `AvaloniaWindowManager`)**: Launching a second `codeLess` process forwards CLI arguments over a per-user named pipe (`NamedPipeIpcTransport`) to the primary instance and exits cleanly; if a folder or file is already open in an existing window, that window is focused instead of duplicating it.
- **VS Code CLI flags (`ApplicationSession.ProcessArgs`)**: Full support for `<folder|file>`, `-n` / `--new-window`, `-r` / `--reuse-window`, `-g` / `--goto <file:line[:col]>`, `-d` / `--diff <left> <right>`, `-a` / `--add <folder>`, `-w` / `--wait` (blocks caller until the target tab or window closes), `--new-instance`, `--user-data-dir <dir>`, and `--workspace <dir>`.
- **Window session restore (`window.restoreWindows` & `WorkspaceSessionStore`)**: Restores open windows, bounds, maximised/fullscreen state, split editors, and tabs on startup (`all`, `one`, or `none`), with cascading placement for new windows and macOS dock stay-alive semantics when the last window closes.
- **Live cross-window settings sync (`IdeSettingsWatcher`)**: Editing settings or themes in one window or on disk propagates live to all open windows without clobbering per-window layout or MRU state.

### 2. VS Code Keybindings & Settings (`W3`)
- **Data-driven `KeybindingManager` & `WhenParser`**: Every shortcut in `DefaultCommands` (`CommandRegistry.AllCommands`) is evaluated through boolean `when`-clauses (`editorTextFocus`, `terminalFocus`, `suggestWidgetVisible`, `inQuickOpen`, `findInputFocussed`, `inDebugMode`, etc.) with multi-chord dispatch (`Cmd/Ctrl+K ...`) and `TerminalCommands.CommandsToSkipShell` pass-through.
- **Keyboard Shortcuts editor (`Cmd/Ctrl+K Cmd/Ctrl+S`) & `keybindings.json`**: Searchable shortcut table with live conflict detection, key-recording modal, and round-trip persistence to `keybindings.json` (preserving JSONC comments and `-command` unbind entries).
- **Layered `settings.json` (JSONC)**: Two-way synchronisation between the Settings UI (`Cmd/Ctrl+,`) and `settings.json`, plus workspace overrides in `.vscode/settings.json`.

### 3. `opencode`-Derived Agent Harness (`M1–M8`)
- **14 built-in agent tools (`LlmHarnessAndAgentEngine.Tools`)**: `read_file` [alias `read`] (0-based `offset` or 1-based `start_line`, default `limit` 2,000 lines, 2,000-char line cap, binary file guard, Levenshtein missing-file suggestions), `write_file` [alias `write`], `edit_file` [alias `edit`] (9-stage fuzzy `EditReplacers` cascade + `FileTimeTracker` read-before-edit guard), `multiedit` (atomic all-or-nothing batch edits with `FileTimeTracker` read-before-edit guard), `list_dir` [alias `list`], `grep_search` [alias `grep`], `glob`, `run_terminal` [aliases `run_command`, `bash`] (30,000-character head+tail truncation), `get_diagnostics`, `ast_outline`, `todowrite`, `todoread`, `webfetch` (HTML-to-Markdown/text via `LedgerHttp` / `NetworkLedger`), and `task` (depth-1 read-only subagent, `MaxSubagentIterations = 6`).
- **Plan vs. Build modes & `AgentPermissions`**: Switch between `Build` and `Plan` modes from the chat header or with `Tab` in the chat input; `Plan` mode denies all file edits (`write_file`, `edit_file`, `multiedit`) and restricts `run_terminal` to read-only inspection commands.
- **Resilience, context & diagnostics**: Native OpenAI (`tools` / `tool_calls`) and Anthropic (`tool_use` / `tool_result`) streaming function-calling with automatic text-mode fallback, exponential backoff honoring `retry-after-ms` / `retry-after`, 3-strike doom-loop detection, automatic context compaction, hierarchical rules discovery (`AGENTS.md`, `CLAUDE.md`, `.github/copilot-instructions.md`, `.cursorrules`, `.codelessrules`, `.codeless/instructions.md`), and post-edit LSP `<file_diagnostics>` feedback.

### 4. Editor & Workbench Touch-and-Feel Parity (`W5` & `W6`)
- **Editor (`W5`, 58/58 behaviours verified)**: `editor.wordSeparators` word navigation and `camelCase`/`PascalCase`/`snake_case` subword navigation (`Ctrl+Alt+Left/Right` / `Ctrl+Option+Left/Right`), progressive Expand/Shrink Selection with full history stack, drag-and-drop selection move (`Alt`/`Option` to copy), `Sort Lines Ascending/Descending`, `Transpose Letters` (`Ctrl+T`), multi-tabstop snippet navigation (`Tab` / `Shift+Tab` / `Esc`), fuzzy completion highlighting with recency boost, `Fold Level 1..7`, `editor.cursorSurroundingLines`, `editor.useTabStops`, and `editor.emptySelectionClipboard`.
- **Workbench (`W6`, 30/30 behaviours verified)**: Explorer file Copy/Cut/Paste (`Cmd/Ctrl+C/X/V`), drag-and-drop move with open-tab repathing, compact single-child folders, type-to-filter, and problem count badges; automatic tab parent-folder disambiguation (`index.ts  a` vs `index.ts  b`) and dirty dot (`●`) to close (`×`) hover swap; Quick Open `?` help prefix, `Cmd/Ctrl+P` cycling, `Cmd/Ctrl+Enter` open-to-side, and recently used commands first in `>`; Search single-match replace and `Delete` to dismiss results/groups; Status Bar `(N selected)` count, Problems text filter, Notification Center with `"Do Not Show Again"`, and `Edit → Emoji & Symbols`.

### 5. Memory Profiling, Lazy Subsystems & Leak Fixes (`W4`)
- **Lazy subsystems & workspace indexing**: Deferred BM25/symbol indexing, AI harness, debugger, git blame, LSP manager, and network ledger until first use, cutting `LoadWorkspace` managed heap growth from `+77.24 MB` to **`+0.46 MB` (-99.4%)** and 10-file open live heap from `109.71 MB` to **`28.20 MB` (-74.3%)**.
- **Deterministic window, tab & panel collection**: Stopped all background timers (`devToolsPoll`, debounce timers, caret/scroll timers) and cleared MRU/editor/LSP retainers on close, reducing post-close live heap from `126.43 MB` to **`6.70 MB` (-94.7%)**, guarded by `WeakReference` regression tests.
- **Shared immutable themes & bitmaps**: Cached built-in `StudioTheme` instances (`0 B` allocated across repeated lookups) and shared window icons/welcome bitmaps across windows.

---

# codeLess v3.3.5

A focused update for the terminal, and the first version you can install from inside codeLess
(**Help → Check for Updates…**, or the codeLess menu on macOS).

## Fixed

- **Pasting into full-screen terminal apps** (e.g. the Antigravity CLI `agy`, other Bubble Tea / Ink apps): codeLess now
  answers the terminal capability questions these apps ask at startup (device attributes, terminal version, foreground /
  background colour), exactly like VS Code's terminal. Without the answers some apps waited and didn't take input.
- **Edit → Paste / Copy / Select All with the terminal focused** (also ⌘V / ⌘C through the macOS menu) now go straight
  to the focused terminal, including split terminals.

## Trust and verification

Releases include GitHub build-provenance attestations and a Sigstore-signed `SHA256SUMS.txt`; Linux packages are also
OpenPGP-signed (`docs/codeLess-release-signing-key.asc`). See `docs/SIGNING.md`.

## Upgrade notes

- From v3.3.4+: Help → Check for Updates… downloads, verifies and installs this version. From v3.3.3 or older, download
  it once by hand.
- Windows SmartScreen and macOS Gatekeeper may still warn until code-signing certificates are in place.
