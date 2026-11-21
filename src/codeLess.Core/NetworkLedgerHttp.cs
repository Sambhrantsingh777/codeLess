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
    /// automatically and counts body bytes. Blocked requests throw <see cref="NetworkBlockedException"/>; HTTP error
    /// statuses are returned (not thrown); transport errors are returned in <see cref="LedgerHttpResponse.Error"/>.
    /// Blocking: call from a worker thread.
    /// </summary>
    public static class LedgerHttp
    {
        public static LedgerHttpResponse Get(string feature, string url, int timeoutMs)
        {
            LedgerHttpRequest r = new LedgerHttpRequest();
            r.Feature = feature;
            r.Url = url;
            r.TimeoutMs = timeoutMs;
            return Send(r);
        }

        public static LedgerHttpResponse Send(LedgerHttpRequest request)
        {
            if (request == null) throw new ArgumentNullException("request");
            string method = string.IsNullOrEmpty(request.Method) ? "GET" : request.Method.ToUpperInvariant();
            NetworkRequestScope scope = NetworkLedger.Begin(request.Feature, method, request.Url, request.BlockedMessage);
            LedgerHttpResponse result = new LedgerHttpResponse();
            try
            {
                try { ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12; } catch { }
                HttpWebRequest req = CreateWebRequest(request, method);
                if (request.Body != null && method != "GET" && method != "HEAD")
                {
                    req.ContentLength = request.Body.Length;
                    using (Stream s = scope.WrapRequestStream(req.GetRequestStream()))
                        s.Write(request.Body, 0, request.Body.Length);
                }
                HttpWebResponse res = null;
                try
                {
                    res = (HttpWebResponse)req.GetResponse();
                }
                catch (WebException wex)
                {
                    res = wex.Response as HttpWebResponse;
                    if (res == null)
                    {
                        result.Error = wex.Message;
                        scope.Fail(wex);
                        return Finish(result, scope);
                    }
                }
                using (res)
                {
                    ReadResponse(res, scope, result, request.MaxResponseBytes);
                }
                scope.Complete();
                return Finish(result, scope);
            }
            catch (NetworkBlockedException)
            {
                throw;
            }
            catch (Exception ex)
            {
                if (string.IsNullOrEmpty(result.Error)) result.Error = ex.Message;
                scope.Fail(ex);
                return Finish(result, scope);
            }
        }

        /// <summary>Downloads <paramref name="url"/> to <paramref name="filePath"/> (streamed, counted). Body is not kept in memory.</summary>
        public static LedgerHttpResponse DownloadToFile(string feature, string url, string filePath, string userAgent, int timeoutMs)
        {
            NetworkRequestScope scope = NetworkLedger.Begin(feature, "GET", url);
            LedgerHttpResponse result = new LedgerHttpResponse();
            try
            {
                try { ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12; } catch { }
                LedgerHttpRequest r = new LedgerHttpRequest();
                r.Url = url;
                r.UserAgent = string.IsNullOrEmpty(userAgent) ? "codeLess" : userAgent;
                r.TimeoutMs = timeoutMs;
                HttpWebRequest req = CreateWebRequest(r, "GET");
                using (HttpWebResponse res = (HttpWebResponse)req.GetResponse())
                {
                    scope.Status = (int)res.StatusCode;
                    result.Status = scope.Status;
                    result.StatusDescription = res.StatusDescription ?? "";
                    result.ContentType = res.ContentType ?? "";
                    using (Stream input = scope.WrapResponseStream(res.GetResponseStream()))
                    using (FileStream output = new FileStream(filePath, FileMode.Create, FileAccess.Write, FileShare.None))
                    {
                        input.CopyTo(output, 81920);
                    }
                }
                scope.Complete();
                return Finish(result, scope);
            }
            catch (Exception ex)
            {
                result.Status = NetworkLedger.StatusFromException(ex);
                result.Error = result.Status > 0 ? ("HTTP " + result.Status) : ex.Message;
                scope.Fail(ex);
                return Finish(result, scope);
            }
        }

        private static HttpWebRequest CreateWebRequest(LedgerHttpRequest request, string method)
        {
            HttpWebRequest req = (HttpWebRequest)WebRequest.Create(request.Url);
            req.Method = method;
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
                result.Body = DecodeBody(ms.ToArray(), result.ContentType);
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

        private static LedgerHttpResponse Finish(LedgerHttpResponse result, NetworkRequestScope scope)
        {
            result.BytesSent = scope.BytesSent;
            result.BytesReceived = scope.BytesReceived;
            result.DurationMs = scope.ElapsedMs;
            return result;
        }
    }

    /// <summary>
    /// <see cref="HttpClient"/> handler that routes every request through the ledger:
    /// <c>new HttpClient(new LedgerHttpMessageHandler("HTTP Client", new HttpClientHandler()))</c>.
    /// Offline Mode refusals throw <see cref="NetworkBlockedException"/>. Response bytes are counted as the caller reads
    /// the content; the entry is recorded when the content is fully read or the response is disposed.
    /// </summary>
    public class LedgerHttpMessageHandler : DelegatingHandler
    {
        private readonly string feature;

        public LedgerHttpMessageHandler(string feature, HttpMessageHandler innerHandler)
            : base(innerHandler ?? new HttpClientHandler())
        {
            this.feature = string.IsNullOrEmpty(feature) ? "HTTP" : feature;
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
            string url = request.RequestUri != null ? request.RequestUri.ToString() : "";
            string method = request.Method != null ? request.Method.Method : "GET";
            NetworkRequestScope scope = NetworkLedger.Begin(f, method, url);
            HttpResponseMessage response;
            try
            {
                if (request.Content != null)
                {
                    await request.Content.LoadIntoBufferAsync().ConfigureAwait(false);
                    long? len = request.Content.Headers.ContentLength;
                    if (len.HasValue) scope.AddSent(len.Value);
                }
                response = await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                scope.Fail(ex);
                throw;
            }
            scope.Status = (int)response.StatusCode;
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
