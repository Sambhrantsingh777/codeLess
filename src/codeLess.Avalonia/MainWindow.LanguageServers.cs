using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using codeLess.Studio;

namespace codeLess.AvaloniaApp
{
    /// <summary>
    /// Language Server Protocol integration for the Avalonia workbench (macOS / Linux): document sync, diagnostics,
    /// completion, hover, signature help, go to definition / references, rename, formatting, code actions, outline,
    /// status bar item, install hints, log and configuration dialogs. The protocol work lives in codeLess.Core
    /// (LanguageServerManager), shared with the WinForms UI.
    /// </summary>
    public partial class MainWindow
    {
        /// <summary>Tests force servers on; by default they only run in the live desktop app (never in headless runs).</summary>
        public static bool? LanguageServersEnabledOverride;
        /// <summary>Tests point every language at the fake server; null = the user's settings.</summary>
        public static Func<LanguageServerUserSettings> LanguageServerSettingsOverride;

        private LanguageServerManager lsp;
        private UiPost lspUi;
        private Button statusLspBtn;
        private Border lspToast;
        private TextBlock lspToastText;
        private readonly Dictionary<string, EditorTabBuffer> lspOpenDocs = new Dictionary<string, EditorTabBuffer>(StringComparer.Ordinal);
        private readonly List<LspEditorBinding> lspBindings = new List<LspEditorBinding>();

        /// <summary>The language server manager (null when language servers are not enabled for this window).</summary>
        public LanguageServerManager LanguageServers => lsp;
        public LspEditorBinding PrimaryLspBinding => lspBindings.Count > 0 ? lspBindings[0] : null;

        private void InitLanguageServers()
        {
            bool enabled = LanguageServersEnabledOverride ?? AvaloniaTerminalEmulatorControl.IsDesktopRuntime;
            if (!enabled)
            {
                // no language servers: the editors still get word-based suggestions + snippets (QA #31)
                lspBindings.Add(new LspEditorBinding(this, PrimaryEditor));
                lspBindings.Add(new LspEditorBinding(this, SplitEditor));
                ApplyLanguageServerTheme(currentTheme);
                return;
            }
            lspUi = UiPost.CaptureCurrent();
            lsp = new LanguageServerManager(a => lspUi.Post(a));
            lsp.ClientVersion = typeof(MainWindow).Assembly.GetName().Version?.ToString(3) ?? "3.2";
            lsp.UserSettingsProvider = LanguageServerSettingsOverride ?? (() => settings.GetLanguageServerUserSettings());
            lsp.IsHintSuppressed = lang => settings.IsLanguageServerHintDismissed(lang);
            lsp.ApplyWorkspaceEditHandler = edit => ApplyLspWorkspaceEdit(edit, "Applied", LspEditGuard.ForServer(lsp)).Success;
            lsp.DiagnosticsChanged += OnLspDiagnosticsChanged;
            lsp.StatusChanged += UpdateLspStatusItem;
            lsp.ServerMissing += ShowLanguageServerHint;
            lsp.ServerMessage += (sev, msg) => SetStatusMessage(msg);
            if (!string.IsNullOrEmpty(workspaceRoot)) lsp.SetWorkspaceRoot(workspaceRoot);
            lspBindings.Add(new LspEditorBinding(this, PrimaryEditor));
            lspBindings.Add(new LspEditorBinding(this, SplitEditor));
            ApplyLanguageServerTheme(currentTheme);
        }

        private void ApplyLanguageServerTheme(StudioTheme theme)
        {
            if (theme == null) return;
            if (statusLspBtn != null) statusLspBtn.Foreground = new SolidColorBrush(AvaloniaColorHelper.ToAvColor(UiTokens.StatusBarForeground(theme)));
            foreach (var b in lspBindings) b.ApplyTheme(theme);
            if (lspToast != null) WorkbenchStyles.ThemeNotification(lspToast, theme);
        }

        private void ShutdownLanguageServers()
        {
            var m = lsp;
            lsp = null;
            foreach (var b in lspBindings) b.Detach();
            lspBindings.Clear();
            lspOpenDocs.Clear();
            if (m == null) return;
            try { m.Dispose(); } catch { }
        }

        private static string LspKey(string path) => LspText.PathKey(path ?? "");

        // ------------------------------------------------------------------ document lifecycle hooks

        private static bool IsLspEligible(EditorTabBuffer tab) =>
            tab != null && !string.IsNullOrEmpty(tab.FilePath) && File.Exists(tab.FilePath) && tab.Content != null && tab.Content.Length <= 4 * 1024 * 1024;

        /// <summary>Hook: ActivateTab. Opens the document on its server and closes documents whose tab is gone.</summary>
        private void LspOnTabActivated(EditorTabBuffer tab)
        {
            foreach (var b in lspBindings) b.HidePopupsIfEditorChanged(); // a suggest list of the previous file must not edit this one
            if (lsp == null) return;
            lsp.SetWorkspaceRoot(string.IsNullOrEmpty(workspaceRoot) ? null : workspaceRoot);
            LspSyncOpenDocuments();
            if (IsLspEligible(tab) && !lspOpenDocs.ContainsKey(LspKey(tab.FilePath)))
            {
                lspOpenDocs[LspKey(tab.FilePath)] = tab;
                var captured = tab;
                lsp.OpenDocument(tab.FilePath, () => LspEditing.BufferText(captured));
            }
            UpdateLspStatusItem();
        }

        private void LspSyncOpenDocuments()
        {
            if (lsp == null) return;
            foreach (var kv in lspOpenDocs.ToList())
            {
                if (openTabs.Contains(kv.Value) && LspKey(kv.Value.FilePath) == kv.Key) continue;
                lspOpenDocs.Remove(kv.Key);
                lsp.CloseDocument(kv.Key);
            }
        }

        private void LspOnBufferModified(EditorTabBuffer buf)
        {
            if (buf == null) return;
            if (lsp != null && !string.IsNullOrEmpty(buf.FilePath)) lsp.ChangeDocument(buf.FilePath);
            foreach (var b in lspBindings) b.OnBufferModified(buf); // also re-filters word-based suggestions (no server)
        }

        private void LspOnSaved(EditorTabBuffer tab)
        {
            if (lsp == null || tab == null) return;
            if (!lspOpenDocs.ContainsKey(LspKey(tab.FilePath))) { LspOnTabActivated(activeTab); return; } // e.g. untitled saved for the first time
            lsp.SaveDocument(tab.FilePath);
        }

        private void LspOnTabClosed(EditorTabBuffer tab)
        {
            foreach (var b in lspBindings) b.HideAll();
            if (lsp == null) return;
            LspSyncOpenDocuments();
        }

        private void LspOnWorkspaceChanged()
        {
            if (lsp == null) return;
            lsp.SetWorkspaceRoot(string.IsNullOrEmpty(workspaceRoot) ? null : workspaceRoot);
        }

        private void OnLspDiagnosticsChanged(string path)
        {
            var tab = FindOpenTabByPath(path);
            if (tab == null) return;
            LspDiagnosticsStore.MergeInto(tab);
            if (ReferenceEquals(tab, activeTab)) RefreshOutlineAndProblems(tab);
            if (ReferenceEquals(PrimaryEditor.CurrentBuffer, tab)) PrimaryEditor.Canvas.InvalidateVisual();
            if (ReferenceEquals(SplitEditor.CurrentBuffer, tab)) SplitEditor.Canvas.InvalidateVisual();
        }

        internal EditorTabBuffer FindOpenTabByPath(string path)
        {
            if (string.IsNullOrEmpty(path)) return null;
            string key = LspKey(path);
            return openTabs.FirstOrDefault(t => !string.IsNullOrEmpty(t.FilePath) && LspKey(t.FilePath) == key);
        }

        public string LspPathFor(EditorTabBuffer buf) =>
            lsp != null && buf != null && !string.IsNullOrEmpty(buf.FilePath) && lsp.IsDocumentOpen(buf.FilePath) ? buf.FilePath : null;

        // ------------------------------------------------------------------ status bar

        private void UpdateLspStatusItem()
        {
            if (statusLspBtn == null) return;
            var st = lsp != null && activeTab != null && !string.IsNullOrEmpty(activeTab.FilePath) ? lsp.GetStatus(activeTab.FilePath) : null;
            if (st == null || string.IsNullOrEmpty(st.StatusBarText))
            {
                statusLspBtn.IsVisible = false;
                return;
            }
            statusLspBtn.IsVisible = true;
            statusLspBtn.Content = new LiteralTextBlock(st.StatusBarText) { FontSize = UiTokens.FontSmall };
            ToolTip.SetTip(statusLspBtn, st.Tooltip);
        }

        /// <summary>Status bar text of the language server item ("" when hidden). Test hook.</summary>
        public string LanguageServerStatusText => statusLspBtn != null && statusLspBtn.IsVisible ? (statusLspBtn.Content as TextBlock)?.Text ?? "" : "";

        private void OpenLanguageServerStatusMenu()
        {
            var st = lsp != null && activeTab != null ? lsp.GetStatus(activeTab.FilePath) : null;
            var items = new List<object>
            {
                MakeCtxItem("Show Language Server Log", ShowLanguageServerLog),
                MakeCtxItem("Restart Language Server", RestartLanguageServerForActiveFile),
                MakeCtxItem("Configure Language Servers...", ShowLanguageServerSettingsModal)
            };
            if (st != null && st.State == LanguageServerState.NotInstalled && !string.IsNullOrEmpty(st.InstallCommand))
            {
                string cmd = st.InstallCommand;
                items.Insert(0, new Separator());
                items.Insert(0, MakeCtxItem("Install in Terminal: " + cmd, () => TypeInstallCommandInTerminal(cmd)));
            }
            var menu = new ContextMenu { ItemsSource = items };
            menu.Open(statusLspBtn);
        }

        public void RestartLanguageServerForActiveFile()
        {
            if (lsp == null) { SetStatusMessage("Language servers are not available in this window."); return; }
            if (activeTab == null || LspPathFor(activeTab) == null) { SetStatusMessage("No language server for the active file."); return; }
            lsp.RestartServer(activeTab.FilePath);
            SetStatusMessage("Restarting language server…");
        }

        private void TypeInstallCommandInTerminal(string cmd)
        {
            if (string.IsNullOrEmpty(cmd)) return;
            EnsureInitialTerminalSession();
            SwitchBottomTab("terminal");
            // Typed, not executed: installing software is the user's decision (press Enter to run).
            activeTerminalSession?.Control?.WriteAutomatedInput(cmd);
            SetStatusMessage("Press Enter in the terminal to install, then use Restart Language Server.");
        }

        private void ShowLanguageServerHint(LanguageServerHint hint)
        {
            if (hint == null) return;
            var host = CommandPalette?.Parent as Panel;
            // The toast carries the message; repeating it in the status bar only duplicates it (VS Code shows one).
            if (host == null) { SetStatusMessage(hint.Message); return; }
            if (lspToast != null) host.Children.Remove(lspToast);
            var text = new TextBlock { Text = hint.Message };
            Border toast = null;
            void Close() { if (toast != null) host.Children.Remove(toast); if (ReferenceEquals(lspToast, toast)) { lspToast = null; lspToastText = null; } }
            var buttons = new List<Button>();
            if (!string.IsNullOrEmpty(hint.InstallCommand))
            {
                var install = new Button { Content = "Install in Terminal" };
                ToolTip.SetTip(install, hint.InstallCommand);
                install.Click += (s, e) => { Close(); TypeInstallCommandInTerminal(hint.InstallCommand); };
                buttons.Add(install);
            }
            var never = new Button { Content = "Don't Show Again" };
            never.Click += (s, e) =>
            {
                settings.DismissLanguageServerHint(hint.LanguageId);
                try { settings.Save(); } catch { }
                Close();
            };
            buttons.Add(never);
            toast = WorkbenchStyles.BuildNotification(currentTheme, "info", text, null, buttons.ToArray(), !string.IsNullOrEmpty(hint.InstallCommand), () => Close());
            toast.Margin = new Thickness(0, 0, 12, UiTokens.StatusBarHeight + 10);
            toast.ZIndex = 180;
            lspToastText = text;
            lspToast = toast;
            host.Children.Add(toast);
            DispatcherTimer.RunOnce(Close, TimeSpan.FromSeconds(20));
        }

        /// <summary>Text of the visible "install a language server" hint ("" when none). Test hook.</summary>
        public string LanguageServerHintText => lspToast != null ? lspToastText?.Text ?? "" : "";

        /// <summary>Closes the "install a language server" toast if it is showing.</summary>
        public void DismissLanguageServerHintToast()
        {
            if (lspToast != null && lspToast.Parent is Panel p) p.Children.Remove(lspToast);
            lspToast = null;
            lspToastText = null;
        }

        public void ShowLanguageServerLog()
        {
            if (lsp == null) { SetStatusMessage("Language servers are not available in this window."); return; }
            string path = activeTab?.FilePath;
            var box = new TextBox
            {
                Text = lsp.GetLog(path),
                IsReadOnly = true,
                AcceptsReturn = true,
                TextWrapping = TextWrapping.NoWrap,
                FontFamily = new FontFamily("Menlo, Consolas, monospace"),
                FontSize = 11.5,
                Height = 380
            };
            box.CaretIndex = box.Text?.Length ?? 0;
            var refresh = new Button { Content = "Refresh", Padding = new Thickness(12, 4) };
            refresh.Click += (s, e) => { box.Text = lsp?.GetLog(path) ?? ""; box.CaretIndex = box.Text.Length; };
            var copy = new Button { Content = "Copy", Padding = new Thickness(12, 4) };
            copy.Click += async (s, e) => { var cb = Clipboard; if (cb != null) await cb.SetTextAsync(box.Text ?? ""); };
            var restart = new Button { Content = "Restart Server", Padding = new Thickness(12, 4) };
            restart.Click += (s, e) => { RestartLanguageServerForActiveFile(); };
            var close = new Button { Content = "Close", Padding = new Thickness(12, 4) };
            close.Click += (s, e) => ModalHost.CloseModal();
            var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right, Children = { refresh, copy, restart, close } };
            var view = new StackPanel { Spacing = 8, Children = { box, buttons } };
            ModalHost.ShowModal("lsp-log", "Language Server Log", view, 860);
        }

        public void ShowLanguageServerSettingsModal()
        {
            var overrides = new TextBox
            {
                Text = string.IsNullOrWhiteSpace(settings.LanguageServerOverrides) ? LanguageServerUserSettings.OverridesHelp : settings.LanguageServerOverrides,
                AcceptsReturn = true,
                TextWrapping = TextWrapping.NoWrap,
                FontFamily = new FontFamily("Menlo, Consolas, monospace"),
                FontSize = 11.5,
                Height = 150
            };
            var disableAll = new CheckBox { Content = "Disable all language servers", IsChecked = settings.DisableLanguageServers };
            var detected = new TextBlock { Text = "Detecting installed language servers…", FontSize = 11.5, TextWrapping = TextWrapping.Wrap, Foreground = AvaloniaColorHelper.ToBrush(currentTheme.TextSecondary) };
            var save = new Button { Content = "Save", Padding = new Thickness(16, 4), Background = AvaloniaColorHelper.ToBrush(currentTheme.AccentPrimary), Foreground = Brushes.White };
            var cancel = new Button { Content = "Cancel", Padding = new Thickness(14, 4) };
            cancel.Click += (s, e) => ModalHost.CloseModal();
            save.Click += (s, e) =>
            {
                string text = overrides.Text ?? "";
                settings.LanguageServerOverrides = text.Trim() == LanguageServerUserSettings.OverridesHelp.Trim() ? "" : text;
                settings.DisableLanguageServers = disableAll.IsChecked == true;
                try { settings.Save(); } catch { }
                ModalHost.CloseModal();
                if (lsp != null)
                {
                    LanguageServerRegistry.ClearCache();
                    foreach (var inst in lsp.GetInstances()) lsp.RestartInstance(inst);
                }
                SetStatusMessage("Language server settings saved.");
            };
            var view = new StackPanel
            {
                Spacing = 8,
                Children =
                {
                    new TextBlock { Text = "Overrides (one per line: language = command args, or language = off):", FontSize = 12, Foreground = AvaloniaColorHelper.ToBrush(currentTheme.TextPrimary) },
                    overrides,
                    disableAll,
                    new TextBlock { Text = "Detected servers:", FontSize = 12, FontWeight = FontWeight.SemiBold, Foreground = AvaloniaColorHelper.ToBrush(currentTheme.TextPrimary) },
                    new ScrollViewer { Content = detected, MaxHeight = 200 },
                    new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right, Children = { save, cancel } }
                }
            };
            ModalHost.ShowModal("lsp-settings", "Language Servers", view, 720);
            string root = workspaceRoot;
            var user = settings.GetLanguageServerUserSettings();
            var ui = UiPost.CaptureCurrent();
            _ = Task.Run(() =>
            {
                var lines = new List<string>();
                foreach (var d in LanguageServerRegistry.Definitions)
                {
                    LanguageServerResolution r;
                    try { r = LanguageServerRegistry.Resolve(d.Id, string.IsNullOrEmpty(root) ? null : root, user); }
                    catch (Exception ex) { lines.Add(d.DisplayName + ": error " + ex.Message); continue; }
                    if (r.CanStart) lines.Add("✓ " + d.DisplayName + ": " + r.DisplayName + "  (" + r.Executable + ")");
                    else if (r.Disabled) lines.Add("– " + d.DisplayName + ": off");
                    else lines.Add("✗ " + d.DisplayName + ": not found" + (r.InstallCommand != null ? "  — install: " + r.InstallCommand : ""));
                }
                string text = string.Join("\n", lines);
                ui.Post(() => detected.Text = text);
            });
        }

        // ------------------------------------------------------------------ navigation / refactoring commands

        private static async Task<T> WithCap<T>(Task<T> t, int ms) where T : class
        {
            var done = await Task.WhenAny(t, Task.Delay(ms));
            return done == t && !t.IsFaulted && !t.IsCanceled ? t.Result : null;
        }

        /// <summary>F12 / Ctrl+Click on an identifier: language server first, built-in resolver as fallback.</summary>
        private async Task<bool> TryLspNavigateAsync(EditorTabBuffer buf, int line, int col, string method, string what)
        {
            string path = LspPathFor(buf);
            if (path == null || !lsp.IsActive(path)) return false;
            List<LspLocation> locs;
            try { locs = await WithCap(lsp.RequestLocationsAsync(path, line, col, method, CancellationToken.None), 4000); }
            catch { locs = null; }
            if (locs == null) return false;
            if (locs.Count == 0)
            {
                if (method == "textDocument/definition") return false; // let the heuristic resolver try
                SetStatusMessage("No " + what + " found.");
                return true;
            }
            if (locs.Count == 1) NavigateToLspLocation(buf, locs[0]);
            else ShowLocationPicker(buf, locs, what);
            return true;
        }

        internal void NavigateToLspLocation(EditorTabBuffer source, LspLocation loc)
        {
            if (loc == null || string.IsNullOrEmpty(loc.FilePath)) return;
            int line = loc.Range != null ? loc.Range.StartLine + 1 : 1;
            int col = loc.Range != null ? loc.Range.StartCharacter + 1 : 1;
            NavigateToFileLocation(source, loc.FilePath, line, col, null);
        }

        private string LocationPreview(LspLocation loc)
        {
            if (loc?.Range == null) return "";
            var tab = FindOpenTabByPath(loc.FilePath);
            try
            {
                if (tab != null) return loc.Range.StartLine < tab.Lines.Count ? tab.Lines[loc.Range.StartLine].Trim() : "";
                if (File.Exists(loc.FilePath) && new FileInfo(loc.FilePath).Length < 8 * 1024 * 1024)
                    return (File.ReadLines(loc.FilePath).Skip(loc.Range.StartLine).FirstOrDefault() ?? "").Trim();
            }
            catch { }
            return "";
        }

        private void ShowLocationPicker(EditorTabBuffer source, List<LspLocation> locs, string what)
        {
            var entries = new List<PaletteEntryItem>();
            foreach (var l in locs.Take(500))
            {
                var loc = l;
                string rel = !string.IsNullOrEmpty(workspaceRoot) ? LlmHarnessAndAgentEngine.GetRelativePath(workspaceRoot, loc.FilePath) : Path.GetFileName(loc.FilePath);
                entries.Add(new PaletteEntryItem
                {
                    Category = rel + ":" + (loc.Range.StartLine + 1),
                    Title = LocationPreview(loc),
                    stringSubtitleOrPath = loc.FilePath,
                    Execute = () => NavigateToLspLocation(source, loc)
                });
            }
            CommandPalette.Open("", entries, currentTheme?.Id);
            CommandPalette.ModeHintLabel.Text = locs.Count + " " + what + (locs.Count > 500 ? " (first 500 shown)" : "") + " — ↑↓ Navigate • Enter Open • Esc Close";
            SetStatusMessage(locs.Count + " " + what);
        }

        public async void GoToTypeDefinitionAtCursor()
        {
            if (activeTab == null) return;
            if (!await TryLspNavigateAsync(activeTab, activeTab.CursorLine, activeTab.CursorCol, "textDocument/typeDefinition", "type definitions"))
                SetStatusMessage("Go to Type Definition needs a language server for this file.");
        }

        public async void GoToImplementationAtCursor()
        {
            if (activeTab == null) return;
            if (!await TryLspNavigateAsync(activeTab, activeTab.CursorLine, activeTab.CursorCol, "textDocument/implementation", "implementations"))
                SetStatusMessage("Go to Implementation needs a language server for this file.");
        }

        public async void FindAllReferencesAtCursor()
        {
            var buf = activeTab;
            if (buf == null) return;
            string path = LspPathFor(buf);
            if (path == null || !lsp.IsActive(path))
            {
                SetStatusMessage("Find All References needs a language server for this file.");
                return;
            }
            SetStatusMessage("Finding references…");
            List<LspLocation> refs;
            try { refs = await lsp.RequestReferencesAsync(path, buf.CursorLine, buf.CursorCol, true, CancellationToken.None); }
            catch { refs = null; }
            if (refs == null) { SetStatusMessage("The language server could not find references."); return; }
            if (refs.Count == 0) { SetStatusMessage("No references found."); return; }
            ShowLocationPicker(buf, refs, "references");
        }

        public async void RenameSymbolAtCursor()
        {
            var buf = activeTab;
            if (buf == null) return;
            string path = LspPathFor(buf);
            if (path == null || !lsp.Supports(path, "renameProvider"))
            {
                PrimaryEditor.ShowFindBar(true); // previous behaviour without a language server
                return;
            }
            int line = buf.CursorLine, col = buf.CursorCol;
            string placeholder;
            try { placeholder = await lsp.PrepareRenameAsync(path, line, col, CancellationToken.None); }
            catch (Exception ex)
            {
                var inner = ex is AggregateException && ex.InnerException != null ? ex.InnerException : ex;
                SetStatusMessage("Rename: " + inner.Message);
                return;
            }
            if (placeholder == null) placeholder = LspEditing.WordAt(buf.Lines[line], col, out int _);
            if (string.IsNullOrEmpty(placeholder)) { SetStatusMessage("Nothing to rename at the cursor."); return; }
            var view = AvaloniaDialogBuilder.BuildPromptInputView(
                "Rename '" + placeholder + "' to:",
                placeholder,
                currentTheme,
                newName =>
                {
                    ModalHost.CloseModal();
                    if (!string.IsNullOrWhiteSpace(newName) && newName != placeholder) _ = ApplyRenameAsync(buf, path, line, col, newName.Trim());
                },
                () => ModalHost.CloseModal(),
                out var tb);
            ModalHost.ShowModal("lsp-rename", "Rename Symbol", view, 440);
            FocusPromptTextBox(tb);
        }

        /// <summary>Requests a rename and applies the resulting WorkspaceEdit (open buffers get an undo step, closed files are written).</summary>
        public async Task<LspApplyResult> ApplyRenameAsync(EditorTabBuffer buf, string path, int line, int col, string newName)
        {
            LspWorkspaceEdit edit;
            // Snapshot before the request: typing while the server computes the rename must not shift its offsets (LSP-6).
            var guard = LspEditGuard.Capture(openTabs, lsp);
            try { edit = await lsp.RequestRenameAsync(path, line, col, newName, CancellationToken.None); }
            catch (Exception ex)
            {
                var inner = ex is AggregateException && ex.InnerException != null ? ex.InnerException : ex;
                SetStatusMessage("Rename failed: " + inner.Message);
                return null;
            }
            if (edit == null || edit.IsEmpty) { SetStatusMessage("Rename: the language server returned no changes."); return null; }
            return ApplyLspWorkspaceEdit(edit, "Renamed to '" + newName + "':", guard);
        }

        internal LspApplyResult ApplyLspWorkspaceEdit(LspWorkspaceEdit edit, string verb) => ApplyLspWorkspaceEdit(edit, verb, null);

        internal LspApplyResult ApplyLspWorkspaceEdit(LspWorkspaceEdit edit, string verb, LspEditGuard guard)
        {
            var result = LspEditing.ApplyWorkspaceEdit(edit, FindOpenTabByPath, (oldPath, newPath) =>
            {
                var tab = FindOpenTabByPath(oldPath);
                if (tab == null) return;
                tab.FilePath = newPath;
                tab.FileName = Path.GetFileName(newPath);
                tab.RelativePath = !string.IsNullOrEmpty(workspaceRoot) ? LlmHarnessAndAgentEngine.GetRelativePath(workspaceRoot, newPath) : tab.FileName;
            }, guard);
            if (result.Rejected)
            {
                SetStatusMessage(verb + " not applied: " + result.Errors[0]);
                return result;
            }
            foreach (var b in result.ChangedBuffers)
            {
                if (ReferenceEquals(PrimaryEditor.CurrentBuffer, b)) PrimaryEditor.Canvas.CommitExternalEdit();
                else if (ReferenceEquals(SplitEditor.CurrentBuffer, b)) SplitEditor.Canvas.CommitExternalEdit();
                else
                {
                    AnalyzeBufferSoon(b);
                    if (lsp != null && !string.IsNullOrEmpty(b.FilePath)) lsp.ChangeDocument(b.FilePath);
                }
            }
            LspSyncOpenDocuments();
            LspOnTabActivated(activeTab);
            RefreshTabStrip();
            RefreshOpenEditorsSection();
            UpdateWindowTitle();
            SetStatusMessage(verb + " " + result.Summary);
            return result;
        }

        public async void FormatDocumentOrSelection(bool selectionOnly)
        {
            var buf = activeTab;
            if (buf == null) return;
            string path = LspPathFor(buf);
            if (path == null || !lsp.IsActive(path)) { SetStatusMessage("No formatter: install a language server for this file type."); return; }
            LspRange range = null;
            if (selectionOnly && buf.HasSelection())
            {
                buf.GetNormalizedSelection(out int sl, out int sc, out int el, out int ec);
                range = new LspRange(sl, sc, el, ec);
            }
            string before = LspEditing.BufferText(buf);
            var edits = await lsp.RequestFormattingAsync(path, range, settings.TabSize, settings.InsertSpaces, CancellationToken.None);
            if (edits == null) { SetStatusMessage("The language server does not support formatting this file."); return; }
            if (!ReferenceEquals(buf, activeTab) || LspEditing.BufferText(buf) != before) { SetStatusMessage("Format skipped: the document changed."); return; }
            if (edits.Count == 0) { SetStatusMessage("Already formatted."); return; }
            if (!LspEditing.ApplyEditsToBuffer(buf, edits, true)) { SetStatusMessage("Format failed: overlapping edits."); return; }
            PrimaryEditor.Canvas.CommitExternalEdit();
            SetStatusMessage("Formatted (" + edits.Count + " edit" + (edits.Count == 1 ? "" : "s") + ").");
        }

        public async void ShowQuickFixes()
        {
            var buf = activeTab;
            if (buf == null) return;
            string path = LspPathFor(buf);
            if (path == null || !lsp.Supports(path, "codeActionProvider")) { SetStatusMessage("No code actions available (no language server)."); return; }
            LspRange range;
            if (buf.HasSelection())
            {
                buf.GetNormalizedSelection(out int sl, out int sc, out int el, out int ec);
                range = new LspRange(sl, sc, el, ec);
            }
            else range = new LspRange(buf.CursorLine, buf.CursorCol, buf.CursorLine, buf.CursorCol);
            var listGuard = LspEditGuard.Capture(openTabs, lsp);
            var actions = await lsp.RequestCodeActionsAsync(path, range, CancellationToken.None);
            if (actions == null || actions.Count == 0) { SetStatusMessage("No code actions available."); return; }
            var ordered = actions.OrderByDescending(a => a.IsPreferred).ThenBy(a => a.DisabledReason != null).ToList();
            var entries = ordered.Select(a => new PaletteEntryItem
            {
                Category = a.DisabledReason != null ? "Unavailable" : (string.IsNullOrEmpty(a.Kind) ? "Action" : (a.Kind.StartsWith("quickfix") ? "Quick Fix" : a.Kind)),
                Title = a.Title + (a.IsPreferred ? "  ★" : "") + (a.DisabledReason != null ? "  (" + a.DisabledReason + ")" : ""),
                Execute = () => { if (a.DisabledReason == null) _ = ApplyCodeActionAsync(path, a, listGuard); }
            }).ToList();
            CommandPalette.Open("", entries, currentTheme?.Id);
            CommandPalette.ModeHintLabel.Text = "Code Actions — Enter to apply";
        }

        public async Task ApplyCodeActionAsync(string path, LspCodeAction action, LspEditGuard listGuard = null)
        {
            if (lsp == null || action == null) return;
            // An edit that came with the list was computed for the text at list time; a lazily resolved one for the text at resolve time.
            var guard = action.Edit != null && listGuard != null ? listGuard : LspEditGuard.Capture(openTabs, lsp);
            var resolved = await lsp.ResolveCodeActionAsync(path, action, CancellationToken.None) ?? action;
            if (resolved.Edit != null) ApplyLspWorkspaceEdit(resolved.Edit, resolved.Title + ":", guard);
            if (resolved.Command != null && lsp.ClassifyCommand(path, resolved.Command) == LanguageServerManager.LspCommandTarget.Server)
                await lsp.ExecuteCommandAsync(path, resolved.Command, CancellationToken.None);
        }

        private List<PaletteEntryItem> BuildLanguageServerPaletteCommands()
        {
            return new List<PaletteEntryItem>
            {
                new PaletteEntryItem { Category = "Go", Title = "Go to Type Definition", Execute = GoToTypeDefinitionAtCursor },
                new PaletteEntryItem { Category = "Go", Title = "Go to Implementation", Shortcut = "Ctrl+F12", Execute = GoToImplementationAtCursor },
                new PaletteEntryItem { Category = "Go", Title = "Find All References", Shortcut = "Shift+F12", Execute = FindAllReferencesAtCursor },
                new PaletteEntryItem { Category = "Edit", Title = "Rename Symbol", Shortcut = "F2", Execute = RenameSymbolAtCursor },
                new PaletteEntryItem { Category = "Edit", Title = "Format Document", Shortcut = "Shift+Alt+F", Execute = () => FormatDocumentOrSelection(false) },
                new PaletteEntryItem { Category = "Edit", Title = "Format Selection", Execute = () => FormatDocumentOrSelection(true) },
                new PaletteEntryItem { Category = "Edit", Title = "Quick Fix...", Shortcut = "Ctrl+.", Execute = ShowQuickFixes },
                new PaletteEntryItem { Category = "Edit", Title = "Trigger Suggest", Shortcut = "Ctrl+Space", Execute = () => PrimaryLspBinding?.TriggerCompletion(null, explicitInvoke: true) },
                new PaletteEntryItem { Category = "Edit", Title = "Trigger Parameter Hints", Shortcut = "Ctrl+Shift+Space", Execute = () => PrimaryLspBinding?.TriggerSignatureHelp(null, false) },
                new PaletteEntryItem { Category = "Language Server", Title = "Restart Language Server", Execute = RestartLanguageServerForActiveFile },
                new PaletteEntryItem { Category = "Language Server", Title = "Show Language Server Log", Execute = ShowLanguageServerLog },
                new PaletteEntryItem { Category = "Language Server", Title = "Configure Language Servers...", Execute = ShowLanguageServerSettingsModal },
            };
        }

        /// <summary>Editor-focused LSP shortcuts (called from the canvas key preview). Returns true when handled.</summary>
        public bool HandleLanguageServerEditorKey(KeyEventArgs e)
        {
            bool ctrl = e.KeyModifiers.HasFlag(KeyModifiers.Control);
            bool primary = MacKeymap.IsPrimary(e.KeyModifiers);
            bool shift = e.KeyModifiers.HasFlag(KeyModifiers.Shift);
            bool alt = e.KeyModifiers.HasFlag(KeyModifiers.Alt);
            if (e.Key == Key.F2 && !primary && !shift && !alt) { RenameSymbolAtCursor(); return true; }
            if (e.Key == Key.F12 && shift && !primary && !alt) { FindAllReferencesAtCursor(); return true; }
            if (e.Key == Key.F12 && primary && !shift && !alt) { GoToImplementationAtCursor(); return true; }
            if (e.Key == Key.F && shift && alt && !primary) { FormatDocumentOrSelection(activeTab != null && activeTab.HasSelection()); return true; }
            if (e.Key == Key.OemPeriod && primary && !shift && !alt) { ShowQuickFixes(); return true; }
            return false;
        }

        /// <summary>Wraps FollowEditorLink: identifiers go to the language server first (Cmd/Ctrl+Click).</summary>
        private async void FollowEditorLinkWithLsp(AvaloniaEditorControl editor, EditorTabBuffer buf, EditorLinkTarget target, int line)
        {
            if (target != null && target.Kind == EditorLinkTarget.KindSymbol && lsp != null && buf != null)
            {
                var binding = lspBindings.FirstOrDefault(b => ReferenceEquals(b.Editor, editor));
                int col = binding != null ? binding.LastPointerColumn(line) : -1;
                if (col < 0) col = buf.CursorLine == line ? buf.CursorCol : -1;
                if (col >= 0 && await TryLspNavigateAsync(buf, line, col, "textDocument/definition", "definitions")) return;
            }
            FollowEditorLink(buf, target);
        }
    }

    /// <summary>Per-editor LSP UI state: completion popup, hover and signature tips, request cancellation.</summary>
    public sealed partial class LspEditorBinding
    {
        private readonly MainWindow window;
        public readonly AvaloniaEditorControl Editor;
        public readonly LspCompletionPopup Completion = new LspCompletionPopup();
        public readonly LspInfoTip HoverTip = new LspInfoTip();
        public readonly LspInfoTip SignatureTip = new LspInfoTip();
        private LspCompletionSession session;
        private CancellationTokenSource completionCts, hoverCts, signatureCts, resolveCts;
        private readonly DispatcherTimer hoverTimer;
        private readonly DispatcherTimer symbolsTimer;
        private Point hoverPt, lastPointerPt;
        private int hoverLine = -1, hoverWordStart = -1;
        private int completionGeneration;

        public LspEditorBinding(MainWindow window, AvaloniaEditorControl editor)
        {
            this.window = window;
            Editor = editor;
            editor.OverlayHost.Children.Add(Completion);
            editor.OverlayHost.Children.Add(HoverTip);
            editor.OverlayHost.Children.Add(SignatureTip);
            SignatureTip.AnchorAbove = true;
            Completion.ItemClicked += idx =>
            {
                if (session == null) return;
                session.SelectedIndex = idx;
                _ = AcceptSelectedAsync();
            };
            hoverTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
            hoverTimer.Tick += (s, e) => { hoverTimer.Stop(); _ = RequestHoverAsync(hoverPt); };
            symbolsTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(700) };
            symbolsTimer.Tick += (s, e) => { symbolsTimer.Stop(); _ = RefreshSymbolsAsync(); };
            editor.Canvas.PreviewKeyHandler = OnPreviewKey;
            editor.Canvas.TextTyped += OnTextTyped;
            editor.Canvas.PointerMovedOverCanvas += OnPointerMoved;
            editor.Canvas.PointerLeftCanvas += OnPointerLeft;
            editor.CaretMoved += OnCaretMoved;
        }

        public void Detach()
        {
            HideAll();
            hoverTimer.Stop();
            symbolsTimer.Stop();
            Editor.Canvas.PreviewKeyHandler = null;
            Editor.Canvas.TextTyped -= OnTextTyped;
            Editor.Canvas.PointerMovedOverCanvas -= OnPointerMoved;
            Editor.Canvas.PointerLeftCanvas -= OnPointerLeft;
            Editor.CaretMoved -= OnCaretMoved;
        }

        public void ApplyTheme(StudioTheme t)
        {
            Completion.ApplyTheme(t);
            HoverTip.ApplyTheme(t);
            SignatureTip.ApplyTheme(t);
        }

        private LanguageServerManager Lsp => window.LanguageServers;
        private EditorTabBuffer Buf => Editor.CurrentBuffer;
        private string PathOrNull => window.LspPathFor(Buf);
        public bool IsCompletionVisible => Completion.IsVisible;
        public LspCompletionSession Session => session;

        public void HideAll()
        {
            CloseCompletion();
            HideHover();
            HideSignature();
        }

        /// <summary>Screenshot harness only: shows the suggest widget with fixture items at (line, col).</summary>
        public void ShowFixtureCompletion(LspCompletionList list, int line, int col)
        {
            var buf = Buf;
            if (buf == null || list == null || list.Items.Count == 0) return;
            buf.CursorLine = Math.Min(line, buf.Lines.Count - 1);
            buf.CursorCol = Math.Min(col, buf.Lines[buf.CursorLine].Length);
            foreach (var it in list.Items) it.Resolved = true;
            var s = new LspCompletionSession { FilePath = buf.FilePath, Line = buf.CursorLine, StartCol = buf.CursorCol };
            s.SetItems(list);
            if (!s.Refilter(buf.Lines[buf.CursorLine], buf.CursorLine, buf.CursorCol)) return;
            session = s;
            Editor.Canvas.InvalidateVisual();
            PositionCompletion();
            Completion.Show(s);
            Completion.SetDocumentation(DocFor(s.Selected));
        }

        private void HideHover()
        {
            hoverTimer.Stop();
            hoverCts?.Cancel();
            HoverTip.Hide();
            hoverLine = -1;
        }

        private void HideSignature()
        {
            signatureCts?.Cancel();
            SignatureTip.Hide();
        }

        public void CloseCompletion()
        {
            completionCts?.Cancel();
            resolveCts?.Cancel();
            session = null;
            Completion.Hide();
        }

        /// <summary>Column under the last pointer position on <paramref name="line"/> (-1 when unknown).</summary>
        public int LastPointerColumn(int line)
        {
            return Editor.Canvas.TryHitTestText(lastPointerPt, out int l, out int c) && l == line ? c : -1;
        }

        // ---------------------------------------------------------------- keys

        private bool OnPreviewKey(KeyEventArgs e)
        {
            if (Buf == null) return false;
            bool ctrl = e.KeyModifiers.HasFlag(KeyModifiers.Control);
            bool shift = e.KeyModifiers.HasFlag(KeyModifiers.Shift);
            bool alt = e.KeyModifiers.HasFlag(KeyModifiers.Alt);
            bool meta = e.KeyModifiers.HasFlag(KeyModifiers.Meta);
            if (HoverTip.IsVisible) HideHover();
            if (Completion.IsVisible && session != null)
            {
                bool plain = !ctrl && !alt && !meta;
                switch (e.Key)
                {
                    case Key.Down when plain && !shift: Move(1); return true;
                    case Key.Up when plain && !shift: Move(-1); return true;
                    case Key.PageDown when plain: Move(LspCompletionPopup.VisibleRows); return true;
                    case Key.PageUp when plain: Move(-LspCompletionPopup.VisibleRows); return true;
                    case Key.Enter when plain && !shift:
                    case Key.Tab when plain && !shift:
                        _ = AcceptSelectedAsync();
                        return true;
                    case Key.Escape:
                        CloseCompletion();
                        return true;
                    case Key.Left:
                    case Key.Right:
                    case Key.Home:
                    case Key.End:
                        break; // caret moves; OnCaretMoved re-filters or closes
                }
            }
            if (e.Key == Key.Escape && SignatureTip.IsVisible) { HideSignature(); return true; }
            // Ctrl+Space on every platform (Cmd+Space is Spotlight on macOS), like VS Code.
            if (e.Key == Key.Space && ctrl && !alt && !meta)
            {
                if (shift) TriggerSignatureHelp(null, false);
                else TriggerCompletion(null, explicitInvoke: true);
                return true;
            }
            return window.LanguageServers != null && window.HandleLanguageServerEditorKey(e);
        }

        private void Move(int delta)
        {
            session.MoveSelection(delta);
            Completion.Render();
            ScheduleResolve();
        }

        // ---------------------------------------------------------------- typing / caret

        private void OnTextTyped(string text)
        {
            var lsp = Lsp;
            string path = PathOrNull;
            HideHover();
            if (string.IsNullOrEmpty(text) || Buf == null) return;
            if (TryImportCompletionOnTyped(text)) return; // import paths / Python modules (merged with the server's items)
            if (lsp == null || path == null || !lsp.IsActive(path)) { OnTextTypedWordBased(text); return; }
            var buf = Buf;
            string lineText = buf.Lines[buf.CursorLine];
            // Signature help triggers / closes.
            char last = text[text.Length - 1];
            if (last == ')') HideSignature();
            if (lsp.GetSignatureTriggerCharacters(path, false).Contains(last.ToString())) TriggerSignatureHelp(last.ToString(), false);
            else if (SignatureTip.IsVisible) TriggerSignatureHelp(null, true);

            if (session != null && Completion.IsVisible)
            {
                if (LspEditing.IsWordChar(last))
                {
                    if (session.IsIncomplete) TriggerCompletion(null, incompleteRetrigger: true);
                    return; // OnBufferModified already re-filtered
                }
                CloseCompletion();
            }
            if (LspCompletionSession.ShouldTrigger(lineText, buf.CursorCol, text, lsp.GetCompletionTriggerCharacters(path), 1, out string trig))
                TriggerCompletion(trig);
        }

        public void OnBufferModified(EditorTabBuffer buf)
        {
            if (!ReferenceEquals(buf, Buf)) return;
            HideHover();
            if (session != null && Completion.IsVisible) Refilter();
            symbolsTimer.Stop();
            symbolsTimer.Start();
        }

        private void OnCaretMoved(EditorTabBuffer buf)
        {
            if (session != null && Completion.IsVisible) Refilter();
            if (SignatureTip.IsVisible && buf != null && buf.CursorLine != signatureLine) HideSignature();
        }

        private void Refilter()
        {
            var buf = Buf;
            if (buf == null || session == null) return;
            buf.ClampCursor();
            if (!session.Refilter(buf.Lines[buf.CursorLine], buf.CursorLine, buf.CursorCol)) { CloseCompletion(); return; }
            Completion.Render();
            ScheduleResolve();
        }

        // ---------------------------------------------------------------- completion

        // ---------------------------------------------------------------- word-based suggestions (no language server, QA #31)

        /// <summary>As-you-type without a server: identifier characters open the word / snippet list, never in comments or strings.</summary>
        private void OnTextTypedWordBased(string text)
        {
            var buf = Buf;
            char last = text[text.Length - 1];
            if (session != null && Completion.IsVisible)
            {
                if (LspEditing.IsWordChar(last)) return; // OnBufferModified already re-filtered
                CloseCompletion();
            }
            buf.ClampCursor();
            string lineText = buf.Lines[buf.CursorLine];
            if (!LspCompletionSession.ShouldTrigger(lineText, buf.CursorCol, text, null, 1, out _)) return;
            // the word typed so far had no suggestions: more characters of the same word cannot add any (VS Code does not re-ask)
            int ws = LspEditing.WordStart(lineText, buf.CursorCol);
            if (ReferenceEquals(buf, noWordsBuf) && buf.CursorLine == noWordsLine && ws == noWordsStart && buf.CursorCol > noWordsCol) return;
            if (!WordBasedSuggestions.QuickSuggestionsAllowed(buf, buf.CursorLine, buf.CursorCol)) return;
            ShowWordBasedSuggestions();
        }

        private EditorTabBuffer noWordsBuf;
        private int noWordsLine = -1, noWordsStart = -1, noWordsCol = -1;

        /// <summary>Words of the open documents of the same language + the language's snippets, in the suggest widget.</summary>
        public void ShowWordBasedSuggestions()
        {
            var buf = Buf;
            if (buf == null || !WordBasedSuggestions.Enabled) { CloseCompletion(); return; }
            buf.ClampCursor();
            int line = buf.CursorLine, col = buf.CursorCol;
            string lineText = buf.Lines[line];
            int start = LspEditing.WordStart(lineText, col);
            var items = WordBasedSuggestions.Build(buf, window.OpenTabs, line, col, window.SnippetsForBuffer(buf), lineText.Substring(start, col - start));
            var s = new LspCompletionSession { FilePath = buf.FilePath, Line = line, StartCol = start };
            s.SetItems(new LspCompletionList { Items = items });
            if (items.Count == 0 || !s.Refilter(lineText, line, col))
            {
                noWordsBuf = buf; noWordsLine = line; noWordsStart = start; noWordsCol = col;
                CloseCompletion();
                return;
            }
            noWordsBuf = null;
            completionCts?.Cancel();
            session = s;
            PositionCompletion();
            Completion.Show(s);
            ScheduleResolve();
        }

        public async void TriggerCompletion(string triggerCharacter, bool incompleteRetrigger = false, bool explicitInvoke = false)
        {
            var lsp = Lsp;
            var buf = Buf;
            string path = PathOrNull;
            if (buf == null) return;
            if (!incompleteRetrigger && TryStartImportCompletionAtCaret(triggerCharacter)) return;
            if (lsp == null || path == null || !lsp.IsActive(path))
            {
                if (triggerCharacter == null && !incompleteRetrigger) ShowWordBasedSuggestions(); // Ctrl+Space works in comments / strings too
                return;
            }
            completionCts?.Cancel();
            var cts = new CancellationTokenSource();
            completionCts = cts;
            int gen = ++completionGeneration;
            buf.ClampCursor();
            int line = buf.CursorLine, col = buf.CursorCol;
            string lineText = buf.Lines[line];
            int startCol = LspEditing.WordStart(lineText, col);
            LspCompletionList list;
            try { list = await lsp.RequestCompletionAsync(path, line, col, triggerCharacter, incompleteRetrigger, cts.Token); }
            catch { return; }
            if (cts.IsCancellationRequested || gen != completionGeneration || !ReferenceEquals(buf, Buf)) return;
            if (list == null || list.Items.Count == 0)
            {
                if (incompleteRetrigger) return;
                // VS Code: word-based suggestions appear when no other provider has results (not after trigger characters)
                buf.ClampCursor();
                if (triggerCharacter == null && (explicitInvoke || WordBasedSuggestions.QuickSuggestionsAllowed(buf, buf.CursorLine, buf.CursorCol))) ShowWordBasedSuggestions();
                else CloseCompletion();
                return;
            }
            var s = new LspCompletionSession { FilePath = path, Line = line, StartCol = startCol };
            // A server-provided edit range defines where the word being completed starts.
            var first = list.Items.FirstOrDefault(i => i.TextEdit?.Range != null && i.TextEdit.Range.StartLine == line);
            if (first != null && first.TextEdit.Range.StartCharacter < startCol && IsWordRun(lineText, first.TextEdit.Range.StartCharacter, startCol))
                s.StartCol = first.TextEdit.Range.StartCharacter;
            s.SetItems(list);
            buf.ClampCursor();
            if (!s.Refilter(buf.Lines[buf.CursorLine], buf.CursorLine, buf.CursorCol)) { CloseCompletion(); return; }
            session = s;
            PositionCompletion();
            Completion.Show(s);
            ScheduleResolve();
        }

        private static bool IsWordRun(string line, int from, int to)
        {
            for (int i = from; i < to && i < line.Length; i++) if (!LspEditing.IsWordChar(line[i])) return false;
            return true;
        }

        private void PositionCompletion()
        {
            Rect caret = Editor.Canvas.CaretRect;
            double hostH = Editor.OverlayHost.Bounds.Height;
            double hostW = Editor.OverlayHost.Bounds.Width;
            double popupH = LspCompletionPopup.VisibleRows * LspCompletionPopup.RowHeight + 4;
            double y = caret.Bottom + 2;
            if (hostH > 0 && y + popupH > hostH && caret.Top - popupH > 0) y = caret.Top - popupH - 2;
            double x = Math.Max(0, caret.X - 24);
            if (hostW > 0) x = Math.Min(x, Math.Max(0, hostW - 400));
            Completion.Margin = new Thickness(x, y, 0, 0);
        }

        private void ScheduleResolve()
        {
            var item = session?.Selected;
            resolveCts?.Cancel();
            if (item == null) return;
            Completion.SetDocumentation(DocFor(item));
            if (item.Resolved) return;
            var cts = new CancellationTokenSource();
            resolveCts = cts;
            string path = session.FilePath;
            DispatcherTimer.RunOnce(async () =>
            {
                if (cts.IsCancellationRequested || Lsp == null) return;
                var r = await Lsp.ResolveCompletionAsync(path, item, cts.Token);
                if (cts.IsCancellationRequested || session == null || !ReferenceEquals(session.Selected, item)) return;
                Completion.SetDocumentation(DocFor(r ?? item));
            }, TimeSpan.FromMilliseconds(80));
        }

        private static string DocFor(LspCompletionItem it)
        {
            string detail = it.Detail ?? "";
            string doc = it.Documentation ?? "";
            if (detail.Length > 0 && doc.Length > 0) return detail + "\n\n" + doc;
            return detail + doc;
        }

        public async Task AcceptSelectedAsync()
        {
            var s = session;
            var item = s?.Selected;
            var buf = Buf;
            if (item == null || buf == null) { CloseCompletion(); return; }
            if (item.Tag is CodeSnippetItem snippet)
            {
                // snippets from the word-based list expand with the shared snippet engine (tab stops, variables, indentation)
                int typed = Math.Max(0, buf.CursorCol - s.StartCol);
                CloseCompletion();
                window.InsertSnippetReplacing(buf, snippet, typed);
                Editor.Canvas.CommitExternalEdit();
                return;
            }
            string before = LspEditing.BufferText(buf);
            CloseCompletion();
            var lsp = Lsp;
            if (lsp != null && !item.Resolved)
            {
                // Auto-import edits often arrive only with completionItem/resolve: wait briefly for them.
                var t = lsp.ResolveCompletionAsync(s.FilePath, item, CancellationToken.None);
                await Task.WhenAny(t, Task.Delay(400));
                if (!ReferenceEquals(buf, Buf) || LspEditing.BufferText(buf) != before) return;
            }
            PrepareImportAccept(s, item, buf);
            if (!LspEditing.ApplyCompletion(buf, item)) return;
            Editor.Canvas.CommitExternalEdit();
            if (RetriggerAfterImportAccept(item)) return; // accepted a folder: list its contents (popup stays open)
            if (item.Command != null && lsp != null)
            {
                switch (lsp.ClassifyCommand(s.FilePath, item.Command))
                {
                    case LanguageServerManager.LspCommandTarget.TriggerSuggest: TriggerCompletion(null, false); return;
                    case LanguageServerManager.LspCommandTarget.TriggerParameterHints: TriggerSignatureHelp(null, false); return;
                    case LanguageServerManager.LspCommandTarget.Server: _ = lsp.ExecuteCommandAsync(s.FilePath, item.Command, CancellationToken.None); break;
                }
            }
            string path = PathOrNull;
            if (lsp != null && path != null && item.IsSnippet && (item.InsertText ?? item.TextEdit?.NewText ?? "").Contains("("))
                TriggerSignatureHelp(null, false);
        }

        // ---------------------------------------------------------------- signature help

        private int signatureLine = -1;

        public async void TriggerSignatureHelp(string triggerCharacter, bool retrigger)
        {
            var lsp = Lsp;
            var buf = Buf;
            string path = PathOrNull;
            if (lsp == null || buf == null || path == null) return;
            signatureCts?.Cancel();
            var cts = new CancellationTokenSource();
            signatureCts = cts;
            int line = buf.CursorLine, col = buf.CursorCol;
            LspSignatureHelp help;
            try { help = await lsp.RequestSignatureHelpAsync(path, line, col, triggerCharacter, retrigger, cts.Token); }
            catch { return; }
            if (cts.IsCancellationRequested || !ReferenceEquals(buf, Buf)) return;
            if (help == null || help.Active == null) { SignatureTip.Hide(); return; }
            Rect caret = Editor.Canvas.CaretRect;
            SignatureTip.HostHeight = Editor.OverlayHost.Bounds.Height;
            bool above = caret.Top > 60;
            SignatureTip.AnchorAbove = above;
            signatureLine = line;
            SignatureTip.ShowSignature(help, new Point(Math.Max(0, caret.X - 24), above ? caret.Top - 2 : caret.Bottom + 2));
        }

        public string SignatureText => SignatureTip.IsVisible ? SignatureTip.Text : "";
        public string HoverText => HoverTip.IsVisible ? HoverTip.Text : "";

        // ---------------------------------------------------------------- hover

        private void OnPointerMoved(Point pt)
        {
            lastPointerPt = pt;
            if (Lsp == null || PathOrNull == null) return;
            if (HoverTip.IsVisible)
            {
                // Keep the tip while the pointer stays on the same word.
                if (Editor.Canvas.TryHitTestText(pt, out int l, out int c) && l == hoverLine && Buf != null &&
                    LspEditing.WordStart(Buf.Lines[l], Math.Min(Buf.Lines[l].Length, c + 1)) == hoverWordStart)
                    return;
                HideHover();
            }
            hoverPt = pt;
            hoverTimer.Stop();
            hoverTimer.Start();
        }

        private void OnPointerLeft()
        {
            hoverTimer.Stop();
            HideHover();
        }

        /// <summary>Requests hover for a canvas point and shows the tip below the hovered line. Public for tests.</summary>
        public async Task RequestHoverAsync(Point pt)
        {
            var lsp = Lsp;
            var buf = Buf;
            string path = PathOrNull;
            if (lsp == null || buf == null || path == null || Completion.IsVisible) return;
            if (!Editor.Canvas.TryHitTestText(pt, out int line, out int col)) return;
            hoverCts?.Cancel();
            var cts = new CancellationTokenSource();
            hoverCts = cts;
            // Language-server diagnostics under the pointer are shown first (VS Code shows problems in the hover).
            var diagText = string.Join("\n", LspDiagnosticsStore.Get(path)
                .Where(d => d.Line - 1 == line && col >= d.Column - 1 && col < Math.Max(d.Column, d.EndColumn - 1 + (d.EndColumn <= d.Column ? 1 : 0)))
                .Select(d => d.Severity + ": " + d.Message + (string.IsNullOrEmpty(d.Source) ? "" : "  (" + d.Source + (string.IsNullOrEmpty(d.Code) ? "" : " " + d.Code) + ")")));
            LspHover hover = null;
            if (lsp.Supports(path, "hoverProvider"))
            {
                try { hover = await lsp.RequestHoverAsync(path, line, col, cts.Token); }
                catch { hover = null; }
            }
            if (cts.IsCancellationRequested || !ReferenceEquals(buf, Buf)) return;
            string text = hover?.Text ?? "";
            if (diagText.Length > 0) text = text.Length > 0 ? diagText + "\n────────\n" + text : diagText;
            if (string.IsNullOrWhiteSpace(text)) return;
            hoverLine = line;
            hoverWordStart = LspEditing.WordStart(buf.Lines[line], Math.Min(buf.Lines[line].Length, col + 1));
            double lineH = Math.Max(14, Editor.Canvas.CaretRect.Height);
            HoverTip.AnchorAbove = false;
            HoverTip.ShowText(text, new Point(Math.Max(0, pt.X - 8), pt.Y + lineH * 0.8));
        }

        // ---------------------------------------------------------------- outline

        private async Task RefreshSymbolsAsync()
        {
            var lsp = Lsp;
            var buf = Buf;
            string path = PathOrNull;
            if (lsp == null || buf == null || path == null || !lsp.Supports(path, "documentSymbolProvider")) return;
            var syms = await lsp.RequestDocumentSymbolsAsync(path, CancellationToken.None);
            if (syms == null || !ReferenceEquals(buf, Buf)) return;
            LspSymbolsStore.Set(path, syms);
            LspSymbolsStore.ApplyTo(buf);
            if (ReferenceEquals(buf, window.ActiveTab)) window.RefreshOutlineAndProblems(buf);
        }

        /// <summary>Requests document symbols now (tests / tab activation).</summary>
        public Task RefreshSymbolsNowAsync() => RefreshSymbolsAsync();
    }
}
