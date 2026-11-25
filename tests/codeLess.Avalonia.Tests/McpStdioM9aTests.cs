using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using codeLess.Studio;
using Xunit;

namespace codeLess.Avalonia.Tests
{
    public class McpStdioM9aTests : IDisposable
    {
        private readonly List<string> _tempDirs = new List<string>();
        private readonly List<IDisposable> _disposables = new List<IDisposable>();

        private string CreateTempWorkspace()
        {
            string dir = Path.Combine(Path.GetTempPath(), "codeless-mcp-test-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            dir = Path.GetFullPath(dir);
            TestTrust.Trust(dir);
            _tempDirs.Add(dir);
            return dir;
        }

        public void Dispose()
        {
            for (int i = _disposables.Count - 1; i >= 0; i--)
            {
                try { _disposables[i].Dispose(); } catch { }
            }
            _disposables.Clear();

            foreach (string dir in _tempDirs)
            {
                try
                {
                    if (Directory.Exists(dir))
                        Directory.Delete(dir, true);
                }
                catch { }
            }
            _tempDirs.Clear();
        }

        /// <summary>
        /// Tiny in-process fake MCP server run over BlockingPipeStream via InMemoryPipeMcpTransport.
        /// Speaks newline-delimited JSON-RPC 2.0 per MCP stdio specification.
        /// </summary>
        private sealed class FakeInProcessMcpServer : IDisposable
        {
            public readonly InMemoryPipeMcpTransport Transport = new InMemoryPipeMcpTransport();
            public bool ReceivedInitialize;
            public bool ReceivedInitializedNotification;
            public int ToolsListCount;
            public int ToolsCallCount;
            public bool DelayToolCall;
            public int DelayMs = 500;
            public bool CrashOnToolCall;
            public string CrashStderr = "FATAL: simulated MCP server crash";
            private Thread _serverThread;
            private volatile bool _stopped;
            private readonly JavaScriptSerializer _ser = new JavaScriptSerializer();

            public FakeInProcessMcpServer()
            {
                Transport.OnStart = StartServerLoop;
                Transport.OnDispose = () => { _stopped = true; };
            }

            private void StartServerLoop()
            {
                _serverThread = new Thread(ServerLoop)
                {
                    IsBackground = true,
                    Name = "FakeInProcessMcpServer"
                };
                _serverThread.Start();
            }

            private void ServerLoop()
            {
                try
                {
                    var reader = new StreamReader(Transport.ServerInput, Encoding.UTF8, false, 1024, true);
                    var writer = new StreamWriter(Transport.ServerOutput, new UTF8Encoding(false), 1024, true)
                    {
                        NewLine = "\n",
                        AutoFlush = true
                    };

                    string line;
                    while (!_stopped && (line = reader.ReadLine()) != null)
                    {
                        if (string.IsNullOrWhiteSpace(line)) continue;
                        var msg = _ser.DeserializeObject(line) as Dictionary<string, object>;
                        if (msg == null) continue;

                        string method = msg.ContainsKey("method") ? Convert.ToString(msg["method"]) : "";
                        object id = msg.ContainsKey("id") ? msg["id"] : null;

                        if (method == "initialize")
                        {
                            ReceivedInitialize = true;
                            var resp = new Dictionary<string, object>
                            {
                                ["jsonrpc"] = "2.0",
                                ["id"] = id,
                                ["result"] = new Dictionary<string, object>
                                {
                                    ["protocolVersion"] = "2024-11-05",
                                    ["capabilities"] = new Dictionary<string, object>
                                    {
                                        ["tools"] = new Dictionary<string, object>()
                                    },
                                    ["serverInfo"] = new Dictionary<string, object>
                                    {
                                        ["name"] = "fake-mcp",
                                        ["version"] = "1.0.0"
                                    }
                                }
                            };
                            writer.WriteLine(_ser.Serialize(resp));
                        }
                        else if (method == "notifications/initialized")
                        {
                            ReceivedInitializedNotification = true;
                        }
                        else if (method == "tools/list")
                        {
                            ToolsListCount++;
                            var resp = new Dictionary<string, object>
                            {
                                ["jsonrpc"] = "2.0",
                                ["id"] = id,
                                ["result"] = new Dictionary<string, object>
                                {
                                    ["tools"] = new object[]
                                    {
                                        new Dictionary<string, object>
                                        {
                                            ["name"] = "echo",
                                            ["description"] = "Echoes back a message",
                                            ["inputSchema"] = new Dictionary<string, object>
                                            {
                                                ["type"] = "object",
                                                ["properties"] = new Dictionary<string, object>
                                                {
                                                    ["text"] = new Dictionary<string, object> { ["type"] = "string" }
                                                },
                                                ["required"] = new object[] { "text" }
                                            }
                                        },
                                        new Dictionary<string, object>
                                        {
                                            ["name"] = "fail_rpc",
                                            ["description"] = "Returns a JSON-RPC error",
                                            ["inputSchema"] = new Dictionary<string, object> { ["type"] = "object" }
                                        }
                                    }
                                }
                            };
                            writer.WriteLine(_ser.Serialize(resp));
                        }
                        else if (method == "tools/call")
                        {
                            ToolsCallCount++;
                            if (CrashOnToolCall)
                            {
                                Transport.SimulateServerCrash(CrashStderr);
                                return;
                            }
                            if (DelayToolCall)
                            {
                                Thread.Sleep(DelayMs);
                            }

                            var p = msg.ContainsKey("params") ? msg["params"] as Dictionary<string, object> : null;
                            string toolName = p != null && p.ContainsKey("name") ? Convert.ToString(p["name"]) : "";
                            var args = p != null && p.ContainsKey("arguments") ? p["arguments"] as Dictionary<string, object> : null;

                            if (toolName == "fail_rpc")
                            {
                                var errResp = new Dictionary<string, object>
                                {
                                    ["jsonrpc"] = "2.0",
                                    ["id"] = id,
                                    ["error"] = new Dictionary<string, object>
                                    {
                                        ["code"] = -32602,
                                        ["message"] = "Invalid arguments for fail_rpc"
                                    }
                                };
                                writer.WriteLine(_ser.Serialize(errResp));
                            }
                            else
                            {
                                string textArg = args != null && args.ContainsKey("text") ? Convert.ToString(args["text"]) : "";
                                var okResp = new Dictionary<string, object>
                                {
                                    ["jsonrpc"] = "2.0",
                                    ["id"] = id,
                                    ["result"] = new Dictionary<string, object>
                                    {
                                        ["isError"] = false,
                                        ["content"] = new object[]
                                        {
                                            new Dictionary<string, object>
                                            {
                                                ["type"] = "text",
                                                ["text"] = "ECHO:" + textArg
                                            }
                                        }
                                    }
                                };
                                writer.WriteLine(_ser.Serialize(okResp));
                            }
                        }
                    }
                }
                catch { }
            }

            public void Dispose()
            {
                _stopped = true;
                Transport.Dispose();
            }
        }

        [Fact]
        public void McpConfigLoader_MergesUserAndWorkspaceSettings_WorkspaceWins()
        {
            string userJson = @"{
                ""codeless.mcp"": {
                    ""alpha"": {
                        ""type"": ""local"",
                        ""command"": [""node"", ""user-alpha.js""],
                        ""environment"": { ""API_TOKEN"": ""user-tok"", ""KEEP_ME"": ""1"" },
                        ""enabled"": true
                    },
                    ""beta"": {
                        ""type"": ""local"",
                        ""command"": [""python"", ""beta.py""],
                        ""enabled"": true
                    }
                }
            }";

            string workspaceJson = @"{
                // Workspace overrides alpha command & env, disables beta, adds gamma
                ""codeless.mcp"": {
                    ""alpha"": {
                        ""type"": ""local"",
                        ""command"": [""node"", ""ws-alpha.js"", ""--stdio""],
                        ""environment"": { ""API_TOKEN"": ""ws-tok"" },
                        ""enabled"": true
                    },
                    ""beta"": {
                        ""enabled"": false
                    },
                    ""gamma"": {
                        ""type"": ""local"",
                        ""command"": [""/usr/local/bin/gamma-mcp""],
                        ""enabled"": true
                    }
                }
            }";

            var merged = McpConfigLoader.LoadMergedConfig(null, null, userJson, workspaceJson);
            Assert.Equal(3, merged.Count);

            Assert.True(merged["alpha"].Enabled);
            Assert.Equal(new[] { "node", "ws-alpha.js", "--stdio" }, merged["alpha"].Command);
            Assert.Equal("ws-tok", merged["alpha"].Environment["API_TOKEN"]);
            Assert.Equal("1", merged["alpha"].Environment["KEEP_ME"]);

            Assert.False(merged["beta"].Enabled);
            Assert.True(merged["gamma"].Enabled);
            Assert.Equal(new[] { "/usr/local/bin/gamma-mcp" }, merged["gamma"].Command);
        }

        [Fact]
        public void McpStdioClient_Handshake_ToolsList_And_ToolsCall_OverInProcessPipe()
        {
            using (var fake = new FakeInProcessMcpServer())
            {
                var cfg = new McpServerConfig
                {
                    Name = "testserver",
                    Type = "local",
                    Command = new List<string> { "fake" },
                    Enabled = true,
                    TimeoutMs = 5000
                };

                using (var client = new McpStdioClient(cfg, fake.Transport))
                {
                    client.ConnectAndInitialize();
                    Assert.True(fake.ReceivedInitialize);
                    Assert.True(client.IsInitialized);

                    var tools = client.ListTools();
                    Assert.True(fake.ReceivedInitializedNotification);
                    Assert.Equal(2, tools.Count);
                    Assert.Equal("testserver_echo", tools[0].ExposedName);
                    Assert.Equal("Echoes back a message", tools[0].Description);
                    Assert.Contains("\"text\"", tools[0].InputSchemaJson);

                    var callRes = client.CallTool("echo", new Dictionary<string, object> { ["text"] = "hello-mcp" });
                    Assert.True(callRes.Success);
                    Assert.Equal("testserver_echo", callRes.ToolName);
                    Assert.Equal("ECHO:hello-mcp", callRes.Output);
                }

                Assert.True(fake.Transport.WasDisposed);
            }
        }

        [Fact]
        public void McpStdioClient_HandlesRpcError_Timeout_And_ServerCrashStderr()
        {
            // 1. JSON-RPC Error response
            using (var fake = new FakeInProcessMcpServer())
            {
                var cfg = new McpServerConfig { Name = "srv", Command = new List<string> { "fake" }, TimeoutMs = 3000 };
                using (var client = new McpStdioClient(cfg, fake.Transport))
                {
                    var errRes = client.CallTool("fail_rpc", new Dictionary<string, object>());
                    Assert.False(errRes.Success);
                    Assert.Contains("Invalid arguments for fail_rpc", errRes.Output);
                }
            }

            // 2. Timeout handling
            using (var fake = new FakeInProcessMcpServer { DelayToolCall = true, DelayMs = 600 })
            {
                var cfg = new McpServerConfig { Name = "srv", Command = new List<string> { "fake" }, TimeoutMs = 3000 };
                using (var client = new McpStdioClient(cfg, fake.Transport))
                {
                    client.ConnectAndInitialize(2000);
                    var timeoutRes = client.CallTool("echo", new Dictionary<string, object> { ["text"] = "slow" }, timeoutMs: 80);
                    Assert.False(timeoutRes.Success);
                    Assert.Contains("timed out", timeoutRes.Output, StringComparison.OrdinalIgnoreCase);
                }
            }

            // 3. Server crash + stderr capture
            using (var fake = new FakeInProcessMcpServer { CrashOnToolCall = true, CrashStderr = "panic: nil pointer in mcp worker" })
            {
                var cfg = new McpServerConfig { Name = "srv", Command = new List<string> { "fake" }, TimeoutMs = 2000 };
                using (var client = new McpStdioClient(cfg, fake.Transport))
                {
                    client.ConnectAndInitialize(2000);
                    var crashRes = client.CallTool("echo", new Dictionary<string, object> { ["text"] = "boom" }, timeoutMs: 2000);
                    Assert.False(crashRes.Success);
                    Assert.Contains("panic: nil pointer in mcp worker", crashRes.Output);
                }
            }
        }

        [Fact]
        public void McpServerManager_IsLazy_ExposesToolsInNativeAndTextModes_And_EnforcesPermissions()
        {
            string ws = CreateTempWorkspace();
            Directory.CreateDirectory(Path.Combine(ws, ".vscode"));
            File.WriteAllText(Path.Combine(ws, ".vscode", "settings.json"), @"{
                ""codeless.mcp"": {
                    ""myserver"": {
                        ""type"": ""local"",
                        ""command"": [""fake-mcp""],
                        ""enabled"": true
                    }
                }
            }");

            var fakeServer = new FakeInProcessMcpServer();
            _disposables.Add(fakeServer);

            var settings = new IdePersistentSettings
            {
                RequireAgentToolApproval = false
            };
            var engine = new LlmHarnessAndAgentEngine(settings);
            _disposables.Add(engine.McpManager);
            engine.McpManager.TransportFactory = (cfg, root) => fakeServer.Transport;

            // LAZY check: before any turn or tool query, zero servers have been started
            Assert.Equal(0, engine.McpManager.ActiveServerCount);
            Assert.Equal(0, engine.McpManager.TotalServersStartedCount);
            Assert.False(fakeServer.ReceivedInitialize);

            // First tool request starts the server lazily
            var availableTools = engine.GetAvailableToolsForCurrentDepth(ws).ToList();
            Assert.Equal(1, engine.McpManager.ActiveServerCount);
            Assert.Equal(1, engine.McpManager.TotalServersStartedCount);
            Assert.Contains(availableTools, t => t.Name == "myserver_echo");

            // Native tool calling schema includes <server>_<tool>
            var oaiTools = NativeToolCalling.BuildOpenAiTools(availableTools) as List<object>;
            Assert.NotNull(oaiTools);
            string oaiJson = new JavaScriptSerializer().Serialize(oaiTools);
            Assert.Contains("myserver_echo", oaiJson);

            // Text-mode system prompt includes <server>_<tool>
            string textPrompt = LlmHarnessAndAgentEngine.BuildSystemHarnessPrompt(
                "build", engine.Harnesses[0], engine.Vendors[0], ws, null, false, false, availableTools);
            Assert.Contains("\"myserver_echo\"", textPrompt);

            // Default permission for MCP tools is "ask" even when RequireAgentToolApproval is false
            bool approvalPrompted = false;
            engine.OnRequestToolApproval = (tool, argsJson) =>
            {
                approvalPrompted = true;
                return false; // User denies approval prompt
            };
            var deniedByAsk = engine.ExecuteWorkspaceTool(
                "myserver_echo",
                new Dictionary<string, object> { ["text"] = "test" },
                ws, new List<string>(), null, new Dictionary<string, string>(), "build", settings);
            Assert.True(approvalPrompted);
            Assert.False(deniedByAsk.Success);

            // When user approves "ask", tool executes
            engine.OnRequestToolApproval = (tool, argsJson) => true;
            var approvedRes = engine.ExecuteWorkspaceTool(
                "myserver_echo",
                new Dictionary<string, object> { ["text"] = "approved" },
                ws, new List<string>(), null, new Dictionary<string, string>(), "build", settings);
            Assert.True(approvedRes.Success);
            Assert.Equal("ECHO:approved", approvedRes.Output);

            // Pattern-based "deny" in AgentPermissionMcp blocks without prompting
            approvalPrompted = false;
            settings.AgentPermissionMcp["myserver_*"] = "deny";
            var patternDenied = engine.ExecuteWorkspaceTool(
                "myserver_echo",
                new Dictionary<string, object> { ["text"] = "blocked" },
                ws, new List<string>(), null, new Dictionary<string, string>(), "build", settings);
            Assert.False(approvalPrompted);
            Assert.False(patternDenied.Success);
            Assert.Contains("Permission Denied", patternDenied.Summary);

            // Specific pattern "myserver_echo" = "allow" overrides shorter wildcard "myserver_*" = "deny"
            settings.AgentPermissionMcp["myserver_echo"] = "allow";
            engine.OnRequestToolApproval = null;
            var patternAllowed = engine.ExecuteWorkspaceTool(
                "myserver_echo",
                new Dictionary<string, object> { ["text"] = "auto-allowed" },
                ws, new List<string>(), null, new Dictionary<string, string>(), "build", settings);
            Assert.True(patternAllowed.Success);
            Assert.Equal("ECHO:auto-allowed", patternAllowed.Output);

            // Changing workspace stops active servers
            string ws2 = CreateTempWorkspace();
            engine.McpManager.OnWorkspaceChanged(ws2);
            Assert.Equal(0, engine.McpManager.ActiveServerCount);
            Assert.True(fakeServer.Transport.WasDisposed);
        }

        [Fact]
        public void McpStdioClient_RealProcess_Spawns_Communicates_And_DisposeKillsProcess()
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows) || !File.Exists("/bin/sh"))
            {
                // On non-Unix without /bin/sh, verify command argument builder and return cleanly.
                Assert.Equal("\"a b\" --flag", StdioProcessMcpTransport.BuildCommandArguments(new[] { "cmd", "a b", "--flag" }));
                return;
            }

            string ws = CreateTempWorkspace();
            string scriptPath = Path.Combine(ws, "fake_mcp_server.sh");
            string script =
                "#!/bin/sh\n" +
                "echo \"mcp-server-stderr-ready\" >&2\n" +
                "while IFS= read -r line; do\n" +
                "  case \"$line\" in\n" +
                "    *\"\\\"method\\\":\\\"initialize\\\"\"*)\n" +
                "      printf '{\"jsonrpc\":\"2.0\",\"id\":1,\"result\":{\"protocolVersion\":\"2024-11-05\",\"capabilities\":{},\"serverInfo\":{\"name\":\"sh-mcp\",\"version\":\"1.0\"}}}\\n'\n" +
                "      ;;\n" +
                "    *\"\\\"method\\\":\\\"tools/list\\\"\"*)\n" +
                "      printf '{\"jsonrpc\":\"2.0\",\"id\":2,\"result\":{\"tools\":[{\"name\":\"ping\",\"description\":\"Ping tool\",\"inputSchema\":{\"type\":\"object\"}}]}}\\n'\n" +
                "      ;;\n" +
                "    *\"\\\"method\\\":\\\"tools/call\\\"\"*)\n" +
                "      printf '{\"jsonrpc\":\"2.0\",\"id\":3,\"result\":{\"content\":[{\"type\":\"text\",\"text\":\"pong-from-real-proc\"}],\"isError\":false}}\\n'\n" +
                "      ;;\n" +
                "  esac\n" +
                "done\n";
            File.WriteAllText(scriptPath, script);

            var cfg = new McpServerConfig
            {
                Name = "realsh",
                Type = "local",
                Command = new List<string> { "/bin/sh", scriptPath },
                Enabled = true,
                TimeoutMs = 5000
            };

            var transport = new StdioProcessMcpTransport(cfg, ws);
            _disposables.Add(transport);

            int pid;
            Process proc;
            using (var client = new McpStdioClient(cfg, transport))
            {
                client.ConnectAndInitialize(5000);
                Assert.True(transport.IsAlive);
                pid = transport.ProcessId;
                proc = transport.Process;
                Assert.True(pid > 0);

                var tools = client.ListTools(5000);
                Assert.Single(tools);
                Assert.Equal("realsh_ping", tools[0].ExposedName);

                var callRes = client.CallTool("ping", new Dictionary<string, object>(), 5000);
                Assert.True(callRes.Success);
                Assert.Equal("pong-from-real-proc", callRes.Output);
            }

            // Dispose must kill the real child process so zero orphan processes remain
            Assert.False(transport.IsAlive);
            if (proc != null)
            {
                try { Assert.True(proc.HasExited); } catch { }
            }
        }
    }
}
