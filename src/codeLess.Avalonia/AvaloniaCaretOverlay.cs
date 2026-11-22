using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using codeLess.Studio;

namespace codeLess.AvaloniaApp
{
    /// <summary>
    /// The editor's carets on their own layer above the text canvas (VS Code keeps its cursors in a separate layer
    /// too): blinking (editor.cursorBlinking smooth / phase / expand / blink) and the smooth caret transition
    /// (editor.cursorSmoothCaretAnimation) repaint only this layer, whose draw list covers just the caret rectangles,
    /// so the compositor redraws a few pixels instead of the whole editor. Whenever the canvas repaints (scrolling,
    /// typing) this layer repaints in the same frame and computes the caret position from the same buffer state, so
    /// the caret never lags behind or flickers during scrolling.
    /// </summary>
    public sealed class AvaloniaCaretOverlay : Control
    {
        private readonly AvaloniaVirtualEditorCanvas canvas;
        private readonly CaretMoveAnimation move = new CaretMoveAnimation();
        private readonly List<Rect> extra = new List<Rect>();
        private EditorTabBuffer lastBuf;
        private int lastLine = -1, lastCol = -1, lastEditVersion = int.MinValue;
        private double lastContentX, lastContentY;
        private bool hasLast;

        public AvaloniaCaretOverlay(AvaloniaVirtualEditorCanvas canvas)
        {
            this.canvas = canvas;
            IsHitTestVisible = false;
            Focusable = false;
            ClipToBounds = true;
        }

        /// <summary>True while the caret glides to a new position (the canvas keeps requesting animation frames).</summary>
        public bool NeedsFrames => move.IsActive;
        /// <summary>Primary caret rectangle painted in the last frame (tests); empty when hidden.</summary>
        public Rect LastPrimaryRect { get; private set; }
        /// <summary>Opacity the carets were painted with in the last frame (tests).</summary>
        public double LastOpacity { get; private set; }
        /// <summary>Whether the last frame was part of a smooth caret transition (tests).</summary>
        public bool LastFrameAnimated { get; private set; }

        public override void Render(DrawingContext context)
        {
            base.Render(context);
            LastPrimaryRect = default;
            LastFrameAnimated = false;
            if (!canvas.IsEffectivelyVisible) return;
            if (!canvas.TryGetCaretLayout(out var primary, out bool primaryVisible, extra, out Rect clip, out double contentDx, out double contentDy, out IBrush brush))
            {
                hasLast = false;
                return;
            }
            var b = canvas.Buffer;
            double now = canvas.NowMs;

            // smooth caret: glide in document space after navigation; snap after edits, buffer switches and layout changes
            double cx = primary.X + contentDx, cy = primary.Y + contentDy;
            bool moved = b.CursorLine != lastLine || b.CursorCol != lastCol;
            bool edited = b.EditVersion != lastEditVersion || !ReferenceEquals(b, lastBuf);
            bool animate = hasLast && moved && !edited && EditorScrollOptions.SmoothCaretAnimation && canvas.AnimationsAllowed
                           && Math.Abs(cy - lastContentY) <= Math.Max(Bounds.Height, 1) && primaryVisible;
            if (animate)
            {
                move.Sample(now, out double fx, out double fy); // from where it is drawn now (retargets mid-flight)
                if (!move.IsActive) { fx = lastContentX; fy = lastContentY; }
                move.Start(fx, fy, cx, cy, now, EditorScrollOptions.CaretAnimationMs);
            }
            else if (moved || edited || !move.IsActive || Math.Abs(move.TargetX - cx) > 0.01 || Math.Abs(move.TargetY - cy) > 0.01)
            {
                move.Snap(cx, cy);
            }
            lastBuf = b; lastLine = b.CursorLine; lastCol = b.CursorCol; lastEditVersion = b.EditVersion;
            lastContentX = cx; lastContentY = cy; hasLast = true;

            move.Sample(now, out double ax, out double ay);
            bool gliding = move.IsActive;
            LastFrameAnimated = gliding;
            var drawPrimary = new Rect(ax - contentDx, ay - contentDy, primary.Width, primary.Height);

            canvas.GetCaretAppearance(out double opacity, out double scaleY);
            if (gliding) { opacity = 1; scaleY = 1; } // a moving caret is always solid
            LastOpacity = opacity;
            if (opacity <= 0.003 || scaleY <= 0.003) { if (gliding) canvas.RequestCaretFrame(); return; }

            using (context.PushClip(clip))
            using (context.PushOpacity(opacity))
            {
                if (primaryVisible || gliding)
                {
                    var r = Scale(drawPrimary, scaleY);
                    if (r.Intersects(clip)) { context.FillRectangle(brush, r); LastPrimaryRect = r; }
                }
                foreach (var er in extra) context.FillRectangle(brush, Scale(er, scaleY));
            }
            if (gliding) canvas.RequestCaretFrame();
        }

        private static Rect Scale(Rect r, double sy)
        {
            if (sy >= 0.999) return r;
            double h = r.Height * sy;
            return new Rect(r.X, r.Y + (r.Height - h) / 2, r.Width, h);
        }
    }

    public partial class AvaloniaVirtualEditorCanvas
    {
        private AvaloniaCaretOverlay caretOverlay;
        private bool caretFramePending;

        /// <summary>The caret layer (created by the editor control and stacked right above the canvas).</summary>
        public AvaloniaCaretOverlay CaretOverlay => caretOverlay ??= new AvaloniaCaretOverlay(this);

        /// <summary>Repaints the canvas and, in the same frame, its caret layer (the caret follows scrolling / edits without lag).</summary>
        public new void InvalidateVisual()
        {
            base.InvalidateVisual();
            caretOverlay?.InvalidateVisual();
        }

        /// <summary>Next animation frame for the caret transition (repaints only the caret layer).</summary>
        internal void RequestCaretFrame()
        {
            if (caretFramePending) return;
            var top = TopLevel.GetTopLevel(this);
            if (top == null) return;
            caretFramePending = true;
            top.RequestAnimationFrame(_ =>
            {
                caretFramePending = false;
                caretOverlay?.InvalidateVisual();
            });
        }

        /// <summary>
        /// Where the carets are for the current buffer state (computed on demand, not from the last canvas frame):
        /// the primary caret rect in canvas coordinates, extra carets, the clip (text area below the sticky header) and
        /// the offset from canvas to document coordinates (for the smooth caret animation).
        /// </summary>
        internal bool TryGetCaretLayout(out Rect primary, out bool primaryVisible, List<Rect> extras, out Rect clip, out double contentDx, out double contentDy, out IBrush brush)
        {
            primary = default; primaryVisible = false; clip = default; contentDx = contentDy = 0; brush = null;
            extras.Clear();
            var b = Buffer;
            if (b == null || b.Lines.Count == 0 || Bounds.Width <= GutterWidth + 4 || Bounds.Height <= 0) return false;
            b.ClampCursor();
            double h = Bounds.Height;
            double lh = LineHeight;
            double offset = TopRowOffsetPx;
            int visRows = (int)(h / lh) + 2;
            double codeBaseX = GutterWidth + 8 - (b.WordWrap ? 0 : b.ScrollLeftPx);
            double stickyBottom = EditorNicetyOptions.StickyScroll ? lastStickyLines.Count * lh : 0;
            double right = CodeAreaRight;
            clip = new Rect(GutterWidth + 1, stickyBottom, Math.Max(0, right - GutterWidth - 1), Math.Max(0, h - stickyBottom));
            if (caretBrush == null || !ReferenceEquals(caretBrushTheme, Theme))
            {
                caretBrushTheme = Theme;
                caretBrush = new SolidColorBrush(AvaloniaColorHelper.ToAvColor(Theme.AccentPrimary));
            }
            brush = caretBrush;
            NormalizeScroll(b);
            contentDx = b.WordWrap ? 0 : b.ScrollLeftPx;
            contentDy = TopRowExact(b) * lh;

            int cRow = CaretRowOf(b, b.CursorLine, b.CursorCol, true);
            int dist = RowDistance(b, b.ScrollTopLine, b.ScrollTopWrapRow, b.CursorLine, cRow, visRows);
            double x = RowColX(b, MakeRow(b, b.CursorLine, cRow), b.CursorCol, codeBaseX);
            double y = dist >= 0 ? dist * lh - offset : -lh * 2; // above the viewport: still a valid animation origin
            if (dist > visRows) y = h + lh;
            primary = new Rect(x, y + 2, 2.0, lh - 4);
            primaryVisible = dist >= 0 && dist <= visRows && !IsImeComposing && EditorFoldState.HiddenBy(b, b.CursorLine) < 0;
            foreach (var ec in ExtraCursors)
            {
                if (ec.Line < 0 || ec.Line >= b.Lines.Count || EditorFoldState.HiddenBy(b, ec.Line) >= 0) continue;
                int er = CaretRowOf(b, ec.Line, ec.Col, false);
                int d = RowDistance(b, b.ScrollTopLine, b.ScrollTopWrapRow, ec.Line, er, visRows);
                if (d < 0 || d > visRows) continue;
                double ex = RowColX(b, MakeRow(b, ec.Line, er), Math.Min(ec.Col, (b.Lines[ec.Line] ?? "").Length), codeBaseX);
                extras.Add(new Rect(ex, d * lh - offset + 2, 2.0, lh - 4));
            }
            return true;
        }

        private IBrush caretBrush;
        private StudioTheme caretBrushTheme;
    }
}
