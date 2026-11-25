using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Web.Script.Serialization;

namespace codeLess.Studio
{
    public sealed class CustomAgentDefinition
    {
        public string Name { get; set; }
        public string FilePath { get; set; }
        public string SourceScope { get; set; }
        public string Description { get; set; }
        public string Model { get; set; }
        public bool HasTemperature { get; set; }
        public double Temperature { get; set; }
        public string Mode { get; set; }
        public Dictionary<string, bool> Tools { get; set; }
        public Dictionary<string, string> Permissions { get; set; }
        public Dictionary<string, string> BashPermissions { get; set; }
        public string Prompt { get; set; }

        public CustomAgentDefinition()
        {
            Name = "";
            FilePath = "";
            SourceScope = "";
            Description = "";
            Model = "";
            HasTemperature = false;
            Temperature = 0.15;
            Mode = "primary";
            Tools = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
            Permissions = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            BashPermissions = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            Prompt = "";
        }

        public bool IsPrimary
        {
            get
            {
                string m = (Mode ?? "").Trim().ToLowerInvariant();
                return string.IsNullOrEmpty(m) || m == "primary" || m == "all";
            }
        }

        public bool IsSubagent
        {
            get
            {
                string m = (Mode ?? "").Trim().ToLowerInvariant();
                return m == "subagent" || m == "all";
            }
        }

        public bool IsToolEnabled(string toolName, IEnumerable<string> aliases = null)
        {
            if (Tools == null || Tools.Count == 0 || string.IsNullOrEmpty(toolName))
                return true;

            bool val;
            if (Tools.TryGetValue(toolName, out val))
                return val;

            // Check standard opencode / codeLess aliases
            string[] canonicalAliases = GetCanonicalToolAliases(toolName, aliases);
            for (int i = 0; i < canonicalAliases.Length; i++)
            {
                if (Tools.TryGetValue(canonicalAliases[i], out val))
                    return val;
            }

            // Check wildcard patterns in Tools (e.g. "myserver_*": false or "*": false)
            int bestLen = -1;
            bool? bestVal = null;
            foreach (KeyValuePair<string, bool> kv in Tools)
            {
                if (string.IsNullOrEmpty(kv.Key) || kv.Key.IndexOf('*') < 0) continue;
                string pattern = "^" + Regex.Escape(kv.Key).Replace("\\*", ".*") + "$";
                if (Regex.IsMatch(toolName, pattern, RegexOptions.IgnoreCase) && kv.Key.Length > bestLen)
                {
                    bestLen = kv.Key.Length;
                    bestVal = kv.Value;
                }
            }
            if (bestVal.HasValue) return bestVal.Value;

            return true;
        }

        private static string[] GetCanonicalToolAliases(string toolName, IEnumerable<string> extraAliases)
        {
            List<string> list = new List<string>();
            if (extraAliases != null)
            {
                foreach (string a in extraAliases)
                {
                    if (!string.IsNullOrEmpty(a)) list.Add(a);
                }
            }
            string low = (toolName ?? "").ToLowerInvariant();
            if (low == "write_file" || low == "write") { list.Add("write"); list.Add("write_file"); }
            else if (low == "edit_file" || low == "edit") { list.Add("edit"); list.Add("edit_file"); }
            else if (low == "read_file" || low == "read") { list.Add("read"); list.Add("read_file"); }
            else if (low == "run_terminal" || low == "run_command" || low == "bash") { list.Add("bash"); list.Add("run_terminal"); list.Add("run_command"); }
            else if (low == "grep_search" || low == "grep") { list.Add("grep"); list.Add("grep_search"); }
            else if (low == "list_dir" || low == "list") { list.Add("list"); list.Add("list_dir"); }
            return list.ToArray();
        }
    }

    public sealed class CustomCommandDefinition
    {
        public string Name { get; set; }
        public string FilePath { get; set; }
        public string SourceScope { get; set; }
        public string Description { get; set; }
        public string Agent { get; set; }
        public string Model { get; set; }
        public bool Subtask { get; set; }
        public string Template { get; set; }

        public CustomCommandDefinition()
        {
            Name = "";
            FilePath = "";
            SourceScope = "";
            Description = "";
            Agent = "";
            Model = "";
            Subtask = false;
            Template = "";
        }
    }

    /// <summary>
    /// Parses Markdown files with YAML-ish frontmatter for custom agents and custom commands,
    /// and caches parsed definitions with an mtime + size check. Strict C# 5.
    /// </summary>
    public static class CustomAgentAndCommandRegistry
    {
        private sealed class CachedMarkdownEntry
        {
            public string FullPath;
            public DateTime LastWriteTimeUtc;
            public long FileLength;
            public CustomAgentDefinition Agent;
            public CustomCommandDefinition Command;
        }

        private static readonly object cacheLock = new object();
        private static readonly Dictionary<string, CachedMarkdownEntry> fileCache =
            new Dictionary<string, CachedMarkdownEntry>(StringComparer.OrdinalIgnoreCase);
        private static int totalFileParseCount;

        public static int TotalFileParseCount
        {
            get { lock (cacheLock) { return totalFileParseCount; } }
        }

        public static void ClearCache()
        {
            lock (cacheLock)
            {
                fileCache.Clear();
                totalFileParseCount = 0;
            }
        }

        public static string GetDefaultSettingsDir()
        {
            try
            {
                return Path.GetDirectoryName(IdePersistentSettings.GetSettingsFilePath());
            }
            catch
            {
                return "";
            }
        }

        /// <summary>
        /// Discovers custom agents from &lt;settingsDir&gt;/agent/*.md, .opencode/agent/*.md, and .codeless/agent/*.md.
        /// Precedence (highest wins): .codeless/agent &gt; .opencode/agent &gt; &lt;settingsDir&gt;/agent.
        /// </summary>
        public static Dictionary<string, CustomAgentDefinition> DiscoverAgents(string workspaceRoot, string settingsDirOverride = null)
        {
            Dictionary<string, CustomAgentDefinition> map =
                new Dictionary<string, CustomAgentDefinition>(StringComparer.OrdinalIgnoreCase);

            string settingsDir = settingsDirOverride ?? GetDefaultSettingsDir();
            if (!string.IsNullOrEmpty(settingsDir))
            {
                LoadAgentsFromDir(Path.Combine(settingsDir, "agent"), "settings", map);
                LoadAgentsFromDir(Path.Combine(settingsDir, "agents"), "settings", map);
            }

            if (!string.IsNullOrEmpty(workspaceRoot))
            {
                LoadAgentsFromDir(Path.Combine(workspaceRoot, ".opencode", "agent"), "opencode", map);
                LoadAgentsFromDir(Path.Combine(workspaceRoot, ".opencode", "agents"), "opencode", map);
                LoadAgentsFromDir(Path.Combine(workspaceRoot, ".codeless", "agent"), "codeless", map);
                LoadAgentsFromDir(Path.Combine(workspaceRoot, ".codeless", "agents"), "codeless", map);
            }

            return map;
        }

        public static List<CustomAgentDefinition> GetPrimaryAgents(string workspaceRoot, string settingsDirOverride = null)
        {
            Dictionary<string, CustomAgentDefinition> all = DiscoverAgents(workspaceRoot, settingsDirOverride);
            return all.Values
                .Where(a => a != null && a.IsPrimary && !string.Equals(a.Name, "build", StringComparison.OrdinalIgnoreCase) && !string.Equals(a.Name, "plan", StringComparison.OrdinalIgnoreCase))
                .OrderBy(a => a.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        public static List<CustomAgentDefinition> GetSubagents(string workspaceRoot, string settingsDirOverride = null)
        {
            Dictionary<string, CustomAgentDefinition> all = DiscoverAgents(workspaceRoot, settingsDirOverride);
            return all.Values
                .Where(a => a != null && a.IsSubagent)
                .OrderBy(a => a.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        public static bool TryGetAgent(string name, string workspaceRoot, out CustomAgentDefinition agent, string settingsDirOverride = null)
        {
            agent = null;
            if (string.IsNullOrWhiteSpace(name)) return false;
            Dictionary<string, CustomAgentDefinition> all = DiscoverAgents(workspaceRoot, settingsDirOverride);
            return all.TryGetValue(name.Trim(), out agent) && agent != null;
        }

        public static bool TryGetSubagent(string name, string workspaceRoot, out CustomAgentDefinition subagent, string settingsDirOverride = null)
        {
            subagent = null;
            if (string.IsNullOrWhiteSpace(name)) return false;
            CustomAgentDefinition found;
            if (TryGetAgent(name, workspaceRoot, out found, settingsDirOverride) && found != null && found.IsSubagent)
            {
                subagent = found;
                return true;
            }
            return false;
        }

        /// <summary>
        /// Discovers custom commands from &lt;settingsDir&gt;/command/*.md, .opencode/command/*.md, and .codeless/command/*.md.
        /// Precedence (highest wins): .codeless/command &gt; .opencode/command &gt; &lt;settingsDir&gt;/command.
        /// </summary>
        public static Dictionary<string, CustomCommandDefinition> DiscoverCommands(string workspaceRoot, string settingsDirOverride = null)
        {
            Dictionary<string, CustomCommandDefinition> map =
                new Dictionary<string, CustomCommandDefinition>(StringComparer.OrdinalIgnoreCase);

            string settingsDir = settingsDirOverride ?? GetDefaultSettingsDir();
            if (!string.IsNullOrEmpty(settingsDir))
            {
                LoadCommandsFromDir(Path.Combine(settingsDir, "command"), "settings", map);
                LoadCommandsFromDir(Path.Combine(settingsDir, "commands"), "settings", map);
            }

            if (!string.IsNullOrEmpty(workspaceRoot))
            {
                LoadCommandsFromDir(Path.Combine(workspaceRoot, ".opencode", "command"), "opencode", map);
                LoadCommandsFromDir(Path.Combine(workspaceRoot, ".opencode", "commands"), "opencode", map);
                LoadCommandsFromDir(Path.Combine(workspaceRoot, ".codeless", "command"), "codeless", map);
                LoadCommandsFromDir(Path.Combine(workspaceRoot, ".codeless", "commands"), "codeless", map);
            }

            return map;
        }

        public static bool TryGetCommand(string name, string workspaceRoot, out CustomCommandDefinition command, string settingsDirOverride = null)
        {
            command = null;
            if (string.IsNullOrWhiteSpace(name)) return false;
            string clean = name.Trim().TrimStart('/');
            if (string.IsNullOrEmpty(clean)) return false;
            Dictionary<string, CustomCommandDefinition> all = DiscoverCommands(workspaceRoot, settingsDirOverride);
            return all.TryGetValue(clean, out command) && command != null;
        }

        private static void LoadAgentsFromDir(string dir, string scope, Dictionary<string, CustomAgentDefinition> target)
        {
            if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) return;
            string[] files;
            try { files = Directory.GetFiles(dir, "*.md"); }
            catch { return; }

            Array.Sort(files, StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < files.Length; i++)
            {
                CustomAgentDefinition parsed = GetOrParseAgentFile(files[i], scope);
                if (parsed != null && !string.IsNullOrEmpty(parsed.Name))
                {
                    target[parsed.Name] = parsed;
                }
            }
        }

        private static void LoadCommandsFromDir(string dir, string scope, Dictionary<string, CustomCommandDefinition> target)
        {
            if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) return;
            string[] files;
            try { files = Directory.GetFiles(dir, "*.md"); }
            catch { return; }

            Array.Sort(files, StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < files.Length; i++)
            {
                CustomCommandDefinition parsed = GetOrParseCommandFile(files[i], scope);
                if (parsed != null && !string.IsNullOrEmpty(parsed.Name))
                {
                    target[parsed.Name] = parsed;
                }
            }
        }

        public static CustomAgentDefinition GetOrParseAgentFile(string filePath, string scope = "")
        {
            if (string.IsNullOrEmpty(filePath) || !File.Exists(filePath)) return null;
            try
            {
                string full = Path.GetFullPath(filePath);
                FileInfo fi = new FileInfo(full);
                DateTime mtime = fi.LastWriteTimeUtc;
                long len = fi.Length;

                lock (cacheLock)
                {
                    CachedMarkdownEntry cached;
                    if (fileCache.TryGetValue(full, out cached) &&
                        cached != null &&
                        cached.Agent != null &&
                        cached.LastWriteTimeUtc == mtime &&
                        cached.FileLength == len)
                    {
                        cached.Agent.SourceScope = scope ?? cached.Agent.SourceScope;
                        return cached.Agent;
                    }
                }

                string content = File.ReadAllText(full);
                string name = Path.GetFileNameWithoutExtension(full);
                CustomAgentDefinition agent = ParseAgentMarkdown(name, content, full, scope);

                lock (cacheLock)
                {
                    CachedMarkdownEntry entry;
                    if (!fileCache.TryGetValue(full, out entry) || entry == null)
                    {
                        entry = new CachedMarkdownEntry();
                        fileCache[full] = entry;
                    }
                    entry.FullPath = full;
                    entry.LastWriteTimeUtc = mtime;
                    entry.FileLength = len;
                    entry.Agent = agent;
                    totalFileParseCount++;
                }

                return agent;
            }
            catch
            {
                return null;
            }
        }

        public static CustomCommandDefinition GetOrParseCommandFile(string filePath, string scope = "")
        {
            if (string.IsNullOrEmpty(filePath) || !File.Exists(filePath)) return null;
            try
            {
                string full = Path.GetFullPath(filePath);
                FileInfo fi = new FileInfo(full);
                DateTime mtime = fi.LastWriteTimeUtc;
                long len = fi.Length;

                lock (cacheLock)
                {
                    CachedMarkdownEntry cached;
                    if (fileCache.TryGetValue(full, out cached) &&
                        cached != null &&
                        cached.Command != null &&
                        cached.LastWriteTimeUtc == mtime &&
                        cached.FileLength == len)
                    {
                        cached.Command.SourceScope = scope ?? cached.Command.SourceScope;
                        return cached.Command;
                    }
                }

                string content = File.ReadAllText(full);
                string name = Path.GetFileNameWithoutExtension(full);
                CustomCommandDefinition cmd = ParseCommandMarkdown(name, content, full, scope);

                lock (cacheLock)
                {
                    CachedMarkdownEntry entry;
                    if (!fileCache.TryGetValue(full, out entry) || entry == null)
                    {
                        entry = new CachedMarkdownEntry();
                        fileCache[full] = entry;
                    }
                    entry.FullPath = full;
                    entry.LastWriteTimeUtc = mtime;
                    entry.FileLength = len;
                    entry.Command = cmd;
                    totalFileParseCount++;
                }

                return cmd;
            }
            catch
            {
                return null;
            }
        }

        public static CustomAgentDefinition ParseAgentMarkdown(string name, string markdownContent, string filePath = "", string scope = "")
        {
            CustomAgentDefinition def = new CustomAgentDefinition();
            def.Name = (name ?? "").Trim();
            def.FilePath = filePath ?? "";
            def.SourceScope = scope ?? "";

            string frontmatter;
            string body;
            SplitFrontmatter(markdownContent ?? "", out frontmatter, out body);
            def.Prompt = body.Trim();

            if (!string.IsNullOrEmpty(frontmatter))
            {
                ParseFrontmatterIntoAgent(frontmatter, def);
            }

            return def;
        }

        public static CustomCommandDefinition ParseCommandMarkdown(string name, string markdownContent, string filePath = "", string scope = "")
        {
            CustomCommandDefinition def = new CustomCommandDefinition();
            def.Name = (name ?? "").Trim();
            def.FilePath = filePath ?? "";
            def.SourceScope = scope ?? "";

            string frontmatter;
            string body;
            SplitFrontmatter(markdownContent ?? "", out frontmatter, out body);
            def.Template = body.Trim();

            if (!string.IsNullOrEmpty(frontmatter))
            {
                ParseFrontmatterIntoCommand(frontmatter, def);
            }

            return def;
        }

        private static void SplitFrontmatter(string content, out string frontmatter, out string body)
        {
            frontmatter = "";
            body = content ?? "";
            string norm = body.Replace("\r\n", "\n").Replace("\r", "\n");
            if (!norm.StartsWith("---\n") && norm != "---")
                return;

            int endIdx = norm.IndexOf("\n---", 4, StringComparison.Ordinal);
            if (endIdx < 0) return;

            int afterEnd = endIdx + 4;
            if (afterEnd < norm.Length && norm[afterEnd] != '\n')
            {
                // Make sure "---" is on its own line
                string restLine = norm.Substring(afterEnd);
                int nl = restLine.IndexOf('\n');
                string trailing = nl >= 0 ? restLine.Substring(0, nl) : restLine;
                if (!string.IsNullOrWhiteSpace(trailing))
                    return;
                afterEnd = nl >= 0 ? afterEnd + nl + 1 : norm.Length;
            }
            else if (afterEnd < norm.Length && norm[afterEnd] == '\n')
            {
                afterEnd++;
            }

            frontmatter = norm.Substring(4, endIdx - 4);
            body = afterEnd < norm.Length ? norm.Substring(afterEnd) : "";
        }

        private static void ParseFrontmatterIntoAgent(string fm, CustomAgentDefinition agent)
        {
            string[] lines = fm.Split('\n');
            string currentSection = "";
            string currentSubSection = "";

            for (int i = 0; i < lines.Length; i++)
            {
                string rawLine = lines[i];
                string trimmed = rawLine.Trim();
                if (trimmed.Length == 0 || trimmed.StartsWith("#")) continue;

                int indent = GetIndentCount(rawLine);
                if (indent == 0)
                {
                    currentSection = "";
                    currentSubSection = "";
                    string key;
                    string val;
                    if (!TrySplitKeyValue(trimmed, out key, out val)) continue;
                    string lowKey = key.ToLowerInvariant();

                    if (lowKey == "description")
                    {
                        agent.Description = UnquoteYaml(val);
                    }
                    else if (lowKey == "model")
                    {
                        agent.Model = UnquoteYaml(val);
                    }
                    else if (lowKey == "temperature")
                    {
                        double temp;
                        if (double.TryParse(UnquoteYaml(val), NumberStyles.Float, CultureInfo.InvariantCulture, out temp))
                        {
                            agent.HasTemperature = true;
                            agent.Temperature = temp;
                        }
                    }
                    else if (lowKey == "mode")
                    {
                        agent.Mode = UnquoteYaml(val).ToLowerInvariant();
                    }
                    else if (lowKey == "tools")
                    {
                        if (val.StartsWith("{") && val.EndsWith("}"))
                            ParseInlineBoolMap(val, agent.Tools);
                        else
                            currentSection = "tools";
                    }
                    else if (lowKey == "permission" || lowKey == "permissions")
                    {
                        if (val.StartsWith("{") && val.EndsWith("}"))
                            ParseInlineStringMap(val, agent.Permissions);
                        else
                            currentSection = "permission";
                    }
                }
                else if (currentSection == "tools")
                {
                    string key;
                    string val;
                    if (TrySplitKeyValue(trimmed, out key, out val))
                    {
                        bool b;
                        if (TryParseYamlBool(val, out b))
                            agent.Tools[UnquoteYaml(key)] = b;
                    }
                }
                else if (currentSection == "permission")
                {
                    string key;
                    string val;
                    if (TrySplitKeyValue(trimmed, out key, out val))
                    {
                        string cleanKey = UnquoteYaml(key);
                        string cleanVal = UnquoteYaml(val);
                        if (indent <= 4 && string.Equals(cleanKey, "bash", StringComparison.OrdinalIgnoreCase))
                        {
                            if (string.IsNullOrEmpty(cleanVal))
                            {
                                currentSubSection = "bash";
                            }
                            else if (cleanVal.StartsWith("{") && cleanVal.EndsWith("}"))
                            {
                                ParseInlineStringMap(cleanVal, agent.BashPermissions);
                            }
                            else
                            {
                                agent.Permissions["bash"] = cleanVal.ToLowerInvariant();
                                currentSubSection = "";
                            }
                        }
                        else if (currentSubSection == "bash" && indent > 2)
                        {
                            agent.BashPermissions[cleanKey] = cleanVal.ToLowerInvariant();
                        }
                        else
                        {
                            currentSubSection = "";
                            agent.Permissions[cleanKey] = cleanVal.ToLowerInvariant();
                        }
                    }
                }
            }
        }

        private static void ParseFrontmatterIntoCommand(string fm, CustomCommandDefinition cmd)
        {
            string[] lines = fm.Split('\n');
            for (int i = 0; i < lines.Length; i++)
            {
                string trimmed = lines[i].Trim();
                if (trimmed.Length == 0 || trimmed.StartsWith("#")) continue;
                string key;
                string val;
                if (!TrySplitKeyValue(trimmed, out key, out val)) continue;
                string lowKey = key.ToLowerInvariant();
                if (lowKey == "description") cmd.Description = UnquoteYaml(val);
                else if (lowKey == "agent") cmd.Agent = UnquoteYaml(val);
                else if (lowKey == "model") cmd.Model = UnquoteYaml(val);
                else if (lowKey == "subtask")
                {
                    bool b;
                    if (TryParseYamlBool(val, out b)) cmd.Subtask = b;
                }
            }
        }

        private static int GetIndentCount(string line)
        {
            int count = 0;
            for (int i = 0; i < line.Length; i++)
            {
                if (line[i] == ' ') count++;
                else if (line[i] == '\t') count += 2;
                else break;
            }
            return count;
        }

        private static bool TrySplitKeyValue(string line, out string key, out string val)
        {
            key = "";
            val = "";
            if (string.IsNullOrEmpty(line)) return false;

            bool inSingle = false;
            bool inDouble = false;
            int colonIdx = -1;
            for (int i = 0; i < line.Length; i++)
            {
                char c = line[i];
                if (c == '\'' && !inDouble) inSingle = !inSingle;
                else if (c == '"' && !inSingle) inDouble = !inDouble;
                else if (c == ':' && !inSingle && !inDouble)
                {
                    colonIdx = i;
                    break;
                }
            }
            if (colonIdx <= 0) return false;
            key = line.Substring(0, colonIdx).Trim();
            val = colonIdx + 1 < line.Length ? line.Substring(colonIdx + 1).Trim() : "";
            return key.Length > 0;
        }

        private static string UnquoteYaml(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            string t = s.Trim();
            if (t.Length >= 2 && ((t[0] == '"' && t[t.Length - 1] == '"') || (t[0] == '\'' && t[t.Length - 1] == '\'')))
                return t.Substring(1, t.Length - 2);
            return t;
        }

        private static bool TryParseYamlBool(string val, out bool result)
        {
            result = false;
            string clean = UnquoteYaml(val).ToLowerInvariant();
            if (clean == "true" || clean == "yes" || clean == "on") { result = true; return true; }
            if (clean == "false" || clean == "no" || clean == "off") { result = false; return true; }
            return false;
        }

        private static void ParseInlineBoolMap(string inline, Dictionary<string, bool> target)
        {
            string inner = inline.Trim();
            if (inner.StartsWith("{")) inner = inner.Substring(1);
            if (inner.EndsWith("}")) inner = inner.Substring(0, inner.Length - 1);
            string[] parts = inner.Split(',');
            for (int i = 0; i < parts.Length; i++)
            {
                string k;
                string v;
                if (TrySplitKeyValue(parts[i].Trim(), out k, out v))
                {
                    bool b;
                    if (TryParseYamlBool(v, out b))
                        target[UnquoteYaml(k)] = b;
                }
            }
        }

        private static void ParseInlineStringMap(string inline, Dictionary<string, string> target)
        {
            string inner = inline.Trim();
            if (inner.StartsWith("{")) inner = inner.Substring(1);
            if (inner.EndsWith("}")) inner = inner.Substring(0, inner.Length - 1);
            string[] parts = inner.Split(',');
            for (int i = 0; i < parts.Length; i++)
            {
                string k;
                string v;
                if (TrySplitKeyValue(parts[i].Trim(), out k, out v))
                {
                    target[UnquoteYaml(k)] = UnquoteYaml(v).ToLowerInvariant();
                }
            }
        }

        // ------------------------------------------------------------------ Slash command expansion

        public static List<string> TokenizeArguments(string rawArgs)
        {
            List<string> args = new List<string>();
            if (string.IsNullOrWhiteSpace(rawArgs)) return args;

            StringBuilder cur = new StringBuilder();
            bool inSingle = false;
            bool inDouble = false;
            bool tokenStarted = false;

            for (int i = 0; i < rawArgs.Length; i++)
            {
                char c = rawArgs[i];
                if (c == '\\' && i + 1 < rawArgs.Length && (inDouble || !inSingle))
                {
                    cur.Append(rawArgs[i + 1]);
                    tokenStarted = true;
                    i++;
                    continue;
                }
                if (c == '"' && !inSingle)
                {
                    inDouble = !inDouble;
                    tokenStarted = true;
                    continue;
                }
                if (c == '\'' && !inDouble)
                {
                    inSingle = !inSingle;
                    tokenStarted = true;
                    continue;
                }
                if (char.IsWhiteSpace(c) && !inSingle && !inDouble)
                {
                    if (tokenStarted)
                    {
                        args.Add(cur.ToString());
                        cur.Length = 0;
                        tokenStarted = false;
                    }
                    continue;
                }
                cur.Append(c);
                tokenStarted = true;
            }
            if (tokenStarted)
                args.Add(cur.ToString());

            return args;
        }

        /// <summary>
        /// Checks whether <paramref name="userInput"/> starts with `/name` matching a discovered custom command,
        /// and if so expands `$ARGUMENTS`, `$1..$9`, `@file` includes, and `!`cmd`` (gated behind `run_terminal` permission).
        /// </summary>
        public static bool TryExpandSlashCommand(
            string userInput,
            string workspaceRoot,
            IdePersistentSettings settings,
            string currentMode,
            Func<string, string, bool> onRequestToolApproval,
            Func<string, string, string> shellRunner,
            out string expandedPrompt,
            out CustomCommandDefinition matchedCommand,
            string settingsDirOverride = null)
        {
            expandedPrompt = userInput;
            matchedCommand = null;

            if (string.IsNullOrWhiteSpace(userInput)) return false;
            string trimmed = userInput.Trim();
            if (!trimmed.StartsWith("/") || trimmed.Length < 2) return false;

            int spaceIdx = -1;
            for (int i = 1; i < trimmed.Length; i++)
            {
                if (char.IsWhiteSpace(trimmed[i]))
                {
                    spaceIdx = i;
                    break;
                }
            }

            string cmdName = spaceIdx > 0 ? trimmed.Substring(1, spaceIdx - 1) : trimmed.Substring(1);
            string rawArgs = spaceIdx > 0 ? trimmed.Substring(spaceIdx + 1).Trim() : "";

            if (!TryGetCommand(cmdName, workspaceRoot, out matchedCommand, settingsDirOverride) || matchedCommand == null)
                return false;

            expandedPrompt = ExpandCommandTemplate(
                matchedCommand.Template,
                rawArgs,
                workspaceRoot,
                settings,
                !string.IsNullOrEmpty(matchedCommand.Agent) ? matchedCommand.Agent : currentMode,
                onRequestToolApproval,
                shellRunner,
                settingsDirOverride);
            return true;
        }

        public static string ExpandCommandTemplate(
            string template,
            string rawArgs,
            string workspaceRoot,
            IdePersistentSettings settings,
            string mode,
            Func<string, string, bool> onRequestToolApproval = null,
            Func<string, string, string> shellRunner = null,
            string settingsDirOverride = null)
        {
            if (string.IsNullOrEmpty(template)) return "";
            string result = template;

            // 1. Expand $ARGUMENTS and positional $1..$9
            List<string> posArgs = TokenizeArguments(rawArgs ?? "");
            result = result.Replace("$ARGUMENTS", rawArgs ?? "");
            for (int i = 9; i >= 1; i--)
            {
                string val = (i - 1 < posArgs.Count) ? posArgs[i - 1] : "";
                result = result.Replace("$" + i.ToString(CultureInfo.InvariantCulture), val);
            }

            // 2. Expand !`cmd` shell output placeholders (gated behind run_terminal permission)
            CustomAgentDefinition customAgent = null;
            if (!string.IsNullOrEmpty(mode))
                TryGetAgent(mode, workspaceRoot, out customAgent, settingsDirOverride);

            bool isRestricted = WorkspaceTrust.IsRestricted(workspaceRoot);
            JavaScriptSerializer ser = new JavaScriptSerializer();

            result = Regex.Replace(result, @"!`([^`]+)`", delegate(Match m)
            {
                string cmd = m.Groups[1].Value.Trim();
                if (string.IsNullOrEmpty(cmd)) return "";

                Dictionary<string, object> argsObj = new Dictionary<string, object>();
                argsObj["command"] = cmd;
                string argsJson = ser.Serialize(argsObj);

                string perm = AgentPermissions.EvaluateTool(mode ?? "build", "run_terminal", argsJson, isRestricted, settings, customAgent);
                if (customAgent != null && !customAgent.IsToolEnabled("run_terminal"))
                    perm = "deny";

                if (perm == "deny")
                    return "[Command denied by permission: " + cmd + "]";

                if (perm == "ask")
                {
                    if (onRequestToolApproval == null || !onRequestToolApproval("run_terminal", argsJson))
                        return "[Command approval denied: " + cmd + "]";
                }

                if (shellRunner != null)
                {
                    try { return (shellRunner(cmd, workspaceRoot) ?? "").Trim(); }
                    catch (Exception ex) { return "[Command error: " + ex.Message + "]"; }
                }

                return RunShellCommandCapture(cmd, workspaceRoot);
            });

            // 3. Expand @file includes (with read_file size and line caps + workspace sandbox + binary check)
            result = ExpandAtFileReferences(result, workspaceRoot);

            return result;
        }

        public static string ExpandAtFileReferences(string text, string workspaceRoot)
        {
            if (string.IsNullOrEmpty(text) || string.IsNullOrEmpty(workspaceRoot))
                return text ?? "";

            StringBuilder appendedFiles = new StringBuilder();
            Regex atFileRegex = new Regex(@"(?<![A-Za-z0-9_.\-/])@([A-Za-z0-9_.\-/\\]+)");
            MatchCollection matches = atFileRegex.Matches(text);
            HashSet<string> includedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (Match m in matches)
            {
                string candidateRel = m.Groups[1].Value.TrimEnd('.', ',', ';', ':', ')', ']');
                if (string.IsNullOrEmpty(candidateRel)) continue;

                string fullPath = LlmHarnessAndAgentEngine.ResolveWorkspacePath(workspaceRoot, candidateRel);
                if (!LlmHarnessAndAgentEngine.IsPathInsideWorkspaceStrict(workspaceRoot, fullPath))
                    continue;
                if (!File.Exists(fullPath) || includedPaths.Contains(fullPath))
                    continue;

                includedPaths.Add(fullPath);
                string formatted = ReadFileWithHarnessCaps(fullPath, workspaceRoot);
                if (!string.IsNullOrEmpty(formatted))
                {
                    if (appendedFiles.Length > 0) appendedFiles.AppendLine();
                    appendedFiles.Append(formatted);
                }
            }

            if (appendedFiles.Length > 0)
            {
                return text + "\n\n" + appendedFiles.ToString();
            }
            return text;
        }

        public static string ReadFileWithHarnessCaps(string fullPath, string workspaceRoot, int maxLines = 2000, int maxCharsPerLine = 2000, int maxTotalBytes = 51200)
        {
            if (string.IsNullOrEmpty(fullPath) || !File.Exists(fullPath)) return "";
            string relPath = LlmHarnessAndAgentEngine.GetRelativePath(workspaceRoot, fullPath);
            if (BinaryDetector.IsBinaryFile(fullPath))
                return "<file path=\"" + relPath + "\">\n[Cannot read binary file: " + relPath + "]\n</file>";

            string rawText;
            try { rawText = File.ReadAllText(fullPath); }
            catch { return ""; }

            string[] lines = (rawText ?? "").Replace("\r\n", "\n").Replace("\r", "\n").Split('\n');
            int endLine = Math.Min(lines.Length, maxLines);
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("<file path=\"" + relPath + "\">");
            int byteCount = 0;
            bool truncatedByBytes = false;
            int linesWritten = 0;

            for (int i = 0; i < endLine; i++)
            {
                string lineText = lines[i] ?? "";
                if (lineText.Length > maxCharsPerLine)
                    lineText = lineText.Substring(0, maxCharsPerLine) + "...";
                string formattedLine = string.Format("{0:D5}| {1}", i + 1, lineText);
                int lineBytes = Encoding.UTF8.GetByteCount(formattedLine) + 1;
                if (byteCount + lineBytes > maxTotalBytes)
                {
                    truncatedByBytes = true;
                    break;
                }
                sb.AppendLine(formattedLine);
                byteCount += lineBytes;
                linesWritten++;
            }

            if (truncatedByBytes || linesWritten < lines.Length)
                sb.AppendLine(string.Format("(File has more lines. Showing {0} of {1} lines)", linesWritten, lines.Length));
            else
                sb.AppendLine(string.Format("(End of file - total {0} lines)", lines.Length));

            sb.Append("</file>");
            return sb.ToString();
        }

        private static string RunShellCommandCapture(string command, string workingDirectory)
        {
            try
            {
                ProcessStartInfo psi = new ProcessStartInfo();
                if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                {
                    psi.FileName = "cmd.exe";
                    psi.Arguments = "/c " + command;
                }
                else
                {
                    psi.FileName = "/bin/sh";
                    psi.Arguments = "-c \"" + command.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
                }
                psi.UseShellExecute = false;
                psi.RedirectStandardOutput = true;
                psi.RedirectStandardError = true;
                psi.CreateNoWindow = true;
                if (!string.IsNullOrEmpty(workingDirectory) && Directory.Exists(workingDirectory))
                    psi.WorkingDirectory = workingDirectory;

                using (Process p = Process.Start(psi))
                {
                    if (p == null) return "";
                    string stdout = p.StandardOutput.ReadToEnd();
                    string stderr = p.StandardError.ReadToEnd();
                    if (!p.WaitForExit(10000))
                    {
                        try { p.Kill(); } catch { }
                    }
                    string combined = (stdout ?? "").Trim();
                    if (string.IsNullOrEmpty(combined) && !string.IsNullOrEmpty(stderr))
                        combined = stderr.Trim();
                    return combined;
                }
            }
            catch (Exception ex)
            {
                return "[Command failed: " + ex.Message + "]";
            }
        }
    }
}
