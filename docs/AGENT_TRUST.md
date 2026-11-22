# Agent trust: verification, auto-repair, Best-of-N, per-turn checkpoints

Same behaviour on Windows (WinForms) and macOS/Linux (Avalonia); the logic lives in `src/codeLess.Core/Agent*.cs`.
Nothing the agent proposes is written to your files until you accept it.

## Reviewing proposals (both UIs)

- **Inline review** (agent proposals opened in the editor, inline AI edits, code-block Apply): per-hunk **Accept** /
  **Reject** and **Accept All** / **Reject All** in the editor. Hunks are always recomputed from the text the proposal
  was compared with and the current buffer, so decisions compose in any order, **Reject** restores every removed line,
  **Reject All** keeps hunks you already accepted, and your own edits during review are never lost. Rejects are undoable.
- **One accept rule:** accepting puts the text in the editor. For an **agent change set** the accepted file is also
  written to disk and recorded in the turn checkpoint (from the chat card, the review bar or the editor's Accept All);
  per-hunk decisions only change the buffer until you save (the save is recorded too). Every **other inline proposal**
  (inline AI edit, code-block Apply) stays in the buffer as an unsaved change, exactly like VS Code's inline chat:
  Ctrl/Cmd+S or auto save writes it.
- **Auto save never writes a proposal that is still under review**; it saves once you accept.
- **Agent commands (`run_terminal`)** never write staged edits into your files: when the turn has staged edits, the
  command runs in a temporary copy of the workspace with those edits applied (the same copy verification uses) and the
  copy is deleted afterwards; files the command creates or changes in the copy are discarded (the model is told so,
  including which dependency folders, if any, are shared with your real ones - see "The temporary copy").
  Without staged edits the command runs in your workspace. One agent turn runs per chat at a time; Stop cancels it.

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

- **git repository** (with at least one commit): a private clone (`git clone --shared --no-checkout`, then a detached
  checkout of HEAD with hooks and LFS smudge disabled, then its remotes are removed). The clone has its own refs, stash,
  config and hooks: `git commit`, `git stash`, `git branch -f`, `git tag` or `git push` run in the copy can't change your
  repository (the copy has no remotes), and nothing is registered in your repository (no linked worktree, and codeLess
  never runs `git worktree prune` on it). Git objects are shared read-only through git's alternates mechanism, so the
  clone is fast and small. Then your uncommitted work (modified, added, untracked-not-ignored, deleted files) is copied
  over, then the staged edits are written. Copied bytes are capped (300 MB). A workspace inside a bigger repo maps
  through the subfolder.
- **otherwise**: a filtered copy of the tree (APFS clones on macOS), skipping `.git`, `bin`, `obj`, `target`, `dist`,
  `build`, `out`, caches; capped at 40 000 files / 300 MB, files over 64 MB skipped.
- `node_modules`, `.venv`, `venv`, `Pods`, `bower_components`, `.yarn` (up to 3 levels deep) get a **private
  copy-on-write clone** where the file system supports it (APFS `clonefile` on macOS - instant, no data copied; reflink
  on Linux btrfs / XFS). Installs or cache writes in the copy then stay in the copy. Where cloning isn't possible
  (Windows, ext4, a workspace on another volume) the folder is **shared** with your real one through a link (symlink /
  junction): the verification result and the agent are told so explicitly, and agent commands that would install or
  remove packages (`npm install`, `pip install`, `poetry add`, `rm -rf node_modules`, ...) are refused in the copy while
  a dependency folder is shared. Links are always removed without following them.
- Copies live in `~/.codeless/verify-overlays/<id>` with a marker file and are deleted after each run; copies left by a
  crash or kill are removed at the next start. Worktree entries created by older codeLess versions are removed by their
  exact path; your own worktrees are never touched.

Limits: build outputs are not shared, so compiled projects build from scratch in the copy (package caches such as
`~/.nuget`, `~/.cargo`, Go's module cache are shared); git submodules are not populated; LFS files are pointers unless
modified locally; on file systems without copy-on-write, tools that write into shared dependency folders (e.g.
`node_modules/.cache`) write into your real folder; the copy is not a security sandbox - approval is the safety
boundary, and in Restricted Mode (see [Workspace Trust](#workspace-trust)) verification doesn't run at all.

## Workspace Trust

When you open a folder for the first time, codeLess asks "Do you trust the authors of the files in this folder?"
(**Trust Folder** / **Restricted Mode**, optionally trusting the parent folder). Decisions are remembered per folder in
`workspace-trust.json` next to `settings.json`; Settings ▸ Workspace Trust (and the "Manage Workspace Trust" command)
lists trusted folders and lets you revoke them. In **Restricted Mode** codeLess never runs anything from the workspace
on its own: no language servers / interpreters installed in the project (`node_modules/.bin`, virtual environments), no
language servers that execute project code by design (rust-analyzer, jdtls, C#), no tasks, no debugging, no agent
verification, no venv auto-activation in new terminals, no git (repository config can run commands), and agent terminal
commands always ask. Global language servers from PATH keep working. A "Restricted Mode" status-bar item and a banner
offer **Manage**; trusting enables everything immediately, without a restart.

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
