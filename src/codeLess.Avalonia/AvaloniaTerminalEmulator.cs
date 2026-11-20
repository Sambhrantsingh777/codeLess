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

        public bool StartShell(string workingDir, string customShellCommand = null)
        {
            StopShell(notifyExit: false);
            exitedNotified = false;
            WorkingDirectory = !string.IsNullOrEmpty(workingDir) && Directory.Exists(workingDir)
                ? workingDir
                : Environment.CurrentDirectory;

            string resolvedCmd = string.IsNullOrWhiteSpace(customShellCommand)
                ? ResolveDefaultShellCommand()
                : customShellCommand;
            ShellDisplayName = NormalizeShellLabel(resolvedCmd);

            int cols = Math.Max(40, gridCanvas.CalcColumns());
            int rows = Math.Max(10, gridCanvas.CalcRows());

            try
            {
                Session = new ConPtyTerminalSession(ShellDisplayName, resolvedCmd, WorkingDirectory, cols, rows, IsLightTerminal);
                Session.OutputReceived += () =>
                {
                    Dispatcher.UIThread.Post(() =>
                    {
                        gridCanvas.InvalidateVisual();
                        OutputReceived?.Invoke(GetVisibleScreenText());
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
        public const double CellW = 8.0;
        public const double CellH = 17.0;
        public const double PadLeft = 10.0;
        public const double PadTop = 6.0;

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

        public int CalcColumns() => Bounds.Width > 160 ? Math.Max(100, (int)((Bounds.Width - PadLeft * 2 - 8) / CellW)) : 100;
        public int CalcRows() => Bounds.Height > 80 ? Math.Max(10, (int)((Bounds.Height - PadTop * 2) / CellH)) : 24;

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

        protected override void OnPointerPressed(PointerPressedEventArgs e)
        {
            base.OnPointerPressed(e);
            Focus();
            var pt = e.GetPosition(this);
            int c = Math.Max(0, (int)((pt.X - PadLeft) / CellW));
            int r = Math.Max(0, (int)((pt.Y - PadTop) / CellH));
            var props = e.GetCurrentPoint(this).Properties;
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
            if (!isSelecting) return;
            var pt = e.GetPosition(this);
            selEndCol = Math.Max(0, (int)((pt.X - PadLeft) / CellW));
            selEndRow = Math.Max(0, (int)((pt.Y - PadTop) / CellH));
            InvalidateVisual();
        }

        protected override void OnPointerReleased(PointerReleasedEventArgs e)
        {
            base.OnPointerReleased(e);
            isSelecting = false;
        }

        protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
        {
            base.OnPointerWheelChanged(e);
            var buf = owner.ActiveBuffer;
            if (buf == null) return;
            int maxSb = buf.ScrollbackCount;
            scrollbackOffset = Math.Max(0, Math.Min(maxSb, scrollbackOffset + (int)(e.Delta.Y * 3)));
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
                scrollbackOffset = Math.Min(owner.ActiveBuffer.ScrollbackCount, scrollbackOffset + 10);
                InvalidateVisual();
                e.Handled = true;
                return;
            }
            if (shift && e.Key == Key.PageDown)
            {
                scrollbackOffset = Math.Max(0, scrollbackOffset - 10);
                InvalidateVisual();
                e.Handled = true;
                return;
            }

            string vt = null;
            bool appCursor = owner.ActiveBuffer?.ApplicationCursorKeys ?? false;
            switch (e.Key)
            {
                case Key.Enter:
                    if (string.Equals(lineInputTracker.ToString().Trim(), "exit", StringComparison.OrdinalIgnoreCase))
                    {
                        lineInputTracker.Clear();
                        owner.WriteAutomatedInput("exit");
                        e.Handled = true;
                        return;
                    }
                    lineInputTracker.Clear();
                    vt = "\r";
                    break;
                case Key.Back:
                    if (lineInputTracker.Length > 0) lineInputTracker.Length--;
                    vt = "\x7f";
                    break;
                case Key.Tab: vt = shift ? "\x1b[Z" : "\t"; break;
                case Key.Escape: vt = "\x1b"; break;
                case Key.Up: vt = appCursor ? "\x1bOA" : "\x1b[A"; break;
                case Key.Down: vt = appCursor ? "\x1bOB" : "\x1b[B"; break;
                case Key.Right: vt = appCursor ? "\x1bOC" : "\x1b[C"; break;
                case Key.Left: vt = appCursor ? "\x1bOD" : "\x1b[D"; break;
                case Key.Home: vt = "\x1b[H"; break;
                case Key.End: vt = "\x1b[F"; break;
                case Key.Delete: vt = "\x1b[3~"; break;
                case Key.PageUp: vt = "\x1b[5~"; break;
                case Key.PageDown: vt = "\x1b[6~"; break;
                default:
                    if (e.KeyModifiers.HasFlag(KeyModifiers.Control) && !alt && e.Key >= Key.A && e.Key <= Key.Z)
                    {
                        int ctrlCode = (e.Key - Key.A) + 1;
                        vt = ((char)ctrlCode).ToString();
                    }
                    break;
            }

            if (!string.IsNullOrEmpty(vt))
            {
                scrollbackOffset = 0;
                ClearSelection();
                owner.WriteAutomatedInput(vt);
                e.Handled = true;
            }
        }

        protected override void OnTextInput(TextInputEventArgs e)
        {
            base.OnTextInput(e);
            if (string.IsNullOrEmpty(e.Text)) return;
            scrollbackOffset = 0;
            ClearSelection();
            foreach (char ch in e.Text)
            {
                if (ch >= 32) lineInputTracker.Append(ch);
            }
            owner.WriteAutomatedInput(e.Text);
            e.Handled = true;
        }

        public override void Render(DrawingContext context)
        {
            base.Render(context);
            double w = Bounds.Width;
            double h = Bounds.Height;
            context.FillRectangle(new SolidColorBrush(owner.TermBgColor), new Rect(0, 0, w, h));

            var buf = owner.ActiveBuffer;
            if (buf == null) return;

            var fontRegular = new Typeface("Cascadia Code, Consolas, Menlo, monospace", FontStyle.Normal, FontWeight.Normal);
            var fontBold = new Typeface("Cascadia Code, Consolas, Menlo, monospace", FontStyle.Normal, FontWeight.Bold);
            var fontItalic = new Typeface("Cascadia Code, Consolas, Menlo, monospace", FontStyle.Italic, FontWeight.Normal);

            int rows = buf.Rows;
            int cols = buf.Cols;
            for (int r = 0; r < rows; r++)
            {
                double y = PadTop + r * CellH;
                if (y + CellH < 0 || y > h) continue;

                for (int c = 0; c < cols; c++)
                {
                    double x = PadLeft + c * CellW;
                    if (x > w) break;

                    TermCell cell = buf.GetViewportCell(r, c, scrollbackOffset);
                    bool inverse = (cell.Flags & TermCell.FLAG_INVERSE) != 0;
                    bool isDefBg = (cell.Flags & TermCell.FLAG_DEFAULT_BG) != 0 ||
                                   (cell.Bg.R == 0 && cell.Bg.G == 0 && cell.Bg.B == 0) ||
                                   (owner.IsLightTerminal && cell.Bg.R < 45 && cell.Bg.G < 45 && cell.Bg.B < 55) ||
                                   (!owner.IsLightTerminal && cell.Bg.R > 235 && cell.Bg.G > 235 && cell.Bg.B > 235);
                    bool isDefFg = (cell.Flags & TermCell.FLAG_DEFAULT_FG) != 0 ||
                                   (owner.IsLightTerminal && cell.Fg.R > 195 && cell.Fg.G > 195 && cell.Fg.B > 195) ||
                                   (!owner.IsLightTerminal && cell.Fg.R < 50 && cell.Fg.G < 50 && cell.Fg.B < 55 && (cell.Fg.R + cell.Fg.G + cell.Fg.B) > 0);

                    Color fg = isDefFg ? owner.TermFgColor : AvaloniaColorHelper.ToAvColor(cell.Fg);
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

                    bool selected = IsCellSelected(r, c);
                    if (selected)
                    {
                        bg = Color.FromArgb(120, owner.AccentColor.R, owner.AccentColor.G, owner.AccentColor.B);
                    }
                    if (bg != owner.TermBgColor || selected)
                    {
                        context.FillRectangle(new SolidColorBrush(bg), new Rect(x, y, CellW + 0.5, CellH + 0.5));
                    }

                    char ch = cell.Char;
                    if (ch > ' ' && ch != '\0')
                    {
                        bool bold = (cell.Flags & TermCell.FLAG_BOLD) != 0;
                        bool italic = (cell.Flags & TermCell.FLAG_ITALIC) != 0;
                        bool dim = (cell.Flags & TermCell.FLAG_DIM) != 0;
                        if (dim) fg = Color.FromArgb(165, fg.R, fg.G, fg.B);

                        var ft = new FormattedText(
                            ch.ToString(),
                            CultureInfo.InvariantCulture,
                            FlowDirection.LeftToRight,
                            bold ? fontBold : (italic ? fontItalic : fontRegular),
                            12.5,
                            new SolidColorBrush(fg));
                        context.DrawText(ft, new Point(x, y + 1));

                        if ((cell.Flags & TermCell.FLAG_UNDERLINE) != 0)
                        {
                            context.DrawLine(new Pen(new SolidColorBrush(fg), 1), new Point(x, y + CellH - 2), new Point(x + CellW, y + CellH - 2));
                        }
                    }
                }
            }

            // Draw terminal cursor when at bottom of scrollback
            if (scrollbackOffset == 0 && buf.CursorVisible && owner.CursorBlinkOn)
            {
                double cx = PadLeft + buf.CursorCol * CellW;
                double cy = PadTop + buf.CursorRow * CellH;
                var cursorRect = new Rect(cx, cy + 1, CellW, CellH - 2);
                context.FillRectangle(
                    new SolidColorBrush(Color.FromArgb(185, owner.AccentColor.R, owner.AccentColor.G, owner.AccentColor.B)),
                    cursorRect);
            }
        }
    }
}
