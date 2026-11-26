using System;
using System.Diagnostics;
using System.Text;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using codeLess.AvaloniaApp;
using codeLess.Studio;
using Xunit;
using Xunit.Abstractions;

namespace codeLess.Avalonia.Tests
{
    /// <summary>
    /// The macOS / Linux terminal against VS Code's terminal (user report: "the terminal feel and animation looks
    /// unnatural"): PTY output coalesced to one repaint per frame, the cursor on its own layer (blinking never repaints
    /// the grid; steady block by default, outline when unfocused, DECSCUSR shapes), exact scroll positions from trackpad
    /// deltas, the view staying put while output arrives when scrolled back, selections following the text, smooth
    /// scrolling on a fake clock, synchronized output, line-height metrics and frame-cost budgets.
    /// </summary>
    [Collection("SequentialWorkbench")]
    public class TerminalFeelHeadlessTests
    {
        private readonly ITestOutputHelper output;
        public TerminalFeelHeadlessTests(ITestOutputHelper output) { this.output = output; }

        private static AvaloniaTerminalEmulatorControl Filled(int lines, double w = 800, double h = 300)
        {
            var term = new AvaloniaTerminalEmulatorControl();
            var sb = new StringBuilder();
            for (int i = 0; i < lines; i++) sb.Append("line ").Append(i).Append("\r\n");
            term.FallbackBuffer.FeedText(sb.ToString());
            term.RenderGridForTest(w, h);
            return term;
        }

        /// <summary>A full screen of short runs in random 256-colour foregrounds with some truecolor backgrounds.</summary>
        internal static string DenseColouredScreen(int cols, int rows, int seed)
        {
            var sb = new StringBuilder();
            var rnd = new Random(seed);
            for (int r = 0; r < rows; r++)
            {
                int c = 0;
                while (c < cols)
                {
                    int n = Math.Min(cols - c, 3 + rnd.Next(6));
                    sb.Append("\x1b[38;5;").Append(16 + rnd.Next(216)).Append('m');
                    if (rnd.Next(4) == 0) sb.Append("\x1b[48;2;").Append(rnd.Next(80)).Append(';').Append(rnd.Next(80)).Append(';').Append(rnd.Next(80)).Append('m');
                    for (int i = 0; i < n; i++) sb.Append((char)('!' + rnd.Next(90)));
                    sb.Append("\x1b[0m");
                    c += n;
                }
                if (r < rows - 1) sb.Append("\r\n");
            }
            return sb.ToString();
        }

        private static string TopRowText(AvaloniaTerminalEmulatorControl term)
        {
            term.SelectLineAtCell(0);
            string t = term.GetSelectedText();
            term.ClearSelection();
            return t;
        }

        [AvaloniaFact]
        public void Output_ManyChunks_RepaintOncePerFrame()
        {
            var term = Filled(10);
            long inv0 = term.GridInvalidateCountForTest;
            for (int i = 0; i < 500; i++)
            {
                term.FallbackBuffer.FeedText("chunk " + i + "\r\n");
                term.SimulatePtyOutputForTest();
            }
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(1, term.GridInvalidateCountForTest - inv0); // 500 chunks, one repaint
            Assert.Equal(1, term.OutputRepaintRequestsForTest);

            term.RenderGridForTest(800, 300); // the frame paints: the next output may ask again
            // from the PTY reader thread, as in the app
            Task.Run(() => { for (int i = 0; i < 1000; i++) term.SimulatePtyOutputForTest(); }).Wait();
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(2, term.GridInvalidateCountForTest - inv0);
            Assert.Equal(2, term.OutputRepaintRequestsForTest);

            // nothing is lost: output that arrives while a frame paints schedules the next one
            term.RenderGridForTest(800, 300);
            term.SimulatePtyOutputForTest();
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(3, term.GridInvalidateCountForTest - inv0);
        }

        private static void Settle(Window window = null)
        {
            Dispatcher.UIThread.RunJobs();
            window?.UpdateLayout();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Dispatcher.UIThread.RunJobs();
            window?.UpdateLayout();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Dispatcher.UIThread.RunJobs();
        }

        private static void WaitUntil(Window window, Func<bool> condition, int timeoutMs = 5000)
        {
            Settle(window);
            if (condition()) return;
            var sw = Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < timeoutMs && !condition())
            {
                System.Threading.Thread.Sleep(5);
                Settle(window);
            }
        }

        [AvaloniaFact]
        public void Cursor_BlinkRepaintsOnlyItsLayer_SteadyBlockByDefault_OutlineWhenUnfocused()
        {
            var term = new AvaloniaTerminalEmulatorControl();
            var other = new TextBox();
            var panel = new DockPanel();
            DockPanel.SetDock(other, Dock.Bottom);
            panel.Children.Add(other);
            panel.Children.Add(term);
            var window = new Window { Width = 800, Height = 400, Content = panel };
            window.Show();
            window.Activate();
            Settle(window);
            try
            {
                term.FallbackBuffer.FeedText("$ echo hi");
                term.SimulatePtyOutputForTest();
                WaitUntil(window, () => term.GridRenderCountForTest > 0);
                Assert.True(term.GridRenderCountForTest > 0);

                // unfocused: hollow outline at the cursor cell (terminal.integrated.cursorStyleInactive)
                string style = term.RenderCursorLayerForTest(out Rect rect);
                Assert.Equal("outline", style);
                Assert.Equal(AvaloniaTerminalGridCanvasPad.Left + 9 * term.CellWidthForTest, rect.X, 3);
                Assert.Equal(term.CellHeightForTest, rect.Height, 3);

                term.FocusTerminalCanvas();
                WaitUntil(window, () => term.CursorHasFocus);
                Assert.True(term.CursorHasFocus);
                Assert.Equal("block", term.RenderCursorLayerForTest(out rect)); // VS Code default: steady block
                Assert.Equal(term.CellWidthForTest, rect.Width, 3);
                Assert.False(term.IsCursorBlinkTimerRunning); // terminal.integrated.cursorBlinking = false

                // blink ticks repaint only the cursor layer: the compositor never re-renders the grid for them
                Settle(window);
                long grid0 = term.GridInvalidateCountForTest, render0 = term.GridRenderCountForTest, layer0 = term.CursorLayerRenderCountForTest;
                for (int i = 0; i < 6; i++)
                {
                    long before = term.CursorLayerRenderCountForTest;
                    term.TickCursorBlinkForTest();
                    WaitUntil(window, () => term.CursorLayerRenderCountForTest > before, 1000);
                }
                Dispatcher.UIThread.RunJobs();
                Assert.Equal(grid0, term.GridInvalidateCountForTest);
                Assert.Equal(render0, term.GridRenderCountForTest);
                Assert.True(term.CursorLayerRenderCountForTest >= layer0 + 3, "layer renders " + (term.CursorLayerRenderCountForTest - layer0));

                // the application asks for a blinking bar (vim insert mode, DECSCUSR 5)
                long renderBeforeBar = term.GridRenderCountForTest;
                term.FallbackBuffer.FeedText("\x1b[5 q");
                term.SimulatePtyOutputForTest();
                WaitUntil(window, () => term.GridRenderCountForTest > renderBeforeBar);
                Assert.Equal("line", term.RenderCursorLayerForTest(out rect));
                Assert.Equal(1, rect.Width, 3);
                Assert.True(term.IsCursorBlinkTimerRunning);
                term.FallbackBuffer.FeedText("\x1b[0 q"); // back to the user's settings
                term.SimulatePtyOutputForTest();
                Settle();
                Assert.False(term.IsCursorBlinkTimerRunning);

                // settings: underline + blinking
                var keepStyle = TerminalOptions.CursorStyle; var keepBlink = TerminalOptions.CursorBlinking;
                try
                {
                    TerminalOptions.CursorStyle = TerminalCursorStyle.Underline;
                    TerminalOptions.CursorBlinking = true;
                    term.ApplyTerminalOptions();
                    Settle();
                    Assert.Equal("underline", term.RenderCursorLayerForTest(out rect));
                    Assert.Equal(1, rect.Height, 3);
                    Assert.True(term.IsCursorBlinkTimerRunning);
                    other.Focus();
                    Settle();
                    Assert.False(term.IsCursorBlinkTimerRunning); // never blinks unfocused
                    Assert.Equal("outline", term.RenderCursorLayerForTest(out _));

                    // typing scrolls back to the live screen (VS Code scrollOnUserInput)
                    for (int i = 0; i < 200; i++) term.FallbackBuffer.FeedText("l " + i + "\r\n");
                    term.SimulatePtyOutputForTest();
                    Settle();
                    term.WheelForTest(1.0);
                    Assert.True(term.ScrollbackOffset > 0);
                    term.FocusTerminalCanvas();
                    window.KeyTextInput("a");
                    Settle();
                    Assert.Equal(0, term.ScrollbackOffset);
                }
                finally
                {
                    TerminalOptions.CursorStyle = keepStyle; TerminalOptions.CursorBlinking = keepBlink;
                    term.ApplyTerminalOptions();
                }
            }
            finally { window.Close(); }
        }

        [AvaloniaFact]
        public void Scrollback_PreciseDeltas_FractionalPosition_RowsSnap()
        {
            var term = Filled(400);
            for (int i = 0; i < 9; i++) term.WheelForTest(0.11); // a trackpad stream (back-to-back events)
            double exact = term.ExactScrollOffset;
            Assert.True(Math.Abs(exact - Math.Round(exact)) > 0.01, "fractional position expected, got " + exact);
            Assert.Equal((int)Math.Round(exact, MidpointRounding.AwayFromZero), term.ScrollbackOffset);
            // a flick is not capped at 10 rows per event any more
            term.WheelForTest(8.3);
            Assert.True(term.ScrollbackOffset > 20, "offset " + term.ScrollbackOffset); // old cap: 10 rows per event (rows per wheel unit differ per OS)
            term.RenderGridForTest(800, 300);
            Assert.Equal("line " + (term.FallbackBuffer.ScrollbackCount - term.ScrollbackOffset), TopRowText(term));
        }

        [AvaloniaFact]
        public void Scrollback_ScrolledUp_OutputDoesNotMoveTheView_AndSelectionFollowsText()
        {
            var term = Filled(300);
            for (int i = 0; i < 5; i++) term.WheelForTest(1.0); // 5 notches = 15 rows
            term.RenderGridForTest(800, 300);
            Assert.Equal(15, term.ScrollbackOffset);
            string top = TopRowText(term);
            term.SelectLineAtCell(2);
            string selected = term.GetSelectedText();

            var sb = new StringBuilder();
            for (int i = 0; i < 40; i++) sb.Append("new ").Append(i).Append("\r\n");
            term.FallbackBuffer.FeedText(sb.ToString());
            term.RenderGridForTest(800, 300);
            Assert.Equal(55, term.ScrollbackOffset); // anchored to the same rows
            Assert.Equal(selected, term.GetSelectedText());
            Assert.Equal(top, TopRowText(term));

            // at the bottom the view follows the output; a selection moves up with its text
            Assert.True(term.ClickScrollbackScrollbarAt(new Point(795, 298)));
            Assert.Equal(0, term.ScrollbackOffset);
            term.RenderGridForTest(800, 300);
            term.SelectLineAtCell(3);
            string sel2 = term.GetSelectedText();
            term.FallbackBuffer.FeedText("more 1\r\nmore 2\r\nmore 3\r\n");
            term.RenderGridForTest(800, 300);
            Assert.Equal(0, term.ScrollbackOffset);
            Assert.Equal(sel2, term.GetSelectedText());
        }

        [AvaloniaFact]
        public void Scrollback_SmoothScrolling_AnimatesNotchesOnTheFrameClock()
        {
            var keep = TerminalOptions.SmoothScrolling;
            try
            {
                TerminalOptions.SmoothScrolling = true;
                var term = Filled(300);
                double clock = 1000;
                term.SetAnimationClockForTest(() => clock);
                term.WheelForTest(1.0);
                Assert.True(term.IsScrollAnimatingForTest);
                Assert.Equal(0, term.ExactScrollOffset);
                clock += 50;
                term.AdvanceScrollAnimationForTest();
                Assert.True(term.ExactScrollOffset > 1 && term.ExactScrollOffset < 3, "mid " + term.ExactScrollOffset);
                clock += 200;
                term.AdvanceScrollAnimationForTest();
                Assert.False(term.IsScrollAnimatingForTest);
                Assert.Equal(3, term.ExactScrollOffset, 6);
                Assert.Equal(3, term.ScrollbackOffset);
            }
            finally { TerminalOptions.SmoothScrolling = keep; }
        }

        [AvaloniaFact]
        public void SynchronizedOutput_HoldsTheLastCompleteFrame()
        {
            var term = Filled(5);
            term.FallbackBuffer.FeedText("\x1b[?2026h\x1b[2J\x1b[Hhalf-drawn");
            term.RenderGridForTest(800, 300);
            Assert.True(term.LastFrameHeldBySyncOutputForTest);
            term.FallbackBuffer.FeedText(" frame\x1b[?2026l");
            term.RenderGridForTest(800, 300);
            Assert.False(term.LastFrameHeldBySyncOutputForTest);
            Assert.Equal("half-drawn frame", TopRowText(term));
        }

        [AvaloniaFact]
        public void Metrics_LineHeightSetting_ScalesRows_FontSizeFollowsSettings()
        {
            var keepLh = TerminalOptions.LineHeight; var keepFs = TerminalOptions.FontSize;
            try
            {
                var term = Filled(5, 800, 400);
                double h1 = term.CellHeightForTest;
                int rows1 = term.FallbackBuffer.Rows;
                Assert.True(h1 >= TerminalOptions.EffectiveFontSize(MacKeymap.IsMacOS) && h1 <= Math.Ceiling(TerminalOptions.EffectiveFontSize(MacKeymap.IsMacOS) * 1.4),
                    "lineHeight 1 = the font's line height (was a fixed 17 px), got " + h1);
                TerminalOptions.LineHeight = 1.5;
                term.ApplyTerminalOptions();
                double h2 = term.CellHeightForTest;
                Assert.True(h2 >= h1 * 1.4 && h2 <= h1 * 1.5 + 1, h1 + " -> " + h2);
                Assert.True(term.FallbackBuffer.Rows < rows1);
                TerminalOptions.LineHeight = 1.0;
                TerminalOptions.FontSize = 20;
                term.ApplyTerminalOptions();
                Assert.True(term.CellHeightForTest > h1);
            }
            finally
            {
                TerminalOptions.LineHeight = keepLh; TerminalOptions.FontSize = keepFs;
            }
        }

        [AvaloniaFact]
        public void Frame_DenseColouredScreen_StaysWithinBudget_AndRowsAreReused()
        {
            var term = new AvaloniaTerminalEmulatorControl();
            const double w = 1600, h = 900;
            term.RenderGridForTest(w, h);
            var buf = term.FallbackBuffer;
            var screens = new string[8];
            for (int i = 0; i < screens.Length; i++) screens[i] = "\x1b[H" + DenseColouredScreen(buf.Cols, buf.Rows, 10 + i);
            buf.FeedText(screens[0]);
            for (int i = 0; i < 20; i++) { buf.FeedText(screens[i % 8]); term.RenderGridForTest(w, h); } // warm-up (JIT, glyph caches)

            // a static dense screen (cursor / selection / hover repaints): rows come from the cache
            var sw = Stopwatch.StartNew();
            long a0 = GC.GetAllocatedBytesForCurrentThread();
            const int frames = 60;
            for (int i = 0; i < frames; i++) term.RenderGridForTest(w, h);
            long staticAlloc = (GC.GetAllocatedBytesForCurrentThread() - a0) / frames;
            double staticMs = sw.Elapsed.TotalMilliseconds / frames;
            Assert.Equal(0, term.LastFrameRowsBuiltForTest);

            // every row changes every frame (worst case)
            sw.Restart();
            a0 = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < frames; i++) { buf.FeedText(screens[(i * 3) % 8]); term.RenderGridForTest(w, h); }
            double changingMs = sw.Elapsed.TotalMilliseconds / frames;
            long changingAlloc = (GC.GetAllocatedBytesForCurrentThread() - a0) / frames;

            // streaming output: each frame scrolls by a line, only the new row is shaped
            buf.FeedText("\x1b[0m\x1b[2J\x1b[H");
            for (int i = 0; i < buf.Rows + 5; i++) buf.FeedText("\x1b[3" + (i % 7) + "m" + i + " streaming output\x1b[0m\r\n");
            term.RenderGridForTest(w, h);
            buf.FeedText("one more line\r\n");
            term.RenderGridForTest(w, h);
            int streamedRows = term.LastFrameRowsBuiltForTest;

            output.WriteLine("grid " + buf.Cols + "x" + buf.Rows + ": static frame " + staticMs.ToString("0.00") + " ms / " + (staticAlloc / 1024) + " KB, changing frame (incl. parse) " +
                changingMs.ToString("0.00") + " ms / " + (changingAlloc / 1024) + " KB, rows shaped when output scrolls by one line: " + streamedRows +
                " (before: every frame re-shaped every row; 5.7 ms / 2.3 MB for a static 157x53 frame)");
            Assert.True(streamedRows <= 2, "rows shaped " + streamedRows);
            Assert.True(staticMs < PerfBudget.Ms(4), "static frame " + staticMs + " ms");
            Assert.True(staticAlloc < 64 * 1024, "static frame allocates " + staticAlloc);
            Assert.True(changingMs < PerfBudget.Ms(40), "changing frame " + changingMs + " ms");
        }

        [AvaloniaFact]
        public void Output_100kLines_StreamedWithFrames_StaysFast()
        {
            var term = new AvaloniaTerminalEmulatorControl();
            term.RenderGridForTest(1000, 600);
            var buf = term.FallbackBuffer;
            var sb = new StringBuilder();
            for (int i = 0; i < 100000; i++) sb.Append("\x1b[32m").Append(i).Append("\x1b[0m output line with some text in it\r\n");
            byte[] all = Encoding.UTF8.GetBytes(sb.ToString());
            sb.Clear();
            var chunk = new byte[8192];
            var sw = Stopwatch.StartNew();
            int frames = 0;
            for (int off = 0; off < all.Length; off += 8192)
            {
                int n = Math.Min(8192, all.Length - off);
                Buffer.BlockCopy(all, off, chunk, 0, n);
                buf.ProcessBytes(chunk, n);
                if ((off / 8192) % 8 == 7) { term.RenderGridForTest(1000, 600); frames++; } // a frame per 64 KB
            }
            term.RenderGridForTest(1000, 600);
            sw.Stop();
            output.WriteLine("100k lines (" + (all.Length / 1024) + " KB) parsed + " + frames + " frames: " + sw.ElapsedMilliseconds + " ms (before: ~800 ms)");
            Assert.True(sw.ElapsedMilliseconds < PerfBudget.Ms(1500), sw.ElapsedMilliseconds + " ms");
            term.SelectLineAtCell(buf.Rows - 2);
            Assert.Contains("99999", term.GetSelectedText());
        }

        [AvaloniaFact]
        public void BoxDrawingAndWideGlyphs_RenderOnTheGrid()
        {
            var term = new AvaloniaTerminalEmulatorControl();
            term.FallbackBuffer.FeedText("╭──┬──╮\r\n│ a│你│\r\n├──┼──┤\r\n╰──┴──╯ ░▒▓█ \uE0B0\uE0B2 \U0001F680\r\n");
            term.RenderGridForTest(800, 300);
            term.RenderGridForTest(800, 300);
            Assert.Equal(0, term.LastFrameRowsBuiltForTest);
            term.SelectLineAtCell(1);
            Assert.Equal("│ a│你│", term.GetSelectedText());
        }
    }

    internal static class AvaloniaTerminalGridCanvasPad
    {
        public static readonly double Left = (double)typeof(AvaloniaTerminalEmulatorControl).Assembly
            .GetType("codeLess.AvaloniaApp.AvaloniaTerminalGridCanvas").GetField("PadLeft").GetValue(null);
    }
}
