using System;
using System.Collections.Generic;

namespace codeLess.Studio
{
    /// <summary>One outbound network request made by codeLess itself (shown in the Network Activity panel).</summary>
    public class NetworkLedgerEntry
    {
        public DateTime TimestampUtc;
        /// <summary>Feature that made the request, e.g. "AI: Anthropic", "Update check", "OpenVSX search", "HTTP Client".</summary>
        public string Feature = "";
        public string Method = "GET";
        public string Url = "";
        public string Host = "";
        public long BytesSent;
        public long BytesReceived;
        /// <summary>HTTP status code, 0 if the request failed before a response, -1 if blocked by Offline Mode.</summary>
        public int Status;
        public bool Blocked;
        public string Note = "";
    }

    /// <summary>
    /// Single choke point for every outbound request codeLess makes (shared by Windows and macOS/Linux; C# 5 only).
    /// Call <see cref="Check"/> before sending and <see cref="Record"/> after. Offline Mode blocks everything except
    /// loopback (local Ollama / LM Studio / language servers).
    /// </summary>
    public static class NetworkLedger
    {
        private static readonly object gate = new object();
        private static readonly List<NetworkLedgerEntry> entries = new List<NetworkLedgerEntry>();
        private const int MaxEntries = 2000;

        /// <summary>When true, all non-loopback requests are refused.</summary>
        public static bool OfflineMode;

        /// <summary>Raised (on the calling thread) after an entry is added; UIs must marshal to their UI thread.</summary>
        public static event Action<NetworkLedgerEntry> EntryAdded;

        /// <summary>Raised when <see cref="OfflineMode"/> is changed through <see cref="SetOfflineMode"/>.</summary>
        public static event Action<bool> OfflineModeChanged;

        public static void SetOfflineMode(bool offline)
        {
            if (OfflineMode == offline) return;
            OfflineMode = offline;
            Action<bool> h = OfflineModeChanged;
            if (h != null) h(offline);
        }

        /// <summary>True if the URL targets this machine (localhost, 127.0.0.0/8, ::1).</summary>
        public static bool IsLoopback(string url)
        {
            Uri u;
            if (string.IsNullOrEmpty(url) || !Uri.TryCreate(url, UriKind.Absolute, out u)) return false;
            if (u.IsLoopback) return true;
            string h = u.Host.ToLowerInvariant();
            return h == "localhost" || h.StartsWith("127.") || h == "[::1]" || h == "::1";
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

        public static void Record(string feature, string method, string url, long bytesSent, long bytesReceived, int status, string note)
        {
            NetworkLedgerEntry e = new NetworkLedgerEntry();
            e.Feature = feature ?? "";
            e.Method = method ?? "GET";
            e.Url = url ?? "";
            e.BytesSent = bytesSent;
            e.BytesReceived = bytesReceived;
            e.Status = status;
            e.Note = note ?? "";
            Add(e);
        }

        public static List<NetworkLedgerEntry> Snapshot()
        {
            lock (gate) return new List<NetworkLedgerEntry>(entries);
        }

        public static void Clear()
        {
            lock (gate) entries.Clear();
        }

        private static void Add(NetworkLedgerEntry e)
        {
            e.TimestampUtc = DateTime.UtcNow;
            e.Host = HostOf(e.Url);
            lock (gate)
            {
                entries.Add(e);
                if (entries.Count > MaxEntries) entries.RemoveRange(0, entries.Count - MaxEntries);
            }
            Action<NetworkLedgerEntry> h = EntryAdded;
            if (h != null)
            {
                try { h(e); } catch { }
            }
        }

        public static string HostOf(string url)
        {
            Uri u;
            if (!string.IsNullOrEmpty(url) && Uri.TryCreate(url, UriKind.Absolute, out u)) return u.Host;
            return url ?? "";
        }
    }
}
