using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using codeLess.Core;
using codeLess.Studio;

namespace codeLess.AvaloniaApp
{
    public class AvaloniaKeyboardShortcutsEditor : UserControl
    {
        private TextBox searchBox;
        private ListBox listView;
        private StudioTheme currentTheme;
        private List<KeybindingEntry> allEntries = new List<KeybindingEntry>();
        private Action openJsonAction;

        public AvaloniaKeyboardShortcutsEditor(StudioTheme theme, Action openJson)
        {
            this.currentTheme = theme;
            this.openJsonAction = openJson;
            BuildUI();
            RefreshData();
        }

        private void BuildUI()
        {
            var root = new DockPanel { Margin = new Thickness(16) };

            var topBar = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Margin = new Thickness(0, 0, 0, 16) };
            DockPanel.SetDock(topBar, Dock.Top);

            searchBox = new TextBox { Width = 300, Watermark = "Search (e.g. key:\"ctrl+c\" or copy)" };
            searchBox.KeyUp += (s, e) => FilterList();
            
            var btnJson = new Button { Content = "Open Keyboard Shortcuts (JSON)" };
            btnJson.Click += (s, e) => {
                
                openJsonAction?.Invoke();
            };

            topBar.Children.Add(searchBox);
            topBar.Children.Add(btnJson);
            root.Children.Add(topBar);

            listView = new ListBox
            {
                Background = Brushes.Transparent,
                BorderThickness = new Thickness(0),
                SelectionMode = SelectionMode.Single
            };
            listView.DoubleTapped += (s, e) => DefineBinding();
            listView.KeyUp += (s, e) => {
                if (e.Key == Key.Enter) DefineBinding();
                if (e.Key == Key.Delete || e.Key == Key.Back) RemoveBinding();
            };
            
            var sv = new ScrollViewer { Content = listView, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled };
            root.Children.Add(sv);

            this.Content = root;
        }

        private void RefreshData()
        {
            allEntries.Clear();
            var defaults = new KeybindingManager().Defaults; // dirty hack to get defaults
            allEntries.AddRange(defaults);
            FilterList();
        }

        private void FilterList()
        {
            string q = searchBox.Text?.ToLowerInvariant() ?? "";
            var items = new List<ListBoxItem>();
            
            foreach (var e in allEntries)
            {
                var def = CommandRegistry.GetCommand(e.Command);
                string title = def?.Title ?? e.Command;
                if (!string.IsNullOrEmpty(q) && !title.ToLowerInvariant().Contains(q) && !e.Command.ToLowerInvariant().Contains(q) && !(e.Key ?? "").Contains(q))
                    continue;

                var grid = new Grid
                {
                    ColumnDefinitions = new ColumnDefinitions("2*, 1*, 1*")
                };

                var txtCommand = new TextBlock { Text = title, VerticalAlignment = VerticalAlignment.Center };
                var txtKey = new TextBlock { Text = e.Key, VerticalAlignment = VerticalAlignment.Center, Foreground = Brushes.LightGray };
                var txtWhen = new TextBlock { Text = e.When, VerticalAlignment = VerticalAlignment.Center, Foreground = Brushes.Gray };

                Grid.SetColumn(txtCommand, 0);
                Grid.SetColumn(txtKey, 1);
                Grid.SetColumn(txtWhen, 2);

                grid.Children.Add(txtCommand);
                grid.Children.Add(txtKey);
                grid.Children.Add(txtWhen);

                items.Add(new ListBoxItem { Content = grid, Tag = e });
            }
            listView.ItemsSource = items;
        }

        private void DefineBinding()
        {
            // Simple placeholder for recording keys
            if (listView.SelectedItem is ListBoxItem item && item.Tag is KeybindingEntry entry)
            {
                // TODO
            }
        }

        private void RemoveBinding()
        {
            // TODO
        }
    }
}
