# codeLess — Lightweight Native Agentic IDE & Multi-Harness Coding Workbench

[![License: MIT](https://img.shields.io/badge/License-MIT-blue.svg)](./LICENSE)
[![Version](https://img.shields.io/badge/version-3.2.3-0ea5e9.svg)](https://github.com/Sambhrantsingh777/codeLess/releases)
[![Platforms](https://img.shields.io/badge/platform-Windows%20%7C%20macOS%20%7C%20Linux-10b981.svg)](#installation--building-from-source)
[![BYOK](https://img.shields.io/badge/AI-100%25%20BYOK%20%26%20Local%20Ollama-8b5cf6.svg)](#multi-profile-llm-harness--byok-providers)

**codeLess** (by **Savatarr**) is a fast, open-source, native polyglot code editor and AI software engineering IDE for **Windows**, **macOS** (`arm64` & `x64`), and **Linux** (`x64` & `arm64`).

Instead of bundling a Chromium browser and Node.js runtime, **codeLess** renders its workbench, syntax-highlighted virtualized editor canvas, minimap, diff review hunks, and truecolor VT100 terminal directly through native C# graphics pipelines (**Win32 / GDI+ on `.NET Framework 4.8`** for a **`~1010 KB` standalone Windows `.exe`**, and **`.NET 8 + Avalonia Skia`** for native macOS and Linux desktop bundles). It pairs **VS Code keybindings** with a **Bring-Your-Own-Key (BYOK) autonomous coding agent** and **5 model-family-tuned LLM harnesses**—with zero subscription lock-in, zero intermediary proxy servers, and zero telemetry.

---

## Measured Footprint & Performance

| Metric | Measured Value |
| :--- | :--- |
| **Windows Standalone Executable (`codeLess.exe`)** | **`1010 KB`** (portable single `.exe` with `codeLess.Core.dll` embedded) |
| **Shared Core Engine (`codeLess.Core.dll`)** | **`212 KB`** (`net48` / `netstandard2.0` / `net8.0`) |
| **Windows Setup Installer (`codeLess-Setup.exe`)** | **`2.39 MB`** (Inno Setup LZMA2, per-user `PrivilegesRequired=lowest`) |
| **macOS Self-Contained Disk Image (`.dmg`)** | **`47.5 MB`** (`osx-arm64`) / **`48.3 MB`** (`osx-x64`) *(bundles `.NET 8` + Skia runtime)* |
| **Managed (.NET GC) Heap (Full Workbench Loaded)** | **`~30 MB`** |
| **Total Memory Footprint, macOS (Retina, Metal, incl. GPU surfaces)** | **`~230 MB`** steady · **`~260 MB`** startup peak *(Activity Monitor "Memory")* |
| **20,000-File Repository Async Open** | **`30 ms` return**, **`23 ms` first tree paint** (non-blocking background indexing) |
| **6 MB (`70,000+`-Line) Source File Open** | **`< 200 ms`** (viewport-virtualized line rendering) |

---

## Architectural Comparison: codeLess vs. Other IDEs

| Dimension | Electron / Chromium IDEs *(VS Code, Cursor, Windsurf)* | JVM Desktop IDEs *(IntelliJ Platform)* | **codeLess** |
| :--- | :--- | :--- | :--- |
| **UI & Rendering Stack** | Chromium DOM/Canvas + bundled V8 + Node.js multi-process host | Bundled Java Runtime (JBR) + Swing/AWT | **Windows**: Native Win32 + GDI+ (`csc.exe` on OS-built-in `.NET 4.8`)<br>**macOS / Linux**: Native `.NET 8` + Skia (`Avalonia`) |
| **Bundled Browser / Node Runtime** | Bundles Chromium + Node.js in every installation | Bundles custom Java runtime | **None** (`0` Chromium processes; optional system `node` only if activating JS `.vsix` scripts) |
| **Windows Portable Binary Size** | Multi-hundred-MB application directory | Multi-hundred-MB installation | **`1010 KB` single portable `codeLess.exe`** (`2.39 MB` installer) |
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

- **Viewport-Virtualized Code Editor**: Syntax highlighting across major languages, code folding (brace and Python indentation blocks), `76px` token minimap, breadcrumbs (`folder › file › symbol`), inline Code Lens complexity hints, multi-cursor editing (`Ctrl/Cmd+D`, `Ctrl/Cmd+Shift+L`, `Alt+Click`, rectangular box selection), and auto-closing pairs.
- **Inline Diff Staging & Time-Travel Checkpoints**: Review AI edits directly in the editor with per-hunk **Accept** / **Reject** controls and instant rollback via disk-backed **Time-Travel Checkpoints** (`~/.codeless/checkpoints`).
- **Byte-Accurate Git Integration**: Preserves `CRLF`, `LF`, `UTF-8 BOM`, and trailing-newline metadata across saves, formatting, search/replace, and AI diff staging so single-line edits produce clean 1-line `git diff` hunks. Includes sidebar Source Control (stage, unstage, discard, commit) and live gutter diff bars.
- **Native Integrated Terminal (ConPTY & Unix PTY)**: Interactive shells (`pwsh`, `powershell`, `cmd`, `zsh`, `bash`, `WSL`) with 24-bit Truecolor ANSI/VT100 rendering, alternate screen (`?1049h`), 5,000-line scrollback, split terminal panes (`Ctrl+Shift+5`), multiple named sessions, and `file:line` click-to-jump.
- **11 Built-In Studio Themes + VSIX / OpenVSX Extensions**: Ships 11 dark and light themes (`Savatarr Graphite` (default), `codeLess Obsidian Pro`, `Tokyo Night Storm`, `Catppuccin Mocha`, `Sublime Mariana`, `Solarized Light`, `GitHub Light Daylight`, and more), plus `.vsix` theme/snippet installation and OpenVSX registry search.

---

## Installation & Building from Source

### Direct Pre-Built Downloads ([Latest Release](https://github.com/Sambhrantsingh777/codeLess/releases/latest))

| Platform | Package | Direct Download |
| :--- | :--- | :--- |
| **Windows 10 / 11 (x64)** | Setup Installer (`.exe`, ~2.4 MB) | [`codeLess-Setup.exe`](https://github.com/Sambhrantsingh777/codeLess/releases/latest/download/codeLess-Setup.exe) |
| **Windows 10 / 11 (x64)** | Windows Installer Package (`.msi`, ~524 KB) | [`codeLess-Setup.msi`](https://github.com/Sambhrantsingh777/codeLess/releases/latest/download/codeLess-Setup.msi) |
| **Windows 10 / 11 (x64)** | Portable Standalone (`.exe`, ~966 KB) | [`codeLess.exe`](https://github.com/Sambhrantsingh777/codeLess/releases/latest/download/codeLess.exe) |
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
