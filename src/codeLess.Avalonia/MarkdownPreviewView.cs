using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using codeLess.Studio;

namespace codeLess.AvaloniaApp
{
    /// <summary>
    /// Native Markdown preview (no browser engine): builds Avalonia controls from <see cref="MarkdownParser"/> output.
    /// Code fences reuse the editor tokenizer; links raise <see cref="LinkClicked"/>; scroll syncs by source line.
    /// </summary>
    public class MarkdownPreviewView : Grid
    {
        public const string NetworkFeature = "Markdown Preview";
        private readonly ScrollViewer scroll;
        private readonly StackPanel root = new StackPanel { Spacing = 10, Margin = new Thickness(28, 18, 28, 40), MaxWidth = 980, HorizontalAlignment = HorizontalAlignment.Left };
        private readonly List<KeyValuePair<int, Control>> lineAnchors = new List<KeyValuePair<int, Control>>();
        private readonly Dictionary<string, Control> headingAnchors = new Dictionary<string, Control>(StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<string, Bitmap> remoteImageCache = new Dictionary<string, Bitmap>();
        private StudioTheme theme = StudioTheme.GetById(StudioTheme.DefaultThemeId);
        private string lastText;
        private int lastSyncedLine = -1;

        public string FilePath { get; set; }
        public MdDocument Document { get; private set; }
        /// <summary>(url, resolved target).</summary>
        public event Action<string, MdLinkTarget> LinkClicked;
        public int RenderCount { get; private set; }
        public StackPanel ContentPanel => root;

        public MarkdownPreviewView(string filePath)
        {
            FilePath = filePath;
            scroll = new ScrollViewer
            {
                Content = root,
                HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
                VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto
            };
            Children.Add(scroll);
        }

        public void ApplyTheme(StudioTheme t)
        {
            theme = t;
            Background = AvaloniaColorHelper.ToBrush(t.BgEditor);
            if (lastText != null) { string txt = lastText; lastText = null; Render(txt); }
        }

        /// <summary>Re-renders when the text changed (keeps the scroll position).</summary>
        public void Render(string markdown)
        {
            markdown = markdown ?? "";
            if (markdown == lastText) return;
            lastText = markdown;
            var offset = scroll.Offset;
            Document = MarkdownParser.Parse(markdown);
            root.Children.Clear();
            lineAnchors.Clear();
            headingAnchors.Clear();
            foreach (var b in Document.Blocks)
            {
                var c = BuildBlock(b, 0);
                if (c == null) continue;
                root.Children.Add(c);
                lineAnchors.Add(new KeyValuePair<int, Control>(b.SourceLine, c));
            }
            RenderCount++;
            Dispatcher.UIThread.Post(() => scroll.Offset = offset, DispatcherPriority.Background);
        }

        /// <summary>Scrolls so the block at (or just before) the editor's top line is at the top of the preview.</summary>
        public void SyncToSourceLine(int topLine)
        {
            if (topLine == lastSyncedLine || lineAnchors.Count == 0) return;
            lastSyncedLine = topLine;
            int idx = -1;
            for (int i = 0; i < lineAnchors.Count; i++) { if (lineAnchors[i].Key <= topLine) idx = i; else break; }
            if (idx < 0) { scroll.Offset = new Vector(scroll.Offset.X, 0); return; }
            var a = lineAnchors[idx];
            double y = YOf(a.Value);
            if (idx + 1 < lineAnchors.Count)
            {
                var b = lineAnchors[idx + 1];
                int span = Math.Max(1, b.Key - a.Key);
                double frac = Math.Min(1.0, (topLine - a.Key) / (double)span);
                y += (YOf(b.Value) - y) * frac;
            }
            scroll.Offset = new Vector(scroll.Offset.X, Math.Max(0, y - 8));
        }

        public bool ScrollToAnchor(string anchor)
        {
            Control c;
            if (string.IsNullOrEmpty(anchor) || !headingAnchors.TryGetValue(anchor, out c)) return false;
            scroll.Offset = new Vector(scroll.Offset.X, Math.Max(0, YOf(c) - 8));
            return true;
        }

        public double VerticalOffset => scroll.Offset.Y;

        private double YOf(Control c)
        {
            var p = c.TranslatePoint(new Point(0, 0), root);
            return p.HasValue ? p.Value.Y + root.Margin.Top : 0;
        }

        private IBrush Fg => AvaloniaColorHelper.ToBrush(theme.FgPrimary);
        private IBrush FgMuted => AvaloniaColorHelper.ToBrush(theme.FgSecondary);

        private Control BuildBlock(MdBlock b, int depth)
        {
            switch (b.Kind)
            {
                case MdBlockKind.Heading:
                    {
                        double[] sizes = { 26, 21, 17.5, 15.5, 14, 13 };
                        var tb = NewTextBlock(sizes[Math.Max(1, Math.Min(6, b.Level)) - 1], FontWeight.Bold);
                        AddInlines(tb.Inlines, b.Inlines, false, false, false);
                        Control result = tb;
                        if (b.Level <= 2)
                            result = new Border { Child = tb, BorderBrush = AvaloniaColorHelper.ToBrush(theme.BorderSubtle), BorderThickness = new Thickness(0, 0, 0, 1), Padding = new Thickness(0, 0, 0, 5), Margin = new Thickness(0, 8, 0, 0) };
                        else tb.Margin = new Thickness(0, 6, 0, 0);
                        if (!string.IsNullOrEmpty(b.Anchor) && !headingAnchors.ContainsKey(b.Anchor)) headingAnchors[b.Anchor] = result;
                        return result;
                    }
                case MdBlockKind.Paragraph:
                    {
                        // A paragraph made of a single image renders as the image itself.
                        if (b.Inlines.Count == 1 && b.Inlines[0].Kind == MdInlineKind.Image) return BuildImage(b.Inlines[0]);
                        var images = b.Inlines.Where(i => i.Kind == MdInlineKind.Image).ToList();
                        var tb = NewTextBlock(14, FontWeight.Normal);
                        AddInlines(tb.Inlines, b.Inlines, false, false, false);
                        if (images.Count == 0) return tb;
                        var wrap = new WrapPanel { Orientation = Orientation.Horizontal };
                        foreach (var im in images) { var ic = BuildImage(im); ic.Margin = new Thickness(0, 0, 6, 4); wrap.Children.Add(ic); }
                        bool onlyImagesAndSpace = b.Inlines.All(i => i.Kind == MdInlineKind.Image || (i.Kind == MdInlineKind.Text && i.Text.Trim().Length == 0) || i.Kind == MdInlineKind.LineBreak);
                        if (onlyImagesAndSpace) return wrap;
                        return new StackPanel { Spacing = 4, Children = { tb, wrap } };
                    }
                case MdBlockKind.CodeBlock:
                    {
                        var stb = new SelectableTextBlock { FontFamily = new FontFamily(DevToolsFonts.MonoFamily), FontSize = 12.5, TextWrapping = TextWrapping.NoWrap };
                        var lines = (b.Code ?? "").Split('\n');
                        var inl = new InlineCollection();
                        for (int i = 0; i < lines.Length; i++)
                        {
                            if (i > 0) inl.Add(new LineBreak());
                            string line = lines[i].Replace("\t", "    ");
                            if (line.Length == 0) continue;
                            foreach (var tk in AvaloniaVirtualEditorCanvas.TokenizeLineForTheme(line, theme))
                            {
                                var run = new Run(tk.Text) { Foreground = new SolidColorBrush(tk.Color) };
                                if (tk.IsBold) run.FontWeight = FontWeight.SemiBold;
                                if (tk.IsItalic) run.FontStyle = FontStyle.Italic;
                                inl.Add(run);
                            }
                        }
                        stb.Inlines = inl;
                        var sv = new ScrollViewer { Content = stb, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto, VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled };
                        return new Border
                        {
                            Child = sv,
                            Background = AvaloniaColorHelper.ToBrush(theme.IsLight ? theme.BgSidebar : theme.BgWorkbench),
                            BorderBrush = AvaloniaColorHelper.ToBrush(theme.BorderSubtle),
                            BorderThickness = new Thickness(1),
                            CornerRadius = new CornerRadius(5),
                            Padding = new Thickness(12, 10)
                        };
                    }
                case MdBlockKind.HorizontalRule:
                    return new Border { Height = 2, Background = AvaloniaColorHelper.ToBrush(theme.BorderSubtle), Margin = new Thickness(0, 6) };
                case MdBlockKind.BlockQuote:
                    {
                        var sp = new StackPanel { Spacing = 8 };
                        foreach (var c in b.Children) { var cc = BuildBlock(c, depth + 1); if (cc != null) sp.Children.Add(cc); }
                        SetForegroundRecursive(sp, FgMuted);
                        return new Border
                        {
                            Child = sp,
                            BorderBrush = AvaloniaColorHelper.ToBrush(theme.AccentPrimary, 160),
                            BorderThickness = new Thickness(4, 0, 0, 0),
                            Padding = new Thickness(14, 4, 4, 4),
                            Background = AvaloniaColorHelper.ToBrush(theme.BgSidebar, 120)
                        };
                    }
                case MdBlockKind.List:
                    {
                        var sp = new StackPanel { Spacing = 4, Margin = new Thickness(depth == 0 ? 6 : 0, 0, 0, 0) };
                        int num = b.StartNumber;
                        foreach (var item in b.Items)
                        {
                            var g = new Grid { ColumnDefinitions = new ColumnDefinitions("26,*") };
                            string marker = item.IsTask ? (item.IsChecked ? "☑" : "☐") : (b.Ordered ? (num++).ToString(CultureInfo.InvariantCulture) + "." : (depth % 3 == 0 ? "•" : (depth % 3 == 1 ? "◦" : "▪")));
                            var mk = new TextBlock { Text = marker, FontSize = item.IsTask ? 15 : 14, Foreground = item.IsTask && item.IsChecked ? AvaloniaColorHelper.ToBrush(theme.AccentPrimary) : FgMuted, Margin = new Thickness(0, item.IsTask ? -1 : 1, 0, 0) };
                            g.Children.Add(mk);
                            var content = new StackPanel { Spacing = 4 };
                            foreach (var ib in item.Blocks) { var c = BuildBlock(ib, depth + 1); if (c != null) content.Children.Add(c); }
                            Grid.SetColumn(content, 1);
                            g.Children.Add(content);
                            sp.Children.Add(g);
                            lineAnchors.Add(new KeyValuePair<int, Control>(item.SourceLine, g));
                        }
                        return sp;
                    }
                case MdBlockKind.Table:
                    return BuildTable(b);
            }
            return null;
        }

        private static void SetForegroundRecursive(Control c, IBrush brush)
        {
            if (c is TextBlock tb && !(c is SelectableTextBlock)) tb.Foreground = brush;
            if (c is Panel p) foreach (var ch in p.Children) SetForegroundRecursive(ch, brush);
            if (c is Decorator d && d.Child != null) SetForegroundRecursive(d.Child, brush);
        }

        private Control BuildTable(MdBlock b)
        {
            int cols = Math.Max(b.TableHeader.Count, b.TableRows.Count > 0 ? b.TableRows.Max(r => r.Count) : 0);
            var g = new Grid();
            for (int c = 0; c < cols; c++) g.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));
            int rowCount = 1 + b.TableRows.Count;
            for (int r = 0; r < rowCount; r++) g.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            for (int r = 0; r < rowCount; r++)
            {
                var cells = r == 0 ? b.TableHeader : b.TableRows[r - 1];
                for (int c = 0; c < cols; c++)
                {
                    var tb = NewTextBlock(13.5, r == 0 ? FontWeight.SemiBold : FontWeight.Normal);
                    tb.MaxWidth = 420;
                    if (c < cells.Count) AddInlines(tb.Inlines, cells[c], false, false, false);
                    MdAlign al = c < b.TableAlign.Count ? b.TableAlign[c] : MdAlign.None;
                    tb.TextAlignment = al == MdAlign.Right ? TextAlignment.Right : (al == MdAlign.Center ? TextAlignment.Center : TextAlignment.Left);
                    var cell = new Border
                    {
                        Child = tb,
                        Padding = new Thickness(10, 5),
                        BorderBrush = AvaloniaColorHelper.ToBrush(theme.BorderSubtle),
                        BorderThickness = new Thickness(c == 0 ? 1 : 0, r == 0 ? 1 : 0, 1, 1),
                        Background = r == 0 ? AvaloniaColorHelper.ToBrush(theme.BgSidebar) : (r % 2 == 0 ? AvaloniaColorHelper.ToBrush(theme.BgHover, 60) : Brushes.Transparent)
                    };
                    Grid.SetRow(cell, r);
                    Grid.SetColumn(cell, c);
                    g.Children.Add(cell);
                }
            }
            return new ScrollViewer { Content = g, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto, VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled };
        }

        private TextBlock NewTextBlock(double size, FontWeight weight)
        {
            return new TextBlock { FontSize = size, FontWeight = weight, TextWrapping = TextWrapping.Wrap, Foreground = Fg, LineHeight = size * 1.45 };
        }

        private void AddInlines(InlineCollection target, List<MdInline> inlines, bool bold, bool italic, bool strike)
        {
            foreach (var i in inlines)
            {
                switch (i.Kind)
                {
                    case MdInlineKind.Text:
                        {
                            var run = new Run(i.Text);
                            if (bold) run.FontWeight = FontWeight.Bold;
                            if (italic) run.FontStyle = FontStyle.Italic;
                            if (strike) run.TextDecorations = TextDecorations.Strikethrough;
                            target.Add(run);
                            break;
                        }
                    case MdInlineKind.Strong: AddInlines(target, i.Children, true, italic, strike); break;
                    case MdInlineKind.Emphasis: AddInlines(target, i.Children, bold, true, strike); break;
                    case MdInlineKind.Strikethrough: AddInlines(target, i.Children, bold, italic, true); break;
                    case MdInlineKind.LineBreak: target.Add(new LineBreak()); break;
                    case MdInlineKind.Code:
                        target.Add(new Run(i.Text)
                        {
                            FontFamily = new FontFamily(DevToolsFonts.MonoFamily),
                            Background = AvaloniaColorHelper.ToBrush(theme.BgHover),
                            Foreground = AvaloniaColorHelper.ToBrush(theme.TokenString)
                        });
                        break;
                    case MdInlineKind.Image:
                        break; // rendered as image controls by the paragraph builder
                    case MdInlineKind.Link:
                        {
                            var captured = i;
                            var linkText = new TextBlock
                            {
                                Text = MarkdownParser.ToPlainText(i.Children),
                                Foreground = AvaloniaColorHelper.ToBrush(theme.AccentPrimary),
                                TextDecorations = TextDecorations.Underline,
                                Cursor = CursorHelper.GetCursor(StandardCursorType.Hand),
                                FontWeight = bold ? FontWeight.Bold : FontWeight.Normal,
                                FontStyle = italic ? FontStyle.Italic : FontStyle.Normal
                            };
                            if (linkText.Text.Length == 0) linkText.Text = i.Url;
                            ToolTip.SetTip(linkText, string.IsNullOrEmpty(i.Title) ? i.Url : i.Title + "\n" + i.Url);
                            linkText.PointerPressed += (s, e) =>
                            {
                                if (!e.GetCurrentPoint(linkText).Properties.IsLeftButtonPressed) return;
                                e.Handled = true;
                                ActivateLink(captured.Url);
                            };
                            target.Add(new InlineUIContainer(linkText) { BaselineAlignment = BaselineAlignment.TextBottom });
                            break;
                        }
                }
            }
        }

        public void ActivateLink(string url)
        {
            var t = MarkdownLinkResolver.Resolve(url, FilePath);
            if (t.Kind == MdLinkTarget.KindAnchor) { ScrollToAnchor(t.Anchor); }
            LinkClicked?.Invoke(url, t);
        }

        private Control BuildImage(MdInline img)
        {
            var placeholder = new Border
            {
                Padding = new Thickness(8, 4),
                BorderBrush = AvaloniaColorHelper.ToBrush(theme.BorderSubtle),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(3),
                HorizontalAlignment = HorizontalAlignment.Left,
                Child = new TextBlock { Text = "🖼 " + (string.IsNullOrEmpty(img.Text) ? img.Url : img.Text), FontSize = 12, Foreground = FgMuted, TextWrapping = TextWrapping.Wrap }
            };
            ToolTip.SetTip(placeholder, img.Url);
            string local = MarkdownLinkResolver.ResolveLocalImage(img.Url, FilePath);
            if (local != null)
            {
                if (local.EndsWith(".svg", StringComparison.OrdinalIgnoreCase)) return placeholder;
                try
                {
                    if (File.Exists(local))
                    {
                        var bmp = new Bitmap(local);
                        return MakeImage(bmp, img);
                    }
                }
                catch { }
                return placeholder;
            }
            if (MarkdownLinkResolver.IsRemoteImage(img.Url))
            {
                Bitmap cached;
                lock (remoteImageCache) remoteImageCache.TryGetValue(img.Url, out cached);
                if (cached != null) return MakeImage(cached, img);
                string reason;
                if (!NetworkLedger.Check(NetworkFeature, "GET", img.Url, out reason))
                {
                    ((TextBlock)placeholder.Child).Text = "🖼 " + (string.IsNullOrEmpty(img.Text) ? "image" : img.Text) + " (remote image not loaded: Offline Mode)";
                    return placeholder;
                }
                if (img.Url.EndsWith(".svg", StringComparison.OrdinalIgnoreCase) || img.Url.Contains(".svg?")) return placeholder;
                var host = new ContentControl { Content = placeholder };
                var ui = UiPost.CaptureCurrent();
                string url = img.Url;
                Task.Run(async () =>
                {
                    // Every redirect hop is checked against Offline Mode and recorded in the Network ledger.
                    byte[] data = await Task.Run(() => LedgerHttp.DownloadBytes(NetworkFeature, url, 15000, 20 * 1024 * 1024)).ConfigureAwait(false);
                    if (data == null || data.Length > 20 * 1024 * 1024) return;
                    ui.Post(() =>
                    {
                        try
                        {
                            var bmp = new Bitmap(new MemoryStream(data));
                            lock (remoteImageCache) remoteImageCache[url] = bmp;
                            host.Content = MakeImage(bmp, img);
                        }
                        catch { }
                    });
                });
                return host;
            }
            return placeholder;
        }

        private static Control MakeImage(Bitmap bmp, MdInline img)
        {
            var image = new Image { Source = bmp, Stretch = Stretch.Uniform, MaxWidth = Math.Min(900, bmp.Size.Width), HorizontalAlignment = HorizontalAlignment.Left };
            if (!string.IsNullOrEmpty(img.Text)) ToolTip.SetTip(image, img.Text);
            return image;
        }
    }
}
