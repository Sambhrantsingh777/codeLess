using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using codeLess.Studio;

namespace codeLess.AvaloniaApp
{
    /// <summary>Virtualized commit list with natively drawn graph lanes, ref badges, author and relative date.</summary>
    public class GitGraphRowsControl : VirtualRowsControl
    {
        public readonly List<GitCommitInfo> Commits = new List<GitCommitInfo>();
        public readonly List<GitGraphRow> Graph = new List<GitGraphRow>();
        public bool ShowGraph = true;
        public string EmptyText = "Loading history…";
        private const double LaneW = 14;

        private static readonly Color[] LaneColors =
        {
            Color.FromRgb(25, 120, 229), Color.FromRgb(234, 88, 12), Color.FromRgb(22, 163, 74), Color.FromRgb(192, 38, 211),
            Color.FromRgb(220, 38, 38), Color.FromRgb(13, 148, 136), Color.FromRgb(202, 138, 4), Color.FromRgb(99, 102, 241)
        };

        public GitGraphRowsControl() { RowHeight = 24; }

        public override int RowCount => Commits.Count;

        public void Clear()
        {
            Commits.Clear();
            Graph.Clear();
            ResetRows();
        }

        public void Append(List<GitCommitInfo> commits, List<GitGraphRow> rows)
        {
            Commits.AddRange(commits);
            Graph.AddRange(rows);
            InvalidateVisual();
        }

        protected override void DrawEmpty(DrawingContext ctx)
        {
            ctx.DrawText(Text(EmptyText, DevToolsFonts.Ui, 12.5, AvaloniaColorHelper.ToBrush(Theme.FgMuted)), new Point(16, 14));
        }

        private static Color LaneColor(int idx) => LaneColors[((idx % LaneColors.Length) + LaneColors.Length) % LaneColors.Length];

        protected override void DrawRow(DrawingContext ctx, int index, Rect r, bool selected)
        {
            var c = Commits[index];
            if (selected) ctx.FillRectangle(AvaloniaColorHelper.ToBrush(Theme.BgSelection), r);
            else if (index % 2 == 1) ctx.FillRectangle(AvaloniaColorHelper.ToBrush(Theme.BgHover, 40), r);
            double x = r.X + 8;
            double cy = r.Y + r.Height / 2;
            if (ShowGraph && index < Graph.Count)
            {
                var g = Graph[index];
                int lanes = Math.Min(g.LaneCount, 24);
                foreach (var s in g.Segments)
                {
                    double fx = x + s.FromLane * LaneW + LaneW / 2, tx = x + s.ToLane * LaneW + LaneW / 2;
                    var pen = new Pen(new SolidColorBrush(LaneColor(s.Color)), 1.8);
                    if (s.Kind == GitGraphSegment.TopToBottom) ctx.DrawLine(pen, new Point(fx, r.Y), new Point(tx, r.Bottom));
                    else if (s.Kind == GitGraphSegment.TopToCenter) DrawCurve(ctx, pen, new Point(fx, r.Y), new Point(tx, cy));
                    else DrawCurve(ctx, pen, new Point(fx, cy), new Point(tx, r.Bottom));
                }
                double nx = x + g.NodeLane * LaneW + LaneW / 2;
                var nodeBrush = new SolidColorBrush(LaneColor(g.NodeColor));
                if (c.IsMerge) ctx.DrawEllipse(AvaloniaColorHelper.ToBrush(Theme.BgEditor), new Pen(nodeBrush, 2), new Point(nx, cy), 4, 4);
                else ctx.DrawEllipse(nodeBrush, new Pen(AvaloniaColorHelper.ToBrush(Theme.BgEditor), 1.2), new Point(nx, cy), 4.5, 4.5);
                x += lanes * LaneW + 8;
            }
            else
            {
                ctx.DrawEllipse(AvaloniaColorHelper.ToBrush(Theme.AccentPrimary), null, new Point(x + 6, cy), 4, 4);
                x += 20;
            }
            double right = r.Right;
            double dateW = 104, authorW = Math.Min(150, Math.Max(80, r.Width * 0.16)), hashW = 70;
            bool compact = r.Width < 560;
            if (compact) { authorW = 0; hashW = 0; }
            double msgRight = right - dateW - authorW - hashW - 8;
            // Ref badges
            foreach (var rf in c.Refs)
            {
                if (x > msgRight - 40) break;
                Color bc = rf.Kind == GitRefBadge.KindTag ? Color.FromRgb(202, 138, 4) : (rf.Kind == GitRefBadge.KindRemote ? Color.FromRgb(100, 116, 139) : AvaloniaColorHelper.ToAvColor(Theme.AccentPrimary));
                string label = (rf.Kind == GitRefBadge.KindTag ? "🏷 " : "") + rf.Name;
                var ft = Text(label, rf.IsCurrent ? DevToolsFonts.UiBold : DevToolsFonts.Ui, 10.5, Brushes.White);
                double bw = Math.Min(ft.Width + 10, 180);
                ft.MaxTextWidth = bw - 8; ft.MaxLineCount = 1; ft.Trimming = TextTrimming.CharacterEllipsis;
                var br = new Rect(x, r.Y + 4, bw, r.Height - 8);
                ctx.DrawRectangle(new SolidColorBrush(bc, rf.IsCurrent ? 1.0 : 0.8), rf.IsCurrent ? new Pen(Brushes.White, 1) : null, br, 3, 3);
                ctx.DrawText(ft, new Point(br.X + 5, br.Y + (br.Height - ft.Height) / 2));
                x += bw + 4;
            }
            DrawClipped(ctx, c.Subject, DevToolsFonts.Ui, 12.5, AvaloniaColorHelper.ToBrush(Theme.FgPrimary), x + 2, r, msgRight - x);
            double ax = msgRight + 8;
            if (authorW > 0) DrawClipped(ctx, c.AuthorName, DevToolsFonts.Ui, 12, AvaloniaColorHelper.ToBrush(Theme.FgSecondary), ax, r, authorW - 6);
            DrawClipped(ctx, GitCli.FormatRelativeTime(c.AuthorTimeUtc, DateTime.UtcNow), DevToolsFonts.Ui, 12, AvaloniaColorHelper.ToBrush(Theme.FgMuted), ax + authorW, r, dateW - 6);
            if (hashW > 0) DrawClipped(ctx, c.ShortHash, DevToolsFonts.Mono, 11.5, AvaloniaColorHelper.ToBrush(Theme.FgMuted), ax + authorW + dateW, r, hashW);
        }

        private static void DrawCurve(DrawingContext ctx, Pen pen, Point a, Point b)
        {
            if (Math.Abs(a.X - b.X) < 0.5) { ctx.DrawLine(pen, a, b); return; }
            var geo = new StreamGeometry();
            using (var g = geo.Open())
            {
                g.BeginFigure(a, false);
                double my = (a.Y + b.Y) / 2;
                g.CubicBezierTo(new Point(a.X, my), new Point(b.X, my), b);
                g.EndFigure(false);
            }
            ctx.DrawGeometry(null, pen, geo);
        }
    }

    /// <summary>
    /// Git Graph / File History document: toolbar (filter, all-branches, refresh), the commit list (loaded page by page as
    /// you scroll) and a details pane with the commit message and changed files (click a file to open its diff).
    /// </summary>
    public class GitGraphView : Grid
    {
        public string RepoRoot { get; }
        public string FilePathFilter { get; }
        public GitGraphRowsControl List { get; } = new GitGraphRowsControl();
        private readonly TextBox filterBox = new TextBox { Watermark = "Filter by message or author", FontSize = 12, MinWidth = 180, Height = 26, Padding = new Thickness(6, 3) };
        private readonly CheckBox allBranches = new CheckBox { Content = "All branches", IsChecked = true, FontSize = 12, Margin = new Thickness(8, 0) };
        private readonly TextBlock statusText = new TextBlock { FontSize = 11.5, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0) };
        private readonly Border toolbar;
        private readonly Border detailsBorder;
        private readonly StackPanel detailsPanel = new StackPanel { Spacing = 6, Margin = new Thickness(12, 10) };
        private readonly ListBox filesList = new ListBox { Background = Brushes.Transparent, BorderThickness = new Thickness(0) };
        private StudioTheme theme = StudioTheme.GetById(StudioTheme.DefaultThemeId);
        private GitGraphLayout layout = new GitGraphLayout();
        private List<string> remotes;
        private int generation;
        private bool loading, exhausted;
        private string currentFilter = "";
        private DispatcherTimer filterDebounce;
        private string pendingSelectHash;
        private int pendingSelectPages;
        private GitCommitInfo selectedCommit;
        private List<GitChangedFile> selectedFiles = new List<GitChangedFile>();

        public event Action<GitCommitInfo, GitChangedFile> FileDiffRequested;
        public Action<string> CopyToClipboard;
        public GitCommitInfo SelectedCommit => selectedCommit;
        public IReadOnlyList<GitChangedFile> SelectedFiles => selectedFiles;
        public bool IsLoading => loading;
        public string StatusText => statusText.Text ?? "";

        private readonly UiPost ui = UiPost.CaptureCurrent();

        public GitGraphView(string repoRoot, string repoRelativeFile)
        {
            RepoRoot = repoRoot;
            FilePathFilter = repoRelativeFile;
            RowDefinitions = new RowDefinitions("34,*");
            ColumnDefinitions = new ColumnDefinitions("*,4,320");
            List.ShowGraph = string.IsNullOrEmpty(repoRelativeFile);

            var refreshBtn = new Button { Content = new VectorIconControl { IconName = "refresh", Width = 13, Height = 13 }, Width = 26, Height = 24, Padding = new Thickness(0), Background = Brushes.Transparent };
            ToolTip.SetTip(refreshBtn, "Refresh");
            refreshBtn.Click += (s, e) => Reload();
            var tg = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,Auto,Auto,*"), Margin = new Thickness(8, 4) };
            tg.Children.Add(filterBox);
            Grid.SetColumn(allBranches, 1); tg.Children.Add(allBranches);
            Grid.SetColumn(refreshBtn, 2); tg.Children.Add(refreshBtn);
            Grid.SetColumn(statusText, 3); tg.Children.Add(statusText);
            allBranches.IsVisible = List.ShowGraph;
            toolbar = new Border { Child = tg, BorderThickness = new Thickness(0, 0, 0, 1) };
            Grid.SetColumnSpan(toolbar, 3);
            Children.Add(toolbar);

            Grid.SetRow(List, 1);
            Children.Add(List);
            var splitter = new GridSplitter { Width = 4, Background = Brushes.Transparent, ResizeDirection = GridResizeDirection.Columns };
            Grid.SetRow(splitter, 1); Grid.SetColumn(splitter, 1);
            Children.Add(splitter);

            var detailsGrid = new Grid { RowDefinitions = new RowDefinitions("Auto,*") };
            detailsGrid.Children.Add(new ScrollViewer { Content = detailsPanel, MaxHeight = 320 });
            Grid.SetRow(filesList, 1);
            detailsGrid.Children.Add(filesList);
            detailsBorder = new Border { Child = detailsGrid, BorderThickness = new Thickness(1, 0, 0, 0) };
            Grid.SetRow(detailsBorder, 1); Grid.SetColumn(detailsBorder, 2);
            Children.Add(detailsBorder);

            List.SelectionChanged += idx => ShowDetails(idx >= 0 && idx < List.Commits.Count ? List.Commits[idx] : null);
            List.NearEnd += () => LoadMore();
            filesList.SelectionChanged += (s, e) =>
            {
                int i = filesList.SelectedIndex;
                if (i >= 0 && i < selectedFiles.Count && selectedCommit != null) FileDiffRequested?.Invoke(selectedCommit, selectedFiles[i]);
            };
            filterDebounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(350) };
            filterDebounce.Tick += (s, e) => { filterDebounce.Stop(); currentFilter = (filterBox.Text ?? "").Trim(); Reload(); };
            filterBox.TextChanged += (s, e) => { filterDebounce.Stop(); filterDebounce.Start(); };
            allBranches.IsCheckedChanged += (s, e) => Reload();
            ShowDetails(null);
            Reload();
        }

        public void Reload()
        {
            generation++;
            layout = new GitGraphLayout();
            List.Clear();
            List.ShowGraph = string.IsNullOrEmpty(FilePathFilter) && string.IsNullOrEmpty(currentFilter);
            List.EmptyText = "Loading history…";
            exhausted = false;
            loading = false;
            LoadMore();
        }

        public void LoadMore()
        {
            if (loading || exhausted) return;
            loading = true;
            int gen = generation;
            int skip = List.Commits.Count;
            string filter = currentFilter;
            bool all = allBranches.IsChecked == true;
            var lay = layout;
            statusText.Text = skip == 0 ? "Loading…" : "Loading more…";
            Task.Run(() =>
            {
                if (remotes == null) remotes = GitHistoryService.GetRemoteNames(RepoRoot);
                string err;
                var page = GitHistoryService.LoadPage(RepoRoot, skip, GitHistoryService.PageSize, filter, FilePathFilter, all, remotes, out err);
                var rows = new List<GitGraphRow>();
                lock (lay) foreach (var c in page) rows.Add(lay.AddCommit(c.Hash, c.Parents));
                ui.Post(() =>
                {
                    if (gen != generation) return;
                    loading = false;
                    if (page.Count < GitHistoryService.PageSize) exhausted = true;
                    List.Append(page, rows);
                    if (List.Commits.Count == 0) List.EmptyText = string.IsNullOrEmpty(err) ? (string.IsNullOrEmpty(filter) ? "No commits." : "No commits match \"" + filter + "\".") : err;
                    statusText.Text = List.Commits.Count.ToString(CultureInfo.InvariantCulture) + (exhausted ? "" : "+") + " commits" + (string.IsNullOrEmpty(FilePathFilter) ? "" : " touching " + FilePathFilter);
                    if (List.SelectedIndex < 0 && List.Commits.Count > 0 && pendingSelectHash == null) List.Select(0);
                    TryResolvePendingSelection();
                    List.InvalidateVisual();
                });
            });
        }

        /// <summary>Selects a commit by (full or abbreviated) hash, loading more pages if needed.</summary>
        public void SelectCommit(string hash)
        {
            pendingSelectHash = hash;
            pendingSelectPages = 0;
            TryResolvePendingSelection();
        }

        private void TryResolvePendingSelection()
        {
            if (pendingSelectHash == null) return;
            int idx = List.Commits.FindIndex(c => c.Hash.StartsWith(pendingSelectHash, StringComparison.OrdinalIgnoreCase));
            if (idx >= 0) { pendingSelectHash = null; List.Select(idx); List.ScrollTo(Math.Max(0, (idx - 5) * List.RowHeight)); return; }
            if (!exhausted && pendingSelectPages++ < 25) { if (!loading) LoadMore(); return; }
            pendingSelectHash = null;
        }

        private void ShowDetails(GitCommitInfo c)
        {
            selectedCommit = c;
            selectedFiles = new List<GitChangedFile>();
            filesList.ItemsSource = null;
            detailsPanel.Children.Clear();
            if (c == null)
            {
                detailsPanel.Children.Add(Muted("Select a commit to see its details and changed files."));
                return;
            }
            var hashRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
            hashRow.Children.Add(new SelectableTextBlock { Text = c.Hash, FontFamily = new FontFamily(DevToolsFonts.MonoFamily), FontSize = 11.5, Foreground = AvaloniaColorHelper.ToBrush(theme.FgSecondary), VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap, MaxWidth = 220 });
            var copy = new Button { Content = "Copy", FontSize = 10.5, Padding = new Thickness(6, 1) };
            copy.Click += (s, e) => CopyToClipboard?.Invoke(c.Hash);
            hashRow.Children.Add(copy);
            detailsPanel.Children.Add(hashRow);
            detailsPanel.Children.Add(new SelectableTextBlock { Text = c.Subject, FontWeight = FontWeight.SemiBold, FontSize = 13, TextWrapping = TextWrapping.Wrap, Foreground = AvaloniaColorHelper.ToBrush(theme.FgPrimary) });
            detailsPanel.Children.Add(Muted(c.AuthorName + " <" + c.AuthorEmail + ">"));
            detailsPanel.Children.Add(Muted(c.AuthorTimeUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) + "  (" + GitCli.FormatRelativeTime(c.AuthorTimeUtc, DateTime.UtcNow) + ")"));
            if (c.Refs.Count > 0) detailsPanel.Children.Add(Muted(string.Join(", ", c.Refs.Select(r => r.Name))));
            if (c.IsMerge) detailsPanel.Children.Add(Muted("Merge of " + string.Join(", ", c.Parents.Select(GitCli.ShortHash)) + " — files shown against the first parent"));
            var body = new SelectableTextBlock { TextWrapping = TextWrapping.Wrap, FontSize = 12, Foreground = AvaloniaColorHelper.ToBrush(theme.FgSecondary) };
            detailsPanel.Children.Add(body);
            var filesHeader = new TextBlock { Text = "CHANGED FILES", FontSize = 11, FontWeight = FontWeight.Bold, Margin = new Thickness(0, 6, 0, 0), Foreground = AvaloniaColorHelper.ToBrush(theme.FgMuted) };
            detailsPanel.Children.Add(filesHeader);
            var captured = c;
            Task.Run(() =>
            {
                string err;
                var files = GitHistoryService.GetChangedFiles(RepoRoot, captured, out err);
                string msg = GitHistoryService.GetFullMessage(RepoRoot, captured.Hash);
                ui.Post(() =>
                {
                    if (!ReferenceEquals(selectedCommit, captured)) return;
                    int nl = msg.IndexOf('\n');
                    body.Text = nl >= 0 ? msg.Substring(nl + 1).Trim() : "";
                    body.IsVisible = body.Text.Length > 0;
                    selectedFiles = files;
                    filesHeader.Text = "CHANGED FILES (" + files.Count + ")" + (err.Length > 0 ? " — " + err : "");
                    filesList.ItemsSource = files.Select(f => new TextBlock
                    {
                        Text = f.Status + "   " + f.DisplayName,
                        FontSize = 12,
                        Foreground = new SolidColorBrush(StatusColor(f.Status)),
                        TextTrimming = TextTrimming.CharacterEllipsis
                    }).ToList();
                });
            });
        }

        private Color StatusColor(string st)
        {
            switch (st)
            {
                case "A": return Color.FromRgb(46, 160, 67);
                case "D": return Color.FromRgb(220, 60, 60);
                case "R": return Color.FromRgb(37, 130, 235);
                default: return AvaloniaColorHelper.ToAvColor(theme.FgPrimary);
            }
        }

        private TextBlock Muted(string s) => new TextBlock { Text = s, FontSize = 11.5, TextWrapping = TextWrapping.Wrap, Foreground = AvaloniaColorHelper.ToBrush(theme.FgMuted) };

        public void ApplyTheme(StudioTheme t)
        {
            theme = t;
            Background = AvaloniaColorHelper.ToBrush(t.BgEditor);
            toolbar.Background = AvaloniaColorHelper.ToBrush(t.BgSidebar);
            toolbar.BorderBrush = AvaloniaColorHelper.ToBrush(t.BorderSubtle);
            detailsBorder.Background = AvaloniaColorHelper.ToBrush(t.BgSidebar);
            detailsBorder.BorderBrush = AvaloniaColorHelper.ToBrush(t.BorderSubtle);
            statusText.Foreground = AvaloniaColorHelper.ToBrush(t.FgMuted);
            allBranches.Foreground = AvaloniaColorHelper.ToBrush(t.FgSecondary);
            List.ApplyTheme(t);
            ShowDetails(selectedCommit);
        }
    }
}
