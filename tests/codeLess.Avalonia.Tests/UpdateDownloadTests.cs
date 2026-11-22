using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using Avalonia.Headless.XUnit;
using codeLess.Studio;
using Xunit;

namespace codeLess.Avalonia.Tests
{
    /// <summary>
    /// Update check + download against a local fake GitHub release (HttpListener on 127.0.0.1; no real GitHub calls):
    /// SHA-256 verification, refusal on mismatch / missing entry, progress, cancellation, Offline Mode refusal, ledger.
    /// Shares the Offline Mode / ledger statics with NetworkPrivacyTests, hence the sequential collection.
    /// </summary>
    [Collection("SequentialWorkbench")]
    public class UpdateDownloadTests : IDisposable
    {
        private readonly HttpListener listener = new HttpListener();
        private readonly string baseUrl;
        private readonly string dir = Path.Combine(Path.GetTempPath(), "codeLess_upd_dl_" + Guid.NewGuid().ToString("N").Substring(0, 8));
        private readonly Dictionary<string, byte[]> files = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        private readonly List<string> requests = new List<string>();
        private readonly Thread server;
        private volatile bool stopping;

        public UpdateDownloadTests()
        {
            Directory.CreateDirectory(dir);
            int port = FreePort();
            baseUrl = "http://127.0.0.1:" + port + "/";
            listener.Prefixes.Add(baseUrl);
            listener.Start();
            server = new Thread(Serve) { IsBackground = true };
            server.Start();
            NetworkLedger.SetOfflineMode(false);
        }

        public void Dispose()
        {
            stopping = true;
            UpdateChecker.ApiUrlOverride = null;
            NetworkLedger.SetOfflineMode(false);
            try { listener.Stop(); listener.Close(); } catch { }
            try { Directory.Delete(dir, true); } catch { }
        }

        private static int FreePort()
        {
            var l = new TcpListener(IPAddress.Loopback, 0);
            l.Start();
            int p = ((IPEndPoint)l.LocalEndpoint).Port;
            l.Stop();
            return p;
        }

        private void Serve()
        {
            while (!stopping)
            {
                HttpListenerContext ctx;
                try { ctx = listener.GetContext(); } catch { return; }
                try
                {
                    string path = ctx.Request.Url.AbsolutePath.TrimStart('/');
                    lock (requests) requests.Add(path);
                    byte[] body;
                    lock (files) files.TryGetValue(path, out body);
                    if (body == null) { ctx.Response.StatusCode = 404; ctx.Response.Close(); continue; }
                    ctx.Response.StatusCode = 200;
                    ctx.Response.ContentLength64 = body.Length;
                    ctx.Response.OutputStream.Write(body, 0, body.Length);
                    ctx.Response.Close();
                }
                catch { try { ctx.Response.Abort(); } catch { } }
            }
        }

        private static string Sha(byte[] data) => Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant();

        /// <summary>Publishes a fake "latest release" with one asset (+ SHA256SUMS.txt unless <paramref name="sums"/> is null).</summary>
        private UpdateCheckResult Publish(string version, string assetName, byte[] payload, string sums)
        {
            lock (files)
            {
                files["dl/" + assetName] = payload;
                if (sums != null) files["dl/SHA256SUMS.txt"] = Encoding.UTF8.GetBytes(sums);
                else files.Remove("dl/SHA256SUMS.txt");
            }
            string assets = "{\"name\":\"" + assetName + "\",\"size\":" + payload.Length + ",\"browser_download_url\":\"" + baseUrl + "dl/" + assetName + "\"}";
            if (sums != null) assets += ",{\"name\":\"SHA256SUMS.txt\",\"size\":100,\"browser_download_url\":\"" + baseUrl + "dl/SHA256SUMS.txt\"}";
            string json = "{\"tag_name\":\"v" + version + "\",\"html_url\":\"https://github.com/x/y/releases/tag/v" + version + "\",\"body\":\"Notes\",\"assets\":[" + assets + "]}";
            lock (files) files["api/latest"] = Encoding.UTF8.GetBytes(json);
            UpdateChecker.ApiUrlOverride = baseUrl + "api/latest";
            var r = UpdateChecker.Check("3.3.3", 5000);
            Assert.True(r.Success, r.Error);
            return r;
        }

        private static byte[] Payload(int size)
        {
            var b = new byte[size];
            new Random(42).NextBytes(b);
            return b;
        }

        [AvaloniaFact]
        public void Check_ParsesTheFakeRelease_AssetsAndNewerVersion()
        {
            var r = Publish("3.3.4", "codeLess-mac-arm64.zip", Payload(1000), "x");
            Assert.True(r.IsNewer);
            Assert.Equal("3.3.4", r.LatestVersion);
            Assert.NotNull(r.FindAsset("codeLess-mac-arm64.zip"));
            Assert.Equal(1000, r.FindAsset("codeLess-mac-arm64.zip").Size);
            Assert.NotNull(r.FindAsset("SHA256SUMS.txt"));
            Assert.Equal("Notes", r.ReleaseNotes);
        }

        [AvaloniaFact]
        public void Download_VerifiesSha256_ReportsProgress_AndGoesThroughTheLedger()
        {
            byte[] data = Payload(300_000);
            var r = Publish("3.3.4", "codeLess-linux-x64.tar.gz", data, Sha(data) + "  codeLess-linux-x64.tar.gz\n" + new string('1', 64) + "  other.zip\n");
            long last = -1, total = -1;
            int ledgerBefore = NetworkLedger.Snapshot().Count(e => e.Feature == UpdateDownloader.Feature);
            var d = UpdateDownloader.Download(r, "codeLess-linux-x64.tar.gz", dir, (done, t) => { last = done; total = t; }, null);
            Assert.True(d.Success, d.Error);
            Assert.Equal(Sha(data), UpdateAssets.ComputeSha256(d.FilePath));
            Assert.Equal(data.Length, last);
            Assert.Equal(data.Length, total);
            Assert.False(File.Exists(d.FilePath + ".partial"));
            Assert.True(NetworkLedger.Snapshot().Count(e => e.Feature == UpdateDownloader.Feature) >= ledgerBefore + 2); // sums + asset

            // a verified file from an earlier download is reused without downloading again
            int requestsBefore;
            lock (requests) requestsBefore = requests.Count(p => p == "dl/codeLess-linux-x64.tar.gz");
            var again = UpdateDownloader.Download(r, "codeLess-linux-x64.tar.gz", dir, null, null);
            Assert.True(again.Success, again.Error);
            lock (requests) Assert.Equal(requestsBefore, requests.Count(p => p == "dl/codeLess-linux-x64.tar.gz"));
        }

        [AvaloniaFact]
        public void Download_RefusesChecksumMismatch_MissingEntry_AndMissingSumsFile()
        {
            byte[] data = Payload(5000);
            var r = Publish("3.3.4", "codeLess.exe", data, new string('0', 64) + "  codeLess.exe\n");
            var d = UpdateDownloader.Download(r, "codeLess.exe", dir, null, null);
            Assert.False(d.Success);
            Assert.Contains("mismatch", d.Error, StringComparison.OrdinalIgnoreCase);
            Assert.False(File.Exists(Path.Combine(dir, "codeLess.exe")));
            Assert.False(File.Exists(Path.Combine(dir, "codeLess.exe.partial")));

            r = Publish("3.3.4", "codeLess.exe", data, Sha(data) + "  codeLess-Setup.exe\n");
            d = UpdateDownloader.Download(r, "codeLess.exe", dir, null, null);
            Assert.False(d.Success);
            Assert.Contains("no entry", d.Error);

            r = Publish("3.3.4", "codeLess.exe", data, null);
            d = UpdateDownloader.Download(r, "codeLess.exe", dir, null, null);
            Assert.False(d.Success);
            Assert.Contains("SHA256SUMS.txt", d.Error);
            Assert.False(File.Exists(Path.Combine(dir, "codeLess.exe")));
        }

        [AvaloniaFact]
        public void Download_CanBeCancelled_AndLeavesNoPartialFile()
        {
            byte[] data = Payload(2_000_000);
            var r = Publish("3.3.4", "codeLess-mac-x64.zip", data, Sha(data) + "  codeLess-mac-x64.zip\n");
            bool cancel = false;
            var d = UpdateDownloader.Download(r, "codeLess-mac-x64.zip", dir, (done, t) => { if (done > 0) cancel = true; }, () => cancel);
            Assert.False(d.Success);
            Assert.True(d.Cancelled);
            Assert.False(File.Exists(Path.Combine(dir, "codeLess-mac-x64.zip")));
            Assert.False(File.Exists(Path.Combine(dir, "codeLess-mac-x64.zip.partial")));
        }

        [AvaloniaFact]
        public void Download_IsRefusedInOfflineMode_WithoutSending()
        {
            var r = new UpdateCheckResult { Success = true, IsNewer = true, LatestVersion = "3.3.4" };
            r.Assets.Add(new UpdateReleaseAsset { Name = "codeLess-mac-arm64.zip", Url = "https://github.com/x/y/releases/download/v3.3.4/codeLess-mac-arm64.zip" });
            r.Assets.Add(new UpdateReleaseAsset { Name = "SHA256SUMS.txt", Url = "https://github.com/x/y/releases/download/v3.3.4/SHA256SUMS.txt" });
            NetworkLedger.SetOfflineMode(true);
            try
            {
                int sent = NetworkLedger.Snapshot().Count(e => e.Feature == UpdateDownloader.Feature && !e.Blocked);
                var d = UpdateDownloader.Download(r, "codeLess-mac-arm64.zip", dir, null, null);
                Assert.False(d.Success);
                Assert.True(d.Blocked);
                Assert.Equal(sent, NetworkLedger.Snapshot().Count(e => e.Feature == UpdateDownloader.Feature && !e.Blocked));
                Assert.True(NetworkLedger.Snapshot().Last(e => e.Feature == UpdateDownloader.Feature).Blocked);

                // the check itself is refused too (GitHub API URL, nothing sent)
                UpdateChecker.ApiUrlOverride = null;
                var c = UpdateChecker.Check("3.3.3", 2000);
                Assert.True(c.Blocked);
            }
            finally { NetworkLedger.SetOfflineMode(false); }
        }

        [AvaloniaFact]
        public void Service_Check_Download_Prepare_ReachesReady_WithManualPlanForDeb()
        {
            byte[] data = Payload(10_000);
            Publish("3.3.4", "codeLess-linux-x64.deb", data, Sha(data) + "  codeLess-linux-x64.deb\n");
            var env = new UpdateEnvironment { Os = UpdateOs.Linux, Arm64 = false, Kind = UpdateInstallKind.LinuxDeb, InstallPath = "/usr/lib/codeless" };
            var svc = new UpdateService("3.3.3", env, Path.Combine(dir, "work"));
            var states = new List<UpdateState>();
            svc.Changed += (s, e) => { lock (states) states.Add(svc.State); };
            var r = svc.CheckNow(5000);
            Assert.True(r.IsNewer);
            Assert.Equal(UpdateState.Available, svc.State);
            Assert.Equal("Download Update...", svc.MenuLabel);
            svc.DownloadWorker(svc.Release);
            Assert.Equal(UpdateState.Ready, svc.State);
            Assert.False(svc.Plan.CanInstall);
            Assert.Equal("Install Update...", svc.MenuLabel);
            Assert.Contains("sudo apt install", svc.Plan.ManualCommand);
            lock (states) Assert.Contains(UpdateState.Checking, states);

            // not newer: back to Idle
            Publish("3.3.3", "codeLess-linux-x64.deb", data, Sha(data) + "  codeLess-linux-x64.deb\n");
            var svc2 = new UpdateService("3.3.3", env, Path.Combine(dir, "work2"));
            Assert.False(svc2.CheckNow(5000).IsNewer);
            Assert.Equal(UpdateState.Idle, svc2.State);
            Assert.Equal("Check for Updates...", svc2.MenuLabel);
        }
    }
}
