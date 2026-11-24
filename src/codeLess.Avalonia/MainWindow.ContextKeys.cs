using System;
using codeLess.Core;
using Avalonia.Controls;
using System.Linq;

namespace codeLess.AvaloniaApp
{
    public partial class MainWindow
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
                        return window.PrimaryEditor?.CurrentBuffer?.HasSelection() == true;
                    case "editorreadonly":
                        return window.activeTab?.IsReadOnlyDiffPreview == true || window.activeTab?.IsReadOnly == true;
                    case "terminalfocus":
                        var focus = window.FocusManager?.GetFocusedElement() as global::Avalonia.Visual;
                        return MainWindow.IsFromTerminal(focus);
                    case "inputfocus":
                        return window.FocusManager?.GetFocusedElement() is TextBox;
                    case "findwidgetvisible":
                        return window.PrimaryEditor?.IsFindBarVisible == true;
                    case "suggestwidgetvisible":
                        return window.PrimaryLspBinding?.IsCompletionVisible == true;
                    case "explorerviewletfocus":
                        return window.sidebarBorder != null && window.sidebarBorder.IsVisible && (window.FocusManager?.GetFocusedElement() is Avalonia.Visual v2 && Avalonia.VisualTree.VisualExtensions.GetVisualAncestors(v2).Contains(window.sidebarBorder));
                    case "panelfocus":
                        return window.bottomPanelBorder != null && window.bottomPanelBorder.IsVisible && MainWindow.IsFromTerminal(window.FocusManager?.GetFocusedElement() as Avalonia.Visual);
                    case "inquickopen":
                        return window.CommandPalette != null && window.CommandPalette.IsVisible;
                    case "resourceextname":
                        return System.IO.Path.GetExtension(window.activeTab?.FilePath ?? "");
                    case "editorlangid":
                        return window.activeTab?.Language ?? "";
                    case "sidebarfocus":
                        return window.sidebarBorder != null && window.sidebarBorder.IsVisible && window.FocusManager?.GetFocusedElement() is Avalonia.Visual v && Avalonia.VisualTree.VisualExtensions.GetVisualAncestors(v).Contains(window.sidebarBorder);
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
}
