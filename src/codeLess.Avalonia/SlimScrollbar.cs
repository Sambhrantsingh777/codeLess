using System;
using Avalonia;
using Avalonia.Input;
using Avalonia.Media;
using codeLess.Studio;

namespace codeLess.AvaloniaApp
{
    /// <summary>
    /// The VS Code list / terminal scrollbar for owner-drawn surfaces (explorer tree, terminal scrollback, git graph and
    /// diff rows), consistent with the editor scrollbar (QA #28): 10 px, transparent track, scrollbarSlider.* colors
    /// (UiTokens), slider shown while the pointer is over the surface or dragging, drag keeps the grab offset and a
    /// track click jumps there (VS Code's default). Geometry is the shared <see cref="EditorScrollbar"/>.
    /// </summary>
    public sealed class SlimScrollbar
    {
        public const double DefaultWidth = 10;
        public double Width = DefaultWidth;
        private EditorScrollbarGeometry geom;
        private Rect bounds;
        private bool dragging, hover;
        private double grab;

        public EditorScrollbarGeometry Geometry => geom;
        public Rect Bounds => bounds;
        public bool IsDragging => dragging;
        public bool IsVisible => geom.Visible;
        /// <summary>Whether the slider was painted in the last frame.</summary>
        public bool WasShown { get; private set; }

        /// <summary>Lays the bar out along the right edge of <paramref name="host"/> and paints the slider when <paramref name="show"/>.</summary>
        public void Paint(DrawingContext ctx, StudioTheme theme, Size host, double contentPx, double viewportPx, double offsetPx, bool show)
        {
            bounds = new Rect(Math.Max(0, host.Width - Width), 0, Width, host.Height);
            geom = EditorScrollbar.Horizontal(0, host.Height, contentPx, viewportPx, offsetPx);
            WasShown = false;
            if (!geom.Visible || (!show && !dragging) || theme == null) return;
            var c = dragging ? UiTokens.ScrollbarSliderActive(theme) : (hover ? UiTokens.ScrollbarSliderHover(theme) : UiTokens.ScrollbarSlider(theme));
            ctx.FillRectangle(new SolidColorBrush(AvaloniaColorHelper.ToAvColor(c)), new Rect(bounds.X, geom.ThumbStart, Width, geom.ThumbLength));
            WasShown = true;
        }

        public bool Contains(Point p) => geom.Visible && p.X >= bounds.X && p.X <= bounds.Right && p.Y >= 0 && p.Y <= bounds.Bottom;

        /// <summary>Left button down on the bar: starts a drag (a track click first jumps). Returns false when not on the bar.</summary>
        public bool Press(Point p, out double newOffset)
        {
            newOffset = 0;
            if (!Contains(p)) return false;
            int hit = EditorScrollbar.HitTest(geom, p.Y);
            if (hit == EditorScrollbar.HitThumb)
            {
                grab = p.Y - geom.ThumbStart;
                newOffset = EditorScrollbar.ValueForThumbStart(geom, geom.ThumbStart);
            }
            else
            {
                newOffset = EditorScrollbar.ValueForJump(geom, p.Y);
                grab = geom.ThumbLength / 2.0;
            }
            dragging = true;
            return true;
        }

        public bool Drag(Point p, out double newOffset)
        {
            newOffset = 0;
            if (!dragging) return false;
            newOffset = EditorScrollbar.ValueForThumbStart(geom, p.Y - grab);
            return true;
        }

        public bool Release()
        {
            bool was = dragging;
            dragging = false;
            return was;
        }

        /// <summary>Updates the hover state; true when it changed (repaint).</summary>
        public bool UpdateHover(Point? p)
        {
            bool h = p.HasValue && Contains(p.Value);
            if (h == hover) return false;
            hover = h;
            return true;
        }
    }
}
