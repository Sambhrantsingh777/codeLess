using System;
using System.Collections.Generic;
using System.Linq;
using System.Web.Script.Serialization;

namespace codeLess.Studio
{
    public class AgentToolSpec
    {
        public string Name { get; set; }
        public string Description { get; set; }
        public Dictionary<string, object> Parameters { get; set; }
    }

    public class NativeToolCall
    {
        public string Id { get; set; }
        public string Name { get; set; }
        private string _argsJson;
        public string ArgumentsJson { get { return _argsJson ?? ""; } set { _argsJson = value; } }
        
        public Dictionary<string, object> GetArguments()
        {
            try {
                var obj = new JavaScriptSerializer().DeserializeObject(ArgumentsJson);
                if (obj is Dictionary<string, object>) return (Dictionary<string, object>)obj;
                throw new Exception("Not a dict");
            } catch {
                var d = new Dictionary<string, object>();
                d["error"] = "Malformed JSON arguments";
                return d;
            }
        }
    }

    public static class NativeToolCalling
    {
        private static readonly JavaScriptSerializer Serializer = new JavaScriptSerializer();
        
        public static readonly AgentToolSpec[] Tools = new AgentToolSpec[]
        {
            new AgentToolSpec { Name = "read_file", Description = "Read the contents of a file.", Parameters = (Dictionary<string, object>)Serializer.DeserializeObject("{\"type\":\"object\",\"properties\":{\"path\":{\"type\":\"string\"},\"start_line\":{\"type\":\"integer\"},\"end_line\":{\"type\":\"integer\"},\"offset\":{\"type\":\"integer\"},\"limit\":{\"type\":\"integer\"}},\"required\":[\"path\"]}") },
            new AgentToolSpec { Name = "write_file", Description = "Write new content to a file, overwriting it entirely.", Parameters = (Dictionary<string, object>)Serializer.DeserializeObject("{\"type\":\"object\",\"properties\":{\"path\":{\"type\":\"string\"},\"content\":{\"type\":\"string\"}},\"required\":[\"path\",\"content\"]}") },
            new AgentToolSpec { Name = "edit_file", Description = "Edit a file using search and replace blocks.", Parameters = (Dictionary<string, object>)Serializer.DeserializeObject("{\"type\":\"object\",\"properties\":{\"path\":{\"type\":\"string\"},\"search\":{\"type\":\"string\"},\"replace\":{\"type\":\"string\"},\"oldString\":{\"type\":\"string\"},\"newString\":{\"type\":\"string\"},\"replaceAll\":{\"type\":\"boolean\"}},\"required\":[\"path\"]}") },
            new AgentToolSpec { Name = "multiedit", Description = "Apply multiple edits to a file atomically.", Parameters = (Dictionary<string, object>)Serializer.DeserializeObject("{\"type\":\"object\",\"properties\":{\"path\":{\"type\":\"string\"},\"edits\":{\"type\":\"array\",\"items\":{\"type\":\"object\",\"properties\":{\"search\":{\"type\":\"string\"},\"replace\":{\"type\":\"string\"},\"oldString\":{\"type\":\"string\"},\"newString\":{\"type\":\"string\"},\"replaceAll\":{\"type\":\"boolean\"}}}}},\"required\":[\"path\",\"edits\"]}") },
            new AgentToolSpec { Name = "list_dir", Description = "List files and directories in a folder.", Parameters = (Dictionary<string, object>)Serializer.DeserializeObject("{\"type\":\"object\",\"properties\":{\"path\":{\"type\":\"string\"}},\"required\":[\"path\"]}") },
            new AgentToolSpec { Name = "grep_search", Description = "Search for a regex pattern across files.", Parameters = (Dictionary<string, object>)Serializer.DeserializeObject("{\"type\":\"object\",\"properties\":{\"query\":{\"type\":\"string\"},\"include\":{\"type\":\"string\"}},\"required\":[\"query\"]}") },
            new AgentToolSpec { Name = "glob", Description = "Find files matching a glob pattern.", Parameters = (Dictionary<string, object>)Serializer.DeserializeObject("{\"type\":\"object\",\"properties\":{\"pattern\":{\"type\":\"string\"},\"path\":{\"type\":\"string\"}},\"required\":[\"pattern\"]}") },
            new AgentToolSpec { Name = "run_terminal", Description = "Run a shell command in the terminal.", Parameters = (Dictionary<string, object>)Serializer.DeserializeObject("{\"type\":\"object\",\"properties\":{\"command\":{\"type\":\"string\"},\"timeout\":{\"type\":\"integer\"},\"description\":{\"type\":\"string\"}},\"required\":[\"command\"]}") },
            new AgentToolSpec { Name = "get_diagnostics", Description = "Get language server diagnostics (errors/warnings) for a file.", Parameters = (Dictionary<string, object>)Serializer.DeserializeObject("{\"type\":\"object\",\"properties\":{\"path\":{\"type\":\"string\"}},\"required\":[\"path\"]}") },
            new AgentToolSpec { Name = "ast_outline", Description = "Get an AST outline of a file.", Parameters = (Dictionary<string, object>)Serializer.DeserializeObject("{\"type\":\"object\",\"properties\":{\"path\":{\"type\":\"string\"}},\"required\":[\"path\"]}") },
            new AgentToolSpec { Name = "webfetch", Description = "Fetch content from a URL.", Parameters = (Dictionary<string, object>)Serializer.DeserializeObject("{\"type\":\"object\",\"properties\":{\"url\":{\"type\":\"string\"},\"format\":{\"type\":\"string\"},\"timeout\":{\"type\":\"integer\"}},\"required\":[\"url\"]}") },
            new AgentToolSpec { Name = "task", Description = "Run a nested read-only subagent task.", Parameters = (Dictionary<string, object>)Serializer.DeserializeObject("{\"type\":\"object\",\"properties\":{\"description\":{\"type\":\"string\"},\"prompt\":{\"type\":\"string\"}},\"required\":[\"prompt\"]}") },
            new AgentToolSpec { Name = "todowrite", Description = "Write the plan todos.", Parameters = (Dictionary<string, object>)Serializer.DeserializeObject("{\"type\":\"object\",\"properties\":{\"todos\":{\"type\":\"array\"}},\"required\":[\"todos\"]}") },
            new AgentToolSpec { Name = "todoread", Description = "Read the plan todos.", Parameters = (Dictionary<string, object>)Serializer.DeserializeObject("{\"type\":\"object\",\"properties\":{},\"required\":[]}") }
        };

        public static object BuildOpenAiTools()
        {
            var res = new List<object>();
            foreach (var t in Tools)
            {
                var fn = new Dictionary<string, object>();
                fn["name"] = t.Name;
                fn["description"] = t.Description;
                fn["parameters"] = t.Parameters;
                var tool = new Dictionary<string, object>();
                tool["type"] = "function";
                tool["function"] = fn;
                res.Add(tool);
            }
            return res;
        }

        public static object BuildAnthropicTools()
        {
            var res = new List<object>();
            foreach (var t in Tools)
            {
                var tool = new Dictionary<string, object>();
                tool["name"] = t.Name;
                tool["description"] = t.Description;
                tool["input_schema"] = t.Parameters;
                res.Add(tool);
            }
            return res;
        }

        public static List<object> SerialiseOpenAiHistory(List<NativeToolCall> calls, string text)
        {
            var res = new List<object>();
            
            var tool_calls = new List<object>();
            foreach(var c in calls)
            {
                var f = new Dictionary<string, object>();
                f["name"] = c.Name;
                f["arguments"] = c.ArgumentsJson;
                
                var t = new Dictionary<string, object>();
                t["id"] = c.Id;
                t["type"] = "function";
                t["function"] = f;
                tool_calls.Add(t);
            }
            
            var assistantMsg = new Dictionary<string, object>();
            assistantMsg["role"] = "assistant";
            assistantMsg["content"] = text;
            if (tool_calls.Count > 0)
                assistantMsg["tool_calls"] = tool_calls;
                
            res.Add(assistantMsg);
            
            foreach (var c in calls)
            {
                var toolMsg = new Dictionary<string, object>();
                toolMsg["role"] = "tool";
                toolMsg["tool_call_id"] = c.Id;
                toolMsg["content"] = ""; // The result will be filled later, here we just output the shape requested
                res.Add(toolMsg);
            }
            
            return res;
        }

        public static List<object> SerialiseAnthropicHistory(List<NativeToolCall> calls, string text)
        {
            var res = new List<object>();
            
            var content = new List<object>();
            if (!string.IsNullOrEmpty(text))
            {
                var textBlock = new Dictionary<string, object>();
                textBlock["type"] = "text";
                textBlock["text"] = text;
                content.Add(textBlock);
            }
            
            foreach (var c in calls)
            {
                var tu = new Dictionary<string, object>();
                tu["type"] = "tool_use";
                tu["id"] = c.Id;
                tu["name"] = c.Name;
                tu["input"] = c.GetArguments(); // Anthropic uses parsed JSON object here
                content.Add(tu);
            }
            
            var assistantMsg = new Dictionary<string, object>();
            assistantMsg["role"] = "assistant";
            assistantMsg["content"] = content;
            res.Add(assistantMsg);
            
            var userContent = new List<object>();
            foreach (var c in calls)
            {
                var tr = new Dictionary<string, object>();
                tr["type"] = "tool_result";
                tr["tool_use_id"] = c.Id;
                tr["content"] = ""; // Result to be filled later
                tr["is_error"] = false;
                userContent.Add(tr);
            }
            
            if (userContent.Count > 0)
            {
                var userMsg = new Dictionary<string, object>();
                userMsg["role"] = "user";
                userMsg["content"] = userContent;
                res.Add(userMsg);
            }
            
            return res;
        }
    }

    public class OpenAiToolCallAccumulator
    {
        private List<NativeToolCall> _calls = new List<NativeToolCall>();
        public List<NativeToolCall> Calls { get { return _calls; } set { _calls = value; } }
        private string _text = "";
        public string Text { get { return _text; } set { _text = value; } }
        private bool _isFinished;
        public bool IsFinished { get { return _isFinished; } set { _isFinished = value; } }

        public void Accumulate(Dictionary<string, object> chunk)
        {
            if (chunk == null) return;
            if (chunk.ContainsKey("choices"))
            {
                var choices = chunk["choices"] as System.Collections.ArrayList;
                if (choices != null && choices.Count > 0)
                {
                    var choice = choices[0] as Dictionary<string, object>;
                    if (choice != null)
                    {
                        if (choice.ContainsKey("finish_reason") && choice["finish_reason"] != null)
                        {
                            string fr = choice["finish_reason"].ToString();
                            if (fr == "tool_calls") IsFinished = true;
                        }

                        if (choice.ContainsKey("delta"))
                        {
                            var delta = choice["delta"] as Dictionary<string, object>;
                            if (delta != null)
                            {
                                if (delta.ContainsKey("content") && delta["content"] != null)
                                {
                                    Text += delta["content"].ToString();
                                }

                                if (delta.ContainsKey("tool_calls"))
                                {
                                    var tcs = delta["tool_calls"] as System.Collections.ArrayList;
                                    if (tcs != null)
                                    {
                                        foreach (var obj in tcs)
                                        {
                                            var tc = obj as Dictionary<string, object>;
                                            if (tc != null && tc.ContainsKey("index"))
                                            {
                                                int idx = Convert.ToInt32(tc["index"]);
                                                while (Calls.Count <= idx) Calls.Add(new NativeToolCall());
                                                var call = Calls[idx];
                                                
                                                if (tc.ContainsKey("id") && tc["id"] != null)
                                                    call.Id = tc["id"].ToString();
                                                
                                                if (tc.ContainsKey("function"))
                                                {
                                                    var fn = tc["function"] as Dictionary<string, object>;
                                                    if (fn != null)
                                                    {
                                                        if (fn.ContainsKey("name") && fn["name"] != null)
                                                            call.Name = fn["name"].ToString();
                                                        if (fn.ContainsKey("arguments") && fn["arguments"] != null)
                                                            call.ArgumentsJson += fn["arguments"].ToString();
                                                    }
                                                }
                                            }
                                        }
                                    }
                                }
                            }
                        }
                    }
                }
            }
        }
    }

    public class AnthropicToolCallAccumulator
    {
        private List<NativeToolCall> _calls = new List<NativeToolCall>();
        public List<NativeToolCall> Calls { get { return _calls; } set { _calls = value; } }
        private string _text = "";
        public string Text { get { return _text; } set { _text = value; } }
        private bool _isFinished;
        public bool IsFinished { get { return _isFinished; } set { _isFinished = value; } }

        public void Accumulate(Dictionary<string, object> chunk)
        {
            if (chunk == null) return;
            if (chunk.ContainsKey("type"))
            {
                string type = chunk["type"].ToString();
                
                if (type == "message_delta")
                {
                    if (chunk.ContainsKey("delta"))
                    {
                        var delta = chunk["delta"] as Dictionary<string, object>;
                        if (delta != null && delta.ContainsKey("stop_reason") && delta["stop_reason"] != null)
                        {
                            if (delta["stop_reason"].ToString() == "tool_use")
                                IsFinished = true;
                        }
                    }
                }
                else if (type == "content_block_start")
                {
                    if (chunk.ContainsKey("content_block"))
                    {
                        var cb = chunk["content_block"] as Dictionary<string, object>;
                        if (cb != null && cb.ContainsKey("type") && cb["type"].ToString() == "tool_use")
                        {
                            var call = new NativeToolCall();
                            if (cb.ContainsKey("id") && cb["id"] != null) call.Id = cb["id"].ToString();
                            if (cb.ContainsKey("name") && cb["name"] != null) call.Name = cb["name"].ToString();
                            Calls.Add(call);
                        }
                    }
                }
                else if (type == "content_block_delta")
                {
                    if (chunk.ContainsKey("delta"))
                    {
                        var delta = chunk["delta"] as Dictionary<string, object>;
                        if (delta != null && delta.ContainsKey("type"))
                        {
                            string dtype = delta["type"].ToString();
                            if (dtype == "text_delta" && delta.ContainsKey("text") && delta["text"] != null)
                            {
                                Text += delta["text"].ToString();
                            }
                            else if (dtype == "input_json_delta" && delta.ContainsKey("partial_json") && delta["partial_json"] != null)
                            {
                                if (Calls.Count > 0)
                                {
                                    Calls[Calls.Count - 1].ArgumentsJson += delta["partial_json"].ToString();
                                }
                            }
                        }
                    }
                }
            }
        }
    }
}
