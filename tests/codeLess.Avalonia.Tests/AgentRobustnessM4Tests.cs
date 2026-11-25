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
        public async Task Engine_Cancellation_DuringRetry_ReturnsGracefully()
        {
            var settings = new IdePersistentSettings { RequireAgentToolApproval = false };
            var harness = new LlmHarnessAndAgentEngine(settings);
            
            // Set up a mock provider that throws 429
            harness.Vendors.Add(new ByokVendor { Id = "mock", Name = "Mock", BaseUrl = "http://localhost:9999", ApiKey = "mock", SelectedModel = "mock" });
            
            // Force it to hit the network and fail with a delay
            var method = harness.GetType().GetMethod("CallByokVendorMultiTurnAsync", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public);
            
            var oldDelay = ApiRetryHandler.DelayTaskAsync;
            try {
                // Cancel streaming on the first delay
                ApiRetryHandler.DelayTaskAsync = async (ms, ct) => {
                    harness.CancelStreamingRequested = true;
                    await Task.Delay(1, ct);
                };
                
                // Need a mock server or we mock the task? Wait, ExecuteWithRetriesAsync tries to run Task.Run which attempts real HTTP. 
                // That will throw "Connection Refused" not 429. Connection Refused does not trigger a retry-after wait (GetDelayMs returns -1 or backoff).
                // Actually, if it returns -1, it doesn't wait. Wait, GetDelayMs returns backoff if delay < 0. Connection Refused doesn't have "overloaded", so it returns backoff (2000 ms).
                // It will wait 2000ms. In that wait, it gets cancelled!
                // So CallByokVendorMultiTurnAsync returns "".
                
                string res = await (Task<string>)method.Invoke(harness, new object[] { harness.Vendors[harness.Vendors.Count - 1], "sys", new List<Dictionary<string, string>>(), 0.15 });
                Assert.Equal("", res);
            }
            finally {
                ApiRetryHandler.DelayTaskAsync = oldDelay;
            }
        }

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


                    if (lastMsg["content"].Contains("<tool_result tool=\"list_dir\"")) listDirExecutionsSeen++;
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



    public class DiagnosticsProviderM7bTests
    {
        [Fact]
        public async Task Diagnostics_AppendsErrors_CapsAt20_IgnoresWarnings_AndDoesNotHang()
        {
            var harness = new LlmHarnessAndAgentEngine(new IdePersistentSettings { RequireAgentToolApproval = false });
            harness.MaxAgentIterations = 2;
            
            var fakeDiags = new FakeDiagnosticsProvider();
            for (int i = 0; i < 25; i++)
            {
                fakeDiags.Items.Add(new DiagnosticItem { Severity = "Error", Line = i + 1, Message = "Err" + i });
            }
            fakeDiags.Items.Add(new DiagnosticItem { Severity = "Warning", Line = 100, Message = "Warn" });
            
            harness.DiagnosticsProvider = fakeDiags;
            
            int waitedTotal = 0;
            var oldDelay = LlmHarnessAndAgentEngine.DiagnosticsWaitDelayAsync;
            LlmHarnessAndAgentEngine.DiagnosticsWaitDelayAsync = ms => { waitedTotal += ms; return Task.Delay(1); };
            
            int turns = 0;
            harness.CustomModelResponder = (sys, msgs) =>
            {
                turns++;
                if (turns == 1) return Task.FromResult("<tool_call>{\"tool\": \"write_file\", \"args\": {\"path\": \"/tmp/test.cs\", \"content\": \"code\"}}</tool_call>");
                return Task.FromResult("Done");
            };
            
            var method = harness.GetType().GetMethod("RunAutonomousAgentLoopCoreAsync", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            var buf = new EditorTabBuffer { FilePath = "/tmp/other.cs" };
            
            try {
                var res = await (Task<AgentExecutionTurnResult>)method.Invoke(harness, new object[] { "test", "instruct", null, null, buf, "/tmp", new List<string>(), new List<string>(), "", new Dictionary<string, string>() });
                
                Assert.Equal(1500, waitedTotal); // timeout doesn't hang and uses test seam
                Assert.Single(res.ToolCalls);
                var tc = res.ToolCalls[0];
                Assert.Contains("<file_diagnostics>", tc.Output);
                Assert.Contains("Err0", tc.Output);
                Assert.Contains("Err19", tc.Output);
                Assert.DoesNotContain("Err20", tc.Output); // capped at 20
                Assert.DoesNotContain("Warn", tc.Output); // warnings excluded
                
                // Test null provider appends nothing
                harness.DiagnosticsProvider = null;
                turns = 0;
                var resNull = await (Task<AgentExecutionTurnResult>)method.Invoke(harness, new object[] { "test", "instruct", null, null, buf, "/tmp", new List<string>(), new List<string>(), "", new Dictionary<string, string>() });
                Assert.DoesNotContain("<file_diagnostics>", resNull.ToolCalls[0].Output);
                
                // Test no errors appends nothing
                fakeDiags.Items.Clear();
                harness.DiagnosticsProvider = fakeDiags;
                turns = 0;
                var resEmpty = await (Task<AgentExecutionTurnResult>)method.Invoke(harness, new object[] { "test", "instruct", null, null, buf, "/tmp", new List<string>(), new List<string>(), "", new Dictionary<string, string>() });
                Assert.DoesNotContain("<file_diagnostics>", resEmpty.ToolCalls[0].Output);
                
            } finally {
                LlmHarnessAndAgentEngine.DiagnosticsWaitDelayAsync = oldDelay;
            }
        }
    }


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
        public async Task ApiRetry_Cancellation_SlicesDelay()
        {
            var oldDelay = ApiRetryHandler.DelayTaskAsync;
            try {
                int waitedTotal = 0;
                bool shouldCancel = false;
                ApiRetryHandler.DelayTaskAsync = async (ms, ct) => { 
                    waitedTotal += ms; 
                    if (waitedTotal > 0) shouldCancel = true;
                    await Task.Delay(1, ct); 
                };
                int attempts = 0;
                var res = await ApiRetryHandler.ExecuteWithRetriesAsync(async () =>
                {
                    attempts++;
                    if (attempts == 1)
                    {
                        var resp = new FakeWebResponse(429);
                        resp.Headers.Add("retry-after", "30"); // 30 seconds -> 30000 ms
                        throw new WebException("429", null, WebExceptionStatus.ProtocolError, resp);
                    }
                    return "OK";
                }, () => shouldCancel, CancellationToken.None);
                
                Assert.Equal(1, attempts);
                Assert.True(waitedTotal < 1000, "Waited total should be < 1000 (was " + waitedTotal + ")");
                Assert.Null(res); // returns null on cancellation
            } finally {
                ApiRetryHandler.DelayTaskAsync = oldDelay;
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
