using System;
using System.IO;
using Xunit;
using codeLess.Core;
using codeLess.AvaloniaApp;
using Avalonia.Headless.XUnit;

namespace codeLess.Avalonia.Tests
{
    [Collection("SequentialWorkbench")]
    public class WorkspaceSettingsHeadlessTests2
    {
        [AvaloniaFact]
        public void WorkspaceTheme_NotPersisted()
        {
            string dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
            Directory.CreateDirectory(dir);
            MainWindow mw = null;
            try
            {
                mw = new MainWindow();
                string initialTheme = codeLess.Studio.StudioSettings.Load().ActiveThemeId;

                string vscodeDir = Path.Combine(dir, ".vscode");
                Directory.CreateDirectory(vscodeDir);
                File.WriteAllText(Path.Combine(vscodeDir, "settings.json"), "{ \"workbench.colorTheme\": \"High Contrast\" }");

                mw.LoadWorkspace(dir);

                string persistedTheme = codeLess.Studio.StudioSettings.Load().ActiveThemeId;
                Assert.Equal(initialTheme, persistedTheme);
            }
            finally
            {
                if (mw != null) { mw.SuppressUnsavedPrompts = true; mw.Close(); }
                try { Directory.Delete(dir, true); } catch { }
            }
        }

        [AvaloniaFact]
        public void OpenFolder_AppliesWorkspaceSettings()
        {
            string dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
            Directory.CreateDirectory(dir);
            MainWindow mw = null;
            try
            {
                mw = new MainWindow();
                string vscodeDir = Path.Combine(dir, ".vscode");
                Directory.CreateDirectory(vscodeDir);
                File.WriteAllText(Path.Combine(vscodeDir, "settings.json"), "{ \"editor.tabSize\": 12 }");

                mw.LoadWorkspace(dir);

                Assert.Equal(12, mw.PrimaryEditor.Canvas.TabSize);
            }
            finally
            {
                if (mw != null) { mw.SuppressUnsavedPrompts = true; mw.Close(); }
                try { Directory.Delete(dir, true); } catch { }
            }
        }

        [AvaloniaFact]
        public void UserChange_KeepsWorkspaceOverride()
        {
            string dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
            Directory.CreateDirectory(dir);
            string userPath = VscodeSettingsManager.GetUserSettingsPath();
            bool hadUser = File.Exists(userPath);
            string userBackup = hadUser ? File.ReadAllText(userPath) : null;
            MainWindow mw = null;
            try
            {
                mw = new MainWindow();
                string vscodeDir = Path.Combine(dir, ".vscode");
                Directory.CreateDirectory(vscodeDir);
                File.WriteAllText(Path.Combine(vscodeDir, "settings.json"), "{ \"editor.tabSize\": 14 }");
                mw.LoadWorkspace(dir);
                Assert.Equal(14, mw.PrimaryEditor.Canvas.TabSize);

                Directory.CreateDirectory(Path.GetDirectoryName(userPath));
                File.WriteAllText(userPath, "{ \"editor.tabSize\": 3 }");

                var method = typeof(MainWindow).GetMethod("ApplyVscodeSettings", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                method.Invoke(mw, new object[] { "{ \"editor.tabSize\": 3 }", null });

                Assert.Equal(14, mw.PrimaryEditor.Canvas.TabSize);
            }
            finally
            {
                if (mw != null) { mw.SuppressUnsavedPrompts = true; mw.Close(); }
                try
                {
                    if (hadUser) File.WriteAllText(userPath, userBackup);
                    else if (File.Exists(userPath)) File.Delete(userPath);
                }
                catch { }
                try { Directory.Delete(dir, true); } catch { }
            }
        }
    }
}
