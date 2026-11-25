using System;
using System.Collections.Generic;
using System.Linq;

namespace codeLess.Studio
{
    public sealed class FormattedTodoItem
    {
        public string Id { get; set; }
        public string Content { get; set; }
        public string Status { get; set; }
        public string Priority { get; set; }
        public string Icon { get; set; }
        public bool Strikethrough { get; set; }
        public string DisplayText { get; set; }
    }

    public static class AgentUiParity
    {
        public static string ActiveWorkspaceRoot { get; set; }

        public static List<string> GetPrimaryModeCycle(string workspaceRoot = null, string settingsDirOverride = null)
        {
            string effWs = !string.IsNullOrEmpty(workspaceRoot) ? workspaceRoot : ActiveWorkspaceRoot;
            List<string> modes = new List<string> { "build", "plan" };
            if (!string.IsNullOrEmpty(effWs) || !string.IsNullOrEmpty(settingsDirOverride))
            {
                List<CustomAgentDefinition> customPrimary = CustomAgentAndCommandRegistry.GetPrimaryAgents(effWs, settingsDirOverride);
                for (int i = 0; i < customPrimary.Count; i++)
                {
                    string name = customPrimary[i].Name;
                    if (!string.IsNullOrEmpty(name) && !modes.Contains(name, StringComparer.OrdinalIgnoreCase))
                        modes.Add(name);
                }
            }
            return modes;
        }

        public static string NormalizeBuildPlanMode(string mode)
        {
            string norm = (mode ?? "").Trim().ToLowerInvariant();
            if (norm == "plan" || norm == "composer")
                return "plan";
            return "build";
        }

        public static string NormalizeAgentMode(string mode, string workspaceRoot = null, string settingsDirOverride = null)
        {
            string norm = (mode ?? "").Trim().ToLowerInvariant();
            if (norm == "plan" || norm == "composer")
                return "plan";
            if (norm == "build" || norm == "agent" || string.IsNullOrEmpty(norm))
                return "build";

            string effWs = !string.IsNullOrEmpty(workspaceRoot) ? workspaceRoot : ActiveWorkspaceRoot;
            if (!string.IsNullOrEmpty(effWs) || !string.IsNullOrEmpty(settingsDirOverride))
            {
                CustomAgentDefinition custom;
                if (CustomAgentAndCommandRegistry.TryGetAgent(norm, effWs, out custom, settingsDirOverride) && custom != null && custom.IsPrimary)
                    return custom.Name;
            }
            return "build";
        }

        public static string CycleBuildPlanMode(string currentMode)
        {
            return CycleBuildPlanMode(currentMode, ActiveWorkspaceRoot, null);
        }

        public static string CycleBuildPlanMode(string currentMode, string workspaceRoot, string settingsDirOverride = null)
        {
            List<string> modes = GetPrimaryModeCycle(workspaceRoot, settingsDirOverride);
            string norm = NormalizeAgentMode(currentMode, workspaceRoot, settingsDirOverride);
            int idx = -1;
            for (int i = 0; i < modes.Count; i++)
            {
                if (string.Equals(modes[i], norm, StringComparison.OrdinalIgnoreCase))
                {
                    idx = i;
                    break;
                }
            }
            if (idx < 0) return "plan";
            return modes[(idx + 1) % modes.Count];
        }

        public static string GetModePillLabel(string currentMode)
        {
            return GetModePillLabel(currentMode, null, null);
        }

        public static string GetModePillLabel(string currentMode, string workspaceRoot, string settingsDirOverride = null)
        {
            string norm = NormalizeAgentMode(currentMode, workspaceRoot, settingsDirOverride);
            if (norm == "plan") return "Plan";
            if (norm == "build") return "Build";
            return char.ToUpperInvariant(norm[0]) + (norm.Length > 1 ? norm.Substring(1) : "");
        }

        public static FormattedTodoItem FormatTodoItem(Dictionary<string, object> todo)
        {
            if (todo == null)
            {
                return new FormattedTodoItem
                {
                    Id = "",
                    Content = "",
                    Status = "pending",
                    Priority = "medium",
                    Icon = "\u2610",
                    Strikethrough = false,
                    DisplayText = "\u2610 : "
                };
            }

            string id = todo.ContainsKey("id") && todo["id"] != null ? Convert.ToString(todo["id"]) : "";
            string content = todo.ContainsKey("content") && todo["content"] != null ? Convert.ToString(todo["content"]) : "";
            string status = todo.ContainsKey("status") && todo["status"] != null
                ? Convert.ToString(todo["status"]).Trim().ToLowerInvariant()
                : "pending";
            string priority = todo.ContainsKey("priority") && todo["priority"] != null
                ? Convert.ToString(todo["priority"]).Trim().ToLowerInvariant()
                : "medium";

            string icon = "\u2610"; // ☐
            bool strike = false;
            if (status == "in_progress")
            {
                icon = "\u25D0"; // ◐
            }
            else if (status == "completed")
            {
                icon = "\u2611"; // ☑
            }
            else if (status == "cancelled")
            {
                icon = "\u2612"; // ☒
                strike = true;
            }

            return new FormattedTodoItem
            {
                Id = id,
                Content = content,
                Status = status,
                Priority = priority,
                Icon = icon,
                Strikethrough = strike,
                DisplayText = icon + " " + id + ": " + content
            };
        }

        public static List<FormattedTodoItem> FormatTodoList(List<Dictionary<string, object>> todos)
        {
            var list = new List<FormattedTodoItem>();
            if (todos == null) return list;
            for (int i = 0; i < todos.Count; i++)
            {
                list.Add(FormatTodoItem(todos[i]));
            }
            return list;
        }
    }
}
