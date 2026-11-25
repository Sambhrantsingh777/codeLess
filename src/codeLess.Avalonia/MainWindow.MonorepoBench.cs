using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;

namespace codeLess.AvaloniaApp
{
    /// <summary>
    /// Big-monorepo workload on the real desktop app (generate a repo with bench/gen_monorepo.py first):
    ///   CODELESS_MONOREPO_BENCH=report.txt CODELESS_MONOREPO_BENCH_WS=/tmp/cl-monorepo codeLess --workspace /tmp/empty
    /// Measures workspace open (first paint / background completion / UI stalls / memory), Explorer expand, Quick Open,
    /// Find in Files, the lazy BM25 index and the file watcher under an npm-install-like burst, then quits.
    /// </summary>
    public partial class MainWindow
    {
        // Perf counters (cheap, always on; read by the monorepo bench and tests).
        public int PerfWorkspaceLoadCompletions;
        public int PerfTreeRefreshCount;
        public int PerfReindexCount;

        public static bool IsMonorepoBenchRequested =>
            !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("CODELESS_MONOREPO_BENCH")) &&
            !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("CODELESS_MONOREPO_BENCH_WS"));

        public static void AttachMonorepoBenchIfRequested(MainWindow window)
        {
            if (!IsMonorepoBenchRequested || window == null) return;
            window.Opened += async (s, e) => await window.RunMonorepoBenchAsync(
                Environment.GetEnvironmentVariable("CODELESS_MONOREPO_BENCH"),
                Environment.GetEnvironmentVariable("CODELESS_MONOREPO_BENCH_WS"));
        }

        private sealed class BenchProbe : IDisposable
        {
            private readonly Process proc = Process.GetCurrentProcess();
            private readonly CancellationTokenSource cts = new CancellationTokenSource();
            private readonly DispatcherTimer ticker;
            private long lastTick;
            public long PeakRss;
            public double MaxUiGapMs;
            public TimeSpan Cpu0;
            public Stopwatch Sw = Stopwatch.StartNew();

            public BenchProbe()
            {
                proc.Refresh();
                Cpu0 = proc.TotalProcessorTime;
                PeakRss = proc.WorkingSet64;
                lastTick = Sw.ElapsedTicks;
                ticker = new DispatcherTimer(TimeSpan.FromMilliseconds(5), DispatcherPriority.Render, (s, e) =>
                {
                    long now = Sw.ElapsedTicks;
                    double gap = (now - lastTick) * 1000.0 / Stopwatch.Frequency;
                    if (gap > MaxUiGapMs) MaxUiGapMs = gap;
                    lastTick = now;
                });
                ticker.Start();
                var token = cts.Token;
                Task.Run(async () =>
                {
                    var p = Process.GetCurrentProcess();
                    while (!token.IsCancellationRequested)
                    {
                        p.Refresh();
                        long ws = p.WorkingSet64;
                        if (ws > Interlocked.Read(ref PeakRss)) Interlocked.Exchange(ref PeakRss, ws);
                        try { await Task.Delay(20, token); } catch { }
                    }
                });
            }

            public double CpuMs { get { proc.Refresh(); return (proc.TotalProcessorTime - Cpu0).TotalMilliseconds; } }

            public void Dispose()
            {
                ticker.Stop();
                cts.Cancel();
            }
        }

        private Task NextFrameAsync()
        {
            var tcs = new TaskCompletionSource<bool>();
            RequestAnimationFrame(_ => tcs.TrySetResult(true));
            return Task.WhenAny(tcs.Task, Task.Delay(1000));
        }

        private bool IsWorkspaceSearchRunningForBench() => false;
        private string LastIndexStatusForBench() => "n/a";

        private static long RssMb() { var p = Process.GetCurrentProcess(); p.Refresh(); return p.WorkingSet64 / 1048576; }

        private static long LiveManagedMbAfterGc()
        {
            GC.Collect(2, GCCollectionMode.Forced, true, true);
            GC.WaitForPendingFinalizers();
            return GC.GetTotalMemory(true) / 1048576;
        }

        private async Task<bool> WaitUntilAsync(Func<bool> cond, int timeoutMs)
        {
            var sw = Stopwatch.StartNew();
            while (!cond())
            {
                if (sw.ElapsedMilliseconds > timeoutMs) return false;
                await Task.Delay(2);
            }
            return true;
        }

        public async Task RunMonorepoBenchAsync(string reportPath, string ws)
        {
            var sb = new StringBuilder();
            void Log(string s) { sb.AppendLine(s); try { File.WriteAllText(reportPath, sb.ToString()); } catch { } }
            try
            {
                ws = Path.GetFullPath(ws);
                string manifestPath = ws.TrimEnd('/') + ".manifest.json";
                var manifest = System.Text.Json.JsonDocument.Parse(File.ReadAllText(manifestPath)).RootElement;
                string needle = manifest.GetProperty("needle").GetString();
                int needleFiles = manifest.GetProperty("needle_files").GetArrayLength();
                var qoTargets = manifest.GetProperty("quick_open_targets").EnumerateArray().Select(x => x.GetString()).ToList();
                await Task.Delay(2500);
                long rss0 = RssMb();
                long live0 = LiveManagedMbAfterGc();
                Log($"baseline: rss_mb={rss0} managed_live_mb={live0}");

                // ---- A. workspace open
                int loads0 = PerfWorkspaceLoadCompletions;
                using (var probe = new BenchProbe())
                {
                    LoadWorkspace(ws);
                    double tSync = probe.Sw.Elapsed.TotalMilliseconds;
                    await NextFrameAsync();
                    double tPaint = probe.Sw.Elapsed.TotalMilliseconds;
                    bool done = await WaitUntilAsync(() => PerfWorkspaceLoadCompletions > loads0, 240000);
                    double tDone = probe.Sw.Elapsed.TotalMilliseconds;
                    Log($"open: sync_ms={tSync:F0} first_paint_ms={tPaint:F0} background_done_ms={tDone:F0} completed={done} " +
                        $"cpu_ms={probe.CpuMs:F0} max_ui_stall_ms={probe.MaxUiGapMs:F0} peak_rss_mb={probe.PeakRss / 1048576} " +
                        $"workspace_files={workspaceFiles.Count} tree_root_children={(ExplorerTree.Nodes.FirstOrDefault()?.Nodes.Count ?? 0)}");
                }
                await Task.Delay(3000);
                Log($"open_steady: rss_mb={RssMb()} managed_live_mb={LiveManagedMbAfterGc()}");

                // ---- B. explorer expand (cold: first expansion in this process)
                var root = ExplorerTree.Nodes.FirstOrDefault();
                foreach (string name in new[] { "packages", "node_modules" })
                {
                    var node = root?.Nodes.FirstOrDefault(n => n.IsDirectory && n.Text == name);
                    if (node == null) { Log($"expand_{name}: missing"); continue; }
                    var sw = Stopwatch.StartNew();
                    ExplorerTree.ToggleFolderNode(node);
                    double tSync = sw.Elapsed.TotalMilliseconds;
                    await NextFrameAsync();
                    Log($"expand_{name}: sync_ms={tSync:F1} paint_ms={sw.Elapsed.TotalMilliseconds:F1} children={node.Nodes.Count} dimmed={node.IsDimmed}");
                    ExplorerTree.ToggleFolderNode(node); // collapse again
                }

                // ---- C. quick open
                {
                    var sw = Stopwatch.StartNew();
                    OpenQuickOpen();
                    double tOpen = sw.Elapsed.TotalMilliseconds;
                    await NextFrameAsync();
                    double tOpenPaint = sw.Elapsed.TotalMilliseconds;
                    Log($"quickopen_open: sync_ms={tOpen:F1} paint_ms={tOpenPaint:F1} entries={CommandPalette.AllEntries.Count}");
                    foreach (var (query, expect) in new[] { ("UniqueQuokka", "UniqueQuokkaWidget.tsx"), ("QuokkaLedgerController", "QuokkaLedgerController.cs"), ("invoice page", (string)null) })
                    {
                        sw.Restart();
                        CommandPalette.SearchInput.Text = query;
                        double tSync = sw.Elapsed.TotalMilliseconds;
                        bool found = await WaitUntilAsync(() => CommandPalette.FilteredEntries.Any(en =>
                            (en.Id ?? "").StartsWith("file:") && (expect == null || (en.Id ?? "").EndsWith(expect))), 15000);
                        double tFirst = sw.Elapsed.TotalMilliseconds;
                        await NextFrameAsync();
                        Log($"quickopen_query[{query}]: sync_ms={tSync:F1} first_result_ms={tFirst:F1} paint_ms={sw.Elapsed.TotalMilliseconds:F1} found={found} results={CommandPalette.FilteredEntries.Count(en => (en.Id ?? "").StartsWith("file:"))}");
                    }
                    CommandPalette.Close();
                }

                // ---- D. find in files
                for (int rep = 0; rep < 2; rep++)
                {
                    using var probe = new BenchProbe();
                    ExecuteWorkspaceSearch(needle);
                    double tSync = probe.Sw.Elapsed.TotalMilliseconds;
                    double tFirst = -1;
                    bool done = await WaitUntilAsync(() =>
                    {
                        if (tFirst < 0 && workspaceSearchHits.Count > 0) tFirst = probe.Sw.Elapsed.TotalMilliseconds;
                        return !IsWorkspaceSearchRunningForBench();
                    }, 120000);
                    double tDone = probe.Sw.Elapsed.TotalMilliseconds;
                    var files = workspaceSearchHits.Select(h => h.FilePath).Distinct().ToList();
                    int ignoredHits = files.Count(f => f.Contains("/node_modules/") || f.Contains("/dist/") || f.Contains("/generated/"));
                    Log($"find_in_files[{rep}]: sync_ms={tSync:F0} first_result_ms={tFirst:F0} done_ms={tDone:F0} completed={done} hits={workspaceSearchHits.Count} " +
                        $"files={files.Count}/{needleFiles} ignored_hits={ignoredHits} cpu_ms={probe.CpuMs:F0} max_ui_stall_ms={probe.MaxUiGapMs:F0} peak_rss_mb={probe.PeakRss / 1048576}");
                }

                // ---- E. BM25 / symbol index (lazy, background)
                {
                    long liveBefore = LiveManagedMbAfterGc();
                    using var probe = new BenchProbe();
                    await Task.Run(() => EnsureWorkspaceIndexed());
                    double t = probe.Sw.Elapsed.TotalMilliseconds;
                    var eng = EnsureAiEngine();
                    int chunks = eng.IndexedChunks?.Count ?? 0;
                    int filesIdx = eng.IndexedChunks?.Select(c => c.FilePath).Distinct().Count() ?? 0;
                    long peak = probe.PeakRss / 1048576;
                    probe.Dispose();
                    long liveAfter = LiveManagedMbAfterGc();
                    Log($"bm25: time_ms={t:F0} cpu_ms={probe.CpuMs:F0} files={filesIdx} chunks={chunks} symbols={eng.WorkspaceSymbols?.Count ?? 0} " +
                        $"peak_rss_mb={peak} managed_live_delta_mb={liveAfter - liveBefore} max_ui_stall_ms={probe.MaxUiGapMs:F0} status={LastIndexStatusForBench()}");
                }

                // ---- F. watcher burst: npm-install-like churn inside node_modules (external process), then source churn
                {
                    string script = "import os,sys,shutil\nroot=sys.argv[1]\nfor d in range(100):\n  p=os.path.join(root,'node_modules','.burst-%d'%d,'lib')\n  os.makedirs(p,exist_ok=True)\n  for f in range(100):\n    open(os.path.join(p,'f%d.js'%f),'w').write('module.exports=1;\\n')\nfor d in range(100):\n  shutil.rmtree(os.path.join(root,'node_modules','.burst-%d'%d))\n";
                    int tree0 = PerfTreeRefreshCount, reidx0 = PerfReindexCount;
                    using var probe = new BenchProbe();
                    var psi = new ProcessStartInfo("python3") { UseShellExecute = false };
                    psi.ArgumentList.Add("-c"); psi.ArgumentList.Add(script); psi.ArgumentList.Add(ws);
                    using (var p = Process.Start(psi)) { await Task.Run(() => p.WaitForExit()); }
                    double tGen = probe.Sw.Elapsed.TotalMilliseconds;
                    await Task.Delay(8000); // let debounce + any re-scan run
                    Log($"watcher_burst_node_modules: files=10000(created+deleted) gen_ms={tGen:F0} cpu_ms_incl_8s_settle={probe.CpuMs:F0} " +
                        $"max_ui_stall_ms={probe.MaxUiGapMs:F0} peak_rss_mb={probe.PeakRss / 1048576} tree_refreshes={PerfTreeRefreshCount - tree0} reindexes={PerfReindexCount - reidx0} workspace_files={workspaceFiles.Count}");
                }
                {
                    string dir = Path.Combine(ws, "packages", "pkg-burst", "src");
                    int tree0 = PerfTreeRefreshCount, reidx0 = PerfReindexCount;
                    using var probe = new BenchProbe();
                    var psi = new ProcessStartInfo("python3") { UseShellExecute = false };
                    psi.ArgumentList.Add("-c");
                    psi.ArgumentList.Add("import os,sys\nfor d in range(20):\n  p=os.path.join(sys.argv[1],'m%d'%d)\n  os.makedirs(p,exist_ok=True)\n  for f in range(100):\n    open(os.path.join(p,'BurstFile%d_%d.ts'%(d,f)),'w').write('export const x=1;\\n')\n");
                    psi.ArgumentList.Add(dir);
                    using (var p = Process.Start(psi)) { await Task.Run(() => p.WaitForExit()); }
                    string probeFile = Path.Combine(dir, "m19", "BurstFile19_99.ts");
                    bool indexed = await WaitUntilAsync(() => workspaceFiles.Contains(probeFile), 30000);
                    double tIdx = probe.Sw.Elapsed.TotalMilliseconds;
                    int burstCount = workspaceFiles.Count(f => f.StartsWith(dir, StringComparison.Ordinal));
                    Log($"watcher_burst_src_create: files=2000 visible_in_file_list={indexed} ms={tIdx:F0} count={burstCount} cpu_ms={probe.CpuMs:F0} max_ui_stall_ms={probe.MaxUiGapMs:F0} tree_refreshes={PerfTreeRefreshCount - tree0} reindexes={PerfReindexCount - reidx0}");
                    var sw = Stopwatch.StartNew();
                    Directory.Delete(Path.Combine(ws, "packages", "pkg-burst"), true);
                    bool removed = await WaitUntilAsync(() => !workspaceFiles.Contains(probeFile), 30000);
                    Log($"watcher_burst_src_delete: removed_from_file_list={removed} ms={sw.ElapsedMilliseconds}");
                }
                await Task.Delay(1000);
                Log($"final: rss_mb={RssMb()} managed_live_mb={LiveManagedMbAfterGc()} workspace_files={workspaceFiles.Count}");
            }
            catch (Exception ex) { Log("error=" + ex); }
            Log("done");
            SuppressUnsavedPrompts = true;
            Close();
        }
    }
}
