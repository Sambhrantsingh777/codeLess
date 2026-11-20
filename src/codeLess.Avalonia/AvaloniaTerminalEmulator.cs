using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using Avalonia.Threading;
using codeLess.Studio;

namespace codeLess.AvaloniaApp
{
    public class AvaloniaTerminalEmulatorControl : Grid
    {
        public ConPtyTerminalSession Session { get; private set; }
        public Vt100TerminalBuffer FallbackBuffer { get; private set; }
        public Vt100TerminalBuffer ActiveBuffer => Session?.Buffer ?? FallbackBuffer;
        public string ShellDisplayName { get; set; } = "pwsh";
        public string WorkingDirectory { get; private set; } = "";
        public bool IsLightTerminal { get; private set; } = false;

        private readonly AvaloniaTerminalGridCanvas gridCanvas;
        private readonly Border inlineAiBar;
        private readonly TextBox inlineAiInput;
        private readonly Button inlineAiGenerateBtn;
        private readonly Button inlineAiCancelBtn;
        private readonly DispatcherTimer cursorTimer;
        private bool cursorBlinkOn = true;
        private bool exitedNotified = false;

        public event Action<string, Action<string>> InlineShellPromptRequested;
        public event Action<AvaloniaTerminalEmulatorControl> SessionExited;
        public event Action<string> OutputReceived;

        public Color TermBgColor { get; private set; } = Color.FromRgb(15, 17, 23);
        public Color TermFgColor { get; private set; } = Color.FromRgb(226, 232, 240);
        public Color AccentColor { get; private set; } = Color.FromRgb(59, 130, 246);

        public AvaloniaTerminalEmulatorControl()
        {
            Focusable = true;
            ClipToBounds = true;
            FallbackBuffer = new Vt100TerminalBuffer(100, 26, false);

            gridCanvas = new AvaloniaTerminalGridCanvas(this);
            Children.Add(gridCanvas);

            // Inline Ctrl/Cmd+K AI shell assistant bar
            inlineAiInput = new TextBox
            {
                Watermark = "Describe shell command (e.g., list top 5 largest files, git undo last commit)...",
                FontSize = 12,
                MinWidth = 280,
                Padding = new Thickness(8, 4)
            };
            inlineAiGenerateBtn = new Button
            {
                Content = "Generate ↵",
                FontSize = 11.5,
                Padding = new Thickness(10, 4),
                Margin = new Thickness(6, 0, 0, 0)
            };
            inlineAiCancelBtn = new Button
            {
                Content = "Esc",
                FontSize = 11.5,
                Padding = new Thickness(8, 4),
                Margin = new Thickness(4, 0, 0, 0)
            };

            var barGrid = new Grid
            {
                ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto,Auto"),
                VerticalAlignment = global::Avalonia.Layout.VerticalAlignment.Center
            };
            var barLabel = new TextBlock
            {
                Text = "✦ Terminal AI (Ctrl+K):",
                FontWeight = FontWeight.Bold,
                FontSize = 11.5,
                VerticalAlignment = global::Avalonia.Layout.VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 8, 0),
                Foreground = new SolidColorBrush(Color.FromRgb(96, 165, 250))
            };
            barGrid.Children.Add(barLabel);
            Grid.SetColumn(inlineAiInput, 1);
            barGrid.Children.Add(inlineAiInput);
            Grid.SetColumn(inlineAiGenerateBtn, 2);
            barGrid.Children.Add(inlineAiGenerateBtn);
            Grid.SetColumn(inlineAiCancelBtn, 3);
            barGrid.Children.Add(inlineAiCancelBtn);

            inlineAiBar = new Border
            {
                Child = barGrid,
                Background = new SolidColorBrush(Color.FromRgb(26, 30, 42)),
                BorderBrush = new SolidColorBrush(Color.FromRgb(59, 130, 246)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(5),
                Padding = new Thickness(10, 6),
                Margin = new Thickness(12, 8, 18, 0),
                VerticalAlignment = global::Avalonia.Layout.VerticalAlignment.Top,
                IsVisible = false
            };
            Children.Add(inlineAiBar);

            inlineAiGenerateBtn.Click += (s, e) => SubmitInlineAiShellRequest();
            inlineAiCancelBtn.Click += (s, e) => HideInlineAiBar();
            inlineAiInput.KeyDown += (s, e) =>
            {
                if (e.Key == Key.Enter)
                {
                    SubmitInlineAiShellRequest();
                    e.Handled = true;
                }
                else if (e.Key == Key.Escape)
                {
                    HideInlineAiBar();
                    e.Handled = true;
                }
            };

            cursorTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(530) };
            cursorTimer.Tick += (s, e) =>
            {
                cursorBlinkOn = !cursorBlinkOn;
                if (IsVisible) gridCanvas.InvalidateVisual();
            };
            cursorTimer.Start();

            AttachedToVisualTree += (s, e) => { if (!cursorTimer.IsEnabled) cursorTimer.Start(); };
            DetachedFromVisualTree += (s, e) => { if (cursorTimer.IsEnabled) cursorTimer.Stop(); };
        }

        public bool IsInlineAiBarVisible => inlineAiBar.IsVisible;
        public bool CursorBlinkOn => cursorBlinkOn;

        public void ShowInlineAiBar()
        {
            inlineAiBar.IsVisible = true;
            inlineAiInput.Focus();
            inlineAiInput.SelectAll();
            InvalidateVisual();
        }

        public void HideInlineAiBar()
        {
            inlineAiBar.IsVisible = false;
            gridCanvas.Focus();
            InvalidateVisual();
        }

        private void SubmitInlineAiShellRequest()
        {
            string prompt = (inlineAiInput.Text ?? "").Trim();
            if (string.IsNullOrEmpty(prompt)) return;

            if (InlineShellPromptRequested != null)
            {
                InlineShellPromptRequested(prompt, generatedCmd =>
                {
                    Dispatcher.UIThread.Post(() =>
                    {
                        HideInlineAiBar();
                        if (!string.IsNullOrWhiteSpace(generatedCmd))
                        {
                            WriteAutomatedInput(generatedCmd.Trim());
                        }
                    });
                });
            }
            else
            {
                HideInlineAiBar();
                WriteAutomatedInput(prompt);
            }
        }

        public void ApplyTheme(StudioTheme theme)
        {
            if (theme == null) return;
            IsLightTerminal = theme.IsLight || AvaloniaColorHelper.IsLightColor(theme.EditorBg) || AvaloniaColorHelper.IsLightColor(theme.TerminalBg);
            TermBgColor = IsLightTerminal ? Color.FromRgb(255, 255, 255) : AvaloniaColorHelper.ToAvColor(theme.TerminalBg);
            TermFgColor = IsLightTerminal ? Color.FromRgb(31, 35, 40) : AvaloniaColorHelper.ToAvColor(theme.TerminalText);
            AccentColor = AvaloniaColorHelper.ToAvColor(theme.AccentPrimary);
            inlineAiBar.Background = AvaloniaColorHelper.ToBrush(theme.FloatingBg);
            inlineAiBar.BorderBrush = AvaloniaColorHelper.ToBrush(theme.AccentPrimary);

            var sysBg = System.Drawing.Color.FromArgb(TermBgColor.R, TermBgColor.G, TermBgColor.B);
            var sysFg = System.Drawing.Color.FromArgb(TermFgColor.R, TermFgColor.G, TermFgColor.B);
            FallbackBuffer?.ApplyThemePalette(IsLightTerminal, sysBg, sysFg);
            Session?.Buffer?.ApplyThemePalette(IsLightTerminal, sysBg, sysFg);
            gridCanvas.InvalidateVisual();
        }

        public static string ResolveDefaultShellCommand()
        {
            if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                return ConPtyTerminalSession.GetUserUnixShell();
            }
            return "powershell.exe";
        }

        public static string NormalizeShellLabel(string shellCommand)
        {
            if (string.IsNullOrWhiteSpace(shellCommand))
            {
                return RuntimeInformation.IsOSPlatform(OSPlatform.OSX) ? "zsh"
                    : (RuntimeInformation.IsOSPlatform(OSPlatform.Linux) ? "bash" : "pwsh");
            }
            string firstToken = shellCommand.Trim();
            int spaceIdx = firstToken.IndexOf(' ');
            if (spaceIdx > 0 && !firstToken.StartsWith("\""))
                firstToken = firstToken.Substring(0, spaceIdx);
            firstToken = firstToken.Trim('"');
            string leaf = Path.GetFileNameWithoutExtension(firstToken).ToLowerInvariant();
            if (leaf.Contains("zsh")) return "zsh";
            if (leaf.Contains("bash")) return "bash";
            if (leaf.Contains("pwsh")) return "pwsh";
            if (leaf.Contains("powershell")) return "powershell";
            if (leaf.Contains("cmd")) return "cmd";
            return string.IsNullOrEmpty(leaf) ? "zsh" : leaf;
        }

        private int repaintQueued = 0;

        public static bool IsDesktopRuntime =>
            Application.Current?.ApplicationLifetime is global::Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime;

        public bool StartShell(string workingDir, string customShellCommand = null)
        {
            StopShell(notifyExit: false);
            exitedNotified = false;
            if (!cursorTimer.IsEnabled) cursorTimer.Start();
            WorkingDirectory = (!string.IsNullOrEmpty(workingDir) && workingDir != "/" && Directory.Exists(workingDir))
                ? workingDir
                : ConPtyTerminalSession.GetDefaultHomeDirectory();

            string resolvedCmd = string.IsNullOrWhiteSpace(customShellCommand)
                ? ResolveDefaultShellCommand()
                : customShellCommand;
            ShellDisplayName = NormalizeShellLabel(resolvedCmd);

            int cols = Math.Max(20, gridCanvas.CalcColumns());
            int rows = Math.Max(6, gridCanvas.CalcRows());

            // In headless unit tests (UseHeadlessDrawing), keep fast deterministic FallbackBuffer instead of spawning 40 OS login shells
            if (!IsDesktopRuntime)
            {
                FallbackBuffer?.Resize(cols, rows);
                gridCanvas.InvalidateVisual();
                return false;
            }

            try
            {
                Session = new ConPtyTerminalSession(ShellDisplayName, resolvedCmd, WorkingDirectory, cols, rows, IsLightTerminal);
                // The session clamps its initial grid (min 20x6); if the laid-out canvas is smaller, sync the
                // PTY winsize to what is actually rendered, since no further SizeChanged will arrive.
                if (gridCanvas.Bounds.Height > 1)
                    Session.Resize(gridCanvas.CalcColumns(), gridCanvas.CalcRows());
                Session.OutputReceived += () =>
                {
                    if (System.Threading.Interlocked.Exchange(ref repaintQueued, 1) != 0)
                        return;
                    Dispatcher.UIThread.Post(() =>
                    {
                        System.Threading.Interlocked.Exchange(ref repaintQueued, 0);
                        gridCanvas.InvalidateVisual();
                        if (OutputReceived != null)
                            OutputReceived.Invoke(GetVisibleScreenText());
                    });
                };
                Session.ProcessExited += code =>
                {
                    Dispatcher.UIThread.Post(() =>
                    {
                        if (!exitedNotified)
                        {
                            exitedNotified = true;
                            SessionExited?.Invoke(this);
                        }
                    });
                };
                gridCanvas.InvalidateVisual();
                return Session.IsRunning;
            }
            catch
            {
                gridCanvas.InvalidateVisual();
                return false;
            }
        }

        public void StopShell(bool notifyExit = false)
        {
            if (cursorTimer != null && cursorTimer.IsEnabled)
                cursorTimer.Stop();
            if (Session != null)
            {
                try { Session.Dispose(); } catch { }
                Session = null;
            }
            if (notifyExit && !exitedNotified)
            {
                exitedNotified = true;
                SessionExited?.Invoke(this);
            }
        }

        public void WriteAutomatedInput(string text, bool echoLocallyWhenLive = false)
        {
            if (string.IsNullOrEmpty(text)) return;
            string trimmed = text.Trim();
            if (string.Equals(trimmed, "exit", StringComparison.OrdinalIgnoreCase))
            {
                if (Session != null && Session.IsRunning)
                {
                    try { Session.WriteInput("exit\r"); } catch { }
                }
                StopShell(notifyExit: true);
                return;
            }

            if (Session != null && Session.IsRunning)
            {
                Session.WriteInput(text);
                if (echoLocallyWhenLive)
                    ActiveBuffer?.FeedText(text);
            }
            else
            {
                ActiveBuffer?.FeedText(text);
            }
            gridCanvas.InvalidateVisual();
        }

        public void SeedInitialPrompt(string ansiPrompt)
        {
            FallbackBuffer?.FeedText(ansiPrompt ?? "");
            Session?.Buffer?.FeedText(ansiPrompt ?? "");
            gridCanvas.InvalidateVisual();
        }

        public void FeedAnsiPreview(string ansiSequence)
        {
            if (Session != null)
            {
                exitedNotified = true;
                var oldSession = Session;
                Session = null;
                try { oldSession.Dispose(); } catch { }
            }
            if (FallbackBuffer == null)
            {
                FallbackBuffer = new Vt100TerminalBuffer(120, 28, IsLightTerminal);
            }
            if (FallbackBuffer.CursorCol > 0)
            {
                FallbackBuffer.FeedText("\r\n");
            }
            FallbackBuffer.FeedText(ansiSequence ?? "");
            gridCanvas.InvalidateVisual();
        }

        public string GetVisibleScreenText()
        {
            return ActiveBuffer?.GetScrollbackAndScreenText(250) ?? "";
        }

        public void ClearTerminal()
        {
            if (Session != null && Session.IsRunning)
            {
                try { Session.WriteInput("\x0c"); } catch { }
            }
            ActiveBuffer?.ClearScrollbackAndScreen();
            gridCanvas.InvalidateVisual();
        }

        public string GetSelectedText() => gridCanvas.GetSelectedText();
        public void SelectAll() => gridCanvas.SelectAll();
        public void ClearSelection() => gridCanvas.ClearSelection();
        public void FocusTerminalCanvas() => gridCanvas.Focus();
    }

    internal class AvaloniaTerminalGridCanvas : Control
    {
        private readonly AvaloniaTerminalEmulatorControl owner;
        public const double DefaultCellW = 7.6;
        public const double CellH = 17.0;
        public const double PadLeft = 10.0;
        public const double PadTop = 6.0;
        private const double TermFontSize = 12.5;

        private static readonly string MonoFontFamilyName = RuntimeInformation.IsOSPlatform(OSPlatform.OSX)
            ? "Menlo, Monaco, SF Mono, monospace"
            : (RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
                ? "Cascadia Code, Consolas, monospace"
                : "DejaVu Sans Mono, Liberation Mono, Ubuntu Mono, monospace");

        private static readonly Typeface FontRegular = new Typeface(MonoFontFamilyName, FontStyle.Normal, FontWeight.Normal);
        private static readonly Typeface FontBold = new Typeface(MonoFontFamilyName, FontStyle.Normal, FontWeight.Bold);
        private static readonly Typeface FontItalic = new Typeface(MonoFontFamilyName, FontStyle.Italic, FontWeight.Normal);

        private double measuredCellW = 0;
        public double CellW
        {
            get
            {
                if (measuredCellW <= 3.0)
                {
                    try
                    {
                        var probe = new FormattedText("MMMMMMMMMM", CultureInfo.InvariantCulture, FlowDirection.LeftToRight, FontRegular, TermFontSize, Brushes.White);
                        double w10 = probe.WidthIncludingTrailingWhitespace / 10.0;
                        measuredCellW = (w10 > 4.0 && w10 < 16.0) ? w10 : DefaultCellW;
                        double th = probe.Height;
                        measuredTextH = (th > 4.0 && th <= CellH) ? th : CellH - 2;
                    }
                    catch
                    {
                        measuredCellW = DefaultCellW;
                        measuredTextH = CellH - 2;
                    }
                }
                return measuredCellW;
            }
        }

        // Height of one text line (ascent + descent) of the font actually used for drawing.
        private double measuredTextH = 0;

        /// <summary>
        /// Vertical offset from the top of a cell row to the top of the drawn text line, so that the
        /// glyph box (and the cursor, which uses the same box) is centred inside the CellH row.
        /// Previously text was drawn at a fixed +1px with a 17px cell, which left the glyphs sitting in
        /// the upper part of the row/cursor block on fonts with a short line height (Menlo on macOS).
        /// </summary>
        public double TextTopOffset
        {
            get
            {
                double unused = CellW; // ensures metrics are measured
                double th = measuredTextH > 0 ? measuredTextH : CellH - 2;
                return Math.Max(0, (CellH - th) / 2.0);
            }
        }

        public double TextLineHeight
        {
            get
            {
                double unused = CellW;
                return measuredTextH > 0 ? measuredTextH : CellH - 2;
            }
        }

        // Brushes are cached per colour: Render runs at up to 60 fps under heavy output and used to
        // allocate a new SolidColorBrush (and Pen) for every text run and background span every frame.
        private readonly Dictionary<Color, IImmutableSolidColorBrush> brushCache = new Dictionary<Color, IImmutableSolidColorBrush>();
        private IImmutableSolidColorBrush GetBrush(Color c)
        {
            IImmutableSolidColorBrush b;
            if (!brushCache.TryGetValue(c, out b))
            {
                if (brushCache.Count > 512) brushCache.Clear();
                b = new ImmutableSolidColorBrush(c);
                brushCache[c] = b;
            }
            return b;
        }

        // Text runs are cached across frames (cursor blink and incremental output repaint mostly identical
        // rows). Bounded: the cache is dropped wholesale once it exceeds the cap so it can never grow unbounded.
        private const int MaxTextCacheEntries = 4096;
        private readonly Dictionary<(string, int, Color), FormattedText> textCache = new Dictionary<(string, int, Color), FormattedText>();
        private FormattedText GetFormattedText(string text, bool bold, bool italic, Color fg)
        {
            int style = bold ? 1 : (italic ? 2 : 0);
            var key = (text, style, fg);
            FormattedText ft;
            if (!textCache.TryGetValue(key, out ft))
            {
                if (textCache.Count >= MaxTextCacheEntries) textCache.Clear();
                var tf = bold ? FontBold : (italic ? FontItalic : FontRegular);
                ft = new FormattedText(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, tf, TermFontSize, GetBrush(fg));
                textCache[key] = ft;
            }
            return ft;
        }

        internal int TextCacheCount => textCache.Count;
        internal int BrushCacheCount => brushCache.Count;

        private int scrollbackOffset = 0;
        private bool isSelecting = false;
        private int selStartRow = -1, selStartCol = -1;
        private int selEndRow = -1, selEndCol = -1;

        public AvaloniaTerminalGridCanvas(AvaloniaTerminalEmulatorControl owner)
        {
            this.owner = owner;
            Focusable = true;
            ClipToBounds = true;
        }

        // Rows fall back to a default only before layout (zero height). For a real but short panel, report the
        // rows that actually fit: telling the PTY 24 rows for a 70px-tall panel put the shell's cursor row
        // below the visible area, so the prompt/typed text were drawn off-row or clipped. (Shrinking rows is
        // lossless - Buffer.Resize pushes top rows into scrollback - unlike columns, which truncate, so the
        // column threshold is left as-is.)
        public int CalcColumns() => Bounds.Width > 260 ? Math.Max(32, (int)((Bounds.Width - PadLeft * 2 - 4) / CellW)) : 100;
        public int CalcRows() => Bounds.Height > 1 ? Math.Max(4, (int)((Bounds.Height - PadTop * 2) / CellH)) : 24;

        protected override void OnSizeChanged(SizeChangedEventArgs e)
        {
            base.OnSizeChanged(e);
            int c = CalcColumns();
            int r = CalcRows();
            if (owner.Session != null)
            {
                owner.Session.Resize(c, r);
            }
            else if (owner.FallbackBuffer != null)
            {
                owner.FallbackBuffer.Resize(c, r);
            }
            InvalidateVisual();
        }

        public bool HasSelection()
        {
            return selStartRow >= 0 && selEndRow >= 0 && (selStartRow != selEndRow || selStartCol != selEndCol);
        }

        public void ClearSelection()
        {
            selStartRow = selStartCol = selEndRow = selEndCol = -1;
            isSelecting = false;
            InvalidateVisual();
        }

        public void SelectAll()
        {
            var buf = owner.ActiveBuffer;
            if (buf == null) return;
            selStartRow = 0;
            selStartCol = 0;
            selEndRow = buf.Rows - 1;
            selEndCol = buf.Cols - 1;
            InvalidateVisual();
        }

        public string GetSelectedText()
        {
            var buf = owner.ActiveBuffer;
            if (!HasSelection() || buf == null) return "";
            int r0 = selStartRow, c0 = selStartCol, r1 = selEndRow, c1 = selEndCol;
            if (r0 > r1 || (r0 == r1 && c0 > c1))
            {
                int tr = r0; r0 = r1; r1 = tr;
                int tc = c0; c0 = c1; c1 = tc;
            }
            var sb = new StringBuilder();
            for (int r = Math.Max(0, r0); r <= Math.Min(buf.Rows - 1, r1); r++)
            {
                int startC = (r == r0) ? Math.Max(0, c0) : 0;
                int endC = (r == r1) ? Math.Min(buf.Cols - 1, c1) : (buf.Cols - 1);
                var lineSb = new StringBuilder();
                for (int c = startC; c <= endC; c++)
                {
                    char ch = buf.GetViewportCell(r, c, scrollbackOffset).Char;
                    lineSb.Append(ch == '\0' ? ' ' : ch);
                }
                if (r < r1) sb.AppendLine(lineSb.ToString().TrimEnd());
                else sb.Append(lineSb.ToString().TrimEnd());
            }
            return sb.ToString();
        }

        private bool IsCellSelected(int r, int c)
        {
            if (!HasSelection()) return false;
            int r0 = selStartRow, c0 = selStartCol, r1 = selEndRow, c1 = selEndCol;
            if (r0 > r1 || (r0 == r1 && c0 > c1))
            {
                int tr = r0; r0 = r1; r1 = tr;
                int tc = c0; c0 = c1; c1 = tc;
            }
            if (r < r0 || r > r1) return false;
            if (r == r0 && r == r1) return c >= c0 && c <= c1;
            if (r == r0) return c >= c0;
            if (r == r1) return c <= c1;
            return true;
        }

        private int activeMouseBtnCode = -1;

        protected override void OnPointerPressed(PointerPressedEventArgs e)
        {
            base.OnPointerPressed(e);
            Focus();
            var buf = owner.ActiveBuffer;
            var pt = e.GetPosition(this);
            int c = Math.Max(0, (int)((pt.X - PadLeft) / CellW));
            int r = Math.Max(0, (int)((pt.Y - PadTop) / CellH));
            var props = e.GetCurrentPoint(this).Properties;
            bool shift = e.KeyModifiers.HasFlag(KeyModifiers.Shift);
            bool alt = e.KeyModifiers.HasFlag(KeyModifiers.Alt);
            bool ctrl = e.KeyModifiers.HasFlag(KeyModifiers.Control);

            if (buf != null && buf.MouseTrackingMode != 0 && !shift && !HasSelection())
            {
                int btn = props.IsLeftButtonPressed ? 0 : (props.IsMiddleButtonPressed ? 1 : (props.IsRightButtonPressed ? 2 : -1));
                if (btn >= 0)
                {
                    string seq = buf.EncodeMouseButtonOrMoveSequence(btn, false, false, c + 1, r + 1, shift, alt, ctrl);
                    if (seq != null)
                    {
                        activeMouseBtnCode = btn;
                        owner.WriteAutomatedInput(seq);
                        e.Handled = true;
                        return;
                    }
                }
            }

            if (props.IsLeftButtonPressed)
            {
                isSelecting = true;
                selStartRow = selEndRow = r;
                selStartCol = selEndCol = c;
                InvalidateVisual();
            }
            e.Handled = true;
        }

        protected override void OnPointerMoved(PointerEventArgs e)
        {
            base.OnPointerMoved(e);
            var buf = owner.ActiveBuffer;
            var pt = e.GetPosition(this);
            int c = Math.Max(0, (int)((pt.X - PadLeft) / CellW));
            int r = Math.Max(0, (int)((pt.Y - PadTop) / CellH));
            bool shift = e.KeyModifiers.HasFlag(KeyModifiers.Shift);

            if (buf != null && buf.MouseTrackingMode >= 1002 && !shift && !isSelecting)
            {
                string seq = buf.EncodeMouseButtonOrMoveSequence(
                    activeMouseBtnCode,
                    false,
                    true,
                    c + 1,
                    r + 1,
                    shift,
                    e.KeyModifiers.HasFlag(KeyModifiers.Alt),
                    e.KeyModifiers.HasFlag(KeyModifiers.Control));
                if (seq != null)
                {
                    owner.WriteAutomatedInput(seq);
                    return;
                }
            }

            if (!isSelecting) return;
            selEndCol = c;
            selEndRow = r;
            InvalidateVisual();
        }

        protected override void OnPointerReleased(PointerReleasedEventArgs e)
        {
            base.OnPointerReleased(e);
            if (activeMouseBtnCode >= 0 && owner.ActiveBuffer != null)
            {
                int released = activeMouseBtnCode;
                activeMouseBtnCode = -1;
                var pt = e.GetPosition(this);
                int c = Math.Max(0, (int)((pt.X - PadLeft) / CellW));
                int r = Math.Max(0, (int)((pt.Y - PadTop) / CellH));
                string relSeq = owner.ActiveBuffer.EncodeMouseButtonOrMoveSequence(
                    released,
                    true,
                    false,
                    c + 1,
                    r + 1,
                    e.KeyModifiers.HasFlag(KeyModifiers.Shift),
                    e.KeyModifiers.HasFlag(KeyModifiers.Alt),
                    e.KeyModifiers.HasFlag(KeyModifiers.Control));
                if (relSeq != null)
                    owner.WriteAutomatedInput(relSeq);
            }
            isSelecting = false;
        }

        protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
        {
            base.OnPointerWheelChanged(e);
            var buf = owner.ActiveBuffer;
            if (buf == null || Math.Abs(e.Delta.Y) < 0.01) return;

            bool wheelUp = e.Delta.Y > 0;
            int steps = Math.Max(1, Math.Min(10, (int)Math.Round(Math.Abs(e.Delta.Y) * 3)));
            var pt = e.GetPosition(this);
            int col1 = Math.Max(1, (int)((pt.X - PadLeft) / CellW) + 1);
            int row1 = Math.Max(1, (int)((pt.Y - PadTop) / CellH) + 1);
            bool shift = e.KeyModifiers.HasFlag(KeyModifiers.Shift);
            bool alt = e.KeyModifiers.HasFlag(KeyModifiers.Alt);
            bool ctrl = e.KeyModifiers.HasFlag(KeyModifiers.Control);

            string vtWheel = buf.EncodeMouseWheelSequence(wheelUp, col1, row1, shift, alt, ctrl);
            if (vtWheel != null)
            {
                var batch = new StringBuilder(vtWheel.Length * steps);
                for (int i = 0; i < steps; i++) batch.Append(vtWheel);
                owner.WriteAutomatedInput(batch.ToString());
                e.Handled = true;
                return;
            }

            int maxSb = buf.ScrollbackCount;
            scrollbackOffset = Math.Max(0, Math.Min(maxSb, scrollbackOffset + (wheelUp ? steps : -steps)));
            InvalidateVisual();
            e.Handled = true;
        }

        private readonly StringBuilder lineInputTracker = new StringBuilder();

        protected override async void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            bool ctrlOrCmd = e.KeyModifiers.HasFlag(KeyModifiers.Control) || e.KeyModifiers.HasFlag(KeyModifiers.Meta);
            bool shift = e.KeyModifiers.HasFlag(KeyModifiers.Shift);
            bool alt = e.KeyModifiers.HasFlag(KeyModifiers.Alt);

            if (ctrlOrCmd && e.Key == Key.K)
            {
                owner.ShowInlineAiBar();
                e.Handled = true;
                return;
            }

            if ((e.KeyModifiers.HasFlag(KeyModifiers.Meta) && e.Key == Key.C) ||
                (e.KeyModifiers.HasFlag(KeyModifiers.Control) && shift && e.Key == Key.C) ||
                (e.KeyModifiers.HasFlag(KeyModifiers.Control) && e.Key == Key.C && HasSelection()))
            {
                string sel = GetSelectedText();
                if (!string.IsNullOrEmpty(sel))
                {
                    var top = TopLevel.GetTopLevel(this);
                    if (top?.Clipboard != null)
                        await top.Clipboard.SetTextAsync(sel);
                    ClearSelection();
                }
                e.Handled = true;
                return;
            }

            if ((ctrlOrCmd && e.Key == Key.V) || (shift && e.Key == Key.Insert))
            {
                var top = TopLevel.GetTopLevel(this);
                if (top?.Clipboard != null)
                {
                    string clip = await top.Clipboard.GetTextAsync();
                    if (!string.IsNullOrEmpty(clip))
                        owner.WriteAutomatedInput(clip);
                }
                e.Handled = true;
                return;
            }

            if (shift && e.Key == Key.PageUp && owner.ActiveBuffer != null)
            {
                if (owner.ActiveBuffer.IsAlternateScreen)
                {
                    owner.WriteAutomatedInput("\x1b[5~");
                }
                else
                {
                    scrollbackOffset = Math.Min(owner.ActiveBuffer.ScrollbackCount, scrollbackOffset + 10);
                    InvalidateVisual();
                }
                e.Handled = true;
                return;
            }
            if (shift && e.Key == Key.PageDown && owner.ActiveBuffer != null)
            {
                if (owner.ActiveBuffer.IsAlternateScreen)
                {
                    owner.WriteAutomatedInput("\x1b[6~");
                }
                else
                {
                    scrollbackOffset = Math.Max(0, scrollbackOffset - 10);
                    InvalidateVisual();
                }
                e.Handled = true;
                return;
            }

            bool appCursor = owner.ActiveBuffer?.ApplicationCursorKeys ?? false;
            if (e.Key == Key.Enter && !shift && !alt)
            {
                if (string.Equals(lineInputTracker.ToString().Trim(), "exit", StringComparison.OrdinalIgnoreCase))
                {
                    lineInputTracker.Clear();
                    owner.WriteAutomatedInput("exit");
                    e.Handled = true;
                    return;
                }
                lineInputTracker.Clear();
            }
            else if (e.Key == Key.Back)
            {
                if (lineInputTracker.Length > 0) lineInputTracker.Length--;
            }
            string vt = TranslateKeyToVtSequence(e.Key, e.KeyModifiers, appCursor);

            if (!string.IsNullOrEmpty(vt))
            {
                scrollbackOffset = 0;
                ClearSelection();
                owner.WriteAutomatedInput(vt);
                e.Handled = true;
            }
        }

        /// <summary>
        /// Maps a non-text key press to the byte sequence sent to the PTY (null when the key is not a
        /// control/navigation key and should arrive via OnTextInput instead).
        /// Shift+Enter and Alt/Option+Enter send ESC CR ("\x1b\r"), the newline-without-submit sequence
        /// TUI apps such as Claude Code and opencode recognise; plain Enter sends CR.
        /// </summary>
        public static string TranslateKeyToVtSequence(Key key, KeyModifiers mods, bool appCursor)
        {
            bool shift = mods.HasFlag(KeyModifiers.Shift);
            bool alt = mods.HasFlag(KeyModifiers.Alt);
            bool ctrl = mods.HasFlag(KeyModifiers.Control);
            switch (key)
            {
                case Key.Enter: return (shift || alt) ? "\x1b\r" : "\r";
                case Key.Back: return "\x7f";
                case Key.Tab: return shift ? "\x1b[Z" : "\t";
                case Key.Escape: return "\x1b";
                case Key.Up: return appCursor ? "\x1bOA" : "\x1b[A";
                case Key.Down: return appCursor ? "\x1bOB" : "\x1b[B";
                case Key.Right: return appCursor ? "\x1bOC" : "\x1b[C";
                case Key.Left: return appCursor ? "\x1bOD" : "\x1b[D";
                case Key.Home: return "\x1b[H";
                case Key.End: return "\x1b[F";
                case Key.Delete: return "\x1b[3~";
                case Key.PageUp: return "\x1b[5~";
                case Key.PageDown: return "\x1b[6~";
                default:
                    if (ctrl && !alt && key >= Key.A && key <= Key.Z)
                        return ((char)((key - Key.A) + 1)).ToString();
                    return null;
            }
        }

        protected override void OnTextInput(TextInputEventArgs e)
        {
            base.OnTextInput(e);
            if (string.IsNullOrEmpty(e.Text)) return;

            // Filter out control characters (Enter, Backspace, Escape, Tab, Ctrl+A..Z) already handled in OnKeyDown
            var clean = new StringBuilder(e.Text.Length);
            foreach (char ch in e.Text)
            {
                if (ch >= 32 && ch != 127)
                {
                    clean.Append(ch);
                    lineInputTracker.Append(ch);
                }
            }
            if (clean.Length == 0)
            {
                e.Handled = true;
                return;
            }
            scrollbackOffset = 0;
            ClearSelection();
            owner.WriteAutomatedInput(clean.ToString());
            e.Handled = true;
        }

        public override void Render(DrawingContext context)
        {
            base.Render(context);
            double w = Bounds.Width;
            double h = Bounds.Height;
            double cellW = CellW;
            double textTop = TextTopOffset;
            var termBgBrush = GetBrush(owner.TermBgColor);
            context.FillRectangle(termBgBrush, new Rect(0, 0, w, h));

            var buf = owner.ActiveBuffer;
            if (buf == null) return;

            int rows = buf.Rows;
            int cols = buf.Cols;
            int maxVisCols = Math.Min(cols, Math.Max(1, (int)((w - PadLeft) / cellW) + 1));
            var selHighlightCol = Color.FromArgb(120, owner.AccentColor.R, owner.AccentColor.G, owner.AccentColor.B);
            var selHighlightBrush = GetBrush(selHighlightCol);
            var runSb = new StringBuilder(128);

            for (int r = 0; r < rows; r++)
            {
                double y = PadTop + r * CellH;
                if (y + CellH < 0 || y > h) continue;

                // Pass 1: Draw non-default cell backgrounds & selection highlights (coalesced horizontal spans)
                int cBg = 0;
                while (cBg < maxVisCols)
                {
                    Color cellBg = ResolveCellColors(buf.GetViewportCell(r, cBg, scrollbackOffset), IsCellSelected(r, cBg), selHighlightCol, out _, out _, out _, out _);
                    if (cellBg == owner.TermBgColor)
                    {
                        cBg++;
                        continue;
                    }
                    int runEnd = cBg + 1;
                    while (runEnd < maxVisCols)
                    {
                        Color nextBg = ResolveCellColors(buf.GetViewportCell(r, runEnd, scrollbackOffset), IsCellSelected(r, runEnd), selHighlightCol, out _, out _, out _, out _);
                        if (nextBg != cellBg) break;
                        runEnd++;
                    }
                    var bgBrush = (cellBg == selHighlightCol) ? selHighlightBrush : GetBrush(cellBg);
                    context.FillRectangle(bgBrush, new Rect(PadLeft + cBg * cellW, y, (runEnd - cBg) * cellW + 0.5, CellH + 0.5));
                    cBg = runEnd;
                }

                // Pass 2: Draw ASCII text in batched runs + non-ASCII glyphs pinned to exact cell X
                int c = 0;
                while (c < maxVisCols)
                {
                    TermCell cell = buf.GetViewportCell(r, c, scrollbackOffset);
                    char ch = cell.Char;
                    ResolveCellColors(cell, IsCellSelected(r, c), selHighlightCol, out Color fg, out bool bold, out bool italic, out bool underline);

                    if (ch <= ' ' || ch == '\0')
                    {
                        if (underline)
                        {
                            double ux = PadLeft + c * cellW;
                            context.DrawLine(new Pen(GetBrush(fg), 1), new Point(ux, y + CellH - 2), new Point(ux + cellW, y + CellH - 2));
                        }
                        c++;
                        continue;
                    }

                    // Non-ASCII symbol/box-drawing glyph: draw individually at exact column coordinate so fallback fonts never shift cursor
                    if (ch > 126)
                    {
                        double gx = PadLeft + c * cellW;
                        var fgBrush = GetBrush(fg);
                        string glyph = ch.ToString();
                        if (char.IsLowSurrogate(ch))
                        {
                            // Second cell of a wide astral emoji whose first half was drawn already (or clipped).
                            c++;
                            continue;
                        }
                        if (char.IsHighSurrogate(ch))
                        {
                            char lo = (c + 1 < cols) ? buf.GetViewportCell(r, c + 1, scrollbackOffset).Char : '\0';
                            if (!char.IsLowSurrogate(lo)) { c++; continue; }
                            glyph = new string(new[] { ch, lo });
                        }
                        var ftGlyph = GetFormattedText(glyph, bold, italic, fg);
                        context.DrawText(ftGlyph, new Point(gx, y + textTop));
                        if (underline)
                            context.DrawLine(new Pen(fgBrush, 1), new Point(gx, y + CellH - 2), new Point(gx + cellW, y + CellH - 2));
                        c++;
                        continue;
                    }

                    // Contiguous ASCII run sharing identical style & foreground color
                    int startCol = c;
                    runSb.Clear();
                    runSb.Append(ch);
                    c++;
                    while (c < maxVisCols)
                    {
                        TermCell next = buf.GetViewportCell(r, c, scrollbackOffset);
                        char nch = next.Char;
                        if (nch < 32 || nch > 126) break;
                        ResolveCellColors(next, IsCellSelected(r, c), selHighlightCol, out Color nfg, out bool nbold, out bool nitalic, out bool nunderline);
                        if (nfg != fg || nbold != bold || nitalic != italic || nunderline != underline)
                            break;
                        runSb.Append(nch);
                        c++;
                    }

                    double rx = PadLeft + startCol * cellW;
                    var runBrush = GetBrush(fg);
                    var ft = GetFormattedText(runSb.ToString(), bold, italic, fg);
                    context.DrawText(ft, new Point(rx, y + textTop));
                    if (underline)
                    {
                        context.DrawLine(new Pen(runBrush, 1), new Point(rx, y + CellH - 2), new Point(rx + (c - startCol) * cellW, y + CellH - 2));
                    }
                }
            }

            // Draw terminal cursor when at bottom of scrollback
            if (scrollbackOffset == 0 && buf.CursorVisible && owner.CursorBlinkOn)
            {
                double cx = PadLeft + buf.CursorCol * cellW;
                double cy = PadTop + buf.CursorRow * CellH;
                // Cursor covers exactly the same vertical box the glyphs are drawn in.
                var cursorRect = new Rect(cx, cy + textTop, cellW, TextLineHeight);
                context.FillRectangle(
                    GetBrush(Color.FromArgb(185, owner.AccentColor.R, owner.AccentColor.G, owner.AccentColor.B)),
                    cursorRect);
            }
        }

        private Color ResolveCellColors(TermCell cell, bool selected, Color selHighlightCol, out Color fg, out bool bold, out bool italic, out bool underline)
        {
            bool inverse = (cell.Flags & TermCell.FLAG_INVERSE) != 0;
            bool isDefBg = (cell.Flags & TermCell.FLAG_DEFAULT_BG) != 0 ||
                           (cell.Bg.R == 0 && cell.Bg.G == 0 && cell.Bg.B == 0) ||
                           (owner.IsLightTerminal && cell.Bg.R < 45 && cell.Bg.G < 45 && cell.Bg.B < 55) ||
                           (!owner.IsLightTerminal && cell.Bg.R > 235 && cell.Bg.G > 235 && cell.Bg.B > 235);
            bool isDefFg = (cell.Flags & TermCell.FLAG_DEFAULT_FG) != 0 ||
                           (owner.IsLightTerminal && cell.Fg.R > 195 && cell.Fg.G > 195 && cell.Fg.B > 195) ||
                           (!owner.IsLightTerminal && cell.Fg.R < 50 && cell.Fg.G < 50 && cell.Fg.B < 55 && (cell.Fg.R + cell.Fg.G + cell.Fg.B) > 0);

            fg = isDefFg ? owner.TermFgColor : AvaloniaColorHelper.ToAvColor(cell.Fg);
            Color bg = isDefBg ? owner.TermBgColor : AvaloniaColorHelper.ToAvColor(cell.Bg);

            if (owner.IsLightTerminal && !isDefFg)
            {
                if (fg.R > 200 && fg.G > 180 && fg.B < 100) fg = Color.FromRgb(148, 104, 0);
                else if (fg.R < 80 && fg.G > 180 && fg.B > 180) fg = Color.FromRgb(5, 122, 155);
                else if (fg.R < 80 && fg.G > 180 && fg.B < 150) fg = Color.FromRgb(16, 124, 65);
            }

            if (inverse)
            {
                var tmp = fg; fg = bg; bg = tmp;
            }
            if ((cell.Flags & TermCell.FLAG_DIM) != 0)
            {
                fg = Color.FromArgb(165, fg.R, fg.G, fg.B);
            }
            if (selected)
            {
                bg = selHighlightCol;
            }

            bold = (cell.Flags & TermCell.FLAG_BOLD) != 0;
            italic = (cell.Flags & TermCell.FLAG_ITALIC) != 0;
            underline = (cell.Flags & TermCell.FLAG_UNDERLINE) != 0;
            return bg;
        }
    }
}
