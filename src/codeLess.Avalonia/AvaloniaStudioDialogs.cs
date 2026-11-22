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
            InitialPrefix = prefix ?? "";
            AllEntries = entries ?? new List<PaletteEntryItem>();
            originalThemeIdBeforePreview = currentThemeId;
            IsVisible = true;
            SearchInput.Text = InitialPrefix;
            SearchInput.CaretIndex = SearchInput.Text.Length;
            RefreshFilter();
            SearchInput.Focus();
        }

        public void Close(bool restorePreviewTheme = false)
        {
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
            bool hasFileEntries = fileMode && AllEntries.Any(en => !string.IsNullOrEmpty(en.FilePath));
            if (hasFileEntries)
            {
                // Quick Open (Ctrl/Cmd+P): files only, fuzzy ranked, recent first, optional ":line[:col]" suffix.
                string fileQuery = WorkspaceSearchEngine.SplitQuickOpenLineSuffix(query, out pendingQuickOpenLine, out pendingQuickOpenColumn);
                var files = AllEntries.Where(en => !string.IsNullOrEmpty(en.FilePath));
                if (string.IsNullOrEmpty(fileQuery))
                {
                    FilteredEntries = files.Where(en => en.IsRecent).Concat(files.Where(en => !en.IsRecent)).Take(120).ToList();
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
                        .OrderByDescending(x => x.Score)
                        .ThenBy(x => x.Entry.stringSubtitleOrPath.Length)
                        .Take(120)
                        .Select(x => x.Entry)
                        .ToList();
                }
                if (pendingQuickOpenLine > 0)
                    ModeHintLabel.Text = $"Open file and go to line {pendingQuickOpenLine}" + (pendingQuickOpenColumn > 0 ? $", column {pendingQuickOpenColumn}" : "");
            }
            else if (raw.StartsWith(">") && AllEntries.Any(en => !string.IsNullOrEmpty(en.FilePath)) && string.IsNullOrEmpty(query))
            {
                FilteredEntries = AllEntries.Where(en => string.IsNullOrEmpty(en.FilePath)).Take(120).ToList();
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
                if (shortcut == "Ctrl+`") return "Ctrl+`";
                return shortcut
                    .Replace("Ctrl+", "Cmd+")
                    .Replace("Alt+", "Option+");
            }
            return shortcut;
        }

        private void OnSearchKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Escape)
            {
                Close(restorePreviewTheme: true);
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
                ExecuteSelected();
                e.Handled = true;
            }
        }

        public void ExecuteSelected()
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
                originalThemeIdBeforePreview = null; // keep chosen theme
                int line = pendingQuickOpenLine, col = pendingQuickOpenColumn;
                Close(restorePreviewTheme: false);
                if (!string.IsNullOrEmpty(chosen.FilePath) && FileOpenRequested != null)
                    FileOpenRequested(chosen.FilePath, line, col);
                else
                    chosen.Execute?.Invoke();
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

            var cardStack = new StackPanel();
            cardStack.Children.Add(headerGrid);
            cardStack.Children.Add(bodyHost);

            cardBorder = new Border
            {
                Child = cardStack,
                Width = 620,
                MaxHeight = 560,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                CornerRadius = new CornerRadius(UiTokens.PopupRadius + 2),
                BorderThickness = new Thickness(1),
                Padding = new Thickness(20, 14, 20, 18)
            };

            Child = cardBorder;
        }

        private readonly Button closeButton;

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
            Action onClosed)
        {
            Color textPrimary = AvaloniaColorHelper.ToAvColor(theme.TextPrimary);
            Color textSecondary = AvaloniaColorHelper.ToAvColor(theme.TextSecondary);
            Color accent = AvaloniaColorHelper.ToAvColor(theme.AccentPrimary);

            var stack = new StackPanel { Spacing = 8 };

            // Theme selector
            var themes = StudioTheme.GetAllBuiltInThemes();
            var themeCombo = new ComboBox
            {
                ItemsSource = themes.Select(t => $"{t.Name} ({t.Id})").ToList(),
                SelectedIndex = Math.Max(0, themes.FindIndex(t => string.Equals(t.Id, settings.ThemeId, StringComparison.OrdinalIgnoreCase))),
                HorizontalAlignment = HorizontalAlignment.Stretch
            };

            var fontBox = new TextBox { Text = settings.FontSize.ToString("0.#", CultureInfo.InvariantCulture), Width = 90 };
            var tabBox = new TextBox { Text = settings.TabSize.ToString(CultureInfo.InvariantCulture), Width = 90 };
            var wrapChk = new CheckBox { Content = "Word Wrap (Alt+Z)", IsChecked = settings.WordWrap, Foreground = new SolidColorBrush(textPrimary) };
            var minimapChk = new CheckBox { Content = "Show Editor Minimap", IsChecked = settings.ShowMinimap, Foreground = new SolidColorBrush(textPrimary) };
            var autoSaveChk = new CheckBox { Content = "Auto Save Modified Files", IsChecked = settings.AutoSave, Foreground = new SolidColorBrush(textPrimary) };
            var venvChk = new CheckBox { Content = "Activate Python environment in new terminals", IsChecked = settings.ActivatePythonEnvironmentInTerminal, Foreground = new SolidColorBrush(textPrimary) };
            var excludedBox = new TextBox { Text = settings.ExcludedFolders ?? "" };
            var offlineChk = new CheckBox { Content = "Offline Mode (block all non-local network requests; local Ollama / LM Studio keep working)", IsChecked = NetworkLedger.OfflineMode, Foreground = new SolidColorBrush(textPrimary) };

            var providers = LlmHarnessAndAgentEngine.GetAllProviderTemplates();
            string activeNormId = StudioSettings.NormalizeProviderId(settings.ActiveProvider);
            int initProvIdx = providers.FindIndex(p =>
                string.Equals(p.Name, settings.ActiveProvider, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(StudioSettings.NormalizeProviderId(p.Name), activeNormId, StringComparison.OrdinalIgnoreCase));
            var providerCombo = new ComboBox
            {
                ItemsSource = providers.Select(p => p.Name).ToList(),
                SelectedIndex = Math.Max(0, initProvIdx),
                HorizontalAlignment = HorizontalAlignment.Stretch
            };
            var modelBox = new TextBox { Text = settings.ActiveModel ?? "" };
            var apiKeyBox = new TextBox { Text = settings.GetActiveApiKey() ?? "", PasswordChar = '•', Watermark = "sk-... / API Key (stored per provider)" };
            providerCombo.SelectionChanged += (s, e) =>
            {
                if (providerCombo.SelectedIndex >= 0 && providerCombo.SelectedIndex < providers.Count)
                {
                    var selectedProv = providers[providerCombo.SelectedIndex];
                    apiKeyBox.Text = settings.GetApiKeyForProvider(selectedProv.Name) ?? "";
                    if (!string.IsNullOrWhiteSpace(selectedProv.DefaultModel))
                        modelBox.Text = selectedProv.DefaultModel;
                }
            };

            stack.Children.Add(WorkbenchStyles.SectionLabel("Color Theme", theme));
            stack.Children.Add(themeCombo);

            var rowMetrics = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Margin = new Thickness(0, 4, 0, 0) };
            rowMetrics.Children.Add(new TextBlock { Text = "Font Size", FontSize = UiTokens.FontUi, VerticalAlignment = VerticalAlignment.Center, Foreground = new SolidColorBrush(textSecondary) });
            rowMetrics.Children.Add(fontBox);
            rowMetrics.Children.Add(new TextBlock { Text = "Tab Size", FontSize = UiTokens.FontUi, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0), Foreground = new SolidColorBrush(textSecondary) });
            rowMetrics.Children.Add(tabBox);
            stack.Children.Add(rowMetrics);

            var togglesRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 18 };
            togglesRow.Children.Add(wrapChk);
            togglesRow.Children.Add(minimapChk);
            togglesRow.Children.Add(autoSaveChk);
            stack.Children.Add(togglesRow);
            stack.Children.Add(venvChk);
            stack.Children.Add(offlineChk);

            stack.Children.Add(WorkbenchStyles.SectionLabel("Excluded Workspace Folders (comma-separated)", theme));
            stack.Children.Add(excludedBox);

            stack.Children.Add(WorkbenchStyles.SectionLabel("AI Provider & BYOK Configuration", theme));
            stack.Children.Add(providerCombo);
            stack.Children.Add(modelBox);
            stack.Children.Add(apiKeyBox);

            var btnRow = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                Spacing = 8,
                Margin = new Thickness(0, 6, 0, 0)
            };
            var saveBtn = new Button
            {
                Content = "Save & Apply",
                Padding = new Thickness(14, 4),
                MinHeight = UiTokens.ControlHeight
            };
            WorkbenchStyles.StylePrimaryButton(saveBtn, theme);
            var closeBtn = new Button
            {
                Content = "Cancel",
                Padding = new Thickness(14, 4),
                MinHeight = UiTokens.ControlHeight
            };

            saveBtn.Click += (s, e) =>
            {
                if (themeCombo.SelectedIndex >= 0 && themeCombo.SelectedIndex < themes.Count)
                    settings.ThemeId = themes[themeCombo.SelectedIndex].Id;
                if (float.TryParse(fontBox.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out float fs) && fs >= 8 && fs <= 32)
                    settings.FontSize = fs;
                if (int.TryParse(tabBox.Text, out int ts) && ts >= 1 && ts <= 8)
                    settings.TabSize = ts;
                settings.WordWrap = wrapChk.IsChecked == true;
                settings.ShowMinimap = minimapChk.IsChecked == true;
                settings.AutoSave = autoSaveChk.IsChecked == true;
                settings.ActivatePythonEnvironmentInTerminal = venvChk.IsChecked == true;
                settings.OfflineMode = offlineChk.IsChecked == true;
                NetworkLedger.SetOfflineMode(settings.OfflineMode);
                settings.ExcludedFolders = (excludedBox.Text ?? "").Trim();
                if (providerCombo.SelectedIndex >= 0 && providerCombo.SelectedIndex < providers.Count)
                    settings.ActiveProvider = StudioSettings.NormalizeProviderId(providers[providerCombo.SelectedIndex].Name);
                if (!string.IsNullOrWhiteSpace(modelBox.Text))
                    settings.ActiveModel = modelBox.Text.Trim();
                settings.SetApiKeyForProvider(settings.ActiveProvider, (apiKeyBox.Text ?? "").Trim());

                onApplied?.Invoke(settings);
            };
            closeBtn.Click += (s, e) => onClosed?.Invoke();

            btnRow.Children.Add(saveBtn);
            btnRow.Children.Add(closeBtn);
            stack.Children.Add(btnRow);
            return stack;
        }

        public static Control BuildKeyboardShortcutsView(StudioTheme theme)
        {
            Color textPrimary = AvaloniaColorHelper.ToAvColor(theme.TextPrimary);
            Color textSecondary = AvaloniaColorHelper.ToAvColor(theme.TextSecondary);
            Color accent = AvaloniaColorHelper.ToAvColor(theme.AccentPrimary);

            var allBindings = new List<(string Category, string Action, string WinKey, string MacKey)>
            {
                ("Workbench & Navigation", "Show All Commands / Command Palette", "Ctrl+Shift+P / F1", "Cmd+Shift+P / F1"),
                ("Workbench & Navigation", "Quick Open, Go to File", "Ctrl+P / Ctrl+E", "Cmd+P / Cmd+E"),
                ("Workbench & Navigation", "Go to Symbol in Editor", "Ctrl+Shift+O", "Cmd+Shift+O"),
                ("Workbench & Navigation", "Go to Symbol in Workspace", "Ctrl+T", "Cmd+T"),
                ("Workbench & Navigation", "Go to Line / Column", "Ctrl+G", "Cmd+G"),
                ("Workbench & Navigation", "Navigate Back / Forward", "Alt+Left / Alt+Right", "Ctrl+- / Ctrl+Shift+-"),
                ("Workbench & Navigation", "Open Settings", "Ctrl+,", "Cmd+,"),
                ("Workbench & Navigation", "Open Keyboard Shortcuts", "Ctrl+K Ctrl+S", "Cmd+K Cmd+S"),
                ("Workbench & Navigation", "Select Color Theme", "Ctrl+K Ctrl+T", "Cmd+K Cmd+T"),
                ("Workbench & Navigation", "Toggle Primary Sidebar", "Ctrl+B", "Cmd+B"),
                ("Workbench & Navigation", "Toggle Bottom Panel / Terminal", "Ctrl+J / Ctrl+`", "Cmd+J / Ctrl+`"),
                ("Workbench & Navigation", "Show Explorer / Search / SCM / Extensions", "Ctrl+Shift+E / F / G / X", "Cmd+Shift+E / F / G / X"),
                ("Workbench & Navigation", "Focus / Toggle AI Agent Dock", "Ctrl+Alt+I / Ctrl+Shift+A", "Cmd+Option+I / Cmd+Shift+A"),
                ("Workbench & Navigation", "Zoom In / Zoom Out / Reset Zoom", "Ctrl+= / Ctrl+- / Ctrl+0", "Cmd+= / Cmd+- / Cmd+0"),
                ("Workbench & Navigation", "Zen Mode", "Ctrl+K Z", "Cmd+K Z"),
                ("File & Tab Management", "New Untitled File", "Ctrl+N", "Cmd+N"),
                ("File & Tab Management", "Open File / Open Folder", "Ctrl+O / Ctrl+K Ctrl+O", "Cmd+O / Cmd+K Cmd+O"),
                ("File & Tab Management", "Save / Save As / Save All", "Ctrl+S / Ctrl+Shift+S / Ctrl+K S", "Cmd+S / Cmd+Shift+S / Cmd+Option+S"),
                ("File & Tab Management", "Close Active Tab / Close Folder", "Ctrl+W / Ctrl+K F", "Cmd+W / Cmd+K F"),
                ("File & Tab Management", "Reopen Closed Editor Tab", "Ctrl+Shift+T", "Cmd+Shift+T"),
                ("File & Tab Management", "Split Editor Right", "Ctrl+\\", "Cmd+\\"),
                ("File & Tab Management", "Switch Editor Tab", "Ctrl+Tab / Ctrl+PageDown", "Ctrl+Tab / Cmd+Option+Right"),
                ("Editor & Multi-Cursor", "Undo / Redo", "Ctrl+Z / Ctrl+Y", "Cmd+Z / Cmd+Shift+Z"),
                ("Editor & Multi-Cursor", "Find / Replace in File", "Ctrl+F / Ctrl+H", "Cmd+F / Cmd+Option+F"),
                ("Editor & Multi-Cursor", "Add Selection to Next Find Match", "Ctrl+D", "Cmd+D"),
                ("Editor & Multi-Cursor", "Select All Occurrences of Find Match", "Ctrl+Shift+L", "Cmd+Shift+L"),
                ("Editor & Multi-Cursor", "Add Cursor Above / Below", "Ctrl+Alt+Up / Ctrl+Alt+Down", "Cmd+Option+Up / Cmd+Option+Down"),
                ("Editor & Multi-Cursor", "Add Cursors to Line Ends", "Shift+Alt+I", "Shift+Option+I"),
                ("Editor & Multi-Cursor", "Move Line Up / Down", "Alt+Up / Alt+Down", "Option+Up / Option+Down"),
                ("Editor & Multi-Cursor", "Copy Line Up / Down", "Shift+Alt+Up / Shift+Alt+Down", "Shift+Option+Up / Shift+Option+Down"),
                ("Editor & Multi-Cursor", "Delete Line / Duplicate Line", "Ctrl+Shift+K / Ctrl+Shift+D", "Cmd+Shift+K / Cmd+Shift+D"),
                ("Editor & Multi-Cursor", "Toggle Line Comment / Block Comment", "Ctrl+/ / Shift+Alt+A", "Cmd+/ / Shift+Option+A"),
                ("Editor & Multi-Cursor", "Jump to Matching Bracket", "Ctrl+Shift+\\", "Cmd+Shift+\\"),
                ("Editor & Multi-Cursor", "Toggle Word Wrap", "Alt+Z", "Option+Z"),
                ("AI & Terminal", "Inline AI Edit / Generate at Cursor", "Ctrl+K / Ctrl+I", "Cmd+K / Cmd+I"),
                ("AI & Terminal", "Accept Ghost Text / Next Word", "Tab / Ctrl+Right", "Tab / Cmd+Right"),
                ("AI & Terminal", "New Integrated Terminal / Split Terminal", "Ctrl+Shift+` / Ctrl+Shift+5", "Ctrl+Shift+` / Cmd+\\")
            };

            var filterBox = new TextBox
            {
                Watermark = "Filter keyboard shortcuts by command or keybinding...",
                FontSize = 12.5,
                Padding = new Thickness(8, 5)
            };

            var listPanel = new StackPanel { Spacing = 4 };
            void RenderRows(string q)
            {
                listPanel.Children.Clear();
                foreach (var b in allBindings)
                {
                    if (!string.IsNullOrWhiteSpace(q))
                    {
                        string hay = (b.Category + " " + b.Action + " " + b.WinKey + " " + b.MacKey).ToLowerInvariant();
                        if (!hay.Contains(q.Trim().ToLowerInvariant())) continue;
                    }
                    var row = new Grid
                    {
                        ColumnDefinitions = new ColumnDefinitions("145,*,140,145"),
                        Margin = new Thickness(2, 2)
                    };
                    row.Children.Add(new TextBlock { Text = b.Category, FontSize = 11, Foreground = new SolidColorBrush(accent), VerticalAlignment = VerticalAlignment.Center });
                    var actTb = new TextBlock { Text = b.Action, FontSize = 12, Foreground = new SolidColorBrush(textPrimary), VerticalAlignment = VerticalAlignment.Center };
                    Grid.SetColumn(actTb, 1);
                    row.Children.Add(actTb);
                    var winTb = new TextBlock { Text = b.WinKey, FontFamily = new FontFamily("Cascadia Code, Consolas, Menlo, monospace"), FontSize = 11, Foreground = new SolidColorBrush(textSecondary), VerticalAlignment = VerticalAlignment.Center };
                    Grid.SetColumn(winTb, 2);
                    row.Children.Add(winTb);
                    var macTb = new TextBlock { Text = b.MacKey, FontFamily = new FontFamily("Cascadia Code, Consolas, Menlo, monospace"), FontSize = 11, Foreground = new SolidColorBrush(textPrimary), VerticalAlignment = VerticalAlignment.Center };
                    Grid.SetColumn(macTb, 3);
                    row.Children.Add(macTb);
                    listPanel.Children.Add(row);
                }
            }
            filterBox.TextChanged += (s, e) => RenderRows(filterBox.Text);
            RenderRows("");

            var outer = new StackPanel { Spacing = 8 };
            outer.Children.Add(filterBox);
            outer.Children.Add(new ScrollViewer
            {
                Height = 380,
                Content = listPanel
            });
            return outer;
        }
    }
}
