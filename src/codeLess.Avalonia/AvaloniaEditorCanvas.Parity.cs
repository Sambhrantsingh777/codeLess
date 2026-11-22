using System;
using System.Globalization;
using Avalonia;
using Avalonia.Media;
using codeLess.Studio;

namespace codeLess.AvaloniaApp
{
    public partial class AvaloniaEditorControl
    {
        /// <summary>Click on a gutter git-diff bar: (buffer, 0-based line, bar rectangle in canvas coordinates).</summary>
        public event Action<EditorTabBuffer, int, Rect> GutterDiffClicked;

        public static bool HasGutterDiffMarker(EditorTabBuffer buf, int line)
        {
            if (buf == null || line < 0) return false;
            if (buf.GutterAddedLines != null && buf.GutterAddedLines.Contains(line)) return true;
            if (buf.GutterModifiedLines != null && buf.GutterModifiedLines.Contains(line)) return true;
            if (buf.GutterDeletedAfterLine != null && buf.GutterDeletedAfterLine.Contains(line)) return true;
            return false;
        }

        internal bool RaiseGutterDiffClicked(EditorTabBuffer buf, int line, Rect bar)
        {
            var h = GutterDiffClicked;
            if (h == null || !HasGutterDiffMarker(buf, line)) return false;
            h(buf, line, bar);
            return true;
        }
    }

    /// <summary>Merge-conflict line backgrounds and segmented code lenses (Accept Current | Accept Incoming | ...).</summary>
    public partial class AvaloniaVirtualEditorCanvas
    {
        // VS Code merge colors: current = green, incoming = blue, common ancestor = gray.
        private static readonly IBrush ConflictCurrentHeaderBg = new SolidColorBrush(Color.FromArgb(128, 64, 200, 174));
        private static readonly IBrush ConflictCurrentBg = new SolidColorBrush(Color.FromArgb(51, 64, 200, 174));
        private static readonly IBrush ConflictIncomingHeaderBg = new SolidColorBrush(Color.FromArgb(128, 64, 166, 255));
        private static readonly IBrush ConflictIncomingBg = new SolidColorBrush(Color.FromArgb(51, 64, 166, 255));
        private static readonly IBrush ConflictBaseHeaderBg = new SolidColorBrush(Color.FromArgb(102, 96, 96, 96));
        private static readonly IBrush ConflictBaseBg = new SolidColorBrush(Color.FromArgb(41, 96, 96, 96));

        public static IBrush ConflictBrushFor(ConflictLineKind kind)
        {
            switch (kind)
            {
                case ConflictLineKind.CurrentHeader: return ConflictCurrentHeaderBg;
                case ConflictLineKind.Current: return ConflictCurrentBg;
                case ConflictLineKind.IncomingHeader: return ConflictIncomingHeaderBg;
                case ConflictLineKind.Incoming: return ConflictIncomingBg;
                case ConflictLineKind.BaseHeader: return ConflictBaseHeaderBg;
                case ConflictLineKind.Base: return ConflictBaseBg;
                default: return null;
            }
        }

        private void DrawConflictLineBackground(DrawingContext context, EditorTabBuffer buf, int docLine, double y, double codeAreaRight)
        {
            if (buf == null || buf.IsLargeFile && buf.Lines.Count > 200000) return;
            var kind = ConflictDecorations.KindAt(buf, docLine);
            if (kind == ConflictLineKind.None || kind == ConflictLineKind.Separator) return;
            var b = ConflictBrushFor(kind);
            if (b != null) context.FillRectangle(b, new Rect(GutterWidth + 1, y, Math.Max(0, codeAreaRight - GutterWidth - 1), LineHeight));
        }

        /// <summary>Draws an annotation with clickable segments; each part gets its own hit box.</summary>
        private void DrawSegmentedAnnotation(DrawingContext context, EditorLineAnnotation ann, int docLine, double ax, double y, double codeAreaRight)
        {
            var parts = ann.GetSegmentParts();
            if (parts == null) return;
            double x = ax;
            var sepBrush = new SolidColorBrush(AvaloniaColorHelper.ToAvColor(Theme.TextSecondary, (byte)150));
            for (int i = 0; i < parts.Length; i++)
            {
                if (x > codeAreaRight - 30) break;
                if (i > 0)
                {
                    var sep = new FormattedText(EditorLineAnnotation.SegmentSeparator, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, CachedUiFontBold, FontSizePx - 1.5, sepBrush);
                    context.DrawText(sep, new Point(x, y + (LineHeight - sep.Height) / 2.0));
                    x += sep.WidthIncludingTrailingWhitespace;
                }
                var p = parts[i];
                bool hot = ReferenceEquals(p, hoveredAnnotation);
                IBrush br = new SolidColorBrush(AvaloniaColorHelper.ToAvColor(Theme.AccentPrimary, (byte)(hot ? 255 : 205)));
                var ft = new FormattedText(p.Text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, CachedUiFontBold, FontSizePx - 1.5, br);
                ft.MaxTextWidth = Math.Max(20, codeAreaRight - x - 8);
                ft.MaxLineCount = 1;
                ft.Trimming = TextTrimming.CharacterEllipsis;
                if (hot) ft.SetTextDecorations(TextDecorations.Underline);
                context.DrawText(ft, new Point(x, y + (LineHeight - ft.Height) / 2.0));
                double w = Math.Min(ft.WidthIncludingTrailingWhitespace, ft.MaxTextWidth);
                annotationHitBoxes.Add((new Rect(x, y, w, LineHeight), docLine, p));
                x += w;
            }
        }
    }
}
