global using codeLess.Studio;
using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using Avalonia;

namespace codeLess.AvaloniaApp
{
    public static class Program
    {
        [STAThread]
        public static void Main(string[] args)
        {
            EnsureDeveloperPathOnUnix();
            RegisterCrashGuards();

            if (args != null)
            {
                for (int i = 0; i < args.Length; i++)
                {
                    string a = args[i] ?? "";
                    if (a.StartsWith("-psn_", StringComparison.Ordinal))
                        continue;
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

        private static void EnsureDeveloperPathOnUnix()
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                return;

            try
            {
                string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) ?? "";
                string currentPath = Environment.GetEnvironmentVariable("PATH") ?? "/usr/bin:/bin:/usr/sbin:/sbin";
                var existing = new HashSet<string>(currentPath.Split(':', StringSplitOptions.RemoveEmptyEntries), StringComparer.Ordinal);
                var additions = new List<string>
                {
                    "/opt/homebrew/bin",
                    "/opt/homebrew/sbin",
                    "/usr/local/bin",
                    "/usr/local/share/dotnet"
                };
                if (!string.IsNullOrEmpty(home))
                {
                    additions.Add(Path.Combine(home, ".dotnet"));
                    additions.Add(Path.Combine(home, ".dotnet", "tools"));
                    additions.Add(Path.Combine(home, ".cargo", "bin"));
                    additions.Add(Path.Combine(home, ".local", "bin"));
                }

                var merged = new List<string>();
                foreach (string dir in additions)
                {
                    if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir) && !existing.Contains(dir))
                    {
                        merged.Add(dir);
                        existing.Add(dir);
                    }
                }
                if (merged.Count > 0)
                {
                    Environment.SetEnvironmentVariable("PATH", string.Join(":", merged) + ":" + currentPath);
                }
            }
            catch { }
        }

        private static void RegisterCrashGuards()
        {
            AppDomain.CurrentDomain.UnhandledException += (s, e) => LogCrash("AppDomain", e.ExceptionObject as Exception);
            TaskScheduler.UnobservedTaskException += (s, e) =>
            {
                LogCrash("UnobservedTask", e.Exception);
                e.SetObserved();
            };
        }

        private static void LogCrash(string source, Exception ex)
        {
            if (ex == null) return;
            try
            {
                string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                string dir = Path.Combine(home, ".codeless");
                Directory.CreateDirectory(dir);
                File.AppendAllText(
                    Path.Combine(dir, "crash.log"),
                    $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] [{source}] {ex}{Environment.NewLine}",
                    Encoding.UTF8);
            }
            catch { }
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
