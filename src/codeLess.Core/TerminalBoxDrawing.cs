using System;
using System.Collections.Generic;

namespace codeLess.Studio
{
    /// <summary>One drawing primitive of a custom terminal glyph (logical pixels, already snapped to device pixels).</summary>
    public struct TermGlyphPrim
    {
        public const byte FillRect = 0, FillPolygon = 1, StrokePolyline = 2;
        public byte Kind;
        public double X, Y, W, H;
        /// <summary>x0, y0, x1, y1, ... for polygons / polylines.</summary>
        public double[] Points;
        public double Thickness;
        /// <summary>Opacity multiplier of the foreground (shade characters).</summary>
        public double Alpha;
    }

    /// <summary>
    /// Box drawing (U+2500-U+257F), block elements (U+2580-U+259F) and the Powerline separators (U+E0B0-U+E0B3) drawn as
    /// shapes on the cell grid like xterm.js's customGlyphs, instead of font glyphs: font glyphs rarely span the full
    /// cell height (gaps between the rows of a box or a tree), and fallback fonts shift them. Lines are centred in the
    /// cell and snapped to device pixels so neighbouring cells always join. STRICT C# 5.
    /// </summary>
    public static class TerminalBoxDrawing
    {
        // U+2500..U+257F: arm weights up, right, down, left (0 none, 1 light, 2 heavy, 3 double), then a dash count
        // (0 solid) and a special code (A = arc, D = diagonal). One 6-char entry per character.
        private static readonly string Table =
            "010100" + "020200" + "101000" + "202000" + "010130" + "020230" + "101030" + "202030" +
            "010140" + "020240" + "101040" + "202040" + "011000" + "021000" + "012000" + "022000" +
            "001100" + "001200" + "002100" + "002200" + "110000" + "120000" + "210000" + "220000" +
            "100100" + "100200" + "200100" + "200200" + "111000" + "121000" + "211000" + "112000" +
            "212000" + "221000" + "122000" + "222000" + "101100" + "101200" + "201100" + "102100" +
            "202100" + "201200" + "102200" + "202200" + "011100" + "011200" + "021100" + "021200" +
            "012100" + "012200" + "022100" + "022200" + "110100" + "110200" + "120100" + "120200" +
            "210100" + "210200" + "220100" + "220200" + "111100" + "111200" + "121100" + "121200" +
            "211100" + "112100" + "212100" + "211200" + "221100" + "112200" + "122100" + "221200" +
            "122200" + "212200" + "222100" + "222200" + "010120" + "020220" + "101020" + "202020" +
            "030300" + "303000" + "031000" + "013000" + "033000" + "001300" + "003100" + "003300" +
            "130000" + "310000" + "330000" + "100300" + "300100" + "300300" + "131000" + "313000" +
            "333000" + "101300" + "303100" + "303300" + "031300" + "013100" + "033300" + "130300" +
            "310100" + "330300" + "131300" + "313100" + "333300" + "00000A" + "00000A" + "00000A" +
            "00000A" + "00000D" + "00000D" + "00000D" + "000100" + "100000" + "010000" + "001000" +
            "000200" + "200000" + "020000" + "002000" + "020100" + "102000" + "010200" + "201000";

        /// <summary>True for the characters drawn as shapes.</summary>
        public static bool IsCustomGlyph(char ch)
        {
            return (ch >= '─' && ch <= '▟') || (ch >= '\uE0B0' && ch <= '\uE0B3');
        }

        /// <summary>
        /// Appends the shapes of <paramref name="ch"/> for the cell at (<paramref name="x"/>, <paramref name="y"/>) of
        /// size <paramref name="w"/> x <paramref name="h"/>; <paramref name="scale"/> is the device-pixel ratio used for
        /// snapping. False (nothing appended) for characters drawn from the font.
        /// </summary>
        public static bool TryBuild(char ch, double x, double y, double w, double h, double scale, List<TermGlyphPrim> output)
        {
            if (!IsCustomGlyph(ch) || output == null || w <= 0 || h <= 0) return false;
            if (scale <= 0 || double.IsNaN(scale)) scale = 1;
            double L = Math.Round(x * scale), R = Math.Round((x + w) * scale);
            double T = Math.Round(y * scale), B = Math.Round((y + h) * scale);
            if (R - L < 1) R = L + 1;
            if (B - T < 1) B = T + 1;
            if (ch >= '\uE0B0') { Powerline(ch, L, T, R, B, scale, output); return true; }
            if (ch >= '▀') { Block(ch, L, T, R, B, scale, output); return true; }
            int i = (ch - '─') * 6;
            char special = Table[i + 5];
            double t = LightThickness(R - L, scale);
            if (special == 'A') { Arc(ch, L, T, R, B, t, scale, output); return true; }
            if (special == 'D') { Diagonal(ch, L, T, R, B, t, scale, output); return true; }
            int up = Table[i] - '0', right = Table[i + 1] - '0', down = Table[i + 2] - '0', left = Table[i + 3] - '0';
            int dashes = Table[i + 4] - '0';
            if (dashes > 0) { Dashed(up, right, down, left, dashes, L, T, R, B, t, scale, output); return true; }
            Lines(up, right, down, left, L, T, R, B, t, scale, output);
            return true;
        }

        /// <summary>Light line thickness in device pixels: about 1/8 of the cell width, at least one device pixel.</summary>
        public static double LightThickness(double deviceCellWidth, double scale)
        {
            return Math.Max(Math.Max(1.0, Math.Floor(scale)), Math.Round(deviceCellWidth / 8.0));
        }

        private static double Thick(int weight, double t) { return weight == 2 ? t * 2 : t; }

        // Start of a centred band of the given width inside [a, b] (device pixels).
        private static double Centre(double a, double b, double width) { return a + Math.Floor((b - a - width) / 2.0); }

        private static void Lines(int up, int right, int down, int left, double L, double T, double R, double B, double t, double s, List<TermGlyphPrim> o)
        {
            bool anyDouble = up == 3 || right == 3 || down == 3 || left == 3;
            // centre square: the band of the widest vertical arm x the band of the widest horizontal arm
            // (a double pair is 3t wide); without arms in one direction the square collapses onto the cell middle
            double vw = Math.Max(ArmWidth(up, t), ArmWidth(down, t));
            double hw = Math.Max(ArmWidth(left, t), ArmWidth(right, t));
            double midX = L + Math.Floor((R - L) / 2.0), midY = T + Math.Floor((B - T) / 2.0);
            double x0 = vw > 0 ? Centre(L, R, vw) : midX, x1 = vw > 0 ? x0 + vw : midX;
            double y0 = hw > 0 ? Centre(T, B, hw) : midY, y1 = hw > 0 ? y0 + hw : midY;
            bool hasH = hw > 0, hasV = vw > 0;

            // straight lines of one weight through the whole cell: one shape per line
            if (!hasH && up == down && up != 0) { VerticalFull(up, L, R, T, B, t, s, o); return; }
            if (!hasV && left == right && left != 0) { HorizontalFull(left, L, R, T, B, t, s, o); return; }

            VerticalArm(up, true, L, R, T, B, y0, y1, midY, hasH, t, s, o);
            VerticalArm(down, false, L, R, T, B, y0, y1, midY, hasH, t, s, o);
            HorizontalArm(left, true, L, R, T, B, x0, x1, midX, hasV, t, s, o);
            HorizontalArm(right, false, L, R, T, B, x0, x1, midX, hasV, t, s, o);

            if (!anyDouble || !hasH || !hasV) return;
            // walls of the centre square close the double lines where no arm leaves (corners, tees)
            if (left == 0) Rect(x0, y0, t, y1 - y0, s, o);
            if (right == 0) Rect(x1 - t, y0, t, y1 - y0, s, o);
            if (up == 0) Rect(x0, y0, x1 - x0, t, s, o);
            if (down == 0) Rect(x0, y1 - t, x1 - x0, t, s, o);
        }

        private static void VerticalFull(int weight, double L, double R, double T, double B, double t, double s, List<TermGlyphPrim> o)
        {
            if (weight == 3)
            {
                double px = Centre(L, R, 3 * t);
                Rect(px, T, t, B - T, s, o);
                Rect(px + 2 * t, T, t, B - T, s, o);
                return;
            }
            double th = Thick(weight, t);
            Rect(Centre(L, R, th), T, th, B - T, s, o);
        }

        private static void HorizontalFull(int weight, double L, double R, double T, double B, double t, double s, List<TermGlyphPrim> o)
        {
            if (weight == 3)
            {
                double py = Centre(T, B, 3 * t);
                Rect(L, py, R - L, t, s, o);
                Rect(L, py + 2 * t, R - L, t, s, o);
                return;
            }
            double th = Thick(weight, t);
            Rect(L, Centre(T, B, th), R - L, th, s, o);
        }

        private static double ArmWidth(int weight, double t)
        {
            return weight == 0 ? 0 : (weight == 3 ? 3 * t : Thick(weight, t));
        }

        // An arm from the top / bottom edge. Single lines run through the centre square (they join every horizontal
        // line); double lines stop one line-width inside it (inner and outer lines turn the corner separately).
        private static void VerticalArm(int weight, bool isUp, double L, double R, double T, double B, double y0, double y1, double midY, bool hasH, double t, double s, List<TermGlyphPrim> o)
        {
            if (weight == 0) return;
            double th = weight == 3 ? t : Thick(weight, t);
            double from, to;
            if (!hasH)
            {
                // straight line: both halves meet in the middle; a lone half line ends just past it
                from = isUp ? T : midY - Math.Floor(th / 2.0);
                to = isUp ? midY + Math.Ceiling(th / 2.0) : B;
            }
            else if (weight == 3) { from = isUp ? T : y1 - t; to = isUp ? y0 + t : B; }
            else { from = isUp ? T : y0; to = isUp ? y1 : B; }
            if (weight == 3)
            {
                double px = Centre(L, R, 3 * t);
                Rect(px, from, t, to - from, s, o);
                Rect(px + 2 * t, from, t, to - from, s, o);
            }
            else Rect(Centre(L, R, th), from, th, to - from, s, o);
        }

        private static void HorizontalArm(int weight, bool isLeft, double L, double R, double T, double B, double x0, double x1, double midX, bool hasV, double t, double s, List<TermGlyphPrim> o)
        {
            if (weight == 0) return;
            double th = weight == 3 ? t : Thick(weight, t);
            double from, to;
            if (!hasV)
            {
                from = isLeft ? L : midX - Math.Floor(th / 2.0);
                to = isLeft ? midX + Math.Ceiling(th / 2.0) : R;
            }
            else if (weight == 3) { from = isLeft ? L : x1 - t; to = isLeft ? x0 + t : R; }
            else { from = isLeft ? L : x0; to = isLeft ? x1 : R; }
            if (weight == 3)
            {
                double py = Centre(T, B, 3 * t);
                Rect(from, py, to - from, t, s, o);
                Rect(from, py + 2 * t, to - from, t, s, o);
            }
            else Rect(from, Centre(T, B, th), to - from, th, s, o);
        }

        private static void Dashed(int up, int right, int down, int left, int n, double L, double T, double R, double B, double t, double s, List<TermGlyphPrim> o)
        {
            bool horizontal = left > 0 || right > 0;
            double th = Thick(horizontal ? Math.Max(left, right) : Math.Max(up, down), t);
            if (horizontal)
            {
                double ty = Centre(T, B, th), span = (R - L) / n, gap = Math.Max(1, Math.Round(span * 0.4));
                for (int i = 0; i < n; i++)
                {
                    double a = Math.Round(L + i * span + gap / 2), b = Math.Round(L + (i + 1) * span - gap / 2);
                    if (b > a) Rect(a, ty, b - a, th, s, o);
                }
            }
            else
            {
                double lx = Centre(L, R, th), span = (B - T) / n, gap = Math.Max(1, Math.Round(span * 0.4));
                for (int i = 0; i < n; i++)
                {
                    double a = Math.Round(T + i * span + gap / 2), b = Math.Round(T + (i + 1) * span - gap / 2);
                    if (b > a) Rect(lx, a, th, b - a, s, o);
                }
            }
        }

        private static void Arc(char ch, double L, double T, double R, double B, double t, double s, List<TermGlyphPrim> o)
        {
            // quarter curve from the middle of one edge to the middle of the other, through the line centres of the
            // neighbouring straight lines (xterm.js: C.5,1,.5,.5,1,.5 for U+256D)
            double cx = Centre(L, R, t) + t / 2.0, cy = Centre(T, B, t) + t / 2.0;
            double sx, sy, ex, ey;
            switch (ch)
            {
                case '╭': sx = cx; sy = B; ex = R; ey = cy; break; // down and right
                case '╮': sx = cx; sy = B; ex = L; ey = cy; break; // down and left
                case '╯': sx = cx; sy = T; ex = L; ey = cy; break; // up and left
                default: sx = cx; sy = T; ex = R; ey = cy; break;       // up and right
            }
            const int steps = 10;
            double[] pts = new double[(steps + 1) * 2];
            for (int i = 0; i <= steps; i++)
            {
                double u = i / (double)steps, v = 1 - u;
                // quadratic Bezier with the control point in the cell centre
                pts[i * 2] = (v * v * sx + 2 * v * u * cx + u * u * ex) / s;
                pts[i * 2 + 1] = (v * v * sy + 2 * v * u * cy + u * u * ey) / s;
            }
            Polyline(pts, t / s, o);
        }

        private static void Diagonal(char ch, double L, double T, double R, double B, double t, double s, List<TermGlyphPrim> o)
        {
            if (ch == '╱' || ch == '╳') Polyline(new double[] { R / s, T / s, L / s, B / s }, t / s, o);
            if (ch == '╲' || ch == '╳') Polyline(new double[] { L / s, T / s, R / s, B / s }, t / s, o);
        }

        private static void Block(char ch, double L, double T, double R, double B, double s, List<TermGlyphPrim> o)
        {
            double W = R - L, H = B - T;
            int code = ch - '▀';
            if (code == 0) { Rect(L, T, W, Math.Round(H / 2), s, o); return; } // upper half
            if (code >= 1 && code <= 8) // lower n/8 (the complement of the upper part, so U+2580 + U+2584 = full)
            {
                double hh = H - Math.Round(H * (8 - code) / 8.0);
                Rect(L, B - hh, W, hh, s, o);
                return;
            }
            if (code >= 9 && code <= 15) // left (16 - code)/8: 7/8 .. 1/8
            {
                double ww = Math.Round(W * (16 - code) / 8.0);
                Rect(L, T, Math.Max(1, ww), H, s, o);
                return;
            }
            if (code == 16) { double hw = Math.Round(W / 2); Rect(L + hw, T, W - hw, H, s, o); return; } // right half
            if (code >= 17 && code <= 19) // shades
            {
                TermGlyphPrim p = MakeRect(L, T, W, H, s);
                p.Alpha = code == 17 ? 0.25 : (code == 18 ? 0.5 : 0.75);
                o.Add(p);
                return;
            }
            if (code == 20) { Rect(L, T, W, Math.Max(1, Math.Round(H / 8)), s, o); return; } // upper 1/8
            if (code == 21) { double ww = Math.Max(1, Math.Round(W / 8)); Rect(R - ww, T, ww, H, s, o); return; } // right 1/8
            // quadrants: bit 1 upper left, 2 upper right, 4 lower left, 8 lower right
            int[] quads = { 4, 8, 1, 1 | 4 | 8, 1 | 8, 1 | 2 | 4, 1 | 2 | 8, 2, 2 | 4, 2 | 4 | 8 };
            int q = quads[code - 22];
            double mx = L + Math.Round(W / 2), my = T + Math.Round(H / 2);
            if ((q & 1) != 0) Rect(L, T, mx - L, my - T, s, o);
            if ((q & 2) != 0) Rect(mx, T, R - mx, my - T, s, o);
            if ((q & 4) != 0) Rect(L, my, mx - L, B - my, s, o);
            if ((q & 8) != 0) Rect(mx, my, R - mx, B - my, s, o);
        }

        private static void Powerline(char ch, double L, double T, double R, double B, double s, List<TermGlyphPrim> o)
        {
            double my = (T + B) / 2.0;
            double t = LightThickness(R - L, s);
            switch (ch)
            {
                case '\uE0B0': o.Add(Polygon(new double[] { L / s, T / s, R / s, my / s, L / s, B / s })); break;
                case '\uE0B2': o.Add(Polygon(new double[] { R / s, T / s, L / s, my / s, R / s, B / s })); break;
                case '\uE0B1': Polyline(new double[] { L / s, T / s, R / s, my / s, L / s, B / s }, t / s, o); break;
                default: Polyline(new double[] { R / s, T / s, L / s, my / s, R / s, B / s }, t / s, o); break;
            }
        }

        private static TermGlyphPrim MakeRect(double x, double y, double w, double h, double s)
        {
            TermGlyphPrim p = new TermGlyphPrim();
            p.Kind = TermGlyphPrim.FillRect;
            p.X = x / s; p.Y = y / s; p.W = w / s; p.H = h / s;
            p.Alpha = 1;
            return p;
        }

        private static void Rect(double x, double y, double w, double h, double s, List<TermGlyphPrim> o)
        {
            if (w <= 0 || h <= 0) return;
            o.Add(MakeRect(x, y, w, h, s));
        }

        private static TermGlyphPrim Polygon(double[] pts)
        {
            TermGlyphPrim p = new TermGlyphPrim();
            p.Kind = TermGlyphPrim.FillPolygon;
            p.Points = pts;
            p.Alpha = 1;
            return p;
        }

        private static void Polyline(double[] pts, double thickness, List<TermGlyphPrim> o)
        {
            TermGlyphPrim p = new TermGlyphPrim();
            p.Kind = TermGlyphPrim.StrokePolyline;
            p.Points = pts;
            p.Thickness = thickness;
            p.Alpha = 1;
            o.Add(p);
        }
    }
}
