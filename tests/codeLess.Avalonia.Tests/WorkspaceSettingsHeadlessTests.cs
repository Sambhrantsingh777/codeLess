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
        public void WorkspaceSettings_ApplyInMemory_AndDoNotLeak()
        {
            string w1Root = Path.Combine(Path.GetTempPath(), "w1_test_" + Guid.NewGuid());
            Directory.CreateDirectory(w1Root);
            
            string w3Root = Path.Combine(Path.GetTempPath(), "w3_test_" + Guid.NewGuid());
            Directory.CreateDirectory(w3Root);
            
            string wsPath = VscodeSettingsManager.GetWorkspaceSettingsPath(w1Root);
            Directory.CreateDirectory(Path.GetDirectoryName(wsPath));
            File.WriteAllText(wsPath, "{\n  \"editor.tabSize\": 8\n}");
            
            var baseSettings = StudioSettings.Load();
            int baseTabSize = baseSettings.TabSize;
            
            var w1 = new MainWindow(w1Root);
            Assert.Equal(8, AvaloniaVirtualEditorCanvas.DisplayTabSize);
            
            var w3 = new MainWindow(w3Root);
            Assert.Equal(baseTabSize, AvaloniaVirtualEditorCanvas.DisplayTabSize);
        }
    }
}
