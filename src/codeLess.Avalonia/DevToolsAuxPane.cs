using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using codeLess.Studio;

namespace codeLess.AvaloniaApp
{
    /// <summary>A document shown in the side pane (Markdown preview, HTTP response, git graph, diff).</summary>
    public class AuxPaneDocument
    {
        public string Id = "";
        public string Title = "";
        public string Tooltip = "";
        public Control Content;
        public Action<StudioTheme> ApplyTheme;
        public Action Closed;
    }

    /// <summary>
    /// Second "editor group" to the right of the editor with its own tab strip. Hosts the built-in dev tool views so
    /// they never replace the user's editor tabs. Can be maximized over the editor area.
    /// </summary>
    public class AuxPaneHost : Grid
    {
        private readonly StackPanel tabStack;
        private readonly Border headerBorder;
        private readonly Border contentHost;
        private readonly Button maximizeBtn;
        private readonly Button closeBtn;
        private readonly List<AuxPaneDocument> docs = new List<AuxPaneDocument>();
        private AuxPaneDocument active;
        private StudioTheme theme = StudioTheme.GetById(StudioTheme.DefaultThemeId);
        private bool isMaximized;

        public event Action AllClosed;
        public event Action<bool> MaximizeToggled;
        public event Action<AuxPaneDocument> ActiveDocumentChanged;

        public IReadOnlyList<AuxPaneDocument> Documents => docs;
        public AuxPaneDocument ActiveDocument => active;
        public bool IsMaximized => isMaximized;

        public AuxPaneHost()
        {
            RowDefinitions = new RowDefinitions("30,*");
            ClipToBounds = true;
            var headerGrid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
            tabStack = new StackPanel { Orientation = Orientation.Horizontal };
            headerGrid.Children.Add(new ScrollViewer
            {
                Content = tabStack,
                HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Hidden,
                VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled
            });
            var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2, Margin = new Thickness(4, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center };
            maximizeBtn = MakeIconButton("maximize-up", "Maximize / Restore Pane");
            maximizeBtn.Click += (s, e) => SetMaximized(!isMaximized);
            closeBtn = MakeIconButton("close", "Close Pane");
            closeBtn.Click += (s, e) => CloseAll();
            actions.Children.Add(maximizeBtn);
            actions.Children.Add(closeBtn);
            Grid.SetColumn(actions, 1);
            headerGrid.Children.Add(actions);
            headerBorder = new Border { Child = headerGrid, BorderThickness = new Thickness(0, 0, 0, 1) };
            Children.Add(headerBorder);
            contentHost = new Border();
            Grid.SetRow(contentHost, 1);
            Children.Add(contentHost);
        }

        private static Button MakeIconButton(string icon, string tip)
        {
            var b = new Button
            {
                Content = new VectorIconControl { IconName = icon, Width = 13, Height = 13 },
                Width = 24,
                Height = 22,
                Padding = new Thickness(0),
                Background = Brushes.Transparent
            };
            ToolTip.SetTip(b, tip);
            return b;
        }

        public AuxPaneDocument Find(string id) => docs.FirstOrDefault(d => d.Id == id);

        /// <summary>Opens (or replaces content of) a document and activates it.</summary>
        public void Open(AuxPaneDocument doc)
        {
            var existing = Find(doc.Id);
            if (existing != null)
            {
                if (!ReferenceEquals(existing.Content, doc.Content))
                {
                    existing.Closed?.Invoke();
                    existing.Content = doc.Content;
                    existing.Closed = doc.Closed;
                    existing.ApplyTheme = doc.ApplyTheme;
                }
                existing.Title = doc.Title;
                existing.Tooltip = doc.Tooltip;
                Activate(existing);
                return;
            }
            docs.Add(doc);
            doc.ApplyTheme?.Invoke(theme);
            Activate(doc);
        }

        public void Activate(AuxPaneDocument doc)
        {
            active = doc;
            contentHost.Child = doc?.Content;
            RebuildTabs();
            ActiveDocumentChanged?.Invoke(doc);
        }

        public void Close(AuxPaneDocument doc)
        {
            if (doc == null) return;
            int idx = docs.IndexOf(doc);
            if (idx < 0) return;
            docs.RemoveAt(idx);
            try { doc.Closed?.Invoke(); } catch { }
            if (ReferenceEquals(active, doc))
            {
                active = null;
                contentHost.Child = null;
                if (docs.Count > 0) Activate(docs[Math.Min(idx, docs.Count - 1)]);
            }
            RebuildTabs();
            if (docs.Count == 0)
            {
                if (isMaximized) SetMaximized(false);
                AllClosed?.Invoke();
            }
        }

        public void CloseAll()
        {
            foreach (var d in docs.ToList()) Close(d);
        }

        public void SetMaximized(bool max)
        {
            if (isMaximized == max) return;
            isMaximized = max;
            if (maximizeBtn.Content is VectorIconControl ic) { ic.IconName = max ? "min" : "maximize-up"; ic.InvalidateVisual(); }
            MaximizeToggled?.Invoke(max);
        }

        public void ApplyTheme(StudioTheme t)
        {
            if (t == null) return;
            theme = t;
            Background = AvaloniaColorHelper.ToBrush(t.BgEditor);
            headerBorder.Background = AvaloniaColorHelper.ToBrush(t.BgSidebar);
            headerBorder.BorderBrush = AvaloniaColorHelper.ToBrush(t.BorderSubtle);
            foreach (var b in new[] { maximizeBtn, closeBtn })
            {
                b.Foreground = AvaloniaColorHelper.ToBrush(t.FgSecondary);
                if (b.Content is VectorIconControl ic) { ic.IconColor = AvaloniaColorHelper.ToAvColor(t.FgSecondary); ic.InvalidateVisual(); }
            }
            foreach (var d in docs) { try { d.ApplyTheme?.Invoke(t); } catch { } }
            RebuildTabs();
        }

        public void RefreshTitles() => RebuildTabs();

        private void RebuildTabs()
        {
            tabStack.Children.Clear();
            foreach (var d in docs)
            {
                var captured = d;
                bool isActive = ReferenceEquals(d, active);
                var title = new TextBlock
                {
                    Text = d.Title,
                    FontSize = 12,
                    VerticalAlignment = VerticalAlignment.Center,
                    Foreground = AvaloniaColorHelper.ToBrush(isActive ? theme.FgPrimary : theme.FgMuted),
                    MaxWidth = 220,
                    TextTrimming = TextTrimming.CharacterEllipsis
                };
                var x = new Button
                {
                    Content = "✕",
                    FontSize = 10,
                    Padding = new Thickness(3, 0),
                    Margin = new Thickness(6, 0, 0, 0),
                    Background = Brushes.Transparent,
                    Foreground = AvaloniaColorHelper.ToBrush(theme.FgMuted),
                    VerticalAlignment = VerticalAlignment.Center
                };
                ToolTip.SetTip(x, "Close");
                x.Click += (s, e) => { Close(captured); e.Handled = true; };
                var row = new StackPanel { Orientation = Orientation.Horizontal, Children = { title, x } };
                var tab = new Border
                {
                    Child = row,
                    Padding = new Thickness(12, 0, 6, 0),
                    Height = 30,
                    Background = AvaloniaColorHelper.ToBrush(isActive ? theme.BgEditor : theme.BgSidebar),
                    BorderBrush = AvaloniaColorHelper.ToBrush(isActive ? theme.AccentPrimary : theme.BorderSubtle),
                    BorderThickness = isActive ? new Thickness(0, 2, 1, 0) : new Thickness(0, 0, 1, 0),
                    Cursor = new Cursor(StandardCursorType.Hand)
                };
                if (!string.IsNullOrEmpty(d.Tooltip)) ToolTip.SetTip(tab, d.Tooltip);
                tab.PointerPressed += (s, e) =>
                {
                    var pp = e.GetCurrentPoint(tab).Properties;
                    if (pp.IsMiddleButtonPressed) Close(captured);
                    else Activate(captured);
                };
                tabStack.Children.Add(tab);
            }
        }
    }

    /// <summary>
    /// Base for virtualized, custom-drawn row lists (git graph, diff view): only visible rows are drawn, with its own
    /// scroll thumb, wheel, drag and keyboard navigation. Theme-aware through <see cref="Theme"/>.
    /// </summary>
    public abstract class VirtualRowsControl : Control
    {
        public StudioTheme Theme { get; private set; } = StudioTheme.GetById(StudioTheme.DefaultThemeId);
        public double RowHeight { get; set; } = 22;
        public double ScrollOffset { get; private set; }
        public int SelectedIndex { get; private set; } = -1;
        protected const double ThumbWidth = 10;
        private bool draggingThumb;
        private double dragStartY, dragStartOffset;

        public event Action<int> SelectionChanged;
        public event Action<int> RowActivated;
        /// <summary>Raised when the view scrolled near the end (incremental loading).</summary>
        public event Action NearEnd;

        protected VirtualRowsControl()
        {
            Focusable = true;
            ClipToBounds = true;
        }

        public abstract int RowCount { get; }
        protected abstract void DrawRow(DrawingContext ctx, int index, Rect rowRect, bool selected);

        public void ApplyTheme(StudioTheme t)
        {
            if (t == null) return;
            Theme = t;
            InvalidateVisual();
        }

        protected double MaxOffset => Math.Max(0, RowCount * RowHeight - Bounds.Height);

        public void ScrollTo(double offset)
        {
            double o = Math.Max(0, Math.Min(MaxOffset, offset));
            if (Math.Abs(o - ScrollOffset) < 0.01) return;
            ScrollOffset = o;
            InvalidateVisual();
            CheckNearEnd();
        }

        public void EnsureVisible(int index)
        {
            if (index < 0) return;
            double top = index * RowHeight;
            if (top < ScrollOffset) ScrollTo(top);
            else if (top + RowHeight > ScrollOffset + Bounds.Height) ScrollTo(top + RowHeight - Bounds.Height);
        }

        public void Select(int index, bool notify = true)
        {
            if (RowCount == 0) index = -1;
            else index = Math.Max(0, Math.Min(RowCount - 1, index));
            if (index == SelectedIndex) return;
            SelectedIndex = index;
            EnsureVisible(index);
            InvalidateVisual();
            if (notify) SelectionChanged?.Invoke(index);
        }

        protected void ResetRows()
        {
            SelectedIndex = -1;
            ScrollOffset = 0;
            InvalidateVisual();
        }

        protected void CheckNearEnd()
        {
            if (RowCount > 0 && ScrollOffset + Bounds.Height >= RowCount * RowHeight - RowHeight * 40) NearEnd?.Invoke();
        }

        public override void Render(DrawingContext ctx)
        {
            base.Render(ctx);
            var bg = AvaloniaColorHelper.ToBrush(Theme.BgEditor);
            ctx.FillRectangle(bg, new Rect(Bounds.Size));
            int count = RowCount;
            if (count == 0) { DrawEmpty(ctx); return; }
            int first = Math.Max(0, (int)(ScrollOffset / RowHeight));
            int last = Math.Min(count - 1, (int)((ScrollOffset + Bounds.Height) / RowHeight) + 1);
            double w = Math.Max(0, Bounds.Width - ThumbWidth);
            for (int i = first; i <= last; i++)
            {
                var r = new Rect(0, i * RowHeight - ScrollOffset, w, RowHeight);
                using (ctx.PushClip(r)) DrawRow(ctx, i, r, i == SelectedIndex);
            }
            // Scroll thumb
            double total = count * RowHeight;
            if (total > Bounds.Height)
            {
                double th = Math.Max(24, Bounds.Height * Bounds.Height / total);
                double ty = (Bounds.Height - th) * (ScrollOffset / Math.Max(1, MaxOffset));
                ctx.FillRectangle(AvaloniaColorHelper.ToBrush(Theme.FgMuted, 70), new Rect(Bounds.Width - ThumbWidth + 2, ty, ThumbWidth - 4, th), 3);
            }
        }

        protected virtual void DrawEmpty(DrawingContext ctx) { }

        protected override void OnSizeChanged(SizeChangedEventArgs e)
        {
            base.OnSizeChanged(e);
            ScrollTo(ScrollOffset);
            CheckNearEnd();
        }

        protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
        {
            base.OnPointerWheelChanged(e);
            ScrollTo(ScrollOffset - e.Delta.Y * RowHeight * 3);
            e.Handled = true;
        }

        protected override void OnPointerPressed(PointerPressedEventArgs e)
        {
            base.OnPointerPressed(e);
            Focus();
            var pt = e.GetPosition(this);
            if (pt.X >= Bounds.Width - ThumbWidth && RowCount * RowHeight > Bounds.Height)
            {
                draggingThumb = true;
                dragStartY = pt.Y;
                dragStartOffset = ScrollOffset;
                e.Pointer.Capture(this);
                e.Handled = true;
                return;
            }
            int idx = (int)((pt.Y + ScrollOffset) / RowHeight);
            if (idx >= 0 && idx < RowCount)
            {
                if (OnRowPointerPressed(idx, pt, e)) { e.Handled = true; return; }
                Select(idx);
                if (e.ClickCount >= 2) RowActivated?.Invoke(idx);
            }
            e.Handled = true;
        }

        /// <summary>Lets subclasses handle clicks on parts of a row (return true to consume).</summary>
        protected virtual bool OnRowPointerPressed(int index, Point pt, PointerPressedEventArgs e) => false;

        protected override void OnPointerMoved(PointerEventArgs e)
        {
            base.OnPointerMoved(e);
            if (!draggingThumb) return;
            double total = RowCount * RowHeight;
            double th = Math.Max(24, Bounds.Height * Bounds.Height / Math.Max(1, total));
            double ratio = MaxOffset / Math.Max(1, Bounds.Height - th);
            ScrollTo(dragStartOffset + (e.GetPosition(this).Y - dragStartY) * ratio);
        }

        protected override void OnPointerReleased(PointerReleasedEventArgs e)
        {
            base.OnPointerReleased(e);
            if (draggingThumb) { draggingThumb = false; e.Pointer.Capture(null); }
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            int page = Math.Max(1, (int)(Bounds.Height / RowHeight) - 1);
            switch (e.Key)
            {
                case Key.Down: Select(SelectedIndex + 1); e.Handled = true; break;
                case Key.Up: Select(SelectedIndex - 1); e.Handled = true; break;
                case Key.PageDown: Select(SelectedIndex + page); e.Handled = true; break;
                case Key.PageUp: Select(SelectedIndex - page); e.Handled = true; break;
                case Key.Home: Select(0); e.Handled = true; break;
                case Key.End: Select(RowCount - 1); e.Handled = true; break;
                case Key.Enter: if (SelectedIndex >= 0) RowActivated?.Invoke(SelectedIndex); e.Handled = true; break;
            }
        }

        protected static FormattedText Text(string s, Typeface tf, double size, IBrush brush)
        {
            return new FormattedText(s ?? "", CultureInfo.InvariantCulture, FlowDirection.LeftToRight, tf, size, brush);
        }

        protected static void DrawClipped(DrawingContext ctx, string s, Typeface tf, double size, IBrush brush, double x, Rect row, double maxWidth)
        {
            if (maxWidth <= 4 || string.IsNullOrEmpty(s)) return;
            var ft = Text(s, tf, size, brush);
            ft.MaxTextWidth = maxWidth;
            ft.MaxLineCount = 1;
            ft.Trimming = TextTrimming.CharacterEllipsis;
            ctx.DrawText(ft, new Point(x, row.Y + (row.Height - ft.Height) / 2));
        }
    }

    public static class DevToolsFonts
    {
        public static readonly string MonoFamily = System.Runtime.InteropServices.RuntimeInformation.IsOSPlatform(System.Runtime.InteropServices.OSPlatform.OSX)
            ? "Menlo, Monaco, SF Mono, monospace"
            : "Cascadia Code, Consolas, DejaVu Sans Mono, monospace";
        public static readonly Typeface Mono = new Typeface(MonoFamily);
        public static readonly Typeface MonoBold = new Typeface(MonoFamily, FontStyle.Normal, FontWeight.Bold);
        public static readonly Typeface MonoItalic = new Typeface(MonoFamily, FontStyle.Italic, FontWeight.Normal);
        public static readonly Typeface Ui = new Typeface("Inter, Segoe UI, sans-serif");
        public static readonly Typeface UiBold = new Typeface("Inter, Segoe UI, sans-serif", FontStyle.Normal, FontWeight.SemiBold);
    }
}
