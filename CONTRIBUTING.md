# Contributing to codeLess

Thank you for contributing to **codeLess** ([`https://github.com/Sambhrantsingh777/codeLess`](https://github.com/Sambhrantsingh777/codeLess))!

## Repository Structure

| Path | Description |
| :--- | :--- |
| `src/codeLess.Core/` | Shared multi-targeted core library (`net48`, `netstandard2.0`, `net8.0`): models, themes, workspace search (`WorkspaceSearchEngine.cs`), syntax verification (`SyntaxVerifier.cs`), JSON compatibility (`JsonCompat.cs`). |
| `src/LlmHarnessAndAgentEngine.cs` | Multi-profile LLM harness, BM25 + 1-hop AST symbol indexer, surgical `SEARCH/REPLACE` patcher, 8-tool autonomous agent loop, and SSE streaming client. |
| `src/ConPtyTerminal.cs` | Native Windows `CreatePseudoConsole` (ConPTY) + Unix `forkpty` (`libc`/`libutil`) + VT100/xterm truecolor terminal buffer. |
| `src/VsixAndPluginHostEngine.cs` | `.vsix` archive installer, OpenVSX registry search, theme/snippet loader, and clean-room `vscode.*` extension shim. |
| `src/CodeLessMainForm.cs`, `src/CodeLessEditorCanvas.cs`, `src/CoreModelsAndThemes.cs`, `src/StudioDialogs.cs` | Native Windows workbench (`.NET Framework 4.8` + custom GDI+ virtualized editor canvas, compiles to a `< 1 MB` standalone `.exe`). |
| `src/codeLess.Avalonia/` | Cross-platform desktop workbench (`.NET 8.0` + `Avalonia 11.1.3`) for macOS (`osx-arm64`, `osx-x64`), Linux (`linux-x64`, `linux-arm64`), and Windows. |
| `tests/codeLess.Avalonia.Tests/` | Automated `Avalonia.Headless.XUnit` verification suite (32 tests). |

## Building from Source

### 1. Cross-Platform Solution (`.NET 8.0` — Windows, macOS, Linux)
```bash
dotnet build codeLess.sln -c Release
```

### 2. Native Windows Standalone Executable (`codeLess.exe`)
On Windows (uses the built-in OS `.NET Framework 4.8` `csc.exe` compiler—no SDK install required):
```powershell
.\Compile.ps1
```
To also build and install locally to `%LOCALAPPDATA%\Programs\codeLess` (requires Inno Setup 6):
```powershell
.\Compile.ps1 -Install
```

### 3. macOS `.app` & `.dmg` Bundle (`osx-arm64` / `osx-x64`)
```bash
chmod +x ./build-mac.sh
./build-mac.sh          # builds for host architecture
./build-mac.sh both     # builds both Apple Silicon and Intel bundles
```

### 4. Linux Native Bundle & Debian `.deb` Package
```bash
chmod +x ./cross-platform/build-linux.sh
./cross-platform/build-linux.sh
```

## Running Tests Before Submitting a PR

```bash
dotnet test codeLess.sln -c Release
```
On Windows hosts, also verify that `.\Compile.ps1 -SkipInstaller` compiles `codeLess.Core.dll` and `codeLess.exe` with zero errors.

## Coding Guidelines
- **Minimal Dependencies**: Keep binaries small and fast; avoid introducing webview/Chromium runtimes or heavy external dependencies.
- **Preserve File Metadata**: File read/write paths must preserve original encoding, UTF-8 BOM state, CRLF vs. LF line endings, and trailing-newline state via `EditorTabBuffer.ReadFilePreservingMetadata` / `WriteFilePreservingMetadata`.
- **Clean Commits**: Never commit personal paths, API keys, or binary build outputs (`bin/`, `obj/`, `Output/`, `dist/`).
