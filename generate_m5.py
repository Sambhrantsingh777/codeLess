import os

agent_context_cs = """using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace codeLess.Core
{
    public static class AgentContextBuilder
    {
        public static string DiscoverInstructions(string workspaceRoot)
        {
            var instructions = new List<string>();
            var seen = new HashSet<string>();
            int totalLength = 0;
            
            try
            {
                string currentDir = workspaceRoot;
                while (!string.IsNullOrEmpty(currentDir))
                {
                    string path = Path.Combine(currentDir, ".codeless", "instructions.md");
                    if (File.Exists(path))
                    {
                        string text = File.ReadAllText(path).Trim();
                        if (!string.IsNullOrEmpty(text) && !seen.Contains(text))
                        {
                            seen.Add(text);
                            if (totalLength + text.Length <= 10000)
                            {
                                instructions.Add(text);
                                totalLength += text.Length;
                            }
                        }
                    }
                    
                    if (Directory.Exists(Path.Combine(currentDir, ".git")))
                        break;
                        
                    var parent = Directory.GetParent(currentDir);
                    if (parent == null) break;
                    currentDir = parent.FullName;
                }

                // User home
                string homePath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codeless", "instructions.md");
                if (File.Exists(homePath))
                {
                    string text = File.ReadAllText(homePath).Trim();
                    if (!string.IsNullOrEmpty(text) && !seen.Contains(text))
                    {
                        seen.Add(text);
                        if (totalLength + text.Length <= 10000)
                        {
                            instructions.Add(text);
                            totalLength += text.Length;
                        }
                    }
                }
            }
            catch { }
            
            return string.Join("\\n\\n", instructions);
        }

        public static string BuildEnvBlock(string workspaceRoot)
        {
            var sb = new StringBuilder();
            sb.AppendLine("<env>");
            sb.AppendLine("OS: " + Environment.OSVersion.ToString());
            sb.AppendLine("PWD: " + workspaceRoot);
            sb.AppendLine("SHELL: " + (Environment.GetEnvironmentVariable("SHELL") ?? "/bin/sh"));
            sb.AppendLine("HOME: " + Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
            sb.AppendLine("USER: " + Environment.UserName);
            sb.AppendLine("</env>");
            return sb.ToString();
        }

        public static string GetProviderPrompt(string family)
        {
            string basePrompt = "You are an interactive CLI tool that helps users with software engineering tasks. Use the instructions below and the tools available to you to assist the user.\\n" +
                                "Output text to communicate with the user; all text you output outside of tool use is displayed to the user.\\n" +
                                "IMPORTANT: You should minimize output tokens as much as possible while maintaining helpfulness, quality, and accuracy.\\n" +
                                "Keep your responses short, since they will be displayed on a command line interface.\\n" +
                                "MIT License - adapted from opencode.\\n";
            if (family == "anthropic")
            {
                return basePrompt + "As an Anthropic model, pay special attention to avoiding preamble and postamble.";
            }
            else if (family == "openai")
            {
                return basePrompt + "As an OpenAI model, ensure strict adherence to JSON tool calling formats if applicable.";
            }
            else if (family == "gemini")
            {
                return basePrompt + "As a Gemini model, use clear and direct language.";
            }
            return basePrompt;
        }
        
        public static string PruneHistory(string history, out bool pruned)
        {
            // Dummy implementation to be expanded in Engine where history is structural
            pruned = false;
            return history;
        }
    }
}
"""

with open('src/codeLess.Core/AgentContextBuilder.cs', 'w') as f:
    f.write(agent_context_cs)
