# Agent trust: verification, auto-repair, Best-of-N, per-turn checkpoints

Same behaviour on Windows (WinForms) and macOS/Linux (Avalonia); the logic lives in `src/codeLess.Core/Agent*.cs`.
Nothing the agent proposes is written to your files until you accept it.

## Verification gate

Setting: **Off** (default) / **Verify** / **Verify + Auto-Repair** — chat composer chip (Avalonia), Help ▸ Agent
Verification (Windows), or the command palette ("Agent: …").

After a turn proposes edits, codeLess builds a temporary copy of the workspace with the staged edits applied, runs the
verification command there and shows the result in the chat and the review bar ("✓ build passed", "✗ 3 errors" with
clickable diagnostics). In Verify + Auto-Repair mode a failure is sent back to the model (diagnostics, output tail and
the staged files) for at most N repair turns (default 2, max 5). The loop also stops when the model makes no further
change, returns no usable edit, on Stop/cancel, and on failures code can't fix (timeout, tool missing, approval
denied). Acceptance is always yours; **Accept if Verified** applies the change set only when the latest verification
passed for exactly the current proposals (a rejected file or an edit made in the editor afterwards invalidates it).

### Which command runs

1. `.codeless/verify` in the workspace (first non-empty, non-`#` line)
2. `"codeless.verifyCommand"` in `.vscode/settings.json`
3. the user setting (Verification Command…)
4. the default build task, else the default test task, of `.vscode/tasks.json` (background / `dependsOn` tasks skipped)
5. auto-detected: `dotnet build`, `cargo check`, `go build ./...`, `npm test` (npm's placeholder test script is
   skipped) or `npm run build`, `python -m pytest -q`, `make build|all|check|test`

The command is always resolved from the real workspace before staged edits are applied, so the model can't choose it.
Problem matchers: the task's own, else one guessed from the tool (`$msCompile`, `$tsc`, `$rustc`, `$go`, `$python`,
`$gcc`, `$eslint-stylish`; unknown scripts try the common compiler formats). Pytest `FAILED file::test - msg` lines are
reported too. Timeout: 300 s (setting). The process gets `CI=true` and MSBuild node reuse / shared compilation off so no
build servers outlive the run; the whole process tree is killed on timeout or Stop.

### Approval

Verification executes your project's build/test including code the AI just wrote, so it uses the same gate as the
agent's `run_terminal`: a repository-chosen command (sources 1, 2, 4, 5) asks once (Run Once / Always Allow in This
Workspace); a run whose staged edits touch build-control files (`package.json`, `*.csproj`, `Makefile`, `build.rs`,
`conftest.py`, `pyproject.toml`, `tasks.json`, …) always asks and can't be "always allowed". Commands you configured
yourself don't ask. With "Require agent tool approval" off nothing asks (same as `run_terminal`). Headless / unattended
runs never execute without an answer.

### The temporary copy

- **git repository** (with at least one commit): `git worktree add --detach` of HEAD with hooks and LFS smudge
  disabled, then your uncommitted work (modified, added, untracked-not-ignored, deleted files) is copied over, then the
  staged edits are written. Copied bytes are capped (300 MB). A workspace inside a bigger repo maps through the
  subfolder.
- **otherwise**: a filtered copy of the tree (APFS clones on macOS), skipping `.git`, `bin`, `obj`, `target`, `dist`,
  `build`, `out`, caches; capped at 40 000 files / 300 MB, files over 64 MB skipped.
- `node_modules`, `.venv`, `venv`, `Pods`, `bower_components`, `.yarn` (up to 3 levels deep) are linked, not copied
  (symlink on macOS/Linux, junction on Windows). Links are always removed without following them.
- Copies live in `~/.codeless/verify-overlays/<id>` with a marker file and are deleted after each run; copies left by a
  crash or kill are removed at the next start (and `git worktree prune` cleans the repository's worktree list).

Limits: build outputs are not shared, so compiled projects build from scratch in the copy (package caches such as
`~/.nuget`, `~/.cargo`, Go's module cache are shared); git submodules are not populated; LFS files are pointers unless
modified locally; tools that write into linked folders (e.g. `node_modules/.cache`) write into your real folder; the
copy is not a security sandbox — approval is the safety boundary.

## Best-of-N

"Best of N" in the composer (Avalonia), "Best-of-N Arena" in the command palette (both) and the Help menu (Windows). The request runs concurrently on N candidates
(default 2): the active provider plus other configured providers, else other models / harnesses of a configured one.
Offline Mode uses local endpoints only (and the network layer blocks anything else). Candidates run on isolated engine
copies with the terminal tool disabled; each candidate's edits are verified (one verification at a time to keep CPU and
memory low). The comparison shows result, diff size, latency and an estimated cost (local = free; unknown models
"n/a"); passed candidates rank first, then smaller diffs, then faster. **Adopt** turns a candidate into a normal
reviewable change set — still nothing is written.

## Per-turn checkpoints

Every agent turn that changes files is recorded in `~/.codeless/checkpoints/turns/<workspace-hash>/` (timeline JSON +
content-addressed blobs, last 50 turns, survives restarts): accepted edits (including manual saves after per-hunk
review) and files changed by the turn's terminal commands (git status + HEAD blobs in a repository; a size/mtime +
content snapshot of up to 32 MB otherwise). Ignored folders (`node_modules`, build output) are not tracked.

"Undo This Turn" / "Undo Back to Here" (chat cards, the Agent Timeline, or the Restore button on your message)
show a confirmation listing every file (restore / delete / recreate, renames shown as such). A file that changed after
the turn — your edits, a later turn, or unsaved editor changes — is a conflict: it is kept unless you choose
**Overwrite My Changes**, and an overwritten version is kept as a backup that **Redo** restores. Redo re-applies the
last undo if the files still match. Non-git workspaces over the snapshot budget list files changed by commands but can't
restore them ("no backup").
