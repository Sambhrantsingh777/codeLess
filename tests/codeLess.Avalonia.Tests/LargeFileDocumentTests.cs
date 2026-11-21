using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using codeLess.Studio;
using Xunit;

namespace codeLess.Avalonia.Tests
{
    /// <summary>Line index, paging, search and tail logic of the shared large-file engine (codeLess.Core).</summary>
    public class LargeFileDocumentTests : IDisposable
    {
        private readonly string dir;

        public LargeFileDocumentTests()
        {
            dir = Path.Combine(Path.GetTempPath(), "cl-largefile-tests-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
        }

        public void Dispose()
        {
            try { Directory.Delete(dir, true); } catch { }
        }

        private string WriteFile(string name, byte[] bytes)
        {
            string p = Path.Combine(dir, name);
            File.WriteAllBytes(p, bytes);
            return p;
        }

        private static LargeFileDocument OpenIndexed(string path)
        {
            var doc = LargeFileDocument.Open(path);
            Assert.True(doc.WaitForIndex(20000), "index did not complete");
            return doc;
        }

        [Fact]
        public void Lf_TrailingNewline_ProducesEmptyLastLine()
        {
            string p = WriteFile("a.txt", Encoding.UTF8.GetBytes("alpha\nbeta\ngamma\n"));
            using var doc = OpenIndexed(p);
            Assert.Equal(4, doc.LineCount);
            Assert.Equal("alpha", doc.GetLine(0));
            Assert.Equal("beta", doc.GetLine(1));
            Assert.Equal("gamma", doc.GetLine(2));
            Assert.Equal("", doc.GetLine(3));
            Assert.Equal("UTF-8", doc.EncodingLabel);
        }

        [Fact]
        public void NoTrailingNewline_LastLineIsComplete()
        {
            string p = WriteFile("b.txt", Encoding.UTF8.GetBytes("one\ntwo\nthree"));
            using var doc = OpenIndexed(p);
            Assert.Equal(3, doc.LineCount);
            Assert.Equal("three", doc.GetLine(2));
            Assert.Equal(new[] { "one", "two", "three" }, doc.GetLines(0, 10));
        }

        [Fact]
        public void EmptyFile_HasOneEmptyLine()
        {
            string p = WriteFile("e.txt", new byte[0]);
            using var doc = OpenIndexed(p);
            Assert.Equal(1, doc.LineCount);
            Assert.Equal("", doc.GetLine(0));
        }

        [Fact]
        public void Crlf_IsStripped_AndMixedEndingsWork()
        {
            string p = WriteFile("c.txt", Encoding.UTF8.GetBytes("first\r\nsecond\r\n\r\nfourth\nfifth\r\n"));
            using var doc = OpenIndexed(p);
            Assert.Equal(6, doc.LineCount);
            Assert.Equal(new[] { "first", "second", "", "fourth", "fifth", "" }, doc.GetLines(0, 6));
            bool truncated;
            string text = doc.GetText(0, 2, 1, 3, 1000, out truncated);
            Assert.Equal("rst\nsec", text);
            Assert.False(truncated);
        }

        [Fact]
        public void Utf8Bom_IsSkipped()
        {
            var bytes = new List<byte> { 0xEF, 0xBB, 0xBF };
            bytes.AddRange(Encoding.UTF8.GetBytes("héllo wörld\nzwei\n"));
            string p = WriteFile("bom.txt", bytes.ToArray());
            using var doc = OpenIndexed(p);
            Assert.Equal("UTF-8 BOM", doc.EncodingLabel);
            Assert.Equal("héllo wörld", doc.GetLine(0));
            Assert.Equal("zwei", doc.GetLine(1));
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void Utf16WithBom_LinesAndSearch(bool bigEndian)
        {
            var enc = new UnicodeEncoding(bigEndian, true);
            // U+0A0A and U+0D0A contain 0x0A bytes that are NOT newlines; they must not split lines.
            string text = "Grüße ਊഊ one\r\nline two ünïcode\nlast";
            var bytes = enc.GetPreamble().Concat(enc.GetBytes(text)).ToArray();
            string p = WriteFile("u16.txt", bytes);
            using var doc = OpenIndexed(p);
            Assert.Equal(bigEndian ? "UTF-16 BE" : "UTF-16 LE", doc.EncodingLabel);
            Assert.Equal(2, doc.CodeUnitSize);
            Assert.Equal(3, doc.LineCount);
            Assert.Equal("Grüße ਊഊ one", doc.GetLine(0));
            Assert.Equal("line two ünïcode", doc.GetLine(1));
            Assert.Equal("last", doc.GetLine(2));

            var search = new LargeFileSearch(doc, "ÜNÏ", false, false);
            search.Run();
            Assert.Equal(1, search.TotalMatches);
            var m = search.GetMatch(0);
            Assert.Equal(1, m.Line);
            Assert.Equal(9, m.Column);
        }

        [Fact]
        public void Latin1Fallback_ForInvalidUtf8()
        {
            string p = WriteFile("l1.txt", new byte[] { (byte)'c', (byte)'a', (byte)'f', 0xE9, (byte)'\n', (byte)'x' });
            using var doc = OpenIndexed(p);
            Assert.Equal("Latin-1", doc.EncodingLabel);
            Assert.Equal("café", doc.GetLine(0));
        }

        [Fact]
        public void LineAvailableBeforeIndexing_FirstLineReadable()
        {
            string p = WriteFile("pre.txt", Encoding.UTF8.GetBytes("header line\nrest\n"));
            using var doc = LargeFileDocument.Open(p, false);
            Assert.False(doc.IsIndexComplete);
            Assert.Equal("header line", doc.GetLine(0));
            doc.EnsureIndexing();
            Assert.True(doc.WaitForIndex(10000));
            Assert.Equal(3, doc.LineCount);
        }

        private static List<string> BuildVariedLines(int count, int seed)
        {
            var rnd = new Random(seed);
            var lines = new List<string>(count);
            for (int i = 0; i < count; i++)
            {
                int len = rnd.Next(0, 10) == 0 ? rnd.Next(2000, 90000) : rnd.Next(0, 120); // some lines cross checkpoint byte intervals
                var sb = new StringBuilder();
                sb.Append("L").Append(i).Append(':');
                while (sb.Length < len) sb.Append((char)('a' + rnd.Next(26)));
                lines.Add(sb.ToString());
            }
            return lines;
        }

        [Fact]
        public void CheckpointIndex_RandomAccessMatchesSource()
        {
            var lines = BuildVariedLines(6000, 7);
            string p = WriteFile("big.txt", Encoding.UTF8.GetBytes(string.Join("\r\n", lines)));
            using var doc = OpenIndexed(p);
            Assert.Equal(lines.Count, doc.LineCount);
            Assert.True(doc.CheckpointCount > lines.Count / LargeFileDocument.CheckpointLineInterval);
            var rnd = new Random(3);
            for (int k = 0; k < 400; k++)
            {
                int i = rnd.Next(lines.Count);
                string expect = lines[i].Length > LargeFileDocument.MaxDisplayLineChars ? lines[i].Substring(0, LargeFileDocument.MaxDisplayLineChars) : lines[i];
                Assert.Equal(expect, doc.GetLine(i));
            }
            var window = doc.GetLines(1234, 50);
            Assert.Equal(50, window.Length);
            for (int i = 0; i < 50; i++)
                Assert.StartsWith("L" + (1234 + i) + ":", window[i]);

            long off = doc.GetLineStartOffset(4321);
            Assert.Equal(4321, doc.GetLineForOffset(off));
            Assert.Equal(4321, doc.GetLineForOffset(off + 2));
        }

        [Fact]
        public void VeryLongLine_IsTruncatedForDisplay()
        {
            string longLine = new string('x', LargeFileDocument.MaxDisplayLineChars * 3);
            string p = WriteFile("long.txt", Encoding.UTF8.GetBytes("a\n" + longLine + "\nafter\n"));
            using var doc = OpenIndexed(p);
            bool truncated;
            Assert.Equal(LargeFileDocument.MaxDisplayLineChars, doc.GetLine(1, out truncated).Length);
            Assert.True(truncated);
            Assert.Equal(new[] { "a", new string('x', LargeFileDocument.MaxDisplayLineChars), "after", "" }, doc.GetLines(0, 4));
        }

        private static int BruteCount(List<string> lines, string q, StringComparison cmp)
        {
            int n = 0;
            foreach (var l in lines)
            {
                int i = 0;
                while ((i = l.IndexOf(q, i, cmp)) >= 0) { n++; i += q.Length; }
            }
            return n;
        }

        [Fact]
        public void ByteSearch_CountsLinesAndColumns_AcrossChunkBoundaries()
        {
            // > 4 MB so the 4 MB search chunks have boundaries inside the data.
            var rnd = new Random(11);
            var lines = new List<string>();
            long size = 0;
            int i = 0;
            while (size < 9 * 1024 * 1024)
            {
                string l = "2026-11-22 12:00:" + (i % 60).ToString("00") + " INFO worker-" + rnd.Next(100) + (rnd.Next(7) == 0 ? " Needle found ünïcode" : " ok") + " id=" + i;
                lines.Add(l);
                size += Encoding.UTF8.GetByteCount(l) + 1;
                i++;
            }
            string p = WriteFile("search.log", Encoding.UTF8.GetBytes(string.Join("\n", lines) + "\n"));
            using var doc = OpenIndexed(p);

            var s1 = new LargeFileSearch(doc, "Needle", true, false);
            s1.Run();
            Assert.Equal("bytes", s1.Strategy);
            Assert.Equal(BruteCount(lines, "Needle", StringComparison.Ordinal), s1.TotalMatches);

            var s2 = new LargeFileSearch(doc, "nEEdle", false, false);
            s2.Run();
            Assert.Equal("bytes", s2.Strategy);
            Assert.Equal(s1.TotalMatches, s2.TotalMatches);

            for (int k = 0; k < s1.StoredMatchCount; k += 997)
            {
                var m = s1.GetMatch(k);
                string line = lines[(int)m.Line];
                int col = m.Column >= 0 ? m.Column : doc.ByteOffsetToColumn(m.Line, m.ByteOffset);
                Assert.Equal("Needle", line.Substring(col, 6));
                Assert.Equal(line, doc.GetLine(m.Line));
            }

            var s3 = new LargeFileSearch(doc, "worker-4[0-9] Needle", true, true);
            s3.Run();
            Assert.Equal("lines", s3.Strategy);
            int expectRx = lines.Sum(l => Regex.Matches(l, "worker-4[0-9] Needle").Count);
            Assert.Equal(expectRx, s3.TotalMatches);

            // Navigation helper
            var first = s1.GetMatch(0);
            Assert.Equal(0, s1.FindNextMatchIndex(0, 0));
            Assert.Equal(1, s1.FindNextMatchIndex(first.Line, (first.Column >= 0 ? first.Column : 0) + 1));
        }

        [Fact]
        public void Search_CapsStoredMatchesButCountsAll()
        {
            var sb = new StringBuilder();
            for (int i = 0; i < 5000; i++) sb.Append("abc abc\n");
            string p = WriteFile("cap.txt", Encoding.UTF8.GetBytes(sb.ToString()));
            using var doc = OpenIndexed(p);
            var s = new LargeFileSearch(doc, "abc", true, false, 100);
            s.Run();
            Assert.Equal(10000, s.TotalMatches);
            Assert.Equal(100, s.StoredMatchCount);
            Assert.True(s.IsCapped);
            Assert.Equal(49, s.GetMatch(99).Line);
            Assert.Equal(4, s.GetMatch(99).Column);
        }

        [Fact]
        public void InvalidRegex_ReportsError()
        {
            string p = WriteFile("rx.txt", Encoding.UTF8.GetBytes("a\n"));
            using var doc = OpenIndexed(p);
            var s = new LargeFileSearch(doc, "([", true, true);
            s.Run();
            Assert.True(s.IsCompleted);
            Assert.Contains("Invalid regular expression", s.Error);
        }

        [Fact]
        public void Tail_AppendsGrowth_IncludingPartialLine()
        {
            string p = WriteFile("tail.log", Encoding.UTF8.GetBytes("one\ntwo"));
            using var doc = OpenIndexed(p);
            doc.Follow = true;
            int appended = 0;
            doc.ContentAppended += (s, e) => Interlocked.Increment(ref appended);
            Assert.Equal(2, doc.LineCount);
            Assert.Equal("two", doc.GetLine(1));

            using (var fs = new FileStream(p, FileMode.Append, FileAccess.Write, FileShare.ReadWrite))
            {
                var more = Encoding.UTF8.GetBytes(" (cont)\nthree\nfour\n");
                fs.Write(more, 0, more.Length);
            }
            Assert.True(doc.PollForChanges());
            Assert.True(doc.WaitForIndex(10000));
            Assert.Equal(5, doc.LineCount);
            Assert.Equal("two (cont)", doc.GetLine(1));
            Assert.Equal("four", doc.GetLine(3));
            Assert.False(doc.PollForChanges());
            SpinWait.SpinUntil(() => Volatile.Read(ref appended) > 0, 2000);
            Assert.True(appended > 0);
        }

        [Fact]
        public void Tail_TruncationResetsIndex()
        {
            string p = WriteFile("trunc.log", Encoding.UTF8.GetBytes("aaaa\nbbbb\ncccc\ndddd\n"));
            using var doc = OpenIndexed(p);
            doc.Follow = true;
            int resets = 0;
            doc.ContentReset += (s, e) => Interlocked.Increment(ref resets);
            File.WriteAllText(p, "new\n");
            Assert.True(doc.PollForChanges());
            Assert.True(doc.WaitForIndex(10000));
            Assert.Equal(1, resets);
            Assert.Equal(1, doc.ResetCount);
            Assert.Equal(2, doc.LineCount);
            Assert.Equal("new", doc.GetLine(0));
        }

        [Fact]
        public void Tail_RotationWithSameOrLargerSize_IsDetectedByHeadSignature()
        {
            string p = WriteFile("rot.log", Encoding.UTF8.GetBytes("2026-11-22 boot\nline\n"));
            using var doc = OpenIndexed(p);
            doc.Follow = true;
            // Rotate: move the old file away and create a new, larger one at the same path.
            File.Move(p, p + ".1");
            File.WriteAllText(p, "2026-11-23 boot\nfresh line one\nfresh line two\n");
            Assert.True(doc.PollForChanges());
            Assert.True(doc.WaitForIndex(10000));
            Assert.Equal(1, doc.ResetCount);
            Assert.Equal("2026-11-23 boot", doc.GetLine(0));
            Assert.Equal("fresh line two", doc.GetLine(2));
        }

        [Fact]
        public void Tail_MissingFileDuringRotation_KeepsOldContent()
        {
            string p = WriteFile("gone.log", Encoding.UTF8.GetBytes("keep me\n"));
            using var doc = OpenIndexed(p);
            File.Delete(p);
            Assert.False(doc.PollForChanges());
            Assert.NotNull(doc.LastError);
            Assert.Equal("keep me", doc.GetLine(0));
        }

        [Fact]
        public void Policy_UsesSizeAndLineThresholds()
        {
            string small = WriteFile("small.txt", Encoding.UTF8.GetBytes("x\n"));
            string reason;
            Assert.False(LargeFilePolicy.ShouldUseViewer(small, 1024, 2000000, out reason));
            byte[] many = new byte[5 * 1024 * 1024];
            for (int i = 0; i < many.Length; i++) many[i] = (byte)((i % 2 == 1) ? '\n' : 'a'); // 2.6M short lines
            string dense = WriteFile("dense.txt", many);
            Assert.True(LargeFilePolicy.ShouldUseViewer(dense, 50L * 1024 * 1024, 2000000, out reason));
            Assert.Equal("lines", reason);
            Assert.True(LargeFilePolicy.ShouldUseViewer(dense, 1024 * 1024, 2000000, out reason));
            Assert.Equal("size", reason);

            var settings = new IdePersistentSettings();
            Assert.Equal(50, settings.LargeFileThresholdMB);
            Assert.Equal(2000000, settings.LargeFileLineThreshold);
            Assert.Equal(128, settings.LargeFileEditLimitMB);
        }
    }
}
