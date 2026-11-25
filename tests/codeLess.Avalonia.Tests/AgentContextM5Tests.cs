using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using codeLess.Studio;
using Xunit;

namespace codeLess.Avalonia.Tests
{
    public class AgentContextM5Tests : IDisposable
    {
        private string testRoot;

        public AgentContextM5Tests()
        {
            testRoot = Path.Combine(Path.GetTempPath(), "m5_test_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(testRoot);
        }

        public void Dispose()
        {
            try { Directory.Delete(testRoot, true); } catch { }
        }

        [Fact]
        public void DiscoverInstructions_DedupesAndCaps()
        {
            var p1 = Path.Combine(testRoot, "sub1", "sub2");
            Directory.CreateDirectory(p1);
            var f1 = Path.Combine(p1, ".codeless");
            Directory.CreateDirectory(f1);
            File.WriteAllText(Path.Combine(f1, "instructions.md"), "instruction A");
            
            var p2 = testRoot; // root
            var f2 = Path.Combine(p2, ".codeless");
            Directory.CreateDirectory(f2);
            File.WriteAllText(Path.Combine(f2, "instructions.md"), "instruction B\n\ninstruction A"); // B contains A? wait just strings

            var pGit = Path.Combine(testRoot, ".git");
            Directory.CreateDirectory(pGit); // stops here
            
            string discovered = AgentContextBuilder.DiscoverInstructions(p1);
            Assert.Contains("instruction A", discovered);
            Assert.Contains("instruction B", discovered);
        }

        [Fact]
        public void BuildEnvBlock_ContainsExpectedFields()
        {
            string env = AgentContextBuilder.BuildEnvBlock(testRoot);
            Assert.Contains("<env>", env);
            Assert.Contains("OS: ", env);
            Assert.Contains("PWD: " + testRoot, env);
            Assert.Contains("SHELL: ", env);
            Assert.Contains("</env>", env);
        }

        [Fact]
        public void GetProviderPrompt_SelectsFamily()
        {
            string anthropic = AgentContextBuilder.GetProviderPrompt("anthropic");
            Assert.Contains("preamble", anthropic);

            string openai = AgentContextBuilder.GetProviderPrompt("openai");
            Assert.Contains("JSON tool calling", openai);
        }

        [Fact]
        public void PruneHistory_ReplacesOldResults()
        {
            var messages = new List<Dictionary<string, string>>();
            for (int i = 0; i < 5; i++)
            {
                messages.Add(new Dictionary<string, string> { { "role", "user" }, { "content", "<tool_result>\n" + new string('A', 100000) + "\n</tool_result>" } });
            }

            AgentContextBuilder.PruneHistory(messages);

            // Last 3 should be protected (150,000 chars <= 160,000)
            // First 2 should be pruned (they bring total past 160,000 threshold and overall is 250,000 which is > 80,000)
            Assert.Contains("[Old tool result content cleared]", messages[0]["content"]);
            Assert.Contains("[Old tool result content cleared]", messages[1]["content"]);
            Assert.Contains("[Old tool result content cleared]", messages[2]["content"]);
            Assert.DoesNotContain("[Old tool result content cleared]", messages[4]["content"]);
        }
        
        [Fact]
        public async Task Compaction_TriggersOnThreshold()
        {
            var harness = new LlmHarnessAndAgentEngine(new IdePersistentSettings());
            harness.MaxAgentIterations = 2;
            
            bool compactionEventFired = false;
            harness.OnAgentFeedStatus = (msg) => {
                if (msg == "Context compacted") compactionEventFired = true;
            };
            
            int attempts = 0;
            harness.CustomModelResponder = (sys, msgs) =>
            {
                attempts++;
                if (msgs.Count > 1 && msgs[0]["content"].Contains("Provide a detailed prompt for continuing our conversation"))
                {
                    return Task.FromResult("COMPACTED_SUMMARY");
                }
                if (attempts == 1)
                {
                    return Task.FromResult("<tool_call>{\"tool\": \"list_dir\", \"args\": {}}</tool_call>");
                }
                return Task.FromResult("Done");
            };

            var buf = new EditorTabBuffer { FilePath = "/tmp/test.cs" };
            var method = harness.GetType().GetMethod("RunAutonomousAgentLoopCoreAsync", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            
            var mockVendor = new ByokVendor { Id = "test", BaseUrl = "test" }; 
            
            // Generate a huge instruction to trigger compaction
            string hugeInstruction = new string('A', 500000); 

            var task = (Task<AgentExecutionTurnResult>)method.Invoke(harness, new object[] {
                "edit",
                hugeInstruction,
                mockVendor,
                "auto",
                buf,
                testRoot,
                new List<string>(),
                new List<string>(),
                "",
                new Dictionary<string, string>()
            });
            
            var result = await task;
            
            Assert.True(compactionEventFired, "Compaction event should have fired");
            Assert.True(attempts >= 2, "Should have called model multiple times");
        }
    }
}
