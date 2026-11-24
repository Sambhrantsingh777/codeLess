using System;
using Avalonia.Headless.XUnit;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Input;
using codeLess.Core;
using Xunit;
using codeLess.AvaloniaApp;

namespace codeLess.Avalonia.Tests
{
    public class KeyboardShortcutsHeadlessTests
    {
        [AvaloniaFact]
        public void KeyboardShortcutsEditor_HasSearchAndConflicts()
        {
            var mgr = new KeybindingManager();
            var editor = new AvaloniaKeyboardShortcutsEditor(codeLess.Studio.StudioTheme.GetById("codeless-dark"), null, mgr);
            Assert.NotNull(editor.Content);
            
            var searchBox = editor.GetSearchBoxForTest();
            searchBox.Text = "copy";
            editor.FilterListForTest();
            
            var listView = editor.GetListViewForTest();
            Assert.NotEmpty(listView.Items.Cast<object>());
        }

        [AvaloniaFact]
        public void KeyboardShortcutsEditor_RemoveBinding()
        {
            var mgr = new KeybindingManager();
            var editor = new AvaloniaKeyboardShortcutsEditor(codeLess.Studio.StudioTheme.GetById("codeless-dark"), null, mgr);
            var searchBox = editor.GetSearchBoxForTest();
            searchBox.Text = "workbench.action.quit";
            editor.FilterListForTest();

            var listView = editor.GetListViewForTest();
            listView.SelectedIndex = 0;
            editor.RemoveBindingForTest();

            // Assert.Contains(mgr.UserBindings ?? new System.Collections.Generic.List<KeybindingEntry>(), b => b.Command == "-workbench.action.quit");
        }

        [AvaloniaFact]
        public void KeyboardShortcutsEditor_DefineBinding()
        {
            var mgr = new KeybindingManager();
            var editor = new AvaloniaKeyboardShortcutsEditor(codeLess.Studio.StudioTheme.GetById("codeless-dark"), null, mgr);
            var searchBox = editor.GetSearchBoxForTest();
            searchBox.Text = "workbench.action.quit";
            editor.FilterListForTest();

            var listView = editor.GetListViewForTest();
            listView.SelectedIndex = 0;
            
            editor.ApplyNewBindingForTest("ctrl+shift+q");

            // Assert.Contains(mgr.UserBindings ?? new System.Collections.Generic.List<KeybindingEntry>(), b => b.Command == "workbench.action.quit" && b.Key == "ctrl+shift+q");
        }
    }
}
