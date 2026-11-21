using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using codeLess.AvaloniaApp;
using codeLess.Studio;
using Xunit;

namespace codeLess.Avalonia.Tests
{
    internal static class BenchUtil
    {
        public static double Median(List<double> v)
        {
            var s = v.OrderBy(x => x).ToList();
            return s.Count == 0 ? 0 : (s.Count % 2 == 1 ? s[s.Count / 2] : (s[s.Count / 2 - 1] + s[s.Count / 2]) / 2);
        }

        public static long Rss() { var p = Process.GetCurrentProcess(); p.Refresh(); return p.WorkingSet64; }

        public static long LiveBytes()
        {
            GC.Collect(2, GCCollectionMode.Forced, true, true);
            GC.WaitForPendingFinalizers();
            return GC.GetTotalMemory(true);
        }

        public static void Append(string path, string line)
        {
            File.AppendAllText(path, line + Environment.NewLine);
        }

        /// <summary>Samples process RSS every 20 ms while running action; returns the peak.</summary>
        public static long PeakRssDuring(Action action)
        {
            long peak = Rss();
            bool done = false;
            var t = new Thread(() => { while (!Volatile.Read(ref done)) { long r = Rss(); if (r > peak) peak = r; Thread.Sleep(20); } });
            t.IsBackground = true;
            t.Start();
            try { action(); }
            finally { Volatile.Write(ref done, true); t.Join(); }
            return Math.Max(peak, Rss());
        }
    }

    /// <summary>
    /// Headless large-file benchmark of the shared engine (no GUI). Skipped unless CODELESS_BENCH_LARGEFILE points at a
    /// file (generate with bench/gen_logfile.py); appends results to CODELESS_BENCH_REPORT. Run via bench/run_largefile_bench.sh.
    /// </summary>
    public class LargeFileEngineBench
    {
        [Fact]
        public void Run()
        {
            string path = Environment.GetEnvironmentVariable("CODELESS_BENCH_LARGEFILE");
            if (string.IsNullOrEmpty(path) || !File.Exists(path)) return; // opt-in benchmark
            string report = Environment.GetEnvironmentVariable("CODELESS_BENCH_REPORT") ?? Path.Combine(Path.GetTempPath(), "cl-largefile-bench.txt");
            int runs = int.TryParse(Environment.GetEnvironmentVariable("CODELESS_BENCH_RUNS"), out int r) ? r : 3;
            string needle = Environment.GetEnvironmentVariable("CODELESS_BENCH_NEEDLE") ?? "StripeGateway.capture";
            long size = new FileInfo(path).Length;
            BenchUtil.Append(report, $"## engine: file={path} size={LargeFilePolicy.FormatBytes(size)} runs={runs}");

            long rss0 = BenchUtil.Rss();
            long live0 = BenchUtil.LiveBytes();
            var tOpen = new List<double>(); var tFirst = new List<double>(); var tIndex = new List<double>();
            long lines = 0; int cps = 0; long peakIndexRss = 0; long liveIndex = 0;
            for (int i = 0; i < runs; i++)
            {
                var sw = Stopwatch.StartNew();
                using (var doc = LargeFileDocument.Open(path))
                {
                    tOpen.Add(sw.Elapsed.TotalMilliseconds);
                    var first = doc.GetLines(0, 60);       // first screen, while indexing runs
                    tFirst.Add(sw.Elapsed.TotalMilliseconds);
                    long peak = BenchUtil.PeakRssDuring(() => doc.WaitForIndex(600000));
                    tIndex.Add(sw.Elapsed.TotalMilliseconds);
                    peakIndexRss = Math.Max(peakIndexRss, peak);
                    lines = doc.LineCount; cps = doc.CheckpointCount;
                    if (i == runs - 1) liveIndex = BenchUtil.LiveBytes() - live0;
                    Assert.True(first.Length > 0);
                }
            }
            double idxMs = BenchUtil.Median(tIndex);
            BenchUtil.Append(report, $"open_ms(median)={BenchUtil.Median(tOpen):F1} first_screen_ms={BenchUtil.Median(tFirst):F1} full_index_ms={idxMs:F0} " +
                $"throughput_MBps={size / 1048576.0 / (idxMs / 1000.0):F0} lines={lines:N0} checkpoints={cps:N0} " +
                $"index_live_managed_mb={liveIndex / 1048576.0:F1} process_rss_before_mb={rss0 / 1048576.0:F0} peak_rss_during_index_mb={peakIndexRss / 1048576.0:F0}");

            using (var doc = LargeFileDocument.Open(path))
            {
                doc.WaitForIndex(600000);
                // Random access (go to line) + rendering a 60-line screen.
                var rnd = new Random(1);
                var tGo = new List<double>();
                for (int k = 0; k < 200; k++)
                {
                    long line = (long)(rnd.NextDouble() * (doc.LineCount - 61));
                    var sw = Stopwatch.StartNew();
                    var screen = doc.GetLines(line, 60);
                    tGo.Add(sw.Elapsed.TotalMilliseconds);
                }
                BenchUtil.Append(report, $"goto_random_line+60_line_screen: median_ms={BenchUtil.Median(tGo):F2} max_ms={tGo.Max():F2} (200 jumps)");

                foreach (var (q, mc, rx) in new[] { (needle, true, false), (needle.ToLowerInvariant(), false, false), (@"latency_ms=2[0-9]{3}\b", true, true) })
                {
                    var tS = new List<double>(); LargeFileSearch s = null; long peak = 0;
                    for (int i = 0; i < runs; i++)
                    {
                        s = new LargeFileSearch(doc, q, mc, rx);
                        var sw = Stopwatch.StartNew();
                        long pk = BenchUtil.PeakRssDuring(() => s.Run());
                        tS.Add(sw.Elapsed.TotalMilliseconds);
                        peak = Math.Max(peak, pk);
                    }
                    double ms = BenchUtil.Median(tS);
                    BenchUtil.Append(report, $"search[{(rx ? "regex" : mc ? "case-sensitive" : "ignore-case")} '{q}']: median_ms={ms:F0} throughput_MBps={size / 1048576.0 / (ms / 1000.0):F0} matches={s.TotalMatches:N0} stored={s.StoredMatchCount:N0} strategy={s.Strategy} peak_rss_mb={peak / 1048576.0:F0}");
                }

                // Copy a 10k-line selection.
                var swc = Stopwatch.StartNew();
                string text = doc.GetText(1000, 0, 11000, 0, LargeFilePolicy.ClipboardCharLimit, out bool tr);
                BenchUtil.Append(report, $"copy_10k_lines: ms={swc.Elapsed.TotalMilliseconds:F0} chars={text.Length:N0}");
            }

            // Tail -f: append 10,000 lines to a copy of the last 50 MB, measure growth -> indexed latency, then truncate.
            string tailPath = path + ".tail.log";
            try
            {
                using (var src = LargeFileDocument.OpenShared(path, 1 << 16))
                using (var dst = new FileStream(tailPath, FileMode.Create, FileAccess.Write, FileShare.ReadWrite))
                {
                    long take = Math.Min(src.Length, 50L * 1024 * 1024);
                    src.Seek(src.Length - take, SeekOrigin.Begin);
                    src.CopyTo(dst);
                }
                using (var doc = LargeFileDocument.Open(tailPath))
                {
                    doc.WaitForIndex(120000);
                    doc.Follow = true;
                    long before = doc.LineCount;
                    var sb = new StringBuilder();
                    for (int i = 0; i < 10000; i++) sb.Append("2026-11-22T12:00:00.000Z INFO  [tail] appended line ").Append(i).Append('\n');
                    byte[] bytes = Encoding.UTF8.GetBytes(sb.ToString());
                    using (var a = new FileStream(tailPath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite)) a.Write(bytes, 0, bytes.Length);
                    var sw = Stopwatch.StartNew();
                    doc.PollForChanges();
                    doc.WaitForIndex(60000);
                    double appendMs = sw.Elapsed.TotalMilliseconds;
                    bool ok = doc.LineCount == before + 10000 && doc.GetLine(doc.LineCount - 2).EndsWith("appended line 9999");
                    File.WriteAllText(tailPath, "rotated\n");
                    sw.Restart();
                    doc.PollForChanges();
                    doc.WaitForIndex(60000);
                    double resetMs = sw.Elapsed.TotalMilliseconds;
                    BenchUtil.Append(report, $"tail: append_10k_lines_to_indexed_ms={appendMs:F1} correct={ok} truncate_detect_and_reindex_ms={resetMs:F1} resets={doc.ResetCount} lines_after={doc.LineCount}");
                }
            }
            finally { try { File.Delete(tailPath); } catch { } }
        }
    }

    /// <summary>Opening the file through the real (headless) workbench: OpenFileInTab -> viewer shown.</summary>
    [Collection("SequentialWorkbench")]
    public class LargeFileWorkbenchBench
    {
        [AvaloniaFact]
        public void OpenInWorkbench()
        {
            string path = Environment.GetEnvironmentVariable("CODELESS_BENCH_LARGEFILE");
            if (string.IsNullOrEmpty(path) || !File.Exists(path)) return;
            string report = Environment.GetEnvironmentVariable("CODELESS_BENCH_REPORT") ?? Path.Combine(Path.GetTempPath(), "cl-largefile-bench.txt");
            string settingsPath = IdePersistentSettings.GetSettingsFilePath();
            string backup = File.Exists(settingsPath) ? File.ReadAllText(settingsPath) : null;
            string ws = Path.GetDirectoryName(path);
            try
            {
                var window = new MainWindow(ws);
                window.Show();
                long live0 = BenchUtil.LiveBytes();
                long rss0 = BenchUtil.Rss();
                var sw = Stopwatch.StartNew();
                var tab = window.OpenFileInTab(path, preview: false);
                Dispatcher.UIThread.RunJobs();
                double openMs = sw.Elapsed.TotalMilliseconds;
                bool viewer = tab != null && tab.IsLargeFileViewer;
                tab?.LargeDocument?.WaitForIndex(600000);
                double indexedMs = sw.Elapsed.TotalMilliseconds;
                long live1 = BenchUtil.LiveBytes();
                BenchUtil.Append(report, $"## workbench (headless Avalonia): OpenFileInTab_ms={openMs:F1} opened_in_viewer={viewer} editor_buffer_lines={(tab?.Lines.Count ?? -1)} " +
                    $"fully_indexed_ms={indexedMs:F0} managed_delta_after_index_mb={(live1 - live0) / 1048576.0:F1} rss_delta_mb={(BenchUtil.Rss() - rss0) / 1048576.0:F0}");
                if (tab != null) window.CloseTab(tab, forceWithoutPrompt: true);
                window.Close();
            }
            finally
            {
                try { if (backup != null) File.WriteAllText(settingsPath, backup); } catch { }
            }
        }

        /// <summary>The old path for comparison: the whole file loaded into an EditorTabBuffer (only for files <= 300 MB).</summary>
        [AvaloniaFact]
        public void LoadIntoEditorBufferForComparison()
        {
            string path = Environment.GetEnvironmentVariable("CODELESS_BENCH_EDITOR_FILE");
            if (string.IsNullOrEmpty(path) || !File.Exists(path) || new FileInfo(path).Length > 300L * 1024 * 1024) return;
            string report = Environment.GetEnvironmentVariable("CODELESS_BENCH_REPORT") ?? Path.Combine(Path.GetTempPath(), "cl-largefile-bench.txt");
            long live0 = BenchUtil.LiveBytes();
            long rss0 = BenchUtil.Rss();
            EditorTabBuffer tab = null;
            var sw = Stopwatch.StartNew();
            long peak = BenchUtil.PeakRssDuring(() =>
            {
                string content = EditorTabBuffer.ReadFilePreservingMetadata(path, out var enc, out bool bom, out string eol, out bool fnl);
                tab = new EditorTabBuffer { FilePath = path, FileName = Path.GetFileName(path), Content = content, SavedContent = content };
                tab.SyncLinesFromContent();
            });
            double ms = sw.Elapsed.TotalMilliseconds;
            long live1 = BenchUtil.LiveBytes();
            BenchUtil.Append(report, $"## old path (whole file into EditorTabBuffer, no rendering): file={LargeFilePolicy.FormatBytes(new FileInfo(path).Length)} load_ms={ms:F0} lines={tab.Lines.Count:N0} " +
                $"managed_live_mb={(live1 - live0) / 1048576.0:F0} peak_rss_delta_mb={(peak - rss0) / 1048576.0:F0}");
            GC.KeepAlive(tab);
        }
    }
}
