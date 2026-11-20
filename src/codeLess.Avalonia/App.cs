using System;
using System.IO;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
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
                if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime dt)
                    dt.Shutdown(0);
            };
            appMenu.Add(quitItem);
            NativeMenu.SetMenu(this, appMenu);
        }

        public override void OnFrameworkInitializationCompleted()
        {
            Name = "codeLess";
            if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            {
                var mainWindow = new MainWindow(StartupWorkspacePath);
                desktop.MainWindow = mainWindow;

                if (!string.IsNullOrWhiteSpace(ScreenshotSuiteDir))
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
