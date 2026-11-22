using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using codeLess.Studio;
using Xunit;

namespace codeLess.Avalonia.Tests
{
    /// <summary>Stream that returns at most N bytes per Read (exercises framing across chunk boundaries).</summary>
    internal sealed class TricklingStream : Stream
    {
        private readonly MemoryStream inner;
        private readonly int chunk;
        public TricklingStream(byte[] data, int chunk) { inner = new MemoryStream(data); this.chunk = chunk; }
        public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, Math.Min(count, chunk));
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => inner.Length;
        public override long Position { get => inner.Position; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    /// <summary>
    /// Scripted fake debug adapter on a loopback TCP port. Behaves like debugpy where it matters: "initialized"
    /// after the initialize response, the launch response only after configurationDone, then a stop at a breakpoint.
    /// </summary>
    internal sealed class FakeDapAdapter : IDisposable
    {
        private readonly TcpListener listener;
        private Stream stream;
        private int seq = 1000;
        private readonly object writeLock = new object();
        private int pendingLaunchSeq = -1;
        private int currentLine = 3;
        public readonly string ProgramPath;
        public readonly ConcurrentQueue<Dictionary<string, object>> Requests = new ConcurrentQueue<Dictionary<string, object>>();
        public readonly ConcurrentQueue<Dictionary<string, object>> ReverseResponses = new ConcurrentQueue<Dictionary<string, object>>();
        public bool SendRunInTerminal;
        public int Port => ((IPEndPoint)listener.LocalEndpoint).Port;

        public FakeDapAdapter(string programPath)
        {
            ProgramPath = programPath;
            listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            var t = new Thread(Run) { IsBackground = true, Name = "FakeDap" };
            t.Start();
        }

        public List<string> Commands() => Requests.Select(r => DapJson.Str(r, "command")).ToList();

        private void Send(Dictionary<string, object> m)
        {
            m["seq"] = Interlocked.Increment(ref seq);
            byte[] bytes = DapMessageReader.Frame(DapJson.Serialize(m));
            lock (writeLock) { stream.Write(bytes, 0, bytes.Length); stream.Flush(); }
        }

        private void Respond(Dictionary<string, object> req, object body, bool success = true, string message = null)
        {
            var m = DapJson.Dict("type", "response", "request_seq", DapJson.Int(req, "seq", 0), "success", success,
                "command", DapJson.Str(req, "command"), "message", message, "body", body ?? new Dictionary<string, object>());
            Send(m);
        }

        private void Event(string name, object body) => Send(DapJson.Dict("type", "event", "event", name, "body", body ?? new Dictionary<string, object>()));

        private void Run()
        {
            try
            {
                var client = listener.AcceptTcpClient();
                stream = client.GetStream();
                var reader = new DapMessageReader(stream);
                string json;
                while ((json = reader.ReadMessage()) != null)
                {
                    var msg = DapJson.ParseObject(json);
                    if (DapJson.Str(msg, "type") == "response") { ReverseResponses.Enqueue(msg); continue; }
                    Requests.Enqueue(msg);
                    Handle(msg);
                }
            }
            catch { }
        }

        private Dictionary<string, object> Frame(int id, string name, int line) =>
            DapJson.Dict("id", id, "name", name, "line", line, "column", 1, "source", DapJson.Dict("path", ProgramPath, "name", Path.GetFileName(ProgramPath)));

        private void Handle(Dictionary<string, object> req)
        {
            string cmd = DapJson.Str(req, "command");
            var args = DapJson.Obj(req, "arguments");
            switch (cmd)
            {
                case "initialize":
                    Respond(req, DapJson.Dict("supportsConfigurationDoneRequest", true, "supportsConditionalBreakpoints", true,
                        "supportsHitConditionalBreakpoints", true, "supportsLogPoints", true, "supportsTerminateRequest", true,
                        "exceptionBreakpointFilters", new List<object>
                        {
                            DapJson.Dict("filter", "raised", "label", "Raised Exceptions", "default", false),
                            DapJson.Dict("filter", "uncaught", "label", "Uncaught Exceptions", "default", true)
                        }));
                    Event("initialized", null);
                    break;
                case "launch":
                    pendingLaunchSeq = DapJson.Int(req, "seq", 0);
                    if (SendRunInTerminal)
                        Send(DapJson.Dict("type", "request", "command", "runInTerminal", "arguments",
                            DapJson.Dict("kind", "integrated", "title", "Fake", "cwd", "/tmp", "args", new List<object> { "python3", "x.py" }, "env", DapJson.Dict("A", "1"))));
                    break;
                case "setBreakpoints":
                    {
                        var bps = DapJson.Arr(args, "breakpoints") ?? new List<object>();
                        var res = new List<object>();
                        int id = 1;
                        foreach (var b in bps)
                        {
                            int line = DapJson.Int(b, "line", 0);
                            res.Add(DapJson.Dict("id", id++, "verified", line < 100, "line", line));
                        }
                        Respond(req, DapJson.Dict("breakpoints", res));
                        break;
                    }
                case "setExceptionBreakpoints":
                    Respond(req, null);
                    break;
                case "configurationDone":
                    Respond(req, null);
                    Send(DapJson.Dict("type", "response", "request_seq", pendingLaunchSeq, "success", true, "command", "launch", "body", new Dictionary<string, object>()));
                    Event("thread", DapJson.Dict("reason", "started", "threadId", 1));
                    Event("output", DapJson.Dict("category", "stdout", "output", "hello from fake\n"));
                    Event("stopped", DapJson.Dict("reason", "breakpoint", "threadId", 1, "allThreadsStopped", true));
                    break;
                case "threads":
                    Respond(req, DapJson.Dict("threads", new List<object> { DapJson.Dict("id", 1, "name", "MainThread") }));
                    break;
                case "stackTrace":
                    Respond(req, DapJson.Dict("stackFrames", new List<object> { Frame(7, "main", currentLine), Frame(8, "<module>", 10) }, "totalFrames", 2));
                    break;
                case "scopes":
                    Respond(req, DapJson.Dict("scopes", new List<object> { DapJson.Dict("name", "Locals", "variablesReference", 100, "expensive", false) }));
                    break;
                case "variables":
                    {
                        int r = DapJson.Int(args, "variablesReference", 0);
                        var vars = r == 100
                            ? new List<object>
                            {
                                DapJson.Dict("name", "x", "value", currentLine == 3 ? "42" : "43", "type", "int", "variablesReference", 0, "evaluateName", "x"),
                                DapJson.Dict("name", "items", "value", "[1, 2]", "type", "list", "variablesReference", 101, "evaluateName", "items")
                            }
                            : new List<object> { DapJson.Dict("name", "0", "value", "1", "variablesReference", 0), DapJson.Dict("name", "1", "value", "2", "variablesReference", 0) };
                        Respond(req, DapJson.Dict("variables", vars));
                        break;
                    }
                case "evaluate":
                    {
                        string expr = DapJson.Str(args, "expression");
                        if (expr == "boom") Respond(req, null, false, "NameError: name 'boom' is not defined");
                        else Respond(req, DapJson.Dict("result", expr == "x" ? (currentLine == 3 ? "42" : "43") : "'" + expr + "'", "variablesReference", 0));
                        break;
                    }
                case "next":
                    Respond(req, null);
                    currentLine = 4;
                    Event("stopped", DapJson.Dict("reason", "step", "threadId", 1));
                    break;
                case "continue":
                    Respond(req, DapJson.Dict("allThreadsContinued", true));
                    Event("output", DapJson.Dict("category", "stdout", "output", "done\n"));
                    Event("exited", DapJson.Dict("exitCode", 0));
                    Event("terminated", null);
                    break;
                case "terminate":
                    Respond(req, null);
                    Event("terminated", null);
                    break;
                case "disconnect":
                    Respond(req, null);
                    break;
                default:
                    Respond(req, null, false, "unsupported " + cmd);
                    break;
            }
        }

        public void Dispose()
        {
            try { listener.Stop(); } catch { }
            try { stream?.Dispose(); } catch { }
        }
    }

    /// <summary>Single-threaded "UI" for DebugController tests: Post queues, the test thread pumps.</summary>
    internal sealed class FakeDebugHost : IDebugUiHost
    {
        private readonly BlockingCollection<Action> queue = new BlockingCollection<Action>();
        public readonly List<EditorTabBuffer> Buffers = new List<EditorTabBuffer>();
        public readonly List<string> Statuses = new List<string>();
        public readonly List<string> Errors = new List<string>();
        public readonly List<string> TerminalCommands = new List<string>();
        public readonly List<(string Path, int Line)> Reveals = new List<(string, int)>();
        public string Root;
        public IdePersistentSettings SettingsObj = new IdePersistentSettings();
        public Func<string, string, List<string>, Dictionary<string, string>, Task<int?>> RunInTerminal;

        public void Post(Action action) => queue.Add(action);

        public bool PumpUntil(Func<bool> condition, int timeoutMs)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < timeoutMs)
            {
                if (condition()) return true;
                if (queue.TryTake(out var a, 20)) a();
            }
            return condition();
        }

        public Task<int?> RunInTerminalAsync(string title, string cwd, List<string> args, Dictionary<string, string> env)
        {
            TerminalCommands.Add(DebugTerminalCommands.Build(TerminalShellFlavor.Posix, cwd, args, env));
            return RunInTerminal != null ? RunInTerminal(title, cwd, args, env) : Task.FromResult<int?>(4242);
        }
        public void RevealLocation(string path, int line) => Reveals.Add((path, line));
        public IList<EditorTabBuffer> GetOpenBuffers() => Buffers;
        public void RefreshEditors() { }
        public void ShowStatus(string message) => Statuses.Add(message);
        public void ShowDebugError(string message, string hint, string installCommand) => Errors.Add(message + (hint != null ? " | " + hint : ""));
        public string WorkspaceRoot => Root;
        public IdePersistentSettings Settings => SettingsObj;
    }

    public class DapFramingTests
    {
        [Fact]
        public void Reader_HandlesChunkedStreams_MultibyteText_AndHeaderVariants()
        {
            string a = DapJson.Serialize(DapJson.Dict("seq", 1, "type", "event", "event", "output", "body", DapJson.Dict("output", "héllo wörld ✓ 🐍\n")));
            string b = "{\"seq\":2,\"type\":\"response\",\"request_seq\":1,\"success\":true,\"command\":\"x\"}";
            var ms = new MemoryStream();
            byte[] fa = DapMessageReader.Frame(a);
            ms.Write(fa, 0, fa.Length);
            // Second message with an extra header and lowercase name, as some adapters send.
            byte[] bodyB = Encoding.UTF8.GetBytes(b);
            byte[] hdr = Encoding.ASCII.GetBytes("content-length: " + bodyB.Length + "\r\nContent-Type: application/vscode-jsonrpc; charset=utf-8\r\n\r\n");
            ms.Write(hdr, 0, hdr.Length);
            ms.Write(bodyB, 0, bodyB.Length);
            foreach (int chunk in new[] { 1, 3, 7, 4096 })
            {
                var r = new DapMessageReader(new TricklingStream(ms.ToArray(), chunk));
                string m1 = r.ReadMessage();
                string m2 = r.ReadMessage();
                Assert.Equal(a, m1);
                Assert.Equal(b, m2);
                Assert.Null(r.ReadMessage());
                Assert.Equal("héllo wörld ✓ 🐍\n", DapJson.Str(DapJson.Obj(DapJson.Parse(m1), "body"), "output"));
            }
        }

        [Fact]
        public void Frame_CountsBytesNotChars()
        {
            byte[] f = DapMessageReader.Frame("\"é\"");
            string s = Encoding.UTF8.GetString(f);
            Assert.StartsWith("Content-Length: 4\r\n\r\n", s);
        }

        [Fact]
        public void Json_RoundTrips_AndParsesJsonc()
        {
            var obj = DapJson.ParseObject("{ // comment\n \"a\": [1, 2.5, true, null, \"x\\n\\u00e9\",], /* block */ \"b\": {\"c\": -3,},\n}");
            var arr = DapJson.Arr(obj, "a");
            Assert.Equal(5, arr.Count);
            Assert.Equal(1L, arr[0]);
            Assert.Equal(2.5, arr[1]);
            Assert.Equal(true, arr[2]);
            Assert.Null(arr[3]);
            Assert.Equal("x\né", arr[4]);
            Assert.Equal(-3, DapJson.Int(DapJson.Obj(obj, "b"), "c", 0));
            string again = DapJson.Serialize(obj);
            Assert.Equal(again, DapJson.Serialize(DapJson.Parse(again)));
            Assert.Throws<FormatException>(() => DapJson.Parse("{\"a\": }"));
            Assert.Equal("// not a comment", DapJson.Str(DapJson.Parse("{\"u\": \"// not a comment\"}"), "u"));
        }
    }

    public class DapSessionTests
    {
        private static string TempProgram()
        {
            string dir = Path.Combine(Path.GetTempPath(), "codeLess_dap_" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(dir);
            string p = Path.Combine(dir, "prog.py");
            File.WriteAllText(p, "import sys\n\ndef main():\n    x = 42\n    return x\n\nmain()\n");
            return p;
        }

        [Fact]
        public async Task Session_FullHandshake_StopsAndStepsAndTerminates_AgainstScriptedAdapter()
        {
            string prog = TempProgram();
            using var fake = new FakeDapAdapter(prog) { SendRunInTerminal = true };
            var session = new DebugSession { Name = "fake" };
            var states = new ConcurrentQueue<DebugSessionState>();
            var output = new StringBuilder();
            var stopped = new SemaphoreSlim(0);
            var terminated = new TaskCompletionSource<bool>();
            session.StateChanged += s => states.Enqueue(s);
            session.Output += (c, t) => { lock (output) output.Append(c + ":" + t); };
            session.Stopped += (tid, reason, d, all) => stopped.Release();
            session.Terminated += () => terminated.TrySetResult(true);
            List<string> rit = null;
            session.RunInTerminalHandler = (kind, title, cwd, args, env) => { rit = args; return Task.FromResult<int?>(777); };
            var sentBps = new List<Dictionary<string, object>>();
            session.ConfigureHandler = async s =>
            {
                var bps = new List<DebugSourceBreakpoint>
                {
                    new DebugSourceBreakpoint { FilePath = prog, Line = 3, Condition = "x > 1" },
                    new DebugSourceBreakpoint { FilePath = prog, Line = 4, HitCondition = "2" },
                    new DebugSourceBreakpoint { FilePath = prog, Line = 5, LogMessage = "x={x}" },
                    new DebugSourceBreakpoint { FilePath = prog, Line = 500 }
                };
                sentBps = await s.SetBreakpointsAsync(prog, bps);
                s.ExceptionFilters.First(f => f.Filter == "raised").Enabled = true;
                await s.SendExceptionBreakpointsAsync();
            };
            var desc = new DebugAdapterDescriptor { Type = "codeless", UseTcp = true, Port = fake.Port, AdapterId = "fake" };
            string err = await session.StartAsync(desc, "launch", DapJson.Dict("program", prog, "console", "integratedTerminal"));
            Assert.Null(err);
            Assert.True(await stopped.WaitAsync(5000), "no stopped event");
            Assert.Equal(DebugSessionState.Stopped, session.State);
            Assert.Equal("breakpoint", session.StopReason);

            // Handshake order: initialize, launch (unanswered), setBreakpoints, setExceptionBreakpoints, configurationDone.
            var cmds = fake.Commands();
            Assert.Equal(new[] { "initialize", "launch", "setBreakpoints", "setExceptionBreakpoints", "configurationDone" }, cmds.Take(5).ToArray());
            var setBp = fake.Requests.First(r => DapJson.Str(r, "command") == "setBreakpoints");
            var bpArgs = DapJson.Arr(DapJson.Obj(setBp, "arguments"), "breakpoints");
            Assert.Equal(4L, DapJson.Long(bpArgs[0], "line", 0)); // 0-based 3 -> 1-based 4
            Assert.Equal("x > 1", DapJson.Str(bpArgs[0], "condition"));
            Assert.Equal("2", DapJson.Str(bpArgs[1], "hitCondition"));
            Assert.Equal("x={x}", DapJson.Str(bpArgs[2], "logMessage"));
            Assert.Equal(4, sentBps.Count);
            Assert.False(DapJson.Bool(sentBps[3], "verified", true));
            var exc = fake.Requests.First(r => DapJson.Str(r, "command") == "setExceptionBreakpoints");
            Assert.Equal(new object[] { "raised", "uncaught" }, DapJson.Arr(DapJson.Obj(exc, "arguments"), "filters").ToArray());
            var init = DapJson.Obj(fake.Requests.First(), "arguments");
            Assert.True(DapJson.Bool(init, "supportsRunInTerminalRequest", false));
            Assert.True(DapJson.Bool(init, "linesStartAt1", false));

            // Reverse request runInTerminal answered with the pid.
            Assert.True(SpinWait.SpinUntil(() => !fake.ReverseResponses.IsEmpty, 3000));
            fake.ReverseResponses.TryPeek(out var rr);
            Assert.True(DapJson.Bool(rr, "success", false));
            Assert.Equal(777, DapJson.Int(DapJson.Obj(rr, "body"), "processId", 0));
            Assert.Equal(new[] { "python3", "x.py" }, rit);

            var threads = await session.GetThreadsAsync();
            Assert.Equal("MainThread", threads.Single().Name);
            var frames = await session.GetStackTraceAsync(1, 20);
            Assert.Equal(2, frames.Count);
            Assert.Equal(3, frames[0].Line);
            Assert.Equal(prog, frames[0].SourcePath);
            var scopes = await session.GetScopesAsync(frames[0].Id);
            Assert.Equal("Locals", scopes[0].Name);
            var vars = await session.GetVariablesAsync(scopes[0].VariablesReference);
            Assert.Equal("42", vars.First(v => v.Name == "x").Value);
            var items = vars.First(v => v.Name == "items");
            Assert.True(items.HasChildren);
            var children = await session.GetVariablesAsync(items.VariablesReference); // lazy expansion
            Assert.Equal(2, children.Count);
            var ev = await session.EvaluateAsync("x", frames[0].Id, "watch");
            Assert.True(ev.Success);
            Assert.Equal("42", ev.Result);
            var bad = await session.EvaluateAsync("boom", frames[0].Id, "repl");
            Assert.False(bad.Success);
            Assert.Contains("NameError", bad.Error);

            await session.NextAsync(1);
            Assert.True(await stopped.WaitAsync(5000), "no stop after step");
            frames = await session.GetStackTraceAsync(1, 20);
            Assert.Equal(4, frames[0].Line);

            await session.ContinueAsync(1);
            Assert.True(await Task.WhenAny(terminated.Task, Task.Delay(5000)) == terminated.Task, "not terminated");
            Assert.Equal(DebugSessionState.Terminated, session.State);
            Assert.Equal(0, session.ExitCode);
            lock (output) Assert.Contains("stdout:hello from fake", output.ToString());
            Assert.Contains(DebugSessionState.Running, states);
            session.Dispose();
        }

        [Fact]
        public async Task Session_ReportsAdapterThatCannotStart()
        {
            var session = new DebugSession();
            string err = await session.StartAsync(new DebugAdapterDescriptor { Type = "x", Command = "/nonexistent/adapter-binary-xyz" }, "launch", new Dictionary<string, object>());
            Assert.NotNull(err);
            Assert.Equal(DebugSessionState.Terminated, session.State);
        }

        [Fact]
        public async Task Client_FailsPendingRequests_WhenConnectionCloses()
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            int port = ((IPEndPoint)listener.LocalEndpoint).Port;
            var tcp = new TcpClient();
            var acceptTask = listener.AcceptTcpClientAsync();
            tcp.Connect(IPAddress.Loopback, port);
            var server = await acceptTask;
            var client = new DapClient(tcp.GetStream(), tcp.GetStream());
            string closedWith = "not closed";
            client.Closed += e => closedWith = e;
            client.Start();
            var pending = client.SendRequestAsync("threads", null, 0);
            server.Close();
            listener.Stop();
            var r = await pending;
            Assert.False(r.Success);
            Assert.True(SpinWait.SpinUntil(() => closedWith != "not closed", 3000));
            var after = await client.SendRequestAsync("threads", null);
            Assert.False(after.Success);
        }

        [Fact]
        public async Task Controller_EndToEnd_WithScriptedAdapter_BreakpointsWatchesReplAndDecorations()
        {
            string prog = TempProgram();
            using var fake = new FakeDapAdapter(prog);
            var host = new FakeDebugHost { Root = Path.GetDirectoryName(prog) };
            var buf = new EditorTabBuffer { FilePath = prog, FileName = "prog.py", Language = "py" };
            buf.Lines = File.ReadAllLines(prog).ToList();
            host.Buffers.Add(buf);
            var ctl = new DebugController(host);
            ctl.ToggleBreakpoint(buf, 3);
            Assert.Contains(3, buf.Breakpoints);
            Assert.Equal(DebugBreakpointGlyph.Enabled, buf.GetBreakpointGlyph(3));
            ctl.SetBreakpointOptions(prog, 4, "x > 0", null, null);
            Assert.Equal(DebugBreakpointGlyph.Conditional, buf.GetBreakpointGlyph(4));
            ctl.AddWatch("x");

            var cfg = new DebugLaunchConfig { Name = "fake", Type = "codeless", Request = "launch" };
            cfg.Properties["debugServer"] = fake.Port;
            cfg.Properties["program"] = "${file}";
            _ = ctl.StartAsync(cfg, prog);
            Assert.True(host.PumpUntil(() => ctl.IsStopped && ctl.SelectedFrame != null, 8000), "controller never stopped: " + string.Join(";", host.Errors));
            Assert.Equal(3 - 1, buf.DebugStoppedLine); // frame line 3 (1-based) -> 0-based 2
            Assert.True(buf.DebugStoppedIsTopFrame);
            Assert.Contains(host.Reveals, r => r.Path == prog && r.Line == 3);
            Assert.True(host.PumpUntil(() => ctl.Watches[0].Result != null, 3000));
            Assert.Equal("42", ctl.Watches[0].Result.Result);
            var launch = fake.Requests.First(r => DapJson.Str(r, "command") == "launch");
            Assert.Equal(prog, DapJson.Str(DapJson.Obj(launch, "arguments"), "program")); // ${file} substituted

            // Uncaught filter default on; raised off.
            Assert.Contains(ctl.ExceptionFilters, f => f.Filter == "uncaught" && f.Enabled);

            ctl.SubmitRepl("x");
            Assert.True(host.PumpUntil(() => ctl.Console.Any(e => e.Category == "result" && e.Text == "42\n"), 3000));
            ctl.SubmitRepl("boom");
            Assert.True(host.PumpUntil(() => ctl.Console.Any(e => e.Category == "error" && e.Text.Contains("NameError")), 3000));
            Assert.Equal(new[] { "x", "boom" }, ctl.ReplHistory);

            // Selecting the outer frame moves the marker (green, not top).
            var outer = ctl.FramesByThread[ctl.SelectedThreadId][1];
            ctl.SelectFrame(outer);
            Assert.Equal(9, buf.DebugStoppedLine);
            Assert.False(buf.DebugStoppedIsTopFrame);

            // Gutter toggle during the session re-sends setBreakpoints for that file.
            int before = fake.Commands().Count(c => c == "setBreakpoints");
            buf.Breakpoints.Add(5);
            ctl.OnBufferBreakpointToggled(buf, 5);
            Assert.True(host.PumpUntil(() => fake.Commands().Count(c => c == "setBreakpoints") > before, 3000));

            ctl.StepOver();
            Assert.True(host.PumpUntil(() => ctl.IsStopped && ctl.SelectedFrame != null && ctl.SelectedFrame.Line == 4, 5000));
            Assert.True(host.PumpUntil(() => ctl.Watches[0].Result != null && ctl.Watches[0].Result.Result == "43", 3000));

            ctl.Continue();
            Assert.True(host.PumpUntil(() => ctl.State == DebugSessionState.Terminated, 5000));
            Assert.Equal(-1, buf.DebugStoppedLine);
            // The adapter's `exited` event may be processed just after the state flips to Terminated.
            Assert.True(host.PumpUntil(() => ctl.ConsoleText().Contains("exited with code 0"), 3000), ctl.ConsoleText());
            Assert.Contains("done", ctl.ConsoleText());
        }

        [Fact]
        public void Controller_MissingDebugpy_ReportsInstallCommand()
        {
            var host = new FakeDebugHost();
            string fakePython = Path.Combine(Path.GetTempPath(), "no-such-python-" + Guid.NewGuid().ToString("N"));
            var cfg = new DebugLaunchConfig { Name = "py", Type = "debugpy" };
            cfg.Properties["python"] = fakePython;
            cfg.Properties["program"] = "/tmp/x.py";
            var env = new DebugAdapterEnvironment { FindExecutable = n => null, RunProcess = (e, a, t) => null, HomeDir = "/home/u" };
            var res = DebugAdapterRegistry.Resolve(cfg, DebugLaunchConfigurations.PrepareLaunchArguments(cfg, new DebugVariableContext()), null, env);
            Assert.Null(res.Descriptor);
            Assert.Contains("debugpy is not installed", res.Error);
            Assert.EndsWith("-m pip install debugpy", res.InstallCommand);
        }
    }

    public class LaunchJsonTests
    {
        private const string Sample = @"{
    // Use IntelliSense to learn about possible attributes.
    ""version"": ""0.2.0"",
    ""configurations"": [
        {
            ""name"": ""Python: Current File"",
            ""type"": ""debugpy"",
            ""request"": ""launch"",
            ""program"": ""${file}"",
            ""cwd"": ""${fileDirname}"",
            ""args"": [""--root"", ""${workspaceFolder}"", ""${env:CODELESS_TEST_VAR}""], /* inline */
            ""env"": { ""HOME_DIR"": ""${userHome}"", ""BASE"": ""${fileBasenameNoExtension}${fileExtname}"" },
        },
        {
            ""name"": ""Go"",
            ""type"": ""go"",
            ""request"": ""launch"",
            ""program"": ""${workspaceFolder}/cmd/app"",
        },
        { ""name"": ""Chrome"", ""type"": ""chrome"", ""request"": ""launch"" },
        { ""name"": ""Custom"", ""type"": ""codeless"", ""request"": ""launch"", ""adapter"": { ""command"": ""${workspaceFolder}/bin/my-dap"", ""args"": [""--stdio""] } },
    ],
}";

        [Fact]
        public void ParsesJsoncWithCommentsAndTrailingCommas()
        {
            var cfgs = DebugLaunchConfigurations.ParseLaunchJson(Sample, out string err);
            Assert.Null(err);
            Assert.Equal(4, cfgs.Count);
            Assert.Equal("python", cfgs[0].AdapterFamily);
            Assert.Equal("go", cfgs[1].AdapterFamily);
            Assert.False(DebugLaunchConfigurations.IsSupportedFamily(cfgs[2].AdapterFamily));
            Assert.Equal("codeless", cfgs[3].AdapterFamily);
        }

        [Fact]
        public void SubstitutesVariables()
        {
            var cfgs = DebugLaunchConfigurations.ParseLaunchJson(Sample, out _);
            string ws = Path.Combine(Path.GetTempPath(), "proj");
            string file = Path.Combine(ws, "src", "main.py");
            var ctx = new DebugVariableContext { WorkspaceFolder = ws, File = file, PythonInterpreter = "/venv/bin/python", GetEnv = n => n == "CODELESS_TEST_VAR" ? "fromEnv" : null };
            var args = DebugLaunchConfigurations.PrepareLaunchArguments(cfgs[0], ctx);
            Assert.Equal(file, DapJson.Str(args, "program"));
            Assert.Equal(Path.Combine(ws, "src"), DapJson.Str(args, "cwd"));
            var a = DapJson.Arr(args, "args");
            Assert.Equal(new object[] { "--root", ws, "fromEnv" }, a.ToArray());
            Assert.Equal("main.py", DapJson.Str(DapJson.Obj(args, "env"), "BASE"));
            Assert.Equal("/venv/bin/python", DapJson.Str(args, "python")); // selected interpreter injected
            Assert.Equal("integratedTerminal", DapJson.Str(args, "console"));
            // Original config untouched (substitution works on a copy).
            Assert.Equal("${file}", DapJson.Str(cfgs[0].Properties, "program"));
            Assert.Equal(ws + "/cmd/app", DapJson.Str(DebugLaunchConfigurations.PrepareLaunchArguments(cfgs[1], ctx), "program"));
            Assert.Equal("${unknownVar}", DebugLaunchConfigurations.SubstituteString("${unknownVar}", ctx));
            Assert.Equal("src/main.py".Replace('/', Path.DirectorySeparatorChar), DebugLaunchConfigurations.SubstituteString("${relativeFile}", ctx));
        }

        [Fact]
        public void GenericAdapterUsesSubstitutedCommand_AndStripsAdapterBlock()
        {
            var cfgs = DebugLaunchConfigurations.ParseLaunchJson(Sample, out _);
            var ctx = new DebugVariableContext { WorkspaceFolder = "/w" };
            var args = DebugLaunchConfigurations.PrepareLaunchArguments(cfgs[3], ctx);
            var res = DebugAdapterRegistry.Resolve(cfgs[3], args, null, new DebugAdapterEnvironment { FindExecutable = n => null });
            Assert.NotNull(res.Descriptor);
            Assert.Equal("/w/bin/my-dap", res.Descriptor.Command);
            Assert.Equal(new[] { "--stdio" }, res.Descriptor.Args);
            Assert.False(args.ContainsKey("adapter"));
        }

        [Fact]
        public void AdapterRegistry_DetectsAdaptersAndGivesHints()
        {
            DebugAdapterResolution R(string type, Func<string, string> find, List<UserDebugAdapterEntry> user = null)
            {
                var cfg = new DebugLaunchConfig { Name = "x", Type = type };
                var env = new DebugAdapterEnvironment { FindExecutable = find, RunProcess = (e, a, t) => null, HomeDir = "/nonexistent-home", UserAdapters = user };
                return DebugAdapterRegistry.Resolve(cfg, DebugLaunchConfigurations.PrepareLaunchArguments(cfg, new DebugVariableContext()), null, env);
            }
            var go = R("go", n => null);
            Assert.Null(go.Descriptor);
            Assert.Contains("go install github.com/go-delve/delve/cmd/dlv@latest", go.InstallCommand);
            var goOk = R("go", n => n == "dlv" ? "/bin/dlv" : null);
            Assert.True(goOk.Descriptor.UseTcp);
            Assert.Contains("--listen=127.0.0.1:${port}", goOk.Descriptor.Args);
            var lldb = R("lldb-dap", n => n == "lldb-dap" ? "/usr/bin/lldb-dap" : null);
            Assert.Equal("/usr/bin/lldb-dap", lldb.Descriptor.Command);
            Assert.False(lldb.Descriptor.UseTcp);
            Assert.NotNull(R("lldb", n => null).Hint);
            var net = R("coreclr", n => n == "netcoredbg" ? "/opt/netcoredbg" : null);
            Assert.Equal(new[] { "--interpreter=vscode" }, net.Descriptor.Args);
            Assert.Contains("js-debug", R("node", n => null).Error);
            var user = R("node", n => null, new List<UserDebugAdapterEntry> { new UserDebugAdapterEntry { Type = "node", Command = "node", Args = "\"/js debug/dapDebugServer.js\" ${port} 127.0.0.1", UseTcp = true } });
            Assert.Equal(new[] { "/js debug/dapDebugServer.js", "${port}", "127.0.0.1" }, user.Descriptor.Args);
            Assert.Contains("not supported", R("chrome", n => null).Error);

            var host = new FakeDebugHost();
            var ctl = new DebugController(host);
            Assert.True(ctl.ShouldRunWithoutDebugging(null, "/w/build.sh"));
            Assert.False(ctl.ShouldRunWithoutDebugging(null, "/w/notes.txt"));
            var nodeDefault = DebugLaunchConfigurations.CreateDefaultForFile("/w/app.js", "/w", out _);
            Assert.True(ctl.ShouldRunWithoutDebugging(nodeDefault, "/w/app.js"));
            host.SettingsObj.DebugAdapters = new List<UserDebugAdapterEntry> { new UserDebugAdapterEntry { Type = "pwa-node", Command = "node" } };
            Assert.False(ctl.ShouldRunWithoutDebugging(nodeDefault, "/w/app.js"));
        }

        [Fact]
        public void MalformedLaunchJson_ReportsError()
        {
            var cfgs = DebugLaunchConfigurations.ParseLaunchJson("{ \"configurations\": [ { \"name\": } ] }", out string err);
            Assert.Empty(cfgs);
            Assert.NotNull(err);
        }

        [Fact]
        public void DefaultsForActiveFile_AndTemplateIsValidJsonc()
        {
            using var t = new TempDir();
            string py = t.File("app.py", "print(1)\n");
            var c = DebugLaunchConfigurations.CreateDefaultForFile(py, t.Path, out string err);
            Assert.Null(err);
            Assert.Equal("python", c.AdapterFamily);
            Assert.Equal(py, DapJson.Str(c.Properties, "program"));
            Assert.Null(DebugLaunchConfigurations.CreateDefaultForFile(t.File("notes.txt"), t.Path, out err));
            Assert.NotNull(err);
            Assert.Null(DebugLaunchConfigurations.CreateDefaultForFile(t.File("main.c", "int main(){}"), t.Path, out err));
            Assert.Contains("Build", err);
            t.File("main", "binary");
            var cc = DebugLaunchConfigurations.CreateDefaultForFile(Path.Combine(t.Path, "main.c"), t.Path, out err);
            Assert.Equal("lldb", cc.AdapterFamily);

            foreach (var fam in new[] { "python", "go", "lldb", "coreclr" })
            {
                var parsed = DebugLaunchConfigurations.ParseLaunchJson(DebugLaunchConfigurations.BuildLaunchJsonTemplate(fam), out err);
                Assert.Null(err);
                Assert.NotEmpty(parsed);
            }
            string path = DebugLaunchConfigurations.CreateLaunchJson(t.Path, "python", out err);
            Assert.True(File.Exists(path));
            File.WriteAllText(path, "{ \"configurations\": [] }");
            Assert.Equal(path, DebugLaunchConfigurations.CreateLaunchJson(t.Path, "python", out err)); // never overwrites
            Assert.Equal("{ \"configurations\": [] }", File.ReadAllText(path));
        }

        [Fact]
        public void TerminalCommands_QuotePerShell()
        {
            var args = new List<string> { "/my venv/bin/python", "-m", "it's" };
            var env = new Dictionary<string, string> { ["A"] = "1 2", ["DROP"] = null };
            Assert.Equal("cd '/w d' && /usr/bin/env 'A=1 2' -u DROP '/my venv/bin/python' -m 'it'\\''s'",
                DebugTerminalCommands.Build(TerminalShellFlavor.Posix, "/w d", args, env));
            string ps = DebugTerminalCommands.Build(TerminalShellFlavor.PowerShell, "C:\\w", args, env);
            Assert.Equal("cd 'C:\\w'; $env:A='1 2'; Remove-Item Env:DROP -ErrorAction SilentlyContinue; & '/my venv/bin/python' '-m' 'it''s'", ps);
            Assert.Equal("python3 /a/b.py", DebugTerminalCommands.BuildRun(TerminalShellFlavor.Posix, "python3", "/a/b.py"));
            Assert.Equal("python 'C:\\a b\\x.py'", DebugTerminalCommands.BuildRun(TerminalShellFlavor.PowerShell, "python", "C:\\a b\\x.py"));
        }

        [Fact]
        public void BreakpointStore_SyncsWithBuffers()
        {
            var store = new DebugBreakpointStore();
            var buf = new EditorTabBuffer { FilePath = "/tmp/codeless_bp_test.py" };
            buf.Lines = new List<string> { "a", "b", "c", "d" };
            buf.Breakpoints.Add(1);
            store.ApplyToBuffer(buf, false); // adopts gutter lines
            Assert.Single(store.All());
            var bp = store.Toggle("/private/tmp/codeless_bp_test.py", 2); // macOS /private/tmp alias = same file
            Assert.NotNull(bp);
            store.ApplyToBuffer(buf, false);
            Assert.Equal(new[] { 1, 2 }, buf.Breakpoints.OrderBy(x => x).ToArray());
            store.SetEnabled(bp, false);
            store.ApplyToBuffer(buf, true);
            Assert.Equal(DebugBreakpointGlyph.Disabled, buf.GetBreakpointGlyph(2));
            store.Update(store.Find(buf.FilePath, 1), null, null, "hit {x}");
            store.ApplyToBuffer(buf, true);
            Assert.Equal(DebugBreakpointGlyph.Logpoint, buf.GetBreakpointGlyph(1));
            buf.Breakpoints.Remove(1);
            store.SyncFromBuffer(buf);
            Assert.Single(store.All());
            Assert.Equal("x", DebugController.ExpressionAt("    y = x + 1", 8));
            Assert.Equal("self.items", DebugController.ExpressionAt("return self.items[0]", 13));
            Assert.Null(DebugController.ExpressionAt("def foo():", 1));
        }
    }

    public class PythonInterpreterDiscoveryTests
    {
        private static PythonDiscoveryEnvironment FakeEnv(string home, Dictionary<string, string> envVars = null, Dictionary<string, string> tools = null,
            Func<string, string, string> run = null)
        {
            return new PythonDiscoveryEnvironment
            {
                IsWindows = false,
                HomeDir = home,
                GetEnv = n => envVars != null && envVars.TryGetValue(n, out var v) ? v : null,
                FindExecutable = n => tools != null && tools.TryGetValue(n, out var p) ? p : null,
                RunProcess = (exe, args, timeout) => run?.Invoke(exe, args),
                ProbeVersions = false
            };
        }

        private static string MakeVenv(TempDir t, string rel, string version, string extraCfg = "")
        {
            t.File(Path.Combine(rel, "pyvenv.cfg"), "home = /usr/bin\nversion = " + version + "\n" + extraCfg);
            return t.File(Path.Combine(rel, "bin", "python"), "#!");
        }

        [Fact]
        public void FindsWorkspaceVenvs_Uv_Pyenv_Conda_And_System()
        {
            using var t = new TempDir();
            string ws = Path.Combine(t.Path, "proj");
            string home = Path.Combine(t.Path, "home");
            string dotVenvPy = MakeVenv(t, "proj/.venv", "3.12.1");
            string otherPy = MakeVenv(t, "proj/tools-env", "3.11.2");
            t.File("proj/uv.lock", "");
            // pyenv
            string pyenvPy = t.File("home/.pyenv/versions/3.10.4/bin/python", "#!");
            // conda base + env (conda-meta marks a conda prefix)
            t.File("home/miniconda3/conda-meta/python-3.11.4-h1_0.json", "{}");
            string condaBase = t.File("home/miniconda3/bin/python", "#!");
            t.File("home/miniconda3/envs/ml/conda-meta/python-3.10.13-h2_0.json", "{}");
            string condaMl = t.File("home/miniconda3/envs/ml/bin/python", "#!");
            string sysPy = t.File("usr/bin/python3", "#!");

            var env = FakeEnv(home, tools: new Dictionary<string, string> { ["python3"] = sysPy });
            var all = PythonInterpreterDiscovery.Discover(ws, env);

            var dot = all.First(i => i.Path == dotVenvPy);
            Assert.Equal("uv", dot.Kind); // uv.lock + .venv
            Assert.Equal("3.12.1 ('.venv')", dot.DisplayLabel);
            Assert.Equal(Path.Combine(ws, ".venv"), dot.EnvPath);
            Assert.Equal(0, all.IndexOf(dot)); // workspace env first
            Assert.Contains(all, i => i.Path == otherPy && i.Kind == "venv" && i.Version == "3.11.2");
            Assert.Contains(all, i => i.Path == pyenvPy && i.Kind == "pyenv" && i.Version == "3.10.4");
            var baseEnv = all.First(i => i.Path == condaBase);
            Assert.Equal("conda", baseEnv.Kind);
            Assert.Equal("base", baseEnv.EnvName);
            Assert.Equal("3.11.4 ('base': conda)", baseEnv.DisplayLabel);
            var ml = all.First(i => i.Path == condaMl);
            Assert.Equal("ml", ml.EnvName);
            Assert.Equal("3.10.13", ml.Version);
            Assert.Contains(all, i => i.Path == sysPy && i.Kind == "system");
            Assert.Equal(dot, PythonInterpreterDiscovery.PickDefault(all));
        }

        [Fact]
        public void UsesPoetryPipenvAndCondaTools_WhenPresent()
        {
            using var t = new TempDir();
            string ws = Path.Combine(t.Path, "my_app");
            string home = Path.Combine(t.Path, "home");
            t.File("my_app/pyproject.toml", "[tool.poetry]\nname = \"my-app\"\n");
            t.File("my_app/Pipfile", "[packages]\n");
            string poetryPy = MakeVenv(t, "cache/poetry/my-app-AbC12-py3.12", "3.12.0");
            string pipenvPy = MakeVenv(t, "home/.local/share/virtualenvs/my_app-Zz9", "3.9.18");
            t.File("conda/envs/data/conda-meta/history", "");
            string condaPy = t.File("conda/envs/data/bin/python", "#!");
            var tools = new Dictionary<string, string> { ["poetry"] = "/fake/poetry", ["conda"] = "/fake/conda" };
            string poetryEnvDir = Path.GetDirectoryName(Path.GetDirectoryName(poetryPy));
            var env = FakeEnv(home, tools: tools, run: (exe, args) =>
            {
                if (exe == "/fake/poetry" && args.StartsWith("env info -p")) return poetryEnvDir + "\n";
                if (exe == "/fake/conda" && args == "env list --json")
                    return DapJson.Serialize(DapJson.Dict("envs", new List<object> { Path.Combine(t.Path, "conda", "envs", "data") }));
                return null;
            });
            var all = PythonInterpreterDiscovery.Discover(ws, env);
            Assert.Contains(all, i => i.Path == poetryPy && i.Kind == "poetry" && i.DisplayLabel.Contains("poetry"));
            Assert.Contains(all, i => i.Path == pipenvPy && i.Kind == "pipenv");
            Assert.Contains(all, i => i.Path == condaPy && i.Kind == "conda" && i.EnvName == "data");
        }

        [Fact]
        public void MissingToolsAndEmptyWorkspace_DoNotThrow()
        {
            using var t = new TempDir();
            var all = PythonInterpreterDiscovery.Discover(null, FakeEnv(Path.Combine(t.Path, "nohome")));
            // Only well-known system locations (/usr/bin/python3 ...) can appear without a workspace or tools.
            Assert.All(all, i => Assert.Equal("system", i.Kind));
            Assert.Null(PythonInterpreterDiscovery.PickDefault(new List<PythonInterpreterInfo>()));
            Assert.Equal("3.12.1", PythonInterpreterDiscovery.ParseVersionOutput("Python 3.12.1\n"));
        }

        [Fact]
        public void SelectedInterpreter_PersistsPerWorkspace_AndDrivesTerminalActivation()
        {
            using var t = new TempDir();
            string ws = Path.Combine(t.Path, "ws");
            Directory.CreateDirectory(ws);
            string venvPy = MakeVenv(t, "envs/custom", "3.12.3");
            t.File("envs/custom/bin/activate", "");
            var settings = new IdePersistentSettings();
            var prevOverride = PythonVenvActivator.SelectedEnvironmentOverride;
            try
            {
                // Bind without touching the user's real settings file: Select() saves, so use a throwaway object only
                // for Get/Set and call the activator logic directly.
                settings.SetPythonInterpreterFor(ws, venvPy);
                Assert.Equal(venvPy, settings.GetPythonInterpreterFor(ws + "/"));
                Assert.Null(settings.GetPythonInterpreterFor(t.Path));
                var info = PythonInterpreterDiscovery.FromPath(venvPy, null);
                Assert.Equal("venv", info.Kind);
                Assert.Equal("3.12.3 ('custom')", info.DisplayLabel);
                PythonVenvActivator.SelectedEnvironmentOverride = root => root == ws ? info.EnvPath : null;
                string cmd = PythonVenvActivator.ResolveActivationCommand(ws, ws, "/bin/zsh", "zsh", null, false);
                Assert.Equal(" source " + Path.Combine(info.EnvPath, "bin", "activate"), cmd);
                // conda prefix -> conda activate
                t.File("conda/x/conda-meta/history", "");
                string condaPrefix = Path.Combine(t.Path, "conda", "x");
                PythonVenvActivator.SelectedEnvironmentOverride = root => root == ws ? condaPrefix : null;
                Assert.Equal(" conda activate " + condaPrefix, PythonVenvActivator.ResolveActivationCommand(ws, ws, "/bin/bash", "bash", null, false));
            }
            finally
            {
                PythonVenvActivator.SelectedEnvironmentOverride = prevOverride;
            }
        }
    }
}
