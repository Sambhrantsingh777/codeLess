using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using codeLess.Studio;
using Xunit;

namespace codeLess.Avalonia.Tests
{
    /// <summary>NET-1: redirects are checked against Offline Mode and recorded hop by hop.</summary>
    [Collection("SequentialWorkbench")]
    public class EngineReviewNetworkTests : IDisposable
    {
        public EngineReviewNetworkTests() { NetworkLedger.SetOfflineMode(false); }
        public void Dispose() { NetworkLedger.SetOfflineMode(false); }

        /// <summary>Loopback server: "/r" answers 302 to <paramref name="location"/>, anything else 200 "done".</summary>
        private static (int port, TcpListener listener) RedirectServer(Func<int, string> location)
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            int port = ((IPEndPoint)listener.LocalEndpoint).Port;
            Task.Run(async () =>
            {
                while (true)
                {
                    TcpClient c;
                    try { c = await listener.AcceptTcpClientAsync(); } catch { return; }
                    _ = Task.Run(() =>
                    {
                        using (c)
                        using (var s = c.GetStream())
                        {
                            var head = new StringBuilder();
                            var buf = new byte[1];
                            while (!head.ToString().EndsWith("\r\n\r\n") && s.Read(buf, 0, 1) == 1) head.Append((char)buf[0]);
                            string path = head.ToString().Split(' ').Length > 1 ? head.ToString().Split(' ')[1] : "/";
                            string resp = path.StartsWith("/r")
                                ? "HTTP/1.1 302 Found\r\nLocation: " + location(port) + "\r\nContent-Length: 0\r\nConnection: close\r\n\r\n"
                                : "HTTP/1.1 200 OK\r\nContent-Type: text/plain\r\nContent-Length: 4\r\nConnection: close\r\n\r\ndone";
                            byte[] b = Encoding.ASCII.GetBytes(resp);
                            s.Write(b, 0, b.Length);
                        }
                    });
                }
            });
            return (port, listener);
        }

        private static string Feature(string p) => p + " " + Guid.NewGuid().ToString("N").Substring(0, 8);

        [Fact]
        public void NET1_OfflineMode_RedirectToExternalHost_IsBlocked_AndBothHopsAreInTheLedger()
        {
            var (port, listener) = RedirectServer(p => "http://codeless-offline-leak.invalid/pixel");
            try
            {
                NetworkLedger.SetOfflineMode(true);
                string f = Feature("NET1 ledger");
                Assert.Throws<NetworkBlockedException>(() => LedgerHttp.Send(new LedgerHttpRequest { Feature = f, Url = $"http://127.0.0.1:{port}/r", TimeoutMs = 5000 }));
                var entries = NetworkLedger.Snapshot().Where(e => e.Feature == f).ToList();
                Assert.Equal(2, entries.Count);
                Assert.Equal(302, entries[0].Status);
                Assert.False(entries[0].Blocked);
                Assert.True(entries[1].Blocked);
                Assert.Contains("codeless-offline-leak.invalid", entries[1].Host);

                var res = HttpClientRunner.SendAsync(new HttpResolvedRequest { Method = "GET", Url = $"http://127.0.0.1:{port}/r" }, 5000, CancellationToken.None).Result;
                Assert.True(res.Blocked, res.Error);
                Assert.Contains("Redirect blocked", res.Error);

                Assert.Null(LedgerHttp.DownloadBytes(Feature("NET1 img"), $"http://127.0.0.1:{port}/r", 5000, 1024));
            }
            finally { listener.Stop(); }
        }

        [Fact]
        public void NET1_Online_RedirectsAreFollowed_AndEveryHopIsRecorded()
        {
            var (port, listener) = RedirectServer(p => $"http://127.0.0.1:{p}/final");
            try
            {
                string f = Feature("NET1 online");
                var res = LedgerHttp.Send(new LedgerHttpRequest { Feature = f, Url = $"http://127.0.0.1:{port}/r", TimeoutMs = 5000 });
                Assert.Equal(200, res.Status);
                Assert.Equal("done", res.Body);
                var entries = NetworkLedger.Snapshot().Where(e => e.Feature == f).ToList();
                Assert.Equal(new[] { 302, 200 }, entries.Select(e => e.Status).ToArray());
                Assert.EndsWith("/final", entries[1].Path);

                string f2 = Feature("NET1 handler");
                using (var client = new HttpClient(new LedgerHttpMessageHandler(f2, new HttpClientHandler())))
                {
                    string body = client.GetStringAsync($"http://127.0.0.1:{port}/r").Result;
                    Assert.Equal("done", body);
                }
                Assert.True(SpinWait.SpinUntil(() => NetworkLedger.Snapshot().Count(e => e.Feature == f2) == 2, 3000));

                var hr = HttpClientRunner.SendAsync(new HttpResolvedRequest { Method = "GET", Url = $"http://127.0.0.1:{port}/r" }, 5000, CancellationToken.None).Result;
                Assert.Equal(200, hr.StatusCode);
                Assert.Single(hr.Redirects);
                var noRedirect = HttpClientRunner.SendAsync(new HttpResolvedRequest { Method = "GET", Url = $"http://127.0.0.1:{port}/r", NoRedirect = true }, 5000, CancellationToken.None).Result;
                Assert.Equal(302, noRedirect.StatusCode);
            }
            finally { listener.Stop(); }
        }

        [Fact]
        public void NET1_RedirectRules_MethodChange_AndHttpsDowngradeRefused()
        {
            string m = "POST";
            bool drop;
            Assert.Equal("http://h/x", LedgerHttp.ResolveRedirect("http://h/a", 302, "/x", ref m, out drop));
            Assert.Equal("GET", m);
            Assert.True(drop);
            m = "POST";
            LedgerHttp.ResolveRedirect("http://h/a", 307, "/x", ref m, out drop);
            Assert.Equal("POST", m);
            Assert.False(drop);
            m = "GET";
            Assert.Null(LedgerHttp.ResolveRedirect("https://h/a", 301, "http://h/b", ref m, out drop));
            Assert.Null(LedgerHttp.ResolveRedirect("http://h/a", 301, "file:///etc/passwd", ref m, out drop));
        }

        [Theory]
        [InlineData("https://api.telegram.org/bot" + "123456789:AAH3x9abcdefghijklmnopqrstuvwxyz" + "/sendMessage", "https://api.telegram.org/bot***/sendMessage")]
        [InlineData("https://hooks.slack.com/services/T0123ABCD/" + "B0456EFGH/" + "abcdefghijklmnop12345678", "https://hooks.slack.com/services/T0123ABCD/B0456EFGH/***")]
        [InlineData("https://discord.com/api/webhooks/" + "123456789012345678/" + "aB3dE6fG9hJ2kL5mN8pQ1rS4tU7vW0xY3zA6bC9dE2fG5", "https://discord.com/api/webhooks/123456789012345678/***")]
        [InlineData("https://api.openai.com/v1/chat/completions", "https://api.openai.com/v1/chat/completions")]
        [InlineData("https://generativelanguage.googleapis.com/v1beta/models/gemini-1.5-pro:generateContent", "https://generativelanguage.googleapis.com/v1beta/models/gemini-1.5-pro:generateContent")]
        public void SEC1_SecretsInUrlPaths_AreMasked(string url, string expected)
        {
            Assert.Equal(expected, NetworkLedger.RedactUrl(url));
        }
    }

    /// <summary>EXT-1, SH-1, FS-1, FS-2, AGT-2, AGT-3, AGT-4.</summary>
    public class EngineReviewMiscTests : IDisposable
    {
        private readonly string root;

        public EngineReviewMiscTests()
        {
            root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "cl-misc-" + Guid.NewGuid().ToString("N").Substring(0, 8)));
            Directory.CreateDirectory(root);
        }

        public void Dispose()
        {
            try { Directory.Delete(root, true); } catch { }
        }

        [Fact]
        public void EXT1_PackageJsonVersion_CannotEscapeTheExtensionsFolder()
        {
            string extRoot = Path.Combine(root, "home", ".codeless", "extensions");
            Directory.CreateDirectory(extRoot);
            string plugin = Path.Combine(root, "plugin");
            Directory.CreateDirectory(plugin);
            File.WriteAllText(Path.Combine(plugin, "package.json"), "{ \"publisher\": \"evil\", \"name\": \"x\", \"version\": \"1/../../../../escaped\" }");
            File.WriteAllText(Path.Combine(plugin, "payload.txt"), "owned");
            var host = new VsixAndPluginHostEngine(new List<StudioTheme>(), extRoot);
            host.LoadPluginFromDirectory(plugin, new List<StudioTheme>());
            Assert.False(Directory.Exists(Path.Combine(root, "escaped")));
            Assert.False(File.Exists(Path.Combine(root, "home", "escaped", "payload.txt")));
            foreach (string f in Directory.GetFiles(root, "payload.txt", SearchOption.AllDirectories))
                Assert.True(f.StartsWith(plugin) || f.StartsWith(extRoot + Path.DirectorySeparatorChar), "copied outside the extensions folder: " + f);

            Assert.Equal(extRoot, Path.GetDirectoryName(VsixAndPluginHostEngine.SafeExtensionFolder(extRoot, "..", "")));
            Assert.Null(VsixAndPluginHostEngine.SafeExtensionFolder(null, "a", "1"));
            string ok = VsixAndPluginHostEngine.SafeExtensionFolder(extRoot, "pub.name", "1.2.3");
            Assert.Equal(extRoot, Path.GetDirectoryName(ok));
            Assert.Equal("pub.name-1.2.3", Path.GetFileName(ok));
            Assert.Equal(extRoot, Path.GetDirectoryName(VsixAndPluginHostEngine.SafeExtensionFolder(extRoot, "a/../../b", "1/../../c")));
        }

        [Fact]
        public void SH1_NulSeparatedEnv_KeepsMultiLineValuesAndBlankLines()
        {
            string m = "__M__";
            string text = "noise\n" + m + "PATH=/usr/bin\0MULTI=line1\n\nFAKE=notavar\0EMPTY=\0" + m + "trailer";
            var env = UserShellEnvironment.ParseEnvBlock(text, m);
            Assert.Equal("/usr/bin", env["PATH"]);
            Assert.Equal("line1\n\nFAKE=notavar", env["MULTI"]);
            Assert.False(env.ContainsKey("FAKE"));
            Assert.Equal("", env["EMPTY"]);
        }

        [Fact]
        public void FS1_OnlySymlinksAndJunctionsAreSkipped()
        {
            string dir = Path.Combine(root, "real");
            Directory.CreateDirectory(dir);
            Assert.False(LlmHarnessAndAgentEngine.IsSymlinkOrJunction(dir, new DirectoryInfo(dir).Attributes));
            if (Path.DirectorySeparatorChar == '/')
            {
                string link = Path.Combine(root, "link");
                Directory.CreateSymbolicLink(link, dir);
                Assert.True(LlmHarnessAndAgentEngine.IsSymlinkOrJunction(link, new DirectoryInfo(link).Attributes));
            }
            // A reparse point that is not a name surrogate (OneDrive placeholder) is walked: verified on Windows by the
            // attribute check; here at least the non-reparse case must not be skipped.
            Assert.False(LlmHarnessAndAgentEngine.IsSymlinkOrJunction(dir, FileAttributes.Directory));
        }

        [Fact]
        public void FS2_WorkspaceInsideARepo_HonoursParentGitignore_AndInfoExclude()
        {
            string repo = Path.Combine(root, "repo");
            Directory.CreateDirectory(Path.Combine(repo, ".git", "info"));
            File.WriteAllText(Path.Combine(repo, ".gitignore"), "*.log\ngenerated/\n");
            File.WriteAllText(Path.Combine(repo, ".git", "info", "exclude"), "secret.txt\n");
            string ws = Path.Combine(repo, "packages", "app");
            Directory.CreateDirectory(Path.Combine(ws, "generated"));
            File.WriteAllText(Path.Combine(ws, "main.ts"), "x");
            File.WriteAllText(Path.Combine(ws, "debug.log"), "x");
            File.WriteAllText(Path.Combine(ws, "secret.txt"), "x");
            File.WriteAllText(Path.Combine(ws, "generated", "out.ts"), "x");
            var rules = new WorkspaceIgnoreRules(ws, null, true);
            bool capped;
            var files = rules.EnumerateFiles(new[] { ws }, 1000, CancellationToken.None, out capped).Select(Path.GetFileName).ToList();
            Assert.Contains("main.ts", files);
            Assert.DoesNotContain("debug.log", files);
            Assert.DoesNotContain("secret.txt", files);
            Assert.DoesNotContain("out.ts", files);
            Assert.True(rules.IsPathExcluded(Path.Combine(ws, "debug.log"), false));
        }

        [Fact]
        public void AGT2_SymlinkCreatedAfterStaging_CannotRedirectTheAcceptOutsideTheWorkspace()
        {
            if (Path.DirectorySeparatorChar == '\\') return;
            string ws = Path.Combine(root, "ws");
            string outside = Path.Combine(root, "outside");
            Directory.CreateDirectory(ws);
            Directory.CreateDirectory(outside);
            File.WriteAllText(Path.Combine(outside, "target.txt"), "precious");
            string staged = Path.Combine(ws, "sub", "target.txt");
            var cs = AgentChangeSet.FromStaged(new Dictionary<string, string> { { staged, "overwritten" } }, ws, null, "t");
            Directory.CreateSymbolicLink(Path.Combine(ws, "sub"), outside); // e.g. by an approved terminal command
            var res = AgentChangeSetApplier.Accept(cs, cs.Files, null, true);
            Assert.Empty(res.Written);
            Assert.Contains(res.Errors, e => e.Contains("outside the workspace"));
            Assert.Equal("precious", File.ReadAllText(Path.Combine(outside, "target.txt")));
        }

        [Fact]
        public void AGT4_Latin1File_WithUnrepresentableProposal_IsSavedAsUtf8_NotQuestionMarks()
        {
            string f = Path.Combine(root, "legacy.txt");
            File.WriteAllBytes(f, new byte[] { (byte)'c', (byte)'a', (byte)'f', 0xE9, (byte)'\n' });
            string note;
            AgentChangeSetApplier.WriteTextLikeExisting(f, "café → ok 😀\n", out note);
            Assert.NotNull(note);
            Assert.Equal("café → ok 😀\n", File.ReadAllText(f, Encoding.UTF8));

            string g = Path.Combine(root, "legacy2.txt");
            File.WriteAllBytes(g, new byte[] { (byte)'c', (byte)'a', (byte)'f', 0xE9, (byte)'\n' });
            AgentChangeSetApplier.WriteTextLikeExisting(g, "café!\n", out note);
            Assert.Null(note);
            Assert.Equal(new byte[] { (byte)'c', (byte)'a', (byte)'f', 0xE9, (byte)'!', (byte)'\n' }, File.ReadAllBytes(g)); // still Latin-1

            // The editor's own save uses the same rule (no silent '?').
            string h = Path.Combine(root, "legacy3.txt");
            File.WriteAllBytes(h, new byte[] { 0xE9, (byte)'\n' });
            var buf = new EditorTabBuffer { FilePath = h };
            string eol; Encoding enc; bool bom; bool fin;
            buf.Content = EditorTabBuffer.ReadFilePreservingMetadata(h, out eol, out enc, out bom, out fin) + "→";
            buf.FileEncoding = enc; buf.EolSequence = eol; buf.HasFinalNewline = fin;
            buf.SyncLinesFromContent();
            buf.WriteToDisk(h);
            Assert.Contains("→", File.ReadAllText(h, Encoding.UTF8));
            Assert.Equal(65001, buf.FileEncoding.CodePage);
        }

        [Fact]
        public void AGT3_TwoWindowsOnOneWorkspace_DontDropEachOthersTurnsOrBlobs()
        {
            // Explicit root: mutating the static CheckpointsRoot raced with AgentTrustCoreTests running in parallel.
            string cpRoot = Path.Combine(root, "checkpoints");
            try
            {
                string ws = Path.Combine(root, "cpws");
                Directory.CreateDirectory(ws);
                string a = Path.Combine(ws, "a.txt"), b = Path.Combine(ws, "b.txt");
                File.WriteAllText(a, "a0"); File.WriteAllText(b, "b0");
                var w1 = new AgentTurnCheckpointStore(ws, cpRoot);
                var w2 = new AgentTurnCheckpointStore(ws, cpRoot); // second window, loaded before w1 wrote anything
                w1.RecordFileWrite("t1", "turn 1", a, Encoding.UTF8.GetBytes("a0"), Encoding.UTF8.GetBytes("a1"));
                w2.RecordFileWrite("t2", "turn 2", b, Encoding.UTF8.GetBytes("b0"), Encoding.UTF8.GetBytes("b1"));
                var reloaded = new AgentTurnCheckpointStore(ws, cpRoot);
                var ids = reloaded.Turns.Select(t => t.Id).ToList();
                Assert.Contains("t1", ids);
                Assert.Contains("t2", ids);
                // w2's save must not have garbage-collected w1's blobs.
                foreach (var t in reloaded.Turns)
                    foreach (var f in t.Files)
                    {
                        if (f.PreHash != null) Assert.True(File.Exists(Path.Combine(reloaded.BlobDir, f.PreHash)), "missing pre blob of " + t.Id);
                        if (f.PostHash != null) Assert.True(File.Exists(Path.Combine(reloaded.BlobDir, f.PostHash)), "missing post blob of " + t.Id);
                    }
            }
            finally { }
        }
    }

    /// <summary>TASK-1 / TASK-2 / DBG-1 / DBG-2 / DBG-3.</summary>
    public class EngineReviewTaskDebugTests
    {
        private static TaskExecution FinishedExec(TaskDefinition t, int code)
        {
            var ex = TaskExecution.Prepare(t, new DebugVariableContext { WorkspaceFolder = Path.GetTempPath() }, false);
            ex.MarkStarted();
            ex.OnExited(code);
            return ex;
        }

        [Fact]
        public void TASK1_WhenFinished_AfterTheTaskAlreadyExited_StillFires()
        {
            var ex = FinishedExec(new TaskDefinition { Label = "fast", Command = "true" }, 0);
            bool? ok = null;
            ex.WhenFinished((x, success) => ok = success);
            Assert.True(ok);
        }

        private static TaskChainRunner Runner(List<TaskDefinition> all, List<string> started, Func<TaskDefinition, int> exitCode, bool synchronousExit = true)
        {
            return new TaskChainRunner
            {
                LoadTasks = () => all,
                StartLeaf = t =>
                {
                    lock (started) started.Add(t.Label);
                    var ex = TaskExecution.Prepare(t, new DebugVariableContext { WorkspaceFolder = Path.GetTempPath() }, false);
                    ex.MarkStarted();
                    if (synchronousExit) ex.OnExited(exitCode(t)); // exits before the runner subscribes (the old race)
                    else Task.Run(() => { Thread.Sleep(30); ex.OnExited(exitCode(t)); });
                    return ex;
                },
                PostToUi = a => a()
            };
        }

        [Fact]
        public void TASK1_TASK2_FastDependencies_CompositeDependencies_AndOrder()
        {
            var all = new List<TaskDefinition>
            {
                new TaskDefinition { Label = "mkdir", Command = "mkdir -p out" },
                new TaskDefinition { Label = "gen", Command = "gen" },
                new TaskDefinition { Label = "prep", DependsOn = { "mkdir", "gen" }, DependsOrderSequence = true },          // composite
                new TaskDefinition { Label = "build", Command = "build", DependsOn = { "prep" }, DependsOrderSequence = true },
            };
            var started = new List<string>();
            bool? ok = null;
            Runner(all, started, t => 0).Run(all[3], (s, m) => ok = s);
            Assert.True(ok);
            Assert.Equal(new[] { "mkdir", "gen", "build" }, started.ToArray());

            started.Clear();
            ok = null;
            string msg = null;
            Runner(all, started, t => t.Label == "gen" ? 2 : 0).Run(all[3], (s, m) => { ok = s; msg = m; });
            Assert.False(ok);
            Assert.Contains("gen", msg);
            Assert.DoesNotContain("build", started);
        }

        [Fact]
        public void TASK2_ParallelDependencies_AllFinishBeforeTheTask_CyclesAndMissingAreReported()
        {
            var all = new List<TaskDefinition>
            {
                new TaskDefinition { Label = "a", Command = "a" },
                new TaskDefinition { Label = "b", Command = "b" },
                new TaskDefinition { Label = "c", Command = "c", DependsOn = { "a", "b" }, DependsOrderSequence = false },
                new TaskDefinition { Label = "x", Command = "x", DependsOn = { "y" } },
                new TaskDefinition { Label = "y", Command = "y", DependsOn = { "x" } },
                new TaskDefinition { Label = "m", Command = "m", DependsOn = { "nope" } },
            };
            var started = new List<string>();
            var done = new ManualResetEventSlim();
            bool? ok = null;
            Runner(all, started, t => 0, synchronousExit: false).Run(all[2], (s, m) => { ok = s; done.Set(); });
            Assert.True(done.Wait(5000));
            Assert.True(ok);
            Assert.Equal("c", started.Last());
            Assert.Equal(3, started.Count);

            string msg = null;
            Runner(all, new List<string>(), t => 0).Run(all[3], (s, m) => msg = m);
            Assert.Contains("cycle", msg);
            Runner(all, new List<string>(), t => 0).Run(all[5], (s, m) => msg = m);
            Assert.Contains("'nope'", msg);
            Assert.False(new TaskDefinition().DependsOn.Count > 0);
        }

        [Fact]
        public void TASK2_BackgroundDependency_IsReadyWhenItsEndsPatternMatches()
        {
            var watch = new TaskDefinition { Label = "watch", Command = "tsc -w", IsBackground = true, ProblemMatchers = { "$tsc-watch" } };
            var ex = TaskExecution.Prepare(watch, new DebugVariableContext { WorkspaceFolder = Path.GetTempPath() }, false);
            ex.MarkStarted();
            bool? ready = null;
            ex.WhenFinished((x, ok) => ready = ok);
            ex.OnOutputText("Starting compilation in watch mode...\n");
            Assert.Null(ready);
            ex.OnOutputText("Found 0 errors. Watching for file changes.\n");
            Assert.True(ready);
            Assert.True(ex.IsRunning); // still watching
        }

        [Fact]
        public void TASK2_TasksJson_DependsOrderDefaultsToParallel_LikeVsCode()
        {
            string err;
            var tasks = TaskConfiguration.ParseTasksJson("{ \"version\": \"2.0.0\", \"tasks\": [ { \"label\": \"p\", \"dependsOn\": [\"a\"] }, { \"label\": \"s\", \"dependsOn\": [\"a\"], \"dependsOrder\": \"sequence\" } ] }", "osx", out err);
            Assert.False(tasks[0].DependsOrderSequence);
            Assert.True(tasks[1].DependsOrderSequence);
        }

        private sealed class TaskHost : IDebugUiHost, IDebugTaskHost
        {
            public readonly FakeDebugHost Inner = new FakeDebugHost();
            public readonly List<string> Tasks = new List<string>();
            public bool TaskSucceeds = true;
            public void RunTaskByLabel(string label, Action<bool, string> completed) { Tasks.Add(label); completed(TaskSucceeds, TaskSucceeds ? null : "exit code 1"); }
            public void Post(Action action) => Inner.Post(action);
            public Task<int?> RunInTerminalAsync(string title, string cwd, List<string> args, Dictionary<string, string> env) => Inner.RunInTerminalAsync(title, cwd, args, env);
            public void RevealLocation(string path, int line) => Inner.RevealLocation(path, line);
            public IList<EditorTabBuffer> GetOpenBuffers() => Inner.GetOpenBuffers();
            public void RefreshEditors() { }
            public void ShowStatus(string message) => Inner.ShowStatus(message);
            public void ShowDebugError(string message, string hint, string installCommand) => Inner.ShowDebugError(message, hint, installCommand);
            public string WorkspaceRoot => Inner.Root;
            public IdePersistentSettings Settings => Inner.SettingsObj;
        }

        [Fact]
        public void DBG1_PreLaunchTask_RunsFirst_AndAFailureDoesNotDebugAStaleBuild()
        {
            string ws = TestTrust.Trust(Path.Combine(Path.GetTempPath(), "cl-dbg1-" + Guid.NewGuid().ToString("N").Substring(0, 6)));
            Directory.CreateDirectory(ws);
            var cfg = new DebugLaunchConfig { Name = "Launch", Type = "no-such-adapter", Request = "launch", FromLaunchJson = true };
            cfg.Properties["preLaunchTask"] = "build";
            cfg.Properties["osx"] = new Dictionary<string, object> { { "preLaunchTask", "build-mac" } };
            cfg.Properties["linux"] = new Dictionary<string, object> { { "preLaunchTask", "build-linux" } };
            string expected = DebugLaunchConfigurations.GetTaskLabel(cfg, "preLaunchTask");
            Assert.Equal(TaskConfiguration.CurrentPlatformKey == "osx" ? "build-mac" : TaskConfiguration.CurrentPlatformKey == "linux" ? "build-linux" : "build", expected);

            var host = new TaskHost { TaskSucceeds = false };
            host.Inner.Root = ws;
            var ctl = new DebugController(host);
            ctl.StartAsync(cfg, null).Wait(5000);
            Assert.Equal(new[] { expected }, host.Tasks.ToArray());
            Assert.Contains(host.Inner.Errors, e => e.Contains("preLaunchTask") && e.Contains("failed"));
            Assert.DoesNotContain(host.Inner.Statuses, s => s.StartsWith("Starting "));

            var ok = new TaskHost { TaskSucceeds = true };
            ok.Inner.Root = ws;
            var ctl2 = new DebugController(ok);
            ctl2.StartAsync(cfg, null).Wait(10000);
            Assert.Single(ok.Tasks);
            Assert.Contains(ok.Inner.Statuses, s => s.StartsWith("Starting ")); // went on to start the adapter

            var args = DebugLaunchConfigurations.PrepareLaunchArguments(cfg, new DebugVariableContext { WorkspaceFolder = ws });
            Assert.False(args.ContainsKey("preLaunchTask"));
            Assert.False(args.ContainsKey("osx"));
            Assert.False(args.ContainsKey("linux"));
        }

        private sealed class BlockingStream : Stream
        {
            public readonly ManualResetEventSlim Release = new ManualResetEventSlim();
            public override bool CanRead => false;
            public override bool CanSeek => false;
            public override bool CanWrite => true;
            public override long Length => 0;
            public override long Position { get => 0; set { } }
            public override void Flush() { }
            public override int Read(byte[] buffer, int offset, int count) => 0;
            public override long Seek(long offset, SeekOrigin origin) => 0;
            public override void SetLength(long value) { }
            public override void Write(byte[] buffer, int offset, int count) => Release.Wait(30000);
        }

        [Fact]
        public void DBG2_AHungAdapter_CannotBlockTheCaller_AndHugeFramesAreRejected()
        {
            var output = new BlockingStream();
            var input = new BlockingInput();
            var client = new DapClient(input, output);
            client.Start();
            var sw = Stopwatch.StartNew();
            var t = client.SendRequestAsync("setBreakpoints", new Dictionary<string, object> { { "x", new string('a', 1 << 20) } }, 500);
            Assert.True(sw.ElapsedMilliseconds < PerfBudget.Ms(400), "SendRequestAsync blocked on the adapter's stdin: " + sw.ElapsedMilliseconds + " ms");
            Assert.True(t.Wait(5000));
            Assert.False(t.Result.Success);
            output.Release.Set();
            input.Release.Set();
            client.Dispose();

            var reader = new DapMessageReader(new MemoryStream(Encoding.ASCII.GetBytes("Content-Length: 2000000000\r\n\r\n{}")));
            Assert.Null(reader.ReadMessage());
        }

        private sealed class BlockingInput : Stream
        {
            public readonly ManualResetEventSlim Release = new ManualResetEventSlim();
            public override bool CanRead => true;
            public override bool CanSeek => false;
            public override bool CanWrite => false;
            public override long Length => 0;
            public override long Position { get => 0; set { } }
            public override void Flush() { }
            public override int Read(byte[] buffer, int offset, int count) { Release.Wait(30000); return 0; }
            public override long Seek(long offset, SeekOrigin origin) => 0;
            public override void SetLength(long value) { }
            public override void Write(byte[] buffer, int offset, int count) { }
        }

        [Fact]
        public void DBG3_DisposingTheAdapterConnection_KillsTheDebuggeeItStarted()
        {
            if (Path.DirectorySeparatorChar == '\\') return;
            string tag = (61000 + new Random().Next(999)).ToString();
            var psi = new ProcessStartInfo("/bin/sh", "-c \"sleep " + tag + " & wait\"") { UseShellExecute = false };
            var adapter = Process.Start(psi);
            Thread.Sleep(300);
            Func<int> count = () =>
            {
                var p = Process.Start(new ProcessStartInfo("pgrep", "-f \"sleep " + tag + "\"") { RedirectStandardOutput = true, UseShellExecute = false });
                string o = p.StandardOutput.ReadToEnd();
                p.WaitForExit(3000);
                return o.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length;
            };
            Assert.True(count() >= 1);
            new DebugAdapterConnection { Process = adapter }.Dispose();
            Assert.True(SpinWait.SpinUntil(() => count() == 0, 5000), "the debuggee child survived");
        }
    }
}
