using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Threading.Tasks;
using Xunit;
using codeLess.Studio;
using System.Web.Script.Serialization;

namespace codeLess.Avalonia.Tests
{
    public class NativeToolCallingEngineTests : IDisposable
    {
        private readonly JavaScriptSerializer _js = new JavaScriptSerializer();
        private readonly string _testDir;

        public NativeToolCallingEngineTests()
        {
            _testDir = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "m7c_engine_" + Guid.NewGuid().ToString("N")));
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
        public async Task Engine_Native_OpenAi_ExecutesAndMaintainsHistory()
        {
            var harness = new LlmHarnessAndAgentEngine(new IdePersistentSettings { RequireAgentToolApproval = false, NativeToolCalling = true });
            harness.OnRequestToolApproval = (tool, args) => true;
            harness.EnableSseStreaming = true;
            harness.Vendors.Add(new ByokVendor { Id = "openai", ApiKey = "fake_key", SelectedModel = "gpt-4o", BaseUrl = "http://127.0.0.1:8080" });
            
            var streamedChunks = new List<string>();
            harness.OnStreamingChunk += chunk => streamedChunks.Add(chunk);

            string fakeFile = Path.Combine(_testDir, "fake.txt");
            File.WriteAllText(fakeFile, "file content");
            string escapedPath = fakeFile.Replace("\\", "\\\\");

            var payloads = new List<string>();
            harness.NativeTransportOverride = (payload, vendor) =>
            {
                payloads.Add(payload);
                if (payloads.Count == 1)
                {
                    return new string[]
                    {
                        "data: {\"choices\":[{\"delta\":{\"content\":\"thinking\"}}]}",
                        "data: {\"choices\":[{\"delta\":{\"tool_calls\":[{\"index\":0,\"id\":\"call_1\",\"function\":{\"name\":\"read_file\",\"arguments\":\"{\\\"path\\\":\\\"" + escapedPath + "\\\"}\"}}]}}]}",
                        "data: [DONE]"
                    };
                }
                return new string[]
                {
                    "data: {\"choices\":[{\"delta\":{\"content\":\"all done\"}}]}",
                    "data: [DONE]"
                };
            };

            var res = await harness.RunAutonomousAgentLoopAsync("agent", "do it", harness.Vendors.Last(), "auto", null, _testDir, null, null, "", new Dictionary<string, string>());

            Assert.Equal(2, payloads.Count);
            Assert.Equal("all done", res.AssistantReply);
            Assert.Contains("thinking", streamedChunks);
            Assert.Contains("all done", streamedChunks);
            
            var p1 = _js.DeserializeObject(payloads[0]) as Dictionary<string, object>;
            Assert.True(p1.ContainsKey("tools"));
            var toolsArr = p1["tools"] as System.Collections.ArrayList;
            Assert.NotNull(toolsArr);
            Assert.Equal(LlmHarnessAndAgentEngine.Tools.Length, toolsArr.Count);
            Assert.Equal("auto", p1["tool_choice"]);
            
            var p2 = _js.DeserializeObject(payloads[1]) as Dictionary<string, object>;
            var messages = p2["messages"] as System.Collections.ArrayList;
            
            bool foundToolCall = false;
            bool foundToolResult = false;
            foreach (Dictionary<string, object> m in messages)
            {
                if (m["role"].ToString() == "assistant" && m.ContainsKey("tool_calls")) foundToolCall = true;
                if (m["role"].ToString() == "tool" && m.ContainsKey("content") && m["content"] != null && m["content"].ToString().Contains("file content")) foundToolResult = true;
            }
            Assert.True(foundToolCall, "Assistant message with tool_calls not found in history");
            Assert.True(foundToolResult, "Tool result message not found in history");
        }

        [Fact]
        public async Task Engine_Native_Anthropic_ExecutesAndMaintainsHistory()
        {
            var harness = new LlmHarnessAndAgentEngine(new IdePersistentSettings { RequireAgentToolApproval = false, NativeToolCalling = true });
            harness.OnRequestToolApproval = (tool, args) => true;
            harness.EnableSseStreaming = true;
            harness.Vendors.Add(new ByokVendor { Id = "anthropic", ApiKey = "fake_key", SelectedModel = "claude-3-sonnet", BaseUrl = "http://127.0.0.1:8080" });
            
            string fakeFile = Path.Combine(_testDir, "fake.txt");
            File.WriteAllText(fakeFile, "file content");
            string escapedPath = fakeFile.Replace("\\", "\\\\");

            var payloads = new List<string>();
            harness.NativeTransportOverride = (payload, vendor) =>
            {
                payloads.Add(payload);
                if (payloads.Count == 1)
                {
                    return new string[]
                    {
                        "data: {\"type\":\"content_block_start\",\"content_block\":{\"type\":\"tool_use\",\"id\":\"call_1\",\"name\":\"read_file\"}}",
                        "data: {\"type\":\"content_block_delta\",\"delta\":{\"type\":\"input_json_delta\",\"partial_json\":\"{\\\"path\\\":\\\"" + escapedPath + "\\\"}\"}}",
                        "data: {\"type\":\"message_delta\",\"delta\":{\"stop_reason\":\"tool_use\"}}"
                    };
                }
                return new string[]
                {
                    "data: {\"type\":\"content_block_delta\",\"delta\":{\"type\":\"text_delta\",\"text\":\"done\"}}",
                    "data: {\"type\":\"message_delta\",\"delta\":{\"stop_reason\":\"end_turn\"}}"
                };
            };

            var res = await harness.RunAutonomousAgentLoopAsync("agent", "do it", harness.Vendors.Last(), "auto", null, _testDir, null, null, "", new Dictionary<string, string>());

            Assert.Equal(2, payloads.Count);
            Assert.Equal("done", res.AssistantReply);
            
            var p1 = _js.DeserializeObject(payloads[0]) as Dictionary<string, object>;
            Assert.True(p1.ContainsKey("tools"));
            
            var p2 = _js.DeserializeObject(payloads[1]) as Dictionary<string, object>;
            var messages = p2["messages"] as System.Collections.ArrayList;
            
            bool foundToolUse = false;
            bool foundToolResult = false;
            foreach (Dictionary<string, object> m in messages)
            {
                if (m["role"].ToString() == "assistant" && m.ContainsKey("content") && _js.Serialize(m["content"]).Contains("tool_use")) foundToolUse = true;
                if (m["role"].ToString() == "user" && m.ContainsKey("content") && _js.Serialize(m["content"]).Contains("tool_result") && _js.Serialize(m["content"]).Contains("file content")) foundToolResult = true;
            }
            Assert.True(foundToolUse, "Assistant message with tool_use not found in history");
            Assert.True(foundToolResult, "Tool result message not found in history");
        }

        [Fact]
        public async Task Engine_Native_Fallback_On400()
        {
            var harness = new LlmHarnessAndAgentEngine(new IdePersistentSettings { RequireAgentToolApproval = false, NativeToolCalling = true });
            harness.OnRequestToolApproval = (tool, args) => true;
            harness.EnableSseStreaming = true;
            harness.Vendors.Add(new ByokVendor { Id = "openai", ApiKey = "fake_key", SelectedModel = "gpt-4o", BaseUrl = "http://127.0.0.1:8080" });
            
            var statusLines = new List<string>();
            harness.OnAgentFeedStatus = s => statusLines.Add(s);

            var payloads = new List<string>();
            harness.NativeTransportOverride = (payload, vendor) =>
            {
                payloads.Add(payload);
                var p = _js.DeserializeObject(payload) as Dictionary<string, object>;
                if (p.ContainsKey("tools"))
                {
                    throw new WebException("Bad Request", null, WebExceptionStatus.ProtocolError,
                        new FakeWebResponse(400, "{\"error\":{\"message\":\"This model does not support tools or function calling\"}}"));
                }
                return new string[]
                {
                    "data: {\"choices\":[{\"delta\":{\"content\":\"all done text mode\"}}]}",
                    "data: [DONE]"
                };
            };

            var res = await harness.RunAutonomousAgentLoopAsync("agent", "do it", harness.Vendors.Last(), "auto", null, _testDir, null, null, "", new Dictionary<string, string>());

            Assert.Equal(2, payloads.Count);
            Assert.Single(statusLines);
            Assert.Equal("all done text mode", res.AssistantReply);
            
            var p1 = _js.DeserializeObject(payloads[0]) as Dictionary<string, object>;
            Assert.True(p1.ContainsKey("tools"), "First request should have tools");
            
            var p2 = _js.DeserializeObject(payloads[1]) as Dictionary<string, object>;
            Assert.False(p2.ContainsKey("tools"), "Second request (retry) should not have tools");

            // Next turn on the same engine with the same vendor+model must also not send tools
            var res2 = await harness.RunAutonomousAgentLoopAsync("agent", "second turn", harness.Vendors.Last(), "auto", null, _testDir, null, null, "", new Dictionary<string, string>());
            Assert.Equal(3, payloads.Count);
            var p3 = _js.DeserializeObject(payloads[2]) as Dictionary<string, object>;
            Assert.False(p3.ContainsKey("tools"), "Subsequent turn on the same vendor+model should remain text-only");
        }

        [Fact]
        public async Task Engine_Native_SettingOff_NoToolsPayload()
        {
            var harness = new LlmHarnessAndAgentEngine(new IdePersistentSettings { RequireAgentToolApproval = false, NativeToolCalling = false });
            harness.EnableSseStreaming = true;
            harness.Vendors.Add(new ByokVendor { Id = "openai", ApiKey = "fake_key", SelectedModel = "gpt-4o", BaseUrl = "http://127.0.0.1:8080" });
            
            var payloads = new List<string>();
            harness.NativeTransportOverride = (payload, vendor) =>
            {
                payloads.Add(payload);
                return new string[]
                {
                    "data: {\"choices\":[{\"delta\":{\"content\":\"text format\"}}]}",
                    "data: [DONE]"
                };
            };

            var res = await harness.RunAutonomousAgentLoopAsync("agent", "do it", harness.Vendors.Last(), "auto", null, _testDir, null, null, "", new Dictionary<string, string>());
            
            Assert.Single(payloads);
            var p1 = _js.DeserializeObject(payloads[0]) as Dictionary<string, object>;
            Assert.False(p1.ContainsKey("tools"), "Request should not have tools when setting is off");
        }

        [Fact]
        public async Task Engine_PlanMode_DeniesWriteFile()
        {
            var harness = new LlmHarnessAndAgentEngine(new IdePersistentSettings { RequireAgentToolApproval = false, NativeToolCalling = true });
            harness.OnRequestToolApproval = (tool, args) => true;
            harness.EnableSseStreaming = true;
            harness.Vendors.Add(new ByokVendor { Id = "openai", ApiKey = "fake_key", SelectedModel = "gpt-4o", BaseUrl = "http://127.0.0.1:8080" });
            
            string targetPath = Path.Combine(_testDir, "plan_target.txt").Replace("\\", "\\\\");
            var payloads = new List<string>();
            harness.NativeTransportOverride = (payload, vendor) =>
            {
                payloads.Add(payload);
                if (payloads.Count == 1)
                {
                    return new string[]
                    {
                        "data: {\"choices\":[{\"delta\":{\"tool_calls\":[{\"index\":0,\"id\":\"call_1\",\"function\":{\"name\":\"write_file\",\"arguments\":\"{\\\"path\\\":\\\"" + targetPath + "\\\",\\\"content\\\":\\\"plan\\\"}\"}}]}}]}",
                        "data: [DONE]"
                    };
                }
                return new string[]
                {
                    "data: {\"choices\":[{\"delta\":{\"content\":\"stopped\"}}]}",
                    "data: [DONE]"
                };
            };

            var res = await harness.RunAutonomousAgentLoopAsync("plan", "do it", harness.Vendors.Last(), "auto", null, _testDir, null, null, "", new Dictionary<string, string>());
            
            Assert.Equal(2, payloads.Count);
            
            // Check that the tool result was denial
            var p2 = _js.DeserializeObject(payloads[1]) as Dictionary<string, object>;
            var messages = p2["messages"] as System.Collections.ArrayList;
            bool foundDenial = false;
            foreach (Dictionary<string, object> m in messages)
            {
                if (m["role"].ToString() == "tool" && m.ContainsKey("content") && m["content"].ToString().Contains("permission")) foundDenial = true;
            }
            Assert.True(foundDenial, "Tool result message should be a read-only denial");
            Assert.Empty(res.StagedFileContents);
        }
    }
}
