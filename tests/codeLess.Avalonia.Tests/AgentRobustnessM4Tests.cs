using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using codeLess.Studio;
using Xunit;

namespace codeLess.Avalonia.Tests
{
    public class AgentRobustnessM4Tests
    {
        [Fact]
        public void ParseToolCalls_InvalidJson_SynthesizesError()
        {
            var harness = new LlmHarnessAndAgentEngine(null);
            var method = harness.GetType().GetMethod("ParseToolCalls", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            
            // Missing quotes around value
            string llmOutput = "<tool_call>this is not json</tool_call>";
            var calls = (List<Dictionary<string, object>>)method.Invoke(harness, new object[] { llmOutput });
            
            Assert.Single(calls);
            Assert.Equal("invalid", calls[0]["tool"]);
            var args = (Dictionary<string, object>)calls[0]["args"];
            Assert.Contains("JSON parsed but missing 'tool' field", args["error"].ToString());
        }

        [Fact]
        public void ParseToolCalls_UnknownTool_SuggestsLevenshtein()
        {
            var harness = new LlmHarnessAndAgentEngine(null);
            
            // "list_dirr" instead of "list_dir"
            var args = new Dictionary<string, object> { { "path", "/tmp" } };
            var result = harness.ExecuteWorkspaceTool("list_dirr", args, "/tmp", new List<string>(), null, null, "agent", null);
            
            Assert.False(result.Success);
            Assert.Equal("invalid", result.ToolName);
            Assert.Contains("list_dir", result.Output);
        }

        [Fact]
        public async Task DoomLoop_InterceptsFourthAttempt()
        {
            var harness = new LlmHarnessAndAgentEngine(null);
            harness.MaxAgentIterations = 10;
            
            int attempts = 0;
            bool approvalRequested = false;

            harness.CustomModelResponder = (sys, msgs) =>
            {
                attempts++;
                System.IO.File.AppendAllText("/tmp/doomloop_debug.txt", "RESPONDER CALLED, attempt=" + attempts + "\n");
                if (attempts <= 4)
                {
                    return Task.FromResult("<tool_call>{\"tool\": \"read_file\", \"args\": {\"path\": \"/nonexistent\"}}</tool_call>");
                }
                return Task.FromResult("Done"); // 5th attempt exits
            };

            Console.WriteLine("ATTEMPTS SO FAR: " + attempts);
            harness.OnRequestToolApproval = (toolName, toolArgs) =>
            {
                if (toolName == "doom_loop")
                {
                    approvalRequested = true;
                    return false; // Reject it
                }
                return true; // Auto-approve others
            };

            var buf = new EditorTabBuffer { FilePath = "/tmp/test.cs" };
            
            var method = harness.GetType().GetMethod("RunAutonomousAgentLoopCoreAsync", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            var result = await (Task<AgentExecutionTurnResult>)method.Invoke(harness, new object[] { 
                "test", "instruction", null, null, buf, "/tmp", new List<string>(), new List<string>(), "", new Dictionary<string, string>() 
            });
            
            Console.WriteLine("ASSISTANT REPLY: " + result.AssistantReply);
            Assert.True(attempts > 0, "Agent loop didn't run at all!");
            Assert.True(approvalRequested, "Doom loop should have requested approval (ran " + attempts + " times)");
        }
        
        [Fact]
        public async Task ApiRetryHandler_RetriesOn502()
        {
            int attempts = 0;
            var result = await ApiRetryHandler.ExecuteWithRetriesAsync(() =>
            {
                attempts++;
                if (attempts < 3)
                {
                    throw new Exception("Provider overloaded");
                }
                return Task.FromResult("Success");
            }, null);

            Assert.Equal(3, attempts);
            Assert.Equal("Success", result);
        }
    }
}
