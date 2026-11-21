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
