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
            var mockCtx = new MockContext();
            var failures = new List<string>();

            // Explicitly allowed collisions / reasons:
            var allowed = new HashSet<string> {
                "editor.action.revealDefinition", // shadowed by F12 developer tools or goToDeclaration
                "editor.action.referenceSearch.trigger", // shadowed by Shift+F12 find all references
                "workbench.action.findInFiles", // cmd+shift+f is global search
                "inlineChat.start", // cmd+i is inline AI
                "workbench.action.files.copyPathOfActiveFile" // cmd+k p is a chord which we split by space and dispatch partials!
            };

            foreach (var b in manager.Defaults)
            {
                if (string.IsNullOrEmpty(b.Key)) continue;
                bool executed = false;
                manager.BindAction(b.Command, () => { executed = true; });
                
                if (WhenParser.Evaluate(b.WhenParsed, mockCtx))
                {
                    if (allowed.Contains(b.Command))
                    {
                        continue; // explicitly allowed
                    }

                    string[] parts = b.Key.Split(' ');
                    foreach (string p in parts)
                    {
                        manager.Dispatch(p, mockCtx, s => { }, false);
                    }
                    if (!executed)
                    {
                        failures.Add($"{b.Command} on {b.Key}");
                    }
                }
            }
            Assert.True(failures.Count == 0, string.Join(", ", failures));
        }
        
        [AvaloniaFact]
        public void PaletteShortcutLabels_MatchRegistry()
        {
            DefaultCommands.Register();
            var w = new MainWindow(null);
            var manager = new KeybindingManager();
            var p = w.BuildAllPaletteCommands();
            int checkedItems = 0;
            foreach (var item in p)
            {
                if (!string.IsNullOrEmpty(item.Id))
                {
                    var b = manager.ActiveBindings.FirstOrDefault(x => x.Command == item.Id);
                    if (b != null && !string.IsNullOrEmpty(b.Key))
                    {
                        Assert.Equal(MacKeymap.FormatShortcut(b.Key), item.Shortcut);
                        checkedItems++;
                    }
                }
            }
            Assert.True(checkedItems >= 30, "Should check at least 30 palette items");
        }

        private class MockContext : IContextKeyService
        {
            public object GetValue(string key) => true; 
        }
    }
}
