using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net;
using System.Text;
using System.Threading;

namespace codeLess.Studio
{
    /// <summary>One outbound network request made by codeLess itself (shown in the Network Activity panel).</summary>
    public class NetworkLedgerEntry
    {
        /// <summary>Monotonic id within this session (1, 2, 3, ...).</summary>
        public long Id;
        public DateTime TimestampUtc;
        /// <summary>Feature that made the request, e.g. "AI: Anthropic", "Update check", "OpenVSX search", "HTTP Client".</summary>
        public string Feature = "";
        public string Method = "GET";
        /// <summary>URL with secrets removed (user info dropped, values of key-like query parameters replaced by ***).</summary>
        public string Url = "";
        public string Host = "";
        /// <summary>URL path without query string.</summary>
        public string Path = "";
        /// <summary>Request body bytes written by codeLess (headers excluded).</summary>
        public long BytesSent;
        /// <summary>Response body bytes read by codeLess (headers excluded).</summary>
        public long BytesReceived;
        /// <summary>HTTP status code, 0 if the request failed before a response, -1 if blocked by Offline Mode.</summary>
        public int Status;
        public bool Blocked;
        /// <summary>Wall-clock duration from start of the request to the last byte read (0 when unknown).</summary>
        public long DurationMs;
        /// <summary>True when the request targeted this machine (localhost / 127.0.0.0/8 / ::1).</summary>
        public bool IsLoopback;
        public string Note = "";
    }

    /// <summary>Aggregated totals for one feature over the session.</summary>
    public class NetworkFeatureTotal
    {
        public string Feature = "";
        public int Requests;
        public int Blocked;
        public int Failed;
        public long BytesSent;
        public long BytesReceived;
        public long DurationMs;
        public List<string> Hosts = new List<string>();
    }

    /// <summary>Thrown by the ledger helpers when Offline Mode refuses a request. Message is suitable for the user.</summary>
    public class NetworkBlockedException : Exception
    {
        public string Feature;
        public string Url;

        public NetworkBlockedException(string message, string feature, string url)
            : base(message)
        {
            Feature = feature ?? "";
            Url = NetworkLedger.RedactUrl(url);
        }
    }

    /// <summary>
    /// Single choke point for every outbound request codeLess makes (shared by Windows and macOS/Linux; C# 5 only).
    /// Call <see cref="Check"/> before sending and <see cref="Record"/> after - or use <see cref="Begin(string,string,string)"/>,
    /// <see cref="LedgerHttp"/> or <see cref="LedgerHttpMessageHandler"/>, which do both and count bytes. Offline Mode blocks
    /// everything except loopback (local Ollama / LM Studio / language servers).
    /// Privacy: only method, redacted URL, sizes, status and duration are kept - never headers, API keys or bodies.
    /// </summary>
    public static class NetworkLedger
    {
        private static readonly object gate = new object();
        private static readonly List<NetworkLedgerEntry> entries = new List<NetworkLedgerEntry>();
        private static readonly Dictionary<string, NetworkFeatureTotal> totals = new Dictionary<string, NetworkFeatureTotal>(StringComparer.Ordinal);
        private static readonly Dictionary<string, int> hostCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        private static long nextId;
        private static int sessionRequests;
        private static int sessionBlocked;
        private static long sessionBytesSent;
        private static long sessionBytesReceived;
        private static int inFlight;
        private const int MaxEntries = 2000;

        /// <summary>Message shown in AI surfaces when a cloud provider is refused by Offline Mode.</summary>
        public const string OfflineAiMessage = "Offline Mode — only local models (Ollama/LM Studio on localhost) are available. Turn Offline Mode off (shield in the status bar, or Preferences) to use cloud providers.";

        /// <summary>Shown in the Network Activity panel: what the ledger does not see.</summary>
        public const string UntrackedNote = "Only requests made by codeLess itself are listed. Commands you run in the terminal, git (clone/fetch/push), package managers (npm, pip, dotnet restore), agent run_terminal commands and language servers are separate processes and are not tracked here (Offline Mode does not block them either).";

        /// <summary>When true, all non-loopback requests are refused.</summary>
        public static bool OfflineMode;

        /// <summary>Raised (on the calling thread) after an entry is added; UIs must marshal to their UI thread.</summary>
        public static event Action<NetworkLedgerEntry> EntryAdded;

        /// <summary>Raised when <see cref="OfflineMode"/> is changed through <see cref="SetOfflineMode"/>.</summary>
        public static event Action<bool> OfflineModeChanged;

        /// <summary>Raised after <see cref="Clear"/>.</summary>
        public static event Action Cleared;

        /// <summary>Raised when a request starts or finishes (argument: number of requests currently in flight).</summary>
        public static event Action<int> InFlightChanged;

        public static void SetOfflineMode(bool offline)
        {
            if (OfflineMode == offline) return;
            OfflineMode = offline;
            Action<bool> h = OfflineModeChanged;
            if (h != null)
            {
                try { h(offline); } catch { }
            }
        }

        /// <summary>True if the URL targets this machine (localhost, *.localhost, 127.0.0.0/8, ::1, ::ffff:127.x).</summary>
        public static bool IsLoopback(string url)
        {
            Uri u;
            if (string.IsNullOrEmpty(url) || !Uri.TryCreate(url, UriKind.Absolute, out u)) return false;
            if (u.IsFile) return false;
            return IsLoopbackHost(u.Host);
        }

        /// <summary>Host-name form of <see cref="IsLoopback"/> (accepts "[::1]" and "localhost.").</summary>
        public static bool IsLoopbackHost(string host)
        {
            if (string.IsNullOrEmpty(host)) return false;
            string h = host.Trim().ToLowerInvariant();
            if (h.StartsWith("[") && h.EndsWith("]")) h = h.Substring(1, h.Length - 2);
            h = h.TrimEnd('.');
            if (h.Length == 0) return false;
            if (h == "localhost" || h.EndsWith(".localhost")) return true;
            // Only real IP literals: "127.evil.example" must NOT count as loopback.
            IPAddress ip;
            if (IPAddress.TryParse(h, out ip))
            {
                if (IPAddress.IsLoopback(ip)) return true;
                if (ip.IsIPv4MappedToIPv6)
                {
                    try { return IPAddress.IsLoopback(ip.MapToIPv4()); } catch { }
                }
            }
            return false;
        }

        /// <summary>
        /// Returns true when the request may be sent. When it is blocked (Offline Mode, non-loopback) a blocked entry is
        /// recorded and <paramref name="reason"/> explains why, suitable for showing to the user.
        /// </summary>
        public static bool Check(string feature, string method, string url, out string reason)
        {
            reason = null;
            if (!OfflineMode || IsLoopback(url)) return true;
            reason = "Offline Mode is on: codeLess did not contact " + HostOf(url) + ". Turn off Offline Mode to allow network access.";
            NetworkLedgerEntry e = new NetworkLedgerEntry();
            e.Feature = feature ?? "";
            e.Method = method ?? "GET";
            e.Url = url ?? "";
            e.Status = -1;
            e.Blocked = true;
            e.Note = "Blocked by Offline Mode";
            Add(e);
            return false;
        }

        /// <summary>Like <see cref="Check"/> but throws <see cref="NetworkBlockedException"/> when blocked.</summary>
        public static void EnsureAllowed(string feature, string method, string url)
        {
            EnsureAllowed(feature, method, url, null);
        }

        /// <summary>Throws <see cref="NetworkBlockedException"/> (with <paramref name="blockedMessage"/> if given) when blocked.</summary>
        public static void EnsureAllowed(string feature, string method, string url, string blockedMessage)
        {
            string reason;
            if (!Check(feature, method, url, out reason))
                throw new NetworkBlockedException(string.IsNullOrEmpty(blockedMessage) ? reason : blockedMessage, feature, url);
        }

        public static void Record(string feature, string method, string url, long bytesSent, long bytesReceived, int status, string note)
        {
            Record(feature, method, url, bytesSent, bytesReceived, status, note, 0);
        }

        public static void Record(string feature, string method, string url, long bytesSent, long bytesReceived, int status, string note, long durationMs)
        {
            NetworkLedgerEntry e = new NetworkLedgerEntry();
            e.Feature = feature ?? "";
            e.Method = method ?? "GET";
            e.Url = url ?? "";
            e.BytesSent = Math.Max(0, bytesSent);
            e.BytesReceived = Math.Max(0, bytesReceived);
            e.Status = status;
            e.DurationMs = Math.Max(0, durationMs);
            e.Note = note ?? "";
            Add(e);
        }

        /// <summary>
        /// Checks the request (throws <see cref="NetworkBlockedException"/> when Offline Mode refuses it) and returns a scope
        /// that times the request, counts bytes and records exactly one entry when completed or disposed.
        /// </summary>
        public static NetworkRequestScope Begin(string feature, string method, string url)
        {
            return Begin(feature, method, url, null);
        }

        public static NetworkRequestScope Begin(string feature, string method, string url, string blockedMessage)
        {
            EnsureAllowed(feature, method, url, blockedMessage);
            return new NetworkRequestScope(feature, method, url);
        }

        /// <summary>Non-throwing form of <see cref="Begin(string,string,string)"/>.</summary>
        public static bool TryBegin(string feature, string method, string url, out NetworkRequestScope scope, out string reason)
        {
            scope = null;
            if (!Check(feature, method, url, out reason)) return false;
            scope = new NetworkRequestScope(feature, method, url);
            return true;
        }

        public static List<NetworkLedgerEntry> Snapshot()
        {
            lock (gate) return new List<NetworkLedgerEntry>(entries);
        }

        public static void Clear()
        {
            lock (gate)
            {
                entries.Clear();
                totals.Clear();
                hostCounts.Clear();
                sessionRequests = 0;
                sessionBlocked = 0;
                sessionBytesSent = 0;
                sessionBytesReceived = 0;
            }
            Action h = Cleared;
            if (h != null)
            {
                try { h(); } catch { }
            }
        }

        /// <summary>Requests (sent or blocked) since launch or the last <see cref="Clear"/>; not limited by the table cap.</summary>
        public static int SessionRequestCount
        {
            get { lock (gate) return sessionRequests; }
        }

        public static int SessionBlockedCount
        {
            get { lock (gate) return sessionBlocked; }
        }

        public static long SessionBytesSent
        {
            get { lock (gate) return sessionBytesSent; }
        }

        public static long SessionBytesReceived
        {
            get { lock (gate) return sessionBytesReceived; }
        }

        public static int InFlightCount
        {
            get { return Thread.VolatileRead(ref inFlight); }
        }

        /// <summary>codeLess has no telemetry. A feature named "Telemetry..." would be counted here (and must never exist).</summary>
        public static bool IsTelemetryFeature(string feature)
        {
            return !string.IsNullOrEmpty(feature) && feature.StartsWith("Telemetry", StringComparison.OrdinalIgnoreCase);
        }

        public static int TelemetryRequestCount
        {
            get
            {
                lock (gate)
                {
                    int n = 0;
                    foreach (KeyValuePair<string, NetworkFeatureTotal> kv in totals)
                        if (IsTelemetryFeature(kv.Key)) n += kv.Value.Requests;
                    return n;
                }
            }
        }

        /// <summary>Per-feature totals for the session, most requests first.</summary>
        public static List<NetworkFeatureTotal> GetFeatureTotals()
        {
            List<NetworkFeatureTotal> list = new List<NetworkFeatureTotal>();
            lock (gate)
            {
                foreach (KeyValuePair<string, NetworkFeatureTotal> kv in totals)
                {
                    NetworkFeatureTotal t = kv.Value;
                    NetworkFeatureTotal c = new NetworkFeatureTotal();
                    c.Feature = t.Feature;
                    c.Requests = t.Requests;
                    c.Blocked = t.Blocked;
                    c.Failed = t.Failed;
                    c.BytesSent = t.BytesSent;
                    c.BytesReceived = t.BytesReceived;
                    c.DurationMs = t.DurationMs;
                    c.Hosts = new List<string>(t.Hosts);
                    list.Add(c);
                }
            }
            list.Sort(delegate(NetworkFeatureTotal a, NetworkFeatureTotal b)
            {
                int r = b.Requests.CompareTo(a.Requests);
                return r != 0 ? r : string.Compare(a.Feature, b.Feature, StringComparison.OrdinalIgnoreCase);
            });
            return list;
        }

        /// <summary>Hosts actually contacted (blocked requests excluded), most-used first.</summary>
        public static List<string> GetContactedHosts()
        {
            List<KeyValuePair<string, int>> pairs;
            lock (gate) pairs = new List<KeyValuePair<string, int>>(hostCounts);
            pairs.Sort(delegate(KeyValuePair<string, int> a, KeyValuePair<string, int> b)
            {
                int r = b.Value.CompareTo(a.Value);
                return r != 0 ? r : string.Compare(a.Key, b.Key, StringComparison.OrdinalIgnoreCase);
            });
            List<string> hosts = new List<string>();
            foreach (KeyValuePair<string, int> p in pairs) hosts.Add(p.Key);
            return hosts;
        }

        /// <summary>One-line summary, e.g. "12 requests this session, all to api.anthropic.com · 0 telemetry".</summary>
        public static string GetSummary()
        {
            int requests, blocked, telemetry = TelemetryRequestCount;
            lock (gate)
            {
                requests = sessionRequests;
                blocked = sessionBlocked;
            }
            int sent = requests - blocked;
            List<string> hosts = GetContactedHosts();
            StringBuilder sb = new StringBuilder();
            if (OfflineMode) sb.Append("Offline Mode on · ");
            if (sent <= 0)
            {
                sb.Append("No network requests this session");
            }
            else
            {
                sb.Append(sent).Append(sent == 1 ? " request" : " requests").Append(" this session, ");
                if (hosts.Count == 1) sb.Append(sent == 1 ? "to " : "all to ").Append(hosts[0]);
                else if (hosts.Count <= 3) sb.Append("to ").Append(JoinHuman(hosts));
                else sb.Append("to ").Append(hosts.Count).Append(" hosts");
            }
            if (blocked > 0) sb.Append(" · ").Append(blocked).Append(" blocked by Offline Mode");
            sb.Append(" · ").Append(telemetry).Append(" telemetry");
            return sb.ToString();
        }

        private static string JoinHuman(List<string> items)
        {
            if (items.Count == 0) return "";
            if (items.Count == 1) return items[0];
            return string.Join(", ", items.GetRange(0, items.Count - 1).ToArray()) + " and " + items[items.Count - 1];
        }

        // ------------------------------------------------------------------ redaction

        private static readonly string[] SecretParamExact = new string[]
        {
            "key", "apikey", "api_key", "api-key", "auth", "authorization", "code", "sig", "signature", "session", "sessionid",
            "jwt", "bearer", "assertion", "credential", "credentials", "pass", "pwd", "passwd", "password", "secret", "token"
        };

        private static readonly string[] SecretParamParts = new string[]
        {
            "token", "secret", "password", "passwd", "apikey", "api_key", "api-key", "access_key", "private_key", "signature", "credential"
        };

        /// <summary>True for query-parameter names whose values must never be logged (key, token, secret, signature, ...).</summary>
        public static bool IsSecretQueryParameter(string name)
        {
            if (string.IsNullOrEmpty(name)) return false;
            string n = name.Trim().ToLowerInvariant();
            for (int i = 0; i < SecretParamExact.Length; i++)
                if (n == SecretParamExact[i]) return true;
            for (int i = 0; i < SecretParamParts.Length; i++)
                if (n.Contains(SecretParamParts[i])) return true;
            if (n.EndsWith("_key") || n.EndsWith("-key") || n.EndsWith(".key") || (n.EndsWith("key") && n.Length <= 8)) return true;
            if (n.StartsWith("x-amz-") || n.StartsWith("x-goog-")) return true;
            return false;
        }

        /// <summary>
        /// Removes secrets from a URL before it is stored: drops user info and the fragment and replaces values of key-like
        /// query parameters with "***". Non-URL strings are returned unchanged (minus anything after '?').
        /// </summary>
        public static string RedactUrl(string url)
        {
            if (string.IsNullOrEmpty(url)) return url ?? "";
            string s = url.Trim();
            int hash = s.IndexOf('#');
            if (hash >= 0) s = s.Substring(0, hash);
            string query = null;
            int q = s.IndexOf('?');
            if (q >= 0)
            {
                query = s.Substring(q + 1);
                s = s.Substring(0, q);
            }
            // user:password@host
            int scheme = s.IndexOf("://", StringComparison.Ordinal);
            if (scheme > 0)
            {
                int authStart = scheme + 3;
                int pathStart = s.IndexOf('/', authStart);
                string authority = pathStart >= 0 ? s.Substring(authStart, pathStart - authStart) : s.Substring(authStart);
                int at = authority.LastIndexOf('@');
                if (at >= 0)
                {
                    string rest = pathStart >= 0 ? s.Substring(pathStart) : "";
                    s = s.Substring(0, authStart) + authority.Substring(at + 1) + rest;
                }
            }
            if (query == null) return s;
            if (query.Length == 0) return s + "?";
            string[] pairs = query.Split('&');
            StringBuilder sb = new StringBuilder(s);
            sb.Append('?');
            for (int i = 0; i < pairs.Length; i++)
            {
                if (i > 0) sb.Append('&');
                string p = pairs[i];
                int eq = p.IndexOf('=');
                string name = eq >= 0 ? p.Substring(0, eq) : p;
                string decodedName = name;
                try { decodedName = Uri.UnescapeDataString(name.Replace('+', ' ')); } catch { }
                if (eq >= 0 && IsSecretQueryParameter(decodedName)) sb.Append(name).Append("=***");
                else sb.Append(p);
            }
            return sb.ToString();
        }

        public static string HostOf(string url)
        {
            Uri u;
            if (!string.IsNullOrEmpty(url) && Uri.TryCreate(url, UriKind.Absolute, out u)) return u.Host;
            return url ?? "";
        }

        /// <summary>Path part of a URL without query or fragment ("/v1/messages").</summary>
        public static string PathOf(string url)
        {
            Uri u;
            if (!string.IsNullOrEmpty(url) && Uri.TryCreate(url, UriKind.Absolute, out u)) return u.AbsolutePath;
            return "";
        }

        /// <summary>Short status text for an exception (never includes headers or bodies).</summary>
        public static int StatusFromException(Exception ex)
        {
            WebException wex = ex as WebException;
            if (wex != null)
            {
                HttpWebResponse hr = wex.Response as HttpWebResponse;
                if (hr != null)
                {
                    try { return (int)hr.StatusCode; } catch { }
                }
            }
            return 0;
        }

        public static string NoteFromException(Exception ex)
        {
            if (ex == null) return "";
            if (ex is NetworkBlockedException) return "Blocked by Offline Mode";
            WebException wex = ex as WebException;
            if (wex != null)
            {
                if (wex.Status == WebExceptionStatus.RequestCanceled) return "Cancelled";
                if (wex.Status == WebExceptionStatus.ProtocolError) return "HTTP error";
                return wex.Status.ToString();
            }
            if (ex is OperationCanceledException) return "Cancelled";
            if (ex is TimeoutException) return "Timeout";
            return ex.GetType().Name.Replace("Exception", "");
        }

        // ------------------------------------------------------------------ export

        public static string ExportJson(List<NetworkLedgerEntry> list)
        {
            if (list == null) list = Snapshot();
            StringBuilder sb = new StringBuilder();
            sb.Append("{\n  \"summary\": ").Append(JsonString(GetSummary())).Append(",\n");
            sb.Append("  \"offlineMode\": ").Append(OfflineMode ? "true" : "false").Append(",\n");
            sb.Append("  \"exportedUtc\": ").Append(JsonString(DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture))).Append(",\n");
            sb.Append("  \"entries\": [");
            for (int i = 0; i < list.Count; i++)
            {
                NetworkLedgerEntry e = list[i];
                sb.Append(i == 0 ? "\n" : ",\n");
                sb.Append("    {");
                sb.Append("\"id\": ").Append(e.Id.ToString(CultureInfo.InvariantCulture));
                sb.Append(", \"timeUtc\": ").Append(JsonString(e.TimestampUtc.ToString("o", CultureInfo.InvariantCulture)));
                sb.Append(", \"feature\": ").Append(JsonString(e.Feature));
                sb.Append(", \"method\": ").Append(JsonString(e.Method));
                sb.Append(", \"host\": ").Append(JsonString(e.Host));
                sb.Append(", \"path\": ").Append(JsonString(e.Path));
                sb.Append(", \"url\": ").Append(JsonString(e.Url));
                sb.Append(", \"bytesSent\": ").Append(e.BytesSent.ToString(CultureInfo.InvariantCulture));
                sb.Append(", \"bytesReceived\": ").Append(e.BytesReceived.ToString(CultureInfo.InvariantCulture));
                sb.Append(", \"status\": ").Append(e.Status.ToString(CultureInfo.InvariantCulture));
                sb.Append(", \"durationMs\": ").Append(e.DurationMs.ToString(CultureInfo.InvariantCulture));
                sb.Append(", \"blocked\": ").Append(e.Blocked ? "true" : "false");
                sb.Append(", \"loopback\": ").Append(e.IsLoopback ? "true" : "false");
                sb.Append(", \"note\": ").Append(JsonString(e.Note));
                sb.Append("}");
            }
            sb.Append(list.Count > 0 ? "\n  ]\n}\n" : "]\n}\n");
            return sb.ToString();
        }

        public static string ExportCsv(List<NetworkLedgerEntry> list)
        {
            if (list == null) list = Snapshot();
            StringBuilder sb = new StringBuilder();
            sb.Append("id,time_utc,feature,method,host,path,url,bytes_sent,bytes_received,status,duration_ms,blocked,loopback,note\r\n");
            foreach (NetworkLedgerEntry e in list)
            {
                sb.Append(e.Id.ToString(CultureInfo.InvariantCulture)).Append(',');
                sb.Append(Csv(e.TimestampUtc.ToString("o", CultureInfo.InvariantCulture))).Append(',');
                sb.Append(Csv(e.Feature)).Append(',');
                sb.Append(Csv(e.Method)).Append(',');
                sb.Append(Csv(e.Host)).Append(',');
                sb.Append(Csv(e.Path)).Append(',');
                sb.Append(Csv(e.Url)).Append(',');
                sb.Append(e.BytesSent.ToString(CultureInfo.InvariantCulture)).Append(',');
                sb.Append(e.BytesReceived.ToString(CultureInfo.InvariantCulture)).Append(',');
                sb.Append(e.Status.ToString(CultureInfo.InvariantCulture)).Append(',');
                sb.Append(e.DurationMs.ToString(CultureInfo.InvariantCulture)).Append(',');
                sb.Append(e.Blocked ? "true" : "false").Append(',');
                sb.Append(e.IsLoopback ? "true" : "false").Append(',');
                sb.Append(Csv(e.Note)).Append("\r\n");
            }
            return sb.ToString();
        }

        /// <summary>Case-insensitive match of <paramref name="filter"/> against feature, host, path, method, status and note.</summary>
        public static bool MatchesFilter(NetworkLedgerEntry e, string filter)
        {
            if (e == null) return false;
            if (string.IsNullOrEmpty(filter) || filter.Trim().Length == 0) return true;
            string f = filter.Trim();
            if (string.Equals(f, "blocked", StringComparison.OrdinalIgnoreCase)) return e.Blocked;
            return Contains(e.Feature, f) || Contains(e.Host, f) || Contains(e.Path, f) || Contains(e.Method, f) ||
                   Contains(e.Note, f) || e.Status.ToString(CultureInfo.InvariantCulture) == f;
        }

        private static bool Contains(string hay, string needle)
        {
            return !string.IsNullOrEmpty(hay) && hay.IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        /// <summary>"1.2 KB" style size for tables.</summary>
        public static string FormatBytes(long bytes)
        {
            if (bytes < 1024) return bytes.ToString(CultureInfo.InvariantCulture) + " B";
            if (bytes < 1024 * 1024) return (bytes / 1024.0).ToString("0.0", CultureInfo.InvariantCulture) + " KB";
            return (bytes / (1024.0 * 1024.0)).ToString("0.0", CultureInfo.InvariantCulture) + " MB";
        }

        public static string FormatStatus(NetworkLedgerEntry e)
        {
            if (e == null) return "";
            if (e.Blocked) return "blocked";
            if (e.Status <= 0) return "failed";
            return e.Status.ToString(CultureInfo.InvariantCulture);
        }

        private static string Csv(string v)
        {
            string s = v ?? "";
            if (s.IndexOfAny(new char[] { ',', '"', '\r', '\n' }) >= 0) return "\"" + s.Replace("\"", "\"\"") + "\"";
            return s;
        }

        private static string JsonString(string v)
        {
            string s = v ?? "";
            StringBuilder sb = new StringBuilder(s.Length + 2);
            sb.Append('"');
            foreach (char c in s)
            {
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (c < 0x20) sb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                        else sb.Append(c);
                        break;
                }
            }
            sb.Append('"');
            return sb.ToString();
        }

        // ------------------------------------------------------------------ internals

        internal static void ChangeInFlight(int delta)
        {
            int now = Interlocked.Add(ref inFlight, delta);
            if (now < 0) { Interlocked.Exchange(ref inFlight, 0); now = 0; }
            Action<int> h = InFlightChanged;
            if (h != null)
            {
                try { h(now); } catch { }
            }
        }

        private static void Add(NetworkLedgerEntry e)
        {
            e.TimestampUtc = DateTime.UtcNow;
            string raw = e.Url ?? "";
            e.Host = HostOf(raw);
            e.Path = PathOf(raw);
            e.IsLoopback = IsLoopback(raw);
            e.Url = RedactUrl(raw);
            if (e.Host == raw) e.Host = RedactUrl(raw);
            lock (gate)
            {
                e.Id = ++nextId;
                entries.Add(e);
                if (entries.Count > MaxEntries) entries.RemoveRange(0, entries.Count - MaxEntries);

                sessionRequests++;
                if (e.Blocked) sessionBlocked++;
                sessionBytesSent += e.BytesSent;
                sessionBytesReceived += e.BytesReceived;
                NetworkFeatureTotal t;
                if (!totals.TryGetValue(e.Feature, out t))
                {
                    t = new NetworkFeatureTotal();
                    t.Feature = e.Feature;
                    totals[e.Feature] = t;
                }
                t.Requests++;
                if (e.Blocked) t.Blocked++;
                else if (e.Status <= 0 || e.Status >= 400) t.Failed++;
                t.BytesSent += e.BytesSent;
                t.BytesReceived += e.BytesReceived;
                t.DurationMs += e.DurationMs;
                if (!string.IsNullOrEmpty(e.Host) && !t.Hosts.Contains(e.Host)) t.Hosts.Add(e.Host);
                if (!e.Blocked && !string.IsNullOrEmpty(e.Host))
                {
                    int c;
                    hostCounts.TryGetValue(e.Host, out c);
                    hostCounts[e.Host] = c + 1;
                }
            }
            Action<NetworkLedgerEntry> h = EntryAdded;
            if (h != null)
            {
                try { h(e); } catch { }
            }
        }
    }
}
