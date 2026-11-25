using System;
using System.Collections.Generic;
using System.IO;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using codeLess.Studio;

namespace codeLess.AvaloniaApp
{
    /// <summary>
    /// VS Code-style Workspace Trust UI (Avalonia): the first-open prompt ("Do you trust the authors of the files in this
    /// folder?"), the "Restricted Mode" status-bar item, the Restricted Mode banner above the editor, the Manage Workspace
    /// Trust dialog (also reachable from Settings) and live re-evaluation when trust changes (language servers restart,
    /// git refreshes, interpreters are re-discovered - no restart needed). The rules live in Core (<see cref="WorkspaceTrust"/>).
    /// </summary>
    public partial class MainWindow
    {
        private Button statusRestrictedBtn;
        private Border restrictedBanner;
        private string restrictedBannerDismissedFor;
        private UiPost trustUi;
        private bool trustWindowOpen;

        /// <summary>Tests: show the first-open prompt in headless runs too (it is skipped there by default).</summary>
        public static bool ShowTrustPromptInHeadless;

        /// <summary>True when a folder is open and it isn't trusted.</summary>
        public bool IsWorkspaceRestricted => !string.IsNullOrEmpty(workspaceRoot) && WorkspaceTrust.IsRestricted(workspaceRoot);
        public bool IsRestrictedModeStatusVisible => statusRestrictedBtn != null && statusRestrictedBtn.IsVisible;
        public bool IsRestrictedModeBannerVisible => restrictedBanner != null && restrictedBanner.IsVisible;
        public Border RestrictedModeBannerControl => restrictedBanner;

        private void InitializeWorkspaceTrust()
        {
            trustUi = UiPost.CaptureCurrent();
            Opened += (s, e) => { trustWindowOpen = true; WorkspaceTrust.TrustChanged += OnWorkspaceTrustChangedAnyThread; ApplyWorkspaceTrustState(); };
            Closed += (s, e) => { trustWindowOpen = false; WorkspaceTrust.TrustChanged -= OnWorkspaceTrustChangedAnyThread; };
        }

        /// <summary>Raised by Core on any thread (also for changes made in another window / process).</summary>
        private void OnWorkspaceTrustChangedAnyThread()
        {
            var ui = trustUi;
            if (ui == null || !trustWindowOpen) return;
            ui.Post(() => { if (trustWindowOpen) OnWorkspaceTrustChanged(); });
        }

        /// <summary>Re-enables / disables trust-gated features at once (UI thread).</summary>
        public void OnWorkspaceTrustChanged()
        {
            ApplyWorkspaceTrustState();
            try { lsp?.OnWorkspaceTrustChanged(); } catch { }
            if (!string.IsNullOrEmpty(workspaceRoot))
            {
                try { RefreshGitStatusInBackground(); } catch { }
                try { PythonInterpreterService.RefreshInBackground(workspaceRoot); } catch { }
            }
        }

        /// <summary>Called by LoadWorkspace: updates the Restricted Mode indicators and asks on first open.</summary>
        private void OnWorkspaceTrustFolderOpened()
        {
            ApplyWorkspaceTrustState();
            if (string.IsNullOrEmpty(workspaceRoot) || !WorkspaceTrust.NeedsPrompt(workspaceRoot)) return;
            if (!AvaloniaTerminalEmulatorControl.IsDesktopRuntime && !ShowTrustPromptInHeadless) return;
            ShowWorkspaceTrustPrompt();
        }

        private void ApplyWorkspaceTrustState()
        {
            bool restricted = IsWorkspaceRestricted;
            if (statusRestrictedBtn != null)
            {
                statusRestrictedBtn.IsVisible = restricted;
                ToolTip.SetTip(statusRestrictedBtn, "Restricted Mode: tasks, debugging, git, workspace language servers and verification are disabled. Click to manage Workspace Trust.");
            }
            if (restrictedBanner != null)
                restrictedBanner.IsVisible = restricted && !string.Equals(restrictedBannerDismissedFor, workspaceRoot, StringComparison.Ordinal);
            if (restricted && currentTheme != null && restrictedBanner != null) ThemeRestrictedBanner(currentTheme);
        }

        // ------------------------------------------------------------------ status bar / banner

        internal Control BuildRestrictedModeStatusItem()
        {
            var content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, VerticalAlignment = VerticalAlignment.Center };
            content.Children.Add(new VectorIconControl { IconName = "shield", Width = 12, Height = 12, IconColor = Colors.White, VerticalAlignment = VerticalAlignment.Center });
            content.Children.Add(new TextBlock { Text = "Restricted Mode", FontSize = UiTokens.FontSmall, VerticalAlignment = VerticalAlignment.Center });
            statusRestrictedBtn = MakeStatusBarItem(content, ShowManageWorkspaceTrust, isRemoteBadge: true);
            statusRestrictedBtn.Background = new SolidColorBrush(Color.FromRgb(0x8A, 0x5A, 0x00));
            statusRestrictedBtn.IsVisible = false;
            return statusRestrictedBtn;
        }

        internal Control WrapWithRestrictedModeBanner(Control editorArea)
        {
            var dock = new DockPanel { LastChildFill = true };
            var text = new TextBlock
            {
                Text = WorkspaceTrust.BannerText,
                VerticalAlignment = VerticalAlignment.Center,
                TextWrapping = TextWrapping.Wrap,
                FontSize = UiTokens.FontUi,
                Margin = new Thickness(8, 0, 8, 0)
            };
            var manage = new Button { Name = "RestrictedBannerManageBtn", Content = "Manage", Padding = new Thickness(10, 2), VerticalAlignment = VerticalAlignment.Center };
            manage.Click += (s, e) => ShowManageWorkspaceTrust();
            var learn = new Button { Name = "RestrictedBannerLearnMoreBtn", Content = "Learn More", Padding = new Thickness(10, 2), VerticalAlignment = VerticalAlignment.Center };
            learn.Click += (s, e) => EditorLinkResolver.OpenExternalUrl(WorkspaceTrust.LearnMoreUrl);
            var close = new Button { Name = "RestrictedBannerCloseBtn", Content = "✕", Padding = new Thickness(8, 2), VerticalAlignment = VerticalAlignment.Center, Background = Brushes.Transparent, BorderThickness = new Thickness(0) };
            ToolTip.SetTip(close, "Hide (Restricted Mode stays on)");
            close.Click += (s, e) => { restrictedBannerDismissedFor = workspaceRoot; ApplyWorkspaceTrustState(); };
            var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto,Auto,Auto"), Margin = new Thickness(8, 4) };
            var icon = new VectorIconControl { IconName = "shield", Width = 14, Height = 14, VerticalAlignment = VerticalAlignment.Center };
            Grid.SetColumn(icon, 0); grid.Children.Add(icon);
            Grid.SetColumn(text, 1); grid.Children.Add(text);
            Grid.SetColumn(manage, 2); grid.Children.Add(manage);
            Grid.SetColumn(learn, 3); grid.Children.Add(learn);
            Grid.SetColumn(close, 4); grid.Children.Add(close);
            manage.Margin = new Thickness(0, 0, 6, 0);
            learn.Margin = new Thickness(0, 0, 6, 0);
            restrictedBanner = new Border { Child = grid, BorderThickness = new Thickness(0, 0, 0, 1), IsVisible = false };
            DockPanel.SetDock(restrictedBanner, Dock.Top);
            dock.Children.Add(restrictedBanner);
            dock.Children.Add(editorArea);
            return dock;
        }

        private void ThemeRestrictedBanner(StudioTheme theme)
        {
            if (restrictedBanner == null || theme == null) return;
            bool isLight = theme.IsLight;
            // VS Code warning banner & button tokens (dark_modern.json / light_modern.json):
            restrictedBanner.Background = new SolidColorBrush(isLight ? Color.FromRgb(0xFF, 0xF4, 0xCE) : Color.FromRgb(0x35, 0x2A, 0x05));
            restrictedBanner.BorderBrush = new SolidColorBrush(AvaloniaColorHelper.ToAvColor(theme.BorderSubtle));
            var fg = new SolidColorBrush(AvaloniaColorHelper.ToAvColor(theme.TextPrimary));
            Color iconCol = isLight ? Color.FromRgb(0x89, 0x55, 0x03) : Color.FromRgb(0xE2, 0xC0, 0x8D);
            if (statusRestrictedBtn != null)
            {
                statusRestrictedBtn.Background = new SolidColorBrush(Color.FromRgb(0x89, 0x55, 0x03));
                statusRestrictedBtn.Foreground = Brushes.White;
            }
            if (restrictedBanner.Child is Grid g)
                foreach (var c in g.Children)
                {
                    if (c is TextBlock tb) tb.Foreground = fg;
                    if (c is VectorIconControl ic) { ic.IconColor = iconCol; ic.InvalidateVisual(); }
                    if (c is Button b)
                    {
                        if (b.Name == "RestrictedBannerManageBtn")
                        {
                            WorkbenchStyles.StylePrimaryButton(b, theme);
                            b.BorderThickness = new Thickness(1);
                            b.BorderBrush = new SolidColorBrush(isLight ? Color.FromArgb(0x1A, 0x00, 0x00, 0x00) : Color.FromArgb(0x1A, 0xFF, 0xFF, 0xFF));
                        }
                        else if (b.Name == "RestrictedBannerLearnMoreBtn")
                        {
                            if (b.Classes.Contains("primary")) b.Classes.Remove("primary");
                            b.Background = new SolidColorBrush(isLight ? Color.FromRgb(0xE5, 0xE5, 0xE5) : Color.FromRgb(0x31, 0x31, 0x31));
                            b.Foreground = fg;
                            b.BorderThickness = new Thickness(1);
                            b.BorderBrush = new SolidColorBrush(isLight ? Color.FromRgb(0xCE, 0xCE, 0xCE) : Color.FromRgb(0x3C, 0x3C, 0x3C));
                            b.CornerRadius = new CornerRadius(UiTokens.ControlRadius);
                        }
                        else
                        {
                            b.Background = Brushes.Transparent;
                            b.Foreground = fg;
                            b.BorderThickness = new Thickness(0);
                        }
                    }
                }
        }

        // ------------------------------------------------------------------ prompt / manage

        /// <summary>The first-open prompt: Trust Folder &amp; Continue / Restricted Mode, optionally trusting the parent folder.</summary>
        public void ShowWorkspaceTrustPrompt()
        {
            string folder = workspaceRoot;
            if (string.IsNullOrEmpty(folder)) return;
            var fg = new SolidColorBrush(AvaloniaColorHelper.ToAvColor(currentTheme.TextPrimary));
            var fg2 = new SolidColorBrush(AvaloniaColorHelper.ToAvColor(currentTheme.TextSecondary));
            var stack = new StackPanel { Spacing = 12 };
            stack.Children.Add(new TextBlock { Text = WorkspaceTrust.PromptBody, TextWrapping = TextWrapping.Wrap, FontSize = 12.5, Foreground = fg });
            stack.Children.Add(new TextBlock { Text = folder, TextWrapping = TextWrapping.Wrap, FontSize = UiTokens.FontSmall, Foreground = fg2 });
            string parent = WorkspaceTrust.ParentFolderForPrompt(folder);
            CheckBox parentChk = null;
            if (parent != null)
            {
                parentChk = new CheckBox { Content = "Trust the authors of all files in the parent folder '" + Path.GetFileName(parent) + "'", Foreground = fg };
                ToolTip.SetTip(parentChk, parent);
                stack.Children.Add(parentChk);
            }
            var row = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 8 };
            var trust = new Button { Content = "Yes, I trust the authors", Padding = new Thickness(14, 5) };
            WorkbenchStyles.StylePrimaryButton(trust, currentTheme);
            var restrict = new Button { Content = "No, I don't trust the authors (Restricted Mode)", Padding = new Thickness(14, 5) };
            trust.Click += (s, e) =>
            {
                ModalHost.CloseModal();
                WorkspaceTrust.ApplyPromptAnswer(folder, true, parentChk?.IsChecked == true);
                SetStatusMessage("Trusted " + folder + ": all features are enabled.");
            };
            restrict.Click += (s, e) =>
            {
                ModalHost.CloseModal();
                WorkspaceTrust.ApplyPromptAnswer(folder, false, false);
                SetStatusMessage("Restricted Mode: codeLess won't run anything from this folder. Use Manage Workspace Trust to change it.");
            };
            row.Children.Add(trust);
            row.Children.Add(restrict);
            stack.Children.Add(row);
            ModalHost.ShowModal("workspace-trust", WorkspaceTrust.PromptTitle, stack, 560);
            MainUi.Post(() => restrict.Focus(), Avalonia.Threading.DispatcherPriority.Loaded);
        }

        /// <summary>Manage Workspace Trust: trust / restrict the open folder and revoke trusted folders.</summary>
        public void ShowManageWorkspaceTrust()
        {
            var content = BuildWorkspaceTrustManager(currentTheme, () => ShowManageWorkspaceTrust());
            ModalHost.ShowModal("workspace-trust-manage", "Workspace Trust", content, 620);
        }

        /// <summary>The trust manager body (also embedded in Settings). Changes apply immediately.</summary>
        internal Control BuildWorkspaceTrustManager(StudioTheme theme, Action rebuild)
        {
            var fg = new SolidColorBrush(AvaloniaColorHelper.ToAvColor(theme.TextPrimary));
            var fg2 = new SolidColorBrush(AvaloniaColorHelper.ToAvColor(theme.TextSecondary));
            var stack = new StackPanel { Spacing = 8 };
            if (!string.IsNullOrEmpty(workspaceRoot))
            {
                bool trusted = WorkspaceTrust.IsTrusted(workspaceRoot);
                stack.Children.Add(new TextBlock
                {
                    Text = (trusted ? "This folder is trusted: all features are enabled." : "This folder is in Restricted Mode: codeLess won't run tasks, debugging, git, workspace language servers / interpreters or verification here.") +
                           "\n" + workspaceRoot,
                    TextWrapping = TextWrapping.Wrap,
                    Foreground = fg
                });
                var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
                if (!trusted)
                {
                    var t = new Button { Content = "Trust Folder", Padding = new Thickness(12, 4) };
                    WorkbenchStyles.StylePrimaryButton(t, theme);
                    t.Click += (s, e) => { WorkspaceTrust.Trust(workspaceRoot); rebuild?.Invoke(); };
                    row.Children.Add(t);
                    string parent = WorkspaceTrust.ParentFolderForPrompt(workspaceRoot);
                    if (parent != null)
                    {
                        var tp = new Button { Content = "Trust Parent Folder", Padding = new Thickness(12, 4) };
                        ToolTip.SetTip(tp, parent);
                        tp.Click += (s, e) => { WorkspaceTrust.Trust(parent); rebuild?.Invoke(); };
                        row.Children.Add(tp);
                    }
                }
                else
                {
                    string covering = WorkspaceTrust.TrustedRootFor(workspaceRoot);
                    var r = new Button { Content = "Don't Trust (Restricted Mode)", Padding = new Thickness(12, 4) };
                    if (covering != null && !string.Equals(LspText.PathKey(covering), LspText.PathKey(workspaceRoot), StringComparison.Ordinal))
                    {
                        r.IsEnabled = false;
                        ToolTip.SetTip(r, "Trusted through its parent folder " + covering + ": remove that entry below first.");
                    }
                    r.Click += (s, e) => { WorkspaceTrust.Restrict(workspaceRoot); rebuild?.Invoke(); };
                    row.Children.Add(r);
                }
                stack.Children.Add(row);
            }
            stack.Children.Add(new TextBlock { Text = "Trusted Folders", FontWeight = FontWeight.SemiBold, Foreground = fg, Margin = new Thickness(0, 6, 0, 0) });
            List<string> folders = WorkspaceTrust.GetTrustedFolders();
            if (folders.Count == 0) stack.Children.Add(new TextBlock { Text = "No trusted folders yet.", Foreground = fg2 });
            foreach (string f in folders)
            {
                string folder = f;
                var g = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
                var label = new TextBlock { Text = folder, Foreground = fg, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
                ToolTip.SetTip(label, folder);
                var rm = new Button { Content = "Remove", Padding = new Thickness(10, 2) };
                rm.Click += (s, e) => { WorkspaceTrust.Revoke(folder); rebuild?.Invoke(); };
                Grid.SetColumn(rm, 1);
                g.Children.Add(label);
                g.Children.Add(rm);
                stack.Children.Add(g);
            }
            var enabled = new CheckBox { Content = "Enable Workspace Trust (ask before running code from new folders)", IsChecked = WorkspaceTrust.Enabled, Foreground = fg, Margin = new Thickness(0, 6, 0, 0) };
            ToolTip.SetTip(enabled, "security.workspace.trust.enabled - when off, every folder is trusted.");
            enabled.IsCheckedChanged += (s, e) => { WorkspaceTrust.Enabled = enabled.IsChecked == true; };
            stack.Children.Add(enabled);
            return stack;
        }

        private List<PaletteEntryItem> BuildWorkspaceTrustPaletteCommands()
        {
            return new List<PaletteEntryItem>
            {
                new PaletteEntryItem { Category = "Workspaces", Title = "Manage Workspace Trust", Execute = ShowManageWorkspaceTrust },
                new PaletteEntryItem { Category = "Workspaces", Title = "Trust This Folder", Execute = () => { if (!string.IsNullOrEmpty(workspaceRoot)) WorkspaceTrust.Trust(workspaceRoot); } },
                new PaletteEntryItem { Category = "Workspaces", Title = "Restricted Mode: Don't Trust This Folder", Execute = () => { if (!string.IsNullOrEmpty(workspaceRoot)) WorkspaceTrust.Restrict(workspaceRoot); } }
            };
        }
    }
}
