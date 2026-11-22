using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using codeLess.AvaloniaApp;
using codeLess.Studio;
using Xunit;

namespace codeLess.Avalonia.Tests
{
    /// <summary>
    /// UI review round 2 (#41 Settings overflow, #42 DEBUG CONSOLE tab, #43 occurrence highlight, #44 Git Graph split):
    /// headless layout and behaviour assertions.
    /// </summary>
    [Collection("SequentialWorkbench")]
    public class UiReviewRound2HeadlessTests : IDisposable
    {
        private readonly string settingsPath = IdePersistentSettings.GetSettingsFilePath();
        private readonly string settingsBackup;
        private readonly string ws = Path.Combine(Path.GetTempPath(), "cl-uirev2-" + Guid.NewGuid().ToString("N").Substring(0, 8));

        public UiReviewRound2HeadlessTests()
        {
            settingsBackup = File.Exists(settingsPath) ? File.ReadAllText(settingsPath) : null;
            Directory.CreateDirectory(ws);
        }

        public void Dispose()
        {
            EditorNicetyOptions.ApplyFrom(new IdePersistentSettings());
            try { if (settingsBackup != null) File.WriteAllText(settingsPath, settingsBackup); else if (File.Exists(settingsPath)) File.Delete(settingsPath); } catch { }
            try { Directory.Delete(ws, true); } catch { }
        }

        private static void Layout(TopLevel w)
        {
            Dispatcher.UIThread.RunJobs();
            w.UpdateLayout();
            Dispatcher.UIThread.RunJobs();
            w.UpdateLayout();
        }

        private static Rect BoundsIn(Visual v, Visual root)
        {
            var p = v.TranslatePoint(new Point(0, 0), root) ?? new Point(double.NaN, double.NaN);
            return new Rect(p, v.Bounds.Size);
        }

        private static string CheckText(CheckBox c) => c.Content as string ?? (c.Content as TextBlock)?.Text ?? "";

        // ------------------------------------------------------------------ #41 settings dialog

        [AvaloniaTheory]
        [InlineData(1440, 810)]
        [InlineData(1000, 600)]
        [InlineData(760, 520)]
        public void Settings_EveryChildStaysInsideTheDialog_AndTheBodyScrolls(int width, int height)
        {
            var w = new MainWindow(ws) { SuppressUnsavedPrompts = true, Width = width, Height = height };
            w.Show();
            try
            {
                Layout(w);
                w.ShowSettingsModal();
                Layout(w);
                var host = w.ModalHost;
                Assert.Equal("settings", host.ActiveDialogId);
                var card = BoundsIn(host.Card, w);
                Assert.True(card.X >= 0 && card.Y >= 0 && card.Right <= w.Bounds.Width + 0.5 && card.Bottom <= w.Bounds.Height + 0.5, $"card {card} outside window {w.Bounds.Size}");
                Assert.True(host.Card.ClipToBounds);

                var root = (Control)host.ActiveDialogBody;
                Assert.Equal(SettingsDialogView.RootName, root.Name);
                var scroll = root.GetVisualDescendants().OfType<ScrollViewer>().First(s => s.Name == SettingsDialogView.ScrollName);
                var view = BoundsIn(scroll, w);
                Assert.True(view.Y >= card.Y - 0.5 && view.Bottom <= card.Bottom + 0.5, $"scroll viewport {view} outside card {card}");
                Assert.True(view.X >= card.X - 0.5 && view.Right <= card.Right + 0.5, $"scroll viewport {view} outside card {card}");

                // footer buttons and the search box are inside the card
                foreach (var name in new[] { SettingsDialogView.SaveName, SettingsDialogView.CancelName, SettingsDialogView.SearchName })
                {
                    var c = root.GetVisualDescendants().OfType<Control>().First(x => x.Name == name);
                    var r = BoundsIn(c, w);
                    Assert.True(r.Y >= card.Y && r.Bottom <= card.Bottom + 0.5 && r.X >= card.X && r.Right <= card.Right + 0.5, $"{name} {r} outside card {card}");
                }

                // every setting row: no horizontal overflow, labels wrap instead of being cut off
                var body = (Control)scroll.Content;
                var checks = body.GetVisualDescendants().OfType<CheckBox>().Where(c => c.IsEffectivelyVisible).ToList();
                Assert.True(checks.Count >= 20, "check boxes: " + checks.Count);
                foreach (var c in body.GetVisualDescendants().OfType<Control>().Where(c => c.IsEffectivelyVisible && (c is CheckBox || c is TextBox || c is ComboBox)))
                {
                    var r = BoundsIn(c, scroll);
                    Assert.True(r.X >= -0.5 && r.Right <= scroll.Viewport.Width + 0.5, $"{c.GetType().Name} '{(c as CheckBox != null ? CheckText((CheckBox)c) : c.Name)}' {r} overflows viewport width {scroll.Viewport.Width}");
                }
                foreach (var c in checks)
                {
                    var tb = c.GetVisualDescendants().OfType<TextBlock>().FirstOrDefault(t => !string.IsNullOrEmpty(t.Text));
                    Assert.NotNull(tb);
                    Assert.Equal(TextWrapping.Wrap, tb.TextWrapping);
                    tb.Measure(new Size(tb.Bounds.Width, double.PositiveInfinity));
                    Assert.True(tb.DesiredSize.Height <= tb.Bounds.Height + 0.5, $"'{tb.Text}' needs {tb.DesiredSize.Height:0.#}px but has {tb.Bounds.Height:0.#}px (clipped)");
                    var tr = BoundsIn(tb, scroll);
                    Assert.True(tr.Right <= scroll.Viewport.Width + 0.5, $"'{tb.Text}' right edge {tr.Right:0.#} beyond {scroll.Viewport.Width:0.#}");
                }

                // the last section (AI provider) is reachable by scrolling inside the dialog, never drawn below it
                var provider = body.GetVisualDescendants().OfType<ComboBox>().First(c => (ToolTip.GetTip(c) as string) == "codeless.ai.provider");
                if (scroll.Extent.Height > scroll.Viewport.Height + 1)
                {
                    var before = BoundsIn(provider, w);
                    Assert.True(before.Y >= view.Bottom - 0.5 || before.Bottom <= view.Bottom + 0.5, "provider is either below the viewport (clipped) or inside it");
                    scroll.Offset = new Vector(0, scroll.Extent.Height - scroll.Viewport.Height);
                    Layout(w);
                }
                var after = BoundsIn(provider, w);
                Assert.True(after.Y >= view.Y - 0.5 && after.Bottom <= view.Bottom + 0.5, $"provider {after} not visible in viewport {view} after scrolling");
                if (height <= 600) Assert.True(scroll.Extent.Height > scroll.Viewport.Height, "small windows must scroll the settings body");
            }
            finally { w.ModalHost.CloseModal(); w.Close(); }
        }

        [AvaloniaFact]
        public void Settings_SearchFiltersRows_HidesEmptySections_AndShowsNoResults()
        {
            var w = new MainWindow(ws) { SuppressUnsavedPrompts = true, Width = 1280, Height = 800 };
            w.Show();
            try
            {
                Layout(w);
                w.ShowSettingsModal();
                Layout(w);
                var root = (Control)w.ModalHost.ActiveDialogBody;
                var search = root.GetVisualDescendants().OfType<TextBox>().First(t => t.Name == SettingsDialogView.SearchName);
                var noResults = root.GetVisualDescendants().OfType<TextBlock>().First(t => t.Name == SettingsDialogView.NoResultsName);
                List<string> Visible() => root.GetVisualDescendants().OfType<CheckBox>().Where(c => c.IsEffectivelyVisible).Select(CheckText).ToList();
                int all = Visible().Count;

                search.Text = "indent";
                Layout(w);
                var shown = Visible();
                Assert.Contains(shown, t => t.StartsWith("Detect indentation"));
                Assert.Contains(shown, t => t == "Indent guides");
                Assert.DoesNotContain(shown, t => t.StartsWith("Word wrap"));
                Assert.DoesNotContain(shown, t => t.StartsWith("Offline"));
                Assert.False(noResults.IsVisible);
                // the AI section has no match and is hidden entirely
                Assert.DoesNotContain(root.GetVisualDescendants().OfType<TextBlock>(), t => t.IsEffectivelyVisible && t.Text == "AI PROVIDER & BYOK");

                // VS Code setting ids match too
                search.Text = "editor.stickyScroll";
                Layout(w);
                Assert.Equal(new[] { "Sticky scroll" }, Visible());

                search.Text = "zzzz-no-such-setting";
                Layout(w);
                Assert.Empty(Visible());
                Assert.True(noResults.IsVisible);

                search.Text = "";
                Layout(w);
                Assert.Equal(all, Visible().Count);
                Assert.False(noResults.IsVisible);
            }
            finally { w.ModalHost.CloseModal(); w.Close(); }
        }

        [AvaloniaFact]
        public void ModalHost_TallContent_ScrollsInsideTheCard()
        {
            var w = new MainWindow(ws) { SuppressUnsavedPrompts = true, Width = 1000, Height = 600 };
            w.Show();
            try
            {
                Layout(w);
                var tall = new StackPanel();
                for (int i = 0; i < 80; i++) tall.Children.Add(new TextBlock { Text = "Row " + i, Height = 20 });
                w.ModalHost.ShowModal("tall", "Tall", tall, 500);
                Layout(w);
                var card = BoundsIn(w.ModalHost.Card, w);
                Assert.True(card.Bottom <= w.Bounds.Height + 0.5 && card.Y >= 0, "card " + card);
                var sv = w.ModalHost.BodyScroll;
                Assert.True(sv.Extent.Height > sv.Viewport.Height, "tall content scrolls");
                Assert.True(BoundsIn(sv, w).Bottom <= card.Bottom + 0.5);
            }
            finally { w.ModalHost.CloseModal(); w.Close(); }
        }

        // ------------------------------------------------------------------ #42 panel tabs

        [AvaloniaFact]
        public void PanelTabs_VsCodeOrder_DebugConsoleSelectable_ToolbarAndContentSwap()
        {
            var w = new MainWindow(ws) { SuppressUnsavedPrompts = true, Width = 1280, Height = 800 };
            w.Show();
            try
            {
                Layout(w);
                Button Tab(string id) => w.GetVisualDescendants().OfType<Button>().First(b => b.Name == "PanelTab_" + id);
                var row = (Panel)Tab("problems").Parent;
                var order = row.Children.OfType<Button>().Where(b => b.IsVisible).Select(b => b.Name).ToList();
                Assert.Equal(new[] { "PanelTab_problems", "PanelTab_output", "PanelTab_debug", "PanelTab_terminal" }, order);

                Button Named(string n) => w.GetVisualDescendants().OfType<Button>().First(b => b.Name == n);
                var accent = AvaloniaColorHelper.ToAvColor(StudioTheme.GetById(StudioTheme.DefaultThemeId).AccentPrimary);
                bool Selected(Button b) => b.BorderBrush is ISolidColorBrush sb && sb.Color == accent;

                Tab("debug").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Layout(w);
                Assert.Equal("debug", w.ActiveBottomTabId);
                Assert.True(Tab("debug").IsVisible);
                Assert.True(Selected(Tab("debug")));
                Assert.False(Selected(Tab("terminal")));
                Assert.True(w.DebugConsole.IsVisible);
                Assert.True(w.DebugConsole.Input.IsFocused, "explicitly opening the DEBUG CONSOLE focuses its input");
                Assert.False(Named("NewTerminalBtn").IsVisible);
                Assert.False(Named("SplitTerminalBtn").IsVisible);
                Assert.True(Named("ClearPanelBtn").IsVisible);
                Assert.Equal("Clear Console", ToolTip.GetTip(Named("ClearPanelBtn")));

                Tab("terminal").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Layout(w);
                Assert.False(w.DebugConsole.IsVisible);
                Assert.True(Selected(Tab("terminal")));
                Assert.False(Selected(Tab("debug")));
                Assert.True(Named("NewTerminalBtn").IsVisible);
                Assert.False(Named("ClearPanelBtn").IsVisible);

                w.SwitchBottomTab("problems");
                Layout(w);
                Assert.True(Selected(Tab("problems")));
                Assert.False(Named("NewTerminalBtn").IsVisible);
                Assert.False(Named("KillTerminalBtn").IsVisible);
                Assert.False(Named("ClearPanelBtn").IsVisible);

                // a programmatic switch (e.g. debug session start) reveals the tab and selects it without moving focus
                w.PrimaryEditor.Canvas.Focus();
                w.SwitchBottomTab("debug");
                Layout(w);
                Assert.True(Selected(Tab("debug")));
                Assert.False(w.DebugConsole.Input.IsFocused);
            }
            finally { w.Close(); }
        }

        [AvaloniaFact]
        public void Explorer_SingleFolderRootIsTheSectionHeader_NotARepeatedTreeRow()
        {
            Directory.CreateDirectory(Path.Combine(ws, "src"));
            File.WriteAllText(Path.Combine(ws, "src", "a.cs"), "class A {}\n");
            File.WriteAllText(Path.Combine(ws, "readme.md"), "# x\n");
            var w = new MainWindow(ws) { SuppressUnsavedPrompts = true, Width = 1200, Height = 800 };
            w.Show();
            try
            {
                Layout(w);
                var tree = w.ExplorerTree;
                Assert.Single(tree.Nodes);
                var root = tree.Nodes[0];
                Assert.DoesNotContain(root, tree.VisibleFlatRows);           // VS Code / WinForms HideRootFolderRow
                Assert.Contains(tree.VisibleFlatRows, n => n.Text == "src");
                Assert.Contains(tree.VisibleFlatRows, n => n.Text == "readme.md");
                Assert.All(tree.VisibleFlatRows.Where(n => ReferenceEquals(n.Parent, root)), n => Assert.Equal(0, tree.DisplayDepth(n)));
                tree.CollapseAllFolders();
                Assert.True(root.IsExpanded);
                Assert.Contains(tree.VisibleFlatRows, n => n.Text == "readme.md");
            }
            finally { w.Close(); }
        }

        // ------------------------------------------------------------------ #44 git graph split

        [AvaloniaFact]
        public void GitGraph_SplitIsDraggableWithMinWidths_AndStacksWhenNarrow()
        {
            var view = new GitGraphView(ws, null);
            var win = new Window { Width = 1000, Height = 600, Content = view };
            win.Show();
            try
            {
                Layout(win);
                Assert.False(view.IsStacked);
                var splitter = view.GetVisualDescendants().OfType<GridSplitter>().First(s => s.Name == "GitGraphSplitter");
                Assert.Equal(GridResizeDirection.Columns, splitter.ResizeDirection);
                var list = BoundsIn(view.List, view);
                var details = BoundsIn(view.DetailsPane, view);
                Assert.True(list.Width >= 560, "list keeps most of a wide pane: " + list.Width);
                Assert.InRange(details.Width, GitGraphSplitLayout.DetailsMinWidth - 0.5, GitGraphSplitLayout.DetailsMaxInitialWidth + 0.5);
                Assert.True(details.X >= list.Right, "details to the right of the list");

                // a typical aux-pane width: the graph must not be squeezed to a sliver
                win.Width = 600;
                Layout(win);
                Assert.False(view.IsStacked);
                list = BoundsIn(view.List, view);
                details = BoundsIn(view.DetailsPane, view);
                Assert.True(list.Width >= GitGraphSplitLayout.ListMinWidth - 0.5, "list " + list.Width);
                Assert.True(details.Width >= GitGraphSplitLayout.DetailsMinWidth - 0.5, "details " + details.Width);

                win.Width = 420;
                Layout(win);
                Assert.True(view.IsStacked);
                Assert.Equal(GridResizeDirection.Rows, splitter.ResizeDirection);
                list = BoundsIn(view.List, view);
                details = BoundsIn(view.DetailsPane, view);
                Assert.True(list.Width >= 410 && details.Width >= 410, $"full width when stacked: {list.Width} / {details.Width}");
                Assert.True(details.Y >= list.Bottom, "details below the list");
                Assert.True(list.Height >= GitGraphSplitLayout.ListMinHeight - 0.5 && details.Height >= GitGraphSplitLayout.DetailsMinHeight - 0.5);

                win.Width = 900;
                Layout(win);
                Assert.False(view.IsStacked);
            }
            finally { win.Close(); }
        }

        // ------------------------------------------------------------------ #43 occurrences

        private static void RenderOnce(AvaloniaVirtualEditorCanvas canvas, double w = 1000, double h = 600)
        {
            canvas.Measure(new Size(w, h));
            canvas.Arrange(new Rect(0, 0, w, h));
            var target = new RenderTargetBitmap(new PixelSize(10, 10));
            using (var ctx = target.CreateDrawingContext())
                canvas.Render(ctx);
        }

        [AvaloniaTheory]
        [InlineData(true)]
        [InlineData(false)]
        public void Occurrences_NotOnKeywords_OnIdentifiers_WithAndWithoutTextMate(bool textMate)
        {
            var s = new IdePersistentSettings { EditorTextMateHighlighting = textMate };
            EditorNicetyOptions.ApplyFrom(s);
            var sb = new StringBuilder();
            sb.Append("using System;\nusing System.Linq;\n\nnamespace Demo\n{\n    public class Orders\n    {\n        public int Count(int count)\n        {\n");
            for (int i = 0; i < 6; i++) sb.Append("            if (count > " + i + ") return count;\n");
            sb.Append("            return 0;\n        }\n    }\n}\n");
            string file = Path.Combine(ws, "Orders.cs");
            File.WriteAllText(file, sb.ToString());
            var w = new MainWindow(ws) { SuppressUnsavedPrompts = true };
            w.Show();
            try
            {
                var tab = w.OpenFileInTab(file, preview: false);
                var canvas = w.PrimaryEditor.Canvas;
                RenderOnce(canvas);
                canvas.CompleteBackgroundTokenization();
                RenderOnce(canvas);

                void At(int line, int col) { tab.CursorLine = line; tab.CursorCol = col; canvas.FlushOccurrenceHighlight(); RenderOnce(canvas); }

                At(0, 2);   // using
                Assert.Null(canvas.CurrentOccurrenceTarget);
                Assert.Equal(0, canvas.LastFrameOccurrenceCount);
                At(9, 13);  // if
                Assert.Null(canvas.CurrentOccurrenceTarget);
                At(9, 33);  // return
                Assert.Null(canvas.CurrentOccurrenceTarget);
                At(7, 16);  // int
                Assert.Null(canvas.CurrentOccurrenceTarget);
                At(5, 6);   // public
                Assert.Null(canvas.CurrentOccurrenceTarget);
                At(9, 10);  // whitespace / punctuation "(" area
                tab.CursorCol = 15; canvas.FlushOccurrenceHighlight();
                Assert.Null(canvas.CurrentOccurrenceTarget); // on " " before '('? -> nothing

                At(9, 17);  // count (identifier)
                Assert.Equal("count", canvas.CurrentOccurrenceTarget);
                Assert.True(canvas.LastFrameOccurrenceCount >= 12, "occurrences " + canvas.LastFrameOccurrenceCount);
                At(0, 8);   // System (identifier after a keyword)
                Assert.Equal("System", canvas.CurrentOccurrenceTarget);

                // an explicit selection is always highlighted, even a keyword
                tab.CursorLine = 0; tab.SelectionAnchorLine = 0; tab.SelectionAnchorCol = 0; tab.CursorCol = 5;
                canvas.FlushOccurrenceHighlight();
                Assert.Equal("using", canvas.CurrentOccurrenceTarget);
            }
            finally { w.Close(); }
        }
    }
}
