using System;
using System.Collections.Generic;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using codeLess.Studio;

namespace codeLess.AvaloniaApp
{
    /// <summary>Settings dialog section for save actions, auto save modes and git sync options (VS Code names in tooltips).</summary>
    public static class ParitySettingsSection
    {
        public static readonly string[] AutoSaveModes = { IdePersistentSettings.AutoSaveOff, IdePersistentSettings.AutoSaveAfterDelay, IdePersistentSettings.AutoSaveOnFocusChange, IdePersistentSettings.AutoSaveOnWindowChange };

        public static Control Build(StudioSettings s, StudioTheme theme, out Action<StudioSettings> apply)
        {
            var fg = new SolidColorBrush(AvaloniaColorHelper.ToAvColor(theme.TextPrimary));
            var fg2 = new SolidColorBrush(AvaloniaColorHelper.ToAvColor(theme.TextSecondary));
            var stack = new StackPanel { Spacing = 6 };
            CheckBox Check(string text, bool value, string vscodeName)
            {
                var c = new CheckBox { Content = text, IsChecked = value, Foreground = fg };
                ToolTip.SetTip(c, vscodeName);
                return c;
            }

            stack.Children.Add(new TextBlock { Text = "Files", FontWeight = FontWeight.SemiBold, FontSize = 12, Foreground = fg });
            var modeCombo = new ComboBox { ItemsSource = AutoSaveModes, SelectedIndex = Math.Max(0, Array.IndexOf(AutoSaveModes, s.EffectiveAutoSaveMode)), MinWidth = 150 };
            ToolTip.SetTip(modeCombo, "files.autoSave");
            var delayBox = new TextBox { Text = s.EffectiveAutoSaveDelayMs.ToString(CultureInfo.InvariantCulture), Width = 80 };
            ToolTip.SetTip(delayBox, "files.autoSaveDelay (ms)");
            var autoRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
            autoRow.Children.Add(new TextBlock { Text = "Auto Save:", VerticalAlignment = VerticalAlignment.Center, Foreground = fg2 });
            autoRow.Children.Add(modeCombo);
            autoRow.Children.Add(new TextBlock { Text = "Delay (ms):", VerticalAlignment = VerticalAlignment.Center, Foreground = fg2 });
            autoRow.Children.Add(delayBox);
            stack.Children.Add(autoRow);

            var format = Check("Format on save (language server formatter)", s.FormatOnSave, "editor.formatOnSave");
            var trim = Check("Trim trailing whitespace on save", s.TrimTrailingWhitespaceOnSave, "files.trimTrailingWhitespace");
            var finalNl = Check("Insert final newline on save", s.InsertFinalNewlineOnSave, "files.insertFinalNewline");
            var trimFinal = Check("Trim final newlines on save", s.TrimFinalNewlinesOnSave, "files.trimFinalNewlines");
            var saveRow1 = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 18 };
            saveRow1.Children.Add(format);
            saveRow1.Children.Add(trim);
            var saveRow2 = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 18 };
            saveRow2.Children.Add(finalNl);
            saveRow2.Children.Add(trimFinal);
            stack.Children.Add(saveRow1);
            stack.Children.Add(saveRow2);

            stack.Children.Add(new TextBlock { Text = "Git", FontWeight = FontWeight.SemiBold, FontSize = 12, Foreground = fg, Margin = new Thickness(0, 4, 0, 0) });
            var autoFetch = Check("Auto fetch", s.GitAutoFetch, "git.autofetch");
            var period = new TextBox { Text = s.EffectiveAutoFetchPeriodSeconds.ToString(CultureInfo.InvariantCulture), Width = 70 };
            ToolTip.SetTip(period, "git.autofetchPeriod (seconds)");
            var rebase = Check("Rebase when syncing", s.GitRebaseWhenSync, "git.rebaseWhenSync");
            var gitRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
            gitRow.Children.Add(autoFetch);
            gitRow.Children.Add(new TextBlock { Text = "every (s):", VerticalAlignment = VerticalAlignment.Center, Foreground = fg2 });
            gitRow.Children.Add(period);
            gitRow.Children.Add(rebase);
            stack.Children.Add(gitRow);

            apply = target =>
            {
                int mi = modeCombo.SelectedIndex;
                target.SetAutoSaveMode(mi >= 0 && mi < AutoSaveModes.Length ? AutoSaveModes[mi] : IdePersistentSettings.AutoSaveOff);
                if (int.TryParse(delayBox.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int d) && d >= 100 && d <= 600000) target.AutoSaveDelayMs = d;
                target.FormatOnSave = format.IsChecked == true;
                target.TrimTrailingWhitespaceOnSave = trim.IsChecked == true;
                target.InsertFinalNewlineOnSave = finalNl.IsChecked == true;
                target.TrimFinalNewlinesOnSave = trimFinal.IsChecked == true;
                target.GitAutoFetch = autoFetch.IsChecked == true;
                if (int.TryParse(period.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int p) && p >= 30 && p <= 86400) target.GitAutoFetchPeriodSeconds = p;
                target.GitRebaseWhenSync = rebase.IsChecked == true;
            };
            return stack;
        }
    }
}
