using System;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using codeLess.Studio;

namespace codeLess.AvaloniaApp
{
    /// <summary>
    /// Read-only paged viewer for huge files (Avalonia). Renders only the visible lines of a LargeFileDocument,
    /// with a banner (size / encoding / indexing progress, Follow, Find, Go to Line, Open in Editor), a streaming
    /// find bar with match count and navigation, keyboard + mouse selection and clipboard copy. Word wrap is always off.
    /// </summary>
    public class AvaloniaLargeFileViewer : Grid
    {
        private readonly Border banner;
        private readonly TextBlock bannerTitle;
        private readonly TextBlock bannerStatus;
        private readonly ToggleButton followBtn;
        private readonly Button findBtn;
        private readonly Button gotoBtn;
        private readonly Button editBtn;
        private readonly Border findBar;
        private readonly TextBox findBox;
        private readonly ToggleButton matchCaseBtn;
        private readonly ToggleButton regexBtn;
        private readonly TextBlock findCount;
        private readonly Border gotoBar;
        private readonly TextBox gotoBox;
        private readonly ScrollBar vScroll;
        private readonly ScrollBar hScroll;
        public LargeFileViewport Viewport { get; }

        private StudioTheme theme = StudioTheme.GetById(StudioTheme.DefaultThemeId);
        private DispatcherTimer followTimer;
        private DispatcherTimer searchDebounce;
        private readonly UiPost ui = UiPost.CaptureCurrent();
        private bool pollInFlight;
        private bool pollAgain;
        private bool uiUpdatePending;
        private bool suppressScrollEvents;
        private long editLimitBytes;
        private int activeMatch = -1;
        private bool userNavigatedSinceSearch;

        public LargeFileDocument Document { get; private set; }
        public LargeFileSearch ActiveSearch { get; private set; }
        public int ActiveMatchIndex => activeMatch;
        public string FindStatusText => findCount.Text ?? "";
        public string BannerStatusText => bannerStatus.Text ?? "";
        public bool IsFindBarVisible => findBar.IsVisible;
        public bool IsEditOfferEnabled => editBtn.IsEnabled;

        /// <summary>Raised when the user asks to open the file in the normal (editable) editor.</summary>
        public event Action<string> OpenInEditorRequested;
        public event Action<string> StatusMessagePublished;

        public AvaloniaLargeFileViewer()
        {
            RowDefinitions = new RowDefinitions("Auto,Auto,Auto,*,Auto");
            ColumnDefinitions = new ColumnDefinitions("*,Auto");
            ClipToBounds = true;

            // ---- banner
            bannerTitle = new TextBlock { Text = LargeFilePolicy.BannerTitle, FontWeight = FontWeight.SemiBold, FontSize = 12, VerticalAlignment = VerticalAlignment.Center };
            bannerStatus = new TextBlock { FontSize = 11.5, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 0, 0), TextTrimming = TextTrimming.CharacterEllipsis };
            followBtn = new ToggleButton { Content = "Follow (tail -f)", FontSize = 11, Padding = new Thickness(8, 2), Margin = new Thickness(4, 3) };
            findBtn = new Button { Content = "Find", FontSize = 11, Padding = new Thickness(8, 2), Margin = new Thickness(4, 3) };
            gotoBtn = new Button { Content = "Go to Line", FontSize = 11, Padding = new Thickness(8, 2), Margin = new Thickness(4, 3) };
            editBtn = new Button { Content = "Open in Editor", FontSize = 11, Padding = new Thickness(8, 2), Margin = new Thickness(4, 3) };
            ToolTip.SetTip(followBtn, "Keep reading new lines as the file grows; handles truncation and log rotation");
            followBtn.IsCheckedChanged += (s, e) => SetFollow(followBtn.IsChecked == true);
            findBtn.Click += (s, e) => ShowFind();
            gotoBtn.Click += (s, e) => ShowGoTo();
            editBtn.Click += (s, e) => { if (Document != null) OpenInEditorRequested?.Invoke(Document.FilePath); };

            var bannerGrid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto,Auto,Auto,Auto"), Margin = new Thickness(12, 0, 6, 0) };
            bannerGrid.Children.Add(bannerTitle);
            Grid.SetColumn(bannerStatus, 1); bannerGrid.Children.Add(bannerStatus);
            Grid.SetColumn(followBtn, 2); bannerGrid.Children.Add(followBtn);
            Grid.SetColumn(findBtn, 3); bannerGrid.Children.Add(findBtn);
            Grid.SetColumn(gotoBtn, 4); bannerGrid.Children.Add(gotoBtn);
            Grid.SetColumn(editBtn, 5); bannerGrid.Children.Add(editBtn);
            banner = new Border { Child = bannerGrid, BorderThickness = new Thickness(0, 0, 0, 1), MinHeight = 30 };
            Grid.SetRow(banner, 0); Grid.SetColumnSpan(banner, 2);
            Children.Add(banner);

            // ---- find bar
            findBox = new TextBox { Width = 260, FontSize = 12, Watermark = "Find (streaming, whole file)", Padding = new Thickness(6, 2), VerticalContentAlignment = VerticalAlignment.Center };
            matchCaseBtn = new ToggleButton { Content = "Aa", FontSize = 11, Padding = new Thickness(6, 2), Margin = new Thickness(4, 0, 0, 0) };
            regexBtn = new ToggleButton { Content = ".*", FontSize = 11, Padding = new Thickness(6, 2), Margin = new Thickness(2, 0, 0, 0) };
            ToolTip.SetTip(matchCaseBtn, "Match Case");
            ToolTip.SetTip(regexBtn, "Use Regular Expression");
            var prevBtn = new Button { Content = "↑", FontSize = 11, Padding = new Thickness(8, 2), Margin = new Thickness(6, 0, 0, 0) };
            var nextBtn = new Button { Content = "↓", FontSize = 11, Padding = new Thickness(8, 2), Margin = new Thickness(2, 0, 0, 0) };
            var closeFindBtn = new Button { Content = "✕", FontSize = 11, Padding = new Thickness(8, 2), Margin = new Thickness(2, 0, 0, 0) };
            findCount = new TextBlock { FontSize = 11.5, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 0, 0), Text = "" };
            prevBtn.Click += (s, e) => NavigateMatch(-1);
            nextBtn.Click += (s, e) => NavigateMatch(1);
            closeFindBtn.Click += (s, e) => HideFind();
            matchCaseBtn.IsCheckedChanged += (s, e) => StartSearch(findBox.Text, matchCaseBtn.IsChecked == true, regexBtn.IsChecked == true);
            regexBtn.IsCheckedChanged += (s, e) => StartSearch(findBox.Text, matchCaseBtn.IsChecked == true, regexBtn.IsChecked == true);
            findBox.KeyDown += OnFindBoxKeyDown;
            findBox.PropertyChanged += (s, e) =>
            {
                if (e.Property == TextBox.TextProperty && findBar.IsVisible) ScheduleSearch();
            };
            var findRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(12, 4) };
            findRow.Children.Add(findBox);
            findRow.Children.Add(matchCaseBtn);
            findRow.Children.Add(regexBtn);
            findRow.Children.Add(prevBtn);
            findRow.Children.Add(nextBtn);
            findRow.Children.Add(closeFindBtn);
            findRow.Children.Add(findCount);
            findBar = new Border { Child = findRow, BorderThickness = new Thickness(0, 0, 0, 1), IsVisible = false };
            Grid.SetRow(findBar, 1); Grid.SetColumnSpan(findBar, 2);
            Children.Add(findBar);

            // ---- go to line bar
            gotoBox = new TextBox { Width = 180, FontSize = 12, Watermark = "Line number", Padding = new Thickness(6, 2) };
            gotoBox.KeyDown += (s, e) =>
            {
                if (e.Key == Key.Enter)
                {
                    long n;
                    if (long.TryParse((gotoBox.Text ?? "").Split(':')[0].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out n))
                        GoToLine(n);
                    gotoBar.IsVisible = false;
                    Viewport.Focus();
                    e.Handled = true;
                }
                else if (e.Key == Key.Escape)
                {
                    gotoBar.IsVisible = false;
                    Viewport.Focus();
                    e.Handled = true;
                }
            };
            var gotoRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(12, 4) };
            gotoRow.Children.Add(new TextBlock { Text = "Go to line:", FontSize = 11.5, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) });
            gotoRow.Children.Add(gotoBox);
            gotoBar = new Border { Child = gotoRow, BorderThickness = new Thickness(0, 0, 0, 1), IsVisible = false };
            Grid.SetRow(gotoBar, 2); Grid.SetColumnSpan(gotoBar, 2);
            Children.Add(gotoBar);

            // ---- viewport + scrollbars
            Viewport = new LargeFileViewport(this);
            Grid.SetRow(Viewport, 3);
            Children.Add(Viewport);
            vScroll = new ScrollBar { Orientation = Orientation.Vertical, Minimum = 0, AllowAutoHide = false };
            Grid.SetRow(vScroll, 3); Grid.SetColumn(vScroll, 1);
            Children.Add(vScroll);
            hScroll = new ScrollBar { Orientation = Orientation.Horizontal, Minimum = 0, AllowAutoHide = false };
            Grid.SetRow(hScroll, 4);
            Children.Add(hScroll);
            vScroll.PropertyChanged += (s, e) =>
            {
                if (e.Property == RangeBase.ValueProperty && !suppressScrollEvents)
                {
                    Viewport.TopLine = (long)Math.Round(vScroll.Value);
                    Viewport.AutoScrollToEnd = Document != null && Document.Follow && Viewport.IsAtEnd;
                    Viewport.InvalidateVisual();
                }
            };
            hScroll.PropertyChanged += (s, e) =>
            {
                if (e.Property == RangeBase.ValueProperty && !suppressScrollEvents)
                {
                    Viewport.LeftColumn = (int)Math.Round(hScroll.Value);
                    Viewport.InvalidateVisual();
                }
            };

            DetachedFromVisualTree += (s, e) => StopTimers();
            ApplyTheme(theme);
        }

        // ------------------------------------------------------------------ document lifecycle

        public void LoadDocument(LargeFileDocument doc, long editLimit, double fontSize)
        {
            if (ReferenceEquals(doc, Document))
            {
                Viewport.FontSizePx = fontSize;
                RefreshUi();
                return;
            }
            UnloadDocument();
            Document = doc;
            editLimitBytes = editLimit;
            Viewport.FontSizePx = fontSize;
            Viewport.ResetPosition();
            if (doc != null)
            {
                doc.IndexProgressChanged += OnDocChanged;
                doc.ContentAppended += OnDocAppended;
                doc.ContentReset += OnDocReset;
                followBtn.IsChecked = doc.Follow;
                if (doc.Follow) StartFollowTimer();
                long len = doc.FileLength;
                editBtn.IsEnabled = editLimit > 0 && len <= editLimit;
                ToolTip.SetTip(editBtn, editBtn.IsEnabled
                    ? "Load the whole file into the normal editor (uses roughly " + LargeFilePolicy.FormatBytes(len * 5) + " of memory)"
                    : "Too large to edit: the limit is " + LargeFilePolicy.FormatBytes(editLimit) + " (Settings: LargeFileEditLimitMB)");
            }
            RefreshUi();
        }

        public void UnloadDocument()
        {
            CancelSearch();
            StopTimers();
            if (Document != null)
            {
                Document.IndexProgressChanged -= OnDocChanged;
                Document.ContentAppended -= OnDocAppended;
                Document.ContentReset -= OnDocReset;
            }
            Document = null;
            activeMatch = -1;
            findCount.Text = "";
            Viewport.InvalidateVisual();
        }

        private void StopTimers()
        {
            followTimer?.Stop();
            searchDebounce?.Stop();
        }

        private void OnDocChanged(object sender, EventArgs e) => PostUiRefresh();

        private void OnDocAppended(object sender, EventArgs e)
        {
            ui.Post(() =>
            {
                if (Document == null || !ReferenceEquals(sender, Document)) return;
                if (Document.Follow && Viewport.AutoScrollToEnd) Viewport.ScrollToEnd(false);
                RefreshUi();
            });
        }

        private void OnDocReset(object sender, EventArgs e)
        {
            ui.Post(() =>
            {
                if (Document == null || !ReferenceEquals(sender, Document)) return;
                CancelSearch();
                activeMatch = -1;
                findCount.Text = findBar.IsVisible ? "File changed — search again" : "";
                Viewport.ClampPosition();
                if (Document.Follow) Viewport.AutoScrollToEnd = true;
                StatusMessagePublished?.Invoke(System.IO.Path.GetFileName(Document.FilePath) + " was truncated or rotated — re-reading from the start.");
                RefreshUi();
            });
        }

        private void PostUiRefresh()
        {
            if (uiUpdatePending) return;
            uiUpdatePending = true;
            ui.Post(() =>
            {
                uiUpdatePending = false;
                RefreshUi();
            }, DispatcherPriority.Background);
        }

        /// <summary>Updates banner text, scrollbar ranges and repaints (UI thread).</summary>
        public void RefreshUi()
        {
            var doc = Document;
            bannerStatus.Text = doc != null ? LargeFilePolicy.DescribeStatus(doc) : "";
            if (doc == null) { Viewport.InvalidateVisual(); return; }
            suppressScrollEvents = true;
            try
            {
                long lines = doc.LineCount;
                int rows = Math.Max(1, Viewport.VisibleRows);
                vScroll.Maximum = Math.Max(0, lines - rows + 1);
                vScroll.ViewportSize = rows;
                vScroll.LargeChange = Math.Max(1, rows - 1);
                vScroll.SmallChange = 1;
                vScroll.Value = Math.Min(vScroll.Maximum, Viewport.TopLine);
                int cols = Math.Max(1, Viewport.VisibleColumns);
                hScroll.Maximum = Math.Max(0, Viewport.WidestSeenColumns - cols + 4);
                hScroll.ViewportSize = cols;
                hScroll.LargeChange = Math.Max(1, cols - 4);
                hScroll.Value = Math.Min(hScroll.Maximum, Viewport.LeftColumn);
                hScroll.IsVisible = hScroll.Maximum > 0;
            }
            finally { suppressScrollEvents = false; }
            if (ActiveSearch != null) findCount.Text = LargeFilePolicy.DescribeSearch(ActiveSearch, activeMatch);
            Viewport.InvalidateVisual();
        }

        internal void SyncScrollBarsFromViewport() => RefreshUi();

        // ------------------------------------------------------------------ follow (tail -f)

        public bool FollowEnabled => Document != null && Document.Follow;

        public void SetFollow(bool on)
        {
            if (Document == null) return;
            if (followBtn.IsChecked != on) followBtn.IsChecked = on; // re-enters via IsCheckedChanged
            if (Document.Follow == on && (on == (followTimer?.IsEnabled ?? false))) return;
            Document.Follow = on;
            if (on)
            {
                Viewport.AutoScrollToEnd = true;
                StartFollowTimer();
                PollNow();
                Viewport.ScrollToEnd(false);
            }
            else
            {
                followTimer?.Stop();
            }
            RefreshUi();
        }

        private void StartFollowTimer()
        {
            if (followTimer == null)
            {
                followTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
                followTimer.Tick += (s, e) => PollNow();
            }
            followTimer.Start();
        }

        /// <summary>Checks the file for growth / truncation off the UI thread.</summary>
        public void PollNow()
        {
            var doc = Document;
            if (doc == null) return;
            if (pollInFlight) { pollAgain = true; return; }
            pollInFlight = true;
            Task.Run(() =>
            {
                try { doc.PollForChanges(); }
                catch { }
                finally
                {
                    ui.Post(() =>
                    {
                        pollInFlight = false;
                        if (pollAgain) { pollAgain = false; PollNow(); }
                    });
                }
            });
        }

        // ------------------------------------------------------------------ navigation

        /// <summary>1-based go to line. Lines beyond what is indexed so far are clamped (with a status message).</summary>
        public void GoToLine(long oneBased, int oneBasedCol = 1)
        {
            if (Document == null) return;
            long known = Document.LineCount;
            long line = Math.Max(0, oneBased - 1);
            if (line >= known)
            {
                if (!Document.IsIndexComplete)
                    StatusMessagePublished?.Invoke("Line " + oneBased.ToString("N0", CultureInfo.InvariantCulture) + " is not indexed yet (" + (int)(Document.IndexProgress * 100) + "% done) — showing the last indexed line.");
                line = known - 1;
            }
            userNavigatedSinceSearch = true;
            Viewport.SetCaret(line, Math.Max(0, oneBasedCol - 1), false);
            Viewport.CenterOnLine(line);
            Viewport.AutoScrollToEnd = false;
            RefreshUi();
            Viewport.Focus();
        }

        public void ShowGoTo()
        {
            gotoBar.IsVisible = true;
            gotoBox.Text = (Viewport.CaretLine + 1).ToString(CultureInfo.InvariantCulture);
            gotoBox.Focus();
            gotoBox.SelectAll();
        }

        public void ShowFind()
        {
            findBar.IsVisible = true;
            string sel = Viewport.GetSelectedText(4096);
            if (!string.IsNullOrEmpty(sel) && sel.IndexOf('\n') < 0) findBox.Text = sel;
            findBox.Focus();
            findBox.SelectAll();
            if (ActiveSearch == null && !string.IsNullOrEmpty(findBox.Text)) StartSearch(findBox.Text, matchCaseBtn.IsChecked == true, regexBtn.IsChecked == true);
        }

        public void HideFind()
        {
            findBar.IsVisible = false;
            CancelSearch();
            activeMatch = -1;
            findCount.Text = "";
            Viewport.InvalidateVisual();
            Viewport.Focus();
        }

        private void OnFindBoxKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter)
            {
                bool shift = e.KeyModifiers.HasFlag(KeyModifiers.Shift);
                if (ActiveSearch == null || !string.Equals(ActiveSearch.Query, findBox.Text ?? "", StringComparison.Ordinal))
                    StartSearch(findBox.Text, matchCaseBtn.IsChecked == true, regexBtn.IsChecked == true);
                else
                    NavigateMatch(shift ? -1 : 1);
                e.Handled = true;
            }
            else if (e.Key == Key.F3)
            {
                NavigateMatch(e.KeyModifiers.HasFlag(KeyModifiers.Shift) ? -1 : 1);
                e.Handled = true;
            }
            else if (e.Key == Key.Escape)
            {
                HideFind();
                e.Handled = true;
            }
        }

        private void ScheduleSearch()
        {
            if (searchDebounce == null)
            {
                searchDebounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(350) };
                searchDebounce.Tick += (s, e) =>
                {
                    searchDebounce.Stop();
                    StartSearch(findBox.Text, matchCaseBtn.IsChecked == true, regexBtn.IsChecked == true);
                };
            }
            searchDebounce.Stop();
            searchDebounce.Start();
        }

        public void SetFindOptions(bool matchCase, bool regex)
        {
            matchCaseBtn.IsChecked = matchCase;
            regexBtn.IsChecked = regex;
        }

        /// <summary>Starts a streaming search on a background thread (cancelling any previous one).</summary>
        public LargeFileSearch StartSearch(string query, bool matchCase, bool regex)
        {
            searchDebounce?.Stop();
            CancelSearch();
            activeMatch = -1;
            userNavigatedSinceSearch = false;
            if (Document == null || string.IsNullOrEmpty(query))
            {
                findCount.Text = string.IsNullOrEmpty(query) ? "" : "No results";
                Viewport.InvalidateVisual();
                return null;
            }
            if (!findBar.IsVisible) findBar.IsVisible = true;
            if (!string.Equals(findBox.Text, query, StringComparison.Ordinal))
            {
                findBox.Text = query;
                searchDebounce?.Stop();
            }
            var search = new LargeFileSearch(Document, query, matchCase, regex);
            ActiveSearch = search;
            long caretLine = Viewport.CaretLine;
            int caretCol = Viewport.CaretColumn;
            search.Progress += (s, e) => ui.Post(() =>
            {
                if (!ReferenceEquals(ActiveSearch, search)) return;
                if (activeMatch < 0 && !userNavigatedSinceSearch && search.StoredMatchCount > 0)
                {
                    int idx = search.FindNextMatchIndex(caretLine, caretCol);
                    var m = search.GetMatch(idx < 0 ? 0 : idx);
                    // Only jump once a match at/after the caret is known (or the search is done) so results don't flicker.
                    if (m.Line >= caretLine || search.IsCompleted) RevealMatch(idx < 0 ? 0 : idx);
                }
                findCount.Text = LargeFilePolicy.DescribeSearch(search, activeMatch);
                Viewport.InvalidateVisual();
            }, DispatcherPriority.Background);
            findCount.Text = "Searching…";
            search.Start();
            return search;
        }

        public void CancelSearch()
        {
            if (ActiveSearch != null) ActiveSearch.Cancel();
            ActiveSearch = null;
        }

        public void NavigateMatch(int direction)
        {
            var s = ActiveSearch;
            if (s == null)
            {
                if (!string.IsNullOrEmpty(findBox.Text)) StartSearch(findBox.Text, matchCaseBtn.IsChecked == true, regexBtn.IsChecked == true);
                return;
            }
            int count = s.StoredMatchCount;
            if (count == 0) return;
            int idx;
            if (activeMatch < 0)
            {
                idx = s.FindNextMatchIndex(Viewport.CaretLine, Viewport.CaretColumn);
                if (idx < 0) idx = 0;
                if (direction < 0) idx = (idx - 1 + count) % count;
            }
            else
            {
                idx = (activeMatch + direction + count) % count;
            }
            userNavigatedSinceSearch = true;
            RevealMatch(idx);
        }

        private void RevealMatch(int idx)
        {
            var s = ActiveSearch;
            if (s == null || idx < 0 || idx >= s.StoredMatchCount || Document == null) return;
            activeMatch = idx;
            var m = s.GetMatch(idx);
            int col = m.Column >= 0 ? m.Column : Document.ByteOffsetToColumn(m.Line, m.ByteOffset);
            Viewport.SetCaret(m.Line, col, false);
            Viewport.SetSelection(m.Line, col, m.Line, col + m.Length);
            Viewport.RevealLine(m.Line, col, m.Length);
            Viewport.AutoScrollToEnd = false;
            findCount.Text = LargeFilePolicy.DescribeSearch(s, activeMatch);
            RefreshUi();
        }

        // ------------------------------------------------------------------ clipboard

        /// <summary>Copies the selection (capped at 16M characters) to the clipboard and returns the copied text.</summary>
        public string CopySelection()
        {
            bool truncated;
            string text = Viewport.GetSelectedText(LargeFilePolicy.ClipboardCharLimit, out truncated);
            if (string.IsNullOrEmpty(text)) return "";
            try
            {
                var top = TopLevel.GetTopLevel(this);
                if (top?.Clipboard != null) _ = top.Clipboard.SetTextAsync(text);
            }
            catch { }
            if (truncated) StatusMessagePublished?.Invoke("Copied the first " + LargeFilePolicy.ClipboardCharLimit.ToString("N0", CultureInfo.InvariantCulture) + " characters of the selection.");
            return text;
        }

        // ------------------------------------------------------------------ theme

        public StudioTheme Theme => theme;

        public void ApplyTheme(StudioTheme t)
        {
            if (t == null) return;
            theme = t;
            bool light = AvaloniaColorHelper.IsLightColor(t.EditorBg);
            Background = AvaloniaColorHelper.ToBrush(t.EditorBg);
            banner.Background = new SolidColorBrush(light ? Color.FromRgb(254, 243, 199) : Color.FromRgb(58, 46, 18));
            banner.BorderBrush = new SolidColorBrush(light ? Color.FromRgb(217, 119, 6) : Color.FromRgb(180, 120, 30));
            bannerTitle.Foreground = AvaloniaColorHelper.ToBrush(t.TextPrimary);
            bannerStatus.Foreground = AvaloniaColorHelper.ToBrush(t.TextSecondary);
            foreach (var bar in new[] { findBar, gotoBar })
            {
                bar.Background = AvaloniaColorHelper.ToBrush(t.FloatingBg);
                bar.BorderBrush = AvaloniaColorHelper.ToBrush(t.BorderSubtle);
            }
            findCount.Foreground = AvaloniaColorHelper.ToBrush(t.TextSecondary);
            Viewport.InvalidateVisual();
        }
    }

    /// <summary>The virtualised text surface of the large-file viewer: draws only the visible rows.</summary>
    public class LargeFileViewport : Control
    {
        private readonly AvaloniaLargeFileViewer owner;
        private static readonly string MonoFont = RuntimeInformation.IsOSPlatform(OSPlatform.OSX)
            ? "Menlo, Monaco, SF Mono, monospace"
            : "Cascadia Code, Consolas, DejaVu Sans Mono, monospace";
        private static readonly Typeface Font = new Typeface(MonoFont);
        private const int TabSize = 4;

        private double measuredCharWidth;
        private double measuredFontSize = -1;
        private bool dragging;
        private long selAnchorLine = -1;
        private int selAnchorCol;

        public double FontSizePx { get; set; } = 13.0;
        public double LineHeight => Math.Round(FontSizePx * 1.54);
        public double CharWidth => measuredCharWidth > 3 ? measuredCharWidth : Math.Round(FontSizePx * 0.6, 2);
        public long TopLine { get; set; }
        public int LeftColumn { get; set; }
        public long CaretLine { get; private set; }
        public int CaretColumn { get; private set; }
        public bool AutoScrollToEnd { get; set; }
        public int WidestSeenColumns { get; private set; } = 80;
        /// <summary>Number of FormattedText lines drawn in the last frame (tests verify virtualisation).</summary>
        public int LastRenderedLineCount { get; private set; }

        public LargeFileViewport(AvaloniaLargeFileViewer owner)
        {
            this.owner = owner;
            Focusable = true;
            ClipToBounds = true;
            Cursor = new Cursor(StandardCursorType.Ibeam);
        }

        private LargeFileDocument Doc => owner.Document;

        public int VisibleRows => Math.Max(1, (int)(Bounds.Height / LineHeight));
        public int VisibleColumns => Math.Max(1, (int)((Bounds.Width - GutterWidth) / CharWidth));
        public bool IsAtEnd => Doc == null || TopLine >= Math.Max(0, Doc.LineCount - VisibleRows);

        public double GutterWidth
        {
            get
            {
                long lines = Doc != null ? Math.Max(Doc.EstimatedTotalLines, Doc.LineCount) : 1;
                int digits = Math.Max(4, lines.ToString(CultureInfo.InvariantCulture).Length);
                return Math.Ceiling((digits + 3) * CharWidth);
            }
        }

        public void ResetPosition()
        {
            TopLine = 0; LeftColumn = 0; CaretLine = 0; CaretColumn = 0;
            selAnchorLine = -1; AutoScrollToEnd = false; WidestSeenColumns = 80;
        }

        public void ClampPosition()
        {
            if (Doc == null) return;
            long max = Math.Max(0, Doc.LineCount - 1);
            if (CaretLine > max) CaretLine = max;
            if (TopLine > max) TopLine = Math.Max(0, max - VisibleRows + 1);
            if (selAnchorLine > max) selAnchorLine = -1;
        }

        public void ScrollToEnd(bool moveCaret)
        {
            if (Doc == null) return;
            TopLine = Math.Max(0, Doc.LineCount - VisibleRows + 1);
            if (moveCaret) { CaretLine = Doc.LineCount - 1; CaretColumn = 0; }
        }

        public void SetCaret(long line, int col, bool extendSelection)
        {
            if (Doc == null) return;
            long max = Math.Max(0, Doc.LineCount - 1);
            line = Math.Max(0, Math.Min(max, line));
            if (extendSelection)
            {
                if (selAnchorLine < 0) { selAnchorLine = CaretLine; selAnchorCol = CaretColumn; }
            }
            else selAnchorLine = -1;
            CaretLine = line;
            CaretColumn = Math.Max(0, col);
        }

        public void SetSelection(long l1, int c1, long l2, int c2)
        {
            selAnchorLine = l1; selAnchorCol = c1;
            CaretLine = l2; CaretColumn = c2;
        }

        public bool HasSelection => selAnchorLine >= 0 && (selAnchorLine != CaretLine || selAnchorCol != CaretColumn);

        private void GetSelection(out long l1, out int c1, out long l2, out int c2)
        {
            l1 = selAnchorLine; c1 = selAnchorCol; l2 = CaretLine; c2 = CaretColumn;
            if (l2 < l1 || (l2 == l1 && c2 < c1))
            {
                long tl = l1; l1 = l2; l2 = tl;
                int tc = c1; c1 = c2; c2 = tc;
            }
        }

        public string GetSelectedText(int maxChars) => GetSelectedText(maxChars, out _);

        public string GetSelectedText(int maxChars, out bool truncated)
        {
            truncated = false;
            if (Doc == null || !HasSelection) return "";
            GetSelection(out long l1, out int c1, out long l2, out int c2);
            return Doc.GetText(l1, c1, l2, c2, maxChars, out truncated);
        }

        public void SelectAll()
        {
            if (Doc == null) return;
            selAnchorLine = 0; selAnchorCol = 0;
            CaretLine = Math.Max(0, Doc.LineCount - 1);
            CaretColumn = Doc.GetLine(CaretLine).Length;
            InvalidateVisual();
        }

        public void CenterOnLine(long line)
        {
            TopLine = Math.Max(0, line - VisibleRows / 2);
        }

        public void RevealLine(long line, int col, int len)
        {
            int rows = VisibleRows;
            if (line < TopLine || line >= TopLine + rows - 1) CenterOnLine(line);
            if (Doc != null)
            {
                string text = Doc.GetLine(line);
                int vStart = VisualColumn(text, col);
                int vEnd = VisualColumn(text, Math.Min(text.Length, col + len));
                int cols = VisibleColumns;
                if (vStart < LeftColumn) LeftColumn = Math.Max(0, vStart - 8);
                else if (vEnd > LeftColumn + cols - 2) LeftColumn = Math.Max(0, vEnd - cols + 8);
                WidestSeenColumns = Math.Max(WidestSeenColumns, vEnd + 8);
            }
        }

        private void EnsureCaretVisible()
        {
            int rows = VisibleRows;
            if (CaretLine < TopLine) TopLine = CaretLine;
            else if (CaretLine > TopLine + rows - 2) TopLine = Math.Max(0, CaretLine - rows + 2);
            if (Doc != null)
            {
                int v = VisualColumn(Doc.GetLine(CaretLine), CaretColumn);
                int cols = VisibleColumns;
                if (v < LeftColumn) LeftColumn = Math.Max(0, v - 4);
                else if (v > LeftColumn + cols - 2) LeftColumn = v - cols + 4;
            }
        }

        // ---- columns: tabs expand to the next multiple of TabSize

        public static int VisualColumn(string text, int col)
        {
            int v = 0;
            int n = Math.Min(col, text.Length);
            for (int i = 0; i < n; i++) v = text[i] == '\t' ? (v / TabSize + 1) * TabSize : v + 1;
            return v + Math.Max(0, col - text.Length);
        }

        public static int CharColumnFromVisual(string text, int visual)
        {
            int v = 0;
            for (int i = 0; i < text.Length; i++)
            {
                int next = text[i] == '\t' ? (v / TabSize + 1) * TabSize : v + 1;
                if (visual < next) return (visual - v) * 2 >= (next - v) ? i + 1 : i;
                v = next;
            }
            return text.Length;
        }

        private static string ExpandTabs(string text)
        {
            if (text.IndexOf('\t') < 0) return text;
            var sb = new StringBuilder(text.Length + 16);
            foreach (char c in text)
            {
                if (c == '\t') { int n = TabSize - (sb.Length % TabSize); sb.Append(' ', n); }
                else sb.Append(c);
            }
            return sb.ToString();
        }

        // ---- input

        private void HitTest(Point p, out long line, out int col)
        {
            line = TopLine + (long)Math.Max(0, Math.Floor(p.Y / LineHeight));
            if (Doc == null) { col = 0; return; }
            line = Math.Min(line, Math.Max(0, Doc.LineCount - 1));
            int visual = LeftColumn + (int)Math.Max(0, Math.Round((p.X - GutterWidth - 4) / CharWidth));
            col = CharColumnFromVisual(Doc.GetLine(line), visual);
        }

        protected override void OnPointerPressed(PointerPressedEventArgs e)
        {
            base.OnPointerPressed(e);
            Focus();
            if (Doc == null) return;
            var pt = e.GetCurrentPoint(this);
            if (!pt.Properties.IsLeftButtonPressed) return;
            HitTest(pt.Position, out long line, out int col);
            bool shift = e.KeyModifiers.HasFlag(KeyModifiers.Shift);
            if (e.ClickCount >= 2 && !shift)
            {
                string text = Doc.GetLine(line);
                int s = Math.Min(col, text.Length), en = s;
                while (s > 0 && IsWordChar(text[s - 1])) s--;
                while (en < text.Length && IsWordChar(text[en])) en++;
                SetSelection(line, s, line, en);
            }
            else
            {
                SetCaret(line, col, shift);
                if (!shift) { selAnchorLine = line; selAnchorCol = col; }
                dragging = true;
                e.Pointer.Capture(this);
            }
            InvalidateVisual();
            e.Handled = true;
        }

        private static bool IsWordChar(char c) => char.IsLetterOrDigit(c) || c == '_';

        protected override void OnPointerMoved(PointerEventArgs e)
        {
            base.OnPointerMoved(e);
            if (!dragging || Doc == null) return;
            var p = e.GetPosition(this);
            if (p.Y < 0 && TopLine > 0) TopLine = Math.Max(0, TopLine - 1);
            else if (p.Y > Bounds.Height) TopLine = Math.Min(Math.Max(0, Doc.LineCount - VisibleRows + 1), TopLine + 1);
            HitTest(new Point(p.X, Math.Max(0, Math.Min(Bounds.Height - 1, p.Y))), out long line, out int col);
            CaretLine = line; CaretColumn = col;
            owner.SyncScrollBarsFromViewport();
        }

        protected override void OnPointerReleased(PointerReleasedEventArgs e)
        {
            base.OnPointerReleased(e);
            if (dragging) { dragging = false; e.Pointer.Capture(null); }
        }

        protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
        {
            base.OnPointerWheelChanged(e);
            if (Doc == null) return;
            if (Math.Abs(e.Delta.X) > Math.Abs(e.Delta.Y) || e.KeyModifiers.HasFlag(KeyModifiers.Shift))
            {
                double d = Math.Abs(e.Delta.X) > 0 ? e.Delta.X : e.Delta.Y;
                LeftColumn = Math.Max(0, LeftColumn - (int)Math.Round(d * 6));
            }
            else
            {
                long max = Math.Max(0, Doc.LineCount - VisibleRows + 1);
                TopLine = Math.Max(0, Math.Min(max, TopLine - (long)Math.Round(e.Delta.Y * 3)));
                AutoScrollToEnd = Doc.Follow && IsAtEnd;
            }
            owner.SyncScrollBarsFromViewport();
            e.Handled = true;
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            if (Doc == null || e.Handled) return;
            bool primary = MacKeymap.IsPrimary(e.KeyModifiers);
            bool shift = e.KeyModifiers.HasFlag(KeyModifiers.Shift);
            int rows = VisibleRows;
            bool handled = true;
            bool moved = true;
            switch (e.Key)
            {
                case Key.C when primary:
                case Key.Insert when primary:
                    owner.CopySelection(); moved = false; break;
                case Key.A when primary:
                    SelectAll(); moved = false; break;
                case Key.F when primary:
                    owner.ShowFind(); moved = false; break;
                case Key.L when primary && e.KeyModifiers.HasFlag(KeyModifiers.Control) && !RuntimeInformation.IsOSPlatform(OSPlatform.OSX):
                    owner.ShowGoTo(); moved = false; break;
                case Key.F3:
                    owner.NavigateMatch(shift ? -1 : 1); moved = false; break;
                case Key.Escape:
                    if (owner.IsFindBarVisible) owner.HideFind();
                    else selAnchorLine = -1;
                    moved = false; break;
                case Key.Up: SetCaret(CaretLine - 1, CaretColumn, shift); break;
                case Key.Down: SetCaret(CaretLine + 1, CaretColumn, shift); break;
                case Key.PageUp: SetCaret(CaretLine - (rows - 1), CaretColumn, shift); TopLine = Math.Max(0, TopLine - (rows - 1)); break;
                case Key.PageDown: SetCaret(CaretLine + (rows - 1), CaretColumn, shift); TopLine = TopLine + (rows - 1); break;
                case Key.Left:
                    if (CaretColumn > 0) SetCaret(CaretLine, CaretColumn - 1, shift);
                    else if (CaretLine > 0) SetCaret(CaretLine - 1, Doc.GetLine(CaretLine - 1).Length, shift);
                    break;
                case Key.Right:
                    if (CaretColumn < Doc.GetLine(CaretLine).Length) SetCaret(CaretLine, CaretColumn + 1, shift);
                    else SetCaret(CaretLine + 1, 0, shift);
                    break;
                case Key.Home:
                    if (primary) SetCaret(0, 0, shift); else SetCaret(CaretLine, 0, shift);
                    break;
                case Key.End:
                    if (primary) { SetCaret(Doc.LineCount - 1, 0, shift); AutoScrollToEnd = Doc.Follow; }
                    else SetCaret(CaretLine, Doc.GetLine(CaretLine).Length, shift);
                    break;
                default:
                    handled = false; moved = false; break;
            }
            if (moved)
            {
                long maxTop = Math.Max(0, Doc.LineCount - rows + 1);
                if (TopLine > maxTop) TopLine = maxTop;
                EnsureCaretVisible();
                if (e.Key != Key.End) AutoScrollToEnd = Doc.Follow && IsAtEnd;
            }
            if (handled)
            {
                e.Handled = true;
                owner.SyncScrollBarsFromViewport();
            }
        }

        protected override void OnSizeChanged(SizeChangedEventArgs e)
        {
            base.OnSizeChanged(e);
            if (Doc != null && Doc.Follow && AutoScrollToEnd) ScrollToEnd(false);
            Dispatcher.UIThread.Post(owner.SyncScrollBarsFromViewport, DispatcherPriority.Background);
        }

        // ---- rendering

        public override void Render(DrawingContext context)
        {
            var theme = owner.Theme;
            var bounds = new Rect(Bounds.Size);
            context.FillRectangle(AvaloniaColorHelper.ToBrush(theme.EditorBg), bounds);
            var doc = Doc;
            LastRenderedLineCount = 0;
            if (doc == null) return;

            if (Math.Abs(measuredFontSize - FontSizePx) > 0.01)
            {
                var probe = new FormattedText("MMMMMMMMMM", CultureInfo.InvariantCulture, FlowDirection.LeftToRight, Font, FontSizePx, Brushes.White);
                double w = probe.WidthIncludingTrailingWhitespace / 10.0;
                if (w > 3) { measuredCharWidth = w; measuredFontSize = FontSizePx; }
            }

            double lh = LineHeight;
            double gutter = GutterWidth;
            double cw = CharWidth;
            int rows = (int)Math.Ceiling(Bounds.Height / lh) + 1;
            int cols = VisibleColumns + 2;
            string[] lines = doc.GetLines(TopLine, rows);
            LastRenderedLineCount = lines.Length;

            var fg = AvaloniaColorHelper.ToBrush(theme.TextPrimary);
            var lnBrush = AvaloniaColorHelper.ToBrush(theme.FgMuted);
            var lnActive = AvaloniaColorHelper.ToBrush(theme.TextPrimary);
            var gutterBg = AvaloniaColorHelper.ToBrush(theme.EditorBg);
            var curLineBrush = AvaloniaColorHelper.ToBrush(theme.BgLineHighlight);
            var selBrush = AvaloniaColorHelper.ToBrush(theme.BgSelection);
            var matchBrush = new SolidColorBrush(AvaloniaColorHelper.ToAvColor(theme.AccentSecondary, 70));
            var activeMatchBrush = new SolidColorBrush(AvaloniaColorHelper.ToAvColor(theme.AccentPrimary, 130));
            var truncBrush = AvaloniaColorHelper.ToBrush(theme.FgMuted);

            bool hasSel = HasSelection;
            long sl1 = 0, sl2 = 0; int sc1 = 0, sc2 = 0;
            if (hasSel) GetSelection(out sl1, out sc1, out sl2, out sc2);

            List<LargeFileMatch> matches = null;
            LargeFileMatch active = default(LargeFileMatch);
            bool hasActive = false;
            var search = owner.ActiveSearch;
            if (search != null)
            {
                matches = search.GetMatchesInLines(TopLine, TopLine + lines.Length);
                if (owner.ActiveMatchIndex >= 0 && owner.ActiveMatchIndex < search.StoredMatchCount)
                {
                    active = search.GetMatch(owner.ActiveMatchIndex);
                    hasActive = true;
                }
            }

            double textX = gutter + 4 - LeftColumn * cw;
            for (int r = 0; r < lines.Length; r++)
            {
                long docLine = TopLine + r;
                double y = r * lh;
                string raw = lines[r];
                if (docLine == CaretLine && !hasSel)
                    context.FillRectangle(curLineBrush, new Rect(gutter, y, Math.Max(0, Bounds.Width - gutter), lh));

                if (hasSel && docLine >= sl1 && docLine <= sl2)
                {
                    int a = docLine == sl1 ? VisualColumn(raw, sc1) : 0;
                    int b = docLine == sl2 ? VisualColumn(raw, sc2) : VisualColumn(raw, raw.Length) + 1;
                    double x1 = Math.Max(gutter, textX + a * cw), x2 = textX + b * cw;
                    if (x2 > x1) context.FillRectangle(selBrush, new Rect(x1, y, x2 - x1, lh));
                }

                if (matches != null)
                {
                    foreach (var m in matches)
                    {
                        if (m.Line != docLine) continue;
                        int c = m.Column >= 0 ? m.Column : doc.ByteOffsetToColumn(m.Line, m.ByteOffset);
                        int a = VisualColumn(raw, c), b = VisualColumn(raw, c + m.Length);
                        double x1 = Math.Max(gutter, textX + a * cw), x2 = textX + b * cw;
                        bool isActive = hasActive && m.Line == active.Line && m.Column == active.Column && m.ByteOffset == active.ByteOffset;
                        if (x2 > x1) context.FillRectangle(isActive ? activeMatchBrush : matchBrush, new Rect(x1, y + 1, x2 - x1, lh - 2));
                    }
                }

                string shown = ExpandTabs(raw);
                if (shown.Length > WidestSeenColumns) WidestSeenColumns = Math.Min(LargeFileDocument.MaxDisplayLineChars + 8, shown.Length);
                if (shown.Length > LeftColumn)
                {
                    int take = Math.Min(cols, shown.Length - LeftColumn);
                    string slice = shown.Substring(LeftColumn, take);
                    var ft = new FormattedText(slice, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, Font, FontSizePx, fg);
                    context.DrawText(ft, new Point(gutter + 4, y + (lh - ft.Height) / 2));
                    if (raw.Length >= LargeFileDocument.MaxDisplayLineChars && LeftColumn + take >= shown.Length)
                    {
                        var tft = new FormattedText("  … (line truncated for display)", CultureInfo.InvariantCulture, FlowDirection.LeftToRight, Font, FontSizePx * 0.9, truncBrush);
                        context.DrawText(tft, new Point(gutter + 4 + take * cw, y + (lh - tft.Height) / 2));
                    }
                }

                if (docLine == CaretLine && IsFocused)
                {
                    double cx = textX + VisualColumn(raw, CaretColumn) * cw;
                    if (cx >= gutter) context.FillRectangle(fg, new Rect(cx, y + 2, 1.6, lh - 4));
                }
            }

            // Gutter drawn last so horizontally scrolled text never overlaps it.
            context.FillRectangle(gutterBg, new Rect(0, 0, gutter, Bounds.Height));
            for (int r = 0; r < lines.Length; r++)
            {
                long docLine = TopLine + r;
                var lft = new FormattedText((docLine + 1).ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture, FlowDirection.LeftToRight, Font, FontSizePx, docLine == CaretLine ? lnActive : lnBrush);
                context.DrawText(lft, new Point(gutter - 10 - lft.Width, r * lh + (lh - lft.Height) / 2));
            }

            if (!doc.IsIndexComplete)
            {
                double frac = doc.IndexProgress;
                context.FillRectangle(AvaloniaColorHelper.ToBrush(theme.AccentPrimary), new Rect(0, 0, Bounds.Width * frac, 2));
            }
        }
    }
}
