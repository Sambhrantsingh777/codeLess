using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;

namespace codeLess.Studio
{
    /// <summary>The command used to verify staged agent edits, and where it came from.</summary>
    public sealed class VerificationCommand
    {
        /// <summary>Shell command line (tasks are re-built against the verification folder at run time).</summary>
        public string CommandLine = "";
        /// <summary>Human readable origin: ".codeless/verify", "workspace setting", "user setting", "tasks.json: build", "auto-detected (dotnet)".</summary>
        public string Source = "";
        /// <summary>"build", "test", "check" or "custom" (drives the "build passed" / "tests passed" wording).</summary>
        public string Kind = "custom";
        /// <summary>Problem matcher names or inline definitions (tasks.json syntax).</summary>
        public List<object> ProblemMatchers = new List<object>();
        /// <summary>Set for tasks.json / auto-detected tasks: rebuilt with ${workspaceFolder} = the verification folder.</summary>
        public TaskDefinition Task;
        /// <summary>True when the repository (not the user) chose the command: needs approval before the first run.</summary>
        public bool IsWorkspaceControlled;

        public string KindNoun
        {
            get
            {
                if (Kind == "build") return "build";
                if (Kind == "test") return "tests";
                if (Kind == "check") return "check";
                return "verification";
            }
        }
    }

    /// <summary>
    /// Chooses the verification command: per-workspace ".codeless/verify" or "codeless.verifyCommand" in
    /// .vscode/settings.json, then the user setting, then the task system (default build task in tasks.json,
    /// else an auto-detected build/check/test task: dotnet build, cargo check, go build ./..., npm test, pytest -q).
    /// Always resolved from the real workspace before any staged edit is applied, so the model can't pick it.
    /// </summary>
    public static class VerificationCommandResolver
    {
        public const string WorkspaceFileName = "verify";
        public const string WorkspaceSettingKey = "codeless.verifyCommand";

        public static string WorkspaceVerifyFilePath(string workspaceRoot)
        {
            return string.IsNullOrEmpty(workspaceRoot) ? null : Path.Combine(Path.Combine(workspaceRoot, ".codeless"), WorkspaceFileName);
        }

        public static VerificationCommand Resolve(string workspaceRoot, string userCommand)
        {
            if (string.IsNullOrEmpty(workspaceRoot) || !Directory.Exists(workspaceRoot)) return null;

            string fileCmd = ReadWorkspaceVerifyFile(workspaceRoot);
            if (!string.IsNullOrEmpty(fileCmd))
                return Custom(fileCmd, ".codeless/verify", true);

            string wsSetting = ReadWorkspaceSetting(workspaceRoot);
            if (!string.IsNullOrEmpty(wsSetting))
                return Custom(wsSetting, "workspace setting " + WorkspaceSettingKey, true);

            if (!string.IsNullOrWhiteSpace(userCommand))
                return Custom(userCommand.Trim(), "user setting", false);

            string err;
            List<TaskDefinition> jsonTasks = TaskConfiguration.LoadTasksJson(workspaceRoot, out err);
            TaskDefinition def = PickDefault(jsonTasks, "build");
            if (def == null) def = PickDefault(jsonTasks, "test");
            if (def != null) return FromTask(def, "tasks.json: " + def.Label, def.Group == "test" ? "test" : "build");

            return AutoDetect(workspaceRoot);
        }

        private static TaskDefinition PickDefault(List<TaskDefinition> tasks, string group)
        {
            if (tasks == null) return null;
            foreach (TaskDefinition t in tasks)
                if (t.Group == group && t.IsDefault && IsRunnableAlone(t)) return t;
            return null;
        }

        /// <summary>Background (watch) tasks never finish and dependsOn chains aren't run by the verifier.</summary>
        private static bool IsRunnableAlone(TaskDefinition t)
        {
            return t != null && !t.IsBackground && t.DependsOn.Count == 0 && !string.IsNullOrWhiteSpace(t.Command);
        }

        public static VerificationCommand AutoDetect(string workspaceRoot)
        {
            List<TaskDefinition> detected = TaskAutoDetector.Detect(workspaceRoot);
            // Ecosystem preference: fast, deterministic compile checks first; tests where there is no compile step.
            TaskDefinition t = Find(detected, "dotnet", "build");
            if (t != null) return FromTask(t, "auto-detected (dotnet)", "build");
            t = Find(detected, "cargo", "check");
            if (t != null) return FromTask(t, "auto-detected (cargo)", "check");
            t = Find(detected, "go", "build");
            if (t != null) return FromTask(t, "auto-detected (go)", "build");
            t = Find(detected, "npm", "test");
            if (t != null && !IsNpmPlaceholderTest(t)) return FromTask(t, "auto-detected (npm)", "test");
            t = Find(detected, "npm", "build");
            if (t != null) return FromTask(t, "auto-detected (npm)", "build");
            t = Find(detected, "python", "pytest");
            if (t != null)
            {
                TaskDefinition q = CloneTask(t);
                q.Command = t.Command + " -q";
                return FromTask(q, "auto-detected (pytest)", "test");
            }
            foreach (string target in new string[] { "build", "all", "check", "test" })
            {
                t = Find(detected, "make", target);
                if (t != null) return FromTask(t, "auto-detected (make " + target + ")", target == "check" ? "check" : (target == "test" ? "test" : "build"));
            }
            return null;
        }

        private static TaskDefinition Find(List<TaskDefinition> list, string source, string label)
        {
            foreach (TaskDefinition t in list)
                if (t.Source == source && t.Label == label) return t;
            return null;
        }

        /// <summary>npm init's default "test" script always fails ("Error: no test specified").</summary>
        private static bool IsNpmPlaceholderTest(TaskDefinition t)
        {
            return (t.Detail ?? "").IndexOf("no test specified", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static TaskDefinition CloneTask(TaskDefinition t)
        {
            TaskDefinition c = new TaskDefinition();
            c.Label = t.Label; c.Type = t.Type; c.Command = t.Command; c.Args = new List<string>(t.Args);
            c.Cwd = t.Cwd; c.Env = new Dictionary<string, string>(t.Env, StringComparer.Ordinal); c.Group = t.Group;
            c.IsDefault = t.IsDefault; c.ProblemMatchers = new List<object>(t.ProblemMatchers); c.Source = t.Source; c.Detail = t.Detail;
            return c;
        }

        public static VerificationCommand Custom(string commandLine, string source, bool workspaceControlled)
        {
            VerificationCommand c = new VerificationCommand();
            c.CommandLine = commandLine.Trim();
            c.Source = source;
            c.Kind = GuessKind(c.CommandLine);
            c.IsWorkspaceControlled = workspaceControlled;
            string m = GuessMatcher(c.CommandLine);
            if (m != null) c.ProblemMatchers.Add(m);
            else
            {
                // Unknown tool (scripts, make wrappers): the common compiler formats are distinctive enough to try all.
                c.ProblemMatchers.Add("$msCompile");
                c.ProblemMatchers.Add("$tsc");
                c.ProblemMatchers.Add("$gcc");
                c.ProblemMatchers.Add("$go");
                c.ProblemMatchers.Add("$python");
            }
            return c;
        }

        private static VerificationCommand FromTask(TaskDefinition t, string source, string kind)
        {
            VerificationCommand c = new VerificationCommand();
            c.Task = t;
            c.Source = source;
            c.Kind = kind;
            c.IsWorkspaceControlled = true;
            c.CommandLine = TaskConfiguration.BuildCommandLine(t, null, Path.DirectorySeparatorChar == '\\');
            c.ProblemMatchers.AddRange(t.ProblemMatchers);
            if (c.ProblemMatchers.Count == 0)
            {
                string m = GuessMatcher(c.CommandLine);
                if (m != null) c.ProblemMatchers.Add(m);
            }
            return c;
        }

        /// <summary>First non-empty, non-comment line of .codeless/verify.</summary>
        public static string ReadWorkspaceVerifyFile(string workspaceRoot)
        {
            try
            {
                string p = WorkspaceVerifyFilePath(workspaceRoot);
                if (p == null || !File.Exists(p)) return null;
                FileInfo fi = new FileInfo(p);
                if (fi.Length > 64 * 1024) return null;
                foreach (string raw in File.ReadAllLines(p))
                {
                    string line = raw.Trim();
                    if (line.Length == 0 || line.StartsWith("#", StringComparison.Ordinal) || line.StartsWith("//", StringComparison.Ordinal)) continue;
                    return line;
                }
            }
            catch { }
            return null;
        }

        /// <summary>"codeless.verifyCommand" in .vscode/settings.json (JSONC).</summary>
        public static string ReadWorkspaceSetting(string workspaceRoot)
        {
            try
            {
                string p = Path.Combine(Path.Combine(workspaceRoot, ".vscode"), "settings.json");
                if (!File.Exists(p)) return null;
                string err;
                object root = DapJson.TryParse(File.ReadAllText(p), out err);
                string v = DapJson.Str(root, WorkspaceSettingKey);
                return string.IsNullOrWhiteSpace(v) ? null : v.Trim();
            }
            catch { return null; }
        }

        public static string GuessKind(string commandLine)
        {
            string c = (commandLine ?? "").ToLowerInvariant();
            if (Regex.IsMatch(c, @"\b(test|tests|pytest|jest|vitest|mocha|rspec|ctest|unittest)\b")) return "test";
            if (Regex.IsMatch(c, @"\b(check|lint|clippy|mypy|tsc\s+--noemit|vet)\b")) return "check";
            if (Regex.IsMatch(c, @"\b(build|compile|make|tsc|msbuild|javac|gradle|mvn)\b")) return "build";
            return "custom";
        }

        /// <summary>Problem matcher for a free-form command (VS Code's built-in matcher names).</summary>
        public static string GuessMatcher(string commandLine)
        {
            string c = (commandLine ?? "").Trim().ToLowerInvariant();
            if (c.StartsWith("dotnet", StringComparison.Ordinal) || c.Contains("msbuild")) return "$msCompile";
            if (c.StartsWith("cargo", StringComparison.Ordinal) || c.StartsWith("rustc", StringComparison.Ordinal)) return "$rustc";
            if (c.StartsWith("go ", StringComparison.Ordinal)) return "$go";
            if (c.Contains("pytest") || c.StartsWith("python", StringComparison.Ordinal)) return "$python";
            if (Regex.IsMatch(c, @"(^|\s|/)tsc(\s|$)")) return "$tsc";
            if (c.Contains("eslint")) return "$eslint-stylish";
            if (c.StartsWith("make", StringComparison.Ordinal) || c.StartsWith("gcc", StringComparison.Ordinal) ||
                c.StartsWith("clang", StringComparison.Ordinal) || c.StartsWith("g++", StringComparison.Ordinal) || c.StartsWith("cmake", StringComparison.Ordinal)) return "$gcc";
            if (c.StartsWith("npm", StringComparison.Ordinal) || c.StartsWith("yarn", StringComparison.Ordinal) || c.StartsWith("pnpm", StringComparison.Ordinal)) return "$tsc";
            return null;
        }
    }

    public enum VerificationApprovalDecision
    {
        Deny = 0,
        AllowOnce = 1,
        AllowAlwaysForWorkspace = 2
    }

    /// <summary>What the user is asked before a verification command runs.</summary>
    public sealed class VerificationApprovalRequest
    {
        public string WorkspaceRoot;
        public string CommandLine;
        public string Source;
        /// <summary>Why approval is needed (first run of a repository-chosen command, build files edited by the AI, ...).</summary>
        public string Reason;
        /// <summary>Staged files that control the build (package.json, *.csproj, Makefile, conftest.py, ...).</summary>
        public List<string> BuildControlFiles = new List<string>();
        /// <summary>True when "Always allow" is offered (not offered when the AI edited build files).</summary>
        public bool CanAlwaysAllow = true;
    }

    /// <summary>
    /// Approval gate for verification runs, consistent with the agent's run_terminal gate: verification executes the
    /// project's build/test (code the AI just edited), so a repository-chosen command needs explicit approval the first
    /// time, and every run that includes AI edits to build-control files (package.json scripts, *.csproj targets,
    /// Makefile, conftest.py, ...) is confirmed again. Commands the user configured are trusted. When the user turned
    /// "Require agent tool approval" off, nothing is asked (same as run_terminal).
    /// </summary>
    public static class VerificationApprovalPolicy
    {
        private static readonly Regex BuildControlName = new Regex(
            @"^(package\.json|package-lock\.json|npm-shrinkwrap\.json|pnpm-workspace\.yaml|\.npmrc|\.yarnrc(\.yml)?|yarn\.lock|" +
            @"global\.json|nuget\.config|directory\.build\.(props|targets)|directory\.packages\.props|" +
            @"cargo\.toml|build\.rs|go\.mod|go\.work|makefile|gnumakefile|cmakelists\.txt|" +
            @"setup\.py|setup\.cfg|pyproject\.toml|conftest\.py|pytest\.ini|tox\.ini|noxfile\.py|" +
            @"tasks\.json|settings\.json|verify|build\.gradle(\.kts)?|settings\.gradle(\.kts)?|pom\.xml|gemfile|rakefile|" +
            @"(jest|vitest|webpack|vite|rollup|babel|eslint)\.config\.[a-z]+|\.babelrc)$",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly Regex BuildControlExt = new Regex(@"\.(csproj|fsproj|vbproj|vcxproj|sln|props|targets|mk|cmake)$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        public static bool IsBuildControlFile(string path)
        {
            if (string.IsNullOrEmpty(path)) return false;
            string name = Path.GetFileName(path.Replace('\\', '/').TrimEnd('/'));
            string norm = path.Replace('\\', '/');
            if (norm.IndexOf("/.cargo/", StringComparison.Ordinal) >= 0 || norm.StartsWith(".cargo/", StringComparison.Ordinal)) return true;
            if (norm.IndexOf("/.github/workflows/", StringComparison.Ordinal) >= 0) return false;
            return BuildControlName.IsMatch(name) || BuildControlExt.IsMatch(name);
        }

        public static List<string> FindBuildControlFiles(string workspaceRoot, IEnumerable<string> stagedPaths)
        {
            List<string> res = new List<string>();
            if (stagedPaths == null) return res;
            foreach (string p in stagedPaths)
            {
                if (!IsBuildControlFile(p)) continue;
                res.Add(LlmHarnessAndAgentEngine.GetRelativePath(workspaceRoot, p));
            }
            return res;
        }

        /// <summary>
        /// Null when the run may proceed without asking; otherwise the request to show. <paramref name="sessionApproved"/>
        /// holds commands already allowed during this verified turn / Best-of-N batch.
        /// </summary>
        public static VerificationApprovalRequest Evaluate(IdePersistentSettings settings, string workspaceRoot, VerificationCommand cmd,
            IEnumerable<string> stagedPaths, ICollection<string> sessionApproved)
        {
            if (cmd == null) return null;
            if (settings != null && !settings.RequireAgentToolApproval) return null;
            List<string> buildFiles = FindBuildControlFiles(workspaceRoot, stagedPaths);
            string key = IdePersistentSettings.AgentVerifyApprovalKey(workspaceRoot, cmd.CommandLine);
            if (buildFiles.Count > 0)
            {
                string bfKey = key + "\n" + string.Join("|", buildFiles.ToArray());
                if (sessionApproved != null && sessionApproved.Contains(bfKey)) return null;
                VerificationApprovalRequest r = NewRequest(workspaceRoot, cmd);
                r.BuildControlFiles = buildFiles;
                r.CanAlwaysAllow = false;
                r.Reason = "The AI's changes edit files that control the build (" + string.Join(", ", buildFiles.ToArray()) +
                           "). Verifying runs them. Review those edits before allowing.";
                return r;
            }
            if (!cmd.IsWorkspaceControlled) return null;
            if (settings != null && settings.IsAgentVerifyCommandApproved(workspaceRoot, cmd.CommandLine)) return null;
            if (sessionApproved != null && sessionApproved.Contains(key)) return null;
            VerificationApprovalRequest req = NewRequest(workspaceRoot, cmd);
            req.Reason = "Verification runs this " + cmd.Source + " command, including the AI's proposed code, in a temporary copy of the workspace. Your files are not changed.";
            return req;
        }

        private static VerificationApprovalRequest NewRequest(string workspaceRoot, VerificationCommand cmd)
        {
            VerificationApprovalRequest r = new VerificationApprovalRequest();
            r.WorkspaceRoot = workspaceRoot;
            r.CommandLine = cmd.CommandLine;
            r.Source = cmd.Source;
            return r;
        }

        /// <summary>Records the decision (session cache and, for "always", the persistent list). Returns true when allowed.</summary>
        public static bool Apply(IdePersistentSettings settings, VerificationApprovalRequest req, VerificationApprovalDecision decision, ICollection<string> sessionApproved)
        {
            if (req == null) return true;
            if (decision == VerificationApprovalDecision.Deny) return false;
            string key = IdePersistentSettings.AgentVerifyApprovalKey(req.WorkspaceRoot, req.CommandLine);
            if (req.BuildControlFiles != null && req.BuildControlFiles.Count > 0)
                key = key + "\n" + string.Join("|", req.BuildControlFiles.ToArray());
            if (sessionApproved != null && !sessionApproved.Contains(key)) sessionApproved.Add(key);
            if (decision == VerificationApprovalDecision.AllowAlwaysForWorkspace && req.CanAlwaysAllow && settings != null)
            {
                settings.ApproveAgentVerifyCommand(req.WorkspaceRoot, req.CommandLine);
                try { settings.Save(); } catch { }
            }
            return true;
        }
    }
}
