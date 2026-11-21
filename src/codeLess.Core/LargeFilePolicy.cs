using System;
using System.Globalization;
using System.IO;

namespace codeLess.Studio
{
    /// <summary>Large-file mode: a tab whose content lives in a paged LargeFileDocument instead of Lines/Content.</summary>
    public partial class EditorTabBuffer
    {
        /// <summary>
        /// Non-null when the tab shows a file in the read-only large-file viewer. Content / Lines stay empty, so every
        /// feature that walks open buffers (agent context, search in open editors, autosave) sees an empty clean buffer;
        /// saving such a tab is refused by both UIs.
        /// </summary>
        public LargeFileDocument LargeDocument;

        public bool IsLargeFileViewer
        {
            get { return LargeDocument != null; }
        }
    }

    /// <summary>Large-file thresholds, persisted with the other IDE settings (JSON) on both UIs.</summary>
    public partial class IdePersistentSettings
    {
        private int largeFileThresholdMB = LargeFilePolicy.DefaultThresholdMB;
        private int largeFileLineThreshold = LargeFilePolicy.DefaultLineThreshold;
        private int largeFileEditLimitMB = LargeFilePolicy.DefaultEditLimitMB;

        /// <summary>Files at or above this size open in the read-only large-file viewer (default 50 MB; 0 disables).</summary>
        public int LargeFileThresholdMB
        {
            get { return largeFileThresholdMB; }
            set { largeFileThresholdMB = value; }
        }

        /// <summary>Files estimated to have more lines than this also open in the viewer (default 2,000,000).</summary>
        public int LargeFileLineThreshold
        {
            get { return largeFileLineThreshold; }
            set { largeFileLineThreshold = value; }
        }

        /// <summary>"Open in Editor" is offered from the viewer banner only up to this size (default 128 MB).</summary>
        public int LargeFileEditLimitMB
        {
            get { return largeFileEditLimitMB; }
            set { largeFileEditLimitMB = value; }
        }
    }

    public static class LargeFilePolicy
    {
        public const int DefaultThresholdMB = 50;
        public const int DefaultLineThreshold = 2000000;
        public const int DefaultEditLimitMB = 128;
        /// <summary>Below this size the line estimate is not sampled (the normal editor handles it).</summary>
        public const long MinBytesForLineSampling = 4L * 1024 * 1024;
        public const int ClipboardCharLimit = 16 * 1024 * 1024;

        public static long ThresholdBytes(IdePersistentSettings s)
        {
            int mb = s != null ? s.LargeFileThresholdMB : DefaultThresholdMB;
            if (mb <= 0) return long.MaxValue;
            return (long)mb * 1024 * 1024;
        }

        public static long EditLimitBytes(IdePersistentSettings s)
        {
            int mb = s != null ? s.LargeFileEditLimitMB : DefaultEditLimitMB;
            if (mb <= 0) return 0;
            return (long)mb * 1024 * 1024;
        }

        /// <summary>
        /// Decides instantly (one stat, plus a 1 MB sample for files over 4 MB) whether a file should open in the
        /// large-file viewer: size at/above the threshold, or an estimated line count above the line threshold.
        /// </summary>
        public static bool ShouldUseViewer(string path, IdePersistentSettings s)
        {
            string reason;
            return ShouldUseViewer(path, ThresholdBytes(s), s != null ? s.LargeFileLineThreshold : DefaultLineThreshold, out reason);
        }

        public static bool ShouldUseViewer(string path, long thresholdBytes, long lineThreshold, out string reason)
        {
            reason = null;
            long len;
            try
            {
                FileInfo fi = new FileInfo(path);
                if (!fi.Exists) return false;
                len = fi.Length;
            }
            catch { return false; }
            if (len >= thresholdBytes)
            {
                reason = "size";
                return true;
            }
            if (lineThreshold > 0 && len >= MinBytesForLineSampling)
            {
                long est = EstimateLineCount(path, len);
                if (est > lineThreshold)
                {
                    reason = "lines";
                    return true;
                }
            }
            return false;
        }

        /// <summary>Line-count estimate from the newline density of the first 1 MB.</summary>
        public static long EstimateLineCount(string path, long length)
        {
            try
            {
                using (FileStream fs = LargeFileDocument.OpenShared(path, 1))
                {
                    byte[] buf = new byte[1024 * 1024];
                    int n = 0;
                    while (n < buf.Length)
                    {
                        int r = fs.Read(buf, n, buf.Length - n);
                        if (r <= 0) break;
                        n += r;
                    }
                    if (n <= 0) return 0;
                    long count = 0;
                    int i = 0;
                    while (i < n)
                    {
                        int p = Array.IndexOf(buf, (byte)0x0A, i, n - i);
                        if (p < 0) break;
                        count++;
                        i = p + 1;
                    }
                    return (long)((double)count * length / n);
                }
            }
            catch { return 0; }
        }

        public static string FormatBytes(long bytes)
        {
            CultureInfo ci = CultureInfo.InvariantCulture;
            if (bytes >= 1024L * 1024 * 1024) return (bytes / (1024.0 * 1024 * 1024)).ToString("0.00", ci) + " GB";
            if (bytes >= 1024L * 1024) return (bytes / (1024.0 * 1024)).ToString("0.0", ci) + " MB";
            if (bytes >= 1024) return (bytes / 1024.0).ToString("0.0", ci) + " KB";
            return bytes.ToString(ci) + " B";
        }

        public const string BannerTitle = "Large file — opened read-only for speed";

        /// <summary>Second banner line: size, encoding, indexing progress / line count, follow state.</summary>
        public static string DescribeStatus(LargeFileDocument doc)
        {
            if (doc == null) return "";
            CultureInfo ci = CultureInfo.InvariantCulture;
            string s = FormatBytes(doc.FileLength) + "  ·  " + doc.EncodingLabel + "  ·  ";
            if (doc.IsIndexComplete)
                s += doc.LineCount.ToString("N0", ci) + " lines";
            else
                s += "indexing " + ((int)(doc.IndexProgress * 100)).ToString(ci) + "% (" + doc.LineCount.ToString("N0", ci) + " lines so far)";
            if (doc.Follow) s += "  ·  following (tail -f)";
            if (!string.IsNullOrEmpty(doc.LastError)) s += "  ·  " + doc.LastError;
            return s;
        }

        public static string DescribeSearch(LargeFileSearch search, int activeIndex)
        {
            if (search == null) return "";
            CultureInfo ci = CultureInfo.InvariantCulture;
            if (!string.IsNullOrEmpty(search.Error)) return search.Error;
            long total = search.TotalMatches;
            string s;
            if (total == 0)
                s = search.IsCompleted ? "No results" : "Searching…";
            else if (activeIndex >= 0)
                s = (activeIndex + 1).ToString("N0", ci) + " of " + total.ToString("N0", ci);
            else
                s = total.ToString("N0", ci) + " results";
            if (!search.IsCompleted)
                s += "  (" + ((int)(search.ProgressFraction * 100)).ToString(ci) + "%)";
            else if (search.IsCapped)
                s += "  (first " + search.StoredMatchCount.ToString("N0", ci) + " navigable)";
            return s;
        }
    }
}
