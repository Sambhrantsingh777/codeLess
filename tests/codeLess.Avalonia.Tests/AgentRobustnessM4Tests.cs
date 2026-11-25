using Avalonia.Headless.XUnit;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using codeLess.Studio;
using Xunit;
using System.Net;
using System.Reflection;

namespace codeLess.Avalonia.Tests
{
    public class AgentRobustnessM4Tests
    {
        [Fact]
        public void ParseToolCalls_InvalidJson_SynthesizesError()
        {
            var harness = new LlmHarnessAndAgentEngine(null);
            var method = harness.GetType().GetMethod("ParseToolCalls", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            string llmOutput = "<tool_call>this is not json</tool_call>";
            var calls = (List<Dictionary<string, object>>)method.Invoke(harness, new object[] { llmOutput });
            Assert.Single(calls);
            Assert.Equal("invalid", calls[0]["tool"]);
            var args = (Dictionary<string, object>)calls[0]["args"];
            Assert.Contains("JSON parsed but missing 'tool' field", args["error"].ToString());
        }

        [Fact]
        public void ToolName_Repair_CaseInsensitive_And_Unknown()
        {
            var settings = new IdePersistentSettings { RequireAgentToolApproval = false };
            var harness = new LlmHarnessAndAgentEngine(settings);
            
            // Case-insensitive repair
            var res1 = harness.ExecuteWorkspaceTool("Read_File", new Dictionary<string, object> { { "path", "/tmp/nonexistent" } }, "/tmp", new List<string>(), null, new Dictionary<string, string>(), "agent", settings);
            Assert.Equal("read_file", res1.ToolName);
            
            // Unknown tool
            var res2 = harness.ExecuteWorkspaceTool("completely_made_up_tool", new Dictionary<string, object>(), "/tmp", new List<string>(), null, new Dictionary<string, string>(), "agent", settings);
            Assert.Equal("invalid", res2.ToolName);
            Assert.False(res2.Success);
            Assert.StartsWith("Invalid tool \"completely_made_up_tool\". Available tools:", res2.Output);
            Assert.Contains("read_file", res2.Output);
        }

        [Fact]
        public async Task DoomLoop_TestsBothGrantedAndDenied()
        {
            var harness = new LlmHarnessAndAgentEngine(null);
            harness.MaxAgentIterations = 10;
            
            int listDirCallsFromModel = 0;
            int listDirExecutionsSeen = 0;
            bool approvalRequested = false;
            bool allowDoomLoop = false;
            bool sawDenialMessage = false;
            
            harness.CustomModelResponder = (sys, msgs) =>
            {
                var lastMsg = msgs[msgs.Count - 1];
                if (lastMsg["role"] == "user") {
                    
                    
                    if (lastMsg["content"].Contains("Doom loop") || lastMsg["content"].Contains("Doom")) sawDenialMessage = true;


                    if (lastMsg["content"].Contains("Found ") || lastMsg["content"].Contains("total ")) listDirExecutionsSeen++;
                }

                listDirCallsFromModel++;
                if (listDirCallsFromModel <= 4) return Task.FromResult("<tool_call>{\"tool\": \"list_dir\", \"args\": {\"path\": \"/tmp\"}}</tool_call>");
                return Task.FromResult("Done");
            };

            harness.OnRequestToolApproval = (toolName, toolArgs) =>
            {
                if (toolName == "doom_loop")
                {
                    approvalRequested = true;
                    return allowDoomLoop;
                }
                return true; 
            };

            var buf = new EditorTabBuffer { FilePath = "/tmp/test.cs" };
            var method = harness.GetType().GetMethod("RunAutonomousAgentLoopCoreAsync", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            
            // Test Denied
            var resultDenied = await (Task<AgentExecutionTurnResult>)method.Invoke(harness, new object[] { "test", "instruction", null, null, buf, "/tmp", new List<string>(), new List<string>(), "", new Dictionary<string, string>() });
            Assert.True(approvalRequested);
            Assert.True(sawDenialMessage, "Model should receive a denial message");
            Assert.Equal(3, listDirExecutionsSeen); // 1, 2, 3 executed, 4 denied
            
            // Re-test Granted
            listDirCallsFromModel = 0;
            listDirExecutionsSeen = 0;
            approvalRequested = false;
            allowDoomLoop = true;
            sawDenialMessage = false;
            
            var resultGranted = await (Task<AgentExecutionTurnResult>)method.Invoke(harness, new object[] { "test", "instruction", null, null, buf, "/tmp", new List<string>(), new List<string>(), "", new Dictionary<string, string>() });
            Assert.True(approvalRequested);
            Assert.False(sawDenialMessage, "Should not see denial message when granted");
            Assert.Equal(4, listDirExecutionsSeen); // 1, 2, 3, 4 executed
        }
    }

    [CollectionDefinition("ApiRetryStatic", DisableParallelization = true)]
    public class ApiRetryStaticCollection { }

    [Collection("ApiRetryStatic")]
    public class ApiRetryHandlerTests
    {
        [Fact]
        public async Task ApiRetry_BasicRetries()
        {
            int waitedTotal = 0;
            var oldDelay = ApiRetryHandler.DelayTaskAsync;
            try {
                ApiRetryHandler.DelayTaskAsync = (ms, ct) => { waitedTotal += ms; return Task.Delay(1, ct); };
                
                int attempts = 0;
                var result = await ApiRetryHandler.ExecuteWithRetriesAsync(() =>
                {
                    attempts++;
                    if (attempts < 3)
                    {
                        var ex = new WebException("502", null, WebExceptionStatus.ProtocolError, new FakeWebResponse(502));
                        throw ex;
                    }
                    return Task.FromResult("Success");
                }, null, CancellationToken.None);

                Assert.Equal(3, attempts);
                Assert.Equal("Success", result);
                Assert.True(waitedTotal > 0);
            } finally {
                ApiRetryHandler.DelayTaskAsync = (ms, ct) => Task.Delay(ms, ct);
            }
        }

        [Fact]
        public async Task ApiRetry_Cancellation_BeforeWait()
        {
            var oldDelay = ApiRetryHandler.DelayTaskAsync;
            try {
                int waitedTotal = 0;
                ApiRetryHandler.DelayTaskAsync = (ms, ct) => { waitedTotal += ms; return Task.Delay(1, ct); };
                var cts = new CancellationTokenSource();
                cts.Cancel();
                await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await ApiRetryHandler.ExecuteWithRetriesAsync(async () => {
                    var ex = new WebException("502", null, WebExceptionStatus.ProtocolError, new FakeWebResponse(502));
                    throw ex;
                }, null, cts.Token));
            } finally {
                ApiRetryHandler.DelayTaskAsync = (ms, ct) => Task.Delay(ms, ct);
            }
        }
        
        [Fact]
        public async Task ApiRetry_Cancellation_DuringWait()
        {
            var oldDelay = ApiRetryHandler.DelayTaskAsync;
            try {
                var cts = new CancellationTokenSource();
                ApiRetryHandler.DelayTaskAsync = async (ms, ct) => {
                    cts.Cancel(); // cancel DURING the wait
                    await Task.Delay(ms, ct); // should throw OperationCanceledException
                };
                int attempts = 0;
                await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await ApiRetryHandler.ExecuteWithRetriesAsync(() => {
                    attempts++;
                    var ex = new WebException("502", null, WebExceptionStatus.ProtocolError, new FakeWebResponse(502));
                    throw ex;
                }, null, cts.Token));
                Assert.Equal(1, attempts); // Only tried once, then cancelled in delay
            } finally {
                ApiRetryHandler.DelayTaskAsync = (ms, ct) => Task.Delay(ms, ct);
            }
        }

        [Fact]
        public async Task ApiRetry_Headers_Ms()
        {
            var oldDelay = ApiRetryHandler.DelayTaskAsync;
            try {
                int waitedTotal = 0;
                ApiRetryHandler.DelayTaskAsync = (ms, ct) => { waitedTotal += ms; return Task.Delay(1, ct); };
                int attempts = 0;
                var resHeaderMs = await ApiRetryHandler.ExecuteWithRetriesAsync(() =>
                {
                    attempts++;
                    if (attempts < 2)
                    {
                        var resp = new FakeWebResponse(429);
                        resp.Headers.Add("retry-after-ms", "1234");
                        throw new WebException("429", null, WebExceptionStatus.ProtocolError, resp);
                    }
                    return Task.FromResult("OK");
                }, null, CancellationToken.None);
                Assert.Equal(1234, waitedTotal);
            } finally {
                ApiRetryHandler.DelayTaskAsync = (ms, ct) => Task.Delay(ms, ct);
            }
        }

        [Fact]
        public async Task ApiRetry_Headers_Seconds()
        {
            var oldDelay = ApiRetryHandler.DelayTaskAsync;
            try {
                int waitedTotal = 0;
                ApiRetryHandler.DelayTaskAsync = (ms, ct) => { waitedTotal += ms; return Task.Delay(1, ct); };
                int attempts = 0;
                var resHeaderSec = await ApiRetryHandler.ExecuteWithRetriesAsync(() =>
                {
                    attempts++;
                    if (attempts < 2)
                    {
                        var resp = new FakeWebResponse(429);
                        resp.Headers.Add("retry-after", "2");
                        throw new WebException("429", null, WebExceptionStatus.ProtocolError, resp);
                    }
                    return Task.FromResult("OK");
                }, null, CancellationToken.None);
                Assert.Equal(2000, waitedTotal);
            } finally {
                ApiRetryHandler.DelayTaskAsync = (ms, ct) => Task.Delay(ms, ct);
            }
        }

        [Fact]
        public async Task ApiRetry_Headers_Date()
        {
            var oldDelay = ApiRetryHandler.DelayTaskAsync;
            try {
                int waitedTotal = 0;
                ApiRetryHandler.DelayTaskAsync = (ms, ct) => { waitedTotal += ms; return Task.Delay(1, ct); };
                int attempts = 0;
                var resHeaderDate = await ApiRetryHandler.ExecuteWithRetriesAsync(() =>
                {
                    attempts++;
                    if (attempts < 2)
                    {
                        var resp = new FakeWebResponse(429);
                        resp.Headers.Add("retry-after", DateTime.UtcNow.AddSeconds(3).ToString("R"));
                        throw new WebException("429", null, WebExceptionStatus.ProtocolError, resp);
                    }
                    return Task.FromResult("OK");
                }, null, CancellationToken.None);
                Assert.True(waitedTotal >= 2000 && waitedTotal <= 4000, "Waited: " + waitedTotal);
            } finally {
                ApiRetryHandler.DelayTaskAsync = (ms, ct) => Task.Delay(ms, ct);
            }
        }

        [Fact]
        public async Task ApiRetry_Headers_DelayCap()
        {
            var oldDelay = ApiRetryHandler.DelayTaskAsync;
            try {
                int waitedTotal = 0;
                ApiRetryHandler.DelayTaskAsync = (ms, ct) => { waitedTotal += ms; return Task.Delay(1, ct); };
                int attempts = 0;
                var resHeaderCap = await ApiRetryHandler.ExecuteWithRetriesAsync(() =>
                {
                    attempts++;
                    if (attempts < 2)
                    {
                        var resp = new FakeWebResponse(429);
                        resp.Headers.Add("retry-after", "3600");
                        throw new WebException("429", null, WebExceptionStatus.ProtocolError, resp);
                    }
                    return Task.FromResult("OK");
                }, null, CancellationToken.None);
                Assert.Equal(30000, waitedTotal);
            } finally {
                ApiRetryHandler.DelayTaskAsync = (ms, ct) => Task.Delay(ms, ct);
            }
        }

        [Fact]
        public async Task ApiRetry_Max5Attempts()
        {
            var oldDelay = ApiRetryHandler.DelayTaskAsync;
            try {
                int waitedTotal = 0;
                ApiRetryHandler.DelayTaskAsync = (ms, ct) => { waitedTotal += ms; return Task.Delay(1, ct); };
                int attempts = 0;
                await Assert.ThrowsAnyAsync<Exception>(async () => await ApiRetryHandler.ExecuteWithRetriesAsync(() =>
                {
                    attempts++;
                    var ex = new WebException("502", null, WebExceptionStatus.ProtocolError, new FakeWebResponse(502));
                    throw ex;
                }, null, CancellationToken.None));
                Assert.Equal(5, attempts);
            } finally {
                ApiRetryHandler.DelayTaskAsync = (ms, ct) => Task.Delay(ms, ct);
            }
        }
    }

    public class FakeWebResponse : WebResponse
    {
        private int _statusCode;
        private WebHeaderCollection _headers = new WebHeaderCollection();
        public FakeWebResponse(int statusCode) { _statusCode = statusCode; }
        public int StatusCode { get { return _statusCode; } }
        public override WebHeaderCollection Headers { get { return _headers; } }
    }
}
