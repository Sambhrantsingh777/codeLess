using System;
using System.IO;
using System.Threading;
using Xunit;
using codeLess.Studio;

namespace codeLess.AvaloniaApp.Tests
{
    [Collection("SequentialWorkbench")]
    public class IdeSettingsWatcherTests : IDisposable
    {
        private readonly string _prevSettingsDir;
        private readonly string _tempDir;

        public IdeSettingsWatcherTests()
        {
            _prevSettingsDir = Environment.GetEnvironmentVariable("CODELESS_SETTINGS_DIR");
            _tempDir = Path.Combine(Path.GetTempPath(), "settings-watch-test-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_tempDir);
            Environment.SetEnvironmentVariable("CODELESS_SETTINGS_DIR", _tempDir);
        }

        public void Dispose()
        {
            IdeSettingsWatcher.DebounceTimerChangeSeam = null;
            IdeSettingsWatcher.StopForTests();
            Environment.SetEnvironmentVariable("CODELESS_SETTINGS_DIR", _prevSettingsDir);
            try { if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, true); } catch { }
        }

        [Fact]
        public void SettingsFileChanged_Debounced()
        {
            string path = IdePersistentSettings.GetSettingsFilePath();
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            File.WriteAllText(path, "{}");

            int fires = 0;
            IdePersistentSettings lastLoaded = null;
            Action<IdePersistentSettings> handler = (s) =>
            {
                lastLoaded = s;
                Interlocked.Increment(ref fires);
            };

            int scheduledCount = 0;
            Timer controlledTimer = null;
            controlledTimer = new Timer(_ =>
            {
                IdeSettingsWatcher.ForceDebounceTickForTests();
            }, null, Timeout.Infinite, Timeout.Infinite);

            IdeSettingsWatcher.DebounceTimerChangeSeam = (ms) =>
            {
                Interlocked.Increment(ref scheduledCount);
                controlledTimer.Change(ms, Timeout.Infinite);
            };

            IdeSettingsWatcher.OnSettingsFileChanged += handler;
            try
            {
                IdeSettingsWatcher.Start();

                // Burst of writes within the 300ms debounce window
                File.WriteAllText(path, "{\"TabSize\": 2}");
                IdeSettingsWatcher.ScheduleDebounce(300);
                Thread.Sleep(40);
                File.WriteAllText(path, "{\"TabSize\": 4}");
                IdeSettingsWatcher.ScheduleDebounce(300);
                Thread.Sleep(40);
                File.WriteAllText(path, "{\"TabSize\": 8}");
                IdeSettingsWatcher.ScheduleDebounce(300);

                // Before the 300ms window elapses, the callback has not fired yet
                Assert.Equal(0, Volatile.Read(ref fires));
                Assert.True(Volatile.Read(ref scheduledCount) >= 3);

                // Wait for the single debounced tick to fire
                for (int i = 0; i < 30 && Volatile.Read(ref fires) == 0; i++)
                {
                    Thread.Sleep(30);
                }
                Thread.Sleep(150);

                Assert.Equal(1, fires);
                Assert.NotNull(lastLoaded);
                Assert.Equal(8, lastLoaded.TabSize);
            }
            finally
            {
                IdeSettingsWatcher.OnSettingsFileChanged -= handler;
                IdeSettingsWatcher.DebounceTimerChangeSeam = null;
                controlledTimer.Dispose();
                IdeSettingsWatcher.StopForTests();
            }
        }
    }
}
