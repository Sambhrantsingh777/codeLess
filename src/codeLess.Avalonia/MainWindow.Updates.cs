using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.LogicalTree;
using Avalonia.Media;
using Avalonia.Threading;
using codeLess.Studio;

namespace codeLess.AvaloniaApp
{
    /// <summary>
    /// VS Code-style updates: Check for Updates... → Downloading Update... → Restart to Update (menu items change label,
    /// a status-bar item appears while downloading and when an update is ready, a notification offers Restart Now / Later /
    /// Release Notes). The download is verified against SHA256SUMS.txt and installed by a helper after the app quits
    /// (<see cref="UpdateService"/>, <see cref="UpdateInstaller"/>). Settings › Update mode: none / manual / start / default.
    /// </summary>
    public partial class MainWindow
    {
        public static string CurrentAppVersion
        {
            get
            {
                var v = typeof(MainWindow).Assembly.GetName().Version;
                return v == null ? "0.0.0" : $"{v.Major}.{v.Minor}.{Math.Max(0, v.Build)}";
            }
        }

        private const string UpdateMenuId = "app.checkUpdates";
        private readonly List<MenuItem> updateMenuItems = new List<MenuItem>();
        private readonly List<NativeMenuItem> updateNativeMenuItems = new List<NativeMenuItem>();
        private Button statusUpdateBtn;
        private DispatcherTimer updatePeriodicTimer;
        private UpdateState lastSeenUpdateState = UpdateState.Idle;
        private bool updateCheckInteractive;
        private bool updatesInitialized;

        private static UpdateService sharedUpdates;

        /// <summary>The process-wide update service (one download for every window).</summary>
        public static UpdateService Updates => sharedUpdates ??= new UpdateService(CurrentAppVersion,
            UpdateEnvironment.DetectCurrent(Environment.ProcessPath, Environment.ProcessId), Path.Combine(Path.GetTempPath(), "codeLess-update"));

        /// <summary>Tests: replaces the shared service (null = recreate on next use).</summary>
        public static void SetUpdateServiceForTests(UpdateService service) => sharedUpdates = service;

        /// <summary>Current label of the Check for Updates menu item (test hook).</summary>
        public string UpdateMenuLabel => Updates.MenuLabel;
        /// <summary>True while the status-bar update item is shown (test hook).</summary>
        public bool IsUpdateStatusItemVisible => statusUpdateBtn != null && statusUpdateBtn.IsVisible;

        private void InitUpdates()
        {
            if (updatesInitialized) return;
            updatesInitialized = true;
            Opened += (s, e) =>
            {
                Updates.Changed -= OnUpdatesChangedAnyThread;
                Updates.Changed += OnUpdatesChangedAnyThread;
                lastSeenUpdateState = Updates.State;
                ApplyUpdateMenuState();
            };
            Closed += (s, e) =>
            {
                Updates.Changed -= OnUpdatesChangedAnyThread;
                updatePeriodicTimer?.Stop();
            };
        }

        private void OnUpdatesChangedAnyThread(object sender, EventArgs e)
        {
            if (windowClosed) return;
            MainUi.Post(OnUpdatesChanged);
        }

        private void OnUpdatesChanged()
        {
            if (windowClosed) return;
            var u = Updates;
            ApplyUpdateMenuState();
            var prev = lastSeenUpdateState;
            lastSeenUpdateState = u.State;
            if (prev == u.State) return;
            if (u.State == UpdateState.Ready && IsActiveUpdateWindow()) ShowUpdateReadyNotification();
            else if (u.State == UpdateState.Error && prev == UpdateState.Downloading && IsActiveUpdateWindow())
                ShowUpdateNotification("error", "The update could not be downloaded: " + u.LastError, new[] { ("Release Notes", (Action)(() => UpdateChecker.OpenInBrowser(u.Release?.ReleasePageUrl))) });
        }

        /// <summary>Only one window shows update notifications (the focused one, else the main one).</summary>
        private bool IsActiveUpdateWindow()
        {
            if (IsActive) return true;
            if (Application.Current?.ApplicationLifetime is Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime dt)
            {
                foreach (var w in dt.Windows) if (w is MainWindow m && m.IsActive) return false;
                return ReferenceEquals(dt.MainWindow, this);
            }
            return true;
        }

        /// <summary>Menu labels (in-window menu, native Help menu, app menu), status-bar item.</summary>
        private void ApplyUpdateMenuState()
        {
            var u = Updates;
            string label = u.MenuLabel;
            bool enabled = u.MenuEnabled;
            foreach (var mi in updateMenuItems)
            {
                mi.Header = new LiteralTextBlock(label);
                mi.IsEnabled = enabled;
            }
            foreach (var ni in updateNativeMenuItems)
            {
                if (ni.Header != label) ni.Header = label;
                if (ni.IsEnabled != enabled) ni.IsEnabled = enabled;
            }
            AppNativeMenu.SetUpdateItem(label, enabled);
            RefreshUpdateStatusItem();
        }

        private MenuItem MakeUpdateGearItem()
        {
            var mi = new MenuItem { Header = new LiteralTextBlock(Updates.MenuLabel), IsEnabled = Updates.MenuEnabled };
            mi.Click += (s, e) => CheckForUpdatesInteractive();
            return mi;
        }

        private Button BuildUpdateStatusItem()
        {
            statusUpdateBtn = MakeStatusBarItem(new IconLabel("sync", "Restart to Update", 13, UiTokens.FontSmall, "⟳ "), OnUpdateStatusItemClicked);
            statusUpdateBtn.IsVisible = false;
            RefreshUpdateStatusItem();
            return statusUpdateBtn;
        }

        private void RefreshUpdateStatusItem()
        {
            if (statusUpdateBtn == null) return;
            var u = Updates;
            string text = null, tip = null;
            if (u.State == UpdateState.Downloading)
            {
                int pct = u.ProgressPercent;
                text = pct >= 0 ? $"Downloading Update ({pct}%)" : "Downloading Update...";
                tip = $"Downloading codeLess {u.Release?.LatestVersion} — click to cancel";
            }
            else if (u.State == UpdateState.Ready)
            {
                text = u.MenuLabel;
                tip = u.ReadyMessage;
            }
            statusUpdateBtn.IsVisible = text != null;
            if (text == null) return;
            if (statusUpdateBtn.Content is IconLabel il) il.Text = text;
            else statusUpdateBtn.Content = new LiteralTextBlock(text);
            ToolTip.SetTip(statusUpdateBtn, tip);
        }

        private void OnUpdateStatusItemClicked()
        {
            var u = Updates;
            if (u.State == UpdateState.Downloading)
            {
                ShowUpdateNotification("info", $"Downloading codeLess {u.Release?.LatestVersion}...", new[]
                {
                    ("Cancel Download", (Action)(() => { u.CancelDownload(); SetStatusMessage("Update download cancelled."); }))
                });
            }
            else CheckForUpdatesInteractive();
        }

        /// <summary>Automatic check on launch (mode start / default), then every 6 h in default mode.</summary>
        public void MaybeAutoCheckForUpdates()
        {
            if (settings == null || !AvaloniaTerminalEmulatorControl.IsDesktopRuntime) return;
            string mode = settings.GetEffectiveUpdateMode();
            if (mode == UpdateModes.Default && updatePeriodicTimer == null)
            {
                updatePeriodicTimer = new DispatcherTimer { Interval = TimeSpan.FromHours(6) };
                updatePeriodicTimer.Tick += (s, e) => MaybeAutoCheckForUpdates();
                updatePeriodicTimer.Start();
            }
            // a previous update's backup is removed once the new copy runs
            Task.Run(() => UpdateInstaller.CleanupStaleBackup(Updates.Environment));
            if (!UpdateService.ShouldAutoCheck(mode, settings.LastUpdateCheckUtc, NetworkLedger.OfflineMode, 1.0, DateTime.UtcNow)) return;
            RunUpdateCheck(interactive: false);
        }

        /// <summary>Help / app menu › Check for Updates... (the label and action follow the update state).</summary>
        public void CheckForUpdatesInteractive()
        {
            var u = Updates;
            switch (u.State)
            {
                case UpdateState.Checking:
                case UpdateState.Downloading:
                    OnUpdateStatusItemClicked();
                    return;
                case UpdateState.Ready:
                    if (u.Plan != null && u.Plan.CanInstall) RestartToUpdate();
                    else ShowManualInstallModal();
                    return;
                case UpdateState.Available:
                    if (u.Release != null) { ShowUpdateAvailableModal(u.Release); return; }
                    break;
            }
            if (settings != null && settings.GetEffectiveUpdateMode() == UpdateModes.None)
            {
                ShowUpdateNotification("info", "Updates are turned off (Settings › Network & Updates › Update mode: None).", new[]
                {
                    ("Open Settings", (Action)ShowSettingsModal),
                    ("Check Once", (Action)(() => RunUpdateCheck(interactive: true)))
                });
                return;
            }
            RunUpdateCheck(interactive: true);
        }

        private void RunUpdateCheck(bool interactive)
        {
            var u = Updates;
            if (u.State == UpdateState.Checking || u.State == UpdateState.Downloading) return;
            if (u.State == UpdateState.Error) u.ClearError();
            updateCheckInteractive = interactive;
            if (interactive) SetStatusMessage("Checking for updates...");
            Task.Run(() => u.CheckNow(8000)).ContinueWith(t =>
            {
                MainUi.Post(() =>
                {
                    if (windowClosed) return;
                    var r = t.Status == TaskStatus.RanToCompletion && t.Result != null ? t.Result : new UpdateCheckResult { Error = "Update check failed." };
                    if (r.Success)
                    {
                        settings.LastUpdateCheckUtc = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture);
                        try { settings.Save(); } catch { }
                    }
                    if (!r.Success)
                    {
                        if (interactive)
                        {
                            SetStatusMessage("");
                            ShowMessageModal("Check for Updates", (r.Blocked ? "Offline Mode is on, so codeLess did not contact GitHub.\n\n" : "Could not check for updates.\n\n") + r.Error);
                        }
                        u.ClearError();
                        return;
                    }
                    if (!r.IsNewer)
                    {
                        if (interactive) { SetStatusMessage(""); ShowMessageModal("Check for Updates", $"There are currently no updates available. codeLess {CurrentAppVersion} is the latest version."); }
                        return;
                    }
                    if (!interactive && string.Equals(settings.SkippedUpdateVersion, r.LatestVersion, StringComparison.OrdinalIgnoreCase))
                        return;
                    if (!interactive && settings.GetEffectiveUpdateMode() == UpdateModes.Default)
                    {
                        // VS Code default: download in the background, then offer Restart to Update
                        u.StartDownload();
                        return;
                    }
                    SetStatusMessage("");
                    ShowUpdateAvailableModal(r);
                });
            });
        }

        private void StartUpdateDownload()
        {
            var u = Updates;
            if (u.StartDownload())
                SetStatusMessage($"Downloading codeLess {u.Release?.LatestVersion}...");
        }

        private void ShowUpdateAvailableModal(UpdateCheckResult r)
        {
            Color textPrimary = AvaloniaColorHelper.ToAvColor(currentTheme.TextPrimary);
            var stack = new StackPanel { Spacing = 10 };
            stack.Children.Add(new TextBlock
            {
                Text = $"codeLess {r.LatestVersion} is available — you have {r.CurrentVersion}.",
                FontSize = 13.5,
                FontWeight = FontWeight.SemiBold,
                TextWrapping = TextWrapping.Wrap,
                Foreground = new SolidColorBrush(textPrimary)
            });
            var env = Updates.Environment;
            stack.Children.Add(new TextBlock
            {
                Text = env.SupportsAutomaticInstall
                    ? "codeLess downloads the update, verifies its SHA-256 checksum and installs it when you restart."
                    : "codeLess downloads the update and verifies its SHA-256 checksum; you then install it yourself (instructions follow).",
                TextWrapping = TextWrapping.Wrap,
                FontSize = 12,
                Foreground = new SolidColorBrush(textPrimary)
            });
            string notes = (r.ReleaseNotes ?? "").Trim();
            if (notes.Length > 0)
            {
                stack.Children.Add(new TextBox
                {
                    Text = notes.Length > 2000 ? notes.Substring(0, 2000) + "\n..." : notes,
                    IsReadOnly = true,
                    TextWrapping = TextWrapping.Wrap,
                    MaxHeight = 200,
                    FontSize = 11.5
                });
            }

            var row = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 8 };
            var download = new Button
            {
                Content = env.SupportsAutomaticInstall ? "Download and Install" : "Download",
                Padding = new Thickness(16, 5),
                Background = new SolidColorBrush(AvaloniaColorHelper.ToAvColor(currentTheme.AccentPrimary)),
                Foreground = Brushes.White
            };
            var notesBtn = new Button { Content = "Release Notes", Padding = new Thickness(12, 5) };
            var skip = new Button { Content = "Skip This Version", Padding = new Thickness(12, 5) };
            var later = new Button { Content = "Later", Padding = new Thickness(12, 5) };
            download.Click += (s, e) => { ModalHost.CloseModal(); StartUpdateDownload(); };
            notesBtn.Click += (s, e) => UpdateChecker.OpenInBrowser(r.ReleasePageUrl);
            skip.Click += (s, e) =>
            {
                settings.SkippedUpdateVersion = r.LatestVersion;
                try { settings.Save(); } catch { }
                ModalHost.CloseModal();
            };
            later.Click += (s, e) => ModalHost.CloseModal();
            row.Children.Add(later);
            row.Children.Add(skip);
            row.Children.Add(notesBtn);
            row.Children.Add(download);
            stack.Children.Add(row);
            ModalHost.ShowModal("update-available", "Update Available", stack, 560);
            MainUi.Post(() => download.Focus(), DispatcherPriority.Loaded);
        }

        private Border updateToast;

        /// <summary>Bottom-right notification (VS Code toast) with action buttons.</summary>
        private void ShowUpdateNotification(string icon, string message, (string Label, Action Run)[] actions)
        {
            var host = CommandPalette?.Parent as Panel;
            if (host == null) { SetStatusMessage(message); return; }
            if (updateToast != null) host.Children.Remove(updateToast);
            Border toast = null;
            void Close() { if (toast != null) host.Children.Remove(toast); if (ReferenceEquals(updateToast, toast)) updateToast = null; }
            var buttons = new List<Button>();
            foreach (var (label, run) in actions)
            {
                var b = new Button { Content = label };
                var act = run;
                b.Click += (s, e) => { Close(); act?.Invoke(); };
                buttons.Add(b);
            }
            toast = WorkbenchStyles.BuildNotification(currentTheme, icon, new TextBlock { Text = message }, null, buttons.ToArray(), true, () => Close());
            toast.Margin = new Thickness(0, 0, 12, UiTokens.StatusBarHeight + 10);
            toast.ZIndex = 185;
            updateToast = toast;
            host.Children.Add(toast);
        }

        /// <summary>Text of the visible update notification ("" when none). Test hook.</summary>
        public string UpdateNotificationText
        {
            get
            {
                if (updateToast == null) return "";
                foreach (var tb in updateToast.GetLogicalDescendants().OfType<TextBlock>()) if (!string.IsNullOrEmpty(tb.Text)) return tb.Text;
                return "";
            }
        }

        private void ShowUpdateReadyNotification()
        {
            var u = Updates;
            if (u.Plan != null && u.Plan.CanInstall)
            {
                ShowUpdateNotification("info", u.ReadyMessage, new[]
                {
                    ("Restart Now", (Action)RestartToUpdate),
                    ("Later", (Action)(() => { })),
                    ("Release Notes", (Action)(() => UpdateChecker.OpenInBrowser(u.Release?.ReleasePageUrl)))
                });
            }
            else
            {
                ShowUpdateNotification("info", u.ReadyMessage + " It has to be installed manually.", new[]
                {
                    ("Show Instructions", (Action)ShowManualInstallModal),
                    ("Later", (Action)(() => { })),
                    ("Release Notes", (Action)(() => UpdateChecker.OpenInBrowser(u.Release?.ReleasePageUrl)))
                });
            }
        }

        private void ShowManualInstallModal()
        {
            var u = Updates;
            var plan = u.Plan;
            if (plan == null) return;
            Color textPrimary = AvaloniaColorHelper.ToAvColor(currentTheme.TextPrimary);
            var stack = new StackPanel { Spacing = 10 };
            stack.Children.Add(new SelectableTextBlock { Text = plan.ManualMessage, TextWrapping = TextWrapping.Wrap, Foreground = new SolidColorBrush(textPrimary) });
            var row = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 8 };
            if (!string.IsNullOrEmpty(plan.ManualCommand))
            {
                var copy = new Button { Content = "Copy Command", Padding = new Thickness(12, 5) };
                copy.Click += async (s, e) => { try { if (Clipboard != null) await Clipboard.SetTextAsync(plan.ManualCommand); SetStatusMessage("Copied: " + plan.ManualCommand); } catch { } };
                row.Children.Add(copy);
            }
            var reveal = new Button { Content = OperatingSystem.IsMacOS() ? "Show in Finder" : "Show Download", Padding = new Thickness(12, 5) };
            reveal.Click += (s, e) => UpdateInstaller.Reveal(plan.RevealPath);
            var close = new Button { Content = "Close", Padding = new Thickness(12, 5) };
            close.Click += (s, e) => ModalHost.CloseModal();
            row.Children.Add(reveal);
            row.Children.Add(close);
            stack.Children.Add(row);
            ModalHost.ShowModal("update-manual", "Install Update", stack, 600);
        }

        /// <summary>Tests: called instead of quitting after the helper was started.</summary>
        public Action RestartForUpdateOverride { get; set; }

        /// <summary>Restart to Update: unsaved files go through the normal prompt, then the helper starts and codeLess quits.</summary>
        public void RestartToUpdate()
        {
            var u = Updates;
            if (u.State != UpdateState.Ready || u.Plan == null || !u.Plan.CanInstall) { CheckForUpdatesInteractive(); return; }
            // other windows' unsaved files are never discarded silently
            if (Application.Current?.ApplicationLifetime is Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime dt)
            {
                foreach (var w in dt.Windows)
                {
                    if (w is MainWindow other && !ReferenceEquals(other, this) && !other.SuppressUnsavedPrompts && other.openTabs.Any(UntitledFileDefaults.NeedsSavePrompt))
                    {
                        ShowMessageModal("Restart to Update", "Another codeLess window has unsaved changes. Save or close them there, then choose Restart to Update again.");
                        other.Activate();
                        return;
                    }
                }
            }
            if (!ConfirmSaveOrDiscardDirtyTabs(openTabs, ProceedRestartToUpdate)) return; // modal shown; continues after Save / Don't Save
            ProceedRestartToUpdate();
        }

        private void ProceedRestartToUpdate()
        {
            var u = Updates;
            if (RestartForUpdateOverride != null) { RestartForUpdateOverride(); return; }
            if (!u.LaunchInstaller(out string error))
            {
                ShowMessageModal("Restart to Update", "The update could not be started: " + error);
                return;
            }
            SuppressUnsavedPrompts = true;
            closeConfirmed = true;
            if (Application.Current?.ApplicationLifetime is Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime dt)
            {
                foreach (var w in new List<Window>(dt.Windows))
                {
                    if (w is MainWindow m) { m.SuppressUnsavedPrompts = true; m.closeConfirmed = true; }
                    try { w.Close(); } catch { }
                }
                dt.Shutdown(0);
            }
            else Close();
        }
    }
}
