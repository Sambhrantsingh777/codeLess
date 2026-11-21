using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using codeLess.Studio;

namespace codeLess.AvaloniaApp
{
    public class AvaloniaAgentFeedItem
    {
        public string Role { get; set; } = "assistant"; // "user", "assistant", "command_approval", "diff_review", "error"
        public string Content { get; set; } = "";
        public string ReasoningText { get; set; } = "";
        public bool ReasoningExpanded { get; set; } = false;
        public List<string> ToolItems { get; set; } = new List<string>();
        public bool ToolsExpanded { get; set; } = false;
        public string CommandText { get; set; } = "";
        public string CommandStatus { get; set; } = "Pending"; // "Pending", "Approved", "Skipped"
        public string DiffFilePath { get; set; } = "";
        public int DiffAddedLines { get; set; }
        public int DiffRemovedLines { get; set; }
        public string DiffPreviewText { get; set; } = "";
        public string DiffStatus { get; set; } = "Pending"; // "Pending", "Accepted", "Rejected"
        public string ModelBadge { get; set; } = "";
        public DateTime Timestamp { get; set; } = DateTime.Now;
    }

    public class AvaloniaAgentFeedControl : Grid
    {
        public List<AvaloniaAgentFeedItem> Items { get; } = new List<AvaloniaAgentFeedItem>();
        private readonly ScrollViewer scrollViewer;
        private readonly StackPanel cardsStack;
        private StudioTheme currentTheme = StudioTheme.GetById(StudioTheme.DefaultThemeId);

        public event Action<string, string> CodeActionRequested; // (action: "copy"|"insert"|"apply", code)
        public event Action<AvaloniaAgentFeedItem, bool> CommandApprovalClicked; // (item, approved)
        public event Action<AvaloniaAgentFeedItem, bool> DiffDecisionClicked; // (item, accepted)
        public event Action<AvaloniaAgentFeedItem, int> RestoreCheckpointRequested;
        public event Action<string> QuickPromptClicked;
        public event Action OpenSettingsRequested;

        public AvaloniaAgentFeedControl()
        {
            cardsStack = new StackPanel
            {
                Spacing = 7,
                Margin = new Thickness(8, 6, 10, 10)
            };
            scrollViewer = new ScrollViewer
            {
                Content = cardsStack,
                VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled
            };
            Children.Add(scrollViewer);
            RebuildVisualFeed();
        }

        public void ApplyTheme(StudioTheme theme)
        {
            if (theme == null) return;
            currentTheme = theme;
            Background = AvaloniaColorHelper.ToBrush(theme.ChatBg);
            RebuildVisualFeed();
        }

        public void ClearFeed()
        {
            Items.Clear();
            RebuildVisualFeed();
        }

        public void AddMessage(AvaloniaAgentFeedItem item)
        {
            if (item == null) return;
            Items.Add(item);
            RebuildVisualFeed();
            ScrollToBottom();
        }

        public void ScrollToBottom()
        {
            scrollViewer.Offset = new Vector(0, 999999);
        }

        public void ScrollToTop()
        {
            scrollViewer.Offset = new Vector(0, 0);
        }

        public void RebuildVisualFeed()
        {
            cardsStack.Children.Clear();
            bool isLight = AvaloniaColorHelper.IsLightColor(currentTheme.ChatBg);
            Color textPrimary = AvaloniaColorHelper.ToAvColor(currentTheme.TextPrimary);
            Color textSecondary = AvaloniaColorHelper.ToAvColor(currentTheme.TextSecondary);
            Color accent = AvaloniaColorHelper.ToAvColor(currentTheme.AccentPrimary);
            Color borderCol = AvaloniaColorHelper.ToAvColor(currentTheme.BorderSubtle);

            if (Items.Count == 0)
            {
                var emptyCard = new StackPanel
                {
                    Spacing = 10,
                    Margin = new Thickness(8, 36, 8, 12),
                    HorizontalAlignment = HorizontalAlignment.Center
                };
                var emptyTitle = new IconLabel("sparkle", "codeLess Agent", 16, 14, "✦ ") { Spacing = 7, HorizontalAlignment = HorizontalAlignment.Center };
                emptyTitle.Icon.FollowForeground = false;
                emptyTitle.Icon.IconColor = accent;
                emptyTitle.Label.FontWeight = FontWeight.SemiBold;
                emptyTitle.Label.Foreground = new SolidColorBrush(textPrimary);
                emptyCard.Children.Add(emptyTitle);
                emptyCard.Children.Add(new TextBlock
                {
                    Text = "Ask questions, edit files with inline diffs, or run multi-step workspace tools.",
                    FontSize = 12,
                    Foreground = new SolidColorBrush(textSecondary),
                    TextWrapping = TextWrapping.Wrap,
                    TextAlignment = TextAlignment.Center,
                    MaxWidth = 280
                });

                string[] starters = new[]
                {
                    "Explain active file & architecture",
                    "Refactor selected code with inline diff",
                    "Inspect workspace diagnostics & fix issues"
                };
                foreach (string st in starters)
                {
                    string promptText = st;
                    var chipLabel = new IconLabel("chevron-right", promptText, 11, UiTokens.FontSmall) { Spacing = 8 };
                    chipLabel.Icon.FollowForeground = false;
                    chipLabel.Icon.IconColor = textSecondary;
                    var chipBtn = new Button
                    {
                        Content = chipLabel,
                        FontSize = UiTokens.FontSmall,
                        HorizontalAlignment = HorizontalAlignment.Stretch,
                        HorizontalContentAlignment = HorizontalAlignment.Left,
                        Padding = new Thickness(10, 5),
                        Background = Brushes.Transparent,
                        Foreground = new SolidColorBrush(textPrimary),
                        BorderBrush = new SolidColorBrush(AvaloniaColorHelper.ToAvColor(UiTokens.InputBorder(currentTheme))),
                        BorderThickness = new Thickness(1),
                        CornerRadius = new CornerRadius(UiTokens.ToolbarRadius)
                    };
                    chipBtn.Classes.Add("flat");
                    chipBtn.Click += (s, e) => QuickPromptClicked?.Invoke(promptText);
                    emptyCard.Children.Add(chipBtn);
                }
                cardsStack.Children.Add(emptyCard);
                return;
            }

            for (int idx = 0; idx < Items.Count; idx++)
            {
                var item = Items[idx];
                int capturedIdx = idx;

                if (string.Equals(item.Role, "user", StringComparison.OrdinalIgnoreCase))
                {
                    cardsStack.Children.Add(BuildUserCard(item, capturedIdx, isLight, textPrimary, textSecondary, accent, borderCol));
                }
                else if (string.Equals(item.Role, "command_approval", StringComparison.OrdinalIgnoreCase))
                {
                    cardsStack.Children.Add(BuildCommandApprovalCard(item, isLight, textPrimary, textSecondary, accent, borderCol));
                }
                else if (string.Equals(item.Role, "diff_review", StringComparison.OrdinalIgnoreCase))
                {
                    cardsStack.Children.Add(BuildDiffReviewCard(item, isLight, textPrimary, textSecondary, accent, borderCol));
                }
                else if (string.Equals(item.Role, "error", StringComparison.OrdinalIgnoreCase))
                {
                    cardsStack.Children.Add(BuildErrorCard(item, isLight, textPrimary, textSecondary, borderCol));
                }
                else if (string.Equals(item.Role, "tool", StringComparison.OrdinalIgnoreCase) ||
                         string.Equals(item.Role, "system", StringComparison.OrdinalIgnoreCase))
                {
                    cardsStack.Children.Add(new Border
                    {
                        Padding = new Thickness(8, 3),
                        CornerRadius = new CornerRadius(4),
                        Background = new SolidColorBrush(Color.FromArgb((byte)(isLight ? 14 : 22), accent.R, accent.G, accent.B)),
                        BorderBrush = new SolidColorBrush(borderCol),
                        BorderThickness = new Thickness(1),
                        Child = new TextBlock
                        {
                            Text = (string.Equals(item.Role, "tool", StringComparison.OrdinalIgnoreCase) ? "▸ " : "• ") + (item.Content ?? ""),
                            FontSize = 11,
                            Foreground = new SolidColorBrush(textSecondary),
                            TextWrapping = TextWrapping.Wrap
                        }
                    });
                }
                else
                {
                    cardsStack.Children.Add(BuildAssistantCard(item, isLight, textPrimary, textSecondary, accent, borderCol));
                }
            }
        }

        private Control BuildUserCard(AvaloniaAgentFeedItem item, int index, bool isLight, Color textPrimary, Color textSecondary, Color accent, Color borderCol)
        {
            var headerGrid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto") };
            headerGrid.Children.Add(new TextBlock
            {
                Text = "You",
                FontWeight = FontWeight.Bold,
                FontSize = 11.5,
                Foreground = new SolidColorBrush(accent),
                VerticalAlignment = VerticalAlignment.Center
            });

            var restoreBtn = new Button
            {
                Content = "↺ Restore",
                FontSize = 10.5,
                Padding = new Thickness(6, 2),
                Margin = new Thickness(4, 0, 0, 0),
                Background = Brushes.Transparent,
                Foreground = new SolidColorBrush(textSecondary)
            };
            restoreBtn.Click += (s, e) => RestoreCheckpointRequested?.Invoke(item, index);
            Grid.SetColumn(restoreBtn, 1);
            headerGrid.Children.Add(restoreBtn);

            var copyBtn = new Button
            {
                Content = "Copy",
                FontSize = 10.5,
                Padding = new Thickness(6, 2),
                Margin = new Thickness(4, 0, 0, 0),
                Background = Brushes.Transparent,
                Foreground = new SolidColorBrush(textSecondary)
            };
            copyBtn.Click += (s, e) => CodeActionRequested?.Invoke("copy", item.Content ?? "");
            Grid.SetColumn(copyBtn, 2);
            headerGrid.Children.Add(copyBtn);

            var bodyStack = new StackPanel { Spacing = 5 };
            bodyStack.Children.Add(headerGrid);
            bodyStack.Children.Add(new TextBlock
            {
                Text = item.Content ?? "",
                FontSize = UiTokens.FontUi,
                Foreground = new SolidColorBrush(textPrimary),
                TextWrapping = TextWrapping.Wrap
            });

            return new Border
            {
                Child = bodyStack,
                Background = new SolidColorBrush(AvaloniaColorHelper.ToAvColor(UiTokens.ListInactiveSelection(currentTheme, currentTheme.ChatBg))),
                BorderBrush = Brushes.Transparent,
                BorderThickness = new Thickness(0),
                CornerRadius = new CornerRadius(UiTokens.PopupRadius),
                Padding = new Thickness(10, 8)
            };
        }

        private Control BuildAssistantCard(AvaloniaAgentFeedItem item, bool isLight, Color textPrimary, Color textSecondary, Color accent, Color borderCol)
        {
            var stack = new StackPanel { Spacing = 7 };

            // Header row: ✦ Assistant + Model Badge
            var headerGrid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto") };
            headerGrid.Children.Add(new TextBlock
            {
                Text = "✦ Assistant",
                FontWeight = FontWeight.Bold,
                FontSize = 11.5,
                Foreground = new SolidColorBrush(textPrimary),
                VerticalAlignment = VerticalAlignment.Center
            });
            if (!string.IsNullOrEmpty(item.ModelBadge))
            {
                var badge = new TextBlock
                {
                    Text = item.ModelBadge,
                    FontSize = 10.5,
                    Foreground = new SolidColorBrush(textSecondary),
                    Margin = new Thickness(8, 0, 0, 0),
                    VerticalAlignment = VerticalAlignment.Center
                };
                Grid.SetColumn(badge, 1);
                headerGrid.Children.Add(badge);
            }
            stack.Children.Add(headerGrid);

            // Collapsible Thought block
            if (!string.IsNullOrWhiteSpace(item.ReasoningText))
            {
                var thoughtBtn = new Button
                {
                    Content = new LiteralTextBlock((item.ReasoningExpanded ? "▾ " : "▸ ") + "Thought for 1s") { FontSize = 11 },
                    FontSize = 11,
                    Padding = new Thickness(6, 2),
                    Background = Brushes.Transparent,
                    Foreground = new SolidColorBrush(textSecondary),
                    HorizontalAlignment = HorizontalAlignment.Left
                };
                thoughtBtn.Click += (s, e) =>
                {
                    item.ReasoningExpanded = !item.ReasoningExpanded;
                    RebuildVisualFeed();
                };
                stack.Children.Add(thoughtBtn);

                if (item.ReasoningExpanded)
                {
                    stack.Children.Add(new Border
                    {
                        BorderBrush = new SolidColorBrush(borderCol),
                        BorderThickness = new Thickness(2, 0, 0, 0),
                        Padding = new Thickness(8, 2, 4, 2),
                        Child = new TextBlock
                        {
                            Text = item.ReasoningText.Trim(),
                            FontSize = 11.5,
                            FontStyle = FontStyle.Italic,
                            Foreground = new SolidColorBrush(textSecondary),
                            TextWrapping = TextWrapping.Wrap
                        }
                    });
                }
            }

            // Collapsible Tool Calls chip
            if (item.ToolItems != null && item.ToolItems.Count > 0)
            {
                var toolBtn = new Button
                {
                    Content = new LiteralTextBlock((item.ToolsExpanded ? "▾ " : "▸ ") + $"{item.ToolItems.Count} tool call(s) executed") { FontSize = 11 },
                    FontSize = 11,
                    Padding = new Thickness(8, 3),
                    Background = new SolidColorBrush(isLight ? Color.FromRgb(236, 240, 246) : Color.FromRgb(26, 28, 36)),
                    Foreground = new SolidColorBrush(textSecondary),
                    BorderBrush = new SolidColorBrush(borderCol),
                    BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(4),
                    HorizontalAlignment = HorizontalAlignment.Left
                };
                toolBtn.Click += (s, e) =>
                {
                    item.ToolsExpanded = !item.ToolsExpanded;
                    RebuildVisualFeed();
                };
                stack.Children.Add(toolBtn);

                if (item.ToolsExpanded)
                {
                    var toolList = new StackPanel { Spacing = 2, Margin = new Thickness(8, 2, 0, 2) };
                    foreach (string tl in item.ToolItems)
                    {
                        toolList.Children.Add(new TextBlock
                        {
                            Text = "✓ " + tl,
                            FontFamily = new FontFamily("Cascadia Code, Consolas, Menlo, monospace"),
                            FontSize = 11,
                            Foreground = new SolidColorBrush(Color.FromRgb(74, 222, 128)),
                            TextWrapping = TextWrapping.Wrap
                        });
                    }
                    stack.Children.Add(toolList);
                }
            }

            // Parse markdown segments (prose + fenced code blocks)
            RenderMarkdownIntoStack(stack, item.Content ?? "", isLight, textPrimary, textSecondary, accent, borderCol);

            return new Border
            {
                Child = stack,
                Background = new SolidColorBrush(isLight ? Color.FromRgb(248, 250, 252) : Color.FromRgb(24, 25, 32)),
                BorderBrush = new SolidColorBrush(borderCol),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(10, 9)
            };
        }

        private void RenderMarkdownIntoStack(StackPanel stack, string markdown, bool isLight, Color textPrimary, Color textSecondary, Color accent, Color borderCol)
        {
            string normalized = (markdown ?? "").Replace("\r\n", "\n");
            string[] lines = normalized.Split('\n');
            bool inCode = false;
            string codeLang = "";
            var codeSb = new StringBuilder();
            var proseSb = new StringBuilder();

            void FlushProse()
            {
                if (proseSb.Length == 0) return;
                string block = proseSb.ToString().TrimEnd();
                proseSb.Clear();
                if (string.IsNullOrEmpty(block)) return;

                foreach (string pLine in block.Split('\n'))
                {
                    string trimmed = pLine.Trim();
                    if (trimmed.StartsWith("### "))
                    {
                        stack.Children.Add(new TextBlock
                        {
                            Text = trimmed.Substring(4),
                            FontWeight = FontWeight.Bold,
                            FontSize = 12.5,
                            Foreground = new SolidColorBrush(textPrimary),
                            TextWrapping = TextWrapping.Wrap,
                            Margin = new Thickness(0, 4, 0, 1)
                        });
                    }
                    else if (trimmed.StartsWith("## ") || trimmed.StartsWith("# "))
                    {
                        int cut = trimmed.StartsWith("## ") ? 3 : 2;
                        stack.Children.Add(new TextBlock
                        {
                            Text = trimmed.Substring(cut),
                            FontWeight = FontWeight.Bold,
                            FontSize = 13.5,
                            Foreground = new SolidColorBrush(textPrimary),
                            TextWrapping = TextWrapping.Wrap,
                            Margin = new Thickness(0, 5, 0, 2)
                        });
                    }
                    else if (trimmed.StartsWith("- ") || trimmed.StartsWith("* "))
                    {
                        stack.Children.Add(new TextBlock
                        {
                            Text = "  • " + trimmed.Substring(2),
                            FontSize = 12,
                            Foreground = new SolidColorBrush(textPrimary),
                            TextWrapping = TextWrapping.Wrap
                        });
                    }
                    else
                    {
                        stack.Children.Add(new TextBlock
                        {
                            Text = pLine,
                            FontSize = 12,
                            Foreground = new SolidColorBrush(textPrimary),
                            TextWrapping = TextWrapping.Wrap
                        });
                    }
                }
            }

            void FlushCode()
            {
                string codeText = codeSb.ToString().TrimEnd('\r', '\n');
                codeSb.Clear();
                stack.Children.Add(BuildFencedCodeCard(codeLang, codeText, isLight, textPrimary, textSecondary, accent, borderCol));
            }

            foreach (string line in lines)
            {
                string t = line.TrimStart();
                if (t.StartsWith("```"))
                {
                    if (!inCode)
                    {
                        FlushProse();
                        inCode = true;
                        codeLang = t.Substring(3).Trim();
                        if (string.IsNullOrEmpty(codeLang)) codeLang = "code";
                    }
                    else
                    {
                        inCode = false;
                        FlushCode();
                    }
                }
                else if (inCode)
                {
                    codeSb.AppendLine(line);
                }
                else
                {
                    proseSb.AppendLine(line);
                }
            }

            if (inCode) FlushCode();
            else FlushProse();
        }

        private Control BuildFencedCodeCard(string lang, string code, bool isLight, Color textPrimary, Color textSecondary, Color accent, Color borderCol)
        {
            var topBar = new Grid
            {
                ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto,Auto"),
                Background = new SolidColorBrush(AvaloniaColorHelper.ToAvColor(UiTokens.Blend(currentTheme.BgEditor, currentTheme.FgPrimary, isLight ? 0.04 : 0.03))),
                Margin = new Thickness(0)
            };
            topBar.Children.Add(new TextBlock
            {
                Text = string.IsNullOrEmpty(lang) ? "code" : lang,
                FontFamily = new FontFamily("Cascadia Code, Consolas, Menlo, monospace"),
                FontSize = 11,
                FontWeight = FontWeight.Normal,
                Foreground = new SolidColorBrush(textSecondary),
                Margin = new Thickness(8, 4),
                VerticalAlignment = VerticalAlignment.Center
            });

            Button MakeCodeBtn(string label, string actionKey, int col, bool primary = false)
            {
                var b = new Button
                {
                    Content = label,
                    FontSize = 11,
                    Padding = new Thickness(7, 1),
                    MinHeight = 20,
                    Margin = new Thickness(2, 2, 4, 2),
                    Background = primary ? new SolidColorBrush(accent) : Brushes.Transparent,
                    Foreground = new SolidColorBrush(primary ? Colors.White : textSecondary)
                };
                b.Classes.Add(primary ? "primary" : "flat");
                string capturedCode = code ?? "";
                b.Click += (s, e) => CodeActionRequested?.Invoke(actionKey, capturedCode);
                Grid.SetColumn(b, col);
                return b;
            }

            topBar.Children.Add(MakeCodeBtn("Copy", "copy", 1));
            topBar.Children.Add(MakeCodeBtn("Insert", "insert", 2));
            topBar.Children.Add(MakeCodeBtn("✓ Apply", "apply", 3, primary: true));

            var codeLinesStack = new StackPanel { Spacing = 1, Margin = new Thickness(8, 6) };
            bool isDiff = string.Equals(lang, "diff", StringComparison.OrdinalIgnoreCase) ||
                          (code != null && (code.Contains("\n+") || code.Contains("\n-") || code.StartsWith("@@")));

            foreach (string rawLine in (code ?? "").Replace("\r\n", "\n").Split('\n'))
            {
                Color lineFg = textPrimary;
                Color? lineBg = null;
                if (isDiff || rawLine.StartsWith("+ ") || rawLine.StartsWith("- ") || (rawLine.Length > 1 && (rawLine[0] == '+' || rawLine[0] == '-') && rawLine[1] != rawLine[0]))
                {
                    if (rawLine.StartsWith("+"))
                    {
                        lineFg = isLight ? Color.FromRgb(21, 128, 61) : Color.FromRgb(74, 222, 128);
                        lineBg = isLight ? Color.FromArgb(40, 34, 197, 94) : Color.FromArgb(45, 34, 197, 94);
                    }
                    else if (rawLine.StartsWith("-"))
                    {
                        lineFg = isLight ? Color.FromRgb(185, 28, 28) : Color.FromRgb(248, 113, 113);
                        lineBg = isLight ? Color.FromArgb(40, 239, 68, 68) : Color.FromArgb(45, 239, 68, 68);
                    }
                    else if (rawLine.StartsWith("@@"))
                    {
                        lineFg = accent;
                    }
                }

                var tb = new TextBlock
                {
                    Text = rawLine,
                    FontFamily = new FontFamily("Cascadia Code, Consolas, Menlo, monospace"),
                    FontSize = 11.5,
                    Foreground = new SolidColorBrush(lineFg),
                    TextWrapping = TextWrapping.NoWrap
                };
                if (lineBg.HasValue)
                {
                    codeLinesStack.Children.Add(new Border
                    {
                        Background = new SolidColorBrush(lineBg.Value),
                        Padding = new Thickness(4, 1),
                        CornerRadius = new CornerRadius(2),
                        Child = tb
                    });
                }
                else
                {
                    codeLinesStack.Children.Add(tb);
                }
            }

            var outer = new StackPanel();
            outer.Children.Add(topBar);
            outer.Children.Add(codeLinesStack);

            return new Border
            {
                Child = outer,
                Background = new SolidColorBrush(AvaloniaColorHelper.ToAvColor(currentTheme.BgEditor)),
                BorderBrush = new SolidColorBrush(borderCol),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(UiTokens.ToolbarRadius),
                ClipToBounds = true
            };
        }

        private Control BuildDiffReviewCard(AvaloniaAgentFeedItem item, bool isLight, Color textPrimary, Color textSecondary, Color accent, Color borderCol)
        {
            var stack = new StackPanel { Spacing = 6 };
            var headerGrid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto") };

            headerGrid.Children.Add(new TextBlock
            {
                Text = $"Proposed Edit: {System.IO.Path.GetFileName(item.DiffFilePath ?? "active file")}",
                FontWeight = FontWeight.SemiBold,
                FontSize = UiTokens.FontUi,
                Foreground = new SolidColorBrush(textPrimary),
                VerticalAlignment = VerticalAlignment.Center
            });

            var statsText = new TextBlock
            {
                Text = $"+{item.DiffAddedLines}  -{item.DiffRemovedLines}",
                FontFamily = new FontFamily("Cascadia Code, Consolas, Menlo, monospace"),
                FontWeight = FontWeight.Bold,
                FontSize = 11,
                Foreground = new SolidColorBrush(Color.FromRgb(74, 222, 128)),
                Margin = new Thickness(8, 0),
                VerticalAlignment = VerticalAlignment.Center
            };
            Grid.SetColumn(statsText, 1);
            headerGrid.Children.Add(statsText);

            var statusLabel = new TextBlock
            {
                Text = item.DiffStatus ?? "Pending",
                FontSize = 10.5,
                Foreground = new SolidColorBrush(textSecondary),
                VerticalAlignment = VerticalAlignment.Center
            };
            Grid.SetColumn(statusLabel, 2);
            headerGrid.Children.Add(statusLabel);
            stack.Children.Add(headerGrid);

            if (!string.IsNullOrEmpty(item.DiffPreviewText))
            {
                stack.Children.Add(BuildFencedCodeCard("diff", item.DiffPreviewText, isLight, textPrimary, textSecondary, accent, borderCol));
            }

            var btnRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            var acceptBtn = new Button
            {
                Content = "Accept Changes",
                FontSize = UiTokens.FontSmall,
                Padding = new Thickness(10, 3),
                MinHeight = 24
            };
            WorkbenchStyles.StylePrimaryButton(acceptBtn, currentTheme);
            var rejectBtn = new Button
            {
                Content = "Reject",
                FontSize = UiTokens.FontSmall,
                Padding = new Thickness(10, 3),
                MinHeight = 24
            };
            WorkbenchStyles.StyleSecondaryButton(rejectBtn, currentTheme);
            acceptBtn.Click += (s, e) =>
            {
                item.DiffStatus = "Accepted";
                DiffDecisionClicked?.Invoke(item, true);
                RebuildVisualFeed();
            };
            rejectBtn.Click += (s, e) =>
            {
                item.DiffStatus = "Rejected";
                DiffDecisionClicked?.Invoke(item, false);
                RebuildVisualFeed();
            };
            btnRow.Children.Add(acceptBtn);
            btnRow.Children.Add(rejectBtn);
            stack.Children.Add(btnRow);

            return new Border
            {
                Child = stack,
                Background = new SolidColorBrush(AvaloniaColorHelper.ToAvColor(UiTokens.WidgetBackground(currentTheme))),
                BorderBrush = new SolidColorBrush(AvaloniaColorHelper.ToAvColor(UiTokens.WidgetBorder(currentTheme))),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(UiTokens.PopupRadius),
                Padding = new Thickness(10, 8)
            };
        }

        private Control BuildCommandApprovalCard(AvaloniaAgentFeedItem item, bool isLight, Color textPrimary, Color textSecondary, Color accent, Color borderCol)
        {
            var stack = new StackPanel { Spacing = 6 };
            stack.Children.Add(new TextBlock
            {
                Text = "△ Tool Approval Required — Run Shell Command",
                FontWeight = FontWeight.Bold,
                FontSize = 11.5,
                Foreground = new SolidColorBrush(Color.FromRgb(250, 204, 21))
            });
            stack.Children.Add(new Border
            {
                Background = new SolidColorBrush(isLight ? Color.FromRgb(241, 245, 249) : Color.FromRgb(15, 17, 23)),
                BorderBrush = new SolidColorBrush(borderCol),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(4),
                Padding = new Thickness(8, 5),
                Child = new TextBlock
                {
                    Text = "$ " + (item.CommandText ?? ""),
                    FontFamily = new FontFamily("Cascadia Code, Consolas, Menlo, monospace"),
                    FontSize = 11.5,
                    Foreground = new SolidColorBrush(textPrimary),
                    TextWrapping = TextWrapping.Wrap
                }
            });

            var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            var runBtn = new Button
            {
                Content = "▶ Run in Terminal",
                FontSize = 11,
                Padding = new Thickness(10, 4),
                Background = new SolidColorBrush(accent),
                Foreground = Brushes.White
            };
            var skipBtn = new Button
            {
                Content = "Skip",
                FontSize = 11,
                Padding = new Thickness(10, 4)
            };
            runBtn.Click += (s, e) =>
            {
                item.CommandStatus = "Approved";
                CommandApprovalClicked?.Invoke(item, true);
                RebuildVisualFeed();
            };
            skipBtn.Click += (s, e) =>
            {
                item.CommandStatus = "Skipped";
                CommandApprovalClicked?.Invoke(item, false);
                RebuildVisualFeed();
            };
            actions.Children.Add(runBtn);
            actions.Children.Add(skipBtn);
            stack.Children.Add(actions);

            return new Border
            {
                Child = stack,
                Background = new SolidColorBrush(isLight ? Color.FromRgb(254, 252, 232) : Color.FromRgb(32, 28, 20)),
                BorderBrush = new SolidColorBrush(Color.FromRgb(202, 138, 4)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(10, 8)
            };
        }

        private Control BuildErrorCard(AvaloniaAgentFeedItem item, bool isLight, Color textPrimary, Color textSecondary, Color borderCol)
        {
            var stack = new StackPanel { Spacing = 6 };
            stack.Children.Add(new TextBlock
            {
                Text = "△ AI Provider Notice",
                FontWeight = FontWeight.Bold,
                FontSize = 11.5,
                Foreground = new SolidColorBrush(Color.FromRgb(248, 113, 113))
            });
            stack.Children.Add(new TextBlock
            {
                Text = item.Content ?? "",
                FontSize = 12,
                Foreground = new SolidColorBrush(textPrimary),
                TextWrapping = TextWrapping.Wrap
            });
            var cfgBtn = new Button
            {
                Content = "⋯ Open AI Settings & BYOK Keys",
                FontSize = 11,
                Padding = new Thickness(10, 4),
                HorizontalAlignment = HorizontalAlignment.Left
            };
            cfgBtn.Click += (s, e) => OpenSettingsRequested?.Invoke();
            stack.Children.Add(cfgBtn);

            return new Border
            {
                Child = stack,
                Background = new SolidColorBrush(isLight ? Color.FromRgb(254, 242, 242) : Color.FromRgb(38, 22, 26)),
                BorderBrush = new SolidColorBrush(Color.FromRgb(239, 68, 68)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(10, 8)
            };
        }
    }
}
