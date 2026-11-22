using System;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;
using codeLess.Studio;

namespace codeLess.AvaloniaApp
{
    public static class AvaloniaColorHelper
    {
        public static Color ToAvColor(System.Drawing.Color c)
        {
            return Color.FromArgb(c.A, c.R, c.G, c.B);
        }

        public static Color ToAvColor(System.Drawing.Color c, byte alpha)
        {
            return Color.FromArgb(alpha, c.R, c.G, c.B);
        }

        public static SolidColorBrush ToBrush(System.Drawing.Color c)
        {
            return new SolidColorBrush(ToAvColor(c));
        }

        public static SolidColorBrush ToBrush(System.Drawing.Color c, byte alpha)
        {
            return new SolidColorBrush(ToAvColor(c, alpha));
        }

        public static bool IsLightColor(System.Drawing.Color c)
        {
            double lum = (0.299 * c.R + 0.587 * c.G + 0.114 * c.B) / 255.0;
            return lum > 0.55;
        }
    }

    public static class AvaloniaVectorIcons
    {
        public static void DrawIcon(DrawingContext ctx, string iconName, Rect bounds, Color color, float strokeWidth = 1.6f)
        {
            double cx = bounds.X + bounds.Width / 2.0;
            double cy = bounds.Y + bounds.Height / 2.0;
            double s = Math.Min(bounds.Width, bounds.Height) * 0.44;
            var pen = new Pen(new SolidColorBrush(color), strokeWidth, lineCap: PenLineCap.Round, lineJoin: PenLineJoin.Round);
            var fill = new SolidColorBrush(color);

            switch ((iconName ?? "").ToLowerInvariant())
            {
                case "explorer":
                {
                    var backRect = new Rect(cx - s * 0.85, cy - s * 0.90, s * 1.25, s * 1.45);
                    var frontRect = new Rect(cx - s * 0.40, cy - s * 0.50, s * 1.25, s * 1.45);
                    ctx.DrawRectangle(null, pen, backRect, 1.5, 1.5);
                    ctx.DrawRectangle(null, pen, frontRect, 1.5, 1.5);
                    ctx.DrawLine(pen, new Point(frontRect.X + 3, frontRect.Y + 5), new Point(frontRect.Right - 3, frontRect.Y + 5));
                    ctx.DrawLine(pen, new Point(frontRect.X + 3, frontRect.Y + 9), new Point(frontRect.Right - 5, frontRect.Y + 9));
                    break;
                }
                case "search":
                {
                    double r = s * 0.62;
                    Point ringCenter = new Point(cx - s * 0.18, cy - s * 0.18);
                    ctx.DrawEllipse(null, pen, ringCenter, r, r);
                    ctx.DrawLine(pen, new Point(cx + s * 0.26, cy + s * 0.26), new Point(cx + s * 0.88, cy + s * 0.88));
                    break;
                }
                case "git":
                {
                    double nr = Math.Max(2.0, s * 0.22);
                    Point topL = new Point(cx - s * 0.48, cy - s * 0.65);
                    Point botL = new Point(cx - s * 0.48, cy + s * 0.65);
                    Point topR = new Point(cx + s * 0.52, cy - s * 0.25);
                    ctx.DrawLine(pen, new Point(topL.X, topL.Y + nr), new Point(botL.X, botL.Y - nr));
                    ctx.DrawLine(pen, new Point(topL.X, cy + s * 0.15), new Point(topR.X, topR.Y + nr));
                    ctx.DrawEllipse(null, pen, topL, nr, nr);
                    ctx.DrawEllipse(null, pen, botL, nr, nr);
                    ctx.DrawEllipse(null, pen, topR, nr, nr);
                    break;
                }
                case "harness":
                case "sparkle":
                {
                    var geo = new StreamGeometry();
                    using (var gctx = geo.Open())
                    {
                        gctx.BeginFigure(new Point(cx, cy - s * 0.95), true);
                        gctx.LineTo(new Point(cx + s * 0.26, cy - s * 0.26));
                        gctx.LineTo(new Point(cx + s * 0.95, cy));
                        gctx.LineTo(new Point(cx + s * 0.26, cy + s * 0.26));
                        gctx.LineTo(new Point(cx, cy + s * 0.95));
                        gctx.LineTo(new Point(cx - s * 0.26, cy + s * 0.26));
                        gctx.LineTo(new Point(cx - s * 0.95, cy));
                        gctx.LineTo(new Point(cx - s * 0.26, cy - s * 0.26));
                        gctx.EndFigure(true);
                    }
                    ctx.DrawGeometry(null, pen, geo);
                    break;
                }
                case "arena":
                {
                    var leftCard = new Rect(cx - s * 0.92, cy - s * 0.72, s * 0.82, s * 1.44);
                    var rightCard = new Rect(cx + s * 0.10, cy - s * 0.72, s * 0.82, s * 1.44);
                    ctx.DrawRectangle(null, pen, leftCard, 1.5, 1.5);
                    ctx.DrawRectangle(null, pen, rightCard, 1.5, 1.5);
                    break;
                }
                case "terminal":
                {
                    var box = new Rect(cx - s * 0.92, cy - s * 0.72, s * 1.84, s * 1.44);
                    ctx.DrawRectangle(null, pen, box, 2, 2);
                    ctx.DrawLine(pen, new Point(cx - s * 0.55, cy - s * 0.28), new Point(cx - s * 0.18, cy + s * 0.04));
                    ctx.DrawLine(pen, new Point(cx - s * 0.18, cy + s * 0.04), new Point(cx - s * 0.55, cy + s * 0.36));
                    ctx.DrawLine(pen, new Point(cx + s * 0.05, cy + s * 0.36), new Point(cx + s * 0.52, cy + s * 0.36));
                    break;
                }
                case "run":
                case "debug":
                {
                    var geo = new StreamGeometry();
                    using (var gctx = geo.Open())
                    {
                        gctx.BeginFigure(new Point(cx - s * 0.55, cy - s * 0.78), true);
                        gctx.LineTo(new Point(cx + s * 0.75, cy));
                        gctx.LineTo(new Point(cx - s * 0.55, cy + s * 0.78));
                        gctx.EndFigure(true);
                    }
                    ctx.DrawGeometry(null, pen, geo);
                    break;
                }
                case "extensions":
                {
                    double q = s * 0.72;
                    double gap = s * 0.12;
                    ctx.DrawRectangle(null, pen, new Rect(cx - q - gap / 2, cy - q - gap / 2, q, q));
                    ctx.DrawRectangle(null, pen, new Rect(cx + gap / 2, cy - q - gap / 2 - s * 0.12, q, q));
                    ctx.DrawRectangle(null, pen, new Rect(cx - q - gap / 2, cy + gap / 2, q, q));
                    ctx.DrawRectangle(null, pen, new Rect(cx + gap / 2, cy + gap / 2, q, q));
                    break;
                }
                case "home":
                {
                    var roof = new StreamGeometry();
                    using (var gctx = roof.Open())
                    {
                        gctx.BeginFigure(new Point(cx - s * 0.92, cy - s * 0.05), false);
                        gctx.LineTo(new Point(cx, cy - s * 0.88));
                        gctx.LineTo(new Point(cx + s * 0.92, cy - s * 0.05));
                    }
                    ctx.DrawGeometry(null, pen, roof);
                    var walls = new StreamGeometry();
                    using (var gctx = walls.Open())
                    {
                        gctx.BeginFigure(new Point(cx - s * 0.62, cy - s * 0.12), false);
                        gctx.LineTo(new Point(cx - s * 0.62, cy + s * 0.78));
                        gctx.LineTo(new Point(cx + s * 0.62, cy + s * 0.78));
                        gctx.LineTo(new Point(cx + s * 0.62, cy - s * 0.12));
                    }
                    ctx.DrawGeometry(null, pen, walls);
                    break;
                }
                case "gear":
                {
                    double ro = s * 0.58;
                    double ri = s * 0.26;
                    ctx.DrawEllipse(null, pen, new Point(cx, cy), ro, ro);
                    ctx.DrawEllipse(null, pen, new Point(cx, cy), ri, ri);
                    for (int i = 0; i < 8; i++)
                    {
                        double ang = i * Math.PI / 4.0;
                        double x1 = cx + Math.Cos(ang) * (ro - 0.5);
                        double y1 = cy + Math.Sin(ang) * (ro - 0.5);
                        double x2 = cx + Math.Cos(ang) * (s * 0.88);
                        double y2 = cy + Math.Sin(ang) * (s * 0.88);
                        ctx.DrawLine(pen, new Point(x1, y1), new Point(x2, y2));
                    }
                    break;
                }
                case "account":
                {
                    double rHead = s * 0.34;
                    ctx.DrawEllipse(null, pen, new Point(cx, cy - s * 0.32), rHead, rHead);
                    var arc = new StreamGeometry();
                    using (var gctx = arc.Open())
                    {
                        gctx.BeginFigure(new Point(cx - s * 0.72, cy + s * 0.78), false);
                        gctx.CubicBezierTo(
                            new Point(cx - s * 0.65, cy + s * 0.15),
                            new Point(cx + s * 0.65, cy + s * 0.15),
                            new Point(cx + s * 0.72, cy + s * 0.78));
                    }
                    ctx.DrawGeometry(null, pen, arc);
                    break;
                }
                case "new-file":
                {
                    ctx.DrawRectangle(null, pen, new Rect(cx - s * 0.65, cy - s * 0.75, s * 1.10, s * 1.50));
                    double px = cx + s * 0.55;
                    double py = cy + s * 0.45;
                    ctx.DrawLine(pen, new Point(px - s * 0.32, py), new Point(px + s * 0.32, py));
                    ctx.DrawLine(pen, new Point(px, py - s * 0.32), new Point(px, py + s * 0.32));
                    break;
                }
                case "new-folder":
                {
                    ctx.DrawRectangle(null, pen, new Rect(cx - s * 0.78, cy - s * 0.45, s * 1.45, s * 1.15));
                    ctx.DrawLine(pen, new Point(cx - s * 0.78, cy - s * 0.65), new Point(cx - s * 0.15, cy - s * 0.65));
                    ctx.DrawLine(pen, new Point(cx - s * 0.15, cy - s * 0.65), new Point(cx + s * 0.05, cy - s * 0.45));
                    ctx.DrawLine(pen, new Point(cx, cy - s * 0.12), new Point(cx, cy + s * 0.38));
                    ctx.DrawLine(pen, new Point(cx - s * 0.25, cy + s * 0.13), new Point(cx + s * 0.25, cy + s * 0.13));
                    break;
                }
                case "refresh":
                {
                    double r = s * 0.68;
                    ctx.DrawEllipse(null, pen, new Point(cx, cy), r, r);
                    ctx.DrawLine(pen, new Point(cx + s * 0.52, cy - s * 0.45), new Point(cx + s * 0.78, cy - s * 0.15));
                    ctx.DrawLine(pen, new Point(cx + s * 0.52, cy - s * 0.45), new Point(cx + s * 0.22, cy - s * 0.32));
                    break;
                }
                case "collapse-all":
                {
                    ctx.DrawRectangle(null, pen, new Rect(cx - s * 0.72, cy - s * 0.72, s * 1.44, s * 1.44));
                    ctx.DrawLine(pen, new Point(cx - s * 0.38, cy), new Point(cx + s * 0.38, cy));
                    break;
                }
                case "layout-left":
                {
                    var box = new Rect(cx - s * 0.85, cy - s * 0.68, s * 1.70, s * 1.36);
                    ctx.DrawRectangle(null, pen, box, 1, 1);
                    double splitX = box.X + box.Width * 0.34;
                    ctx.DrawLine(pen, new Point(splitX, box.Y), new Point(splitX, box.Bottom));
                    break;
                }
                case "layout-bottom":
                {
                    var box = new Rect(cx - s * 0.85, cy - s * 0.68, s * 1.70, s * 1.36);
                    ctx.DrawRectangle(null, pen, box, 1, 1);
                    double splitY = box.Y + box.Height * 0.66;
                    ctx.DrawLine(pen, new Point(box.X, splitY), new Point(box.Right, splitY));
                    break;
                }
                case "layout-right":
                {
                    var box = new Rect(cx - s * 0.85, cy - s * 0.68, s * 1.70, s * 1.36);
                    ctx.DrawRectangle(null, pen, box, 1, 1);
                    double splitX = box.X + box.Width * 0.66;
                    ctx.DrawLine(pen, new Point(splitX, box.Y), new Point(splitX, box.Bottom));
                    break;
                }
                case "split-right":
                {
                    var box = new Rect(cx - s * 0.82, cy - s * 0.68, s * 1.64, s * 1.36);
                    ctx.DrawRectangle(null, pen, box, 1, 1);
                    ctx.DrawLine(pen, new Point(cx, box.Y), new Point(cx, box.Bottom));
                    break;
                }
                case "plus":
                {
                    ctx.DrawLine(pen, new Point(cx - s * 0.68, cy), new Point(cx + s * 0.68, cy));
                    ctx.DrawLine(pen, new Point(cx, cy - s * 0.68), new Point(cx, cy + s * 0.68));
                    break;
                }
                case "trash":
                {
                    ctx.DrawLine(pen, new Point(cx - s * 0.72, cy - s * 0.48), new Point(cx + s * 0.72, cy - s * 0.48));
                    ctx.DrawLine(pen, new Point(cx - s * 0.28, cy - s * 0.68), new Point(cx + s * 0.28, cy - s * 0.68));
                    ctx.DrawRectangle(null, pen, new Rect(cx - s * 0.52, cy - s * 0.48, s * 1.04, s * 1.18));
                    break;
                }
                case "maximize-up":
                {
                    var geo = new StreamGeometry();
                    using (var gctx = geo.Open())
                    {
                        gctx.BeginFigure(new Point(cx - s * 0.68, cy + s * 0.28), false);
                        gctx.LineTo(new Point(cx, cy - s * 0.38));
                        gctx.LineTo(new Point(cx + s * 0.68, cy + s * 0.28));
                    }
                    ctx.DrawGeometry(null, pen, geo);
                    break;
                }
                case "chevron-down":
                {
                    var geo = new StreamGeometry();
                    using (var gctx = geo.Open())
                    {
                        gctx.BeginFigure(new Point(cx - s * 0.68, cy - s * 0.28), false);
                        gctx.LineTo(new Point(cx, cy + s * 0.38));
                        gctx.LineTo(new Point(cx + s * 0.68, cy - s * 0.28));
                    }
                    ctx.DrawGeometry(null, pen, geo);
                    break;
                }
                case "chevron-right":
                {
                    var geo = new StreamGeometry();
                    using (var gctx = geo.Open())
                    {
                        gctx.BeginFigure(new Point(cx - s * 0.28, cy - s * 0.68), false);
                        gctx.LineTo(new Point(cx + s * 0.38, cy));
                        gctx.LineTo(new Point(cx - s * 0.28, cy + s * 0.68));
                    }
                    ctx.DrawGeometry(null, pen, geo);
                    break;
                }
                case "close":
                {
                    double d = s * 0.58;
                    ctx.DrawLine(pen, new Point(cx - d, cy - d), new Point(cx + d, cy + d));
                    ctx.DrawLine(pen, new Point(cx + d, cy - d), new Point(cx - d, cy + d));
                    break;
                }
                case "min":
                {
                    ctx.DrawLine(pen, new Point(cx - s * 0.58, cy), new Point(cx + s * 0.58, cy));
                    break;
                }
                case "max":
                {
                    ctx.DrawRectangle(null, pen, new Rect(cx - s * 0.55, cy - s * 0.55, s * 1.10, s * 1.10));
                    break;
                }
                case "logo":
                {
                    var geo = new StreamGeometry();
                    using (var gctx = geo.Open())
                    {
                        gctx.BeginFigure(new Point(cx + s * 0.15, cy - s * 0.95), true);
                        gctx.LineTo(new Point(cx - s * 0.65, cy + s * 0.08));
                        gctx.LineTo(new Point(cx - s * 0.05, cy + s * 0.08));
                        gctx.LineTo(new Point(cx - s * 0.22, cy + s * 0.95));
                        gctx.LineTo(new Point(cx + s * 0.68, cy - s * 0.12));
                        gctx.LineTo(new Point(cx + s * 0.06, cy - s * 0.12));
                        gctx.EndFigure(true);
                    }
                    ctx.DrawGeometry(fill, null, geo);
                    break;
                }
                case "brand-bolt":
                {
                    double x = bounds.X;
                    double y = bounds.Y;
                    double w = bounds.Width;
                    double h = bounds.Height;
                    var leftGeo = new StreamGeometry();
                    using (var gctx = leftGeo.Open())
                    {
                        gctx.BeginFigure(new Point(x + w * 0.58, y + h * 0.04), true);
                        gctx.LineTo(new Point(x + w * 0.18, y + h * 0.54));
                        gctx.LineTo(new Point(x + w * 0.48, y + h * 0.54));
                        gctx.LineTo(new Point(x + w * 0.36, y + h * 0.96));
                        gctx.LineTo(new Point(x + w * 0.52, y + h * 0.48));
                        gctx.LineTo(new Point(x + w * 0.44, y + h * 0.48));
                        gctx.EndFigure(true);
                    }
                    var rightGeo = new StreamGeometry();
                    using (var gctx = rightGeo.Open())
                    {
                        gctx.BeginFigure(new Point(x + w * 0.58, y + h * 0.04), true);
                        gctx.LineTo(new Point(x + w * 0.44, y + h * 0.48));
                        gctx.LineTo(new Point(x + w * 0.82, y + h * 0.48));
                        gctx.LineTo(new Point(x + w * 0.36, y + h * 0.96));
                        gctx.LineTo(new Point(x + w * 0.50, y + h * 0.54));
                        gctx.EndFigure(true);
                    }
                    ctx.DrawGeometry(new SolidColorBrush(Color.FromRgb(0, 180, 240)), null, leftGeo);
                    ctx.DrawGeometry(new SolidColorBrush(Color.FromRgb(0, 104, 196)), null, rightGeo);
                    break;
                }
                case "remote":
                {
                    // "><" connection glyph drawn as two chevrons
                    var geo = new StreamGeometry();
                    using (var gctx = geo.Open())
                    {
                        gctx.BeginFigure(new Point(cx - s * 0.80, cy - s * 0.50), false);
                        gctx.LineTo(new Point(cx - s * 0.20, cy));
                        gctx.LineTo(new Point(cx - s * 0.80, cy + s * 0.50));
                        gctx.BeginFigure(new Point(cx + s * 0.80, cy - s * 0.50), false);
                        gctx.LineTo(new Point(cx + s * 0.20, cy));
                        gctx.LineTo(new Point(cx + s * 0.80, cy + s * 0.50));
                    }
                    ctx.DrawGeometry(null, pen, geo);
                    break;
                }
                case "branch":
                {
                    double r = s * 0.22;
                    var top = new Point(cx - s * 0.40, cy - s * 0.62);
                    var bottom = new Point(cx - s * 0.40, cy + s * 0.62);
                    var side = new Point(cx + s * 0.45, cy - s * 0.30);
                    ctx.DrawEllipse(null, pen, top, r, r);
                    ctx.DrawEllipse(null, pen, bottom, r, r);
                    ctx.DrawEllipse(null, pen, side, r, r);
                    ctx.DrawLine(pen, new Point(top.X, top.Y + r), new Point(bottom.X, bottom.Y - r));
                    var geo = new StreamGeometry();
                    using (var gctx = geo.Open())
                    {
                        gctx.BeginFigure(new Point(side.X, side.Y + r), false);
                        gctx.QuadraticBezierTo(new Point(side.X, cy + s * 0.25), new Point(bottom.X + r * 0.6, bottom.Y - r * 1.4));
                    }
                    ctx.DrawGeometry(null, pen, geo);
                    break;
                }
                case "error":
                {
                    double r = s * 0.72;
                    ctx.DrawEllipse(null, pen, new Point(cx, cy), r, r);
                    double d = s * 0.30;
                    ctx.DrawLine(pen, new Point(cx - d, cy - d), new Point(cx + d, cy + d));
                    ctx.DrawLine(pen, new Point(cx + d, cy - d), new Point(cx - d, cy + d));
                    break;
                }
                case "warning":
                {
                    var geo = new StreamGeometry();
                    using (var gctx = geo.Open())
                    {
                        gctx.BeginFigure(new Point(cx, cy - s * 0.78), false);
                        gctx.LineTo(new Point(cx + s * 0.82, cy + s * 0.66));
                        gctx.LineTo(new Point(cx - s * 0.82, cy + s * 0.66));
                        gctx.EndFigure(true);
                    }
                    ctx.DrawGeometry(null, pen, geo);
                    ctx.DrawLine(pen, new Point(cx, cy - s * 0.24), new Point(cx, cy + s * 0.18));
                    ctx.DrawEllipse(fill, null, new Point(cx, cy + s * 0.42), s * 0.07, s * 0.07);
                    break;
                }
                case "info":
                {
                    double r = s * 0.72;
                    ctx.DrawEllipse(null, pen, new Point(cx, cy), r, r);
                    ctx.DrawLine(pen, new Point(cx, cy - s * 0.08), new Point(cx, cy + s * 0.38));
                    ctx.DrawEllipse(fill, null, new Point(cx, cy - s * 0.34), s * 0.07, s * 0.07);
                    break;
                }
                case "pencil":
                case "edit":
                {
                    var geo = new StreamGeometry();
                    using (var gctx = geo.Open())
                    {
                        gctx.BeginFigure(new Point(cx - s * 0.70, cy + s * 0.70), false);
                        gctx.LineTo(new Point(cx - s * 0.62, cy + s * 0.32));
                        gctx.LineTo(new Point(cx + s * 0.42, cy - s * 0.72));
                        gctx.LineTo(new Point(cx + s * 0.72, cy - s * 0.42));
                        gctx.LineTo(new Point(cx - s * 0.32, cy + s * 0.62));
                        gctx.EndFigure(true);
                    }
                    ctx.DrawGeometry(null, pen, geo);
                    ctx.DrawLine(pen, new Point(cx + s * 0.20, cy - s * 0.50), new Point(cx + s * 0.50, cy - s * 0.20));
                    break;
                }
                case "chevron-up":
                {
                    var geo = new StreamGeometry();
                    using (var gctx = geo.Open())
                    {
                        gctx.BeginFigure(new Point(cx - s * 0.62, cy + s * 0.28), false);
                        gctx.LineTo(new Point(cx, cy - s * 0.34));
                        gctx.LineTo(new Point(cx + s * 0.62, cy + s * 0.28));
                    }
                    ctx.DrawGeometry(null, pen, geo);
                    break;
                }
                case "arrow-up":
                case "arrow-down":
                {
                    double dir = iconName.EndsWith("up", StringComparison.OrdinalIgnoreCase) ? -1 : 1;
                    ctx.DrawLine(pen, new Point(cx, cy - s * 0.70 * dir), new Point(cx, cy + s * 0.70 * dir));
                    var geo = new StreamGeometry();
                    using (var gctx = geo.Open())
                    {
                        gctx.BeginFigure(new Point(cx - s * 0.50, cy + s * 0.20 * dir), false);
                        gctx.LineTo(new Point(cx, cy + s * 0.70 * dir));
                        gctx.LineTo(new Point(cx + s * 0.50, cy + s * 0.20 * dir));
                    }
                    ctx.DrawGeometry(null, pen, geo);
                    break;
                }
                case "replace":
                case "replace-all":
                {
                    bool all = iconName.EndsWith("all", StringComparison.OrdinalIgnoreCase);
                    ctx.DrawRectangle(null, pen, new Rect(cx - s * 0.75, cy - s * 0.70, s * 0.70, s * 0.62), 1, 1);
                    if (all) ctx.DrawRectangle(null, pen, new Rect(cx - s * 0.75, cy + s * 0.12, s * 0.70, s * 0.58), 1, 1);
                    var geo = new StreamGeometry();
                    using (var gctx = geo.Open())
                    {
                        gctx.BeginFigure(new Point(cx + s * 0.12, cy - s * 0.38), false);
                        gctx.LineTo(new Point(cx + s * 0.55, cy - s * 0.38));
                        gctx.LineTo(new Point(cx + s * 0.55, cy + s * 0.40));
                        gctx.BeginFigure(new Point(cx + s * 0.30, cy + s * 0.16), false);
                        gctx.LineTo(new Point(cx + s * 0.55, cy + s * 0.42));
                        gctx.LineTo(new Point(cx + s * 0.80, cy + s * 0.16));
                    }
                    ctx.DrawGeometry(null, pen, geo);
                    break;
                }
                case "copy":
                {
                    ctx.DrawRectangle(null, pen, new Rect(cx - s * 0.70, cy - s * 0.70, s * 1.0, s * 1.05), 1.5, 1.5);
                    ctx.DrawRectangle(null, pen, new Rect(cx - s * 0.30, cy - s * 0.32, s * 1.0, s * 1.05), 1.5, 1.5);
                    break;
                }
                case "ellipsis":
                {
                    double r = Math.Max(0.9, s * 0.10);
                    ctx.DrawEllipse(fill, null, new Point(cx - s * 0.55, cy), r, r);
                    ctx.DrawEllipse(fill, null, new Point(cx, cy), r, r);
                    ctx.DrawEllipse(fill, null, new Point(cx + s * 0.55, cy), r, r);
                    break;
                }
                case "clear-all":
                {
                    ctx.DrawLine(pen, new Point(cx - s * 0.75, cy - s * 0.50), new Point(cx + s * 0.20, cy - s * 0.50));
                    ctx.DrawLine(pen, new Point(cx - s * 0.75, cy), new Point(cx + s * 0.05, cy));
                    ctx.DrawLine(pen, new Point(cx - s * 0.75, cy + s * 0.50), new Point(cx - s * 0.10, cy + s * 0.50));
                    double d = s * 0.22;
                    ctx.DrawLine(pen, new Point(cx + s * 0.52 - d, cy + s * 0.25 - d), new Point(cx + s * 0.52 + d, cy + s * 0.25 + d));
                    ctx.DrawLine(pen, new Point(cx + s * 0.52 + d, cy + s * 0.25 - d), new Point(cx + s * 0.52 - d, cy + s * 0.25 + d));
                    break;
                }
                case "history":
                {
                    double r = s * 0.70;
                    var geo = new StreamGeometry();
                    using (var gctx = geo.Open())
                    {
                        gctx.BeginFigure(new Point(cx - r, cy), false);
                        gctx.ArcTo(new Point(cx + r, cy), new Size(r, r), 0, false, SweepDirection.Clockwise);
                        gctx.ArcTo(new Point(cx - r * 0.70, cy + r * 0.70), new Size(r, r), 0, false, SweepDirection.Clockwise);
                    }
                    ctx.DrawGeometry(null, pen, geo);
                    ctx.DrawLine(pen, new Point(cx, cy - s * 0.38), new Point(cx, cy));
                    ctx.DrawLine(pen, new Point(cx, cy), new Point(cx + s * 0.28, cy + s * 0.20));
                    break;
                }
                case "discard":
                {
                    double r = s * 0.62;
                    var geo = new StreamGeometry();
                    using (var gctx = geo.Open())
                    {
                        gctx.BeginFigure(new Point(cx - r, cy - s * 0.05), false);
                        gctx.ArcTo(new Point(cx + r * 0.2, cy + r), new Size(r, r), 0, true, SweepDirection.Clockwise);
                    }
                    ctx.DrawGeometry(null, pen, geo);
                    ctx.DrawLine(pen, new Point(cx - r, cy - s * 0.05), new Point(cx - r - s * 0.20, cy - s * 0.32));
                    ctx.DrawLine(pen, new Point(cx - r, cy - s * 0.05), new Point(cx - r + s * 0.30, cy - s * 0.25));
                    break;
                }
                case "minus":
                {
                    ctx.DrawLine(pen, new Point(cx - s * 0.62, cy), new Point(cx + s * 0.62, cy));
                    break;
                }
                case "send":
                {
                    var geo = new StreamGeometry();
                    using (var gctx = geo.Open())
                    {
                        gctx.BeginFigure(new Point(cx - s * 0.75, cy - s * 0.70), true);
                        gctx.LineTo(new Point(cx + s * 0.80, cy));
                        gctx.LineTo(new Point(cx - s * 0.75, cy + s * 0.70));
                        gctx.LineTo(new Point(cx - s * 0.45, cy));
                        gctx.EndFigure(true);
                    }
                    ctx.DrawGeometry(null, pen, geo);
                    break;
                }
                case "case-sensitive":
                case "whole-word":
                case "regex":
                {
                    string lower = iconName.ToLowerInvariant();
                    string txt = lower == "case-sensitive" ? "Aa" : (lower == "whole-word" ? "ab" : ".*");
                    var ft = new FormattedText(txt, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                        new Typeface("Inter, Segoe UI, sans-serif", FontStyle.Normal, FontWeight.SemiBold), Math.Max(8, bounds.Height * 0.66), fill);
                    ctx.DrawText(ft, new Point(cx - ft.Width / 2.0, cy - ft.Height / 2.0));
                    if (lower == "whole-word")
                        ctx.DrawLine(new Pen(fill, 1), new Point(cx - ft.Width / 2.0 - 1, cy + ft.Height * 0.40), new Point(cx + ft.Width / 2.0 + 1, cy + ft.Height * 0.40));
                    break;
                }
                case "check":
                {
                    var geo = new StreamGeometry();
                    using (var gctx = geo.Open())
                    {
                        gctx.BeginFigure(new Point(cx - s * 0.68, cy + s * 0.05), false);
                        gctx.LineTo(new Point(cx - s * 0.18, cy + s * 0.55));
                        gctx.LineTo(new Point(cx + s * 0.72, cy - s * 0.48));
                    }
                    ctx.DrawGeometry(null, pen, geo);
                    break;
                }
            }
        }
    }

    public class VectorIconControl : Control
    {
        public string IconName { get; set; } = "explorer";
        public Color IconColor { get; set; } = Color.FromRgb(180, 188, 204);
        public float StrokeWidth { get; set; } = 1.6f;
        public int BadgeCount { get; set; } = 0;
        public Color BadgeBackground { get; set; } = Color.FromRgb(0, 122, 204);
        public Color BadgeForeground { get; set; } = Colors.White;

        /// <summary>When true the glyph uses the inherited text foreground (e.g. of the hosting Button) instead of <see cref="IconColor"/>.</summary>
        public bool FollowForeground { get; set; }

        static VectorIconControl()
        {
            AffectsRender<VectorIconControl>(Avalonia.Controls.Documents.TextElement.ForegroundProperty);
        }

        public VectorIconControl()
        {
            Width = 20;
            Height = 20;
            IsHitTestVisible = false;
        }

        public override void Render(DrawingContext context)
        {
            base.Render(context);
            Color color = IconColor;
            if (FollowForeground && Avalonia.Controls.Documents.TextElement.GetForeground(this) is ISolidColorBrush fb)
                color = fb.Color;
            AvaloniaVectorIcons.DrawIcon(context, IconName, new Rect(0, 0, Bounds.Width, Bounds.Height), color, StrokeWidth);

            if (BadgeCount > 0)
            {
                string badgeText = BadgeCount > 99 ? "99+" : BadgeCount.ToString(CultureInfo.InvariantCulture);
                var ft = new FormattedText(
                    badgeText,
                    CultureInfo.InvariantCulture,
                    FlowDirection.LeftToRight,
                    new Typeface("Inter, Segoe UI, sans-serif", FontStyle.Normal, FontWeight.Bold),
                    8.5,
                    new SolidColorBrush(BadgeForeground));
                double bw = Math.Max(14, ft.Width + 6);
                double bh = 13;
                var badgeRect = new Rect(Bounds.Width - bw + 2, Bounds.Height - bh + 2, bw, bh);
                context.DrawRectangle(new SolidColorBrush(BadgeBackground), null, badgeRect, 6.5, 6.5);
                context.DrawText(ft, new Point(badgeRect.X + (badgeRect.Width - ft.Width) / 2.0, badgeRect.Y + (badgeRect.Height - ft.Height) / 2.0));
            }
        }
    }

    public class AvaloniaIndeterminateBar : Control
    {
        private readonly DispatcherTimer animTimer;
        private double phase = 0;
        private bool isAnimating = false;

        public Color TrackColor { get; set; } = Color.FromRgb(22, 22, 26);
        public Color BarColor { get; set; } = Color.FromRgb(59, 130, 246);

        public new bool IsAnimating => isAnimating;

        public AvaloniaIndeterminateBar()
        {
            Height = 2;
            IsVisible = false;
            animTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(30) };
            animTimer.Tick += (s, e) =>
            {
                phase = (phase + 0.035) % 1.0;
                InvalidateVisual();
            };
            // 33 frames/s only while animating AND on screen (not minimized, not inside a hidden panel).
            animGate = new UiTimerGate(this, animTimer, requireFocus: false, wanted: () => isAnimating);
        }

        private readonly UiTimerGate animGate;
        public bool IsAnimationTimerRunning => animTimer.IsEnabled;

        public void StartProgress()
        {
            isAnimating = true;
            IsVisible = true;
            animGate.Update();
            InvalidateVisual();
        }

        public void StopProgress()
        {
            isAnimating = false;
            animGate.Update();
            IsVisible = false;
            InvalidateVisual();
        }

        public override void Render(DrawingContext context)
        {
            base.Render(context);
            context.FillRectangle(new SolidColorBrush(TrackColor), new Rect(0, 0, Bounds.Width, Bounds.Height));
            if (!isAnimating) return;

            double barW = Math.Max(48, Bounds.Width * 0.28);
            double x = (Bounds.Width + barW) * phase - barW;
            context.FillRectangle(new SolidColorBrush(BarColor), new Rect(x, 0, barW, Bounds.Height));
        }
    }

    /// <summary>
    /// Vector glyph + label used for status-bar items and section headers. <see cref="ToString"/> keeps the legacy
    /// text form (e.g. "▾ OUTLINE", "⎇ main") so callers/tests reading Content.ToString() keep working.
    /// </summary>
    public class IconLabel : StackPanel
    {
        public VectorIconControl Icon { get; }
        public TextBlock Label { get; }
        public string LegacyPrefix { get; set; } = "";

        public IconLabel(string icon, string text, double iconSize = 14, double fontSize = 12, string legacyPrefix = "")
        {
            Orientation = Avalonia.Layout.Orientation.Horizontal;
            Spacing = 4;
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center;
            LegacyPrefix = legacyPrefix ?? "";
            Icon = new VectorIconControl { IconName = icon, Width = iconSize, Height = iconSize, FollowForeground = true, StrokeWidth = 1.4f, VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center };
            Label = new TextBlock { Text = text ?? "", FontSize = fontSize, VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center };
            Icon.IsVisible = !string.IsNullOrEmpty(icon);
            Children.Add(Icon);
            Children.Add(Label);
        }

        public string Text
        {
            get => Label.Text ?? "";
            set => Label.Text = value ?? "";
        }

        public override string ToString() => LegacyPrefix + (Label.Text ?? "");
    }

    /// <summary>
    /// Collapsible section header ("▾ OUTLINE"): vector chevron + 11 px semibold uppercase label. Built from the
    /// legacy text form, which <see cref="IconLabel.ToString"/> returns unchanged.
    /// </summary>
    public class SectionHeaderLabel : IconLabel
    {
        public bool IsExpanded { get; }

        public SectionHeaderLabel(string legacyText)
            : base(ChevronFor(legacyText), StripChevron(legacyText), 12, codeLess.Studio.UiTokens.FontHeader, PrefixFor(legacyText))
        {
            IsExpanded = !(legacyText ?? "").StartsWith("▸", StringComparison.Ordinal);
            Spacing = 4;
            Label.FontWeight = FontWeight.SemiBold;
            Icon.StrokeWidth = 1.5f;
        }

        private static bool HasChevron(string t) => !string.IsNullOrEmpty(t) && (t.StartsWith("▾ ", StringComparison.Ordinal) || t.StartsWith("▸ ", StringComparison.Ordinal));
        private static string ChevronFor(string t) => !HasChevron(t) ? "" : (t.StartsWith("▸", StringComparison.Ordinal) ? "chevron-right" : "chevron-down");
        private static string StripChevron(string t) => HasChevron(t) ? t.Substring(2) : (t ?? "");
        private static string PrefixFor(string t) => HasChevron(t) ? t.Substring(0, 2) : "";
    }

    public class LiteralTextBlock : TextBlock
    {
        public LiteralTextBlock()
        {
        }

        public LiteralTextBlock(string text)
        {
            Text = text ?? "";
        }

        public override string ToString()
        {
            return Text ?? "";
        }
    }
}
