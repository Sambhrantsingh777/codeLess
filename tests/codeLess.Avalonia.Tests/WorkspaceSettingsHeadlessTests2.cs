using System;
using System.IO;
using Xunit;
using codeLess.Core;
using codeLess.AvaloniaApp;
using Avalonia.Headless.XUnit;

namespace codeLess.Avalonia.Tests
{
    public class WorkspaceSettingsHeadlessTests2
    {
        [AvaloniaFact]
        public void WorkspaceTheme_NotPersisted()
        {
            string dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
            Directory.CreateDirectory(dir);
            try {
                var mw = new MainWindow();
                string initialTheme = codeLess.Studio.StudioSettings.Load().ActiveThemeId;
                
                string vscodeDir = Path.Combine(dir, ".vscode");
                Directory.CreateDirectory(vscodeDir);
                File.WriteAllText(Path.Combine(vscodeDir, "settings.json"), "{ \"workbench.colorTheme\": \"High Contrast\" }");
                
                mw.LoadWorkspace(dir);
                
                string persistedTheme = codeLess.Studio.StudioSettings.Load().ActiveThemeId;
                Assert.Equal(initialTheme, persistedTheme);
            } finally {
                Directory.Delete(dir, true);
            }
        }

        [AvaloniaFact]
        public void OpenFolder_AppliesWorkspaceSettings()
        {
            string dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
            Directory.CreateDirectory(dir);
            try {
                var mw = new MainWindow();
                string vscodeDir = Path.Combine(dir, ".vscode");
                Directory.CreateDirectory(vscodeDir);
                File.WriteAllText(Path.Combine(vscodeDir, "settings.json"), "{ \"editor.tabSize\": 12 }");
                
                mw.LoadWorkspace(dir);
                
                Assert.Equal(12, mw.PrimaryEditor.Canvas.TabSize);
            } finally {
                Directory.Delete(dir, true);
            }
        }
        
        [AvaloniaFact]
        public void UserChange_KeepsWorkspaceOverride()
        {
            string dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
            Directory.CreateDirectory(dir);
            try {
                var mw = new MainWindow();
                string vscodeDir = Path.Combine(dir, ".vscode");
                Directory.CreateDirectory(vscodeDir);
                File.WriteAllText(Path.Combine(vscodeDir, "settings.json"), "{ \"editor.tabSize\": 14 }");
                mw.LoadWorkspace(dir);
                Assert.Equal(14, mw.PrimaryEditor.Canvas.TabSize);

                string userPath = VscodeSettingsManager.GetUserSettingsPath();
                File.WriteAllText(userPath, "{ \"editor.tabSize\": 3 }");
                
                var method = typeof(MainWindow).GetMethod("ApplyVscodeSettings", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                method.Invoke(mw, new object[] { "{ \"editor.tabSize\": 3 }", null });
                
                Assert.Equal(14, mw.PrimaryEditor.Canvas.TabSize);
            } finally {
                Directory.Delete(dir, true);
            }
        }
    }
}
