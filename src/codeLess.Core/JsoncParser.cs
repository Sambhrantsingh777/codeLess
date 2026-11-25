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

            bool hadUnterminatedString;
            string cleanJson = StripJsoncInternal(jsonc, out hadUnterminatedString);
            string syntaxErr = DetectJsonSyntaxError(cleanJson, hadUnterminatedString);
            if (syntaxErr != null)
                errorMessage = syntaxErr;

            var serializer = new JavaScriptSerializer();
            serializer.MaxJsonLength = int.MaxValue;
            try
            {
                object parsed = serializer.Deserialize<object>(cleanJson);
                var dict = parsed as Dictionary<string, object>;
                if (dict != null && dict.ContainsKey(""))
                {
                    if (errorMessage == null)
                        errorMessage = "JSON parse error: expected quoted property name";
                    dict.Remove("");
                }
                return parsed;
            }
            catch (ArgumentException ex)
            {
                if (errorMessage == null)
                    errorMessage = "JSON parse error: " + ex.Message;
                return TryRecoverPartialObject(cleanJson);
            }
            catch (Exception ex)
            {
                if (errorMessage == null)
                    errorMessage = "JSON error: " + ex.Message;
                return TryRecoverPartialObject(cleanJson);
            }
        }

        private static Dictionary<string, object> TryRecoverPartialObject(string cleanJson)
        {
            var dict = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
            if (string.IsNullOrEmpty(cleanJson)) return dict;
            var rx = new System.Text.RegularExpressions.Regex("\"((?:[^\"\\\\]|\\\\.)*)\"\\s*:\\s*(\"((?:[^\"\\\\]|\\\\.)*)\"|true|false|null|-?\\d+(?:\\.\\d+)?)");
            var matches = rx.Matches(cleanJson);
            foreach (System.Text.RegularExpressions.Match m in matches)
            {
                string key = m.Groups[1].Value;
                string rawVal = m.Groups[2].Value;
                if (m.Groups[3].Success) dict[key] = m.Groups[3].Value;
                else if (string.Equals(rawVal, "true", StringComparison.OrdinalIgnoreCase)) dict[key] = true;
                else if (string.Equals(rawVal, "false", StringComparison.OrdinalIgnoreCase)) dict[key] = false;
                else if (string.Equals(rawVal, "null", StringComparison.OrdinalIgnoreCase)) dict[key] = null;
                else
                {
                    int iv;
                    double dv;
                    if (int.TryParse(rawVal, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out iv))
                        dict[key] = iv;
                    else if (double.TryParse(rawVal, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out dv))
                        dict[key] = dv;
                }
            }
            return dict;
        }

        private static string DetectJsonSyntaxError(string cleanJson, bool hadUnterminatedString)
        {
            if (hadUnterminatedString)
                return "JSON parse error: unterminated string literal";
            if (string.IsNullOrEmpty(cleanJson))
                return "JSON parse error: empty JSON input";

            string trimmed = cleanJson.Trim();
            if (trimmed.Length == 0)
                return "JSON parse error: empty JSON input";

            int braceDepth = 0;
            int bracketDepth = 0;
            bool inStr = false;
            bool esc = false;
            char prevSig = '\0';
            for (int i = 0; i < cleanJson.Length; i++)
            {
                char c = cleanJson[i];
                if (inStr)
                {
                    if (esc) esc = false;
                    else if (c == '\\') esc = true;
                    else if (c == '"') { inStr = false; prevSig = '"'; }
                    continue;
                }
                if (char.IsWhiteSpace(c)) continue;
                if (c == '"')
                {
                    inStr = true;
                    continue;
                }
                if (c == ',')
                {
                    if (prevSig == '{' || prevSig == '[' || prevSig == ',' || prevSig == ':')
                        return "JSON parse error: unexpected comma";
                }
                if (c == '{') braceDepth++;
                else if (c == '}')
                {
                    braceDepth--;
                    if (braceDepth < 0) return "JSON parse error: unexpected '}'";
                }
                else if (c == '[') bracketDepth++;
                else if (c == ']')
                {
                    bracketDepth--;
                    if (bracketDepth < 0) return "JSON parse error: unexpected ']'";
                }
                prevSig = c;
            }
            if (inStr) return "JSON parse error: unterminated string literal";
            if (braceDepth != 0) return "JSON parse error: unexpected end of JSON input (unclosed '{')";
            if (bracketDepth != 0) return "JSON parse error: unexpected end of JSON input (unclosed '[')";
            return null;
        }

        public static string StripJsonc(string input)
        {
            bool unused;
            return StripJsoncInternal(input, out unused);
        }

        private static string StripJsoncInternal(string input, out bool hadUnterminatedString)
        {
            hadUnterminatedString = false;
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
                    if (c == '\n' || c == '\r')
                    {
                        hadUnterminatedString = true;
                        inString = false;
                        escape = false;
                        sb.Append('"');
                        sb.Append(c);
                        i++;
                        continue;
                    }
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
            if (inString)
            {
                hadUnterminatedString = true;
                sb.Append('"');
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
