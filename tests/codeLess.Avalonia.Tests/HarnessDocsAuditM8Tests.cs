using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using codeLess.Studio;
using Xunit;

namespace codeLess.Avalonia.Tests
{
    public class HarnessDocsAuditM8Tests
    {
        [Fact]
        public void M8_OpencodeDoc_And_SpecAudit_AreCompleteAndValid()
        {
            string repoRoot = Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "..", "..", "..", ".."));
            string opencodeDocPath = Path.Combine(repoRoot, "docs", "HARNESS_OPENCODE.md");
            string specAuditPath = Path.Combine(repoRoot, "docs", "HARNESS_SPEC_AUDIT.md");

            Assert.True(File.Exists(opencodeDocPath), "Expected docs/HARNESS_OPENCODE.md to exist");
            Assert.True(File.Exists(specAuditPath), "Expected docs/HARNESS_SPEC_AUDIT.md to exist");

            string doc = File.ReadAllText(opencodeDocPath);
            Assert.Contains("sst/opencode", doc);
            Assert.Contains("MIT License", doc);
            Assert.Contains("EditReplacers.cs", doc);
            Assert.Contains("NativeToolCalling.cs", doc);
            Assert.Contains("WebFetchTool.cs", doc);
            Assert.Contains("codeless.agent.nativeToolCalling", doc);
            Assert.Contains("RequireAgentToolApproval", doc);
            Assert.Contains("agent.permission.webfetch", doc);
            Assert.Contains("webfetch", doc);
            Assert.Contains("task", doc);

            string audit = File.ReadAllText(specAuditPath);
            Assert.Contains("M7c-1:", audit);
            Assert.Contains("M7c-2:", audit);
            Assert.Contains("M7d:", audit);
            Assert.Contains("M7e:", audit);
            Assert.Contains("M7f:", audit);
            Assert.Contains("M8:", audit);
            Assert.Contains("M9a:", audit);
            Assert.Contains("M9b:", audit);
            Assert.Contains("M9c:", audit);
            Assert.DoesNotContain("| TODO |", audit);
            Assert.DoesNotContain("| Partial |", audit);
            Assert.DoesNotContain("| Missing |", audit);
        }

        [Fact]
        public void HarnessDocToolTableMatchesCode()
        {
            string repoRoot = Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "..", "..", "..", ".."));
            string opencodeDocPath = Path.Combine(repoRoot, "docs", "HARNESS_OPENCODE.md");
            string[] lines = File.ReadAllLines(opencodeDocPath);

            int headerIdx = Array.FindIndex(lines, l => l.Trim().StartsWith("| Tool Name | Aliases | Read-Only |", StringComparison.Ordinal));
            Assert.True(headerIdx >= 0, "Expected §4 Tool Reference Table header in docs/HARNESS_OPENCODE.md");

            var docRows = new List<(string Name, string[] Aliases, bool IsReadOnly)>();
            for (int i = headerIdx + 2; i < lines.Length; i++)
            {
                string line = lines[i].Trim();
                if (!line.StartsWith("|")) break;
                string[] cells = line.Split('|').Select(c => c.Trim()).ToArray();
                if (cells.Length < 4) continue;
                string toolName = cells[1].Trim('`', ' ');
                string aliasCell = cells[2].Trim();
                string[] aliases = (aliasCell == "—" || aliasCell == "-" || string.IsNullOrEmpty(aliasCell))
                    ? new string[0]
                    : aliasCell.Split(',').Select(a => a.Trim().Trim('`')).Where(a => a.Length > 0).ToArray();
                bool isReadOnly = string.Equals(cells[3], "Yes", StringComparison.OrdinalIgnoreCase);
                docRows.Add((toolName, aliases, isReadOnly));
            }

            Assert.Equal(LlmHarnessAndAgentEngine.Tools.Length, docRows.Count);
            for (int i = 0; i < LlmHarnessAndAgentEngine.Tools.Length; i++)
            {
                AgentToolDefinition codeTool = LlmHarnessAndAgentEngine.Tools[i];
                var docRow = docRows[i];
                Assert.Equal(codeTool.Name, docRow.Name);
                Assert.Equal(codeTool.Aliases ?? new string[0], docRow.Aliases);
                Assert.Equal(codeTool.IsReadOnly, docRow.IsReadOnly);
            }
        }
    }
}
