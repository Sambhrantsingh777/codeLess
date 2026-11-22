using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using codeLess.Studio;
using Xunit;

namespace codeLess.Avalonia.Tests
{
    /// <summary>
    /// QA #24: per-turn engine state (Stop flag, streaming handler, trace history, checkpoint hook) is never shared by
    /// two turns: a second turn on the same engine is refused with a clear reply, one-shot calls don't stream into or get
    /// cancelled by a running turn, and concurrent turns on isolated copies (Best-of-N) don't interfere.
    /// </summary>
    [Collection("SequentialWorkbench")] // NetworkLedger (global) records the local SSE requests
    public class AgentEngineConcurrencyTests
    {
        private static Task<AgentExecutionTurnResult> Turn(LlmHarnessAndAgentEngine e, string prompt) =>
            Task.Run(() => e.RunAutonomousAgentLoopAsync("composer", prompt, null, null, null, null, new List<string>(), new List<string>()));

        [Fact]
        public async Task SecondTurnOnTheSameEngine_IsRefusedClearly_AndDoesNotResetTheFirstTurnsStop()
        {
            var e = new LlmHarnessAndAgentEngine(null);
            var firstStarted = new TaskCompletionSource<bool>();
            var release = new TaskCompletionSource<string>();
            int calls = 0;
            e.CustomModelResponder = (sys, msgs) =>
            {
                if (Interlocked.Increment(ref calls) == 1) { firstStarted.TrySetResult(true); return release.Task; }
                return Task.FromResult("second step must not run after Stop");
            };
            var first = Turn(e, "first");
            await firstStarted.Task;
            Assert.True(e.IsTurnRunning);

            e.CancelStreamingRequested = true;                 // the user presses Stop on the first turn
            var second = await Turn(e, "second");              // meanwhile another turn is attempted
            Assert.True(LlmHarnessAndAgentEngine.IsBusyResult(second));
            Assert.Equal(LlmHarnessAndAgentEngine.BusyReply, second.AssistantReply);
            Assert.True(e.CancelStreamingRequested);          // the refused turn didn't reset the Stop

            release.SetResult("<tool_call>{\"tool\": \"grep_search\", \"args\": {\"query\": \"x\"}}</tool_call>");
            var r1 = await first;
            Assert.Equal(1, r1.Iterations);                    // Stop honoured: no second step
            Assert.False(e.IsTurnRunning);
            Assert.Equal(1, calls);

            e.CustomModelResponder = (sys, msgs) => Task.FromResult("done");
            var third = await Turn(e, "third");                // the engine is free again
            Assert.Equal("done", third.AssistantReply);
            Assert.Single(e.GetTraceHistorySnapshot().Where(t => t.RawResponsePreview == "done"));
        }

        [Fact]
        public async Task IsolatedCopies_RunConcurrently_WithSeparateStopAndTrace()
        {
            var main = new LlmHarnessAndAgentEngine(null);
            var gate = new TaskCompletionSource<bool>();
            main.CustomModelResponder = async (sys, msgs) => { await gate.Task; return "ok:" + msgs[0]["content"].Length; };
            var a = main.CreateIsolatedCopy();
            var b = main.CreateIsolatedCopy();
            var ta = Turn(a, "a");
            var tb = Turn(b, "b");
            await Task.Delay(50);
            Assert.True(a.IsTurnRunning && b.IsTurnRunning);
            Assert.False(main.IsTurnRunning);
            a.CancelStreamingRequested = true;
            Assert.False(b.CancelStreamingRequested);
            gate.SetResult(true);
            var ra = await ta;
            var rb = await tb;
            Assert.False(LlmHarnessAndAgentEngine.IsBusyResult(ra));
            Assert.False(LlmHarnessAndAgentEngine.IsBusyResult(rb));
            Assert.Single(a.GetTraceHistorySnapshot());
            Assert.Single(b.GetTraceHistorySnapshot());
            Assert.Empty(main.GetTraceHistorySnapshot());
        }

        [Fact]
        public async Task OneShotCalls_DoNotStreamIntoOrGetCancelledByARunningTurn()
        {
            using (var server = new FakeSseServer("Hello", " world"))
            {
                var e = new LlmHarnessAndAgentEngine(null);
                var vendor = new ByokVendor { Id = "openai", Name = "Local", BaseUrl = server.BaseUrl, ApiKey = "sk-local-test", SelectedModel = "m", Models = new[] { "m" } };
                var chunks = new List<string>();
                e.OnStreamingChunk += c => { lock (chunks) chunks.Add(c); };

                // Outside a turn (inline AI, commit messages, ...): no chat streaming, and a pending Stop doesn't apply.
                e.CancelStreamingRequested = true;
                string reply = await e.CallByokVendorAsync(vendor, "sys", "hi");
                Assert.Equal("Hello world", reply);
                Assert.Empty(chunks);

                // Inside a turn the same call streams to the chat.
                var turn = await Task.Run(() => e.RunAutonomousAgentLoopAsync("composer", "q", vendor, null, null, null, new List<string>(), new List<string>()));
                Assert.Equal("Hello world", turn.AssistantReply);
                Assert.Equal(new[] { "Hello", " world" }, chunks.ToArray());
            }
        }

        [Fact]
        public void SnapshotTab_IsDetachedFromTheLiveBuffer()
        {
            var live = new EditorTabBuffer { FilePath = "/w/a.cs", FileName = "a.cs", RelativePath = "a.cs", Language = "csharp", Content = "one\ntwo\n" };
            live.SyncLinesFromContent();
            live.Lines[0] = "ONE (typed, not yet synced)";
            live.CursorLine = 1; live.CursorCol = 1; live.SelectionAnchorLine = 1; live.SelectionAnchorCol = 3;
            live.Diagnostics.Add(new DiagnosticItem { Line = 1, Message = "m" });
            var snap = AgentTurnInputs.SnapshotTab(live);
            Assert.Equal("ONE (typed, not yet synced)\ntwo\n", snap.Content);
            Assert.Equal("wo", snap.GetSelectedText());
            live.Lines.Add("more");
            live.Diagnostics.Clear();
            Assert.Equal(3, snap.Lines.Count);
            Assert.Single(snap.Diagnostics);
            Assert.Equal("/w/a.cs", snap.FilePath);
        }
    }

    /// <summary>OpenAI-compatible SSE endpoint on 127.0.0.1 streaming fixed deltas.</summary>
    internal sealed class FakeSseServer : IDisposable
    {
        private readonly TcpListener listener;
        private readonly string[] deltas;
        public readonly int Port;

        public FakeSseServer(params string[] deltas)
        {
            this.deltas = deltas;
            listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            Port = ((IPEndPoint)listener.LocalEndpoint).Port;
            new Thread(Loop) { IsBackground = true }.Start();
        }

        public string BaseUrl => "http://127.0.0.1:" + Port + "/v1";

        private void Loop()
        {
            while (true)
            {
                TcpClient c;
                try { c = listener.AcceptTcpClient(); } catch { return; }
                ThreadPool.QueueUserWorkItem(_ => Handle(c));
            }
        }

        private void Handle(TcpClient c)
        {
            try
            {
                using (c)
                using (var ns = c.GetStream())
                {
                    var buf = new MemoryStream();
                    var one = new byte[8192];
                    int headerEnd = -1, contentLength = 0;
                    while (true)
                    {
                        int n = ns.Read(one, 0, one.Length);
                        if (n <= 0) break;
                        buf.Write(one, 0, n);
                        string sofar = Encoding.UTF8.GetString(buf.ToArray());
                        if (headerEnd < 0)
                        {
                            headerEnd = sofar.IndexOf("\r\n\r\n", StringComparison.Ordinal);
                            if (headerEnd >= 0)
                            {
                                var m = System.Text.RegularExpressions.Regex.Match(sofar.Substring(0, headerEnd), @"Content-Length:\s*(\d+)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                                if (m.Success) contentLength = int.Parse(m.Groups[1].Value);
                            }
                        }
                        if (headerEnd >= 0 && buf.Length >= headerEnd + 4 + contentLength) break;
                    }
                    var ser = new System.Web.Script.Serialization.JavaScriptSerializer();
                    var sb = new StringBuilder();
                    foreach (var d in deltas)
                        sb.Append("data: ").Append(ser.Serialize(new Dictionary<string, object> { { "choices", new object[] { new Dictionary<string, object> { { "delta", new Dictionary<string, object> { { "content", d } } } } } } })).Append("\n\n");
                    sb.Append("data: [DONE]\n\n");
                    byte[] bytes = Encoding.UTF8.GetBytes(sb.ToString());
                    byte[] hdr = Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nContent-Type: text/event-stream\r\nContent-Length: " + bytes.Length + "\r\nConnection: close\r\n\r\n");
                    ns.Write(hdr, 0, hdr.Length);
                    ns.Write(bytes, 0, bytes.Length);
                }
            }
            catch { }
        }

        public void Dispose()
        {
            try { listener.Stop(); } catch { }
        }
    }
}
