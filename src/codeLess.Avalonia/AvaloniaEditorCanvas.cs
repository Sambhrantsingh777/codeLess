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
        private readonly StackPanel recentFoldersPanel;

        private bool findMatchCase = false;
        private bool findWholeWord = false;
        private bool findUseRegex = false;
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

            // Welcome Page Host
            recentFoldersPanel = new StackPanel { Spacing = 4 };
            welcomeHost = BuildWelcomePage();
            bodyOverlayGrid.Children.Add(welcomeHost);

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
            if (tab == null)
            {
                ShowWelcomeView = true;
                welcomeHost.IsVisible = true;
                Canvas.IsVisible = false;
                breadcrumbBar.IsVisible = false;
                diffReviewBanner.IsVisible = false;
                RefreshRecentFoldersOnWelcome();
                return;
            }

            ShowWelcomeView = false;
            welcomeHost.IsVisible = false;
            Canvas.IsVisible = true;
            breadcrumbBar.IsVisible = true;
            Canvas.ResetExtraCursors();
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
            ShowWelcomeView = true;
            welcomeHost.IsVisible = true;
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
            RebuildWelcomeThemeColors();
            RefreshBreadcrumbs();
            Canvas.ApplyTheme(theme);
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
                int count = CurrentBuffer.ProposedNewLines != null ? CurrentBuffer.ProposedNewLines.Count : 0;
                diffBannerSummaryText.Text = count == 1 ? $"1 proposed change in {CurrentBuffer.FileName}" : $"{count} proposed changes in {CurrentBuffer.FileName}";
                diffReviewBanner.IsVisible = true;
            }
            else
            {
                diffReviewBanner.IsVisible = false;
            }
        }

        public void StageProposedDiffContent(string newFullContent)
        {
            if (CurrentBuffer == null || newFullContent == null) return;
            CurrentBuffer.PushUndoSnapshot();
            string[] oldLines = CurrentBuffer.Lines.ToArray();
            string[] newLines = newFullContent.Replace("\r\n", "\n").Split('\n');

            CurrentBuffer.ProposedNewLines.Clear();
            CurrentBuffer.RemovedLineHistory.Clear();

            int prefix = 0;
            int minLen = Math.Min(oldLines.Length, newLines.Length);
            while (prefix < minLen && oldLines[prefix] == newLines[prefix])
                prefix++;

            int oldSuffix = oldLines.Length - 1;
            int newSuffix = newLines.Length - 1;
            while (oldSuffix >= prefix && newSuffix >= prefix && oldLines[oldSuffix] == newLines[newSuffix])
            {
                oldSuffix--;
                newSuffix--;
            }

            if (newSuffix >= prefix)
            {
                for (int i = prefix; i <= newSuffix; i++)
                    CurrentBuffer.ProposedNewLines.Add(i);
            }
            else if (oldSuffix >= prefix && prefix < newLines.Length)
            {
                CurrentBuffer.ProposedNewLines.Add(prefix);
            }

            if (oldSuffix >= prefix)
            {
                var removed = new List<string>();
                for (int i = prefix; i <= oldSuffix; i++)
                    removed.Add(oldLines[i]);
                int attachLine = Math.Min(prefix, Math.Max(0, newLines.Length - 1));
                CurrentBuffer.RemovedLineHistory[attachLine] = removed;
            }

            CurrentBuffer.Lines = new List<string>(newLines);
            CurrentBuffer.SyncContentFromLines();
            CurrentBuffer.IsDirty = true;
            CurrentBuffer.RefreshGutterDiff();
            RefreshDiffBanner();
            BufferModified?.Invoke(CurrentBuffer);
            Canvas.InvalidateVisual();
        }

        public void AcceptAllPendingDiffs()
        {
            if (CurrentBuffer == null) return;
            CurrentBuffer.ProposedNewLines.Clear();
            CurrentBuffer.RemovedLineHistory.Clear();
            CurrentBuffer.RefreshGutterDiff();
            RefreshDiffBanner();
            DiffAllDecisionRequested?.Invoke(CurrentBuffer, true);
            BufferModified?.Invoke(CurrentBuffer);
            Canvas.InvalidateVisual();
        }

        public void RejectAllPendingDiffs()
        {
            if (CurrentBuffer == null || !CurrentBuffer.HasPendingDiff) return;
            if (CurrentBuffer.UndoStack.Count > 0)
            {
                string prev = CurrentBuffer.UndoStack.Pop().Content;
                CurrentBuffer.Content = prev;
                CurrentBuffer.SyncLinesFromContent();
            }
            CurrentBuffer.ProposedNewLines.Clear();
            CurrentBuffer.RemovedLineHistory.Clear();
            CurrentBuffer.IsDirty = (CurrentBuffer.Content != CurrentBuffer.SavedContent);
            CurrentBuffer.RefreshGutterDiff();
            RefreshDiffBanner();
            DiffAllDecisionRequested?.Invoke(CurrentBuffer, false);
            BufferModified?.Invoke(CurrentBuffer);
            Canvas.InvalidateVisual();
        }

        public void AcceptDiffHunkAtLine(int lineIdx)
        {
            if (CurrentBuffer == null) return;
            for (int l = lineIdx - 3; l <= lineIdx + 10; l++)
            {
                CurrentBuffer.ProposedNewLines.Remove(l);
                CurrentBuffer.RemovedLineHistory.Remove(l);
            }
            RefreshDiffBanner();
            BufferModified?.Invoke(CurrentBuffer);
            Canvas.InvalidateVisual();
        }

        public void RejectDiffHunkAtLine(int lineIdx)
        {
            if (CurrentBuffer == null) return;
            if (CurrentBuffer.RemovedLineHistory.TryGetValue(lineIdx, out var oldLines) && oldLines != null && oldLines.Count > 0)
            {
                if (lineIdx >= 0 && lineIdx < CurrentBuffer.Lines.Count)
                    CurrentBuffer.Lines[lineIdx] = oldLines[0];
            }
            CurrentBuffer.ProposedNewLines.Remove(lineIdx);
            CurrentBuffer.RemovedLineHistory.Remove(lineIdx);
            CurrentBuffer.SyncContentFromLines();
            RefreshDiffBanner();
            BufferModified?.Invoke(CurrentBuffer);
            Canvas.InvalidateVisual();
        }

        public void JumpDiffHunk(int direction)
        {
            if (CurrentBuffer == null || CurrentBuffer.ProposedNewLines.Count == 0) return;
            var sorted = CurrentBuffer.ProposedNewLines.OrderBy(x => x).ToList();
            int cur = CurrentBuffer.CursorLine;
            int target = direction >= 0
                ? sorted.FirstOrDefault(x => x > cur)
                : sorted.LastOrDefault(x => x < cur);
            if (target == 0 && !sorted.Contains(0))
                target = direction >= 0 ? sorted[0] : sorted[sorted.Count - 1];
            GoToLine(target + 1);
        }

        public void GoToLine(int oneBasedLine, int oneBasedCol = 1)
        {
            if (CurrentBuffer == null) return;
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

        public void HideFindBar()
        {
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
            string q = findInputBox.Text ?? "";
            if (CurrentBuffer == null || string.IsNullOrEmpty(q))
            {
                findCountLabel.Text = "No results";
                Canvas.InvalidateVisual();
                return;
            }

            Regex rx = null;
            if (findUseRegex || findWholeWord)
            {
                try
                {
                    string pat = findUseRegex ? q : $@"\b{Regex.Escape(q)}\b";
                    var opts = findMatchCase ? RegexOptions.None : RegexOptions.IgnoreCase;
                    rx = new Regex(pat, opts);
                }
                catch
                {
                    findCountLabel.Text = "Invalid regex";
                    Canvas.InvalidateVisual();
                    return;
                }
            }

            var cmp = findMatchCase ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
            for (int i = 0; i < CurrentBuffer.Lines.Count; i++)
            {
                string line = CurrentBuffer.Lines[i] ?? "";
                if (rx != null)
                {
                    foreach (Match m in rx.Matches(line))
                    {
                        if (m.Length > 0)
                            FindMatches.Add(new FindMatchSpan { Line = i, StartCol = m.Index, Length = m.Length });
                    }
                }
                else
                {
                    int pos = 0;
                    while (pos <= line.Length - q.Length)
                    {
                        int idx = line.IndexOf(q, pos, cmp);
                        if (idx < 0) break;
                        FindMatches.Add(new FindMatchSpan { Line = i, StartCol = idx, Length = q.Length });
                        pos = idx + Math.Max(1, q.Length);
                    }
                }
            }

            if (FindMatches.Count > 0)
            {
                activeFindMatchIndex = 0;
                findCountLabel.Text = $"1 of {FindMatches.Count}";
            }
            else
            {
                findCountLabel.Text = "No results";
            }
            Canvas.InvalidateVisual();
        }

        public void FindNext(int step = 1)
        {
            if (FindMatches.Count == 0) RecomputeFindMatches();
            if (FindMatches.Count == 0 || CurrentBuffer == null) return;

            activeFindMatchIndex = (activeFindMatchIndex + step + FindMatches.Count) % FindMatches.Count;
            var m = FindMatches[activeFindMatchIndex];
            CurrentBuffer.SelectionStartLine = m.Line;
            CurrentBuffer.SelectionStartCol = m.StartCol;
            CurrentBuffer.CursorLine = m.Line;
            CurrentBuffer.CursorCol = m.StartCol + m.Length;
            findCountLabel.Text = $"{activeFindMatchIndex + 1} of {FindMatches.Count}";
            Canvas.EnsureCursorVisible();
            CaretMoved?.Invoke(CurrentBuffer);
            Canvas.InvalidateVisual();
        }

        public void ReplaceCurrentMatch(string replacement = null)
        {
            if (CurrentBuffer == null) return;
            if (FindMatches.Count == 0) RecomputeFindMatches();
            if (FindMatches.Count == 0) return;

            string rep = replacement ?? (replaceInputBox.Text ?? "");
            if (activeFindMatchIndex < 0 || activeFindMatchIndex >= FindMatches.Count)
                activeFindMatchIndex = 0;

            var m = FindMatches[activeFindMatchIndex];
            CurrentBuffer.PushUndoSnapshot();
            string line = CurrentBuffer.Lines[m.Line];
            CurrentBuffer.Lines[m.Line] = line.Substring(0, m.StartCol) + rep + line.Substring(m.StartCol + m.Length);
            CurrentBuffer.CursorLine = m.Line;
            CurrentBuffer.CursorCol = m.StartCol + rep.Length;
            CurrentBuffer.ClearSelection();
            CurrentBuffer.SyncContentFromLines();
            CurrentBuffer.IsDirty = true;
            CurrentBuffer.RefreshGutterDiff();
            LlmHarnessAndAgentEngine.AnalyzeBufferAstAndDiagnostics(CurrentBuffer);
            RecomputeFindMatches();
            BufferModified?.Invoke(CurrentBuffer);
            Canvas.InvalidateVisual();
        }

        public int ReplaceAllMatches(string replacement = null)
        {
            if (CurrentBuffer == null) return 0;
            if (FindMatches.Count == 0) RecomputeFindMatches();
            if (FindMatches.Count == 0) return 0;

            string rep = replacement ?? (replaceInputBox.Text ?? "");
            int count = FindMatches.Count;
            CurrentBuffer.PushUndoSnapshot();

            for (int i = FindMatches.Count - 1; i >= 0; i--)
            {
                var m = FindMatches[i];
                string line = CurrentBuffer.Lines[m.Line];
                if (m.StartCol >= 0 && m.StartCol + m.Length <= line.Length)
                {
                    CurrentBuffer.Lines[m.Line] = line.Substring(0, m.StartCol) + rep + line.Substring(m.StartCol + m.Length);
                }
            }
            CurrentBuffer.ClearSelection();
            CurrentBuffer.SyncContentFromLines();
            CurrentBuffer.IsDirty = true;
            CurrentBuffer.RefreshGutterDiff();
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

            mcBtn = WorkbenchStyles.IconButton("case-sensitive", "Match Case (Alt+C)", null, currentTheme, 20, 14);
            wwBtn = WorkbenchStyles.IconButton("whole-word", "Match Whole Word (Alt+W)", null, currentTheme, 20, 14);
            rxBtn = WorkbenchStyles.IconButton("regex", "Use Regular Expression (Alt+R)", null, currentTheme, 20, 14);
            foreach (var tb in new[] { mcBtn, wwBtn, rxBtn }) { tb.Classes.Remove("icon"); tb.Classes.Add("toggle"); tb.BorderThickness = new Thickness(1); tb.BorderBrush = Brushes.Transparent; tb.CornerRadius = new CornerRadius(3); }
            fBox.InnerRightContent = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 1, Margin = new Thickness(0, 0, 2, 0), VerticalAlignment = VerticalAlignment.Center, Children = { mcBtn, wwBtn, rxBtn } };
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
            findRow.Children.Add(closeBtn);

            var repOneBtn = WorkbenchStyles.IconButton("replace", "Replace (Enter)", null, currentTheme);
            var repAllBtn = WorkbenchStyles.IconButton("replace-all", "Replace All (Ctrl+Alt+Enter)", null, currentTheme);
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

            myFBox.TextChanged += (s, e) => RecomputeFindMatches();
            myFBox.KeyDown += (s, e) =>
            {
                bool alt = e.KeyModifiers.HasFlag(KeyModifiers.Alt);
                if (alt && e.Key == Key.C)
                {
                    findMatchCase = !findMatchCase;
                    UpdateFindToggleButtonStyles();
                    RecomputeFindMatches();
                    e.Handled = true;
                }
                else if (alt && e.Key == Key.W)
                {
                    findWholeWord = !findWholeWord;
                    UpdateFindToggleButtonStyles();
                    RecomputeFindMatches();
                    e.Handled = true;
                }
                else if (alt && e.Key == Key.R)
                {
                    findUseRegex = !findUseRegex;
                    UpdateFindToggleButtonStyles();
                    RecomputeFindMatches();
                    e.Handled = true;
                }
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
                if (e.Key == Key.Enter) { ReplaceCurrentMatch(); e.Handled = true; }
                else if (e.Key == Key.Escape) { HideFindBar(); e.Handled = true; }
            };

            mcBtn.Click += (s, e) => { findMatchCase = !findMatchCase; UpdateFindToggleButtonStyles(); RecomputeFindMatches(); };
            wwBtn.Click += (s, e) => { findWholeWord = !findWholeWord; UpdateFindToggleButtonStyles(); RecomputeFindMatches(); };
            rxBtn.Click += (s, e) => { findUseRegex = !findUseRegex; UpdateFindToggleButtonStyles(); RecomputeFindMatches(); };
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
            void Style(Button b, bool on)
            {
                if (b == null) return;
                b.Background = on ? WorkbenchStyles.B(UiTokens.ListActiveSelection(t, UiTokens.InputBackground(t))) : Brushes.Transparent;
                b.BorderBrush = on ? WorkbenchStyles.B(t.AccentPrimary) : Brushes.Transparent;
                WorkbenchStyles.ThemeIconButton(b, t, on);
            }
            Style(matchCaseBtn, findMatchCase);
            Style(wholeWordBtn, findWholeWord);
            Style(regexBtn, findUseRegex);
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
        public static int DisplayTabSize = 4;

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

        private static int CellsForChar(string s, int i)
        {
            char c = s[i];
            if (char.IsLowSurrogate(c)) return 0; // counted with its high surrogate
            if (char.IsHighSurrogate(c))
            {
                if (i + 1 < s.Length && char.IsLowSurrogate(s[i + 1]))
                {
                    int cp = char.ConvertToUtf32(c, s[i + 1]);
                    if (Vt100TerminalBuffer.IsZeroWidthAstral(cp)) return 0;
                    return Vt100TerminalBuffer.IsWideAstral(cp) ? 2 : 1;
                }
                return 1;
            }
            if (Vt100TerminalBuffer.IsZeroWidthBmp(c)) return 0;
            return Vt100TerminalBuffer.IsWideBmp(c) ? 2 : 1;
        }

        /// <summary>Visual column (in cells) of character index <paramref name="col"/> in <paramref name="line"/>.</summary>
        public static int VisualColumn(string line, int col)
        {
            if (string.IsNullOrEmpty(line) || col <= 0) return Math.Max(0, col);
            int tab = Math.Max(1, DisplayTabSize);
            int v = 0;
            int n = Math.Min(col, line.Length);
            for (int i = 0; i < n; i++)
            {
                if (line[i] == '\t') v += tab - (v % tab);
                else v += CellsForChar(line, i);
            }
            return v + Math.Max(0, col - line.Length); // virtual space past end of line
        }

        /// <summary>Character index nearest to visual column <paramref name="vcol"/> (inverse of VisualColumn).</summary>
        public static int CharIndexForVisualColumn(string line, double vcol)
        {
            if (string.IsNullOrEmpty(line) || vcol <= 0) return 0;
            int tab = Math.Max(1, DisplayTabSize);
            int v = 0;
            for (int i = 0; i < line.Length; i++)
            {
                int w = line[i] == '\t' ? tab - (v % tab) : CellsForChar(line, i);
                if (w == 0) continue;
                if (vcol < v + w / 2.0) return i;
                v += w;
            }
            return line.Length;
        }

        /// <summary>X offset (relative to the start of the code area) of a character index on a document line.</summary>
        private double ColX(EditorTabBuffer buf, int docLine, int col)
        {
            if (buf == null || docLine < 0 || docLine >= buf.Lines.Count) return col * CharWidth;
            return VisualColumn(buf.Lines[docLine], col) * CharWidth;
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
        private bool caretBlinkOn = true;
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

            blinkTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(530) };
            blinkTimer.Tick += (s, e) =>
            {
                caretBlinkOn = !caretBlinkOn;
                if (IsVisible) InvalidateVisual();
            };
            blinkTimer.Start();

            AttachedToVisualTree += (s, e) => { if (!blinkTimer.IsEnabled) blinkTimer.Start(); };
            DetachedFromVisualTree += (s, e) => { if (blinkTimer.IsEnabled) blinkTimer.Stop(); };
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

        public void EnsureCursorVisible()
        {
            var buf = Buffer;
            if (buf == null) return;
            int visRows = Math.Max(1, (int)(Bounds.Height / LineHeight) - 1);
            if (buf.CursorLine < buf.ScrollTopLine)
                buf.ScrollTopLine = Math.Max(0, buf.CursorLine);
            else if (buf.CursorLine >= buf.ScrollTopLine + visRows)
                buf.ScrollTopLine = Math.Max(0, buf.CursorLine - visRows + 1);
            // Horizontal: keep the caret inside the viewport when typing past the right edge (was never scrolled).
            if (!buf.WordWrap && Bounds.Width > GutterWidth + 40)
            {
                double caretX = ColX(buf, buf.CursorLine, buf.CursorCol);
                double viewW = Bounds.Width - GutterWidth - 24;
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
                InvalidateVisual();
            }
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
            for (int i = 0; i < buf.Lines.Count; i++)
            {
                if (CanFoldLine(buf, i))
                {
                    int endLine = FindFoldEndLine(buf, i);
                    if (endLine > i)
                        buf.FoldedBlockRanges[i] = endLine;
                }
            }
            InvalidateVisual();
        }

        public void UnfoldAll()
        {
            var buf = Buffer;
            if (buf == null) return;
            buf.FoldedBlockRanges.Clear();
            InvalidateVisual();
        }

        public static int FindFoldEndLine(EditorTabBuffer buf, int startLine)
        {
            if (buf == null || startLine < 0 || startLine >= buf.Lines.Count) return startLine;
            string first = buf.Lines[startLine] ?? "";
            if (first.Contains("{"))
            {
                int depth = 0;
                for (int i = startLine; i < buf.Lines.Count; i++)
                {
                    foreach (char c in buf.Lines[i])
                    {
                        if (c == '{') depth++;
                        else if (c == '}')
                        {
                            depth--;
                            if (depth == 0 && i > startLine) return i;
                        }
                    }
                }
            }
            int baseIndent = GetLeadingSpaces(first);
            int lastSub = startLine;
            for (int i = startLine + 1; i < buf.Lines.Count; i++)
            {
                string ln = buf.Lines[i];
                if (string.IsNullOrWhiteSpace(ln)) continue;
                if (GetLeadingSpaces(ln) > baseIndent) lastSub = i;
                else break;
            }
            return lastSub;
        }

        private static int GetLeadingSpaces(string s)
        {
            if (string.IsNullOrEmpty(s)) return 0;
            int n = 0;
            foreach (char c in s)
            {
                if (c == ' ') n++;
                else if (c == '\t') n += 4;
                else break;
            }
            return n;
        }

        private List<int> GetVisibleDocumentLines()
        {
            var buf = Buffer;
            var list = new List<int>();
            if (buf == null) return list;
            int line = 0;
            while (line < buf.Lines.Count)
            {
                list.Add(line);
                if (buf.FoldedBlockRanges != null && buf.FoldedBlockRanges.TryGetValue(line, out int foldEnd) && foldEnd > line)
                {
                    line = foldEnd + 1;
                }
                else
                {
                    line++;
                }
            }
            return list;
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
                    InvalidateVisual();
                }
                e.Handled = true;
                return;
            }
            double dy = e.Delta.Y;
            double dx = e.Delta.X;
            if (e.KeyModifiers.HasFlag(KeyModifiers.Shift) && Math.Abs(dx) < 0.001) { dx = dy; dy = 0; } // Shift+wheel scrolls horizontally
            // Fractional accumulation: truncating each small trackpad delta to 0 lines made scrolling dead/jerky.
            wheelAccumY += -dy * 3;
            int deltaLines = (int)wheelAccumY;
            wheelAccumY -= deltaLines;
            if (deltaLines != 0)
                buf.ScrollTopLine = Math.Max(0, Math.Min(Math.Max(0, buf.Lines.Count - 1), buf.ScrollTopLine + deltaLines));
            if (Math.Abs(dx) > 0.001)
            {
                int longest = 0;
                int first = Math.Max(0, buf.ScrollTopLine);
                for (int i = first; i < Math.Min(buf.Lines.Count, first + 400); i++) longest = Math.Max(longest, buf.Lines[i].Length);
                double maxPx = Math.Max(0, longest * CharWidth - Math.Max(0, Bounds.Width - GutterWidth - 40));
                buf.ScrollLeftPx = Math.Max(0, Math.Min(maxPx, buf.ScrollLeftPx - dx * CharWidth * 3));
            }
            InvalidateVisual();
            e.Handled = true;
        }

        private double wheelAccumY;
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

        private double CodeAreaRight => ShowMinimap ? Math.Max(GutterWidth + 60, Bounds.Width - MinimapWidth) : Bounds.Width;

        /// <summary>The character the pointer is over (not the nearest caret boundary); -1 past the end of the line.</summary>
        private int CharIndexUnderPoint(int docLine, double x)
        {
            var buf = Buffer;
            if (buf == null || docLine < 0 || docLine >= buf.Lines.Count) return -1;
            string line = buf.Lines[docLine] ?? "";
            double vcol = (x - GutterWidth - 8 + buf.ScrollLeftPx) / CharWidth;
            if (vcol < 0) return -1;
            int tab = Math.Max(1, DisplayTabSize);
            int v = 0;
            for (int i = 0; i < line.Length; i++)
            {
                int w = line[i] == '\t' ? tab - (v % tab) : CellsForChar(line, i);
                if (w == 0) continue;
                if (vcol < v + w) return i;
                v += w;
            }
            return -1;
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
            if (ShowMinimap && pt.X >= Bounds.Width - MinimapWidth) { SetPointerCursor(StandardCursorType.Arrow); return; }
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
            var props = e.GetCurrentPoint(this).Properties;
            var mods = e.KeyModifiers;

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
            double minimapX = Bounds.Width - (ShowMinimap ? MinimapWidth : 0);
            if (ShowMinimap && pt.X >= minimapX)
            {
                isMinimapDragging = true;
                ScrollFromMinimapY(pt.Y);
                e.Handled = true;
                return;
            }

            int docLine = ScreenYToDocLine(pt.Y);
            int docCol = ScreenXToDocCol(docLine, pt.X);

            if (pt.X < GutterWidth)
            {
                // Glyph margin (left of the line numbers): toggle a breakpoint, like VS Code.
                if (pt.X < EditorDebugHooks.GlyphMarginWidth && owner.RaiseBreakpointGutter(buf, docLine, false)) { e.Handled = true; return; }
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
                caretBlinkOn = true;
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
                caretBlinkOn = true;
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
                ApplyColumnBoxSelection(buf.CursorLine, buf.CursorCol, docLine, docCol);
                InvalidateVisual();
                e.Handled = true;
                return;
            }

            ExtraCursors.Clear();
            if (e.KeyModifiers.HasFlag(KeyModifiers.Shift))
            {
                if (!buf.HasSelection())
                {
                    buf.SelectionStartLine = buf.CursorLine;
                    buf.SelectionStartCol = buf.CursorCol;
                }
                buf.CursorLine = docLine;
                buf.CursorCol = docCol;
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
                buf.ClearSelection();
                isMouseSelecting = true;
                e.Pointer.Capture(this);
            }

            caretBlinkOn = true;
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
            PointerMovedOverCanvas?.Invoke(pt);

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
                    int anchorL = buf.SelectionStartLine >= 0 ? buf.SelectionStartLine : buf.CursorLine;
                    int anchorC = buf.SelectionStartCol >= 0 ? buf.SelectionStartCol : buf.CursorCol;
                    ApplyColumnBoxSelection(anchorL, anchorC, docLine, docCol);
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
                }
                // Horizontal follow while dragging past the left / right edge.
                if (!buf.WordWrap && (pt.X > CodeAreaRight - 8 || (pt.X < GutterWidth + 8 && buf.ScrollLeftPx > 0)))
                    EnsureCursorVisible();
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
                    int maxTop = Math.Max(0, b.Lines.Count - 1);
                    b.ScrollTopLine = Math.Max(0, Math.Min(maxTop, b.ScrollTopLine + (over < 0 ? -step : step)));
                    ApplyDragSelection(lastDragPt, lastDragMods);
                };
            }
            if (!dragScrollTimer.IsEnabled) dragScrollTimer.Start();
        }

        protected override void OnPointerReleased(PointerReleasedEventArgs e)
        {
            base.OnPointerReleased(e);
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
            buf.ScrollTopLine = (int)(ratio * Math.Max(0, buf.Lines.Count - 1));
            InvalidateVisual();
        }

        private int ScreenYToDocLine(double y)
        {
            var buf = Buffer;
            if (buf == null || buf.Lines.Count == 0) return 0;
            int rowOffset = Math.Max(0, (int)(y / LineHeight));
            if (buf.FoldedBlockRanges == null || buf.FoldedBlockRanges.Count == 0)
            {
                return Math.Max(0, Math.Min(buf.Lines.Count - 1, buf.ScrollTopLine + rowOffset));
            }
            var visLines = GetVisibleDocumentLines();
            int startIdx = visLines.BinarySearch(buf.ScrollTopLine);
            if (startIdx < 0) startIdx = Math.Min(visLines.Count - 1, ~startIdx);
            int targetIdx = Math.Max(0, Math.Min(visLines.Count - 1, startIdx + rowOffset));
            return visLines[targetIdx];
        }

        private int ScreenXToDocCol(int docLine, double x)
        {
            var buf = Buffer;
            if (buf == null || docLine < 0 || docLine >= buf.Lines.Count) return 0;
            double relX = Math.Max(0, x - GutterWidth - 8 + buf.ScrollLeftPx);
            int col = CharIndexForVisualColumn(buf.Lines[docLine], relX / CharWidth);
            return Math.Max(0, Math.Min(buf.Lines[docLine].Length, col));
        }

        private bool CanFoldLine(EditorTabBuffer buf, int lineIdx)
        {
            if (buf == null || lineIdx < 0 || lineIdx >= buf.Lines.Count) return false;
            if (buf.FoldedBlockRanges.ContainsKey(lineIdx)) return true;
            string ln = (buf.Lines[lineIdx] ?? "").TrimEnd();
            return ln.EndsWith("{") || ln.EndsWith(":");
        }

        public void ApplyColumnBoxSelection(int startLine, int startCol, int endLine, int endCol)
        {
            var buf = Buffer;
            if (buf == null) return;
            ExtraCursors.Clear();
            int l0 = Math.Min(startLine, endLine);
            int l1 = Math.Max(startLine, endLine);
            int c0 = Math.Min(startCol, endCol);
            int c1 = Math.Max(startCol, endCol);

            buf.SelectionStartLine = l0;
            buf.SelectionStartCol = Math.Min(c0, buf.Lines[l0].Length);
            buf.CursorLine = l0;
            buf.CursorCol = Math.Min(c1, buf.Lines[l0].Length);

            for (int l = l0 + 1; l <= l1 && l < buf.Lines.Count; l++)
            {
                int len = buf.Lines[l].Length;
                ExtraCursors.Add(new ExtraCursorCaret
                {
                    AnchorLine = l,
                    AnchorCol = Math.Min(c0, len),
                    Line = l,
                    Col = Math.Min(c1, len)
                });
            }
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
            int targetCol = Math.Min(refCol, buf.Lines[targetLine].Length);
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

        public void AddNextOccurrenceOrSelectWord()
        {
            var buf = Buffer;
            if (buf == null) return;
            if (!buf.HasSelection())
            {
                SelectWordAt(buf.CursorLine, buf.CursorCol);
                owner.NotifyCaretMoved();
                InvalidateVisual();
                return;
            }

            string needle = buf.GetSelectedText();
            if (string.IsNullOrEmpty(needle) || needle.Contains("\n")) return;

            int searchStartLine = ExtraCursors.Count > 0 ? ExtraCursors[ExtraCursors.Count - 1].Line : buf.CursorLine;
            int searchStartCol = ExtraCursors.Count > 0 ? ExtraCursors[ExtraCursors.Count - 1].Col : buf.CursorCol;

            for (int pass = 0; pass < 2; pass++)
            {
                int lStart = (pass == 0) ? searchStartLine : 0;
                int lEnd = (pass == 0) ? buf.Lines.Count - 1 : searchStartLine;
                for (int l = lStart; l <= lEnd; l++)
                {
                    string line = buf.Lines[l];
                    int cStart = (pass == 0 && l == searchStartLine) ? searchStartCol : 0;
                    int idx = line.IndexOf(needle, cStart, StringComparison.Ordinal);
                    while (idx >= 0)
                    {
                        int endCol = idx + needle.Length;
                        bool isPrimary = (l == buf.CursorLine && endCol == buf.CursorCol);
                        bool inExtra = ExtraCursors.Any(c => c.Line == l && c.Col == endCol);
                        if (!isPrimary && !inExtra)
                        {
                            ExtraCursors.Add(new ExtraCursorCaret
                            {
                                AnchorLine = l,
                                AnchorCol = idx,
                                Line = l,
                                Col = endCol
                            });
                            owner.NotifyCaretMoved();
                            InvalidateVisual();
                            return;
                        }
                        idx = line.IndexOf(needle, idx + 1, StringComparison.Ordinal);
                    }
                }
            }
        }

        public void SelectAllFindOccurrences()
        {
            var buf = Buffer;
            if (buf == null) return;
            string needle = buf.HasSelection() ? buf.GetSelectedText() : "";
            if (string.IsNullOrEmpty(needle))
            {
                SelectWordAt(buf.CursorLine, buf.CursorCol);
                needle = buf.GetSelectedText();
            }
            if (string.IsNullOrEmpty(needle) || needle.Contains("\n")) return;

            ExtraCursors.Clear();
            bool firstSet = false;
            for (int l = 0; l < buf.Lines.Count; l++)
            {
                string line = buf.Lines[l];
                int pos = 0;
                while (pos <= line.Length - needle.Length)
                {
                    int idx = line.IndexOf(needle, pos, StringComparison.Ordinal);
                    if (idx < 0) break;
                    if (!firstSet)
                    {
                        buf.SelectionStartLine = l;
                        buf.SelectionStartCol = idx;
                        buf.CursorLine = l;
                        buf.CursorCol = idx + needle.Length;
                        firstSet = true;
                    }
                    else
                    {
                        ExtraCursors.Add(new ExtraCursorCaret
                        {
                            AnchorLine = l,
                            AnchorCol = idx,
                            Line = l,
                            Col = idx + needle.Length
                        });
                    }
                    pos = idx + needle.Length;
                }
            }
            owner.NotifyCaretMoved();
            InvalidateVisual();
        }

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
                SelectWordAt(buf.CursorLine, Math.Min(buf.Lines[buf.CursorLine].Length, GetLeadingSpaces(buf.Lines[buf.CursorLine])));
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

        public void ScrollLines(int delta)
        {
            var buf = Buffer;
            if (buf == null) return;
            buf.ScrollTopLine = Math.Max(0, Math.Min(Math.Max(0, buf.Lines.Count - 1), buf.ScrollTopLine + delta));
            InvalidateVisual();
        }

        public void MoveActiveLine(int direction)
        {
            var buf = Buffer;
            if (buf == null) return;
            int l = buf.CursorLine;
            int target = l + direction;
            if (target < 0 || target >= buf.Lines.Count) return;
            buf.PushUndoSnapshot();
            string tmp = buf.Lines[l];
            buf.Lines[l] = buf.Lines[target];
            buf.Lines[target] = tmp;
            buf.CursorLine = target;
            FinalizeBufferEdit(buf);
        }

        public void CopyActiveLine(int direction)
        {
            var buf = Buffer;
            if (buf == null) return;
            int l = buf.CursorLine;
            buf.PushUndoSnapshot();
            string copy = buf.Lines[l];
            int insertAt = direction < 0 ? l : l + 1;
            buf.Lines.Insert(insertAt, copy);
            if (direction > 0) buf.CursorLine = l + 1;
            FinalizeBufferEdit(buf);
        }

        public void DeleteActiveLine()
        {
            var buf = Buffer;
            if (buf == null) return;
            buf.PushUndoSnapshot();
            if (buf.Lines.Count <= 1)
            {
                buf.Lines[0] = "";
                buf.CursorLine = 0;
                buf.CursorCol = 0;
            }
            else
            {
                buf.Lines.RemoveAt(buf.CursorLine);
                if (buf.CursorLine >= buf.Lines.Count)
                    buf.CursorLine = buf.Lines.Count - 1;
                buf.CursorCol = Math.Min(buf.CursorCol, buf.Lines[buf.CursorLine].Length);
            }
            buf.ClearSelection();
            FinalizeBufferEdit(buf);
        }

        public void ToggleLineComment()
        {
            var buf = Buffer;
            if (buf == null) return;
            buf.PushUndoSnapshot();
            string ext = (buf.Language ?? "").ToLowerInvariant();
            string prefix = (ext == "py" || ext == "sh" || ext == "zsh" || ext == "ps1" || ext == "yaml" || ext == "yml" || ext == "toml") ? "# " : "// ";
            string rawPrefix = prefix.TrimEnd();

            int l0 = buf.HasSelection() ? Math.Min(buf.SelectionStartLine, buf.CursorLine) : buf.CursorLine;
            int l1 = buf.HasSelection() ? Math.Max(buf.SelectionStartLine, buf.CursorLine) : buf.CursorLine;

            bool allCommented = true;
            for (int l = l0; l <= l1; l++)
            {
                string trimmed = buf.Lines[l].TrimStart();
                if (trimmed.Length > 0 && !trimmed.StartsWith(rawPrefix, StringComparison.Ordinal))
                {
                    allCommented = false;
                    break;
                }
            }

            for (int l = l0; l <= l1; l++)
            {
                string line = buf.Lines[l];
                if (allCommented)
                {
                    int idx = line.IndexOf(prefix, StringComparison.Ordinal);
                    if (idx >= 0)
                        buf.Lines[l] = line.Remove(idx, prefix.Length);
                    else
                    {
                        idx = line.IndexOf(rawPrefix, StringComparison.Ordinal);
                        if (idx >= 0) buf.Lines[l] = line.Remove(idx, rawPrefix.Length);
                    }
                }
                else
                {
                    buf.Lines[l] = prefix + line;
                }
            }
            FinalizeBufferEdit(buf);
        }

        public void ToggleBlockComment()
        {
            var buf = Buffer;
            if (buf == null) return;
            buf.PushUndoSnapshot();
            if (buf.HasSelection())
            {
                string sel = buf.GetSelectedText();
                if (sel.StartsWith("/*") && sel.EndsWith("*/") && sel.Length >= 4)
                    buf.ReplaceSelection(sel.Substring(2, sel.Length - 4));
                else
                    buf.ReplaceSelection("/*" + sel + "*/");
            }
            else
            {
                string ln = buf.Lines[buf.CursorLine];
                if (ln.Trim().StartsWith("/*") && ln.Trim().EndsWith("*/"))
                    buf.Lines[buf.CursorLine] = ln.Replace("/*", "").Replace("*/", "");
                else
                    buf.Lines[buf.CursorLine] = "/* " + ln + " */";
            }
            FinalizeBufferEdit(buf);
        }

        public void IndentOrOutdentLines(bool outdent)
        {
            var buf = Buffer;
            if (buf == null) return;
            buf.PushUndoSnapshot();
            int l0 = buf.HasSelection() ? Math.Min(buf.SelectionStartLine, buf.CursorLine) : buf.CursorLine;
            int l1 = buf.HasSelection() ? Math.Max(buf.SelectionStartLine, buf.CursorLine) : buf.CursorLine;
            for (int l = l0; l <= l1; l++)
            {
                if (outdent)
                {
                    string ln = buf.Lines[l];
                    int removeCount = 0;
                    while (removeCount < 4 && removeCount < ln.Length && ln[removeCount] == ' ')
                        removeCount++;
                    if (removeCount > 0)
                    {
                        buf.Lines[l] = ln.Substring(removeCount);
                        if (l == buf.CursorLine) buf.CursorCol = Math.Max(0, buf.CursorCol - removeCount);
                    }
                }
                else
                {
                    buf.Lines[l] = "    " + buf.Lines[l];
                    if (l == buf.CursorLine) buf.CursorCol += 4;
                }
            }
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

        private void FinalizeBufferEdit(EditorTabBuffer buf)
        {
            if (buf == null) return;
            bool deferHeavyAnalysis = AvaloniaTerminalEmulatorControl.IsDesktopRuntime && buf.Lines != null && buf.Lines.Count > 250;
            buf.SyncContentFromLines(recomputeGutterDiff: !deferHeavyAnalysis);
            buf.IsDirty = true;
            if (!deferHeavyAnalysis && !buf.IsLargeFile)
            {
                LlmHarnessAndAgentEngine.AnalyzeBufferAstAndDiagnostics(buf);
            }
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
            if (ExtraCursors.Count == 0) { move(); return; }
            var primary = ReadPrimary(buf);
            var moved = new List<ExtraCursorCaret>();
            foreach (var ec in ExtraCursors.ToList())
            {
                if (ec.Line < 0 || ec.Line >= buf.Lines.Count) continue;
                WritePrimary(buf, new CaretState { Line = ec.Line, Col = ec.Col, AnchorLine = ec.HasSelection ? ec.AnchorLine : -1, AnchorCol = ec.HasSelection ? ec.AnchorCol : -1 });
                move();
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

        private void InsertTextAcrossCursors(string text)
        {
            var buf = Buffer;
            if (buf == null || string.IsNullOrEmpty(text)) return;
            bool coalesce = ExtraCursors.Count == 0 && text.Length == 1 && text[0] != '\n' && text[0] != '\r' && !buf.HasSelection();
            buf.PushUndoSnapshot(coalesceTyping: coalesce);
            buf.GhostTextSuggestion = "";
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
            ApplyEditToAllCarets(buf, (b, k) => b.PerformBackspace());
            FinalizeBufferEdit(buf);
        }

        private void PerformDeleteAcrossCursors()
        {
            var buf = Buffer;
            if (buf == null) return;
            buf.PushUndoSnapshot();
            ClampExtraCursors(buf);
            ApplyEditToAllCarets(buf, (b, k) => b.PerformDelete());
            FinalizeBufferEdit(buf);
        }

        /// <summary>Enter at every caret, keeping the line's own indentation (tabs stay tabs) plus one level after { or :.</summary>
        private void PerformNewlineAcrossCursors()
        {
            var buf = Buffer;
            if (buf == null) return;
            buf.PushUndoSnapshot();
            ClampExtraCursors(buf);
            ApplyEditToAllCarets(buf, (b, k) =>
            {
                string cur = b.Lines[b.CursorLine];
                int ws = 0;
                while (ws < cur.Length && (cur[ws] == ' ' || cur[ws] == '\t')) ws++;
                string indent = cur.Substring(0, Math.Min(ws, b.CursorCol));
                string beforeCaret = cur.Substring(0, Math.Min(cur.Length, b.CursorCol)).TrimEnd();
                if (beforeCaret.EndsWith("{") || beforeCaret.EndsWith(":") || beforeCaret.EndsWith("(") || beforeCaret.EndsWith("["))
                    indent += indent.Contains('\t') ? "\t" : new string(' ', Math.Max(1, DisplayTabSize));
                b.InsertTextAtCursor("\n" + indent);
            });
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
                if (e.Key == Key.Tab && !shift && !ctrlOrCmd && !alt)
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
                int endLine = ExtraCursors.Count > 0 ? ExtraCursors[ExtraCursors.Count - 1].Line : buf.CursorLine;
                int endCol = ExtraCursors.Count > 0 ? ExtraCursors[ExtraCursors.Count - 1].Col : buf.CursorCol;
                if (e.Key == Key.Down) endLine = Math.Min(buf.Lines.Count - 1, endLine + 1);
                else if (e.Key == Key.Up) endLine = Math.Max(0, endLine - 1);
                else if (e.Key == Key.Right) endCol++;
                else if (e.Key == Key.Left) endCol = Math.Max(0, endCol - 1);
                int startL = buf.SelectionStartLine >= 0 ? buf.SelectionStartLine : buf.CursorLine;
                int startC = buf.SelectionStartCol >= 0 ? buf.SelectionStartCol : buf.CursorCol;
                ApplyColumnBoxSelection(startL, startC, endLine, endCol);
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
                buf.PushUndoSnapshot();
                int insertAt = shift ? buf.CursorLine : buf.CursorLine + 1;
                buf.Lines.Insert(insertAt, "");
                buf.CursorLine = insertAt;
                buf.CursorCol = 0;
                buf.ClearSelection();
                FinalizeBufferEdit(buf);
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
                FoldAtLine(buf.CursorLine);
                e.Handled = true;
                return;
            }
            if (ctrlOrCmd && shift && e.Key == Key.OemCloseBrackets)
            {
                UnfoldAtLine(buf.CursorLine);
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
            if (alt && !ctrlOrCmd && !shift && e.Key == Key.Z)
            {
                ToggleWordWrap();
                e.Handled = true;
                return;
            }

            // Undo / Redo
            if (ctrlOrCmd && !alt && e.Key == Key.Z)
            {
                if (shift) buf.PerformRedo();
                else buf.PerformUndo();
                LlmHarnessAndAgentEngine.AnalyzeBufferAstAndDiagnostics(buf);
                owner.NotifyBufferModified();
                InvalidateVisual();
                e.Handled = true;
                return;
            }
            if (ctrlOrCmd && !shift && !alt && e.Key == Key.Y)
            {
                buf.PerformRedo();
                LlmHarnessAndAgentEngine.AnalyzeBufferAstAndDiagnostics(buf);
                owner.NotifyBufferModified();
                InvalidateVisual();
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
                string sel = buf.HasSelection() ? buf.GetSelectedText() : buf.Lines[buf.CursorLine] + "\n";
                var top = TopLevel.GetTopLevel(this);
                if (top?.Clipboard != null) await top.Clipboard.SetTextAsync(sel);
                return;
            }
            if (ctrlOrCmd && !shift && !alt && e.Key == Key.X)
            {
                e.Handled = true;
                string sel = buf.HasSelection() ? buf.GetSelectedText() : buf.Lines[buf.CursorLine] + "\n";
                var top = TopLevel.GetTopLevel(this);
                if (top?.Clipboard != null) await top.Clipboard.SetTextAsync(sel);
                if (buf.HasSelection())
                {
                    buf.PushUndoSnapshot();
                    buf.DeleteSelection();
                    FinalizeBufferEdit(buf);
                }
                else
                {
                    DeleteActiveLine();
                }
                e.Handled = true;
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
                        InsertTextAcrossCursors(clip);
                }
                return;
            }

            // Standard Editing & Navigation Keys
            switch (e.Key)
            {
                case Key.Back:
                    // macOS: Option+Backspace = word, Cmd+Backspace = to line start. Windows/Linux: Ctrl = word.
                    if (MacKeymap.IsMacOS && meta && !alt)
                        DeleteToLineBoundary(buf, toEnd: false);
                    else if (MacKeymap.IsMacOS ? alt : ctrlOrCmd)
                        DeleteWordLeft();
                    else
                        PerformBackspaceAcrossCursors();
                    e.Handled = true;
                    break;
                case Key.Delete:
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
                case Key.Enter:
                    if (!ctrlOrCmd)
                    {
                        PerformNewlineAcrossCursors();
                        e.Handled = true;
                    }
                    break;
                case Key.Tab:
                    if (shift || buf.HasSelection())
                        IndentOrOutdentLines(outdent: shift);
                    else
                        InsertTextAcrossCursors("    ");
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
            if (toEnd)
            {
                buf.CursorCol = line.Length;
            }
            else
            {
                int firstNonWs = GetFirstNonWhitespaceIndex(line);
                buf.CursorCol = (buf.CursorCol == firstNonWs) ? 0 : firstNonWs;
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
            SetSelectionAnchorIfNeeded(shift);
            buf.CursorLine = Math.Max(0, Math.Min(buf.Lines.Count - 1, buf.CursorLine + deltaLines));
            buf.CursorCol = Math.Min(buf.CursorCol, buf.Lines[buf.CursorLine].Length);
            EnsureCursorVisible();
            owner.NotifyCaretMoved();
            InvalidateVisual();
        }

        protected override void OnTextInput(TextInputEventArgs e)
        {
            base.OnTextInput(e);
            if (string.IsNullOrEmpty(e.Text)) return;
            char ch = e.Text[0];
            if (ch < 32 && ch != '\t') return;
            HidePointerWhileTyping();
            InsertTextAcrossCursors(e.Text);
            e.Handled = true;
            TextTyped?.Invoke(e.Text);
        }

        /// <summary>Called before the editor handles a key; return true to swallow it (LSP completion / signature popups).</summary>
        public Func<KeyEventArgs, bool> PreviewKeyHandler;
        /// <summary>Raised after typed text was inserted at the caret (as-you-type completion, signature help triggers).</summary>
        public event Action<string> TextTyped;
        /// <summary>Raised on every pointer move over the canvas (canvas coordinates); used for LSP hover.</summary>
        public event Action<Point> PointerMovedOverCanvas;
        /// <summary>Raised when the pointer leaves the canvas, the view scrolls or focus is lost.</summary>
        public event Action PointerLeftCanvas;

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
            annotationHitBoxes.Clear();
            if (buf.ScrollTopLine != lastNotifiedScrollTop)
            {
                lastNotifiedScrollTop = buf.ScrollTopLine;
                Dispatcher.UIThread.Post(() => owner.NotifyScrollChanged(), DispatcherPriority.Background);
            }
            var annotationProvider = owner.LineAnnotationProvider;
            double codeAreaRight = ShowMinimap ? Math.Max(GutterWidth + 60, w - MinimapWidth) : w;

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

            // Bracket match calculation (bounded to ±1500 lines)
            int matchBLine = -1, matchBCol = -1;
            TryFindMatchingBracket(buf, buf.CursorLine, buf.CursorCol, out matchBLine, out matchBCol);

            bool hasFolds = buf.FoldedBlockRanges != null && buf.FoldedBlockRanges.Count > 0;
            List<int> visLines = hasFolds ? GetVisibleDocumentLines() : null;
            int totalVisCount = hasFolds ? visLines.Count : buf.Lines.Count;
            int startVisIdx;
            if (hasFolds)
            {
                startVisIdx = visLines.BinarySearch(buf.ScrollTopLine);
                if (startVisIdx < 0) startVisIdx = Math.Min(Math.Max(0, visLines.Count - 1), ~startVisIdx);
            }
            else
            {
                startVisIdx = Math.Max(0, Math.Min(Math.Max(0, buf.Lines.Count - 1), buf.ScrollTopLine));
            }

            int maxRows = (int)(h / LineHeight) + 2;
            double codeBaseX = GutterWidth + 8 - buf.ScrollLeftPx;
            int firstHorizCol = Math.Max(0, (int)(buf.ScrollLeftPx / CharWidth) - 8);
            int maxHorizCols = Math.Max(120, (int)((codeAreaRight - GutterWidth) / CharWidth) + 24);

            for (int r = 0; r < maxRows && (startVisIdx + r) < totalVisCount; r++)
            {
                int docLine = hasFolds ? visLines[startVisIdx + r] : (startVisIdx + r);
                double y = r * LineHeight;
                string lineText = buf.Lines[docLine] ?? "";

                // Active line highlight
                if (docLine == buf.CursorLine)
                {
                    context.FillRectangle(lineHlBrush, new Rect(GutterWidth + 1, y, codeAreaRight - GutterWidth - 1, LineHeight));
                }
                if (buf.DebugStoppedLine == docLine) DrawDebugStoppedLineBackground(context, buf, y, codeAreaRight);

                // Proposed inline diff highlight & per-hunk Accept/Reject pills
                bool isProposedDiffLine = buf.ProposedNewLines != null && buf.ProposedNewLines.Contains(docLine);
                if (isProposedDiffLine)
                {
                    context.FillRectangle(new SolidColorBrush(Color.FromArgb(42, 34, 197, 94)), new Rect(GutterWidth + 1, y, codeAreaRight - GutterWidth - 1, LineHeight));
                    bool isFirstInHunk = !buf.ProposedNewLines.Contains(docLine - 1);
                    if (isFirstInHunk)
                    {
                        double pillRight = codeAreaRight - 10;
                        var rejectRect = new Rect(pillRight - 58, y + 2, 54, LineHeight - 4);
                        var acceptRect = new Rect(rejectRect.X - 62, y + 2, 58, LineHeight - 4);
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
                }

                // Selection highlight on this line (primary + extra cursors)
                DrawLineSelectionHighlight(context, buf, docLine, y, codeBaseX, selBgBrush);

                // Find match highlights on this line (O(log M) binary search instead of linear scan)
                if (owner.FindMatches.Count > 0)
                {
                    int fmIdx = LowerBoundFindMatchLine(owner.FindMatches, docLine);
                    while (fmIdx < owner.FindMatches.Count && owner.FindMatches[fmIdx].Line == docLine)
                    {
                        var fm = owner.FindMatches[fmIdx++];
                        double fx = codeBaseX + ColX(buf, docLine, fm.StartCol);
                        double fw = Math.Max(CharWidth, ColX(buf, docLine, fm.StartCol + fm.Length) - ColX(buf, docLine, fm.StartCol));
                        if (fx + fw >= GutterWidth && fx <= codeAreaRight)
                            context.FillRectangle(findMatchBrush, new Rect(fx, y + 1, fw, LineHeight - 2));
                    }
                }

                // Gutter line number
                var lnFt = new FormattedText(
                    (docLine + 1).ToString(CultureInfo.InvariantCulture),
                    CultureInfo.InvariantCulture,
                    FlowDirection.LeftToRight,
                    docLine == buf.CursorLine ? fontBold : fontRegular,
                    FontSizePx - 1.5,
                    docLine == buf.CursorLine ? textPriBrush : textSecBrush);
                context.DrawText(lnFt, new Point(Math.Max(4, GutterWidth - 22 - lnFt.Width), y + (LineHeight - lnFt.Height) / 2.0));
                DrawDebugGutterGlyphs(context, buf, docLine, y);

                // Gutter Git diff indicator bar (O(1) HashSet lookup, zero dictionary allocations)
                if (buf.GutterAddedLines != null && buf.GutterAddedLines.Contains(docLine))
                {
                    context.FillRectangle(gitAddBrush, new Rect(GutterWidth - 5, y + 1, 3, LineHeight - 2));
                }
                else if (buf.GutterModifiedLines != null && buf.GutterModifiedLines.Contains(docLine))
                {
                    context.FillRectangle(gitModBrush, new Rect(GutterWidth - 5, y + 1, 3, LineHeight - 2));
                }
                if (buf.GutterDeletedAfterLine != null && buf.GutterDeletedAfterLine.Contains(docLine))
                {
                    context.FillRectangle(gitDelBrush, new Rect(GutterWidth - 7, y + LineHeight - 3, 6, 3));
                }

                // Gutter folding chevron
                if (CanFoldLine(buf, docLine))
                {
                    bool isFolded = hasFolds && buf.FoldedBlockRanges.ContainsKey(docLine);
                    AvaloniaVectorIcons.DrawIcon(
                        context,
                        isFolded ? "chevron-right" : "chevron-down",
                        new Rect(GutterWidth - 17, y + (LineHeight - 10) / 2.0, 10, 10),
                        textSecondary,
                        1.4f);
                }

                // Syntax-highlighted tokens (horizontally windowed for ultra-long / minified lines)
                double curX = codeBaseX;
                string renderLineText = lineText;
                int renderVcol = 0;
                if (lineText.Length > 360)
                {
                    int sliceStart = Math.Min(lineText.Length, firstHorizCol);
                    int sliceLen = Math.Min(lineText.Length - sliceStart, maxHorizCols);
                    renderLineText = sliceLen > 0 ? lineText.Substring(sliceStart, sliceLen) : "";
                    renderVcol = VisualColumn(lineText, sliceStart);
                    curX = codeBaseX + renderVcol * CharWidth;
                }

                if (renderLineText.Length > 0)
                {
                    var tokens = TokenizeLineForTheme(renderLineText, Theme);
                    int tabStop = Math.Max(1, DisplayTabSize);
                    foreach (var tk in tokens)
                    {
                        var tf = tk.IsBold ? fontBold : (tk.IsItalic ? fontItalic : fontRegular);
                        var brush = new SolidColorBrush(tk.Color);
                        string t = tk.Text ?? "";
                        bool simple = true;
                        for (int ci = 0; ci < t.Length; ci++)
                        {
                            char ch = t[ci];
                            if (ch == '\t' || ch >= '\u1100') { simple = false; break; }
                        }
                        if (simple)
                        {
                            // Fast path: plain monospace run, one FormattedText positioned on the grid.
                            double x0 = codeBaseX + renderVcol * CharWidth;
                            double runW = t.Length * CharWidth;
                            if (x0 + runW >= GutterWidth && x0 <= codeAreaRight)
                            {
                                var ft = new FormattedText(t, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, tf, FontSizePx, brush);
                                context.DrawText(ft, new Point(x0, y + (LineHeight - ft.Height) / 2.0));
                            }
                            renderVcol += t.Length;
                        }
                        else
                        {
                            // Tabs / wide / astral characters: place each glyph cluster at its own cell.
                            for (int ci = 0; ci < t.Length; ci++)
                            {
                                char ch = t[ci];
                                if (ch == '\t') { renderVcol += tabStop - (renderVcol % tabStop); continue; }
                                int cells = CellsForChar(t, ci);
                                string glyph = (char.IsHighSurrogate(ch) && ci + 1 < t.Length) ? t.Substring(ci, 2) : ch.ToString();
                                double gx0 = codeBaseX + renderVcol * CharWidth;
                                if (gx0 + CharWidth * Math.Max(1, cells) >= GutterWidth && gx0 <= codeAreaRight && !char.IsLowSurrogate(ch))
                                {
                                    var gft = new FormattedText(glyph, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, tf, FontSizePx, brush);
                                    context.DrawText(gft, new Point(gx0, y + (LineHeight - gft.Height) / 2.0));
                                }
                                if (glyph.Length == 2) ci++;
                                renderVcol += cells;
                            }
                        }
                        curX = codeBaseX + renderVcol * CharWidth;
                        if (curX > codeAreaRight + 40) break;
                    }
                }

                // Folded block placeholder pill
                if (hasFolds && buf.FoldedBlockRanges.TryGetValue(docLine, out int foldedTo))
                {
                    var pillFt = new FormattedText(
                        $" ⋯ ({foldedTo - docLine} lines folded) ",
                        CultureInfo.InvariantCulture,
                        FlowDirection.LeftToRight,
                        fontItalic,
                        FontSizePx - 1.5,
                        accentBrush);
                    var pillRect = new Rect(curX + 6, y + 2, pillFt.Width + 6, LineHeight - 4);
                    context.DrawRectangle(new SolidColorBrush(Color.FromArgb(40, accent.R, accent.G, accent.B)), new Pen(accentBrush, 1), pillRect, 3, 3);
                    context.DrawText(pillFt, new Point(pillRect.X + 3, y + (LineHeight - pillFt.Height) / 2.0));
                }

                // End-of-line annotation (inline blame / code lens)
                if (annotationProvider != null && !(docLine == buf.CursorLine && !string.IsNullOrEmpty(buf.GhostTextSuggestion)) &&
                    !(hasFolds && buf.FoldedBlockRanges.ContainsKey(docLine)))
                {
                    EditorLineAnnotation ann = null;
                    try { ann = annotationProvider(buf, docLine); } catch { ann = null; }
                    if (ann != null && !string.IsNullOrEmpty(ann.Text))
                    {
                        double ax = codeBaseX + ColX(buf, docLine, lineText.Length) + CharWidth * 3;
                        if (lineText.Length == 0) ax = codeBaseX + CharWidth * (ann.IsAction ? 0 : 1);
                        if (ax < GutterWidth + 4) ax = GutterWidth + 4;
                        if (ax < codeAreaRight - 40)
                        {
                            bool hot = ReferenceEquals(ann, hoveredAnnotation) || (hoveredAnnotation != null && hoveredAnnotation.Tag != null && ReferenceEquals(hoveredAnnotation.Tag, ann.Tag));
                            IBrush annBrush = ann.IsAction
                                ? new SolidColorBrush(AvaloniaColorHelper.ToAvColor(Theme.AccentPrimary, (byte)(hot ? 255 : 200)))
                                : new SolidColorBrush(AvaloniaColorHelper.ToAvColor(Theme.TextSecondary, (byte)(hot ? 200 : 125)));
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

                // Ghost text suggestion after cursor
                if (docLine == buf.CursorLine && !string.IsNullOrEmpty(buf.GhostTextSuggestion))
                {
                    double gx = Math.Max(curX + (lineText.Length > 0 ? 8 : 0), codeBaseX + ColX(buf, docLine, buf.CursorCol));
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
                if (docLine == buf.CursorLine)
                {
                    double icx = codeBaseX + ColX(buf, docLine, buf.CursorCol);
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
                if (docLine == matchBLine && matchBCol >= 0)
                {
                    double bx = codeBaseX + ColX(buf, docLine, matchBCol);
                    context.DrawRectangle(null, new Pen(accentBrush, 1.3), new Rect(bx, y + 2, CharWidth + 1, LineHeight - 4));
                }

                // Cmd/Ctrl+hover link underline (VS Code "editor link")
                if (linkHover != null && docLine == linkHoverLine && linkHover.EndCol > linkHover.StartCol)
                {
                    double lx0 = codeBaseX + ColX(buf, docLine, linkHover.StartCol);
                    double lx1 = codeBaseX + ColX(buf, docLine, Math.Min(lineText.Length, linkHover.EndCol));
                    double ly = Math.Round(y + (LineHeight + FontSizePx) / 2.0 + 1.5) + 0.5;
                    if (lx1 > lx0)
                        context.DrawLine(new Pen(accentBrush, 1.0), new Point(lx0, ly), new Point(lx1, ly));
                }

                // Diagnostics squiggles on this line
                if (buf.Diagnostics != null && buf.Diagnostics.Count > 0)
                {
                    for (int di = 0; di < buf.Diagnostics.Count; di++)
                    {
                        var diag = buf.Diagnostics[di];
                        if (diag.LineNumber - 1 == docLine)
                        {
                            bool isErr = string.Equals(diag.Severity, "Error", StringComparison.OrdinalIgnoreCase);
                            Color sqCol = isErr
                                ? AvaloniaColorHelper.ToAvColor(Theme.DiagnosticError)
                                : AvaloniaColorHelper.ToAvColor(Theme.DiagnosticWarning);
                            int firstChar = GetLeadingSpaces(lineText);
                            double sx = codeBaseX + ColX(buf, docLine, firstChar);
                            double sw = Math.Max(CharWidth * 4, Math.Min(codeAreaRight - sx, ColX(buf, docLine, lineText.Length) - ColX(buf, docLine, firstChar)));
                            if (diag.IsLanguageServer && diag.EndColumn > 0)
                            {
                                // Language-server diagnostics carry an exact range: underline just that span.
                                int c0 = Math.Max(0, Math.Min(lineText.Length, diag.Column - 1));
                                int c1 = Math.Max(c0, Math.Min(lineText.Length, diag.EndColumn - 1));
                                sx = codeBaseX + ColX(buf, docLine, c0);
                                sw = Math.Max(CharWidth, Math.Min(codeAreaRight - sx, ColX(buf, docLine, c1) - ColX(buf, docLine, c0)));
                            }
                            if (sw > 0)
                                DrawSquiggleUnderline(context, sx, y + LineHeight - 3, sw, sqCol);
                        }
                    }
                }

                // Primary Caret & Extra Multi-Cursors on this line
                if (caretBlinkOn)
                {
                    if (docLine == buf.CursorLine)
                    {
                        double cx = codeBaseX + ColX(buf, docLine, buf.CursorCol);
                        if (!IsImeComposing)
                            context.FillRectangle(accentBrush, new Rect(cx, y + 2, 2.0, LineHeight - 4));
                    }
                    for (int eci = 0; eci < ExtraCursors.Count; eci++)
                    {
                        var ec = ExtraCursors[eci];
                        if (ec.Line == docLine)
                        {
                            double ecx = codeBaseX + ColX(buf, docLine, ec.Col);
                            context.FillRectangle(accentBrush, new Rect(ecx, y + 2, 2.0, LineHeight - 4));
                        }
                    }
                }
            }

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

            // Right-side Minimap
            if (ShowMinimap && w > GutterWidth + MinimapWidth + 80)
            {
                RenderMinimap(context, buf, w - MinimapWidth, MinimapWidth, h);
            }
        }

        private void DrawLineSelectionHighlight(DrawingContext context, EditorTabBuffer buf, int docLine, double y, double codeBaseX, IBrush selBrush)
        {
            if (buf.HasSelection())
            {
                int l0 = buf.SelectionStartLine, c0 = buf.SelectionStartCol;
                int l1 = buf.CursorLine, c1 = buf.CursorCol;
                if (l0 > l1 || (l0 == l1 && c0 > c1))
                {
                    int tl = l0; l0 = l1; l1 = tl;
                    int tc = c0; c0 = c1; c1 = tc;
                }
                if (docLine >= l0 && docLine <= l1)
                {
                    int startC = (docLine == l0) ? c0 : 0;
                    int endC = (docLine == l1) ? c1 : Math.Max(1, buf.Lines[docLine].Length);
                    if (endC > startC)
                    {
                        double sx0 = ColX(buf, docLine, startC);
                        context.FillRectangle(selBrush, new Rect(codeBaseX + sx0, y, Math.Max(CharWidth * 0.5, ColX(buf, docLine, endC) - sx0), LineHeight));
                    }
                }
            }

            foreach (var ec in ExtraCursors)
            {
                if (ec.HasSelection && ec.Line == docLine && ec.AnchorLine == docLine)
                {
                    int sc = Math.Min(ec.Col, ec.AnchorCol);
                    int ecCol = Math.Max(ec.Col, ec.AnchorCol);
                    if (ecCol > sc)
                    {
                        double esx = ColX(buf, docLine, sc);
                        context.FillRectangle(selBrush, new Rect(codeBaseX + esx, y, ColX(buf, docLine, ecCol) - esx, LineHeight));
                    }
                }
            }
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
                if ((c0 == '/' && c1 == '/') || c0 == '#')
                    barBrush = cmBrush;
                else if (ln.IndexOf('(', firstNonWs, Math.Min(80, nonWsLen)) >= 0)
                    barBrush = fnBrush;
                else if (c0 == 'p' || c0 == 'c' || c0 == 'd' || c0 == 'e' || c0 == 'f')
                    barBrush = kwBrush;

                context.FillRectangle(barBrush, new Rect(mx, my, mw, 1.6));
            }

            // Viewport slider highlight
            int visRows = Math.Max(1, (int)(h / LineHeight));
            double trackH = Math.Min(h - 4, maxMmLines * 3.0);
            double vpTop = ((double)buf.ScrollTopLine / totalLines) * Math.Max(10, trackH - 18);
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
