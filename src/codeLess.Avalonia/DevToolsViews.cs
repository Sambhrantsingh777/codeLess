using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using codeLess.Studio;

namespace codeLess.AvaloniaApp
{
    /// <summary>Custom-drawn, virtualized side-by-side / inline diff with syntax colors from the editor tokenizer.</summary>
    public class DiffRowsControl : VirtualRowsControl
    {
        private List<LineDiffRow> rows = new List<LineDiffRow>();
        public bool SideBySide { get; private set; } = true;
        private string oldText = "", newText = "";
        public string Note = "";

        public override int RowCount => rows.Count;
        public IReadOnlyList<LineDiffRow> Rows => rows;

        public DiffRowsControl() { RowHeight = 19; }

        public void SetTexts(string oldT, string newT, bool sideBySide)
        {
            oldText = oldT ?? "";
            newText = newT ?? "";
            SideBySide = sideBySide;
            rows = sideBySide ? LineDiff.SideBySide(oldText, newText) : LineDiff.Inline(oldText, newText);
            ResetRows();
        }

        public void SetMode(bool sideBySide)
        {
            if (sideBySide == SideBySide) return;
            SetTexts(oldText, newText, sideBySide);
        }

        /// <summary>Index of the next/previous changed block from the current scroll position.</summary>
        public void JumpChange(int dir)
        {
            int start = Math.Max(0, (int)(ScrollOffset / RowHeight)) + (dir > 0 ? 2 : -1);
            for (int i = start; i >= 0 && i < rows.Count; i += dir)
            {
                bool changed = rows[i].Kind != LineDiffRow.Equal;
                bool prevSame = i > 0 && rows[i - 1].Kind != LineDiffRow.Equal;
                if (changed && !prevSame) { ScrollTo(Math.Max(0, (i - 3) * RowHeight)); return; }
            }
        }

        protected override void DrawEmpty(DrawingContext ctx)
        {
            var ft = Text(string.IsNullOrEmpty(Note) ? "No differences." : Note, DevToolsFonts.Ui, 12.5, AvaloniaColorHelper.ToBrush(Theme.FgMuted));
            ctx.DrawText(ft, new Point(16, 14));
        }

        protected override void DrawRow(DrawingContext ctx, int index, Rect r, bool selected)
        {
            var row = rows[index];
            bool light = Theme.IsLight;
            var addBg = new SolidColorBrush(light ? Color.FromArgb(70, 46, 160, 67) : Color.FromArgb(55, 46, 160, 67));
            var delBg = new SolidColorBrush(light ? Color.FromArgb(70, 220, 50, 50) : Color.FromArgb(60, 241, 76, 76));
            var numBrush = AvaloniaColorHelper.ToBrush(Theme.FgMuted);
            double numW = 44;
            if (SideBySide)
            {
                double half = r.Width / 2;
                var left = new Rect(r.X, r.Y, half - 1, r.Height);
                var right = new Rect(r.X + half + 1, r.Y, half - 1, r.Height);
                if (row.LeftLine >= 0 && row.Kind != LineDiffRow.Equal) ctx.FillRectangle(delBg, left);
                if (row.RightLine >= 0 && row.Kind != LineDiffRow.Equal) ctx.FillRectangle(addBg, right);
                if (row.LeftLine < 0) ctx.FillRectangle(AvaloniaColorHelper.ToBrush(Theme.BgSidebar, 160), left);
                if (row.RightLine < 0) ctx.FillRectangle(AvaloniaColorHelper.ToBrush(Theme.BgSidebar, 160), right);
                ctx.DrawLine(new Pen(AvaloniaColorHelper.ToBrush(Theme.BorderSubtle), 1), new Point(r.X + half, r.Y), new Point(r.X + half, r.Bottom));
                if (row.LeftLine >= 0)
                {
                    DrawNum(ctx, row.LeftLine + 1, left.X, r, numW, numBrush);
                    using (ctx.PushClip(left)) DrawCode(ctx, row.LeftText, left.X + numW + 6, r);
                }
                if (row.RightLine >= 0)
                {
                    DrawNum(ctx, row.RightLine + 1, right.X, r, numW, numBrush);
                    using (ctx.PushClip(right)) DrawCode(ctx, row.RightText, right.X + numW + 6, r);
                }
            }
            else
            {
                if (row.Kind == LineDiffRow.Removed) ctx.FillRectangle(delBg, r);
                else if (row.Kind == LineDiffRow.Added) ctx.FillRectangle(addBg, r);
                if (row.LeftLine >= 0) DrawNum(ctx, row.LeftLine + 1, r.X, r, numW, numBrush);
                if (row.RightLine >= 0) DrawNum(ctx, row.RightLine + 1, r.X + numW, r, numW, numBrush);
                string sign = row.Kind == LineDiffRow.Removed ? "−" : (row.Kind == LineDiffRow.Added ? "+" : " ");
                ctx.DrawText(Text(sign, DevToolsFonts.Mono, 12, numBrush), new Point(r.X + numW * 2 + 4, r.Y + 2));
                DrawCode(ctx, row.Kind == LineDiffRow.Removed ? row.LeftText : row.RightText, r.X + numW * 2 + 18, r);
            }
            if (selected) ctx.DrawRectangle(null, new Pen(AvaloniaColorHelper.ToBrush(Theme.AccentPrimary, 120), 1), r.Deflate(0.5));
        }

        private static void DrawNum(DrawingContext ctx, int n, double x, Rect r, double w, IBrush b)
        {
            var ft = Text(n.ToString(CultureInfo.InvariantCulture), DevToolsFonts.Mono, 11, b);
            ctx.DrawText(ft, new Point(x + w - ft.Width - 4, r.Y + (r.Height - ft.Height) / 2));
        }

        private void DrawCode(DrawingContext ctx, string line, double x, Rect r)
        {
            if (string.IsNullOrEmpty(line)) return;
            string t = line.Replace("\t", "    ");
            if (t.Length > 400) t = t.Substring(0, 400);
            double cw = 7.2;
            var probe = Text("MMMMMMMMMM", DevToolsFonts.Mono, 12, Brushes.White);
            if (probe.Width > 30) cw = probe.Width / 10.0;
            double cx = x;
            foreach (var tk in AvaloniaVirtualEditorCanvas.TokenizeLineForTheme(t, Theme))
            {
                if (cx > r.Right) break;
                var ft = Text(tk.Text, tk.IsBold ? DevToolsFonts.MonoBold : (tk.IsItalic ? DevToolsFonts.MonoItalic : DevToolsFonts.Mono), 12, new SolidColorBrush(tk.Color));
                ctx.DrawText(ft, new Point(cx, r.Y + (r.Height - ft.Height) / 2));
                cx += (tk.Text ?? "").Length * cw;
            }
        }
    }

    /// <summary>Diff document: header (title, +/− counts, mode toggle, prev/next change) and the rows control.</summary>
    public class DiffView : Grid
    {
        public DiffRowsControl Rows { get; } = new DiffRowsControl();
        private readonly TextBlock titleText = new TextBlock { FontSize = 12, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
        private readonly TextBlock statsText = new TextBlock { FontSize = 11.5, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0) };
        private readonly Button modeBtn;
        private readonly Border header;

        public DiffView(string title, string oldText, string newText, string note, bool sideBySide = true)
        {
            RowDefinitions = new RowDefinitions("30,*");
            titleText.Text = title;
            modeBtn = new Button { FontSize = 11, Padding = new Thickness(8, 2), Margin = new Thickness(4, 0), VerticalAlignment = VerticalAlignment.Center };
            modeBtn.Click += (s, e) => { Rows.SetMode(!Rows.SideBySide); UpdateHeader(); };
            var prev = WorkbenchStyles.IconButton("arrow-up", "Previous Change", () => Rows.JumpChange(-1));
            var next = WorkbenchStyles.IconButton("arrow-down", "Next Change", () => Rows.JumpChange(1));
            next.Margin = new Thickness(2, 0, 8, 0);
            var hg = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto,Auto,Auto"), Margin = new Thickness(10, 0, 0, 0) };
            hg.Children.Add(titleText);
            Grid.SetColumn(statsText, 1); hg.Children.Add(statsText);
            Grid.SetColumn(modeBtn, 2); hg.Children.Add(modeBtn);
            Grid.SetColumn(prev, 3); hg.Children.Add(prev);
            Grid.SetColumn(next, 4); hg.Children.Add(next);
            header = new Border { Child = hg, BorderThickness = new Thickness(0, 0, 0, 1) };
            Children.Add(header);
            Grid.SetRow(Rows, 1);
            Children.Add(Rows);
            Rows.Note = note ?? "";
            Rows.SetTexts(oldText, newText, sideBySide);
            UpdateHeader();
        }

        private void UpdateHeader()
        {
            int add, rem;
            LineDiff.CountChanges(Rows.Rows.ToList(), out add, out rem);
            statsText.Text = "+" + add + "  −" + rem;
            modeBtn.Content = Rows.SideBySide ? "Inline View" : "Side by Side";
        }

        public void ApplyTheme(StudioTheme t)
        {
            Background = AvaloniaColorHelper.ToBrush(t.BgEditor);
            header.Background = AvaloniaColorHelper.ToBrush(t.BgSidebar);
            header.BorderBrush = AvaloniaColorHelper.ToBrush(t.BorderSubtle);
            titleText.Foreground = AvaloniaColorHelper.ToBrush(t.FgPrimary);
            statsText.Foreground = AvaloniaColorHelper.ToBrush(t.FgSecondary);
            Rows.ApplyTheme(t);
        }
    }

    /// <summary>Response panel for the .http client: status, timing, size, headers and a pretty, colored body.</summary>
    public class HttpResponseView : Grid
    {
        private readonly Border header;
        private readonly Border statusBadge;
        private readonly TextBlock statusText = new TextBlock { FontSize = 12, FontWeight = FontWeight.SemiBold, VerticalAlignment = VerticalAlignment.Center };
        private readonly TextBlock metaText = new TextBlock { FontSize = 11.5, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0), TextTrimming = TextTrimming.CharacterEllipsis };
        private readonly Button cancelBtn, curlBtn, copyBtn, resendBtn;
        private readonly Button bodyTab, headersTab, requestTab;
        private readonly SelectableTextBlock bodyText = new SelectableTextBlock { TextWrapping = TextWrapping.NoWrap, FontSize = 12.5, Margin = new Thickness(12, 8) };
        private readonly ScrollViewer bodyScroll;
        private StudioTheme theme = StudioTheme.GetById(StudioTheme.DefaultThemeId);
        private string activeTab = "body";
        private CancellationTokenSource runningCts;

        public HttpResponseResult Result { get; private set; }
        public HttpResolvedRequest Request { get; private set; }
        public bool IsRunning => runningCts != null;
        public string BodyDisplayText => bodyText.Inlines != null && bodyText.Inlines.Count > 0 ? string.Concat(bodyText.Inlines.OfType<Run>().Select(r => r.Text)) : (bodyText.Text ?? "");
        public string StatusLine => statusText.Text ?? "";
        public event Action ResendRequested;
        /// <summary>Copies text to the clipboard (supplied by the window).</summary>
        public Action<string> CopyToClipboard;

        public HttpResponseView()
        {
            RowDefinitions = new RowDefinitions("Auto,28,*");
            bodyText.FontFamily = new FontFamily(DevToolsFonts.MonoFamily);
            statusBadge = new Border { Child = statusText, Padding = new Thickness(8, 2), CornerRadius = new CornerRadius(3), VerticalAlignment = VerticalAlignment.Center };
            cancelBtn = SmallButton("Cancel", "Cancel the running request");
            curlBtn = SmallButton("Copy as cURL", "Copy this request as a curl command");
            copyBtn = SmallButton("Copy Body", "Copy the response body");
            resendBtn = SmallButton("Send Again", "Send the request again");
            cancelBtn.Click += (s, e) => { try { runningCts?.Cancel(); } catch { } };
            curlBtn.Click += (s, e) => { if (Request != null) CopyToClipboard?.Invoke(HttpClientRunner.ToCurl(Request)); };
            copyBtn.Click += (s, e) => { if (Result != null) CopyToClipboard?.Invoke(Result.Body ?? ""); };
            resendBtn.Click += (s, e) => ResendRequested?.Invoke();
            var hg = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto,Auto,Auto,Auto"), Margin = new Thickness(10, 6, 8, 6) };
            hg.Children.Add(statusBadge);
            Grid.SetColumn(metaText, 1); hg.Children.Add(metaText);
            Grid.SetColumn(cancelBtn, 2); hg.Children.Add(cancelBtn);
            Grid.SetColumn(curlBtn, 3); hg.Children.Add(curlBtn);
            Grid.SetColumn(copyBtn, 4); hg.Children.Add(copyBtn);
            Grid.SetColumn(resendBtn, 5); hg.Children.Add(resendBtn);
            header = new Border { Child = hg, BorderThickness = new Thickness(0, 0, 0, 1) };
            Children.Add(header);

            bodyTab = TabButton("BODY", "body");
            headersTab = TabButton("HEADERS", "headers");
            requestTab = TabButton("REQUEST", "request");
            var tabs = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, Margin = new Thickness(10, 0), Children = { bodyTab, headersTab, requestTab } };
            Grid.SetRow(tabs, 1);
            Children.Add(tabs);
            bodyScroll = new ScrollViewer
            {
                Content = bodyText,
                HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
                VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto
            };
            Grid.SetRow(bodyScroll, 2);
            Children.Add(bodyScroll);
            cancelBtn.IsVisible = false;
        }

        private static Button SmallButton(string text, string tip)
        {
            var b = new Button { Content = text, FontSize = UiTokens.FontSmall, Padding = new Thickness(8, 2), MinHeight = 22, Margin = new Thickness(4, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
            ToolTip.SetTip(b, tip);
            return b;
        }

        private Button TabButton(string text, string id)
        {
            var b = new Button { Content = text, FontSize = UiTokens.FontHeader, FontWeight = FontWeight.Normal, Padding = new Thickness(6, 0), MinHeight = 0, VerticalAlignment = VerticalAlignment.Stretch, VerticalContentAlignment = VerticalAlignment.Center, CornerRadius = new CornerRadius(0), Background = Brushes.Transparent, BorderThickness = new Thickness(0, 0, 0, UiTokens.PanelTabIndicatorWidth) };
            b.Classes.Add("paneltab");
            b.Click += (s, e) => { activeTab = id; Render(); };
            return b;
        }

        /// <summary>Shows the "sending" state; returns the token the caller must pass to the runner.</summary>
        public CancellationToken BeginRequest(HttpResolvedRequest req)
        {
            try { runningCts?.Cancel(); } catch { }
            runningCts = new CancellationTokenSource();
            Request = req;
            Result = null;
            cancelBtn.IsVisible = true;
            statusText.Text = "Sending…";
            metaText.Text = req.Method + " " + req.Url;
            activeTab = "body";
            Render();
            return runningCts.Token;
        }

        public void ShowResult(HttpResponseResult r)
        {
            runningCts = null;
            cancelBtn.IsVisible = false;
            Result = r;
            Request = r.Request ?? Request;
            if (r.HasResponse)
            {
                statusText.Text = r.StatusCode.ToString(CultureInfo.InvariantCulture) + " " + r.ReasonPhrase;
                metaText.Text = r.ElapsedMs.ToString(CultureInfo.InvariantCulture) + " ms  ·  " + HttpClientRunner.FormatSize(r.BodyBytes) + "  ·  " + (Request != null ? Request.Method + " " + Request.Url : "");
            }
            else
            {
                statusText.Text = r.Blocked ? "Blocked" : (r.Cancelled ? "Cancelled" : "Error");
                metaText.Text = r.Error;
            }
            Render();
        }

        private void Render()
        {
            bodyText.Inlines?.Clear();
            bodyText.Text = null;
            ApplyTabStyles();
            UpdateBadge();
            if (activeTab == "headers")
            {
                bodyText.Text = Result != null && Result.HasResponse ? HttpClientRunner.FormatHeaders(Result) : (Result != null ? Result.Error : "");
                return;
            }
            if (activeTab == "request")
            {
                if (Request == null) return;
                var sb = new System.Text.StringBuilder();
                sb.Append(Request.Method).Append(' ').Append(Request.Url).Append('\n');
                foreach (var h in Request.Headers) sb.Append(h.Name).Append(": ").Append(h.Value).Append('\n');
                if (!string.IsNullOrEmpty(Request.Body)) sb.Append('\n').Append(Request.Body);
                if (Request.UnresolvedVariables.Count > 0) sb.Append("\n\n# Unresolved variables: ").Append(string.Join(", ", Request.UnresolvedVariables));
                bodyText.Text = sb.ToString();
                return;
            }
            if (Result == null) { bodyText.Text = Request != null ? "Waiting for response…" : ""; return; }
            if (!Result.HasResponse)
            {
                string hint = Result.Blocked ? "\n\nTurn off Offline Mode (or target localhost) to send this request." : "";
                bodyText.Text = Result.Error + hint;
                return;
            }
            List<BodyToken> tokens;
            string pretty = BodyFormatter.FormatForDisplay(Result.ContentType, Result.Body ?? "", out tokens);
            if (tokens.Count == 0 || pretty.Length > 400000) { bodyText.Text = pretty; return; }
            var inl = new InlineCollection();
            int pos = 0;
            foreach (var tk in tokens)
            {
                if (tk.Start > pos) inl.Add(new Run(pretty.Substring(pos, tk.Start - pos)) { Foreground = AvaloniaColorHelper.ToBrush(theme.FgPrimary) });
                inl.Add(new Run(pretty.Substring(tk.Start, tk.Length)) { Foreground = new SolidColorBrush(ColorFor(tk.Kind)) });
                pos = tk.Start + tk.Length;
            }
            if (pos < pretty.Length) inl.Add(new Run(pretty.Substring(pos)) { Foreground = AvaloniaColorHelper.ToBrush(theme.FgPrimary) });
            bodyText.Inlines = inl;
        }

        private Color ColorFor(BodyTokenKind k)
        {
            switch (k)
            {
                case BodyTokenKind.Key: return AvaloniaColorHelper.ToAvColor(theme.TokenFunction);
                case BodyTokenKind.String: return AvaloniaColorHelper.ToAvColor(theme.TokenString);
                case BodyTokenKind.Number: return AvaloniaColorHelper.ToAvColor(theme.TokenNumber);
                case BodyTokenKind.Literal: return AvaloniaColorHelper.ToAvColor(theme.TokenKeyword);
                case BodyTokenKind.Tag: return AvaloniaColorHelper.ToAvColor(theme.TokenKeyword);
                case BodyTokenKind.AttributeName: return AvaloniaColorHelper.ToAvColor(theme.TokenType);
                case BodyTokenKind.Comment: return AvaloniaColorHelper.ToAvColor(theme.TokenComment);
                case BodyTokenKind.Punctuation: return AvaloniaColorHelper.ToAvColor(theme.FgSecondary);
                default: return AvaloniaColorHelper.ToAvColor(theme.FgPrimary);
            }
        }

        private void UpdateBadge()
        {
            Color c;
            if (Result == null) c = AvaloniaColorHelper.ToAvColor(theme.AccentPrimary);
            else if (!Result.HasResponse) c = Color.FromRgb(185, 28, 28);
            else if (Result.StatusCode < 300) c = Color.FromRgb(22, 140, 64);
            else if (Result.StatusCode < 400) c = Color.FromRgb(37, 99, 235);
            else if (Result.StatusCode < 500) c = Color.FromRgb(202, 138, 4);
            else c = Color.FromRgb(185, 28, 28);
            statusBadge.Background = new SolidColorBrush(c);
            statusText.Foreground = Brushes.White;
        }

        private void ApplyTabStyles()
        {
            foreach (var (b, id) in new[] { (bodyTab, "body"), (headersTab, "headers"), (requestTab, "request") })
            {
                bool on = id == activeTab;
                b.Foreground = AvaloniaColorHelper.ToBrush(on ? theme.FgPrimary : theme.FgMuted);
                b.BorderBrush = on ? AvaloniaColorHelper.ToBrush(theme.AccentPrimary) : Brushes.Transparent;
            }
        }

        public void ApplyTheme(StudioTheme t)
        {
            theme = t;
            Background = AvaloniaColorHelper.ToBrush(t.BgEditor);
            header.Background = AvaloniaColorHelper.ToBrush(t.BgSidebar);
            header.BorderBrush = AvaloniaColorHelper.ToBrush(t.BorderSubtle);
            metaText.Foreground = AvaloniaColorHelper.ToBrush(t.FgSecondary);
            bodyText.Foreground = AvaloniaColorHelper.ToBrush(t.FgPrimary);
            Render();
        }
    }
}
