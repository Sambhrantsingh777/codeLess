using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace codeLess.Studio
{
    /// <summary>
    /// JSON-RPC 2.0 over a byte stream pair with LSP "Content-Length" framing. A dedicated reader thread parses
    /// incoming messages and a dedicated writer thread drains an outbound queue, so callers (the UI thread) never
    /// block on a full pipe or a slow server. C# 5 only.
    /// </summary>
    public sealed class LspJsonRpcConnection : IDisposable
    {
        private readonly Stream input;   // server stdout
        private readonly Stream output;  // server stdin
        private readonly BlockingCollection<byte[]> outbox = new BlockingCollection<byte[]>(new ConcurrentQueue<byte[]>());
        private Thread readerThread;
        private Thread writerThread;
        private volatile bool disposed;
        private int closedRaised;

        /// <summary>Raised on the reader thread for every decoded message (request, response or notification).</summary>
        public event Action<Dictionary<string, object>> MessageReceived;
        /// <summary>Raised once when the input stream ends or a fatal framing / IO error occurs.</summary>
        public event Action<string> Closed;
        /// <summary>Optional trace sink (one line per message direction, never the full payload).</summary>
        public Action<string> Trace;

        /// <summary>Maximum accepted message size (64 MB) so a broken server cannot exhaust memory.</summary>
        public const int MaxMessageBytes = 64 * 1024 * 1024;

        public LspJsonRpcConnection(Stream input, Stream output)
        {
            if (input == null) throw new ArgumentNullException("input");
            if (output == null) throw new ArgumentNullException("output");
            this.input = input;
            this.output = output;
        }

        public bool IsClosed { get { return closedRaised != 0 || disposed; } }

        public void Start()
        {
            readerThread = new Thread(ReadLoop);
            readerThread.IsBackground = true;
            readerThread.Name = "codeLess LSP reader";
            readerThread.Start();
            writerThread = new Thread(WriteLoop);
            writerThread.IsBackground = true;
            writerThread.Name = "codeLess LSP writer";
            writerThread.Start();
        }

        /// <summary>Queue a message (thread-safe, non-blocking).</summary>
        public void Send(Dictionary<string, object> message)
        {
            if (message == null || IsClosed) return;
            if (!message.ContainsKey("jsonrpc")) message["jsonrpc"] = "2.0";
            string json = LspJson.Serialize(message);
            try { outbox.Add(EncodeFrame(json)); }
            catch (InvalidOperationException) { } // completed for adding (disposed)
        }

        public static byte[] EncodeFrame(string json)
        {
            byte[] body = Encoding.UTF8.GetBytes(json);
            byte[] header = Encoding.ASCII.GetBytes("Content-Length: " + body.Length.ToString(CultureInfo.InvariantCulture) + "\r\n\r\n");
            byte[] frame = new byte[header.Length + body.Length];
            Buffer.BlockCopy(header, 0, frame, 0, header.Length);
            Buffer.BlockCopy(body, 0, frame, header.Length, body.Length);
            return frame;
        }

        /// <summary>
        /// Reads one framed message body from <paramref name="stream"/>. Returns null at end of stream.
        /// Tolerates extra headers (Content-Type) and both CRLF and bare LF header terminators.
        /// </summary>
        public static string ReadFrame(Stream stream)
        {
            int contentLength = -1;
            StringBuilder line = new StringBuilder();
            bool anyHeader = false;
            while (true)
            {
                int b = stream.ReadByte();
                if (b < 0) return null;
                if (b == '\n')
                {
                    string h = line.ToString().TrimEnd('\r');
                    line.Length = 0;
                    if (h.Length == 0)
                    {
                        if (!anyHeader) continue; // stray blank line between messages
                        break;
                    }
                    anyHeader = true;
                    int colon = h.IndexOf(':');
                    if (colon > 0 && string.Equals(h.Substring(0, colon).Trim(), "Content-Length", StringComparison.OrdinalIgnoreCase))
                    {
                        int len;
                        if (int.TryParse(h.Substring(colon + 1).Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out len))
                            contentLength = len;
                    }
                    continue;
                }
                if (line.Length > 8192) throw new InvalidDataException("LSP header line too long (server wrote non-protocol output to stdout?)");
                line.Append((char)b);
            }
            if (contentLength < 0) throw new InvalidDataException("LSP message without Content-Length header");
            if (contentLength > MaxMessageBytes) throw new InvalidDataException("LSP message too large: " + contentLength + " bytes");
            byte[] body = new byte[contentLength];
            int read = 0;
            while (read < contentLength)
            {
                int n = stream.Read(body, read, contentLength - read);
                if (n <= 0) return null;
                read += n;
            }
            return Encoding.UTF8.GetString(body, 0, contentLength);
        }

        private void ReadLoop()
        {
            string reason = "Server closed the connection";
            try
            {
                while (!disposed)
                {
                    string json = ReadFrame(input);
                    if (json == null) break;
                    Dictionary<string, object> msg;
                    try { msg = LspJson.Parse(json) as Dictionary<string, object>; }
                    catch (Exception pe)
                    {
                        if (Trace != null) Trace("[client] dropped malformed message: " + pe.Message);
                        continue;
                    }
                    if (msg == null) continue;
                    Action<Dictionary<string, object>> h = MessageReceived;
                    if (h != null)
                    {
                        try { h(msg); }
                        catch (Exception he)
                        {
                            if (Trace != null) Trace("[client] message handler error: " + he.Message);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                reason = "Connection error: " + ex.Message;
            }
            RaiseClosed(reason);
        }

        private void WriteLoop()
        {
            try
            {
                foreach (byte[] frame in outbox.GetConsumingEnumerable())
                {
                    output.Write(frame, 0, frame.Length);
                    output.Flush();
                }
            }
            catch (Exception ex)
            {
                RaiseClosed("Write failed: " + ex.Message);
            }
        }

        private void RaiseClosed(string reason)
        {
            if (Interlocked.Exchange(ref closedRaised, 1) != 0) return;
            try { outbox.CompleteAdding(); } catch { }
            Action<string> c = Closed;
            if (c != null)
            {
                try { c(reason); } catch { }
            }
        }

        /// <summary>Waits (bounded) until queued messages are written, e.g. to deliver "exit" before killing.</summary>
        public void DrainOutbox(int timeoutMs)
        {
            DateTime until = DateTime.UtcNow.AddMilliseconds(timeoutMs);
            while (outbox.Count > 0 && DateTime.UtcNow < until && !IsClosed)
                Thread.Sleep(10);
        }

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            try { outbox.CompleteAdding(); } catch { }
            try { output.Dispose(); } catch { }
            try { input.Dispose(); } catch { }
            RaiseClosed("Disposed");
        }
    }

    /// <summary>Error returned by the server for a request (JSON-RPC error object).</summary>
    public sealed class LspRequestException : Exception
    {
        public int Code { get; private set; }
        public LspRequestException(int code, string message) : base(message) { Code = code; }
        public const int RequestCancelled = -32800;
        public const int ContentModified = -32801;
        public const int ServerCancelled = -32802;
        public const int MethodNotFound = -32601;
    }

    /// <summary>
    /// Request / response correlation, cancellation ($/cancelRequest), notifications and server-to-client requests
    /// on top of <see cref="LspJsonRpcConnection"/>.
    /// </summary>
    public sealed class LspRpcEndpoint : IDisposable
    {
        private readonly LspJsonRpcConnection connection;
        private readonly ConcurrentDictionary<long, PendingRequest> pending = new ConcurrentDictionary<long, PendingRequest>();
        private long nextId;

        private sealed class PendingRequest
        {
            public TaskCompletionSource<object> Tcs;
            public string Method;
            public DateTime StartedUtc;
        }

        /// <summary>Server notifications (method, params). Raised on the reader thread.</summary>
        public event Action<string, object> NotificationReceived;
        /// <summary>Server-to-client requests: return the result object, or throw <see cref="LspRequestException"/>.
        /// Called on a thread-pool thread (may block, e.g. to marshal to the UI).</summary>
        public Func<string, object, object> RequestHandler;
        public event Action<string> Closed;
        public Action<string> Trace;

        public LspRpcEndpoint(LspJsonRpcConnection connection)
        {
            this.connection = connection;
            connection.MessageReceived += OnMessage;
            connection.Closed += OnClosed;
        }

        public bool IsClosed { get { return connection.IsClosed; } }
        public int PendingCount { get { return pending.Count; } }

        public void Start()
        {
            connection.Trace = Trace;
            connection.Start();
        }

        public void SendNotification(string method, object parameters)
        {
            Dictionary<string, object> msg = LspJson.Obj("jsonrpc", "2.0", "method", method);
            if (parameters != null) msg["params"] = parameters;
            if (Trace != null && method != "$/cancelRequest") Trace("--> " + method);
            connection.Send(msg);
        }

        /// <summary>
        /// Sends a request. The task completes with the "result" value (may be null), faults with
        /// <see cref="LspRequestException"/> / <see cref="TimeoutException"/>, or is cancelled when
        /// <paramref name="ct"/> fires (a $/cancelRequest is sent to the server).
        /// </summary>
        public Task<object> SendRequestAsync(string method, object parameters, CancellationToken ct, int timeoutMs)
        {
            TaskCompletionSource<object> tcs = new TaskCompletionSource<object>(TaskCreationOptions.RunContinuationsAsynchronously);
            if (connection.IsClosed)
            {
                tcs.TrySetException(new IOException("Language server is not running"));
                return tcs.Task;
            }
            long id = Interlocked.Increment(ref nextId);
            PendingRequest pr = new PendingRequest();
            pr.Tcs = tcs;
            pr.Method = method;
            pr.StartedUtc = DateTime.UtcNow;
            pending[id] = pr;

            Dictionary<string, object> msg = LspJson.Obj("jsonrpc", "2.0", "id", id, "method", method);
            if (parameters != null) msg["params"] = parameters;
            if (Trace != null) Trace("--> " + method + " #" + id.ToString(CultureInfo.InvariantCulture));
            connection.Send(msg);

            if (ct.CanBeCanceled)
            {
                CancellationTokenRegistration reg = ct.Register(delegate { Cancel(id, false); });
                tcs.Task.ContinueWith(delegate { reg.Dispose(); }, TaskContinuationOptions.ExecuteSynchronously);
            }
            if (timeoutMs > 0)
            {
                Timer timer = null;
                timer = new Timer(delegate
                {
                    try { timer.Dispose(); } catch { }
                    Cancel(id, true);
                }, null, timeoutMs, Timeout.Infinite);
                tcs.Task.ContinueWith(delegate { try { timer.Dispose(); } catch { } }, TaskContinuationOptions.ExecuteSynchronously);
            }
            return tcs.Task;
        }

        private void Cancel(long id, bool timedOut)
        {
            PendingRequest pr;
            if (!pending.TryRemove(id, out pr)) return;
            SendNotification("$/cancelRequest", LspJson.Obj("id", id));
            if (Trace != null) Trace("x-- " + pr.Method + " #" + id.ToString(CultureInfo.InvariantCulture) + (timedOut ? " timed out" : " cancelled"));
            if (timedOut) pr.Tcs.TrySetException(new TimeoutException(pr.Method + " timed out"));
            else pr.Tcs.TrySetCanceled();
        }

        private void OnMessage(Dictionary<string, object> msg)
        {
            bool hasId = msg.ContainsKey("id") && msg["id"] != null;
            string method = LspJson.GetString(msg, "method");
            if (method == null && hasId)
            {
                // Response
                long id;
                object rawId = msg["id"];
                if (rawId is long) id = (long)rawId;
                else if (!long.TryParse(Convert.ToString(rawId, CultureInfo.InvariantCulture), NumberStyles.Integer, CultureInfo.InvariantCulture, out id)) return;
                PendingRequest pr;
                if (!pending.TryRemove(id, out pr)) return; // cancelled / timed out already
                if (Trace != null)
                    Trace("<-- #" + id.ToString(CultureInfo.InvariantCulture) + " " + pr.Method + " (" + ((int)(DateTime.UtcNow - pr.StartedUtc).TotalMilliseconds).ToString(CultureInfo.InvariantCulture) + " ms)");
                Dictionary<string, object> err = LspJson.GetObject(msg, "error");
                if (err != null)
                {
                    int code = LspJson.GetInt(err, "code", 0);
                    string m = LspJson.GetString(err, "message") ?? "error";
                    if (code == LspRequestException.RequestCancelled) pr.Tcs.TrySetCanceled();
                    else pr.Tcs.TrySetException(new LspRequestException(code, m));
                }
                else
                {
                    pr.Tcs.TrySetResult(LspJson.Get(msg, "result"));
                }
                return;
            }
            if (method == null) return;
            object prms = LspJson.Get(msg, "params");
            if (hasId)
            {
                object reqId = msg["id"];
                if (Trace != null) Trace("<== " + method + " (server request)");
                // Server -> client request: answer off the reader thread (handlers may marshal to the UI).
                ThreadPool.QueueUserWorkItem(delegate
                {
                    Dictionary<string, object> resp = LspJson.Obj("jsonrpc", "2.0", "id", reqId);
                    try
                    {
                        Func<string, object, object> h = RequestHandler;
                        if (h == null) throw new LspRequestException(LspRequestException.MethodNotFound, "Unhandled method " + method);
                        resp["result"] = h(method, prms);
                    }
                    catch (LspRequestException lre)
                    {
                        resp["error"] = LspJson.Obj("code", lre.Code, "message", lre.Message);
                    }
                    catch (Exception ex)
                    {
                        resp["error"] = LspJson.Obj("code", -32603, "message", ex.Message);
                    }
                    connection.Send(resp);
                });
                return;
            }
            Action<string, object> n = NotificationReceived;
            if (n != null) n(method, prms);
        }

        private void OnClosed(string reason)
        {
            foreach (KeyValuePair<long, PendingRequest> kv in pending)
            {
                PendingRequest pr;
                if (pending.TryRemove(kv.Key, out pr))
                    pr.Tcs.TrySetException(new IOException("Language server connection closed: " + reason));
            }
            Action<string> c = Closed;
            if (c != null) c(reason);
        }

        public void DrainOutbox(int timeoutMs) { connection.DrainOutbox(timeoutMs); }

        public void Dispose()
        {
            connection.Dispose();
        }
    }
}
