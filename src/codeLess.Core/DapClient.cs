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
    /// Debug Adapter Protocol base framing: "Content-Length: N\r\n\r\n" followed by N bytes of UTF-8 JSON.
    /// Reads are byte-exact (the length counts bytes, not chars), so multi-byte text never desynchronizes the stream.
    /// </summary>
    public sealed class DapMessageReader
    {
        private readonly Stream stream;
        private readonly byte[] buf = new byte[16384];
        private int bufStart;
        private int bufEnd;

        public DapMessageReader(Stream stream)
        {
            this.stream = stream;
        }

        private bool Fill()
        {
            if (bufStart > 0 && bufStart == bufEnd) { bufStart = 0; bufEnd = 0; }
            if (bufEnd == buf.Length)
            {
                if (bufStart == 0) return true; // full; caller consumes
                Buffer.BlockCopy(buf, bufStart, buf, 0, bufEnd - bufStart);
                bufEnd -= bufStart;
                bufStart = 0;
            }
            int n = stream.Read(buf, bufEnd, buf.Length - bufEnd);
            if (n <= 0) return false;
            bufEnd += n;
            return true;
        }

        private string ReadHeaderLine()
        {
            StringBuilder line = new StringBuilder();
            while (true)
            {
                while (bufStart < bufEnd)
                {
                    byte b = buf[bufStart++];
                    if (b == (byte)'\n')
                    {
                        if (line.Length > 0 && line[line.Length - 1] == '\r') line.Length--;
                        return line.ToString();
                    }
                    line.Append((char)b);
                    if (line.Length > 8192) throw new InvalidDataException("DAP header line too long");
                }
                if (!Fill()) return line.Length > 0 ? line.ToString() : null;
            }
        }

        /// <summary>Blocks until a full message is read. Returns null at end of stream.</summary>
        /// <summary>Largest accepted message body (64 MB); larger Content-Length values end the stream.</summary>
        public const int MaxMessageBytes = 64 * 1024 * 1024;

        public string ReadMessage()
        {
            int contentLength = -1;
            while (true)
            {
                string header = ReadHeaderLine();
                if (header == null) return null;
                if (header.Length == 0)
                {
                    if (contentLength >= 0) break;
                    continue; // stray blank line between messages
                }
                int colon = header.IndexOf(':');
                if (colon > 0 && string.Equals(header.Substring(0, colon).Trim(), "Content-Length", StringComparison.OrdinalIgnoreCase))
                {
                    int len;
                    if (int.TryParse(header.Substring(colon + 1).Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out len) && len >= 0)
                        contentLength = len;
                }
            }
            // A corrupt / hostile header must not make us allocate gigabytes: treat oversized frames as a broken stream.
            if (contentLength > MaxMessageBytes) return null;
            byte[] body = new byte[contentLength];
            int got = 0;
            while (got < contentLength)
            {
                if (bufStart < bufEnd)
                {
                    int take = Math.Min(contentLength - got, bufEnd - bufStart);
                    Buffer.BlockCopy(buf, bufStart, body, got, take);
                    bufStart += take;
                    got += take;
                    continue;
                }
                bufStart = 0;
                bufEnd = 0;
                int n = stream.Read(buf, 0, buf.Length);
                if (n <= 0) return null;
                bufEnd = n;
            }
            return Encoding.UTF8.GetString(body);
        }

        public static byte[] Frame(string json)
        {
            byte[] body = Encoding.UTF8.GetBytes(json);
            byte[] header = Encoding.ASCII.GetBytes("Content-Length: " + body.Length.ToString(CultureInfo.InvariantCulture) + "\r\n\r\n");
            byte[] all = new byte[header.Length + body.Length];
            Buffer.BlockCopy(header, 0, all, 0, header.Length);
            Buffer.BlockCopy(body, 0, all, header.Length, body.Length);
            return all;
        }
    }

    /// <summary>A DAP response (success or failure). Failures are values, not exceptions, so callers can show them.</summary>
    public sealed class DapResponse
    {
        public bool Success;
        public string Command;
        public string Message;
        public Dictionary<string, object> Body;
        public Dictionary<string, object> Raw;

        public static DapResponse Failure(string command, string message)
        {
            DapResponse r = new DapResponse();
            r.Success = false;
            r.Command = command;
            r.Message = message;
            r.Body = new Dictionary<string, object>();
            return r;
        }

        /// <summary>Human-readable error: body.error.format (with {vars} substituted) or message.</summary>
        public string ErrorText
        {
            get
            {
                Dictionary<string, object> err = DapJson.Obj(Body, "error");
                string fmt = DapJson.Str(err, "format");
                if (!string.IsNullOrEmpty(fmt))
                {
                    Dictionary<string, object> vars = DapJson.Obj(err, "variables");
                    if (vars != null)
                        foreach (KeyValuePair<string, object> kv in vars)
                            fmt = fmt.Replace("{" + kv.Key + "}", Convert.ToString(kv.Value, CultureInfo.InvariantCulture));
                    return fmt;
                }
                return string.IsNullOrEmpty(Message) ? (Command + " failed") : Message;
            }
        }
    }

    /// <summary>
    /// Transport-agnostic DAP client: sequence numbers, request/response correlation, events and reverse requests
    /// (e.g. runInTerminal). One reader thread parses messages; events and reverse requests are dispatched in order on
    /// a separate dispatch thread so a slow handler can never stall response delivery.
    /// </summary>
    public sealed class DapClient : IDisposable
    {
        private readonly Stream input;
        private readonly Stream output;
        private readonly object writeLock = new object();
        private readonly object pendingLock = new object();
        private readonly Dictionary<int, TaskCompletionSource<DapResponse>> pending = new Dictionary<int, TaskCompletionSource<DapResponse>>();
        private readonly Dictionary<int, string> pendingCommands = new Dictionary<int, string>();
        private readonly BlockingCollection<Action> dispatchQueue = new BlockingCollection<Action>();
        private int seq;
        private volatile bool closed;
        private Thread readerThread;
        private Thread dispatchThread;

        /// <summary>(event name, body). Raised on the dispatch thread.</summary>
        public event Action<string, Dictionary<string, object>> EventReceived;

        /// <summary>Raised once when the stream ends or the client is disposed (argument: error, or null).</summary>
        public event Action<string> Closed;

        /// <summary>Optional protocol trace ("--> json" / "<-- json").</summary>
        public event Action<string> Trace;

        /// <summary>
        /// Reverse-request handler: (command, arguments) returns a task producing the response body. A faulted task
        /// (or null handler) produces an error response.
        /// </summary>
        public Func<string, Dictionary<string, object>, Task<Dictionary<string, object>>> ReverseRequestHandler;

        public int DefaultTimeoutMs = 30000;

        public bool IsClosed { get { return closed; } }

        public DapClient(Stream input, Stream output)
        {
            this.input = input;
            this.output = output;
        }

        private readonly System.Collections.Concurrent.BlockingCollection<byte[]> outbox = new System.Collections.Concurrent.BlockingCollection<byte[]>();
        private Thread writerThread;

        /// <summary>
        /// Writes queued frames on a dedicated thread: a debug adapter that stops reading stdin (hung, or a full pipe with
        /// a large setBreakpoints) can't block the caller - usually the UI thread - like the old synchronous Write did.
        /// </summary>
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
                Shutdown("write failed: " + ex.Message);
            }
        }

        public void Start()
        {
            writerThread = new Thread(WriteLoop);
            writerThread.IsBackground = true;
            writerThread.Name = "DapWriter";
            writerThread.Start();
            dispatchThread = new Thread(DispatchLoop);
            dispatchThread.IsBackground = true;
            dispatchThread.Name = "DapDispatch";
            dispatchThread.Start();
            readerThread = new Thread(ReadLoop);
            readerThread.IsBackground = true;
            readerThread.Name = "DapReader";
            readerThread.Start();
        }

        private void DispatchLoop()
        {
            try
            {
                foreach (Action a in dispatchQueue.GetConsumingEnumerable())
                {
                    try { a(); }
                    catch { }
                }
            }
            catch { }
        }

        private void Enqueue(Action a)
        {
            try { if (!dispatchQueue.IsAddingCompleted) dispatchQueue.Add(a); }
            catch { }
        }

        private void ReadLoop()
        {
            string error = null;
            try
            {
                DapMessageReader reader = new DapMessageReader(input);
                while (!closed)
                {
                    string json = reader.ReadMessage();
                    if (json == null) break;
                    Action<string> tr = Trace;
                    if (tr != null) { try { tr("<-- " + json); } catch { } }
                    Dictionary<string, object> msg;
                    try { msg = DapJson.ParseObject(json); }
                    catch { continue; }
                    HandleMessage(msg);
                }
            }
            catch (Exception ex)
            {
                if (!closed) error = ex.Message;
            }
            Shutdown(error);
        }

        private void HandleMessage(Dictionary<string, object> msg)
        {
            string type = DapJson.Str(msg, "type");
            if (type == "response")
            {
                int reqSeq = DapJson.Int(msg, "request_seq", -1);
                TaskCompletionSource<DapResponse> tcs = null;
                lock (pendingLock)
                {
                    if (pending.TryGetValue(reqSeq, out tcs))
                    {
                        pending.Remove(reqSeq);
                        pendingCommands.Remove(reqSeq);
                    }
                }
                if (tcs == null) return;
                DapResponse r = new DapResponse();
                r.Success = DapJson.Bool(msg, "success", false);
                r.Command = DapJson.Str(msg, "command");
                r.Message = DapJson.Str(msg, "message");
                r.Body = DapJson.Obj(msg, "body") ?? new Dictionary<string, object>();
                r.Raw = msg;
                tcs.TrySetResult(r);
            }
            else if (type == "event")
            {
                string ev = DapJson.Str(msg, "event") ?? "";
                Dictionary<string, object> body = DapJson.Obj(msg, "body") ?? new Dictionary<string, object>();
                Enqueue(delegate
                {
                    Action<string, Dictionary<string, object>> h = EventReceived;
                    if (h != null) h(ev, body);
                });
            }
            else if (type == "request")
            {
                int reqSeq = DapJson.Int(msg, "seq", 0);
                string command = DapJson.Str(msg, "command") ?? "";
                Dictionary<string, object> args = DapJson.Obj(msg, "arguments") ?? new Dictionary<string, object>();
                Enqueue(delegate { HandleReverseRequest(reqSeq, command, args); });
            }
        }

        private void HandleReverseRequest(int reqSeq, string command, Dictionary<string, object> args)
        {
            Func<string, Dictionary<string, object>, Task<Dictionary<string, object>>> h = ReverseRequestHandler;
            if (h == null)
            {
                SendReverseResponse(reqSeq, command, false, "Unsupported request: " + command, null);
                return;
            }
            Task<Dictionary<string, object>> t;
            try { t = h(command, args); }
            catch (Exception ex)
            {
                SendReverseResponse(reqSeq, command, false, ex.Message, null);
                return;
            }
            if (t == null)
            {
                SendReverseResponse(reqSeq, command, false, "Unsupported request: " + command, null);
                return;
            }
            t.ContinueWith(delegate (Task<Dictionary<string, object>> done)
            {
                if (done.IsFaulted || done.IsCanceled)
                {
                    string err = done.Exception != null ? done.Exception.GetBaseException().Message : "cancelled";
                    SendReverseResponse(reqSeq, command, false, err, null);
                }
                else
                {
                    SendReverseResponse(reqSeq, command, true, null, done.Result);
                }
            });
        }

        private void SendReverseResponse(int reqSeq, string command, bool success, string message, Dictionary<string, object> body)
        {
            Dictionary<string, object> m = new Dictionary<string, object>(StringComparer.Ordinal);
            m["seq"] = Interlocked.Increment(ref seq);
            m["type"] = "response";
            m["request_seq"] = reqSeq;
            m["success"] = success;
            m["command"] = command;
            if (message != null) m["message"] = message;
            m["body"] = body ?? new Dictionary<string, object>();
            try { Write(m); } catch { }
        }

        private void Write(Dictionary<string, object> m)
        {
            string json = DapJson.Serialize(m);
            Action<string> tr = Trace;
            if (tr != null) { try { tr("--> " + json); } catch { } }
            byte[] bytes = DapMessageReader.Frame(json);
            if (writerThread == null)
            {
                // Not started (tests drive the client directly): write synchronously.
                lock (writeLock)
                {
                    output.Write(bytes, 0, bytes.Length);
                    output.Flush();
                }
                return;
            }
            if (closed || outbox.IsAddingCompleted) throw new IOException("Debug adapter is not running");
            outbox.Add(bytes);
        }

        public Task<DapResponse> SendRequestAsync(string command, object arguments)
        {
            return SendRequestAsync(command, arguments, DefaultTimeoutMs);
        }

        /// <summary>
        /// Sends a request; the task completes with the response (Success=false on adapter error, timeout or closed
        /// connection). Never faults.
        /// </summary>
        public Task<DapResponse> SendRequestAsync(string command, object arguments, int timeoutMs)
        {
            TaskCompletionSource<DapResponse> tcs = new TaskCompletionSource<DapResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
            if (closed)
            {
                tcs.TrySetResult(DapResponse.Failure(command, "Debug adapter is not running"));
                return tcs.Task;
            }
            int mySeq = Interlocked.Increment(ref seq);
            Dictionary<string, object> m = new Dictionary<string, object>(StringComparer.Ordinal);
            m["seq"] = mySeq;
            m["type"] = "request";
            m["command"] = command;
            if (arguments != null) m["arguments"] = arguments;
            lock (pendingLock)
            {
                pending[mySeq] = tcs;
                pendingCommands[mySeq] = command;
            }
            if (closed)
            {
                // Closed between the first check and the registration: Shutdown may already have drained `pending`.
                bool mine;
                lock (pendingLock) { mine = pending.Remove(mySeq); pendingCommands.Remove(mySeq); }
                if (mine) tcs.TrySetResult(DapResponse.Failure(command, "Debug adapter is not running"));
                return tcs.Task;
            }
            try { Write(m); }
            catch (Exception ex)
            {
                lock (pendingLock) { pending.Remove(mySeq); pendingCommands.Remove(mySeq); }
                tcs.TrySetResult(DapResponse.Failure(command, "Failed to send '" + command + "': " + ex.Message));
                return tcs.Task;
            }
            if (timeoutMs > 0)
            {
                Timer timer = null;
                timer = new Timer(delegate
                {
                    bool removed;
                    lock (pendingLock)
                    {
                        removed = pending.Remove(mySeq);
                        pendingCommands.Remove(mySeq);
                    }
                    if (removed) tcs.TrySetResult(DapResponse.Failure(command, "Timed out waiting for '" + command + "' response"));
                    try { timer.Dispose(); } catch { }
                }, null, timeoutMs, Timeout.Infinite);
                tcs.Task.ContinueWith(delegate { try { timer.Dispose(); } catch { } });
            }
            return tcs.Task;
        }

        private void Shutdown(string error)
        {
            bool wasClosed = closed;
            closed = true;
            List<KeyValuePair<int, TaskCompletionSource<DapResponse>>> toFail = new List<KeyValuePair<int, TaskCompletionSource<DapResponse>>>();
            Dictionary<int, string> cmds;
            lock (pendingLock)
            {
                toFail.AddRange(pending);
                cmds = new Dictionary<int, string>(pendingCommands);
                pending.Clear();
                pendingCommands.Clear();
            }
            foreach (KeyValuePair<int, TaskCompletionSource<DapResponse>> kv in toFail)
            {
                string c;
                cmds.TryGetValue(kv.Key, out c);
                kv.Value.TrySetResult(DapResponse.Failure(c, "Debug adapter connection closed"));
            }
            if (!wasClosed)
            {
                Enqueue(delegate
                {
                    Action<string> h = Closed;
                    if (h != null) h(error);
                });
            }
            try { dispatchQueue.CompleteAdding(); } catch { }
            try { outbox.CompleteAdding(); } catch { }
        }

        public void Dispose()
        {
            if (closed) return;
            try { output.Dispose(); } catch { }
            try { input.Dispose(); } catch { }
            Shutdown(null);
        }
    }
}
