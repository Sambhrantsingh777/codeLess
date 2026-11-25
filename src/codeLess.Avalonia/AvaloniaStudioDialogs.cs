using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.VisualTree;
using codeLess.Studio;

namespace codeLess.AvaloniaApp
{
    public enum UnsavedPromptResult
    {
        SaveAll,
        DontSave,
        Cancel
    }

    public class PaletteEntryItem
    {
        public string Id { get; set; } = "";
        public string Category { get; set; } = "";
        public string Title { get; set; } = "";
        public string stringSubtitleOrPath { get; set; } = "";
        public string Shortcut { get; set; } = "";
        public Action Execute { get; set; }
        public string PreviewThemeId { get; set; } = "";
        /// <summary>Set for Quick Open file entries: choosing the entry raises FileOpenRequested (supports "file:line:col").</summary>
        public string FilePath { get; set; } = "";
        /// <summary>Recently opened / open editors: listed first when the Quick Open query is empty.</summary>
        public bool IsRecent { get; set; }

        public string DisplayLabel => string.IsNullOrEmpty(Category) ? Title : $"{Category}: {Title}";
    }

    public class AvaloniaCommandPaletteOverlay : Border
    {
        public TextBox SearchInput { get; }
        public ListBox ResultsListBox { get; }
        public TextBlock ModeHintLabel { get; }
        public List<PaletteEntryItem> AllEntries { get; private set; } = new List<PaletteEntryItem>();
        public List<PaletteEntryItem> FilteredEntries { get; private set; } = new List<PaletteEntryItem>();
        public string InitialPrefix { get; private set; } = ">";
        private string originalThemeIdBeforePreview = null;

        public event Action<string> ThemePreviewRequested;
        public event Action<int> GoToLineRequested;
        /// <summary>(path, 1-based line or 0, 1-based column or 0)</summary>
        public event Action<string, int, int> FileOpenRequested;
        public event Action<string, int, int> FileOpenToSideRequested;
        public List<string> RecentCommandTitles { get; } = new List<string>();
        private int pendingQuickOpenLine;
        private int pendingQuickOpenColumn;
        public event Action Closed;

        private StudioTheme currentTheme = StudioTheme.GetById(StudioTheme.DefaultThemeId);

        public AvaloniaCommandPaletteOverlay()
        {
            Width = 640;
            MaxHeight = 440;
            HorizontalAlignment = HorizontalAlignment.Center;
            VerticalAlignment = VerticalAlignment.Top;
            Margin = new Thickness(0, 40, 0, 0);
            CornerRadius = new CornerRadius(UiTokens.PopupRadius);
            BorderThickness = new Thickness(1);
            Padding = new Thickness(6, 6, 6, 6);
            IsVisible = false;
            ZIndex = 200;

            var rootStack = new StackPanel { Spacing = 4 };

            SearchInput = new TextBox
            {
                Watermark = "Type the name of a command, file, @symbol, #workspace-symbol, or :line...",
                FontSize = UiTokens.FontUi,
                Padding = new Thickness(10, 4),
                MinHeight = UiTokens.ControlHeight
            };

            ModeHintLabel = new TextBlock
            {
                Text = "Tip: '>' Commands  •  '@' File Symbols  •  '#' Workspace Symbols  •  ':' Go to Line",
                FontSize = UiTokens.FontSmall - 0.5,
                Margin = new Thickness(8, 2, 8, 2)
            };

            ResultsListBox = new ListBox
            {
                Height = 330,
                BorderThickness = new Thickness(0),
                Background = Brushes.Transparent
            };

            rootStack.Children.Add(SearchInput);
            rootStack.Children.Add(ModeHintLabel);
            rootStack.Children.Add(ResultsListBox);
            Child = rootStack;

            SearchInput.TextChanged += (s, e) => RefreshFilter();
            SearchInput.KeyDown += OnSearchKeyDown;
            ResultsListBox.SelectionChanged += (s, e) =>
            {
                int idx = ResultsListBox.SelectedIndex;
                if (idx >= 0 && idx < FilteredEntries.Count)
                {
                    var item = FilteredEntries[idx];
                    if (!string.IsNullOrEmpty(item.PreviewThemeId))
                        ThemePreviewRequested?.Invoke(item.PreviewThemeId);
                }
            };
            ResultsListBox.DoubleTapped += (s, e) => ExecuteSelected();
            // VS Code: a single click on a Quick Open / Command Palette row picks it (was double-click only).
            ResultsListBox.Tapped += (s, e) =>
            {
                var v = e.Source as Visual;
                var item = v as ListBoxItem ?? v?.FindAncestorOfType<ListBoxItem>();
                if (item == null) return;
                int idx = ResultsListBox.IndexFromContainer(item);
                if (idx >= 0) ResultsListBox.SelectedIndex = idx;
                ExecuteSelected();
                e.Handled = true;
            };
        }

        public void ApplyTheme(StudioTheme theme)
        {
            if (theme == null) return;
            currentTheme = theme;
            // Elevated quick-input widget: widget background, subtle border and a soft shadow (no accent frame).
            Background = WorkbenchStyles.B(UiTokens.WidgetBackground(theme));
            BorderBrush = WorkbenchStyles.B(UiTokens.WidgetBorder(theme));
            BoxShadow = new BoxShadows(new BoxShadow { OffsetY = 8, Blur = 24, Color = WorkbenchStyles.C(UiTokens.WidgetShadow(theme)) });
            ModeHintLabel.Foreground = AvaloniaColorHelper.ToBrush(theme.FgMuted);
            SearchInput.ClearValue(TextBox.BackgroundProperty);
            SearchInput.ClearValue(TextBox.BorderBrushProperty);
            SearchInput.Foreground = AvaloniaColorHelper.ToBrush(theme.TextPrimary);
            ApplyListSelectionResources(theme);
            RenderListItems();
        }

        /// <summary>Selection colors are resolved from the list's own resources so they follow the widget background.</summary>
        private void ApplyListSelectionResources(StudioTheme t)
        {
            var w = UiTokens.WidgetBackground(t);
            ResultsListBox.Resources["SystemControlHighlightListAccentLowBrush"] = WorkbenchStyles.B(UiTokens.ListActiveSelection(t, w));
            ResultsListBox.Resources["SystemControlHighlightListAccentMediumBrush"] = WorkbenchStyles.B(UiTokens.Blend(UiTokens.ListActiveSelection(t, w), t.FgPrimary, 0.04));
            ResultsListBox.Resources["SystemControlHighlightListAccentHighBrush"] = WorkbenchStyles.B(UiTokens.Blend(UiTokens.ListActiveSelection(t, w), t.FgPrimary, 0.08));
            ResultsListBox.Resources["SystemControlHighlightListLowBrush"] = WorkbenchStyles.B(UiTokens.Hover(t, w));
            ResultsListBox.Resources["SystemControlHighlightListMediumBrush"] = WorkbenchStyles.B(UiTokens.Pressed(t, w));
        }

        public void Open(string prefix, List<PaletteEntryItem> entries, string currentThemeId = null)
        {
            QuickOpenFileSearch = null;
            InitialPrefix = prefix ?? "";
            AllEntries = entries ?? new List<PaletteEntryItem>();
            originalThemeIdBeforePreview = currentThemeId;
            IsVisible = true;
            SearchInput.Text = InitialPrefix;
            SearchInput.CaretIndex = SearchInput.Text.Length;
            RefreshFilter();
            SearchInput.Focus();
        }

        /// <summary>Optional fast file search used by Quick Open for big workspaces (set by the host, cleared on Close).</summary>
        public Func<string, int, List<(PaletteEntryItem Entry, int Score)>> QuickOpenFileSearch { get; set; }

        public void Close(bool restorePreviewTheme = false)
        {
            QuickOpenFileSearch = null;
            if (!IsVisible) return;
            IsVisible = false;
            if (restorePreviewTheme && !string.IsNullOrEmpty(originalThemeIdBeforePreview))
            {
                ThemePreviewRequested?.Invoke(originalThemeIdBeforePreview);
            }
            originalThemeIdBeforePreview = null;
            Closed?.Invoke();
        }

        public void RefreshFilter()
        {
            string raw = SearchInput.Text ?? "";
            if (raw.StartsWith("?"))
            {
                ModeHintLabel.Text = "Quick Open Help — select a prefix to switch mode";
                FilteredEntries = new List<PaletteEntryItem>
                {
                    new PaletteEntryItem { Category = "...", Title = "Go to File", stringSubtitleOrPath = "Search files by name", Execute = () => { SearchInput.Text = ""; SearchInput.CaretIndex = 0; } },
                    new PaletteEntryItem { Category = ">", Title = "Show and Run Commands", stringSubtitleOrPath = "Execute workbench commands", Execute = () => { SearchInput.Text = ">"; SearchInput.CaretIndex = 1; } },
                    new PaletteEntryItem { Category = "@", Title = "Go to Symbol in Editor", stringSubtitleOrPath = "Jump to outline symbol in active file", Execute = () => { SearchInput.Text = "@"; SearchInput.CaretIndex = 1; } },
                    new PaletteEntryItem { Category = "#", Title = "Go to Symbol in Workspace", stringSubtitleOrPath = "Search symbols across workspace", Execute = () => { SearchInput.Text = "#"; SearchInput.CaretIndex = 1; } },
                    new PaletteEntryItem { Category = ":", Title = "Go to Line/Column", stringSubtitleOrPath = "Jump to a line number in active editor", Execute = () => { SearchInput.Text = ":"; SearchInput.CaretIndex = 1; } }
                };
                RenderListItems();
                ResultsListBox.SelectedIndex = 0;
                return;
            }
            string query = raw;
            if (query.StartsWith(">") || query.StartsWith("@") || query.StartsWith("#") || query.StartsWith(":"))
                query = query.Substring(1).Trim();
            else
                query = query.Trim();

            if (raw.StartsWith(":"))
            {
                ModeHintLabel.Text = "Type a line number and press Enter to jump (e.g. :42 or :42:10)";
            }
            else if (raw.StartsWith("@"))
            {
                ModeHintLabel.Text = "Go to Symbol in Editor (@)";
            }
            else if (raw.StartsWith("#"))
            {
                ModeHintLabel.Text = "Go to Symbol in Workspace (#)";
            }
            else if (raw.StartsWith(">"))
            {
                ModeHintLabel.Text = $"{AllEntries.Count} Commands Available — ↑↓ Navigate • Enter Run • Esc Close";
            }
            else
            {
                ModeHintLabel.Text = "Quick Open Files by Name (type '>' for commands, '@' for symbols, ':' for line)";
            }

            pendingQuickOpenLine = 0;
            pendingQuickOpenColumn = 0;
            bool fileMode = raw.Length == 0 || !(raw.StartsWith(">") || raw.StartsWith("@") || raw.StartsWith("#") || raw.StartsWith(":"));
            bool hasFileEntries = fileMode && (QuickOpenFileSearch != null || AllEntries.Any(en => !string.IsNullOrEmpty(en.FilePath)));
            if (hasFileEntries)
            {
                // Quick Open (Ctrl/Cmd+P): files only, fuzzy ranked, recent first, optional ":line[:col]" suffix.
                string fileQuery = WorkspaceSearchEngine.SplitQuickOpenLineSuffix(query, out pendingQuickOpenLine, out pendingQuickOpenColumn);
                var files = AllEntries.Where(en => !string.IsNullOrEmpty(en.FilePath));
                if (string.IsNullOrEmpty(fileQuery))
                {
                    var ordered = files.Where(en => en.IsRecent).Concat(files.Where(en => !en.IsRecent));
                    if (QuickOpenFileSearch != null) ordered = ordered.Concat(QuickOpenFileSearch("", 120).Select(x => x.Entry));
                    FilteredEntries = ordered.Take(120).ToList();
                }
                else
                {
                    string[] toks = fileQuery.Replace('\\', '/').Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                    FilteredEntries = files
                        .Select(en =>
                        {
                            int total = 0;
                            foreach (string t in toks)
                            {
                                int byName = WorkspaceSearchEngine.ComputeFuzzySubsequenceScore(en.Title, t);
                                int byPath = t.IndexOf('/') >= 0 || byName == 0
                                    ? WorkspaceSearchEngine.ComputeFuzzySubsequenceScore(en.stringSubtitleOrPath.Replace('\\', '/'), t) / 2
                                    : 0;
                                int sc = Math.Max(byName, byPath);
                                if (sc <= 0) return (Entry: en, Score: 0);
                                total += sc;
                            }
                            if (en.IsRecent) total += 15;
                            return (Entry: en, Score: total);
                        })
                        .Where(x => x.Score > 0)
                        // Large workspaces: the indexed file list is searched by QuickOpenFileIndex (top 120 only).
                        .Concat(QuickOpenFileSearch != null ? QuickOpenFileSearch(fileQuery, 120) : new List<(PaletteEntryItem Entry, int Score)>())
                        .OrderByDescending(x => x.Score)
                        .ThenBy(x => x.Entry.stringSubtitleOrPath.Length)
                        .Take(120)
                        .Select(x => x.Entry)
                        .ToList();
                }
                if (pendingQuickOpenLine > 0)
                    ModeHintLabel.Text = $"Open file and go to line {pendingQuickOpenLine}" + (pendingQuickOpenColumn > 0 ? $", column {pendingQuickOpenColumn}" : "");
            }
            else if (raw.StartsWith(">") && string.IsNullOrEmpty(query))
            {
                var cmdItems = AllEntries.Where(en => string.IsNullOrEmpty(en.FilePath)).ToList();
                var recentSet = new List<PaletteEntryItem>();
                foreach (string rt in RecentCommandTitles)
                {
                    var found = cmdItems.FirstOrDefault(c => string.Equals(c.Title, rt, StringComparison.OrdinalIgnoreCase));
                    if (found != null && !recentSet.Contains(found))
                    {
                        found.IsRecent = true;
                        recentSet.Add(found);
                    }
                }
                FilteredEntries = recentSet.Concat(cmdItems.Where(c => !recentSet.Contains(c))).Take(120).ToList();
            }
            else if (string.IsNullOrEmpty(query))
            {
                FilteredEntries = AllEntries.Take(120).ToList();
            }
            else
            {
                string[] tokens = query.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                bool commandMode = raw.StartsWith(">");
                FilteredEntries = AllEntries
                    .Where(item => !commandMode || string.IsNullOrEmpty(item.FilePath))
                    .Where(item =>
                    {
                        string hay = (item.Category + " " + item.Title + " " + item.stringSubtitleOrPath + " " + item.Shortcut).ToLowerInvariant();
                        return tokens.All(t => hay.Contains(t.ToLowerInvariant()));
                    })
                    .OrderByDescending(item => item.Title.StartsWith(query, StringComparison.OrdinalIgnoreCase))
                    .ThenBy(item => item.DisplayLabel.Length)
                    .Take(120)
                    .ToList();
            }

            RenderListItems();
            if (FilteredEntries.Count > 0)
                ResultsListBox.SelectedIndex = 0;
        }

        private void RenderListItems()
        {
            Color textPrimary = AvaloniaColorHelper.ToAvColor(currentTheme.TextPrimary);
            Color textSecondary = AvaloniaColorHelper.ToAvColor(currentTheme.TextSecondary);
            Color accent = AvaloniaColorHelper.ToAvColor(currentTheme.AccentPrimary);
            bool isLight = AvaloniaColorHelper.IsLightColor(currentTheme.FloatingBg);

            var controls = new List<Control>(FilteredEntries.Count);
            foreach (var entry in FilteredEntries)
            {
                var row = new Grid
                {
                    ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"),
                    Margin = new Thickness(2, 0, 0, 0),
                    MinHeight = UiTokens.ListRowHeight - 4
                };

                if (!string.IsNullOrEmpty(entry.Category))
                {
                    row.Children.Add(new TextBlock
                    {
                        Text = entry.Category + ":",
                        FontWeight = FontWeight.Normal,
                        FontSize = UiTokens.FontUi,
                        Foreground = new SolidColorBrush(textSecondary),
                        Margin = new Thickness(0, 0, 5, 0),
                        VerticalAlignment = VerticalAlignment.Center
                    });
                }

                var titleStack = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
                titleStack.Children.Add(new TextBlock
                {
                    Text = entry.Title,
                    FontSize = UiTokens.FontUi,
                    Foreground = new SolidColorBrush(textPrimary),
                    VerticalAlignment = VerticalAlignment.Center
                });
                if (!string.IsNullOrEmpty(entry.stringSubtitleOrPath))
                {
                    titleStack.Children.Add(new TextBlock
                    {
                        Text = entry.stringSubtitleOrPath,
                        FontSize = UiTokens.FontSmall,
                        Foreground = new SolidColorBrush(AvaloniaColorHelper.ToAvColor(currentTheme.FgMuted)),
                        VerticalAlignment = VerticalAlignment.Center
                    });
                }
                Grid.SetColumn(titleStack, 1);
                row.Children.Add(titleStack);

                if (!string.IsNullOrEmpty(entry.Shortcut))
                {
                    string sc = FormatShortcutForPlatform(entry.Shortcut);
                    // Keybinding label: quiet neutral chip in the UI font (VS Code keybindingLabel).
                    var kbdBadge = new Border
                    {
                        Background = WorkbenchStyles.B(UiTokens.ButtonSecondaryBackground(currentTheme)),
                        BorderThickness = new Thickness(0),
                        CornerRadius = new CornerRadius(3),
                        Padding = new Thickness(5, 1),
                        VerticalAlignment = VerticalAlignment.Center,
                        Child = new TextBlock
                        {
                            Text = sc,
                            FontSize = UiTokens.FontSmall - 1,
                            Foreground = new SolidColorBrush(textSecondary)
                        }
                    };
                    Grid.SetColumn(kbdBadge, 2);
                    row.Children.Add(kbdBadge);
                }

                controls.Add(row);
            }
            ResultsListBox.ItemsSource = controls;
        }

        public static string FormatShortcutForPlatform(string shortcut)
        {
            if (string.IsNullOrEmpty(shortcut)) return "";
            if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            {
                // VS Code's macOS keymap keeps literal Control for these (Cmd+G is Find Next, Cmd+Tab is the app switcher).
                if (shortcut == "Ctrl+`" || shortcut == "Ctrl+G" || shortcut == "Ctrl+Shift+G" || shortcut == "Ctrl+Tab" ||
                    shortcut == "Ctrl+Shift+Tab" || shortcut == "Ctrl+-" || shortcut == "Ctrl+Shift+-") return shortcut;
                return shortcut
                    .Replace("Ctrl+", "Cmd+")
                    .Replace("Alt+", "Option+");
            }
            return shortcut;
        }

        public void CycleSelection(int delta = 1)
        {
            if (FilteredEntries.Count == 0) return;
            int cur = ResultsListBox.SelectedIndex < 0 ? 0 : ResultsListBox.SelectedIndex;
            ResultsListBox.SelectedIndex = ((cur + delta) % FilteredEntries.Count + FilteredEntries.Count) % FilteredEntries.Count;
        }

        private void OnSearchKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Escape)
            {
                Close(restorePreviewTheme: true);
                e.Handled = true;
            }
            else if (e.Key == Key.P && (e.KeyModifiers.HasFlag(KeyModifiers.Control) || e.KeyModifiers.HasFlag(KeyModifiers.Meta)) && !e.KeyModifiers.HasFlag(KeyModifiers.Shift))
            {
                CycleSelection(1);
                e.Handled = true;
            }
            else if (e.Key == Key.Down)
            {
                if (FilteredEntries.Count > 0)
                {
                    ResultsListBox.SelectedIndex = Math.Min(FilteredEntries.Count - 1, ResultsListBox.SelectedIndex + 1);
                }
                e.Handled = true;
            }
            else if (e.Key == Key.Up)
            {
                if (FilteredEntries.Count > 0)
                {
                    ResultsListBox.SelectedIndex = Math.Max(0, ResultsListBox.SelectedIndex - 1);
                }
                e.Handled = true;
            }
            else if (e.Key == Key.Enter)
            {
                bool toSide = e.KeyModifiers.HasFlag(KeyModifiers.Control) || e.KeyModifiers.HasFlag(KeyModifiers.Meta);
                ExecuteSelected(openToSide: toSide);
                e.Handled = true;
            }
        }

        public void ExecuteSelected(bool openToSide = false)
        {
            string raw = (SearchInput.Text ?? "").Trim();
            if (raw.StartsWith(":"))
            {
                string numPart = raw.Substring(1).Split(':')[0].Trim();
                if (int.TryParse(numPart, out int lineNum) && lineNum >= 1)
                {
                    Close(restorePreviewTheme: false);
                    GoToLineRequested?.Invoke(lineNum);
                    return;
                }
            }

            int idx = ResultsListBox.SelectedIndex;
            if (idx < 0 && FilteredEntries.Count > 0) idx = 0;
            if (idx >= 0 && idx < FilteredEntries.Count)
            {
                var chosen = FilteredEntries[idx];
                if (raw.StartsWith("?"))
                {
                    chosen.Execute?.Invoke();
                    return;
                }
                if (raw.StartsWith(">") && !string.IsNullOrEmpty(chosen.Title))
                {
                    RecentCommandTitles.RemoveAll(t => string.Equals(t, chosen.Title, StringComparison.OrdinalIgnoreCase));
                    RecentCommandTitles.Insert(0, chosen.Title);
                }
                originalThemeIdBeforePreview = null; // keep chosen theme
                int line = pendingQuickOpenLine, col = pendingQuickOpenColumn;
                Close(restorePreviewTheme: false);
                if (!string.IsNullOrEmpty(chosen.FilePath))
                {
                    if (openToSide && FileOpenToSideRequested != null)
                        FileOpenToSideRequested(chosen.FilePath, line, col);
                    else if (FileOpenRequested != null)
                        FileOpenRequested(chosen.FilePath, line, col);
                }
                else
                {
                    chosen.Execute?.Invoke();
                }
            }
        }
    }
}

namespace codeLess.AvaloniaApp
{
    public class AvaloniaModalDialogHost : Border
    {
        private readonly Border cardBorder;
        private readonly TextBlock titleBlock;
        private readonly ContentControl bodyHost;
        private StudioTheme currentTheme = StudioTheme.GetById(StudioTheme.DefaultThemeId);

        public string ActiveDialogId { get; private set; } = "";
        public Control ActiveDialogBody => bodyHost.Content as Control;
        public event Action Closed;

        public AvaloniaModalDialogHost()
        {
            HorizontalAlignment = HorizontalAlignment.Stretch;
            VerticalAlignment = VerticalAlignment.Stretch;
            Background = new SolidColorBrush(Color.FromArgb(110, 0, 0, 0));
            IsVisible = false;
            ZIndex = 250;

            titleBlock = new TextBlock
            {
                FontSize = UiTokens.FontUi + 1,
                FontWeight = FontWeight.SemiBold,
                VerticalAlignment = VerticalAlignment.Center
            };
            closeButton = WorkbenchStyles.IconButton("close", "Close (Escape)", CloseModal, currentTheme);

            var headerGrid = new Grid
            {
                ColumnDefinitions = new ColumnDefinitions("*,Auto"),
                Margin = new Thickness(0, 0, -6, 12)
            };
            var closeBtn = closeButton;
            headerGrid.Children.Add(titleBlock);
            Grid.SetColumn(closeBtn, 1);
            headerGrid.Children.Add(closeBtn);

            bodyHost = new ContentControl();
            // The body gets the card's remaining height: content that fills the dialog (its own search box /
            // scrolling / footer) is sized to it, anything else scrolls here. Nothing may draw outside the card.
            bodyScroll = new ScrollViewer
            {
                Content = bodyHost,
                HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
                VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto
            };

            var cardGrid = new Grid { RowDefinitions = new RowDefinitions("Auto,*") };
            cardGrid.Children.Add(headerGrid);
            Grid.SetRow(bodyScroll, 1);
            cardGrid.Children.Add(bodyScroll);

            cardBorder = new Border
            {
                Child = cardGrid,
                Width = 620,
                MaxHeight = 560,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                CornerRadius = new CornerRadius(UiTokens.PopupRadius + 2),
                BorderThickness = new Thickness(1),
                Padding = new Thickness(20, 14, 20, 18),
                ClipToBounds = true
            };

            Child = cardBorder;
        }

        private readonly Button closeButton;
        private readonly ScrollViewer bodyScroll;

        /// <summary>Largest card height (px); the card also keeps a margin to the window edges.</summary>
        public const double MaxCardHeight = 760;
        public const double CardWindowMargin = 24;
        public Border Card => cardBorder;
        public ScrollViewer BodyScroll => bodyScroll;

        /// <summary>Marks dialog content that sizes itself to the dialog (own scrolling region and footer).</summary>
        public static readonly AttachedProperty<bool> FillsDialogProperty =
            AvaloniaProperty.RegisterAttached<AvaloniaModalDialogHost, Control, bool>("FillsDialog");
        public static void SetFillsDialog(Control c, bool value) => c.SetValue(FillsDialogProperty, value);
        public static bool GetFillsDialog(Control c) => c.GetValue(FillsDialogProperty);

        protected override Size MeasureOverride(Size availableSize)
        {
            if (!double.IsInfinity(availableSize.Height))
                cardBorder.MaxHeight = Math.Max(160, Math.Min(MaxCardHeight, availableSize.Height - CardWindowMargin * 2));
            if (!double.IsInfinity(availableSize.Width))
                cardBorder.MaxWidth = Math.Max(240, availableSize.Width - CardWindowMargin * 2);
            return base.MeasureOverride(availableSize);
        }

        public void ApplyTheme(StudioTheme theme)
        {
            if (theme == null) return;
            currentTheme = theme;
            cardBorder.Background = WorkbenchStyles.B(UiTokens.WidgetBackground(theme));
            cardBorder.BorderBrush = WorkbenchStyles.B(UiTokens.WidgetBorder(theme));
            cardBorder.BoxShadow = new BoxShadows(new BoxShadow { OffsetY = 10, Blur = 32, Color = WorkbenchStyles.C(UiTokens.WidgetShadow(theme)) });
            titleBlock.Foreground = AvaloniaColorHelper.ToBrush(theme.TextPrimary);
            WorkbenchStyles.ThemeIconButton(closeButton, theme);
        }

        private IInputElement focusBeforeModal;

        public void ShowModal(string dialogId, string title, Control content, double width = 620)
        {
            var top = TopLevel.GetTopLevel(this);
            if (!IsVisible) focusBeforeModal = top?.FocusManager?.GetFocusedElement();
            ActiveDialogId = dialogId ?? "";
            titleBlock.Text = title ?? "codeLess";
            cardBorder.Width = width;
            bool fills = content != null && GetFillsDialog(content);
            bodyScroll.VerticalScrollBarVisibility = fills ? Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled : Avalonia.Controls.Primitives.ScrollBarVisibility.Auto;
            bodyScroll.Offset = new Vector(0, 0);
            bodyHost.Content = content;
            IsVisible = true;
            // Move keyboard focus into the dialog; otherwise keystrokes / Enter kept editing the file behind the
            // overlay (e.g. while the Unsaved Changes prompt was open). Callers may focus a specific control later.
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                if (!IsVisible || !ReferenceEquals(bodyHost.Content, content)) return;
                var focused = top?.FocusManager?.GetFocusedElement() as Visual;
                if (focused != null && IsVisualDescendant(focused, cardBorder)) return;
                var first = FindFirstFocusable(content);
                if (first != null) first.Focus();
                else cardBorder.Focus();
            }, Avalonia.Threading.DispatcherPriority.Loaded);
        }

        private static bool IsVisualDescendant(Visual v, Visual ancestor)
        {
            while (v != null)
            {
                if (ReferenceEquals(v, ancestor)) return true;
                v = Avalonia.VisualTree.VisualExtensions.GetVisualParent(v);
            }
            return false;
        }

        private static InputElement FindFirstFocusable(Visual root)
        {
            if (root is TextBox tb && tb.IsEffectivelyEnabled && !tb.IsReadOnly) return tb;
            foreach (var child in Avalonia.VisualTree.VisualExtensions.GetVisualChildren(root))
            {
                var f = FindFirstFocusable(child);
                if (f != null) return f;
            }
            if (root is Button b && b.IsEffectivelyEnabled) return b;
            return null;
        }

        public void CloseModal()
        {
            if (!IsVisible) return;
            IsVisible = false;
            ActiveDialogId = "";
            bodyHost.Content = null;
            var restore = focusBeforeModal;
            focusBeforeModal = null;
            try { restore?.Focus(); } catch { }
            Closed?.Invoke();
        }
    }

    public static class AvaloniaDialogBuilder
    {
        public static Control BuildUnsavedChangesView(
            IReadOnlyList<EditorTabBuffer> dirtyTabs,
            StudioTheme theme,
            Action<UnsavedPromptResult> onDecision)
        {
            Color textPrimary = AvaloniaColorHelper.ToAvColor(theme.TextPrimary);
            Color textSecondary = AvaloniaColorHelper.ToAvColor(theme.TextSecondary);
            Color accent = AvaloniaColorHelper.ToAvColor(theme.AccentPrimary);

            var stack = new StackPanel { Spacing = 10 };
            stack.Children.Add(new TextBlock
            {
                Text = dirtyTabs.Count == 1
                    ? $"Do you want to save the changes you made to {dirtyTabs[0].FileName}?"
                    : $"Do you want to save the changes to the following {dirtyTabs.Count} modified files?",
                FontSize = 13,
                FontWeight = FontWeight.SemiBold,
                Foreground = new SolidColorBrush(textPrimary),
                TextWrapping = TextWrapping.Wrap
            });

            var fileList = new StackPanel { Spacing = 3, Margin = new Thickness(8, 2) };
            foreach (var t in dirtyTabs.Take(8))
            {
                fileList.Children.Add(new TextBlock
                {
                    Text = "●  " + (t.RelativePath ?? t.FileName),
                    FontFamily = new FontFamily("Cascadia Code, Consolas, Menlo, monospace"),
                    FontSize = 11.5,
                    Foreground = new SolidColorBrush(textSecondary)
                });
            }
            stack.Children.Add(fileList);

            stack.Children.Add(new TextBlock
            {
                Text = "Your changes will be lost if you don't save them.",
                FontSize = 11.5,
                Foreground = new SolidColorBrush(textSecondary)
            });

            var btnRow = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                Spacing = 8,
                Margin = new Thickness(0, 8, 0, 0)
            };

            var saveAllBtn = new Button
            {
                Name = "SaveAllBtn",
                Content = dirtyTabs.Count > 1 ? "Save All" : "Save",
                FontWeight = FontWeight.SemiBold,
                Padding = new Thickness(14, 6),
                Background = new SolidColorBrush(accent),
                Foreground = Brushes.White
            };
            var dontSaveBtn = new Button
            {
                Name = "DontSaveBtn",
                Content = "Don't Save",
                Padding = new Thickness(14, 6)
            };
            var cancelBtn = new Button
            {
                Name = "CancelBtn",
                Content = "Cancel",
                Padding = new Thickness(14, 6)
            };

            saveAllBtn.Click += (s, e) => onDecision?.Invoke(UnsavedPromptResult.SaveAll);
            dontSaveBtn.Click += (s, e) => onDecision?.Invoke(UnsavedPromptResult.DontSave);
            cancelBtn.Click += (s, e) => onDecision?.Invoke(UnsavedPromptResult.Cancel);

            btnRow.Children.Add(saveAllBtn);
            btnRow.Children.Add(dontSaveBtn);
            btnRow.Children.Add(cancelBtn);
            stack.Children.Add(btnRow);
            return stack;
        }

        public static Control BuildPromptInputView(
            string promptLabel,
            string initialValue,
            StudioTheme theme,
            Action<string> onConfirmed,
            Action onCancelled,
            out TextBox inputBox)
        {
            Color textPrimary = AvaloniaColorHelper.ToAvColor(theme.TextPrimary);
            Color accent = AvaloniaColorHelper.ToAvColor(theme.AccentPrimary);

            var stack = new StackPanel { Spacing = 10 };
            stack.Children.Add(new TextBlock
            {
                Text = promptLabel,
                FontSize = 12.5,
                Foreground = new SolidColorBrush(textPrimary)
            });

            var tb = new TextBox
            {
                Text = initialValue ?? "",
                FontSize = 13,
                Padding = new Thickness(8, 6)
            };
            inputBox = tb;
            stack.Children.Add(tb);

            var btnRow = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                Spacing = 8
            };
            var okBtn = new Button
            {
                Content = "OK",
                FontWeight = FontWeight.SemiBold,
                Padding = new Thickness(16, 5),
                Background = new SolidColorBrush(accent),
                Foreground = Brushes.White
            };
            var cancelBtn = new Button
            {
                Content = "Cancel",
                Padding = new Thickness(14, 5)
            };

            okBtn.Click += (s, e) => onConfirmed?.Invoke(tb.Text ?? "");
            cancelBtn.Click += (s, e) => onCancelled?.Invoke();
            tb.KeyDown += (s, e) =>
            {
                if (e.Key == Key.Enter) { onConfirmed?.Invoke(tb.Text ?? ""); e.Handled = true; }
                else if (e.Key == Key.Escape) { onCancelled?.Invoke(); e.Handled = true; }
            };

            btnRow.Children.Add(okBtn);
            btnRow.Children.Add(cancelBtn);
            stack.Children.Add(btnRow);
            return stack;
        }

        public static Control BuildSettingsView(
            StudioSettings settings,
            StudioTheme theme,
            Action<StudioSettings> onApplied,
            Action onClosed,
            Control workspaceTrustSection = null)
        {
            return SettingsDialogView.Build(settings, theme, onApplied, onClosed, workspaceTrustSection);
        }

        public static Control BuildKeyboardShortcutsView(StudioTheme theme, Action openJson, codeLess.Core.KeybindingManager manager)
        {
            return new codeLess.AvaloniaApp.AvaloniaKeyboardShortcutsEditor(theme, openJson, manager);
        }
    }
}
