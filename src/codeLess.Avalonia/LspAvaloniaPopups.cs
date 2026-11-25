using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using codeLess.Studio;

namespace codeLess.AvaloniaApp
{
    /// <summary>VS Code-style suggest widget: kind glyph, label, detail on the right, documentation side panel.</summary>
    public class LspCompletionPopup : Border
    {
        public const int VisibleRows = 12;
        public const double RowHeight = 21;

        private readonly StackPanel rows = new StackPanel();
        private readonly Border docPanel;
        private readonly TextBlock docText;
        private LspCompletionSession session;
        private int scrollTop;
        private StudioTheme theme = StudioTheme.GetById(StudioTheme.DefaultThemeId);

        /// <summary>Raised when an item is clicked (index into the session's visible list).</summary>
        public event Action<int> ItemClicked;

        public LspCompletionPopup()
        {
            HorizontalAlignment = HorizontalAlignment.Left;
            VerticalAlignment = VerticalAlignment.Top;
            BorderThickness = new Thickness(1);
            CornerRadius = new CornerRadius(UiTokens.ToolbarRadius);
            Padding = new Thickness(0, 2);
            IsVisible = false;
            ZIndex = 150;
            ClipToBounds = false;

            docText = new TextBlock { TextWrapping = TextWrapping.Wrap, FontSize = UiTokens.FontSmall, MaxWidth = 340 };
            docPanel = new Border
            {
                Padding = new Thickness(8, 6),
                BorderThickness = new Thickness(1, 0, 0, 0),
                Child = new ScrollViewer { Content = docText, MaxHeight = VisibleRows * RowHeight, VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto },
                IsVisible = false
            };
            var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,Auto") };
            rows.Width = 380;
            grid.Children.Add(rows);
            Grid.SetColumn(docPanel, 1);
            grid.Children.Add(docPanel);
            Child = grid;
            PointerWheelChanged += (s, e) =>
            {
                if (session == null) return;
                scrollTop = Math.Max(0, Math.Min(Math.Max(0, session.Visible.Count - VisibleRows), scrollTop - (int)Math.Round(e.Delta.Y * 3)));
                Render();
                e.Handled = true;
            };
        }

        public LspCompletionSession Session => session;
        public string DocumentationText => docPanel.IsVisible ? docText.Text : "";

        public void ApplyTheme(StudioTheme t)
        {
            if (t == null) return;
            theme = t;
            Background = WorkbenchStyles.B(UiTokens.WidgetBackground(t));
            BorderBrush = WorkbenchStyles.B(UiTokens.WidgetBorder(t));
            BoxShadow = WorkbenchStyles.WidgetShadow(t);
            docPanel.BorderBrush = WorkbenchStyles.B(UiTokens.WidgetBorder(t));
            docText.Foreground = AvaloniaColorHelper.ToBrush(t.TextSecondary);
            if (IsVisible) Render();
        }

        public void Show(LspCompletionSession s)
        {
            session = s;
            scrollTop = 0;
            IsVisible = true;
            SetDocumentation(null);
            Render();
        }

        public void Hide()
        {
            IsVisible = false;
            session = null;
            rows.Children.Clear();
        }

        public void SetDocumentation(string text)
        {
            text = (text ?? "").Trim();
            docText.Text = text;
            docPanel.IsVisible = text.Length > 0;
        }

        public void Render()
        {
            rows.Children.Clear();
            if (session == null) return;
            int sel = session.SelectedIndex;
            if (sel < scrollTop) scrollTop = sel;
            if (sel >= scrollTop + VisibleRows) scrollTop = sel - VisibleRows + 1;
            int end = Math.Min(session.Visible.Count, scrollTop + VisibleRows);
            var fg = AvaloniaColorHelper.ToBrush(theme.TextPrimary);
            var fg2 = AvaloniaColorHelper.ToBrush(theme.TextSecondary);
            var accent = AvaloniaColorHelper.ToBrush(theme.AccentPrimary);
            var selBg = WorkbenchStyles.B(UiTokens.ListActiveSelection(theme, UiTokens.WidgetBackground(theme)));
            for (int i = scrollTop; i < end; i++)
            {
                var it = session.Visible[i];
                int index = i;
                var row = new Grid { ColumnDefinitions = new ColumnDefinitions("22,*,Auto"), Height = RowHeight };
                Control glyph = CompletionKindIcons.ForItem(it, theme) ?? // files: type badge, folders: folder glyph (path completion)
                    new TextBlock { Text = KindGlyph(it.Kind), Foreground = KindBrush(it.Kind), FontSize = 11.5, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center };
                var label = new TextBlock
                {
                    Text = LspCompletionSession.LeftText(it),
                    Foreground = fg,
                    FontSize = UiTokens.FontUi,
                    VerticalAlignment = VerticalAlignment.Center,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    TextDecorations = it.Deprecated ? TextDecorations.Strikethrough : null
                };
                var right = new TextBlock { Text = LspCompletionSession.RightText(it), Foreground = fg2, FontSize = UiTokens.FontSmall, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 8, 0), MaxWidth = 170, TextTrimming = TextTrimming.CharacterEllipsis };
                row.Children.Add(glyph);
                Grid.SetColumn(label, 1); row.Children.Add(label);
                Grid.SetColumn(right, 2); row.Children.Add(right);
                var b = new Border { Child = row, Background = i == sel ? selBg : Brushes.Transparent, Cursor = CursorHelper.GetCursor(StandardCursorType.Hand) };
                b.PointerPressed += (s, e) => { ItemClicked?.Invoke(index); e.Handled = true; };
                rows.Children.Add(b);
            }
        }

        /// <summary>Kind glyph colors from the theme's syntax roles (VS Code: purple methods, blue members, orange types).</summary>
        private IBrush KindBrush(int kind)
        {
            switch (kind)
            {
                case 2: case 3: case 4: return AvaloniaColorHelper.ToBrush(theme.TokenFunction);
                case 5: case 6: case 10: return AvaloniaColorHelper.ToBrush(theme.TokenKeyword);
                case 7: case 8: case 22: case 13: case 20: return AvaloniaColorHelper.ToBrush(theme.TokenNumber);
                default: return AvaloniaColorHelper.ToBrush(theme.FgSecondary);
            }
        }

        public static string KindGlyph(int kind)
        {
            switch (kind)
            {
                case 2: case 3: case 4: return "ƒ";      // method, function, constructor
                case 5: case 10: return "◇";              // field, property
                case 6: return "𝑥";                       // variable
                case 7: case 8: case 22: return "◆";      // class, interface, struct
                case 9: return "▣";                       // module
                case 13: case 20: return "≡";             // enum, enum member
                case 14: return "⌘";                      // keyword
                case 15: return "⧉";                      // snippet
                case 17: case 19: return "▤";             // file, folder
                case 21: return "π";                      // constant
                default: return "·";
            }
        }
    }

    /// <summary>Non-interactive floating tip used for hover text and signature help.</summary>
    public class LspInfoTip : Border
    {
        private readonly TextBlock text;
        private StudioTheme theme = StudioTheme.GetById(StudioTheme.DefaultThemeId);

        public LspInfoTip()
        {
            HorizontalAlignment = HorizontalAlignment.Left;
            VerticalAlignment = VerticalAlignment.Top;
            BorderThickness = new Thickness(1);
            CornerRadius = new CornerRadius(UiTokens.ToolbarRadius);
            Padding = new Thickness(10, 6);
            IsVisible = false;
            IsHitTestVisible = false; // never steal pointer events from the editor (hover would flicker)
            ZIndex = 140;
            MaxWidth = 560;
            text = new TextBlock { TextWrapping = TextWrapping.Wrap, FontSize = 12.5 };
            Child = new ScrollViewer { Content = text, MaxHeight = 300, VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Hidden };
        }

        public string Text => text.Inlines != null && text.Inlines.Count > 0 ? string.Concat(InlineTexts()) : (text.Text ?? "");

        private IEnumerable<string> InlineTexts()
        {
            foreach (var i in text.Inlines) if (i is Run r) yield return r.Text;
        }

        public void ApplyTheme(StudioTheme t)
        {
            if (t == null) return;
            theme = t;
            Background = WorkbenchStyles.B(UiTokens.WidgetBackground(t));
            BorderBrush = WorkbenchStyles.B(UiTokens.WidgetBorder(t));
            BoxShadow = WorkbenchStyles.WidgetShadow(t);
            text.Foreground = AvaloniaColorHelper.ToBrush(t.TextPrimary);
        }

        public void ShowText(string s, Point at)
        {
            text.Inlines?.Clear();
            text.Text = s ?? "";
            Place(at);
            IsVisible = !string.IsNullOrEmpty(s);
        }

        /// <summary>Signature label with the active parameter in bold accent, followed by documentation.</summary>
        public void ShowSignature(LspSignatureHelp help, Point at)
        {
            var sig = help?.Active;
            if (sig == null) { Hide(); return; }
            text.Text = null;
            text.Inlines ??= new InlineCollection();
            text.Inlines.Clear();
            int[] span = help.ActiveParameterSpan;
            string label = sig.Label ?? "";
            if (span != null && span[0] >= 0 && span[1] <= label.Length && span[1] > span[0])
            {
                text.Inlines.Add(new Run(label.Substring(0, span[0])));
                text.Inlines.Add(new Run(label.Substring(span[0], span[1] - span[0])) { FontWeight = FontWeight.Bold, Foreground = AvaloniaColorHelper.ToBrush(theme.AccentPrimary), TextDecorations = TextDecorations.Underline });
                text.Inlines.Add(new Run(label.Substring(span[1])));
            }
            else text.Inlines.Add(new Run(label));
            if (help.Signatures.Count > 1)
                text.Inlines.Add(new Run("   (" + (help.ActiveSignature + 1) + "/" + help.Signatures.Count + ")") { Foreground = AvaloniaColorHelper.ToBrush(theme.TextSecondary) });
            int ap = sig.ActiveParameter >= 0 ? sig.ActiveParameter : help.ActiveParameter;
            string pdoc = ap >= 0 && ap < sig.ParameterDocs.Count ? sig.ParameterDocs[ap] : null;
            if (!string.IsNullOrEmpty(pdoc)) text.Inlines.Add(new Run("\n" + pdoc) { Foreground = AvaloniaColorHelper.ToBrush(theme.TextSecondary) });
            if (!string.IsNullOrEmpty(sig.Documentation)) text.Inlines.Add(new Run("\n" + sig.Documentation) { FontSize = 12 });
            Place(at);
            IsVisible = true;
        }

        /// <summary>When set, the tip is anchored with its bottom edge at the given Y (placed above a line).</summary>
        public bool AnchorAbove;
        public double HostHeight;

        private void Place(Point at)
        {
            double x = Math.Max(0, at.X);
            if (AnchorAbove && HostHeight > 0)
            {
                VerticalAlignment = VerticalAlignment.Bottom;
                Margin = new Thickness(x, 0, 0, Math.Max(0, HostHeight - at.Y));
            }
            else
            {
                VerticalAlignment = VerticalAlignment.Top;
                Margin = new Thickness(x, Math.Max(0, at.Y), 0, 0);
            }
        }

        public void Hide()
        {
            IsVisible = false;
        }
    }
}
