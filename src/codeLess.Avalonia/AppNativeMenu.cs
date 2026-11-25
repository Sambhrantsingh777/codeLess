using System;
using System.Collections.Generic;
using Avalonia.Controls;
using codeLess.Studio;

namespace codeLess.AvaloniaApp
{
    /// <summary>
    /// The macOS application menu ("codeLess": About, Check for Updates..., Settings ▸, Hide, Quit) built from the shared
    /// <see cref="WorkbenchMenuModel"/>. Avalonia 11.1's macOS exporter reads <c>NativeMenu.GetMenu(Application)</c> once,
    /// right after <c>Application.Initialize</c>; replacing it later with <c>NativeMenu.SetMenu</c> is never exported, so
    /// v3.3.3 showed a Quit-only app menu (no About / Check for Updates / Settings). The full menu is therefore built in
    /// <see cref="App.Initialize"/>; items dispatch to the main window's command map and their labels are updated in place
    /// (the update state: "Check for Updates..." → "Downloading Update..." → "Restart to Update").
    /// </summary>
    public static class AppNativeMenu
    {
        private static readonly Dictionary<string, NativeMenuItem> items = new Dictionary<string, NativeMenuItem>(StringComparer.Ordinal);

        /// <summary>Items of the app menu by command id (empty until <see cref="Build"/> ran).</summary>
        public static IReadOnlyDictionary<string, NativeMenuItem> Items => items;

        public static NativeMenu Build(Func<MainWindow> window, Action quitWithoutWindow)
        {
            items.Clear();
            var root = new NativeMenu();
            foreach (var menu in WorkbenchMenuModel.Build())
            {
                if (menu.Title != "codeLess") continue;
                Fill(root, menu.Items, window, quitWithoutWindow);
            }
            return root;
        }

        private static void Fill(NativeMenu target, List<WorkbenchMenuEntry> entries, Func<MainWindow> window, Action quitWithoutWindow)
        {
            bool lastWasSep = true;
            foreach (var e in entries)
            {
                if (!e.VisibleOn(true)) continue;
                if (e.IsSeparator)
                {
                    if (!lastWasSep) { target.Add(new NativeMenuItemSeparator()); lastWasSep = true; }
                    continue;
                }
                lastWasSep = false;
                if (e.IsSubmenu)
                {
                    var sub = new NativeMenu();
                    Fill(sub, e.Children, window, quitWithoutWindow);
                    if (e.Id == "window") {
                        sub.Items.Add(new NativeMenuItemSeparator());
                        // NativeMenuItem doesn't have an Opening event in Avalonia's NativeMenu.
                        // On macOS, Avalonia automatically populates the Window menu with open windows.
                    }
                    target.Add(new NativeMenuItem(e.Label) { Menu = sub });
                    continue;
                }
                string id = e.Id;
                var item = new NativeMenuItem(e.Label);
                if (!e.DisplayOnly && MainWindow.TryParseMenuGesture(e.ShortcutFor(true), out var g)) item.Gesture = g;
                item.Click += (s, a) =>
                {
                    var w = window();
                    if (w != null) w.InvokeMenuCommand(id);
                    else if (id == "app.quit") quitWithoutWindow?.Invoke();
                };
                items[id] = item;
                target.Add(item);
            }
        }

        /// <summary>Updates the "Check for Updates..." item in place (label + enabled) so the exported menu follows.</summary>
        public static void SetUpdateItem(string label, bool enabled)
        {
            if (!items.TryGetValue("app.checkUpdates", out var item)) return;
            if (item.Header != label) item.Header = label;
            if (item.IsEnabled != enabled) item.IsEnabled = enabled;
        }
    }
}
