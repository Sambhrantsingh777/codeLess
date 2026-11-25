using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using codeLess.Core;

namespace codeLess.Studio
{
    public sealed class McpServerConfig
    {
        public string Name { get; set; }
        public string Type { get; set; }
        public List<string> Command { get; set; }
        public Dictionary<string, string> Environment { get; set; }
        public bool Enabled { get; set; }
        public int TimeoutMs { get; set; }

        public McpServerConfig()
        {
            Name = "";
            Type = "local";
            Command = new List<string>();
            Environment = new Dictionary<string, string>(StringComparer.Ordinal);
            Enabled = true;
            TimeoutMs = 15000;
        }

        public McpServerConfig Clone()
        {
            McpServerConfig copy = new McpServerConfig();
            copy.Name = Name;
            copy.Type = Type;
            copy.Command = Command != null ? new List<string>(Command) : new List<string>();
            copy.Environment = Environment != null
                ? new Dictionary<string, string>(Environment, StringComparer.Ordinal)
                : new Dictionary<string, string>(StringComparer.Ordinal);
            copy.Enabled = Enabled;
            copy.TimeoutMs = TimeoutMs;
            return copy;
        }
    }

    public sealed class McpToolInfo
    {
        public string ServerName { get; set; }
        public string ToolName { get; set; }
        public string ExposedName { get; set; }
        public string Description { get; set; }
        public string InputSchemaJson { get; set; }

        public McpToolInfo()
        {
            ServerName = "";
            ToolName = "";
            ExposedName = "";
            Description = "";
            InputSchemaJson = "{\"type\":\"object\",\"properties\":{}}";
        }

        public AgentToolDefinition ToAgentToolDefinition()
        {
            AgentToolDefinition def = new AgentToolDefinition();
            def.Name = ExposedName;
            def.Aliases = new string[0];
            def.Description = string.IsNullOrEmpty(Description)
                ? ("MCP tool '" + ToolName + "' from server '" + ServerName + "'.")
                : Description;
            def.JsonSchema = string.IsNullOrEmpty(InputSchemaJson)
                ? "{\"type\":\"object\",\"properties\":{}}"
                : InputSchemaJson;
            def.IsReadOnly = false;
            return def;
        }
    }

    public sealed class McpException : Exception
    {
        public int ErrorCode { get; private set; }
        public string StderrTail { get; private set; }

        public McpException(string message, int errorCode = 0, string stderrTail = "")
            : base(message)
        {
            ErrorCode = errorCode;
            StderrTail = stderrTail ?? "";
        }
    }

    public interface IMcpTransport : IDisposable
    {
        Stream Input { get; }
        Stream Output { get; }
        bool IsAlive { get; }
        void Start();
        string GetStderrTail();
    }

    /// <summary>
    /// Thread-safe in-memory blocking FIFO byte stream used to wire an in-process fake MCP server
    /// over stdio stream pipes in unit tests without spawning an external binary. Strict C# 5.
    /// </summary>
    public sealed class BlockingPipeStream : Stream
    {
        private readonly object sync = new object();
        private readonly Queue<byte> buffer = new Queue<byte>();
        private bool closed;

        public override bool CanRead { get { return true; } }
        public override bool CanSeek { get { return false; } }
        public override bool CanWrite { get { return true; } }
        public override long Length { get { throw new NotSupportedException(); } }
        public override long Position
        {
            get { throw new NotSupportedException(); }
            set { throw new NotSupportedException(); }
        }

        public bool IsClosed
        {
            get { lock (sync) { return closed; } }
        }

        public override void Flush()
        {
        }

        public override int Read(byte[] dest, int offset, int count)
        {
            if (dest == null) throw new ArgumentNullException("dest");
            if (count <= 0) return 0;
            lock (sync)
            {
                while (buffer.Count == 0 && !closed)
                {
                    Monitor.Wait(sync);
                }
                if (buffer.Count == 0) return 0;
                int read = 0;
                while (read < count && buffer.Count > 0)
                {
                    dest[offset + read] = buffer.Dequeue();
                    read++;
                }
                return read;
            }
        }

        public override void Write(byte[] src, int offset, int count)
        {
            if (src == null) throw new ArgumentNullException("src");
            if (count <= 0) return;
            lock (sync)
            {
                if (closed) throw new ObjectDisposedException("BlockingPipeStream");
                for (int i = 0; i < count; i++)
                {
                    buffer.Enqueue(src[offset + i]);
                }
                Monitor.PulseAll(sync);
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                lock (sync)
                {
                    if (!closed)
                    {
                        closed = true;
                        Monitor.PulseAll(sync);
                    }
                }
            }
            base.Dispose(disposing);
        }

        public override long Seek(long offset, SeekOrigin origin) { throw new NotSupportedException(); }
        public override void SetLength(long value) { throw new NotSupportedException(); }
    }

    /// <summary>
    /// In-process stream-pipe transport for deterministic testing of the MCP JSON-RPC stdio client.
    /// </summary>
    public sealed class InMemoryPipeMcpTransport : IMcpTransport
    {
        private readonly BlockingPipeStream clientToServer = new BlockingPipeStream();
        private readonly BlockingPipeStream serverToClient = new BlockingPipeStream();
        private readonly StringBuilder stderrSb = new StringBuilder();
        private readonly object stderrLock = new object();
        private volatile bool started;
        private volatile bool disposed;

        public Action OnStart;
        public Action OnDispose;

        public Stream Input { get { return serverToClient; } }
        public Stream Output { get { return clientToServer; } }
        public Stream ServerInput { get { return clientToServer; } }
        public Stream ServerOutput { get { return serverToClient; } }

        public bool IsAlive
        {
            get { return started && !disposed && !serverToClient.IsClosed && !clientToServer.IsClosed; }
        }

        public bool WasDisposed
        {
            get { return disposed; }
        }

        public void Start()
        {
            if (disposed) throw new ObjectDisposedException("InMemoryPipeMcpTransport");
            started = true;
            Action cb = OnStart;
            if (cb != null) cb();
        }

        public void AppendStderr(string line)
        {
            if (string.IsNullOrEmpty(line)) return;
            lock (stderrLock)
            {
                stderrSb.AppendLine(line);
            }
        }

        public void SimulateServerCrash(string stderrMessage = null)
        {
            if (!string.IsNullOrEmpty(stderrMessage))
                AppendStderr(stderrMessage);
            serverToClient.Dispose();
            clientToServer.Dispose();
        }

        public string GetStderrTail()
        {
            lock (stderrLock)
            {
                return stderrSb.ToString().Trim();
            }
        }

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            try { clientToServer.Dispose(); } catch { }
            try { serverToClient.Dispose(); } catch { }
            Action cb = OnDispose;
            if (cb != null)
            {
                try { cb(); } catch { }
            }
        }
    }

    /// <summary>
    /// Real child-process MCP stdio transport. Captures stderr into a bounded buffer and guarantees
    /// the child process is killed on Dispose so zero orphan processes remain.
    /// </summary>
    public sealed class StdioProcessMcpTransport : IMcpTransport
    {
        private readonly McpServerConfig config;
        private readonly string workingDirectory;
        private readonly List<string> stderrLines = new List<string>();
        private readonly object stderrLock = new object();
        private Process process;
        private volatile bool disposed;

        public StdioProcessMcpTransport(McpServerConfig config, string workingDirectory)
        {
            if (config == null) throw new ArgumentNullException("config");
            this.config = config;
            this.workingDirectory = workingDirectory ?? "";
        }

        public Stream Input
        {
            get { return process != null ? process.StandardOutput.BaseStream : null; }
        }

        public Stream Output
        {
            get { return process != null ? process.StandardInput.BaseStream : null; }
        }

        public int ProcessId
        {
            get
            {
                try { return process != null ? process.Id : -1; }
                catch { return -1; }
            }
        }

        public Process Process
        {
            get { return process; }
        }

        public bool IsAlive
        {
            get
            {
                if (disposed || process == null) return false;
                try { return !process.HasExited; }
                catch { return false; }
            }
        }

        public void Start()
        {
            if (disposed) throw new ObjectDisposedException("StdioProcessMcpTransport");
            if (config.Command == null || config.Command.Count == 0 || string.IsNullOrWhiteSpace(config.Command[0]))
                throw new InvalidOperationException("MCP server '" + config.Name + "' has an empty command.");

            ProcessStartInfo psi = new ProcessStartInfo();
            psi.FileName = config.Command[0];
            psi.Arguments = BuildCommandArguments(config.Command);
            psi.UseShellExecute = false;
            psi.RedirectStandardInput = true;
            psi.RedirectStandardOutput = true;
            psi.RedirectStandardError = true;
            psi.CreateNoWindow = true;
            if (!string.IsNullOrEmpty(workingDirectory) && Directory.Exists(workingDirectory))
                psi.WorkingDirectory = workingDirectory;

            if (config.Environment != null)
            {
                foreach (KeyValuePair<string, string> kv in config.Environment)
                {
                    if (!string.IsNullOrEmpty(kv.Key))
                        psi.EnvironmentVariables[kv.Key] = kv.Value ?? "";
                }
            }

            process = Process.Start(psi);
            if (process == null)
                throw new InvalidOperationException("Failed to start MCP server process '" + config.Name + "'.");

            Thread errThread = new Thread(ReadStderrLoop);
            errThread.IsBackground = true;
            errThread.Name = "codeLess MCP stderr: " + config.Name;
            errThread.Start();
        }

        private void ReadStderrLoop()
        {
            try
            {
                StreamReader errReader = process != null ? process.StandardError : null;
                if (errReader == null) return;
                string line;
                while (!disposed && (line = errReader.ReadLine()) != null)
                {
                    lock (stderrLock)
                    {
                        stderrLines.Add(line);
                        if (stderrLines.Count > 64)
                            stderrLines.RemoveAt(0);
                    }
                }
            }
            catch { }
        }

        public string GetStderrTail()
        {
            lock (stderrLock)
            {
                return string.Join("\n", stderrLines.ToArray()).Trim();
            }
        }

        public static string BuildCommandArguments(IList<string> command)
        {
            if (command == null || command.Count <= 1) return "";
            StringBuilder sb = new StringBuilder();
            for (int i = 1; i < command.Count; i++)
            {
                if (i > 1) sb.Append(' ');
                string arg = command[i] ?? "";
                if (arg.Length == 0 || arg.IndexOf(' ') >= 0 || arg.IndexOf('\t') >= 0 || arg.IndexOf('"') >= 0)
                {
                    sb.Append('"');
                    sb.Append(arg.Replace("\\", "\\\\").Replace("\"", "\\\""));
                    sb.Append('"');
                }
                else
                {
                    sb.Append(arg);
                }
            }
            return sb.ToString();
        }

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            Process p = process;
            process = null;
            if (p != null)
            {
                try { p.StandardInput.Close(); } catch { }
                try
                {
                    if (!p.HasExited)
                    {
                        try { p.Kill(); } catch { }
                        try { p.WaitForExit(1500); } catch { }
                    }
                }
                catch { }
                try { p.Dispose(); } catch { }
            }
        }
    }

    /// <summary>
    /// Hand-rolled JSON-RPC 2.0 client over newline-delimited stdio per the MCP specification.
    /// Uses JavaScriptSerializer and handles initialize, notifications/initialized, tools/list,
    /// tools/call, timeouts, server crashes, and stderr capture. Strict C# 5.
    /// </summary>
    public sealed class McpStdioClient : IDisposable
    {
        private readonly McpServerConfig config;
        private readonly IMcpTransport transport;
        private readonly JavaScriptSerializer serializer = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };
        private readonly Dictionary<long, TaskCompletionSource<Dictionary<string, object>>> pending =
            new Dictionary<long, TaskCompletionSource<Dictionary<string, object>>>();
        private readonly object sync = new object();
        private StreamWriter writer;
        private Thread readerThread;
        private long nextId;
        private volatile bool initialized;
        private volatile bool disposed;
        private volatile bool closed;
        private string closeReason = "";

        public McpStdioClient(McpServerConfig config, IMcpTransport transport)
        {
            if (config == null) throw new ArgumentNullException("config");
            if (transport == null) throw new ArgumentNullException("transport");
            this.config = config;
            this.transport = transport;
        }

        public string ServerName { get { return config.Name; } }
        public IMcpTransport Transport { get { return transport; } }
        public bool IsInitialized { get { return initialized && !closed && !disposed && transport.IsAlive; } }

        public void ConnectAndInitialize(int timeoutMs = 0)
        {
            if (disposed) throw new ObjectDisposedException("McpStdioClient");
            if (initialized) return;
            int effTimeout = timeoutMs > 0 ? timeoutMs : (config.TimeoutMs > 0 ? config.TimeoutMs : 15000);

            transport.Start();
            UTF8Encoding utf8NoBom = new UTF8Encoding(false);
            writer = new StreamWriter(transport.Output, utf8NoBom, 4096, true);
            writer.NewLine = "\n";
            writer.AutoFlush = true;

            readerThread = new Thread(ReadLoop);
            readerThread.IsBackground = true;
            readerThread.Name = "codeLess MCP reader: " + config.Name;
            readerThread.Start();

            Dictionary<string, object> initParams = new Dictionary<string, object>();
            initParams["protocolVersion"] = "2024-11-05";
            initParams["capabilities"] = new Dictionary<string, object>();
            Dictionary<string, object> clientInfo = new Dictionary<string, object>();
            clientInfo["name"] = "codeLess";
            clientInfo["version"] = "1.0.0";
            initParams["clientInfo"] = clientInfo;

            SendRequest("initialize", initParams, effTimeout);
            SendNotification("notifications/initialized", new Dictionary<string, object>());
            initialized = true;
        }

        public List<McpToolInfo> ListTools(int timeoutMs = 0)
        {
            if (!initialized) ConnectAndInitialize(timeoutMs);
            int effTimeout = timeoutMs > 0 ? timeoutMs : (config.TimeoutMs > 0 ? config.TimeoutMs : 15000);
            Dictionary<string, object> result = SendRequest("tools/list", new Dictionary<string, object>(), effTimeout);
            List<McpToolInfo> tools = new List<McpToolInfo>();
            if (result == null || !result.ContainsKey("tools") || result["tools"] == null)
                return tools;

            IEnumerable rawTools = result["tools"] as IEnumerable;
            if (rawTools == null) return tools;

            foreach (object item in rawTools)
            {
                Dictionary<string, object> td = item as Dictionary<string, object>;
                if (td == null) continue;
                string name = td.ContainsKey("name") && td["name"] != null ? Convert.ToString(td["name"]).Trim() : "";
                if (string.IsNullOrEmpty(name)) continue;
                string desc = td.ContainsKey("description") && td["description"] != null ? Convert.ToString(td["description"]) : "";
                string schemaJson = "{\"type\":\"object\",\"properties\":{}}";
                if (td.ContainsKey("inputSchema") && td["inputSchema"] != null)
                {
                    try { schemaJson = serializer.Serialize(td["inputSchema"]); }
                    catch { }
                }

                McpToolInfo info = new McpToolInfo();
                info.ServerName = config.Name;
                info.ToolName = name;
                info.ExposedName = FormatExposedToolName(config.Name, name);
                info.Description = desc;
                info.InputSchemaJson = schemaJson;
                tools.Add(info);
            }
            return tools;
        }

        public AgentToolCallResult CallTool(string rawToolName, Dictionary<string, object> arguments, int timeoutMs = 0)
        {
            string exposedName = FormatExposedToolName(config.Name, rawToolName);
            try
            {
                if (!initialized) ConnectAndInitialize(timeoutMs);
                int effTimeout = timeoutMs > 0 ? timeoutMs : (config.TimeoutMs > 0 ? config.TimeoutMs : 15000);

                Dictionary<string, object> callParams = new Dictionary<string, object>();
                callParams["name"] = rawToolName;
                callParams["arguments"] = arguments ?? new Dictionary<string, object>();

                Dictionary<string, object> result = SendRequest("tools/call", callParams, effTimeout);
                bool isError = false;
                if (result != null && result.ContainsKey("isError") && result["isError"] != null)
                {
                    try { isError = Convert.ToBoolean(result["isError"]); } catch { }
                }

                StringBuilder textSb = new StringBuilder();
                if (result != null && result.ContainsKey("content") && result["content"] is IEnumerable && !(result["content"] is string))
                {
                    foreach (object blockObj in (IEnumerable)result["content"])
                    {
                        Dictionary<string, object> block = blockObj as Dictionary<string, object>;
                        if (block == null) continue;
                        if (block.ContainsKey("text") && block["text"] != null)
                        {
                            if (textSb.Length > 0) textSb.AppendLine();
                            textSb.Append(Convert.ToString(block["text"]));
                        }
                        else
                        {
                            if (textSb.Length > 0) textSb.AppendLine();
                            textSb.Append(serializer.Serialize(block));
                        }
                    }
                }
                else if (result != null)
                {
                    textSb.Append(serializer.Serialize(result));
                }

                string outputText = textSb.ToString();
                return new AgentToolCallResult
                {
                    ToolName = exposedName,
                    Success = !isError,
                    Summary = isError ? ("MCP error from " + exposedName) : ("Called MCP tool " + exposedName),
                    Output = outputText
                };
            }
            catch (TimeoutException tex)
            {
                return new AgentToolCallResult
                {
                    ToolName = exposedName,
                    Success = false,
                    Summary = "MCP tool timed out: " + exposedName,
                    Output = "MCP tool '" + exposedName + "' timed out: " + tex.Message
                };
            }
            catch (McpException mex)
            {
                string errOut = "MCP error (" + exposedName + "): " + mex.Message;
                if (!string.IsNullOrEmpty(mex.StderrTail))
                    errOut += "\nStderr: " + mex.StderrTail;
                return new AgentToolCallResult
                {
                    ToolName = exposedName,
                    Success = false,
                    Summary = "MCP error: " + mex.Message,
                    Output = errOut
                };
            }
            catch (Exception ex)
            {
                string stderr = transport != null ? transport.GetStderrTail() : "";
                string errOut = "MCP error (" + exposedName + "): " + ex.Message;
                if (!string.IsNullOrEmpty(stderr))
                    errOut += "\nStderr: " + stderr;
                return new AgentToolCallResult
                {
                    ToolName = exposedName,
                    Success = false,
                    Summary = "MCP call failed: " + exposedName,
                    Output = errOut
                };
            }
        }

        public static string FormatExposedToolName(string serverName, string toolName)
        {
            return (serverName ?? "").Trim() + "_" + (toolName ?? "").Trim();
        }

        public void SendNotification(string method, Dictionary<string, object> parameters)
        {
            Dictionary<string, object> msg = new Dictionary<string, object>();
            msg["jsonrpc"] = "2.0";
            msg["method"] = method;
            if (parameters != null) msg["params"] = parameters;
            WriteLineJson(msg);
        }

        public Dictionary<string, object> SendRequest(string method, Dictionary<string, object> parameters, int timeoutMs)
        {
            long id = Interlocked.Increment(ref nextId);
            TaskCompletionSource<Dictionary<string, object>> tcs =
                new TaskCompletionSource<Dictionary<string, object>>();

            lock (sync)
            {
                if (closed || disposed)
                {
                    string stderr = transport.GetStderrTail();
                    throw new McpException("MCP server '" + config.Name + "' is closed" +
                        (string.IsNullOrEmpty(closeReason) ? "." : (": " + closeReason)), 0, stderr);
                }
                pending[id] = tcs;
            }

            Dictionary<string, object> req = new Dictionary<string, object>();
            req["jsonrpc"] = "2.0";
            req["id"] = id;
            req["method"] = method;
            if (parameters != null) req["params"] = parameters;

            try
            {
                WriteLineJson(req);
            }
            catch (Exception ex)
            {
                lock (sync) { pending.Remove(id); }
                string stderr = transport.GetStderrTail();
                throw new McpException("Failed to write to MCP server '" + config.Name + "': " + ex.Message, 0, stderr);
            }

            int waitMs = timeoutMs > 0 ? timeoutMs : 15000;
            bool completed;
            try
            {
                completed = tcs.Task.Wait(waitMs);
            }
            catch (AggregateException aex)
            {
                Exception inner = aex.InnerException ?? aex;
                if (inner is McpException) throw inner;
                throw new McpException(inner.Message, 0, transport.GetStderrTail());
            }

            if (!completed)
            {
                lock (sync) { pending.Remove(id); }
                throw new TimeoutException("MCP request '" + method + "' to server '" + config.Name + "' timed out after " + waitMs + " ms.");
            }

            return tcs.Task.GetAwaiter().GetResult();
        }

        private void WriteLineJson(Dictionary<string, object> message)
        {
            string json = serializer.Serialize(message);
            lock (sync)
            {
                if (writer == null || closed || disposed)
                    throw new IOException("MCP transport stream is closed.");
                writer.WriteLine(json);
                writer.Flush();
            }
        }

        private void ReadLoop()
        {
            string reason = "MCP server process exited unexpectedly";
            try
            {
                StreamReader reader = new StreamReader(transport.Input, Encoding.UTF8, false, 4096, true);
                string line;
                while (!disposed && (line = reader.ReadLine()) != null)
                {
                    string trimmed = line.Trim();
                    if (trimmed.Length == 0) continue;

                    Dictionary<string, object> msg = null;
                    try
                    {
                        msg = serializer.DeserializeObject(trimmed) as Dictionary<string, object>;
                    }
                    catch
                    {
                        continue;
                    }
                    if (msg == null) continue;

                    if (msg.ContainsKey("id") && msg["id"] != null && !msg.ContainsKey("method"))
                    {
                        long id;
                        if (long.TryParse(Convert.ToString(msg["id"], CultureInfo.InvariantCulture), NumberStyles.Integer, CultureInfo.InvariantCulture, out id))
                        {
                            TaskCompletionSource<Dictionary<string, object>> tcs = null;
                            lock (sync)
                            {
                                if (pending.TryGetValue(id, out tcs))
                                    pending.Remove(id);
                            }
                            if (tcs != null)
                            {
                                if (msg.ContainsKey("error") && msg["error"] != null)
                                {
                                    Dictionary<string, object> errObj = msg["error"] as Dictionary<string, object>;
                                    int code = -32000;
                                    string errMsg = "Unknown MCP error";
                                    if (errObj != null)
                                    {
                                        if (errObj.ContainsKey("code") && errObj["code"] != null)
                                        {
                                            try { code = Convert.ToInt32(errObj["code"]); } catch { }
                                        }
                                        if (errObj.ContainsKey("message") && errObj["message"] != null)
                                            errMsg = Convert.ToString(errObj["message"]);
                                    }
                                    else
                                    {
                                        errMsg = Convert.ToString(msg["error"]);
                                    }
                                    tcs.TrySetException(new McpException(errMsg, code, transport.GetStderrTail()));
                                }
                                else
                                {
                                    Dictionary<string, object> resObj = msg.ContainsKey("result")
                                        ? (msg["result"] as Dictionary<string, object> ?? new Dictionary<string, object>())
                                        : new Dictionary<string, object>();
                                    tcs.TrySetResult(resObj);
                                }
                            }
                        }
                    }
                    else if (msg.ContainsKey("id") && msg["id"] != null && msg.ContainsKey("method"))
                    {
                        // Server-to-client request (e.g. ping)
                        string m = Convert.ToString(msg["method"]);
                        Dictionary<string, object> reply = new Dictionary<string, object>();
                        reply["jsonrpc"] = "2.0";
                        reply["id"] = msg["id"];
                        if (m == "ping")
                        {
                            reply["result"] = new Dictionary<string, object>();
                        }
                        else
                        {
                            Dictionary<string, object> err = new Dictionary<string, object>();
                            err["code"] = -32601;
                            err["message"] = "Method not supported: " + m;
                            reply["error"] = err;
                        }
                        try { WriteLineJson(reply); } catch { }
                    }
                }
            }
            catch (Exception ex)
            {
                reason = ex.Message;
            }

            MarkClosed(reason);
        }

        private void MarkClosed(string reason)
        {
            List<TaskCompletionSource<Dictionary<string, object>>> toFail =
                new List<TaskCompletionSource<Dictionary<string, object>>>();
            lock (sync)
            {
                if (closed) return;
                closed = true;
                closeReason = reason ?? "MCP connection closed";
                foreach (KeyValuePair<long, TaskCompletionSource<Dictionary<string, object>>> kv in pending)
                {
                    toFail.Add(kv.Value);
                }
                pending.Clear();
            }

            string stderr = transport != null ? transport.GetStderrTail() : "";
            string fullMsg = "MCP server '" + config.Name + "' crashed or closed connection: " + closeReason;
            if (!string.IsNullOrEmpty(stderr))
                fullMsg += " (stderr: " + stderr + ")";

            foreach (TaskCompletionSource<Dictionary<string, object>> tcs in toFail)
            {
                tcs.TrySetException(new McpException(fullMsg, -32000, stderr));
            }
        }

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            MarkClosed("Disposed");
            lock (sync)
            {
                if (writer != null)
                {
                    try { writer.Dispose(); } catch { }
                    writer = null;
                }
            }
            try { transport.Dispose(); } catch { }
        }
    }

    /// <summary>
    /// Parses `codeless.mcp` (and `mcp`) from user settings.json and workspace .vscode/settings.json
    /// with workspace precedence. Strict C# 5.
    /// </summary>
    public static class McpConfigLoader
    {
        public static Dictionary<string, McpServerConfig> LoadMergedConfig(
            string workspaceRoot,
            IdePersistentSettings settings = null,
            string userSettingsJsonOverride = null,
            string workspaceSettingsJsonOverride = null)
        {
            Dictionary<string, McpServerConfig> merged =
                new Dictionary<string, McpServerConfig>(StringComparer.OrdinalIgnoreCase);

            // 1. User settings.json (IdePersistentSettings.GetSettingsFilePath and User/settings.json)
            if (userSettingsJsonOverride != null)
            {
                MergeFromJson(userSettingsJsonOverride, merged);
            }
            else
            {
                try
                {
                    string primaryUserPath = IdePersistentSettings.GetSettingsFilePath();
                    if (File.Exists(primaryUserPath))
                        MergeFromJson(File.ReadAllText(primaryUserPath), merged);
                }
                catch { }

                try
                {
                    string vscodeUserPath = VscodeSettingsManager.GetUserSettingsPath();
                    if (File.Exists(vscodeUserPath))
                        MergeFromJson(File.ReadAllText(vscodeUserPath), merged);
                }
                catch { }
            }

            // 2. In-memory IdePersistentSettings.McpServers (if populated programmatically or via ApplySettings)
            if (settings != null && settings.McpServers != null)
            {
                foreach (KeyValuePair<string, McpServerConfig> kv in settings.McpServers)
                {
                    if (!string.IsNullOrEmpty(kv.Key) && kv.Value != null)
                    {
                        McpServerConfig clone = kv.Value.Clone();
                        clone.Name = kv.Key;
                        merged[kv.Key] = clone;
                    }
                }
            }

            // 3. Workspace .vscode/settings.json (workspace wins over user settings)
            if (workspaceSettingsJsonOverride != null)
            {
                MergeFromJson(workspaceSettingsJsonOverride, merged);
            }
            else if (!string.IsNullOrEmpty(workspaceRoot))
            {
                try
                {
                    string wsSettingsPath = VscodeSettingsManager.GetWorkspaceSettingsPath(workspaceRoot);
                    if (!string.IsNullOrEmpty(wsSettingsPath) && File.Exists(wsSettingsPath))
                        MergeFromJson(File.ReadAllText(wsSettingsPath), merged);
                }
                catch { }
            }

            return merged;
        }

        public static void MergeFromJson(string jsonContent, Dictionary<string, McpServerConfig> target)
        {
            if (string.IsNullOrWhiteSpace(jsonContent) || target == null) return;
            string err;
            Dictionary<string, object> root = JsoncParser.Parse(jsonContent, out err) as Dictionary<string, object>;
            if (root == null) return;

            MergeFromRootDictionary(root, target);
        }

        public static void MergeFromRootDictionary(Dictionary<string, object> root, Dictionary<string, McpServerConfig> target)
        {
            if (root == null || target == null) return;
            object mcpObj = null;
            if (root.ContainsKey("codeless.mcp")) mcpObj = root["codeless.mcp"];
            else if (root.ContainsKey("mcp")) mcpObj = root["mcp"];

            Dictionary<string, object> serversDict = mcpObj as Dictionary<string, object>;
            if (serversDict == null) return;

            foreach (KeyValuePair<string, object> kv in serversDict)
            {
                string serverName = (kv.Key ?? "").Trim();
                if (string.IsNullOrEmpty(serverName)) continue;
                Dictionary<string, object> entry = kv.Value as Dictionary<string, object>;
                if (entry == null) continue;

                McpServerConfig cfg;
                if (!target.TryGetValue(serverName, out cfg) || cfg == null)
                {
                    cfg = new McpServerConfig();
                    cfg.Name = serverName;
                }
                else
                {
                    cfg = cfg.Clone();
                    cfg.Name = serverName;
                }

                if (entry.ContainsKey("type") && entry["type"] != null)
                    cfg.Type = Convert.ToString(entry["type"]).Trim();

                if (entry.ContainsKey("enabled") && entry["enabled"] != null)
                {
                    try { cfg.Enabled = Convert.ToBoolean(entry["enabled"]); } catch { }
                }

                if (entry.ContainsKey("timeout") && entry["timeout"] != null)
                {
                    try { cfg.TimeoutMs = Convert.ToInt32(entry["timeout"]); } catch { }
                }

                if (entry.ContainsKey("command") && entry["command"] != null)
                {
                    List<string> cmdList = new List<string>();
                    if (entry["command"] is string)
                    {
                        string single = Convert.ToString(entry["command"]).Trim();
                        if (!string.IsNullOrEmpty(single)) cmdList.Add(single);
                    }
                    else if (entry["command"] is IEnumerable)
                    {
                        foreach (object c in (IEnumerable)entry["command"])
                        {
                            if (c != null) cmdList.Add(Convert.ToString(c));
                        }
                    }
                    cfg.Command = cmdList;
                }

                string envKey = entry.ContainsKey("environment") ? "environment" : (entry.ContainsKey("env") ? "env" : null);
                if (envKey != null && entry[envKey] is Dictionary<string, object>)
                {
                    Dictionary<string, object> envDict = (Dictionary<string, object>)entry[envKey];
                    foreach (KeyValuePair<string, object> ekv in envDict)
                    {
                        if (!string.IsNullOrEmpty(ekv.Key))
                            cfg.Environment[ekv.Key] = ekv.Value != null ? Convert.ToString(ekv.Value) : "";
                    }
                }

                target[serverName] = cfg;
            }
        }
    }

    /// <summary>
    /// Lazy lifecycle manager for MCP stdio servers. Starts a server ONLY when an agent turn
    /// first requests the tool list or invokes an MCP tool, and kills all server processes
    /// when the window closes or the workspace changes. Zero orphan processes. Strict C# 5.
    /// </summary>
    public sealed class McpServerManager : IDisposable
    {
        private readonly object sync = new object();
        private readonly Dictionary<string, McpStdioClient> activeClients =
            new Dictionary<string, McpStdioClient>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, McpToolInfo> discoveredToolsByExposedName =
            new Dictionary<string, McpToolInfo>(StringComparer.OrdinalIgnoreCase);
        private string activeWorkspaceRoot;
        private bool toolsLoadedForActiveWorkspace;
        private int startedServerCount;

        public Func<McpServerConfig, string, IMcpTransport> TransportFactory { get; set; }

        public McpServerManager()
        {
            TransportFactory = DefaultTransportFactory;
        }

        private static IMcpTransport DefaultTransportFactory(McpServerConfig cfg, string workspaceRoot)
        {
            return new StdioProcessMcpTransport(cfg, workspaceRoot);
        }

        private static string NormalizeWorkspacePath(string workspaceRoot)
        {
            if (string.IsNullOrWhiteSpace(workspaceRoot)) return "";
            try { return Path.GetFullPath(workspaceRoot).TrimEnd('\\', '/'); }
            catch { return workspaceRoot.TrimEnd('\\', '/'); }
        }

        public int ActiveServerCount
        {
            get { lock (sync) { return activeClients.Count; } }
        }

        public int TotalServersStartedCount
        {
            get { lock (sync) { return startedServerCount; } }
        }

        public void OnWorkspaceChanged(string newWorkspaceRoot)
        {
            string normNew = NormalizeWorkspacePath(newWorkspaceRoot);
            lock (sync)
            {
                if (!string.Equals(activeWorkspaceRoot ?? "", normNew, StringComparison.OrdinalIgnoreCase))
                {
                    StopAllLocked();
                    activeWorkspaceRoot = normNew;
                }
            }
        }

        /// <summary>
        /// Lazily starts enabled local MCP servers for the given workspace and returns their tool definitions.
        /// </summary>
        public List<AgentToolDefinition> GetAgentToolDefinitions(string workspaceRoot, IdePersistentSettings settings)
        {
            List<McpToolInfo> infos = EnsureToolsLoaded(workspaceRoot, settings);
            List<AgentToolDefinition> defs = new List<AgentToolDefinition>();
            for (int i = 0; i < infos.Count; i++)
            {
                defs.Add(infos[i].ToAgentToolDefinition());
            }
            return defs;
        }

        public List<McpToolInfo> EnsureToolsLoaded(string workspaceRoot, IdePersistentSettings settings)
        {
            string normWs = NormalizeWorkspacePath(workspaceRoot);
            lock (sync)
            {
                if (activeWorkspaceRoot != null && !string.Equals(activeWorkspaceRoot, normWs, StringComparison.OrdinalIgnoreCase))
                {
                    StopAllLocked();
                }
                activeWorkspaceRoot = normWs;

                if (toolsLoadedForActiveWorkspace)
                {
                    return new List<McpToolInfo>(discoveredToolsByExposedName.Values);
                }

                Dictionary<string, McpServerConfig> configs = McpConfigLoader.LoadMergedConfig(workspaceRoot, settings);
                discoveredToolsByExposedName.Clear();

                foreach (KeyValuePair<string, McpServerConfig> kv in configs)
                {
                    McpServerConfig cfg = kv.Value;
                    if (cfg == null || !cfg.Enabled) continue;
                    if (!string.IsNullOrEmpty(cfg.Type) && !string.Equals(cfg.Type, "local", StringComparison.OrdinalIgnoreCase) && !string.Equals(cfg.Type, "stdio", StringComparison.OrdinalIgnoreCase))
                        continue;
                    if (cfg.Command == null || cfg.Command.Count == 0)
                        continue;

                    try
                    {
                        McpStdioClient client;
                        if (!activeClients.TryGetValue(cfg.Name, out client) || client == null || !client.IsInitialized)
                        {
                            if (client != null)
                            {
                                try { client.Dispose(); } catch { }
                                activeClients.Remove(cfg.Name);
                            }
                            Func<McpServerConfig, string, IMcpTransport> factory = TransportFactory ?? DefaultTransportFactory;
                            IMcpTransport transport = factory(cfg, workspaceRoot);
                            client = new McpStdioClient(cfg, transport);
                            client.ConnectAndInitialize(cfg.TimeoutMs);
                            activeClients[cfg.Name] = client;
                            startedServerCount++;
                        }

                        List<McpToolInfo> serverTools = client.ListTools(cfg.TimeoutMs);
                        for (int i = 0; i < serverTools.Count; i++)
                        {
                            McpToolInfo ti = serverTools[i];
                            discoveredToolsByExposedName[ti.ExposedName] = ti;
                        }
                    }
                    catch
                    {
                        // Server failed handshake or tool listing: dispose broken client so it doesn't leak
                        McpStdioClient broken;
                        if (activeClients.TryGetValue(cfg.Name, out broken))
                        {
                            try { broken.Dispose(); } catch { }
                            activeClients.Remove(cfg.Name);
                        }
                    }
                }

                toolsLoadedForActiveWorkspace = true;
                return new List<McpToolInfo>(discoveredToolsByExposedName.Values);
            }
        }

        public bool TryResolveMcpTool(string exposedToolName, string workspaceRoot, IdePersistentSettings settings, out McpToolInfo toolInfo)
        {
            toolInfo = null;
            if (string.IsNullOrEmpty(exposedToolName) || exposedToolName.IndexOf('_') <= 0)
                return false;

            // Check if already loaded
            lock (sync)
            {
                if (toolsLoadedForActiveWorkspace && discoveredToolsByExposedName.TryGetValue(exposedToolName, out toolInfo))
                    return true;
            }

            // Only trigger lazy startup if exposedToolName starts with a configured server prefix "<server>_"
            Dictionary<string, McpServerConfig> configs = McpConfigLoader.LoadMergedConfig(workspaceRoot, settings);
            bool matchesServerPrefix = false;
            foreach (KeyValuePair<string, McpServerConfig> kv in configs)
            {
                if (kv.Value != null && kv.Value.Enabled &&
                    exposedToolName.StartsWith(kv.Key + "_", StringComparison.OrdinalIgnoreCase))
                {
                    matchesServerPrefix = true;
                    break;
                }
            }
            if (!matchesServerPrefix) return false;

            EnsureToolsLoaded(workspaceRoot, settings);
            lock (sync)
            {
                return discoveredToolsByExposedName.TryGetValue(exposedToolName, out toolInfo);
            }
        }

        public AgentToolCallResult ExecuteMcpTool(McpToolInfo toolInfo, Dictionary<string, object> args, int timeoutMs = 0)
        {
            if (toolInfo == null)
            {
                return new AgentToolCallResult
                {
                    ToolName = "invalid",
                    Success = false,
                    Summary = "Unknown MCP tool",
                    Output = "Unknown MCP tool."
                };
            }

            McpStdioClient client = null;
            lock (sync)
            {
                activeClients.TryGetValue(toolInfo.ServerName, out client);
            }

            if (client == null)
            {
                return new AgentToolCallResult
                {
                    ToolName = toolInfo.ExposedName,
                    Success = false,
                    Summary = "MCP server not running: " + toolInfo.ServerName,
                    Output = "MCP server '" + toolInfo.ServerName + "' is not running."
                };
            }

            return client.CallTool(toolInfo.ToolName, args, timeoutMs);
        }

        public void InvalidateCache()
        {
            lock (sync)
            {
                toolsLoadedForActiveWorkspace = false;
            }
        }

        public void StopAll()
        {
            lock (sync)
            {
                StopAllLocked();
            }
        }

        private void StopAllLocked()
        {
            foreach (KeyValuePair<string, McpStdioClient> kv in activeClients)
            {
                if (kv.Value != null)
                {
                    try { kv.Value.Dispose(); } catch { }
                }
            }
            activeClients.Clear();
            discoveredToolsByExposedName.Clear();
            toolsLoadedForActiveWorkspace = false;
        }

        public void Dispose()
        {
            StopAll();
        }
    }
}
