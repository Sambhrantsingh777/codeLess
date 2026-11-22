using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using codeLess.Studio;

namespace codeLess.AvaloniaApp
{
    /// <summary>
    /// Workbench chrome polish (docs/UI_GUIDELINES.md): slim neutral status bar with icon items and transient,
    /// ellipsized messages; shared hover styles for status-bar and toolbar icon buttons.
    /// </summary>
    public partial class MainWindow
    {
        private DispatcherTimer statusMessageTimer;
        private static bool chromeStylesInstalled;

        /// <summary>Full (unclamped) text of the last status message.</summary>
        public string StatusMessageText { get; private set; } = "";

        private Button MakeStatusBarItem(object content, Action onClick, bool isRemoteBadge = false)
        {
            var b = new Button
            {
                Content = content is string s ? new LiteralTextBlock(s) { FontSize = UiTokens.FontSmall, VerticalAlignment = VerticalAlignment.Center } : content,
                FontSize = UiTokens.FontSmall,
                Padding = new Thickness(isRemoteBadge ? 9 : 6, 0),
                Height = UiTokens.StatusBarHeight,
                MinHeight = 0,
                VerticalAlignment = VerticalAlignment.Stretch,
                VerticalContentAlignment = VerticalAlignment.Center,
                CornerRadius = new CornerRadius(0),
                Background = Brushes.Transparent,
                Foreground = Brushes.White,
                BorderThickness = new Thickness(0),
                Focusable = false
            };
            b.Classes.Add(isRemoteBadge ? "statusBadge" : "statusItem");
            if (onClick != null) b.Click += (s2, e) => onClick();
            return b;
        }

        private void SetStatusBranch(string branch)
        {
            if (statusBranchBtn == null) return;
            bool noGit = string.IsNullOrEmpty(branch) || branch == "no-git";
            string text = noGit ? "no-git" : branch;
            if (statusBranchBtn.Content is IconLabel il) il.Text = text;
            else statusBranchBtn.Content = new IconLabel("branch", text, 14, UiTokens.FontSmall, "⎇ ");
            // Like VS Code, the branch item only appears inside a repository.
            statusBranchBtn.IsVisible = !noGit;
            ToolTip.SetTip(statusBranchBtn, noGit ? null : branch + " (Git branch) — open Source Control");
        }

        private void SetStatusProblems(int errors, int warnings)
        {
            if (statusProblemsBtn == null) return;
            if (!(statusProblemsBtn.Content is StatusProblemsContent pc))
            {
                pc = new StatusProblemsContent();
                statusProblemsBtn.Content = pc;
            }
            pc.Set(errors, warnings);
            ToolTip.SetTip(statusProblemsBtn, errors + " error(s), " + warnings + " warning(s) — open Problems");
        }

        /// <summary>
        /// Status messages are transient like VS Code's: single line, ellipsized to <see cref="UiTokens.StatusMessageMaxChars"/>
        /// (full text in the tooltip) and cleared after <see cref="UiTokens.StatusMessageTimeoutMs"/>, so they never take over the bar.
        /// </summary>
        private void ApplyStatusMessage(string msg)
        {
            StatusMessageText = msg ?? "";
            if (statusMessageLabel == null) return;
            string clamped = UiTokens.ClampStatusMessage(msg);
            statusMessageLabel.Text = clamped;
            ToolTip.SetTip(statusMessageLabel, clamped.Length < StatusMessageText.Trim().Length ? StatusMessageText : null);
            if (statusMessageTimer == null)
            {
                statusMessageTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(UiTokens.StatusMessageTimeoutMs) };
                statusMessageTimer.Tick += (s, e) =>
                {
                    statusMessageTimer.Stop();
                    if (statusMessageLabel != null) statusMessageLabel.Text = "";
                    ToolTip.SetTip(statusMessageLabel, null);
                };
            }
            statusMessageTimer.Stop();
            if (clamped.Length > 0) statusMessageTimer.Start();
        }

        private void ApplyStatusBarTheme(StudioTheme theme)
        {
            if (statusBarBorder == null || theme == null) return;
            statusBarBorder.BorderBrush = WorkbenchStyles.B(UiTokens.StatusBarBorder(theme));
            statusBarBorder.BorderThickness = new Thickness(0, UiTokens.DividerWidth, 0, 0);
            if (statusRemoteBtn != null)
            {
                statusRemoteBtn.Background = WorkbenchStyles.B(UiTokens.StatusBadgeBackground(theme));
                statusRemoteBtn.Foreground = WorkbenchStyles.B(UiTokens.OnAccent(theme));
            }
        }

        /// <summary>Hover/pressed visuals for status-bar items and flat icon buttons (installed once, colors via resources).</summary>
        private static void InstallChromeStyles(StudioTheme theme)
        {
            var app = Application.Current;
            if (app == null || theme == null) return;
            var r = app.Resources;
            r["CodelessStatusItemHover"] = WorkbenchStyles.B(UiTokens.StatusBarItemHover(theme));
            r["CodelessStatusBadgeHover"] = WorkbenchStyles.B(UiTokens.Blend(UiTokens.StatusBadgeBackground(theme), System.Drawing.Color.White, 0.12));
            r["CodelessToolbarHover"] = new SolidColorBrush(WorkbenchStyles.C(UiTokens.WithAlpha(theme.FgPrimary, theme.IsLight ? 20 : 26)));
            r["CodelessToolbarPressed"] = new SolidColorBrush(WorkbenchStyles.C(UiTokens.WithAlpha(theme.FgPrimary, theme.IsLight ? 34 : 40)));
            r["CodelessStatusForeground"] = WorkbenchStyles.B(UiTokens.StatusBarForeground(theme));
            r["CodelessFgPrimary"] = WorkbenchStyles.B(theme.FgPrimary);
            r["CodelessPrimaryHover"] = WorkbenchStyles.B(UiTokens.ButtonPrimaryHover(theme));
            r["CodelessOnAccent"] = WorkbenchStyles.B(UiTokens.OnAccent(theme));
            if (chromeStylesInstalled) return;
            chromeStylesInstalled = true;

            Style S(Func<Selector, Selector> sel, AvaloniaProperty p, object v)
            {
                var st = new Style(sel);
                st.Setters.Add(new Setter(p, v));
                return st;
            }
            app.Styles.Add(S(x => x.OfType<Button>().Class("statusItem").Class(":pointerover").Template().OfType<ContentPresenter>().Name("PART_ContentPresenter"),
                ContentPresenter.BackgroundProperty, new DynamicResourceExtension("CodelessStatusItemHover")));
            app.Styles.Add(S(x => x.OfType<Button>().Class("statusItem").Class(":pointerover").Template().OfType<ContentPresenter>().Name("PART_ContentPresenter"),
                ContentPresenter.ForegroundProperty, new DynamicResourceExtension("CodelessStatusForeground")));
            app.Styles.Add(S(x => x.OfType<Button>().Class("statusBadge").Class(":pointerover").Template().OfType<ContentPresenter>().Name("PART_ContentPresenter"),
                ContentPresenter.BackgroundProperty, new DynamicResourceExtension("CodelessStatusBadgeHover")));
            app.Styles.Add(S(x => x.OfType<Button>().Class("statusBadge").Class(":pointerover").Template().OfType<ContentPresenter>().Name("PART_ContentPresenter"),
                ContentPresenter.ForegroundProperty, new DynamicResourceExtension("CodelessOnAccent")));
            app.Styles.Add(S(x => x.OfType<Button>().Class("icon").Class(":pointerover").Template().OfType<ContentPresenter>().Name("PART_ContentPresenter"),
                ContentPresenter.BackgroundProperty, new DynamicResourceExtension("CodelessToolbarHover")));
            app.Styles.Add(S(x => x.OfType<Button>().Class("icon").Class(":pressed").Template().OfType<ContentPresenter>().Name("PART_ContentPresenter"),
                ContentPresenter.BackgroundProperty, new DynamicResourceExtension("CodelessToolbarPressed")));
            app.Styles.Add(S(x => x.OfType<Button>().Class("flat").Class(":pointerover").Template().OfType<ContentPresenter>().Name("PART_ContentPresenter"),
                ContentPresenter.BackgroundProperty, new DynamicResourceExtension("CodelessToolbarHover")));
            // Panel / view tabs: no hover fill, text brightens (VS Code panel titles).
            app.Styles.Add(S(x => x.OfType<Button>().Class("paneltab").Class(":pointerover").Template().OfType<ContentPresenter>().Name("PART_ContentPresenter"),
                ContentPresenter.BackgroundProperty, Brushes.Transparent));
            app.Styles.Add(S(x => x.OfType<Button>().Class("paneltab").Class(":pointerover").Template().OfType<ContentPresenter>().Name("PART_ContentPresenter"),
                ContentPresenter.ForegroundProperty, new DynamicResourceExtension("CodelessFgPrimary")));
            app.Styles.Add(S(x => x.OfType<Button>().Class("paneltab").Class(":pressed").Template().OfType<ContentPresenter>().Name("PART_ContentPresenter"),
                ContentPresenter.BackgroundProperty, Brushes.Transparent));
            // Primary (accent) buttons keep an accent hover instead of turning grey.
            foreach (var state in new[] { ":pointerover", ":pressed" })
            {
                string st = state;
                app.Styles.Add(S(x => x.OfType<Button>().Class("primary").Class(st).Template().OfType<ContentPresenter>().Name("PART_ContentPresenter"),
                    ContentPresenter.BackgroundProperty, new DynamicResourceExtension("CodelessPrimaryHover")));
                app.Styles.Add(S(x => x.OfType<Button>().Class("primary").Class(st).Template().OfType<ContentPresenter>().Name("PART_ContentPresenter"),
                    ContentPresenter.ForegroundProperty, new DynamicResourceExtension("CodelessOnAccent")));
            }
        }
    }

    /// <summary>Status-bar problems item: error glyph + count, warning glyph + count (ToString keeps "⊗ e  △ w").</summary>
    public class StatusProblemsContent : StackPanel
    {
        private readonly TextBlock errText = new TextBlock { FontSize = UiTokens.FontSmall, VerticalAlignment = VerticalAlignment.Center };
        private readonly TextBlock warnText = new TextBlock { FontSize = UiTokens.FontSmall, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 0, 0) };
        private int errors, warnings;

        public StatusProblemsContent()
        {
            Orientation = Orientation.Horizontal;
            Spacing = 3;
            VerticalAlignment = VerticalAlignment.Center;
            Children.Add(new VectorIconControl { IconName = "error", Width = 14, Height = 14, FollowForeground = true, StrokeWidth = 1.3f, VerticalAlignment = VerticalAlignment.Center });
            Children.Add(errText);
            Children.Add(new VectorIconControl { IconName = "warning", Width = 14, Height = 14, FollowForeground = true, StrokeWidth = 1.3f, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(5, 0, 0, 0) });
            Children.Add(warnText);
            Set(0, 0);
        }

        public void Set(int e, int w)
        {
            errors = e;
            warnings = w;
            errText.Text = e.ToString(System.Globalization.CultureInfo.InvariantCulture);
            warnText.Text = w.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        public override string ToString() => "⊗ " + errors + "  △ " + warnings;
    }
}
