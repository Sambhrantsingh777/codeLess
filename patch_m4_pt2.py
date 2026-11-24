import sys

with open('src/LlmHarnessAndAgentEngine.cs', 'r') as f:
    content = f.read()

# 1. ParseToolCalls
start_sig = "private List<Dictionary<string, object>> ParseToolCalls(string text)"
end_sig = "return list;\n        }"
idx1 = content.find(start_sig)
if idx1 != -1:
    idx2 = content.find(end_sig, idx1)
    if idx2 != -1:
        new_method = """private List<Dictionary<string, object>> ParseToolCalls(string text)
        {
            var list = new List<Dictionary<string, object>>();
            int startIndex = 0;
            while ((startIndex = text.IndexOf("<tool_call>", startIndex, StringComparison.OrdinalIgnoreCase)) != -1)
            {
                startIndex += "<tool_call>".Length;
                int endIndex = text.IndexOf("</tool_call>", startIndex, StringComparison.OrdinalIgnoreCase);
                if (endIndex != -1)
                {
                    string json = text.Substring(startIndex, endIndex - startIndex).Trim();
                    try
                    {
                        var js = new System.Web.Script.Serialization.JavaScriptSerializer();
                        var tc = js.Deserialize<Dictionary<string, object>>(json);
                        if (tc == null || !tc.ContainsKey("tool"))
                        {
                            list.Add(new Dictionary<string, object>
                            {
                                { "tool", "invalid" },
                                { "args", new Dictionary<string, object> { { "error", "JSON parsed but missing 'tool' field: " + json } } }
                            });
                        }
                        else
                        {
                            list.Add(tc);
                        }
                    }
                    catch (Exception ex)
                    {
                        list.Add(new Dictionary<string, object>
                        {
                            { "tool", "invalid" },
                            { "args", new Dictionary<string, object> { { "error", "Invalid JSON inside tool_call: " + ex.Message + "\\n" + json } } }
                        });
                    }
                    startIndex = endIndex + "</tool_call>".Length;
                }
            }
            return list;
        }"""
        content = content[:idx1] + new_method + content[idx2 + len(end_sig):]

# 2. Levenshtein for unknown tools
default_case = """default:
                    result.Success = false;
                    result.Output = "Unknown tool: " + toolName;
                    break;"""

if default_case in content:
    new_default = """default:
                    result.Success = false;
                    string[] knownTools = new string[] { "read_file", "search_web", "run_command", "list_dir", "edit_replacers", "grep_search" };
                    string closest = null;
                    int minDistance = int.MaxValue;
                    foreach (string k in knownTools)
                    {
                        int dist = ComputeLevenshteinDistance(toolName, k);
                        if (dist < minDistance)
                        {
                            minDistance = dist;
                            closest = k;
                        }
                    }
                    if (minDistance <= 3 && closest != null)
                    {
                        result.Output = "Unknown tool: " + toolName + ". Did you mean '" + closest + "'?";
                    }
                    else
                    {
                        result.Output = "Unknown tool: " + toolName;
                    }
                    result.ToolName = "invalid";
                    break;"""
    content = content.replace(default_case, new_default)

if "ComputeLevenshteinDistance" not in content:
    idx3 = content.find("public AgentToolCallResult ExecuteWorkspaceTool")
    if idx3 != -1:
        lev = """
        private int ComputeLevenshteinDistance(string s, string t)
        {
            if (string.IsNullOrEmpty(s)) return string.IsNullOrEmpty(t) ? 0 : t.Length;
            if (string.IsNullOrEmpty(t)) return s.Length;
            int[] v0 = new int[t.Length + 1];
            int[] v1 = new int[t.Length + 1];
            for (int i = 0; i < v0.Length; i++) v0[i] = i;
            for (int i = 0; i < s.Length; i++)
            {
                v1[0] = i + 1;
                for (int j = 0; j < t.Length; j++)
                {
                    int cost = (s[i] == t[j]) ? 0 : 1;
                    v1[j + 1] = Math.Min(v1[j] + 1, Math.Min(v0[j + 1] + 1, v0[j] + cost));
                }
                for (int j = 0; j < v0.Length; j++) v0[j] = v1[j];
            }
            return v1[t.Length];
        }
        """
        content = content[:idx3] + lev + content[idx3:]

with open('src/LlmHarnessAndAgentEngine.cs', 'w') as f:
    f.write(content)
