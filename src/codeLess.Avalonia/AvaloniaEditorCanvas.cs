using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using codeLess.Studio;

namespace codeLess.AvaloniaApp
{
    public struct ExtraCursorCaret
    {
        public int Line;
        public int Col;
        public int AnchorLine;
        public int AnchorCol;

        public bool HasSelection => AnchorLine >= 0 && (AnchorLine != Line || AnchorCol != Col);
    }

    public struct FindMatchSpan
    {
        public int Line;
        public int StartCol;
        public int Length;
    }

    public partial class AvaloniaEditorControl : Grid
    {
        public EditorTabBuffer CurrentBuffer { get; private set; }
        public AvaloniaVirtualEditorCanvas Canvas { get; }
        public bool ShowWelcomeView { get; private set; } = true;
        public bool ShowMinimap
        {
            get => Canvas.ShowMinimap;
            set { Canvas.ShowMinimap = value; Canvas.InvalidateVisual(); }
        }
        public bool ShowBreadcrumbs
        {
            get => breadcrumbBar.IsVisible;
            set { breadcrumbBar.IsVisible = value && !ShowWelcomeView; }
        }
        public bool ColumnSelectionMode
        {
            get => Canvas.ColumnSelectionMode;
            set => Canvas.ColumnSelectionMode = value;
        }

        private readonly Border breadcrumbBar;
        private readonly StackPanel breadcrumbSegmentsPanel;
        private readonly Border diffReviewBanner;
        private readonly TextBlock diffBannerSummaryText;
        private readonly Border findReplaceOverlay;
        private readonly TextBox findInputBox;
        private readonly TextBox replaceInputBox;
        private readonly StackPanel replaceRowPanel;
        private readonly TextBlock findCountLabel;
        private readonly Button matchCaseBtn;
        private readonly Button wholeWordBtn;
        private readonly Button regexBtn;
        private readonly Border inlineAiBar;
        private readonly TextBox inlineAiInputBox;
        private readonly Border welcomeHost;
        /// <summary>VS Code's empty editor group (faded logo + key shortcuts), shown when Welcome is closed and no file is open.</summary>
        private readonly Border emptyHost;
        private readonly StackPanel emptyShortcuts;
        /// <summary>False once the Welcome tab was closed: an empty editor area then shows the watermark instead of Welcome.</summary>
        public bool WelcomeEnabled { get; set; } = true;
        public bool IsEmptyWatermarkVisible => emptyHost.IsVisible;
        public bool IsWelcomePageVisible => welcomeHost.IsVisible;
        private readonly StackPanel recentFoldersPanel;

        private bool findMatchCase = false;
        private bool findWholeWord = false;
        private bool findUseRegex = false;
        private bool findPreserveCase = false;
        private bool findInSelection = false;
        private EditorFindScope findScope;
        private Button preserveCaseBtn;
        private Button inSelectionBtn;
        private string findError;
        private int activeFindMatchIndex = -1;
        public List<FindMatchSpan> FindMatches { get; } = new List<FindMatchSpan>();

        private StudioTheme currentTheme = StudioTheme.GetById(StudioTheme.DefaultThemeId);
        private string workspaceRoot = "";
        private List<string> recentWorkspaces = new List<string>();
        private string lastBreadcrumbStateKey = "";

        public event Action<EditorTabBuffer> BufferModified;
        public event Action<EditorTabBuffer> CaretMoved;
        public event Action<string, EditorTabBuffer> InlineAiEditRequested;
        public event Action<string> WelcomeActionRequested; // "new-file", "open-file", "open-folder", "open-chat", "theme-picker", "byok-dialog", "arena-dialog"
        public event Action<string> OpenRecentWorkspaceRequested;
        public event Action<EditorTabBuffer, Point> EditorContextMenuRequested;
        public event Action<EditorTabBuffer, bool> DiffAllDecisionRequested; // (buf, accepted)
        public event Action<string> StatusMessagePublished;

        /// <summary>End-of-line annotations (inline git blame, "Send Request" lens). Called per visible line while rendering.</summary>
        public Func<EditorTabBuffer, int, EditorLineAnnotation> LineAnnotationProvider { get; set; }
        /// <summary>Annotation clicked: (buffer, annotation, 0-based line, rect in canvas coordinates).</summary>
        public event Action<EditorTabBuffer, EditorLineAnnotation, int, Rect> LineAnnotationClicked;
        /// <summary>Pointer entered (annotation) or left (null) an annotation.</summary>
        public event Action<EditorTabBuffer, EditorLineAnnotation, int, Rect> LineAnnotationHovered;
        internal void NotifyAnnotationClicked(EditorLineAnnotation a, int line, Rect r) { if (CurrentBuffer != null) LineAnnotationClicked?.Invoke(CurrentBuffer, a, line, r); }
        internal void NotifyAnnotationHovered(EditorLineAnnotation a, int line, Rect r) { LineAnnotationHovered?.Invoke(CurrentBuffer, a, line, r); }
        /// <summary>Raised after the canvas scrolled vertically (Markdown preview scroll sync).</summary>
        public event Action<EditorTabBuffer> ScrollChanged;
        internal void NotifyScrollChanged() { if (CurrentBuffer != null) ScrollChanged?.Invoke(CurrentBuffer); }

        public AvaloniaEditorControl()
        {
            RowDefinitions = new RowDefinitions("Auto,Auto,*");

            // Row 0: Breadcrumbs bar (24px)
            breadcrumbSegmentsPanel = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 4,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(12, 0)
            };
            breadcrumbBar = new Border
            {
                Height = 24,
                Child = breadcrumbSegmentsPanel,
                BorderThickness = new Thickness(0, 0, 0, 1),
                IsVisible = false
            };
            Grid.SetRow(breadcrumbBar, 0);
            Children.Add(breadcrumbBar);

            // Row 1: Inline Diff Review Banner (28px, hidden unless buffer has pending diff)
            var diffGrid = new Grid
            {
                ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto,Auto,Auto"),
                Margin = new Thickness(12, 0)
            };
            diffBannerSummaryText = new TextBlock
            {
                Text = "Proposed Changes",
                FontWeight = FontWeight.Normal,
                FontSize = UiTokens.FontSmall,
                VerticalAlignment = VerticalAlignment.Center
            };
            var prevHunkBtn = WorkbenchStyles.IconButton("arrow-up", "Previous Change", null, currentTheme);
            var nextHunkBtn = WorkbenchStyles.IconButton("arrow-down", "Next Change", null, currentTheme);
            prevHunkBtn.Margin = new Thickness(2, 0);
            nextHunkBtn.Margin = new Thickness(2, 0, 8, 0);
            var acceptAllBtn = new Button
            {
                Name = "AcceptAllDiffBtn",
                Content = "Accept All",
                FontSize = UiTokens.FontSmall,
                Padding = new Thickness(10, 2),
                MinHeight = 22,
                Margin = new Thickness(4, 0),
                VerticalAlignment = VerticalAlignment.Center
            };
            acceptAllBtn.Classes.Add("primary");
            ToolTip.SetTip(acceptAllBtn, "Accept all proposed changes (Ctrl+Enter)");
            var rejectAllBtn = new Button
            {
                Name = "RejectAllDiffBtn",
                Content = "Reject All",
                FontSize = UiTokens.FontSmall,
                Padding = new Thickness(10, 2),
                MinHeight = 22,
                Margin = new Thickness(2, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center
            };
            ToolTip.SetTip(rejectAllBtn, "Reject all proposed changes (Esc)");
            diffBannerButtons = new[] { prevHunkBtn, nextHunkBtn, acceptAllBtn, rejectAllBtn };
            prevHunkBtn.Click += (s, e) => JumpDiffHunk(-1);
            nextHunkBtn.Click += (s, e) => JumpDiffHunk(1);
            acceptAllBtn.Click += (s, e) => AcceptAllPendingDiffs();
            rejectAllBtn.Click += (s, e) => RejectAllPendingDiffs();

            diffGrid.Children.Add(diffBannerSummaryText);
            Grid.SetColumn(prevHunkBtn, 1); diffGrid.Children.Add(prevHunkBtn);
            Grid.SetColumn(nextHunkBtn, 2); diffGrid.Children.Add(nextHunkBtn);
            Grid.SetColumn(acceptAllBtn, 3); diffGrid.Children.Add(acceptAllBtn);
            Grid.SetColumn(rejectAllBtn, 4); diffGrid.Children.Add(rejectAllBtn);

            diffReviewBanner = new Border
            {
                Height = 30,
                Child = diffGrid,
                BorderThickness = new Thickness(0, 0, 0, 1),
                IsVisible = false
            };
            Grid.SetRow(diffReviewBanner, 1);
            Children.Add(diffReviewBanner);

            // Row 2: Editor Body (Canvas + Welcome Host + Floating Overlays)
            var bodyOverlayGrid = new Grid();
            Canvas = new AvaloniaVirtualEditorCanvas(this);
            bodyOverlayGrid.Children.Add(Canvas);
            // carets on their own layer right above the text (cheap blinking / smooth caret), hidden with the canvas
            var caretLayer = Canvas.CaretOverlay;
            caretLayer.Bind(IsVisibleProperty, Canvas.GetObservable(IsVisibleProperty));
            bodyOverlayGrid.Children.Add(caretLayer);

            // Welcome Page Host
            recentFoldersPanel = new StackPanel { Spacing = 4 };
            welcomeHost = BuildWelcomePage();
            bodyOverlayGrid.Children.Add(welcomeHost);
            emptyShortcuts = new StackPanel { Spacing = 10, HorizontalAlignment = HorizontalAlignment.Center };
            emptyHost = new Border
            {
                IsVisible = false,
                Child = new StackPanel
                {
                    Spacing = 28,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                    Children =
                    {
                        new VectorIconControl { IconName = "logo", Width = 96, Height = 96, Opacity = 0.12, HorizontalAlignment = HorizontalAlignment.Center },
                        emptyShortcuts
                    }
                }
            };
            bodyOverlayGrid.Children.Add(emptyHost);

            // Floating Find/Replace Widget (top-right)
            findReplaceOverlay = BuildFindReplaceOverlay(
                out findInputBox, out replaceInputBox, out replaceRowPanel,
                out findCountLabel, out matchCaseBtn, out wholeWordBtn, out regexBtn);
            bodyOverlayGrid.Children.Add(findReplaceOverlay);

            // Floating Inline AI Bar (Ctrl/Cmd+K or Ctrl/Cmd+I)
            inlineAiBar = BuildInlineAiOverlay(out inlineAiInputBox);
            bodyOverlayGrid.Children.Add(inlineAiBar);

            Grid.SetRow(bodyOverlayGrid, 2);
            Children.Add(bodyOverlayGrid);
            InitLargeFileHost(bodyOverlayGrid);
            OverlayHost = bodyOverlayGrid;
        }

        /// <summary>Grid stacked over the editor body; hosts floating widgets (find bar, LSP completion / hover popups).</summary>
        public Grid OverlayHost { get; private set; }

        public bool IsFindOverlayVisible => findReplaceOverlay.IsVisible;
        public bool IsReplaceRowVisible => replaceRowPanel.IsVisible;
        public bool IsInlineAiBarVisible => inlineAiBar.IsVisible;
        public bool IsDiffBannerVisible => diffReviewBanner.IsVisible;
        public string BreadcrumbText { get; private set; } = "";

        public void SetWorkspaceContext(string rootDir, List<string> recents)
        {
            workspaceRoot = rootDir ?? "";
            if (recents != null)
                recentWorkspaces = new List<string>(recents);
            RefreshRecentFoldersOnWelcome();
            RefreshBreadcrumbs();
        }

        public void LoadBuffer(EditorTabBuffer tab)
        {
            CurrentBuffer = tab;
            if (SyncLargeFileViewer(tab)) return;
            if (tab == null)
            {
                ShowWelcomeView = true;
                welcomeHost.IsVisible = WelcomeEnabled;
                emptyHost.IsVisible = !WelcomeEnabled;
                Canvas.IsVisible = false;
                breadcrumbBar.IsVisible = false;
                diffReviewBanner.IsVisible = false;
                RefreshRecentFoldersOnWelcome();
                return;
            }

            ShowWelcomeView = false;
            welcomeHost.IsVisible = false;
            emptyHost.IsVisible = false;
            Canvas.IsVisible = true;
            breadcrumbBar.IsVisible = true;
            Canvas.ResetExtraCursors();
            Canvas.BindUndoCaretSource(tab);
            RefreshBreadcrumbs();
            RefreshDiffBanner();
            if (findReplaceOverlay.IsVisible)
                RecomputeFindMatches();
            Canvas.EnsureCursorVisible();
            Canvas.InvalidateVisual();
        }

        public void ShowWelcomeScreen()
        {
            CurrentBuffer = null;
            SyncLargeFileViewer(null);
            ShowWelcomeView = true;
            welcomeHost.IsVisible = WelcomeEnabled;
            emptyHost.IsVisible = !WelcomeEnabled;
            Canvas.IsVisible = false;
            breadcrumbBar.IsVisible = false;
            diffReviewBanner.IsVisible = false;
            RefreshRecentFoldersOnWelcome();
        }

        public void ApplyTheme(StudioTheme theme)
        {
            if (theme == null) return;
            currentTheme = theme;
            Background = AvaloniaColorHelper.ToBrush(theme.EditorBg);
            breadcrumbBar.Background = AvaloniaColorHelper.ToBrush(theme.EditorBg);
            breadcrumbBar.BorderBrush = AvaloniaColorHelper.ToBrush(theme.BorderSubtle);

            bool isLight = AvaloniaColorHelper.IsLightColor(theme.EditorBg);
            diffReviewBanner.Background = WorkbenchStyles.B(UiTokens.WidgetBackground(theme));
            diffReviewBanner.BorderBrush = WorkbenchStyles.B(theme.BorderSubtle);
            diffBannerSummaryText.Foreground = AvaloniaColorHelper.ToBrush(theme.TextPrimary);
            foreach (var b in diffBannerButtons)
            {
                if (b.Classes.Contains("icon")) WorkbenchStyles.ThemeIconButton(b, theme);
                else if (b.Classes.Contains("primary")) WorkbenchStyles.StylePrimaryButton(b, theme);
                else WorkbenchStyles.StyleSecondaryButton(b, theme);
            }

            findReplaceOverlay.Background = WorkbenchStyles.B(UiTokens.WidgetBackground(theme));
            findReplaceOverlay.BorderBrush = WorkbenchStyles.B(UiTokens.WidgetBorder(theme));
            findReplaceOverlay.BoxShadow = WorkbenchStyles.WidgetShadow(theme);
            UpdateFindToggleButtonStyles();
            inlineAiBar.Background = AvaloniaColorHelper.ToBrush(theme.FloatingBg);
            inlineAiBar.BorderBrush = AvaloniaColorHelper.ToBrush(theme.AccentPrimary);

            welcomeHost.Background = AvaloniaColorHelper.ToBrush(theme.EditorBg);
            emptyHost.Background = AvaloniaColorHelper.ToBrush(theme.EditorBg);
            RebuildEmptyWatermark(theme);
            RebuildWelcomeThemeColors();
            RefreshBreadcrumbs();
            Canvas.ApplyTheme(theme);
            LargeFileViewer?.ApplyTheme(theme);
        }

        public void RefreshBreadcrumbs(bool onlyIfChanged = false)
        {
            if (CurrentBuffer == null)
            {
                if (breadcrumbSegmentsPanel.Children.Count > 0)
                    breadcrumbSegmentsPanel.Children.Clear();
                BreadcrumbText = "";
                lastBreadcrumbStateKey = "";
                return;
            }

            string rel = !string.IsNullOrEmpty(CurrentBuffer.RelativePath)
                ? CurrentBuffer.RelativePath
                : CurrentBuffer.FileName;

            AstSymbolItem activeSym = null;
            if (CurrentBuffer.Symbols != null && CurrentBuffer.Symbols.Count > 0)
            {
                int curLine1 = CurrentBuffer.CursorLine + 1;
                for (int i = 0; i < CurrentBuffer.Symbols.Count; i++)
                {
                    var cand = CurrentBuffer.Symbols[i];
                    if (cand.LineNumber <= curLine1 && (activeSym == null || cand.LineNumber >= activeSym.LineNumber))
                        activeSym = cand;
                }
                if (activeSym == null)
                    activeSym = CurrentBuffer.Symbols[0];
            }

            string stateKey = $"{currentTheme?.Id}|{rel}|{activeSym?.Kind}:{activeSym?.Name}:{activeSym?.LineNumber}";
            if (onlyIfChanged && string.Equals(stateKey, lastBreadcrumbStateKey, StringComparison.Ordinal))
                return;
            lastBreadcrumbStateKey = stateKey;

            breadcrumbSegmentsPanel.Children.Clear();

            Color textPrimary = AvaloniaColorHelper.ToAvColor(currentTheme.TextPrimary);
            Color textSecondary = AvaloniaColorHelper.ToAvColor(currentTheme.TextSecondary);
            Color accent = AvaloniaColorHelper.ToAvColor(currentTheme.AccentPrimary);

            // Breadcrumb displays clean path hierarchy without dirty indicator
            string[] parts = (rel ?? "Untitled").Replace('\\', '/').Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries);

            var sb = new StringBuilder();
            var secBrush = new SolidColorBrush(textSecondary);
            var priBrush = new SolidColorBrush(textPrimary);
            var accBrush = new SolidColorBrush(accent);
            for (int i = 0; i < parts.Length; i++)
            {
                if (i > 0)
                {
                    sb.Append(" › ");
                    breadcrumbSegmentsPanel.Children.Add(new TextBlock
                    {
                        Text = "›",
                        FontSize = 11.5,
                        Foreground = secBrush,
                        VerticalAlignment = VerticalAlignment.Center,
                        Margin = new Thickness(2, 0)
                    });
                }
                sb.Append(parts[i]);
                bool isLast = (i == parts.Length - 1);
                breadcrumbSegmentsPanel.Children.Add(new TextBlock
                {
                    Text = parts[i],
                    FontSize = UiTokens.FontSmall,
                    FontWeight = FontWeight.Normal,
                    Foreground = isLast ? priBrush : secBrush,
                    VerticalAlignment = VerticalAlignment.Center
                });
            }

            if (activeSym != null)
            {
                sb.Append(" › ").Append(activeSym.Name);
                breadcrumbSegmentsPanel.Children.Add(new TextBlock
                {
                    Text = "›",
                    FontSize = UiTokens.FontSmall + 1,
                    Foreground = AvaloniaColorHelper.ToBrush(currentTheme.FgMuted),
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(2, 0)
                });

                // VS Code breadcrumbs: symbols are plain secondary text, not accent-colored links.
                var symBtn = new Button
                {
                    Content = new LiteralTextBlock($"{activeSym.Kind}: {activeSym.Name}")
                    {
                        FontSize = UiTokens.FontSmall
                    },
                    FontSize = UiTokens.FontSmall,
                    Padding = new Thickness(4, 0),
                    MinHeight = 18,
                    Background = Brushes.Transparent,
                    Foreground = secBrush
                };
                symBtn.Classes.Add("flat");
                symBtn.Click += (s, e) => OpenBreadcrumbSymbolMenu(symBtn);
                breadcrumbSegmentsPanel.Children.Add(symBtn);
            }

            BreadcrumbText = sb.ToString();
        }

        private void OpenBreadcrumbSymbolMenu(Control anchor)
        {
            if (CurrentBuffer == null || CurrentBuffer.Symbols == null || CurrentBuffer.Symbols.Count == 0) return;
            var menu = new ContextMenu();
            var items = new List<MenuItem>();
            foreach (var sym in CurrentBuffer.Symbols)
            {
                var captured = sym;
                var mi = new MenuItem
                {
                    Header = new LiteralTextBlock($"{captured.Kind}  {captured.Name}   (Ln {captured.LineNumber})")
                };
                mi.Click += (s, e) =>
                {
                    GoToLine(captured.LineNumber);
                };
                items.Add(mi);
            }
            menu.ItemsSource = items;
            menu.Open(anchor);
        }

        public void RefreshDiffBanner()
        {
            if (CurrentBuffer != null && CurrentBuffer.HasPendingDiff)
            {
                int count = CurrentBuffer.DiffHunks != null ? CurrentBuffer.DiffHunks.Count(h => h.IsPending) : 0;
                diffBannerSummaryText.Text = count == 1 ? $"1 proposed change in {CurrentBuffer.FileName}" : $"{count} proposed changes in {CurrentBuffer.FileName}";
                diffReviewBanner.IsVisible = true;
            }
            else
            {
                diffReviewBanner.IsVisible = false;
            }
        }

        // Inline review of a proposed full-file text. The logic (derived hunks, per-hunk accept/reject, undo) is shared
        // with WinForms in codeLess.Core/InlineDiffReview.cs; this layer only refreshes the view.

        public void StageProposedDiffContent(string newFullContent)
        {
            if (CurrentBuffer == null || newFullContent == null) return;
            InlineDiffReview.Stage(CurrentBuffer, newFullContent);
            AfterInlineReviewChange();
            var first = CurrentBuffer.DiffHunks.Where(h => h.IsPending).OrderBy(h => h.StartLine).FirstOrDefault();
            if (first != null)
            {
                GoToLine(InlineDiffReview.AnchorLine(CurrentBuffer, first) + 1);
                Canvas.RevealDeletedZone(first.StartLine); // its removed lines are part of the change (QA #30)
            }
        }

        /// <summary>Re-derives the pending hunks after the buffer was edited outside the review actions (typing, undo).</summary>
        public void NotifyExternalBufferEdit()
        {
            if (CurrentBuffer == null || CurrentBuffer.PreDiffContent == null) return;
            InlineDiffReview.Refresh(CurrentBuffer);
            CurrentBuffer.SyncProposedDiffProjection();
            RefreshDiffBanner();
            Canvas.InvalidateVisual();
        }

        private void AfterInlineReviewChange()
        {
            CurrentBuffer.SyncProposedDiffProjection();
            CurrentBuffer.ClampCursor();
            RefreshDiffBanner();
            BufferModified?.Invoke(CurrentBuffer);
            Canvas.InvalidateVisual();
        }

        public void AcceptAllPendingDiffs()
        {
            if (CurrentBuffer == null) return;
            bool hadReview = CurrentBuffer.PreDiffContent != null || CurrentBuffer.HasPendingDiff;
            InlineDiffReview.AcceptAll(CurrentBuffer);
            CurrentBuffer.SyncProposedDiffProjection();
            RefreshDiffBanner();
            if (hadReview) DiffAllDecisionRequested?.Invoke(CurrentBuffer, true);
            BufferModified?.Invoke(CurrentBuffer);
            Canvas.InvalidateVisual();
        }

        public void RejectAllPendingDiffs()
        {
            if (CurrentBuffer == null || (CurrentBuffer.PreDiffContent == null && !CurrentBuffer.HasPendingDiff)) return;
            InlineDiffReview.RejectAll(CurrentBuffer);
            CurrentBuffer.SyncProposedDiffProjection();
            CurrentBuffer.ClampCursor();
            CurrentBuffer.IsDirty = CurrentBuffer.ComputeDirtyFromLines(CurrentBuffer.CursorLine);
            CurrentBuffer.RefreshGutterDiff();
            RefreshDiffBanner();
            DiffAllDecisionRequested?.Invoke(CurrentBuffer, false);
            BufferModified?.Invoke(CurrentBuffer);
            Canvas.InvalidateVisual();
        }

        public void AcceptDiffHunkAtLine(int lineIdx)
        {
            if (CurrentBuffer == null) return;
            if (InlineDiffReview.AcceptHunkAtLine(CurrentBuffer, lineIdx)) AfterInlineReviewChange();
        }

        public void RejectDiffHunkAtLine(int lineIdx)
        {
            if (CurrentBuffer == null) return;
            if (InlineDiffReview.RejectHunkAtLine(CurrentBuffer, lineIdx)) AfterInlineReviewChange();
        }

        public void JumpDiffHunk(int direction)
        {
            if (CurrentBuffer == null || CurrentBuffer.DiffHunks == null) return;
            var sorted = CurrentBuffer.DiffHunks.Where(h => h.IsPending).Select(h => InlineDiffReview.AnchorLine(CurrentBuffer, h)).Distinct().OrderBy(x => x).ToList();
            if (sorted.Count == 0) return;
            int cur = CurrentBuffer.CursorLine;
            int target = direction >= 0
                ? sorted.Where(x => x > cur).DefaultIfEmpty(sorted[0]).First()
                : sorted.Where(x => x < cur).DefaultIfEmpty(sorted[sorted.Count - 1]).Last();
            GoToLine(target + 1);
            Canvas.RevealDeletedZone(target);
        }

        public void GoToLine(int oneBasedLine, int oneBasedCol = 1)
        {
            if (CurrentBuffer == null) return;
            if (IsShowingLargeFile) { LargeFileViewer.GoToLine(oneBasedLine, oneBasedCol); return; }
            int line = Math.Max(0, Math.Min(CurrentBuffer.Lines.Count - 1, oneBasedLine - 1));
            int maxCol = CurrentBuffer.Lines[line].Length;
            int col = Math.Max(0, Math.Min(maxCol, oneBasedCol - 1));
            CurrentBuffer.CursorLine = line;
            CurrentBuffer.CursorCol = col;
            CurrentBuffer.ClearSelection();
            Canvas.ResetExtraCursors();
            Canvas.EnsureCursorVisible();
            RefreshBreadcrumbs();
            CaretMoved?.Invoke(CurrentBuffer);
            Canvas.InvalidateVisual();
        }

        public void ShowFindBar(bool includeReplace)
        {
            if (IsShowingLargeFile) { LargeFileViewer.ShowFind(); return; }
            findReplaceOverlay.IsVisible = true;
            replaceRowPanel.IsVisible = includeReplace;
            UpdateFindToggleButtonStyles();
            if (CurrentBuffer != null && CurrentBuffer.HasSelection())
            {
                string sel = CurrentBuffer.GetSelectedText();
                if (!string.IsNullOrEmpty(sel) && !sel.Contains("\n"))
                    findInputBox.Text = sel;
            }
            RecomputeFindMatches();
            findInputBox.Focus();
            findInputBox.SelectAll();
        }

        private string lastFindHistoryText, lastReplaceHistoryText;

        /// <summary>Up = older search, Down = newer (past the newest: what was being typed). True when handled.</summary>
        private bool TryNavigateFindHistory(TextBox box, InputHistory history, KeyEventArgs e)
        {
            if ((e.Key != Key.Up && e.Key != Key.Down) || e.KeyModifiers != KeyModifiers.None) return false;
            string next = e.Key == Key.Up ? history.Previous(box.Text ?? "") : history.Next(box.Text ?? "");
            if (next == null) return true; // nothing older / not navigating: swallow like VS Code
            if (ReferenceEquals(box, findInputBox)) lastFindHistoryText = next; else lastReplaceHistoryText = next;
            box.Text = next;
            box.CaretIndex = next.Length;
            box.SelectionStart = box.SelectionEnd = next.Length;
            return true;
        }

        /// <summary>Remembers the current find (and replace) text in the history.</summary>
        public void CommitFindHistory(bool includeReplace = false)
        {
            FindHistoryStore.Commit(FindHistoryStore.Find, findInputBox.Text ?? "");
            if (includeReplace) FindHistoryStore.Commit(FindHistoryStore.Replace, replaceInputBox.Text ?? "");
        }

        public void HideFindBar()
        {
            if (findReplaceOverlay.IsVisible) CommitFindHistory(replaceRowPanel.IsVisible && !string.IsNullOrEmpty(replaceInputBox.Text));
            findReplaceOverlay.IsVisible = false;
            FindMatches.Clear();
            activeFindMatchIndex = -1;
            Canvas.Focus();
            Canvas.InvalidateVisual();
        }

        public void ShowInlineAiBar()
        {
            inlineAiBar.IsVisible = true;
            inlineAiInputBox.Focus();
            inlineAiInputBox.SelectAll();
        }

        public void HideInlineAiBar()
        {
            inlineAiBar.IsVisible = false;
            Canvas.Focus();
            Canvas.InvalidateVisual();
        }

        public void SetFindQuery(string query, bool? matchCase = null, bool? wholeWord = null, bool? useRegex = null)
        {
            if (matchCase.HasValue) findMatchCase = matchCase.Value;
            if (wholeWord.HasValue) findWholeWord = wholeWord.Value;
            if (useRegex.HasValue) findUseRegex = useRegex.Value;
            UpdateFindToggleButtonStyles();
            findInputBox.Text = query ?? "";
            RecomputeFindMatches();
        }

        public void RecomputeFindMatches()
        {
            FindMatches.Clear();
            activeFindMatchIndex = -1;
            findError = null;
            string q = findInputBox.Text ?? "";
            if (CurrentBuffer == null || string.IsNullOrEmpty(q))
            {
                SetFindCount("No results", false, null);
                Canvas.InvalidateVisual();
                return;
            }
            // shared VS Code semantics (Core): case / whole word (also with regex) / regex with timeout / in selection
            var found = EditorFindEngine.FindAll(CurrentBuffer.Lines, q, findMatchCase, findWholeWord, findUseRegex, findInSelection ? findScope : null, out string err);
            if (err != null)
            {
                findError = err;
                SetFindCount(EditorFindEngine.ShortError(err), true, err);
                Canvas.InvalidateVisual();
                return;
            }
            foreach (var m in found) FindMatches.Add(new FindMatchSpan { Line = m.Line, StartCol = m.Col, Length = m.Length });

            if (FindMatches.Count > 0)
            {
                activeFindMatchIndex = 0;
                SetFindCount(EditorFindEngine.CountLabel(0, FindMatches.Count), false, null);
            }
            else
            {
                SetFindCount("No results", false, null);
            }
            Canvas.InvalidateVisual();
        }

        /// <summary>Find widget count / status text (tests).</summary>
        public string FindCountText => findCountLabel.Text;
        /// <summary>Full regex error of the last search (null when the pattern is valid).</summary>
        public string FindErrorText => findError;

        private void SetFindCount(string text, bool error, string tip)
        {
            findCountLabel.Text = text;
            if (currentTheme != null)
                findCountLabel.Foreground = WorkbenchStyles.B(error ? UiTokens.FindErrorForeground(currentTheme) : currentTheme.TextSecondary);
            ToolTip.SetTip(findCountLabel, tip);
            if (findInputBox != null && currentTheme != null)
                findInputBox.BorderBrush = error ? WorkbenchStyles.B(UiTokens.FindErrorForeground(currentTheme)) : null;
        }

        /// <summary>Current state of a find option (FindOptionToggles.MatchCase / WholeWord / Regex / PreserveCase / InSelection).</summary>
        public bool IsFindOptionOn(string option)
        {
            switch (option)
            {
                case FindOptionToggles.MatchCase: return findMatchCase;
                case FindOptionToggles.WholeWord: return findWholeWord;
                case FindOptionToggles.Regex: return findUseRegex;
                case FindOptionToggles.PreserveCase: return findPreserveCase;
                case FindOptionToggles.InSelection: return findInSelection;
            }
            return false;
        }

        /// <summary>The toggle button of an option (tests / screenshot suite).</summary>
        public Button FindOptionButton(string option)
        {
            switch (option)
            {
                case FindOptionToggles.MatchCase: return matchCaseBtn;
                case FindOptionToggles.WholeWord: return wholeWordBtn;
                case FindOptionToggles.Regex: return regexBtn;
                case FindOptionToggles.PreserveCase: return preserveCaseBtn;
                case FindOptionToggles.InSelection: return inSelectionBtn;
            }
            return null;
        }

        public bool IsFindBarVisible => findReplaceOverlay.IsVisible;
        /// <summary>Find / replace inputs of the widget (tests).</summary>
        public TextBox FindInputBox => findInputBox;
        public TextBox ReplaceInputBox => replaceInputBox;

        /// <summary>Flips a find option (button click, Alt+C/W/R/P/L, Cmd+Option+C/W/R/P/L on macOS) and re-searches.</summary>
        public void ToggleFindOption(string option)
        {
            switch (option)
            {
                case FindOptionToggles.MatchCase: findMatchCase = !findMatchCase; break;
                case FindOptionToggles.WholeWord: findWholeWord = !findWholeWord; break;
                case FindOptionToggles.Regex: findUseRegex = !findUseRegex; break;
                case FindOptionToggles.PreserveCase: findPreserveCase = !findPreserveCase; break;
                case FindOptionToggles.InSelection:
                    findInSelection = !findInSelection;
                    findScope = null;
                    if (findInSelection && CurrentBuffer != null)
                    {
                        // VS Code: the scope is the selection when the toggle is turned on (the caret line without one)
                        CurrentBuffer.GetNormalizedSelection(out int sl, out int sc, out int el, out int ec);
                        if (!CurrentBuffer.HasSelection()) { sl = el = CurrentBuffer.CursorLine; sc = 0; ec = CurrentBuffer.Lines[sl].Length; }
                        else if (ec == 0 && el > sl) { el--; ec = CurrentBuffer.Lines[el].Length; }
                        findScope = new EditorFindScope(sl, sc, el, ec);
                    }
                    break;
                default: return;
            }
            UpdateFindToggleButtonStyles();
            if (option != FindOptionToggles.PreserveCase) RecomputeFindMatches();
            StatusMessagePublished?.Invoke(FindOptionToggles.TitleOf(option) + ": " + (IsFindOptionOn(option) ? "On" : "Off"));
        }

        /// <summary>Handles the find option shortcuts while the find widget or the editor has focus.</summary>
        public bool TryHandleFindOptionKey(KeyEventArgs e)
        {
            if (e == null || IsShowingLargeFile) return false;
            string opt = FindOptionToggles.OptionForKey(e.Key, e.PhysicalKey, e.KeyModifiers);
            if (opt == null) return false;
            ToggleFindOption(opt);
            Canvas.SuppressNextTextInput();
            e.Handled = true;
            return true;
        }

        /// <summary>The find regex (same pattern/options as <see cref="RecomputeFindMatches"/>) used to expand $1 in replacements.</summary>
        private Regex BuildFindRegexForReplace()
        {
            string q = findInputBox.Text ?? "";
            if (q.Length == 0 || !findUseRegex) return null;
            return EditorFindEngine.Compile(q, findMatchCase, findWholeWord, true, out _);
        }

        public void FindNext(int step = 1)
        {
            CommitFindHistory();
            if (FindMatches.Count == 0) RecomputeFindMatches();
            if (FindMatches.Count == 0 || CurrentBuffer == null) return;

            activeFindMatchIndex = (activeFindMatchIndex + step + FindMatches.Count) % FindMatches.Count;
            var m = FindMatches[activeFindMatchIndex];
            CurrentBuffer.SelectionStartLine = m.Line;
            CurrentBuffer.SelectionStartCol = m.StartCol;
            CurrentBuffer.CursorLine = m.Line;
            CurrentBuffer.CursorCol = m.StartCol + m.Length;
            SetFindCount(EditorFindEngine.CountLabel(activeFindMatchIndex, FindMatches.Count), false, null);
            Canvas.EnsureCursorVisible();
            CaretMoved?.Invoke(CurrentBuffer);
            Canvas.InvalidateVisual();
        }

        public void ReplaceCurrentMatch(string replacement = null)
        {
            if (replacement == null) CommitFindHistory(includeReplace: true);
            if (CurrentBuffer == null) return;
            if (FindMatches.Count == 0) RecomputeFindMatches();
            if (FindMatches.Count == 0) return;

            string rep = replacement ?? (replaceInputBox.Text ?? "");
            if (activeFindMatchIndex < 0 || activeFindMatchIndex >= FindMatches.Count)
                activeFindMatchIndex = 0;

            var m = FindMatches[activeFindMatchIndex];
            CurrentBuffer.PushUndoSnapshot();
            string line = CurrentBuffer.Lines[m.Line];
            string expanded = WorkspaceSearchEngine.ExpandReplacementAt(line, BuildFindRegexForReplace(), m.StartCol, m.Length, rep, findUseRegex);
            if (findPreserveCase) expanded = EditorFindEngine.ApplyPreserveCase(line.Substring(m.StartCol, m.Length), expanded);
            CurrentBuffer.Lines[m.Line] = line.Substring(0, m.StartCol) + expanded + line.Substring(m.StartCol + m.Length);
            CurrentBuffer.CursorLine = m.Line;
            CurrentBuffer.CursorCol = m.StartCol + expanded.Length;
            CurrentBuffer.ClearSelection();
            CurrentBuffer.SyncContentFromLines();
            CurrentBuffer.IsDirty = true;
            CurrentBuffer.RefreshGutterDiff();
            // big documents: the debounced background analysis of BufferModified covers it (QA #35)
            if (CurrentBuffer.Lines.Count <= AvaloniaVirtualEditorCanvas.DeferAnalysisLineThreshold)
                LlmHarnessAndAgentEngine.AnalyzeBufferAstAndDiagnostics(CurrentBuffer);
            RecomputeFindMatches();
            BufferModified?.Invoke(CurrentBuffer);
            Canvas.InvalidateVisual();
        }

        public int ReplaceAllMatches(string replacement = null)
        {
            if (replacement == null) CommitFindHistory(includeReplace: true);
            if (CurrentBuffer == null) return 0;
            if (FindMatches.Count == 0) RecomputeFindMatches();
            if (FindMatches.Count == 0) return 0;

            string rep = replacement ?? (replaceInputBox.Text ?? "");
            int count = FindMatches.Count;
            CurrentBuffer.PushUndoSnapshot();
            Regex repRx = findUseRegex ? BuildFindRegexForReplace() : null;

            for (int i = FindMatches.Count - 1; i >= 0; i--)
            {
                var m = FindMatches[i];
                string line = CurrentBuffer.Lines[m.Line];
                if (m.StartCol >= 0 && m.StartCol + m.Length <= line.Length)
                {
                    // Expand $1 / $& against the original line (matches are applied right-to-left, so earlier columns are untouched).
                    string expanded = WorkspaceSearchEngine.ExpandReplacementAt(line, repRx, m.StartCol, m.Length, rep, findUseRegex);
                    if (findPreserveCase) expanded = EditorFindEngine.ApplyPreserveCase(line.Substring(m.StartCol, m.Length), expanded);
                    CurrentBuffer.Lines[m.Line] = line.Substring(0, m.StartCol) + expanded + line.Substring(m.StartCol + m.Length);
                }
            }
            CurrentBuffer.ClearSelection();
            CurrentBuffer.SyncContentFromLines();
            CurrentBuffer.IsDirty = true;
            CurrentBuffer.RefreshGutterDiff();
            // big documents: the debounced background analysis of BufferModified covers it (QA #35)
            if (CurrentBuffer.Lines.Count <= AvaloniaVirtualEditorCanvas.DeferAnalysisLineThreshold)
                LlmHarnessAndAgentEngine.AnalyzeBufferAstAndDiagnostics(CurrentBuffer);
            RecomputeFindMatches();
            BufferModified?.Invoke(CurrentBuffer);
            Canvas.InvalidateVisual();
            return count;
        }

        private Border BuildFindReplaceOverlay(
            out TextBox fBox, out TextBox rBox, out StackPanel rRow,
            out TextBlock cntLbl, out Button mcBtn, out Button wwBtn, out Button rxBtn)
        {
            // VS Code-style find widget: option toggles inside the input, vector glyph buttons, compact 24 px rows.
            fBox = new TextBox { Watermark = "Find", Width = 230, FontSize = UiTokens.FontUi, Padding = new Thickness(8, 2, 2, 2), MinHeight = 24, VerticalContentAlignment = VerticalAlignment.Center };
            rBox = new TextBox { Watermark = "Replace", Width = 230, FontSize = UiTokens.FontUi, Padding = new Thickness(8, 2, 6, 2), MinHeight = 24, VerticalContentAlignment = VerticalAlignment.Center };
            cntLbl = new TextBlock
            {
                Text = "No results",
                FontSize = UiTokens.FontSmall,
                MinWidth = 64,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(8, 0, 4, 0)
            };

            // VS Code input option toggles: 20 px, 16 px glyphs, hover fill, accent fill + border when on (QA: "there is no
            // case-sensitive / whole-word option": they were three faint 14 px glyphs with no visible state)
            mcBtn = FindOptionToggles.Create(FindOptionToggles.MatchCase, currentTheme);
            wwBtn = FindOptionToggles.Create(FindOptionToggles.WholeWord, currentTheme);
            rxBtn = FindOptionToggles.Create(FindOptionToggles.Regex, currentTheme);
            preserveCaseBtn = FindOptionToggles.Create(FindOptionToggles.PreserveCase, currentTheme);
            inSelectionBtn = FindOptionToggles.Create(FindOptionToggles.InSelection, currentTheme);
            inSelectionBtn.Width = inSelectionBtn.Height = 22;
            fBox.MinHeight = 26;
            rBox.MinHeight = 26;
            fBox.InnerRightContent = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2, Margin = new Thickness(0, 0, 3, 0), VerticalAlignment = VerticalAlignment.Center, Children = { mcBtn, wwBtn, rxBtn } };
            rBox.InnerRightContent = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 3, 0), VerticalAlignment = VerticalAlignment.Center, Children = { preserveCaseBtn } };
            var prevBtn = WorkbenchStyles.IconButton("arrow-up", "Previous Match (Shift+Enter)", null, currentTheme);
            var nextBtn = WorkbenchStyles.IconButton("arrow-down", "Next Match (Enter)", null, currentTheme);
            var toggleRepBtn = WorkbenchStyles.IconButton("chevron-right", "Toggle Replace", null, currentTheme, 18, 12);
            toggleRepBtn.Height = 24;
            toggleRepBtn.Margin = new Thickness(0, 0, 4, 0);
            var closeBtn = WorkbenchStyles.IconButton("close", "Close (Escape)", null, currentTheme);
            findToggleReplaceButton = toggleRepBtn;
            findWidgetIconButtons = new[] { prevBtn, nextBtn, toggleRepBtn, closeBtn };

            var findRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2 };
            findRow.Children.Add(toggleRepBtn);
            findRow.Children.Add(fBox);
            findRow.Children.Add(cntLbl);
            findRow.Children.Add(prevBtn);
            findRow.Children.Add(nextBtn);
            findRow.Children.Add(inSelectionBtn);
            findRow.Children.Add(closeBtn);

            var repOneBtn = WorkbenchStyles.IconButton("replace", "Replace (Enter)", null, currentTheme);
            var repAllBtn = WorkbenchStyles.IconButton("replace-all", MacKeymap.IsMacOS ? "Replace All (⌥⌘Enter)" : "Replace All (Ctrl+Alt+Enter)", null, currentTheme);
            findWidgetReplaceButtons = new[] { repOneBtn, repAllBtn };
            rRow = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 2,
                Margin = new Thickness(22, 4, 0, 0),
                IsVisible = false
            };
            rRow.Children.Add(rBox);
            rRow.Children.Add(new Border { Width = 6 });
            rRow.Children.Add(repOneBtn);
            rRow.Children.Add(repAllBtn);

            var stack = new StackPanel();
            stack.Children.Add(findRow);
            stack.Children.Add(rRow);

            var myFBox = fBox;
            var myRBox = rBox;
            var myRRow = rRow;

            // TextChanged is raised after the setter returns: compare with the text history put there instead of a flag.
            myFBox.TextChanged += (s, e) => { if (!string.Equals(myFBox.Text, lastFindHistoryText, StringComparison.Ordinal)) FindHistoryStore.Find.ResetNavigation(); RecomputeFindMatches(); };
            myRBox.TextChanged += (s, e) => { if (!string.Equals(myRBox.Text, lastReplaceHistoryText, StringComparison.Ordinal)) FindHistoryStore.Replace.ResetNavigation(); };
            // VS Code: Up / Down in the find or replace box walk through earlier searches (tunnel: the TextBox would
            // otherwise consume the arrows to move its caret).
            myFBox.AddHandler(KeyDownEvent, (s, e) => { if (TryNavigateFindHistory(myFBox, FindHistoryStore.Find, e)) e.Handled = true; }, global::Avalonia.Interactivity.RoutingStrategies.Tunnel);
            myRBox.AddHandler(KeyDownEvent, (s, e) => { if (TryNavigateFindHistory(myRBox, FindHistoryStore.Replace, e)) e.Handled = true; }, global::Avalonia.Interactivity.RoutingStrategies.Tunnel);
            myFBox.KeyDown += (s, e) =>
            {
                bool alt = e.KeyModifiers.HasFlag(KeyModifiers.Alt);
                if (TryHandleFindOptionKey(e)) { }
                else if (e.Key == Key.Enter)
                {
                    if (alt)
                    {
                        Canvas.SelectAllFindOccurrences();
                    }
                    else
                    {
                        FindNext(e.KeyModifiers.HasFlag(KeyModifiers.Shift) ? -1 : 1);
                    }
                    e.Handled = true;
                }
                else if (e.Key == Key.Escape)
                {
                    HideFindBar();
                    e.Handled = true;
                }
            };
            myRBox.KeyDown += (s, e) =>
            {
                if (TryHandleFindOptionKey(e)) { }
                else if (e.Key == Key.Enter && e.KeyModifiers.HasFlag(MacKeymap.PrimaryModifier) && e.KeyModifiers.HasFlag(KeyModifiers.Alt)) { ReplaceAllMatches(); e.Handled = true; }
                else if (e.Key == Key.Enter) { ReplaceCurrentMatch(); e.Handled = true; }
                else if (e.Key == Key.Escape) { HideFindBar(); e.Handled = true; }
            };

            mcBtn.Click += (s, e) => ToggleFindOption(FindOptionToggles.MatchCase);
            wwBtn.Click += (s, e) => ToggleFindOption(FindOptionToggles.WholeWord);
            rxBtn.Click += (s, e) => ToggleFindOption(FindOptionToggles.Regex);
            preserveCaseBtn.Click += (s, e) => ToggleFindOption(FindOptionToggles.PreserveCase);
            inSelectionBtn.Click += (s, e) => ToggleFindOption(FindOptionToggles.InSelection);
            prevBtn.Click += (s, e) => FindNext(-1);
            nextBtn.Click += (s, e) => FindNext(1);
            toggleRepBtn.Click += (s, e) => { myRRow.IsVisible = !myRRow.IsVisible; UpdateFindToggleButtonStyles(); };
            repOneBtn.Click += (s, e) => ReplaceCurrentMatch();
            repAllBtn.Click += (s, e) => ReplaceAllMatches();
            closeBtn.Click += (s, e) => HideFindBar();

            return new Border
            {
                Child = stack,
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(0, 0, 86, 0),
                Padding = new Thickness(4, 5, 6, 5),
                CornerRadius = new CornerRadius(0, 0, UiTokens.PopupRadius, UiTokens.PopupRadius),
                BorderThickness = new Thickness(1, 0, 1, 1),
                IsVisible = false,
                ZIndex = 50
            };
        }

        private Button[] diffBannerButtons = new Button[0];
        private Button findToggleReplaceButton;
        private Button[] findWidgetIconButtons = new Button[0];
        private Button[] findWidgetReplaceButtons = new Button[0];

        /// <summary>Option toggles: accent-tinted fill + 1 px accent border when on (VS Code inputOption.active*).</summary>
        private void UpdateFindToggleButtonStyles()
        {
            if (currentTheme == null) return;
            var t = currentTheme;
            FindOptionToggles.Style(matchCaseBtn, findMatchCase, t);
            FindOptionToggles.Style(wholeWordBtn, findWholeWord, t);
            FindOptionToggles.Style(regexBtn, findUseRegex, t);
            FindOptionToggles.Style(preserveCaseBtn, findPreserveCase, t);
            FindOptionToggles.Style(inSelectionBtn, findInSelection, t);
            foreach (var b in findWidgetIconButtons) WorkbenchStyles.ThemeIconButton(b, t);
            foreach (var b in findWidgetReplaceButtons) WorkbenchStyles.ThemeIconButton(b, t);
            if (findToggleReplaceButton?.Content is VectorIconControl chev && replaceRowPanel != null)
            {
                chev.IconName = replaceRowPanel.IsVisible ? "chevron-down" : "chevron-right";
                chev.InvalidateVisual();
            }
        }

        private Border BuildInlineAiOverlay(out TextBox input)
        {
            input = new TextBox
            {
                Watermark = "Ask AI to edit or generate code at cursor (e.g., add null check, convert to async, add XML docs)...",
                Width = 360,
                FontSize = 12,
                Padding = new Thickness(8, 4)
            };
            var genBtn = new Button
            {
                Content = "✦ Generate Diff ↵",
                FontSize = 11.5,
                FontWeight = FontWeight.SemiBold,
                Padding = new Thickness(10, 4),
                Background = new SolidColorBrush(Color.FromRgb(59, 130, 246)),
                Foreground = Brushes.White
            };
            var escBtn = new Button
            {
                Content = "Esc",
                FontSize = 11,
                Padding = new Thickness(8, 4)
            };

            var topRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
            topRow.Children.Add(new TextBlock
            {
                Text = "✦ Inline Edit",
                FontWeight = FontWeight.Bold,
                FontSize = 11.5,
                Foreground = new SolidColorBrush(Color.FromRgb(96, 165, 250)),
                VerticalAlignment = VerticalAlignment.Center
            });
            topRow.Children.Add(input);
            topRow.Children.Add(genBtn);
            topRow.Children.Add(escBtn);

            var chipsRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Margin = new Thickness(0, 4, 0, 0) };
            var myInput = input;
            foreach (string chip in new[] { "Add XML doc summary", "Add error handling", "Optimize performance", "Extract helper" })
            {
                string cText = chip;
                var chipBtn = new Button
                {
                    Content = new LiteralTextBlock(cText) { FontSize = 10.5 },
                    FontSize = 10.5,
                    Padding = new Thickness(7, 2)
                };
                chipBtn.Click += (s, e) =>
                {
                    myInput.Text = cText;
                    SubmitInlineAiPrompt(cText);
                };
                chipsRow.Children.Add(chipBtn);
            }

            genBtn.Click += (s, e) => SubmitInlineAiPrompt(myInput.Text);
            escBtn.Click += (s, e) => HideInlineAiBar();
            myInput.KeyDown += (s, e) =>
            {
                if (e.Key == Key.Enter) { SubmitInlineAiPrompt(myInput.Text); e.Handled = true; }
                else if (e.Key == Key.Escape) { HideInlineAiBar(); e.Handled = true; }
            };

            var outer = new StackPanel();
            outer.Children.Add(topRow);
            outer.Children.Add(chipsRow);

            return new Border
            {
                Child = outer,
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(64, 18, 0, 0),
                Padding = new Thickness(10, 7),
                CornerRadius = new CornerRadius(6),
                BorderThickness = new Thickness(1),
                IsVisible = false,
                ZIndex = 55
            };
        }

        private void SubmitInlineAiPrompt(string promptText)
        {
            string p = (promptText ?? "").Trim();
            if (string.IsNullOrEmpty(p) || CurrentBuffer == null) return;
            HideInlineAiBar();
            if (InlineAiEditRequested != null)
            {
                InlineAiEditRequested(p, CurrentBuffer);
            }
            else
            {
                // Apply deterministic inline diff preview
                int line = Math.Max(0, Math.Min(CurrentBuffer.Lines.Count - 1, CurrentBuffer.CursorLine));
                string curLineText = CurrentBuffer.Lines[line];
                int indentLen = curLineText.Length - curLineText.TrimStart().Length;
                string indent = indentLen > 0 ? curLineText.Substring(0, indentLen) : "";
                var clone = new List<string>(CurrentBuffer.Lines);
                clone.Insert(line, $"{indent}// ✦ AI ({p})");
                StageProposedDiffContent(string.Join("\n", clone));
            }
        }

        /// <summary>VS Code's empty-editor watermark: a few key commands with their platform shortcuts.</summary>
        private void RebuildEmptyWatermark(StudioTheme theme)
        {
            emptyShortcuts.Children.Clear();
            var label = AvaloniaColorHelper.ToBrush(theme.TextSecondary);
            var rows = new[]
            {
                ("Show All Commands", "Ctrl+Shift+P"),
                ("Go to File", "Ctrl+P"),
                ("Find in Files", "Ctrl+Shift+F"),
                ("Toggle Terminal", "Ctrl+`"),
                ("Open Folder", "Ctrl+O"),
            };
            foreach (var (name, sc) in rows)
            {
                var g = new Grid { ColumnDefinitions = new ColumnDefinitions("200,Auto"), Opacity = 0.75 };
                var n = new TextBlock { Text = name, FontSize = UiTokens.FontUi, Foreground = label, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 0, 14, 0) };
                var k = new TextBlock { Text = AvaloniaCommandPaletteOverlay.FormatShortcutForPlatform(sc), FontSize = UiTokens.FontUi, Foreground = label };
                Grid.SetColumn(k, 1);
                g.Children.Add(n);
                g.Children.Add(k);
                emptyShortcuts.Children.Add(g);
            }
        }

        private Border BuildWelcomePage()
        {
            var outerGrid = new Grid
            {
                MaxWidth = 920,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(22, 20)
            };

            var mainStack = new StackPanel { Spacing = 22 };

            // Header with crisp Savatarr Bolt Logo + Title + Subtitle
            var heroRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 16, VerticalAlignment = VerticalAlignment.Center };
            bool addedBitmapLogo = false;
            try
            {
                string[] logoCandidates = new[]
                {
                    Path.Combine(AppContext.BaseDirectory, "assets", "savatarr_bolt.png"),
                    Path.Combine(Environment.CurrentDirectory, "assets", "savatarr_bolt.png"),
                    Path.Combine(Environment.CurrentDirectory, "codeLess", "assets", "savatarr_bolt.png"),
                    Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "assets", "savatarr_bolt.png"))
                };
                string logoPath = logoCandidates.FirstOrDefault(File.Exists);
                if (!string.IsNullOrEmpty(logoPath))
                {
                    var logoBadge = new Border
                    {
                        Name = "WelcomeLogoBadge",
                        Width = 52,
                        Height = 52,
                        CornerRadius = new CornerRadius(12),
                        Padding = new Thickness(4),
                        VerticalAlignment = VerticalAlignment.Center,
                        Child = new Image
                        {
                            Source = new Bitmap(logoPath),
                            Width = 44,
                            Height = 44,
                            Stretch = Stretch.Uniform,
                            VerticalAlignment = VerticalAlignment.Center,
                            HorizontalAlignment = HorizontalAlignment.Center
                        }
                    };
                    heroRow.Children.Add(logoBadge);
                    addedBitmapLogo = true;
                }
            }
            catch { }

            if (!addedBitmapLogo)
            {
                heroRow.Children.Add(new Border
                {
                    Name = "WelcomeLogoBadge",
                    Width = 52,
                    Height = 52,
                    CornerRadius = new CornerRadius(12),
                    Padding = new Thickness(4),
                    VerticalAlignment = VerticalAlignment.Center,
                    Child = new VectorIconControl
                    {
                        IconName = "brand-bolt",
                        Width = 44,
                        Height = 44,
                        VerticalAlignment = VerticalAlignment.Center,
                        HorizontalAlignment = HorizontalAlignment.Center
                    }
                });
            }

            var titleStack = new StackPanel { Spacing = 3, VerticalAlignment = VerticalAlignment.Center };
            titleStack.Children.Add(new TextBlock
            {
                Name = "WelcomeTitleText",
                Text = "codeLess",
                FontSize = 28,
                FontWeight = FontWeight.SemiBold
            });
            titleStack.Children.Add(new TextBlock
            {
                Name = "WelcomeSubtitleText",
                Text = "AI-First Polyglot IDE & Multi-Harness Engineering Workbench",
                FontSize = UiTokens.FontUi + 1
            });
            heroRow.Children.Add(titleStack);
            mainStack.Children.Add(heroRow);

            // Two-column Start / Recent + Walkthroughs (wider left column so Recent paths never clip)
            var columns = new Grid { ColumnDefinitions = new ColumnDefinitions("1.36*,18,1.0*") };

            var leftCol = new StackPanel { Spacing = 6 };
            leftCol.Children.Add(new TextBlock
            {
                Name = "StartHeader",
                Text = "Start",
                FontSize = 15,
                FontWeight = FontWeight.Normal
            });

            var startLinks = new (string Label, string Shortcut, string ActionId)[]
            {
                ("New File...", "Ctrl+N", "new-file"),
                ("Open File...", "Ctrl+O", "open-file"),
                ("Open Folder...", "Ctrl+K Ctrl+O", "open-folder"),
                ("Open AI Agent Chat...", "Ctrl+Alt+I", "open-chat"),
                ("Command Palette...", "Ctrl+Shift+P", "command-palette")
            };
            foreach (var sl in startLinks)
            {
                string act = sl.ActionId;
                string sc = AvaloniaCommandPaletteOverlay.FormatShortcutForPlatform(sl.Shortcut);
                // VS Code welcome: link-colored action, quiet shortcut hint beside it.
                var linkRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
                linkRow.Children.Add(new LiteralTextBlock(sl.Label) { Name = "WelcomeActionLinkText", FontSize = UiTokens.FontUi });
                linkRow.Children.Add(new LiteralTextBlock(sc) { Name = "WelcomeShortcutText", FontSize = UiTokens.FontSmall, VerticalAlignment = VerticalAlignment.Center });
                var btn = new Button
                {
                    Content = linkRow,
                    FontSize = UiTokens.FontUi,
                    Background = Brushes.Transparent,
                    Padding = new Thickness(4, 2),
                    HorizontalAlignment = HorizontalAlignment.Left
                };
                btn.Classes.Add("flat");
                ToolTip.SetTip(btn, sl.Label.TrimEnd('.') + " (" + sc + ")");
                btn.Click += (s, e) => WelcomeActionRequested?.Invoke(act);
                leftCol.Children.Add(btn);
            }

            leftCol.Children.Add(new TextBlock
            {
                Name = "RecentHeader",
                Text = "Recent",
                FontSize = 15,
                FontWeight = FontWeight.Normal,
                Margin = new Thickness(0, 14, 0, 0)
            });
            leftCol.Children.Add(recentFoldersPanel);

            var rightCol = new StackPanel { Spacing = 10 };
            rightCol.Children.Add(new TextBlock
            {
                Name = "WalkthroughsHeader",
                Text = "Walkthroughs",
                FontSize = 15,
                FontWeight = FontWeight.Normal
            });

            var walkthroughs = new (string Title, string Desc, string ActionId)[]
            {
                ("Configure BYOK AI Providers & Keys", "Connect OpenAI, Anthropic, Gemini, DeepSeek, Groq, or local Ollama models.", "byok-dialog"),
                ("Autonomous Verification & Tool Harness", "Run tool-calling agents with build/test loops and inline diff previews.", "open-chat"),
                ("Customize Color Themes & Layout", "Switch between 11 built-in dark and light studio themes with live preview.", "theme-picker")
            };
            foreach (var wt in walkthroughs)
            {
                string act = wt.ActionId;
                string icon = act == "byok-dialog" ? "sparkle" : (act == "open-chat" ? "check" : "gear");
                var cardStack = new StackPanel { Spacing = 3 };
                var cardTitle = new Grid { ColumnDefinitions = new ColumnDefinitions("23,*") };
                cardTitle.Children.Add(new VectorIconControl { Name = "WalkthroughIcon", IconName = icon, Width = 15, Height = 15, StrokeWidth = 1.5f, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Center });
                var cardTitleText = new LiteralTextBlock(wt.Title) { FontWeight = FontWeight.SemiBold, FontSize = UiTokens.FontUi, TextWrapping = TextWrapping.Wrap };
                Grid.SetColumn(cardTitleText, 1);
                cardTitle.Children.Add(cardTitleText);
                cardStack.Children.Add(cardTitle);
                cardStack.Children.Add(new LiteralTextBlock(wt.Desc) { Name = "WalkthroughDescText", FontSize = UiTokens.FontSmall, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(23, 0, 0, 0) });
                var cardBtn = new Button
                {
                    Content = cardStack,
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                    HorizontalContentAlignment = HorizontalAlignment.Left,
                    Padding = new Thickness(12, 10),
                    CornerRadius = new CornerRadius(UiTokens.PopupRadius)
                };
                cardBtn.Click += (s, e) => WelcomeActionRequested?.Invoke(act);
                rightCol.Children.Add(cardBtn);
            }

            Grid.SetColumn(leftCol, 0);
            columns.Children.Add(leftCol);
            Grid.SetColumn(rightCol, 2);
            columns.Children.Add(rightCol);
            mainStack.Children.Add(columns);

            outerGrid.Children.Add(mainStack);
            return new Border
            {
                Child = outerGrid,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                VerticalAlignment = VerticalAlignment.Stretch
            };
        }

        public static string FormatRecentDisplayPath(string fullPath, int maxChars = 64)
        {
            if (string.IsNullOrEmpty(fullPath)) return "";
            if (fullPath.Length <= maxChars) return fullPath;

            char sep = fullPath.Contains('\\') ? '\\' : '/';
            string[] parts = fullPath.Split(new[] { '\\', '/' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length >= 4)
            {
                string prefix = fullPath.StartsWith("/") ? "/" + parts[0] : parts[0] + sep + parts[1];
                string tail = parts[parts.Length - 2] + sep + parts[parts.Length - 1];
                string candidate = prefix + sep + "…" + sep + tail;
                if (candidate.Length <= maxChars)
                    return candidate;
            }

            int headLen = Math.Max(12, (maxChars - 1) / 2 - 4);
            int tailLen = Math.Max(16, maxChars - 1 - headLen);
            return fullPath.Substring(0, headLen) + "…" + fullPath.Substring(fullPath.Length - tailLen);
        }

        private void RefreshRecentFoldersOnWelcome()
        {
            recentFoldersPanel.Children.Clear();
            Color accent = AvaloniaColorHelper.ToAvColor(currentTheme.AccentPrimary);
            Color textSecondary = AvaloniaColorHelper.ToAvColor(currentTheme.TextSecondary);

            if (recentWorkspaces == null || recentWorkspaces.Count == 0)
            {
                recentFoldersPanel.Children.Add(new TextBlock
                {
                    Text = "No recent folders opened yet.",
                    FontSize = 11.5,
                    Foreground = new SolidColorBrush(textSecondary)
                });
                return;
            }

            foreach (string path in recentWorkspaces.Take(5))
            {
                string captured = path;
                string leaf = Path.GetFileName(captured.TrimEnd('\\', '/'));
                if (string.IsNullOrEmpty(leaf)) leaf = captured;
                string displayPath = FormatRecentDisplayPath(captured, 68);

                var rowWrap = new WrapPanel
                {
                    Orientation = Orientation.Horizontal,
                    HorizontalAlignment = HorizontalAlignment.Stretch
                };
                var leafTb = new LiteralTextBlock(leaf)
                {
                    Name = "RecentLeafText",
                    FontSize = UiTokens.FontUi,
                    FontWeight = FontWeight.Normal,
                    Foreground = new SolidColorBrush(accent),
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(0, 0, 8, 0)
                };
                var pathTb = new LiteralTextBlock(displayPath)
                {
                    Name = "RecentPathText",
                    FontSize = UiTokens.FontSmall,
                    Foreground = AvaloniaColorHelper.ToBrush(currentTheme.FgMuted),
                    TextWrapping = TextWrapping.Wrap,
                    VerticalAlignment = VerticalAlignment.Center
                };
                rowWrap.Children.Add(leafTb);
                rowWrap.Children.Add(pathTb);

                var rowBtn = new Button
                {
                    Content = rowWrap,
                    Tag = captured,
                    Background = Brushes.Transparent,
                    Padding = new Thickness(4, 2),
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                    HorizontalContentAlignment = HorizontalAlignment.Left
                };
                ToolTip.SetTip(rowBtn, captured);
                rowBtn.Click += (s, e) => OpenRecentWorkspaceRequested?.Invoke(captured);
                recentFoldersPanel.Children.Add(rowBtn);
            }
        }

        private void RebuildWelcomeThemeColors()
        {
            Color textPrimary = AvaloniaColorHelper.ToAvColor(currentTheme.TextPrimary);
            Color textSecondary = AvaloniaColorHelper.ToAvColor(currentTheme.TextSecondary);
            Color accent = AvaloniaColorHelper.ToAvColor(currentTheme.AccentPrimary);
            bool isLight = AvaloniaColorHelper.IsLightColor(currentTheme.EditorBg);

            void StyleTree(Control ctrl)
            {
                if (ctrl is Border b && b.Name == "WelcomeLogoBadge")
                {
                    b.Background = new SolidColorBrush(isLight ? Color.FromRgb(234, 240, 248) : Color.FromRgb(24, 28, 38));
                    b.BorderBrush = AvaloniaColorHelper.ToBrush(currentTheme.BorderSubtle);
                    b.BorderThickness = new Thickness(1);
                }
                else if (ctrl is TextBlock tb)
                {
                    if (tb.Name == "WelcomeSubtitleText" || tb.Name == "WalkthroughDescText")
                        tb.Foreground = new SolidColorBrush(textSecondary);
                    else if (tb.Name == "RecentPathText" || tb.Name == "WelcomeShortcutText")
                        tb.Foreground = AvaloniaColorHelper.ToBrush(currentTheme.FgMuted);
                    else if (tb.Name == "WelcomeActionLinkText" || tb.Name == "RecentLeafText")
                        tb.Foreground = new SolidColorBrush(accent);
                    else
                        tb.Foreground = new SolidColorBrush(textPrimary);
                }
                else if (ctrl is VectorIconControl vic && vic.Name == "WalkthroughIcon")
                {
                    vic.FollowForeground = false;
                    vic.IconColor = accent;
                    vic.InvalidateVisual();
                }
                else if (ctrl is Button btn)
                {
                    if (btn.Content is StackPanel sp && !btn.Classes.Contains("flat"))
                    {
                        btn.Background = WorkbenchStyles.B(UiTokens.Blend(currentTheme.BgEditor, currentTheme.FgPrimary, isLight ? 0.03 : 0.035));
                        btn.BorderBrush = AvaloniaColorHelper.ToBrush(currentTheme.BorderSubtle);
                        btn.BorderThickness = new Thickness(1);
                    }
                    else if (btn.Content is StackPanel)
                    {
                        btn.Background = Brushes.Transparent;
                    }
                    else
                    {
                        btn.Foreground = new SolidColorBrush(accent);
                    }
                }
                if (ctrl is Panel p)
                {
                    foreach (var ch in p.Children.OfType<Control>())
                        StyleTree(ch);
                }
                else if (ctrl is ContentControl cc && cc.Content is Control inner)
                {
                    StyleTree(inner);
                }
                else if (ctrl is Decorator dec && dec.Child is Control dch)
                {
                    StyleTree(dch);
                }
            }
            StyleTree(welcomeHost);
            RefreshRecentFoldersOnWelcome();
        }

        internal void NotifyBufferModified()
        {
            if (CurrentBuffer != null)
            {
                if (CurrentBuffer.PreDiffContent != null) { InlineDiffReview.Refresh(CurrentBuffer); CurrentBuffer.SyncProposedDiffProjection(); }
                RefreshBreadcrumbs(onlyIfChanged: true);
                RefreshDiffBanner();
                BufferModified?.Invoke(CurrentBuffer);
            }
        }

        internal void NotifyCaretMoved()
        {
            if (CurrentBuffer != null)
            {
                RefreshBreadcrumbs(onlyIfChanged: true);
                CaretMoved?.Invoke(CurrentBuffer);
            }
        }

        /// <summary>Raised for Cmd/Ctrl+Click on an import, path, URL or identifier: (buffer, target, clicked 0-based line).</summary>
        public event Action<EditorTabBuffer, EditorLinkTarget, int> LinkActivated;
        /// <summary>Mouse back (-1) / forward (+1) buttons.</summary>
        public event Action<int> NavigateHistoryRequested;

        internal string WorkspaceRootForLinks => workspaceRoot;

        /// <summary>Resolves the Cmd/Ctrl+Click target at a position. Never throws.</summary>
        public EditorLinkTarget ResolveLinkAt(EditorTabBuffer buf, int line, int col)
        {
            if (buf == null) return null;
            try
            {
                string root = workspaceRoot;
                if (string.IsNullOrEmpty(root) && !string.IsNullOrEmpty(buf.FilePath)) root = Path.GetDirectoryName(buf.FilePath);
                return EditorLinkResolver.Resolve(buf.Lines, line, col, buf.FilePath, root);
            }
            catch { return null; }
        }

        internal void NotifyLinkActivated(EditorLinkTarget target, int line)
        {
            if (CurrentBuffer != null && target != null)
                LinkActivated?.Invoke(CurrentBuffer, target, line);
        }

        internal void NotifyNavigateHistory(int direction) => NavigateHistoryRequested?.Invoke(direction);

        internal void NotifyContextMenu(Point pt)
        {
            if (CurrentBuffer != null)
                EditorContextMenuRequested?.Invoke(CurrentBuffer, pt);
        }

        internal void PublishStatus(string msg)
        {
            StatusMessagePublished?.Invoke(msg);
        }
    }
}

namespace codeLess.AvaloniaApp
{
    public struct SyntaxTokenSpan
    {
        public string Text;
        public Color Color;
        public bool IsBold;
        public bool IsItalic;
    }

    /// <summary>
    /// Platform input-method bridge (macOS press-and-hold accents, dead keys, Japanese / Chinese / Korean IMEs,
    /// Linux IBus/Fcitx, Windows TSF): reports the caret rectangle for the candidate window and receives the
    /// in-progress composition ("preedit"), which the canvas draws underlined at the caret. Committed text still
    /// arrives through OnTextInput.
    /// </summary>
    internal sealed class EditorImeClient : global::Avalonia.Input.TextInput.TextInputMethodClient
    {
        private readonly AvaloniaVirtualEditorCanvas canvas;
        public EditorImeClient(AvaloniaVirtualEditorCanvas canvas) { this.canvas = canvas; }
        public override Visual TextViewVisual => canvas;
        public override bool SupportsPreedit => true;
        public override bool SupportsSurroundingText => true;
        public override string SurroundingText => canvas.ImeSurroundingText;
        public override Rect CursorRectangle => canvas.ImeCaretRect;
        public override global::Avalonia.Input.TextInput.TextSelection Selection
        {
            get => new global::Avalonia.Input.TextInput.TextSelection(canvas.ImeCaretColumn, canvas.ImeCaretColumn);
            set { }
        }
        public override void SetPreeditText(string preeditText) => canvas.SetImePreedit(preeditText);
        public override void SetPreeditText(string preeditText, int? cursorPos) => canvas.SetImePreedit(preeditText);
        public void NotifyCaretChanged()
        {
            RaiseCursorRectangleChanged();
            RaiseSelectionChanged();
            RaiseSurroundingTextChanged();
        }
    }

    public partial class AvaloniaVirtualEditorCanvas : Control
    {
        private readonly AvaloniaEditorControl owner;
        public new StudioTheme Theme { get; private set; } = StudioTheme.GetById(StudioTheme.DefaultThemeId);

        public double FontSizePx { get; set; } = 13.0;
        public double LineHeight => Math.Round(FontSizePx * 1.54);
        private double measuredCharWidth = 0;
        private double measuredCharWidthFontSize = -1;
        public double CharWidth => measuredCharWidth > 3.0 ? measuredCharWidth : Math.Round(FontSizePx * 0.60, 2);

        // ---- Visual column model -------------------------------------------------------------------------
        // Text, caret, selection, find highlights and click hit-testing all use the same grid: a tab advances to
        // the next tab stop, CJK / emoji occupy 2 cells, combining marks 0. (Previously text was drawn with
        // measured glyph widths while carets used column * CharWidth, so they drifted apart on such lines.)
        public static int DefaultDisplayTabSize = 4;
        public int DisplayTabSize { get; set; } = DefaultDisplayTabSize;

        private readonly EditorImeClient imeClient;
        private string imePreedit;
        private Rect imeCaretRect = new Rect(0, 0, 2, 16);
        internal Rect ImeCaretRect => imeCaretRect;
        internal int ImeCaretColumn => Buffer?.CursorCol ?? 0;
        internal string ImeSurroundingText
        {
            get
            {
                var b = Buffer;
                return b != null && b.CursorLine >= 0 && b.CursorLine < b.Lines.Count ? b.Lines[b.CursorLine] : "";
            }
        }
        public bool IsImeComposing => !string.IsNullOrEmpty(imePreedit);

        internal void SetImePreedit(string text)
        {
            imePreedit = string.IsNullOrEmpty(text) ? null : text;
            InvalidateVisual();
        }

        private static int CellsForChar(string s, int i) => EditorTextLayout.CellsForChar(s, i);

        /// <summary>Visual column (in cells) of character index <paramref name="col"/> with the global tab size.</summary>
        public int VisualColumn(string line, int col) => EditorTextLayout.VisualColumn(line, col, DisplayTabSize);

        /// <summary>Character index nearest to visual column <paramref name="vcol"/> (inverse of VisualColumn), global tab size.</summary>
        public int CharIndexForVisualColumn(string line, double vcol) => EditorTextLayout.CharIndexForVisualColumn(line, vcol, DisplayTabSize);

        /// <summary>X offset (relative to the start of the code area) of a character index on a document line (unwrapped).</summary>
        private double ColX(EditorTabBuffer buf, int docLine, int col)
        {
            if (buf == null || docLine < 0 || docLine >= buf.Lines.Count) return col * CharWidth;
            return EditorTextLayout.VisualColumn(buf.Lines[docLine], col, TabFor(buf)) * CharWidth;
        }
        public const double GutterWidth = 66.0;
        public const double MinimapWidth = 76.0;

        private static readonly string CodeMonoFont = RuntimeInformation.IsOSPlatform(OSPlatform.OSX)
            ? "Menlo, Monaco, SF Mono, monospace"
            : "Cascadia Code, Consolas, DejaVu Sans Mono, monospace";

        private static readonly Typeface CachedFontRegular = new Typeface(CodeMonoFont, FontStyle.Normal, FontWeight.Normal);
        private static readonly Typeface CachedFontBold = new Typeface(CodeMonoFont, FontStyle.Normal, FontWeight.Bold);
        private static readonly Typeface CachedFontItalic = new Typeface(CodeMonoFont, FontStyle.Italic, FontWeight.Normal);
        private static readonly Typeface CachedUiFontBold = new Typeface("Inter, Segoe UI, sans-serif", FontStyle.Normal, FontWeight.SemiBold);
        private static readonly Typeface CachedUiFontRegular = new Typeface("Inter, Segoe UI, sans-serif", FontStyle.Normal, FontWeight.Normal);

        public bool ShowMinimap { get; set; } = true;
        public bool ColumnSelectionMode { get; set; } = false;
        public List<ExtraCursorCaret> ExtraCursors { get; } = new List<ExtraCursorCaret>();

        private readonly DispatcherTimer blinkTimer;
        private bool isMouseSelecting = false;
        private bool isMinimapDragging = false;
        private int hoveredLine = -1;
        private Point lastHoverPt;

        private readonly List<(Rect Rect, int Line, bool IsAccept)> hunkActionHitBoxes = new List<(Rect, int, bool)>();
        private readonly List<(Rect Rect, int Line, EditorLineAnnotation Ann)> annotationHitBoxes = new List<(Rect, int, EditorLineAnnotation)>();
        private EditorLineAnnotation hoveredAnnotation;
        private int lastNotifiedScrollTop = -1;

        private static readonly HashSet<string> LanguageKeywords = new HashSet<string>(StringComparer.Ordinal)
        {
            "abstract", "as", "async", "await", "base", "bool", "break", "byte", "case", "catch",
            "char", "checked", "class", "const", "continue", "decimal", "default", "delegate", "do",
            "double", "else", "enum", "event", "explicit", "extern", "false", "finally", "fixed",
            "float", "for", "foreach", "goto", "if", "implicit", "in", "int", "interface", "internal",
            "is", "lock", "long", "namespace", "new", "null", "object", "operator", "out", "override",
            "params", "private", "protected", "public", "readonly", "ref", "return", "sbyte", "sealed",
            "short", "sizeof", "stackalloc", "static", "string", "struct", "switch", "this", "throw",
            "true", "try", "typeof", "uint", "ulong", "unchecked", "unsafe", "ushort", "using", "var",
            "virtual", "void", "volatile", "while", "get", "set", "init", "record", "partial", "where",
            "yield", "def", "elif", "except", "from", "global", "import", "lambda", "nonlocal", "not",
            "or", "and", "pass", "raise", "with", "None", "True", "False", "self", "function", "let",
            "export", "extends", "implements", "typeof", "instanceof", "fn", "pub", "mut", "impl",
            "trait", "match", "mod", "use", "crate"
        };

        public AvaloniaVirtualEditorCanvas(AvaloniaEditorControl owner)
        {
            this.owner = owner;
            Focusable = true;
            ClipToBounds = true;
            imeClient = new EditorImeClient(this);
            TextInputMethodClientRequested += (s, e) => e.Client = imeClient;
            global::Avalonia.Input.InputMethod.SetIsInputMethodEnabled(this, true);

            blinkTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(CaretBlink.IntervalMs) };
            blinkTimer.Tick += (s, e) => OnCaretBlinkTick();
            // Blink only while focused in an active, visible window (and until VS Code's animated blink runs out);
            // otherwise draw a steady caret and stay asleep.
            blinkGate = new UiTimerGate(this, blinkTimer, requireFocus: true, wanted: () => CaretBlink.Animates(EditorScrollOptions.CursorBlinking) && !caretBlinkFinished);
            blinkGate.RunningChanged += () => { caretBlinkResetMs = NowMs; caretOverlay?.InvalidateVisual(); };
            AddHandler(KeyDownEvent, (s, e) => { RestartCaretBlink(); BindUndoCaretSource(Buffer); }, global::Avalonia.Interactivity.RoutingStrategies.Tunnel, true);
            AddHandler(TextInputEvent, (s, e) => { RestartCaretBlink(); BindUndoCaretSource(Buffer); }, global::Avalonia.Interactivity.RoutingStrategies.Tunnel, true);
        }

        private readonly UiTimerGate blinkGate;
        private double caretBlinkResetMs;
        private bool caretBlinkFinished;
        public bool IsCaretBlinkTimerRunning => blinkTimer.IsEnabled;
        /// <summary>Whether the caret is (mostly) visible right now.</summary>
        public bool IsCaretShown { get { GetCaretAppearance(out double o, out double sy); return o * sy >= 0.5; } }

        /// <summary>Caret opacity / vertical scale for the current blink phase (solid while the blink timer sleeps).</summary>
        public void GetCaretAppearance(out double opacity, out double scaleY)
        {
            opacity = 1; scaleY = 1;
            if (!blinkTimer.IsEnabled) return;
            CaretBlink.Evaluate(EditorScrollOptions.CursorBlinking, NowMs - caretBlinkResetMs, out opacity, out scaleY);
        }

        /// <summary>Any input shows the caret solid (VS Code) and restarts the blink cycle.</summary>
        private void RestartCaretBlink()
        {
            caretBlinkResetMs = NowMs;
            caretBlinkFinished = false;
            blinkGate?.Update();
            if (blinkTimer.IsEnabled) blinkTimer.Interval = TimeSpan.FromMilliseconds(CaretBlink.IntervalMs); // restarts the timer
            caretOverlay?.InvalidateVisual();
        }

        /// <summary>Re-reads the caret settings (blink style, smooth caret) after they changed.</summary>
        public void ApplyCaretSettings()
        {
            RestartCaretBlink();
            InvalidateVisual();
        }

        private void OnCaretBlinkTick()
        {
            caretOverlay?.InvalidateVisual();
            double next = CaretBlink.NextChangeMs(EditorScrollOptions.CursorBlinking, NowMs - caretBlinkResetMs);
            if (next < 0)
            {
                caretBlinkFinished = true; // VS Code's animated blink ran out: solid caret, no more wake-ups until the next input
                blinkGate.Update();
                return;
            }
            // fading: ~60 Hz repaints of the caret layer only; holding: sleep until the next change
            double ms = next <= 0 ? 16 : Math.Max(16, next);
            if (Math.Abs(blinkTimer.Interval.TotalMilliseconds - ms) > 0.5) blinkTimer.Interval = TimeSpan.FromMilliseconds(ms);
        }

        public EditorTabBuffer Buffer => owner.CurrentBuffer;

        public void ApplyTheme(StudioTheme theme)
        {
            if (theme == null) return;
            Theme = theme;
            InvalidateVisual();
        }

        public void ResetExtraCursors()
        {
            ExtraCursors.Clear();
            InvalidateVisual();
        }

        public int TotalCursorCount => 1 + ExtraCursors.Count;

        /// <summary>
        /// Scrolls the primary caret into view. Animated (smooth scrolling) after navigation; snaps right after a text
        /// edit so typing never waits for an animation.
        /// </summary>
        public void EnsureCursorVisible()
        {
            var buf = Buffer;
            if (buf == null) return;
            bool afterEdit = RevealFollowsEdit(buf);
            RunReveal(buf, () => EnsureCursorVisibleCore(buf), animate: !afterEdit);
        }

        /// <summary>Scrolls the primary caret into view, animated or not (overrides the typing heuristic).</summary>
        public void EnsureCursorVisible(bool animate)
        {
            var buf = Buffer;
            if (buf == null) return;
            RevealFollowsEdit(buf);
            RunReveal(buf, () => EnsureCursorVisibleCore(buf), animate);
        }

        private void EnsureCursorVisibleCore(EditorTabBuffer buf)
        {
            buf.ClampCursor();
            EnsureCaretRowVisible(buf);
            KeepCaretOutOfStickyScroll(buf);
            // Horizontal: keep the caret inside the viewport when typing past the right edge (never while wrapping).
            if (buf.WordWrap) { buf.ScrollLeftPx = 0; return; }
            if (Bounds.Width > GutterWidth + 40)
            {
                double caretX = ColX(buf, buf.CursorLine, buf.CursorCol);
                double viewW = CodeAreaRight - GutterWidth - 24;
                if (caretX < buf.ScrollLeftPx) buf.ScrollLeftPx = Math.Max(0, caretX - CharWidth * 4);
                else if (caretX > buf.ScrollLeftPx + viewW) buf.ScrollLeftPx = caretX - viewW + CharWidth * 4;
            }
        }

        public void FoldAtLine(int lineIdx)
        {
            var buf = Buffer;
            if (buf == null || lineIdx < 0 || lineIdx >= buf.Lines.Count) return;
            int endLine = FindFoldEndLine(buf, lineIdx);
            if (endLine > lineIdx)
            {
                buf.FoldedBlockRanges[lineIdx] = endLine;
                // VS Code: a caret inside the collapsed region moves to its header
                if (buf.CursorLine > lineIdx && buf.CursorLine <= endLine)
                {
                    buf.CursorLine = lineIdx;
                    buf.CursorCol = Math.Min(buf.CursorCol, buf.Lines[lineIdx].Length);
                    buf.ClearSelection();
                    owner.NotifyCaretMoved();
                }
                NormalizeScroll(buf);
                InvalidateVisual();
            }
        }

        /// <summary>Ctrl+Shift+[ at a line that is not a header folds the innermost range containing it (VS Code "Fold").</summary>
        public void FoldAtCaret()
        {
            var buf = Buffer;
            if (buf == null) return;
            int line = buf.CursorLine;
            if (FindFoldEndLine(buf, line) > line && !buf.FoldedBlockRanges.ContainsKey(line)) { FoldAtLine(line); return; }
            var model = FoldModel(buf, 0);
            int best = -1, bestEnd = int.MaxValue;
            foreach (var r in model.Ranges)
            {
                if (r.Start < line && r.End >= line && !buf.FoldedBlockRanges.ContainsKey(r.Start) && r.End - r.Start < bestEnd - Math.Max(0, best))
                { best = r.Start; bestEnd = r.End; }
            }
            if (best >= 0) FoldAtLine(best);
        }

        /// <summary>Ctrl+Shift+] unfolds the region at the caret line or the innermost folded region containing the caret.</summary>
        public void UnfoldAtCaret()
        {
            var buf = Buffer;
            if (buf == null) return;
            if (buf.FoldedBlockRanges.ContainsKey(buf.CursorLine)) { UnfoldAtLine(buf.CursorLine); return; }
            int best = -1;
            foreach (var kv in buf.FoldedBlockRanges)
                if (kv.Key < buf.CursorLine && kv.Value >= buf.CursorLine && kv.Key > best) best = kv.Key;
            if (best >= 0) UnfoldAtLine(best);
        }

        public void UnfoldAtLine(int lineIdx)
        {
            var buf = Buffer;
            if (buf == null) return;
            if (buf.FoldedBlockRanges.Remove(lineIdx))
                InvalidateVisual();
        }

        public void ToggleFoldAtLine(int lineIdx)
        {
            var buf = Buffer;
            if (buf == null) return;
            if (buf.FoldedBlockRanges.ContainsKey(lineIdx))
                UnfoldAtLine(lineIdx);
            else
                FoldAtLine(lineIdx);
        }

        public void FoldAll()
        {
            var buf = Buffer;
            if (buf == null) return;
            var model = FoldModel(buf, 0);
            foreach (var r in model.Ranges)
                buf.FoldedBlockRanges[r.Start] = r.End;
            if (buf.FoldedBlockRanges.Count > 0)
            {
                int h = EditorFoldState.HiddenBy(buf, buf.CursorLine);
                if (h >= 0) { buf.CursorLine = h; buf.CursorCol = Math.Min(buf.CursorCol, buf.Lines[h].Length); buf.ClearSelection(); }
            }
            NormalizeScroll(buf);
            InvalidateVisual();
        }

        public void UnfoldAll()
        {
            var buf = Buffer;
            if (buf == null) return;
            buf.FoldedBlockRanges.Clear();
            InvalidateVisual();
        }

        /// <summary>Last line hidden when <paramref name="startLine"/> is folded (startLine itself when it is not a fold header).</summary>
        public int FindFoldEndLine(EditorTabBuffer buf, int startLine)
        {
            if (buf == null || startLine < 0 || startLine >= buf.Lines.Count) return startLine;
            int e = EditorFoldingModel.GetFoldEnd(buf, startLine, TabFor(buf));
            return e > startLine ? e : startLine;
        }

        protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
        {
            base.OnPointerWheelChanged(e);
            PointerLeftCanvas?.Invoke();
            var buf = Buffer;
            if (buf == null) return;
            if (e.KeyModifiers.HasFlag(KeyModifiers.Control) || e.KeyModifiers.HasFlag(KeyModifiers.Meta))
            {
                // Accumulate so a trackpad's stream of tiny deltas doesn't swing the font size every event.
                zoomAccum += e.Delta.Y;
                if (Math.Abs(zoomAccum) >= 1.0)
                {
                    FontSizePx = Math.Max(9.0, Math.Min(28.0, FontSizePx + (zoomAccum > 0 ? 1.0 : -1.0)));
                    zoomAccum = 0;
                    NormalizeScroll(buf); // wrap columns follow the new character width
                    InvalidateVisual();
                }
                e.Handled = true;
                return;
            }
            HandleWheelScroll(buf, e); // pixel-precise / animated (AvaloniaEditorCanvas.Smooth.cs)
            InvalidateVisual();
            e.Handled = true;
        }

        private double zoomAccum;

        // ---- Pointer cursor, Cmd/Ctrl+Click links, gutter selection and drag auto-scroll ---------------------
        private static readonly Dictionary<StandardCursorType, Cursor> cursorCache = new Dictionary<StandardCursorType, Cursor>();
        private StandardCursorType? appliedCursorType;
        private EditorLinkTarget linkHover;
        private int linkHoverLine = -1;
        private bool isGutterSelecting;
        private int gutterAnchorLine = -1;
        private DispatcherTimer dragScrollTimer;
        private Point lastDragPt;

        /// <summary>The link currently underlined under the pointer while Cmd (macOS) / Ctrl is held, or null.</summary>
        public EditorLinkTarget HoveredLink => linkHover;
        public int HoveredLinkLine => linkHoverLine;

        /// <summary>Caret rectangle from the last rendered frame (canvas coordinates); used to place LSP popups.</summary>
        public Rect CaretRect => imeCaretRect;

        /// <summary>Document position of the character under <paramref name="pt"/> (canvas coordinates); false over gutter / minimap / past line end.</summary>
        public bool TryHitTestText(Point pt, out int line, out int col)
        {
            line = -1;
            col = -1;
            if (Buffer == null || pt.X < GutterWidth || pt.X >= CodeAreaRight || pt.Y < 0 || pt.Y >= Bounds.Height) return false;
            line = ScreenYToDocLine(pt.Y);
            col = CharIndexUnderPoint(line, pt.X);
            return line >= 0 && col >= 0;
        }

        private static Cursor GetCachedCursor(StandardCursorType type)
        {
            if (cursorCache.TryGetValue(type, out var c)) return c;
            try { c = new Cursor(type); } catch { c = null; }
            cursorCache[type] = c;
            return c;
        }

        private void SetPointerCursor(StandardCursorType type)
        {
            if (appliedCursorType == type) return;
            var c = GetCachedCursor(type);
            if (c == null) return;
            appliedCursorType = type;
            Cursor = c;
        }

        /// <summary>Right edge of the text area: the vertical scrollbar / overview ruler (and the minimap left of it) are excluded.</summary>
        private double CodeAreaRight => Math.Max(GutterWidth + 60, Bounds.Width - ScrollbarWidth - (ShowMinimap ? MinimapWidth : 0));

        /// <summary>Left edge of the minimap (it sits between the text and the vertical scrollbar, like VS Code).</summary>
        private double MinimapLeft => Bounds.Width - ScrollbarWidth - MinimapWidth;

        private bool IsOverMinimap(Point pt) => ShowMinimap && pt.X >= MinimapLeft && pt.X < Bounds.Width - ScrollbarWidth;

        /// <summary>The character the pointer is over (not the nearest caret boundary); -1 past the end of the row.</summary>
        private int CharIndexUnderPoint(int docLine, double x)
        {
            var buf = Buffer;
            if (buf == null || docLine < 0 || docLine >= buf.Lines.Count) return -1;
            string line = buf.Lines[docLine] ?? "";
            var row = hitRowValid && hitRow.DocLine == docLine ? hitRow : MakeRow(buf, docLine, 0);
            double vcol = (x - GutterWidth - 8 + (buf.WordWrap ? 0 : buf.ScrollLeftPx)) / CharWidth;
            return EditorTextLayout.CharIndexUnderVisualColumn(line, row.Start, row.End, vcol, TabFor(buf), row.StartVcol);
        }

        /// <summary>Recomputes which pointer shape the editor should show at <paramref name="pt"/> (VS Code parity:
        /// I-beam over text, arrow over the gutter / minimap, hand over links and inline buttons).</summary>
        private void UpdatePointerCursor(Point pt)
        {
            if (isGutterSelecting) { SetPointerCursor(StandardCursorType.Arrow); return; }
            if (isMouseSelecting) { SetPointerCursor(StandardCursorType.Ibeam); return; }
            foreach (var hb in hunkActionHitBoxes)
            {
                if (hb.Rect.Contains(pt)) { SetPointerCursor(StandardCursorType.Hand); return; }
            }
            foreach (var ah in annotationHitBoxes)
            {
                if (ah.Rect.Contains(pt)) { SetPointerCursor(StandardCursorType.Hand); return; }
            }
            if (IsOverMinimap(pt) || IsOverVerticalScrollbar(pt) || IsOverHorizontalScrollbar(pt)) { SetPointerCursor(StandardCursorType.Arrow); return; }
            if (pt.X < GutterWidth)
            {
                var buf = Buffer;
                bool overChevron = buf != null && pt.X >= GutterWidth - 18 && CanFoldLine(buf, ScreenYToDocLine(pt.Y));
                SetPointerCursor(overChevron ? StandardCursorType.Hand : StandardCursorType.Arrow);
                return;
            }
            SetPointerCursor(linkHover != null ? StandardCursorType.Hand : StandardCursorType.Ibeam);
        }

        /// <summary>Resolves the link under the pointer while the primary modifier is held and underlines it.</summary>
        private void UpdateLinkHover(Point pt, KeyModifiers mods)
        {
            EditorLinkTarget next = null;
            int nextLine = -1;
            var buf = Buffer;
            bool primary = MacKeymap.IsPrimary(mods) && (mods & (KeyModifiers.Shift | KeyModifiers.Alt)) == 0;
            if (buf != null && primary && !isMouseSelecting && !isGutterSelecting && IsPointerOver &&
                pt.X >= GutterWidth && pt.X < CodeAreaRight && pt.Y >= 0 && pt.Y < Bounds.Height)
            {
                int line = ScreenYToDocLine(pt.Y);
                int col = CharIndexUnderPoint(line, pt.X);
                if (col >= 0)
                {
                    nextLine = line;
                    if (linkHover != null && linkHoverLine == line && col >= linkHover.StartCol && col < linkHover.EndCol)
                        next = linkHover;
                    else
                        next = owner.ResolveLinkAt(buf, line, col);
                }
            }
            if (!ReferenceEquals(next, linkHover))
            {
                bool changed = next == null || linkHover == null || linkHoverLine != nextLine || !next.SameSpan(linkHover);
                linkHover = next;
                linkHoverLine = next != null ? nextLine : -1;
                if (changed) InvalidateVisual();
            }
            UpdatePointerCursor(pt);
        }

        private void ClearLinkHover()
        {
            if (linkHover == null) return;
            linkHover = null;
            linkHoverLine = -1;
            InvalidateVisual();
        }

        protected override void OnPointerExited(PointerEventArgs e)
        {
            base.OnPointerExited(e);
            ClearLinkHover();
            ClearScrollbarHover();
            NoteScrollActivity(); // the slider fades out shortly after the pointer leaves (VS Code)
            if (stickyHoverIndex >= 0) { stickyHoverIndex = -1; InvalidateVisual(); }
            PointerLeftCanvas?.Invoke();
            if (hoveredAnnotation != null) { hoveredAnnotation = null; owner.NotifyAnnotationHovered(null, -1, default); InvalidateVisual(); }
        }

        protected override void OnLostFocus(global::Avalonia.Interactivity.RoutedEventArgs e)
        {
            base.OnLostFocus(e);
            ClearLinkHover();
        }

        protected override void OnKeyUp(KeyEventArgs e)
        {
            base.OnKeyUp(e);
            // Releasing Cmd / Ctrl removes the link underline without needing a pointer move (VS Code parity).
            if (IsModifierKey(e.Key)) UpdateLinkHover(lastHoverPt, e.KeyModifiers);
        }

        private static bool IsModifierKey(Key k) =>
            k == Key.LWin || k == Key.RWin || k == Key.LeftCtrl || k == Key.RightCtrl ||
            k == Key.LeftShift || k == Key.RightShift || k == Key.LeftAlt || k == Key.RightAlt;

        private bool IsInsideSelection(EditorTabBuffer buf, int line, int col)
        {
            if (buf == null || !buf.HasSelection()) return false;
            int sl = buf.SelectionStartLine, sc = buf.SelectionStartCol, el = buf.CursorLine, ec = buf.CursorCol;
            if (sl > el || (sl == el && sc > ec)) { (sl, el) = (el, sl); (sc, ec) = (ec, sc); }
            if (line < sl || line > el) return false;
            if (line == sl && col < sc) return false;
            if (line == el && col > ec) return false;
            return true;
        }

        private void SelectWholeLines(EditorTabBuffer buf, int anchorLine, int activeLine)
        {
            int last = buf.Lines.Count - 1;
            anchorLine = Math.Max(0, Math.Min(last, anchorLine));
            activeLine = Math.Max(0, Math.Min(last, activeLine));
            if (activeLine >= anchorLine)
            {
                buf.SelectionStartLine = anchorLine;
                buf.SelectionStartCol = 0;
                if (activeLine < last) { buf.CursorLine = activeLine + 1; buf.CursorCol = 0; }
                else { buf.CursorLine = activeLine; buf.CursorCol = buf.Lines[activeLine].Length; }
            }
            else
            {
                // Dragging upward: anchor at the end of the anchor line so it stays fully selected.
                if (anchorLine < last) { buf.SelectionStartLine = anchorLine + 1; buf.SelectionStartCol = 0; }
                else { buf.SelectionStartLine = anchorLine; buf.SelectionStartCol = buf.Lines[anchorLine].Length; }
                buf.CursorLine = activeLine;
                buf.CursorCol = 0;
            }
        }

        protected override void OnPointerPressed(PointerPressedEventArgs e)
        {
            base.OnPointerPressed(e);
            Focus();
            var buf = Buffer;
            if (buf == null) return;

            var pt = e.GetPosition(this);
            lastHoverPt = pt;
            multiAutoClosed.Clear();
            var props = e.GetCurrentPoint(this).Properties;
            var mods = e.KeyModifiers;
            if (props.IsLeftButtonPressed && TryHandleScrollbarPressed(pt, e)) return; // scrollbar / overview ruler (QA #28)
            if (IsOverVerticalScrollbar(pt) || IsOverHorizontalScrollbar(pt)) { e.Handled = true; return; }
            if (props.IsLeftButtonPressed && TryHandleStickyPointerPressed(pt)) { e.Handled = true; return; }

            // End-of-line annotation click (Send Request lens / blame details)
            if (props.IsLeftButtonPressed && annotationHitBoxes.Count > 0)
            {
                foreach (var ah in annotationHitBoxes)
                {
                    if (ah.Rect.Contains(pt))
                    {
                        owner.NotifyAnnotationClicked(ah.Ann, ah.Line, ah.Rect);
                        e.Handled = true;
                        return;
                    }
                }
            }

            // Mouse back / forward buttons navigate history like VS Code.
            if (props.IsXButton1Pressed || props.IsXButton2Pressed)
            {
                owner.NotifyNavigateHistory(props.IsXButton1Pressed ? -1 : 1);
                e.Handled = true;
                return;
            }

            // macOS: Control+Click is the secondary click.
            bool macCtrlClick = MacKeymap.IsMacOS && props.IsLeftButtonPressed &&
                                (mods & KeyModifiers.Control) != 0 && (mods & KeyModifiers.Meta) == 0;
            if (props.IsRightButtonPressed || macCtrlClick)
            {
                // Like VS Code: a right-click outside the selection moves the caret there first, so context-menu
                // commands (Go to Definition, Rename, ...) act on the clicked word, not the old caret position.
                if (pt.X >= GutterWidth && pt.X < CodeAreaRight)
                {
                    int rl = ScreenYToDocLine(pt.Y);
                    int rc = ScreenXToDocCol(rl, pt.X);
                    if (!IsInsideSelection(buf, rl, rc))
                    {
                        ExtraCursors.Clear();
                        buf.ClearSelection();
                        buf.CursorLine = rl;
                        buf.CursorCol = rc;
                        ApplyHitAffinity(buf);
                        owner.NotifyCaretMoved();
                        InvalidateVisual();
                    }
                }
                if (pt.X < EditorDebugHooks.GlyphMarginWidth && owner.RaiseBreakpointGutter(buf, ScreenYToDocLine(pt.Y), true)) { e.Handled = true; return; }
                owner.NotifyContextMenu(pt);
                e.Handled = true;
                return;
            }
            if (!props.IsLeftButtonPressed) return;

            // Check per-hunk Accept / Reject buttons
            foreach (var hb in hunkActionHitBoxes)
            {
                if (hb.Rect.Contains(pt))
                {
                    if (hb.IsAccept) owner.AcceptDiffHunkAtLine(hb.Line);
                    else owner.RejectDiffHunkAtLine(hb.Line);
                    e.Handled = true;
                    return;
                }
            }

            // Check Minimap click/drag
            if (IsOverMinimap(pt))
            {
                isMinimapDragging = true;
                ScrollFromMinimapY(pt.Y);
                e.Handled = true;
                return;
            }

            int docLine = ScreenYToDocLine(pt.Y);
            int docCol = ScreenXToDocCol(docLine, pt.X);

            // the gutter of a read-only deleted row (inline diff) has no breakpoints / folding / line selection
            if (pt.X < GutterWidth && IsDeletedRowAt(pt.Y)) { e.Handled = true; return; }
            if (pt.X < GutterWidth)
            {
                // Glyph margin (left of the line numbers): toggle a breakpoint, like VS Code.
                if (pt.X < EditorDebugHooks.GlyphMarginWidth && owner.RaiseBreakpointGutter(buf, docLine, false)) { e.Handled = true; return; }
                // Git diff bar (rightmost gutter pixels): Stage / Revert / Diff popup, like VS Code's quick diff peek.
                if (pt.X >= GutterWidth - 8 && owner.RaiseGutterDiffClicked(buf, docLine, new Rect(GutterWidth - 7, Math.Floor((pt.Y + TopRowOffsetPx) / LineHeight) * LineHeight - TopRowOffsetPx, 7, LineHeight))) { e.Handled = true; return; }
                // Fold chevron
                if (pt.X >= GutterWidth - 18 && CanFoldLine(buf, docLine))
                {
                    ToggleFoldAtLine(docLine);
                    e.Handled = true;
                    return;
                }
                // Line numbers: click selects the whole line, Shift+click extends, drag selects a line range.
                ExtraCursors.Clear();
                int anchor = docLine;
                if ((mods & KeyModifiers.Shift) != 0)
                    anchor = buf.HasSelection() ? Math.Min(buf.SelectionStartLine, buf.CursorLine) : buf.CursorLine;
                gutterAnchorLine = anchor;
                SelectWholeLines(buf, anchor, docLine);
                isGutterSelecting = true;
                e.Pointer.Capture(this);
                RestartCaretBlink();
                owner.NotifyCaretMoved();
                InvalidateVisual();
                e.Handled = true;
                return;
            }

            // Cmd+Click (macOS) / Ctrl+Click: follow the import / path / URL or go to the definition (VS Code).
            if (MacKeymap.IsPrimary(mods) && (mods & (KeyModifiers.Shift | KeyModifiers.Alt)) == 0 && e.ClickCount == 1)
            {
                int charIdx = CharIndexUnderPoint(docLine, pt.X);
                EditorLinkTarget target = charIdx >= 0
                    ? (linkHover != null && linkHoverLine == docLine && charIdx >= linkHover.StartCol && charIdx < linkHover.EndCol
                        ? linkHover
                        : owner.ResolveLinkAt(buf, docLine, charIdx))
                    : null;
                ExtraCursors.Clear();
                buf.ClearSelection();
                buf.CursorLine = docLine;
                buf.CursorCol = docCol;
                ApplyHitAffinity(buf);
                RestartCaretBlink();
                owner.NotifyCaretMoved();
                ClearLinkHover();
                InvalidateVisual();
                e.Handled = true;
                if (target != null) owner.NotifyLinkActivated(target, docLine);
                return;
            }

            // Alt+Click adds/removes multi-cursor
            if (e.KeyModifiers.HasFlag(KeyModifiers.Alt) && !e.KeyModifiers.HasFlag(KeyModifiers.Shift))
            {
                int existingIdx = ExtraCursors.FindIndex(c => c.Line == docLine && c.Col == docCol);
                if (existingIdx >= 0)
                    ExtraCursors.RemoveAt(existingIdx);
                else
                    ExtraCursors.Add(new ExtraCursorCaret { Line = docLine, Col = docCol, AnchorLine = -1, AnchorCol = -1 });
                InvalidateVisual();
                e.Handled = true;
                return;
            }

            // Shift+Alt+Click or ColumnSelectionMode -> rectangular box selection
            if ((e.KeyModifiers.HasFlag(KeyModifiers.Shift) && e.KeyModifiers.HasFlag(KeyModifiers.Alt)) ||
                (ColumnSelectionMode && e.KeyModifiers.HasFlag(KeyModifiers.Shift)))
            {
                if (boxAnchorLine < 0 || !buf.HasSelection() && ExtraCursors.Count == 0) { boxAnchorLine = buf.CursorLine; boxAnchorVcol = Vcol(buf.Lines[buf.CursorLine], buf.CursorCol); }
                ApplyColumnBoxSelectionVisual(boxAnchorLine, boxAnchorVcol, docLine, VisualColumnAtX(buf, pt.X));
                isMouseSelecting = true;
                e.Pointer.Capture(this);
                InvalidateVisual();
                e.Handled = true;
                return;
            }

            ExtraCursors.Clear();
            boxAnchorLine = -1;
            buf.LastTypingUndoTick = 0;
            if (e.KeyModifiers.HasFlag(KeyModifiers.Shift))
            {
                if (!buf.HasSelection())
                {
                    buf.SelectionStartLine = buf.CursorLine;
                    buf.SelectionStartCol = buf.CursorCol;
                }
                buf.CursorLine = docLine;
                buf.CursorCol = docCol;
                ApplyHitAffinity(buf);
                isMouseSelecting = true;
                e.Pointer.Capture(this);
            }
            else if (e.ClickCount == 2)
            {
                SelectWordAt(docLine, docCol);
            }
            else if (e.ClickCount >= 3)
            {
                // Triple-click selects the whole line including its line break (VS Code).
                SelectWholeLines(buf, docLine, docLine);
            }
            else
            {
                buf.CursorLine = docLine;
                buf.CursorCol = docCol;
                ApplyHitAffinity(buf);
                buf.ClearSelection();
                isMouseSelecting = true;
                e.Pointer.Capture(this);
            }

            RestartCaretBlink();
            owner.NotifyCaretMoved();
            UpdatePointerCursor(pt);
            InvalidateVisual();
            e.Handled = true;
        }

        protected override void OnPointerMoved(PointerEventArgs e)
        {
            base.OnPointerMoved(e);
            var buf = Buffer;
            var pt = e.GetPosition(this);
            lastHoverPt = pt;
            if (buf == null) { SetPointerCursor(StandardCursorType.Arrow); return; }
            if (TryHandleScrollbarMoved(pt)) return;
            PointerMovedOverCanvas?.Invoke(pt);
            if ((stickyHitBoxes.Count > 0 || stickyHoverIndex >= 0) && !isMouseSelecting && UpdateStickyHover(pt)) { SetPointerCursor(StandardCursorType.Hand); return; }

            if (isMinimapDragging)
            {
                ScrollFromMinimapY(pt.Y);
                return;
            }

            int docLine = ScreenYToDocLine(pt.Y);
            if (docLine != hoveredLine)
            {
                int prevHover = hoveredLine;
                hoveredLine = docLine;
                bool hadDiag = prevHover >= 0 && buf.Diagnostics != null && buf.Diagnostics.Any(d => d.LineNumber - 1 == prevHover);
                bool hasDiag = docLine >= 0 && buf.Diagnostics != null && buf.Diagnostics.Any(d => d.LineNumber - 1 == docLine);
                if (hadDiag || hasDiag || hunkActionHitBoxes.Count > 0)
                    InvalidateVisual();
            }

            if (isGutterSelecting || isMouseSelecting)
            {
                lastDragPt = pt;
                ApplyDragSelection(pt, e.KeyModifiers);
                UpdateDragAutoScroll(pt);
                UpdatePointerCursor(pt);
                return;
            }

            UpdateAnnotationHover(pt);
            UpdateLinkHover(pt, e.KeyModifiers);
        }

        private void UpdateAnnotationHover(Point pt)
        {
            EditorLineAnnotation hit = null;
            int hitLine = -1;
            Rect hitRect = default;
            foreach (var ah in annotationHitBoxes)
            {
                if (ah.Rect.Contains(pt)) { hit = ah.Ann; hitLine = ah.Line; hitRect = ah.Rect; break; }
            }
            if (!ReferenceEquals(hit, hoveredAnnotation))
            {
                hoveredAnnotation = hit;
                owner.NotifyAnnotationHovered(hit, hitLine, hitRect);
                InvalidateVisual();
            }
        }

        internal bool IsPointerOverAnnotation => hoveredAnnotation != null;

        private KeyModifiers lastDragMods;

        private void ApplyDragSelection(Point pt, KeyModifiers mods)
        {
            var buf = Buffer;
            if (buf == null) return;
            lastDragMods = mods;
            int docLine = ScreenYToDocLine(Math.Max(0, Math.Min(Bounds.Height - 1, pt.Y)));
            if (isGutterSelecting)
            {
                SelectWholeLines(buf, gutterAnchorLine, docLine);
            }
            else
            {
                int docCol = ScreenXToDocCol(docLine, pt.X);
                if (mods.HasFlag(KeyModifiers.Alt) || ColumnSelectionMode)
                {
                    if (boxAnchorLine < 0 || boxAnchorLine >= buf.Lines.Count)
                    {
                        int anchorL = buf.SelectionStartLine >= 0 ? buf.SelectionStartLine : buf.CursorLine;
                        int anchorC = buf.SelectionStartCol >= 0 ? buf.SelectionStartCol : buf.CursorCol;
                        boxAnchorLine = anchorL;
                        boxAnchorVcol = Vcol(buf.Lines[anchorL], anchorC);
                    }
                    ApplyColumnBoxSelectionVisual(boxAnchorLine, boxAnchorVcol, docLine, VisualColumnAtX(buf, pt.X));
                }
                else
                {
                    if (!buf.HasSelection())
                    {
                        buf.SelectionStartLine = buf.CursorLine;
                        buf.SelectionStartCol = buf.CursorCol;
                    }
                    buf.CursorLine = docLine;
                    buf.CursorCol = docCol;
                    ApplyHitAffinity(buf);
                }
                // Horizontal follow while dragging past the left / right edge.
                if (!buf.WordWrap && (pt.X > CodeAreaRight - 8 || (pt.X < GutterWidth + 8 && buf.ScrollLeftPx > 0)))
                    EnsureCursorVisible(animate: false); // drag-scrolling follows the pointer directly
            }
            owner.NotifyCaretMoved();
            InvalidateVisual();
        }

        /// <summary>While drag-selecting above / below the viewport, keep scrolling and extending the selection.</summary>
        private void UpdateDragAutoScroll(Point pt)
        {
            bool outside = pt.Y < 0 || pt.Y > Bounds.Height;
            if (!outside)
            {
                dragScrollTimer?.Stop();
                return;
            }
            if (dragScrollTimer == null)
            {
                dragScrollTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(40) };
                dragScrollTimer.Tick += (s, e) =>
                {
                    var b = Buffer;
                    if (b == null || (!isMouseSelecting && !isGutterSelecting)) { dragScrollTimer.Stop(); return; }
                    double over = lastDragPt.Y < 0 ? lastDragPt.Y : lastDragPt.Y - Bounds.Height;
                    if (Math.Abs(over) < 0.5) { dragScrollTimer.Stop(); return; }
                    int step = Math.Max(1, Math.Min(12, (int)(Math.Abs(over) / Math.Max(4, LineHeight)) + 1));
                    ScrollByRows(b, over < 0 ? -step : step);
                    ApplyDragSelection(lastDragPt, lastDragMods);
                };
            }
            if (!dragScrollTimer.IsEnabled) dragScrollTimer.Start();
        }

        protected override void OnPointerReleased(PointerReleasedEventArgs e)
        {
            base.OnPointerReleased(e);
            if (EndScrollbarDrag()) e.Pointer.Capture(null);
            isMouseSelecting = false;
            isMinimapDragging = false;
            isGutterSelecting = false;
            dragScrollTimer?.Stop();
            var pt = e.GetPosition(this);
            lastHoverPt = pt;
            UpdateLinkHover(pt, e.KeyModifiers);
        }

        protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
        {
            base.OnPointerCaptureLost(e);
            EndScrollbarDrag();
            isMouseSelecting = false;
            isMinimapDragging = false;
            isGutterSelecting = false;
            dragScrollTimer?.Stop();
        }

        /// <summary>macOS hides the pointer while typing until the mouse moves (NSTextView / VS Code behaviour).</summary>
        private static void HidePointerWhileTyping()
        {
            if (!MacKeymap.IsMacOS || !AvaloniaTerminalEmulatorControl.IsDesktopRuntime) return;
            try { MacCursorNative.SetHiddenUntilMouseMoves(); } catch { }
        }

        private void ScrollFromMinimapY(double y)
        {
            var buf = Buffer;
            if (buf == null || Bounds.Height <= 10) return;
            double ratio = Math.Max(0, Math.Min(1.0, y / Bounds.Height));
            buf.ScrollTopLine = VisibleLineAtOrAbove(buf, (int)(ratio * Math.Max(0, buf.Lines.Count - 1)));
            buf.ScrollTopWrapRow = 0;
            InvalidateVisual();
        }

        /// <summary>Document line of the screen row at <paramref name="y"/>; remembers the row for the following X hit test.</summary>
        private int ScreenYToDocLine(double y)
        {
            var buf = Buffer;
            if (buf == null || buf.Lines.Count == 0) return 0;
            hitRow = RowAtY(y);
            hitRowValid = true;
            return hitRow.DocLine;
        }

        private bool lastHitEndOfRow;

        /// <summary>Caret boundary nearest to x on the row last hit by <see cref="ScreenYToDocLine"/> (first row of the line otherwise).</summary>
        private int ScreenXToDocCol(int docLine, double x)
        {
            var buf = Buffer;
            lastHitEndOfRow = false;
            if (buf == null || docLine < 0 || docLine >= buf.Lines.Count) return 0;
            var row = hitRowValid && hitRow.DocLine == docLine ? hitRow : MakeRow(buf, docLine, 0);
            string text = buf.Lines[docLine] ?? "";
            double rel = Math.Max(0, x - GutterWidth - 8 + (buf.WordWrap ? 0 : buf.ScrollLeftPx));
            int col = EditorTextLayout.CharIndexForVisualColumnInRange(text, row.Start, row.End, rel / CharWidth, TabFor(buf), row.StartVcol);
            if (!row.IsLastRow && col >= row.End) { col = row.End; lastHitEndOfRow = true; }
            return Math.Max(0, Math.Min(text.Length, col));
        }

        /// <summary>After a click / drag set the primary caret: keeps it on the clicked row when it lands on a soft break.</summary>
        private void ApplyHitAffinity(EditorTabBuffer buf)
        {
            if (lastHitEndOfRow) SetAffinityEndOfRow(buf.CursorLine, buf.CursorCol);
            else { affinityLine = -1; affinityCol = -1; }
        }

        private bool CanFoldLine(EditorTabBuffer buf, int lineIdx)
        {
            if (buf == null || lineIdx < 0 || lineIdx >= buf.Lines.Count) return false;
            if (buf.FoldedBlockRanges.ContainsKey(lineIdx)) return true;
            var m = FoldModelForFrame(buf);
            return m != null && m.IsFoldable(lineIdx);
        }

        // anchor of the current column (box) selection: line + visual column (VS Code keeps it while extending)
        private int boxAnchorLine = -1;
        private double boxAnchorVcol;

        /// <summary>Column (box) selection between two document positions, measured in visual columns (tabs / wide characters).</summary>
        public void ApplyColumnBoxSelection(int startLine, int startCol, int endLine, int endCol)
        {
            var buf = Buffer;
            if (buf == null) return;
            startLine = Math.Max(0, Math.Min(buf.Lines.Count - 1, startLine));
            endLine = Math.Max(0, Math.Min(buf.Lines.Count - 1, endLine));
            ApplyColumnBoxSelectionVisual(startLine, Vcol(buf.Lines[startLine], startCol), endLine, Vcol(buf.Lines[endLine], endCol));
        }

        /// <summary>Box selection from an anchor (line, visual column) to the active end; the primary caret is the active end.</summary>
        internal void ApplyColumnBoxSelectionVisual(int anchorLine, double anchorV, int activeLine, double activeV)
        {
            var buf = Buffer;
            if (buf == null) return;
            boxAnchorLine = anchorLine;
            boxAnchorVcol = anchorV;
            ExtraCursors.Clear();
            int tab = TabSize;
            int step = activeLine >= anchorLine ? 1 : -1;
            for (int l = anchorLine; ; l += step)
            {
                string text = buf.Lines[l] ?? "";
                int a = EditorTextLayout.CharIndexForVisualColumn(text, anchorV, tab);
                int c = EditorTextLayout.CharIndexForVisualColumn(text, activeV, tab);
                if (l == activeLine)
                {
                    buf.SelectionStartLine = l; buf.SelectionStartCol = a;
                    buf.CursorLine = l; buf.CursorCol = c;
                    if (a == c) buf.ClearSelection();
                    break;
                }
                ExtraCursors.Add(new ExtraCursorCaret { AnchorLine = a == c ? -1 : l, AnchorCol = a == c ? -1 : a, Line = l, Col = c });
            }
            affinityLine = -1;
        }

        /// <summary>Visual column under a canvas x on the row (may lie past the end of the line: box selections use virtual space).</summary>
        private double VisualColumnAtX(EditorTabBuffer buf, double x)
        {
            return Math.Max(0, Math.Round((x - GutterWidth - 8 + (buf.WordWrap ? 0 : buf.ScrollLeftPx)) / CharWidth));
        }

        public void SelectWordAt(int line, int col)
        {
            var buf = Buffer;
            if (buf == null || line < 0 || line >= buf.Lines.Count) return;
            string text = buf.Lines[line];
            if (text.Length == 0) return;
            int idx = Math.Max(0, Math.Min(text.Length - 1, col));
            int start = idx, end = idx;
            while (start > 0 && IsWordChar(text[start - 1])) start--;
            while (end < text.Length && IsWordChar(text[end])) end++;
            buf.SelectionStartLine = line;
            buf.SelectionStartCol = start;
            buf.CursorLine = line;
            buf.CursorCol = end;
        }

        private static bool IsWordChar(char c) => char.IsLetterOrDigit(c) || c == '_';

        public void AddCursorVertical(int deltaLine)
        {
            var buf = Buffer;
            if (buf == null) return;
            int refLine = buf.CursorLine;
            int refCol = buf.CursorCol;
            if (ExtraCursors.Count > 0)
            {
                var edge = deltaLine < 0
                    ? ExtraCursors.OrderBy(c => c.Line).First()
                    : ExtraCursors.OrderByDescending(c => c.Line).First();
                if ((deltaLine < 0 && edge.Line < refLine) || (deltaLine > 0 && edge.Line > refLine))
                {
                    refLine = edge.Line;
                    refCol = edge.Col;
                }
            }
            int targetLine = refLine + deltaLine;
            if (targetLine < 0 || targetLine >= buf.Lines.Count) return;
            // same visual column (tabs / wide characters), like VS Code's Add Cursor Above / Below
            int targetCol = EditorTextLayout.CharIndexForVisualColumn(buf.Lines[targetLine], Vcol(buf.Lines[refLine], refCol), TabSize);
            if (targetLine != buf.CursorLine && !ExtraCursors.Any(c => c.Line == targetLine))
            {
                ExtraCursors.Add(new ExtraCursorCaret
                {
                    Line = targetLine,
                    Col = targetCol,
                    AnchorLine = -1,
                    AnchorCol = -1
                });
                owner.NotifyCaretMoved();
                InvalidateVisual();
            }
        }

        // Cmd/Ctrl+D started from an empty selection searches whole words, case-sensitively (VS Code)
        private string ctrlDWholeWordNeedle;

        public void AddNextOccurrenceOrSelectWord()
        {
            var buf = Buffer;
            if (buf == null) return;
            if (!buf.HasSelection())
            {
                SelectWordAt(buf.CursorLine, buf.CursorCol);
                ctrlDWholeWordNeedle = buf.HasSelection() ? buf.GetSelectedText() : null;
                owner.NotifyCaretMoved();
                InvalidateVisual();
                return;
            }

            string needle = buf.GetSelectedText();
            if (string.IsNullOrEmpty(needle) || needle.Contains("\n")) return;
            bool whole = ctrlDWholeWordNeedle != null && needle == ctrlDWholeWordNeedle;

            int searchStartLine = ExtraCursors.Count > 0 ? ExtraCursors[ExtraCursors.Count - 1].Line : buf.CursorLine;
            int searchStartCol = ExtraCursors.Count > 0 ? ExtraCursors[ExtraCursors.Count - 1].Col : buf.CursorCol;

            for (int pass = 0; pass < 2; pass++)
            {
                int lStart = (pass == 0) ? searchStartLine : 0;
                int lEnd = (pass == 0) ? buf.Lines.Count - 1 : searchStartLine;
                for (int l = lStart; l <= lEnd; l++)
                {
                    string line = buf.Lines[l];
                    int cStart = (pass == 0 && l == searchStartLine) ? Math.Min(line.Length, searchStartCol) : 0;
                    int idx = line.IndexOf(needle, cStart, StringComparison.Ordinal);
                    while (idx >= 0)
                    {
                        int endCol = idx + needle.Length;
                        bool boundary = !whole || ((idx == 0 || !IsWordChar(line[idx - 1])) && (endCol >= line.Length || !IsWordChar(line[endCol])));
                        bool isPrimary = (l == buf.CursorLine && endCol == buf.CursorCol);
                        bool inExtra = ExtraCursors.Any(c => c.Line == l && c.Col == endCol);
                        if (boundary && isPrimary && pass == 1) return; // wrapped around: every occurrence is selected
                        if (boundary && !isPrimary && !inExtra)
                        {
                            ExtraCursors.Add(new ExtraCursorCaret
                            {
                                AnchorLine = l,
                                AnchorCol = idx,
                                Line = l,
                                Col = endCol
                            });
                            RevealLine(buf, l); // VS Code reveals the added selection
                            owner.NotifyCaretMoved();
                            InvalidateVisual();
                            return;
                        }
                        idx = line.IndexOf(needle, idx + 1, StringComparison.Ordinal);
                    }
                }
            }
        }

        /// <summary>Cmd/Ctrl+Shift+L and Cmd/Ctrl+F2 (see <see cref="ChangeAllOccurrences"/>).</summary>
        public void SelectAllFindOccurrences() => ChangeAllOccurrences();

        public void AddCursorsToLineEnds()
        {
            var buf = Buffer;
            if (buf == null) return;
            ExtraCursors.Clear();
            int startLine = buf.HasSelection() ? Math.Min(buf.SelectionStartLine, buf.CursorLine) : 0;
            int endLine = buf.HasSelection() ? Math.Max(buf.SelectionStartLine, buf.CursorLine) : buf.Lines.Count - 1;
            buf.ClearSelection();
            buf.CursorLine = startLine;
            buf.CursorCol = buf.Lines[startLine].Length;
            for (int l = startLine + 1; l <= endLine; l++)
            {
                ExtraCursors.Add(new ExtraCursorCaret
                {
                    Line = l,
                    Col = buf.Lines[l].Length,
                    AnchorLine = -1,
                    AnchorCol = -1
                });
            }
            owner.NotifyCaretMoved();
            InvalidateVisual();
        }

        public void UndoLastCursor()
        {
            if (ExtraCursors.Count > 0)
            {
                ExtraCursors.RemoveAt(ExtraCursors.Count - 1);
                owner.NotifyCaretMoved();
                InvalidateVisual();
            }
        }

        public void SkipAndAddNextOccurrence()
        {
            var buf = Buffer;
            if (buf == null) return;
            if (ExtraCursors.Count > 0)
            {
                AddNextOccurrenceOrSelectWord();
                if (ExtraCursors.Count >= 2)
                    ExtraCursors.RemoveAt(ExtraCursors.Count - 2);
            }
            else if (buf.HasSelection())
            {
                string needle = buf.GetSelectedText();
                AddNextOccurrenceOrSelectWord();
                if (ExtraCursors.Count > 0)
                {
                    var last = ExtraCursors[ExtraCursors.Count - 1];
                    ExtraCursors.RemoveAt(ExtraCursors.Count - 1);
                    buf.SelectionStartLine = last.AnchorLine >= 0 ? last.AnchorLine : last.Line;
                    buf.SelectionStartCol = last.AnchorCol >= 0 ? last.AnchorCol : Math.Max(0, last.Col - needle.Length);
                    buf.CursorLine = last.Line;
                    buf.CursorCol = last.Col;
                }
            }
            else
            {
                AddNextOccurrenceOrSelectWord();
            }
            owner.NotifyCaretMoved();
            InvalidateVisual();
        }

        public void SelectCurrentLine()
        {
            var buf = Buffer;
            if (buf == null) return;
            if (!buf.HasSelection())
            {
                buf.SelectionStartLine = buf.CursorLine;
                buf.SelectionStartCol = 0;
                if (buf.CursorLine + 1 < buf.Lines.Count)
                {
                    buf.CursorLine++;
                    buf.CursorCol = 0;
                }
                else
                {
                    buf.CursorCol = buf.Lines[buf.CursorLine].Length;
                }
            }
            else
            {
                if (buf.CursorLine + 1 < buf.Lines.Count)
                {
                    buf.CursorLine++;
                    buf.CursorCol = 0;
                }
                else
                {
                    buf.CursorCol = buf.Lines[buf.CursorLine].Length;
                }
            }
            owner.NotifyCaretMoved();
            InvalidateVisual();
        }

        public void ExpandSelection()
        {
            var buf = Buffer;
            if (buf == null) return;
            if (!buf.HasSelection())
            {
                SelectWordAt(buf.CursorLine, buf.CursorCol);
            }
            else if (buf.SelectionStartLine == buf.CursorLine &&
                     (buf.SelectionStartCol > 0 || buf.CursorCol < buf.Lines[buf.CursorLine].Length))
            {
                buf.SelectionStartCol = 0;
                buf.CursorCol = buf.Lines[buf.CursorLine].Length;
            }
            else
            {
                buf.SelectAll();
            }
            owner.NotifyCaretMoved();
            InvalidateVisual();
        }

        public void ShrinkSelection()
        {
            var buf = Buffer;
            if (buf == null) return;
            if (!buf.HasSelection()) return;
            if (buf.SelectionStartLine != buf.CursorLine)
            {
                buf.SelectionStartLine = buf.CursorLine;
                buf.SelectionStartCol = 0;
                buf.CursorCol = buf.Lines[buf.CursorLine].Length;
            }
            else if (buf.SelectionStartCol == 0 && buf.CursorCol == buf.Lines[buf.CursorLine].Length)
            {
                SelectWordAt(buf.CursorLine, Math.Min(buf.Lines[buf.CursorLine].Length, EditorIndentation.LeadingWhitespaceLength(buf.Lines[buf.CursorLine])));
            }
            else
            {
                buf.ClearSelection();
            }
            owner.NotifyCaretMoved();
            InvalidateVisual();
        }

        private void ClampExtraCursors(EditorTabBuffer buf)
        {
            if (ExtraCursors.Count == 0 || buf == null) return;
            ExtraCursors.RemoveAll(c => c.Line < 0 || c.Line >= buf.Lines.Count);
        }

        /// <summary>Cmd+Backspace / Cmd+Delete / Ctrl+K on macOS: delete from the caret to line start or end.</summary>
        private void DeleteToLineBoundary(EditorTabBuffer buf, bool toEnd)
        {
            if (buf == null) return;
            if (buf.HasSelection())
            {
                buf.PushUndoSnapshot();
                buf.DeleteSelection();
                FinalizeBufferEdit(buf);
                return;
            }
            string line = buf.Lines[buf.CursorLine];
            int col = Math.Max(0, Math.Min(line.Length, buf.CursorCol));
            if (toEnd && col == line.Length) { buf.PushUndoSnapshot(); buf.PerformDelete(); FinalizeBufferEdit(buf); return; } // join next line
            if (!toEnd && col == 0) { PerformBackspaceAcrossCursors(); return; }
            buf.PushUndoSnapshot();
            if (toEnd)
                buf.Lines[buf.CursorLine] = line.Substring(0, col);
            else
            {
                int indent = 0;
                while (indent < line.Length && (line[indent] == ' ' || line[indent] == '\t')) indent++;
                int stop = col > indent ? indent : 0; // like VS Code: first press keeps indentation
                buf.Lines[buf.CursorLine] = line.Substring(0, stop) + line.Substring(col);
                buf.CursorCol = stop;
            }
            buf.ClearSelection();
            buf.SyncContentFromLines();
            FinalizeBufferEdit(buf);
        }

        public void DeleteWordLeft()
        {
            var buf = Buffer;
            if (buf == null) return;
            buf.PushUndoSnapshot();
            ClampExtraCursors(buf);
            ApplyEditToAllCarets(buf, (b, k) => DeleteWordCore(b, left: true));
            FinalizeBufferEdit(buf);
        }

        public void DeleteWordRight()
        {
            var buf = Buffer;
            if (buf == null) return;
            buf.PushUndoSnapshot();
            ClampExtraCursors(buf);
            ApplyEditToAllCarets(buf, (b, k) => DeleteWordCore(b, left: false));
            FinalizeBufferEdit(buf);
        }

        private static void DeleteWordCore(EditorTabBuffer b, bool left)
        {
            if (b.HasSelection()) { b.DeleteSelection(); return; }
            string ln = b.Lines[b.CursorLine];
            int c = Math.Min(ln.Length, b.CursorCol);
            if (left)
            {
                if (c == 0) { b.PerformBackspace(); return; }
                int start = c;
                while (start > 0 && !IsWordChar(ln[start - 1])) start--;
                while (start > 0 && IsWordChar(ln[start - 1])) start--;
                b.Lines[b.CursorLine] = ln.Remove(start, c - start);
                b.CursorCol = start;
            }
            else
            {
                if (c >= ln.Length) { b.PerformDelete(); return; }
                int end = c;
                while (end < ln.Length && IsWordChar(ln[end])) end++;
                while (end < ln.Length && !IsWordChar(ln[end])) end++;
                b.Lines[b.CursorLine] = ln.Remove(c, Math.Max(1, end - c));
            }
            b.SyncContentFromLines(false);
        }

        /// <summary>Scrolls by visual rows without moving the caret (Ctrl+Up/Down, Alt+PageUp/Down); animated when smooth scrolling is on.</summary>
        public void ScrollLines(int delta)
        {
            var buf = Buffer;
            if (buf == null) return;
            SmoothScrollRowsBy(buf, delta);
            InvalidateVisual();
        }

        /// <summary>Lines covered by the primary selection (a selection ending at column 0 excludes that line), or the caret line.</summary>
        private void SelectedLineRange(EditorTabBuffer buf, out int first, out int last)
        {
            if (!buf.HasSelection()) { first = last = buf.CursorLine; return; }
            buf.GetNormalizedSelection(out int sl, out int sc, out int el, out int ec);
            first = sl;
            last = (el > sl && ec == 0) ? el - 1 : el;
        }

        /// <summary>Alt+Up / Alt+Down: move the selected lines (or the caret line) and keep the selection (VS Code).</summary>
        public void MoveActiveLine(int direction)
        {
            var buf = Buffer;
            if (buf == null) return;
            ExtraCursors.Clear();
            SelectedLineRange(buf, out int first, out int last);
            if (direction < 0 && first == 0) return;
            if (direction > 0 && last >= buf.Lines.Count - 1) return;
            buf.PushUndoSnapshot();
            if (direction < 0)
            {
                string above = buf.Lines[first - 1];
                buf.Lines.RemoveAt(first - 1);
                buf.Lines.Insert(last, above);
            }
            else
            {
                string below = buf.Lines[last + 1];
                buf.Lines.RemoveAt(last + 1);
                buf.Lines.Insert(first, below);
            }
            buf.CursorLine += direction;
            if (buf.HasSelection()) buf.SelectionStartLine += direction;
            FinalizeBufferEdit(buf);
        }

        /// <summary>Shift+Alt+Up / Down: duplicate the selected lines above / below, selecting the copy when going down (VS Code).</summary>
        public void CopyActiveLine(int direction)
        {
            var buf = Buffer;
            if (buf == null) return;
            ExtraCursors.Clear();
            SelectedLineRange(buf, out int first, out int last);
            buf.PushUndoSnapshot();
            var block = buf.Lines.GetRange(first, last - first + 1);
            int count = block.Count;
            buf.Lines.InsertRange(direction < 0 ? first : last + 1, block);
            if (direction > 0)
            {
                buf.CursorLine += count;
                if (buf.HasSelection()) buf.SelectionStartLine += count;
            }
            FinalizeBufferEdit(buf);
        }

        /// <summary>Cmd/Ctrl+Shift+K: delete every line touched by the selection; the caret keeps its column (VS Code).</summary>
        public void DeleteActiveLine()
        {
            var buf = Buffer;
            if (buf == null) return;
            ExtraCursors.Clear();
            SelectedLineRange(buf, out int first, out int last);
            int col = buf.CursorCol;
            buf.PushUndoSnapshot();
            if (last - first + 1 >= buf.Lines.Count)
            {
                buf.Lines.Clear();
                buf.Lines.Add("");
                buf.CursorLine = 0;
            }
            else
            {
                buf.Lines.RemoveRange(first, last - first + 1);
                buf.CursorLine = Math.Min(first, buf.Lines.Count - 1);
            }
            buf.CursorCol = Math.Min(col, buf.Lines[buf.CursorLine].Length);
            buf.ClearSelection();
            FinalizeBufferEdit(buf);
        }

        /// <summary>Toggle Line Comment (Cmd/Ctrl+/) on the lines of every caret: language tokens, smallest indentation, blank lines skipped (VS Code).</summary>
        
        public void JoinLines()
        {
            var buf = Buffer;
            if (buf == null || buf.Lines.Count <= 1) return;
            buf.PushUndoSnapshot();
            ClampExtraCursors(buf);
            ApplyEditToAllCarets(buf, (b, k) => {
                b.GetNormalizedSelection(out int startLine, out int startCol, out int endLine, out int endCol);
                if (startLine == endLine && startLine < b.Lines.Count - 1) {
                    endLine = startLine + 1;
                }
                if (startLine == endLine) return;
                
                string newText = b.Lines[startLine];
                for (int i = startLine + 1; i <= endLine; i++) {
                    string l = b.Lines[i].TrimStart();
                    newText += (newText.EndsWith(" ") || newText.Length == 0 ? "" : " ") + l;
                }
                
                b.SelectionAnchorLine = startLine;
                b.SelectionAnchorCol = 0;
                b.CursorLine = endLine;
                b.CursorCol = b.Lines[endLine].Length;
                
                b.DeleteSelection();
                b.InsertTextAtCursor(newText);
            });
            FinalizeBufferEdit(buf);
        }

        public void TransformToUppercase()
        {
            var buf = Buffer;
            if (buf == null) return;
            buf.PushUndoSnapshot();
            ClampExtraCursors(buf);
            ApplyEditToAllCarets(buf, (b, k) => {
                if (!b.HasSelection()) return;
                string text = b.GetSelectedText();
                b.DeleteSelection();
                b.InsertTextAtCursor(text.ToUpperInvariant());
            });
            FinalizeBufferEdit(buf);
        }

        public void TransformToLowercase()
        {
            var buf = Buffer;
            if (buf == null) return;
            buf.PushUndoSnapshot();
            ClampExtraCursors(buf);
            ApplyEditToAllCarets(buf, (b, k) => {
                if (!b.HasSelection()) return;
                string text = b.GetSelectedText();
                b.DeleteSelection();
                b.InsertTextAtCursor(text.ToLowerInvariant());
            });
            FinalizeBufferEdit(buf);
        }

        public void TrimTrailingWhitespace()
        {
            var buf = Buffer;
            if (buf == null) return;
            buf.PushUndoSnapshot();
            for (int i = 0; i < buf.Lines.Count; i++) {
                string l = buf.Lines[i];
                int end = l.Length - 1;
                while (end >= 0 && char.IsWhiteSpace(l[end])) end--;
                if (end < l.Length - 1) {
                    buf.Lines[i] = l.Substring(0, end + 1);
                }
            }
            buf.SyncContentFromLines(false);
            FinalizeBufferEdit(buf);
        }

        public void ToggleLineComment()
        {
            var buf = Buffer;
            if (buf == null) return;
            ClampExtraCursors(buf);
            var ranges = new List<int[]>();
            void AddRange(int al, int ac, int cl, int cc, bool hasSel)
            {
                int a = hasSel ? Math.Min(al, cl) : cl, b = hasSel ? Math.Max(al, cl) : cl;
                if (hasSel && b > a)
                {
                    int endCol = (cl > al || (cl == al && cc >= ac)) ? cc : ac;
                    if (endCol == 0) b--; // a selection ending at column 0 does not include that line
                }
                ranges.Add(new[] { a, b });
            }
            AddRange(buf.SelectionStartLine, buf.SelectionStartCol, buf.CursorLine, buf.CursorCol, buf.HasSelection());
            foreach (var ec in ExtraCursors) AddRange(ec.AnchorLine, ec.AnchorCol, ec.Line, ec.Col, ec.HasSelection);
            buf.PushUndoSnapshot();
            var edits = EditorComments.ToggleLineComments(buf, ranges);
            if (edits == null || edits.Count == 0) return;
            for (int i = 0; i < ExtraCursors.Count; i++)
            {
                var ec = ExtraCursors[i];
                ec.Col = EditorComments.AdjustColumn(edits, ec.Line, ec.Col);
                if (ec.HasSelection) ec.AnchorCol = EditorComments.AdjustColumn(edits, ec.AnchorLine, ec.AnchorCol);
                ExtraCursors[i] = ec;
            }
            FinalizeBufferEdit(buf);
        }

        /// <summary>Toggle Block Comment (Shift+Alt+A): language block tokens around the selection, or an empty block at the caret.</summary>
        public void ToggleBlockComment()
        {
            var buf = Buffer;
            if (buf == null) return;
            buf.PushUndoSnapshot();
            if (!EditorComments.ToggleBlockComment(buf)) return;
            ExtraCursors.Clear();
            FinalizeBufferEdit(buf);
        }

        /// <summary>
        /// Indent / outdent the lines of every selection (Tab with a multi-line selection, Shift+Tab, Cmd/Ctrl+] / [):
        /// one level of the buffer's indentation (tab size, spaces or tabs), normalized to tab stops like VS Code;
        /// blank lines are not indented when several lines are selected; selections and carets follow the text.
        /// </summary>
        public void IndentOrOutdentLines(bool outdent)
        {
            var buf = Buffer;
            if (buf == null) return;
            ClampExtraCursors(buf);
            int tab = TabSize;
            bool spaces = InsertSpaces;
            var carets = new List<CaretState> { ReadPrimary(buf) };
            foreach (var ec in ExtraCursors)
                carets.Add(new CaretState { Line = ec.Line, Col = ec.Col, AnchorLine = ec.HasSelection ? ec.AnchorLine : -1, AnchorCol = ec.HasSelection ? ec.AnchorCol : -1 });
            var lineSet = new SortedSet<int>();
            foreach (var c in carets)
            {
                int a = c.AnchorLine >= 0 ? c.AnchorLine : c.Line;
                int l0 = Math.Min(a, c.Line), l1 = Math.Max(a, c.Line);
                // a selection ending at column 0 of a later line does not include that line (VS Code)
                if (c.AnchorLine >= 0 && l1 > l0)
                {
                    int endCol = c.Line > a ? c.Col : c.AnchorCol;
                    if (endCol == 0) l1--;
                }
                for (int l = l0; l <= l1; l++) lineSet.Add(l);
            }
            bool multi = lineSet.Count > 1;
            var deltas = new Dictionary<int, int>();
            var newWs = new Dictionary<int, int>();
            var newLines = new Dictionary<int, string>();
            foreach (int l in lineSet)
            {
                string ln = buf.Lines[l] ?? "";
                if (!outdent && multi && ln.Trim().Length == 0) continue;
                string nl = outdent ? EditorIndentation.OutdentLine(ln, tab, spaces) : EditorIndentation.IndentLine(ln, tab, spaces);
                if (nl == ln) continue;
                newLines[l] = nl;
                int ws = EditorIndentation.LeadingWhitespaceLength(nl);
                newWs[l] = ws;
                deltas[l] = ws - EditorIndentation.LeadingWhitespaceLength(ln);
            }
            if (newLines.Count == 0) return;
            buf.PushUndoSnapshot();
            foreach (var kv in newLines) buf.Lines[kv.Key] = kv.Value;
            int ShiftCol(int line, int col)
            {
                if (!deltas.TryGetValue(line, out int d)) return col;
                int oldWs = newWs[line] - d;
                if (col >= oldWs) return Math.Max(0, col + d);
                return outdent ? Math.Min(col, newWs[line]) : col + d; // inside the indentation
            }
            var shifted = new List<CaretState>();
            foreach (var c in carets)
            {
                var n = c;
                n.Col = ShiftCol(c.Line, c.Col);
                if (c.AnchorLine >= 0)
                    n.AnchorCol = (c.AnchorCol == 0 && !outdent) ? 0 : ShiftCol(c.AnchorLine, c.AnchorCol); // a selection from column 0 keeps starting there
                shifted.Add(n);
            }
            WritePrimary(buf, shifted[0]);
            ExtraCursors.Clear();
            for (int i = 1; i < shifted.Count; i++)
                ExtraCursors.Add(new ExtraCursorCaret { Line = shifted[i].Line, Col = shifted[i].Col, AnchorLine = shifted[i].AnchorLine, AnchorCol = shifted[i].AnchorCol });
            FinalizeBufferEdit(buf);
        }

        public void JumpToMatchingBracket()
        {
            var buf = Buffer;
            if (buf == null) return;
            if (TryFindMatchingBracket(buf, buf.CursorLine, buf.CursorCol, out int mLine, out int mCol))
            {
                buf.CursorLine = mLine;
                buf.CursorCol = mCol;
                buf.ClearSelection();
                EnsureCursorVisible();
                owner.NotifyCaretMoved();
                InvalidateVisual();
            }
        }

        public static bool TryFindMatchingBracket(EditorTabBuffer buf, int line, int col, out int matchLine, out int matchCol)
        {
            matchLine = -1;
            matchCol = -1;
            if (buf == null || line < 0 || line >= buf.Lines.Count) return false;
            string curLine = buf.Lines[line];
            if (curLine.Length == 0) return false;

            int checkCol = -1;
            char ch = '\0';
            if (col < curLine.Length && IsBracketChar(curLine[col]))
            {
                checkCol = col;
                ch = curLine[col];
            }
            else if (col > 0 && col - 1 < curLine.Length && IsBracketChar(curLine[col - 1]))
            {
                checkCol = col - 1;
                ch = curLine[col - 1];
            }
            if (checkCol < 0) return false;

            bool forward = (ch == '(' || ch == '{' || ch == '[');
            char target = ch == '(' ? ')' : ch == ')' ? '(' : ch == '{' ? '}' : ch == '}' ? '{' : ch == '[' ? ']' : '[';
            int depth = 0;

            if (forward)
            {
                int maxForwardLine = Math.Min(buf.Lines.Count - 1, line + 1500);
                for (int l = line; l <= maxForwardLine; l++)
                {
                    string ln = buf.Lines[l];
                    if (ln.Length > 4000) continue;
                    int cStart = (l == line) ? checkCol : 0;
                    for (int c = cStart; c < ln.Length; c++)
                    {
                        if (ln[c] == ch) depth++;
                        else if (ln[c] == target)
                        {
                            depth--;
                            if (depth == 0) { matchLine = l; matchCol = c; return true; }
                        }
                    }
                }
            }
            else
            {
                int minBackwardLine = Math.Max(0, line - 1500);
                for (int l = line; l >= minBackwardLine; l--)
                {
                    string ln = buf.Lines[l];
                    if (ln.Length > 4000) continue;
                    int cStart = (l == line) ? checkCol : ln.Length - 1;
                    for (int c = cStart; c >= 0; c--)
                    {
                        if (ln[c] == ch) depth++;
                        else if (ln[c] == target)
                        {
                            depth--;
                            if (depth == 0) { matchLine = l; matchCol = c; return true; }
                        }
                    }
                }
            }
            return false;
        }

        private static bool IsBracketChar(char c) => c == '(' || c == ')' || c == '{' || c == '}' || c == '[' || c == ']';

        public void ToggleWordWrap()
        {
            var buf = Buffer;
            if (buf == null) return;
            buf.WordWrap = !buf.WordWrap;
            buf.ScrollLeftPx = 0;
            buf.ScrollTopWrapRow = 0;
            affinityLine = -1;
            EnsureCursorVisible();
            InvalidateVisual();
        }

        public void AcceptGhostText(bool singleWordOnly = false)
        {
            var buf = Buffer;
            if (buf == null || string.IsNullOrEmpty(buf.GhostTextSuggestion)) return;
            buf.PushUndoSnapshot();
            string suggestion = buf.GhostTextSuggestion;
            if (singleWordOnly)
            {
                int nextBreak = 0;
                while (nextBreak < suggestion.Length && char.IsWhiteSpace(suggestion[nextBreak])) nextBreak++;
                while (nextBreak < suggestion.Length && !char.IsWhiteSpace(suggestion[nextBreak])) nextBreak++;
                string word = suggestion.Substring(0, Math.Max(1, nextBreak));
                buf.InsertTextAtCursor(word);
                buf.GhostTextSuggestion = suggestion.Substring(word.Length);
            }
            else
            {
                buf.InsertTextAtCursor(suggestion);
                buf.GhostTextSuggestion = "";
            }
            FinalizeBufferEdit(buf);
        }

        /// <summary>Finishes an edit made directly on the buffer (LSP completion / rename / format): re-sync, analyze, notify, redraw.</summary>
        public void CommitExternalEdit()
        {
            var buf = Buffer;
            if (buf == null) return;
            ExtraCursors.Clear();
            FinalizeBufferEdit(buf);
        }

        /// <summary>Documents above this many lines get the AST pass / gutter diff debounced (MainWindow) instead of per keystroke.</summary>
        public const int DeferAnalysisLineThreshold = 250;

        private void FinalizeBufferEdit(EditorTabBuffer buf)
        {
            if (buf == null) return;
            // O(edited lines): Lines stay the source of truth, the full text is rebuilt lazily when someone needs it.
            bool deferHeavyAnalysis = buf.Lines != null && buf.Lines.Count > DeferAnalysisLineThreshold;
            buf.SyncContentFromLines(recomputeGutterDiff: !deferHeavyAnalysis);
            buf.IsDirty = true; // like VS Code: any edit dirties the editor (EOL / encoding changes included); undo back to the saved text cleans it
            if (!deferHeavyAnalysis && !buf.IsLargeFile)
            {
                LlmHarnessAndAgentEngine.AnalyzeBufferAstAndDiagnostics(buf);
            }
            preferredCaretVcol = -1;
            boxAnchorLine = -1;
            EnsureCursorVisible();
            linkHover = null;
            linkHoverLine = -1;
            owner.NotifyBufferModified();
            owner.NotifyCaretMoved();
            InvalidateVisual();
        }

        // ---- Multi-cursor engine --------------------------------------------------------------------------
        // Every edit / movement applies to all carets (primary + ExtraCursors). Edits run from the last caret to
        // the first with one undo snapshot; carets already processed are tracked as absolute offsets and shifted
        // by each edit's length delta, so line splits / joins never misplace them.
        private struct CaretState { public int Line, Col, AnchorLine, AnchorCol; }

        private static CaretState ReadPrimary(EditorTabBuffer b) => new CaretState
        {
            Line = b.CursorLine, Col = b.CursorCol,
            AnchorLine = b.HasSelection() ? b.SelectionStartLine : -1,
            AnchorCol = b.HasSelection() ? b.SelectionStartCol : -1
        };

        private static void WritePrimary(EditorTabBuffer b, CaretState c)
        {
            b.CursorLine = Math.Max(0, Math.Min(b.Lines.Count - 1, c.Line));
            b.CursorCol = Math.Max(0, Math.Min(b.Lines[b.CursorLine].Length, c.Col));
            if (c.AnchorLine >= 0 && c.AnchorCol >= 0)
            {
                b.SelectionStartLine = Math.Max(0, Math.Min(b.Lines.Count - 1, c.AnchorLine));
                b.SelectionStartCol = Math.Max(0, Math.Min(b.Lines[b.SelectionStartLine].Length, c.AnchorCol));
            }
            else b.ClearSelection();
        }

        private static long OffsetOf(EditorTabBuffer b, int line, int col)
        {
            long off = 0;
            int l = Math.Max(0, Math.Min(b.Lines.Count - 1, line));
            for (int i = 0; i < l; i++) off += b.Lines[i].Length + 1;
            return off + Math.Max(0, Math.Min(b.Lines[l].Length, col));
        }

        private static void PositionOf(EditorTabBuffer b, long offset, out int line, out int col)
        {
            long rem = Math.Max(0, offset);
            for (int i = 0; i < b.Lines.Count; i++)
            {
                int len = b.Lines[i].Length;
                if (rem <= len || i == b.Lines.Count - 1) { line = i; col = (int)Math.Min(rem, len); return; }
                rem -= len + 1;
            }
            line = 0; col = 0;
        }

        private static long TotalLength(EditorTabBuffer b)
        {
            long t = Math.Max(0, b.Lines.Count - 1);
            for (int i = 0; i < b.Lines.Count; i++) t += b.Lines[i].Length;
            return t;
        }

        /// <summary>Runs a single-caret edit at every caret (bottom-to-top). The caller pushes the undo snapshot.</summary>
        private void ApplyEditToAllCarets(EditorTabBuffer buf, Action<EditorTabBuffer, int> edit)
        {
            if (ExtraCursors.Count == 0) { edit(buf, 0); return; }
            var carets = new List<CaretState> { ReadPrimary(buf) };
            foreach (var ec in ExtraCursors)
                if (ec.Line >= 0 && ec.Line < buf.Lines.Count)
                    carets.Add(new CaretState { Line = ec.Line, Col = ec.Col, AnchorLine = ec.HasSelection ? ec.AnchorLine : -1, AnchorCol = ec.HasSelection ? ec.AnchorCol : -1 });
            // sort by selection end (descending), so each edit only shifts carets that were already processed
            var ordered = carets.Select((c, i) => new
            {
                c, i,
                end = Math.Max(OffsetOf(buf, c.Line, c.Col), c.AnchorLine >= 0 ? OffsetOf(buf, c.AnchorLine, c.AnchorCol) : 0)
            }).OrderByDescending(x => x.end).ToList();
            var done = new List<KeyValuePair<int, long>>(); // original caret index -> final offset
            int k = 0;
            foreach (var item in ordered)
            {
                WritePrimary(buf, item.c);
                long before = TotalLength(buf);
                edit(buf, k++);
                long delta = TotalLength(buf) - before;
                for (int d = 0; d < done.Count; d++) done[d] = new KeyValuePair<int, long>(done[d].Key, done[d].Value + delta);
                done.Add(new KeyValuePair<int, long>(item.i, OffsetOf(buf, buf.CursorLine, buf.CursorCol)));
            }
            // restore: index 0 is the primary caret
            ExtraCursors.Clear();
            var seen = new HashSet<long>();
            foreach (var kv in done.OrderBy(x => x.Key))
            {
                PositionOf(buf, kv.Value, out int l, out int c);
                if (kv.Key == 0) { buf.CursorLine = l; buf.CursorCol = c; buf.ClearSelection(); seen.Add(kv.Value); }
            }
            foreach (var kv in done.OrderBy(x => x.Key))
            {
                if (kv.Key == 0 || !seen.Add(kv.Value)) continue; // merge carets that collapsed onto each other
                PositionOf(buf, kv.Value, out int l, out int c);
                ExtraCursors.Add(new ExtraCursorCaret { Line = l, Col = c, AnchorLine = -1, AnchorCol = -1 });
            }
        }

        /// <summary>Applies a caret motion to every caret (the motion acts on the buffer's primary caret).</summary>
        private void ApplyMoveToAllCarets(Action move)
        {
            var buf = Buffer;
            if (buf == null) return;
            // the sticky Up/Down column of every caret survives only motions that come from Up/Down themselves
            nextPreferredVcolByPos = new Dictionary<long, int>();
            boxAnchorLine = -1;
            multiAutoClosed.Clear();
            buf.LastTypingUndoTick = 0; // moving the caret ends the current typing undo group (VS Code)
            try
            {
                if (ExtraCursors.Count == 0) { move(); return; }
                var primary = ReadPrimary(buf);
                var moved = new List<ExtraCursorCaret>();
                foreach (var ec in ExtraCursors.ToList())
                {
                    if (ec.Line < 0 || ec.Line >= buf.Lines.Count) continue;
                    WritePrimary(buf, new CaretState { Line = ec.Line, Col = ec.Col, AnchorLine = ec.HasSelection ? ec.AnchorLine : -1, AnchorCol = ec.HasSelection ? ec.AnchorCol : -1 });
                    movingExtraCaret = true;
                    move();
                    movingExtraCaret = false;
                    var st = ReadPrimary(buf);
                    moved.Add(new ExtraCursorCaret { Line = st.Line, Col = st.Col, AnchorLine = st.AnchorLine, AnchorCol = st.AnchorCol });
                }
                WritePrimary(buf, primary);
                move();
                ExtraCursors.Clear();
                foreach (var m in moved)
                {
                    bool dup = (m.Line == buf.CursorLine && m.Col == buf.CursorCol) || ExtraCursors.Any(x => x.Line == m.Line && x.Col == m.Col);
                    if (!dup) ExtraCursors.Add(m);
                }
            }
            finally
            {
                movingExtraCaret = false;
                preferredVcolByPos = nextPreferredVcolByPos ?? new Dictionary<long, int>();
                nextPreferredVcolByPos = null;
            }
        }

        private bool movingExtraCaret;

        private void InsertTextAcrossCursors(string text)
        {
            var buf = Buffer;
            if (buf == null || string.IsNullOrEmpty(text)) return;
            // VS Code undo grouping: consecutive typed characters (one or many carets) form one step; a keystroke that
            // replaces selections starts a new step that the following characters join
            bool typedChar = text.Length == 1 && text[0] != '\n' && text[0] != '\r';
            bool replacing = buf.HasSelection() || ExtraCursors.Any(c => c.HasSelection);
            buf.PushUndoSnapshot(coalesceTyping: typedChar && !replacing);
            if (typedChar && replacing) { buf.LastTypingUndoTick = Environment.TickCount; buf.LastTypingUndoLine = buf.CursorLine; }
            buf.GhostTextSuggestion = "";
            multiAutoClosed.Clear();
            ClampExtraCursors(buf);
            if (ExtraCursors.Count == 0)
            {
                buf.InsertTextAtCursor(text);
                FinalizeBufferEdit(buf);
                return;
            }
            // VS Code: pasting N lines with N carets puts one line at each caret (in document order).
            string norm = text.Replace("\r\n", "\n").Replace("\r", "\n");
            string[] pieces = norm.TrimEnd('\n').Split('\n');
            int caretCount = ExtraCursors.Count + 1;
            bool distribute = pieces.Length == caretCount && pieces.Length > 1;
            int total = caretCount;
            ApplyEditToAllCarets(buf, (b, k) =>
            {
                // k counts from the LAST caret in document order, so the matching line is pieces[total-1-k]
                b.InsertTextAtCursor(distribute ? pieces[total - 1 - k] : norm);
            });
            FinalizeBufferEdit(buf);
        }

        private void PerformBackspaceAcrossCursors()
        {
            var buf = Buffer;
            if (buf == null) return;
            bool coalesce = ExtraCursors.Count == 0 && !buf.HasSelection() && buf.CursorCol > 0;
            buf.PushUndoSnapshot(coalesceTyping: coalesce);
            ClampExtraCursors(buf);
            var linked = MaybeBeginLinkedTagEdit(buf);
            int tab = TabSize;
            int lineBefore = buf.CursorLine, lenBefore = buf.Lines[buf.CursorLine].Length, countBefore = buf.Lines.Count;
            if (ExtraCursors.Count > 0 || buf.HasSelection()) autoClosed.Clear();
            if (ExtraCursors.Count > 0) BackspaceAtAllCarets(buf); // shared with WinForms: empty auto-closed pairs at every caret
            else ApplyEditToAllCarets(buf, (b, k) => BackspaceAtCaret(b, tab));
            if (autoClosed.Count > 0)
            {
                if (buf.Lines.Count != countBefore || buf.CursorLine != lineBefore) autoClosed.Clear();
                else ShiftAutoClosed(buf.CursorLine, buf.CursorCol, buf.Lines[buf.CursorLine].Length - lenBefore);
            }
            CompleteLinkedTagEdit(buf, linked);
            FinalizeBufferEdit(buf);
        }

        private void PerformDeleteAcrossCursors()
        {
            var buf = Buffer;
            if (buf == null) return;
            buf.PushUndoSnapshot();
            multiAutoClosed.Clear();
            ClampExtraCursors(buf);
            var linked = MaybeBeginLinkedTagEdit(buf);
            ApplyEditToAllCarets(buf, (b, k) => b.PerformDelete());
            CompleteLinkedTagEdit(buf, linked);
            FinalizeBufferEdit(buf);
        }

        protected override async void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            var buf = Buffer;
            if (buf == null) return;

            // While an IME composition is active, Enter / Backspace / arrows belong to the input method.
            if (IsImeComposing) return;
            // Language-server popups (completion list, signature help) get first look at navigation keys.
            if (PreviewKeyHandler != null && PreviewKeyHandler(e)) { e.Handled = true; return; }
            if (SnippetKeyHandler != null && SnippetKeyHandler(e)) { e.Handled = true; return; }
            bool ctrl = e.KeyModifiers.HasFlag(KeyModifiers.Control);
            bool meta = e.KeyModifiers.HasFlag(KeyModifiers.Meta);
            // Cmd on macOS, Ctrl on Windows/Linux (plain Ctrl on macOS is reserved for Cocoa/emacs editing keys).
            bool ctrlOrCmd = MacKeymap.IsPrimary(e.KeyModifiers);
            bool shift = e.KeyModifiers.HasFlag(KeyModifiers.Shift);
            bool alt = e.KeyModifiers.HasFlag(KeyModifiers.Alt);
            if (IsModifierKey(e.Key))
            {
                // Pressing Cmd / Ctrl over a link underlines it immediately, without a pointer move.
                UpdateLinkHover(lastHoverPt, e.KeyModifiers);
                return;
            }
            if (linkHover != null) ClearLinkHover();
            suppressTextInputAfterShortcut = false;
            // find option toggles work while the editor has focus too (VS Code: Alt+C/W/R/P/L, Cmd+Option+... on macOS)
            if (owner.TryHandleFindOptionKey(e)) return;
            if (!ctrlOrCmd && (e.Key == Key.Back || e.Key == Key.Delete || e.Key == Key.Enter || e.Key == Key.Tab))
                HidePointerWhileTyping();
            ClampExtraCursors(buf);

            // macOS Cocoa text-system / VS Code emacs bindings (Ctrl, not Cmd)
            if (MacKeymap.IsMacOS && ctrl && !meta && !alt && !ctrlOrCmd)
            {
                bool handledEmacs = true;
                switch (e.Key)
                {
                    case Key.A: MoveCaretToLineBoundary(false, shift); break;
                    case Key.E: MoveCaretToLineBoundary(true, shift); break;
                    case Key.F: MoveCaretHorizontal(1, shift, false); break;
                    case Key.B: MoveCaretHorizontal(-1, shift, false); break;
                    case Key.N: MoveCaretVertical(1, shift); break;
                    case Key.P: MoveCaretVertical(-1, shift); break;
                    case Key.H: PerformBackspaceAcrossCursors(); break;
                    case Key.D: buf.PushUndoSnapshot(); buf.PerformDelete(); FinalizeBufferEdit(buf); break;
                    case Key.K: DeleteToLineBoundary(buf, toEnd: true); break;
                    default: handledEmacs = false; break;
                }
                if (handledEmacs)
                {
                    EnsureCursorVisible();
                    InvalidateVisual();
                    e.Handled = true;
                    return;
                }
            }

            // Ghost text acceptance
            if (!string.IsNullOrEmpty(buf.GhostTextSuggestion))
            {
                if (e.Key == Key.Tab && !shift && !ctrl && !meta && !alt)
                {
                    AcceptGhostText(singleWordOnly: false);
                    e.Handled = true;
                    return;
                }
                if (e.Key == Key.Right && ctrlOrCmd && !shift)
                {
                    AcceptGhostText(singleWordOnly: true);
                    e.Handled = true;
                    return;
                }
                if (e.Key == Key.Escape)
                {
                    buf.GhostTextSuggestion = "";
                    InvalidateVisual();
                    e.Handled = true;
                    return;
                }
            }

            // Accept / Reject pending inline diff
            if (buf.HasPendingDiff)
            {
                if (ctrlOrCmd && e.Key == Key.Enter)
                {
                    owner.AcceptAllPendingDiffs();
                    e.Handled = true;
                    return;
                }
                if (e.Key == Key.Escape && !owner.IsFindOverlayVisible && !owner.IsInlineAiBarVisible)
                {
                    owner.RejectAllPendingDiffs();
                    e.Handled = true;
                    return;
                }
            }

            if (e.Key == Key.Escape)
            {
                if (owner.IsFindOverlayVisible) { owner.HideFindBar(); e.Handled = true; return; }
                if (owner.IsInlineAiBarVisible) { owner.HideInlineAiBar(); e.Handled = true; return; }
                if (ExtraCursors.Count > 0) { ExtraCursors.Clear(); InvalidateVisual(); e.Handled = true; return; }
                if (buf.HasSelection()) { buf.ClearSelection(); InvalidateVisual(); e.Handled = true; return; }
            }

            // Find & Replace
            if (ctrlOrCmd && !shift && !alt && e.Key == Key.F)
            {
                owner.ShowFindBar(includeReplace: false);
                e.Handled = true;
                return;
            }
            if ((ctrlOrCmd && !shift && !alt && e.Key == Key.H) || (meta && alt && e.Key == Key.F))
            {
                owner.ShowFindBar(includeReplace: true);
                e.Handled = true;
                return;
            }
            if (e.Key == Key.F3)
            {
                owner.FindNext(shift ? -1 : 1);
                e.Handled = true;
                return;
            }

            // Inline AI Edit (Ctrl/Cmd+I)
            if (ctrlOrCmd && !shift && !alt && e.Key == Key.I)
            {
                owner.ShowInlineAiBar();
                e.Handled = true;
                return;
            }

            // macOS caret navigation (VS Code mac keymap). Must run before the Windows-style
            // Shift+Alt+Arrow (expand/shrink) and Ctrl+Arrow (word jump) handlers below, which
            // would otherwise turn Cmd+Shift+Right into a word selection.
            if (MacKeymap.IsMacOS && (e.Key == Key.Left || e.Key == Key.Right || e.Key == Key.Up || e.Key == Key.Down))
            {
                bool horizontal = e.Key == Key.Left || e.Key == Key.Right;
                bool forward = e.Key == Key.Right || e.Key == Key.Down;
                if (meta && !ctrl && !alt)
                {
                    // Cmd+Left/Right -> line start (smart) / end; Cmd+Up/Down -> document start / end (+Shift selects)
                    if (horizontal) MoveCaretToLineBoundary(forward, shift);
                    else MoveCaretToDocumentBoundary(forward, shift);
                    e.Handled = true;
                    return;
                }
                if (alt && !meta && !ctrl && horizontal)
                {
                    // Option+Left/Right -> word move (+Shift selects)
                    MoveCaretHorizontal(forward ? 1 : -1, shift, true);
                    e.Handled = true;
                    return;
                }
                if (ctrl && meta && shift && !alt && horizontal)
                {
                    // Ctrl+Shift+Cmd+Left/Right -> shrink / expand selection on macOS
                    if (forward) ExpandSelection(); else ShrinkSelection();
                    e.Handled = true;
                    return;
                }
            }

            // Multi-Cursor & Selection Shortcuts
            if (ctrlOrCmd && !shift && !alt && e.Key == Key.D)
            {
                AddNextOccurrenceOrSelectWord();
                e.Handled = true;
                return;
            }
            if (ctrlOrCmd && !shift && !alt && e.Key == Key.L)
            {
                SelectCurrentLine();
                e.Handled = true;
                return;
            }
            if ((ctrlOrCmd && shift && !alt && e.Key == Key.L) || (ctrlOrCmd && !shift && !alt && e.Key == Key.F2))
            {
                SelectAllFindOccurrences();
                e.Handled = true;
                return;
            }
            if (shift && alt && !ctrlOrCmd && e.Key == Key.Right)
            {
                ExpandSelection();
                e.Handled = true;
                return;
            }
            if (shift && alt && !ctrlOrCmd && e.Key == Key.Left)
            {
                ShrinkSelection();
                e.Handled = true;
                return;
            }
            if (shift && alt && !ctrlOrCmd && e.Key == Key.I)
            {
                AddCursorsToLineEnds();
                e.Handled = true;
                return;
            }
            if (ctrlOrCmd && !shift && !alt && e.Key == Key.U)
            {
                UndoLastCursor();
                e.Handled = true;
                return;
            }
            if (ctrlOrCmd && alt && !shift && e.Key == Key.Up)
            {
                AddCursorVertical(-1);
                e.Handled = true;
                return;
            }
            if (ctrlOrCmd && alt && !shift && e.Key == Key.Down)
            {
                AddCursorVertical(1);
                e.Handled = true;
                return;
            }

            // Column Box Selection via Ctrl+Shift+Alt+Arrow
            if (ctrlOrCmd && shift && alt && (e.Key == Key.Down || e.Key == Key.Up || e.Key == Key.Right || e.Key == Key.Left))
            {
                // the primary caret is the box's active end; the anchor stays where the box started
                if (boxAnchorLine < 0 || boxAnchorLine >= buf.Lines.Count || (ExtraCursors.Count == 0 && !buf.HasSelection()))
                {
                    boxAnchorLine = buf.HasSelection() ? buf.SelectionStartLine : buf.CursorLine;
                    boxAnchorVcol = Vcol(buf.Lines[boxAnchorLine], buf.HasSelection() ? buf.SelectionStartCol : buf.CursorCol);
                }
                int endLine = buf.CursorLine;
                double endV = Vcol(buf.Lines[buf.CursorLine], buf.CursorCol);
                if (e.Key == Key.Down) endLine = Math.Min(buf.Lines.Count - 1, endLine + 1);
                else if (e.Key == Key.Up) endLine = Math.Max(0, endLine - 1);
                else if (e.Key == Key.Right) endV++;
                else if (e.Key == Key.Left) endV = Math.Max(0, endV - 1);
                ApplyColumnBoxSelectionVisual(boxAnchorLine, boxAnchorVcol, endLine, endV);
                InvalidateVisual();
                e.Handled = true;
                return;
            }

            // Line Operations
            if (alt && !ctrlOrCmd && !shift && e.Key == Key.Up)
            {
                MoveActiveLine(-1);
                e.Handled = true;
                return;
            }
            if (alt && !ctrlOrCmd && !shift && e.Key == Key.Down)
            {
                MoveActiveLine(1);
                e.Handled = true;
                return;
            }
            if (alt && shift && !ctrlOrCmd && e.Key == Key.Up)
            {
                CopyActiveLine(-1);
                e.Handled = true;
                return;
            }
            if (alt && shift && !ctrlOrCmd && e.Key == Key.Down)
            {
                CopyActiveLine(1);
                e.Handled = true;
                return;
            }
            if (ctrlOrCmd && shift && !alt && e.Key == Key.K)
            {
                DeleteActiveLine();
                e.Handled = true;
                return;
            }
            if (ctrlOrCmd && shift && !alt && e.Key == Key.D)
            {
                CopyActiveLine(1);
                e.Handled = true;
                return;
            }
            if (ctrlOrCmd && !alt && e.Key == Key.Enter)
            {
                InsertLineAboveOrBelow(above: shift); // keeps / increases the indentation like VS Code
                e.Handled = true;
                return;
            }

            // Commenting & Brackets & Folding & Word Wrap
            if (ctrlOrCmd && !shift && !alt && (e.Key == Key.OemQuestion || e.Key == Key.Divide))
            {
                ToggleLineComment();
                e.Handled = true;
                return;
            }
            if (shift && alt && !ctrlOrCmd && e.Key == Key.A)
            {
                ToggleBlockComment();
                e.Handled = true;
                return;
            }
            if (ctrlOrCmd && shift && (e.Key == Key.OemPipe || e.Key == Key.OemBackslash))
            {
                JumpToMatchingBracket();
                e.Handled = true;
                return;
            }
            if (ctrlOrCmd && shift && e.Key == Key.OemOpenBrackets)
            {
                FoldAtCaret();
                e.Handled = true;
                return;
            }
            if (ctrlOrCmd && shift && e.Key == Key.OemCloseBrackets)
            {
                UnfoldAtCaret();
                e.Handled = true;
                return;
            }
            if (ctrlOrCmd && !shift && e.Key == Key.OemOpenBrackets)
            {
                IndentOrOutdentLines(outdent: true);
                e.Handled = true;
                return;
            }
            if (ctrlOrCmd && !shift && e.Key == Key.OemCloseBrackets)
            {
                IndentOrOutdentLines(outdent: false);
                e.Handled = true;
                return;
            }
            if (IsToggleWordWrapKey(e.Key, e.PhysicalKey, e.KeyModifiers))
            {
                // ⌥Z on macOS: matched by physical key and the "Ω" text input that follows is swallowed (VS Code does both)
                ToggleWordWrap();
                SuppressNextTextInput();
                e.Handled = true;
                return;
            }

            // Undo / Redo
            if (ctrlOrCmd && !alt && e.Key == Key.Z)
            {
                PerformUndoRedo(redo: shift);
                e.Handled = true;
                return;
            }
            if (ctrlOrCmd && !shift && !alt && e.Key == Key.Y)
            {
                PerformUndoRedo(redo: true);
                e.Handled = true;
                return;
            }

            // Select All / Copy / Cut / Paste
            if (ctrlOrCmd && !shift && !alt && e.Key == Key.A)
            {
                buf.SelectAll();
                InvalidateVisual();
                e.Handled = true;
                return;
            }
            if (ctrlOrCmd && !shift && !alt && e.Key == Key.C)
            {
                e.Handled = true; // before any await: routing finishes at the first yield (else: system beep / double handling)
                string sel = GetCopyText(buf);
                var top = TopLevel.GetTopLevel(this);
                if (top?.Clipboard != null) await top.Clipboard.SetTextAsync(sel);
                return;
            }
            if (ctrlOrCmd && !shift && !alt && e.Key == Key.X)
            {
                e.Handled = true;
                string sel = GetCopyText(buf);
                CutSelectionsOrLines(buf); // edit first (synchronously), then publish to the clipboard
                var top = TopLevel.GetTopLevel(this);
                if (top?.Clipboard != null) await top.Clipboard.SetTextAsync(sel);
                return;
            }
            if (ctrlOrCmd && !shift && !alt && e.Key == Key.V)
            {
                e.Handled = true;
                var top = TopLevel.GetTopLevel(this);
                if (top?.Clipboard != null)
                {
                    string clip = await top.Clipboard.GetTextAsync();
                    if (!string.IsNullOrEmpty(clip))
                        PasteText(clip);
                }
                return;
            }

            // Standard Editing & Navigation Keys
            switch (e.Key)
            {
                case Key.Back: if (buf.IsReadOnly || buf.IsReadOnlyDiffPreview) { e.Handled = true; break; }
                    // macOS: Option+Backspace = word, Cmd+Backspace = to line start. Windows/Linux: Ctrl = word.
                    if (MacKeymap.IsMacOS && meta && !alt)
                        DeleteToLineBoundary(buf, toEnd: false);
                    else if (MacKeymap.IsMacOS ? alt : ctrlOrCmd)
                        DeleteWordLeft();
                    else
                        PerformBackspaceAcrossCursors();
                    e.Handled = true;
                    break;
                case Key.Delete: if (buf.IsReadOnly || buf.IsReadOnlyDiffPreview) { e.Handled = true; break; }
                    if (MacKeymap.IsMacOS && meta && !alt)
                    {
                        DeleteToLineBoundary(buf, toEnd: true);
                    }
                    else if (MacKeymap.IsMacOS ? alt : ctrlOrCmd)
                    {
                        DeleteWordRight();
                    }
                    else
                    {
                        PerformDeleteAcrossCursors();
                    }
                    e.Handled = true;
                    break;
                case Key.Enter: if (buf.IsReadOnly || buf.IsReadOnlyDiffPreview) { e.Handled = true; break; }
                    if (!ctrlOrCmd)
                    {
                        PerformNewlineAcrossCursors();
                        e.Handled = true;
                    }
                    break;
                case Key.Tab: if (buf.IsReadOnly || buf.IsReadOnlyDiffPreview) { e.Handled = true; break; }
                    if (ctrl || meta) break; // Ctrl+Tab / Cmd+Tab switch editors (workbench), never indent
                    if (!shift && !ctrlOrCmd && !alt && TryHandleNicetyTab(buf)) { e.Handled = true; break; } // Emmet
                    HandleTabKey(shift); // honours the buffer's tab size / insert spaces (detected, .editorconfig, settings)
                    e.Handled = true;
                    break;
                case Key.Left:
                    MoveCaretHorizontal(-1, shift, ctrlOrCmd);
                    e.Handled = true;
                    break;
                case Key.Right:
                    MoveCaretHorizontal(1, shift, ctrlOrCmd);
                    e.Handled = true;
                    break;
                case Key.Up:
                    if (ctrl && !meta && !alt && !shift)
                    {
                        ScrollLines(-1);
                        e.Handled = true;
                    }
                    else if (!alt && !ctrlOrCmd)
                    {
                        MoveCaretVertical(-1, shift);
                        e.Handled = true;
                    }
                    break;
                case Key.Down:
                    if (ctrl && !meta && !alt && !shift)
                    {
                        ScrollLines(1);
                        e.Handled = true;
                    }
                    else if (!alt && !ctrlOrCmd)
                    {
                        MoveCaretVertical(1, shift);
                        e.Handled = true;
                    }
                    break;
                case Key.Home:
                    if (ctrlOrCmd) MoveCaretToDocumentBoundary(false, shift);
                    else MoveCaretToLineBoundary(false, shift);
                    e.Handled = true;
                    break;
                case Key.End:
                    if (ctrlOrCmd) MoveCaretToDocumentBoundary(true, shift);
                    else MoveCaretToLineBoundary(true, shift);
                    e.Handled = true;
                    break;
                case Key.PageUp:
                    if (alt && !ctrlOrCmd && !shift)
                    {
                        ScrollLines(-Math.Max(5, (int)(Bounds.Height / LineHeight) - 2));
                        e.Handled = true;
                    }
                    else if (!ctrlOrCmd)
                    {
                        MoveCaretVertical(-Math.Max(5, (int)(Bounds.Height / LineHeight) - 2), shift);
                        e.Handled = true;
                    }
                    break;
                case Key.PageDown:
                    if (alt && !ctrlOrCmd && !shift)
                    {
                        ScrollLines(Math.Max(5, (int)(Bounds.Height / LineHeight) - 2));
                        e.Handled = true;
                    }
                    else if (!ctrlOrCmd)
                    {
                        MoveCaretVertical(Math.Max(5, (int)(Bounds.Height / LineHeight) - 2), shift);
                        e.Handled = true;
                    }
                    break;
            }
        }

        private void SetSelectionAnchorIfNeeded(bool shift)
        {
            var buf = Buffer;
            if (buf == null) return;
            if (shift)
            {
                if (!buf.HasSelection())
                {
                    buf.SelectionStartLine = buf.CursorLine;
                    buf.SelectionStartCol = buf.CursorCol;
                }
            }
            else
            {
                buf.ClearSelection();
            }
        }

        private void MoveCaretHorizontal(int dir, bool shift, bool wordJump) => ApplyMoveToAllCarets(() => MoveCaretHorizontalCore(dir, shift, wordJump));

        private void MoveCaretHorizontalCore(int dir, bool shift, bool wordJump)
        {
            var buf = Buffer;
            if (buf == null) return;
            if (!movingExtraCaret) affinityLine = -1;
            // VS Code: Left / Right without Shift collapse a selection to its start / end without moving further.
            if (!shift && !wordJump && buf.HasSelection())
            {
                buf.GetNormalizedSelection(out int sl, out int sc, out int el, out int ec);
                if (dir < 0) { buf.CursorLine = sl; buf.CursorCol = sc; } else { buf.CursorLine = el; buf.CursorCol = ec; }
                buf.ClearSelection();
                if (!movingExtraCaret) EnsureCursorVisible();
                owner.NotifyCaretMoved();
                InvalidateVisual();
                return;
            }
            SetSelectionAnchorIfNeeded(shift);
            string line = buf.Lines[buf.CursorLine];
            if (dir < 0)
            {
                if (buf.CursorCol > 0)
                {
                    if (wordJump)
                    {
                        int c = buf.CursorCol - 1;
                        while (c > 0 && !IsWordChar(line[c - 1])) c--;
                        while (c > 0 && IsWordChar(line[c - 1])) c--;
                        buf.CursorCol = c;
                    }
                    else
                    {
                        buf.CursorCol--;
                        if (buf.CursorCol > 0 && char.IsLowSurrogate(line[buf.CursorCol]) && char.IsHighSurrogate(line[buf.CursorCol - 1])) buf.CursorCol--;
                    }
                }
                else if (buf.CursorLine > 0)
                {
                    buf.CursorLine--;
                    buf.CursorCol = buf.Lines[buf.CursorLine].Length;
                }
            }
            else
            {
                if (buf.CursorCol < line.Length)
                {
                    if (wordJump)
                    {
                        int c = buf.CursorCol + 1;
                        while (c < line.Length && IsWordChar(line[c])) c++;
                        while (c < line.Length && !IsWordChar(line[c])) c++;
                        buf.CursorCol = c;
                    }
                    else
                    {
                        buf.CursorCol++;
                        if (buf.CursorCol < line.Length && char.IsLowSurrogate(line[buf.CursorCol]) && char.IsHighSurrogate(line[buf.CursorCol - 1])) buf.CursorCol++;
                    }
                }
                else if (buf.CursorLine < buf.Lines.Count - 1)
                {
                    buf.CursorLine++;
                    buf.CursorCol = 0;
                }
            }
            EnsureCursorVisible();
            owner.NotifyCaretMoved();
            InvalidateVisual();
        }

        private static int GetFirstNonWhitespaceIndex(string s)
        {
            if (string.IsNullOrEmpty(s)) return 0;
            int i = 0;
            while (i < s.Length && (s[i] == ' ' || s[i] == '\t')) i++;
            return i;
        }

        // Home / Cmd+Left: first non-whitespace char, then column 0. End / Cmd+Right: end of the logical line.
        private void MoveCaretToLineBoundary(bool toEnd, bool shift) => ApplyMoveToAllCarets(() => MoveCaretToLineBoundaryCore(toEnd, shift));

        private void MoveCaretToLineBoundaryCore(bool toEnd, bool shift)
        {
            var buf = Buffer;
            if (buf == null || buf.Lines.Count == 0) return;
            SetSelectionAnchorIfNeeded(shift);
            buf.CursorLine = Math.Max(0, Math.Min(buf.Lines.Count - 1, buf.CursorLine));
            string line = buf.Lines[buf.CursorLine] ?? "";
            bool primary = !movingExtraCaret;
            if (toEnd)
            {
                if (!(primary && TryWrappedEnd(buf)))
                {
                    buf.CursorCol = line.Length;
                    if (primary) affinityLine = -1;
                }
            }
            else if (!(primary && TryWrappedHome(buf)))
            {
                int firstNonWs = GetFirstNonWhitespaceIndex(line);
                buf.CursorCol = (buf.CursorCol == firstNonWs) ? 0 : firstNonWs;
                if (primary) affinityLine = -1;
            }
            EnsureCursorVisible();
            owner.NotifyCaretMoved();
            InvalidateVisual();
        }

        // Ctrl+Home/End (Windows) or Cmd+Up/Down (macOS): start / end of document.
        private void MoveCaretToDocumentBoundary(bool toEnd, bool shift) => ApplyMoveToAllCarets(() => MoveCaretToDocumentBoundaryCore(toEnd, shift));

        private void MoveCaretToDocumentBoundaryCore(bool toEnd, bool shift)
        {
            var buf = Buffer;
            if (buf == null || buf.Lines.Count == 0) return;
            SetSelectionAnchorIfNeeded(shift);
            if (toEnd)
            {
                buf.CursorLine = buf.Lines.Count - 1;
                buf.CursorCol = (buf.Lines[buf.CursorLine] ?? "").Length;
            }
            else
            {
                buf.CursorLine = 0;
                buf.CursorCol = 0;
            }
            EnsureCursorVisible();
            owner.NotifyCaretMoved();
            InvalidateVisual();
        }

        private void MoveCaretVertical(int deltaLines, bool shift) => ApplyMoveToAllCarets(() => MoveCaretVerticalCore(deltaLines, shift));

        private void MoveCaretVerticalCore(int deltaLines, bool shift)
        {
            var buf = Buffer;
            if (buf == null) return;
            // VS Code: without Shift, Up moves from the selection's start and Down from its end.
            if (!shift && buf.HasSelection())
            {
                buf.GetNormalizedSelection(out int sl, out int sc, out int el, out int ec);
                if (deltaLines < 0) { buf.CursorLine = sl; buf.CursorCol = sc; } else { buf.CursorLine = el; buf.CursorCol = ec; }
            }
            SetSelectionAnchorIfNeeded(shift);
            MoveCaretByRows(buf, deltaLines, !movingExtraCaret);
            if (!movingExtraCaret) EnsureCursorVisible();
            owner.NotifyCaretMoved();
            InvalidateVisual();
        }

        private bool suppressTextInputAfterShortcut;

        /// <summary>
        /// Swallows the text input that macOS sends after an Option shortcut the editor handled (⌥Z = "Ω", ⌥⌘C = "ç"):
        /// the next text input is dropped unless another key goes down first.
        /// </summary>
        public void SuppressNextTextInput() { suppressTextInputAfterShortcut = true; }

        /// <summary>View: Toggle Word Wrap = Alt+Z (⌥Z on macOS), by key or by physical key (layouts / Option symbols).</summary>
        public static bool IsToggleWordWrapKey(Key key, PhysicalKey physical, KeyModifiers mods)
        {
            bool alt = (mods & KeyModifiers.Alt) != 0;
            bool other = (mods & (KeyModifiers.Control | KeyModifiers.Meta | KeyModifiers.Shift)) != 0;
            return alt && !other && (key == Key.Z || physical == PhysicalKey.Z);
        }

        protected override void OnTextInput(TextInputEventArgs e)
        {
            if (Buffer != null && (Buffer.IsReadOnly || Buffer.IsReadOnlyDiffPreview)) { e.Handled = true; return; }
            base.OnTextInput(e);
            if (suppressTextInputAfterShortcut)
            {
                suppressTextInputAfterShortcut = false;
                e.Handled = true;
                return;
            }
            if (string.IsNullOrEmpty(e.Text)) return;
            char ch = e.Text[0];
            if (ch < 32 && ch != '\t') return;
            HidePointerWhileTyping();
            if (TryHandleNicetyTextInput(e.Text)) { e.Handled = true; return; } // tag auto-close / linked rename
            if (TryHandleAutoClosingInput(e.Text)) { e.Handled = true; return; } // brackets / quotes / surround / overtype
            InsertTextAcrossCursors(e.Text);
            e.Handled = true;
            TextTyped?.Invoke(e.Text);
        }

        /// <summary>Called before the editor handles a key; return true to swallow it (LSP completion / signature popups).</summary>
        public Func<KeyEventArgs, bool> PreviewKeyHandler;
        /// <summary>Snippet tab-expansion hook (MainWindow.Extensions.cs), consulted after the language-server popups.</summary>
        public Func<KeyEventArgs, bool> SnippetKeyHandler;
        /// <summary>Raised after typed text was inserted at the caret (as-you-type completion, signature help triggers).</summary>
        public event Action<string> TextTyped;
        /// <summary>Raised on every pointer move over the canvas (canvas coordinates); used for LSP hover.</summary>
        public event Action<Point> PointerMovedOverCanvas;
        /// <summary>Raised when the pointer leaves the canvas, the view scrolls or focus is lost.</summary>
        public event Action PointerLeftCanvas;

        /// <summary>
        /// Fallback highlighting for a known language (no TextMate grammar / large files): the shared language-aware
        /// FallbackTokenizer (same as Windows), so '#' / "//" are comments only where the language says so.
        /// </summary>
        public static List<SyntaxTokenSpan> TokenizeLineForTheme(string line, StudioTheme theme, string language)
        {
            if (string.IsNullOrEmpty(language)) return TokenizeLineForTheme(line, theme);
            var list = new List<SyntaxTokenSpan>();
            if (string.IsNullOrEmpty(line)) return list;
            Color cText = AvaloniaColorHelper.ToAvColor(theme.TextPrimary);
            int pos = 0;
            foreach (var t in FallbackTokenizer.Tokenize(line, language))
            {
                if (t.Start > pos) list.Add(new SyntaxTokenSpan { Text = line.Substring(pos, t.Start - pos), Color = cText });
                var span = new SyntaxTokenSpan { Text = line.Substring(t.Start, t.Length), Color = cText };
                switch (t.Kind)
                {
                    case CodeTokenKind.Comment: span.Color = AvaloniaColorHelper.ToAvColor(theme.SyntaxComment); span.IsItalic = true; break;
                    case CodeTokenKind.String: span.Color = AvaloniaColorHelper.ToAvColor(theme.SyntaxString); break;
                    case CodeTokenKind.Number: span.Color = AvaloniaColorHelper.ToAvColor(theme.SyntaxNumber); break;
                    case CodeTokenKind.Keyword: span.Color = AvaloniaColorHelper.ToAvColor(theme.SyntaxKeyword); span.IsBold = true; break;
                    case CodeTokenKind.Control: span.Color = AvaloniaColorHelper.ToAvColor(theme.TokenControl); span.IsBold = true; break;
                    case CodeTokenKind.Type: span.Color = AvaloniaColorHelper.ToAvColor(theme.SyntaxType); break;
                    case CodeTokenKind.Function: span.Color = AvaloniaColorHelper.ToAvColor(theme.SyntaxFunction); break;
                }
                list.Add(span);
                pos = t.Start + t.Length;
            }
            if (pos < line.Length) list.Add(new SyntaxTokenSpan { Text = line.Substring(pos), Color = cText });
            return list;
        }

        public static List<SyntaxTokenSpan> TokenizeLineForTheme(string line, StudioTheme theme)
        {
            var list = new List<SyntaxTokenSpan>();
            if (string.IsNullOrEmpty(line)) return list;

            Color cText = AvaloniaColorHelper.ToAvColor(theme.TextPrimary);
            Color cKeyword = AvaloniaColorHelper.ToAvColor(theme.SyntaxKeyword);
            Color cType = AvaloniaColorHelper.ToAvColor(theme.SyntaxType);
            Color cFunc = AvaloniaColorHelper.ToAvColor(theme.SyntaxFunction);
            Color cString = AvaloniaColorHelper.ToAvColor(theme.SyntaxString);
            Color cComment = AvaloniaColorHelper.ToAvColor(theme.SyntaxComment);
            Color cNumber = AvaloniaColorHelper.ToAvColor(theme.SyntaxNumber);

            int i = 0;
            int n = line.Length;
            while (i < n)
            {
                // Single-line comment // or #
                if ((line[i] == '/' && i + 1 < n && line[i + 1] == '/') ||
                    (line[i] == '#' && (i == 0 || char.IsWhiteSpace(line[i - 1])) && !line.TrimStart().StartsWith("#region") && !line.TrimStart().StartsWith("#include")))
                {
                    list.Add(new SyntaxTokenSpan { Text = line.Substring(i), Color = cComment, IsItalic = true });
                    break;
                }

                // String literal "..." or '...'
                if (line[i] == '"' || line[i] == '\'')
                {
                    char quote = line[i];
                    int start = i++;
                    while (i < n)
                    {
                        if (line[i] == '\\' && i + 1 < n) { i += 2; continue; }
                        if (line[i] == quote) { i++; break; }
                        i++;
                    }
                    list.Add(new SyntaxTokenSpan { Text = line.Substring(start, i - start), Color = cString });
                    continue;
                }

                // Number literal
                if (char.IsDigit(line[i]) && (i == 0 || !IsWordChar(line[i - 1])))
                {
                    int start = i++;
                    while (i < n && (char.IsLetterOrDigit(line[i]) || line[i] == '.')) i++;
                    list.Add(new SyntaxTokenSpan { Text = line.Substring(start, i - start), Color = cNumber });
                    continue;
                }

                // Identifier or Keyword
                if (char.IsLetter(line[i]) || line[i] == '_')
                {
                    int start = i++;
                    while (i < n && IsWordChar(line[i])) i++;
                    string word = line.Substring(start, i - start);
                    if (LanguageKeywords.Contains(word))
                    {
                        list.Add(new SyntaxTokenSpan { Text = word, Color = cKeyword, IsBold = true });
                    }
                    else if (i < n && line[i] == '(')
                    {
                        list.Add(new SyntaxTokenSpan { Text = word, Color = cFunc });
                    }
                    else if (char.IsUpper(word[0]) && word.Length > 1)
                    {
                        list.Add(new SyntaxTokenSpan { Text = word, Color = cType });
                    }
                    else
                    {
                        list.Add(new SyntaxTokenSpan { Text = word, Color = cText });
                    }
                    continue;
                }

                // Punctuation & whitespace run
                int pStart = i++;
                while (i < n && !char.IsLetterOrDigit(line[i]) && line[i] != '_' && line[i] != '"' && line[i] != '\'' && line[i] != '/' && line[i] != '#')
                {
                    i++;
                }
                list.Add(new SyntaxTokenSpan { Text = line.Substring(pStart, i - pStart), Color = cText });
            }
            return list;
        }

        private static int LowerBoundFindMatchLine(List<FindMatchSpan> matches, int targetLine)
        {
            int lo = 0, hi = matches.Count;
            while (lo < hi)
            {
                int mid = lo + ((hi - lo) >> 1);
                if (matches[mid].Line < targetLine) lo = mid + 1;
                else hi = mid;
            }
            return lo;
        }

        public override void Render(DrawingContext context)
        {
            base.Render(context);
            double w = Bounds.Width;
            double h = Bounds.Height;

            Color bg = AvaloniaColorHelper.ToAvColor(Theme.EditorBg);
            Color gutterBg = AvaloniaColorHelper.ToAvColor(Theme.GutterBg);
            Color textPrimary = AvaloniaColorHelper.ToAvColor(Theme.TextPrimary);
            Color textSecondary = AvaloniaColorHelper.ToAvColor(Theme.TextSecondary);
            Color accent = AvaloniaColorHelper.ToAvColor(Theme.AccentPrimary);
            Color borderCol = AvaloniaColorHelper.ToAvColor(Theme.BorderSubtle);
            Color selBg = AvaloniaColorHelper.ToAvColor(Theme.SelectionBg, 130);
            Color lineHighlight = AvaloniaColorHelper.ToAvColor(Theme.LineHighlightBg);

            var bgBrush = new SolidColorBrush(bg);
            context.FillRectangle(bgBrush, new Rect(0, 0, w, h));

            var buf = Buffer;
            if (buf == null) return;

            hunkActionHitBoxes.Clear();
            renderFrameNo++;
            deferredOverlays.Clear();
            annotationHitBoxes.Clear();
            if (buf.ScrollTopLine != lastNotifiedScrollTop)
            {
                lastNotifiedScrollTop = buf.ScrollTopLine;
                Dispatcher.UIThread.Post(() => owner.NotifyScrollChanged(), DispatcherPriority.Background);
            }
            var annotationProvider = owner.LineAnnotationProvider;
            double codeAreaRight = CodeAreaRight;

            // Gutter background & divider
            var gutterBrush = new SolidColorBrush(gutterBg);
            var borderBrush = new SolidColorBrush(borderCol);
            var textPriBrush = new SolidColorBrush(textPrimary);
            var textSecBrush = new SolidColorBrush(textSecondary);
            var accentBrush = new SolidColorBrush(accent);
            var lineHlBrush = new SolidColorBrush(lineHighlight);
            var selBgBrush = new SolidColorBrush(selBg);
            var findMatchBrush = new SolidColorBrush(Color.FromArgb(110, 234, 179, 8));
            var gitAddBrush = new SolidColorBrush(Color.FromRgb(46, 160, 67));
            var gitModBrush = new SolidColorBrush(Color.FromRgb(0, 122, 204));
            var gitDelBrush = new SolidColorBrush(Color.FromRgb(241, 76, 76));

            context.FillRectangle(gutterBrush, new Rect(0, 0, GutterWidth, h));
            context.DrawLine(new Pen(borderBrush, 1), new Point(GutterWidth, 0), new Point(GutterWidth, h));

            var fontRegular = CachedFontRegular;
            var fontBold = CachedFontBold;
            var fontItalic = CachedFontItalic;
            var uiFontBold = CachedUiFontBold;

            if (Math.Abs(measuredCharWidthFontSize - FontSizePx) > 0.01 || measuredCharWidth <= 3.0)
            {
                var metricProbe = new FormattedText("MMMMMMMMMM", CultureInfo.InvariantCulture, FlowDirection.LeftToRight, fontRegular, FontSizePx, Brushes.White);
                double w10 = metricProbe.WidthIncludingTrailingWhitespace / 10.0;
                if (w10 > 3.0)
                {
                    measuredCharWidth = w10;
                    measuredCharWidthFontSize = FontSizePx;
                }
            }

            bool hasFolds = buf.FoldedBlockRanges.Count > 0;
            bool wrapping = buf.WordWrap;
            if (wrapping && buf.ScrollLeftPx > 0) buf.ScrollLeftPx = 0;
            else if (!wrapping && buf.ScrollLeftPx > 0 && Bounds.Width > GutterWidth + 40)
            {
                // a horizontal offset left over from another layout (narrower window, longer content before an edit,
                // a file shown in a narrow split) never scrolls past the longest line: no text clipped on the left
                double maxLeft = MaxScrollLeftPx(buf);
                if (buf.ScrollLeftPx > maxLeft + 0.5) buf.ScrollLeftPx = maxLeft;
            }
            int maxRows = (int)(h / LineHeight) + 2;
            var rows = BuildViewportRows(buf, maxRows);
            lastFrameRows = rows;
            // a deleted-zone row sits between the line above and its anchor line (sticky scroll scopes)
            Func<int, int> rowToDoc = row => row >= 0 && row < rows.Count ? (rows[row].IsDeleted && !rows[row].DeletedAfterLast ? Math.Max(0, rows[row].DocLine - 1) : rows[row].DocLine) : -1;
            BeginNicetiesFrame(buf, rowToDoc, maxRows);
            // folding ranges: incremental, bounded per frame; the rest finishes from a background tick
            var foldModel = FoldModelForFrame(buf);
            lastFrameChevronLines.Clear();

            // Matching bracket (string / comment aware when the language profile is known; naive scan otherwise)
            int matchBLine = -1, matchBCol = -1;
            if (frameSession != null && frameSession.Brackets.Profile.Enabled) TryNicetyBracketMatch(buf, out matchBLine, out matchBCol);
            else TryFindMatchingBracket(buf, buf.CursorLine, buf.CursorCol, out matchBLine, out matchBCol);
            double codeBaseX = GutterWidth + 8 - (wrapping ? 0 : buf.ScrollLeftPx);
            int firstHorizCol = Math.Max(0, (int)(buf.ScrollLeftPx / CharWidth) - 8);
            int maxHorizCols = Math.Max(120, (int)((codeAreaRight - GutterWidth) / CharWidth) + 24);
            int tabStop = TabFor(buf);
            int caretRowIdx = CaretRowOf(buf, buf.CursorLine, buf.CursorCol, true);
            // pixel-precise scrolling: rows start above the viewport by the fractional part of the top row
            double rowOffsetPx = TopRowOffsetPx;

            // Code rows never paint past the text area (long tokens, ghost text and annotations used to show between the
            // minimap and the scroll bar); WinForms clips the same way.
            using (context.PushClip(new Rect(0, 0, Math.Max(0, codeAreaRight), Math.Max(0, Bounds.Height))))
            for (int r = 0; r < rows.Count; r++)
            {
                var vr = rows[r];
                int docLine = vr.DocLine;
                double y = r * LineHeight - rowOffsetPx;
                if (vr.IsDeleted) { DrawDeletedRow(context, buf, vr, y, codeBaseX, codeAreaRight); continue; } // read-only removed lines (QA #30)
                string lineText = buf.Lines[docLine] ?? "";
                bool isFoldedHeader = hasFolds && buf.FoldedBlockRanges.ContainsKey(docLine);

                // Active line highlight (every row of the caret's line)
                if (docLine == buf.CursorLine)
                {
                    context.FillRectangle(lineHlBrush, new Rect(GutterWidth + 1, y, codeAreaRight - GutterWidth - 1, LineHeight));
                }
                if (buf.DebugStoppedLine == docLine) DrawDebugStoppedLineBackground(context, buf, y, codeAreaRight);
                DrawConflictLineBackground(context, buf, docLine, y, codeAreaRight);

                // Proposed inline diff highlight & per-hunk Accept/Reject pills
                bool isProposedDiffLine = buf.ProposedNewLines.Contains(docLine);
                bool isHunkStart = buf.ProposedHunkStarts.Contains(docLine);
                if (isProposedDiffLine || isHunkStart)
                {
                    if (isProposedDiffLine)
                        context.FillRectangle(new SolidColorBrush(Color.FromArgb(42, 34, 197, 94)), new Rect(GutterWidth + 1, y, codeAreaRight - GutterWidth - 1, LineHeight));
                    // Pills only on the first visual row of a word-wrapped hunk-start line.
                    if (isHunkStart && vr.IsFirstRow)
                    {
                        // Drawn after every row's text (below) so code never paints over the overlay widget.
                        double pillY = y;
                        int pillLine = docLine;
                        deferredOverlays.Add(() => DrawHunkActionPills(context, pillY, pillLine, codeAreaRight));
                    }
                }

                // Selection highlight on this row (primary + extra cursors)
                DrawRowSelectionHighlight(context, buf, vr, y, codeBaseX, selBgBrush);

                // Find match highlights on this row (O(log M) binary search instead of linear scan)
                if (owner.FindMatches.Count > 0)
                {
                    int fmIdx = LowerBoundFindMatchLine(owner.FindMatches, docLine);
                    while (fmIdx < owner.FindMatches.Count && owner.FindMatches[fmIdx].Line == docLine)
                    {
                        var fm = owner.FindMatches[fmIdx++];
                        int s0 = Math.Max(fm.StartCol, vr.Start);
                        int e0 = vr.IsLastRow ? fm.StartCol + fm.Length : Math.Min(fm.StartCol + fm.Length, vr.End);
                        if (e0 <= s0 && !(fm.Length == 0 && fm.StartCol >= vr.Start && (vr.IsLastRow || fm.StartCol < vr.End))) continue;
                        double fx = RowColX(buf, vr, s0, codeBaseX);
                        double fw = Math.Max(CharWidth * (fm.Length == 0 ? 0.5 : 1), RowColX(buf, vr, e0, codeBaseX) - fx);
                        if (fx + fw >= GutterWidth && fx <= codeAreaRight)
                            context.FillRectangle(findMatchBrush, new Rect(fx, y + 1, fw, LineHeight - 2));
                    }
                }

                DrawOccurrenceHighlights(context, buf, vr, lineText, y, codeBaseX, codeAreaRight);
                DrawNicetyGuides(context, buf, docLine, y, codeBaseX, codeAreaRight);

                // Gutter: line number, breakpoints and fold chevron on the first row; git bars on every row of the line
                if (vr.IsFirstRow)
                {
                    var lnFt = LineNumberText(docLine, docLine == buf.CursorLine, fontRegular, fontBold, textPriBrush, textSecBrush);
                    lnFt.Draw(context, new Point(Math.Max(4, GutterWidth - 22 - lnFt.Width), y + (LineHeight - lnFt.Height) / 2.0));
                    DrawDebugGutterGlyphs(context, buf, docLine, y);
                }

                // Gutter Git diff indicator bar (O(1) HashSet lookup, zero dictionary allocations)
                if (buf.GutterAddedLines != null && buf.GutterAddedLines.Contains(docLine))
                {
                    context.FillRectangle(gitAddBrush, new Rect(GutterWidth - 5, y + (vr.IsFirstRow ? 1 : 0), 3, LineHeight - (vr.IsFirstRow ? 1 : 0) - (vr.IsLastRow ? 1 : 0)));
                }
                else if (buf.GutterModifiedLines != null && buf.GutterModifiedLines.Contains(docLine))
                {
                    context.FillRectangle(gitModBrush, new Rect(GutterWidth - 5, y + (vr.IsFirstRow ? 1 : 0), 3, LineHeight - (vr.IsFirstRow ? 1 : 0) - (vr.IsLastRow ? 1 : 0)));
                }
                if (vr.IsLastRow && buf.GutterDeletedAfterLine != null && buf.GutterDeletedAfterLine.Contains(docLine))
                {
                    context.FillRectangle(gitDelBrush, new Rect(GutterWidth - 7, y + LineHeight - 3, 6, 3));
                }
                if (docLine == 0 && vr.IsFirstRow && buf.GutterDeletedAfterLine != null && buf.GutterDeletedAfterLine.Contains(-1))
                    context.FillRectangle(gitDelBrush, new Rect(GutterWidth - 7, y, 6, 3));

                // Gutter folding chevron
                if (vr.IsFirstRow && (isFoldedHeader || foldModel.IsFoldable(docLine)))
                {
                    lastFrameChevronLines.Add(docLine);
                    AvaloniaVectorIcons.DrawIcon(
                        context,
                        isFoldedHeader ? "chevron-right" : "chevron-down",
                        new Rect(GutterWidth - 17, y + (LineHeight - 10) / 2.0, 10, 10),
                        textSecondary,
                        1.4f);
                }

                // Syntax-highlighted tokens: the row's slice when wrapping, horizontally windowed for ultra-long lines otherwise
                int renderSliceStart = vr.Start;
                int renderSliceEnd = vr.End;
                int renderVcol = vr.StartVcol;
                if (!wrapping && lineText.Length > 360)
                {
                    renderSliceStart = Math.Min(lineText.Length, firstHorizCol);
                    renderSliceEnd = Math.Min(lineText.Length, renderSliceStart + maxHorizCols);
                    renderVcol = EditorTextLayout.VisualColumn(lineText, renderSliceStart, tabStop);
                }
                double curX = codeBaseX + renderVcol * CharWidth;
                string renderLineText = renderSliceEnd > renderSliceStart ? lineText.Substring(renderSliceStart, renderSliceEnd - renderSliceStart) : "";

                // code text never paints over the line-number gutter when scrolled horizontally (runs that start left of
                // the text area used to be drawn whole: "posi16ry = repo" over the line numbers)
                var textClip = new Rect(GutterWidth + 1, y, Math.Max(0, codeAreaRight - GutterWidth - 1), LineHeight);
                LastFrameTextClipLeft = textClip.X;
                if (renderLineText.Length > 0)
                using (context.PushClip(textClip))
                {
                    var tokens = GetNicetyTokens(buf, docLine, lineText, renderSliceStart, renderLineText);
                    // shaped runs are cached per row (AvaloniaEditorCanvas.RenderCache.cs): scrolling shapes only new rows
                    var shaped = GetRowTextRuns(docLine, vr.Row, false, lineText, renderSliceStart, renderVcol, tokens, tabStop);
                    curX = DrawCachedRow(context, shaped, docLine, codeBaseX, y, codeAreaRight);
                }

                // Folded block placeholder pill (after the header's last row)
                if (isFoldedHeader && vr.IsLastRow && buf.FoldedBlockRanges.TryGetValue(docLine, out int foldedTo))
                {
                    var pillFt = new FormattedText(
                        $" ⋯ ({foldedTo - docLine} lines folded) ",
                        CultureInfo.InvariantCulture,
                        FlowDirection.LeftToRight,
                        fontItalic,
                        FontSizePx - 1.5,
                        accentBrush);
                    double pillX = curX + 6; // right after the text, like VS Code's folded-region marker (never over the code)
                    var pillRect = new Rect(pillX, y + 2, pillFt.Width + 6, LineHeight - 4);
                    context.DrawRectangle(new SolidColorBrush(Color.FromArgb(40, accent.R, accent.G, accent.B)), new Pen(accentBrush, 1), pillRect, 3, 3);
                    context.DrawText(pillFt, new Point(pillRect.X + 3, y + (LineHeight - pillFt.Height) / 2.0));
                }

                // End-of-line annotation (inline blame / code lens) after the line's last row
                if (vr.IsLastRow && annotationProvider != null && !(docLine == buf.CursorLine && !string.IsNullOrEmpty(buf.GhostTextSuggestion)) && !isFoldedHeader)
                {
                    EditorLineAnnotation ann = null;
                    try { ann = annotationProvider(buf, docLine); } catch { ann = null; }
                    if (ann != null && !string.IsNullOrEmpty(ann.Text))
                    {
                        double ax = RowColX(buf, vr, lineText.Length, codeBaseX) + CharWidth * 3;
                        if (lineText.Length == 0) ax = codeBaseX + CharWidth * (ann.IsAction ? 0 : 1);
                        if (ax < GutterWidth + 4) ax = GutterWidth + 4;
                        if (ax < codeAreaRight - 40 && ann.Segments != null)
                        {
                            DrawSegmentedAnnotation(context, ann, docLine, ax, y, codeAreaRight);
                        }
                        else if (ax < codeAreaRight - 40)
                        {
                            bool hot = ReferenceEquals(ann, hoveredAnnotation) || (hoveredAnnotation != null && hoveredAnnotation.Tag != null && ReferenceEquals(hoveredAnnotation.Tag, ann.Tag));
                            IBrush annBrush = ann.IsAction
                                ? new SolidColorBrush(AvaloniaColorHelper.ToAvColor(Theme.AccentPrimary, (byte)(hot ? 255 : 200)))
                                : new SolidColorBrush(AvaloniaColorHelper.ToAvColor(Theme.TextSecondary, (byte)(hot ? 200 : (Theme.IsLight ? 165 : 125))));
                            var aft = new FormattedText(ann.Text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, ann.IsAction ? CachedUiFontBold : fontItalic, FontSizePx - (ann.IsAction ? 1.5 : 0.5), annBrush);
                            aft.MaxTextWidth = Math.Max(20, codeAreaRight - ax - 8);
                            aft.MaxLineCount = 1;
                            aft.Trimming = TextTrimming.CharacterEllipsis;
                            if (hot && ann.IsAction) aft.SetTextDecorations(TextDecorations.Underline);
                            context.DrawText(aft, new Point(ax, y + (LineHeight - aft.Height) / 2.0));
                            annotationHitBoxes.Add((new Rect(ax, y, Math.Min(aft.WidthIncludingTrailingWhitespace, aft.MaxTextWidth), LineHeight), docLine, ann));
                        }
                    }
                }

                bool isCaretRow = docLine == buf.CursorLine && vr.Row == caretRowIdx;

                // Ghost text suggestion after the caret
                if (isCaretRow && !string.IsNullOrEmpty(buf.GhostTextSuggestion))
                {
                    double gx = Math.Max(curX + (lineText.Length > 0 ? 8 : 0), RowColX(buf, vr, buf.CursorCol, codeBaseX));
                    int nlIdx = buf.GhostTextSuggestion.IndexOf('\n');
                    string firstGhostLine = nlIdx >= 0 ? buf.GhostTextSuggestion.Substring(0, nlIdx) : buf.GhostTextSuggestion;
                    var gft = new FormattedText(
                        firstGhostLine + "   [Tab to accept]",
                        CultureInfo.InvariantCulture,
                        FlowDirection.LeftToRight,
                        fontItalic,
                        FontSizePx,
                        AvaloniaColorHelper.ToBrush(Theme.SyntaxComment));
                    context.DrawText(gft, new Point(gx, y + (LineHeight - gft.Height) / 2.0));
                }

                // IME caret geometry + in-progress composition (drawn even while the caret blink is off)
                if (isCaretRow)
                {
                    double icx = RowColX(buf, vr, buf.CursorCol, codeBaseX);
                    var newRect = new Rect(icx, y, 2, LineHeight);
                    if (newRect != imeCaretRect)
                    {
                        imeCaretRect = newRect;
                        Dispatcher.UIThread.Post(() => imeClient.NotifyCaretChanged(), DispatcherPriority.Background);
                    }
                    if (IsImeComposing)
                    {
                        var pft = new FormattedText(imePreedit, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, fontRegular, FontSizePx, accentBrush);
                        pft.SetTextDecorations(TextDecorations.Underline);
                        context.FillRectangle(new SolidColorBrush(AvaloniaColorHelper.ToAvColor(Theme.EditorBg)), new Rect(icx, y + 1, pft.WidthIncludingTrailingWhitespace + 3, LineHeight - 2));
                        context.DrawText(pft, new Point(icx, y + (LineHeight - pft.Height) / 2.0));
                        context.FillRectangle(accentBrush, new Rect(icx + pft.WidthIncludingTrailingWhitespace + 1, y + 2, 2.0, LineHeight - 4));
                    }
                }

                // Matching bracket highlight box
                if (docLine == matchBLine && matchBCol >= vr.Start && (matchBCol < vr.End || (vr.IsLastRow && matchBCol <= vr.End)))
                {
                    double bx = RowColX(buf, vr, matchBCol, codeBaseX);
                    context.DrawRectangle(null, new Pen(accentBrush, 1.3), new Rect(bx, y + 2, CharWidth + 1, LineHeight - 4));
                }

                // Cmd/Ctrl+hover link underline (VS Code "editor link")
                if (linkHover != null && docLine == linkHoverLine && linkHover.EndCol > linkHover.StartCol)
                {
                    int ls = Math.Max(linkHover.StartCol, vr.Start), le = Math.Min(Math.Min(lineText.Length, linkHover.EndCol), vr.IsLastRow ? int.MaxValue : vr.End);
                    double lx0 = RowColX(buf, vr, ls, codeBaseX);
                    double lx1 = RowColX(buf, vr, le, codeBaseX);
                    double ly = Math.Round(y + (LineHeight + FontSizePx) / 2.0 + 1.5) + 0.5;
                    if (lx1 > lx0)
                        context.DrawLine(new Pen(accentBrush, 1.0), new Point(lx0, ly), new Point(lx1, ly));
                }

                // Diagnostics squiggles on this row
                if (buf.Diagnostics != null && buf.Diagnostics.Count > 0)
                {
                    for (int di = 0; di < buf.Diagnostics.Count; di++)
                    {
                        var diag = buf.Diagnostics[di];
                        if (diag.LineNumber - 1 != docLine) continue;
                        bool isErr = string.Equals(diag.Severity, "Error", StringComparison.OrdinalIgnoreCase);
                        Color sqCol = isErr
                            ? AvaloniaColorHelper.ToAvColor(Theme.DiagnosticError)
                            : AvaloniaColorHelper.ToAvColor(Theme.DiagnosticWarning);
                        int c0 = EditorIndentation.LeadingWhitespaceLength(lineText), c1 = lineText.Length;
                        if (diag.IsLanguageServer && diag.EndColumn > 0)
                        {
                            // Language-server diagnostics carry an exact range: underline just that span.
                            c0 = Math.Max(0, Math.Min(lineText.Length, diag.Column - 1));
                            c1 = Math.Max(c0, Math.Min(lineText.Length, diag.EndColumn - 1));
                        }
                        int s0 = Math.Max(c0, vr.Start), e0 = vr.IsLastRow ? c1 : Math.Min(c1, vr.End);
                        if (e0 < s0 || (e0 == s0 && !(vr.IsLastRow && c0 >= vr.Start))) continue;
                        double sx = RowColX(buf, vr, s0, codeBaseX);
                        double sw = Math.Min(codeAreaRight - sx, RowColX(buf, vr, e0, codeBaseX) - sx);
                        if (!(diag.IsLanguageServer && diag.EndColumn > 0) && vr.IsLastRow) sw = Math.Max(sw, CharWidth * 4);
                        sw = Math.Max(CharWidth, sw);
                        if (sw > 0)
                            DrawSquiggleUnderline(context, sx, y + LineHeight - 3, sw, sqCol);
                    }
                }

                // carets (primary and extra) are painted by the caret overlay (AvaloniaCaretOverlay): blinking and the
                // smooth caret animation repaint only that small layer, never the whole editor
            }

            foreach (var overlay in deferredOverlays) overlay();
            deferredOverlays.Clear();
            RenderStickyScroll(context, buf, rowToDoc, codeBaseX, codeAreaRight);
            EndNicetiesFrame(buf);

            // Diagnostic Hover Tooltip Card
            if (hoveredLine >= 0 && buf.Diagnostics != null)
            {
                var hoverDiag = buf.Diagnostics.FirstOrDefault(d => d.LineNumber - 1 == hoveredLine);
                if (hoverDiag != null)
                {
                    var dft = new FormattedText(
                        $"△ [{hoverDiag.Severity}] {hoverDiag.Message}",
                        CultureInfo.InvariantCulture,
                        FlowDirection.LeftToRight,
                        uiFontBold,
                        11.5,
                        new SolidColorBrush(textPrimary));
                    double tx = Math.Min(w - dft.Width - 30, Math.Max(GutterWidth + 12, lastHoverPt.X));
                    double ty = Math.Max(4, lastHoverPt.Y - 30);
                    var tipRect = new Rect(tx, ty, dft.Width + 16, dft.Height + 10);
                    context.DrawRectangle(
                        AvaloniaColorHelper.ToBrush(Theme.FloatingBg),
                        new Pen(AvaloniaColorHelper.ToBrush(Theme.DiagnosticWarning), 1),
                        tipRect, 4, 4);
                    context.DrawText(dft, new Point(tipRect.X + 8, tipRect.Y + 5));
                }
            }

            // Right-side Minimap (left of the scrollbar)
            if (ShowMinimap && w > GutterWidth + MinimapWidth + ScrollbarWidth + 80)
            {
                RenderMinimap(context, buf, MinimapLeft, MinimapWidth, h);
            }
            RenderScrollbars(context, buf, w, h);
            TrimRenderCaches(maxRows);
        }

        /// <summary>Selections of the primary caret and every extra caret (single- and multi-line) on one screen row.</summary>

        private readonly List<Action> deferredOverlays = new List<Action>();

        /// <summary>Per-hunk Accept / Reject overlay widget (opaque, bordered) at the right edge of a hunk's first row.</summary>
        private void DrawHunkActionPills(DrawingContext context, double y, int docLine, double codeAreaRight)
        {
            double pillRight = codeAreaRight - 10;
            var rejectRect = new Rect(pillRight - 58, y + 2, 54, LineHeight - 4);
            var acceptRect = new Rect(rejectRect.X - 62, y + 2, 58, LineHeight - 4);
            // the removed lines themselves are shown as the read-only red zone above (QA #30), like VS Code
            // VS Code-style overlay widget: an opaque bordered backdrop so code under the actions never bleeds through.
            var backdrop = new Rect(acceptRect.X - 4, y + 0.5, rejectRect.Right - acceptRect.X + 8, LineHeight - 1);
            context.DrawRectangle(new SolidColorBrush(AvaloniaColorHelper.ToAvColor(UiTokens.WidgetShadow(Theme))), null, backdrop.Translate(new Vector(0, 1)), UiTokens.ControlRadius + 1, UiTokens.ControlRadius + 1);
            context.DrawRectangle(new SolidColorBrush(AvaloniaColorHelper.ToAvColor(UiTokens.WidgetBackground(Theme))),
                new Pen(new SolidColorBrush(AvaloniaColorHelper.ToAvColor(UiTokens.WidgetBorder(Theme))), 1), backdrop, UiTokens.ControlRadius + 1, UiTokens.ControlRadius + 1);
            // Calm per-hunk actions: primary (accent) Accept, secondary (neutral) Reject.
            context.DrawRectangle(new SolidColorBrush(AvaloniaColorHelper.ToAvColor(UiTokens.ButtonPrimaryBackground(Theme))), null, acceptRect, UiTokens.ControlRadius, UiTokens.ControlRadius);
            context.DrawRectangle(new SolidColorBrush(AvaloniaColorHelper.ToAvColor(UiTokens.ButtonSecondaryBackground(Theme))), null, rejectRect, UiTokens.ControlRadius, UiTokens.ControlRadius);

            var accFt = new FormattedText("Accept", CultureInfo.InvariantCulture, FlowDirection.LeftToRight, CachedUiFontRegular, 10.5, new SolidColorBrush(AvaloniaColorHelper.ToAvColor(UiTokens.OnAccent(Theme))));
            var rejFt = new FormattedText("Reject", CultureInfo.InvariantCulture, FlowDirection.LeftToRight, CachedUiFontRegular, 10.5, new SolidColorBrush(AvaloniaColorHelper.ToAvColor(Theme.FgPrimary)));
            context.DrawText(accFt, new Point(acceptRect.X + (acceptRect.Width - accFt.Width) / 2, acceptRect.Y + (acceptRect.Height - accFt.Height) / 2));
            context.DrawText(rejFt, new Point(rejectRect.X + (rejectRect.Width - rejFt.Width) / 2, rejectRect.Y + (rejectRect.Height - rejFt.Height) / 2));

            hunkActionHitBoxes.Add((acceptRect, docLine, true));
            hunkActionHitBoxes.Add((rejectRect, docLine, false));
        }

        private void DrawRowSelectionHighlight(DrawingContext context, EditorTabBuffer buf, EditorVisualRow vr, double y, double codeBaseX, IBrush selBrush)
        {
            if (buf.HasSelection())
            {
                buf.GetNormalizedSelection(out int l0, out int c0, out int l1, out int c1);
                FillSelectionOnRow(context, buf, vr, l0, c0, l1, c1, y, codeBaseX, selBrush);
            }
            foreach (var ec in ExtraCursors)
            {
                if (!ec.HasSelection) continue;
                int al = ec.AnchorLine, ac = ec.AnchorCol, cl = ec.Line, cc = ec.Col;
                bool fwd = al < cl || (al == cl && ac <= cc);
                if (fwd) FillSelectionOnRow(context, buf, vr, al, ac, cl, cc, y, codeBaseX, selBrush);
                else FillSelectionOnRow(context, buf, vr, cl, cc, al, ac, y, codeBaseX, selBrush);
            }
        }

        private void FillSelectionOnRow(DrawingContext context, EditorTabBuffer buf, EditorVisualRow vr, int l0, int c0, int l1, int c1, double y, double codeBaseX, IBrush selBrush)
        {
            int docLine = vr.DocLine;
            if (docLine < l0 || docLine > l1) return;
            int lineLen = (buf.Lines[docLine] ?? "").Length;
            int startC = docLine == l0 ? c0 : 0;
            int endC = docLine == l1 ? c1 : lineLen;
            bool includesBreak = docLine < l1; // the selection continues on the next line
            int s = Math.Max(startC, vr.Start);
            int e = vr.IsLastRow ? endC : Math.Min(endC, vr.End);
            bool continuesPastRow = !vr.IsLastRow && endC > vr.End;
            if (e < s || (e == s && !(vr.IsLastRow && includesBreak && startC <= lineLen) && !continuesPastRow)) return;
            double x0 = RowColX(buf, vr, s, codeBaseX);
            double x1 = RowColX(buf, vr, e, codeBaseX);
            // an empty line inside a selection / the line break itself show a small block, like VS Code
            if (vr.IsLastRow && includesBreak && e >= lineLen) x1 += CharWidth * 0.5;
            double width = Math.Max(includesBreak || continuesPastRow ? CharWidth * 0.5 : 0, x1 - x0);
            if (width > 0) context.FillRectangle(selBrush, new Rect(x0, y, width, LineHeight));
        }

        private static void DrawSquiggleUnderline(DrawingContext context, double x, double y, double width, Color color)
        {
            var pen = new Pen(new SolidColorBrush(color), 1.3);
            double step = 3.5;
            bool up = true;
            for (double cur = x; cur < x + width; cur += step)
            {
                double y1 = up ? y - 1.5 : y + 1.5;
                double y2 = up ? y + 1.5 : y - 1.5;
                context.DrawLine(pen, new Point(cur, y1), new Point(Math.Min(x + width, cur + step), y2));
                up = !up;
            }
        }

        private void RenderMinimap(DrawingContext context, EditorTabBuffer buf, double mmX, double mmW, double h)
        {
            bool isLight = Theme.IsLight || AvaloniaColorHelper.IsLightColor(Theme.EditorBg);
            Color mmBg = AvaloniaColorHelper.ToAvColor(Theme.SidebarBg);
            Color borderCol = AvaloniaColorHelper.ToAvColor(Theme.BorderSubtle);
            Color accent = AvaloniaColorHelper.ToAvColor(Theme.AccentPrimary);

            context.FillRectangle(new SolidColorBrush(mmBg), new Rect(mmX, 0, mmW, h));
            context.DrawLine(new Pen(new SolidColorBrush(borderCol), 1), new Point(mmX, 0), new Point(mmX, h));

            int totalLines = Math.Max(1, buf.Lines.Count);
            int maxMmLines = Math.Min(totalLines, Math.Max(1, (int)(h / 3.0)));
            double step = totalLines > maxMmLines ? (double)totalLines / maxMmLines : 1.0;

            byte barAlpha = (byte)(isLight ? 90 : 135);
            var kwBrush = new SolidColorBrush(AvaloniaColorHelper.ToAvColor(Theme.SyntaxKeyword, barAlpha));
            var fnBrush = new SolidColorBrush(AvaloniaColorHelper.ToAvColor(Theme.SyntaxFunction, barAlpha));
            var cmBrush = new SolidColorBrush(AvaloniaColorHelper.ToAvColor(Theme.SyntaxComment, barAlpha));
            var txBrush = new SolidColorBrush(AvaloniaColorHelper.ToAvColor(Theme.TextSecondary, barAlpha));

            for (int r = 0; r < maxMmLines; r++)
            {
                int idx = (int)(r * step);
                if (idx >= totalLines) break;
                string ln = buf.Lines[idx];
                if (string.IsNullOrEmpty(ln)) continue;

                int firstNonWs = 0;
                int leadSpaces = 0;
                while (firstNonWs < ln.Length && (ln[firstNonWs] == ' ' || ln[firstNonWs] == '\t'))
                {
                    leadSpaces += (ln[firstNonWs] == '\t') ? 4 : 1;
                    firstNonWs++;
                }
                if (firstNonWs >= ln.Length) continue;

                int nonWsLen = ln.Length - firstNonWs;
                double mx = mmX + 6 + Math.Min(20, leadSpaces * 1.2);
                double mw = Math.Max(4, Math.Min(mmW - 12 - (mx - mmX), Math.Min(120, nonWsLen) * 0.45));
                double my = r * 3.0 + 4.0;

                char c0 = ln[firstNonWs];
                char c1 = (firstNonWs + 1 < ln.Length) ? ln[firstNonWs + 1] : '\0';
                IBrush barBrush = txBrush;
                if (FallbackTokenizer.StartsWithLineComment(ln, firstNonWs, buf.Language)
                    || (frameSession != null && FallbackTokenizer.IsCommentState(frameSession.FallbackStartState(idx)))) // inside a /* */ block
                    barBrush = cmBrush;
                else if (ln.IndexOf('(', firstNonWs, Math.Min(80, nonWsLen)) >= 0)
                    barBrush = fnBrush;
                else if (c0 == 'p' || c0 == 'c' || c0 == 'd' || c0 == 'e' || c0 == 'f')
                    barBrush = kwBrush;

                context.FillRectangle(barBrush, new Rect(mx, my, mw, 1.6));
            }

            // Viewport slider highlight
            // the slider covers the document lines actually on screen (folded regions / wrapped rows included)
            int visRows = Math.Max(1, (int)(h / LineHeight));
            if (lastFrameRows.Count > 0) visRows = Math.Max(1, lastFrameRows[lastFrameRows.Count - 1].DocLine - lastFrameRows[0].DocLine + 1);
            double trackH = Math.Min(h - 4, maxMmLines * 3.0);
            double vpTop = ((buf.ScrollTopLine + buf.ScrollTopFraction) / totalLines) * Math.Max(10, trackH - 18);
            double vpH = Math.Max(20, ((double)visRows / totalLines) * trackH);
            var vpRect = new Rect(mmX + 1, Math.Min(h - vpH, vpTop), mmW - 2, Math.Min(h - vpTop, vpH));
            context.FillRectangle(
                new SolidColorBrush(Color.FromArgb((byte)(isLight ? 36 : 45), accent.R, accent.G, accent.B)),
                vpRect);
            context.DrawRectangle(
                null,
                new Pen(new SolidColorBrush(Color.FromArgb(110, accent.R, accent.G, accent.B)), 1),
                vpRect);
        }
    }

    /// <summary>[NSCursor setHiddenUntilMouseMoves:YES] — the native macOS "hide the pointer while typing" behaviour.</summary>
    internal static class MacCursorNative
    {
        [DllImport("/usr/lib/libobjc.A.dylib", EntryPoint = "objc_getClass")]
        private static extern IntPtr objc_getClass(string name);
        [DllImport("/usr/lib/libobjc.A.dylib", EntryPoint = "sel_registerName")]
        private static extern IntPtr sel_registerName(string name);
        [DllImport("/usr/lib/libobjc.A.dylib", EntryPoint = "objc_msgSend")]
        private static extern void objc_msgSend_bool(IntPtr receiver, IntPtr selector, [MarshalAs(UnmanagedType.I1)] bool value);

        private static IntPtr cls, sel;

        public static void SetHiddenUntilMouseMoves()
        {
            if (cls == IntPtr.Zero)
            {
                cls = objc_getClass("NSCursor");
                sel = sel_registerName("setHiddenUntilMouseMoves:");
            }
            if (cls != IntPtr.Zero && sel != IntPtr.Zero) objc_msgSend_bool(cls, sel, true);
        }
    }
}
