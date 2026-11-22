using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using codeLess.Studio;

namespace codeLess.AvaloniaApp
{
    /// <summary>
    /// VS Code editor switching: Ctrl+Tab / Ctrl+Shift+Tab open the "recently used editors" quick pick of the editor
    /// group (MRU order) while Ctrl is held; Tab / Shift+Tab / arrows move, releasing Ctrl opens, Escape cancels, a quick
    /// tap switches to the previous editor. Ctrl/Cmd+PageUp/PageDown and Cmd+Option+Left/Right stay tab-strip order.
    /// Works from any focus (editor, terminal, side bar, panels, chat); on macOS AppKit never delivers Ctrl+Tab to the
    /// view's keyDown:, so <see cref="MacCtrlTabBridge"/> forwards it.
    /// </summary>
    public partial class MainWindow
    {
        private readonly EditorMruList<EditorTabBuffer> editorMru = new EditorMruList<EditorTabBuffer>();
        private readonly EditorSwitcherSession<EditorTabBuffer> editorSwitcher = new EditorSwitcherSession<EditorTabBuffer>();
        private EditorSwitcherOverlay editorSwitcherOverlay;

        /// <summary>True while the Ctrl+Tab picker is shown.</summary>
        public bool IsEditorSwitcherOpen => editorSwitcher.IsOpen;
        /// <summary>The editor the picker would open now (null when closed).</summary>
        public EditorTabBuffer EditorSwitcherSelection => editorSwitcher.Selected;
        /// <summary>Editors of the group in the picker's order (index 0 = active).</summary>
        public List<EditorTabBuffer> EditorSwitcherItems => editorSwitcher.IsOpen ? new List<EditorTabBuffer>(editorSwitcher.Items) : new List<EditorTabBuffer>();
        /// <summary>Open editors in most-recently-used order.</summary>
        public List<EditorTabBuffer> EditorMruOrder => editorMru.Ordered(openTabs);

        private void InitEditorSwitcher()
        {
            AddHandler(KeyUpEvent, OnWindowKeyUpForEditorSwitcher, RoutingStrategies.Tunnel, true);
            Deactivated += (s, e) => { if (editorSwitcher.IsOpen) CancelEditorSwitcher(); };
            // Fallback when the key-up of Ctrl is not delivered: the next pointer move without Ctrl opens the selection.
            AddHandler(PointerMovedEvent, (s, e) =>
            {
                if (editorSwitcher.IsOpen && !e.KeyModifiers.HasFlag(KeyModifiers.Control)) CommitEditorSwitcher();
            }, RoutingStrategies.Tunnel, true);
            MacCtrlTabBridge.Install();
        }

        /// <summary>Ctrl+Tab (forward) / Ctrl+Shift+Tab (backwards): opens the picker or moves its selection.</summary>
        public void OpenOrAdvanceEditorSwitcher(bool backwards)
        {
            if (editorSwitcher.IsOpen)
            {
                editorSwitcher.Move(backwards ? -1 : 1);
                RenderEditorSwitcher();
                return;
            }
            var order = editorMru.Ordered(openTabs);
            if (activeTab != null)
            {
                // the active editor always heads the list (it may have been activated outside ActivateTab)
                order.Remove(activeTab);
                order.Insert(0, activeTab);
            }
            if (order.Count == 0) return;
            if (order.Count == 1)
            {
                if (!ReferenceEquals(order[0], activeTab)) ActivateTab(order[0]);
                return;
            }
            if (!editorSwitcher.Begin(order, backwards)) return;
            EnsureEditorSwitcherOverlay();
            editorSwitcherOverlay.ApplyTheme(currentTheme);
            editorSwitcherOverlay.IsVisible = true;
            RenderEditorSwitcher();
        }

        private void EnsureEditorSwitcherOverlay()
        {
            if (editorSwitcherOverlay != null) return;
            editorSwitcherOverlay = new EditorSwitcherOverlay();
            editorSwitcherOverlay.ItemClicked += idx =>
            {
                editorSwitcher.Select(idx);
                CommitEditorSwitcher();
            };
            if (Content is Panel root) root.Children.Add(editorSwitcherOverlay);
        }

        private void RenderEditorSwitcher()
        {
            if (editorSwitcherOverlay == null) return;
            editorSwitcherOverlay.Render(editorSwitcher.Items, editorSwitcher.SelectedIndex, workspaceRoot);
        }

        /// <summary>Opens the selected editor and closes the picker.</summary>
        public void CommitEditorSwitcher()
        {
            if (!editorSwitcher.IsOpen) return;
            var target = editorSwitcher.Commit();
            if (editorSwitcherOverlay != null) editorSwitcherOverlay.IsVisible = false;
            if (target != null && openTabs.Contains(target))
            {
                if (!ReferenceEquals(target, activeTab)) ActivateTab(target);
                PrimaryEditor.Canvas.Focus();
            }
        }

        public void CancelEditorSwitcher()
        {
            editorSwitcher.Cancel();
            if (editorSwitcherOverlay != null) editorSwitcherOverlay.IsVisible = false;
        }

        private static bool IsCtrlKey(Key k) => k == Key.LeftCtrl || k == Key.RightCtrl;

        /// <summary>Keys while the picker is open (window tunnel, before any control). True = consumed.</summary>
        private bool HandleEditorSwitcherKey(Key key, KeyModifiers mods)
        {
            if (!editorSwitcher.IsOpen) return false;
            bool ctrl = mods.HasFlag(KeyModifiers.Control);
            bool shift = mods.HasFlag(KeyModifiers.Shift);
            switch (key)
            {
                case Key.LeftCtrl: case Key.RightCtrl: case Key.LeftShift: case Key.RightShift:
                case Key.LeftAlt: case Key.RightAlt: case Key.LWin: case Key.RWin:
                    return true;
                case Key.Tab:
                    editorSwitcher.Move(shift ? -1 : 1);
                    RenderEditorSwitcher();
                    return true;
                case Key.Down:
                    editorSwitcher.Move(1);
                    RenderEditorSwitcher();
                    return true;
                case Key.Up:
                    editorSwitcher.Move(-1);
                    RenderEditorSwitcher();
                    return true;
                case Key.Enter:
                    CommitEditorSwitcher();
                    return true;
                case Key.Escape:
                    CancelEditorSwitcher();
                    return true;
            }
            if (!ctrl)
            {
                // Ctrl was released without us seeing the key-up (focus change, OS shortcut): open, then let the key through.
                CommitEditorSwitcher();
                return false;
            }
            return true;
        }

        private void OnWindowKeyUpForEditorSwitcher(object sender, KeyEventArgs e)
        {
            if (!editorSwitcher.IsOpen) return;
            if (IsCtrlKey(e.Key) || !e.KeyModifiers.HasFlag(KeyModifiers.Control))
            {
                // Releasing Ctrl opens the selection (a quick Ctrl+Tab tap = previous editor), like VS Code.
                CommitEditorSwitcher();
                if (IsCtrlKey(e.Key)) e.Handled = true;
            }
        }

        /// <summary>Screenshot suite: the Ctrl+Tab picker and import path completion (fixture edit, reverted; nothing saved).</summary>
        private async System.Threading.Tasks.Task RunEditorSwitchAndImportScreensAsync(string outputDir)
        {
            try
            {
                var files = workspaceFiles.Where(f => f.EndsWith(".cs", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".ts", StringComparison.OrdinalIgnoreCase) ||
                                                      f.EndsWith(".py", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".md", StringComparison.OrdinalIgnoreCase)).Take(5).ToList();
                foreach (var f in files) OpenFileInTab(f, preview: false);
                if (openTabs.Count >= 2)
                {
                    OpenOrAdvanceEditorSwitcher(backwards: false);
                    await CaptureScreenshotAsync(outputDir, "r13_ctrl_tab_picker.png");
                    CancelEditorSwitcher();
                }
            }
            catch (Exception ex) { LogSuiteError(outputDir, ex); }

            try
            {
                string ts = workspaceFiles.FirstOrDefault(f => f.EndsWith(".ts", StringComparison.OrdinalIgnoreCase));
                var binding = PrimaryLspBinding;
                if (ts != null && binding != null)
                {
                    var tab = OpenFileInTab(ts, preview: false);
                    var original = tab.Lines.ToList();
                    bool wasDirty = tab.IsDirty;
                    tab.Lines.Insert(0, "import { Money } from '../'");
                    tab.CursorLine = 0;
                    tab.CursorCol = tab.Lines[0].Length - 1;
                    PrimaryEditor.Canvas.CommitExternalEdit();
                    binding.TriggerCompletion(null, explicitInvoke: true);
                    for (int i = 0; i < 40 && !binding.IsCompletionVisible; i++) await System.Threading.Tasks.Task.Delay(50);
                    await CaptureScreenshotAsync(outputDir, "r13_import_path_completion.png");
                    binding.HideAll();
                    tab.Lines.Clear();
                    tab.Lines.AddRange(original);
                    tab.CursorLine = 0;
                    tab.CursorCol = 0;
                    PrimaryEditor.Canvas.CommitExternalEdit();
                    tab.IsDirty = wasDirty;
                }
            }
            catch (Exception ex) { LogSuiteError(outputDir, ex); }
        }

        /// <summary>Test hook: simulates releasing Ctrl.</summary>
        public void SimulateCtrlRelease()
        {
            if (editorSwitcher.IsOpen) CommitEditorSwitcher();
        }
    }

    /// <summary>The Ctrl+Tab quick pick (widget colors, 22 px rows, file badge + name + folder).</summary>
    public sealed class EditorSwitcherOverlay : Border
    {
        private const int MaxRows = 14;
        private readonly StackPanel rows = new StackPanel();
        private readonly TextBlock title;
        private StudioTheme theme = StudioTheme.GetById(StudioTheme.DefaultThemeId);
        private int scrollTop;

        public event Action<int> ItemClicked;

        public EditorSwitcherOverlay()
        {
            Width = 600;
            HorizontalAlignment = HorizontalAlignment.Center;
            VerticalAlignment = VerticalAlignment.Top;
            Margin = new Thickness(0, 40, 0, 0);
            CornerRadius = new CornerRadius(UiTokens.PopupRadius);
            BorderThickness = new Thickness(1);
            Padding = new Thickness(6);
            IsVisible = false;
            ZIndex = 210;
            Focusable = false;
            title = new TextBlock
            {
                Text = "Recently used editors",
                FontSize = UiTokens.FontSmall,
                Margin = new Thickness(8, 2, 8, 6)
            };
            var stack = new StackPanel { Spacing = 0 };
            stack.Children.Add(title);
            stack.Children.Add(rows);
            Child = stack;
        }

        public void ApplyTheme(StudioTheme t)
        {
            if (t == null) return;
            theme = t;
            Background = WorkbenchStyles.B(UiTokens.WidgetBackground(t));
            BorderBrush = WorkbenchStyles.B(UiTokens.WidgetBorder(t));
            BoxShadow = new BoxShadows(new BoxShadow { OffsetY = 8, Blur = 24, Color = WorkbenchStyles.C(UiTokens.WidgetShadow(t)) });
            title.Foreground = AvaloniaColorHelper.ToBrush(t.FgMuted);
        }

        public int RowCount => rows.Children.Count;

        public void Render(IList<EditorTabBuffer> items, int selected, string workspaceRoot)
        {
            rows.Children.Clear();
            if (items == null) return;
            if (selected < scrollTop) scrollTop = selected;
            if (selected >= scrollTop + MaxRows) scrollTop = selected - MaxRows + 1;
            if (scrollTop > Math.Max(0, items.Count - MaxRows)) scrollTop = Math.Max(0, items.Count - MaxRows);
            var fg = AvaloniaColorHelper.ToBrush(theme.TextPrimary);
            var muted = AvaloniaColorHelper.ToBrush(theme.FgMuted);
            var w = UiTokens.WidgetBackground(theme);
            var selBg = WorkbenchStyles.B(UiTokens.ListActiveSelection(theme, w));
            var hoverBg = WorkbenchStyles.B(UiTokens.Hover(theme, w));
            int end = Math.Min(items.Count, scrollTop + MaxRows);
            for (int i = scrollTop; i < end; i++)
            {
                var tab = items[i];
                int index = i;
                AvaloniaExplorerTree.GetFileTypeBadge(tab.FileName ?? "", out string badge, out Color badgeColor);
                var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Center };
                row.Children.Add(new TextBlock
                {
                    Text = badge,
                    FontFamily = new FontFamily("Cascadia Code, Consolas, Menlo, monospace"),
                    FontWeight = FontWeight.Bold,
                    FontSize = 9.5,
                    Width = 22,
                    TextAlignment = TextAlignment.Center,
                    Foreground = new SolidColorBrush(badgeColor),
                    VerticalAlignment = VerticalAlignment.Center
                });
                row.Children.Add(new TextBlock
                {
                    Text = (tab.IsDirty ? "● " : "") + (tab.FileName ?? ""),
                    FontSize = UiTokens.FontUi,
                    Foreground = fg,
                    VerticalAlignment = VerticalAlignment.Center
                });
                string dir = DescribeFolder(tab, workspaceRoot);
                if (!string.IsNullOrEmpty(dir))
                {
                    row.Children.Add(new TextBlock
                    {
                        Text = dir,
                        FontSize = UiTokens.FontSmall,
                        Foreground = muted,
                        VerticalAlignment = VerticalAlignment.Center,
                        TextTrimming = TextTrimming.CharacterEllipsis,
                        MaxWidth = 380
                    });
                }
                bool isSel = i == selected;
                var b = new Border
                {
                    Child = row,
                    Height = UiTokens.ListRowHeight,
                    Padding = new Thickness(4, 0),
                    CornerRadius = new CornerRadius(UiTokens.ControlRadius),
                    Background = isSel ? selBg : Brushes.Transparent,
                    Cursor = new Cursor(StandardCursorType.Hand),
                    Tag = tab
                };
                b.PointerEntered += (s, e) => { if (!isSel) b.Background = hoverBg; };
                b.PointerExited += (s, e) => { if (!isSel) b.Background = Brushes.Transparent; };
                b.PointerPressed += (s, e) => { ItemClicked?.Invoke(index); e.Handled = true; };
                rows.Children.Add(b);
            }
        }

        private static string DescribeFolder(EditorTabBuffer tab, string workspaceRoot)
        {
            string rel = tab.RelativePath;
            if (string.IsNullOrEmpty(rel) && !string.IsNullOrEmpty(tab.FilePath))
            {
                try
                {
                    rel = !string.IsNullOrEmpty(workspaceRoot) ? Path.GetRelativePath(workspaceRoot, tab.FilePath) : tab.FilePath;
                    if (rel.StartsWith("..", StringComparison.Ordinal)) rel = tab.FilePath;
                }
                catch { rel = tab.FilePath; }
            }
            if (string.IsNullOrEmpty(rel)) return "";
            string dir = Path.GetDirectoryName(rel) ?? "";
            return dir.Replace('\\', '/');
        }
    }
}
