using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using codeLess.Studio;

namespace codeLess.AvaloniaApp
{
    /// <summary>
    /// VS Code extensions on macOS / Linux (QA #14), same engine as Windows (VsixAndPluginHostEngine): installed VSIX
    /// packages load at startup (color themes join the theme picker, snippets work in the editor), the Extensions view
    /// installs a local .vsix or searches / installs from OpenVSX (through the network ledger, so Offline Mode applies)
    /// and uninstalls. Snippets expand with Tab on an exact prefix (and "Snippets: Insert Snippet").
    /// </summary>
    public partial class MainWindow
    {
        /// <summary>Tests: folder used instead of ~/.codeless/extensions.</summary>
        public static string ExtensionsRootOverride;

        private VsixAndPluginHostEngine vsixEngine;
        private TextBlock extensionsNoticeText;
        private Button extensionsInstallVsixBtn;
        private List<VsixExtensionPackage> extensionsSearchResults = new List<VsixExtensionPackage>();

        public VsixAndPluginHostEngine ExtensionHost => vsixEngine;
        public string ExtensionsNotice => extensionsNoticeText?.Text ?? "";

        /// <summary>Created before the persisted theme is resolved, so a VSIX theme can be restored at startup.</summary>
        private void InitExtensionHost()
        {
            try { vsixEngine = new VsixAndPluginHostEngine(StudioTheme.ExtensionThemes, ExtensionsRootOverride); }
            catch (Exception ex) { vsixEngine = null; Console.Error.WriteLine("Extension host unavailable: " + ex.Message); }
        }

        /// <summary>Snippets (built-in + extensions) that apply to the buffer's language.</summary>
        public List<CodeSnippetItem> SnippetsForBuffer(EditorTabBuffer buf) =>
            SnippetEngine.ForLanguage(vsixEngine?.AllLoadedSnippets ?? new List<CodeSnippetItem>(), buf?.Language);

        private int EffectiveTabSize(EditorTabBuffer b) => b?.CustomTabSize is int t && t > 0 ? t : Math.Max(1, settings.TabSize);
        private bool EffectiveInsertSpaces(EditorTabBuffer b) => b?.CustomInsertSpaces ?? settings.InsertSpaces;

        /// <summary>Editor key hook: Tab on an exact snippet prefix expands the snippet (first tab stop selected).</summary>
        private bool OnSnippetKey(AvaloniaEditorControl editor, KeyEventArgs e)
        {
            if (e.Key != Key.Tab || e.KeyModifiers != KeyModifiers.None || vsixEngine == null) return false;
            var buf = editor?.CurrentBuffer;
            if (buf == null || buf.IsLargeFileViewer || !string.IsNullOrEmpty(buf.GhostTextSuggestion) || buf.HasPendingDiff) return false;
            if (editor.Canvas.ExtraCursors.Count > 0) return false;
            if (!SnippetEngine.TryExpandPrefixAtCaret(buf, vsixEngine.AllLoadedSnippets, EffectiveTabSize(buf), EffectiveInsertSpaces(buf))) return false;
            editor.Canvas.CommitExternalEdit();
            editor.Canvas.EnsureCursorVisible();
            return true;
        }

        /// <summary>Inserts a snippet at the caret of the active editor (palette "Snippets: Insert Snippet").</summary>
        public bool InsertSnippet(CodeSnippetItem snippet)
        {
            var buf = PrimaryEditor?.CurrentBuffer;
            if (buf == null || snippet == null || buf.IsLargeFileViewer) return false;
            SnippetEngine.InsertAtCaret(buf, snippet, 0, EffectiveTabSize(buf), EffectiveInsertSpaces(buf));
            PrimaryEditor.Canvas.CommitExternalEdit();
            PrimaryEditor.Canvas.EnsureCursorVisible();
            PrimaryEditor.Canvas.Focus();
            return true;
        }

        public void OpenInsertSnippetPicker()
        {
            var buf = PrimaryEditor?.CurrentBuffer;
            var snippets = SnippetsForBuffer(buf);
            if (snippets.Count == 0) { SetStatusMessage("No snippets for " + (buf?.Language ?? "this file") + ". Install a VSIX snippet extension from the Extensions view."); return; }
            CommandPalette.Open(">", snippets.Select(s => new PaletteEntryItem
            {
                Category = "Snippet",
                Title = s.Prefix,
                stringSubtitleOrPath = s.Description ?? "",
                Execute = () => InsertSnippet(s)
            }).ToList());
        }

        private List<PaletteEntryItem> BuildExtensionPaletteCommands() => new List<PaletteEntryItem>
        {
            new PaletteEntryItem { Category = "Snippets", Title = "Insert Snippet", Execute = OpenInsertSnippetPicker },
            new PaletteEntryItem { Category = "Extensions", Title = "Install from VSIX...", Execute = () => _ = PickAndInstallVsixAsync() },
            new PaletteEntryItem { Category = "Extensions", Title = "Show Installed Extensions", Execute = () => SwitchSidebarView("extensions") }
        };

        // ------------------------------------------------------------------ install / uninstall / search

        public VsixExtensionPackage InstallVsixFromFile(string path)
        {
            if (vsixEngine == null) { SetExtensionsNotice("The extension host is unavailable."); return null; }
            var pkg = vsixEngine.InstallVsix(path, StudioTheme.ExtensionThemes, out string status);
            SetExtensionsNotice(status);
            SetStatusMessage(status);
            RefreshExtensionsView();
            return pkg;
        }

        public async Task PickAndInstallVsixAsync()
        {
            if (StorageProvider == null) return;
            var res = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = "Install from VSIX — codeLess",
                AllowMultiple = false,
                FileTypeFilter = new[] { new FilePickerFileType("VS Code extension (*.vsix)") { Patterns = new[] { "*.vsix" } } }
            });
            string p = res?.FirstOrDefault()?.TryGetLocalPath();
            if (!string.IsNullOrEmpty(p)) InstallVsixFromFile(p);
        }

        public async Task<int> SearchExtensionsAsync(string query)
        {
            if (vsixEngine == null || string.IsNullOrWhiteSpace(query)) { extensionsSearchResults = new List<VsixExtensionPackage>(); RefreshExtensionsView(); return 0; }
            SetExtensionsNotice("Searching OpenVSX for \"" + query.Trim() + "\"…");
            await vsixEngine.SearchOpenVsxRegistryAsync(query.Trim());
            extensionsSearchResults = vsixEngine.LastSearchResults?.ToList() ?? new List<VsixExtensionPackage>();
            SetExtensionsNotice(!string.IsNullOrEmpty(vsixEngine.LastNetworkNotice)
                ? vsixEngine.LastNetworkNotice
                : (extensionsSearchResults.Count == 0 ? "No extensions found." : ""));
            RefreshExtensionsView();
            return extensionsSearchResults.Count;
        }

        public async Task<bool> InstallFromMarketplaceAsync(VsixExtensionPackage ext)
        {
            if (vsixEngine == null || ext == null) return false;
            SetExtensionsNotice("Installing " + ext.DisplayName + "…");
            bool ok = await vsixEngine.DownloadAndInstallFromOpenVsxAsync(ext, StudioTheme.ExtensionThemes);
            SetExtensionsNotice(ok ? "Installed " + ext.DisplayName + "." : (vsixEngine.LastNetworkNotice ?? "Install failed."));
            RefreshExtensionsView();
            return ok;
        }

        public bool UninstallExtension(VsixExtensionPackage ext)
        {
            if (vsixEngine == null || ext == null) return false;
            bool activeThemeGone = ext.ContributedThemes.Any(t => string.Equals(t.Id, currentTheme?.Id, StringComparison.OrdinalIgnoreCase));
            bool ok = vsixEngine.Uninstall(ext, StudioTheme.ExtensionThemes, out string status);
            if (ok && activeThemeGone) ApplyTheme(StudioTheme.GetById(StudioTheme.DefaultThemeId), persistSetting: true);
            SetExtensionsNotice(status);
            SetStatusMessage(status);
            RefreshExtensionsView();
            return ok;
        }

        private void SetExtensionsNotice(string text)
        {
            if (extensionsNoticeText == null) return;
            extensionsNoticeText.Text = text ?? "";
            extensionsNoticeText.IsVisible = !string.IsNullOrEmpty(text);
        }

        // ------------------------------------------------------------------ view

        private Grid BuildExtensionsSidebarViewReal()
        {
            var grid = new Grid { RowDefinitions = new RowDefinitions("Auto,*"), Margin = new Thickness(8, 6), IsVisible = false };
            var top = new StackPanel { Spacing = 6 };
            extensionsSearchInput = new TextBox { Watermark = "Search Extensions in OpenVSX…", FontSize = UiTokens.FontUi, Name = "ExtensionsSearchInput" };
            extensionsSearchInput.KeyDown += (s, e) =>
            {
                if (e.Key == Key.Enter) { _ = SearchExtensionsAsync(extensionsSearchInput.Text); e.Handled = true; }
                else if (e.Key == Key.Escape) { extensionsSearchInput.Text = ""; _ = SearchExtensionsAsync(""); e.Handled = true; }
            };
            top.Children.Add(extensionsSearchInput);
            extensionsInstallVsixBtn = new Button { Content = "Install from VSIX…", FontSize = UiTokens.FontSmall, Padding = new Thickness(10, 3), HorizontalAlignment = HorizontalAlignment.Left, Name = "ExtensionsInstallVsixButton" };
            extensionsInstallVsixBtn.Click += (s, e) => _ = PickAndInstallVsixAsync();
            top.Children.Add(extensionsInstallVsixBtn);
            extensionsNoticeText = new TextBlock { FontSize = UiTokens.FontSmall, TextWrapping = TextWrapping.Wrap, IsVisible = false };
            top.Children.Add(extensionsNoticeText);
            Grid.SetRow(top, 0);
            grid.Children.Add(top);

            extensionsCardsStack = new StackPanel { Spacing = 6, Margin = new Thickness(0, 8, 0, 0) };
            var scroll = new ScrollViewer { Content = extensionsCardsStack };
            Grid.SetRow(scroll, 1);
            grid.Children.Add(scroll);
            RefreshExtensionsView();
            return grid;
        }

        /// <summary>Rebuilds the cards: search results (when searching), then installed extensions.</summary>
        public void RefreshExtensionsView()
        {
            if (extensionsCardsStack == null) return;
            var t = currentTheme ?? StudioTheme.GetById(StudioTheme.DefaultThemeId);
            extensionsCardsStack.Children.Clear();
            if (extensionsNoticeText != null) extensionsNoticeText.Foreground = WorkbenchStyles.B(t.FgSecondary);
            if (extensionsInstallVsixBtn != null) WorkbenchStyles.StyleSecondaryButton(extensionsInstallVsixBtn, t);
            if (vsixEngine == null)
            {
                extensionsCardsStack.Children.Add(new TextBlock { Text = "The extension host could not start.", Foreground = WorkbenchStyles.B(t.FgSecondary) });
                return;
            }
            if (extensionsSearchResults.Count > 0)
            {
                extensionsCardsStack.Children.Add(WorkbenchStyles.SectionLabel("Marketplace (OpenVSX)", t));
                foreach (var ext in extensionsSearchResults) extensionsCardsStack.Children.Add(BuildExtensionCard(ext, t));
            }
            extensionsCardsStack.Children.Add(WorkbenchStyles.SectionLabel("Installed", t));
            foreach (var ext in vsixEngine.Extensions.Where(x => x.IsInstalled).ToList())
                extensionsCardsStack.Children.Add(BuildExtensionCard(ext, t));
        }

        private Control BuildExtensionCard(VsixExtensionPackage ext, StudioTheme t)
        {
            var card = new StackPanel { Spacing = 2 };
            bool userInstalled = ext.IsInstalled && !string.IsNullOrEmpty(ext.InstallPath);
            string state = !ext.IsInstalled ? "" : (userInstalled ? "Installed" : "Built in");
            card.Children.Add(new TextBlock { Text = ext.DisplayName ?? ext.Id, FontWeight = FontWeight.SemiBold, FontSize = UiTokens.FontUi, Foreground = WorkbenchStyles.B(t.FgPrimary), TextTrimming = TextTrimming.CharacterEllipsis });
            card.Children.Add(new TextBlock { Text = (ext.Publisher ?? "") + "  ·  v" + (ext.Version ?? "?") + (state.Length > 0 ? "  ·  " + state : ""), FontSize = UiTokens.FontSmall, Foreground = WorkbenchStyles.B(t.FgSecondary) });
            if (!string.IsNullOrEmpty(ext.Description))
                card.Children.Add(new TextBlock { Text = ext.Description, FontSize = UiTokens.FontSmall, TextWrapping = TextWrapping.Wrap, MaxLines = 3, Foreground = WorkbenchStyles.B(t.FgSecondary) });
            var contrib = new List<string>();
            if (ext.ContributedThemes.Count > 0) contrib.Add(ext.ContributedThemes.Count + " theme(s)");
            if (ext.ContributedSnippets.Count > 0) contrib.Add(ext.ContributedSnippets.Count + " snippet(s)");
            if (contrib.Count > 0) card.Children.Add(new TextBlock { Text = string.Join(", ", contrib), FontSize = UiTokens.FontSmall, Foreground = WorkbenchStyles.B(t.FgMuted) });

            var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Margin = new Thickness(0, 4, 0, 0) };
            if (!ext.IsInstalled && !string.IsNullOrEmpty(ext.DownloadUrl))
            {
                var install = new Button { Content = "Install", FontSize = UiTokens.FontSmall, Padding = new Thickness(10, 2) };
                WorkbenchStyles.StylePrimaryButton(install, t);
                install.Click += (s, e) => _ = InstallFromMarketplaceAsync(ext);
                actions.Children.Add(install);
            }
            if (ext.IsInstalled && ext.ContributedThemes.Count > 0)
            {
                var setTheme = new Button { Content = "Set Color Theme", FontSize = UiTokens.FontSmall, Padding = new Thickness(10, 2) };
                WorkbenchStyles.StyleSecondaryButton(setTheme, t);
                setTheme.Click += (s, e) =>
                {
                    if (ext.ContributedThemes.Count == 1) ApplyTheme(ext.ContributedThemes[0], persistSetting: true);
                    else OpenThemePickerPalette();
                };
                actions.Children.Add(setTheme);
            }
            if (userInstalled)
            {
                var uninstall = new Button { Content = "Uninstall", FontSize = UiTokens.FontSmall, Padding = new Thickness(10, 2) };
                WorkbenchStyles.StyleSecondaryButton(uninstall, t);
                uninstall.Click += (s, e) => UninstallExtension(ext);
                actions.Children.Add(uninstall);
            }
            if (actions.Children.Count > 0) card.Children.Add(actions);
            return new Border
            {
                Child = card,
                CornerRadius = new CornerRadius(UiTokens.ToolbarRadius),
                BorderThickness = new Thickness(1),
                BorderBrush = WorkbenchStyles.B(t.BorderSubtle),
                Padding = new Thickness(8, 6),
                Tag = ext.Id
            };
        }
    }
}
