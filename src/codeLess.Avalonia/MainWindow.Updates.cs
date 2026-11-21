using System;
using System.Globalization;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using codeLess.Studio;

namespace codeLess.AvaloniaApp
{
    /// <summary>Help > Check for Updates and the once-a-day automatic update check.</summary>
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

        private bool updateCheckInFlight;

        /// <summary>Automatic check on launch: at most once per 24 h, silent unless a newer, non-skipped release exists.</summary>
        public void MaybeAutoCheckForUpdates()
        {
            if (settings == null || !settings.AutoCheckForUpdates || !AvaloniaTerminalEmulatorControl.IsDesktopRuntime) return;
            if (NetworkLedger.OfflineMode) return; // Offline Mode: skip the automatic check silently
            DateTime last;
            if (DateTime.TryParse(settings.LastUpdateCheckUtc, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out last) &&
                (DateTime.UtcNow - last).TotalHours < 24)
                return;
            RunUpdateCheck(interactive: false);
        }

        /// <summary>Help > Check for Updates...</summary>
        public void CheckForUpdatesInteractive() => RunUpdateCheck(interactive: true);

        private void RunUpdateCheck(bool interactive)
        {
            if (updateCheckInFlight) return;
            updateCheckInFlight = true;
            if (interactive) SetStatusMessage("Checking for updates...");
            string current = CurrentAppVersion;
            Task.Run(() => UpdateChecker.Check(current, 8000)).ContinueWith(t =>
            {
                Dispatcher.UIThread.Post(() =>
                {
                    updateCheckInFlight = false;
                    var r = t.Status == TaskStatus.RanToCompletion ? t.Result : new UpdateCheckResult { Error = "Update check failed." };
                    if (r.Success)
                    {
                        settings.LastUpdateCheckUtc = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture);
                        try { settings.Save(); } catch { }
                    }
                    if (!r.Success)
                    {
                        if (interactive) ShowMessageModal("Check for Updates", "Could not check for updates.\n\n" + r.Error);
                        return;
                    }
                    if (!r.IsNewer)
                    {
                        if (interactive) ShowMessageModal("Check for Updates", $"codeLess {current} is the latest version.");
                        else SetStatusMessage("");
                        return;
                    }
                    if (!interactive && string.Equals(settings.SkippedUpdateVersion, r.LatestVersion, StringComparison.OrdinalIgnoreCase))
                        return;
                    ShowUpdateAvailableModal(r);
                });
            });
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
            var auto = new CheckBox
            {
                Content = "Check for updates automatically",
                IsChecked = settings.AutoCheckForUpdates,
                Foreground = new SolidColorBrush(textPrimary)
            };
            auto.IsCheckedChanged += (s, e) =>
            {
                settings.AutoCheckForUpdates = auto.IsChecked == true;
                try { settings.Save(); } catch { }
            };
            stack.Children.Add(auto);

            var row = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 8 };
            var download = new Button
            {
                Content = "Download",
                Padding = new Thickness(16, 5),
                Background = new SolidColorBrush(AvaloniaColorHelper.ToAvColor(currentTheme.AccentPrimary)),
                Foreground = Brushes.White
            };
            var notesBtn = new Button { Content = "Release Notes", Padding = new Thickness(12, 5) };
            var skip = new Button { Content = "Skip This Version", Padding = new Thickness(12, 5) };
            var later = new Button { Content = "Later", Padding = new Thickness(12, 5) };
            download.Click += (s, e) =>
            {
                ModalHost.CloseModal();
                UpdateChecker.OpenInBrowser(r.DownloadUrl);
                SetStatusMessage($"Downloading codeLess {r.LatestVersion} in your browser. Quit codeLess before installing.");
            };
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
            Dispatcher.UIThread.Post(() => download.Focus(), DispatcherPriority.Loaded);
        }
    }
}
