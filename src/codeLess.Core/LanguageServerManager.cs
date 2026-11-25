using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace codeLess.Studio
{
    public enum LanguageServerState
    {
        None,
        Starting,
        Running,
        Error,
        Stopped,
        NotInstalled,
        Disabled,
        /// <summary>Not started: the workspace is in Restricted Mode and this server would run project code.</summary>
        Restricted,
        /// <summary>Missing and being installed automatically into the codeLess-managed folder.</summary>
        Installing
    }

    /// <summary>Snapshot of the server serving one file, for the status bar.</summary>
    public class LanguageServerStatus
    {
        public string Key;
        public string LanguageId;
        public string ServerName;
        public LanguageServerState State;
        public string Message;
        public string ProgressText;
        public string InstallCommand;
        /// <summary>codeLess knows an automatic (sudo-free) install route for this server family.</summary>
        public bool CanInstall;

        public string StatusBarText
        {
            get
            {
                string name = string.IsNullOrEmpty(ServerName) ? (LanguageId ?? "") : ServerName;
                switch (State)
                {
                    case LanguageServerState.Starting: return "{ } " + name + " (starting…)";
                    case LanguageServerState.Running:
                        return string.IsNullOrEmpty(ProgressText) ? "{ } " + name : "{ } " + name + ": " + ProgressText;
                    case LanguageServerState.Error: return "{ } " + name + " ⚠";
                    case LanguageServerState.Stopped: return "{ } " + name + " (stopped)";
                    case LanguageServerState.NotInstalled:
                        {
                            LanguageServerDefinition d = LanguageServerRegistry.DefinitionForLanguage(LanguageId ?? "");
                            return "{ } No " + (d != null ? d.DisplayName : name) + " server";
                        }
                    case LanguageServerState.Disabled: return "{ } " + name + " (off)";
                    case LanguageServerState.Restricted: return "{ } " + name + " (Restricted Mode)";
                    case LanguageServerState.Installing:
                        {
                            LanguageServerDefinition d = LanguageServerRegistry.DefinitionForLanguage(LanguageId ?? "");
                            return "⟳ Installing " + (d != null ? d.DisplayName : name) + " language server…";
                        }
                    default: return "";
                }
            }
        }

        public string Tooltip
        {
            get
            {
                string s = (ServerName ?? LanguageId ?? "Language server") + " — " + State;
                if (!string.IsNullOrEmpty(Message)) s += "\n" + Message;
                if (!string.IsNullOrEmpty(InstallCommand) && State != LanguageServerState.Installing) s += "\nInstall: " + InstallCommand;
                s += State == LanguageServerState.Installing ? "\nClick to see the log or cancel." : "\nClick for log and restart.";
                return s;
            }
        }
    }

    /// <summary>"Install a language server" suggestion (raised once per language per session).</summary>
    public class LanguageServerHint
    {
        public string LanguageId;
        public string LanguageName;
        public string ServerName;
        public string InstallCommand;
        public string InstallUrl;
        public string Message;
        /// <summary>An automatic install was attempted and failed (the notification offers Show Log / Copy Command).</summary>
        public bool InstallFailed;
        /// <summary>Why it wasn't installed automatically / why the install failed.</summary>
        public string Reason;
    }

    internal sealed class LspOpenDocument
    {
        public string Path;
        public string Key;
        public string Uri;
        public string LanguageId;
        public Func<string> TextProvider;
        public string LatestText;   // last snapshot taken on the UI thread
        public string SentText;     // what the server has (null = not opened on the server)
        public int Version;
        public bool Dirty;
        public LanguageServerInstance Instance;
    }

    /// <summary>One running (or failed) server process for a (server family, root) pair.</summary>
    public sealed class LanguageServerInstance
    {
        public string Key { get; internal set; }
        public string Root { get; internal set; }
        public LanguageServerDefinition Definition { get; internal set; }
        public string LanguageId { get; internal set; }
        public LanguageServerResolution Resolution { get; internal set; }
        public LanguageServerState State { get; internal set; }
        public string Message { get; internal set; }
        public string ProgressText { get; internal set; }
        public Dictionary<string, object> ServerCapabilities { get; internal set; }
        public int ProcessId { get; internal set; }
        public int RestartCount { get; internal set; }

        internal LspRpcEndpoint Endpoint;
        internal Process Process;
        internal volatile bool ShuttingDown;
        internal int Generation;
        internal DateTime LastResolveUtc;
        internal readonly HashSet<string> DynamicRegistrations = new HashSet<string>(StringComparer.Ordinal);
        internal readonly Dictionary<string, string> ProgressTitles = new Dictionary<string, string>(StringComparer.Ordinal);
        internal readonly List<DateTime> CrashTimes = new List<DateTime>();
        /// <summary>workspace/didChangeWatchedFiles watchers by registration id (dynamic registration).</summary>
        internal readonly Dictionary<string, List<LspFileWatcher>> FileWatchers = new Dictionary<string, List<LspFileWatcher>>(StringComparer.Ordinal);

        /// <summary>Number of workspace/didChangeWatchedFiles watchers the server registered.</summary>
        public int RegisteredFileWatcherCount
        {
            get { lock (FileWatchers) { int n = 0; foreach (List<LspFileWatcher> l in FileWatchers.Values) n += l.Count; return n; } }
        }

        private readonly object chainLock = new object();
        private Task chain = Task.FromResult(true);

        private readonly object logLock = new object();
        private readonly LinkedList<string> log = new LinkedList<string>();
        private int logChars;
        public const int MaxLogLines = 3000;
        public const int MaxLogChars = 1024 * 1024;
        public const int MaxLogLineChars = 4000;

        public string DisplayName
        {
            get
            {
                if (Resolution != null && !string.IsNullOrEmpty(Resolution.DisplayName)) return Resolution.DisplayName;
                return Definition != null ? Definition.DisplayName : LanguageId;
            }
        }

        public void AppendLog(string line)
        {
            if (line == null) return;
            if (line.Length > MaxLogLineChars) line = line.Substring(0, MaxLogLineChars) + " …";
            string stamped = DateTime.Now.ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture) + " " + line;
            lock (logLock)
            {
                log.AddLast(stamped);
                logChars += stamped.Length;
                while (log.Count > MaxLogLines || logChars > MaxLogChars)
                {
                    logChars -= log.First.Value.Length;
                    log.RemoveFirst();
                }
            }
        }

        public string GetLogText()
        {
            lock (logLock)
            {
                StringBuilder sb = new StringBuilder(logChars + log.Count);
                foreach (string l in log) sb.Append(l).Append('\n');
                return sb.ToString();
            }
        }

        public int LogLineCount { get { lock (logLock) { return log.Count; } } }

        /// <summary>Runs <paramref name="action"/> after everything queued before it (serial, thread-pool).</summary>
        internal void Enqueue(Action action)
        {
            lock (chainLock)
            {
                chain = chain.ContinueWith(delegate (Task t)
                {
                    try { action(); }
                    catch (Exception ex) { AppendLog("[client] " + ex.GetType().Name + ": " + ex.Message); }
                }, CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);
            }
        }

        /// <summary>Queues a request send behind pending document syncs; the returned task completes with the response.</summary>
        internal Task<object> EnqueueRequest(Func<Task<object>> send)
        {
            lock (chainLock)
            {
                Task<Task<object>> outer = chain.ContinueWith(delegate (Task t)
                {
                    try { return send(); }
                    catch (Exception ex)
                    {
                        TaskCompletionSource<object> f = new TaskCompletionSource<object>();
                        f.SetException(ex);
                        return f.Task;
                    }
                }, CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);
                chain = outer;
                return outer.Unwrap();
            }
        }

        public bool HasCapability(string name)
        {
            if (DynamicRegistrations.Contains(name)) return true;
            Dictionary<string, object> caps = ServerCapabilities;
            if (caps == null) return false;
            object v;
            if (!caps.TryGetValue(name, out v) || v == null) return false;
            if (v is bool) return (bool)v;
            return true;
        }

        /// <summary>0 = none, 1 = full, 2 = incremental.</summary>
        public int TextSyncKind
        {
            get
            {
                object ts = ServerCapabilities != null ? LspJson.Get(ServerCapabilities, "textDocumentSync") : null;
                if (ts == null) return 1;
                if (ts is long) return (int)(long)ts;
                return LspJson.GetInt(ts, "change", 1);
            }
        }

        public bool SaveIncludesText
        {
            get
            {
                object ts = ServerCapabilities != null ? LspJson.Get(ServerCapabilities, "textDocumentSync") : null;
                object save = LspJson.Get(ts, "save");
                return save is Dictionary<string, object> && LspJson.GetBool(save, "includeText", false);
            }
        }

        public List<string> GetTriggerCharacters(string provider, string field)
        {
            List<string> r = new List<string>();
            object p = ServerCapabilities != null ? LspJson.Get(ServerCapabilities, provider) : null;
            List<object> arr = LspJson.GetList(p, field);
            if (arr != null) foreach (object o in arr) { string s = o as string; if (!string.IsNullOrEmpty(s)) r.Add(s); }
            return r;
        }
    }

    /// <summary>
    /// Starts one language server per (server family, workspace root) lazily when a matching file opens, keeps open
    /// documents in sync (debounced didChange, incremental when supported), restarts crashed servers with backoff,
    /// and exposes LSP features as Task-returning calls that never block the caller. Shared by the WinForms and
    /// Avalonia UIs; all public members are meant to be called on the UI thread, events are raised through the
    /// <c>postToUi</c> callback. C# 5 only.
    /// </summary>
    public sealed class LanguageServerManager : IDisposable
    {
        private readonly Action<Action> postToUi;
        private readonly object sync = new object();
        private readonly Dictionary<string, LanguageServerInstance> instances = new Dictionary<string, LanguageServerInstance>(StringComparer.Ordinal);
        private readonly Dictionary<string, LspOpenDocument> documents = new Dictionary<string, LspOpenDocument>(StringComparer.Ordinal);
        private readonly HashSet<string> hintedLanguages = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private Timer debounceTimer;
        private bool disposed;

        public string WorkspaceRoot { get; private set; }
        public int ChangeDebounceMs = 150;
        public int MaxRestarts = 3;
        public int RestartWindowSeconds = 180;
        public int RequestTimeoutMs = 8000;
        public int LongRequestTimeoutMs = 20000;
        public int InitializeTimeoutMs = 60000;
        /// <summary>Base backoff before restarting a crashed server (doubles per crash).</summary>
        public int RestartBackoffMs = 1000;
        public string ClientVersion = "3.2";

        /// <summary>Current user overrides (called whenever a server is resolved).</summary>
        public Func<LanguageServerUserSettings> UserSettingsProvider;
        /// <summary>Languages whose install hint must not be raised (persisted dismissals).</summary>
        public Func<string, bool> IsHintSuppressed;
        /// <summary>Applies a WorkspaceEdit requested by the server (workspace/applyEdit). Called on the UI thread.</summary>
        public Func<LspWorkspaceEdit, bool> ApplyWorkspaceEditHandler;
        /// <summary>Installs missing servers into the managed folder (null: no installs; the default managed folder is still searched).</summary>
        public LanguageServerInstaller Installer;
        /// <summary>"Install missing language servers automatically" (read whenever a server turns out to be missing).</summary>
        public Func<bool> AutoInstallEnabled;
        /// <summary>One line of install output (raised on the UI thread) for the Output panel.</summary>
        public event Action<string> InstallOutput;
        /// <summary>An install finished (raised on the UI thread).</summary>
        public event Action<LanguageServerInstallResult> InstallFinished;

        /// <summary>Folder searched before PATH (where automatic installs go).</summary>
        public string ManagedServersRoot
        {
            get { LanguageServerInstaller i = Installer; return i != null ? i.ManagedRoot : LanguageServerInstaller.DefaultManagedRoot(); }
        }

        public event Action<string> DiagnosticsChanged;
        public event Action StatusChanged;
        public event Action<LanguageServerHint> ServerMissing;
        /// <summary>(severity "error"/"warning"/"info", text) from window/showMessage.</summary>
        public event Action<string, string> ServerMessage;

        public LanguageServerManager(Action<Action> postToUi)
        {
            this.postToUi = postToUi ?? delegate (Action a) { a(); };
        }

        private void Post(Action a)
        {
            if (disposed) return;
            try { postToUi(delegate { try { a(); } catch { } }); } catch { }
        }

        private void RaiseStatusChanged()
        {
            Post(delegate { Action h = StatusChanged; if (h != null) h(); });
        }

        // ================================================================ workspace / lifecycle

        /// <summary>Switches workspace: servers of the previous root are shut down (in the background).</summary>
        public void SetWorkspaceRoot(string root)
        {
            string norm = string.IsNullOrEmpty(root) ? null : root;
            if (string.Equals(LspText.PathKey(norm ?? ""), LspText.PathKey(WorkspaceRoot ?? ""), StringComparison.Ordinal)) return;
            WorkspaceRoot = norm;
            List<LanguageServerInstance> old;
            lock (sync)
            {
                old = instances.Values.ToList();
                instances.Clear();
                foreach (LspOpenDocument d in documents.Values) { d.Instance = null; d.SentText = null; }
            }
            ClearDiagnosticsFor(old);
            if (old.Count > 0)
                Task.Factory.StartNew(delegate { foreach (LanguageServerInstance i in old) StopInstance(i, 1500); }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
            // Re-attach open documents to servers for the new root.
            List<LspOpenDocument> docs;
            lock (sync) { docs = documents.Values.ToList(); }
            foreach (LspOpenDocument d in docs) AttachDocument(d);
            RaiseStatusChanged();
        }

        /// <summary>Synchronous best-effort shutdown of every server (app exit). Bounded to about <paramref name="timeoutMs"/>.</summary>
        public void ShutdownAll(int timeoutMs)
        {
            List<LanguageServerInstance> all;
            lock (sync)
            {
                all = instances.Values.ToList();
                instances.Clear();
            }
            ClearDiagnosticsFor(all);
            if (all.Count == 0) return;
            List<Task> tasks = new List<Task>();
            foreach (LanguageServerInstance i in all)
            {
                LanguageServerInstance inst = i;
                tasks.Add(Task.Factory.StartNew(delegate { StopInstance(inst, Math.Max(200, timeoutMs - 200)); }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default));
            }
            try { Task.WaitAll(tasks.ToArray(), timeoutMs); } catch { }
            foreach (LanguageServerInstance i in all) KillProcessTree(i.Process);
        }

        public void Dispose()
        {
            if (disposed) return;
            ShutdownAll(1500);
            disposed = true;
            if (debounceTimer != null) { try { debounceTimer.Dispose(); } catch { } }
            lock (watchLock) { if (watchTimer != null) { try { watchTimer.Dispose(); } catch { } watchTimer = null; } }
        }

        public List<LanguageServerInstance> GetInstances()
        {
            lock (sync) { return instances.Values.ToList(); }
        }

        // ================================================================ documents

        /// <summary>Registers an open document (idempotent). Starts its server lazily. <paramref name="textProvider"/> is called on the UI thread.</summary>
        public void OpenDocument(string path, Func<string> textProvider)
        {
            if (disposed || string.IsNullOrEmpty(path) || textProvider == null) return;
            string lang = LanguageServerRegistry.LanguageIdForPath(path);
            if (lang == null) return;
            string key = LspText.PathKey(path);
            LspOpenDocument doc;
            lock (sync)
            {
                if (documents.TryGetValue(key, out doc))
                {
                    doc.TextProvider = textProvider;
                    if (doc.Instance != null && doc.Instance.State == LanguageServerState.NotInstalled &&
                        (DateTime.UtcNow - doc.Instance.LastResolveUtc).TotalSeconds > 20)
                    {
                        // A server may have been installed meanwhile: re-detect.
                        RetryResolve(doc.Instance);
                    }
                    return;
                }
                doc = new LspOpenDocument();
                doc.Path = path;
                doc.Key = key;
                doc.Uri = LspText.PathToUri(Path.GetFullPath(path));
                doc.LanguageId = lang;
                doc.TextProvider = textProvider;
                documents[key] = doc;
            }
            try { doc.LatestText = textProvider() ?? ""; } catch { doc.LatestText = ""; }
            AttachDocument(doc);
        }

        /// <summary>LSP version last sent to the server for this document (-1 when it isn't open on a server).</summary>
        public int GetSentVersion(string path)
        {
            LspOpenDocument d = FindDoc(path);
            if (d == null || d.SentText == null) return -1;
            return d.Version;
        }

        /// <summary>Text the server currently has for this document (null when it isn't open on a server).</summary>
        public string GetSentText(string path)
        {
            LspOpenDocument d = FindDoc(path);
            return d != null ? d.SentText : null;
        }

        public bool IsDocumentOpen(string path)
        {
            lock (sync) { return documents.ContainsKey(LspText.PathKey(path)); }
        }

        /// <summary>Cheap: marks the document dirty and (re)starts the debounce timer.</summary>
        public void ChangeDocument(string path)
        {
            if (disposed || string.IsNullOrEmpty(path)) return;
            lock (sync)
            {
                LspOpenDocument doc;
                if (!documents.TryGetValue(LspText.PathKey(path), out doc)) return;
                doc.Dirty = true;
                if (debounceTimer == null) debounceTimer = new Timer(delegate { Post(FlushAllDirty); }, null, Timeout.Infinite, Timeout.Infinite);
                debounceTimer.Change(Math.Max(1, ChangeDebounceMs), Timeout.Infinite);
            }
        }

        /// <summary>Snapshots dirty documents and queues didChange. UI thread.</summary>
        public void FlushAllDirty()
        {
            List<LspOpenDocument> dirty;
            lock (sync) { dirty = documents.Values.Where(d => d.Dirty).ToList(); }
            foreach (LspOpenDocument d in dirty) FlushDoc(d);
        }

        /// <summary>Sends pending changes of one document now (called before every request). UI thread.</summary>
        public void FlushDocument(string path)
        {
            LspOpenDocument doc = FindDoc(path);
            if (doc != null) FlushDoc(doc);
        }

        private void FlushDoc(LspOpenDocument doc)
        {
            string text;
            try { text = doc.TextProvider() ?? ""; } catch { return; }
            LanguageServerInstance inst;
            lock (sync)
            {
                doc.Dirty = false;
                doc.LatestText = text;
                inst = doc.Instance;
            }
            if (inst != null) inst.Enqueue(delegate { SyncDocument(inst, doc); });
        }

        public void SaveDocument(string path)
        {
            LspOpenDocument doc = FindDoc(path);
            if (doc == null) return;
            FlushDoc(doc);
            LanguageServerInstance inst = doc.Instance;
            if (inst == null) return;
            inst.Enqueue(delegate
            {
                if (inst.Endpoint == null || inst.State != LanguageServerState.Running || doc.SentText == null) return;
                Dictionary<string, object> p = LspJson.Obj("textDocument", LspJson.Obj("uri", doc.Uri));
                if (inst.SaveIncludesText) p["text"] = doc.SentText;
                inst.Endpoint.SendNotification("textDocument/didSave", p);
            });
        }

        public void CloseDocument(string path)
        {
            LspOpenDocument doc;
            string key = LspText.PathKey(path);
            lock (sync)
            {
                if (!documents.TryGetValue(key, out doc)) return;
                documents.Remove(key);
            }
            LanguageServerInstance inst = doc.Instance;
            if (inst != null)
            {
                inst.Enqueue(delegate
                {
                    if (inst.Endpoint != null && inst.State == LanguageServerState.Running && doc.SentText != null)
                        inst.Endpoint.SendNotification("textDocument/didClose", LspJson.Obj("textDocument", LspJson.Obj("uri", doc.Uri)));
                    doc.SentText = null;
                });
            }
            LspSymbolsStore.Clear(path);
            if (LspDiagnosticsStore.Count(path) > 0)
            {
                LspDiagnosticsStore.Clear(path);
                RaiseDiagnosticsChanged(path);
            }
        }

        private LspOpenDocument FindDoc(string path)
        {
            if (string.IsNullOrEmpty(path)) return null;
            lock (sync)
            {
                LspOpenDocument d;
                return documents.TryGetValue(LspText.PathKey(path), out d) ? d : null;
            }
        }

        private string RootFor(string path, LanguageServerDefinition def)
        {
            string ws = WorkspaceRoot;
            if (!string.IsNullOrEmpty(ws))
            {
                string wk = LspText.PathKey(ws) + "/";
                if (LspText.PathKey(path).StartsWith(wk, StringComparison.Ordinal)) return Path.GetFullPath(ws);
            }
            return LanguageServerRegistry.FindProjectRoot(path, def) ?? Path.GetDirectoryName(Path.GetFullPath(path));
        }

        private void AttachDocument(LspOpenDocument doc)
        {
            LanguageServerDefinition def = LanguageServerRegistry.DefinitionForLanguage(doc.LanguageId);
            string root = RootFor(doc.Path, def);
            string instKey = (def != null ? def.Id : doc.LanguageId) + "|" + LspText.PathKey(root);
            LanguageServerInstance inst;
            bool created = false;
            lock (sync)
            {
                if (!instances.TryGetValue(instKey, out inst))
                {
                    inst = new LanguageServerInstance();
                    inst.Key = instKey;
                    inst.Root = root;
                    inst.Definition = def;
                    inst.LanguageId = doc.LanguageId;
                    inst.State = LanguageServerState.Starting;
                    inst.Message = "Detecting language server…";
                    instances[instKey] = inst;
                    created = true;
                }
                doc.Instance = inst;
                doc.SentText = null;
            }
            if (created)
            {
                LanguageServerInstance captured = inst;
                inst.Enqueue(delegate { StartInstance(captured, true); });
                RaiseStatusChanged();
            }
            inst.Enqueue(delegate { SyncDocument(inst, doc); });
        }

        private void RetryResolve(LanguageServerInstance inst)
        {
            inst.LastResolveUtc = DateTime.UtcNow;
            inst.Enqueue(delegate
            {
                if (inst.State != LanguageServerState.NotInstalled) return;
                LanguageServerRegistry.ClearCache();
                StartInstance(inst, false);
                ReopenDocuments(inst);
            });
        }

        /// <summary>Runs on the instance chain: didOpen if needed, otherwise didChange (incremental or full).</summary>
        private void SyncDocument(LanguageServerInstance inst, LspOpenDocument doc)
        {
            if (inst.Endpoint == null || inst.State != LanguageServerState.Running || !ReferenceEquals(doc.Instance, inst)) return;
            lock (sync) { if (!documents.ContainsKey(doc.Key)) return; }
            string latest = doc.LatestText ?? "";
            if (doc.SentText == null)
            {
                doc.Version = 1;
                inst.Endpoint.SendNotification("textDocument/didOpen", LspJson.Obj("textDocument",
                    LspJson.Obj("uri", doc.Uri, "languageId", doc.LanguageId, "version", doc.Version, "text", latest)));
                doc.SentText = latest;
                return;
            }
            if (string.Equals(doc.SentText, latest, StringComparison.Ordinal)) return;
            int kind = inst.TextSyncKind;
            if (kind == 0) { doc.SentText = latest; return; }
            doc.Version++;
            List<object> changes = new List<object>();
            int sl, sc, el, ec;
            string ins;
            if (kind == 2 && LspText.ComputeIncrementalChange(doc.SentText, latest, LspText.EncodingUtf16, out sl, out sc, out el, out ec, out ins))
                changes.Add(LspJson.Obj("range", new LspRange(sl, sc, el, ec).ToJson(), "text", ins));
            else
                changes.Add(LspJson.Obj("text", latest));
            inst.Endpoint.SendNotification("textDocument/didChange", LspJson.Obj(
                "textDocument", LspJson.Obj("uri", doc.Uri, "version", doc.Version),
                "contentChanges", changes));
            doc.SentText = latest;
        }

        private void ReopenDocuments(LanguageServerInstance inst)
        {
            List<LspOpenDocument> docs;
            lock (sync) { docs = documents.Values.Where(d => ReferenceEquals(d.Instance, inst)).ToList(); }
            foreach (LspOpenDocument d in docs)
            {
                d.SentText = null;
                SyncDocument(inst, d);
            }
        }

        // ================================================================ process management

        /// <summary>Resolves, launches and initializes the server. Runs on the instance chain (blocking is fine there).</summary>
        private void StartInstance(LanguageServerInstance inst, bool raiseHint)
        {
            if (disposed || inst.ShuttingDown) return;
            int gen = Interlocked.Increment(ref inst.Generation);
            // One process per instance: a previous process that is still alive (double restart, crash-restart race) is
            // killed before a new one starts - otherwise it would be orphaned until codeLess exits.
            Process previous = inst.Process;
            LspRpcEndpoint previousEp = inst.Endpoint;
            if (previous != null || previousEp != null)
            {
                inst.Endpoint = null;
                inst.Process = null;
                if (previousEp != null) { try { previousEp.Dispose(); } catch { } }
                KillProcessTree(previous);
            }
            inst.State = LanguageServerState.Starting;
            inst.ProgressText = null;
            LanguageServerUserSettings user = null;
            try { if (UserSettingsProvider != null) user = UserSettingsProvider(); } catch { }
            LanguageServerResolution res = LanguageServerRegistry.Resolve(inst.LanguageId, inst.Root, user, ManagedServersRoot);
            inst.Resolution = res;
            inst.LastResolveUtc = DateTime.UtcNow;
            if (!res.CanStart && !res.Disabled && !res.Restricted && !res.FromUserOverride && res.Definition != null && TryAutoInstall(inst, res))
                return;
            if (!res.CanStart)
            {
                inst.State = res.Disabled ? LanguageServerState.Disabled : res.Restricted ? LanguageServerState.Restricted : LanguageServerState.NotInstalled;
                inst.Message = res.Problem;
                inst.AppendLog("[client] " + res.Problem + (res.InstallCommand != null ? " Install with: " + res.InstallCommand : ""));
                RaiseStatusChanged();
                if (!res.Disabled && !res.Restricted && raiseHint) MaybeRaiseHint(inst, res);
                return;
            }

            inst.Message = "Starting " + res.DisplayName + "…";
            RaiseStatusChanged();
            Process proc = null;
            try
            {
                ProcessStartInfo psi = LanguageServerRegistry.BuildStartInfo(res.Executable, res.Args, inst.Root);
                inst.AppendLog("[client] Starting " + res.Executable + " " + LanguageServerRegistry.JoinArgs(res.Args) + "  (root: " + inst.Root + ")");
                proc = new Process();
                proc.StartInfo = psi;
                proc.EnableRaisingEvents = true;
                proc.Start();
                inst.Process = proc;
                inst.ProcessId = proc.Id;
                proc.ErrorDataReceived += delegate (object s, DataReceivedEventArgs e) { if (e.Data != null) inst.AppendLog("[stderr] " + e.Data); };
                proc.BeginErrorReadLine();
                Process capturedProc = proc;
                proc.Exited += delegate { OnProcessExited(inst, capturedProc, gen); };
                if (inst.Generation != gen || inst.ShuttingDown || disposed)
                {
                    AbandonStart(inst, proc, null);
                    return;
                }
            }
            catch (Exception ex)
            {
                inst.AppendLog("[client] Failed to start: " + ex.Message);
                KillProcessTree(proc);
                HandleCrash(inst, gen, "Failed to start " + res.DisplayName + ": " + ex.Message);
                return;
            }

            LspJsonRpcConnection conn = new LspJsonRpcConnection(new BufferedStream(proc.StandardOutput.BaseStream, 64 * 1024), proc.StandardInput.BaseStream);
            LspRpcEndpoint ep = new LspRpcEndpoint(conn);
            ep.Trace = delegate (string t) { inst.AppendLog(t); };
            ep.NotificationReceived += delegate (string method, object prms) { OnNotification(inst, method, prms); };
            ep.RequestHandler = delegate (string method, object prms) { return OnServerRequest(inst, method, prms); };
            Process epProc = proc;
            ep.Closed += delegate (string reason)
            {
                if (inst.Generation == gen && !inst.ShuttingDown)
                {
                    inst.AppendLog("[client] connection closed: " + reason);
                    try
                    {
                        if (epProc != null && epProc.WaitForExit(500))
                            OnProcessExited(inst, epProc, gen);
                    }
                    catch { }
                }
            };
            inst.Endpoint = ep;
            ep.Start();

            try
            {
                Task<object> init = ep.SendRequestAsync("initialize", BuildInitializeParams(inst), CancellationToken.None, InitializeTimeoutMs);
                init.Wait();
                Dictionary<string, object> caps = LspJson.GetObject(init.Result, "capabilities") ?? new Dictionary<string, object>(StringComparer.Ordinal);
                inst.ServerCapabilities = caps;
                object serverInfo = LspJson.Get(init.Result, "serverInfo");
                string sname = LspJson.GetString(serverInfo, "name");
                if (!string.IsNullOrEmpty(sname)) inst.AppendLog("[client] Server: " + sname + " " + (LspJson.GetString(serverInfo, "version") ?? ""));
                ep.SendNotification("initialized", new Dictionary<string, object>());
                SendConfiguration(inst);
            }
            catch (Exception ex)
            {
                Exception inner = ex is AggregateException && ex.InnerException != null ? ex.InnerException : ex;
                if (inst.Generation != gen || inst.ShuttingDown || disposed) { AbandonStart(inst, proc, ep); return; }
                inst.AppendLog("[client] initialize failed: " + inner.Message);
                KillProcessTree(proc);
                HandleCrash(inst, gen, "initialize failed: " + inner.Message);
                return;
            }
            if (inst.Generation != gen || inst.ShuttingDown || disposed) { AbandonStart(inst, proc, ep); return; }
            inst.State = LanguageServerState.Running;
            inst.Message = res.DisplayName + " is running (pid " + inst.ProcessId + ").";
            inst.AppendLog("[client] " + inst.Message);
            RaiseStatusChanged();
        }

        /// <summary>
        /// A restart / workspace switch / shutdown happened while this start was in progress: the process it launched is no
        /// longer tracked by anyone, so kill it here (LSP-4) instead of leaking it until codeLess exits.
        /// </summary>
        private static void AbandonStart(LanguageServerInstance inst, Process proc, LspRpcEndpoint ep)
        {
            if (ep != null && ReferenceEquals(inst.Endpoint, ep)) inst.Endpoint = null;
            if (proc != null && ReferenceEquals(inst.Process, proc)) inst.Process = null;
            int pid = 0;
            try { pid = proc != null ? proc.Id : 0; } catch { }
            if (ep != null) { try { ep.Dispose(); } catch { } }
            KillProcessTree(proc);
            inst.AppendLog("[client] Discarded server process " + pid + " (restarted or stopped while it was starting)");
        }

        private void SendConfiguration(LanguageServerInstance inst)
        {
            Dictionary<string, object> settings = new Dictionary<string, object>(StringComparer.Ordinal);
            string venvPy = LanguageServerRegistry.FindWorkspaceVenvPython(inst.Root);
            if (inst.Definition != null && inst.Definition.Id == "python" && venvPy != null)
                settings["python"] = LspJson.Obj("pythonPath", venvPy);
            inst.Endpoint.SendNotification("workspace/didChangeConfiguration", LspJson.Obj("settings", settings));
        }

        private Dictionary<string, object> BuildInitializeParams(LanguageServerInstance inst)
        {
            string rootUri = LspText.PathToUri(inst.Root);
            string name = Path.GetFileName(inst.Root.TrimEnd('/', '\\'));
            if (string.IsNullOrEmpty(name)) name = inst.Root;
            int pid = 0;
            try { pid = Process.GetCurrentProcess().Id; } catch { }
            Dictionary<string, object> initOptions = new Dictionary<string, object>(StringComparer.Ordinal);
            if (inst.Definition != null && (inst.Definition.Id == "json" || inst.Definition.Id == "html" || inst.Definition.Id == "css"))
                initOptions["provideFormatter"] = true;
            if (inst.Resolution != null && inst.Resolution.InitializationOptions != null)
                foreach (KeyValuePair<string, object> kv in inst.Resolution.InitializationOptions) initOptions[kv.Key] = kv.Value;
            return LspJson.Obj(
                "processId", pid,
                "clientInfo", LspJson.Obj("name", "codeLess", "version", ClientVersion),
                "locale", "en",
                "rootPath", inst.Root,
                "rootUri", rootUri,
                "workspaceFolders", LspJson.Arr(LspJson.Obj("uri", rootUri, "name", name)),
                "initializationOptions", initOptions,
                "trace", "off",
                "capabilities", BuildClientCapabilities());
        }

        public static Dictionary<string, object> BuildClientCapabilities()
        {
            List<object> completionKinds = new List<object>();
            for (int i = 1; i <= 25; i++) completionKinds.Add(i);
            List<object> symbolKinds = new List<object>();
            for (int i = 1; i <= 26; i++) symbolKinds.Add(i);
            List<object> mdPlain = LspJson.Arr("markdown", "plaintext");
            return LspJson.Obj(
                "general", LspJson.Obj("positionEncodings", LspJson.Arr(LspText.EncodingUtf16)),
                "workspace", LspJson.Obj(
                    "applyEdit", true,
                    "workspaceEdit", LspJson.Obj("documentChanges", true, "resourceOperations", LspJson.Arr("create", "rename", "delete"), "failureHandling", "textOnlyTransactional"),
                    "didChangeConfiguration", LspJson.Obj("dynamicRegistration", true),
                    "didChangeWatchedFiles", LspJson.Obj("dynamicRegistration", true, "relativePatternSupport", true),
                    "executeCommand", LspJson.Obj("dynamicRegistration", false),
                    "configuration", true,
                    "workspaceFolders", true),
                "textDocument", LspJson.Obj(
                    "synchronization", LspJson.Obj("dynamicRegistration", false, "willSave", false, "willSaveWaitUntil", false, "didSave", true),
                    "completion", LspJson.Obj(
                        "dynamicRegistration", false,
                        "contextSupport", true,
                        "completionItem", LspJson.Obj(
                            "snippetSupport", true,
                            "commitCharactersSupport", true,
                            "documentationFormat", mdPlain,
                            "deprecatedSupport", true,
                            "preselectSupport", true,
                            "insertReplaceSupport", true,
                            "labelDetailsSupport", true,
                            "tagSupport", LspJson.Obj("valueSet", LspJson.Arr(1)),
                            "resolveSupport", LspJson.Obj("properties", LspJson.Arr("documentation", "detail", "additionalTextEdits")),
                            "insertTextModeSupport", LspJson.Obj("valueSet", LspJson.Arr(1, 2))),
                        "completionItemKind", LspJson.Obj("valueSet", completionKinds),
                        "completionList", LspJson.Obj("itemDefaults", LspJson.Arr("commitCharacters", "editRange", "insertTextFormat", "insertTextMode", "data"))),
                    "hover", LspJson.Obj("dynamicRegistration", false, "contentFormat", mdPlain),
                    "signatureHelp", LspJson.Obj(
                        "dynamicRegistration", false,
                        "contextSupport", true,
                        "signatureInformation", LspJson.Obj(
                            "documentationFormat", mdPlain,
                            "parameterInformation", LspJson.Obj("labelOffsetSupport", true),
                            "activeParameterSupport", true)),
                    "definition", LspJson.Obj("linkSupport", true),
                    "declaration", LspJson.Obj("linkSupport", true),
                    "typeDefinition", LspJson.Obj("linkSupport", true),
                    "implementation", LspJson.Obj("linkSupport", true),
                    "references", LspJson.Obj("dynamicRegistration", false),
                    "documentSymbol", LspJson.Obj("hierarchicalDocumentSymbolSupport", true, "symbolKind", LspJson.Obj("valueSet", symbolKinds)),
                    "formatting", LspJson.Obj("dynamicRegistration", false),
                    "rangeFormatting", LspJson.Obj("dynamicRegistration", false),
                    "rename", LspJson.Obj("dynamicRegistration", false, "prepareSupport", true),
                    "codeAction", LspJson.Obj(
                        "dynamicRegistration", false,
                        "isPreferredSupport", true,
                        "disabledSupport", true,
                        "dataSupport", true,
                        "resolveSupport", LspJson.Obj("properties", LspJson.Arr("edit")),
                        "codeActionLiteralSupport", LspJson.Obj("codeActionKind", LspJson.Obj("valueSet", LspJson.Arr(
                            "", "quickfix", "refactor", "refactor.extract", "refactor.inline", "refactor.rewrite", "source", "source.organizeImports", "source.fixAll")))),
                    "publishDiagnostics", LspJson.Obj("relatedInformation", true, "versionSupport", true, "codeDescriptionSupport", true, "dataSupport", true,
                        "tagSupport", LspJson.Obj("valueSet", LspJson.Arr(1, 2)))),
                "window", LspJson.Obj(
                    "workDoneProgress", true,
                    "showMessage", LspJson.Obj("messageActionItem", LspJson.Obj("additionalPropertiesSupport", false)),
                    "showDocument", LspJson.Obj("support", false)));
        }

        private void OnProcessExited(LanguageServerInstance inst, Process proc, int gen)
        {
            int code = -1;
            try { code = proc.ExitCode; } catch { }
            if (inst.ShuttingDown || inst.Generation != gen) return;
            inst.AppendLog("[client] Server process exited with code " + code);
            HandleCrash(inst, gen, inst.DisplayName + " exited unexpectedly (code " + code + ").");
        }

        private void HandleCrash(LanguageServerInstance inst, int gen, string message)
        {
            lock (inst.CrashTimes)
            {
                if (inst.Generation != gen || inst.ShuttingDown) return;
                Interlocked.Increment(ref inst.Generation); // ignore late events of this process
                inst.State = LanguageServerState.Error;
                inst.Message = message;
                LspRpcEndpoint ep = inst.Endpoint;
                inst.Endpoint = null;
                if (ep != null) { try { ep.Dispose(); } catch { } }
                DateTime now = DateTime.UtcNow;
                inst.CrashTimes.RemoveAll(t => (now - t).TotalSeconds > RestartWindowSeconds);
                inst.CrashTimes.Add(now);
                int crashes = inst.CrashTimes.Count;
                ClearDiagnosticsFor(new List<LanguageServerInstance> { inst });
                if (crashes > MaxRestarts)
                {
                    inst.Message = message + " Not restarting after " + MaxRestarts + " restarts; use Restart Language Server.";
                    inst.AppendLog("[client] " + inst.Message);
                    RaiseStatusChanged();
                    return;
                }
                int delay = RestartBackoffMs * (1 << Math.Min(6, crashes - 1));
                inst.AppendLog("[client] Restarting in " + delay + " ms (attempt " + crashes + "/" + MaxRestarts + ")");
                RaiseStatusChanged();
                inst.Enqueue(delegate
                {
                    Thread.Sleep(delay);
                    if (inst.ShuttingDown || disposed) return;
                    lock (sync) { if (!instances.ContainsKey(inst.Key)) return; }
                    inst.RestartCount++;
                    StartInstance(inst, false);
                    ReopenDocuments(inst);
                });
            }
        }

        /// <summary>
        /// Workspace Trust changed: servers whose root changed between trusted and Restricted Mode are re-resolved and
        /// restarted (trusting enables workspace-local / project-executing servers at once; revoking stops them).
        /// </summary>
        public void OnWorkspaceTrustChanged()
        {
            LanguageServerRegistry.ClearCache();
            foreach (LanguageServerInstance inst in GetInstances())
            {
                bool trustedNow = WorkspaceTrust.IsTrusted(inst.Root);
                bool trustedThen = inst.Resolution == null || inst.Resolution.WorkspaceTrusted;
                if (trustedNow == trustedThen) continue;
                inst.AppendLog("[client] Workspace Trust changed (" + (trustedNow ? "trusted" : "Restricted Mode") + "): restarting");
                RestartInstance(inst);
            }
        }

        /// <summary>Restarts the server serving <paramref name="path"/> (also re-detects a newly installed one).</summary>
        public void RestartServer(string path)
        {
            LanguageServerInstance inst = GetInstanceForPath(path);
            if (inst == null) return;
            RestartInstance(inst);
        }

        public void RestartInstance(LanguageServerInstance inst)
        {
            inst.AppendLog("[client] Restart requested");
            inst.State = LanguageServerState.Starting;
            inst.Message = "Restarting…";
            RaiseStatusChanged();
            lock (inst.CrashTimes) { inst.CrashTimes.Clear(); }
            Task.Factory.StartNew(delegate
            {
                inst.ShuttingDown = true;
                StopInstance(inst, 1500);
                inst.ShuttingDown = false;
                LanguageServerRegistry.ClearCache();
                inst.Enqueue(delegate
                {
                    StartInstance(inst, false);
                    ReopenDocuments(inst);
                });
            }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        }

        /// <summary>shutdown + exit, then kill the process tree. Blocking (call off the UI thread, or at exit).</summary>
        private void StopInstance(LanguageServerInstance inst, int timeoutMs)
        {
            inst.ShuttingDown = true;
            Interlocked.Increment(ref inst.Generation);
            LspRpcEndpoint ep = inst.Endpoint;
            Process proc = inst.Process;
            inst.Endpoint = null;
            if (ep != null && !ep.IsClosed && inst.State == LanguageServerState.Running)
            {
                try
                {
                    Task<object> t = ep.SendRequestAsync("shutdown", null, CancellationToken.None, Math.Max(100, timeoutMs / 2));
                    t.Wait(Math.Max(100, timeoutMs / 2));
                }
                catch { }
                try
                {
                    ep.SendNotification("exit", null);
                    ep.DrainOutbox(200);
                }
                catch { }
            }
            try
            {
                if (proc != null && !proc.HasExited) proc.WaitForExit(Math.Max(50, timeoutMs / 3));
            }
            catch { }
            KillProcessTree(proc);
            if (ep != null) { try { ep.Dispose(); } catch { } }
            inst.State = LanguageServerState.Stopped;
            inst.AppendLog("[client] Server stopped");
        }

        /// <summary>Kills a process and its children (taskkill /T on Windows, Process.Kill(true) / pkill on Unix).</summary>
        public static void KillProcessTree(Process proc)
        {
            if (proc == null) return;
            try { if (proc.HasExited) return; } catch { return; }
            int pid = 0;
            try { pid = proc.Id; } catch { }
            try
            {
                if (LspText.IsWindows())
                {
                    ProcessStartInfo psi = new ProcessStartInfo("taskkill", "/PID " + pid + " /T /F");
                    psi.UseShellExecute = false;
                    psi.CreateNoWindow = true;
                    using (Process k = Process.Start(psi)) { k.WaitForExit(3000); }
                }
                else
                {
                    MethodInfo killTree = typeof(Process).GetMethod("Kill", new Type[] { typeof(bool) });
                    if (killTree != null) killTree.Invoke(proc, new object[] { true });
                    else
                    {
                        ProcessStartInfo psi = new ProcessStartInfo("pkill", "-KILL -P " + pid);
                        psi.UseShellExecute = false;
                        psi.CreateNoWindow = true;
                        using (Process k = Process.Start(psi)) { k.WaitForExit(2000); }
                    }
                }
            }
            catch { }
            try { if (!proc.HasExited) proc.Kill(); } catch { }
        }

        private void MaybeRaiseHint(LanguageServerInstance inst, LanguageServerResolution res)
        {
            LanguageServerDefinition d = res.Definition;
            string lang = d != null ? d.Id : inst.LanguageId;
            lock (sync)
            {
                if (hintedLanguages.Contains(lang)) return;
                hintedLanguages.Add(lang);
            }
            try { if (IsHintSuppressed != null && (IsHintSuppressed(lang) || IsHintSuppressed(inst.LanguageId))) return; } catch { }
            LanguageServerHint h = new LanguageServerHint();
            h.LanguageId = lang;
            h.LanguageName = d != null ? d.DisplayName : inst.LanguageId;
            h.ServerName = res.DisplayName;
            h.InstallCommand = res.InstallCommand;
            h.InstallUrl = d != null ? d.InstallUrl : null;
            h.Reason = res.AutoInstallBlockedReason;
            string why = string.IsNullOrEmpty(h.Reason) ? "" : " — " + h.Reason.TrimEnd('.');
            h.Message = "No " + h.LanguageName + " language server found" + why +
                        (h.InstallCommand != null ? " — install one with: " + h.InstallCommand : (h.InstallUrl != null ? " — see " + h.InstallUrl : "."));
            Post(delegate { Action<LanguageServerHint> e = ServerMissing; if (e != null) e(h); });
        }

        // ================================================================ automatic installs

        private readonly HashSet<LanguageServerInstallJob> ownJobs = new HashSet<LanguageServerInstallJob>();

        /// <summary>Runs on the instance chain: starts (or joins) an automatic install when policy allows. True = installing.</summary>
        private bool TryAutoInstall(LanguageServerInstance inst, LanguageServerResolution res)
        {
            LanguageServerInstaller installer = Installer;
            if (installer == null || disposed) return false;
            bool auto = false;
            try { auto = AutoInstallEnabled != null && AutoInstallEnabled(); } catch { }
            if (!auto) return false;
            string why;
            if (!installer.ShouldAutoInstall(res.Definition, out why))
            {
                res.AutoInstallBlockedReason = why;
                inst.AppendLog("[client] Not installed automatically: " + why);
                return false;
            }
            inst.State = LanguageServerState.Installing;
            BeginInstall(res.Definition, false);
            return true;
        }

        /// <summary>Installs (or updates) the server family serving <paramref name="path"/> now, ignoring the failure backoff.</summary>
        public LanguageServerInstallJob InstallServerForPath(string path)
        {
            LanguageServerInstance inst = GetInstanceForPath(path);
            LanguageServerDefinition d = inst != null ? inst.Definition : LanguageServerRegistry.DefinitionForLanguage(LanguageServerRegistry.LanguageIdForPath(path));
            return d != null ? InstallServer(d.Id) : null;
        }

        /// <summary>Installs (or updates) a server family by id ("typescript", "python"...): the "Install/Update…" command.</summary>
        public LanguageServerInstallJob InstallServer(string serverId)
        {
            LanguageServerDefinition d = LanguageServerRegistry.DefinitionById(serverId);
            if (d == null || Installer == null || disposed) return null;
            return BeginInstall(d, true);
        }

        /// <summary>Cancels the running install of a server family (no-op when none).</summary>
        public void CancelInstall(string serverId)
        {
            LanguageServerDefinition d = LanguageServerRegistry.DefinitionById(serverId);
            LanguageServerInstaller installer = Installer;
            if (d == null || installer == null) return;
            LanguageServerInstallJob j = installer.GetActiveJob(d);
            if (j != null) j.Cancel();
        }

        public List<LanguageServerInstallJob> GetActiveInstallJobs()
        {
            LanguageServerInstaller installer = Installer;
            return installer != null ? installer.GetActiveJobs() : new List<LanguageServerInstallJob>();
        }

        private List<LanguageServerInstance> InstancesOf(LanguageServerDefinition d)
        {
            lock (sync) { return instances.Values.Where(i => i.Definition != null && string.Equals(i.Definition.Id, d.Id, StringComparison.OrdinalIgnoreCase)).ToList(); }
        }

        private LanguageServerInstallJob BeginInstall(LanguageServerDefinition d, bool userInitiated)
        {
            LanguageServerInstaller installer = Installer;
            if (installer == null) return null;
            LanguageServerInstallJob job = installer.Start(d, userInitiated);
            bool subscribe;
            lock (sync)
            {
                subscribe = !ownJobs.Contains(job);
                if (subscribe) ownJobs.Add(job);
            }
            foreach (LanguageServerInstance i in InstancesOf(d))
            {
                // an update keeps a running server until the new version is in place
                if (i.State != LanguageServerState.NotInstalled && i.State != LanguageServerState.Installing && i.State != LanguageServerState.Error) continue;
                i.State = LanguageServerState.Installing;
                i.Message = job.StatusText;
            }
            RaiseStatusChanged();
            if (!subscribe) return job;
            Action<string> onOutput = delegate (string line)
            {
                foreach (LanguageServerInstance i in InstancesOf(d)) i.AppendLog("[install] " + line);
                Post(delegate { Action<string> e = InstallOutput; if (e != null) e(line); });
            };
            job.AddOutputListener(onOutput); // replays the lines logged before this subscription
            job.Task.ContinueWith(delegate (Task<LanguageServerInstallResult> t)
            {
                job.RemoveOutputListener(onOutput);
                lock (sync) { ownJobs.Remove(job); }
                if (disposed) return;
                OnInstallFinished(d, t.Result, userInitiated);
            }, TaskScheduler.Default);
            return job;
        }

        private void OnInstallFinished(LanguageServerDefinition d, LanguageServerInstallResult r, bool userInitiated)
        {
            LanguageServerRegistry.ClearCache();
            foreach (LanguageServerInstance inst in InstancesOf(d))
            {
                if (r.Success)
                {
                    if (inst.State == LanguageServerState.Running) { RestartInstance(inst); continue; } // updated: pick up the new version
                    inst.State = LanguageServerState.NotInstalled;
                    RetryResolve(inst); // starts the server and re-sends the open documents
                }
                else if (inst.State == LanguageServerState.Installing)
                {
                    inst.State = LanguageServerState.NotInstalled;
                    inst.Message = r.Message;
                    if (inst.Resolution != null && !string.IsNullOrEmpty(r.Command)) inst.Resolution.InstallCommand = r.Command;
                }
            }
            RaiseStatusChanged();
            Post(delegate { Action<LanguageServerInstallResult> e = InstallFinished; if (e != null) e(r); });
            if (r.Success || r.Cancelled) return;
            if (!userInitiated)
            {
                try { if (IsHintSuppressed != null && IsHintSuppressed(d.Id)) return; } catch { }
            }
            LanguageServerHint h = new LanguageServerHint();
            h.LanguageId = d.Id;
            h.LanguageName = d.DisplayName;
            h.ServerName = d.Candidates.Count > 0 ? d.Candidates[0].DisplayName : d.DisplayName;
            h.InstallCommand = !string.IsNullOrEmpty(r.Command) ? r.Command : LanguageServerRegistry.GetInstallCommand(d, WorkspaceRoot);
            h.InstallUrl = d.InstallUrl;
            h.InstallFailed = !r.BlockedOffline;
            h.Reason = r.Message;
            h.Message = r.Message ?? ("Couldn't install the " + d.DisplayName + " language server.");
            lock (sync) { hintedLanguages.Add(d.Id); }
            Post(delegate { Action<LanguageServerHint> e = ServerMissing; if (e != null) e(h); });
        }


        // ================================================================ server -> client

        private void OnNotification(LanguageServerInstance inst, string method, object prms)
        {
            switch (method)
            {
                case "textDocument/publishDiagnostics":
                    OnPublishDiagnostics(inst, prms);
                    break;
                case "window/logMessage":
                    inst.AppendLog("[log] " + (LspJson.GetString(prms, "message") ?? ""));
                    break;
                case "window/showMessage":
                    {
                        int type = LspJson.GetInt(prms, "type", 3);
                        string msg = LspJson.GetString(prms, "message") ?? "";
                        inst.AppendLog("[message] " + msg);
                        string sev = type == 1 ? "error" : (type == 2 ? "warning" : "info");
                        if (type <= 2) Post(delegate { Action<string, string> e = ServerMessage; if (e != null) e(sev, inst.DisplayName + ": " + msg); });
                        break;
                    }
                case "$/progress":
                    OnProgress(inst, prms);
                    break;
                default:
                    break;
            }
        }

        private void OnProgress(LanguageServerInstance inst, object prms)
        {
            string token = LspJson.GetString(prms, "token") ?? "";
            object v = LspJson.Get(prms, "value");
            string kind = LspJson.GetString(v, "kind");
            string text = null;
            lock (inst.ProgressTitles)
            {
                if (kind == "begin") inst.ProgressTitles[token] = LspJson.GetString(v, "title") ?? "";
                if (kind == "end") inst.ProgressTitles.Remove(token);
                if (kind == "begin" || kind == "report")
                {
                    string title;
                    inst.ProgressTitles.TryGetValue(token, out title);
                    string message = LspJson.GetString(v, "message");
                    int pct = LspJson.GetInt(v, "percentage", -1);
                    text = (title ?? "") + (string.IsNullOrEmpty(message) ? "" : " " + message) + (pct >= 0 ? " " + pct + "%" : "");
                    text = text.Trim();
                    if (text.Length > 40) text = text.Substring(0, 40) + "…";
                }
                else if (inst.ProgressTitles.Count > 0) text = inst.ProgressTitles.Values.First();
            }
            if (!string.Equals(text, inst.ProgressText, StringComparison.Ordinal))
            {
                inst.ProgressText = string.IsNullOrEmpty(text) ? null : text;
                RaiseStatusChanged();
            }
        }

        private void OnPublishDiagnostics(LanguageServerInstance inst, object prms)
        {
            string uri = LspJson.GetString(prms, "uri");
            string path = LspText.UriToPath(uri);
            if (path == null) return;
            LspOpenDocument doc = FindDoc(path);
            // versionSupport is advertised: diagnostics computed for an older version than the one the server already got
            // would be drawn at stale positions; a fresh publish for the current version follows.
            int diagVersion = LspJson.GetInt(prms, "version", -1);
            if (doc != null && doc.SentText != null && diagVersion >= 0 && diagVersion < doc.Version)
            {
                inst.AppendLog("[client] dropped diagnostics for v" + diagVersion + " (document is at v" + doc.Version + ")");
                return;
            }
            string text = doc != null ? (doc.SentText ?? doc.LatestText) : null;
            List<int> starts = text != null ? LspText.LineStarts(text) : null;
            List<LspDiagnostic> diags = LspDiagnostic.ParseMany(LspJson.Get(prms, "diagnostics"));
            List<DiagnosticItem> items = new List<DiagnosticItem>();
            foreach (LspDiagnostic g in diags)
            {
                DiagnosticItem it = new DiagnosticItem();
                it.IsLanguageServer = true;
                it.Severity = LspDiagnostic.SeverityName(g.Severity);
                it.FilePath = doc != null ? doc.Path : path;
                it.RelativePath = Path.GetFileName(path);
                it.Line = g.Range.StartLine + 1;
                it.Column = g.Range.StartCharacter + 1;
                it.EndLine = g.Range.EndLine + 1;
                it.EndColumn = g.Range.EndCharacter + 1;
                int len;
                if (g.Range.EndLine == g.Range.StartLine) len = g.Range.EndCharacter - g.Range.StartCharacter;
                else
                {
                    string lt = starts != null ? LspText.LineText(text, starts, g.Range.StartLine) : "";
                    len = Math.Max(1, lt.Length - g.Range.StartCharacter);
                    it.EndLine = it.Line;
                    it.EndColumn = it.Column + len;
                }
                it.Length = Math.Max(1, len);
                it.Message = g.Message;
                it.Code = g.Code;
                it.Source = !string.IsNullOrEmpty(g.Source) ? g.Source : inst.DisplayName;
                it.LspRaw = g.Raw;
                items.Add(it);
            }
            LspDiagnosticsStore.Set(path, items);
            RaiseDiagnosticsChanged(path);
        }

        private void RaiseDiagnosticsChanged(string path)
        {
            Post(delegate { Action<string> e = DiagnosticsChanged; if (e != null) e(path); });
        }

        private void ClearDiagnosticsFor(List<LanguageServerInstance> insts)
        {
            if (insts == null || insts.Count == 0) return;
            List<LspOpenDocument> docs;
            lock (sync) { docs = documents.Values.ToList(); }
            HashSet<LanguageServerInstance> set = new HashSet<LanguageServerInstance>(insts);
            foreach (LspOpenDocument d in docs)
            {
                if (d.Instance == null || set.Contains(d.Instance))
                {
                    LspSymbolsStore.Clear(d.Path);
                    if (LspDiagnosticsStore.Count(d.Path) > 0)
                    {
                        LspDiagnosticsStore.Clear(d.Path);
                        RaiseDiagnosticsChanged(d.Path);
                    }
                }
            }
        }

        private object OnServerRequest(LanguageServerInstance inst, string method, object prms)
        {
            switch (method)
            {
                case "workspace/configuration":
                    {
                        List<object> items = LspJson.GetList(prms, "items") ?? new List<object>();
                        List<object> result = new List<object>();
                        string venvPy = LanguageServerRegistry.FindWorkspaceVenvPython(inst.Root);
                        foreach (object it in items)
                        {
                            string section = LspJson.GetString(it, "section") ?? "";
                            if (section == "python" && venvPy != null) result.Add(LspJson.Obj("pythonPath", venvPy));
                            else result.Add(new Dictionary<string, object>(StringComparer.Ordinal));
                        }
                        return result;
                    }
                case "window/workDoneProgress/create":
                    return null;
                case "client/registerCapability":
                    {
                        List<object> regs = LspJson.GetList(prms, "registrations");
                        if (regs != null)
                            foreach (object r in regs)
                            {
                                string m = LspJson.GetString(r, "method");
                                string cap = CapabilityForMethod(m);
                                if (cap != null) inst.DynamicRegistrations.Add(cap);
                                if (m == "workspace/didChangeWatchedFiles")
                                {
                                    List<LspFileWatcher> ws = LspFileWatcher.ParseMany(LspJson.Get(r, "registerOptions"), inst.Root);
                                    lock (inst.FileWatchers) { inst.FileWatchers[LspJson.GetString(r, "id") ?? Guid.NewGuid().ToString("N")] = ws; }
                                }
                                inst.AppendLog("[client] registered " + m);
                            }
                        return null;
                    }
                case "client/unregisterCapability":
                    {
                        List<object> regs = LspJson.GetList(prms, "unregisterations") ?? LspJson.GetList(prms, "unregistrations");
                        if (regs != null)
                            foreach (object r in regs)
                            {
                                string cap = CapabilityForMethod(LspJson.GetString(r, "method"));
                                if (cap != null) inst.DynamicRegistrations.Remove(cap);
                                string id = LspJson.GetString(r, "id");
                                if (id != null) lock (inst.FileWatchers) { inst.FileWatchers.Remove(id); }
                            }
                        return null;
                    }
                case "window/showMessageRequest":
                    {
                        string msg = LspJson.GetString(prms, "message") ?? "";
                        int type = LspJson.GetInt(prms, "type", 3);
                        inst.AppendLog("[message] " + msg);
                        Post(delegate { Action<string, string> e = ServerMessage; if (e != null) e(type == 1 ? "error" : (type == 2 ? "warning" : "info"), inst.DisplayName + ": " + msg); });
                        return null;
                    }
                case "window/showDocument":
                    return LspJson.Obj("success", false);
                case "workspace/workspaceFolders":
                    {
                        string name = Path.GetFileName(inst.Root.TrimEnd('/', '\\'));
                        return LspJson.Arr(LspJson.Obj("uri", LspText.PathToUri(inst.Root), "name", string.IsNullOrEmpty(name) ? inst.Root : name));
                    }
                case "workspace/applyEdit":
                    {
                        LspWorkspaceEdit we = LspWorkspaceEdit.Parse(LspJson.Get(prms, "edit"));
                        bool applied = false;
                        Func<LspWorkspaceEdit, bool> h = ApplyWorkspaceEditHandler;
                        if (h != null)
                        {
                            ManualResetEvent done = new ManualResetEvent(false);
                            Post(delegate
                            {
                                try { applied = h(we); }
                                finally { done.Set(); }
                            });
                            done.WaitOne(15000);
                        }
                        return LspJson.Obj("applied", applied);
                    }
                case "workspace/semanticTokens/refresh":
                case "workspace/inlayHint/refresh":
                case "workspace/codeLens/refresh":
                case "workspace/diagnostic/refresh":
                case "workspace/inlineValue/refresh":
                    return null;
                default:
                    throw new LspRequestException(LspRequestException.MethodNotFound, "Method not supported by codeLess: " + method);
            }
        }

        private static string CapabilityForMethod(string method)
        {
            switch (method)
            {
                case "textDocument/completion": return "completionProvider";
                case "textDocument/hover": return "hoverProvider";
                case "textDocument/signatureHelp": return "signatureHelpProvider";
                case "textDocument/definition": return "definitionProvider";
                case "textDocument/typeDefinition": return "typeDefinitionProvider";
                case "textDocument/implementation": return "implementationProvider";
                case "textDocument/declaration": return "declarationProvider";
                case "textDocument/references": return "referencesProvider";
                case "textDocument/rename": return "renameProvider";
                case "textDocument/formatting": return "documentFormattingProvider";
                case "textDocument/rangeFormatting": return "documentRangeFormattingProvider";
                case "textDocument/documentSymbol": return "documentSymbolProvider";
                case "textDocument/codeAction": return "codeActionProvider";
                default: return null;
            }
        }

        // ================================================================ watched files

        private readonly object watchLock = new object();
        private readonly List<KeyValuePair<string, int>> pendingFileEvents = new List<KeyValuePair<string, int>>();
        private Timer watchTimer;
        public int WatchedFilesDebounceMs = 200;

        /// <summary>LSP FileChangeType: 1 = created, 2 = changed, 3 = deleted.</summary>
        public const int FileCreated = 1, FileChanged = 2, FileDeleted = 3;

        /// <summary>
        /// Feeds a file system event (from the workspace watcher, any thread) to servers that registered matching
        /// workspace/didChangeWatchedFiles watchers. Batched and debounced; .git internals and node_modules packages are
        /// skipped like VS Code's files.watcherExclude defaults.
        /// </summary>
        public void NotifyFileSystemChange(string path, int changeType)
        {
            if (disposed || string.IsNullOrEmpty(path) || changeType < 1 || changeType > 3) return;
            string norm = path.Replace('\\', '/');
            if (norm.Contains("/.git/") || norm.EndsWith("/.git", StringComparison.Ordinal)) return;
            int nm = norm.IndexOf("/node_modules/", StringComparison.Ordinal);
            if (nm >= 0 && norm.IndexOf('/', nm + "/node_modules/".Length) >= 0) return;
            lock (watchLock)
            {
                if (pendingFileEvents.Count >= 10000) return;
                pendingFileEvents.Add(new KeyValuePair<string, int>(path, changeType));
                if (watchTimer == null) watchTimer = new Timer(delegate { FlushFileEvents(); }, null, Timeout.Infinite, Timeout.Infinite);
                watchTimer.Change(Math.Max(1, WatchedFilesDebounceMs), Timeout.Infinite);
            }
        }

        /// <summary>Sends the queued file events now (also called by the debounce timer).</summary>
        public void FlushFileEvents()
        {
            List<KeyValuePair<string, int>> events;
            lock (watchLock)
            {
                if (pendingFileEvents.Count == 0) return;
                events = new List<KeyValuePair<string, int>>(pendingFileEvents);
                pendingFileEvents.Clear();
            }
            // Collapse per path: created+changed = created, created+deleted = nothing, deleted+created = changed.
            Dictionary<string, int> last = new Dictionary<string, int>(StringComparer.Ordinal);
            List<string> order = new List<string>();
            foreach (KeyValuePair<string, int> e in events)
            {
                string key = LspText.PathKey(e.Key);
                int prev;
                if (!last.TryGetValue(key, out prev)) { last[key] = e.Value; order.Add(e.Key); continue; }
                int merged = e.Value;
                if (prev == FileCreated && e.Value == FileChanged) merged = FileCreated;
                else if (prev == FileCreated && e.Value == FileDeleted) merged = 0;
                else if (prev == FileDeleted && e.Value == FileCreated) merged = FileChanged;
                else if (prev == 0 && e.Value != FileDeleted) merged = FileCreated;
                last[key] = merged;
            }
            foreach (LanguageServerInstance inst in GetInstances())
            {
                if (inst.State != LanguageServerState.Running || inst.Endpoint == null) continue;
                List<LspFileWatcher> watchers = new List<LspFileWatcher>();
                lock (inst.FileWatchers) { foreach (List<LspFileWatcher> l in inst.FileWatchers.Values) watchers.AddRange(l); }
                if (watchers.Count == 0) continue;
                List<object> changes = new List<object>();
                foreach (string p in order)
                {
                    int type = last[LspText.PathKey(p)];
                    if (type == 0) continue;
                    foreach (LspFileWatcher w in watchers)
                    {
                        if (!w.Matches(p, type)) continue;
                        changes.Add(LspJson.Obj("uri", LspText.PathToUri(Path.GetFullPath(p)), "type", type));
                        break;
                    }
                }
                if (changes.Count == 0) continue;
                LanguageServerInstance target = inst;
                target.Enqueue(delegate
                {
                    if (target.Endpoint != null && target.State == LanguageServerState.Running)
                        target.Endpoint.SendNotification("workspace/didChangeWatchedFiles", LspJson.Obj("changes", changes));
                });
            }
        }

        // ================================================================ status / queries

        public LanguageServerInstance GetInstanceForPath(string path)
        {
            LspOpenDocument d = FindDoc(path);
            return d != null ? d.Instance : null;
        }

        /// <summary>Status of the server for this file, or null when no built-in server covers its language.</summary>
        public LanguageServerStatus GetStatus(string path)
        {
            LspOpenDocument d = FindDoc(path);
            if (d == null || d.Instance == null) return null;
            LanguageServerInstance i = d.Instance;
            LanguageServerStatus s = new LanguageServerStatus();
            s.Key = i.Key;
            s.LanguageId = d.LanguageId;
            s.ServerName = i.DisplayName;
            s.State = i.State;
            s.Message = i.Message;
            s.ProgressText = i.ProgressText;
            s.InstallCommand = i.Resolution != null ? i.Resolution.InstallCommand : null;
            s.CanInstall = Installer != null && i.Definition != null && LanguageServerInstaller.RecipesFor(i.Definition.Id).Count > 0;
            return s;
        }

        /// <summary>True when a server is (or is about to be) serving this file, i.e. LSP results should be awaited.</summary>
        public bool IsActive(string path)
        {
            LanguageServerInstance i = GetInstanceForPath(path);
            return i != null && (i.State == LanguageServerState.Running || i.State == LanguageServerState.Starting) &&
                   (i.Resolution == null || i.Resolution.CanStart);
        }

        public bool Supports(string path, string capability)
        {
            LanguageServerInstance i = GetInstanceForPath(path);
            return i != null && i.State == LanguageServerState.Running && i.HasCapability(capability);
        }

        public List<string> GetCompletionTriggerCharacters(string path)
        {
            LanguageServerInstance i = GetInstanceForPath(path);
            return i != null && i.State == LanguageServerState.Running ? i.GetTriggerCharacters("completionProvider", "triggerCharacters") : new List<string>();
        }

        public List<string> GetSignatureTriggerCharacters(string path, bool retrigger)
        {
            LanguageServerInstance i = GetInstanceForPath(path);
            if (i == null || i.State != LanguageServerState.Running) return new List<string>();
            List<string> r = i.GetTriggerCharacters("signatureHelpProvider", "triggerCharacters");
            if (retrigger) r.AddRange(i.GetTriggerCharacters("signatureHelpProvider", "retriggerCharacters"));
            return r;
        }

        public string GetLog(string path)
        {
            LanguageServerInstance i = GetInstanceForPath(path);
            if (i != null) return "== " + i.DisplayName + " (" + i.Root + ") ==\n" + i.GetLogText();
            return GetAllLogs();
        }

        public string GetAllLogs()
        {
            StringBuilder sb = new StringBuilder();
            foreach (LanguageServerInstance i in GetInstances())
            {
                sb.Append("== ").Append(i.DisplayName).Append(" (").Append(i.Root).Append(") — ").Append(i.State).Append(" ==\n");
                sb.Append(i.GetLogText()).Append('\n');
            }
            if (sb.Length == 0) sb.Append("No language servers have been started in this window yet. Open a source file (Python, TypeScript, Go, Rust, C/C++, ...).\n");
            return sb.ToString();
        }

        // ================================================================ features

        private Task<object> Request(string path, string capability, string method, Func<LspOpenDocument, object> buildParams, CancellationToken ct, int timeoutMs)
        {
            LspOpenDocument doc = FindDoc(path);
            if (doc == null || doc.Instance == null) return Task.FromResult<object>(null);
            LanguageServerInstance inst = doc.Instance;
            if (inst.State != LanguageServerState.Running && inst.State != LanguageServerState.Starting) return Task.FromResult<object>(null);
            FlushDoc(doc);
            Task<object> t = inst.EnqueueRequest(delegate
            {
                if (inst.Endpoint == null || inst.State != LanguageServerState.Running) return Task.FromResult<object>(null);
                if (capability != null && !inst.HasCapability(capability)) return Task.FromResult<object>(null);
                if (ct.IsCancellationRequested)
                {
                    TaskCompletionSource<object> c = new TaskCompletionSource<object>();
                    c.SetCanceled();
                    return c.Task;
                }
                return inst.Endpoint.SendRequestAsync(method, buildParams(doc), ct, timeoutMs);
            });
            return WithTimeout(t, timeoutMs + (inst.State == LanguageServerState.Starting ? InitializeTimeoutMs : 0));
        }

        private static Task<object> WithTimeout(Task<object> t, int ms)
        {
            TaskCompletionSource<object> tcs = new TaskCompletionSource<object>(TaskCreationOptions.RunContinuationsAsynchronously);
            Timer timer = null;
            timer = new Timer(delegate { try { timer.Dispose(); } catch { } tcs.TrySetException(new TimeoutException()); }, null, ms, Timeout.Infinite);
            t.ContinueWith(delegate (Task<object> x)
            {
                try { timer.Dispose(); } catch { }
                if (x.IsCanceled) tcs.TrySetCanceled();
                else if (x.IsFaulted) tcs.TrySetException(x.Exception.InnerException ?? x.Exception);
                else tcs.TrySetResult(x.Result);
            }, TaskContinuationOptions.ExecuteSynchronously);
            return tcs.Task;
        }

        private static Dictionary<string, object> PosParams(LspOpenDocument doc, int line, int col)
        {
            return LspJson.Obj("textDocument", LspJson.Obj("uri", doc.Uri), "position", LspJson.Obj("line", line, "character", Math.Max(0, col)));
        }

        private void LogFailure(string path, string what, Exception ex)
        {
            LanguageServerInstance i = GetInstanceForPath(path);
            if (i == null || ex == null) return;
            Exception inner = ex is AggregateException && ex.InnerException != null ? ex.InnerException : ex;
            if (inner is TaskCanceledException || inner is OperationCanceledException) return;
            i.AppendLog("[client] " + what + " failed: " + inner.Message);
        }

        /// <summary>textDocument/completion. <paramref name="triggerCharacter"/> null = invoked (Ctrl/Cmd+Space or typing).</summary>
        public async Task<LspCompletionList> RequestCompletionAsync(string path, int line, int col, string triggerCharacter, bool incompleteRetrigger, CancellationToken ct)
        {
            try
            {
                object r = await Request(path, "completionProvider", "textDocument/completion", delegate (LspOpenDocument doc)
                {
                    Dictionary<string, object> p = PosParams(doc, line, col);
                    Dictionary<string, object> ctx = LspJson.Obj("triggerKind", incompleteRetrigger ? 3 : (triggerCharacter != null ? 2 : 1));
                    if (triggerCharacter != null && !incompleteRetrigger) ctx["triggerCharacter"] = triggerCharacter;
                    p["context"] = ctx;
                    return p;
                }, ct, RequestTimeoutMs).ConfigureAwait(false);
                return r == null ? null : LspCompletionList.Parse(r);
            }
            catch (Exception ex) { LogFailure(path, "completion", ex); return null; }
        }

        public async Task<LspCompletionItem> ResolveCompletionAsync(string path, LspCompletionItem item, CancellationToken ct)
        {
            if (item == null || item.Resolved || item.Raw == null) return item;
            LanguageServerInstance inst = GetInstanceForPath(path);
            if (inst == null || inst.State != LanguageServerState.Running) return item;
            object cp = inst.ServerCapabilities != null ? LspJson.Get(inst.ServerCapabilities, "completionProvider") : null;
            if (!LspJson.GetBool(cp, "resolveProvider", false)) { item.Resolved = true; return item; }
            try
            {
                object r = await Request(path, null, "completionItem/resolve", delegate { return item.Raw; }, ct, RequestTimeoutMs).ConfigureAwait(false);
                item.MergeResolved(r);
            }
            catch (Exception ex) { LogFailure(path, "completionItem/resolve", ex); }
            return item;
        }

        public async Task<LspHover> RequestHoverAsync(string path, int line, int col, CancellationToken ct)
        {
            try
            {
                object r = await Request(path, "hoverProvider", "textDocument/hover", delegate (LspOpenDocument doc) { return PosParams(doc, line, col); }, ct, RequestTimeoutMs).ConfigureAwait(false);
                return LspHover.Parse(r);
            }
            catch (Exception ex) { LogFailure(path, "hover", ex); return null; }
        }

        public async Task<LspSignatureHelp> RequestSignatureHelpAsync(string path, int line, int col, string triggerCharacter, bool isRetrigger, CancellationToken ct)
        {
            try
            {
                object r = await Request(path, "signatureHelpProvider", "textDocument/signatureHelp", delegate (LspOpenDocument doc)
                {
                    Dictionary<string, object> p = PosParams(doc, line, col);
                    Dictionary<string, object> ctx = LspJson.Obj("triggerKind", triggerCharacter != null ? 2 : (isRetrigger ? 3 : 1), "isRetrigger", isRetrigger);
                    if (triggerCharacter != null) ctx["triggerCharacter"] = triggerCharacter;
                    p["context"] = ctx;
                    return p;
                }, ct, RequestTimeoutMs).ConfigureAwait(false);
                return LspSignatureHelp.Parse(r);
            }
            catch (Exception ex) { LogFailure(path, "signatureHelp", ex); return null; }
        }

        /// <summary>Definition-like requests: "textDocument/definition", "typeDefinition", "implementation", "declaration".
        /// Returns null when the server cannot answer (caller should fall back), an empty list when it found nothing.</summary>
        public async Task<List<LspLocation>> RequestLocationsAsync(string path, int line, int col, string method, CancellationToken ct)
        {
            string cap = CapabilityForMethod(method);
            if (!IsActive(path)) return null;
            LanguageServerInstance inst = GetInstanceForPath(path);
            if (inst != null && inst.State == LanguageServerState.Running && cap != null && !inst.HasCapability(cap)) return null;
            try
            {
                object r = await Request(path, cap, method, delegate (LspOpenDocument doc) { return PosParams(doc, line, col); }, ct, RequestTimeoutMs).ConfigureAwait(false);
                return LspLocation.ParseMany(r);
            }
            catch (Exception ex) { LogFailure(path, method, ex); return null; }
        }

        public async Task<List<LspLocation>> RequestReferencesAsync(string path, int line, int col, bool includeDeclaration, CancellationToken ct)
        {
            if (!IsActive(path)) return null;
            try
            {
                object r = await Request(path, "referencesProvider", "textDocument/references", delegate (LspOpenDocument doc)
                {
                    Dictionary<string, object> p = PosParams(doc, line, col);
                    p["context"] = LspJson.Obj("includeDeclaration", includeDeclaration);
                    return p;
                }, ct, LongRequestTimeoutMs).ConfigureAwait(false);
                return LspLocation.ParseMany(r);
            }
            catch (Exception ex) { LogFailure(path, "references", ex); return null; }
        }

        /// <summary>textDocument/prepareRename: returns the placeholder (or null = use the word at the caret). Throws LspRequestException when renaming is not possible.</summary>
        public async Task<string> PrepareRenameAsync(string path, int line, int col, CancellationToken ct)
        {
            LanguageServerInstance inst = GetInstanceForPath(path);
            if (inst == null || inst.State != LanguageServerState.Running) return null;
            object rp = inst.ServerCapabilities != null ? LspJson.Get(inst.ServerCapabilities, "renameProvider") : null;
            if (!LspJson.GetBool(rp, "prepareProvider", false)) return null;
            object r;
            try
            {
                r = await Request(path, null, "textDocument/prepareRename", delegate (LspOpenDocument doc) { return PosParams(doc, line, col); }, ct, RequestTimeoutMs).ConfigureAwait(false);
            }
            catch (LspRequestException) { throw; }
            catch (Exception ex) { LogFailure(path, "prepareRename", ex); return null; }
            if (r == null) throw new LspRequestException(0, "The element can't be renamed.");
            string placeholder = LspJson.GetString(r, "placeholder");
            if (placeholder != null) return placeholder;
            LspRange range = LspRange.Parse(r) ?? LspRange.Parse(LspJson.Get(r, "range"));
            LspOpenDocument d = FindDoc(path);
            if (range != null && d != null && range.StartLine == range.EndLine)
            {
                string text = d.LatestText ?? "";
                List<int> starts = LspText.LineStarts(text);
                string lt = LspText.LineText(text, starts, range.StartLine);
                int s = Math.Min(range.StartCharacter, lt.Length), e = Math.Min(range.EndCharacter, lt.Length);
                if (e > s) return lt.Substring(s, e - s);
            }
            return null;
        }

        /// <summary>textDocument/rename. Throws <see cref="LspRequestException"/> with the server's message on refusal.</summary>
        public async Task<LspWorkspaceEdit> RequestRenameAsync(string path, int line, int col, string newName, CancellationToken ct)
        {
            if (!IsActive(path)) return null;
            object r;
            try
            {
                r = await Request(path, "renameProvider", "textDocument/rename", delegate (LspOpenDocument doc)
                {
                    Dictionary<string, object> p = PosParams(doc, line, col);
                    p["newName"] = newName;
                    return p;
                }, ct, LongRequestTimeoutMs).ConfigureAwait(false);
            }
            catch (LspRequestException) { throw; }
            catch (Exception ex) { LogFailure(path, "rename", ex); return null; }
            return r == null ? null : LspWorkspaceEdit.Parse(r);
        }

        /// <summary>Document (range == null) or range formatting. Null = not supported / failed.</summary>
        public async Task<List<LspTextEdit>> RequestFormattingAsync(string path, LspRange range, int tabSize, bool insertSpaces, CancellationToken ct)
        {
            string cap = range == null ? "documentFormattingProvider" : "documentRangeFormattingProvider";
            if (!Supports(path, cap))
            {
                if (range == null || !Supports(path, "documentFormattingProvider")) return null;
                range = null; // fall back to whole-document formatting
                cap = "documentFormattingProvider";
            }
            try
            {
                object r = await Request(path, cap, range == null ? "textDocument/formatting" : "textDocument/rangeFormatting", delegate (LspOpenDocument doc)
                {
                    Dictionary<string, object> p = LspJson.Obj("textDocument", LspJson.Obj("uri", doc.Uri),
                        "options", LspJson.Obj("tabSize", Math.Max(1, tabSize), "insertSpaces", insertSpaces, "trimTrailingWhitespace", true, "insertFinalNewline", true));
                    if (range != null) p["range"] = range.ToJson();
                    return p;
                }, ct, LongRequestTimeoutMs).ConfigureAwait(false);
                return LspTextEdit.ParseMany(r);
            }
            catch (Exception ex) { LogFailure(path, "formatting", ex); return null; }
        }

        public async Task<List<LspDocumentSymbol>> RequestDocumentSymbolsAsync(string path, CancellationToken ct)
        {
            if (!Supports(path, "documentSymbolProvider")) return null;
            try
            {
                object r = await Request(path, "documentSymbolProvider", "textDocument/documentSymbol", delegate (LspOpenDocument doc)
                {
                    return LspJson.Obj("textDocument", LspJson.Obj("uri", doc.Uri));
                }, ct, RequestTimeoutMs).ConfigureAwait(false);
                return LspDocumentSymbol.ParseFlat(r);
            }
            catch (Exception ex) { LogFailure(path, "documentSymbol", ex); return null; }
        }

        /// <summary>textDocument/codeAction for <paramref name="range"/> with the overlapping diagnostics as context.</summary>
        public async Task<List<LspCodeAction>> RequestCodeActionsAsync(string path, LspRange range, CancellationToken ct)
        {
            if (!Supports(path, "codeActionProvider")) return null;
            List<object> diags = new List<object>();
            foreach (DiagnosticItem d in LspDiagnosticsStore.Get(path))
            {
                if (d.LspRaw == null) continue;
                int dl0 = d.Line - 1, dl1 = Math.Max(d.Line, d.EndLine) - 1;
                if (dl1 < range.StartLine || dl0 > range.EndLine) continue;
                diags.Add(d.LspRaw);
            }
            try
            {
                object r = await Request(path, "codeActionProvider", "textDocument/codeAction", delegate (LspOpenDocument doc)
                {
                    return LspJson.Obj("textDocument", LspJson.Obj("uri", doc.Uri), "range", range.ToJson(),
                        "context", LspJson.Obj("diagnostics", diags, "triggerKind", 1));
                }, ct, RequestTimeoutMs).ConfigureAwait(false);
                return LspCodeAction.ParseMany(r);
            }
            catch (Exception ex) { LogFailure(path, "codeAction", ex); return null; }
        }

        public async Task<LspCodeAction> ResolveCodeActionAsync(string path, LspCodeAction action, CancellationToken ct)
        {
            if (action == null || action.Edit != null || action.Raw == null) return action;
            LanguageServerInstance inst = GetInstanceForPath(path);
            object cap = inst != null && inst.ServerCapabilities != null ? LspJson.Get(inst.ServerCapabilities, "codeActionProvider") : null;
            if (!LspJson.GetBool(cap, "resolveProvider", false)) return action;
            try
            {
                object r = await Request(path, null, "codeAction/resolve", delegate { return action.Raw; }, ct, RequestTimeoutMs).ConfigureAwait(false);
                if (r != null)
                {
                    if (LspJson.Has(r, "edit")) action.Edit = LspWorkspaceEdit.Parse(LspJson.Get(r, "edit"));
                    if (LspJson.Has(r, "command")) action.Command = LspJson.Get(r, "command");
                }
            }
            catch (Exception ex) { LogFailure(path, "codeAction/resolve", ex); }
            return action;
        }

        /// <summary>Sends an arbitrary request for the document's server (after pending syncs). <paramref name="buildParams"/> receives the document URI.</summary>
        public Task<object> RequestRawAsync(string path, string method, Func<string, object> buildParams, CancellationToken ct, int timeoutMs)
        {
            return Request(path, null, method, delegate (LspOpenDocument doc) { return buildParams != null ? buildParams(doc.Uri) : null; }, ct, timeoutMs > 0 ? timeoutMs : RequestTimeoutMs);
        }

        /// <summary>Sends an arbitrary notification to the document's server (after pending syncs).</summary>
        public void NotifyRaw(string path, string method, object parameters)
        {
            LspOpenDocument doc = FindDoc(path);
            if (doc == null || doc.Instance == null) return;
            LanguageServerInstance inst = doc.Instance;
            inst.Enqueue(delegate { if (inst.Endpoint != null) inst.Endpoint.SendNotification(method, parameters); });
        }

        /// <summary>How the client must handle a Command attached to a completion item / code action.</summary>
        public enum LspCommandTarget { Ignore, TriggerSuggest, TriggerParameterHints, Server }

        /// <summary>
        /// Commands such as editor.action.triggerParameterHints are *client* commands (VS Code runs them locally); only commands
        /// the server lists in executeCommandProvider.commands may be sent with workspace/executeCommand.
        /// </summary>
        public static LspCommandTarget ClassifyCommand(string commandName, IList<string> serverCommands)
        {
            if (string.IsNullOrEmpty(commandName)) return LspCommandTarget.Ignore;
            if (commandName == "editor.action.triggerSuggest") return LspCommandTarget.TriggerSuggest;
            if (commandName == "editor.action.triggerParameterHints") return LspCommandTarget.TriggerParameterHints;
            if (serverCommands != null)
                foreach (string c in serverCommands)
                    if (string.Equals(c, commandName, StringComparison.Ordinal)) return LspCommandTarget.Server;
            return LspCommandTarget.Ignore;
        }

        /// <summary><see cref="ClassifyCommand(string, IList{string})"/> for the server of <paramref name="path"/> (unknown commands are logged).</summary>
        public LspCommandTarget ClassifyCommand(string path, object command)
        {
            string name = LspJson.GetString(command, "command");
            LanguageServerInstance i = GetInstanceForPath(path);
            List<string> serverCommands = i != null ? i.GetTriggerCharacters("executeCommandProvider", "commands") : null;
            LspCommandTarget t = ClassifyCommand(name, serverCommands);
            if (t == LspCommandTarget.Ignore && i != null && !string.IsNullOrEmpty(name))
                i.AppendLog("[client] ignored client command '" + name + "' (not supported by codeLess and not offered by the server)");
            return t;
        }

        /// <summary>workspace/executeCommand for a Command object ({title, command, arguments}).</summary>
        public async Task<object> ExecuteCommandAsync(string path, object command, CancellationToken ct)
        {
            string name = LspJson.GetString(command, "command");
            if (string.IsNullOrEmpty(name)) return null;
            // Commands often answer with workspace/applyEdit for several files: the server must have every pending change first.
            FlushAllDirty();
            try
            {
                Dictionary<string, object> p = LspJson.Obj("command", name);
                object args = LspJson.Get(command, "arguments");
                if (args != null) p["arguments"] = args;
                return await Request(path, null, "workspace/executeCommand", delegate { return p; }, ct, LongRequestTimeoutMs).ConfigureAwait(false);
            }
            catch (Exception ex) { LogFailure(path, "executeCommand " + name, ex); return null; }
        }
    }
}
