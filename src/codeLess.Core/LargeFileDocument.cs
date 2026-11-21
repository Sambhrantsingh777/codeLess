using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;

namespace codeLess.Studio
{
    /// <summary>
    /// Read-only, paged view over an arbitrarily large text file (multi-GB logs). Nothing is loaded up front:
    /// a background thread builds a sparse line-offset index (one checkpoint every 64 lines or 64 KB), the UI reads
    /// only the lines it renders through a small LRU page cache, and "follow" mode (tail -f) extends the index as the
    /// file grows and resets it on truncation / rotation. Lines are split on LF (CRLF has its CR stripped); a lone CR is
    /// not a line break. Supports UTF-8 (with or without BOM), UTF-16 LE/BE (BOM or detected), and falls back to
    /// Latin-1 for byte content that is not valid UTF-8 so every byte is shown.
    /// Shared by the WinForms and Avalonia viewers; strict C# 5.
    /// </summary>
    public sealed class LargeFileDocument : IDisposable
    {
        public const int CheckpointLineInterval = 64;
        public const int CheckpointByteInterval = 64 * 1024;
        public const int PageSize = 64 * 1024;
        public const int MaxCachedPages = 48;
        /// <summary>Lines longer than this many characters are truncated for display (the rest is not decoded).</summary>
        public const int MaxDisplayLineChars = 20000;
        private const int IndexChunkSize = 1024 * 1024;
        private const int HeadSignatureLength = 4096;

        private readonly object indexLock = new object();
        private readonly object streamLock = new object();
        private readonly object workerLock = new object();

        // Sparse checkpoints: line cpLine[i] starts at byte cpOffset[i]. Appended only by the index worker under indexLock.
        private long[] cpLine = new long[1024];
        private long[] cpOffset = new long[1024];
        private int cpCount;
        private long newlineCount;          // newline units found in [dataStart, indexedBytes)
        private long indexedBytes;          // absolute file offset up to which the index is built
        private long lastLineStart;         // absolute start offset of the last (possibly partial) line
        private long lastCheckpointLine;
        private long lastCheckpointOffset;

        private FileStream readStream;
        private readonly Dictionary<long, CachedPage> pageCache = new Dictionary<long, CachedPage>();
        private long pageClock;

        private volatile bool disposed;
        private volatile bool indexComplete;
        private volatile bool workerRunning;
        private int generation;
        private long fileLength;
        private long indexTargetLength;
        private byte[] headSignature = new byte[0];
        private long lastProgressTicks;
        private bool follow;

        private sealed class CachedPage
        {
            public byte[] Data;
            public int Valid;
            public long LastUse;
        }

        public string FilePath { get; private set; }
        public Encoding TextEncoding { get; private set; }
        public string EncodingLabel { get; private set; }
        public int BomLength { get; private set; }
        /// <summary>1 for UTF-8 / single-byte encodings, 2 for UTF-16.</summary>
        public int CodeUnitSize { get; private set; }
        public bool IsBigEndian { get; private set; }
        public DateTime OpenedUtc { get; private set; }
        /// <summary>Number of times the file was detected as truncated / rotated and re-indexed.</summary>
        public int ResetCount { get; private set; }
        /// <summary>Error text if the last index pass or poll failed (e.g. file deleted); null otherwise.</summary>
        public string LastError { get; private set; }

        /// <summary>Raised from a background thread (throttled to ~10/s) while indexing and when indexing finishes.</summary>
        public event EventHandler IndexProgressChanged;
        /// <summary>Raised from a background thread when follow mode found new content that has been indexed.</summary>
        public event EventHandler ContentAppended;
        /// <summary>Raised from a background thread when the file shrank or was replaced (log rotation) and the index restarted.</summary>
        public event EventHandler ContentReset;

        private LargeFileDocument(string path)
        {
            FilePath = path;
            OpenedUtc = DateTime.UtcNow;
        }

        /// <summary>Opens the file (shared read, so writers / log rotators keep working) and starts background indexing.</summary>
        public static LargeFileDocument Open(string path)
        {
            return Open(path, true);
        }

        public static LargeFileDocument Open(string path, bool startIndexing)
        {
            if (string.IsNullOrEmpty(path)) throw new ArgumentNullException("path");
            LargeFileDocument doc = new LargeFileDocument(Path.GetFullPath(path));
            doc.OpenStreamAndDetect();
            if (startIndexing) doc.EnsureIndexing();
            return doc;
        }

        public static FileStream OpenShared(string path, int bufferSize)
        {
            return new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, bufferSize, FileOptions.None);
        }

        private void OpenStreamAndDetect()
        {
            FileStream fs = OpenShared(FilePath, 1);
            byte[] head = new byte[64 * 1024];
            int n = ReadFully(fs, 0, head, 0, head.Length);
            int bomLen; int unit; bool bigEndian; string label;
            Encoding enc = DetectEncoding(head, n, out bomLen, out unit, out bigEndian, out label);
            lock (streamLock)
            {
                if (readStream != null) { try { readStream.Dispose(); } catch { } }
                readStream = fs;
                pageCache.Clear();
            }
            TextEncoding = enc;
            EncodingLabel = label;
            BomLength = bomLen;
            CodeUnitSize = unit;
            IsBigEndian = bigEndian;
            long len = fs.Length;
            int sigLen = (int)Math.Min(HeadSignatureLength, Math.Min(len, n));
            byte[] sig = new byte[sigLen];
            Buffer.BlockCopy(head, 0, sig, 0, sigLen);
            lock (indexLock)
            {
                headSignature = sig;
                cpCount = 1;
                cpLine[0] = 0;
                cpOffset[0] = bomLen;
                newlineCount = 0;
                indexedBytes = Math.Min(bomLen, len);
                lastLineStart = bomLen;
                lastCheckpointLine = 0;
                lastCheckpointOffset = bomLen;
                fileLength = len;
                indexTargetLength = len;
                indexComplete = false;
            }
        }

        // ------------------------------------------------------------------ encoding

        /// <summary>
        /// BOM sniffing (UTF-8, UTF-16 LE/BE), then a NUL-distribution heuristic for BOM-less UTF-16, then strict UTF-8
        /// validation of the sample (ignoring a sequence cut at the sample end); anything else is shown as Latin-1.
        /// </summary>
        public static Encoding DetectEncoding(byte[] sample, int count, out int bomLength, out int codeUnitSize, out bool bigEndian, out string label)
        {
            bomLength = 0; codeUnitSize = 1; bigEndian = false;
            if (count >= 3 && sample[0] == 0xEF && sample[1] == 0xBB && sample[2] == 0xBF)
            {
                bomLength = 3; label = "UTF-8 BOM";
                return new UTF8Encoding(false, false);
            }
            if (count >= 2 && sample[0] == 0xFF && sample[1] == 0xFE)
            {
                bomLength = 2; codeUnitSize = 2; label = "UTF-16 LE";
                return new UnicodeEncoding(false, false, false);
            }
            if (count >= 2 && sample[0] == 0xFE && sample[1] == 0xFF)
            {
                bomLength = 2; codeUnitSize = 2; bigEndian = true; label = "UTF-16 BE";
                return new UnicodeEncoding(true, false, false);
            }
            int pairs = count / 2;
            if (pairs >= 8)
            {
                int zeroEven = 0, zeroOdd = 0;
                for (int i = 0; i + 1 < count; i += 2)
                {
                    if (sample[i] == 0) zeroEven++;
                    if (sample[i + 1] == 0) zeroOdd++;
                }
                if (zeroOdd > pairs * 0.3 && zeroEven < pairs * 0.05)
                {
                    codeUnitSize = 2; label = "UTF-16 LE";
                    return new UnicodeEncoding(false, false, false);
                }
                if (zeroEven > pairs * 0.3 && zeroOdd < pairs * 0.05)
                {
                    codeUnitSize = 2; bigEndian = true; label = "UTF-16 BE";
                    return new UnicodeEncoding(true, false, false);
                }
            }
            if (IsValidUtf8Prefix(sample, count))
            {
                label = "UTF-8";
                return new UTF8Encoding(false, false);
            }
            label = "Latin-1";
            return Encoding.GetEncoding(28591);
        }

        private static bool IsValidUtf8Prefix(byte[] b, int count)
        {
            int i = 0;
            while (i < count)
            {
                byte c = b[i];
                if (c < 0x80) { i++; continue; }
                int need;
                if (c >= 0xC2 && c <= 0xDF) need = 1;
                else if (c >= 0xE0 && c <= 0xEF) need = 2;
                else if (c >= 0xF0 && c <= 0xF4) need = 3;
                else return false;
                if (i + need >= count) return true; // sequence cut by the sample boundary
                for (int k = 1; k <= need; k++)
                {
                    if ((b[i + k] & 0xC0) != 0x80) return false;
                }
                i += need + 1;
            }
            return true;
        }

        // ------------------------------------------------------------------ state

        public long FileLength { get { return Interlocked.Read(ref fileLength); } }
        public long IndexedBytes { get { lock (indexLock) { return indexedBytes; } } }
        public bool IsIndexComplete { get { return indexComplete; } }
        public bool IsIndexing { get { return workerRunning; } }

        /// <summary>Lines known so far (newlines found + 1). Grows while indexing; final once IsIndexComplete.</summary>
        public long LineCount { get { lock (indexLock) { return newlineCount + 1; } } }

        /// <summary>0..1 fraction of the file that has been indexed.</summary>
        public double IndexProgress
        {
            get
            {
                long len = FileLength;
                if (indexComplete || len <= 0) return 1.0;
                return Math.Min(1.0, (double)IndexedBytes / len);
            }
        }

        /// <summary>Total line estimate from the indexed portion (exact when indexing is complete).</summary>
        public long EstimatedTotalLines
        {
            get
            {
                lock (indexLock)
                {
                    if (indexComplete || indexedBytes <= BomLength || fileLength <= indexedBytes) return newlineCount + 1;
                    double perByte = (double)newlineCount / Math.Max(1, indexedBytes - BomLength);
                    return Math.Max(newlineCount + 1, (long)(perByte * (fileLength - BomLength)) + 1);
                }
            }
        }

        public int CheckpointCount { get { lock (indexLock) { return cpCount; } } }

        /// <summary>Approximate managed memory held by the index and page cache.</summary>
        public long ApproximateMemoryBytes
        {
            get
            {
                long b;
                lock (indexLock) { b = (long)cpLine.Length * 16; }
                lock (streamLock) { b += (long)pageCache.Count * PageSize; }
                return b;
            }
        }

        // ------------------------------------------------------------------ indexing

        /// <summary>Starts (or continues) the background index pass up to the current target length.</summary>
        public void EnsureIndexing()
        {
            if (disposed) return;
            lock (workerLock)
            {
                if (workerRunning) return;
                if (indexComplete && IndexedBytes >= Interlocked.Read(ref indexTargetLength)) return;
                workerRunning = true;
            }
            int gen = Volatile_Read(ref generation);
            Thread t = new Thread(delegate () { IndexWorker(gen); });
            t.IsBackground = true;
            t.Name = "codeLess large-file indexer";
            t.Priority = ThreadPriority.BelowNormal;
            t.Start();
        }

        private static int Volatile_Read(ref int v) { return Volatile.Read(ref v); }

        /// <summary>Blocks until the current index pass finishes (tests / tools). Returns false on timeout.</summary>
        public bool WaitForIndex(int timeoutMs)
        {
            DateTime until = DateTime.UtcNow.AddMilliseconds(timeoutMs);
            while (DateTime.UtcNow < until)
            {
                if (!workerRunning && indexComplete) return true;
                if (!workerRunning && !indexComplete) EnsureIndexing();
                Thread.Sleep(5);
            }
            return !workerRunning && indexComplete;
        }

        private void IndexWorker(int gen)
        {
            FileStream fs = null;
            bool appended = false;
            try
            {
                fs = new FileStream(FilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1, FileOptions.SequentialScan);
                byte[] buf = new byte[IndexChunkSize];
                long[] newStarts = new long[4096];
                while (!disposed && gen == Volatile_Read(ref generation))
                {
                    long pos; long target;
                    lock (indexLock) { pos = indexedBytes; target = indexTargetLength; }
                    if (CodeUnitSize == 2)
                    {
                        // Keep reads aligned to whole UTF-16 units; a trailing odd byte is ignored until completed.
                        target = BomLength + ((target - BomLength) / 2) * 2;
                    }
                    if (pos >= target) break;
                    int want = (int)Math.Min(buf.Length, target - pos);
                    int n = ReadFully(fs, pos, buf, 0, want);
                    if (n <= 0) break;
                    if (CodeUnitSize == 2) n -= n % 2;
                    if (n <= 0) break;

                    int found = 0;
                    if (CodeUnitSize == 1)
                    {
                        int i = 0;
                        while (i < n)
                        {
                            int p = Array.IndexOf(buf, (byte)0x0A, i, n - i);
                            if (p < 0) break;
                            if (found == newStarts.Length) Array.Resize(ref newStarts, newStarts.Length * 2);
                            newStarts[found++] = pos + p + 1;
                            i = p + 1;
                        }
                    }
                    else
                    {
                        int i = 0;
                        while (i < n)
                        {
                            int p = Array.IndexOf(buf, (byte)0x0A, i, n - i);
                            if (p < 0) break;
                            int unitStart = IsBigEndian ? p - 1 : p;
                            if (unitStart >= 0 && ((pos + unitStart - BomLength) & 1) == 0 && unitStart + 1 < n)
                            {
                                byte other = IsBigEndian ? buf[unitStart] : buf[unitStart + 1];
                                if (other == 0)
                                {
                                    if (found == newStarts.Length) Array.Resize(ref newStarts, newStarts.Length * 2);
                                    newStarts[found++] = pos + unitStart + 2;
                                }
                            }
                            i = p + 1;
                        }
                    }

                    lock (indexLock)
                    {
                        if (gen != generation) break;
                        for (int k = 0; k < found; k++)
                        {
                            long start = newStarts[k];
                            newlineCount++;
                            lastLineStart = start;
                            if (newlineCount - lastCheckpointLine >= CheckpointLineInterval || start - lastCheckpointOffset >= CheckpointByteInterval)
                            {
                                if (cpCount == cpLine.Length)
                                {
                                    Array.Resize(ref cpLine, cpLine.Length * 2);
                                    Array.Resize(ref cpOffset, cpOffset.Length * 2);
                                }
                                cpLine[cpCount] = newlineCount;
                                cpOffset[cpCount] = start;
                                cpCount++;
                                lastCheckpointLine = newlineCount;
                                lastCheckpointOffset = start;
                            }
                        }
                        indexedBytes = pos + n;
                        if (found > 0) appended = true;
                    }
                    RaiseProgress(false);
                }
                lock (indexLock)
                {
                    if (gen == generation && indexedBytes >= indexTargetLength - (CodeUnitSize == 2 ? 1 : 0))
                        indexComplete = true;
                }
                LastError = null;
            }
            catch (Exception ex)
            {
                LastError = ex.Message;
                lock (indexLock) { if (gen == generation) indexComplete = true; }
            }
            finally
            {
                if (fs != null) { try { fs.Dispose(); } catch { } }
                lock (workerLock) { workerRunning = false; }
            }
            if (disposed) return;
            if (gen != Volatile_Read(ref generation))
            {
                EnsureIndexing();
                return;
            }
            // Follow mode may have raised the target while we were finishing the last chunk.
            if (IndexedBytes < Interlocked.Read(ref indexTargetLength) - (CodeUnitSize == 2 ? 1 : 0))
            {
                lock (indexLock) { indexComplete = false; }
                EnsureIndexing();
            }
            RaiseProgress(true);
            if (appended || follow)
            {
                EventHandler h = ContentAppended;
                if (h != null && follow) { try { h(this, EventArgs.Empty); } catch { } }
            }
        }

        private void RaiseProgress(bool force)
        {
            long now = DateTime.UtcNow.Ticks;
            if (!force && now - Interlocked.Read(ref lastProgressTicks) < TimeSpan.TicksPerMillisecond * 100) return;
            Interlocked.Exchange(ref lastProgressTicks, now);
            EventHandler h = IndexProgressChanged;
            if (h != null) { try { h(this, EventArgs.Empty); } catch { } }
        }

        // ------------------------------------------------------------------ follow / tail -f

        /// <summary>When true the host should call PollForChanges periodically (the viewers use a 500 ms timer).</summary>
        public bool Follow
        {
            get { return follow; }
            set { follow = value; }
        }

        /// <summary>
        /// Checks the file on disk: growth extends the index (ContentAppended is raised when indexed), a shrink or a changed
        /// head (log rotation: a new file at the same path) resets the index (ContentReset). Cheap: one stat + a 4 KB read.
        /// Safe to call from any thread. Returns true if something changed.
        /// </summary>
        public bool PollForChanges()
        {
            if (disposed) return false;
            long len;
            byte[] head;
            try
            {
                using (FileStream fs = OpenShared(FilePath, 1))
                {
                    len = fs.Length;
                    int sigLen;
                    lock (indexLock) { sigLen = headSignature.Length; }
                    head = new byte[(int)Math.Min(sigLen, len)];
                    if (head.Length > 0) ReadFully(fs, 0, head, 0, head.Length);
                }
                LastError = null;
            }
            catch (Exception ex)
            {
                LastError = ex.Message; // e.g. rotated away and not yet re-created; try again next poll
                return false;
            }

            bool replaced = false;
            long known;
            lock (indexLock)
            {
                known = indexTargetLength;
                if (len < known || head.Length < headSignature.Length) replaced = true;
                else
                {
                    for (int i = 0; i < head.Length; i++)
                    {
                        if (head[i] != headSignature[i]) { replaced = true; break; }
                    }
                }
            }

            if (replaced)
            {
                ResetAndReindex();
                return true;
            }
            if (len > known)
            {
                bool refreshSig;
                lock (indexLock)
                {
                    indexTargetLength = len;
                    fileLength = len;
                    indexComplete = false;
                    // The file was tiny when opened: extend the rotation signature as content arrives.
                    refreshSig = headSignature.Length < HeadSignatureLength;
                }
                if (refreshSig) RefreshHeadSignature();
                EnsureIndexing();
                return true;
            }
            return false;
        }

        private void RefreshHeadSignature()
        {
            try
            {
                using (FileStream fs = OpenShared(FilePath, 1))
                {
                    byte[] sig = new byte[(int)Math.Min(HeadSignatureLength, fs.Length)];
                    int n = ReadFully(fs, 0, sig, 0, sig.Length);
                    if (n < sig.Length) Array.Resize(ref sig, n);
                    lock (indexLock) { if (sig.Length >= headSignature.Length) headSignature = sig; }
                }
            }
            catch { }
        }

        /// <summary>Drops the index and page cache, re-detects the encoding and re-indexes from the start.</summary>
        public void ResetAndReindex()
        {
            if (disposed) return;
            Interlocked.Increment(ref generation);
            try { OpenStreamAndDetect(); }
            catch (Exception ex)
            {
                LastError = ex.Message;
                return;
            }
            ResetCount++;
            EventHandler h = ContentReset;
            if (h != null) { try { h(this, EventArgs.Empty); } catch { } }
            EnsureIndexing();
        }

        // ------------------------------------------------------------------ line access

        /// <summary>Absolute byte offset where the line starts, or -1 if that line has not been indexed yet.</summary>
        public long GetLineStartOffset(long line)
        {
            if (line < 0) return -1;
            long cpL, cpO;
            lock (indexLock)
            {
                if (line > newlineCount) return -1;
                if (line == newlineCount) return lastLineStart;
                int idx = FindCheckpointForLine(line);
                cpL = cpLine[idx];
                cpO = cpOffset[idx];
            }
            long offset = cpO;
            for (long l = cpL; l < line; l++)
            {
                long nl = FindNextNewlineUnit(offset, long.MaxValue);
                if (nl < 0) return -1;
                offset = nl + CodeUnitSize;
            }
            return offset;
        }

        private int FindCheckpointForLine(long line)
        {
            int lo = 0, hi = cpCount - 1;
            while (lo < hi)
            {
                int mid = (lo + hi + 1) >> 1;
                if (cpLine[mid] <= line) lo = mid; else hi = mid - 1;
            }
            return lo;
        }

        /// <summary>Line containing the absolute byte offset (within the indexed range), or -1.</summary>
        public long GetLineForOffset(long byteOffset)
        {
            long cpL, cpO, end;
            lock (indexLock)
            {
                if (byteOffset < BomLength) return 0;
                if (byteOffset >= lastLineStart) return byteOffset <= fileLength ? newlineCount : -1;
                int lo = 0, hi = cpCount - 1;
                while (lo < hi)
                {
                    int mid = (lo + hi + 1) >> 1;
                    if (cpOffset[mid] <= byteOffset) lo = mid; else hi = mid - 1;
                }
                cpL = cpLine[lo];
                cpO = cpOffset[lo];
                end = indexedBytes;
            }
            long line = cpL;
            long offset = cpO;
            while (offset <= byteOffset)
            {
                long nl = FindNextNewlineUnit(offset, end);
                if (nl < 0 || nl >= byteOffset) break;
                line++;
                offset = nl + CodeUnitSize;
            }
            return line;
        }

        /// <summary>Decoded text of a line (CR/LF stripped). Long lines are truncated to MaxDisplayLineChars.</summary>
        public string GetLine(long line)
        {
            bool truncated;
            return GetLine(line, out truncated);
        }

        public string GetLine(long line, out bool truncated)
        {
            truncated = false;
            long start = GetLineStartOffset(line);
            if (start < 0) return "";
            return DecodeLineAt(start, out truncated);
        }

        /// <summary>Reads count consecutive lines starting at first with a single forward walk (used for rendering).</summary>
        public string[] GetLines(long first, int count)
        {
            if (count <= 0) return new string[0];
            long total = LineCount;
            if (first < 0) first = 0;
            if (first >= total) return new string[0];
            int n = (int)Math.Min(count, total - first);
            string[] result = new string[n];
            long offset = GetLineStartOffset(first);
            if (offset < 0) return new string[0];
            long limit = FileLength;
            for (int i = 0; i < n; i++)
            {
                bool tr;
                long next;
                result[i] = DecodeLineAt(offset, out tr, out next);
                if (next < 0 || next > limit)
                {
                    if (i + 1 < n) Array.Resize(ref result, i + 1);
                    break;
                }
                offset = next;
            }
            return result;
        }

        private string DecodeLineAt(long start, out bool truncated)
        {
            long next;
            return DecodeLineAt(start, out truncated, out next);
        }

        /// <summary>Decodes the line starting at start; next receives the offset of the following line or -1 at EOF.</summary>
        private string DecodeLineAt(long start, out bool truncated, out long next)
        {
            truncated = false;
            long maxBytes = (long)MaxDisplayLineChars * (CodeUnitSize == 2 ? 2 : 4);
            long nl = FindNextNewlineUnit(start, start + maxBytes + CodeUnitSize);
            long end;
            if (nl >= 0)
            {
                end = nl;
                next = nl + CodeUnitSize;
            }
            else
            {
                long len = FileLength;
                if (len - start > maxBytes)
                {
                    end = start + maxBytes;
                    truncated = true;
                    long realNl = FindNextNewlineUnit(end, long.MaxValue);
                    next = realNl >= 0 ? realNl + CodeUnitSize : -1;
                }
                else
                {
                    end = len;
                    next = -1;
                }
            }
            if (CodeUnitSize == 2) end = start + ((end - start) / 2) * 2;
            int byteLen = (int)Math.Max(0, end - start);
            if (byteLen == 0) return "";
            byte[] bytes = ReadBytes(start, byteLen);
            int usable = bytes.Length;
            // Strip the CR of a CRLF pair.
            if (!truncated)
            {
                if (CodeUnitSize == 1 && usable > 0 && bytes[usable - 1] == 0x0D) usable--;
                else if (CodeUnitSize == 2 && usable >= 2)
                {
                    bool cr = IsBigEndian ? (bytes[usable - 2] == 0 && bytes[usable - 1] == 0x0D) : (bytes[usable - 2] == 0x0D && bytes[usable - 1] == 0);
                    if (cr) usable -= 2;
                }
            }
            string s = TextEncoding.GetString(bytes, 0, usable);
            if (s.Length > MaxDisplayLineChars)
            {
                s = s.Substring(0, MaxDisplayLineChars);
                truncated = true;
            }
            return s;
        }

        /// <summary>Character column of an absolute byte offset inside a line (for search hits found at byte level).</summary>
        public int ByteOffsetToColumn(long line, long byteOffset)
        {
            long start = GetLineStartOffset(line);
            if (start < 0 || byteOffset <= start) return 0;
            int len = (int)Math.Min(byteOffset - start, (long)MaxDisplayLineChars * 4);
            byte[] b = ReadBytes(start, len);
            return TextEncoding.GetCharCount(b, 0, b.Length);
        }

        /// <summary>
        /// Text between two (line, column) positions, joined with "\n". Stops after maxChars characters; truncated tells
        /// whether the cap was hit (the viewers cap clipboard copies at 16M characters).
        /// </summary>
        public string GetText(long startLine, int startCol, long endLine, int endCol, int maxChars, out bool truncated)
        {
            truncated = false;
            if (endLine < startLine || (endLine == startLine && endCol < startCol))
            {
                long tl = startLine; startLine = endLine; endLine = tl;
                int tc = startCol; startCol = endCol; endCol = tc;
            }
            StringBuilder sb = new StringBuilder();
            long total = LineCount;
            if (startLine >= total) return "";
            if (endLine >= total) { endLine = total - 1; endCol = int.MaxValue; }
            long offset = GetLineStartOffset(startLine);
            if (offset < 0) return "";
            for (long l = startLine; l <= endLine; l++)
            {
                bool tr; long next;
                string text = DecodeLineAt(offset, out tr, out next);
                int from = l == startLine ? Math.Min(Math.Max(0, startCol), text.Length) : 0;
                int to = l == endLine ? Math.Min(Math.Max(0, endCol), text.Length) : text.Length;
                if (to > from) sb.Append(text, from, to - from);
                if (l < endLine) sb.Append('\n');
                if (sb.Length >= maxChars)
                {
                    sb.Length = maxChars;
                    truncated = true;
                    break;
                }
                if (next < 0) break;
                offset = next;
            }
            return sb.ToString();
        }

        // ------------------------------------------------------------------ byte access (page cache)

        /// <summary>Absolute offset of the next newline code unit at or after from (and before limit), or -1.</summary>
        private long FindNextNewlineUnit(long from, long limit)
        {
            long len = FileLength;
            if (limit > len) limit = len;
            if (CodeUnitSize == 2 && ((from - BomLength) & 1) != 0) from++;
            long pos = from;
            while (pos < limit)
            {
                long pageStart = (pos / PageSize) * PageSize;
                byte[] data; int valid;
                if (!GetPage(pageStart, out data, out valid)) return -1;
                int i = (int)(pos - pageStart);
                int end = (int)Math.Min(valid, limit - pageStart);
                if (i >= end) return -1;
                while (i < end)
                {
                    int p = Array.IndexOf(data, (byte)0x0A, i, end - i);
                    if (p < 0) break;
                    if (CodeUnitSize == 1) return pageStart + p;
                    int unitStart = IsBigEndian ? p - 1 : p;
                    if (unitStart >= 0 && ((pageStart + unitStart - BomLength) & 1) == 0 && unitStart + 1 < valid)
                    {
                        byte other = IsBigEndian ? data[unitStart] : data[unitStart + 1];
                        if (other == 0) return pageStart + unitStart;
                    }
                    i = p + 1;
                }
                pos = pageStart + PageSize;
                if (valid < PageSize) return -1;
            }
            return -1;
        }

        public byte[] ReadBytes(long offset, int count)
        {
            byte[] result = new byte[count];
            int done = 0;
            while (done < count)
            {
                long pos = offset + done;
                long pageStart = (pos / PageSize) * PageSize;
                byte[] data; int valid;
                if (!GetPage(pageStart, out data, out valid)) break;
                int i = (int)(pos - pageStart);
                if (i >= valid) break;
                int take = Math.Min(valid - i, count - done);
                Buffer.BlockCopy(data, i, result, done, take);
                done += take;
            }
            if (done < count) Array.Resize(ref result, done);
            return result;
        }

        private bool GetPage(long pageStart, out byte[] data, out int valid)
        {
            lock (streamLock)
            {
                data = null; valid = 0;
                if (readStream == null) return false;
                CachedPage page;
                long len = FileLength;
                if (pageCache.TryGetValue(pageStart, out page))
                {
                    if (page.Valid == PageSize || pageStart + page.Valid >= len)
                    {
                        page.LastUse = ++pageClock;
                        data = page.Data; valid = page.Valid;
                        return true;
                    }
                    pageCache.Remove(pageStart); // partial page and the file has grown since: re-read
                }
                if (pageCache.Count >= MaxCachedPages)
                {
                    long victim = -1; long oldest = long.MaxValue;
                    foreach (KeyValuePair<long, CachedPage> kv in pageCache)
                    {
                        if (kv.Value.LastUse < oldest) { oldest = kv.Value.LastUse; victim = kv.Key; }
                    }
                    if (victim >= 0) pageCache.Remove(victim);
                }
                byte[] buf = new byte[PageSize];
                int n;
                try { n = ReadFully(readStream, pageStart, buf, 0, PageSize); }
                catch { return false; }
                page = new CachedPage { Data = buf, Valid = n, LastUse = ++pageClock };
                pageCache[pageStart] = page;
                data = buf; valid = n;
                return n > 0;
            }
        }

        private static int ReadFully(FileStream fs, long position, byte[] buffer, int offset, int count)
        {
            fs.Seek(position, SeekOrigin.Begin);
            int total = 0;
            while (total < count)
            {
                int n = fs.Read(buffer, offset + total, count - total);
                if (n <= 0) break;
                total += n;
            }
            return total;
        }

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            Interlocked.Increment(ref generation);
            lock (streamLock)
            {
                if (readStream != null) { try { readStream.Dispose(); } catch { } readStream = null; }
                pageCache.Clear();
            }
            lock (indexLock)
            {
                cpLine = new long[1];
                cpOffset = new long[1];
                cpCount = 1;
            }
        }

        public bool IsDisposed { get { return disposed; } }
    }
}
