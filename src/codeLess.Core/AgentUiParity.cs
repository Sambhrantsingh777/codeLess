using System;
using System.Collections.Generic;

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
        public static string NormalizeBuildPlanMode(string mode)
        {
            string norm = (mode ?? "").Trim().ToLowerInvariant();
            if (norm == "plan" || norm == "composer")
                return "plan";
            return "build";
        }

        public static string CycleBuildPlanMode(string currentMode)
        {
            string norm = NormalizeBuildPlanMode(currentMode);
            return norm == "plan" ? "build" : "plan";
        }

        public static string GetModePillLabel(string currentMode)
        {
            string norm = NormalizeBuildPlanMode(currentMode);
            return norm == "plan" ? "Plan" : "Build";
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
