using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading.Tasks;

namespace codeLess.Studio
{
    /// <summary>What the debugger needs from a UI (implemented by the WinForms form and the Avalonia window).</summary>
    public interface IDebugUiHost
    {
        /// <summary>Runs on the UI thread (asynchronously).</summary>
        void Post(Action action);
        /// <summary>runInTerminal: start the debuggee in the integrated terminal; returns its pid when known.</summary>
        Task<int?> RunInTerminalAsync(string title, string cwd, List<string> args, Dictionary<string, string> env);
        /// <summary>Opens the file (if needed) and reveals the 1-based line.</summary>
        void RevealLocation(string path, int line);
        IList<EditorTabBuffer> GetOpenBuffers();
        /// <summary>Repaint editors (gutter glyphs / stopped line changed).</summary>
        void RefreshEditors();
        void ShowStatus(string message);
        /// <summary>Non-modal notification; when installCommand is set, offer to run it in the terminal.</summary>
        void ShowDebugError(string message, string hint, string installCommand);
        string WorkspaceRoot { get; }
        IdePersistentSettings Settings { get; }
    }

    public sealed class DebugConsoleEntry
    {
        public string Category; // stdout | stderr | console | important | input | result | error
        public string Text;
    }

    public sealed class DebugWatchItem
    {
        public string Expression;
        public DapEvaluateResult Result;
    }

    /// <summary>
    /// The debugger brain shared by both UIs: configuration -> adapter -> session, breakpoints, call stack / frame
    /// selection, watches, debug console (REPL + history), exception filters, stopped-line decorations. All public
    /// state is read and mutated on the UI thread (host.Post); DAP traffic runs in the background. Strict C# 5.
    /// </summary>
    public sealed class DebugController
    {
        private readonly IDebugUiHost host;
        private DebugSession session;
        private DebugLaunchConfig lastConfig;
        private string lastActiveFile;
        private bool restartPending;
        private readonly Dictionary<string, bool> exceptionFilterChoices = new Dictionary<string, bool>(StringComparer.Ordinal);
        private int stopGeneration;

        public readonly DebugBreakpointStore Breakpoints = new DebugBreakpointStore();
        public readonly List<DebugWatchItem> Watches = new List<DebugWatchItem>();
        public readonly List<DebugConsoleEntry> Console = new List<DebugConsoleEntry>();
        public readonly List<string> ReplHistory = new List<string>();
        public List<DapThreadInfo> Threads = new List<DapThreadInfo>();
        public readonly Dictionary<int, List<DapStackFrameInfo>> FramesByThread = new Dictionary<int, List<DapStackFrameInfo>>();
        public int SelectedThreadId;
        public DapStackFrameInfo SelectedFrame;
        public string LastError;
        public string StatusText = "";

        /// <summary>Raised on the UI thread whenever session state changes (Running / Stopped / Terminated...).</summary>
        public event Action StateChanged;
        public event Action<DebugConsoleEntry> ConsoleAppended;
        public event Action ConsoleCleared;
        /// <summary>Threads / frames / selected frame changed (refresh CALL STACK + VARIABLES).</summary>
        public event Action CallStackChanged;
        public event Action BreakpointsChanged;
        public event Action WatchesChanged;

        public DebugController(IDebugUiHost host)
        {
            this.host = host;
            Breakpoints.Changed += delegate (string path) { OnStoreChanged(path); };
        }

        public DebugSession Session { get { return session; } }
        public DebugSessionState State { get { return session != null ? session.State : DebugSessionState.NotStarted; } }
        public bool IsActive { get { return session != null && session.IsActive; } }
        public bool IsStopped { get { return session != null && session.State == DebugSessionState.Stopped; } }
        public string SessionName { get { return session != null ? session.Name : null; } }
        public DebugLaunchConfig LastConfig { get { return lastConfig; } }

        public List<DapExceptionFilter> ExceptionFilters
        {
            get { return session != null ? session.ExceptionFilters : new List<DapExceptionFilter>(); }
        }

        private void Ui(Action a)
        {
            host.Post(delegate { try { a(); } catch (Exception ex) { AppendConsole("stderr", "[codeLess] " + ex.Message + "\n"); } });
        }

        private void RaiseState() { Action h = StateChanged; if (h != null) h(); }
        private void RaiseCallStack() { Action h = CallStackChanged; if (h != null) h(); }
        private void RaiseWatches() { Action h = WatchesChanged; if (h != null) h(); }

        // ---- Console ----

        public void AppendConsole(string category, string text)
        {
            if (string.IsNullOrEmpty(text)) return;
            DebugConsoleEntry e = new DebugConsoleEntry();
            e.Category = category;
            e.Text = text;
            Console.Add(e);
            if (Console.Count > 5000) Console.RemoveRange(0, Console.Count - 5000);
            Action<DebugConsoleEntry> h = ConsoleAppended;
            if (h != null) h(e);
        }

        public void ClearConsole()
        {
            Console.Clear();
            Action h = ConsoleCleared;
            if (h != null) h();
        }

        public string ConsoleText()
        {
            StringBuilder sb = new StringBuilder();
            foreach (DebugConsoleEntry e in Console) sb.Append(e.Text);
            return sb.ToString();
        }

        // ---- Configurations ----

        /// <summary>launch.json configurations (supported types only) plus the dynamic "Debug Active File" entry.</summary>
        public List<DebugLaunchConfig> GetConfigurations(out string error)
        {
            List<DebugLaunchConfig> res = new List<DebugLaunchConfig>();
            foreach (DebugLaunchConfig c in DebugLaunchConfigurations.LoadLaunchJson(host.WorkspaceRoot, out error))
                if (DebugLaunchConfigurations.IsSupportedFamily(c.AdapterFamily)) res.Add(c);
            return res;
        }

        public const string ActiveFileConfigName = "Debug Active File";

        /// <summary>
        /// F5: the chosen launch.json configuration (by name), else the first one, else Debug Active File defaults.
        /// </summary>
        public DebugLaunchConfig PickConfiguration(string preferredName, string activeFile, out string error)
        {
            error = null;
            string jsonErr;
            List<DebugLaunchConfig> all = GetConfigurations(out jsonErr);
            if (!string.IsNullOrEmpty(preferredName) && preferredName != ActiveFileConfigName)
                foreach (DebugLaunchConfig c in all) if (c.Name == preferredName) return c;
            if (preferredName != ActiveFileConfigName && all.Count > 0)
            {
                // Prefer a config whose family matches the active file (VS Code shows them first).
                string fam = DebugLaunchConfigurations.FamilyForFile(activeFile);
                foreach (DebugLaunchConfig c in all) if (fam != null && c.AdapterFamily == fam) return c;
                return all[0];
            }
            if (string.IsNullOrEmpty(activeFile))
            {
                error = jsonErr ?? "Open a file to debug, or create a launch.json.";
                return null;
            }
            return DebugLaunchConfigurations.CreateDefaultForFile(activeFile, host.WorkspaceRoot, out error);
        }

        /// <summary>
        /// F5 on a file codeLess cannot debug (no launch.json entry): true when it should simply run the file in the
        /// terminal instead (shell / PowerShell / Rust scripts, and Node.js unless a js-debug adapter is configured).
        /// </summary>
        public bool ShouldRunWithoutDebugging(DebugLaunchConfig cfg, string activeFile)
        {
            if (cfg == null)
            {
                if (string.IsNullOrEmpty(activeFile) || DebugLaunchConfigurations.FamilyForFile(activeFile) != null) return false;
                string ext = (Path.GetExtension(activeFile) ?? "").ToLowerInvariant();
                return ext == ".ps1" || ext == ".sh" || ext == ".zsh" || ext == ".bash" || ext == ".html" || ext == ".htm";
            }
            if (cfg.FromLaunchJson || cfg.AdapterFamily != "node") return false;
            IdePersistentSettings s = host.Settings;
            if (s != null && s.DebugAdapters != null)
                foreach (UserDebugAdapterEntry e in s.DebugAdapters)
                    if (e != null && DebugLaunchConfigurations.NormalizeType(e.Type) == "node") return false;
            return true;
        }

        // ---- Session lifecycle ----

        /// <summary>Starts debugging (no-op when a session is already active). Call on the UI thread.</summary>
        public Task StartAsync(DebugLaunchConfig config, string activeFile)
        {
            if (config == null) return Task.FromResult(0);
            if (IsActive)
            {
                host.ShowStatus("A debug session is already running.");
                return Task.FromResult(0);
            }
            lastConfig = config;
            lastActiveFile = activeFile;
            LastError = null;
            SyncOpenBuffersToStore();

            string ws = host.WorkspaceRoot;
            PythonInterpreterInfo interp = PythonInterpreterService.GetSelected(ws);
            DebugVariableContext ctx = new DebugVariableContext();
            ctx.WorkspaceFolder = string.IsNullOrEmpty(ws) ? (string.IsNullOrEmpty(activeFile) ? null : Path.GetDirectoryName(activeFile)) : ws;
            ctx.File = activeFile;
            ctx.PythonInterpreter = interp != null ? interp.Path : null;
            Dictionary<string, object> args = DebugLaunchConfigurations.PrepareLaunchArguments(config, ctx);

            DebugSession s = new DebugSession();
            s.Name = config.Name;
            session = s;
            WireSession(s);
            StatusText = "Starting " + config.Name + "...";
            host.ShowStatus(StatusText);
            AppendConsole("console", "Starting '" + config.Name + "'...\n");
            RaiseState();

            IdePersistentSettings settings = host.Settings;
            string pythonPath = ctx.PythonInterpreter;
            return Task.Run(delegate
            {
                DebugAdapterResolution res = DebugAdapterRegistry.Resolve(config, args, pythonPath, DebugAdapterEnvironment.CreateDefault(settings));
                return res;
            }).ContinueWith(delegate (Task<DebugAdapterResolution> t)
            {
                DebugAdapterResolution res = t.IsFaulted ? null : t.Result;
                if (res == null || res.Descriptor == null)
                {
                    string err = res != null ? res.Error : (t.Exception != null ? t.Exception.GetBaseException().Message : "Could not resolve a debug adapter");
                    Ui(delegate
                    {
                        LastError = err;
                        AppendConsole("stderr", err + "\n" + (res != null && res.Hint != null ? res.Hint + "\n" : ""));
                        host.ShowDebugError(err, res != null ? res.Hint : null, res != null ? res.InstallCommand : null);
                        s.Dispose();
                        if (session == s) session = null;
                        StatusText = "";
                        RaiseState();
                    });
                    return Task.FromResult(0);
                }
                if (res.Descriptor.Cwd == null) res.Descriptor.Cwd = ctx.WorkspaceFolder;
                return s.StartAsync(res.Descriptor, config.Request, args).ContinueWith(delegate (Task<string> st)
                {
                    string err = st.IsFaulted ? st.Exception.GetBaseException().Message : st.Result;
                    if (err != null)
                    {
                        Ui(delegate
                        {
                            LastError = err;
                            host.ShowDebugError(err, null, null);
                        });
                    }
                    return 0;
                });
            }).Unwrap();
        }

        private void WireSession(DebugSession s)
        {
            s.Output += delegate (string cat, string text) { Ui(delegate { if (session == s) AppendConsole(cat, text); }); };
            s.StateChanged += delegate (DebugSessionState st)
            {
                Ui(delegate
                {
                    if (session != s) return;
                    if (st == DebugSessionState.Running) StatusText = "Running: " + s.Name;
                    RaiseState();
                });
            };
            s.Stopped += delegate (int threadId, string reason, string description, bool all) { OnStopped(s, threadId, reason, description); };
            s.Continued += delegate (int threadId, bool all)
            {
                Ui(delegate
                {
                    if (session != s) return;
                    stopGeneration++;
                    ClearStoppedDecorations();
                    FramesByThread.Clear();
                    SelectedFrame = null;
                    RaiseCallStack();
                    RaiseState();
                });
            };
            s.Exited += delegate (int code) { Ui(delegate { if (session == s) AppendConsole("console", "Process exited with code " + code + ".\n"); }); };
            s.Terminated += delegate { Ui(delegate { OnTerminated(s); }); };
            s.BreakpointEvent += delegate (string reason, Dictionary<string, object> bp) { Ui(delegate { OnAdapterBreakpoint(bp); }); };
            s.ThreadEvent += delegate (string reason, int tid)
            {
                if (reason != "exited") return;
                Ui(delegate
                {
                    if (session != s) return;
                    Threads.RemoveAll(delegate (DapThreadInfo t) { return t.Id == tid; });
                    RaiseCallStack();
                });
            };
            s.RunInTerminalHandler = delegate (string kind, string title, string cwd, List<string> argv, Dictionary<string, string> env)
            {
                TaskCompletionSource<int?> tcs = new TaskCompletionSource<int?>();
                host.Post(delegate
                {
                    try
                    {
                        host.RunInTerminalAsync(title, cwd, argv, env).ContinueWith(delegate (Task<int?> t)
                        {
                            tcs.TrySetResult(t.IsFaulted || t.IsCanceled ? (int?)null : t.Result);
                        });
                    }
                    catch { tcs.TrySetResult(null); }
                });
                return tcs.Task;
            };
            s.ConfigureHandler = delegate (DebugSession sess) { return ConfigureSessionAsync(sess); };
        }

        private async Task ConfigureSessionAsync(DebugSession s)
        {
            // Snapshot on the UI thread, then send in the background.
            TaskCompletionSource<List<KeyValuePair<string, List<DebugSourceBreakpoint>>>> snap =
                new TaskCompletionSource<List<KeyValuePair<string, List<DebugSourceBreakpoint>>>>();
            host.Post(delegate
            {
                List<KeyValuePair<string, List<DebugSourceBreakpoint>>> l = new List<KeyValuePair<string, List<DebugSourceBreakpoint>>>();
                foreach (string f in Breakpoints.Files()) l.Add(new KeyValuePair<string, List<DebugSourceBreakpoint>>(f, EnabledFor(f)));
                foreach (DapExceptionFilter ef in s.ExceptionFilters)
                {
                    bool on;
                    if (exceptionFilterChoices.TryGetValue(s.AdapterType + "|" + ef.Filter, out on)) ef.Enabled = on;
                }
                snap.TrySetResult(l);
            });
            Task done = await Task.WhenAny(snap.Task, Task.Delay(5000)).ConfigureAwait(false);
            if (done == snap.Task)
            {
                foreach (KeyValuePair<string, List<DebugSourceBreakpoint>> kv in snap.Task.Result)
                {
                    List<Dictionary<string, object>> results = await s.SetBreakpointsAsync(kv.Key, kv.Value).ConfigureAwait(false);
                    ApplyVerification(kv.Value, results);
                }
            }
            await s.SendExceptionBreakpointsAsync().ConfigureAwait(false);
            Ui(delegate { RefreshBufferDecorations(); RaiseState(); });
        }

        private List<DebugSourceBreakpoint> EnabledFor(string file)
        {
            List<DebugSourceBreakpoint> res = new List<DebugSourceBreakpoint>();
            foreach (DebugSourceBreakpoint bp in Breakpoints.GetForFile(file)) if (bp.Enabled) res.Add(bp);
            return res;
        }

        private void ApplyVerification(List<DebugSourceBreakpoint> sent, List<Dictionary<string, object>> results)
        {
            Ui(delegate
            {
                for (int i = 0; i < sent.Count; i++)
                {
                    DebugSourceBreakpoint bp = sent[i];
                    if (i >= results.Count) { bp.Verified = null; continue; }
                    bp.Verified = DapJson.Bool(results[i], "verified", true);
                    bp.VerifyMessage = DapJson.Str(results[i], "message");
                    bp.AdapterId = DapJson.Int(results[i], "id", 0);
                }
                RefreshBufferDecorations();
                Action h = BreakpointsChanged;
                if (h != null) h();
            });
        }

        private void OnAdapterBreakpoint(Dictionary<string, object> bp)
        {
            int id = DapJson.Int(bp, "id", 0);
            if (id <= 0) return;
            foreach (DebugSourceBreakpoint b in Breakpoints.All())
            {
                if (b.AdapterId != id) continue;
                b.Verified = DapJson.Bool(bp, "verified", true);
                b.VerifyMessage = DapJson.Str(bp, "message");
            }
            RefreshBufferDecorations();
            Action h = BreakpointsChanged;
            if (h != null) h();
        }

        private void OnStopped(DebugSession s, int threadId, string reason, string description)
        {
            Task.Run(async delegate
            {
                List<DapThreadInfo> threads = await s.GetThreadsAsync().ConfigureAwait(false);
                int tid = threadId;
                if (tid <= 0 && threads.Count > 0) tid = threads[0].Id;
                List<DapStackFrameInfo> frames = await s.GetStackTraceAsync(tid, 50).ConfigureAwait(false);
                Ui(delegate
                {
                    if (session != s || s.State != DebugSessionState.Stopped) return;
                    stopGeneration++;
                    Threads = threads;
                    FramesByThread.Clear();
                    FramesByThread[tid] = frames;
                    SelectedThreadId = tid;
                    DapStackFrameInfo top = null;
                    foreach (DapStackFrameInfo f in frames) if (f.HasSource && f.PresentationHint != "subtle" && f.PresentationHint != "label") { top = f; break; }
                    if (top == null && frames.Count > 0) top = frames[0];
                    StatusText = "Paused on " + (reason ?? "breakpoint") + (string.IsNullOrEmpty(description) ? "" : ": " + description);
                    if (reason == "exception" && !string.IsNullOrEmpty(description))
                        AppendConsole("stderr", "Exception: " + description + "\n");
                    SelectFrameCore(top, frames.Count > 0 && top == frames[0], true);
                    RaiseState();
                    RefreshWatches();
                });
            });
        }

        /// <summary>Loads the stack of another thread (CALL STACK expansion).</summary>
        public void LoadThreadFrames(int threadId)
        {
            DebugSession s = session;
            if (s == null || !IsStopped) return;
            Task.Run(async delegate
            {
                List<DapStackFrameInfo> frames = await s.GetStackTraceAsync(threadId, 50).ConfigureAwait(false);
                Ui(delegate
                {
                    if (session != s) return;
                    FramesByThread[threadId] = frames;
                    RaiseCallStack();
                });
            });
        }

        public void SelectFrame(DapStackFrameInfo frame)
        {
            if (frame == null) return;
            List<DapStackFrameInfo> frames;
            bool isTop = FramesByThread.TryGetValue(frame.ThreadId, out frames) && frames.Count > 0 && frames[0] == frame;
            SelectedThreadId = frame.ThreadId;
            SelectFrameCore(frame, isTop, true);
            RefreshWatches();
        }

        private void SelectFrameCore(DapStackFrameInfo frame, bool isTop, bool reveal)
        {
            SelectedFrame = frame;
            ClearStoppedDecorations();
            if (frame != null && frame.HasSource && frame.Line > 0)
            {
                if (reveal) host.RevealLocation(frame.SourcePath, frame.Line);
                foreach (EditorTabBuffer b in host.GetOpenBuffers())
                {
                    if (DebugPaths.Same(b.FilePath, frame.SourcePath))
                    {
                        b.DebugStoppedLine = frame.Line - 1;
                        b.DebugStoppedIsTopFrame = isTop;
                    }
                }
            }
            host.RefreshEditors();
            RaiseCallStack();
        }

        /// <summary>Re-applies stopped markers after a tab was (re)opened while stopped.</summary>
        public void ApplyDecorations(EditorTabBuffer buf)
        {
            if (buf == null) return;
            Breakpoints.ApplyToBuffer(buf, IsActive);
            buf.DebugStoppedLine = -1;
            if (IsStopped && SelectedFrame != null && SelectedFrame.HasSource && DebugPaths.Same(buf.FilePath, SelectedFrame.SourcePath))
            {
                buf.DebugStoppedLine = SelectedFrame.Line - 1;
                List<DapStackFrameInfo> frames;
                buf.DebugStoppedIsTopFrame = FramesByThread.TryGetValue(SelectedFrame.ThreadId, out frames) && frames.Count > 0 && frames[0] == SelectedFrame;
            }
        }

        private void ClearStoppedDecorations()
        {
            foreach (EditorTabBuffer b in host.GetOpenBuffers()) b.DebugStoppedLine = -1;
            host.RefreshEditors();
        }

        private void OnTerminated(DebugSession s)
        {
            if (session != s) return;
            stopGeneration++;
            ClearStoppedDecorations();
            FramesByThread.Clear();
            Threads = new List<DapThreadInfo>();
            SelectedFrame = null;
            foreach (DebugSourceBreakpoint bp in Breakpoints.All()) bp.Verified = null;
            RefreshBufferDecorations();
            AppendConsole("console", "Debug session '" + s.Name + "' ended.\n");
            StatusText = "";
            RaiseCallStack();
            RaiseState();
            Action h = BreakpointsChanged;
            if (h != null) h();
            if (restartPending)
            {
                restartPending = false;
                session = null;
                StartAsync(lastConfig, lastActiveFile);
            }
        }

        // ---- Execution control (UI thread) ----

        private int CurrentThread()
        {
            if (SelectedThreadId > 0) return SelectedThreadId;
            if (session != null && session.StoppedThreadId > 0) return session.StoppedThreadId;
            return Threads.Count > 0 ? Threads[0].Id : 1;
        }

        public void ContinueOrPause()
        {
            if (!IsActive) return;
            if (IsStopped) Continue(); else Pause();
        }

        public void Continue() { if (IsStopped) session.ContinueAsync(CurrentThread()); }
        public void StepOver() { if (IsStopped) session.NextAsync(CurrentThread()); }
        public void StepInto() { if (IsStopped) session.StepInAsync(CurrentThread()); }
        public void StepOut() { if (IsStopped) session.StepOutAsync(CurrentThread()); }

        public void Pause()
        {
            if (session == null || session.State != DebugSessionState.Running) return;
            int tid = CurrentThread();
            DebugSession s = session;
            if (Threads.Count == 0)
            {
                // Threads are unknown until the first stop; ask the adapter first.
                s.GetThreadsAsync().ContinueWith(delegate (Task<List<DapThreadInfo>> t)
                {
                    int id = t.Result.Count > 0 ? t.Result[0].Id : tid;
                    s.PauseAsync(id);
                });
                return;
            }
            s.PauseAsync(tid);
        }

        public Task StopAsync()
        {
            restartPending = false;
            DebugSession s = session;
            if (s == null) return Task.FromResult(0);
            StatusText = "Stopping...";
            RaiseState();
            return s.StopAsync();
        }

        public void Restart()
        {
            if (lastConfig == null) return;
            if (!IsActive) { session = null; StartAsync(lastConfig, lastActiveFile); return; }
            restartPending = true;
            DebugSession s = session;
            s.StopAsync();
        }

        // ---- Breakpoints ----

        /// <summary>F9 / gutter: toggle at the 0-based line of a buffer.</summary>
        public void ToggleBreakpoint(EditorTabBuffer buf, int line)
        {
            if (buf == null || string.IsNullOrEmpty(buf.FilePath)) return;
            if (buf.Breakpoints == null) buf.Breakpoints = new HashSet<int>();
            if (!buf.Breakpoints.Remove(line)) buf.Breakpoints.Add(line);
            OnBufferBreakpointToggled(buf, line);
        }

        /// <summary>The canvases already flipped buf.Breakpoints (EditorDebugHooks): sync store, glyphs, adapter.</summary>
        public void OnBufferBreakpointToggled(EditorTabBuffer buf, int line)
        {
            if (buf == null || string.IsNullOrEmpty(buf.FilePath)) return;
            Breakpoints.SyncFromBuffer(buf);
            Breakpoints.ApplyToBuffer(buf, IsActive);
            host.RefreshEditors();
        }

        public void SetBreakpointEnabled(DebugSourceBreakpoint bp, bool enabled) { Breakpoints.SetEnabled(bp, enabled); }
        public void RemoveBreakpoint(DebugSourceBreakpoint bp) { Breakpoints.Remove(bp); }

        /// <summary>Adds or edits a conditional breakpoint / logpoint at a line.</summary>
        public DebugSourceBreakpoint SetBreakpointOptions(string path, int line, string condition, string hitCondition, string logMessage)
        {
            DebugSourceBreakpoint bp = Breakpoints.Find(path, line) ?? Breakpoints.Add(path, line);
            Breakpoints.Update(bp, condition, hitCondition, logMessage);
            return bp;
        }

        private void OnStoreChanged(string path)
        {
            RefreshBufferDecorations();
            host.RefreshEditors();
            Action h = BreakpointsChanged;
            if (h != null) h();
            if (!IsActive || session.State == DebugSessionState.Initializing) return;
            List<string> files = new List<string>();
            if (path != null) files.Add(path);
            else
            {
                files.AddRange(Breakpoints.Files());
                foreach (string f in sentFiles) if (!files.Contains(f)) files.Add(f);
            }
            DebugSession s = session;
            foreach (string f in files)
            {
                if (!sentFiles.Contains(f)) sentFiles.Add(f);
                List<DebugSourceBreakpoint> enabled = EnabledFor(f);
                s.SetBreakpointsAsync(f, enabled).ContinueWith(delegate (Task<List<Dictionary<string, object>>> t)
                {
                    if (!t.IsFaulted) ApplyVerification(enabled, t.Result);
                });
            }
        }

        private readonly List<string> sentFiles = new List<string>();

        private void SyncOpenBuffersToStore()
        {
            foreach (EditorTabBuffer b in host.GetOpenBuffers())
            {
                if (string.IsNullOrEmpty(b.FilePath)) continue;
                Breakpoints.ApplyToBuffer(b, false);
            }
            sentFiles.Clear();
            sentFiles.AddRange(Breakpoints.Files());
        }

        private void RefreshBufferDecorations()
        {
            bool active = IsActive;
            foreach (EditorTabBuffer b in host.GetOpenBuffers())
                if (!string.IsNullOrEmpty(b.FilePath)) Breakpoints.ApplyToBuffer(b, active);
        }

        public void SetExceptionFilter(DapExceptionFilter filter, bool enabled)
        {
            if (filter == null) return;
            filter.Enabled = enabled;
            if (session != null) exceptionFilterChoices[session.AdapterType + "|" + filter.Filter] = enabled;
            if (IsActive) session.SendExceptionBreakpointsAsync();
            Action h = BreakpointsChanged;
            if (h != null) h();
        }

        // ---- Variables / evaluation ----

        public Task<List<DapScopeInfo>> GetScopesAsync(DapStackFrameInfo frame)
        {
            if (session == null || frame == null || !IsStopped) return Task.FromResult(new List<DapScopeInfo>());
            return session.GetScopesAsync(frame.Id);
        }

        public Task<List<DapVariableInfo>> GetVariablesAsync(int variablesReference)
        {
            if (session == null || !IsStopped) return Task.FromResult(new List<DapVariableInfo>());
            return session.GetVariablesAsync(variablesReference);
        }

        public Task<DapEvaluateResult> EvaluateAsync(string expression, string context)
        {
            if (session == null || !IsActive)
            {
                DapEvaluateResult r = new DapEvaluateResult();
                r.Error = "No active debug session";
                return Task.FromResult(r);
            }
            int frameId = SelectedFrame != null ? SelectedFrame.Id : 0;
            return session.EvaluateAsync(expression, frameId, context);
        }

        /// <summary>DEBUG CONSOLE input: echoes, evaluates in the selected frame, prints the result.</summary>
        public void SubmitRepl(string expression)
        {
            if (string.IsNullOrWhiteSpace(expression)) return;
            string expr = expression.Trim();
            if (ReplHistory.Count == 0 || ReplHistory[ReplHistory.Count - 1] != expr) ReplHistory.Add(expr);
            if (ReplHistory.Count > 200) ReplHistory.RemoveAt(0);
            AppendConsole("input", "> " + expr + "\n");
            if (!IsActive)
            {
                AppendConsole("error", "No active debug session. Start debugging (F5) to evaluate expressions.\n");
                return;
            }
            EvaluateAsync(expr, "repl").ContinueWith(delegate (Task<DapEvaluateResult> t)
            {
                DapEvaluateResult r = t.Result;
                Ui(delegate
                {
                    if (r.Success) AppendConsole("result", (r.Result ?? "") + "\n");
                    else AppendConsole("error", (r.Error ?? "Evaluation failed") + "\n");
                });
            });
        }

        public void AddWatch(string expression)
        {
            if (string.IsNullOrWhiteSpace(expression)) return;
            DebugWatchItem w = new DebugWatchItem();
            w.Expression = expression.Trim();
            Watches.Add(w);
            RaiseWatches();
            RefreshWatches();
        }

        public void RemoveWatch(DebugWatchItem w)
        {
            if (Watches.Remove(w)) RaiseWatches();
        }

        public void RefreshWatches()
        {
            if (Watches.Count == 0) return;
            if (!IsStopped)
            {
                foreach (DebugWatchItem w in Watches) w.Result = null;
                RaiseWatches();
                return;
            }
            int gen = stopGeneration;
            List<DebugWatchItem> items = new List<DebugWatchItem>(Watches);
            foreach (DebugWatchItem w in items)
            {
                DebugWatchItem item = w;
                EvaluateAsync(item.Expression, "watch").ContinueWith(delegate (Task<DapEvaluateResult> t)
                {
                    Ui(delegate
                    {
                        if (gen != stopGeneration) return;
                        item.Result = t.Result;
                        RaiseWatches();
                    });
                });
            }
        }

        /// <summary>
        /// Hover while stopped: evaluates the expression ("hover" context) and returns "value" text, or null.
        /// </summary>
        public Task<string> EvaluateForHoverAsync(string expression)
        {
            if (!IsStopped || string.IsNullOrWhiteSpace(expression)) return Task.FromResult<string>(null);
            return EvaluateAsync(expression, "hover").ContinueWith(delegate (Task<DapEvaluateResult> t)
            {
                DapEvaluateResult r = t.Result;
                if (!r.Success) return null;
                string v = r.Result ?? "";
                if (v.Length > 400) v = v.Substring(0, 400) + "...";
                return v;
            });
        }

        /// <summary>Extracts a hover expression (identifier with dotted members) around a column, or null.</summary>
        public static string ExpressionAt(string line, int col)
        {
            if (string.IsNullOrEmpty(line) || col < 0 || col > line.Length) return null;
            int c = col < line.Length ? col : line.Length - 1;
            if (c < 0 || !(char.IsLetterOrDigit(line[c]) || line[c] == '_'))
            {
                if (c > 0 && (char.IsLetterOrDigit(line[c - 1]) || line[c - 1] == '_')) c--;
                else return null;
            }
            int end = c;
            while (end + 1 < line.Length && (char.IsLetterOrDigit(line[end + 1]) || line[end + 1] == '_')) end++;
            int start = c;
            while (start > 0 && (char.IsLetterOrDigit(line[start - 1]) || line[start - 1] == '_' || line[start - 1] == '.')) start--;
            while (start < c && line[start] == '.') start++;
            string expr = line.Substring(start, end - start + 1);
            if (expr.Length == 0 || char.IsDigit(expr[0])) return null;
            switch (expr)
            {
                case "def": case "class": case "return": case "if": case "else": case "for": case "while": case "import":
                case "from": case "in": case "and": case "or": case "not": case "func": case "var": case "let": case "const":
                case "public": case "private": case "static": case "void": case "int": case "new":
                    return null;
            }
            return expr;
        }
    }
}
