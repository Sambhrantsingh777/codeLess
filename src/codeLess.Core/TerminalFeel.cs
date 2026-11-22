using System;
using System.Collections.Generic;
using System.Threading;

namespace codeLess.Studio
{
    /// <summary>
    /// Runtime terminal settings shared by both terminal UIs, mirroring VS Code's terminal.integrated.* defaults:
    /// cursorStyle "block", cursorStyleInactive "outline", cursorBlinking false, cursorWidth 1, smoothScrolling false,
    /// fontSize 12 on macOS / 14 elsewhere, lineHeight 1, minimumContrastRatio 4.5, drawBoldTextInBrightColors true,
    /// mouseWheelScrollSensitivity 1, fastScrollSensitivity 5. STRICT C# 5.
    /// </summary>
    public static class TerminalOptions
    {
        public static string CursorStyle = TerminalCursorStyle.Block;
        public static string CursorStyleInactive = TerminalCursorStyle.Outline;
        public static bool CursorBlinking = false;
        /// <summary>Width of the "line" cursor in pixels (terminal.integrated.cursorWidth).</summary>
        public static int CursorWidth = 1;
        public static bool SmoothScrolling = false;
        /// <summary>Font size in pixels; 0 = the platform default (<see cref="DefaultFontSize"/>).</summary>
        public static double FontSize = 0;
        public static double LineHeight = 1.0;
        /// <summary>Font family list; empty = the editor's monospace family.</summary>
        public static string FontFamily = "";
        public static double MinimumContrastRatio = 4.5;
        public static bool DrawBoldTextInBrightColors = true;
        public static double MouseWheelScrollSensitivity = 1.0;
        public static double FastScrollSensitivity = 5.0;

        /// <summary>xterm.js smooth scroll duration when terminal.integrated.smoothScrolling is on (ms).</summary>
        public const double SmoothScrollDurationMs = 125;
        /// <summary>xterm.js cursor blink interval (ms).</summary>
        public const double BlinkIntervalMs = 600;

        /// <summary>VS Code's terminal font size: 12 on macOS, 14 elsewhere.</summary>
        public static double DefaultFontSize(bool isMac) { return isMac ? 12 : 14; }

        public static double EffectiveFontSize(bool isMac)
        {
            double v = FontSize;
            if (double.IsNaN(v) || v < 6 || v > 100) return DefaultFontSize(isMac);
            return v;
        }

        public static double EffectiveLineHeight
        {
            get { return ClampLineHeight(LineHeight); }
        }

        public static double ClampLineHeight(double v)
        {
            if (double.IsNaN(v) || double.IsInfinity(v) || v < 1) return 1.0;
            return Math.Min(3.0, v);
        }

        public static void ApplyFrom(IdePersistentSettings s)
        {
            if (s == null) return;
            CursorStyle = TerminalCursorStyle.NormalizeActive(s.TerminalCursorStyle);
            CursorStyleInactive = TerminalCursorStyle.NormalizeInactive(s.TerminalCursorStyleInactive);
            CursorBlinking = s.TerminalCursorBlinking;
            SmoothScrolling = s.TerminalSmoothScrolling;
            FontSize = s.TerminalFontSize;
            LineHeight = ClampLineHeight(s.TerminalLineHeight);
            FontFamily = s.TerminalFontFamily ?? "";
            MinimumContrastRatio = ClampContrast(s.TerminalMinimumContrastRatio);
            MouseWheelScrollSensitivity = EditorScrollOptions.ClampSensitivity(s.TerminalMouseWheelScrollSensitivity, 1.0);
            FastScrollSensitivity = EditorScrollOptions.ClampSensitivity(s.TerminalFastScrollSensitivity, 5.0);
        }

        public static void CopyTo(IdePersistentSettings s)
        {
            if (s == null) return;
            s.TerminalCursorStyle = CursorStyle;
            s.TerminalCursorStyleInactive = CursorStyleInactive;
            s.TerminalCursorBlinking = CursorBlinking;
            s.TerminalSmoothScrolling = SmoothScrolling;
            s.TerminalFontSize = FontSize;
            s.TerminalLineHeight = LineHeight;
            s.TerminalFontFamily = FontFamily ?? "";
            s.TerminalMinimumContrastRatio = MinimumContrastRatio;
            s.TerminalMouseWheelScrollSensitivity = MouseWheelScrollSensitivity;
            s.TerminalFastScrollSensitivity = FastScrollSensitivity;
        }

        /// <summary>block -> line -> underline -> block (command palette).</summary>
        public static string CycleCursorStyle()
        {
            int i = Array.IndexOf(TerminalCursorStyle.ActiveStyles, TerminalCursorStyle.NormalizeActive(CursorStyle));
            CursorStyle = TerminalCursorStyle.ActiveStyles[(i + 1) % TerminalCursorStyle.ActiveStyles.Length];
            return CursorStyle;
        }

        /// <summary>Parses a font size / line height / ratio typed in the settings (current value when unparsable).</summary>
        public static double ParseNumber(string text, double current)
        {
            double v;
            string t = (text ?? "").Trim().Replace(',', '.');
            if (t.Length == 0) return 0;
            if (double.TryParse(t, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out v)) return v;
            return current;
        }

        public static string FormatNumber(double v)
        {
            return v.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture);
        }

        /// <summary>1 (off) .. 21; unparsable values fall back to VS Code's 4.5.</summary>
        public static double ClampContrast(double v)
        {
            if (double.IsNaN(v) || double.IsInfinity(v) || v <= 0) return 4.5;
            return Math.Max(1.0, Math.Min(21.0, v));
        }

        /// <summary>
        /// Rows to scroll for a wheel delta (Avalonia / WPF units: a notch = 1.0, precise trackpad deltas are points / 50;
        /// WinForms passes Delta / 120). Positive = towards older output.
        /// </summary>
        public static double WheelRows(double delta, bool precise, double rowHeight, bool fast)
        {
            double h = Math.Max(1.0, rowHeight);
            double px = precise ? delta * 50.0 : delta * 3.0 * h;
            return px / h * MouseWheelScrollSensitivity * (fast ? FastScrollSensitivity : 1.0);
        }
    }

    /// <summary>Persisted terminal settings (absent keys keep the VS Code defaults when loading old files).</summary>
    public partial class IdePersistentSettings
    {
        private string terminalCursorStyle = codeLess.Studio.TerminalCursorStyle.Block;
        private string terminalCursorStyleInactive = codeLess.Studio.TerminalCursorStyle.Outline;
        private bool terminalCursorBlinking;
        private bool terminalSmoothScrolling;
        private double terminalFontSize;
        private double terminalLineHeight = 1.0;
        private string terminalFontFamily = "";
        private double terminalMinimumContrastRatio = 4.5;
        private double terminalMouseWheelScrollSensitivity = 1.0;
        private double terminalFastScrollSensitivity = 5.0;

        public string TerminalCursorStyle { get { return terminalCursorStyle; } set { terminalCursorStyle = codeLess.Studio.TerminalCursorStyle.NormalizeActive(value); } }
        public string TerminalCursorStyleInactive { get { return terminalCursorStyleInactive; } set { terminalCursorStyleInactive = codeLess.Studio.TerminalCursorStyle.NormalizeInactive(value); } }
        public bool TerminalCursorBlinking { get { return terminalCursorBlinking; } set { terminalCursorBlinking = value; } }
        public bool TerminalSmoothScrolling { get { return terminalSmoothScrolling; } set { terminalSmoothScrolling = value; } }
        /// <summary>Pixels; 0 = platform default (12 on macOS, 14 elsewhere).</summary>
        public double TerminalFontSize { get { return terminalFontSize; } set { terminalFontSize = value; } }
        public double TerminalLineHeight { get { return terminalLineHeight; } set { terminalLineHeight = value; } }
        public string TerminalFontFamily { get { return terminalFontFamily; } set { terminalFontFamily = value ?? ""; } }
        public double TerminalMinimumContrastRatio { get { return terminalMinimumContrastRatio; } set { terminalMinimumContrastRatio = value; } }
        public double TerminalMouseWheelScrollSensitivity { get { return terminalMouseWheelScrollSensitivity; } set { terminalMouseWheelScrollSensitivity = value; } }
        public double TerminalFastScrollSensitivity { get { return terminalFastScrollSensitivity; } set { terminalFastScrollSensitivity = value; } }
    }

    /// <summary>terminal.integrated.cursorStyle / cursorStyleInactive values and the DECSCUSR override. STRICT C# 5.</summary>
    public static class TerminalCursorStyle
    {
        public const string Block = "block", Line = "line", Underline = "underline", Outline = "outline", None = "none";
        public static readonly string[] ActiveStyles = new string[] { Block, Line, Underline };
        public static readonly string[] InactiveStyles = new string[] { Outline, Block, Line, Underline, None };

        public static string NormalizeActive(string s)
        {
            string v = (s ?? "").Trim().ToLowerInvariant();
            if (v == "bar") v = Line;
            for (int i = 0; i < ActiveStyles.Length; i++) if (ActiveStyles[i] == v) return v;
            return Block;
        }

        public static string NormalizeInactive(string s)
        {
            string v = (s ?? "").Trim().ToLowerInvariant();
            if (v == "bar") v = Line;
            for (int i = 0; i < InactiveStyles.Length; i++) if (InactiveStyles[i] == v) return v;
            return Outline;
        }

        /// <summary>
        /// The cursor to draw: the application's DECSCUSR shape (1/2 block, 3/4 underline, 5/6 bar; odd = blinking)
        /// wins over the user's style and blink setting like in xterm.js; 0 means the user's settings. Unfocused
        /// terminals draw <paramref name="inactiveStyle"/> and never blink.
        /// </summary>
        public static void Resolve(string activeStyle, string inactiveStyle, bool blinkSetting, int decscusr, bool focused, out string style, out bool blink)
        {
            style = NormalizeActive(activeStyle);
            blink = blinkSetting;
            if (decscusr >= 1 && decscusr <= 6)
            {
                style = decscusr <= 2 ? Block : (decscusr <= 4 ? Underline : Line);
                blink = (decscusr % 2) == 1;
            }
            if (!focused)
            {
                style = NormalizeInactive(inactiveStyle);
                blink = false;
            }
        }

        /// <summary>Cursor rectangle inside a cell (x, y = cell origin): block = cell, line = left bar, underline = bottom bar.</summary>
        public static void Geometry(string style, double x, double y, double cellW, double cellH, double lineWidth, out double rx, out double ry, out double rw, out double rh)
        {
            rx = x; ry = y; rw = cellW; rh = cellH;
            double lw = Math.Max(1.0, lineWidth);
            if (style == Line) { rw = Math.Min(cellW, lw); }
            else if (style == Underline) { rh = Math.Min(cellH, lw); ry = y + cellH - rh; }
        }

        /// <summary>Cursor visibility <paramref name="msSinceReset"/> after the last input (xterm.js: solid, then 600 ms on / off).</summary>
        public static bool BlinkVisible(bool blink, double msSinceReset)
        {
            if (!blink || msSinceReset < TerminalOptions.BlinkIntervalMs) return true;
            long phase = (long)Math.Floor(msSinceReset / TerminalOptions.BlinkIntervalMs);
            return phase % 2 == 0;
        }
    }

    /// <summary>The visible rows and the cursor copied from a <see cref="Vt100TerminalBuffer"/> in one lock. STRICT C# 5.</summary>
    public sealed class TerminalViewportSnapshot
    {
        public int Cols, Rows;
        public TermCell[][] Cells = new TermCell[0][];
        public int ScrollbackOffset, ScrollbackCount;
        public long ScrollbackPushCount;
        public int CursorRow, CursorCol, CursorShape;
        public bool CursorVisible, IsAlternateScreen, SynchronizedOutput;
        public int DefaultFgArgb, DefaultBgArgb;
        public long Version;

        /// <summary>Sizes the row arrays (kept between frames while the grid size stays).</summary>
        public void Prepare(int cols, int rows)
        {
            if (rows != Rows || cols != Cols || Cells.Length != rows)
            {
                TermCell[][] n = new TermCell[rows][];
                for (int r = 0; r < rows; r++) n[r] = new TermCell[cols];
                Cells = n;
                Cols = cols;
                Rows = rows;
            }
        }

        public TermCell Cell(int row, int col)
        {
            if (row < 0 || row >= Rows || col < 0 || col >= Cols) return new TermCell();
            return Cells[row][col];
        }

        /// <summary>Text of a row, one character per cell ('\0' continuation / empty cells as spaces), trailing blanks trimmed.</summary>
        public string RowText(int row)
        {
            if (row < 0 || row >= Rows) return "";
            char[] chars = new char[Cols];
            TermCell[] line = Cells[row];
            for (int c = 0; c < Cols; c++) chars[c] = line[c].Char == '\0' ? ' ' : line[c].Char;
            return new string(chars).TrimEnd();
        }

        /// <summary>FNV-1a hash of a row's cells (renderers key their row caches with it, then compare the cells).</summary>
        public static ulong HashRow(TermCell[] cells, int count)
        {
            ulong h = 14695981039346656037UL;
            for (int c = 0; c < count; c++)
            {
                TermCell t = cells[c];
                h = (h ^ t.Char) * 1099511628211UL;
                h = (h ^ t.Flags) * 1099511628211UL;
                h = (h ^ (uint)t.FgArgb) * 1099511628211UL;
                h = (h ^ (uint)t.BgArgb) * 1099511628211UL;
            }
            return h ^ (ulong)count;
        }

        public static bool SameCells(TermCell[] a, TermCell[] b, int count)
        {
            if (a == null || b == null || a.Length < count || b.Length < count) return false;
            for (int c = 0; c < count; c++) if (!a[c].SameAs(b[c])) return false;
            return true;
        }
    }

    /// <summary>
    /// Scrollback rows as a ring buffer: appending to a full scrollback drops the oldest row in O(1) (a List shifted all
    /// 5000 references per line, which dominated the cost of large outputs). STRICT C# 5.
    /// </summary>
    public sealed class TermRowRing
    {
        private TermCell[][] items = new TermCell[64][];
        private int head, count;

        public int Count { get { return count; } }

        public TermCell[] this[int index]
        {
            get
            {
                if (index < 0 || index >= count) throw new ArgumentOutOfRangeException("index");
                return items[(head + index) % items.Length];
            }
        }

        /// <summary>Appends a row, dropping the oldest rows while the ring holds <paramref name="max"/> or more.</summary>
        public void Add(TermCell[] row, int max)
        {
            if (max < 1) max = 1;
            while (count >= max) RemoveFirst();
            if (count == items.Length)
            {
                int size = Math.Max(count + 1, Math.Min(max, items.Length * 2));
                TermCell[][] n = new TermCell[size][];
                for (int i = 0; i < count; i++) n[i] = items[(head + i) % items.Length];
                items = n;
                head = 0;
            }
            items[(head + count) % items.Length] = row;
            count++;
        }

        public void RemoveFirst()
        {
            if (count == 0) return;
            items[head] = null;
            head = (head + 1) % items.Length;
            count--;
        }

        public void Clear()
        {
            Array.Clear(items, 0, items.Length);
            head = 0;
            count = 0;
        }
    }

    /// <summary>
    /// The terminal viewport's scroll position, shared by both terminal UIs. Like xterm.js the position is kept exactly
    /// (precise trackpad deltas accumulate, fractional while scrolling) and the rendered rows snap to the nearest whole
    /// row (xterm.js ydisp = round(scrollTop / rowHeight)); the scrollbar follows the exact position. Mouse-wheel notches
    /// animate for 125 ms when terminal.integrated.smoothScrolling is on, trackpad deltas always apply 1:1. While the user
    /// is scrolled back, new output does not move the rows being read (the position is anchored to the buffer's
    /// <see cref="Vt100TerminalBuffer.ScrollbackPushCount"/>); at the bottom the view follows the output. Units are rows
    /// back from the live screen (0 = bottom). STRICT C# 5.
    /// </summary>
    public sealed class TerminalScrollModel
    {
        private readonly SmoothScrollAnimation anim = new SmoothScrollAnimation();
        private readonly WheelScrollClassifier classifier = new WheelScrollClassifier();
        private double offset;
        private long lastPushCount = -1;
        private int lastScrollbackCount;

        /// <summary>Exact rows back from the live screen (fractional while scrolling precisely or animating).</summary>
        public double Offset { get { return offset; } }

        /// <summary>Whole rows back the viewport shows (the exact position rounded, like xterm.js).</summary>
        public int DisplayOffset
        {
            get
            {
                int v = (int)Math.Round(offset, MidpointRounding.AwayFromZero);
                return Math.Max(0, Math.Min(lastScrollbackCount, v));
            }
        }

        public bool IsAnimating { get { return anim.IsActive; } }
        /// <summary>Where a running animation ends (the position when idle).</summary>
        public double Target { get { return anim.IsActive ? anim.Target : offset; } }
        /// <summary>True while scrolled back from the live screen.</summary>
        public bool IsScrolledBack { get { return offset > 1e-6 || (anim.IsActive && anim.Target > 1e-6); } }

        /// <summary>
        /// Follows buffer changes: keeps the same rows in view while scrolled back (output pushed N rows into the
        /// scrollback moves the offset by N; rows trimmed from the top clamp it), stays at the bottom otherwise.
        /// </summary>
        public void Sync(long scrollbackPushCount, int scrollbackCount)
        {
            if (scrollbackCount < 0) scrollbackCount = 0;
            long delta = lastPushCount < 0 ? 0 : scrollbackPushCount - lastPushCount;
            lastPushCount = scrollbackPushCount;
            lastScrollbackCount = scrollbackCount;
            if (delta < 0) delta = 0; // a new buffer / session
            if (IsScrolledBack && delta > 0)
            {
                offset += delta;
                if (anim.IsActive) anim.ShiftBy(delta);
            }
            Clamp(scrollbackCount);
        }

        private void Clamp(int scrollbackCount)
        {
            if (offset < 0) offset = 0;
            if (offset > scrollbackCount) offset = scrollbackCount;
            if (anim.IsActive) anim.ClampTarget(0, scrollbackCount);
        }

        /// <summary>
        /// Applies a wheel / trackpad delta (positive = towards older output). Precise streams apply immediately; isolated
        /// notches animate when <paramref name="smooth"/> is on. Returns true when the position (or its animation) changed.
        /// </summary>
        public bool Wheel(double delta, double nowMs, double rowHeight, bool fast, bool smooth, int scrollbackCount, int viewportRows)
        {
            if (Math.Abs(delta) < 1e-9) return false;
            lastScrollbackCount = Math.Max(0, scrollbackCount);
            bool precise = classifier.IsPrecise(0, delta, nowMs);
            double rows = TerminalOptions.WheelRows(delta, precise, rowHeight, fast);
            double before = Target;
            if (precise || !smooth)
            {
                if (anim.IsActive) { anim.ShiftBy(rows); anim.ClampTarget(0, lastScrollbackCount); }
                offset += rows;
                Clamp(lastScrollbackCount);
                return Math.Abs(Target - before) > 1e-9 || anim.IsActive;
            }
            if (!anim.IsActive) anim.Cancel(offset);
            anim.AddToTarget(offset, rows, Math.Max(1, viewportRows), nowMs, TerminalOptions.SmoothScrollDurationMs);
            anim.ClampTarget(0, lastScrollbackCount);
            if (!anim.IsActive) return Math.Abs(offset - before) > 1e-9;
            return true;
        }

        /// <summary>Advances a running animation; true while it keeps running.</summary>
        public bool Tick(double nowMs)
        {
            if (!anim.IsActive) return false;
            offset = anim.Sample(nowMs);
            Clamp(lastScrollbackCount);
            return anim.IsActive;
        }

        /// <summary>Jumps to a position (scrollbar drag, Shift+PageUp / PageDown, tests); stops any animation.</summary>
        public void SetOffset(double rows, int scrollbackCount)
        {
            lastScrollbackCount = Math.Max(0, scrollbackCount);
            anim.Cancel(rows);
            offset = rows;
            Clamp(lastScrollbackCount);
        }

        /// <summary>Back to the live screen (typing, paste, clear: xterm.js scrollOnUserInput).</summary>
        public void ScrollToBottom()
        {
            anim.Cancel(0);
            offset = 0;
        }

        public void ResetClassifier() { classifier.Reset(); }
    }

    /// <summary>
    /// Coalesces PTY output into at most one repaint per painted frame (requestAnimationFrame-like): the reader thread
    /// asks for a repaint only when none is pending, and the pending flag is cleared when a frame starts painting
    /// (before it snapshots the buffer, so output arriving during the paint schedules the next frame). Thread-safe.
    /// STRICT C# 5.
    /// </summary>
    public sealed class TerminalRepaintGate
    {
        private int pending;
        private long outputEvents, repaintRequests, framesPainted;

        /// <summary>Reader thread: output arrived. True when the caller should post one repaint to the UI thread.</summary>
        public bool OnOutput()
        {
            Interlocked.Increment(ref outputEvents);
            if (Interlocked.CompareExchange(ref pending, 1, 0) != 0) return false;
            Interlocked.Increment(ref repaintRequests);
            return true;
        }

        /// <summary>UI thread: a frame starts painting (call before snapshotting the buffer).</summary>
        public void OnFramePainting()
        {
            Interlocked.Exchange(ref pending, 0);
            Interlocked.Increment(ref framesPainted);
        }

        /// <summary>Drops a pending request (the control cannot paint now; the next output asks again).</summary>
        public void Reset() { Interlocked.Exchange(ref pending, 0); }

        public bool IsPending { get { return Interlocked.CompareExchange(ref pending, 0, 0) != 0; } }
        public long OutputEvents { get { return Interlocked.Read(ref outputEvents); } }
        public long RepaintRequests { get { return Interlocked.Read(ref repaintRequests); } }
        public long FramesPainted { get { return Interlocked.Read(ref framesPainted); } }
    }

    /// <summary>
    /// PTY window-size updates like VS Code's TerminalResizeDebouncer: rows apply immediately; with a large buffer
    /// (200+ lines) column changes wait 100 ms, so dragging a sash does not reflow the scrollback and make the shell
    /// redraw its prompt on every pixel. STRICT C# 5.
    /// </summary>
    public static class TerminalResizePolicy
    {
        public const int DebounceColumnsMs = 100;
        public const int StartDebouncingThreshold = 200;

        public static bool ShouldDebounceColumns(int bufferLines, int currentCols, int newCols)
        {
            return newCols != currentCols && bufferLines >= StartDebouncingThreshold;
        }
    }

    /// <summary>Cell metrics like xterm.js: the cell is the font's advance wide and its line height (rounded up to device pixels) tall, times lineHeight. STRICT C# 5.</summary>
    public static class TerminalCellMetrics
    {
        /// <param name="charWidth">Advance of one monospace character.</param>
        /// <param name="charHeight">Natural line height of the font (ascent + descent + gap).</param>
        public static void Compute(double charWidth, double charHeight, double lineHeight, double scale, out double cellW, out double cellH, out double textTop)
        {
            if (scale <= 0 || double.IsNaN(scale)) scale = 1;
            lineHeight = TerminalOptions.ClampLineHeight(lineHeight);
            cellW = Math.Max(1.0, charWidth);
            double deviceCharH = Math.Ceiling(Math.Max(1.0, charHeight) * scale - 1e-6);
            double deviceCellH = Math.Floor(deviceCharH * lineHeight + 1e-6);
            cellH = deviceCellH / scale;
            textTop = Math.Round((deviceCellH - deviceCharH) / 2.0) / scale;
        }
    }

    /// <summary>
    /// WCAG contrast adjustment of xterm.js (terminal.integrated.minimumContrastRatio): a foreground too close to its
    /// background is lightened or darkened in 10 % steps until the ratio is met. Colours are 0xAARRGGBB. STRICT C# 5.
    /// </summary>
    public static class TerminalContrast
    {
        public static double RelativeLuminance(int rgb)
        {
            return Lum((rgb >> 16) & 0xFF, (rgb >> 8) & 0xFF, rgb & 0xFF);
        }

        public static double Ratio(double l1, double l2)
        {
            return l1 < l2 ? (l2 + 0.05) / (l1 + 0.05) : (l1 + 0.05) / (l2 + 0.05);
        }

        /// <summary>The foreground to draw (alpha kept); unchanged when the ratio is met or <paramref name="ratio"/> &lt;= 1.</summary>
        public static int Ensure(int fg, int bg, double ratio)
        {
            if (ratio <= 1.0) return fg;
            double bgL = RelativeLuminance(bg);
            double fgL = RelativeLuminance(fg);
            if (Ratio(bgL, fgL) >= ratio) return fg;
            int a, b;
            if (fgL < bgL)
            {
                a = Reduce(bg, fg, ratio);
                double ra = Ratio(bgL, RelativeLuminance(a));
                if (ra >= ratio) return a;
                b = Increase(bg, fg, ratio);
                return ra > Ratio(bgL, RelativeLuminance(b)) ? a : b;
            }
            a = Increase(bg, fg, ratio);
            double ra2 = Ratio(bgL, RelativeLuminance(a));
            if (ra2 >= ratio) return a;
            b = Reduce(bg, fg, ratio);
            return ra2 > Ratio(bgL, RelativeLuminance(b)) ? a : b;
        }

        private static int Reduce(int bg, int fg, double ratio)
        {
            int r = (fg >> 16) & 0xFF, g = (fg >> 8) & 0xFF, b = fg & 0xFF;
            double bgL = RelativeLuminance(bg);
            double cr = Ratio(Lum(r, g, b), bgL);
            while (cr < ratio && (r > 0 || g > 0 || b > 0))
            {
                r -= Math.Max(0, (int)Math.Ceiling(r * 0.1));
                g -= Math.Max(0, (int)Math.Ceiling(g * 0.1));
                b -= Math.Max(0, (int)Math.Ceiling(b * 0.1));
                cr = Ratio(Lum(r, g, b), bgL);
            }
            return (fg & unchecked((int)0xFF000000)) | (r << 16) | (g << 8) | b;
        }

        private static int Increase(int bg, int fg, double ratio)
        {
            int r = (fg >> 16) & 0xFF, g = (fg >> 8) & 0xFF, b = fg & 0xFF;
            double bgL = RelativeLuminance(bg);
            double cr = Ratio(Lum(r, g, b), bgL);
            while (cr < ratio && (r < 0xFF || g < 0xFF || b < 0xFF))
            {
                r = Math.Min(0xFF, r + (int)Math.Ceiling((255 - r) * 0.1));
                g = Math.Min(0xFF, g + (int)Math.Ceiling((255 - g) * 0.1));
                b = Math.Min(0xFF, b + (int)Math.Ceiling((255 - b) * 0.1));
                cr = Ratio(Lum(r, g, b), bgL);
            }
            return (fg & unchecked((int)0xFF000000)) | (r << 16) | (g << 8) | b;
        }

        private static double Lum(int r, int g, int b)
        {
            return Chan(r) * 0.2126 + Chan(g) * 0.7152 + Chan(b) * 0.0722;
        }

        // sRGB channel -> linear, precomputed (Math.Pow per channel made contrast checks dominate dense colour frames)
        private static readonly double[] LinearChannel = BuildLinearTable();

        private static double[] BuildLinearTable()
        {
            double[] t = new double[256];
            for (int v = 0; v < 256; v++)
            {
                double s = v / 255.0;
                t[v] = s <= 0.03928 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4);
            }
            return t;
        }

        private static double Chan(int v)
        {
            return LinearChannel[v < 0 ? 0 : (v > 255 ? 255 : v)];
        }
    }
}
