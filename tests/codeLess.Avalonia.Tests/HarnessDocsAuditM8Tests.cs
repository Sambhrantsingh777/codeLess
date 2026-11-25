using System;
using System.IO;
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
            Assert.DoesNotContain("| TODO |", audit);
            Assert.DoesNotContain("| Partial |", audit);
            Assert.DoesNotContain("| Missing |", audit);
        }
    }
}
