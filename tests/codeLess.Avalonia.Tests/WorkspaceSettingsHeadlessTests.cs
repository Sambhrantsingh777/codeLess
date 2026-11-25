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
    [Collection("SequentialWorkbench")]
    public class WorkspaceSettingsHeadlessTests
    {
        [AvaloniaFact]
        public void WorkspaceSettings_ApplyInMemory_AndDoNotLeak()
        {
            string w1Root = Path.Combine(Path.GetTempPath(), "w1_test_" + Guid.NewGuid());
            Directory.CreateDirectory(w1Root);

            string w3Root = Path.Combine(Path.GetTempPath(), "w3_test_" + Guid.NewGuid());
            Directory.CreateDirectory(w3Root);

            MainWindow w1 = null;
            MainWindow w3 = null;
            try
            {
                string wsPath = VscodeSettingsManager.GetWorkspaceSettingsPath(w1Root);
                Directory.CreateDirectory(Path.GetDirectoryName(wsPath));
                File.WriteAllText(wsPath, "{\n  \"editor.tabSize\": 8\n}");

                var baseSettings = StudioSettings.Load();
                int baseTabSize = baseSettings.TabSize;

                w1 = new MainWindow(w1Root);
                Assert.Equal(8, w1.PrimaryEditor?.Canvas?.DisplayTabSize);

                w3 = new MainWindow(w3Root);
                Assert.Equal(baseTabSize, w3.PrimaryEditor?.Canvas?.DisplayTabSize);
                Assert.Equal(8, w1.PrimaryEditor?.Canvas?.DisplayTabSize);
            }
            finally
            {
                if (w1 != null) { w1.SuppressUnsavedPrompts = true; w1.Close(); }
                if (w3 != null) { w3.SuppressUnsavedPrompts = true; w3.Close(); }
                try { Directory.Delete(w1Root, true); } catch { }
                try { Directory.Delete(w3Root, true); } catch { }
            }
        }
    }
}
