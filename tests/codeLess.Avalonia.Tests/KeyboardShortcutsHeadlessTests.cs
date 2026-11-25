using System;
using Avalonia.Headless.XUnit;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Input;
using codeLess.Core;
using Xunit;
using codeLess.AvaloniaApp;
using System.Collections.Generic;

namespace codeLess.Avalonia.Tests
{
    public class KeyboardShortcutsHeadlessTests
    {
        [AvaloniaFact]
        public void KeyboardShortcutsEditor_HasSearchAndConflicts()
        {
            DefaultCommands.Register();
            typeof(KeybindingManager).GetField("cachedDefaults", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static).SetValue(null, null);
            var mgr = new KeybindingManager();
            var editor = new AvaloniaKeyboardShortcutsEditor(codeLess.Studio.StudioTheme.GetById("codeless-dark"), null, mgr);
            
            var searchBox = editor.GetSearchBoxForTest();
            searchBox.Text = "copy";
            editor.FilterListForTest();
            
            var listView = editor.GetListViewForTest();
            var items = listView.ItemsSource.Cast<ListBoxItem>().ToList();
            Assert.NotEmpty(items);
            foreach (var item in items) {
                var e = item.Tag;
                var cmdType = e.GetType();
                string cmd = (string)cmdType.GetProperty("Command").GetValue(e);
                string title = (string)cmdType.GetProperty("Title").GetValue(e);
                string key = (string)cmdType.GetProperty("Key").GetValue(e);
                
                bool match = (cmd != null && cmd.ToLowerInvariant().Contains("copy")) || 
                             (title != null && title.ToLowerInvariant().Contains("copy")) || 
                             (key != null && key.ToLowerInvariant().Contains("copy"));
                Assert.True(match, "Search results should only contain rows matching the query");
            }

            // Test conflict
            editor.ApplyNewBindingForTest("ctrl+f");
            items = listView.ItemsSource.Cast<ListBoxItem>().ToList();
            
            // Should have a conflict for ctrl+f now
            bool foundConflict = false;
            foreach (var item in items) {
                var content = item.Content as global::Avalonia.Controls.Grid;
                var txtKey = content.Children.OfType<global::Avalonia.Controls.TextBlock>().ElementAt(1);
                if (txtKey.Text.Contains("commands")) {
                    foundConflict = true;
                    Assert.Equal(global::Avalonia.Media.Brushes.Orange, txtKey.Foreground);
                }
            }
            Assert.True(foundConflict, "Conflict indicator missing");
        }

        [AvaloniaFact]
        public void KeyboardShortcutsEditor_RemoveBinding()
        {
            DefaultCommands.Register();
            typeof(KeybindingManager).GetField("cachedDefaults", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static).SetValue(null, null);
            var mgr = new KeybindingManager();
            var editor = new AvaloniaKeyboardShortcutsEditor(codeLess.Studio.StudioTheme.GetById("codeless-dark"), null, mgr);
            var searchBox = editor.GetSearchBoxForTest();
            searchBox.Text = "editor.action.clipboardCopyAction";
            editor.FilterListForTest();
            var listView = editor.GetListViewForTest();
            var itemsSource = listView.ItemsSource.Cast<ListBoxItem>().ToList();
            Assert.NotEmpty(itemsSource);
            listView.SelectedItem = itemsSource[0];
            editor.RemoveBindingForTest();
            
            string path = codeLess.Studio.IdePersistentSettings.GetKeybindingsFilePath();
            Assert.True(System.IO.File.Exists(path), "File should be created");
            string content = System.IO.File.ReadAllText(path);
            var err = mgr.LoadUserBindings(content);
            Assert.Null(err);

            Assert.Contains(mgr.UserBindings ?? new List<KeybindingEntry>(), b => b.Command == "-editor.action.clipboardCopyAction");
        }

        [AvaloniaFact]
        public void KeyboardShortcutsEditor_DefineBinding()
        {
            DefaultCommands.Register();
            typeof(KeybindingManager).GetField("cachedDefaults", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static).SetValue(null, null);
            var mgr = new KeybindingManager();
            var editor = new AvaloniaKeyboardShortcutsEditor(codeLess.Studio.StudioTheme.GetById("codeless-dark"), null, mgr);
            var searchBox = editor.GetSearchBoxForTest();
            searchBox.Text = "editor.action.clipboardCopyAction";
            editor.FilterListForTest();
            var listView = editor.GetListViewForTest();
            var itemsSource = listView.ItemsSource.Cast<ListBoxItem>().ToList();
            Assert.NotEmpty(itemsSource);
            listView.SelectedItem = itemsSource[0];
            
            editor.ApplyNewBindingForTest("ctrl+shift+q");
            
            string path = codeLess.Studio.IdePersistentSettings.GetKeybindingsFilePath();
            string content = System.IO.File.ReadAllText(path);
            mgr.LoadUserBindings(content);

            Assert.Contains(mgr.UserBindings ?? new List<KeybindingEntry>(), b => b.Command == "editor.action.clipboardCopyAction" && b.Key == "ctrl+shift+q");
        }
    }
}
