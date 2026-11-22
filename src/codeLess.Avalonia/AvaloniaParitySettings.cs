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

        /// <summary>Controls of the Files / Source Control settings (laid out by <see cref="SettingsDialogView"/>).</summary>
        public sealed class Controls
        {
            public ComboBox ModeCombo;
            public TextBox DelayBox;
            /// <summary>Save-action check boxes with their labels.</summary>
            public List<Tuple<CheckBox, string>> SaveChecks = new List<Tuple<CheckBox, string>>();
            public CheckBox AutoFetch;
            public TextBox Period;
            public CheckBox Rebase;
            public Action<StudioSettings> Apply;
        }

        public static Controls CreateControls(StudioSettings s)
        {
            var r = new Controls();
            CheckBox Check(string text, bool value, string vscodeName)
            {
                var c = new CheckBox { Content = text, IsChecked = value };
                ToolTip.SetTip(c, vscodeName);
                return c;
            }
            r.ModeCombo = new ComboBox { ItemsSource = AutoSaveModes, SelectedIndex = Math.Max(0, Array.IndexOf(AutoSaveModes, s.EffectiveAutoSaveMode)), MinWidth = 150 };
            ToolTip.SetTip(r.ModeCombo, "files.autoSave");
            r.DelayBox = new TextBox { Text = s.EffectiveAutoSaveDelayMs.ToString(CultureInfo.InvariantCulture), Width = 80 };
            ToolTip.SetTip(r.DelayBox, "files.autoSaveDelay (ms)");
            var format = Check("Format on save (language server formatter)", s.FormatOnSave, "editor.formatOnSave");
            var trim = Check("Trim trailing whitespace on save", s.TrimTrailingWhitespaceOnSave, "files.trimTrailingWhitespace");
            var finalNl = Check("Insert final newline on save", s.InsertFinalNewlineOnSave, "files.insertFinalNewline");
            var trimFinal = Check("Trim final newlines on save", s.TrimFinalNewlinesOnSave, "files.trimFinalNewlines");
            foreach (var c in new[] { format, trim, finalNl, trimFinal }) r.SaveChecks.Add(Tuple.Create(c, (string)c.Content));
            r.AutoFetch = Check("Auto fetch", s.GitAutoFetch, "git.autofetch");
            r.Period = new TextBox { Text = s.EffectiveAutoFetchPeriodSeconds.ToString(CultureInfo.InvariantCulture), Width = 70 };
            ToolTip.SetTip(r.Period, "git.autofetchPeriod (seconds)");
            r.Rebase = Check("Rebase when syncing", s.GitRebaseWhenSync, "git.rebaseWhenSync");
            r.Apply = target =>
            {
                int mi = r.ModeCombo.SelectedIndex;
                target.SetAutoSaveMode(mi >= 0 && mi < AutoSaveModes.Length ? AutoSaveModes[mi] : IdePersistentSettings.AutoSaveOff);
                if (int.TryParse(r.DelayBox.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int d) && d >= 100 && d <= 600000) target.AutoSaveDelayMs = d;
                target.FormatOnSave = format.IsChecked == true;
                target.TrimTrailingWhitespaceOnSave = trim.IsChecked == true;
                target.InsertFinalNewlineOnSave = finalNl.IsChecked == true;
                target.TrimFinalNewlinesOnSave = trimFinal.IsChecked == true;
                target.GitAutoFetch = r.AutoFetch.IsChecked == true;
                if (int.TryParse(r.Period.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int p) && p >= 30 && p <= 86400) target.GitAutoFetchPeriodSeconds = p;
                target.GitRebaseWhenSync = r.Rebase.IsChecked == true;
            };
            return r;
        }
    }
}
