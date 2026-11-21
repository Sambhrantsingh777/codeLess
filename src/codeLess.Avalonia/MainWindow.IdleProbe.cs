using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using Avalonia.Controls;

namespace codeLess.AvaloniaApp
{
    public partial class MainWindow
    {
        [StructLayout(LayoutKind.Sequential)]
        private struct RusageInfoV0
        {
            public ulong Uuid0, Uuid1;
            public ulong UserTime, SystemTime, PkgIdleWkups, InterruptWkups, Pageins, WiredSize, ResidentSize, PhysFootprint, ProcStartAbstime, ProcExitAbstime;
        }

        [DllImport("/usr/lib/libSystem.B.dylib", EntryPoint = "proc_pid_rusage")]
        private static extern int proc_pid_rusage(int pid, int flavor, out RusageInfoV0 info);

        private static bool TryReadRusage(out RusageInfoV0 info)
        {
            info = default;
            if (!RuntimeInformation.IsOSPlatform(OSPlatform.OSX)) return false;
            try { return proc_pid_rusage(Environment.ProcessId, 0, out info) == 0; } catch { return false; }
        }

        /// <summary>
        /// Idle-cost probe (`codeLess --idle-probe report.txt --workspace dir`): opens a source file, then measures process
        /// CPU time, idle wakeups (macOS) and memory over a quiet window with the main window (a) focused/active,
        /// (b) inactive (another app brought to the front) and (c) minimized. Nothing is typed or scrolled, so every
        /// CPU millisecond and wakeup in a window is background cost (timers, polling, GC).
        /// </summary>
        public async Task RunIdleProbeAsync(string reportPath)
        {
            int settleMs = ReadProbeEnvMs("CODELESS_IDLE_PROBE_SETTLE_MS", 15000);
            int windowMs = ReadProbeEnvMs("CODELESS_IDLE_PROBE_WINDOW_MS", 60000);
            var sb = new StringBuilder();
            void Log(string s) { sb.AppendLine(s); try { File.WriteAllText(reportPath, sb.ToString()); } catch { } }
            try
            {
                await Task.Delay(ReadProbeEnvMs("CODELESS_IDLE_PROBE_WARMUP_MS", 20000));
                string src = Path.Combine(workspaceRoot ?? "", "src", "codeLess.Avalonia", "App.cs");
                if (File.Exists(src)) OpenFileInTab(src, preview: false);
                Log("file=" + (activeTab != null ? activeTab.FilePath : ""));

                Activate();
                PrimaryEditor?.Canvas?.Focus();
                await MeasureIdlePhaseAsync("active", settleMs, windowMs, Log);

                try { Process.Start(new ProcessStartInfo("/usr/bin/open", "-a Finder") { UseShellExecute = false })?.Dispose(); } catch { }
                await MeasureIdlePhaseAsync("inactive", settleMs, windowMs, Log);

                WindowState = WindowState.Minimized;
                await MeasureIdlePhaseAsync("minimized", settleMs, windowMs, Log);
            }
            catch (Exception ex) { Log("error=" + ex.Message); }
            SuppressUnsavedPrompts = true;
            Close();
        }

        private async Task MeasureIdlePhaseAsync(string phase, int settleMs, int windowMs, Action<string> log)
        {
            await Task.Delay(settleMs);
            var proc = Process.GetCurrentProcess();
            proc.Refresh();
            var cpu0 = proc.TotalProcessorTime;
            bool haveRu0 = TryReadRusage(out var ru0);
            var sw = Stopwatch.StartNew();
            await Task.Delay(windowMs);
            proc.Refresh();
            double cpuMs = (proc.TotalProcessorTime - cpu0).TotalMilliseconds;
            double secs = sw.Elapsed.TotalSeconds;
            bool haveRu1 = TryReadRusage(out var ru1);
            string wake = haveRu0 && haveRu1
                ? $" idle_wakeups={ru1.PkgIdleWkups - ru0.PkgIdleWkups} interrupt_wakeups={ru1.InterruptWkups - ru0.InterruptWkups} wakeups_per_s={(ru1.InterruptWkups - ru0.InterruptWkups) / secs:F1} phys_footprint_mb={ru1.PhysFootprint / 1048576.0:F1}"
                : "";
            log($"{phase}: is_active={IsActive} state={WindowState} secs={secs:F1} cpu_ms={cpuMs:F0} cpu_pct={cpuMs / (secs * 10.0):F3} rss_mb={proc.WorkingSet64 / 1048576.0:F1}{wake} managed_mb={GC.GetTotalMemory(false) / 1048576.0:F1}");
        }

        private static int ReadProbeEnvMs(string name, int fallback)
        {
            int v;
            return int.TryParse(Environment.GetEnvironmentVariable(name), out v) && v >= 0 ? v : fallback;
        }
    }
}
