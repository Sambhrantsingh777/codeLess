using System;
using System.IO;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
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

        public override void Initialize()
        {
            RequestedThemeVariant = ThemeVariant.Dark;
            Styles.Add(new FluentTheme());
        }

        public override void OnFrameworkInitializationCompleted()
        {
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
                        }
                    };
                }
                else if (!string.IsNullOrWhiteSpace(ScreenshotOutputPath))
                {
                    mainWindow.Opened += async (s, e) =>
                    {
                        try
                        {
                            await Task.Delay(350);
                            Dispatcher.UIThread.RunJobs();
                            string dir = Path.GetDirectoryName(Path.GetFullPath(ScreenshotOutputPath));
                            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                            int w = Math.Max(1280, (int)mainWindow.Bounds.Width);
                            int h = Math.Max(820, (int)mainWindow.Bounds.Height);
                            using var rtb = new RenderTargetBitmap(new PixelSize(w, h), new Vector(96, 96));
                            rtb.Render(mainWindow);
                            rtb.Save(ScreenshotOutputPath);
                        }
                        finally
                        {
                            desktop.Shutdown(0);
                        }
                    };
                }
            }

            base.OnFrameworkInitializationCompleted();
        }
    }
}
