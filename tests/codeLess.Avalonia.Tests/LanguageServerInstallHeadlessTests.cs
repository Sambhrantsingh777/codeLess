using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using codeLess.AvaloniaApp;
using codeLess.Studio;
using Xunit;

namespace codeLess.Avalonia.Tests
{
    /// <summary>Automatic language server install in the Avalonia workbench: status item, Output panel, failure toast, settings.</summary>
    [Collection("SequentialWorkbench")]
    public class LanguageServerInstallHeadlessTests : IDisposable
    {
        private readonly string settingsFilePath;
        private readonly string settingsBackup;
        private readonly bool hadSettingsFile;
        private readonly string dir;
        private MainWindow window;

        public LanguageServerInstallHeadlessTests()
        {
            settingsFilePath = IdePersistentSettings.GetSettingsFilePath();
            hadSettingsFile = !string.IsNullOrEmpty(settingsFilePath) && File.Exists(settingsFilePath);
            settingsBackup = hadSettingsFile ? File.ReadAllText(settingsFilePath) : null;
            dir = Path.Combine(Path.GetTempPath(), "codeLess LspInstallUi " + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(Path.Combine(dir, "ws"));
        }

        public void Dispose()
        {
            try
            {
                if (window != null)
                {
                    window.SuppressUnsavedPrompts = true;
                    window.ModalHost.CloseModal();
                    window.Close();
                }
            }
            catch { }
            MainWindow.LanguageServersEnabledOverride = null;
            MainWindow.LanguageServerSettingsOverride = null;
            MainWindow.LanguageServerInstallerOverride = null;
            try
            {
                if (hadSettingsFile && settingsBackup != null) File.WriteAllText(settingsFilePath, settingsBackup);
                else if (!hadSettingsFile && !string.IsNullOrEmpty(settingsFilePath) && File.Exists(settingsFilePath)) File.Delete(settingsFilePath);
            }
            catch { }
            try { Directory.Delete(dir, true); } catch { }
        }

        private static void Pump(Func<bool> cond, int ms = 20000, string what = "condition")
        {
            ms = PerfBudget.Wait(ms);
            var sw = Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < ms)
            {
                Dispatcher.UIThread.RunJobs();
                if (cond()) return;
                Thread.Sleep(15);
            }
            Dispatcher.UIThread.RunJobs();
            Assert.True(cond(), "Timed out waiting for " + what);
        }

        private string Managed => Path.Combine(dir, "servers");

        /// <summary>Window whose installer uses the fake npm process; false when Python is missing or a real server exists.</summary>
        private bool Setup(string npmMode, bool autoInstall = true)
        {
            string npm = LanguageServerInstallKit.CreateFakeNpm(Path.Combine(dir, "tools"));
            if (npm == null || LanguageServerRegistry.FindExecutable("bash-language-server", null) != null) return false;
            Directory.CreateDirectory(Managed);
            if (npmMode != null) File.WriteAllText(Path.Combine(Managed, "fake_npm_mode.txt"), npmMode);
            var probe = new LanguageServerInstallKit.FakeProbe();
            probe.Tools["npm"] = npm;
            MainWindow.LanguageServersEnabledOverride = true;
            MainWindow.LanguageServerSettingsOverride = () => LanguageServerUserSettings.Parse("", false);
            MainWindow.LanguageServerInstallerOverride = () =>
            {
                var i = new LanguageServerInstaller(Managed, probe, null, new LanguageServerInstallKit.FakeDownloader());
                i.IsOffline = () => false;
                return i;
            };
            window = new MainWindow(Path.Combine(dir, "ws")) { Width = 1300, Height = 860 };
            window.CurrentSettings.AutoInstallLanguageServers = autoInstall;
            window.Show();
            Dispatcher.UIThread.RunJobs();
            return true;
        }

        private string NewScript()
        {
            string f = Path.Combine(dir, "ws", "deploy.sh");
            File.WriteAllText(f, "#!/bin/sh\necho BAD\n");
            return f;
        }

        [AvaloniaFact]
        public void Missing_Server_Is_Installed_With_Status_Progress_And_Output_Then_Starts()
        {
            if (!Setup("slow")) return;
            string f = NewScript();
            window.OpenFileInTab(f, preview: false);
            Pump(() => window.LanguageServerStatusText == "⟳ Installing Bash language server…", what: "installing status");
            Pump(() => window.LanguageServerStatusText == "{ } Bash", what: "server started after install");
            Assert.Equal("", window.LanguageServerHintText); // no notification on success
            Assert.Contains("[Language Servers] $ npm install --prefix", window.OutputPanelText);
            Assert.Contains("[Language Servers]   added 1 packages", window.OutputPanelText);
            Pump(() => LspDiagnosticsStore.Count(f) > 0, what: "diagnostics for the open file");
            Assert.True(File.Exists(Path.Combine(Managed, "bash", "node_modules", ".bin", OperatingSystem.IsWindows() ? "bash-language-server.cmd" : "bash-language-server")));
        }

        [AvaloniaFact]
        public void Failed_Install_Shows_One_Notification_And_Falls_Back_To_Not_Installed()
        {
            if (!Setup("fail")) return;
            string f = NewScript();
            window.OpenFileInTab(f, preview: false);
            Pump(() => window.LanguageServerHintText.Contains("Couldn't install the Bash language server"), what: "failure notification");
            Assert.Contains("exited with code 1", window.LanguageServerHintText);
            Pump(() => window.LanguageServerStatusText.StartsWith("{ } No Bash server"), what: "not-installed status");
            Assert.Contains("npm ERR! code E404", window.OutputPanelText + window.LanguageServers.Installer.GetLog());
            // the toast offers Show Log / Copy Command / Don't Ask Again for Bash
            var labels = window.GetVisualDescendants().OfType<Button>().Select(b => b.Content as string).Where(s => s != null).ToList();
            Assert.Contains("Show Log", labels);
            Assert.Contains("Copy Command", labels);
            Assert.Contains("Don't Ask Again for Bash", labels);
            window.DismissLanguageServerHintToast();
        }

        [AvaloniaFact]
        public void Setting_Off_Keeps_The_Install_Hint_And_Runs_Nothing()
        {
            if (!Setup(null, autoInstall: false)) return;
            string f = NewScript();
            window.OpenFileInTab(f, preview: false);
            Pump(() => window.LanguageServerHintText.Contains("No Bash language server found"), what: "install hint");
            Assert.Contains("install one with:", window.LanguageServerHintText);
            Thread.Sleep(200);
            Assert.False(File.Exists(Path.Combine(Managed, "fake_npm_calls.txt")));
        }

        [AvaloniaFact]
        public void Both_Settings_Dialogs_Toggle_Automatic_Installs()
        {
            if (!Setup(null)) return;
            Assert.True(window.CurrentSettings.AutoInstallLanguageServers);
            // Preferences dialog
            window.ShowSettingsModal();
            Dispatcher.UIThread.RunJobs();
            var body = (Control)window.ModalHost.ActiveDialogBody;
            var chk = body.GetVisualDescendants().OfType<CheckBox>().First(c => (ToolTip.GetTip(c) as string) == "codeless.languageServers.autoInstall");
            Assert.True(chk.IsChecked);
            chk.IsChecked = false;
            body.GetVisualDescendants().OfType<Button>().First(b => b.Name == SettingsDialogView.SaveName).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Dispatcher.UIThread.RunJobs();
            Assert.False(window.CurrentSettings.AutoInstallLanguageServers);

            // Language Servers dialog
            window.ShowLanguageServerSettingsModal();
            Dispatcher.UIThread.RunJobs();
            body = (Control)window.ModalHost.ActiveDialogBody;
            var lspChk = body.GetVisualDescendants().OfType<CheckBox>().First(c => c.Name == "LspAutoInstallCheck");
            Assert.False(lspChk.IsChecked);
            lspChk.IsChecked = true;
            body.GetVisualDescendants().OfType<Button>().First(b => (b.Content as string) == "Save").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Dispatcher.UIThread.RunJobs();
            Assert.True(window.CurrentSettings.AutoInstallLanguageServers);
        }
    }
}
