using System;
using System.Collections.Generic;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace codeLess.Studio
{
    /// <summary>
    /// M7d webfetch tool (derived from opencode src/tool/webfetch.ts, MIT).
    /// Routes all HTTP(S) requests exclusively through <see cref="LedgerHttp"/> / <see cref="NetworkLedger"/>.
    /// Strict C# 5 / net48 compatible.
    /// </summary>
    public static class WebFetchTool
    {
        public const string LedgerFeature = "Agent: webfetch";
        public const long MaxResponseBytes = 5L * 1024 * 1024; // 5 MB
        public const int DefaultTimeoutSeconds = 30;
        public const int MaxTimeoutSeconds = 120;

        public static AgentToolCallResult Execute(Dictionary<string, object> args)
        {
            if (args == null) args = new Dictionary<string, object>();

            string url = args.ContainsKey("url") && args["url"] != null ? Convert.ToString(args["url"]).Trim() : "";
            if (string.IsNullOrEmpty(url))
            {
                return new AgentToolCallResult
                {
                    ToolName = "webfetch",
                    Success = false,
                    Summary = "Missing URL",
                    Output = "Missing required 'url' argument."
                };
            }

            Uri uri;
            if (!Uri.TryCreate(url, UriKind.Absolute, out uri) ||
                (!string.Equals(uri.Scheme, "http", StringComparison.OrdinalIgnoreCase) &&
                 !string.Equals(uri.Scheme, "https", StringComparison.OrdinalIgnoreCase)))
            {
                return new AgentToolCallResult
                {
                    ToolName = "webfetch",
                    Success = false,
                    Summary = "Invalid URL scheme",
                    Output = "URL must start with http:// or https://"
                };
            }

            string format = args.ContainsKey("format") && args["format"] != null
                ? Convert.ToString(args["format"]).Trim().ToLowerInvariant()
                : "markdown";
            if (string.IsNullOrEmpty(format)) format = "markdown";

            if (format != "text" && format != "markdown" && format != "html")
            {
                return new AgentToolCallResult
                {
                    ToolName = "webfetch",
                    Success = false,
                    Summary = "Invalid format: " + format,
                    Output = "Invalid format '" + format + "'. Must be one of: text, markdown, html."
                };
            }

            int timeoutSec = DefaultTimeoutSeconds;
            if (args.ContainsKey("timeout") && args["timeout"] != null)
            {
                try
                {
                    int parsed = Convert.ToInt32(args["timeout"]);
                    if (parsed > 0) timeoutSec = Math.Min(MaxTimeoutSeconds, parsed);
                }
                catch { }
            }
            int timeoutMs = Math.Max(1000, Math.Min(MaxTimeoutSeconds * 1000, timeoutSec * 1000));

            LedgerHttpRequest req = new LedgerHttpRequest();
            req.Feature = LedgerFeature;
            req.Method = "GET";
            req.Url = uri.AbsoluteUri;
            req.TimeoutMs = timeoutMs;
            req.MaxResponseBytes = MaxResponseBytes;
            req.AllowCrossHostRedirects = false;
            req.UserAgent = "codeLess/3.3 (webfetch)";
            req.Accept = format == "html"
                ? "text/html,application/xhtml+xml,*/*;q=0.8"
                : "text/markdown,text/html,text/plain,*/*;q=0.8";

            LedgerHttpResponse res;
            try
            {
                res = LedgerHttp.Send(req);
            }
            catch (NetworkBlockedException nbex)
            {
                return new AgentToolCallResult
                {
                    ToolName = "webfetch",
                    Success = false,
                    Summary = "Blocked by Offline Mode",
                    Output = nbex.Message
                };
            }
            catch (Exception ex)
            {
                return new AgentToolCallResult
                {
                    ToolName = "webfetch",
                    Success = false,
                    Summary = "Request failed",
                    Output = "webfetch error: " + ex.Message
                };
            }

            if (res.Truncated)
            {
                return new AgentToolCallResult
                {
                    ToolName = "webfetch",
                    Success = false,
                    Summary = "Response exceeds 5 MB limit",
                    Output = "Response too large (exceeds 5MB limit)"
                };
            }

            if (!string.IsNullOrEmpty(res.Error))
            {
                return new AgentToolCallResult
                {
                    ToolName = "webfetch",
                    Success = false,
                    Summary = "Fetch failed: " + res.Error,
                    Output = "Request failed: " + res.Error
                };
            }

            if (!res.IsSuccess)
            {
                return new AgentToolCallResult
                {
                    ToolName = "webfetch",
                    Success = false,
                    Summary = "HTTP " + res.Status,
                    Output = "Request failed with status code: " + res.Status
                };
            }

            string body = res.Body ?? "";
            string contentType = (res.ContentType ?? "").ToLowerInvariant();
            bool isHtml = contentType.Contains("html") || LooksLikeHtml(body);

            string output;
            if (format == "html")
            {
                output = body;
            }
            else if (format == "text")
            {
                output = isHtml ? ConvertHtmlToText(body) : body;
            }
            else
            {
                output = isHtml ? ConvertHtmlToMarkdown(body) : body;
            }

            return new AgentToolCallResult
            {
                ToolName = "webfetch",
                Success = true,
                Summary = string.Format("Fetched {0} ({1} bytes)", uri.Host, res.BytesReceived),
                Output = output
            };
        }

        public static bool LooksLikeHtml(string text)
        {
            if (string.IsNullOrEmpty(text)) return false;
            return Regex.IsMatch(text, @"<\s*(?:!doctype\s+html|html|head|body|div|p|h[1-6]|article|main|section|span|a|ul|ol|table)\b", RegexOptions.IgnoreCase);
        }

        public static string ConvertHtmlToText(string html)
        {
            if (string.IsNullOrEmpty(html)) return "";
            string s = StripHiddenHtmlElements(html);
            s = Regex.Replace(s, @"<\s*br\s*/?\s*>", "\n", RegexOptions.IgnoreCase);
            s = Regex.Replace(s, @"</\s*(?:p|div|h[1-6]|li|tr|blockquote|section|article|header|footer|pre)\s*>", "\n", RegexOptions.IgnoreCase);
            s = Regex.Replace(s, @"<[^>]+>", " ");
            s = WebUtility.HtmlDecode(s);
            s = s.Replace("\r\n", "\n").Replace("\r", "\n");
            string[] rawLines = s.Split('\n');
            StringBuilder sb = new StringBuilder();
            int blankRun = 0;
            for (int i = 0; i < rawLines.Length; i++)
            {
                string cleaned = Regex.Replace(rawLines[i], @"[ \t\f\v]+", " ").Trim();
                if (cleaned.Length == 0)
                {
                    blankRun++;
                    if (blankRun <= 1 && sb.Length > 0) sb.Append('\n');
                }
                else
                {
                    blankRun = 0;
                    if (sb.Length > 0 && sb[sb.Length - 1] != '\n') sb.Append('\n');
                    sb.Append(cleaned);
                }
            }
            return sb.ToString().Trim();
        }

        public static string ConvertHtmlToMarkdown(string html)
        {
            if (string.IsNullOrEmpty(html)) return "";
            string s = StripHiddenHtmlElements(html);

            for (int level = 1; level <= 6; level++)
            {
                string hashes = new string('#', level);
                string pattern = @"<\s*h" + level + @"\b[^>]*>([\s\S]*?)</\s*h" + level + @"\s*>";
                s = Regex.Replace(s, pattern, m => "\n\n" + hashes + " " + StripTagsAndDecode(m.Groups[1].Value).Trim() + "\n\n", RegexOptions.IgnoreCase);
            }

            s = Regex.Replace(s, @"<\s*(?:strong|b)\b[^>]*>([\s\S]*?)</\s*(?:strong|b)\s*>",
                m => "**" + StripTagsAndDecode(m.Groups[1].Value).Trim() + "**",
                RegexOptions.IgnoreCase);

            s = Regex.Replace(s, @"<\s*(?:em|i)\b[^>]*>([\s\S]*?)</\s*(?:em|i)\s*>",
                m => "*" + StripTagsAndDecode(m.Groups[1].Value).Trim() + "*",
                RegexOptions.IgnoreCase);

            s = Regex.Replace(s, @"<\s*code\b[^>]*>([\s\S]*?)</\s*code\s*>",
                m => "`" + StripTagsAndDecode(m.Groups[1].Value).Trim() + "`",
                RegexOptions.IgnoreCase);

            s = Regex.Replace(s, @"<\s*a\b[^>]*\bhref\s*=\s*[""']([^""']+)[""'][^>]*>([\s\S]*?)</\s*a\s*>",
                m => "[" + StripTagsAndDecode(m.Groups[2].Value).Trim() + "](" + m.Groups[1].Value.Trim() + ")",
                RegexOptions.IgnoreCase);

            s = Regex.Replace(s, @"<\s*li\b[^>]*>([\s\S]*?)</\s*li\s*>",
                m => "\n- " + StripTagsAndDecode(m.Groups[1].Value).Trim(),
                RegexOptions.IgnoreCase);

            s = Regex.Replace(s, @"<\s*br\s*/?\s*>", "\n", RegexOptions.IgnoreCase);
            s = Regex.Replace(s, @"</\s*(?:p|div|section|article|blockquote|ul|ol|table|tr)\s*>", "\n\n", RegexOptions.IgnoreCase);
            s = Regex.Replace(s, @"<[^>]+>", "");
            s = WebUtility.HtmlDecode(s);
            s = s.Replace("\r\n", "\n").Replace("\r", "\n");
            s = Regex.Replace(s, @"[ \t]+\n", "\n");
            s = Regex.Replace(s, @"\n{3,}", "\n\n");
            return s.Trim();
        }

        private static string StripHiddenHtmlElements(string html)
        {
            string s = Regex.Replace(html, @"<!--[\s\S]*?-->", "", RegexOptions.IgnoreCase);
            s = Regex.Replace(s, @"<\s*script\b[^>]*>[\s\S]*?</\s*script\s*>", "", RegexOptions.IgnoreCase);
            s = Regex.Replace(s, @"<\s*style\b[^>]*>[\s\S]*?</\s*style\s*>", "", RegexOptions.IgnoreCase);
            s = Regex.Replace(s, @"<\s*noscript\b[^>]*>[\s\S]*?</\s*noscript\s*>", "", RegexOptions.IgnoreCase);
            s = Regex.Replace(s, @"<\s*head\b[^>]*>[\s\S]*?</\s*head\s*>", "", RegexOptions.IgnoreCase);
            return s;
        }

        private static string StripTagsAndDecode(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            return WebUtility.HtmlDecode(Regex.Replace(s, @"<[^>]+>", ""));
        }
    }
}
