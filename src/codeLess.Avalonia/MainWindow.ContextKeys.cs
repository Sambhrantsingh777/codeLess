using System;
using codeLess.Core;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.VisualTree;

namespace codeLess.AvaloniaApp
{
    public class WindowContextKeyService : IContextKeyService
    {
        private readonly MainWindow window;
        public WindowContextKeyService(MainWindow w) { window = w; }

        public object GetValue(string key)
        {
            switch (key.ToLowerInvariant())
            {
                case "editortextfocus":
                    return window.FocusManager?.GetFocusedElement() is AvaloniaVirtualEditorCanvas;
                case "editorhasselection":
                    var ed = window.FocusManager?.GetFocusedElement() as AvaloniaVirtualEditorCanvas;
                    return ed != null && ed.HasSelection();
                case "editorreadonly":
                    return false;
                case "terminalfocus":
                    var focus = window.FocusManager?.GetFocusedElement() as global::Avalonia.Visual;
                    return MainWindow.IsFromTerminal(focus);
                case "inputfocus":
                    return window.FocusManager?.GetFocusedElement() is TextBox;
                case "findwidgetvisible":
                    return window.PrimaryEditor.IsFindVisible;
                case "sidebarfocus":
                    return window.IsSidebarVisible;
                case "ismac":
                    return MacKeymap.IsMacOS;
                case "iswindows":
                    return !MacKeymap.IsMacOS && System.Runtime.InteropServices.RuntimeInformation.IsOSPlatform(System.Runtime.InteropServices.OSPlatform.Windows);
                case "islinux":
                    return !MacKeymap.IsMacOS && System.Runtime.InteropServices.RuntimeInformation.IsOSPlatform(System.Runtime.InteropServices.OSPlatform.Linux);
                default:
                    return false;
            }
        }
    }
}
