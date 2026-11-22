# codeLess performance benchmarks

How codeLess behaves where editors usually struggle: multi-GB logs, big monorepos, and idle cost. Every number here
was measured on the machine below with the scripts in `bench/` (see `bench/README.md` for exact methods). Medians
unless noted. Numbers are not rounded in our favour; where something was not measured it says so.

**Machine:** Apple M3 (8 cores), 8 GB RAM, macOS 26.6.2, internal SSD, .NET SDK 8.0.428, Release build.
The machine was not idle (other build jobs and a user VS Code window were running), so treat small differences as noise.
**codeLess:** v3.3.0 (commit 3e4456d, Release, self-contained). **VS Code:** 1.147.0 (Electron 43.8.9), fresh
`--user-data-dir` / `--extensions-dir`, extensions disabled.

## Head-to-head: codeLess vs VS Code (measured 2026-11-22)

`python3 bench/run_bench.py --apps codeless,vscode --include-vscode -n 3 --huge-mb 300 --huge-iterations 1`, run with
nothing else heavy running (the user's own VS Code window stayed open, so absolute numbers include some noise; both apps
were measured under the same conditions, one at a time). Median of 3 (min – max). Workspace: a clone of this repository
(312 files) with `src/codeLess.Avalonia/MainWindow.cs` open.

| Metric | codeLess | VS Code | Difference |
|---|---|---|---|
| Cold start: launch → first frame with workspace + file | **993 ms** (926 – 1125) | 1089 ms (1086 – 1179) | ~9% faster |
| Idle memory, sum of phys_footprint (whole process tree) | **199 MB** (198 – 215) | 1062 MB (1053 – 1078) | **5.3× less** |
| Idle memory, sum of RSS | **214 MB** (213 – 219) | 1651 MB (1604 – 1652) | 7.7× less |
| Processes | **2** | 10 | |
| Idle CPU (% of one core, 30 s window) | **0.4 %** | 4.3 % (4.2 – 5.0) | ~10× less |
| Idle wake-ups per second | **75** (73 – 76) | 95 (93 – 96) | |
| Open a 100 MB file: request → content painted | **74 ms** (66 – 78) | 355 ms (310 – 367) | **4.8× faster** |
| Memory after opening the 100 MB file (phys_footprint) | **264 MB** (230 – 268) | 1398 MB (1397 – 1428) | 5.3× less |
| Open a 300 MB file: time to content | **75 ms** | 891 ms | 12× faster |
| Memory after opening the 300 MB file (phys_footprint) | **270 MB** | 1659 MB | 6.1× less |

Caveats, so nobody over-reads this:
- Cold start is only modestly faster; the large wins are memory, idle CPU and big files.
- codeLess opens files ≥ 50 MB in its read-only large-file viewer (search, go to line, live tail; "Open in Editor" up to
  128 MB). VS Code opened both files in its editor but turned off tokenization, wrapping, folding, code lens, word
  highlighting and sticky scroll for them.
- VS Code ran with extensions disabled; with typical extensions its memory is higher. codeLess's numbers include its
  built-in features (no extension host).
- One machine (Apple M3, 8 GB); results on Windows/Linux and other hardware will differ. Raw data:
  `bench/results/` (not committed) / reproduce with the command above.

## 1. Huge files (large-file viewer)

Files at or above 50 MB (`LargeFileThresholdMB`) or estimated above 2,000,000 lines (`LargeFileLineThreshold`) open in
a paged read-only viewer instead of being loaded into the editor buffer (`EditorTabBuffer.Lines`). A background thread
builds a sparse line index (one checkpoint per 64 lines or 64 KB); the UI reads only the visible lines through a
3 MB page cache. "Open in Editor" is offered up to `LargeFileEditLimitMB` (128 MB).

Synthetic service log (`bench/gen_logfile.py`, ~148 bytes/line, ASCII, LF). `bench/run_largefile_bench.sh "100 1024" 3`.
The file was in the OS page cache (just written); a cold read is bounded by SSD throughput and was not measured
(needs `sudo purge`).

| | 100 MB (709,314 lines) | 1 GB (7,265,557 lines) |
|---|---:|---:|
| `LargeFileDocument.Open` (encoding sniff, index thread started) | 0.2 ms | 0.2 ms |
| First 60 lines readable (while indexing) | 0.2 ms | 0.3 ms |
| Full line index | 42 ms | 191 ms |
| Index memory (managed) | 1.6 MB | 3.4 MB (113,525 checkpoints) |
| Peak process RSS while indexing (test host incl. ~98 MB baseline) | 103 MB | 100 MB |
| Open through the (headless) workbench, `OpenFileInTab` until the viewer is shown | 72 ms | 83 ms |
| Workbench RSS growth after open + full index | 2 MB | 4 MB |
| Go to random line + read a 60-line screen (200 jumps) | 0.02 ms (max 0.67) | 0.03 ms (max 0.62) |
| Search, case-sensitive literal (byte-level, streaming) | 42 ms | 295 ms (101,537 matches) |
| Search, ignore case | 84 ms | 839 ms |
| Search, regex `latency_ms=2[0-9]{3}\b` (line decoding) | 105 ms | 986 ms (135,569 matches) |
| Copy a 10,000-line selection | 5 ms | 14 ms |
| Tail -f: 10,000 appended lines indexed after `PollForChanges` | 6 ms | 6 ms |
| Truncation / rotation detected and re-indexed | 6 ms | 6 ms |

For comparison, the previous code path for the 100 MB file (whole file decoded into `Content` + `Lines`, no rendering,
same harness): **469 ms and +395 MB peak RSS** (managed heap +640 MB after the load). A 1 GB file was not attempted
that way (it would need several GB of RAM). 5 GB files were not tested: the safety rules for this machine allow one
test file of at most 1 GB.

Behaviour notes: the viewer is read-only, word wrap is always off, lines longer than 20,000 characters are truncated
for display (marked in the viewer), a lone CR is not treated as a line break, and only the first 200,000 search
matches are kept for navigation (all matches are counted). Encodings: UTF-8 (with/without BOM), UTF-16 LE/BE (BOM or
detected), other bytes shown as Latin-1.

## 2. Big monorepos

Synthetic repo from `bench/gen_monorepo.py --files 38000 --node-modules 10000`: 54,800 files on disk, of which 37,984
are tracked source files and 16,795 are ignored (`node_modules/`, `dist/`, `build/`, `generated/`, `*.log`, nested
`.gitignore`s). Headless, 5 runs, `bench/run_monorepo_bench.sh`. "Old" = the previous code path in the same build.

| | Old | New |
|---|---|---|
| Workspace file list (Quick Open / indexing) | 556 ms, **12,000 files (cap hit)**, depth limit 7 | 1,277 ms, **37,984 files (all)**, no depth limit |
| gitignored files in that list | 41 | 0 (one `!important.log` re-included by a negation, correct) |
| Deep Quick Open targets reachable | 0 / 2 | 2 / 2 |
| Quick Open per keystroke over 37,984 files (5 queries) | 10.4 ms median, 25.6 ms max | 6.0 ms median, 10.6 ms max |
| Quick Open memory for the file list | 35.2 MB (one palette entry + closure per file) | 26.2 MB (lower-cased index, entries only for the top 120) |
| Find in Files (walk + search, needle in 17 files) | 6,884 ms, 18 hits incl. 1 in an ignored folder (no .gitignore, sequential) | 3,341 ms, 17/17, 0 ignored (.gitignore, 4 parallel readers) |
| BM25 index of the 1,500-file candidate list | 226 ms, 24.4 MB | 186 ms, 22.9 MB |
| 10,000 files created + deleted inside `node_modules` | a full re-walk (~518 ms with the old walker) + tree refresh + git status per flush | 32,794 watcher events classified as ignored in 66 ms total; no re-walk, no git status; Explorer refreshed only if the folder is expanded |

The new walk is slower than the old one in absolute time because it walks 3.2x more files (per file it is ~34 µs vs
~46 µs; this file system needed ~25 µs per file even for `find`). Find in Files with `.gitignore` but sequential took
7,183 ms; the speed-up comes from parallel reads, the correctness fix from `.gitignore`.

Earlier run of the **real app with the old code** on a 240k-file version of the same generator (before the changes;
one run, measured with the in-app harness `CODELESS_MONOREPO_BENCH`, machine busy):
first Explorer paint 293 ms; background workspace scan 17.6 s; file list capped at 12,000; Quick Open did not find
either deep target (gave up after 15 s); Find in Files found 2 of 17 needle files (the default exclude list contained
`packages`, which is where a JS monorepo keeps its source); 2,000 new source files were still not visible in Quick Open
after 30 s (cap); the UI stalled up to 1.4 s during a watcher burst. These GUI numbers were not re-measured after the
changes because GUI runs are currently not allowed on this machine.

What changed: `.gitignore` support (root, nested, `.git/info/exclude`, negation, `**`, anchoring, dir-only) shared
by both UIs (`WorkspaceIgnoreRules`), `packages/` only excluded when it is a NuGet folder, 200k file cap with a status
message, incremental watcher updates (create/delete applied to the list; ignored paths dropped), Quick Open on a
lower-cased index with top-K selection, parallel batched Find in Files with deterministic result order. The Explorer
still shows ignored folders (dimmed, loaded lazily).

## 3. Idle cost

Cosmetic timers now run only while their effect is visible (`UiTimerGate`, headless tests in
`IdleTimerGateHeadlessTests`):

| Timer | Before | After |
|---|---|---|
| Editor caret blink (Avalonia 530 ms, WinForms 520 ms) | always running, also when the window was inactive or minimised | only while the editor has keyboard focus in an active, non-minimised window; steady caret otherwise |
| Terminal cursor blink (Avalonia 530 ms, WinForms 530 ms) | always running | only while the terminal is focused in an active window; hollow cursor otherwise |
| Progress bar animation (Avalonia 30 ms) | while "animating", even when hidden or minimised | only while animating and visible |
| Agent feed shimmer (WinForms 90 ms) | always running | only while a message is streaming |
| Status bar telemetry (WinForms 3.5 s) | always | only while the window is active |
| Autosave (WinForms 3 s) | always | only while a saved file is dirty and autosave is on |
| Large-file follow poll (500 ms) | n/a | only while Follow (tail -f) is on |

Effect on idle CPU / wakeups of the real app: **not measured** (requires launching the GUI; `bench/run_bench.py`
and the in-app probe `codeLess --idle-probe report.txt` measure it). Structurally, an inactive window with an editor
and a terminal used to wake the UI thread about 3.8 times per second for blinking alone; now 0.

## Reproducing

```bash
bench/run_largefile_bench.sh "100 1024" 3
bench/run_monorepo_bench.sh 38000 10000 5
python3 bench/run_bench.py -n 5                                   # codeLess GUI metrics
python3 bench/run_bench.py -n 5 --apps codeless,vscode --include-vscode   # + VS Code, idle machine only
```
