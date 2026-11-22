# codeLess v3.3.1

The biggest release yet: VS Code-level code intelligence and debugging without Electron, a trustworthy AI agent, and a
quality pass that fixed well over a hundred bugs. Every feature ships on Windows, macOS and Linux.

## New

- **Built-in language servers (LSP)** — completion with docs and snippets, hover, signature help, go to definition /
  type definition / implementation, find references, rename across files, format document/selection, quick fixes,
  live diagnostics and outline. Servers are auto-detected (pyright, typescript-language-server, gopls, rust-analyzer,
  clangd, csharp-ls, jdtls, …); a one-time hint offers the install command when one is missing. No extensions needed.
- **Debugger (DAP)** — Run and Debug view, breakpoints with conditions / hit counts / logpoints, variables, watch, call
  stack, debug console, `launch.json` with `preLaunchTask`, debugpy / dlv / lldb-dap / codelldb / netcoredbg.
- **Zero-setup Python** — interpreter picker (venv, uv, poetry, pipenv, conda, pyenv), Run/Debug Python File, and
  automatic venv activation in new terminals.
- **Verified AI agent** — AI edits can be built/tested in a private copy of your workspace before you accept them
  (your files are never touched), with optional auto-repair, "Accept if Verified", Best-of-N across models with a ranked
  comparison, and per-turn undo/redo that also restores files the agent's commands created or deleted.
- **Git, built in** — inline blame, commit graph and file history, branch picker, Sync/Pull/Push/Publish, merge-conflict
  "Accept Current / Incoming / Both", stage or revert a single hunk from the gutter.
- **Tasks** — `tasks.json`, Run Build Task (Cmd/Ctrl+Shift+B), auto-detected npm/make/dotnet/cargo/go/pytest tasks and
  problem matchers feeding the Problems panel.
- **Editor** — real word wrap, sticky scroll, bracket-pair colours and guides, indent guides, occurrence highlight,
  HTML/JSX tag auto-close and linked rename, Emmet, TextMate grammars (18 bundled + grammars from VSIX extensions),
  scrollbar with overview ruler, indentation detection and `.editorconfig`, word-based suggestions, inline diff review
  with deleted-line zones.
- **Huge files and big repos** — multi-GB files open instantly in a read-only viewer with search and live tail;
  `.gitignore`-aware search and Quick Open across 50k+ files.
- **Privacy you can see** — every outbound request is listed in View → Network Activity; Offline Mode blocks everything
  except localhost (local models keep working). Zero telemetry.
- **Workspace Trust** — opening an unfamiliar folder asks before anything from it can run (Restricted Mode), like VS Code.
- **More** — `.http` request runner, native Markdown preview, VSIX themes/snippets on macOS/Linux, a cleaner VS Code-like UI
  with a searchable Settings view, and Finder/Dock launches that pick up your shell PATH (nvm, pyenv, Homebrew).

## Notable fixes

Search results open the file again; Cmd/Ctrl+Click follows imports; the I-beam cursor and VS Code mouse behaviours;
per-hunk Reject no longer loses lines; AI terminal commands no longer touch unaccepted files; Replace All in files no
longer joins lines; case-only renames are safe; non-UTF-8 files keep their encoding; Offline Mode checks every redirect;
typing on 50k-line files is ~3× faster with ~99% fewer allocations.

## Trust and verification

Releases include GitHub build-provenance attestations and a Sigstore-signed `SHA256SUMS.txt`; Linux packages are also
OpenPGP-signed (`docs/codeLess-release-signing-key.asc`). See `docs/SIGNING.md`.

## Upgrade notes

- Previously opened folders ask for Workspace Trust once; in Restricted Mode git, tasks and workspace tools stay off
  until you trust the folder.
- Windows SmartScreen and macOS Gatekeeper may still warn until code-signing certificates are in place.
