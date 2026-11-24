import re
import sys

with open('src/LlmHarnessAndAgentEngine.cs', 'r') as f:
    content = f.read()

# 1. ParseToolCalls robustification
# We need to catch invalid JSON and return a synthesized 'invalid' tool call with the parsing error.
parse_tool_calls_regex = r"(private List<Dictionary<string, object>> ParseToolCalls\(string text\)\s*\{\s*var list = new List<Dictionary<string, object>>\(\);\s*try\s*\{\s*)(.*?)(return list;\s*\})"

def parse_repl(m):
    return """private List<Dictionary<string, object>> ParseToolCalls(string text)
        {
            var list = new List<Dictionary<string, object>>();
            // Ensure even malformed tool_call blocks without proper JSON are caught
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
                            { "args", new Dictionary<string, object> { { "error", "Invalid JSON inside tool_call: " + ex.Message + "\n" + json } } }
                        });
                    }
                    startIndex = endIndex + "</tool_call>".Length;
                }
            }
            return list;
        }"""

content = re.sub(parse_tool_calls_regex, parse_repl, content, flags=re.DOTALL)

# 2. Levenshtein for unknown tools
exec_tool_regex = r"(public AgentToolCallResult ExecuteWorkspaceTool\(string toolName, Dictionary<string, object> args, string workspaceRoot, List<string> workspaceFiles, EditorTabBuffer activeTab, Dictionary<string, string> stagedFiles\)\s*\{\s*AgentToolCallResult result = new AgentToolCallResult \{ ToolName = toolName, Success = true \};\s*try\s*\{)(.*?)(\}\s*catch \(Exception ex\)\s*\{\s*result\.Success = false;\s*result\.Output = \"Exception: \" \+ ex\.Message;\s*\}\s*return result;\s*\})"

def exec_repl(m):
    original_inner = m.group(2)
    # The unknown tool fallback is inside a switch statement block... 
    # Actually, we can just replace the default block of the switch (toolName).
    return m.group(1) + original_inner + m.group(3)

# Let's do a more targeted replace for the switch default case in ExecuteWorkspaceTool
# Wait, ExecuteWorkspaceTool has a large switch(toolName)
default_regex = r"(default:\s*result\.Success = false;\s*result\.Output = \"Unknown tool: \" \+ toolName;\s*break;)"

def default_repl(m):
    return """default:
                    result.Success = false;
                    
                    // Suggest Levenshtein
                    string[] knownTools = new string[] { "read_file", "search_web", "run_command", "list_dir", "edit_replacers", "grep_search" }; // plus others as needed
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

content = re.sub(default_regex, default_repl, content)

# Also add the ComputeLevenshteinDistance method
if "ComputeLevenshteinDistance(" not in content:
    idx = content.find("public AgentToolCallResult ExecuteWorkspaceTool")
    lev_method = """
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
    content = content[:idx] + lev_method + content[idx:]

# Debug doom loop
doom_regex = r"(bool isDoomLoop = false;\s*if \(doomLoopHistory\.Count >= 3\)\s*\{\s*var t1 = doomLoopHistory\[doomLoopHistory\.Count - 1\];\s*var t2 = doomLoopHistory\[doomLoopHistory\.Count - 2\];\s*var t3 = doomLoopHistory\[doomLoopHistory\.Count - 3\];\s*if \(t1\.Item1 == signature && t2\.Item1 == signature && t3\.Item1 == signature\)\s*\{\s*if \(t1\.Item2 == t2\.Item2 && t2\.Item2 == t3\.Item2\)\s*\{\s*isDoomLoop = true;\s*\}\s*\}\s*\})"

def doom_repl(m):
    return """
                        Console.WriteLine("CHECKING DOOMLOOP. History length: " + doomLoopHistory.Count);
                        for(int _i = 0; _i < doomLoopHistory.Count; _i++) Console.WriteLine(" - " + doomLoopHistory[_i].Item1 + " => " + doomLoopHistory[_i].Item2.Substring(0, Math.Min(20, doomLoopHistory[_i].Item2.Length)));
                        bool isDoomLoop = false;
                        if (doomLoopHistory.Count >= 3)
                        {
                            var t1 = doomLoopHistory[doomLoopHistory.Count - 1];
                            var t2 = doomLoopHistory[doomLoopHistory.Count - 2];
                            var t3 = doomLoopHistory[doomLoopHistory.Count - 3];
                            if (t1.Item1 == signature && t2.Item1 == signature && t3.Item1 == signature)
                            {
                                if (t1.Item2 == t2.Item2 && t2.Item2 == t3.Item2)
                                {
                                    isDoomLoop = true;
                                    Console.WriteLine("DOOMLOOP DETECTED FOR: " + signature);
                                }
                            }
                        }"""
content = re.sub(doom_regex, doom_repl, content)

with open('src/LlmHarnessAndAgentEngine.cs', 'w') as f:
    f.write(content)
