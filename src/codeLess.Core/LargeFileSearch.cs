using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

namespace codeLess.Studio
{
    public struct LargeFileMatch
    {
        /// <summary>0-based line.</summary>
        public long Line;
        /// <summary>Absolute byte offset of the match, or -1 when found by the line-decoding path.</summary>
        public long ByteOffset;
        /// <summary>0-based character column, or -1 when it must be resolved via LargeFileDocument.ByteOffsetToColumn.</summary>
        public int Column;
        /// <summary>Match length in characters.</summary>
        public int Length;
    }

    /// <summary>
    /// Streaming search over a LargeFileDocument on a dedicated background thread. Plain-text queries on UTF-8 /
    /// single-byte files are matched directly on the bytes (vectorised Array.IndexOf on the first byte) while counting
    /// lines on the fly; regex, UTF-16 and non-ASCII case-insensitive queries decode line by line. Every match is
    /// counted; the first MaxStoredMatches are kept for navigation. Searches the content present when it started.
    /// </summary>
    public sealed class LargeFileSearch
    {
        public const int DefaultMaxStoredMatches = 200000;
        private const int ChunkSize = 4 * 1024 * 1024;
        private const int MaxSearchLineBytes = 1024 * 1024;

        private readonly LargeFileDocument doc;
        private readonly object matchLock = new object();
        private readonly List<LargeFileMatch> matches = new List<LargeFileMatch>();
        private volatile bool cancelled;
        private volatile bool completed;
        private long totalMatches;
        private long scannedBytes;
        private long searchLength;
        private long lastProgressTicks;

        public string Query { get; private set; }
        public bool MatchCase { get; private set; }
        public bool UseRegex { get; private set; }
        public int MaxStoredMatches { get; private set; }
        public string Error { get; private set; }
        public TimeSpan Elapsed { get; private set; }
        /// <summary>"bytes" for the byte-level fast path, "lines" for the decoding path.</summary>
        public string Strategy { get; private set; }

        /// <summary>Raised from the search thread (throttled to ~10/s) and once when the search completes.</summary>
        public event EventHandler Progress;

        public LargeFileSearch(LargeFileDocument document, string query, bool matchCase, bool useRegex)
            : this(document, query, matchCase, useRegex, DefaultMaxStoredMatches)
        {
        }

        public LargeFileSearch(LargeFileDocument document, string query, bool matchCase, bool useRegex, int maxStoredMatches)
        {
            if (document == null) throw new ArgumentNullException("document");
            doc = document;
            Query = query ?? "";
            MatchCase = matchCase;
            UseRegex = useRegex;
            MaxStoredMatches = Math.Max(1, maxStoredMatches);
        }

        public bool IsCompleted { get { return completed; } }
        public bool IsCancelled { get { return cancelled; } }
        public long TotalMatches { get { return Interlocked.Read(ref totalMatches); } }
        public int StoredMatchCount { get { lock (matchLock) { return matches.Count; } } }
        public bool IsCapped { get { return TotalMatches > StoredMatchCount; } }

        public double ProgressFraction
        {
            get
            {
                long len = Interlocked.Read(ref searchLength);
                if (completed || len <= 0) return 1.0;
                return Math.Min(1.0, (double)Interlocked.Read(ref scannedBytes) / len);
            }
        }

        public LargeFileMatch GetMatch(int index)
        {
            lock (matchLock) { return matches[index]; }
        }

        /// <summary>Index of the first stored match at or after (line, column), wrapping to 0; -1 if none.</summary>
        public int FindNextMatchIndex(long line, int column)
        {
            lock (matchLock)
            {
                int lo = 0, hi = matches.Count;
                while (lo < hi)
                {
                    int mid = (lo + hi) >> 1;
                    if (matches[mid].Line < line) lo = mid + 1; else hi = mid;
                }
                for (int i = lo; i < matches.Count && matches[i].Line == line; i++)
                {
                    int c = matches[i].Column;
                    if (c < 0) c = doc.ByteOffsetToColumn(matches[i].Line, matches[i].ByteOffset);
                    if (c >= column) return i;
                    lo = i + 1;
                }
                if (lo < matches.Count) return lo;
                return matches.Count > 0 ? 0 : -1;
            }
        }

        /// <summary>Stored matches on the given line range (used to highlight the visible lines).</summary>
        public List<LargeFileMatch> GetMatchesInLines(long firstLine, long lastLine)
        {
            List<LargeFileMatch> result = new List<LargeFileMatch>();
            lock (matchLock)
            {
                int lo = 0, hi = matches.Count;
                while (lo < hi)
                {
                    int mid = (lo + hi) >> 1;
                    if (matches[mid].Line < firstLine) lo = mid + 1; else hi = mid;
                }
                for (int i = lo; i < matches.Count && matches[i].Line <= lastLine; i++)
                    result.Add(matches[i]);
            }
            return result;
        }

        public void Cancel() { cancelled = true; }

        /// <summary>Starts the search on a background thread.</summary>
        public void Start()
        {
            Thread t = new Thread(delegate () { Run(); });
            t.IsBackground = true;
            t.Name = "codeLess large-file search";
            t.Priority = ThreadPriority.BelowNormal;
            t.Start();
        }

        /// <summary>Runs the search synchronously on the calling thread (tests / tools).</summary>
        public void Run()
        {
            DateTime t0 = DateTime.UtcNow;
            try
            {
                if (Query.Length == 0) return;
                Interlocked.Exchange(ref searchLength, doc.FileLength);
                byte[] needle = null;
                if (!UseRegex && doc.CodeUnitSize == 1 && Query.IndexOf('\n') < 0)
                {
                    bool asciiLetters = true;
                    for (int i = 0; i < Query.Length; i++)
                    {
                        if (Query[i] > 127 && char.ToLowerInvariant(Query[i]) != char.ToUpperInvariant(Query[i])) { asciiLetters = false; break; }
                    }
                    if (MatchCase || asciiLetters)
                    {
                        needle = doc.TextEncoding.GetBytes(Query);
                        // Latin-1 cannot encode everything: if the round trip fails the byte path cannot match it.
                        if (!string.Equals(doc.TextEncoding.GetString(needle), Query, StringComparison.Ordinal)) needle = null;
                    }
                }
                if (needle != null && needle.Length > 0 && needle.Length < 64 * 1024)
                {
                    Strategy = "bytes";
                    RunByteSearch(needle);
                }
                else
                {
                    Strategy = "lines";
                    RunLineSearch();
                }
            }
            catch (Exception ex)
            {
                Error = ex.Message;
            }
            finally
            {
                Elapsed = DateTime.UtcNow - t0;
                completed = true;
                RaiseProgress(true);
            }
        }

        private void AddMatch(LargeFileMatch m)
        {
            Interlocked.Increment(ref totalMatches);
            lock (matchLock)
            {
                if (matches.Count < MaxStoredMatches) matches.Add(m);
            }
        }

        private bool StoreFull { get { lock (matchLock) { return matches.Count >= MaxStoredMatches; } } }

        private void RaiseProgress(bool force)
        {
            long now = DateTime.UtcNow.Ticks;
            if (!force && now - lastProgressTicks < TimeSpan.TicksPerMillisecond * 100) return;
            lastProgressTicks = now;
            EventHandler h = Progress;
            if (h != null) { try { h(this, EventArgs.Empty); } catch { } }
        }

        // ------------------------------------------------------------------ byte-level fast path

        private void RunByteSearch(byte[] needle)
        {
            int nlen = needle.Length;
            byte[] foldNeedle = new byte[nlen];
            byte[] fold = new byte[256];
            for (int i = 0; i < 256; i++)
                fold[i] = (!MatchCase && i >= 'A' && i <= 'Z') ? (byte)(i + 32) : (byte)i;
            for (int i = 0; i < nlen; i++) foldNeedle[i] = fold[needle[i]];
            byte first = foldNeedle[0];
            bool twoCase = !MatchCase && first >= 'a' && first <= 'z';
            byte firstUpper = twoCase ? (byte)(first - 32) : first;

            long length = Interlocked.Read(ref searchLength);
            byte[] buf = new byte[ChunkSize + nlen];
            long chunkStart = doc.BomLength;
            long line = 0;
            long lineStartAbs = doc.BomLength;
            long resumeAbs = chunkStart; // next position a match may start (non-overlapping matches)

            using (FileStream fs = new FileStream(doc.FilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1, FileOptions.SequentialScan))
            {
                while (chunkStart < length && !cancelled)
                {
                    int want = (int)Math.Min(buf.Length, length - chunkStart);
                    fs.Seek(chunkStart, SeekOrigin.Begin);
                    int n = 0;
                    while (n < want)
                    {
                        int r = fs.Read(buf, n, want - n);
                        if (r <= 0) break;
                        n += r;
                    }
                    if (n <= 0) break;
                    bool atEnd = chunkStart + n >= length;
                    // Matches may only START in [0, limit); the next chunk begins at limit so lines are counted once.
                    int limit = atEnd ? n : Math.Max(1, n - (nlen - 1));
                    if (limit > n) limit = n;
                    int countedTo = 0;
                    bool storeFull = StoreFull;

                    int i = (int)Math.Max(0, resumeAbs - chunkStart);
                    int nextLower = -2, nextUpper = -2;
                    while (i < limit && !cancelled)
                    {
                        int p;
                        if (twoCase)
                        {
                            if (nextLower < i && nextLower != -1) nextLower = Array.IndexOf(buf, first, i, limit - i);
                            if (nextUpper < i && nextUpper != -1) nextUpper = Array.IndexOf(buf, firstUpper, i, limit - i);
                            if (nextLower < 0) p = nextUpper;
                            else if (nextUpper < 0) p = nextLower;
                            else p = Math.Min(nextLower, nextUpper);
                        }
                        else
                        {
                            p = Array.IndexOf(buf, first, i, limit - i);
                        }
                        if (p < 0) break;
                        if (p + nlen > n) break; // only possible at EOF
                        bool ok = true;
                        for (int k = 1; k < nlen; k++)
                        {
                            if (fold[buf[p + k]] != foldNeedle[k]) { ok = false; break; }
                        }
                        if (!ok) { i = p + 1; continue; }

                        if (!storeFull)
                        {
                            // Advance the line counter to the match.
                            int q = countedTo;
                            while (q < p)
                            {
                                int nl = Array.IndexOf(buf, (byte)0x0A, q, p - q);
                                if (nl < 0) break;
                                line++;
                                lineStartAbs = chunkStart + nl + 1;
                                q = nl + 1;
                            }
                            countedTo = p;
                            long abs = chunkStart + p;
                            int col = -1;
                            if (lineStartAbs >= chunkStart)
                            {
                                int ls = (int)(lineStartAbs - chunkStart);
                                col = doc.TextEncoding.GetCharCount(buf, ls, p - ls);
                            }
                            LargeFileMatch m = new LargeFileMatch();
                            m.Line = line; m.ByteOffset = abs; m.Column = col; m.Length = Query.Length;
                            AddMatch(m);
                            storeFull = StoreFull;
                        }
                        else
                        {
                            Interlocked.Increment(ref totalMatches);
                        }
                        i = p + nlen;
                    }
                    if (i > limit) resumeAbs = chunkStart + i; else resumeAbs = chunkStart + limit;

                    if (!storeFull)
                    {
                        int q = countedTo;
                        while (q < limit)
                        {
                            int nl = Array.IndexOf(buf, (byte)0x0A, q, limit - q);
                            if (nl < 0) break;
                            line++;
                            lineStartAbs = chunkStart + nl + 1;
                            q = nl + 1;
                        }
                    }
                    chunkStart += limit;
                    Interlocked.Exchange(ref scannedBytes, chunkStart);
                    RaiseProgress(false);
                }
            }
        }

        // ------------------------------------------------------------------ line-decoding path (regex / UTF-16 / Unicode case folding)

        private void RunLineSearch()
        {
            Regex rx = null;
            if (UseRegex)
            {
                RegexOptions opts = RegexOptions.CultureInvariant;
                if (!MatchCase) opts |= RegexOptions.IgnoreCase;
                try { rx = new Regex(Query, opts); }
                catch (ArgumentException ex) { Error = "Invalid regular expression: " + ex.Message; return; }
            }
            StringComparison cmp = MatchCase ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
            long length = Interlocked.Read(ref searchLength);
            int unit = doc.CodeUnitSize;
            bool be = doc.IsBigEndian;
            Encoding enc = doc.TextEncoding;

            using (FileStream fs = new FileStream(doc.FilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1, FileOptions.SequentialScan))
            {
                byte[] buf = new byte[ChunkSize];
                byte[] carry = new byte[0];
                int carryLen = 0;
                bool carryOverflow = false;
                long pos = doc.BomLength;
                long line = 0;
                bool done = false;
                while (!done && !cancelled)
                {
                    int want = (int)Math.Min(buf.Length, Math.Max(0, length - pos));
                    int n = 0;
                    if (want > 0)
                    {
                        fs.Seek(pos, SeekOrigin.Begin);
                        while (n < want)
                        {
                            int r = fs.Read(buf, n, want - n);
                            if (r <= 0) break;
                            n += r;
                        }
                    }
                    if (unit == 2) n -= n % 2;
                    bool atEnd = n <= 0 || pos + n >= length;
                    int i = 0;
                    while (i < n && !cancelled)
                    {
                        int nlPos = -1;
                        int s = i;
                        while (s < n)
                        {
                            int p = Array.IndexOf(buf, (byte)0x0A, s, n - s);
                            if (p < 0) break;
                            if (unit == 1) { nlPos = p; break; }
                            int us = be ? p - 1 : p;
                            if (us >= 0 && ((us - i) & 1) == 0 && us + 1 < n && (be ? buf[us] : buf[us + 1]) == 0) { nlPos = us; break; }
                            s = p + 1;
                        }
                        if (nlPos < 0)
                        {
                            // Partial line: carry it into the next chunk (bounded).
                            int rest = n - i;
                            if (carryLen + rest <= MaxSearchLineBytes)
                            {
                                if (carry.Length < carryLen + rest) Array.Resize(ref carry, Math.Max(carryLen + rest, carry.Length * 2));
                                Buffer.BlockCopy(buf, i, carry, carryLen, rest);
                                carryLen += rest;
                            }
                            else
                            {
                                int fit = MaxSearchLineBytes - carryLen;
                                if (unit == 2) fit -= fit % 2;
                                if (fit > 0)
                                {
                                    if (carry.Length < carryLen + fit) Array.Resize(ref carry, carryLen + fit);
                                    Buffer.BlockCopy(buf, i, carry, carryLen, fit);
                                    carryLen += fit;
                                }
                                carryOverflow = true;
                            }
                            break;
                        }
                        string text;
                        if (carryLen > 0 || carryOverflow)
                        {
                            int take = Math.Min(nlPos - i, Math.Max(0, MaxSearchLineBytes - carryLen));
                            if (carry.Length < carryLen + take) Array.Resize(ref carry, carryLen + take);
                            Buffer.BlockCopy(buf, i, carry, carryLen, take);
                            text = DecodeLine(enc, carry, 0, carryLen + take, unit, be);
                            carryLen = 0;
                            carryOverflow = false;
                        }
                        else
                        {
                            text = DecodeLine(enc, buf, i, Math.Min(nlPos - i, MaxSearchLineBytes), unit, be);
                        }
                        SearchLine(text, line, rx, cmp);
                        line++;
                        i = nlPos + unit;
                    }
                    pos += n;
                    Interlocked.Exchange(ref scannedBytes, pos);
                    RaiseProgress(false);
                    if (atEnd)
                    {
                        if (carryLen > 0 || carryOverflow)
                            SearchLine(DecodeLine(enc, carry, 0, carryLen, unit, be), line, rx, cmp);
                        done = true;
                    }
                }
            }
        }

        private static string DecodeLine(Encoding enc, byte[] b, int offset, int count, int unit, bool be)
        {
            if (count <= 0) return "";
            if (unit == 1 && b[offset + count - 1] == 0x0D) count--;
            else if (unit == 2 && count >= 2)
            {
                bool cr = be ? (b[offset + count - 2] == 0 && b[offset + count - 1] == 0x0D) : (b[offset + count - 2] == 0x0D && b[offset + count - 1] == 0);
                if (cr) count -= 2;
            }
            return enc.GetString(b, offset, count);
        }

        private void SearchLine(string text, long line, Regex rx, StringComparison cmp)
        {
            if (text.Length == 0) return;
            if (rx != null)
            {
                Match m = rx.Match(text);
                while (m.Success && !cancelled)
                {
                    if (m.Length == 0)
                    {
                        m = m.NextMatch();
                        continue;
                    }
                    LargeFileMatch lm = new LargeFileMatch();
                    lm.Line = line; lm.ByteOffset = -1; lm.Column = m.Index; lm.Length = m.Length;
                    AddMatch(lm);
                    m = m.NextMatch();
                }
                return;
            }
            int idx = 0;
            while (idx <= text.Length - Query.Length)
            {
                int p = text.IndexOf(Query, idx, cmp);
                if (p < 0) break;
                LargeFileMatch lm = new LargeFileMatch();
                lm.Line = line; lm.ByteOffset = -1; lm.Column = p; lm.Length = Query.Length;
                AddMatch(lm);
                idx = p + Query.Length;
            }
        }
    }
}
