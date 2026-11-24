using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using codeLess.Studio;

namespace codeLess.AvaloniaApp
{
    public class TerminalSessionEntry
    {
        public string Id { get; set; } = Guid.NewGuid().ToString("N");
        public string Title { get; set; } = "pwsh";
        public string ShellCommand { get; set; } = "";
        public AvaloniaTerminalEmulatorControl Control { get; set; }
        /// <summary>Task terminals stay open after their process exits ("Terminal will be reused by tasks").</summary>
        public bool KeepOpenOnExit { get; set; }
        /// <summary>Display label of the task that owns this terminal (null for shells).</summary>
        public string TaskLabel { get; set; }
    }

    public partial class MainWindow : Window
    {
        // Core State & Engine
        private StudioSettings settings;
        private StudioTheme currentTheme;
        private LlmHarnessAndAgentEngine aiEngine;
        private string workspaceRoot = "";
        private readonly List<string> workspaceFiles = new List<string>();
        private readonly List<string> additionalWorkspaceFolders = new List<string>();
        private readonly List<EditorTabBuffer> openTabs = new List<EditorTabBuffer>();
        private readonly Stack<string> closedTabHistory = new Stack<string>();
        private EditorTabBuffer activeTab;
        private bool showWelcomeTab = true;
        private string activeSidebarView = "explorer";
        private string activeBottomTab = "terminal";
        private string currentAgentMode = "agent";
        private bool isZenMode = false;
        private bool isFullScreen = false;
        private bool isBottomDockMaximized = false;
        private double savedBottomDockHeight = 225;
        private DateTime lastCtrlKChordTime = DateTime.MinValue;
        private readonly List<(string FilePath, int Line)> navHistory = new List<(string, int)>();
        private int navHistoryIndex = -1;
        private bool suppressNavRecord = false;
        private string selectedForComparePath = "";
        private CancellationTokenSource workspaceLoadCts;
        private int workspaceLoadGeneration = 0;
        private DispatcherTimer debouncedUiSyncTimer;
        private EditorTabBuffer pendingDebouncedBuffer;
        private EditorTabBuffer lastRenderedTabRef;
        private bool lastRenderedTabDirty = false;
        private bool lastRenderedTabPreview = false;
        private int lastRenderedOpenTabCount = -1;

        // Unsaved Prompt Override for Headless Testing
        public Func<IReadOnlyList<EditorTabBuffer>, UnsavedPromptResult> UnsavedPromptOverride { get; set; }
        public bool SuppressUnsavedPrompts { get; set; } = false;

        // Top Title & Menu Bar
        private Border topMenuBarBorder;
        private Menu mainTopMenu;
        private Button commandCenterBtn;
        private TextBlock commandCenterText;

        // Main Workbench Grid
        private Grid workbenchCenterGrid;
        private Border activityBarBorder;
        private readonly Dictionary<string, (Button Btn, VectorIconControl Icon, Border ActiveBar)> activityBarButtons
            = new Dictionary<string, (Button, VectorIconControl, Border)>(StringComparer.OrdinalIgnoreCase);

        // Primary Sidebar
        private Border sidebarBorder;
        private TextBlock sidebarHeaderTitle;
        private StackPanel sidebarHeaderActions;
        private AvaloniaIndeterminateBar sidebarProgressBar;
        private Grid explorerPanelView;
        private Button openEditorsHeaderBtn;
        private StackPanel openEditorsStack;
        private Button workspaceFolderHeaderBtn;
        public AvaloniaExplorerTree ExplorerTree { get; private set; }
        private Button outlineHeaderBtn;
        private ListBox outlineListBox;
        private Grid searchPanelView;
        private TextBox workspaceSearchInput;
        private TextBox workspaceReplaceInput;
        private bool wsSearchMatchCase = false;
        private bool wsSearchWholeWord = false;
        private bool wsSearchRegex = false;
        private Button wsMatchCaseBtn;
        private Button wsWholeWordBtn;
        private Button wsRegexBtn;
        private TextBlock workspaceSearchSummary;
        private ListBox workspaceSearchResultsList;
        private readonly List<(string FilePath, int LineNumber, string Preview)> workspaceSearchHits = new List<(string, int, string)>();

        private Grid scmPanelView;
        private TextBlock scmBranchBanner;
        private TextBox scmCommitInput;
        private TextBlock scmStagedHeader;
        private StackPanel scmStagedListStack;
        private TextBlock scmChangesHeader;
        private StackPanel scmChangesListStack;
        private List<GitStatusEntry> currentGitEntries = new List<GitStatusEntry>();

        private Grid runDebugPanelView;
        private ComboBox runConfigCombo;
        private TextBlock runStatusSummary;

        private Grid extensionsPanelView;
        private TextBox extensionsSearchInput;
        private StackPanel extensionsCardsStack;

        private Grid harnessPanelView;
        private TextBox harnessSystemPromptInput;
        private TextBox harnessVerifyCmdInput;

        // Center Editor Area & Tab Strip
        private Border tabStripBorder;
        private StackPanel tabStripStack;
        private Grid editorSplitGrid;
        public AvaloniaEditorControl PrimaryEditor { get; private set; }
        private void OnSettingsFileChanged(codeLess.Studio.IdePersistentSettings newSettings)
        {
            Avalonia.Threading.Dispatcher.UIThread.Post(() => {
                this.settings = codeLess.Studio.StudioSettings.Load();
                codeLess.Studio.EditorNicetyOptions.ApplyFrom(this.settings);
                codeLess.AvaloniaApp.AvaloniaVirtualEditorCanvas.DisplayTabSize = System.Math.Max(1, System.Math.Min(16, settings.TabSize <= 0 ? 4 : settings.TabSize));
                codeLess.AvaloniaApp.AvaloniaVirtualEditorCanvas.InsertSpacesSetting = settings.InsertSpaces;
                if (!string.IsNullOrWhiteSpace(settings.ThemeId)) {
                    var newTheme = codeLess.Studio.StudioTheme.GetById(settings.ThemeId);
                    if (newTheme != currentTheme) {
                        currentTheme = newTheme;
                        ApplyTheme(currentTheme);
                    }
                }
                if (PrimaryEditor != null && PrimaryEditor.Canvas != null) {
                    PrimaryEditor.Canvas.FontSizePx = settings.EditorFontSize;
                    PrimaryEditor.Canvas.InvalidateVisual();
                }
                if (SplitEditor != null && SplitEditor.Canvas != null) {
                    SplitEditor.Canvas.FontSizePx = settings.EditorFontSize;
                    SplitEditor.Canvas.InvalidateVisual();
                }
            });
        }

        public float GetEditorFontSizeForTests() { return (float)PrimaryEditor.Canvas.FontSizePx; }
        public bool TestCheckDisposed() {
            return windowClosed && terminalSessions.Count == 0 && workspaceLoadCts?.IsCancellationRequested == true;
        }
        public void TestAddWorkspaceFolder(string path) { workspaceFiles.Add(path); }
        public AvaloniaEditorControl SplitEditor { get; private set; }
        private Border splitEditorBorder;
        private bool isEditorSplit = false;

        // Bottom Panel (Problems / Output / Terminal)
        private Border bottomPanelBorder;
        private Button btmProblemsTabBtn;
        private Button btmOutputTabBtn;
        private Button btmTerminalTabBtn;
        private Button btmDebugConsoleTabBtn;
        private Button btmPortsTabBtn;
        private StackPanel terminalToolbarActions;
        private ComboBox shellProfileSelector;
        private ListBox problemsListBox;
        private TextBox outputStreamBox;
        private ComboBox outputChannelCombo;
        private Grid terminalHostGrid;
        private Grid terminalEmulatorSplitGrid;
        private Border terminalSessionListBorder;
        private StackPanel terminalSessionListStack;
        private readonly List<TerminalSessionEntry> terminalSessions = new List<TerminalSessionEntry>();
        private TerminalSessionEntry activeTerminalSession;
        private TerminalSessionEntry secondarySplitTerminalSession;
        private bool isTerminalSplit = false;

        // Right AI Agent & Composer Dock
        private Border rightChatDockBorder;
        private AvaloniaIndeterminateBar chatProgressBar;
        private Border multiFileChangesetBar;
        private TextBlock changesetSummaryText;
        public AvaloniaAgentFeedControl AgentFeed { get; private set; }
        private WrapPanel contextPillsWrap;
        private readonly List<string> attachedContextPills = new List<string>();
        private TextBox chatPromptInput;
        private readonly Dictionary<string, Button> agentModeButtons = new Dictionary<string, Button>(StringComparer.OrdinalIgnoreCase);
        private ComboBox chatProviderCombo;
        private ComboBox chatModelCombo;
        private Button chatSendBtn;

        // Status Bar
        private Border statusBarBorder;
        private Button statusRemoteBtn;
        private Button statusBranchBtn;
        private Button statusProblemsBtn;
        private TextBlock statusMessageLabel;
        private TextBlock statusChordLabel;
        private Button statusLnColBtn;
        private Button statusSpacesBtn;
        private Button statusEncodingBtn;
        private Button statusEolBtn;
        private Button statusLanguageBtn;
        private Button statusAiModelBtn;

        // Overlays & Modals
        public AvaloniaCommandPaletteOverlay CommandPalette { get; private set; }
        public AvaloniaModalDialogHost ModalHost { get; private set; }

        // Public Accessors for Parity & Headless Verification
        public Menu TopMenuControl => mainTopMenu;
        public IReadOnlyList<string> WorkspaceFiles => workspaceFiles;
        public IReadOnlyList<(string FilePath, int LineNumber, string Preview)> WorkspaceSearchHits => workspaceSearchHits;
        public IReadOnlyList<EditorTabBuffer> OpenTabs => openTabs;
        public EditorTabBuffer ActiveTab => activeTab;
        public string WorkspaceRoot => workspaceRoot;
        public StudioSettings CurrentSettings => settings;
        public StudioTheme CurrentTheme => currentTheme;
        public string ActiveSidebarView => activeSidebarView;
        public string ActiveBottomTab => activeBottomTab;
        public string CurrentAgentMode => currentAgentMode;
        public bool IsSidebarVisible => sidebarBorder.IsVisible;
        public bool IsBottomPanelVisible => bottomPanelBorder.IsVisible;
        public bool IsRightChatVisible => rightChatDockBorder.IsVisible;
        public bool IsEditorSplit => isEditorSplit;
        public bool IsTerminalSplit => isTerminalSplit;
        public bool IsZenMode => isZenMode;
        public bool IsCommandPaletteOpen => CommandPalette.IsVisible;
        public bool IsModalOpen => ModalHost.IsVisible;
        public string ActiveModalId => ModalHost.ActiveDialogId;
        public IReadOnlyList<TerminalSessionEntry> TerminalSessions => terminalSessions;
        public TerminalSessionEntry ActiveTerminalSession => activeTerminalSession;
        public IReadOnlyList<GitStatusEntry> GitEntries => currentGitEntries;
        public string StatusBarLineColText => statusLnColBtn?.Content?.ToString() ?? "";
        public string StatusBarLanguageText => statusLanguageBtn?.Content?.ToString() ?? "";
        public string StatusBarEolText => statusEolBtn?.Content?.ToString() ?? "";
        public string StatusBarEncodingText => statusEncodingBtn?.Content?.ToString() ?? "";
        public string StatusBarBranchText => statusBranchBtn?.Content?.ToString() ?? "";
        public bool IsStatusBarLineColVisible => statusLnColBtn?.IsVisible ?? false;
        public bool IsStatusBarSpacesVisible => statusSpacesBtn?.IsVisible ?? false;
        public bool IsStatusBarEncodingVisible => statusEncodingBtn?.IsVisible ?? false;
        public bool IsStatusBarEolVisible => statusEolBtn?.IsVisible ?? false;
        public bool IsStatusBarLanguageVisible => statusLanguageBtn?.IsVisible ?? false;
        public ListBox OutlineListBoxControl => outlineListBox;
        public Button WorkspaceFolderHeaderButton => workspaceFolderHeaderBtn;
        public string StatusBarChordText => statusChordLabel?.Text ?? "";

        public MainWindow() : this(null) { }

        /// <summary>
        /// UI posts from background work (git, file watcher, indexing, processes) run in the window's own UI execution
        /// context (captured at construction on the UI thread), never in the posting thread's context.
        /// </summary>
        private readonly UiPost mainUiPost = UiPost.CaptureCurrent();
        private UiPost MainUi => mainUiPost;

        public MainWindow(string initialPath)
        {
            Title = "codeLess";
            Width = 1440;
            Height = 900;
            MinWidth = 900;
            MinHeight = 580;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;

            TryLoadWindowIcon();

            settings = StudioSettings.Load();
            InitExtensionHost(); // VSIX themes must be registered before the persisted theme is resolved
            if (string.IsNullOrWhiteSpace(settings.ThemeId))
                settings.ThemeId = StudioTheme.DefaultThemeId;
            currentTheme = StudioTheme.GetById(settings.ThemeId);
            AvaloniaVirtualEditorCanvas.DisplayTabSize = Math.Max(1, Math.Min(16, settings.TabSize <= 0 ? 4 : settings.TabSize));
            AvaloniaVirtualEditorCanvas.InsertSpacesSetting = settings.InsertSpaces;
            EditorNicetyOptions.ApplyFrom(settings);
            aiEngine = new LlmHarnessAndAgentEngine(settings);
            InitNetworkPrivacy();
            
            codeLess.Studio.IdeSettingsWatcher.Start();
            codeLess.Studio.IdeSettingsWatcher.OnSettingsFileChanged += OnSettingsFileChanged;

            // When the user switches away, give back memory freed by editing / terminal bursts (throttled: 1/min).
            Deactivated += (s, e) => ProcessMemoryTrimmer.Trim(compactManagedHeap: true); // user is elsewhere: no visible cost
            aiEngine.OnRequestToolApproval = RequestAgentToolApproval;

            BuildWorkbenchLayout();
            // Preferences: "Show Welcome page on startup" (both UIs); off -> the empty-editor watermark like VS Code.
            showWelcomeTab = settings == null || settings.ShowWelcomeOnStartup;
            PrimaryEditor.WelcomeEnabled = showWelcomeTab;
            PrimaryEditor.Canvas.SnippetKeyHandler = e => OnSnippetKey(PrimaryEditor, e);
            SplitEditor.Canvas.SnippetKeyHandler = e => OnSnippetKey(SplitEditor, e);
            InitializeDevTools();
            InitializeWorkbenchParity();
            InitAgentTrust();
            BuildNativeMacMenu();
            InitLanguageServers();
            InitializeWorkspaceTrust();
            ApplyTheme(currentTheme);
            InitDebugWorkbench();

            // Register tunnel & bubble key handlers so workbench shortcuts work anywhere
            AddHandler(KeyDownEvent, OnWindowKeyDown, RoutingStrategies.Tunnel);
            InstallMenuKeyHooks(); // MainWindow.Menus.cs: Alt+Z / ⌥Z word wrap from anywhere, Option text-input suppression
            InitEditorSwitcher(); // Ctrl+Tab recently-used-editors picker (key-up of Ctrl commits)
            InitUpdates(); // MainWindow.Updates.cs: Check for Updates → Downloading → Restart to Update

            string startPath = initialPath;

            if (!string.IsNullOrWhiteSpace(startPath))
            {
                if (File.Exists(startPath))
                {
                    string dir = Path.GetDirectoryName(Path.GetFullPath(startPath));
                    if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir) && !IsUnsafeDefaultStartupDirectory(dir))
                        LoadWorkspace(dir);
                    OpenFileInTab(Path.GetFullPath(startPath), preview: false);
                }
                else if (Directory.Exists(startPath))
                {
                    LoadWorkspace(Path.GetFullPath(startPath));
                }
            }
            else
            {
                CloseWorkspaceFolder();
            }

            EnsureInitialTerminalSession();
            UpdateWindowTitle();
            UpdateStatusBar();
        }

        private static bool IsHomeOrSystemTempDirectory(string dir)
        {
            try
            {
                string norm = Path.GetFullPath(dir).TrimEnd('\\', '/');
                string home = (Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) ?? "").TrimEnd('\\', '/');
                if (!string.IsNullOrEmpty(home) && string.Equals(norm, home, StringComparison.Ordinal)) return true;
                foreach (string d in new[] { "/home", "/root", "/tmp", "/var/tmp", "/private/tmp" })
                    if (string.Equals(norm, d, StringComparison.Ordinal)) return true;
                string tmp = Path.GetTempPath().TrimEnd('\\', '/');
                return string.Equals(norm, tmp, StringComparison.Ordinal);
            }
            catch { return false; }
        }

        private static bool IsUnsafeDefaultStartupDirectory(string dir)
        {
            if (string.IsNullOrWhiteSpace(dir)) return true;
            try
            {
                string norm = Path.GetFullPath(dir).TrimEnd('\\', '/');
                if (string.IsNullOrEmpty(norm) || norm == "/" || norm == "\\")
                    return true;
                if (norm.IndexOf(".app/Contents", StringComparison.OrdinalIgnoreCase) >= 0)
                    return true;
                if (string.Equals(norm, "/Applications", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(norm, "/System", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(norm, "/Library", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(norm, "/Users", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(norm, "/usr", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(norm, "/bin", StringComparison.OrdinalIgnoreCase))
                    return true;
                string appBase = (AppContext.BaseDirectory ?? "").TrimEnd('\\', '/');
                if (!string.IsNullOrEmpty(appBase) && string.Equals(norm, appBase, StringComparison.OrdinalIgnoreCase) && !File.Exists(Path.Combine(norm, "codeLess.sln")))
                    return true;
                if (IdePersistentSettings.IsInstallerProgramDirectory(norm))
                    return true;
            }
            catch
            {
                return true;
            }
            return false;
        }

        private bool closeConfirmed;

        protected override void OnClosing(WindowClosingEventArgs e)
        {
            // VS Code parity: quitting (Cmd+Q / red close button / Alt+F4) with modified files asks to save first.
            if (!closeConfirmed && !SuppressUnsavedPrompts && openTabs.Any(t => t.IsDirty))
            {
                if (!ConfirmSaveOrDiscardDirtyTabs(openTabs, () => { closeConfirmed = true; Close(); }))
                {
                    e.Cancel = true; // modal shown (or cancelled); the continuation closes after Save / Don't Save
                    return;
                }
                closeConfirmed = true;
            }
            base.OnClosing(e);
        }

        protected override void OnClosed(EventArgs e)
        {
            windowClosed = true; // late background results (analysis) must not touch this window any more
            StopWorkspaceWatcher();
            ShutdownLanguageServers();
            try { workspaceLoadCts?.Cancel(); } catch { }
            if (debouncedUiSyncTimer != null)
                debouncedUiSyncTimer.Stop();
            foreach (var ts in terminalSessions.ToList())
            {
                try { ts.Control?.StopShell(notifyExit: false); } catch { }
            }
            terminalSessions.Clear();
            base.OnClosed(e);
        }

        private void TryLoadWindowIcon()
        {
            try
            {
                string[] candidates = new[]
                {
                    Path.Combine(AppContext.BaseDirectory, "assets", "savatarr_bolt.png"),
                    Path.Combine(AppContext.BaseDirectory, "..", "Resources", "assets", "savatarr_bolt.png"),
                    Path.Combine(Environment.CurrentDirectory, "assets", "savatarr_bolt.png"),
                    Path.Combine(Environment.CurrentDirectory, "codeLess", "assets", "savatarr_bolt.png"),
                    Path.Combine(AppContext.BaseDirectory, "assets", "savatarr_logo_source.png"),
                    Path.Combine(AppContext.BaseDirectory, "..", "Resources", "assets", "savatarr_logo_source.png"),
                    Path.Combine(Environment.CurrentDirectory, "assets", "savatarr_logo_source.png"),
                    Path.Combine(Environment.CurrentDirectory, "codeLess", "assets", "savatarr_logo_source.png")
                };
                string iconPath = candidates.FirstOrDefault(File.Exists);
                if (!string.IsNullOrEmpty(iconPath))
                {
                    Icon = new WindowIcon(iconPath);
                }
            }
            catch { }
        }

        private void BuildWorkbenchLayout()
        {
            var rootOverlayGrid = new Grid();

            var mainShellGrid = new Grid
            {
                RowDefinitions = new RowDefinitions("34,*," + UiTokens.StatusBarHeight)
            };

            // Row 0: Custom VS Code Title & Menu Bar
            topMenuBarBorder = BuildTopTitleAndMenuBar();
            Grid.SetRow(topMenuBarBorder, 0);
            mainShellGrid.Children.Add(topMenuBarBorder);

            // Row 1: Workbench Center (Activity Bar | Sidebar | Splitter | Center Editor+Bottom Dock | Splitter | Right Chat Dock)
            workbenchCenterGrid = new Grid
            {
                ColumnDefinitions = new ColumnDefinitions("48,260,4,*,4,385")
            };

            activityBarBorder = BuildActivityBar();
            Grid.SetColumn(activityBarBorder, 0);
            workbenchCenterGrid.Children.Add(activityBarBorder);

            sidebarBorder = BuildPrimarySidebar();
            Grid.SetColumn(sidebarBorder, 1);
            workbenchCenterGrid.Children.Add(sidebarBorder);

            var leftSplitter = new GridSplitter
            {
                Width = 4,
                Background = Brushes.Transparent,
                ResizeDirection = GridResizeDirection.Columns
            };
            Grid.SetColumn(leftSplitter, 2);
            workbenchCenterGrid.Children.Add(leftSplitter);

            var centerVerticalGrid = BuildCenterEditorAndBottomPanel();
            Grid.SetColumn(centerVerticalGrid, 3);
            workbenchCenterGrid.Children.Add(centerVerticalGrid);

            var rightSplitter = new GridSplitter
            {
                Width = 4,
                Background = Brushes.Transparent,
                ResizeDirection = GridResizeDirection.Columns
            };
            Grid.SetColumn(rightSplitter, 4);
            workbenchCenterGrid.Children.Add(rightSplitter);

            rightChatDockBorder = BuildRightChatDock();
            Grid.SetColumn(rightChatDockBorder, 5);
            workbenchCenterGrid.Children.Add(rightChatDockBorder);

            Grid.SetRow(workbenchCenterGrid, 1);
            mainShellGrid.Children.Add(workbenchCenterGrid);

            // Row 2: Status Bar
            statusBarBorder = BuildStatusBar();
            Grid.SetRow(statusBarBorder, 2);
            mainShellGrid.Children.Add(statusBarBorder);

            rootOverlayGrid.Children.Add(mainShellGrid);

            // Floating Command Palette Overlay
            CommandPalette = new AvaloniaCommandPaletteOverlay();
            CommandPalette.ThemePreviewRequested += previewId =>
            {
                var t = StudioTheme.GetById(previewId);
                if (t != null) ApplyTheme(t, persistSetting: false);
            };
            CommandPalette.GoToLineRequested += lineNum => PrimaryEditor.GoToLine(lineNum);
            CommandPalette.FileOpenRequested += (path, line, col) => OpenFileAtLocation(path, line, Math.Max(1, col), 0, preview: false, focusEditor: true);
            CommandPalette.Closed += () => PrimaryEditor.Canvas.Focus();
            rootOverlayGrid.Children.Add(CommandPalette);

            // Modal Dialog Host
            ModalHost = new AvaloniaModalDialogHost();
            ModalHost.Closed += () => PrimaryEditor.Canvas.Focus();
            rootOverlayGrid.Children.Add(ModalHost);

            Content = rootOverlayGrid;
        }

        private Border BuildTopTitleAndMenuBar()
        {
            var barGrid = new Grid
            {
                ColumnDefinitions = new ColumnDefinitions("Auto,Auto,*,Auto")
            };

            // Left lightning logo
            var logoHost = new Border
            {
                Padding = new Thickness(10, 0, 6, 0),
                VerticalAlignment = VerticalAlignment.Center,
                Child = new VectorIconControl
                {
                    IconName = "logo",
                    IconColor = Color.FromRgb(59, 130, 246),
                    Width = 18,
                    Height = 18
                }
            };
            Grid.SetColumn(logoHost, 0);
            barGrid.Children.Add(logoHost);

            // VS Code Top Menu Bar (File / Edit / Selection / View / Go / Run / Terminal / Help)
            mainTopMenu = BuildTopMenuControl();
            Grid.SetColumn(mainTopMenu, 1);
            barGrid.Children.Add(mainTopMenu);

            // Centered Command Center Search Bar
            var cmdInner = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 8,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
            cmdInner.Children.Add(new VectorIconControl
            {
                IconName = "search",
                Width = 14,
                Height = 14,
                FollowForeground = true,
                VerticalAlignment = VerticalAlignment.Center
            });
            commandCenterText = new TextBlock
            {
                Text = "codeLess",
                FontSize = UiTokens.FontSmall,
                VerticalAlignment = VerticalAlignment.Center
            };
            cmdInner.Children.Add(commandCenterText);

            commandCenterBtn = new Button
            {
                Content = cmdInner,
                MinWidth = 340,
                MaxWidth = 520,
                Height = 24,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalContentAlignment = HorizontalAlignment.Center,
                Padding = new Thickness(14, 0),
                CornerRadius = new CornerRadius(UiTokens.PopupRadius)
            };
            commandCenterBtn.Click += (s, e) => OpenQuickOpen();
            Grid.SetColumn(commandCenterBtn, 2);
            barGrid.Children.Add(commandCenterBtn);

            // Right Layout Toggles
            var rightIcons = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 2,
                Margin = new Thickness(0, 0, 8, 0),
                VerticalAlignment = VerticalAlignment.Center
            };

            Button MakeLayoutBtn(string iconName, string tip, Action onClick)
            {
                var b = WorkbenchStyles.IconButton(iconName, tip, onClick, currentTheme, 26, 16);
                sidebarHeaderIconButtons.Add(b);
                return b;
            }

            rightIcons.Children.Add(MakeLayoutBtn("layout-left", "Toggle Primary Side Bar (Ctrl+B)", TogglePrimarySidebar));
            rightIcons.Children.Add(MakeLayoutBtn("layout-bottom", "Toggle Panel (Ctrl+J)", ToggleBottomPanel));
            rightIcons.Children.Add(MakeLayoutBtn("layout-right", "Toggle Secondary Side Bar / AI Chat (Ctrl+Alt+I)", ToggleRightChatDock));

            if (!RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            {
                Button MakeWinCaptionBtn(string icon, string tip, Action onClick)
                {
                    var b = WorkbenchStyles.IconButton(icon, tip, onClick, currentTheme, 34, 14);
                    b.Height = 30;
                    b.CornerRadius = new CornerRadius(0);
                    sidebarHeaderIconButtons.Add(b);
                    return b;
                }

                rightIcons.Children.Add(new Border { Width = 8 });
                rightIcons.Children.Add(MakeWinCaptionBtn("min", "Minimize", () => WindowState = WindowState.Minimized));
                rightIcons.Children.Add(MakeWinCaptionBtn("max", "Maximize / Restore", () =>
                    WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized));
                rightIcons.Children.Add(MakeWinCaptionBtn("close", "Close (Alt+F4)", Close));
            }

            Grid.SetColumn(rightIcons, 3);
            barGrid.Children.Add(rightIcons);

            return new Border
            {
                Height = 34,
                Child = barGrid,
                BorderThickness = new Thickness(0, 0, 0, 1)
            };
        }

        /// <summary>In-window menu bar (Windows / Linux; hidden on the live macOS desktop): the shared VS Code menu model.</summary>
        private Menu BuildTopMenuControl()
        {
            return BuildModelTopMenu(false);
        }

        private void BuildNativeMacMenu()
        {
            try
            {
                var rootMenu = new NativeMenu();
                bool isLiveMacDesktop = RuntimeInformation.IsOSPlatform(OSPlatform.OSX) &&
                                        Application.Current?.ApplicationLifetime is global::Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime;
                // app menu (About, Check for Updates, Settings ▸, Hide / Hide Others / Show All, Quit), File ... Window, Help
                BuildModelNativeMenu(rootMenu, includeAppMenu: !isLiveMacDesktop);
                if (isLiveMacDesktop && mainTopMenu != null)
                {
                    // On macOS desktop, menus live in the native top system menu bar; hide redundant in-window menu strip
                    mainTopMenu.IsVisible = false;
                }
                NativeMenu.SetMenu(this, rootMenu);
            }
            catch { }
        }
        private Border BuildActivityBar()
        {
            var outerGrid = new Grid
            {
                RowDefinitions = new RowDefinitions("*,Auto")
            };

            var topStack = new StackPanel
            {
                Spacing = 0,
                Margin = new Thickness(0, 0, 0, 0)
            };

            var bottomStack = new StackPanel
            {
                Spacing = 0,
                Margin = new Thickness(0, 0, 0, 4)
            };

            Button AddActivityIcon(StackPanel host, string id, string iconName, string tooltip, Action customAction = null)
            {
                // VS Code activity bar item: 48x48 cell, 24 px glyph, 2 px full-height accent bar when active,
                // no hover fill (the glyph brightens instead).
                var activeBar = new Border
                {
                    Width = 2,
                    HorizontalAlignment = HorizontalAlignment.Left,
                    VerticalAlignment = VerticalAlignment.Stretch,
                    IsVisible = false
                };
                var iconCtrl = new VectorIconControl
                {
                    IconName = iconName,
                    Width = UiTokens.ActivityIconSize,
                    Height = UiTokens.ActivityIconSize,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center
                };
                var cellGrid = new Grid { Width = UiTokens.ActivityBarWidth - 1, Height = UiTokens.ActivityBarWidth };
                cellGrid.Children.Add(activeBar);
                cellGrid.Children.Add(iconCtrl);

                var btn = new Button
                {
                    Content = cellGrid,
                    Width = UiTokens.ActivityBarWidth,
                    Height = UiTokens.ActivityBarWidth,
                    Padding = new Thickness(0),
                    Background = Brushes.Transparent,
                    BorderThickness = new Thickness(0),
                    CornerRadius = new CornerRadius(0)
                };
                btn.Classes.Add("paneltab");
                btn.PointerEntered += (s, e) => { iconCtrl.IconColor = AvaloniaColorHelper.ToAvColor(currentTheme.TextPrimary); iconCtrl.InvalidateVisual(); };
                btn.PointerExited += (s, e) => UpdateActivityBarSelectionVisuals();
                ToolTip.SetTip(btn, AvaloniaCommandPaletteOverlay.FormatShortcutForPlatform(tooltip));
                btn.Click += (s, e) =>
                {
                    if (customAction != null)
                        customAction();
                    else
                        SwitchSidebarView(id);
                };

                activityBarButtons[id] = (btn, iconCtrl, activeBar);
                host.Children.Add(btn);
                return btn;
            }

            AddActivityIcon(topStack, "explorer", "explorer", "Explorer (Ctrl+Shift+E)");
            AddActivityIcon(topStack, "search", "search", "Search Across Files (Ctrl+Shift+F)");
            AddActivityIcon(topStack, "git", "git", "Source Control (Ctrl+Shift+G)");
            AddActivityIcon(topStack, "run", "run", "Run and Debug (Ctrl+Shift+D)");
            AddActivityIcon(topStack, "extensions", "extensions", "Extensions (Ctrl+Shift+X)");
            AddActivityIcon(topStack, "harness", "harness", "AI Harness & BYOK Configuration");

            AddActivityIcon(bottomStack, "account", "account", "Accounts & BYOK Providers", ShowByokAndHarnessModal);
            Button gearBtn = null;
            gearBtn = AddActivityIcon(bottomStack, "settings", "gear", "Manage / Settings (Ctrl+,)", () => OpenGearSettingsMenu(gearBtn));

            Grid.SetRow(topStack, 0);
            outerGrid.Children.Add(topStack);
            Grid.SetRow(bottomStack, 1);
            outerGrid.Children.Add(bottomStack);

            return new Border
            {
                Width = 48,
                Child = outerGrid,
                BorderThickness = new Thickness(0, 0, 1, 0)
            };
        }

        private void OpenGearSettingsMenu(Control anchor)
        {
            string Sc(string k) => AvaloniaCommandPaletteOverlay.FormatShortcutForPlatform(k);
            var menu = new ContextMenu
            {
                ItemsSource = new object[]
                {
                    MakeCtxItem($"Command Palette...   ({Sc("Ctrl+Shift+P")})", () => OpenCommandPalette(">")),
                    new Separator(),
                    MakeCtxItem($"Settings   ({Sc("Ctrl+,")})", ShowSettingsModal),
                    MakeCtxItem($"Keyboard Shortcuts   ({Sc("Ctrl+K Ctrl+S")})", ShowKeyboardShortcutsModal),
                    MakeCtxItem($"Color Theme   ({Sc("Ctrl+K Ctrl+T")})", OpenThemePickerPalette),
                    new Separator(),
                    MakeCtxItem("BYOK AI Providers & Keys...", ShowByokAndHarnessModal),
                    new Separator(),
                    MakeUpdateGearItem(), // VS Code: Check for Updates... / Restart to Update in the Manage menu
                    MakeCtxItem("About codeLess", ShowAboutModal)
                }
            };
            menu.Open(anchor);
        }

        private static MenuItem MakeCtxItem(string header, Action onClick)
        {
            var mi = new MenuItem { Header = new LiteralTextBlock(header) };
            if (onClick != null)
                mi.Click += (s, e) => onClick();
            return mi;
        }
    }
}

namespace codeLess.AvaloniaApp
{
    public partial class MainWindow
    {
        private Border BuildPrimarySidebar()
        {
            var sidebarGrid = new Grid
            {
                RowDefinitions = new RowDefinitions(UiTokens.ViewHeaderHeight + ",2,*")
            };

            // Row 0: Sidebar Header + Action Icons
            var headerGrid = new Grid
            {
                ColumnDefinitions = new ColumnDefinitions("*,Auto"),
                Margin = new Thickness(20, 0, 8, 0)
            };
            sidebarHeaderTitle = new TextBlock
            {
                Text = "EXPLORER",
                FontSize = UiTokens.FontHeader,
                FontWeight = FontWeight.Normal,
                VerticalAlignment = VerticalAlignment.Center
            };
            sidebarHeaderActions = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 2,
                VerticalAlignment = VerticalAlignment.Center
            };

            Button MakeHeaderAction(string iconName, string tooltip, Action onClick)
            {
                var b = WorkbenchStyles.IconButton(iconName, tooltip, onClick, currentTheme);
                sidebarHeaderIconButtons.Add(b);
                return b;
            }

            sidebarHeaderActions.Children.Add(MakeHeaderAction("new-file", "New File...", PromptCreateNewFileInWorkspace));
            sidebarHeaderActions.Children.Add(MakeHeaderAction("new-folder", "New Folder...", PromptCreateNewFolderInWorkspace));
            sidebarHeaderActions.Children.Add(MakeHeaderAction("refresh", "Refresh Explorer", () => { if (!string.IsNullOrEmpty(workspaceRoot)) RefreshWorkspaceAfterFileOperation(); }));
            sidebarHeaderActions.Children.Add(MakeHeaderAction("collapse-all", "Collapse Folders in Explorer", () => ExplorerTree?.CollapseAllFolders()));

            headerGrid.Children.Add(sidebarHeaderTitle);
            Grid.SetColumn(sidebarHeaderActions, 1);
            headerGrid.Children.Add(sidebarHeaderActions);
            Grid.SetRow(headerGrid, 0);
            sidebarGrid.Children.Add(headerGrid);

            // Row 1: Indeterminate Progress Bar
            sidebarProgressBar = new AvaloniaIndeterminateBar();
            Grid.SetRow(sidebarProgressBar, 1);
            sidebarGrid.Children.Add(sidebarProgressBar);

            // Row 2: View Deck (Explorer, Search, SCM, Run, Extensions, Harness)
            var deckGrid = new Grid();
            explorerPanelView = BuildExplorerSidebarView();
            searchPanelView = BuildSearchSidebarView();
            scmPanelView = BuildScmSidebarView();
            runDebugPanelView = BuildDebugSidebarView();
            extensionsPanelView = BuildExtensionsSidebarView();
            harnessPanelView = BuildHarnessSidebarView();

            deckGrid.Children.Add(explorerPanelView);
            deckGrid.Children.Add(searchPanelView);
            deckGrid.Children.Add(scmPanelView);
            deckGrid.Children.Add(runDebugPanelView);
            deckGrid.Children.Add(extensionsPanelView);
            deckGrid.Children.Add(harnessPanelView);

            Grid.SetRow(deckGrid, 2);
            sidebarGrid.Children.Add(deckGrid);

            return new Border
            {
                Child = sidebarGrid,
                BorderThickness = new Thickness(0, 0, 1, 0)
            };
        }

        private Grid BuildExplorerSidebarView()
        {
            var grid = new Grid
            {
                RowDefinitions = new RowDefinitions("Auto,Auto,Auto,*,Auto,Auto")
            };

            // Section 1: OPEN EDITORS
            openEditorsHeaderBtn = new Button
            {
                Content = new SectionHeaderLabel("▾ OPEN EDITORS"),
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Left,
                VerticalContentAlignment = VerticalAlignment.Center,
                Padding = new Thickness(4, 0),
                Height = UiTokens.SectionHeaderHeight,
                MinHeight = 0,
                CornerRadius = new CornerRadius(0),
                Background = Brushes.Transparent
            };
            openEditorsStack = new StackPanel { Spacing = 1, Margin = new Thickness(0, 0, 0, 4) };
            openEditorsHeaderBtn.Click += (s, e) =>
            {
                openEditorsStack.IsVisible = !openEditorsStack.IsVisible;
                openEditorsHeaderBtn.Content = new SectionHeaderLabel((openEditorsStack.IsVisible ? "▾ " : "▸ ") + $"OPEN EDITORS ({openTabs.Count})");
            };
            Grid.SetRow(openEditorsHeaderBtn, 0);
            grid.Children.Add(openEditorsHeaderBtn);
            Grid.SetRow(openEditorsStack, 1);
            grid.Children.Add(openEditorsStack);

            // Section 2: WORKSPACE FOLDER HEADER
            workspaceFolderHeaderBtn = new Button
            {
                Content = new SectionHeaderLabel("▾ WORKSPACE"),
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Left,
                VerticalContentAlignment = VerticalAlignment.Center,
                Padding = new Thickness(4, 0),
                Height = UiTokens.SectionHeaderHeight,
                MinHeight = 0,
                CornerRadius = new CornerRadius(0),
                Background = Brushes.Transparent
            };
            Grid.SetRow(workspaceFolderHeaderBtn, 2);
            grid.Children.Add(workspaceFolderHeaderBtn);

            // Section 3: AvaloniaExplorerTree (22px rows, chevrons, file-type icons, indent guides, context menu)
            ExplorerTree = new AvaloniaExplorerTree();
            ExplorerTree.BeforeExpand += EnsureDirectoryNodePopulated;
            ExplorerTree.NodeActivated += (node, isDouble) =>
            {
                if (node != null && !node.IsDirectory && File.Exists(node.Tag))
                {
                    OpenFileInTab(node.Tag, preview: !isDouble);
                }
            };
            ExplorerTree.NodeContextMenuRequested += OpenExplorerNodeContextMenu;
            ExplorerTree.RenameRequested += PromptRenameExplorerNode;
            ExplorerTree.DeleteRequested += DeleteExplorerNodeToTrash;
            Grid.SetRow(ExplorerTree, 3);
            grid.Children.Add(ExplorerTree);

            // Section 4: OUTLINE (Bottom collapsible symbol list)
            outlineHeaderBtn = new Button
            {
                Content = new SectionHeaderLabel("▸ OUTLINE"),
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Left,
                VerticalContentAlignment = VerticalAlignment.Center,
                Padding = new Thickness(4, 0),
                Height = UiTokens.SectionHeaderHeight,
                MinHeight = 0,
                CornerRadius = new CornerRadius(0),
                Background = Brushes.Transparent
            };
            outlineListBox = new ListBox
            {
                Height = 125,
                IsVisible = false,
                BorderThickness = new Thickness(0),
                Background = Brushes.Transparent
            };
            outlineHeaderBtn.Click += (s, e) =>
            {
                outlineListBox.IsVisible = !outlineListBox.IsVisible;
                outlineHeaderBtn.Content = new SectionHeaderLabel((outlineListBox.IsVisible ? "▾ " : "▸ ") + "OUTLINE");
            };
            outlineListBox.SelectionChanged += (s, e) =>
            {
                int idx = outlineListBox.SelectedIndex;
                if (activeTab?.Symbols != null && idx >= 0 && idx < activeTab.Symbols.Count)
                {
                    PrimaryEditor.GoToLine(activeTab.Symbols[idx].LineNumber);
                }
            };
            Grid.SetRow(outlineHeaderBtn, 4);
            grid.Children.Add(outlineHeaderBtn);
            Grid.SetRow(outlineListBox, 5);
            grid.Children.Add(outlineListBox);

            return grid;
        }

        private Grid BuildScmSidebarView()
        {
            var grid = new Grid
            {
                RowDefinitions = new RowDefinitions("Auto,*"),
                Margin = new Thickness(8, 6),
                IsVisible = false
            };

            var topStack = new StackPanel { Spacing = 6 };
            scmBranchBanner = new TextBlock { Text = "Branch: main", FontSize = UiTokens.FontSmall, FontWeight = FontWeight.Normal, Margin = new Thickness(2, 0, 0, 0) };
            scmCommitInput = new TextBox
            {
                Watermark = "Message (Ctrl+Enter to commit on 'main')",
                AcceptsReturn = true,
                TextWrapping = TextWrapping.Wrap,
                Height = 54,
                FontSize = UiTokens.FontUi,
                Padding = new Thickness(8, 5)
            };

            var btnRow = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto,Auto") };
            var commitBtn = new Button
            {
                Content = new IconLabel("check", "Commit", 14, UiTokens.FontUi) { HorizontalAlignment = HorizontalAlignment.Center },
                FontSize = UiTokens.FontUi,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Center,
                Padding = new Thickness(8, 3),
                MinHeight = UiTokens.ControlHeight
            };
            commitBtn.Classes.Add("primary");
            scmCommitButton = commitBtn;
            var stageAllBtn = WorkbenchStyles.IconButton("plus", "Stage All Changes", null, currentTheme, 26, 15);
            stageAllBtn.Margin = new Thickness(4, 0, 0, 0);
            var unstageAllBtn = WorkbenchStyles.IconButton("minus", "Unstage All Changes", null, currentTheme, 26, 15);
            unstageAllBtn.Margin = new Thickness(2, 0, 0, 0);
            sidebarHeaderIconButtons.Add(stageAllBtn);
            sidebarHeaderIconButtons.Add(unstageAllBtn);
            var refreshGitBtn = WorkbenchStyles.IconButton("refresh", "Refresh", null, currentTheme, 26, 15);
            refreshGitBtn.Margin = new Thickness(4, 0, 0, 0);

            commitBtn.Click += (s, e) => ExecuteGitCommit();
            stageAllBtn.Click += (s, e) => ExecuteGitStageAll();
            unstageAllBtn.Click += (s, e) => ExecuteGitUnstageAll();
            refreshGitBtn.Click += (s, e) => RefreshGitStatus();

            btnRow.Children.Add(commitBtn);
            Grid.SetColumn(stageAllBtn, 1); btnRow.Children.Add(stageAllBtn);
            Grid.SetColumn(unstageAllBtn, 2); btnRow.Children.Add(unstageAllBtn);
            Grid.SetColumn(refreshGitBtn, 3); btnRow.Children.Add(refreshGitBtn);

            topStack.Children.Add(scmBranchBanner);
            topStack.Children.Add(scmCommitInput);
            topStack.Children.Add(btnRow);
            Grid.SetRow(topStack, 0);
            grid.Children.Add(topStack);

            var listsStack = new StackPanel { Spacing = 6, Margin = new Thickness(0, 8, 0, 0) };
            scmStagedHeader = new TextBlock { Text = "▾ STAGED CHANGES (0)", FontSize = UiTokens.FontHeader, FontWeight = FontWeight.SemiBold, Margin = new Thickness(2, 0, 0, 0) };
            scmStagedListStack = new StackPanel { Spacing = 2 };
            scmChangesHeader = new TextBlock { Text = "▾ CHANGES (0)", FontSize = UiTokens.FontHeader, FontWeight = FontWeight.SemiBold, Margin = new Thickness(2, 6, 0, 0) };
            scmChangesListStack = new StackPanel { Spacing = 2 };

            listsStack.Children.Add(scmStagedHeader);
            listsStack.Children.Add(scmStagedListStack);
            listsStack.Children.Add(scmChangesHeader);
            listsStack.Children.Add(scmChangesListStack);

            var scmScroll = new ScrollViewer { Content = listsStack };
            Grid.SetRow(scmScroll, 1);
            grid.Children.Add(scmScroll);
            return grid;
        }

        private Grid BuildExtensionsSidebarView() => BuildExtensionsSidebarViewReal(); // MainWindow.Extensions.cs

        private Grid BuildHarnessSidebarView()
        {
            var grid = new Grid
            {
                RowDefinitions = new RowDefinitions("Auto,*"),
                Margin = new Thickness(10, 8),
                IsVisible = false
            };

            var stack = new StackPanel { Spacing = 8 };
            stack.Children.Add(new TextBlock { Text = "SYSTEM PROMPT & HARNESS RULES", FontSize = 10.5, FontWeight = FontWeight.Bold });
            harnessSystemPromptInput = new TextBox
            {
                Text = "You are the codeLess Principal Engineering Agent. Produce clean, verified code diffs.",
                AcceptsReturn = true,
                Height = 90,
                FontSize = 11.5
            };
            stack.Children.Add(harnessSystemPromptInput);

            stack.Children.Add(new TextBlock { Text = "AUTO-VERIFICATION SHELL COMMAND", FontSize = 10.5, FontWeight = FontWeight.Bold });
            harnessVerifyCmdInput = new TextBox
            {
                Text = "dotnet build --nologo",
                FontSize = 11.5
            };
            stack.Children.Add(harnessVerifyCmdInput);

            var openByokBtn = new Button
            {
                Content = "⋯ Configure BYOK Providers & API Keys...",
                FontSize = 11.5,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                Padding = new Thickness(10, 6)
            };
            openByokBtn.Click += (s, e) => ShowByokAndHarnessModal();
            stack.Children.Add(openByokBtn);

            Grid.SetRow(stack, 0);
            grid.Children.Add(stack);
            return grid;
        }

        private Grid BuildCenterEditorAndBottomPanel()
        {
            var centerGrid = new Grid
            {
                RowDefinitions = new RowDefinitions("35,*,4,225")
            };

            // Row 0: Tab Strip + Right Actions (Split Editor Right)
            var tabBarGrid = new Grid
            {
                ColumnDefinitions = new ColumnDefinitions("*,Auto")
            };
            tabStripStack = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 0
            };
            var tabScroll = new ScrollViewer
            {
                Content = tabStripStack,
                HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
                VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled
            };
            Grid.SetColumn(tabScroll, 0);
            tabBarGrid.Children.Add(tabScroll);

            var tabRightActions = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 2,
                Margin = new Thickness(4, 0, 8, 0),
                VerticalAlignment = VerticalAlignment.Center
            };
            var splitBtn = WorkbenchStyles.IconButton("split-right", AvaloniaCommandPaletteOverlay.FormatShortcutForPlatform("Split Editor Right (Ctrl+\\)"), ToggleSplitEditor, currentTheme);
            sidebarHeaderIconButtons.Add(splitBtn);
            tabRightActions.Children.Add(splitBtn);
            Grid.SetColumn(tabRightActions, 1);
            tabBarGrid.Children.Add(tabRightActions);
            // 1 px divider under the tab row; inactive tabs repeat it, the active tab covers it so it merges into
            // the editor (VS Code).
            tabStripBottomLine = new Border { Height = UiTokens.DividerWidth, VerticalAlignment = VerticalAlignment.Bottom, IsHitTestVisible = false };
            Grid.SetColumnSpan(tabStripBottomLine, 2);
            tabBarGrid.Children.Insert(0, tabStripBottomLine);

            tabStripBorder = new Border
            {
                Height = UiTokens.TabHeight,
                Child = tabBarGrid,
                BorderThickness = new Thickness(0)
            };
            Grid.SetRow(tabStripBorder, 0);
            centerGrid.Children.Add(tabStripBorder);

            // Row 1: Editor Split Grid (Primary Editor | Secondary Split Editor)
            editorSplitGrid = new Grid
            {
                ColumnDefinitions = new ColumnDefinitions("*,0")
            };

            PrimaryEditor = new AvaloniaEditorControl();
            PrimaryEditor.BufferModified += OnEditorBufferModified;
            PrimaryEditor.CaretMoved += buf => UpdateStatusBar();
            PrimaryEditor.InlineAiEditRequested += ExecuteInlineAiEdit;
            PrimaryEditor.WelcomeActionRequested += HandleWelcomeAction;
            PrimaryEditor.OpenRecentWorkspaceRequested += dir => { if (Directory.Exists(dir)) LoadWorkspace(dir); };
            PrimaryEditor.EditorContextMenuRequested += OpenEditorContextMenu;
            PrimaryEditor.DiffAllDecisionRequested += (buf, accepted) => RefreshChangesetBanner();
            PrimaryEditor.LinkActivated += (buf, target, line) => FollowEditorLinkWithLsp(PrimaryEditor, buf, target, line);
            PrimaryEditor.NavigateHistoryRequested += dir => { if (dir < 0) NavigateBack(); else NavigateForward(); };
            Grid.SetColumn(PrimaryEditor, 0);
            editorSplitGrid.Children.Add(PrimaryEditor);

            SplitEditor = new AvaloniaEditorControl();
            SplitEditor.BufferModified += OnEditorBufferModified;
            SplitEditor.CaretMoved += buf => UpdateStatusBar();
            SplitEditor.LinkActivated += (buf, target, line) => FollowEditorLinkWithLsp(SplitEditor, buf, target, line);
            SplitEditor.NavigateHistoryRequested += dir => { if (dir < 0) NavigateBack(); else NavigateForward(); };
            splitEditorBorder = new Border
            {
                Child = SplitEditor,
                BorderThickness = new Thickness(1, 0, 0, 0),
                IsVisible = false
            };
            Grid.SetColumn(splitEditorBorder, 1);
            editorSplitGrid.Children.Add(splitEditorBorder);

            var editorWithTrustBanner = WrapWithRestrictedModeBanner(editorSplitGrid);
            Grid.SetRow(editorWithTrustBanner, 1);
            centerGrid.Children.Add(editorWithTrustBanner);

            // Row 2: Horizontal Splitter
            var btmSplitter = new GridSplitter
            {
                Height = 4,
                Background = Brushes.Transparent,
                ResizeDirection = GridResizeDirection.Rows
            };
            Grid.SetRow(btmSplitter, 2);
            centerGrid.Children.Add(btmSplitter);

            // Row 3: Bottom Panel (PROBLEMS / OUTPUT / TERMINAL)
            bottomPanelBorder = BuildBottomPanel();
            Grid.SetRow(bottomPanelBorder, 3);
            centerGrid.Children.Add(bottomPanelBorder);

            return centerGrid;
        }

        private Border BuildBottomPanel()
        {
            var grid = new Grid
            {
                RowDefinitions = new RowDefinitions(UiTokens.PanelHeaderHeight + ",*")
            };

            // Row 0: Panel Tab Header + Right Terminal Actions
            var headerGrid = new Grid
            {
                ColumnDefinitions = new ColumnDefinitions("*,Auto"),
                Margin = new Thickness(8, 0, 8, 0)
            };

            var tabsRow = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 4,
                VerticalAlignment = VerticalAlignment.Stretch
            };

            Button MakePanelTab(string label, string id, bool visibleByDefault = true)
            {
                var b = new Button
                {
                    Content = label,
                    FontSize = UiTokens.FontHeader,
                    FontWeight = FontWeight.Normal,
                    Padding = new Thickness(0),
                    Margin = new Thickness(8, 0), // VS Code: the 1 px active underline spans the label only
                    MinHeight = 0,
                    VerticalAlignment = VerticalAlignment.Stretch,
                    VerticalContentAlignment = VerticalAlignment.Center,
                    Background = Brushes.Transparent,
                    CornerRadius = new CornerRadius(0),
                    BorderThickness = new Thickness(0, 0, 0, UiTokens.PanelTabIndicatorWidth),
                    IsVisible = visibleByDefault,
                    Focusable = false
                };
                b.Classes.Add("paneltab");
                b.Name = "PanelTab_" + id;
                b.Click += (s, e) => { SwitchBottomTab(id); FocusBottomTabContent(id); };
                return b;
            }

            // VS Code order: PROBLEMS, OUTPUT, DEBUG CONSOLE, TERMINAL (PORTS only when shown explicitly).
            btmProblemsTabBtn = MakePanelTab("PROBLEMS (0)", "problems");
            btmOutputTabBtn = MakePanelTab("OUTPUT", "output");
            btmDebugConsoleTabBtn = MakePanelTab("DEBUG CONSOLE", "debug");
            btmTerminalTabBtn = MakePanelTab("TERMINAL", "terminal");
            btmPortsTabBtn = MakePanelTab("PORTS", "ports", visibleByDefault: false);

            tabsRow.Children.Add(btmProblemsTabBtn);
            tabsRow.Children.Add(btmOutputTabBtn);
            tabsRow.Children.Add(btmDebugConsoleTabBtn);
            tabsRow.Children.Add(btmTerminalTabBtn);
            tabsRow.Children.Add(btmPortsTabBtn);
            headerGrid.Children.Add(tabsRow);

            // Right-side Terminal & Panel Actions
            terminalToolbarActions = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 2,
                VerticalAlignment = VerticalAlignment.Center
            };

            string[] profiles = RuntimeInformation.IsOSPlatform(OSPlatform.OSX)
                ? new[] { "zsh", "bash", "pwsh" }
                : (RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
                    ? new[] { "pwsh", "powershell", "cmd", "bash" }
                    : new[] { "bash", "zsh", "sh", "pwsh" });
            shellProfileSelector = new ComboBox
            {
                ItemsSource = profiles,
                SelectedIndex = 0,
                Height = UiTokens.IconButtonSize,
                MinHeight = UiTokens.IconButtonSize,
                FontSize = UiTokens.FontSmall,
                Padding = new Thickness(6, 0, 0, 0),
                Margin = new Thickness(0, 0, 4, 0),
                BorderThickness = new Thickness(0),
                Background = Brushes.Transparent,
                VerticalAlignment = VerticalAlignment.Center
            };
            ToolTip.SetTip(shellProfileSelector, "Shell profile for new terminals");

            var newTermBtn = WorkbenchStyles.IconButton("plus", "New Terminal (Ctrl+Shift+`)", () =>
            {
                string prof = shellProfileSelector.SelectedItem?.ToString();
                RequestNewTerminal(prof);
            }, currentTheme);
            newTermBtn.Name = "NewTerminalBtn";
            var splitTermBtn = WorkbenchStyles.IconButton("split-right", "Split Terminal (Ctrl+Shift+5)", ToggleSplitTerminal, currentTheme);
            splitTermBtn.Name = "SplitTerminalBtn";
            var renameTermBtn = WorkbenchStyles.IconButton("pencil", "Rename Active Terminal Session...", PromptRenameActiveTerminalSession, currentTheme);
            renameTermBtn.Name = "RenameTerminalBtn";
            var killTermBtn = WorkbenchStyles.IconButton("trash", "Kill Active Terminal Session", () => { if (activeTerminalSession != null) KillTerminalSession(activeTerminalSession); }, currentTheme);
            killTermBtn.Name = "KillTerminalBtn";
            var clearPanelBtn = WorkbenchStyles.IconButton("clear-all", "Clear", ClearActiveBottomTabContent, currentTheme);
            clearPanelBtn.Name = "ClearPanelBtn";
            clearPanelBtn.IsVisible = false;
            var maxPanelBtn = WorkbenchStyles.IconButton("chevron-up", "Maximize Panel Size", ToggleMaximizeBottomPanel, currentTheme);
            var closePanelBtn = WorkbenchStyles.IconButton("close", "Close Panel (Ctrl+J)", ToggleBottomPanel, currentTheme);
            panelToolbarIconButtons = new[] { newTermBtn, splitTermBtn, renameTermBtn, killTermBtn, maxPanelBtn, closePanelBtn, clearPanelBtn };
            terminalOnlyPanelButtons = new[] { newTermBtn, splitTermBtn, renameTermBtn, killTermBtn };
            panelClearButton = clearPanelBtn;

            outputChannelCombo = new ComboBox
            {
                ItemsSource = new[] { "codeLess Workbench", "AI Harness Engine", "Git Source Control", "Extension Host" },
                SelectedIndex = 0,
                Height = UiTokens.IconButtonSize,
                MinHeight = UiTokens.IconButtonSize,
                FontSize = UiTokens.FontSmall,
                Margin = new Thickness(0, 0, 6, 0),
                VerticalAlignment = VerticalAlignment.Center,
                IsVisible = false
            };

            terminalToolbarActions.Children.Add(outputChannelCombo);
            terminalToolbarActions.Children.Add(shellProfileSelector);
            terminalToolbarActions.Children.Add(newTermBtn);
            terminalToolbarActions.Children.Add(splitTermBtn);
            terminalToolbarActions.Children.Add(renameTermBtn);
            terminalToolbarActions.Children.Add(killTermBtn);
            terminalToolbarActions.Children.Add(clearPanelBtn);
            terminalToolbarActions.Children.Add(maxPanelBtn);
            terminalToolbarActions.Children.Add(closePanelBtn);

            Grid.SetColumn(terminalToolbarActions, 1);
            headerGrid.Children.Add(terminalToolbarActions);
            Grid.SetRow(headerGrid, 0);
            grid.Children.Add(headerGrid);

            // Row 1: Bottom Panel Body Deck
            var bodyDeck = new Grid();

            // Problems List
            problemsListBox = new ListBox
            {
                IsVisible = false,
                BorderThickness = new Thickness(0),
                Background = Brushes.Transparent
            };
            problemsListBox.SelectionChanged += (s, e) =>
            {
                int idx = problemsListBox.SelectedIndex;
                if (activeTab?.Diagnostics != null && idx >= 0 && idx < activeTab.Diagnostics.Count)
                {
                    PrimaryEditor.GoToLine(activeTab.Diagnostics[idx].LineNumber);
                }
                else if (idx >= 0) TryNavigateTaskProblem(idx - (activeTab?.Diagnostics?.Count ?? 0));
            };
            bodyDeck.Children.Add(problemsListBox);

            // Output Stream Box
            outputStreamBox = new TextBox
            {
                IsReadOnly = true,
                AcceptsReturn = true,
                FontFamily = new FontFamily("Cascadia Code, Consolas, Menlo, monospace"),
                FontSize = 12,
                BorderThickness = new Thickness(0),
                Padding = new Thickness(12, 6),
                Text = $"[codeLess] Workbench initialized ({RuntimeInformation.OSDescription}, {RuntimeInformation.ProcessArchitecture}).\n",
                IsVisible = false
            };
            bodyDeck.Children.Add(outputStreamBox);
            bodyDeck.Children.Add(BuildDebugConsolePanel());

            // Terminal Host Grid: Left VT100 Emulator(s) | Right Session List (160px)
            terminalHostGrid = new Grid
            {
                ColumnDefinitions = new ColumnDefinitions("*,160")
            };
            terminalEmulatorSplitGrid = new Grid
            {
                ColumnDefinitions = new ColumnDefinitions("*,0")
            };
            Grid.SetColumn(terminalEmulatorSplitGrid, 0);
            terminalHostGrid.Children.Add(terminalEmulatorSplitGrid);

            terminalSessionListStack = new StackPanel
            {
                Spacing = 0,
                Margin = new Thickness(0, 2)
            };
            terminalSessionListBorder = new Border
            {
                Child = new ScrollViewer { Content = terminalSessionListStack },
                BorderThickness = new Thickness(1, 0, 0, 0)
            };
            Grid.SetColumn(terminalSessionListBorder, 1);
            terminalHostGrid.Children.Add(terminalSessionListBorder);

            bodyDeck.Children.Add(terminalHostGrid);

            Grid.SetRow(bodyDeck, 1);
            grid.Children.Add(bodyDeck);

            return new Border
            {
                Child = grid,
                BorderThickness = new Thickness(0, 1, 0, 0)
            };
        }

        private Button[] panelToolbarIconButtons = new Button[0];
        private readonly List<Button> sidebarHeaderIconButtons = new List<Button>();
        private Button[] chatHeaderIconButtons = new Button[0];
        private Button[] changesetButtons = new Button[0];
        private IconLabel chatHeaderTitle;
        private Button scmCommitButton;
        private Border tabStripBottomLine;
        private Border composerBorder;

        private void ApplyComposerTheme()
        {
            if (currentTheme == null || composerBorder == null) return;
            var t = currentTheme;
            composerBorder.Background = WorkbenchStyles.B(UiTokens.InputBackground(t));
            composerBorder.BorderBrush = WorkbenchStyles.B(chatPromptInput != null && chatPromptInput.IsFocused ? UiTokens.FocusBorder(t) : UiTokens.InputBorder(t));
        }

        private void ApplyChatDockChromeTheme(StudioTheme t)
        {
            if (t == null) return;
            if (chatHeaderTitle != null) { chatHeaderTitle.Label.Foreground = WorkbenchStyles.B(t.FgSecondary); chatHeaderTitle.Icon.FollowForeground = false; chatHeaderTitle.Icon.IconColor = WorkbenchStyles.C(t.AccentPrimary); chatHeaderTitle.Icon.InvalidateVisual(); }
            foreach (var b in chatHeaderIconButtons) WorkbenchStyles.ThemeIconButton(b, t);
            if (changesetButtons.Length == 2) { WorkbenchStyles.StylePrimaryButton(changesetButtons[0], t); WorkbenchStyles.StyleSecondaryButton(changesetButtons[1], t); }
            if (chatSendBtn != null) { chatSendBtn.Foreground = WorkbenchStyles.B(t.AccentPrimary); if (chatSendBtn.Content is VectorIconControl sg) { sg.IconColor = WorkbenchStyles.C(t.AccentPrimary); sg.InvalidateVisual(); } }
            foreach (var c in new[] { chatProviderCombo, chatModelCombo })
                if (c != null) { c.Foreground = WorkbenchStyles.B(t.FgSecondary); }
            ApplyComposerTheme();
        }

        private Border BuildRightChatDock()
        {
            var grid = new Grid
            {
                RowDefinitions = new RowDefinitions(UiTokens.ViewHeaderHeight + ",2,Auto,*,Auto")
            };

            // Row 0: Chat Header (New, History, and Close — matching WinForms)
            var headerGrid = new Grid
            {
                ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto,Auto"),
                Margin = new Thickness(12, 0, 8, 0)
            };
            chatHeaderTitle = new IconLabel("sparkle", "AI AGENT & COMPOSER", 13, UiTokens.FontHeader, "✦ ") { Spacing = 6 };
            chatHeaderTitle.Label.FontWeight = FontWeight.Normal;
            headerGrid.Children.Add(chatHeaderTitle);

            var newChatBtn = WorkbenchStyles.IconButton("plus", "New Chat", () => AgentFeed.ClearFeed(), currentTheme);
            Grid.SetColumn(newChatBtn, 1);
            headerGrid.Children.Add(newChatBtn);

            var historyChatBtn = WorkbenchStyles.IconButton("history", "Agent Timeline (Undo / Redo turns)", () => ShowAgentTimelineModal(), currentTheme);
            Grid.SetColumn(historyChatBtn, 2);
            headerGrid.Children.Add(historyChatBtn);

            var closeChatBtn = WorkbenchStyles.IconButton("close", "Close AI Chat (Ctrl+Alt+I)", ToggleRightChatDock, currentTheme);
            Grid.SetColumn(closeChatBtn, 3);
            headerGrid.Children.Add(closeChatBtn);
            chatHeaderIconButtons = new[] { newChatBtn, historyChatBtn, closeChatBtn };

            Grid.SetRow(headerGrid, 0);
            grid.Children.Add(headerGrid);

            // Row 1: Chat Indeterminate Progress Bar
            chatProgressBar = new AvaloniaIndeterminateBar();
            Grid.SetRow(chatProgressBar, 1);
            grid.Children.Add(chatProgressBar);

            // Row 2: Multi-file Changeset Review Banner
            var csGrid = new Grid
            {
                ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto"),
                Margin = new Thickness(12, 5, 10, 5)
            };
            changesetSummaryText = new TextBlock
            {
                Text = "1 file pending",
                FontSize = UiTokens.FontSmall,
                FontWeight = FontWeight.Normal,
                TextTrimming = TextTrimming.CharacterEllipsis,
                VerticalAlignment = VerticalAlignment.Center
            };
            var csAcceptBtn = new Button
            {
                Content = "Accept All",
                FontSize = UiTokens.FontSmall,
                Padding = new Thickness(8, 2),
                MinHeight = 22,
                Margin = new Thickness(4, 0)
            };
            csAcceptBtn.Classes.Add("primary");
            var csRejectBtn = new Button
            {
                Content = "Reject All",
                FontSize = UiTokens.FontSmall,
                Padding = new Thickness(8, 2),
                MinHeight = 22
            };
            changesetButtons = new[] { csAcceptBtn, csRejectBtn };
            csAcceptBtn.Click += (s, e) => OnChangesetAcceptAll();
            csRejectBtn.Click += (s, e) => OnChangesetRejectAll();
            csGrid.Children.Add(changesetSummaryText);
            Grid.SetColumn(csAcceptBtn, 1); csGrid.Children.Add(csAcceptBtn);
            Grid.SetColumn(csRejectBtn, 2); csGrid.Children.Add(csRejectBtn);

            multiFileChangesetBar = new Border
            {
                Child = csGrid,
                Margin = new Thickness(0),
                CornerRadius = new CornerRadius(0),
                BorderThickness = new Thickness(0, 0, 0, 1),
                IsVisible = false
            };
            Grid.SetRow(multiFileChangesetBar, 2);
            grid.Children.Add(multiFileChangesetBar);

            // Row 3: Agent Feed Control
            AgentFeed = new AvaloniaAgentFeedControl();
            AgentFeed.CodeActionRequested += HandleAgentCodeBlockAction;
            AgentFeed.CommandApprovalClicked += HandleAgentCommandApproval;
            AgentFeed.DiffDecisionClicked += HandleAgentDiffDecision;
            AgentFeed.QuickPromptClicked += prompt => { chatPromptInput.Text = prompt; SubmitChatMessage(); };
            AgentFeed.OpenSettingsRequested += ShowByokAndHarnessModal;
            Grid.SetRow(AgentFeed, 3);
            grid.Children.Add(AgentFeed);

            // Row 4: Footer Composer Card (Context Pills + Prompt Input + Mode Picker + Provider/Model Picker + Send)
            var composerStack = new StackPanel { Spacing = 4 };

            // Context pills row
            contextPillsWrap = new WrapPanel { Orientation = Orientation.Horizontal };
            RebuildContextPillsBar();
            composerStack.Children.Add(contextPillsWrap);

            // Prompt TextBox
            chatPromptInput = new TextBox
            {
                Name = "ChatPromptInput",
                Watermark = "Plan, @context, ask questions, or request multi-file edits (Enter to send)...",
                // VS Code chat input: grows with the prompt (and the wrapped placeholder) from two lines up to a cap,
                // then scrolls. The fixed 52 px height clipped the two-line placeholder (QA #4).
                AcceptsReturn = true,
                MinHeight = Math.Ceiling(UiTokens.FontUi * 1.45 * 2 + 8),
                MaxHeight = Math.Ceiling(UiTokens.FontUi * 1.45 * 12 + 8),
                FontSize = UiTokens.FontUi,
                TextWrapping = TextWrapping.Wrap,
                VerticalContentAlignment = VerticalAlignment.Top,
                Padding = new Thickness(2, 4),
                BorderThickness = new Thickness(0),
                Background = Brushes.Transparent
            };
            ScrollViewer.SetVerticalScrollBarVisibility(chatPromptInput, Avalonia.Controls.Primitives.ScrollBarVisibility.Auto);
            // Enter sends, Shift+Enter inserts a newline (handled before the TextBox inserts one itself).
            chatPromptInput.AddHandler(KeyDownEvent, (s, e) =>
            {
                if (e.Key == Key.Enter && !e.KeyModifiers.HasFlag(KeyModifiers.Shift))
                {
                    SubmitChatMessage();
                    e.Handled = true;
                }
            }, Avalonia.Interactivity.RoutingStrategies.Tunnel);
            composerStack.Children.Add(chatPromptInput);

            // Mode pills row: ✦ Agent | Ask | Edit | Auto-Fix
            var modeRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
            foreach (var m in new (string Id, string Label)[] { ("agent", "Agent"), ("ask", "Ask"), ("edit", "Edit"), ("autofix", "Auto-Fix") })
            {
                string modeId = m.Id;
                var mb = new Button
                {
                    Content = m.Label,
                    FontSize = UiTokens.FontSmall,
                    Padding = new Thickness(8, 2),
                    MinHeight = 22,
                    BorderThickness = new Thickness(0)
                };
                mb.Classes.Add("flat");
                mb.Click += (s, e) => SetAgentMode(modeId);
                agentModeButtons[modeId] = mb;
                modeRow.Children.Add(mb);
            }
            composerStack.Children.Add(modeRow);

            // Provider + Model Picker + Send Button Row
            var bottomRow = new Grid
            {
                ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto")
            };
            var providers = LlmHarnessAndAgentEngine.GetAllProviderTemplates();
            chatProviderCombo = new ComboBox
            {
                ItemsSource = providers.Select(p => p.Name).ToList(),
                SelectedIndex = Math.Max(0, providers.FindIndex(p => string.Equals(p.Name, settings.ActiveProvider, StringComparison.OrdinalIgnoreCase))),
                FontSize = UiTokens.FontSmall,
                Height = 24,
                MinHeight = 24,
                Width = 140,
                Padding = new Thickness(6, 0, 0, 0),
                BorderThickness = new Thickness(0),
                Background = Brushes.Transparent,
                VerticalAlignment = VerticalAlignment.Center
            };
            ToolTip.SetTip(chatProviderCombo, "AI provider");
            chatModelCombo = new ComboBox
            {
                FontSize = UiTokens.FontSmall,
                Height = 24,
                MinHeight = 24,
                Margin = new Thickness(2, 0, 6, 0),
                HorizontalAlignment = HorizontalAlignment.Stretch,
                Padding = new Thickness(6, 0, 0, 0),
                BorderThickness = new Thickness(0),
                Background = Brushes.Transparent,
                VerticalAlignment = VerticalAlignment.Center
            };
            ToolTip.SetTip(chatModelCombo, "Model");
            PopulateModelsForActiveProvider();

            chatProviderCombo.SelectionChanged += (s, e) =>
            {
                if (chatProviderCombo.SelectedItem != null)
                {
                    settings.ActiveProvider = chatProviderCombo.SelectedItem.ToString();
                    PopulateModelsForActiveProvider();
                    UpdateStatusBar();
                }
            };
            chatModelCombo.SelectionChanged += (s, e) =>
            {
                if (chatModelCombo.SelectedItem != null)
                {
                    settings.ActiveModel = chatModelCombo.SelectedItem.ToString();
                    UpdateStatusBar();
                }
            };

            chatSendBtn = WorkbenchStyles.IconButton("send", "Send (Enter)", null, currentTheme, 28, 16);
            chatSendBtn.Name = "ChatSendBtn";
            chatSendBtn.Click += (s, e) => { if (IsChatTurnRunning) StopChatTurn(); else SubmitChatMessage(); };

            Grid.SetColumn(chatProviderCombo, 0); bottomRow.Children.Add(chatProviderCombo);
            Grid.SetColumn(chatModelCombo, 1); bottomRow.Children.Add(chatModelCombo);
            Grid.SetColumn(chatSendBtn, 2); bottomRow.Children.Add(chatSendBtn);
            composerStack.Children.Add(bottomRow);

            // VS Code chat input: one input-colored card (pills, prompt, toolbar) with a 1 px border that turns
            // accent while the prompt has focus.
            composerBorder = new Border
            {
                Child = composerStack,
                Margin = new Thickness(10, 4, 10, 10),
                Padding = new Thickness(8, 6, 6, 6),
                CornerRadius = new CornerRadius(UiTokens.ToolbarRadius),
                BorderThickness = new Thickness(1)
            };
            chatPromptInput.GotFocus += (s, e) => ApplyComposerTheme();
            chatPromptInput.LostFocus += (s, e) => ApplyComposerTheme();
            Grid.SetRow(composerBorder, 4);
            grid.Children.Add(composerBorder);

            return new Border
            {
                Child = grid,
                BorderThickness = new Thickness(1, 0, 0, 0)
            };
        }

        private void PopulateModelsForActiveProvider()
        {
            var tpl = LlmHarnessAndAgentEngine.GetProviderTemplate(settings.ActiveProvider);
            var models = new List<string>(tpl.SuggestedModels ?? new[] { tpl.DefaultModel ?? "gpt-4o" });
            if (!string.IsNullOrEmpty(settings.ActiveModel) && !models.Contains(settings.ActiveModel))
                models.Insert(0, settings.ActiveModel);
            chatModelCombo.ItemsSource = models;
            int idx = models.IndexOf(settings.ActiveModel);
            chatModelCombo.SelectedIndex = idx >= 0 ? idx : 0;
        }

        public void AttachChatContextPill(string pillName)
        {
            if (!string.IsNullOrWhiteSpace(pillName) && !attachedContextPills.Contains(pillName))
            {
                attachedContextPills.Add(pillName);
                RebuildContextPillsBar();
            }
        }

        private void RebuildContextPillsBar()
        {
            if (contextPillsWrap == null) return;
            contextPillsWrap.Children.Clear();

            var pillTheme = currentTheme ?? StudioTheme.GetById(StudioTheme.DefaultThemeId);
            var addCtxBtn = new Button
            {
                Content = "+ @ Context",
                FontSize = UiTokens.FontSmall - 0.5,
                Padding = new Thickness(6, 1),
                MinHeight = 20,
                Margin = new Thickness(0, 0, 4, 3),
                Background = Brushes.Transparent,
                Foreground = WorkbenchStyles.B(pillTheme.FgSecondary),
                BorderThickness = new Thickness(1),
                BorderBrush = WorkbenchStyles.B(UiTokens.InputBorder(pillTheme))
            };
            ToolTip.SetTip(addCtxBtn, "Attach the active file as context");
            addCtxBtn.Click += (s, e) =>
            {
                if (activeTab != null && !attachedContextPills.Contains(activeTab.FileName))
                {
                    attachedContextPills.Add(activeTab.FileName);
                    RebuildContextPillsBar();
                }
            };
            contextPillsWrap.Children.Add(addCtxBtn);

            string activePillText = activeTab != null ? $"@{activeTab.FileName}" : "@Workspace";
            contextPillsWrap.Children.Add(new Border
            {
                CornerRadius = new CornerRadius(UiTokens.ControlRadius),
                Padding = new Thickness(6, 2),
                Margin = new Thickness(0, 0, 4, 3),
                Background = WorkbenchStyles.B(UiTokens.ButtonSecondaryBackground(pillTheme)),
                Child = new TextBlock { Text = activePillText, FontSize = UiTokens.FontSmall - 0.5, Foreground = WorkbenchStyles.B(pillTheme.FgPrimary) }
            });

            foreach (string pill in attachedContextPills.ToList())
            {
                string captured = pill;
                var pBtn = new Button
                {
                    Content = new LiteralTextBlock($"@{captured}  ✕") { FontSize = UiTokens.FontSmall - 0.5 },
                    FontSize = UiTokens.FontSmall - 0.5,
                    Padding = new Thickness(6, 1),
                    MinHeight = 20,
                    Margin = new Thickness(0, 0, 4, 3)
                };
                ToolTip.SetTip(pBtn, "Remove context");
                pBtn.Click += (s, e) =>
                {
                    attachedContextPills.Remove(captured);
                    RebuildContextPillsBar();
                };
                contextPillsWrap.Children.Add(pBtn);
            }
        }

        private Border BuildStatusBar()
        {
            var grid = new Grid
            {
                ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto")
            };

            var leftRow = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 0,
                VerticalAlignment = VerticalAlignment.Stretch
            };

            Button MakeStatusBtn(string text, Action onClick, bool isRemoteBadge = false) => MakeStatusBarItem(text, onClick, isRemoteBadge);

            statusRemoteBtn = MakeStatusBarItem(new IconLabel("remote", "codeLess", 12, UiTokens.FontSmall, "><  "), () => OpenCommandPalette(">"), isRemoteBadge: true);
            ToolTip.SetTip(statusRemoteBtn, "codeLess — Show All Commands");
            statusBranchBtn = MakeStatusBarItem(new IconLabel("branch", "main", 14, UiTokens.FontSmall, "⎇ "), () => OpenBranchPicker());
            statusSyncBtn = MakeStatusBtn("⟳", () => OnSyncStatusClicked());
            statusSyncBtn.IsVisible = false;
            statusProblemsBtn = MakeStatusBarItem(new StatusProblemsContent(), () => SwitchBottomTab("problems"));
            statusMessageLabel = new TextBlock
            {
                Text = "",
                FontSize = UiTokens.FontSmall,
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis,
                TextWrapping = TextWrapping.NoWrap,
                Margin = new Thickness(8, 0, 8, 0)
            };
            statusChordLabel = new TextBlock
            {
                Text = "",
                FontSize = UiTokens.FontSmall,
                FontWeight = FontWeight.SemiBold,
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis,
                Margin = new Thickness(4, 0, 8, 0)
            };

            leftRow.Children.Add(statusRemoteBtn);
            leftRow.Children.Add(BuildRestrictedModeStatusItem());
            leftRow.Children.Add(statusBranchBtn);
            leftRow.Children.Add(statusSyncBtn);
            leftRow.Children.Add(statusProblemsBtn);
            Grid.SetColumn(leftRow, 0);
            grid.Children.Add(leftRow);

            // Transient messages live in the flexible middle column and are ellipsized, so they can never push
            // the right-hand items off the bar.
            var middle = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*"), ClipToBounds = true };
            Grid.SetColumn(statusMessageLabel, 1);
            middle.Children.Add(statusChordLabel);
            middle.Children.Add(statusMessageLabel);
            Grid.SetColumn(middle, 1);
            grid.Children.Add(middle);

            var rightRow = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 0,
                Margin = new Thickness(0, 0, 4, 0),
                VerticalAlignment = VerticalAlignment.Stretch
            };

            statusLnColBtn = MakeStatusBtn("Ln 1, Col 1", PromptGoToLineModal);
            statusSpacesBtn = MakeStatusBtn("Spaces: 4", OpenIndentationPicker);
            statusEncodingBtn = MakeStatusBtn("UTF-8", () =>
            {
                if (activeTab != null)
                {
                    activeTab.HasBom = !activeTab.HasBom;
                    activeTab.IsDirty = true;
                    UpdateStatusBar();
                }
            });
            statusEolBtn = MakeStatusBtn("CRLF", () =>
            {
                if (activeTab != null)
                {
                    activeTab.EolSequence = activeTab.EolSequence == "\n" ? "\r\n" : "\n";
                    activeTab.IsDirty = true;
                    UpdateStatusBar();
                }
            });
            statusLanguageBtn = MakeStatusBtn("Plain Text", OpenLanguageModePicker);
            statusAiModelBtn = MakeStatusBtn($"✦ {settings.ActiveModel}", ShowByokAndHarnessModal);
            statusLspBtn = MakeStatusBtn("", OpenLanguageServerStatusMenu);
            statusLspBtn.IsVisible = false;

            rightRow.Children.Add(BuildUpdateStatusItem()); // hidden unless downloading / Restart to Update
            rightRow.Children.Add(statusLspBtn);
            rightRow.Children.Add(statusLnColBtn);
            rightRow.Children.Add(statusSpacesBtn);
            rightRow.Children.Add(statusEncodingBtn);
            rightRow.Children.Add(statusEolBtn);
            rightRow.Children.Add(statusLanguageBtn);
            rightRow.Children.Add(BuildNetworkStatusButton());
            rightRow.Children.Add(statusAiModelBtn);
            Grid.SetColumn(rightRow, 2);
            grid.Children.Add(rightRow);

            return new Border
            {
                Height = UiTokens.StatusBarHeight,
                Child = grid
            };
        }
    }
}

namespace codeLess.AvaloniaApp
{
    public partial class MainWindow
    {
        public void ApplyTheme(StudioTheme theme, bool persistSetting = true)
        {
            if (theme == null) return;
            currentTheme = theme;
            RefreshExtensionsView();
            if (persistSetting && settings != null)
            {
                settings.ThemeId = theme.Id;
                try { settings.Save(); } catch { }
            }

            Color textPrimary = AvaloniaColorHelper.ToAvColor(theme.TextPrimary);
            Color textSecondary = AvaloniaColorHelper.ToAvColor(theme.TextSecondary);
            Color accent = AvaloniaColorHelper.ToAvColor(theme.AccentPrimary);
            Color borderCol = AvaloniaColorHelper.ToAvColor(theme.BorderSubtle);
            bool isLight = AvaloniaColorHelper.IsLightColor(theme.EditorBg);

            RequestedThemeVariant = isLight
                ? global::Avalonia.Styling.ThemeVariant.Light
                : global::Avalonia.Styling.ThemeVariant.Dark;
            WorkbenchStyles.Apply(theme);
            InstallChromeStyles(theme);

            Background = AvaloniaColorHelper.ToBrush(theme.EditorBg);

            // Top menu bar
            topMenuBarBorder.Background = AvaloniaColorHelper.ToBrush(theme.TitleBarBg);
            topMenuBarBorder.BorderBrush = new SolidColorBrush(borderCol);
            mainTopMenu.Foreground = new SolidColorBrush(textPrimary);
            if (mainTopMenu.ItemsSource != null)
            {
                foreach (var mi in mainTopMenu.ItemsSource.OfType<MenuItem>())
                    mi.Foreground = new SolidColorBrush(textPrimary);
            }
            commandCenterBtn.Background = WorkbenchStyles.B(UiTokens.Blend(theme.TitleBarBg, theme.FgPrimary, isLight ? 0.04 : 0.05));
            commandCenterBtn.BorderBrush = WorkbenchStyles.B(UiTokens.Blend(theme.TitleBarBg, theme.FgPrimary, isLight ? 0.14 : 0.12));
            commandCenterBtn.BorderThickness = new Thickness(1);
            commandCenterText.Foreground = new SolidColorBrush(textSecondary);
            commandCenterBtn.Foreground = new SolidColorBrush(textSecondary);

            // Activity Bar
            activityBarBorder.Background = AvaloniaColorHelper.ToBrush(theme.ActivityBarBg);
            activityBarBorder.BorderBrush = new SolidColorBrush(borderCol);
            UpdateActivityBarSelectionVisuals();

            // Sidebar
            sidebarBorder.Background = AvaloniaColorHelper.ToBrush(theme.SidebarBg);
            sidebarBorder.BorderBrush = new SolidColorBrush(borderCol);
            sidebarHeaderTitle.Foreground = new SolidColorBrush(textSecondary);
            foreach (var hb in sidebarHeaderIconButtons) WorkbenchStyles.ThemeIconButton(hb, theme);
            sidebarProgressBar.TrackColor = AvaloniaColorHelper.ToAvColor(theme.SidebarBg);
            sidebarProgressBar.BarColor = accent;
            openEditorsHeaderBtn.Foreground = new SolidColorBrush(textSecondary);
            workspaceFolderHeaderBtn.Foreground = new SolidColorBrush(textPrimary);
            outlineHeaderBtn.Foreground = new SolidColorBrush(textSecondary);
            ExplorerTree.ApplyTheme(theme);

            // Tab Strip & Editors
            tabStripBorder.Background = AvaloniaColorHelper.ToBrush(theme.TabBarBg);
            tabStripBorder.BorderBrush = new SolidColorBrush(borderCol);
            if (tabStripBottomLine != null) tabStripBottomLine.Background = new SolidColorBrush(borderCol);
            splitEditorBorder.BorderBrush = new SolidColorBrush(borderCol);
            PrimaryEditor.ApplyTheme(theme);
            SplitEditor.ApplyTheme(theme);
            RefreshTabStrip();
            RefreshOpenEditorsSection();
            RefreshOutlineAndProblems(activeTab);
            RebuildScmListsUi();

            // Bottom Panel
            bottomPanelBorder.Background = AvaloniaColorHelper.ToBrush(theme.TerminalBg);
            bottomPanelBorder.BorderBrush = new SolidColorBrush(borderCol);
            terminalSessionListBorder.Background = AvaloniaColorHelper.ToBrush(theme.SidebarBg);
            terminalSessionListBorder.BorderBrush = new SolidColorBrush(borderCol);
            outputStreamBox.Background = AvaloniaColorHelper.ToBrush(theme.TerminalBg);
            outputStreamBox.Foreground = AvaloniaColorHelper.ToBrush(theme.TerminalText);
            foreach (var ts in terminalSessions)
                ts.Control?.ApplyTheme(theme);
            RefreshBottomTabStyles();
            RefreshTerminalSessionListUi();

            // Right AI Agent Dock
            rightChatDockBorder.Background = AvaloniaColorHelper.ToBrush(theme.ChatBg);
            rightChatDockBorder.BorderBrush = new SolidColorBrush(borderCol);
            chatProgressBar.TrackColor = AvaloniaColorHelper.ToAvColor(theme.ChatBg);
            chatProgressBar.BarColor = accent;
            multiFileChangesetBar.Background = AvaloniaColorHelper.ToBrush(theme.ChatBg);
            multiFileChangesetBar.BorderBrush = new SolidColorBrush(borderCol);
            ApplyChatDockChromeTheme(theme);
            if (scmCommitButton != null) WorkbenchStyles.StylePrimaryButton(scmCommitButton, theme);
            changesetSummaryText.Foreground = new SolidColorBrush(textPrimary);
            RebuildContextPillsBar();
            AgentFeed.ApplyTheme(theme);
            SetAgentMode(currentAgentMode);
            UpdateAgentTrustComposer();
            RefreshAgentTrustBanner();

            // Status Bar
            statusBarBorder.Background = AvaloniaColorHelper.ToBrush(UiTokens.StatusBarBackground(theme));
            Color statusFg = AvaloniaColorHelper.ToAvColor(UiTokens.StatusBarForeground(theme));
            ApplyStatusBarTheme(theme);
            statusBranchBtn.Foreground = new SolidColorBrush(statusFg);
            if (statusSyncBtn != null) statusSyncBtn.Foreground = new SolidColorBrush(statusFg);
            statusProblemsBtn.Foreground = new SolidColorBrush(statusFg);
            statusMessageLabel.Foreground = new SolidColorBrush(statusFg);
            statusChordLabel.Foreground = new SolidColorBrush(Color.FromRgb(250, 204, 21));
            statusLnColBtn.Foreground = new SolidColorBrush(statusFg);
            statusSpacesBtn.Foreground = new SolidColorBrush(statusFg);
            statusEncodingBtn.Foreground = new SolidColorBrush(statusFg);
            statusEolBtn.Foreground = new SolidColorBrush(statusFg);
            statusLanguageBtn.Foreground = new SolidColorBrush(statusFg);
            statusAiModelBtn.Foreground = new SolidColorBrush(statusFg);
            ApplyNetworkPrivacyTheme(theme);

            ApplySearchViewTheme();
            ApplyLanguageServerTheme(theme);

            // Overlays
            CommandPalette.ApplyTheme(theme);
            ModalHost.ApplyTheme(theme);
            ApplyDebugTheme(theme);
            ApplyDevToolsTheme(theme);
        }

        private void UpdateActivityBarSelectionVisuals()
        {
            if (currentTheme == null) return;
            Color activeCol = AvaloniaColorHelper.ToAvColor(currentTheme.TextPrimary);
            Color inactiveCol = AvaloniaColorHelper.ToAvColor(currentTheme.TextSecondary);
            Color accent = AvaloniaColorHelper.ToAvColor(currentTheme.AccentPrimary);

            foreach (var kv in activityBarButtons)
            {
                bool isActive = sidebarBorder.IsVisible && string.Equals(kv.Key, activeSidebarView, StringComparison.OrdinalIgnoreCase);
                kv.Value.Icon.IconColor = isActive ? activeCol : inactiveCol;
                kv.Value.ActiveBar.Background = new SolidColorBrush(accent);
                kv.Value.ActiveBar.IsVisible = isActive;
                kv.Value.Icon.InvalidateVisual();
            }
        }

        public static readonly HashSet<string> DefaultIgnoredBinaryExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ".exe", ".dll", ".pdb", ".zip", ".ico", ".png", ".dat", ".bin", ".obj",
            ".cache", ".suo", ".vsix", ".nupkg", ".jpg", ".jpeg", ".gif", ".bmp",
            ".webp", ".pdf", ".woff", ".woff2", ".ttf", ".eot", ".mp3", ".mp4",
            ".wav", ".avi", ".mov", ".tar", ".gz", ".7z", ".so", ".dylib", ".class", ".pyc", ".icns"
        };

        public HashSet<string> GetExcludedFolderSet(string rootDir = null)
        {
            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                ".git", "node_modules", "bin", "obj", "dist", "build", "out",
                ".venv", "__pycache__", ".next", "target", "packages", ".vs"
            };
            if (settings != null)
            {
                string[] custom = settings.GetExcludedFoldersList();
                if (custom != null && custom.Length > 0)
                {
                    set.Clear();
                    foreach (string c in custom)
                    {
                        if (!string.IsNullOrWhiteSpace(c))
                            set.Add(c.Trim().Trim('/', '\\'));
                    }
                    set.Add(".vs");
                    set.Add(".git");
                }
            }

            string checkDir = !string.IsNullOrEmpty(rootDir) ? rootDir : workspaceRoot;
            if (!string.IsNullOrEmpty(checkDir))
            {
                try
                {
                    string gitignorePath = Path.Combine(checkDir, ".gitignore");
                    if (File.Exists(gitignorePath))
                    {
                        foreach (string rawLine in File.ReadLines(gitignorePath))
                        {
                            string line = (rawLine ?? "").Trim();
                            if (line.Length == 0 || line[0] == '#' || line[0] == '!') continue;
                            string clean = line.Trim('/');
                            if (clean.Length > 0 && !clean.Contains('*') && !clean.Contains('?') && !clean.Contains('/'))
                                set.Add(clean);
                        }
                    }
                }
                catch { }
            }
            return set;
        }

        public void LoadWorkspace(string rootDir, bool keepCurrentTabs = false)
        {
            if (string.IsNullOrWhiteSpace(rootDir) || !Directory.Exists(rootDir)) return;

            if (!keepCurrentTabs && !ConfirmSaveOrDiscardDirtyTabs(openTabs, () => LoadWorkspace(rootDir, keepCurrentTabs)))
                return;

            try { workspaceLoadCts?.Cancel(); } catch { }
            var cts = new CancellationTokenSource();
            workspaceLoadCts = cts;
            int loadGen = ++workspaceLoadGeneration;

            sidebarProgressBar.StartProgress();
            string newRoot = Path.GetFullPath(rootDir);
            if (!keepCurrentTabs)
            {
                // A different folder: its own remembered extra folders, not the previous workspace's.
                additionalWorkspaceFolders.Clear();
                additionalWorkspaceFolders.AddRange(WorkspaceFolders.Load(settings, newRoot));
            }
            workspaceRoot = newRoot;
            settings.AddRecentWorkspace(workspaceRoot);
            OnDebugWorkspaceChanged();
            LspOnWorkspaceChanged();
            OnWorkspaceTrustFolderOpened();
            FollowWorkspaceInTerminals(workspaceRoot); // VS Code: terminals start in the newly opened folder

            string folderName = Path.GetFileName(workspaceRoot.TrimEnd('\\', '/'));
            if (string.IsNullOrEmpty(folderName)) folderName = workspaceRoot;
            workspaceFolderHeaderBtn.Content = new SectionHeaderLabel("▾ " + folderName.ToUpperInvariant());
            string scP = AvaloniaCommandPaletteOverlay.FormatShortcutForPlatform("Ctrl+P");
            string scShiftP = AvaloniaCommandPaletteOverlay.FormatShortcutForPlatform("Ctrl+Shift+P");
            commandCenterText.Text = folderName;
            ToolTip.SetTip(commandCenterBtn, $"Search files by name ({scP}) · Show and run commands ({scShiftP})");

            workspaceFiles.Clear();
            ExplorerTree.Nodes.Clear();
            lock (workspaceIndexLock)
            {
                // New folder: drop the previous workspace's index (frees its memory); rebuilt lazily on first use.
                if (workspaceIndexState != 1)
                {
                    workspaceIndexState = 0;
                    indexCandidateFiles = null;
                    aiEngine.IndexedChunks = new List<Bm25CodeChunk>();
                    aiEngine.WorkspaceSymbolMap = new List<WorkspaceSymbolLocation>();
                    aiEngine.WorkspaceSymbols = new List<string>();
                }
            }

            var excluded = GetExcludedFolderSet(workspaceRoot);
            excluded.Add("DerivedData");
            excluded.Add("Pods");
            excluded.Add(".gradle");
            excluded.Add(".nuxt");
            excluded.Add(".svelte-kit");
            excluded.Add("coverage");
            excluded.Add(".turbo");
            excluded.Add(".cache");

            var seenFiles = new HashSet<string>(PathKeyComparer);
            RefreshExplorerDimmedNames();
            var explorerHidden = GetExplorerHiddenSet();
            var rootNode = new ExplorerTreeNode(folderName)
            {
                Tag = workspaceRoot,
                IsDirectory = true,
                StateImageIndex = 1,
                IsExpanded = true
            };
            PopulateSingleDirectoryLevel(workspaceRoot, rootNode, explorerHidden, addFilesToIndex: true, seenSet: seenFiles);
            ExplorerTree.Nodes.Add(rootNode);

            foreach (string extraDir in additionalWorkspaceFolders)
            {
                if (Directory.Exists(extraDir))
                {
                    var extraNode = new ExplorerTreeNode(Path.GetFileName(extraDir))
                    {
                        Tag = extraDir,
                        IsDirectory = true,
                        StateImageIndex = 1,
                        IsExpanded = true
                    };
                    PopulateSingleDirectoryLevel(extraDir, extraNode, explorerHidden, addFilesToIndex: true, seenSet: seenFiles);
                    ExplorerTree.Nodes.Add(extraNode);
                }
            }

            ExplorerTree.RebuildTreeLayout(preserveScroll: false);
            StartWorkspaceWatcher(workspaceRoot);
            PrimaryEditor.SetWorkspaceContext(workspaceRoot, settings.RecentWorkspaces);
            SplitEditor.SetWorkspaceContext(workspaceRoot, settings.RecentWorkspaces);

            // Fast branch read via .git/HEAD file (zero subprocess spawn)
            string fastBranch = LlmHarnessAndAgentEngine.GetGitBranch(workspaceRoot);
            bool noGit = string.IsNullOrEmpty(fastBranch) || fastBranch == "no-git";
            SetStatusBranch(noGit ? "no-git" : fastBranch);
            scmBranchBanner.Text = noGit ? "No Git repository initialized" : $"Branch: {fastBranch}";

            bool isLiveDesktop = AvaloniaTerminalEmulatorControl.IsDesktopRuntime;
            if (!isLiveDesktop)
            {
                // Headless test mode: complete indexing synchronously for immediate deterministic assertions
                CollectWorkspaceFilesRecursive(workspaceRoot, excluded, depth: 0, seenSet: seenFiles, maxLimit: 1400, ct: CancellationToken.None);
                aiEngine.IndexWorkspace(workspaceRoot, workspaceFiles);
                lock (workspaceIndexLock) { workspaceIndexState = 2; indexCandidateFiles = new List<string>(workspaceFiles); }
                if (!noGit) RefreshGitStatus();
                UpdateWindowTitle();
                UpdateStatusBar();
                sidebarProgressBar.StopProgress();
                return;
            }

            // Like VS Code, opening a folder opens no file by itself (Welcome or the empty editor stays; the user picks).
            UpdateWindowTitle();
            UpdateStatusBar();

            string capturedRoot = workspaceRoot;
            bool capturedKeepTabs = keepCurrentTabs;
            var token = cts.Token;
            var ignoreRules = CreateWorkspaceIgnoreRules(capturedRoot);
            var capturedRoots = GetWorkspaceRootFolders().ToList();

            _ = Task.Run(() =>
            {
                // .gitignore-aware walk, no depth limit, capped at MaxIndexedWorkspaceFiles (was: 12,000 files / depth 7).
                var bgFiles = CollectIndexableFiles(ignoreRules, capturedRoots, token, out bool bgCapped);
                WorkspaceFileListCapped = bgCapped;
                if (token.IsCancellationRequested || loadGen != workspaceLoadGeneration) return;

                // The BM25 / symbol index (~25-30 MB for a mid-size repo) is built lazily on first use
                // (AI chat, Go to Definition, workspace symbol search) instead of on every folder open.
                lock (workspaceIndexLock) { workspaceIndexState = 0; indexCandidateFiles = bgFiles.Count <= 1500 ? bgFiles : bgFiles.Take(1500).ToList(); }
                if (token.IsCancellationRequested || loadGen != workspaceLoadGeneration) return;

                List<GitStatusEntry> bgGitEntries = noGit
                    ? new List<GitStatusEntry>()
                    : (LlmHarnessAndAgentEngine.GetGitStatusEntries(capturedRoot) ?? new List<GitStatusEntry>());
                if (token.IsCancellationRequested || loadGen != workspaceLoadGeneration) return;

                MainUi.Post(() =>
                {
                    if (token.IsCancellationRequested || loadGen != workspaceLoadGeneration) return;
                    workspaceFiles.Clear();
                    workspaceFiles.AddRange(bgFiles);
                    ApplyGitEntriesToUi(fastBranch, bgGitEntries);
                    AppendOutputLog($"[Workspace] Loaded '{folderName}' ({workspaceFiles.Count} files{(WorkspaceFileListCapped ? $", capped at {MaxIndexedWorkspaceFiles:N0}: Quick Open / indexing cover the first {MaxIndexedWorkspaceFiles:N0} files" : "")}).");
                    if (WorkspaceFileListCapped) SetStatusMessage($"Large workspace: indexed the first {MaxIndexedWorkspaceFiles:N0} files (node_modules, build output and .gitignore'd files are skipped).");
                    PerfWorkspaceLoadCompletions++;
                    // Startup burst is over: hand freed native pages back to the OS (cheap, non-blocking). The managed
                    // heap is NOT force-compacted here: a blocking full GC right as the user starts working cost ~4%
                    // scroll smoothness in benchmarks. Compaction happens only when the window is in the background.
                    DispatcherTimer.RunOnce(() => ProcessMemoryTrimmer.Trim(compactManagedHeap: false), TimeSpan.FromSeconds(3));
                    sidebarProgressBar.StopProgress();
                });
            }, token);
        }

        private void CollectWorkspaceFilesRecursive(string dirPath, HashSet<string> excluded, int depth, HashSet<string> seenSet, int maxLimit, CancellationToken ct)
        {
            CollectWorkspaceFilesListRecursive(dirPath, excluded, depth, workspaceFiles, seenSet, maxLimit, ct);
        }

        private static void CollectWorkspaceFilesListRecursive(
            string dirPath,
            HashSet<string> excluded,
            int depth,
            List<string> targetList,
            HashSet<string> seenSet,
            int maxLimit,
            CancellationToken ct)
        {
            if (depth > 7 || targetList.Count >= maxLimit || ct.IsCancellationRequested) return;
            try
            {
                foreach (string f in Directory.EnumerateFiles(dirPath))
                {
                    if (ct.IsCancellationRequested || targetList.Count >= maxLimit) return;
                    string name = Path.GetFileName(f);
                    if (string.IsNullOrEmpty(name) || name.StartsWith("unins000.", StringComparison.OrdinalIgnoreCase)) continue;
                    string ext = Path.GetExtension(name);
                    if (!string.IsNullOrEmpty(ext) && DefaultIgnoredBinaryExtensions.Contains(ext)) continue;
                    if (seenSet.Add(f))
                        targetList.Add(f);
                }
                foreach (string sub in Directory.EnumerateDirectories(dirPath))
                {
                    if (ct.IsCancellationRequested || targetList.Count >= maxLimit) return;
                    string subName = Path.GetFileName(sub);
                    if (string.IsNullOrEmpty(subName) || (excluded != null && excluded.Contains(subName))) continue;
                    CollectWorkspaceFilesListRecursive(sub, excluded, depth + 1, targetList, seenSet, maxLimit, ct);
                }
            }
            catch { }
        }

        public void EnsureDirectoryNodePopulated(ExplorerTreeNode folderNode)
        {
            if (folderNode == null || !folderNode.IsDirectory) return;
            if (folderNode.Nodes.Count == 1 && folderNode.Nodes[0].Tag == "__LAZY_DIR__")
            {
                folderNode.Nodes.Clear();
                PopulateSingleDirectoryLevel(folderNode.Tag, folderNode, GetExplorerHiddenSet(), addFilesToIndex: false, seenSet: null);
            }
        }

        /// <param name="hiddenNames">Names hidden from the tree (VS Code files.exclude); see GetExplorerHiddenSet.</param>
        private void PopulateSingleDirectoryLevel(string dirPath, ExplorerTreeNode parentNode, HashSet<string> hiddenNames, bool addFilesToIndex, HashSet<string> seenSet = null)
        {
            try
            {
                var subDirs = new List<string>();
                foreach (string d in Directory.EnumerateDirectories(dirPath))
                {
                    string name = Path.GetFileName(d);
                    if (string.IsNullOrEmpty(name)) continue;
                    if (hiddenNames != null && hiddenNames.Contains(name)) continue;
                    subDirs.Add(d);
                }
                subDirs.Sort((a, b) => string.Compare(Path.GetFileName(a), Path.GetFileName(b), StringComparison.OrdinalIgnoreCase));

                foreach (string subDir in subDirs)
                {
                    string name = Path.GetFileName(subDir);
                    var dirNode = new ExplorerTreeNode(name)
                    {
                        Tag = subDir,
                        IsDirectory = true,
                        StateImageIndex = 1,
                        IsExpanded = false,
                        IsDimmed = parentNode.IsDimmed || IsExplorerDimmedName(name),
                        Parent = parentNode
                    };
                    dirNode.Nodes.Add(new ExplorerTreeNode("") { Tag = "__LAZY_DIR__", StateImageIndex = 2, Parent = dirNode });
                    parentNode.Nodes.Add(dirNode);
                }

                var files = new List<string>();
                foreach (string f in Directory.EnumerateFiles(dirPath))
                {
                    string name = Path.GetFileName(f);
                    if (string.IsNullOrEmpty(name)) continue;
                    if (hiddenNames != null && hiddenNames.Contains(name)) continue;
                    files.Add(f);
                }
                files.Sort((a, b) => string.Compare(Path.GetFileName(a), Path.GetFileName(b), StringComparison.OrdinalIgnoreCase));

                foreach (string file in files)
                {
                    string ext = Path.GetExtension(file);
                    bool indexable = string.IsNullOrEmpty(ext) || !DefaultIgnoredBinaryExtensions.Contains(ext);
                    if (addFilesToIndex && indexable && (seenSet != null ? seenSet.Add(file) : !workspaceFiles.Contains(file, PathKeyComparer)))
                        workspaceFiles.Add(file);
                    string fname = Path.GetFileName(file);
                    parentNode.Nodes.Add(new ExplorerTreeNode(fname)
                    {
                        Tag = file,
                        IsDirectory = false,
                        StateImageIndex = 2,
                        IsDimmed = parentNode.IsDimmed || IsExplorerDimmedName(fname),
                        Parent = parentNode
                    });
                }
            }
            catch { }
        }

        public EditorTabBuffer OpenFileInTab(string filePath, bool preview = false)
        {
            if (string.IsNullOrWhiteSpace(filePath)) return null;
            string fullPath = Path.GetFullPath(filePath);

            var existing = openTabs.FirstOrDefault(t => string.Equals(t.FilePath, fullPath, PathKeyComparison));
            if (existing == null && File.Exists(fullPath) && IsLikelyBinaryFile(fullPath))
            {
                // The text editor would corrupt binary content on save. Only passive media types go to the OS viewer:
                // "open"/xdg-open on an .app, .command, .sh, .desktop, .jar... would execute it on a single click.
                string bext = Path.GetExtension(fullPath);
                if (!string.IsNullOrEmpty(bext) && SafeViewerExtensions.Contains(bext))
                {
                    if (AvaloniaTerminalEmulatorControl.IsDesktopRuntime) OpenWithSystemDefaultApp(fullPath);
                    SetStatusMessage($"'{Path.GetFileName(fullPath)}' opened in the default viewer.");
                }
                else
                {
                    SetStatusMessage($"'{Path.GetFileName(fullPath)}' is a binary file and was not opened (use Reveal in Finder / File Manager).");
                }
                return null;
            }
            if (existing != null)
            {
                if (!preview && existing.IsPreviewTab)
                {
                    existing.IsPreviewTab = false;
                    RefreshTabStrip();
                }
                ActivateTab(existing);
                return existing;
            }

            // Replace existing clean preview tab if opening in preview mode
            if (preview)
            {
                var prevTab = openTabs.FirstOrDefault(t => t.IsPreviewTab && !t.IsDirty && !t.IsPinned);
                if (prevTab != null)
                    openTabs.Remove(prevTab);
            }
            if (TryOpenLargeFileTab(fullPath, preview, out var largeFileTab)) return largeFileTab;

            Encoding fileEnc = new UTF8Encoding(false);
            bool hasBom = false;
            string eol = "\r\n";
            bool hasFinalNl = false;
            string content = File.Exists(fullPath)
                ? EditorTabBuffer.ReadFilePreservingMetadata(fullPath, out fileEnc, out hasBom, out eol, out hasFinalNl)
                : "";

            bool isLiveDesktop = AvaloniaTerminalEmulatorControl.IsDesktopRuntime;
            string gitHead = (!isLiveDesktop && File.Exists(fullPath) && !string.IsNullOrEmpty(workspaceRoot))
                ? LlmHarnessAndAgentEngine.GetGitQuickDiffBaseline(workspaceRoot, fullPath)
                : null;

            var tab = new EditorTabBuffer
            {
                FilePath = fullPath,
                RelativePath = !string.IsNullOrEmpty(workspaceRoot) ? LlmHarnessAndAgentEngine.GetRelativePath(workspaceRoot, fullPath) : Path.GetFileName(fullPath),
                FileName = Path.GetFileName(fullPath),
                Language = Path.GetExtension(fullPath).TrimStart('.'),
                Content = content,
                SavedContent = content,
                GitHeadContent = gitHead,
                FileEncoding = fileEnc,
                HasBom = hasBom,
                EolSequence = eol,
                HasFinalNewline = hasFinalNl,
                WordWrap = settings.WordWrap,
                IsDirty = false,
                IsPreviewTab = preview
            };
            tab.SyncLinesFromContent();
            EditorIndentation.ApplyOnOpen(tab, settings); // .editorconfig, else detectIndentation (QA #12)
            if (!tab.IsLargeFile)
                tab.RefreshGutterDiff();
            AnalyzeBufferSoon(tab, OnBufferAnalyzed); // big files: off the UI thread (QA #35)

            openTabs.Add(tab);
            if (File.Exists(fullPath))
                settings.AddRecentFile(fullPath);

            ActivateTab(tab);

            // Fetch Git HEAD asynchronously on live desktop so clicking files in Explorer never waits on a git subprocess
            if (isLiveDesktop && File.Exists(fullPath) && !string.IsNullOrEmpty(workspaceRoot) && !tab.IsLargeFile)
            {
                string capturedWs = workspaceRoot;
                _ = Task.Run(() =>
                {
                    string head = LlmHarnessAndAgentEngine.GetGitQuickDiffBaseline(capturedWs, fullPath); // gutter = working tree vs index
                    if (head != null)
                    {
                        MainUi.Post(() =>
                        {
                            tab.GitHeadContent = head;
                            tab.RefreshGutterDiff();
                            if (ReferenceEquals(activeTab, tab))
                                PrimaryEditor?.Canvas?.InvalidateVisual();
                        });
                    }
                });
            }

            return tab;
        }

        public EditorTabBuffer CreateNewUntitledTab()
        {
            // VS Code: "Untitled-N" (no extension); the language follows the workspace's main language, not a fixed ".cs".
            string name = UntitledFileDefaults.NextUntitledName(openTabs.Select(t => t.FileName));
            var tab = new EditorTabBuffer
            {
                FilePath = "",
                RelativePath = name,
                FileName = name,
                Language = UntitledFileDefaults.DetectWorkspaceExtension(workspaceRoot, workspaceFiles),
                Content = "",
                SavedContent = "",
                WordWrap = settings.WordWrap,
                IsDirty = false,
                IsUntitled = true,
                IsPreviewTab = false
            };
            tab.SyncLinesFromContent();
            openTabs.Add(tab);
            ActivateTab(tab);
            return tab;
        }

        public void ActivateTab(EditorTabBuffer tab)
        {
            SweepClosedLargeFileTabs();
            OnEditorFocusLeaving(activeTab, tab);
            // Remember where the caret was in the editor being left so Back returns there (not to where it was opened).
            if (!suppressNavRecord && !ReferenceEquals(activeTab, tab)) CaptureCurrentNavPosition();
            activeTab = tab;
            if (tab != null) editorMru.Touch(tab); // per-group MRU (Ctrl+Tab order, focus after close)
            if (tab != null)
            {
                PrimaryEditor.LoadBuffer(tab);
                if (isEditorSplit && SplitEditor.CurrentBuffer == null)
                    SplitEditor.LoadBuffer(tab);
                ExplorerTree.ActiveEditorFilePath = tab.FilePath ?? "";
                if (!string.IsNullOrEmpty(tab.FilePath)) RevealFileInExplorer(tab.FilePath);
                ExplorerTree.InvalidateVisual();
                RecordNavigationHistory(tab.FilePath, tab.CursorLine + 1);
            }
            else
            {
                PrimaryEditor.ShowWelcomeScreen();
                ExplorerTree.ActiveEditorFilePath = "";
                ExplorerTree.InvalidateVisual();
            }

            RefreshTabStrip();
            RefreshOpenEditorsSection();
            RefreshOutlineAndProblems(tab);
            RebuildContextPillsBar();
            UpdateWindowTitle();
            UpdateStatusBar();
            LspOnTabActivated(tab);
        }

        public void OpenWelcomeTab()
        {
            showWelcomeTab = true;
            PrimaryEditor.WelcomeEnabled = true;
            ActivateTab(null);
        }

        public bool IsWelcomeTabOpen => showWelcomeTab;

        public void CloseWelcomeTab()
        {
            showWelcomeTab = false;
            PrimaryEditor.WelcomeEnabled = false;
            if (activeTab == null && openTabs.Count > 0)
                ActivateTab(editorMru.MostRecentOther(openTabs, null) ?? openTabs[openTabs.Count - 1]);
            else
            {
                if (activeTab == null) PrimaryEditor.ShowWelcomeScreen(); // now the empty-editor watermark
                RefreshTabStrip();
            }
            UpdateWindowTitle();
        }

        public bool CloseTab(EditorTabBuffer tab, bool forceWithoutPrompt = false)
        {
            if (tab == null) return true;
            if (!forceWithoutPrompt && UntitledFileDefaults.NeedsSavePrompt(tab))
            {
                if (!ConfirmSaveOrDiscardDirtyTabs(new[] { tab }, () => { if (openTabs.Contains(tab)) CloseTab(tab, forceWithoutPrompt: true); }))
                    return false;
            }

            if (!string.IsNullOrEmpty(tab.FilePath) && File.Exists(tab.FilePath))
                closedTabHistory.Push(tab.FilePath);

            int idx = openTabs.IndexOf(tab);
            openTabs.Remove(tab);
            if (tab.OnClosed != null) { try { tab.OnClosed(); } catch { } }
            SweepClosedLargeFileTabs();

            if (ReferenceEquals(activeTab, tab))
            {
                if (openTabs.Count > 0)
                {
                    // VS Code (workbench.editor.focusRecentEditorAfterClose): the most recently used editor takes over.
                    int nextIdx = Math.Max(0, Math.Min(openTabs.Count - 1, idx));
                    ActivateTab(editorMru.MostRecentOther(openTabs, tab) ?? openTabs[nextIdx]);
                }
                else
                {
                    ActivateTab(null);
                }
            }
            else
            {
                RefreshTabStrip();
                RefreshOpenEditorsSection();
            }
            LspOnTabClosed(tab);
            return true;
        }

        public void ReopenClosedTab()
        {
            while (closedTabHistory.Count > 0)
            {
                string path = closedTabHistory.Pop();
                if (File.Exists(path))
                {
                    OpenFileInTab(path, preview: false);
                    return;
                }
            }
        }

        public bool SaveActiveTab()
        {
            if (activeTab == null) return false;
            if (TryFormatThenSave(activeTab)) return true;
            return SaveBufferToDisk(activeTab);
        }

        public void SaveAllTabs()
        {
            foreach (var tab in openTabs.ToList())
            {
                if (tab.IsDirty)
                    SaveBufferToDisk(tab);
            }
        }

        public bool SaveBufferToDisk(EditorTabBuffer tab)
        {
            if (tab == null) return false;
            if (tab.IsLargeFileViewer) { SetStatusMessage($"{tab.FileName} is open read-only in the large-file viewer and cannot be saved."); return false; }
            if (string.IsNullOrWhiteSpace(tab.FilePath) && AvaloniaTerminalEmulatorControl.IsDesktopRuntime && StorageProvider != null)
            {
                // Untitled: ask where to save it (VS Code) instead of silently writing "Untitled-1" somewhere.
                _ = PromptSaveAsAsync(tab);
                return false;
            }
            if (string.IsNullOrWhiteSpace(tab.FilePath))
            {
                string dir = !string.IsNullOrEmpty(workspaceRoot) && Directory.Exists(workspaceRoot)
                    ? workspaceRoot
                    : Path.GetTempPath();
                tab.FilePath = Path.Combine(dir, UntitledFileDefaults.SuggestSaveFileName(tab, workspaceRoot, workspaceFiles));
                tab.FileName = Path.GetFileName(tab.FilePath);
                tab.IsUntitled = false;
            }

            try
            {
                ApplySaveParticipants(tab);
                tab.SyncContentFromLines();
                var agentSave = BeginAgentTrackedSave(tab);
                tab.WriteFilePreservingMetadata(tab.FilePath);
                EndAgentTrackedSave(agentSave, tab);
                tab.SavedContent = tab.Content;
                tab.IsDirty = false;
                tab.IsPreviewTab = false;
                tab.RefreshGutterDiff();
                RefreshTabStrip();
                RefreshOpenEditorsSection();
                RefreshGitStatus();
                UpdateWindowTitle();
                SetStatusMessage($"Saved {tab.FileName}");
                LspOnSaved(tab);
                return true;
            }
            catch (Exception ex)
            {
                SetStatusMessage("Save error: " + ex.Message);
                return false;
            }
        }

        public void RevertActiveTab()
        {
            if (activeTab == null || string.IsNullOrEmpty(activeTab.FilePath) || !File.Exists(activeTab.FilePath)) return;
            if (activeTab.IsLargeFileViewer) return;
            activeTab.PushUndoSnapshot();
            string disk = File.ReadAllText(activeTab.FilePath);
            activeTab.Content = disk;
            activeTab.SavedContent = disk;
            activeTab.SyncLinesFromContent();
            activeTab.IsDirty = false;
            activeTab.RefreshGutterDiff();
            PrimaryEditor.LoadBuffer(activeTab);
            RefreshTabStrip();
            RefreshOpenEditorsSection();
            UpdateWindowTitle();
            LspOnBufferModified(activeTab);
        }

        private void OnEditorBufferModified(EditorTabBuffer buf)
        {
            if (buf == null) return;
            if (buf.IsDirty && UntitledFileDefaults.IsEmptyUntitled(buf)) buf.IsDirty = false; // empty untitled: nothing to save (VS Code)
            LspOnBufferModified(buf);
            bool previewChanged = false;
            if (buf.IsDirty && buf.IsPreviewTab)
            {
                buf.IsPreviewTab = false;
                previewChanged = true;
            }

            bool tabMetaChanged = previewChanged ||
                                  !ReferenceEquals(lastRenderedTabRef, activeTab) ||
                                  lastRenderedTabDirty != buf.IsDirty ||
                                  lastRenderedTabPreview != buf.IsPreviewTab ||
                                  lastRenderedOpenTabCount != openTabs.Count;

            if (tabMetaChanged)
            {
                RefreshTabStrip();
                RefreshOpenEditorsSection();
                UpdateWindowTitle();
                lastRenderedTabRef = activeTab;
                lastRenderedTabDirty = buf.IsDirty;
                lastRenderedTabPreview = buf.IsPreviewTab;
                lastRenderedOpenTabCount = openTabs.Count;
            }

            RefreshChangesetBanner();
            UpdateStatusBar();

            // For large files on live desktop, coalesce Outline/Problems/AutoSave/GutterDiff rebuilds via 280ms idle debounce
            if (buf.Lines != null && buf.Lines.Count > AvaloniaVirtualEditorCanvas.DeferAnalysisLineThreshold)
            {
                pendingDebouncedBuffer = buf;
                if (debouncedUiSyncTimer == null)
                {
                    debouncedUiSyncTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(280) };
                    debouncedUiSyncTimer.Tick += (s, e) =>
                    {
                        debouncedUiSyncTimer.Stop();
                        var target = pendingDebouncedBuffer;
                        pendingDebouncedBuffer = null;
                        if (target == null) return;
                        if (settings.AutoSave && !string.IsNullOrEmpty(target.FilePath) && File.Exists(target.FilePath))
                            ScheduleAutoSave(target);
                        if (!target.IsLargeFile)
                            target.RefreshGutterDiff();
                        if (ReferenceEquals(target, activeTab))
                            PrimaryEditor?.Canvas?.InvalidateVisual(); // gutter bars now; outline / squiggles when analyzed
                        // the AST / diagnostics pass runs on the thread pool; a result for an outdated version is dropped (QA #35)
                        if (!target.IsLargeFile)
                            AnalyzeBufferSoon(target, OnBufferAnalyzed);
                    };
                }
                debouncedUiSyncTimer.Stop();
                debouncedUiSyncTimer.Start();
            }
            else
            {
                if (settings.AutoSave && !string.IsNullOrEmpty(buf.FilePath) && File.Exists(buf.FilePath))
                    ScheduleAutoSave(buf);
                RefreshOutlineAndProblems(buf);
            }
        }

        private void RefreshChangesetBanner()
        {
            int pendingFiles = openTabs.Count(t => t.HasPendingDiff);
            if (pendingFiles > 0)
            {
                changesetSummaryText.Text = pendingFiles == 1 ? "1 file pending review" : $"{pendingFiles} files pending review";
                multiFileChangesetBar.IsVisible = true;
            }
            else
            {
                multiFileChangesetBar.IsVisible = false;
            }
            RefreshAgentTrustBanner();
        }

        public void RefreshTabStrip()
        {
            if (tabStripStack == null || currentTheme == null) return;
            tabStripStack.Children.Clear();

            Color activeBg = AvaloniaColorHelper.ToAvColor(currentTheme.ActiveTabBg);
            Color inactiveBg = AvaloniaColorHelper.ToAvColor(currentTheme.TabBarBg);
            Color textPrimary = AvaloniaColorHelper.ToAvColor(currentTheme.TextPrimary);
            Color textSecondary = AvaloniaColorHelper.ToAvColor(currentTheme.TextSecondary);
            Color accent = AvaloniaColorHelper.ToAvColor(currentTheme.AccentPrimary);
            Color borderCol = AvaloniaColorHelper.ToAvColor(currentTheme.BorderSubtle);

            lastRenderedTabRef = activeTab;
            lastRenderedTabDirty = activeTab?.IsDirty ?? false;
            lastRenderedTabPreview = activeTab?.IsPreviewTab ?? false;
            lastRenderedOpenTabCount = openTabs.Count;
            if (showWelcomeTab) // closable like VS Code: with no files open the editor area then shows the empty watermark
            {
                bool isWelcomeActive = (activeTab == null);
                var wRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Center };
                wRow.Children.Add(new VectorIconControl { IconName = "logo", Width = 13, Height = 13, IconColor = accent });
                wRow.Children.Add(new TextBlock
                {
                    Text = "Welcome",
                    FontSize = UiTokens.FontUi,
                    Foreground = new SolidColorBrush(isWelcomeActive ? textPrimary : textSecondary),
                    VerticalAlignment = VerticalAlignment.Center
                });
                var wClose = WorkbenchStyles.IconButton("close", "Close", CloseWelcomeTab, currentTheme, 20, 14);
                WorkbenchStyles.ThemeIconButton(wClose, currentTheme, isWelcomeActive);
                wRow.Children.Add(wClose);

                var wTabBorder = new Border
                {
                    Child = wRow,
                    Height = UiTokens.TabHeight,
                    Padding = new Thickness(12, 0, 6, 0),
                    Background = new SolidColorBrush(isWelcomeActive ? activeBg : inactiveBg),
                    BorderBrush = new SolidColorBrush(isWelcomeActive ? accent : borderCol),
                    BorderThickness = isWelcomeActive ? new Thickness(0, UiTokens.TabIndicatorWidth, 0, 0) : new Thickness(0, 0, 1, 1)
                };
                wTabBorder.PointerPressed += (s, e) => OpenWelcomeTab();
                tabStripStack.Children.Add(wTabBorder);
            }

            foreach (var tab in openTabs)
            {
                var captured = tab;
                bool isActive = ReferenceEquals(captured, activeTab);
                AvaloniaExplorerTree.GetFileTypeBadge(captured.FileName, out string badgeTxt, out Color badgeCol);

                var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, VerticalAlignment = VerticalAlignment.Center };
                row.Children.Add(new TextBlock
                {
                    Text = badgeTxt,
                    FontFamily = new FontFamily("Cascadia Code, Consolas, Menlo, monospace"),
                    FontWeight = FontWeight.Bold,
                    FontSize = 9.5,
                    Foreground = new SolidColorBrush(badgeCol),
                    VerticalAlignment = VerticalAlignment.Center
                });

                string labelText = (captured.IsPinned ? "◈ " : "") + captured.FileName;
                row.Children.Add(new TextBlock
                {
                    Text = labelText,
                    FontSize = UiTokens.FontUi,
                    FontStyle = captured.IsPreviewTab ? FontStyle.Italic : FontStyle.Normal,
                    FontWeight = FontWeight.Normal,
                    Foreground = new SolidColorBrush(isActive ? textPrimary : textSecondary),
                    VerticalAlignment = VerticalAlignment.Center
                });

                if (captured.IsDirty)
                {
                    row.Children.Add(new TextBlock
                    {
                        Text = "●",
                        FontSize = 11,
                        Foreground = new SolidColorBrush(accent),
                        VerticalAlignment = VerticalAlignment.Center,
                        Margin = new Thickness(2, 0, 0, 0)
                    });
                }

                // VS Code: the close glyph shows on the active tab and on hover; a dirty tab shows its dot instead.
                var closeBtn = WorkbenchStyles.IconButton("close", "Close (Ctrl+W)", () => CloseTab(captured), currentTheme, 20, 14);
                WorkbenchStyles.ThemeIconButton(closeBtn, currentTheme, isActive);
                closeBtn.Opacity = isActive ? 1 : 0;
                row.Children.Add(closeBtn);

                var tabBorder = new Border
                {
                    Child = row,
                    Height = UiTokens.TabHeight,
                    Padding = new Thickness(12, 0, 6, 0),
                    Background = new SolidColorBrush(isActive ? activeBg : inactiveBg),
                    BorderBrush = new SolidColorBrush(isActive ? accent : borderCol),
                    BorderThickness = isActive ? new Thickness(0, UiTokens.TabIndicatorWidth, 0, 0) : new Thickness(0, 0, 1, 1)
                };
                var tabHoverBrush = new SolidColorBrush(AvaloniaColorHelper.ToAvColor(UiTokens.Hover(currentTheme, currentTheme.TabBarBg)));
                tabBorder.PointerEntered += (s, e) => { closeBtn.Opacity = 1; if (!isActive) tabBorder.Background = tabHoverBrush; };
                tabBorder.PointerExited += (s, e) => { closeBtn.Opacity = isActive ? 1 : 0; if (!isActive) tabBorder.Background = new SolidColorBrush(inactiveBg); };
                tabBorder.PointerPressed += (s, e) =>
                {
                    var props = e.GetCurrentPoint(tabBorder).Properties;
                    if (props.IsRightButtonPressed)
                    {
                        OpenTabContextMenu(captured, tabBorder);
                        e.Handled = true;
                    }
                    else if (props.IsMiddleButtonPressed)
                    {
                        CloseTab(captured);
                        e.Handled = true;
                    }
                    else
                    {
                        if (e.ClickCount >= 2 && captured.IsPreviewTab)
                            captured.IsPreviewTab = false;
                        ActivateTab(captured);
                    }
                };
                tabStripStack.Children.Add(tabBorder);
            }
        }

        public void RefreshOpenEditorsSection()
        {
            if (openEditorsStack == null || currentTheme == null) return;
            openEditorsStack.Children.Clear();
            openEditorsHeaderBtn.Content = new SectionHeaderLabel((openEditorsStack.IsVisible ? "▾ " : "▸ ") + $"OPEN EDITORS ({openTabs.Count})");

            Color textPrimary = AvaloniaColorHelper.ToAvColor(currentTheme.TextPrimary);
            Color textSecondary = AvaloniaColorHelper.ToAvColor(currentTheme.TextSecondary);
            Color accent = AvaloniaColorHelper.ToAvColor(currentTheme.AccentPrimary);

            foreach (var tab in openTabs)
            {
                var captured = tab;
                bool isActive = ReferenceEquals(captured, activeTab);
                var rowGrid = new Grid
                {
                    ColumnDefinitions = new ColumnDefinitions("18,*,Auto"),
                    Height = 22,
                    Margin = new Thickness(8, 0, 6, 0)
                };
                rowGrid.Children.Add(new TextBlock
                {
                    Text = captured.IsDirty ? "●" : "✕",
                    FontSize = 10,
                    Foreground = new SolidColorBrush(captured.IsDirty ? accent : textSecondary),
                    VerticalAlignment = VerticalAlignment.Center
                });
                var nameTb = new TextBlock
                {
                    Text = captured.FileName,
                    FontSize = 11.5,
                    FontStyle = captured.IsPreviewTab ? FontStyle.Italic : FontStyle.Normal,
                    FontWeight = isActive ? FontWeight.SemiBold : FontWeight.Normal,
                    Foreground = new SolidColorBrush(isActive ? textPrimary : textSecondary),
                    VerticalAlignment = VerticalAlignment.Center
                };
                Grid.SetColumn(nameTb, 1);
                rowGrid.Children.Add(nameTb);

                var btn = new Button
                {
                    Content = rowGrid,
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                    HorizontalContentAlignment = HorizontalAlignment.Stretch,
                    Padding = new Thickness(0),
                    Height = 22,
                    Background = isActive
                        ? new SolidColorBrush(Color.FromArgb(40, accent.R, accent.G, accent.B))
                        : Brushes.Transparent
                };
                btn.Click += (s, e) => ActivateTab(captured);
                openEditorsStack.Children.Add(btn);
            }
        }

        public void RefreshOutlineAndProblems(EditorTabBuffer tab)
        {
            if (outlineListBox == null || problemsListBox == null || currentTheme == null) return;
            Color textPrimary = AvaloniaColorHelper.ToAvColor(currentTheme.TextPrimary);
            Color textSecondary = AvaloniaColorHelper.ToAvColor(currentTheme.TextSecondary);
            outlineListBox.Foreground = new SolidColorBrush(textPrimary);

            var symItems = new List<Control>();
            if (tab?.Symbols != null)
            {
                foreach (var s in tab.Symbols)
                {
                    symItems.Add(new LiteralTextBlock($"{s.Kind}  {s.Name}   :{s.LineNumber}")
                    {
                        FontSize = 11.5,
                        Foreground = new SolidColorBrush(textPrimary)
                    });
                }
            }
            if (symItems.Count == 0)
            {
                symItems.Add(new LiteralTextBlock("No symbols in active editor")
                {
                    FontSize = 11.5,
                    Foreground = new SolidColorBrush(textSecondary)
                });
            }
            outlineListBox.ItemsSource = symItems;

            var probItems = new List<Control>();
            int errCount = 0;
            int warnCount = 0;
            if (tab?.Diagnostics != null)
            {
                foreach (var d in tab.Diagnostics)
                {
                    bool isErr = string.Equals(d.Severity, "Error", StringComparison.OrdinalIgnoreCase);
                    if (isErr) errCount++; else warnCount++;
                    probItems.Add(new LiteralTextBlock($"{(isErr ? "⊗" : "△")} [{d.Severity}] {tab.FileName}:{d.LineNumber} — {d.Message}")
                    {
                        FontFamily = new FontFamily("Cascadia Code, Consolas, Menlo, monospace"),
                        FontSize = 11.5,
                        Foreground = new SolidColorBrush(isErr ? Color.FromRgb(248, 113, 113) : Color.FromRgb(250, 204, 21))
                    });
                }
            }
            AppendTaskProblemsForOtherFiles(probItems, tab, ref errCount, ref warnCount);
            if (probItems.Count == 0)
            {
                probItems.Add(new LiteralTextBlock("No problems have been detected in the workspace.")
                {
                    FontSize = 11.5,
                    Foreground = new SolidColorBrush(textSecondary),
                    Margin = new Thickness(8, 6)
                });
            }
            problemsListBox.ItemsSource = probItems;
            int totalProbs = errCount + warnCount;
            btmProblemsTabBtn.Content = new LiteralTextBlock($"PROBLEMS ({totalProbs})");
            SetStatusProblems(errCount, warnCount);
        }

        public void UpdateWindowTitle()
        {
            string folderName = !string.IsNullOrEmpty(workspaceRoot)
                ? Path.GetFileName(workspaceRoot.TrimEnd('\\', '/'))
                : "";

            if (activeTab != null)
            {
                string dirtyPrefix = activeTab.IsDirty ? "● " : "";
                Title = !string.IsNullOrEmpty(folderName)
                    ? $"{dirtyPrefix}{activeTab.FileName} — {folderName} — codeLess"
                    : $"{dirtyPrefix}{activeTab.FileName} — codeLess";
            }
            else if (!string.IsNullOrEmpty(folderName))
            {
                Title = showWelcomeTab ? $"Welcome — {folderName} — codeLess" : $"{folderName} — codeLess";
            }
            else
            {
                Title = "codeLess";
            }
        }

        public void UpdateStatusBar()
        {
            if (statusLnColBtn == null) return;
            if (activeTab != null)
            {
                statusLnColBtn.IsVisible = true;
                statusSpacesBtn.IsVisible = true;
                statusEncodingBtn.IsVisible = true;
                statusEolBtn.IsVisible = true;
                statusLanguageBtn.IsVisible = true;

                int carets = PrimaryEditor.Canvas.TotalCursorCount;
                string caretSuffix = carets > 1 ? $" ({carets} cursors)" : "";
                statusLnColBtn.Content = new LiteralTextBlock($"Ln {activeTab.CursorLine + 1}, Col {activeTab.CursorCol + 1}{caretSuffix}");
                statusSpacesBtn.Content = new LiteralTextBlock(StatusIndentationText);
                statusEncodingBtn.Content = new LiteralTextBlock(activeTab.HasBom ? "UTF-8 with BOM" : "UTF-8");
                statusEolBtn.Content = new LiteralTextBlock(activeTab.EolLabel);
                statusLanguageBtn.Content = new LiteralTextBlock(MapLanguageDisplayName(activeTab.Language));
            }
            else
            {
                statusLnColBtn.IsVisible = false;
                statusSpacesBtn.IsVisible = false;
                statusEncodingBtn.IsVisible = false;
                statusEolBtn.IsVisible = false;
                statusLanguageBtn.IsVisible = false;

                statusLnColBtn.Content = new LiteralTextBlock("");
                statusSpacesBtn.Content = new LiteralTextBlock("");
                statusEncodingBtn.Content = new LiteralTextBlock("");
                statusEolBtn.Content = new LiteralTextBlock("");
                statusLanguageBtn.Content = new LiteralTextBlock("");
            }
            statusAiModelBtn.Content = new LiteralTextBlock($"✦ {settings.ActiveModel}");
            UpdateDebugStatusItems();
            UpdateLspStatusItem();
        }

        public static string MapLanguageDisplayName(string ext)
        {
            switch ((ext ?? "").TrimStart('.').ToLowerInvariant())
            {
                case "cs": return "C#";
                case "py": return "Python";
                case "js": return "JavaScript";
                case "jsx": return "JavaScript React";
                case "ts": return "TypeScript";
                case "tsx": return "TypeScript React";
                case "rs": return "Rust";
                case "json": return "JSON";
                case "md": return "Markdown";
                case "ps1": return "PowerShell";
                case "sh":
                case "zsh": return "Shell Script";
                case "html": return "HTML";
                case "css": return "CSS";
                case "yaml":
                case "yml": return "YAML";
                default: return string.IsNullOrEmpty(ext) ? "Plain Text" : ext.ToUpperInvariant();
            }
        }

        public void SetStatusMessage(string msg)
        {
            ApplyStatusMessage(msg);
        }

        public void AppendOutputLog(string line)
        {
            if (outputStreamBox == null) return;
            outputStreamBox.Text += (line ?? "") + "\n";
        }
    }
}
