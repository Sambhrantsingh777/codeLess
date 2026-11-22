using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using codeLess.Studio;

namespace codeLess.AvaloniaApp
{
    /// <summary>Vector glyphs for the debug toolbar / Run and Debug view (drawn, so they follow the theme color).</summary>
    public class DebugGlyph : Control
    {
        public static readonly StyledProperty<string> KindProperty = AvaloniaProperty.Register<DebugGlyph, string>(nameof(Kind), "continue");
        public static readonly StyledProperty<Color> GlyphColorProperty = AvaloniaProperty.Register<DebugGlyph, Color>(nameof(GlyphColor), Colors.White);

        static DebugGlyph()
        {
            AffectsRender<DebugGlyph>(KindProperty, GlyphColorProperty);
        }

        public string Kind { get => GetValue(KindProperty); set => SetValue(KindProperty, value); }
        public Color GlyphColor { get => GetValue(GlyphColorProperty); set => SetValue(GlyphColorProperty, value); }

        public DebugGlyph()
        {
            Width = 16;
            Height = 16;
            IsHitTestVisible = false;
        }

        public override void Render(DrawingContext ctx)
        {
            double w = Bounds.Width, h = Bounds.Height;
            if (w <= 0 || h <= 0) return;
            var brush = new SolidColorBrush(GlyphColor);
            var pen = new Pen(brush, 1.5, lineCap: PenLineCap.Round, lineJoin: PenLineJoin.Round);
            double s = Math.Min(w, h);
            double ox = (w - s) / 2, oy = (h - s) / 2;
            Point P(double x, double y) => new Point(ox + x * s, oy + y * s);
            StreamGeometry Poly(params Point[] pts)
            {
                var g = new StreamGeometry();
                using (var c = g.Open())
                {
                    c.BeginFigure(pts[0], true);
                    for (int i = 1; i < pts.Length; i++) c.LineTo(pts[i]);
                    c.EndFigure(true);
                }
                return g;
            }
            switch (Kind)
            {
                case "start":
                    ctx.DrawGeometry(new SolidColorBrush(Color.FromRgb(56, 170, 90)), null, Poly(P(0.25, 0.15), P(0.85, 0.5), P(0.25, 0.85)));
                    break;
                case "continue":
                    ctx.FillRectangle(brush, new Rect(P(0.16, 0.18), P(0.3, 0.82)));
                    ctx.DrawGeometry(brush, null, Poly(P(0.4, 0.18), P(0.88, 0.5), P(0.4, 0.82)));
                    break;
                case "pause":
                    ctx.FillRectangle(brush, new Rect(P(0.25, 0.18), P(0.42, 0.82)));
                    ctx.FillRectangle(brush, new Rect(P(0.58, 0.18), P(0.75, 0.82)));
                    break;
                case "stepOver":
                    {
                        var g = new StreamGeometry();
                        using (var c = g.Open())
                        {
                            c.BeginFigure(P(0.15, 0.55), false);
                            c.ArcTo(P(0.85, 0.55), new Size(s * 0.36, s * 0.32), 0, false, SweepDirection.Clockwise);
                            c.EndFigure(false);
                        }
                        ctx.DrawGeometry(null, pen, g);
                        ctx.DrawGeometry(brush, null, Poly(P(0.7, 0.42), P(0.95, 0.5), P(0.82, 0.7)));
                        ctx.DrawEllipse(brush, null, P(0.5, 0.82), s * 0.09, s * 0.09);
                        break;
                    }
                case "stepInto":
                    ctx.DrawLine(pen, P(0.5, 0.1), P(0.5, 0.55));
                    ctx.DrawGeometry(brush, null, Poly(P(0.32, 0.45), P(0.68, 0.45), P(0.5, 0.66)));
                    ctx.DrawEllipse(brush, null, P(0.5, 0.84), s * 0.09, s * 0.09);
                    break;
                case "stepOut":
                    ctx.DrawLine(pen, P(0.5, 0.62), P(0.5, 0.25));
                    ctx.DrawGeometry(brush, null, Poly(P(0.32, 0.33), P(0.68, 0.33), P(0.5, 0.1)));
                    ctx.DrawEllipse(brush, null, P(0.5, 0.84), s * 0.09, s * 0.09);
                    break;
                case "restart":
                    {
                        var g = new StreamGeometry();
                        using (var c = g.Open())
                        {
                            c.BeginFigure(P(0.78, 0.38), false);
                            c.ArcTo(P(0.72, 0.72), new Size(s * 0.32, s * 0.32), 0, true, SweepDirection.CounterClockwise);
                            c.EndFigure(false);
                        }
                        ctx.DrawGeometry(null, new Pen(new SolidColorBrush(Color.FromRgb(56, 170, 90)), 1.6, lineCap: PenLineCap.Round), g);
                        ctx.DrawGeometry(new SolidColorBrush(Color.FromRgb(56, 170, 90)), null, Poly(P(0.62, 0.18), P(0.9, 0.2), P(0.82, 0.46)));
                        break;
                    }
                case "stop":
                    ctx.FillRectangle(new SolidColorBrush(Color.FromRgb(232, 80, 80)), new Rect(P(0.22, 0.22), P(0.78, 0.78)), 1.5f);
                    break;
                case "gear":
                    ctx.DrawEllipse(null, pen, P(0.5, 0.5), s * 0.2, s * 0.2);
                    for (int i = 0; i < 8; i++)
                    {
                        double a = i * Math.PI / 4;
                        ctx.DrawLine(pen, P(0.5 + Math.Cos(a) * 0.28, 0.5 + Math.Sin(a) * 0.28), P(0.5 + Math.Cos(a) * 0.4, 0.5 + Math.Sin(a) * 0.4));
                    }
                    break;
                case "plus":
                    ctx.DrawLine(pen, P(0.5, 0.2), P(0.5, 0.8));
                    ctx.DrawLine(pen, P(0.2, 0.5), P(0.8, 0.5));
                    break;
                case "close":
                    ctx.DrawLine(pen, P(0.25, 0.25), P(0.75, 0.75));
                    ctx.DrawLine(pen, P(0.75, 0.25), P(0.25, 0.75));
                    break;
                case "grip":
                    for (int r = 0; r < 3; r++)
                        for (int col = 0; col < 2; col++)
                            ctx.DrawEllipse(brush, null, P(0.38 + col * 0.24, 0.25 + r * 0.25), s * 0.06, s * 0.06);
                    break;
                case "toggle":
                    ctx.DrawEllipse(null, pen, P(0.38, 0.5), s * 0.2, s * 0.2);
                    ctx.DrawEllipse(brush, null, P(0.64, 0.5), s * 0.2, s * 0.2);
                    break;
            }
        }
    }

    internal static class DebugUi
    {
        public static Button IconButton(string kind, string tooltip, Action onClick, double size = 22)
        {
            var b = new Button
            {
                Content = new DebugGlyph { Kind = kind, Width = 14, Height = 14 },
                Width = size,
                Height = size,
                Padding = new Thickness(0),
                Background = Brushes.Transparent,
                BorderThickness = new Thickness(0),
                Cursor = new Cursor(StandardCursorType.Hand)
            };
            ToolTip.SetTip(b, AvaloniaCommandPaletteOverlay.FormatShortcutForPlatform(tooltip));
            if (onClick != null) b.Click += (s, e) => onClick();
            return b;
        }

        public static void ColorGlyphs(Control root, Color c)
        {
            if (root is DebugGlyph g) g.GlyphColor = c;
            if (root is ContentControl cc && cc.Content is Control child) ColorGlyphs(child, c);
            if (root is Panel p) foreach (var ch in p.Children) ColorGlyphs(ch, c);
            if (root is Decorator d && d.Child != null) ColorGlyphs(d.Child, c);
        }
    }

    /// <summary>
    /// RUN AND DEBUG sidebar: configuration picker + Start, VARIABLES (lazy tree), WATCH, CALL STACK, BREAKPOINTS
    /// (with enable/disable and exception filters). All data comes from the shared <see cref="DebugController"/>.
    /// </summary>
    public class AvaloniaDebugSidebar : Grid
    {
        private readonly DebugController ctl;
        private readonly UiPost ui = UiPost.CaptureCurrent();
        private StudioTheme theme;
        public ComboBox ConfigCombo { get; }
        public Button StartButton { get; }
        public TextBlock HintText { get; }
        public Button CreateLaunchJsonLink { get; }
        public StackPanel VariablesHost { get; } = new StackPanel();
        public StackPanel WatchHost { get; } = new StackPanel();
        public StackPanel CallStackHost { get; } = new StackPanel();
        public StackPanel BreakpointsHost { get; } = new StackPanel();
        private readonly List<(Button Header, StackPanel Body, string Title)> sections = new List<(Button, StackPanel, string)>();
        private readonly HashSet<string> expandedKeys = new HashSet<string>(StringComparer.Ordinal);
        private readonly HashSet<string> collapsedScopes = new HashSet<string>(StringComparer.Ordinal);
        private DapStackFrameInfo variablesFrame;
        private int variablesBuildGen;
        private TextBox watchInput;

        public event Action StartRequested;
        public event Action CreateLaunchJsonRequested;
        public event Action OpenLaunchJsonRequested;
        public event Action<string> ConfigurationChosen;
        public event Action<DebugSourceBreakpoint> EditBreakpointRequested;
        public event Action<string, int> OpenLocationRequested;

        public AvaloniaDebugSidebar(DebugController controller)
        {
            ctl = controller;
            RowDefinitions = new RowDefinitions("Auto,*");
            Margin = new Thickness(0, 4, 0, 0);

            var top = new StackPanel { Spacing = 6, Margin = new Thickness(10, 2, 8, 6) };
            var row = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto") };
            StartButton = DebugUi.IconButton("start", "Start Debugging (F5)", () => StartRequested?.Invoke(), 26);
            ConfigCombo = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch, FontSize = 12, MinHeight = 24, Margin = new Thickness(4, 0) };
            ConfigCombo.SelectionChanged += (s, e) =>
            {
                if (suppressComboEvents) return;
                if (ConfigCombo.SelectedItem is string name) ConfigurationChosen?.Invoke(name);
            };
            var gear = DebugUi.IconButton("gear", "Open launch.json", () => OpenLaunchJsonRequested?.Invoke(), 24);
            row.Children.Add(StartButton);
            Grid.SetColumn(ConfigCombo, 1);
            row.Children.Add(ConfigCombo);
            Grid.SetColumn(gear, 2);
            row.Children.Add(gear);
            top.Children.Add(row);
            HintText = new TextBlock { FontSize = 11.5, TextWrapping = TextWrapping.Wrap, Text = "" };
            CreateLaunchJsonLink = new Button
            {
                Content = new LiteralTextBlock("create a launch.json file") { FontSize = 11.5, TextDecorations = TextDecorations.Underline },
                Background = Brushes.Transparent,
                Padding = new Thickness(0),
                BorderThickness = new Thickness(0),
                Cursor = new Cursor(StandardCursorType.Hand)
            };
            CreateLaunchJsonLink.Click += (s, e) => CreateLaunchJsonRequested?.Invoke();
            top.Children.Add(HintText);
            top.Children.Add(CreateLaunchJsonLink);
            Children.Add(top);

            var body = new StackPanel { Spacing = 0 };
            AddSection(body, "VARIABLES", VariablesHost, null);
            var watchActions = new StackPanel { Orientation = Orientation.Horizontal };
            watchActions.Children.Add(DebugUi.IconButton("plus", "Add Expression", ShowWatchInput, 20));
            AddSection(body, "WATCH", WatchHost, watchActions);
            AddSection(body, "CALL STACK", CallStackHost, null);
            var bpActions = new StackPanel { Orientation = Orientation.Horizontal };
            bpActions.Children.Add(DebugUi.IconButton("toggle", "Toggle Activate Breakpoints", () =>
            {
                bool anyEnabled = ctl.Breakpoints.All().Any(b => b.Enabled);
                ctl.Breakpoints.SetAllEnabled(!anyEnabled);
            }, 20));
            bpActions.Children.Add(DebugUi.IconButton("close", "Remove All Breakpoints", () => ctl.Breakpoints.RemoveAll(), 20));
            AddSection(body, "BREAKPOINTS", BreakpointsHost, bpActions);
            var scroll = new ScrollViewer { Content = body, HorizontalScrollBarVisibility = global::Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled };
            Grid.SetRow(scroll, 1);
            Children.Add(scroll);

            ctl.StateChanged += RefreshAll;
            ctl.CallStackChanged += () => { RebuildCallStack(); RebuildVariables(); };
            ctl.BreakpointsChanged += RebuildBreakpoints;
            ctl.WatchesChanged += RebuildWatches;
        }

        private bool suppressComboEvents;

        private void AddSection(StackPanel parent, string title, StackPanel host, Control actions)
        {
            var headerGrid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
            var header = new Button
            {
                Content = new SectionHeaderLabel("▾ " + title),
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Left,
                Padding = new Thickness(4, 0),
                Height = UiTokens.SectionHeaderHeight,
                MinHeight = 0,
                VerticalContentAlignment = VerticalAlignment.Center,
                Background = Brushes.Transparent,
                BorderThickness = new Thickness(0, 1, 0, 0),
                CornerRadius = new CornerRadius(0)
            };
            host.Margin = new Thickness(0, 0, 0, 6);
            header.Click += (s, e) =>
            {
                host.IsVisible = !host.IsVisible;
                header.Content = new SectionHeaderLabel((host.IsVisible ? "▾ " : "▸ ") + title);
            };
            headerGrid.Children.Add(header);
            if (actions != null)
            {
                actions.VerticalAlignment = VerticalAlignment.Center;
                actions.Margin = new Thickness(0, 0, 6, 0);
                Grid.SetColumn(actions, 1);
                headerGrid.Children.Add(actions);
            }
            parent.Children.Add(headerGrid);
            parent.Children.Add(host);
            sections.Add((header, host, title));
        }

        public void ApplyTheme(StudioTheme t)
        {
            theme = t;
            var fg = new SolidColorBrush(AvaloniaColorHelper.ToAvColor(t.TextPrimary));
            var fg2 = new SolidColorBrush(AvaloniaColorHelper.ToAvColor(t.TextSecondary));
            var border = new SolidColorBrush(AvaloniaColorHelper.ToAvColor(t.BorderSubtle));
            foreach (var s in sections)
            {
                s.Header.Foreground = fg2;
                s.Header.BorderBrush = border;
                if (s.Header.Content is TextBlock tb) tb.Foreground = fg2;
            }
            HintText.Foreground = fg2;
            CreateLaunchJsonLink.Foreground = new SolidColorBrush(AvaloniaColorHelper.ToAvColor(t.AccentPrimary));
            if (CreateLaunchJsonLink.Content is TextBlock lt) lt.Foreground = CreateLaunchJsonLink.Foreground;
            DebugUi.ColorGlyphs(this, AvaloniaColorHelper.ToAvColor(t.TextPrimary));
            RefreshAll();
        }

        public void SetConfigurations(IList<string> names, string selected, bool hasLaunchJson, string launchJsonError)
        {
            suppressComboEvents = true;
            ConfigCombo.ItemsSource = names.ToList();
            ConfigCombo.SelectedItem = names.Contains(selected) ? selected : names.FirstOrDefault();
            suppressComboEvents = false;
            CreateLaunchJsonLink.IsVisible = !hasLaunchJson;
            if (!string.IsNullOrEmpty(launchJsonError)) HintText.Text = launchJsonError;
        }

        public string SelectedConfiguration => ConfigCombo.SelectedItem as string;

        // ---- rows ----

        private Color C(System.Drawing.Color c) => AvaloniaColorHelper.ToAvColor(c);

        private Grid MakeRow(int depth, bool? expanded, IEnumerable<(string Text, Color Color, bool Bold)> parts, Action onClick, string tooltip = null)
        {
            var g = new Grid
            {
                ColumnDefinitions = new ColumnDefinitions("Auto,Auto,*"),
                Height = 22,
                Background = Brushes.Transparent,
                Cursor = onClick != null ? new Cursor(StandardCursorType.Hand) : Cursor.Default
            };
            var spacer = new Border { Width = 8 + depth * 12 };
            g.Children.Add(spacer);
            Control chevron;
            Color sec = theme != null ? C(theme.TextSecondary) : Colors.Gray;
            if (expanded.HasValue)
                chevron = new VectorIconControl { IconName = expanded.Value ? "chevron-down" : "chevron-right", Width = 10, Height = 10, IconColor = sec, Margin = new Thickness(0, 0, 4, 0), VerticalAlignment = VerticalAlignment.Center };
            else
                chevron = new Border { Width = 14 };
            Grid.SetColumn(chevron, 1);
            g.Children.Add(chevron);
            var tb = new TextBlock { FontSize = 12, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, Inlines = new InlineCollection() };
            foreach (var p in parts)
                tb.Inlines.Add(new Run(p.Text) { Foreground = new SolidColorBrush(p.Color), FontWeight = p.Bold ? FontWeight.SemiBold : FontWeight.Normal });
            Grid.SetColumn(tb, 2);
            g.Children.Add(tb);
            if (!string.IsNullOrEmpty(tooltip)) ToolTip.SetTip(g, tooltip);
            if (onClick != null)
            {
                g.PointerPressed += (s, e) =>
                {
                    if (e.GetCurrentPoint(g).Properties.IsLeftButtonPressed) { onClick(); e.Handled = true; }
                };
            }
            if (theme != null)
            {
                var hover = new SolidColorBrush(C(theme.BgHover));
                g.PointerEntered += (s, e) => g.Background = hover;
                g.PointerExited += (s, e) => g.Background = Brushes.Transparent;
            }
            return g;
        }

        private TextBlock Placeholder(string text)
        {
            return new TextBlock
            {
                Text = text,
                FontSize = 11.5,
                Margin = new Thickness(22, 2, 8, 4),
                TextWrapping = TextWrapping.Wrap,
                Foreground = theme != null ? new SolidColorBrush(C(theme.TextSecondary)) : Brushes.Gray
            };
        }

        public void RefreshAll()
        {
            if (theme == null) return;
            StartButton.IsEnabled = !ctl.IsActive;
            if (!ctl.IsActive && string.IsNullOrEmpty(HintText.Text)) HintText.Text = "";
            RebuildCallStack();
            RebuildVariables();
            RebuildWatches();
            RebuildBreakpoints();
        }

        // VARIABLES: scopes of the selected frame, lazily expanded; expansion state survives steps (by name path).
        public void RebuildVariables()
        {
            if (theme == null) return;
            var frame = ctl.IsStopped ? ctl.SelectedFrame : null;
            if (frame != null && variablesFrame == frame && VariablesHost.Children.Count > 0) return;
            variablesFrame = frame;
            int gen = ++variablesBuildGen;
            VariablesHost.Children.Clear();
            if (frame == null)
            {
                if (ctl.IsActive) VariablesHost.Children.Add(Placeholder(ctl.IsStopped ? "No frame selected." : "Running..."));
                return;
            }
            ctl.GetScopesAsync(frame).ContinueWith(t => ui.Post(() =>
            {
                if (gen != variablesBuildGen) return;
                var scopes = t.IsFaulted ? new List<DapScopeInfo>() : t.Result;
                if (scopes.Count == 0) { VariablesHost.Children.Add(Placeholder("No variables.")); return; }
                for (int i = 0; i < scopes.Count; i++)
                {
                    var sc = scopes[i];
                    string key = "scope:" + sc.Name;
                    bool expand = !sc.Expensive && !collapsedScopes.Contains(key) && (i == 0 || expandedKeys.Contains(key));
                    AddLazyNode(VariablesHost, 0, key, new[] { (sc.Name, C(theme.TextPrimary), true) }, sc.VariablesReference, expand, isScope: true);
                }
            }));
        }

        private void AddLazyNode(StackPanel parent, int depth, string key, (string, Color, bool)[] parts, int variablesReference, bool expand, bool isScope = false, string tooltip = null)
        {
            var container = new StackPanel();
            var children = new StackPanel { IsVisible = false };
            bool hasChildren = variablesReference > 0;
            bool loaded = false;
            Grid row = null;
            void Toggle()
            {
                if (!hasChildren) return;
                bool nowOpen = !children.IsVisible;
                children.IsVisible = nowOpen;
                if (nowOpen) { expandedKeys.Add(key); collapsedScopes.Remove(key); }
                else { expandedKeys.Remove(key); if (isScope) collapsedScopes.Add(key); }
                var newRow = MakeRow(depth, nowOpen, parts, Toggle, tooltip);
                container.Children[0] = newRow;
                row = newRow;
                if (nowOpen && !loaded)
                {
                    loaded = true;
                    children.Children.Add(Placeholder("Loading..."));
                    ctl.GetVariablesAsync(variablesReference).ContinueWith(t => ui.Post(() =>
                    {
                        children.Children.Clear();
                        var vars = t.IsFaulted ? new List<DapVariableInfo>() : t.Result;
                        if (vars.Count == 0) children.Children.Add(Placeholder("(empty)"));
                        foreach (var v in vars.Take(500))
                        {
                            string childKey = key + "/" + v.Name;
                            AddLazyNode(children, depth + 1, childKey, VariableParts(v), v.VariablesReference, expandedKeys.Contains(childKey),
                                tooltip: string.IsNullOrEmpty(v.Type) ? v.Value : v.Type + ": " + v.Value);
                        }
                        if (vars.Count > 500) children.Children.Add(Placeholder("... " + (vars.Count - 500) + " more"));
                    }));
                }
            }
            row = MakeRow(depth, hasChildren ? (bool?)false : null, parts, hasChildren ? (Action)Toggle : null, tooltip);
            container.Children.Add(row);
            container.Children.Add(children);
            parent.Children.Add(container);
            if (expand && hasChildren) Toggle();
        }

        private (string, Color, bool)[] VariableParts(DapVariableInfo v)
        {
            Color valueColor = C(theme.TextPrimary);
            string val = v.Value ?? "";
            if (val.StartsWith("'") || val.StartsWith("\"")) valueColor = C(theme.TokenString);
            else if (val.Length > 0 && (char.IsDigit(val[0]) || (val[0] == '-' && val.Length > 1 && char.IsDigit(val[1])))) valueColor = C(theme.TokenNumber);
            else if (val == "True" || val == "False" || val == "true" || val == "false" || val == "None" || val == "null" || val == "nil") valueColor = C(theme.TokenKeyword);
            if (val.Length > 300) val = val.Substring(0, 300) + "…";
            return new[] { (v.Name, C(theme.TokenFunction), false), (": ", C(theme.TextSecondary), false), (val.Replace("\n", "⏎"), valueColor, false) };
        }

        public void RebuildWatches()
        {
            if (theme == null) return;
            WatchHost.Children.Clear();
            foreach (var w in ctl.Watches.ToList())
            {
                var item = w;
                string val = item.Result == null ? (ctl.IsStopped ? "…" : "not available")
                    : item.Result.Success ? item.Result.Result : item.Result.Error;
                Color vc = item.Result != null && !item.Result.Success ? C(theme.DiagnosticError) : C(theme.TextPrimary);
                var holder = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
                var parts = new[] { (item.Expression, C(theme.TokenFunction), false), (": ", C(theme.TextSecondary), false), ((val ?? "").Replace("\n", "⏎"), vc, false) };
                var nodeHost = new StackPanel();
                if (item.Result != null && item.Result.Success && item.Result.VariablesReference > 0)
                    AddLazyNode(nodeHost, 0, "watch:" + item.Expression, parts, item.Result.VariablesReference, false);
                else
                    nodeHost.Children.Add(MakeRow(0, null, parts, null, val));
                holder.Children.Add(nodeHost);
                var remove = DebugUi.IconButton("close", "Remove Expression", () => ctl.RemoveWatch(item), 18);
                DebugUi.ColorGlyphs(remove, C(theme.TextSecondary));
                remove.VerticalAlignment = VerticalAlignment.Top;
                remove.Margin = new Thickness(0, 2, 6, 0);
                Grid.SetColumn(remove, 1);
                holder.Children.Add(remove);
                WatchHost.Children.Add(holder);
            }
            if (watchInput != null) WatchHost.Children.Add(watchInput);
        }

        public void ShowWatchInput()
        {
            if (watchInput == null)
            {
                watchInput = new TextBox { Watermark = "Expression to watch", FontSize = 12, Margin = new Thickness(20, 2, 8, 4), Padding = new Thickness(5, 2) };
                watchInput.KeyDown += (s, e) =>
                {
                    if (e.Key == Key.Enter)
                    {
                        string text = watchInput.Text;
                        watchInput = null;
                        ctl.AddWatch(text);
                        RebuildWatches();
                        e.Handled = true;
                    }
                    else if (e.Key == Key.Escape)
                    {
                        watchInput = null;
                        RebuildWatches();
                        e.Handled = true;
                    }
                };
            }
            RebuildWatches();
            WatchHost.IsVisible = true;
            Dispatcher.UIThread.Post(() => watchInput?.Focus(), DispatcherPriority.Loaded);
        }

        public void RebuildCallStack()
        {
            if (theme == null) return;
            CallStackHost.Children.Clear();
            if (!ctl.IsActive) return;
            if (!ctl.IsStopped)
            {
                CallStackHost.Children.Add(MakeRow(0, null, new[] { (ctl.SessionName ?? "Session", C(theme.TextPrimary), true), ("   RUNNING", C(theme.TextSecondary), false) }, null));
                return;
            }
            foreach (var th in ctl.Threads)
            {
                var thread = th;
                bool hasFrames = ctl.FramesByThread.TryGetValue(thread.Id, out var frames);
                bool isStoppedThread = thread.Id == ctl.SelectedThreadId;
                string state = isStoppedThread ? "   PAUSED ON " + (ctl.Session?.StopReason ?? "BREAKPOINT").ToUpperInvariant() : "   PAUSED";
                CallStackHost.Children.Add(MakeRow(0, hasFrames, new[] { (thread.Name, C(theme.TextPrimary), true), (state, C(theme.TextSecondary), false) },
                    () => { if (!hasFrames) ctl.LoadThreadFrames(thread.Id); }));
                if (!hasFrames) continue;
                foreach (var f in frames)
                {
                    var frame = f;
                    bool sel = ReferenceEquals(frame, ctl.SelectedFrame);
                    bool subtle = !frame.HasSource || frame.PresentationHint == "subtle";
                    string loc = frame.HasSource ? (frame.SourceName ?? Path.GetFileName(frame.SourcePath)) + ":" + frame.Line : (frame.SourceName ?? "unknown source");
                    var row = MakeRow(1, null, new[]
                    {
                        (frame.Name ?? "?", subtle ? C(theme.TextSecondary) : C(theme.TextPrimary), sel),
                        ("   " + loc, C(theme.TextSecondary), false)
                    }, () => ctl.SelectFrame(frame), frame.HasSource ? frame.SourcePath + ":" + frame.Line : null);
                    if (sel) row.Background = WorkbenchStyles.B(UiTokens.ListActiveSelection(theme, theme.BgSidebar));
                    CallStackHost.Children.Add(row);
                }
            }
        }

        public void RebuildBreakpoints()
        {
            if (theme == null) return;
            BreakpointsHost.Children.Clear();
            foreach (var f in ctl.ExceptionFilters.ToList())
            {
                var filter = f;
                var cb = new CheckBox
                {
                    Content = new LiteralTextBlock(filter.Label) { FontSize = 12, Foreground = new SolidColorBrush(C(theme.TextPrimary)) },
                    IsChecked = filter.Enabled,
                    Margin = new Thickness(14, 0, 4, 0),
                    MinHeight = 20,
                    Padding = new Thickness(4, 0)
                };
                cb.IsCheckedChanged += (s, e) => ctl.SetExceptionFilter(filter, cb.IsChecked == true);
                BreakpointsHost.Children.Add(cb);
            }
            var all = ctl.Breakpoints.All();
            foreach (var b in all)
            {
                var bp = b;
                var g = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto,Auto"), Height = 22, Background = Brushes.Transparent };
                var cb = new CheckBox { IsChecked = bp.Enabled, Margin = new Thickness(14, 0, 0, 0), MinHeight = 20, Padding = new Thickness(0) };
                cb.IsCheckedChanged += (s, e) => ctl.SetBreakpointEnabled(bp, cb.IsChecked == true);
                g.Children.Add(cb);
                string dir = Path.GetFileName(Path.GetDirectoryName(bp.FilePath) ?? "");
                var tb = new TextBlock { FontSize = 12, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, Inlines = new InlineCollection(), Cursor = new Cursor(StandardCursorType.Hand) };
                Color nameColor = bp.Verified == false ? C(theme.TextSecondary) : C(theme.TextPrimary);
                tb.Inlines.Add(new Run(Path.GetFileName(bp.FilePath)) { Foreground = new SolidColorBrush(nameColor) });
                tb.Inlines.Add(new Run("  " + dir) { Foreground = new SolidColorBrush(C(theme.TextSecondary)), FontSize = 11 });
                string extra = bp.IsLogpoint ? "  log: " + bp.LogMessage : bp.IsConditional ? "  " + (bp.Condition ?? ("hits " + bp.HitCondition)) : "";
                if (extra.Length > 0) tb.Inlines.Add(new Run(extra) { Foreground = new SolidColorBrush(C(theme.TokenString)), FontSize = 11 });
                tb.PointerPressed += (s, e) => { OpenLocationRequested?.Invoke(bp.FilePath, bp.Line + 1); e.Handled = true; };
                ToolTip.SetTip(tb, DebugBreakpointStore.Describe(bp) + (string.IsNullOrEmpty(bp.VerifyMessage) ? "" : "\n" + bp.VerifyMessage));
                Grid.SetColumn(tb, 1);
                g.Children.Add(tb);
                var edit = WorkbenchStyles.IconButton("pencil", "Edit Condition / Hit Count / Log Message", () => EditBreakpointRequested?.Invoke(bp), theme, 18, 13);
                Grid.SetColumn(edit, 2);
                g.Children.Add(edit);
                var line = new TextBlock { Text = (bp.Line + 1).ToString(), FontSize = 11, Margin = new Thickness(4, 0, 4, 0), VerticalAlignment = VerticalAlignment.Center, Foreground = new SolidColorBrush(C(theme.TextSecondary)) };
                var remove = DebugUi.IconButton("close", "Remove Breakpoint", () => ctl.RemoveBreakpoint(bp), 18);
                DebugUi.ColorGlyphs(remove, C(theme.TextSecondary));
                var right = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 6, 0) };
                right.Children.Add(line);
                right.Children.Add(remove);
                Grid.SetColumn(right, 3);
                g.Children.Add(right);
                var hover = new SolidColorBrush(C(theme.BgHover));
                g.PointerEntered += (s, e) => g.Background = hover;
                g.PointerExited += (s, e) => g.Background = Brushes.Transparent;
                BreakpointsHost.Children.Add(g);
            }
            if (all.Count == 0 && ctl.ExceptionFilters.Count == 0)
                BreakpointsHost.Children.Add(Placeholder("Click the gutter left of a line number (or press F9) to add a breakpoint."));
        }
    }

    /// <summary>DEBUG CONSOLE panel: program output (colored by category) plus a REPL input with history.</summary>
    public class AvaloniaDebugConsole : Grid
    {
        private readonly DebugController ctl;
        private StudioTheme theme;
        public SelectableTextBlock Output { get; }
        public TextBox Input { get; }
        private readonly ScrollViewer scroll;
        private int historyIndex = -1;
        private int renderedEntries;

        public AvaloniaDebugConsole(DebugController controller)
        {
            ctl = controller;
            RowDefinitions = new RowDefinitions("*,Auto");
            IsVisible = false;
            Output = new SelectableTextBlock
            {
                FontFamily = new FontFamily("Cascadia Code, Consolas, Menlo, monospace"),
                FontSize = 12,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(10, 4, 10, 4),
                Inlines = new InlineCollection()
            };
            scroll = new ScrollViewer { Content = Output };
            Children.Add(scroll);
            Input = new TextBox
            {
                Watermark = "Evaluate expression in the selected frame (Enter). ↑/↓ history",
                FontFamily = new FontFamily("Cascadia Code, Consolas, Menlo, monospace"),
                FontSize = 12,
                Margin = new Thickness(8, 2, 8, 4),
                Padding = new Thickness(6, 3)
            };
            Input.AddHandler(KeyDownEvent, OnInputKey, global::Avalonia.Interactivity.RoutingStrategies.Tunnel);
            Grid.SetRow(Input, 1);
            Children.Add(Input);
            ctl.ConsoleAppended += e => Append(e);
            ctl.ConsoleCleared += () => { Output.Inlines.Clear(); renderedEntries = 0; };
        }

        public string Text => string.Concat(Output.Inlines.OfType<Run>().Select(r => r.Text));

        private void OnInputKey(object sender, KeyEventArgs e)
        {
            var hist = ctl.ReplHistory;
            if (e.Key == Key.Enter && (e.KeyModifiers & KeyModifiers.Shift) == 0)
            {
                string text = Input.Text ?? "";
                Input.Text = "";
                historyIndex = -1;
                ctl.SubmitRepl(text);
                e.Handled = true;
            }
            else if (e.Key == Key.Up && hist.Count > 0)
            {
                historyIndex = historyIndex < 0 ? hist.Count - 1 : Math.Max(0, historyIndex - 1);
                Input.Text = hist[historyIndex];
                Input.CaretIndex = Input.Text.Length;
                e.Handled = true;
            }
            else if (e.Key == Key.Down && historyIndex >= 0)
            {
                historyIndex++;
                if (historyIndex >= hist.Count) { historyIndex = -1; Input.Text = ""; }
                else Input.Text = hist[historyIndex];
                Input.CaretIndex = (Input.Text ?? "").Length;
                e.Handled = true;
            }
        }

        private Color ColorFor(string category)
        {
            if (theme == null) return Colors.White;
            switch (category)
            {
                case "stderr":
                case "error": return AvaloniaColorHelper.ToAvColor(theme.DiagnosticError);
                case "console":
                case "important": return AvaloniaColorHelper.ToAvColor(theme.AccentPrimary);
                case "input": return AvaloniaColorHelper.ToAvColor(theme.TextSecondary);
                case "result": return AvaloniaColorHelper.ToAvColor(theme.TokenString);
                default: return AvaloniaColorHelper.ToAvColor(theme.TerminalText);
            }
        }

        private void Append(DebugConsoleEntry e)
        {
            Output.Inlines.Add(new Run(e.Text) { Foreground = new SolidColorBrush(ColorFor(e.Category)) });
            renderedEntries++;
            while (Output.Inlines.Count > 3000) Output.Inlines.RemoveAt(0);
            bool atBottom = scroll.Offset.Y + scroll.Viewport.Height >= scroll.Extent.Height - 30;
            if (atBottom || e.Category == "input") Dispatcher.UIThread.Post(() => scroll.ScrollToEnd(), DispatcherPriority.Background);
        }

        public void ApplyTheme(StudioTheme t)
        {
            theme = t;
            Background = AvaloniaColorHelper.ToBrush(t.TerminalBg);
            Output.Inlines.Clear();
            foreach (var e in ctl.Console) Output.Inlines.Add(new Run(e.Text) { Foreground = new SolidColorBrush(ColorFor(e.Category)) });
        }
    }

    /// <summary>Floating debug toolbar (Continue/Pause, Step Over/Into/Out, Restart, Stop); drag the grip to move it.</summary>
    public class AvaloniaDebugToolbar : Border
    {
        private readonly DebugController ctl;
        private readonly Button continueBtn;
        private readonly DebugGlyph continueGlyph;
        private readonly Button stepOverBtn, stepIntoBtn, stepOutBtn;
        private bool dragging;
        private Point dragStart;
        private double startOffset;
        public double DragOffset { get; private set; }

        public AvaloniaDebugToolbar(DebugController controller, Action restart, Action stop)
        {
            ctl = controller;
            HorizontalAlignment = HorizontalAlignment.Center;
            VerticalAlignment = VerticalAlignment.Top;
            Margin = new Thickness(0, 4, 0, 0);
            CornerRadius = new CornerRadius(4);
            BorderThickness = new Thickness(1);
            Padding = new Thickness(2, 1);
            IsVisible = false;
            ZIndex = 50;
            BoxShadow = new BoxShadows(new BoxShadow { OffsetY = 2, Blur = 8, Color = Color.FromArgb(90, 0, 0, 0) });
            var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 1 };
            var grip = new Border { Width = 12, Height = 24, Background = Brushes.Transparent, Cursor = new Cursor(StandardCursorType.SizeWestEast), Child = new DebugGlyph { Kind = "grip", Width = 12, Height = 14 } };
            grip.PointerPressed += (s, e) => { dragging = true; dragStart = e.GetPosition(Parent as Visual); startOffset = DragOffset; e.Pointer.Capture(grip); e.Handled = true; };
            grip.PointerMoved += (s, e) =>
            {
                if (!dragging) return;
                DragOffset = startOffset + (e.GetPosition(Parent as Visual).X - dragStart.X);
                RenderTransform = new TranslateTransform(DragOffset, 0);
            };
            grip.PointerReleased += (s, e) => { dragging = false; e.Pointer.Capture(null); };
            row.Children.Add(grip);
            continueBtn = DebugUi.IconButton("continue", "Continue (F5)", () => ctl.ContinueOrPause(), 26);
            continueGlyph = (DebugGlyph)continueBtn.Content;
            stepOverBtn = DebugUi.IconButton("stepOver", "Step Over (F10)", () => ctl.StepOver(), 26);
            stepIntoBtn = DebugUi.IconButton("stepInto", "Step Into (F11)", () => ctl.StepInto(), 26);
            stepOutBtn = DebugUi.IconButton("stepOut", "Step Out (Shift+F11)", () => ctl.StepOut(), 26);
            row.Children.Add(continueBtn);
            row.Children.Add(stepOverBtn);
            row.Children.Add(stepIntoBtn);
            row.Children.Add(stepOutBtn);
            row.Children.Add(DebugUi.IconButton("restart", "Restart (Ctrl+Shift+F5)", restart, 26));
            row.Children.Add(DebugUi.IconButton("stop", "Stop (Shift+F5)", stop, 26));
            Child = row;
            ctl.StateChanged += Refresh;
        }

        public void Refresh()
        {
            IsVisible = ctl.IsActive;
            bool stopped = ctl.IsStopped;
            continueGlyph.Kind = stopped ? "continue" : "pause";
            ToolTip.SetTip(continueBtn, AvaloniaCommandPaletteOverlay.FormatShortcutForPlatform(stopped ? "Continue (F5)" : "Pause (F6)"));
            stepOverBtn.IsEnabled = stopped;
            stepIntoBtn.IsEnabled = stopped;
            stepOutBtn.IsEnabled = stopped;
        }

        public void ApplyTheme(StudioTheme t)
        {
            Background = AvaloniaColorHelper.ToBrush(t.FloatingBg);
            BorderBrush = AvaloniaColorHelper.ToBrush(t.BorderSubtle);
            DebugUi.ColorGlyphs(this, AvaloniaColorHelper.ToAvColor(t.TextPrimary));
            continueGlyph.GlyphColor = Color.FromRgb(70, 140, 230);
            foreach (var b in new[] { stepOverBtn, stepIntoBtn, stepOutBtn })
                DebugUi.ColorGlyphs(b, Color.FromRgb(70, 140, 230));
        }
    }
}
