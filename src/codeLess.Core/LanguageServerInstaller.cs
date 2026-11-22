using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace codeLess.Studio
{
    /// <summary>How a recipe installs a server.</summary>
    public enum LanguageServerInstallKind
    {
        /// <summary>npm install --prefix &lt;managed dir&gt; (never -g).</summary>
        Npm,
        /// <summary>A private venv in the managed folder + pip install (fallback when npm is missing).</summary>
        PythonVenv,
        /// <summary>go install with GOBIN = &lt;managed dir&gt;/bin.</summary>
        GoInstall,
        /// <summary>rustup component add (user-level toolchain folder, not the managed folder).</summary>
        Rustup,
        /// <summary>dotnet tool install --tool-path &lt;managed dir&gt;/bin.</summary>
        DotnetTool,
        /// <summary>Official GitHub release asset for this OS / CPU, verified and extracted into the managed folder.</summary>
        GitHubRelease
    }

    /// <summary>One sudo-free way to install a language server (tried in order per server).</summary>
    public class LanguageServerInstallRecipe
    {
        public string ServerId;
        /// <summary>Sub-folder of the managed root (html / css / json share one package).</summary>
        public string PackageId;
        public LanguageServerInstallKind Kind;
        /// <summary>Tool that must exist ("npm", "python", "go", "rustup", "dotnet"); null for downloads.</summary>
        public string Tool;
        public string[] Packages = new string[0];
        /// <summary>Short description of the route for messages ("npm", "pip", "GitHub release"...).</summary>
        public string Via;
        /// <summary>Representative URL for the Network Activity ledger.</summary>
        public string NetworkUrl;
        /// <summary>GitHub "owner/repo" for <see cref="LanguageServerInstallKind.GitHubRelease"/>.</summary>
        public string ReleaseRepo;
        /// <summary>Platform key ("macos-arm64", "linux-x64", "windows-x64"...) to asset-name regex.</summary>
        public Dictionary<string, string> AssetPatterns = new Dictionary<string, string>(StringComparer.Ordinal);
        /// <summary>The server lands outside the managed folder (rustup): verified through the normal search path.</summary>
        public bool InstallsOutsideManagedFolder;
    }

    /// <summary>One process run by an install (npm install..., python -m venv..., tar -xzf...).</summary>
    public class LanguageServerInstallStep
    {
        public string Exe;
        public string[] Args = new string[0];
        public Dictionary<string, string> Env = new Dictionary<string, string>(StringComparer.Ordinal);
        public string WorkingDir;
        /// <summary>How the executable is shown in the copyable command ("npm" instead of /opt/homebrew/bin/npm).</summary>
        public string DisplayExe;

        public string ToDisplayString(bool windows)
        {
            return LanguageServerInstaller.FormatCommandLine(DisplayExe ?? Exe, Args, Env, windows);
        }
    }

    /// <summary>What an install would do for one server on this machine.</summary>
    public class LanguageServerInstallPlan
    {
        public LanguageServerDefinition Definition;
        /// <summary>The chosen recipe; null when no automatic route exists here (see <see cref="Reason"/>).</summary>
        public LanguageServerInstallRecipe Recipe;
        public List<LanguageServerInstallStep> Steps = new List<LanguageServerInstallStep>();
        public string TargetDir;
        /// <summary>Why no automatic route exists (toolchain missing, needs sudo / Homebrew, no download for this OS).</summary>
        public string Reason;
        /// <summary>The exact command(s) a user can run by hand.</summary>
        public string ManualCommand;

        public bool CanInstall { get { return Recipe != null; } }
    }

    public class LanguageServerInstallResult
    {
        public string ServerId;
        public string PackageId;
        public bool Success;
        public bool Cancelled;
        /// <summary>Not attempted because Offline Mode is on.</summary>
        public bool BlockedOffline;
        public string Message;
        /// <summary>Exact command(s) used (or to use by hand).</summary>
        public string Command;
        public string Via;
        public string Log;
    }

    /// <summary>Finds a toolchain executable ("npm", "python", "go", "rustup", "dotnet"); null when missing.</summary>
    public interface ILanguageServerToolProbe
    {
        string Find(string tool);
    }

    /// <summary>Runs one install step; returns the exit code. Throws OperationCanceledException / TimeoutException.</summary>
    public interface ILanguageServerProcessRunner
    {
        int Run(LanguageServerInstallStep step, Action<string> output, CancellationToken ct, int timeoutMs);
    }

    /// <summary>Network access for release downloads (the default goes through the NetworkLedger choke point).</summary>
    public interface ILanguageServerDownloader
    {
        string GetText(string url, int timeoutMs, out string error);
        bool DownloadFile(string url, string path, long maxBytes, CancellationToken ct, int timeoutMs, out string error);
    }

    /// <summary>Default probe: PATH + the usual tool folders (after the login-shell environment resolved), never relative paths.</summary>
    public sealed class DefaultLanguageServerToolProbe : ILanguageServerToolProbe
    {
        public string Find(string tool)
        {
            if (string.IsNullOrEmpty(tool)) return null;
            UserShellEnvironment.WaitForResolution(8000);
            string[] names;
            if (tool == "python") names = LspText.IsWindows() ? new string[] { "python", "py" } : new string[] { "python3", "python" };
            else names = new string[] { tool };
            foreach (string n in names)
            {
                // workspace root null: workspace node_modules/.bin or venvs are never used to install anything
                string p = LanguageServerRegistry.FindExecutable(n, null);
                if (string.IsNullOrEmpty(p) || !Path.IsPathRooted(p)) continue;
                // The Microsoft Store "python.exe" alias only opens the Store.
                if (LspText.IsWindows() && p.IndexOf("\\WindowsApps\\", StringComparison.OrdinalIgnoreCase) >= 0) continue;
                return p;
            }
            return null;
        }
    }

    /// <summary>Runs install steps with drained output, a timeout and cancellation (process tree killed).</summary>
    public sealed class DefaultLanguageServerProcessRunner : ILanguageServerProcessRunner
    {
        public int Run(LanguageServerInstallStep step, Action<string> output, CancellationToken ct, int timeoutMs)
        {
            ProcessStartInfo psi = LanguageServerRegistry.BuildStartInfo(step.Exe, step.Args, step.WorkingDir);
            if (step.Env != null)
                foreach (KeyValuePair<string, string> kv in step.Env) psi.EnvironmentVariables[kv.Key] = kv.Value;
            Process p = new Process();
            p.StartInfo = psi;
            DataReceivedEventHandler h = delegate (object s, DataReceivedEventArgs e)
            {
                if (e.Data != null && output != null) { try { output(e.Data); } catch { } }
            };
            p.OutputDataReceived += h;
            p.ErrorDataReceived += h;
            try
            {
                p.Start();
                try { p.StandardInput.Close(); } catch { } // EOF: an installer must never wait for input
                p.BeginOutputReadLine();
                p.BeginErrorReadLine();
                Stopwatch sw = Stopwatch.StartNew();
                while (!p.WaitForExit(200))
                {
                    if (ct.IsCancellationRequested)
                    {
                        LanguageServerManager.KillProcessTree(p);
                        throw new OperationCanceledException("Cancelled");
                    }
                    if (timeoutMs > 0 && sw.ElapsedMilliseconds > timeoutMs)
                    {
                        LanguageServerManager.KillProcessTree(p);
                        throw new TimeoutException("timed out after " + (timeoutMs / 1000) + " s");
                    }
                }
                p.WaitForExit(); // flushes the async output readers
                return p.ExitCode;
            }
            finally
            {
                try { p.Dispose(); } catch { }
            }
        }
    }

    /// <summary>Release downloads through <see cref="LedgerHttp"/> (Offline Mode check + Network Activity entry per hop).</summary>
    public sealed class LedgerLanguageServerDownloader : ILanguageServerDownloader
    {
        public string GetText(string url, int timeoutMs, out string error)
        {
            error = null;
            try
            {
                LedgerHttpRequest r = new LedgerHttpRequest();
                r.Feature = LanguageServerInstaller.LedgerFeature;
                r.Url = url;
                r.Accept = "application/vnd.github+json";
                r.TimeoutMs = timeoutMs;
                r.MaxResponseBytes = 4L * 1024 * 1024;
                LedgerHttpResponse res = LedgerHttp.Send(r);
                if (!res.IsSuccess) { error = !string.IsNullOrEmpty(res.Error) ? res.Error : "HTTP " + res.Status; return null; }
                if (res.Truncated) { error = "response too large"; return null; }
                return res.Body;
            }
            catch (NetworkBlockedException ex) { error = ex.Message; return null; }
            catch (Exception ex) { error = ex.Message; return null; }
        }

        public bool DownloadFile(string url, string path, long maxBytes, CancellationToken ct, int timeoutMs, out string error)
        {
            error = null;
            bool tooBig = false;
            try
            {
                LedgerHttpResponse res = LedgerHttp.DownloadToFile(LanguageServerInstaller.LedgerFeature, url, path, "codeLess", timeoutMs,
                    delegate (long written, long total) { if (written > maxBytes || total > maxBytes) tooBig = true; },
                    delegate { return tooBig || ct.IsCancellationRequested; });
                if (tooBig) { error = "download is larger than " + (maxBytes / (1024 * 1024)) + " MB"; return false; }
                if (ct.IsCancellationRequested) throw new OperationCanceledException("Cancelled");
                if (!res.IsSuccess) { error = !string.IsNullOrEmpty(res.Error) ? res.Error : "HTTP " + res.Status; return false; }
                return true;
            }
            catch (NetworkBlockedException ex) { error = ex.Message; return false; }
        }
    }

    /// <summary>A running (or finished) install of one server package. Shared by every caller that asked for it.</summary>
    public sealed class LanguageServerInstallJob
    {
        private readonly TaskCompletionSource<LanguageServerInstallResult> tcs = new TaskCompletionSource<LanguageServerInstallResult>();
        private readonly StringBuilder log = new StringBuilder();
        private readonly object logLock = new object();
        internal readonly CancellationTokenSource Cts = new CancellationTokenSource();

        public string ServerId { get; internal set; }
        public string PackageId { get; internal set; }
        public string DisplayName { get; internal set; }
        public bool UserInitiated { get; internal set; }
        public string Via { get; internal set; }
        public DateTime StartedUtc { get; internal set; }

        private readonly List<string> lines = new List<string>();
        private readonly List<Action<string>> listeners = new List<Action<string>>();
        private const int MaxReplayLines = 5000;

        public Task<LanguageServerInstallResult> Task { get { return tcs.Task; } }
        public bool IsCompleted { get { return tcs.Task.IsCompleted; } }

        public string StatusText { get { return "Installing " + DisplayName + " language server…"; } }

        public void Cancel()
        {
            try { Cts.Cancel(); } catch { }
        }

        public string GetLog()
        {
            lock (logLock) return log.ToString();
        }

        /// <summary>
        /// Subscribes to output lines (called on worker threads, in order). The job starts working before its callers can
        /// subscribe (and later callers join a running job), so the lines logged so far are replayed first, atomically with
        /// the subscription: no line is ever missed or duplicated, and the order is preserved.
        /// </summary>
        public void AddOutputListener(Action<string> listener)
        {
            if (listener == null) return;
            lock (logLock)
            {
                foreach (string l in lines) { try { listener(l); } catch { } }
                listeners.Add(listener);
            }
        }

        public void RemoveOutputListener(Action<string> listener)
        {
            lock (logLock) listeners.Remove(listener);
        }

        internal void Log(string line)
        {
            line = line ?? "";
            // Listeners run under the lock: stdout and stderr lines arrive on different threads, and a listener must see
            // them in exactly the order they were logged (and never concurrently with its replay).
            lock (logLock)
            {
                if (log.Length < 512 * 1024) log.Append(line).Append('\n');
                if (lines.Count < MaxReplayLines) lines.Add(line);
                foreach (Action<string> h in listeners) { try { h(line); } catch { } }
            }
        }

        internal void Complete(LanguageServerInstallResult r)
        {
            r.Log = GetLog();
            tcs.TrySetResult(r);
        }
    }

    /// <summary>
    /// Installs missing language servers into a codeLess-managed folder (never global, never sudo / admin), like VS Code
    /// extensions that ship their servers: npm --prefix, a private Python venv, go install with GOBIN, dotnet tool
    /// --tool-path, rustup components, or a verified GitHub release download. One install per package at a time (callers
    /// share the running job), installs run one after another, failures are remembered (no retry loop; automatic retry
    /// after <see cref="FailureBackoff"/> or on request), Offline Mode refuses installs, downloads go through the
    /// NetworkLedger and subprocess installs are listed there too. Nothing from the workspace is executed: tools are found
    /// without the workspace, and every process runs in the managed folder. C# 5 only.
    /// </summary>
    public sealed class LanguageServerInstaller
    {
        public const string LedgerFeature = "Language server install";
        /// <summary>Overrides the managed folder (tests, portable setups).</summary>
        public const string ManagedRootEnvVar = "CODELESS_LSP_SERVERS_DIR";
        public const long MaxDownloadBytes = 300L * 1024 * 1024;

        private static LanguageServerInstaller shared;
        private static readonly object sharedLock = new object();

        private readonly object gate = new object();
        private readonly Dictionary<string, LanguageServerInstallJob> jobs = new Dictionary<string, LanguageServerInstallJob>(StringComparer.OrdinalIgnoreCase);
        private readonly SemaphoreSlim oneAtATime = new SemaphoreSlim(1, 1);
        private readonly StringBuilder allLog = new StringBuilder();

        public string ManagedRoot { get; private set; }
        public ILanguageServerToolProbe Probe;
        public ILanguageServerProcessRunner Runner;
        public ILanguageServerDownloader Downloader;
        /// <summary>"macos-arm64", "linux-x64", "windows-x64"... (selects release assets).</summary>
        public string Platform;
        public Func<bool> IsOffline;
        public Func<DateTime> UtcNow;
        /// <summary>Per-step timeout (npm / pip / go / downloads).</summary>
        public int StepTimeoutMs = 10 * 60 * 1000;
        public TimeSpan FailureBackoff = TimeSpan.FromHours(24);

        public LanguageServerInstaller(string managedRoot)
            : this(managedRoot, null, null, null)
        {
        }

        public LanguageServerInstaller(string managedRoot, ILanguageServerToolProbe probe, ILanguageServerProcessRunner runner, ILanguageServerDownloader downloader)
        {
            ManagedRoot = string.IsNullOrEmpty(managedRoot) ? DefaultManagedRoot() : managedRoot;
            Probe = probe ?? new DefaultLanguageServerToolProbe();
            Runner = runner ?? new DefaultLanguageServerProcessRunner();
            Downloader = downloader ?? new LedgerLanguageServerDownloader();
            Platform = CurrentPlatform();
            IsOffline = delegate { return NetworkLedger.OfflineMode; };
            UtcNow = delegate { return DateTime.UtcNow; };
        }

        /// <summary>Process-wide installer used by the desktop UIs (one install per server across windows).</summary>
        public static LanguageServerInstaller Shared
        {
            get
            {
                lock (sharedLock)
                {
                    if (shared == null) shared = new LanguageServerInstaller(null);
                    return shared;
                }
            }
        }

        /// <summary>~/.codeless/servers (macOS / Linux), %LOCALAPPDATA%\codeLess\servers (Windows), or $CODELESS_LSP_SERVERS_DIR.</summary>
        public static string DefaultManagedRoot()
        {
            string env = Environment.GetEnvironmentVariable(ManagedRootEnvVar);
            if (!string.IsNullOrEmpty(env) && Path.IsPathRooted(env)) return env;
            try
            {
                if (LspText.IsWindows())
                {
                    string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                    if (!string.IsNullOrEmpty(local)) return Path.Combine(Path.Combine(local, "codeLess"), "servers");
                }
                string home = LanguageServerRegistry.HomeDir();
                if (!string.IsNullOrEmpty(home)) return Path.Combine(Path.Combine(home, ".codeless"), "servers");
            }
            catch { }
            return Path.Combine(Path.GetTempPath(), "codeLess-servers");
        }

        public static string CurrentPlatform()
        {
            string os = LspText.IsWindows() ? "windows" : LspText.IsMacOS() ? "macos" : "linux";
            string arch = "x64";
            try
            {
                Architecture a = RuntimeInformation.OSArchitecture;
                if (a == Architecture.Arm64) arch = "arm64";
                else if (a == Architecture.X86) arch = "x86";
                else if (a == Architecture.Arm) arch = "arm";
            }
            catch { }
            return os + "-" + arch;
        }

        private bool PlatformIsWindows { get { return (Platform ?? "").StartsWith("windows", StringComparison.Ordinal); } }

        // ================================================================ recipes

        /// <summary>Install routes for a server, in the order they are tried.</summary>
        public static List<LanguageServerInstallRecipe> RecipesFor(string serverId)
        {
            List<LanguageServerInstallRecipe> list = new List<LanguageServerInstallRecipe>();
            switch ((serverId ?? "").ToLowerInvariant())
            {
                case "python":
                    list.Add(Npm("python", "python", "pyright"));
                    list.Add(Recipe("python", "python", LanguageServerInstallKind.PythonVenv, "python", "pip (private venv)", "https://pypi.org/simple/basedpyright/", "basedpyright"));
                    break;
                case "typescript":
                    list.Add(Npm("typescript", "typescript", "typescript-language-server", "typescript"));
                    break;
                case "bash":
                    list.Add(Npm("bash", "bash", "bash-language-server"));
                    break;
                case "yaml":
                    list.Add(Npm("yaml", "yaml", "yaml-language-server"));
                    break;
                case "json":
                case "html":
                case "css":
                    list.Add(Npm(serverId.ToLowerInvariant(), "vscode-langservers-extracted", "vscode-langservers-extracted"));
                    break;
                case "go":
                    list.Add(Recipe("go", "go", LanguageServerInstallKind.GoInstall, "go", "go install", "https://proxy.golang.org/golang.org/x/tools/gopls/@latest", "golang.org/x/tools/gopls@latest"));
                    break;
                case "rust":
                    {
                        LanguageServerInstallRecipe r = Recipe("rust", "rust", LanguageServerInstallKind.Rustup, "rustup", "rustup", "https://static.rust-lang.org/", "rust-analyzer");
                        r.InstallsOutsideManagedFolder = true;
                        list.Add(r);
                        break;
                    }
                case "csharp":
                    list.Add(Recipe("csharp", "csharp", LanguageServerInstallKind.DotnetTool, "dotnet", "dotnet tool", "https://api.nuget.org/v3/index.json", "csharp-ls"));
                    break;
                case "clangd":
                    {
                        // clangd/clangd publishes x64 builds for Linux and Windows (the mac zip's architecture isn't
                        // documented, and Apple's Command Line Tools ship clangd anyway).
                        LanguageServerInstallRecipe r = Recipe("clangd", "clangd", LanguageServerInstallKind.GitHubRelease, null, "GitHub release", null);
                        r.ReleaseRepo = "clangd/clangd";
                        r.AssetPatterns["linux-x64"] = @"^clangd-linux-[0-9][0-9.]*\.zip$";
                        r.AssetPatterns["windows-x64"] = @"^clangd-windows-[0-9][0-9.]*\.zip$";
                        list.Add(r);
                        break;
                    }
                case "lua":
                    {
                        LanguageServerInstallRecipe r = Recipe("lua", "lua", LanguageServerInstallKind.GitHubRelease, null, "GitHub release", null);
                        r.ReleaseRepo = "LuaLS/lua-language-server";
                        r.AssetPatterns["macos-arm64"] = @"^lua-language-server-[0-9][0-9.]*-darwin-arm64\.tar\.gz$";
                        r.AssetPatterns["macos-x64"] = @"^lua-language-server-[0-9][0-9.]*-darwin-x64\.tar\.gz$";
                        r.AssetPatterns["linux-x64"] = @"^lua-language-server-[0-9][0-9.]*-linux-x64\.tar\.gz$";
                        r.AssetPatterns["linux-arm64"] = @"^lua-language-server-[0-9][0-9.]*-linux-arm64\.tar\.gz$";
                        r.AssetPatterns["windows-x64"] = @"^lua-language-server-[0-9][0-9.]*-win32-x64\.zip$";
                        list.Add(r);
                        break;
                    }
            }
            foreach (LanguageServerInstallRecipe r in list)
                if (r.Kind == LanguageServerInstallKind.GitHubRelease) r.NetworkUrl = "https://api.github.com/repos/" + r.ReleaseRepo + "/releases/latest";
            return list;
        }

        private static LanguageServerInstallRecipe Npm(string serverId, string packageId, params string[] packages)
        {
            return Recipe(serverId, packageId, LanguageServerInstallKind.Npm, "npm", "npm", "https://registry.npmjs.org/" + packages[0], packages);
        }

        private static LanguageServerInstallRecipe Recipe(string serverId, string packageId, LanguageServerInstallKind kind, string tool, string via, string url, params string[] packages)
        {
            LanguageServerInstallRecipe r = new LanguageServerInstallRecipe();
            r.ServerId = serverId;
            r.PackageId = packageId;
            r.Kind = kind;
            r.Tool = tool;
            r.Via = via;
            r.NetworkUrl = url;
            r.Packages = packages ?? new string[0];
            return r;
        }

        /// <summary>Package folder shared by the servers of one package (html / css / json).</summary>
        public static string PackageIdFor(LanguageServerDefinition d)
        {
            if (d == null) return null;
            List<LanguageServerInstallRecipe> r = RecipesFor(d.Id);
            return r.Count > 0 ? r[0].PackageId : d.Id;
        }

        /// <summary>Folders inside the managed root that may hold this server's executables (checked before PATH).</summary>
        public static List<string> ManagedSearchDirectories(string managedRoot, LanguageServerDefinition d)
        {
            List<string> dirs = new List<string>();
            if (string.IsNullOrEmpty(managedRoot) || d == null) return dirs;
            string pkg = Path.Combine(managedRoot, PackageIdFor(d));
            dirs.Add(Path.Combine(Path.Combine(pkg, "node_modules"), ".bin"));
            dirs.Add(Path.Combine(pkg, "bin"));
            dirs.Add(Path.Combine(Path.Combine(pkg, "venv"), LspText.IsWindows() ? "Scripts" : "bin"));
            try
            {
                // extracted release archives: <pkg>/clangd_23.1.0/bin
                if (Directory.Exists(pkg))
                    foreach (string sub in Directory.GetDirectories(pkg))
                    {
                        string name = Path.GetFileName(sub);
                        if (name == "node_modules" || name == "venv" || name == "bin" || name.StartsWith(".", StringComparison.Ordinal)) continue;
                        dirs.Add(Path.Combine(sub, "bin"));
                    }
            }
            catch { }
            return dirs;
        }

        // ================================================================ planning

        /// <summary>Chooses the first recipe whose toolchain / download exists here and builds its commands. Probes tools (off the UI thread).</summary>
        public LanguageServerInstallPlan Plan(LanguageServerDefinition d)
        {
            LanguageServerInstallPlan plan = new LanguageServerInstallPlan();
            plan.Definition = d;
            if (d == null) { plan.Reason = "unknown language"; return plan; }
            List<LanguageServerInstallRecipe> recipes = RecipesFor(d.Id);
            if (recipes.Count == 0)
            {
                plan.Reason = NoRecipeReason(d);
                return plan;
            }
            List<string> missing = new List<string>();
            foreach (LanguageServerInstallRecipe r in recipes)
            {
                string dir = Path.Combine(ManagedRoot, r.PackageId);
                if (r.Kind == LanguageServerInstallKind.GitHubRelease)
                {
                    string pattern;
                    if (!r.AssetPatterns.TryGetValue(Platform ?? "", out pattern))
                    {
                        missing.Add("no prebuilt " + ServerName(d) + " download for " + Platform);
                        continue;
                    }
                    plan.Recipe = r;
                    plan.TargetDir = dir;
                    plan.ManualCommand = "Download the " + Platform + " asset of the latest release from https://github.com/" + r.ReleaseRepo + "/releases and extract it into " + dir;
                    return plan;
                }
                string tool = Probe.Find(r.Tool);
                if (string.IsNullOrEmpty(tool))
                {
                    missing.Add(ToolDisplayName(r.Tool) + " was not found");
                    continue;
                }
                plan.Recipe = r;
                plan.TargetDir = dir;
                plan.Steps = BuildSteps(r, tool, dir);
                List<string> shown = new List<string>();
                foreach (LanguageServerInstallStep s in plan.Steps) shown.Add(s.ToDisplayString(PlatformIsWindows));
                plan.ManualCommand = string.Join(PlatformIsWindows ? " && " : " && ", shown.ToArray());
                return plan;
            }
            plan.Reason = "codeLess can't install it automatically: " + string.Join("; ", missing.Distinct().ToArray()) + NeedsHint(recipes);
            return plan;
        }

        private static string NeedsHint(List<LanguageServerInstallRecipe> recipes)
        {
            List<string> tools = new List<string>();
            foreach (LanguageServerInstallRecipe r in recipes)
                if (r.Tool != null) tools.Add(ToolDisplayName(r.Tool));
            if (tools.Count == 0) return ".";
            return " (it needs " + string.Join(" or ", tools.Distinct().ToArray()) + ").";
        }

        private static string ToolDisplayName(string tool)
        {
            switch (tool)
            {
                case "npm": return "Node.js (npm)";
                case "python": return "Python 3";
                case "go": return "Go";
                case "rustup": return "rustup";
                case "dotnet": return "the .NET SDK";
                default: return tool;
            }
        }

        private static string ServerName(LanguageServerDefinition d)
        {
            return d.Candidates.Count > 0 ? d.Candidates[0].DisplayName : d.DisplayName;
        }

        private static string NoRecipeReason(LanguageServerDefinition d)
        {
            switch (d.Id)
            {
                case "java": return "codeLess can't install jdtls automatically: it is distributed through Homebrew / Eclipse snapshot archives and needs a Java runtime.";
                default: return "codeLess has no sudo-free automatic installer for " + ServerName(d) + ".";
            }
        }

        /// <summary>Command lines for a recipe (also what "Copy Command" shows).</summary>
        public List<LanguageServerInstallStep> BuildSteps(LanguageServerInstallRecipe r, string toolPath, string dir)
        {
            List<LanguageServerInstallStep> steps = new List<LanguageServerInstallStep>();
            bool win = PlatformIsWindows;
            LanguageServerInstallStep s;
            switch (r.Kind)
            {
                case LanguageServerInstallKind.Npm:
                    {
                        s = NewStep(toolPath, "npm");
                        List<string> a = new List<string> { "install", "--prefix", dir, "--no-audit", "--no-fund", "--no-update-notifier", "--ignore-scripts" };
                        a.AddRange(r.Packages);
                        s.Args = a.ToArray();
                        s.Env["npm_config_update_notifier"] = "false";
                        steps.Add(s);
                        break;
                    }
                case LanguageServerInstallKind.PythonVenv:
                    {
                        string venv = Path.Combine(dir, "venv");
                        bool pyLauncher = string.Equals(Path.GetFileNameWithoutExtension(toolPath), "py", StringComparison.OrdinalIgnoreCase);
                        s = NewStep(toolPath, pyLauncher ? "py" : (win ? "python" : "python3"));
                        s.Args = pyLauncher ? new string[] { "-3", "-m", "venv", venv } : new string[] { "-m", "venv", venv };
                        steps.Add(s);
                        string venvPy = win ? Path.Combine(Path.Combine(venv, "Scripts"), "python.exe") : Path.Combine(Path.Combine(venv, "bin"), "python");
                        s = NewStep(venvPy, null);
                        List<string> a = new List<string> { "-m", "pip", "install", "--disable-pip-version-check", "--no-input", "--upgrade" };
                        a.AddRange(r.Packages);
                        s.Args = a.ToArray();
                        s.Env["PIP_NO_INPUT"] = "1";
                        s.Env["PYTHONNOUSERSITE"] = "1";
                        steps.Add(s);
                        break;
                    }
                case LanguageServerInstallKind.GoInstall:
                    {
                        s = NewStep(toolPath, "go");
                        List<string> a = new List<string> { "install" };
                        a.AddRange(r.Packages);
                        s.Args = a.ToArray();
                        s.Env["GOBIN"] = Path.Combine(dir, "bin");
                        steps.Add(s);
                        break;
                    }
                case LanguageServerInstallKind.Rustup:
                    {
                        s = NewStep(toolPath, "rustup");
                        List<string> a = new List<string> { "component", "add" };
                        a.AddRange(r.Packages);
                        s.Args = a.ToArray();
                        steps.Add(s);
                        break;
                    }
                case LanguageServerInstallKind.DotnetTool:
                    {
                        string toolDir = Path.Combine(dir, "bin");
                        bool installed = false;
                        try { installed = Directory.Exists(Path.Combine(toolDir, ".store")); } catch { }
                        s = NewStep(toolPath, "dotnet");
                        List<string> a = new List<string> { "tool", installed ? "update" : "install" };
                        a.AddRange(r.Packages);
                        a.Add("--tool-path");
                        a.Add(toolDir);
                        s.Args = a.ToArray();
                        s.Env["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1";
                        s.Env["DOTNET_NOLOGO"] = "1";
                        steps.Add(s);
                        break;
                    }
            }
            foreach (LanguageServerInstallStep st in steps)
            {
                // Never the workspace: a project's .npmrc / go.mod / global.json must not steer the install.
                st.WorkingDir = ManagedRoot;
            }
            return steps;
        }

        private static LanguageServerInstallStep NewStep(string exe, string display)
        {
            LanguageServerInstallStep s = new LanguageServerInstallStep();
            s.Exe = exe;
            s.DisplayExe = display ?? exe;
            return s;
        }

        // ================================================================ command display / quoting

        /// <summary>Copyable command line: POSIX single quotes on macOS / Linux, CommandLineToArgvW quoting on Windows.</summary>
        public static string FormatCommandLine(string exe, string[] args, Dictionary<string, string> env, bool windows)
        {
            StringBuilder sb = new StringBuilder();
            if (env != null)
            {
                foreach (KeyValuePair<string, string> kv in env)
                {
                    if (kv.Key.StartsWith("npm_config_", StringComparison.Ordinal) || kv.Key.StartsWith("DOTNET_", StringComparison.Ordinal) ||
                        kv.Key == "PIP_NO_INPUT" || kv.Key == "PYTHONNOUSERSITE") continue; // cosmetic for a manual run
                    if (windows) sb.Append("set \"").Append(kv.Key).Append('=').Append(kv.Value).Append("\" && ");
                    else sb.Append(kv.Key).Append('=').Append(PosixQuote(kv.Value)).Append(' ');
                }
            }
            sb.Append(windows ? LanguageServerRegistry.QuoteArg(exe) : PosixQuote(exe));
            if (args != null)
                foreach (string a in args) sb.Append(' ').Append(windows ? LanguageServerRegistry.QuoteArg(a) : PosixQuote(a));
            return sb.ToString();
        }

        public static string PosixQuote(string s)
        {
            if (s == null) return "''";
            if (s.Length > 0 && Regex.IsMatch(s, @"^[A-Za-z0-9_@%+=:,./-]+$")) return s;
            return "'" + s.Replace("'", "'\\''") + "'";
        }

        // ================================================================ policy

        /// <summary>
        /// Whether an automatic install may start now (Offline Mode off, no remembered failure within the backoff, a route
        /// exists). <paramref name="reason"/> explains a "no".
        /// </summary>
        public bool ShouldAutoInstall(LanguageServerDefinition d, out string reason)
        {
            reason = null;
            if (d == null) { reason = "unknown language"; return false; }
            lock (gate)
            {
                LanguageServerInstallJob running;
                if (jobs.TryGetValue(PackageIdFor(d), out running) && !running.IsCompleted) return true;
            }
            if (Offline())
            {
                reason = "Offline Mode is on, so codeLess didn't install it automatically.";
                return false;
            }
            string failure;
            if (IsInBackoff(d, out failure))
            {
                reason = failure;
                return false;
            }
            LanguageServerInstallPlan plan = Plan(d);
            if (!plan.CanInstall)
            {
                reason = plan.Reason;
                return false;
            }
            return true;
        }

        private bool Offline()
        {
            try { return IsOffline != null && IsOffline(); } catch { return false; }
        }

        public LanguageServerInstallJob GetActiveJob(LanguageServerDefinition d)
        {
            if (d == null) return null;
            lock (gate)
            {
                LanguageServerInstallJob j;
                return jobs.TryGetValue(PackageIdFor(d), out j) && !j.IsCompleted ? j : null;
            }
        }

        public List<LanguageServerInstallJob> GetActiveJobs()
        {
            lock (gate) return jobs.Values.Where(j => !j.IsCompleted).ToList();
        }

        /// <summary>Starts (or joins) the install of <paramref name="d"/>. <paramref name="userInitiated"/> ignores the failure backoff.</summary>
        public LanguageServerInstallJob Start(LanguageServerDefinition d, bool userInitiated)
        {
            if (d == null) throw new ArgumentNullException("d");
            string pkg = PackageIdFor(d);
            LanguageServerInstallJob job;
            lock (gate)
            {
                LanguageServerInstallJob existing;
                if (jobs.TryGetValue(pkg, out existing) && !existing.IsCompleted) return existing;
                job = new LanguageServerInstallJob();
                job.ServerId = d.Id;
                job.PackageId = pkg;
                job.DisplayName = d.DisplayName;
                job.UserInitiated = userInitiated;
                job.StartedUtc = Now();
                jobs[pkg] = job;
            }
            LanguageServerInstallJob captured = job;
            System.Threading.Tasks.Task.Factory.StartNew(delegate { RunJob(captured, d, userInitiated); },
                CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
            return job;
        }

        /// <summary>Full output of every install this session (for "Show Log").</summary>
        public string GetLog()
        {
            lock (allLog) return allLog.ToString();
        }

        private void LogLine(LanguageServerInstallJob job, string line)
        {
            lock (allLog)
            {
                if (allLog.Length > 1024 * 1024) allLog.Remove(0, allLog.Length / 2);
                allLog.Append(line ?? "").Append('\n');
            }
            job.Log(line);
        }

        private DateTime Now()
        {
            try { return UtcNow != null ? UtcNow() : DateTime.UtcNow; } catch { return DateTime.UtcNow; }
        }

        private void RunJob(LanguageServerInstallJob job, LanguageServerDefinition d, bool userInitiated)
        {
            LanguageServerInstallResult result = new LanguageServerInstallResult();
            result.ServerId = d.Id;
            result.PackageId = job.PackageId;
            bool acquired = false;
            CancellationToken ct = job.Cts.Token;
            try
            {
                oneAtATime.Wait(ct); // installs run one after another (memory / bandwidth friendly)
                acquired = true;
                LanguageServerInstallPlan plan = Plan(d);
                result.Command = plan.ManualCommand;
                if (!plan.CanInstall)
                {
                    result.Message = "No " + d.DisplayName + " language server found — " + plan.Reason;
                    LogLine(job, "[" + d.DisplayName + "] " + plan.Reason);
                    return;
                }
                job.Via = plan.Recipe.Via;
                result.Via = plan.Recipe.Via;
                if (!userInitiated)
                {
                    string memo;
                    if (IsInBackoff(d, out memo)) { result.Message = memo; return; }
                }
                if (Offline())
                {
                    string why;
                    if (!NetworkLedger.Check(LedgerFeature, "GET", plan.Recipe.NetworkUrl, out why)) { }
                    result.BlockedOffline = true;
                    result.Message = "Offline Mode is on — codeLess doesn't install language servers while offline.";
                    LogLine(job, "[" + d.DisplayName + "] " + result.Message);
                    return;
                }
                LogLine(job, "[" + d.DisplayName + "] Installing via " + plan.Recipe.Via + " into " + plan.TargetDir);
                Directory.CreateDirectory(ManagedRoot);
                Stopwatch sw = Stopwatch.StartNew();
                string error = plan.Recipe.Kind == LanguageServerInstallKind.GitHubRelease
                    ? InstallFromGitHub(job, plan, ct)
                    : RunSteps(job, plan, ct);
                if (plan.Recipe.Kind != LanguageServerInstallKind.GitHubRelease)
                {
                    // Separate process: bytes aren't measured, but the contact is listed in Network Activity.
                    NetworkLedger.Record(LedgerFeature, "GET", plan.Recipe.NetworkUrl, 0, 0, error == null ? 200 : 0,
                        plan.Recipe.Via + " (separate process, bytes not measured)" + (error != null ? ": " + error : ""), sw.ElapsedMilliseconds);
                }
                if (error == null)
                {
                    string exe = FindInstalledExecutable(d, plan.Recipe);
                    if (exe == null) error = "the install finished, but no " + string.Join(" / ", d.Candidates.Select(c => c.Command).Distinct().ToArray()) + " executable was found afterwards";
                    else LogLine(job, "[" + d.DisplayName + "] Installed: " + exe);
                }
                if (error != null)
                {
                    result.Message = "Couldn't install the " + d.DisplayName + " language server (" + plan.Recipe.Via + "): " + error;
                    LogLine(job, "[" + d.DisplayName + "] " + result.Message);
                    RememberFailure(d, error);
                    return;
                }
                ClearFailure(d);
                LanguageServerRegistry.ClearCache();
                result.Success = true;
                result.Message = d.DisplayName + " language server installed (" + plan.Recipe.Via + ").";
            }
            catch (OperationCanceledException)
            {
                result.Cancelled = true;
                result.Message = "Installation of the " + d.DisplayName + " language server was cancelled.";
                LogLine(job, "[" + d.DisplayName + "] " + result.Message);
            }
            catch (TimeoutException ex)
            {
                result.Message = "Couldn't install the " + d.DisplayName + " language server: " + ex.Message;
                LogLine(job, "[" + d.DisplayName + "] " + result.Message);
                RememberFailure(d, ex.Message);
            }
            catch (Exception ex)
            {
                result.Message = "Couldn't install the " + d.DisplayName + " language server: " + ex.Message;
                LogLine(job, "[" + d.DisplayName + "] " + result.Message);
                RememberFailure(d, ex.Message);
            }
            finally
            {
                if (acquired) { try { oneAtATime.Release(); } catch { } }
                job.Complete(result);
            }
        }

        private string RunSteps(LanguageServerInstallJob job, LanguageServerInstallPlan plan, CancellationToken ct)
        {
            Directory.CreateDirectory(plan.TargetDir);
            foreach (LanguageServerInstallStep step in plan.Steps)
            {
                ct.ThrowIfCancellationRequested();
                LogLine(job, "$ " + step.ToDisplayString(PlatformIsWindows));
                LanguageServerInstallJob j = job;
                int code = Runner.Run(step, delegate (string line) { LogLine(j, "  " + line); }, ct, StepTimeoutMs);
                if (code != 0) return (step.DisplayExe ?? Path.GetFileName(step.Exe)) + " exited with code " + code;
            }
            return null;
        }

        /// <summary>Path of the installed server (managed folder, or the normal search path for rustup); null when missing.</summary>
        public string FindInstalledExecutable(LanguageServerDefinition d, LanguageServerInstallRecipe recipe)
        {
            List<string> dirs = ManagedSearchDirectories(ManagedRoot, d);
            string[] exts = LspText.IsWindows() ? new string[] { ".exe", ".cmd", ".bat", "" } : new string[] { "" };
            foreach (LanguageServerCandidate c in d.Candidates)
                foreach (string dir in dirs)
                    foreach (string ext in exts)
                    {
                        string p = Path.Combine(dir, c.Command + ext);
                        try { if (File.Exists(p) && (ext.Length > 0 || !LspText.IsWindows())) return p; } catch { }
                    }
            if (recipe != null && recipe.InstallsOutsideManagedFolder)
            {
                LanguageServerRegistry.ClearCache();
                foreach (LanguageServerCandidate c in d.Candidates)
                {
                    string p = LanguageServerRegistry.FindExecutable(c.Command, null);
                    if (p != null) return p;
                }
            }
            return null;
        }

        // ================================================================ GitHub releases

        private string InstallFromGitHub(LanguageServerInstallJob job, LanguageServerInstallPlan plan, CancellationToken ct)
        {
            LanguageServerInstallRecipe r = plan.Recipe;
            string pattern = r.AssetPatterns[Platform];
            string apiUrl = r.NetworkUrl;
            LogLine(job, "GET " + apiUrl);
            string err;
            string json = Downloader.GetText(apiUrl, 30000, out err);
            if (json == null) return "couldn't read the latest release (" + err + ")";
            ct.ThrowIfCancellationRequested();
            Dictionary<string, object> rel = null;
            try { rel = LspJson.Parse(json) as Dictionary<string, object>; } catch { }
            if (rel == null) return "unexpected release data";
            string tag = LspJson.GetString(rel, "tag_name");
            List<object> assets = rel.ContainsKey("assets") ? rel["assets"] as List<object> : null;
            Dictionary<string, object> asset = null;
            if (assets != null)
                foreach (object o in assets)
                {
                    Dictionary<string, object> a = o as Dictionary<string, object>;
                    if (a != null && Regex.IsMatch(LspJson.GetString(a, "name") ?? "", pattern)) { asset = a; break; }
                }
            if (asset == null) return "release " + tag + " has no asset for " + Platform;
            string name = LspJson.GetString(asset, "name");
            string url = LspJson.GetString(asset, "browser_download_url") ?? "";
            long size = 0;
            object so;
            if (asset.TryGetValue("size", out so) && so != null) { try { size = Convert.ToInt64(so, CultureInfo.InvariantCulture); } catch { } }
            string digest = LspJson.GetString(asset, "digest");
            string expectedPrefix = "https://github.com/" + r.ReleaseRepo + "/releases/download/";
            if (!url.StartsWith(expectedPrefix, StringComparison.Ordinal)) return "unexpected download location " + url;
            if (name.IndexOfAny(new char[] { '/', '\\' }) >= 0 || name.Contains("..")) return "unexpected asset name";
            if (size <= 0 || size > MaxDownloadBytes) return "unexpected asset size " + size;

            string staging = Path.Combine(ManagedRoot, ".staging-" + r.PackageId + "-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(staging);
            try
            {
                string archive = Path.Combine(staging, name);
                LogLine(job, "Downloading " + name + " (" + (size / 1024) + " KB) from release " + tag);
                if (!Downloader.DownloadFile(url, archive, MaxDownloadBytes, ct, 120000, out err))
                {
                    ct.ThrowIfCancellationRequested();
                    return "download failed (" + err + ")";
                }
                ct.ThrowIfCancellationRequested();
                long actual = new FileInfo(archive).Length;
                if (actual != size) return "downloaded " + actual + " bytes, expected " + size;
                if (!string.IsNullOrEmpty(digest) && digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase))
                {
                    string want = digest.Substring(7).Trim().ToLowerInvariant();
                    string got = Sha256File(archive);
                    if (!string.Equals(want, got, StringComparison.Ordinal)) return "SHA-256 mismatch (expected " + want + ", got " + got + ")";
                    LogLine(job, "SHA-256 verified: " + got);
                }
                else LogLine(job, "No checksum published for this asset; verified HTTPS origin (" + expectedPrefix + ") and size.");
                string extractDir = Path.Combine(staging, "x");
                Directory.CreateDirectory(extractDir);
                string exErr = Extract(job, archive, extractDir, ct);
                if (exErr != null) return exErr;
                try { File.Delete(archive); } catch { }
                string target = plan.TargetDir;
                if (Directory.Exists(target))
                {
                    string old = Path.Combine(ManagedRoot, ".old-" + r.PackageId + "-" + Guid.NewGuid().ToString("N").Substring(0, 8));
                    try { Directory.Move(target, old); }
                    catch (Exception ex) { return "the previous " + r.PackageId + " install is in use (" + ex.Message + "); stop the server and retry"; }
                    try { Directory.Delete(old, true); } catch { }
                }
                Directory.Move(extractDir, target);
                return null;
            }
            finally
            {
                try { if (Directory.Exists(staging)) Directory.Delete(staging, true); } catch { }
            }
        }

        private string Extract(LanguageServerInstallJob job, string archive, string dir, CancellationToken ct)
        {
            string lower = archive.ToLowerInvariant();
            LanguageServerInstallJob j = job;
            Action<string> log = delegate (string line) { LogLine(j, "  " + line); };
            if (lower.EndsWith(".tar.gz") || lower.EndsWith(".tgz"))
            {
                string tar = LspText.IsWindows() ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "tar.exe") : "/usr/bin/tar";
                if (!File.Exists(tar)) tar = LanguageServerRegistry.FindExecutable("tar", null);
                if (tar == null) return "tar was not found to extract " + Path.GetFileName(archive);
                LanguageServerInstallStep s = NewStep(tar, "tar");
                s.Args = new string[] { "-xzf", archive, "-C", dir };
                s.WorkingDir = dir;
                int code = Runner.Run(s, log, ct, StepTimeoutMs);
                return code == 0 ? null : "tar exited with code " + code;
            }
            if (lower.EndsWith(".zip"))
            {
                if (!LspText.IsWindows())
                {
                    string unzip = File.Exists("/usr/bin/unzip") ? "/usr/bin/unzip" : LanguageServerRegistry.FindExecutable("unzip", null);
                    if (unzip != null)
                    {
                        // unzip keeps the executable bits (ZipFile doesn't on .NET Framework)
                        LanguageServerInstallStep s = NewStep(unzip, "unzip");
                        s.Args = new string[] { "-q", "-o", archive, "-d", dir };
                        s.WorkingDir = dir;
                        int code = Runner.Run(s, log, ct, StepTimeoutMs);
                        return code == 0 ? null : "unzip exited with code " + code;
                    }
                }
                ZipFile.ExtractToDirectory(archive, dir); // rejects entries outside the folder (zip-slip)
                if (!LspText.IsWindows())
                {
                    LanguageServerInstallStep s = NewStep("/bin/chmod", "chmod");
                    s.Args = new string[] { "-R", "u+x", dir };
                    s.WorkingDir = dir;
                    try { Runner.Run(s, log, ct, 60000); } catch (OperationCanceledException) { throw; } catch { }
                }
                return null;
            }
            return "unsupported archive " + Path.GetFileName(archive);
        }

        public static string Sha256File(string path)
        {
            using (SHA256 sha = SHA256.Create())
            using (FileStream fs = File.OpenRead(path))
            {
                byte[] h = sha.ComputeHash(fs);
                StringBuilder sb = new StringBuilder(h.Length * 2);
                foreach (byte b in h) sb.Append(b.ToString("x2", CultureInfo.InvariantCulture));
                return sb.ToString();
            }
        }

        // ================================================================ failure memo (persisted, per package)

        private string MemoPath { get { return Path.Combine(ManagedRoot, ".install-failures"); } }

        private Dictionary<string, KeyValuePair<DateTime, string>> ReadMemo()
        {
            Dictionary<string, KeyValuePair<DateTime, string>> m = new Dictionary<string, KeyValuePair<DateTime, string>>(StringComparer.OrdinalIgnoreCase);
            try
            {
                if (!File.Exists(MemoPath)) return m;
                foreach (string line in File.ReadAllLines(MemoPath))
                {
                    string[] p = line.Split(new char[] { '\t' }, 3);
                    long ticks;
                    if (p.Length < 2 || !long.TryParse(p[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out ticks)) continue;
                    m[p[0]] = new KeyValuePair<DateTime, string>(new DateTime(ticks, DateTimeKind.Utc), p.Length > 2 ? p[2] : "");
                }
            }
            catch { }
            return m;
        }

        private void WriteMemo(Dictionary<string, KeyValuePair<DateTime, string>> m)
        {
            try
            {
                Directory.CreateDirectory(ManagedRoot);
                List<string> lines = new List<string>();
                foreach (KeyValuePair<string, KeyValuePair<DateTime, string>> kv in m)
                    lines.Add(kv.Key + "\t" + kv.Value.Key.Ticks.ToString(CultureInfo.InvariantCulture) + "\t" + (kv.Value.Value ?? "").Replace('\n', ' ').Replace('\r', ' ').Replace('\t', ' '));
                File.WriteAllLines(MemoPath, lines.ToArray());
            }
            catch { }
        }

        public void RememberFailure(LanguageServerDefinition d, string reason)
        {
            lock (gate)
            {
                Dictionary<string, KeyValuePair<DateTime, string>> m = ReadMemo();
                m[PackageIdFor(d)] = new KeyValuePair<DateTime, string>(Now(), reason ?? "");
                WriteMemo(m);
            }
        }

        public void ClearFailure(LanguageServerDefinition d)
        {
            lock (gate)
            {
                Dictionary<string, KeyValuePair<DateTime, string>> m = ReadMemo();
                if (m.Remove(PackageIdFor(d))) WriteMemo(m);
            }
        }

        /// <summary>True when an automatic install of this package failed less than <see cref="FailureBackoff"/> ago.</summary>
        public bool IsInBackoff(LanguageServerDefinition d, out string reason)
        {
            reason = null;
            if (d == null) return false;
            KeyValuePair<DateTime, string> f;
            lock (gate)
            {
                if (!ReadMemo().TryGetValue(PackageIdFor(d), out f)) return false;
            }
            TimeSpan age = Now() - f.Key;
            if (age >= FailureBackoff || age < TimeSpan.Zero) return false;
            int hours = (int)Math.Floor(age.TotalHours);
            string when = hours >= 1 ? hours + " h ago" : Math.Max(1, (int)age.TotalMinutes) + " min ago";
            reason = "the automatic install failed " + when + " (" + f.Value + "); it is retried after " + (int)FailureBackoff.TotalHours +
                     " h, or now with \"Language Servers: Install/Update…\".";
            return true;
        }
    }
}
