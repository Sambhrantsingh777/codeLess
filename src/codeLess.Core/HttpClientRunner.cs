using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace codeLess.Studio
{
    /// <summary>
    /// Sends resolved .http requests. Every request goes through <see cref="NetworkLedger"/> (feature "HTTP Client"):
    /// Offline Mode blocks non-loopback hosts. Async, cancellable, with a timeout. Shared by both UIs (C# 5).
    /// </summary>
    public static class HttpClientRunner
    {
        public const string LedgerFeature = "HTTP Client";
        public const int DefaultTimeoutMs = 30000;
        /// <summary>Bodies larger than this are truncated for display (the size shown is still the real size).</summary>
        public const int MaxDisplayBodyBytes = 8 * 1024 * 1024;

        private static readonly string[] ContentHeaderNames = new string[]
        {
            "Content-Type", "Content-Length", "Content-Encoding", "Content-Language", "Content-Location",
            "Content-MD5", "Content-Range", "Content-Disposition", "Expires", "Last-Modified", "Allow"
        };

        private static bool IsContentHeader(string name)
        {
            foreach (string h in ContentHeaderNames) if (string.Equals(h, name, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        public static async Task<HttpResponseResult> SendAsync(HttpResolvedRequest req, int timeoutMs, CancellationToken userToken)
        {
            HttpResponseResult result = new HttpResponseResult();
            result.Request = req;
            string url = req.Url ?? "";
            Uri uri;
            if (!Uri.TryCreate(url, UriKind.Absolute, out uri) || (uri.Scheme != "http" && uri.Scheme != "https"))
            {
                result.Error = "Invalid URL \"" + url + "\": only absolute http:// and https:// URLs can be sent.";
                return result;
            }
            string reason;
            if (!NetworkLedger.Check(LedgerFeature, req.Method, url, out reason))
            {
                result.Blocked = true;
                result.Error = reason;
                return result;
            }

            Stopwatch sw = Stopwatch.StartNew();
            long sent = 0;
            HttpClientHandler handler = new HttpClientHandler();
            handler.AllowAutoRedirect = !req.NoRedirect;
            handler.UseCookies = false;
            try { handler.AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate; } catch { }
            using (CancellationTokenSource timeoutCts = new CancellationTokenSource())
            using (CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(userToken, timeoutCts.Token))
            using (HttpClient client = new HttpClient(handler))
            {
                client.Timeout = System.Threading.Timeout.InfiniteTimeSpan;
                timeoutCts.CancelAfter(Math.Max(1000, timeoutMs));
                HttpRequestMessage msg = new HttpRequestMessage(new HttpMethod(req.Method ?? "GET"), uri);
                string contentType = null;
                List<HttpHeader> contentHeaders = new List<HttpHeader>();
                foreach (HttpHeader h in req.Headers)
                {
                    if (string.Equals(h.Name, "Content-Type", StringComparison.OrdinalIgnoreCase)) { contentType = h.Value; continue; }
                    if (string.Equals(h.Name, "Content-Length", StringComparison.OrdinalIgnoreCase)) continue;
                    if (string.Equals(h.Name, "Host", StringComparison.OrdinalIgnoreCase)) continue;
                    if (IsContentHeader(h.Name)) { contentHeaders.Add(h); continue; }
                    if (!msg.Headers.TryAddWithoutValidation(h.Name, h.Value)) contentHeaders.Add(h);
                    sent += h.Name.Length + h.Value.Length + 4;
                }
                if (!string.IsNullOrEmpty(req.Body) || contentType != null)
                {
                    byte[] bodyBytes = Encoding.UTF8.GetBytes(req.Body ?? "");
                    sent += bodyBytes.Length;
                    ByteArrayContent content = new ByteArrayContent(bodyBytes);
                    if (contentType != null) content.Headers.TryAddWithoutValidation("Content-Type", contentType);
                    foreach (HttpHeader ch in contentHeaders) content.Headers.TryAddWithoutValidation(ch.Name, ch.Value);
                    msg.Content = content;
                }
                sent += url.Length + 16;
                try
                {
                    using (HttpResponseMessage resp = await client.SendAsync(msg, HttpCompletionOption.ResponseHeadersRead, linked.Token).ConfigureAwait(false))
                    {
                        result.StatusCode = (int)resp.StatusCode;
                        result.ReasonPhrase = resp.ReasonPhrase ?? "";
                        result.HttpVersion = "HTTP/" + resp.Version.ToString();
                        foreach (KeyValuePair<string, IEnumerable<string>> kv in resp.Headers)
                            result.Headers.Add(new HttpHeader(kv.Key, string.Join(", ", kv.Value)));
                        byte[] data = new byte[0];
                        if (resp.Content != null)
                        {
                            foreach (KeyValuePair<string, IEnumerable<string>> kv in resp.Content.Headers)
                                result.Headers.Add(new HttpHeader(kv.Key, string.Join(", ", kv.Value)));
                            using (Stream s = await resp.Content.ReadAsStreamAsync().ConfigureAwait(false))
                            using (MemoryStream ms = new MemoryStream())
                            {
                                byte[] buf = new byte[81920];
                                long total = 0;
                                int n;
                                while ((n = await s.ReadAsync(buf, 0, buf.Length, linked.Token).ConfigureAwait(false)) > 0)
                                {
                                    total += n;
                                    if (ms.Length < MaxDisplayBodyBytes) ms.Write(buf, 0, (int)Math.Min(n, MaxDisplayBodyBytes - ms.Length));
                                }
                                result.BodyBytes = total;
                                data = ms.ToArray();
                            }
                            if (resp.Content.Headers.ContentType != null)
                                result.ContentType = resp.Content.Headers.ContentType.ToString();
                        }
                        result.Body = DecodeBody(data, result.ContentType);
                        if (result.BodyBytes > data.Length)
                            result.Body += "\n\n[codeLess: response truncated for display at " + (MaxDisplayBodyBytes / (1024 * 1024)) + " MB]";
                    }
                }
                catch (OperationCanceledException)
                {
                    if (userToken.IsCancellationRequested) { result.Cancelled = true; result.Error = "Request cancelled."; }
                    else result.Error = "Request timed out after " + (Math.Max(1000, timeoutMs) / 1000.0).ToString("0.#", CultureInfo.InvariantCulture) + " s.";
                }
                catch (HttpRequestException ex)
                {
                    result.Error = DescribeException(ex);
                }
                catch (Exception ex)
                {
                    result.Error = DescribeException(ex);
                }
            }
            sw.Stop();
            result.ElapsedMs = sw.ElapsedMilliseconds;
            NetworkLedger.Record(LedgerFeature, req.Method, url, sent, result.BodyBytes, result.StatusCode, result.Error ?? "");
            return result;
        }

        private static string DescribeException(Exception ex)
        {
            Exception e = ex;
            string msg = ex.Message;
            while (e.InnerException != null) { e = e.InnerException; msg = e.Message; }
            return "Request failed: " + msg;
        }

        public static string DecodeBody(byte[] data, string contentType)
        {
            if (data == null || data.Length == 0) return "";
            Encoding enc = new UTF8Encoding(false);
            string ct = (contentType ?? "").ToLowerInvariant();
            int cs = ct.IndexOf("charset=", StringComparison.Ordinal);
            if (cs >= 0)
            {
                string name = ct.Substring(cs + 8).Trim().Trim('"', '\'');
                int semi = name.IndexOf(';');
                if (semi >= 0) name = name.Substring(0, semi);
                try { enc = Encoding.GetEncoding(name); } catch { }
            }
            int skip = 0;
            if (data.Length >= 3 && data[0] == 0xEF && data[1] == 0xBB && data[2] == 0xBF) skip = 3;
            if (IsLikelyBinary(data, ct))
                return "[binary response: " + data.Length.ToString(CultureInfo.InvariantCulture) + " bytes, " + (string.IsNullOrEmpty(contentType) ? "unknown type" : contentType) + "]";
            return enc.GetString(data, skip, data.Length - skip);
        }

        private static bool IsLikelyBinary(byte[] data, string ct)
        {
            if (ct.StartsWith("image/") || ct.StartsWith("audio/") || ct.StartsWith("video/") || ct.Contains("octet-stream") || ct.Contains("zip") || ct.Contains("pdf")) return true;
            int n = Math.Min(data.Length, 4096);
            for (int i = 0; i < n; i++) if (data[i] == 0) return true;
            return false;
        }

        /// <summary>bash-style curl command for the request.</summary>
        public static string ToCurl(HttpResolvedRequest req)
        {
            StringBuilder sb = new StringBuilder("curl");
            if (!string.Equals(req.Method, "GET", StringComparison.OrdinalIgnoreCase) || !string.IsNullOrEmpty(req.Body))
                sb.Append(" -X ").Append(req.Method.ToUpperInvariant());
            sb.Append(' ').Append(ShellQuote(req.Url));
            foreach (HttpHeader h in req.Headers)
                sb.Append(" \\\n  -H ").Append(ShellQuote(h.Name + ": " + h.Value));
            if (!string.IsNullOrEmpty(req.Body))
                sb.Append(" \\\n  --data-raw ").Append(ShellQuote(req.Body));
            if (req.NoRedirect) { } else sb.Append(" \\\n  -L");
            return sb.ToString();
        }

        public static string ShellQuote(string s)
        {
            return "'" + (s ?? "").Replace("'", "'\\''") + "'";
        }

        public static string FormatSize(long bytes)
        {
            if (bytes < 1024) return bytes.ToString(CultureInfo.InvariantCulture) + " B";
            if (bytes < 1024 * 1024) return (bytes / 1024.0).ToString("0.##", CultureInfo.InvariantCulture) + " KB";
            return (bytes / (1024.0 * 1024.0)).ToString("0.##", CultureInfo.InvariantCulture) + " MB";
        }

        public static string StatusSummary(HttpResponseResult r)
        {
            if (r == null) return "";
            if (!r.HasResponse) return r.Error;
            return r.StatusCode.ToString(CultureInfo.InvariantCulture) + " " + r.ReasonPhrase + "  ·  " + r.ElapsedMs.ToString(CultureInfo.InvariantCulture) + " ms  ·  " + FormatSize(r.BodyBytes);
        }

        /// <summary>Raw response head ("HTTP/1.1 200 OK" + headers) for the Headers tab.</summary>
        public static string FormatHeaders(HttpResponseResult r)
        {
            StringBuilder sb = new StringBuilder();
            sb.Append(r.HttpVersion).Append(' ').Append(r.StatusCode.ToString(CultureInfo.InvariantCulture)).Append(' ').Append(r.ReasonPhrase).Append('\n');
            foreach (HttpHeader h in r.Headers) sb.Append(h.Name).Append(": ").Append(h.Value).Append('\n');
            return sb.ToString();
        }
    }

    /// <summary>Token kinds for colored response bodies (mapped to theme colors by each UI).</summary>
    public enum BodyTokenKind
    {
        Text = 0,
        Key = 1,
        String = 2,
        Number = 3,
        Literal = 4,
        Punctuation = 5,
        Tag = 6,
        AttributeName = 7,
        Comment = 8
    }

    public class BodyToken
    {
        public int Start;
        public int Length;
        public BodyTokenKind Kind;

        public BodyToken(int start, int length, BodyTokenKind kind) { Start = start; Length = length; Kind = kind; }
    }

    /// <summary>Pretty printing and tokenizing of JSON / XML response bodies (no external libraries; C# 5).</summary>
    public static class BodyFormatter
    {
        public static bool LooksLikeJson(string contentType, string body)
        {
            string ct = (contentType ?? "").ToLowerInvariant();
            if (ct.Contains("json")) return true;
            string t = (body ?? "").TrimStart();
            return t.StartsWith("{") || t.StartsWith("[");
        }

        public static bool LooksLikeXml(string contentType, string body)
        {
            string ct = (contentType ?? "").ToLowerInvariant();
            if (ct.Contains("xml") || ct.Contains("html")) return true;
            return (body ?? "").TrimStart().StartsWith("<");
        }

        /// <summary>Re-indents JSON with 2 spaces, preserving string / number text exactly. Invalid JSON is returned unchanged.</summary>
        public static string PrettyJson(string json)
        {
            if (string.IsNullOrEmpty(json)) return json ?? "";
            StringBuilder sb = new StringBuilder(json.Length + json.Length / 4);
            int indent = 0;
            bool inStr = false;
            int depthCheck = 0;
            for (int i = 0; i < json.Length; i++)
            {
                char c = json[i];
                if (inStr)
                {
                    sb.Append(c);
                    if (c == '\\' && i + 1 < json.Length) { sb.Append(json[i + 1]); i++; }
                    else if (c == '"') inStr = false;
                    continue;
                }
                switch (c)
                {
                    case '"': inStr = true; sb.Append(c); break;
                    case '{':
                    case '[':
                        {
                            depthCheck++;
                            // Keep empty {} / [] on one line.
                            int j = i + 1;
                            while (j < json.Length && char.IsWhiteSpace(json[j])) j++;
                            char closer = c == '{' ? '}' : ']';
                            if (j < json.Length && json[j] == closer) { sb.Append(c).Append(closer); i = j; depthCheck--; break; }
                            indent++;
                            sb.Append(c).Append('\n').Append(' ', indent * 2);
                            break;
                        }
                    case '}':
                    case ']':
                        depthCheck--;
                        indent = Math.Max(0, indent - 1);
                        sb.Append('\n').Append(' ', indent * 2).Append(c);
                        break;
                    case ',':
                        sb.Append(c).Append('\n').Append(' ', indent * 2);
                        break;
                    case ':':
                        sb.Append(": ");
                        break;
                    default:
                        if (!char.IsWhiteSpace(c)) sb.Append(c);
                        break;
                }
            }
            if (inStr || depthCheck != 0) return json;
            return sb.ToString();
        }

        /// <summary>Indents XML/HTML by tag nesting (text kept on the line of its element when short).</summary>
        public static string PrettyXml(string xml)
        {
            if (string.IsNullOrEmpty(xml)) return xml ?? "";
            string src = xml.Trim();
            if (!src.StartsWith("<")) return xml;
            StringBuilder sb = new StringBuilder(src.Length + src.Length / 4);
            int indent = 0;
            int i = 0;
            bool lastWasOpenWithText = false;
            while (i < src.Length)
            {
                if (src[i] == '<')
                {
                    int end;
                    if (string.CompareOrdinal(src, i, "<!--", 0, 4) == 0) { end = src.IndexOf("-->", i, StringComparison.Ordinal); end = end < 0 ? src.Length - 1 : end + 2; }
                    else if (string.CompareOrdinal(src, i, "<![CDATA[", 0, 9) == 0) { end = src.IndexOf("]]>", i, StringComparison.Ordinal); end = end < 0 ? src.Length - 1 : end + 2; }
                    else { end = src.IndexOf('>', i); if (end < 0) end = src.Length - 1; }
                    string tag = src.Substring(i, end - i + 1);
                    bool closing = tag.StartsWith("</");
                    bool selfClosing = tag.EndsWith("/>") || tag.StartsWith("<?") || tag.StartsWith("<!");
                    if (closing)
                    {
                        indent = Math.Max(0, indent - 1);
                        if (!lastWasOpenWithText) { if (sb.Length > 0) sb.Append('\n'); sb.Append(' ', indent * 2); }
                        sb.Append(tag);
                        lastWasOpenWithText = false;
                    }
                    else
                    {
                        if (sb.Length > 0) sb.Append('\n');
                        sb.Append(' ', indent * 2).Append(tag);
                        if (!selfClosing && !IsVoidHtmlTag(tag)) indent++;
                        lastWasOpenWithText = false;
                    }
                    i = end + 1;
                }
                else
                {
                    int next = src.IndexOf('<', i);
                    if (next < 0) next = src.Length;
                    string text = src.Substring(i, next - i).Trim();
                    if (text.Length > 0)
                    {
                        bool closesNext = next < src.Length - 1 && src[next + 1] == '/';
                        if (closesNext && text.Length <= 120 && text.IndexOf('\n') < 0)
                        {
                            sb.Append(text);
                            lastWasOpenWithText = true;
                        }
                        else
                        {
                            sb.Append('\n').Append(' ', indent * 2).Append(text);
                            lastWasOpenWithText = false;
                        }
                    }
                    i = next;
                }
            }
            return sb.ToString();
        }

        private static bool IsVoidHtmlTag(string tag)
        {
            string t = tag.TrimStart('<').ToLowerInvariant();
            string[] voids = new string[] { "br", "hr", "img", "input", "meta", "link", "area", "base", "col", "embed", "source", "track", "wbr" };
            foreach (string v in voids)
            {
                if (t.StartsWith(v) && (t.Length == v.Length || !char.IsLetterOrDigit(t[v.Length]))) return true;
            }
            return false;
        }

        public static List<BodyToken> TokenizeJson(string text)
        {
            List<BodyToken> list = new List<BodyToken>();
            if (string.IsNullOrEmpty(text)) return list;
            int i = 0, n = text.Length;
            while (i < n)
            {
                char c = text[i];
                if (c == '"')
                {
                    int s = i++;
                    while (i < n && text[i] != '"' && text[i] != '\n') { if (text[i] == '\\') i++; i++; }
                    if (i < n && text[i] == '"') i++;
                    int j = i;
                    while (j < n && (text[j] == ' ' || text[j] == '\t')) j++;
                    list.Add(new BodyToken(s, i - s, j < n && text[j] == ':' ? BodyTokenKind.Key : BodyTokenKind.String));
                    continue;
                }
                if (c == '-' || char.IsDigit(c))
                {
                    int s = i++;
                    while (i < n && (char.IsDigit(text[i]) || text[i] == '.' || text[i] == 'e' || text[i] == 'E' || text[i] == '+' || text[i] == '-')) i++;
                    list.Add(new BodyToken(s, i - s, BodyTokenKind.Number));
                    continue;
                }
                if (char.IsLetter(c))
                {
                    int s = i;
                    while (i < n && char.IsLetter(text[i])) i++;
                    string w = text.Substring(s, i - s);
                    list.Add(new BodyToken(s, i - s, (w == "true" || w == "false" || w == "null") ? BodyTokenKind.Literal : BodyTokenKind.Text));
                    continue;
                }
                if (c == '{' || c == '}' || c == '[' || c == ']' || c == ',' || c == ':')
                {
                    list.Add(new BodyToken(i, 1, BodyTokenKind.Punctuation));
                    i++;
                    continue;
                }
                i++;
            }
            return list;
        }

        public static List<BodyToken> TokenizeXml(string text)
        {
            List<BodyToken> list = new List<BodyToken>();
            if (string.IsNullOrEmpty(text)) return list;
            int i = 0, n = text.Length;
            while (i < n)
            {
                if (text[i] == '<')
                {
                    if (string.CompareOrdinal(text, i, "<!--", 0, 4) == 0)
                    {
                        int e = text.IndexOf("-->", i, StringComparison.Ordinal);
                        e = e < 0 ? n : e + 3;
                        list.Add(new BodyToken(i, e - i, BodyTokenKind.Comment));
                        i = e;
                        continue;
                    }
                    int s = i;
                    i++;
                    if (i < n && (text[i] == '/' || text[i] == '?' || text[i] == '!')) i++;
                    while (i < n && !char.IsWhiteSpace(text[i]) && text[i] != '>' && text[i] != '/') i++;
                    list.Add(new BodyToken(s, i - s, BodyTokenKind.Tag));
                    // Attributes until '>'.
                    while (i < n && text[i] != '>')
                    {
                        char c = text[i];
                        if (c == '"' || c == '\'')
                        {
                            int qs = i++;
                            while (i < n && text[i] != c) i++;
                            if (i < n) i++;
                            list.Add(new BodyToken(qs, i - qs, BodyTokenKind.String));
                            continue;
                        }
                        if (char.IsLetter(c) || c == '_' || c == ':')
                        {
                            int a = i;
                            while (i < n && (char.IsLetterOrDigit(text[i]) || text[i] == '_' || text[i] == ':' || text[i] == '-' || text[i] == '.')) i++;
                            list.Add(new BodyToken(a, i - a, BodyTokenKind.AttributeName));
                            continue;
                        }
                        i++;
                    }
                    if (i < n)
                    {
                        int closeStart = (i > 0 && (text[i - 1] == '/' || text[i - 1] == '?')) ? i - 1 : i;
                        list.Add(new BodyToken(closeStart, i - closeStart + 1, BodyTokenKind.Tag));
                        i++;
                    }
                    continue;
                }
                i++;
            }
            return list;
        }

        /// <summary>Pretty text + tokens for a response body based on its content type.</summary>
        public static string FormatForDisplay(string contentType, string body, out List<BodyToken> tokens)
        {
            tokens = new List<BodyToken>();
            if (string.IsNullOrEmpty(body)) return body ?? "";
            if (LooksLikeJson(contentType, body))
            {
                string pretty = PrettyJson(body);
                if (pretty.Length <= 2 * 1024 * 1024) tokens = TokenizeJson(pretty);
                return pretty;
            }
            if (LooksLikeXml(contentType, body))
            {
                string pretty = (contentType ?? "").ToLowerInvariant().Contains("html") ? body : PrettyXml(body);
                if (pretty.Length <= 2 * 1024 * 1024) tokens = TokenizeXml(pretty);
                return pretty;
            }
            return body;
        }
    }
}
