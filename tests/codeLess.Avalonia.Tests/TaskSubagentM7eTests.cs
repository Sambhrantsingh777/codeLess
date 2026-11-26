using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Xunit;
using codeLess.Studio;

namespace codeLess.Avalonia.Tests
{
    public class TaskSubagentM7eTests : IDisposable
    {
        private readonly string _testDir;

        public TaskSubagentM7eTests()
        {
            _testDir = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "m7e_task_" + Guid.NewGuid().ToString("N")));
            Directory.CreateDirectory(_testDir);
            TestTrust.Trust(_testDir);
        }

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(_testDir))
                    Directory.Delete(_testDir, true);
            }
            catch { }
        }

        [Fact]
        public async Task Task_ChildDeniedWriteFile_AndNothingIsStaged()
        {
            var settings = new IdePersistentSettings { RequireAgentToolApproval = false, NativeToolCalling = false };
            var harness = new LlmHarnessAndAgentEngine(settings);
            harness.OnRequestToolApproval = (tool, args) => true;

            string forbiddenFile = Path.Combine(_testDir, "forbidden.txt").Replace("\\", "/");
            int callIndex = 0;
            bool childSawWriteDenial = false;

            harness.CustomModelResponder = (sys, msgs) =>
            {
                callIndex++;
                string lastContent = msgs[msgs.Count - 1]["content"];
                if (callIndex == 1)
                {
                    // Parent invokes task
                    return Task.FromResult("<tool_call>{\"tool\":\"task\",\"args\":{\"description\":\"attempt write\",\"prompt\":\"try writing a file\"}}</tool_call>");
                }
                if (callIndex == 2)
                {
                    // Child step 1: tries write_file
                    return Task.FromResult("<tool_call>{\"tool\":\"write_file\",\"args\":{\"path\":\"" + forbiddenFile + "\",\"content\":\"hacked\"}}</tool_call>");
                }
                if (callIndex == 3)
                {
                    // Child step 2: receives denial, also emits a SEARCH/REPLACE block to verify child never stages blocks either
                    if (lastContent.Contains("not allowed inside a read-only task subagent") || lastContent.Contains("Permission Denied"))
                        childSawWriteDenial = true;
                    return Task.FromResult("Child finished after write denial.\n### File: " + forbiddenFile + "\n<<<<<<< SEARCH\n=======\nhacked\n>>>>>>> REPLACE");
                }
                // Parent step 2: receives child output
                return Task.FromResult("Parent done: " + lastContent);
            };

            var res = await harness.RunAutonomousAgentLoopAsync("build", "run subagent", null, "auto", null, _testDir, new List<string>(), new List<string>());

            Assert.True(childSawWriteDenial, "Child should have received write_file denial");
            Assert.Empty(res.StagedFileContents);
            Assert.False(File.Exists(forbiddenFile));
            Assert.Contains("Child finished after write denial", res.AssistantReply);
        }

        [Fact]
        public async Task Task_NestedTask_IsRefused()
        {
            var settings = new IdePersistentSettings { RequireAgentToolApproval = false, NativeToolCalling = false };
            var harness = new LlmHarnessAndAgentEngine(settings);
            harness.OnRequestToolApproval = (tool, args) => true;

            int callIndex = 0;
            bool childSawNestedRefusal = false;

            harness.CustomModelResponder = (sys, msgs) =>
            {
                callIndex++;
                string lastContent = msgs[msgs.Count - 1]["content"];
                if (callIndex == 1)
                {
                    // Parent invokes task (depth 0 -> 1)
                    return Task.FromResult("<tool_call>{\"tool\":\"task\",\"args\":{\"description\":\"outer\",\"prompt\":\"spawn inner\"}}</tool_call>");
                }
                if (callIndex == 2)
                {
                    // Child (depth 1) attempts nested task (depth 1 -> 2, must be refused)
                    return Task.FromResult("<tool_call>{\"tool\":\"task\",\"args\":{\"description\":\"inner\",\"prompt\":\"nested\"}}</tool_call>");
                }
                if (callIndex == 3)
                {
                    if (lastContent.Contains("Nested task subagents are not allowed"))
                        childSawNestedRefusal = true;
                    return Task.FromResult("Nested task was refused as expected.");
                }
                return Task.FromResult("Parent received: " + lastContent);
            };

            var res = await harness.RunAutonomousAgentLoopAsync("build", "test nested", null, "auto", null, _testDir, new List<string>(), new List<string>());

            Assert.True(childSawNestedRefusal, "Nested task call inside child must be refused");
            Assert.Contains("Nested task was refused as expected", res.AssistantReply);
        }

        [Fact]
        public async Task Task_StopDuringChild_CancelsPromptly()
        {
            var settings = new IdePersistentSettings { RequireAgentToolApproval = false, NativeToolCalling = false };
            var harness = new LlmHarnessAndAgentEngine(settings);
            harness.OnRequestToolApproval = (tool, args) => true;

            var childStarted = new TaskCompletionSource<bool>();
            int callIndex = 0;

            harness.CustomModelResponder = async (sys, msgs) =>
            {
                callIndex++;
                if (callIndex == 1)
                {
                    return "<tool_call>{\"tool\":\"task\",\"args\":{\"description\":\"long research\",\"prompt\":\"wait\"}}</tool_call>";
                }
                // Inside child subagent: signal started and simulate a long-running model call
                childStarted.TrySetResult(true);
                await Task.Delay(10000);
                return "Should have been cancelled";
            };

            var parentTurnTask = harness.RunAutonomousAgentLoopAsync("build", "start", null, "auto", null, _testDir, new List<string>(), new List<string>());

            await childStarted.Task;
            // Measure cancellation latency only (from Stop to the parent turn returning), not agent-loop startup/JIT time.
            var sw = Stopwatch.StartNew();
            harness.CancelStreamingRequested = true;

            var res = await parentTurnTask;
            sw.Stop();

            Assert.True(sw.ElapsedMilliseconds < 2000, "Cancellation of parent should cancel child promptly, took " + sw.ElapsedMilliseconds + "ms");
            Assert.Empty(res.StagedFileContents);
        }

        [Fact]
        public async Task Task_OutputComesBackToParent_AndReadOnlyToolsWork()
        {
            string infoFile = Path.Combine(_testDir, "notes.txt");
            File.WriteAllText(infoFile, "magic_value=98765");
            string escapedPath = infoFile.Replace("\\", "/");

            var settings = new IdePersistentSettings { RequireAgentToolApproval = false, NativeToolCalling = false };
            var harness = new LlmHarnessAndAgentEngine(settings);
            harness.OnRequestToolApproval = (tool, args) => true;

            int callIndex = 0;
            harness.CustomModelResponder = (sys, msgs) =>
            {
                callIndex++;
                string lastContent = msgs[msgs.Count - 1]["content"];
                if (callIndex == 1)
                {
                    return Task.FromResult("<tool_call>{\"tool\":\"task\",\"args\":{\"description\":\"read notes\",\"prompt\":\"find magic_value in notes.txt\"}}</tool_call>");
                }
                if (callIndex == 2)
                {
                    // Child uses read_file
                    return Task.FromResult("<tool_call>{\"tool\":\"read_file\",\"args\":{\"path\":\"" + escapedPath + "\"}}</tool_call>");
                }
                if (callIndex == 3)
                {
                    // Child sees file content and returns final text
                    Assert.Contains("magic_value=98765", lastContent);
                    return Task.FromResult("Subagent found magic_value=98765");
                }
                // Parent receives child's final text in tool_result
                Assert.Contains("Subagent found magic_value=98765", lastContent);
                return Task.FromResult("Final parent answer: magic_value=98765");
            };

            var res = await harness.RunAutonomousAgentLoopAsync("build", "find magic value", null, "auto", null, _testDir, new List<string> { infoFile }, new List<string>());

            Assert.Equal("Final parent answer: magic_value=98765", res.AssistantReply);
            Assert.Single(res.ToolCalls);
            Assert.Equal("task", res.ToolCalls[0].ToolName);
            Assert.Equal("Subagent found magic_value=98765", res.ToolCalls[0].Output);
            Assert.Empty(res.StagedFileContents);
        }
    }
}
