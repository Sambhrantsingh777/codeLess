import sys
import re

with open('src/codeLess.Core/AgentContextBuilder.cs', 'r') as f:
    content = f.read()

# Add PruneHistory and CompactHistory to AgentContextBuilder
methods = """
        public static void PruneHistory(List<Dictionary<string, string>> messages)
        {
            int protectedChars = 0;
            int prunableCharsSeen = 0;
            
            // Limit to 40,000 tokens -> 160,000 chars protected. 
            // 20,000 tokens -> 80,000 chars threshold for pruning.
            int protectedCharLimit = 40000 * 4;
            int pruningThreshold = 20000 * 4;
            
            // Walk backwards
            for (int i = messages.Count - 1; i >= 0; i--)
            {
                var msg = messages[i];
                if (msg["role"] == "user" && msg["content"].Contains("<tool_result"))
                {
                    if (protectedChars < protectedCharLimit)
                    {
                        protectedChars += msg["content"].Length;
                    }
                    else
                    {
                        prunableCharsSeen += msg["content"].Length;
                    }
                }
            }
            
            if (prunableCharsSeen > pruningThreshold)
            {
                protectedChars = 0;
                for (int i = messages.Count - 1; i >= 0; i--)
                {
                    var msg = messages[i];
                    if (msg["role"] == "user" && msg["content"].Contains("<tool_result"))
                    {
                        if (protectedChars < protectedCharLimit)
                        {
                            protectedChars += msg["content"].Length;
                        }
                        else
                        {
                            // Replace <tool_result> content with [Old tool result content cleared]
                            // We need to preserve the tool_result tags maybe, or just replace the whole body.
                            msg["content"] = System.Text.RegularExpressions.Regex.Replace(
                                msg["content"], 
                                @"(?<=<tool_result[^>]*>)[\\s\\S]*?(?=</tool_result>)", 
                                "\\n[Old tool result content cleared]\\n"
                            );
                        }
                    }
                }
            }
        }
"""
content = content.replace("public static string PruneHistory(string history, out bool pruned)\n        {\n            // Dummy implementation to be expanded in Engine where history is structural\n            pruned = false;\n            return history;\n        }", methods)

with open('src/codeLess.Core/AgentContextBuilder.cs', 'w') as f:
    f.write(content)
