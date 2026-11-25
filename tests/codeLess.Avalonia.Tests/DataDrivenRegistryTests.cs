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
    [Collection("SequentialWorkbench")]
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
                "editor.action.revealDefinition", // shadowed by goToDeclaration on F12
                "editor.action.referenceSearch.trigger", // shadowed by goToReferences on Shift+F12
                "workbench.action.findInFiles", // shadowed by workbench.view.search on Cmd/Ctrl+Shift+F
                "inlineChat.start", // shadowed by editor.action.inlineAiBar on Cmd/Ctrl+I
                "workbench.action.files.copyPathOfActiveFile" // shadowed by copyFilePath on Cmd/Ctrl+K P
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
                        continue;
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
            try
            {
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
            finally
            {
                w.SuppressUnsavedPrompts = true;
                w.Close();
            }
        }

        [Fact]
        public void KeybindingsDocMatchesCode()
        {
            Assert.Equal("3.4.0", MainWindow.CurrentAppVersion);

            string docPath = System.IO.Path.GetFullPath(System.IO.Path.Combine(AppContext.BaseDirectory, "../../../../../docs/KEYBINDINGS.md"));
            if (!System.IO.File.Exists(docPath))
            {
                docPath = System.IO.Path.GetFullPath(System.IO.Path.Combine(Environment.CurrentDirectory, "docs/KEYBINDINGS.md"));
            }
            if (!System.IO.File.Exists(docPath))
            {
                docPath = System.IO.Path.GetFullPath(System.IO.Path.Combine(Environment.CurrentDirectory, "../../docs/KEYBINDINGS.md"));
            }
            Assert.True(System.IO.File.Exists(docPath), "docs/KEYBINDINGS.md must exist at " + docPath);

            string expected = DefaultCommands.FormatKeybindingsMarkdown().Replace("\r\n", "\n").Trim();
            string actual = System.IO.File.ReadAllText(docPath).Replace("\r\n", "\n").Trim();
            Assert.Equal(expected, actual);
        }

        private class MockContext : IContextKeyService
        {
            public object GetValue(string key) => true;
        }
    }
}
