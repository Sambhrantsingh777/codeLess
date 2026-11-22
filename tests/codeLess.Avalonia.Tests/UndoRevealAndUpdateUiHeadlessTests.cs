using System;
using System.IO;
using System.Linq;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using codeLess.AvaloniaApp;
using codeLess.Studio;
using Xunit;

namespace codeLess.Avalonia.Tests
{
    /// <summary>
    /// v3.3.3 report: "When user presses Ctrl+Z OR Ctrl+Shift+Z, he should be navigated to exact text where the undo or
    /// redo was done" (VS Code: undo restores the selection before the edit, redo the one after, revealed centered when
    /// off-screen, no scroll when visible). Plus the update menu / status item / Restart to Update flow.
    /// </summary>
    [Collection("SequentialWorkbench")]
    public class UndoRevealAndUpdateUiHeadlessTests : IDisposable
    {
        private readonly string dir = Path.Combine(Path.GetTempPath(), "codeLess_undoReveal_" + Guid.NewGuid().ToString("N").Substring(0, 8));

        public UndoRevealAndUpdateUiHeadlessTests() { Directory.CreateDirectory(dir); }

        public void Dispose()
        {
            MainWindow.SetUpdateServiceForTests(null);
            try { Directory.Delete(dir, true); } catch { }
        }

        private (MainWindow, EditorTabBuffer) Open(string name, int lines)
        {
            string file = Path.Combine(dir, name);
            var sb = new StringBuilder();
            for (int i = 0; i < lines; i++) sb.Append("line ").Append(i).Append('\n');
            File.WriteAllText(file, sb.ToString());
            var w = new MainWindow(dir);
            w.Show();
            var tab = w.OpenFileInTab(file, preview: false);
            return (w, tab);
        }

        private static void Close(MainWindow w)
        {
            w.SuppressUnsavedPrompts = true;
            w.Close();
        }

        private static void Type(AvaloniaVirtualEditorCanvas canvas, string text)
        {
            foreach (char c in text)
                canvas.RaiseEvent(new TextInputEventArgs { RoutedEvent = InputElement.TextInputEvent, Text = c.ToString() });
        }

        private static void Layout(AvaloniaVirtualEditorCanvas canvas, double w = 1000, double h = 600)
        {
            canvas.Measure(new Size(w, h));
            canvas.Arrange(new Rect(0, 0, w, h));
            var target = new RenderTargetBitmap(new PixelSize(10, 10));
            using (var ctx = target.CreateDrawingContext())
                canvas.Render(ctx);
        }

        private static int VisibleRows(AvaloniaVirtualEditorCanvas canvas) => Math.Max(1, (int)(canvas.Bounds.Height / canvas.LineHeight) - 1);

        private static void AssertCentered(AvaloniaVirtualEditorCanvas canvas, EditorTabBuffer tab, int line)
        {
            int rows = VisibleRows(canvas);
            int offset = line - tab.ScrollTopLine;
            Assert.InRange(offset, 0, rows - 1);
            Assert.InRange(offset, rows / 2 - 2, rows / 2 + 2);
        }

        [AvaloniaFact]
        public void Undo_OfAnEditFarOffScreen_MovesCaretToIt_AndCentersIt_RedoToo()
        {
            var (w, tab) = Open("far.txt", 600);
            var canvas = w.PrimaryEditor.Canvas;
            Layout(canvas);
            tab.CursorLine = 400; tab.CursorCol = 0; tab.ScrollTopLine = 380;
            Type(canvas, "XYZ");
            Assert.Equal("XYZline 400", tab.Lines[400]);

            // the user goes back to the top of the file
            tab.CursorLine = 0; tab.CursorCol = 0; tab.ScrollTopLine = 0; tab.ScrollTopWrapRow = 0;
            w.SimulateShortcut(Key.Z, MacKeymap.PrimaryModifier);
            Assert.Equal("line 400", tab.Lines[400]);
            Assert.Equal(400, tab.CursorLine);
            Assert.Equal(0, tab.CursorCol);            // selection from before the edit
            Assert.False(tab.HasSelection());
            AssertCentered(canvas, tab, 400);

            // away again, then redo: lands after the inserted text
            tab.CursorLine = 5; tab.CursorCol = 0; tab.ScrollTopLine = 0; tab.ScrollTopWrapRow = 0;
            w.SimulateShortcut(Key.Z, MacKeymap.PrimaryModifier | KeyModifiers.Shift);
            Assert.Equal("XYZline 400", tab.Lines[400]);
            Assert.Equal(400, tab.CursorLine);
            Assert.Equal(3, tab.CursorCol);
            AssertCentered(canvas, tab, 400);
            Close(w);
        }

        [AvaloniaFact]
        public void Undo_WithSmoothScrolling_AnimatesToTheCenteredChange()
        {
            var (w, tab) = Open("smooth.txt", 600);
            var canvas = w.PrimaryEditor.Canvas;
            double clock = 0;
            bool savedSmooth = EditorScrollOptions.SmoothScrolling;
            EditorScrollOptions.SmoothScrolling = true;
            canvas.AnimationsOverride = true;
            canvas.AnimationClock = () => clock;
            try
            {
                Layout(canvas);
                tab.CursorLine = 450; tab.CursorCol = 0; tab.ScrollTopLine = 430;
                Type(canvas, "Q");
                canvas.AdvanceAnimationsForTest();
                clock += 1000; canvas.AdvanceAnimationsForTest();
                tab.CursorLine = 0; tab.CursorCol = 0; tab.ScrollTopLine = 0; tab.ScrollTopWrapRow = 0; tab.ScrollTopFraction = 0;
                canvas.PerformUndoRedo(redo: false);
                Assert.Equal(450, tab.CursorLine);
                Assert.True(canvas.IsScrollAnimating, "undo reveal is animated (the text did not change under the viewport)");
                int rows = VisibleRows(canvas);
                Assert.InRange(canvas.ScrollTargetRow, 450 - rows / 2 - 2, 450 - rows / 2 + 2);
                clock += 1000;
                canvas.AdvanceAnimationsForTest();
                Assert.False(canvas.IsScrollAnimating);
                AssertCentered(canvas, tab, 450);
            }
            finally
            {
                EditorScrollOptions.SmoothScrolling = savedSmooth;
                canvas.AnimationsOverride = false;
                Close(w);
            }
        }

        [AvaloniaFact]
        public void Undo_OfAVisibleEdit_DoesNotScroll_AndRedoRestoresTheAfterSelection()
        {
            var (w, tab) = Open("near.txt", 200);
            var canvas = w.PrimaryEditor.Canvas;
            Layout(canvas);
            tab.ScrollTopLine = 0;
            // replace a selected word: undo restores that selection, redo the caret after the new text
            tab.CursorLine = 10; tab.SelectionAnchorLine = 10; tab.SelectionAnchorCol = 0; tab.CursorCol = 4;
            Type(canvas, "row");
            Assert.Equal("row 10", tab.Lines[10]);
            tab.ClearSelection();
            tab.CursorLine = 12; tab.CursorCol = 1; // still visible
            canvas.PerformUndoRedo(redo: false);
            Assert.Equal("line 10", tab.Lines[10]);
            Assert.Equal(0, tab.ScrollTopLine);
            Assert.Equal(10, tab.CursorLine);
            Assert.Equal("line", tab.GetSelectedText());

            canvas.PerformUndoRedo(redo: true);
            Assert.Equal("row 10", tab.Lines[10]);
            Assert.Equal(0, tab.ScrollTopLine);
            Assert.Equal(10, tab.CursorLine);
            Assert.Equal(3, tab.CursorCol);
            Assert.False(tab.HasSelection());
            Close(w);
        }

        [AvaloniaFact]
        public void Undo_MultiCursorEdit_RestoresEveryCaret_AndRevealsThePrimary()
        {
            var (w, tab) = Open("multi.txt", 500);
            var canvas = w.PrimaryEditor.Canvas;
            Layout(canvas);
            tab.CursorLine = 300; tab.CursorCol = 0; tab.ScrollTopLine = 290;
            canvas.ExtraCursors.Add(new ExtraCursorCaret { Line = 305, Col = 0, AnchorLine = -1, AnchorCol = -1 });
            Type(canvas, "Q");
            Assert.Equal("Qline 300", tab.Lines[300]);
            Assert.Equal("Qline 305", tab.Lines[305]);

            tab.ScrollTopLine = 0; tab.ScrollTopWrapRow = 0; // scrolled away, carets untouched
            canvas.PerformUndoRedo(redo: false);
            Assert.Equal("line 300", tab.Lines[300]);
            Assert.Equal("line 305", tab.Lines[305]);
            Assert.Equal(300, tab.CursorLine);
            Assert.Equal(0, tab.CursorCol);
            Assert.Single(canvas.ExtraCursors);
            Assert.Equal(305, canvas.ExtraCursors[0].Line);
            AssertCentered(canvas, tab, 300);

            tab.ScrollTopLine = 0; tab.ScrollTopWrapRow = 0;
            canvas.PerformUndoRedo(redo: true);
            Assert.Equal("Qline 300", tab.Lines[300]);
            Assert.Equal(300, tab.CursorLine);
            Assert.Equal(1, tab.CursorCol);
            Assert.Single(canvas.ExtraCursors);
            Assert.Equal(1, canvas.ExtraCursors[0].Col);
            AssertCentered(canvas, tab, 300);
            canvas.ResetExtraCursors();
            Close(w);
        }

        [AvaloniaFact]
        public void Undo_OfAnEditHiddenInAFold_ExpandsTheFold()
        {
            var (w, tab) = Open("fold.txt", 300);
            var canvas = w.PrimaryEditor.Canvas;
            Layout(canvas);
            tab.CursorLine = 150; tab.CursorCol = 0; tab.ScrollTopLine = 140;
            Type(canvas, "Z");
            tab.CursorLine = 0; tab.CursorCol = 0; tab.ScrollTopLine = 0;
            tab.FoldedBlockRanges[100] = 200; // collapsed around the edit
            canvas.PerformUndoRedo(redo: false);
            Assert.Equal(150, tab.CursorLine);
            Assert.False(tab.FoldedBlockRanges.ContainsKey(100));
            Close(w);
        }

        // ------------------------------------------------------------------ updates in the workbench

        [AvaloniaFact]
        public void UpdateMenu_HelpHasCheckForUpdates_AndLabelsFollowTheState_ThenRestartGoesThroughUnsavedPrompt()
        {
            var svc = new UpdateService("3.3.3", new UpdateEnvironment { Os = UpdateOs.Mac, Kind = UpdateInstallKind.MacAppBundle }, Path.Combine(dir, "upd"));
            MainWindow.SetUpdateServiceForTests(svc);
            var (w, tab) = Open("u.txt", 5);

            var native = NativeMenu.GetMenu(w);
            NativeMenuItem HelpUpdateItem() => native.Items.OfType<NativeMenuItem>().First(i => i.Header == "Help").Menu
                .Items.OfType<NativeMenuItem>().FirstOrDefault(i => i.Header != null && (i.Header.Contains("Update")));
            Assert.Equal("Check for Updates...", HelpUpdateItem()?.Header);
            Assert.False(w.IsUpdateStatusItemVisible);

            // the macOS app menu (built once in App.Initialize) has About / Check for Updates / Settings
            var appMenu = AppNativeMenuForTest();
            var appHeaders = appMenu.Items.OfType<NativeMenuItem>().Select(i => i.Header).ToList();
            Assert.Contains("About codeLess", appHeaders);
            Assert.Contains("Check for Updates...", appHeaders);
            Assert.Contains("Settings", appHeaders);
            Assert.Contains("Quit codeLess", appHeaders);

            var release = new UpdateCheckResult { Success = true, IsNewer = true, LatestVersion = "3.3.4", CurrentVersion = "3.3.3" };
            svc.SetStateForTests(UpdateState.Downloading, release, null);
            Dispatcher.UIThread.RunJobs();
            Assert.Equal("Downloading Update...", HelpUpdateItem()?.Header);
            Assert.False(HelpUpdateItem().IsEnabled);
            Assert.True(w.IsUpdateStatusItemVisible);

            var plan = new UpdateInstallPlan { CanInstall = true, ScriptPath = Path.Combine(dir, "nope.sh") };
            svc.SetStateForTests(UpdateState.Ready, release, plan);
            Dispatcher.UIThread.RunJobs();
            Assert.Equal("Restart to Update", HelpUpdateItem()?.Header);
            Assert.True(HelpUpdateItem().IsEnabled);
            Assert.Equal("Restart to Update", AppNativeMenuForTest().Items.OfType<NativeMenuItem>().First(i => i.Header.Contains("Update")).Header);
            Assert.True(w.IsUpdateStatusItemVisible);
            Assert.Contains("3.3.4", w.UpdateNotificationText);

            // unsaved files go through the normal prompt first: Cancel keeps codeLess running
            int restarts = 0;
            w.RestartForUpdateOverride = () => restarts++;
            tab.Lines[0] = "changed"; tab.IsDirty = true;
            w.UnsavedPromptOverride = d => UnsavedPromptResult.Cancel;
            w.RestartToUpdate();
            Assert.Equal(0, restarts);
            w.UnsavedPromptOverride = d => UnsavedPromptResult.DontSave;
            w.InvokeMenuCommand("app.checkUpdates"); // the menu item now restarts
            Assert.Equal(1, restarts);
            Close(w);
        }

        private static NativeMenu AppNativeMenuForTest()
        {
            if (AppNativeMenu.Items.Count == 0) AppNativeMenu.Build(() => null, null);
            var item = AppNativeMenu.Items["app.about"];
            return (NativeMenu)item.Parent;
        }
    }
}
