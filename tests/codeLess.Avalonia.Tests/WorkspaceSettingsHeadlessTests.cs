using System;
using Avalonia.Headless.XUnit;
using System.IO;
using System.Threading.Tasks;
using Xunit;
using codeLess.Core;
using codeLess.Studio;
using codeLess.AvaloniaApp;

namespace codeLess.Avalonia.Tests
{
    public class WorkspaceSettingsHeadlessTests
    {
        [AvaloniaFact]
        public async Task WorkspaceSettings_ApplyInMemory_AndDoNotLeak()
        {
            string w1Root = Path.Combine(Path.GetTempPath(), "w1_test_" + Guid.NewGuid());
            Directory.CreateDirectory(w1Root);
            var w1 = new MainWindow(w1Root);
            var w2 = new MainWindow(null);
            
            string wsPath = VscodeSettingsManager.GetWorkspaceSettingsPath(w1Root);
            Directory.CreateDirectory(Path.GetDirectoryName(wsPath));
            File.WriteAllText(wsPath, "{ \"editor.tabSize\": 8 }");
            
            // We need to wait for watcher debounce, but we can also manually trigger apply
            var clone1 = IdePersistentSettings.Load();
            VscodeSettingsManager.ApplySettings(File.ReadAllText(wsPath), clone1);
            
            Assert.Equal(8, clone1.TabSize);
            
            var clone2 = IdePersistentSettings.Load();
            Assert.NotEqual(8, clone2.TabSize); // did not leak globally
        }
    }
}
