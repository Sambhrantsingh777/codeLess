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
            Assert.NotEmpty(listView.ItemsSource.Cast<object>());
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
