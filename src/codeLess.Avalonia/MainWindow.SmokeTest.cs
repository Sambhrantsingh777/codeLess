using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace codeLess.AvaloniaApp
{
    public partial class MainWindow
    {
        /// <summary>
        /// End-to-end smoke test on a real desktop session (`codeLess --smoke-quit report.txt --workspace dir`):
        /// spawns real PTY shells, runs a TUI app and a long job, exercises Explorer folder creation, then quits
        /// through the normal window-close path with an unsaved tab. The external runner verifies the process
        /// exits promptly and leaves no orphaned children.
        /// </summary>
        /// <summary>
        /// Fixed performance workload (`codeLess --bench report.txt --workspace dir`): 600 editor scroll frames through
        /// a large file at ~60 fps and a 200k-line terminal flood. Reports wall time and process CPU time so memory
        /// changes can be checked for performance regressions.
        /// </summary>
        public async Task RunBenchmarkAsync(string reportPath)
        {
            var sb = new StringBuilder();
            try
            {
                await Task.Delay(2500);
                var proc = System.Diagnostics.Process.GetCurrentProcess();
                string big = Path.Combine(workspaceRoot ?? "", "src", "CodeLessMainForm.cs");
                var tab = File.Exists(big) ? OpenFileInTab(big, preview: false) : activeTab;
                await Task.Delay(800);

                proc.Refresh();
                var cpu0 = proc.TotalProcessorTime;
                var sw = System.Diagnostics.Stopwatch.StartNew();
                for (int i = 0; i < 600 && tab != null; i++)
                {
                    tab.ScrollTopLine = (i * 17) % Math.Max(1, tab.Lines.Count - 60);
                    PrimaryEditor.Canvas.InvalidateVisual();
                    await Task.Delay(16);
                }
                proc.Refresh();
                sb.AppendLine($"editor_scroll: wall_ms={sw.ElapsedMilliseconds} cpu_ms={(proc.TotalProcessorTime - cpu0).TotalMilliseconds:F0}");

                var term = CreateNewTerminalSession();
                await Task.Delay(2500);
                proc.Refresh();
                cpu0 = proc.TotalProcessorTime;
                sw.Restart();
                term.Control.WriteAutomatedInput("seq 1 200000; echo BENCH_$((40+2))_DONE\r");
                while (sw.ElapsedMilliseconds < 60000 && !(term.Control.GetVisibleScreenText() ?? "").Contains("BENCH_42_DONE"))
                    await Task.Delay(50);
                proc.Refresh();
                sb.AppendLine($"terminal_flood: wall_ms={sw.ElapsedMilliseconds} cpu_ms={(proc.TotalProcessorTime - cpu0).TotalMilliseconds:F0}");
            }
            catch (Exception ex) { sb.AppendLine("error=" + ex.Message); }
            try { File.WriteAllText(reportPath, sb.ToString()); } catch { }
            SuppressUnsavedPrompts = true;
            Close();
        }

        public async Task RunSmokeQuitAsync(string reportPath)
        {
            var sb = new StringBuilder();
            void Log(string s) { sb.AppendLine(s); try { File.WriteAllText(reportPath, sb.ToString()); } catch { } }
            try
            {
                await Task.Delay(1200);
                Log("workspace=" + workspaceRoot);
                Log("path=" + Environment.GetEnvironmentVariable("PATH"));
                var root = ExplorerTree.Nodes.FirstOrDefault();
                Log("explorer_root_children=" + (root == null ? "" : string.Join("|", root.Nodes.Select(n => n.Text))));

                string probeDir = Path.GetDirectoryName(Path.GetFullPath(reportPath));
                var t1 = CreateNewTerminalSession();
                var t2 = CreateNewTerminalSession();
                await Task.Delay(2500);
                Log("terminal1_running=" + (t1.Control.Session != null && t1.Control.Session.IsRunning));
                Log("terminal2_running=" + (t2.Control.Session != null && t2.Control.Session.IsRunning));
                t1.Control.WriteAutomatedInput("ps -o tty= -p $$ > '" + Path.Combine(probeDir, "smoke-tty.txt") + "'; vim -u NONE\r");
                t2.Control.WriteAutomatedInput("echo $$ > '" + Path.Combine(probeDir, "smoke-shell-pid.txt") + "'; sleep 1000\r");

                if (!string.IsNullOrEmpty(workspaceRoot))
                {
                    string target = Directory.GetDirectories(workspaceRoot).FirstOrDefault() ?? workspaceRoot;
                    string created = CreateFolderInDirectory(target, "smoke-folder-" + DateTime.Now.Ticks);
                    var node = created != null ? ExplorerTree.FindNodeByPath(created) : null;
                    Log("new_folder_created=" + (created != null && Directory.Exists(created)));
                    Log("new_folder_visible=" + (node != null && ExplorerTree.VisibleFlatRows.Contains(node)));
                    if (created != null) { try { Directory.Delete(created); } catch { } }
                }

                var scratch = CreateNewUntitledTab();
                scratch.IsDirty = true;
                UnsavedPromptOverride = dirty => UnsavedPromptResult.DontSave;
                await Task.Delay(1500);
                var gi = GC.GetGCMemoryInfo();
                Log($"gc_before: live_mb={GC.GetTotalMemory(false) / 1048576} heap_mb={gi.HeapSizeBytes / 1048576} committed_mb={gi.TotalCommittedBytes / 1048576} frag_mb={gi.FragmentedBytes / 1048576}");
                GC.Collect(2, GCCollectionMode.Forced, true, true);
                gi = GC.GetGCMemoryInfo();
                Log($"gc_after_full: live_mb={GC.GetTotalMemory(true) / 1048576} heap_mb={gi.HeapSizeBytes / 1048576} committed_mb={gi.TotalCommittedBytes / 1048576}");
                Log("closing_at=" + DateTime.UtcNow.ToString("o"));
            }
            catch (Exception ex)
            {
                Log("error=" + ex);
            }
            Close();
        }
    }
}
