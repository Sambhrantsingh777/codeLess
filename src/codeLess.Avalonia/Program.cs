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
                    else if (a == "--smoke-quit" && i + 1 < args.Length)
                    {
                        App.SmokeQuitReportPath = args[++i];
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

            int exitCode = BuildAvaloniaApp().StartWithClassicDesktopLifetime(args ?? Array.Empty<string>());
            // The UI lifetime has ended (last window closed / Cmd+Q). Terminate deterministically so no lingering
            // worker (blocked pty write, git subprocess wait, HTTP stream) can keep a windowless process alive.
            Environment.Exit(exitCode);
        }

        /// <summary>
        /// GUI launches (Finder, Dock, Spotlight, .desktop files) do not inherit the login shell's environment, so
        /// nvm / pyenv / asdf / volta / Homebrew paths and exported API keys were missing for git, syntax checks and
        /// the agent's run_terminal. Like VS Code, resolve the environment once from "$SHELL -ilc env" and merge it.
        /// </summary>
        private static void ImportLoginShellEnvironment()
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return;
            // Started from a terminal: the environment is already the user's shell environment.
            if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("SHLVL"))) return;
            try
            {
                string shell = Environment.GetEnvironmentVariable("SHELL");
                if (string.IsNullOrEmpty(shell) || !File.Exists(shell)) shell = File.Exists("/bin/zsh") ? "/bin/zsh" : "/bin/bash";
                const string marker = "__CODELESS_ENV_BEGIN__";
                var psi = new System.Diagnostics.ProcessStartInfo(shell)
                {
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    RedirectStandardInput = true,
                    CreateNoWindow = true
                };
                psi.ArgumentList.Add("-ilc");
                psi.ArgumentList.Add("printf '" + marker + "'; command env -0");
                psi.Environment["CODELESS_RESOLVING_ENVIRONMENT"] = "1";
                using var p = System.Diagnostics.Process.Start(psi);
                if (p == null) return;
                p.StandardInput.Close();
                var outTask = p.StandardOutput.ReadToEndAsync();
                _ = p.StandardError.ReadToEndAsync();
                if (!p.WaitForExit(5000))
                {
                    try { p.Kill(true); } catch { }
                    return;
                }
                if (!outTask.Wait(1000)) return;
                string raw = outTask.Result ?? "";
                int idx = raw.LastIndexOf(marker, StringComparison.Ordinal);
                if (idx < 0) return;
                var skip = new HashSet<string>(StringComparer.Ordinal) { "PWD", "OLDPWD", "SHLVL", "_", "TERM", "TERM_PROGRAM", "TERM_PROGRAM_VERSION", "TERM_SESSION_ID", "CODELESS_RESOLVING_ENVIRONMENT", "PS1", "PS2" };
                foreach (string entry in raw.Substring(idx + marker.Length).Split('\0'))
                {
                    int eq = entry.IndexOf('=');
                    if (eq <= 0) continue;
                    string key = entry.Substring(0, eq);
                    string val = entry.Substring(eq + 1);
                    if (skip.Contains(key) || key.IndexOf('\n') >= 0) continue;
                    if (key == "PATH" || string.IsNullOrEmpty(Environment.GetEnvironmentVariable(key)))
                        Environment.SetEnvironmentVariable(key, val);
                }
            }
            catch { }
        }

        private static void EnsureDeveloperPathOnUnix()
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                return;
            ImportLoginShellEnvironment();

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
                // Avalonia's default Skia GPU resource cache is ~100 MB of texture memory (shown as "graphics" in
                // Activity Monitor). An editor's working set of glyph atlases / layers fits comfortably in 24 MB;
                // Skia evicts LRU textures beyond the limit.
                .With(new SkiaOptions { MaxGpuResourceSizeBytes = 24L * 1024 * 1024 })
                .With(new AvaloniaNativePlatformOptions { RenderingMode = ResolveMacRenderingModes() })
                .LogToTrace();
        }

        private static AvaloniaNativeRenderingMode[] ResolveMacRenderingModes()
        {
            string forced = (Environment.GetEnvironmentVariable("CODELESS_RENDER") ?? "").Trim().ToLowerInvariant();
            if (forced == "software") return new[] { AvaloniaNativeRenderingMode.Software };
            if (forced == "gl" || forced == "opengl") return new[] { AvaloniaNativeRenderingMode.OpenGl, AvaloniaNativeRenderingMode.Software };
            return new[] { AvaloniaNativeRenderingMode.Metal, AvaloniaNativeRenderingMode.OpenGl, AvaloniaNativeRenderingMode.Software };
        }
    }
}
