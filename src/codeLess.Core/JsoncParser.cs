using System;
using System.Collections.Generic;
using System.Text;
using System.Web.Script.Serialization;

namespace codeLess.Core
{
    public static class JsoncParser
    {
        public static object Parse(string jsonc, out string errorMessage)
        {
            errorMessage = null;
            if (string.IsNullOrEmpty(jsonc)) return null;

            string cleanJson = StripJsonc(jsonc);
            string trimmed = cleanJson != null ? cleanJson.Trim() : "";
            if (trimmed.Length == 0 ||
                (trimmed[0] == '{' && trimmed[trimmed.Length - 1] != '}') ||
                (trimmed[0] == '[' && trimmed[trimmed.Length - 1] != ']'))
            {
                errorMessage = "JSON parse error: unexpected end of JSON input";
                return null;
            }
            var serializer = new JavaScriptSerializer();
            serializer.MaxJsonLength = int.MaxValue;
            try
            {
                object parsed = serializer.Deserialize<object>(cleanJson);
                var dict = parsed as Dictionary<string, object>;
                if (dict != null && dict.ContainsKey(""))
                {
                    errorMessage = "JSON parse error: expected quoted property name";
                    return null;
                }
                return parsed;
            }
            catch (ArgumentException ex)
            {
                // JavaScriptSerializer throws ArgumentException for parse errors.
                // We'll just return the exception message. Real line number extraction
                // would require custom parsing, but we can do a best effort.
                errorMessage = "JSON parse error: " + ex.Message;
                return null;
            }
            catch (Exception ex)
            {
                errorMessage = "JSON error: " + ex.Message;
                return null;
            }
        }

        public static string StripJsonc(string input)
        {
            if (string.IsNullOrEmpty(input)) return input;
            
            var sb = new StringBuilder(input.Length);
            bool inString = false;
            bool escape = false;
            int i = 0;
            
            while (i < input.Length)
            {
                char c = input[i];
                
                if (inString)
                {
                    sb.Append(c);
                    if (escape) escape = false;
                    else if (c == '\\') escape = true;
                    else if (c == '"') inString = false;
                    i++;
                }
                else
                {
                    if (c == '"')
                    {
                        inString = true;
                        sb.Append(c);
                        i++;
                    }
                    else if (c == '/' && i + 1 < input.Length)
                    {
                        if (input[i + 1] == '/')
                        {
                            // line comment
                            i += 2;
                            while (i < input.Length && input[i] != '\n')
                            {
                                sb.Append(' '); // keep length / newlines for error offsets
                                i++;
                            }
                        }
                        else if (input[i + 1] == '*')
                        {
                            // block comment
                            sb.Append(' ');
                            sb.Append(' ');
                            i += 2;
                            while (i + 1 < input.Length && !(input[i] == '*' && input[i + 1] == '/'))
                            {
                                if (input[i] == '\n') sb.Append('\n');
                                else sb.Append(' ');
                                i++;
                            }
                            if (i + 1 < input.Length)
                            {
                                sb.Append(' ');
                                sb.Append(' ');
                                i += 2;
                            }
                        }
                        else
                        {
                            sb.Append(c);
                            i++;
                        }
                    }
                    else
                    {
                        sb.Append(c);
                        i++;
                    }
                }
            }
            
            // Remove trailing commas (lazy approach: replace commas that are followed only by whitespace and closing bracket/brace)
            string result = sb.ToString();
            // A quick loop backwards to remove trailing commas while keeping length matching is hard.
            // We can just use a simple regex for trailing commas since it's already stripped of comments.
            // But wait, strings can contain `, }`.
            // Let's do a quick pass.
            sb.Clear();
            inString = false;
            escape = false;
            
            int lastCommaIndex = -1;
            
            for (i = 0; i < result.Length; i++)
            {
                char c = result[i];
                if (inString)
                {
                    if (escape) escape = false;
                    else if (c == '\\') escape = true;
                    else if (c == '"') inString = false;
                }
                else
                {
                    if (c == '"') inString = true;
                    else if (c == ',')
                    {
                        lastCommaIndex = i;
                    }
                    else if (!char.IsWhiteSpace(c))
                    {
                        if (c == ']' || c == '}')
                        {
                            if (lastCommaIndex != -1)
                            {
                                // it's a trailing comma. Replace it with a space in the result array
                                var arr = result.ToCharArray();
                                arr[lastCommaIndex] = ' ';
                                result = new string(arr);
                            }
                        }
                        lastCommaIndex = -1;
                    }
                }
            }
            
            return result;
        }
    }
}
