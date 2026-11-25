using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Xunit;
using codeLess.Studio;
using System.Web.Script.Serialization;

namespace codeLess.Avalonia.Tests
{
    public class NativeToolCallingTests
    {
        private readonly JavaScriptSerializer _js = new JavaScriptSerializer();

        [Fact]
        public void ToolsTable_MatchesExecuteWorkspaceTool()
        {
            var engineTools = LlmHarnessAndAgentEngine.Tools.Select(t => t.Name).OrderBy(x => x).ToList();
            var nativeTools = NativeToolCalling.Tools.Select(t => t.Name).OrderBy(x => x).ToList();
            Assert.Equal(engineTools, nativeTools);
        }

        [Fact]
        public void Builders_ReturnCorrectJsonShape()
        {
            var oai = NativeToolCalling.BuildOpenAiTools() as List<object>;
            Assert.NotNull(oai);
            Assert.Equal(NativeToolCalling.Tools.Length, oai.Count);
            
            var oaiFirst = (Dictionary<string, object>)oai[0];
            Assert.Equal("function", oaiFirst["type"]);
            var fn = (Dictionary<string, object>)oaiFirst["function"];
            Assert.Equal(NativeToolCalling.Tools[0].Name, fn["name"]);
            Assert.Equal(NativeToolCalling.Tools[0].Description, fn["description"]);
            Assert.NotNull(fn["parameters"]);

            var anth = NativeToolCalling.BuildAnthropicTools() as List<object>;
            Assert.NotNull(anth);
            Assert.Equal(NativeToolCalling.Tools.Length, anth.Count);
            
            var anthFirst = (Dictionary<string, object>)anth[0];
            Assert.Equal(NativeToolCalling.Tools[0].Name, anthFirst["name"]);
            Assert.Equal(NativeToolCalling.Tools[0].Description, anthFirst["description"]);
            Assert.NotNull(anthFirst["input_schema"]);
        }

        [Fact]
        public void OpenAiAccumulator_SingleCall_MixedText_SplitArgs()
        {
            var acc = new OpenAiToolCallAccumulator();
            
            acc.Accumulate((Dictionary<string, object>)_js.DeserializeObject("{\"choices\":[{\"delta\":{\"content\":\"Here is text\"}}]}"));
            
            acc.Accumulate((Dictionary<string, object>)_js.DeserializeObject("{\"choices\":[{\"delta\":{\"tool_calls\":[{\"index\":0,\"id\":\"call_1\",\"function\":{\"name\":\"read_file\",\"arguments\":\"{\\\"path\\\": \\\"\"}}]} ]}]}"));
            acc.Accumulate((Dictionary<string, object>)_js.DeserializeObject("{\"choices\":[{\"delta\":{\"tool_calls\":[{\"index\":0,\"function\":{\"arguments\":\"file.txt\\\"\"}}]}}]}"));
            acc.Accumulate((Dictionary<string, object>)_js.DeserializeObject("{\"choices\":[{\"delta\":{\"tool_calls\":[{\"index\":0,\"function\":{\"arguments\":\"}\"}}]}}]}"));
            
            acc.Accumulate((Dictionary<string, object>)_js.DeserializeObject("{\"choices\":[{\"finish_reason\":\"tool_calls\",\"delta\":{}}]}"));
            
            Assert.True(acc.IsFinished);
            Assert.Equal("Here is text", acc.Text);
            Assert.Single(acc.Calls);
            Assert.Equal("call_1", acc.Calls[0].Id);
            Assert.Equal("read_file", acc.Calls[0].Name);
            Assert.Equal("{\"path\": \"file.txt\"}", acc.Calls[0].ArgumentsJson);
            
            var parsedArgs = acc.Calls[0].GetArguments();
            Assert.Equal("file.txt", parsedArgs["path"]);
        }

        [Fact]
        public void OpenAiAccumulator_ParallelCalls()
        {
            var acc = new OpenAiToolCallAccumulator();
            acc.Accumulate((Dictionary<string, object>)_js.DeserializeObject("{\"choices\":[{\"delta\":{\"tool_calls\":[{\"index\":0,\"id\":\"call_1\",\"function\":{\"name\":\"read_file\",\"arguments\":\"{\\\"path\\\": \\\"1\\\"}\"}}]}}]}"));
            acc.Accumulate((Dictionary<string, object>)_js.DeserializeObject("{\"choices\":[{\"delta\":{\"tool_calls\":[{\"index\":1,\"id\":\"call_2\",\"function\":{\"name\":\"read_file\",\"arguments\":\"{\\\"path\\\": \\\"2\\\"}\"}}]}}]}"));
            
            Assert.Equal(2, acc.Calls.Count);
            Assert.Equal("call_1", acc.Calls[0].Id);
            Assert.Equal("call_2", acc.Calls[1].Id);
        }

        [Fact]
        public void AnthropicAccumulator_SingleCall_MixedText_SplitArgs()
        {
            var acc = new AnthropicToolCallAccumulator();
            acc.Accumulate((Dictionary<string, object>)_js.DeserializeObject("{\"type\":\"content_block_delta\",\"delta\":{\"type\":\"text_delta\",\"text\":\"Here is text\"}}"));
            
            acc.Accumulate((Dictionary<string, object>)_js.DeserializeObject("{\"type\":\"content_block_start\",\"content_block\":{\"type\":\"tool_use\",\"id\":\"call_1\",\"name\":\"read_file\"}}"));
            acc.Accumulate((Dictionary<string, object>)_js.DeserializeObject("{\"type\":\"content_block_delta\",\"delta\":{\"type\":\"input_json_delta\",\"partial_json\":\"{\\\"path\\\": \\\"\"}}"));
            acc.Accumulate((Dictionary<string, object>)_js.DeserializeObject("{\"type\":\"content_block_delta\",\"delta\":{\"type\":\"input_json_delta\",\"partial_json\":\"file.txt\\\"\"}}"));
            acc.Accumulate((Dictionary<string, object>)_js.DeserializeObject("{\"type\":\"content_block_delta\",\"delta\":{\"type\":\"input_json_delta\",\"partial_json\":\"}\"}}"));
            
            acc.Accumulate((Dictionary<string, object>)_js.DeserializeObject("{\"type\":\"message_delta\",\"delta\":{\"stop_reason\":\"tool_use\"}}"));
            
            Assert.True(acc.IsFinished);
            Assert.Equal("Here is text", acc.Text);
            Assert.Single(acc.Calls);
            Assert.Equal("call_1", acc.Calls[0].Id);
            Assert.Equal("read_file", acc.Calls[0].Name);
            Assert.Equal("{\"path\": \"file.txt\"}", acc.Calls[0].ArgumentsJson);
        }

        [Fact]
        public void ToolCall_MalformedArgs_ReturnsErrorField()
        {
            var tc = new NativeToolCall { ArgumentsJson = "not a dictionary" };
            var args = tc.GetArguments();
            Assert.True(args.ContainsKey("error"));
            Assert.Equal("Malformed JSON arguments", args["error"]);
        }

        [Fact]
        public void HistorySerialisers_ShapeTests()
        {
            var calls = new List<NativeToolCall> { new NativeToolCall { Id = "call_1", Name = "read_file", ArgumentsJson = "{\"path\": \"f.txt\"}" } };
            
            var oaiHistory = NativeToolCalling.SerialiseOpenAiHistory(calls, "text");
            Assert.Equal(2, oaiHistory.Count);
            
            var astMsg = (Dictionary<string, object>)oaiHistory[0];
            Assert.Equal("assistant", astMsg["role"]);
            Assert.Equal("text", astMsg["content"]);
            var tcs = (List<object>)astMsg["tool_calls"];
            Assert.Single(tcs);
            var tcOai = (Dictionary<string, object>)tcs[0];
            Assert.Equal("function", tcOai["type"]);
            
            var toolMsg = (Dictionary<string, object>)oaiHistory[1];
            Assert.Equal("tool", toolMsg["role"]);
            Assert.Equal("call_1", toolMsg["tool_call_id"]);

            var anthHistory = NativeToolCalling.SerialiseAnthropicHistory(calls, "text");
            Assert.Equal(2, anthHistory.Count);
            
            var astAnth = (Dictionary<string, object>)anthHistory[0];
            Assert.Equal("assistant", astAnth["role"]);
            var astAnthContent = (List<object>)astAnth["content"];
            Assert.Equal(2, astAnthContent.Count); // text block and tool_use
            var txtBlock = (Dictionary<string, object>)astAnthContent[0];
            Assert.Equal("text", txtBlock["type"]);
            var tuBlock = (Dictionary<string, object>)astAnthContent[1];
            Assert.Equal("tool_use", tuBlock["type"]);
            
            var usrAnth = (Dictionary<string, object>)anthHistory[1];
            Assert.Equal("user", usrAnth["role"]);
            var usrAnthContent = (List<object>)usrAnth["content"];
            Assert.Single(usrAnthContent);
            var trBlock = (Dictionary<string, object>)usrAnthContent[0];
            Assert.Equal("tool_result", trBlock["type"]);
        }
    }
}
