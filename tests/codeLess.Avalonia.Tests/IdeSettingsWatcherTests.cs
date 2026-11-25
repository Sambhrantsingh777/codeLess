using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Xunit;
using codeLess.Studio;

namespace codeLess.AvaloniaApp.Tests
{
    public class IdeSettingsWatcherTests
    {
        [Fact]
        public void SettingsFileChanged_Debounced()
        {
            // Setup a fake settings file
            Environment.SetEnvironmentVariable("CODELESS_SETTINGS_DIR", Path.Combine(Path.GetTempPath(), "settings-watch-test-" + Guid.NewGuid().ToString("N")));
            string path = IdePersistentSettings.GetSettingsFilePath();
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            File.WriteAllText(path, "{}");

            int fires = 0;
            Action<IdePersistentSettings> handler = (s) => {
                Interlocked.Increment(ref fires);
            };
            IdeSettingsWatcher.OnSettingsFileChanged += handler;
            IdeSettingsWatcher.Start();

            // Simulate external writes
            File.WriteAllText(path, "{\"TabSize\": 2}");
            Thread.Sleep(50);
            File.WriteAllText(path, "{\"TabSize\": 4}");
            Thread.Sleep(50);
            File.WriteAllText(path, "{\"TabSize\": 8}");

            // Wait for debounce
            Thread.Sleep(1000);

            Assert.Equal(1, fires);
            IdeSettingsWatcher.OnSettingsFileChanged -= handler;
            
            // cleanup
            try { Directory.Delete(Path.GetDirectoryName(path), true); } catch { }
        }
    }
}
