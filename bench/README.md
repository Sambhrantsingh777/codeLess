# codeLess benchmarks

Reproducible performance measurements. Results and methodology are summarised in `docs/BENCHMARKS.md`.
Nothing in here is shipped with the app; generated data goes to `/tmp` and `bench/results/` (git-ignored).

| Script | Launches a GUI? | What it measures |
|---|---|---|
| `run_largefile_bench.sh [sizes_mb] [runs]` | no (headless) | Large-file viewer engine: open, first screen, full line index, go to line, streaming search, copy, tail -f / truncation; opening through the headless workbench; the old "load into EditorTabBuffer" path for files <= 300 MB |
| `run_monorepo_bench.sh [files] [node_modules] [runs]` | no (headless) | Big repo: workspace walk (old vs new), Quick Open per-keystroke cost, Find in Files, BM25 candidate indexing, file-watcher burst inside `node_modules` |
| `run_bench.py` | **yes** | Real app: cold start to first rendered frame, idle memory (process tree RSS + phys_footprint), idle CPU, time to open a 100 MB file, behaviour with a 1 GB file. codeLess only by default; VS Code only with `--apps codeless,vscode --include-vscode` |

Helpers: `gen_logfile.py` (stream-writes a synthetic service log of N MB, never held in memory), `gen_monorepo.py`
(deterministic synthetic monorepo with `node_modules/`, `dist/`, nested `.gitignore` files, a git history and a
manifest of needle files / deep Quick Open targets), `proc_metrics.py` (process-tree RSS / phys_footprint / CPU via
`proc_pid_rusage`, no sudo), `cdp_client.py` (minimal Chrome DevTools client used to observe VS Code's renderer).

## Headless benchmarks (safe on small machines)

```bash
bench/run_largefile_bench.sh "100 1024" 3   # one file at a time, deleted right after its run
bench/run_monorepo_bench.sh 38000 10000 5    # ~55k files on disk, deleted afterwards
```

Both drive the shared engine through xunit tests that are skipped unless their environment variables are set
(`tests/codeLess.Avalonia.Tests/LargeFileBench.cs`, `MonorepoScaleBench.cs`). Timings are medians of `runs`.
Note: the large-file numbers are with the file in the OS page cache (it was just written); macOS needs `sudo purge`
for a cold cache, which the script does not do.

## GUI benchmark (`run_bench.py`)

Run it on an otherwise idle machine with enough free RAM (it launches the app repeatedly; with `--include-vscode` also
VS Code, which is several Electron processes). `python3 bench/run_bench.py -h` lists all options.

```bash
python3 bench/run_bench.py -n 5                                     # codeLess only (builds into /tmp/cl-bench-pub)
python3 bench/run_bench.py -n 5 --apps codeless,vscode --include-vscode
```

Method (per app, fresh profile per launch, same workspace folder and same file open):

* **codeLess** is published like `build-mac.sh` (self-contained, single file) into `/tmp/cl-bench-pub` unless
  `--codeless-bin` is given. Launched with an isolated `$HOME` and `CODELESS_SETTINGS_DIR`, plus
  `--bench-ready <file>` (written after the window opened and two chained `RequestAnimationFrame` callbacks, i.e. the
  first frame with the workspace and file has been committed; may overshoot by one frame) and
  `--bench-open-file/--bench-open-report` (opens a file through the normal `OpenFileInTab` path and stamps the next
  rendered frame). Hooks: `src/codeLess.Avalonia/MainWindow.BenchHooks.cs`.
* **VS Code** runs with a fresh `--user-data-dir` and `--extensions-dir` under `/tmp`, `--disable-extensions
  --skip-welcome --skip-release-notes --disable-workspace-trust --disable-telemetry --disable-crash-reporter
  --disable-updates --disable-experiments`, and a pre-seeded settings.json (updates, telemetry, experiments, startup
  editor off). Ready = max(first-contentful-paint, `didStartWorkbench`) read from the renderer over CDP (a lower bound
  that favours VS Code). File open = `code -r <file>` into the running instance, observed in the renderer until the
  text is painted (or the large-file warning is shown, which is recorded as such; by default the harness clicks
  "Open Anyway" and keeps waiting up to `--open-timeout`).
* **Memory** = sum over the whole process tree (helpers included) of RSS and of phys_footprint, sampled after
  `--settle` (30 s) idle. **CPU idle** = process-tree CPU time over a `--cpu-window` (30 s) idle window, as % of one core.
* Each metric is repeated `-n` times (first launch per app discarded as warm-up); medians, min and max are reported in
  `bench/results/<timestamp>/results.{json,md}` together with versions and hardware.
