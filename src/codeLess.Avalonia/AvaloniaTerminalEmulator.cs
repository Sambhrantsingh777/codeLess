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

        /// <summary>Scrollback scrollbar of the grid (tests / screenshots).</summary>
        public SlimScrollbar ScrollbackScrollbar => gridCanvas.ScrollbackScrollbar;
        /// <summary>Rows scrolled back from the live screen.</summary>
        public int ScrollbackOffset => gridCanvas.ScrollbackOffset;
        /// <summary>A left click on the scrollback scrollbar at a grid point (tests); false when not on it.</summary>
        public bool ClickScrollbackScrollbarAt(Point pt) => gridCanvas.ClickScrollbarAt(pt);

        /// <summary>Lays out and paints the grid once at the given size (tests).</summary>
        public void RenderGridForTest(double w, double h)
        {
            gridCanvas.Measure(new Size(w, h));
            gridCanvas.Arrange(new Rect(0, 0, w, h));
            var target = new global::Avalonia.Media.Imaging.RenderTargetBitmap(new PixelSize(10, 10));
            using (var ctx = target.CreateDrawingContext())
                gridCanvas.Render(ctx);
        }

        // PTY reader threads raise output/exit events: post UI work in the control's own (UI) execution context.
        private readonly UiPost uiPost = UiPost.CaptureCurrent();

        public AvaloniaTerminalEmulatorControl()
        {
            Focusable = true;
            ClipToBounds = true;
            FallbackBuffer = new Vt100TerminalBuffer(100, 26, false);

            gridCanvas = new AvaloniaTerminalGridCanvas(this);
            Children.Add(gridCanvas);
            // the cursor on its own layer: blinking repaints a cell, not the grid
            Children.Add(gridCanvas.CursorLayer);

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

            // xterm.js blink interval; only the cursor layer repaints on a tick
            cursorTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(TerminalOptions.BlinkIntervalMs) };
            cursorTimer.Tick += (s, e) =>
            {
                cursorBlinkOn = !cursorBlinkOn;
                if (IsVisible) gridCanvas.CursorLayer.InvalidateVisual();
            };
            // Blink only when terminal.integrated.cursorBlinking (off by default, like VS Code) or the application's
            // DECSCUSR asks for it, and only while this terminal has focus in an active window; otherwise the cursor is
            // steady (an outline when unfocused).
            cursorGate = new UiTimerGate(this, cursorTimer, requireFocus: true, wanted: () => !cursorBlinkSuspended && gridCanvas.CursorWantsBlink);
            cursorGate.RunningChanged += () => { cursorBlinkOn = true; gridCanvas.CursorLayer.InvalidateVisual(); };
            AddHandler(KeyDownEvent, (s, e) =>
            {
                cursorBlinkOn = true; // typing keeps the cursor solid; restart the blink interval
                if (cursorTimer.IsEnabled) { cursorTimer.Stop(); cursorTimer.Start(); }
                gridCanvas.CursorLayer.InvalidateVisual();
            }, global::Avalonia.Interactivity.RoutingStrategies.Tunnel, true);
            // focus changes switch between the filled and the outline cursor
            GotFocus += (s, e) => gridCanvas.CursorLayer.InvalidateVisual();
            LostFocus += (s, e) => gridCanvas.CursorLayer.InvalidateVisual();
        }

        private readonly UiTimerGate cursorGate;
        private bool cursorBlinkSuspended = false;

        public bool IsInlineAiBarVisible => inlineAiBar.IsVisible;
        public bool CursorBlinkOn => cursorBlinkOn;
        public bool IsCursorBlinkTimerRunning => cursorTimer.IsEnabled;

        /// <summary>The application changed the cursor shape (DECSCUSR): blinking may start or stop.</summary>
        internal void OnCursorShapeChanged()
        {
            cursorGate?.Update();
            gridCanvas.CursorLayer.InvalidateVisual();
        }

        /// <summary>Terminal settings changed (font, line height, cursor, contrast, smooth scrolling): apply them.</summary>
        public void ApplyTerminalOptions()
        {
            gridCanvas.ApplyTerminalOptions();
            cursorBlinkOn = true;
            cursorGate?.Update();
        }

        // ---- test hooks (the grid canvas and the cursor layer are internal) ----
        /// <summary>Grid repaints requested so far.</summary>
        public long GridInvalidateCountForTest => gridCanvas.GridInvalidateCount;
        /// <summary>Grid paints so far.</summary>
        public long GridRenderCountForTest => gridCanvas.GridRenderCount;
        /// <summary>Repaints the PTY output asked for (at most one per painted frame).</summary>
        public long OutputRepaintRequestsForTest => gridCanvas.RepaintGate.RepaintRequests;
        /// <summary>Rows shaped by the last grid paint (row cache misses).</summary>
        public int LastFrameRowsBuiltForTest => gridCanvas.LastFrameRowsBuilt;
        /// <summary>True when the last paint kept the previous frame (DEC 2026 synchronized output).</summary>
        public bool LastFrameHeldBySyncOutputForTest => gridCanvas.LastFrameHeldBySyncOutput;
        /// <summary>Exact scroll position (rows back, fractional while scrolling).</summary>
        public double ExactScrollOffset => gridCanvas.ExactScrollOffset;
        public bool IsScrollAnimatingForTest => gridCanvas.ScrollModel.IsAnimating;
        /// <summary>Cell size of the grid.</summary>
        public double CellWidthForTest => gridCanvas.CellW;
        public double CellHeightForTest => gridCanvas.CellH;

        /// <summary>A wheel / trackpad delta on the scrollback (Avalonia units: a notch = 1).</summary>
        public void WheelForTest(double delta, bool alt = false) => gridCanvas.ScrollbackWheel(delta, alt);
        /// <summary>Fake clock for the smooth-scroll animation.</summary>
        public void SetAnimationClockForTest(Func<double> clock) => gridCanvas.AnimationClock = clock;
        public void AdvanceScrollAnimationForTest() => gridCanvas.AdvanceScrollAnimationForTest();

        /// <summary>One tick of the blink timer: repaints the cursor layer only.</summary>
        public void TickCursorBlinkForTest()
        {
            cursorBlinkOn = !cursorBlinkOn;
            gridCanvas.CursorLayer.InvalidateVisual();
        }

        /// <summary>Paints the cursor layer once; returns the style painted (null = hidden) and its rectangle.</summary>
        public string RenderCursorLayerForTest(out Rect rect)
        {
            var layer = gridCanvas.CursorLayer;
            var target = new global::Avalonia.Media.Imaging.RenderTargetBitmap(new PixelSize(10, 10));
            using (var ctx = target.CreateDrawingContext())
                layer.Render(ctx);
            rect = layer.LastRect;
            return layer.LastStyle;
        }

        public long CursorLayerRenderCountForTest => gridCanvas.CursorLayer.RenderCount;

        /// <summary>Simulates the PTY reader thread reporting output (repaint coalescing).</summary>
        public void SimulatePtyOutputForTest() => OnPtyOutput();

        private void OnPtyOutput()
        {
            gridCanvas.OnPtyOutput(uiPost, () =>
            {
                if (OutputReceived != null)
                    OutputReceived.Invoke(GetVisibleScreenText());
            });
        }
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
                    uiPost.Post(() =>
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

        /// <summary>Theme used for the scrollback scrollbar colors.</summary>
        public StudioTheme CurrentTheme { get; private set; } = StudioTheme.GetById(StudioTheme.DefaultThemeId);

        public void ApplyTheme(StudioTheme theme)
        {
            if (theme == null) return;
            CurrentTheme = theme;
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
            gridCanvas.InvalidateRenderCaches();
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


        public static bool IsDesktopRuntime =>
            Application.Current?.ApplicationLifetime is global::Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime;

        public bool StartShell(string workingDir, string customShellCommand = null) => StartShell(workingDir, customShellCommand, null);

        /// <summary>Starts the shell with <paramref name="hooks"/> attached before the process runs (tasks).</summary>
        public bool StartShell(string workingDir, string customShellCommand, PtyStartHooks hooks)
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
                Session = new ConPtyTerminalSession(ShellDisplayName, resolvedCmd, WorkingDirectory, cols, rows, IsLightTerminal, hooks);
                // The session clamps its initial grid (min 20x6); if the laid-out canvas is smaller, sync the
                // PTY winsize to what is actually rendered, since no further SizeChanged will arrive.
                if (gridCanvas.Bounds.Height > 1)
                    Session.Resize(gridCanvas.CalcColumns(), gridCanvas.CalcRows());
                // at most one repaint per painted frame, however many chunks the reader delivers (TerminalRepaintGate)
                Session.OutputReceived += OnPtyOutput;
                Session.ProcessExited += code =>
                {
                    uiPost.Post(() =>
                    {
                        if (!exitedNotified)
                        {
                            exitedNotified = true;
                            SessionExited?.Invoke(this);
                        }
                    });
                };
                gridCanvas.InvalidateVisual();
                // A command that already finished (e.g. a task failing immediately) was still started: returning
                // false made the task runner run it a second time through pipes. Report the missed exit instead.
                if (Session.HasExited && !exitedNotified)
                    uiPost.Post(() => { if (!exitedNotified) { exitedNotified = true; SessionExited?.Invoke(this); } });
                return true;
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
            gridCanvas.ScrollToBottom();
            gridCanvas.InvalidateVisual();
        }

        public string GetSelectedText() => gridCanvas.GetSelectedText();
        public void SelectAll() => gridCanvas.SelectAll();
        /// <summary>Copy the selection to the clipboard (Edit → Copy while the terminal has focus).</summary>
        public void CopySelection() => gridCanvas.CopySelectionToClipboard();
        /// <summary>Paste the clipboard into the shell (Edit → Paste while the terminal has focus).</summary>
        public System.Threading.Tasks.Task PasteFromClipboardAsync() => gridCanvas.PasteFromClipboardPublicAsync();
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

    internal partial class AvaloniaTerminalGridCanvas : Control
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
        public const double PadLeft = 14.0;
        public const double PadTop = 6.0;
        internal AvaloniaTerminalEmulatorControl Owner => owner;

        private int scrollbackOffset = 0;
        private bool isSelecting = false;
        // Selection in absolute rows (Vt100TerminalBuffer.ScrollbackPushCount based): it moves with the text when the
        // output scrolls or the user scrolls back, like xterm.js.
        private bool hasSelAnchor;
        private long selStartAbs, selEndAbs;
        private int selStartCol = -1, selEndCol = -1;

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
            ApplySize();
            InvalidateVisual();
        }

        private DispatcherTimer resizeColsTimer;

        /// <summary>
        /// Resizes the buffer / PTY to the laid-out grid like VS Code: nothing while collapsed (a hidden panel used to
        /// reflow the terminal to 100x24 and back, and the shell redrew its prompt each time); rows at once; with a large
        /// buffer column changes are debounced 100 ms (TerminalResizePolicy) so dragging a sash stays smooth.
        /// </summary>
        internal void ApplySize()
        {
            if (Bounds.Width < 2 || Bounds.Height < 2) return;
            int c = CalcColumns();
            int r = CalcRows();
            var buf = owner.ActiveBuffer;
            if (buf == null) return;
            if (TerminalResizePolicy.ShouldDebounceColumns(buf.ScrollbackCount + buf.Rows, buf.Cols, c) && AvaloniaTerminalEmulatorControl.IsDesktopRuntime)
            {
                ResizeTo(buf.Cols, r);
                if (resizeColsTimer == null)
                {
                    resizeColsTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(TerminalResizePolicy.DebounceColumnsMs) };
                    resizeColsTimer.Tick += (s, e) =>
                    {
                        resizeColsTimer.Stop();
                        if (Bounds.Width >= 2 && Bounds.Height >= 2) { ResizeTo(CalcColumns(), CalcRows()); InvalidateVisual(); }
                    };
                }
                resizeColsTimer.Stop();
                resizeColsTimer.Start();
                return;
            }
            resizeColsTimer?.Stop();
            ResizeTo(c, r);
        }

        private void ResizeTo(int c, int r)
        {
            if (owner.Session != null) owner.Session.Resize(c, r);
            else owner.FallbackBuffer?.Resize(c, r);
        }

        // Word/line/select-all selections are real even when they cover a single cell (a plain click is not).
        private bool explicitSelection;

        public bool HasSelection()
        {
            return hasSelAnchor && (explicitSelection || selStartAbs != selEndAbs || selStartCol != selEndCol);
        }

        public void ClearSelection()
        {
            hasSelAnchor = false;
            selStartCol = selEndCol = -1;
            explicitSelection = false;
            isSelecting = false;
            InvalidateVisual();
        }

        public void SelectAll()
        {
            var buf = owner.ActiveBuffer;
            if (buf == null) return;
            // the whole buffer (scrollback and screen), like VS Code's Select All
            buf.GetAbsoluteRange(out long first, out long last);
            hasSelAnchor = true;
            selStartAbs = first;
            selStartCol = 0;
            selEndAbs = last;
            selEndCol = buf.Cols - 1;
            explicitSelection = true;
            InvalidateVisual();
        }

        public string GetSelectedText()
        {
            var buf = owner.ActiveBuffer;
            if (!HasSelection() || buf == null) return "";
            long r0 = selStartAbs, r1 = selEndAbs;
            int c0 = selStartCol, c1 = selEndCol;
            if (r0 > r1 || (r0 == r1 && c0 > c1))
            {
                long tr = r0; r0 = r1; r1 = tr;
                int tc = c0; c0 = c1; c1 = tc;
            }
            buf.GetAbsoluteRange(out long first, out long last);
            int cols = buf.Cols;
            var sb = new StringBuilder();
            for (long r = Math.Max(first, r0); r <= Math.Min(last, r1); r++)
            {
                int startC = (r == r0) ? Math.Max(0, c0) : 0;
                int endC = (r == r1) ? Math.Min(cols - 1, c1) : (cols - 1);
                var lineSb = new StringBuilder();
                for (int c = startC; c <= endC; c++)
                {
                    char ch = buf.GetAbsoluteCell(r, c).Char;
                    if (ch == '\0')
                    {
                        // '\0' after a wide glyph is its continuation cell, not a space
                        char prev = c > startC ? buf.GetAbsoluteCell(r, c - 1).Char : ' ';
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
            hasSelAnchor = true;
            selStartAbs = selEndAbs = ViewRowToAbs(row);
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
            hasSelAnchor = true;
            selStartAbs = selEndAbs = ViewRowToAbs(row);
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
            scrollbar.UpdateHover(null);
            InvalidateVisual();
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
            if (scrollbar.Contains(pt))
            {
                if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed && scrollbar.Press(pt, out double sbOff))
                {
                    ScrollbackFromSlider(sbOff);
                    e.Pointer.Capture(this);
                }
                e.Handled = true;
                return;
            }
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
                else if (shift && hasSelAnchor)
                {
                    // Shift+Click extends the current selection (VS Code / xterm.js).
                    selEndAbs = ViewRowToAbs(r);
                    selEndCol = c;
                    isSelecting = true;
                    InvalidateVisual();
                }
                else
                {
                    explicitSelection = false;
                    isSelecting = true;
                    hasSelAnchor = true;
                    selStartAbs = selEndAbs = ViewRowToAbs(r);
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
            if (scrollbar.Drag(pt, out double sbOff)) { ScrollbackFromSlider(sbOff); return; }
            if (scrollbar.UpdateHover(pt)) InvalidateVisual();
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
            selEndAbs = ViewRowToAbs(r);
            InvalidateVisual();
        }

        protected override void OnPointerReleased(PointerReleasedEventArgs e)
        {
            // A release that belongs to a click forwarded to a mouse-tracking app must not also raise the
            // context-menu gesture (Control.OnPointerReleased raises ContextRequested for unhandled right releases).
            if (activeMouseBtnCode >= 0) e.Handled = true;
            if (scrollbar.Release()) { e.Pointer.Capture(null); e.Handled = true; InvalidateVisual(); }
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

            var pt = e.GetPosition(this);
            bool shift = e.KeyModifiers.HasFlag(KeyModifiers.Shift);
            bool alt = e.KeyModifiers.HasFlag(KeyModifiers.Alt);
            bool ctrl = e.KeyModifiers.HasFlag(KeyModifiers.Control);
            int col1 = Math.Max(1, (int)((pt.X - PadLeft) / CellW) + 1);
            int row1 = Math.Max(1, (int)((pt.Y - PadTop) / CellH) + 1);
            if (buf.EncodeMouseWheelSequence(e.Delta.Y > 0, col1, row1, shift, alt, ctrl) == null)
            {
                // scrollback: exact (fractional) position, rows snap like xterm.js; Alt scrolls 5x (fastScrollSensitivity)
                wheelAccum = 0;
                ScrollbackWheel(e.Delta.Y, alt);
                e.Handled = true;
                return;
            }

            // Mouse-tracking apps / alternate screen: whole lines; fractional trackpad deltas accumulate.
            wheelAccum += e.Delta.Y * 3;
            int whole = (int)wheelAccum;
            if (whole == 0) { e.Handled = true; return; }
            wheelAccum -= whole;
            bool wheelUp = whole > 0;
            int steps = Math.Min(10, Math.Abs(whole));

            string vtWheel = buf.EncodeMouseWheelSequence(wheelUp, col1, row1, shift, alt, ctrl);
            if (vtWheel != null)
            {
                var batch = new StringBuilder(vtWheel.Length * steps);
                for (int i = 0; i < steps; i++) batch.Append(vtWheel);
                owner.WriteAutomatedInput(batch.ToString());
                e.Handled = true;
                return;
            }

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
                SetScrollOffset(Math.Round(scrollModel.Offset) + Math.Max(1, owner.ActiveBuffer.Rows - 1)); // a page, like xterm.js
                e.Handled = true;
                return;
            }
            if (shift && !ctrl && !meta && e.Key == Key.PageDown && owner.ActiveBuffer != null && !owner.ActiveBuffer.IsAlternateScreen)
            {
                SetScrollOffset(Math.Round(scrollModel.Offset) - Math.Max(1, owner.ActiveBuffer.Rows - 1));
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
                    ScrollToBottom();
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
                ScrollToBottom();
                ClearSelection();
                owner.WriteAutomatedInput(vt);
                e.Handled = true;
                if (alt || ctrl) suppressNextTextInput = true;
            }
        }

        /// <summary>Edit → Copy / ⌘C via the menu: copies the selection (and clears it like the keyboard path).</summary>
        public void CopySelectionToClipboard()
        {
            string sel = GetSelectedText();
            if (string.IsNullOrEmpty(sel)) return;
            _ = CopyToClipboardAsync(sel);
            ClearSelection();
        }

        /// <summary>Edit → Paste / ⌘V via the menu (bracketed paste when the app asked for it).</summary>
        public Task PasteFromClipboardPublicAsync() => PasteFromClipboardAsync();

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
                ScrollToBottom();
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
            ScrollToBottom();
            ClearSelection();
            owner.WriteAutomatedInput(clean.ToString());
            e.Handled = true;
        }

        private readonly SlimScrollbar scrollbar = new SlimScrollbar();
        private long lastScrollTicks;
        /// <summary>The scrollback scrollbar (tests).</summary>
        internal SlimScrollbar ScrollbackScrollbar => scrollbar;
        /// <summary>Rows scrolled back from the live screen (tests).</summary>
        internal int ScrollbackOffset => scrollbackOffset;

        /// <summary>Scroll position from the slider: offset (px from the top of the scrollback) to rows back from the bottom.</summary>
        private void ScrollbackFromSlider(double offsetPx)
        {
            var buf = owner.ActiveBuffer;
            if (buf == null) return;
            int sb = buf.ScrollbackCount;
            SetScrollOffset(sb - offsetPx / Math.Max(1, CellH)); // exact: the thumb follows the pointer, rows snap
        }

        /// <summary>Simulated click on the scrollback scrollbar (tests): true when the point is on it.</summary>
        internal bool ClickScrollbarAt(Point pt)
        {
            if (!scrollbar.Press(pt, out double off)) return false;
            ScrollbackFromSlider(off);
            scrollbar.Release();
            return true;
        }
    }
}
