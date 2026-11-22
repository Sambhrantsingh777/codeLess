using System;
using System.IO;
using System.Linq;
using Avalonia.Headless.XUnit;
using codeLess.AvaloniaApp;
using codeLess.Studio;
using Xunit;

namespace codeLess.Avalonia.Tests
{
    /// <summary>QA #23: per-hunk Accept / Reject of an inline proposal in the Avalonia editor must never lose text.</summary>
    [Collection("SequentialWorkbench")]
    public class InlineDiffReviewHeadlessTests : IDisposable
    {
        private readonly string settingsFilePath = IdePersistentSettings.GetSettingsFilePath();
        private readonly string settingsBackup;
        private readonly string ws;

        public InlineDiffReviewHeadlessTests()
        {
            settingsBackup = File.Exists(settingsFilePath) ? File.ReadAllText(settingsFilePath) : null;
            ws = Path.Combine(Path.GetTempPath(), "codeLess_InlineDiff_" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(ws);
        }

        public void Dispose()
        {
            try
            {
                if (settingsBackup != null) File.WriteAllText(settingsFilePath, settingsBackup);
                else if (File.Exists(settingsFilePath)) File.Delete(settingsFilePath);
            }
            catch { }
            try { Directory.Delete(ws, true); } catch { }
        }

        private (MainWindow, EditorTabBuffer) Open(string content, string name = "a.txt")
        {
            string path = Path.Combine(ws, name);
            File.WriteAllText(path, content);
            var w = new MainWindow(ws) { SuppressUnsavedPrompts = true };
            w.Show();
            var tab = w.OpenFileInTab(path, preview: false);
            return (w, tab);
        }

        private static int FirstProposedLine(EditorTabBuffer tab) => tab.DiffHunks.Where(h => h.IsPending).OrderBy(h => h.StartLine).First().StartLine;

        [AvaloniaFact]
        public void RejectHunk_RestoresEveryRemovedLine_OfAMultiLineReplacement()
        {
            var (w, tab) = Open("keep1\nold1\nold2\nold3\nkeep2\n");
            try
            {
                w.PrimaryEditor.StageProposedDiffContent("keep1\nnew1\nkeep2\n");
                Assert.True(tab.HasPendingDiff);
                w.PrimaryEditor.RejectDiffHunkAtLine(FirstProposedLine(tab));
                Assert.Equal("keep1\nold1\nold2\nold3\nkeep2\n", tab.Content);
                Assert.False(tab.HasPendingDiff);
                Assert.False(tab.IsDirty);
            }
            finally { w.Close(); }
        }

        [AvaloniaFact]
        public void RejectHunk_PureDeletion_RestoresTheDeletedLines()
        {
            var (w, tab) = Open("a\nb\nc\nd\ne\n");
            try
            {
                w.PrimaryEditor.StageProposedDiffContent("a\ne\n");
                Assert.True(tab.HasPendingDiff);
                w.PrimaryEditor.RejectDiffHunkAtLine(FirstProposedLine(tab));
                Assert.Equal("a\nb\nc\nd\ne\n", tab.Content);
                Assert.False(tab.HasPendingDiff);
            }
            finally { w.Close(); }
        }

        [AvaloniaFact]
        public void AdjacentHunks_RejectOne_AcceptOther_KeepsBothDecisions()
        {
            var (w, tab) = Open("l1\nl2\nl3\nl4\nl5\nl6\nl7\n");
            try
            {
                // Two hunks separated by a single unchanged line (l4).
                w.PrimaryEditor.StageProposedDiffContent("l1\nX2\nX3\nl4\nY5\nY5b\nl6\nl7\n");
                var hunks = tab.DiffHunks.Where(h => h.IsPending).OrderBy(h => h.StartLine).ToList();
                Assert.Equal(2, hunks.Count);
                w.PrimaryEditor.AcceptDiffHunkAtLine(hunks[1].StartLine);   // keep the second
                Assert.True(tab.HasPendingDiff);
                w.PrimaryEditor.RejectDiffHunkAtLine(FirstProposedLine(tab)); // reject the first
                Assert.Equal("l1\nl2\nl3\nl4\nY5\nY5b\nl6\nl7\n", tab.Content);
                Assert.False(tab.HasPendingDiff);
            }
            finally { w.Close(); }
        }

        [AvaloniaFact]
        public void RejectAll_AfterPartialAccept_KeepsTheAcceptedHunk()
        {
            var (w, tab) = Open("a\nb\nc\nd\ne\nf\ng\n");
            try
            {
                w.PrimaryEditor.StageProposedDiffContent("A\nb\nc\nd\ne\nf\nG\n");
                var first = FirstProposedLine(tab);
                w.PrimaryEditor.AcceptDiffHunkAtLine(first);
                w.PrimaryEditor.RejectAllPendingDiffs();
                Assert.Equal("A\nb\nc\nd\ne\nf\ng\n", tab.Content);
                Assert.False(tab.HasPendingDiff);
            }
            finally { w.Close(); }
        }

        [AvaloniaFact]
        public void RejectHunk_AfterTypingAboveIt_StillRestoresTheRightLines()
        {
            var (w, tab) = Open("a\nb\nc\nd\n");
            try
            {
                w.PrimaryEditor.StageProposedDiffContent("a\nb\nC\nd\n");
                // The user inserts two lines at the top while the proposal is pending.
                tab.Lines.InsertRange(0, new[] { "typed1", "typed2" });
                tab.SyncContentFromLines();
                w.PrimaryEditor.NotifyExternalBufferEdit();
                var hunk = tab.DiffHunks.Where(h => h.IsPending).OrderBy(h => h.StartLine).Last();
                w.PrimaryEditor.RejectDiffHunkAtLine(hunk.StartLine);
                Assert.Equal("typed1\ntyped2\na\nb\nc\nd\n", tab.Content);
            }
            finally { w.Close(); }
        }

        [AvaloniaFact]
        public void CrLfFile_WithoutFinalNewline_RejectHunk_RestoresBytesOnSave()
        {
            var (w, tab) = Open("one\r\ntwo\r\nthree\r\nfour");
            try
            {
                w.PrimaryEditor.StageProposedDiffContent("one\nTWO\nfour");
                w.PrimaryEditor.RejectDiffHunkAtLine(FirstProposedLine(tab));
                Assert.Equal("one\ntwo\nthree\nfour", tab.Content);
                Assert.False(tab.IsDirty);
                Assert.True(w.SaveActiveTab());
                Assert.Equal("one\r\ntwo\r\nthree\r\nfour", File.ReadAllText(Path.Combine(ws, "a.txt")));
            }
            finally { w.Close(); }
        }

        private static T FindByName<T>(global::Avalonia.Controls.Control root, string name) where T : global::Avalonia.Controls.Control
        {
            foreach (var c in global::Avalonia.VisualTree.VisualExtensions.GetVisualDescendants(root))
                if (c is T t && t.Name == name) return t;
            return null;
        }

        [AvaloniaFact]
        public void AutoSaveOnFocusChange_FocusMovingFromEditorToChat_SavesTheFile()
        {
            var (w, tab) = Open("v1\n");
            try
            {
                w.CurrentSettings.SetAutoSaveMode(IdePersistentSettings.AutoSaveOnFocusChange);
                w.PrimaryEditor.Canvas.Focus();
                global::Avalonia.Threading.Dispatcher.UIThread.RunJobs();
                tab.Lines[0] = "v2";
                w.PrimaryEditor.Canvas.CommitExternalEdit();
                Assert.True(tab.IsDirty);
                var chat = FindByName<global::Avalonia.Controls.TextBox>(w, "ChatPromptInput");
                Assert.NotNull(chat);
                chat.Focus();
                global::Avalonia.Threading.Dispatcher.UIThread.RunJobs();
                Assert.Equal("v2\n", File.ReadAllText(Path.Combine(ws, "a.txt")));
                Assert.False(tab.IsDirty);

                // Widgets inside the editor (find bar) keep the editor focused: no save.
                w.PrimaryEditor.Canvas.Focus();
                tab.Lines[0] = "v3";
                w.PrimaryEditor.Canvas.CommitExternalEdit();
                w.PrimaryEditor.ShowFindBar(false);
                global::Avalonia.Threading.Dispatcher.UIThread.RunJobs();
                Assert.Equal("v2\n", File.ReadAllText(Path.Combine(ws, "a.txt")));

                // onWindowChange: leaving the editor for another part of the workbench doesn't save.
                w.CurrentSettings.SetAutoSaveMode(IdePersistentSettings.AutoSaveOnWindowChange);
                w.PrimaryEditor.Canvas.Focus();
                chat.Focus();
                global::Avalonia.Threading.Dispatcher.UIThread.RunJobs();
                Assert.Equal("v2\n", File.ReadAllText(Path.Combine(ws, "a.txt")));
            }
            finally { w.CurrentSettings.SetAutoSaveMode(IdePersistentSettings.AutoSaveOff); w.Close(); }
        }

        [AvaloniaFact]
        public void AutoSave_NeverWritesAProposalStillUnderReview_ButSavesOnceAccepted()
        {
            var (w, tab) = Open("orig\n");
            try
            {
                w.CurrentSettings.SetAutoSaveMode(IdePersistentSettings.AutoSaveAfterDelay);
                w.PrimaryEditor.StageProposedDiffContent("AI\n");
                w.FlushPendingAutoSave();
                w.CurrentSettings.SetAutoSaveMode(IdePersistentSettings.AutoSaveOnFocusChange);
                w.PrimaryEditor.Canvas.Focus();
                FindByName<global::Avalonia.Controls.TextBox>(w, "ChatPromptInput").Focus();
                global::Avalonia.Threading.Dispatcher.UIThread.RunJobs();
                Assert.Equal("orig\n", File.ReadAllText(Path.Combine(ws, "a.txt")));

                // Accepting a non-agent inline proposal keeps it in the buffer (dirty); auto save then writes it.
                w.CurrentSettings.SetAutoSaveMode(IdePersistentSettings.AutoSaveAfterDelay);
                w.PrimaryEditor.AcceptAllPendingDiffs();
                Assert.True(tab.IsDirty);
                w.FlushPendingAutoSave();
                Assert.Equal("AI\n", File.ReadAllText(Path.Combine(ws, "a.txt")));
            }
            finally { w.CurrentSettings.SetAutoSaveMode(IdePersistentSettings.AutoSaveOff); w.Close(); }
        }

        [AvaloniaFact]
        public void ProposalDroppingTheFinalNewline_DoesNotCreateASpuriousHunk()
        {
            var (w, tab) = Open("x\ny\n");
            try
            {
                w.PrimaryEditor.StageProposedDiffContent("x\nY");
                var hunks = tab.DiffHunks.Where(h => h.IsPending).ToList();
                Assert.Single(hunks);
                Assert.Equal(new[] { "y" }, hunks[0].RemovedLines);
                Assert.Equal(new[] { "Y" }, hunks[0].AddedLines);
                w.PrimaryEditor.AcceptAllPendingDiffs();
                Assert.True(w.SaveActiveTab());
                Assert.Equal("x\nY\n", File.ReadAllText(Path.Combine(ws, "a.txt")));
            }
            finally { w.Close(); }
        }
    }
}
