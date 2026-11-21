using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;

namespace codeLess.AvaloniaApp
{
    /// <summary>
    /// Instrumentation for the external benchmark harness (bench/run_bench.py). Unlike --bench / --smoke-quit these
    /// hooks never quit the app: the harness keeps measuring memory and idle CPU of the live process tree.
    ///
    ///   --bench-ready &lt;file&gt;        once the window has been opened AND the first frame containing the startup
    ///                                 workspace / file has been rendered, write a JSON stamp (wall-clock epoch ms and
    ///                                 ms since the OS process start).
    ///   --bench-open-file &lt;path&gt;    after ready (+ --bench-open-delay-ms, default 0) open the file through the
    ///   --bench-open-report &lt;file&gt;  normal OpenFileInTab path and write a JSON stamp when the first frame after the
    ///                                 open has been rendered.
    ///
    /// "Rendered" = two chained TopLevel.RequestAnimationFrame callbacks: the first runs right before frame N is
    /// rendered, the second right before frame N+1, so frame N (with the new content) has been committed. This can
    /// overshoot by up to one frame interval, i.e. it errs against codeLess.
    /// </summary>
    public partial class MainWindow
    {
        private bool benchProbeStarted;

        public void StartBenchProbes(string readyPath, string openFilePath, string openReportPath, int openDelayMs)
        {
            Opened += (s, e) =>
            {
                if (benchProbeStarted) return;
                benchProbeStarted = true;
                AfterNextRenderedFrame(() =>
                {
                    double readyEpoch = EpochMsNow();
                    if (!string.IsNullOrWhiteSpace(readyPath))
                    {
                        var stamp = BaseBenchStamp("ready", readyEpoch);
                        stamp["workspace"] = workspaceRoot ?? "";
                        stamp["open_tabs"] = openTabs.Count;
                        stamp["active_file"] = activeTab?.FilePath ?? "";
                        stamp["frame_wait_timed_out"] = benchLastFrameTimedOut;
                        WriteBenchJson(readyPath, stamp);
                    }
                    if (!string.IsNullOrWhiteSpace(openFilePath))
                        _ = RunBenchOpenFileAsync(openFilePath, openReportPath, Math.Max(0, openDelayMs));
                });
            };
        }

        private async Task RunBenchOpenFileAsync(string path, string reportPath, int delayMs)
        {
            if (delayMs > 0) await Task.Delay(delayMs);
            string full = path;
            long bytes = -1;
            try { full = Path.GetFullPath(path); bytes = new FileInfo(full).Length; } catch { }

            double requested = EpochMsNow();
            var started = BaseBenchStamp("open-started", requested);
            started["file"] = full;
            started["file_bytes"] = bytes;
            if (!string.IsNullOrWhiteSpace(reportPath)) WriteBenchJson(reportPath, started);

            EditorTabBuffer tab = null;
            string error = null;
            try { tab = OpenFileInTab(full, preview: false); }
            catch (Exception ex) { error = ex.GetType().Name + ": " + ex.Message; }
            double returned = EpochMsNow();

            AfterNextRenderedFrame(() =>
            {
                double painted = EpochMsNow();
                var stamp = BaseBenchStamp("open", painted);
                stamp["file"] = full;
                stamp["file_bytes"] = bytes;
                stamp["requested_epoch_ms"] = requested;
                stamp["returned_epoch_ms"] = returned;
                stamp["painted_epoch_ms"] = painted;
                stamp["open_ms"] = Math.Round(painted - requested, 1);
                stamp["call_ms"] = Math.Round(returned - requested, 1);
                bool shown = tab != null && error == null && string.Equals(activeTab?.FilePath, full, StringComparison.Ordinal);
                stamp["ok"] = shown;
                stamp["error"] = error ?? (tab == null ? "OpenFileInTab returned no tab" : "");
                stamp["line_count"] = tab?.Lines?.Count ?? -1;
                stamp["is_large_file"] = tab != null && tab.IsLargeFile;
                stamp["is_large_file_viewer"] = tab != null && tab.IsLargeFileViewer;
                stamp["active_file"] = activeTab?.FilePath ?? "";
                stamp["managed_heap_mb"] = Math.Round(GC.GetTotalMemory(false) / 1048576.0, 1);
                stamp["frame_wait_timed_out"] = benchLastFrameTimedOut;
                if (!string.IsNullOrWhiteSpace(reportPath)) WriteBenchJson(reportPath, stamp);
            });
        }

        private bool benchLastFrameTimedOut;

        private void AfterNextRenderedFrame(Action action)
        {
            bool done = false;
            void Run(bool timedOut)
            {
                if (done) return;
                done = true;
                benchLastFrameTimedOut = timedOut;
                try { action(); } catch { }
            }
            RequestAnimationFrame(_ => RequestAnimationFrame(_ => Run(false)));
            // Safety net: a hidden / minimised window may never render; report anyway (flagged) instead of hanging.
            Avalonia.Threading.DispatcherTimer.RunOnce(() => Run(true), TimeSpan.FromSeconds(30));
        }

        private static double EpochMsNow()
        {
            return (DateTime.UtcNow - DateTime.UnixEpoch).TotalMilliseconds;
        }

        private static Dictionary<string, object> BaseBenchStamp(string evt, double epochMs)
        {
            var d = new Dictionary<string, object>
            {
                ["event"] = evt,
                ["epoch_ms"] = Math.Round(epochMs, 1),
                ["pid"] = Environment.ProcessId
            };
            try
            {
                double startEpoch = (Process.GetCurrentProcess().StartTime.ToUniversalTime() - DateTime.UnixEpoch).TotalMilliseconds;
                d["process_start_epoch_ms"] = Math.Round(startEpoch, 1);
                d["since_process_start_ms"] = Math.Round(epochMs - startEpoch, 1);
                if (App.MainEnteredUtc != default)
                    d["main_entered_since_process_start_ms"] = Math.Round((App.MainEnteredUtc - DateTime.UnixEpoch).TotalMilliseconds - startEpoch, 1);
            }
            catch { }
            return d;
        }

        private static void WriteBenchJson(string path, Dictionary<string, object> data)
        {
            try
            {
                string full = Path.GetFullPath(path);
                string dir = Path.GetDirectoryName(full);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                string tmp = full + ".tmp";
                File.WriteAllText(tmp, JsonSerializer.Serialize(data));
                File.Move(tmp, full, overwrite: true); // atomic: the harness never reads a half-written stamp
            }
            catch { }
        }
    }
}
