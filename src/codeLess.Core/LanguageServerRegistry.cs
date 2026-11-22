using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;

namespace codeLess.Studio
{
    /// <summary>One way to launch a server (first one found on disk wins).</summary>
    public class LanguageServerCandidate
    {
        public string Command;
        public string[] Args = new string[0];
        public string DisplayName;

        public LanguageServerCandidate(string command, string displayName, params string[] args)
        {
            Command = command;
            DisplayName = displayName;
            Args = args ?? new string[0];
        }
    }

    /// <summary>A built-in language server family (e.g. "python" = basedpyright / pyright / pylsp / jedi).</summary>
    public class LanguageServerDefinition
    {
        public string Id;
        public string DisplayName;
        public string[] LanguageIds = new string[0];
        public List<LanguageServerCandidate> Candidates = new List<LanguageServerCandidate>();
        /// <summary>Files / globs ("*.csproj") that mark a project root, used for files outside the workspace.</summary>
        public string[] RootMarkers = new string[0];
        public bool NodeBased;
        public bool PythonBased;
        public string InstallCommandNpm;
        public string InstallCommandMac;
        public string InstallCommandLinux;
        public string InstallCommandWindows;
        public string InstallCommandAny;
        public string InstallUrl;
        /// <summary>
        /// The server executes project code by design (rust-analyzer build scripts / proc macros, jdtls Gradle / Maven
        /// import, MSBuild evaluation for C#): never started in Restricted Mode, even from PATH.
        /// </summary>
        public bool ExecutesProjectCode;

        public bool HandlesLanguage(string languageId)
        {
            foreach (string l in LanguageIds) if (string.Equals(l, languageId, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }
    }

    /// <summary>Result of resolving which server to run for a language.</summary>
    public class LanguageServerResolution
    {
        public LanguageServerDefinition Definition;
        public string LanguageId;
        public string Executable;     // absolute path, or null when not found / disabled
        public string[] Args = new string[0];
        public string DisplayName;
        public bool FromUserOverride;
        public bool Disabled;
        public string Problem;        // human readable reason when Executable == null
        public string InstallCommand; // suggested install command (may be null)
        /// <summary>Not started because the workspace is in Restricted Mode (Problem says why).</summary>
        public bool Restricted;
        /// <summary>Whether the workspace was trusted when this was resolved (a trust change re-resolves).</summary>
        public bool WorkspaceTrusted = true;
        /// <summary>Extra initializationOptions (e.g. the TypeScript server pinned to a non-workspace tsserver in Restricted Mode).</summary>
        public Dictionary<string, object> InitializationOptions;

        public bool CanStart { get { return !Disabled && !Restricted && !string.IsNullOrEmpty(Executable); } }
    }

    /// <summary>User overrides parsed from the "LanguageServerOverrides" setting (one "language = command args" per line).</summary>
    public class LanguageServerUserSettings
    {
        public bool GloballyDisabled;
        public Dictionary<string, string> CommandLines = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        public HashSet<string> DisabledLanguages = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        public const string OverridesHelp =
            "# One line per language: <language> = <command> [args...]   or   <language> = off\n" +
            "# Languages: python, typescript, javascript, go, rust, c, cpp, csharp, java, lua, shellscript, yaml, json, html, css\n" +
            "# Server ids also work as keys (e.g. \"typescript\" covers .ts/.tsx/.js/.jsx). Quote paths that contain spaces.\n" +
            "# Examples:\n" +
            "#   python = /opt/homebrew/bin/basedpyright-langserver --stdio\n" +
            "#   rust = off\n";

        public static LanguageServerUserSettings Parse(string text, bool globallyDisabled)
        {
            LanguageServerUserSettings s = new LanguageServerUserSettings();
            s.GloballyDisabled = globallyDisabled;
            if (string.IsNullOrEmpty(text)) return s;
            foreach (string raw in text.Replace("\r\n", "\n").Split('\n'))
            {
                string line = raw.Trim();
                if (line.Length == 0 || line.StartsWith("#", StringComparison.Ordinal) || line.StartsWith("//", StringComparison.Ordinal)) continue;
                int eq = line.IndexOf('=');
                if (eq <= 0) continue;
                string key = line.Substring(0, eq).Trim().ToLowerInvariant();
                string val = line.Substring(eq + 1).Trim();
                if (key.Length == 0) continue;
                string lv = val.ToLowerInvariant();
                if (lv == "off" || lv == "disabled" || lv == "false" || lv == "none")
                {
                    s.DisabledLanguages.Add(key);
                    s.CommandLines.Remove(key);
                }
                else if (val.Length > 0)
                {
                    s.CommandLines[key] = val;
                    s.DisabledLanguages.Remove(key);
                }
            }
            return s;
        }

        /// <summary>Splits a command line honouring double / single quotes.</summary>
        public static List<string> SplitCommandLine(string commandLine)
        {
            List<string> parts = new List<string>();
            if (string.IsNullOrEmpty(commandLine)) return parts;
            StringBuilder cur = new StringBuilder();
            char quote = '\0';
            bool any = false;
            for (int i = 0; i < commandLine.Length; i++)
            {
                char c = commandLine[i];
                if (quote != '\0')
                {
                    if (c == quote) { quote = '\0'; continue; }
                    cur.Append(c);
                    continue;
                }
                if (c == '"' || c == '\'') { quote = c; any = true; continue; }
                if (char.IsWhiteSpace(c))
                {
                    if (cur.Length > 0 || any) { parts.Add(cur.ToString()); cur.Length = 0; any = false; }
                    continue;
                }
                cur.Append(c);
            }
            if (cur.Length > 0 || any) parts.Add(cur.ToString());
            return parts;
        }
    }

    /// <summary>
    /// Built-in server catalogue and executable discovery (PATH, Homebrew, ~/.local/bin, ~/.cargo/bin, ~/go/bin,
    /// ~/.dotnet/tools, the npm global prefix, %APPDATA%\npm, workspace node_modules/.bin and the workspace venv).
    /// No extensions required. C# 5 only.
    /// </summary>
    public static class LanguageServerRegistry
    {
        private static readonly List<LanguageServerDefinition> definitions = BuildDefinitions();
        private static readonly object cacheLock = new object();
        private static readonly Dictionary<string, KeyValuePair<DateTime, string>> exeCache = new Dictionary<string, KeyValuePair<DateTime, string>>();
        private static string npmGlobalBin;
        private static bool npmGlobalBinProbed;

        /// <summary>Extra search directories (path-separator separated). Checked first; used by tests and power users.</summary>
        public const string ExtraPathEnvVar = "CODELESS_LSP_EXTRA_PATH";

        public static IList<LanguageServerDefinition> Definitions { get { return definitions.AsReadOnly(); } }

        private static List<LanguageServerDefinition> BuildDefinitions()
        {
            List<LanguageServerDefinition> list = new List<LanguageServerDefinition>();
            LanguageServerDefinition d;

            d = new LanguageServerDefinition();
            d.Id = "python"; d.DisplayName = "Python";
            d.LanguageIds = new string[] { "python" };
            d.Candidates.Add(new LanguageServerCandidate("basedpyright-langserver", "basedpyright", "--stdio"));
            d.Candidates.Add(new LanguageServerCandidate("pyright-langserver", "Pyright", "--stdio"));
            d.Candidates.Add(new LanguageServerCandidate("pylsp", "pylsp"));
            d.Candidates.Add(new LanguageServerCandidate("jedi-language-server", "Jedi"));
            d.RootMarkers = new string[] { "pyproject.toml", "setup.py", "setup.cfg", "requirements.txt", "pyrightconfig.json", "Pipfile" };
            d.NodeBased = true; d.PythonBased = true;
            d.InstallCommandNpm = "npm i -g pyright";
            d.InstallCommandAny = "python3 -m pip install --user basedpyright";
            list.Add(d);

            d = new LanguageServerDefinition();
            d.Id = "typescript"; d.DisplayName = "TypeScript";
            d.LanguageIds = new string[] { "typescript", "typescriptreact", "javascript", "javascriptreact" };
            d.Candidates.Add(new LanguageServerCandidate("typescript-language-server", "TypeScript", "--stdio"));
            d.RootMarkers = new string[] { "tsconfig.json", "jsconfig.json", "package.json" };
            d.NodeBased = true;
            d.InstallCommandNpm = "npm i -g typescript-language-server typescript";
            list.Add(d);

            d = new LanguageServerDefinition();
            d.Id = "go"; d.DisplayName = "Go";
            d.LanguageIds = new string[] { "go" };
            d.Candidates.Add(new LanguageServerCandidate("gopls", "gopls"));
            d.RootMarkers = new string[] { "go.work", "go.mod" };
            d.InstallCommandAny = "go install golang.org/x/tools/gopls@latest";
            list.Add(d);

            d = new LanguageServerDefinition();
            d.Id = "rust"; d.DisplayName = "Rust";
            d.LanguageIds = new string[] { "rust" };
            d.Candidates.Add(new LanguageServerCandidate("rust-analyzer", "rust-analyzer"));
            d.RootMarkers = new string[] { "Cargo.toml" };
            d.ExecutesProjectCode = true; // build scripts, proc macros, cargo config wrappers
            d.InstallCommandAny = "rustup component add rust-analyzer";
            list.Add(d);

            d = new LanguageServerDefinition();
            d.Id = "clangd"; d.DisplayName = "C/C++";
            d.LanguageIds = new string[] { "c", "cpp", "objective-c", "objective-cpp" };
            d.Candidates.Add(new LanguageServerCandidate("clangd", "clangd", "--background-index"));
            d.RootMarkers = new string[] { "compile_commands.json", "compile_flags.txt", ".clangd", "CMakeLists.txt", "Makefile" };
            d.InstallCommandMac = "xcode-select --install";
            d.InstallCommandLinux = "sudo apt install clangd";
            d.InstallCommandWindows = "winget install LLVM.LLVM";
            list.Add(d);

            d = new LanguageServerDefinition();
            d.Id = "csharp"; d.DisplayName = "C#";
            d.LanguageIds = new string[] { "csharp" };
            d.Candidates.Add(new LanguageServerCandidate("csharp-ls", "csharp-ls"));
            d.Candidates.Add(new LanguageServerCandidate("OmniSharp", "OmniSharp", "-lsp"));
            d.Candidates.Add(new LanguageServerCandidate("omnisharp", "OmniSharp", "-lsp"));
            d.RootMarkers = new string[] { "*.sln", "*.csproj" };
            d.ExecutesProjectCode = true; // MSBuild evaluation runs targets / tasks from the project
            d.InstallCommandAny = "dotnet tool install --global csharp-ls";
            list.Add(d);

            d = new LanguageServerDefinition();
            d.Id = "java"; d.DisplayName = "Java";
            d.LanguageIds = new string[] { "java" };
            d.Candidates.Add(new LanguageServerCandidate("jdtls", "jdtls"));
            d.RootMarkers = new string[] { "pom.xml", "build.gradle", "build.gradle.kts", "settings.gradle", ".project" };
            d.ExecutesProjectCode = true; // Gradle / Maven project import runs build scripts and wrappers
            d.InstallCommandMac = "brew install jdtls";
            d.InstallUrl = "https://download.eclipse.org/jdtls/snapshots/";
            list.Add(d);

            d = new LanguageServerDefinition();
            d.Id = "lua"; d.DisplayName = "Lua";
            d.LanguageIds = new string[] { "lua" };
            d.Candidates.Add(new LanguageServerCandidate("lua-language-server", "Lua"));
            d.RootMarkers = new string[] { ".luarc.json", ".luarc.jsonc" };
            d.InstallCommandMac = "brew install lua-language-server";
            d.InstallUrl = "https://github.com/LuaLS/lua-language-server/releases";
            list.Add(d);

            d = new LanguageServerDefinition();
            d.Id = "bash"; d.DisplayName = "Bash";
            d.LanguageIds = new string[] { "shellscript" };
            d.Candidates.Add(new LanguageServerCandidate("bash-language-server", "Bash", "start"));
            d.NodeBased = true;
            d.InstallCommandNpm = "npm i -g bash-language-server";
            list.Add(d);

            d = new LanguageServerDefinition();
            d.Id = "yaml"; d.DisplayName = "YAML";
            d.LanguageIds = new string[] { "yaml" };
            d.Candidates.Add(new LanguageServerCandidate("yaml-language-server", "YAML", "--stdio"));
            d.NodeBased = true;
            d.InstallCommandNpm = "npm i -g yaml-language-server";
            list.Add(d);

            d = new LanguageServerDefinition();
            d.Id = "json"; d.DisplayName = "JSON";
            d.LanguageIds = new string[] { "json", "jsonc" };
            d.Candidates.Add(new LanguageServerCandidate("vscode-json-language-server", "JSON", "--stdio"));
            d.NodeBased = true;
            d.InstallCommandNpm = "npm i -g vscode-langservers-extracted";
            list.Add(d);

            d = new LanguageServerDefinition();
            d.Id = "html"; d.DisplayName = "HTML";
            d.LanguageIds = new string[] { "html" };
            d.Candidates.Add(new LanguageServerCandidate("vscode-html-language-server", "HTML", "--stdio"));
            d.NodeBased = true;
            d.InstallCommandNpm = "npm i -g vscode-langservers-extracted";
            list.Add(d);

            d = new LanguageServerDefinition();
            d.Id = "css"; d.DisplayName = "CSS";
            d.LanguageIds = new string[] { "css", "scss", "less" };
            d.Candidates.Add(new LanguageServerCandidate("vscode-css-language-server", "CSS", "--stdio"));
            d.NodeBased = true;
            d.InstallCommandNpm = "npm i -g vscode-langservers-extracted";
            list.Add(d);

            return list;
        }

        /// <summary>LSP language identifier for a file (null when no built-in server family covers it).</summary>
        public static string LanguageIdForPath(string path)
        {
            if (string.IsNullOrEmpty(path)) return null;
            string name = Path.GetFileName(path).ToLowerInvariant();
            if (name == ".bashrc" || name == ".zshrc" || name == ".profile" || name == ".bash_profile") return "shellscript";
            string ext = Path.GetExtension(name).TrimStart('.');
            switch (ext)
            {
                case "py": case "pyi": case "pyw": return "python";
                case "ts": case "mts": case "cts": return "typescript";
                case "tsx": return "typescriptreact";
                case "js": case "mjs": case "cjs": return "javascript";
                case "jsx": return "javascriptreact";
                case "go": return "go";
                case "rs": return "rust";
                case "c": return "c";
                case "h": return "c";
                case "cpp": case "cc": case "cxx": case "c++": case "hpp": case "hh": case "hxx": case "h++": case "ipp": case "inl": return "cpp";
                case "m": return "objective-c";
                case "mm": return "objective-cpp";
                case "cs": case "csx": return "csharp";
                case "java": return "java";
                case "lua": return "lua";
                case "sh": case "bash": case "zsh": case "ksh": return "shellscript";
                case "yaml": case "yml": return "yaml";
                case "json": return "json";
                case "jsonc": return "jsonc";
                case "html": case "htm": case "xhtml": return "html";
                case "css": return "css";
                case "scss": return "scss";
                case "less": return "less";
                default: return null;
            }
        }

        public static LanguageServerDefinition DefinitionForLanguage(string languageId)
        {
            if (string.IsNullOrEmpty(languageId)) return null;
            foreach (LanguageServerDefinition d in definitions) if (d.HandlesLanguage(languageId)) return d;
            return null;
        }

        public static LanguageServerDefinition DefinitionById(string id)
        {
            foreach (LanguageServerDefinition d in definitions) if (string.Equals(d.Id, id, StringComparison.OrdinalIgnoreCase)) return d;
            return null;
        }

        /// <summary>Install command suitable for this OS (null when only a download URL exists).</summary>
        public static string GetInstallCommand(LanguageServerDefinition d, string workspaceRoot)
        {
            if (d == null) return null;
            if (d.InstallCommandNpm != null && FindExecutable("npm", workspaceRoot) != null) return d.InstallCommandNpm;
            if (LspText.IsMacOS() && d.InstallCommandMac != null) return d.InstallCommandMac;
            if (LspText.IsWindows() && d.InstallCommandWindows != null) return d.InstallCommandWindows;
            if (!LspText.IsWindows() && !LspText.IsMacOS() && d.InstallCommandLinux != null) return d.InstallCommandLinux;
            if (d.InstallCommandAny != null)
            {
                if (LspText.IsWindows() && d.InstallCommandAny.StartsWith("python3 ", StringComparison.Ordinal))
                    return "py -m" + d.InstallCommandAny.Substring("python3 -m".Length);
                return d.InstallCommandAny;
            }
            return d.InstallCommandNpm;
        }

        /// <summary>Decides which server (if any) to run for <paramref name="languageId"/>. Performs file-system probing; call off the UI thread.</summary>
        public static LanguageServerResolution Resolve(string languageId, string workspaceRoot, LanguageServerUserSettings user)
        {
            UserShellEnvironment.WaitForResolution(8000);
            LanguageServerResolution r = new LanguageServerResolution();
            r.LanguageId = languageId;
            LanguageServerDefinition d = DefinitionForLanguage(languageId);
            r.Definition = d;
            bool trusted = WorkspaceTrust.IsTrusted(workspaceRoot);
            r.WorkspaceTrusted = trusted;
            if (user == null) user = new LanguageServerUserSettings();
            string overrideLine = null;
            if (user.GloballyDisabled)
            {
                r.Disabled = true;
                r.Problem = "Language servers are disabled in Settings.";
                return r;
            }
            if (user.DisabledLanguages.Contains(languageId) || (d != null && user.DisabledLanguages.Contains(d.Id)))
            {
                r.Disabled = true;
                r.Problem = "Language server for " + languageId + " is turned off in Settings.";
                return r;
            }
            if (!user.CommandLines.TryGetValue(languageId, out overrideLine) && d != null)
                user.CommandLines.TryGetValue(d.Id, out overrideLine);

            if (!string.IsNullOrEmpty(overrideLine))
            {
                List<string> parts = LanguageServerUserSettings.SplitCommandLine(overrideLine);
                if (parts.Count > 0)
                {
                    r.FromUserOverride = true;
                    string exe = parts[0];
                    string resolved = Path.IsPathRooted(exe) ? (File.Exists(exe) ? exe : null) : FindExecutable(exe, workspaceRoot);
                    if (resolved == null && exe.StartsWith("~", StringComparison.Ordinal))
                    {
                        string home = HomeDir();
                        string expanded = Path.Combine(home, exe.Substring(1).TrimStart('/', '\\'));
                        if (File.Exists(expanded)) resolved = expanded;
                    }
                    r.Args = parts.Skip(1).ToArray();
                    r.DisplayName = Path.GetFileNameWithoutExtension(exe);
                    string lowerName = r.DisplayName.ToLowerInvariant();
                    bool interpreter = lowerName.StartsWith("python", StringComparison.Ordinal) || lowerName == "node" || lowerName == "java" ||
                                       lowerName == "dotnet" || lowerName == "bash" || lowerName == "sh" || lowerName == "py" || lowerName == "deno" || lowerName == "bun";
                    if (interpreter)
                    {
                        foreach (string a in r.Args)
                        {
                            if (a.StartsWith("-", StringComparison.Ordinal)) continue;
                            r.DisplayName = Path.GetFileNameWithoutExtension(a);
                            break;
                        }
                    }
                    if (d != null)
                    {
                        foreach (LanguageServerCandidate c in d.Candidates)
                            if (string.Equals(c.Command, r.DisplayName, StringComparison.OrdinalIgnoreCase)) { r.DisplayName = c.DisplayName; break; }
                    }
                    if (resolved == null)
                    {
                        r.Problem = "Configured language server '" + exe + "' was not found.";
                        return r;
                    }
                    r.Executable = resolved;
                    ApplyRestrictedMode(r, workspaceRoot, trusted);
                    return r;
                }
            }

            if (d == null)
            {
                r.Problem = "No built-in language server for " + languageId + ".";
                return r;
            }
            foreach (LanguageServerCandidate c in d.Candidates)
            {
                string exe = FindExecutable(c.Command, workspaceRoot);
                if (exe == null) continue;
                r.Executable = exe;
                r.Args = (string[])c.Args.Clone();
                r.DisplayName = c.DisplayName;
                if (d.Id == "java") r.Args = new string[] { "-data", JdtlsDataDir(workspaceRoot) };
                ApplyRestrictedMode(r, workspaceRoot, trusted);
                return r;
            }
            r.DisplayName = d.Candidates.Count > 0 ? d.Candidates[0].DisplayName : d.DisplayName;
            r.InstallCommand = GetInstallCommand(d, workspaceRoot);
            r.Problem = "No " + d.DisplayName + " language server found (looked for " + string.Join(", ", d.Candidates.Select(c => c.Command).Distinct().ToArray()) + ").";
            return r;
        }

        /// <summary>
        /// Restricted Mode rules for a resolved server: servers that run project code by design are not started; the
        /// TypeScript server is pinned to a TypeScript that doesn't come from the workspace (typescript-language-server
        /// would otherwise load node_modules/typescript); pyright / basedpyright don't start when the project's config
        /// names a virtual environment (pyright would execute its interpreter); lua-language-server doesn't start when
        /// .luarc loads a runtime plugin.
        /// </summary>
        private static void ApplyRestrictedMode(LanguageServerResolution r, string workspaceRoot, bool trusted)
        {
            if (trusted || r.Definition == null) return;
            LanguageServerDefinition d = r.Definition;
            if (d.ExecutesProjectCode)
            {
                r.Restricted = true;
                r.Problem = r.DisplayName + " runs code from the project (build scripts / project import), so it doesn't start in Restricted Mode. Trust this folder to enable it.";
                return;
            }
            if (d.Id == "typescript")
            {
                string tsserver = FindNonWorkspaceTsserver(r.Executable, workspaceRoot);
                if (tsserver == null)
                {
                    r.Restricted = true;
                    r.Problem = "In Restricted Mode the TypeScript server must not load the workspace's own TypeScript, and no global TypeScript was found next to " +
                                r.DisplayName + " (install it with: npm i -g typescript). Trust this folder to use the workspace version.";
                    return;
                }
                r.InitializationOptions = new Dictionary<string, object>(StringComparer.Ordinal);
                r.InitializationOptions["tsserver"] = LspJson.Obj("path", tsserver);
                return;
            }
            if (d.Id == "python" && WorkspaceNamesPythonEnvironment(workspaceRoot) && r.DisplayName != null &&
                (r.DisplayName.IndexOf("pyright", StringComparison.OrdinalIgnoreCase) >= 0))
            {
                r.Restricted = true;
                r.Problem = "This project's pyright configuration selects a virtual environment, whose interpreter " + r.DisplayName +
                            " would run; that doesn't happen in Restricted Mode. Trust this folder to enable it.";
                return;
            }
            if (d.Id == "lua" && LuaConfigLoadsPlugin(workspaceRoot))
            {
                r.Restricted = true;
                r.Problem = "This project's .luarc file loads a runtime plugin (Lua code run by the server), so lua-language-server doesn't start in Restricted Mode. Trust this folder to enable it.";
            }
        }

        /// <summary>A tsserver.js that is not inside <paramref name="workspaceRoot"/>: bundled with / installed next to the server.</summary>
        public static string FindNonWorkspaceTsserver(string serverExe, string workspaceRoot)
        {
            if (string.IsNullOrEmpty(serverExe)) return null;
            List<string> starts = new List<string>();
            try { starts.Add(Path.GetDirectoryName(Path.GetFullPath(serverExe))); } catch { }
            try
            {
                if (!LspText.IsWindows())
                {
                    string real = LlmHarnessAndAgentEngine.ResolveRealPathUnix(serverExe);
                    if (!string.IsNullOrEmpty(real)) starts.Add(Path.GetDirectoryName(real));
                }
            }
            catch { }
            string wsKey = string.IsNullOrEmpty(workspaceRoot) ? null : LspText.PathKey(workspaceRoot) + "/";
            foreach (string start in starts)
            {
                string dir = start;
                for (int i = 0; i < 6 && !string.IsNullOrEmpty(dir); i++)
                {
                    string[] cands = new string[]
                    {
                        JoinPath(dir, "node_modules", "typescript", "lib", "tsserver.js"),
                        JoinPath(dir, "lib", "node_modules", "typescript", "lib", "tsserver.js"),
                        JoinPath(dir, "lib", "node_modules", "typescript-language-server", "node_modules", "typescript", "lib", "tsserver.js")
                    };
                    foreach (string c in cands)
                    {
                        try
                        {
                            if (!File.Exists(c)) continue;
                            if (wsKey != null && LspText.PathKey(c).StartsWith(wsKey, StringComparison.Ordinal)) continue;
                            return c;
                        }
                        catch { }
                    }
                    dir = Path.GetDirectoryName(dir);
                }
            }
            return null;
        }

        private static string JoinPath(string first, params string[] parts)
        {
            string r = first;
            foreach (string p in parts) r = Path.Combine(r, p);
            return r;
        }

        /// <summary>pyrightconfig.json / pyproject.toml [tool.pyright|basedpyright] names "venv" or "venvPath".</summary>
        public static bool WorkspaceNamesPythonEnvironment(string workspaceRoot)
        {
            if (string.IsNullOrEmpty(workspaceRoot)) return false;
            try
            {
                string cfg = Path.Combine(workspaceRoot, "pyrightconfig.json");
                if (File.Exists(cfg))
                {
                    string t = File.ReadAllText(cfg);
                    if (t.IndexOf("\"venv", StringComparison.Ordinal) >= 0 || t.IndexOf("pythonPath", StringComparison.Ordinal) >= 0) return true;
                }
                string pp = Path.Combine(workspaceRoot, "pyproject.toml");
                if (File.Exists(pp))
                {
                    string t = File.ReadAllText(pp);
                    int sec = t.IndexOf("[tool.pyright]", StringComparison.Ordinal);
                    if (sec < 0) sec = t.IndexOf("[tool.basedpyright]", StringComparison.Ordinal);
                    if (sec >= 0)
                    {
                        int next = t.IndexOf("\n[", sec + 5, StringComparison.Ordinal);
                        string body = next > 0 ? t.Substring(sec, next - sec) : t.Substring(sec);
                        if (System.Text.RegularExpressions.Regex.IsMatch(body, @"(?m)^\s*(venv|venvPath|pythonPath)\s*=")) return true;
                    }
                }
            }
            catch { }
            return false;
        }

        /// <summary>.luarc.json(c) configures runtime.plugin.</summary>
        public static bool LuaConfigLoadsPlugin(string workspaceRoot)
        {
            if (string.IsNullOrEmpty(workspaceRoot)) return false;
            foreach (string n in new string[] { ".luarc.json", ".luarc.jsonc" })
            {
                try
                {
                    string p = Path.Combine(workspaceRoot, n);
                    if (File.Exists(p) && File.ReadAllText(p).IndexOf("plugin", StringComparison.OrdinalIgnoreCase) >= 0) return true;
                }
                catch { }
            }
            return false;
        }

        /// <summary>
        /// jdtls workspace (-data) folder for a project root. Stable across launches (SHA-1 of the normalized path - not
        /// string.GetHashCode, which .NET Core randomizes per process) and kept in the user cache folder, not in temp, so
        /// jdtls reuses its index instead of re-importing the project and leaving a new multi-hundred-MB folder each session.
        /// </summary>
        public static string JdtlsDataDir(string root)
        {
            string norm = string.IsNullOrEmpty(root) ? "default" : LspText.PathKey(root);
            string key = AgentHash.Sha1Hex(Encoding.UTF8.GetBytes(norm)).Substring(0, 16);
            string name = Path.GetFileName((root ?? "").TrimEnd('/', '\\'));
            StringBuilder safe = new StringBuilder();
            foreach (char c in name ?? "")
            {
                if (safe.Length >= 32) break;
                safe.Append(char.IsLetterOrDigit(c) || c == '-' || c == '_' || c == '.' ? c : '_');
            }
            return Path.Combine(Path.Combine(UserCacheDir(), "jdtls"), (safe.Length > 0 ? safe + "-" : "") + key);
        }

        /// <summary>Per-user cache folder for codeLess (~/Library/Caches/codeLess, $XDG_CACHE_HOME/codeLess, %LOCALAPPDATA%/codeLess/Cache).</summary>
        public static string UserCacheDir()
        {
            string baseDir = null;
            try
            {
                if (LspText.IsWindows())
                {
                    string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                    if (!string.IsNullOrEmpty(local)) baseDir = Path.Combine(Path.Combine(local, "codeLess"), "Cache");
                }
                else if (LspText.IsMacOS())
                {
                    baseDir = Path.Combine(Path.Combine(Path.Combine(HomeDir(), "Library"), "Caches"), "codeLess");
                }
                else
                {
                    string xdg = Environment.GetEnvironmentVariable("XDG_CACHE_HOME");
                    baseDir = Path.Combine(!string.IsNullOrEmpty(xdg) && Path.IsPathRooted(xdg) ? xdg : Path.Combine(HomeDir(), ".cache"), "codeLess");
                }
            }
            catch { }
            if (string.IsNullOrEmpty(baseDir)) baseDir = Path.Combine(Path.GetTempPath(), "codeLess-cache");
            return baseDir;
        }

        // ---------------------------------------------------------------- executable discovery

        public static string HomeDir()
        {
            string h = Environment.GetEnvironmentVariable("HOME");
            if (string.IsNullOrEmpty(h)) h = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            return h ?? "";
        }

        /// <summary>Ordered list of directories searched for language server executables.</summary>
        public static List<string> SearchDirectories(string workspaceRoot)
        {
            List<string> dirs = new List<string>();
            string extra = Environment.GetEnvironmentVariable(ExtraPathEnvVar);
            if (!string.IsNullOrEmpty(extra))
                foreach (string p in extra.Split(Path.PathSeparator)) AddDir(dirs, p);

            // Workspace-local servers (node_modules/.bin, venvs) only for trusted folders: in Restricted Mode a cloned repo
            // must not be able to plant "node_modules/.bin/typescript-language-server" and have it run on open.
            if (!string.IsNullOrEmpty(workspaceRoot) && WorkspaceTrust.IsTrusted(workspaceRoot))
            {
                AddDir(dirs, Path.Combine(workspaceRoot, "node_modules", ".bin"));
                foreach (string venv in new string[] { ".venv", "venv", "env", ".env" })
                    AddDir(dirs, Path.Combine(workspaceRoot, venv, LspText.IsWindows() ? "Scripts" : "bin"));
            }
            string virtualEnv = Environment.GetEnvironmentVariable("VIRTUAL_ENV");
            if (!string.IsNullOrEmpty(virtualEnv)) AddDir(dirs, Path.Combine(virtualEnv, LspText.IsWindows() ? "Scripts" : "bin"));

            string path = Environment.GetEnvironmentVariable("PATH") ?? "";
            foreach (string p in path.Split(Path.PathSeparator)) AddDir(dirs, p.Trim().Trim('"'));

            foreach (string c in CommonDirectories()) AddDir(dirs, c);
            return dirs;
        }

        public static List<string> CommonDirectories()
        {
            List<string> dirs = new List<string>();
            string home = HomeDir();
            if (LspText.IsWindows())
            {
                string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
                string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                string pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
                if (!string.IsNullOrEmpty(appData)) dirs.Add(Path.Combine(appData, "npm"));
                dirs.Add(Path.Combine(home, ".cargo", "bin"));
                dirs.Add(Path.Combine(home, "go", "bin"));
                dirs.Add(Path.Combine(home, ".dotnet", "tools"));
                dirs.Add(Path.Combine(home, "scoop", "shims"));
                dirs.Add(Path.Combine(home, ".local", "bin"));
                if (!string.IsNullOrEmpty(local))
                {
                    dirs.Add(Path.Combine(local, "Microsoft", "WinGet", "Links"));
                    dirs.Add(Path.Combine(local, "Programs", "Python", "Scripts"));
                }
                if (!string.IsNullOrEmpty(pf))
                {
                    dirs.Add(Path.Combine(pf, "LLVM", "bin"));
                    dirs.Add(Path.Combine(pf, "nodejs"));
                    dirs.Add(Path.Combine(pf, "Go", "bin"));
                }
            }
            else
            {
                dirs.Add("/opt/homebrew/bin");
                dirs.Add("/usr/local/bin");
                dirs.Add(Path.Combine(home, ".local", "bin"));
                dirs.Add(Path.Combine(home, ".cargo", "bin"));
                string gopath = Environment.GetEnvironmentVariable("GOPATH");
                if (!string.IsNullOrEmpty(gopath)) dirs.Add(Path.Combine(gopath, "bin"));
                dirs.Add(Path.Combine(home, "go", "bin"));
                dirs.Add(Path.Combine(home, ".dotnet", "tools"));
                dirs.Add(Path.Combine(home, ".npm-global", "bin"));
                dirs.Add(Path.Combine(home, ".volta", "bin"));
                dirs.Add(Path.Combine(home, ".bun", "bin"));
                dirs.Add(Path.Combine(home, ".yarn", "bin"));
                dirs.Add(Path.Combine(home, "Library", "pnpm"));
                dirs.Add(Path.Combine(home, ".local", "share", "pnpm"));
                dirs.Add(Path.Combine(home, ".asdf", "shims"));
                dirs.Add(Path.Combine(home, ".pyenv", "shims"));
                dirs.Add("/usr/bin");
                dirs.Add("/bin");
                dirs.Add("/snap/bin");
                dirs.Add("/opt/local/bin");
                dirs.Add("/opt/homebrew/opt/llvm/bin");
                dirs.Add("/usr/local/opt/llvm/bin");
                dirs.Add("/Library/Developer/CommandLineTools/usr/bin");
                // nvm: newest node version first
                try
                {
                    string nvm = Path.Combine(home, ".nvm", "versions", "node");
                    if (Directory.Exists(nvm))
                    {
                        List<string> versions = Directory.GetDirectories(nvm).ToList();
                        versions.Sort(delegate (string a, string b) { return CompareVersionDirs(b, a); });
                        foreach (string v in versions) dirs.Add(Path.Combine(v, "bin"));
                    }
                }
                catch { }
            }
            return dirs;
        }

        private static int CompareVersionDirs(string a, string b)
        {
            Version va, vb;
            bool pa = Version.TryParse(Path.GetFileName(a).TrimStart('v'), out va);
            bool pb = Version.TryParse(Path.GetFileName(b).TrimStart('v'), out vb);
            if (pa && pb) return va.CompareTo(vb);
            return string.CompareOrdinal(a, b);
        }

        private static void AddDir(List<string> dirs, string d)
        {
            if (string.IsNullOrEmpty(d)) return;
            foreach (string x in dirs) if (string.Equals(x, d, LspText.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)) return;
            dirs.Add(d);
        }

        /// <summary>Finds an executable by name in <see cref="SearchDirectories"/> (+ npm global prefix). Cached for 20 s.</summary>
        public static string FindExecutable(string name, string workspaceRoot)
        {
            if (string.IsNullOrEmpty(name)) return null;
            string cacheKey = name + "|" + (workspaceRoot ?? "") + "|" + (WorkspaceTrust.IsTrusted(workspaceRoot) ? "t" : "r");
            lock (cacheLock)
            {
                KeyValuePair<DateTime, string> hit;
                if (exeCache.TryGetValue(cacheKey, out hit) && (DateTime.UtcNow - hit.Key).TotalSeconds < 20) return hit.Value;
            }
            string found = FindInDirs(name, SearchDirectories(workspaceRoot));
            if (found == null && name != "npm" && name != "node")
            {
                string npmBin = GetNpmGlobalBin(workspaceRoot);
                if (npmBin != null) found = FindInDirs(name, new List<string> { npmBin });
            }
            lock (cacheLock) { exeCache[cacheKey] = new KeyValuePair<DateTime, string>(DateTime.UtcNow, found); }
            return found;
        }

        public static void ClearCache()
        {
            lock (cacheLock) { exeCache.Clear(); }
        }

        private static string FindInDirs(string name, List<string> dirs)
        {
            string[] exts = LspText.IsWindows() ? new string[] { ".exe", ".cmd", ".bat", ".com", "" } : new string[] { "" };
            foreach (string dir in dirs)
            {
                try
                {
                    if (!Directory.Exists(dir)) continue;
                    foreach (string ext in exts)
                    {
                        string candidate = Path.Combine(dir, name + ext);
                        if (File.Exists(candidate) && (LspText.IsWindows() ? ext.Length > 0 : true)) return candidate;
                    }
                }
                catch { }
            }
            return null;
        }

        /// <summary>The npm global "bin" directory (`npm config get prefix`), probed once per session.</summary>
        private static string GetNpmGlobalBin(string workspaceRoot)
        {
            lock (cacheLock)
            {
                if (npmGlobalBinProbed) return npmGlobalBin;
                npmGlobalBinProbed = true;
            }
            string result = null;
            try
            {
                string prefixEnv = Environment.GetEnvironmentVariable("NPM_CONFIG_PREFIX") ?? Environment.GetEnvironmentVariable("npm_config_prefix");
                string prefix = prefixEnv;
                if (string.IsNullOrEmpty(prefix))
                {
                    string npm = FindInDirs("npm", SearchDirectories(workspaceRoot));
                    if (npm != null) prefix = RunAndCapture(npm, "config get prefix", 4000);
                }
                if (!string.IsNullOrEmpty(prefix))
                {
                    prefix = prefix.Trim();
                    string bin = LspText.IsWindows() ? prefix : Path.Combine(prefix, "bin");
                    if (Directory.Exists(bin)) result = bin;
                }
            }
            catch { }
            lock (cacheLock) { npmGlobalBin = result; }
            return result;
        }

        /// <summary>
        /// Runs a short probe (e.g. `npm config get prefix`) with a real timeout: stdout and stderr are drained asynchronously
        /// (a chatty stderr can't fill its pipe and block the child) and a hung probe is killed with its process tree. Runs in
        /// the user's home folder so a project's .npmrc can't redirect the probe.
        /// </summary>
        internal static string RunAndCapture(string exe, string args, int timeoutMs)
        {
            Process p = null;
            try
            {
                ProcessStartInfo psi = BuildStartInfo(exe, LanguageServerUserSettings.SplitCommandLine(args).ToArray(), null);
                psi.RedirectStandardInput = false;
                string home = HomeDir();
                if (!string.IsNullOrEmpty(home) && Directory.Exists(home)) psi.WorkingDirectory = home;
                StringBuilder output = new StringBuilder();
                object gate = new object();
                p = new Process();
                p.StartInfo = psi;
                ManualResetEvent eof = new ManualResetEvent(false);
                p.OutputDataReceived += delegate (object s, DataReceivedEventArgs e)
                {
                    if (e.Data == null) { eof.Set(); return; }
                    lock (gate) { if (output.Length < 65536) output.Append(e.Data).Append('\n'); }
                };
                p.ErrorDataReceived += delegate (object s, DataReceivedEventArgs e) { };
                p.Start();
                p.BeginOutputReadLine();
                p.BeginErrorReadLine();
                if (!p.WaitForExit(Math.Max(100, timeoutMs)))
                {
                    LanguageServerManager.KillProcessTree(p);
                    return null;
                }
                eof.WaitOne(1000); // bounded: a grandchild holding the pipe open must not hang the probe
                lock (gate) { return output.ToString(); }
            }
            catch { if (p != null) LanguageServerManager.KillProcessTree(p); return null; }
            finally { if (p != null) { try { p.Dispose(); } catch { } } }
        }

        /// <summary>Finds a Python interpreter inside a workspace virtualenv (for pyright's python.pythonPath).</summary>
        public static string FindWorkspaceVenvPython(string workspaceRoot)
        {
            if (string.IsNullOrEmpty(workspaceRoot)) return null;
            if (!WorkspaceTrust.IsTrusted(workspaceRoot)) return null; // Restricted Mode: never hand the server a workspace interpreter
            foreach (string venv in new string[] { ".venv", "venv", "env", ".env" })
            {
                string p = LspText.IsWindows()
                    ? Path.Combine(workspaceRoot, venv, "Scripts", "python.exe")
                    : Path.Combine(workspaceRoot, venv, "bin", "python");
                try { if (File.Exists(p)) return p; } catch { }
            }
            return null;
        }

        /// <summary>Root for a file outside the open workspace: nearest ancestor with a project marker, else the file's folder.</summary>
        public static string FindProjectRoot(string filePath, LanguageServerDefinition d)
        {
            string dir;
            try { dir = Path.GetDirectoryName(Path.GetFullPath(filePath)); } catch { return null; }
            string start = dir;
            string[] markers = d != null && d.RootMarkers.Length > 0 ? d.RootMarkers : new string[0];
            int guard = 0;
            while (!string.IsNullOrEmpty(dir) && guard++ < 40)
            {
                try
                {
                    foreach (string m in markers)
                    {
                        if (m.StartsWith("*", StringComparison.Ordinal))
                        {
                            if (Directory.GetFiles(dir, m).Length > 0) return dir;
                        }
                        else if (File.Exists(Path.Combine(dir, m)) || Directory.Exists(Path.Combine(dir, m))) return dir;
                    }
                    if (Directory.Exists(Path.Combine(dir, ".git"))) return dir;
                }
                catch { }
                string parent = Path.GetDirectoryName(dir);
                if (parent == dir) break;
                dir = parent;
            }
            return start;
        }

        // ---------------------------------------------------------------- process start

        /// <summary>
        /// Builds a ProcessStartInfo with redirected stdio, no window, and a PATH that also contains the server's own
        /// folder and the common tool folders (a Finder-launched app has a minimal PATH, which breaks
        /// "#!/usr/bin/env node" scripts). Windows .cmd / .bat shims run through cmd.exe.
        /// </summary>
        public static ProcessStartInfo BuildStartInfo(string exe, string[] args, string workingDir)
        {
            ProcessStartInfo psi = new ProcessStartInfo();
            string ext = Path.GetExtension(exe).ToLowerInvariant();
            string argLine = JoinArgs(args);
            if (LspText.IsWindows() && (ext == ".cmd" || ext == ".bat"))
            {
                psi.FileName = Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe";
                psi.Arguments = BuildCmdExeArguments(exe, args);
            }
            else
            {
                psi.FileName = exe;
                psi.Arguments = argLine;
            }
            psi.UseShellExecute = false;
            psi.CreateNoWindow = true;
            psi.RedirectStandardInput = true;
            psi.RedirectStandardOutput = true;
            psi.RedirectStandardError = true;
            if (!string.IsNullOrEmpty(workingDir) && Directory.Exists(workingDir)) psi.WorkingDirectory = workingDir;
            try
            {
                List<string> pathDirs = new List<string>();
                string exeDir = Path.GetDirectoryName(exe);
                if (!string.IsNullOrEmpty(exeDir)) pathDirs.Add(exeDir);
                string cur = Environment.GetEnvironmentVariable("PATH") ?? "";
                // Only absolute PATH entries: "." or relative entries would resolve "node" / "python" inside the workspace
                // (the server's working directory).
                foreach (string p in cur.Split(Path.PathSeparator))
                {
                    string q = p.Trim().Trim('"');
                    if (q.Length > 0 && Path.IsPathRooted(q)) AddDir(pathDirs, q);
                }
                foreach (string c in CommonDirectories()) { try { if (Directory.Exists(c)) AddDir(pathDirs, c); } catch { } }
                psi.EnvironmentVariables["PATH"] = string.Join(Path.PathSeparator.ToString(), pathDirs.ToArray());
                // cmd.exe (npm .cmd shims) otherwise looks for "node.exe" etc. in the current directory - the workspace.
                if (LspText.IsWindows()) psi.EnvironmentVariables["NoDefaultCurrentDirectoryInExePath"] = "1";
            }
            catch { }
            return psi;
        }

        /// <summary>
        /// Arguments for running a .cmd / .bat shim through cmd.exe: every token is quoted for CommandLineToArgvW and then
        /// every cmd metacharacter ( ) % ! ^ " &lt; &gt; &amp; | is caret-escaped, so paths such as C:\Users\R&amp;D\... or
        /// arguments with %VAR% / quotes reach the program literally instead of being split or expanded by cmd.
        /// </summary>
        public static string BuildCmdExeArguments(string exe, string[] args)
        {
            StringBuilder cmd = new StringBuilder();
            cmd.Append(QuoteArgAlways(exe));
            if (args != null)
                foreach (string a in args) cmd.Append(' ').Append(QuoteArgAlways(a));
            return "/d /s /c \"" + CaretEscapeForCmd(cmd.ToString()) + "\"";
        }

        /// <summary>Prefixes every cmd.exe metacharacter with '^'.</summary>
        public static string CaretEscapeForCmd(string s)
        {
            StringBuilder sb = new StringBuilder((s ?? "").Length * 2);
            foreach (char c in s ?? "")
            {
                if ("()%!^\"<>&|".IndexOf(c) >= 0) sb.Append('^');
                sb.Append(c);
            }
            return sb.ToString();
        }

        /// <summary>CommandLineToArgvW quoting that always wraps the argument in double quotes.</summary>
        public static string QuoteArgAlways(string a)
        {
            StringBuilder sb = new StringBuilder("\"");
            int backslashes = 0;
            foreach (char c in a ?? "")
            {
                if (c == '\\') { backslashes++; continue; }
                if (c == '"') { sb.Append('\\', backslashes * 2 + 1); sb.Append('"'); backslashes = 0; continue; }
                sb.Append('\\', backslashes);
                backslashes = 0;
                sb.Append(c);
            }
            sb.Append('\\', backslashes * 2);
            sb.Append('"');
            return sb.ToString();
        }

        public static string JoinArgs(string[] args)
        {
            if (args == null || args.Length == 0) return "";
            StringBuilder sb = new StringBuilder();
            foreach (string a in args)
            {
                if (sb.Length > 0) sb.Append(' ');
                sb.Append(QuoteArg(a));
            }
            return sb.ToString();
        }

        /// <summary>Windows CommandLineToArgvW-compatible quoting (also what .NET uses to split Arguments on Unix).</summary>
        public static string QuoteArg(string a)
        {
            if (a == null) return "\"\"";
            if (a.Length > 0 && a.IndexOfAny(new char[] { ' ', '\t', '"', '\'' }) < 0) return a;
            StringBuilder sb = new StringBuilder("\"");
            int backslashes = 0;
            foreach (char c in a)
            {
                if (c == '\\') { backslashes++; continue; }
                if (c == '"') { sb.Append('\\', backslashes * 2 + 1); sb.Append('"'); backslashes = 0; continue; }
                sb.Append('\\', backslashes);
                backslashes = 0;
                sb.Append(c);
            }
            sb.Append('\\', backslashes * 2);
            sb.Append('"');
            return sb.ToString();
        }
    }
}
