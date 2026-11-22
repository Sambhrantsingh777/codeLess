using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Styling;
using codeLess.Studio;

namespace codeLess.AvaloniaApp
{
    /// <summary>
    /// Workbench-wide control styling (docs/UI_GUIDELINES.md): re-skins the Fluent default controls (TextBox,
    /// ComboBox, Button, CheckBox, menus, tooltips, scroll bars) into compact, flat, theme-colored VS Code-like
    /// controls. Colors are pushed as Fluent resource overrides on every theme change; metrics are set once.
    /// </summary>
    public static class WorkbenchStyles
    {
        private static bool stylesInstalled;

        public static Color C(System.Drawing.Color c) => AvaloniaColorHelper.ToAvColor(c);
        public static SolidColorBrush B(System.Drawing.Color c) => new SolidColorBrush(AvaloniaColorHelper.ToAvColor(c));

        /// <summary>Installs metric styles once and refreshes all theme-derived resources.</summary>
        public static void Apply(StudioTheme t)
        {
            var app = Application.Current;
            if (app == null || t == null) return;
            if (!stylesInstalled)
            {
                stylesInstalled = true;
                InstallMetricStyles(app);
            }
            ApplyColorResources(app.Resources, t);
        }

        private static void ApplyColorResources(IResourceDictionary r, StudioTheme t)
        {
            bool light = t.IsLight;
            var fg = B(t.FgPrimary);
            var fg2 = B(t.FgSecondary);
            var muted = B(t.FgMuted);
            var transparent = Brushes.Transparent;
            var inputBg = B(UiTokens.InputBackground(t));
            var inputBorder = B(UiTokens.InputBorder(t));
            var inputBorderHover = new SolidColorBrush(C(UiTokens.Blend(UiTokens.InputBorder(t), t.FgPrimary, 0.12)));
            var focus = B(UiTokens.FocusBorder(t));
            var btnBg = B(UiTokens.ButtonSecondaryBackground(t));
            var btnHover = B(UiTokens.ButtonSecondaryHover(t));
            var btnPressed = new SolidColorBrush(C(UiTokens.Blend(UiTokens.ButtonSecondaryHover(t), t.FgPrimary, 0.06)));
            var widget = B(UiTokens.WidgetBackground(t));
            var widgetBorder = B(UiTokens.WidgetBorder(t));
            var menuHover = B(UiTokens.ListActiveSelection(t, UiTokens.WidgetBackground(t)));
            var accent = B(t.AccentPrimary);
            var disabledFg = new SolidColorBrush(C(UiTokens.WithAlpha(t.FgSecondary, 120)));

            // Accent (Fluent derives checkbox / selection / focus colors from these)
            r["SystemAccentColor"] = C(t.AccentPrimary);
            r["SystemAccentColorDark1"] = C(UiTokens.Blend(t.AccentPrimary, System.Drawing.Color.Black, 0.12));
            r["SystemAccentColorLight1"] = C(UiTokens.Blend(t.AccentPrimary, System.Drawing.Color.White, 0.15));
            r["TextControlSelectionHighlightColor"] = new SolidColorBrush(C(UiTokens.WithAlpha(t.AccentPrimary, 110)));

            // Text boxes
            r["TextControlForeground"] = fg;
            r["TextControlForegroundPointerOver"] = fg;
            r["TextControlForegroundFocused"] = fg;
            r["TextControlForegroundDisabled"] = disabledFg;
            r["TextControlBackground"] = inputBg;
            r["TextControlBackgroundPointerOver"] = inputBg;
            r["TextControlBackgroundFocused"] = inputBg;
            r["TextControlBackgroundDisabled"] = inputBg;
            r["TextControlBorderBrush"] = inputBorder;
            r["TextControlBorderBrushPointerOver"] = inputBorderHover;
            r["TextControlBorderBrushFocused"] = focus;
            r["TextControlBorderBrushDisabled"] = inputBorder;
            r["TextControlPlaceholderForeground"] = muted;
            r["TextControlPlaceholderForegroundPointerOver"] = muted;
            r["TextControlPlaceholderForegroundFocused"] = muted;
            r["TextControlPlaceholderForegroundDisabled"] = muted;
            r["TextControlButtonForeground"] = fg2;
            r["TextControlButtonForegroundPointerOver"] = fg;
            r["TextControlButtonBackgroundPointerOver"] = btnHover;

            // Buttons (secondary by default; primary buttons set Classes="primary" or local accent colors)
            r["ButtonBackground"] = btnBg;
            r["ButtonBackgroundPointerOver"] = btnHover;
            r["ButtonBackgroundPressed"] = btnPressed;
            r["ButtonBackgroundDisabled"] = btnBg;
            r["ButtonForeground"] = fg;
            r["ButtonForegroundPointerOver"] = fg;
            r["ButtonForegroundPressed"] = fg;
            r["ButtonForegroundDisabled"] = disabledFg;
            r["ButtonBorderBrush"] = transparent;
            r["ButtonBorderBrushPointerOver"] = transparent;
            r["ButtonBorderBrushPressed"] = transparent;
            r["ButtonBorderBrushDisabled"] = transparent;

            // Combo boxes look like inputs
            r["ComboBoxBackground"] = inputBg;
            r["ComboBoxBackgroundPointerOver"] = inputBg;
            r["ComboBoxBackgroundPressed"] = inputBg;
            r["ComboBoxBackgroundDisabled"] = inputBg;
            r["ComboBoxBackgroundUnfocused"] = inputBg;
            r["ComboBoxBorderBrush"] = inputBorder;
            r["ComboBoxBorderBrushPointerOver"] = inputBorderHover;
            r["ComboBoxBorderBrushPressed"] = focus;
            r["ComboBoxBorderBrushDisabled"] = inputBorder;
            r["ComboBoxBackgroundBorderBrushFocused"] = focus;
            r["ComboBoxBackgroundBorderBrushUnfocused"] = inputBorder;
            r["ComboBoxForeground"] = fg;
            r["ComboBoxForegroundFocused"] = fg;
            r["ComboBoxForegroundFocusedPressed"] = fg;
            r["ComboBoxForegroundDisabled"] = disabledFg;
            r["ComboBoxPlaceHolderForeground"] = muted;
            r["ComboBoxDropDownGlyphForeground"] = fg2;
            r["ComboBoxDropDownGlyphForegroundFocused"] = fg2;
            r["ComboBoxDropDownGlyphForegroundFocusedPressed"] = fg;
            r["ComboBoxDropDownBackground"] = widget;
            r["ComboBoxDropDownBorderBrush"] = widgetBorder;
            r["ComboBoxItemForeground"] = fg;
            r["ComboBoxItemForegroundPointerOver"] = fg;
            r["ComboBoxItemForegroundSelected"] = fg;
            r["ComboBoxItemForegroundSelectedPointerOver"] = fg;
            r["ComboBoxItemForegroundPressed"] = fg;
            r["ComboBoxItemForegroundSelectedPressed"] = fg;
            r["ComboBoxItemBackgroundPointerOver"] = B(UiTokens.Hover(t, UiTokens.WidgetBackground(t)));
            r["ComboBoxItemBackgroundSelected"] = menuHover;
            r["ComboBoxItemBackgroundSelectedPointerOver"] = menuHover;
            r["ComboBoxItemBackgroundPressed"] = menuHover;
            r["ComboBoxItemBackgroundSelectedPressed"] = menuHover;

            // Check boxes: small, input-colored box, accent when checked
            r["CheckBoxForegroundUnchecked"] = fg;
            r["CheckBoxForegroundUncheckedPointerOver"] = fg;
            r["CheckBoxForegroundChecked"] = fg;
            r["CheckBoxForegroundCheckedPointerOver"] = fg;
            r["CheckBoxForegroundUncheckedPressed"] = fg;
            r["CheckBoxForegroundCheckedPressed"] = fg;
            r["CheckBoxCheckBackgroundFillUnchecked"] = inputBg;
            r["CheckBoxCheckBackgroundFillUncheckedPointerOver"] = inputBg;
            r["CheckBoxCheckBackgroundFillUncheckedPressed"] = inputBg;
            r["CheckBoxCheckBackgroundStrokeUnchecked"] = B(UiTokens.CheckBoxBorder(t));
            r["CheckBoxCheckBackgroundStrokeUncheckedPointerOver"] = B(UiTokens.Blend(UiTokens.CheckBoxBorder(t), t.FgPrimary, 0.25));
            r["CheckBoxCheckBackgroundStrokeUncheckedPressed"] = focus;
            r["CheckBoxCheckBackgroundFillChecked"] = accent;
            r["CheckBoxCheckBackgroundFillCheckedPointerOver"] = accent;
            r["CheckBoxCheckBackgroundFillCheckedPressed"] = accent;
            r["CheckBoxCheckBackgroundStrokeCheckedPointerOver"] = accent;
            r["CheckBoxCheckBackgroundStrokeCheckedPressed"] = accent;
            r["CheckBoxCheckGlyphForegroundChecked"] = B(UiTokens.OnAccent(t));
            r["CheckBoxCheckGlyphForegroundCheckedPointerOver"] = B(UiTokens.OnAccent(t));
            r["CheckBoxCheckGlyphForegroundCheckedPressed"] = B(UiTokens.OnAccent(t));

            // Menus / context menus / combo popups
            r["MenuFlyoutPresenterBackground"] = widget;
            r["MenuFlyoutPresenterBorderBrush"] = widgetBorder;
            r["MenuFlyoutItemForeground"] = fg;
            r["MenuFlyoutItemForegroundPointerOver"] = fg;
            r["MenuFlyoutItemForegroundPressed"] = fg;
            r["MenuFlyoutItemForegroundDisabled"] = disabledFg;
            r["MenuFlyoutItemBackgroundPointerOver"] = menuHover;
            r["MenuFlyoutItemBackgroundPressed"] = menuHover;
            r["MenuFlyoutItemKeyboardAcceleratorTextForeground"] = fg2;
            r["MenuFlyoutItemKeyboardAcceleratorTextForegroundPointerOver"] = fg2;
            r["MenuFlyoutItemKeyboardAcceleratorTextForegroundPressed"] = fg2;
            r["MenuFlyoutSubItemChevron"] = fg2;
            r["MenuFlyoutSubItemChevronPointerOver"] = fg;
            r["MenuFlyoutSubItemChevronSubMenuOpened"] = fg;

            // List boxes (palette, problems, outline, pickers): calm accent-tinted selection, neutral hover
            r["SystemControlHighlightListAccentLowBrush"] = B(UiTokens.ListActiveSelection(t, t.BgSidebar));
            r["SystemControlHighlightListAccentMediumBrush"] = B(UiTokens.Blend(UiTokens.ListActiveSelection(t, t.BgSidebar), t.FgPrimary, 0.04));
            r["SystemControlHighlightListAccentHighBrush"] = B(UiTokens.Blend(UiTokens.ListActiveSelection(t, t.BgSidebar), t.FgPrimary, 0.08));
            r["SystemControlHighlightListLowBrush"] = B(UiTokens.Hover(t, t.BgSidebar));
            r["SystemControlHighlightListMediumBrush"] = B(UiTokens.Pressed(t, t.BgSidebar));

            // Tool tips
            r["ToolTipBackground"] = widget;
            r["ToolTipForeground"] = fg;
            r["ToolTipBorderBrush"] = widgetBorder;

            // Scroll bars: thin, VS Code slider colors (the same tokens as the editor / list / terminal scrollbars)
            var thumb = new SolidColorBrush(C(UiTokens.ScrollbarSlider(t)));
            r["ScrollBarThumbFillPointerOver"] = new SolidColorBrush(C(UiTokens.ScrollbarSliderHover(t)));
            r["ScrollBarThumbFillPressed"] = new SolidColorBrush(C(UiTokens.ScrollbarSliderActive(t)));
            r["ScrollBarThumbBackgroundColor"] = C(UiTokens.ScrollbarSlider(t));
            r["ScrollBarPanningThumbBackground"] = thumb;
            r["ScrollBarTrackFill"] = transparent;
            r["ScrollBarTrackFillPointerOver"] = transparent;
            r["ScrollBarTrackStroke"] = transparent;
            r["ScrollBarTrackStrokePointerOver"] = transparent;
            r["ScrollBarBackground"] = transparent;
            r["ScrollBarBackgroundPointerOver"] = transparent;
            r["ScrollBarButtonArrowForeground"] = fg2;
        }

        private static void InstallMetricStyles(Application app)
        {
            var r = app.Resources;
            r["ControlCornerRadius"] = new CornerRadius(UiTokens.ControlRadius);
            r["OverlayCornerRadius"] = new CornerRadius(UiTokens.PopupRadius);
            r["ControlContentThemeFontSize"] = UiTokens.FontUi;
            r["TextControlThemeMinHeight"] = (double)UiTokens.ControlHeight;
            r["TextControlThemePadding"] = new Thickness(8, 4, 8, 4);
            r["TextControlBorderThemeThickness"] = new Thickness(1);
            r["TextControlBorderThemeThicknessFocused"] = new Thickness(1);
            r["ComboBoxMinHeight"] = (double)UiTokens.ControlHeight;
            r["ComboBoxPadding"] = new Thickness(8, 3, 0, 3);
            r["ComboBoxItemThemePadding"] = new Thickness(8, 4, 8, 4);
            r["ButtonPadding"] = new Thickness(10, 4, 10, 4);
            r["ListBoxItemPadding"] = new Thickness(8, 2, 8, 2);
            r["MenuFlyoutItemThemePadding"] = new Thickness(10, 5, 10, 5);
            r["MenuFlyoutItemThemePaddingNarrow"] = new Thickness(10, 4, 10, 4);
            r["MenuFlyoutPresenterThemePadding"] = new Thickness(0, 4, 0, 4);
            r["MenuFlyoutThemeMinHeight"] = 24d;
            r["ToolTipContentThemeFontSize"] = UiTokens.FontSmall;
            r["ToolTipBorderThemePadding"] = new Thickness(8, 4, 8, 5);

            // Styles beat ControlTheme setters, so these win over Fluent's defaults even where Fluent uses
            // StaticResource for metrics.
            app.Styles.Add(MakeStyle(x => x.OfType<TextBox>(),
                (TemplatedControl.MinHeightProperty, (object)(double)UiTokens.ControlHeight),
                (TemplatedControl.PaddingProperty, new Thickness(8, 4, 8, 4)),
                (TemplatedControl.CornerRadiusProperty, new CornerRadius(UiTokens.ControlRadius)),
                (TemplatedControl.BorderThicknessProperty, new Thickness(1))));
            app.Styles.Add(MakeStyle(x => x.OfType<TextBox>().Class(":focus").Template().OfType<Border>().Name("PART_BorderElement"),
                (Border.BorderThicknessProperty, (object)new Thickness(1))));
            app.Styles.Add(MakeStyle(x => x.OfType<ComboBox>(),
                (TemplatedControl.MinHeightProperty, (object)(double)UiTokens.ControlHeight),
                (TemplatedControl.PaddingProperty, new Thickness(8, 2, 0, 2)),
                (TemplatedControl.CornerRadiusProperty, new CornerRadius(UiTokens.ControlRadius)),
                (TemplatedControl.BorderThicknessProperty, new Thickness(1)),
                (TemplatedControl.FontSizeProperty, UiTokens.FontSmall + 0.5)));
            app.Styles.Add(MakeStyle(x => x.OfType<ComboBoxItem>(),
                (TemplatedControl.PaddingProperty, (object)new Thickness(8, 3, 8, 3)),
                (TemplatedControl.MinHeightProperty, (double)UiTokens.ListRowHeight),
                (TemplatedControl.FontSizeProperty, UiTokens.FontSmall + 0.5)));
            app.Styles.Add(MakeStyle(x => x.OfType<ListBoxItem>(),
                (TemplatedControl.PaddingProperty, (object)new Thickness(8, 2, 8, 2)),
                (Layoutable.MinHeightProperty, (double)UiTokens.ListRowHeight)));
            app.Styles.Add(MakeStyle(x => x.OfType<Button>(),
                (TemplatedControl.CornerRadiusProperty, (object)new CornerRadius(UiTokens.ControlRadius))));
            app.Styles.Add(MakeStyle(x => x.OfType<CheckBox>(),
                (TemplatedControl.MinHeightProperty, (object)22d),
                (TemplatedControl.FontSizeProperty, UiTokens.FontUi)));
            // The Fluent template sets the 20 px box size on the part itself (template priority); a selector with a
            // pseudo-class activator runs at style-trigger priority, which wins over template values.
            app.Styles.Add(MakeStyle(x => x.OfType<CheckBox>().Not(y => y.Class(":__never")).Template().OfType<Border>().Name("NormalRectangle"),
                (Layoutable.WidthProperty, (object)16d),
                (Layoutable.HeightProperty, 16d),
                (Border.CornerRadiusProperty, new CornerRadius(3)),
                (Border.BorderThicknessProperty, new Thickness(1))));
            app.Styles.Add(MakeStyle(x => x.OfType<CheckBox>().Template().OfType<Viewbox>(),
                (Layoutable.WidthProperty, (object)10d),
                (Layoutable.HeightProperty, 10d)));
            app.Styles.Add(MakeStyle(x => x.OfType<MenuItem>(),
                (TemplatedControl.FontSizeProperty, (object)UiTokens.FontUi),
                (TemplatedControl.PaddingProperty, new Thickness(10, 4, 10, 4))));
            app.Styles.Add(MakeStyle(x => x.OfType<ContextMenu>(),
                (TemplatedControl.CornerRadiusProperty, (object)new CornerRadius(UiTokens.PopupRadius)),
                (TemplatedControl.PaddingProperty, new Thickness(4)),
                (TemplatedControl.BorderThicknessProperty, new Thickness(1))));
            app.Styles.Add(MakeStyle(x => x.OfType<MenuFlyoutPresenter>(),
                (TemplatedControl.CornerRadiusProperty, (object)new CornerRadius(UiTokens.PopupRadius)),
                (TemplatedControl.PaddingProperty, new Thickness(4)),
                (TemplatedControl.BorderThicknessProperty, new Thickness(1))));
            app.Styles.Add(MakeStyle(x => x.OfType<ToolTip>(),
                (TemplatedControl.CornerRadiusProperty, (object)new CornerRadius(UiTokens.ControlRadius)),
                (TemplatedControl.FontSizeProperty, UiTokens.FontSmall)));
            app.Styles.Add(MakeStyle(x => x.OfType<ScrollBar>().Class(":vertical"),
                (Layoutable.WidthProperty, (object)10d)));
            app.Styles.Add(MakeStyle(x => x.OfType<ScrollBar>().Class(":horizontal"),
                (Layoutable.HeightProperty, (object)10d)));
        }

        private static Style MakeStyle(Func<Selector, Selector> selector, params (AvaloniaProperty prop, object value)[] setters)
        {
            var s = new Style(selector);
            foreach (var (prop, value) in setters)
                s.Setters.Add(new Setter(prop, value));
            return s;
        }

        // ------------------------------------------------------------------ shared component builders

        /// <summary>Flat square toolbar button with a vector glyph (replaces ASCII glyph buttons like 'x', '^', '✎').</summary>
        public static Button IconButton(string icon, string tooltip, Action onClick, StudioTheme theme = null, double size = UiTokens.IconButtonSize, double iconSize = UiTokens.IconSize)
        {
            var t = theme ?? StudioTheme.GetById(StudioTheme.DefaultThemeId);
            var glyph = new VectorIconControl { IconName = icon, Width = iconSize, Height = iconSize, IconColor = C(t.FgSecondary), StrokeWidth = 1.4f, FollowForeground = true };
            var b = new Button
            {
                Content = glyph,
                Width = size,
                Height = size,
                Padding = new Thickness(0),
                Background = Brushes.Transparent,
                Foreground = B(t.FgSecondary),
                BorderThickness = new Thickness(0),
                CornerRadius = new CornerRadius(UiTokens.ToolbarRadius),
                HorizontalContentAlignment = HorizontalAlignment.Center,
                VerticalContentAlignment = VerticalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Focusable = false
            };
            b.Classes.Add("icon");
            if (!string.IsNullOrEmpty(tooltip)) ToolTip.SetTip(b, tooltip);
            if (onClick != null) b.Click += (s, e) => { onClick(); e.Handled = true; };
            return b;
        }

        /// <summary>Re-colors an <see cref="IconButton"/> glyph for the theme.</summary>
        public static void ThemeIconButton(Button b, StudioTheme t, bool active = false)
        {
            if (b == null || t == null) return;
            b.Foreground = B(active ? t.FgPrimary : t.FgSecondary);
            if (b.Content is VectorIconControl ic)
            {
                ic.IconColor = C(active ? t.FgPrimary : t.FgSecondary);
                ic.InvalidateVisual();
            }
        }

        /// <summary>Primary (accent) button colors.</summary>
        public static void StylePrimaryButton(Button b, StudioTheme t)
        {
            if (b == null || t == null) return;
            if (!b.Classes.Contains("primary")) b.Classes.Add("primary");
            b.Background = B(UiTokens.ButtonPrimaryBackground(t));
            b.Foreground = B(UiTokens.ButtonPrimaryForeground(t));
            b.BorderThickness = new Thickness(0);
            b.CornerRadius = new CornerRadius(UiTokens.ControlRadius);
        }

        /// <summary>Secondary (neutral) button colors.</summary>
        public static void StyleSecondaryButton(Button b, StudioTheme t)
        {
            if (b == null || t == null) return;
            b.Background = B(UiTokens.ButtonSecondaryBackground(t));
            b.Foreground = B(t.FgPrimary);
            b.BorderThickness = new Thickness(0);
            b.CornerRadius = new CornerRadius(UiTokens.ControlRadius);
        }

        /// <summary>
        /// VS Code-style notification toast: widget background, 1 px widget border, 6 px radius, soft shadow, severity
        /// glyph, 13 px message, optional 12 px detail, right-aligned buttons (first = primary when requested) and a
        /// close glyph. Re-theme with <see cref="ThemeNotification"/>.
        /// </summary>
        public static Border BuildNotification(StudioTheme t, string icon, TextBlock message, Control detail, Button[] buttons, bool firstIsPrimary, Action onClose, double width = 400)
        {
            message.TextWrapping = TextWrapping.Wrap;
            message.FontSize = UiTokens.FontUi;
            message.VerticalAlignment = VerticalAlignment.Top;
            var glyph = new VectorIconControl { IconName = icon ?? "info", Width = 16, Height = 16, StrokeWidth = 1.5f, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 1, 0, 0), Tag = "severity" };
            var close = IconButton("close", "Close", onClose, t, 20, 14);
            close.VerticalAlignment = VerticalAlignment.Top;
            var head = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto") };
            message.Margin = new Thickness(8, 0, 6, 0);
            head.Children.Add(glyph);
            Grid.SetColumn(message, 1);
            head.Children.Add(message);
            Grid.SetColumn(close, 2);
            head.Children.Add(close);
            var stack = new StackPanel { Spacing = 8 };
            stack.Children.Add(head);
            if (detail != null)
            {
                detail.Margin = new Thickness(24, -2, 0, 0);
                stack.Children.Add(detail);
            }
            if (buttons != null && buttons.Length > 0)
            {
                var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, HorizontalAlignment = HorizontalAlignment.Right };
                for (int i = 0; i < buttons.Length; i++)
                {
                    var b = buttons[i];
                    b.FontSize = UiTokens.FontSmall;
                    b.Padding = new Thickness(10, 3);
                    b.MinHeight = 24;
                    if (i == 0 && firstIsPrimary) b.Classes.Add("primary");
                    row.Children.Add(b);
                }
                stack.Children.Add(row);
            }
            var toast = new Border
            {
                Child = stack,
                Width = width,
                Padding = new Thickness(12, 10, 8, 10),
                CornerRadius = new CornerRadius(UiTokens.PopupRadius),
                BorderThickness = new Thickness(1),
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Bottom
            };
            ThemeNotification(toast, t);
            return toast;
        }

        /// <summary>Applies theme colors to a toast from <see cref="BuildNotification"/> (or any similar card).</summary>
        public static void ThemeNotification(Border toast, StudioTheme t)
        {
            if (toast == null || t == null) return;
            toast.Background = B(UiTokens.WidgetBackground(t));
            toast.BorderBrush = B(UiTokens.WidgetBorder(t));
            toast.BoxShadow = WidgetShadow(t);
            ThemeTree(toast.Child, t);
        }

        private static void ThemeTree(Control c, StudioTheme t)
        {
            switch (c)
            {
                case null:
                    return;
                case Button b:
                    if (b.Classes.Contains("icon")) ThemeIconButton(b, t);
                    else if (b.Classes.Contains("primary")) StylePrimaryButton(b, t);
                    else StyleSecondaryButton(b, t);
                    return;
                case VectorIconControl ic when Equals(ic.Tag, "severity"):
                    ic.IconColor = C(ic.IconName == "error" ? t.DangerCoral : (ic.IconName == "warning" ? t.WarnAmber : t.AccentPrimary));
                    ic.InvalidateVisual();
                    return;
                case SelectableTextBlock stb:
                    stb.Foreground = B(t.FgSecondary);
                    return;
                case TextBlock tb:
                    tb.Foreground = B(t.FgPrimary);
                    return;
                case Panel p:
                    foreach (var child in p.Children) ThemeTree(child, t);
                    return;
                case Decorator d:
                    ThemeTree(d.Child, t);
                    return;
            }
        }

        /// <summary>UPPERCASE 11 px semibold section label in FgSecondary (dialogs, views).</summary>
        public static TextBlock SectionLabel(string text, StudioTheme t)
        {
            return new TextBlock
            {
                Text = (text ?? "").ToUpperInvariant(),
                FontSize = UiTokens.FontHeader,
                FontWeight = FontWeight.SemiBold,
                Foreground = B((t ?? StudioTheme.GetById(StudioTheme.DefaultThemeId)).FgSecondary),
                Margin = new Thickness(0, 8, 0, 0)
            };
        }

        /// <summary>Soft drop shadow for floating widgets.</summary>
        public static BoxShadows WidgetShadow(StudioTheme t)
        {
            var c = C(UiTokens.WidgetShadow(t));
            return new BoxShadows(new BoxShadow { OffsetX = 0, OffsetY = 4, Blur = 16, Spread = 0, Color = c });
        }
    }
}
