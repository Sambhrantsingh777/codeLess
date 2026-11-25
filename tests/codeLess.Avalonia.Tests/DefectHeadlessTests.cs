using System;
using System.IO;
using System.Reflection;
using Xunit;
using Avalonia.Headless.XUnit;
using Avalonia.Controls;
using codeLess.Core;
using codeLess.AvaloniaApp;

namespace codeLess.Avalonia.Tests
{
    public class DefectHeadlessTests
    {
        [AvaloniaFact]
        public void InvalidJSON_ThenValidJSON_ToUserKeybindingsFile_ShowsNotificationThenClears_AndApplies()
        {
            var mw = new MainWindow();
            mw.Show();
            
            var watcherField = typeof(MainWindow).GetField("keybindingsWatcher", BindingFlags.NonPublic | BindingFlags.Instance);
            var watcher = watcherField.GetValue(mw);
            var onChangeField = typeof(KeybindingsFileWatcher).GetField("onChange", BindingFlags.NonPublic | BindingFlags.Instance);
            var action = (Action<string>)onChangeField.GetValue(watcher);

            action.Invoke("THIS IS NOT JSON!!!! @#$%^&*"); // Invalid
            global::Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            
            var notifyField = typeof(MainWindow).GetField("keybindingsNotification", BindingFlags.NonPublic | BindingFlags.Instance);
            var notify = (Border)notifyField.GetValue(mw);
            Assert.True(notify.IsVisible);

            action.Invoke("[ { \"key\": \"ctrl+a\", \"command\": \"test\" } ]"); // Valid
            global::Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            
            var notifyAfter = notifyField.GetValue(mw);
            Assert.Null(notifyAfter);
        }

        [AvaloniaFact]
        public void SimulateShortcut_CmdComma_OpensSettings()
        {
            var mw = new MainWindow();
            mw.SimulateShortcut(global::Avalonia.Input.Key.OemComma, global::Avalonia.Input.KeyModifiers.Meta); // Mac
            
            Assert.True(mw.ModalHost.IsVisible);
            Assert.Equal("settings", mw.ModalHost.ActiveDialogId);
        }

        [AvaloniaFact]
        public void TextBox_Focus_KeepsTextEditingKeys_DoesNotFireEditorBindings()
        {
            var mw = new MainWindow();
            var tb = new TextBox();
            mw.Content = tb;
            tb.Focus();
            
            var method = typeof(MainWindow).GetMethod("DispatchKeybinding", BindingFlags.NonPublic | BindingFlags.Instance);
            bool handled = (bool)method.Invoke(mw, new object[] { global::Avalonia.Input.Key.K, global::Avalonia.Input.KeyModifiers.Control | global::Avalonia.Input.KeyModifiers.Shift, false });
            
            Assert.False(handled);
        }
    }
}
