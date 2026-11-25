# codeLess Memory Architecture & Profile (`W4`)

This document records the measured memory profile of `codeLess`, the top managed heap retainers identified before optimization, the leak fixes and lazy-subsystem changes applied across both the Avalonia (`src/codeLess.Avalonia`) and WinForms (`src/*.cs`) workbenches, and the runtime/GC configuration.

---

## 1. Where Memory Goes — Measured Before vs. After

All measurements below were captured deterministically in-process using the headless memory profiler test `MemoryProfileAndLeakTests.MemoryProfile_EndToEnd_StepsAndRetainers` (`tests/codeLess.Avalonia.Tests/MemoryProfileAndLeakTests.cs`) on macOS (Apple Silicon, .NET 8.0.428) via:

```bash
zsh /tmp/codeless-work/tools/cl-test.sh /tmp/codeless-work/mem "MemoryProfileAndLeakTests"
```

At each step, pending UI jobs are drained (`Dispatcher.UIThread.RunJobs()`), a compacting full GC is triggered (`GCSettings.LargeObjectHeapCompactionMode = CompactOnce`, `GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true)`), and `GC.GetTotalMemory(true)`, `GC.GetGCMemoryInfo()`, and `GC.GetAllocatedBytesForCurrentThread()` are recorded.

### Step-by-Step Managed Heap Profile

| Step | Workload | Before (Live Heap) | Before (Step Delta) | After (Live Heap) | After (Step Delta) | Change |
|---|---|---:|---:|---:|---:|---:|
| **0. Baseline** | Test host before creating any window | `0.69 MB` (`723,568 B`) | — | `2.74 MB` (`2,873,696 B`) | — | — |
| **1. Empty Window** | `new MainWindow(null)` + `Measure`/`Arrange` (1280×800) | `18.11 MB` (`18,994,000 B`) | `+17.42 MB` (alloc `21.24 MB`) | `17.61 MB` (`18,460,184 B`) | **`+14.86 MB`** (alloc `17.51 MB`) | **-14.7% delta / -17.6% alloc** |
| **2. Open Workspace** | `LoadWorkspace` on this repository (~398 tracked files) | `95.35 MB` (`99,985,608 B`) | `+77.24 MB` | `18.07 MB` (`18,944,288 B`) | **`+0.46 MB`** | **-99.4% workspace delta** |
| **3. Open 10 Files** | Open 10 largest `.cs` files in `src/codeLess.Core` (12,447 lines) + syntax prep | `109.71 MB` (`115,041,056 B`) | `+14.36 MB` | `28.20 MB` (`29,565,200 B`) | **`+10.13 MB`** | **-74.3% total live heap** |
| **4. Open Panels** | Search (`"MainWindow"`, 388 hits), SCM status, Terminal, AI Chat dock | `114.92 MB` (`120,498,840 B`) | `+5.20 MB` | `29.24 MB` (`30,665,128 B`) | **`+1.05 MB`** | **-79.8% panel delta** |
| **5. Second Window** | Open a second `MainWindow(null)` (1280×800) | `131.31 MB` (`137,689,936 B`) | `+16.39 MB` | `43.22 MB` (`45,322,416 B`) | **`+13.98 MB`** | **-14.7% per-window delta** |
| **6. Close All** | Close all tabs, workspace, and both windows (`WeakReference` check) | `126.43 MB` (`132,570,672 B`) | `+125.74 MB` vs. base (`w1/w2/tab` leaked) | `6.70 MB` (`7,020,728 B`) | **`+3.95 MB`** vs. base (`w1/w2/tab` collected) | **-94.7% post-close heap** |

### Micro-Allocations on Hot Paths

| Operation | Before | After |
|---|---:|---:|
| `StudioTheme.GetById(DefaultThemeId)` × 50 calls | `1,994,000 B` (~1.99 MB) | **`0 B`** |
| `MainWindow` window icon + Welcome page logo `Bitmap` per window | 3 decoded bitmaps / window | **1 shared `WindowIcon` + 1 shared `Bitmap` process-wide** |

---

## 2. Top Retainers Identified & How Each Was Fixed

### Retainer 1: Eager BM25 & Workspace Symbol Indexing + Per-Call `RegexOptions.Compiled` (`+77.24 MB` on `LoadWorkspace`)
- **Root cause**:
  1. `MainWindow.LoadWorkspace` eagerly invoked `aiEngine.IndexWorkspace(workspaceRoot, workspaceFiles)` in non-desktop mode, scanning and chunking every source file into 4,067 `Bm25CodeChunk`s and 4,000 `WorkspaceSymbolLocation`s before any AI or workspace-symbol feature was used.
  2. `LlmHarnessAndAgentEngine.IndexWorkspace` instantiated a new `Regex(..., RegexOptions.Compiled)` (`extraMethodRegex`) on every call, generating dynamic IL assemblies that can never be unloaded, and stored un-pooled token strings in every chunk's `TermFreq` dictionary.
- **Fix**:
  - `LoadWorkspace` now records candidate files and leaves `workspaceIndexState = 0` until first use.
  - `LlmHarnessAndAgentEngine` exposes `OnEnsureIndexed` wired to `MainWindow.EnsureWorkspaceIndexed`; reading `IndexedChunks`, `WorkspaceSymbols`, `WorkspaceSymbolMap`, `ProjectRulesText`, or `CompactRepoMap` triggers indexing lazily on first access.
  - `SharedExtraMethodRegex` and `SharedControlKeywords` are hoisted to `private static readonly` fields, and `IndexWorkspace` pools token strings via `termPool` so identical terms across chunks and `docFreq` share a single `string` instance.

### Retainer 2: Running `DispatcherTimer`s & Static Event Subscriptions Rooted Closed `MainWindow`s (`126.43 MB` retained after close)
- **Root cause**:
  1. `InitializeDevTools()` unconditionally started a 400 ms `devToolsPoll` `DispatcherTimer` on every window, and `MainWindow.OnClosed` only stopped `debouncedUiSyncTimer`, leaving `devToolsPoll`, `blameDebounce`, `markdownDebounce`, `blameHoverTimer`, `debugHoverTimer`, `fsChangeDebounceTimer`, `autoFetchTimer`, `statusMessageTimer`, `searchOnTypeTimer`, `taskProblemsDebounce`, `autoSaveTimer`, `updatePeriodicTimer`, and editor canvas timers enabled on the Avalonia `Dispatcher`.
  2. Calling `Close()` on a `MainWindow` that had not been shown via `Show()` (common in headless tests) bypassed `Window.OnClosed`, leaving the window subscribed to static events (`IdeSettingsWatcher.OnSettingsFileChanged`, `IdePersistentSettings.OnSettingsSavedInProcess`, `App.WindowManager`).
- **Fix**:
  - `MainWindow.Close()` ensures `OnClosed` runs even when the window was never shown on screen.
  - `MainWindow.OnClosed` is idempotent and stops all window timers, progress bars, and editor canvas timers/caches (`StopAllTimersAndCaches()`), unsubscribes all static events, clears `NativeMenu`, `FocusManager`, and `Content`, and triggers `ProcessMemoryTrimmer.NotifyLargeFree()`.
  - `devToolsPoll` is no longer started at startup unless inline or file git blame is enabled (`UpdateDevToolsPollTimer()`).

### Retainer 3: Closed `EditorTabBuffer` Retained by `editorMru`, Editor Canvases, and LSP Bindings (`tab_alive=True` after `CloseTab`)
- **Root cause**:
  - `MainWindow.CloseTab` (Avalonia) and `CodeLessMainForm.CloseTabBuffer` (WinForms) did not remove the closed buffer from `editorMru` / `editorMruWin`.
  - Editor canvas fields (`occBuf`, `guideBuf`, `animBuf`, `lastRevealBuf`, `widthLinesRef`, `zonesHunksRef`, `AvaloniaCaretOverlay.lastBuf`, and WinForms `wfOccBuf`, `wfGuideBuf`, `wfAnimBuf`, `wfRevealBuf`, `wfCaretBuf`), `LspEditorBinding` (`noWordsBuf`, `popupOwner`), and `MainWindow` fields (`pendingDebouncedBuffer`, `lastRenderedTabRef`, `lastPolledBuffer`, `httpDocBuffer`, `pendingMarkdownBuffer`, `lastDebugDecoratedTab`, `hunkPopupBuffer`, `focusedEditorBufferForAutoSave`, `pendingAutoSave`) retained references to the last active `EditorTabBuffer`.
- **Fix**:
  - Added `MainWindow.ReleaseClosedTabReferences(EditorTabBuffer)` (and WinForms `ReleaseBufferReferencesWF(EditorTabBuffer)` + `editorMruWin.Remove(captured)`), clearing all editor, overlay, LSP binding, and MRU references when a tab closes.
  - `AuxPaneHost.Close(AuxPaneDocument)` clears `Closed`, `ApplyTheme`, and `Content` references on closed side-pane documents.

### Retainer 4: Quadrupled Text & Line Storage per Open `EditorTabBuffer` (`+14.36 MB` -> `+10.13 MB` for 10 files)
- **Root cause**:
  - Opening a clean git-tracked file stored separate `Content` and `GitHeadContent` full-file strings and split `Content`, `SavedContent`, and `GitHeadContent` three separate times into `Lines`, `savedLinesCache`, and `_cachedBaseLines`, then ran an O(N) line diff across identical lines.
- **Fix**:
  - `EditorTabBuffer.SyncLinesFromContent()` normalizes `raw` once, shares the `raw` string reference with `SavedContent` and `GitHeadContent` when their normalized text matches, and shares the `SplitNormalizedLines(raw)` array across `savedLinesCache` and `_cachedBaseLines`.
  - `EditorTabBuffer.RefreshGutterDiff()` short-circuits in O(1) when `!contentStale && ReferenceEquals(baseText, contentText)`.

### Retainer 5: Eager Subsystem Instantiation & Duplicate Theme/Bitmap Allocations
- **Root cause**:
  - Every `MainWindow` eagerly constructed `LlmHarnessAndAgentEngine`, `AgentTrustCoordinator`, `DebugController`, `AvaloniaDebugSidebar`, `AvaloniaDebugConsole`, `AvaloniaDebugToolbar`, and `GitBlameService` in the constructor even when unused.
  - `StudioTheme.GetBuiltInThemes()` allocated 11 new `StudioTheme` objects and parsed 250+ hex color strings on every `StudioTheme.GetById` call (~40 KB per call).
- **Fix**:
  - Subsystems are now lazy (`EnsureAiEngine`, `EnsureAgentTrust`, `EnsureDebugController`, `EnsureDebugSidebarRealized`, `EnsureDebugConsoleRealized`, `EnsureDebugToolbarRealized`, `EnsureBlameService`), verified by `MemoryProfileAndLeakTests.Subsystems_AreLazyAtStartup_AndConstructedOnFirstUse`.
  - `StudioTheme` caches immutable built-in instances in `_cachedBuiltInThemes` and resolves IDs via `ResolveThemeFromShared` with zero allocations.
  - Window icons (`s_sharedWindowIcon`) and welcome-page bitmaps (`s_sharedWelcomeLogoBitmap`) are decoded once and shared across all windows.

---

## 3. Runtime, GC, and Avalonia Configuration Evaluation

Evaluated in `src/codeLess.Avalonia/codeLess.Avalonia.csproj` and `src/codeLess.Avalonia/Program.cs`:

| Option | Setting | Measured Effect & Rationale |
|---|---|---|
| `ServerGarbageCollection` | `false` | Workstation GC keeps per-heap segment reserves small (~5 MB baseline heap vs. 32+ MB with Server GC on 8-core Apple Silicon). |
| `ConcurrentGarbageCollection` | `true` | Background Gen 2 collections avoid UI frame stalls during editor scrolling and typing while keeping steady-state heap compact. |
| `RetainVMGarbageCollection` / `System.GC.RetainVM` | `false` | Returns decommitted GC segments to the OS instead of caching virtual memory reserves after closing large files or workspaces. |
| `System.GC.ConserveMemory` | `9` | Instructs the .NET 8 GC to favor aggressive compaction and lower fragmentation over throughput (upgraded from `5`). |
| `TieredCompilation` & `TieredPGO` | `true` | Tier-0 quick JIT reduces startup codegen memory and time; Dynamic PGO optimizes hot editor rendering and tokenizer loops in Tier-1. |
| `UseSystemResourceKeys` | `true` | Strips verbose framework exception resource strings from trimmed/published framework assemblies. |
| `InvariantGlobalization` | `false` (evaluated, not enabled) | Tested; saves ~0.3 MB of ICU tables but breaks locale-aware date/number formatting and culture-sensitive string comparisons expected by user workspaces and tests. Left disabled. |
| `SkiaOptions.MaxGpuResourceSizeBytes` | Avalonia default (24 MB) / headless Skia | Evaluated; Avalonia's default Skia GPU resource cache cap bounds glyph atlas and surface memory on desktop while keeping 120 Hz scrolling smooth. |
| `ProcessMemoryTrimmer` (`CompactOnce`) | Enabled on Deactivated & Large Frees | Sets `GCSettings.LargeObjectHeapCompactionMode = CompactOnce`, forces a single compacting GC (throttled to ≤1/min on idle and ≤1/5s on large frees), and calls `malloc_zone_pressure_relief` (macOS) / `malloc_trim` (Linux). |

---

## 4. Process Architecture

`codeLess` runs as **1 process** at startup (plus **1 shell child process** when an integrated terminal session is running in desktop mode — 2 processes total, vs. 10+ in Electron-based editors). Language servers and git CLI commands run only on demand for the active workspace.

---

## 5. Regression Tests (`tests/codeLess.Avalonia.Tests/MemoryProfileAndLeakTests.cs`)

| Test | What It Verifies |
|---|---|
| `MemoryProfile_EndToEnd_StepsAndRetainers` | Profiles all 6 lifecycle steps; asserts 10-file heap delta `< 45 MB`, per-extra-window delta `< 18 MB`, 0 B theme lookup allocations, and `WeakReference` collection of closed windows/tabs. |
| `CloseTab_CollectsEditorTabBuffer_WeakReference` | Opening, syntax-preparing, and closing a tab collects its `EditorTabBuffer` (`WeakReference.IsAlive == false`) and empties `EditorMruOrder`. |
| `ClosePanel_CollectsAuxPaneDocumentAndTerminal_WeakReference` | Opening and closing a Markdown preview side-pane and terminal session collects `AuxPaneDocument`, `MarkdownPreviewView`, and `TerminalSessionEntry`. |
| `CloseWindow_CollectsMainWindow_WeakReference` | Opening a `MainWindow`, loading a workspace and file, and calling `Close()` collects the `MainWindow` instance (`WeakReference.IsAlive == false`). |
| `Subsystems_AreLazyAtStartup_AndConstructedOnFirstUse` | Verifies `IsAgentEngineCreated`, `IsAgentTrustCreated`, `IsDebuggerCreated`, `IsBlameServiceCreated`, `IsLanguageServerManagerCreated`, and `IsNetworkPanelCreated` are `false` at startup and after `LoadWorkspace`, and transition to `true` only on first access. |
| `SharedImmutableThemesAndIcons_ZeroDuplicateAllocations` | Verifies `StudioTheme.GetById` returns the same shared instance with `0 B` allocated over 100 calls, and multiple `MainWindow` instances share the same `WindowIcon`. |
