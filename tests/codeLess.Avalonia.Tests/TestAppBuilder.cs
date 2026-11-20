using Avalonia;
using Avalonia.Headless;
using codeLess.AvaloniaApp;

[assembly: AvaloniaTestApplication(typeof(codeLess.Avalonia.Tests.TestAppBuilder))]

namespace codeLess.Avalonia.Tests
{
    public class TestAppBuilder
    {
        public static AppBuilder BuildAvaloniaApp() =>
            AppBuilder.Configure<App>()
                .UseHeadless(new AvaloniaHeadlessPlatformOptions
                {
                    UseHeadlessDrawing = true
                });
    }
}
