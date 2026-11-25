using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Xunit;
using codeLess.Studio;

namespace codeLess.AvaloniaApp.Tests
{
    [Collection("SettingsTests")]
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
            
            int timerChanges = 0;
            IdeSettingsWatcher.DebounceTimerChangeSeam = (ms) => { Interlocked.Increment(ref timerChanges); };
            
            IdeSettingsWatcher.Start();

            // Simulate external writes
            File.WriteAllText(path, "{\"TabSize\": 2}");
            Thread.Sleep(200); // give FSW time to catch it
            File.WriteAllText(path, "{\"TabSize\": 4}");
            Thread.Sleep(200);
            File.WriteAllText(path, "{\"TabSize\": 8}");
            Thread.Sleep(200);

            // Trigger manual tick ONCE to simulate debounce completion
            IdeSettingsWatcher.ForceDebounceTickForTests();

            Assert.Equal(1, fires);
            Assert.True(timerChanges >= 1);
            
            IdeSettingsWatcher.DebounceTimerChangeSeam = null;
            IdeSettingsWatcher.OnSettingsFileChanged -= handler;
            
            // cleanup
            try { Directory.Delete(Path.GetDirectoryName(path), true); } catch { }
        }
    }
}
