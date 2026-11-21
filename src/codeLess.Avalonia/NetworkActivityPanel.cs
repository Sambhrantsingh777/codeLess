using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using codeLess.Studio;

namespace codeLess.AvaloniaApp
{
    /// <summary>Shield glyph for the status bar (filled when Offline Mode is on).</summary>
    public class NetworkShieldIcon : Control
    {
        public static readonly StyledProperty<bool> FilledProperty = AvaloniaProperty.Register<NetworkShieldIcon, bool>(nameof(Filled));
        public static readonly StyledProperty<IBrush> StrokeProperty = AvaloniaProperty.Register<NetworkShieldIcon, IBrush>(nameof(Stroke), Brushes.White);

        static NetworkShieldIcon()
        {
            AffectsRender<NetworkShieldIcon>(FilledProperty, StrokeProperty);
        }

        public bool Filled { get => GetValue(FilledProperty); set => SetValue(FilledProperty, value); }
        public IBrush Stroke { get => GetValue(StrokeProperty); set => SetValue(StrokeProperty, value); }

        public NetworkShieldIcon()
        {
            Width = 12;
            Height = 13;
            IsHitTestVisible = false;
        }

        public override void Render(DrawingContext ctx)
        {
            double w = Bounds.Width, h = Bounds.Height;
            if (w <= 0 || h <= 0) return;
            var geo = new StreamGeometry();
            using (var g = geo.Open())
            {
                g.BeginFigure(new Point(w / 2, 0.5), true);
                g.LineTo(new Point(w - 0.5, h * 0.18));
                g.LineTo(new Point(w - 0.5, h * 0.48));
                g.CubicBezierTo(new Point(w - 0.5, h * 0.78), new Point(w * 0.72, h * 0.92), new Point(w / 2, h - 0.5));
                g.CubicBezierTo(new Point(w * 0.28, h * 0.92), new Point(0.5, h * 0.78), new Point(0.5, h * 0.48));
                g.LineTo(new Point(0.5, h * 0.18));
                g.EndFigure(true);
            }
            if (Filled) ctx.DrawGeometry(Stroke, null, geo);
            else ctx.DrawGeometry(null, new Pen(Stroke, 1.3), geo);
        }
    }

    /// <summary>
    /// Network Activity view (macOS/Linux): live ledger table, totals per feature, filter, clear, export and the
    /// Offline Mode toggle. Hosted in <see cref="NetworkActivityWindow"/>.
    /// </summary>
    public class NetworkActivityView : Border
    {
        private static readonly string[] ColumnNames = { "Time", "Feature", "Method", "Host", "Path", "Up", "Down", "Status", "Duration", "Blocked" };
        private const string ColumnWidths = "70,220,58,160,*,64,70,60,70,58";

        private StudioTheme theme;
        private readonly TextBlock summaryText;
        private readonly TextBlock noteText;
        private readonly CheckBox offlineBox;
        private readonly TextBox filterBox;
        private readonly Button clearBtn, exportJsonBtn, exportCsvBtn;
        private readonly Grid headerGrid;
        private readonly Border headerBorder, tableBorder, totalsBorder;
        private readonly ListBox table;
        private readonly StackPanel totalsStack;
        private readonly TextBlock emptyText;
        private bool refreshQueued;
        private bool attached;
        private UiPost uiPost = UiPost.CaptureCurrent();

        public List<NetworkLedgerEntry> VisibleEntries { get; private set; } = new List<NetworkLedgerEntry>();
        public string SummaryText => summaryText.Text ?? "";
        public string NoteText => noteText.Text ?? "";
        public ListBox Table => table;
        public TextBox FilterBox => filterBox;
        public CheckBox OfflineToggle => offlineBox;
        public int TotalsRowCount => totalsStack.Children.Count;

        /// <summary>Set by the host to save exports (path chooser). Returns the chosen local path or null.</summary>
        public Func<string, string, Task<string>> PickSavePath;

        public NetworkActivityView(StudioTheme theme)
        {
            this.theme = theme ?? StudioTheme.GetById(StudioTheme.DefaultThemeId);
            Padding = new Thickness(14, 10, 14, 12);

            summaryText = new TextBlock { FontSize = UiTokens.FontUi + 1, FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap };
            noteText = new TextBlock { FontSize = UiTokens.FontSmall - 0.5, TextWrapping = TextWrapping.Wrap, Text = NetworkLedger.UntrackedNote, Margin = new Thickness(0, 4, 0, 10), MaxWidth = 820, HorizontalAlignment = HorizontalAlignment.Left };

            offlineBox = new CheckBox { Content = "Offline Mode (block all non-local requests)", IsChecked = NetworkLedger.OfflineMode, FontSize = UiTokens.FontUi, VerticalAlignment = VerticalAlignment.Center };
            offlineBox.IsCheckedChanged += (s, e) => NetworkLedger.SetOfflineMode(offlineBox.IsChecked == true);
            filterBox = new TextBox { Watermark = "Filter (feature, host, path, status, blocked)", FontSize = UiTokens.FontUi, Width = 280, Padding = new Thickness(8, 3), VerticalAlignment = VerticalAlignment.Center };
            filterBox.PropertyChanged += (s, e) => { if (e.Property == TextBox.TextProperty) RefreshNow(); };
            clearBtn = new Button { Content = "Clear", FontSize = UiTokens.FontSmall, Padding = new Thickness(10, 3), MinHeight = UiTokens.ControlHeight, VerticalAlignment = VerticalAlignment.Center };
            exportJsonBtn = new Button { Content = "Export JSON…", FontSize = UiTokens.FontSmall, Padding = new Thickness(10, 3), MinHeight = UiTokens.ControlHeight, VerticalAlignment = VerticalAlignment.Center };
            exportCsvBtn = new Button { Content = "Export CSV…", FontSize = UiTokens.FontSmall, Padding = new Thickness(10, 3), MinHeight = UiTokens.ControlHeight, VerticalAlignment = VerticalAlignment.Center };
            clearBtn.Click += (s, e) => NetworkLedger.Clear();
            exportJsonBtn.Click += (s, e) => _ = ExportAsync(true);
            exportCsvBtn.Click += (s, e) => _ = ExportAsync(false);

            var toolbar = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"), Margin = new Thickness(0, 0, 0, 8) };
            toolbar.Children.Add(offlineBox);
            var right = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
            right.Children.Add(filterBox);
            right.Children.Add(clearBtn);
            right.Children.Add(exportJsonBtn);
            right.Children.Add(exportCsvBtn);
            Grid.SetColumn(right, 2);
            toolbar.Children.Add(right);

            headerGrid = new Grid { ColumnDefinitions = new ColumnDefinitions(ColumnWidths) };
            for (int i = 0; i < ColumnNames.Length; i++)
            {
                var tb = new TextBlock { Text = ColumnNames[i].ToUpperInvariant(), FontSize = UiTokens.FontHeader, FontWeight = FontWeight.SemiBold, Margin = new Thickness(6, 6), TextTrimming = TextTrimming.CharacterEllipsis, Tag = "header" };
                Grid.SetColumn(tb, i);
                headerGrid.Children.Add(tb);
            }
            headerBorder = new Border { Child = headerGrid, BorderThickness = new Thickness(0, 0, 0, 1) };

            table = new ListBox { Background = Brushes.Transparent, BorderThickness = new Thickness(0), Padding = new Thickness(0) };
            table.ItemTemplate = new FuncDataTemplate<NetworkLedgerEntry>((entry, _) => BuildRow(entry), supportsRecycling: false);
            emptyText = new TextBlock { Text = "No requests yet. Entries appear here as codeLess contacts the network.", FontSize = 12, Margin = new Thickness(8, 12), IsVisible = false };

            var tableGrid = new Grid { RowDefinitions = new RowDefinitions("Auto,*") };
            tableGrid.Children.Add(headerBorder);
            var tableBody = new Panel();
            tableBody.Children.Add(table);
            tableBody.Children.Add(emptyText);
            Grid.SetRow(tableBody, 1);
            tableGrid.Children.Add(tableBody);
            tableBorder = new Border { Child = tableGrid, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(4) };

            totalsStack = new StackPanel { Spacing = 1 };
            var totalsScroll = new ScrollViewer { Content = totalsStack, MaxHeight = 150 };
            var totalsOuter = new StackPanel { Spacing = 4 };
            totalsOuter.Children.Add(new TextBlock { Text = "Totals per feature", FontSize = 11.5, FontWeight = FontWeight.SemiBold, Tag = "secondary" });
            totalsOuter.Children.Add(totalsScroll);
            totalsBorder = new Border { Child = totalsOuter, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(4), Padding = new Thickness(8, 6), Margin = new Thickness(0, 8, 0, 0) };

            var root = new Grid { RowDefinitions = new RowDefinitions("Auto,Auto,Auto,*,Auto") };
            root.Children.Add(summaryText);
            Grid.SetRow(noteText, 1);
            root.Children.Add(noteText);
            Grid.SetRow(toolbar, 2);
            root.Children.Add(toolbar);
            Grid.SetRow(tableBorder, 3);
            root.Children.Add(tableBorder);
            Grid.SetRow(totalsBorder, 4);
            root.Children.Add(totalsBorder);
            Child = root;

            ApplyTheme(this.theme);
            RefreshNow();
        }

        protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
        {
            base.OnAttachedToVisualTree(e);
            if (attached) return;
            attached = true;
            uiPost = UiPost.CaptureCurrent();
            NetworkLedger.EntryAdded += OnEntryAdded;
            NetworkLedger.Cleared += OnLedgerChanged;
            NetworkLedger.InFlightChanged += OnInFlightChanged;
            NetworkLedger.OfflineModeChanged += OnOfflineModeChanged;
            RefreshNow();
        }

        protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
        {
            base.OnDetachedFromVisualTree(e);
            if (!attached) return;
            attached = false;
            NetworkLedger.EntryAdded -= OnEntryAdded;
            NetworkLedger.Cleared -= OnLedgerChanged;
            NetworkLedger.InFlightChanged -= OnInFlightChanged;
            NetworkLedger.OfflineModeChanged -= OnOfflineModeChanged;
        }

        private void OnEntryAdded(NetworkLedgerEntry e) => QueueRefresh();
        private void OnLedgerChanged() => QueueRefresh();
        private void OnInFlightChanged(int n) => QueueRefresh();

        private void OnOfflineModeChanged(bool offline)
        {
            uiPost.Post(() =>
            {
                if ((offlineBox.IsChecked == true) != offline) offlineBox.IsChecked = offline;
                RefreshNow();
            });
        }

        private void QueueRefresh()
        {
            // Ledger events arrive on background threads (AI / update / OpenVSX requests): marshal first, then coalesce
            // bursts (e.g. streaming) into one refresh per ~150 ms.
            uiPost.Post(() =>
            {
                if (refreshQueued || !attached) return;
                refreshQueued = true;
                DispatcherTimer.RunOnce(() => { refreshQueued = false; if (attached) RefreshNow(); }, TimeSpan.FromMilliseconds(150));
            });
        }

        public void ApplyTheme(StudioTheme t)
        {
            if (t == null) return;
            theme = t;
            Background = AvaloniaColorHelper.ToBrush(t.EditorBg);
            var primary = AvaloniaColorHelper.ToBrush(t.TextPrimary);
            var secondary = AvaloniaColorHelper.ToBrush(t.TextSecondary);
            var border = AvaloniaColorHelper.ToBrush(t.BorderSubtle);
            summaryText.Foreground = primary;
            noteText.Foreground = secondary;
            offlineBox.Foreground = primary;
            emptyText.Foreground = secondary;
            foreach (var c in headerGrid.Children.OfType<TextBlock>()) c.Foreground = secondary;
            headerBorder.Background = AvaloniaColorHelper.ToBrush(t.SidebarBg);
            headerBorder.BorderBrush = border;
            tableBorder.BorderBrush = border;
            tableBorder.Background = AvaloniaColorHelper.ToBrush(t.SidebarBg);
            totalsBorder.BorderBrush = border;
            totalsBorder.Background = AvaloniaColorHelper.ToBrush(t.SidebarBg);
            if (totalsBorder.Child is StackPanel sp)
                foreach (var tb in sp.Children.OfType<TextBlock>()) tb.Foreground = secondary;
            RefreshNow();
        }

        public void RefreshNow()
        {
            string filter = filterBox?.Text ?? "";
            var all = NetworkLedger.Snapshot();
            var list = new List<NetworkLedgerEntry>(all.Count);
            for (int i = all.Count - 1; i >= 0; i--)
                if (NetworkLedger.MatchesFilter(all[i], filter)) list.Add(all[i]);
            VisibleEntries = list;
            table.ItemsSource = list;
            emptyText.IsVisible = list.Count == 0;
            if (list.Count == 0 && all.Count > 0) emptyText.Text = "No entries match the filter.";
            else emptyText.Text = "No requests yet. Entries appear here as codeLess contacts the network.";

            int inFlight = NetworkLedger.InFlightCount;
            summaryText.Text = NetworkLedger.GetSummary() + (inFlight > 0 ? $" · {inFlight} in flight" : "");
            RebuildTotals();
        }

        private void RebuildTotals()
        {
            totalsStack.Children.Clear();
            var primary = AvaloniaColorHelper.ToBrush(theme.TextPrimary);
            var secondary = AvaloniaColorHelper.ToBrush(theme.TextSecondary);
            foreach (var t in NetworkLedger.GetFeatureTotals())
            {
                string detail = $"{t.Requests} req" + (t.Blocked > 0 ? $" ({t.Blocked} blocked)" : "") +
                                $" · ↑ {NetworkLedger.FormatBytes(t.BytesSent)} · ↓ {NetworkLedger.FormatBytes(t.BytesReceived)} · {t.DurationMs} ms" +
                                (t.Hosts.Count > 0 ? " · " + string.Join(", ", t.Hosts) : "");
                totalsStack.Children.Add(TotalsRow(t.Feature, detail, primary, secondary, false));
            }
            string session = $"{NetworkLedger.SessionRequestCount} req" +
                             (NetworkLedger.SessionBlockedCount > 0 ? $" ({NetworkLedger.SessionBlockedCount} blocked)" : "") +
                             $" · ↑ {NetworkLedger.FormatBytes(NetworkLedger.SessionBytesSent)} · ↓ {NetworkLedger.FormatBytes(NetworkLedger.SessionBytesReceived)}" +
                             $" · {NetworkLedger.TelemetryRequestCount} telemetry";
            totalsStack.Children.Add(TotalsRow("Session total", session, primary, secondary, true));
        }

        private static Control TotalsRow(string feature, string detail, IBrush primary, IBrush secondary, bool bold)
        {
            var g = new Grid { ColumnDefinitions = new ColumnDefinitions("240,*") };
            g.Children.Add(new TextBlock { Text = feature, FontSize = 11.5, Foreground = primary, FontWeight = bold ? FontWeight.Bold : FontWeight.Normal, TextTrimming = TextTrimming.CharacterEllipsis });
            var d = new TextBlock { Text = detail, FontSize = 11.5, Foreground = bold ? primary : secondary, FontWeight = bold ? FontWeight.Bold : FontWeight.Normal, TextTrimming = TextTrimming.CharacterEllipsis };
            Grid.SetColumn(d, 1);
            g.Children.Add(d);
            return g;
        }

        private Control BuildRow(NetworkLedgerEntry e)
        {
            var g = new Grid { ColumnDefinitions = new ColumnDefinitions(ColumnWidths), Margin = new Thickness(-8, -2) };
            if (e == null) return g;
            // Only the Status / Blocked cells carry the error / warning color; the rest of the row stays neutral.
            IBrush fg = AvaloniaColorHelper.ToBrush(theme.TextPrimary);
            IBrush statusFg = fg;
            if (e.Blocked) statusFg = AvaloniaColorHelper.ToBrush(theme.WarnAmber);
            else if (e.Status <= 0 || e.Status >= 400) statusFg = AvaloniaColorHelper.ToBrush(theme.DangerCoral);
            string[] cells =
            {
                e.TimestampUtc.ToLocalTime().ToString("HH:mm:ss", CultureInfo.InvariantCulture),
                e.Feature,
                e.Method,
                e.Host,
                e.Path,
                NetworkLedger.FormatBytes(e.BytesSent),
                NetworkLedger.FormatBytes(e.BytesReceived),
                NetworkLedger.FormatStatus(e),
                e.Blocked ? "" : e.DurationMs.ToString(CultureInfo.InvariantCulture) + " ms",
                e.Blocked ? "yes" : ""
            };
            for (int i = 0; i < cells.Length; i++)
            {
                var tb = new TextBlock { Text = cells[i] ?? "", FontSize = UiTokens.FontSmall, Foreground = (i == 7 || i == 9) ? statusFg : fg, Margin = new Thickness(6, 2), TextTrimming = TextTrimming.CharacterEllipsis };
                Grid.SetColumn(tb, i);
                g.Children.Add(tb);
            }
            ToolTip.SetTip(g, e.Url + (string.IsNullOrEmpty(e.Note) ? "" : "\n" + e.Note));
            return g;
        }

        private async Task ExportAsync(bool json)
        {
            string suggested = "codeless-network-activity-" + DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) + (json ? ".json" : ".csv");
            string path = null;
            try
            {
                if (PickSavePath != null) path = await PickSavePath(suggested, json ? "json" : "csv");
                else
                {
                    var top = TopLevel.GetTopLevel(this);
                    if (top?.StorageProvider == null) return;
                    var file = await top.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
                    {
                        Title = "Export Network Activity — codeLess",
                        SuggestedFileName = suggested,
                        DefaultExtension = json ? "json" : "csv"
                    });
                    path = file?.TryGetLocalPath();
                }
                if (string.IsNullOrEmpty(path)) return;
                var rows = new List<NetworkLedgerEntry>(VisibleEntries);
                rows.Reverse();
                File.WriteAllText(path, json ? NetworkLedger.ExportJson(rows) : NetworkLedger.ExportCsv(rows), new UTF8Encoding(false));
                summaryText.Text = NetworkLedger.GetSummary() + " · exported to " + Path.GetFileName(path);
            }
            catch (Exception ex)
            {
                summaryText.Text = "Could not export: " + ex.Message;
            }
        }
    }

    /// <summary>Non-modal window hosting <see cref="NetworkActivityView"/> (stays open beside the workbench).</summary>
    public class NetworkActivityWindow : Window
    {
        public NetworkActivityView View { get; }

        public NetworkActivityWindow(StudioTheme theme)
        {
            Title = "Network Activity — codeLess";
            Width = 1040;
            Height = 620;
            MinWidth = 640;
            MinHeight = 360;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            View = new NetworkActivityView(theme);
            Content = View;
            ApplyTheme(theme);
            KeyDown += (s, e) =>
            {
                if (e.Key == Key.Escape) { Close(); e.Handled = true; }
            };
        }

        public void ApplyTheme(StudioTheme theme)
        {
            if (theme == null) return;
            bool isLight = AvaloniaColorHelper.IsLightColor(theme.EditorBg);
            RequestedThemeVariant = isLight ? global::Avalonia.Styling.ThemeVariant.Light : global::Avalonia.Styling.ThemeVariant.Dark;
            Background = AvaloniaColorHelper.ToBrush(theme.EditorBg);
            View.ApplyTheme(theme);
        }
    }
}
