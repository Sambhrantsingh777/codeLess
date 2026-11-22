using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Media;
using Avalonia.Styling;
using codeLess.Studio;

namespace codeLess.AvaloniaApp
{
    /// <summary>
    /// VS Code input option toggles (find widget, Search view): Match Case, Match Whole Word, Use Regular Expression,
    /// Preserve Case, Find in Selection. 20 px buttons with 16 px glyphs, a neutral hover fill, and an obvious ON state
    /// (accent-tinted fill + 1 px accent border, kept while hovered), tooltips with the platform shortcut. The v3.3.1
    /// report said these options did not exist: they were three faint 14 px glyphs with no visible state.
    /// </summary>
    public static class FindOptionToggles
    {
        public const string MatchCase = "case";
        public const string WholeWord = "word";
        public const string Regex = "regex";
        public const string PreserveCase = "preserve";
        public const string InSelection = "selection";

        private static bool stylesInstalled;

        /// <summary>Title and key letter of an option ("Match Case", 'C').</summary>
        public static string TitleOf(string option)
        {
            switch (option)
            {
                case MatchCase: return "Match Case";
                case WholeWord: return "Match Whole Word";
                case Regex: return "Use Regular Expression";
                case PreserveCase: return "Preserve Case";
                case InSelection: return "Find in Selection";
            }
            return option;
        }

        private static Key KeyOf(string option)
        {
            switch (option)
            {
                case MatchCase: return Key.C;
                case WholeWord: return Key.W;
                case Regex: return Key.R;
                case PreserveCase: return Key.P;
                case InSelection: return Key.L;
            }
            return Key.None;
        }

        private static PhysicalKey PhysicalOf(string option)
        {
            switch (option)
            {
                case MatchCase: return PhysicalKey.C;
                case WholeWord: return PhysicalKey.W;
                case Regex: return PhysicalKey.R;
                case PreserveCase: return PhysicalKey.P;
                case InSelection: return PhysicalKey.L;
            }
            return PhysicalKey.None;
        }

        /// <summary>Platform shortcut label: "⌥⌘C" on macOS, "Alt+C" on Windows / Linux (VS Code defaults).</summary>
        public static string ShortcutLabel(string option)
        {
            string k = KeyOf(option).ToString();
            return MacKeymap.IsMacOS ? "⌥⌘" + k : "Alt+" + k;
        }

        public static string Tooltip(string option)
        {
            return TitleOf(option) + " (" + ShortcutLabel(option) + ")";
        }

        private static string IconOf(string option)
        {
            switch (option)
            {
                case MatchCase: return "case-sensitive";
                case WholeWord: return "whole-word";
                case Regex: return "regex";
                case PreserveCase: return "preserve-case";
                case InSelection: return "find-selection";
            }
            return option;
        }

        /// <summary>
        /// The option a key press toggles, or null: Alt+C/W/R/P/L on Windows / Linux, Cmd+Option+C/W/R/P/L on macOS
        /// (matched by physical key too: Option changes the produced character, e.g. ⌥C = "ç").
        /// </summary>
        public static string OptionForKey(Key key, PhysicalKey physical, KeyModifiers mods)
        {
            bool alt = (mods & KeyModifiers.Alt) != 0;
            bool shift = (mods & KeyModifiers.Shift) != 0;
            bool ctrl = (mods & KeyModifiers.Control) != 0;
            bool meta = (mods & KeyModifiers.Meta) != 0;
            if (!alt || shift) return null;
            bool ok = MacKeymap.IsMacOS ? (meta && !ctrl) : (!ctrl && !meta);
            if (!ok) return null;
            foreach (string o in new[] { MatchCase, WholeWord, Regex, PreserveCase, InSelection })
                if (key == KeyOf(o) || (physical != PhysicalKey.None && physical == PhysicalOf(o))) return o;
            return null;
        }

        public static Button Create(string option, StudioTheme theme)
        {
            var t = theme ?? StudioTheme.GetById(StudioTheme.DefaultThemeId);
            var glyph = new VectorIconControl
            {
                IconName = IconOf(option),
                Width = UiTokens.InputOptionIconSize,
                Height = UiTokens.InputOptionIconSize,
                IconColor = WorkbenchStyles.C(UiTokens.InputOptionForeground(t, false)),
                StrokeWidth = 1.3f
            };
            var b = new Button
            {
                Content = glyph,
                Width = UiTokens.InputOptionSize,
                Height = UiTokens.InputOptionSize,
                Padding = new Thickness(0),
                Background = Brushes.Transparent,
                BorderThickness = new Thickness(1),
                BorderBrush = Brushes.Transparent,
                CornerRadius = new CornerRadius(3),
                HorizontalContentAlignment = HorizontalAlignment.Center,
                VerticalContentAlignment = VerticalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Focusable = false,
                Tag = option
            };
            b.Classes.Add("inputOption");
            ToolTip.SetTip(b, Tooltip(option));
            AutomationHelpText(b, option);
            return b;
        }

        private static void AutomationHelpText(Button b, string option)
        {
            try { global::Avalonia.Automation.AutomationProperties.SetName(b, TitleOf(option)); } catch { }
        }

        /// <summary>Applies the ON / OFF look (and refreshes the hover resources for the theme).</summary>
        public static void Style(Button b, bool on, StudioTheme t)
        {
            if (b == null || t == null) return;
            EnsureStyles(t);
            if (on) { if (!b.Classes.Contains("on")) b.Classes.Add("on"); }
            else b.Classes.Remove("on");
            b.Background = on ? WorkbenchStyles.B(UiTokens.InputOptionActiveBackground(t)) : Brushes.Transparent;
            b.BorderBrush = on ? WorkbenchStyles.B(UiTokens.InputOptionActiveBorder(t)) : Brushes.Transparent;
            var fg = UiTokens.InputOptionForeground(t, on);
            b.Foreground = WorkbenchStyles.B(fg);
            if (b.Content is VectorIconControl ic) { ic.IconColor = WorkbenchStyles.C(fg); ic.InvalidateVisual(); }
        }

        public static bool IsOn(Button b) => b != null && b.Classes.Contains("on");

        private static void EnsureStyles(StudioTheme t)
        {
            var app = Application.Current;
            if (app == null) return;
            var r = app.Resources;
            r["CodelessInputOptionHover"] = WorkbenchStyles.B(UiTokens.InputOptionHoverBackground(t));
            r["CodelessInputOptionActive"] = WorkbenchStyles.B(UiTokens.InputOptionActiveBackground(t));
            r["CodelessInputOptionActiveBorder"] = WorkbenchStyles.B(UiTokens.InputOptionActiveBorder(t));
            r["CodelessInputOptionFg"] = WorkbenchStyles.B(t.FgPrimary);
            if (stylesInstalled) return;
            stylesInstalled = true;
            global::Avalonia.Styling.Style S(Func<Selector, Selector> sel, AvaloniaProperty p, object v)
            {
                var st = new global::Avalonia.Styling.Style(sel);
                st.Setters.Add(new Setter(p, v));
                return st;
            }
            foreach (var state in new[] { ":pointerover", ":pressed" })
            {
                string s = state;
                Func<Selector, Selector> off = x => x.OfType<Button>().Class("inputOption").Class(s).Template().OfType<ContentPresenter>().Name("PART_ContentPresenter");
                Func<Selector, Selector> on = x => x.OfType<Button>().Class("inputOption").Class("on").Class(s).Template().OfType<ContentPresenter>().Name("PART_ContentPresenter");
                app.Styles.Add(S(off, ContentPresenter.BackgroundProperty, new DynamicResourceExtension("CodelessInputOptionHover")));
                app.Styles.Add(S(off, ContentPresenter.BorderBrushProperty, Brushes.Transparent));
                app.Styles.Add(S(off, ContentPresenter.ForegroundProperty, new DynamicResourceExtension("CodelessInputOptionFg")));
                app.Styles.Add(S(on, ContentPresenter.BackgroundProperty, new DynamicResourceExtension("CodelessInputOptionActive")));
                app.Styles.Add(S(on, ContentPresenter.BorderBrushProperty, new DynamicResourceExtension("CodelessInputOptionActiveBorder")));
            }
        }
    }
}
