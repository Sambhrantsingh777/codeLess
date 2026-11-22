using Avalonia;
using Avalonia.Headless;
using codeLess.AvaloniaApp;

[assembly: AvaloniaTestApplication(typeof(codeLess.Avalonia.Tests.TestAppBuilder))]

namespace codeLess.Avalonia.Tests
{
    public class TestAppBuilder
    {
        public static AppBuilder BuildAvaloniaApp()
        {
            // Headless windows never load the developer's real ~/.codeless/extensions (themes / snippets would leak
            // into the tests' theme lists); extension tests point this at their own folder.
            if (MainWindow.ExtensionsRootOverride == null)
                MainWindow.ExtensionsRootOverride = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "codeless-test-extensions-" + System.Environment.ProcessId);
            return AppBuilder.Configure<App>()
                .UseHeadless(new AvaloniaHeadlessPlatformOptions
                {
                    UseHeadlessDrawing = true
                });
        }
    }
}
