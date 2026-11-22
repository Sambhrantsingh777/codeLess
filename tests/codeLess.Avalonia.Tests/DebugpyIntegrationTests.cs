using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using codeLess.Studio;
using Xunit;
using Xunit.Abstractions;

namespace codeLess.Avalonia.Tests
{
    /// <summary>
    /// Real end-to-end debugging with debugpy through the same DebugController the UIs use. Needs a Python with
    /// debugpy: $CODELESS_DEBUGPY_PYTHON, else /tmp/dbgvenv/bin/python (python3 -m venv /tmp/dbgvenv &amp;&amp;
    /// /tmp/dbgvenv/bin/pip install debugpy). When none is available the tests return early (reported in output).
    /// </summary>
    public class DebugpyIntegrationTests
    {
        private readonly ITestOutputHelper log;
        public DebugpyIntegrationTests(ITestOutputHelper log) { this.log = log; }

        internal static string FindDebugpyPython()
        {
            var cands = new List<string>();
            string env = Environment.GetEnvironmentVariable("CODELESS_DEBUGPY_PYTHON");
            if (!string.IsNullOrEmpty(env)) cands.Add(env);
            cands.Add("/tmp/dbgvenv/bin/python");
            cands.Add(@"C:\tmp\dbgvenv\Scripts\python.exe");
            foreach (string c in cands)
            {
                if (!File.Exists(c)) continue;
                string o = ToolLocator.RunAndCapture(c, "-c \"import debugpy,sys;sys.stdout.write('ok')\"", 15000);
                if (o != null && o.Contains("ok")) return c;
            }
            return null;
        }

        private static string WriteScript(out string dir)
        {
            dir = Path.Combine(Path.GetTempPath(), "codeLess_dbgpy_" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(dir);
            TestTrust.Trust(dir); // workspace-local tools / git / tasks need a trusted folder
            string p = Path.Combine(dir, "sample.py");
            File.WriteAllText(p,
                "def compute(n):\n" +            // 1
                "    total = 0\n" +              // 2
                "    for i in range(n):\n" +     // 3
                "        total += i\n" +         // 4
                "    return total\n" +           // 5
                "\n" +                           // 6
                "items = [10, 20, 30]\n" +       // 7
                "value = compute(4)\n" +         // 8
                "label = 'answer'\n" +           // 9
                "print(label, value)\n");        // 10
            return p;
        }

        private async Task RunScenario(string python, string console, FakeDebugHost host)
        {
            string script = WriteScript(out string dir);
            host.Root = dir;
            var buf = new EditorTabBuffer { FilePath = script, FileName = "sample.py", Language = "py" };
            buf.Lines = File.ReadAllLines(script).ToList();
            host.Buffers.Add(buf);
            var ctl = new DebugController(host);
            ctl.ToggleBreakpoint(buf, 8); // line 9: label = 'answer'
            ctl.AddWatch("value * 2");

            try
            {
                await RunScenarioCore(python, console, host, ctl, script, buf);
            }
            finally
            {
                // Never leave debugpy / the debuggee behind, even when an assertion failed.
                try { ctl.Session?.Dispose(); } catch { }
                KillProcessesMatching(dir);
                try { Directory.Delete(dir, true); } catch { }
            }
        }

        /// <summary>Kills any process whose command line mentions <paramref name="needle"/> (debuggee / launcher).</summary>
        internal static void KillProcessesMatching(string needle)
        {
            if (string.IsNullOrEmpty(needle) || Path.DirectorySeparatorChar == '\\') return;
            try
            {
                var psi = new ProcessStartInfo("/usr/bin/pkill", "-f " + DebugAdapterConnection.QuoteArg(needle)) { UseShellExecute = false, CreateNoWindow = true };
                using var p = Process.Start(psi);
                p?.WaitForExit(3000);
            }
            catch { }
        }

        private async Task RunScenarioCore(string python, string console, FakeDebugHost host, DebugController ctl, string script, EditorTabBuffer buf)
        {
            var cfg = new DebugLaunchConfig { Name = "debugpy e2e", Type = "debugpy", Request = "launch" };
            cfg.Properties["program"] = "${file}";
            cfg.Properties["python"] = python;
            cfg.Properties["console"] = console;
            cfg.Properties["justMyCode"] = true;
            var sw = Stopwatch.StartNew();
            _ = ctl.StartAsync(cfg, script);

            bool stopped = host.PumpUntil(() => ctl.IsStopped && ctl.SelectedFrame != null, 30000);
            log.WriteLine("console so far:\n" + ctl.ConsoleText());
            Assert.True(stopped, "never hit the breakpoint. Errors: " + string.Join(" / ", host.Errors));
            log.WriteLine($"start -> breakpoint hit: {sw.ElapsedMilliseconds} ms");
            Assert.Equal(9, ctl.SelectedFrame.Line);
            Assert.True(DebugPaths.Same(script, ctl.SelectedFrame.SourcePath), ctl.SelectedFrame.SourcePath);
            Assert.Equal(8, buf.DebugStoppedLine);
            Assert.True(ctl.Breakpoints.All().Single().Verified == true);

            // Read locals: scopes -> variables (lazy), expand a list.
            var scopes = await ctl.GetScopesAsync(ctl.SelectedFrame);
            Assert.NotEmpty(scopes);
            var locals = scopes.First(s => s.Name.IndexOf("Local", StringComparison.OrdinalIgnoreCase) >= 0 || s.Name.IndexOf("Global", StringComparison.OrdinalIgnoreCase) >= 0);
            var vars = await ctl.GetVariablesAsync(locals.VariablesReference);
            var value = vars.FirstOrDefault(v => v.Name == "value");
            Assert.NotNull(value);
            Assert.Equal("6", value.Value);
            var items = vars.First(v => v.Name == "items");
            Assert.True(items.HasChildren);
            var children = await ctl.GetVariablesAsync(items.VariablesReference);
            Assert.Contains(children, c => c.Value == "20");

            Assert.True(host.PumpUntil(() => ctl.Watches[0].Result != null, 5000));
            Assert.Equal("12", ctl.Watches[0].Result.Result);
            Assert.Equal("6", await ctl.EvaluateForHoverAsync("value"));

            ctl.SubmitRepl("len(items) + value");
            Assert.True(host.PumpUntil(() => ctl.Console.Any(e => e.Category == "result" && e.Text.Trim() == "9"), 5000), ctl.ConsoleText());

            // Step over: line 9 -> 10, label now defined.
            ctl.StepOver();
            Assert.True(host.PumpUntil(() => ctl.IsStopped && ctl.SelectedFrame != null && ctl.SelectedFrame.Line == 10, 10000), "step did not reach line 10");
            Assert.Equal(9, buf.DebugStoppedLine);
            var hover = await ctl.EvaluateForHoverAsync("label");
            Assert.Equal("'answer'", hover);

            // Continue to exit.
            ctl.Continue();
            Assert.True(host.PumpUntil(() => ctl.State == DebugSessionState.Terminated, 15000), "did not terminate");
            Assert.Equal(-1, buf.DebugStoppedLine);
            log.WriteLine($"total session: {sw.ElapsedMilliseconds} ms");
            log.WriteLine(ctl.ConsoleText());
        }

        [Fact]
        public async Task Debugpy_InternalConsole_BreakpointLocalsStepContinue()
        {
            string python = FindDebugpyPython();
            if (python == null) { log.WriteLine("SKIPPED: no Python with debugpy (set CODELESS_DEBUGPY_PYTHON)"); return; }
            var host = new FakeDebugHost();
            await RunScenario(python, "internalConsole", host);
        }

        [Fact]
        public async Task Debugpy_IntegratedTerminal_UsesRunInTerminalReverseRequest()
        {
            string python = FindDebugpyPython();
            if (python == null) { log.WriteLine("SKIPPED: no Python with debugpy (set CODELESS_DEBUGPY_PYTHON)"); return; }
            var host = new FakeDebugHost();
            Process launched = null;
            host.RunInTerminal = (title, cwd, args, env) =>
            {
                // Stand-in for the integrated terminal: start argv directly with the env overrides.
                var psi = new ProcessStartInfo(args[0]) { UseShellExecute = false, WorkingDirectory = cwd, RedirectStandardOutput = true };
                foreach (var a in args.Skip(1)) psi.ArgumentList.Add(a);
                foreach (var kv in env) { if (kv.Value == null) psi.Environment.Remove(kv.Key); else psi.Environment[kv.Key] = kv.Value; }
                launched = Process.Start(psi);
                return Task.FromResult<int?>(launched.Id);
            };
            try
            {
                await RunScenario(python, "integratedTerminal", host);
                Assert.Single(host.TerminalCommands);
                Assert.Contains("debugpy", host.TerminalCommands[0]);
                log.WriteLine("runInTerminal command: " + host.TerminalCommands[0]);
                Assert.NotNull(launched);
                Assert.True(launched.WaitForExit(10000));
                Assert.Contains("answer 6", launched.StandardOutput.ReadToEnd());
            }
            finally
            {
                try { if (launched != null && !launched.HasExited) launched.Kill(); } catch { }
            }
        }
    }
}
