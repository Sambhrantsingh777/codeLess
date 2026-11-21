using System;
using System.IO;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Platform.Storage;
using Avalonia.Input;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using Avalonia.Themes.Fluent;
using Avalonia.Threading;

namespace codeLess.AvaloniaApp
{
    public class App : Application
    {
        public static string StartupWorkspacePath { get; set; }
        public static string ScreenshotOutputPath { get; set; }
        public static string ScreenshotSuiteDir { get; set; }
        public static string SmokeQuitReportPath { get; set; }

        public App()
        {
            Name = "codeLess";
        }

        public override void Initialize()
        {
            Name = "codeLess";
            RequestedThemeVariant = ThemeVariant.Dark;
            Styles.Add(new FluentTheme());

            // Seed default macOS application menu so the top menu bar always displays "codeLess" instead of "Avalonia Application"
            var appMenu = new NativeMenu();
            var quitItem = new NativeMenuItem("Quit codeLess")
            {
                Gesture = new KeyGesture(Key.Q, KeyModifiers.Meta)
            };
            quitItem.Click += (s, e) =>
            {
                // Close via the window so the unsaved-changes prompt in MainWindow.OnClosing can run.
                if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime dt)
                {
                    if (dt.MainWindow != null) dt.MainWindow.Close();
                    else dt.TryShutdown(0);
                }
            };
            appMenu.Add(quitItem);
            NativeMenu.SetMenu(this, appMenu);
        }

        public override void OnFrameworkInitializationCompleted()
        {
            Name = "codeLess";
            // A bug in one handler (e.g. an editor command) must not take the whole IDE and unsaved work down:
            // log UI-thread exceptions to ~/.codeless/crash.log and keep running.
            Dispatcher.UIThread.UnhandledException += (s, e) =>
            {
                try
                {
                    string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codeless");
                    Directory.CreateDirectory(dir);
                    File.AppendAllText(Path.Combine(dir, "crash.log"),
                        $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] [UIThread] {e.Exception}{Environment.NewLine}");
                }
                catch { }
                e.Handled = true;
            };
            if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            {
                var mainWindow = new MainWindow(StartupWorkspacePath);
                desktop.MainWindow = mainWindow;

                // macOS delivers "Open With", dock-icon drops and `open -a codeLess <path>` as Apple Events,
                // not argv; they were silently ignored.
                var activatable = TryGetFeature(typeof(IActivatableLifetime)) as IActivatableLifetime;
                if (activatable != null)
                {
                    activatable.Activated += (s, e) =>
                    {
                        if (e is FileActivatedEventArgs fe && fe.Files != null)
                        {
                            foreach (var item in fe.Files)
                            {
                                string path = item?.TryGetLocalPath();
                                if (!string.IsNullOrEmpty(path))
                                    Dispatcher.UIThread.Post(() => mainWindow.OpenPathFromOs(path));
                            }
                        }
                    };
                }

                bool automatedRun = !string.IsNullOrWhiteSpace(SmokeQuitReportPath) || !string.IsNullOrWhiteSpace(ScreenshotSuiteDir) || !string.IsNullOrWhiteSpace(ScreenshotOutputPath);
                if (!automatedRun)
                    mainWindow.Opened += (s, e) => Dispatcher.UIThread.Post(mainWindow.MaybeAutoCheckForUpdates, DispatcherPriority.Background);

                if (!string.IsNullOrWhiteSpace(SmokeQuitReportPath))
                {
                    mainWindow.Opened += async (s, e) => await mainWindow.RunSmokeQuitAsync(SmokeQuitReportPath);
                }
                else if (!string.IsNullOrWhiteSpace(ScreenshotSuiteDir))
                {
                    mainWindow.Opened += async (s, e) =>
                    {
                        try
                        {
                            await mainWindow.RunAutomatedScreenshotSuiteAsync(ScreenshotSuiteDir);
                        }
                        finally
                        {
                            desktop.Shutdown(0);
                            Environment.Exit(0);
                        }
                    };
                }
                else if (!string.IsNullOrWhiteSpace(ScreenshotOutputPath))
                {
                    string targetOut = Path.GetFullPath(ScreenshotOutputPath);
                    int completed = 0;

                    void CaptureAndExit()
                    {
                        if (System.Threading.Interlocked.Exchange(ref completed, 1) != 0)
                            return;
                        try
                        {
                            Dispatcher.UIThread.RunJobs();
                            string dir = Path.GetDirectoryName(targetOut);
                            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                            int w = Math.Max(1280, (int)mainWindow.Bounds.Width);
                            int h = Math.Max(820, (int)mainWindow.Bounds.Height);
                            mainWindow.Measure(new Size(w, h));
                            mainWindow.Arrange(new Rect(0, 0, w, h));
                            using var rtb = new RenderTargetBitmap(new PixelSize(w, h), new Vector(96, 96));
                            rtb.Render(mainWindow);
                            rtb.Save(targetOut);
                        }
                        finally
                        {
                            try { mainWindow.Close(); } catch { }
                            Environment.Exit(0);
                        }
                    }

                    mainWindow.Opened += async (s, e) =>
                    {
                        await Task.Delay(350);
                        CaptureAndExit();
                    };

                    // OS thread watchdog for displayless SSH CI VMs where Aqua NSRunLoop does not emit Opened
                    var watchdog = new System.Threading.Thread(() =>
                    {
                        System.Threading.Thread.Sleep(2200);
                        Dispatcher.UIThread.Post(CaptureAndExit);
                        System.Threading.Thread.Sleep(2000);
                        try { mainWindow.Close(); } catch { }
                        Environment.Exit(0);
                    })
                    {
                        IsBackground = true,
                        Name = "ScreenshotWatchdog"
                    };
                    watchdog.Start();
                }
            }

            base.OnFrameworkInitializationCompleted();
        }
    }
}
