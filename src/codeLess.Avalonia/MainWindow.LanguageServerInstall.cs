using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using codeLess.Studio;

namespace codeLess.AvaloniaApp
{
    /// <summary>
    /// Automatic language server installs (VS Code-like: servers "just work"): the shared installer puts missing servers
    /// into the codeLess-managed folder; this part wires progress into the status bar item and the Output panel, the
    /// failure notification (Show Log / Copy Command / Don't Ask Again), the install log, and the
    /// "Language Servers: Install/Update…" command. Logic lives in codeLess.Core (LanguageServerInstaller).
    /// </summary>
    public partial class MainWindow
    {
        /// <summary>Tests inject an installer with fake tools; null = the shared installer in the desktop app, none in headless runs.</summary>
        public static Func<LanguageServerInstaller> LanguageServerInstallerOverride;

        private void InitLanguageServerInstalls()
        {
            if (lsp == null) return;
            var installer = LanguageServerInstallerOverride?.Invoke() ?? (AvaloniaTerminalEmulatorControl.IsDesktopRuntime ? LanguageServerInstaller.Shared : null);
            lsp.Installer = installer;
            // Automation (screenshot suite / benchmarks / smoke runs) never installs anything on the machine.
            lsp.AutoInstallEnabled = () => (settings == null || settings.AutoInstallLanguageServers) && !App.IsAutomationRun;
            lsp.InstallOutput += line => AppendOutputLog("[Language Servers] " + line);
            lsp.InstallFinished += r =>
            {
                if (r.Success) SetStatusMessage(r.Message);
                else if (r.Cancelled) SetStatusMessage(r.Message);
            };
        }

        /// <summary>Text of the Output panel (test hook).</summary>
        public string OutputPanelText => outputStreamBox?.Text ?? "";

        /// <summary>Status-bar menu entries for installs (inserted at the top of the language server menu).</summary>
        private List<object> LanguageServerInstallMenuItems(LanguageServerStatus st)
        {
            var items = new List<object>();
            if (lsp == null || st == null || activeTab == null) return items;
            var def = LanguageServerRegistry.DefinitionForLanguage(st.LanguageId ?? "");
            if (def == null) return items;
            if (st.State == LanguageServerState.Installing)
            {
                items.Add(MakeCtxItem("Cancel Installation", () => lsp?.CancelInstall(def.Id)));
                items.Add(MakeCtxItem("Show Installation Log", ShowLanguageServerInstallLog));
                items.Add(new Separator());
            }
            else if (st.State == LanguageServerState.NotInstalled && st.CanInstall)
            {
                items.Add(MakeCtxItem("Install " + def.DisplayName + " Language Server", () => InstallLanguageServer(def.Id)));
            }
            return items;
        }

        /// <summary>Installs (or updates) one server family now (ignores the failure backoff).</summary>
        public void InstallLanguageServer(string serverId)
        {
            if (lsp == null || lsp.Installer == null) { SetStatusMessage("Language server installs are not available in this window."); return; }
            var job = lsp.InstallServer(serverId);
            if (job != null) SetStatusMessage(job.StatusText);
        }

        /// <summary>"Language Servers: Install/Update…": pick a server family to install or update.</summary>
        public void ShowLanguageServerInstallPicker()
        {
            if (lsp == null || lsp.Installer == null) { SetStatusMessage("Language server installs are not available in this window."); return; }
            string root = workspaceRoot;
            string managed = lsp.ManagedServersRoot;
            var user = settings.GetLanguageServerUserSettings();
            var ui = UiPost.CaptureCurrent();
            SetStatusMessage("Detecting language servers…");
            _ = Task.Run(() =>
            {
                var rows = new List<(LanguageServerDefinition def, string state)>();
                foreach (var d in LanguageServerRegistry.Definitions)
                {
                    if (LanguageServerInstaller.RecipesFor(d.Id).Count == 0) continue;
                    string state;
                    try
                    {
                        var r = LanguageServerRegistry.Resolve(d.LanguageIds[0], string.IsNullOrEmpty(root) ? null : root, user, managed);
                        state = r.FromManagedFolder ? "installed by codeLess — update" : r.Executable != null ? "found " + r.DisplayName + " (" + r.Executable + ") — install a codeLess copy" : "not installed";
                    }
                    catch (Exception ex) { state = ex.Message; }
                    rows.Add((d, state));
                }
                ui.Post(() =>
                {
                    var entries = rows.Select(row => new PaletteEntryItem
                    {
                        Category = "Language Server",
                        Title = row.def.DisplayName + "  —  " + row.state,
                        Execute = () => InstallLanguageServer(row.def.Id)
                    }).ToList();
                    CommandPalette.Open("", entries, currentTheme?.Id);
                    CommandPalette.ModeHintLabel.Text = "Install or update a language server — Enter to install";
                    SetStatusMessage("");
                });
            });
        }

        public void ShowLanguageServerInstallLog()
        {
            var installer = lsp?.Installer;
            string text = installer?.GetLog() ?? "";
            if (string.IsNullOrEmpty(text)) text = "No language server was installed in this session.";
            var box = new TextBox
            {
                Text = text,
                IsReadOnly = true,
                AcceptsReturn = true,
                TextWrapping = TextWrapping.NoWrap,
                FontFamily = new FontFamily("Menlo, Consolas, monospace"),
                FontSize = 11.5,
                Height = 380
            };
            box.CaretIndex = box.Text?.Length ?? 0;
            var copy = new Button { Content = "Copy", Padding = new Thickness(12, 4) };
            copy.Click += async (s, e) => { var cb = Clipboard; if (cb != null) await cb.SetTextAsync(box.Text ?? ""); };
            var close = new Button { Content = "Close", Padding = new Thickness(12, 4) };
            close.Click += (s, e) => ModalHost.CloseModal();
            var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right, Children = { copy, close } };
            var view = new StackPanel
            {
                Spacing = 8,
                Children =
                {
                    new TextBlock { Text = "Servers are installed into " + (lsp?.ManagedServersRoot ?? LanguageServerInstaller.DefaultManagedRoot()), FontSize = 11.5, TextWrapping = TextWrapping.Wrap, Foreground = AvaloniaColorHelper.ToBrush(currentTheme.TextSecondary) },
                    box,
                    buttons
                }
            };
            ModalHost.ShowModal("lsp-install-log", "Language Server Installs", view, 860);
        }

        /// <summary>Failure notification of an automatic install: Show Log / Copy Command / Don't Ask Again for &lt;lang&gt;.</summary>
        private void ShowLanguageServerInstallFailure(LanguageServerHint hint)
        {
            var host = CommandPalette?.Parent as Panel;
            AppendOutputLog("[Language Servers] " + hint.Message);
            if (host == null) { SetStatusMessage(hint.Message); return; }
            if (lspToast != null) host.Children.Remove(lspToast);
            var text = new TextBlock { Text = hint.Message };
            Border toast = null;
            void Close() { if (toast != null) host.Children.Remove(toast); if (ReferenceEquals(lspToast, toast)) { lspToast = null; lspToastText = null; } }
            var buttons = new List<Button>();
            var log = new Button { Content = "Show Log" };
            log.Click += (s, e) => { Close(); ShowLanguageServerInstallLog(); };
            buttons.Add(log);
            if (!string.IsNullOrEmpty(hint.InstallCommand))
            {
                var copy = new Button { Content = "Copy Command" };
                ToolTip.SetTip(copy, hint.InstallCommand);
                copy.Click += async (s, e) =>
                {
                    var cb = Clipboard;
                    if (cb != null) await cb.SetTextAsync(hint.InstallCommand);
                    SetStatusMessage("Install command copied to the clipboard.");
                    Close();
                };
                buttons.Add(copy);
            }
            var never = new Button { Content = "Don't Ask Again for " + hint.LanguageName };
            never.Click += (s, e) =>
            {
                settings.DismissLanguageServerHint(hint.LanguageId);
                try { settings.Save(); } catch { }
                Close();
            };
            buttons.Add(never);
            toast = WorkbenchStyles.BuildNotification(currentTheme, "warning", text, null, buttons.ToArray(), true, () => Close(), 460);
            toast.Margin = new Thickness(0, 0, 12, UiTokens.StatusBarHeight + 10);
            toast.ZIndex = 180;
            lspToastText = text;
            lspToast = toast;
            host.Children.Add(toast);
            DispatcherTimer.RunOnce(Close, TimeSpan.FromSeconds(30));
        }
    }
}
