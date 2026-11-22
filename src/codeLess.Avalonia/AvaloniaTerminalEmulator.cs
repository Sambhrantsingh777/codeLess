using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
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
            // Blink only while this terminal has focus in an active window; otherwise a steady hollow cursor (VS Code).
            cursorGate = new UiTimerGate(this, cursorTimer, requireFocus: true, wanted: () => !cursorBlinkSuspended);
            cursorGate.RunningChanged += () => { cursorBlinkOn = true; gridCanvas.InvalidateVisual(); };
            AddHandler(KeyDownEvent, (s, e) =>
            {
                cursorBlinkOn = true; // typing keeps the cursor solid; restart the blink interval
                if (cursorTimer.IsEnabled) { cursorTimer.Stop(); cursorTimer.Start(); }
            }, global::Avalonia.Interactivity.RoutingStrategies.Tunnel, true);
        }

        private readonly UiTimerGate cursorGate;
        private bool cursorBlinkSuspended = false;

        public bool IsInlineAiBarVisible => inlineAiBar.IsVisible;
        public bool CursorBlinkOn => cursorBlinkOn;
        public bool IsCursorBlinkTimerRunning => cursorTimer.IsEnabled;
        /// <summary>False when the terminal is unfocused or its window is inactive: the cursor is drawn as a hollow box.</summary>
        public bool CursorHasFocus => cursorGate.HasActiveFocus;

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
            cursorBlinkSuspended = false;
            cursorGate.Update();
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
            cursorBlinkSuspended = true;
            if (cursorGate != null) cursorGate.Update();
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
            // Text goes to the PTY verbatim. (Typing a line that read "exit" used to kill the whole session even
            // inside vim / nano / ssh / a REPL; session end is now driven only by the real process exit.)
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

        // ---- Links (Cmd+Click on macOS, Ctrl+Click on Windows/Linux), like VS Code's terminal ----

        /// <summary>Workspace root used (after the shell's live cwd) to resolve relative file links.</summary>
        public Func<string> WorkspaceRootProvider { get; set; }

        /// <summary>Cmd/Ctrl+Click on a file link: absolute path, 1-based line (0 = none), 1-based column (0 = none).</summary>
        public event Action<string, int, int> FileLinkActivated;

        /// <summary>Opens http(s) links. Defaults to the system browser; replaceable (tests, embedding).</summary>
        public Action<string> UrlOpener { get; set; }

        /// <summary>Link under a viewport cell (row/col 0-based), or null. Only existing files count as file links.</summary>
        public TerminalLinkMatch FindLinkAtCell(int viewportRow, int col) => gridCanvas.FindLinkAtCell(viewportRow, col);

        /// <summary>Selects the word at a viewport cell using VS Code's word separators (double-click).</summary>
        public bool SelectWordAtCell(int viewportRow, int col) => gridCanvas.SelectWordAt(viewportRow, col);

        /// <summary>Selects a whole viewport row (triple-click).</summary>
        public void SelectLineAtCell(int viewportRow) => gridCanvas.SelectLineAt(viewportRow);

        internal List<string> GetLinkBaseDirectories()
        {
            string live = null;
            try { live = Session?.GetLiveWorkingDirectory(); } catch { }
            string root = null;
            try { root = WorkspaceRootProvider?.Invoke(); } catch { }
            return TerminalLinkDetector.BuildBaseDirectories(live, Session?.WorkingDirectory ?? WorkingDirectory, root);
        }

        public void ActivateLink(TerminalLinkMatch link)
        {
            if (link == null) return;
            if (link.IsUrl)
            {
                (UrlOpener ?? OpenUrlInDefaultBrowser)(link.Url);
                return;
            }
            if (!string.IsNullOrEmpty(link.FilePath))
                FileLinkActivated?.Invoke(link.FilePath, link.Line, link.Column);
        }

        public static void OpenUrlInDefaultBrowser(string url)
        {
            if (string.IsNullOrEmpty(url)) return;
            if (!url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) && !url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                return;
            try
            {
                if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                {
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true });
                    return;
                }
                var psi = new System.Diagnostics.ProcessStartInfo(RuntimeInformation.IsOSPlatform(OSPlatform.OSX) ? "open" : "xdg-open")
                {
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                psi.ArgumentList.Add(url);
                System.Diagnostics.Process.Start(psi);
            }
            catch { }
        }
    }

    /// <summary>Input-method bridge for the terminal (composition drawn at the cursor cell, committed text via OnTextInput).</summary>
    internal sealed class TerminalImeClient : global::Avalonia.Input.TextInput.TextInputMethodClient
    {
        private readonly AvaloniaTerminalGridCanvas canvas;
        public TerminalImeClient(AvaloniaTerminalGridCanvas canvas) { this.canvas = canvas; }
        public override Visual TextViewVisual => canvas;
        public override bool SupportsPreedit => true;
        public override bool SupportsSurroundingText => false;
        public override string SurroundingText => "";
        public override Rect CursorRectangle => canvas.ImeCaretRect;
        public override global::Avalonia.Input.TextInput.TextSelection Selection
        {
            get => new global::Avalonia.Input.TextInput.TextSelection(0, 0);
            set { }
        }
        public override void SetPreeditText(string preeditText) => canvas.SetImePreedit(preeditText);
        public override void SetPreeditText(string preeditText, int? cursorPos) => canvas.SetImePreedit(preeditText);
        public void NotifyCaretChanged() => RaiseCursorRectangleChanged();
    }

    internal class AvaloniaTerminalGridCanvas : Control
    {
        private TerminalImeClient imeClient;
        private string imePreedit;
        private Rect imeCaretRect = new Rect(0, 0, 8, 16);
        internal Rect ImeCaretRect => imeCaretRect;
        internal bool IsImeComposing => !string.IsNullOrEmpty(imePreedit);
        internal void SetImePreedit(string text)
        {
            imePreedit = string.IsNullOrEmpty(text) ? null : text;
            InvalidateVisual();
        }

        private readonly AvaloniaTerminalEmulatorControl owner;
        public const double DefaultCellW = 7.6;
        public const double CellH = 17.0;
        public const double PadLeft = 14.0;
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
        // Each entry pins a native glyph run; 4096 cost ~10 MB per terminal. (CODELESS_TERM_TEXTCACHE overrides, for benchmarking.)
        private static readonly int MaxTextCacheEntries =
            int.TryParse(Environment.GetEnvironmentVariable("CODELESS_TERM_TEXTCACHE"), out int tc) && tc >= 64 ? tc : 1024;
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

        // Pointer cursors are created once (platform cursor objects); creation can fail on headless/odd platforms.
        private static Cursor ibeamCursor, handCursor;
        private static bool cursorsCreated;
        private static void EnsureCursors()
        {
            if (cursorsCreated) return;
            cursorsCreated = true;
            try { ibeamCursor = new Cursor(StandardCursorType.Ibeam); } catch { ibeamCursor = null; }
            try { handCursor = new Cursor(StandardCursorType.Hand); } catch { handCursor = null; }
        }

        // Hovered link (shown while Cmd/Ctrl is held): viewport row, the row text it was detected in, and the match.
        private TerminalLinkMatch hoverLink;
        private int hoverRow = -1;
        private string hoverLineText;
        private Point? lastPointerPos;
        internal TerminalLinkMatch HoveredLink => hoverLink;

        public AvaloniaTerminalGridCanvas(AvaloniaTerminalEmulatorControl owner)
        {
            this.owner = owner;
            Focusable = true;
            ClipToBounds = true;
            EnsureCursors();
            if (ibeamCursor != null) Cursor = ibeamCursor;
            imeClient = new TerminalImeClient(this);
            TextInputMethodClientRequested += (s, e) => e.Client = imeClient;
            global::Avalonia.Input.InputMethod.SetIsInputMethodEnabled(this, true);
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

        // Word/line/select-all selections are real even when they cover a single cell (a plain click is not).
        private bool explicitSelection;

        public bool HasSelection()
        {
            return selStartRow >= 0 && selEndRow >= 0 && (explicitSelection || selStartRow != selEndRow || selStartCol != selEndCol);
        }

        public void ClearSelection()
        {
            selStartRow = selStartCol = selEndRow = selEndCol = -1;
            explicitSelection = false;
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
            explicitSelection = true;
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
                    if (ch == '\0')
                    {
                        // '\0' after a wide glyph is its continuation cell, not a space
                        char prev = c > startC ? buf.GetViewportCell(r, c - 1, scrollbackOffset).Char : ' ';
                        if (prev != '\0' && prev != ' ' && (Vt100TerminalBuffer.IsWideBmp(prev) || char.IsLowSurrogate(prev))) continue;
                        ch = ' ';
                    }
                    lineSb.Append(ch);
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

        /// <summary>Text of a viewport row, one character per cell (continuation/empty cells as spaces).</summary>
        internal string GetViewportRowText(int row)
        {
            var buf = owner.ActiveBuffer;
            if (buf == null || row < 0 || row >= buf.Rows) return "";
            int cols = buf.Cols;
            var sb = new StringBuilder(cols);
            for (int c = 0; c < cols; c++)
            {
                char ch = buf.GetViewportCell(row, c, scrollbackOffset).Char;
                sb.Append(ch == '\0' ? ' ' : ch);
            }
            return sb.ToString().TrimEnd();
        }

        internal static bool IsLinkModifier(KeyModifiers mods) =>
            MacKeymap.IsMacOS ? mods.HasFlag(KeyModifiers.Meta) : mods.HasFlag(KeyModifiers.Control);

        private static bool IsLinkModifierKey(Key key) =>
            MacKeymap.IsMacOS ? (key == Key.LWin || key == Key.RWin) : (key == Key.LeftCtrl || key == Key.RightCtrl);

        private void PointToCell(Point pt, out int row, out int col)
        {
            col = Math.Max(0, (int)((pt.X - PadLeft) / CellW));
            row = Math.Max(0, (int)((pt.Y - PadTop) / CellH));
        }

        public TerminalLinkMatch FindLinkAtCell(int row, int col)
        {
            var buf = owner.ActiveBuffer;
            if (buf == null || row < 0 || row >= buf.Rows || col < 0) return null;
            string line = GetViewportRowText(row);
            if (col >= line.Length) return null;
            try { return TerminalLinkDetector.FindLinkAt(line, col, owner.GetLinkBaseDirectories()); }
            catch { return null; }
        }

        /// <summary>Re-evaluates the hovered link: shown (underline + hand) only while the link modifier is held.</summary>
        private void UpdateHoverLink(Point pt, bool modifierHeld)
        {
            if (!modifierHeld || isSelecting || owner.ActiveBuffer == null)
            {
                ClearHoverLink();
                return;
            }
            PointToCell(pt, out int r, out int c);
            if (hoverLink != null && r == hoverRow && c >= hoverLink.StartCol && c < hoverLink.EndColExclusive)
            {
                if (string.Equals(GetViewportRowText(r), hoverLineText, StringComparison.Ordinal)) return;
            }
            string line = GetViewportRowText(r);
            TerminalLinkMatch link = null;
            if (c < line.Length)
            {
                try { link = TerminalLinkDetector.FindLinkAt(line, c, owner.GetLinkBaseDirectories()); }
                catch { link = null; }
            }
            if (link == null)
            {
                ClearHoverLink();
                return;
            }
            hoverLink = link;
            hoverRow = r;
            hoverLineText = line;
            if (handCursor != null) Cursor = handCursor;
            InvalidateVisual();
        }

        private void ClearHoverLink()
        {
            if (hoverLink == null) return;
            hoverLink = null;
            hoverRow = -1;
            hoverLineText = null;
            if (ibeamCursor != null) Cursor = ibeamCursor;
            InvalidateVisual();
        }

        public bool SelectWordAt(int row, int col)
        {
            string line = GetViewportRowText(row);
            if (!TerminalLinkDetector.GetWordBounds(line, col, out int start, out int end)) return false;
            selStartRow = selEndRow = row;
            selStartCol = start;
            selEndCol = end;
            explicitSelection = true;
            isSelecting = false;
            InvalidateVisual();
            return true;
        }

        public void SelectLineAt(int row)
        {
            var buf = owner.ActiveBuffer;
            if (buf == null || row < 0 || row >= buf.Rows) return;
            // Whole row like VS Code; GetSelectedText trims the trailing blanks.
            selStartRow = selEndRow = row;
            selStartCol = 0;
            selEndCol = buf.Cols - 1;
            explicitSelection = true;
            isSelecting = false;
            InvalidateVisual();
        }

        private void ShowContextMenu()
        {
            string copyKey = MacKeymap.IsMacOS ? "Cmd+C" : "Ctrl+Shift+C";
            string pasteKey = MacKeymap.IsMacOS ? "Cmd+V" : "Ctrl+Shift+V";
            var copy = new MenuItem { Header = "Copy   (" + copyKey + ")", IsEnabled = HasSelection() };
            copy.Click += (s, e) =>
            {
                string sel = GetSelectedText();
                if (!string.IsNullOrEmpty(sel)) _ = CopyToClipboardAsync(sel);
            };
            var paste = new MenuItem { Header = "Paste   (" + pasteKey + ")" };
            paste.Click += (s, e) => _ = PasteFromClipboardAsync();
            var selectAll = new MenuItem { Header = "Select All" };
            selectAll.Click += (s, e) => SelectAll();
            var clear = new MenuItem { Header = "Clear Terminal" };
            clear.Click += (s, e) => owner.ClearTerminal();
            var menu = new ContextMenu { ItemsSource = new object[] { copy, paste, new Separator(), selectAll, clear } };
            try { menu.Open(this); } catch { }
        }

        protected override void OnPointerExited(PointerEventArgs e)
        {
            base.OnPointerExited(e);
            lastPointerPos = null;
            ClearHoverLink();
        }

        protected override void OnKeyUp(KeyEventArgs e)
        {
            base.OnKeyUp(e);
            if (IsLinkModifierKey(e.Key)) ClearHoverLink();
        }

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

            // Cmd/Ctrl+Click opens a link. Checked before mouse-tracking forwarding so links work inside TUIs too,
            // but only when a link is actually under the pointer (otherwise the click goes to the app as before).
            if (props.IsLeftButtonPressed && IsLinkModifier(e.KeyModifiers))
            {
                var link = FindLinkAtCell(r, c);
                if (link != null)
                {
                    ClearHoverLink();
                    owner.ActivateLink(link);
                    e.Handled = true;
                    return;
                }
            }

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
                if (e.ClickCount == 2)
                {
                    if (!SelectWordAt(r, c)) ClearSelection();
                }
                else if (e.ClickCount >= 3)
                {
                    SelectLineAt(r);
                }
                else if (shift && selStartRow >= 0)
                {
                    // Shift+Click extends the current selection (VS Code / xterm.js).
                    selEndRow = r;
                    selEndCol = c;
                    isSelecting = true;
                    InvalidateVisual();
                }
                else
                {
                    explicitSelection = false;
                    isSelecting = true;
                    selStartRow = selEndRow = r;
                    selStartCol = selEndCol = c;
                    InvalidateVisual();
                }
            }
            else if (props.IsRightButtonPressed)
            {
                ShowContextMenu();
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
            lastPointerPos = pt;
            UpdateHoverLink(pt, IsLinkModifier(e.KeyModifiers));

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
            // A release that belongs to a click forwarded to a mouse-tracking app must not also raise the
            // context-menu gesture (Control.OnPointerReleased raises ContextRequested for unhandled right releases).
            if (activeMouseBtnCode >= 0) e.Handled = true;
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

            // Accumulate fractional trackpad deltas: forcing >= 1 line per tiny event made scrollback fly past.
            wheelAccum += e.Delta.Y * 3;
            int whole = (int)wheelAccum;
            if (whole == 0) { e.Handled = true; return; }
            wheelAccum -= whole;
            bool wheelUp = whole > 0;
            int steps = Math.Min(10, Math.Abs(whole));
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
            ClearHoverLink();
            if (lastPointerPos.HasValue) UpdateHoverLink(lastPointerPos.Value, IsLinkModifier(e.KeyModifiers));
            InvalidateVisual();
            e.Handled = true;
        }

        private double wheelAccum;
        private bool suppressNextTextInput;

        protected override void OnKeyDown(KeyEventArgs e)
        {
            // A new physical key press always clears stale suppression: Ctrl+C etc. produce no TextInput, and a
            // dangling flag swallowed the next typed character.
            suppressNextTextInput = false;
            base.OnKeyDown(e);
            // Pressing Cmd/Ctrl while the pointer rests over a link reveals it without having to move the mouse.
            if (IsLinkModifierKey(e.Key))
            {
                if (lastPointerPos.HasValue) UpdateHoverLink(lastPointerPos.Value, true);
                return;
            }
            if (IsImeComposing) return; // Enter / Backspace / arrows belong to the input method while composing
            bool isMac = MacKeymap.IsMacOS;
            bool ctrl = e.KeyModifiers.HasFlag(KeyModifiers.Control);
            bool meta = e.KeyModifiers.HasFlag(KeyModifiers.Meta);
            bool shift = e.KeyModifiers.HasFlag(KeyModifiers.Shift);
            bool alt = e.KeyModifiers.HasFlag(KeyModifiers.Alt);
            bool primary = isMac ? meta : ctrl;

            // Inline AI command bar: Cmd+I / Ctrl+I (VS Code terminal inline chat). Ctrl+K stays a shell key.
            if (primary && !shift && !alt && e.Key == Key.I)
            {
                owner.ShowInlineAiBar();
                e.Handled = true;
                return;
            }
            // Cmd+K clears the terminal on macOS (VS Code / Terminal.app).
            if (isMac && meta && !shift && !alt && e.Key == Key.K)
            {
                owner.ClearTerminal();
                e.Handled = true;
                return;
            }
            if (isMac && meta && !shift && e.Key == Key.A)
            {
                SelectAll();
                e.Handled = true;
                return;
            }

            // Copy: Cmd+C on macOS; Ctrl+Shift+C, or Ctrl+C with an active selection, on Windows/Linux.
            // On macOS plain Ctrl+C always sends SIGINT, even with a selection.
            bool copy = isMac
                ? (meta && !shift && e.Key == Key.C)
                : (ctrl && e.Key == Key.C && (shift || HasSelection()));
            if (copy)
            {
                e.Handled = true; // set before any await: routing completes at the first yield
                string sel = GetSelectedText();
                if (!string.IsNullOrEmpty(sel))
                {
                    _ = CopyToClipboardAsync(sel);
                    ClearSelection();
                }
                return;
            }

            bool paste = isMac
                ? (meta && !shift && e.Key == Key.V)
                : ((ctrl && shift && e.Key == Key.V) || (shift && e.Key == Key.Insert));
            if (paste)
            {
                e.Handled = true;
                _ = PasteFromClipboardAsync();
                return;
            }

            if (shift && !ctrl && !meta && e.Key == Key.PageUp && owner.ActiveBuffer != null && !owner.ActiveBuffer.IsAlternateScreen)
            {
                scrollbackOffset = Math.Min(owner.ActiveBuffer.ScrollbackCount, scrollbackOffset + 10);
                InvalidateVisual();
                e.Handled = true;
                return;
            }
            if (shift && !ctrl && !meta && e.Key == Key.PageDown && owner.ActiveBuffer != null && !owner.ActiveBuffer.IsAlternateScreen)
            {
                scrollbackOffset = Math.Max(0, scrollbackOffset - 10);
                InvalidateVisual();
                e.Handled = true;
                return;
            }

            // Linux/Windows: Alt+printable is "Meta" (ESC prefix) like xterm.js; macOS Option composes characters.
            if (!isMac && alt && !ctrl && !meta)
            {
                string ch = PrintableForKey(e.Key, shift);
                if (ch != null)
                {
                    suppressNextTextInput = true;
                    scrollbackOffset = 0;
                    ClearSelection();
                    owner.WriteAutomatedInput("\x1b" + ch);
                    e.Handled = true;
                    return;
                }
            }

            bool appCursor = owner.ActiveBuffer?.ApplicationCursorKeys ?? false;
            string vt = TranslateKeyToVtSequence(e.Key, e.KeyModifiers, appCursor, isMac);
            if (!string.IsNullOrEmpty(vt))
            {
                scrollbackOffset = 0;
                ClearSelection();
                owner.WriteAutomatedInput(vt);
                e.Handled = true;
                if (alt || ctrl) suppressNextTextInput = true;
            }
        }

        private async Task CopyToClipboardAsync(string text)
        {
            try
            {
                var top = TopLevel.GetTopLevel(this);
                if (top?.Clipboard != null) await top.Clipboard.SetTextAsync(text);
            }
            catch { }
        }

        private async Task PasteFromClipboardAsync()
        {
            try
            {
                var top = TopLevel.GetTopLevel(this);
                if (top?.Clipboard == null) return;
                string clip = await top.Clipboard.GetTextAsync();
                if (string.IsNullOrEmpty(clip)) return;
                scrollbackOffset = 0;
                ClearSelection();
                bool bracketed = owner.ActiveBuffer != null && owner.ActiveBuffer.BracketedPasteMode;
                owner.WriteAutomatedInput(Vt100TerminalBuffer.PreparePasteText(clip, bracketed));
            }
            catch { }
        }

        private static string PrintableForKey(Key key, bool shift)
        {
            if (key >= Key.A && key <= Key.Z)
            {
                char c = (char)('a' + (key - Key.A));
                return (shift ? char.ToUpperInvariant(c) : c).ToString();
            }
            if (key >= Key.D0 && key <= Key.D9 && !shift) return ((char)('0' + (key - Key.D0))).ToString();
            switch (key)
            {
                case Key.OemPeriod: return shift ? ">" : ".";
                case Key.OemComma: return shift ? "<" : ",";
                case Key.OemMinus: return shift ? "_" : "-";
                case Key.OemPlus: return shift ? "+" : "=";
                case Key.OemQuestion: return shift ? "?" : "/";
                case Key.OemSemicolon: return shift ? ":" : ";";
                case Key.OemQuotes: return shift ? "\"" : "'";
                case Key.OemOpenBrackets: return shift ? "{" : "[";
                case Key.OemCloseBrackets: return shift ? "}" : "]";
                case Key.OemPipe: return shift ? "|" : "\\";
                case Key.OemTilde: return shift ? "~" : "`";
                case Key.Space: return " ";
                default: return null;
            }
        }

        public static string TranslateKeyToVtSequence(Key key, KeyModifiers mods, bool appCursor)
        {
            return TranslateKeyToVtSequence(key, mods, appCursor, MacKeymap.IsMacOS);
        }

        /// <summary>
        /// Maps a non-text key press to the bytes sent to the PTY (null when the key should arrive via
        /// OnTextInput). Follows xterm.js: modifier-encoded cursor/edit keys (CSI 1;m X), DECCKM-aware
        /// Home/End, F1-F12, Insert, Ctrl+punctuation control codes, and macOS line/word editing for
        /// Cmd/Option + arrows/backspace. Shift/Option+Enter send ESC CR (newline-without-submit for TUIs).
        /// </summary>
        public static string TranslateKeyToVtSequence(Key key, KeyModifiers mods, bool appCursor, bool isMac)
        {
            bool shift = mods.HasFlag(KeyModifiers.Shift);
            bool alt = mods.HasFlag(KeyModifiers.Alt);
            bool ctrl = mods.HasFlag(KeyModifiers.Control);
            bool meta = mods.HasFlag(KeyModifiers.Meta);
            int modParam = 1 + (shift ? 1 : 0) + (alt ? 2 : 0) + (ctrl ? 4 : 0);

            if (isMac)
            {
                // macOS text-editing conventions (Terminal.app / iTerm2 / VS Code)
                if (meta && !ctrl)
                {
                    switch (key)
                    {
                        case Key.Left: return "\x01";      // line start (Ctrl+A)
                        case Key.Right: return "\x05";     // line end (Ctrl+E)
                        case Key.Back: return "\x15";      // delete to line start (Ctrl+U)
                        case Key.Delete: return "\x0b";    // delete to line end (Ctrl+K)
                        default: return null;            // other Cmd combos are app shortcuts, never shell input
                    }
                }
                if (alt && !ctrl && !shift)
                {
                    if (key == Key.Left) return "\u001bb";   // word left
                    if (key == Key.Right) return "\u001bf";  // word right
                    if (key == Key.Back) return "\x1b\x7f"; // delete word left
                    if (key == Key.Delete) return "\u001bd"; // delete word right
                }
            }
            else if (meta)
            {
                return null; // Super/Windows key combos are window-manager shortcuts
            }

            string Csi(string final) => modParam > 1 ? "\x1b[1;" + modParam + final : null;
            string Tilde(int code) => modParam > 1 ? "\x1b[" + code + ";" + modParam + "~" : "\x1b[" + code + "~";

            switch (key)
            {
                case Key.Enter: return (shift || alt) ? "\x1b\r" : "\r";
                case Key.Back: return ctrl ? "\x08" : (alt ? "\x1b\x7f" : "\x7f");
                case Key.Tab: return shift ? "\x1b[Z" : "\t";
                case Key.Escape: return "\x1b";
                case Key.Up: return Csi("A") ?? (appCursor ? "\x1bOA" : "\x1b[A");
                case Key.Down: return Csi("B") ?? (appCursor ? "\x1bOB" : "\x1b[B");
                case Key.Right: return Csi("C") ?? (appCursor ? "\x1bOC" : "\x1b[C");
                case Key.Left: return Csi("D") ?? (appCursor ? "\x1bOD" : "\x1b[D");
                case Key.Home: return Csi("H") ?? (appCursor ? "\x1bOH" : "\x1b[H");
                case Key.End: return Csi("F") ?? (appCursor ? "\x1bOF" : "\x1b[F");
                case Key.Insert: return Tilde(2);
                case Key.Delete: return Tilde(3);
                case Key.PageUp: return Tilde(5);
                case Key.PageDown: return Tilde(6);
                case Key.F1: return Csi("P") ?? "\x1bOP";
                case Key.F2: return Csi("Q") ?? "\x1bOQ";
                case Key.F3: return Csi("R") ?? "\x1bOR";
                case Key.F4: return Csi("S") ?? "\x1bOS";
                case Key.F5: return Tilde(15);
                case Key.F6: return Tilde(17);
                case Key.F7: return Tilde(18);
                case Key.F8: return Tilde(19);
                case Key.F9: return Tilde(20);
                case Key.F10: return Tilde(21);
                case Key.F11: return Tilde(23);
                case Key.F12: return Tilde(24);
            }

            if (ctrl)
            {
                string prefix = alt ? "\x1b" : "";
                if (key >= Key.A && key <= Key.Z) return prefix + ((char)((key - Key.A) + 1)).ToString();
                switch (key)
                {
                    case Key.Space: case Key.D2: return prefix + "\0";
                    case Key.OemOpenBrackets: case Key.D3: return prefix + "\x1b";
                    case Key.OemPipe: case Key.D4: return prefix + "\x1c";
                    case Key.OemCloseBrackets: case Key.D5: return prefix + "\x1d";
                    case Key.D6: return prefix + "\x1e";
                    case Key.OemQuestion: case Key.OemMinus: case Key.D7: return prefix + "\x1f";
                    case Key.D8: return prefix + "\x7f";
                }
            }
            return null;
        }

        protected override void OnTextInput(TextInputEventArgs e)
        {
            base.OnTextInput(e);
            if (string.IsNullOrEmpty(e.Text)) return;
            if (suppressNextTextInput)
            {
                // The key was already encoded in OnKeyDown (Alt-as-Meta, Ctrl combos).
                suppressNextTextInput = false;
                e.Handled = true;
                return;
            }

            // Filter out control characters (Enter, Backspace, Escape, Tab, Ctrl+A..Z) already handled in OnKeyDown
            var clean = new StringBuilder(e.Text.Length);
            foreach (char ch in e.Text)
            {
                if (ch >= 32 && ch != 127)
                    clean.Append(ch);
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

            // Underline the Cmd/Ctrl-hovered link (skipped if the row's text changed under it since detection).
            if (hoverLink != null && hoverRow >= 0 && hoverRow < rows &&
                string.Equals(GetViewportRowText(hoverRow), hoverLineText, StringComparison.Ordinal))
            {
                double ly = PadTop + hoverRow * CellH + textTop + TextLineHeight - 0.5;
                double lx1 = PadLeft + hoverLink.StartCol * cellW;
                double lx2 = PadLeft + Math.Min(cols, hoverLink.EndColExclusive) * cellW;
                context.DrawLine(new Pen(GetBrush(owner.TermFgColor), 1), new Point(lx1, ly), new Point(lx2, ly));
            }

            // IME caret geometry + composition preview at the cursor cell
            {
                double icx = PadLeft + buf.CursorCol * cellW;
                double icy = PadTop + buf.CursorRow * CellH;
                var newRect = new Rect(icx, icy, cellW, CellH);
                if (newRect != imeCaretRect)
                {
                    imeCaretRect = newRect;
                    Dispatcher.UIThread.Post(() => imeClient.NotifyCaretChanged(), DispatcherPriority.Background);
                }
                if (IsImeComposing && scrollbackOffset == 0)
                {
                    var pft = new FormattedText(imePreedit, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, FontRegular, TermFontSize, GetBrush(owner.AccentColor));
                    pft.SetTextDecorations(TextDecorations.Underline);
                    context.FillRectangle(termBgBrush, new Rect(icx, icy, pft.WidthIncludingTrailingWhitespace + 2, CellH));
                    context.DrawText(pft, new Point(icx, icy + textTop));
                }
            }

            // Draw terminal cursor when at bottom of scrollback
            if (scrollbackOffset == 0 && buf.CursorVisible && owner.CursorBlinkOn && !IsImeComposing)
            {
                double cx = PadLeft + buf.CursorCol * cellW;
                double cy = PadTop + buf.CursorRow * CellH;
                // Cursor covers exactly the same vertical box the glyphs are drawn in.
                var cursorRect = new Rect(cx, cy + textTop, cellW, TextLineHeight);
                var cursorBrush = GetBrush(Color.FromArgb(185, owner.AccentColor.R, owner.AccentColor.G, owner.AccentColor.B));
                if (owner.CursorHasFocus)
                    context.FillRectangle(cursorBrush, cursorRect);
                else
                    context.DrawRectangle(new Pen(cursorBrush, 1), cursorRect.Deflate(0.5));
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
