using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace codeLess.Studio
{
    /// <summary>One launch configuration (from .vscode/launch.json or generated for "Debug Active File").</summary>
    public sealed class DebugLaunchConfig
    {
        public string Name;
        /// <summary>Type as written ("debugpy", "python", "go", "lldb", "lldb-dap", "coreclr", "node", "codeless"...).</summary>
        public string Type;
        public string Request = "launch";
        /// <summary>All properties (unsubstituted for launch.json entries).</summary>
        public Dictionary<string, object> Properties = new Dictionary<string, object>(StringComparer.Ordinal);
        public bool FromLaunchJson;

        /// <summary>Normalized adapter family: python | go | lldb | coreclr | node | codeless | (other, unsupported).</summary>
        public string AdapterFamily { get { return DebugLaunchConfigurations.NormalizeType(Type); } }

        public override string ToString() { return Name ?? Type ?? "Debug"; }
    }

    /// <summary>Values for ${...} substitution in launch configurations.</summary>
    public sealed class DebugVariableContext
    {
        public string WorkspaceFolder;
        public string File;
        public int LineNumber;
        public string SelectedText;
        public string PythonInterpreter;
        public Func<string, string> GetEnv;
    }

    /// <summary>
    /// .vscode/launch.json support (JSONC with comments / trailing commas), VS Code variable substitution, the
    /// "Debug Active File" defaults and the "create launch.json" template. Strict C# 5 (shared with WinForms).
    /// </summary>
    public static class DebugLaunchConfigurations
    {
        public static string NormalizeType(string type)
        {
            string t = (type ?? "").Trim().ToLowerInvariant();
            switch (t)
            {
                case "python":
                case "debugpy":
                    return "python";
                case "go":
                    return "go";
                case "lldb":
                case "lldb-dap":
                case "lldb-vscode":
                case "codelldb":
                case "swift":
                case "rust":
                    return "lldb";
                case "coreclr":
                case "netcoredbg":
                case "dotnet":
                    return "coreclr";
                case "node":
                case "pwa-node":
                case "node-terminal":
                    return "node";
                case "codeless":
                    return "codeless";
            }
            return t;
        }

        public static string LaunchJsonPath(string workspaceRoot)
        {
            if (string.IsNullOrEmpty(workspaceRoot)) return null;
            return Path.Combine(Path.Combine(workspaceRoot, ".vscode"), "launch.json");
        }

        /// <summary>Reads .vscode/launch.json. Returns an empty list when absent; error is set for malformed files.</summary>
        public static List<DebugLaunchConfig> LoadLaunchJson(string workspaceRoot, out string error)
        {
            error = null;
            string path = LaunchJsonPath(workspaceRoot);
            if (path == null) return new List<DebugLaunchConfig>();
            string text;
            try
            {
                if (!File.Exists(path)) return new List<DebugLaunchConfig>();
                text = File.ReadAllText(path);
            }
            catch (Exception ex)
            {
                error = "Could not read launch.json: " + ex.Message;
                return new List<DebugLaunchConfig>();
            }
            return ParseLaunchJson(text, out error);
        }

        public static List<DebugLaunchConfig> ParseLaunchJson(string text, out string error)
        {
            error = null;
            List<DebugLaunchConfig> res = new List<DebugLaunchConfig>();
            object root = DapJson.TryParse(text ?? "", out error);
            if (root == null)
            {
                if (error != null) error = "launch.json: " + error;
                return res;
            }
            List<object> configs = DapJson.Arr(root, "configurations");
            if (configs == null) return res;
            foreach (object o in configs)
            {
                Dictionary<string, object> d = o as Dictionary<string, object>;
                if (d == null) continue;
                DebugLaunchConfig c = new DebugLaunchConfig();
                c.Name = DapJson.Str(d, "name") ?? DapJson.Str(d, "type") ?? "Configuration";
                c.Type = DapJson.Str(d, "type") ?? "";
                c.Request = DapJson.Str(d, "request") ?? "launch";
                c.Properties = d;
                c.FromLaunchJson = true;
                res.Add(c);
            }
            return res;
        }

        public static bool IsSupportedFamily(string family)
        {
            return family == "python" || family == "go" || family == "lldb" || family == "coreclr" || family == "node" || family == "codeless";
        }

        /// <summary>Replaces ${...} variables recursively; returns a substituted deep copy.</summary>
        public static Dictionary<string, object> Substitute(Dictionary<string, object> props, DebugVariableContext ctx)
        {
            return (Dictionary<string, object>)SubstituteValue(DapJson.DeepClone(props), ctx);
        }

        private static object SubstituteValue(object v, DebugVariableContext ctx)
        {
            string s = v as string;
            if (s != null) return SubstituteString(s, ctx);
            Dictionary<string, object> d = v as Dictionary<string, object>;
            if (d != null)
            {
                List<string> keys = new List<string>(d.Keys);
                foreach (string k in keys) d[k] = SubstituteValue(d[k], ctx);
                return d;
            }
            List<object> l = v as List<object>;
            if (l != null)
            {
                for (int i = 0; i < l.Count; i++) l[i] = SubstituteValue(l[i], ctx);
                return l;
            }
            return v;
        }

        public static string SubstituteString(string s, DebugVariableContext ctx)
        {
            if (string.IsNullOrEmpty(s) || s.IndexOf("${", StringComparison.Ordinal) < 0) return s;
            StringBuilder sb = new StringBuilder();
            int i = 0;
            while (i < s.Length)
            {
                int start = s.IndexOf("${", i, StringComparison.Ordinal);
                if (start < 0) { sb.Append(s, i, s.Length - i); break; }
                int end = s.IndexOf('}', start + 2);
                if (end < 0) { sb.Append(s, i, s.Length - i); break; }
                sb.Append(s, i, start - i);
                string name = s.Substring(start + 2, end - start - 2);
                string val = ResolveVariable(name, ctx);
                sb.Append(val ?? s.Substring(start, end - start + 1));
                i = end + 1;
            }
            return sb.ToString();
        }

        private static string ResolveVariable(string name, DebugVariableContext ctx)
        {
            string ws = ctx != null ? ctx.WorkspaceFolder : null;
            string file = ctx != null ? ctx.File : null;
            if (name.StartsWith("env:", StringComparison.Ordinal))
            {
                string key = name.Substring(4);
                string v = ctx != null && ctx.GetEnv != null ? ctx.GetEnv(key) : Environment.GetEnvironmentVariable(key);
                return v ?? "";
            }
            if (name.StartsWith("workspaceFolder:", StringComparison.Ordinal)) return ws ?? "";
            switch (name)
            {
                case "workspaceFolder":
                case "workspaceRoot":
                    return ws ?? "";
                case "workspaceFolderBasename":
                    return string.IsNullOrEmpty(ws) ? "" : Path.GetFileName(ws.TrimEnd('/', '\\'));
                case "file":
                    return file ?? "";
                case "fileBasename":
                    return string.IsNullOrEmpty(file) ? "" : Path.GetFileName(file);
                case "fileBasenameNoExtension":
                    return string.IsNullOrEmpty(file) ? "" : Path.GetFileNameWithoutExtension(file);
                case "fileExtname":
                    return string.IsNullOrEmpty(file) ? "" : Path.GetExtension(file);
                case "fileDirname":
                    return string.IsNullOrEmpty(file) ? "" : (Path.GetDirectoryName(file) ?? "");
                case "fileDirnameBasename":
                    return string.IsNullOrEmpty(file) ? "" : Path.GetFileName(Path.GetDirectoryName(file) ?? "");
                case "relativeFile":
                    return RelativeTo(ws, file);
                case "relativeFileDirname":
                    return string.IsNullOrEmpty(file) ? "" : RelativeTo(ws, Path.GetDirectoryName(file));
                case "cwd":
                    return ws ?? Environment.CurrentDirectory;
                case "lineNumber":
                    return ctx != null && ctx.LineNumber > 0 ? ctx.LineNumber.ToString(CultureInfo.InvariantCulture) : "";
                case "selectedText":
                    return ctx != null ? (ctx.SelectedText ?? "") : "";
                case "pathSeparator":
                case "/":
                    return Path.DirectorySeparatorChar.ToString();
                case "userHome":
                    return Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                case "command:python.interpreterPath":
                case "command:python.interpreter":
                    return ctx != null ? ctx.PythonInterpreter : null;
            }
            return null; // unknown: left untouched (VS Code would prompt / fail)
        }

        private static string RelativeTo(string root, string path)
        {
            if (string.IsNullOrEmpty(path)) return "";
            if (string.IsNullOrEmpty(root)) return path;
            string r = root.TrimEnd('/', '\\');
            if (path.Length > r.Length && path.StartsWith(r, DebugPaths.IsWindows ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
                return path.Substring(r.Length + 1);
            return path;
        }

        /// <summary>Language id of a file for the Debug Active File defaults (null = not debuggable).</summary>
        public static string FamilyForFile(string filePath)
        {
            string ext = (Path.GetExtension(filePath ?? "") ?? "").ToLowerInvariant();
            switch (ext)
            {
                case ".py":
                case ".pyw":
                    return "python";
                case ".go":
                    return "go";
                case ".c":
                case ".cc":
                case ".cpp":
                case ".cxx":
                case ".m":
                case ".mm":
                case ".rs":
                case ".swift":
                    return "lldb";
                case ".cs":
                case ".csproj":
                case ".fs":
                    return "coreclr";
                case ".js":
                case ".mjs":
                case ".cjs":
                case ".ts":
                    return "node";
            }
            return null;
        }

        /// <summary>
        /// "Debug Active File": sensible defaults when there is no launch.json (VS Code's dynamic configurations).
        /// Compiled languages debug the most likely build output; returns null with a reason when not possible.
        /// </summary>
        public static DebugLaunchConfig CreateDefaultForFile(string filePath, string workspaceRoot, out string error)
        {
            error = null;
            string family = FamilyForFile(filePath);
            if (family == null)
            {
                error = "No debugger is available for " + (string.IsNullOrEmpty(filePath) ? "this file" : Path.GetFileName(filePath)) +
                        ". Supported: Python, Go, C/C++/Rust/Swift (lldb-dap), C# (netcoredbg), or add .vscode/launch.json.";
                return null;
            }
            string dir = Path.GetDirectoryName(filePath) ?? workspaceRoot;
            string cwd = !string.IsNullOrEmpty(workspaceRoot) ? workspaceRoot : dir;
            DebugLaunchConfig c = new DebugLaunchConfig();
            c.Request = "launch";
            Dictionary<string, object> p = c.Properties;
            switch (family)
            {
                case "python":
                    c.Name = "Python Debugger: Current File";
                    c.Type = "debugpy";
                    p["program"] = filePath;
                    p["console"] = "integratedTerminal";
                    p["cwd"] = cwd;
                    p["justMyCode"] = true;
                    break;
                case "go":
                    c.Name = "Go: Launch Package";
                    c.Type = "go";
                    p["mode"] = "debug";
                    p["program"] = dir;
                    p["cwd"] = cwd;
                    break;
                case "lldb":
                    {
                        string ext = (Path.GetExtension(filePath) ?? "").ToLowerInvariant();
                        string program = GuessNativeProgram(filePath, workspaceRoot, ext);
                        if (program == null)
                        {
                            error = "Build the program first (with debug info, e.g. clang -g / cargo build), then debug it, or add a launch.json with \"program\".";
                            return null;
                        }
                        c.Name = "LLDB: Launch " + Path.GetFileName(program);
                        c.Type = "lldb-dap";
                        p["program"] = program;
                        p["cwd"] = cwd;
                        p["args"] = new List<object>();
                        p["stopOnEntry"] = false;
                        break;
                    }
                case "coreclr":
                    {
                        string dll = GuessDotnetProgram(workspaceRoot, dir);
                        if (dll == null)
                        {
                            error = "Run 'dotnet build' first; no Debug build output (bin/Debug/*/*.dll) was found.";
                            return null;
                        }
                        c.Name = ".NET: Launch " + Path.GetFileName(dll);
                        c.Type = "coreclr";
                        p["program"] = dll;
                        p["cwd"] = cwd;
                        p["args"] = new List<object>();
                        p["stopAtEntry"] = false;
                        p["console"] = "internalConsole";
                        break;
                    }
                case "node":
                    c.Name = "Node.js: Launch Current File";
                    c.Type = "pwa-node";
                    p["program"] = filePath;
                    p["cwd"] = cwd;
                    p["console"] = "integratedTerminal";
                    break;
            }
            p["name"] = c.Name;
            p["type"] = c.Type;
            p["request"] = c.Request;
            return c;
        }

        private static string GuessNativeProgram(string filePath, string workspaceRoot, string ext)
        {
            List<string> cands = new List<string>();
            string dir = Path.GetDirectoryName(filePath) ?? "";
            string stem = Path.GetFileNameWithoutExtension(filePath);
            bool win = DebugPaths.IsWindows;
            string exeExt = win ? ".exe" : "";
            if (ext == ".rs" && !string.IsNullOrEmpty(workspaceRoot))
            {
                string wsName = Path.GetFileName(workspaceRoot.TrimEnd('/', '\\'));
                string cargoName = ReadCargoPackageName(workspaceRoot) ?? wsName;
                cands.Add(Path.Combine(Path.Combine(Path.Combine(workspaceRoot, "target"), "debug"), cargoName + exeExt));
                cands.Add(Path.Combine(Path.Combine(Path.Combine(workspaceRoot, "target"), "debug"), cargoName.Replace('-', '_') + exeExt));
            }
            if (ext == ".swift" && !string.IsNullOrEmpty(workspaceRoot))
                cands.Add(Path.Combine(Path.Combine(Path.Combine(workspaceRoot, ".build"), "debug"), Path.GetFileName(workspaceRoot.TrimEnd('/', '\\'))));
            cands.Add(Path.Combine(dir, stem + exeExt));
            cands.Add(Path.Combine(dir, "a.out"));
            if (!string.IsNullOrEmpty(workspaceRoot))
            {
                cands.Add(Path.Combine(Path.Combine(workspaceRoot, "build"), stem + exeExt));
                cands.Add(Path.Combine(workspaceRoot, stem + exeExt));
            }
            foreach (string c in cands)
            {
                try { if (File.Exists(c)) return c; } catch { }
            }
            return null;
        }

        private static string ReadCargoPackageName(string root)
        {
            try
            {
                string toml = Path.Combine(root, "Cargo.toml");
                if (!File.Exists(toml)) return null;
                bool inPackage = false;
                foreach (string raw in File.ReadAllLines(toml))
                {
                    string line = raw.Trim();
                    if (line.StartsWith("[")) { inPackage = line == "[package]"; continue; }
                    if (inPackage && line.StartsWith("name"))
                    {
                        int eq = line.IndexOf('=');
                        if (eq > 0) return line.Substring(eq + 1).Trim().Trim('"', '\'');
                    }
                }
            }
            catch { }
            return null;
        }

        private static string GuessDotnetProgram(string workspaceRoot, string fileDir)
        {
            foreach (string start in new string[] { fileDir, workspaceRoot })
            {
                if (string.IsNullOrEmpty(start)) continue;
                string cur = start;
                for (int guard = 0; guard < 6 && !string.IsNullOrEmpty(cur); guard++)
                {
                    try
                    {
                        string[] projs = Directory.GetFiles(cur, "*.csproj");
                        if (projs.Length > 0)
                        {
                            string name = Path.GetFileNameWithoutExtension(projs[0]);
                            string debugDir = Path.Combine(Path.Combine(cur, "bin"), "Debug");
                            if (Directory.Exists(debugDir))
                            {
                                string best = null;
                                DateTime bestTime = DateTime.MinValue;
                                foreach (string tfm in Directory.GetDirectories(debugDir))
                                {
                                    string dll = Path.Combine(tfm, name + ".dll");
                                    if (File.Exists(dll) && File.GetLastWriteTimeUtc(dll) > bestTime) { best = dll; bestTime = File.GetLastWriteTimeUtc(dll); }
                                }
                                if (best != null) return best;
                            }
                            return null;
                        }
                    }
                    catch { }
                    if (workspaceRoot != null && DebugPaths.Same(cur, workspaceRoot)) break;
                    try { cur = Path.GetDirectoryName(cur); } catch { break; }
                }
            }
            return null;
        }

        /// <summary>Contents for "create launch.json" (JSONC like VS Code's), tailored to the active file's language.</summary>
        public static string BuildLaunchJsonTemplate(string activeFileFamily)
        {
            StringBuilder sb = new StringBuilder();
            sb.Append("{\n");
            sb.Append("    // Run and Debug configurations (same format as VS Code's launch.json; comments and trailing commas are fine).\n");
            sb.Append("    // Supported types: debugpy (Python), go (dlv dap), lldb-dap / lldb (C, C++, Rust, Swift), coreclr (netcoredbg),\n");
            sb.Append("    // codeless (any DAP adapter: \"adapter\": { \"command\": \"...\", \"args\": [ ... ] }).\n");
            sb.Append("    // Variables: ${workspaceFolder} ${file} ${fileDirname} ${fileBasenameNoExtension} ${env:NAME}\n");
            sb.Append("    \"version\": \"0.2.0\",\n");
            sb.Append("    \"configurations\": [\n");
            List<string> entries = new List<string>();
            string py =
                "        {\n" +
                "            \"name\": \"Python Debugger: Current File\",\n" +
                "            \"type\": \"debugpy\",\n" +
                "            \"request\": \"launch\",\n" +
                "            \"program\": \"${file}\",\n" +
                "            \"console\": \"integratedTerminal\",\n" +
                "            \"justMyCode\": true\n" +
                "        }";
            string go =
                "        {\n" +
                "            \"name\": \"Go: Launch Package\",\n" +
                "            \"type\": \"go\",\n" +
                "            \"request\": \"launch\",\n" +
                "            \"mode\": \"debug\",\n" +
                "            \"program\": \"${fileDirname}\"\n" +
                "        }";
            string lldb =
                "        {\n" +
                "            \"name\": \"LLDB: Launch\",\n" +
                "            \"type\": \"lldb-dap\",\n" +
                "            \"request\": \"launch\",\n" +
                "            \"program\": \"${fileDirname}/${fileBasenameNoExtension}\",\n" +
                "            \"args\": [],\n" +
                "            \"cwd\": \"${workspaceFolder}\"\n" +
                "        }";
            string net =
                "        {\n" +
                "            \"name\": \".NET: Launch\",\n" +
                "            \"type\": \"coreclr\",\n" +
                "            \"request\": \"launch\",\n" +
                "            \"program\": \"${workspaceFolder}/bin/Debug/net8.0/${workspaceFolderBasename}.dll\",\n" +
                "            \"args\": [],\n" +
                "            \"cwd\": \"${workspaceFolder}\",\n" +
                "            \"stopAtEntry\": false\n" +
                "        }";
            switch (activeFileFamily)
            {
                case "go": entries.Add(go); entries.Add(py); break;
                case "lldb": entries.Add(lldb); entries.Add(py); break;
                case "coreclr": entries.Add(net); entries.Add(py); break;
                default: entries.Add(py); break;
            }
            sb.Append(string.Join(",\n", entries.ToArray()));
            sb.Append("\n    ]\n}\n");
            return sb.ToString();
        }

        /// <summary>Creates .vscode/launch.json if missing. Returns its path, or null with an error.</summary>
        public static string CreateLaunchJson(string workspaceRoot, string activeFileFamily, out string error)
        {
            error = null;
            if (string.IsNullOrEmpty(workspaceRoot) || !Directory.Exists(workspaceRoot))
            {
                error = "Open a folder first: launch.json lives in <folder>/.vscode/.";
                return null;
            }
            string path = LaunchJsonPath(workspaceRoot);
            try
            {
                if (File.Exists(path)) return path;
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                File.WriteAllText(path, BuildLaunchJsonTemplate(activeFileFamily), new UTF8Encoding(false));
                return path;
            }
            catch (Exception ex)
            {
                error = "Could not create launch.json: " + ex.Message;
                return null;
            }
        }

        /// <summary>
        /// Final launch arguments for the adapter: substituted properties plus family defaults (python interpreter,
        /// cwd). Removes client-only keys ("adapter" for codeless, "preLaunchTask").
        /// </summary>
        public static Dictionary<string, object> PrepareLaunchArguments(DebugLaunchConfig config, DebugVariableContext ctx)
        {
            Dictionary<string, object> args = Substitute(MergePlatformSection(config.Properties, TaskConfiguration.CurrentPlatformKey), ctx);
            string fam = config.AdapterFamily;
            if (!args.ContainsKey("name")) args["name"] = config.Name ?? "Debug";
            if (!args.ContainsKey("type")) args["type"] = config.Type;
            if (!args.ContainsKey("request")) args["request"] = config.Request;
            if (fam == "python")
            {
                if (!args.ContainsKey("python") && ctx != null && !string.IsNullOrEmpty(ctx.PythonInterpreter))
                    args["python"] = ctx.PythonInterpreter;
                if (!args.ContainsKey("cwd") && ctx != null && !string.IsNullOrEmpty(ctx.WorkspaceFolder) && string.Equals(config.Request, "launch", StringComparison.OrdinalIgnoreCase))
                    args["cwd"] = ctx.WorkspaceFolder;
                if (!args.ContainsKey("console")) args["console"] = "integratedTerminal";
                // debugpy rejects the legacy "internalConsole"+"redirectOutput" combos less often with explicit values.
                if (!args.ContainsKey("redirectOutput") && DapJson.Str(args, "console") == "internalConsole") args["redirectOutput"] = true;
            }
            else if (fam == "go")
            {
                if (!args.ContainsKey("mode")) args["mode"] = "debug";
                if (!args.ContainsKey("cwd") && ctx != null && !string.IsNullOrEmpty(ctx.WorkspaceFolder)) args["cwd"] = ctx.WorkspaceFolder;
            }
            else if (fam == "lldb" || fam == "coreclr")
            {
                if (!args.ContainsKey("cwd") && ctx != null && !string.IsNullOrEmpty(ctx.WorkspaceFolder)) args["cwd"] = ctx.WorkspaceFolder;
            }
            args.Remove("preLaunchTask");
            args.Remove("postDebugTask");
            args.Remove("windows");
            args.Remove("osx");
            args.Remove("linux");
            return args;
        }

        /// <summary>
        /// launch.json platform sections ("windows" / "osx" / "linux"): their properties override the configuration's on
        /// that platform (VS Code semantics; nested objects such as "env" are replaced, not merged).
        /// </summary>
        public static Dictionary<string, object> MergePlatformSection(Dictionary<string, object> props, string platformKey)
        {
            Dictionary<string, object> merged = new Dictionary<string, object>(StringComparer.Ordinal);
            if (props == null) return merged;
            foreach (KeyValuePair<string, object> kv in props)
                if (kv.Key != "windows" && kv.Key != "osx" && kv.Key != "linux") merged[kv.Key] = kv.Value;
            Dictionary<string, object> plat = DapJson.Obj(props, platformKey);
            if (plat != null) foreach (KeyValuePair<string, object> kv in plat) merged[kv.Key] = kv.Value;
            return merged;
        }

        /// <summary>preLaunchTask / postDebugTask label (platform section aware), or null. A task object ({ "task": label }) is accepted too.</summary>
        public static string GetTaskLabel(DebugLaunchConfig config, string key)
        {
            if (config == null || config.Properties == null) return null;
            Dictionary<string, object> merged = MergePlatformSection(config.Properties, TaskConfiguration.CurrentPlatformKey);
            object v;
            if (!merged.TryGetValue(key, out v) || v == null) return null;
            string s = v as string;
            if (s == null) s = DapJson.Str(v as Dictionary<string, object>, "task");
            if (s == null) return null;
            s = s.Trim();
            return s.Length == 0 ? null : s;
        }
    }
}
