using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Linq;

namespace codeLess.Studio
{
    public static class AgentContextBuilder
    {
        public static string DiscoverInstructions(string startDir, string workspaceRoot = null)
        {
            var instructions = new List<string>();
            var seenPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            int totalLength = 0;
            int maxChars = 16384; // 16 KB

            string[] targetFiles = new[] { "AGENTS.md", "CLAUDE.md", ".github/copilot-instructions.md", ".cursorrules", ".codelessrules", ".codeless/instructions.md" };

            try
            {
                string currentDir = startDir;
                while (!string.IsNullOrEmpty(currentDir))
                {
                    foreach (var tf in targetFiles)
                    {
                        string path = Path.Combine(currentDir, tf.Replace('/', Path.DirectorySeparatorChar));
                        if (File.Exists(path) && !seenPaths.Contains(path))
                        {
                            seenPaths.Add(path);
                            string text = File.ReadAllText(path).Trim();
                            if (!string.IsNullOrEmpty(text))
                            {
                                string labeled = "### " + path + "\n" + text;
                                if (totalLength + labeled.Length <= maxChars)
                                {
                                    instructions.Add(labeled);
                                    totalLength += labeled.Length;
                                }
                            }
                        }
                    }

                    if (Directory.Exists(Path.Combine(currentDir, ".git")) || 
                        (!string.IsNullOrEmpty(workspaceRoot) && string.Equals(currentDir, workspaceRoot, StringComparison.OrdinalIgnoreCase)))
                    {
                        break;
                    }

                    var parent = Directory.GetParent(currentDir);
                    if (parent == null) break;
                    currentDir = parent.FullName;
                }

                string globalPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codeless", "AGENTS.md");
                if (File.Exists(globalPath) && !seenPaths.Contains(globalPath))
                {
                    seenPaths.Add(globalPath);
                    string text = File.ReadAllText(globalPath).Trim();
                    if (!string.IsNullOrEmpty(text))
                    {
                        string labeled = "### " + globalPath + "\n" + text;
                        if (totalLength + labeled.Length <= maxChars)
                        {
                            instructions.Add(labeled);
                            totalLength += labeled.Length;
                        }
                    }
                }
            }
            catch { }

            return string.Join("\n\n", instructions);
        }

        public static string BuildEnvBlock(string workspaceRoot)
        {
            var sb = new StringBuilder();
            sb.AppendLine("<env>");
            sb.AppendLine("PWD: " + (workspaceRoot ?? ""));
            bool isGit = !string.IsNullOrEmpty(workspaceRoot) && Directory.Exists(Path.Combine(workspaceRoot, ".git"));
            sb.AppendLine("Is directory a git repo: " + (isGit ? "yes" : "no"));
            
            string platform = "linux";
            if (Environment.OSVersion.Platform == PlatformID.Win32NT) platform = "win32";
            else if (Environment.OSVersion.Platform == PlatformID.MacOSX || System.Runtime.InteropServices.RuntimeInformation.IsOSPlatform(System.Runtime.InteropServices.OSPlatform.OSX)) platform = "darwin";
            sb.AppendLine("OS: " + platform);
            sb.AppendLine("SHELL: /bin/sh");
            sb.AppendLine("Today's date: " + DateTime.Now.ToString("yyyy-MM-dd"));
            sb.AppendLine("</env>");
            
            sb.AppendLine("<files>");
            if (!string.IsNullOrEmpty(workspaceRoot) && Directory.Exists(workspaceRoot))
            {
                int count = 0;
                foreach (var f in WalkDirectory(workspaceRoot))
                {
                    if (count >= 200) break;
                    sb.AppendLine(f.Substring(workspaceRoot.Length).TrimStart(Path.DirectorySeparatorChar));
                    count++;
                }
            }
            sb.AppendLine("</files>");
            return sb.ToString();
        }

        private static readonly HashSet<string> DefaultIgnoredDirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ".git", "node_modules", "bin", "obj", "dist", "build", "target", "vendor",
            ".venv", "venv", "__pycache__", ".idea", ".vs", "coverage", ".cache", "tmp", "codeless-work"
        };

        private static IEnumerable<string> WalkDirectory(string root)
        {
            var stack = new Stack<string>();
            stack.Push(root);
            while (stack.Count > 0)
            {
                string dir = stack.Pop();
                string[] files = null, dirs = null;
                try {
                    files = Directory.GetFiles(dir);
                    dirs = Directory.GetDirectories(dir);
                } catch { continue; }

                if (files != null) foreach (var f in files) yield return f;
                if (dirs != null)
                {
                    foreach (var d in dirs)
                    {
                        string name = Path.GetFileName(d);
                        if (!string.IsNullOrEmpty(name) && DefaultIgnoredDirs.Contains(name)) continue;
                        stack.Push(d);
                    }
                }
            }
        }

        public static string GetProviderPrompt(string family)
        {
            string basePrompt = "You are codeLess Autonomous Software Engineering Agent.\n" +
                                "Rules:\n" +
                                "- Concise answers.\n" +
                                "- Read before edit.\n" +
                                "- No placeholders.\n" +
                                "- Verify by running tests when possible.\n";
            if (string.Equals(family, "anthropic", StringComparison.OrdinalIgnoreCase))
            {
                return basePrompt + "As an Anthropic model, pay special attention to avoiding preamble and postamble. MIT License - adapted from opencode.";
            }
            else if (string.Equals(family, "openai", StringComparison.OrdinalIgnoreCase))
            {
                return basePrompt + "As an OpenAI model, ensure strict adherence to JSON tool calling formats if applicable. MIT License - adapted from opencode.";
            }
            else if (string.Equals(family, "gemini", StringComparison.OrdinalIgnoreCase))
            {
                return basePrompt + "As a Gemini model, use clear and direct language. MIT License - adapted from opencode.";
            }
            return basePrompt + "MIT License - adapted from opencode.";
        }

        public static void PruneHistory(List<Dictionary<string, string>> messages)
        {
            int protectedChars = 0;
            int prunableCharsSeen = 0;
            
            int protectedCharLimit = 40000 * 4;
            int pruningThreshold = 20000 * 4;
            
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
                            msg["content"] = Regex.Replace(
                                msg["content"], 
                                @"(<tool_result[^>]*>)[\s\S]*?(</tool_result>)", 
                                "$1\n[Old tool result content cleared]\n$2"
                            );
                        }
                    }
                }
            }
        }
    }
}