using System;
using System.Drawing;

namespace codeLess.Studio
{
    /// <summary>
    /// Shared workbench design tokens (see docs/UI_GUIDELINES.md). Used by BOTH UIs so the Windows (WinForms) and
    /// macOS/Linux (Avalonia) workbenches have the same type scale, metrics and state colors.
    /// Sizes are device-independent pixels (Avalonia units). WinForms fonts are in points: use <see cref="Pt"/>.
    /// Colors are derived from the active <see cref="StudioTheme"/> roles so all built-in themes (light and dark)
    /// stay consistent; nothing here is theme-specific.
    /// STRICT C# 5 (compiled by the .NET Framework 4.8 build).
    /// </summary>
    public static class UiTokens
    {
        // ------------------------------------------------------------------ typography (px)
        /// <summary>Default UI text: tree/list rows, tabs, inputs, menus, buttons.</summary>
        public const double FontUi = 13;
        /// <summary>Secondary UI text: status bar, hints, descriptions, badges, panel meta text.</summary>
        public const double FontSmall = 12;
        /// <summary>UPPERCASE view / section / panel titles (semibold).</summary>
        public const double FontHeader = 11;
        /// <summary>Tiny counters inside badges.</summary>
        public const double FontBadge = 10;
        /// <summary>Dialog / welcome headings.</summary>
        public const double FontTitle = 20;

        /// <summary>Points for WinForms fonts from a pixel size (96 dpi: 1 pt = 4/3 px).</summary>
        public static float Pt(double px) { return (float)(px * 0.75); }

        // ------------------------------------------------------------------ metrics (px)
        public const int TitleBarHeight = 34;
        public const int ActivityBarWidth = 48;
        public const int ActivityIconSize = 24;
        /// <summary>Side bar view title row ("EXPLORER") and panel tab row height.</summary>
        public const int ViewHeaderHeight = 35;
        /// <summary>Collapsible section header row inside a view ("OPEN EDITORS", "OUTLINE").</summary>
        public const int SectionHeaderHeight = 22;
        /// <summary>Tree / list row height (explorer, search results, outline, SCM, terminal list, completion).</summary>
        public const int ListRowHeight = 22;
        /// <summary>Editor tab height.</summary>
        public const int TabHeight = 35;
        public const int PanelHeaderHeight = 35;
        public const int StatusBarHeight = 22;
        /// <summary>Per-level tree indentation and the twistie (chevron) column width.</summary>
        public const int TreeIndent = 12;
        public const int TreeTwistieWidth = 16;
        /// <summary>Compact input / combo box / button height.</summary>
        public const int ControlHeight = 26;
        /// <summary>Square toolbar icon button and the glyph drawn inside it.</summary>
        public const int IconButtonSize = 22;
        public const int IconSize = 16;
        /// <summary>Horizontal content padding of views and panels.</summary>
        public const int ViewPaddingX = 12;
        /// <summary>Inputs, combo boxes, buttons, check boxes (VS Code Modern: 2 px).</summary>
        public const int ControlRadius = 2;
        /// <summary>Hover/pressed background of flat toolbar icon buttons and status/list chips.</summary>
        public const int ToolbarRadius = 4;
        /// <summary>Floating widgets: find widget, suggest/hover, notifications, palette, menus, dialogs.</summary>
        public const int PopupRadius = 6;
        /// <summary>All dividers are 1 px BorderSubtle; active editor tab = 2 px accent on top, panel tab = 1 px accent below.</summary>
        public const int DividerWidth = 1;
        public const int TabIndicatorWidth = 2;
        public const int PanelTabIndicatorWidth = 1;
        /// <summary>Transient status-bar messages are cleared after this many milliseconds.</summary>
        public const int StatusMessageTimeoutMs = 8000;
        /// <summary>Longest status-bar message (characters) before it is ellipsized.</summary>
        public const int StatusMessageMaxChars = 90;

        // ------------------------------------------------------------------ color helpers
        public static Color Blend(Color baseColor, Color over, double amount)
        {
            if (amount < 0) amount = 0;
            if (amount > 1) amount = 1;
            return Color.FromArgb(255,
                (int)Math.Round(baseColor.R + (over.R - baseColor.R) * amount),
                (int)Math.Round(baseColor.G + (over.G - baseColor.G) * amount),
                (int)Math.Round(baseColor.B + (over.B - baseColor.B) * amount));
        }

        public static Color WithAlpha(Color c, int alpha)
        {
            return Color.FromArgb(Math.Max(0, Math.Min(255, alpha)), c.R, c.G, c.B);
        }

        public static bool IsLight(StudioTheme t)
        {
            return t != null && t.IsLight;
        }

        /// <summary>HSL saturation (0..1) — used to tell brand-colored status bars from neutral ones.</summary>
        public static double Saturation(Color c)
        {
            double r = c.R / 255.0, g = c.G / 255.0, b = c.B / 255.0;
            double max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b));
            double l = (max + min) / 2.0;
            if (max - min < 0.0001) return 0;
            return l > 0.5 ? (max - min) / (2.0 - max - min) : (max - min) / (max + min);
        }

        // ------------------------------------------------------------------ state colors (opaque, per surface)
        /// <summary>Pointer-over row / toolbar button background on <paramref name="surface"/>.</summary>
        public static Color Hover(StudioTheme t, Color surface)
        {
            return Blend(surface, t.FgPrimary, IsLight(t) ? 0.06 : 0.07);
        }

        /// <summary>Pressed toolbar button / toggled-on toggle background.</summary>
        public static Color Pressed(StudioTheme t, Color surface)
        {
            return Blend(surface, t.FgPrimary, IsLight(t) ? 0.11 : 0.13);
        }

        /// <summary>Selected row in a focused list/tree: a calm accent tint (never a saturated fill).</summary>
        public static Color ListActiveSelection(StudioTheme t, Color surface)
        {
            return Blend(surface, t.AccentPrimary, IsLight(t) ? 0.16 : 0.24);
        }

        /// <summary>Selected row when the list is not focused / "current item" marker (neutral).</summary>
        public static Color ListInactiveSelection(StudioTheme t, Color surface)
        {
            return Blend(surface, t.FgPrimary, IsLight(t) ? 0.08 : 0.10);
        }

        public static Color InputBackground(StudioTheme t)
        {
            return IsLight(t) ? Color.White : Blend(t.BgSidebar, t.FgPrimary, 0.045);
        }

        public static Color InputBorder(StudioTheme t)
        {
            return IsLight(t) ? Blend(t.BgEditor, t.FgPrimary, 0.20) : Blend(t.BgSidebar, t.FgPrimary, 0.13);
        }

        public static Color FocusBorder(StudioTheme t)
        {
            return t.AccentPrimary;
        }

        public static Color ButtonPrimaryBackground(StudioTheme t)
        {
            return t.AccentPrimary;
        }

        public static Color ButtonPrimaryHover(StudioTheme t)
        {
            return Blend(t.AccentPrimary, Color.Black, 0.12);
        }

        public static Color ButtonPrimaryForeground(StudioTheme t)
        {
            return OnAccent(t);
        }

        /// <summary>Text/glyph color on an accent fill: white, or near-black for light accents (e.g. Monokai).</summary>
        public static Color OnAccent(StudioTheme t)
        {
            if (t == null) return Color.White;
            Color a = t.AccentPrimary;
            double lum = (0.299 * a.R + 0.587 * a.G + 0.114 * a.B) / 255.0;
            return lum > 0.62 ? Color.FromArgb(24, 24, 24) : Color.White;
        }

        public static Color ButtonSecondaryBackground(StudioTheme t)
        {
            return IsLight(t) ? Blend(t.BgEditor, t.FgPrimary, 0.07) : Blend(t.BgSidebar, t.FgPrimary, 0.10);
        }

        public static Color ButtonSecondaryHover(StudioTheme t)
        {
            return IsLight(t) ? Blend(t.BgEditor, t.FgPrimary, 0.12) : Blend(t.BgSidebar, t.FgPrimary, 0.15);
        }

        /// <summary>Floating widgets (find widget, suggest, hover, notifications, palette, menus).</summary>
        public static Color WidgetBackground(StudioTheme t)
        {
            return t.BgElevated;
        }

        public static Color WidgetBorder(StudioTheme t)
        {
            return IsLight(t) ? Blend(t.BgElevated, t.FgPrimary, 0.16) : Blend(t.BgElevated, t.FgPrimary, 0.10);
        }

        /// <summary>Drop-shadow color for floating widgets (alpha included).</summary>
        public static Color WidgetShadow(StudioTheme t)
        {
            return Color.FromArgb(IsLight(t) ? 46 : 120, 0, 0, 0);
        }

        public static Color Divider(StudioTheme t)
        {
            return t.BorderSubtle;
        }

        public static Color IndentGuide(StudioTheme t, Color surface)
        {
            return Blend(surface, t.FgPrimary, IsLight(t) ? 0.13 : 0.11);
        }

        public static Color IndentGuideActive(StudioTheme t, Color surface)
        {
            return Blend(surface, t.FgPrimary, IsLight(t) ? 0.35 : 0.32);
        }

        /// <summary>Count badges (activity bar, search counts): neutral on lists, accent on the activity bar.</summary>
        public static Color BadgeBackground(StudioTheme t)
        {
            return IsLight(t) ? Blend(t.BgSidebar, t.FgPrimary, 0.12) : Blend(t.BgSidebar, t.FgPrimary, 0.16);
        }

        // ------------------------------------------------------------------ status bar
        /// <summary>
        /// Slim, calm status bar: themes whose status color is a saturated brand color get a neutral bar (title-bar
        /// background) with the brand color kept for the left workspace badge; neutral theme status colors are kept.
        /// </summary>
        public static Color StatusBarBackground(StudioTheme t)
        {
            if (t == null) return Color.FromArgb(24, 24, 24);
            if (Saturation(t.BgStatus) > 0.35 && !IsLight(t)) return t.BgActivity;
            return t.BgStatus;
        }

        public static Color StatusBarForeground(StudioTheme t)
        {
            Color bg = StatusBarBackground(t);
            return IsLight(t) ? Blend(t.FgPrimary, bg, 0.12) : Blend(t.FgPrimary, bg, 0.18);
        }

        public static Color StatusBarItemHover(StudioTheme t)
        {
            return Blend(StatusBarBackground(t), t.FgPrimary, IsLight(t) ? 0.10 : 0.12);
        }

        public static Color StatusBarBorder(StudioTheme t)
        {
            return t.BorderSubtle;
        }

        /// <summary>Left workspace badge ("remote indicator" position) — the only accent fill in the status bar.</summary>
        public static Color StatusBadgeBackground(StudioTheme t)
        {
            return t.AccentPrimary;
        }

        /// <summary>Shortens a transient status message so it never pushes the right-hand items off the bar.</summary>
        public static string ClampStatusMessage(string message)
        {
            if (string.IsNullOrEmpty(message)) return "";
            string m = message.Replace("\r", " ").Replace("\n", " ").Trim();
            if (m.Length <= StatusMessageMaxChars) return m;
            return m.Substring(0, StatusMessageMaxChars - 1).TrimEnd() + "…";
        }
    }
}
