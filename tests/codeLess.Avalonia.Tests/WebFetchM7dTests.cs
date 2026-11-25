using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Avalonia.Headless.XUnit;
using Xunit;
using codeLess.Studio;

namespace codeLess.Avalonia.Tests
{
    [Collection("SequentialWorkbench")]
    public class WebFetchM7dTests : IDisposable
    {
        private readonly string _testDir;

        public WebFetchM7dTests()
        {
            _testDir = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "m7d_webfetch_" + Guid.NewGuid().ToString("N")));
            Directory.CreateDirectory(_testDir);
            TestTrust.Trust(_testDir);
            NetworkLedger.SetOfflineMode(false);
            LedgerHttp.TransportOverride = null;
        }

        public void Dispose()
        {
            LedgerHttp.TransportOverride = null;
            NetworkLedger.SetOfflineMode(false);
            try
            {
                if (Directory.Exists(_testDir))
                    Directory.Delete(_testDir, true);
            }
            catch { }
        }

        [AvaloniaFact]
        public void WebFetch_OfflineMode_BlockedWithStandardMessage_AndRecordedInLedger()
        {
            bool transportCalled = false;
            LedgerHttp.TransportOverride = req =>
            {
                transportCalled = true;
                return new LedgerHttpResponse { Status = 200, Body = "should not reach" };
            };

            NetworkLedger.SetOfflineMode(true);
            var settings = new IdePersistentSettings { RequireAgentToolApproval = false, AgentPermissionWebfetch = "allow" };
            var engine = new LlmHarnessAndAgentEngine(settings);

            string targetUrl = "https://docs.example.com/guide/offline-test";
            var res = engine.ExecuteWorkspaceTool("webfetch", new Dictionary<string, object>
            {
                { "url", targetUrl },
                { "format", "text" }
            }, _testDir, new List<string>(), null, new Dictionary<string, string>(), "build", settings);

            Assert.False(res.Success);
            Assert.False(transportCalled);
            Assert.Contains("Offline Mode is on: codeLess did not contact docs.example.com", res.Output);

            var entry = NetworkLedger.Snapshot().LastOrDefault(e => e.Url == targetUrl && e.Feature == WebFetchTool.LedgerFeature);
            Assert.NotNull(entry);
            Assert.True(entry.Blocked);
            Assert.Equal(WebFetchTool.LedgerFeature, entry.Feature);
        }

        [AvaloniaFact]
        public void WebFetch_CapEnforced_RejectsOver5MB()
        {
            byte[] over5Mb = new byte[5 * 1024 * 1024 + 1024];
            for (int i = 0; i < over5Mb.Length; i++) over5Mb[i] = (byte)'a';

            LedgerHttp.TransportOverride = req => new LedgerHttpResponse
            {
                Status = 200,
                ContentType = "text/plain",
                BodyBytes = over5Mb
            };

            var settings = new IdePersistentSettings { RequireAgentToolApproval = false, AgentPermissionWebfetch = "allow" };
            var engine = new LlmHarnessAndAgentEngine(settings);

            var res = engine.ExecuteWorkspaceTool("webfetch", new Dictionary<string, object>
            {
                { "url", "https://example.com/huge.txt" },
                { "format", "text" }
            }, _testDir, new List<string>(), null, new Dictionary<string, string>(), "build", settings);

            Assert.False(res.Success);
            Assert.Contains("5MB", res.Output);
        }

        [AvaloniaFact]
        public void WebFetch_HtmlToText_And_HtmlToMarkdown_And_RecordsLedgerEntry()
        {
            string html = "<html><head><title>Ignored</title><script>alert('xss');</script><style>body{color:red}</style></head>" +
                          "<body><h1>Hello &amp; Welcome</h1><p>Read the <a href=\"https://example.com/docs\"><strong>docs</strong></a> now.</p>" +
                          "<ul><li>First item</li><li>Second item</li></ul></body></html>";

            string uniqueUrl = "https://example.com/docs/html-test";
            LedgerHttp.TransportOverride = req => new LedgerHttpResponse
            {
                Status = 200,
                ContentType = "text/html; charset=utf-8",
                Body = html,
                BodyBytes = Encoding.UTF8.GetBytes(html)
            };

            var settings = new IdePersistentSettings { RequireAgentToolApproval = false, AgentPermissionWebfetch = "allow" };
            var engine = new LlmHarnessAndAgentEngine(settings);

            var resText = engine.ExecuteWorkspaceTool("webfetch", new Dictionary<string, object>
            {
                { "url", uniqueUrl },
                { "format", "text" }
            }, _testDir, new List<string>(), null, new Dictionary<string, string>(), "build", settings);

            Assert.True(resText.Success);
            Assert.Contains("Hello & Welcome", resText.Output);
            Assert.Contains("Read the docs now.", resText.Output);
            Assert.DoesNotContain("alert(", resText.Output);
            Assert.DoesNotContain("color:red", resText.Output);
            Assert.DoesNotContain("<h1>", resText.Output);

            var resMd = engine.ExecuteWorkspaceTool("webfetch", new Dictionary<string, object>
            {
                { "url", uniqueUrl },
                { "format", "markdown" }
            }, _testDir, new List<string>(), null, new Dictionary<string, string>(), "build", settings);

            Assert.True(resMd.Success);
            Assert.Contains("# Hello & Welcome", resMd.Output);
            Assert.Contains("[**docs**](https://example.com/docs)", resMd.Output);
            Assert.Contains("- First item", resMd.Output);

            var ledgerEntries = NetworkLedger.Snapshot().Where(e => e.Url == uniqueUrl && e.Feature == WebFetchTool.LedgerFeature).ToList();
            Assert.Equal(2, ledgerEntries.Count);
            Assert.All(ledgerEntries, e =>
            {
                Assert.False(e.Blocked);
                Assert.Equal(200, e.Status);
                Assert.True(e.BytesReceived > 0);
            });
        }

        [AvaloniaFact]
        public void WebFetch_PermissionAskByDefault_DeniedDoesNotFetch_AndAllowedInPlanModeWithApproval()
        {
            bool fetched = false;
            LedgerHttp.TransportOverride = req =>
            {
                fetched = true;
                return new LedgerHttpResponse
                {
                    Status = 200,
                    ContentType = "text/plain",
                    Body = "plan content"
                };
            };

            // Default settings: AgentPermissionWebfetch is "ask" even when RequireAgentToolApproval is false
            var settings = new IdePersistentSettings { RequireAgentToolApproval = false };
            Assert.Equal("ask", settings.AgentPermissionWebfetch);

            var engine = new LlmHarnessAndAgentEngine(settings);
            bool approvalAsked = false;
            engine.OnRequestToolApproval = (tool, argsJson) =>
            {
                approvalAsked = true;
                return false; // user denies
            };

            var deniedRes = engine.ExecuteWorkspaceTool("webfetch", new Dictionary<string, object>
            {
                { "url", "https://example.com/secret" }
            }, _testDir, new List<string>(), null, new Dictionary<string, string>(), "build", settings);

            Assert.True(approvalAsked);
            Assert.False(deniedRes.Success);
            Assert.False(fetched, "Must not fetch when permission is denied");

            // In Plan mode: webfetch is read-only so it is NOT auto-denied; it still asks and succeeds when approved
            approvalAsked = false;
            engine.OnRequestToolApproval = (tool, argsJson) =>
            {
                approvalAsked = true;
                return true;
            };

            var planRes = engine.ExecuteWorkspaceTool("webfetch", new Dictionary<string, object>
            {
                { "url", "https://example.com/plan-doc" },
                { "format", "text" }
            }, _testDir, new List<string>(), null, new Dictionary<string, string>(), "plan", settings);

            Assert.True(approvalAsked);
            Assert.True(planRes.Success);
            Assert.True(fetched);
            Assert.Equal("plan content", planRes.Output);
        }

        [AvaloniaFact]
        public void WebFetch_RefusesCrossHostRedirect_AndNonHttpScheme()
        {
            LedgerHttp.TransportOverride = req => new LedgerHttpResponse
            {
                Status = 302,
                Headers = new List<KeyValuePair<string, string>>
                {
                    new KeyValuePair<string, string>("Location", "https://evil.example.org/steal")
                }
            };

            var settings = new IdePersistentSettings { RequireAgentToolApproval = false, AgentPermissionWebfetch = "allow" };
            var engine = new LlmHarnessAndAgentEngine(settings);

            var redirRes = engine.ExecuteWorkspaceTool("webfetch", new Dictionary<string, object>
            {
                { "url", "https://safe.example.com/start" }
            }, _testDir, new List<string>(), null, new Dictionary<string, string>(), "build", settings);

            Assert.False(redirRes.Success);
            Assert.Contains("evil.example.org", redirRes.Output);

            var ftpRes = engine.ExecuteWorkspaceTool("webfetch", new Dictionary<string, object>
            {
                { "url", "ftp://example.com/file" }
            }, _testDir, new List<string>(), null, new Dictionary<string, string>(), "build", settings);

            Assert.False(ftpRes.Success);
            Assert.Contains("http:// or https://", ftpRes.Output);
        }
    }
}
