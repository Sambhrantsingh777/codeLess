# codeLess — Lightweight Native Agentic IDE & Multi-Harness Coding Workbench

[![License: MIT](https://img.shields.io/badge/License-MIT-blue.svg)](./LICENSE)
[![Version](https://img.shields.io/badge/version-3.4.0-0ea5e9.svg)](https://github.com/Sambhrantsingh777/codeLess/releases)
[![Platforms](https://img.shields.io/badge/platform-Windows%20%7C%20macOS%20%7C%20Linux-10b981.svg)](#installation--building-from-source)
[![BYOK](https://img.shields.io/badge/AI-100%25%20BYOK%20%26%20Local%20Ollama-8b5cf6.svg)](#opencode-derived-agent-harness--multi-profile-byok-providers)

**codeLess** (by **Savatarr**) is a fast, open-source, native polyglot code editor and AI software engineering IDE for **Windows**, **macOS** (`arm64` & `x64`), and **Linux** (`x64` & `arm64`).

Instead of bundling a Chromium browser and Node.js runtime, **codeLess** renders its workbench, syntax-highlighted virtualized editor canvas, minimap, diff review hunks, and truecolor VT100 terminal directly through native C# graphics pipelines (**Win32 / GDI+ on `.NET Framework 4.8`** for a **`~2.6 MB` standalone Windows `.exe`**, and **`.NET 8 + Avalonia Skia`** for native macOS and Linux desktop bundles). It pairs **VS Code keybindings, multi-window workflows, and settings** with an **`sst/opencode`-derived Bring-Your-Own-Key (BYOK) autonomous coding agent** and **5 model-family-tuned LLM harnesses**—with zero subscription lock-in, zero intermediary proxy servers, and zero telemetry.

---

## Measured: codeLess vs VS Code

### 1. Whole-Process Desktop Benchmark (`docs/BENCHMARKS.md`)

Same Mac (Apple M3, 8 GB), same workspace (312 files) and file (`MainWindow.cs`), one app at a time, median of 3 — VS Code 1.147 with a fresh profile and extensions disabled (measured 2026-11-22 on v3.3.1; desktop GUI RSS re-measurement after the v3.4.0 `W4` memory optimisations is pending an interactive desktop session). Full method, ranges and caveats: [`docs/BENCHMARKS.md`](./docs/BENCHMARKS.md).

| Metric | codeLess | VS Code | Difference |
| :--- | :--- | :--- | :--- |
| Idle memory (whole process tree, `phys_footprint`) | **199 MB** (198 – 215) | 1,062 MB (1,053 – 1,078) | **5.3× less** |
| Idle memory (whole process tree, RSS) | **214 MB** (213 – 219) | 1,651 MB (1,604 – 1,652) | **7.7× less** |
| Idle CPU (30 s window) | **0.4 %** of one core | 4.3 % (4.2 – 5.0) | **~10× less** |
| Processes | **2** | 10 | **5× fewer** |
| Open a 100 MB file (request → content painted) | **74 ms** (66 – 78) | 355 ms (310 – 367) | **4.8× faster** |
| Memory after opening the 100 MB file (`phys_footprint`) | **264 MB** (230 – 268) | 1,398 MB (1,397 – 1,428) | **5.3× less** |
| Open a 300 MB file (time to content) | **75 ms** | 891 ms | **12× faster** |
| Memory after opening the 300 MB file (`phys_footprint`) | **270 MB** | 1,659 MB | **6.1× less** |
| Cold start to first frame | **993 ms** (926 – 1,125) | 1,089 ms (1,086 – 1,179) | **~9% faster** |

### 2. In-Process Managed Heap Profile (`docs/MEMORY.md`, v3.4.0 `W4`)

Measured deterministically in-process via `MemoryProfileAndLeakTests.MemoryProfile_EndToEnd_StepsAndRetainers` (`GC.GetTotalMemory(true)` after a compacting full GC on Apple Silicon, .NET 8.0.428; see [`docs/MEMORY.md`](./docs/MEMORY.md)):

| Lifecycle Step (In-Process Managed Heap) | Before `W4` (`a3dfb17`) | After `W4` (v3.4.0) | Change |
| :--- | ---: | ---: | ---: |
| Startup with empty window (`1280×800`, delta over baseline) | `+17.42 MB` (`18.11 MB` live) | **`+14.86 MB`** (`17.61 MB` live) | **`-14.7%`** |
| Open workspace (this repo, ~398 tracked files, step delta) | `+77.24 MB` (`95.35 MB` live) | **`+0.46 MB`** (`18.07 MB` live) | **`-99.4%`** (lazy BM25/symbol index) |
| Open 10 source files (`12,447` lines, total live managed heap) | `109.71 MB` (`+14.36 MB` delta) | **`28.20 MB`** (`+10.13 MB` delta) | **`-74.3%`** total live heap |
| Open Search, SCM, Terminal, and AI Chat panels (step delta) | `+5.20 MB` (`114.92 MB` live) | **`+1.05 MB`** (`29.24 MB` live) | **`-79.8%`** |
| Open a second window (step delta) | `+16.39 MB` (`131.31 MB` live) | **`+13.98 MB`** (`43.22 MB` live) | **`-14.7%`** |
| Close all tabs, workspace, and both windows (live managed heap) | `126.43 MB` (windows/tabs leaked) | **`6.70 MB`** (windows/tabs collected) | **`-94.7%`** |
| `StudioTheme.GetById` × 50 lookups (allocated bytes) | `1,994,000 B` | **`0 B`** | **`-100%`** |

---

## Measured Footprint & Performance Summary

| Metric | Measured Value |
| :--- | :--- |
| **Windows Standalone Executable (`codeLess.exe`)** | **`2.57 MB`** (portable single `.exe` with `codeLess.Core.dll` and 20 TextMate grammars embedded) |
| **Shared Core Engine (`codeLess.Core.dll`)** | `net48` / `netstandard2.0` / `net8.0` — LSP, DAP, git, tasks, keybindings, agent and search engines shared by every platform |
| **Windows Setup Installer (`codeLess-Setup.exe`)** | **`2.99 MB`** (Inno Setup LZMA2, per-user `PrivilegesRequired=lowest`) |
| **macOS Self-Contained Disk Image (`.dmg`)** | **`47.7 MB`** (`osx-arm64`) / **`49.2 MB`** (`osx-x64`) *(bundles `.NET 8` + Skia runtime)* |
| **In-Process Managed (.NET GC) Heap (Workspace + 10 Files + All Panels)** | **`29.24 MB` live** (`18.07 MB` after opening workspace; `6.70 MB` after closing all windows) |
| **Total Desktop Memory Footprint, macOS (`bench/run_bench.py`)** | **`199 MB`** `phys_footprint` · `214 MB` RSS · 2 processes *(VS Code 1.147: 1,062 MB / 10 processes)* |
| **1 GB (`7.26M`-Line) Log File Index & Search (`LargeFileDocument`)** | **`0.3 ms`** first 60 lines readable · **`191 ms`** full index (`3.4 MB` managed) · **`295 ms`** literal search |
| **38,000-File Monorepo Quick Open (`WorkspaceIgnoreRules`)** | **`6.0 ms` median** per keystroke (`26.2 MB` lower-cased index, `.gitignore`-aware) |

---

## Architectural Comparison: codeLess vs. Other IDEs

| Dimension | Electron / Chromium IDEs *(VS Code, Cursor, Windsurf)* | JVM Desktop IDEs *(IntelliJ Platform)* | **codeLess** |
| :--- | :--- | :--- | :--- |
| **UI & Rendering Stack** | Chromium DOM/Canvas + bundled V8 + Node.js multi-process host | Bundled Java Runtime (JBR) + Swing/AWT | **Windows**: Native Win32 + GDI+ (`csc.exe` on OS-built-in `.NET 4.8`)<br>**macOS / Linux**: Native `.NET 8` + Skia (`Avalonia`) |
| **Bundled Browser / Node Runtime** | Bundles Chromium + Node.js in every installation | Bundles custom Java runtime | **None** (`0` Chromium processes; optional system `node` only if activating JS `.vsix` scripts) |
| **Windows Portable Binary Size** | Multi-hundred-MB application directory | Multi-hundred-MB installation | **`2.57 MB` single portable `codeLess.exe`** (`2.99 MB` installer) |
| **Terminal Subsystem** | `node-pty` + `xterm.js` DOM/WebGL renderer | `pty4j` + JediTerm | Direct P/Invoke **`CreatePseudoConsole` (Windows ConPTY)** & **`forkpty` (Unix PTY)** + native C# **VT100/Truecolor** cell grid |
| **AI Routing & Data Privacy** | Cloud proxy / vendor account required in commercial AI forks | Plugin-dependent cloud routing | **Direct HTTPS from user machine to chosen API or `127.0.0.1` Ollama** (`0` proxy servers, `0` telemetry) |
| **Codebase Search & Retrieval** | Cloud vector upload in several AI IDEs or external indexers | Heavy full-project background index | **Lazy in-process BM25 + 1-hop AST symbol graph** (`0` code uploaded for indexing) |

---

## `opencode`-Derived Agent Harness & Multi-Profile BYOK Providers

Portions of the **codeLess** autonomous agent harness (`src/codeLess.Core/` and `src/LlmHarnessAndAgentEngine.cs`) are derived from **[sst/opencode](https://github.com/sst/opencode)** (MIT License) and adapted for an interactive desktop IDE with staged inline diff review, Workspace Trust, and the Network Privacy Ledger (see [`docs/HARNESS_OPENCODE.md`](./docs/HARNESS_OPENCODE.md)).

### 1. `Build` & `Plan` Modes, Permissions, and 14 Built-In Agent Tools
- **`Build` vs. `Plan` Mode (`Tab` to cycle)**: Toggle between `Build` (full read/write/terminal access) and `Plan` (read-only analysis and planning) in the AI chat bar or by pressing `Tab` in the chat input (`AgentPermissions`). In `Plan` mode, all file-modifying tools (`write_file`, `edit_file`, `multiedit`) are denied and `run_terminal` only permits read-only inspection commands (`ls`, `cat`, `head`, `tail`, `rg`, `grep`, `find`, `git status/diff/log/show`, `pwd`, `wc`, `tree`), prompting on anything else.
- **14 Autonomous Workspace Tools (`LlmHarnessAndAgentEngine.Tools`)**:
  1. **`read_file`** (alias `read`): 0-based `offset` (or 1-based `start_line`) & `limit` (default 2,000 lines), `00001| ` line prefixes, 2,000-char line cap, `BinaryDetector` guard, and `MissingFileSuggestions` ("Did you mean?") via Levenshtein distance.
  2. **`write_file`** (alias `write`): Creates/stages files into `StagedFileContents` for inline diff review.
  3. **`edit_file`** (alias `edit`): Applies surgical replacements to existing files via the **9-stage `EditReplacers` cascade** (`Simple`, `LineTrimmed`, `BlockAnchor`, `WhitespaceNormalized`, `IndentationFlexible`, `EscapeNormalized`, `TrimmedBoundary`, `ContextAware`, `MultiOccurrence`), guarded by `FileTimeTracker` read-before-edit and mtime checks.
  4. **`multiedit`**: Applies multiple `EditReplacers` edits sequentially to a single file on an in-memory snapshot (enforcing `FileTimeTracker` read-before-edit) and rolls back atomically if any edit fails.
  5. **`list_dir`** (alias `list`): Indented directory tree skipping ignored directories (`node_modules`, `.git`, `bin`, `obj`), capped at 100 entries.
  6. **`grep_search`** (alias `grep`): Regex search across workspace and staged files, grouped by file, sorted newest-first, capped at 100 matches.
  7. **`glob`**: Glob matching (`*`, `**`, `?`, `{a,b}`), sorted newest-first by mtime, capped at 100 files.
  8. **`run_terminal`** (aliases `run_command`, `bash`): Runs shell commands with process-tree timeout enforcement and 30,000-character head+tail output truncation.
  9. **`get_diagnostics`**: Returns compiler, syntax, and LSP diagnostics for a file or workspace.
  10. **`ast_outline`**: Extracts class, struct, interface, method, and function signatures for a workspace file.
  11. **`todowrite`** & 12. **`todoread`**: Structured session task list (`pending`, `in_progress`, `completed`, `cancelled`) rendered live in the AI sidebar (`☐`, `◐`, `☑`, `☒`).
  13. **`webfetch`**: Fetches documentation URLs through `LedgerHttp` / `NetworkLedger` (honouring Offline Mode, 5 MB cap, no cross-host redirects) and converts HTML to Markdown or plain text.
  14. **`task`**: Spawns a depth-1 read-only subagent (`SubagentDepth = 1`, `MaxSubagentIterations = 6`, read-only tools only, isolated staging) for parallel codebase investigation.
- **Resilience, Compaction & Diagnostics Loop**:
  - **Native Function Calling (`NativeToolCalling`)**: Streaming OpenAI (`tools` / `tool_calls`) and Anthropic (`tool_use` / `tool_result`) tool payloads with automatic per-`vendor:model` fallback to `<tool_call>` XML/JSON text mode on HTTP 400.
  - **Exponential Retry (`ApiRetryHandler`) & Doom-Loop Guard**: Retries transient HTTP 429 / 5xx errors up to 5 times honouring `retry-after-ms` and `retry-after` headers; halts and prompts after 3 identical consecutive tool calls (`doom_loop`).
  - **Context Compaction & Rules Discovery (`AgentContextBuilder`)**: Automatically discovers `AGENTS.md`, `CLAUDE.md`, `.github/copilot-instructions.md`, `.cursorrules`, `.codelessrules`, and `.codeless/instructions.md` (walking up to the git root, 16 KB cap), prunes old tool outputs (`PruneHistory`), and summarizes conversation history when approaching `ContextWindow - MaxOutputTokens`.
  - **Post-Edit LSP Diagnostics (`LspDiagnosticsProvider`) & AST Syntax Gate (`SyntaxVerifier`)**: Validates edits before staging and injects `<file_diagnostics>` (up to 20 errors) so the agent repairs compiler/type errors immediately.

### 2. Five Model-Family-Tuned Harness Profiles
1. **`claude-xml-surgical`** (*Anthropic Claude 3.7 / 4*): XML-structured `<repository_map>`, `<active_editor>`, `<diagnostics>`, `<thinking>` planning blocks, and surgical edits.
2. **`openai-reasoning-patch`** (*OpenAI GPT-4o / GPT-4.1 / o-series*): Developer-hierarchy instructions with native function calling and multi-hunk patching.
3. **`gemini-long-context`** (*Google Gemini 2.5 Pro / Flash*): `128k`-token budget feeding cross-file AST symbol maps and dependency chains.
4. **`deepseek-r1-cot`** (*DeepSeek R1 / V3*): Isolates `<think>...</think>` chain-of-thought reasoning tokens from executable tool calls and patches.
5. **`qwen-codestral-fim`** (*Qwen 2.5 Coder / Codestral / Groq / Local Ollama*): Low-temperature (`0.05`) Fill-In-the-Middle (`<PRE>...<CURSOR>...<SUF>`) and zero-prose surgical patches for local and LPU models.

### 3. Supported BYOK Providers (Zero Lock-In)
Connect API keys (via Preferences `Ctrl/Cmd+,` or standard environment variables) for **Anthropic Claude**, **OpenAI**, **Google Gemini**, **OpenRouter**, **DeepSeek**, **Groq LPU**, **Mistral / Codestral**, **xAI Grok**, **Ollama / LM Studio (`127.0.0.1:11434`)**, or any **Custom OpenAI-Compatible `/v1` Endpoint**.

---

## Key IDE Features

Everything below is built in — no extensions to find, install or configure — and works on Windows, macOS and Linux.

- **Multi-window & multi-instance CLI workflow (`W1`)**: Single-instance IPC forwarding over per-user named pipes (`ApplicationSession` / `NamedPipeIpcTransport` / `WorkspaceSessionStore` / `AvaloniaWindowManager`); full VS Code CLI flags (`codeLess <folder|file>`, `-n`/`--new-window`, `-r`/`--reuse-window`, `-g`/`--goto <file:line[:col]>`, `-d`/`--diff <left> <right>`, `-a`/`--add <folder>`, `-w`/`--wait`, `--new-instance`, `--user-data-dir <dir>`, `--workspace <dir>`); focusing already-open workspace/file windows instead of duplicating; per-window state isolation; live cross-window settings sync (`IdeSettingsWatcher`); cascading window placement; macOS dock stay-alive when the last window closes; and full session restoration (`window.restoreWindows`: `all` | `one` | `none`).
- **VS Code Keybindings & Settings (`W3`)**: Data-driven `KeybindingManager` and boolean `WhenParser` (`editorTextFocus`, `terminalFocus`, `suggestWidgetVisible`, `inQuickOpen`, `findInputFocussed`, `inDebugMode`) with multi-chord support (`Cmd/Ctrl+K ...`); searchable Keyboard Shortcuts UI (`Cmd/Ctrl+K Cmd/Ctrl+S`) with live conflict detection and key recording; and JSONC-preserving `settings.json` and `keybindings.json` sync across:
  - **macOS**: `~/Library/Application Support/codeLess/User/{settings,keybindings}.json`
  - **Linux**: `~/.config/codeLess/User/{settings,keybindings}.json`
  - **Windows**: `%APPDATA%\codeLess\User\{settings,keybindings}.json`
  - **Workspace overrides**: `<workspace>/.vscode/settings.json`
- **Editor touch & feel parity (`W5`, 58/58 audited behaviours)**: Viewport-virtualized canvas, `editor.wordSeparators` word navigation and `camelCase`/`PascalCase`/`snake_case` subword navigation (`Ctrl+Alt+Left/Right` / `Ctrl+Option+Left/Right`), progressive Expand/Shrink Selection (`Shift+Alt+Right/Left` / `Ctrl+Shift+Cmd+Right/Left`) with full history stack, double-click word drag & triple-click line drag, drag-and-drop selection move (`Alt`/`Option` to copy), multi-cursor (`Alt+Click`, `Cmd/Ctrl+D`, `Cmd/Ctrl+Shift+L`, `Shift+Alt+I` on selected lines, `Cmd/Ctrl+U` cursor undo, box selection), `Sort Lines Ascending/Descending`, `Transpose Letters` (`Ctrl+T`), `Join Lines`, `Transform to Uppercase/Lowercase`, `editor.emptySelectionClipboard` whole-line copy/cut/paste-above, `editor.useTabStops`, `editor.cursorSurroundingLines`, `Fold Level 1..7` (`Cmd/Ctrl+K Cmd/Ctrl+1..7`), `editor.renderWhitespace` (`selection` / `all` / `none`), multi-tabstop snippet navigation (`Tab` / `Shift+Tab` / `Esc`), and fuzzy completion with camelCase hump highlighting and recency boost.
- **Workbench parity (`W6`, 30/30 audited behaviours)**: Explorer file Copy/Cut/Paste (`Cmd/Ctrl+C/X/V`), drag-and-drop move with open-tab repathing, compact single-child folders (`explorer.compactFolders`), type-to-filter, git badges (`M`/`U`/`D`), and problem count badges; automatic tab title parent-folder disambiguation (`index.ts  a` vs `index.ts  b`) and dirty dot (`●`) to close (`×`) hover swap; Quick Open `?` help prefix, `Cmd/Ctrl+P` selection cycling, `Cmd/Ctrl+Enter` open-to-side, and recently used commands first in `>`; Search single-match replace and `Delete` to dismiss results/groups; Status Bar `(N selected)` count, Problems text filter, Notifications with `"Do Not Show Again"` & Notification Center history, and `Edit → Emoji & Symbols`.
- **Code intelligence (built-in LSP client)**: Completion with docs and snippets, hover, signature help, go to definition / type definition / implementation, find references, rename across files, format, quick fixes, live diagnostics and outline. Language servers are auto-detected (`pyright`, `typescript-language-server`, `gopls`, `rust-analyzer`, `clangd`, `csharp-ls`, `jdtls`, …); without one, heuristic outline, word-based suggestions and import-aware `Cmd/Ctrl+Click` navigation still work.
- **Debugger (DAP)**: Run and Debug view, breakpoints with conditions / hit counts / logpoints, variables, watch, call stack, debug console, `launch.json` with `preLaunchTask`; `debugpy`, `dlv`, `lldb-dap` / `codelldb`, `netcoredbg`. **Zero-setup Python**: interpreter picker (`venv`, `uv`, `poetry`, `pipenv`, `conda`, `pyenv`), Run/Debug Python File and automatic venv activation in new terminals.
- **Git & Source Control**: Staged / Unstaged / Untracked / Merge Changes groups, per-file and all stage/unstage/discard, commit box with `Cmd/Ctrl+Enter` and Amend, inline blame, commit graph and file history, branch picker, Sync / Pull / Push / Publish, merge-conflict "Accept Current / Incoming / Both", stage or revert a single hunk from the gutter, byte-accurate saves (`CRLF` / `LF` / BOM preserved).
- **Huge files and big monorepos**: Files of 50 MB+ open in a paged read-only viewer (`LargeFileDocument`) with streaming byte/regex search, go to line and live `tail -f`; `.gitignore`-aware Find in Files and Quick Open across 50k+ files.
- **Privacy you can see**: `View → Network Activity` lists every outbound request `codeLess` makes; **Offline Mode** blocks everything except localhost (`127.0.0.1` / `[::1]`, so local Ollama models keep working); zero telemetry. **Workspace Trust** asks before anything from an unfamiliar folder can run and upgrades agent permissions to `"ask"` in restricted mode.
- **Native Integrated Terminal (ConPTY & Unix PTY)**: Truecolor VT100, split panes, named sessions, full device-attribute/OSC query responses for TUI apps (`agy`, Bubble Tea, Ink), and `Cmd/Ctrl+Click` on URLs and `file:line:col`.

---

## Installation & Building from Source

### Direct Pre-Built Downloads ([Latest Release](https://github.com/Sambhrantsingh777/codeLess/releases/latest))

> **Code signing:** Windows releases use free code signing provided by [SignPath.io](https://about.signpath.io/), certificate by [SignPath Foundation](https://signpath.org/) *(application under review — until it is approved, Windows binaries are unsigned)*. Every release also ships GitHub build-provenance attestations and a Sigstore-signed `SHA256SUMS.txt`; Linux packages are OpenPGP-signed. See the [code signing policy](./docs/CODE_SIGNING_POLICY.md).

| Platform | Package | Direct Download |
| :--- | :--- | :--- |
| **Windows 10 / 11 (x64)** | Windows Installer Package (`.msi`, ~1.2 MB) — recommended | [`codeLess-Setup.msi`](https://github.com/Sambhrantsingh777/codeLess/releases/latest/download/codeLess-Setup.msi) |
| **Windows 10 / 11 (x64)** | Setup Installer (`.exe`, ~2.9 MB) | [`codeLess-Setup.exe`](https://github.com/Sambhrantsingh777/codeLess/releases/latest/download/codeLess-Setup.exe) |
| **Windows 10 / 11 (x64)** | Portable Standalone (`.exe`, ~2.6 MB) | [`codeLess.exe`](https://github.com/Sambhrantsingh777/codeLess/releases/latest/download/codeLess.exe) |
| **macOS Apple Silicon (M1–M4)** | Disk Image (`.dmg`) / Bundle (`.zip`) | [`codeLess-mac-arm64.dmg`](https://github.com/Sambhrantsingh777/codeLess/releases/latest/download/codeLess-mac-arm64.dmg) · [`.zip`](https://github.com/Sambhrantsingh777/codeLess/releases/latest/download/codeLess-mac-arm64.zip) |
| **macOS Intel (x64)** | Disk Image (`.dmg`) / Bundle (`.zip`) | [`codeLess-mac-x64.dmg`](https://github.com/Sambhrantsingh777/codeLess/releases/latest/download/codeLess-mac-x64.dmg) · [`.zip`](https://github.com/Sambhrantsingh777/codeLess/releases/latest/download/codeLess-mac-x64.zip) |
| **Linux (Ubuntu / Debian x64)** | Debian Package (`.deb`) / Tarball (`.tar.gz`) | [`codeLess-linux-x64.deb`](https://github.com/Sambhrantsingh777/codeLess/releases/latest/download/codeLess-linux-x64.deb) · [`.tar.gz`](https://github.com/Sambhrantsingh777/codeLess/releases/latest/download/codeLess-linux-x64.tar.gz) |
| **Linux (ARM64)** | Debian Package (`.deb`) / Tarball (`.tar.gz`) | [`codeLess-linux-arm64.deb`](https://github.com/Sambhrantsingh777/codeLess/releases/latest/download/codeLess-linux-arm64.deb) · [`.tar.gz`](https://github.com/Sambhrantsingh777/codeLess/releases/latest/download/codeLess-linux-arm64.tar.gz) |

### Windows (Compile from Source — Zero SDK Required)
Windows 10/11 includes the `.NET Framework 4.8` compiler (`csc.exe`) out of the box:
```powershell
git clone https://github.com/Sambhrantsingh777/codeLess.git
cd codeLess
.\Compile.ps1
.\codeLess.exe
```
- Pass `.\Compile.ps1 -Install` (with [Inno Setup 6](https://jrsoftware.org/isinfo.php) installed) to build `Output\codeLess-Setup.exe` and install per-user shortcuts.

### macOS (`osx-arm64` Apple Silicon & `osx-x64` Intel)
```bash
git clone https://github.com/Sambhrantsingh777/codeLess.git
cd codeLess
chmod +x ./build-mac.sh
./build-mac.sh          # outputs codeLess.app, .zip, and .dmg in dist/
```
See [`docs/MAC.md`](./docs/MAC.md) for installation and first-run Gatekeeper instructions.

### Linux (`linux-x64` & `linux-arm64`)
```bash
git clone https://github.com/Sambhrantsingh777/codeLess.git
cd codeLess
chmod +x ./cross-platform/build-linux.sh
./cross-platform/build-linux.sh
```

### Cross-Platform `.NET 8` Build & Automated Tests
```bash
dotnet build codeLess.sln -c Release
dotnet test codeLess.sln -c Release
```

---

## Documentation

- [`docs/BENCHMARKS.md`](./docs/BENCHMARKS.md) — Head-to-head VS Code benchmarks, large-file & monorepo numbers, and in-process memory profile
- [`docs/MEMORY.md`](./docs/MEMORY.md) — Managed heap profile, top retainers fixed, lazy subsystems, and GC configuration (`W4`)
- [`docs/HARNESS_OPENCODE.md`](./docs/HARNESS_OPENCODE.md) — `sst/opencode` attribution, architecture mapping (`M1–M8`), tool reference, and settings
- [`docs/KEYBINDINGS.md`](./docs/KEYBINDINGS.md) — Generated default keyboard shortcuts (`CommandRegistry`) and menu accelerators (`WorkbenchMenuModel`)
- [`docs/EDITOR_PARITY.md`](./docs/EDITOR_PARITY.md) — 58-behaviour VS Code editor touch & feel parity matrix (`W5`)
- [`docs/WORKBENCH_PARITY.md`](./docs/WORKBENCH_PARITY.md) — 30-behaviour VS Code workbench parity matrix (`W6`)
- [`docs/AGENT_TRUST.md`](./docs/AGENT_TRUST.md) — Agent verification, workspace trust, and network privacy ledger
- [`docs/MAC.md`](./docs/MAC.md) — macOS build, packaging, and installation guide
- [`docs/SIGNING.md`](./docs/SIGNING.md) — Windows Authenticode & SignPath CI code-signing guide
- [`CONTRIBUTING.md`](./CONTRIBUTING.md) — Architecture overview and contributor workflow
- [`SECURITY.md`](./SECURITY.md) — Local-first privacy model and vulnerability reporting

---

## License

Released under the [MIT License](./LICENSE). Copyright © 2026 Sambhrant Singh.
