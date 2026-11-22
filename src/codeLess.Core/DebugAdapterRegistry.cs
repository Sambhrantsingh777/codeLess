using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace codeLess.Studio
{
    /// <summary>Result of resolving an adapter for a configuration.</summary>
    public sealed class DebugAdapterResolution
    {
        public DebugAdapterDescriptor Descriptor;
        /// <summary>Why the adapter can't be used (null when Descriptor is set).</summary>
        public string Error;
        /// <summary>Optional one-line hint ("Install with: ...").</summary>
        public string Hint;
        /// <summary>Shell command that installs the missing piece (typed into the terminal only when the user agrees).</summary>
        public string InstallCommand;
    }

    /// <summary>Injectable lookups so adapter detection is testable.</summary>
    public sealed class DebugAdapterEnvironment
    {
        public Func<string, string> FindExecutable;
        public Func<string, string, int, string> RunProcess;
        public string HomeDir;
        public bool IsWindows;
        public bool IsMac;
        public List<UserDebugAdapterEntry> UserAdapters;

        public static DebugAdapterEnvironment CreateDefault(IdePersistentSettings settings)
        {
            DebugAdapterEnvironment e = new DebugAdapterEnvironment();
            e.FindExecutable = ToolLocator.Find;
            e.RunProcess = ToolLocator.RunAndCapture;
            e.HomeDir = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            e.IsWindows = Path.DirectorySeparatorChar == '\\';
            e.IsMac = !e.IsWindows && Directory.Exists("/System/Library/CoreServices") && Directory.Exists("/Applications");
            e.UserAdapters = settings != null ? settings.DebugAdapters : null;
            return e;
        }
    }

    /// <summary>
    /// Debug adapter registry with auto-detection: Python (debugpy, run by the selected interpreter), Go (dlv dap),
    /// C/C++/Rust/Swift (lldb-dap, codelldb), .NET (netcoredbg), any adapter via "type": "codeless" or settings.
    /// Node.js needs a user-configured vscode-js-debug dapDebugServer.js (codeLess does not speak the Chrome DevTools
    /// protocol behind "node --inspect" directly). Blocking (may run short processes): call off the UI thread.
    /// </summary>
    public static class DebugAdapterRegistry
    {
        public static DebugAdapterResolution Resolve(DebugLaunchConfig config, Dictionary<string, object> launchArgs, string pythonInterpreter, DebugAdapterEnvironment env)
        {
            UserShellEnvironment.WaitForResolution(8000);
            DebugAdapterResolution r = new DebugAdapterResolution();
            if (env == null) env = DebugAdapterEnvironment.CreateDefault(null);
            string family = config.AdapterFamily;
            string rawType = (config.Type ?? "").Trim();

            // "debugServer": connect to an adapter that is already listening (VS Code convention).
            long debugServer = DapJson.Long(launchArgs, "debugServer", 0);
            if (debugServer > 0 && debugServer < 65536)
            {
                DebugAdapterDescriptor d = NewDescriptor(family, "Debug server :" + debugServer, null);
                d.UseTcp = true;
                d.Port = (int)debugServer;
                r.Descriptor = d;
                return r;
            }

            UserDebugAdapterEntry user = FindUserAdapter(env.UserAdapters, rawType, family);
            if (user != null && (!string.IsNullOrEmpty(user.Command) || user.Port > 0))
            {
                DebugAdapterDescriptor d = NewDescriptor(family, (user.Type ?? family) + " (user adapter)", user.Command);
                d.Args = SplitArgs(user.Args);
                d.UseTcp = user.UseTcp || user.Port > 0;
                d.Port = user.Port;
                d.AdapterId = rawType.Length > 0 ? rawType : family;
                r.Descriptor = d;
                return r;
            }

            switch (family)
            {
                case "python": return ResolvePython(launchArgs, pythonInterpreter, env);
                case "go": return ResolveGo(env);
                case "lldb": return ResolveLldb(rawType, env);
                case "coreclr": return ResolveNetcoredbg(env);
                case "node":
                    r.Error = "Node.js debugging needs vscode-js-debug.";
                    r.Hint = "Download js-debug (github.com/microsoft/vscode-js-debug releases, js-debug-dap-*.tar.gz) and add a debug adapter in settings: " +
                             "Type \"node\", Command \"node\", Args \"<path>/js-debug/src/dapDebugServer.js ${port} 127.0.0.1\", UseTcp true.";
                    return r;
                case "codeless": return ResolveGeneric(config, launchArgs);
            }
            r.Error = "Debug type '" + rawType + "' is not supported.";
            r.Hint = "Supported types: debugpy, go, lldb-dap / lldb, coreclr, codeless; or add a debug adapter in settings.";
            return r;
        }

        private static DebugAdapterDescriptor NewDescriptor(string type, string name, string command)
        {
            DebugAdapterDescriptor d = new DebugAdapterDescriptor();
            d.Type = type;
            d.DisplayName = name;
            d.Command = command;
            d.AdapterId = type;
            return d;
        }

        private static UserDebugAdapterEntry FindUserAdapter(List<UserDebugAdapterEntry> list, string rawType, string family)
        {
            if (list == null) return null;
            foreach (UserDebugAdapterEntry e in list)
                if (e != null && string.Equals(e.Type, rawType, StringComparison.OrdinalIgnoreCase)) return e;
            foreach (UserDebugAdapterEntry e in list)
                if (e != null && string.Equals(DebugLaunchConfigurations.NormalizeType(e.Type), family, StringComparison.OrdinalIgnoreCase)) return e;
            return null;
        }

        public static List<string> SplitArgs(string s)
        {
            List<string> res = new List<string>();
            if (string.IsNullOrEmpty(s)) return res;
            StringBuilder cur = new StringBuilder();
            bool inQ = false, any = false;
            foreach (char c in s)
            {
                if (c == '"') { inQ = !inQ; any = true; continue; }
                if (!inQ && (c == ' ' || c == '\t'))
                {
                    if (cur.Length > 0 || any) { res.Add(cur.ToString()); cur.Length = 0; any = false; }
                    continue;
                }
                cur.Append(c);
            }
            if (cur.Length > 0 || any) res.Add(cur.ToString());
            return res;
        }

        public static string PythonFromArgs(Dictionary<string, object> launchArgs)
        {
            if (launchArgs == null) return null;
            object v;
            if (!launchArgs.TryGetValue("python", out v) || v == null) return null;
            string s = v as string;
            if (!string.IsNullOrEmpty(s)) return s;
            List<object> l = v as List<object>;
            if (l != null && l.Count > 0) return l[0] as string;
            return null;
        }

        /// <summary>Shell-safe "python -m pip install debugpy" for the given interpreter.</summary>
        public static string BuildPipInstallCommand(string python, bool windows)
        {
            if (windows)
                return (python.IndexOf(' ') >= 0 ? "& \"" + python + "\"" : python) + " -m pip install debugpy";
            return PythonVenvActivator.QuotePosix(python) + " -m pip install debugpy";
        }

        private static DebugAdapterResolution ResolvePython(Dictionary<string, object> launchArgs, string pythonInterpreter, DebugAdapterEnvironment env)
        {
            DebugAdapterResolution r = new DebugAdapterResolution();
            string python = PythonFromArgs(launchArgs);
            if (string.IsNullOrEmpty(python)) python = pythonInterpreter;
            if (string.IsNullOrEmpty(python) && env.FindExecutable != null)
                python = env.FindExecutable(env.IsWindows ? "python" : "python3") ?? env.FindExecutable("python") ?? (env.IsWindows ? env.FindExecutable("py") : null);
            if (string.IsNullOrEmpty(python))
            {
                r.Error = "No Python interpreter was found.";
                r.Hint = env.IsWindows ? "Install Python from python.org (or 'winget install Python.Python.3.12'), then pick it with 'Python: Select Interpreter'."
                                       : "Install Python 3 (e.g. 'brew install python' or your package manager), then pick it with 'Python: Select Interpreter'.";
                return r;
            }
            if (env.RunProcess != null)
            {
                string o = env.RunProcess(python, "-c \"import debugpy,sys;sys.stdout.write('debugpy-ok')\"", 15000);
                if (o == null || o.IndexOf("debugpy-ok", StringComparison.Ordinal) < 0)
                {
                    r.Error = "debugpy is not installed for " + python + ".";
                    r.Hint = "codeLess can install it into this environment with pip.";
                    r.InstallCommand = BuildPipInstallCommand(python, env.IsWindows);
                    return r;
                }
            }
            DebugAdapterDescriptor d = NewDescriptor("python", "debugpy (" + python + ")", python);
            d.Args.Add("-m");
            d.Args.Add("debugpy.adapter");
            d.AdapterId = "debugpy";
            r.Descriptor = d;
            return r;
        }

        private static DebugAdapterResolution ResolveGo(DebugAdapterEnvironment env)
        {
            DebugAdapterResolution r = new DebugAdapterResolution();
            string dlv = env.FindExecutable != null ? env.FindExecutable("dlv") : null;
            if (dlv == null)
            {
                r.Error = "Delve (dlv) was not found.";
                r.Hint = "Install it with: go install github.com/go-delve/delve/cmd/dlv@latest";
                r.InstallCommand = "go install github.com/go-delve/delve/cmd/dlv@latest";
                return r;
            }
            DebugAdapterDescriptor d = NewDescriptor("go", "Delve (dlv dap)", dlv);
            d.Args.Add("dap");
            d.Args.Add("--listen=127.0.0.1:${port}");
            d.UseTcp = true;
            d.AdapterId = "go";
            r.Descriptor = d;
            return r;
        }

        private static string FindCodelldb(DebugAdapterEnvironment env)
        {
            string onPath = env.FindExecutable != null ? env.FindExecutable("codelldb") : null;
            if (onPath != null) return onPath;
            foreach (string extRoot in new string[] { ".vscode", ".vscode-oss", ".cursor", ".windsurf" })
            {
                try
                {
                    string dir = Path.Combine(Path.Combine(env.HomeDir ?? "", extRoot), "extensions");
                    if (!Directory.Exists(dir)) continue;
                    string[] cands = Directory.GetDirectories(dir, "vadimcn.vscode-lldb-*");
                    Array.Sort(cands, StringComparer.OrdinalIgnoreCase);
                    for (int i = cands.Length - 1; i >= 0; i--)
                    {
                        string exe = Path.Combine(Path.Combine(cands[i], "adapter"), env.IsWindows ? "codelldb.exe" : "codelldb");
                        if (File.Exists(exe)) return exe;
                    }
                }
                catch { }
            }
            return null;
        }

        private static string FindLldbDap(DebugAdapterEnvironment env)
        {
            if (env.FindExecutable != null)
            {
                string p = env.FindExecutable("lldb-dap") ?? env.FindExecutable("lldb-vscode");
                if (p != null) return p;
            }
            if (env.IsMac && env.RunProcess != null)
            {
                string o = env.RunProcess("/usr/bin/xcrun", "-f lldb-dap", 8000);
                if (!string.IsNullOrWhiteSpace(o))
                {
                    string path = o.Trim().Split('\n')[0].Trim();
                    if (File.Exists(path)) return path;
                }
            }
            return null;
        }

        private static DebugAdapterResolution ResolveLldb(string rawType, DebugAdapterEnvironment env)
        {
            DebugAdapterResolution r = new DebugAdapterResolution();
            bool wantsCodelldb = string.Equals(rawType, "lldb", StringComparison.OrdinalIgnoreCase) || string.Equals(rawType, "codelldb", StringComparison.OrdinalIgnoreCase);
            string codelldb = FindCodelldb(env);
            string lldbDap = FindLldbDap(env);
            if (codelldb != null && (wantsCodelldb || lldbDap == null))
            {
                DebugAdapterDescriptor d = NewDescriptor("lldb", "CodeLLDB", codelldb);
                d.Args.Add("--port");
                d.Args.Add("${port}");
                d.UseTcp = true;
                d.AdapterId = "lldb";
                r.Descriptor = d;
                return r;
            }
            if (lldbDap != null)
            {
                DebugAdapterDescriptor d = NewDescriptor("lldb", "lldb-dap", lldbDap);
                d.AdapterId = "lldb-dap";
                r.Descriptor = d;
                return r;
            }
            r.Error = "No LLDB debug adapter (lldb-dap or codelldb) was found.";
            r.Hint = env.IsMac ? "Install the Xcode Command Line Tools: xcode-select --install"
                   : env.IsWindows ? "Install LLVM (winget install LLVM.LLVM) which ships lldb-dap, or CodeLLDB."
                   : "Install lldb (e.g. 'sudo apt install lldb') which ships lldb-dap, or CodeLLDB.";
            return r;
        }

        private static DebugAdapterResolution ResolveNetcoredbg(DebugAdapterEnvironment env)
        {
            DebugAdapterResolution r = new DebugAdapterResolution();
            string exe = env.FindExecutable != null ? env.FindExecutable("netcoredbg") : null;
            if (exe == null)
            {
                foreach (string c in new string[] { Path.Combine(Path.Combine(env.HomeDir ?? "", "netcoredbg"), env.IsWindows ? "netcoredbg.exe" : "netcoredbg") })
                    if (File.Exists(c)) exe = c;
            }
            if (exe == null)
            {
                r.Error = "netcoredbg was not found.";
                r.Hint = "Download netcoredbg (github.com/Samsung/netcoredbg/releases) and put it on PATH or in ~/netcoredbg/.";
                return r;
            }
            DebugAdapterDescriptor d = NewDescriptor("coreclr", "netcoredbg", exe);
            d.Args.Add("--interpreter=vscode");
            d.AdapterId = "coreclr";
            r.Descriptor = d;
            return r;
        }

        private static DebugAdapterResolution ResolveGeneric(DebugLaunchConfig config, Dictionary<string, object> launchArgs)
        {
            DebugAdapterResolution r = new DebugAdapterResolution();
            // launchArgs is already substituted; the client-only "adapter" block is removed before launch.
            Dictionary<string, object> a = DapJson.Obj(launchArgs, "adapter") ?? DapJson.Obj(config.Properties, "adapter");
            if (launchArgs != null) launchArgs.Remove("adapter");
            string command = DapJson.Str(a, "command");
            int port = DapJson.Int(a, "port", 0);
            if (string.IsNullOrEmpty(command) && port <= 0)
            {
                r.Error = "A \"codeless\" configuration needs \"adapter\": { \"command\": \"...\", \"args\": [ ... ] } (or \"port\").";
                return r;
            }
            DebugAdapterDescriptor d = NewDescriptor("codeless", "Custom adapter", string.IsNullOrEmpty(command) ? null : command);
            List<object> args = DapJson.Arr(a, "args");
            if (args != null) foreach (object o in args) d.Args.Add(Convert.ToString(o, CultureInfo.InvariantCulture));
            d.UseTcp = DapJson.Bool(a, "tcp", false) || port > 0;
            d.Port = port;
            d.AdapterId = DapJson.Str(a, "adapterID") ?? "codeless";
            r.Descriptor = d;
            return r;
        }
    }
}
