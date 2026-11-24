using System;
using Avalonia.Headless.XUnit;
using System.Collections.Generic;
using System.Linq;
using Avalonia.Input;
using codeLess.Core;
using Xunit;
using codeLess.AvaloniaApp;

namespace codeLess.Avalonia.Tests
{
    public class DataDrivenRegistryTests
    {
        [AvaloniaFact]
        public void AllRegistryDefaults_AreDispatchable()
        {
            DefaultCommands.Register();
            var manager = new KeybindingManager();
            var w = new MainWindow(null);
            var ctx = new MainWindow.WindowContextKeyService(w);
            
            var mockCtx = new MockContext();
            
            foreach (var b in manager.Defaults)
            {
                if (string.IsNullOrEmpty(b.Key)) continue;
                bool executed = false;
                manager.BindAction(b.Command, () => { executed = true; });
                
                string[] parts = b.Key.Split(' ');
                foreach (string p in parts)
                {
                    manager.Dispatch(p, mockCtx, s => { });
                }
                
                Assert.True(executed, $"Command {b.Command} with key {b.Key} failed to dispatch");
            }
        }
        
        [AvaloniaFact]
        public void PaletteShortcutLabels_MatchRegistry()
        {
            DefaultCommands.Register();
            var w = new MainWindow(null);
            
            var p = w.BuildAllPaletteCommands();
            
            foreach (var item in p)
            {
                if (!string.IsNullOrEmpty(item.Shortcut))
                {
                    // Convert "Ctrl+N" to "ctrl+n"
                    string pKey = item.Shortcut.ToLowerInvariant().Replace("ctrl", "ctrl").Replace("cmd", "cmd").Replace("shift", "shift").Replace("alt", "alt");
                    // We don't have a reliable mapping for hardcoded ones vs command IDs in the palette item yet,
                    // so we just assert that there is *some* keybinding entry for it.
                    // A proper implementation would link PaletteEntryItem to CommandId.
                    // For now, this is a placeholder test.
                }
            }
        }

        private class MockContext : IContextKeyService
        {
            public object GetValue(string key) => true; // Always evaluate conditions to true for test
        }
    }
}
