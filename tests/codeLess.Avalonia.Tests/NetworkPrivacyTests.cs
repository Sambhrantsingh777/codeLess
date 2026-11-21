using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Headless.XUnit;
using codeLess.AvaloniaApp;
using codeLess.Studio;
using Xunit;

namespace codeLess.Avalonia.Tests
{
    /// <summary>NetworkLedger choke point, Offline Mode and the Network Activity UI (headless Avalonia).</summary>
    [Collection("SequentialWorkbench")]
    public class NetworkPrivacyTests : IDisposable
    {
        private readonly string _settingsFilePath;
        private readonly string _settingsBackupContent;
        private readonly bool _hadSettingsFile;

        public NetworkPrivacyTests()
        {
            _settingsFilePath = IdePersistentSettings.GetSettingsFilePath();
            _hadSettingsFile = !string.IsNullOrEmpty(_settingsFilePath) && File.Exists(_settingsFilePath);
            _settingsBackupContent = _hadSettingsFile ? File.ReadAllText(_settingsFilePath) : null;
            NetworkLedger.SetOfflineMode(false);
        }

        public void Dispose()
        {
            NetworkLedger.SetOfflineMode(false);
            try
            {
                if (_hadSettingsFile && _settingsBackupContent != null) File.WriteAllText(_settingsFilePath, _settingsBackupContent);
                else if (!_hadSettingsFile && !string.IsNullOrEmpty(_settingsFilePath) && File.Exists(_settingsFilePath)) File.Delete(_settingsFilePath);
            }
            catch { }
        }

        private static string UniqueFeature(string prefix) => prefix + " " + Guid.NewGuid().ToString("N").Substring(0, 8);

        private static List<NetworkLedgerEntry> EntriesFor(string feature) =>
            NetworkLedger.Snapshot().Where(e => e.Feature == feature).ToList();

        /// <summary>Minimal one-shot HTTP/1.1 server on 127.0.0.1 that answers every request with <paramref name="body"/>.</summary>
        internal static (int port, Task serverTask, TcpListener listener) StartLoopbackServerForTests(string body, int status = 200) => StartLoopbackServer(body, status);

        private static (int port, Task serverTask, TcpListener listener) StartLoopbackServer(string body, int status = 200)
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            int port = ((IPEndPoint)listener.LocalEndpoint).Port;
            var task = Task.Run(async () =>
            {
                try
                {
                    using var client = await listener.AcceptTcpClientAsync();
                    using var stream = client.GetStream();
                    var buf = new byte[8192];
                    var req = new StringBuilder();
                    int contentLength = 0;
                    while (true)
                    {
                        int n = await stream.ReadAsync(buf, 0, buf.Length);
                        if (n <= 0) break;
                        req.Append(Encoding.ASCII.GetString(buf, 0, n));
                        string s = req.ToString();
                        int hdrEnd = s.IndexOf("\r\n\r\n", StringComparison.Ordinal);
                        if (hdrEnd < 0) continue;
                        foreach (var line in s.Substring(0, hdrEnd).Split("\r\n"))
                            if (line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase))
                                int.TryParse(line.Substring(15).Trim(), out contentLength);
                        if (s.Length - (hdrEnd + 4) >= contentLength) break;
                    }
                    byte[] payload = Encoding.UTF8.GetBytes(body);
                    string head = $"HTTP/1.1 {status} {(status == 200 ? "OK" : "Error")}\r\nContent-Type: application/json\r\nContent-Length: {payload.Length}\r\nConnection: close\r\n\r\n";
                    byte[] headBytes = Encoding.ASCII.GetBytes(head);
                    await stream.WriteAsync(headBytes, 0, headBytes.Length);
                    await stream.WriteAsync(payload, 0, payload.Length);
                    await stream.FlushAsync();
                }
                catch { }
            });
            return (port, task, listener);
        }

        [AvaloniaFact]
        public void Loopback_Detection_Accepts_Local_Hosts_Only()
        {
            Assert.True(NetworkLedger.IsLoopback("http://localhost:11434/api/tags"));
            Assert.True(NetworkLedger.IsLoopback("http://127.0.0.1:1234/v1"));
            Assert.True(NetworkLedger.IsLoopback("http://127.5.6.7/"));
            Assert.True(NetworkLedger.IsLoopback("http://[::1]:8080/"));
            Assert.True(NetworkLedger.IsLoopback("http://dev.localhost/"));
            Assert.False(NetworkLedger.IsLoopback("https://api.openai.com/v1"));
            Assert.False(NetworkLedger.IsLoopback("http://127.evil.example/"));
            Assert.False(NetworkLedger.IsLoopback("http://192.168.1.10/"));
            Assert.False(NetworkLedger.IsLoopback("not a url"));
        }

        [AvaloniaFact]
        public void OfflineMode_Blocks_Remote_Allows_Loopback_And_Records_Blocked_Entry()
        {
            string feature = UniqueFeature("Test remote");
            NetworkLedger.SetOfflineMode(true);
            string reason;
            Assert.False(NetworkLedger.Check(feature, "GET", "https://api.github.com/repos/x/y", out reason));
            Assert.Contains("Offline Mode", reason);
            Assert.True(NetworkLedger.Check(feature, "GET", "http://127.0.0.1:11434/api/tags", out reason));

            var entries = EntriesFor(feature);
            Assert.Single(entries);
            Assert.True(entries[0].Blocked);
            Assert.Equal("api.github.com", entries[0].Host);
            Assert.Throws<NetworkBlockedException>(() => NetworkLedger.Begin(feature, "POST", "https://example.com/x"));

            NetworkLedger.SetOfflineMode(false);
            Assert.True(NetworkLedger.Check(feature, "GET", "https://api.github.com/repos/x/y", out reason));
        }

        [AvaloniaFact]
        public void Record_Redacts_Secrets_And_Totals_Aggregate_Per_Feature()
        {
            string feature = UniqueFeature("Test redact");
            NetworkLedger.Record(feature, "GET", "https://generativelanguage.googleapis.com/v1/models?key=SECRET123&alt=sse", 100, 2000, 200, "", 42);
            NetworkLedger.Record(feature, "POST", "https://generativelanguage.googleapis.com/v1/chat", 300, 500, 500, "", 8);

            var entries = EntriesFor(feature);
            Assert.Equal(2, entries.Count);
            Assert.DoesNotContain("SECRET123", entries[0].Url);
            Assert.Contains("alt=sse", entries[0].Url);

            var total = NetworkLedger.GetFeatureTotals().Single(t => t.Feature == feature);
            Assert.Equal(2, total.Requests);
            Assert.Equal(400, total.BytesSent);
            Assert.Equal(2500, total.BytesReceived);
            Assert.Contains("generativelanguage.googleapis.com", total.Hosts);

            string csv = NetworkLedger.ExportCsv(entries);
            string json = NetworkLedger.ExportJson(entries);
            Assert.Contains(feature, csv);
            Assert.Contains(feature, json);
            Assert.DoesNotContain("SECRET123", csv + json);
            Assert.True(NetworkLedger.MatchesFilter(entries[1], "googleapis"));
            Assert.False(NetworkLedger.MatchesFilter(entries[1], "no-such-host-xyz"));
        }

        [AvaloniaFact]
        public void StatusBar_Shield_Shows_Count_Toggles_Offline_And_Persists_Setting()
        {
            var window = new MainWindow();
            window.Show();
            try
            {
                Assert.NotNull(window.StatusNetworkButton);
                Assert.Contains("Click: Network Activity", global::Avalonia.Controls.ToolTip.GetTip(window.StatusNetworkButton) as string);

                window.ToggleOfflineMode();
                global::Avalonia.Threading.Dispatcher.UIThread.RunJobs();
                Assert.True(NetworkLedger.OfflineMode);
                Assert.True(IdePersistentSettings.Load().OfflineMode);
                var texts = global::Avalonia.VisualTree.VisualExtensions.GetVisualDescendants(window.StatusNetworkButton)
                    .OfType<global::Avalonia.Controls.TextBlock>().Select(t => t.Text).ToList();
                Assert.Contains("Offline", texts);

                window.ToggleOfflineMode();
                global::Avalonia.Threading.Dispatcher.UIThread.RunJobs();
                Assert.False(NetworkLedger.OfflineMode);
                Assert.False(IdePersistentSettings.Load().OfflineMode);
            }
            finally
            {
                window.Close();
            }
        }

        [AvaloniaFact]
        public void Network_Activity_Window_Lists_Filters_And_Clears_Entries_In_Every_Theme()
        {
            var window = new MainWindow();
            window.Show();
            try
            {
                string feature = UniqueFeature("Panel test");
                NetworkLedger.Record(feature, "GET", "https://open-vsx.org/api/-/search?query=python", 120, 4096, 200, "", 33);
                NetworkLedger.Record(feature, "GET", "https://api.github.com/repos/a/b/releases/latest", 90, 2048, 404, "", 12);

                window.ShowNetworkActivity();
                var panel = window.NetworkActivityWindowInstance;
                Assert.NotNull(panel);
                Assert.True(panel.IsVisible);
                var view = panel.View;
                view.RefreshNow();
                Assert.Contains(view.VisibleEntries, e => e.Feature == feature && e.Host == "open-vsx.org");
                Assert.Contains("request", view.SummaryText, StringComparison.OrdinalIgnoreCase);
                Assert.Contains("terminal", view.NoteText);
                Assert.True(view.TotalsRowCount >= 2);

                view.FilterBox.Text = "open-vsx";
                view.RefreshNow();
                Assert.All(view.VisibleEntries, e => Assert.True(NetworkLedger.MatchesFilter(e, "open-vsx")));
                Assert.DoesNotContain(view.VisibleEntries, e => e.Host == "api.github.com" && e.Feature == feature);

                // Second call focuses the same window instead of opening another one.
                window.ShowNetworkActivity();
                Assert.Same(panel, window.NetworkActivityWindowInstance);

                // Offline toggle in the panel drives the global mode.
                view.OfflineToggle.IsChecked = true;
                Assert.True(NetworkLedger.OfflineMode);
                view.OfflineToggle.IsChecked = false;
                Assert.False(NetworkLedger.OfflineMode);

                foreach (var theme in StudioTheme.GetBuiltInThemes())
                    window.ApplyTheme(theme, persistSetting: false);
                // Theme application is app-wide (styles / theme variant): restore the default for later tests.
                window.ApplyTheme(StudioTheme.GetById(StudioTheme.DefaultThemeId), persistSetting: false);

                view.FilterBox.Text = "";
                NetworkLedger.Clear();
                view.RefreshNow();
                Assert.Empty(view.VisibleEntries);

                panel.Close();
                Assert.Null(window.NetworkActivityWindowInstance);
            }
            finally
            {
                window.Close();
            }
        }
    }

    /// <summary>Offline-mode network tests that need no UI. Kept in their own class (they run on the headless UI thread like
    /// the rest of the collection so ledger events are raised in the session context).
    /// Same collection, so Offline Mode is never flipped while a UI test runs.</summary>
    [Collection("SequentialWorkbench")]
    public class NetworkPrivacyOfflineLogicTests : IDisposable
    {
        public NetworkPrivacyOfflineLogicTests() { NetworkLedger.SetOfflineMode(false); }
        public void Dispose() { NetworkLedger.SetOfflineMode(false); }

        private static string UniqueFeature(string prefix) => prefix + " " + Guid.NewGuid().ToString("N").Substring(0, 8);
        private static List<NetworkLedgerEntry> EntriesFor(string feature) =>
            NetworkLedger.Snapshot().Where(e => e.Feature == feature).ToList();
        private static (int port, Task serverTask, TcpListener listener) StartLoopbackServer(string body, int status = 200) =>
            NetworkPrivacyTests.StartLoopbackServerForTests(body, status);

        [Fact]
        public void LedgerHttp_Loopback_Request_Works_In_Offline_Mode_And_Counts_Bytes()
        {
            var (port, server, listener) = StartLoopbackServer("{\"ok\":true}");
            try
            {
                NetworkLedger.SetOfflineMode(true);
                string feature = UniqueFeature("Test loopback");
                var req = new LedgerHttpRequest { Feature = feature, Method = "POST", Url = $"http://127.0.0.1:{port}/api/chat", Body = Encoding.UTF8.GetBytes("{\"q\":1}"), ContentType = "application/json", TimeoutMs = 5000 };
                var res = LedgerHttp.Send(req);
                Assert.True(res.IsSuccess, res.Error);
                Assert.Equal("{\"ok\":true}", res.Body);

                var entry = EntriesFor(feature).Single();
                Assert.False(entry.Blocked);
                Assert.Equal(200, entry.Status);
                Assert.Equal(7, entry.BytesSent);
                Assert.True(entry.BytesReceived >= 11);
                Assert.True(entry.IsLoopback);
                Assert.True(server.Wait(5000));
            }
            finally
            {
                listener.Stop();
            }
        }

        [Fact]
        public void UpdateChecker_Is_Refused_In_Offline_Mode_Without_Sending()
        {
            NetworkLedger.SetOfflineMode(true);
            int before = NetworkLedger.Snapshot().Count(e => e.Feature == "Update check" && !e.Blocked);
            var r = UpdateChecker.Check("1.0.0", 3000);
            Assert.False(r.Success);
            Assert.True(r.Blocked);
            Assert.Contains("Offline Mode", r.Error);
            Assert.Equal(before, NetworkLedger.Snapshot().Count(e => e.Feature == "Update check" && !e.Blocked));
            Assert.True(NetworkLedger.Snapshot().Last(e => e.Feature == "Update check").Blocked);
        }

        [Fact]
        public async Task Cloud_AI_Is_Refused_In_Offline_Mode_With_Local_Model_Hint()
        {
            var engine = new LlmHarnessAndAgentEngine(new StudioSettings());
            var cloud = new ByokVendor { Id = "openai", Name = "OpenAI", BaseUrl = "https://api.openai.com/v1", SelectedModel = "gpt-test", ApiKey = "sk-test" };
            var local = new ByokVendor { Id = "ollama", Name = "Ollama", BaseUrl = "http://localhost:11434/v1", SelectedModel = "llama3" };
            NetworkLedger.SetOfflineMode(true);
            Assert.False(LlmHarnessAndAgentEngine.IsVendorReachableUnderOfflineMode(cloud));
            Assert.True(LlmHarnessAndAgentEngine.IsVendorReachableUnderOfflineMode(local));

            var msgs = new List<Dictionary<string, string>> { new Dictionary<string, string> { { "role", "user" }, { "content", "hi" } } };
            var ex = await Assert.ThrowsAsync<NetworkBlockedException>(() => engine.CallByokVendorMultiTurnAsync(cloud, "sys", msgs));
            Assert.Equal(NetworkLedger.OfflineAiMessage, ex.Message);
            var last = NetworkLedger.Snapshot().Last(e => e.Feature.StartsWith("AI: OpenAI", StringComparison.Ordinal));
            Assert.True(last.Blocked);
            Assert.Equal("api.openai.com", last.Host);
        }

    }
}
