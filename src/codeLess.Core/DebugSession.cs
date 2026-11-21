using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace codeLess.Studio
{
    /// <summary>How to start / reach a debug adapter.</summary>
    public sealed class DebugAdapterDescriptor
    {
        /// <summary>Normalized adapter type ("python", "go", "lldb", "coreclr", "node", or a user type).</summary>
        public string Type;
        public string DisplayName;
        /// <summary>Executable to start (null when connecting to an already running server on Host:Port).</summary>
        public string Command;
        public List<string> Args = new List<string>();
        public string Cwd;
        public Dictionary<string, string> Env;
        /// <summary>True: the adapter is a TCP server. "${port}" in Args is replaced by a free loopback port.</summary>
        public bool UseTcp;
        public string Host = "127.0.0.1";
        public int Port;
        /// <summary>DAP adapterID sent in initialize.</summary>
        public string AdapterId;

        public string CommandLineForDisplay
        {
            get
            {
                StringBuilder sb = new StringBuilder(Command ?? (Host + ":" + Port));
                foreach (string a in Args) sb.Append(' ').Append(a.IndexOf(' ') >= 0 ? "\"" + a + "\"" : a);
                return sb.ToString();
            }
        }
    }

    /// <summary>Streams to a running adapter: a child process (stdio or TCP) or a plain TCP connection.</summary>
    public sealed class DebugAdapterConnection : IDisposable
    {
        public Stream Input;   // adapter -> client
        public Stream Output;  // client -> adapter
        public Process Process;
        public TcpClient Tcp;
        public event Action<string> StderrLine;

        internal void RaiseStderr(string line)
        {
            Action<string> h = StderrLine;
            if (h != null) { try { h(line); } catch { } }
        }

        public static int FindFreeLoopbackPort()
        {
            TcpListener l = new TcpListener(IPAddress.Loopback, 0);
            l.Start();
            int port = ((IPEndPoint)l.LocalEndpoint).Port;
            l.Stop();
            return port;
        }

        /// <summary>Starts / connects to the adapter. Blocking (call from a background thread).</summary>
        public static DebugAdapterConnection Open(DebugAdapterDescriptor d, out string error)
        {
            error = null;
            DebugAdapterConnection c = new DebugAdapterConnection();
            int port = d.Port;
            List<string> args = new List<string>();
            if (d.UseTcp && port <= 0 && !string.IsNullOrEmpty(d.Command)) port = FindFreeLoopbackPort();
            foreach (string a in d.Args)
                args.Add(a.Replace("${port}", port.ToString(CultureInfo.InvariantCulture)));

            if (!string.IsNullOrEmpty(d.Command))
            {
                ProcessStartInfo psi = new ProcessStartInfo();
                psi.FileName = d.Command;
                psi.Arguments = BuildArgumentString(args);
                psi.UseShellExecute = false;
                psi.CreateNoWindow = true;
                psi.RedirectStandardError = true;
                psi.RedirectStandardInput = !d.UseTcp;
                psi.RedirectStandardOutput = true;
                if (!string.IsNullOrEmpty(d.Cwd) && Directory.Exists(d.Cwd)) psi.WorkingDirectory = d.Cwd;
                if (d.Env != null)
                    foreach (KeyValuePair<string, string> kv in d.Env)
                    {
                        if (kv.Value == null) psi.EnvironmentVariables.Remove(kv.Key);
                        else psi.EnvironmentVariables[kv.Key] = kv.Value;
                    }
                Process p;
                try { p = Process.Start(psi); }
                catch (Exception ex)
                {
                    error = "Could not start debug adapter '" + d.Command + "': " + ex.Message;
                    return null;
                }
                if (p == null) { error = "Could not start debug adapter '" + d.Command + "'"; return null; }
                c.Process = p;
                Thread errThread = new Thread(delegate ()
                {
                    try
                    {
                        string line;
                        while ((line = p.StandardError.ReadLine()) != null) c.RaiseStderr(line);
                    }
                    catch { }
                });
                errThread.IsBackground = true;
                errThread.Name = "DapAdapterStderr";
                errThread.Start();
                if (!d.UseTcp)
                {
                    c.Input = p.StandardOutput.BaseStream;
                    c.Output = p.StandardInput.BaseStream;
                    return c;
                }
                // TCP server adapters print to stdout; drain it so the pipe never fills.
                Thread outThread = new Thread(delegate ()
                {
                    try
                    {
                        string line;
                        while ((line = p.StandardOutput.ReadLine()) != null) c.RaiseStderr(line);
                    }
                    catch { }
                });
                outThread.IsBackground = true;
                outThread.Name = "DapAdapterStdout";
                outThread.Start();
            }

            // TCP: retry the connect while the server starts (up to ~10s). Loopback only.
            string host = string.IsNullOrEmpty(d.Host) ? "127.0.0.1" : d.Host;
            string url = "tcp://" + host + ":" + port.ToString(CultureInfo.InvariantCulture);
            string reason;
            if (!NetworkLedger.Check("Debugger", "CONNECT", url, out reason))
            {
                error = reason;
                c.Dispose();
                return null;
            }
            Exception last = null;
            Stopwatch sw = Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < 10000)
            {
                if (c.Process != null && c.Process.HasExited) break;
                try
                {
                    TcpClient tcp = new TcpClient();
                    tcp.NoDelay = true;
                    tcp.Connect(host, port);
                    c.Tcp = tcp;
                    NetworkStream ns = tcp.GetStream();
                    c.Input = ns;
                    c.Output = ns;
                    NetworkLedger.Record("Debugger", "CONNECT", url, 0, 0, 0, "debug adapter");
                    return c;
                }
                catch (Exception ex)
                {
                    last = ex;
                    Thread.Sleep(150);
                }
            }
            error = "Could not connect to debug adapter at " + host + ":" + port + (last != null ? " (" + last.Message + ")" : "");
            c.Dispose();
            return null;
        }

        public static string BuildArgumentString(IList<string> args)
        {
            StringBuilder sb = new StringBuilder();
            foreach (string a in args)
            {
                if (sb.Length > 0) sb.Append(' ');
                sb.Append(QuoteArg(a));
            }
            return sb.ToString();
        }

        /// <summary>Windows CommandLineToArgvW-compatible quoting (also what .NET Core uses to split on Unix).</summary>
        public static string QuoteArg(string a)
        {
            if (a == null) return "\"\"";
            if (a.Length > 0 && a.IndexOfAny(new char[] { ' ', '\t', '"', '\n' }) < 0) return a;
            StringBuilder sb = new StringBuilder("\"");
            int backslashes = 0;
            foreach (char ch in a)
            {
                if (ch == '\\') { backslashes++; continue; }
                if (ch == '"') { sb.Append('\\', backslashes * 2 + 1); sb.Append('"'); backslashes = 0; continue; }
                sb.Append('\\', backslashes);
                backslashes = 0;
                sb.Append(ch);
            }
            sb.Append('\\', backslashes * 2);
            sb.Append('"');
            return sb.ToString();
        }

        public void Dispose()
        {
            try { if (Tcp != null) Tcp.Close(); } catch { }
            try { if (Output != null) Output.Dispose(); } catch { }
            try { if (Process != null && !Process.HasExited) Process.Kill(); } catch { }
            try { if (Process != null) Process.Dispose(); } catch { }
        }
    }

    /// <summary>
    /// One debug session: the DAP state machine on top of <see cref="DapClient"/> (initialize, launch/attach,
    /// initialized -> setBreakpoints / setExceptionBreakpoints / configurationDone, stopped / continued / terminated).
    /// Thread-agnostic: events fire on the DAP dispatch thread; UIs marshal them (see DebugController).
    /// </summary>
    public sealed class DebugSession : IDisposable
    {
        private DapClient client;
        private DebugAdapterConnection connection;
        private volatile DebugSessionState state = DebugSessionState.NotStarted;
        private TaskCompletionSource<bool> initializedTcs;
        private TaskCompletionSource<bool> terminatedTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        private bool terminatedRaised;

        public Dictionary<string, object> Capabilities = new Dictionary<string, object>();
        public readonly List<DapExceptionFilter> ExceptionFilters = new List<DapExceptionFilter>();
        public string Name;
        public string AdapterType;
        public int StoppedThreadId;
        public string StopReason;
        public string StopDescription;
        public int? ExitCode;
        public DebugAdapterDescriptor Adapter;
        public bool IsAttach;

        public DebugSessionState State { get { return state; } }
        public DapClient Client { get { return client; } }
        public bool IsActive { get { return state != DebugSessionState.NotStarted && state != DebugSessionState.Terminated; } }

        public event Action<DebugSessionState> StateChanged;
        /// <summary>(category, text): adapter "output" events plus adapter stderr ("stderr") and client notes ("console").</summary>
        public event Action<string, string> Output;
        /// <summary>(threadId, reason, description, allThreadsStopped, text).</summary>
        public event Action<int, string, string, bool> Stopped;
        public event Action<int, bool> Continued;
        public event Action Terminated;
        public event Action<int> Exited;
        /// <summary>Adapter "breakpoint" event body (reason, breakpoint).</summary>
        public event Action<string, Dictionary<string, object>> BreakpointEvent;
        /// <summary>(reason "started"/"exited", threadId).</summary>
        public event Action<string, int> ThreadEvent;

        /// <summary>runInTerminal reverse request: (kind, title, cwd, args, env) -> process id (or null).</summary>
        public Func<string, string, string, List<string>, Dictionary<string, string>, Task<int?>> RunInTerminalHandler;

        /// <summary>Called after "initialized": send all breakpoints (the session awaits the returned task).</summary>
        public Func<DebugSession, Task> ConfigureHandler;

        private void SetState(DebugSessionState s)
        {
            if (state == s) return;
            if (state == DebugSessionState.Terminated) return;
            state = s;
            Action<DebugSessionState> h = StateChanged;
            if (h != null) { try { h(s); } catch { } }
        }

        private void RaiseOutput(string category, string text)
        {
            Action<string, string> h = Output;
            if (h != null) { try { h(category, text); } catch { } }
        }

        /// <summary>Uses an existing client (unit tests with a scripted fake adapter).</summary>
        public void AttachClient(DapClient c)
        {
            client = c;
            client.EventReceived += OnEvent;
            client.Closed += OnClosed;
            client.ReverseRequestHandler = OnReverseRequest;
        }

        /// <summary>
        /// Full start: open the adapter, initialize, send launch/attach, configure on "initialized", wait for the
        /// launch response. Returns null on success or an error message. Never throws.
        /// </summary>
        public async Task<string> StartAsync(DebugAdapterDescriptor adapter, string request, Dictionary<string, object> configuration)
        {
            Adapter = adapter;
            AdapterType = adapter != null ? adapter.Type : null;
            SetState(DebugSessionState.Initializing);
            if (client == null)
            {
                string err = null;
                DebugAdapterConnection conn = await Task.Run(delegate
                {
                    string e2;
                    DebugAdapterConnection cc = DebugAdapterConnection.Open(adapter, out e2);
                    err = e2;
                    return cc;
                }).ConfigureAwait(false);
                if (conn == null)
                {
                    Fail(err ?? "Could not start the debug adapter");
                    return err ?? "Could not start the debug adapter";
                }
                connection = conn;
                connection.StderrLine += delegate (string line) { RaiseOutput("stderr", line + "\n"); };
                if (connection.Process != null)
                {
                    try
                    {
                        connection.Process.EnableRaisingEvents = true;
                        connection.Process.Exited += delegate { Task.Delay(400).ContinueWith(delegate { OnClosed(null); }); };
                    }
                    catch { }
                }
                AttachClient(new DapClient(conn.Input, conn.Output));
                client.Start();
            }
            return await RunHandshakeAsync(adapter != null ? (adapter.AdapterId ?? adapter.Type) : "codeless", request, configuration).ConfigureAwait(false);
        }

        private void Fail(string message)
        {
            RaiseOutput("stderr", message + "\n");
            MarkTerminated();
        }

        private async Task<string> RunHandshakeAsync(string adapterId, string request, Dictionary<string, object> configuration)
        {
            initializedTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            Dictionary<string, object> initArgs = DapJson.Dict(
                "clientID", "codeless",
                "clientName", "codeLess",
                "adapterID", adapterId ?? "codeless",
                "pathFormat", "path",
                "linesStartAt1", true,
                "columnsStartAt1", true,
                "supportsVariableType", true,
                "supportsVariablePaging", false,
                "supportsRunInTerminalRequest", true,
                "supportsProgressReporting", false,
                "supportsInvalidatedEvent", false,
                "supportsMemoryReferences", false,
                "supportsArgsCanBeInterpretedByShell", false,
                "supportsStartDebuggingRequest", false,
                "locale", "en-us");
            DapResponse init = await client.SendRequestAsync("initialize", initArgs, 20000).ConfigureAwait(false);
            if (!init.Success)
            {
                string e = "Debug adapter failed to initialize: " + init.ErrorText;
                Fail(e);
                return e;
            }
            Capabilities = init.Body ?? new Dictionary<string, object>();
            ExceptionFilters.Clear();
            List<object> filters = DapJson.Arr(Capabilities, "exceptionBreakpointFilters");
            if (filters != null)
            {
                foreach (object f in filters)
                {
                    DapExceptionFilter ef = new DapExceptionFilter();
                    ef.Filter = DapJson.Str(f, "filter");
                    ef.Label = DapJson.Str(f, "label") ?? ef.Filter;
                    ef.Default = DapJson.Bool(f, "default", false);
                    ef.Enabled = ef.Default;
                    if (!string.IsNullOrEmpty(ef.Filter)) ExceptionFilters.Add(ef);
                }
            }

            string req = string.Equals(request, "attach", StringComparison.OrdinalIgnoreCase) ? "attach" : "launch";
            IsAttach = req == "attach";
            // debugpy answers launch only after configurationDone, so do not await it before configuring.
            Task<DapResponse> launchTask = client.SendRequestAsync(req, configuration ?? new Dictionary<string, object>(), 120000);
            Task first = await Task.WhenAny(initializedTcs.Task, launchTask).ConfigureAwait(false);
            if (first == launchTask && !launchTask.Result.Success)
            {
                string e = "Could not " + req + ": " + launchTask.Result.ErrorText;
                Fail(e);
                return e;
            }
            if (!initializedTcs.Task.IsCompleted)
            {
                Task waited = await Task.WhenAny(initializedTcs.Task, Task.Delay(15000)).ConfigureAwait(false);
                if (waited != initializedTcs.Task && client.IsClosed)
                {
                    Fail("Debug adapter exited before it was initialized");
                    return "Debug adapter exited before it was initialized";
                }
            }
            if (state == DebugSessionState.Initializing) SetState(DebugSessionState.Running);
            try
            {
                Func<DebugSession, Task> cfg = ConfigureHandler;
                if (cfg != null) await cfg(this).ConfigureAwait(false);
                else await SendExceptionBreakpointsAsync().ConfigureAwait(false);
            }
            catch (Exception ex) { RaiseOutput("stderr", "Failed to send breakpoints: " + ex.Message + "\n"); }
            if (DapJson.Bool(Capabilities, "supportsConfigurationDoneRequest", false))
                await client.SendRequestAsync("configurationDone", new Dictionary<string, object>()).ConfigureAwait(false);

            DapResponse launched = await launchTask.ConfigureAwait(false);
            if (!launched.Success)
            {
                string e = "Could not " + req + ": " + launched.ErrorText;
                Fail(e);
                return e;
            }
            return null;
        }

        private void OnEvent(string ev, Dictionary<string, object> body)
        {
            switch (ev)
            {
                case "initialized":
                    if (initializedTcs != null) initializedTcs.TrySetResult(true);
                    break;
                case "stopped":
                    {
                        Interlocked.Increment(ref stopCount);
                        StoppedThreadId = DapJson.Int(body, "threadId", StoppedThreadId);
                        StopReason = DapJson.Str(body, "reason") ?? "pause";
                        StopDescription = DapJson.Str(body, "description") ?? DapJson.Str(body, "text");
                        bool all = DapJson.Bool(body, "allThreadsStopped", false);
                        SetState(DebugSessionState.Stopped);
                        Action<int, string, string, bool> h = Stopped;
                        if (h != null) { try { h(StoppedThreadId, StopReason, StopDescription, all); } catch { } }
                        break;
                    }
                case "continued":
                    {
                        int tid = DapJson.Int(body, "threadId", 0);
                        bool all = DapJson.Bool(body, "allThreadsContinued", true);
                        SetState(DebugSessionState.Running);
                        Action<int, bool> h = Continued;
                        if (h != null) { try { h(tid, all); } catch { } }
                        break;
                    }
                case "output":
                    {
                        string cat = DapJson.Str(body, "category") ?? "console";
                        if (cat == "telemetry") break;
                        string text = DapJson.Str(body, "output") ?? "";
                        RaiseOutput(cat, text);
                        break;
                    }
                case "exited":
                    {
                        ExitCode = DapJson.Int(body, "exitCode", 0);
                        Action<int> h = Exited;
                        if (h != null) { try { h(ExitCode.Value); } catch { } }
                        break;
                    }
                case "terminated":
                    MarkTerminated();
                    break;
                case "breakpoint":
                    {
                        Action<string, Dictionary<string, object>> h = BreakpointEvent;
                        if (h != null) { try { h(DapJson.Str(body, "reason"), DapJson.Obj(body, "breakpoint")); } catch { } }
                        break;
                    }
                case "thread":
                    {
                        Action<string, int> h = ThreadEvent;
                        if (h != null) { try { h(DapJson.Str(body, "reason"), DapJson.Int(body, "threadId", 0)); } catch { } }
                        break;
                    }
            }
        }

        private void OnClosed(string error)
        {
            if (initializedTcs != null) initializedTcs.TrySetResult(false);
            if (!string.IsNullOrEmpty(error)) RaiseOutput("stderr", "Debug adapter connection error: " + error + "\n");
            MarkTerminated();
        }

        private void MarkTerminated()
        {
            lock (terminatedTcs)
            {
                if (terminatedRaised) return;
                terminatedRaised = true;
            }
            SetState(DebugSessionState.Terminated);
            terminatedTcs.TrySetResult(true);
            Action h = Terminated;
            if (h != null) { try { h(); } catch { } }
            // Let the adapter exit on its own (it got disconnect / its debuggee ended), then clean up.
            Task.Delay(1500).ContinueWith(delegate { DisposeTransport(); });
        }

        private Task<Dictionary<string, object>> OnReverseRequest(string command, Dictionary<string, object> args)
        {
            if (command != "runInTerminal") return null;
            Func<string, string, string, List<string>, Dictionary<string, string>, Task<int?>> h = RunInTerminalHandler;
            if (h == null) return null;
            string kind = DapJson.Str(args, "kind") ?? "integrated";
            string title = DapJson.Str(args, "title") ?? Name ?? "Debug";
            string cwd = DapJson.Str(args, "cwd");
            List<string> argv = new List<string>();
            List<object> raw = DapJson.Arr(args, "args");
            if (raw != null) foreach (object o in raw) argv.Add(Convert.ToString(o, CultureInfo.InvariantCulture));
            Dictionary<string, string> env = new Dictionary<string, string>();
            Dictionary<string, object> rawEnv = DapJson.Obj(args, "env");
            if (rawEnv != null)
                foreach (KeyValuePair<string, object> kv in rawEnv)
                    env[kv.Key] = kv.Value == null ? null : Convert.ToString(kv.Value, CultureInfo.InvariantCulture);
            Task<int?> t = h(kind, title, cwd, argv, env);
            return t.ContinueWith(delegate (Task<int?> done)
            {
                Dictionary<string, object> body = new Dictionary<string, object>();
                if (done.Result.HasValue) body["processId"] = done.Result.Value;
                return body;
            });
        }

        // ---- Requests ----

        public Task<DapResponse> Request(string command, Dictionary<string, object> args)
        {
            if (client == null) return Task.FromResult(DapResponse.Failure(command, "No debug session"));
            return client.SendRequestAsync(command, args ?? new Dictionary<string, object>());
        }

        /// <summary>Sends setBreakpoints for one file; returns the adapter's per-breakpoint results (same order).</summary>
        public async Task<List<Dictionary<string, object>>> SetBreakpointsAsync(string path, IList<DebugSourceBreakpoint> bps)
        {
            List<object> list = new List<object>();
            List<object> lines = new List<object>();
            foreach (DebugSourceBreakpoint bp in bps)
            {
                Dictionary<string, object> sb = DapJson.Dict("line", bp.Line + 1);
                if (!string.IsNullOrEmpty(bp.Condition) && DapJson.Bool(Capabilities, "supportsConditionalBreakpoints", true)) sb["condition"] = bp.Condition;
                if (!string.IsNullOrEmpty(bp.HitCondition) && DapJson.Bool(Capabilities, "supportsHitConditionalBreakpoints", true)) sb["hitCondition"] = bp.HitCondition;
                if (!string.IsNullOrEmpty(bp.LogMessage) && DapJson.Bool(Capabilities, "supportsLogPoints", true)) sb["logMessage"] = bp.LogMessage;
                list.Add(sb);
                lines.Add(bp.Line + 1);
            }
            Dictionary<string, object> args = DapJson.Dict(
                "source", DapJson.Dict("path", path, "name", Path.GetFileName(path)),
                "breakpoints", list,
                "lines", lines,
                "sourceModified", false);
            DapResponse r = await Request("setBreakpoints", args).ConfigureAwait(false);
            List<Dictionary<string, object>> result = new List<Dictionary<string, object>>();
            List<object> got = r.Success ? DapJson.Arr(r.Body, "breakpoints") : null;
            if (got != null) foreach (object o in got) result.Add(o as Dictionary<string, object> ?? new Dictionary<string, object>());
            return result;
        }

        public Task<DapResponse> SendExceptionBreakpointsAsync()
        {
            List<object> on = new List<object>();
            foreach (DapExceptionFilter f in ExceptionFilters) if (f.Enabled) on.Add(f.Filter);
            if (ExceptionFilters.Count == 0 && !DapJson.Has(Capabilities, "exceptionBreakpointFilters"))
                return Task.FromResult(DapResponse.Failure("setExceptionBreakpoints", "not supported"));
            return Request("setExceptionBreakpoints", DapJson.Dict("filters", on));
        }

        public async Task<List<DapThreadInfo>> GetThreadsAsync()
        {
            List<DapThreadInfo> res = new List<DapThreadInfo>();
            DapResponse r = await Request("threads", null).ConfigureAwait(false);
            List<object> arr = r.Success ? DapJson.Arr(r.Body, "threads") : null;
            if (arr != null)
                foreach (object o in arr)
                {
                    DapThreadInfo t = new DapThreadInfo();
                    t.Id = DapJson.Int(o, "id", 0);
                    t.Name = DapJson.Str(o, "name") ?? ("Thread " + t.Id);
                    res.Add(t);
                }
            return res;
        }

        public async Task<List<DapStackFrameInfo>> GetStackTraceAsync(int threadId, int levels)
        {
            List<DapStackFrameInfo> res = new List<DapStackFrameInfo>();
            DapResponse r = await Request("stackTrace", DapJson.Dict("threadId", threadId, "startFrame", 0, "levels", levels)).ConfigureAwait(false);
            List<object> arr = r.Success ? DapJson.Arr(r.Body, "stackFrames") : null;
            if (arr != null)
                foreach (object o in arr)
                {
                    DapStackFrameInfo f = new DapStackFrameInfo();
                    f.Id = DapJson.Int(o, "id", 0);
                    f.Name = DapJson.Str(o, "name");
                    f.Line = DapJson.Int(o, "line", 0);
                    f.Column = DapJson.Int(o, "column", 0);
                    f.PresentationHint = DapJson.Str(o, "presentationHint");
                    f.ThreadId = threadId;
                    Dictionary<string, object> src = DapJson.Obj(o, "source");
                    if (src != null)
                    {
                        f.SourcePath = DapJson.Str(src, "path");
                        f.SourceName = DapJson.Str(src, "name");
                        f.SourceReference = DapJson.Int(src, "sourceReference", 0);
                    }
                    res.Add(f);
                }
            return res;
        }

        public async Task<List<DapScopeInfo>> GetScopesAsync(int frameId)
        {
            List<DapScopeInfo> res = new List<DapScopeInfo>();
            DapResponse r = await Request("scopes", DapJson.Dict("frameId", frameId)).ConfigureAwait(false);
            List<object> arr = r.Success ? DapJson.Arr(r.Body, "scopes") : null;
            if (arr != null)
                foreach (object o in arr)
                {
                    DapScopeInfo s = new DapScopeInfo();
                    s.Name = DapJson.Str(o, "name") ?? "Scope";
                    s.VariablesReference = DapJson.Int(o, "variablesReference", 0);
                    s.Expensive = DapJson.Bool(o, "expensive", false);
                    s.NamedVariables = DapJson.Int(o, "namedVariables", 0);
                    s.IndexedVariables = DapJson.Int(o, "indexedVariables", 0);
                    res.Add(s);
                }
            return res;
        }

        public async Task<List<DapVariableInfo>> GetVariablesAsync(int variablesReference)
        {
            List<DapVariableInfo> res = new List<DapVariableInfo>();
            if (variablesReference <= 0) return res;
            DapResponse r = await Request("variables", DapJson.Dict("variablesReference", variablesReference)).ConfigureAwait(false);
            List<object> arr = r.Success ? DapJson.Arr(r.Body, "variables") : null;
            if (arr != null)
                foreach (object o in arr)
                {
                    DapVariableInfo v = new DapVariableInfo();
                    v.Name = DapJson.Str(o, "name") ?? "";
                    v.Value = DapJson.Str(o, "value") ?? "";
                    v.Type = DapJson.Str(o, "type");
                    v.EvaluateName = DapJson.Str(o, "evaluateName");
                    v.VariablesReference = DapJson.Int(o, "variablesReference", 0);
                    v.NamedVariables = DapJson.Int(o, "namedVariables", 0);
                    v.IndexedVariables = DapJson.Int(o, "indexedVariables", 0);
                    res.Add(v);
                }
            return res;
        }

        public async Task<DapEvaluateResult> EvaluateAsync(string expression, int frameId, string context)
        {
            Dictionary<string, object> args = DapJson.Dict("expression", expression, "context", context ?? "repl");
            if (frameId > 0) args["frameId"] = frameId;
            DapResponse r = await Request("evaluate", args).ConfigureAwait(false);
            DapEvaluateResult res = new DapEvaluateResult();
            res.Success = r.Success;
            if (r.Success)
            {
                res.Result = DapJson.Str(r.Body, "result") ?? "";
                res.Type = DapJson.Str(r.Body, "type");
                res.VariablesReference = DapJson.Int(r.Body, "variablesReference", 0);
            }
            else res.Error = r.ErrorText;
            return res;
        }

        public Task<DapResponse> ContinueAsync(int threadId)
        {
            int before = Interlocked.CompareExchange(ref stopCount, 0, 0);
            Task<DapResponse> t = Request("continue", DapJson.Dict("threadId", threadId));
            MarkRunningAfter(t, before);
            return t;
        }

        public Task<DapResponse> NextAsync(int threadId) { return Step("next", threadId); }
        public Task<DapResponse> StepInAsync(int threadId) { return Step("stepIn", threadId); }
        public Task<DapResponse> StepOutAsync(int threadId) { return Step("stepOut", threadId); }
        public Task<DapResponse> PauseAsync(int threadId) { return Request("pause", DapJson.Dict("threadId", threadId)); }

        private Task<DapResponse> Step(string cmd, int threadId)
        {
            int before = Interlocked.CompareExchange(ref stopCount, 0, 0);
            Task<DapResponse> t = Request(cmd, DapJson.Dict("threadId", threadId));
            MarkRunningAfter(t, before);
            return t;
        }

        // Adapters need not send "continued" after continue/step responses: the client assumes the program runs.
        // A "stopped" event that raced ahead of the response (fast steps) must not be overwritten with Running.
        private int stopCount;

        private void MarkRunningAfter(Task<DapResponse> t, int stopCountBefore)
        {
            t.ContinueWith(delegate (Task<DapResponse> done)
            {
                if (done.Result.Success && state == DebugSessionState.Stopped && Interlocked.CompareExchange(ref stopCount, 0, 0) == stopCountBefore)
                {
                    SetState(DebugSessionState.Running);
                    Action<int, bool> h = Continued;
                    if (h != null) { try { h(StoppedThreadId, true); } catch { } }
                }
            });
        }

        /// <summary>
        /// Ends the session (Stop / Shift+F5): terminate (graceful, if supported) then disconnect, then kill the
        /// adapter if it is still around. Never throws.
        /// </summary>
        public async Task StopAsync()
        {
            if (client == null || client.IsClosed) { MarkTerminated(); return; }
            try
            {
                bool launched = !IsAttach;
                if (DapJson.Bool(Capabilities, "supportsTerminateRequest", false) && state != DebugSessionState.Terminated)
                {
                    await client.SendRequestAsync("terminate", DapJson.Dict("restart", false), 3000).ConfigureAwait(false);
                    await Task.WhenAny(terminatedTcs.Task, Task.Delay(2500)).ConfigureAwait(false);
                }
                if (!client.IsClosed)
                    await client.SendRequestAsync("disconnect", DapJson.Dict("restart", false, "terminateDebuggee", launched), 3000).ConfigureAwait(false);
            }
            catch { }
            MarkTerminated();
        }

        private void DisposeTransport()
        {
            try { if (client != null) client.Dispose(); } catch { }
            try { if (connection != null) connection.Dispose(); } catch { }
        }

        public void Dispose()
        {
            MarkTerminated();
            DisposeTransport();
        }
    }
}
