using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace codeLess.Studio
{
    public static class AgentPermissions
    {
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
                    string bestMatch = null;
                    int maxLen = -1;
                    foreach (var kvp in settings.AgentPermissionBash)
                    {
                        string pattern = "^" + Regex.Escape(kvp.Key).Replace("\\*", ".*") + "$";
                        if (Regex.IsMatch(cmd, pattern))
                        {
                            if (kvp.Key.Length > maxLen)
                            {
                                maxLen = kvp.Key.Length;
                                bestMatch = kvp.Value.ToLowerInvariant();
                            }
                        }
                    }
                    if (bestMatch != null)
                    {
                        return MergeStricter(baseLevel, bestMatch);
                    }
                }
                
                return baseLevel;
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
