using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Xunit;
using codeLess.Studio;

namespace codeLess.AvaloniaApp.Tests
{
    public class RestoreWindowTests
    {
        [Fact]
        public void ProcessArgs_RestoreWindows_All()
        {
            var wm = new FakeWindowManager();
            var settings = IdePersistentSettings.Load();
            settings.WindowRestoreWindows = "all";
            settings.RestoreWindowPaths.Add("/workspace/a");
            settings.RestoreWindowPaths.Add("/workspace/b");
            settings.RestoreWindowPaths.Add("/workspace/c");
            settings.Save();

            ApplicationSession.ProcessArgs(new string[0], "", wm);
            Assert.Equal(3, wm.Windows.Count);
            Assert.Equal("/workspace/a", wm.Windows[0].WorkspaceRoot);
            Assert.Equal("/workspace/b", wm.Windows[1].WorkspaceRoot);
            Assert.Equal("/workspace/c", wm.Windows[2].WorkspaceRoot);
        }

        [Fact]
        public void ProcessArgs_RestoreWindows_One()
        {
            var wm = new FakeWindowManager();
            var settings = IdePersistentSettings.Load();
            settings.WindowRestoreWindows = "one";
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
            settings.RestoreWindowPaths.Add("/workspace/a");
            settings.Save();

            ApplicationSession.ProcessArgs(new string[0], "", wm);
            Assert.Single(wm.Windows);
            Assert.Null(wm.Windows[0].WorkspaceRoot);
        }
    }
}
