using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Collections;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using codeLess.Studio;
using CoreSearchMatch = codeLess.Studio.WorkspaceSearchMatch;

namespace codeLess.AvaloniaApp
{
    /// <summary>All matches of one file in the Search view (VS Code groups results per file).</summary>
    public sealed class SearchFileGroup
    {
        public string FilePath { get; set; } = "";
        public string RelativePath { get; set; } = "";
        public bool IsCollapsed { get; set; }
        public List<CoreSearchMatch> Matches { get; } = new List<CoreSearchMatch>();
        public SearchResultRow HeaderRow { get; set; }
        public List<SearchResultRow> MatchRows { get; } = new List<SearchResultRow>();
        /// <summary>Chevron of the realized header row; updated in place so the row (and pointer gestures) stay stable.</summary>
        internal TextBlock ChevronBlock { get; set; }
    }

    /// <summary>One row of the Search results list: either a file header or a single match occurrence.</summary>
    public sealed class SearchResultRow
    {
        public bool IsFileHeader { get; set; }
        public SearchFileGroup Group { get; set; }
        public CoreSearchMatch Match { get; set; }
        public override string ToString() => IsFileHeader
            ? (Group?.RelativePath ?? "")
            : $"{Match?.RelativePath}:{Match?.LineNumber}  {Match?.LinePreview}";
    }

    /// <summary>
    /// Search view (Find / Replace in Files) with VS Code parity: grouped results, click opens the file and selects
    /// the matched range, include / exclude globs, search-on-type, background search, F4 / Shift+F4.
    /// </summary>
    public partial class MainWindow
    {
        private TextBox workspaceSearchIncludeInput;
        private TextBox workspaceSearchExcludeInput;
        private Grid workspaceReplaceRow;
        private Button wsReplaceToggleBtn;
        private Button wsReplaceAllBtn;
        private TextBlock wsIncludeLabel;
        private TextBlock wsExcludeLabel;
        private readonly AvaloniaList<SearchResultRow> searchRows = new AvaloniaList<SearchResultRow>();
        private readonly List<SearchFileGroup> searchGroups = new List<SearchFileGroup>();
        private CancellationTokenSource workspaceSearchCts;
        private int workspaceSearchGeneration;
        private DispatcherTimer searchOnTypeTimer;
        private bool suppressSearchOnType;
        private int searchCursorMatchIndex = -1;
        private string lastSearchSignature = "";
        private int lastSearchMatchCount;

        public bool IsWorkspaceSearchRunning { get; private set; }
        public IReadOnlyList<SearchResultRow> WorkspaceSearchRows => searchRows;
        public IReadOnlyList<SearchFileGroup> WorkspaceSearchGroups => searchGroups;
        public ListBox WorkspaceSearchResultsListControl => workspaceSearchResultsList;
        public TextBox WorkspaceSearchQueryInput => workspaceSearchInput;
        public TextBox WorkspaceSearchIncludeInput => workspaceSearchIncludeInput;
        public TextBox WorkspaceSearchExcludeInput => workspaceSearchExcludeInput;
        public string WorkspaceSearchSummaryText => workspaceSearchSummary?.Text ?? "";
        public bool IsWorkspaceReplaceVisible => workspaceReplaceRow != null && workspaceReplaceRow.IsVisible;

        private Grid BuildSearchSidebarView()
        {
            var grid = new Grid
            {
                RowDefinitions = new RowDefinitions("Auto,*"),
                Margin = new Thickness(6, 6, 6, 4),
                IsVisible = false
            };

            var topStack = new StackPanel { Spacing = 5 };

            // Row 1: [>] [ search box ............ Aa ab .* ]
            var findRow = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto") };
            wsReplaceToggleBtn = new Button
            {
                Content = new LiteralTextBlock("▸"),
                FontSize = 13,
                Width = 18,
                Padding = new Thickness(0),
                Margin = new Thickness(0, 0, 3, 0),
                VerticalAlignment = VerticalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Center,
                VerticalContentAlignment = VerticalAlignment.Center,
                Background = Brushes.Transparent
            };
            ToolTip.SetTip(wsReplaceToggleBtn, "Toggle Replace");
            wsReplaceToggleBtn.Click += (s, e) => SetReplaceRowVisible(!IsWorkspaceReplaceVisible);
            findRow.Children.Add(wsReplaceToggleBtn);

            workspaceSearchInput = new TextBox { Watermark = "Search", FontSize = 12, Padding = new Thickness(7, 4) };
            Grid.SetColumn(workspaceSearchInput, 1);
            findRow.Children.Add(workspaceSearchInput);

            var toggleRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2, Margin = new Thickness(3, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
            wsMatchCaseBtn = MakeSearchToggle("Aa", "Match Case");
            wsWholeWordBtn = MakeSearchToggle("ab", "Match Whole Word");
            wsRegexBtn = MakeSearchToggle(".*", "Use Regular Expression");
            wsMatchCaseBtn.Click += (s, e) => { wsSearchMatchCase = !wsSearchMatchCase; UpdateSearchToggleVisuals(); ExecuteWorkspaceSearch(); };
            wsWholeWordBtn.Click += (s, e) => { wsSearchWholeWord = !wsSearchWholeWord; UpdateSearchToggleVisuals(); ExecuteWorkspaceSearch(); };
            wsRegexBtn.Click += (s, e) => { wsSearchRegex = !wsSearchRegex; UpdateSearchToggleVisuals(); ExecuteWorkspaceSearch(); };
            toggleRow.Children.Add(wsMatchCaseBtn);
            toggleRow.Children.Add(wsWholeWordBtn);
            toggleRow.Children.Add(wsRegexBtn);
            Grid.SetColumn(toggleRow, 2);
            findRow.Children.Add(toggleRow);
            topStack.Children.Add(findRow);

            // Row 2 (collapsible): [ replace box ............ ] [Replace All]
            workspaceReplaceRow = new Grid { ColumnDefinitions = new ColumnDefinitions("21,*,Auto"), IsVisible = false };
            workspaceReplaceInput = new TextBox { Watermark = "Replace", FontSize = 12, Padding = new Thickness(7, 4) };
            Grid.SetColumn(workspaceReplaceInput, 1);
            workspaceReplaceRow.Children.Add(workspaceReplaceInput);
            wsReplaceAllBtn = new Button { Content = new LiteralTextBlock("Replace All"), FontSize = 11, Padding = new Thickness(8, 3), Margin = new Thickness(3, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
            ToolTip.SetTip(wsReplaceAllBtn, "Replace All (in the files listed below)");
            wsReplaceAllBtn.Click += (s, e) => ConfirmAndReplaceAll();
            Grid.SetColumn(wsReplaceAllBtn, 2);
            workspaceReplaceRow.Children.Add(wsReplaceAllBtn);
            topStack.Children.Add(workspaceReplaceRow);

            // Rows 3/4: files to include / exclude
            wsIncludeLabel = new TextBlock { Text = "files to include", FontSize = 10.5, Margin = new Thickness(21, 2, 0, 0) };
            workspaceSearchIncludeInput = new TextBox { Watermark = "e.g. ./src, *.cs, src/**/*.ts", FontSize = 11.5, Padding = new Thickness(7, 3), Margin = new Thickness(21, 0, 0, 0) };
            wsExcludeLabel = new TextBlock { Text = "files to exclude", FontSize = 10.5, Margin = new Thickness(21, 2, 0, 0) };
            workspaceSearchExcludeInput = new TextBox { Watermark = "e.g. *.min.js, **/generated/**", FontSize = 11.5, Padding = new Thickness(7, 3), Margin = new Thickness(21, 0, 0, 0) };
            topStack.Children.Add(wsIncludeLabel);
            topStack.Children.Add(workspaceSearchIncludeInput);
            topStack.Children.Add(wsExcludeLabel);
            topStack.Children.Add(workspaceSearchExcludeInput);

            // Summary + actions
            var summaryRow = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), Margin = new Thickness(0, 3, 0, 0) };
            workspaceSearchSummary = new TextBlock { Text = "Type a query and press Enter to search across files.", FontSize = 11, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };
            summaryRow.Children.Add(workspaceSearchSummary);
            var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 1 };
            actions.Children.Add(MakeSearchActionButton("⟳", "Refresh", () => ExecuteWorkspaceSearch()));
            actions.Children.Add(MakeSearchActionButton("⊟", "Collapse / Expand All", ToggleCollapseAllSearchGroups));
            actions.Children.Add(MakeSearchActionButton("✕", "Clear Search Results", ClearWorkspaceSearch));
            Grid.SetColumn(actions, 1);
            summaryRow.Children.Add(actions);
            topStack.Children.Add(summaryRow);

            Grid.SetRow(topStack, 0);
            grid.Children.Add(topStack);

            workspaceSearchResultsList = new ListBox
            {
                Margin = new Thickness(0, 4, 0, 0),
                BorderThickness = new Thickness(0),
                Background = Brushes.Transparent,
                SelectionMode = SelectionMode.Single,
                ItemsSource = searchRows,
                ItemTemplate = new FuncDataTemplate<SearchResultRow>((row, _) => BuildSearchRowControl(row))
            };
            workspaceSearchResultsList.Styles.Add(new Style(x => x.OfType<ListBoxItem>())
            {
                Setters =
                {
                    new Setter(ListBoxItem.PaddingProperty, new Thickness(2, 0)),
                    new Setter(ListBoxItem.MinHeightProperty, 22.0)
                }
            });
            ScrollViewer.SetHorizontalScrollBarVisibility(workspaceSearchResultsList, global::Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled);
            // Tapped (not SelectionChanged): clicking the already-selected result again must re-open / re-reveal it.
            workspaceSearchResultsList.Tapped += (s, e) =>
            {
                var row = SearchRowFromSource(e.Source);
                if (row == null) return;
                if (row.IsFileHeader) ToggleSearchGroup(row.Group);
                else ActivateSearchMatch(row.Match, preview: true, focusEditor: false);
                e.Handled = true;
            };
            workspaceSearchResultsList.DoubleTapped += (s, e) =>
            {
                var row = SearchRowFromSource(e.Source);
                if (row == null) return;
                // The 2nd click of a double click raises DoubleTapped instead of Tapped: a header toggles again
                // (VS Code tree behaviour), a match is opened pinned with the editor focused.
                if (row.IsFileHeader) ToggleSearchGroup(row.Group);
                else ActivateSearchMatch(row.Match, preview: false, focusEditor: true);
                e.Handled = true;
            };
            workspaceSearchResultsList.AddHandler(KeyDownEvent, OnSearchResultsKeyDown, RoutingStrategies.Tunnel);
            Grid.SetRow(workspaceSearchResultsList, 1);
            grid.Children.Add(workspaceSearchResultsList);

            workspaceSearchInput.KeyDown += (s, e) =>
            {
                if (e.Key == Key.Enter) { ExecuteWorkspaceSearch(); e.Handled = true; }
                else if (e.Key == Key.Down && searchRows.Count > 0) { FocusSearchResults(); e.Handled = true; }
            };
            workspaceReplaceInput.KeyDown += (s, e) =>
            {
                if (e.Key == Key.Enter && e.KeyModifiers.HasFlag(MacKeymap.PrimaryModifier)) { ConfirmAndReplaceAll(); e.Handled = true; }
            };
            EventHandler<KeyEventArgs> runOnEnter = (s, e) => { if (e.Key == Key.Enter) { ExecuteWorkspaceSearch(); e.Handled = true; } };
            workspaceSearchIncludeInput.KeyDown += runOnEnter;
            workspaceSearchExcludeInput.KeyDown += runOnEnter;

            // VS Code "search.searchOnType": re-run 300 ms after typing stops (desktop only; tests drive searches explicitly).
            searchOnTypeTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
            searchOnTypeTimer.Tick += (s, e) => { searchOnTypeTimer.Stop(); ExecuteWorkspaceSearch(); };
            EventHandler<TextChangedEventArgs> onType = (s, e) =>
            {
                if (suppressSearchOnType || !AvaloniaTerminalEmulatorControl.IsDesktopRuntime) return;
                searchOnTypeTimer.Stop();
                searchOnTypeTimer.Start();
            };
            workspaceSearchInput.TextChanged += onType;
            workspaceSearchIncludeInput.TextChanged += onType;
            workspaceSearchExcludeInput.TextChanged += onType;

            return grid;
        }

        private static Button MakeSearchToggle(string label, string tip)
        {
            var b = new Button
            {
                Content = new LiteralTextBlock(label),
                FontSize = 10.5,
                MinWidth = 24,
                Padding = new Thickness(4, 2),
                BorderThickness = new Thickness(1),
                HorizontalContentAlignment = HorizontalAlignment.Center
            };
            ToolTip.SetTip(b, tip);
            return b;
        }

        private static Button MakeSearchActionButton(string glyph, string tip, Action onClick)
        {
            var b = new Button
            {
                Content = new LiteralTextBlock(glyph),
                FontSize = 12,
                Padding = new Thickness(5, 0),
                Background = Brushes.Transparent,
                BorderThickness = new Thickness(0)
            };
            ToolTip.SetTip(b, tip);
            b.Click += (s, e) => onClick();
            return b;
        }

        private void UpdateSearchToggleVisuals()
        {
            if (currentTheme == null || wsMatchCaseBtn == null) return;
            void style(Button b, bool on)
            {
                b.Background = on ? AvaloniaColorHelper.ToBrush(currentTheme.AccentPrimary, 70) : Brushes.Transparent;
                b.BorderBrush = on ? AvaloniaColorHelper.ToBrush(currentTheme.AccentPrimary) : Brushes.Transparent;
                b.Foreground = AvaloniaColorHelper.ToBrush(on ? currentTheme.TextPrimary : currentTheme.TextSecondary);
            }
            style(wsMatchCaseBtn, wsSearchMatchCase);
            style(wsWholeWordBtn, wsSearchWholeWord);
            style(wsRegexBtn, wsSearchRegex);
        }

        /// <summary>Re-themes the Search view (called from ApplyTheme).</summary>
        private void ApplySearchViewTheme()
        {
            if (currentTheme == null || workspaceSearchSummary == null) return;
            var secondary = AvaloniaColorHelper.ToBrush(currentTheme.TextSecondary);
            workspaceSearchSummary.Foreground = secondary;
            wsIncludeLabel.Foreground = secondary;
            wsExcludeLabel.Foreground = secondary;
            wsReplaceToggleBtn.Foreground = secondary;
            UpdateSearchToggleVisuals();
            // Rows capture theme colours when built: rebuild the realized containers.
            workspaceSearchResultsList.ItemsSource = null;
            workspaceSearchResultsList.ItemsSource = searchRows;
        }

        public void SetReplaceRowVisible(bool visible)
        {
            workspaceReplaceRow.IsVisible = visible;
            wsReplaceToggleBtn.Content = new LiteralTextBlock(visible ? "▾" : "▸");
        }

        public void SetWorkspaceSearchFlags(bool? matchCase = null, bool? wholeWord = null, bool? useRegex = null)
        {
            if (matchCase.HasValue) wsSearchMatchCase = matchCase.Value;
            if (wholeWord.HasValue) wsSearchWholeWord = wholeWord.Value;
            if (useRegex.HasValue) wsSearchRegex = useRegex.Value;
            UpdateSearchToggleVisuals();
        }

        private void SetSearchBoxText(TextBox box, string text)
        {
            suppressSearchOnType = true;
            try { box.Text = text ?? ""; }
            finally { suppressSearchOnType = false; }
        }

        /// <summary>Ctrl/Cmd+Shift+F (and +H): shows the Search view seeded with the editor's single-line selection.</summary>
        public void ShowSearchView(bool showReplace = false)
        {
            SwitchSidebarView("search");
            if (showReplace) SetReplaceRowVisible(true);
            string seed = null;
            if (activeTab != null && activeTab.HasSelection())
            {
                string sel = activeTab.GetSelectedText();
                if (!string.IsNullOrEmpty(sel) && sel.IndexOf('\n') < 0 && sel.Length <= 500) seed = sel;
            }
            if (seed != null && seed != workspaceSearchInput.Text)
            {
                SetSearchBoxText(workspaceSearchInput, seed);
                ExecuteWorkspaceSearch();
            }
            workspaceSearchInput.Focus();
            workspaceSearchInput.SelectAll();
        }

        /// <summary>Explorer &gt; Find in Folder...: scopes "files to include" to the folder (VS Code uses "./relative/path").</summary>
        public void FindInFolder(string path)
        {
            if (string.IsNullOrEmpty(path)) return;
            string dir = Directory.Exists(path) ? path : Path.GetDirectoryName(path);
            if (string.IsNullOrEmpty(dir)) return;
            dir = Path.GetFullPath(dir);
            string include;
            string root = string.IsNullOrEmpty(workspaceRoot) ? "" : workspaceRoot.TrimEnd('\\', '/');
            if (root.Length > 0 && string.Equals(dir.TrimEnd('\\', '/'), root, PathKeyComparison))
                include = "";
            else if (root.Length > 0 && IsSameOrUnder(dir, root))
                include = "./" + LlmHarnessAndAgentEngine.GetRelativePath(root, dir).Replace('\\', '/').TrimEnd('/');
            else
                include = dir;

            SwitchSidebarView("search");
            SetSearchBoxText(workspaceSearchIncludeInput, include);
            workspaceSearchInput.Focus();
            workspaceSearchInput.SelectAll();
            if (!string.IsNullOrEmpty(workspaceSearchInput.Text))
                ExecuteWorkspaceSearch();
            else
                workspaceSearchSummary.Text = string.IsNullOrEmpty(include)
                    ? "Type a query to search the workspace."
                    : $"Type a query to search in '{include}'.";
        }

        private WorkspaceSearchOptions BuildWorkspaceSearchOptions()
        {
            var overrides = new Dictionary<string, string>(PathKeyComparer);
            foreach (var t in openTabs)
            {
                // Unsaved editors are searched in memory (VS Code searches the dirty model, not the stale disk copy).
                if (t != null && t.IsDirty && !string.IsNullOrEmpty(t.FilePath))
                    overrides[t.FilePath] = t.Content ?? "";
            }
            return new WorkspaceSearchOptions
            {
                CaseSensitive = wsSearchMatchCase,
                WholeWord = wsSearchWholeWord,
                UseRegex = wsSearchRegex,
                IncludePattern = workspaceSearchIncludeInput?.Text ?? "",
                ExcludePattern = workspaceSearchExcludeInput?.Text ?? "",
                ContentOverrides = overrides,
                MaxResults = 10000
            };
        }

        private string ComputeSearchSignature(string q) =>
            string.Join("\u0001", q, wsSearchMatchCase, wsSearchWholeWord, wsSearchRegex,
                workspaceSearchIncludeInput?.Text ?? "", workspaceSearchExcludeInput?.Text ?? "");

        public void CancelWorkspaceSearch()
        {
            if (workspaceSearchCts != null)
            {
                try { workspaceSearchCts.Cancel(); } catch { }
                workspaceSearchCts = null;
            }
            if (IsWorkspaceSearchRunning)
            {
                IsWorkspaceSearchRunning = false;
                sidebarProgressBar?.StopProgress();
            }
        }

        public void ClearWorkspaceSearch()
        {
            CancelWorkspaceSearch();
            searchOnTypeTimer?.Stop();
            SetSearchBoxText(workspaceSearchInput, "");
            ResetSearchResultsModel();
            lastSearchSignature = "";
            workspaceSearchSummary.Text = "Type a query and press Enter to search across files.";
            workspaceSearchInput.Focus();
        }

        private void ResetSearchResultsModel()
        {
            searchGroups.Clear();
            searchRows.Clear();
            workspaceSearchHits.Clear();
            searchCursorMatchIndex = -1;
            lastSearchMatchCount = 0;
        }

        // Workspace Search & Replace
        public void ExecuteWorkspaceSearch(string overrideQuery = null)
        {
            if (overrideQuery != null)
                SetSearchBoxText(workspaceSearchInput, overrideQuery);
            searchOnTypeTimer?.Stop();
            string q = workspaceSearchInput.Text ?? "";
            CancelWorkspaceSearch();
            ResetSearchResultsModel();
            int gen = ++workspaceSearchGeneration;

            if (q.Length == 0)
            {
                lastSearchSignature = "";
                workspaceSearchSummary.Text = "Type a query to search across workspace files.";
                return;
            }

            var options = BuildWorkspaceSearchOptions();
            WorkspaceSearchEngine.CompileSearchRegex(q, options, out string regexErr);
            if (regexErr != null)
            {
                lastSearchSignature = "";
                workspaceSearchSummary.Text = "Invalid regular expression: " + regexErr;
                return;
            }

            lastSearchSignature = ComputeSearchSignature(q);
            string root = workspaceRoot;
            var roots = GetWorkspaceRootFolders().Where(Directory.Exists).ToList();
            var excluded = GetExcludedFolderSet(root);
            // Open editors that live outside every workspace folder (or with no folder open) are searched too.
            var looseFiles = openTabs
                .Where(t => !string.IsNullOrEmpty(t.FilePath) && !roots.Any(r => IsSameOrUnder(t.FilePath, r)))
                .Select(t => t.FilePath)
                .ToList();

            var cts = new CancellationTokenSource();
            workspaceSearchCts = cts;

            WorkspaceSearchResult runSearch(Action<List<CoreSearchMatch>> onFile)
            {
                var include = GlobPathFilter.Parse(options.IncludePattern, root);
                var exclude = GlobPathFilter.Parse(options.ExcludePattern, root);
                var walkRoots = include.GetLiteralRootDirectories() ?? roots;
                var files = WorkspaceSearchEngine.EnumerateSearchFiles(walkRoots, root, excluded, include, exclude, 200000, cts.Token);
                foreach (string lf in looseFiles)
                {
                    string rel = Path.GetFileName(lf);
                    if ((include.IsEmpty || include.IsMatch(lf, rel)) && !(exclude.IsMatch(lf, rel)) && !files.Contains(lf, PathKeyComparer))
                        files.Add(lf);
                }
                return WorkspaceSearchEngine.Search(root, files, q, options, cts.Token, onFile);
            }

            if (!AvaloniaTerminalEmulatorControl.IsDesktopRuntime)
            {
                // Headless / tests: synchronous so assertions see results immediately.
                var res = runSearch(list => AppendSearchGroup(list));
                FinishWorkspaceSearch(gen, res);
                return;
            }

            IsWorkspaceSearchRunning = true;
            sidebarProgressBar?.StartProgress();
            workspaceSearchSummary.Text = "Searching…";
            _ = Task.Run(() =>
            {
                var pending = new List<List<CoreSearchMatch>>();
                var sw = Stopwatch.StartNew();
                void flush()
                {
                    List<List<CoreSearchMatch>> batch;
                    lock (pending)
                    {
                        if (pending.Count == 0) return;
                        batch = new List<List<CoreSearchMatch>>(pending);
                        pending.Clear();
                    }
                    Dispatcher.UIThread.Post(() =>
                    {
                        if (gen != workspaceSearchGeneration) return;
                        foreach (var list in batch) AppendSearchGroup(list);
                        workspaceSearchSummary.Text = $"Searching… {lastSearchMatchCount} result(s) in {searchGroups.Count} file(s)";
                    });
                }
                WorkspaceSearchResult res;
                try
                {
                    res = runSearch(list =>
                    {
                        lock (pending) pending.Add(list);
                        if (sw.ElapsedMilliseconds > 80) { flush(); sw.Restart(); }
                    });
                }
                catch (Exception ex)
                {
                    res = new WorkspaceSearchResult { Error = ex.Message };
                }
                flush();
                Dispatcher.UIThread.Post(() => FinishWorkspaceSearch(gen, res));
            });
        }

        private void FinishWorkspaceSearch(int gen, WorkspaceSearchResult res)
        {
            if (gen != workspaceSearchGeneration) return;
            if (IsWorkspaceSearchRunning)
            {
                IsWorkspaceSearchRunning = false;
                sidebarProgressBar?.StopProgress();
            }
            if (res == null) return;
            if (!string.IsNullOrEmpty(res.Error))
            {
                workspaceSearchSummary.Text = "Search failed: " + res.Error;
                return;
            }
            int total = lastSearchMatchCount;
            int files = searchGroups.Count;
            if (total == 0)
            {
                string scope = string.IsNullOrWhiteSpace(workspaceSearchIncludeInput.Text) ? "" : $" in '{workspaceSearchIncludeInput.Text.Trim()}'";
                workspaceSearchSummary.Text = res.Cancelled ? "Search cancelled." : $"No results found{scope}. Review your settings for configured exclusions.";
                return;
            }
            string summary = $"{total} result{(total == 1 ? "" : "s")} in {files} file{(files == 1 ? "" : "s")}";
            if (res.Truncated) summary += " — showing the first " + total + " (refine your search)";
            workspaceSearchSummary.Text = summary;
        }

        private void AppendSearchGroup(List<CoreSearchMatch> matches)
        {
            if (matches == null || matches.Count == 0) return;
            var g = new SearchFileGroup { FilePath = matches[0].FilePath, RelativePath = matches[0].RelativePath ?? Path.GetFileName(matches[0].FilePath) };
            g.Matches.AddRange(matches);
            g.HeaderRow = new SearchResultRow { IsFileHeader = true, Group = g };
            var rows = new List<SearchResultRow>(matches.Count + 1) { g.HeaderRow };
            foreach (var m in matches)
            {
                var r = new SearchResultRow { Group = g, Match = m };
                g.MatchRows.Add(r);
                rows.Add(r);
                workspaceSearchHits.Add((m.FilePath, m.LineNumber, m.LinePreview));
            }
            searchGroups.Add(g);
            lastSearchMatchCount += matches.Count;
            searchRows.AddRange(rows);
        }

        private Control BuildSearchRowControl(SearchResultRow row)
        {
            var theme = currentTheme ?? StudioTheme.GetById(StudioTheme.DefaultThemeId);
            var primary = AvaloniaColorHelper.ToBrush(theme.TextPrimary);
            var secondary = AvaloniaColorHelper.ToBrush(theme.TextSecondary);
            if (row == null) return new TextBlock();

            if (row.IsFileHeader)
            {
                var g = row.Group;
                var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("14,Auto,Auto,*,Auto"), Background = Brushes.Transparent, Height = 22 };
                var chevron = new TextBlock { Text = g.IsCollapsed ? "▸" : "▾", FontSize = 12, Foreground = secondary, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center };
                g.ChevronBlock = chevron;
                grid.Children.Add(chevron);
                string fileName = Path.GetFileName(g.FilePath);
                AvaloniaExplorerTree.GetFileTypeBadge(fileName, out string badge, out Color badgeColor);
                var badgeTb = new TextBlock { Text = badge, FontSize = 8.5, FontWeight = FontWeight.Bold, Foreground = new SolidColorBrush(badgeColor), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(2, 0, 5, 0) };
                Grid.SetColumn(badgeTb, 1);
                grid.Children.Add(badgeTb);
                var nameTb = new TextBlock { Text = fileName, FontSize = 12, FontWeight = FontWeight.SemiBold, Foreground = primary, VerticalAlignment = VerticalAlignment.Center };
                Grid.SetColumn(nameTb, 2);
                grid.Children.Add(nameTb);
                string relDir = Path.GetDirectoryName((g.RelativePath ?? "").Replace('/', Path.DirectorySeparatorChar)) ?? "";
                var dirTb = new TextBlock { Text = relDir.Replace('\\', '/'), FontSize = 11, Foreground = secondary, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0, 4, 0), TextTrimming = TextTrimming.CharacterEllipsis };
                Grid.SetColumn(dirTb, 3);
                grid.Children.Add(dirTb);
                var countBadge = new Border
                {
                    Background = AvaloniaColorHelper.ToBrush(theme.AccentPrimary, 60),
                    CornerRadius = new CornerRadius(8),
                    Padding = new Thickness(6, 0),
                    Margin = new Thickness(0, 0, 4, 0),
                    VerticalAlignment = VerticalAlignment.Center,
                    Child = new TextBlock { Text = g.Matches.Count.ToString(), FontSize = 10.5, Foreground = primary }
                };
                Grid.SetColumn(countBadge, 4);
                grid.Children.Add(countBadge);
                ToolTip.SetTip(grid, g.FilePath);
                return grid;
            }

            var m = row.Match;
            string preview = m?.LinePreview ?? "";
            int ps = Math.Max(0, Math.Min(m?.PreviewMatchStart ?? 0, preview.Length));
            int pl = Math.Max(0, Math.Min(m?.PreviewMatchLength ?? 0, preview.Length - ps));
            var tb = new TextBlock
            {
                FontSize = 12,
                Foreground = primary,
                TextTrimming = TextTrimming.CharacterEllipsis,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(22, 0, 0, 0)
            };
            tb.Inlines = new InlineCollection();
            if (ps > 0) tb.Inlines.Add(new Run(preview.Substring(0, ps)));
            if (pl > 0)
            {
                tb.Inlines.Add(new Run(preview.Substring(ps, pl))
                {
                    Background = AvaloniaColorHelper.ToBrush(theme.AccentPrimary, 85),
                    FontWeight = FontWeight.SemiBold
                });
            }
            if (ps + pl < preview.Length) tb.Inlines.Add(new Run(preview.Substring(ps + pl)));
            var host = new Border { Background = Brushes.Transparent, Height = 22, Child = tb };
            ToolTip.SetTip(host, $"{m?.RelativePath}:{m?.LineNumber}:{m?.Column}");
            return host;
        }

        private static SearchResultRow SearchRowFromSource(object source)
        {
            var v = source as Visual;
            var item = v as ListBoxItem ?? v?.FindAncestorOfType<ListBoxItem>();
            return item?.DataContext as SearchResultRow;
        }

        private void OnSearchResultsKeyDown(object sender, KeyEventArgs e)
        {
            var row = workspaceSearchResultsList.SelectedItem as SearchResultRow;
            if (row == null) return;
            switch (e.Key)
            {
                case Key.Enter:
                    if (row.IsFileHeader) ToggleSearchGroup(row.Group);
                    else ActivateSearchMatch(row.Match, preview: false, focusEditor: true);
                    e.Handled = true;
                    break;
                case Key.Space:
                    if (!row.IsFileHeader) { ActivateSearchMatch(row.Match, preview: true, focusEditor: false); e.Handled = true; }
                    break;
                case Key.Left:
                    if (row.IsFileHeader && !row.Group.IsCollapsed) ToggleSearchGroup(row.Group);
                    else if (!row.IsFileHeader) { workspaceSearchResultsList.SelectedItem = row.Group.HeaderRow; workspaceSearchResultsList.ScrollIntoView(row.Group.HeaderRow); }
                    e.Handled = true;
                    break;
                case Key.Right:
                    if (row.IsFileHeader && row.Group.IsCollapsed) { ToggleSearchGroup(row.Group); e.Handled = true; }
                    break;
            }
        }

        private void FocusSearchResults()
        {
            if (searchRows.Count == 0) return;
            int idx = searchRows.Count > 1 && searchRows[0].IsFileHeader ? 1 : 0;
            workspaceSearchResultsList.SelectedIndex = idx;
            workspaceSearchResultsList.ScrollIntoView(idx);
            var container = workspaceSearchResultsList.ContainerFromIndex(idx);
            if (container != null) container.Focus();
            else workspaceSearchResultsList.Focus();
        }

        public void ToggleSearchGroup(SearchFileGroup g)
        {
            if (g == null) return;
            int hi = searchRows.IndexOf(g.HeaderRow);
            if (hi < 0) return;
            if (g.IsCollapsed)
            {
                g.IsCollapsed = false;
                searchRows.InsertRange(hi + 1, g.MatchRows);
            }
            else
            {
                g.IsCollapsed = true;
                searchRows.RemoveRange(hi + 1, g.MatchRows.Count);
            }
            if (g.ChevronBlock != null) g.ChevronBlock.Text = g.IsCollapsed ? "▸" : "▾";
            workspaceSearchResultsList.SelectedIndex = hi;
        }

        private void ToggleCollapseAllSearchGroups()
        {
            if (searchGroups.Count == 0) return;
            bool collapse = searchGroups.Any(g => !g.IsCollapsed);
            var rows = new List<SearchResultRow>();
            foreach (var g in searchGroups)
            {
                g.IsCollapsed = collapse;
                if (g.ChevronBlock != null) g.ChevronBlock.Text = collapse ? "▸" : "▾";
                rows.Add(g.HeaderRow);
                if (!collapse) rows.AddRange(g.MatchRows);
            }
            searchRows.Clear();
            searchRows.AddRange(rows);
        }

        /// <summary>Opens the match's file, selects the matched range and scrolls it into view (VS Code result click).</summary>
        public EditorTabBuffer ActivateSearchMatch(CoreSearchMatch m, bool preview = true, bool focusEditor = false)
        {
            if (m == null) return null;
            int flat = 0;
            foreach (var g in searchGroups)
            {
                int i = g.Matches.IndexOf(m);
                if (i >= 0) { searchCursorMatchIndex = flat + i; break; }
                flat += g.Matches.Count;
            }
            if (!File.Exists(m.FilePath) && !openTabs.Any(t => string.Equals(t.FilePath, m.FilePath, PathKeyComparison)))
            {
                SetStatusMessage($"'{m.RelativePath}' no longer exists.");
                return null;
            }
            return OpenFileAtLocation(m.FilePath, m.LineNumber, m.Column, m.MatchLength, preview, focusEditor);
        }

        /// <summary>F4 / Shift+F4: go to the next / previous search result (wraps). Returns false when there are no results.</summary>
        public bool NavigateSearchResult(int direction)
        {
            int total = searchGroups.Sum(g => g.Matches.Count);
            if (total == 0) return false;
            int idx = searchCursorMatchIndex < 0
                ? (direction >= 0 ? 0 : total - 1)
                : ((searchCursorMatchIndex + (direction >= 0 ? 1 : -1)) % total + total) % total;
            int flat = 0;
            foreach (var g in searchGroups)
            {
                if (idx < flat + g.Matches.Count)
                {
                    int local = idx - flat;
                    if (g.IsCollapsed) ToggleSearchGroup(g);
                    var row = g.MatchRows[local];
                    workspaceSearchResultsList.SelectedItem = row;
                    workspaceSearchResultsList.ScrollIntoView(row);
                    ActivateSearchMatch(g.Matches[local], preview: true, focusEditor: true);
                    searchCursorMatchIndex = idx;
                    return true;
                }
                flat += g.Matches.Count;
            }
            return false;
        }

        private void ConfirmAndReplaceAll()
        {
            string q = workspaceSearchInput.Text ?? "";
            if (q.Length == 0) return;
            if (!AvaloniaTerminalEmulatorControl.IsDesktopRuntime) { ExecuteWorkspaceReplaceAll(); return; }
            bool fresh = lastSearchSignature == ComputeSearchSignature(q) && searchGroups.Count > 0;
            string what = fresh
                ? $"{lastSearchMatchCount} occurrence(s) across {searchGroups.Count} file(s)"
                : "all occurrences in the workspace";
            ShowConfirmModal("Replace All",
                $"Replace {what} with '{workspaceReplaceInput.Text ?? ""}'?",
                "Replace",
                () => ExecuteWorkspaceReplaceAll());
        }

        /// <summary>
        /// Replaces in the files of the current results (re-searching if the query / flags changed). Unsaved open editors
        /// are edited in memory (undoable, left dirty) instead of overwriting their file on disk.
        /// </summary>
        public int ExecuteWorkspaceReplaceAll(string overrideFind = null, string overrideReplace = null)
        {
            if (overrideFind != null) SetSearchBoxText(workspaceSearchInput, overrideFind);
            if (overrideReplace != null) workspaceReplaceInput.Text = overrideReplace;

            string q = workspaceSearchInput.Text ?? "";
            if (q.Length == 0) return 0;
            CancelWorkspaceSearch();
            var options = BuildWorkspaceSearchOptions();
            var rx = WorkspaceSearchEngine.BuildReplaceRegex(q, options, out string err);
            if (rx == null)
            {
                workspaceSearchSummary.Text = "Invalid regular expression: " + err;
                return 0;
            }
            string rep = WorkspaceSearchEngine.PrepareReplacementText(workspaceReplaceInput.Text ?? "", options.UseRegex);

            List<string> targets;
            if (lastSearchSignature == ComputeSearchSignature(q) && searchGroups.Count > 0 && !IsWorkspaceSearchRunning)
            {
                targets = searchGroups.Select(g => g.FilePath).ToList();
            }
            else
            {
                string root = workspaceRoot;
                var include = GlobPathFilter.Parse(options.IncludePattern, root);
                var exclude = GlobPathFilter.Parse(options.ExcludePattern, root);
                var roots = include.GetLiteralRootDirectories() ?? GetWorkspaceRootFolders().Where(Directory.Exists).ToList();
                var files = WorkspaceSearchEngine.EnumerateSearchFiles(roots, root, GetExcludedFolderSet(root), include, exclude, 200000, CancellationToken.None);
                options.MaxResults = int.MaxValue;
                targets = WorkspaceSearchEngine.Search(root, files, q, options, CancellationToken.None, null)
                    .Matches.Select(m => m.FilePath).Distinct(PathKeyComparer).ToList();
            }

            int modifiedFiles = 0;
            int replacedTotal = 0;
            foreach (string file in targets)
            {
                var open = openTabs.FirstOrDefault(t => string.Equals(t.FilePath, file, PathKeyComparison));
                try
                {
                    if (open != null && open.IsDirty)
                    {
                        string next = WorkspaceSearchEngine.ReplaceInText(open.Content ?? "", rx, rep, out int n);
                        if (n == 0) continue;
                        open.PushUndoSnapshot();
                        open.Content = next;
                        open.SyncLinesFromContent();
                        open.IsDirty = true;
                        replacedTotal += n;
                        modifiedFiles++;
                        continue;
                    }
                    if (!File.Exists(file)) continue;
                    string raw = EditorTabBuffer.ReadFilePreservingMetadata(file, out var enc, out bool bom, out string eol, out bool finalNl);
                    if (raw.IndexOf('\0') >= 0) continue;
                    string updated = WorkspaceSearchEngine.ReplaceInText(raw, rx, rep, out int count);
                    if (count == 0) continue;
                    EditorTabBuffer.WriteFilePreservingMetadata(file, updated, enc, bom, eol, finalNl);
                    replacedTotal += count;
                    modifiedFiles++;
                    if (open != null)
                    {
                        string written = EditorTabBuffer.ReadFilePreservingMetadata(file, out Encoding _e2, out bool _b2, out string _l2, out bool _f2);
                        open.PushUndoSnapshot();
                        open.Content = written;
                        open.SavedContent = written;
                        open.SyncLinesFromContent();
                        open.IsDirty = false;
                        if (!open.IsLargeFile) open.RefreshGutterDiff();
                    }
                }
                catch { }
            }

            if (activeTab != null)
            {
                activeTab.CursorLine = Math.Max(0, Math.Min(activeTab.CursorLine, activeTab.Lines.Count - 1));
                activeTab.CursorCol = Math.Max(0, Math.Min(activeTab.CursorCol, activeTab.Lines[activeTab.CursorLine].Length));
                activeTab.ClearSelection();
                PrimaryEditor.LoadBuffer(activeTab);
            }
            RefreshTabStrip();
            ExecuteWorkspaceSearch();
            PrimaryEditor.Canvas.InvalidateVisual();
            SetStatusMessage($"Replaced {replacedTotal} occurrence(s) across {modifiedFiles} file(s).");
            return modifiedFiles;
        }

        // Navigation: open-at-location with history (Back / Forward) and range reveal

        /// <summary>Stores the active editor's current caret line in the current history entry before navigating away.</summary>
        private void CaptureCurrentNavPosition()
        {
            if (activeTab == null || string.IsNullOrEmpty(activeTab.FilePath)) return;
            if (navHistoryIndex >= 0 && navHistoryIndex < navHistory.Count &&
                string.Equals(navHistory[navHistoryIndex].FilePath, activeTab.FilePath, PathKeyComparison))
            {
                navHistory[navHistoryIndex] = (activeTab.FilePath, activeTab.CursorLine + 1);
            }
            else
            {
                RecordNavigationHistory(activeTab.FilePath, activeTab.CursorLine + 1);
            }
        }

        /// <summary>
        /// Opens <paramref name="filePath"/> (activating its tab when already open), moves the caret to the 1-based
        /// line / column, selects <paramref name="selectionLength"/> characters, reveals the range in the centre when it
        /// was off-screen and records the jump in the navigation history. A line &lt;= 0 keeps the caret where it was.
        /// </summary>
        public EditorTabBuffer OpenFileAtLocation(string filePath, int line1 = 0, int col1 = 1, int selectionLength = 0, bool preview = false, bool focusEditor = true)
        {
            if (string.IsNullOrWhiteSpace(filePath)) return null;
            bool recording = !suppressNavRecord;
            if (recording) CaptureCurrentNavPosition();
            EditorTabBuffer tab;
            suppressNavRecord = true;
            try { tab = OpenFileInTab(filePath, preview); }
            finally { suppressNavRecord = !recording; }
            if (tab == null) return null;
            if (!ReferenceEquals(activeTab, tab)) ActivateTab(tab);

            if (line1 > 0) RevealRangeInEditor(tab, line1, col1, selectionLength);
            if (recording) RecordNavigationHistory(tab.FilePath, tab.CursorLine + 1);
            if (focusEditor) PrimaryEditor.Canvas.Focus();
            return tab;
        }

        private void RevealRangeInEditor(EditorTabBuffer tab, int line1, int col1, int length)
        {
            if (tab == null || tab.Lines == null || tab.Lines.Count == 0) return;
            var canvas = PrimaryEditor.Canvas;
            int line = Math.Max(0, Math.Min(tab.Lines.Count - 1, line1 - 1));
            int rowsBefore = VisibleEditorRows();
            bool wasVisible = line >= tab.ScrollTopLine && line < tab.ScrollTopLine + rowsBefore;

            // A match inside a folded region must be visible: unfold the regions containing it.
            if (tab.FoldedBlockRanges.Count > 0)
            {
                foreach (int start in tab.FoldedBlockRanges.Where(kv => kv.Key < line && kv.Value >= line).Select(kv => kv.Key).ToList())
                    tab.FoldedBlockRanges.Remove(start);
            }

            PrimaryEditor.GoToLine(line + 1, Math.Max(1, col1));
            string text = tab.Lines[tab.CursorLine] ?? "";
            int startCol = tab.CursorCol;
            int endCol = Math.Min(text.Length, startCol + Math.Max(0, length));
            if (endCol > startCol)
            {
                tab.SelectionAnchorLine = tab.CursorLine;
                tab.SelectionAnchorCol = startCol;
                tab.CursorCol = endCol;
            }

            void center()
            {
                int rows = VisibleEditorRows();
                if (rows <= 1 || !ReferenceEquals(PrimaryEditor.CurrentBuffer, tab)) return;
                if (!wasVisible || tab.CursorLine < tab.ScrollTopLine || tab.CursorLine >= tab.ScrollTopLine + rows)
                    tab.ScrollTopLine = Math.Max(0, Math.Min(Math.Max(0, tab.Lines.Count - 1), tab.CursorLine - rows / 2));
                canvas.InvalidateVisual();
            }
            if (canvas.Bounds.Height < canvas.LineHeight * 2)
            {
                // Canvas was hidden (welcome page) and has not been laid out yet: centre after layout.
                Dispatcher.UIThread.Post(center, DispatcherPriority.Loaded);
            }
            center();
            UpdateStatusBar();
            canvas.InvalidateVisual();
        }

        private int VisibleEditorRows()
        {
            var canvas = PrimaryEditor.Canvas;
            double lh = Math.Max(1.0, canvas.LineHeight);
            return (int)(canvas.Bounds.Height / lh);
        }

        // Explorer auto-reveal (VS Code "explorer.autoReveal")

        /// <summary>Expands the Explorer down to <paramref name="filePath"/>, selects it and scrolls it into view.</summary>
        public bool RevealFileInExplorer(string filePath)
        {
            if (string.IsNullOrEmpty(filePath) || ExplorerTree == null || ExplorerTree.Nodes.Count == 0) return false;
            string full;
            try { full = Path.GetFullPath(filePath); } catch { return false; }

            ExplorerTreeNode rootNode = ExplorerTree.Nodes.FirstOrDefault(n => !string.IsNullOrEmpty(n.Tag) && IsSameOrUnder(full, n.Tag));
            if (rootNode == null) return false;

            bool changed = false;
            var cur = rootNode;
            string rel = LlmHarnessAndAgentEngine.GetRelativePath(rootNode.Tag, full).Replace('\\', '/');
            string[] parts = rel.Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries);
            string acc = rootNode.Tag;
            foreach (string part in parts)
            {
                if (!cur.IsExpanded) { EnsureDirectoryNodePopulated(cur); cur.IsExpanded = true; changed = true; }
                else EnsureDirectoryNodePopulated(cur);
                acc = Path.Combine(acc, part);
                var next = cur.Nodes.FirstOrDefault(n => string.Equals(n.Tag, acc, PathKeyComparison));
                if (next == null) { if (changed) ExplorerTree.RebuildTreeLayout(preserveScroll: true); return false; }
                cur = next;
            }
            if (changed) ExplorerTree.RebuildTreeLayout(preserveScroll: true);
            ExplorerTree.SelectedNode = cur;
            ExplorerTree.ScrollNodeIntoView(cur);
            ExplorerTree.InvalidateVisual();
            return true;
        }
    }
}
