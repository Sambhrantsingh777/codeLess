using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Xunit;
using codeLess.Core;
using codeLess.Studio;
using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.Headless.XUnit;

namespace codeLess.AvaloniaApp.Tests
{
    [Collection("SequentialWorkbench")]
    public class MultiWindowHeadlessTests : IDisposable
    {
        private readonly string _prevSettingsDir;
        private readonly string _tempRoot;
        private readonly List<MainWindow> _createdWindows = new List<MainWindow>();

        public MultiWindowHeadlessTests()
        {
            _prevSettingsDir = Environment.GetEnvironmentVariable("CODELESS_SETTINGS_DIR");
            _tempRoot = Path.Combine(Path.GetTempPath(), "mw-tests-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_tempRoot);
            string settingsDir = Path.Combine(_tempRoot, "settings");
            Directory.CreateDirectory(settingsDir);
            Environment.SetEnvironmentVariable("CODELESS_SETTINGS_DIR", settingsDir);

            // Ensure no leftover windows from other tests remain registered
            foreach (var existing in App.WindowManager.GetOpenWindows().OfType<MainWindow>().ToList())
            {
                try { existing.SuppressUnsavedPrompts = true; existing.Close(); } catch { }
            }
        }

        public void Dispose()
        {
            foreach (var w in _createdWindows.ToList())
            {
                try
                {
                    if (!w.IsClosed)
                    {
                        w.SuppressUnsavedPrompts = true;
                        w.Close();
                    }
                }
                catch { }
            }
            foreach (var w in App.WindowManager.GetOpenWindows().OfType<MainWindow>().ToList())
            {
                try
                {
                    w.SuppressUnsavedPrompts = true;
                    w.Close();
                }
                catch { }
            }
            Dispatcher.UIThread.RunJobs();
            IdeSettingsWatcher.StopForTests();
            Environment.SetEnvironmentVariable("CODELESS_SETTINGS_DIR", _prevSettingsDir);
            try { if (Directory.Exists(_tempRoot)) Directory.Delete(_tempRoot, true); } catch { }
        }

        private string CreateSubDir(string name)
        {
            string dir = Path.Combine(_tempRoot, name + "-" + Guid.NewGuid().ToString("N").Substring(0, 6));
            Directory.CreateDirectory(dir);
            return dir;
        }

        private MainWindow Track(MainWindow w)
        {
            if (w != null && !_createdWindows.Contains(w))
                _createdWindows.Add(w);
            return w;
        }

        [AvaloniaFact]
        public void WindowClose_DisposesWatchers()
        {
            string wRoot = Path.Combine(Path.GetTempPath(), "w_test_" + Guid.NewGuid());
            Directory.CreateDirectory(wRoot);
            try
            {
                string wsPath = VscodeSettingsManager.GetWorkspaceSettingsPath(wRoot);
                Directory.CreateDirectory(Path.GetDirectoryName(wsPath));
                File.WriteAllText(wsPath, "{}");

                var w = Track(new MainWindow(wRoot));
                var wsWatcher = w.GetType().GetField("workspaceSettingsWatcher", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance).GetValue(w);
                var userWatcher = w.GetType().GetField("userSettingsWatcher", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance).GetValue(w);
                var keysWatcher = w.GetType().GetField("keybindingsWatcher", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance).GetValue(w);

                Assert.NotNull(wsWatcher);
                Assert.NotNull(userWatcher);
                Assert.NotNull(keysWatcher);

                w.SuppressUnsavedPrompts = true;
                w.Close(); // Will call OnClosed

                wsWatcher = w.GetType().GetField("workspaceSettingsWatcher", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance).GetValue(w);
                userWatcher = w.GetType().GetField("userSettingsWatcher", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance).GetValue(w);
                keysWatcher = w.GetType().GetField("keybindingsWatcher", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance).GetValue(w);

                Assert.Null(wsWatcher);
                Assert.Null(userWatcher);
                Assert.Null(keysWatcher);
            }
            finally
            {
                try { Directory.Delete(wRoot, true); } catch { }
            }
        }

        [AvaloniaFact]
        public void WindowTitle_Format()
        {
            string tmpDir1 = CreateSubDir("ws1");
            string tmpDir2 = CreateSubDir("ws2");
            string file1 = Path.Combine(tmpDir1, "test.txt");
            string file2 = Path.Combine(tmpDir2, "other.txt");
            File.WriteAllText(file1, "hello");
            File.WriteAllText(file2, "world");

            var w1 = Track(new MainWindow(tmpDir1));
            var w2 = Track(new MainWindow(tmpDir2));
            w1.Show();
            w2.Show();

            var tab1 = w1.OpenFileInTab(file1, preview: false);
            var tab2 = w2.OpenFileInTab(file2, preview: false);
            Dispatcher.UIThread.RunJobs();

            string folder1 = Path.GetFileName(tmpDir1);
            string folder2 = Path.GetFileName(tmpDir2);
            Assert.Equal("test.txt — " + folder1 + " — codeLess", w1.Title);
            Assert.Equal("other.txt — " + folder2 + " — codeLess", w2.Title);

            // Dirty marker ("● file — folder — codeLess") when modified in w1, while w2 stays clean
            tab1.IsDirty = true;
            w1.UpdateWindowTitle();
            Assert.Equal("● test.txt — " + folder1 + " — codeLess", w1.Title);
            Assert.Equal("other.txt — " + folder2 + " — codeLess", w2.Title);

            tab1.IsDirty = false;
            w1.UpdateWindowTitle();
            Assert.Equal("test.txt — " + folder1 + " — codeLess", w1.Title);

            w1.SuppressUnsavedPrompts = true;
            w2.SuppressUnsavedPrompts = true;
            w1.Close();
            w2.Close();
        }

        [AvaloniaFact]
        public void MultipleWindows_OwnWorkspaceTabsTitleAndKeybindingManager()
        {
            string tmpDir1 = CreateSubDir("projA");
            string tmpDir2 = CreateSubDir("projB");
            string fileA = Path.Combine(tmpDir1, "alpha.cs");
            string fileB = Path.Combine(tmpDir2, "beta.py");
            File.WriteAllText(fileA, "class Alpha {}");
            File.WriteAllText(fileB, "print('beta')");

            var w1 = Track(App.WindowManager.CreateNewWindow(tmpDir1) as MainWindow);
            var w2 = Track(App.WindowManager.CreateNewWindow(tmpDir2) as MainWindow);
            Assert.NotNull(w1);
            Assert.NotNull(w2);
            Assert.NotSame(w1, w2);

            w1.OpenFileInTab(fileA, preview: false);
            w2.OpenFileInTab(fileB, preview: false);
            Dispatcher.UIThread.RunJobs();

            // Each window has its own workspace, tabs, title, and KeybindingManager
            Assert.Equal(Path.GetFullPath(tmpDir1), w1.WorkspaceRoot);
            Assert.Equal(Path.GetFullPath(tmpDir2), w2.WorkspaceRoot);
            Assert.Single(w1.OpenTabs);
            Assert.Single(w2.OpenTabs);
            Assert.Equal("alpha.cs", w1.ActiveTab.FileName);
            Assert.Equal("beta.py", w2.ActiveTab.FileName);
            Assert.NotEqual(w1.Title, w2.Title);

            Assert.NotNull(w1.KeybindingManager);
            Assert.NotNull(w2.KeybindingManager);
            Assert.NotSame(w1.KeybindingManager, w2.KeybindingManager);

            // Executing a command via w1's KeybindingManager affects only w1, not w2
            Assert.True(w1.KeybindingManager.ExecuteCommand("file.newTextFile"));
            Assert.Equal(2, w1.OpenTabs.Count);
            Assert.Single(w2.OpenTabs);

            w1.SuppressUnsavedPrompts = true;
            w2.SuppressUnsavedPrompts = true;
            w1.Close();
            w2.Close();
        }

        [AvaloniaFact]
        public void SharedWindowManager_OwnedByApp_AllWindowsRegister()
        {
            Assert.NotNull(App.WindowManager);
            var sameRef = App.WindowManager;
            Assert.Same(sameRef, App.WindowManager);

            string tmpDir1 = CreateSubDir("reg1");
            var w1 = Track(new MainWindow(tmpDir1));
            w1.Show();

            var w2 = Track(w1.CreateNewWindowInstance());
            Assert.NotNull(w2);

            var open = App.WindowManager.GetOpenWindows().ToList();
            Assert.Equal(2, open.Count);
            Assert.Contains(w1, open.Cast<MainWindow>());
            Assert.Contains(w2, open.Cast<MainWindow>());

            w2.Close();
            open = App.WindowManager.GetOpenWindows().ToList();
            Assert.Single(open);
            Assert.Same(w1, open[0]);

            w1.Close();
            Assert.Empty(App.WindowManager.GetOpenWindows());
        }

        [AvaloniaFact]
        public void NewWindow_OpensEmpty_Cascaded()
        {
            string tmpDir1 = CreateSubDir("cascade1");

            var w1 = Track(new MainWindow(tmpDir1));
            w1.Show();
            var pos1 = w1.Position;

            var w2 = Track(w1.CreateNewWindowInstance());
            Assert.NotNull(w2);

            var ww1 = (IWorkbenchWindow)w1;
            var ww2 = (IWorkbenchWindow)w2;

            Assert.Equal(Path.GetFullPath(tmpDir1), ww1.WorkspaceRoot);
            Assert.True(string.IsNullOrEmpty(ww2.WorkspaceRoot));
            Assert.True(w2.Position.X > pos1.X && w2.Position.Y > pos1.Y);

            var windows = App.WindowManager.GetOpenWindows().ToList();
            Assert.Equal(2, windows.Count);

            w2.Close();
            w1.Close();
        }

        [AvaloniaFact]
        public void CloseWindow_DisposesOwnResources()
        {
            string tmpDir1 = CreateSubDir("disp1");
            string tmpDir2 = CreateSubDir("disp2");

            var w1 = Track(new MainWindow(tmpDir1));
            var w2 = Track(new MainWindow(tmpDir2));
            w1.Show();
            w2.Show();

            w1.Close();
            Dispatcher.UIThread.RunJobs();

            Assert.True(w1.TestCheckDisposed());
            Assert.False(w2.TestCheckDisposed());

            w2.Close();
            Dispatcher.UIThread.RunJobs();
            Assert.True(w2.TestCheckDisposed());
        }

        [AvaloniaFact]
        public void SettingsChange_Propagates()
        {
            string tmpDir1 = CreateSubDir("scp1");
            string tmpDir2 = CreateSubDir("scp2");

            var w1 = Track(new MainWindow(tmpDir1));
            var w2 = Track(new MainWindow(tmpDir2));
            w1.Show();
            w2.Show();

            float oldSize = w2.GetEditorFontSizeForTests();
            float newSize = Math.Abs(oldSize - 20f) < 0.1f ? 22f : 20f;

            var settings = IdePersistentSettings.Load();
            settings.EditorFontSize = newSize;
            settings.Save();
            Dispatcher.UIThread.RunJobs();

            Assert.Equal(newSize, w1.GetEditorFontSizeForTests());
            Assert.Equal(newSize, w2.GetEditorFontSizeForTests());

            w1.Close();
            w2.Close();
        }

        [AvaloniaFact]
        public void WindowMenu_ItemsMatchOpenWindows()
        {
            string tmpDir1 = CreateSubDir("winmenu1");
            string tmpDir2 = CreateSubDir("winmenu2");

            var w1 = Track(new MainWindow(tmpDir1));
            var w2 = Track(new MainWindow(tmpDir2));
            w1.Show();
            w2.Show();
            Dispatcher.UIThread.RunJobs();

            // w2 was created/shown second, so it is currently the active window
            Assert.Same(w2, App.WindowManager.GetLastActiveWindow());

            var nativeMenu = NativeMenu.GetMenu(w2);
            Assert.NotNull(nativeMenu);
            var windowMenuEntry = nativeMenu.Items.OfType<NativeMenuItem>().FirstOrDefault(i => i.Header == "Window");
            Assert.NotNull(windowMenuEntry);
            Assert.NotNull(windowMenuEntry.Menu);

            var subItems = windowMenuEntry.Menu.Items.OfType<NativeMenuItem>().ToList();
            var w1Item = subItems.FirstOrDefault(i => i.Header == w1.Title);
            var w2Item = subItems.FirstOrDefault(i => i.Header == w2.Title);
            Assert.NotNull(w1Item);
            Assert.NotNull(w2Item);

            // Choosing w1 from the Window menu focuses w1
            Assert.NotNull(w1Item.Command);
            w1Item.Command.Execute(null);
            Dispatcher.UIThread.RunJobs();

            Assert.Same(w1, App.WindowManager.GetLastActiveWindow());
            Assert.True(((IWorkbenchWindow)w1).IsActive);

            w1.Close();
            w2.Close();
        }

        [AvaloniaFact]
        public void RestoreState_SingleClose_RemovesFromPaths()
        {
            string tmpDir1 = CreateSubDir("rs1");
            string tmpDir2 = CreateSubDir("rs2");

            var settings = IdePersistentSettings.Load();
            settings.WindowRestoreWindows = "all";
            settings.RestoreWindowPaths.Clear();
            settings.RestoreWindowPaths.Add(tmpDir1);
            settings.RestoreWindowPaths.Add(tmpDir2);
            settings.Save();

            var wm = App.WindowManager;
            var w1 = Track(wm.CreateNewWindow(tmpDir1) as MainWindow);
            var w2 = Track(wm.CreateNewWindow(tmpDir2) as MainWindow);

            w1.Close();
            Dispatcher.UIThread.RunJobs();

            var s2 = IdePersistentSettings.Load();
            Assert.DoesNotContain(tmpDir1, s2.RestoreWindowPaths);
            Assert.Contains(tmpDir2, s2.RestoreWindowPaths);

            w2.Close();
            Dispatcher.UIThread.RunJobs();
        }

        [AvaloniaFact]
        public void RestoreState_Quit_SavesAllPaths()
        {
            string tmpDir1 = CreateSubDir("rs3");
            string tmpDir2 = CreateSubDir("rs4");

            var settings = IdePersistentSettings.Load();
            settings.WindowRestoreWindows = "all";
            settings.RestoreWindowPaths.Clear();
            settings.RestoreWindows.Clear();
            settings.Save();

            var wm = App.WindowManager;
            var w1 = Track(wm.CreateNewWindow(tmpDir1) as MainWindow);
            var w2 = Track(wm.CreateNewWindow(tmpDir2) as MainWindow);
            ((IWorkbenchWindow)w1).SetWindowBounds(new RestoreWindowInfo { WorkspacePath = tmpDir1, X = 120, Y = 140, Width = 1100, Height = 720 });
            ((IWorkbenchWindow)w2).SetWindowBounds(new RestoreWindowInfo { WorkspacePath = tmpDir2, X = 180, Y = 200, Width = 1280, Height = 800 });

            wm.QuitApplication();
            Dispatcher.UIThread.RunJobs();

            var s2 = IdePersistentSettings.Load();
            Assert.Contains(Path.GetFullPath(tmpDir1), s2.RestoreWindowPaths);
            Assert.Contains(Path.GetFullPath(tmpDir2), s2.RestoreWindowPaths);
            Assert.Equal(2, s2.RestoreWindows.Count);
            Assert.Equal(1100, s2.RestoreWindows[0].Width);
            Assert.Equal(720, s2.RestoreWindows[0].Height);
            Assert.Equal(1280, s2.RestoreWindows[1].Width);
            Assert.Equal(800, s2.RestoreWindows[1].Height);

            // Launching with restore re-opens both windows with their folders and bounds
            Assert.Empty(wm.GetOpenWindows());
            ApplicationSession.ProcessArgs(new string[0], "", wm);
            var reopened = wm.GetOpenWindows().OfType<MainWindow>().ToList();
            foreach (var rw in reopened) Track(rw);
            Assert.Equal(2, reopened.Count);
            Assert.Equal(Path.GetFullPath(tmpDir1), reopened[0].WorkspaceRoot);
            Assert.Equal(Path.GetFullPath(tmpDir2), reopened[1].WorkspaceRoot);
            Assert.Equal(120, reopened[0].Position.X);
            Assert.Equal(140, reopened[0].Position.Y);
            Assert.Equal(1100, (int)reopened[0].Width);
            Assert.Equal(720, (int)reopened[0].Height);
            Assert.Equal(180, reopened[1].Position.X);
            Assert.Equal(200, reopened[1].Position.Y);
            Assert.Equal(1280, (int)reopened[1].Width);
            Assert.Equal(800, (int)reopened[1].Height);

            foreach (var rw in reopened) rw.Close();
        }
    }
}
