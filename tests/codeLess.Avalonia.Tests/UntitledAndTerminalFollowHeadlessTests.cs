using System;
using System.IO;
using System.Linq;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using codeLess.AvaloniaApp;
using codeLess.Studio;
using Xunit;

namespace codeLess.Avalonia.Tests
{
    [Collection("SequentialWorkbench")]
    public class UntitledAndTerminalFollowHeadlessTests : IDisposable
    {
        private readonly string _settingsFilePath;
        private readonly string _settingsBackup;
        private readonly string _root;

        public UntitledAndTerminalFollowHeadlessTests()
        {
            _settingsFilePath = IdePersistentSettings.GetSettingsFilePath();
            _settingsBackup = File.Exists(_settingsFilePath) ? File.ReadAllText(_settingsFilePath) : null;
            _root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "codeLess_Untitled_" + Guid.NewGuid().ToString("N").Substring(0, 8)));
            Directory.CreateDirectory(_root);
            TestTrust.Trust(_root);
            UntitledFileDefaults.ResetCache();
        }

        public void Dispose()
        {
            try
            {
                if (_settingsBackup != null) File.WriteAllText(_settingsFilePath, _settingsBackup);
                else if (File.Exists(_settingsFilePath)) File.Delete(_settingsFilePath);
            }
            catch { }
            UntitledFileDefaults.ResetCache();
            try { Directory.Delete(_root, true); } catch { }
        }

        private string Sub(string name)
        {
            string d = Path.Combine(_root, name);
            Directory.CreateDirectory(d);
            TestTrust.Trust(d);
            return d;
        }

        [AvaloniaFact]
        public void New_File_Follows_The_Workspace_Language_And_Closes_Without_A_Prompt_When_Empty()
        {
            string ws = Sub("pyws");
            File.WriteAllText(Path.Combine(ws, "main.py"), "print(1)\n");
            File.WriteAllText(Path.Combine(ws, "util.py"), "x = 1\n");
            var w = new MainWindow(ws);
            w.Show();
            try
            {
                int prompts = 0;
                w.UnsavedPromptOverride = d => { prompts++; return UnsavedPromptResult.Cancel; };
                var tab = w.CreateNewUntitledTab();
                Assert.Equal("Untitled-1", tab.FileName);
                Assert.Equal("py", tab.Language);
                Assert.True(tab.IsUntitled);
                Assert.Equal("Untitled-1.py", UntitledFileDefaults.SuggestSaveFileName(tab, ws, null));

                // typed and deleted again: still nothing to save
                tab.IsDirty = true;
                Assert.True(w.CloseTab(tab));
                Assert.Equal(0, prompts);
                Assert.DoesNotContain(tab, w.OpenTabs);

                // with text it asks
                var tab2 = w.CreateNewUntitledTab();
                tab2.Content = "import os\n"; tab2.SyncLinesFromContent(); tab2.IsDirty = true;
                Assert.False(w.CloseTab(tab2));
                Assert.Equal(1, prompts);
                Assert.Contains(tab2, w.OpenTabs);
            }
            finally { w.SuppressUnsavedPrompts = true; w.Close(); }
        }

        [AvaloniaFact]
        public void Open_Folder_Moves_Idle_Terminals_And_Leaves_Busy_Ones()
        {
            string a = Sub("first"), b = Sub("second");
            var w = new MainWindow(a);
            w.Show();
            try
            {
                int before = w.TerminalSessions.Count;
                var idle = w.CreateNewTerminalSession();
                var busy = w.CreateNewTerminalSession();
                Assert.Equal(a, idle.Control.WorkingDirectory);
                w.TerminalIdleOverride = t => !ReferenceEquals(t, busy);
                w.LoadWorkspace(b);
                Assert.Equal(b, idle.Control.WorkingDirectory);
                Assert.Equal(a, busy.Control.WorkingDirectory);
                Assert.Equal(before + 2, w.TerminalSessions.Count); // nothing was closed

                var fresh = w.CreateNewTerminalSession();
                Assert.Equal(b, fresh.Control.WorkingDirectory);
            }
            finally { w.SuppressUnsavedPrompts = true; w.Close(); }
        }
            [AvaloniaFact]
        public void Mac_Control_G_Opens_Go_To_Line_And_Cmd_G_Is_Find_Next()
        {
            string ws = Sub("gotoline");
            string f = Path.Combine(ws, "a.txt");
            File.WriteAllText(f, "foo\nbar\nfoo\n");
            MacKeymap.ForceMacKeymapOverride = true;
            var w = new MainWindow(ws) { Width = 1200, Height = 800 };
            w.Show();
            try
            {
                w.OpenFileInTab(f, preview: false);
                Dispatcher.UIThread.RunJobs();
                w.PrimaryEditor.Canvas.Focus();
                w.KeyPress(Key.G, RawInputModifiers.Meta, PhysicalKey.G, "g"); // Cmd+G = Find Next, not Go to Line
                Dispatcher.UIThread.RunJobs();
                Assert.False(w.IsCommandPaletteOpen);
                w.KeyPress(Key.G, RawInputModifiers.Control, PhysicalKey.G, "g"); // Control+G = Go to Line (VS Code mac)
                Dispatcher.UIThread.RunJobs();
                Assert.True(w.IsCommandPaletteOpen);
            }
            finally { MacKeymap.ForceMacKeymapOverride = null; w.SuppressUnsavedPrompts = true; w.Close(); }
        }
            [AvaloniaFact]
        public void Welcome_Tab_Closes_Even_With_No_Files_And_Leaves_The_Empty_Editor_Watermark()
        {
            string ws = Sub("welcome");
            string f = Path.Combine(ws, "a.txt");
            File.WriteAllText(f, "a\n");
            var w = new MainWindow(ws) { Width = 1200, Height = 800 };
            w.Show();
            try
            {
                Dispatcher.UIThread.RunJobs();
                Assert.Empty(w.OpenTabs); // opening a folder opens no file by itself (VS Code)
                Assert.True(w.IsWelcomeTabOpen);
                Assert.True(w.PrimaryEditor.IsWelcomePageVisible);

                w.CloseWelcomeTab(); // no files open: it used to stay forever
                Dispatcher.UIThread.RunJobs();
                Assert.False(w.IsWelcomeTabOpen);
                Assert.False(w.PrimaryEditor.IsWelcomePageVisible);
                Assert.True(w.PrimaryEditor.IsEmptyWatermarkVisible);
                Assert.DoesNotContain("Welcome", w.Title);

                var tab = w.OpenFileInTab(f, preview: false);
                Assert.False(w.PrimaryEditor.IsEmptyWatermarkVisible);
                w.CloseTab(tab);
                Dispatcher.UIThread.RunJobs();
                Assert.False(w.IsWelcomeTabOpen); // closing the last file doesn't bring Welcome back
                Assert.True(w.PrimaryEditor.IsEmptyWatermarkVisible);

                w.OpenWelcomeTab(); // Help > Welcome
                Assert.True(w.IsWelcomeTabOpen);
                Assert.True(w.PrimaryEditor.IsWelcomePageVisible);
                Assert.False(w.PrimaryEditor.IsEmptyWatermarkVisible);
            }
            finally { w.SuppressUnsavedPrompts = true; w.Close(); }
        }
            [AvaloniaFact]
        public void Find_Box_Up_Arrow_Recalls_Previous_Searches()
        {
            string ws = Sub("findhist");
            string f = Path.Combine(ws, "a.txt");
            File.WriteAllText(f, "foo bar baz\n");
            FindHistoryStore.DisablePersistence = true;
            FindHistoryStore.ResetForTests();
            var w = new MainWindow(ws) { Width = 1200, Height = 800 };
            w.Show();
            try
            {
                w.OpenFileInTab(f, preview: false);
                Dispatcher.UIThread.RunJobs();
                w.PrimaryEditor.ShowFindBar(false);
                var box = w.PrimaryEditor.FindInputBox;
                box.Text = "foo";
                w.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null);
                box.Text = "bar";
                w.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null);
                box.Text = "ba"; // typing something new
                w.KeyPress(Key.Up, RawInputModifiers.None, PhysicalKey.ArrowUp, null);
                Assert.Equal("bar", box.Text);
                w.KeyPress(Key.Up, RawInputModifiers.None, PhysicalKey.ArrowUp, null);
                Assert.Equal("foo", box.Text);
                w.KeyPress(Key.Down, RawInputModifiers.None, PhysicalKey.ArrowDown, null);
                w.KeyPress(Key.Down, RawInputModifiers.None, PhysicalKey.ArrowDown, null);
                Assert.Equal("ba", box.Text); // back to what was being typed
            }
            finally
            {
                FindHistoryStore.ResetForTests();
                FindHistoryStore.DisablePersistence = false;
                w.SuppressUnsavedPrompts = true; w.Close();
            }
        }
    }
}
