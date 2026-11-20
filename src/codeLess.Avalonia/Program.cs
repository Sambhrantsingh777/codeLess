global using codeLess.Studio;
using System;
using System.IO;
using Avalonia;

namespace codeLess.AvaloniaApp
{
    public static class Program
    {
        [STAThread]
        public static void Main(string[] args)
        {
            if (args != null)
            {
                for (int i = 0; i < args.Length; i++)
                {
                    string a = args[i] ?? "";
                    if ((a == "--screenshot-suite" || a == "-ss") && i + 1 < args.Length)
                    {
                        App.ScreenshotSuiteDir = args[++i];
                    }
                    else if ((a == "--screenshot" || a == "-s") && i + 1 < args.Length)
                    {
                        App.ScreenshotOutputPath = args[++i];
                    }
                    else if ((a == "--workspace" || a == "-w") && i + 1 < args.Length)
                    {
                        App.StartupWorkspacePath = args[++i];
                    }
                    else if (!a.StartsWith("-") && (Directory.Exists(a) || File.Exists(a)) && string.IsNullOrEmpty(App.StartupWorkspacePath))
                    {
                        App.StartupWorkspacePath = a;
                    }
                }
            }

            BuildAvaloniaApp().StartWithClassicDesktopLifetime(args ?? Array.Empty<string>());
        }

        public static AppBuilder BuildAvaloniaApp()
        {
            return AppBuilder.Configure<App>()
                .UsePlatformDetect()
                .WithInterFont()
                .LogToTrace();
        }
    }
}
