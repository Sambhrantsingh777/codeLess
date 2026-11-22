using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using Avalonia;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Media.Imaging;
using codeLess.AvaloniaApp;
using codeLess.Studio;
using Xunit;
using Xunit.Abstractions;

namespace codeLess.Avalonia.Tests
{
    /// <summary>
    /// Smooth scrolling and caret in the Avalonia editor (user report: scrolling and the caret felt rough next to VS
    /// Code): pixel-precise trackpad offsets, animated wheel notches that retarget, reveals that animate only when smooth
    /// scrolling is on, the caret transition snapping on typing, the caret layer, settings, and the frame cost of
    /// scrolling a 50k-line file (shaped rows are cached). Animations run on a fake clock.
    /// </summary>
    [Collection("SequentialWorkbench")]
    public class EditorSmoothScrollingHeadlessTests : IDisposable
    {
        private readonly ITestOutputHelper output;
        private readonly string dir;
        private readonly string settingsPath;
        private readonly string settingsBackup;
        private readonly bool hadSettings;
        private double clock = 1000;

        public EditorSmoothScrollingHeadlessTests(ITestOutputHelper output)
        {
            this.output = output;
            settingsPath = IdePersistentSettings.GetSettingsFilePath();
            hadSettings = File.Exists(settingsPath);
            settingsBackup = hadSettings ? File.ReadAllText(settingsPath) : null;
            dir = Path.Combine(Path.GetTempPath(), "codeLess_Smooth_" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(dir);
        }

        public void Dispose()
        {
            try
            {
                if (hadSettings) File.WriteAllText(settingsPath, settingsBackup);
                else if (File.Exists(settingsPath)) File.Delete(settingsPath);
            }
            catch { }
            try { Directory.Delete(dir, true); } catch { }
        }

        private static void Layout(AvaloniaVirtualEditorCanvas canvas)
        {
            canvas.Measure(new Size(1200, 800));
            canvas.Arrange(new Rect(0, 0, 1200, 800));
            canvas.CaretOverlay.Measure(new Size(1200, 800));
            canvas.CaretOverlay.Arrange(new Rect(0, 0, 1200, 800));
        }

        private static void RenderOnce(AvaloniaVirtualEditorCanvas canvas)
        {
            var target = new RenderTargetBitmap(new PixelSize(10, 10));
            using (var ctx = target.CreateDrawingContext())
                canvas.Render(ctx);
        }

        private static void RenderCaret(AvaloniaVirtualEditorCanvas canvas)
        {
            var target = new RenderTargetBitmap(new PixelSize(10, 10));
            using (var ctx = target.CreateDrawingContext())
                canvas.CaretOverlay.Render(ctx);
        }

        private (MainWindow w, EditorTabBuffer tab, AvaloniaVirtualEditorCanvas canvas) Open(int lines, bool animations = true, string longLine = null)
        {
            var sb = new StringBuilder();
            for (int i = 0; i < lines; i++)
            {
                if (i == 3 && longLine != null) { sb.Append(longLine).Append('\n'); continue; }
                sb.Append("    int value").Append(i).Append(" = Compute(").Append(i).Append("); // filler\n");
            }
            string file = Path.Combine(dir, "Big.cs");
            File.WriteAllText(file, sb.ToString());
            TestTrust.Trust(dir);
            var w = new MainWindow(dir);
            w.Show();
            var tab = w.OpenFileInTab(file, preview: false);
            var canvas = w.PrimaryEditor.Canvas;
            canvas.AnimationsOverride = animations;
            canvas.AnimationClock = () => clock;
            Layout(canvas);
            RenderOnce(canvas);
            return (w, tab, canvas);
        }

        private static void Close(MainWindow w)
        {
            w.SuppressUnsavedPrompts = true;
            w.Close();
        }

        [AvaloniaFact]
        public void PreciseTrackpadDeltas_ScrollByFractionalPixels_AndHitTestFollows()
        {
            var (w, tab, canvas) = Open(400);
            try
            {
                double lh = canvas.LineHeight;
                // a trackpad gesture: events every 8 ms (the first, isolated one is animated and composes with the rest)
                double[] deltas = { -0.06, -0.1, -0.14, -0.08, -0.04, -0.02 };
                double expectedPx = 0;
                foreach (double d in deltas)
                {
                    canvas.SimulateWheelForTest(0, d);
                    expectedPx += Math.Abs(d) * (deltas[0] == d ? 3 * lh : 50) * EditorScrollOptions.MouseWheelScrollSensitivity;
                    clock += 8;
                    canvas.AdvanceAnimationsForTest();
                }
                clock += 300;
                canvas.AdvanceAnimationsForTest();
                Assert.False(canvas.IsScrollAnimating);
                double top = canvas.TopRowExactForTest;
                Assert.Equal(expectedPx / lh, top, 3);
                Assert.True(top % 1 > 0.01, "the position is fractional, not snapped to a line: " + top);
                Assert.True(tab.ScrollTopFraction > 0);

                // the painted rows start above the viewport by the fraction; a click maps through the same offset
                RenderOnce(canvas);
                int whole = (int)Math.Floor(top);
                double frac = top - whole;
                double yInSecondRow = (1 - frac) * lh + lh / 2; // middle of the second painted row
                canvas.ClickAtForTest(new Point(200, yInSecondRow));
                Assert.Equal(whole + 1, tab.CursorLine);
            }
            finally { Close(w); }
        }

        [AvaloniaFact]
        public void PreciseHorizontalDeltas_KeepExactPixels()
        {
            var (w, tab, canvas) = Open(50, longLine: new string('x', 600)); // wider than the viewport
            try
            {
                Assert.True(canvas.MaxScrollLeftPxForTest() > 100);
                double[] deltas = { -0.07, -0.05, -0.03 };
                foreach (double d in deltas) { canvas.SimulateWheelForTest(d, 0); clock += 8; canvas.AdvanceAnimationsForTest(); }
                clock += 300;
                canvas.AdvanceAnimationsForTest();
                double expected = (0.07 * 3 * canvas.LineHeight + 0.05 * 50 + 0.03 * 50) * EditorScrollOptions.MouseWheelScrollSensitivity;
                Assert.Equal(expected, tab.ScrollLeftPx, 3); // not rounded to 7.8 px columns any more
            }
            finally { Close(w); }
        }

        [AvaloniaFact]
        public void WheelNotch_AnimatesToTarget_AndRetargetsMidFlight()
        {
            var (w, tab, canvas) = Open(400);
            try
            {
                Assert.Equal(0, canvas.TopRowExactForTest, 6);
                canvas.SimulateWheelForTest(0, -1); // one notch: 3 lines
                Assert.True(canvas.IsScrollAnimating);
                Assert.Equal(3, canvas.ScrollTargetRow, 6);
                clock += 50;
                canvas.AdvanceAnimationsForTest();
                double mid = canvas.TopRowExactForTest;
                Assert.InRange(mid, 0.01, 2.99);
                canvas.SimulateWheelForTest(0, -1); // the next notch arrives mid-animation
                Assert.Equal(6, canvas.ScrollTargetRow, 6);
                Assert.Equal(mid, canvas.TopRowExactForTest, 6); // retargets from where it is: no jump
                clock += 60;
                canvas.AdvanceAnimationsForTest();
                Assert.InRange(canvas.TopRowExactForTest, mid, 6);
                clock += 200;
                canvas.AdvanceAnimationsForTest();
                Assert.False(canvas.IsScrollAnimating);
                Assert.Equal(6, canvas.TopRowExactForTest, 6);
                Assert.Equal(6, tab.ScrollTopLine);

                // something else moving the view (a direct jump) cancels a running animation
                canvas.SimulateWheelForTest(0, -1);
                tab.ScrollTopLine = 100;
                clock += 30;
                canvas.AdvanceAnimationsForTest();
                Assert.False(canvas.IsScrollAnimating);
                Assert.Equal(100, tab.ScrollTopLine);
            }
            finally { Close(w); }
        }

        [AvaloniaFact]
        public void Reveal_AnimatesWhenSmoothScrollingIsOn_AndJumpsWhenOff()
        {
            var (w, tab, canvas) = Open(3000);
            try
            {
                canvas.EnsureCursorVisible(); // first reveal in a buffer snaps (it is not navigation)
                w.PrimaryEditor.GoToLine(200);
                Assert.True(canvas.IsScrollAnimating, "go to line glides there");
                double target = canvas.ScrollTargetRow;
                Assert.True(target > 150 && target <= 200);
                Assert.True(canvas.TopRowExactForTest < 1, "nothing jumped yet");
                clock += 40;
                canvas.AdvanceAnimationsForTest();
                double mid = canvas.TopRowExactForTest;
                Assert.InRange(mid, 1, target - 0.01);
                clock += 200;
                canvas.AdvanceAnimationsForTest();
                Assert.Equal(target, canvas.TopRowExactForTest, 6);

                // the stable reveal API: animated by default, immediate with animate: false
                canvas.RevealPosition(2500, 0);
                Assert.True(canvas.IsScrollAnimating);
                clock += 300;
                canvas.AdvanceAnimationsForTest();
                Assert.True(tab.ScrollTopLine <= 2500 && tab.ScrollTopLine > 2400);
                canvas.RevealRange(10, 0, 12, 4, animate: false);
                Assert.False(canvas.IsScrollAnimating);
                Assert.True(tab.ScrollTopLine <= 10);

                // smooth scrolling off (per editor here; the setting feeds the same switch): reveals jump
                canvas.AnimationsOverride = false;
                w.PrimaryEditor.GoToLine(1500);
                Assert.False(canvas.IsScrollAnimating);
                Assert.True(tab.ScrollTopLine <= 1499 && tab.ScrollTopLine > 1400, "jumped: " + tab.ScrollTopLine);
                canvas.SimulateWheelForTest(0, -1);
                Assert.False(canvas.IsScrollAnimating); // notches jump too
            }
            finally { Close(w); }
        }

        [AvaloniaFact]
        public void TypingRevealsSnap_NavigationAnimates()
        {
            var (w, tab, canvas) = Open(400);
            try
            {
                canvas.Focus();
                canvas.EnsureCursorVisible();
                int rows = (int)(800 / canvas.LineHeight);
                tab.CursorLine = rows - 2; tab.CursorCol = 0;
                canvas.EnsureCursorVisible();
                // Down past the bottom edge: navigation, animated
                for (int i = 0; i < 3; i++) w.SimulateShortcut(Key.Down, KeyModifiers.None);
                Assert.True(canvas.IsScrollAnimating);
                clock += 300;
                canvas.AdvanceAnimationsForTest();
                // Enter at the bottom edge: an edit, the view follows immediately
                tab.CursorLine = tab.ScrollTopLine + rows - 2;
                canvas.EnsureCursorVisible();
                clock += 300;
                canvas.AdvanceAnimationsForTest();
                for (int i = 0; i < 4; i++)
                {
                    canvas.RaiseEvent(new TextInputEventArgs { RoutedEvent = InputElement.TextInputEvent, Text = "x" });
                    w.SimulateShortcut(Key.Enter, KeyModifiers.None);
                    Assert.False(canvas.IsScrollAnimating, "typing never waits for an animation");
                }
            }
            finally { Close(w); }
        }

        [AvaloniaFact]
        public void CaretLayer_GlidesOnNavigation_SnapsOnTyping_AndBlinksWithoutRepaintingTheCanvas()
        {
            var (w, tab, canvas) = Open(200);
            try
            {
                w.Activate();
                canvas.Focus();
                global::Avalonia.Threading.Dispatcher.UIThread.RunJobs();
                tab.CursorLine = 5; tab.CursorCol = 4;
                canvas.InvalidateVisual();
                RenderCaret(canvas);
                clock += 200; // let a glide from the previous position settle
                RenderCaret(canvas);
                var start = canvas.CaretOverlay.LastPrimaryRect;
                Assert.True(start.Width > 0, "caret painted by the layer");

                w.SimulateShortcut(Key.Down, KeyModifiers.None); // navigation
                RenderCaret(canvas);
                Assert.True(canvas.CaretOverlay.LastFrameAnimated, "smooth caret: glides after a navigation move");
                var glide = canvas.CaretOverlay.LastPrimaryRect;
                clock += 40;
                RenderCaret(canvas);
                var mid = canvas.CaretOverlay.LastPrimaryRect;
                Assert.True(mid.Y > start.Y && mid.Y < start.Y + canvas.LineHeight, "half way: start " + start + " glide " + glide + " mid " + mid + " lh " + canvas.LineHeight);
                clock += 100;
                RenderCaret(canvas);
                Assert.False(canvas.CaretOverlay.LastFrameAnimated);
                Assert.Equal(start.Y + canvas.LineHeight, canvas.CaretOverlay.LastPrimaryRect.Y, 3);

                // typing: the caret jumps to its new place at once
                canvas.RaiseEvent(new TextInputEventArgs { RoutedEvent = InputElement.TextInputEvent, Text = "abc" });
                RenderCaret(canvas);
                Assert.False(canvas.CaretOverlay.LastFrameAnimated, "typing never animates the caret");
                double charW = canvas.CharWidth;
                Assert.Equal(glide.X + 3 * charW, canvas.CaretOverlay.LastPrimaryRect.X, 1);

                // smooth blinking: solid right after input, fading later, solid again on input
                if (canvas.IsCaretBlinkTimerRunning)
                {
                    canvas.GetCaretAppearance(out double o0, out _);
                    Assert.Equal(1, o0, 3);
                    clock += 500 + 200;
                    canvas.GetCaretAppearance(out double o1, out _);
                    Assert.InRange(o1, 0.01, 0.99);
                    canvas.RaiseEvent(new TextInputEventArgs { RoutedEvent = InputElement.TextInputEvent, Text = "d" });
                    canvas.GetCaretAppearance(out double o2, out _);
                    Assert.Equal(1, o2, 3);
                }
                else output.WriteLine("blink timer not running in this headless session (focus gate); blink covered by the core tests");
            }
            finally { Close(w); }
        }

        [AvaloniaFact]
        public void SettingsPanel_AppliesSmoothnessSettings_AndTheyPersist()
        {
            var s = new IdePersistentSettings();
            var panel = EditorSmoothnessSettingsPanel.Build(s, global::Avalonia.Media.Colors.White);
            Assert.True(panel.Boxes[0].IsChecked == true);
            Assert.True(panel.Boxes[1].IsChecked == true);
            Assert.Equal(Array.IndexOf(CaretBlink.Styles, CaretBlink.Smooth), panel.BlinkCombo.SelectedIndex);
            panel.Boxes[0].IsChecked = false;
            panel.Boxes[1].IsChecked = false;
            panel.BlinkCombo.SelectedIndex = Array.IndexOf(CaretBlink.Styles, CaretBlink.Solid);
            panel.SensitivityBox.Text = "1,5";
            panel.FastSensitivityBox.Text = "nonsense";
            panel.Apply(s);
            Assert.False(s.EditorSmoothScrolling);
            Assert.False(s.EditorSmoothCaretAnimation);
            Assert.Equal(CaretBlink.Solid, s.EditorCursorBlinking);
            Assert.Equal(1.5, s.EditorMouseWheelScrollSensitivity, 6);
            Assert.Equal(5.0, s.EditorFastScrollSensitivity, 6);
            s.Save();
            var loaded = IdePersistentSettings.Load();
            Assert.False(loaded.EditorSmoothScrolling);
            Assert.False(loaded.EditorSmoothCaretAnimation);
            Assert.Equal(CaretBlink.Solid, loaded.EditorCursorBlinking);
            Assert.Equal(1.5, loaded.EditorMouseWheelScrollSensitivity, 6);
        }

        [AvaloniaFact]
        public void ScrollingA50kLineFile_ReusesShapedRows_AndStaysInsideTheFrameBudget()
        {
            var (w, tab, canvas) = Open(50000, animations: false);
            try
            {
                tab.ScrollTopLine = 20000;
                RenderOnce(canvas);
                canvas.CompleteBackgroundTokenization();
                RenderOnce(canvas);
                RenderOnce(canvas);
                long shaped0 = canvas.ShapedRowCount;
                RenderOnce(canvas);
                Assert.Equal(shaped0, canvas.ShapedRowCount); // a repaint without changes shapes nothing

                var times = new List<double>();
                var sw = new Stopwatch();
                long alloc0 = GC.GetAllocatedBytesForCurrentThread();
                int frames = 240;
                for (int i = 0; i < frames; i++)
                {
                    canvas.ScrollByPixelsForTest(5); // trackpad-like: 5 px per frame
                    sw.Restart();
                    RenderOnce(canvas);
                    sw.Stop();
                    times.Add(sw.Elapsed.TotalMilliseconds);
                }
                double allocKb = (GC.GetAllocatedBytesForCurrentThread() - alloc0) / 1024.0 / frames;
                long shaped = canvas.ShapedRowCount - shaped0;
                double rowsScrolled = frames * 5 / canvas.LineHeight;
                times.Sort();
                double median = times[times.Count / 2], p95 = times[(int)(times.Count * 0.95)];
                output.WriteLine(string.Format(System.Globalization.CultureInfo.InvariantCulture,
                    "scroll 50k lines (headless): median {0:F2} ms p95 {1:F2} ms, {2:F0} KB/frame, shaped {3} rows for {4:F0} rows scrolled",
                    median, p95, allocKb, shaped, rowsScrolled));
                Assert.True(shaped <= rowsScrolled + 4, "only rows that come into view are shaped: " + shaped);
                Assert.True(canvas.CachedRowTextCount < 600, "the shaped-row cache stays bounded: " + canvas.CachedRowTextCount);
                Assert.True(median < PerfBudget.Ms(8), "median frame " + median + " ms");
                Assert.True(tab.ScrollTopLine > 20000);
            }
            finally { Close(w); }
        }
    }
}
