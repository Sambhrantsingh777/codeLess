using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;

namespace codeLess.Studio
{
    /// <summary>One Python interpreter found on this machine / in the workspace.</summary>
    public sealed class PythonInterpreterInfo
    {
        /// <summary>Full path to the python executable.</summary>
        public string Path;
        /// <summary>"3.12.1" (may be empty when unknown).</summary>
        public string Version;
        /// <summary>Short environment name shown in parentheses: ".venv", "base", "myproj-AbC123-py3.11".</summary>
        public string EnvName;
        /// <summary>venv | uv | poetry | pipenv | conda | pyenv | system | py-launcher | custom</summary>
        public string Kind;
        /// <summary>Environment root for venv / conda kinds (activation target), else null.</summary>
        public string EnvPath;

        public bool IsVirtualEnv { get { return Kind == "venv" || Kind == "uv" || Kind == "poetry" || Kind == "pipenv"; } }
        public bool IsConda { get { return Kind == "conda"; } }

        /// <summary>VS Code style: "3.12.1 ('.venv')", "3.11.4 ('base': conda)", "3.9.6 64-bit".</summary>
        public string DisplayLabel
        {
            get
            {
                string v = string.IsNullOrEmpty(Version) ? "Python" : Version;
                if (!string.IsNullOrEmpty(EnvName))
                {
                    if (Kind == "conda") return v + " ('" + EnvName + "': conda)";
                    if (Kind == "poetry" || Kind == "pipenv" || Kind == "pyenv") return v + " ('" + EnvName + "': " + Kind + ")";
                    return v + " ('" + EnvName + "')";
                }
                return v;
            }
        }

        /// <summary>Long description for pickers: kind and path.</summary>
        public string Detail
        {
            get { return (Kind ?? "python") + "  " + Path; }
        }

        public override string ToString() { return DisplayLabel; }
    }

    /// <summary>
    /// Injectable OS access for interpreter discovery (unit tests use fake directory trees and fake tool output).
    /// </summary>
    public sealed class PythonDiscoveryEnvironment
    {
        public bool IsWindows;
        public string HomeDir;
        public Func<string, string> GetEnv;
        /// <summary>Finds an executable on PATH (plus well-known dirs); null when missing.</summary>
        public Func<string, string> FindExecutable;
        /// <summary>(exe, args, timeoutMs) -> stdout, or null on failure / timeout.</summary>
        public Func<string, string, int, string> RunProcess;
        /// <summary>False in unit tests: never runs "python --version" for unknown versions.</summary>
        public bool ProbeVersions = true;

        public static PythonDiscoveryEnvironment CreateDefault()
        {
            PythonDiscoveryEnvironment e = new PythonDiscoveryEnvironment();
            e.IsWindows = Path.DirectorySeparatorChar == '\\';
            e.HomeDir = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            e.GetEnv = delegate (string name) { return Environment.GetEnvironmentVariable(name); };
            e.FindExecutable = delegate (string name) { return ToolLocator.Find(name); };
            e.RunProcess = ToolLocator.RunAndCapture;
            return e;
        }
    }

    /// <summary>PATH lookup and short process runs that never hang the caller (always used off the UI thread).</summary>
    public static class ToolLocator
    {
        /// <summary>Extra directories GUI-launched apps miss (macOS apps started from Finder get a minimal PATH).</summary>
        public static List<string> ExtraSearchDirs()
        {
            List<string> dirs = new List<string>();
            string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (Path.DirectorySeparatorChar == '\\')
            {
                string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                dirs.Add(System.IO.Path.Combine(home, "go", "bin"));
                dirs.Add(System.IO.Path.Combine(home, ".cargo", "bin"));
                dirs.Add(System.IO.Path.Combine(home, ".dotnet", "tools"));
                dirs.Add(System.IO.Path.Combine(local, "Programs", "Python", "Launcher"));
                dirs.Add(System.IO.Path.Combine(home, ".local", "bin"));
            }
            else
            {
                dirs.Add("/opt/homebrew/bin");
                dirs.Add("/usr/local/bin");
                dirs.Add("/usr/bin");
                dirs.Add(System.IO.Path.Combine(home, ".local", "bin"));
                dirs.Add(System.IO.Path.Combine(home, "go", "bin"));
                dirs.Add(System.IO.Path.Combine(home, ".cargo", "bin"));
                dirs.Add(System.IO.Path.Combine(home, ".dotnet", "tools"));
                dirs.Add(System.IO.Path.Combine(home, ".pyenv", "shims"));
                dirs.Add(System.IO.Path.Combine(home, "miniconda3", "bin"));
                dirs.Add(System.IO.Path.Combine(home, "miniforge3", "bin"));
                dirs.Add(System.IO.Path.Combine(home, "anaconda3", "bin"));
                dirs.Add("/opt/homebrew/opt/llvm/bin");
            }
            return dirs;
        }

        public static string Find(string name)
        {
            if (string.IsNullOrEmpty(name)) return null;
            try
            {
                if (name.IndexOf('/') >= 0 || name.IndexOf('\\') >= 0) return File.Exists(name) ? name : null;
                bool win = Path.DirectorySeparatorChar == '\\';
                string[] exts = win ? new string[] { ".exe", ".cmd", ".bat", "" } : new string[] { "" };
                List<string> dirs = new List<string>();
                string pathVar = Environment.GetEnvironmentVariable("PATH") ?? "";
                foreach (string d in pathVar.Split(System.IO.Path.PathSeparator)) if (d.Trim().Length > 0) dirs.Add(d.Trim().Trim('"'));
                dirs.AddRange(ExtraSearchDirs());
                foreach (string d in dirs)
                {
                    foreach (string ext in exts)
                    {
                        string p;
                        try { p = System.IO.Path.Combine(d, name + ext); } catch { continue; }
                        if (File.Exists(p))
                        {
                            // WindowsApps "python.exe" is the Store installer stub, not an interpreter.
                            if (win && p.IndexOf("WindowsApps", StringComparison.OrdinalIgnoreCase) >= 0 && name.StartsWith("python", StringComparison.OrdinalIgnoreCase)) continue;
                            return p;
                        }
                    }
                }
            }
            catch { }
            return null;
        }

        /// <summary>Runs a program and returns stdout (stderr appended when stdout is empty), or null on failure.</summary>
        public static string RunAndCapture(string exe, string args, int timeoutMs)
        {
            try
            {
                ProcessStartInfo psi = new ProcessStartInfo(exe, args ?? "");
                psi.UseShellExecute = false;
                psi.CreateNoWindow = true;
                psi.RedirectStandardOutput = true;
                psi.RedirectStandardError = true;
                psi.RedirectStandardInput = false;
                using (Process p = Process.Start(psi))
                {
                    if (p == null) return null;
                    StringBuilder err = new StringBuilder();
                    p.ErrorDataReceived += delegate (object s, DataReceivedEventArgs e) { if (e.Data != null) lock (err) err.AppendLine(e.Data); };
                    p.BeginErrorReadLine();
                    System.Threading.Tasks.Task<string> outTask = p.StandardOutput.ReadToEndAsync();
                    if (!p.WaitForExit(timeoutMs))
                    {
                        try { p.Kill(); } catch { }
                        return null;
                    }
                    p.WaitForExit();
                    string o = outTask.Wait(1000) ? outTask.Result : "";
                    if (string.IsNullOrWhiteSpace(o)) lock (err) o = err.ToString();
                    return p.ExitCode == 0 ? o : null;
                }
            }
            catch { return null; }
        }
    }

    /// <summary>
    /// Zero-setup Python: finds interpreters like VS Code's Python extension (workspace venvs, uv, poetry, pipenv,
    /// conda, pyenv, system / py launcher). Strict C# 5. Discovery may run tools, so always call it off the UI thread.
    /// </summary>
    public static class PythonInterpreterDiscovery
    {
        public static List<PythonInterpreterInfo> Discover(string workspaceRoot, PythonDiscoveryEnvironment env)
        {
            if (env == null) env = PythonDiscoveryEnvironment.CreateDefault();
            List<PythonInterpreterInfo> found = new List<PythonInterpreterInfo>();
            HashSet<string> seen = new HashSet<string>(env.IsWindows ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);

            bool hasWorkspace = !string.IsNullOrEmpty(workspaceRoot) && SafeDirExists(workspaceRoot);
            string projectName = hasWorkspace ? System.IO.Path.GetFileName(workspaceRoot.TrimEnd('/', '\\')) : "";

            // 1. Workspace virtual environments (.venv first). uv projects keep theirs in .venv next to uv.lock.
            if (hasWorkspace)
            {
                bool isUv = SafeFileExists(System.IO.Path.Combine(workspaceRoot, "uv.lock"));
                List<string> candidates = new List<string>();
                foreach (string n in PythonVenvActivator.CandidateFolderNames) candidates.Add(System.IO.Path.Combine(workspaceRoot, n));
                try
                {
                    foreach (string d in Directory.GetDirectories(workspaceRoot))
                        if (!candidates.Contains(d)) candidates.Add(d);
                }
                catch { }
                foreach (string dir in candidates)
                {
                    if (!SafeFileExists(System.IO.Path.Combine(dir, "pyvenv.cfg"))) continue;
                    string exe = VenvPython(dir, env.IsWindows);
                    if (exe == null) continue;
                    string leaf = System.IO.Path.GetFileName(dir);
                    string kind = (isUv && leaf == ".venv") || ReadPyvenvCfg(dir, "uv") != null ? "uv" : "venv";
                    Add(found, seen, exe, ReadVenvVersion(dir), leaf, kind, dir);
                }
            }

            // 2. Poetry: in-project .venv is covered above; else ask poetry, else scan its cache dir.
            if (hasWorkspace && FileContains(System.IO.Path.Combine(workspaceRoot, "pyproject.toml"), "[tool.poetry"))
            {
                string poetry = env.FindExecutable != null ? env.FindExecutable("poetry") : null;
                string envDir = null;
                if (poetry != null && env.RunProcess != null)
                {
                    string o = env.RunProcess(poetry, "env info -p --directory " + DebugAdapterConnection.QuoteArg(workspaceRoot), 8000);
                    if (!string.IsNullOrWhiteSpace(o)) envDir = o.Trim().Split('\n')[0].Trim();
                }
                List<string> dirs = new List<string>();
                if (!string.IsNullOrEmpty(envDir)) dirs.Add(envDir);
                string cache = env.IsWindows
                    ? System.IO.Path.Combine(Coalesce(env.GetEnv("LOCALAPPDATA"), System.IO.Path.Combine(env.HomeDir ?? "", "AppData", "Local")), "pypoetry", "Cache", "virtualenvs")
                    : (IsMacHome(env) ? System.IO.Path.Combine(env.HomeDir ?? "", "Library", "Caches", "pypoetry", "virtualenvs")
                                      : System.IO.Path.Combine(env.HomeDir ?? "", ".cache", "pypoetry", "virtualenvs"));
                dirs.AddRange(ChildDirsWithPrefix(cache, NormalizePoetryName(projectName) + "-"));
                foreach (string d in dirs)
                {
                    string exe = VenvPython(d, env.IsWindows);
                    if (exe != null) Add(found, seen, exe, ReadVenvVersion(d), System.IO.Path.GetFileName(d.TrimEnd('/', '\\')), "poetry", d);
                }
            }

            // 3. Pipenv.
            if (hasWorkspace && SafeFileExists(System.IO.Path.Combine(workspaceRoot, "Pipfile")))
            {
                List<string> dirs = new List<string>();
                string pipenv = env.FindExecutable != null ? env.FindExecutable("pipenv") : null;
                if (pipenv != null && env.RunProcess != null)
                {
                    string o = env.RunProcess(pipenv, "--venv", 8000);
                    if (!string.IsNullOrWhiteSpace(o)) dirs.Add(o.Trim().Split('\n')[0].Trim());
                }
                string store = Coalesce(env.GetEnv("WORKON_HOME"), System.IO.Path.Combine(env.HomeDir ?? "", ".local", "share", "virtualenvs"));
                dirs.AddRange(ChildDirsWithPrefix(store, projectName + "-"));
                foreach (string d in dirs)
                {
                    string exe = VenvPython(d, env.IsWindows);
                    if (exe != null) Add(found, seen, exe, ReadVenvVersion(d), System.IO.Path.GetFileName(d.TrimEnd('/', '\\')), "pipenv", d);
                }
            }

            // 4. Conda: active env, `conda env list --json`, and the usual install roots.
            {
                List<string> condaPrefixes = new List<string>();
                string active = env.GetEnv("CONDA_PREFIX");
                if (!string.IsNullOrEmpty(active)) condaPrefixes.Add(active);
                string conda = env.FindExecutable != null ? env.FindExecutable("conda") : null;
                if (conda != null && env.RunProcess != null)
                {
                    string o = env.RunProcess(conda, "env list --json", 10000);
                    if (!string.IsNullOrEmpty(o))
                    {
                        try
                        {
                            List<object> envs = DapJson.Arr(DapJson.Parse(o.Trim()), "envs");
                            if (envs != null) foreach (object x in envs) { string s = x as string; if (!string.IsNullOrEmpty(s)) condaPrefixes.Add(s); }
                        }
                        catch { }
                    }
                }
                foreach (string rootName in new string[] { "miniconda3", "miniforge3", "anaconda3", "mambaforge", "micromamba" })
                {
                    string root = System.IO.Path.Combine(env.HomeDir ?? "", rootName);
                    if (!SafeDirExists(root)) continue;
                    condaPrefixes.Add(root);
                    try { foreach (string d in Directory.GetDirectories(System.IO.Path.Combine(root, "envs"))) condaPrefixes.Add(d); }
                    catch { }
                }
                foreach (string prefix in condaPrefixes)
                {
                    if (!SafeDirExists(System.IO.Path.Combine(prefix, "conda-meta"))) continue;
                    string exe = env.IsWindows ? System.IO.Path.Combine(prefix, "python.exe") : System.IO.Path.Combine(prefix, "bin", "python");
                    if (!SafeFileExists(exe)) continue;
                    string name = SafeDirExists(System.IO.Path.Combine(prefix, "envs")) ? "base" : System.IO.Path.GetFileName(prefix.TrimEnd('/', '\\'));
                    Add(found, seen, exe, ReadCondaVersion(prefix), name, "conda", prefix);
                }
            }

            // 5. pyenv versions.
            {
                string root = Coalesce(env.GetEnv("PYENV_ROOT"), System.IO.Path.Combine(env.HomeDir ?? "", env.IsWindows ? System.IO.Path.Combine(".pyenv", "pyenv-win") : ".pyenv"));
                string versions = System.IO.Path.Combine(root, "versions");
                if (SafeDirExists(versions))
                {
                    string[] dirs;
                    try { dirs = Directory.GetDirectories(versions); } catch { dirs = new string[0]; }
                    Array.Sort(dirs, StringComparer.OrdinalIgnoreCase);
                    Array.Reverse(dirs);
                    foreach (string d in dirs)
                    {
                        string exe = env.IsWindows ? System.IO.Path.Combine(d, "python.exe") : System.IO.Path.Combine(d, "bin", "python");
                        if (!SafeFileExists(exe) && !env.IsWindows) exe = System.IO.Path.Combine(d, "bin", "python3");
                        if (!SafeFileExists(exe)) continue;
                        string leaf = System.IO.Path.GetFileName(d);
                        string ver = LooksLikeVersion(leaf) ? leaf : ReadVenvVersion(d);
                        Add(found, seen, exe, ver, LooksLikeVersion(leaf) ? null : leaf, "pyenv", SafeFileExists(System.IO.Path.Combine(d, "pyvenv.cfg")) ? d : null);
                    }
                }
            }

            // 6. System interpreters: py launcher (Windows), then PATH python3/python and well-known locations.
            if (env.IsWindows)
            {
                string py = env.FindExecutable != null ? env.FindExecutable("py") : null;
                if (py != null && env.RunProcess != null)
                {
                    string o = env.RunProcess(py, "-0p", 6000);
                    if (!string.IsNullOrEmpty(o))
                    {
                        foreach (string raw in o.Split('\n'))
                        {
                            string line = raw.Trim();
                            int idx = line.IndexOf(":\\", StringComparison.Ordinal);
                            if (idx < 1) continue;
                            string path = line.Substring(idx - 1).Trim();
                            string tag = line.Substring(0, idx - 1).Trim().TrimStart('-').Replace("V:", "").Replace("*", "").Trim();
                            string ver = tag.Split('-')[0].Trim();
                            if (SafeFileExists(path)) Add(found, seen, path, ver, null, "system", null);
                        }
                    }
                }
                string local = Coalesce(env.GetEnv("LOCALAPPDATA"), System.IO.Path.Combine(env.HomeDir ?? "", "AppData", "Local"));
                foreach (string d in ChildDirsWithPrefix(System.IO.Path.Combine(local, "Programs", "Python"), "Python3"))
                {
                    string exe = System.IO.Path.Combine(d, "python.exe");
                    if (SafeFileExists(exe)) Add(found, seen, exe, null, null, "system", null);
                }
            }
            List<string> sys = new List<string>();
            if (env.FindExecutable != null)
            {
                string p3 = env.FindExecutable("python3");
                if (p3 != null) sys.Add(p3);
                string p = env.FindExecutable("python");
                if (p != null) sys.Add(p);
            }
            if (!env.IsWindows)
                foreach (string p in new string[] { "/opt/homebrew/bin/python3", "/usr/local/bin/python3", "/usr/bin/python3" })
                    if (SafeFileExists(p)) sys.Add(p);
            foreach (string p in sys)
            {
                // pyenv shims resolve to a pyenv version already listed.
                if (p.IndexOf(System.IO.Path.DirectorySeparatorChar + "shims" + System.IO.Path.DirectorySeparatorChar, StringComparison.Ordinal) >= 0) continue;
                Add(found, seen, p, null, null, "system", null);
            }

            // Versions for system interpreters (cheap: one short process per interpreter, background thread only).
            if (env.ProbeVersions && env.RunProcess != null)
            {
                foreach (PythonInterpreterInfo i in found)
                {
                    if (!string.IsNullOrEmpty(i.Version)) continue;
                    string o = env.RunProcess(i.Path, "--version", 4000);
                    if (!string.IsNullOrEmpty(o)) i.Version = ParseVersionOutput(o);
                }
            }
            return found;
        }

        private static bool IsMacHome(PythonDiscoveryEnvironment env)
        {
            return !env.IsWindows && (env.HomeDir ?? "").StartsWith("/Users/", StringComparison.Ordinal);
        }

        private static string NormalizePoetryName(string name)
        {
            return (name ?? "").ToLowerInvariant().Replace('_', '-').Replace('.', '-').Replace(' ', '-');
        }

        private static string Coalesce(string a, string b) { return string.IsNullOrEmpty(a) ? b : a; }

        private static void Add(List<PythonInterpreterInfo> list, HashSet<string> seen, string exe, string version, string envName, string kind, string envPath)
        {
            string key = exe;
            try { key = System.IO.Path.GetFullPath(exe); } catch { }
            if (!seen.Add(key)) return;
            PythonInterpreterInfo i = new PythonInterpreterInfo();
            i.Path = exe;
            i.Version = version ?? "";
            i.EnvName = envName;
            i.Kind = kind;
            i.EnvPath = envPath;
            list.Add(i);
        }

        public static string VenvPython(string venvDir, bool windows)
        {
            if (string.IsNullOrEmpty(venvDir)) return null;
            string[] cands = windows
                ? new string[] { System.IO.Path.Combine(venvDir, "Scripts", "python.exe"), System.IO.Path.Combine(venvDir, "python.exe"), System.IO.Path.Combine(venvDir, "bin", "python") }
                : new string[] { System.IO.Path.Combine(venvDir, "bin", "python"), System.IO.Path.Combine(venvDir, "bin", "python3") };
            foreach (string c in cands) if (SafeFileExists(c)) return c;
            return null;
        }

        /// <summary>Reads a key from pyvenv.cfg ("version", "version_info", "uv", "home"); null if absent.</summary>
        public static string ReadPyvenvCfg(string venvDir, string key)
        {
            try
            {
                string cfg = System.IO.Path.Combine(venvDir, "pyvenv.cfg");
                if (!File.Exists(cfg)) return null;
                foreach (string raw in File.ReadAllLines(cfg))
                {
                    int eq = raw.IndexOf('=');
                    if (eq <= 0) continue;
                    if (string.Equals(raw.Substring(0, eq).Trim(), key, StringComparison.OrdinalIgnoreCase))
                        return raw.Substring(eq + 1).Trim();
                }
            }
            catch { }
            return null;
        }

        public static string ReadVenvVersion(string venvDir)
        {
            string v = ReadPyvenvCfg(venvDir, "version") ?? ReadPyvenvCfg(venvDir, "version_info");
            if (string.IsNullOrEmpty(v)) return null;
            // version_info = 3.12.1.final.0 -> 3.12.1
            string[] parts = v.Split('.');
            if (parts.Length > 3) v = parts[0] + "." + parts[1] + "." + parts[2];
            return v;
        }

        private static string ReadCondaVersion(string prefix)
        {
            try
            {
                foreach (string f in Directory.GetFiles(System.IO.Path.Combine(prefix, "conda-meta"), "python-3*.json"))
                {
                    string leaf = System.IO.Path.GetFileNameWithoutExtension(f); // python-3.11.4-h1234_0
                    string[] parts = leaf.Split('-');
                    if (parts.Length >= 2 && LooksLikeVersion(parts[1])) return parts[1];
                }
            }
            catch { }
            return null;
        }

        public static string ParseVersionOutput(string output)
        {
            if (string.IsNullOrEmpty(output)) return "";
            string s = output.Trim();
            int idx = s.IndexOf("Python ", StringComparison.OrdinalIgnoreCase);
            if (idx >= 0) s = s.Substring(idx + 7);
            int end = 0;
            while (end < s.Length && (char.IsDigit(s[end]) || s[end] == '.')) end++;
            return s.Substring(0, end).Trim('.');
        }

        private static bool LooksLikeVersion(string s)
        {
            if (string.IsNullOrEmpty(s) || !char.IsDigit(s[0])) return false;
            int dots = 0;
            foreach (char c in s)
            {
                if (c == '.') dots++;
                else if (!char.IsDigit(c)) return false;
            }
            return dots >= 1;
        }

        private static IEnumerable<string> ChildDirsWithPrefix(string dir, string prefix)
        {
            List<string> res = new List<string>();
            try
            {
                if (!Directory.Exists(dir)) return res;
                foreach (string d in Directory.GetDirectories(dir))
                    if (System.IO.Path.GetFileName(d).StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) res.Add(d);
            }
            catch { }
            return res;
        }

        private static bool FileContains(string path, string text)
        {
            try { return File.Exists(path) && File.ReadAllText(path).IndexOf(text, StringComparison.Ordinal) >= 0; }
            catch { return false; }
        }

        private static bool SafeFileExists(string p) { try { return !string.IsNullOrEmpty(p) && File.Exists(p); } catch { return false; } }
        private static bool SafeDirExists(string p) { try { return !string.IsNullOrEmpty(p) && Directory.Exists(p); } catch { return false; } }

        /// <summary>
        /// The interpreter to use when the user has not picked one: first workspace environment, else the first
        /// system interpreter, else any.
        /// </summary>
        public static PythonInterpreterInfo PickDefault(IList<PythonInterpreterInfo> all)
        {
            if (all == null || all.Count == 0) return null;
            foreach (PythonInterpreterInfo i in all) if (i.IsVirtualEnv) return i;
            foreach (PythonInterpreterInfo i in all) if (i.Kind == "system") return i;
            return all[0];
        }

        /// <summary>Builds an info for a user-entered interpreter path (Select Interpreter > Enter path).</summary>
        public static PythonInterpreterInfo FromPath(string exe, PythonDiscoveryEnvironment env)
        {
            if (string.IsNullOrEmpty(exe)) return null;
            PythonInterpreterInfo i = new PythonInterpreterInfo();
            i.Path = exe;
            i.Kind = "custom";
            try
            {
                string binDir = System.IO.Path.GetDirectoryName(exe);
                string root = System.IO.Path.GetDirectoryName(binDir);
                if (root != null && SafeFileExists(System.IO.Path.Combine(root, "pyvenv.cfg")))
                {
                    i.Kind = "venv"; i.EnvPath = root; i.EnvName = System.IO.Path.GetFileName(root); i.Version = ReadVenvVersion(root);
                }
                else if (binDir != null && SafeDirExists(System.IO.Path.Combine(binDir, "conda-meta")))
                {
                    i.Kind = "conda"; i.EnvPath = binDir; i.EnvName = System.IO.Path.GetFileName(binDir);
                }
                else if (root != null && SafeDirExists(System.IO.Path.Combine(root, "conda-meta")))
                {
                    i.Kind = "conda"; i.EnvPath = root; i.EnvName = System.IO.Path.GetFileName(root);
                }
            }
            catch { }
            if (string.IsNullOrEmpty(i.Version) && env != null && env.ProbeVersions && env.RunProcess != null)
                i.Version = ParseVersionOutput(env.RunProcess(exe, "--version", 4000));
            return i;
        }
    }

    /// <summary>
    /// Per-workspace selected interpreter (persisted in settings) plus a cache of the last discovery, shared by the
    /// status bar item, Run / Debug Python File and terminal activation on both UIs.
    /// </summary>
    public static class PythonInterpreterService
    {
        private static readonly object gate = new object();
        private static readonly Dictionary<string, List<PythonInterpreterInfo>> cache = new Dictionary<string, List<PythonInterpreterInfo>>();
        private static readonly Dictionary<string, PythonInterpreterInfo> selectedInfo = new Dictionary<string, PythonInterpreterInfo>();
        private static IdePersistentSettings boundSettings;

        /// <summary>Raised (on a background thread) after discovery finishes or the selection changes.</summary>
        public static event Action<string> Changed;

        public static void BindSettings(IdePersistentSettings settings)
        {
            boundSettings = settings;
            // Terminal activation follows the selected environment (venv or conda) when there is one.
            PythonVenvActivator.SelectedEnvironmentOverride = GetSelectedEnvironmentPath;
        }

        private static string Key(string workspaceRoot) { return DebugPaths.Key(workspaceRoot ?? ""); }

        public static List<PythonInterpreterInfo> GetCached(string workspaceRoot)
        {
            lock (gate)
            {
                List<PythonInterpreterInfo> l;
                return cache.TryGetValue(Key(workspaceRoot), out l) ? new List<PythonInterpreterInfo>(l) : null;
            }
        }

        /// <summary>Discovers synchronously (background threads only) and caches.</summary>
        public static List<PythonInterpreterInfo> Refresh(string workspaceRoot, PythonDiscoveryEnvironment env)
        {
            List<PythonInterpreterInfo> all;
            try { all = PythonInterpreterDiscovery.Discover(workspaceRoot, env); }
            catch { all = new List<PythonInterpreterInfo>(); }
            lock (gate) { cache[Key(workspaceRoot)] = all; }
            RaiseChanged(workspaceRoot);
            return all;
        }

        /// <summary>Starts discovery on a background thread; <see cref="Changed"/> fires when done.</summary>
        public static void RefreshInBackground(string workspaceRoot)
        {
            Thread t = new Thread(delegate () { Refresh(workspaceRoot, null); });
            t.IsBackground = true;
            t.Name = "PythonDiscovery";
            t.Start();
        }

        private static void RaiseChanged(string ws)
        {
            Action<string> h = Changed;
            if (h != null) { try { h(ws); } catch { } }
        }

        /// <summary>The interpreter for a workspace: the persisted choice if it still exists, else the default.</summary>
        public static PythonInterpreterInfo GetSelected(string workspaceRoot)
        {
            string chosen = boundSettings != null ? boundSettings.GetPythonInterpreterFor(workspaceRoot) : null;
            lock (gate)
            {
                PythonInterpreterInfo sel;
                if (!string.IsNullOrEmpty(chosen) && selectedInfo.TryGetValue(Key(workspaceRoot), out sel) && sel != null && sel.Path == chosen)
                    return sel;
                List<PythonInterpreterInfo> all;
                cache.TryGetValue(Key(workspaceRoot), out all);
                if (!string.IsNullOrEmpty(chosen))
                {
                    if (all != null) foreach (PythonInterpreterInfo i in all) if (i.Path == chosen) return i;
                    bool exists;
                    try { exists = File.Exists(chosen); } catch { exists = false; }
                    if (exists)
                    {
                        PythonDiscoveryEnvironment noProbe = PythonDiscoveryEnvironment.CreateDefault();
                        noProbe.ProbeVersions = false;
                        PythonInterpreterInfo info = PythonInterpreterDiscovery.FromPath(chosen, noProbe);
                        selectedInfo[Key(workspaceRoot)] = info;
                        return info;
                    }
                }
                return all != null ? PythonInterpreterDiscovery.PickDefault(all) : null;
            }
        }

        public static void Select(string workspaceRoot, PythonInterpreterInfo info)
        {
            if (info == null) return;
            lock (gate)
            {
                selectedInfo[Key(workspaceRoot)] = info;
                List<PythonInterpreterInfo> all;
                if (cache.TryGetValue(Key(workspaceRoot), out all))
                {
                    bool present = false;
                    foreach (PythonInterpreterInfo i in all) if (i.Path == info.Path) present = true;
                    if (!present) all.Insert(0, info);
                }
            }
            if (boundSettings != null)
            {
                boundSettings.SetPythonInterpreterFor(workspaceRoot, info.Path);
                try { boundSettings.Save(); } catch { }
            }
            RaiseChanged(workspaceRoot);
        }

        /// <summary>Activation target for terminals: the selected env root when it is a venv / conda env.</summary>
        public static string GetSelectedEnvironmentPath(string workspaceRoot)
        {
            try
            {
                if (boundSettings == null || string.IsNullOrEmpty(boundSettings.GetPythonInterpreterFor(workspaceRoot))) return null;
                PythonInterpreterInfo sel = GetSelected(workspaceRoot);
                if (sel == null || string.IsNullOrEmpty(sel.EnvPath)) return null;
                return sel.EnvPath;
            }
            catch { return null; }
        }

        /// <summary>Interpreter command for Run Python File when nothing is discovered yet.</summary>
        public static string FallbackCommand()
        {
            return Path.DirectorySeparatorChar == '\\' ? "python" : "python3";
        }
    }
}
