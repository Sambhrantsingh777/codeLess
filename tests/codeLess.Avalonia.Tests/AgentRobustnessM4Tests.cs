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
            
            int attempts = 0;
            bool approvalRequested = false;
            bool allowDoomLoop = false;
            
            harness.CustomModelResponder = (sys, msgs) =>
            {
                attempts++;
                if (attempts <= 5) return Task.FromResult("<tool_call>{\"tool\": \"list_dir\", \"args\": {\"path\": \"/tmp\"}}</tool_call>");
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
            Assert.True(resultDenied.AssistantReply.Contains("Done"));
            
            // Re-test Granted
            attempts = 0;
            approvalRequested = false;
            allowDoomLoop = true;
            var resultGranted = await (Task<AgentExecutionTurnResult>)method.Invoke(harness, new object[] { "test", "instruction", null, null, buf, "/tmp", new List<string>(), new List<string>(), "", new Dictionary<string, string>() });
            Assert.True(approvalRequested);
        }
        
        [Fact]
        public async Task ApiRetryHandler_RetriesAndHeadersAndCancellation()
        {
            int waitedTotal = 0;
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
            
            // Test Cancellation
            var cts = new CancellationTokenSource();
            cts.Cancel();
            await Assert.ThrowsAnyAsync<Exception>(async () => await ApiRetryHandler.ExecuteWithRetriesAsync(async () => {
                var ex = new WebException("502", null, WebExceptionStatus.ProtocolError, new FakeWebResponse(502));
                throw ex;
            }, null, cts.Token));
            
            // Test retry-after-ms header
            attempts = 0;
            waitedTotal = 0;
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
            
            // Test retry-after seconds
            attempts = 0;
            waitedTotal = 0;
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
            // Test retry-after HTTP-date
            attempts = 0;
            waitedTotal = 0;
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
            
            // Restore static state
            ApiRetryHandler.DelayTaskAsync = (ms, ct) => Task.Delay(ms, ct);
        }

    }
    
    public class FakeWebResponse : WebResponse
    {
        private readonly HttpStatusCode code;
        private readonly WebHeaderCollection headers;
        public FakeWebResponse(int statusCode) { code = (HttpStatusCode)statusCode; headers = new WebHeaderCollection(); }
        public HttpStatusCode StatusCode { get { return code; } }
        public override WebHeaderCollection Headers { get { return headers; } }
    }
}
