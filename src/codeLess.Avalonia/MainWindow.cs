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
            if (string.IsNullOrWhiteSpace(settings.ThemeId))
                settings.ThemeId = "codeless-obsidian";
            currentTheme = StudioTheme.GetById(settings.ThemeId);
            aiEngine = new LlmHarnessAndAgentEngine(settings);

            BuildWorkbenchLayout();
            BuildNativeMacMenu();
            ApplyTheme(currentTheme);

            // Register tunnel & bubble key handlers so workbench shortcuts work anywhere
            AddHandler(KeyDownEvent, OnWindowKeyDown, RoutingStrategies.Tunnel);

            string startPath = initialPath;
            if (string.IsNullOrWhiteSpace(startPath))
            {
                if (settings.RecentWorkspaces != null && settings.RecentWorkspaces.Count > 0 && Directory.Exists(settings.RecentWorkspaces[0]))
                    startPath = settings.RecentWorkspaces[0];
                else
                    startPath = Environment.CurrentDirectory;
            }

            if (!string.IsNullOrWhiteSpace(startPath))
            {
                if (File.Exists(startPath))
                {
                    string dir = Path.GetDirectoryName(Path.GetFullPath(startPath));
                    if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir))
                        LoadWorkspace(dir);
                    OpenFileInTab(Path.GetFullPath(startPath), preview: false);
                }
                else if (Directory.Exists(startPath))
                {
                    LoadWorkspace(Path.GetFullPath(startPath));
                }
            }

            EnsureInitialTerminalSession();
            UpdateWindowTitle();
            UpdateStatusBar();
        }

        protected override void OnClosed(EventArgs e)
        {
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
                    Path.Combine(Environment.CurrentDirectory, "assets", "savatarr_bolt.png"),
                    Path.Combine(Environment.CurrentDirectory, "codeLess", "assets", "savatarr_bolt.png"),
                    Path.Combine(AppContext.BaseDirectory, "assets", "savatarr_logo_source.png"),
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
                RowDefinitions = new RowDefinitions("34,*,24")
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
                Width = 13,
                Height = 13,
                IconColor = Color.FromRgb(148, 163, 184)
            });
            commandCenterText = new TextBlock
            {
                Text = "codeLess — Search files (Ctrl+P) or >commands (Ctrl+Shift+P)",
                FontSize = 11.5,
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
                Padding = new Thickness(14, 2),
                CornerRadius = new CornerRadius(5)
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
                var ic = new VectorIconControl { IconName = iconName, Width = 16, Height = 16 };
                var b = new Button
                {
                    Content = ic,
                    Width = 28,
                    Height = 26,
                    Padding = new Thickness(0),
                    Background = Brushes.Transparent
                };
                ToolTip.SetTip(b, tip);
                b.Click += (s, e) => onClick?.Invoke();
                return b;
            }

            rightIcons.Children.Add(MakeLayoutBtn("layout-left", "Toggle Primary Side Bar (Ctrl+B)", TogglePrimarySidebar));
            rightIcons.Children.Add(MakeLayoutBtn("layout-bottom", "Toggle Panel (Ctrl+J)", ToggleBottomPanel));
            rightIcons.Children.Add(MakeLayoutBtn("layout-right", "Toggle Secondary Side Bar / AI Chat (Ctrl+Alt+I)", ToggleRightChatDock));

            if (!RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            {
                Button MakeWinCaptionBtn(string glyph, string tip, Action onClick)
                {
                    var b = new Button
                    {
                        Content = glyph,
                        FontSize = 11,
                        Width = 34,
                        Height = 28,
                        HorizontalContentAlignment = HorizontalAlignment.Center,
                        VerticalContentAlignment = VerticalAlignment.Center,
                        Padding = new Thickness(0),
                        Background = Brushes.Transparent
                    };
                    ToolTip.SetTip(b, tip);
                    b.Click += (s, e) => onClick?.Invoke();
                    return b;
                }

                rightIcons.Children.Add(MakeWinCaptionBtn("—", "Minimize", () => WindowState = WindowState.Minimized));
                rightIcons.Children.Add(MakeWinCaptionBtn("□", "Maximize / Restore", () =>
                    WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized));
                rightIcons.Children.Add(MakeWinCaptionBtn("✕", "Close (Alt+F4)", Close));
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

        private Menu BuildTopMenuControl()
        {
            string Sc(string winKey) => AvaloniaCommandPaletteOverlay.FormatShortcutForPlatform(winKey);

            MenuItem Mi(string header, string shortcut, Action action)
            {
                string text = string.IsNullOrEmpty(shortcut) ? header : $"{header}    ({Sc(shortcut)})";
                var item = new MenuItem
                {
                    Header = new LiteralTextBlock(text)
                };
                if (action != null)
                    item.Click += (s, e) => action();
                return item;
            }

            var fileMenu = new MenuItem
            {
                Header = "_File",
                ItemsSource = new object[]
                {
                    Mi("New Text File", "Ctrl+N", () => CreateNewUntitledTab()),
                    Mi("New File in Workspace...", "Ctrl+Alt+N", PromptCreateNewFileInWorkspace),
                    Mi("New Folder in Workspace...", "", PromptCreateNewFolderInWorkspace),
                    new Separator(),
                    Mi("Open File...", "Ctrl+O", () => _ = PromptOpenFileDialogAsync()),
                    Mi("Open Folder...", "Ctrl+K Ctrl+O", () => _ = PromptOpenFolderDialogAsync()),
                    Mi("Add Folder to Workspace...", "", () => _ = PromptAddFolderToWorkspaceAsync()),
                    new Separator(),
                    Mi("Save", "Ctrl+S", () => SaveActiveTab()),
                    Mi("Save As...", "Ctrl+Shift+S", () => _ = PromptSaveAsAsync()),
                    Mi("Save All", "Ctrl+K S", SaveAllTabs),
                    new Separator(),
                    Mi("Auto Save", "", () => { settings.AutoSave = !settings.AutoSave; SetStatusMessage("Auto Save: " + (settings.AutoSave ? "On" : "Off")); }),
                    Mi("Preferences: Settings", "Ctrl+,", ShowSettingsModal),
                    Mi("Preferences: Keyboard Shortcuts", "Ctrl+K Ctrl+S", ShowKeyboardShortcutsModal),
                    Mi("Preferences: Color Theme", "Ctrl+K Ctrl+T", OpenThemePickerPalette),
                    new Separator(),
                    Mi("Revert File", "", RevertActiveTab),
                    Mi("Close Editor", "Ctrl+W", () => { if (activeTab != null) CloseTab(activeTab); else if (showWelcomeTab) CloseWelcomeTab(); }),
                    Mi("Close Folder", "Ctrl+K F", CloseWorkspaceFolder),
                    Mi("Exit", "Alt+F4", Close)
                }
            };

            var editMenu = new MenuItem
            {
                Header = "_Edit",
                ItemsSource = new object[]
                {
                    Mi("Undo", "Ctrl+Z", () => { if (activeTab != null) { activeTab.PerformUndo(); PrimaryEditor.Canvas.InvalidateVisual(); } }),
                    Mi("Redo", "Ctrl+Y", () => { if (activeTab != null) { activeTab.PerformRedo(); PrimaryEditor.Canvas.InvalidateVisual(); } }),
                    new Separator(),
                    Mi("Cut", "Ctrl+X", () => PrimaryEditor.Canvas.Focus()),
                    Mi("Copy", "Ctrl+C", () => PrimaryEditor.Canvas.Focus()),
                    Mi("Paste", "Ctrl+V", () => PrimaryEditor.Canvas.Focus()),
                    new Separator(),
                    Mi("Find", "Ctrl+F", () => PrimaryEditor.ShowFindBar(false)),
                    Mi("Replace", "Ctrl+H", () => PrimaryEditor.ShowFindBar(true)),
                    Mi("Find in Files", "Ctrl+Shift+F", () => SwitchSidebarView("search")),
                    Mi("Replace in Files", "Ctrl+Shift+H", () => SwitchSidebarView("search")),
                    new Separator(),
                    Mi("Toggle Line Comment", "Ctrl+/", () => PrimaryEditor.Canvas.ToggleLineComment()),
                    Mi("Toggle Block Comment", "Shift+Alt+A", () => PrimaryEditor.Canvas.ToggleBlockComment())
                }
            };

            var selectionMenu = new MenuItem
            {
                Header = "_Selection",
                ItemsSource = new object[]
                {
                    Mi("Select All", "Ctrl+A", () => { if (activeTab != null) { activeTab.SelectAll(); PrimaryEditor.Canvas.InvalidateVisual(); } }),
                    new Separator(),
                    Mi("Copy Line Up", "Shift+Alt+Up", () => PrimaryEditor.Canvas.CopyActiveLine(-1)),
                    Mi("Copy Line Down", "Shift+Alt+Down", () => PrimaryEditor.Canvas.CopyActiveLine(1)),
                    Mi("Move Line Up", "Alt+Up", () => PrimaryEditor.Canvas.MoveActiveLine(-1)),
                    Mi("Move Line Down", "Alt+Down", () => PrimaryEditor.Canvas.MoveActiveLine(1)),
                    Mi("Duplicate Selection / Line", "Ctrl+Shift+D", () => PrimaryEditor.Canvas.CopyActiveLine(1)),
                    new Separator(),
                    Mi("Add Cursor Above", "Ctrl+Alt+Up", () => PrimaryEditor.Canvas.AddCursorVertical(-1)),
                    Mi("Add Cursor Below", "Ctrl+Alt+Down", () => PrimaryEditor.Canvas.AddCursorVertical(1)),
                    Mi("Add Cursors to Line Ends", "Shift+Alt+I", () => PrimaryEditor.Canvas.AddCursorsToLineEnds()),
                    Mi("Add Next Occurrence", "Ctrl+D", () => PrimaryEditor.Canvas.AddNextOccurrenceOrSelectWord()),
                    Mi("Select All Occurrences", "Ctrl+Shift+L", () => PrimaryEditor.Canvas.SelectAllFindOccurrences()),
                    Mi("Column Selection Mode", "", () => { PrimaryEditor.ColumnSelectionMode = !PrimaryEditor.ColumnSelectionMode; SetStatusMessage("Column Selection Mode: " + (PrimaryEditor.ColumnSelectionMode ? "On" : "Off")); })
                }
            };

            var viewMenu = new MenuItem
            {
                Header = "_View",
                ItemsSource = new object[]
                {
                    Mi("Command Palette...", "Ctrl+Shift+P", () => OpenCommandPalette(">")),
                    new Separator(),
                    Mi("Explorer", "Ctrl+Shift+E", () => SwitchSidebarView("explorer")),
                    Mi("Search", "Ctrl+Shift+F", () => SwitchSidebarView("search")),
                    Mi("Source Control", "Ctrl+Shift+G", () => SwitchSidebarView("git")),
                    Mi("Run and Debug", "Ctrl+Shift+D", () => SwitchSidebarView("run")),
                    Mi("Extensions", "Ctrl+Shift+X", () => SwitchSidebarView("extensions")),
                    Mi("AI Chat & Composer", "Ctrl+Alt+I", ToggleRightChatDock),
                    new Separator(),
                    Mi("Problems", "Ctrl+Shift+M", () => SwitchBottomTab("problems")),
                    Mi("Output", "Ctrl+Shift+U", () => SwitchBottomTab("output")),
                    Mi("Terminal", "Ctrl+`", () => SwitchBottomTab("terminal")),
                    new Separator(),
                    Mi("Split Editor Right", "Ctrl+\\", ToggleSplitEditor),
                    Mi("Toggle Primary Side Bar", "Ctrl+B", TogglePrimarySidebar),
                    Mi("Toggle Panel", "Ctrl+J", ToggleBottomPanel),
                    Mi("Zen Mode", "Ctrl+K Z", ToggleZenMode),
                    Mi("Full Screen", "F11", ToggleFullScreenMode),
                    new Separator(),
                    Mi("Toggle Word Wrap", "Alt+Z", () => PrimaryEditor.Canvas.ToggleWordWrap()),
                    Mi("Toggle Minimap", "", () => { PrimaryEditor.ShowMinimap = !PrimaryEditor.ShowMinimap; settings.ShowMinimap = PrimaryEditor.ShowMinimap; }),
                    Mi("Toggle Breadcrumbs", "", () => PrimaryEditor.ShowBreadcrumbs = !PrimaryEditor.ShowBreadcrumbs),
                    Mi("Zoom In", "Ctrl+=", () => AdjustZoom(1)),
                    Mi("Zoom Out", "Ctrl+-", () => AdjustZoom(-1)),
                    Mi("Reset Zoom", "Ctrl+0", () => AdjustZoom(0))
                }
            };

            var goMenu = new MenuItem
            {
                Header = "_Go",
                ItemsSource = new object[]
                {
                    Mi("Back", "Alt+Left", NavigateBack),
                    Mi("Forward", "Alt+Right", NavigateForward),
                    new Separator(),
                    Mi("Go to File...", "Ctrl+P", OpenQuickOpen),
                    Mi("Go to Symbol in Editor...", "Ctrl+Shift+O", () => OpenCommandPalette("@")),
                    Mi("Go to Symbol in Workspace...", "Ctrl+T", () => OpenCommandPalette("#")),
                    Mi("Go to Definition", "F12", GoToDefinitionAtCursor),
                    Mi("Go to Line/Column...", "Ctrl+G", PromptGoToLineModal),
                    Mi("Go to Bracket", "Ctrl+Shift+\\", () => PrimaryEditor.Canvas.JumpToMatchingBracket()),
                    new Separator(),
                    Mi("Next Problem", "F8", () => JumpDiagnostic(1)),
                    Mi("Previous Problem", "Shift+F8", () => JumpDiagnostic(-1))
                }
            };

            var runMenu = new MenuItem
            {
                Header = "_Run",
                ItemsSource = new object[]
                {
                    Mi("Start Debugging / Run Active File", "F5", RunActiveFileInTerminal),
                    Mi("Run Without Debugging", "Ctrl+F5", RunActiveFileInTerminal),
                    Mi("Run Build Task", "Ctrl+Shift+B", RunWorkspaceBuildInTerminal),
                    Mi("Open Run & Debug View", "Ctrl+Shift+D", () => SwitchSidebarView("run"))
                }
            };

            var terminalMenu = new MenuItem
            {
                Header = "_Terminal",
                ItemsSource = new object[]
                {
                    Mi("New Terminal", "Ctrl+Shift+`", () => CreateNewTerminalSession()),
                    Mi("Split Terminal", "Ctrl+Shift+5", ToggleSplitTerminal),
                    new Separator(),
                    Mi("Inline Terminal AI Assistant...", "Ctrl+K", () => activeTerminalSession?.Control?.ShowInlineAiBar()),
                    Mi("Run Active File in Active Terminal", "", RunActiveFileInTerminal),
                    Mi("Clear Terminal", "", () => activeTerminalSession?.Control?.ClearTerminal()),
                    Mi("Kill Active Terminal", "", () => { if (activeTerminalSession != null) KillTerminalSession(activeTerminalSession); })
                }
            };

            var helpMenu = new MenuItem
            {
                Header = "_Help",
                ItemsSource = new object[]
                {
                    Mi("Welcome", "", OpenWelcomeTab),
                    Mi("Show All Commands", "Ctrl+Shift+P", () => OpenCommandPalette(">")),
                    Mi("Keyboard Shortcuts Reference", "Ctrl+K Ctrl+S", ShowKeyboardShortcutsModal),
                    Mi("BYOK AI Provider & Harness Setup", "", ShowByokAndHarnessModal),
                    new Separator(),
                    Mi("About codeLess", "", ShowAboutModal)
                }
            };

            return new Menu
            {
                VerticalAlignment = VerticalAlignment.Center,
                ItemsSource = new object[]
                {
                    fileMenu, editMenu, selectionMenu, viewMenu, goMenu, runMenu, terminalMenu, helpMenu
                }
            };
        }

        private void BuildNativeMacMenu()
        {
            try
            {
                var rootMenu = new NativeMenu();

                NativeMenuItem Nmi(string header, Action onClick, Key key = Key.None, KeyModifiers mods = KeyModifiers.None)
                {
                    var item = new NativeMenuItem(header);
                    if (key != Key.None)
                        item.Gesture = new KeyGesture(key, mods);
                    if (onClick != null)
                        item.Click += (s, e) => onClick();
                    return item;
                }

                var meta = RuntimeInformation.IsOSPlatform(OSPlatform.OSX) ? KeyModifiers.Meta : KeyModifiers.Control;

                var appSub = new NativeMenu
                {
                    Nmi("About codeLess", ShowAboutModal),
                    new NativeMenuItemSeparator(),
                    Nmi("Settings...", ShowSettingsModal, Key.OemComma, meta),
                    Nmi("Keyboard Shortcuts...", ShowKeyboardShortcutsModal),
                    new NativeMenuItemSeparator(),
                    Nmi("Quit codeLess", Close, Key.Q, meta)
                };
                rootMenu.Add(new NativeMenuItem("codeLess") { Menu = appSub });

                var fileSub = new NativeMenu
                {
                    Nmi("New File", () => CreateNewUntitledTab(), Key.N, meta),
                    Nmi("Open File...", () => _ = PromptOpenFileDialogAsync(), Key.O, meta),
                    Nmi("Open Folder...", () => _ = PromptOpenFolderDialogAsync()),
                    new NativeMenuItemSeparator(),
                    Nmi("Save", () => SaveActiveTab(), Key.S, meta),
                    Nmi("Save As...", () => _ = PromptSaveAsAsync(), Key.S, meta | KeyModifiers.Shift),
                    Nmi("Save All", SaveAllTabs),
                    new NativeMenuItemSeparator(),
                    Nmi("Close Editor", () => { if (activeTab != null) CloseTab(activeTab); }, Key.W, meta),
                    Nmi("Close Folder", CloseWorkspaceFolder)
                };
                rootMenu.Add(new NativeMenuItem("File") { Menu = fileSub });

                var editSub = new NativeMenu
                {
                    Nmi("Undo", () => activeTab?.PerformUndo(), Key.Z, meta),
                    Nmi("Redo", () => activeTab?.PerformRedo(), Key.Z, meta | KeyModifiers.Shift),
                    new NativeMenuItemSeparator(),
                    Nmi("Find", () => PrimaryEditor.ShowFindBar(false), Key.F, meta),
                    Nmi("Replace", () => PrimaryEditor.ShowFindBar(true), Key.F, meta | KeyModifiers.Alt),
                    Nmi("Toggle Line Comment", () => PrimaryEditor.Canvas.ToggleLineComment(), Key.OemQuestion, meta)
                };
                rootMenu.Add(new NativeMenuItem("Edit") { Menu = editSub });

                var selSub = new NativeMenu
                {
                    Nmi("Select All", () => activeTab?.SelectAll(), Key.A, meta),
                    Nmi("Add Next Occurrence", () => PrimaryEditor.Canvas.AddNextOccurrenceOrSelectWord(), Key.D, meta),
                    Nmi("Select All Occurrences", () => PrimaryEditor.Canvas.SelectAllFindOccurrences(), Key.L, meta | KeyModifiers.Shift),
                    Nmi("Add Cursor Above", () => PrimaryEditor.Canvas.AddCursorVertical(-1), Key.Up, meta | KeyModifiers.Alt),
                    Nmi("Add Cursor Below", () => PrimaryEditor.Canvas.AddCursorVertical(1), Key.Down, meta | KeyModifiers.Alt)
                };
                rootMenu.Add(new NativeMenuItem("Selection") { Menu = selSub });

                var viewSub = new NativeMenu
                {
                    Nmi("Command Palette...", () => OpenCommandPalette(">"), Key.P, meta | KeyModifiers.Shift),
                    new NativeMenuItemSeparator(),
                    Nmi("Explorer", () => SwitchSidebarView("explorer"), Key.E, meta | KeyModifiers.Shift),
                    Nmi("Search", () => SwitchSidebarView("search"), Key.F, meta | KeyModifiers.Shift),
                    Nmi("Source Control", () => SwitchSidebarView("git"), Key.G, meta | KeyModifiers.Shift),
                    Nmi("Run and Debug", () => SwitchSidebarView("run"), Key.D, meta | KeyModifiers.Shift),
                    Nmi("Extensions", () => SwitchSidebarView("extensions"), Key.X, meta | KeyModifiers.Shift),
                    new NativeMenuItemSeparator(),
                    Nmi("Toggle Primary Side Bar", TogglePrimarySidebar, Key.B, meta),
                    Nmi("Toggle Panel", ToggleBottomPanel, Key.J, meta),
                    Nmi("Toggle AI Chat", ToggleRightChatDock, Key.I, meta | KeyModifiers.Alt)
                };
                rootMenu.Add(new NativeMenuItem("View") { Menu = viewSub });

                var goSub = new NativeMenu
                {
                    Nmi("Go to File...", OpenQuickOpen, Key.P, meta),
                    Nmi("Go to Symbol in Editor...", () => OpenCommandPalette("@"), Key.O, meta | KeyModifiers.Shift),
                    Nmi("Go to Symbol in Workspace...", () => OpenCommandPalette("#"), Key.T, meta),
                    Nmi("Go to Line/Column...", PromptGoToLineModal, Key.G, meta)
                };
                rootMenu.Add(new NativeMenuItem("Go") { Menu = goSub });

                var runSub = new NativeMenu
                {
                    Nmi("Start Debugging", RunActiveFileInTerminal, Key.F5, KeyModifiers.None),
                    Nmi("Run Without Debugging", RunActiveFileInTerminal, Key.F5, KeyModifiers.Control)
                };
                rootMenu.Add(new NativeMenuItem("Run") { Menu = runSub });

                var termSub = new NativeMenu
                {
                    Nmi("New Terminal", () => CreateNewTerminalSession()),
                    Nmi("Split Terminal", ToggleSplitTerminal),
                    Nmi("Clear Terminal", () => activeTerminalSession?.Control?.ClearTerminal())
                };
                rootMenu.Add(new NativeMenuItem("Terminal") { Menu = termSub });

                var helpSub = new NativeMenu
                {
                    Nmi("Welcome", OpenWelcomeTab),
                    Nmi("Show All Commands", () => OpenCommandPalette(">")),
                    Nmi("Keyboard Shortcuts Reference", ShowKeyboardShortcutsModal),
                    Nmi("About codeLess", ShowAboutModal)
                };
                rootMenu.Add(new NativeMenuItem("Help") { Menu = helpSub });

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
                Spacing = 2,
                Margin = new Thickness(0, 4, 0, 0)
            };

            var bottomStack = new StackPanel
            {
                Spacing = 2,
                Margin = new Thickness(0, 0, 0, 6)
            };

            Button AddActivityIcon(StackPanel host, string id, string iconName, string tooltip, Action customAction = null)
            {
                var activeBar = new Border
                {
                    Width = 3,
                    Height = 24,
                    HorizontalAlignment = HorizontalAlignment.Left,
                    VerticalAlignment = VerticalAlignment.Center,
                    CornerRadius = new CornerRadius(1.5),
                    IsVisible = false
                };
                var iconCtrl = new VectorIconControl
                {
                    IconName = iconName,
                    Width = 22,
                    Height = 22,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center
                };
                var cellGrid = new Grid { Width = 46, Height = 42 };
                cellGrid.Children.Add(activeBar);
                cellGrid.Children.Add(iconCtrl);

                var btn = new Button
                {
                    Content = cellGrid,
                    Width = 48,
                    Height = 44,
                    Padding = new Thickness(0),
                    Background = Brushes.Transparent,
                    BorderThickness = new Thickness(0)
                };
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
                RowDefinitions = new RowDefinitions("32,2,*")
            };

            // Row 0: Sidebar Header + Action Icons
            var headerGrid = new Grid
            {
                ColumnDefinitions = new ColumnDefinitions("*,Auto"),
                Margin = new Thickness(12, 0, 6, 0)
            };
            sidebarHeaderTitle = new TextBlock
            {
                Text = "EXPLORER",
                FontSize = 11,
                FontWeight = FontWeight.Bold,
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
                var b = new Button
                {
                    Content = new VectorIconControl { IconName = iconName, Width = 15, Height = 15 },
                    Width = 24,
                    Height = 22,
                    Padding = new Thickness(0),
                    Background = Brushes.Transparent
                };
                ToolTip.SetTip(b, tooltip);
                b.Click += (s, e) => onClick?.Invoke();
                return b;
            }

            sidebarHeaderActions.Children.Add(MakeHeaderAction("new-file", "New File...", PromptCreateNewFileInWorkspace));
            sidebarHeaderActions.Children.Add(MakeHeaderAction("new-folder", "New Folder...", PromptCreateNewFolderInWorkspace));
            sidebarHeaderActions.Children.Add(MakeHeaderAction("refresh", "Refresh Explorer", () => { if (!string.IsNullOrEmpty(workspaceRoot)) LoadWorkspace(workspaceRoot, keepCurrentTabs: true); }));
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
            runDebugPanelView = BuildRunDebugSidebarView();
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
                Content = new LiteralTextBlock("▾ OPEN EDITORS"),
                FontSize = 10.5,
                FontWeight = FontWeight.Bold,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Left,
                Padding = new Thickness(8, 3),
                Background = Brushes.Transparent
            };
            openEditorsStack = new StackPanel { Spacing = 1, Margin = new Thickness(0, 0, 0, 4) };
            openEditorsHeaderBtn.Click += (s, e) =>
            {
                openEditorsStack.IsVisible = !openEditorsStack.IsVisible;
                openEditorsHeaderBtn.Content = new LiteralTextBlock((openEditorsStack.IsVisible ? "▾ " : "▸ ") + $"OPEN EDITORS ({openTabs.Count})");
            };
            Grid.SetRow(openEditorsHeaderBtn, 0);
            grid.Children.Add(openEditorsHeaderBtn);
            Grid.SetRow(openEditorsStack, 1);
            grid.Children.Add(openEditorsStack);

            // Section 2: WORKSPACE FOLDER HEADER
            workspaceFolderHeaderBtn = new Button
            {
                Content = new LiteralTextBlock("▾ WORKSPACE"),
                FontSize = 10.5,
                FontWeight = FontWeight.Bold,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Left,
                Padding = new Thickness(8, 3),
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
                Content = new LiteralTextBlock("▸ OUTLINE"),
                FontSize = 10.5,
                FontWeight = FontWeight.Bold,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Left,
                Padding = new Thickness(8, 3),
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
                outlineHeaderBtn.Content = new LiteralTextBlock((outlineListBox.IsVisible ? "▾ " : "▸ ") + "OUTLINE");
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

        private Grid BuildSearchSidebarView()
        {
            var grid = new Grid
            {
                RowDefinitions = new RowDefinitions("Auto,*"),
                Margin = new Thickness(8, 6),
                IsVisible = false
            };

            var topStack = new StackPanel { Spacing = 6 };
            workspaceSearchInput = new TextBox { Watermark = "Search in workspace files...", FontSize = 12, Padding = new Thickness(7, 4) };
            workspaceReplaceInput = new TextBox { Watermark = "Replace in workspace files...", FontSize = 12, Padding = new Thickness(7, 4) };

            var toggleRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
            wsMatchCaseBtn = new Button { Content = "Aa", FontSize = 10.5, Padding = new Thickness(6, 2) };
            wsWholeWordBtn = new Button { Content = "ab", FontSize = 10.5, Padding = new Thickness(6, 2) };
            wsRegexBtn = new Button { Content = ".*", FontSize = 10.5, Padding = new Thickness(6, 2) };
            var searchBtn = new Button { Content = "Search", FontSize = 11, Padding = new Thickness(10, 3) };
            var replaceAllBtn = new Button { Content = "Replace All", FontSize = 11, Padding = new Thickness(10, 3) };

            wsMatchCaseBtn.Click += (s, e) => { wsSearchMatchCase = !wsSearchMatchCase; ExecuteWorkspaceSearch(); };
            wsWholeWordBtn.Click += (s, e) => { wsSearchWholeWord = !wsSearchWholeWord; ExecuteWorkspaceSearch(); };
            wsRegexBtn.Click += (s, e) => { wsSearchRegex = !wsSearchRegex; ExecuteWorkspaceSearch(); };
            searchBtn.Click += (s, e) => ExecuteWorkspaceSearch();
            replaceAllBtn.Click += (s, e) => ExecuteWorkspaceReplaceAll();
            workspaceSearchInput.KeyDown += (s, e) => { if (e.Key == Key.Enter) { ExecuteWorkspaceSearch(); e.Handled = true; } };

            toggleRow.Children.Add(wsMatchCaseBtn);
            toggleRow.Children.Add(wsWholeWordBtn);
            toggleRow.Children.Add(wsRegexBtn);
            toggleRow.Children.Add(searchBtn);
            toggleRow.Children.Add(replaceAllBtn);

            workspaceSearchSummary = new TextBlock { Text = "Type a query and press Enter to search across files.", FontSize = 11 };

            topStack.Children.Add(workspaceSearchInput);
            topStack.Children.Add(workspaceReplaceInput);
            topStack.Children.Add(toggleRow);
            topStack.Children.Add(workspaceSearchSummary);
            Grid.SetRow(topStack, 0);
            grid.Children.Add(topStack);

            workspaceSearchResultsList = new ListBox
            {
                Margin = new Thickness(0, 6, 0, 0),
                BorderThickness = new Thickness(0),
                Background = Brushes.Transparent
            };
            workspaceSearchResultsList.SelectionChanged += (s, e) =>
            {
                int idx = workspaceSearchResultsList.SelectedIndex;
                if (idx >= 0 && idx < workspaceSearchHits.Count)
                {
                    var hit = workspaceSearchHits[idx];
                    OpenFileInTab(hit.FilePath, preview: false);
                    PrimaryEditor.GoToLine(hit.LineNumber);
                }
            };
            Grid.SetRow(workspaceSearchResultsList, 1);
            grid.Children.Add(workspaceSearchResultsList);

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
            scmBranchBanner = new TextBlock { Text = "Branch: main", FontSize = 11.5, FontWeight = FontWeight.SemiBold };
            scmCommitInput = new TextBox
            {
                Watermark = "Message (Ctrl+Enter to commit on 'main')",
                AcceptsReturn = true,
                Height = 54,
                FontSize = 12,
                Padding = new Thickness(7, 5)
            };

            var btnRow = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto,Auto") };
            var commitBtn = new Button
            {
                Content = "✓ Commit",
                FontWeight = FontWeight.SemiBold,
                FontSize = 11.5,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Center,
                Padding = new Thickness(8, 4),
                Background = new SolidColorBrush(Color.FromRgb(59, 130, 246)),
                Foreground = Brushes.White
            };
            var stageAllBtn = new Button { Content = "+ All", FontSize = 11, Padding = new Thickness(7, 4), Margin = new Thickness(4, 0, 0, 0) };
            var unstageAllBtn = new Button { Content = "- All", FontSize = 11, Padding = new Thickness(7, 4), Margin = new Thickness(4, 0, 0, 0) };
            var refreshGitBtn = new Button { Content = "↻", FontSize = 11, Padding = new Thickness(7, 4), Margin = new Thickness(4, 0, 0, 0) };

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
            scmStagedHeader = new TextBlock { Text = "▾ STAGED CHANGES (0)", FontSize = 10.5, FontWeight = FontWeight.Bold };
            scmStagedListStack = new StackPanel { Spacing = 2 };
            scmChangesHeader = new TextBlock { Text = "▾ CHANGES (0)", FontSize = 10.5, FontWeight = FontWeight.Bold, Margin = new Thickness(0, 6, 0, 0) };
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

        private Grid BuildRunDebugSidebarView()
        {
            var grid = new Grid
            {
                RowDefinitions = new RowDefinitions("Auto,*"),
                Margin = new Thickness(10, 8),
                IsVisible = false
            };

            var stack = new StackPanel { Spacing = 8 };
            runConfigCombo = new ComboBox
            {
                ItemsSource = new[]
                {
                    "Polyglot Runner (Active File)",
                    ".NET Build & Run (dotnet run)",
                    "Verification Suite (dotnet test)"
                },
                SelectedIndex = 0,
                HorizontalAlignment = HorizontalAlignment.Stretch
            };

            var startRunBtn = new Button
            {
                Content = "▶  Start Debugging / Run (F5)",
                FontWeight = FontWeight.SemiBold,
                FontSize = 12,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Center,
                Padding = new Thickness(10, 6),
                Background = new SolidColorBrush(Color.FromRgb(22, 163, 74)),
                Foreground = Brushes.White
            };
            startRunBtn.Click += (s, e) => RunActiveFileInTerminal();

            var buildBtn = new Button
            {
                Content = "⚙  Run Workspace Build Task (Ctrl+Shift+B)",
                FontSize = 11.5,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                Padding = new Thickness(10, 5)
            };
            buildBtn.Click += (s, e) => RunWorkspaceBuildInTerminal();

            runStatusSummary = new TextBlock
            {
                Text = "Ready. Press F5 or click Start to execute in the integrated VT100 terminal.",
                FontSize = 11.5,
                TextWrapping = TextWrapping.Wrap
            };

            stack.Children.Add(new TextBlock { Text = "LAUNCH CONFIGURATION", FontSize = 10.5, FontWeight = FontWeight.Bold });
            stack.Children.Add(runConfigCombo);
            stack.Children.Add(startRunBtn);
            stack.Children.Add(buildBtn);
            stack.Children.Add(runStatusSummary);

            Grid.SetRow(stack, 0);
            grid.Children.Add(stack);
            return grid;
        }

        private Grid BuildExtensionsSidebarView()
        {
            var grid = new Grid
            {
                RowDefinitions = new RowDefinitions("Auto,*"),
                Margin = new Thickness(8, 6),
                IsVisible = false
            };

            var topStack = new StackPanel { Spacing = 6 };
            extensionsSearchInput = new TextBox { Watermark = "Search Extensions in OpenVSX Marketplace...", FontSize = 12, Padding = new Thickness(7, 4) };
            topStack.Children.Add(extensionsSearchInput);
            Grid.SetRow(topStack, 0);
            grid.Children.Add(topStack);

            extensionsCardsStack = new StackPanel { Spacing = 6, Margin = new Thickness(0, 8, 0, 0) };
            var builtInExts = new (string Name, string Pub, string Desc)[]
            {
                ("C# & .NET Polyglot Kit", "savatarr", "AST symbols, diagnostics, and Roslyn syntax navigation."),
                ("Python & FastAPI Tools", "savatarr", "Indentation folding, symbol outline, and pytest integration."),
                ("TypeScript & React ESNext", "savatarr", "TS/JS tokenizer, bracket matching, and npm task runner."),
                ("Rust Analyzer Lite", "savatarr", "Cargo diagnostics and crate symbol indexer."),
                ("GitLens Diff & Gutter", "savatarr", "Line-accurate HEAD gutter diff bars and stage/unstage workflow.")
            };
            foreach (var ext in builtInExts)
            {
                var card = new StackPanel { Spacing = 2 };
                card.Children.Add(new TextBlock { Text = ext.Name + "  ✓ Installed", FontWeight = FontWeight.Bold, FontSize = 12 });
                card.Children.Add(new TextBlock { Text = ext.Desc, FontSize = 11, TextWrapping = TextWrapping.Wrap });
                extensionsCardsStack.Children.Add(new Border
                {
                    Child = card,
                    CornerRadius = new CornerRadius(5),
                    BorderThickness = new Thickness(1),
                    Padding = new Thickness(8, 6)
                });
            }

            var scroll = new ScrollViewer { Content = extensionsCardsStack };
            Grid.SetRow(scroll, 1);
            grid.Children.Add(scroll);
            return grid;
        }

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
                Content = "⚙ Configure BYOK Providers & API Keys...",
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
            var splitBtn = new Button
            {
                Content = new VectorIconControl { IconName = "split-right", Width = 15, Height = 15 },
                Width = 26,
                Height = 24,
                Padding = new Thickness(0),
                Background = Brushes.Transparent
            };
            ToolTip.SetTip(splitBtn, AvaloniaCommandPaletteOverlay.FormatShortcutForPlatform("Split Editor Right (Ctrl+\\)"));
            splitBtn.Click += (s, e) => ToggleSplitEditor();
            tabRightActions.Children.Add(splitBtn);
            Grid.SetColumn(tabRightActions, 1);
            tabBarGrid.Children.Add(tabRightActions);

            tabStripBorder = new Border
            {
                Height = 35,
                Child = tabBarGrid,
                BorderThickness = new Thickness(0, 0, 0, 1)
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
            Grid.SetColumn(PrimaryEditor, 0);
            editorSplitGrid.Children.Add(PrimaryEditor);

            SplitEditor = new AvaloniaEditorControl();
            SplitEditor.BufferModified += OnEditorBufferModified;
            SplitEditor.CaretMoved += buf => UpdateStatusBar();
            splitEditorBorder = new Border
            {
                Child = SplitEditor,
                BorderThickness = new Thickness(1, 0, 0, 0),
                IsVisible = false
            };
            Grid.SetColumn(splitEditorBorder, 1);
            editorSplitGrid.Children.Add(splitEditorBorder);

            Grid.SetRow(editorSplitGrid, 1);
            centerGrid.Children.Add(editorSplitGrid);

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
                RowDefinitions = new RowDefinitions("30,*")
            };

            // Row 0: Panel Tab Header + Right Terminal Actions
            var headerGrid = new Grid
            {
                ColumnDefinitions = new ColumnDefinitions("*,Auto"),
                Margin = new Thickness(10, 0, 8, 0)
            };

            var tabsRow = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 12,
                VerticalAlignment = VerticalAlignment.Center
            };

            Button MakePanelTab(string label, string id, bool visibleByDefault = true)
            {
                var b = new Button
                {
                    Content = label,
                    FontSize = 11,
                    FontWeight = FontWeight.Bold,
                    Padding = new Thickness(4, 4),
                    Background = Brushes.Transparent,
                    BorderThickness = new Thickness(0, 0, 0, 2),
                    IsVisible = visibleByDefault
                };
                b.Click += (s, e) => SwitchBottomTab(id);
                return b;
            }

            btmProblemsTabBtn = MakePanelTab("PROBLEMS (0)", "problems");
            btmOutputTabBtn = MakePanelTab("OUTPUT", "output");
            btmTerminalTabBtn = MakePanelTab("TERMINAL", "terminal");
            // Secondary bottom tabs hidden by default
            btmDebugConsoleTabBtn = MakePanelTab("DEBUG CONSOLE", "debug", visibleByDefault: false);
            btmPortsTabBtn = MakePanelTab("PORTS", "ports", visibleByDefault: false);

            tabsRow.Children.Add(btmProblemsTabBtn);
            tabsRow.Children.Add(btmOutputTabBtn);
            tabsRow.Children.Add(btmTerminalTabBtn);
            tabsRow.Children.Add(btmDebugConsoleTabBtn);
            tabsRow.Children.Add(btmPortsTabBtn);
            headerGrid.Children.Add(tabsRow);

            // Right-side Terminal & Panel Actions
            terminalToolbarActions = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 4,
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
                Height = 23,
                FontSize = 11,
                Padding = new Thickness(6, 1)
            };

            var newTermBtn = new Button
            {
                Name = "NewTerminalBtn",
                Content = new VectorIconControl { IconName = "plus", Width = 14, Height = 14 },
                Width = 24,
                Height = 22,
                Padding = new Thickness(0),
                Background = Brushes.Transparent
            };
            ToolTip.SetTip(newTermBtn, "New Terminal (Ctrl+Shift+`)");
            newTermBtn.Click += (s, e) =>
            {
                string prof = shellProfileSelector.SelectedItem?.ToString();
                CreateNewTerminalSession(prof);
            };

            var splitTermBtn = new Button
            {
                Name = "SplitTerminalBtn",
                Content = new VectorIconControl { IconName = "split-right", Width = 14, Height = 14 },
                Width = 24,
                Height = 22,
                Padding = new Thickness(0),
                Background = Brushes.Transparent
            };
            ToolTip.SetTip(splitTermBtn, "Split Terminal (Ctrl+Shift+5)");
            splitTermBtn.Click += (s, e) => ToggleSplitTerminal();

            var renameTermBtn = new Button
            {
                Name = "RenameTerminalBtn",
                Content = "✎",
                FontSize = 11,
                Width = 24,
                Height = 22,
                Padding = new Thickness(0),
                Background = Brushes.Transparent
            };
            ToolTip.SetTip(renameTermBtn, "Rename Active Terminal Session...");
            renameTermBtn.Click += (s, e) => PromptRenameActiveTerminalSession();

            var killTermBtn = new Button
            {
                Name = "KillTerminalBtn",
                Content = new VectorIconControl { IconName = "trash", Width = 14, Height = 14 },
                Width = 24,
                Height = 22,
                Padding = new Thickness(0),
                Background = Brushes.Transparent
            };
            ToolTip.SetTip(killTermBtn, "Kill Active Terminal Session");
            killTermBtn.Click += (s, e) => { if (activeTerminalSession != null) KillTerminalSession(activeTerminalSession); };

            var maxPanelBtn = new Button
            {
                Content = new VectorIconControl { IconName = "maximize-up", Width = 14, Height = 14 },
                Width = 24,
                Height = 22,
                Padding = new Thickness(0),
                Background = Brushes.Transparent
            };
            ToolTip.SetTip(maxPanelBtn, "Maximize Panel Size");
            maxPanelBtn.Click += (s, e) => ToggleMaximizeBottomPanel();

            var closePanelBtn = new Button
            {
                Content = new VectorIconControl { IconName = "close", Width = 13, Height = 13 },
                Width = 24,
                Height = 22,
                Padding = new Thickness(0),
                Background = Brushes.Transparent
            };
            ToolTip.SetTip(closePanelBtn, "Close Panel (Ctrl+J)");
            closePanelBtn.Click += (s, e) => ToggleBottomPanel();

            outputChannelCombo = new ComboBox
            {
                ItemsSource = new[] { "codeLess Workbench", "AI Harness Engine", "Git Source Control", "Extension Host" },
                SelectedIndex = 0,
                Height = 23,
                FontSize = 11,
                IsVisible = false
            };

            terminalToolbarActions.Children.Add(outputChannelCombo);
            terminalToolbarActions.Children.Add(shellProfileSelector);
            terminalToolbarActions.Children.Add(newTermBtn);
            terminalToolbarActions.Children.Add(splitTermBtn);
            terminalToolbarActions.Children.Add(renameTermBtn);
            terminalToolbarActions.Children.Add(killTermBtn);
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
            };
            bodyDeck.Children.Add(problemsListBox);

            // Output Stream Box
            outputStreamBox = new TextBox
            {
                IsReadOnly = true,
                AcceptsReturn = true,
                FontFamily = new FontFamily("Cascadia Code, Consolas, Menlo, monospace"),
                FontSize = 11.5,
                BorderThickness = new Thickness(0),
                Text = $"[codeLess] Workbench initialized ({RuntimeInformation.OSDescription}, {RuntimeInformation.ProcessArchitecture}).\n",
                IsVisible = false
            };
            bodyDeck.Children.Add(outputStreamBox);

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
                Spacing = 2,
                Margin = new Thickness(4, 4)
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

        private Border BuildRightChatDock()
        {
            var grid = new Grid
            {
                RowDefinitions = new RowDefinitions("32,2,Auto,*,Auto")
            };

            // Row 0: Chat Header (New, History, and Close — matching WinForms)
            var headerGrid = new Grid
            {
                ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto,Auto"),
                Margin = new Thickness(12, 0, 6, 0)
            };
            headerGrid.Children.Add(new TextBlock
            {
                Text = "✦ AI AGENT & COMPOSER",
                FontSize = 11,
                FontWeight = FontWeight.Bold,
                VerticalAlignment = VerticalAlignment.Center
            });

            var newChatBtn = new Button { Content = "+ New", FontSize = 10.5, Padding = new Thickness(6, 2), Background = Brushes.Transparent };
            newChatBtn.Click += (s, e) => AgentFeed.ClearFeed();
            Grid.SetColumn(newChatBtn, 1);
            headerGrid.Children.Add(newChatBtn);

            var historyChatBtn = new Button { Content = "History", FontSize = 10.5, Padding = new Thickness(6, 2), Background = Brushes.Transparent };
            historyChatBtn.Click += (s, e) => SetStatusMessage($"Chat history: {AgentFeed.Items.Count} message(s) in current session");
            Grid.SetColumn(historyChatBtn, 2);
            headerGrid.Children.Add(historyChatBtn);

            var closeChatBtn = new Button
            {
                Content = new VectorIconControl { IconName = "close", Width = 12, Height = 12 },
                Width = 24,
                Height = 22,
                Padding = new Thickness(0),
                Background = Brushes.Transparent
            };
            closeChatBtn.Click += (s, e) => ToggleRightChatDock();
            Grid.SetColumn(closeChatBtn, 3);
            headerGrid.Children.Add(closeChatBtn);

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
                Margin = new Thickness(8, 4)
            };
            changesetSummaryText = new TextBlock
            {
                Text = "Δ 1 file pending",
                FontSize = 11,
                FontWeight = FontWeight.SemiBold,
                TextTrimming = TextTrimming.CharacterEllipsis,
                VerticalAlignment = VerticalAlignment.Center
            };
            var csAcceptBtn = new Button
            {
                Content = "✓ Accept All",
                FontSize = 10.5,
                Padding = new Thickness(7, 2),
                Margin = new Thickness(4, 0),
                Background = new SolidColorBrush(Color.FromRgb(22, 163, 74)),
                Foreground = Brushes.White
            };
            var csRejectBtn = new Button
            {
                Content = "✕ Reject All",
                FontSize = 10.5,
                Padding = new Thickness(7, 2),
                Background = new SolidColorBrush(Color.FromRgb(185, 28, 28)),
                Foreground = Brushes.White
            };
            csAcceptBtn.Click += (s, e) => PrimaryEditor.AcceptAllPendingDiffs();
            csRejectBtn.Click += (s, e) => PrimaryEditor.RejectAllPendingDiffs();
            csGrid.Children.Add(changesetSummaryText);
            Grid.SetColumn(csAcceptBtn, 1); csGrid.Children.Add(csAcceptBtn);
            Grid.SetColumn(csRejectBtn, 2); csGrid.Children.Add(csRejectBtn);

            multiFileChangesetBar = new Border
            {
                Child = csGrid,
                Margin = new Thickness(8, 2, 8, 2),
                CornerRadius = new CornerRadius(5),
                BorderThickness = new Thickness(1),
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
            var composerStack = new StackPanel { Spacing = 6 };

            // Context pills row
            contextPillsWrap = new WrapPanel { Orientation = Orientation.Horizontal };
            RebuildContextPillsBar();
            composerStack.Children.Add(contextPillsWrap);

            // Prompt TextBox
            chatPromptInput = new TextBox
            {
                Name = "ChatPromptInput",
                Watermark = "Plan, @context, ask questions, or request multi-file edits (Enter to send)...",
                AcceptsReturn = false,
                Height = 52,
                FontSize = 12,
                TextWrapping = TextWrapping.Wrap,
                Padding = new Thickness(8, 6)
            };
            chatPromptInput.KeyDown += (s, e) =>
            {
                if (e.Key == Key.Enter && !e.KeyModifiers.HasFlag(KeyModifiers.Shift))
                {
                    SubmitChatMessage();
                    e.Handled = true;
                }
            };
            composerStack.Children.Add(chatPromptInput);

            // Mode pills row: ✦ Agent | Ask | Edit | Auto-Fix
            var modeRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
            foreach (var m in new (string Id, string Label)[] { ("agent", "✦ Agent"), ("ask", "Ask"), ("edit", "Edit"), ("autofix", "Auto-Fix") })
            {
                string modeId = m.Id;
                var mb = new Button
                {
                    Content = m.Label,
                    FontSize = 10.5,
                    Padding = new Thickness(7, 2)
                };
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
                FontSize = 10.5,
                Height = 25,
                Width = 144,
                Padding = new Thickness(6, 1)
            };
            chatModelCombo = new ComboBox
            {
                FontSize = 10.5,
                Height = 25,
                Margin = new Thickness(4, 0),
                HorizontalAlignment = HorizontalAlignment.Stretch,
                Padding = new Thickness(6, 1)
            };
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

            chatSendBtn = new Button
            {
                Name = "ChatSendBtn",
                Content = "Send ↑",
                FontSize = 11.5,
                FontWeight = FontWeight.SemiBold,
                Padding = new Thickness(12, 4),
                Background = new SolidColorBrush(Color.FromRgb(59, 130, 246)),
                Foreground = Brushes.White
            };
            chatSendBtn.Click += (s, e) => SubmitChatMessage();

            Grid.SetColumn(chatProviderCombo, 0); bottomRow.Children.Add(chatProviderCombo);
            Grid.SetColumn(chatModelCombo, 1); bottomRow.Children.Add(chatModelCombo);
            Grid.SetColumn(chatSendBtn, 2); bottomRow.Children.Add(chatSendBtn);
            composerStack.Children.Add(bottomRow);

            var composerBorder = new Border
            {
                Child = composerStack,
                Margin = new Thickness(8, 4, 8, 8),
                Padding = new Thickness(8, 8),
                CornerRadius = new CornerRadius(6),
                BorderThickness = new Thickness(1)
            };
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

            var addCtxBtn = new Button
            {
                Content = "+ @ Context",
                FontSize = 10.5,
                Padding = new Thickness(6, 1),
                Margin = new Thickness(0, 0, 4, 3)
            };
            addCtxBtn.Click += (s, e) =>
            {
                if (activeTab != null && !attachedContextPills.Contains(activeTab.FileName))
                {
                    attachedContextPills.Add(activeTab.FileName);
                    RebuildContextPillsBar();
                }
            };
            contextPillsWrap.Children.Add(addCtxBtn);

            string activePillText = activeTab != null ? $"📄 {activeTab.FileName}" : "🌐 @Workspace";
            contextPillsWrap.Children.Add(new Border
            {
                CornerRadius = new CornerRadius(3),
                BorderThickness = new Thickness(1),
                Padding = new Thickness(6, 1),
                Margin = new Thickness(0, 0, 4, 3),
                BorderBrush = new SolidColorBrush(Color.FromRgb(59, 130, 246)),
                Child = new TextBlock { Text = activePillText, FontSize = 10.5 }
            });

            foreach (string pill in attachedContextPills.ToList())
            {
                string captured = pill;
                var pBtn = new Button
                {
                    Content = new LiteralTextBlock($"📎 {captured} ✕") { FontSize = 10 },
                    FontSize = 10,
                    Padding = new Thickness(5, 1),
                    Margin = new Thickness(0, 0, 4, 3)
                };
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
                Spacing = 4,
                VerticalAlignment = VerticalAlignment.Center
            };

            Button MakeStatusBtn(string text, Action onClick, bool isRemoteBadge = false)
            {
                var b = new Button
                {
                    Content = new LiteralTextBlock(text) { FontSize = 11 },
                    FontSize = 11,
                    Padding = new Thickness(isRemoteBadge ? 10 : 7, 1),
                    Height = 24,
                    CornerRadius = new CornerRadius(0),
                    Background = isRemoteBadge ? new SolidColorBrush(Color.FromRgb(37, 99, 235)) : Brushes.Transparent,
                    Foreground = Brushes.White,
                    BorderThickness = new Thickness(0)
                };
                if (onClick != null)
                    b.Click += (s, e) => onClick();
                return b;
            }

            statusRemoteBtn = MakeStatusBtn(">< codeLess", () => OpenCommandPalette(">"), isRemoteBadge: true);
            statusBranchBtn = MakeStatusBtn("⎇ main", () => SwitchSidebarView("git"));
            statusProblemsBtn = MakeStatusBtn("⊗ 0  ⚠ 0", () => SwitchBottomTab("problems"));
            statusMessageLabel = new TextBlock
            {
                Text = "",
                FontSize = 11,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(8, 0, 0, 0)
            };
            statusChordLabel = new TextBlock
            {
                Text = "",
                FontSize = 11,
                FontWeight = FontWeight.SemiBold,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(10, 0, 0, 0)
            };

            leftRow.Children.Add(statusRemoteBtn);
            leftRow.Children.Add(statusBranchBtn);
            leftRow.Children.Add(statusProblemsBtn);
            leftRow.Children.Add(statusMessageLabel);
            leftRow.Children.Add(statusChordLabel);
            Grid.SetColumn(leftRow, 0);
            grid.Children.Add(leftRow);

            var rightRow = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 2,
                Margin = new Thickness(0, 0, 6, 0),
                VerticalAlignment = VerticalAlignment.Center
            };

            statusLnColBtn = MakeStatusBtn("Ln 1, Col 1", PromptGoToLineModal);
            statusSpacesBtn = MakeStatusBtn("Spaces: 4", () =>
            {
                settings.TabSize = settings.TabSize == 4 ? 2 : 4;
                UpdateStatusBar();
            });
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

            rightRow.Children.Add(statusLnColBtn);
            rightRow.Children.Add(statusSpacesBtn);
            rightRow.Children.Add(statusEncodingBtn);
            rightRow.Children.Add(statusEolBtn);
            rightRow.Children.Add(statusLanguageBtn);
            rightRow.Children.Add(statusAiModelBtn);
            Grid.SetColumn(rightRow, 2);
            grid.Children.Add(rightRow);

            return new Border
            {
                Height = 24,
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
            commandCenterBtn.Background = new SolidColorBrush(isLight ? Color.FromRgb(226, 232, 240) : Color.FromRgb(28, 30, 38));
            commandCenterBtn.BorderBrush = new SolidColorBrush(borderCol);
            commandCenterBtn.BorderThickness = new Thickness(1);
            commandCenterText.Foreground = new SolidColorBrush(textSecondary);

            // Activity Bar
            activityBarBorder.Background = AvaloniaColorHelper.ToBrush(theme.ActivityBarBg);
            activityBarBorder.BorderBrush = new SolidColorBrush(borderCol);
            UpdateActivityBarSelectionVisuals();

            // Sidebar
            sidebarBorder.Background = AvaloniaColorHelper.ToBrush(theme.SidebarBg);
            sidebarBorder.BorderBrush = new SolidColorBrush(borderCol);
            sidebarHeaderTitle.Foreground = new SolidColorBrush(textSecondary);
            sidebarProgressBar.TrackColor = AvaloniaColorHelper.ToAvColor(theme.SidebarBg);
            sidebarProgressBar.BarColor = accent;
            openEditorsHeaderBtn.Foreground = new SolidColorBrush(textSecondary);
            workspaceFolderHeaderBtn.Foreground = new SolidColorBrush(textPrimary);
            outlineHeaderBtn.Foreground = new SolidColorBrush(textSecondary);
            ExplorerTree.ApplyTheme(theme);

            // Tab Strip & Editors
            tabStripBorder.Background = AvaloniaColorHelper.ToBrush(theme.TabBarBg);
            tabStripBorder.BorderBrush = new SolidColorBrush(borderCol);
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
            multiFileChangesetBar.Background = new SolidColorBrush(isLight ? Color.FromRgb(224, 242, 254) : Color.FromRgb(22, 34, 52));
            multiFileChangesetBar.BorderBrush = new SolidColorBrush(accent);
            changesetSummaryText.Foreground = new SolidColorBrush(textPrimary);
            AgentFeed.ApplyTheme(theme);
            SetAgentMode(currentAgentMode);

            // Status Bar
            statusBarBorder.Background = AvaloniaColorHelper.ToBrush(theme.StatusBarBg);
            Color statusFg = AvaloniaColorHelper.ToAvColor(theme.StatusBarText);
            statusBranchBtn.Foreground = new SolidColorBrush(statusFg);
            statusProblemsBtn.Foreground = new SolidColorBrush(statusFg);
            statusMessageLabel.Foreground = new SolidColorBrush(statusFg);
            statusChordLabel.Foreground = new SolidColorBrush(Color.FromRgb(250, 204, 21));
            statusLnColBtn.Foreground = new SolidColorBrush(statusFg);
            statusSpacesBtn.Foreground = new SolidColorBrush(statusFg);
            statusEncodingBtn.Foreground = new SolidColorBrush(statusFg);
            statusEolBtn.Foreground = new SolidColorBrush(statusFg);
            statusLanguageBtn.Foreground = new SolidColorBrush(statusFg);
            statusAiModelBtn.Foreground = new SolidColorBrush(statusFg);

            // Overlays
            CommandPalette.ApplyTheme(theme);
            ModalHost.ApplyTheme(theme);
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
                ".venv", "__pycache__", ".next", "target", "packages", ".vs", "Output",
                "bench", "scratch"
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
                    set.Add("Output");
                    set.Add("bench");
                    set.Add("scratch");
                    set.Add("dist");
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

            if (!keepCurrentTabs && !ConfirmSaveOrDiscardDirtyTabs(openTabs))
                return;

            sidebarProgressBar.StartProgress();
            workspaceRoot = Path.GetFullPath(rootDir);
            settings.AddRecentWorkspace(workspaceRoot);

            string folderName = Path.GetFileName(workspaceRoot.TrimEnd('\\', '/'));
            if (string.IsNullOrEmpty(folderName)) folderName = workspaceRoot;
            workspaceFolderHeaderBtn.Content = new LiteralTextBlock("▾ " + folderName.ToUpperInvariant());
            string scP = AvaloniaCommandPaletteOverlay.FormatShortcutForPlatform("Ctrl+P");
            string scShiftP = AvaloniaCommandPaletteOverlay.FormatShortcutForPlatform("Ctrl+Shift+P");
            commandCenterText.Text = $"{folderName} — Search files ({scP}) or >commands ({scShiftP})";

            workspaceFiles.Clear();
            ExplorerTree.Nodes.Clear();

            var excluded = GetExcludedFolderSet(workspaceRoot);
            var rootNode = new ExplorerTreeNode(folderName)
            {
                Tag = workspaceRoot,
                IsDirectory = true,
                StateImageIndex = 1,
                IsExpanded = true
            };
            PopulateSingleDirectoryLevel(workspaceRoot, rootNode, excluded, addFilesToIndex: true);
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
                    PopulateSingleDirectoryLevel(extraDir, extraNode, GetExcludedFolderSet(extraDir), addFilesToIndex: true);
                    ExplorerTree.Nodes.Add(extraNode);
                }
            }

            // Index remaining files across subdirectories (respecting excluded folders)
            CollectWorkspaceFilesRecursive(workspaceRoot, excluded, depth: 0);
            aiEngine.IndexWorkspace(workspaceRoot, workspaceFiles);

            ExplorerTree.RebuildTreeLayout(preserveScroll: false);
            RefreshGitStatus();
            PrimaryEditor.SetWorkspaceContext(workspaceRoot, settings.RecentWorkspaces);
            SplitEditor.SetWorkspaceContext(workspaceRoot, settings.RecentWorkspaces);
            if (!keepCurrentTabs && openTabs.Count == 0 && workspaceFiles.Count > 0)
            {
                OpenFileInTab(workspaceFiles[0], preview: true);
            }
            UpdateWindowTitle();
            UpdateStatusBar();
            AppendOutputLog($"[Workspace] Loaded '{folderName}' ({workspaceFiles.Count} files, {aiEngine.WorkspaceSymbols.Count} symbols).");

            // Stop loading indicator after brief visual feedback
            var stopTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(220) };
            stopTimer.Tick += (s, e) =>
            {
                stopTimer.Stop();
                sidebarProgressBar.StopProgress();
            };
            stopTimer.Start();
        }

        private void CollectWorkspaceFilesRecursive(string dirPath, HashSet<string> excluded, int depth)
        {
            if (depth > 6 || workspaceFiles.Count > 1400) return;
            try
            {
                foreach (string f in Directory.EnumerateFiles(dirPath))
                {
                    string name = Path.GetFileName(f);
                    if (string.IsNullOrEmpty(name) || name.StartsWith("unins000.", StringComparison.OrdinalIgnoreCase)) continue;
                    string ext = Path.GetExtension(name);
                    if (!string.IsNullOrEmpty(ext) && DefaultIgnoredBinaryExtensions.Contains(ext)) continue;
                    if (!workspaceFiles.Contains(f, StringComparer.OrdinalIgnoreCase))
                        workspaceFiles.Add(f);
                }
                foreach (string sub in Directory.EnumerateDirectories(dirPath))
                {
                    string subName = Path.GetFileName(sub);
                    if (string.IsNullOrEmpty(subName) || (excluded != null && excluded.Contains(subName))) continue;
                    CollectWorkspaceFilesRecursive(sub, excluded, depth + 1);
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
                var excluded = GetExcludedFolderSet(workspaceRoot);
                PopulateSingleDirectoryLevel(folderNode.Tag, folderNode, excluded, addFilesToIndex: false);
            }
        }

        private void PopulateSingleDirectoryLevel(string dirPath, ExplorerTreeNode parentNode, HashSet<string> excludedFolders, bool addFilesToIndex)
        {
            try
            {
                var subDirs = new List<string>();
                foreach (string d in Directory.EnumerateDirectories(dirPath))
                {
                    string name = Path.GetFileName(d);
                    if (string.IsNullOrEmpty(name)) continue;
                    if (excludedFolders != null && excludedFolders.Contains(name)) continue;
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
                        Parent = parentNode
                    };
                    dirNode.Nodes.Add(new ExplorerTreeNode("") { Tag = "__LAZY_DIR__", StateImageIndex = 2, Parent = dirNode });
                    parentNode.Nodes.Add(dirNode);
                }

                var files = new List<string>();
                foreach (string f in Directory.EnumerateFiles(dirPath))
                {
                    string name = Path.GetFileName(f);
                    if (string.IsNullOrEmpty(name) || name.StartsWith("unins000.", StringComparison.OrdinalIgnoreCase)) continue;
                    string ext = Path.GetExtension(name);
                    if (!string.IsNullOrEmpty(ext) && DefaultIgnoredBinaryExtensions.Contains(ext)) continue;
                    files.Add(f);
                }
                files.Sort((a, b) => string.Compare(Path.GetFileName(a), Path.GetFileName(b), StringComparison.OrdinalIgnoreCase));

                foreach (string file in files)
                {
                    if (addFilesToIndex && !workspaceFiles.Contains(file, StringComparer.OrdinalIgnoreCase))
                        workspaceFiles.Add(file);
                    parentNode.Nodes.Add(new ExplorerTreeNode(Path.GetFileName(file))
                    {
                        Tag = file,
                        IsDirectory = false,
                        StateImageIndex = 2,
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

            var existing = openTabs.FirstOrDefault(t => string.Equals(t.FilePath, fullPath, StringComparison.OrdinalIgnoreCase));
            if (existing != null)
            {
                if (!preview && existing.IsPreviewTab)
                    existing.IsPreviewTab = false;
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

            Encoding fileEnc = new UTF8Encoding(false);
            bool hasBom = false;
            string eol = "\r\n";
            bool hasFinalNl = false;
            string content = File.Exists(fullPath)
                ? EditorTabBuffer.ReadFilePreservingMetadata(fullPath, out fileEnc, out hasBom, out eol, out hasFinalNl)
                : "";
            string gitHead = File.Exists(fullPath) && !string.IsNullOrEmpty(workspaceRoot)
                ? LlmHarnessAndAgentEngine.GetGitHeadFileContent(workspaceRoot, fullPath)
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
            tab.RefreshGutterDiff();
            LlmHarnessAndAgentEngine.AnalyzeBufferAstAndDiagnostics(tab);

            openTabs.Add(tab);
            if (File.Exists(fullPath))
                settings.AddRecentFile(fullPath);

            ActivateTab(tab);
            return tab;
        }

        public EditorTabBuffer CreateNewUntitledTab()
        {
            int idx = openTabs.Count(t => t.FileName.StartsWith("Untitled-", StringComparison.OrdinalIgnoreCase)) + 1;
            string name = $"Untitled-{idx}.cs";
            var tab = new EditorTabBuffer
            {
                FilePath = "",
                RelativePath = name,
                FileName = name,
                Language = "cs",
                Content = "",
                SavedContent = "",
                WordWrap = settings.WordWrap,
                IsDirty = false,
                IsPreviewTab = false
            };
            tab.SyncLinesFromContent();
            openTabs.Add(tab);
            ActivateTab(tab);
            return tab;
        }

        public void ActivateTab(EditorTabBuffer tab)
        {
            activeTab = tab;
            if (tab != null)
            {
                PrimaryEditor.LoadBuffer(tab);
                if (isEditorSplit && SplitEditor.CurrentBuffer == null)
                    SplitEditor.LoadBuffer(tab);
                ExplorerTree.ActiveEditorFilePath = tab.FilePath ?? "";
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
        }

        public void OpenWelcomeTab()
        {
            showWelcomeTab = true;
            ActivateTab(null);
        }

        public void CloseWelcomeTab()
        {
            showWelcomeTab = false;
            if (activeTab == null && openTabs.Count > 0)
                ActivateTab(openTabs[openTabs.Count - 1]);
            else
                RefreshTabStrip();
            UpdateWindowTitle();
        }

        public bool CloseTab(EditorTabBuffer tab, bool forceWithoutPrompt = false)
        {
            if (tab == null) return true;
            if (!forceWithoutPrompt && tab.IsDirty)
            {
                if (!ConfirmSaveOrDiscardDirtyTabs(new[] { tab }))
                    return false;
            }

            if (!string.IsNullOrEmpty(tab.FilePath) && File.Exists(tab.FilePath))
                closedTabHistory.Push(tab.FilePath);

            int idx = openTabs.IndexOf(tab);
            openTabs.Remove(tab);

            if (ReferenceEquals(activeTab, tab))
            {
                if (openTabs.Count > 0)
                {
                    int nextIdx = Math.Max(0, Math.Min(openTabs.Count - 1, idx));
                    ActivateTab(openTabs[nextIdx]);
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
            if (string.IsNullOrWhiteSpace(tab.FilePath))
            {
                string dir = !string.IsNullOrEmpty(workspaceRoot) && Directory.Exists(workspaceRoot)
                    ? workspaceRoot
                    : Path.GetTempPath();
                tab.FilePath = Path.Combine(dir, tab.FileName);
            }

            try
            {
                tab.SyncContentFromLines();
                tab.WriteFilePreservingMetadata(tab.FilePath);
                tab.SavedContent = tab.Content;
                tab.IsDirty = false;
                tab.IsPreviewTab = false;
                tab.RefreshGutterDiff();
                RefreshTabStrip();
                RefreshOpenEditorsSection();
                RefreshGitStatus();
                UpdateWindowTitle();
                SetStatusMessage($"Saved {tab.FileName}");
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
        }

        private void OnEditorBufferModified(EditorTabBuffer buf)
        {
            if (buf == null) return;
            if (buf.IsDirty && buf.IsPreviewTab)
                buf.IsPreviewTab = false;
            if (settings.AutoSave && !string.IsNullOrEmpty(buf.FilePath) && File.Exists(buf.FilePath))
                SaveBufferToDisk(buf);
            RefreshTabStrip();
            RefreshOpenEditorsSection();
            RefreshOutlineAndProblems(buf);
            RefreshChangesetBanner();
            UpdateWindowTitle();
            UpdateStatusBar();
        }

        private void RefreshChangesetBanner()
        {
            int pendingFiles = openTabs.Count(t => t.HasPendingDiff);
            if (pendingFiles > 0)
            {
                changesetSummaryText.Text = pendingFiles == 1 ? "Δ 1 file pending review" : $"Δ {pendingFiles} files pending review";
                multiFileChangesetBar.IsVisible = true;
            }
            else
            {
                multiFileChangesetBar.IsVisible = false;
            }
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

            // Welcome Tab (closable)
            if (showWelcomeTab || (openTabs.Count == 0 && activeTab == null))
            {
                bool isWelcomeActive = (activeTab == null);
                var wRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Center };
                wRow.Children.Add(new VectorIconControl { IconName = "logo", Width = 13, Height = 13, IconColor = accent });
                wRow.Children.Add(new TextBlock
                {
                    Text = "Welcome",
                    FontSize = 12,
                    Foreground = new SolidColorBrush(isWelcomeActive ? textPrimary : textSecondary),
                    VerticalAlignment = VerticalAlignment.Center
                });
                var wClose = new Button
                {
                    Content = "✕",
                    FontSize = 10,
                    Padding = new Thickness(4, 1),
                    Background = Brushes.Transparent,
                    Foreground = new SolidColorBrush(textSecondary)
                };
                wClose.Click += (s, e) => { CloseWelcomeTab(); e.Handled = true; };
                wRow.Children.Add(wClose);

                var wTabBorder = new Border
                {
                    Child = wRow,
                    Height = 34,
                    Padding = new Thickness(12, 0, 8, 0),
                    Background = new SolidColorBrush(isWelcomeActive ? activeBg : inactiveBg),
                    BorderBrush = new SolidColorBrush(isWelcomeActive ? accent : borderCol),
                    BorderThickness = isWelcomeActive ? new Thickness(0, 2, 1, 0) : new Thickness(0, 0, 1, 0)
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

                string labelText = (captured.IsPinned ? "📌 " : "") + captured.FileName;
                row.Children.Add(new TextBlock
                {
                    Text = labelText,
                    FontSize = 12,
                    FontStyle = captured.IsPreviewTab ? FontStyle.Italic : FontStyle.Normal,
                    FontWeight = isActive ? FontWeight.SemiBold : FontWeight.Normal,
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

                var closeBtn = new Button
                {
                    Content = "✕",
                    FontSize = 10,
                    Padding = new Thickness(4, 1),
                    Background = Brushes.Transparent,
                    Foreground = new SolidColorBrush(textSecondary)
                };
                closeBtn.Click += (s, e) =>
                {
                    CloseTab(captured);
                    e.Handled = true;
                };
                row.Children.Add(closeBtn);

                var tabBorder = new Border
                {
                    Child = row,
                    Height = 34,
                    Padding = new Thickness(12, 0, 6, 0),
                    Background = new SolidColorBrush(isActive ? activeBg : inactiveBg),
                    BorderBrush = new SolidColorBrush(isActive ? accent : borderCol),
                    BorderThickness = isActive ? new Thickness(0, 2, 1, 0) : new Thickness(0, 0, 1, 0)
                };
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
            openEditorsHeaderBtn.Content = new LiteralTextBlock((openEditorsStack.IsVisible ? "▾ " : "▸ ") + $"OPEN EDITORS ({openTabs.Count})");

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
                    probItems.Add(new LiteralTextBlock($"{(isErr ? "⊗" : "⚠")} [{d.Severity}] {tab.FileName}:{d.LineNumber} — {d.Message}")
                    {
                        FontFamily = new FontFamily("Cascadia Code, Consolas, Menlo, monospace"),
                        FontSize = 11.5,
                        Foreground = new SolidColorBrush(isErr ? Color.FromRgb(248, 113, 113) : Color.FromRgb(250, 204, 21))
                    });
                }
            }
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
            statusProblemsBtn.Content = new LiteralTextBlock($"⊗ {errCount}  ⚠ {warnCount}");
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
                Title = $"Welcome — {folderName} — codeLess";
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
                statusSpacesBtn.Content = new LiteralTextBlock($"Spaces: {settings.TabSize}");
                statusEncodingBtn.Content = new LiteralTextBlock(activeTab.HasBom ? "UTF-8 with BOM" : "UTF-8");
                statusEolBtn.Content = new LiteralTextBlock(activeTab.EolSequence == "\n" ? "LF" : "CRLF");
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
            if (statusMessageLabel != null)
                statusMessageLabel.Text = msg ?? "";
        }

        public void AppendOutputLog(string line)
        {
            if (outputStreamBox == null) return;
            outputStreamBox.Text += (line ?? "") + "\n";
        }
    }
}
