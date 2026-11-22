using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Runtime.InteropServices;
using System.Text;
using System.Web.Script.Serialization;

namespace codeLess.Studio
{
    /// <summary>Result of an update check against the public GitHub Releases API.</summary>
    public class UpdateCheckResult
    {
        public bool Success;
        public bool IsNewer;
        public string LatestVersion = "";
        public string CurrentVersion = "";
        public string ReleasePageUrl = "";
        public string DownloadUrl = "";
        public string ReleaseNotes = "";
        public string Error = "";
        /// <summary>True when Offline Mode refused the request (nothing was sent).</summary>
        public bool Blocked;
        /// <summary>Every asset of the release (name, download URL, size).</summary>
        public List<UpdateReleaseAsset> Assets = new List<UpdateReleaseAsset>();

        /// <summary>Asset by exact file name (case-insensitive), or null.</summary>
        public UpdateReleaseAsset FindAsset(string name)
        {
            if (string.IsNullOrEmpty(name) || Assets == null) return null;
            foreach (UpdateReleaseAsset a in Assets)
                if (string.Equals(a.Name, name, StringComparison.OrdinalIgnoreCase)) return a;
            return null;
        }
    }

    /// <summary>One downloadable file of a GitHub release.</summary>
    public class UpdateReleaseAsset
    {
        public string Name = "";
        public string Url = "";
        public long Size;
    }

    /// <summary>
    /// Shared (Windows WinForms + macOS/Linux Avalonia) update checker. Compiled by legacy csc.exe: C# 5 only.
    /// Privacy: one anonymous HTTPS GET to api.github.com with a generic User-Agent; no identifiers are sent. Routed through
    /// NetworkLedger (shown in Network Activity, refused in Offline Mode).
    /// </summary>
    public static class UpdateChecker
    {
        public const string Repository = "Sambhrantsingh777/codeLess";
        public const string LatestReleasePage = "https://github.com/" + Repository + "/releases/latest";
        public const string LatestReleaseApi = "https://api.github.com/repos/" + Repository + "/releases/latest";

        /// <summary>Tests only: points the check at a local fake release API (null = GitHub).</summary>
        public static string ApiUrlOverride;

        /// <summary>Release asset the user should download for this OS / CPU (names produced by CI).</summary>
        public static string GetPlatformAssetName()
        {
            bool arm = RuntimeInformation.OSArchitecture == Architecture.Arm64;
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return "codeLess-Setup.exe";
            if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX)) return arm ? "codeLess-mac-arm64.dmg" : "codeLess-mac-x64.dmg";
            return arm ? "codeLess-linux-arm64.deb" : "codeLess-linux-x64.deb";
        }

        /// <summary>True when <paramref name="latest"/> is a higher version than <paramref name="current"/> ("v3.2.3" vs "3.2.2.0").</summary>
        public static bool IsNewerVersion(string latest, string current)
        {
            Version l, c;
            if (!TryParseVersion(latest, out l) || !TryParseVersion(current, out c)) return false;
            return l > c;
        }

        public static bool TryParseVersion(string text, out Version version)
        {
            version = null;
            if (string.IsNullOrEmpty(text)) return false;
            string t = text.Trim().TrimStart('v', 'V');
            int dash = t.IndexOfAny(new char[] { '-', '+', ' ' });
            if (dash >= 0) t = t.Substring(0, dash);
            string[] parts = t.Split('.');
            int[] nums = new int[] { 0, 0, 0, 0 };
            for (int i = 0; i < parts.Length && i < 4; i++)
            {
                int n;
                if (!int.TryParse(parts[i], out n)) return false;
                nums[i] = n;
            }
            version = new Version(nums[0], nums[1], nums[2], nums[3]);
            return true;
        }

        /// <summary>Blocking check (call from a worker thread).</summary>
        public static UpdateCheckResult Check(string currentVersion, int timeoutMs)
        {
            UpdateCheckResult r = new UpdateCheckResult();
            r.CurrentVersion = currentVersion ?? "";
            r.ReleasePageUrl = LatestReleasePage;
            try
            {
                LedgerHttpRequest req = new LedgerHttpRequest();
                req.Feature = "Update check";
                req.Url = string.IsNullOrEmpty(ApiUrlOverride) ? LatestReleaseApi : ApiUrlOverride;
                req.UserAgent = "codeLess-update-check";
                req.Accept = "application/vnd.github+json";
                req.TimeoutMs = timeoutMs;
                LedgerHttpResponse res = LedgerHttp.Send(req);
                if (res.Status == 0) { r.Error = "Network error: " + res.Error; return r; }
                if (!res.IsSuccess) { r.Error = "GitHub returned HTTP " + res.Status + "."; return r; }
                string json = res.Body;

                ParseRelease(json, r);
            }
            catch (NetworkBlockedException bex)
            {
                // Offline Mode: nothing was sent. The automatic check stays silent; Help > Check for Updates shows this.
                r.Blocked = true;
                r.Error = bex.Message;
            }
            catch (Exception ex)
            {
                r.Error = ex.Message;
            }
            return r;
        }

        /// <summary>Fills <paramref name="r"/> from a GitHub "latest release" JSON document (tag, notes, assets).</summary>
        public static void ParseRelease(string json, UpdateCheckResult r)
        {
            JavaScriptSerializer ser = new JavaScriptSerializer();
            ser.MaxJsonLength = 16 * 1024 * 1024;
            Dictionary<string, object> root = ser.Deserialize<Dictionary<string, object>>(json);
            if (root == null) { r.Error = "Unexpected response from GitHub."; return; }
            r.LatestVersion = root.ContainsKey("tag_name") && root["tag_name"] != null ? Convert.ToString(root["tag_name"]).TrimStart('v', 'V') : "";
            if (root.ContainsKey("html_url") && root["html_url"] != null) r.ReleasePageUrl = Convert.ToString(root["html_url"]);
            if (root.ContainsKey("body") && root["body"] != null) r.ReleaseNotes = Convert.ToString(root["body"]);

            r.Assets.Clear();
            if (root.ContainsKey("assets") && root["assets"] is IEnumerable)
            {
                foreach (object a in (IEnumerable)root["assets"])
                {
                    Dictionary<string, object> asset = a as Dictionary<string, object>;
                    if (asset == null || !asset.ContainsKey("name") || !asset.ContainsKey("browser_download_url")) continue;
                    UpdateReleaseAsset ra = new UpdateReleaseAsset();
                    ra.Name = Convert.ToString(asset["name"]);
                    ra.Url = Convert.ToString(asset["browser_download_url"]);
                    if (asset.ContainsKey("size") && asset["size"] != null)
                    {
                        long size;
                        if (long.TryParse(Convert.ToString(asset["size"], System.Globalization.CultureInfo.InvariantCulture), out size)) ra.Size = size;
                    }
                    r.Assets.Add(ra);
                }
            }
            UpdateReleaseAsset wanted = r.FindAsset(GetPlatformAssetName());
            r.DownloadUrl = wanted != null ? wanted.Url : r.ReleasePageUrl;
            r.IsNewer = IsNewerVersion(r.LatestVersion, r.CurrentVersion);
            r.Success = !string.IsNullOrEmpty(r.LatestVersion);
            if (!r.Success) r.Error = "No release information found.";
        }

        /// <summary>Opens a URL in the default browser (https only).</summary>
        public static void OpenInBrowser(string url)
        {
            if (string.IsNullOrEmpty(url) || !url.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) return;
            try
            {
                if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true });
                else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
                    System.Diagnostics.Process.Start("open", "\"" + url.Replace("\"", "") + "\"");
                else
                    System.Diagnostics.Process.Start("xdg-open", "\"" + url.Replace("\"", "") + "\"");
            }
            catch { }
        }
    }
}
