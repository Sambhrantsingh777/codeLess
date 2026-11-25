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
            
            var myVendor = new codeLess.Studio.ByokVendor { Id = "mock", Name = "Mock", BaseUrl = "http://localhost:9999", ApiKey = "mockkey1234", SelectedModel = "mock" };
            
            var method = harness.GetType().GetMethod("CallByokVendorMultiTurnAsync", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public);
            
            var currentTurnEngineField = typeof(LlmHarnessAndAgentEngine).GetField("currentTurnEngine", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
            var asyncLocal = currentTurnEngineField.GetValue(null);
            var valProp = asyncLocal.GetType().GetProperty("Value");
            valProp.SetValue(asyncLocal, harness, null);
            
            harness.CancelStreamingRequested = true;
            
            string res = await (Task<string>)method.Invoke(harness, new object[] { myVendor, "sys", new List<Dictionary<string, string>>(), 0.15 });
            Assert.Equal("", res);
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

            var buf = new EditorTabBuffer { FilePath = "/tmp/test_" + Guid.NewGuid().ToString() + ".cs" };
            var method = harness.GetType().GetMethod("RunAutonomousAgentLoopCoreAsync", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            
            // Test Denied
            var resultDenied = await (Task<AgentExecutionTurnResult>)method.Invoke(harness, new object[] { "test", "instruction", null, null, buf, "/tmp", new List<string>(), new List<string>(), "build", new Dictionary<string, string>() });
            Assert.True(approvalRequested);
            Assert.True(sawDenialMessage, "Model should receive a denial message");
            Assert.Equal(3, listDirExecutionsSeen); // 1, 2, 3 executed, 4 denied
            
            // Re-test Granted
            listDirCallsFromModel = 0;
            listDirExecutionsSeen = 0;
            approvalRequested = false;
            allowDoomLoop = true;
            sawDenialMessage = false;
            
            var resultGranted = await (Task<AgentExecutionTurnResult>)method.Invoke(harness, new object[] { "test", "instruction", null, null, buf, "/tmp", new List<string>(), new List<string>(), "build", new Dictionary<string, string>() });
            Assert.True(approvalRequested);
            Assert.False(sawDenialMessage, "Should not see denial message when granted");
            Assert.Equal(4, listDirExecutionsSeen); // 1, 2, 3, 4 executed
        }
    }

    [CollectionDefinition("ApiRetryStatic", DisableParallelization = true)]
    public class ApiRetryStaticCollection { }


    public class FakeDiagnosticsProvider : IDiagnosticsProvider
    {
        public List<DiagnosticItem> Items { get; set; } = new List<DiagnosticItem>();
        public int WaitedMs { get; set; } = 0;
        
        public async Task<List<DiagnosticItem>> GetDiagnosticsAsync(string filePath, string expectedContent, int timeoutMs)
        {
            if (expectedContent != "disk_code" && expectedContent != "code") {
                throw new Exception("Unexpected content: " + expectedContent);
            }
            WaitedMs += timeoutMs;
            await Task.Delay(1);
            return Items;
        }
    }

    public class DiagnosticsProviderM7bTests : IDisposable
    {
        private readonly string _testDir;

        public DiagnosticsProviderM7bTests()
        {
            _testDir = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "m7b_diag_" + Guid.NewGuid().ToString("N")));
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
        public async Task Diagnostics_NullProvider_IsFastAndAddsNoBlock()
        {
            var harness = new LlmHarnessAndAgentEngine(new IdePersistentSettings { RequireAgentToolApproval = false });
            harness.MaxAgentIterations = 2;
            harness.DiagnosticsProvider = null;
            string fastFile = Path.Combine(_testDir, "test_fast.txt").Replace("\\", "/");
            string otherFile = Path.Combine(_testDir, "other.cs");
            
            harness.OnRequestToolApproval = (tool, args) => true;
            int turns = 0;
            harness.CustomModelResponder = (sys, msgs) =>
            {
                turns++;
                if (turns == 1) return Task.FromResult("<tool_call>{\"tool\": \"write_file\", \"args\": {\"path\": \"" + fastFile + "\", \"content\": \"code\"}}</tool_call>");
                return Task.FromResult("Done");
            };
            
            var method = harness.GetType().GetMethod("RunAutonomousAgentLoopCoreAsync", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            var buf = new EditorTabBuffer { FilePath = otherFile };
            
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var res = await (Task<AgentExecutionTurnResult>)method.Invoke(harness, new object[] { "test", "instruct", null, null, buf, _testDir, new List<string>(), new List<string>(), "build", new Dictionary<string, string>() });
            sw.Stop();
            
            Assert.True(sw.ElapsedMilliseconds < 1000, "Turn should be fast without provider");
            Assert.DoesNotContain("<file_diagnostics", res.ToolCalls[0].Output);
        }

        [Fact]
        public async Task LspDiagnosticsProvider_ContentMismatch_ReturnsNull()
        {
            var p = new codeLess.AvaloniaApp.LspDiagnosticsProvider();
            string mismatchFile = Path.Combine(_testDir, "test_mismatch.txt");
            System.IO.File.WriteAllText(mismatchFile, "disk_code");
            var res = await p.GetDiagnosticsAsync(mismatchFile, "staged_code", 100);
            Assert.Null(res);
        }

        [Fact]
        public async Task LspDiagnosticsProvider_ContentMatch_ReturnsErrors()
        {
            var p = new codeLess.AvaloniaApp.LspDiagnosticsProvider();
            string matchFile = Path.Combine(_testDir, "test_match.txt");
            System.IO.File.WriteAllText(matchFile, "disk_code");
            
            try {
                LspDiagnosticsStore.Set(matchFile, new List<DiagnosticItem> { new DiagnosticItem { Severity = "Error", Line = 1, Message = "Test" } });
                var res = await p.GetDiagnosticsAsync(matchFile, "disk_code", 100);
                Assert.NotNull(res);
                Assert.Single(res);
                Assert.Equal("Test", res[0].Message);
            } finally {
                LspDiagnosticsStore.Set(matchFile, null);
            }
        }

        [Fact]
        public async Task Diagnostics_AppendsErrors_CapsAt20_IgnoresWarnings_AndDoesNotHang()
        {
            var harness = new LlmHarnessAndAgentEngine(new IdePersistentSettings { RequireAgentToolApproval = false });
            harness.MaxAgentIterations = 2;
            harness.OnRequestToolApproval = (tool, args) => true;
            
            var fakeDiags = new FakeDiagnosticsProvider();
            for (int i = 0; i < 25; i++)
            {
                fakeDiags.Items.Add(new DiagnosticItem { Severity = "Error", Line = i + 1, Message = "Err" + i });
            }
            fakeDiags.Items.Add(new DiagnosticItem { Severity = "Warning", Line = 100, Message = "Warn" });
            
            harness.DiagnosticsProvider = fakeDiags;
            
            int turns = 0;
            harness.CustomModelResponder = (sys, msgs) =>
            {
                turns++;
                string fPath = Path.Combine(_testDir, "test_" + Guid.NewGuid().ToString("N") + ".txt").Replace("\\", "/");
                if (turns == 1) return Task.FromResult("<tool_call>{\"tool\": \"write_file\", \"args\": {\"path\": \"" + fPath + "\", \"content\": \"code\"}}</tool_call>");
                return Task.FromResult("Done");
            };
            
            var method = harness.GetType().GetMethod("RunAutonomousAgentLoopCoreAsync", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            var buf = new EditorTabBuffer { FilePath = Path.Combine(_testDir, "other.cs") };
            
            var res = await (Task<AgentExecutionTurnResult>)method.Invoke(harness, new object[] { "test", "instruct", null, null, buf, _testDir, new List<string>(), new List<string>(), "build", new Dictionary<string, string>() });
            
            Assert.Single(res.ToolCalls);
            var tc = res.ToolCalls[0];
            Assert.True(fakeDiags.WaitedMs > 0, "Provider should wait but didn't. Tool Output: " + tc.Output);
            Assert.Contains("<file_diagnostics path", tc.Output);
            Assert.Contains("Err0", tc.Output);
            Assert.Contains("Err19", tc.Output);
            Assert.DoesNotContain("Err20", tc.Output); // capped at 20
            Assert.DoesNotContain("Warn", tc.Output); // warnings excluded
            
            // Test null provider appends nothing
            harness.DiagnosticsProvider = null;
            turns = 0;
            var resNull = await (Task<AgentExecutionTurnResult>)method.Invoke(harness, new object[] { "test", "instruct", null, null, buf, _testDir, new List<string>(), new List<string>(), "build", new Dictionary<string, string>() });
            Assert.DoesNotContain("<file_diagnostics path", resNull.ToolCalls[0].Output);
            
            // Test no errors appends nothing
            fakeDiags.Items.Clear();
            harness.DiagnosticsProvider = fakeDiags;
            turns = 0;
            var resEmpty = await (Task<AgentExecutionTurnResult>)method.Invoke(harness, new object[] { "test", "instruct", null, null, buf, _testDir, new List<string>(), new List<string>(), "build", new Dictionary<string, string>() });
            Assert.DoesNotContain("<file_diagnostics path", resEmpty.ToolCalls[0].Output);
        }
        
        [Fact]
        public async Task Diagnostics_NotifyChangesApplied_AppendsToNextTurnContext()
        {
            var harness = new LlmHarnessAndAgentEngine(new IdePersistentSettings { RequireAgentToolApproval = false });
            harness.MaxAgentIterations = 3;
            
            var fakeDiags = new FakeDiagnosticsProvider();
            fakeDiags.Items.Add(new DiagnosticItem { Severity = "Error", Line = 1, Column = 1, Message = "PostApplyErr" });
            harness.DiagnosticsProvider = fakeDiags;
            
            string postApplyFile = Path.Combine(_testDir, "post_apply_test.txt");
            System.IO.File.WriteAllText(postApplyFile, "disk_code");
            harness.NotifyChangesApplied(new[] { postApplyFile });
            
            int turns = 0;
            bool sawErrorInContext = false;
            harness.CustomModelResponder = (sys, msgs) =>
            {
                turns++;
                var lastMsg = msgs[msgs.Count - 1];
                if (lastMsg["content"].Contains("PostApplyErr"))
                    sawErrorInContext = true;
                
                if (turns == 1) return Task.FromResult("Thinking...");
                return Task.FromResult("Done");
            };
            
            var method = harness.GetType().GetMethod("RunAutonomousAgentLoopCoreAsync", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            var buf = new EditorTabBuffer { FilePath = Path.Combine(_testDir, "other.cs") };
            
            await (Task<AgentExecutionTurnResult>)method.Invoke(harness, new object[] { "test", "instruct", null, null, buf, _testDir, new List<string>(), new List<string>(), "build", new Dictionary<string, string>() });
            
            Assert.True(sawErrorInContext, "Should see the post-apply diagnostic error in context.");
            Assert.Empty(harness.PendingPostApplyDiagnosticsPaths);
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
        private string _body;
        private WebHeaderCollection _headers = new WebHeaderCollection();
        public FakeWebResponse(int statusCode, string body = "") { _statusCode = statusCode; _body = body ?? ""; }
        public int StatusCode { get { return _statusCode; } }
        public override WebHeaderCollection Headers { get { return _headers; } }
        public override Stream GetResponseStream()
        {
            return new MemoryStream(System.Text.Encoding.UTF8.GetBytes(_body));
        }
    }
}
