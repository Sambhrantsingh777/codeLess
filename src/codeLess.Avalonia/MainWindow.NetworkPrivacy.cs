using System;
using System.Globalization;
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
    /// Network privacy wiring for the macOS/Linux workbench: Offline Mode (persisted in settings), the status bar shield
    /// (request count / "Offline"), and the Network Activity window.
    /// </summary>
    public partial class MainWindow
    {
        private Button statusNetworkBtn;
        private NetworkShieldIcon statusNetworkShield;
        private TextBlock statusNetworkText;
        private NetworkActivityWindow networkActivityWindow;
        private bool networkPrivacyInitialized;
        private bool networkStatusRefreshQueued;

        public Button StatusNetworkButton => statusNetworkBtn;
        public NetworkActivityWindow NetworkActivityWindowInstance => networkActivityWindow;

        /// <summary>Applies the persisted Offline Mode and subscribes to ledger events (call once after settings load).</summary>
        private UiPost networkUiPost;

        private void InitNetworkPrivacy()
        {
            if (networkPrivacyInitialized) return;
            networkPrivacyInitialized = true;
            networkUiPost = UiPost.CaptureCurrent();
            NetworkLedger.SetOfflineMode(settings != null && settings.OfflineMode);
            // Subscribe only while the window is actually open: Close() on a never-shown window raises no Closed
            // event, so subscribing in the constructor leaked handlers (and stale windows) on the static ledger.
            Opened += (s, e) =>
            {
                NetworkLedger.OfflineModeChanged -= OnNetworkOfflineModeChanged;
                NetworkLedger.EntryAdded -= OnNetworkEntryAdded;
                NetworkLedger.Cleared -= OnNetworkLedgerCleared;
                NetworkLedger.OfflineModeChanged += OnNetworkOfflineModeChanged;
                NetworkLedger.EntryAdded += OnNetworkEntryAdded;
                NetworkLedger.Cleared += OnNetworkLedgerCleared;
                RefreshNetworkStatusItem();
            };
            Closed += (s, e) =>
            {
                NetworkLedger.OfflineModeChanged -= OnNetworkOfflineModeChanged;
                NetworkLedger.EntryAdded -= OnNetworkEntryAdded;
                NetworkLedger.Cleared -= OnNetworkLedgerCleared;
                try { networkActivityWindow?.Close(); } catch { }
            };
        }

        /// <summary>Status bar shield: click opens Network Activity, right-click offers the Offline Mode toggle.</summary>
        private Button BuildNetworkStatusButton()
        {
            statusNetworkShield = new NetworkShieldIcon { VerticalAlignment = VerticalAlignment.Center };
            statusNetworkText = new TextBlock { FontSize = 11, VerticalAlignment = VerticalAlignment.Center };
            var content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 5 };
            content.Children.Add(statusNetworkShield);
            content.Children.Add(statusNetworkText);
            statusNetworkBtn = new Button
            {
                Content = content,
                FontSize = 11,
                Padding = new Thickness(7, 1),
                Height = 24,
                CornerRadius = new CornerRadius(0),
                Background = Brushes.Transparent,
                Foreground = Brushes.White,
                BorderThickness = new Thickness(0)
            };
            statusNetworkBtn.Click += (s, e) => ShowNetworkActivity();

            var menu = new ContextMenu();
            var showItem = new MenuItem { Header = "Show Network Activity" };
            showItem.Click += (s, e) => ShowNetworkActivity();
            var offlineItem = new MenuItem { Header = "Offline Mode", ToggleType = MenuItemToggleType.CheckBox };
            offlineItem.Click += (s, e) => ToggleOfflineMode();
            menu.Items.Add(showItem);
            menu.Items.Add(offlineItem);
            menu.Opening += (s, e) => offlineItem.IsChecked = NetworkLedger.OfflineMode;
            statusNetworkBtn.ContextMenu = menu;

            RefreshNetworkStatusItem();
            return statusNetworkBtn;
        }

        public void ToggleOfflineMode()
        {
            NetworkLedger.SetOfflineMode(!NetworkLedger.OfflineMode);
        }

        /// <summary>View > Network Activity: opens (or focuses) the non-modal Network Activity window.</summary>
        public void ShowNetworkActivity()
        {
            if (networkActivityWindow == null)
            {
                networkActivityWindow = new NetworkActivityWindow(currentTheme);
                networkActivityWindow.Closed += (s, e) => networkActivityWindow = null;
                try
                {
                    if (IsVisible) networkActivityWindow.Show(this);
                    else networkActivityWindow.Show();
                }
                catch
                {
                    networkActivityWindow.Show();
                }
            }
            else
            {
                if (networkActivityWindow.WindowState == WindowState.Minimized)
                    networkActivityWindow.WindowState = WindowState.Normal;
                networkActivityWindow.Activate();
            }
        }

        private void ApplyNetworkPrivacyTheme(StudioTheme theme)
        {
            if (theme == null) return;
            if (statusNetworkBtn != null) RefreshNetworkStatusItem();
            networkActivityWindow?.ApplyTheme(theme);
        }

        private void OnNetworkOfflineModeChanged(bool offline)
        {
            networkUiPost.Post(() =>
            {
                if (!IsVisible) return; // closed / hidden windows must not re-layout
                if (settings != null) settings.OfflineMode = offline;
                // Persist only this one field. Every open window receives this event; saving each window's whole
                // (possibly stale) settings object would clobber newer settings written by other windows.
                try
                {
                    var persisted = IdePersistentSettings.Load();
                    if (persisted != null && persisted.OfflineMode != offline)
                    {
                        persisted.OfflineMode = offline;
                        persisted.Save();
                    }
                }
                catch { }
                RefreshNetworkStatusItem();
                SetStatusMessage(offline
                    ? "Offline Mode on: only local requests (Ollama / LM Studio on localhost) are allowed."
                    : "Offline Mode off.");
            });
        }

        private void OnNetworkEntryAdded(NetworkLedgerEntry e) => QueueNetworkStatusRefresh();
        private void OnNetworkLedgerCleared() => QueueNetworkStatusRefresh();

        private void QueueNetworkStatusRefresh()
        {
            if (networkStatusRefreshQueued) return;
            networkStatusRefreshQueued = true;
            networkUiPost.Post(() =>
            {
                networkStatusRefreshQueued = false;
                if (!IsVisible) return;
                RefreshNetworkStatusItem();
            }, DispatcherPriority.Background);
        }

        public void RefreshNetworkStatusItem()
        {
            if (statusNetworkBtn == null || currentTheme == null) return;
            bool offline = NetworkLedger.OfflineMode;
            int sent = NetworkLedger.SessionRequestCount - NetworkLedger.SessionBlockedCount;
            statusNetworkText.Text = offline ? "Offline" : sent.ToString(CultureInfo.InvariantCulture);
            var fg = AvaloniaColorHelper.ToBrush(currentTheme.StatusBarText);
            statusNetworkBtn.Foreground = fg;
            statusNetworkText.Foreground = fg;
            statusNetworkShield.Stroke = fg;
            statusNetworkShield.Filled = offline;
            statusNetworkBtn.Background = offline ? new SolidColorBrush(Color.FromArgb(70, 245, 158, 11)) : Brushes.Transparent;
            ToolTip.SetTip(statusNetworkBtn, NetworkLedger.GetSummary() + "\nClick: Network Activity   Right-click: Offline Mode");
        }
    }
}
