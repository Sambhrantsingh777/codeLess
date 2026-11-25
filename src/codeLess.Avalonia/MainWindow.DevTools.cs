using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
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
    /// <summary>
    /// Built-in "extension-free" developer tools: inline/file git blame, git graph &amp; file history with diffs, the .http
    /// client and the native Markdown preview. Hooks into the workbench through InitializeDevTools / ApplyDevToolsTheme /
    /// HandleDevToolsShortcut / BuildDevToolsPaletteCommands only.
    /// </summary>
    public partial class MainWindow
    {
        private AuxPaneHost auxPane;
        private UiPost devToolsUi;
        private GridSplitter auxSplitter;
        private Canvas devToolsOverlay;
        private GridLength savedEditorCol0 = new GridLength(1, GridUnitType.Star);
        private GridLength savedEditorCol1 = new GridLength(0);
        private GitBlameService blameService;
        private DispatcherTimer blameDebounce;
        private DispatcherTimer devToolsPoll;
        private DispatcherTimer markdownDebounce;
        private DispatcherTimer blameHoverTimer;
        private readonly Dictionary<string, string> expectedBlameKeys = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        private GitBlameFileResult annotatedBlameResult;
        private readonly Dictionary<int, EditorLineAnnotation> blameAnnotations = new Dictionary<int, EditorLineAnnotation>();
        private bool fileBlameMode;
        private EditorTabBuffer lastPolledBuffer;
        private Border blameCard;
        private bool pointerOverBlameCard;
        private EditorTabBuffer httpDocBuffer;
        private int httpDocStamp = -1;
        private HttpFileDocument httpDoc;
        private readonly Dictionary<int, EditorLineAnnotation> httpLensCache = new Dictionary<int, EditorLineAnnotation>();
        private readonly Dictionary<string, HttpVariableResolver> httpResolvers = new Dictionary<string, HttpVariableResolver>(StringComparer.OrdinalIgnoreCase);
        private EditorTabBuffer pendingMarkdownBuffer;
        private int bufferEditStamp;

        public AuxPaneHost AuxPane => auxPane;
        public GitBlameService BlameService => EnsureBlameService();
        public bool IsLineBlameEnabled => settings != null && settings.ShowInlineGitBlame;
        public bool IsFileBlameEnabled => fileBlameMode;
        public bool IsAuxPaneVisible => auxPane != null && auxPane.IsVisible;
        public Border BlameHoverCard => blameCard;

        private GitBlameService EnsureBlameService()
        {
            if (blameService == null)
            {
                blameService = new GitBlameService();
                blameService.BlameReady += path => devToolsUi?.Post(() =>
                {
                    if (activeTab != null && string.Equals(activeTab.FilePath, path, StringComparison.OrdinalIgnoreCase))
                    {
                        PrimaryEditor.Canvas.InvalidateVisual();
                        if (isEditorSplit) SplitEditor.Canvas.InvalidateVisual();
                    }
                });
                blameService.StatusMessage += msg => devToolsUi?.Post(() => SetStatusMessage(msg));
            }
            return blameService;
        }

        private void UpdateDevToolsPollTimer()
        {
            if (devToolsPoll == null || windowClosed) return;
            if (IsLineBlameEnabled || fileBlameMode)
            {
                if (!devToolsPoll.IsEnabled) devToolsPoll.Start();
            }
            else
            {
                devToolsPoll.Stop();
            }
        }

        private void InitializeDevTools()
        {
            devToolsUi = UiPost.CaptureCurrent();

            blameDebounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(280) };
            blameDebounce.Tick += (s, e) => { blameDebounce.Stop(); RequestBlameForActiveBuffer(); };
            markdownDebounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
            markdownDebounce.Tick += (s, e) => { markdownDebounce.Stop(); RefreshMarkdownPreviewFor(pendingMarkdownBuffer); };
            blameHoverTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(350) };
            devToolsPoll = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
            devToolsPoll.Tick += (s, e) =>
            {
                var b = PrimaryEditor.CurrentBuffer;
                if (!ReferenceEquals(b, lastPolledBuffer))
                {
                    lastPolledBuffer = b;
                    HideBlameCard();
                    ScheduleBlame();
                }
            };
            UpdateDevToolsPollTimer();

            foreach (var ed in new[] { PrimaryEditor, SplitEditor })
            {
                var editor = ed;
                editor.LineAnnotationProvider = ProvideLineAnnotation;
                editor.LineAnnotationClicked += (buf, ann, line, rect) => OnLineAnnotationClicked(editor, buf, ann, line, rect);
                editor.LineAnnotationHovered += (buf, ann, line, rect) => OnLineAnnotationHovered(editor, buf, ann, line, rect);
                editor.CaretMoved += buf => ScheduleBlame();
                editor.BufferModified += buf =>
                {
                    bufferEditStamp++;
                    if (buf != null && !string.IsNullOrEmpty(buf.FilePath)) expectedBlameKeys.Remove(buf.FilePath);
                    HideBlameCard();
                    ScheduleBlame();
                    if (buf != null && MarkdownLinkResolver.IsMarkdownFile(buf.FilePath) && auxPane.Find(MarkdownDocId(buf.FilePath)) != null)
                    {
                        pendingMarkdownBuffer = buf;
                        markdownDebounce.Stop();
                        markdownDebounce.Start();
                    }
                };
                editor.ScrollChanged += buf => SyncMarkdownScroll(buf);
            }

            // Side pane lives in two extra columns of the editor split grid: [editor | split editor | splitter | pane].
            editorSplitGrid.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(0)));
            editorSplitGrid.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(0)));
            auxSplitter = new GridSplitter { Width = 4, Background = Brushes.Transparent, ResizeDirection = GridResizeDirection.Columns, IsVisible = false };
            Grid.SetColumn(auxSplitter, 2);
            editorSplitGrid.Children.Add(auxSplitter);
            auxPane = new AuxPaneHost { IsVisible = false };
            Grid.SetColumn(auxPane, 3);
            editorSplitGrid.Children.Add(auxPane);
            auxPane.AllClosed += HideAuxPane;
            auxPane.MaximizeToggled += SetAuxMaximized;

            if (Content is Grid rootGrid)
            {
                devToolsOverlay = new Canvas();
                rootGrid.Children.Insert(Math.Min(1, rootGrid.Children.Count), devToolsOverlay);
            }
        }

        /// <summary>Runs pending debounced work now (headless tests: DispatcherTimer does not tick there).</summary>
        public void FlushDevToolsDebounce()
        {
            if (blameDebounce != null && blameDebounce.IsEnabled) { blameDebounce.Stop(); RequestBlameForActiveBuffer(); }
            if (markdownDebounce != null && markdownDebounce.IsEnabled) { markdownDebounce.Stop(); RefreshMarkdownPreviewFor(pendingMarkdownBuffer); }
            var b = PrimaryEditor.CurrentBuffer;
            if (!ReferenceEquals(b, lastPolledBuffer)) { lastPolledBuffer = b; RequestBlameForActiveBuffer(); }
        }

        private void ApplyDevToolsTheme(StudioTheme theme)
        {
            if (auxPane == null || theme == null) return;
            auxPane.ApplyTheme(theme);
            HideBlameCard();
        }

        // ------------------------------------------------------------------------------------------------
        // Side pane
        // ------------------------------------------------------------------------------------------------

        private void ShowAuxPane()
        {
            if (auxPane.IsVisible) return;
            auxPane.IsVisible = true;
            auxSplitter.IsVisible = true;
            editorSplitGrid.ColumnDefinitions[2].Width = new GridLength(4);
            editorSplitGrid.ColumnDefinitions[3].Width = new GridLength(1, GridUnitType.Star);
        }

        private void HideAuxPane()
        {
            if (auxPane.IsMaximized) auxPane.SetMaximized(false);
            auxPane.IsVisible = false;
            auxSplitter.IsVisible = false;
            editorSplitGrid.ColumnDefinitions[2].Width = new GridLength(0);
            editorSplitGrid.ColumnDefinitions[3].Width = new GridLength(0);
            PrimaryEditor.Canvas.Focus();
        }

        private void SetAuxMaximized(bool max)
        {
            var cols = editorSplitGrid.ColumnDefinitions;
            if (max)
            {
                savedEditorCol0 = cols[0].Width;
                savedEditorCol1 = cols[1].Width;
                cols[0].Width = new GridLength(0);
                cols[1].Width = new GridLength(0);
                cols[2].Width = new GridLength(0);
                auxSplitter.IsVisible = false;
            }
            else
            {
                cols[0].Width = savedEditorCol0.Value <= 0 ? new GridLength(1, GridUnitType.Star) : savedEditorCol0;
                cols[1].Width = isEditorSplit ? new GridLength(1, GridUnitType.Star) : savedEditorCol1;
                if (auxPane.IsVisible) { cols[2].Width = new GridLength(4); auxSplitter.IsVisible = true; }
            }
        }

        private void OpenAuxDocument(AuxPaneDocument doc, bool maximize = false)
        {
            ShowAuxPane();
            auxPane.Open(doc);
            if (maximize) auxPane.SetMaximized(true);
        }

        public void CloseAuxPane() => auxPane?.CloseAll();

        private void CopyTextToClipboard(string text)
        {
            if (string.IsNullOrEmpty(text)) return;
            try { Clipboard?.SetTextAsync(text); } catch { }
            SetStatusMessage("Copied to clipboard.");
        }

        // ------------------------------------------------------------------------------------------------
        // Line annotations (blame + Send Request lens)
        // ------------------------------------------------------------------------------------------------

        private EditorLineAnnotation ProvideLineAnnotation(EditorTabBuffer buf, int line)
        {
            if (buf == null) return null;
            var parityAnn = ProvideParityAnnotation(buf, line);
            if (parityAnn != null) return parityAnn;
            if (string.IsNullOrEmpty(buf.FilePath)) return null;
            if (HttpFileParser.IsHttpFile(buf.FilePath))
            {
                var doc = GetHttpDocument(buf);
                if (doc != null)
                {
                    EditorLineAnnotation lens;
                    if (httpLensCache.TryGetValue(line, out lens)) return lens;
                }
            }
            if (!fileBlameMode && (!IsLineBlameEnabled || line != buf.CursorLine)) return null;
            if (buf.HasPendingDiff) return null;
            var res = blameService?.TryGetCached(buf.FilePath);
            string expected;
            if (res == null || res.NotTracked || !expectedBlameKeys.TryGetValue(buf.FilePath, out expected) || expected != res.ContentKey) return null;
            if (!ReferenceEquals(res, annotatedBlameResult)) { annotatedBlameResult = res; blameAnnotations.Clear(); }
            EditorLineAnnotation ann;
            if (blameAnnotations.TryGetValue(line, out ann)) return FileBlameFilter(res, line, buf, ann);
            GitBlameLine bl;
            if (!res.LinesByZeroBasedIndex.TryGetValue(line, out bl)) return null;
            ann = new EditorLineAnnotation
            {
                Kind = EditorLineAnnotation.KindBlame,
                Text = GitBlameParser.FormatAnnotation(bl.Commit, res.UserName, DateTime.UtcNow),
                Tag = bl
            };
            blameAnnotations[line] = ann;
            return FileBlameFilter(res, line, buf, ann);
        }

        /// <summary>File blame shows the first line of each run of lines from the same commit (plus the caret line).</summary>
        private EditorLineAnnotation FileBlameFilter(GitBlameFileResult res, int line, EditorTabBuffer buf, EditorLineAnnotation ann)
        {
            if (!fileBlameMode || line == buf.CursorLine) return ann;
            GitBlameLine prev;
            var cur = ann.Tag as GitBlameLine;
            if (cur != null && res.LinesByZeroBasedIndex.TryGetValue(line - 1, out prev) && prev.Commit != null && prev.Commit.Hash == cur.Commit.Hash) return null;
            return ann;
        }

        private HttpFileDocument GetHttpDocument(EditorTabBuffer buf)
        {
            if (!ReferenceEquals(buf, httpDocBuffer) || httpDocStamp != bufferEditStamp || httpDoc == null)
            {
                httpDocBuffer = buf;
                httpDocStamp = bufferEditStamp;
                httpDoc = HttpFileParser.Parse(buf.Lines);
                httpLensCache.Clear();
                foreach (var r in httpDoc.Requests)
                {
                    if (r.RequestLine < 0) continue;
                    httpLensCache[r.RequestLine] = new EditorLineAnnotation
                    {
                        Kind = EditorLineAnnotation.KindCodeLens,
                        Text = "▶ Send Request",
                        IsAction = true,
                        Tag = r
                    };
                }
            }
            return httpDoc;
        }

        private void ScheduleBlame()
        {
            if (blameDebounce == null || (!IsLineBlameEnabled && !fileBlameMode)) return;
            blameDebounce.Stop();
            blameDebounce.Start();
        }

        private void RequestBlameForActiveBuffer()
        {
            var buf = PrimaryEditor.CurrentBuffer;
            if (buf == null || buf.IsUntitled || string.IsNullOrEmpty(buf.FilePath) || (!IsLineBlameEnabled && !fileBlameMode)) return;
            if (!File.Exists(buf.FilePath)) return;
            string content = string.Join("\n", buf.Lines);
            if (buf.HasFinalNewline) content += "\n";
            string key = GitBlameService.ComputeContentKey(content);
            expectedBlameKeys[buf.FilePath] = key;
            EnsureBlameService().Request(buf.FilePath, content, buf.IsDirty, buf.CursorLine, buf.Lines.Count);
            PrimaryEditor.Canvas.InvalidateVisual();
        }

        public void ToggleLineBlame()
        {
            settings.ShowInlineGitBlame = !settings.ShowInlineGitBlame;
            try { settings.Save(); } catch { }
            UpdateDevToolsPollTimer();
            SetStatusMessage("Inline git blame: " + (IsLineBlameEnabled ? "On" : "Off"));
            if (IsLineBlameEnabled) RequestBlameForActiveBuffer();
            PrimaryEditor.Canvas.InvalidateVisual();
        }

        public void ToggleFileBlame()
        {
            fileBlameMode = !fileBlameMode;
            UpdateDevToolsPollTimer();
            SetStatusMessage("File blame: " + (fileBlameMode ? "On" : "Off"));
            if (fileBlameMode) RequestBlameForActiveBuffer();
            PrimaryEditor.Canvas.InvalidateVisual();
        }

        private void OnLineAnnotationClicked(AvaloniaEditorControl editor, EditorTabBuffer buf, EditorLineAnnotation ann, int line, Rect rect)
        {
            if (ann == null) return;
            if (HandleParityAnnotationClick(editor, buf, ann, line, rect)) return;
            if (ann.Kind == EditorLineAnnotation.KindCodeLens && ann.Tag is HttpRequestBlock blk)
            {
                SendHttpRequest(buf, blk);
                return;
            }
            if (ann.Tag is GitBlameLine bl) ShowBlameCard(editor, buf, bl, rect);
        }

        private void OnLineAnnotationHovered(AvaloniaEditorControl editor, EditorTabBuffer buf, EditorLineAnnotation ann, int line, Rect rect)
        {
            blameHoverTimer.Stop();
            if (ann != null && ann.Tag is GitBlameLine bl)
            {
                EventHandler handler = null;
                handler = (s, e) =>
                {
                    blameHoverTimer.Stop();
                    blameHoverTimer.Tick -= handler;
                    ShowBlameCard(editor, buf, bl, rect);
                };
                blameHoverTimer.Tick += handler;
                blameHoverTimer.Start();
            }
            else if (ann == null && blameCard != null && blameCard.IsVisible)
            {
                DispatcherTimer.RunOnce(() => { if (!pointerOverBlameCard) HideBlameCard(); }, TimeSpan.FromMilliseconds(450));
            }
        }

        public void HideBlameCard()
        {
            if (blameCard != null) blameCard.IsVisible = false;
            pointerOverBlameCard = false;
        }

        /// <summary>GitLens-style hover card: hash, author, date, full message, Copy Hash / Open in Git Graph / Show Changes.</summary>
        public void ShowBlameCard(AvaloniaEditorControl editor, EditorTabBuffer buf, GitBlameLine bl, Rect anchor)
        {
            if (devToolsOverlay == null || bl == null || bl.Commit == null) return;
            var c = bl.Commit;
            var t = currentTheme;
            var bs = EnsureBlameService();
            var res = bs.TryGetCached(buf.FilePath);
            string repo = res != null ? res.RepoRoot : GitCli.FindRepoRoot(buf.FilePath);
            if (blameCard == null)
            {
                blameCard = new Border { CornerRadius = new CornerRadius(6), BorderThickness = new Thickness(1), Padding = new Thickness(12, 10), MaxWidth = 520, MinWidth = 300 };
                blameCard.PointerEntered += (s, e) => pointerOverBlameCard = true;
                blameCard.PointerExited += (s, e) =>
                {
                    pointerOverBlameCard = false;
                    DispatcherTimer.RunOnce(() => { if (!pointerOverBlameCard && !PrimaryEditor.Canvas.IsPointerOverAnnotation) HideBlameCard(); }, TimeSpan.FromMilliseconds(400));
                };
                devToolsOverlay.Children.Add(blameCard);
            }
            blameCard.Background = AvaloniaColorHelper.ToBrush(t.BgElevated);
            blameCard.BorderBrush = AvaloniaColorHelper.ToBrush(t.BorderSubtle);
            blameCard.BoxShadow = new BoxShadows(new BoxShadow { Blur = 16, OffsetY = 4, Color = Color.FromArgb(90, 0, 0, 0) });
            var sp = new StackPanel { Spacing = 6 };
            var fg = AvaloniaColorHelper.ToBrush(t.FgPrimary);
            var muted = AvaloniaColorHelper.ToBrush(t.FgSecondary);
            if (c.IsUncommitted)
            {
                sp.Children.Add(new TextBlock { Text = "Uncommitted changes", FontWeight = FontWeight.SemiBold, Foreground = fg });
                sp.Children.Add(new TextBlock { Text = "This line has not been committed yet.", Foreground = muted, FontSize = 12 });
            }
            else
            {
                var head = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
                head.Children.Add(new TextBlock { Text = c.Author, FontWeight = FontWeight.SemiBold, Foreground = fg });
                head.Children.Add(new TextBlock { Text = string.IsNullOrEmpty(c.AuthorMail) ? "" : "<" + c.AuthorMail + ">", Foreground = muted, FontSize = 11.5, VerticalAlignment = VerticalAlignment.Center });
                sp.Children.Add(head);
                sp.Children.Add(new TextBlock
                {
                    Text = GitCli.FormatRelativeTime(c.AuthorTimeUtc, DateTime.UtcNow) + "  ·  " + c.AuthorTimeUtc.ToLocalTime().ToString("dddd, MMMM d, yyyy HH:mm", CultureInfo.InvariantCulture),
                    Foreground = muted,
                    FontSize = 11.5
                });
                var msg = new SelectableTextBlock { Text = c.Summary, TextWrapping = TextWrapping.Wrap, Foreground = fg, FontSize = 12.5, Margin = new Thickness(0, 4, 0, 4) };
                sp.Children.Add(msg);
                string full = bs.TryGetFullMessage(repo, c.Hash);
                if (full != null) msg.Text = full;
                else
                {
                    string capturedRepo = repo, hash = c.Hash;
                    Task.Run(() =>
                    {
                        string m = bs.LoadFullMessage(capturedRepo, hash);
                        devToolsUi.Post(() => { if (!string.IsNullOrEmpty(m)) msg.Text = m; });
                    });
                }
                var hashText = new SelectableTextBlock { Text = c.Hash, FontFamily = new FontFamily(DevToolsFonts.MonoFamily), FontSize = 11, Foreground = muted };
                sp.Children.Add(hashText);
                var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Margin = new Thickness(0, 4, 0, 0) };
                var copy = new Button { Content = "Copy Hash", FontSize = 11, Padding = new Thickness(8, 2) };
                copy.Click += (s, e) => { CopyTextToClipboard(c.Hash); HideBlameCard(); };
                var graph = new Button { Content = "Open in Git Graph", FontSize = 11, Padding = new Thickness(8, 2) };
                graph.Click += (s, e) => { HideBlameCard(); ShowGitGraph(c.Hash, repo); };
                var changes = new Button { Content = "Show Changes", FontSize = 11, Padding = new Thickness(8, 2) };
                ToolTip.SetTip(changes, "Diff of this file in commit " + c.ShortHash);
                changes.Click += (s, e) => { HideBlameCard(); OpenCommitFileDiff(repo, c.Hash, string.IsNullOrEmpty(c.Filename) ? GitCli.RepoRelativePath(repo, buf.FilePath) : c.Filename); };
                actions.Children.Add(copy);
                actions.Children.Add(graph);
                actions.Children.Add(changes);
                sp.Children.Add(actions);
            }
            blameCard.Child = sp;
            var origin = editor.Canvas.TranslatePoint(new Point(anchor.X, anchor.Bottom + 2), devToolsOverlay) ?? new Point(anchor.X, anchor.Bottom);
            double maxX = Math.Max(0, devToolsOverlay.Bounds.Width - 540);
            Canvas.SetLeft(blameCard, Math.Max(8, Math.Min(origin.X, maxX)));
            double top = origin.Y;
            if (top + 220 > devToolsOverlay.Bounds.Height) top = Math.Max(8, origin.Y - anchor.Height - 230);
            Canvas.SetTop(blameCard, top);
            blameCard.IsVisible = true;
        }

        // ------------------------------------------------------------------------------------------------
        // Git graph / history / diffs
        // ------------------------------------------------------------------------------------------------

        private string ResolveRepoForActiveContext()
        {
            string r = null;
            if (activeTab != null && !string.IsNullOrEmpty(activeTab.FilePath)) r = GitCli.FindRepoRoot(activeTab.FilePath);
            if (r == null && !string.IsNullOrEmpty(workspaceRoot)) r = GitCli.FindRepoRoot(workspaceRoot);
            return r;
        }

        public GitGraphView ShowGitGraph(string selectHash = null, string repoOverride = null)
        {
            string repo = repoOverride ?? ResolveRepoForActiveContext();
            if (repo == null) { SetStatusMessage("Git Graph: no git repository found for the open folder or file."); return null; }
            string id = "git:graph:" + repo;
            var existing = auxPane.Find(id);
            GitGraphView view = existing?.Content as GitGraphView;
            if (view == null)
            {
                view = new GitGraphView(repo, null);
                WireGraphView(view);
                OpenAuxDocument(new AuxPaneDocument { Id = id, Title = "Git Graph", Tooltip = repo, Content = view, ApplyTheme = view.ApplyTheme }, maximize: false);
            }
            else OpenAuxDocument(existing);
            if (!string.IsNullOrEmpty(selectHash)) view.SelectCommit(selectHash);
            CheckGitAvailableAsync();
            return view;
        }

        public GitGraphView ShowFileHistory()
        {
            var buf = activeTab;
            if (buf == null || string.IsNullOrEmpty(buf.FilePath)) { SetStatusMessage("File History: open a file first."); return null; }
            string repo = GitCli.FindRepoRoot(buf.FilePath);
            string rel = repo != null ? GitCli.RepoRelativePath(repo, buf.FilePath) : null;
            if (rel == null) { SetStatusMessage("File History: " + Path.GetFileName(buf.FilePath) + " is not inside a git repository."); return null; }
            string id = "git:history:" + buf.FilePath;
            var view = new GitGraphView(repo, rel);
            WireGraphView(view);
            OpenAuxDocument(new AuxPaneDocument { Id = id, Title = "History: " + Path.GetFileName(buf.FilePath), Tooltip = rel, Content = view, ApplyTheme = view.ApplyTheme });
            CheckGitAvailableAsync();
            return view;
        }

        private void CheckGitAvailableAsync()
        {
            Task.Run(() =>
            {
                if (!GitCli.IsAvailable()) devToolsUi.Post(() => SetStatusMessage(GitCli.GitMissingHint));
            });
        }

        private void WireGraphView(GitGraphView view)
        {
            view.CopyToClipboard = CopyTextToClipboard;
            view.FileDiffRequested += (commit, file) => OpenCommitFileDiff(view.RepoRoot, commit, file);
        }

        private void OpenCommitFileDiff(string repo, GitCommitInfo commit, GitChangedFile file)
        {
            string id = "diff:" + commit.Hash + ":" + file.Path;
            string title = Path.GetFileName(file.Path) + " (" + commit.ShortHash + ")";
            Task.Run(() =>
            {
                string oldT, newT, note;
                GitHistoryService.GetFileVersions(repo, commit, file, out oldT, out newT, out note);
                devToolsUi.Post(() =>
                {
                    var dv = new DiffView(file.DisplayName + "  ·  " + GitHistoryService.StatusLabel(file.Status) + " in " + commit.ShortHash + " " + commit.Subject, oldT, newT, note);
                    OpenAuxDocument(new AuxPaneDocument { Id = id, Title = title, Tooltip = file.Path, Content = dv, ApplyTheme = dv.ApplyTheme });
                });
            });
        }

        /// <summary>Diff of one file in a commit given only its hash (from blame).</summary>
        private void OpenCommitFileDiff(string repo, string hash, string repoRelPath)
        {
            if (string.IsNullOrEmpty(repo) || string.IsNullOrEmpty(hash) || string.IsNullOrEmpty(repoRelPath)) return;
            Task.Run(() =>
            {
                int exit;
                string raw = GitCli.Run(repo, 10000, out exit, "log", "-1", GitLogParser.LogFormat, hash);
                var commits = exit == 0 ? GitLogParser.Parse(raw, null) : new List<GitCommitInfo>();
                if (commits.Count == 0) return;
                string err;
                var files = GitHistoryService.GetChangedFiles(repo, commits[0], out err);
                var file = files.FirstOrDefault(f => f.Path == repoRelPath) ?? new GitChangedFile { Path = repoRelPath, Status = "M" };
                devToolsUi.Post(() => OpenCommitFileDiff(repo, commits[0], file));
            });
        }

        // ------------------------------------------------------------------------------------------------
        // HTTP client
        // ------------------------------------------------------------------------------------------------

        public void SendHttpRequestAtCaret()
        {
            var buf = activeTab;
            if (buf == null || !HttpFileParser.IsHttpFile(buf.FilePath)) { SetStatusMessage("Send Request works in .http / .rest files."); return; }
            var doc = GetHttpDocument(buf);
            var blk = HttpFileParser.FindRequestAtLine(doc, buf.CursorLine);
            if (blk == null) { SetStatusMessage("No request at the cursor (requests are separated by ###)."); return; }
            SendHttpRequest(buf, blk);
        }

        private HttpVariableResolver ResolverFor(EditorTabBuffer buf)
        {
            HttpVariableResolver r;
            if (!httpResolvers.TryGetValue(buf.FilePath, out r)) { r = new HttpVariableResolver(); httpResolvers[buf.FilePath] = r; }
            return r;
        }

        public HttpResponseView HttpResponsePane
        {
            get { return auxPane?.Find("http:response")?.Content as HttpResponseView; }
        }

        public async void SendHttpRequest(EditorTabBuffer buf, HttpRequestBlock blk)
        {
            if (buf == null || blk == null) return;
            var resolver = ResolverFor(buf);
            var doc = GetHttpDocument(buf);
            string filePath = buf.FilePath;
            string envName = settings.HttpClientEnvironment ?? "";
            string root = workspaceRoot;
            var env = await Task.Run(() => HttpEnvironmentSet.Discover(filePath, root));
            resolver.FileVariables = new Dictionary<string, string>(doc.FileVariables, StringComparer.Ordinal);
            resolver.EnvironmentVariables = env.GetVariables(envName);
            var req = resolver.Resolve(blk, Path.GetDirectoryName(filePath));

            var view = HttpResponsePane;
            if (view == null)
            {
                view = new HttpResponseView { CopyToClipboard = CopyTextToClipboard };
                var v = view;
                v.ResendRequested += () =>
                {
                    var b = activeTab != null && string.Equals(activeTab.FilePath, filePath, StringComparison.OrdinalIgnoreCase) ? activeTab : buf;
                    var d = GetHttpDocument(b);
                    var again = d.Requests.FirstOrDefault(r => r.RequestLine == blk.RequestLine) ?? blk;
                    SendHttpRequest(b, again);
                };
                OpenAuxDocument(new AuxPaneDocument { Id = "http:response", Title = "Response", Content = view, ApplyTheme = view.ApplyTheme, Closed = () => { try { v.ShowResult(new HttpResponseResult { Cancelled = true, Error = "Closed" }); } catch { } } });
            }
            else OpenAuxDocument(auxPane.Find("http:response"));
            var docEntry = auxPane.Find("http:response");
            if (docEntry != null) { docEntry.Title = "Response: " + (string.IsNullOrEmpty(blk.Name) ? req.Method + " " + ShortUrl(req.Url) : blk.Name); auxPane.RefreshTitles(); }
            if (req.UnresolvedVariables.Count > 0)
                SetStatusMessage("HTTP: unresolved variables " + string.Join(", ", req.UnresolvedVariables.Take(4)) + (req.UnresolvedVariables.Any(u => u.Contains(".response.")) ? " (send the named request first)" : ""));
            else SetStatusMessage("HTTP: " + req.Method + " " + req.Url + (envName.Length > 0 ? "  [env: " + envName + "]" : ""));
            var token = view.BeginRequest(req);
            int timeoutMs = settings.HttpClientTimeoutSeconds > 0 ? settings.HttpClientTimeoutSeconds * 1000 : HttpClientRunner.DefaultTimeoutMs;
            HttpResponseResult result;
            try { result = await HttpClientRunner.SendAsync(req, timeoutMs, token); }
            catch (Exception ex) { result = new HttpResponseResult { Request = req, Error = "Request failed: " + ex.Message }; }
            view.ShowResult(result);
            if (result.HasResponse && !string.IsNullOrEmpty(blk.Name)) resolver.NamedResponses[blk.Name] = result;
            SetStatusMessage("HTTP: " + HttpClientRunner.StatusSummary(result));
        }

        private static string ShortUrl(string url)
        {
            Uri u;
            if (Uri.TryCreate(url, UriKind.Absolute, out u)) return u.AbsolutePath.Length > 1 ? u.AbsolutePath : u.Host;
            return url;
        }

        public void CopyHttpRequestAsCurl()
        {
            var buf = activeTab;
            if (buf == null || !HttpFileParser.IsHttpFile(buf.FilePath)) return;
            var blk = HttpFileParser.FindRequestAtLine(GetHttpDocument(buf), buf.CursorLine);
            if (blk == null) return;
            var resolver = ResolverFor(buf);
            resolver.FileVariables = GetHttpDocument(buf).FileVariables;
            resolver.EnvironmentVariables = HttpEnvironmentSet.Discover(buf.FilePath, workspaceRoot).GetVariables(settings.HttpClientEnvironment ?? "");
            CopyTextToClipboard(HttpClientRunner.ToCurl(resolver.Resolve(blk, Path.GetDirectoryName(buf.FilePath))));
        }

        public void PickHttpEnvironment()
        {
            string basePath = activeTab != null && !string.IsNullOrEmpty(activeTab.FilePath) ? activeTab.FilePath : Path.Combine(workspaceRoot ?? "", "x.http");
            var set = HttpEnvironmentSet.Discover(basePath, workspaceRoot);
            var entries = new List<PaletteEntryItem>
            {
                new PaletteEntryItem { Category = "HTTP Environment", Title = "No Environment", stringSubtitleOrPath = "$shared variables only", Execute = () => SetHttpEnvironment("") }
            };
            foreach (var n in set.Names)
            {
                string name = n;
                entries.Add(new PaletteEntryItem { Category = "HTTP Environment", Title = name, stringSubtitleOrPath = name == settings.HttpClientEnvironment ? "current" : "", Execute = () => SetHttpEnvironment(name) });
            }
            if (set.Names.Count == 0) SetStatusMessage("No environments found. Create http-client.env.json next to your .http file: {\"dev\": {\"host\": \"http://localhost:8080\"}}");
            CommandPalette.Open(">", entries, currentTheme?.Id);
        }

        private void SetHttpEnvironment(string name)
        {
            settings.HttpClientEnvironment = name ?? "";
            try { settings.Save(); } catch { }
            SetStatusMessage("HTTP environment: " + (string.IsNullOrEmpty(name) ? "none" : name));
        }

        // ------------------------------------------------------------------------------------------------
        // Markdown preview
        // ------------------------------------------------------------------------------------------------

        private static string MarkdownDocId(string path) => "md:" + (path ?? "");

        public MarkdownPreviewView OpenMarkdownPreview(bool toSide)
        {
            var buf = activeTab;
            if (buf == null || !MarkdownLinkResolver.IsMarkdownFile(buf.FilePath)) { SetStatusMessage("Markdown preview: open a .md file first."); return null; }
            string id = MarkdownDocId(buf.FilePath);
            var existing = auxPane.Find(id);
            MarkdownPreviewView view = existing?.Content as MarkdownPreviewView;
            if (view == null)
            {
                view = new MarkdownPreviewView(buf.FilePath);
                var v = view;
                view.LinkClicked += (url, target) => HandleMarkdownLink(v, target);
                OpenAuxDocument(new AuxPaneDocument { Id = id, Title = "Preview " + Path.GetFileName(buf.FilePath), Tooltip = buf.FilePath, Content = view, ApplyTheme = view.ApplyTheme }, maximize: !toSide);
            }
            else
            {
                OpenAuxDocument(existing, maximize: !toSide);
                if (toSide && auxPane.IsMaximized) auxPane.SetMaximized(false);
            }
            view.Render(string.Join("\n", buf.Lines));
            var vv = view;
            MainUi.Post(() => vv.SyncToSourceLine(buf.ScrollTopLine), DispatcherPriority.Background);
            return view;
        }

        private void RefreshMarkdownPreviewFor(EditorTabBuffer buf)
        {
            if (buf == null) return;
            var view = auxPane.Find(MarkdownDocId(buf.FilePath))?.Content as MarkdownPreviewView;
            view?.Render(string.Join("\n", buf.Lines));
        }

        private void SyncMarkdownScroll(EditorTabBuffer buf)
        {
            if (buf == null || auxPane == null || !auxPane.IsVisible || !MarkdownLinkResolver.IsMarkdownFile(buf.FilePath)) return;
            var view = auxPane.Find(MarkdownDocId(buf.FilePath))?.Content as MarkdownPreviewView;
            if (view != null && ReferenceEquals(auxPane.ActiveDocument?.Content, view)) view.SyncToSourceLine(buf.ScrollTopLine);
        }

        private void HandleMarkdownLink(MarkdownPreviewView view, MdLinkTarget target)
        {
            if (target == null) return;
            if (target.Kind == MdLinkTarget.KindExternal)
            {
                if (!EditorLinkResolver.OpenExternalUrl(target.Url)) SetStatusMessage("Could not open " + target.Url);
                return;
            }
            if (target.Kind == MdLinkTarget.KindFile)
            {
                if (File.Exists(target.FilePath))
                {
                    if (auxPane.IsMaximized) auxPane.SetMaximized(false);
                    OpenFileInTab(target.FilePath, preview: false);
                }
                else if (Directory.Exists(target.FilePath)) SetStatusMessage("Link points to folder " + target.FilePath);
                else SetStatusMessage("Linked file not found: " + target.FilePath);
                return;
            }
            if (target.Kind == MdLinkTarget.KindUnsupported) SetStatusMessage("Unsupported link: " + target.Url);
        }

        // ------------------------------------------------------------------------------------------------
        // Commands & shortcuts
        // ------------------------------------------------------------------------------------------------

        /// <summary>Ctrl/Cmd+Shift+V (preview), Ctrl/Cmd+Alt+R (send request), Escape (hover card).</summary>
        private bool HandleDevToolsShortcut(Key key, KeyModifiers modifiers)
        {
            bool primary = MacKeymap.IsPrimary(modifiers);
            bool shift = modifiers.HasFlag(KeyModifiers.Shift);
            bool alt = modifiers.HasFlag(KeyModifiers.Alt);
            if (key == Key.Escape && blameCard != null && blameCard.IsVisible) { HideBlameCard(); return true; }
            if (primary && shift && !alt && key == Key.V && activeTab != null && MarkdownLinkResolver.IsMarkdownFile(activeTab.FilePath))
            {
                OpenMarkdownPreview(false);
                return true;
            }
            if (primary && alt && !shift && key == Key.R && activeTab != null && HttpFileParser.IsHttpFile(activeTab.FilePath))
            {
                SendHttpRequestAtCaret();
                return true;
            }
            return false;
        }

        private List<PaletteEntryItem> BuildDevToolsPaletteCommands()
        {
            return new List<PaletteEntryItem>
            {
                new PaletteEntryItem { Category = "Git", Title = "Toggle Line Blame", Execute = ToggleLineBlame },
                new PaletteEntryItem { Category = "Git", Title = "Toggle File Blame", Execute = ToggleFileBlame },
                new PaletteEntryItem { Category = "Git", Title = "Show Graph", Execute = () => ShowGitGraph() },
                new PaletteEntryItem { Category = "Git", Title = "File History", Execute = () => ShowFileHistory() },
                new PaletteEntryItem { Category = "HTTP", Title = "Send Request", Shortcut = "Ctrl+Alt+R", Execute = SendHttpRequestAtCaret },
                new PaletteEntryItem { Category = "HTTP", Title = "Switch Environment...", Execute = PickHttpEnvironment },
                new PaletteEntryItem { Category = "HTTP", Title = "Copy Request as cURL", Execute = CopyHttpRequestAsCurl },
                new PaletteEntryItem { Category = "Markdown", Title = "Open Preview", Shortcut = "Ctrl+Shift+V", Execute = () => OpenMarkdownPreview(false) },
                new PaletteEntryItem { Category = "Markdown", Title = "Open Preview to the Side", Shortcut = "Ctrl+K V", Execute = () => OpenMarkdownPreview(true) },
                new PaletteEntryItem { Category = "View", Title = "Close Side Pane", Execute = CloseAuxPane }
            };
        }
    }
}
