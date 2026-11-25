using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace codeLess.Studio
{
    public static class AgentPermissions
    {
        private static readonly HashSet<string> BuiltInToolNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "read_file", "read",
            "write_file", "write",
            "edit_file", "edit",
            "multiedit",
            "list_dir", "list",
            "grep_search", "grep",
            "glob",
            "run_terminal", "run_command", "bash",
            "get_diagnostics",
            "ast_outline",
            "todowrite",
            "todoread",
            "webfetch",
            "task"
        };

        public static bool IsMcpToolName(string toolName)
        {
            if (string.IsNullOrEmpty(toolName)) return false;
            if (BuiltInToolNames.Contains(toolName)) return false;
            return toolName.IndexOf('_') > 0;
        }

        public static string MatchWildcardPatterns(string input, Dictionary<string, string> patterns)
        {
            if (patterns == null || patterns.Count == 0 || input == null) return null;
            string bestMatch = null;
            int maxLen = -1;
            foreach (KeyValuePair<string, string> kvp in patterns)
            {
                if (string.IsNullOrEmpty(kvp.Key) || string.IsNullOrEmpty(kvp.Value)) continue;
                string pattern = "^" + Regex.Escape(kvp.Key).Replace("\\*", ".*") + "$";
                if (Regex.IsMatch(input, pattern, RegexOptions.IgnoreCase))
                {
                    if (kvp.Key.Length > maxLen)
                    {
                        maxLen = kvp.Key.Length;
                        bestMatch = kvp.Value.Trim().ToLowerInvariant();
                    }
                }
            }
            return bestMatch;
        }

        public static string EvaluateTool(string mode, string toolName, string argsJson, bool restricted, IdePersistentSettings settings)
        {
            string baseLevel = (settings != null && settings.RequireAgentToolApproval) ? "ask" : "allow";
            if (restricted) baseLevel = "ask";

            bool isPlan = string.Equals(mode, "plan", StringComparison.OrdinalIgnoreCase);

            if (toolName == "write_file" || toolName == "edit_file" || toolName == "multiedit")
            {
                if (isPlan) return "deny";
                
                string userPerm = settings != null && !string.IsNullOrEmpty(settings.AgentPermissionEdit) ? settings.AgentPermissionEdit.ToLowerInvariant() : "allow";
                return MergeStricter(baseLevel, userPerm);
            }
            
            if (toolName == "webfetch")
            {
                string userPerm = settings != null && !string.IsNullOrEmpty(settings.AgentPermissionWebfetch) ? settings.AgentPermissionWebfetch.ToLowerInvariant() : "ask";
                return MergeStricter(baseLevel, userPerm);
            }

            if (toolName == "run_command" || toolName == "run_terminal" || toolName == "bash")
            {
                string cmd = "";
                try 
                {
                    var js = new System.Web.Script.Serialization.JavaScriptSerializer();
                    var args = js.Deserialize<Dictionary<string, object>>(argsJson);
                    if (args != null && args.ContainsKey("command")) cmd = args["command"].ToString();
                } 
                catch { }

                if (isPlan)
                {
                    string c = cmd.Trim().Split(' ')[0];
                    string[] readOnly = new[] { "ls", "cat", "head", "tail", "rg", "grep", "find", "git", "pwd", "wc", "tree" };
                    if (readOnly.Contains(c))
                    {
                        if (c == "git")
                        {
                            if (!cmd.Contains("status") && !cmd.Contains("diff") && !cmd.Contains("log") && !cmd.Contains("show"))
                                return "ask";
                        }
                    }
                    else
                    {
                        return MergeStricter(baseLevel, "ask");
                    }
                }

                if (settings != null && settings.AgentPermissionBash != null && settings.AgentPermissionBash.Count > 0)
                {
                    string bestMatch = MatchWildcardPatterns(cmd, settings.AgentPermissionBash);
                    if (bestMatch != null)
                    {
                        return MergeStricter(baseLevel, bestMatch);
                    }
                }
                
                return baseLevel;
            }

            if (IsMcpToolName(toolName))
            {
                // MCP tools default to "ask" per opencode / M9a specification, configurable via AgentPermissionMcp
                // or AgentPermissionBash wildcard patterns (e.g. "myserver_*": "allow" / "deny").
                string matched = null;
                if (settings != null)
                {
                    matched = MatchWildcardPatterns(toolName, settings.AgentPermissionMcp);
                    if (matched == null)
                        matched = MatchWildcardPatterns(toolName, settings.AgentPermissionBash);
                }

                if (matched != null)
                {
                    if (matched == "deny") return "deny";
                    if (matched == "ask") return "ask";
                    if (matched == "allow") return restricted ? "ask" : "allow";
                }
                return "ask";
            }

            return baseLevel;
        }

        private static string MergeStricter(string a, string b)
        {
            if (a == "deny" || b == "deny") return "deny";
            if (a == "ask" || b == "ask") return "ask";
            return "allow";
        }
    }
}
