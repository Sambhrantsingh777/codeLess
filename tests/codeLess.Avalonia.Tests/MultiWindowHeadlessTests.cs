using System.IO;
using System;
using Avalonia.Headless.XUnit;
using System.Linq;
using Xunit;
using codeLess.Core;
using codeLess.AvaloniaApp;

namespace codeLess.Avalonia.Tests
{
    public class MultiWindowHeadlessTests
    {
        [AvaloniaFact]
        public void WindowClose_DisposesWatchers()
        {
            string wRoot = Path.Combine(Path.GetTempPath(), "w_test_" + Guid.NewGuid());
            Directory.CreateDirectory(wRoot);
            string wsPath = VscodeSettingsManager.GetWorkspaceSettingsPath(wRoot);
            Directory.CreateDirectory(Path.GetDirectoryName(wsPath));
            File.WriteAllText(wsPath, "{}");

            var w = new MainWindow(wRoot);
            var wsWatcher = w.GetType().GetField("workspaceSettingsWatcher", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance).GetValue(w);
            var userWatcher = w.GetType().GetField("userSettingsWatcher", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance).GetValue(w);
            var keysWatcher = w.GetType().GetField("keybindingsWatcher", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance).GetValue(w);
            
            Assert.NotNull(wsWatcher);
            Assert.NotNull(userWatcher);
            Assert.NotNull(keysWatcher);

            w.Close(); // Will call OnClosed
            
            wsWatcher = w.GetType().GetField("workspaceSettingsWatcher", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance).GetValue(w);
            userWatcher = w.GetType().GetField("userSettingsWatcher", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance).GetValue(w);
            keysWatcher = w.GetType().GetField("keybindingsWatcher", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance).GetValue(w);
            
            Assert.Null(wsWatcher);
            Assert.Null(userWatcher);
            Assert.Null(keysWatcher);
        }
    }
}
