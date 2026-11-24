import sys

with open('src/LlmHarnessAndAgentEngine.cs', 'r') as f:
    content = f.read()

target = 'return new AgentToolCallResult { ToolName = toolName, Success = false, Summary = "Unknown tool", Output = "Unknown tool" };'

replacement = """
            string[] knownTools = new string[] { "read_file", "search_web", "run_command", "list_dir", "edit_replacers", "grep_search", "webfetch", "task" };
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
            string msg = "Unknown tool: " + toolName;
            if (minDistance <= 3 && closest != null)
            {
                msg += ". Did you mean '" + closest + "'?";
            }
            return new AgentToolCallResult { ToolName = "invalid", Success = false, Summary = "Unknown tool", Output = msg };
"""
content = content.replace(target, replacement)

with open('src/LlmHarnessAndAgentEngine.cs', 'w') as f:
    f.write(content)
