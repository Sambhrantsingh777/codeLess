using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using codeLess.Studio;
using Xunit;
using Xunit.Abstractions;

namespace codeLess.Avalonia.Tests
{
    /// <summary>
    /// Opt-in check against a real installed server (clangd): set CODELESS_REAL_LSP=1. Skipped silently otherwise so the
    /// regular suite never starts heavy external processes.
    /// </summary>
    public class RealLanguageServerSmokeTests
    {
        private readonly ITestOutputHelper output;

        public RealLanguageServerSmokeTests(ITestOutputHelper output)
        {
            this.output = output;
        }

        [Fact]
        public void Clangd_Diagnostics_Completion_Hover_Definition_Rename_Format_And_Clean_Shutdown()
        {
            if (Environment.GetEnvironmentVariable("CODELESS_REAL_LSP") != "1") return;
            var res = LanguageServerRegistry.Resolve("c", null, null);
            if (!res.CanStart) return;
            string root = Path.Combine(Path.GetTempPath(), "codeLess_RealLsp_" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(root);
            var m = new LanguageServerManager(a => a());
            try
            {
                m.SetWorkspaceRoot(root);
                string path = Path.Combine(root, "main.c");
                string text = "int add(int a, int b) { return a + b; }\n\nint main(void) {\n  int total = add(1, 2);\n  undefined_call();\n  return total;\n}\n";
                File.WriteAllText(path, text);
                var sw = Stopwatch.StartNew();
                m.OpenDocument(path, () => text);
                var inst = m.GetInstanceForPath(path);
                SpinUntil(() => inst.State == LanguageServerState.Running, 30000, "clangd running: " + inst.Message);
                long startMs = sw.ElapsedMilliseconds;
                SpinUntil(() => LspDiagnosticsStore.Count(path) > 0, 30000, "clangd diagnostics");
                long diagMs = sw.ElapsedMilliseconds;
                Assert.Contains(LspDiagnosticsStore.Get(path), d => d.Line == 5 && d.Message.Contains("undefined_call"));

                sw.Restart();
                var list = m.RequestCompletionAsync(path, 3, 16, null, false, CancellationToken.None).Result; // "int total = ad|d"
                long complMs = sw.ElapsedMilliseconds;
                Assert.NotNull(list);
                Assert.Contains(list.Items, i => i.Label.Contains("add"));
                sw.Restart();
                var hover = m.RequestHoverAsync(path, 3, 15, CancellationToken.None).Result;
                long hoverMs = sw.ElapsedMilliseconds;
                Assert.Contains("add", hover.Text);
                var defs = m.RequestLocationsAsync(path, 3, 15, "textDocument/definition", CancellationToken.None).Result;
                Assert.Equal(0, defs[0].Range.StartLine);
                var we = m.RequestRenameAsync(path, 0, 5, "sum", CancellationToken.None).Result;
                // clangd reports the file under both /var/... and /private/var/... on macOS: must collapse to one step.
                Assert.Single(we.Steps);
                Assert.Equal(2, we.Steps.Sum(s => s.Edits != null ? s.Edits.Count : 0));
                var fmt = m.RequestFormattingAsync(path, null, 4, true, CancellationToken.None).Result;
                Assert.NotNull(fmt);
                int pid = inst.ProcessId;
                output.WriteLine("clangd: running after " + startMs + " ms, first diagnostics " + diagMs + " ms, completion " + complMs + " ms, hover " + hoverMs + " ms, format edits " + fmt.Count);
                m.ShutdownAll(2000);
                SpinUntil(() => { try { return Process.GetProcessById(pid).HasExited; } catch { return true; } }, 5000, "clangd exit");
            }
            finally
            {
                m.Dispose();
                try { Directory.Delete(root, true); } catch { }
            }
        }

        private static void SpinUntil(Func<bool> cond, int ms, string what)
        {
            var sw = Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < ms) { if (cond()) return; Thread.Sleep(25); }
            Assert.Fail("Timed out: " + what);
        }
    }
}
