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
    }

    /// <summary>
    /// Shared (Windows WinForms + macOS/Linux Avalonia) update checker. Compiled by legacy csc.exe: C# 5 only.
    /// Privacy: one anonymous HTTPS GET to api.github.com with a generic User-Agent; no identifiers are sent.
    /// </summary>
    public static class UpdateChecker
    {
        public const string Repository = "Sambhrantsingh777/codeLess";
        public const string LatestReleasePage = "https://github.com/" + Repository + "/releases/latest";

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
                try { ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12; } catch { }
                HttpWebRequest req = (HttpWebRequest)WebRequest.Create("https://api.github.com/repos/" + Repository + "/releases/latest");
                req.Method = "GET";
                req.UserAgent = "codeLess-update-check";
                req.Accept = "application/vnd.github+json";
                req.Timeout = timeoutMs;
                req.ReadWriteTimeout = timeoutMs;
                string json;
                using (HttpWebResponse res = (HttpWebResponse)req.GetResponse())
                using (StreamReader sr = new StreamReader(res.GetResponseStream(), Encoding.UTF8))
                    json = sr.ReadToEnd();

                JavaScriptSerializer ser = new JavaScriptSerializer();
                Dictionary<string, object> root = ser.Deserialize<Dictionary<string, object>>(json);
                if (root == null) { r.Error = "Unexpected response from GitHub."; return r; }
                r.LatestVersion = root.ContainsKey("tag_name") ? Convert.ToString(root["tag_name"]).TrimStart('v', 'V') : "";
                if (root.ContainsKey("html_url") && root["html_url"] != null) r.ReleasePageUrl = Convert.ToString(root["html_url"]);
                if (root.ContainsKey("body") && root["body"] != null) r.ReleaseNotes = Convert.ToString(root["body"]);

                string wanted = GetPlatformAssetName();
                if (root.ContainsKey("assets") && root["assets"] is IEnumerable)
                {
                    foreach (object a in (IEnumerable)root["assets"])
                    {
                        Dictionary<string, object> asset = a as Dictionary<string, object>;
                        if (asset == null || !asset.ContainsKey("name")) continue;
                        if (string.Equals(Convert.ToString(asset["name"]), wanted, StringComparison.OrdinalIgnoreCase) &&
                            asset.ContainsKey("browser_download_url"))
                        {
                            r.DownloadUrl = Convert.ToString(asset["browser_download_url"]);
                            break;
                        }
                    }
                }
                if (string.IsNullOrEmpty(r.DownloadUrl)) r.DownloadUrl = r.ReleasePageUrl;
                r.IsNewer = IsNewerVersion(r.LatestVersion, r.CurrentVersion);
                r.Success = !string.IsNullOrEmpty(r.LatestVersion);
                if (!r.Success) r.Error = "No release information found.";
            }
            catch (WebException wex)
            {
                HttpWebResponse hr = wex.Response as HttpWebResponse;
                r.Error = hr != null ? ("GitHub returned HTTP " + (int)hr.StatusCode + ".") : ("Network error: " + wex.Message);
            }
            catch (Exception ex)
            {
                r.Error = ex.Message;
            }
            return r;
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
