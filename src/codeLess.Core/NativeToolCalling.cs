using System;
using System.Collections.Generic;
using System.Linq;
using System.Web.Script.Serialization;

namespace codeLess.Studio
{
    
    public class NativeToolResult
    {
        public string CallId { get; set; }
        public string Content { get; set; }
        public bool IsError { get; set; }
    }

    public class NativeToolCall
    {
        public string Id { get; set; }
        public string Name { get; set; }
        private string _argsJson;
        public string ArgumentsJson { get { return _argsJson ?? ""; } set { _argsJson = value; } }
        
        public Dictionary<string, object> GetArguments()
        {
            if (string.IsNullOrWhiteSpace(ArgumentsJson))
                return new Dictionary<string, object>();
            string jsonToParse = ArgumentsJson.IndexOf('\\') >= 0
                ? EscapeBareBackslashesInJsonStrings(ArgumentsJson)
                : ArgumentsJson;
            try {
                var obj = new JavaScriptSerializer().DeserializeObject(jsonToParse);
                if (obj is Dictionary<string, object>) return (Dictionary<string, object>)obj;
                throw new Exception("Not a dict");
            } catch {
                var d = new Dictionary<string, object>();
                d["error"] = "Malformed JSON arguments";
                return d;
            }
        }

        private static string EscapeBareBackslashesInJsonStrings(string json)
        {
            var sb = new System.Text.StringBuilder(json.Length + 16);
            int i = 0;
            while (i < json.Length)
            {
                char c = json[i];
                if (c != '"')
                {
                    sb.Append(c);
                    i++;
                    continue;
                }

                // Scan string literal [i .. endQuote]
                int start = i + 1;
                int p = start;
                bool hasInvalidJsonEscape = false;
                bool looksLikeWindowsPath = false;
                if (start + 2 < json.Length &&
                    ((json[start] >= 'A' && json[start] <= 'Z') || (json[start] >= 'a' && json[start] <= 'z')) &&
                    json[start + 1] == ':' && json[start + 2] == '\\' &&
                    (start + 3 >= json.Length || json[start + 3] != '\\'))
                {
                    looksLikeWindowsPath = true;
                }

                while (p < json.Length)
                {
                    char sc = json[p];
                    if (sc == '"')
                        break;
                    if (sc == '\\')
                    {
                        if (p + 1 < json.Length)
                        {
                            char next = json[p + 1];
                            if (next == '"' || next == '\\' || next == '/' ||
                                next == 'b' || next == 'f' || next == 'n' || next == 'r' || next == 't')
                            {
                                p += 2;
                                continue;
                            }
                            if (next == 'u' && p + 5 < json.Length &&
                                IsHexDigit(json[p + 2]) && IsHexDigit(json[p + 3]) &&
                                IsHexDigit(json[p + 4]) && IsHexDigit(json[p + 5]))
                            {
                                p += 6;
                                continue;
                            }
                        }
                        hasInvalidJsonEscape = true;
                        p++;
                        continue;
                    }
                    p++;
                }

                int endQuote = p;
                sb.Append('"');
                if (hasInvalidJsonEscape || looksLikeWindowsPath)
                {
                    int k = start;
                    while (k < endQuote)
                    {
                        char kc = json[k];
                        if (kc == '\\')
                        {
                            if (k + 1 < endQuote && (json[k + 1] == '"' || json[k + 1] == '\\'))
                            {
                                sb.Append('\\');
                                sb.Append(json[k + 1]);
                                k += 2;
                                continue;
                            }
                            sb.Append("\\\\");
                            k++;
                            continue;
                        }
                        sb.Append(kc);
                        k++;
                    }
                }
                else
                {
                    sb.Append(json, start, endQuote - start);
                }
                if (endQuote < json.Length && json[endQuote] == '"')
                {
                    sb.Append('"');
                    i = endQuote + 1;
                }
                else
                {
                    i = endQuote;
                }
            }
            return sb.ToString();
        }

        private static bool IsHexDigit(char ch)
        {
            return (ch >= '0' && ch <= '9') || (ch >= 'a' && ch <= 'f') || (ch >= 'A' && ch <= 'F');
        }
    }

    public static class NativeToolCalling
    {
        private static readonly JavaScriptSerializer Serializer = new JavaScriptSerializer();
        
        
        public static object BuildOpenAiTools(IEnumerable<AgentToolDefinition> tools)
        {
            var res = new List<object>();
            foreach (var t in tools)
            {
                var fn = new Dictionary<string, object>();
                fn["name"] = t.Name;
                fn["description"] = t.Description;
                fn["parameters"] = Serializer.DeserializeObject(t.JsonSchema);
                var tool = new Dictionary<string, object>();
                tool["type"] = "function";
                tool["function"] = fn;
                res.Add(tool);
            }
            return res;
        }

        public static object BuildAnthropicTools(IEnumerable<AgentToolDefinition> tools)
        {
            var res = new List<object>();
            foreach (var t in tools)
            {
                var tool = new Dictionary<string, object>();
                tool["name"] = t.Name;
                tool["description"] = t.Description;
                tool["input_schema"] = Serializer.DeserializeObject(t.JsonSchema);
                res.Add(tool);
            }
            return res;
        }

        public static List<object> SerialiseOpenAiHistory(string text, List<NativeToolCall> calls, List<NativeToolResult> results)
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
            
            if (results != null)
            {
                foreach (var r in results)
                {
                    var toolMsg = new Dictionary<string, object>();
                    toolMsg["role"] = "tool";
                    toolMsg["tool_call_id"] = r.CallId;
                    toolMsg["content"] = r.Content;
                    res.Add(toolMsg);
                }
            }
            
            return res;
        }

        public static List<object> SerialiseAnthropicHistory(string text, List<NativeToolCall> calls, List<NativeToolResult> results)
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
                tu["input"] = c.GetArguments(); 
                content.Add(tu);
            }
            
            var assistantMsg = new Dictionary<string, object>();
            assistantMsg["role"] = "assistant";
            assistantMsg["content"] = content;
            res.Add(assistantMsg);
            
            if (results != null && results.Count > 0)
            {
                var userContent = new List<object>();
                foreach (var r in results)
                {
                    var tr = new Dictionary<string, object>();
                    tr["type"] = "tool_result";
                    tr["tool_use_id"] = r.CallId;
                    tr["content"] = r.Content;
                    tr["is_error"] = r.IsError;
                    userContent.Add(tr);
                }
                
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
