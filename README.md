# codeLess — Lightweight Native Agentic IDE & Multi-Harness Coding Workbench

[![License: MIT](https://img.shields.io/badge/License-MIT-blue.svg)](./LICENSE)
[![Version](https://img.shields.io/badge/version-3.3.3-0ea5e9.svg)](https://github.com/Sambhrantsingh777/codeLess/releases)
[![Platforms](https://img.shields.io/badge/platform-Windows%20%7C%20macOS%20%7C%20Linux-10b981.svg)](#installation--building-from-source)
[![BYOK](https://img.shields.io/badge/AI-100%25%20BYOK%20%26%20Local%20Ollama-8b5cf6.svg)](#multi-profile-llm-harness--byok-providers)

**codeLess** (by **Savatarr**) is a fast, open-source, native polyglot code editor and AI software engineering IDE for **Windows**, **macOS** (`arm64` & `x64`), and **Linux** (`x64` & `arm64`).

Instead of bundling a Chromium browser and Node.js runtime, **codeLess** renders its workbench, syntax-highlighted virtualized editor canvas, minimap, diff review hunks, and truecolor VT100 terminal directly through native C# graphics pipelines (**Win32 / GDI+ on `.NET Framework 4.8`** for a **`~2.4 MB` standalone Windows `.exe`**, and **`.NET 8 + Avalonia Skia`** for native macOS and Linux desktop bundles). It pairs **VS Code keybindings** with a **Bring-Your-Own-Key (BYOK) autonomous coding agent** and **5 model-family-tuned LLM harnesses**—with zero subscription lock-in, zero intermediary proxy servers, and zero telemetry.

---

## Measured: codeLess vs VS Code

Same Mac (Apple M3, 8 GB), same workspace and file, one app at a time, median of 3 — VS Code 1.147 with a fresh profile
and extensions disabled. Full method, ranges and caveats: [`docs/BENCHMARKS.md`](./docs/BENCHMARKS.md).

| Metric | codeLess | VS Code |
| :--- | :--- | :--- |
| Idle memory (whole process tree, phys_footprint) | **199 MB** | 1,062 MB |
| Idle CPU | **0.4 %** of one core | 4.3 % |
| Processes | **2** | 10 |
| Open a 100 MB file | **74 ms** | 355 ms |
| Memory with a 300 MB file open | **270 MB** | 1,659 MB |
| Cold start to first frame | **993 ms** | 1,089 ms |

---

## Measured Footprint & Performance

| Metric | Measured Value |
| :--- | :--- |
| **Windows Standalone Executable (`codeLess.exe`)** | **`2.43 MB`** (portable single `.exe` with `codeLess.Core.dll` and 20 TextMate grammars embedded) |
| **Shared Core Engine (`codeLess.Core.dll`)** | `net48` / `netstandard2.0` / `net8.0` — LSP, DAP, git, tasks, agent and search engines shared by every platform |
| **Windows Setup Installer (`codeLess-Setup.exe`)** | **`2.95 MB`** (Inno Setup LZMA2, per-user `PrivilegesRequired=lowest`) |
| **macOS Self-Contained Disk Image (`.dmg`)** | **`47.6 MB`** (`osx-arm64`) / **`49.1 MB`** (`osx-x64`) *(bundles `.NET 8` + Skia runtime)* |
| **Managed (.NET GC) Heap (Full Workbench Loaded)** | **`~30 MB`** |
| **Total Memory Footprint, macOS (whole process tree, idle, workspace + file open)** | **`199 MB`** phys_footprint · `214 MB` RSS · 2 processes *(measured with `bench/run_bench.py`; VS Code 1.147: 1,062 MB / 10 processes)* |
| **20,000-File Repository Async Open** | **`30 ms` return**, **`23 ms` first tree paint** (non-blocking background indexing) |
| **6 MB (`70,000+`-Line) Source File Open** | **`< 200 ms`** (viewport-virtualized line rendering) |

---

## Architectural Comparison: codeLess vs. Other IDEs

| Dimension | Electron / Chromium IDEs *(VS Code, Cursor, Windsurf)* | JVM Desktop IDEs *(IntelliJ Platform)* | **codeLess** |
| :--- | :--- | :--- | :--- |
| **UI & Rendering Stack** | Chromium DOM/Canvas + bundled V8 + Node.js multi-process host | Bundled Java Runtime (JBR) + Swing/AWT | **Windows**: Native Win32 + GDI+ (`csc.exe` on OS-built-in `.NET 4.8`)<br>**macOS / Linux**: Native `.NET 8` + Skia (`Avalonia`) |
| **Bundled Browser / Node Runtime** | Bundles Chromium + Node.js in every installation | Bundles custom Java runtime | **None** (`0` Chromium processes; optional system `node` only if activating JS `.vsix` scripts) |
| **Windows Portable Binary Size** | Multi-hundred-MB application directory | Multi-hundred-MB installation | **`2.43 MB` single portable `codeLess.exe`** (`2.95 MB` installer) |
| **Terminal Subsystem** | `node-pty` + `xterm.js` DOM/WebGL renderer | `pty4j` + JediTerm | Direct P/Invoke **`CreatePseudoConsole` (Windows ConPTY)** & **`forkpty` (Unix PTY)** + native C# **VT100/Truecolor** cell grid |
| **AI Routing & Data Privacy** | Cloud proxy / vendor account required in commercial AI forks | Plugin-dependent cloud routing | **Direct HTTPS from user machine to chosen API or `127.0.0.1` Ollama** (`0` proxy servers, `0` telemetry) |
| **Codebase Search & Retrieval** | Cloud vector upload in several AI IDEs or external indexers | Heavy full-project background index | **Local in-process BM25 + 1-hop AST symbol graph** (`0` code uploaded for indexing) |

---

## Multi-Profile LLM Harness & BYOK Providers

Most AI coding tools apply a single prompt template to every model family or splice raw text patches without syntax verification. In **codeLess**, `LlmHarnessAndAgentEngine` and `SyntaxVerifier` surround your chosen model with deterministic pre- and post-edit guardrails:

### 1. Five Model-Family-Tuned Harness Profiles
1. **`claude-xml-surgical`** (*Anthropic Claude 3.7 / 4*): XML-structured `<repository_map>`, `<active_editor>`, `<diagnostics>`, `<thinking>` planning blocks, and surgical `SEARCH/REPLACE` hunks.
2. **`openai-reasoning-patch`** (*OpenAI GPT-4o / GPT-4.1 / o-series*): Developer-hierarchy instructions with deterministic JSON `<tool_call>` loops and multi-hunk patching.
3. **`gemini-long-context`** (*Google Gemini 2.5 Pro / Flash*): `128k`-token budget feeding cross-file AST symbol maps and dependency chains.
4. **`deepseek-r1-cot`** (*DeepSeek R1 / V3*): Isolates `<think>...</think>` chain-of-thought reasoning tokens from executable `<tool_call>` JSON and `SEARCH/REPLACE` blocks.
5. **`qwen-codestral-fim`** (*Qwen 2.5 Coder / Codestral / Groq / Local Ollama*): Low-temperature (`0.05`) Fill-In-the-Middle (`<PRE>...<CURSOR>...<SUF>`) and zero-prose surgical patches for local and LPU models.

You can also run two harnesses concurrently on the same file in the **Multi-Harness Parallel Arena** to compare latency and resulting AST diagnostics side-by-side before staging.

### 2. Deterministic Harness Safeguards
- **Indentation, Tab/Space, EOL, and Ellipsis (`...`) Patch Adaptation**: Automatically rebases drifted indentation in LLM `SEARCH/REPLACE` blocks to match nested target blocks, adapts spaces ↔ tabs, preserves `CRLF` vs. `LF` and UTF-8 BOM, resolves unambiguous `...` anchor elisions, resolves headerless multi-file hunks across workspace files, and rejects ambiguous duplicate matches.
- **Atomic Rollback & Pre-Stage Compiler/AST Syntax Gate**: Validates every `edit_file`, `write_file`, and `SEARCH/REPLACE` patch *before* staging via **Roslyn `CSharpSyntaxTree` / `CSharpCodeProvider`** (`.cs`), **`python -m py_compile`** (`.py`), **`node --check`** (`.js`), and JSON parsing. If any hunk fails to match or introduces a syntax error, all hunks roll back atomically and compiler diagnostics are fed back via `<edit_error>` for automatic self-repair in the next turn.
- **Local BM25 + 1-Hop AST Symbol Retrieval**: Splits `camelCase` and `snake_case` identifiers, filters stop-words, and expands 1-hop AST symbol references across co-located module files completely offline.
- **8-Tool Autonomous Agent Loop with Process-Tree Safety**: Supports `read_file`, `write_file`, `edit_file`, `list_dir`, `grep_search`, `ast_outline`, `get_diagnostics`, and `run_terminal` with in-loop staged-file visibility, non-zero exit code reporting, user approval gates, and full child process-tree termination on timeout.

### 3. Supported BYOK Providers (Zero Lock-In)
Connect API keys (via Preferences `Ctrl/Cmd+,` or standard environment variables) for **Anthropic Claude**, **OpenAI**, **Google Gemini**, **OpenRouter**, **DeepSeek**, **Groq LPU**, **Mistral / Codestral**, **xAI Grok**, **Ollama / LM Studio (`127.0.0.1:11434`)**, or any **Custom OpenAI-Compatible `/v1` Endpoint**.

---

## Key IDE Features

Everything below is built in — no extensions to find, install or configure — and works on Windows, macOS and Linux.

- **Code intelligence (built-in LSP client)**: completion with docs and snippets, hover, signature help, go to definition / type definition / implementation, find references, rename across files, format, quick fixes, live diagnostics and outline. Language servers are auto-detected (pyright, typescript-language-server, gopls, rust-analyzer, clangd, csharp-ls, jdtls, …); without one, a heuristic outline, word-based suggestions and import-aware `Cmd/Ctrl+Click` navigation still work.
- **Debugger (DAP)**: Run and Debug view, breakpoints with conditions / hit counts / logpoints, variables, watch, call stack, debug console, `launch.json` with `preLaunchTask`; debugpy, dlv, lldb-dap / codelldb, netcoredbg. **Zero-setup Python**: interpreter picker (venv, uv, poetry, pipenv, conda, pyenv), Run/Debug Python File and automatic venv activation in new terminals.
- **Verified AI agent**: AI edits can be built and tested in a private copy of your workspace before you accept them, with optional auto-repair, "Accept if Verified", Best-of-N across models and per-turn undo/redo (including files the agent's commands created or deleted).
- **Git**: inline blame, commit graph and file history, branch picker, Sync / Pull / Push / Publish, merge-conflict "Accept Current / Incoming / Both", stage or revert a single hunk from the gutter, byte-accurate saves (`CRLF` / `LF` / BOM preserved).
- **Tasks**: `tasks.json`, Run Build Task (`Cmd/Ctrl+Shift+B`), auto-detected npm / make / dotnet / cargo / go / pytest tasks, problem matchers feeding the Problems panel.
- **Editor**: viewport-virtualized canvas, real word wrap, sticky scroll, bracket-pair colours and guides, indent guides, occurrence highlight, folding (braces, Allman, regions, indentation, Markdown), multi-cursor, HTML/JSX tag auto-close and linked rename, Emmet, TextMate grammars (20 bundled + VSIX grammars), scrollbar with overview ruler, indentation detection and `.editorconfig`, inline diff review with deleted-line zones.
- **Huge files and big repos**: files of 50 MB+ open instantly in a read-only viewer with search, go to line and live tail; `.gitignore`-aware Find in Files and Quick Open across 50k+ files.
- **Privacy you can see**: View → Network Activity lists every request codeLess makes; **Offline Mode** blocks everything except localhost (local models keep working); zero telemetry. **Workspace Trust** asks before anything from an unfamiliar folder can run.
- **Native Integrated Terminal (ConPTY & Unix PTY)**: Truecolor VT100, split panes, named sessions, `Cmd/Ctrl+Click` on URLs and `file:line:col`.
- **More**: `.http` request runner, native Markdown preview, 11 built-in themes plus VSIX themes / snippets / grammars and OpenVSX search.

---

## Installation & Building from Source

### Direct Pre-Built Downloads ([Latest Release](https://github.com/Sambhrantsingh777/codeLess/releases/latest))

> **Code signing:** Windows releases use free code signing provided by [SignPath.io](https://about.signpath.io/), certificate by [SignPath Foundation](https://signpath.org/) *(application under review — until it is approved, Windows binaries are unsigned)*. Every release also ships GitHub build-provenance attestations and a Sigstore-signed `SHA256SUMS.txt`; Linux packages are OpenPGP-signed. See the [code signing policy](./docs/CODE_SIGNING_POLICY.md).

| Platform | Package | Direct Download |
| :--- | :--- | :--- |
| **Windows 10 / 11 (x64)** | Setup Installer (`.exe`, ~2.9 MB) | [`codeLess-Setup.exe`](https://github.com/Sambhrantsingh777/codeLess/releases/latest/download/codeLess-Setup.exe) |
| **Windows 10 / 11 (x64)** | Windows Installer Package (`.msi`, ~1.1 MB) | [`codeLess-Setup.msi`](https://github.com/Sambhrantsingh777/codeLess/releases/latest/download/codeLess-Setup.msi) |
| **Windows 10 / 11 (x64)** | Portable Standalone (`.exe`, ~2.4 MB) | [`codeLess.exe`](https://github.com/Sambhrantsingh777/codeLess/releases/latest/download/codeLess.exe) |
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

- [`docs/KEYBINDINGS.md`](./docs/KEYBINDINGS.md) — Keyboard shortcuts & mouse selection reference
- [`docs/MAC.md`](./docs/MAC.md) — macOS build, packaging, and installation guide
- [`docs/SIGNING.md`](./docs/SIGNING.md) — Windows Authenticode & SignPath CI code-signing guide
- [`CONTRIBUTING.md`](./CONTRIBUTING.md) — Architecture overview and contributor workflow
- [`SECURITY.md`](./SECURITY.md) — Local-first privacy model and vulnerability reporting

---

## License

Released under the [MIT License](./LICENSE). Copyright © 2026 Sambhrant Singh.
