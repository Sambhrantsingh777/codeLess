using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using codeLess.Studio;

namespace codeLess.AvaloniaApp
{
    /// <summary>
    /// Settings dialog body (VS Code-like): a search box that filters settings, a clipped scrolling body of titled
    /// sections (every label wraps instead of being cut off) and a fixed footer with Save / Cancel. The modal host
    /// gives the view a bounded height, so nothing draws outside the dialog frame.
    /// </summary>
    public static class SettingsDialogView
    {
        public const string RootName = "SettingsView";
        public const string ScrollName = "SettingsScroll";
        public const string SearchName = "SettingsSearch";
        public const string NoResultsName = "SettingsNoResults";
        public const string SaveName = "SettingsSaveBtn";
        public const string CancelName = "SettingsCancelBtn";
        /// <summary>Check box rows are 24 px (Fluent's 32 px made the list look sparse); rows are 8 px apart.</summary>
        public const double RowMinHeight = 24;

        /// <summary>One filterable setting row.</summary>
        private sealed class Entry
        {
            public Control Row;
            public string Haystack;
            public Section Section;
        }

        private sealed class Section
        {
            public StackPanel Panel;
            public readonly List<Entry> Entries = new List<Entry>();
            public readonly List<FlowGrid> Grids = new List<FlowGrid>();
        }

        /// <summary>Two-column grid of check boxes that re-packs its visible items when the filter hides some.</summary>
        private sealed class FlowGrid : Grid
        {
            public readonly List<Control> Items = new List<Control>();

            public FlowGrid()
            {
                ColumnDefinitions = new ColumnDefinitions("*,*");
                Margin = new Thickness(0, 0, 0, -8); // the last row's gap is the section's own 8 px spacing
            }

            public void Add(Control c)
            {
                Items.Add(c);
                Children.Add(c);
                Relayout();
            }

            public void Relayout()
            {
                RowDefinitions.Clear();
                int n = 0;
                foreach (var c in Items)
                {
                    if (!c.IsVisible) continue;
                    int row = n / 2, col = n % 2;
                    if (col == 0) RowDefinitions.Add(new RowDefinition(GridLength.Auto));
                    Grid.SetRow(c, row);
                    Grid.SetColumn(c, col);
                    c.Margin = new Thickness(col == 0 ? 0 : 8, 0, col == 0 ? 8 : 0, 8);
                    n++;
                }
                IsVisible = n > 0;
            }
        }

        public static Control Build(StudioSettings settings, StudioTheme theme, Action<StudioSettings> onApplied, Action onClosed, Control workspaceTrustSection = null)
        {
            var fg = AvaloniaColorHelper.ToBrush(theme.TextPrimary);
            var fg2 = AvaloniaColorHelper.ToBrush(theme.TextSecondary);
            var sections = new List<Section>();
            var body = new StackPanel { Spacing = 0, Margin = new Thickness(0, 0, 14, 4) };
            Section current = null;

            void BeginSection(string title)
            {
                current = new Section { Panel = new StackPanel { Spacing = 8 } };
                var label = WorkbenchStyles.SectionLabel(title, theme);
                label.Margin = new Thickness(0, sections.Count == 0 ? 2 : 18, 0, 2);
                current.Panel.Children.Add(label);
                body.Children.Add(current.Panel);
                sections.Add(current);
            }

            Entry Register(Control row, string text, string tip)
            {
                var e = new Entry { Row = row, Section = current, Haystack = ((text ?? "") + " " + (tip ?? "")).ToLowerInvariant() };
                current.Entries.Add(e);
                return e;
            }

            void AddRow(Control row, string text, string tip)
            {
                current.Panel.Children.Add(row);
                Register(row, text, tip);
            }

            TextBlock Wrapping(string text) => new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, FontSize = UiTokens.FontUi, VerticalAlignment = VerticalAlignment.Center };

            CheckBox Check(string text, bool value, string tip)
            {
                var c = new CheckBox { Content = Wrapping(text), IsChecked = value, Foreground = fg, HorizontalAlignment = HorizontalAlignment.Stretch, VerticalContentAlignment = VerticalAlignment.Center, MinHeight = RowMinHeight };
                if (!string.IsNullOrEmpty(tip)) ToolTip.SetTip(c, tip);
                return c;
            }

            CheckBox AddCheck(string text, bool value, string tip)
            {
                var c = Check(text, value, tip);
                AddRow(c, text, tip);
                return c;
            }

            FlowGrid AddGrid()
            {
                var g = new FlowGrid();
                current.Panel.Children.Add(g);
                current.Grids.Add(g);
                return g;
            }

            void AddToGrid(FlowGrid g, CheckBox c, string text)
            {
                g.Add(c);
                Register(c, text, ToolTip.GetTip(c) as string);
            }

            /// <summary>Label (wrapping, fixed column) + control; stacks the control under the label when narrow.</summary>
            void AddLabeled(string label, Control control, string tip, string extraKeywords = null)
            {
                var g = new Grid { ColumnDefinitions = new ColumnDefinitions("170,*") };
                var l = Wrapping(label);
                l.Foreground = fg2;
                l.Margin = new Thickness(0, 0, 12, 0);
                g.Children.Add(l);
                Grid.SetColumn(control, 1);
                control.VerticalAlignment = VerticalAlignment.Center;
                g.Children.Add(control);
                if (!string.IsNullOrEmpty(tip)) { ToolTip.SetTip(control, tip); ToolTip.SetTip(l, tip); }
                AddRow(g, label + " " + (extraKeywords ?? ""), tip);
            }

            // ---------------------------------------------------------------- Appearance
            var themes = StudioTheme.GetAllBuiltInThemes();
            var themeCombo = new ComboBox
            {
                ItemsSource = themes.Select(t => t.Name).ToList(),
                SelectedIndex = Math.Max(0, themes.FindIndex(t => string.Equals(t.Id, settings.ThemeId, StringComparison.OrdinalIgnoreCase))),
                HorizontalAlignment = HorizontalAlignment.Stretch
            };
            var minimapChk = Check("Show editor minimap", settings.ShowMinimap, "editor.minimap.enabled");
            BeginSection("Appearance");
            AddLabeled("Color Theme", themeCombo, "workbench.colorTheme", string.Join(" ", themes.Select(t => t.Name)));
            AddRow(minimapChk, "Show editor minimap", "editor.minimap.enabled");

            // ---------------------------------------------------------------- Editor
            var fontBox = new TextBox { Text = settings.FontSize.ToString("0.#", CultureInfo.InvariantCulture), Width = 90, HorizontalAlignment = HorizontalAlignment.Left };
            var tabBox = new TextBox { Text = settings.TabSize.ToString(CultureInfo.InvariantCulture), Width = 90, HorizontalAlignment = HorizontalAlignment.Left };
            BeginSection("Editor");
            AddLabeled("Font Size", fontBox, "editor.fontSize");
            AddLabeled("Tab Size", tabBox, "editor.tabSize");
            var wrapChk = AddCheck("Word wrap (Alt+Z)", settings.WordWrap, "editor.wordWrap");
            var insertSpacesChk = AddCheck("Insert spaces when pressing Tab", settings.InsertSpaces, "editor.insertSpaces");
            var detectIndentChk = AddCheck("Detect indentation from the file's content and .editorconfig", settings.DetectIndentation, "editor.detectIndentation");
            var niceties = EditorNicetiesSettingsPanel.Build(settings, theme, AvaloniaColorHelper.ToAvColor(theme.TextPrimary));
            var smoothness = EditorSmoothnessSettingsPanel.Build(settings, AvaloniaColorHelper.ToAvColor(theme.TextPrimary));
            var nicetyGrid = AddGrid();
            foreach (var box in niceties.Boxes.Concat(smoothness.Boxes))
            {
                string text = box.Content as string ?? "";
                box.Content = Wrapping(text);
                box.HorizontalAlignment = HorizontalAlignment.Stretch;
                box.MinHeight = RowMinHeight;
                AddToGrid(nicetyGrid, box, text);
            }
            AddLabeled("Cursor Blinking", smoothness.BlinkCombo, "editor.cursorBlinking", "caret cursor blink smooth phase expand solid");
            AddLabeled("Mouse Wheel Scroll Sensitivity", smoothness.SensitivityBox, "editor.mouseWheelScrollSensitivity", "scroll wheel trackpad speed");
            AddLabeled("Fast Scroll Sensitivity (Alt)", smoothness.FastSensitivityBox, "editor.fastScrollSensitivity", "scroll wheel option alt speed");

            // ---------------------------------------------------------------- Files
            BeginSection("Files");
            var parity = ParitySettingsSection.CreateControls(settings);
            var autoSaveRow = new WrapPanel { Orientation = Orientation.Horizontal };
            parity.ModeCombo.Margin = new Thickness(0, 0, 12, 4);
            autoSaveRow.Children.Add(parity.ModeCombo);
            var delayLabel = new TextBlock { Text = "Delay (ms)", Foreground = fg2, FontSize = UiTokens.FontUi, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 4) };
            autoSaveRow.Children.Add(delayLabel);
            parity.DelayBox.Margin = new Thickness(0, 0, 0, 4);
            autoSaveRow.Children.Add(parity.DelayBox);
            AddLabeled("Auto Save", autoSaveRow, "files.autoSave / files.autoSaveDelay", "delay");
            var saveGrid = AddGrid();
            foreach (var pair in parity.SaveChecks) AddToGrid(saveGrid, Restyle(pair.Item1, fg), pair.Item2);
            var excludedBox = new TextBox { Text = settings.ExcludedFolders ?? "", TextWrapping = TextWrapping.Wrap, HorizontalAlignment = HorizontalAlignment.Stretch };
            AddLabeled("Excluded folders (comma-separated)", excludedBox, "files.exclude", "exclude workspace");

            // ---------------------------------------------------------------- Source control
            BeginSection("Source Control");
            var fetchRow = new WrapPanel { Orientation = Orientation.Horizontal };
            Restyle(parity.AutoFetch, fg).Margin = new Thickness(0, 0, 12, 4);
            fetchRow.Children.Add(parity.AutoFetch);
            fetchRow.Children.Add(new TextBlock { Text = "every (seconds)", Foreground = fg2, FontSize = UiTokens.FontUi, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 4) });
            parity.Period.Margin = new Thickness(0, 0, 0, 4);
            fetchRow.Children.Add(parity.Period);
            AddRow(fetchRow, "Auto fetch every seconds git", "git.autofetch git.autofetchPeriod");
            AddRow(Restyle(parity.Rebase, fg), "Rebase when syncing git", "git.rebaseWhenSync");

            // ---------------------------------------------------------------- Terminal, network
            BeginSection("Terminal");
            var venvChk = AddCheck("Activate the Python environment in new terminals", settings.ActivatePythonEnvironmentInTerminal, "python.terminal.activateEnvironment");
            var termFeel = TerminalFeelSettingsPanel.Build(settings, AvaloniaColorHelper.ToAvColor(theme.TextPrimary));
            var termGrid = AddGrid();
            AddToGrid(termGrid, termFeel.BlinkBox, "Terminal cursor blinking");
            AddToGrid(termGrid, termFeel.SmoothBox, "Terminal smooth scrolling");
            AddLabeled("Cursor Style", termFeel.StyleCombo, "terminal.integrated.cursorStyle", "terminal cursor block line bar underline");
            AddLabeled("Cursor Style (Unfocused)", termFeel.InactiveCombo, "terminal.integrated.cursorStyleInactive", "terminal cursor outline inactive");
            AddLabeled("Font Size", termFeel.FontSizeBox, "terminal.integrated.fontSize", "terminal font size");
            AddLabeled("Line Height", termFeel.LineHeightBox, "terminal.integrated.lineHeight", "terminal line height spacing");
            AddLabeled("Font Family", termFeel.FontFamilyBox, "terminal.integrated.fontFamily", "terminal font family");
            AddLabeled("Minimum Contrast Ratio", termFeel.ContrastBox, "terminal.integrated.minimumContrastRatio", "terminal contrast colors");
            BeginSection("Language Servers");
            var lspAutoInstallChk = AddCheck("Install missing language servers automatically (into codeLess's own folder; never global, never sudo)", settings.AutoInstallLanguageServers, "codeless.languageServers.autoInstall");
            BeginSection("Network & Updates");
            var offlineChk = AddCheck("Offline mode: block all non-local network requests (local Ollama / LM Studio keep working)", NetworkLedger.OfflineMode, "codeless.offlineMode");
            var updateModeCombo = new ComboBox
            {
                ItemsSource = UpdateModes.All.Select(UpdateModes.Label).ToList(),
                SelectedIndex = Math.Max(0, Array.IndexOf(UpdateModes.All, settings.GetEffectiveUpdateMode())),
                HorizontalAlignment = HorizontalAlignment.Left,
                MinWidth = 300
            };
            AddLabeled("Update mode", updateModeCombo, "update.mode", "check for updates automatically download restart none manual start default");

            // ---------------------------------------------------------------- Workspace Trust (changes apply immediately)
            if (workspaceTrustSection != null)
            {
                BeginSection("Workspace Trust");
                AddRow(workspaceTrustSection, "Workspace trust trusted folders restricted mode security", "security.workspace.trust");
            }

            // ---------------------------------------------------------------- AI
            var providers = LlmHarnessAndAgentEngine.GetAllProviderTemplates();
            string activeNormId = StudioSettings.NormalizeProviderId(settings.ActiveProvider);
            int initProvIdx = providers.FindIndex(p =>
                string.Equals(p.Name, settings.ActiveProvider, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(StudioSettings.NormalizeProviderId(p.Name), activeNormId, StringComparison.OrdinalIgnoreCase));
            var providerCombo = new ComboBox { ItemsSource = providers.Select(p => p.Name).ToList(), SelectedIndex = Math.Max(0, initProvIdx), HorizontalAlignment = HorizontalAlignment.Stretch };
            var modelBox = new TextBox { Text = settings.ActiveModel ?? "", HorizontalAlignment = HorizontalAlignment.Stretch };
            var apiKeyBox = new TextBox { Text = settings.GetActiveApiKey() ?? "", PasswordChar = '•', Watermark = "sk-... (stored per provider)", HorizontalAlignment = HorizontalAlignment.Stretch };
            providerCombo.SelectionChanged += (s, e) =>
            {
                if (providerCombo.SelectedIndex >= 0 && providerCombo.SelectedIndex < providers.Count)
                {
                    var selectedProv = providers[providerCombo.SelectedIndex];
                    apiKeyBox.Text = settings.GetApiKeyForProvider(selectedProv.Name) ?? "";
                    if (!string.IsNullOrWhiteSpace(selectedProv.DefaultModel)) modelBox.Text = selectedProv.DefaultModel;
                }
            };
            BeginSection("AI Provider & BYOK");
            AddLabeled("Provider", providerCombo, "codeless.ai.provider", "ai byok llm " + string.Join(" ", providers.Select(p => p.Name)));
            AddLabeled("Model", modelBox, "codeless.ai.model", "ai byok llm");
            AddLabeled("API key", apiKeyBox, "Stored per provider in your settings file", "ai byok llm secret token");

            // ---------------------------------------------------------------- search + chrome
            var noResults = new TextBlock
            {
                Name = NoResultsName,
                Text = "No settings found.",
                FontSize = UiTokens.FontUi,
                Foreground = fg2,
                Margin = new Thickness(0, 12, 0, 0),
                IsVisible = false
            };
            body.Children.Add(noResults);

            var scroll = new ScrollViewer
            {
                Name = ScrollName,
                Content = body,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto
            };
            var search = new TextBox { Name = SearchName, Watermark = "Search settings", FontSize = UiTokens.FontUi, Margin = new Thickness(0, 0, 0, 0) };
            var countText = new TextBlock { FontSize = UiTokens.FontSmall, Foreground = fg2, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 0, 0) };
            var searchRow = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), Margin = new Thickness(0, 0, 0, 10) };
            searchRow.Children.Add(search);
            Grid.SetColumn(countText, 1);
            searchRow.Children.Add(countText);

            void ApplyFilter()
            {
                string q = (search.Text ?? "").Trim().ToLowerInvariant();
                string[] terms = q.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                int shown = 0, total = 0;
                foreach (var sec in sections)
                {
                    int secShown = 0;
                    foreach (var e in sec.Entries)
                    {
                        total++;
                        bool match = terms.All(t => e.Haystack.Contains(t));
                        e.Row.IsVisible = match;
                        if (match) { secShown++; shown++; }
                    }
                    foreach (var g in sec.Grids) g.Relayout();
                    sec.Panel.IsVisible = secShown > 0;
                }
                noResults.IsVisible = shown == 0;
                countText.Text = terms.Length == 0 ? "" : (shown == 1 ? "1 setting found" : shown.ToString(CultureInfo.InvariantCulture) + " settings found");
                scroll.Offset = new Vector(0, 0);
            }
            search.TextChanged += (s, e) => ApplyFilter();

            var saveBtn = new Button { Name = SaveName, Content = "Save & Apply", Padding = new Thickness(14, 4), MinHeight = UiTokens.ControlHeight, VerticalContentAlignment = VerticalAlignment.Center };
            WorkbenchStyles.StylePrimaryButton(saveBtn, theme);
            var closeBtn = new Button { Name = CancelName, Content = "Cancel", Padding = new Thickness(14, 4), MinHeight = UiTokens.ControlHeight, VerticalContentAlignment = VerticalAlignment.Center };
            WorkbenchStyles.StyleSecondaryButton(closeBtn, theme);
            var footer = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 8 };
            footer.Children.Add(saveBtn);
            footer.Children.Add(closeBtn);
            var footerBorder = new Border
            {
                Child = footer,
                BorderThickness = new Thickness(0, 1, 0, 0),
                BorderBrush = AvaloniaColorHelper.ToBrush(theme.BorderSubtle),
                Padding = new Thickness(0, 12, 0, 0),
                Margin = new Thickness(0, 8, 0, 0)
            };

            saveBtn.Click += (s, e) =>
            {
                if (themeCombo.SelectedIndex >= 0 && themeCombo.SelectedIndex < themes.Count)
                    settings.ThemeId = themes[themeCombo.SelectedIndex].Id;
                if (float.TryParse(fontBox.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out float fs) && fs >= 8 && fs <= 32)
                    settings.FontSize = fs;
                if (int.TryParse(tabBox.Text, out int ts) && ts >= 1 && ts <= 16)
                    settings.TabSize = ts;
                settings.WordWrap = wrapChk.IsChecked == true;
                settings.InsertSpaces = insertSpacesChk.IsChecked == true;
                settings.DetectIndentation = detectIndentChk.IsChecked == true;
                settings.ShowMinimap = minimapChk.IsChecked == true;
                niceties.Apply(settings);
                smoothness.Apply(settings);
                settings.ActivatePythonEnvironmentInTerminal = venvChk.IsChecked == true;
                termFeel.Apply(settings);
                settings.AutoInstallLanguageServers = lspAutoInstallChk.IsChecked == true;
                settings.OfflineMode = offlineChk.IsChecked == true;
                if (updateModeCombo.SelectedIndex >= 0 && updateModeCombo.SelectedIndex < UpdateModes.All.Length)
                    settings.SetUpdateMode(UpdateModes.All[updateModeCombo.SelectedIndex]);
                NetworkLedger.SetOfflineMode(settings.OfflineMode);
                settings.ExcludedFolders = (excludedBox.Text ?? "").Trim();
                if (providerCombo.SelectedIndex >= 0 && providerCombo.SelectedIndex < providers.Count)
                    settings.ActiveProvider = StudioSettings.NormalizeProviderId(providers[providerCombo.SelectedIndex].Name);
                if (!string.IsNullOrWhiteSpace(modelBox.Text))
                    settings.ActiveModel = modelBox.Text.Trim();
                settings.SetApiKeyForProvider(settings.ActiveProvider, (apiKeyBox.Text ?? "").Trim());
                parity.Apply(settings);
                onApplied?.Invoke(settings);
            };
            closeBtn.Click += (s, e) => onClosed?.Invoke();

            var root = new Grid { Name = RootName, RowDefinitions = new RowDefinitions("Auto,*,Auto") };
            root.Children.Add(searchRow);
            Grid.SetRow(scroll, 1);
            root.Children.Add(scroll);
            Grid.SetRow(footerBorder, 2);
            root.Children.Add(footerBorder);
            AvaloniaModalDialogHost.SetFillsDialog(root, true);
            return root;
        }

        private static CheckBox Restyle(CheckBox c, IBrush fg)
        {
            if (c.Content is string text)
                c.Content = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, FontSize = UiTokens.FontUi, VerticalAlignment = VerticalAlignment.Center };
            c.Foreground = fg;
            c.HorizontalAlignment = HorizontalAlignment.Stretch;
            c.VerticalContentAlignment = VerticalAlignment.Center;
            c.MinHeight = RowMinHeight;
            return c;
        }
    }
}
