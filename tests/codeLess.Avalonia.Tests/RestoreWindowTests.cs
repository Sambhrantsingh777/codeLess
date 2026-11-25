using System;
using System.IO;
using Xunit;
using codeLess.Studio;

namespace codeLess.AvaloniaApp.Tests
{
    [Collection("SequentialWorkbench")]
    public class RestoreWindowTests : IDisposable
    {
        private readonly string _prevSettingsDir;
        private readonly string _tempDir;

        public RestoreWindowTests()
        {
            _prevSettingsDir = Environment.GetEnvironmentVariable("CODELESS_SETTINGS_DIR");
            _tempDir = Path.Combine(Path.GetTempPath(), "settings-rw-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_tempDir);
            Environment.SetEnvironmentVariable("CODELESS_SETTINGS_DIR", _tempDir);
        }

        public void Dispose()
        {
            Environment.SetEnvironmentVariable("CODELESS_SETTINGS_DIR", _prevSettingsDir);
            try { if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, true); } catch { }
        }

        [Fact]
        public void ProcessArgs_RestoreWindows_All()
        {
            var wm = new FakeWindowManager();
            var settings = IdePersistentSettings.Load();
            settings.WindowRestoreWindows = "all";
            settings.RestoreWindowPaths.Clear();
            settings.RestoreWindows.Clear();
            settings.RestoreWindowPaths.Add("/workspace/a");
            settings.RestoreWindowPaths.Add("/workspace/b");
            settings.RestoreWindowPaths.Add("/workspace/c");
            settings.RestoreWindows.Add(new RestoreWindowInfo { WorkspacePath = "/workspace/a", X = 50, Y = 60, Width = 1200, Height = 800 });
            settings.RestoreWindows.Add(new RestoreWindowInfo { WorkspacePath = "/workspace/b", X = 80, Y = 90, Width = 1300, Height = 850 });
            settings.RestoreWindows.Add(new RestoreWindowInfo { WorkspacePath = "/workspace/c", X = 110, Y = 120, Width = 1400, Height = 900 });
            settings.Save();

            ApplicationSession.ProcessArgs(new string[0], "", wm);
            Assert.Equal(3, wm.Windows.Count);
            Assert.Equal("/workspace/a", wm.Windows[0].WorkspaceRoot);
            Assert.Equal("/workspace/b", wm.Windows[1].WorkspaceRoot);
            Assert.Equal("/workspace/c", wm.Windows[2].WorkspaceRoot);
            Assert.Equal(1200, wm.Windows[0].Bounds.Width);
            Assert.Equal(1300, wm.Windows[1].Bounds.Width);
            Assert.Equal(1400, wm.Windows[2].Bounds.Width);
        }

        [Fact]
        public void ProcessArgs_RestoreWindows_One()
        {
            var wm = new FakeWindowManager();
            var settings = IdePersistentSettings.Load();
            settings.WindowRestoreWindows = "one";
            settings.RestoreWindowPaths.Clear();
            settings.RestoreWindows.Clear();
            settings.RestoreWindowPaths.Add("/workspace/a");
            settings.RestoreWindowPaths.Add("/workspace/b");
            settings.Save();

            ApplicationSession.ProcessArgs(new string[0], "", wm);
            Assert.Single(wm.Windows);
            Assert.Equal("/workspace/a", wm.Windows[0].WorkspaceRoot);
        }

        [Fact]
        public void ProcessArgs_RestoreWindows_None()
        {
            var wm = new FakeWindowManager();
            var settings = IdePersistentSettings.Load();
            settings.WindowRestoreWindows = "none";
            settings.RestoreWindowPaths.Clear();
            settings.RestoreWindows.Clear();
            settings.RestoreWindowPaths.Add("/workspace/a");
            settings.Save();

            ApplicationSession.ProcessArgs(new string[0], "", wm);
            Assert.Single(wm.Windows);
            Assert.Null(wm.Windows[0].WorkspaceRoot);
        }
    }
}
