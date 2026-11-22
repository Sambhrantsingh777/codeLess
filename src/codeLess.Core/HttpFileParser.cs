using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace codeLess.Studio
{
    public class HttpHeader
    {
        public string Name = "";
        public string Value = "";

        public HttpHeader() { }
        public HttpHeader(string name, string value) { Name = name; Value = value; }
    }

    /// <summary>One request block of a .http / .rest file (REST Client compatible syntax), before variable substitution.</summary>
    public class HttpRequestBlock
    {
        /// <summary>0-based first line of the block (line after "###" or file start).</summary>
        public int BlockStartLine;
        /// <summary>0-based last line of the block (inclusive).</summary>
        public int BlockEndLine;
        /// <summary>0-based line of "METHOD URL"; the "Send Request" lens is attached here.</summary>
        public int RequestLine = -1;
        public string Name = "";
        public string Title = "";
        public string Method = "GET";
        public string Url = "";
        public string HttpVersion = "";
        public List<HttpHeader> Headers = new List<HttpHeader>();
        public string Body = "";
        /// <summary>"&lt; path" body reference (relative to the .http file), empty when the body is inline.</summary>
        public string BodyFilePath = "";
        public bool BodyFileProcessVariables;
        public bool NoRedirect;
    }

    public class HttpFileDocument
    {
        public List<HttpRequestBlock> Requests = new List<HttpRequestBlock>();
        /// <summary>File variables in definition order ("@name = value"); later definitions win.</summary>
        public Dictionary<string, string> FileVariables = new Dictionary<string, string>(StringComparer.Ordinal);
    }

    /// <summary>Parser for REST Client / JetBrains HTTP Client style request files.</summary>
    public static class HttpFileParser
    {
        private static readonly string[] Methods = new string[] { "GET", "POST", "PUT", "PATCH", "DELETE", "HEAD", "OPTIONS", "TRACE", "CONNECT" };
        private static readonly Regex VarDefRegex = new Regex(@"^\s*@([A-Za-z_][\w.\-]*)\s*=\s*(.*?)\s*$", RegexOptions.Compiled);
        private static readonly Regex MetaRegex = new Regex(@"^\s*(?:#|//)\s*@([\w\-]+)(?:\s+(.*?))?\s*$", RegexOptions.Compiled);
        private static readonly Regex HeaderRegex = new Regex(@"^\s*([!#$%&'*+.^_`|~0-9A-Za-z\-]+)\s*:\s*(.*?)\s*$", RegexOptions.Compiled);

        public static bool IsHttpFile(string path)
        {
            if (string.IsNullOrEmpty(path)) return false;
            string ext = Path.GetExtension(path).ToLowerInvariant();
            return ext == ".http" || ext == ".rest";
        }

        public static bool IsSeparator(string line)
        {
            return line != null && line.TrimStart().StartsWith("###", StringComparison.Ordinal);
        }

        private static bool IsComment(string trimmed)
        {
            return trimmed.StartsWith("#", StringComparison.Ordinal) || trimmed.StartsWith("//", StringComparison.Ordinal);
        }

        public static HttpFileDocument Parse(IList<string> lines)
        {
            HttpFileDocument doc = new HttpFileDocument();
            if (lines == null) return doc;
            int start = 0;
            string pendingTitle = "";
            for (int i = 0; i <= lines.Count; i++)
            {
                if (i == lines.Count || IsSeparator(lines[i]))
                {
                    if (i > start || i == lines.Count)
                    {
                        HttpRequestBlock blk = ParseBlock(lines, start, i - 1, doc.FileVariables);
                        if (blk != null)
                        {
                            if (blk.Title.Length == 0) blk.Title = pendingTitle;
                            doc.Requests.Add(blk);
                        }
                    }
                    if (i < lines.Count)
                    {
                        pendingTitle = lines[i].TrimStart().Substring(3).Trim();
                    }
                    start = i + 1;
                }
            }
            return doc;
        }

        public static HttpFileDocument Parse(string text)
        {
            return Parse((text ?? "").Replace("\r\n", "\n").Replace("\r", "\n").Split('\n'));
        }

        private static HttpRequestBlock ParseBlock(IList<string> lines, int from, int to, Dictionary<string, string> fileVars)
        {
            HttpRequestBlock blk = new HttpRequestBlock();
            blk.BlockStartLine = from;
            blk.BlockEndLine = Math.Max(from, to);
            int i = from;
            // Preamble: blank lines, comments (+ metadata), file variables.
            for (; i <= to; i++)
            {
                string raw = lines[i] ?? "";
                string t = raw.Trim();
                if (t.Length == 0) continue;
                Match vm = VarDefRegex.Match(raw);
                if (vm.Success)
                {
                    fileVars[vm.Groups[1].Value] = vm.Groups[2].Value;
                    continue;
                }
                if (IsComment(t))
                {
                    Match mm = MetaRegex.Match(raw);
                    if (mm.Success)
                    {
                        string key = mm.Groups[1].Value.ToLowerInvariant();
                        string val = mm.Groups[2].Success ? mm.Groups[2].Value.Trim() : "";
                        if (key == "name") blk.Name = val;
                        else if (key == "no-redirect") blk.NoRedirect = true;
                        else if (key == "title") blk.Title = val;
                    }
                    continue;
                }
                break;
            }
            if (i > to) return null; // only variables / comments: not a request

            // Request line.
            string reqLine = (lines[i] ?? "").Trim();
            blk.RequestLine = i;
            string method = "GET";
            string rest = reqLine;
            int sp = reqLine.IndexOf(' ');
            if (sp > 0)
            {
                string first = reqLine.Substring(0, sp).ToUpperInvariant();
                if (Array.IndexOf(Methods, first) >= 0)
                {
                    method = first;
                    rest = reqLine.Substring(sp + 1).Trim();
                }
            }
            else if (Array.IndexOf(Methods, reqLine.ToUpperInvariant()) >= 0)
            {
                method = reqLine.ToUpperInvariant();
                rest = "";
            }
            Match ver = Regex.Match(rest, @"\s+(HTTP/\d(?:\.\d)?)\s*$", RegexOptions.IgnoreCase);
            if (ver.Success)
            {
                blk.HttpVersion = ver.Groups[1].Value.ToUpperInvariant();
                rest = rest.Substring(0, ver.Index).Trim();
            }
            blk.Method = method;
            StringBuilder url = new StringBuilder(rest);
            i++;
            // Multi-line query continuation: lines starting with ? or &.
            for (; i <= to; i++)
            {
                string t = (lines[i] ?? "").Trim();
                if (t.StartsWith("?", StringComparison.Ordinal) || t.StartsWith("&", StringComparison.Ordinal)) url.Append(t);
                else break;
            }
            blk.Url = url.ToString();

            // Headers until the first blank line.
            for (; i <= to; i++)
            {
                string raw = lines[i] ?? "";
                string t = raw.Trim();
                if (t.Length == 0) { i++; break; }
                if (IsComment(t)) continue;
                Match hm = HeaderRegex.Match(raw);
                if (hm.Success) blk.Headers.Add(new HttpHeader(hm.Groups[1].Value, hm.Groups[2].Value));
                else
                {
                    // Not a header: treat the rest as body (tolerate a missing blank line).
                    break;
                }
            }

            // Body: rest of the block, trailing blank lines trimmed.
            int bodyEnd = to;
            while (bodyEnd >= i && (lines[bodyEnd] ?? "").Trim().Length == 0) bodyEnd--;
            if (bodyEnd >= i)
            {
                string firstBody = (lines[i] ?? "").Trim();
                if (i == bodyEnd && (firstBody.StartsWith("< ", StringComparison.Ordinal) || firstBody.StartsWith("<@ ", StringComparison.Ordinal)))
                {
                    blk.BodyFileProcessVariables = firstBody.StartsWith("<@", StringComparison.Ordinal);
                    blk.BodyFilePath = firstBody.Substring(blk.BodyFileProcessVariables ? 2 : 1).Trim();
                }
                else
                {
                    StringBuilder body = new StringBuilder();
                    for (int b = i; b <= bodyEnd; b++)
                    {
                        if (b > i) body.Append('\n');
                        body.Append(lines[b] ?? "");
                    }
                    blk.Body = body.ToString();
                }
            }
            return blk;
        }

        /// <summary>The request whose block contains the 0-based line (or null).</summary>
        public static HttpRequestBlock FindRequestAtLine(HttpFileDocument doc, int line)
        {
            if (doc == null) return null;
            foreach (HttpRequestBlock r in doc.Requests)
            {
                if (line >= r.BlockStartLine && line <= r.BlockEndLine) return r;
            }
            return null;
        }
    }

    /// <summary>A request after variable substitution, ready to send.</summary>
    public class HttpResolvedRequest
    {
        public string Name = "";
        public string Method = "GET";
        public string Url = "";
        public List<HttpHeader> Headers = new List<HttpHeader>();
        public string Body = "";
        public bool NoRedirect;
        /// <summary>Variables that could not be resolved (shown as a warning, request still sent).</summary>
        public List<string> UnresolvedVariables = new List<string>();

        public string GetHeader(string name)
        {
            foreach (HttpHeader h in Headers)
                if (string.Equals(h.Name, name, StringComparison.OrdinalIgnoreCase)) return h.Value;
            return null;
        }
    }

    /// <summary>Response kept for request variables ({{name.response.body.$.x}}) and for display.</summary>
    public class HttpResponseResult
    {
        public int StatusCode;
        public string ReasonPhrase = "";
        public string HttpVersion = "";
        public long ElapsedMs;
        public long BodyBytes;
        public List<HttpHeader> Headers = new List<HttpHeader>();
        public string Body = "";
        public string ContentType = "";
        /// <summary>Error text when no response was received (network error, timeout, blocked, cancelled).</summary>
        public string Error = "";
        public bool Blocked;
        public bool Cancelled;
        public HttpResolvedRequest Request;
        /// <summary>Redirect hops that were followed ("302 https://..."), each checked against Offline Mode and recorded.</summary>
        public List<string> Redirects = new List<string>();

        public bool HasResponse { get { return StatusCode > 0; } }

        public string GetHeader(string name)
        {
            foreach (HttpHeader h in Headers)
                if (string.Equals(h.Name, name, StringComparison.OrdinalIgnoreCase)) return h.Value;
            return null;
        }
    }

    /// <summary>
    /// {{variable}} substitution: request variables, file variables, environment variables and system variables
    /// ($guid, $timestamp, $randomInt min max, $datetime iso8601|rfc1123, $localDatetime, $processEnv NAME).
    /// </summary>
    public class HttpVariableResolver
    {
        private static readonly Regex VarRegex = new Regex(@"\{\{\s*(.+?)\s*\}\}", RegexOptions.Compiled);
        private readonly Random random = new Random();

        public Dictionary<string, string> FileVariables = new Dictionary<string, string>(StringComparer.Ordinal);
        public Dictionary<string, string> EnvironmentVariables = new Dictionary<string, string>(StringComparer.Ordinal);
        /// <summary>Last response per "# @name" (session scoped).</summary>
        public Dictionary<string, HttpResponseResult> NamedResponses = new Dictionary<string, HttpResponseResult>(StringComparer.Ordinal);
        /// <summary>Override for $timestamp/$datetime in tests.</summary>
        public DateTime? FixedNowUtc;

        public string Substitute(string input, List<string> unresolved)
        {
            return Substitute(input, unresolved, 0);
        }

        private string Substitute(string input, List<string> unresolved, int depth)
        {
            if (string.IsNullOrEmpty(input) || input.IndexOf("{{", StringComparison.Ordinal) < 0) return input ?? "";
            if (depth > 10) return input;
            return VarRegex.Replace(input, delegate (Match m)
            {
                string expr = m.Groups[1].Value.Trim();
                string val;
                if (TryResolve(expr, unresolved, depth, out val)) return val;
                if (unresolved != null && !unresolved.Contains(expr)) unresolved.Add(expr);
                return m.Value;
            });
        }

        private bool TryResolve(string expr, List<string> unresolved, int depth, out string value)
        {
            value = null;
            if (expr.StartsWith("$", StringComparison.Ordinal)) return TryResolveSystem(expr, out value);
            string v;
            if (FileVariables.TryGetValue(expr, out v)) { value = Substitute(v, unresolved, depth + 1); return true; }
            if (EnvironmentVariables.TryGetValue(expr, out v)) { value = Substitute(v, unresolved, depth + 1); return true; }
            // Request variable: name.response.body.<path> | name.response.headers.<Name> | name.request.(body|headers.X)
            int dot = expr.IndexOf('.');
            if (dot > 0)
            {
                string reqName = expr.Substring(0, dot);
                HttpResponseResult resp;
                if (NamedResponses.TryGetValue(reqName, out resp) && resp != null)
                {
                    string rest = expr.Substring(dot + 1);
                    return TryResolveRequestVariable(resp, rest, out value);
                }
            }
            return false;
        }

        private static bool TryResolveRequestVariable(HttpResponseResult resp, string rest, out string value)
        {
            value = null;
            string body, headersOwner;
            List<HttpHeader> headers;
            if (rest.StartsWith("response.", StringComparison.Ordinal))
            {
                rest = rest.Substring(9);
                body = resp.Body ?? "";
                headers = resp.Headers;
            }
            else if (rest.StartsWith("request.", StringComparison.Ordinal) && resp.Request != null)
            {
                rest = rest.Substring(8);
                body = resp.Request.Body ?? "";
                headers = resp.Request.Headers;
            }
            else return false;
            headersOwner = rest;
            if (headersOwner.StartsWith("headers.", StringComparison.Ordinal))
            {
                string hn = headersOwner.Substring(8);
                foreach (HttpHeader h in headers)
                    if (string.Equals(h.Name, hn, StringComparison.OrdinalIgnoreCase)) { value = h.Value; return true; }
                return false;
            }
            if (rest == "body" || rest == "body.*") { value = body; return true; }
            if (rest.StartsWith("body.", StringComparison.Ordinal))
            {
                string path = rest.Substring(5);
                return JsonPathQuery.TryEvaluate(body, path, out value);
            }
            return false;
        }

        private bool TryResolveSystem(string expr, out string value)
        {
            value = null;
            string[] parts = expr.Split(new char[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            string name = parts[0];
            DateTime nowUtc = FixedNowUtc.HasValue ? FixedNowUtc.Value : DateTime.UtcNow;
            switch (name)
            {
                case "$guid":
                case "$uuid":
                case "$random.uuid":
                    value = Guid.NewGuid().ToString();
                    return true;
                case "$timestamp":
                    value = ((long)(nowUtc - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalSeconds).ToString(CultureInfo.InvariantCulture);
                    return true;
                case "$randomInt":
                    {
                        int lo = 0, hi = 1000;
                        if (parts.Length >= 3)
                        {
                            int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out lo);
                            int.TryParse(parts[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out hi);
                        }
                        if (hi <= lo) hi = lo + 1;
                        lock (random) value = random.Next(lo, hi).ToString(CultureInfo.InvariantCulture);
                        return true;
                    }
                case "$datetime":
                case "$localDatetime":
                    {
                        DateTime t = name == "$localDatetime" ? nowUtc.ToLocalTime() : nowUtc;
                        string fmt = parts.Length >= 2 ? parts[1] : "iso8601";
                        if (fmt == "rfc1123") value = t.ToString("r", CultureInfo.InvariantCulture);
                        else if (fmt == "iso8601") value = name == "$localDatetime" ? t.ToString("yyyy-MM-ddTHH:mm:ss.fffzzz", CultureInfo.InvariantCulture) : t.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture);
                        else
                        {
                            string custom = string.Join(" ", parts, 1, parts.Length - 1).Trim('"', '\'');
                            try { value = t.ToString(custom, CultureInfo.InvariantCulture); } catch { value = t.ToString("o", CultureInfo.InvariantCulture); }
                        }
                        return true;
                    }
                case "$processEnv":
                    {
                        if (parts.Length < 2) return false;
                        string envName = parts[1].TrimStart('%');
                        string ev = Environment.GetEnvironmentVariable(envName);
                        value = ev ?? "";
                        return ev != null;
                    }
            }
            return false;
        }

        /// <summary>Resolves a parsed block into a sendable request. Body files are read relative to <paramref name="httpFileDir"/>.</summary>
        public HttpResolvedRequest Resolve(HttpRequestBlock block, string httpFileDir)
        {
            HttpResolvedRequest r = new HttpResolvedRequest();
            r.Name = block.Name;
            r.Method = block.Method;
            r.NoRedirect = block.NoRedirect;
            r.Url = Substitute(block.Url, r.UnresolvedVariables).Trim();
            foreach (HttpHeader h in block.Headers)
                r.Headers.Add(new HttpHeader(h.Name, Substitute(h.Value, r.UnresolvedVariables)));
            if (!string.IsNullOrEmpty(block.BodyFilePath))
            {
                string p = Substitute(block.BodyFilePath, r.UnresolvedVariables);
                string full = Path.IsPathRooted(p) ? p : Path.Combine(httpFileDir ?? "", p);
                try
                {
                    string content = File.ReadAllText(full);
                    r.Body = block.BodyFileProcessVariables ? Substitute(content, r.UnresolvedVariables) : content;
                }
                catch (Exception ex)
                {
                    r.UnresolvedVariables.Add("< " + p + " (" + ex.Message + ")");
                }
            }
            else
            {
                r.Body = Substitute(block.Body, r.UnresolvedVariables);
            }
            // Bodies are written with CRLF line breaks like REST Client does for form / multipart payloads.
            return r;
        }
    }

    /// <summary>Minimal JSONPath ($.a.b[0]['c'], $..x not supported) over the JSON shim used elsewhere.</summary>
    public static class JsonPathQuery
    {
        public static bool TryEvaluate(string json, string path, out string value)
        {
            value = null;
            if (string.IsNullOrEmpty(json) || string.IsNullOrEmpty(path)) return false;
            object root;
            try
            {
                System.Web.Script.Serialization.JavaScriptSerializer ser = new System.Web.Script.Serialization.JavaScriptSerializer();
                ser.MaxJsonLength = int.MaxValue;
                root = ser.DeserializeObject(json);
            }
            catch { return false; }
            string p = path.Trim();
            if (p.StartsWith("$", StringComparison.Ordinal)) p = p.Substring(1);
            object cur = root;
            int i = 0;
            while (i < p.Length)
            {
                char c = p[i];
                if (c == '.')
                {
                    int j = i + 1;
                    while (j < p.Length && p[j] != '.' && p[j] != '[') j++;
                    string key = p.Substring(i + 1, j - i - 1);
                    if (key.Length > 0 && !TryMember(cur, key, out cur)) return false;
                    i = j;
                }
                else if (c == '[')
                {
                    int close = p.IndexOf(']', i);
                    if (close < 0) return false;
                    string inner = p.Substring(i + 1, close - i - 1).Trim();
                    if (inner.Length >= 2 && (inner[0] == '\'' || inner[0] == '"'))
                    {
                        if (!TryMember(cur, inner.Substring(1, inner.Length - 2), out cur)) return false;
                    }
                    else
                    {
                        int idx;
                        if (!int.TryParse(inner, NumberStyles.Integer, CultureInfo.InvariantCulture, out idx)) return false;
                        System.Collections.IList list = cur as System.Collections.IList;
                        if (list == null) return false;
                        if (idx < 0) idx += list.Count;
                        if (idx < 0 || idx >= list.Count) return false;
                        cur = list[idx];
                    }
                    i = close + 1;
                }
                else
                {
                    int j = i;
                    while (j < p.Length && p[j] != '.' && p[j] != '[') j++;
                    if (!TryMember(cur, p.Substring(i, j - i), out cur)) return false;
                    i = j;
                }
            }
            value = ToText(cur);
            return true;
        }

        private static bool TryMember(object obj, string key, out object result)
        {
            result = null;
            IDictionary<string, object> d = obj as IDictionary<string, object>;
            if (d != null) return d.TryGetValue(key, out result);
            System.Collections.IDictionary nd = obj as System.Collections.IDictionary;
            if (nd != null && nd.Contains(key)) { result = nd[key]; return true; }
            return false;
        }

        private static string ToText(object v)
        {
            if (v == null) return "null";
            if (v is string) return (string)v;
            if (v is bool) return ((bool)v) ? "true" : "false";
            if (v is IDictionary<string, object> || v is System.Collections.IList || v is System.Collections.IDictionary)
            {
                try { return new System.Web.Script.Serialization.JavaScriptSerializer().Serialize(v); } catch { return v.ToString(); }
            }
            IFormattable f = v as IFormattable;
            if (f != null) return f.ToString(null, CultureInfo.InvariantCulture);
            return v.ToString();
        }
    }

    /// <summary>
    /// Environments for .http files: http-client.env.json (+ http-client.private.env.json), .http-env(.json) or
    /// rest-client.env.json, searched from the .http file folder up to the workspace root. "$shared" is merged into
    /// every environment.
    /// </summary>
    public class HttpEnvironmentSet
    {
        public static readonly string[] FileNames = new string[] { "http-client.env.json", "http-client.private.env.json", ".http-env.json", ".http-env", "rest-client.env.json" };

        public Dictionary<string, Dictionary<string, string>> Environments = new Dictionary<string, Dictionary<string, string>>(StringComparer.Ordinal);
        public Dictionary<string, string> Shared = new Dictionary<string, string>(StringComparer.Ordinal);
        public List<string> SourceFiles = new List<string>();

        public List<string> Names
        {
            get
            {
                List<string> n = new List<string>(Environments.Keys);
                n.Sort(StringComparer.OrdinalIgnoreCase);
                return n;
            }
        }

        public Dictionary<string, string> GetVariables(string envName)
        {
            Dictionary<string, string> result = new Dictionary<string, string>(Shared, StringComparer.Ordinal);
            Dictionary<string, string> env;
            if (!string.IsNullOrEmpty(envName) && Environments.TryGetValue(envName, out env))
            {
                foreach (KeyValuePair<string, string> kv in env) result[kv.Key] = kv.Value;
            }
            return result;
        }

        public static HttpEnvironmentSet Discover(string httpFilePath, string workspaceRoot)
        {
            HttpEnvironmentSet set = new HttpEnvironmentSet();
            try
            {
                string dir = Path.GetDirectoryName(Path.GetFullPath(httpFilePath));
                string stopAt = string.IsNullOrEmpty(workspaceRoot) ? null : Path.GetFullPath(workspaceRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                for (int depth = 0; depth < 32 && !string.IsNullOrEmpty(dir); depth++)
                {
                    bool foundHere = false;
                    foreach (string fn in FileNames)
                    {
                        string p = Path.Combine(dir, fn);
                        if (File.Exists(p)) { set.LoadFile(p); foundHere = true; }
                    }
                    string vscodeSettings = Path.Combine(Path.Combine(dir, ".vscode"), "settings.json");
                    if (File.Exists(vscodeSettings)) { if (set.LoadVsCodeSettings(vscodeSettings)) foundHere = true; }
                    if (foundHere) break;
                    if (stopAt != null && string.Equals(dir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar), stopAt, StringComparison.OrdinalIgnoreCase)) break;
                    DirectoryInfo parent = Directory.GetParent(dir);
                    dir = parent != null ? parent.FullName : null;
                }
            }
            catch { }
            return set;
        }

        public void LoadJson(string json)
        {
            object root;
            try
            {
                System.Web.Script.Serialization.JavaScriptSerializer ser = new System.Web.Script.Serialization.JavaScriptSerializer();
                root = ser.DeserializeObject(StripJsonComments(json));
            }
            catch { return; }
            AddEnvironments(root as IDictionary<string, object>);
        }

        private void AddEnvironments(IDictionary<string, object> envs)
        {
            if (envs == null) return;
            foreach (KeyValuePair<string, object> kv in envs)
            {
                IDictionary<string, object> vars = kv.Value as IDictionary<string, object>;
                if (vars == null) continue;
                Dictionary<string, string> target;
                if (kv.Key == "$shared") target = Shared;
                else if (!Environments.TryGetValue(kv.Key, out target))
                {
                    target = new Dictionary<string, string>(StringComparer.Ordinal);
                    Environments[kv.Key] = target;
                }
                foreach (KeyValuePair<string, object> v in vars)
                {
                    if (v.Value == null || v.Value is IDictionary<string, object>) continue;
                    IFormattable f = v.Value as IFormattable;
                    target[v.Key] = f != null ? f.ToString(null, CultureInfo.InvariantCulture) : (v.Value is bool ? (((bool)v.Value) ? "true" : "false") : v.Value.ToString());
                }
            }
        }

        private void LoadFile(string path)
        {
            try
            {
                LoadJson(File.ReadAllText(path));
                SourceFiles.Add(path);
            }
            catch { }
        }

        private bool LoadVsCodeSettings(string path)
        {
            try
            {
                System.Web.Script.Serialization.JavaScriptSerializer ser = new System.Web.Script.Serialization.JavaScriptSerializer();
                IDictionary<string, object> root = ser.DeserializeObject(StripJsonComments(File.ReadAllText(path))) as IDictionary<string, object>;
                object envs;
                if (root != null && root.TryGetValue("rest-client.environmentVariables", out envs))
                {
                    AddEnvironments(envs as IDictionary<string, object>);
                    SourceFiles.Add(path);
                    return true;
                }
            }
            catch { }
            return false;
        }

        /// <summary>Removes // and /* */ comments and trailing commas outside strings (VS Code settings are JSONC).</summary>
        public static string StripJsonComments(string json)
        {
            if (string.IsNullOrEmpty(json)) return json ?? "";
            StringBuilder sb = new StringBuilder(json.Length);
            bool inStr = false;
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
                if (c == '"') { inStr = true; sb.Append(c); continue; }
                if (c == '/' && i + 1 < json.Length && json[i + 1] == '/')
                {
                    while (i < json.Length && json[i] != '\n') i++;
                    sb.Append('\n');
                    continue;
                }
                if (c == '/' && i + 1 < json.Length && json[i + 1] == '*')
                {
                    int end = json.IndexOf("*/", i + 2, StringComparison.Ordinal);
                    i = end < 0 ? json.Length : end + 1;
                    continue;
                }
                if (c == ',')
                {
                    int j = i + 1;
                    while (j < json.Length && char.IsWhiteSpace(json[j])) j++;
                    if (j < json.Length && (json[j] == '}' || json[j] == ']')) continue;
                }
                sb.Append(c);
            }
            return sb.ToString();
        }
    }
}
