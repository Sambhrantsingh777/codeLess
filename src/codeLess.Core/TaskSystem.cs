using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace codeLess.Studio
{
    /// <summary>One runnable task ("tasks.json" entry or auto-detected), VS Code tasks schema 2.0 subset.</summary>
    public sealed class TaskDefinition
    {
        public string Label = "";
        /// <summary>"shell" or "process" (auto-detected tasks are shell tasks).</summary>
        public string Type = "shell";
        public string Command = "";
        public List<string> Args = new List<string>();
        /// <summary>options.cwd (unsubstituted).</summary>
        public string Cwd;
        public Dictionary<string, string> Env = new Dictionary<string, string>(StringComparer.Ordinal);
        /// <summary>"build", "test" or null.</summary>
        public string Group;
        public bool IsDefault;
        /// <summary>Problem matcher names ("$tsc") or inline definitions (Dictionary) from tasks.json.</summary>
        public List<object> ProblemMatchers = new List<object>();
        /// <summary>"Workspace" for tasks.json, otherwise the detector ("npm", "make", "dotnet", "cargo", "go", "python").</summary>
        public string Source = "Workspace";
        public string Detail = "";
        public List<string> DependsOn = new List<string>();
        public bool DependsOrderSequence = true;
        public bool IsBackground;
        /// <summary>presentation.panel: "shared" (default), "dedicated" or "new".</summary>
        public string Panel = "shared";
        public bool Hide;

        public string DisplayLabel
        {
            get { return Source == "Workspace" || string.IsNullOrEmpty(Source) ? Label : Source + ": " + Label; }
        }

        public override string ToString() { return DisplayLabel; }
    }

    /// <summary>
    /// .vscode/tasks.json reading (JSONC via DapJson), variable substitution (shared with launch.json), command line
    /// building and auto-detection of npm / Makefile / dotnet / cargo / go / pytest tasks. Strict C# 5.
    /// </summary>
    public static class TaskConfiguration
    {
        public static string TasksJsonPath(string workspaceRoot)
        {
            return string.IsNullOrEmpty(workspaceRoot) ? null : Path.Combine(Path.Combine(workspaceRoot, ".vscode"), "tasks.json");
        }

        public static string CurrentPlatformKey
        {
            get
            {
                if (Path.DirectorySeparatorChar == '\\') return "windows";
                if (Directory.Exists("/System/Library") && Directory.Exists("/Applications")) return "osx";
                return "linux";
            }
        }

        public static List<TaskDefinition> LoadTasksJson(string workspaceRoot, out string error)
        {
            error = null;
            string path = TasksJsonPath(workspaceRoot);
            if (path == null || !File.Exists(path)) return new List<TaskDefinition>();
            string text;
            try { text = File.ReadAllText(path); }
            catch (Exception ex) { error = "tasks.json: " + ex.Message; return new List<TaskDefinition>(); }
            return ParseTasksJson(text, CurrentPlatformKey, out error);
        }

        public static List<TaskDefinition> ParseTasksJson(string text, string platformKey, out string error)
        {
            error = null;
            List<TaskDefinition> res = new List<TaskDefinition>();
            object root = DapJson.TryParse(text ?? "", out error);
            if (root == null)
            {
                if (error != null) error = "tasks.json: " + error;
                return res;
            }
            Dictionary<string, object> rootDict = root as Dictionary<string, object>;
            // Top-level defaults (command/args/options/problemMatcher shared by tasks) and platform overrides.
            Dictionary<string, object> globals = new Dictionary<string, object>(StringComparer.Ordinal);
            if (rootDict != null)
            {
                foreach (KeyValuePair<string, object> kv in rootDict)
                    if (kv.Key != "tasks" && kv.Key != "version" && kv.Key != "inputs") globals[kv.Key] = kv.Value;
                Dictionary<string, object> gplat = DapJson.Obj(rootDict, platformKey);
                if (gplat != null) foreach (KeyValuePair<string, object> kv in gplat) globals[kv.Key] = kv.Value;
            }
            List<object> tasks = DapJson.Arr(root, "tasks");
            if (tasks == null) return res;
            foreach (object o in tasks)
            {
                Dictionary<string, object> d0 = o as Dictionary<string, object>;
                if (d0 == null) continue;
                Dictionary<string, object> d = new Dictionary<string, object>(StringComparer.Ordinal);
                foreach (KeyValuePair<string, object> kv in globals) if (kv.Key != "windows" && kv.Key != "osx" && kv.Key != "linux") d[kv.Key] = kv.Value;
                foreach (KeyValuePair<string, object> kv in d0) d[kv.Key] = kv.Value;
                Dictionary<string, object> plat = DapJson.Obj(d0, platformKey);
                if (plat != null) foreach (KeyValuePair<string, object> kv in plat) d[kv.Key] = kv.Value;

                TaskDefinition t = new TaskDefinition();
                t.Type = (DapJson.Str(d, "type") ?? "shell").Trim();
                if (t.Type != "process") t.Type = t.Type == "npm" ? "npm" : "shell";
                t.Command = DapJson.Str(d, "command") ?? "";
                if (t.Type == "npm")
                {
                    string script = DapJson.Str(d, "script") ?? "";
                    t.Type = "shell";
                    t.Command = "npm";
                    t.Args.Add("run");
                    t.Args.Add(script);
                    string npmPath = DapJson.Str(d, "path");
                    if (!string.IsNullOrEmpty(npmPath)) t.Cwd = "${workspaceFolder}/" + npmPath.TrimEnd('/');
                    if (string.IsNullOrEmpty(DapJson.Str(d, "label"))) t.Label = "npm: " + script;
                }
                else
                {
                    List<object> args = DapJson.Arr(d, "args");
                    if (args != null)
                        foreach (object a in args)
                        {
                            string s = a as string;
                            Dictionary<string, object> qa = a as Dictionary<string, object>;
                            if (s != null) t.Args.Add(s);
                            else if (qa != null) t.Args.Add(DapJson.Str(qa, "value") ?? "");
                            else if (a != null) t.Args.Add(Convert.ToString(a, System.Globalization.CultureInfo.InvariantCulture));
                        }
                }
                string label = DapJson.Str(d, "label") ?? DapJson.Str(d, "taskName");
                if (!string.IsNullOrEmpty(label)) t.Label = label;
                if (string.IsNullOrEmpty(t.Label)) t.Label = (t.Command + " " + string.Join(" ", t.Args.ToArray())).Trim();
                t.Detail = DapJson.Str(d, "detail") ?? "";
                Dictionary<string, object> options = DapJson.Obj(d, "options");
                if (options != null)
                {
                    string cwd = DapJson.Str(options, "cwd");
                    if (!string.IsNullOrEmpty(cwd)) t.Cwd = cwd;
                    Dictionary<string, object> env = DapJson.Obj(options, "env");
                    if (env != null) foreach (KeyValuePair<string, object> kv in env) t.Env[kv.Key] = kv.Value == null ? "" : Convert.ToString(kv.Value, System.Globalization.CultureInfo.InvariantCulture);
                }
                object group = d.ContainsKey("group") ? d["group"] : null;
                if (group is string) t.Group = (string)group;
                else if (group is Dictionary<string, object>)
                {
                    t.Group = DapJson.Str(group, "kind");
                    object def = ((Dictionary<string, object>)group).ContainsKey("isDefault") ? ((Dictionary<string, object>)group)["isDefault"] : null;
                    t.IsDefault = (def is bool && (bool)def) || def is string; // "isDefault": "**/*.ts" glob counts as default
                }
                if (d.ContainsKey("isBuildCommand") && d["isBuildCommand"] is bool && (bool)d["isBuildCommand"]) { t.Group = "build"; t.IsDefault = true; }
                if (d.ContainsKey("isTestCommand") && d["isTestCommand"] is bool && (bool)d["isTestCommand"]) { t.Group = "test"; t.IsDefault = true; }
                object pm = d.ContainsKey("problemMatcher") ? d["problemMatcher"] : null;
                if (pm is List<object>) t.ProblemMatchers.AddRange((List<object>)pm);
                else if (pm != null) t.ProblemMatchers.Add(pm);
                object dep = d.ContainsKey("dependsOn") ? d["dependsOn"] : null;
                if (dep is string) t.DependsOn.Add((string)dep);
                else if (dep is List<object>) foreach (object x in (List<object>)dep) { string s = x as string; if (s != null) t.DependsOn.Add(s); }
                string order = DapJson.Str(d, "dependsOrder");
                t.DependsOrderSequence = true; // parallel dependencies are run in sequence too (simpler, deterministic)
                if (order == "parallel") t.DependsOrderSequence = false;
                t.IsBackground = DapJson.Bool(d, "isBackground", false);
                t.Hide = DapJson.Bool(d, "hide", false);
                Dictionary<string, object> pres = DapJson.Obj(d, "presentation");
                if (pres != null) t.Panel = DapJson.Str(pres, "panel") ?? "shared";
                res.Add(t);
            }
            return res;
        }

        /// <summary>VS Code's "Run Build Task" choice: the default build task, else null (the caller shows the build list).</summary>
        public static TaskDefinition FindDefault(List<TaskDefinition> tasks, string group)
        {
            if (tasks == null) return null;
            foreach (TaskDefinition t in tasks) if (t.Group == group && t.IsDefault) return t;
            List<TaskDefinition> inGroup = new List<TaskDefinition>();
            foreach (TaskDefinition t in tasks) if (t.Group == group) inGroup.Add(t);
            return inGroup.Count == 1 ? inGroup[0] : null;
        }

        public static List<TaskDefinition> InGroup(List<TaskDefinition> tasks, string group)
        {
            List<TaskDefinition> r = new List<TaskDefinition>();
            if (tasks != null) foreach (TaskDefinition t in tasks) if (t.Group == group) r.Add(t);
            return r;
        }

        public static TaskDefinition FindByLabel(List<TaskDefinition> tasks, string label)
        {
            if (tasks == null || label == null) return null;
            foreach (TaskDefinition t in tasks) if (t.Label == label || t.DisplayLabel == label) return t;
            return null;
        }

        /// <summary>tasks.json tasks first, then auto-detected ones (detected tasks shadowed by a tasks.json label are skipped).</summary>
        public static List<TaskDefinition> LoadAll(string workspaceRoot, out string error)
        {
            List<TaskDefinition> all = LoadTasksJson(workspaceRoot, out error);
            HashSet<string> labels = new HashSet<string>(StringComparer.Ordinal);
            foreach (TaskDefinition t in all) labels.Add(t.Label);
            foreach (TaskDefinition t in TaskAutoDetector.Detect(workspaceRoot))
                if (!labels.Contains(t.Label) && !labels.Contains(t.DisplayLabel)) all.Add(t);
            return all;
        }

        public static string Substitute(string s, DebugVariableContext ctx)
        {
            return DebugLaunchConfigurations.SubstituteString(s, ctx);
        }

        /// <summary>Working directory after substitution (defaults to the workspace folder).</summary>
        public static string ResolveCwd(TaskDefinition t, DebugVariableContext ctx)
        {
            string ws = ctx != null ? ctx.WorkspaceFolder : null;
            if (string.IsNullOrEmpty(t.Cwd)) return ws;
            string c = Substitute(t.Cwd, ctx);
            if (!Path.IsPathRooted(c) && !string.IsNullOrEmpty(ws)) c = Path.Combine(ws, c);
            try { return Path.GetFullPath(c); } catch { return c; }
        }

        /// <summary>POSIX shell quoting (VS Code "strong" quoting for args with spaces or shell characters).</summary>
        public static string QuotePosix(string a)
        {
            if (a == null) return "''";
            if (a.Length > 0 && Regex.IsMatch(a, @"^[A-Za-z0-9_@%+=:,./\-]+$")) return a;
            return "'" + a.Replace("'", "'\\''") + "'";
        }

        /// <summary>cmd.exe quoting for args.</summary>
        public static string QuoteWindows(string a)
        {
            if (a == null) return "\"\"";
            if (a.Length > 0 && a.IndexOfAny(new char[] { ' ', '\t', '"', '&', '|', '<', '>', '^', '(', ')' }) < 0) return a;
            return "\"" + a.Replace("\"", "\\\"") + "\"";
        }

        /// <summary>
        /// The command line the shell runs: for shell tasks the command is used verbatim (it may contain shell syntax)
        /// and each arg is quoted when needed; for process tasks the command itself is quoted too.
        /// </summary>
        public static string BuildCommandLine(TaskDefinition t, DebugVariableContext ctx, bool windows)
        {
            StringBuilder sb = new StringBuilder();
            string cmd = Substitute(t.Command ?? "", ctx);
            if (t.Type == "process") sb.Append(windows ? QuoteWindows(cmd) : QuotePosix(cmd));
            else sb.Append(cmd);
            foreach (string a0 in t.Args)
            {
                string a = Substitute(a0, ctx);
                sb.Append(' ').Append(windows ? QuoteWindows(a) : QuotePosix(a));
            }
            return sb.ToString().Trim();
        }

        /// <summary>Environment overrides after substitution.</summary>
        public static Dictionary<string, string> ResolveEnv(TaskDefinition t, DebugVariableContext ctx)
        {
            Dictionary<string, string> env = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (KeyValuePair<string, string> kv in t.Env) env[kv.Key] = Substitute(kv.Value, ctx);
            return env;
        }

        /// <summary>
        /// The shell invocation for the integrated terminal: Unix runs the user's shell as a login shell with -c;
        /// Windows uses cmd.exe /d /s /c. Environment overrides are prefixed (env / set) so they reach the command.
        /// </summary>
        public static string BuildTerminalCommandLine(string commandLine, Dictionary<string, string> env, bool windows, string unixShell)
        {
            if (windows)
            {
                StringBuilder w = new StringBuilder();
                if (env != null) foreach (KeyValuePair<string, string> kv in env) w.Append("set \"").Append(kv.Key).Append('=').Append(kv.Value).Append("\" && ");
                w.Append(commandLine);
                return "cmd.exe /d /s /c \"" + w.ToString() + "\"";
            }
            StringBuilder u = new StringBuilder();
            if (env != null) foreach (KeyValuePair<string, string> kv in env) u.Append("export ").Append(kv.Key).Append('=').Append(QuotePosix(kv.Value)).Append("; ");
            u.Append(commandLine);
            string shell = string.IsNullOrEmpty(unixShell) ? "/bin/sh" : unixShell;
            string leaf = Path.GetFileName(shell);
            string flags = leaf == "sh" || leaf == "dash" ? "-c" : "-l -c";
            return shell + " " + flags + " " + QuotePosix(u.ToString());
        }
    }

    /// <summary>Auto-detected tasks when there is no tasks.json entry for them (VS Code task providers).</summary>
    public static class TaskAutoDetector
    {
        public static List<TaskDefinition> Detect(string root)
        {
            List<TaskDefinition> res = new List<TaskDefinition>();
            if (string.IsNullOrEmpty(root) || !Directory.Exists(root)) return res;
            try { DetectNpm(root, res); } catch { }
            try { DetectMake(root, res); } catch { }
            try { DetectDotnet(root, res); } catch { }
            try { DetectCargo(root, res); } catch { }
            try { DetectGo(root, res); } catch { }
            try { DetectPytest(root, res); } catch { }
            return res;
        }

        private static TaskDefinition Make(string source, string label, string command, string group, string matcher, string detail)
        {
            TaskDefinition t = new TaskDefinition();
            t.Source = source;
            t.Label = label;
            t.Command = command;
            t.Group = group;
            t.Detail = detail ?? command;
            if (!string.IsNullOrEmpty(matcher)) t.ProblemMatchers.Add(matcher);
            return t;
        }

        public static void DetectNpm(string root, List<TaskDefinition> res)
        {
            string pkg = Path.Combine(root, "package.json");
            if (!File.Exists(pkg)) return;
            string ignored;
            object o = DapJson.TryParse(File.ReadAllText(pkg), out ignored);
            Dictionary<string, object> scripts = DapJson.Obj(o, "scripts");
            if (scripts == null) return;
            string runner = File.Exists(Path.Combine(root, "pnpm-lock.yaml")) ? "pnpm" : File.Exists(Path.Combine(root, "yarn.lock")) ? "yarn" : "npm";
            foreach (KeyValuePair<string, object> kv in scripts)
            {
                string name = kv.Key;
                string body = kv.Value as string ?? "";
                string group = null;
                if (name == "build" || name.StartsWith("build:", StringComparison.Ordinal) || name == "compile") group = "build";
                else if (name == "test" || name.StartsWith("test:", StringComparison.Ordinal)) group = "test";
                string matcher = null;
                if (body.IndexOf("tsc", StringComparison.Ordinal) >= 0) matcher = "$tsc";
                else if (body.IndexOf("eslint", StringComparison.Ordinal) >= 0) matcher = "$eslint-stylish";
                string cmd = runner == "yarn" ? "yarn " + TaskConfiguration.QuotePosix(name) : runner + " run " + TaskConfiguration.QuotePosix(name);
                res.Add(Make("npm", name, cmd, group, matcher, body));
            }
        }

        private static readonly Regex MakeTarget = new Regex(@"^([A-Za-z0-9][A-Za-z0-9_.\-/]*)\s*:(?!=)", RegexOptions.Compiled);

        public static void DetectMake(string root, List<TaskDefinition> res)
        {
            string mf = null;
            foreach (string n in new string[] { "Makefile", "makefile", "GNUmakefile" })
                if (File.Exists(Path.Combine(root, n))) { mf = Path.Combine(root, n); break; }
            if (mf == null) return;
            HashSet<string> seen = new HashSet<string>(StringComparer.Ordinal);
            List<string> phony = new List<string>();
            foreach (string line in File.ReadAllLines(mf))
            {
                if (line.StartsWith(".PHONY", StringComparison.Ordinal)) continue;
                Match m = MakeTarget.Match(line);
                if (!m.Success) continue;
                string target = m.Groups[1].Value;
                if (target.IndexOf('%') >= 0 || target.StartsWith(".", StringComparison.Ordinal) || !seen.Add(target)) continue;
                phony.Add(target);
            }
            bool first = true;
            foreach (string target in phony)
            {
                string group = target == "all" || target == "build" || (first && target != "clean" && target != "test") ? "build" : (target == "test" || target == "check" ? "test" : null);
                res.Add(Make("make", target, "make " + TaskConfiguration.QuotePosix(target), group, "$gcc", "make " + target));
                first = false;
            }
        }

        public static void DetectDotnet(string root, List<TaskDefinition> res)
        {
            string[] slns = Directory.GetFiles(root, "*.sln");
            string target = null;
            if (slns.Length > 0) target = Path.GetFileName(slns[0]);
            else
            {
                string[] projs = Directory.GetFiles(root, "*.csproj");
                if (projs.Length == 0) projs = Directory.GetFiles(root, "*.fsproj");
                if (projs.Length > 0) target = Path.GetFileName(projs[0]);
            }
            if (target == null) return;
            string q = TaskConfiguration.QuotePosix(target);
            res.Add(Make("dotnet", "build", "dotnet build " + q + " /property:GenerateFullPaths=true /consoleloggerparameters:NoSummary", "build", "$msCompile", "dotnet build " + target));
            res.Add(Make("dotnet", "test", "dotnet test " + q, "test", "$msCompile", "dotnet test " + target));
        }

        public static void DetectCargo(string root, List<TaskDefinition> res)
        {
            if (!File.Exists(Path.Combine(root, "Cargo.toml"))) return;
            res.Add(Make("cargo", "build", "cargo build", "build", "$rustc", null));
            res.Add(Make("cargo", "test", "cargo test", "test", "$rustc", null));
            res.Add(Make("cargo", "check", "cargo check", null, "$rustc", null));
        }

        public static void DetectGo(string root, List<TaskDefinition> res)
        {
            if (!File.Exists(Path.Combine(root, "go.mod"))) return;
            res.Add(Make("go", "build", "go build ./...", "build", "$go", null));
            res.Add(Make("go", "test", "go test ./...", "test", "$go", null));
        }

        public static void DetectPytest(string root, List<TaskDefinition> res)
        {
            bool hasConfig = File.Exists(Path.Combine(root, "pytest.ini")) || File.Exists(Path.Combine(root, "conftest.py"));
            if (!hasConfig)
            {
                string pyproject = Path.Combine(root, "pyproject.toml");
                if (File.Exists(pyproject) && File.ReadAllText(pyproject).IndexOf("[tool.pytest", StringComparison.Ordinal) >= 0) hasConfig = true;
                string setupCfg = Path.Combine(root, "setup.cfg");
                if (!hasConfig && File.Exists(setupCfg) && File.ReadAllText(setupCfg).IndexOf("[tool:pytest]", StringComparison.Ordinal) >= 0) hasConfig = true;
            }
            bool hasTests = hasConfig || HasPythonTests(root, 0);
            if (!hasTests) return;
            string py = Path.DirectorySeparatorChar == '\\' ? "python" : "python3";
            res.Add(Make("python", "pytest", py + " -m pytest", "test", "$python", null));
        }

        private static bool HasPythonTests(string dir, int depth)
        {
            if (depth > 2) return false;
            foreach (string f in Directory.GetFiles(dir, "*.py"))
            {
                string n = Path.GetFileName(f);
                if (n.StartsWith("test_", StringComparison.Ordinal) || n.EndsWith("_test.py", StringComparison.Ordinal)) return true;
            }
            foreach (string sub in Directory.GetDirectories(dir))
            {
                string n = Path.GetFileName(sub);
                if (n.StartsWith(".", StringComparison.Ordinal) || n == "node_modules" || n == "venv" || n == "__pycache__" || n == "build" || n == "dist") continue;
                if (n == "tests" || n == "test" || depth < 1)
                    if (HasPythonTests(sub, depth + 1)) return true;
            }
            return false;
        }
    }
}
