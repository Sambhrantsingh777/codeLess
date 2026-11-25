using System;
using Avalonia.Headless.XUnit;
using Xunit;
using codeLess.Core;
using codeLess.AvaloniaApp;
using Avalonia.Controls;

namespace codeLess.Avalonia.Tests
{
    public class ContextKeysHeadlessTests
    {
        [AvaloniaFact]
        public void Keybindings_TextBoxIgnoresEditorCommands()
        {
            DefaultCommands.Register();
            var w = new MainWindow(null);
            
            bool executed = false;
            w.GetType().GetMethod("InitializeKeybindings", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance).Invoke(w, null);
            var mgr = w.GetType().GetField("keybindings", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance).GetValue(w) as KeybindingManager;
            mgr.BindAction("editor.action.deleteLines", () => executed = true);
            
            // Focus canvas
            w.PrimaryEditor.Canvas.Focus();
            var keyService = new MainWindow.WindowContextKeyService(w);
            Assert.True((bool)keyService.GetValue("editorTextFocus"));
            
            string key = MacKeymap.IsMacOS ? "cmd+shift+k" : "ctrl+shift+k";
            mgr.Dispatch(key, keyService, _ => {}, false);
            Assert.True(executed);
            
            // Focus textbox
            executed = false;
            var tb = new TextBox();
            ((global::Avalonia.Controls.Panel)w.Content).Children.Add(tb);
            tb.Focus();
            
            Assert.False((bool)keyService.GetValue("editorTextFocus"));
            mgr.Dispatch(key, keyService, _ => {}, false);
            Assert.False(executed);
        }
    }
}
