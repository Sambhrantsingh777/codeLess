using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using codeLess.Studio;
using Xunit;

namespace codeLess.Avalonia.Tests
{
    /// <summary>
    /// QA #23 / #26: the shared inline-review engine used by both editors (WinForms and Avalonia). Per-hunk Reject must
    /// restore every removed line; decisions compose in any order; edits during review never corrupt the file.
    /// </summary>
    public class InlineDiffReviewCoreTests
    {
        private static EditorTabBuffer Buf(string text)
        {
            var b = new EditorTabBuffer { FilePath = "", FileName = "t.txt", Content = text, SavedContent = text, HasFinalNewline = text.EndsWith("\n") };
            b.SyncLinesFromContent();
            b.IsDirty = false;
            return b;
        }

        private static List<EditorDiffHunk> Pending(EditorTabBuffer b) => b.DiffHunks.Where(h => h.IsPending).OrderBy(h => h.StartLine).ToList();

        [Fact]
        public void MultiLineReplacement_RejectRestoresAllRemovedLines()
        {
            var b = Buf("k1\no1\no2\no3\nk2\n");
            Assert.Equal(1, InlineDiffReview.Stage(b, "k1\nn1\nk2\n"));
            Assert.True(b.IsDirty);
            Assert.True(InlineDiffReview.RejectHunk(b, Pending(b)[0]));
            Assert.Equal("k1\no1\no2\no3\nk2\n", b.Content);
            Assert.False(InlineDiffReview.HasPending(b));
            Assert.Null(b.PreDiffContent);
            Assert.False(b.IsDirty);
        }

        [Fact]
        public void PureInsertion_And_PureDeletion_RoundTrip()
        {
            var b = Buf("a\nb\nc\n");
            InlineDiffReview.Stage(b, "a\nx\ny\nb\nc\n");
            var h = Pending(b).Single();
            Assert.Empty(h.RemovedLines);
            Assert.True(InlineDiffReview.RejectHunkAtLine(b, h.StartLine + 1)); // any line inside the hunk
            Assert.Equal("a\nb\nc\n", b.Content);

            InlineDiffReview.Stage(b, "a\n");
            h = Pending(b).Single();
            Assert.Empty(h.AddedLines);
            Assert.Equal(new[] { "b", "c" }, h.RemovedLines);
            Assert.True(InlineDiffReview.RejectHunkAtLine(b, InlineDiffReview.AnchorLine(b, h)));
            Assert.Equal("a\nb\nc\n", b.Content);
        }

        [Fact]
        public void DeletionAtEndOfFileWithoutFinalNewline_AnchorsOnLastLine()
        {
            var b = Buf("a\nb\nc");
            InlineDiffReview.Stage(b, "a");
            var h = Pending(b).Single();
            int anchor = InlineDiffReview.AnchorLine(b, h);
            Assert.Equal(b.Lines.Count - 1, anchor);
            Assert.Same(h, InlineDiffReview.FindHunkAtLine(b, anchor));
            Assert.True(InlineDiffReview.RejectHunkAtLine(b, anchor));
            Assert.Equal("a\nb\nc", b.Content);
        }

        [Fact]
        public void MixedAddRemove_AdjacentHunks_AnyOrder()
        {
            string baseText = "l1\nl2\nl3\nl4\nl5\nl6\nl7\nl8\n";
            string proposal = "l1\nX2\nX3\nl4\nl6\nY\nl7\nZ8a\nZ8b\n";
            foreach (var order in new[] { new[] { 0, 1, 2, 3 }, new[] { 3, 2, 1, 0 }, new[] { 1, 3, 0, 2 } })
                foreach (int mask in Enumerable.Range(0, 16))
                {
                    var b = Buf(baseText);
                    InlineDiffReview.Stage(b, proposal);
                    var original = Pending(b);
                    Assert.Equal(4, original.Count); // replace, delete l5, insert Y, replace l8
                    string expected = Expected(baseText, original, i => (mask & (1 << i)) != 0);
                    foreach (int i in order)
                    {
                        bool accept = (mask & (1 << i)) != 0;
                        Assert.True(accept ? InlineDiffReview.AcceptHunk(b, original[i]) : InlineDiffReview.RejectHunk(b, original[i]), "order " + string.Join(",", order) + " mask " + mask + " hunk " + i);
                    }
                    Assert.Equal(expected, b.Content);
                    Assert.False(InlineDiffReview.HasPending(b));
                }
        }

        [Fact]
        public void RejectAll_AfterPartialAccept_KeepsAccepted()
        {
            var b = Buf("a\nb\nc\nd\ne\n");
            InlineDiffReview.Stage(b, "A\nb\nc\nd\nE\n");
            InlineDiffReview.AcceptHunkAtLine(b, 0);
            InlineDiffReview.RejectAll(b);
            Assert.Equal("A\nb\nc\nd\ne\n", b.Content);
            Assert.True(b.IsDirty);
            Assert.Null(b.PreDiffContent);
        }

        [Fact]
        public void AcceptAll_AfterPartialReject_KeepsRejected()
        {
            var b = Buf("a\nb\nc\nd\ne\n");
            InlineDiffReview.Stage(b, "A\nb\nc\nd\nE\n");
            InlineDiffReview.RejectHunkAtLine(b, 0);
            InlineDiffReview.AcceptAll(b);
            Assert.Equal("a\nb\nc\nd\nE\n", b.Content);
            Assert.True(b.IsDirty); // accepted text stays in the buffer (saved by Ctrl+S / auto save, or by the agent layer)
        }

        [Fact]
        public void EditsDuringReview_ShiftHunks_RejectStillRestoresTheRightLines()
        {
            var b = Buf("a\nb\nc\nd\ne\n");
            InlineDiffReview.Stage(b, "a\nb\nC1\nC2\nd\ne\n");
            var stale = Pending(b).Single();
            b.Lines.InsertRange(0, new[] { "t1", "t2", "t3" });  // typed above the hunk
            b.Lines[b.Lines.Count - 2] = "e-edited";           // and below it
            b.SyncContentFromLines();
            Assert.True(InlineDiffReview.RejectHunk(b, stale));
            Assert.Equal("t1\nt2\nt3\na\nb\nc\nd\ne-edited\n", b.Content);
            Assert.True(InlineDiffReview.HasPending(b)); // the user's own edits remain visible as changes vs the base
            InlineDiffReview.AcceptAll(b);
            Assert.Equal("t1\nt2\nt3\na\nb\nc\nd\ne-edited\n", b.Content);
        }

        [Fact]
        public void EditInsideHunk_ThenReject_RestoresOriginal()
        {
            var b = Buf("a\nold\nz\n");
            InlineDiffReview.Stage(b, "a\nnew\nz\n");
            b.Lines[1] = "new-typed";
            b.SyncContentFromLines();
            Assert.True(InlineDiffReview.RejectHunkAtLine(b, 1));
            Assert.Equal("a\nold\nz\n", b.Content);
        }

        [Fact]
        public void RejectHunk_IsUndoable()
        {
            var b = Buf("a\nb\nc\n");
            InlineDiffReview.Stage(b, "a\nB\nc\n");
            InlineDiffReview.RejectHunkAtLine(b, 1);
            Assert.Equal("a\nb\nc\n", b.Content);
            Assert.True(b.Undo());
            Assert.Equal("a\nB\nc\n", b.Content);
        }

        [Fact]
        public void CrLfContent_IsComparedWithLf_AndBytesSurviveSave()
        {
            string dir = Path.Combine(Path.GetTempPath(), "codeLess_idr_" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(dir);
            try
            {
                string path = Path.Combine(dir, "w.txt");
                File.WriteAllText(path, "one\r\ntwo\r\nthree\r\nfour");
                string eol; System.Text.Encoding enc; bool bom, finalNl;
                string text = EditorTabBuffer.ReadFilePreservingMetadata(path, out eol, out enc, out bom, out finalNl);
                var b = new EditorTabBuffer { FilePath = path, FileName = "w.txt", Content = text, SavedContent = text, EolSequence = eol, FileEncoding = enc, HasBom = bom, HasFinalNewline = finalNl };
                b.SyncLinesFromContent();
                InlineDiffReview.Stage(b, "one\r\nTWO\r\nfour\r\n"); // CRLF proposal with a final newline the file doesn't have
                var h = Pending(b).Single();
                Assert.Equal(new[] { "two", "three" }, h.RemovedLines);
                Assert.Equal(new[] { "TWO" }, h.AddedLines);
                InlineDiffReview.RejectHunk(b, h);
                Assert.False(b.IsDirty);
                b.WriteToDisk(path);
                Assert.Equal("one\r\ntwo\r\nthree\r\nfour", File.ReadAllText(path));
            }
            finally { try { Directory.Delete(dir, true); } catch { } }
        }

        [Fact]
        public void RepeatedLines_AllAcceptOrAllReject_AlwaysYieldProposalOrBase()
        {
            var rnd = new Random(1234);
            string[] alphabet = { "x", "y", "{", "}", "", "return;" };
            for (int iter = 0; iter < 300; iter++)
            {
                string baseText = RandomText(rnd, alphabet);
                string proposal = Mutate(rnd, baseText, alphabet);
                foreach (bool accept in new[] { true, false })
                {
                    var b = Buf(baseText);
                    InlineDiffReview.Stage(b, proposal);
                    string staged = b.Content;
                    int guard = 0;
                    while (InlineDiffReview.HasPending(b) && guard++ < 200)
                    {
                        var hs = Pending(b);
                        var h = hs[rnd.Next(hs.Count)];
                        Assert.True(accept ? InlineDiffReview.AcceptHunk(b, h) : InlineDiffReview.RejectHunk(b, h));
                    }
                    Assert.False(InlineDiffReview.HasPending(b));
                    Assert.Equal(accept ? staged : baseText, b.Content);
                }
            }
        }

        [Fact]
        public void UniqueLines_RandomDecisions_MatchTheIndependentExpectation()
        {
            var rnd = new Random(99);
            for (int iter = 0; iter < 300; iter++)
            {
                int n = rnd.Next(1, 25);
                var baseLines = Enumerable.Range(0, n).Select(i => "b" + i).ToList();
                var prop = new List<string>();
                int uid = 0;
                foreach (var l in baseLines)
                {
                    int r = rnd.Next(6);
                    if (r == 0) continue;                                   // delete
                    if (r == 1) { prop.Add("n" + iter + "_" + uid++); continue; } // replace
                    if (r == 2) prop.Add("n" + iter + "_" + uid++);          // insert before
                    prop.Add(l);
                }
                if (rnd.Next(3) == 0) prop.Add("tail" + uid++);
                bool finalNl = rnd.Next(2) == 0;
                string baseText = string.Join("\n", baseLines) + (finalNl ? "\n" : "");
                string proposal = string.Join("\n", prop) + (finalNl ? "\n" : "");
                var b = Buf(baseText);
                InlineDiffReview.Stage(b, proposal);
                var original = Pending(b);
                var decisions = original.Select(_ => rnd.Next(2) == 0).ToList();
                string expected = Expected(baseText, original, i => decisions[i]);
                foreach (int i in Enumerable.Range(0, original.Count).OrderBy(_ => rnd.Next()))
                    Assert.True(decisions[i] ? InlineDiffReview.AcceptHunk(b, original[i]) : InlineDiffReview.RejectHunk(b, original[i]));
                Assert.Equal(expected, b.Content);
            }
        }

        [Fact]
        public void IdenticalProposal_StagesNothing()
        {
            var b = Buf("same\n");
            Assert.Equal(0, InlineDiffReview.Stage(b, "same\n"));
            Assert.False(InlineDiffReview.HasPending(b));
            Assert.Null(b.PreDiffContent);
            Assert.False(b.IsDirty);
        }

        [Fact]
        public void Restage_KeepsTheOriginalBase()
        {
            var b = Buf("a\nb\n");
            InlineDiffReview.Stage(b, "a\nB1\n");
            InlineDiffReview.Stage(b, "a\nB2\n");     // e.g. an auto-repair turn replaced the proposal
            InlineDiffReview.RejectAll(b);
            Assert.Equal("a\nb\n", b.Content);
        }

        // ---------------------------------------------------------------- helpers

        /// <summary>Applies the chosen hunks of the original diff to the base, independently of the engine.</summary>
        private static string Expected(string baseText, List<EditorDiffHunk> hunks, Func<int, bool> accepted)
        {
            var baseLines = baseText.Split('\n').ToList();
            var result = new List<string>();
            int pos = 0;
            for (int i = 0; i < hunks.Count; i++)
            {
                int oldStart = InlineDiffReview.BaseStartLine(hunks, hunks[i]);
                while (pos < oldStart) result.Add(baseLines[pos++]);
                result.AddRange(accepted(i) ? hunks[i].AddedLines : hunks[i].RemovedLines);
                pos += hunks[i].RemovedLines.Count;
            }
            while (pos < baseLines.Count) result.Add(baseLines[pos++]);
            return string.Join("\n", result);
        }

        private static string RandomText(Random rnd, string[] alphabet)
        {
            int n = rnd.Next(0, 14);
            return string.Join("\n", Enumerable.Range(0, n).Select(_ => alphabet[rnd.Next(alphabet.Length)])) + (rnd.Next(2) == 0 ? "\n" : "");
        }

        private static string Mutate(Random rnd, string text, string[] alphabet)
        {
            var lines = text.Split('\n').ToList();
            int edits = rnd.Next(1, 5);
            for (int e = 0; e < edits; e++)
            {
                int at = rnd.Next(lines.Count + 1);
                switch (rnd.Next(3))
                {
                    case 0: lines.Insert(at, alphabet[rnd.Next(alphabet.Length)]); break;
                    case 1: if (at < lines.Count) lines.RemoveAt(at); break;
                    default: if (at < lines.Count) lines[at] = alphabet[rnd.Next(alphabet.Length)] + "!"; break;
                }
            }
            return string.Join("\n", lines);
        }
    }

    /// <summary>QA #8: files.autoSave semantics shared by both UIs.</summary>
    public class AutoSavePolicyCoreTests
    {
        [Fact]
        public void AfterDelay_EachEditRestartsTheDelay()
        {
            var d = new AutoSaveDebouncer();
            var buf = new object();
            d.NoteEdit(buf, 0);
            Assert.False(d.IsDue(buf, 999, 1000));
            d.NoteEdit(buf, 800);                    // typing continues
            Assert.False(d.IsDue(buf, 1000, 1000));  // a repeating timer would have saved here
            Assert.Equal(600, d.RemainingMs(buf, 1200, 1000));
            Assert.True(d.IsDue(buf, 1800, 1000));
            Assert.True(d.IsDue(new object(), 0, 1000)); // never seen: due
            d.Forget(buf);
            Assert.True(d.IsDue(buf, 0, 1000));
        }

        [Fact]
        public void Modes_MapToVsCodeTriggers()
        {
            var s = new IdePersistentSettings();
            s.SetAutoSaveMode(IdePersistentSettings.AutoSaveOnFocusChange);
            Assert.True(AutoSavePolicy.SavesOnEditorFocusLoss(s));
            Assert.True(AutoSavePolicy.SavesOnWindowFocusLoss(s));
            Assert.False(AutoSavePolicy.SavesAfterDelay(s));
            s.SetAutoSaveMode(IdePersistentSettings.AutoSaveOnWindowChange);
            Assert.False(AutoSavePolicy.SavesOnEditorFocusLoss(s)); // only the window losing focus saves
            Assert.True(AutoSavePolicy.SavesOnWindowFocusLoss(s));
            s.SetAutoSaveMode(IdePersistentSettings.AutoSaveAfterDelay);
            Assert.True(AutoSavePolicy.SavesAfterDelay(s));
            Assert.False(AutoSavePolicy.SavesOnEditorFocusLoss(s));
            Assert.False(AutoSavePolicy.SavesOnWindowFocusLoss(s));
            s.SetAutoSaveMode(IdePersistentSettings.AutoSaveOff);
            Assert.False(AutoSavePolicy.SavesAfterDelay(s) || AutoSavePolicy.SavesOnEditorFocusLoss(s) || AutoSavePolicy.SavesOnWindowFocusLoss(s));
        }

        [Fact]
        public void BufferUnderReview_IsNeverEligible()
        {
            string dir = Path.Combine(Path.GetTempPath(), "codeLess_as_" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(dir);
            try
            {
                string path = Path.Combine(dir, "f.txt");
                File.WriteAllText(path, "a\n");
                var b = new EditorTabBuffer { FilePath = path, FileName = "f.txt", Content = "a\n", SavedContent = "a\n" };
                b.SyncLinesFromContent();
                b.Lines[0] = "typed";
                b.SyncContentFromLines();
                Assert.True(AutoSavePolicy.IsEligible(b));
                InlineDiffReview.Stage(b, "AI proposal\n");
                Assert.False(AutoSavePolicy.IsEligible(b));
                InlineDiffReview.AcceptAll(b);
                Assert.True(AutoSavePolicy.IsEligible(b));
                b.IsUntitled = true;
                Assert.False(AutoSavePolicy.IsEligible(b));
            }
            finally { try { Directory.Delete(dir, true); } catch { } }
        }
    }
}
