using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace codeLess.Studio
{
    /// <summary>
    /// One in-flight request: times it, counts body bytes (thread-safe, also while streaming) and records exactly one
    /// <see cref="NetworkLedger"/> entry when <see cref="Complete()"/> or <see cref="Dispose"/> is called. Create with
    /// <see cref="NetworkLedger.Begin(string,string,string)"/> (which performs the Offline Mode check). C# 5.
    /// </summary>
    public sealed class NetworkRequestScope : IDisposable
    {
        private readonly string feature;
        private readonly string method;
        private readonly string url;
        private readonly Stopwatch stopwatch;
        private long sent;
        private long received;
        private int completed;

        /// <summary>HTTP status to record (set it as soon as the response headers arrive).</summary>
        public int Status;

        /// <summary>Optional short note (e.g. "streamed", "Cancelled"). Never put headers, keys or bodies here.</summary>
        public string Note = "";

        internal NetworkRequestScope(string feature, string method, string url)
        {
            this.feature = feature ?? "";
            this.method = string.IsNullOrEmpty(method) ? "GET" : method.ToUpperInvariant();
            this.url = url ?? "";
            stopwatch = Stopwatch.StartNew();
            NetworkLedger.ChangeInFlight(1);
        }

        public string Feature { get { return feature; } }
        public string Method { get { return method; } }
        public bool IsCompleted { get { return Thread.VolatileRead(ref completed) == 1; } }
        public long BytesSent { get { return Interlocked.Read(ref sent); } }
        public long BytesReceived { get { return Interlocked.Read(ref received); } }
        public long ElapsedMs { get { return stopwatch.ElapsedMilliseconds; } }

        public void AddSent(long bytes)
        {
            if (bytes > 0) Interlocked.Add(ref sent, bytes);
        }

        public void AddReceived(long bytes)
        {
            if (bytes > 0) Interlocked.Add(ref received, bytes);
        }

        /// <summary>Wraps a request stream so every byte written is counted as sent.</summary>
        public Stream WrapRequestStream(Stream inner)
        {
            return new LedgerCountingStream(inner, this, false);
        }

        /// <summary>Wraps a response stream so every byte read (as it streams) is counted as received.</summary>
        public Stream WrapResponseStream(Stream inner)
        {
            return new LedgerCountingStream(inner, this, false);
        }

        /// <summary>Records a failure (status from a WebException response, if any) and completes the scope.</summary>
        public void Fail(Exception ex)
        {
            if (Status <= 0) Status = NetworkLedger.StatusFromException(ex);
            if (string.IsNullOrEmpty(Note)) Note = NetworkLedger.NoteFromException(ex);
            Complete();
        }

        public void Complete(int status, string note)
        {
            Status = status;
            if (note != null) Note = note;
            Complete();
        }

        /// <summary>Records the entry once (later calls are ignored).</summary>
        public void Complete()
        {
            if (Interlocked.Exchange(ref completed, 1) == 1) return;
            stopwatch.Stop();
            NetworkLedger.ChangeInFlight(-1);
            NetworkLedger.Record(feature, method, url, BytesSent, BytesReceived, Status, Note, stopwatch.ElapsedMilliseconds);
        }

        public void Dispose()
        {
            if (!IsCompleted && Status == 0 && string.IsNullOrEmpty(Note)) Note = "Incomplete";
            Complete();
        }
    }

    /// <summary>Pass-through stream that counts bytes into a <see cref="NetworkRequestScope"/>.</summary>
    public class LedgerCountingStream : Stream
    {
        private readonly Stream inner;
        private readonly NetworkRequestScope scope;
        private readonly bool completeOnEnd;

        public LedgerCountingStream(Stream inner, NetworkRequestScope scope, bool completeScopeAtEndOrDispose)
        {
            if (inner == null) throw new ArgumentNullException("inner");
            this.inner = inner;
            this.scope = scope;
            completeOnEnd = completeScopeAtEndOrDispose;
        }

        public override bool CanRead { get { return inner.CanRead; } }
        public override bool CanSeek { get { return inner.CanSeek; } }
        public override bool CanWrite { get { return inner.CanWrite; } }
        public override long Length { get { return inner.Length; } }
        public override long Position { get { return inner.Position; } set { inner.Position = value; } }
        public override bool CanTimeout { get { return inner.CanTimeout; } }
        public override int ReadTimeout { get { return inner.ReadTimeout; } set { inner.ReadTimeout = value; } }
        public override int WriteTimeout { get { return inner.WriteTimeout; } set { inner.WriteTimeout = value; } }
        public override void Flush() { inner.Flush(); }
        public override long Seek(long offset, SeekOrigin origin) { return inner.Seek(offset, origin); }
        public override void SetLength(long value) { inner.SetLength(value); }

        public override int Read(byte[] buffer, int offset, int count)
        {
            int n = inner.Read(buffer, offset, count);
            OnRead(n, count);
            return n;
        }

        public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            int n = await inner.ReadAsync(buffer, offset, count, cancellationToken).ConfigureAwait(false);
            OnRead(n, count);
            return n;
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            inner.Write(buffer, offset, count);
            if (scope != null) scope.AddSent(count);
        }

        public override async Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            await inner.WriteAsync(buffer, offset, count, cancellationToken).ConfigureAwait(false);
            if (scope != null) scope.AddSent(count);
        }

        private void OnRead(int n, int requested)
        {
            if (scope == null) return;
            if (n > 0) scope.AddReceived(n);
            else if (n == 0 && requested > 0 && completeOnEnd) scope.Complete();
        }

        protected override void Dispose(bool disposing)
        {
            try
            {
                if (disposing) inner.Dispose();
            }
            finally
            {
                if (completeOnEnd && scope != null) scope.Complete();
                base.Dispose(disposing);
            }
        }
    }

    /// <summary>Request description for <see cref="LedgerHttp.Send"/>. Headers are sent but never logged.</summary>
    public class LedgerHttpRequest
    {
        public string Feature = "";
        public string Method = "GET";
        public string Url = "";
        public Dictionary<string, string> Headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        public byte[] Body;
        public string ContentType;
        public string Accept;
        public string UserAgent = "codeLess";
        public int TimeoutMs = 15000;
        /// <summary>Responses larger than this are truncated (protects the IDE from huge downloads into memory).</summary>
        public long MaxResponseBytes = 32L * 1024 * 1024;
        /// <summary>When false, redirects to a different host are not followed automatically.</summary>
        public bool AllowCrossHostRedirects = true;
        /// <summary>Message used for the <see cref="NetworkBlockedException"/> when Offline Mode refuses the request.</summary>
        public string BlockedMessage;
    }

    /// <summary>Result of <see cref="LedgerHttp.Send"/>.</summary>
    public class LedgerHttpResponse
    {
        /// <summary>HTTP status; 0 when no response arrived (see <see cref="Error"/>).</summary>
        public int Status;
        public string StatusDescription = "";
        public string ProtocolVersion = "";
        public string ContentType = "";
        public string Body = "";
        public bool Truncated;
        /// <summary>Raw (possibly truncated) body bytes, for binary downloads such as images.</summary>
        public byte[] BodyBytes;
        public List<KeyValuePair<string, string>> Headers = new List<KeyValuePair<string, string>>();
        public long BytesSent;
        public long BytesReceived;
        public long DurationMs;
        /// <summary>Transport error (DNS, connect, timeout); empty when a response (any status) arrived.</summary>
        public string Error = "";

        public bool IsSuccess { get { return Status >= 200 && Status < 300; } }
    }

    /// <summary>
    /// HttpWebRequest wrapper that performs <see cref="NetworkLedger.Check"/> + <see cref="NetworkLedger.Record(string,string,string,long,long,int,string,long)"/>
    /// automatically and counts body bytes. Redirects are never followed by the framework: each hop is checked against
    /// Offline Mode and recorded in the ledger before it is contacted (an allowed loopback URL can't bounce the request to
    /// an external host). Blocked requests (also a blocked redirect hop) throw <see cref="NetworkBlockedException"/>; HTTP
    /// error statuses are returned (not thrown); transport errors are returned in <see cref="LedgerHttpResponse.Error"/>.
    /// Blocking: call from a worker thread.
    /// </summary>
    public static class LedgerHttp
    {
        /// <summary>Maximum redirect hops followed (like browsers / HttpClient's default of 50, but tighter).</summary>
        public const int MaxRedirects = 10;

        /// <summary>In-process transport seam for unit tests (invoked after NetworkLedger.Begin check).</summary>
        public static Func<LedgerHttpRequest, LedgerHttpResponse> TransportOverride;

        public static LedgerHttpResponse Get(string feature, string url, int timeoutMs)
        {
            LedgerHttpRequest r = new LedgerHttpRequest();
            r.Feature = feature;
            r.Url = url;
            r.TimeoutMs = timeoutMs;
            return Send(r);
        }

        public static bool IsRedirectStatus(int status)
        {
            return status == 301 || status == 302 || status == 303 || status == 307 || status == 308;
        }

        /// <summary>
        /// Next hop of a redirect, or null when it must not be followed (no / invalid Location, non-http(s) target, or an
        /// https -> http downgrade, which HttpClient refuses too). <paramref name="method"/> / <paramref name="dropBody"/>
        /// follow RFC 9110 + browser behaviour: 303 (and 301/302 after POST) continue as GET without a body.
        /// </summary>
        public static string ResolveRedirect(string currentUrl, int status, string location, ref string method, out bool dropBody)
        {
            dropBody = false;
            if (string.IsNullOrEmpty(location)) return null;
            Uri baseUri, next;
            if (!Uri.TryCreate(currentUrl, UriKind.Absolute, out baseUri)) return null;
            if (!Uri.TryCreate(baseUri, location.Trim(), out next)) return null;
            if (next.Scheme != Uri.UriSchemeHttp && next.Scheme != Uri.UriSchemeHttps) return null;
            if (baseUri.Scheme == Uri.UriSchemeHttps && next.Scheme == Uri.UriSchemeHttp) return null;
            if (status == 303 && method != "HEAD") { method = "GET"; dropBody = true; }
            else if ((status == 301 || status == 302) && method == "POST") { method = "GET"; dropBody = true; }
            return next.AbsoluteUri;
        }

        private static bool SameOrigin(string a, string b)
        {
            Uri ua, ub;
            if (!Uri.TryCreate(a, UriKind.Absolute, out ua) || !Uri.TryCreate(b, UriKind.Absolute, out ub)) return false;
            return string.Equals(ua.Scheme, ub.Scheme, StringComparison.OrdinalIgnoreCase) &&
                   string.Equals(ua.Host, ub.Host, StringComparison.OrdinalIgnoreCase) && ua.Port == ub.Port;
        }

        private static string HostOf(string url)
        {
            Uri u;
            return Uri.TryCreate(url, UriKind.Absolute, out u) ? u.Host : url;
        }

        /// <summary>Copy of the request for the next redirect hop (credentials are dropped when the origin changes).</summary>
        private static LedgerHttpRequest NextHop(LedgerHttpRequest r, string url, bool dropBody, bool sameOrigin)
        {
            LedgerHttpRequest n = new LedgerHttpRequest();
            n.Feature = r.Feature;
            n.Url = url;
            n.Accept = r.Accept;
            n.UserAgent = r.UserAgent;
            n.TimeoutMs = r.TimeoutMs;
            n.MaxResponseBytes = r.MaxResponseBytes;
            n.AllowCrossHostRedirects = r.AllowCrossHostRedirects;
            n.BlockedMessage = r.BlockedMessage;
            n.Body = dropBody ? null : r.Body;
            n.ContentType = dropBody ? null : r.ContentType;
            if (r.Headers != null)
                foreach (KeyValuePair<string, string> h in r.Headers)
                {
                    if (!sameOrigin && (h.Key.Equals("Authorization", StringComparison.OrdinalIgnoreCase) || h.Key.Equals("Cookie", StringComparison.OrdinalIgnoreCase) ||
                                        h.Key.IndexOf("api-key", StringComparison.OrdinalIgnoreCase) >= 0 || h.Key.IndexOf("token", StringComparison.OrdinalIgnoreCase) >= 0)) continue;
                    if (dropBody && h.Key.StartsWith("Content-", StringComparison.OrdinalIgnoreCase)) continue;
                    n.Headers[h.Key] = h.Value;
                }
            return n;
        }

        /// <summary>
        /// Sends one hop with redirects disabled. Returns the response (any status) or null with <paramref name="transportError"/>
        /// set. The scope is not completed here.
        /// </summary>
        private static HttpWebResponse SendOneHop(LedgerHttpRequest request, string method, NetworkRequestScope scope, out Exception transportError)
        {
            transportError = null;
            try { ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12; } catch { }
            HttpWebRequest req = CreateWebRequest(request, method);
            if (request.Body != null && method != "GET" && method != "HEAD")
            {
                req.ContentLength = request.Body.Length;
                using (Stream s = scope.WrapRequestStream(req.GetRequestStream()))
                    s.Write(request.Body, 0, request.Body.Length);
            }
            try
            {
                return (HttpWebResponse)req.GetResponse();
            }
            catch (WebException wex)
            {
                HttpWebResponse res = wex.Response as HttpWebResponse;
                if (res == null) transportError = wex;
                return res;
            }
        }

        public static LedgerHttpResponse Send(LedgerHttpRequest request)
        {
            if (request == null) throw new ArgumentNullException("request");
            string method = string.IsNullOrEmpty(request.Method) ? "GET" : request.Method.ToUpperInvariant();
            LedgerHttpResponse result = new LedgerHttpResponse();
            LedgerHttpRequest hop = request;
            long sentBefore = 0, receivedBefore = 0, msBefore = 0;
            for (int redirects = 0; ; redirects++)
            {
                NetworkRequestScope scope = NetworkLedger.Begin(hop.Feature, method, hop.Url, hop.BlockedMessage);
                try
                {
                    Func<LedgerHttpRequest, LedgerHttpResponse> overrideFn = TransportOverride;
                    if (overrideFn != null)
                    {
                        LedgerHttpResponse fake = overrideFn(hop);
                        if (fake == null)
                        {
                            result.Error = "No response";
                            scope.Fail(new IOException(result.Error));
                            return Finish(result, scope, sentBefore, receivedBefore, msBefore);
                        }
                        int fakeStatus = fake.Status;
                        scope.Status = fakeStatus;
                        if (IsRedirectStatus(fakeStatus) && redirects < MaxRedirects)
                        {
                            string loc = null;
                            if (fake.Headers != null)
                            {
                                foreach (KeyValuePair<string, string> kv in fake.Headers)
                                {
                                    if (string.Equals(kv.Key, "Location", StringComparison.OrdinalIgnoreCase))
                                    {
                                        loc = kv.Value;
                                        break;
                                    }
                                }
                            }
                            string nextMethod = method;
                            bool dropBody;
                            string next = ResolveRedirect(hop.Url, fakeStatus, loc, ref nextMethod, out dropBody);
                            if (next != null)
                            {
                                if (!hop.AllowCrossHostRedirects && !string.Equals(HostOf(hop.Url), HostOf(next), StringComparison.OrdinalIgnoreCase))
                                {
                                    result.Status = fakeStatus;
                                    result.Error = "Cross-host redirect to " + HostOf(next) + " is not allowed.";
                                    scope.Complete(fakeStatus, "Cross-host redirect refused");
                                    return Finish(result, scope, sentBefore, receivedBefore, msBefore);
                                }
                                scope.Complete(fakeStatus, "Redirect to " + HostOf(next));
                                sentBefore += scope.BytesSent; receivedBefore += scope.BytesReceived; msBefore += scope.ElapsedMs;
                                hop = NextHop(hop, next, dropBody, SameOrigin(hop.Url, next));
                                method = nextMethod;
                                continue;
                            }
                        }
                        byte[] rawBytes = fake.BodyBytes ?? Encoding.UTF8.GetBytes(fake.Body ?? "");
                        bool truncated = fake.Truncated;
                        if (hop.MaxResponseBytes > 0 && rawBytes.Length > hop.MaxResponseBytes)
                        {
                            byte[] cut = new byte[hop.MaxResponseBytes];
                            Buffer.BlockCopy(rawBytes, 0, cut, 0, (int)hop.MaxResponseBytes);
                            rawBytes = cut;
                            truncated = true;
                        }
                        scope.AddReceived(rawBytes.Length);
                        result.Status = fakeStatus;
                        result.StatusDescription = fake.StatusDescription ?? "";
                        result.ContentType = fake.ContentType ?? "";
                        result.BodyBytes = rawBytes;
                        result.Body = DecodeBody(rawBytes, result.ContentType);
                        result.Truncated = truncated;
                        result.Headers = fake.Headers ?? new List<KeyValuePair<string, string>>();
                        result.Error = fake.Error ?? "";
                        scope.Complete();
                        return Finish(result, scope, sentBefore, receivedBefore, msBefore);
                    }

                    Exception transport;
                    HttpWebResponse res = SendOneHop(hop, method, scope, out transport);
                    if (res == null)
                    {
                        result.Error = transport != null ? transport.Message : "No response";
                        scope.Fail(transport ?? new IOException(result.Error));
                        return Finish(result, scope, sentBefore, receivedBefore, msBefore);
                    }
                    using (res)
                    {
                        int status = (int)res.StatusCode;
                        if (IsRedirectStatus(status) && redirects < MaxRedirects)
                        {
                            string nextMethod = method;
                            bool dropBody;
                            string next = ResolveRedirect(hop.Url, status, res.Headers["Location"], ref nextMethod, out dropBody);
                            if (next != null)
                            {
                                if (!hop.AllowCrossHostRedirects && !string.Equals(HostOf(hop.Url), HostOf(next), StringComparison.OrdinalIgnoreCase))
                                {
                                    result.Status = status;
                                    result.Error = "Cross-host redirect to " + HostOf(next) + " is not allowed.";
                                    scope.Complete(status, "Cross-host redirect refused");
                                    return Finish(result, scope, sentBefore, receivedBefore, msBefore);
                                }
                                scope.Complete(status, "Redirect to " + HostOf(next));
                                sentBefore += scope.BytesSent; receivedBefore += scope.BytesReceived; msBefore += scope.ElapsedMs;
                                hop = NextHop(hop, next, dropBody, SameOrigin(hop.Url, next));
                                method = nextMethod;
                                continue;
                            }
                        }
                        ReadResponse(res, scope, result, hop.MaxResponseBytes);
                    }
                    scope.Complete();
                    return Finish(result, scope, sentBefore, receivedBefore, msBefore);
                }
                catch (NetworkBlockedException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    if (string.IsNullOrEmpty(result.Error)) result.Error = ex.Message;
                    scope.Fail(ex);
                    return Finish(result, scope, sentBefore, receivedBefore, msBefore);
                }
            }
        }

        /// <summary>Downloads <paramref name="url"/> to <paramref name="filePath"/> (streamed, counted, redirects checked per hop). Body is not kept in memory.</summary>
        public static LedgerHttpResponse DownloadToFile(string feature, string url, string filePath, string userAgent, int timeoutMs)
        {
            return DownloadToFile(feature, url, filePath, userAgent, timeoutMs, null, null);
        }

        /// <summary>
        /// <see cref="DownloadToFile(string,string,string,string,int)"/> with progress (bytes written, Content-Length or -1)
        /// and cooperative cancellation (checked between chunks; a cancelled download returns Error "Cancelled").
        /// </summary>
        public static LedgerHttpResponse DownloadToFile(string feature, string url, string filePath, string userAgent, int timeoutMs,
            Action<long, long> progress, Func<bool> cancelled)
        {
            LedgerHttpResponse result = new LedgerHttpResponse();
            LedgerHttpRequest hop = new LedgerHttpRequest();
            hop.Feature = feature;
            hop.Url = url;
            hop.UserAgent = string.IsNullOrEmpty(userAgent) ? "codeLess" : userAgent;
            hop.TimeoutMs = timeoutMs;
            string method = "GET";
            long sentBefore = 0, receivedBefore = 0, msBefore = 0;
            for (int redirects = 0; ; redirects++)
            {
                NetworkRequestScope scope = NetworkLedger.Begin(feature, method, hop.Url);
                try
                {
                    Exception transport;
                    HttpWebResponse res = SendOneHop(hop, method, scope, out transport);
                    if (res == null) throw transport ?? new IOException("No response");
                    using (res)
                    {
                        int status = (int)res.StatusCode;
                        if (IsRedirectStatus(status) && redirects < MaxRedirects)
                        {
                            bool dropBody;
                            string next = ResolveRedirect(hop.Url, status, res.Headers["Location"], ref method, out dropBody);
                            if (next != null)
                            {
                                scope.Complete(status, "Redirect to " + HostOf(next));
                                sentBefore += scope.BytesSent; receivedBefore += scope.BytesReceived; msBefore += scope.ElapsedMs;
                                hop = NextHop(hop, next, dropBody, SameOrigin(hop.Url, next));
                                continue;
                            }
                        }
                        scope.Status = status;
                        result.Status = status;
                        result.StatusDescription = res.StatusDescription ?? "";
                        result.ContentType = res.ContentType ?? "";
                        if (status < 200 || status >= 300)
                        {
                            result.Error = "HTTP " + status;
                            scope.Complete();
                            return Finish(result, scope, sentBefore, receivedBefore, msBefore);
                        }
                        long total = res.ContentLength;
                        using (Stream input = scope.WrapResponseStream(res.GetResponseStream()))
                        using (FileStream output = new FileStream(filePath, FileMode.Create, FileAccess.Write, FileShare.None))
                        {
                            byte[] buf = new byte[81920];
                            long written = 0;
                            int n;
                            if (progress != null) progress(0, total);
                            while ((n = input.Read(buf, 0, buf.Length)) > 0)
                            {
                                if (cancelled != null && cancelled()) throw new OperationCanceledException("Cancelled");
                                output.Write(buf, 0, n);
                                written += n;
                                if (progress != null) progress(written, total);
                            }
                        }
                    }
                    scope.Complete();
                    return Finish(result, scope, sentBefore, receivedBefore, msBefore);
                }
                catch (NetworkBlockedException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    result.Status = ex is OperationCanceledException ? 0 : NetworkLedger.StatusFromException(ex);
                    result.Error = ex is OperationCanceledException ? "Cancelled" : (result.Status > 0 ? ("HTTP " + result.Status) : ex.Message);
                    scope.Fail(ex);
                    return Finish(result, scope, sentBefore, receivedBefore, msBefore);
                }
            }
        }

        /// <summary>GET into memory (images etc.) with per-hop ledger checks. Null on any failure, blocked hop or non-2xx status.</summary>
        public static byte[] DownloadBytes(string feature, string url, int timeoutMs, long maxBytes)
        {
            LedgerHttpRequest r = new LedgerHttpRequest();
            r.Feature = feature;
            r.Url = url;
            r.TimeoutMs = timeoutMs;
            r.MaxResponseBytes = maxBytes;
            try
            {
                LedgerHttpResponse res = Send(r);
                if (!res.IsSuccess || res.Truncated) return null;
                return res.BodyBytes;
            }
            catch (NetworkBlockedException) { return null; }
            catch { return null; }
        }

        private static HttpWebRequest CreateWebRequest(LedgerHttpRequest request, string method)
        {
            HttpWebRequest req = (HttpWebRequest)WebRequest.Create(request.Url);
            req.Method = method;
            req.AllowAutoRedirect = false; // every hop goes through NetworkLedger (see Send)
            req.Timeout = Math.Max(1000, request.TimeoutMs);
            req.ReadWriteTimeout = Math.Max(1000, request.TimeoutMs);
            req.UserAgent = string.IsNullOrEmpty(request.UserAgent) ? "codeLess" : request.UserAgent;
            if (!string.IsNullOrEmpty(request.Accept)) req.Accept = request.Accept;
            if (!string.IsNullOrEmpty(request.ContentType)) req.ContentType = request.ContentType;
            if (request.Headers != null)
            {
                foreach (KeyValuePair<string, string> h in request.Headers)
                {
                    if (string.IsNullOrEmpty(h.Key)) continue;
                    string k = h.Key.Trim();
                    string v = h.Value ?? "";
                    if (k.Equals("Content-Type", StringComparison.OrdinalIgnoreCase)) req.ContentType = v;
                    else if (k.Equals("Accept", StringComparison.OrdinalIgnoreCase)) req.Accept = v;
                    else if (k.Equals("User-Agent", StringComparison.OrdinalIgnoreCase)) req.UserAgent = v;
                    else if (k.Equals("Referer", StringComparison.OrdinalIgnoreCase)) req.Referer = v;
                    else if (k.Equals("Content-Length", StringComparison.OrdinalIgnoreCase) || k.Equals("Host", StringComparison.OrdinalIgnoreCase) ||
                             k.Equals("Connection", StringComparison.OrdinalIgnoreCase)) continue;
                    else req.Headers[k] = v;
                }
            }
            return req;
        }

        private static void ReadResponse(HttpWebResponse res, NetworkRequestScope scope, LedgerHttpResponse result, long maxBytes)
        {
            scope.Status = (int)res.StatusCode;
            result.Status = scope.Status;
            result.StatusDescription = res.StatusDescription ?? "";
            result.ContentType = res.ContentType ?? "";
            try { result.ProtocolVersion = res.ProtocolVersion != null ? res.ProtocolVersion.ToString() : ""; } catch { }
            foreach (string k in res.Headers.AllKeys)
                result.Headers.Add(new KeyValuePair<string, string>(k, res.Headers[k]));
            using (Stream s = scope.WrapResponseStream(res.GetResponseStream()))
            using (MemoryStream ms = new MemoryStream())
            {
                byte[] buf = new byte[16384];
                int n;
                while ((n = s.Read(buf, 0, buf.Length)) > 0)
                {
                    if (maxBytes > 0 && ms.Length + n > maxBytes)
                    {
                        ms.Write(buf, 0, (int)Math.Max(0, maxBytes - ms.Length));
                        result.Truncated = true;
                        break;
                    }
                    ms.Write(buf, 0, n);
                }
                byte[] all = ms.ToArray();
                result.BodyBytes = all;
                result.Body = DecodeBody(all, result.ContentType);
            }
        }

        private static string DecodeBody(byte[] bytes, string contentType)
        {
            Encoding enc = Encoding.UTF8;
            string ct = contentType ?? "";
            int cs = ct.IndexOf("charset=", StringComparison.OrdinalIgnoreCase);
            if (cs >= 0)
            {
                string name = ct.Substring(cs + 8).Trim().Trim('"', '\'');
                int semi = name.IndexOf(';');
                if (semi >= 0) name = name.Substring(0, semi);
                try { enc = Encoding.GetEncoding(name); } catch { enc = Encoding.UTF8; }
            }
            return enc.GetString(bytes);
        }

        private static LedgerHttpResponse Finish(LedgerHttpResponse result, NetworkRequestScope scope, long sentBefore, long receivedBefore, long msBefore)
        {
            result.BytesSent = sentBefore + scope.BytesSent;
            result.BytesReceived = receivedBefore + scope.BytesReceived;
            result.DurationMs = msBefore + scope.ElapsedMs;
            return result;
        }
    }

    /// <summary>
    /// <see cref="HttpClient"/> handler that routes every request through the ledger:
    /// <c>new HttpClient(new LedgerHttpMessageHandler("HTTP Client", new HttpClientHandler()))</c>.
    /// Offline Mode refusals throw <see cref="NetworkBlockedException"/>. The inner handler's automatic redirects are turned
    /// off; redirects are followed here, one ledger check + entry per hop. Response bytes are counted as the caller reads
    /// the content; the entry is recorded when the content is fully read or the response is disposed.
    /// </summary>
    public class LedgerHttpMessageHandler : DelegatingHandler
    {
        private readonly string feature;

        /// <summary>Follow redirects (each hop checked + recorded). False = return the 3xx response as is.</summary>
        public bool FollowRedirects = true;

        public LedgerHttpMessageHandler(string feature, HttpMessageHandler innerHandler)
            : base(DisableAutoRedirect(innerHandler ?? new HttpClientHandler()))
        {
            this.feature = string.IsNullOrEmpty(feature) ? "HTTP" : feature;
        }

        /// <summary>Turns off the framework's redirect following (HttpClientHandler, SocketsHttpHandler or anything with AllowAutoRedirect).</summary>
        public static HttpMessageHandler DisableAutoRedirect(HttpMessageHandler h)
        {
            HttpClientHandler hch = h as HttpClientHandler;
            if (hch != null) { try { hch.AllowAutoRedirect = false; } catch { } return h; }
            try
            {
                System.Reflection.PropertyInfo p = h.GetType().GetProperty("AllowAutoRedirect");
                if (p != null && p.CanWrite) p.SetValue(h, false, null);
            }
            catch { }
            DelegatingHandler dh = h as DelegatingHandler;
            if (dh != null && dh.InnerHandler != null) DisableAutoRedirect(dh.InnerHandler);
            return h;
        }

        /// <summary>Optional: computes the feature name per request (e.g. include the model). Falls back to the constructor value.</summary>
        public Func<HttpRequestMessage, string> FeatureSelector;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            string f = feature;
            if (FeatureSelector != null)
            {
                try
                {
                    string sel = FeatureSelector(request);
                    if (!string.IsNullOrEmpty(sel)) f = sel;
                }
                catch { }
            }
            byte[] bodyBytes = null;
            if (request.Content != null)
            {
                await request.Content.LoadIntoBufferAsync().ConfigureAwait(false);
                bodyBytes = await request.Content.ReadAsByteArrayAsync().ConfigureAwait(false);
            }
            HttpRequestMessage current = request;
            for (int redirects = 0; ; redirects++)
            {
                string url = current.RequestUri != null ? current.RequestUri.ToString() : "";
                string method = current.Method != null ? current.Method.Method : "GET";
                NetworkRequestScope scope = NetworkLedger.Begin(f, method, url);
                HttpResponseMessage response;
                try
                {
                    if (current.Content != null)
                    {
                        long? len = current.Content.Headers.ContentLength;
                        if (len.HasValue) scope.AddSent(len.Value);
                    }
                    response = await base.SendAsync(current, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    scope.Fail(ex);
                    throw;
                }
                int status = (int)response.StatusCode;
                scope.Status = status;
                if (FollowRedirects && LedgerHttp.IsRedirectStatus(status) && redirects < LedgerHttp.MaxRedirects && response.Headers.Location != null)
                {
                    string nextMethod = method.ToUpperInvariant();
                    bool dropBody;
                    string next = LedgerHttp.ResolveRedirect(url, status, response.Headers.Location.OriginalString, ref nextMethod, out dropBody);
                    if (next != null)
                    {
                        scope.Complete(status, "Redirect");
                        try { response.Dispose(); } catch { }
                        HttpRequestMessage hop = new HttpRequestMessage(new HttpMethod(nextMethod), next);
                        bool sameOrigin = current.RequestUri != null && string.Equals(current.RequestUri.Host, hop.RequestUri.Host, StringComparison.OrdinalIgnoreCase) &&
                                          current.RequestUri.Port == hop.RequestUri.Port && current.RequestUri.Scheme == hop.RequestUri.Scheme;
                        foreach (KeyValuePair<string, IEnumerable<string>> h in current.Headers)
                        {
                            if (!sameOrigin && (h.Key.Equals("Authorization", StringComparison.OrdinalIgnoreCase) || h.Key.Equals("Cookie", StringComparison.OrdinalIgnoreCase) ||
                                                h.Key.IndexOf("api-key", StringComparison.OrdinalIgnoreCase) >= 0 || h.Key.IndexOf("token", StringComparison.OrdinalIgnoreCase) >= 0)) continue;
                            hop.Headers.TryAddWithoutValidation(h.Key, h.Value);
                        }
                        if (!dropBody && bodyBytes != null && current.Content != null)
                        {
                            ByteArrayContent c = new ByteArrayContent(bodyBytes);
                            foreach (KeyValuePair<string, IEnumerable<string>> h in current.Content.Headers) c.Headers.TryAddWithoutValidation(h.Key, h.Value);
                            hop.Content = c;
                        }
                        if (!ReferenceEquals(current, request)) { try { current.Dispose(); } catch { } }
                        current = hop;
                        continue;
                    }
                }
                if (response.Content == null)
                {
                    scope.Complete();
                    return response;
                }
                try
                {
                    HttpContent original = response.Content;
                    Stream body = await original.ReadAsStreamAsync().ConfigureAwait(false);
                    StreamContent counted = new StreamContent(new LedgerCountingStream(body, scope, true));
                    foreach (KeyValuePair<string, IEnumerable<string>> h in original.Headers)
                        counted.Headers.TryAddWithoutValidation(h.Key, h.Value);
                    response.Content = counted;
                }
                catch (Exception ex)
                {
                    scope.Fail(ex);
                }
                return response;
            }
        }
    }
}
