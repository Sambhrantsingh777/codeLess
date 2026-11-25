using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Xunit;
using codeLess.Studio;
using Avalonia.Threading;
using Avalonia.Headless.XUnit;

namespace codeLess.AvaloniaApp.Tests
{
    [Collection("SettingsTests")]
    public class MultiWindowHeadlessTests
    {
        [AvaloniaFact]
        public void WindowTitle_Format()
        {
            string tmpDir1 = Path.Combine(Path.GetTempPath(), "mw-test1-" + Guid.NewGuid().ToString("N"));
            string tmpDir2 = Path.Combine(Path.GetTempPath(), "mw-test2-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tmpDir1);
            Directory.CreateDirectory(tmpDir2);
            File.WriteAllText(Path.Combine(tmpDir1, "test.txt"), "hello");
            File.WriteAllText(Path.Combine(tmpDir2, "other.txt"), "world");
            
            var w1 = new MainWindow(tmpDir1);
            var w2 = new MainWindow(tmpDir2);
            w1.Show();
            w2.Show();
            
            ((IWorkbenchWindow)w1).OpenFileInTab(Path.Combine(tmpDir1, "test.txt"));
            ((IWorkbenchWindow)w2).OpenFileInTab(Path.Combine(tmpDir2, "other.txt"));
            Dispatcher.UIThread.RunJobs();
            
            Assert.Equal("test.txt — " + Path.GetFileName(tmpDir1) + " — codeLess", w1.Title);
            Assert.Equal("other.txt — " + Path.GetFileName(tmpDir2) + " — codeLess", w2.Title);
            
            w1.Close();
            w2.Close();
            try { Directory.Delete(tmpDir1, true); } catch { }
            try { Directory.Delete(tmpDir2, true); } catch { }
        }

        [AvaloniaFact]
        public void NewWindow_OpensEmpty_Cascaded()
        {
            string tmpDir1 = Path.Combine(Path.GetTempPath(), "mw-test1-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tmpDir1);

            var w1 = new MainWindow(tmpDir1);
            w1.Show();
            
            global::codeLess.AvaloniaApp.App.TestOpenWindows = new[] { w1 };
            
            var w2 = new MainWindow(null);
            w2.Show();
            global::codeLess.AvaloniaApp.App.TestOpenWindows = new[] { w1, w2 };
            
            var ww2 = (IWorkbenchWindow)w2;
            var ww1 = (IWorkbenchWindow)w1;
            
            Assert.True(string.IsNullOrEmpty(ww2.WorkspaceRoot));
            Assert.Equal(tmpDir1, ww1.WorkspaceRoot);
            
            var windows = global::codeLess.AvaloniaApp.App.TestOpenWindows.ToList();
            Assert.Equal(2, windows.Count);
            
            w2.Close();
            w1.Close();
            global::codeLess.AvaloniaApp.App.TestOpenWindows = null;
            
            try { Directory.Delete(tmpDir1, true); } catch { }
        }

        [AvaloniaFact]
        public void CloseWindow_DisposesOwnResources()
        {
            string tmpDir1 = Path.Combine(Path.GetTempPath(), "mw-test1-" + Guid.NewGuid().ToString("N"));
            string tmpDir2 = Path.Combine(Path.GetTempPath(), "mw-test2-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tmpDir1);
            Directory.CreateDirectory(tmpDir2);

            var w1 = new MainWindow(tmpDir1);
            var w2 = new MainWindow(tmpDir2);
            w1.Show();
            w2.Show();
            
            w1.Close();
            Dispatcher.UIThread.RunJobs();
            
            System.Threading.Thread.Sleep(50);
            
            Assert.True(w1.TestCheckDisposed());
            Assert.False(w2.TestCheckDisposed());

            w2.Close();
            
            try { Directory.Delete(tmpDir1, true); } catch { }
            try { Directory.Delete(tmpDir2, true); } catch { }
        }
        
        [AvaloniaFact]
        public void SettingsChange_Propagates()
        {
            

            string tmpDir1 = Path.Combine(Path.GetTempPath(), "mw-test1-" + Guid.NewGuid().ToString("N"));
            string tmpDir2 = Path.Combine(Path.GetTempPath(), "mw-test2-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tmpDir1);
            Directory.CreateDirectory(tmpDir2);

            var w1 = new MainWindow(tmpDir1);
            var w2 = new MainWindow(tmpDir2);
            w1.Show();
            w2.Show();
            
            float oldSize = w2.GetEditorFontSizeForTests();
            float newSize = oldSize == 20f ? 22f : 20f;
            
            var settings = IdePersistentSettings.Load();
            settings.EditorFontSize = newSize;
            settings.Save();
            
            codeLess.Studio.IdeSettingsWatcher.ForceDebounceTickForTests();
            Dispatcher.UIThread.RunJobs();
            
            Assert.Equal(newSize, w2.GetEditorFontSizeForTests());
            
            w1.Close();
            w2.Close();
            
            try { Directory.Delete(tmpDir1, true); } catch { }
            try { Directory.Delete(tmpDir2, true); } catch { }
        }

        [AvaloniaFact]
        public void WindowMenu_ItemsMatchOpenWindows()
        {
            if (!OperatingSystem.IsMacOS()) return;

            string tmpDir1 = Path.Combine(Path.GetTempPath(), "mw-test1-" + Guid.NewGuid().ToString("N"));
            string tmpDir2 = Path.Combine(Path.GetTempPath(), "mw-test2-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tmpDir1);
            Directory.CreateDirectory(tmpDir2);

            var w1 = new MainWindow(tmpDir1);
            var w2 = new MainWindow(tmpDir2);
            w1.Show();
            w2.Show();
            
            global::codeLess.AvaloniaApp.App.TestOpenWindows = new[] { w1, w2 };
            var topMenu = w1.TopMenuControl;
            var windowMenu = System.Linq.Enumerable.FirstOrDefault(System.Linq.Enumerable.Cast<global::Avalonia.Controls.MenuItem>(topMenu.ItemsSource), m => 
                m.Header?.ToString().Replace("_", "") == "Window");
                
            Assert.NotNull(windowMenu);
            
            windowMenu.RaiseEvent(new global::Avalonia.Interactivity.RoutedEventArgs(global::Avalonia.Controls.MenuItem.SubmenuOpenedEvent));
            
            Dispatcher.UIThread.RunJobs();
            
            var items = System.Linq.Enumerable.ToList(System.Linq.Enumerable.Cast<object>(windowMenu.ItemsSource));
            
            bool w1Found = false;
            bool w2Found = false;
            foreach(var it in items) {
                if (it is global::Avalonia.Controls.MenuItem mi) {
                    var tb = mi.Header as global::Avalonia.Controls.TextBlock;
                    if (tb != null) {
                        if (tb.Text == w1.Title) w1Found = true;
                        if (tb.Text == w2.Title) w2Found = true;
                    }
                }
            }
            Assert.True(w1Found, "w1 not found in menu items");
            Assert.True(w2Found, "w2 not found in menu items");
            
            w1.Close();
            w2.Close();
            global::codeLess.AvaloniaApp.App.TestOpenWindows = null;
            
            try { Directory.Delete(tmpDir1, true); } catch { }
            try { Directory.Delete(tmpDir2, true); } catch { }
        }

        [AvaloniaFact]
        public void RestoreState_SingleClose_RemovesFromPaths()
        {
            string tmpDir1 = Path.Combine(Path.GetTempPath(), "mw-rs1-" + Guid.NewGuid().ToString("N"));
            string tmpDir2 = Path.Combine(Path.GetTempPath(), "mw-rs2-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tmpDir1);
            Directory.CreateDirectory(tmpDir2);

            

            Environment.SetEnvironmentVariable("CODELESS_SETTINGS_DIR", Path.Combine(Path.GetTempPath(), "settings-rs1-" + Guid.NewGuid().ToString("N")));
            var settings = IdePersistentSettings.Load();
            settings.WindowRestoreWindows = "all";
            settings.RestoreWindowPaths.Clear();
            settings.RestoreWindowPaths.Add(tmpDir1);
            settings.RestoreWindowPaths.Add(tmpDir2);
            settings.Save();

            var desktop = new global::Avalonia.Controls.ApplicationLifetimes.ClassicDesktopStyleApplicationLifetime();
            var wm = new AvaloniaWindowManager(desktop);
            var w1 = wm.CreateNewWindow(tmpDir1);
            var w2 = wm.CreateNewWindow(tmpDir2);
            global::codeLess.AvaloniaApp.App.TestOpenWindows = new[] { w2 };
            
            ((global::Avalonia.Controls.Window)w1).Close();
            Dispatcher.UIThread.RunJobs();

            var s2 = IdePersistentSettings.Load();
            Assert.DoesNotContain(tmpDir1, s2.RestoreWindowPaths);
            Assert.Contains(tmpDir2, s2.RestoreWindowPaths);
            
            ((global::Avalonia.Controls.Window)w2).Close();
            global::codeLess.AvaloniaApp.App.TestOpenWindows = null;
            Dispatcher.UIThread.RunJobs();
            try { Directory.Delete(tmpDir1, true); } catch { }
            try { Directory.Delete(tmpDir2, true); } catch { }
        }

        [AvaloniaFact]
        public void RestoreState_Quit_SavesAllPaths()
        {
            string tmpDir1 = Path.Combine(Path.GetTempPath(), "mw-rs3-" + Guid.NewGuid().ToString("N"));
            string tmpDir2 = Path.Combine(Path.GetTempPath(), "mw-rs4-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tmpDir1);
            Directory.CreateDirectory(tmpDir2);

            

            Environment.SetEnvironmentVariable("CODELESS_SETTINGS_DIR", Path.Combine(Path.GetTempPath(), "settings-rs2-" + Guid.NewGuid().ToString("N")));
            var settings = IdePersistentSettings.Load();
            settings.WindowRestoreWindows = "all";
            settings.RestoreWindowPaths.Clear();
            settings.Save();

            var desktop = new global::Avalonia.Controls.ApplicationLifetimes.ClassicDesktopStyleApplicationLifetime();
            var wm = new AvaloniaWindowManager(desktop);
            var w1 = wm.CreateNewWindow(tmpDir1);
            var w2 = wm.CreateNewWindow(tmpDir2);
            global::codeLess.AvaloniaApp.App.TestOpenWindows = new[] { w1, w2 };
            
            wm.QuitApplication();

            var s2 = IdePersistentSettings.Load();
            Assert.Contains(tmpDir1, s2.RestoreWindowPaths);
            Assert.Contains(tmpDir2, s2.RestoreWindowPaths);
            
            ((global::Avalonia.Controls.Window)w1).Close();
            ((global::Avalonia.Controls.Window)w2).Close();
            global::codeLess.AvaloniaApp.App.TestOpenWindows = null;
            try { Directory.Delete(tmpDir1, true); } catch { }
            try { Directory.Delete(tmpDir2, true); } catch { }
        }
    }
}
