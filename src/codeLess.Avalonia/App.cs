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
        public static AvaloniaWindowManager WindowManager { get; private set; }
        public static System.Collections.Generic.IEnumerable<codeLess.Studio.IWorkbenchWindow> TestOpenWindows { get; set; }
        public static string StartupWorkspacePath { get; set; }

        private static readonly System.Diagnostics.Stopwatch launchClock = System.Diagnostics.Stopwatch.StartNew();

        /// <summary>
        /// AppKit treats every path on the process command line as a document to open and re-delivers it as an
        /// "open file" Apple Event right after launch. Started as `dotnet codeLess.dll ...` that echoed the managed
        /// entry assembly itself ("'codeLess.dll' is a binary file and was not opened"), and `--workspace` /
        /// `--screenshot-suite` values were opened a second time. Program.Main already handled argv, so drop those
        /// echoes during startup; later Open With / dock drops of the same path still work.
        /// </summary>
        public static bool IsEchoedStartupArgument(string path)
        {
            if (string.IsNullOrEmpty(path) || launchClock.Elapsed > TimeSpan.FromSeconds(15)) return false;
            try
            {
                string full = Path.GetFullPath(path).TrimEnd('/', '\\');
                foreach (string arg in Environment.GetCommandLineArgs())
                {
                    if (string.IsNullOrEmpty(arg) || arg.StartsWith("-", StringComparison.Ordinal)) continue;
                    string a;
                    try { a = Path.GetFullPath(arg).TrimEnd('/', '\\'); } catch { continue; }
                    if (string.Equals(a, full, StringComparison.Ordinal)) return true;
                }
            }
            catch { }
            return false;
        }
        public static string ScreenshotOutputPath { get; set; }
        public static string ScreenshotSuiteDir { get; set; }

        /// <summary>Screenshot suite, benchmarks, smoke and self-test runs: no side effects on the machine (e.g. no server installs).</summary>
        public static bool IsAutomationRun =>
            !string.IsNullOrWhiteSpace(SmokeQuitReportPath) || !string.IsNullOrWhiteSpace(BenchmarkReportPath) || !string.IsNullOrWhiteSpace(IdleProbeReportPath) ||
            !string.IsNullOrWhiteSpace(ScreenshotSuiteDir) || !string.IsNullOrWhiteSpace(ScreenshotOutputPath) || MainWindow.IsMonorepoBenchRequested ||
            !string.IsNullOrWhiteSpace(BenchReadyPath) || !string.IsNullOrWhiteSpace(BenchOpenReportPath) || !string.IsNullOrWhiteSpace(NativeSelfTestReportPath);
        public static string SmokeQuitReportPath { get; set; }
        public static string NativeSelfTestReportPath { get; set; }
        public static string BenchmarkReportPath { get; set; }
        public static string IdleProbeReportPath { get; set; }
        public static string BenchReadyPath { get; set; }
        public static string BenchOpenFilePath { get; set; }
        public static string BenchOpenReportPath { get; set; }
        public static int BenchOpenDelayMs { get; set; }
        public static DateTime MainEnteredUtc { get; set; }

        public App()
        {
            Name = "codeLess";
        }

        public override void Initialize()
        {
            Name = "codeLess";
            RequestedThemeVariant = ThemeVariant.Dark;
            Styles.Add(new FluentTheme());

            // The complete macOS application menu (About, Check for Updates..., Settings, Hide, Quit). It must exist now:
            // Avalonia's macOS exporter reads the Application menu once, right after Initialize (see AppNativeMenu).
            // Quit closes via the window so the unsaved-changes prompt in MainWindow.OnClosing can run.
            NativeMenu.SetMenu(this, AppNativeMenu.Build(
                ActiveWorkbenchWindow,
                () => {
                    var lifetime = ApplicationLifetime as IClassicDesktopStyleApplicationLifetime;
                    if (lifetime != null) {
                        var wm = new AvaloniaWindowManager(lifetime);
                        wm.QuitApplication();
                    }
                }));
        }

        /// <summary>The focused workbench window (New Window opens more), else the main one.</summary>
        private MainWindow ActiveWorkbenchWindow()
        {
            if (ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime dt) return null;
            foreach (var w in dt.Windows)
                if (w is MainWindow m && m.IsActive) return m;
            return dt.MainWindow as MainWindow;
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
                        "[" + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff", System.Globalization.CultureInfo.InvariantCulture) + "] [UIThread] " + e.Exception + Environment.NewLine);
                }
                catch { }
                e.Handled = true;
            };
            if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            {
                // Finder/Dock launches get a minimal PATH: import the user's login-shell environment (nvm, pyenv, Homebrew...)
                // in the background so tasks, language servers and debug adapters find the user's tools (VS Code parity).
                UserShellEnvironment.StartResolve();
                
                WindowManager = new AvaloniaWindowManager(desktop);
                var wm = WindowManager;
                
                codeLess.Studio.ApplicationSession.OnMessageReceived = (msg, ack) => {
                    Dispatcher.UIThread.Post(() => {
                        codeLess.Studio.ApplicationSession.ProcessArgs(msg.Args, msg.Cwd, wm, ack);
                    });
                };
                
                codeLess.Studio.ApplicationSession.ProcessArgs(desktop.Args, Environment.CurrentDirectory, wm);
                
                var mainWindow = desktop.MainWindow as MainWindow;
                if (mainWindow == null && desktop.Windows.Count > 0) {
                    mainWindow = desktop.Windows[0] as MainWindow;
                    desktop.MainWindow = mainWindow;
                }

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
                                if (!string.IsNullOrEmpty(path) && !IsEchoedStartupArgument(path))
                                {
                                    Dispatcher.UIThread.Post(() => {
                                        codeLess.Studio.ApplicationSession.ProcessArgs(new string[] { path }, Environment.CurrentDirectory, wm);
                                    });
                                }
                            }
                        }
                        else if (e.Kind == ActivationKind.Background || e.Kind == ActivationKind.OpenUri || e is ActivatedEventArgs)
                        {
                            // Dock click when no windows are open
                            if (!System.Linq.Enumerable.Any(System.Linq.Enumerable.OfType<MainWindow>(desktop.Windows)))
                            {
                                Dispatcher.UIThread.Post(() => {
                                    wm.CreateNewWindow(null);
                                });
                            }
                        }
                    };
                }

                bool automatedRun = !string.IsNullOrWhiteSpace(SmokeQuitReportPath) || !string.IsNullOrWhiteSpace(BenchmarkReportPath) || !string.IsNullOrWhiteSpace(IdleProbeReportPath) || !string.IsNullOrWhiteSpace(ScreenshotSuiteDir) || !string.IsNullOrWhiteSpace(ScreenshotOutputPath) || MainWindow.IsMonorepoBenchRequested || !string.IsNullOrWhiteSpace(BenchReadyPath) || !string.IsNullOrWhiteSpace(BenchOpenReportPath) || !string.IsNullOrWhiteSpace(NativeSelfTestReportPath);
                MainWindow.AttachMonorepoBenchIfRequested(mainWindow);
                if (!string.IsNullOrWhiteSpace(BenchReadyPath) || !string.IsNullOrWhiteSpace(BenchOpenFilePath))
                    mainWindow.StartBenchProbes(BenchReadyPath, BenchOpenFilePath, BenchOpenReportPath, BenchOpenDelayMs);
                if (!automatedRun)
                    mainWindow.Opened += (s, e) => Dispatcher.UIThread.Post(mainWindow.MaybeAutoCheckForUpdates, DispatcherPriority.Background);

                if (!string.IsNullOrWhiteSpace(NativeSelfTestReportPath))
                {
                    string report = NativeSelfTestReportPath;
                    mainWindow.Opened += async (s, e) => await mainWindow.RunNativeSelfTestAsync(report);
                    var selfTestWatchdog = new System.Threading.Thread(() =>
                    {
                        System.Threading.Thread.Sleep(15000);
                        try { if (!File.Exists(report)) File.WriteAllText(report, "{\"result\":\"watchdog-timeout\"}"); } catch { }
                        Environment.Exit(3);
                    }) { IsBackground = true, Name = "NativeSelfTestWatchdog" };
                    selfTestWatchdog.Start();
                }
                else if (!string.IsNullOrWhiteSpace(IdleProbeReportPath))
                {
                    mainWindow.Opened += async (s, e) => await mainWindow.RunIdleProbeAsync(IdleProbeReportPath);
                }
                else if (!string.IsNullOrWhiteSpace(BenchmarkReportPath))
                {
                    mainWindow.Opened += async (s, e) => await mainWindow.RunBenchmarkAsync(BenchmarkReportPath);
                }
                else if (!string.IsNullOrWhiteSpace(SmokeQuitReportPath))
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
