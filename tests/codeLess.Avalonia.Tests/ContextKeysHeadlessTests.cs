using Avalonia.Threading;
using System;
using Avalonia.Headless.XUnit;
using Xunit;
using codeLess.Core;
using codeLess.AvaloniaApp;
using Avalonia.Controls;

namespace codeLess.Avalonia.Tests
{
    [Collection("SequentialWorkbench")]
    public class ContextKeysHeadlessTests
    {
        [AvaloniaFact]
        public void ContextKeys_ReturnsRealState()
        {
            var tmpFile = System.IO.Path.GetTempFileName() + ".cs";
            var w = new MainWindow(null);
            w.Show();
            try
            {
                var ctx = new MainWindow.WindowContextKeyService(w);

                Assert.False((bool)ctx.GetValue("editorhasselection"));
                Assert.False((bool)ctx.GetValue("findwidgetvisible"));
                Assert.False((bool)ctx.GetValue("terminalfocus"));
                Assert.False((bool)ctx.GetValue("suggestwidgetvisible"));
                Assert.False((bool)ctx.GetValue("inquickopen"));
                Assert.False((bool)ctx.GetValue("editorreadonly"));

                System.IO.File.WriteAllText(tmpFile, "class A {}");
                var tab = w.OpenFileInTab(tmpFile);
                tab.SelectionStartLine = 0; tab.SelectionStartCol = 0; tab.CursorLine = 0; tab.CursorCol = 5;

                Assert.True((bool)ctx.GetValue("editorhasselection"));
                Assert.False((bool)ctx.GetValue("editorreadonly"));

                tab.IsReadOnly = true;
                Assert.True((bool)ctx.GetValue("editorreadonly"));

                w.PrimaryEditor.ShowFindBar(false);
                Assert.True((bool)ctx.GetValue("findwidgetvisible"));

                w.OpenCommandPalette(">");
                Assert.True((bool)ctx.GetValue("inquickopen"));

                var ext = ctx.GetValue("resourceextname") as string;
                Assert.Equal(".cs", ext);

                var lang = ctx.GetValue("editorlangid") as string;
                Assert.Equal("cs", lang);

                var isMac = (bool)ctx.GetValue("ismac");
                var isWin = (bool)ctx.GetValue("iswindows");
                var isLinux = (bool)ctx.GetValue("islinux");
                Assert.True(isMac || isWin || isLinux);
            }
            finally
            {
                w.SuppressUnsavedPrompts = true;
                w.Close();
                try { if (System.IO.File.Exists(tmpFile)) System.IO.File.Delete(tmpFile); } catch { }
            }
        }

        [AvaloniaFact]
        public void Keybindings_TextBoxIgnoresEditorCommands()
        {
            DefaultCommands.Register();
            var tmpFile = System.IO.Path.GetTempFileName() + ".txt";
            var w = new MainWindow(null);
            w.Show();
            try
            {
                bool executed = false;
                w.GetType().GetMethod("InitializeKeybindings", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance).Invoke(w, null);
                var mgr = w.GetType().GetField("keybindings", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance).GetValue(w) as KeybindingManager;
                mgr.BindAction("editor.action.deleteLines", () => executed = true);

                System.IO.File.WriteAllText(tmpFile, "test");
                w.OpenFileInTab(tmpFile);

                // Focus canvas
                w.PrimaryEditor.Canvas.Focus();
                global::Avalonia.Threading.Dispatcher.UIThread.RunJobs();
                var keyService = new MainWindow.WindowContextKeyService(w);
                Assert.True((bool)keyService.GetValue("editorTextFocus"));

                string key = MacKeymap.IsMacOS ? "cmd+shift+k" : "ctrl+shift+k";
                mgr.Dispatch(key, keyService, _ => { }, false);
                Assert.True(executed);

                // Focus textbox
                executed = false;
                var tb = new TextBox();
                ((global::Avalonia.Controls.Panel)w.Content).Children.Add(tb);
                tb.Focus();
                global::Avalonia.Threading.Dispatcher.UIThread.RunJobs();

                Assert.False((bool)keyService.GetValue("editorTextFocus"));
                mgr.Dispatch(key, keyService, _ => { }, false);
                Assert.False(executed);
            }
            finally
            {
                w.SuppressUnsavedPrompts = true;
                w.Close();
                try { if (System.IO.File.Exists(tmpFile)) System.IO.File.Delete(tmpFile); } catch { }
            }
        }
    }
}
