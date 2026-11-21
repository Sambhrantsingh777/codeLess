using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows.Forms;

[assembly: AssemblyTitle("codeLess")]
[assembly: AssemblyDescription("codeLess Native Agentic IDE by Savatarr")]
[assembly: AssemblyCompany("Savatarr")]
[assembly: AssemblyProduct("codeLess")]
[assembly: AssemblyCopyright("Copyright © 2026 Savatarr. All rights reserved.")]
[assembly: AssemblyVersion("3.2.3.0")]
[assembly: AssemblyFileVersion("3.2.3.0")]

namespace codeLess.Studio
{
    public class CodeLessMainForm : CodeLessChromelessForm
    {
        [DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        public static extern int SetCurrentProcessExplicitAppUserModelID(string AppID);

        [DllImport("shell32.dll")]
        public static extern void SHChangeNotify(int wEventId, int uFlags, IntPtr dwItem1, IntPtr dwItem2);

        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        public static extern IntPtr SendMessage(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);

        public const uint WM_SETICON = 0x0080;
        public const uint WM_GETICON = 0x007F;
        public static readonly IntPtr ICON_SMALL = new IntPtr(0);
        public static readonly IntPtr ICON_BIG = new IntPtr(1);
        private Icon smallWindowIcon;
        private Icon bigWindowIcon;

        private static readonly Font TabBadgeFont = new Font("Consolas", 7.8f, FontStyle.Bold);
        private static readonly Font TabTitleFont = new Font("Segoe UI", 8.8f, FontStyle.Regular);
        private static readonly Font TabItalicTitleFont = new Font("Segoe UI", 8.8f, FontStyle.Italic);
        private static readonly Font TabSubtitleFont = new Font("Segoe UI", 7.6f, FontStyle.Regular);
        private static readonly Font TabCloseGlyphFont = new Font("Segoe UI", 9.0f, FontStyle.Regular);
        private static readonly Font TabPlusBtnFont = new Font("Consolas", 10f, FontStyle.Bold);
        private static readonly Font TermTabListFont = new Font("Segoe UI", 8.6f, FontStyle.Regular);
        private static readonly Font TermTabListBoldFont = new Font("Segoe UI Semibold", 8.6f, FontStyle.Bold);
        private static readonly Font StatusItemFont = new Font("Segoe UI", 9.0f, FontStyle.Regular);

        private ToolTip statusBarToolTip = new ToolTip();
        private int lastErrorCount = 0;
        private int lastWarningCount = 0;
        private bool isCtrlKChordPending = false;
        private bool hoveredTerminalTabTrash = false;

        private ContextMenuStrip CreateAutoDisposingContextMenu()
        {
            ContextMenuStrip ctx = new ContextMenuStrip { Renderer = new DarkStudioMenuRenderer(() => activeTheme) };
            ctx.Closed += (s, e) =>
            {
                try
                {
                    if (this.IsHandleCreated && !this.IsDisposed)
                        this.BeginInvoke(new Action(() => { try { ctx.Dispose(); } catch { } }));
                    else
                        ctx.Dispose();
                }
                catch { }
            };
            return ctx;
        }

        private IdePersistentSettings settings;
        private List<StudioTheme> themes;
        private StudioTheme activeTheme;
        private LlmHarnessAndAgentEngine aiEngine;
        private VsixAndPluginHostEngine vsixEngine;

        private string workspaceRoot = "";
        private string terminalWorkingDir = "";
        private List<string> workspaceFiles = new List<string>();
        private List<EditorTabBuffer> openTabs = new List<EditorTabBuffer>();
        private EditorTabBuffer activeTab = null;
        public bool IsWelcomeTabOpen = true;

        // Async non-blocking workspace loader & search cancellation state
        private System.Threading.CancellationTokenSource workspaceLoadCts;
        private int workspaceLoadGeneration = 0;
        public volatile bool IsWorkspaceLoading = false;
        public long LastFirstTreePaintMs = 0;
        public long LastFullIndexCompleteMs = 0;
        private Action pendingWorkspaceFinalizeUi;
        private Action layoutBottomDockHeaderAction;
        private int lastResponsiveClientWidth = -1;
        private CodeLessIndeterminateBar sidebarProgressBar;
        private Button explorerNewFileBtn;
        private Button explorerNewFolderBtn;
        private Button explorerRefreshBtn;
        private Button explorerCollapseBtn;
        private System.Threading.CancellationTokenSource workspaceSearchCts;
        public volatile bool IsWorkspaceSearchRunning = false;
        private CodeLessIndeterminateBar searchProgressBar;
        private Button stopSearchBtn;

        // Unified 36px Frameless Titlebar & Menu Bar
        private MenuStrip mainMenuStrip;
        private Panel topBar;
        private Panel brandLogoBtn;
        private Button arenaTopBtn;
        private Button archMapTopBtn;
        private Button cmdPaletteBtn;
        private Label ramTelemetryBadge;
        private CodeLessDropdown themeCombo;
        private Button byokBtn;
        private Button layoutLeftBtn;
        private Button layoutBottomBtn;
        private Button layoutRightBtn;
        private Panel winMinBtn;
        private Panel winMaxBtn;
        private Panel winCloseBtn;

        // Left Activity Bar + Sidebar
        private Panel activityBar;
        private string activeSidebarView = "explorer";
        private Panel leftSidebar;
        private Label sidebarHeaderLabel;

        // Sidebar View 1: Explorer + AST Outline
        private Panel explorerContainer;
        private Panel explorerToolbar;
        private CodeLessExplorerTree explorerTree;
        private ContextMenuStrip explorerContextMenu;
        private Panel outlineBottomPanel;
        private CodeLessListBox outlineListBox;
        private Splitter outlineSplitter;

        // Sidebar View 2: Multi-File Search & Replace
        private Panel searchPanel;
        private CodeLessDarkInputBox searchInput;
        private CodeLessDarkInputBox searchReplaceInput;
        private Button searchCaseToggleBtn;
        private Button searchWordToggleBtn;
        private Button searchRegexToggleBtn;
        private bool sidebarSearchCaseSensitive = false;
        private bool sidebarSearchWholeWord = false;
        private bool sidebarSearchUseRegex = false;
        private CodeLessListBox searchResultsList;

        // Sidebar View 3: Git Source Control
        private Panel gitPanel;
        private Label gitBranchLabel;
        private CodeLessListBox gitStatusList;
        private CodeLessDarkInputBox gitCommitInput;
        private List<GitFileStatus> gitStatusItems = new List<GitFileStatus>();

        // Sidebar View 4: VS Code Extensions (.VSIX & OpenVSX)
        private Panel extensionsPanel;
        private CodeLessDarkInputBox extSearchInput;
        private CodeLessListBox extList;
        private Label extDetailLabel;
        private Button extActionBtn;
        private Button extInstallVsixFileBtn;
        private Button extRunCmdBtn;

        // Sidebar View 5: LLM Harness Studio
        private Panel harnessStudioPanel;
        private Panel harnessListFrame;
        private CodeLessListBox harnessStudioList;
        private CodeLessStreamView harnessStudioDetailBox;
        private CheckBox ghostToggleCheck;

        // Workbench Splitters
        private Splitter leftSplitter;
        private Splitter rightSplitter;
        private Splitter bottomSplitter;

        // Center Workbench (Tab Strip + Primary/Split Virtualized Editors + 5-Tab Bottom Dock)
        private Panel centerContainer;
        private Panel tabBarContainer;
        private FlowLayoutPanel tabStrip;
        private Button tabOverflowBtn;
        private EditorTabBuffer draggedTab = null;
        private Stack<string> closedTabPaths = new Stack<string>();
        private Button splitEditorTabBtn;
        private Button wordWrapTabBtn;
        private Label explorerFolderHeaderLabel;
        private Button composerModePillBtn;
        private Panel editorsSplitContainer;
        private CodeLessEditorControl editorControl;
        private CodeLessEditorControl splitEditorControl;
        private bool isSplitEditorActive = false;

        // Unsaved-changes & File Watcher & Diagnostics state
        public bool SuppressUnsavedPrompts = false;
        public Func<EditorTabBuffer, DialogResult> UnsavedPromptOverride = null;
        public Func<EditorTabBuffer, string> SaveAsDialogOverride = null;
        public int SaveAsDialogCallCount = 0;
        public Func<string, bool> DeleteConfirmOverride = null;
        public Func<EditorTabBuffer, bool> ExternalFileReloadOverride = null;
        public Func<string, string, bool> AgentToolApprovalOverride = null;
        private FileSystemWatcher workspaceFileWatcher;
        private Dictionary<string, DateTime> internalSaveTimestamps = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);
        public List<DiagnosticItem> WorkspaceDiagnostics = new List<DiagnosticItem>();

        public class TerminalSessionState
        {
            public string Title;
            public string WorkingDir;
            public string Transcript = "";
            public Process ActiveProcess;
            public ConPtyTerminalSession ConPtySession;
        }
        private List<TerminalSessionState> terminalSessions = new List<TerminalSessionState>();
        private int activeTerminalSessionIndex = 0;
        private CodeLessDropdown terminalSessionCombo;
        private Button newTermSessionBtn;
        private Button killTermProcBtn;
        private readonly List<int> activeChildProcessIds = new List<int>();

        // Real ConPTY VT100 Terminal Emulator Controls
        public CodeLessTerminalEmulatorControl conPtyTerminalControl;
        public CodeLessTerminalEmulatorControl splitConPtyTerminalControl;
        private Panel terminalSplitContainer;
        private Splitter terminalSplitSplitter;
        public Panel terminalTabsListPanel;
        private int hoveredTerminalTabIndex = -1;
        private bool isTerminalSplit = false;
        private bool isBottomDockMaximized = false;
        private int savedBottomDockHeight = 220;
        private List<string> additionalWorkspaceFolders = new List<string>();
        private CodeLessDropdown outputChannelCombo;
        private Panel statusProblemsPanel;
        private Label statusLnColItem;
        private Label statusSpacesItem;
        private Label statusEncodingItem;
        private Label statusEolItem;
        private Label statusLangItem;
        private Label statusChordLabel;

        // Navigation History (Go -> Back / Forward) & Chord State
        private List<Tuple<string, int>> navHistory = new List<Tuple<string, int>>();
        private int navHistoryIndex = -1;
        private bool suppressNavRecord = false;
        private DateTime lastCtrlKChordTime = DateTime.MinValue;
        private bool isZenMode = false;
        private bool isFullScreen = false;
        private Rectangle preFullScreenBounds;
        private int currentDiagnosticJumpIdx = -1;

        // Checked Menu Items
        private ToolStripMenuItem recentSubMenu;
        private ToolStripMenuItem autoSaveMenuItem;
        private ToolStripMenuItem colSelectMenuItem;
        private ToolStripMenuItem wordWrapMenuItem;
        private ToolStripMenuItem showMenuBarItem;
        private ToolStripMenuItem showPrimarySidebarItem;
        private ToolStripMenuItem showSecondarySidebarItem;
        private ToolStripMenuItem showStatusBarItem;
        private ToolStripMenuItem showPanelItem;
        private ToolStripMenuItem showMinimapItem;
        private ToolStripMenuItem showBreadcrumbsItem;

        // 5-Tab Bottom Dock
        private Panel bottomDockPanel;
        private string activeBottomTab = "terminal";
        private Button btmTermTabBtn;
        private Button btmProbTabBtn;
        private Button btmOutTabBtn;
        private Button btmTraceTabBtn;
        private Button btmExtHostTabBtn;
        private Button autoHealTermBtn;
        private CodeLessDropdown shellSelectorCombo;
        private CodeLessStreamView terminalOutput;
        private CodeLessDarkInputBox terminalInput;
        private List<string> terminalHistory = new List<string>();
        private int terminalHistoryIdx = -1;
        private CodeLessListBox problemsListBox;
        private CodeLessStreamView outputLogBox;
        private CodeLessStreamView harnessTraceBox;
        private CodeLessStreamView extHostLogBox;

        // Right AI Agent & Multi-File Composer Dock
        private Panel rightDock;
        private string currentAgentMode = "agent";
        private Button modeAgentBtn;
        private Button modeComposerBtn;
        private Button modeArchitectBtn;
        private Button modeAutoFixBtn;
        private Panel multiFileChangesetBar;
        private Label changesetSummaryLabel;
        private CodeLessAgentFeedControl agentFeedControl;
        private FlowLayoutPanel contextPillsBar;
        private List<string> attachedContextPills = new List<string>();
        private Panel composerCardPanel;
        private CodeLessDarkInputBox promptInputBox;
        private Panel atMentionPopupFrame;
        private CodeLessListBox atMentionList;
        private List<string> atMentionCandidates = new List<string>();
        private CodeLessDropdown quickVendorCombo;
        private CodeLessDropdown quickHarnessCombo;
        private Button sendPromptBtn;
        private Button stopPromptBtn;
        private bool isAgentStreaming = false;
        private string lastUserQuery = "";
        private CodeLessAgentFeedControl.AgentMessageCard activeStreamingCard = null;
        private List<Tuple<string, List<CodeLessAgentFeedControl.AgentMessageCard>>> chatSessionsHistory = new List<Tuple<string, List<CodeLessAgentFeedControl.AgentMessageCard>>>();

        // Bottom Status Bar
        private Panel statusBar;
        private Label statusLeftLabel;
        private Label statusProblemsLabel;
        private Label statusCursorLangLabel;
        private Label statusIndentLabel;
        private Label statusEolLabel;
        private Label statusLangLabel;
        private Label statusWrapLabel;
        private Label statusRightLabel;
        private Timer telemetryTimer;
        private Timer autoSaveTimer;
        private Panel openEditorsSectionPanel;
        private CodeLessListBox openEditorsListBox;
        private Panel dockHeaderBar;

        public CodeLessMainForm(string initialDir)
        {
            try { SetCurrentProcessExplicitAppUserModelID("Savatarr.codeLess"); } catch { }
            this.settings = IdePersistentSettings.Load();
            this.themes = StudioTheme.GetBuiltInThemes();
            this.activeTheme = StudioTheme.ResolveTheme(themes, settings.ActiveThemeId);
            this.aiEngine = new LlmHarnessAndAgentEngine(settings);
            this.vsixEngine = new VsixAndPluginHostEngine(themes);

            this.aiEngine.OnRequestToolApproval = (toolName, detail) =>
            {
                if (AgentToolApprovalOverride != null)
                    return AgentToolApprovalOverride(toolName, detail);
                if (!settings.RequireAgentToolApproval || SuppressUnsavedPrompts)
                    return true;
                bool approved = false;
                Action ask = () =>
                {
                    approved = MessageBox.Show(
                        this,
                        "Allow AI Agent to execute tool '" + toolName + "'?\n\n" + detail,
                        "codeLess Agent — Tool Execution Approval",
                        MessageBoxButtons.YesNo,
                        MessageBoxIcon.Question
                    ) == DialogResult.Yes;
                };
                if (this.InvokeRequired && this.IsHandleCreated)
                    this.Invoke(ask);
                else
                    ask();
                return approved;
            };

            this.aiEngine.OnExecuteTerminalCommandInConPty = (cmd) =>
            {
                string cwd = !string.IsNullOrEmpty(terminalWorkingDir) && Directory.Exists(terminalWorkingDir) ? terminalWorkingDir : workspaceRoot;
                try
                {
                    if (conPtyTerminalControl != null && conPtyTerminalControl.ActiveSession != null && conPtyTerminalControl.ActiveSession.IsRunning)
                    {
                        conPtyTerminalControl.ActiveSession.WriteCommand(cmd);
                    }
                }
                catch { }
                return LlmHarnessAndAgentEngine.RunProcessCapture("powershell.exe", "-NoProfile -NonInteractive -Command \"" + cmd.Replace("\"", "\\\"") + "\"", cwd, 10000);
            };

            this.Text = "Welcome — codeLess";
            this.AllowDrop = true;
            this.AutoScaleMode = AutoScaleMode.None;
            Rectangle wa = Screen.PrimaryScreen != null ? Screen.PrimaryScreen.WorkingArea : new Rectangle(0, 0, 1600, 900);
            int initW = Math.Max(1060, Math.Min(1540, wa.Width - 20));
            int initH = Math.Max(660, Math.Min(850, wa.Height - 20));
            this.Size = new Size(initW, initH);
            this.MinimumSize = new Size(Math.Min(1024, wa.Width - 40), Math.Min(640, wa.Height - 40));
            this.StartPosition = FormStartPosition.CenterScreen;
            this.DoubleBuffered = true;
            this.KeyPreview = true;

            ApplyWindowAppIcon();
            try { SHChangeNotify(0x08000000, 0x0000, IntPtr.Zero, IntPtr.Zero); } catch { }

            EnableNativeDarkTitleBar();
            IsWelcomeTabOpen = settings != null ? settings.ShowWelcomeOnStartup : true;

            string cliFileToOpen = null;
            if (!string.IsNullOrWhiteSpace(initialDir) && File.Exists(initialDir))
            {
                cliFileToOpen = Path.GetFullPath(initialDir);
                initialDir = Path.GetDirectoryName(cliFileToOpen);
            }

            string startWorkspace = ResolveStartupWorkspace(initialDir);
            if (!string.IsNullOrEmpty(startWorkspace) && Directory.Exists(startWorkspace))
            {
                workspaceRoot = startWorkspace;
                terminalWorkingDir = startWorkspace;
            }
            else
            {
                workspaceRoot = "";
                terminalWorkingDir = ConPtyTerminalSession.GetDefaultHomeDirectory();
            }

            BuildWorkbenchLayout();
            editorControl.ShowWelcomePage = IsWelcomeTabOpen;
            splitEditorControl.ShowWelcomePage = false;
            ApplySettingsToEditors();

            vsixEngine.OnHostLog += (line) =>
            {
                if (this.IsHandleCreated && !this.IsDisposed && extHostLogBox != null)
                {
                    try
                    {
                        this.BeginInvoke(new Action(() =>
                        {
                            if (!this.IsDisposed && extHostLogBox != null)
                                extHostLogBox.AppendText(line + "\n");
                        }));
                    }
                    catch { }
                }
            };
            foreach (string log in vsixEngine.HostLogs)
                extHostLogBox.AppendText(log + "\n");

            RefreshExtensionsListUi();
            ApplyResponsiveWorkbenchWidths(this.ClientSize.Width);
            lastResponsiveClientWidth = this.ClientSize.Width;
            this.Resize += (s, e) =>
            {
                if (this.ClientSize.Width != lastResponsiveClientWidth)
                {
                    lastResponsiveClientWidth = this.ClientSize.Width;
                    ApplyResponsiveWorkbenchWidths(this.ClientSize.Width);
                }
            };
            ApplyThemeToWorkbench(activeTheme);

            if (!string.IsNullOrEmpty(startWorkspace) && Directory.Exists(startWorkspace))
            {
                LoadWorkspace(startWorkspace, restorePreviousTabs: string.IsNullOrEmpty(cliFileToOpen));
                if (!string.IsNullOrEmpty(cliFileToOpen) && File.Exists(cliFileToOpen))
                    OpenFileInTab(cliFileToOpen);
            }
            else
            {
                CloseFolderWorkspace();
            }

            // Probe local Ollama in background (Extension Host remains 0 MB on-demand until triggered)
            Task.Run(async () =>
            {
                bool found = await aiEngine.ProbeLocalOllamaAsync();
                if (found && this.IsHandleCreated && !this.IsDisposed)
                {
                    try
                    {
                        this.BeginInvoke(new Action(() =>
                        {
                            if (this.IsDisposed) return;
                            RefreshQuickVendorCombo();
                            AppendOutputLog("Auto-detected Local Ollama Server on 127.0.0.1:11434.");
                        }));
                    }
                    catch { }
                }
            });

            this.Shown += (s, e) => MaybeAutoCheckForUpdates();
            this.Shown += (s, e) =>
            {
                UpdateTelemetryBadges();
            };

            telemetryTimer = new Timer { Interval = 3500 };
            telemetryTimer.Tick += (s, e) => UpdateTelemetryBadges();
            telemetryTimer.Start();

            autoSaveTimer = new Timer { Interval = 3000 };
            autoSaveTimer.Tick += (s, e) => RunAutoSaveTick();
            autoSaveTimer.Start();

            UpdateTelemetryBadges();
            UpdateWindowTitle();

            this.KeyDown += HandleGlobalKeyDown;
            this.DragEnter += (s, e) =>
            {
                if (e.Data != null && e.Data.GetDataPresent(DataFormats.FileDrop))
                    e.Effect = DragDropEffects.Copy;
            };
            this.DragDrop += (s, e) =>
            {
                try
                {
                    string[] dropped = e.Data.GetData(DataFormats.FileDrop) as string[];
                    if (dropped == null || dropped.Length == 0) return;
                    foreach (string item in dropped)
                    {
                        if (Directory.Exists(item))
                        {
                            LoadWorkspace(item, restorePreviousTabs: false);
                            break;
                        }
                        else if (File.Exists(item))
                        {
                            OpenFileInTab(item);
                        }
                    }
                }
                catch { }
            };
        }

        public void ApplyWindowAppIcon()
        {
            try
            {
                List<string> searchDirs = new List<string>();
                try
                {
                    string asmLoc = typeof(CodeLessMainForm).Assembly.Location;
                    if (!string.IsNullOrEmpty(asmLoc))
                    {
                        string asmDir = Path.GetDirectoryName(asmLoc);
                        if (!string.IsNullOrEmpty(asmDir))
                        {
                            searchDirs.Add(asmDir);
                            DirectoryInfo parent = Directory.GetParent(asmDir);
                            if (parent != null) searchDirs.Add(parent.FullName);
                        }
                    }
                }
                catch { }
                if (!string.IsNullOrEmpty(AppDomain.CurrentDomain.BaseDirectory))
                    searchDirs.Add(AppDomain.CurrentDomain.BaseDirectory);
                if (!string.IsNullOrEmpty(Environment.CurrentDirectory))
                    searchDirs.Add(Environment.CurrentDirectory);
                string localProg = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "codeLess");
                searchDirs.Add(localProg);

                string icoPath = null;
                string[] relNames = new[]
                {
                    Path.Combine("assets", "savatarr_app.ico"),
                    "savatarr_app.ico",
                    Path.Combine("assets", "savatarr.ico"),
                    Path.Combine("assets", "codeless.ico"),
                    "codeless.ico"
                };
                foreach (string dir in searchDirs.Distinct(StringComparer.OrdinalIgnoreCase))
                {
                    foreach (string rel in relNames)
                    {
                        string cand = Path.Combine(dir, rel);
                        if (File.Exists(cand))
                        {
                            icoPath = cand;
                            break;
                        }
                    }
                    if (!string.IsNullOrEmpty(icoPath)) break;
                }

                if (!string.IsNullOrEmpty(icoPath))
                {
                    smallWindowIcon = new Icon(icoPath, SystemInformation.SmallIconSize);
                    bigWindowIcon = new Icon(icoPath, new Size(48, 48));
                }
                else
                {
                    string asmLoc = typeof(CodeLessMainForm).Assembly.Location;
                    if (!string.IsNullOrEmpty(asmLoc) && File.Exists(asmLoc))
                    {
                        Icon assoc = Icon.ExtractAssociatedIcon(asmLoc);
                        if (assoc != null)
                        {
                            smallWindowIcon = new Icon(assoc, SystemInformation.SmallIconSize);
                            bigWindowIcon = new Icon(assoc, new Size(48, 48));
                        }
                    }
                }

                if (bigWindowIcon != null)
                    this.Icon = bigWindowIcon;
                if (this.IsHandleCreated)
                {
                    if (smallWindowIcon != null)
                        SendMessage(this.Handle, WM_SETICON, ICON_SMALL, smallWindowIcon.Handle);
                    if (bigWindowIcon != null)
                        SendMessage(this.Handle, WM_SETICON, ICON_BIG, bigWindowIcon.Handle);
                }
            }
            catch { }
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            try
            {
                if (smallWindowIcon == null || bigWindowIcon == null)
                    ApplyWindowAppIcon();
                if (smallWindowIcon != null)
                    SendMessage(this.Handle, WM_SETICON, ICON_SMALL, smallWindowIcon.Handle);
                if (bigWindowIcon != null)
                    SendMessage(this.Handle, WM_SETICON, ICON_BIG, bigWindowIcon.Handle);
            }
            catch { }
        }

        public string GetActiveModelDisplayName()
        {
            int vendorIdx = quickVendorCombo != null ? Math.Max(0, quickVendorCombo.SelectedIndex) : 0;
            ByokVendor v = (aiEngine != null && aiEngine.Vendors != null && vendorIdx >= 0 && vendorIdx < aiEngine.Vendors.Count)
                ? aiEngine.Vendors[vendorIdx]
                : (aiEngine != null && aiEngine.Vendors != null && aiEngine.Vendors.Count > 0 ? aiEngine.Vendors[0] : null);
            if (v != null && !string.IsNullOrWhiteSpace(v.SelectedModel))
                return v.SelectedModel;
            return "claude-sonnet-4-6";
        }

        public static string GetBufferEncodingDisplayName(EditorTabBuffer buf)
        {
            if (buf == null || buf.FileEncoding == null) return "UTF-8";
            if (buf.FileEncoding is UTF8Encoding)
                return buf.HasBom ? "UTF-8 with BOM" : "UTF-8";
            if (buf.FileEncoding is UnicodeEncoding)
                return buf.FileEncoding.CodePage == 1201 ? "UTF-16 BE" : "UTF-16 LE";
            if (buf.FileEncoding is ASCIIEncoding)
                return "US-ASCII";
            return (buf.FileEncoding.WebName ?? "UTF-8").ToUpperInvariant();
        }

        public void UpdateWindowTitle()
        {
            string filePart = activeTab != null ? ((activeTab.IsDirty ? "● " : "") + activeTab.FileName) : (IsWelcomeTabOpen ? "Welcome" : "");
            string folderPart = !string.IsNullOrEmpty(workspaceRoot) ? Path.GetFileName(workspaceRoot.TrimEnd('\\', '/')) : "";
            if (activeTab != null && !string.IsNullOrEmpty(folderPart))
                this.Text = filePart + " — " + folderPart + " — codeLess";
            else if (activeTab != null)
                this.Text = filePart + " — codeLess";
            else if (!string.IsNullOrEmpty(folderPart))
                this.Text = folderPart + " — codeLess";
            else if (IsWelcomeTabOpen)
                this.Text = "Welcome — codeLess";
            else
                this.Text = "codeLess";
        }

        public void RecordNavigationHistory(string filePath, int line1Based)
        {
            if (suppressNavRecord || string.IsNullOrEmpty(filePath)) return;
            Tuple<string, int> entry = Tuple.Create(filePath, Math.Max(1, line1Based));
            if (navHistoryIndex >= 0 && navHistoryIndex < navHistory.Count)
            {
                var cur = navHistory[navHistoryIndex];
                if (string.Equals(cur.Item1, entry.Item1, StringComparison.OrdinalIgnoreCase))
                {
                    navHistory[navHistoryIndex] = entry;
                    return;
                }
            }
            if (navHistoryIndex < navHistory.Count - 1)
                navHistory.RemoveRange(navHistoryIndex + 1, navHistory.Count - (navHistoryIndex + 1));
            navHistory.Add(entry);
            if (navHistory.Count > 100) navHistory.RemoveAt(0);
            navHistoryIndex = navHistory.Count - 1;
        }

        public bool NavigateBack()
        {
            if (navHistoryIndex <= 0 || navHistory.Count == 0) return false;
            navHistoryIndex--;
            var target = navHistory[navHistoryIndex];
            suppressNavRecord = true;
            try
            {
                if (File.Exists(target.Item1))
                {
                    OpenFileInTab(target.Item1);
                    editorControl.JumpToLine(target.Item2);
                    return true;
                }
            }
            finally { suppressNavRecord = false; }
            return false;
        }

        public bool NavigateForward()
        {
            if (navHistoryIndex >= navHistory.Count - 1 || navHistory.Count == 0) return false;
            navHistoryIndex++;
            var target = navHistory[navHistoryIndex];
            suppressNavRecord = true;
            try
            {
                if (File.Exists(target.Item1))
                {
                    OpenFileInTab(target.Item1);
                    editorControl.JumpToLine(target.Item2);
                    return true;
                }
            }
            finally { suppressNavRecord = false; }
            return false;
        }

        public void JumpToProblemOffset(int delta)
        {
            if (WorkspaceDiagnostics == null || WorkspaceDiagnostics.Count == 0) return;
            currentDiagnosticJumpIdx = (currentDiagnosticJumpIdx + delta + WorkspaceDiagnostics.Count) % WorkspaceDiagnostics.Count;
            DiagnosticItem d = WorkspaceDiagnostics[currentDiagnosticJumpIdx];
            if (!string.IsNullOrEmpty(d.FilePath) && File.Exists(d.FilePath))
                OpenFileInTab(d.FilePath);
            editorControl.JumpToLine(d.Line);
        }

        private void UpdateTelemetryBadges()
        {
            try
            {
                if (ramTelemetryBadge != null)
                {
                    ramTelemetryBadge.Text = "";
                    ramTelemetryBadge.Visible = false;
                }

                if (statusWrapLabel != null)
                    statusWrapLabel.Text = (activeTab != null && activeTab.WordWrap) ? "Wrap: ON" : "Wrap: OFF";

                UpdateStatusBarCursorAndLanguage(activeTab);

                if (statusRightLabel != null)
                {
                    statusRightLabel.UseMnemonic = false;
                    statusRightLabel.Text = GetActiveModelDisplayName();
                }
            }
            catch { }
        }

        public void ToggleActiveBufferEol()
        {
            if (activeTab == null) return;
            activeTab.EolSequence = activeTab.EolSequence == "\n" ? "\r\n" : "\n";
            activeTab.IsDirty = true;
            UpdateStatusBarCursorAndLanguage(activeTab);
            RefreshTabStrip();
        }

        public void ChangeActiveBufferLanguage(string langCode)
        {
            if (activeTab == null || string.IsNullOrWhiteSpace(langCode)) return;
            activeTab.Language = langCode.Trim().ToLowerInvariant();
            LlmHarnessAndAgentEngine.AnalyzeBufferAstAndDiagnostics(activeTab);
            editorControl.LoadBuffer(activeTab);
            if (splitEditorControl != null && splitEditorControl.CurrentBuffer == activeTab)
                splitEditorControl.LoadBuffer(activeTab);
            UpdateStatusBarCursorAndLanguage(activeTab);
            RefreshOutlineAndProblems(activeTab);
            RefreshTabStrip();
        }

        public void SwitchTabByOffset(int delta)
        {
            if (openTabs.Count == 0)
            {
                if (IsWelcomeTabOpen) OpenWelcomeTab();
                return;
            }
            if (activeTab == null)
            {
                activeTab = delta >= 0 ? openTabs[0] : openTabs[openTabs.Count - 1];
            }
            else if (openTabs.Count == 1)
            {
                if (IsWelcomeTabOpen)
                {
                    OpenWelcomeTab();
                    return;
                }
                return;
            }
            else
            {
                int idx = openTabs.IndexOf(activeTab);
                if (idx < 0) idx = 0;
                int nextIdx = (idx + delta % openTabs.Count + openTabs.Count) % openTabs.Count;
                activeTab = openTabs[nextIdx];
            }
            if (explorerTree != null)
            {
                explorerTree.ActiveEditorFilePath = activeTab != null ? activeTab.FilePath : "";
                explorerTree.Invalidate();
            }
            editorControl.LoadBuffer(activeTab);
            RefreshOutlineAndProblems(activeTab);
            RefreshTabStrip();
            UpdateWindowTitle();
        }

        public EditorTabBuffer ReopenLastClosedTab()
        {
            while (closedTabPaths.Count > 0)
            {
                string path = closedTabPaths.Pop();
                if (!string.IsNullOrEmpty(path) && File.Exists(path))
                {
                    return OpenFileInTab(path);
                }
            }
            return null;
        }

        private void UpdateStatusBarCursorAndLanguage(EditorTabBuffer buf)
        {
            if (statusCursorLangLabel == null) return;
            int effTabSize = (buf != null && buf.CustomTabSize.HasValue && buf.CustomTabSize.Value > 0)
                ? buf.CustomTabSize.Value
                : (settings != null && settings.TabSize > 0 ? settings.TabSize : 4);
            bool effSpaces = (buf != null && buf.CustomInsertSpaces.HasValue)
                ? buf.CustomInsertSpaces.Value
                : (settings == null || settings.InsertSpaces);
            string indentText = effSpaces
                ? string.Format("Spaces: {0}", effTabSize)
                : string.Format("Tab Size: {0}", effTabSize);
            if (statusIndentLabel != null)
                statusIndentLabel.Text = indentText;

            string encDisplay = GetBufferEncodingDisplayName(buf);

            if (buf == null)
            {
                string emptyMode = IsWelcomeTabOpen ? "Welcome" : "Plain Text";
                statusCursorLangLabel.Text = string.Format("Ln 1, Col 1  •  {0}  •  CRLF  •  {1}", indentText, emptyMode);
                if (statusLnColItem != null) statusLnColItem.Text = "Ln 1, Col 1";
                if (statusSpacesItem != null) statusSpacesItem.Text = indentText;
                if (statusEncodingItem != null) statusEncodingItem.Text = encDisplay;
                if (statusEolItem != null) statusEolItem.Text = "CRLF";
                if (statusLangItem != null) statusLangItem.Text = emptyMode;
                if (statusEolLabel != null) statusEolLabel.Text = "CRLF";
                if (statusLangLabel != null) statusLangLabel.Text = emptyMode;
                if (statusWrapLabel != null) statusWrapLabel.Text = "Wrap: OFF";
                LayoutStatusBarItems();
                return;
            }
            string langRaw = (buf.Language ?? "txt").ToLowerInvariant();
            string langDisplay = langRaw == "cs" ? "C#"
                : langRaw == "py" ? "Python"
                : (langRaw == "ts" || langRaw == "tsx") ? "TypeScript"
                : (langRaw == "js" || langRaw == "mjs") ? "JavaScript"
                : langRaw == "rs" ? "Rust"
                : langRaw == "go" ? "Go"
                : langRaw == "ps1" ? "PowerShell"
                : langRaw == "json" ? "JSON"
                : langRaw == "md" ? "Markdown"
                : (langRaw == "html" || langRaw == "htm") ? "HTML"
                : langRaw == "css" ? "CSS"
                : (langRaw == "txt" || langRaw == "plaintext") ? "Plain Text"
                : langRaw.ToUpperInvariant();

            string extra = "";
            if (buf.HasMultiCursors)
                extra = string.Format(" ({0} cursors)", 1 + buf.ExtraCarets.Count);
            else if (buf.HasSelection())
                extra = string.Format(" ({0} selected)", buf.GetSelectedText().Length);

            string lnColText = string.Format("Ln {0}, Col {1}{2}", buf.CursorLine + 1, buf.CursorCol + 1, extra);
            statusCursorLangLabel.Text = string.Format(
                "{0}  •  {1}  •  {2}  •  {3}",
                lnColText,
                indentText,
                buf.EolLabel,
                langDisplay
            );
            if (statusLnColItem != null) statusLnColItem.Text = lnColText;
            if (statusSpacesItem != null) statusSpacesItem.Text = indentText;
            if (statusEncodingItem != null) statusEncodingItem.Text = encDisplay;
            if (statusEolItem != null) statusEolItem.Text = buf.EolLabel;
            if (statusLangItem != null) statusLangItem.Text = langDisplay;
            if (statusEolLabel != null)
                statusEolLabel.Text = buf.EolLabel;
            if (statusLangLabel != null)
                statusLangLabel.Text = langDisplay;
            if (statusWrapLabel != null)
                statusWrapLabel.Text = buf.WordWrap ? "Wrap: ON" : "Wrap: OFF";
            LayoutStatusBarItems();
        }

        private bool TryDispatchCtrlKSecondKey(KeyEventArgs e)
        {
            if (e.KeyCode == Keys.ControlKey || e.KeyCode == Keys.ShiftKey || e.KeyCode == Keys.Menu)
                return false;

            lastCtrlKChordTime = DateTime.MinValue;
            if (editorControl != null)
                editorControl.DisarmCtrlKChord();

            if (e.KeyCode == Keys.O) // Ctrl+K Ctrl+O -> Open Folder
            {
                e.SuppressKeyPress = true;
                PromptOpenFolderDialog();
                return true;
            }
            if (e.KeyCode == Keys.S && !e.Control) // Ctrl+K S -> Save All
            {
                e.SuppressKeyPress = true;
                SaveAllDirtyTabs();
                return true;
            }
            if (e.KeyCode == Keys.S && e.Control) // Ctrl+K Ctrl+S -> Keyboard Shortcuts
            {
                e.SuppressKeyPress = true;
                OpenKeyboardShortcutsDialog();
                return true;
            }
            if (e.KeyCode == Keys.T) // Ctrl+K Ctrl+T -> Color Theme
            {
                e.SuppressKeyPress = true;
                OpenCommandPalette(">Preferences: Color Theme");
                return true;
            }
            if (e.KeyCode == Keys.F && !e.Control) // Ctrl+K F -> Close Folder
            {
                e.SuppressKeyPress = true;
                CloseFolderWorkspace();
                return true;
            }
            if (e.KeyCode == Keys.F && e.Control) // Ctrl+K Ctrl+F -> Format Selection
            {
                e.SuppressKeyPress = true;
                if (editorControl != null) editorControl.FormatSelection();
                return true;
            }
            if (e.KeyCode == Keys.Z) // Ctrl+K Z -> Zen Mode
            {
                e.SuppressKeyPress = true;
                ToggleZenMode();
                return true;
            }
            if (e.KeyCode == Keys.R) // Ctrl+K Ctrl+R -> Keyboard Shortcuts Reference
            {
                e.SuppressKeyPress = true;
                OpenKeyboardShortcutsDialog();
                return true;
            }
            if (e.KeyCode == Keys.W) // Ctrl+K W / Ctrl+K Ctrl+W -> Close All Tabs
            {
                e.SuppressKeyPress = true;
                CloseAllTabs();
                return true;
            }
            if (e.KeyCode == Keys.D) // Ctrl+K Ctrl+D -> Move Last Selection to Next Find Match
            {
                e.SuppressKeyPress = true;
                if (editorControl != null) editorControl.SkipAndAddNextOccurrenceCursor();
                return true;
            }
            if (e.KeyCode == Keys.C) // Ctrl+K Ctrl+C -> Add Line Comment
            {
                e.SuppressKeyPress = true;
                if (editorControl != null) editorControl.ToggleCommentActiveDocument();
                return true;
            }
            if (e.KeyCode == Keys.U) // Ctrl+K Ctrl+U -> Remove Line Comment
            {
                e.SuppressKeyPress = true;
                if (editorControl != null) editorControl.ToggleCommentActiveDocument();
                return true;
            }
            if (e.KeyCode == Keys.D0 || e.KeyCode == Keys.NumPad0) // Ctrl+K Ctrl+0 -> Fold All
            {
                e.SuppressKeyPress = true;
                if (editorControl != null) editorControl.FoldAllRegions();
                return true;
            }
            if (e.KeyCode == Keys.J) // Ctrl+K Ctrl+J -> Unfold All
            {
                e.SuppressKeyPress = true;
                if (editorControl != null) editorControl.UnfoldAllRegions();
                return true;
            }
            if (e.KeyCode == Keys.OemOpenBrackets) // Ctrl+K Ctrl+[ -> Fold Region
            {
                e.SuppressKeyPress = true;
                if (editorControl != null) editorControl.FoldCurrentRegion();
                return true;
            }
            if (e.KeyCode == Keys.OemCloseBrackets) // Ctrl+K Ctrl+] -> Unfold Region
            {
                e.SuppressKeyPress = true;
                if (editorControl != null) editorControl.UnfoldCurrentRegion();
                return true;
            }
            if (e.KeyCode == Keys.I) // Ctrl+K Ctrl+I -> Inline AI Edit
            {
                e.SuppressKeyPress = true;
                if (editorControl != null) editorControl.ToggleCtrlKBar();
                return true;
            }
            return true;
        }

        private void HandleGlobalKeyDown(object sender, KeyEventArgs e)
        {
            // Check if we are in a Ctrl+K chord window (within 2.5 seconds of pressing Ctrl+K)
            if ((DateTime.UtcNow - lastCtrlKChordTime).TotalMilliseconds < 2500 || (editorControl != null && editorControl.IsCtrlKChordArmed))
            {
                if (TryDispatchCtrlKSecondKey(e))
                    return;
            }

            bool termFocused = (conPtyTerminalControl != null && (conPtyTerminalControl.Focused || conPtyTerminalControl.ContainsFocus)) ||
                               (splitConPtyTerminalControl != null && (splitConPtyTerminalControl.Focused || splitConPtyTerminalControl.ContainsFocus));

            if (e.Control && !e.Shift && !e.Alt && e.KeyCode == Keys.K)
            {
                if (!termFocused)
                {
                    e.SuppressKeyPress = true;
                    lastCtrlKChordTime = DateTime.UtcNow;
                    if (editorControl != null) editorControl.ArmCtrlKChord();
                    return;
                }
            }

            if ((e.Control && e.Shift && e.KeyCode == Keys.P) || e.KeyCode == Keys.F1) // VS Code Command Palette (>)
            {
                e.SuppressKeyPress = true;
                OpenCommandPalette(">");
            }
            else if (e.Control && e.Shift && e.KeyCode == Keys.O) // VS Code Go to Symbol in Editor (@)
            {
                e.SuppressKeyPress = true;
                OpenCommandPalette("@");
            }
            else if (e.Control && !e.Shift && e.KeyCode == Keys.T) // Ctrl+T Go to Symbol in Workspace (#)
            {
                e.SuppressKeyPress = true;
                OpenCommandPalette("#");
            }
            else if (e.Control && e.KeyCode == Keys.Oemcomma) // VS Code Settings (Ctrl+,)
            {
                e.SuppressKeyPress = true;
                OpenSettingsDialog();
            }
            else if (e.Control && !e.Shift && e.KeyCode == Keys.P) // VS Code Quick Open File
            {
                e.SuppressKeyPress = true;
                OpenCommandPalette("");
            }
            else if (e.Control && e.Alt && (e.KeyCode == Keys.B || e.KeyCode == Keys.I)) // Toggle Right AI Agent Dock (Ctrl+Alt+I / Ctrl+Alt+B)
            {
                e.SuppressKeyPress = true;
                ToggleRightDockVisibility();
                if (rightDock != null && rightDock.Visible && promptInputBox != null)
                {
                    SetAgentMode("agent");
                    promptInputBox.Focus();
                }
            }
            else if (e.Control && !e.Alt && !e.Shift && e.KeyCode == Keys.B) // VS Code Toggle Left Sidebar
            {
                e.SuppressKeyPress = true;
                ToggleLeftSidebarVisibility();
            }
            else if (e.Control && e.Shift && e.KeyCode == Keys.B) // Ctrl+Shift+B Run Build Task
            {
                e.SuppressKeyPress = true;
                RunBuildTaskInTerminal();
            }
            else if (e.Control && e.Shift && e.KeyCode == Keys.Oemtilde) // Ctrl+Shift+` New Terminal
            {
                e.SuppressKeyPress = true;
                if (!bottomDockPanel.Visible) ToggleBottomDockVisibility();
                SwitchBottomDockTab("terminal");
                RequestNewTerminal(null, null);
            }
            else if (e.Control && (e.KeyCode == Keys.J || e.KeyCode == Keys.Oemtilde)) // VS Code Toggle Bottom Panel / Terminal
            {
                e.SuppressKeyPress = true;
                ToggleBottomDockVisibility();
            }
            else if (e.Control && !e.Shift && e.KeyCode == Keys.OemPipe) // Ctrl+\ Toggle Split Dual-Editor
            {
                e.SuppressKeyPress = true;
                ToggleSplitEditorView();
            }
            else if (e.Control && e.Shift && e.KeyCode == Keys.OemPipe) // Ctrl+Shift+\ Go to Bracket
            {
                e.SuppressKeyPress = true;
                editorControl.GoToMatchingBracket();
            }
            else if (e.KeyCode == Keys.F11) // F11 Full Screen
            {
                e.SuppressKeyPress = true;
                ToggleFullScreenMode();
            }
            else if (e.Alt && !e.Shift && !e.Control && e.KeyCode == Keys.Left) // Alt+Left Navigate Back
            {
                e.SuppressKeyPress = true;
                NavigateBack();
            }
            else if (e.Alt && !e.Shift && !e.Control && e.KeyCode == Keys.Right) // Alt+Right Navigate Forward
            {
                e.SuppressKeyPress = true;
                NavigateForward();
            }
            else if (e.Alt && !e.Shift && !e.Control && e.KeyCode >= Keys.D1 && e.KeyCode <= Keys.D9) // Alt+1..9 Open Editor at Index
            {
                e.SuppressKeyPress = true;
                int targetIdx = (int)(e.KeyCode - Keys.D1);
                if (openTabs.Count > 0)
                {
                    int clamped = (targetIdx == 8 && openTabs.Count < 9) ? (openTabs.Count - 1) : Math.Min(targetIdx, openTabs.Count - 1);
                    if (clamped >= 0 && clamped < openTabs.Count)
                    {
                        activeTab = openTabs[clamped];
                        editorControl.LoadBuffer(activeTab);
                        RefreshOutlineAndProblems(activeTab);
                        RefreshTabStrip();
                        UpdateWindowTitle();
                    }
                }
            }
            else if (e.Control && !e.Alt && (e.KeyCode == Keys.Oemplus || e.KeyCode == Keys.Add)) // Ctrl+= Zoom In
            {
                e.SuppressKeyPress = true;
                ZoomWorkbench(1.0f);
            }
            else if (e.Control && !e.Alt && (e.KeyCode == Keys.OemMinus || e.KeyCode == Keys.Subtract)) // Ctrl+- Zoom Out
            {
                e.SuppressKeyPress = true;
                ZoomWorkbench(-1.0f);
            }
            else if (e.Control && !e.Alt && !e.Shift && (e.KeyCode == Keys.D0 || e.KeyCode == Keys.NumPad0)) // Ctrl+0 Reset Zoom
            {
                e.SuppressKeyPress = true;
                ZoomWorkbench(0f);
            }
            else if (e.KeyCode == Keys.F8) // F8 / Shift+F8 Next/Previous Problem
            {
                e.SuppressKeyPress = true;
                JumpToProblemOffset(e.Shift ? -1 : 1);
            }
            else if (e.Alt && e.KeyCode == Keys.Z) // Alt+Z Toggle Word Wrap
            {
                e.SuppressKeyPress = true;
                editorControl.ToggleWordWrap();
                if (splitEditorControl != null)
                {
                    if (splitEditorControl.CurrentBuffer != null && activeTab != null)
                        splitEditorControl.CurrentBuffer.WordWrap = activeTab.WordWrap;
                    splitEditorControl.Invalidate(true);
                }
                UpdateTelemetryBadges();
            }
            else if (e.Control && e.Shift && e.KeyCode == Keys.E) // VS Code Explorer
            {
                e.SuppressKeyPress = true;
                leftSidebar.Visible = true;
                SwitchSidebarView("explorer");
            }
            else if (e.Control && e.Shift && e.KeyCode == Keys.F) // VS Code Multi-File Search
            {
                e.SuppressKeyPress = true;
                leftSidebar.Visible = true;
                SwitchSidebarView("search");
                searchInput.Focus();
            }
            else if (e.Control && e.Shift && e.KeyCode == Keys.H) // Replace in Files
            {
                e.SuppressKeyPress = true;
                leftSidebar.Visible = true;
                SwitchSidebarView("search");
                searchReplaceInput.Focus();
            }
            else if (e.Control && e.Shift && e.KeyCode == Keys.G) // VS Code Source Control
            {
                e.SuppressKeyPress = true;
                leftSidebar.Visible = true;
                SwitchSidebarView("git");
                RefreshGitSidebarStatus();
            }
            else if (e.Control && e.Shift && e.KeyCode == Keys.X) // VS Code Extensions
            {
                e.SuppressKeyPress = true;
                leftSidebar.Visible = true;
                SwitchSidebarView("extensions");
            }
            else if (e.Control && e.Shift && e.KeyCode == Keys.D) // Ctrl+Shift+D Run & Debug / Output
            {
                e.SuppressKeyPress = true;
                bottomDockPanel.Visible = true;
                SwitchBottomDockTab("output");
            }
            else if (e.Control && e.Shift && e.KeyCode == Keys.M) // Ctrl+Shift+M Problems
            {
                e.SuppressKeyPress = true;
                bottomDockPanel.Visible = true;
                SwitchBottomDockTab("problems");
            }
            else if (e.Control && e.Shift && (e.KeyCode == Keys.U || e.KeyCode == Keys.Y)) // Ctrl+Shift+U / Y Output
            {
                e.SuppressKeyPress = true;
                bottomDockPanel.Visible = true;
                SwitchBottomDockTab("output");
            }
            else if (e.Control && e.Shift && e.KeyCode == Keys.D5) // Ctrl+Shift+5 Split Terminal
            {
                e.SuppressKeyPress = true;
                bottomDockPanel.Visible = true;
                SwitchBottomDockTab("terminal");
                ToggleSplitTerminalView();
            }
            else if (e.Control && !e.Shift && !e.Alt && (e.KeyCode == Keys.D1 || e.KeyCode == Keys.D2 || e.KeyCode == Keys.D3)) // Ctrl+1/2/3 Focus Editor Group
            {
                e.SuppressKeyPress = true;
                if (e.KeyCode == Keys.D1)
                {
                    if (editorControl != null) editorControl.Focus();
                }
                else
                {
                    if (!isSplitEditorActive) SplitEditorInDirection(DockStyle.Right, forceOpen: true);
                    if (splitEditorControl != null) splitEditorControl.Focus();
                }
            }
            else if (e.Control && e.Shift && !e.Alt && e.KeyCode == Keys.Z) // Ctrl+Shift+Z Redo
            {
                e.SuppressKeyPress = true;
                if (editorControl != null) editorControl.PerformRedo();
            }
            else if (e.Control && e.KeyCode == Keys.Space) // Ctrl+Space / Ctrl+Shift+Space Suggest
            {
                e.SuppressKeyPress = true;
                if (editorControl != null) editorControl.UpdateAutocompletePopup(true);
            }
            else if (e.Control && e.Shift && e.KeyCode == Keys.I) // Ctrl+Shift+I Developer Tools
            {
                e.SuppressKeyPress = true;
                using (var d = new DeveloperToolsDialog(activeTheme, workspaceRoot, aiEngine != null && aiEngine.IndexedChunks != null ? aiEngine.IndexedChunks.Count : 0))
                    d.ShowDialog(this);
            }
            else if (e.Control && e.Shift && e.KeyCode == Keys.T) // VS Code Reopen Closed Tab
            {
                e.SuppressKeyPress = true;
                ReopenLastClosedTab();
            }
            else if (e.Control && e.Shift && e.KeyCode == Keys.N) // Ctrl+Shift+N New Window
            {
                e.SuppressKeyPress = true;
                try { Process.Start(Application.ExecutablePath); } catch { }
            }
            else if (e.Control && e.Shift && e.KeyCode == Keys.S) // Ctrl+Shift+S Save As
            {
                e.SuppressKeyPress = true;
                PromptSaveAsDialog();
            }
            else if (e.Control && !e.Shift && !e.Alt && e.KeyCode == Keys.S) // Ctrl+S Save
            {
                e.SuppressKeyPress = true;
                if (activeTab != null) SaveTabBuffer(activeTab);
            }
            else if ((e.Control && e.KeyCode == Keys.W) || (e.Control && e.KeyCode == Keys.F4)) // VS Code Close Active Tab
            {
                e.SuppressKeyPress = true;
                if (activeTab != null) CloseTabBuffer(activeTab);
                else if (IsWelcomeTabOpen) CloseWelcomeTab();
            }
            else if (e.Control && e.KeyCode == Keys.Tab) // VS Code Cycle Tabs
            {
                e.SuppressKeyPress = true;
                SwitchTabByOffset(e.Shift ? -1 : 1);
            }
            else if (e.Control && e.KeyCode == Keys.Next) // Ctrl+PageDown Next Tab / Terminal
            {
                e.SuppressKeyPress = true;
                if (termFocused) SwitchTerminalSessionByDelta(1);
                else SwitchTabByOffset(1);
            }
            else if (e.Control && e.KeyCode == Keys.Prior) // Ctrl+PageUp Previous Tab / Terminal
            {
                e.SuppressKeyPress = true;
                if (termFocused) SwitchTerminalSessionByDelta(-1);
                else SwitchTabByOffset(-1);
            }
            else if (e.KeyCode == Keys.F3)
            {
                e.SuppressKeyPress = true;
                if (e.Shift) editorControl.FindPreviousOccurrence();
                else editorControl.FindNextOccurrence();
            }
            else if (e.Control && !e.Shift && e.KeyCode == Keys.H)
            {
                e.SuppressKeyPress = true;
                editorControl.OpenFindBar(true);
            }
            else if (e.Control && !e.Shift && e.KeyCode == Keys.O)
            {
                e.SuppressKeyPress = true;
                PromptOpenFileDialog();
            }
            else if (e.Control && !e.Shift && e.KeyCode == Keys.N)
            {
                e.SuppressKeyPress = true;
                CreateNewUntitledTextTab();
            }
            else if (e.Control && e.KeyCode == Keys.G)
            {
                e.SuppressKeyPress = true;
                OpenCommandPalette(":");
            }
            else if (e.Control && !e.Shift && !e.Alt && e.KeyCode == Keys.I)
            {
                e.SuppressKeyPress = true;
                if (editorControl != null) editorControl.ToggleCtrlKBar();
            }
            else if (e.Control && !e.Shift && !e.Alt && e.KeyCode == Keys.L)
            {
                e.SuppressKeyPress = true;
                if (editorControl != null) editorControl.SelectCurrentLine();
            }
        }

        public void ZoomWorkbench(float deltaPt)
        {
            float nextPt = (deltaPt == 0f) ? 10.5f : Math.Max(7f, Math.Min(28f, editorControl.EditorFontSize + deltaPt));
            editorControl.EditorFontSize = nextPt;
            if (splitEditorControl != null)
                splitEditorControl.EditorFontSize = nextPt;
            if (settings != null)
            {
                settings.EditorFontSize = nextPt;
                settings.Save();
            }
        }

        public void ToggleFullScreenMode()
        {
            isFullScreen = !isFullScreen;
            if (isFullScreen)
            {
                preFullScreenBounds = this.Bounds;
                this.WindowState = FormWindowState.Normal;
                Screen scr = Screen.FromControl(this) ?? Screen.PrimaryScreen;
                if (scr != null) this.Bounds = scr.Bounds;
            }
            else
            {
                if (preFullScreenBounds.Width > 200)
                    this.Bounds = preFullScreenBounds;
            }
        }

        public void ToggleZenMode()
        {
            isZenMode = !isZenMode;
            leftSidebar.Visible = !isZenMode;
            if (leftSplitter != null) leftSplitter.Visible = !isZenMode;
            rightDock.Visible = !isZenMode;
            if (rightSplitter != null) rightSplitter.Visible = !isZenMode;
            bottomDockPanel.Visible = !isZenMode;
            if (bottomSplitter != null) bottomSplitter.Visible = !isZenMode;
            statusBar.Visible = !isZenMode;
            activityBar.Visible = !isZenMode;
        }

        public void OpenWelcomeTab()
        {
            IsWelcomeTabOpen = true;
            activeTab = null;
            PruneFileContextPillsWhenNoTabsOpen();
            if (editorControl != null)
            {
                editorControl.ShowWelcomePage = true;
                editorControl.SetRecentWorkspaces(settings != null ? settings.RecentWorkspaces : null);
                editorControl.LoadBuffer(null);
            }
            if (explorerTree != null)
            {
                explorerTree.ActiveEditorFilePath = "";
                explorerTree.Invalidate();
            }
            RefreshOutlineAndProblems(null);
            RefreshTabStrip();
            RefreshOpenEditorsSection();
            UpdateWindowTitle();
        }

        public bool CloseWelcomeTab()
        {
            if (!IsWelcomeTabOpen) return false;
            IsWelcomeTabOpen = false;
            if (editorControl != null)
                editorControl.ShowWelcomePage = false;

            if (activeTab == null)
            {
                activeTab = openTabs.LastOrDefault();
                if (editorControl != null)
                    editorControl.LoadBuffer(activeTab);
            }
            if (explorerTree != null)
            {
                explorerTree.ActiveEditorFilePath = activeTab != null ? activeTab.FilePath : "";
                explorerTree.Invalidate();
            }
            RefreshOutlineAndProblems(activeTab);
            RefreshTabStrip();
            RefreshOpenEditorsSection();
            UpdateWindowTitle();
            return true;
        }

        public void CloseAllTabs()
        {
            if (!ConfirmSaveOrDiscardAllDirtyBuffers(openTabs))
                return;
            foreach (var tab in openTabs.ToList())
            {
                tab.IsDirty = false;
                CloseTabBuffer(tab);
            }
            CloseWelcomeTab();
            ClearAttachedContextPills();
        }

        public void CloseFolderWorkspace()
        {
            if (!ConfirmSaveOrDiscardAllDirtyBuffers(openTabs))
                return;

            if (workspaceLoadCts != null)
            {
                try { workspaceLoadCts.Cancel(); } catch { }
            }
            System.Threading.Interlocked.Exchange(ref pendingWorkspaceFinalizeUi, null);
            IsWorkspaceLoading = false;
            if (sidebarProgressBar != null)
                sidebarProgressBar.StopProgress();

            openTabs.Clear();
            activeTab = null;
            workspaceRoot = "";
            additionalWorkspaceFolders.Clear();
            terminalWorkingDir = ConPtyTerminalSession.GetDefaultHomeDirectory();
            SyncTerminalsToWorkingDirectory(terminalWorkingDir);
            workspaceFiles.Clear();
            workspaceFileDiagnosticsMap.Clear();
            WorkspaceDiagnostics.Clear();
            if (aiEngine != null)
            {
                if (aiEngine.WorkspaceSymbolMap != null) aiEngine.WorkspaceSymbolMap.Clear();
                if (aiEngine.WorkspaceSymbols != null) aiEngine.WorkspaceSymbols.Clear();
            }
            ClearAttachedContextPills();
            explorerTree.Nodes.Clear();
            explorerTree.ShowEmptyWorkspaceState = true;
            explorerTree.RebuildTreeLayout(true);
            sidebarHeaderLabel.Text = "EXPLORER";
            if (explorerFolderHeaderLabel != null)
            {
                explorerFolderHeaderLabel.Text = "▾ NO FOLDER OPENED";
                explorerFolderHeaderLabel.Width = explorerToolbar != null ? Math.Max(140, explorerToolbar.Width - 12) : 200;
            }
            if (explorerNewFileBtn != null) explorerNewFileBtn.Visible = false;
            if (explorerNewFolderBtn != null) explorerNewFolderBtn.Visible = false;
            if (explorerRefreshBtn != null) explorerRefreshBtn.Visible = false;
            if (explorerCollapseBtn != null) explorerCollapseBtn.Visible = false;
            if (explorerToolbar != null) explorerToolbar.Visible = true;
            if (openEditorsSectionPanel != null) openEditorsSectionPanel.Visible = false;
            if (outlineBottomPanel != null) outlineBottomPanel.Visible = false;
            if (outlineSplitter != null) outlineSplitter.Visible = false;
            if (isSplitEditorActive && splitEditorControl != null)
            {
                isSplitEditorActive = false;
                splitEditorControl.Visible = false;
                splitEditorControl.LoadBuffer(null);
            }
            editorControl.ShowWelcomePage = IsWelcomeTabOpen;
            editorControl.SetRecentWorkspaces(settings.RecentWorkspaces);
            editorControl.LoadBuffer(null);
            RefreshOpenEditorsSection();
            RefreshOutlineAndProblems(null);
            RefreshChangesetReviewBar();
            RefreshTabStrip();
            UpdateWindowTitle();
            if (statusBar != null)
            {
                statusBar.BackColor = ResolveStatusBarBackColor(activeTheme, false); // VS Code #68217a purple when no folder is open
                statusLeftLabel.ForeColor = Color.White;
                statusLeftLabel.Text = "";
                LayoutStatusBarItems();
            }
        }

        public void RunAutoSaveTick()
        {
            if (settings != null && settings.AutoSave)
            {
                foreach (var t in openTabs.ToList())
                {
                    if (t.IsDirty && !t.IsUntitled && !string.IsNullOrEmpty(t.FilePath))
                        SaveTabBuffer(t, true);
                }
            }
        }

        public EditorTabBuffer CreateNewUntitledTextTab()
        {
            int untitledNum = 1;
            while (openTabs.Any(t => string.Equals(t.FileName, "Untitled-" + untitledNum, StringComparison.OrdinalIgnoreCase)))
                untitledNum++;
            string name = "Untitled-" + untitledNum;
            EditorTabBuffer tab = new EditorTabBuffer
            {
                FilePath = "",
                RelativePath = name,
                FileName = name,
                Language = "txt",
                Content = "",
                SavedContent = "",
                EolSequence = "\r\n",
                WordWrap = settings != null ? settings.WordWrap : false,
                IsDirty = false,
                IsUntitled = true
            };
            tab.SyncLinesFromContent();
            openTabs.Add(tab);
            activeTab = tab;
            editorControl.LoadBuffer(activeTab);
            RefreshOpenEditorsSection();
            RefreshOutlineAndProblems(activeTab);
            RefreshTabStrip();
            UpdateWindowTitle();
            return tab;
        }

        public void PromptOpenFileDialog()
        {
            using (OpenFileDialog ofd = new OpenFileDialog())
            {
                ofd.Title = "Open File — codeLess";
                if (!string.IsNullOrEmpty(workspaceRoot) && Directory.Exists(workspaceRoot))
                    ofd.InitialDirectory = workspaceRoot;
                ofd.Filter = "All Files (*.*)|*.*|Source Files (*.cs;*.py;*.ts;*.js;*.rs;*.go;*.ps1;*.json;*.md)|*.cs;*.py;*.ts;*.js;*.rs;*.go;*.ps1;*.json;*.md";
                if (ofd.ShowDialog(this) == DialogResult.OK && File.Exists(ofd.FileName))
                {
                    settings.AddRecentFile(ofd.FileName);
                    OpenFileInTab(ofd.FileName);
                }
            }
        }

        public bool PromptSaveAsDialog(EditorTabBuffer targetTab = null)
        {
            EditorTabBuffer tab = targetTab ?? activeTab;
            if (tab == null) return false;
            SaveAsDialogCallCount++;
            if (SaveAsDialogOverride != null)
            {
                string chosenPath = SaveAsDialogOverride(tab);
                if (string.IsNullOrWhiteSpace(chosenPath))
                    return false;
                tab.FilePath = chosenPath;
                tab.FileName = Path.GetFileName(chosenPath);
                tab.RelativePath = LlmHarnessAndAgentEngine.GetRelativePath(workspaceRoot, chosenPath);
                string chosenExt = Path.GetExtension(chosenPath).TrimStart('.');
                if (!string.IsNullOrEmpty(chosenExt))
                    tab.Language = chosenExt;
                tab.IsUntitled = false;
                tab.IsDeletedOnDisk = false;
                bool savedOverride = SaveTabBuffer(tab);
                if (savedOverride)
                {
                    settings.AddRecentFile(chosenPath);
                    UpdateWindowTitle();
                }
                return savedOverride;
            }
            if (SuppressUnsavedPrompts || !Application.MessageLoop)
                return false;
            using (SaveFileDialog sfd = new SaveFileDialog())
            {
                sfd.Title = "Save As — codeLess";
                string defaultName = tab.FileName ?? "Untitled-1";
                if (tab.IsUntitled && string.IsNullOrEmpty(Path.GetExtension(defaultName)))
                    defaultName += ".txt";
                sfd.FileName = defaultName;
                if (!string.IsNullOrEmpty(workspaceRoot) && Directory.Exists(workspaceRoot))
                    sfd.InitialDirectory = workspaceRoot;
                sfd.Filter = "All Files (*.*)|*.*";
                if (sfd.ShowDialog(this) == DialogResult.OK && !string.IsNullOrWhiteSpace(sfd.FileName))
                {
                    tab.FilePath = sfd.FileName;
                    tab.FileName = Path.GetFileName(sfd.FileName);
                    tab.RelativePath = LlmHarnessAndAgentEngine.GetRelativePath(workspaceRoot, sfd.FileName);
                    string ext = Path.GetExtension(sfd.FileName).TrimStart('.');
                    if (!string.IsNullOrEmpty(ext))
                        tab.Language = ext;
                    tab.IsUntitled = false;
                    tab.IsDeletedOnDisk = false;
                    bool saved = SaveTabBuffer(tab);
                    if (saved)
                    {
                        settings.AddRecentFile(sfd.FileName);
                        UpdateWindowTitle();
                    }
                    return saved;
                }
            }
            return false;
        }

        public void RevertActiveFile()
        {
            if (activeTab == null || string.IsNullOrEmpty(activeTab.FilePath) || !File.Exists(activeTab.FilePath)) return;
            Encoding enc;
            bool hasBom;
            string eol;
            bool hasFinalNl;
            string disk = EditorTabBuffer.ReadFilePreservingMetadata(activeTab.FilePath, out enc, out hasBom, out eol, out hasFinalNl);
            activeTab.PushUndoSnapshot();
            activeTab.FileEncoding = enc;
            activeTab.HasBom = hasBom;
            activeTab.EolSequence = eol;
            activeTab.HasFinalNewline = hasFinalNl;
            activeTab.Content = disk;
            activeTab.SavedContent = disk;
            activeTab.SyncLinesFromContent();
            activeTab.IsDirty = false;
            activeTab.RefreshGutterDiff();
            LlmHarnessAndAgentEngine.AnalyzeBufferAstAndDiagnostics(activeTab);
            editorControl.LoadBuffer(activeTab);
            RefreshTabStrip();
            UpdateWindowTitle();
        }

        public void RunBuildTaskInTerminal()
        {
            if (!bottomDockPanel.Visible) ToggleBottomDockVisibility();
            SwitchBottomDockTab("terminal");
            string buildCmd = File.Exists(Path.Combine(workspaceRoot ?? "", "Compile.ps1"))
                ? ".\\Compile.ps1"
                : "dotnet build";
            if (conPtyTerminalControl != null && conPtyTerminalControl.ActiveSession != null && conPtyTerminalControl.ActiveSession.IsRunning)
            {
                conPtyTerminalControl.ActiveSession.WriteCommand(buildCmd);
            }
            var _ = ExecuteTerminalCommandAsync(buildCmd);
        }

        private void ToggleLeftSidebarVisibility()
        {
            leftSidebar.Visible = !leftSidebar.Visible;
            if (leftSplitter != null) leftSplitter.Visible = leftSidebar.Visible;
            if (layoutLeftBtn != null) layoutLeftBtn.Invalidate();
        }

        private void ToggleBottomDockVisibility()
        {
            bottomDockPanel.Visible = !bottomDockPanel.Visible;
            if (bottomSplitter != null) bottomSplitter.Visible = bottomDockPanel.Visible;
            if (layoutBottomBtn != null) layoutBottomBtn.Invalidate();
        }

        private void ToggleRightDockVisibility()
        {
            rightDock.Visible = !rightDock.Visible;
            if (rightSplitter != null) rightSplitter.Visible = rightDock.Visible;
            if (layoutRightBtn != null) layoutRightBtn.Invalidate();
        }

        private void EnableNativeDarkTitleBar()
        {
            try
            {
                int useDark = (activeTheme != null && activeTheme.IsLight) ? 0 : 1;
                DwmSetWindowAttribute(this.Handle, 20, ref useDark, sizeof(int));
                DwmSetWindowAttribute(this.Handle, 19, ref useDark, sizeof(int));
            }
            catch { }
        }

        public string ResolveStartupWorkspace(string cliDir)
        {
            if (string.Equals(cliDir, "--no-folder", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(cliDir, "__NO_FOLDER__", StringComparison.OrdinalIgnoreCase))
                return "";
            if (!string.IsNullOrWhiteSpace(cliDir) && Directory.Exists(cliDir) && !IdePersistentSettings.IsInstallerProgramDirectory(cliDir))
                return cliDir;
            if (!string.IsNullOrWhiteSpace(settings.LastWorkspacePath) && Directory.Exists(settings.LastWorkspacePath) && !IdePersistentSettings.IsInstallerProgramDirectory(settings.LastWorkspacePath))
                return settings.LastWorkspacePath;
            if (settings.RecentWorkspaces != null)
            {
                string recentValid = settings.RecentWorkspaces.FirstOrDefault(p => !string.IsNullOrWhiteSpace(p) && Directory.Exists(p) && !IdePersistentSettings.IsInstallerProgramDirectory(p));
                if (!string.IsNullOrEmpty(recentValid))
                    return recentValid;
            }
            string baseDir = AppDomain.CurrentDomain.BaseDirectory.TrimEnd('\\', '/');
            if (Directory.Exists(baseDir) && !IdePersistentSettings.IsInstallerProgramDirectory(baseDir))
                return baseDir;
            return Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        }

        private void BuildWorkbenchLayout()
        {
            // 1. Unified 36px VS Code Frameless Dark Titlebar (Vector Logo + Menus + Command Center + Telemetry + Layout + Window Controls)
            topBar = new Panel { Dock = DockStyle.Top, Height = 36 };
            topBar.Paint += (s, e) =>
            {
                if (activeTheme != null)
                {
                    using (Pen bp = new Pen(activeTheme.BorderSubtle, 1))
                        e.Graphics.DrawLine(bp, 0, topBar.Height - 1, topBar.Width, topBar.Height - 1);
                }
            };
            AttachWindowDragHandle(topBar, true);

            brandLogoBtn = new Panel
            {
                Location = new Point(6, 4),
                Size = new Size(30, 28),
                BackColor = Color.Transparent,
                Cursor = Cursors.Hand
            };
            brandLogoBtn.Paint += (s, e) =>
            {
                Color fg = activeTheme != null ? activeTheme.AccentPrimary : Color.FromArgb(56, 189, 248);
                CodeLessVectorIcons.DrawIcon(e.Graphics, "logo", new Rectangle(4, 3, 22, 22), fg, 1.7f);
            };
            brandLogoBtn.Click += (s, e) => OpenWelcomeTab();

            BuildTopMenuBar();

            cmdPaletteBtn = new Button
            {
                Text = "Search files (Ctrl+P) or >Commands",
                Location = new Point(440, 5),
                Size = new Size(340, 25),
                FlatStyle = FlatStyle.Flat,
                Font = new Font("Segoe UI", 8.5f),
                Cursor = Cursors.Hand,
                UseMnemonic = false,
                Tag = "cmd_center"
            };
            cmdPaletteBtn.FlatAppearance.BorderSize = 1;
            cmdPaletteBtn.Paint += (s, e) =>
            {
                Color bg = activeTheme != null ? activeTheme.BgEditor : cmdPaletteBtn.BackColor;
                Color border = activeTheme != null ? activeTheme.BorderSubtle : Color.FromArgb(48, 54, 68);
                Color fg = activeTheme != null ? activeTheme.FgMuted : cmdPaletteBtn.ForeColor;
                e.Graphics.Clear(bg);
                using (Pen bp = new Pen(border, 1f))
                    e.Graphics.DrawRectangle(bp, 0, 0, cmdPaletteBtn.Width - 1, cmdPaletteBtn.Height - 1);
                string cleanText = (cmdPaletteBtn.Text ?? "").Replace("⌕", "").Trim();
                Size tSz = TextRenderer.MeasureText(e.Graphics, cleanText, cmdPaletteBtn.Font, new Size(cmdPaletteBtn.Width, cmdPaletteBtn.Height), TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding);
                int totalW = 16 + tSz.Width;
                int startX = Math.Max(8, (cmdPaletteBtn.Width - totalW) / 2);
                CodeLessVectorIcons.DrawIcon(e.Graphics, "search", new Rectangle(startX, (cmdPaletteBtn.Height - 13) / 2, 13, 13), fg, 1.35f);
                TextRenderer.DrawText(
                    e.Graphics,
                    cleanText,
                    cmdPaletteBtn.Font,
                    new Rectangle(startX + 16, 0, Math.Max(20, cmdPaletteBtn.Width - startX - 20), cmdPaletteBtn.Height),
                    fg,
                    TextFormatFlags.SingleLine | TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding
                );
            };
            cmdPaletteBtn.Click += (s, e) => OpenCommandPalette("");

            arenaTopBtn = CreateHeaderButton("Arena", new Point(790, 5), 68, false);
            arenaTopBtn.FlatAppearance.BorderSize = 0;
            arenaTopBtn.Visible = false;
            arenaTopBtn.Click += (s, e) => OpenMultiHarnessArenaDialog();

            archMapTopBtn = CreateHeaderButton("Map", new Point(862, 5), 62, false);
            archMapTopBtn.FlatAppearance.BorderSize = 0;
            archMapTopBtn.Visible = false;
            archMapTopBtn.Click += (s, e) => OpenArchitectureGraphDialog();

            ramTelemetryBadge = new Label
            {
                AutoSize = true,
                Visible = false,
                Location = new Point(932, 10),
                Font = new Font("Consolas", 8.3f, FontStyle.Bold),
                Text = ""
            };

            themeCombo = new CodeLessDropdown
            {
                Width = 186,
                Height = 25,
                Visible = false,
                Font = new Font("Segoe UI Semibold", 8.4f)
            };
            RefreshThemeComboItems();
            themeCombo.SelectedIndexChanged += (s, e) =>
            {
                int idx = themeCombo.SelectedIndex;
                if (idx >= 0 && idx < themes.Count)
                {
                    ApplyThemeToWorkbench(themes[idx]);
                    settings.ActiveThemeId = themes[idx].Id;
                    settings.Save();
                }
            };

            byokBtn = CreateHeaderButton("BYOK", new Point(0, 5), 76, true);
            byokBtn.Visible = false;
            byokBtn.Click += (s, e) => OpenByokConfigurationDialog();

            layoutLeftBtn = CreateTitleLayoutButton("layout-left", "Toggle Primary Side Bar (Ctrl+B)", () => ToggleLeftSidebarVisibility(), () => leftSidebar != null && leftSidebar.Visible);
            layoutBottomBtn = CreateTitleLayoutButton("layout-bottom", "Toggle Panel (Ctrl+J)", () => ToggleBottomDockVisibility(), () => bottomDockPanel != null && bottomDockPanel.Visible);
            layoutRightBtn = CreateTitleLayoutButton("layout-right", "Toggle Secondary AI Side Bar (Ctrl+Alt+I)", () => ToggleRightDockVisibility(), () => rightDock != null && rightDock.Visible);

            winMinBtn = CodeLessChromelessForm.CreateCaptionButton("min", activeTheme, false, () => { this.WindowState = FormWindowState.Minimized; });
            winMaxBtn = CodeLessChromelessForm.CreateCaptionButton("max", activeTheme, false, () =>
            {
                this.WindowState = (this.WindowState == FormWindowState.Maximized) ? FormWindowState.Normal : FormWindowState.Maximized;
            });
            winCloseBtn = CodeLessChromelessForm.CreateCaptionButton("close", activeTheme, true, () => this.Close());
            winMinBtn.Size = new Size(38, 35);
            winMaxBtn.Size = new Size(38, 35);
            winCloseBtn.Size = new Size(40, 35);

            topBar.Controls.AddRange(new Control[]
            {
                brandLogoBtn, mainMenuStrip, cmdPaletteBtn,
                layoutLeftBtn, layoutBottomBtn, layoutRightBtn,
                winMinBtn, winMaxBtn, winCloseBtn
            });

            topBar.Resize += (s, e) => LayoutTopTitleBar();
            LayoutTopTitleBar();

            // 2. Bottom Status Bar (Separate Segoe UI 9f VS Code Status Bar Items with Hover Highlight & Tooltips)
            statusBar = new Panel { Dock = DockStyle.Bottom, Height = 24 };
            ToolTip statusToolTip = new ToolTip();

            Func<string, string, Action<Control>, Label> createStatusItem = (initText, tipText, onClick) =>
            {
                Label lbl = new Label
                {
                    AutoSize = true,
                    Font = StatusItemFont,
                    Cursor = Cursors.Hand,
                    UseMnemonic = false,
                    Padding = new Padding(5, 3, 5, 3),
                    Margin = new Padding(0),
                    BackColor = Color.Transparent,
                    ForeColor = Color.White,
                    Text = initText
                };
                lbl.MouseEnter += (s, e) => { lbl.BackColor = Color.FromArgb(45, 255, 255, 255); };
                lbl.MouseLeave += (s, e) => { lbl.BackColor = Color.Transparent; };
                if (onClick != null)
                    lbl.Click += (s, e) => onClick(lbl);
                if (!string.IsNullOrEmpty(tipText))
                    statusToolTip.SetToolTip(lbl, tipText);
                return lbl;
            };

            statusLeftLabel = new Label
            {
                AutoSize = false,
                AutoEllipsis = true,
                TextAlign = ContentAlignment.MiddleLeft,
                Padding = new Padding(16, 0, 4, 0),
                Location = new Point(8, 2),
                Size = new Size(196, 20),
                Font = StatusItemFont,
                Cursor = Cursors.Hand,
                UseMnemonic = false,
                BackColor = Color.Transparent,
                Text = ""
            };
            statusLeftLabel.Paint += (s, e) =>
            {
                string t = statusLeftLabel.Text ?? "";
                if (!string.IsNullOrEmpty(t) && !t.StartsWith("No Folder", StringComparison.OrdinalIgnoreCase))
                {
                    CodeLessVectorIcons.DrawIcon(e.Graphics, "git", new Rectangle(2, 3, 13, 13), statusLeftLabel.ForeColor, 1.35f);
                }
            };
            statusLeftLabel.MouseEnter += (s, e) => { statusLeftLabel.BackColor = Color.FromArgb(45, 255, 255, 255); };
            statusLeftLabel.MouseLeave += (s, e) => { statusLeftLabel.BackColor = Color.Transparent; };
            statusLeftLabel.Click += (s, e) => { leftSidebar.Visible = true; SwitchSidebarView("git"); RefreshGitSidebarStatus(); };
            statusToolTip.SetToolTip(statusLeftLabel, "Source Control Branch / Workspace");

            statusProblemsLabel = new Label
            {
                AutoSize = true,
                Visible = false,
                Location = new Point(220, 4),
                Font = StatusItemFont,
                Cursor = Cursors.Hand,
                UseMnemonic = false,
                Text = "0 Errors, 0 Warnings"
            };
            statusProblemsLabel.Click += (s, e) => { bottomDockPanel.Visible = true; SwitchBottomDockTab("problems"); };

            statusProblemsPanel = new Panel
            {
                Location = new Point(200, 2),
                Size = new Size(78, 20),
                Cursor = Cursors.Hand,
                BackColor = Color.Transparent
            };
            statusProblemsPanel.MouseEnter += (s, e) => { statusProblemsPanel.BackColor = Color.FromArgb(45, 255, 255, 255); };
            statusProblemsPanel.MouseLeave += (s, e) => { statusProblemsPanel.BackColor = Color.Transparent; };
            statusProblemsPanel.Click += (s, e) => { bottomDockPanel.Visible = true; SwitchBottomDockTab("problems"); };
            statusProblemsPanel.Paint += (s, e) =>
            {
                int errCnt = WorkspaceDiagnostics != null ? WorkspaceDiagnostics.Count(d => d.Severity == "Error") : 0;
                int warnCnt = WorkspaceDiagnostics != null ? WorkspaceDiagnostics.Count(d => d.Severity != "Error") : 0;
                Color fg = statusLeftLabel != null ? statusLeftLabel.ForeColor : Color.White;
                int x = 4;
                CodeLessVectorIcons.DrawIcon(e.Graphics, "error-circle", new Rectangle(x, 3, 13, 13), fg, 1.4f);
                x += 15;
                string eStr = errCnt.ToString();
                Size eSz = TextRenderer.MeasureText(e.Graphics, eStr, StatusItemFont, new Size(100, 20), TextFormatFlags.NoPadding);
                TextRenderer.DrawText(e.Graphics, eStr, StatusItemFont, new Point(x, 2), fg, TextFormatFlags.NoPadding);
                x += eSz.Width + 6;
                CodeLessVectorIcons.DrawIcon(e.Graphics, "warning-triangle", new Rectangle(x, 3, 13, 13), fg, 1.4f);
                x += 15;
                string wStr = warnCnt.ToString();
                TextRenderer.DrawText(e.Graphics, wStr, StatusItemFont, new Point(x, 2), fg, TextFormatFlags.NoPadding);
            };
            statusToolTip.SetToolTip(statusProblemsPanel, "No Problems — Click to view Problems Panel (Ctrl+Shift+M)");

            statusChordLabel = new Label
            {
                AutoSize = true,
                Visible = false,
                Location = new Point(290, 3),
                Font = StatusItemFont,
                BackColor = Color.Transparent,
                ForeColor = Color.White,
                Text = ""
            };

            Action<Control> showIndentMenu = (anchorCtrl) =>
            {
                ContextMenuStrip indentMenu = new ContextMenuStrip { Renderer = new DarkStudioMenuRenderer(() => activeTheme) };
                indentMenu.Closed += (cs, ce) => { indentMenu.BeginInvoke(new Action(() => indentMenu.Dispose())); };
                EditorTabBuffer cur = activeTab;
                int curSize = (cur != null && cur.CustomTabSize.HasValue && cur.CustomTabSize.Value > 0) ? cur.CustomTabSize.Value : (settings != null ? settings.TabSize : 4);
                bool curSpaces = (cur != null && cur.CustomInsertSpaces.HasValue) ? cur.CustomInsertSpaces.Value : (settings == null || settings.InsertSpaces);

                foreach (int sz in new[] { 1, 2, 3, 4, 6, 8 })
                {
                    int capturedSz = sz;
                    var spItem = new ToolStripMenuItem("Indent Using Spaces (" + capturedSz + ")")
                    {
                        Checked = curSpaces && curSize == capturedSz
                    };
                    spItem.Click += (ms, me) =>
                    {
                        if (activeTab != null)
                        {
                            activeTab.CustomInsertSpaces = true;
                            activeTab.CustomTabSize = capturedSz;
                        }
                        else if (settings != null)
                        {
                            settings.InsertSpaces = true;
                            settings.TabSize = capturedSz;
                            settings.Save();
                        }
                        UpdateStatusBarCursorAndLanguage(activeTab);
                    };
                    indentMenu.Items.Add(spItem);
                }
                indentMenu.Items.Add(new ToolStripSeparator());
                foreach (int sz in new[] { 2, 4, 8 })
                {
                    int capturedSz = sz;
                    var tabItem = new ToolStripMenuItem("Indent Using Tabs (" + capturedSz + ")")
                    {
                        Checked = !curSpaces && curSize == capturedSz
                    };
                    tabItem.Click += (ms, me) =>
                    {
                        if (activeTab != null)
                        {
                            activeTab.CustomInsertSpaces = false;
                            activeTab.CustomTabSize = capturedSz;
                        }
                        else if (settings != null)
                        {
                            settings.InsertSpaces = false;
                            settings.TabSize = capturedSz;
                            settings.Save();
                        }
                        UpdateStatusBarCursorAndLanguage(activeTab);
                    };
                    indentMenu.Items.Add(tabItem);
                }
                indentMenu.Items.Add(new ToolStripSeparator());
                indentMenu.Items.Add("Detect Indentation from Content", null, (ms, me) => DetectActiveBufferIndentation());
                indentMenu.Items.Add("Convert Indentation to Spaces", null, (ms, me) => ConvertActiveBufferIndentation(true));
                indentMenu.Items.Add("Convert Indentation to Tabs", null, (ms, me) => ConvertActiveBufferIndentation(false));
                indentMenu.Show(anchorCtrl, new Point(0, -indentMenu.PreferredSize.Height));
            };

            Action<Control> showEncodingMenu = (anchorCtrl) =>
            {
                ContextMenuStrip encMenu = new ContextMenuStrip { Renderer = new DarkStudioMenuRenderer(() => activeTheme) };
                encMenu.Closed += (cs, ce) => { encMenu.BeginInvoke(new Action(() => encMenu.Dispose())); };
                var encodings = new Tuple<string, Encoding, bool>[]
                {
                    Tuple.Create("UTF-8", (Encoding)new UTF8Encoding(false), false),
                    Tuple.Create("UTF-8 with BOM", (Encoding)new UTF8Encoding(true), true),
                    Tuple.Create("UTF-16 LE", (Encoding)new UnicodeEncoding(false, true), true),
                    Tuple.Create("UTF-16 BE", (Encoding)new UnicodeEncoding(true, true), true),
                    Tuple.Create("US-ASCII", (Encoding)Encoding.ASCII, false),
                    Tuple.Create("ISO-8859-1 (Western)", (Encoding)Encoding.GetEncoding(28591), false),
                    Tuple.Create("Windows-1252", (Encoding)Encoding.GetEncoding(1252), false)
                };
                ToolStripMenuItem reopenSub = new ToolStripMenuItem("Reopen with Encoding");
                ToolStripMenuItem saveSub = new ToolStripMenuItem("Save with Encoding");
                string curEncName = GetBufferEncodingDisplayName(activeTab);
                foreach (var entry in encodings)
                {
                    var captured = entry;
                    var rItem = new ToolStripMenuItem(captured.Item1) { Checked = string.Equals(curEncName, captured.Item1, StringComparison.OrdinalIgnoreCase) };
                    rItem.Click += (ms, me) => ReopenActiveBufferWithEncoding(captured.Item2, captured.Item3);
                    reopenSub.DropDownItems.Add(rItem);

                    var sItem = new ToolStripMenuItem(captured.Item1) { Checked = string.Equals(curEncName, captured.Item1, StringComparison.OrdinalIgnoreCase) };
                    sItem.Click += (ms, me) => SaveActiveBufferWithEncoding(captured.Item2, captured.Item3);
                    saveSub.DropDownItems.Add(sItem);
                }
                encMenu.Items.Add(reopenSub);
                encMenu.Items.Add(saveSub);
                encMenu.Show(anchorCtrl, new Point(0, -encMenu.PreferredSize.Height));
            };

            Action<Control> showEolMenu = (anchorCtrl) =>
            {
                ContextMenuStrip eolMenu = new ContextMenuStrip { Renderer = new DarkStudioMenuRenderer(() => activeTheme) };
                eolMenu.Closed += (cs, ce) => { eolMenu.BeginInvoke(new Action(() => eolMenu.Dispose())); };
                string curEol = activeTab != null ? activeTab.EolLabel : "CRLF";
                var lfItem = new ToolStripMenuItem("LF (\\n)") { Checked = curEol == "LF" };
                lfItem.Click += (ms, me) => SetActiveBufferEol("\n");
                var crlfItem = new ToolStripMenuItem("CRLF (\\r\\n)") { Checked = curEol == "CRLF" };
                crlfItem.Click += (ms, me) => SetActiveBufferEol("\r\n");
                eolMenu.Items.Add(lfItem);
                eolMenu.Items.Add(crlfItem);
                eolMenu.Show(anchorCtrl, new Point(0, -eolMenu.PreferredSize.Height));
            };

            Action<Control> showLangMenu = (anchorCtrl) =>
            {
                ContextMenuStrip langMenu = new ContextMenuStrip { Renderer = new DarkStudioMenuRenderer(() => activeTheme) };
                langMenu.Closed += (cs, ce) => { langMenu.BeginInvoke(new Action(() => langMenu.Dispose())); };
                string[][] langs = new string[][]
                {
                    new[] { "cs", "C#" },
                    new[] { "py", "Python" },
                    new[] { "ts", "TypeScript" },
                    new[] { "js", "JavaScript" },
                    new[] { "rs", "Rust" },
                    new[] { "go", "Go" },
                    new[] { "ps1", "PowerShell" },
                    new[] { "json", "JSON" },
                    new[] { "md", "Markdown" },
                    new[] { "html", "HTML" },
                    new[] { "css", "CSS" },
                    new[] { "txt", "Plain Text" }
                };
                string curLang = activeTab != null ? (activeTab.Language ?? "txt").ToLowerInvariant() : "txt";
                foreach (var pair in langs)
                {
                    string code = pair[0];
                    var lItem = new ToolStripMenuItem(pair[1] + " (." + code + ")") { Checked = curLang == code };
                    lItem.Click += (ms, me) => ChangeActiveBufferLanguage(code);
                    langMenu.Items.Add(lItem);
                }
                langMenu.Show(anchorCtrl, new Point(0, -langMenu.PreferredSize.Height));
            };

            // Hidden legacy composite label kept updated for reflection tests
            statusCursorLangLabel = new Label
            {
                AutoSize = true,
                Visible = false,
                Location = new Point(540, 4),
                Font = StatusItemFont,
                Cursor = Cursors.Hand,
                UseMnemonic = false,
                Text = "Ln 1, Col 1  •  Spaces: 4  •  CRLF  •  C#"
            };

            statusLnColItem = createStatusItem("Ln 1, Col 1", "Go to Line/Column (Ctrl+G)", (ctrl) => OpenCommandPalette(":"));
            statusSpacesItem = createStatusItem("Spaces: 4", "Select Indentation", showIndentMenu);
            statusEncodingItem = createStatusItem("UTF-8", "Select Encoding", showEncodingMenu);
            statusEolItem = createStatusItem("CRLF", "Select End of Line Sequence", showEolMenu);
            statusLangItem = createStatusItem("Plain Text", "Select Language Mode", showLangMenu);

            statusIndentLabel = new Label { AutoSize = true, Visible = false, Text = "Spaces: 4" };
            statusEolLabel = new Label { AutoSize = true, Visible = false, Text = "CRLF" };
            statusLangLabel = new Label { AutoSize = true, Visible = false, Text = "C#" };
            statusWrapLabel = new Label { AutoSize = true, Visible = false, Text = "Wrap: OFF" };

            statusRightLabel = createStatusItem(GetActiveModelDisplayName(), "Select AI Model / Provider", (ctrl) => OpenByokConfigurationDialog());

            statusBar.Controls.AddRange(new Control[]
            {
                statusLeftLabel, statusProblemsPanel, statusProblemsLabel, statusChordLabel,
                statusLnColItem, statusSpacesItem, statusEncodingItem, statusEolItem, statusLangItem,
                statusRightLabel, statusCursorLangLabel, statusIndentLabel, statusEolLabel, statusLangLabel, statusWrapLabel
            });
            statusBar.Resize += (s, e) => LayoutStatusBarItems();
            statusLeftLabel.TextChanged += (s, e) =>
            {
                if (statusLeftLabel.Text != null && statusLeftLabel.Text.Contains("⎇"))
                {
                    statusLeftLabel.Text = statusLeftLabel.Text.Replace("⎇", "").TrimStart();
                    return;
                }
                statusLeftLabel.Invalidate();
                LayoutStatusBarItems();
            };
            LayoutStatusBarItems();

            // 3. Far-Left Vector Activity Bar (VS Code Style with 2px Active Accent Indicator & Re-Click Collapse + Bottom Account/Gear Icons)
            activityBar = new Panel { Dock = DockStyle.Left, Width = 48 };
            Button actExplorerBtn = CreateActivityButton("explorer", "Explorer (Ctrl+Shift+E)", 8, "explorer");
            Button actSearchBtn = CreateActivityButton("search", "Search (Ctrl+Shift+F)", 52, "search");
            Button actGitBtn = CreateActivityButton("git", "Source Control (Ctrl+Shift+G)", 96, "git");
            Button actExtBtn = CreateActivityButton("extensions", "Extensions (Ctrl+Shift+X)", 140, "extensions");
            Button actHarnessBtn = CreateActivityButton("harness", "AI & LLM Harnesses", 184, "harness");
            Button actArenaBtn = CreateActivityButton("arena", "Multi-Harness Parallel Arena", 228, "arena");
            Button actTermBtn = CreateActivityButton("terminal", "Toggle Panel / Terminal (Ctrl+J / Ctrl+`)", 272, "terminal");
            Button actHomeBtn = CreateActivityButton("home", "Welcome Page", 316, "home");

            Button actAccountBtn = CreateActivityButton("account", "Accounts & BYOK Providers", 400, "account_bottom");
            Button actSettingsGearBtn = CreateActivityButton("gear", "Manage / Settings (Ctrl+,)", 444, "gear_bottom");

            Action layoutActivityBarBottomIcons = () =>
            {
                if (activityBar == null) return;
                int h = activityBar.Height;
                actSettingsGearBtn.Location = new Point(2, Math.Max(360, h - 46));
                actAccountBtn.Location = new Point(2, Math.Max(316, h - 90));
            };
            activityBar.Resize += (s, e) => layoutActivityBarBottomIcons();

            Action<string> handleSidebarIconClick = (viewId) =>
            {
                if (leftSidebar.Visible && activeSidebarView == viewId)
                {
                    ToggleLeftSidebarVisibility();
                }
                else
                {
                    leftSidebar.Visible = true;
                    if (leftSplitter != null) leftSplitter.Visible = true;
                    SwitchSidebarView(viewId);
                    if (layoutLeftBtn != null) layoutLeftBtn.Invalidate();
                }
            };

            actExplorerBtn.Click += (s, e) => handleSidebarIconClick("explorer");
            actSearchBtn.Click += (s, e) => handleSidebarIconClick("search");
            actGitBtn.Click += (s, e) => { handleSidebarIconClick("git"); if (leftSidebar.Visible) RefreshGitSidebarStatus(); };
            actExtBtn.Click += (s, e) => handleSidebarIconClick("extensions");
            actHarnessBtn.Click += (s, e) => handleSidebarIconClick("harness");
            actArenaBtn.Click += (s, e) => OpenMultiHarnessArenaDialog();
            actTermBtn.Click += (s, e) => ToggleBottomDockVisibility();
            actHomeBtn.Click += (s, e) => OpenWelcomeTab();
            actAccountBtn.Click += (s, e) => OpenByokConfigurationDialog();
            actSettingsGearBtn.Click += (s, e) =>
            {
                ContextMenuStrip gearMenu = CreateAutoDisposingContextMenu();
                gearMenu.Items.Add(CreateMenuItem("Command Palette...", "Ctrl+Shift+P", () => OpenCommandPalette(">")));
                gearMenu.Items.Add(new ToolStripSeparator());
                gearMenu.Items.Add(CreateMenuItem("Settings", "Ctrl+,", () => OpenSettingsDialog()));
                gearMenu.Items.Add(CreateMenuItem("AI & BYOK Models...", "", () => OpenByokConfigurationDialog()));
                gearMenu.Items.Add(CreateMenuItem("Extensions", "Ctrl+Shift+X", () => handleSidebarIconClick("extensions")));
                gearMenu.Items.Add(CreateMenuItem("Keyboard Shortcuts", "Ctrl+K Ctrl+S", () => OpenKeyboardShortcutsDialog()));
                gearMenu.Items.Add(new ToolStripSeparator());
                gearMenu.Items.Add(CreateMenuItem("Color Theme...", "Ctrl+K Ctrl+T", () => OpenCommandPalette(">Preferences: Color Theme")));
                gearMenu.Show(actSettingsGearBtn, new Point(actSettingsGearBtn.Width, 0));
            };

            activityBar.Controls.AddRange(new Control[]
            {
                actExplorerBtn, actSearchBtn, actGitBtn, actExtBtn,
                actHarnessBtn, actArenaBtn, actTermBtn, actHomeBtn,
                actAccountBtn, actSettingsGearBtn
            });
            layoutActivityBarBottomIcons();

            // 4. Left Sidebar (Explorer + Search + Git + Extensions + Harness Studio)
            leftSidebar = new Panel { Dock = DockStyle.Left, Width = 240 };
            sidebarHeaderLabel = new Label
            {
                Dock = DockStyle.Top,
                Height = 28,
                Padding = new Padding(12, 6, 8, 4),
                Font = new Font("Segoe UI", 8.2f, FontStyle.Regular),
                UseMnemonic = false,
                Text = "EXPLORER"
            };
            sidebarHeaderLabel.Paint += (s, e) =>
            {
                Color fg = activeTheme != null ? activeTheme.FgMuted : Color.FromArgb(148, 163, 184);
                CodeLessVectorIcons.DrawIcon(
                    e.Graphics,
                    "more",
                    new Rectangle(sidebarHeaderLabel.Width - 24, 6, 16, 16),
                    fg,
                    1.5f
                );
            };

            sidebarProgressBar = new CodeLessIndeterminateBar
            {
                Dock = DockStyle.Top,
                Height = 2,
                Visible = false
            };

            BuildExplorerSidebarPanel();
            BuildSearchSidebarPanel();
            BuildGitSidebarPanel();
            BuildExtensionsSidebarPanel();
            BuildHarnessStudioSidebarPanel();

            leftSidebar.Controls.Add(explorerContainer);
            leftSidebar.Controls.Add(searchPanel);
            leftSidebar.Controls.Add(gitPanel);
            leftSidebar.Controls.Add(extensionsPanel);
            leftSidebar.Controls.Add(harnessStudioPanel);
            leftSidebar.Controls.Add(sidebarProgressBar);
            leftSidebar.Controls.Add(sidebarHeaderLabel);

            // 5. Right AI Coding Agent & Multi-File Composer Dock
            BuildRightAgentDock();

            // 6. Center Workbench (Tab Strip + Primary/Split Virtualized Editors + 5-Tab Bottom Dock)
            centerContainer = new Panel { Dock = DockStyle.Fill };

            tabBarContainer = new Panel { Dock = DockStyle.Top, Height = 35 };
            tabBarContainer.Paint += (s, e) =>
            {
                if (activeTheme != null)
                {
                    using (Pen bp = new Pen(activeTheme.BorderSubtle, 1))
                        e.Graphics.DrawLine(bp, 0, tabBarContainer.Height - 1, tabBarContainer.Width, tabBarContainer.Height - 1);
                }
            };

            Panel tabActionsBar = new Panel { Dock = DockStyle.Right, Width = 84 };
            Button runTabActionBtn = CreateExplorerVectorIconButton("run", new Point(4, 5));
            runTabActionBtn.Click += async (s, e) => { if (activeTab != null) await ExecuteActiveFileInTerminalAsync(activeTab); };

            splitEditorTabBtn = CreateExplorerVectorIconButton("split", new Point(30, 5));
            splitEditorTabBtn.Click += (s, e) => ToggleSplitEditorView();

            tabOverflowBtn = CreateExplorerVectorIconButton("more", new Point(56, 5));
            tabOverflowBtn.Click += (s, e) => ShowTabOverflowMenu();

            ToolTip tabActionTips = new ToolTip();
            tabActionTips.SetToolTip(runTabActionBtn, "Run Active File (F5)");
            tabActionTips.SetToolTip(splitEditorTabBtn, "Split Editor Right (Ctrl+\\)");
            tabActionTips.SetToolTip(tabOverflowBtn, "More Actions...");

            wordWrapTabBtn = CreateExplorerVectorIconButton("wrap", new Point(0, 5));
            wordWrapTabBtn.Visible = false;
            wordWrapTabBtn.Click += (s, e) =>
            {
                editorControl.ToggleWordWrap();
                if (splitEditorControl != null)
                {
                    if (splitEditorControl.CurrentBuffer != null && activeTab != null)
                        splitEditorControl.CurrentBuffer.WordWrap = activeTab.WordWrap;
                    splitEditorControl.Invalidate(true);
                }
                UpdateTelemetryBadges();
            };

            tabActionsBar.Controls.AddRange(new Control[] { runTabActionBtn, splitEditorTabBtn, tabOverflowBtn, wordWrapTabBtn });

            tabStrip = new FlowLayoutPanel
            {
                Dock = DockStyle.Fill,
                WrapContents = false,
                AutoScroll = false,
                Padding = new Padding(0)
            };
            tabStrip.Resize += (s, e) => ResizeOpenTabButtons();
            tabBarContainer.Controls.Add(tabStrip);
            tabBarContainer.Controls.Add(tabActionsBar);

            editorsSplitContainer = new Panel { Dock = DockStyle.Fill };

            editorControl = new CodeLessEditorControl(activeTheme)
            {
                Dock = DockStyle.Fill,
                EditorFontSize = settings.EditorFontSize,
                EnableGhostCompletions = settings.EnableInlineGhostText,
                ShowMinimap = settings.ShowMinimap,
                LoadedSnippets = vsixEngine.AllLoadedSnippets
            };
            WireEditorEvents(editorControl);

            splitEditorControl = new CodeLessEditorControl(activeTheme)
            {
                Dock = DockStyle.Right,
                Width = 480,
                Visible = false,
                EditorFontSize = settings.EditorFontSize,
                EnableGhostCompletions = settings.EnableInlineGhostText,
                ShowMinimap = false,
                LoadedSnippets = vsixEngine.AllLoadedSnippets
            };
            WireEditorEvents(splitEditorControl);

            editorsSplitContainer.Controls.Add(editorControl);
            editorsSplitContainer.Controls.Add(splitEditorControl);
            editorsSplitContainer.Resize += (s, e) =>
            {
                if (isSplitEditorActive && splitEditorControl != null)
                {
                    if (splitEditorControl.Dock == DockStyle.Bottom)
                        splitEditorControl.Height = Math.Max(160, editorsSplitContainer.Height / 2);
                    else
                        splitEditorControl.Width = Math.Max(300, editorsSplitContainer.Width / 2);
                }
            };

            BuildBottomMultiTabDock();

            bottomSplitter = new Splitter
            {
                Dock = DockStyle.Bottom,
                Height = 3,
                MinExtra = 180,
                MinSize = 90
            };

            leftSplitter = new Splitter
            {
                Dock = DockStyle.Left,
                Width = 3,
                MinExtra = 320,
                MinSize = 180
            };

            rightSplitter = new Splitter
            {
                Dock = DockStyle.Right,
                Width = 3,
                MinExtra = 320,
                MinSize = 280
            };

            centerContainer.Controls.Add(editorsSplitContainer);
            centerContainer.Controls.Add(bottomSplitter);
            centerContainer.Controls.Add(bottomDockPanel);
            centerContainer.Controls.Add(tabBarContainer);

            this.Controls.Add(centerContainer);
            this.Controls.Add(rightSplitter);
            this.Controls.Add(rightDock);
            this.Controls.Add(leftSplitter);
            this.Controls.Add(leftSidebar);
            this.Controls.Add(activityBar);
            this.Controls.Add(statusBar);
            this.Controls.Add(topBar);
        }

        private Label CreateStatusBarItem(string text, string tooltip, Action<Control> onClick)
        {
            Label lbl = new Label
            {
                Text = text,
                AutoSize = true,
                Height = 22,
                Padding = new Padding(5, 3, 5, 3),
                Font = StatusItemFont,
                ForeColor = Color.White,
                BackColor = Color.Transparent,
                Cursor = Cursors.Hand,
                UseMnemonic = false
            };
            lbl.MouseEnter += (s, e) =>
            {
                Color baseBg = statusBar != null ? statusBar.BackColor : Color.FromArgb(0, 122, 204);
                lbl.BackColor = StudioTheme.Blend(baseBg, Color.White, 0.16);
            };
            lbl.MouseLeave += (s, e) => { lbl.BackColor = Color.Transparent; };
            if (onClick != null)
                lbl.Click += (s, e) => onClick(lbl);
            if (!string.IsNullOrEmpty(tooltip) && statusBarToolTip != null)
                statusBarToolTip.SetToolTip(lbl, tooltip);
            return lbl;
        }

        private void LayoutStatusBarItems()
        {
            if (statusBar == null || statusLeftLabel == null) return;
            int w = statusBar.ClientSize.Width > 100 ? statusBar.ClientSize.Width : 1200;

            int leftX = 8;
            if (statusLeftLabel.Visible && !string.IsNullOrEmpty(statusLeftLabel.Text))
            {
                int textW = TextRenderer.MeasureText(statusLeftLabel.Text, StatusItemFont, new Size(400, 20), TextFormatFlags.NoPadding).Width;
                statusLeftLabel.Width = Math.Min(230, Math.Max(70, textW + 24));
                statusLeftLabel.Location = new Point(leftX, 1);
                leftX = statusLeftLabel.Right + 6;
            }
            if (statusProblemsPanel != null && statusProblemsPanel.Visible)
            {
                int pw = 66 + Math.Max(0, ((lastErrorCount > 9 ? 1 : 0) + (lastWarningCount > 9 ? 1 : 0)) * 7);
                statusProblemsPanel.Width = pw;
                statusProblemsPanel.Location = new Point(leftX, 1);
                leftX = statusProblemsPanel.Right + 8;
            }
            if (statusChordLabel != null && statusChordLabel.Visible)
            {
                statusChordLabel.Location = new Point(leftX, 3);
            }

            int rightX = w - 8;
            Label[] rightItems = new Label[]
            {
                statusRightLabel,
                statusLangItem,
                statusEolItem,
                statusEncodingItem,
                statusSpacesItem,
                statusLnColItem
            };
            foreach (Label item in rightItems)
            {
                if (item == null || !item.Visible) continue;
                rightX -= item.PreferredWidth;
                item.Location = new Point(Math.Max(leftX + 10, rightX), 1);
                rightX -= 2;
            }
        }

        public void DetectActiveBufferIndentation()
        {
            if (activeTab == null || activeTab.Lines == null) return;
            int tabLines = 0;
            int spaceLines = 0;
            Dictionary<int, int> stepCounts = new Dictionary<int, int>();
            int prevSpaces = 0;
            foreach (string raw in activeTab.Lines)
            {
                string ln = raw ?? "";
                if (string.IsNullOrWhiteSpace(ln)) continue;
                if (ln[0] == '\t')
                {
                    tabLines++;
                }
                else if (ln[0] == ' ')
                {
                    spaceLines++;
                    int sp = 0;
                    while (sp < ln.Length && ln[sp] == ' ') sp++;
                    int diff = Math.Abs(sp - prevSpaces);
                    if (diff == 2 || diff == 4 || diff == 8)
                    {
                        int c;
                        stepCounts[diff] = (stepCounts.TryGetValue(diff, out c) ? c : 0) + 1;
                    }
                    prevSpaces = sp;
                }
            }
            activeTab.CustomInsertSpaces = (spaceLines >= tabLines);
            if (activeTab.CustomInsertSpaces.Value && stepCounts.Count > 0)
            {
                activeTab.CustomTabSize = stepCounts.OrderByDescending(kv => kv.Value).First().Key;
            }
            UpdateStatusBarCursorAndLanguage(activeTab);
        }

        private void ConvertActiveBufferIndentation(bool toSpaces)
        {
            if (activeTab == null || activeTab.Lines == null) return;
            int tabSz = (activeTab.CustomTabSize.HasValue && activeTab.CustomTabSize.Value > 0) ? activeTab.CustomTabSize.Value : (settings != null && settings.TabSize > 0 ? settings.TabSize : 4);
            string spacesUnit = new string(' ', tabSz);
            activeTab.PushUndoSnapshot();
            for (int i = 0; i < activeTab.Lines.Count; i++)
            {
                string ln = activeTab.Lines[i] ?? "";
                int leadEnd = 0;
                while (leadEnd < ln.Length && (ln[leadEnd] == ' ' || ln[leadEnd] == '\t')) leadEnd++;
                if (leadEnd == 0) continue;
                string leading = ln.Substring(0, leadEnd);
                string rest = ln.Substring(leadEnd);
                if (toSpaces)
                    leading = leading.Replace("\t", spacesUnit);
                else
                    leading = leading.Replace(spacesUnit, "\t");
                activeTab.Lines[i] = leading + rest;
            }
            activeTab.CustomInsertSpaces = toSpaces;
            activeTab.SyncContentFromLines();
            activeTab.IsDirty = true;
            if (editorControl != null) editorControl.Invalidate(true);
            UpdateStatusBarCursorAndLanguage(activeTab);
            RefreshTabStrip();
        }

        private void ReopenActiveBufferWithEncoding(Encoding enc, bool hasBom)
        {
            if (activeTab == null || string.IsNullOrEmpty(activeTab.FilePath) || !File.Exists(activeTab.FilePath)) return;
            try
            {
                byte[] rawBytes = File.ReadAllBytes(activeTab.FilePath);
                int offset = 0;
                if (hasBom && rawBytes.Length >= 3 && rawBytes[0] == 0xEF && rawBytes[1] == 0xBB && rawBytes[2] == 0xBF) offset = 3;
                else if (hasBom && rawBytes.Length >= 2 && ((rawBytes[0] == 0xFF && rawBytes[1] == 0xFE) || (rawBytes[0] == 0xFE && rawBytes[1] == 0xFF))) offset = 2;
                string decoded = enc.GetString(rawBytes, offset, rawBytes.Length - offset);
                activeTab.FileEncoding = enc;
                activeTab.HasBom = hasBom;
                activeTab.Content = decoded.Replace("\r\n", "\n").Replace("\r", "\n");
                activeTab.SavedContent = activeTab.Content;
                activeTab.SyncLinesFromContent();
                activeTab.IsDirty = false;
                if (editorControl != null) editorControl.LoadBuffer(activeTab);
                UpdateStatusBarCursorAndLanguage(activeTab);
                RefreshTabStrip();
            }
            catch { }
        }

        private void SaveActiveBufferWithEncoding(Encoding enc, bool hasBom)
        {
            if (activeTab == null) return;
            activeTab.FileEncoding = enc;
            activeTab.HasBom = hasBom;
            activeTab.IsDirty = true;
            SaveTabBuffer(activeTab);
            UpdateStatusBarCursorAndLanguage(activeTab);
        }

        private void SetActiveBufferEol(string eol)
        {
            if (activeTab == null) return;
            if (activeTab.EolSequence != eol)
            {
                activeTab.EolSequence = eol;
                activeTab.IsDirty = true;
                UpdateStatusBarCursorAndLanguage(activeTab);
                RefreshTabStrip();
            }
        }

        private void LayoutTopTitleBar()
        {
            if (topBar == null || winCloseBtn == null) return;
            int w = topBar.Width;
            int r = w - 1;
            winCloseBtn.Location = new Point(r - winCloseBtn.Width, 0); r -= winCloseBtn.Width;
            winMaxBtn.Location = new Point(r - winMaxBtn.Width, 0); r -= winMaxBtn.Width;
            winMinBtn.Location = new Point(r - winMinBtn.Width, 0); r -= winMinBtn.Width + 8;

            layoutRightBtn.Location = new Point(r - layoutRightBtn.Width, 5); r -= layoutRightBtn.Width + 2;
            layoutBottomBtn.Location = new Point(r - layoutBottomBtn.Width, 5); r -= layoutBottomBtn.Width + 2;
            layoutLeftBtn.Location = new Point(r - layoutLeftBtn.Width, 5); r -= layoutLeftBtn.Width + 10;

            if (byokBtn != null) byokBtn.Visible = false;
            if (themeCombo != null) themeCombo.Visible = false;
            if (ramTelemetryBadge != null) ramTelemetryBadge.Visible = false;
            if (archMapTopBtn != null) archMapTopBtn.Visible = false;
            if (arenaTopBtn != null) arenaTopBtn.Visible = false;

            int availLeft = (mainMenuStrip != null && mainMenuStrip.Visible ? mainMenuStrip.Right : 38) + 12;
            int availRight = r;
            int maxAllowedW = Math.Max(140, availRight - availLeft - 12);
            int targetW = Math.Min(440, maxAllowedW);
            int centeredX = Math.Max(availLeft, Math.Min(availRight - targetW, (w - targetW) / 2));
            cmdPaletteBtn.Location = new Point(centeredX, 5);
            cmdPaletteBtn.Width = targetW;
            string wsTitle = !string.IsNullOrEmpty(workspaceRoot) ? Path.GetFileName(workspaceRoot.TrimEnd('\\', '/')) : "codeLess";
            cmdPaletteBtn.Text = targetW < 260
                ? wsTitle
                : wsTitle + " — Search files (Ctrl+P) or >commands";
            cmdPaletteBtn.Invalidate();
        }

        private Button CreateTitleLayoutButton(string iconKind, string tooltip, Action onClick, Func<bool> isActiveFn)
        {
            Button b = new Button
            {
                Text = "",
                Size = new Size(26, 25),
                FlatStyle = FlatStyle.Flat,
                Cursor = Cursors.Hand,
                Tag = "layout_toggle"
            };
            b.FlatAppearance.BorderSize = 0;
            b.Paint += (s, e) =>
            {
                bool active = isActiveFn != null && isActiveFn();
                Color bg = activeTheme != null ? activeTheme.BgActivity : Color.FromArgb(14, 16, 22);
                using (SolidBrush sb = new SolidBrush(bg))
                    e.Graphics.FillRectangle(sb, b.ClientRectangle);
                Color ic = active
                    ? (activeTheme != null ? activeTheme.AccentPrimary : Color.FromArgb(56, 189, 248))
                    : (activeTheme != null ? activeTheme.FgMuted : Color.Gray);
                CodeLessVectorIcons.DrawIcon(e.Graphics, iconKind, new Rectangle(3, 2, 20, 20), ic, 1.4f);
            };
            b.Click += (s, e) => { if (onClick != null) onClick(); };
            ToolTip tt = new ToolTip();
            tt.SetToolTip(b, tooltip);
            return b;
        }

        private static ToolStripMenuItem CreateMenuItem(string text, string shortcutDisplay, Action onClick)
        {
            ToolStripMenuItem item = new ToolStripMenuItem(text)
            {
                ShortcutKeyDisplayString = shortcutDisplay ?? "",
                Padding = new Padding(4, 2, 4, 2)
            };
            if (onClick != null)
                item.Click += (s, e) => onClick();
            return item;
        }

        private void RebuildOpenRecentSubMenu()
        {
            if (recentSubMenu == null) return;
            recentSubMenu.DropDownItems.Clear();

            recentSubMenu.DropDownItems.Add(CreateMenuItem("Reopen Closed Editor", "Ctrl+Shift+T", () => ReopenLastClosedTab()));
            recentSubMenu.DropDownItems.Add(new ToolStripSeparator());

            bool addedFolders = false;
            if (settings.RecentWorkspaces != null)
            {
                foreach (string ws in settings.RecentWorkspaces.Take(8))
                {
                    if (string.IsNullOrWhiteSpace(ws)) continue;
                    string captured = ws;
                    recentSubMenu.DropDownItems.Add(CreateMenuItem(captured, "Folder", () =>
                    {
                        if (Directory.Exists(captured)) LoadWorkspace(captured, restorePreviousTabs: false);
                    }));
                    addedFolders = true;
                }
            }
            if (addedFolders) recentSubMenu.DropDownItems.Add(new ToolStripSeparator());

            bool addedFiles = false;
            if (settings.RecentFiles != null)
            {
                foreach (string rf in settings.RecentFiles.Take(8))
                {
                    if (string.IsNullOrWhiteSpace(rf)) continue;
                    string captured = rf;
                    recentSubMenu.DropDownItems.Add(CreateMenuItem(captured, "File", () =>
                    {
                        if (File.Exists(captured)) OpenFileInTab(captured);
                    }));
                    addedFiles = true;
                }
            }
            if (addedFiles) recentSubMenu.DropDownItems.Add(new ToolStripSeparator());

            recentSubMenu.DropDownItems.Add(CreateMenuItem("More...", "Ctrl+R", () => OpenCommandPaletteWithRecentItems()));
            recentSubMenu.DropDownItems.Add(CreateMenuItem("Clear Recently Opened", "", () =>
            {
                settings.ClearRecentlyOpened();
                editorControl.SetRecentWorkspaces(settings.RecentWorkspaces);
            }));
        }

        private void BuildTopMenuBar()
        {
            mainMenuStrip = new MenuStrip
            {
                Dock = DockStyle.None,
                Location = new Point(38, 6),
                AutoSize = true,
                BackColor = activeTheme != null ? activeTheme.BgActivity : Color.FromArgb(14, 16, 22),
                ForeColor = activeTheme != null ? activeTheme.FgPrimary : Color.FromArgb(228, 232, 240),
                Font = new Font("Segoe UI", 8.8f),
                Padding = new Padding(2, 1, 2, 1),
                Renderer = new DarkStudioMenuRenderer(() => activeTheme)
            };

            // 1. FILE MENU
            ToolStripMenuItem fileMenu = new ToolStripMenuItem("&File");
            fileMenu.DropDownItems.Add(CreateMenuItem("New Text File", "Ctrl+N", () => CreateNewUntitledTextTab()));
            fileMenu.DropDownItems.Add(CreateMenuItem("New File...", "Ctrl+Alt+Win+N", () => PromptCreateNewFile(workspaceRoot)));
            fileMenu.DropDownItems.Add(CreateMenuItem("New Window", "Ctrl+Shift+N", () =>
            {
                try { Process.Start(Application.ExecutablePath); } catch { }
            }));
            fileMenu.DropDownItems.Add(new ToolStripSeparator());
            fileMenu.DropDownItems.Add(CreateMenuItem("Open File...", "Ctrl+O", () => PromptOpenFileDialog()));
            fileMenu.DropDownItems.Add(CreateMenuItem("Open Folder...", "Ctrl+K Ctrl+O", () => PromptOpenFolderDialog()));

            recentSubMenu = new ToolStripMenuItem("Open Recent");
            RebuildOpenRecentSubMenu();
            fileMenu.DropDownItems.Add(recentSubMenu);
            fileMenu.DropDownItems.Add(CreateMenuItem("Add Folder to Workspace...", "", () => PromptAddFolderToWorkspaceDialog()));
            fileMenu.DropDownItems.Add(new ToolStripSeparator());
            fileMenu.DropDownItems.Add(CreateMenuItem("Save", "Ctrl+S", () => { if (activeTab != null) SaveTabBuffer(activeTab); }));
            fileMenu.DropDownItems.Add(CreateMenuItem("Save As...", "Ctrl+Shift+S", () => PromptSaveAsDialog()));
            fileMenu.DropDownItems.Add(CreateMenuItem("Save All", "Ctrl+K S", () => SaveAllDirtyTabs()));
            fileMenu.DropDownItems.Add(new ToolStripSeparator());

            autoSaveMenuItem = CreateMenuItem("Auto Save", "", () =>
            {
                settings.AutoSave = !settings.AutoSave;
                autoSaveMenuItem.Checked = settings.AutoSave;
                settings.Save();
            });
            autoSaveMenuItem.Checked = settings != null && settings.AutoSave;
            fileMenu.DropDownItems.Add(autoSaveMenuItem);

            ToolStripMenuItem prefsSubMenu = new ToolStripMenuItem("Preferences");
            prefsSubMenu.DropDownItems.Add(CreateMenuItem("Settings", "Ctrl+,", () => OpenSettingsDialog()));
            prefsSubMenu.DropDownItems.Add(CreateMenuItem("AI & BYOK Configuration...", "", () => OpenByokConfigurationDialog()));
            prefsSubMenu.DropDownItems.Add(CreateMenuItem("Extensions", "Ctrl+Shift+X", () => { leftSidebar.Visible = true; SwitchSidebarView("extensions"); }));
            prefsSubMenu.DropDownItems.Add(CreateMenuItem("Keyboard Shortcuts", "Ctrl+K Ctrl+S", () => OpenKeyboardShortcutsDialog()));
            prefsSubMenu.DropDownItems.Add(new ToolStripSeparator());
            prefsSubMenu.DropDownItems.Add(CreateMenuItem("Color Theme", "Ctrl+K Ctrl+T", () => OpenCommandPalette(">Preferences: Color Theme")));
            fileMenu.DropDownItems.Add(prefsSubMenu);

            fileMenu.DropDownItems.Add(new ToolStripSeparator());
            fileMenu.DropDownItems.Add(CreateMenuItem("Revert File", "", () => RevertActiveFile()));
            fileMenu.DropDownItems.Add(CreateMenuItem("Close Editor", "Ctrl+W", () =>
            {
                if (activeTab != null) CloseTabBuffer(activeTab);
                else if (IsWelcomeTabOpen) CloseWelcomeTab();
            }));
            fileMenu.DropDownItems.Add(CreateMenuItem("Close Folder", "Ctrl+K F", () => CloseFolderWorkspace()));
            fileMenu.DropDownItems.Add(CreateMenuItem("Close Window", "Alt+F4", () => this.Close()));
            fileMenu.DropDownItems.Add(new ToolStripSeparator());
            fileMenu.DropDownItems.Add(CreateMenuItem("Exit", "", () => this.Close()));
            fileMenu.DropDownOpening += (s, e) =>
            {
                if (autoSaveMenuItem != null && settings != null)
                    autoSaveMenuItem.Checked = settings.AutoSave;
                RebuildOpenRecentSubMenu();
            };

            // 2. EDIT MENU
            ToolStripMenuItem editMenu = new ToolStripMenuItem("&Edit");
            editMenu.DropDownItems.Add(CreateMenuItem("Undo", "Ctrl+Z", () => editorControl.PerformUndo()));
            editMenu.DropDownItems.Add(CreateMenuItem("Redo", "Ctrl+Y", () => editorControl.PerformRedo()));
            editMenu.DropDownItems.Add(new ToolStripSeparator());
            editMenu.DropDownItems.Add(CreateMenuItem("Cut", "Ctrl+X", () => editorControl.PerformCut()));
            editMenu.DropDownItems.Add(CreateMenuItem("Copy", "Ctrl+C", () => editorControl.PerformCopy()));
            editMenu.DropDownItems.Add(CreateMenuItem("Paste", "Ctrl+V", () => editorControl.PerformPaste()));
            editMenu.DropDownItems.Add(new ToolStripSeparator());
            editMenu.DropDownItems.Add(CreateMenuItem("Find", "Ctrl+F", () => editorControl.OpenFindBar(false)));
            editMenu.DropDownItems.Add(CreateMenuItem("Replace", "Ctrl+H", () => editorControl.OpenFindBar(true)));
            editMenu.DropDownItems.Add(new ToolStripSeparator());
            editMenu.DropDownItems.Add(CreateMenuItem("Find in Files", "Ctrl+Shift+F", () => { leftSidebar.Visible = true; SwitchSidebarView("search"); searchInput.Focus(); }));
            editMenu.DropDownItems.Add(CreateMenuItem("Replace in Files", "Ctrl+Shift+H", () => { leftSidebar.Visible = true; SwitchSidebarView("search"); searchReplaceInput.Focus(); }));
            editMenu.DropDownItems.Add(new ToolStripSeparator());
            editMenu.DropDownItems.Add(CreateMenuItem("Toggle Line Comment", "Ctrl+/", () => editorControl.ToggleCommentActiveDocument()));
            editMenu.DropDownItems.Add(CreateMenuItem("Toggle Block Comment", "Shift+Alt+A", () => editorControl.ToggleBlockCommentActiveDocument()));

            // 3. SELECTION MENU
            ToolStripMenuItem selectionMenu = new ToolStripMenuItem("&Selection");
            selectionMenu.DropDownItems.Add(CreateMenuItem("Select All", "Ctrl+A", () => editorControl.PerformSelectAll()));
            selectionMenu.DropDownItems.Add(CreateMenuItem("Expand Selection", "Shift+Alt+Right", () => editorControl.ExpandSelection()));
            selectionMenu.DropDownItems.Add(CreateMenuItem("Shrink Selection", "Shift+Alt+Left", () => editorControl.ShrinkSelection()));
            selectionMenu.DropDownItems.Add(new ToolStripSeparator());
            selectionMenu.DropDownItems.Add(CreateMenuItem("Copy Line Up", "Shift+Alt+Up", () => editorControl.CopyLineUp()));
            selectionMenu.DropDownItems.Add(CreateMenuItem("Copy Line Down", "Shift+Alt+Down", () => editorControl.CopyLineDown()));
            selectionMenu.DropDownItems.Add(CreateMenuItem("Move Line Up", "Alt+Up", () => editorControl.MoveLineUp()));
            selectionMenu.DropDownItems.Add(CreateMenuItem("Move Line Down", "Alt+Down", () => editorControl.MoveLineDown()));
            selectionMenu.DropDownItems.Add(CreateMenuItem("Duplicate Selection", "", () => editorControl.DuplicateSelection()));
            selectionMenu.DropDownItems.Add(new ToolStripSeparator());
            selectionMenu.DropDownItems.Add(CreateMenuItem("Add Cursor Above", "Ctrl+Alt+Up", () => editorControl.AddCursorAbove()));
            selectionMenu.DropDownItems.Add(CreateMenuItem("Add Cursor Below", "Ctrl+Alt+Down", () => editorControl.AddCursorBelow()));
            selectionMenu.DropDownItems.Add(CreateMenuItem("Add Cursors to Line Ends", "Shift+Alt+I", () => { if (editorControl.Canvas != null) editorControl.Canvas.AddCursorsToLineEnds(); }));
            selectionMenu.DropDownItems.Add(CreateMenuItem("Add Next Occurrence", "Ctrl+D", () => editorControl.TriggerAddNextOccurrenceCursor()));
            selectionMenu.DropDownItems.Add(CreateMenuItem("Select All Occurrences", "Ctrl+Shift+L", () => editorControl.SelectAllOccurrences()));
            selectionMenu.DropDownItems.Add(new ToolStripSeparator());
            colSelectMenuItem = CreateMenuItem("Column Selection Mode", "", () =>
            {
                editorControl.ToggleColumnSelectionMode();
                colSelectMenuItem.Checked = editorControl.ColumnSelectionMode;
            });
            selectionMenu.DropDownItems.Add(colSelectMenuItem);
            selectionMenu.DropDownOpening += (s, e) =>
            {
                if (colSelectMenuItem != null && editorControl != null)
                    colSelectMenuItem.Checked = editorControl.ColumnSelectionMode;
            };

            // 4. VIEW MENU
            ToolStripMenuItem viewMenu = new ToolStripMenuItem("&View");
            viewMenu.DropDownItems.Add(CreateMenuItem("Command Palette...", "Ctrl+Shift+P", () => OpenCommandPalette(">")));
            viewMenu.DropDownItems.Add(CreateMenuItem("Open View...", "", () => OpenCommandPalette(">View:")));
            viewMenu.DropDownItems.Add(new ToolStripSeparator());

            ToolStripMenuItem appearanceMenu = new ToolStripMenuItem("Appearance");
            appearanceMenu.DropDownItems.Add(CreateMenuItem("Full Screen", "F11", () => ToggleFullScreenMode()));
            appearanceMenu.DropDownItems.Add(CreateMenuItem("Zen Mode", "Ctrl+K Z", () => ToggleZenMode()));
            appearanceMenu.DropDownItems.Add(new ToolStripSeparator());
            showMenuBarItem = CreateMenuItem("Menu Bar", "", () => { mainMenuStrip.Visible = !mainMenuStrip.Visible; LayoutTopTitleBar(); });
            showPrimarySidebarItem = CreateMenuItem("Primary Side Bar", "Ctrl+B", () => ToggleLeftSidebarVisibility());
            showSecondarySidebarItem = CreateMenuItem("Secondary Side Bar (AI Chat)", "Ctrl+Alt+B", () => ToggleRightDockVisibility());
            showStatusBarItem = CreateMenuItem("Status Bar", "", () => { statusBar.Visible = !statusBar.Visible; });
            showPanelItem = CreateMenuItem("Panel", "Ctrl+J", () => ToggleBottomDockVisibility());
            showMinimapItem = CreateMenuItem("Minimap", "", () =>
            {
                editorControl.ShowMinimap = !editorControl.ShowMinimap;
                settings.ShowMinimap = editorControl.ShowMinimap;
                settings.Save();
            });
            showBreadcrumbsItem = CreateMenuItem("Breadcrumbs", "", () =>
            {
                editorControl.ShowBreadcrumbs = !editorControl.ShowBreadcrumbs;
                if (splitEditorControl != null) splitEditorControl.ShowBreadcrumbs = editorControl.ShowBreadcrumbs;
                settings.ShowBreadcrumbs = editorControl.ShowBreadcrumbs;
                settings.Save();
            });
            appearanceMenu.DropDownItems.AddRange(new ToolStripItem[]
            {
                showMenuBarItem, showPrimarySidebarItem, showSecondarySidebarItem, showStatusBarItem, showPanelItem,
                new ToolStripSeparator(),
                showMinimapItem, showBreadcrumbsItem,
                new ToolStripSeparator(),
                CreateMenuItem("Zoom In", "Ctrl+=", () => ZoomWorkbench(1.0f)),
                CreateMenuItem("Zoom Out", "Ctrl+-", () => ZoomWorkbench(-1.0f)),
                CreateMenuItem("Reset Zoom", "Ctrl+NumPad0", () => ZoomWorkbench(0f))
            });
            viewMenu.DropDownItems.Add(appearanceMenu);

            ToolStripMenuItem editorLayoutMenu = new ToolStripMenuItem("Editor Layout");
            editorLayoutMenu.DropDownItems.Add(CreateMenuItem("Split Right", "Ctrl+\\", () => SplitEditorInDirection(DockStyle.Right, forceOpen: true)));
            editorLayoutMenu.DropDownItems.Add(CreateMenuItem("Split Down", "Ctrl+K Ctrl+\\", () => SplitEditorInDirection(DockStyle.Bottom, forceOpen: true)));
            editorLayoutMenu.DropDownItems.Add(CreateMenuItem("Single", "", () => { if (isSplitEditorActive) ToggleSplitEditorView(); }));
            viewMenu.DropDownItems.Add(editorLayoutMenu);

            viewMenu.DropDownItems.Add(new ToolStripSeparator());
            viewMenu.DropDownItems.Add(CreateMenuItem("Explorer", "Ctrl+Shift+E", () => { leftSidebar.Visible = true; SwitchSidebarView("explorer"); }));
            viewMenu.DropDownItems.Add(CreateMenuItem("Search", "Ctrl+Shift+F", () => { leftSidebar.Visible = true; SwitchSidebarView("search"); }));
            viewMenu.DropDownItems.Add(CreateMenuItem("Source Control", "Ctrl+Shift+G", () => { leftSidebar.Visible = true; SwitchSidebarView("git"); RefreshGitSidebarStatus(); }));
            viewMenu.DropDownItems.Add(CreateMenuItem("Run", "Ctrl+Shift+D", async () =>
            {
                if (activeTab != null) await ExecuteActiveFileInTerminalAsync(activeTab);
                else { bottomDockPanel.Visible = true; SwitchBottomDockTab("output"); }
            }));
            viewMenu.DropDownItems.Add(CreateMenuItem("Extensions", "Ctrl+Shift+X", () => { leftSidebar.Visible = true; SwitchSidebarView("extensions"); }));
            viewMenu.DropDownItems.Add(new ToolStripSeparator());
            viewMenu.DropDownItems.Add(CreateMenuItem("Problems", "Ctrl+Shift+M", () => { bottomDockPanel.Visible = true; SwitchBottomDockTab("problems"); }));
            viewMenu.DropDownItems.Add(CreateMenuItem("Output", "Ctrl+Shift+U", () => { bottomDockPanel.Visible = true; SwitchBottomDockTab("output"); }));
            viewMenu.DropDownItems.Add(CreateMenuItem("Debug Console", "Ctrl+Shift+Y", () => { bottomDockPanel.Visible = true; SwitchBottomDockTab("output"); }));
            viewMenu.DropDownItems.Add(CreateMenuItem("Terminal", "Ctrl+`", () =>
            {
                bottomDockPanel.Visible = true;
                SwitchBottomDockTab("terminal");
                if (terminalSessions.Count == 0) CreateNewTerminalSession();
            }));
            viewMenu.DropDownItems.Add(new ToolStripSeparator());
            wordWrapMenuItem = CreateMenuItem("Word Wrap", "Alt+Z", () =>
            {
                editorControl.ToggleWordWrap();
                if (splitEditorControl != null)
                {
                    if (splitEditorControl.CurrentBuffer != null && activeTab != null)
                        splitEditorControl.CurrentBuffer.WordWrap = activeTab.WordWrap;
                    splitEditorControl.Invalidate(true);
                }
                UpdateTelemetryBadges();
            });
            viewMenu.DropDownItems.Add(wordWrapMenuItem);
            viewMenu.DropDownOpening += (s, e) =>
            {
                if (showMenuBarItem != null) showMenuBarItem.Checked = mainMenuStrip.Visible;
                if (showPrimarySidebarItem != null && leftSidebar != null) showPrimarySidebarItem.Checked = leftSidebar.Visible;
                if (showSecondarySidebarItem != null && rightDock != null) showSecondarySidebarItem.Checked = rightDock.Visible;
                if (showStatusBarItem != null && statusBar != null) showStatusBarItem.Checked = statusBar.Visible;
                if (showPanelItem != null && bottomDockPanel != null) showPanelItem.Checked = bottomDockPanel.Visible;
                if (showMinimapItem != null && editorControl != null) showMinimapItem.Checked = editorControl.ShowMinimap;
                if (showBreadcrumbsItem != null && editorControl != null) showBreadcrumbsItem.Checked = editorControl.ShowBreadcrumbs;
                if (wordWrapMenuItem != null) wordWrapMenuItem.Checked = activeTab != null ? activeTab.WordWrap : settings.WordWrap;
            };

            // 5. GO MENU
            ToolStripMenuItem goMenu = new ToolStripMenuItem("&Go");
            goMenu.DropDownItems.Add(CreateMenuItem("Back", "Alt+Left", () => NavigateBack()));
            goMenu.DropDownItems.Add(CreateMenuItem("Forward", "Alt+Right", () => NavigateForward()));
            goMenu.DropDownItems.Add(new ToolStripSeparator());
            goMenu.DropDownItems.Add(CreateMenuItem("Go to File...", "Ctrl+P", () => OpenCommandPalette("")));
            goMenu.DropDownItems.Add(CreateMenuItem("Go to Symbol in Workspace...", "Ctrl+T", () => OpenCommandPalette("#")));
            goMenu.DropDownItems.Add(new ToolStripSeparator());
            goMenu.DropDownItems.Add(CreateMenuItem("Go to Symbol in Editor...", "Ctrl+Shift+O", () => OpenCommandPalette("@")));
            goMenu.DropDownItems.Add(CreateMenuItem("Go to Definition", "F12", () =>
            {
                if (activeTab == null) return;
                if (!activeTab.HasSelection() && editorControl.TriggerFollowLinkAt(activeTab.CursorLine, activeTab.CursorCol)) return;
                HandleGoToDefinition(activeTab.GetSelectedText());
            }));
            goMenu.DropDownItems.Add(CreateMenuItem("Go to References", "Shift+F12", () =>
            {
                if (activeTab != null) HandleGoToReferences(activeTab.GetSelectedText());
            }));
            goMenu.DropDownItems.Add(CreateMenuItem("Go to Line/Column...", "Ctrl+G", () => OpenCommandPalette(":")));
            goMenu.DropDownItems.Add(CreateMenuItem("Go to Bracket", "Ctrl+Shift+\\", () => editorControl.GoToMatchingBracket()));
            goMenu.DropDownItems.Add(new ToolStripSeparator());
            goMenu.DropDownItems.Add(CreateMenuItem("Next Problem", "F8", () => JumpToProblemOffset(1)));
            goMenu.DropDownItems.Add(CreateMenuItem("Previous Problem", "Shift+F8", () => JumpToProblemOffset(-1)));

            // 6. RUN MENU
            ToolStripMenuItem runMenu = new ToolStripMenuItem("&Run");
            runMenu.DropDownItems.Add(CreateMenuItem("Run Active File", "F5", async () =>
            {
                if (activeTab != null) await ExecuteActiveFileInTerminalAsync(activeTab);
            }));
            runMenu.DropDownItems.Add(CreateMenuItem("Run Without Debugging", "Ctrl+F5", async () =>
            {
                if (activeTab != null) await ExecuteActiveFileInTerminalAsync(activeTab);
            }));
            runMenu.DropDownItems.Add(CreateMenuItem("Stop Execution", "Shift+F5", () => KillActiveTerminalProcess()));
            runMenu.DropDownItems.Add(new ToolStripSeparator());
            runMenu.DropDownItems.Add(CreateMenuItem("Toggle Breakpoint", "F9", () =>
            {
                if (activeTab != null)
                {
                    int ln = activeTab.CursorLine + 1;
                    if (!activeTab.Breakpoints.Remove(ln)) activeTab.Breakpoints.Add(ln);
                    editorControl.Invalidate(true);
                }
            }));
            runMenu.DropDownItems.Add(new ToolStripSeparator());
            runMenu.DropDownItems.Add(CreateMenuItem("Auto-Heal Last Crash", "", async () => await AutoHealLastTerminalErrorAsync()));
            runMenu.DropDownItems.Add(CreateMenuItem("HTTP / REST API Playground...", "", () => OpenRestApiPlaygroundDialog()));

            // 7. TERMINAL MENU
            ToolStripMenuItem terminalMenu = new ToolStripMenuItem("&Terminal");
            terminalMenu.DropDownItems.Add(CreateMenuItem("New Terminal", "Ctrl+Shift+`", () =>
            {
                if (!bottomDockPanel.Visible) ToggleBottomDockVisibility();
                SwitchBottomDockTab("terminal");
                RequestNewTerminal(null, null);
            }));
            terminalMenu.DropDownItems.Add(CreateMenuItem("Split Terminal", "Ctrl+Shift+5", () => ToggleSplitTerminalView()));
            terminalMenu.DropDownItems.Add(new ToolStripSeparator());
            terminalMenu.DropDownItems.Add(CreateMenuItem("Focus Next Terminal", "Ctrl+PageDown", () => SwitchTerminalSessionByDelta(1)));
            terminalMenu.DropDownItems.Add(CreateMenuItem("Focus Previous Terminal", "Ctrl+PageUp", () => SwitchTerminalSessionByDelta(-1)));
            terminalMenu.DropDownItems.Add(new ToolStripSeparator());
            terminalMenu.DropDownItems.Add(CreateMenuItem("Run Task...", "", () => RunBuildTaskInTerminal()));
            terminalMenu.DropDownItems.Add(CreateMenuItem("Run Build Task...", "Ctrl+Shift+B", () => RunBuildTaskInTerminal()));
            terminalMenu.DropDownItems.Add(CreateMenuItem("Run Active File", "", async () =>
            {
                if (activeTab != null) await ExecuteActiveFileInTerminalAsync(activeTab);
            }));
            terminalMenu.DropDownItems.Add(CreateMenuItem("Run Selected Text", "", () =>
            {
                if (activeTab != null)
                {
                    string sel = activeTab.GetSelectedText();
                    if (!string.IsNullOrWhiteSpace(sel))
                    {
                        if (!bottomDockPanel.Visible) ToggleBottomDockVisibility();
                        SwitchBottomDockTab("terminal");
                        if (conPtyTerminalControl != null && conPtyTerminalControl.ActiveSession != null && conPtyTerminalControl.ActiveSession.IsRunning)
                            conPtyTerminalControl.ActiveSession.WriteCommand(sel.Trim());
                        else
                        {
                            var _ = ExecuteTerminalCommandAsync(sel.Trim());
                        }
                    }
                }
            }));
            terminalMenu.DropDownItems.Add(new ToolStripSeparator());
            terminalMenu.DropDownItems.Add(CreateMenuItem("Kill Terminal", "", () => KillActiveTerminalSession()));
            terminalMenu.DropDownItems.Add(CreateMenuItem("Clear", "", () =>
            {
                if (conPtyTerminalControl != null) conPtyTerminalControl.ClearTerminal();
                if (terminalOutput != null) terminalOutput.Clear();
            }));

            // 8. HELP MENU
            ToolStripMenuItem helpMenu = new ToolStripMenuItem("&Help");
            helpMenu.DropDownItems.Add(CreateMenuItem("Welcome", "", () => OpenWelcomeTab()));
            helpMenu.DropDownItems.Add(CreateMenuItem("Show All Commands", "Ctrl+Shift+P", () => OpenCommandPalette(">")));
            helpMenu.DropDownItems.Add(CreateMenuItem("Keyboard Shortcuts Reference", "Ctrl+K Ctrl+R", () => OpenKeyboardShortcutsDialog()));
            helpMenu.DropDownItems.Add(CreateMenuItem("Documentation", "", () =>
            {
                try
                {
                    string localReadme = Path.Combine(workspaceRoot ?? "", "README.md");
                    if (!File.Exists(localReadme))
                        localReadme = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "README.md");
                    if (File.Exists(localReadme))
                        OpenFileInTab(localReadme);
                    else
                        Process.Start("https://github.com/Sambhrantsingh777/codeLess");
                }
                catch { }
            }));
            helpMenu.DropDownItems.Add(new ToolStripSeparator());
            helpMenu.DropDownItems.Add(CreateMenuItem("Multi-Harness Parallel Arena...", "", () => OpenMultiHarnessArenaDialog()));
            helpMenu.DropDownItems.Add(CreateMenuItem("Architecture & Dependency Map...", "", () => OpenArchitectureGraphDialog()));
            helpMenu.DropDownItems.Add(CreateMenuItem("Zero-RAM Time-Travel Checkpoints...", "", () => OpenTimeTravelCheckpointsDialog()));
            helpMenu.DropDownItems.Add(new ToolStripSeparator());
            helpMenu.DropDownItems.Add(CreateMenuItem("Toggle Developer Tools", "Ctrl+Shift+I", () =>
            {
                using (var d = new DeveloperToolsDialog(activeTheme, workspaceRoot, aiEngine != null && aiEngine.IndexedChunks != null ? aiEngine.IndexedChunks.Count : 0))
                    d.ShowDialog(this);
            }));
            helpMenu.DropDownItems.Add(CreateMenuItem("Check for Updates...", "", () => RunUpdateCheck(true)));
            helpMenu.DropDownItems.Add(CreateMenuItem("About", "", () =>
            {
                using (var a = new AboutDialog(activeTheme))
                    a.ShowDialog(this);
            }));

            mainMenuStrip.Items.AddRange(new ToolStripItem[]
            {
                fileMenu, editMenu, selectionMenu, viewMenu, goMenu, runMenu, terminalMenu, helpMenu
            });
        }

        private void LaunchInteractiveSandbox()
        {
            try
            {
                // Open sandbox as an in-memory untitled buffer without writing to the workspace.
                string demoCode =
@"# ============================================================================
# codeLess v3.0 Interactive Binary-Native Sandbox (In-Memory Untitled Buffer)
# Try right now:
#   1. Press Ctrl+K -> type 'add error handling' or 'optimize' -> Accept Diff
#   2. Start typing 'def ' or 'try' on a new line -> press Tab for Ghost Text
#   3. Click left gutter to toggle red breakpoints or fold code blocks
#   4. Press Ctrl+S to Save As a .py file and press F5 to execute
# ============================================================================

import time
import math

class QuantumTaskScheduler:
    def __init__(self, worker_name: str = 'codeLess-Native-Core'):
        self.worker_name = worker_name
        self.completed_jobs = []

    def compute_harmonic_series(self, limit: int = 2500) -> float:
        total = 0.0
        for n in range(1, limit + 1):
            total += math.sin(n) * math.cos(n / 2.0) / math.sqrt(n)
        return round(total, 6)

    def run_benchmark(self):
        t0 = time.perf_counter()
        score = self.compute_harmonic_series(10000)
        elapsed_ms = round((time.perf_counter() - t0) * 1000.0, 3)
        print(f'[{self.worker_name}] Harmonic Score = {score} (Computed in {elapsed_ms} ms)')

if __name__ == '__main__':
    scheduler = QuantumTaskScheduler()
    scheduler.run_benchmark()
";
                int idx = 1;
                while (openTabs.Any(t => string.Equals(t.FileName, "Untitled-" + idx, StringComparison.OrdinalIgnoreCase)))
                    idx++;
                string untitledName = "Untitled-" + idx;
                EditorTabBuffer tab = new EditorTabBuffer
                {
                    FilePath = "",
                    RelativePath = untitledName,
                    FileName = untitledName,
                    Language = "py",
                    Content = demoCode.Replace("\r\n", "\n"),
                    SavedContent = "",
                    IsUntitled = true,
                    IsDirty = false,
                    WordWrap = settings != null && settings.WordWrap
                };
                tab.SyncLinesFromContent();
                LlmHarnessAndAgentEngine.AnalyzeBufferAstAndDiagnostics(tab);
                openTabs.Add(tab);
                activeTab = tab;
                editorControl.LoadBuffer(activeTab);
                RefreshOutlineAndProblems(activeTab);
                RefreshTabStrip();
                RefreshOpenEditorsSection();
                UpdateWindowTitle();
                AppendOutputLog("Interactive Sandbox loaded in memory (" + untitledName + ").");
            }
            catch (Exception ex)
            {
                MessageBox.Show("Could not launch sandbox: " + ex.Message, "codeLess IDE");
            }
        }

        private void WireEditorEvents(CodeLessEditorControl ctrl)
        {
            ctrl.OnSaveRequested += (buf) => SaveTabBuffer(buf);
            ctrl.OnRunFileRequested += async (buf) => await ExecuteActiveFileInTerminalAsync(buf);
            ctrl.OnCtrlKRequested += async (inst, buf) => await HandleCtrlKInlineEditAsync(inst, buf);
            ctrl.OnPinContextRequested += (buf) => AddContextPill("@" + buf.FileName);
            EditorTabBuffer lastSyncedBuf = null;
            bool lastSyncedDirty = false;
            int lastSyncedCount = -1;
            System.Windows.Forms.Timer outlineDebounceTimer = null;
            ctrl.OnBufferModified += (buf) =>
            {
                bool previewCleared = false;
                if (buf != null && buf.IsPreview)
                {
                    buf.IsPreview = false;
                    previewCleared = true;
                }
                bool curDirty = buf != null && buf.IsDirty;
                if (previewCleared || !ReferenceEquals(buf, lastSyncedBuf) || curDirty != lastSyncedDirty || openTabs.Count != lastSyncedCount)
                {
                    RefreshTabStrip();
                    RefreshOpenEditorsSection();
                    UpdateWindowTitle();
                    lastSyncedBuf = buf;
                    lastSyncedDirty = curDirty;
                    lastSyncedCount = openTabs.Count;
                }
                if (buf != null && buf.Lines != null && buf.Lines.Count > 350)
                {
                    if (outlineDebounceTimer == null)
                    {
                        outlineDebounceTimer = new System.Windows.Forms.Timer { Interval = 300 };
                        outlineDebounceTimer.Tick += (s, ev) =>
                        {
                            outlineDebounceTimer.Stop();
                            if (!this.IsDisposed) RefreshOutlineAndProblems(activeTab);
                        };
                    }
                    outlineDebounceTimer.Stop();
                    outlineDebounceTimer.Start();
                }
                else
                {
                    RefreshOutlineAndProblems(activeTab);
                }
                RefreshChangesetReviewBar();
                UpdateStatusBarCursorAndLanguage(buf);
            };
            ctrl.OnCursorOrSelectionChanged += (buf) =>
            {
                UpdateStatusBarCursorAndLanguage(buf);
                if (ctrl == editorControl && (buf == null || activeTab == null || editorControl.IsWelcomeVisible))
                    RefreshOutlineAndProblems(null);
                if (buf != null && !string.IsNullOrEmpty(buf.FilePath))
                    RecordNavigationHistory(buf.FilePath, buf.CursorLine + 1);
            };
            ctrl.OnToggleSplitEditorRequested += () => ToggleSplitEditorView();
            ctrl.OnOpenFolderClicked += () => PromptOpenFolderDialog();
            ctrl.OnOpenFileClicked += () => PromptOpenFileDialog();
            ctrl.OnNewFileClicked += () => PromptCreateNewFile(workspaceRoot);
            ctrl.OnNewProjectWizardClicked += () => PromptNewProjectWizard();
            ctrl.OnConfigureByokClicked += () => OpenByokConfigurationDialog();
            ctrl.OnLaunchDemoSandboxClicked += () => LaunchInteractiveSandbox();
            ctrl.OnOpenRecentWorkspaceClicked += (path) =>
            {
                if (File.Exists(path)) OpenFileInTab(path);
                else LoadWorkspace(path, restorePreviousTabs: false);
            };
            ctrl.OnGoToDefinitionRequested += HandleGoToDefinition;
            ctrl.WorkspaceRootProvider = () => workspaceRoot;
            ctrl.OnLinkActivated += (buf, target) => HandleEditorLinkActivated(buf, target);
            ctrl.OnGoToReferencesRequested += (buf, sym) => HandleGoToReferences(sym);
            ctrl.OnPeekDefinitionRequested += (buf, sym) => HandlePeekDefinition(sym);
            ctrl.OnCommandPaletteRequested += () => OpenCommandPalette(">");
            ctrl.OnChordStatusChanged += (msg) =>
            {
                isCtrlKChordPending = !string.IsNullOrEmpty(msg);
                if (statusChordLabel != null)
                {
                    statusChordLabel.Text = msg ?? "";
                    statusChordLabel.Visible = !string.IsNullOrEmpty(msg);
                    LayoutStatusBarItems();
                }
            };
            ctrl.OnUnhandledCtrlKChord += (keyData) => TryDispatchCtrlKSecondKey(keyData);
            ctrl.OnRenameSymbolRequested += (buf, sym) => PromptRenameSymbolInEditor(sym);
            ctrl.AsyncGhostCompletionProvider = async (buf) =>
            {
                if (buf == null || aiEngine == null) return null;
                int vIdx = quickVendorCombo != null ? Math.Max(0, quickVendorCombo.SelectedIndex) : 0;
                ByokVendor v = (vIdx >= 0 && vIdx < aiEngine.Vendors.Count) ? aiEngine.Vendors[vIdx] : null;
                return await aiEngine.RequestModelGhostCompletionAsync(v, buf);
            };
            ctrl.OnSendSelectionToAgentRequested += (buf) =>
            {
                if (buf == null) return;
                if (!rightDock.Visible) ToggleRightDockVisibility();
                AddContextPill("@" + buf.FileName);
                string sel = buf.GetSelectedText();
                if (!string.IsNullOrEmpty(sel))
                {
                    int sL, sC, eL, eC;
                    buf.GetNormalizedSelection(out sL, out sC, out eL, out eC);
                    AddContextPill(string.Format("@{0}:L{1}-L{2}", buf.FileName, sL + 1, eL + 1));
                }
                promptInputBox.Focus();
                statusLeftLabel.Text = "Attached " + buf.FileName;
            };
            ctrl.OnFontSizeChanged += (pt) =>
            {
                settings.EditorFontSize = pt;
                if (editorControl != null && Math.Abs(editorControl.EditorFontSize - pt) > 0.05f)
                    editorControl.EditorFontSize = pt;
                if (splitEditorControl != null && Math.Abs(splitEditorControl.EditorFontSize - pt) > 0.05f)
                    splitEditorControl.EditorFontSize = pt;
            };
            ctrl.OnWordWrapChanged += (wrap) =>
            {
                foreach (EditorTabBuffer t in openTabs) t.WordWrap = wrap;
                if (editorControl != null)
                {
                    if (editorControl.CurrentBuffer != null) editorControl.CurrentBuffer.WordWrap = wrap;
                    editorControl.UpdateCursorStatusLabel();
                    editorControl.Invalidate(true);
                }
                if (splitEditorControl != null)
                {
                    if (splitEditorControl.CurrentBuffer != null) splitEditorControl.CurrentBuffer.WordWrap = wrap;
                    splitEditorControl.UpdateCursorStatusLabel();
                    splitEditorControl.Invalidate(true);
                }
                UpdateTelemetryBadges();
            };
        }

        private void HandleGoToReferences(string symbolName)
        {
            string q = (symbolName ?? "").Trim();
            if (string.IsNullOrEmpty(q) && activeTab != null && activeTab.Lines != null && activeTab.CursorLine >= 0 && activeTab.CursorLine < activeTab.Lines.Count)
            {
                string ln = activeTab.Lines[activeTab.CursorLine] ?? "";
                int col = Math.Max(0, Math.Min(activeTab.CursorCol, ln.Length));
                int s = col, e = col;
                while (s > 0 && (char.IsLetterOrDigit(ln[s - 1]) || ln[s - 1] == '_')) s--;
                while (e < ln.Length && (char.IsLetterOrDigit(ln[e]) || ln[e] == '_')) e++;
                if (e > s) q = ln.Substring(s, e - s);
            }
            if (string.IsNullOrEmpty(q)) return;
            leftSidebar.Visible = true;
            SwitchSidebarView("search");
            ExecuteWorkspaceSearchAsync(q);
        }

        private void HandlePeekDefinition(string symbolName)
        {
            HandleGoToDefinition(symbolName);
        }

        /// <summary>VS Code "follow link" for Ctrl+Click / F12: URLs, imports / includes / paths, imported or local symbols.</summary>
        private void HandleEditorLinkActivated(EditorTabBuffer source, EditorLinkTarget target)
        {
            if (target == null) return;
            if (target.Kind == EditorLinkTarget.KindUrl)
            {
                if (EditorLinkResolver.OpenExternalUrl(target.Url)) statusLeftLabel.Text = "Opened " + target.Url;
                return;
            }
            if (string.IsNullOrEmpty(target.FilePath))
            {
                if (!string.IsNullOrEmpty(target.SymbolName)) HandleGoToDefinition(target.SymbolName);
                return;
            }
            if (!File.Exists(target.FilePath))
            {
                statusLeftLabel.Text = "File not found: " + target.FilePath;
                return;
            }
            EditorTabBuffer origin = source ?? activeTab;
            if (origin != null) RecordNavigationHistory(origin.FilePath, origin.CursorLine + 1);
            EditorTabBuffer tab = OpenFileInTab(target.FilePath);
            if (tab == null) return;
            int defLine, defCol;
            if (target.Line > 0)
                editorControl.JumpToLineAndColumn(target.Line, Math.Max(1, target.Column));
            else if (!string.IsNullOrEmpty(target.SymbolName) && tab.Lines != null &&
                     EditorLinkResolver.FindDefinitionInLines(tab.Lines, target.SymbolName, true, out defLine, out defCol))
                editorControl.JumpToLineAndColumn(defLine + 1, defCol + 1);
            RecordNavigationHistory(tab.FilePath, tab.CursorLine + 1);
            string shown = Path.GetFileName(target.FilePath);
            statusLeftLabel.Text = string.IsNullOrEmpty(target.SymbolName)
                ? "Opened " + shown
                : string.Format("Definition: {0} ({1}:{2})", target.SymbolName, shown, tab.CursorLine + 1);
        }

        private void HandleGoToDefinition(string symbolName)
        {
            if (string.IsNullOrWhiteSpace(symbolName)) return;
            string clean = symbolName.Trim();

            if (activeTab != null && activeTab.Symbols != null)
            {
                var localMatch = activeTab.Symbols.FirstOrDefault(s => string.Equals(s.Name, clean, StringComparison.OrdinalIgnoreCase));
                if (localMatch != null)
                {
                    RecordNavigationHistory(activeTab.FilePath, activeTab.CursorLine + 1);
                    editorControl.JumpToLine(localMatch.Line);
                    RecordNavigationHistory(activeTab.FilePath, localMatch.Line);
                    statusLeftLabel.Text = string.Format("Definition: {0} (Ln {1})", clean, localMatch.Line);
                    return;
                }
            }

            int dl, dc;
            if (activeTab != null && activeTab.Lines != null &&
                EditorLinkResolver.FindDefinitionInLines(activeTab.Lines, clean, false, out dl, out dc) && dl != activeTab.CursorLine)
            {
                RecordNavigationHistory(activeTab.FilePath, activeTab.CursorLine + 1);
                editorControl.JumpToLineAndColumn(dl + 1, dc + 1);
                RecordNavigationHistory(activeTab.FilePath, dl + 1);
                statusLeftLabel.Text = string.Format("Definition: {0} (Ln {1})", clean, dl + 1);
                return;
            }

            var wsMatch = aiEngine.WorkspaceSymbolMap.FirstOrDefault(s => string.Equals(s.SymbolName, clean, StringComparison.OrdinalIgnoreCase));
            if (wsMatch != null && File.Exists(wsMatch.FilePath))
            {
                if (activeTab != null) RecordNavigationHistory(activeTab.FilePath, activeTab.CursorLine + 1);
                OpenFileInTab(wsMatch.FilePath);
                editorControl.JumpToLine(wsMatch.Line);
                RecordNavigationHistory(wsMatch.FilePath, wsMatch.Line);
                statusLeftLabel.Text = string.Format("Definition: {0} ({1}:{2})", clean, wsMatch.RelativePath, wsMatch.Line);
            }
            else
            {
                statusLeftLabel.Text = "No definition found for '" + clean + "'";
            }
        }

        public void PromptRenameSymbolInEditor(string initialSymbol = null)
        {
            if (activeTab == null) return;
            string oldSym = !string.IsNullOrWhiteSpace(initialSymbol)
                ? initialSymbol.Trim()
                : (activeTab.HasSelection() ? activeTab.GetSelectedText().Trim() : "");
            if (string.IsNullOrEmpty(oldSym))
            {
                // Extract word at cursor
                string ln = (activeTab.CursorLine >= 0 && activeTab.CursorLine < activeTab.Lines.Count) ? activeTab.Lines[activeTab.CursorLine] : "";
                int col = Math.Max(0, Math.Min(activeTab.CursorCol, ln.Length));
                int s = col, e = col;
                while (s > 0 && (char.IsLetterOrDigit(ln[s - 1]) || ln[s - 1] == '_')) s--;
                while (e < ln.Length && (char.IsLetterOrDigit(ln[e]) || ln[e] == '_')) e++;
                if (e > s) oldSym = ln.Substring(s, e - s);
            }
            if (string.IsNullOrEmpty(oldSym)) return;

            using (var dlg = new PromptInputDialog("Rename Symbol (F2)", "Rename '" + oldSym + "' across active file to:", oldSym, activeTheme))
            {
                if (dlg.ShowDialog(this) == DialogResult.OK && !string.IsNullOrWhiteSpace(dlg.InputValue) && dlg.InputValue.Trim() != oldSym)
                {
                    int replaced = editorControl.RenameSymbolInCurrentBuffer(oldSym, dlg.InputValue.Trim());
                    statusLeftLabel.Text = string.Format("Renamed '{0}' to '{1}'", oldSym, dlg.InputValue.Trim());
                }
            }
        }

        private void ToggleSplitEditorView()
        {
            SplitEditorInDirection(DockStyle.Right, false);
        }

        public void SplitEditorInDirection(DockStyle splitDock = DockStyle.Right, bool forceOpen = false)
        {
            if (forceOpen)
                isSplitEditorActive = true;
            else if (isSplitEditorActive && splitEditorControl != null && splitEditorControl.Dock != splitDock)
                isSplitEditorActive = true;
            else
                isSplitEditorActive = !isSplitEditorActive;

            splitEditorControl.Dock = splitDock;
            splitEditorControl.Visible = isSplitEditorActive;
            if (isSplitEditorActive)
            {
                if (splitDock == DockStyle.Bottom || splitDock == DockStyle.Top)
                    splitEditorControl.Height = Math.Max(180, editorsSplitContainer.Height / 2);
                else
                    splitEditorControl.Width = Math.Max(320, editorsSplitContainer.Width / 2);
                EditorTabBuffer secondBuf = openTabs.Count > 1
                    ? openTabs.FirstOrDefault(t => t != activeTab)
                    : activeTab;
                splitEditorControl.LoadBuffer(secondBuf);
            }
        }

        private void RefreshOpenEditorsSection()
        {
            if (openEditorsListBox == null || openEditorsSectionPanel == null) return;
            bool hasFolder = !string.IsNullOrEmpty(workspaceRoot) && Directory.Exists(workspaceRoot);
            if (!hasFolder && openTabs.Count == 0)
            {
                openEditorsSectionPanel.Visible = false;
                return;
            }
            openEditorsSectionPanel.Visible = true;
            openEditorsListBox.Items.Clear();
            foreach (EditorTabBuffer t in openTabs)
            {
                string dirty = t.IsDirty ? "● " : "  ";
                string relDir = !string.IsNullOrEmpty(t.RelativePath) ? Path.GetDirectoryName(t.RelativePath) : "";
                string suffix = !string.IsNullOrEmpty(relDir) && relDir != "." ? ("  " + relDir.Replace('\\', '/')) : "";
                openEditorsListBox.Items.Add(dirty + t.FileName + suffix);
            }
            int desiredH = openTabs.Count == 0 ? 22 : Math.Min(126, 26 + openTabs.Count * 20);
            openEditorsSectionPanel.Height = openEditorsListBox.Visible ? desiredH : 22;
            openEditorsListBox.Invalidate();
        }

        private Button CreateExplorerVectorIconButton(string iconKind, Point loc)
        {
            return CreateDynamicVectorIconButton(() => iconKind, loc, 22, 22);
        }

        private Button CreateDynamicVectorIconButton(Func<string> iconKindGetter, Point loc, int width = 22, int height = 22)
        {
            Button btn = new Button
            {
                Text = "",
                Location = loc,
                Size = new Size(width, height),
                FlatStyle = FlatStyle.Flat,
                Cursor = Cursors.Hand,
                UseMnemonic = false,
                Tag = "activity_icon"
            };
            btn.FlatAppearance.BorderSize = 0;
            bool isHovered = false;
            btn.MouseEnter += (s, e) => { isHovered = true; btn.Invalidate(); };
            btn.MouseLeave += (s, e) => { isHovered = false; btn.Invalidate(); };
            btn.Paint += (s, e) =>
            {
                Control parentCtrl = btn.Parent;
                Color bg = parentCtrl != null && parentCtrl.BackColor != Color.Transparent
                    ? parentCtrl.BackColor
                    : (activeTheme != null ? activeTheme.BgSidebar : Color.FromArgb(18, 21, 28));
                e.Graphics.Clear(bg);
                if (isHovered)
                {
                    Color hovBg = activeTheme != null ? activeTheme.BgHover : Color.FromArgb(45, 50, 62);
                    e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
                    using (GraphicsPath gp = StudioTheme.CreateRoundRect(new Rectangle(1, 1, Math.Max(4, btn.Width - 3), Math.Max(4, btn.Height - 3)), 4))
                    using (SolidBrush hb = new SolidBrush(hovBg))
                    {
                        e.Graphics.FillPath(hb, gp);
                    }
                    e.Graphics.SmoothingMode = SmoothingMode.Default;
                }
                Color fg = activeTheme != null
                    ? (isHovered ? activeTheme.FgPrimary : activeTheme.FgSecondary)
                    : Color.FromArgb(160, 174, 192);
                string kind = iconKindGetter != null ? iconKindGetter() : "";
                int gw = Math.Min(16, Math.Max(12, btn.Width - 4));
                int gh = Math.Min(16, Math.Max(12, btn.Height - 4));
                Rectangle glyphRect = new Rectangle((btn.Width - gw) / 2, (btn.Height - gh) / 2, gw, gh);
                CodeLessVectorIcons.DrawIcon(e.Graphics, kind, glyphRect, fg, 1.4f);
            };
            return btn;
        }

        private void BuildExplorerSidebarPanel()
        {
            explorerContainer = new Panel { Dock = DockStyle.Fill };

            explorerToolbar = new Panel
            {
                Dock = DockStyle.Top,
                Height = 24,
                Padding = new Padding(6, 1, 6, 1)
            };

            explorerFolderHeaderLabel = new Label
            {
                Text = "▾ WORKSPACE",
                Location = new Point(6, 3),
                Size = new Size(124, 18),
                Font = new Font("Segoe UI Semibold", 7.8f, FontStyle.Bold),
                Cursor = Cursors.Hand,
                AutoEllipsis = true,
                UseMnemonic = false
            };
            explorerFolderHeaderLabel.Click += (s, e) =>
            {
                if (explorerTree != null && !string.IsNullOrEmpty(workspaceRoot))
                {
                    bool exp = explorerTree.ToggleRootFolderExpanded();
                    explorerFolderHeaderLabel.Text = (exp ? "▾ " : "▸ ") + Path.GetFileName(workspaceRoot).ToUpperInvariant();
                }
            };

            Button newFileBtn = CreateExplorerVectorIconButton("new-file", new Point(136, 1));
            newFileBtn.Click += (s, e) => PromptCreateNewFile(GetSelectedExplorerDirectory());
            explorerNewFileBtn = newFileBtn;

            Button newFolderBtn = CreateExplorerVectorIconButton("new-folder", new Point(160, 1));
            newFolderBtn.Click += (s, e) => PromptCreateNewFolder(GetSelectedExplorerDirectory());
            explorerNewFolderBtn = newFolderBtn;

            Button refreshBtn = CreateExplorerVectorIconButton("refresh", new Point(184, 1));
            refreshBtn.Click += (s, e) =>
            {
                if (!string.IsNullOrEmpty(workspaceRoot) && Directory.Exists(workspaceRoot))
                    AfterExplorerFileOperation(null);
            };
            explorerRefreshBtn = refreshBtn;

            Button collapseAllBtn = CreateExplorerVectorIconButton("collapse-all", new Point(208, 1));
            collapseAllBtn.Click += (s, e) =>
            {
                if (explorerTree != null) explorerTree.CollapseAllFolders();
            };
            explorerCollapseBtn = collapseAllBtn;

            ToolTip expTips = new ToolTip();
            expTips.SetToolTip(newFileBtn, "New File...");
            expTips.SetToolTip(newFolderBtn, "New Folder...");
            expTips.SetToolTip(refreshBtn, "Refresh Explorer");
            expTips.SetToolTip(collapseAllBtn, "Collapse Folders in Explorer");

            explorerToolbar.Controls.AddRange(new Control[] { explorerFolderHeaderLabel, newFileBtn, newFolderBtn, refreshBtn, collapseAllBtn });
            explorerToolbar.Resize += (s, e) =>
            {
                int ew = explorerToolbar.Width;
                collapseAllBtn.Location = new Point(ew - 24, 1);
                refreshBtn.Location = new Point(collapseAllBtn.Left - 23, 1);
                newFolderBtn.Location = new Point(refreshBtn.Left - 23, 1);
                newFileBtn.Location = new Point(newFolderBtn.Left - 23, 1);
                if (string.IsNullOrEmpty(workspaceRoot))
                    explorerFolderHeaderLabel.Width = Math.Max(80, ew - 12);
                else
                    explorerFolderHeaderLabel.Width = Math.Max(60, newFileBtn.Left - 10);
            };

            // Collapsible OPEN EDITORS section at top of Explorer
            openEditorsSectionPanel = new Panel
            {
                Dock = DockStyle.Top,
                Height = 68
            };
            Label openEditorsHdr = new Label
            {
                Dock = DockStyle.Top,
                Height = 22,
                Padding = new Padding(8, 4, 4, 2),
                Font = new Font("Segoe UI Semibold", 7.6f, FontStyle.Bold),
                Cursor = Cursors.Hand,
                Text = "▾ OPEN EDITORS"
            };
            openEditorsListBox = new CodeLessListBox
            {
                Dock = DockStyle.Fill,
                BorderStyle = BorderStyle.None,
                Font = new Font("Segoe UI", 8.5f),
                ItemHeight = 20
            };
            openEditorsHdr.Click += (s, e) =>
            {
                openEditorsListBox.Visible = !openEditorsListBox.Visible;
                openEditorsHdr.Text = (openEditorsListBox.Visible ? "▾ " : "▸ ") + "OPEN EDITORS";
                RefreshOpenEditorsSection();
            };
            openEditorsListBox.SelectedIndexChanged += (s, e) =>
            {
                int idx = openEditorsListBox.SelectedIndex;
                if (idx >= 0 && idx < openTabs.Count)
                {
                    activeTab = openTabs[idx];
                    editorControl.LoadBuffer(activeTab);
                    RefreshOutlineAndProblems(activeTab);
                    RefreshTabStrip();
                    UpdateWindowTitle();
                }
            };
            openEditorsSectionPanel.Controls.Add(openEditorsListBox);
            openEditorsSectionPanel.Controls.Add(openEditorsHdr);

            // Bottom AST Symbol Outline Panel inside Explorer
            outlineBottomPanel = new Panel
            {
                Dock = DockStyle.Bottom,
                Height = 165
            };
            Label outlineHdr = new Label
            {
                Dock = DockStyle.Top,
                Height = 22,
                Padding = new Padding(8, 4, 4, 2),
                Font = new Font("Segoe UI Semibold", 7.6f, FontStyle.Bold),
                Cursor = Cursors.Hand,
                Text = "▾ OUTLINE"
            };
            outlineHdr.Click += (s, e) =>
            {
                outlineListBox.Visible = !outlineListBox.Visible;
                outlineHdr.Text = (outlineListBox.Visible ? "▾ " : "▸ ") + "OUTLINE";
                outlineBottomPanel.Height = outlineListBox.Visible ? 165 : 22;
            };
            outlineListBox = new CodeLessListBox
            {
                Dock = DockStyle.Fill,
                BorderStyle = BorderStyle.None,
                Font = new Font("Segoe UI", 8.4f),
                ItemHeight = 20
            };
            outlineListBox.SelectedIndexChanged += (s, e) =>
            {
                int idx = outlineListBox.SelectedIndex;
                if (idx < 0) return;
                if (activeTab != null && activeTab.Symbols != null && activeTab.Symbols.Count > 0)
                {
                    if (idx < activeTab.Symbols.Count)
                        editorControl.JumpToLine(activeTab.Symbols[idx].Line);
                }
                else if (idx < outlineFallbackTargets.Count)
                {
                    Tuple<string, int> target = outlineFallbackTargets[idx];
                    if (!string.IsNullOrEmpty(target.Item1) && File.Exists(target.Item1))
                    {
                        OpenFileInTab(target.Item1);
                        editorControl.JumpToLine(target.Item2);
                    }
                }
            };
            outlineBottomPanel.Controls.Add(outlineListBox);
            outlineBottomPanel.Controls.Add(outlineHdr);

            outlineSplitter = new Splitter
            {
                Dock = DockStyle.Bottom,
                Height = 3,
                MinExtra = 100,
                MinSize = 22
            };

            explorerContextMenu = new ContextMenuStrip { Renderer = new DarkStudioMenuRenderer(() => activeTheme) };
            ToolStripMenuItem expOpenItem = CreateMenuItem("Open", "", () =>
            {
                string path = GetSelectedNodePath();
                if (!string.IsNullOrEmpty(path) && File.Exists(path))
                    OpenFileInTab(path);
            });
            ToolStripMenuItem expOpenSideItem = CreateMenuItem("Open to the Side", "Ctrl+Enter", () =>
            {
                string path = GetSelectedNodePath();
                if (!string.IsNullOrEmpty(path) && File.Exists(path))
                {
                    EditorTabBuffer buf = OpenFileInTab(path);
                    if (!isSplitEditorActive) SplitEditorInDirection(DockStyle.Right, forceOpen: true);
                    else if (splitEditorControl != null && buf != null) splitEditorControl.LoadBuffer(buf);
                }
            });
            ToolStripMenuItem expRevealItem = CreateMenuItem("Reveal in File Explorer", "Shift+Alt+R", () =>
            {
                string path = GetSelectedNodePath();
                if (!string.IsNullOrEmpty(path))
                    Process.Start("explorer.exe", File.Exists(path) ? "/select,\"" + path + "\"" : "\"" + path + "\"");
            });
            ToolStripMenuItem expOpenTermItem = CreateMenuItem("Open in Integrated Terminal", "", () =>
            {
                string dir = GetSelectedExplorerDirectory();
                if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir))
                {
                    terminalWorkingDir = dir;
                    if (!bottomDockPanel.Visible) ToggleBottomDockVisibility();
                    SwitchBottomDockTab("terminal");
                    if (terminalSessions.Count == 0)
                        CreateNewTerminalSession();
                    if (conPtyTerminalControl != null && conPtyTerminalControl.ActiveSession != null && conPtyTerminalControl.ActiveSession.IsRunning)
                    {
                        conPtyTerminalControl.ActiveSession.NavigateToDirectory(dir, false);
                    }
                }
            });
            ToolStripMenuItem expSelectCompareItem = CreateMenuItem("Select for Compare", "", () =>
            {
                string path = GetSelectedNodePath();
                if (!string.IsNullOrEmpty(path) && File.Exists(path))
                {
                    explorerCompareSourcePath = path;
                    statusLeftLabel.Text = "Selected for compare: " + Path.GetFileName(path);
                }
            });
            ToolStripMenuItem expCompareWithSelectedItem = CreateMenuItem("Compare with Selected", "", () =>
            {
                string path = GetSelectedNodePath();
                if (!string.IsNullOrEmpty(path) && File.Exists(path) && !string.IsNullOrEmpty(explorerCompareSourcePath) && File.Exists(explorerCompareSourcePath))
                {
                    EditorTabBuffer baseTab = OpenFileInTab(explorerCompareSourcePath);
                    if (baseTab != null)
                    {
                        string otherContent = File.ReadAllText(path);
                        editorControl.StageProposedContent(otherContent, "Compare: " + Path.GetFileName(explorerCompareSourcePath) + " ↔ " + Path.GetFileName(path));
                        RefreshTabStrip();
                    }
                }
            });
            ToolStripMenuItem expFindInFolderItem = CreateMenuItem("Find in Folder...", "Shift+Alt+F", () =>
            {
                leftSidebar.Visible = true;
                SwitchSidebarView("search");
                searchInput.Focus();
            });
            ToolStripMenuItem expCutItem = CreateMenuItem("Cut", "Ctrl+X", () =>
            {
                string path = GetSelectedNodePath();
                if (!string.IsNullOrEmpty(path) && !string.Equals(path, workspaceRoot, StringComparison.OrdinalIgnoreCase))
                {
                    explorerClipboardPath = path;
                    explorerClipboardIsCut = true;
                    statusLeftLabel.Text = "Cut " + Path.GetFileName(path);
                }
            });
            ToolStripMenuItem expCopyItem = CreateMenuItem("Copy", "Ctrl+C", () =>
            {
                string path = GetSelectedNodePath();
                if (!string.IsNullOrEmpty(path) && !string.Equals(path, workspaceRoot, StringComparison.OrdinalIgnoreCase))
                {
                    explorerClipboardPath = path;
                    explorerClipboardIsCut = false;
                    statusLeftLabel.Text = "Copied " + Path.GetFileName(path);
                }
            });
            ToolStripMenuItem expPasteItem = CreateMenuItem("Paste", "Ctrl+V", () => PasteExplorerClipboardItem());
            ToolStripMenuItem expCopyPathItem = CreateMenuItem("Copy Path", "Shift+Alt+C", () =>
            {
                string path = GetSelectedNodePath();
                if (!string.IsNullOrEmpty(path)) CodeLessVirtualCanvas.SafeSetClipboardText(path);
            });
            ToolStripMenuItem expCopyRelPathItem = CreateMenuItem("Copy Relative Path", "Ctrl+K Ctrl+Shift+C", () =>
            {
                string path = GetSelectedNodePath();
                if (!string.IsNullOrEmpty(path))
                    CodeLessVirtualCanvas.SafeSetClipboardText(LlmHarnessAndAgentEngine.GetRelativePath(workspaceRoot, path));
            });
            ToolStripMenuItem expNewFileItem = CreateMenuItem("New File...", "", () => PromptCreateNewFile(GetSelectedExplorerDirectory()));
            ToolStripMenuItem expNewFolderItem = CreateMenuItem("New Folder...", "", () => PromptCreateNewFolder(GetSelectedExplorerDirectory()));
            ToolStripMenuItem expRenameItem = CreateMenuItem("Rename...", "F2", () => PromptRenameSelectedNode());
            ToolStripMenuItem expDeleteItem = CreateMenuItem("Delete", "Delete", () => PromptDeleteSelectedNode());

            explorerContextMenu.Items.AddRange(new ToolStripItem[]
            {
                expOpenItem,
                expOpenSideItem,
                expRevealItem,
                expOpenTermItem,
                new ToolStripSeparator(),
                expSelectCompareItem,
                expCompareWithSelectedItem,
                expFindInFolderItem,
                new ToolStripSeparator(),
                expCutItem,
                expCopyItem,
                expPasteItem,
                new ToolStripSeparator(),
                expCopyPathItem,
                expCopyRelPathItem,
                new ToolStripSeparator(),
                expNewFileItem,
                expNewFolderItem,
                expRenameItem,
                expDeleteItem
            });
            explorerContextMenu.Opening += (s, e) =>
            {
                string selPath = GetSelectedNodePath();
                bool isFile = !string.IsNullOrEmpty(selPath) && File.Exists(selPath);
                bool isRoot = string.IsNullOrEmpty(selPath) || string.Equals(selPath, workspaceRoot, StringComparison.OrdinalIgnoreCase);
                expOpenItem.Visible = isFile;
                expOpenSideItem.Visible = isFile;
                expSelectCompareItem.Visible = isFile;
                expCompareWithSelectedItem.Visible = isFile && !string.IsNullOrEmpty(explorerCompareSourcePath) && File.Exists(explorerCompareSourcePath) && !string.Equals(explorerCompareSourcePath, selPath, StringComparison.OrdinalIgnoreCase);
                expPasteItem.Enabled = !string.IsNullOrEmpty(explorerClipboardPath) && (File.Exists(explorerClipboardPath) || Directory.Exists(explorerClipboardPath));
                expRenameItem.Enabled = !isRoot;
                expDeleteItem.Enabled = !isRoot;
                expCutItem.Enabled = !isRoot;
                expCopyItem.Enabled = !isRoot;
            };

            explorerTree = new CodeLessExplorerTree
            {
                Dock = DockStyle.Fill,
                BorderStyle = BorderStyle.None,
                ShowLines = false,
                FullRowSelect = true,
                HideSelection = false,
                ItemHeight = 22,
                Font = new Font("Segoe UI", 9.0f),
                ContextMenuStrip = explorerContextMenu
            };
            explorerTree.OnOpenFolderEmptyStateClicked += () => PromptOpenFolderDialog();
            explorerTree.OnCloneRepoEmptyStateClicked += () => PromptCloneRepositoryDialog();
            explorerTree.BeforeNodeExpand += (node) => EnsureDirectoryNodePopulated(node);
            explorerTree.OnInlineRenameCommitted += (oldPath, newName) =>
            {
                try
                {
                    if (string.IsNullOrEmpty(oldPath) || string.IsNullOrWhiteSpace(newName)) return;
                    string newPath;
                    string nameErr = ValidateNewEntryName(Path.GetDirectoryName(oldPath), newName, out newPath);
                    if (nameErr != null) { ShowToastNotification("Rename Failed", nameErr); return; }
                    if (string.Equals(oldPath, newPath, StringComparison.Ordinal)) return;
                    bool caseOnly = string.Equals(oldPath, newPath, StringComparison.OrdinalIgnoreCase);
                    if (!caseOnly && (File.Exists(newPath) || Directory.Exists(newPath)))
                    {
                        ShowToastNotification("Rename Failed", "A file or folder '" + Path.GetFileName(newPath) + "' already exists at this location.");
                        return;
                    }
                    if (caseOnly)
                    {
                        MovePathCaseSafe(oldPath, newPath);
                        RepathOpenTabsForFileOrDirectoryMove(oldPath, newPath, Directory.Exists(newPath));
                    }
                    else if (File.Exists(oldPath))
                    {
                        string oldName = Path.GetFileName(oldPath);
                        string oldRel = LlmHarnessAndAgentEngine.GetRelativePath(workspaceRoot, oldPath);
                        bool hadPill = attachedContextPills.Any(p => DoesPillMatchFile(p, oldPath, oldName, oldRel));
                        RemoveContextPillsForFile(oldPath, oldName, oldRel);
                        File.Move(oldPath, newPath);
                        RepathOpenTabsForFileOrDirectoryMove(oldPath, newPath, isDirectory: false);
                        if (hadPill)
                            AddContextPill("@" + Path.GetFileName(newPath));
                    }
                    else if (Directory.Exists(oldPath))
                    {
                        Directory.Move(oldPath, newPath);
                        RepathOpenTabsForFileOrDirectoryMove(oldPath, newPath, isDirectory: true);
                    }
                    AfterExplorerFileOperation(newPath);
                    RefreshTabStrip();
                }
                catch (Exception ex)
                {
                    if (statusLeftLabel != null) statusLeftLabel.Text = "Rename failed: " + ex.Message;
                    ShowToastNotification("Rename Failed", ex.Message);
                }
            };

            explorerTree.NodeMouseClick += (s, e) =>
            {
                explorerTree.SelectedNode = e.Node;
                if (e.Button == MouseButtons.Left)
                {
                    string filePath = e.Node.Tag as string;
                    if (!string.IsNullOrEmpty(filePath) && File.Exists(filePath))
                    {
                        OpenFileInTab(filePath);
                    }
                }
            };

            explorerTree.KeyDown += (s, e) =>
            {
                if (e.KeyCode == Keys.F2)
                {
                    e.SuppressKeyPress = true;
                    explorerTree.StartInlineRename();
                }
                else if (e.KeyCode == Keys.Delete) { e.SuppressKeyPress = true; PromptDeleteSelectedNode(); }
                else if (e.Control && e.KeyCode == Keys.C)
                {
                    string p = GetSelectedNodePath();
                    if (!string.IsNullOrEmpty(p) && !string.Equals(p, workspaceRoot, StringComparison.OrdinalIgnoreCase))
                    {
                        explorerClipboardPath = p;
                        explorerClipboardIsCut = false;
                    }
                }
                else if (e.Control && e.KeyCode == Keys.X)
                {
                    string p = GetSelectedNodePath();
                    if (!string.IsNullOrEmpty(p) && !string.Equals(p, workspaceRoot, StringComparison.OrdinalIgnoreCase))
                    {
                        explorerClipboardPath = p;
                        explorerClipboardIsCut = true;
                    }
                }
                else if (e.Control && e.KeyCode == Keys.V)
                {
                    PasteExplorerClipboardItem();
                }
            };

            explorerContainer.Controls.Add(explorerTree);
            explorerContainer.Controls.Add(outlineSplitter);
            explorerContainer.Controls.Add(outlineBottomPanel);
            explorerContainer.Controls.Add(explorerToolbar);
            explorerContainer.Controls.Add(openEditorsSectionPanel);
        }

        private string explorerCompareSourcePath = "";
        private string explorerClipboardPath = "";
        private bool explorerClipboardIsCut = false;

        private void PasteExplorerClipboardItem()
        {
            if (string.IsNullOrEmpty(explorerClipboardPath)) return;
            string destDir = GetSelectedExplorerDirectory();
            if (string.IsNullOrEmpty(destDir) || !Directory.Exists(destDir)) return;
            try
            {
                string baseName = Path.GetFileName(explorerClipboardPath);
                string targetPath = Path.Combine(destDir, baseName);
                if (File.Exists(explorerClipboardPath))
                {
                    if (string.Equals(explorerClipboardPath, targetPath, StringComparison.OrdinalIgnoreCase))
                    {
                        string ext = Path.GetExtension(baseName);
                        string stem = Path.GetFileNameWithoutExtension(baseName);
                        int copyIdx = 1;
                        while (File.Exists(targetPath))
                        {
                            targetPath = Path.Combine(destDir, stem + "_copy" + (copyIdx > 1 ? copyIdx.ToString() : "") + ext);
                            copyIdx++;
                        }
                    }
                    if (explorerClipboardIsCut)
                    {
                        File.Move(explorerClipboardPath, targetPath);
                        RepathOpenTabsForFileOrDirectoryMove(explorerClipboardPath, targetPath, isDirectory: false);
                        explorerClipboardPath = "";
                        explorerClipboardIsCut = false;
                    }
                    else
                    {
                        File.Copy(explorerClipboardPath, targetPath, overwrite: false);
                    }
                    AfterExplorerFileOperation(targetPath);
                }
                else if (Directory.Exists(explorerClipboardPath))
                {
                    // Cut + paste into the same parent is a no-op; copy + paste there creates "<name>_copy".
                    if (explorerClipboardIsCut && string.Equals(explorerClipboardPath, targetPath, StringComparison.OrdinalIgnoreCase))
                        return;
                    if (targetPath.StartsWith(explorerClipboardPath.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase))
                    {
                        ShowToastNotification("Paste Failed", "Cannot paste a folder into itself.");
                        return;
                    }
                    if (explorerClipboardIsCut)
                    {
                        MoveDirectoryAcrossVolumes(explorerClipboardPath, targetPath);
                        RepathOpenTabsForFileOrDirectoryMove(explorerClipboardPath, targetPath, isDirectory: true);
                        explorerClipboardPath = "";
                        explorerClipboardIsCut = false;
                    }
                    else
                    {
                        string dst = targetPath;
                        int copyIdx = 1;
                        while (Directory.Exists(dst) || File.Exists(dst))
                        {
                            dst = targetPath + "_copy" + (copyIdx > 1 ? copyIdx.ToString() : "");
                            copyIdx++;
                        }
                        CopyDirectoryRecursive(explorerClipboardPath, dst);
                        targetPath = dst;
                    }
                    AfterExplorerFileOperation(targetPath);
                }
            }
            catch (Exception ex)
            {
                ShowToastNotification("Paste Failed", ex.Message);
            }
        }

        private void RepathOpenTabsForFileOrDirectoryMove(string oldPath, string newPath, bool isDirectory)
        {
            if (string.IsNullOrEmpty(oldPath) || string.IsNullOrEmpty(newPath)) return;
            if (!isDirectory)
            {
                foreach (var tab in openTabs.Where(t => string.Equals(t.FilePath, oldPath, StringComparison.OrdinalIgnoreCase)))
                {
                    tab.FilePath = newPath;
                    tab.FileName = Path.GetFileName(newPath);
                    tab.RelativePath = LlmHarnessAndAgentEngine.GetRelativePath(workspaceRoot, newPath);
                }
            }
            else
            {
                string prefix = oldPath.TrimEnd('\\', '/') + Path.DirectorySeparatorChar;
                foreach (var tab in openTabs)
                {
                    if (!string.IsNullOrEmpty(tab.FilePath) && tab.FilePath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    {
                        string subRel = tab.FilePath.Substring(prefix.Length);
                        string updatedFull = Path.Combine(newPath, subRel);
                        tab.FilePath = updatedFull;
                        tab.FileName = Path.GetFileName(updatedFull);
                        tab.RelativePath = LlmHarnessAndAgentEngine.GetRelativePath(workspaceRoot, updatedFull);
                    }
                }
            }
            RefreshTabStrip();
            RefreshOpenEditorsSection();
        }

        private static Panel CreateVerticalSpacer(int height)
        {
            return new Panel { Dock = DockStyle.Top, Height = height, BackColor = Color.Transparent };
        }

        private void BuildSearchSidebarPanel()
        {
            searchPanel = new Panel { Dock = DockStyle.Fill, Visible = false, Padding = new Padding(10, 8, 10, 8) };

            Label sLbl = new Label { Text = "FIND IN WORKSPACE", Dock = DockStyle.Top, Height = 20, Font = new Font("Segoe UI Semibold", 7.8f, FontStyle.Bold) };
            searchInput = new CodeLessDarkInputBox
            {
                Dock = DockStyle.Top,
                Height = 28,
                Font = new Font("Consolas", 9.2f),
                Placeholder = "Search files (text or regex)..."
            };

            Panel searchFlagsRow = new Panel { Dock = DockStyle.Top, Height = 26 };
            searchCaseToggleBtn = CreateHeaderButton("Aa", new Point(0, 2), 34, false);
            searchCaseToggleBtn.Height = 22;
            searchCaseToggleBtn.Font = new Font("Consolas", 8.4f, FontStyle.Bold);
            searchWordToggleBtn = CreateHeaderButton("ab", new Point(38, 2), 34, false);
            searchWordToggleBtn.Height = 22;
            searchWordToggleBtn.Font = new Font("Consolas", 8.4f, FontStyle.Bold);
            searchRegexToggleBtn = CreateHeaderButton(".*", new Point(76, 2), 34, false);
            searchRegexToggleBtn.Height = 22;
            searchRegexToggleBtn.Font = new Font("Consolas", 8.4f, FontStyle.Bold);
            Label searchFlagsHint = new Label
            {
                Location = new Point(116, 5),
                AutoSize = true,
                Font = new Font("Segoe UI", 8.0f),
                Text = "Case / Word / Regex"
            };
            searchFlagsRow.Controls.AddRange(new Control[] { searchCaseToggleBtn, searchWordToggleBtn, searchRegexToggleBtn, searchFlagsHint });

            Label rLbl = new Label { Text = "REPLACE ACROSS FILES", Dock = DockStyle.Top, Height = 20, Font = new Font("Segoe UI Semibold", 7.8f, FontStyle.Bold) };
            searchReplaceInput = new CodeLessDarkInputBox
            {
                Dock = DockStyle.Top,
                Height = 28,
                Font = new Font("Consolas", 9.2f),
                Placeholder = "Replacement text..."
            };

            Panel searchBtnRow = new Panel { Dock = DockStyle.Top, Height = 30 };
            Button runSearchBtn = CreateHeaderButton("Search", new Point(0, 2), 92, true);
            runSearchBtn.Height = 26;
            stopSearchBtn = CreateHeaderButton("Stop", new Point(96, 2), 54, false);
            stopSearchBtn.Height = 26;
            stopSearchBtn.Visible = false;
            Button replaceWorkspaceBtn = CreateHeaderButton("Replace All", new Point(154, 2), 104, false);
            replaceWorkspaceBtn.Height = 26;
            searchBtnRow.Controls.AddRange(new Control[] { runSearchBtn, stopSearchBtn, replaceWorkspaceBtn });

            searchProgressBar = new CodeLessIndeterminateBar
            {
                Dock = DockStyle.Top,
                Height = 2,
                Visible = false
            };

            searchResultsList = new CodeLessListBox
            {
                Dock = DockStyle.Fill,
                BorderStyle = BorderStyle.None,
                Font = new Font("Consolas", 8.5f),
                ItemHeight = 22
            };

            List<Tuple<string, int>> matchTargets = new List<Tuple<string, int>>();

            Action executeSearch = () =>
            {
                string q = (searchInput.Text ?? "").Trim();
                if (workspaceSearchCts != null)
                {
                    try { workspaceSearchCts.Cancel(); } catch { }
                }
                if (string.IsNullOrEmpty(q))
                {
                    IsWorkspaceSearchRunning = false;
                    if (searchProgressBar != null) searchProgressBar.StopProgress();
                    if (stopSearchBtn != null) stopSearchBtn.Visible = false;
                    return;
                }
                searchResultsList.Items.Clear();
                matchTargets.Clear();

                Regex rx = null;
                if (sidebarSearchUseRegex || sidebarSearchWholeWord)
                {
                    try
                    {
                        string pat = sidebarSearchUseRegex ? q : Regex.Escape(q);
                        if (sidebarSearchWholeWord) pat = @"\b(?:" + pat + @")\b";
                        rx = new Regex(pat, sidebarSearchCaseSensitive ? RegexOptions.None : RegexOptions.IgnoreCase);
                    }
                    catch (Exception ex)
                    {
                        searchResultsList.Items.Add("✕ Invalid Regex: " + ex.Message);
                        return;
                    }
                }
                StringComparison cmp = sidebarSearchCaseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;

                var cts = new System.Threading.CancellationTokenSource();
                workspaceSearchCts = cts;
                IsWorkspaceSearchRunning = true;
                if (searchProgressBar != null) searchProgressBar.StartProgress();
                if (stopSearchBtn != null) stopSearchBtn.Visible = true;
                List<string> snapshotFiles = workspaceFiles != null ? workspaceFiles.ToList() : new List<string>();
                string snapRoot = workspaceRoot;

                Task.Run(() =>
                {
                    List<Tuple<string, string, int>> batch = new List<Tuple<string, string, int>>();
                    Stopwatch swBatch = Stopwatch.StartNew();
                    int totalFound = 0;

                    Action flushBatch = () =>
                    {
                        if (batch.Count == 0) return;
                        var toAdd = batch.ToList();
                        batch.Clear();
                        Action uiFlush = () =>
                        {
                            if (cts.IsCancellationRequested) return;
                            foreach (var item in toAdd)
                            {
                                searchResultsList.Items.Add(item.Item1);
                                matchTargets.Add(Tuple.Create(item.Item2, item.Item3));
                            }
                            searchResultsList.Invalidate();
                        };
                        if (this.IsHandleCreated && this.InvokeRequired)
                            this.BeginInvoke(uiFlush);
                        else if (!this.IsDisposed)
                            uiFlush();
                    };

                    foreach (string fp in snapshotFiles)
                    {
                        if (cts.IsCancellationRequested) break;
                        try
                        {
                            FileInfo fi = new FileInfo(fp);
                            if (!fi.Exists || fi.Length > 1024 * 1024) continue;
                            string rel = LlmHarnessAndAgentEngine.GetRelativePath(snapRoot, fp);
                            int lineNo = 0;
                            foreach (string line in File.ReadLines(fp))
                            {
                                if (cts.IsCancellationRequested) break;
                                lineNo++;
                                bool isMatch = rx != null ? rx.IsMatch(line) : (line.IndexOf(q, cmp) >= 0);
                                if (isMatch)
                                {
                                    string preview = line.Trim();
                                    if (preview.Length > 140) preview = preview.Substring(0, 140) + "…";
                                    batch.Add(Tuple.Create(rel + ":" + lineNo + "  " + preview, fp, lineNo));
                                    totalFound++;
                                    if (batch.Count >= 25 || swBatch.ElapsedMilliseconds >= 45)
                                    {
                                        flushBatch();
                                        swBatch.Restart();
                                    }
                                    if (totalFound >= 300) break;
                                }
                            }
                            if (totalFound >= 300) break;
                        }
                        catch { }
                    }

                    flushBatch();

                    Action finishSearch = () =>
                    {
                        if (workspaceSearchCts == cts)
                        {
                            IsWorkspaceSearchRunning = false;
                            if (searchProgressBar != null) searchProgressBar.StopProgress();
                            if (stopSearchBtn != null) stopSearchBtn.Visible = false;
                        }
                        if (cts.IsCancellationRequested)
                        {
                            searchResultsList.Items.Add("ℹ Search cancelled.");
                        }
                        else if (matchTargets.Count == 0 && searchResultsList.Items.Count == 0)
                        {
                            searchResultsList.Items.Add("No matches found for '" + q + "'.");
                        }
                        searchResultsList.Invalidate();
                    };
                    if (this.IsHandleCreated && this.InvokeRequired)
                        this.BeginInvoke(finishSearch);
                    else if (!this.IsDisposed)
                        finishSearch();
                }, cts.Token);
            };
            triggerWorkspaceSearchAction = executeSearch;

            stopSearchBtn.Click += (s, e) => CancelWorkspaceSearch();

            searchCaseToggleBtn.Click += (s, e) =>
            {
                sidebarSearchCaseSensitive = !sidebarSearchCaseSensitive;
                searchCaseToggleBtn.Tag = sidebarSearchCaseSensitive ? "primary" : "subtle";
                ApplyThemeToControlTree(searchFlagsRow, activeTheme);
                executeSearch();
            };
            searchWordToggleBtn.Click += (s, e) =>
            {
                sidebarSearchWholeWord = !sidebarSearchWholeWord;
                searchWordToggleBtn.Tag = sidebarSearchWholeWord ? "primary" : "subtle";
                ApplyThemeToControlTree(searchFlagsRow, activeTheme);
                executeSearch();
            };
            searchRegexToggleBtn.Click += (s, e) =>
            {
                sidebarSearchUseRegex = !sidebarSearchUseRegex;
                searchRegexToggleBtn.Tag = sidebarSearchUseRegex ? "primary" : "subtle";
                ApplyThemeToControlTree(searchFlagsRow, activeTheme);
                executeSearch();
            };

            runSearchBtn.Click += (s, e) => executeSearch();
            searchInput.KeyDown += (s, e) =>
            {
                if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; executeSearch(); }
            };

            replaceWorkspaceBtn.Click += (s, e) =>
            {
                string q = searchInput.Text ?? "";
                if (string.IsNullOrEmpty(q)) return;
                string repText = searchReplaceInput.Text ?? "";
                string pattern = sidebarSearchUseRegex ? q : Regex.Escape(q);
                if (sidebarSearchWholeWord) pattern = @"\b(?:" + pattern + @")\b";
                RegexOptions opts = sidebarSearchCaseSensitive ? RegexOptions.None : RegexOptions.IgnoreCase;
                Regex rx;
                try { rx = new Regex(pattern, opts); }
                catch
                {
                    if (statusLeftLabel != null) statusLeftLabel.Text = "Invalid search pattern";
                    return;
                }

                List<string> filesSnapshot = new List<string>(workspaceFiles);
                if (searchProgressBar != null) searchProgressBar.StartProgress();
                if (statusLeftLabel != null) statusLeftLabel.Text = "Replacing…";

                Task.Run(() =>
                {
                    Dictionary<string, string> updatedFiles = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    foreach (string fp in filesSnapshot)
                    {
                        try
                        {
                            FileInfo fi = new FileInfo(fp);
                            if (!fi.Exists || fi.Length > 1024 * 1024) continue;
                            Encoding fEnc;
                            bool fBom;
                            string fEol;
                            bool fFinalNl;
                            string raw = EditorTabBuffer.ReadFilePreservingMetadata(fp, out fEnc, out fBom, out fEol, out fFinalNl);
                            if (raw.IndexOf('\0') >= 0) continue;
                            if (rx.IsMatch(raw))
                            {
                                string next = rx.Replace(raw, repText);
                                internalSaveTimestamps[fp] = DateTime.UtcNow;
                                EditorTabBuffer.WriteFilePreservingMetadata(fp, next, fEnc, fBom, fEol, fFinalNl);
                                string written = EditorTabBuffer.ReadFilePreservingMetadata(fp, out fEnc, out fBom, out fEol, out fFinalNl);
                                updatedFiles[fp] = written;
                            }
                        }
                        catch { }
                    }

                    Action applyReplaceOnUi = () =>
                    {
                        if (this.IsDisposed) return;
                        foreach (var kvp in updatedFiles)
                        {
                            EditorTabBuffer openBuf = openTabs.FirstOrDefault(t => string.Equals(t.FilePath, kvp.Key, StringComparison.OrdinalIgnoreCase));
                            if (openBuf != null)
                            {
                                openBuf.PushUndoSnapshot();
                                openBuf.Content = kvp.Value;
                                openBuf.SavedContent = kvp.Value;
                                openBuf.SyncLinesFromContent();
                                openBuf.IsDirty = false;
                                openBuf.RefreshGutterDiff();
                                LlmHarnessAndAgentEngine.AnalyzeBufferAstAndDiagnostics(openBuf);
                            }
                        }
                        if (searchProgressBar != null) searchProgressBar.StopProgress();
                        if (activeTab != null) editorControl.LoadBuffer(activeTab);
                        if (splitEditorControl != null && splitEditorControl.CurrentBuffer != null)
                            splitEditorControl.LoadBuffer(splitEditorControl.CurrentBuffer);
                        RefreshTabStrip();
                        AppendOutputLog(string.Format("Workspace Replace: Replaced '{0}' across {1} file(s).", q, updatedFiles.Count));
                        if (statusLeftLabel != null)
                            statusLeftLabel.Text = string.Format("Replaced across {0} file(s)", updatedFiles.Count);
                        executeSearch();
                    };

                    if (this.IsHandleCreated) this.BeginInvoke(applyReplaceOnUi);
                    else applyReplaceOnUi();
                });
            };

            searchResultsList.SelectedIndexChanged += (s, e) =>
            {
                int idx = searchResultsList.SelectedIndex;
                if (idx >= 0 && idx < matchTargets.Count)
                {
                    EditorTabBuffer tab = OpenFileInTab(matchTargets[idx].Item1);
                    int targetLine1 = matchTargets[idx].Item2;
                    editorControl.JumpToLine(targetLine1);
                    string q = (searchInput.Text ?? "").Trim();
                    if (tab != null && !string.IsNullOrEmpty(q) && targetLine1 - 1 >= 0 && targetLine1 - 1 < tab.Lines.Count)
                    {
                        string lineStr = tab.Lines[targetLine1 - 1] ?? "";
                        StringComparison cmp = sidebarSearchCaseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
                        int mCol = lineStr.IndexOf(q, cmp);
                        if (mCol >= 0)
                        {
                            tab.SelectionAnchorLine = targetLine1 - 1;
                            tab.SelectionAnchorCol = mCol;
                            tab.CursorLine = targetLine1 - 1;
                            tab.CursorCol = mCol + q.Length;
                            editorControl.UpdateCursorStatusLabel();
                            editorControl.Invalidate(true);
                        }
                    }
                }
            };

            searchPanel.Controls.Add(searchResultsList);
            searchPanel.Controls.Add(searchProgressBar);
            searchPanel.Controls.Add(CreateVerticalSpacer(6));
            searchPanel.Controls.Add(searchBtnRow);
            searchPanel.Controls.Add(CreateVerticalSpacer(4));
            searchPanel.Controls.Add(searchReplaceInput);
            searchPanel.Controls.Add(rLbl);
            searchPanel.Controls.Add(CreateVerticalSpacer(4));
            searchPanel.Controls.Add(searchFlagsRow);
            searchPanel.Controls.Add(CreateVerticalSpacer(2));
            searchPanel.Controls.Add(searchInput);
            searchPanel.Controls.Add(sLbl);
        }

        private Action triggerWorkspaceSearchAction;

        public void ExecuteWorkspaceSearchAsync(string queryOverride = null)
        {
            if (queryOverride != null && searchInput != null)
                searchInput.Text = queryOverride;
            if (triggerWorkspaceSearchAction != null)
                triggerWorkspaceSearchAction();
        }

        public void CancelWorkspaceSearch()
        {
            if (workspaceSearchCts != null)
            {
                try { workspaceSearchCts.Cancel(); } catch { }
            }
            IsWorkspaceSearchRunning = false;
            if (searchProgressBar != null) searchProgressBar.StopProgress();
            if (stopSearchBtn != null) stopSearchBtn.Visible = false;
        }

        private void BuildGitSidebarPanel()
        {
            gitPanel = new Panel { Dock = DockStyle.Fill, Visible = false, Padding = new Padding(10, 8, 10, 8) };

            gitBranchLabel = new Label
            {
                Dock = DockStyle.Top,
                Height = 24,
                Font = new Font("Segoe UI Semibold", 9.0f),
                Text = "No Git repository"
            };
            gitBranchLabel.Paint += (s, e) =>
            {
                Color bg = gitPanel != null ? gitPanel.BackColor : gitBranchLabel.BackColor;
                e.Graphics.Clear(bg);
                string raw = gitBranchLabel.Text ?? "";
                string clean = raw.Replace("⎇ ", "").Trim();
                CodeLessVectorIcons.DrawIcon(e.Graphics, "git", new Rectangle(0, 4, 14, 14), gitBranchLabel.ForeColor, 1.35f);
                TextRenderer.DrawText(e.Graphics, clean, gitBranchLabel.Font, new Rectangle(18, 0, Math.Max(40, gitBranchLabel.Width - 18), gitBranchLabel.Height), gitBranchLabel.ForeColor, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
            };

            gitCommitInput = new CodeLessDarkInputBox
            {
                Dock = DockStyle.Top,
                Height = 28,
                Font = new Font("Segoe UI", 9.0f),
                Placeholder = "Commit message (Enter to commit)..."
            };

            Action performCommit = () =>
            {
                string msg = (gitCommitInput.Text ?? "").Trim();
                if (string.IsNullOrEmpty(msg))
                {
                    statusLeftLabel.Text = "Please enter a commit message.";
                    return;
                }
                List<GitFileStatus> snapshot = gitStatusItems != null ? gitStatusItems.ToList() : new List<GitFileStatus>();
                bool anyStaged = snapshot.Any(g => g.IsStaged);
                if (!anyStaged)
                {
                    if (snapshot.Count == 0) { statusLeftLabel.Text = "Git: nothing to commit"; return; }
                    // VS Code "smart commit" prompt instead of silently staging everything.
                    if (!SuppressUnsavedPrompts && MessageBox.Show(this,
                            "There are no staged changes to commit.\n\nWould you like to stage all your changes and commit them directly?",
                            "codeLess — Git Commit", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
                        return;
                }
                List<string> repos = snapshot.Where(g => !anyStaged || g.IsStaged).Select(g => GitRepoFor(g)).Where(r => !string.IsNullOrEmpty(r)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                if (repos.Count == 0) { statusLeftLabel.Text = "No git repository found."; return; }
                string msgFile = Path.Combine(Path.GetTempPath(), "codeless-commit-" + Guid.NewGuid().ToString("N") + ".txt");
                File.WriteAllText(msgFile, msg + "\n", new UTF8Encoding(false));
                gitCommitInput.Clear();
                statusLeftLabel.Text = "Committing…";
                bool stageAll = !anyStaged;
                Task.Run(() =>
                {
                    StringBuilder report = new StringBuilder();
                    bool allOk = true;
                    foreach (string repo in repos)
                    {
                        int code;
                        if (stageAll) RunGitInRepo(repo, 60000, out code, "add", "-A");
                        // Long timeout: pre-commit hooks / GPG signing must not be killed mid-commit (index.lock).
                        string res = RunGitInRepo(repo, 300000, out code, "commit", "-F", msgFile);
                        report.AppendLine(Path.GetFileName(repo) + ": " + res);
                        if (code != 0) allOk = false;
                    }
                    try { File.Delete(msgFile); } catch { }
                    Action done = () =>
                    {
                        AppendOutputLog("[Git Commit] " + report.ToString());
                        if (allOk) statusLeftLabel.Text = "Committed: " + msg;
                        else
                        {
                            statusLeftLabel.Text = "Git commit failed (see Output)";
                            MessageBox.Show(this, report.ToString(), "Git Commit Failed", MessageBoxButtons.OK, MessageBoxIcon.Error);
                        }
                        RefreshGitSidebarStatus();
                    };
                    if (this.IsHandleCreated && !this.IsDisposed) this.BeginInvoke(done);
                });
            };
            gitCommitInput.KeyDown += (s, e) =>
            {
                if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; performCommit(); }
            };

            Panel gitBtnRow = new Panel { Dock = DockStyle.Top, Height = 58 };
            Button commitBtn = CreateHeaderButton("Commit", new Point(0, 2), 86, true);
            commitBtn.Height = 25;
            commitBtn.Click += (s, e) => performCommit();

            Button stageSelectedBtn = CreateHeaderButton("+ Stage", new Point(90, 2), 62, false);
            stageSelectedBtn.Height = 25;
            stageSelectedBtn.Click += (s, e) => StageOrUnstageSelectedGitFile(true);

            Button unstageSelectedBtn = CreateHeaderButton("- Unstage", new Point(156, 2), 68, false);
            unstageSelectedBtn.Height = 25;
            unstageSelectedBtn.Click += (s, e) => StageOrUnstageSelectedGitFile(false);

            Button refreshGitBtn = CreateExplorerVectorIconButton("refresh", new Point(228, 3));
            refreshGitBtn.Click += (s, e) => RefreshGitSidebarStatus();

            Button diffHeadBtn = CreateHeaderButton("Diff vs HEAD", new Point(0, 30), 124, false);
            diffHeadBtn.Height = 25;
            diffHeadBtn.Click += (s, e) => OpenSelectedGitFileDiff();

            Button discardGitBtn = CreateHeaderButton("Discard File", new Point(128, 30), 128, false);
            discardGitBtn.Height = 25;
            discardGitBtn.Click += (s, e) => DiscardSelectedGitFileChanges();

            gitBtnRow.Controls.AddRange(new Control[] { commitBtn, stageSelectedBtn, unstageSelectedBtn, refreshGitBtn, diffHeadBtn, discardGitBtn });

            ContextMenuStrip gitCtxMenu = new ContextMenuStrip { Renderer = new DarkStudioMenuRenderer(() => activeTheme) };
            gitCtxMenu.Items.Add("Open Diff vs HEAD", null, (s, e) => OpenSelectedGitFileDiff());
            gitCtxMenu.Items.Add("Open File in Editor", null, (s, e) =>
            {
                int idx = gitStatusList.SelectedIndex;
                if (idx >= 0 && idx < gitStatusItems.Count && File.Exists(gitStatusItems[idx].FilePath))
                    OpenFileInTab(gitStatusItems[idx].FilePath);
            });
            gitCtxMenu.Items.Add(new ToolStripSeparator());
            gitCtxMenu.Items.Add("+ Stage Selected File (git add)", null, (s, e) => StageOrUnstageSelectedGitFile(true));
            gitCtxMenu.Items.Add("- Unstage Selected File (git reset)", null, (s, e) => StageOrUnstageSelectedGitFile(false));
            gitCtxMenu.Items.Add("Discard Working Tree Changes", null, (s, e) => DiscardSelectedGitFileChanges());
            gitCtxMenu.Items.Add(new ToolStripSeparator());
            gitCtxMenu.Items.Add("+ Stage All Files (git add -A)", null, (s, e) =>
            {
                StageAllInAllRepos();
                RefreshGitSidebarStatus();
            });

            gitStatusList = new CodeLessListBox
            {
                Dock = DockStyle.Fill,
                BorderStyle = BorderStyle.None,
                Font = new Font("Consolas", 8.7f),
                ItemHeight = 22,
                ContextMenuStrip = gitCtxMenu
            };
            gitStatusList.DoubleClick += (s, e) => OpenSelectedGitFileDiff();

            gitPanel.Controls.Add(gitStatusList);
            gitPanel.Controls.Add(CreateVerticalSpacer(6));
            gitPanel.Controls.Add(gitBtnRow);
            gitPanel.Controls.Add(CreateVerticalSpacer(6));
            gitPanel.Controls.Add(gitCommitInput);
            gitPanel.Controls.Add(CreateVerticalSpacer(4));
            gitPanel.Controls.Add(gitBranchLabel);
        }

        private void StageOrUnstageSelectedGitFile(bool stage)
        {
            if (gitStatusList == null) return;
            int idx = gitStatusList.SelectedIndex;
            if (idx < 0 || idx >= gitStatusItems.Count)
            {
                if (stage)
                {
                    StageAllInAllRepos();
                    RefreshGitSidebarStatus();
                }
                return;
            }
            GitFileStatus item = gitStatusItems[idx];
            string repo = GitRepoFor(item);
            string gitRel = GitRelFor(item);
            if (string.IsNullOrEmpty(repo)) return;
            int code;
            string res;
            if (stage)
                res = RunGitInRepo(repo, 15000, out code, "add", "-A", "--", gitRel);
            else
            {
                res = RunGitInRepo(repo, 15000, out code, "restore", "--staged", "--", gitRel);
                if (code != 0) res = RunGitInRepo(repo, 15000, out code, "reset", "-q", "--", gitRel);
            }
            if (code != 0) AppendOutputLog("[Git] " + res);
            RefreshGitSidebarStatus();
            statusLeftLabel.Text = (stage ? "Staged " : "Unstaged ") + item.RelativePath;
        }

        public void DiscardSelectedGitFileChanges()
        {
            if (gitStatusList == null) return;
            int idx = gitStatusList.SelectedIndex;
            if (idx < 0 || idx >= gitStatusItems.Count) return;
            GitFileStatus item = gitStatusItems[idx];
            if (!SuppressUnsavedPrompts &&
                MessageBox.Show(this, "Discard uncommitted working tree changes in '" + item.RelativePath + "'?", "Confirm Git Discard", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes)
                return;
            string repo = GitRepoFor(item);
            string gitRel = GitRelFor(item);
            int code;
            if (item.StatusCode == "??" || item.StatusCode == "A")
            {
                // Untracked or newly added file/folder: unstage (for A) and send the working copy to the Recycle Bin.
                if (item.StatusCode == "A" && !string.IsNullOrEmpty(repo))
                    RunGitInRepo(repo, 15000, out code, "rm", "-r", "-f", "-q", "--cached", "--", gitRel);
                try
                {
                    if (File.Exists(item.FilePath))
                        Microsoft.VisualBasic.FileIO.FileSystem.DeleteFile(item.FilePath, Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs, Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin);
                    else if (Directory.Exists(item.FilePath))
                        Microsoft.VisualBasic.FileIO.FileSystem.DeleteDirectory(item.FilePath, Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs, Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin);
                }
                catch (Exception ex) { MessageBox.Show(this, "Discard failed: " + ex.Message, "codeLess IDE"); }
            }
            else if (!string.IsNullOrEmpty(repo))
            {
                string res = RunGitInRepo(repo, 15000, out code, "restore", "--staged", "--worktree", "--source=HEAD", "--", gitRel);
                if (code != 0)
                {
                    RunGitInRepo(repo, 15000, out code, "reset", "-q", "HEAD", "--", gitRel);
                    res = RunGitInRepo(repo, 15000, out code, "checkout", "--", gitRel);
                }
                if (code != 0) MessageBox.Show(this, "Git discard failed:\n" + res, "codeLess IDE");
            }
            if (File.Exists(item.FilePath))
            {
                EditorTabBuffer tab = openTabs.FirstOrDefault(t => string.Equals(t.FilePath, item.FilePath, StringComparison.OrdinalIgnoreCase));
                if (tab != null)
                {
                    Encoding fEnc;
                    bool fBom;
                    string fEol;
                    bool fFinalNl;
                    string disk = EditorTabBuffer.ReadFilePreservingMetadata(item.FilePath, out fEnc, out fBom, out fEol, out fFinalNl);
                    tab.FileEncoding = fEnc;
                    tab.HasBom = fBom;
                    tab.EolSequence = fEol;
                    tab.HasFinalNewline = fFinalNl;
                    tab.Content = disk;
                    tab.SavedContent = disk;
                    tab.GitHeadContent = LlmHarnessAndAgentEngine.GetGitHeadFileContent(workspaceRoot, item.FilePath);
                    tab.SyncLinesFromContent();
                    tab.IsDirty = false;
                    tab.RefreshGutterDiff();
                    if (activeTab == tab) editorControl.LoadBuffer(tab);
                }
            }
            RefreshGitSidebarStatus();
            RefreshTabStrip();
        }

        private void OpenSelectedGitFileDiff()
        {
            if (gitStatusList == null) return;
            int idx = gitStatusList.SelectedIndex;
            if (idx < 0 || idx >= gitStatusItems.Count) return;

            GitFileStatus item = gitStatusItems[idx];
            string fullPath = item.FilePath;
            if (string.IsNullOrEmpty(fullPath) || !File.Exists(fullPath)) return;

            EditorTabBuffer tab = OpenFileInTab(fullPath);
            if (tab == null) return;

            string headContent = LlmHarnessAndAgentEngine.GetGitHeadFileContent(workspaceRoot, fullPath);
            bool hasHead = headContent != null;
            string baselineContent = hasHead ? headContent : "";
            string diffLabel = item.IsStaged ? "Git Staged Change vs HEAD" : (hasHead ? "Git Working Tree vs HEAD" : "Git Untracked / New File");

            string currentContent = tab.Content;
            tab.Content = baselineContent;
            tab.PreDiffContent = null;
            tab.SyncLinesFromContent();
            editorControl.LoadBuffer(tab);
            editorControl.StageProposedContent(currentContent, diffLabel);
            RefreshTabStrip();
            statusLeftLabel.Text = "Diff: " + item.RelativePath;
        }

        private string GitRepoFor(GitFileStatus item)
        {
            if (item != null && !string.IsNullOrEmpty(item.RepoRoot)) return item.RepoRoot;
            return LlmHarnessAndAgentEngine.FindGitRepoRoot(item != null && !string.IsNullOrEmpty(item.FilePath) ? Path.GetDirectoryName(item.FilePath) : workspaceRoot);
        }

        private string GitRelFor(GitFileStatus item)
        {
            if (!string.IsNullOrEmpty(item.RepoRelativePath)) return item.RepoRelativePath;
            string repo = GitRepoFor(item);
            return repo != null ? LlmHarnessAndAgentEngine.GetRelativePath(repo, item.FilePath).Replace('\\', '/') : item.RelativePath.Replace('\\', '/');
        }

        private void StageAllInAllRepos()
        {
            HashSet<string> repos = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (gitStatusItems != null) foreach (GitFileStatus g in gitStatusItems) { string r = GitRepoFor(g); if (!string.IsNullOrEmpty(r)) repos.Add(r); }
            if (repos.Count == 0) { string r = LlmHarnessAndAgentEngine.FindGitRepoRoot(workspaceRoot); if (r != null) repos.Add(r); }
            foreach (string repo in repos) { int code; RunGitInRepo(repo, 60000, out code, "add", "-A"); }
        }

        /// <summary>Windows argv quoting (CommandLineToArgvW rules) so paths/messages with spaces, quotes or trailing backslashes survive.</summary>
        private static string QuoteWindowsArg(string arg)
        {
            if (arg == null) arg = "";
            if (arg.Length > 0 && arg.IndexOfAny(new char[] { ' ', '\t', '"', '\n' }) < 0) return arg;
            StringBuilder sb = new StringBuilder("\"");
            int backslashes = 0;
            foreach (char c in arg)
            {
                if (c == '\\') { backslashes++; continue; }
                if (c == '"') { sb.Append('\\', backslashes * 2 + 1); sb.Append('"'); }
                else { sb.Append('\\', backslashes); sb.Append(c); }
                backslashes = 0;
            }
            sb.Append('\\', backslashes * 2);
            sb.Append('"');
            return sb.ToString();
        }

        private static string RunGitInRepo(string repo, int timeoutMs, out int exitCode, params string[] args)
        {
            string joined = string.Join(" ", args.Select(a => QuoteWindowsArg(a)).ToArray());
            bool timedOut;
            string res = LlmHarnessAndAgentEngine.RunProcessCaptureWithExitCode("git", joined, repo, timeoutMs, out exitCode, out timedOut);
            if (timedOut) exitCode = -1;
            return res ?? "";
        }

        private bool gitRefreshRunning;
        private bool gitRefreshPending;

        private void RefreshGitSidebarStatus()
        {
            if (gitStatusList == null) return;
            // Single-flight: overlapping status runs finished out of order (stale "modified" after a commit).
            if (gitRefreshRunning) { gitRefreshPending = true; return; }
            gitRefreshRunning = true;
            string snapRoot = workspaceRoot;
            string curBranch = (aiEngine != null && !string.IsNullOrEmpty(aiEngine.ActiveGitBranch)) ? aiEngine.ActiveGitBranch : "";
            if (gitBranchLabel != null && string.IsNullOrEmpty(gitBranchLabel.Text))
            {
                gitBranchLabel.Text = (string.IsNullOrEmpty(curBranch) || string.Equals(curBranch, "no-git", StringComparison.OrdinalIgnoreCase))
                    ? "No Git repository"
                    : ("Branch: " + curBranch);
            }

            List<string> openTabPaths = openTabs
                .Where(t => t != null && !t.IsLargeFile && !string.IsNullOrEmpty(t.FilePath))
                .Select(t => t.FilePath)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            Task.Run(() =>
            {
                List<GitFileStatus> items = aiEngine.QueryGitWorkspaceStatus(snapRoot);
                string branch = aiEngine.ActiveGitBranch;
                Dictionary<string, string> headMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                if (!string.IsNullOrEmpty(branch) && !string.Equals(branch, "no-git", StringComparison.OrdinalIgnoreCase))
                {
                    foreach (string fp in openTabPaths)
                    {
                        headMap[fp] = LlmHarnessAndAgentEngine.GetGitHeadFileContent(snapRoot, fp);
                    }
                }
                Action applyGit = () =>
                {
                    gitRefreshRunning = false;
                    if (gitRefreshPending) { gitRefreshPending = false; RefreshGitSidebarStatus(); }
                    if (gitStatusList == null || this.IsDisposed) return; // single-flight => results always arrive in order
                    gitStatusItems = items;
                    if (explorerTree != null)
                    {
                        // Explorer M / U / A / D decorations keyed by full path (previously never populated).
                        explorerTree.GitStatusByFile.Clear();
                        foreach (GitFileStatus gi in items)
                        {
                            string code = gi.StatusCode ?? "";
                            string badge = code.Contains("?") ? "U" : code.Contains("D") ? "D" : code.Contains("A") ? "A" : code.Contains("M") || code.Contains("R") ? "M" : null;
                            if (badge != null && !string.IsNullOrEmpty(gi.FilePath)) explorerTree.GitStatusByFile[gi.FilePath] = badge;
                        }
                        explorerTree.Invalidate();
                    }
                    bool isNoGit = string.IsNullOrEmpty(snapRoot) || string.IsNullOrEmpty(branch) || string.Equals(branch, "no-git", StringComparison.OrdinalIgnoreCase);
                    gitBranchLabel.Text = isNoGit
                        ? "No Git repository"
                        : ("Branch: " + branch + " (" + items.Count + " changed)");
                    gitBranchLabel.Invalidate();
                    gitStatusList.Items.Clear();
                    foreach (EditorTabBuffer openTab in openTabs)
                    {
                        if (!openTab.IsLargeFile && !string.IsNullOrEmpty(openTab.FilePath))
                        {
                            string headVal;
                            if (headMap.TryGetValue(openTab.FilePath, out headVal))
                                openTab.GitHeadContent = headVal;
                            openTab.RefreshGutterDiff();
                        }
                    }
                    if (editorControl != null) editorControl.Invalidate(true);
                    if (isNoGit)
                    {
                        gitStatusList.Items.Add("Not a Git repository (run 'git init' in Terminal)");
                        return;
                    }
                    if (items.Count == 0)
                    {
                        gitStatusList.Items.Add("Working tree clean");
                        return;
                    }
                    foreach (GitFileStatus g in items)
                    {
                        string stageTag = g.IsStaged ? "Staged " : "";
                        string lineDiffSuffix = (g.AddedLines > 0 || g.RemovedLines > 0)
                            ? string.Format(" (+{0} -{1})", g.AddedLines, g.RemovedLines)
                            : "";
                        gitStatusList.Items.Add(string.Format("[{0}{1}] {2}{3}", stageTag, g.StatusCode, g.RelativePath, lineDiffSuffix));
                    }
                };
                if (this.IsHandleCreated && this.InvokeRequired)
                    this.BeginInvoke(applyGit);
                else if (!this.IsDisposed)
                    applyGit();
            });
        }

        private void BuildExtensionsSidebarPanel()
        {
            extensionsPanel = new Panel { Dock = DockStyle.Fill, Visible = false, Padding = new Padding(10, 8, 10, 8) };

            extSearchInput = new CodeLessDarkInputBox
            {
                Dock = DockStyle.Top,
                Height = 28,
                Font = new Font("Segoe UI", 9.0f),
                Placeholder = "Search OpenVSX (dracula, python, rust)..."
            };
            extSearchInput.KeyDown += async (s, e) =>
            {
                if (e.KeyCode == Keys.Enter)
                {
                    e.SuppressKeyPress = true;
                    await SearchOpenVsxAsync(extSearchInput.Text.Trim());
                }
            };

            Panel extBtnRow = new Panel { Dock = DockStyle.Top, Height = 30 };
            Button searchOpenVsxBtn = CreateHeaderButton("Search OpenVSX", new Point(0, 2), 118, false);
            searchOpenVsxBtn.Height = 26;
            searchOpenVsxBtn.Click += async (s, e) => await SearchOpenVsxAsync(extSearchInput.Text.Trim());

            extInstallVsixFileBtn = CreateHeaderButton("From .VSIX...", new Point(122, 2), 134, false);
            extInstallVsixFileBtn.Height = 26;
            extInstallVsixFileBtn.Click += (s, e) => PromptInstallVsixFromDisk();
            extBtnRow.Controls.AddRange(new Control[] { searchOpenVsxBtn, extInstallVsixFileBtn });

            extList = new CodeLessListBox
            {
                Dock = DockStyle.Fill,
                BorderStyle = BorderStyle.None,
                Font = new Font("Segoe UI", 9f),
                ItemHeight = 26
            };

            extDetailLabel = new Label
            {
                Dock = DockStyle.Bottom,
                Height = 68,
                Padding = new Padding(6),
                Font = new Font("Consolas", 8.1f),
                AutoEllipsis = true,
                UseMnemonic = false,
                Text = "Select an extension above to install from OpenVSX or activate its themes/snippets/commands."
            };

            extList.SelectedIndexChanged += (s, e) =>
            {
                int idx = extList.SelectedIndex;
                if (idx >= 0 && idx < vsixEngine.Extensions.Count)
                {
                    var ext = vsixEngine.Extensions[idx];
                    extDetailLabel.Text = ext.DisplayName + " v" + ext.Version + "\n" +
                        (string.IsNullOrEmpty(ext.Description) ? ("Publisher: " + ext.Publisher) : ext.Description);
                }
            };

            extActionBtn = CreateHeaderButton("Install / Activate Selected VSIX", new Point(0, 0), 240, true);
            extActionBtn.Dock = DockStyle.Bottom;
            extActionBtn.Height = 28;
            extActionBtn.Click += async (s, e) => await HandleExtensionActionAsync();

            extRunCmdBtn = CreateHeaderButton("Run VSIX Host Command", new Point(0, 0), 240, false);
            extRunCmdBtn.Dock = DockStyle.Bottom;
            extRunCmdBtn.Height = 27;
            extRunCmdBtn.Click += (s, e) => RunBuiltInComplexityAnalysis();

            extensionsPanel.Controls.Add(extList);
            extensionsPanel.Controls.Add(CreateVerticalSpacer(6));
            extensionsPanel.Controls.Add(extBtnRow);
            extensionsPanel.Controls.Add(CreateVerticalSpacer(6));
            extensionsPanel.Controls.Add(extSearchInput);
            extensionsPanel.Controls.Add(extDetailLabel);
            extensionsPanel.Controls.Add(new Panel { Dock = DockStyle.Bottom, Height = 5 });
            extensionsPanel.Controls.Add(extRunCmdBtn);
            extensionsPanel.Controls.Add(new Panel { Dock = DockStyle.Bottom, Height = 5 });
            extensionsPanel.Controls.Add(extActionBtn);
        }

        private void BuildHarnessStudioSidebarPanel()
        {
            harnessStudioPanel = new Panel { Dock = DockStyle.Fill, Visible = false, Padding = new Padding(10, 8, 10, 8) };

            ghostToggleCheck = new CheckBox
            {
                Dock = DockStyle.Top,
                Height = 28,
                Checked = settings.EnableInlineGhostText,
                Font = new Font("Segoe UI Semibold", 8.8f),
                Text = "Enable Inline AI Ghost Text (Tab)"
            };
            ghostToggleCheck.CheckedChanged += (s, e) =>
            {
                settings.EnableInlineGhostText = ghostToggleCheck.Checked;
                editorControl.EnableGhostCompletions = ghostToggleCheck.Checked;
                splitEditorControl.EnableGhostCompletions = ghostToggleCheck.Checked;
                settings.Save();
            };

            harnessListFrame = new Panel { Dock = DockStyle.Top, Height = 176, Padding = new Padding(1) };
            harnessStudioList = new CodeLessListBox
            {
                Dock = DockStyle.Fill,
                BorderStyle = BorderStyle.None,
                Font = new Font("Consolas", 8.8f),
                ItemHeight = 25
            };
            harnessListFrame.Controls.Add(harnessStudioList);
            foreach (LlmHarnessProfile hp in aiEngine.Harnesses)
                harnessStudioList.Items.Add(hp.Name);

            harnessStudioDetailBox = new CodeLessStreamView
            {
                Dock = DockStyle.Fill,
                ReadOnly = true,
                BorderStyle = BorderStyle.None,
                Font = new Font("Consolas", 8.6f)
            };

            harnessStudioList.SelectedIndexChanged += (s, e) =>
            {
                int idx = harnessStudioList.SelectedIndex;
                if (idx >= 0 && idx < aiEngine.Harnesses.Count)
                {
                    LlmHarnessProfile hp = aiEngine.Harnesses[idx];
                    settings.ActiveHarnessId = hp.Id;
                    settings.Save();
                    if (quickHarnessCombo != null && idx + 1 < quickHarnessCombo.Items.Count)
                        quickHarnessCombo.SelectedIndex = idx + 1;

                    harnessStudioDetailBox.Text =
                        "=== " + hp.Name + " ===\n" +
                        "Target Family: " + hp.TargetFamily + "\n" +
                        "Edit Protocol: " + hp.EditProtocol + "\n" +
                        "Context Budget: " + hp.ContextTokenBudget.ToString("N0") + " tokens\n" +
                        "Temperature: " + hp.RecommendedTemp + "\n\n" +
                        "Description:\n" + hp.Description + "\n\n" +
                        "System Directive:\n" + hp.SystemPromptDirective;
                    UpdateTelemetryBadges();
                }
            };
            if (harnessStudioList.Items.Count > 0)
                harnessStudioList.SelectedIndex = 0;

            harnessStudioPanel.Controls.Add(harnessStudioDetailBox);
            harnessStudioPanel.Controls.Add(CreateVerticalSpacer(6));
            harnessStudioPanel.Controls.Add(harnessListFrame);
            harnessStudioPanel.Controls.Add(CreateVerticalSpacer(4));
            harnessStudioPanel.Controls.Add(ghostToggleCheck);
        }

        private Button CreateBottomDockTabButton(string text, Point loc, int width, bool isActive)
        {
            Button b = new Button
            {
                Text = text,
                Location = loc,
                Size = new Size(width, 28),
                FlatStyle = FlatStyle.Flat,
                Font = new Font("Segoe UI", 8.4f, FontStyle.Regular),
                Cursor = Cursors.Hand,
                UseMnemonic = false,
                Tag = isActive ? "docktab_active" : "docktab_inactive"
            };
            b.FlatAppearance.BorderSize = 0;
            b.Paint += (s, e) =>
            {
                Color bg = b.Parent != null ? b.Parent.BackColor : (activeTheme != null ? activeTheme.BgActivity : b.BackColor);
                e.Graphics.Clear(bg);
                bool active = (b.Tag as string) == "docktab_active";
                Color fg = activeTheme != null
                    ? (active ? activeTheme.FgPrimary : activeTheme.FgSecondary)
                    : b.ForeColor;
                TextRenderer.DrawText(
                    e.Graphics,
                    b.Text ?? "",
                    b.Font,
                    new Rectangle(0, 0, b.Width, b.Height - 2),
                    fg,
                    TextFormatFlags.SingleLine | TextFormatFlags.VerticalCenter | TextFormatFlags.HorizontalCenter | TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding
                );
                if (active && activeTheme != null)
                {
                    int textW = TextRenderer.MeasureText(
                        b.Text ?? "",
                        b.Font,
                        new Size(400, 28),
                        TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding
                    ).Width;
                    int inset = Math.Max(4, (b.Width - textW) / 2);
                    using (SolidBrush br = new SolidBrush(activeTheme.AccentPrimary))
                        e.Graphics.FillRectangle(br, inset, b.Height - 2, Math.Max(8, b.Width - inset * 2), 2);
                }
            };
            return b;
        }

        private void BuildBottomMultiTabDock()
        {
            bottomDockPanel = new Panel { Dock = DockStyle.Bottom, Height = 210 };
            dockHeaderBar = new Panel { Dock = DockStyle.Top, Height = 30, Padding = new Padding(6, 1, 6, 1) };
            dockHeaderBar.Paint += (s, e) =>
            {
                if (activeTheme != null)
                {
                    using (Pen bp = new Pen(activeTheme.BorderSubtle, 1))
                        e.Graphics.DrawLine(bp, 0, dockHeaderBar.Height - 1, dockHeaderBar.Width, dockHeaderBar.Height - 1);
                }
            };

            btmProbTabBtn = CreateBottomDockTabButton("PROBLEMS (0)", new Point(6, 1), 94, false);
            btmOutTabBtn = CreateBottomDockTabButton("OUTPUT", new Point(102, 1), 62, false);
            btmTraceTabBtn = CreateBottomDockTabButton("DEBUG CONSOLE", new Point(166, 1), 104, false);
            btmTermTabBtn = CreateBottomDockTabButton("TERMINAL", new Point(272, 1), 74, true);
            btmExtHostTabBtn = CreateBottomDockTabButton("PORTS", new Point(348, 1), 54, false);
            autoHealTermBtn = CreateExplorerVectorIconButton("agent", new Point(406, 4));

            btmTermTabBtn.Click += (s, e) => SwitchBottomDockTab("terminal");
            btmProbTabBtn.Click += (s, e) => SwitchBottomDockTab("problems");
            btmOutTabBtn.Click += (s, e) => SwitchBottomDockTab("output");
            btmTraceTabBtn.Click += (s, e) => SwitchBottomDockTab("debug");
            btmExtHostTabBtn.Click += (s, e) => SwitchBottomDockTab("exthost");
            autoHealTermBtn.Click += async (s, e) => await AutoHealLastTerminalErrorAsync();

            outputChannelCombo = new CodeLessDropdown
            {
                Width = 148,
                Height = 23,
                Visible = false,
                Font = new Font("Segoe UI", 8.2f)
            };
            outputChannelCombo.Items.AddRange(new object[] { "codeLess Studio", "AI Harness Trace", "Extension Host Log" });
            outputChannelCombo.SelectedIndex = 0;
            outputChannelCombo.SelectedIndexChanged += (s, e) =>
            {
                if (activeBottomTab == "output")
                    UpdateActiveOutputChannelVisibility();
            };

            shellSelectorCombo = new CodeLessDropdown
            {
                Width = 96,
                Height = 23,
                Visible = false,
                Font = new Font("Consolas", 8.4f)
            };
            shellSelectorCombo.Items.AddRange(new object[] { "PowerShell", "CMD.exe", "Node.js", "Python" });
            foreach (TerminalProfileInfo prof in ConPtyTerminalSession.DetectAvailableProfiles())
            {
                if (prof.Id == "gitbash" || prof.Id == "wsl" || prof.Id == "pwsh")
                    shellSelectorCombo.Items.Add(prof.Name);
            }
            shellSelectorCombo.SelectedIndex = 0;
            shellSelectorCombo.SelectedIndexChanged += (s, e) =>
            {
                if (terminalInput == null) return;
                string sh = shellSelectorCombo.Text ?? "PowerShell";
                if (sh == "CMD.exe" || sh.IndexOf("Command Prompt", StringComparison.OrdinalIgnoreCase) >= 0)
                    terminalInput.Placeholder = "Type CMD.exe command (dir, cd, git, cargo) & press Enter...";
                else if (sh == "Node.js")
                    terminalInput.Placeholder = "Type JavaScript expression for Node.js REPL & press Enter...";
                else if (sh.IndexOf("Python", StringComparison.OrdinalIgnoreCase) >= 0)
                    terminalInput.Placeholder = "Type Python statement for Python REPL & press Enter...";
                else
                    terminalInput.Placeholder = "Type PowerShell / CLI command (cd, dotnet, python, npm) & press Enter...";
            };

            terminalSessionCombo = new CodeLessDropdown
            {
                Width = 82,
                Height = 23,
                Font = new Font("Consolas", 8.2f)
            };
            terminalSessions.Clear();
            string startDir = GetEffectiveTerminalWorkingDirectory();
            ConPtyTerminalSession firstPty = new ConPtyTerminalSession("PowerShell", null, startDir, 100, 14, activeTheme != null && activeTheme.IsLight);
            string firstShortSh = (firstPty.CommandLine ?? "").IndexOf("pwsh", StringComparison.OrdinalIgnoreCase) >= 0 ? "pwsh" : "powershell";
            TerminalSessionState firstState = new TerminalSessionState
            {
                Title = "1: " + firstShortSh,
                WorkingDir = startDir,
                Transcript = "[codeLess Native Persistent Shell — Ready]\n",
                ConPtySession = firstPty
            };
            WireConPtySessionLifecycle(firstState);
            terminalSessions.Add(firstState);
            terminalSessionCombo.Items.Add("1: " + firstShortSh);
            terminalSessionCombo.SelectedIndex = 0;
            terminalSessionCombo.SelectedIndexChanged += (s, e) =>
            {
                if (!suppressTerminalComboEvent)
                    SwitchTerminalSession(terminalSessionCombo.SelectedIndex);
            };

            newTermSessionBtn = CreateDynamicVectorIconButton(() => "plus-dropdown", new Point(0, 4), 30, 22);
            newTermSessionBtn.MouseUp += (s, e) =>
            {
                if (e.Button == MouseButtons.Right || (e.Button == MouseButtons.Left && e.X > newTermSessionBtn.Width - 13))
                {
                    ContextMenuStrip profMenu = CreateAutoDisposingContextMenu();
                    foreach (TerminalProfileInfo prof in ConPtyTerminalSession.DetectAvailableProfiles())
                    {
                        TerminalProfileInfo capProf = prof;
                        profMenu.Items.Add(capProf.Name, null, (ms, me) =>
                        {
                            for (int i = 0; i < shellSelectorCombo.Items.Count; i++)
                            {
                                if (shellSelectorCombo.Items[i].ToString().IndexOf(capProf.Name, StringComparison.OrdinalIgnoreCase) >= 0)
                                {
                                    shellSelectorCombo.SelectedIndex = i;
                                    break;
                                }
                            }
                            RequestNewTerminal(capProf.Name, capProf.CommandLine);
                        });
                    }
                    profMenu.Items.Add(new ToolStripSeparator());
                    profMenu.Items.Add("Split Terminal", null, (ms, me) => ToggleSplitTerminalView());
                    profMenu.Show(newTermSessionBtn, new Point(0, newTermSessionBtn.Height));
                }
                else if (e.Button == MouseButtons.Left)
                {
                    RequestNewTerminal(null, null);
                }
            };

            Button splitTermBtn = CreateExplorerVectorIconButton("split", new Point(0, 4));
            splitTermBtn.Click += (s, e) => ToggleSplitTerminalView();

            killTermProcBtn = CreateExplorerVectorIconButton("trash", new Point(0, 4));
            killTermProcBtn.Click += (s, e) => KillActiveTerminalSession();

            Button clearTermBtn = CreateHeaderButton("Clear", new Point(0, 3), 42, false);
            clearTermBtn.Height = 23;
            clearTermBtn.Visible = false;
            clearTermBtn.FlatAppearance.BorderSize = 0;
            clearTermBtn.Click += (s, e) =>
            {
                if (activeBottomTab == "terminal")
                {
                    if (conPtyTerminalControl != null) conPtyTerminalControl.ClearTerminal();
                    terminalOutput.Clear();
                    if (activeTerminalSessionIndex >= 0 && activeTerminalSessionIndex < terminalSessions.Count)
                        terminalSessions[activeTerminalSessionIndex].Transcript = "";
                }
                else if (activeBottomTab == "output") outputLogBox.Clear();
                else if (activeBottomTab == "trace" || activeBottomTab == "debug") harnessTraceBox.Clear();
            };

            Button maxDockBtn = CreateDynamicVectorIconButton(() => isBottomDockMaximized ? "chevron-down" : "chevron-up", new Point(0, 4), 22, 22);
            maxDockBtn.Click += (s, e) =>
            {
                isBottomDockMaximized = !isBottomDockMaximized;
                if (isBottomDockMaximized)
                {
                    savedBottomDockHeight = bottomDockPanel.Height;
                    bottomDockPanel.Height = Math.Max(320, (int)(this.ClientSize.Height * 0.68));
                }
                else
                {
                    bottomDockPanel.Height = Math.Max(160, savedBottomDockHeight);
                }
                maxDockBtn.Invalidate();
            };

            Button closeDockBtn = CreateExplorerVectorIconButton("close", new Point(0, 4));
            closeDockBtn.Click += (s, e) => ToggleBottomDockVisibility();

            ToolTip btmTips = new ToolTip();
            btmTips.SetToolTip(newTermSessionBtn, "New Terminal (Ctrl+Shift+`) — Click arrow or right-click for Profile");
            btmTips.SetToolTip(splitTermBtn, "Split Terminal (Ctrl+Shift+5)");
            btmTips.SetToolTip(killTermProcBtn, "Kill Terminal");
            btmTips.SetToolTip(autoHealTermBtn, "Auto-Heal Last Terminal Error with AI");
            btmTips.SetToolTip(maxDockBtn, "Maximize Panel Size");
            btmTips.SetToolTip(closeDockBtn, "Close Panel (Ctrl+J)");

            dockHeaderBar.Controls.AddRange(new Control[]
            {
                btmProbTabBtn, btmOutTabBtn, btmTraceTabBtn, btmTermTabBtn, btmExtHostTabBtn,
                autoHealTermBtn, outputChannelCombo, terminalSessionCombo, newTermSessionBtn, splitTermBtn, killTermProcBtn, shellSelectorCombo, clearTermBtn, maxDockBtn, closeDockBtn
            });
            Font dockTabFont = new Font("Segoe UI", 8.4f, FontStyle.Regular);
            Action layoutDockHeader = () =>
            {
                if (dockHeaderBar == null) return;
                int w = dockHeaderBar.Width > 80 ? dockHeaderBar.Width : 660;
                int probCount = WorkspaceDiagnostics != null ? WorkspaceDiagnostics.Count : 0;
                Button[] dockTabs = new Button[] { btmProbTabBtn, btmOutTabBtn, btmTraceTabBtn, btmTermTabBtn, btmExtHostTabBtn };
                foreach (Button hb in dockTabs)
                {
                    if (hb != null)
                    {
                        hb.Font = dockTabFont;
                        hb.Padding = Padding.Empty;
                        hb.Visible = (hb != btmExtHostTabBtn && hb != btmTraceTabBtn);
                    }
                }
                shellSelectorCombo.Visible = false;
                clearTermBtn.Visible = false;

                btmProbTabBtn.Text = string.Format("PROBLEMS ({0})", probCount);
                btmOutTabBtn.Text = "OUTPUT";
                btmTraceTabBtn.Text = "DEBUG CONSOLE";
                btmTermTabBtn.Text = "TERMINAL";
                btmExtHostTabBtn.Text = "PORTS";

                int horizPadPerSide = w >= 740 ? 11 : 10;
                TextFormatFlags measureFlags = TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding;
                foreach (Button hb in dockTabs)
                {
                    if (hb == null) continue;
                    int textW = TextRenderer.MeasureText(hb.Text ?? "", dockTabFont, new Size(400, 28), measureFlags).Width;
                    hb.Width = textW + horizPadPerSide * 2;
                }

                int gap = 2;
                int cx = 6;
                btmProbTabBtn.Location = new Point(cx, 1); cx += btmProbTabBtn.Width + gap;
                btmOutTabBtn.Location = new Point(cx, 1); cx += btmOutTabBtn.Width + gap;
                btmTraceTabBtn.Location = new Point(cx, 1);
                btmTraceTabBtn.Width = 0;
                btmTraceTabBtn.Visible = false;
                btmTermTabBtn.Location = new Point(cx, 1); cx += btmTermTabBtn.Width + gap;
                btmExtHostTabBtn.Location = new Point(cx, 1);
                btmExtHostTabBtn.Width = 0;
                btmExtHostTabBtn.Visible = false;

                int minRightX = cx + 2;
                int btnGap = 2;
                int rx = w - 4;

                closeDockBtn.Width = 22;
                maxDockBtn.Width = 22;
                autoHealTermBtn.Width = 22;
                killTermProcBtn.Width = 22;
                splitTermBtn.Width = 22;
                newTermSessionBtn.Width = w >= 660 ? 28 : 22;

                closeDockBtn.Visible = true;
                closeDockBtn.Location = new Point(rx - closeDockBtn.Width, 4);
                rx = closeDockBtn.Left - btnGap;

                maxDockBtn.Visible = true;
                maxDockBtn.Location = new Point(rx - maxDockBtn.Width, 4);
                rx = maxDockBtn.Left - btnGap;

                int neededForCoreTermBtns = killTermProcBtn.Width + splitTermBtn.Width + newTermSessionBtn.Width + btnGap * 3;
                if (rx - autoHealTermBtn.Width - neededForCoreTermBtns >= minRightX)
                {
                    autoHealTermBtn.Visible = true;
                    autoHealTermBtn.Location = new Point(rx - autoHealTermBtn.Width, 4);
                    rx = autoHealTermBtn.Left - btnGap;
                }
                else
                {
                    autoHealTermBtn.Visible = false;
                }

                if (rx - killTermProcBtn.Width >= minRightX)
                {
                    killTermProcBtn.Visible = true;
                    killTermProcBtn.Location = new Point(rx - killTermProcBtn.Width, 4);
                    rx = killTermProcBtn.Left - btnGap;
                }
                else
                {
                    killTermProcBtn.Visible = false;
                }

                if (rx - splitTermBtn.Width >= minRightX)
                {
                    splitTermBtn.Visible = true;
                    splitTermBtn.Location = new Point(rx - splitTermBtn.Width, 4);
                    rx = splitTermBtn.Left - btnGap;
                }
                else
                {
                    splitTermBtn.Visible = false;
                }

                if (rx - newTermSessionBtn.Width >= minRightX)
                {
                    newTermSessionBtn.Visible = true;
                    newTermSessionBtn.Location = new Point(rx - newTermSessionBtn.Width, 4);
                    rx = newTermSessionBtn.Left - btnGap;
                }
                else
                {
                    newTermSessionBtn.Visible = false;
                }

                int availForCombo = rx - minRightX;
                if (activeBottomTab == "output")
                {
                    terminalSessionCombo.Visible = false;
                    if (outputChannelCombo != null)
                    {
                        if (availForCombo >= 90)
                        {
                            outputChannelCombo.Visible = true;
                            outputChannelCombo.Width = Math.Min(152, availForCombo);
                            outputChannelCombo.Location = new Point(rx - outputChannelCombo.Width, 3);
                        }
                        else
                        {
                            outputChannelCombo.Visible = false;
                        }
                    }
                }
                else
                {
                    if (outputChannelCombo != null) outputChannelCombo.Visible = false;
                    if (availForCombo < 68)
                    {
                        terminalSessionCombo.Visible = false;
                    }
                    else
                    {
                        terminalSessionCombo.Visible = true;
                        int desiredComboW = w >= 720 ? 82 : 74;
                        terminalSessionCombo.Width = Math.Min(desiredComboW, availForCombo);
                        terminalSessionCombo.Location = new Point(rx - terminalSessionCombo.Width, 3);
                    }
                }
                foreach (Button hb in dockTabs)
                {
                    if (hb != null) hb.Invalidate();
                }
            };
            layoutBottomDockHeaderAction = layoutDockHeader;
            dockHeaderBar.Resize += (s, e) => layoutDockHeader();
            layoutDockHeader();

            terminalInput = new CodeLessDarkInputBox
            {
                Dock = DockStyle.Bottom,
                Height = 28,
                Visible = false,
                Font = new Font("Consolas", 9.3f),
                Placeholder = "Type PowerShell / CLI command (cd, dotnet, python, npm) & press Enter..."
            };
            terminalInput.KeyDown += async (s, e) =>
            {
                if (e.Control && e.KeyCode == Keys.C)
                {
                    e.SuppressKeyPress = true;
                    KillActiveTerminalProcess();
                    return;
                }
                if (e.KeyCode == Keys.Enter)
                {
                    e.SuppressKeyPress = true;
                    string cmd = terminalInput.Text.Trim();
                    terminalInput.Clear();
                    if (!string.IsNullOrEmpty(cmd))
                    {
                        terminalHistory.Add(cmd);
                        terminalHistoryIdx = terminalHistory.Count;
                        await ExecuteTerminalCommandAsync(cmd);
                    }
                }
                else if (e.KeyCode == Keys.Up && terminalHistory.Count > 0)
                {
                    e.SuppressKeyPress = true;
                    terminalHistoryIdx = Math.Max(0, terminalHistoryIdx - 1);
                    terminalInput.Text = terminalHistory[terminalHistoryIdx];
                    terminalInput.SelectionStart = terminalInput.Text.Length;
                }
                else if (e.KeyCode == Keys.Down && terminalHistory.Count > 0)
                {
                    e.SuppressKeyPress = true;
                    terminalHistoryIdx = Math.Min(terminalHistory.Count - 1, terminalHistoryIdx + 1);
                    terminalInput.Text = terminalHistory[terminalHistoryIdx];
                    terminalInput.SelectionStart = terminalInput.Text.Length;
                }
            };

            terminalOutput = new CodeLessStreamView
            {
                Size = new Size(640, 180),
                Dock = DockStyle.None,
                Visible = false,
                ReadOnly = true,
                BorderStyle = BorderStyle.None,
                Font = new Font("Consolas", 9.0f),
                Text = "[codeLess Native Persistent Shell — Ready]\n"
            };
            terminalOutput.LineDoubleClicked += NavigateFromStreamLine;
            terminalOutput.KeyDown += (s, e) =>
            {
                if (e.Control && e.KeyCode == Keys.C && activeTerminalSessionIndex >= 0 && activeTerminalSessionIndex < terminalSessions.Count && terminalSessions[activeTerminalSessionIndex].ActiveProcess != null)
                {
                    e.SuppressKeyPress = true;
                    KillActiveTerminalProcess();
                }
            };

            // Real ConPTY VT100 Terminal Emulator Container (direct canvas input + split terminal + right VS Code tabs list)
            terminalSplitContainer = new Panel { Dock = DockStyle.Fill };
            conPtyTerminalControl = new CodeLessTerminalEmulatorControl
            {
                Dock = DockStyle.Fill
            };
            conPtyTerminalControl.ApplyTheme(activeTheme);
            conPtyTerminalControl.Session = firstPty;
            splitConPtyTerminalControl = new CodeLessTerminalEmulatorControl
            {
                Dock = DockStyle.Right,
                Width = 420,
                Visible = false
            };
            splitConPtyTerminalControl.ApplyTheme(activeTheme);
            terminalSplitSplitter = new Splitter
            {
                Dock = DockStyle.Right,
                Width = 3,
                Visible = false
            };
            terminalSplitSplitter.Paint += (s, e) =>
            {
                Color divCol = activeTheme != null ? activeTheme.BorderSubtle : Color.FromArgb(45, 52, 68);
                using (Pen p = new Pen(divCol, 1f))
                    e.Graphics.DrawLine(p, 1, 0, 1, terminalSplitSplitter.Height);
            };

            BuildTerminalTabsListPanel();

            Func<string, Task<string>> genShellCmd = async (nlPrompt) =>
            {
                if (string.IsNullOrWhiteSpace(nlPrompt)) return "";
                int vIdx = quickVendorCombo != null ? Math.Max(0, quickVendorCombo.SelectedIndex) : 0;
                ByokVendor v = (vIdx >= 0 && vIdx < aiEngine.Vendors.Count) ? aiEngine.Vendors[vIdx] : null;
                if (!IsVendorConfigured(v))
                    return "";
                try
                {
                    string resp = await aiEngine.CallByokVendorAsync(
                        v,
                        "You are a Windows PowerShell / CLI shell command generator. Output ONLY the executable shell command with no markdown fences and no explanation. Never output destructive git reset or rm commands unless explicitly asked.",
                        nlPrompt,
                        0.1
                    );
                    if (!string.IsNullOrWhiteSpace(resp))
                        return resp.Trim().Trim('`');
                }
                catch { }
                return "";
            };

            foreach (var termCtrl in new[] { conPtyTerminalControl, splitConPtyTerminalControl })
            {
                CodeLessTerminalEmulatorControl capTerm = termCtrl;
                termCtrl.GenerateShellCommandCallback = genShellCmd;
                termCtrl.IsAiProviderConfiguredCallback = () => IsActiveVendorConfigured();
                termCtrl.OpenAiSettingsCallback = () => OpenByokConfigurationDialog();
                termCtrl.SwitchTerminalDeltaRequested += (delta) => SwitchTerminalSessionByDelta(delta);
                // Ctrl+Click file links arrive already resolved (shell cwd, then workspace root) and verified to exist.
                termCtrl.WorkspaceRootProvider = () => workspaceRoot;
                termCtrl.FileLinkClicked += (fp, ln, col) =>
                {
                    if (File.Exists(fp))
                    {
                        OpenFileInTab(fp);
                        if (ln > 0 && col > 0) editorControl.JumpToLineAndColumn(ln, col);
                        else if (ln > 0) editorControl.JumpToLine(ln);
                    }
                };
                termCtrl.RequestNewTerminal += () => RequestNewTerminal(null, null);
                termCtrl.RequestSplitTerminal += () => ToggleSplitTerminalView();
                termCtrl.RequestKillTerminal += () =>
                {
                    if (capTerm == splitConPtyTerminalControl && isTerminalSplit)
                        CloseSplitTerminalPane();
                    else
                        KillActiveTerminalSession();
                };
            }

            terminalSplitContainer.Controls.Add(conPtyTerminalControl);
            terminalSplitContainer.Controls.Add(terminalSplitSplitter);
            terminalSplitContainer.Controls.Add(splitConPtyTerminalControl);
            terminalSplitContainer.Controls.Add(terminalTabsListPanel);

            problemsListBox = new CodeLessListBox
            {
                Dock = DockStyle.Fill,
                Visible = false,
                BorderStyle = BorderStyle.None,
                Font = new Font("Consolas", 9.0f),
                ItemHeight = 22
            };
            Action navigateToSelectedProblem = () =>
            {
                if (problemsListBox.SelectedIndex < 0 || problemsListBox.SelectedIndex >= WorkspaceDiagnostics.Count)
                    return;
                DiagnosticItem diag = WorkspaceDiagnostics[problemsListBox.SelectedIndex];
                if (!string.IsNullOrEmpty(diag.FilePath) && File.Exists(diag.FilePath))
                    OpenFileInTab(diag.FilePath);
                editorControl.JumpToLine(diag.Line);
            };
            problemsListBox.Click += (s, e) => navigateToSelectedProblem();
            problemsListBox.DoubleClick += (s, e) => navigateToSelectedProblem();

            outputLogBox = new CodeLessStreamView
            {
                Dock = DockStyle.Fill,
                Visible = false,
                ReadOnly = true,
                BorderStyle = BorderStyle.None,
                Font = new Font("Consolas", 9.0f),
                Text = "[codeLess Studio] Zero-Copy disk indexer and native build system online.\n"
            };
            outputLogBox.LineDoubleClicked += NavigateFromStreamLine;

            harnessTraceBox = new CodeLessStreamView
            {
                Dock = DockStyle.Fill,
                Visible = false,
                ReadOnly = true,
                BorderStyle = BorderStyle.None,
                Font = new Font("Consolas", 8.8f),
                Text = "[LLM Harness Telemetry] Every Agent, Composer, Arena, and Ctrl+K execution logs its prompt token breakdown, active harness profile, and tool call traces here.\n"
            };

            extHostLogBox = new CodeLessStreamView
            {
                Dock = DockStyle.Fill,
                Visible = false,
                ReadOnly = true,
                BorderStyle = BorderStyle.None,
                Font = new Font("Consolas", 8.8f)
            };

            debugConsoleBox = new CodeLessStreamView
            {
                Dock = DockStyle.Fill,
                Visible = false,
                ReadOnly = true,
                BorderStyle = BorderStyle.None,
                Font = new Font("Consolas", 8.8f),
                Text = ""
            };

            bottomDockPanel.Controls.Add(terminalSplitContainer);
            bottomDockPanel.Controls.Add(terminalOutput);
            bottomDockPanel.Controls.Add(problemsListBox);
            bottomDockPanel.Controls.Add(outputLogBox);
            bottomDockPanel.Controls.Add(harnessTraceBox);
            bottomDockPanel.Controls.Add(extHostLogBox);
            bottomDockPanel.Controls.Add(debugConsoleBox);
            bottomDockPanel.Controls.Add(terminalInput);
            bottomDockPanel.Controls.Add(dockHeaderBar);
            terminalSplitContainer.BringToFront();
        }

        private CodeLessStreamView debugConsoleBox;
        private bool suppressTerminalComboEvent = false;

        private void UpdateActiveOutputChannelVisibility()
        {
            int idx = outputChannelCombo != null ? Math.Max(0, outputChannelCombo.SelectedIndex) : 0;
            bool isOut = (activeBottomTab == "output");
            if (outputLogBox != null) outputLogBox.Visible = isOut && idx == 0;
            if (harnessTraceBox != null) harnessTraceBox.Visible = (isOut && idx == 1) || activeBottomTab == "trace";
            if (extHostLogBox != null) extHostLogBox.Visible = (isOut && idx == 2) || activeBottomTab == "exthost";
        }

        private void BuildTerminalTabsListPanel()
        {
            terminalTabsListPanel = new Panel
            {
                Dock = DockStyle.Right,
                Width = 172,
                Visible = false
            };
            typeof(Control).GetProperty("DoubleBuffered", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
                .SetValue(terminalTabsListPanel, true, null);

            terminalTabsListPanel.Paint += (s, e) =>
            {
                Graphics g = e.Graphics;
                Color bg = activeTheme != null ? activeTheme.BgSidebar : Color.FromArgb(20, 23, 30);
                Color border = activeTheme != null ? activeTheme.BorderSubtle : Color.FromArgb(45, 52, 68);
                Color fgPrimary = activeTheme != null ? activeTheme.FgPrimary : Color.FromArgb(226, 232, 240);
                Color fgMuted = activeTheme != null ? activeTheme.FgMuted : Color.FromArgb(140, 152, 172);
                Color selBg = activeTheme != null ? activeTheme.BgSelection : Color.FromArgb(30, 41, 59);
                Color hovBg = activeTheme != null ? activeTheme.BgHover : Color.FromArgb(28, 34, 46);
                Color accent = activeTheme != null ? activeTheme.AccentPrimary : Color.FromArgb(56, 189, 248);

                g.Clear(bg);
                using (Pen lp = new Pen(border, 1f))
                    g.DrawLine(lp, 0, 0, 0, terminalTabsListPanel.Height);

                int rowH = 26;
                for (int i = 0; i < terminalSessions.Count; i++)
                {
                    TerminalSessionState ts = terminalSessions[i];
                    if (ts == null) continue;
                    bool isAct = (i == activeTerminalSessionIndex);
                    bool isHov = (i == hoveredTerminalTabIndex);
                    Rectangle rowRect = new Rectangle(1, i * rowH, terminalTabsListPanel.Width - 1, rowH);

                    if (isAct)
                    {
                        using (SolidBrush sb = new SolidBrush(selBg))
                            g.FillRectangle(sb, rowRect);
                        Color barCol = (ts.ConPtySession != null && ts.ConPtySession.CustomTabColor != Color.Empty)
                            ? ts.ConPtySession.CustomTabColor
                            : accent;
                        using (SolidBrush ab = new SolidBrush(barCol))
                            g.FillRectangle(ab, 1, rowRect.Y + 2, 3, rowRect.Height - 4);
                    }
                    else if (isHov)
                    {
                        using (SolidBrush hb = new SolidBrush(hovBg))
                            g.FillRectangle(hb, rowRect);
                    }

                    Color iconCol = (ts.ConPtySession != null && ts.ConPtySession.CustomTabColor != Color.Empty)
                        ? ts.ConPtySession.CustomTabColor
                        : (isAct ? accent : fgMuted);
                    string iconKind = (ts.ConPtySession != null && !string.IsNullOrEmpty(ts.ConPtySession.CustomIconKind))
                        ? ts.ConPtySession.CustomIconKind
                        : "terminal";
                    CodeLessVectorIcons.DrawIcon(g, iconKind, new Rectangle(10, rowRect.Y + 5, 15, 15), iconCol, 1.35f);

                    int rightReserve = (isHov || isAct) ? 46 : 8;
                    Rectangle textRect = new Rectangle(30, rowRect.Y, Math.Max(30, terminalTabsListPanel.Width - 30 - rightReserve), rowH);
                    TextRenderer.DrawText(
                        g,
                        ts.Title ?? ("Terminal " + (i + 1)),
                        isAct ? TermTabListBoldFont : TermTabListFont,
                        textRect,
                        isAct ? fgPrimary : fgMuted,
                        TextFormatFlags.SingleLine | TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix
                    );

                    if (isHov || isAct)
                    {
                        Rectangle splitIconRect = new Rectangle(terminalTabsListPanel.Width - 44, rowRect.Y + 5, 16, 16);
                        Rectangle trashIconRect = new Rectangle(terminalTabsListPanel.Width - 22, rowRect.Y + 5, 16, 16);
                        CodeLessVectorIcons.DrawIcon(g, "split", splitIconRect, fgMuted, 1.25f);
                        CodeLessVectorIcons.DrawIcon(g, "trash", trashIconRect, isHov && hoveredTerminalTabTrash ? Color.FromArgb(248, 113, 113) : fgMuted, 1.3f);
                    }
                }

                if (isTerminalSplit && splitConPtyTerminalControl != null && splitConPtyTerminalControl.Session != null)
                {
                    int splitRowY = terminalSessions.Count * rowH;
                    Rectangle splitRect = new Rectangle(1, splitRowY, terminalTabsListPanel.Width - 1, rowH);
                    CodeLessVectorIcons.DrawIcon(g, "terminal", new Rectangle(18, splitRect.Y + 5, 14, 14), fgMuted, 1.25f);
                    TextRenderer.DrawText(
                        g,
                        "└ Split: " + (splitConPtyTerminalControl.Session.ShellName ?? "pwsh"),
                        TermTabListFont,
                        new Rectangle(36, splitRect.Y, Math.Max(30, terminalTabsListPanel.Width - 62), rowH),
                        fgMuted,
                        TextFormatFlags.SingleLine | TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix
                    );
                    CodeLessVectorIcons.DrawIcon(g, "trash", new Rectangle(terminalTabsListPanel.Width - 22, splitRect.Y + 5, 16, 16), fgMuted, 1.3f);
                }
            };

            terminalTabsListPanel.MouseMove += (s, e) =>
            {
                int rowH = 26;
                int idx = e.Y / rowH;
                if (idx < 0 || idx >= terminalSessions.Count) idx = -1;
                bool overTrash = idx >= 0 && e.X >= terminalTabsListPanel.Width - 25;
                if (idx != hoveredTerminalTabIndex || overTrash != hoveredTerminalTabTrash)
                {
                    hoveredTerminalTabIndex = idx;
                    hoveredTerminalTabTrash = overTrash;
                    terminalTabsListPanel.Cursor = idx >= 0 ? Cursors.Hand : Cursors.Default;
                    terminalTabsListPanel.Invalidate();
                }
            };

            terminalTabsListPanel.MouseLeave += (s, e) =>
            {
                hoveredTerminalTabIndex = -1;
                hoveredTerminalTabTrash = false;
                terminalTabsListPanel.Invalidate();
            };

            terminalTabsListPanel.MouseDown += (s, e) =>
            {
                int rowH = 26;
                int idx = e.Y / rowH;
                if (isTerminalSplit && idx == terminalSessions.Count)
                {
                    if (e.Button == MouseButtons.Left && e.X >= terminalTabsListPanel.Width - 25)
                        CloseSplitTerminalPane();
                    else if (splitConPtyTerminalControl != null)
                        splitConPtyTerminalControl.Focus();
                    return;
                }
                if (idx < 0 || idx >= terminalSessions.Count) return;

                if (e.Button == MouseButtons.Right)
                {
                    SwitchTerminalSession(idx);
                    ContextMenuStrip ctx = BuildTerminalTabContextMenu(idx);
                    ctx.Show(terminalTabsListPanel, e.Location);
                }
                else if (e.Button == MouseButtons.Left)
                {
                    if (e.X >= terminalTabsListPanel.Width - 25)
                    {
                        KillTerminalSession(idx);
                    }
                    else if (e.X >= terminalTabsListPanel.Width - 46 && e.X < terminalTabsListPanel.Width - 25)
                    {
                        SwitchTerminalSession(idx);
                        ToggleSplitTerminalView();
                    }
                    else
                    {
                        SwitchTerminalSession(idx);
                        if (conPtyTerminalControl != null) conPtyTerminalControl.Focus();
                    }
                }
            };

            terminalTabsListPanel.MouseDoubleClick += (s, e) =>
            {
                int rowH = 26;
                int idx = e.Y / rowH;
                if (idx >= 0 && idx < terminalSessions.Count && e.X < terminalTabsListPanel.Width - 46)
                {
                    PromptRenameTerminalSession(idx);
                }
            };
        }

        public ContextMenuStrip BuildTerminalTabContextMenu(int index)
        {
            ContextMenuStrip ctx = CreateAutoDisposingContextMenu();
            ctx.Items.Add(CreateMenuItem("Rename...", "", () => PromptRenameTerminalSession(index)));
            ToolStripMenuItem colorMenu = new ToolStripMenuItem("Change Color...");
            Tuple<string, Color>[] colors = new Tuple<string, Color>[]
            {
                Tuple.Create("Default", Color.Empty),
                Tuple.Create("Cyan", Color.FromArgb(56, 189, 248)),
                Tuple.Create("Green", Color.FromArgb(74, 222, 128)),
                Tuple.Create("Amber", Color.FromArgb(250, 204, 21)),
                Tuple.Create("Purple", Color.FromArgb(168, 85, 247)),
                Tuple.Create("Red", Color.FromArgb(248, 113, 113))
            };
            foreach (var c in colors)
            {
                var capColor = c;
                colorMenu.DropDownItems.Add(CreateMenuItem(capColor.Item1, "", () =>
                {
                    if (index >= 0 && index < terminalSessions.Count && terminalSessions[index].ConPtySession != null)
                    {
                        terminalSessions[index].ConPtySession.CustomTabColor = capColor.Item2;
                        RefreshTerminalTabsListUi();
                    }
                }));
            }
            ctx.Items.Add(colorMenu);
            ctx.Items.Add(CreateMenuItem("Split Terminal", "Ctrl+Shift+5", () =>
            {
                SwitchTerminalSession(index);
                ToggleSplitTerminalView();
            }));
            ctx.Items.Add(new ToolStripSeparator());
            ctx.Items.Add(CreateMenuItem("Kill Terminal", "Delete", () => KillTerminalSession(index)));
            return ctx;
        }

        public void PromptRenameTerminalSession(int index, string explicitNewTitle = null)
        {
            if (index < 0 || index >= terminalSessions.Count) return;
            TerminalSessionState ts = terminalSessions[index];
            if (!string.IsNullOrWhiteSpace(explicitNewTitle))
            {
                ts.Title = explicitNewTitle.Trim();
                RefreshTerminalTabsListUi();
                return;
            }
            using (var dlg = new PromptInputDialog("Rename Terminal", "Enter terminal name:", ts.Title, activeTheme))
            {
                if (dlg.ShowDialog(this) == DialogResult.OK && !string.IsNullOrWhiteSpace(dlg.InputValue))
                {
                    ts.Title = dlg.InputValue.Trim();
                    RefreshTerminalTabsListUi();
                }
            }
        }

        public void RefreshTerminalTabsListUi()
        {
            suppressTerminalComboEvent = true;
            try
            {
                if (terminalSessionCombo != null)
                {
                    terminalSessionCombo.Items.Clear();
                    foreach (var ts in terminalSessions)
                        terminalSessionCombo.Items.Add(ts.Title ?? "pwsh");
                    if (activeTerminalSessionIndex >= 0 && activeTerminalSessionIndex < terminalSessionCombo.Items.Count)
                        terminalSessionCombo.SelectedIndex = activeTerminalSessionIndex;
                }
            }
            finally
            {
                suppressTerminalComboEvent = false;
            }
            if (terminalTabsListPanel != null)
            {
                bool showTabsList = terminalSessions.Count >= 2 || isTerminalSplit;
                if (terminalTabsListPanel.Visible != showTabsList)
                {
                    terminalTabsListPanel.Visible = showTabsList;
                    if (terminalSplitContainer != null) terminalSplitContainer.PerformLayout();
                }
                terminalTabsListPanel.Invalidate();
            }
            if (layoutBottomDockHeaderAction != null)
                layoutBottomDockHeaderAction();
        }

        private void WireConPtySessionLifecycle(TerminalSessionState state)
        {
            if (state == null || state.ConPtySession == null) return;
            ConPtyTerminalSession session = state.ConPtySession;
            session.ProcessExited += (exitCode) =>
            {
                Action handleExitOnUi = () =>
                {
                    if (this.IsDisposed) return;
                    RemoveExitedTerminalSession(state);
                };
                if (this.IsHandleCreated && this.InvokeRequired)
                {
                    try { this.BeginInvoke(handleExitOnUi); } catch { }
                }
                else if (!this.IsDisposed)
                {
                    handleExitOnUi();
                }
            };
            // Every new interactive session is wired here right after creation: activate the workspace venv in it.
            MaybeActivatePythonVenv(session);
        }

        /// <summary>VS Code-style Python venv activation (Preferences: "Activate Python environment in new terminals").</summary>
        private void MaybeActivatePythonVenv(ConPtyTerminalSession pty)
        {
            if (pty == null || (settings != null && !settings.ActivatePythonEnvironmentInTerminal)) return;
            try { PythonVenvActivator.ScheduleActivation(pty, workspaceRoot); }
            catch { }
        }

        private void RemoveExitedTerminalSession(TerminalSessionState state)
        {
            if (state == null) return;
            int idx = terminalSessions.IndexOf(state);
            if (idx < 0) return;
            KillTerminalSession(idx);
        }

        public void ToggleSplitTerminalView()
        {
            isTerminalSplit = !isTerminalSplit;
            if (splitConPtyTerminalControl == null || terminalSplitSplitter == null) return;
            if (isTerminalSplit)
            {
                string cwd = GetEffectiveTerminalWorkingDirectory();
                if (splitConPtyTerminalControl.Session == null || splitConPtyTerminalControl.Session.HasExited)
                {
                    string sh = shellSelectorCombo != null ? (shellSelectorCombo.Text ?? "PowerShell") : "PowerShell";
                    ConPtyTerminalSession splitPty = new ConPtyTerminalSession(sh, null, cwd, 60, 14, activeTheme != null && activeTheme.IsLight);
                    splitPty.ProcessExited += (exitCode) =>
                    {
                        Action onExit = () => { if (!this.IsDisposed && isTerminalSplit) CloseSplitTerminalPane(); };
                        if (this.IsHandleCreated && this.InvokeRequired)
                        {
                            try { this.BeginInvoke(onExit); } catch { }
                        }
                        else if (!this.IsDisposed)
                        {
                            onExit();
                        }
                    };
                    splitConPtyTerminalControl.Session = splitPty;
                    MaybeActivatePythonVenv(splitPty);
                }
                else
                {
                    splitConPtyTerminalControl.Session.UpdateWorkingDirectory(cwd);
                }
                int listReserve = (terminalTabsListPanel != null && (terminalSessions.Count >= 2 || isTerminalSplit)) ? terminalTabsListPanel.Width : 0;
                splitConPtyTerminalControl.Width = Math.Max(220, (terminalSplitContainer.Width - listReserve) / 2);
                terminalSplitSplitter.Visible = true;
                splitConPtyTerminalControl.Visible = true;
                RefreshTerminalTabsListUi();
                terminalSplitContainer.PerformLayout();
                splitConPtyTerminalControl.Focus();
            }
            else
            {
                CloseSplitTerminalPane();
            }
        }

        public void CloseSplitTerminalPane()
        {
            isTerminalSplit = false;
            if (splitConPtyTerminalControl != null)
            {
                if (splitConPtyTerminalControl.Session != null)
                {
                    try { splitConPtyTerminalControl.Session.Dispose(); } catch { }
                    splitConPtyTerminalControl.Session = null;
                }
                splitConPtyTerminalControl.Visible = false;
            }
            if (terminalSplitSplitter != null)
                terminalSplitSplitter.Visible = false;
            RefreshTerminalTabsListUi();
            if (conPtyTerminalControl != null) conPtyTerminalControl.Focus();
        }

        public void SwitchBottomDockTab(string tabId)
        {
            activeBottomTab = tabId;
            if (terminalSplitContainer != null)
            {
                terminalSplitContainer.Visible = tabId == "terminal";
                if (tabId == "terminal") terminalSplitContainer.BringToFront();
            }
            terminalOutput.Visible = false;
            terminalInput.Visible = false;
            problemsListBox.Visible = tabId == "problems";
            if (debugConsoleBox != null) debugConsoleBox.Visible = tabId == "debug";
            UpdateActiveOutputChannelVisibility();

            btmTermTabBtn.Tag = tabId == "terminal" ? "docktab_active" : "docktab_inactive";
            btmProbTabBtn.Tag = tabId == "problems" ? "docktab_active" : "docktab_inactive";
            btmOutTabBtn.Tag = tabId == "output" ? "docktab_active" : "docktab_inactive";
            btmTraceTabBtn.Tag = (tabId == "trace" || tabId == "debug") ? "docktab_active" : "docktab_inactive";
            btmExtHostTabBtn.Tag = tabId == "exthost" ? "docktab_active" : "docktab_inactive";
            if (layoutBottomDockHeaderAction != null)
                layoutBottomDockHeaderAction();
            ApplyThemeToControlTree(bottomDockPanel, activeTheme);
            bottomDockPanel.Invalidate(true);
        }

        private void BuildRightAgentDock()
        {
            rightDock = new Panel { Dock = DockStyle.Right, Width = 384, Padding = new Padding(8) };

            // Top Chat Header Bar (Minimal Cursor-style: "Chat", "+", history, more)
            Panel chatTopBar = new Panel { Dock = DockStyle.Top, Height = 28, Padding = new Padding(0, 0, 0, 2) };
            Label chatTitleLbl = new Label
            {
                Text = "Chat",
                Location = new Point(4, 4),
                Size = new Size(90, 20),
                Font = new Font("Segoe UI", 9.2f, FontStyle.Regular),
                UseMnemonic = false
            };
            Button newChatBtn = CreateExplorerVectorIconButton("plus", new Point(268, 2));
            newChatBtn.Click += (s, e) =>
            {
                if (agentFeedControl != null && agentFeedControl.MessageCount > 0)
                {
                    string title = !string.IsNullOrEmpty(lastUserQuery)
                        ? (lastUserQuery.Length > 34 ? lastUserQuery.Substring(0, 34) + "..." : lastUserQuery)
                        : ("Chat " + DateTime.Now.ToString("HH:mm:ss"));
                    chatSessionsHistory.Add(Tuple.Create(title, agentFeedControl.GetCardsSnapshot()));
                }
                if (agentFeedControl != null)
                {
                    agentFeedControl.Clear();
                    agentFeedControl.AddMessage(
                        false,
                        GetActiveModelDisplayName(),
                        "",
                        "New chat session started.\n- Type `@` to attach files, folders, symbols, `@Terminal`, `@Problems`, or `@Git`\n- Press **Ctrl+K** in the editor or terminal for inline AI edits"
                    );
                }
            };

            Button historyChatBtn = CreateExplorerVectorIconButton("history", new Point(298, 2));
            historyChatBtn.Click += (s, e) =>
            {
                ContextMenuStrip histMenu = CreateAutoDisposingContextMenu();
                if (chatSessionsHistory.Count == 0)
                {
                    histMenu.Items.Add("(No previous chats in history)").Enabled = false;
                }
                else
                {
                    for (int i = chatSessionsHistory.Count - 1; i >= 0; i--)
                    {
                        var entry = chatSessionsHistory[i];
                        histMenu.Items.Add(entry.Item1, null, (ms, me) =>
                        {
                            if (agentFeedControl != null)
                                agentFeedControl.LoadCardsSnapshot(entry.Item2);
                        });
                    }
                }
                histMenu.Show(historyChatBtn, new Point(0, historyChatBtn.Height));
            };

            Button aiSettingsBtn = CreateExplorerVectorIconButton("more", new Point(328, 2));
            aiSettingsBtn.Click += (s, e) => OpenSettingsDialog();

            ToolTip chatTopTips = new ToolTip();
            chatTopTips.SetToolTip(newChatBtn, "New Chat (Ctrl+L)");
            chatTopTips.SetToolTip(historyChatBtn, "Chat History");
            chatTopTips.SetToolTip(aiSettingsBtn, "AI & Model Settings");

            chatTopBar.Controls.AddRange(new Control[] { chatTitleLbl, newChatBtn, historyChatBtn, aiSettingsBtn });
            chatTopBar.Resize += (s, e) =>
            {
                int cw = chatTopBar.Width;
                aiSettingsBtn.Location = new Point(cw - aiSettingsBtn.Width, 2);
                historyChatBtn.Location = new Point(aiSettingsBtn.Left - historyChatBtn.Width - 3, 2);
                newChatBtn.Location = new Point(historyChatBtn.Left - newChatBtn.Width - 3, 2);
            };

            // Keep modeHeader buttons constructed for programmatic/reflection access, hidden from top strip
            // (Cursor-style mode dropdown pill lives in the composer input footer)
            Panel modeHeader = new Panel { Dock = DockStyle.Top, Height = 0, Visible = false };
            modeAgentBtn = CreateBottomDockTabButton("Agent", new Point(0, 0), 64, true);
            modeComposerBtn = CreateBottomDockTabButton("Ask", new Point(66, 0), 64, false);
            modeArchitectBtn = CreateBottomDockTabButton("Edit", new Point(132, 0), 64, false);
            modeAutoFixBtn = CreateBottomDockTabButton("Auto-Fix", new Point(198, 0), 68, false);

            modeAgentBtn.Click += (s, e) => SetAgentMode("agent");
            modeComposerBtn.Click += (s, e) => SetAgentMode("composer");
            modeArchitectBtn.Click += (s, e) => SetAgentMode("architect");
            modeAutoFixBtn.Click += async (s, e) =>
            {
                SetAgentMode("autofix");
                promptInputBox.Text = "Inspect active file diagnostics and terminal errors, and stage a surgical fix.";
                await SubmitAgentPromptAsync();
            };

            modeHeader.Controls.AddRange(new Control[] { modeAgentBtn, modeComposerBtn, modeArchitectBtn, modeAutoFixBtn });

            // Multi-file Changeset Review Banner inside Right Dock (directly above composer input)
            multiFileChangesetBar = new Panel
            {
                Dock = DockStyle.Bottom,
                Height = 30,
                Visible = false,
                Padding = new Padding(6, 3, 6, 3)
            };
            changesetSummaryLabel = new Label
            {
                AutoSize = false,
                AutoEllipsis = true,
                TextAlign = ContentAlignment.MiddleLeft,
                Location = new Point(6, 4),
                Size = new Size(194, 20),
                Font = new Font("Segoe UI", 8.5f, FontStyle.Regular),
                Text = "1 file changed"
            };
            Button acceptAllFilesBtn = CreateHeaderButton("Accept All", new Point(204, 3), 86, true);
            acceptAllFilesBtn.Height = 22;
            acceptAllFilesBtn.Click += (s, e) =>
            {
                foreach (var t in openTabs)
                {
                    t.DiffHunks.Clear();
                    t.PreDiffContent = null;
                    t.IsDirty = (t.GetNormalizedContentRespectingFinalNewline() != (t.SavedContent ?? ""));
                    t.RefreshGutterDiff();
                }
                editorControl.AcceptAllHunks();
                splitEditorControl.AcceptAllHunks();
                multiFileChangesetBar.Visible = false;
                RefreshTabStrip();
            };
            Button rejectAllFilesBtn = CreateHeaderButton("Reject All", new Point(294, 3), 82, false);
            rejectAllFilesBtn.Height = 22;
            rejectAllFilesBtn.Click += (s, e) =>
            {
                foreach (var t in openTabs)
                {
                    if (t != editorControl.CurrentBuffer && t != splitEditorControl.CurrentBuffer)
                    {
                        if (t.PreDiffContent != null)
                        {
                            t.Content = t.PreDiffContent;
                            t.SyncLinesFromContent();
                            t.PreDiffContent = null;
                            t.IsDirty = (t.GetNormalizedContentRespectingFinalNewline() != (t.SavedContent ?? ""));
                            t.RefreshGutterDiff();
                            LlmHarnessAndAgentEngine.AnalyzeBufferAstAndDiagnostics(t);
                        }
                        t.DiffHunks.Clear();
                    }
                }
                editorControl.RejectAllHunks();
                splitEditorControl.RejectAllHunks();
                multiFileChangesetBar.Visible = false;
                RefreshTabStrip();
            };
            multiFileChangesetBar.Controls.AddRange(new Control[] { changesetSummaryLabel, acceptAllFilesBtn, rejectAllFilesBtn });
            multiFileChangesetBar.Resize += (s, e) =>
            {
                int cw = multiFileChangesetBar.Width;
                rejectAllFilesBtn.Location = new Point(cw - rejectAllFilesBtn.Width - 6, 3);
                acceptAllFilesBtn.Location = new Point(rejectAllFilesBtn.Left - acceptAllFilesBtn.Width - 4, 3);
                changesetSummaryLabel.Size = new Size(Math.Max(60, acceptAllFilesBtn.Left - 12), 20);
            };

            composerCardPanel = new Panel
            {
                Dock = DockStyle.Bottom,
                Height = 148,
                Padding = new Padding(8, 6, 8, 6)
            };
            composerCardPanel.Paint += (s, e) =>
            {
                if (activeTheme != null)
                {
                    e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                    Rectangle r = new Rectangle(0, 0, composerCardPanel.Width - 1, composerCardPanel.Height - 1);
                    using (System.Drawing.Drawing2D.GraphicsPath gp = new System.Drawing.Drawing2D.GraphicsPath())
                    {
                        int d = 12;
                        gp.AddArc(r.X, r.Y, d, d, 180, 90);
                        gp.AddArc(r.Right - d, r.Y, d, d, 270, 90);
                        gp.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
                        gp.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
                        gp.CloseFigure();
                        using (Pen bp = new Pen(promptInputBox != null && promptInputBox.Focused ? activeTheme.AccentPrimary : activeTheme.BorderSubtle, 1.1f))
                            e.Graphics.DrawPath(bp, gp);
                    }
                }
            };

            contextPillsBar = new FlowLayoutPanel
            {
                Dock = DockStyle.Top,
                Height = 26,
                WrapContents = true,
                AutoScroll = false
            };

            Button addFilePillBtn = CreateMiniPillButton("@ Add context");
            addFilePillBtn.Click += (s, e) =>
            {
                ContextMenuStrip ctxMenu = CreateAutoDisposingContextMenu();
                if (activeTab != null)
                    ctxMenu.Items.Add("Active File (@" + activeTab.FileName + ")", null, (ms, me) => AddContextPill("@" + activeTab.FileName));
                ctxMenu.Items.Add("@Codebase (BM25 Workspace Index)", null, (ms, me) => AddContextPill("@Codebase:BM25"));
                ctxMenu.Items.Add("@Terminal (ConPTY Transcript)", null, (ms, me) => AddContextPill("@Terminal"));
                ctxMenu.Items.Add("@Problems (Workspace Diagnostics)", null, (ms, me) => AddContextPill("@Problems"));
                ctxMenu.Items.Add("@Git (Working Tree Diff)", null, (ms, me) => AddContextPill("@Git"));
                ctxMenu.Items.Add("@Rules (.codelessrules / .cursorrules)", null, (ms, me) => AddContextPill("@.codelessrules"));
                ctxMenu.Show(addFilePillBtn, new Point(0, addFilePillBtn.Height));
            };
            Button addCodebasePillBtn = CreateMiniPillButton("@Codebase");
            addCodebasePillBtn.Click += (s, e) => AddContextPill("@Codebase:BM25");
            Button addTermPillBtn = CreateMiniPillButton("+ @Terminal");
            addTermPillBtn.Visible = false;
            addTermPillBtn.Click += (s, e) => AddContextPill("@Terminal");
            Button addProbPillBtn = CreateMiniPillButton("+ @Problems");
            addProbPillBtn.Visible = false;
            addProbPillBtn.Click += (s, e) => AddContextPill("@Problems");
            Button addGitPillBtn = CreateMiniPillButton("+ @Git");
            addGitPillBtn.Visible = false;
            addGitPillBtn.Click += (s, e) => AddContextPill("@Git");
            Button addRulesPillBtn = CreateMiniPillButton("+ @Rules");
            addRulesPillBtn.Visible = false;
            addRulesPillBtn.Click += (s, e) => AddContextPill("@.codelessrules");

            contextPillsBar.Controls.AddRange(new Control[] { addFilePillBtn, addCodebasePillBtn, addTermPillBtn, addProbPillBtn, addGitPillBtn, addRulesPillBtn });

            Panel composerFooter = new Panel { Dock = DockStyle.Bottom, Height = 30 };

            composerModePillBtn = CreateHeaderButton("Agent", new Point(0, 3), 76, false);
            composerModePillBtn.Height = 24;
            composerModePillBtn.Paint += (s, e) =>
            {
                Color bg = activeTheme != null ? activeTheme.BgElevated : composerModePillBtn.BackColor;
                Color border = activeTheme != null ? activeTheme.BorderSubtle : Color.FromArgb(48, 54, 68);
                Color fg = activeTheme != null ? activeTheme.FgPrimary : composerModePillBtn.ForeColor;
                Color accent = activeTheme != null ? activeTheme.AccentPrimary : Color.FromArgb(56, 189, 248);
                e.Graphics.Clear(bg);
                using (Pen bp = new Pen(border, 1f))
                    e.Graphics.DrawRectangle(bp, 0, 0, composerModePillBtn.Width - 1, composerModePillBtn.Height - 1);
                string modeLabel = currentAgentMode == "composer" ? "Ask"
                    : currentAgentMode == "architect" ? "Edit"
                    : currentAgentMode == "autofix" ? "Fix"
                    : "Agent";
                CodeLessVectorIcons.DrawIcon(e.Graphics, "agent", new Rectangle(6, 5, 13, 13), accent, 1.35f);
                TextRenderer.DrawText(
                    e.Graphics,
                    modeLabel,
                    composerModePillBtn.Font,
                    new Rectangle(21, 0, Math.Max(20, composerModePillBtn.Width - 36), composerModePillBtn.Height),
                    fg,
                    TextFormatFlags.SingleLine | TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding
                );
                CodeLessVectorIcons.DrawIcon(e.Graphics, "chevron-down", new Rectangle(composerModePillBtn.Width - 15, 7, 10, 10), activeTheme != null ? activeTheme.FgMuted : fg, 1.35f);
            };
            composerModePillBtn.Click += (s, e) =>
            {
                ContextMenuStrip modeMenu = CreateAutoDisposingContextMenu();
                modeMenu.Items.Add("Agent (Multi-file & Tools)", null, (ms, me) => SetAgentMode("agent"));
                modeMenu.Items.Add("Ask (Read-only Chat)", null, (ms, me) => SetAgentMode("composer"));
                modeMenu.Items.Add("Edit (Inline Manual Diff)", null, (ms, me) => SetAgentMode("architect"));
                modeMenu.Items.Add("Auto-Fix (Repair Diagnostics)", null, async (ms, me) =>
                {
                    SetAgentMode("autofix");
                    promptInputBox.Text = "Inspect active file diagnostics and terminal errors, and stage a surgical fix.";
                    await SubmitAgentPromptAsync();
                });
                modeMenu.Show(composerModePillBtn, new Point(0, composerModePillBtn.Height));
            };

            quickVendorCombo = new CodeLessDropdown
            {
                Location = new Point(80, 3),
                Width = 198,
                Height = 24,
                Font = new Font("Segoe UI", 8.3f)
            };
            RefreshQuickVendorCombo();
            quickVendorCombo.SelectedIndexChanged += (s, e) =>
            {
                int idx = quickVendorCombo.SelectedIndex;
                if (idx >= 0 && idx < aiEngine.Vendors.Count)
                {
                    settings.ActiveVendorId = aiEngine.Vendors[idx].Id;
                    settings.Save();
                    UpdateTelemetryBadges();
                }
            };

            quickHarnessCombo = new CodeLessDropdown
            {
                Location = new Point(0, 31),
                Width = 200,
                Height = 22,
                Visible = false,
                Font = new Font("Consolas", 8.1f)
            };
            quickHarnessCombo.Items.Add("Harness: Auto-Match Model Family");
            foreach (LlmHarnessProfile hp in aiEngine.Harnesses)
                quickHarnessCombo.Items.Add(hp.Name);
            quickHarnessCombo.SelectedIndex = 0;
            quickHarnessCombo.SelectedIndexChanged += (s, e) =>
            {
                settings.ActiveHarnessId = quickHarnessCombo.SelectedIndex <= 0
                    ? "auto"
                    : aiEngine.Harnesses[quickHarnessCombo.SelectedIndex - 1].Id;
                settings.Save();
                UpdateTelemetryBadges();
            };

            sendPromptBtn = CreateHeaderButton("Send", new Point(284, 3), 68, true);
            sendPromptBtn.Height = 24;
            sendPromptBtn.Paint += (s, e) =>
            {
                Color bg = activeTheme != null ? activeTheme.AccentPrimary : sendPromptBtn.BackColor;
                e.Graphics.Clear(bg);
                TextRenderer.DrawText(
                    e.Graphics,
                    "Send",
                    sendPromptBtn.Font,
                    new Rectangle(8, 0, Math.Max(20, sendPromptBtn.Width - 24), sendPromptBtn.Height),
                    Color.White,
                    TextFormatFlags.SingleLine | TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding
                );
                CodeLessVectorIcons.DrawIcon(e.Graphics, "arrow-up", new Rectangle(sendPromptBtn.Width - 19, 6, 12, 12), Color.White, 1.45f);
            };
            sendPromptBtn.Click += async (s, e) => await SubmitAgentPromptAsync();

            stopPromptBtn = CreateHeaderButton("Stop", new Point(284, 3), 68, false);
            stopPromptBtn.Height = 24;
            stopPromptBtn.Visible = false;
            stopPromptBtn.Paint += (s, e) =>
            {
                Color bg = activeTheme != null ? activeTheme.BgElevated : stopPromptBtn.BackColor;
                Color border = activeTheme != null ? activeTheme.BorderSubtle : Color.FromArgb(48, 54, 68);
                Color fg = activeTheme != null ? activeTheme.FgPrimary : stopPromptBtn.ForeColor;
                e.Graphics.Clear(bg);
                using (Pen bp = new Pen(border, 1f))
                    e.Graphics.DrawRectangle(bp, 0, 0, stopPromptBtn.Width - 1, stopPromptBtn.Height - 1);
                using (SolidBrush sb = new SolidBrush(Color.FromArgb(239, 68, 68)))
                    e.Graphics.FillRectangle(sb, 10, 8, 8, 8);
                TextRenderer.DrawText(
                    e.Graphics,
                    "Stop",
                    stopPromptBtn.Font,
                    new Rectangle(23, 0, Math.Max(20, stopPromptBtn.Width - 26), stopPromptBtn.Height),
                    fg,
                    TextFormatFlags.SingleLine | TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding
                );
            };
            stopPromptBtn.Click += (s, e) =>
            {
                if (aiEngine != null) aiEngine.CancelStreamingRequested = true;
                if (agentFeedControl != null && activeStreamingCard != null)
                {
                    agentFeedControl.EndStreamingMessage(activeStreamingCard, activeStreamingCard.RawText + "\n\n*(Stopped by user)*");
                    activeStreamingCard = null;
                }
                stopPromptBtn.Visible = false;
                sendPromptBtn.Visible = true;
                sendPromptBtn.Enabled = true;
                sendPromptBtn.Text = "Send";
                statusLeftLabel.Text = "Stopped";
            };

            composerFooter.Controls.Add(composerModePillBtn);
            composerFooter.Controls.Add(quickVendorCombo);
            composerFooter.Controls.Add(quickHarnessCombo);
            composerFooter.Controls.Add(stopPromptBtn);
            composerFooter.Controls.Add(sendPromptBtn);
            composerFooter.Resize += (s, e) =>
            {
                int fw = composerFooter.Width;
                int sendW = fw < 260 ? 58 : 64;
                int modeW = 68;
                sendPromptBtn.Location = new Point(fw - sendW, 3);
                sendPromptBtn.Width = sendW;
                stopPromptBtn.Location = sendPromptBtn.Location;
                stopPromptBtn.Width = sendW;
                composerModePillBtn.Location = new Point(0, 3);
                composerModePillBtn.Width = modeW;
                quickVendorCombo.Location = new Point(modeW + 3, 3);
                quickVendorCombo.Width = Math.Max(90, sendPromptBtn.Left - (modeW + 6));
            };

            promptInputBox = new CodeLessDarkInputBox
            {
                Dock = DockStyle.Fill,
                Multiline = true,
                Font = new Font("Segoe UI", 9.4f),
                Placeholder = "Plan, @ to add context, / for commands..."
            };

            atMentionPopupFrame = new Panel
            {
                Dock = DockStyle.Bottom,
                Height = 124,
                Visible = false,
                Padding = new Padding(1)
            };
            atMentionList = new CodeLessListBox
            {
                Dock = DockStyle.Fill,
                BorderStyle = BorderStyle.None,
                Font = new Font("Consolas", 8.6f),
                ItemHeight = 22
            };
            atMentionPopupFrame.Controls.Add(atMentionList);

            Action insertSelectedAtMention = () =>
            {
                if (atMentionList.SelectedIndex < 0 || atMentionList.SelectedIndex >= atMentionCandidates.Count) return;
                string chosen = atMentionCandidates[atMentionList.SelectedIndex];
                string txt = promptInputBox.Text ?? "";
                int atPos = txt.LastIndexOf('@');
                if (atPos >= 0)
                    promptInputBox.Text = txt.Substring(0, atPos) + chosen + " ";
                else
                    promptInputBox.Text = txt + chosen + " ";
                promptInputBox.SelectionStart = promptInputBox.Text.Length;
                AddContextPill(chosen);
                atMentionPopupFrame.Visible = false;
                promptInputBox.Focus();
            };

            atMentionList.Click += (s, e) => insertSelectedAtMention();
            atMentionList.KeyDown += (s, e) =>
            {
                if (e.KeyCode == Keys.Enter || e.KeyCode == Keys.Tab)
                {
                    e.SuppressKeyPress = true;
                    insertSelectedAtMention();
                }
                else if (e.KeyCode == Keys.Escape)
                {
                    atMentionPopupFrame.Visible = false;
                    promptInputBox.Focus();
                }
            };

            promptInputBox.TextChanged += (s, e) =>
            {
                string txt = promptInputBox.Text ?? "";
                int atPos = txt.LastIndexOf('@');
                if (atPos >= 0 && atPos >= txt.Length - 28 && !txt.Substring(atPos).Contains(" "))
                {
                    string filter = txt.Substring(atPos + 1).Trim();
                    atMentionCandidates.Clear();
                    atMentionList.Items.Clear();

                    string[] builtIns = new string[] { "@ActiveFile", "@Codebase:BM25", "@Terminal", "@Problems", "@Git", "@.codelessrules" };
                    foreach (string bi in builtIns)
                    {
                        if (string.IsNullOrEmpty(filter) || bi.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0)
                        {
                            atMentionCandidates.Add(bi);
                            atMentionList.Items.Add(bi + "  (Context Provider)");
                        }
                    }
                    foreach (string fp in workspaceFiles)
                    {
                        string rel = "@" + LlmHarnessAndAgentEngine.GetRelativePath(workspaceRoot, fp);
                        if (string.IsNullOrEmpty(filter) || rel.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0)
                        {
                            atMentionCandidates.Add(rel);
                            atMentionList.Items.Add(rel);
                            if (atMentionCandidates.Count >= 16) break;
                        }
                    }

                    if (atMentionCandidates.Count > 0)
                    {
                        atMentionList.SelectedIndex = 0;
                        atMentionPopupFrame.Visible = true;
                    }
                    else
                    {
                        atMentionPopupFrame.Visible = false;
                    }
                }
                else
                {
                    atMentionPopupFrame.Visible = false;
                }
            };

            promptInputBox.KeyDown += async (s, e) =>
            {
                if (atMentionPopupFrame.Visible && (e.KeyCode == Keys.Down || e.KeyCode == Keys.Up))
                {
                    e.SuppressKeyPress = true;
                    int next = atMentionList.SelectedIndex + (e.KeyCode == Keys.Down ? 1 : -1);
                    if (next >= 0 && next < atMentionList.Items.Count)
                        atMentionList.SelectedIndex = next;
                }
                else if (atMentionPopupFrame.Visible && (e.KeyCode == Keys.Tab || e.KeyCode == Keys.Enter))
                {
                    e.SuppressKeyPress = true;
                    insertSelectedAtMention();
                }
                else if (atMentionPopupFrame.Visible && e.KeyCode == Keys.Escape)
                {
                    e.SuppressKeyPress = true;
                    atMentionPopupFrame.Visible = false;
                }
                else if (e.KeyCode == Keys.Escape)
                {
                    e.SuppressKeyPress = true;
                    editorControl.Focus();
                }
                else if (e.KeyCode == Keys.Enter && !e.Shift)
                {
                    e.SuppressKeyPress = true;
                    await SubmitAgentPromptAsync();
                }
            };

            composerCardPanel.Controls.Add(promptInputBox);
            composerCardPanel.Controls.Add(new Panel { Dock = DockStyle.Top, Height = 4 });
            composerCardPanel.Controls.Add(contextPillsBar);
            composerCardPanel.Controls.Add(new Panel { Dock = DockStyle.Bottom, Height = 4 });
            composerCardPanel.Controls.Add(composerFooter);

            agentFeedControl = new CodeLessAgentFeedControl
            {
                Dock = DockStyle.Fill
            };
            agentFeedControl.ApplyCodeRequested += (codeSnippet) =>
            {
                if (string.IsNullOrEmpty(codeSnippet)) return;
                if (activeTab == null && openTabs.Count > 0)
                    activeTab = openTabs[0];
                if (activeTab == null) return;

                TimeTravelCheckpointManager.CreateDiskCheckpoint(activeTab.FilePath, activeTab.Content, "Pre-Apply Snippet");
                string proposedFullContent = SmartMergeSnippetIntoBuffer(activeTab, codeSnippet);
                editorControl.StageProposedContent(proposedFullContent, "Agent Code Block Apply");
                RefreshTabStrip();
                statusLeftLabel.Text = "Updated " + activeTab.FileName;
            };
            agentFeedControl.InsertCodeAtCursorRequested += (codeSnippet) =>
            {
                if (string.IsNullOrEmpty(codeSnippet) || activeTab == null) return;
                editorControl.TypeTextInCanvas(codeSnippet);
                statusLeftLabel.Text = "Inserted into " + activeTab.FileName;
            };
            agentFeedControl.OpenAiSettingsRequested += () => OpenSettingsDialog();
            agentFeedControl.RestoreCheckpointRequested += (cpId) =>
            {
                var recentCps = TimeTravelCheckpointManager.ListCheckpoints();
                if (recentCps.Count > 0)
                {
                    var cp = recentCps[0];
                    string restored = TimeTravelCheckpointManager.RestoreFromCheckpoint(cp);
                    if (restored != null)
                    {
                        EditorTabBuffer tab = OpenFileInTab(cp.OriginalFilePath);
                        if (tab != null)
                        {
                            tab.PushUndoSnapshot();
                            tab.Content = restored;
                            tab.SyncLinesFromContent();
                            tab.WriteToDisk(tab.FilePath);
                            tab.RefreshGutterDiff();
                            LlmHarnessAndAgentEngine.AnalyzeBufferAstAndDiagnostics(tab);
                            editorControl.LoadBuffer(tab);
                            RefreshOutlineAndProblems(tab);
                            RefreshTabStrip();
                            statusLeftLabel.Text = "Restored " + tab.FileName;
                        }
                    }
                }
                else
                {
                    OpenTimeTravelCheckpointsDialog();
                }
            };
            agentFeedControl.ApproveTerminalCommandRequested += async (cmd) =>
            {
                if (string.IsNullOrWhiteSpace(cmd)) return;
                if (!bottomDockPanel.Visible) ToggleBottomDockVisibility();
                SwitchBottomDockTab("terminal");
                await ExecuteTerminalCommandAsync(cmd);
            };
            agentFeedControl.AddMessage(
                false,
                GetActiveModelDisplayName(),
                "",
                "Ask a question or describe an edit.\n- Press **Ctrl+K** for inline edits in the editor or terminal\n- Type `@` to attach files, symbols, `@Problems`, `@Git`, or `@Terminal`\n- Switch between **Agent**, **Ask**, and **Edit** in the footer"
            );

            rightDock.Controls.Add(agentFeedControl);
            rightDock.Controls.Add(atMentionPopupFrame);
            rightDock.Controls.Add(multiFileChangesetBar);
            rightDock.Controls.Add(new Panel { Dock = DockStyle.Bottom, Height = 6 });
            rightDock.Controls.Add(composerCardPanel);
            rightDock.Controls.Add(modeHeader);
            rightDock.Controls.Add(chatTopBar);
        }

        public void RefreshChangesetReviewBar()
        {
            if (multiFileChangesetBar == null || changesetSummaryLabel == null) return;
            var changedTabs = openTabs.Where(t => t.DiffHunks != null && t.DiffHunks.Any(h => h.IsPending)).ToList();
            if (changedTabs.Count == 0)
            {
                multiFileChangesetBar.Visible = false;
                return;
            }
            List<string> fileSummaries = new List<string>();
            foreach (var t in changedTabs)
            {
                int added = 0;
                int removed = 0;
                foreach (var h in t.DiffHunks.Where(x => x.IsPending))
                {
                    if (h.AddedLines != null) added += h.AddedLines.Count;
                    if (h.RemovedLines != null) removed += h.RemovedLines.Count;
                }
                fileSummaries.Add(string.Format("{0} (+{1} −{2})", t.FileName, added, removed));
            }
            changesetSummaryLabel.Text = changedTabs.Count == 1
                ? "1 file: " + fileSummaries[0]
                : string.Format("{0} files: {1}", changedTabs.Count, string.Join(", ", fileSummaries));
            multiFileChangesetBar.Visible = true;
        }

        private readonly List<Tuple<string, int>> outlineFallbackTargets = new List<Tuple<string, int>>();
        private readonly Dictionary<string, List<DiagnosticItem>> workspaceFileDiagnosticsMap = new Dictionary<string, List<DiagnosticItem>>(StringComparer.OrdinalIgnoreCase);

        public void ScanWorkspaceDiagnostics()
        {
            workspaceFileDiagnosticsMap.Clear();
            foreach (string fp in workspaceFiles.Take(80))
            {
                try
                {
                    if (!File.Exists(fp)) continue;
                    EditorTabBuffer openBuf = openTabs.FirstOrDefault(t => string.Equals(t.FilePath, fp, StringComparison.OrdinalIgnoreCase));
                    if (openBuf != null)
                    {
                        LlmHarnessAndAgentEngine.AnalyzeBufferAstAndDiagnostics(openBuf);
                        if (openBuf.Diagnostics != null && openBuf.Diagnostics.Count > 0)
                            workspaceFileDiagnosticsMap[fp] = new List<DiagnosticItem>(openBuf.Diagnostics);
                        continue;
                    }
                    FileInfo fi = new FileInfo(fp);
                    if (fi.Length > 256 * 1024) continue;
                    string raw = File.ReadAllText(fp);
                    EditorTabBuffer temp = new EditorTabBuffer
                    {
                        FilePath = fp,
                        RelativePath = LlmHarnessAndAgentEngine.GetRelativePath(workspaceRoot, fp),
                        FileName = Path.GetFileName(fp),
                        Language = Path.GetExtension(fp).TrimStart('.'),
                        Content = raw
                    };
                    temp.SyncLinesFromContent();
                    LlmHarnessAndAgentEngine.AnalyzeBufferAstAndDiagnostics(temp);
                    if (temp.Diagnostics != null && temp.Diagnostics.Count > 0)
                        workspaceFileDiagnosticsMap[fp] = new List<DiagnosticItem>(temp.Diagnostics);
                }
                catch { }
            }
        }

        private void RefreshOutlineAndProblems(EditorTabBuffer buf)
        {
            EditorTabBuffer effectiveOutlineBuf = (activeTab == null || (editorControl != null && editorControl.IsWelcomeVisible)) ? null : buf;
            if (outlineListBox != null)
            {
                outlineListBox.Items.Clear();
                outlineFallbackTargets.Clear();
                if (effectiveOutlineBuf != null && (effectiveOutlineBuf.Symbols == null || effectiveOutlineBuf.Symbols.Count == 0) && !effectiveOutlineBuf.IsLargeFile && !string.IsNullOrEmpty(effectiveOutlineBuf.Content))
                {
                    LlmHarnessAndAgentEngine.AnalyzeBufferAstAndDiagnostics(effectiveOutlineBuf);
                }
                if (effectiveOutlineBuf != null && effectiveOutlineBuf.Symbols != null && effectiveOutlineBuf.Symbols.Count > 0)
                {
                    foreach (AstSymbolItem s in effectiveOutlineBuf.Symbols)
                        outlineListBox.Items.Add(string.Format("{0}  {1}", s.Kind == "class" ? "class" : "fn", s.Name));
                }
                else
                {
                    outlineListBox.Items.Add("No symbols in active editor");
                }
                outlineListBox.Invalidate();
            }

            if (buf != null && !string.IsNullOrEmpty(buf.FilePath))
            {
                if (buf.Diagnostics != null && buf.Diagnostics.Count > 0)
                    workspaceFileDiagnosticsMap[buf.FilePath] = new List<DiagnosticItem>(buf.Diagnostics);
                else
                    workspaceFileDiagnosticsMap.Remove(buf.FilePath);
            }
            foreach (EditorTabBuffer openBuf in openTabs)
            {
                if (!string.IsNullOrEmpty(openBuf.FilePath))
                {
                    if (openBuf.Diagnostics != null && openBuf.Diagnostics.Count > 0)
                        workspaceFileDiagnosticsMap[openBuf.FilePath] = new List<DiagnosticItem>(openBuf.Diagnostics);
                    else
                        workspaceFileDiagnosticsMap.Remove(openBuf.FilePath);
                }
            }

            WorkspaceDiagnostics.Clear();
            // Put active buffer diagnostics first, then remaining workspace files
            if (buf != null && buf.Diagnostics != null)
            {
                foreach (var d in buf.Diagnostics)
                    WorkspaceDiagnostics.Add(d);
            }
            foreach (var kvp in workspaceFileDiagnosticsMap)
            {
                if (buf != null && string.Equals(kvp.Key, buf.FilePath, StringComparison.OrdinalIgnoreCase))
                    continue;
                foreach (var d in kvp.Value)
                    WorkspaceDiagnostics.Add(d);
            }

            if (problemsListBox != null)
            {
                problemsListBox.Items.Clear();
                int count = WorkspaceDiagnostics.Count;
                int errCount = 0;
                int warnCount = 0;
                if (count == 0)
                {
                    problemsListBox.Items.Add("No problems have been detected in the workspace.");
                }
                foreach (DiagnosticItem d in WorkspaceDiagnostics)
                {
                    string sev = (d.Severity ?? "warning").ToUpperInvariant();
                    if (sev.StartsWith("ERR")) errCount++;
                    else warnCount++;
                    string rel = !string.IsNullOrEmpty(d.RelativePath) ? d.RelativePath : Path.GetFileName(d.FilePath ?? "");
                    problemsListBox.Items.Add(string.Format("[{0}] {1}:Ln {2}, Col {3} — {4} ({5})",
                        sev, rel, d.Line, d.Column, d.Message, d.Source));
                }
                lastErrorCount = errCount;
                lastWarningCount = warnCount;
                if (btmProbTabBtn != null)
                {
                    string nextProbText = string.Format("PROBLEMS ({0})", count);
                    if (btmProbTabBtn.Text != nextProbText)
                    {
                        btmProbTabBtn.Text = nextProbText;
                        if (layoutBottomDockHeaderAction != null)
                            layoutBottomDockHeaderAction();
                    }
                }
                if (statusProblemsLabel != null)
                {
                    statusProblemsLabel.Text = string.Format("⊗ {0}  ⚠ {1}", errCount, warnCount);
                }
                if (statusProblemsPanel != null)
                {
                    statusProblemsPanel.Invalidate();
                    LayoutStatusBarItems();
                }
            }
        }

        private void AppendOutputLog(string msg)
        {
            if (outputLogBox == null) return;
            outputLogBox.AppendText("[" + DateTime.Now.ToString("HH:mm:ss") + "] " + msg + "\n");
        }

        public bool IsVendorConfigured(ByokVendor v)
        {
            if (v == null) return false;
            if (v.Id == "ollama") return aiEngine != null && aiEngine.LocalOllamaDetected;
            return !string.IsNullOrWhiteSpace(v.ApiKey) && v.ApiKey.Trim().Length >= 5;
        }

        public bool IsActiveVendorConfigured()
        {
            if (aiEngine == null || aiEngine.Vendors == null || aiEngine.Vendors.Count == 0) return false;
            int idx = quickVendorCombo != null ? Math.Max(0, quickVendorCombo.SelectedIndex) : 0;
            if (idx >= 0 && idx < aiEngine.Vendors.Count)
                return IsVendorConfigured(aiEngine.Vendors[idx]);
            return false;
        }

        public void RefreshQuickVendorCombo()
        {
            if (quickVendorCombo == null) return;
            quickVendorCombo.Items.Clear();
            int selIdx = 0;
            for (int i = 0; i < aiEngine.Vendors.Count; i++)
            {
                ByokVendor v = aiEngine.Vendors[i];
                string modelName = !string.IsNullOrWhiteSpace(v.SelectedModel) ? v.SelectedModel : v.Name;
                quickVendorCombo.Items.Add(modelName);
                if (v.Id == settings.ActiveVendorId) selIdx = i;
            }
            if (quickVendorCombo.Items.Count > 0)
                quickVendorCombo.SelectedIndex = selIdx;
            UpdateTelemetryBadges();
        }

        private void RefreshThemeComboItems()
        {
            if (themeCombo == null) return;
            themeCombo.Items.Clear();
            int selIdx = 0;
            for (int i = 0; i < themes.Count; i++)
            {
                themeCombo.Items.Add(themes[i].Name);
                if (themes[i].Id == activeTheme.Id) selIdx = i;
            }
            if (themeCombo.Items.Count > 0)
                themeCombo.SelectedIndex = selIdx;
        }

        private Button CreateHeaderButton(string text, Point loc, int width, bool primary)
        {
            return new Button
            {
                Text = text,
                Location = loc,
                Size = new Size(width, 26),
                FlatStyle = FlatStyle.Flat,
                Font = new Font("Segoe UI", 8.5f, FontStyle.Regular),
                Cursor = Cursors.Hand,
                UseMnemonic = false,
                Tag = primary ? "primary" : "subtle"
            };
        }

        private Button CreateActivityButton(string iconKind, string tooltip, int top, string viewId)
        {
            Button b = new Button
            {
                Text = "",
                Location = new Point(2, top),
                Size = new Size(44, 40),
                FlatStyle = FlatStyle.Flat,
                Cursor = Cursors.Hand,
                UseMnemonic = false,
                Tag = "activity_icon"
            };
            b.FlatAppearance.BorderSize = 0;
            b.Paint += (s, e) =>
            {
                bool isActive = (leftSidebar == null || leftSidebar.Visible) && activeSidebarView == viewId;
                Color bg = activeTheme != null ? activeTheme.BgActivity : Color.FromArgb(14, 16, 22);
                using (SolidBrush bgBr = new SolidBrush(bg))
                    e.Graphics.FillRectangle(bgBr, b.ClientRectangle);

                Color accent = activeTheme != null ? activeTheme.AccentPrimary : Color.FromArgb(56, 189, 248);
                Color fg = isActive ? accent : (activeTheme != null ? activeTheme.FgMuted : Color.FromArgb(130, 142, 164));

                if (isActive)
                {
                    using (SolidBrush barBrush = new SolidBrush(accent))
                        e.Graphics.FillRectangle(barBrush, 0, 7, 2, b.Height - 14);
                }

                CodeLessVectorIcons.DrawIcon(e.Graphics, iconKind, new Rectangle(11, 9, 22, 22), fg, isActive ? 1.8f : 1.55f);
            };
            ToolTip tt = new ToolTip();
            tt.SetToolTip(b, tooltip);
            return b;
        }

        private Button CreateMiniPillButton(string label)
        {
            return new Button
            {
                Text = label,
                AutoSize = true,
                Height = 22,
                FlatStyle = FlatStyle.Flat,
                Font = new Font("Consolas", 8f),
                Cursor = Cursors.Hand,
                Tag = "subtle",
                Margin = new Padding(2, 1, 2, 1)
            };
        }

        public void ClearAttachedContextPills()
        {
            attachedContextPills.Clear();
            if (contextPillsBar == null) return;
            foreach (Control c in contextPillsBar.Controls.Cast<Control>().ToList())
            {
                if ((c.Tag as string) == "context_pill")
                    contextPillsBar.Controls.Remove(c);
            }
            contextPillsBar.Height = 26;
            contextPillsBar.AutoScroll = false;
        }

        private static bool IsBuiltInContextPill(string pill)
        {
            if (string.IsNullOrEmpty(pill)) return false;
            return string.Equals(pill, "@ActiveFile", StringComparison.OrdinalIgnoreCase)
                || string.Equals(pill, "@Codebase:BM25", StringComparison.OrdinalIgnoreCase)
                || string.Equals(pill, "@Terminal", StringComparison.OrdinalIgnoreCase)
                || string.Equals(pill, "@Problems", StringComparison.OrdinalIgnoreCase)
                || string.Equals(pill, "@Git", StringComparison.OrdinalIgnoreCase)
                || string.Equals(pill, "@.codelessrules", StringComparison.OrdinalIgnoreCase);
        }

        private static bool DoesPillMatchFile(string pill, string filePath, string fileName, string relativePath)
        {
            if (string.IsNullOrEmpty(pill) || !pill.StartsWith("@")) return false;
            string body = pill.Substring(1);
            int colonIdx = body.LastIndexOf(":L", StringComparison.OrdinalIgnoreCase);
            if (colonIdx > 0) body = body.Substring(0, colonIdx);
            string normBody = body.Replace('\\', '/').TrimStart('.', '/');

            if (!string.IsNullOrEmpty(fileName) && string.Equals(normBody, fileName, StringComparison.OrdinalIgnoreCase))
                return true;
            if (!string.IsNullOrEmpty(relativePath))
            {
                string normRel = relativePath.Replace('\\', '/').TrimStart('.', '/');
                if (string.Equals(normBody, normRel, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            if (!string.IsNullOrEmpty(filePath))
            {
                string normFull = filePath.Replace('\\', '/');
                if (string.Equals(normBody, normFull, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            return false;
        }

        public void RemoveContextPillsForFile(string filePath, string fileName, string relativePath)
        {
            foreach (string pill in attachedContextPills.ToList())
            {
                if (DoesPillMatchFile(pill, filePath, fileName, relativePath))
                    RemoveContextPill(pill);
            }
        }

        private void PruneFileContextPillsWhenNoTabsOpen()
        {
            if (openTabs.Count > 0) return;
            foreach (string pill in attachedContextPills.ToList())
            {
                if (!IsBuiltInContextPill(pill))
                    RemoveContextPill(pill);
            }
        }

        public void RemoveContextPill(string label)
        {
            if (string.IsNullOrEmpty(label)) return;
            attachedContextPills.RemoveAll(p => string.Equals(p, label, StringComparison.OrdinalIgnoreCase));
            if (contextPillsBar == null) return;
            foreach (Control c in contextPillsBar.Controls.Cast<Control>().ToList())
            {
                if ((c.Tag as string) == "context_pill" &&
                    c.Text != null &&
                    c.Text.StartsWith(label + " ", StringComparison.OrdinalIgnoreCase))
                {
                    contextPillsBar.Controls.Remove(c);
                }
            }
            contextPillsBar.Height = contextPillsBar.Controls.Count > 6 ? 50 : 26;
            contextPillsBar.AutoScroll = contextPillsBar.Controls.Count > 9;
        }

        private void AddContextPill(string label)
        {
            if (string.IsNullOrWhiteSpace(label)) return;
            if (attachedContextPills.Any(p => string.Equals(p, label, StringComparison.OrdinalIgnoreCase))) return;
            attachedContextPills.Add(label);
            Button pill = CreateMiniPillButton(label + " ✕");
            pill.Tag = "context_pill";
            pill.BackColor = activeTheme.AccentPrimary;
            pill.ForeColor = Color.White;
            pill.FlatAppearance.BorderSize = 0;
            pill.Click += (s, e) =>
            {
                RemoveContextPill(label);
            };
            contextPillsBar.Controls.Add(pill);
            contextPillsBar.Height = contextPillsBar.Controls.Count > 6 ? 50 : 26;
            contextPillsBar.AutoScroll = contextPillsBar.Controls.Count > 9;
        }

        private void SetAgentMode(string mode)
        {
            currentAgentMode = mode;
            modeAgentBtn.Tag = mode == "agent" ? "docktab_active" : "docktab_inactive";
            modeComposerBtn.Tag = mode == "composer" ? "docktab_active" : "docktab_inactive";
            modeArchitectBtn.Tag = mode == "architect" ? "docktab_active" : "docktab_inactive";
            modeAutoFixBtn.Tag = mode == "autofix" ? "docktab_active" : "docktab_inactive";
            if (composerModePillBtn != null)
            {
                composerModePillBtn.Text = mode == "composer" ? "Ask"
                    : mode == "architect" ? "Edit"
                    : mode == "autofix" ? "Fix"
                    : "Agent";
            }
            ApplyThemeToControlTree(rightDock, activeTheme);
            rightDock.Invalidate(true);
        }

        private void SwitchSidebarView(string view)
        {
            activeSidebarView = view;
            explorerContainer.Visible = view == "explorer";
            searchPanel.Visible = view == "search";
            gitPanel.Visible = view == "git";
            extensionsPanel.Visible = view == "extensions";
            harnessStudioPanel.Visible = view == "harness";

            sidebarHeaderLabel.Text = view == "explorer"
                ? "EXPLORER"
                : view == "search"
                ? "SEARCH"
                : view == "git"
                ? "SOURCE CONTROL"
                : view == "extensions"
                ? "EXTENSIONS"
                : "AI HARNESSES";

            if (activityBar != null) activityBar.Invalidate(true);
        }

        public HashSet<string> GetExcludedFolderSet(string rootDir = null)
        {
            HashSet<string> set = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                ".git", "node_modules", "bin", "obj", "dist", "build", "out",
                ".venv", "__pycache__", ".next", "target", "packages", ".vs", "Output"
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

        // VS Code "files.exclude" defaults: only VCS metadata / OS litter is hidden from the Explorer tree.
        // Build output, node_modules, nested repositories and gitignored folders stay visible (dimmed) and are
        // merely excluded from indexing and search.
        private static readonly string[] ExplorerDefaultHiddenNames = new string[] { ".git", ".svn", ".hg", "CVS", ".DS_Store", "Thumbs.db" };
        private const string DefaultExcludedFoldersListForExplorer = ".git, node_modules, bin, obj, dist, build, out, .venv, __pycache__, .next, target, packages";
        private HashSet<string> explorerDimmedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        public HashSet<string> GetExplorerHiddenSet()
        {
            HashSet<string> set = new HashSet<string>(ExplorerDefaultHiddenNames, StringComparer.OrdinalIgnoreCase);
            if (settings != null)
            {
                HashSet<string> defaults = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (string d in DefaultExcludedFoldersListForExplorer.Split(',')) defaults.Add(d.Trim());
                string[] custom = settings.GetExcludedFoldersList();
                if (custom != null)
                {
                    foreach (string c in custom)
                    {
                        string t = (c ?? "").Trim().Trim('/', '\\');
                        if (t.Length > 0 && !defaults.Contains(t)) set.Add(t);
                    }
                }
            }
            return set;
        }

        private void RefreshExplorerDimmedNames()
        {
            explorerDimmedNames = GetExcludedFolderSet(workspaceRoot);
            explorerDimmedNames.Remove(".git");
            if (explorerTree != null) explorerTree.DimmedPaths.Clear();
        }

        private static readonly string[] WindowsReservedNames = new string[]
        {
            "CON", "PRN", "AUX", "NUL", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
            "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9"
        };

        /// <summary>
        /// Validates a name typed for New File / New Folder / Rename. Relative sub-paths ("src\\util\\a.cs") are allowed
        /// like in VS Code, but the result must stay inside baseDir. Returns null when valid.
        /// </summary>
        public static string ValidateNewEntryName(string baseDir, string input, out string fullPath)
        {
            fullPath = null;
            string name = (input ?? "").Trim();
            if (name.Length == 0) return "A file or folder name must be provided.";
            if (Path.IsPathRooted(name) || name.Contains(":")) return "Please enter a name relative to the selected folder.";
            char[] invalid = Path.GetInvalidFileNameChars().Where(c => c != '/' && c != '\\').ToArray();
            if (name.IndexOfAny(invalid) >= 0) return "The name '" + name + "' contains characters that are not allowed.";
            foreach (string seg in name.Split('/', '\\'))
            {
                if (seg == "." || seg == "..") return "Names cannot contain '.' or '..' path segments.";
                if (seg.Length > 0 && (seg.EndsWith(".") || seg.EndsWith(" "))) return "'" + seg + "' is not a valid name on Windows.";
                string stem = seg.Split('.')[0].ToUpperInvariant();
                if (WindowsReservedNames.Contains(stem)) return "'" + seg + "' is a reserved name on Windows.";
            }
            // Combine with the untrimmed full path: trimming "D:\\" to "D:" made Path.Combine drive-relative.
            string rootFull = Path.GetFullPath(baseDir);
            string root = rootFull.TrimEnd('\\', '/');
            string combined = Path.GetFullPath(Path.Combine(rootFull, name)).TrimEnd('\\', '/');
            if (!combined.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                return "The name must point inside the selected folder.";
            fullPath = combined;
            return null;
        }

        private static readonly HashSet<string> SafeViewerExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ".png", ".jpg", ".jpeg", ".gif", ".bmp", ".webp", ".ico", ".icns", ".tif", ".tiff", ".heic", ".pdf", ".mp3", ".wav", ".m4a", ".flac", ".ogg", ".mp4", ".mov", ".m4v", ".avi", ".mkv", ".webm", ".ttf", ".otf", ".woff", ".woff2"
        };

        private static bool IsLikelyBinaryFile(string path)
        {
            try
            {
                string ext = Path.GetExtension(path ?? "");
                if (!string.IsNullOrEmpty(ext) && DefaultIgnoredBinaryExtensions.Contains(ext)) return true;
                using (FileStream fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                {
                    byte[] buf = new byte[8000];
                    int n = fs.Read(buf, 0, buf.Length);
                    if (n >= 2 && ((buf[0] == 0xFF && buf[1] == 0xFE) || (buf[0] == 0xFE && buf[1] == 0xFF))) return false;
                    for (int i = 0; i < n; i++) if (buf[i] == 0) return true;
                }
            }
            catch { }
            return false;
        }

        /// <summary>
        /// Re-reads the Explorer tree from disk keeping expanded folders, selection and scroll, optionally
        /// revealing a path. Replaces the previous full LoadWorkspace() after every file operation, which
        /// collapsed the whole tree, re-indexed the workspace and respawned exited terminals.
        /// </summary>
        public void RefreshExplorerPreservingState(string revealPath)
        {
            if (explorerTree == null || string.IsNullOrEmpty(workspaceRoot) || !Directory.Exists(workspaceRoot)) return;
            HashSet<string> expanded = explorerTree.GetExpandedPaths();
            string selectedPath = explorerTree.SelectedNode != null ? explorerTree.SelectedNode.Tag as string : null;
            if (!string.IsNullOrEmpty(revealPath))
            {
                selectedPath = revealPath;
                string d = Path.GetDirectoryName(revealPath);
                while (!string.IsNullOrEmpty(d) && d.StartsWith(workspaceRoot, StringComparison.OrdinalIgnoreCase))
                {
                    expanded.Add(d);
                    d = Path.GetDirectoryName(d);
                }
            }

            RefreshExplorerDimmedNames();
            HashSet<string> hidden = GetExplorerHiddenSet();
            bool rootCollapsed = explorerTree.IsRootCollapsedByHeader;
            explorerTree.ClearExpanded();
            List<string> roots = new List<string>();
            roots.Add(workspaceRoot);
            foreach (string extra in additionalWorkspaceFolders)
                if (!string.IsNullOrEmpty(extra) && Directory.Exists(extra) && !string.Equals(extra, workspaceRoot, StringComparison.OrdinalIgnoreCase))
                    roots.Add(extra);

            explorerTree.Nodes.Clear();
            foreach (string root in roots)
            {
                TreeNode rootNode = new TreeNode(Path.GetFileName(root.TrimEnd('\\', '/'))) { Tag = root, StateImageIndex = 1 };
                PopulateSingleDirectoryLevel(root, rootNode, hidden, false);
                if (rootNode.Nodes.Count == 0)
                    rootNode.Nodes.Add(new TreeNode("No files in folder") { Tag = "__EMPTY_FOLDER__", StateImageIndex = 2 });
                explorerTree.Nodes.Add(rootNode);
                if (!(rootCollapsed && string.Equals(root, workspaceRoot, StringComparison.OrdinalIgnoreCase)))
                    explorerTree.MarkExpanded(rootNode);
                RestoreExpandedChildren(rootNode, expanded, hidden);
            }

            TreeNode sel = !string.IsNullOrEmpty(selectedPath) ? explorerTree.FindNodeByPath(selectedPath) : null;
            explorerTree.RebuildTreeLayout(false);
            if (sel != null)
            {
                if (!string.IsNullOrEmpty(revealPath)) explorerTree.RevealAndSelect(sel);
                else explorerTree.SelectedNode = sel;
            }
            else
            {
                explorerTree.SelectedNode = null; // never keep a reference to a detached node
            }
            explorerTree.Invalidate();
        }

        private void RestoreExpandedChildren(TreeNode parent, HashSet<string> expanded, HashSet<string> hidden)
        {
            foreach (TreeNode child in parent.Nodes.Cast<TreeNode>().ToList())
            {
                string p = child.Tag as string;
                if (string.IsNullOrEmpty(p) || child.StateImageIndex != 1 || !expanded.Contains(p)) continue;
                child.Nodes.Clear();
                PopulateSingleDirectoryLevel(p, child, hidden, false);
                explorerTree.MarkExpanded(child);
                RestoreExpandedChildren(child, expanded, hidden);
            }
        }

        /// <summary>Tree refresh plus lightweight index / git updates after a file operation.</summary>
        private void AfterExplorerFileOperation(string revealPath)
        {
            RefreshExplorerPreservingState(revealPath);
            if (!string.IsNullOrEmpty(revealPath) && File.Exists(revealPath) && !workspaceFiles.Contains(revealPath))
                workspaceFiles.Add(revealPath);
            else if (!string.IsNullOrEmpty(revealPath) && Directory.Exists(revealPath))
            {
                // Renamed / pasted / moved folder: index its files (they were dropped from Quick Open before).
                HashSet<string> excluded = GetExcludedFolderSet(workspaceRoot);
                HashSet<string> known = new HashSet<string>(workspaceFiles, StringComparer.OrdinalIgnoreCase);
                Stack<string> dirs = new Stack<string>();
                dirs.Push(revealPath);
                int added = 0;
                while (dirs.Count > 0 && added < 20000)
                {
                    string d = dirs.Pop();
                    try
                    {
                        foreach (string sub in Directory.GetDirectories(d))
                            if (!excluded.Contains(Path.GetFileName(sub))) dirs.Push(sub);
                        foreach (string f in Directory.GetFiles(d))
                        {
                            string ext = Path.GetExtension(f);
                            if ((string.IsNullOrEmpty(ext) || !DefaultIgnoredBinaryExtensions.Contains(ext)) && known.Add(f))
                            {
                                workspaceFiles.Add(f);
                                added++;
                            }
                        }
                    }
                    catch { }
                }
            }
            workspaceFiles.RemoveAll(f => !File.Exists(f));
            try { RefreshGitSidebarStatus(); } catch { }
        }

        private static readonly HashSet<string> DefaultIgnoredBinaryExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ".exe", ".dll", ".pdb", ".zip", ".ico", ".png", ".dat", ".bin", ".obj",
            ".cache", ".suo", ".vsix", ".nupkg", ".jpg", ".jpeg", ".gif", ".bmp",
            ".webp", ".pdf", ".woff", ".woff2", ".ttf", ".eot", ".mp3", ".mp4",
            ".wav", ".avi", ".mov", ".tar", ".gz", ".7z", ".so", ".dylib", ".class", ".pyc"
        };

        public void EnsureDirectoryNodePopulated(TreeNode folderNode)
        {
            if (folderNode == null) return;
            string dirPath = folderNode.Tag as string;
            if (string.IsNullOrEmpty(dirPath) || dirPath == "__LOADING__" || dirPath == "__LAZY_DIR__" || dirPath == "__EMPTY_FOLDER__") return;
            if (folderNode.Nodes.Count == 1 && (folderNode.Nodes[0].Tag as string) == "__LAZY_DIR__")
            {
                folderNode.Nodes.Clear();
                PopulateSingleDirectoryLevel(dirPath, folderNode, GetExplorerHiddenSet(), addFilesToWorkspaceIndex: false);
            }
        }

        /// <param name="hiddenNames">Names hidden from the tree (VS Code files.exclude), see GetExplorerHiddenSet.</param>
        private void PopulateSingleDirectoryLevel(string dirPath, TreeNode parentNode, HashSet<string> hiddenNames, bool addFilesToWorkspaceIndex)
        {
            try
            {
                string parentPath = parentNode.Tag as string;
                bool parentDimmed = explorerTree != null && !string.IsNullOrEmpty(parentPath) && explorerTree.DimmedPaths.Contains(parentPath);
                List<string> subDirs = new List<string>();
                foreach (string d in Directory.EnumerateDirectories(dirPath))
                {
                    string name = Path.GetFileName(d);
                    if (string.IsNullOrEmpty(name)) continue;
                    if (hiddenNames != null && hiddenNames.Contains(name)) continue;
                    subDirs.Add(d);
                    if (explorerTree != null && (parentDimmed || explorerDimmedNames.Contains(name))) explorerTree.DimmedPaths.Add(d);
                }
                subDirs.Sort((a, b) => string.Compare(Path.GetFileName(a), Path.GetFileName(b), StringComparison.OrdinalIgnoreCase));

                foreach (string subDir in subDirs)
                {
                    string name = Path.GetFileName(subDir);
                    TreeNode folderNode = new TreeNode(name)
                    {
                        Tag = subDir,
                        StateImageIndex = 1
                    };
                    // Add a single lightweight lazy marker child so chevron renders without recursive enumeration
                    folderNode.Nodes.Add(new TreeNode("") { Tag = "__LAZY_DIR__", StateImageIndex = 2 });
                    parentNode.Nodes.Add(folderNode);
                }

                List<string> files = new List<string>();
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
                    string name = Path.GetFileName(file);
                    string fext = Path.GetExtension(name);
                    bool indexable = (string.IsNullOrEmpty(fext) || !DefaultIgnoredBinaryExtensions.Contains(fext)) &&
                                     !name.StartsWith("unins000.", StringComparison.OrdinalIgnoreCase);
                    if (addFilesToWorkspaceIndex && indexable)
                        workspaceFiles.Add(file);
                    if (explorerTree != null && (parentDimmed || explorerDimmedNames.Contains(name))) explorerTree.DimmedPaths.Add(file);
                    TreeNode fileNode = new TreeNode(name)
                    {
                        Tag = file,
                        StateImageIndex = 2
                    };
                    parentNode.Nodes.Add(fileNode);
                }
            }
            catch { }
        }

        private static string ResolveFastGitBranch(string rootDir)
        {
            try
            {
                string cur = rootDir;
                while (!string.IsNullOrEmpty(cur))
                {
                    string headFile = Path.Combine(cur, ".git", "HEAD");
                    if (File.Exists(headFile))
                    {
                        string raw = File.ReadAllText(headFile).Trim();
                        const string prefix = "ref: refs/heads/";
                        if (raw.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                            return raw.Substring(prefix.Length).Trim();
                        if (raw.Length >= 7)
                            return raw.Substring(0, 7);
                        break;
                    }
                    DirectoryInfo parent = Directory.GetParent(cur);
                    cur = parent != null ? parent.FullName : null;
                }
            }
            catch { }
            return "no-git";
        }

        public void LoadWorkspace(string rootDir, bool restorePreviousTabs = false, bool keepCurrentTabs = false)
        {
            if (string.IsNullOrEmpty(rootDir) || !Directory.Exists(rootDir))
            {
                IsWorkspaceLoading = false;
                if (sidebarProgressBar != null)
                    sidebarProgressBar.StopProgress();
                if (string.IsNullOrEmpty(workspaceRoot) || !Directory.Exists(workspaceRoot))
                    CloseFolderWorkspace();
                else if (statusLeftLabel != null)
                {
                    string br = ResolveFastGitBranch(workspaceRoot);
                    statusLeftLabel.Text = (string.IsNullOrEmpty(br) || br == "no-git")
                        ? Path.GetFileName(workspaceRoot)
                        : string.Format("{0}  •  {1}", br, Path.GetFileName(workspaceRoot));
                }
                return;
            }

            if (!keepCurrentTabs)
            {
                if (!ConfirmSaveOrDiscardAllDirtyBuffers(openTabs))
                    return;
            }

            Stopwatch swLoad = Stopwatch.StartNew();

            if (workspaceLoadCts != null)
            {
                try { workspaceLoadCts.Cancel(); } catch { }
            }
            System.Threading.Interlocked.Exchange(ref pendingWorkspaceFinalizeUi, null);
            var cts = new System.Threading.CancellationTokenSource();
            workspaceLoadCts = cts;
            int gen = ++workspaceLoadGeneration;
            IsWorkspaceLoading = true;

            if (!keepCurrentTabs)
                additionalWorkspaceFolders.Clear();
            workspaceRoot = rootDir;
            terminalWorkingDir = rootDir;
            SyncTerminalsToWorkingDirectory(rootDir);
            settings.AddRecentWorkspace(rootDir);
            RebuildOpenRecentSubMenu();
            editorControl.SetRecentWorkspaces(settings.RecentWorkspaces);

            workspaceFiles.Clear();
            if (!keepCurrentTabs)
            {
                workspaceFileDiagnosticsMap.Clear();
                WorkspaceDiagnostics.Clear();
                if (aiEngine != null)
                {
                    if (aiEngine.WorkspaceSymbolMap != null) aiEngine.WorkspaceSymbolMap.Clear();
                    if (aiEngine.WorkspaceSymbols != null) aiEngine.WorkspaceSymbols.Clear();
                }
                ClearAttachedContextPills();
            }
            explorerTree.Nodes.Clear();

            sidebarHeaderLabel.Text = "EXPLORER";
            if (explorerToolbar != null) explorerToolbar.Visible = true;
            if (explorerNewFileBtn != null) explorerNewFileBtn.Visible = true;
            if (explorerNewFolderBtn != null) explorerNewFolderBtn.Visible = true;
            if (explorerRefreshBtn != null) explorerRefreshBtn.Visible = true;
            if (explorerCollapseBtn != null)
            {
                explorerCollapseBtn.Visible = true;
                int ew = explorerToolbar.Width;
                explorerCollapseBtn.Location = new Point(ew - 24, 1);
                explorerRefreshBtn.Location = new Point(explorerCollapseBtn.Left - 23, 1);
                explorerNewFolderBtn.Location = new Point(explorerRefreshBtn.Left - 23, 1);
                explorerNewFileBtn.Location = new Point(explorerNewFolderBtn.Left - 23, 1);
                explorerFolderHeaderLabel.Width = Math.Max(60, explorerNewFileBtn.Left - 10);
            }
            if (openEditorsSectionPanel != null) openEditorsSectionPanel.Visible = true;
            if (outlineBottomPanel != null) outlineBottomPanel.Visible = true;
            if (outlineSplitter != null) outlineSplitter.Visible = true;
            if (explorerFolderHeaderLabel != null)
                explorerFolderHeaderLabel.Text = Path.GetFileName(rootDir).ToUpperInvariant();
            if (statusBar != null && activeTheme != null)
                statusBar.BackColor = ResolveStatusBarBackColor(activeTheme, true);
            if (sidebarProgressBar != null)
                sidebarProgressBar.StartProgress();
            if (statusLeftLabel != null)
                statusLeftLabel.Text = "Opening " + Path.GetFileName(rootDir) + "…";

            HashSet<string> excludedFolders = GetExcludedFolderSet(rootDir);

            explorerTree.ShowEmptyWorkspaceState = false;
            TreeNode rootNode = new TreeNode(Path.GetFileName(rootDir))
            {
                Tag = rootDir,
                StateImageIndex = 1
            };
            TreeNode loadingNode = new TreeNode("Loading…")
            {
                Tag = "__LOADING__",
                StateImageIndex = 2
            };
            rootNode.Nodes.Add(loadingNode);
            try
            {
                RefreshExplorerDimmedNames();
                PopulateSingleDirectoryLevel(rootDir, rootNode, GetExplorerHiddenSet(), addFilesToWorkspaceIndex: true);
            }
            finally
            {
                if (rootNode.Nodes.Contains(loadingNode))
                    rootNode.Nodes.Remove(loadingNode);
            }
            if (rootNode.Nodes.Count == 0)
            {
                rootNode.Nodes.Add(new TreeNode("No files in folder")
                {
                    Tag = "__EMPTY_FOLDER__",
                    StateImageIndex = 2
                });
            }
            explorerTree.Nodes.Add(rootNode);
            rootNode.Expand();
            foreach (string extraFolder in additionalWorkspaceFolders.ToList())
            {
                if (!string.IsNullOrEmpty(extraFolder) && Directory.Exists(extraFolder) && !string.Equals(extraFolder, rootDir, StringComparison.OrdinalIgnoreCase))
                {
                    AppendAdditionalFolderRootNode(extraFolder);
                }
            }
            explorerTree.RebuildTreeLayout(true);
            // Watch immediately (not only after full indexing, which can be slow or fail).
            InitializeWorkspaceFileWatcher(rootDir);

            string fastGitBranch = ResolveFastGitBranch(rootDir);
            if (statusLeftLabel != null)
            {
                statusLeftLabel.Text = (string.IsNullOrEmpty(fastGitBranch) || fastGitBranch == "no-git")
                    ? Path.GetFileName(rootDir)
                    : string.Format("{0}  •  {1}", fastGitBranch, Path.GetFileName(rootDir));
            }

            LastFirstTreePaintMs = swLoad.ElapsedMilliseconds;

            if (!keepCurrentTabs)
            {
                openTabs.Clear();
                activeTab = null;

                if (restorePreviousTabs && settings.OpenTabPaths != null)
                {
                    foreach (string savedTabPath in settings.OpenTabPaths)
                    {
                        if (File.Exists(savedTabPath) && savedTabPath.StartsWith(workspaceRoot, StringComparison.OrdinalIgnoreCase))
                        {
                            OpenFileInTab(savedTabPath);
                        }
                    }
                }

                editorControl.ShowWelcomePage = IsWelcomeTabOpen;
                editorControl.LoadBuffer(activeTab);
                RefreshOutlineAndProblems(activeTab);
                RefreshTabStrip();
            }
            else
            {
                editorControl.ShowWelcomePage = IsWelcomeTabOpen;
                if (activeTab == null)
                    editorControl.LoadBuffer(null);
                RefreshOutlineAndProblems(activeTab);
                RefreshTabStrip();
            }

            RefreshOpenEditorsSection();
            LayoutTopTitleBar();
            UpdateWindowTitle();

            // Run full workspace file walk, BM25/@Codebase indexing, AST symbol map, diagnostics, git status & watcher on background thread
            System.Threading.CancellationToken token = cts.Token;
            Task.Run(() =>
            {
                List<string> discoveredFiles = new List<string>(2048);
                Dictionary<string, List<DiagnosticItem>> bgDiags = new Dictionary<string, List<DiagnosticItem>>(StringComparer.OrdinalIgnoreCase);
                List<GitFileStatus> gitItems = new List<GitFileStatus>();
                string gitBranch = ResolveFastGitBranch(rootDir);

                try
                {
                    Queue<string> dirQueue = new Queue<string>();
                    dirQueue.Enqueue(rootDir);
                    foreach (string extraDir in additionalWorkspaceFolders.ToList())
                    {
                        if (!string.IsNullOrEmpty(extraDir) && Directory.Exists(extraDir))
                            dirQueue.Enqueue(extraDir);
                    }
                    Stopwatch swStatus = Stopwatch.StartNew();

                    while (dirQueue.Count > 0 && !token.IsCancellationRequested && discoveredFiles.Count < 25000)
                    {
                        string currentDir = dirQueue.Dequeue();
                        try
                        {
                            foreach (string subDir in Directory.EnumerateDirectories(currentDir))
                            {
                                if (token.IsCancellationRequested) break;
                                string dName = Path.GetFileName(subDir);
                                if (string.IsNullOrEmpty(dName) || excludedFolders.Contains(dName)) continue;
                                dirQueue.Enqueue(subDir);
                            }

                            foreach (string file in Directory.EnumerateFiles(currentDir))
                            {
                                if (token.IsCancellationRequested) break;
                                string fName = Path.GetFileName(file);
                                if (string.IsNullOrEmpty(fName) || fName.StartsWith("unins000.", StringComparison.OrdinalIgnoreCase))
                                    continue;
                                string ext = Path.GetExtension(fName);
                                if (!string.IsNullOrEmpty(ext) && DefaultIgnoredBinaryExtensions.Contains(ext))
                                    continue;
                                discoveredFiles.Add(file);
                                if (discoveredFiles.Count >= 25000) break;
                            }
                        }
                        catch { }
                    }

                    if (token.IsCancellationRequested || gen != workspaceLoadGeneration) return;

                    if (this.IsHandleCreated)
                    {
                        List<string> earlySnapshot = new List<string>(discoveredFiles);
                        this.BeginInvoke(new Action(() =>
                        {
                            if (token.IsCancellationRequested || gen != workspaceLoadGeneration || this.IsDisposed) return;
                            workspaceFiles = earlySnapshot;
                        }));
                    }

                    // Index up to 2,500 source files <= 1 MB on background thread
                    List<string> filesToIndex = discoveredFiles.Count <= 2500 ? discoveredFiles : discoveredFiles.Take(2500).ToList();
                    aiEngine.IndexWorkspace(rootDir, filesToIndex, token, null);

                    if (token.IsCancellationRequested || gen != workspaceLoadGeneration) return;

                    bgDiags = ScanWorkspaceDiagnosticsBackground(rootDir, discoveredFiles, token);
                    if (token.IsCancellationRequested || gen != workspaceLoadGeneration) return;

                    gitItems = aiEngine.QueryGitWorkspaceStatus(rootDir);
                    if (!string.IsNullOrEmpty(aiEngine.ActiveGitBranch))
                        gitBranch = aiEngine.ActiveGitBranch;
                    if (token.IsCancellationRequested || gen != workspaceLoadGeneration) return;
                }
                catch { }
                finally
                {
                    if (gen == workspaceLoadGeneration && !this.IsDisposed)
                    {
                        Action finalizeOnUi = () =>
                        {
                            if (gen != workspaceLoadGeneration || this.IsDisposed) return;

                            if (rootNode.Nodes.Contains(loadingNode))
                            {
                                rootNode.Nodes.Remove(loadingNode);
                                explorerTree.RebuildTreeLayout(true);
                            }

                            if (!token.IsCancellationRequested)
                            {
                                workspaceFiles = discoveredFiles;
                                foreach (var kvp in bgDiags)
                                    workspaceFileDiagnosticsMap[kvp.Key] = kvp.Value;

                                editorControl.AutocompleteSymbols = aiEngine.WorkspaceSymbols;
                                splitEditorControl.AutocompleteSymbols = aiEngine.WorkspaceSymbols;
                                gitStatusItems = gitItems;
                                if (gitBranchLabel != null)
                                {
                                    bool noGit = string.IsNullOrEmpty(gitBranch) || string.Equals(gitBranch, "no-git", StringComparison.OrdinalIgnoreCase);
                                    gitBranchLabel.Text = noGit ? "No Git repository" : ("Branch: " + gitBranch + " (" + gitItems.Count + " changed)");
                                    gitBranchLabel.Invalidate();
                                }
                            }

                            RefreshOutlineAndProblems(activeTab);
                            if (sidebarProgressBar != null)
                                sidebarProgressBar.StopProgress();
                            IsWorkspaceLoading = false;
                            LastFullIndexCompleteMs = swLoad.ElapsedMilliseconds;

                            if (!token.IsCancellationRequested)
                            {
                                AppendOutputLog(string.Format("Indexed workspace '{0}' in {1}ms (first tree paint {2}ms): {3} files, {4} AST symbols, {5} BM25 semantic chunks.",
                                    Path.GetFileName(rootDir), LastFullIndexCompleteMs, LastFirstTreePaintMs, workspaceFiles.Count, aiEngine.WorkspaceSymbols.Count, aiEngine.IndexedChunks.Count));
                            }

                            if (statusLeftLabel != null)
                            {
                                bool noGit = string.IsNullOrEmpty(gitBranch) || string.Equals(gitBranch, "no-git", StringComparison.OrdinalIgnoreCase);
                                statusLeftLabel.Text = noGit
                                    ? Path.GetFileName(rootDir)
                                    : string.Format("{0}  •  {1}", gitBranch, Path.GetFileName(rootDir));
                            }
                        };

                        System.Threading.Interlocked.Exchange(ref pendingWorkspaceFinalizeUi, finalizeOnUi);
                        if (this.IsHandleCreated && this.InvokeRequired)
                        {
                            try
                            {
                                this.BeginInvoke(new Action(() =>
                                {
                                    Action pending = System.Threading.Interlocked.Exchange(ref pendingWorkspaceFinalizeUi, null);
                                    if (pending != null) pending();
                                }));
                            }
                            catch { }
                        }
                        else if (!this.IsDisposed)
                        {
                            Action pending = System.Threading.Interlocked.Exchange(ref pendingWorkspaceFinalizeUi, null);
                            if (pending != null) pending();
                        }
                    }
                }
            }, token);
        }

        private void AppendAdditionalFolderRootNode(string folderPath)
        {
            if (explorerTree == null || string.IsNullOrEmpty(folderPath) || !Directory.Exists(folderPath)) return;
            HashSet<string> excludedFolders = GetExcludedFolderSet(folderPath);
            TreeNode extraRoot = new TreeNode(Path.GetFileName(folderPath))
            {
                Tag = folderPath,
                StateImageIndex = 1
            };
            PopulateSingleDirectoryLevel(folderPath, extraRoot, excludedFolders, addFilesToWorkspaceIndex: true);
            if (extraRoot.Nodes.Count == 0)
            {
                extraRoot.Nodes.Add(new TreeNode("No files in folder")
                {
                    Tag = "__EMPTY_FOLDER__",
                    StateImageIndex = 2
                });
            }
            explorerTree.Nodes.Add(extraRoot);
            extraRoot.Expand();
        }

        public void AddFolderToWorkspace(string folderPath)
        {
            if (string.IsNullOrEmpty(folderPath) || !Directory.Exists(folderPath)) return;
            if (string.IsNullOrEmpty(workspaceRoot) || !Directory.Exists(workspaceRoot))
            {
                LoadWorkspace(folderPath, restorePreviousTabs: false, keepCurrentTabs: true);
                return;
            }
            if (string.Equals(workspaceRoot, folderPath, StringComparison.OrdinalIgnoreCase) ||
                additionalWorkspaceFolders.Any(f => string.Equals(f, folderPath, StringComparison.OrdinalIgnoreCase)))
                return;

            additionalWorkspaceFolders.Add(folderPath);
            AppendAdditionalFolderRootNode(folderPath);
            explorerTree.RebuildTreeLayout(true);
            try
            {
                foreach (string f in Directory.EnumerateFiles(folderPath, "*.*", SearchOption.AllDirectories).Take(2000))
                {
                    string ext = Path.GetExtension(f);
                    if (!string.IsNullOrEmpty(ext) && DefaultIgnoredBinaryExtensions.Contains(ext)) continue;
                    if (!workspaceFiles.Contains(f, StringComparer.OrdinalIgnoreCase))
                        workspaceFiles.Add(f);
                }
            }
            catch { }
        }

        public void PromptAddFolderToWorkspaceDialog()
        {
            string chosen = VistaFolderPicker.ShowDialog(this.Handle, "Select Folder to Add to Workspace", workspaceRoot);
            if (!string.IsNullOrEmpty(chosen) && Directory.Exists(chosen))
            {
                AddFolderToWorkspace(chosen);
            }
        }

        private Dictionary<string, List<DiagnosticItem>> ScanWorkspaceDiagnosticsBackground(string rootDir, List<string> files, System.Threading.CancellationToken ct)
        {
            Dictionary<string, List<DiagnosticItem>> result = new Dictionary<string, List<DiagnosticItem>>(StringComparer.OrdinalIgnoreCase);
            if (files == null) return result;
            int scanned = 0;
            foreach (string fp in files)
            {
                if (ct.IsCancellationRequested || scanned >= 60) break;
                try
                {
                    FileInfo fi = new FileInfo(fp);
                    if (!fi.Exists || fi.Length > 256 * 1024) continue;
                    scanned++;
                    string raw = File.ReadAllText(fp);
                    if (raw.IndexOf('\0') >= 0) continue;
                    EditorTabBuffer temp = new EditorTabBuffer
                    {
                        FilePath = fp,
                        RelativePath = LlmHarnessAndAgentEngine.GetRelativePath(rootDir, fp),
                        FileName = Path.GetFileName(fp),
                        Language = Path.GetExtension(fp).TrimStart('.'),
                        Content = raw
                    };
                    temp.SyncLinesFromContent();
                    LlmHarnessAndAgentEngine.AnalyzeBufferAstAndDiagnostics(temp);
                    if (temp.Diagnostics != null && temp.Diagnostics.Count > 0)
                        result[fp] = new List<DiagnosticItem>(temp.Diagnostics);
                }
                catch { }
            }
            return result;
        }

        private void InitializeWorkspaceFileWatcher(string rootDir)
        {
            try
            {
                if (workspaceFileWatcher != null)
                {
                    workspaceFileWatcher.EnableRaisingEvents = false;
                    workspaceFileWatcher.Dispose();
                    workspaceFileWatcher = null;
                }
                if (string.IsNullOrEmpty(rootDir) || !Directory.Exists(rootDir)) return;

                workspaceFileWatcher = new FileSystemWatcher(rootDir)
                {
                    IncludeSubdirectories = true,
                    NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.Size,
                    InternalBufferSize = 64 * 1024
                };
                FileSystemEventHandler onChanged = (s, e) => QueueWorkspaceFsChange(e.FullPath, e.ChangeType != WatcherChangeTypes.Changed, e.ChangeType == WatcherChangeTypes.Deleted);
                workspaceFileWatcher.Changed += onChanged;
                workspaceFileWatcher.Created += onChanged;
                workspaceFileWatcher.Deleted += onChanged;
                workspaceFileWatcher.Renamed += (s, e) =>
                {
                    QueueWorkspaceFsChange(e.OldFullPath, true, true);
                    QueueWorkspaceFsChange(e.FullPath, true, false);
                };
                workspaceFileWatcher.Error += (s, e) =>
                {
                    // Internal buffer overflow: changes were lost, so do one full refresh.
                    lock (fsChangeLock) { fsTreeDirty = true; fsGitDirty = true; }
                    ScheduleFsFlush();
                };
                workspaceFileWatcher.EnableRaisingEvents = true;
            }
            catch { }
        }

        private readonly object fsChangeLock = new object();
        private bool fsTreeDirty;
        private bool fsGitDirty;
        private readonly HashSet<string> fsChangedFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> fsDeletedFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private System.Windows.Forms.Timer fsFlushTimer;
        private DateTime fsFirstPendingUtc = DateTime.MinValue;
        private readonly HashSet<EditorTabBuffer> tabsWithReloadPromptOpen = new HashSet<EditorTabBuffer>();

        private void QueueWorkspaceFsChange(string fullPath, bool structural, bool deleted)
        {
            if (string.IsNullOrEmpty(fullPath)) return;
            bool insideGit = fullPath.IndexOf("\\.git\\", StringComparison.OrdinalIgnoreCase) >= 0 ||
                             fullPath.EndsWith("\\.git", StringComparison.OrdinalIgnoreCase);
            lock (fsChangeLock)
            {
                if (insideGit)
                {
                    string n = Path.GetFileName(fullPath);
                    if (n == "HEAD" || n == "index" || fullPath.IndexOf("\\refs\\", StringComparison.OrdinalIgnoreCase) >= 0) fsGitDirty = true;
                    else return;
                }
                else
                {
                    fsGitDirty = true;
                    if (structural) fsTreeDirty = true;
                    if (deleted) fsDeletedFiles.Add(fullPath);
                    else { fsChangedFiles.Add(fullPath); fsDeletedFiles.Remove(fullPath); }
                }
            }
            ScheduleFsFlush();
        }

        private void ScheduleFsFlush()
        {
            if (!this.IsHandleCreated || this.IsDisposed) return;
            try
            {
                this.BeginInvoke(new Action(() =>
                {
                    if (this.IsDisposed) return;
                    if (fsFlushTimer == null)
                    {
                        fsFlushTimer = new System.Windows.Forms.Timer { Interval = 450 };
                        fsFlushTimer.Tick += (ts, te) => { fsFlushTimer.Stop(); fsFirstPendingUtc = DateTime.MinValue; FlushWorkspaceFsChanges(); };
                    }
                    if (fsFirstPendingUtc == DateTime.MinValue) fsFirstPendingUtc = DateTime.UtcNow;
                    // Max wait: a file rewritten more often than the debounce (dev-server log) must not starve flushes.
                    if ((DateTime.UtcNow - fsFirstPendingUtc).TotalMilliseconds > 2000)
                    {
                        fsFlushTimer.Stop();
                        fsFirstPendingUtc = DateTime.MinValue;
                        FlushWorkspaceFsChanges();
                        return;
                    }
                    fsFlushTimer.Stop();
                    fsFlushTimer.Start();
                }));
            }
            catch { }
        }

        private bool IsUnderExcludedFolder(string fullPath, HashSet<string> excludedNames)
        {
            if (string.IsNullOrEmpty(workspaceRoot) || !fullPath.StartsWith(workspaceRoot, StringComparison.OrdinalIgnoreCase)) return false;
            foreach (string seg in fullPath.Substring(workspaceRoot.Length).Split(new char[] { '\\', '/' }, StringSplitOptions.RemoveEmptyEntries))
                if (excludedNames.Contains(seg)) return true;
            return false;
        }

        private void FlushWorkspaceFsChanges()
        {
            bool tree, git;
            List<string> changed, deleted;
            lock (fsChangeLock)
            {
                tree = fsTreeDirty; git = fsGitDirty;
                changed = fsChangedFiles.ToList(); deleted = fsDeletedFiles.ToList();
                fsTreeDirty = false; fsGitDirty = false;
                fsChangedFiles.Clear(); fsDeletedFiles.Clear();
            }
            if (string.IsNullOrEmpty(workspaceRoot)) return;
            HashSet<string> excludedNames = GetExcludedFolderSet(workspaceRoot);
            if (tree)
            {
                RefreshExplorerPreservingState(null);
                HashSet<string> known = new HashSet<string>(workspaceFiles, StringComparer.OrdinalIgnoreCase);
                foreach (string c in changed)
                {
                    // node_modules / bin / obj churn refreshes the tree but never enters the search index.
                    if (IsUnderExcludedFolder(c, excludedNames)) continue;
                    string ext = Path.GetExtension(c);
                    if ((string.IsNullOrEmpty(ext) || !DefaultIgnoredBinaryExtensions.Contains(ext)) && File.Exists(c) && known.Add(c))
                        workspaceFiles.Add(c);
                }
                if (deleted.Count > 0) workspaceFiles.RemoveAll(f => !File.Exists(f));
            }
            foreach (string d in deleted)
            {
                foreach (EditorTabBuffer t in openTabs.Where(x => string.Equals(x.FilePath, d, StringComparison.OrdinalIgnoreCase)))
                    if (!File.Exists(d)) t.IsDeletedOnDisk = true;
            }
            if (deleted.Count > 0) RefreshTabStrip();
            // One reload check per file per debounce window: a single save fires several Changed events.
            HashSet<string> openPaths = new HashSet<string>(openTabs.Where(t => !string.IsNullOrEmpty(t.FilePath)).Select(t => t.FilePath), StringComparer.OrdinalIgnoreCase);
            foreach (string c in changed)
                if (openPaths.Contains(c)) HandleExternalFileChanged(c);
            if (git) { try { RefreshGitSidebarStatus(); } catch { } }
        }

        public void HandleExternalFileChanged(string fullPath)
        {
            if (string.IsNullOrEmpty(fullPath) || !File.Exists(fullPath)) return;
            foreach (EditorTabBuffer back in openTabs.Where(t => t.IsDeletedOnDisk && string.Equals(t.FilePath, fullPath, StringComparison.OrdinalIgnoreCase)))
            {
                back.IsDeletedOnDisk = false; // file came back (branch switch / stash pop), even if content is unchanged
                RefreshTabStrip();
            }
            DateTime lastSave;
            if (internalSaveTimestamps.TryGetValue(fullPath, out lastSave) && (DateTime.UtcNow - lastSave).TotalMilliseconds < 1500)
                return;

            EditorTabBuffer tab = openTabs.FirstOrDefault(t => string.Equals(t.FilePath, fullPath, StringComparison.OrdinalIgnoreCase));
            if (tab == null) return;

            Encoding diskEnc;
            bool diskBom;
            string diskEol;
            bool diskFinalNl;
            string diskContent;
            try
            {
                diskContent = EditorTabBuffer.ReadFilePreservingMetadata(fullPath, out diskEnc, out diskBom, out diskEol, out diskFinalNl);
            }
            catch { return; }

            string normDisk = (diskContent ?? "").Replace("\r\n", "\n");
            string normTab = (tab.Content ?? "").Replace("\r\n", "\n");
            if (normDisk == normTab && tab.EolSequence == diskEol && tab.HasBom == diskBom && tab.HasFinalNewline == diskFinalNl) return;

            bool shouldReload = true;
            tab.IsDeletedOnDisk = false;
            if (tab.IsDirty)
            {
                if (tabsWithReloadPromptOpen.Contains(tab)) return; // a prompt for this tab is already showing
                if (ExternalFileReloadOverride != null)
                    shouldReload = ExternalFileReloadOverride(tab);
                else if (SuppressUnsavedPrompts)
                    shouldReload = false;
                else
                {
                    tabsWithReloadPromptOpen.Add(tab);
                    try
                    {
                    shouldReload = MessageBox.Show(
                        this,
                        "The file '" + tab.FileName + "' has been modified on disk, and your editor buffer also has unsaved changes.\n\nDo you want to reload from disk and overwrite your unsaved editor changes?",
                        "codeLess IDE — External File Modification",
                        MessageBoxButtons.YesNo,
                        MessageBoxIcon.Warning
                    ) == DialogResult.Yes;
                    }
                    finally { tabsWithReloadPromptOpen.Remove(tab); }
                }
            }

            if (shouldReload)
            {
                tab.PushUndoSnapshot();
                tab.FileEncoding = diskEnc;
                tab.HasBom = diskBom;
                tab.EolSequence = diskEol;
                tab.HasFinalNewline = diskFinalNl;
                tab.Content = diskContent;
                tab.SavedContent = diskContent;
                tab.SyncLinesFromContent();
                tab.IsDirty = false;
                tab.RefreshGutterDiff();
                LlmHarnessAndAgentEngine.AnalyzeBufferAstAndDiagnostics(tab);
                if (activeTab == tab)
                    editorControl.LoadBuffer(tab);
                if (splitEditorControl != null && splitEditorControl.CurrentBuffer == tab)
                    splitEditorControl.LoadBuffer(tab);
                RefreshOutlineAndProblems(activeTab);
                RefreshTabStrip();
                statusLeftLabel.Text = "Reloaded " + tab.FileName;
            }
        }

        private void PopulateDirectoryNodes(string dirPath, TreeNode parentNode, int depth)
        {
            if (depth > 6 || workspaceFiles.Count > 1400) return;
            string[] ignored = { "node_modules", ".git", "target", "dist", "bin", "obj", ".vs", "__pycache__", "Output" };

            try
            {
                foreach (string subDir in Directory.GetDirectories(dirPath).OrderBy(d => Path.GetFileName(d)))
                {
                    string name = Path.GetFileName(subDir);
                    if (ignored.Contains(name, StringComparer.OrdinalIgnoreCase)) continue;
                    TreeNode folderNode = new TreeNode(name) { Tag = subDir };
                    PopulateDirectoryNodes(subDir, folderNode, depth + 1);
                    parentNode.Nodes.Add(folderNode);
                }

                string[] ignoredExts = new[]
                {
                    ".exe", ".dll", ".pdb", ".zip", ".ico", ".png", ".dat", ".bin", ".obj",
                    ".cache", ".suo", ".vsix", ".nupkg", ".jpg", ".jpeg", ".gif", ".bmp",
                    ".webp", ".pdf", ".woff", ".woff2", ".ttf", ".eot"
                };
                foreach (string file in Directory.GetFiles(dirPath).OrderBy(f => Path.GetFileName(f)))
                {
                    string name = Path.GetFileName(file);
                    if (name.StartsWith("unins000.", StringComparison.OrdinalIgnoreCase))
                        continue;
                    string ext = Path.GetExtension(name);
                    if (!string.IsNullOrEmpty(ext) && ignoredExts.Contains(ext, StringComparer.OrdinalIgnoreCase))
                        continue;

                    workspaceFiles.Add(file);
                    TreeNode fileNode = new TreeNode(name) { Tag = file };
                    parentNode.Nodes.Add(fileNode);
                }
            }
            catch { }
        }

        public EditorTabBuffer OpenFileInTab(string filePath)
        {
            EditorTabBuffer existing = openTabs.FirstOrDefault(
                t => string.Equals(t.FilePath, filePath, StringComparison.OrdinalIgnoreCase)
            );
            if (existing == null && !string.IsNullOrEmpty(filePath) && File.Exists(filePath) && IsLikelyBinaryFile(filePath))
            {
                // Binary content would be corrupted by the text editor on save. Only passive media types are handed to
                // the OS viewer: shell-executing arbitrary binaries would RUN .exe/.msi/.lnk/.jar... on a single click
                // (explorer, terminal file links, Quick Open, tab restore).
                string ext = Path.GetExtension(filePath);
                if (!string.IsNullOrEmpty(ext) && SafeViewerExtensions.Contains(ext))
                {
                    try { Process.Start(new ProcessStartInfo(filePath) { UseShellExecute = true }); } catch { }
                    if (statusLeftLabel != null) statusLeftLabel.Text = Path.GetFileName(filePath) + " opened in the default viewer.";
                }
                else if (statusLeftLabel != null)
                {
                    statusLeftLabel.Text = Path.GetFileName(filePath) + " is a binary file and was not opened (use Reveal in File Explorer).";
                }
                return null;
            }
            if (existing != null)
            {
                activeTab = existing;
                if (explorerTree != null) { explorerTree.ActiveEditorFilePath = filePath; explorerTree.Invalidate(); }
                editorControl.LoadBuffer(activeTab);
                RefreshOutlineAndProblems(activeTab);
                RefreshTabStrip();
                RefreshOpenEditorsSection();
                UpdateWindowTitle();
                RecordNavigationHistory(filePath, activeTab.CursorLine + 1);
                return existing;
            }

            try
            {
                long fileLen = 0;
                if (File.Exists(filePath))
                {
                    try { fileLen = new FileInfo(filePath).Length; } catch { }
                }
                bool isLarge = fileLen > 1024 * 1024;
                Encoding fileEnc = new UTF8Encoding(false);
                bool hasBom = false;
                string eol = "\r\n";
                bool hasFinalNl = false;
                string content = File.Exists(filePath)
                    ? EditorTabBuffer.ReadFilePreservingMetadata(filePath, out fileEnc, out hasBom, out eol, out hasFinalNl)
                    : "";
                string gitHeadContent = (!isLarge && File.Exists(filePath))
                    ? LlmHarnessAndAgentEngine.GetGitHeadFileContent(workspaceRoot, filePath)
                    : null;
                EditorTabBuffer tab = new EditorTabBuffer
                {
                    FilePath = filePath,
                    RelativePath = LlmHarnessAndAgentEngine.GetRelativePath(workspaceRoot, filePath),
                    FileName = Path.GetFileName(filePath),
                    Language = Path.GetExtension(filePath).TrimStart('.'),
                    Content = content,
                    SavedContent = content,
                    GitHeadContent = gitHeadContent,
                    EolSequence = eol,
                    FileEncoding = fileEnc,
                    HasBom = hasBom,
                    HasFinalNewline = hasFinalNl,
                    WordWrap = settings != null ? settings.WordWrap : false,
                    IsDirty = false,
                    IsLargeFile = isLarge
                };
                tab.SyncLinesFromContent();
                tab.RefreshGutterDiff();
                if (!isLarge)
                    LlmHarnessAndAgentEngine.AnalyzeBufferAstAndDiagnostics(tab);
                openTabs.Add(tab);
                activeTab = tab;
                if (settings != null && File.Exists(filePath))
                    settings.AddRecentFile(filePath);
                if (explorerTree != null) { explorerTree.ActiveEditorFilePath = filePath; explorerTree.Invalidate(); }
                editorControl.LoadBuffer(activeTab);
                RefreshOutlineAndProblems(activeTab);
                RefreshTabStrip();
                RefreshOpenEditorsSection();
                UpdateWindowTitle();
                RecordNavigationHistory(filePath, 1);
                PersistOpenTabPaths();
                return tab;
            }
            catch (Exception ex)
            {
                MessageBox.Show("Failed opening file: " + ex.Message, "codeLess IDE");
                return null;
            }
        }

        public bool ConfirmSaveOrDiscardDirtyBuffer(EditorTabBuffer tab)
        {
            if (tab == null || !tab.IsDirty) return true;

            DialogResult res;
            if (UnsavedPromptOverride != null)
            {
                res = UnsavedPromptOverride(tab);
            }
            else if (SuppressUnsavedPrompts || !Application.MessageLoop)
            {
                res = DialogResult.No;
            }
            else
            {
                using (var dlg = new UnsavedChangesDialog(new List<string> { tab.FileName }, activeTheme))
                {
                    res = dlg.ShowDialog(this);
                }
            }

            if (res == DialogResult.Yes)
            {
                return SaveTabBuffer(tab);
            }
            else if (res == DialogResult.No)
            {
                return true;
            }
            return false;
        }

        public bool ConfirmSaveOrDiscardAllDirtyBuffers(IEnumerable<EditorTabBuffer> tabs)
        {
            if (tabs == null) return true;
            List<EditorTabBuffer> dirtyList = tabs.Where(t => t != null && t.IsDirty).ToList();
            if (dirtyList.Count == 0) return true;

            if (UnsavedPromptOverride != null)
            {
                foreach (var t in dirtyList)
                {
                    if (!ConfirmSaveOrDiscardDirtyBuffer(t))
                        return false;
                }
                return true;
            }
            if (SuppressUnsavedPrompts || !Application.MessageLoop)
            {
                return true;
            }

            DialogResult res;
            using (var dlg = new UnsavedChangesDialog(dirtyList.Select(t => t.FileName).ToList(), activeTheme))
            {
                res = dlg.ShowDialog(this);
            }

            if (res == DialogResult.Yes)
            {
                foreach (var t in dirtyList)
                {
                    if (!SaveTabBuffer(t))
                        return false;
                }
                return true;
            }
            else if (res == DialogResult.No)
            {
                return true;
            }
            return false;
        }

        public bool CloseTabBuffer(EditorTabBuffer captured)
        {
            if (captured == null) return false;
            if (!ConfirmSaveOrDiscardDirtyBuffer(captured))
                return false;

            if (!string.IsNullOrEmpty(captured.FilePath))
                closedTabPaths.Push(captured.FilePath);

            openTabs.Remove(captured);
            bool anySameFileRemaining = openTabs.Any(t =>
                (!string.IsNullOrEmpty(captured.FilePath) && string.Equals(t.FilePath, captured.FilePath, StringComparison.OrdinalIgnoreCase)) ||
                (!string.IsNullOrEmpty(captured.FileName) && string.Equals(t.FileName, captured.FileName, StringComparison.OrdinalIgnoreCase)));
            if (!anySameFileRemaining)
            {
                RemoveContextPillsForFile(captured.FilePath, captured.FileName, captured.RelativePath);
            }
            PruneFileContextPillsWhenNoTabsOpen();
            if (!string.IsNullOrEmpty(captured.FilePath) && (workspaceFiles == null || !workspaceFiles.Any(f => string.Equals(f, captured.FilePath, StringComparison.OrdinalIgnoreCase))))
            {
                workspaceFileDiagnosticsMap.Remove(captured.FilePath);
            }
            if (isSplitEditorActive && splitEditorControl != null && splitEditorControl.CurrentBuffer == captured)
            {
                EditorTabBuffer nextSplit = openTabs.LastOrDefault();
                if (nextSplit != null)
                    splitEditorControl.LoadBuffer(nextSplit);
                else
                    ToggleSplitEditorView();
            }
            if (activeTab == captured)
            {
                activeTab = openTabs.LastOrDefault();
                if (editorControl != null)
                {
                    editorControl.ShowWelcomePage = IsWelcomeTabOpen;
                    editorControl.LoadBuffer(activeTab);
                }
            }
            if (explorerTree != null)
            {
                explorerTree.ActiveEditorFilePath = activeTab != null ? activeTab.FilePath : "";
                explorerTree.Invalidate();
            }
            RefreshOutlineAndProblems(activeTab);
            RefreshChangesetReviewBar();
            PersistOpenTabPaths();
            RefreshTabStrip();
            RefreshOpenEditorsSection();
            UpdateWindowTitle();
            return true;
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (!ConfirmSaveOrDiscardAllDirtyBuffers(openTabs))
            {
                e.Cancel = true;
                return;
            }
            KillAllTerminalChildProcesses();
            if (workspaceFileWatcher != null)
            {
                try { workspaceFileWatcher.Dispose(); } catch { }
                workspaceFileWatcher = null;
            }
            base.OnFormClosing(e);
        }

        public void ReorderTab(int fromIndex, int toIndex)
        {
            if (fromIndex < 0 || fromIndex >= openTabs.Count) return;
            if (toIndex < 0 || toIndex >= openTabs.Count || fromIndex == toIndex) return;
            EditorTabBuffer item = openTabs[fromIndex];
            openTabs.RemoveAt(fromIndex);
            openTabs.Insert(toIndex, item);
            PersistOpenTabPaths();
            RefreshTabStrip();
        }

        public void ShowTabOverflowMenu()
        {
            ContextMenuStrip menu = CreateAutoDisposingContextMenu();
            if (IsWelcomeTabOpen)
            {
                var wItem = CreateMenuItem("Welcome", "", () => OpenWelcomeTab());
                wItem.Checked = (activeTab == null);
                menu.Items.Add(wItem);
            }
            for (int i = 0; i < openTabs.Count; i++)
            {
                EditorTabBuffer tab = openTabs[i];
                string relSuffix = !string.IsNullOrEmpty(tab.RelativePath) && !string.Equals(tab.RelativePath, tab.FileName, StringComparison.OrdinalIgnoreCase)
                    ? ("  (" + tab.RelativePath + ")")
                    : "";
                var item = CreateMenuItem(tab.FileName + relSuffix, "", () =>
                {
                    activeTab = tab;
                    editorControl.LoadBuffer(activeTab);
                    RefreshOutlineAndProblems(activeTab);
                    RefreshTabStrip();
                });
                item.Checked = (tab == activeTab);
                menu.Items.Add(item);
            }
            if (IsWelcomeTabOpen || openTabs.Count > 0)
            {
                menu.Items.Add(new ToolStripSeparator());
                menu.Items.Add(CreateMenuItem("Close All Editors", "Ctrl+K Ctrl+W", () => CloseAllTabs()));
            }
            if (closedTabPaths.Count > 0)
            {
                menu.Items.Add(new ToolStripSeparator());
                menu.Items.Add(CreateMenuItem("Reopen Closed Editor", "Ctrl+Shift+T", () => ReopenLastClosedTab()));
            }
            if (tabOverflowBtn != null)
                menu.Show(tabOverflowBtn, new Point(0, tabOverflowBtn.Height));
        }

        private void PersistOpenTabPaths()
        {
            settings.OpenTabPaths = openTabs.Where(t => !t.IsUntitled && !string.IsNullOrEmpty(t.FilePath)).Select(t => t.FilePath).ToList();
            settings.Save();
        }

        private static void ResolveTabBadge(string fileName, StudioTheme theme, out string badge, out Color color)
        {
            string ext = Path.GetExtension(fileName ?? "").ToLowerInvariant();
            if (ext == ".cs") { badge = "C#"; color = Color.FromArgb(168, 85, 247); }
            else if (ext == ".py") { badge = "PY"; color = Color.FromArgb(56, 189, 248); }
            else if (ext == ".js" || ext == ".mjs") { badge = "JS"; color = Color.FromArgb(250, 204, 21); }
            else if (ext == ".ts" || ext == ".tsx") { badge = "TS"; color = Color.FromArgb(59, 130, 246); }
            else if (ext == ".rs") { badge = "RS"; color = Color.FromArgb(251, 146, 60); }
            else if (ext == ".ps1") { badge = "PS"; color = Color.FromArgb(56, 189, 248); }
            else if (ext == ".sh") { badge = "SH"; color = Color.FromArgb(74, 222, 128); }
            else if (ext == ".json") { badge = "{}"; color = Color.FromArgb(250, 204, 21); }
            else if (ext == ".md") { badge = "MD"; color = Color.FromArgb(148, 163, 184); }
            else { badge = "≡"; color = theme != null ? theme.FgSecondary : Color.LightGray; }
        }

        private void ResizeOpenTabButtons()
        {
            if (tabStrip == null || tabStrip.Controls.Count == 0) return;
            int stripW = tabStrip.Width > 40 ? tabStrip.Width : (tabBarContainer != null ? Math.Max(220, tabBarContainer.Width - 174) : 600);
            int homeW = 0;
            bool hasWelcomeBtn = IsWelcomeTabOpen && tabStrip.Controls.Count > 0 && tabStrip.Controls[0].Name == "welcomeTabBtn";
            if (hasWelcomeBtn)
            {
                Control homeCtrl = tabStrip.Controls[0];
                bool compactTabs = stripW < 540 && openTabs.Count >= 2;
                homeCtrl.Width = compactTabs ? 104 : 124;
                homeW = homeCtrl.Width;
                homeCtrl.Invalidate();
            }

            int totalNatural = 0;
            List<Button> fileTabBtns = new List<Button>();
            for (int i = 0; i < tabStrip.Controls.Count; i++)
            {
                Button b = tabStrip.Controls[i] as Button;
                if (b != null && b.Tag is int)
                {
                    fileTabBtns.Add(b);
                    totalNatural += (int)b.Tag;
                }
            }
            if (fileTabBtns.Count == 0) return;

            int availForFiles = Math.Max(fileTabBtns.Count * 78, stripW - homeW - 36);
            if (totalNatural <= availForFiles)
            {
                foreach (Button b in fileTabBtns)
                {
                    int nw = (int)b.Tag;
                    if (b.Width != nw) { b.Width = nw; b.Invalidate(); }
                }
            }
            else
            {
                double scale = (double)availForFiles / Math.Max(1, totalNatural);
                int used = 0;
                for (int i = 0; i < fileTabBtns.Count; i++)
                {
                    Button b = fileTabBtns[i];
                    int nw = (int)b.Tag;
                    int scaled = (i == fileTabBtns.Count - 1)
                        ? Math.Max(78, availForFiles - used)
                        : Math.Max(78, (int)Math.Floor(nw * scale));
                    used += scaled;
                    if (b.Width != scaled) { b.Width = scaled; b.Invalidate(); }
                }
            }
        }

        public ContextMenuStrip BuildWelcomeTabContextMenu()
        {
            ContextMenuStrip tabCtx = CreateAutoDisposingContextMenu();
            tabCtx.Items.Add(CreateMenuItem("Close", "Ctrl+W", () => CloseWelcomeTab()));
            tabCtx.Items.Add(CreateMenuItem("Close Others", "", () =>
            {
                foreach (var other in openTabs.ToList())
                {
                    if (!CloseTabBuffer(other))
                        break;
                }
            }));
            tabCtx.Items.Add(CreateMenuItem("Close to the Right", "", () =>
            {
                foreach (var rightTab in openTabs.ToList())
                {
                    if (!CloseTabBuffer(rightTab))
                        break;
                }
            }));
            tabCtx.Items.Add(CreateMenuItem("Close All", "Ctrl+K Ctrl+W", () => CloseAllTabs()));
            return tabCtx;
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == 0x000F || m.Msg == 0x0317 || m.Msg == 0x0318)
            {
                Action pending = System.Threading.Interlocked.Exchange(ref pendingWorkspaceFinalizeUi, null);
                if (pending != null)
                {
                    try { pending(); } catch { }
                }
            }
            base.WndProc(ref m);
        }

        private void ApplyResponsiveWorkbenchWidths(int clientW)
        {
            if (clientW <= 400 || leftSidebar == null || rightDock == null) return;
            if (clientW <= 1120)
            {
                leftSidebar.Width = 180;
                rightDock.Width = 256;
            }
            else if (clientW <= 1320)
            {
                leftSidebar.Width = 212;
                rightDock.Width = 304;
            }
            else if (clientW <= 1420)
            {
                leftSidebar.Width = 228;
                rightDock.Width = 340;
            }
            else
            {
                leftSidebar.Width = 240;
                rightDock.Width = 384;
            }
            this.PerformLayout();
            if (centerContainer != null) centerContainer.PerformLayout();
            if (layoutBottomDockHeaderAction != null)
                layoutBottomDockHeaderAction();
        }

        private void RefreshTabStrip()
        {
            tabStrip.SuspendLayout();
            Control[] oldTabControls = new Control[tabStrip.Controls.Count];
            tabStrip.Controls.CopyTo(oldTabControls, 0);
            tabStrip.Controls.Clear();
            foreach (Control oldCtrl in oldTabControls)
            {
                if (oldCtrl != null)
                {
                    try { oldCtrl.Dispose(); } catch { }
                }
            }

            bool isWelcomeActive = (activeTab == null && IsWelcomeTabOpen);
            RefreshOutlineAndProblems(activeTab);

            if (IsWelcomeTabOpen)
            {
                Button homeBtn = new Button
                {
                    Name = "welcomeTabBtn",
                    Text = "",
                    Size = new Size(124, 34),
                    FlatStyle = FlatStyle.Flat,
                    BackColor = isWelcomeActive ? activeTheme.BgEditor : activeTheme.BgActivity,
                    ForeColor = isWelcomeActive ? activeTheme.FgPrimary : activeTheme.FgSecondary,
                    Font = TabTitleFont,
                    Cursor = Cursors.Hand,
                    Margin = new Padding(0)
                };
                homeBtn.FlatAppearance.BorderSize = 0;
                homeBtn.Paint += (s, e) =>
                {
                    Graphics g = e.Graphics;
                    using (SolidBrush bg = new SolidBrush(isWelcomeActive ? activeTheme.BgEditor : activeTheme.BgActivity))
                        g.FillRectangle(bg, homeBtn.ClientRectangle);

                    if (isWelcomeActive)
                    {
                        using (SolidBrush topBarBrush = new SolidBrush(activeTheme.AccentPrimary))
                            g.FillRectangle(topBarBrush, 0, 0, homeBtn.Width, 2);
                    }
                    else
                    {
                        using (Pen bp = new Pen(activeTheme.BorderSubtle, 1))
                            g.DrawLine(bp, 0, homeBtn.Height - 1, homeBtn.Width, homeBtn.Height - 1);
                    }
                    using (Pen rp = new Pen(activeTheme.BorderSubtle, 1))
                        g.DrawLine(rp, homeBtn.Width - 1, 0, homeBtn.Width - 1, homeBtn.Height);

                    CodeLessVectorIcons.DrawIcon(g, "home", new Rectangle(8, 9, 15, 15), activeTheme.AccentPrimary, 1.4f);
                    Rectangle titleRect = new Rectangle(26, 0, Math.Max(22, homeBtn.Width - 46), homeBtn.Height);
                    TextRenderer.DrawText(g, "Welcome", TabTitleFont, titleRect,
                        isWelcomeActive ? activeTheme.FgPrimary : activeTheme.FgSecondary,
                        TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix);
                    CodeLessVectorIcons.DrawIcon(g, "close", new Rectangle(homeBtn.Width - 20, 10, 13, 13), activeTheme.FgMuted, 1.4f);
                };
                homeBtn.MouseUp += (s, e) =>
                {
                    if (e.Button == MouseButtons.Right)
                    {
                        ContextMenuStrip wCtx = BuildWelcomeTabContextMenu();
                        wCtx.Show(homeBtn, e.Location);
                    }
                    else if (e.Button == MouseButtons.Middle || (e.Button == MouseButtons.Left && e.X > homeBtn.Width - 26))
                    {
                        CloseWelcomeTab();
                    }
                    else if (e.Button == MouseButtons.Left)
                    {
                        OpenWelcomeTab();
                    }
                };
                tabStrip.Controls.Add(homeBtn);
            }

            Dictionary<string, int> fileNameCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (var t in openTabs)
            {
                string fn = t.FileName ?? "";
                int cnt;
                fileNameCounts[fn] = (fileNameCounts.TryGetValue(fn, out cnt) ? cnt : 0) + 1;
            }

            foreach (EditorTabBuffer tab in openTabs)
            {
                EditorTabBuffer captured = tab;
                bool isCurrent = captured == activeTab;
                int pendingDiffs = captured.DiffHunks != null ? captured.DiffHunks.Count(h => h.IsPending) : 0;

                string badgeText;
                Color badgeCol;
                ResolveTabBadge(captured.FileName, activeTheme, out badgeText, out badgeCol);

                string title = captured.FileName ?? "Untitled";
                if (captured.IsDeletedOnDisk) title += " (deleted)";
                if (pendingDiffs > 0) title += " [+" + pendingDiffs + "]";

                string disambigFolder = "";
                int dupCount;
                if (fileNameCounts.TryGetValue(captured.FileName ?? "", out dupCount) && dupCount > 1 && !string.IsNullOrEmpty(captured.FilePath))
                {
                    string parentDir = Path.GetDirectoryName(captured.FilePath);
                    if (!string.IsNullOrEmpty(parentDir))
                        disambigFolder = Path.GetFileName(parentDir);
                }

                string measureLabel = string.IsNullOrEmpty(disambigFolder) ? title : (title + "  " + disambigFolder);
                int tabW = Math.Max(142, TextRenderer.MeasureText(measureLabel, TabTitleFont).Width + 68);

                Button tabBtn = new Button
                {
                    Text = "",
                    Tag = tabW,
                    Size = new Size(tabW, 34),
                    FlatStyle = FlatStyle.Flat,
                    BackColor = isCurrent ? activeTheme.BgEditor : activeTheme.BgActivity,
                    ForeColor = isCurrent ? activeTheme.FgPrimary : activeTheme.FgSecondary,
                    Cursor = Cursors.Hand,
                    Margin = new Padding(0)
                };
                tabBtn.FlatAppearance.BorderSize = 0;

                bool isCloseHovered = false;
                tabBtn.Paint += (s, e) =>
                {
                    Graphics g = e.Graphics;
                    using (SolidBrush bg = new SolidBrush(isCurrent ? activeTheme.BgEditor : activeTheme.BgActivity))
                        g.FillRectangle(bg, tabBtn.ClientRectangle);

                    if (isCurrent)
                    {
                        using (SolidBrush topBarBrush = new SolidBrush(activeTheme.AccentPrimary))
                            g.FillRectangle(topBarBrush, 0, 0, tabBtn.Width, 2);
                    }
                    else
                    {
                        using (Pen bp = new Pen(activeTheme.BorderSubtle, 1))
                            g.DrawLine(bp, 0, tabBtn.Height - 1, tabBtn.Width, tabBtn.Height - 1);
                    }

                    using (Pen rp = new Pen(activeTheme.BorderSubtle, 1))
                        g.DrawLine(rp, tabBtn.Width - 1, 0, tabBtn.Width - 1, tabBtn.Height);

                    Color diffTitleCol = activeTheme.IsLight ? Color.FromArgb(4, 120, 87) : Color.FromArgb(74, 222, 128);
                    TextRenderer.DrawText(g, badgeText, TabBadgeFont, new Point(8, 10), badgeCol);
                    Font activeTitleFont = captured.IsPreview ? TabItalicTitleFont : TabTitleFont;
                    Rectangle titleRect = new Rectangle(30, 0, Math.Max(22, tabBtn.Width - 48), tabBtn.Height);
                    Color titleFg = pendingDiffs > 0 ? diffTitleCol : (isCurrent ? activeTheme.FgPrimary : activeTheme.FgSecondary);
                    TextRenderer.DrawText(g, title, activeTitleFont, titleRect, titleFg,
                        TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix);

                    if (!string.IsNullOrEmpty(disambigFolder))
                    {
                        int titleW = TextRenderer.MeasureText(g, title, activeTitleFont, new Size(400, 34), TextFormatFlags.NoPadding).Width;
                        int subX = 30 + titleW + 6;
                        if (subX < tabBtn.Width - 36)
                        {
                            Rectangle subRect = new Rectangle(subX, 0, Math.Max(14, tabBtn.Width - 24 - subX), tabBtn.Height);
                            TextRenderer.DrawText(g, disambigFolder, TabSubtitleFont, subRect, activeTheme.FgMuted,
                                TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix);
                        }
                    }

                    Rectangle closeSlot = new Rectangle(tabBtn.Width - 20, 10, 13, 13);
                    if (captured.IsDirty && !isCloseHovered)
                    {
                        SmoothingMode prevSm = g.SmoothingMode;
                        g.SmoothingMode = SmoothingMode.AntiAlias;
                        using (SolidBrush dotBrush = new SolidBrush(isCurrent ? activeTheme.FgPrimary : activeTheme.FgSecondary))
                        {
                            g.FillEllipse(dotBrush, closeSlot.X + 2, closeSlot.Y + 2, 8, 8);
                        }
                        g.SmoothingMode = prevSm;
                    }
                    else if (captured.IsPinned && !isCloseHovered)
                    {
                        CodeLessVectorIcons.DrawIcon(g, "pin", closeSlot, activeTheme.AccentPrimary, 1.4f);
                    }
                    else
                    {
                        CodeLessVectorIcons.DrawIcon(g, "close", closeSlot, isCloseHovered ? activeTheme.FgPrimary : activeTheme.FgMuted, 1.4f);
                    }
                };

                tabBtn.MouseLeave += (s, e) =>
                {
                    if (isCloseHovered)
                    {
                        isCloseHovered = false;
                        tabBtn.Invalidate();
                    }
                };

                tabBtn.MouseDown += (s, e) =>
                {
                    if (e.Button == MouseButtons.Left && e.X <= tabBtn.Width - 26)
                        draggedTab = captured;
                };

                tabBtn.MouseMove += (s, e) =>
                {
                    bool hoverCloseNow = e.X > tabBtn.Width - 26;
                    if (hoverCloseNow != isCloseHovered)
                    {
                        isCloseHovered = hoverCloseNow;
                        tabBtn.Invalidate();
                    }
                    if (e.Button == MouseButtons.Left && draggedTab == captured && openTabs.Count > 1)
                    {
                        Point ptInStrip = tabStrip.PointToClient(tabBtn.PointToScreen(e.Location));
                        int fromIdx = openTabs.IndexOf(captured);
                        int fileBtnOffset = (IsWelcomeTabOpen && tabStrip.Controls.Count > 0 && tabStrip.Controls[0].Name == "welcomeTabBtn") ? 1 : 0;
                        for (int ci = fileBtnOffset; ci < tabStrip.Controls.Count; ci++)
                        {
                            Control other = tabStrip.Controls[ci];
                            if (other != tabBtn && other.Tag is int && other.Bounds.Contains(ptInStrip))
                            {
                                int toIdx = ci - fileBtnOffset;
                                if (fromIdx >= 0 && toIdx >= 0 && toIdx < openTabs.Count && fromIdx != toIdx)
                                {
                                    ReorderTab(fromIdx, toIdx);
                                    break;
                                }
                            }
                        }
                    }
                };

                tabBtn.DoubleClick += (s, e) =>
                {
                    if (captured.IsPreview)
                    {
                        captured.IsPreview = false;
                        tabBtn.Invalidate();
                    }
                };

                tabBtn.MouseUp += (s, e) =>
                {
                    draggedTab = null;
                    if (e.Button == MouseButtons.Right)
                    {
                        ContextMenuStrip tabCtx = BuildTabContextMenuForTab(captured);
                        tabCtx.Show(tabBtn, e.Location);
                    }
                    else if (e.Button == MouseButtons.Middle || (e.Button == MouseButtons.Left && e.X > tabBtn.Width - 26))
                    {
                        if (captured.IsPinned && e.Button == MouseButtons.Left)
                        {
                            captured.IsPinned = false;
                            RefreshTabStrip();
                        }
                        else
                        {
                            CloseTabBuffer(captured);
                        }
                    }
                    else if (e.Button == MouseButtons.Left)
                    {
                        activeTab = captured;
                        if (explorerTree != null) { explorerTree.ActiveEditorFilePath = captured.FilePath; explorerTree.Invalidate(); }
                        editorControl.LoadBuffer(activeTab);
                        RefreshOutlineAndProblems(activeTab);
                        RefreshTabStrip();
                        UpdateWindowTitle();
                    }
                };

                tabStrip.Controls.Add(tabBtn);
            }

            Button plusTabBtn = new Button
            {
                Text = "+",
                Size = new Size(32, 34),
                FlatStyle = FlatStyle.Flat,
                BackColor = activeTheme.BgActivity,
                ForeColor = activeTheme.FgSecondary,
                Font = TabPlusBtnFont,
                Cursor = Cursors.Hand,
                Margin = new Padding(0)
            };
            plusTabBtn.FlatAppearance.BorderSize = 0;
            plusTabBtn.Click += (s, e) =>
            {
                if (string.IsNullOrEmpty(workspaceRoot) || !Directory.Exists(workspaceRoot))
                    CreateNewUntitledTextTab();
                else
                    PromptCreateNewFile(workspaceRoot);
            };
            tabStrip.Controls.Add(plusTabBtn);

            ResizeOpenTabButtons();
            tabStrip.ResumeLayout();
            RefreshOpenEditorsSection();
            UpdateWindowTitle();
        }

        public bool SaveTabBuffer(EditorTabBuffer buf)
        {
            return SaveTabBuffer(buf, false);
        }

        // backgroundSave: autosave tick. Must not reload the editor (LoadBuffer hides the autocomplete popup and
        // resets the symbol dropdown every few seconds while the user is typing).
        public bool SaveTabBuffer(EditorTabBuffer buf, bool backgroundSave)
        {
            if (buf == null) return false;
            if (buf.IsUntitled || string.IsNullOrEmpty(buf.FilePath))
            {
                activeTab = buf;
                return PromptSaveAsDialog(buf);
            }
            try
            {
                internalSaveTimestamps[buf.FilePath] = DateTime.UtcNow;
                buf.WriteToDisk(buf.FilePath);
                buf.IsUntitled = false;
                buf.IsDeletedOnDisk = false;
                buf.IsPreview = false;
                if (!buf.IsLargeFile && buf.GitHeadContent == null && !string.IsNullOrEmpty(workspaceRoot))
                    buf.GitHeadContent = LlmHarnessAndAgentEngine.GetGitHeadFileContent(workspaceRoot, buf.FilePath);
                buf.RefreshGutterDiff();
                if (buf == activeTab)
                {
                    if (backgroundSave)
                        editorControl.RefreshAfterBackgroundSave();
                    else
                        editorControl.LoadBuffer(buf);
                }
                RefreshOutlineAndProblems(activeTab ?? buf);
                RefreshTabStrip();
                if (!string.IsNullOrEmpty(workspaceRoot))
                    RefreshGitSidebarStatus();
                UpdateWindowTitle();
                statusLeftLabel.Text = "Saved " + buf.FileName;
                return true;
            }
            catch (Exception ex)
            {
                if (backgroundSave)
                    statusLeftLabel.Text = "Auto Save failed for " + buf.FileName + ": " + ex.Message;
                else if (!SuppressUnsavedPrompts && Application.MessageLoop)
                    MessageBox.Show("Failed saving file: " + ex.Message, "codeLess IDE");
                return false;
            }
        }

        private async Task ExecuteActiveFileInTerminalAsync(EditorTabBuffer buf)
        {
            if (buf == null) return;
            if (!SaveTabBuffer(buf)) return;

            string ext = Path.GetExtension(buf.FilePath ?? "").ToLowerInvariant();
            string filePath = buf.FilePath ?? "";
            string runCmd;

            if (ext == ".py")
                runCmd = "python \"" + filePath + "\"";
            else if (ext == ".js" || ext == ".mjs" || ext == ".cjs")
                runCmd = "node \"" + filePath + "\"";
            else if (ext == ".ts" || ext == ".tsx")
                runCmd = "npx --yes tsx \"" + filePath + "\"";
            else if (ext == ".ps1")
                runCmd = "powershell -ExecutionPolicy Bypass -File \"" + filePath + "\"";
            else if (ext == ".cs")
            {
                string outExe = Path.Combine(Path.GetTempPath(), "codeless_run_" + Path.GetFileNameWithoutExtension(filePath) + ".exe");
                runCmd = "& \"C:\\Windows\\Microsoft.NET\\Framework64\\v4.0.30319\\csc.exe\" /nologo /out:\"" + outExe + "\" \"" + filePath + "\"; if ($LASTEXITCODE -eq 0) { & \"" + outExe + "\" }";
            }
            else if (ext == ".rs")
                runCmd = "cargo run";
            else if (ext == ".html" || ext == ".htm")
            {
                try { Process.Start(filePath); } catch { }
                return;
            }
            else
            {
                string extDisplay = string.IsNullOrEmpty(ext) ? buf.FileName : ext;
                ShowToastNotification("Cannot Run File", "No run configuration for " + extDisplay + ".");
                statusLeftLabel.Text = "No run configuration for " + extDisplay;
                return;
            }

            bottomDockPanel.Visible = true;
            SwitchBottomDockTab("terminal");
            await ExecuteTerminalCommandAsync(runCmd);
        }

        private bool updateCheckInFlight;

        private static string CurrentAppVersionString()
        {
            Version v = typeof(CodeLessMainForm).Assembly.GetName().Version;
            return v == null ? "0.0.0" : (v.Major + "." + v.Minor + "." + Math.Max(0, v.Build));
        }

        /// <summary>Automatic check: at most once per 24 h, silent unless a newer, non-skipped release exists.</summary>
        private void MaybeAutoCheckForUpdates()
        {
            if (settings == null || !settings.AutoCheckForUpdates || SuppressUnsavedPrompts) return;
            DateTime last;
            if (DateTime.TryParse(settings.LastUpdateCheckUtc, System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal, out last) &&
                (DateTime.UtcNow - last).TotalHours < 24)
                return;
            RunUpdateCheck(false);
        }

        /// <summary>Help > Check for Updates (interactive) or the background launch check.</summary>
        private void RunUpdateCheck(bool interactive)
        {
            if (updateCheckInFlight) return;
            updateCheckInFlight = true;
            string current = CurrentAppVersionString();
            if (interactive && statusLeftLabel != null) statusLeftLabel.Text = "Checking for updates...";
            Task.Run(() =>
            {
                UpdateCheckResult r = UpdateChecker.Check(current, 8000);
                Action show = () =>
                {
                    updateCheckInFlight = false;
                    if (r.Success)
                    {
                        settings.LastUpdateCheckUtc = DateTime.UtcNow.ToString("o", System.Globalization.CultureInfo.InvariantCulture);
                        try { settings.Save(); } catch { }
                    }
                    if (!r.Success)
                    {
                        if (interactive) MessageBox.Show(this, "Could not check for updates.\n\n" + r.Error, "Check for Updates", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                        return;
                    }
                    if (!r.IsNewer)
                    {
                        if (interactive) MessageBox.Show(this, "codeLess " + current + " is the latest version.", "Check for Updates", MessageBoxButtons.OK, MessageBoxIcon.Information);
                        return;
                    }
                    if (!interactive && string.Equals(settings.SkippedUpdateVersion, r.LatestVersion, StringComparison.OrdinalIgnoreCase))
                        return;
                    string notes = (r.ReleaseNotes ?? "").Trim();
                    if (notes.Length > 900) notes = notes.Substring(0, 900) + "\n...";
                    DialogResult choice = MessageBox.Show(this,
                        "codeLess " + r.LatestVersion + " is available - you have " + current + ".\n\n" +
                        (notes.Length > 0 ? notes + "\n\n" : "") +
                        "Yes = Download now      No = Remind me later      Cancel = Skip this version",
                        "Update Available", MessageBoxButtons.YesNoCancel, MessageBoxIcon.Information);
                    if (choice == DialogResult.Yes)
                    {
                        UpdateChecker.OpenInBrowser(r.DownloadUrl);
                        if (statusLeftLabel != null) statusLeftLabel.Text = "Downloading codeLess " + r.LatestVersion + " - close codeLess before installing.";
                    }
                    else if (choice == DialogResult.Cancel)
                    {
                        settings.SkippedUpdateVersion = r.LatestVersion;
                        try { settings.Save(); } catch { }
                    }
                };
                if (this.IsHandleCreated && !this.IsDisposed) this.BeginInvoke(show);
                else updateCheckInFlight = false;
            });
        }

        public string GetEffectiveTerminalWorkingDirectory()
        {
            if (!string.IsNullOrEmpty(workspaceRoot) && Directory.Exists(workspaceRoot))
                return workspaceRoot;
            return ConPtyTerminalSession.GetDefaultHomeDirectory();
        }

        public void SyncTerminalsToWorkingDirectory(string targetDir)
        {
            string resolved = !string.IsNullOrEmpty(targetDir) && Directory.Exists(targetDir)
                ? Path.GetFullPath(targetDir)
                : ConPtyTerminalSession.GetDefaultHomeDirectory();
            terminalWorkingDir = resolved;
            foreach (TerminalSessionState ts in terminalSessions)
            {
                if (ts == null) continue;
                ts.WorkingDir = resolved;
                if (ts.ConPtySession != null)
                {
                    ts.ConPtySession.UpdateWorkingDirectory(resolved);
                }
            }
            if (splitConPtyTerminalControl != null && splitConPtyTerminalControl.Session != null)
            {
                splitConPtyTerminalControl.Session.UpdateWorkingDirectory(resolved);
            }
        }

        public TerminalSessionState CreateNewTerminalSession()
        {
            return CreateNewTerminalSession(null, null);
        }

        public TerminalSessionState CreateNewTerminalSession(string explicitShellName, string explicitCmdLine)
        {
            return CreateNewTerminalSession(explicitShellName, explicitCmdLine, null);
        }

        /// <summary>Test hook: picks the folder for a new terminal in a multi-root workspace (null = cancel).</summary>
        public Func<List<string>, string> TerminalFolderPickerOverride;

        /// <summary>
        /// User-initiated "New Terminal": with two or more folders in the workspace, ask which folder the terminal
        /// should start in (VS Code behaviour); with a single folder, open it there directly.
        /// </summary>
        public void RequestNewTerminal(string explicitShellName, string explicitCmdLine)
        {
            List<string> roots = new List<string>();
            if (!string.IsNullOrEmpty(workspaceRoot) && Directory.Exists(workspaceRoot)) roots.Add(workspaceRoot);
            foreach (string extra in additionalWorkspaceFolders)
            {
                if (!string.IsNullOrEmpty(extra) && Directory.Exists(extra) && !roots.Contains(extra, StringComparer.OrdinalIgnoreCase))
                    roots.Add(extra);
            }
            if (roots.Count < 2)
            {
                CreateNewTerminalSession(explicitShellName, explicitCmdLine, null);
                return;
            }
            string pick = TerminalFolderPickerOverride != null
                ? TerminalFolderPickerOverride(roots)
                : ShowTerminalFolderPicker(roots);
            if (!string.IsNullOrEmpty(pick))
                CreateNewTerminalSession(explicitShellName, explicitCmdLine, pick);
        }

        private string ShowTerminalFolderPicker(List<string> roots)
        {
            using (Form dlg = new Form())
            {
                dlg.Text = "New Terminal — Select Folder";
                dlg.FormBorderStyle = FormBorderStyle.FixedDialog;
                dlg.StartPosition = FormStartPosition.CenterParent;
                dlg.MinimizeBox = false;
                dlg.MaximizeBox = false;
                dlg.ShowInTaskbar = false;
                dlg.KeyPreview = true;
                dlg.ClientSize = new Size(520, Math.Min(360, 70 + roots.Count * 40));
                Color bg = activeTheme != null ? activeTheme.BgSidebar : Color.FromArgb(30, 30, 34);
                Color fg = activeTheme != null ? activeTheme.FgPrimary : Color.White;
                Color fg2 = activeTheme != null ? activeTheme.FgSecondary : Color.Silver;
                dlg.BackColor = bg;
                dlg.ForeColor = fg;

                Label lbl = new Label { Text = "Select the folder for the new terminal:", Dock = DockStyle.Top, Height = 28, Padding = new Padding(10, 8, 10, 0), ForeColor = fg };
                ListBox list = new ListBox
                {
                    Dock = DockStyle.Fill,
                    BorderStyle = BorderStyle.None,
                    BackColor = bg,
                    ForeColor = fg,
                    DrawMode = DrawMode.OwnerDrawFixed,
                    ItemHeight = 40,
                    IntegralHeight = false
                };
                foreach (string r in roots) list.Items.Add(r);
                list.DrawItem += (s, e) =>
                {
                    if (e.Index < 0) return;
                    bool sel = (e.State & DrawItemState.Selected) != 0;
                    using (SolidBrush b = new SolidBrush(sel ? (activeTheme != null ? activeTheme.AccentPrimary : Color.SteelBlue) : bg))
                        e.Graphics.FillRectangle(b, e.Bounds);
                    string path = (string)list.Items[e.Index];
                    string name = Path.GetFileName(path.TrimEnd('\\', '/'));
                    if (string.IsNullOrEmpty(name)) name = path;
                    string prefix = e.Index < 9 ? (e.Index + 1) + "   " : "    ";
                    using (Font bold = new Font(list.Font, FontStyle.Bold))
                        TextRenderer.DrawText(e.Graphics, prefix + name, bold, new Rectangle(e.Bounds.X + 8, e.Bounds.Y + 3, e.Bounds.Width - 16, 18), sel ? Color.White : fg, TextFormatFlags.Left | TextFormatFlags.NoPrefix);
                    TextRenderer.DrawText(e.Graphics, path, list.Font, new Rectangle(e.Bounds.X + 30, e.Bounds.Y + 20, e.Bounds.Width - 38, 18), sel ? Color.White : fg2, TextFormatFlags.Left | TextFormatFlags.NoPrefix | TextFormatFlags.PathEllipsis);
                };
                list.SelectedIndex = 0;
                list.DoubleClick += (s, e) => { if (list.SelectedIndex >= 0) dlg.DialogResult = DialogResult.OK; };
                dlg.KeyDown += (s, e) =>
                {
                    if (e.KeyCode == Keys.Enter && list.SelectedIndex >= 0) { dlg.DialogResult = DialogResult.OK; e.Handled = true; }
                    else if (e.KeyCode == Keys.Escape) { dlg.DialogResult = DialogResult.Cancel; e.Handled = true; }
                    else
                    {
                        int idx = -1;
                        if (e.KeyCode >= Keys.D1 && e.KeyCode <= Keys.D9) idx = e.KeyCode - Keys.D1;
                        else if (e.KeyCode >= Keys.NumPad1 && e.KeyCode <= Keys.NumPad9) idx = e.KeyCode - Keys.NumPad1;
                        if (idx >= 0 && idx < list.Items.Count) { list.SelectedIndex = idx; dlg.DialogResult = DialogResult.OK; e.Handled = true; }
                    }
                };
                dlg.Controls.Add(list);
                dlg.Controls.Add(lbl);
                return dlg.ShowDialog(this) == DialogResult.OK && list.SelectedIndex >= 0 ? (string)list.Items[list.SelectedIndex] : null;
            }
        }

        public TerminalSessionState CreateNewTerminalSession(string explicitShellName, string explicitCmdLine, string workingDirOverride)
        {
            if (bottomDockPanel != null && !bottomDockPanel.Visible)
                bottomDockPanel.Visible = true;
            if (activeTerminalSessionIndex >= 0 && activeTerminalSessionIndex < terminalSessions.Count && terminalOutput != null)
            {
                terminalSessions[activeTerminalSessionIndex].Transcript = terminalOutput.Text;
                terminalSessions[activeTerminalSessionIndex].WorkingDir = terminalWorkingDir;
            }
            int num = 1;
            while (terminalSessions.Any(s => s != null && s.Title != null && s.Title.StartsWith(num + ":", StringComparison.OrdinalIgnoreCase)))
                num++;
            string sh = !string.IsNullOrEmpty(explicitShellName)
                ? explicitShellName
                : (shellSelectorCombo != null ? (shellSelectorCombo.Text ?? "PowerShell") : "PowerShell");
            // VS Code naming: "pwsh" for PowerShell 7+, "powershell" for Windows PowerShell 5.x
            string shortSh = (sh.IndexOf("pwsh", StringComparison.OrdinalIgnoreCase) >= 0 || sh.IndexOf("Core", StringComparison.OrdinalIgnoreCase) >= 0)
                ? "pwsh"
                : (sh.IndexOf("PowerShell", StringComparison.OrdinalIgnoreCase) >= 0 ? "powershell" : sh.ToLowerInvariant());
            string cwd = !string.IsNullOrEmpty(workingDirOverride) && Directory.Exists(workingDirOverride)
                ? workingDirOverride
                : GetEffectiveTerminalWorkingDirectory();
            terminalWorkingDir = cwd;
            ConPtyTerminalSession pty = new ConPtyTerminalSession(sh, explicitCmdLine, cwd, 100, 14, activeTheme != null && activeTheme.IsLight);
            TerminalSessionState session = new TerminalSessionState
            {
                Title = string.Format("{0}: {1}", num, shortSh),
                WorkingDir = cwd,
                Transcript = string.Format("[codeLess Terminal #{0} ({1}) — Ready]\n", num, sh),
                ConPtySession = pty
            };
            WireConPtySessionLifecycle(session);
            terminalSessions.Add(session);
            if (terminalSessionCombo != null)
            {
                terminalSessionCombo.Items.Add(session.Title);
                terminalSessionCombo.SelectedIndex = terminalSessions.Count - 1;
            }
            SwitchTerminalSession(terminalSessions.Count - 1);
            RefreshTerminalTabsListUi();
            return session;
        }

        public void SwitchTerminalSession(int index)
        {
            if (index < 0 || index >= terminalSessions.Count) return;
            if (activeTerminalSessionIndex >= 0 && activeTerminalSessionIndex < terminalSessions.Count && terminalOutput != null && activeTerminalSessionIndex != index)
            {
                terminalSessions[activeTerminalSessionIndex].Transcript = terminalOutput.Text;
                terminalSessions[activeTerminalSessionIndex].WorkingDir = terminalWorkingDir;
            }
            activeTerminalSessionIndex = index;
            TerminalSessionState target = terminalSessions[index];
            if (target.ConPtySession != null && !string.IsNullOrEmpty(target.ConPtySession.WorkingDirectory) && Directory.Exists(target.ConPtySession.WorkingDirectory))
                target.WorkingDir = target.ConPtySession.WorkingDirectory;
            if (!string.IsNullOrEmpty(target.WorkingDir) && Directory.Exists(target.WorkingDir))
                terminalWorkingDir = target.WorkingDir;
            else
                terminalWorkingDir = GetEffectiveTerminalWorkingDirectory();
            if (terminalOutput != null)
                terminalOutput.Text = target.Transcript ?? "";
            if (conPtyTerminalControl != null)
            {
                if (target.ConPtySession == null)
                {
                    string sh = shellSelectorCombo != null ? (shellSelectorCombo.Text ?? "PowerShell") : "PowerShell";
                    target.ConPtySession = new ConPtyTerminalSession(sh, null, terminalWorkingDir, 100, 14, activeTheme != null && activeTheme.IsLight);
                    WireConPtySessionLifecycle(target);
                }
                conPtyTerminalControl.Session = target.ConPtySession;
            }
            if (terminalSessionCombo != null && terminalSessionCombo.SelectedIndex != index && index < terminalSessionCombo.Items.Count)
                terminalSessionCombo.SelectedIndex = index;
            RefreshTerminalTabsListUi();
        }

        public void SwitchTerminalSessionByDelta(int delta)
        {
            if (terminalSessions == null || terminalSessions.Count <= 1) return;
            int next = (activeTerminalSessionIndex + delta) % terminalSessions.Count;
            if (next < 0) next += terminalSessions.Count;
            SwitchTerminalSession(next);
            if (conPtyTerminalControl != null && conPtyTerminalControl.Visible)
                conPtyTerminalControl.Focus();
        }

        public void KillTerminalSession(int index)
        {
            if (index < 0 || index >= terminalSessions.Count) return;
            TerminalSessionState target = terminalSessions[index];
            terminalSessions.RemoveAt(index);
            if (target != null)
            {
                if (target.ActiveProcess != null)
                {
                    try { if (!target.ActiveProcess.HasExited) KillProcessTreeById(target.ActiveProcess.Id); } catch { }
                    target.ActiveProcess = null;
                }
                if (target.ConPtySession != null)
                {
                    try { target.ConPtySession.Dispose(); } catch { }
                    target.ConPtySession = null;
                }
            }

            if (terminalSessions.Count == 0)
            {
                activeTerminalSessionIndex = -1;
                if (conPtyTerminalControl != null)
                    conPtyTerminalControl.Session = null;
                if (terminalOutput != null)
                    terminalOutput.Clear();
                RefreshTerminalTabsListUi();
                if (bottomDockPanel != null && bottomDockPanel.Visible)
                    bottomDockPanel.Visible = false;
                if (layoutBottomBtn != null)
                    layoutBottomBtn.Invalidate();
            }
            else
            {
                int nextIdx = Math.Min(index, terminalSessions.Count - 1);
                activeTerminalSessionIndex = -1;
                SwitchTerminalSession(nextIdx);
            }
        }

        public void KillActiveTerminalSession()
        {
            if (isTerminalSplit && splitConPtyTerminalControl != null && splitConPtyTerminalControl.Focused)
            {
                CloseSplitTerminalPane();
                return;
            }
            if (activeTerminalSessionIndex >= 0 && activeTerminalSessionIndex < terminalSessions.Count)
            {
                KillTerminalSession(activeTerminalSessionIndex);
            }
        }

        public void KillActiveTerminalProcess()
        {
            bool killedAny = false;
            if (conPtyTerminalControl != null && conPtyTerminalControl.Session != null && conPtyTerminalControl.Session.IsRunning)
            {
                try { conPtyTerminalControl.Session.WriteInput("\x03"); } catch { }
            }
            if (activeTerminalSessionIndex >= 0 && activeTerminalSessionIndex < terminalSessions.Count)
            {
                Process p = terminalSessions[activeTerminalSessionIndex].ActiveProcess;
                if (p != null)
                {
                    try
                    {
                        if (!p.HasExited)
                        {
                            KillProcessTreeById(p.Id);
                            killedAny = true;
                        }
                    }
                    catch { }
                    terminalSessions[activeTerminalSessionIndex].ActiveProcess = null;
                }
            }
            lock (activeChildProcessIds)
            {
                foreach (int pid in activeChildProcessIds.ToList())
                {
                    KillProcessTreeById(pid);
                    killedAny = true;
                }
                activeChildProcessIds.Clear();
            }
            if (terminalOutput != null)
            {
                terminalOutput.AppendText(killedAny ? "\n^C [Process terminated]\n" : "\n^C\n");
                if (activeTerminalSessionIndex >= 0 && activeTerminalSessionIndex < terminalSessions.Count)
                    terminalSessions[activeTerminalSessionIndex].Transcript = terminalOutput.Text;
            }
        }

        public void KillAllTerminalChildProcesses()
        {
            foreach (var s in terminalSessions)
            {
                if (s.ActiveProcess != null)
                {
                    try { if (!s.ActiveProcess.HasExited) KillProcessTreeById(s.ActiveProcess.Id); } catch { }
                    s.ActiveProcess = null;
                }
                if (s.ConPtySession != null)
                {
                    try { s.ConPtySession.Dispose(); } catch { }
                    s.ConPtySession = null;
                }
            }
            if (splitConPtyTerminalControl != null && splitConPtyTerminalControl.Session != null)
            {
                try { splitConPtyTerminalControl.Session.Dispose(); } catch { }
                splitConPtyTerminalControl.Session = null;
            }
            lock (activeChildProcessIds)
            {
                foreach (int pid in activeChildProcessIds.ToList())
                    KillProcessTreeById(pid);
                activeChildProcessIds.Clear();
            }
        }

        private static void KillProcessTreeById(int pid)
        {
            if (pid <= 0) return;
            try
            {
                ConPtyNative.KillProcessTreeById(pid);
            }
            catch { }
        }

        public async Task ExecuteTerminalCommandAsync(string cmd)
        {
            TerminalSessionState currentSession = (activeTerminalSessionIndex >= 0 && activeTerminalSessionIndex < terminalSessions.Count)
                ? terminalSessions[activeTerminalSessionIndex]
                : null;
            if (currentSession != null && !string.IsNullOrEmpty(currentSession.WorkingDir) && Directory.Exists(currentSession.WorkingDir))
                terminalWorkingDir = currentSession.WorkingDir;

            string cwd = !string.IsNullOrEmpty(terminalWorkingDir) && Directory.Exists(terminalWorkingDir)
                ? terminalWorkingDir
                : (!string.IsNullOrEmpty(workspaceRoot) && Directory.Exists(workspaceRoot) ? workspaceRoot : Environment.CurrentDirectory);

            // Also forward interactive command to the live ConPTY session (except background kill tests)
            if (conPtyTerminalControl != null && conPtyTerminalControl.Session != null && conPtyTerminalControl.Session.IsRunning &&
                !cmd.StartsWith("Start-Sleep", StringComparison.OrdinalIgnoreCase))
            {
                try { conPtyTerminalControl.Session.SendCommandLine(cmd); } catch { }
            }

            if (string.Equals(cmd, "cls", StringComparison.OrdinalIgnoreCase) || string.Equals(cmd, "clear", StringComparison.OrdinalIgnoreCase))
            {
                if (conPtyTerminalControl != null) conPtyTerminalControl.ClearTerminal();
                terminalOutput.Clear();
                if (currentSession != null) currentSession.Transcript = "";
                return;
            }

            terminalOutput.AppendText("\n[" + Path.GetFileName(cwd) + "] ❯ " + cmd + "\n");

            if (string.Equals(cmd, "pwd", StringComparison.OrdinalIgnoreCase))
            {
                terminalOutput.AppendText(cwd + "\n");
                if (currentSession != null) currentSession.Transcript = terminalOutput.Text;
                return;
            }

            if (cmd.StartsWith("cd ", StringComparison.OrdinalIgnoreCase) || string.Equals(cmd, "cd", StringComparison.OrdinalIgnoreCase))
            {
                string arg = cmd.Length > 2 ? cmd.Substring(2).Trim() : "";
                if (arg.StartsWith("/d ", StringComparison.OrdinalIgnoreCase))
                    arg = arg.Substring(3).Trim();
                arg = arg.Trim('"', '\'');
                if (arg == "~")
                    arg = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                if (string.IsNullOrEmpty(arg))
                {
                    terminalOutput.AppendText(cwd + "\n");
                    if (currentSession != null) currentSession.Transcript = terminalOutput.Text;
                    return;
                }
                try
                {
                    string nextDir = Path.IsPathRooted(arg) ? arg : Path.GetFullPath(Path.Combine(cwd, arg));
                    if (Directory.Exists(nextDir))
                    {
                        terminalWorkingDir = nextDir;
                        if (currentSession != null) currentSession.WorkingDir = nextDir;
                        terminalOutput.AppendText("Working directory changed to: " + terminalWorkingDir + "\n");
                        if (currentSession != null) currentSession.Transcript = terminalOutput.Text;
                        return;
                    }
                    else
                    {
                        terminalOutput.AppendText("Error: Directory does not exist: " + nextDir + "\n");
                        if (currentSession != null) currentSession.Transcript = terminalOutput.Text;
                        return;
                    }
                }
                catch (Exception ex)
                {
                    terminalOutput.AppendText("Error: " + ex.Message + "\n");
                    if (currentSession != null) currentSession.Transcript = terminalOutput.Text;
                    return;
                }
            }

            string selectedShell = shellSelectorCombo != null ? (shellSelectorCombo.Text ?? "PowerShell") : "PowerShell";
            await Task.Run(() =>
            {
                string exeName = "powershell.exe";
                string exeArgs = "-NoProfile -NonInteractive -Command \"" + cmd.Replace("\"", "\\\"") + "\"";
                if (string.Equals(selectedShell, "CMD.exe", StringComparison.OrdinalIgnoreCase) || selectedShell.IndexOf("Command Prompt", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    exeName = "cmd.exe";
                    exeArgs = "/c " + cmd;
                }
                else if (string.Equals(selectedShell, "Node.js", StringComparison.OrdinalIgnoreCase) &&
                         !cmd.StartsWith("node ", StringComparison.OrdinalIgnoreCase))
                {
                    exeName = "node";
                    exeArgs = "-e \"" + cmd.Replace("\"", "\\\"") + "\"";
                }
                else if (selectedShell.IndexOf("Python", StringComparison.OrdinalIgnoreCase) >= 0 &&
                         !cmd.StartsWith("python ", StringComparison.OrdinalIgnoreCase))
                {
                    exeName = "python";
                    exeArgs = "-c \"" + cmd.Replace("\"", "\\\"") + "\"";
                }

                StringBuilder sb = new StringBuilder();
                int procId = -1;
                try
                {
                    ProcessStartInfo psi = new ProcessStartInfo(exeName, exeArgs)
                    {
                        WorkingDirectory = Directory.Exists(cwd) ? cwd : Environment.CurrentDirectory,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        UseShellExecute = false,
                        CreateNoWindow = true,
                        StandardOutputEncoding = Encoding.UTF8,
                        StandardErrorEncoding = Encoding.UTF8
                    };
                    using (Process p = new Process { StartInfo = psi })
                    {
                        p.OutputDataReceived += (s, ev) => { if (ev.Data != null) lock (sb) sb.AppendLine(ev.Data); };
                        p.ErrorDataReceived += (s, ev) => { if (ev.Data != null) lock (sb) sb.AppendLine(ev.Data); };
                        p.Start();
                        procId = p.Id;
                        if (currentSession != null) currentSession.ActiveProcess = p;
                        lock (activeChildProcessIds) activeChildProcessIds.Add(procId);
                        p.BeginOutputReadLine();
                        p.BeginErrorReadLine();
                        if (!p.WaitForExit(20000))
                        {
                            KillProcessTreeById(procId);
                            lock (sb) sb.AppendLine("[Timed out after 20000ms]");
                        }
                        else
                        {
                            p.WaitForExit();
                        }
                    }
                }
                catch (Exception ex)
                {
                    lock (sb) sb.AppendLine("Process error: " + ex.Message);
                }
                finally
                {
                    if (currentSession != null) currentSession.ActiveProcess = null;
                    if (procId > 0)
                    {
                        lock (activeChildProcessIds) activeChildProcessIds.Remove(procId);
                    }
                }

                string output = LlmHarnessAndAgentEngine.StripAnsiCodes(sb.ToString().Trim());
                Action appendUi = () =>
                {
                    if (terminalOutput != null)
                    {
                        terminalOutput.AppendText((string.IsNullOrEmpty(output) ? "(completed with exit code 0)" : output) + "\n");
                        if (currentSession != null) currentSession.Transcript = terminalOutput.Text;
                    }
                };
                if (this.IsHandleCreated && this.InvokeRequired)
                    this.BeginInvoke(appendUi);
                else
                    appendUi();
            });
        }

        private async Task AutoHealLastTerminalErrorAsync()
        {
            if (activeTab == null)
            {
                statusLeftLabel.Text = "Open a file first to Auto-Heal terminal errors.";
                return;
            }

            string termText = (conPtyTerminalControl != null && conPtyTerminalControl.Session != null)
                ? conPtyTerminalControl.Session.GetRecentOutputText(80)
                : (terminalOutput != null ? terminalOutput.Text : "");
            string diagSummary = (activeTab.Diagnostics != null && activeTab.Diagnostics.Count > 0)
                ? string.Join("; ", activeTab.Diagnostics.Select(d => "L" + d.Line + ": " + d.Message))
                : "Inspect terminal stack trace";

            string instruction = "Auto-Heal terminal error & diagnostics (" + diagSummary + "): add defensive error handling and fix any crash.";
            await HandleCtrlKInlineEditAsync(instruction, activeTab);
        }

        public async Task HandleCtrlKInlineEditAsync(string instruction, EditorTabBuffer buf)
        {
            if (buf == null || string.IsNullOrWhiteSpace(instruction)) return;

            int vendorIdx = Math.Max(0, quickVendorCombo.SelectedIndex);
            ByokVendor vendor = aiEngine.Vendors[vendorIdx];
            if (!IsVendorConfigured(vendor))
            {
                if (agentFeedControl != null)
                    agentFeedControl.AddErrorCard("No AI provider configured", "No AI provider is configured for " + vendor.Name + ". Click 'Open AI Settings' to configure an API key or select a local Ollama endpoint.");
                ShowToastNotification("No AI provider configured", "Configure an API key or local Ollama endpoint to use inline AI edits.", "Configure...", () => OpenByokConfigurationDialog());
                statusLeftLabel.Text = "No AI provider configured";
                return;
            }

            string termLog = (conPtyTerminalControl != null && conPtyTerminalControl.Session != null)
                ? conPtyTerminalControl.Session.GetRecentOutputText(100)
                : (terminalOutput != null ? terminalOutput.Text : "");

            AgentExecutionTurnResult turn = await aiEngine.RunAutonomousAgentLoopAsync(
                "ctrl_k",
                instruction,
                vendor,
                settings.ActiveHarnessId,
                buf,
                workspaceRoot,
                workspaceFiles,
                attachedContextPills,
                termLog
            );

            ApplyStagedTurnEdits(turn, instruction);
            AppendTurnToChatAndTrace(turn, GetActiveModelDisplayName());
            statusLeftLabel.Text = "Updated " + buf.FileName;
        }

        public async Task SubmitAgentPromptAsync()
        {
            if (isAgentStreaming) return;
            string prompt = (promptInputBox.Text ?? "").Trim();
            if (string.IsNullOrEmpty(prompt)) return;
            promptInputBox.Clear();
            atMentionPopupFrame.Visible = false;
            lastUserQuery = prompt;

            string cpId = null;
            if (activeTab != null && !string.IsNullOrEmpty(activeTab.FilePath))
            {
                var cp = TimeTravelCheckpointManager.CreateDiskCheckpoint(activeTab.FilePath, activeTab.Content, "Pre-Prompt: " + prompt);
                if (cp != null) cpId = cp.Id;
            }

            if (agentFeedControl != null)
                agentFeedControl.AddMessage(true, "You", "", prompt, null, null, cpId);

            int vendorIdx = Math.Max(0, quickVendorCombo.SelectedIndex);
            ByokVendor vendor = aiEngine.Vendors[vendorIdx];
            if (!IsVendorConfigured(vendor))
            {
                if (agentFeedControl != null)
                    agentFeedControl.AddErrorCard("No AI provider configured", "No AI provider is configured for " + vendor.Name + ". Click 'Open AI Settings' to configure your API key or select a local Ollama endpoint.");
                ShowToastNotification("No AI provider configured", "Configure an API key or local Ollama endpoint to use AI chat.", "Configure...", () => OpenByokConfigurationDialog());
                statusLeftLabel.Text = "No AI provider configured";
                return;
            }

            isAgentStreaming = true;
            aiEngine.CancelStreamingRequested = false;
            sendPromptBtn.Enabled = false;
            sendPromptBtn.Visible = false;
            if (stopPromptBtn != null) stopPromptBtn.Visible = true;

            string activeModelName = GetActiveModelDisplayName();
            if (agentFeedControl != null)
                activeStreamingCard = agentFeedControl.BeginStreamingMessage(activeModelName, "");

            StringBuilder streamedAccum = new StringBuilder();
            Action<string> onChunk = (chunk) =>
            {
                if (string.IsNullOrEmpty(chunk)) return;
                lock (streamedAccum) streamedAccum.Append(chunk);
                string curText;
                lock (streamedAccum) curText = streamedAccum.ToString();
                Action updateUi = () =>
                {
                    if (agentFeedControl != null && activeStreamingCard != null)
                        agentFeedControl.UpdateStreamingMessage(activeStreamingCard, curText);
                };
                if (this.IsHandleCreated && this.InvokeRequired)
                    this.BeginInvoke(updateUi);
                else
                    updateUi();
            };

            aiEngine.OnStreamingChunk += onChunk;
            try
            {
                string termLog = (conPtyTerminalControl != null && conPtyTerminalControl.Session != null)
                    ? conPtyTerminalControl.Session.GetRecentOutputText(120)
                    : (terminalOutput != null ? terminalOutput.Text : "");

                AgentExecutionTurnResult turn = await aiEngine.RunAutonomousAgentLoopAsync(
                    currentAgentMode,
                    prompt,
                    vendor,
                    settings.ActiveHarnessId,
                    activeTab,
                    workspaceRoot,
                    workspaceFiles,
                    attachedContextPills,
                    termLog
                );

                Action completeOnUi = () =>
                {
                    ApplyStagedTurnEdits(turn, prompt);
                    AppendTurnToChatAndTrace(turn, activeModelName);
                    if (turn.AssistantReply != null &&
                        (turn.AssistantReply.StartsWith("Vendor API Error", StringComparison.OrdinalIgnoreCase) ||
                         turn.AssistantReply.StartsWith("⚠️ Vendor API Error", StringComparison.OrdinalIgnoreCase)) &&
                        agentFeedControl != null)
                    {
                        agentFeedControl.AddErrorCard("AI Provider Connection Error", turn.AssistantReply);
                    }
                    if (!string.IsNullOrEmpty(workspaceRoot))
                    {
                        bool noGit = string.IsNullOrEmpty(aiEngine.ActiveGitBranch) || string.Equals(aiEngine.ActiveGitBranch, "no-git", StringComparison.OrdinalIgnoreCase);
                        statusLeftLabel.Text = noGit
                            ? Path.GetFileName(workspaceRoot)
                            : string.Format("{0}  •  {1}", aiEngine.ActiveGitBranch, Path.GetFileName(workspaceRoot));
                    }
                    else
                    {
                        statusLeftLabel.Text = "No Folder Opened";
                    }
                };
                if (this.IsHandleCreated && this.InvokeRequired)
                    this.Invoke(completeOnUi);
                else
                    completeOnUi();
            }
            finally
            {
                aiEngine.OnStreamingChunk -= onChunk;
                Action cleanupOnUi = () =>
                {
                    isAgentStreaming = false;
                    activeStreamingCard = null;
                    if (stopPromptBtn != null) stopPromptBtn.Visible = false;
                    sendPromptBtn.Visible = true;
                    sendPromptBtn.Enabled = true;
                    sendPromptBtn.Text = "Send";
                };
                if (this.IsHandleCreated && this.InvokeRequired)
                    this.Invoke(cleanupOnUi);
                else
                    cleanupOnUi();
            }
        }

        private void AppendTurnToChatAndTrace(AgentExecutionTurnResult turn, string headerTitle)
        {
            List<CodeLessAgentFeedControl.AgentToolChip> chips = new List<CodeLessAgentFeedControl.AgentToolChip>();
            if (turn.ToolCalls != null)
            {
                foreach (var tc in turn.ToolCalls)
                {
                    chips.Add(new CodeLessAgentFeedControl.AgentToolChip
                    {
                        Name = tc.ToolName,
                        Summary = tc.Summary,
                        Output = tc.Output ?? "",
                        Success = tc.Success
                    });
                }
            }

            string metaBadge = "";
            if (agentFeedControl != null)
            {
                if (activeStreamingCard != null)
                {
                    agentFeedControl.EndStreamingMessage(
                        activeStreamingCard,
                        turn.AssistantReply ?? "",
                        turn.ThinkingProcess ?? "",
                        chips,
                        metaBadge
                    );
                    activeStreamingCard = null;
                }
                else
                {
                    agentFeedControl.AddMessage(
                        false,
                        headerTitle,
                        metaBadge,
                        turn.AssistantReply ?? "",
                        turn.ThinkingProcess ?? "",
                        chips
                    );
                }
            }

            if (turn.Trace != null && harnessTraceBox != null)
            {
                HarnessTraceEntry tr = turn.Trace;
                harnessTraceBox.AppendText(string.Format(
                    "\n[{0}] Mode={1} | Model={2} | Harness={3}\n  Tokens: {4} prompt + {5} completion | Latency: {6}ms\n  Tools: {7}\n",
                    tr.Timestamp, tr.Mode, tr.VendorAndModel, tr.HarnessProfileId,
                    tr.PromptTokens, tr.CompletionTokens, tr.ElapsedMs,
                    tr.ToolSummaries.Count > 0 ? string.Join(" -> ", tr.ToolSummaries) : "(none)"
                ));
            }
        }

        private void ApplyStagedTurnEdits(AgentExecutionTurnResult turn, string summaryLabel)
        {
            if (turn == null || turn.StagedFileContents == null || turn.StagedFileContents.Count == 0)
                return;

            foreach (var kvp in turn.StagedFileContents)
            {
                string filePath = kvp.Key;
                string newContent = kvp.Value;

                if (!File.Exists(filePath))
                {
                    try
                    {
                        string dir = Path.GetDirectoryName(filePath);
                        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                        File.WriteAllText(filePath, "");
                        AfterExplorerFileOperation(filePath);
                    }
                    catch { }
                }

                EditorTabBuffer tab = OpenFileInTab(filePath);
                if (tab != null)
                {
                    TimeTravelCheckpointManager.CreateDiskCheckpoint(tab.FilePath, tab.Content, "Pre-AI Edit: " + summaryLabel);
                    activeTab = tab;
                    editorControl.LoadBuffer(tab);
                    editorControl.StageProposedContent(newContent, summaryLabel);
                }
            }

            RefreshChangesetReviewBar();
            RefreshTabStrip();
        }

        private string SmartMergeSnippetIntoBuffer(EditorTabBuffer tab, string snippet)
        {
            string normOrig = (tab.Content ?? "").Replace("\r\n", "\n");
            bool origHadFinalNl = tab.HasFinalNewline;
            string finalNlSuffix = origHadFinalNl ? "\n" : "";
            string normSnip = (snippet ?? "").Replace("\r\n", "\n").TrimEnd();
            if (string.IsNullOrWhiteSpace(normOrig)) return normSnip + finalNlSuffix;

            // 1. If the user currently has a multi-character selection in the editor, replace that selection range
            if (tab.HasSelection())
            {
                int sL, sC, eL, eC;
                tab.GetNormalizedSelection(out sL, out sC, out eL, out eC);
                if (sL != eL || Math.Abs(eC - sC) > 2)
                {
                    List<string> copyLines = new List<string>(tab.Lines);
                    string prefix = copyLines[sL].Substring(0, Math.Min(copyLines[sL].Length, sC));
                    string suffix = copyLines[eL].Substring(Math.Min(copyLines[eL].Length, eC));
                    if (eL > sL) copyLines.RemoveRange(sL + 1, eL - sL);
                    copyLines[sL] = prefix + normSnip + suffix;
                    return string.Join("\n", copyLines);
                }
            }

            // 2. If snippet is already contained in normOrig or is at least 70% the length of normOrig, treat as full file or append
            string[] origLines = normOrig.Split('\n');
            string[] snipLines = normSnip.Split('\n');
            if (snipLines.Length >= Math.Max(4, (int)(origLines.Length * 0.75)))
            {
                return normSnip + finalNlSuffix;
            }

            // 3. If snippet defines a function/class that matches an existing AST symbol in tab, replace that symbol's span
            Match symMatch = Regex.Match(snipLines[0], @"(?:def|function|fn|class|struct|interface|void|int|string|bool|double|float|Task)\s+([A-Za-z_][A-Za-z0-9_]*)");
            if (symMatch.Success && tab.Symbols != null)
            {
                string symName = symMatch.Groups[1].Value;
                List<AstSymbolItem> candidates = tab.Symbols.Where(s => string.Equals(s.Name, symName, StringComparison.Ordinal)).ToList();
                AstSymbolItem existing = candidates.FirstOrDefault();
                if (candidates.Count > 1)
                {
                    int pOpen = snipLines[0].IndexOf('(');
                    int pClose = snipLines[0].LastIndexOf(')');
                    string snipParams = (pOpen >= 0 && pClose > pOpen) ? Regex.Replace(snipLines[0].Substring(pOpen, pClose - pOpen + 1), @"\s+", "") : "";
                    if (!string.IsNullOrEmpty(snipParams))
                    {
                        foreach (AstSymbolItem cand in candidates)
                        {
                            int cIdx = cand.Line - 1;
                            if (cIdx >= 0 && cIdx < origLines.Length)
                            {
                                string origHeader = Regex.Replace(origLines[cIdx], @"\s+", "");
                                if (origHeader.Contains(snipParams))
                                {
                                    existing = cand;
                                    break;
                                }
                            }
                        }
                    }
                }
                if (existing != null && existing.Line >= 1 && existing.Line <= origLines.Length)
                {
                    int startIdx = existing.Line - 1;
                    int endIdx = Math.Min(origLines.Length - 1, startIdx + Math.Max(1, existing.LineSpan) - 1);
                    string origLineStart = origLines[startIdx] ?? "";
                    int origLeadSpaces = origLineStart.Length - origLineStart.TrimStart().Length;
                    string snipLineStart = snipLines[0] ?? "";
                    int snipLeadSpaces = snipLineStart.Length - snipLineStart.TrimStart().Length;
                    int extraIndent = Math.Max(0, origLeadSpaces - snipLeadSpaces);
                    string pad = extraIndent > 0 ? new string(' ', extraIndent) : "";

                    List<string> updated = new List<string>();
                    for (int i = 0; i < startIdx; i++) updated.Add(origLines[i]);
                    for (int i = 0; i < snipLines.Length; i++)
                        updated.Add(string.IsNullOrWhiteSpace(snipLines[i]) ? "" : (pad + snipLines[i]));
                    for (int i = endIdx + 1; i < origLines.Length; i++) updated.Add(origLines[i]);
                    return string.Join("\n", updated);
                }
            }

            // 4. Otherwise append the snippet cleanly to the module (if not already present)
            if (normOrig.Contains(normSnip.Trim()))
                return normOrig;
            return normOrig.TrimEnd() + "\n\n" + normSnip + finalNlSuffix;
        }

        private void NavigateFromStreamLine(string rawLine)
        {
            if (string.IsNullOrWhiteSpace(rawLine)) return;

            // Match Python traceback: File "path", line 42
            Match pyMatch = Regex.Match(rawLine, @"File\s+""([^""]+)""\s*,\s*line\s+(\d+)", RegexOptions.IgnoreCase);
            if (pyMatch.Success)
            {
                string fp = LlmHarnessAndAgentEngine.ResolveWorkspacePath(workspaceRoot, pyMatch.Groups[1].Value);
                int ln;
                if (File.Exists(fp) && int.TryParse(pyMatch.Groups[2].Value, out ln))
                {
                    OpenFileInTab(fp);
                    editorControl.JumpToLine(ln);
                    return;
                }
            }

            // Match C# / MSBuild: path(42,10) or grep/compiler: path:42
            Match pathLineMatch = Regex.Match(rawLine, @"([A-Za-z0-9_./\\:-]+\.[A-Za-z0-9]+)(?:\((\d+)(?:,\d+)?\)|:(?:L|Ln\s*)?(\d+))");
            if (pathLineMatch.Success)
            {
                string cand = pathLineMatch.Groups[1].Value;
                string lnStr = pathLineMatch.Groups[2].Success ? pathLineMatch.Groups[2].Value : pathLineMatch.Groups[3].Value;
                string fp = LlmHarnessAndAgentEngine.ResolveWorkspacePath(workspaceRoot, cand);
                int ln;
                if (File.Exists(fp) && int.TryParse(lnStr, out ln))
                {
                    OpenFileInTab(fp);
                    editorControl.JumpToLine(ln);
                    statusLeftLabel.Text = string.Format("{0}:{1}", Path.GetFileName(fp), ln);
                }
            }
        }

        private void AppendChatMessage(string sender, string body)
        {
            if (agentFeedControl == null) return;
            bool isUser = sender != null && sender.StartsWith("YOU", StringComparison.OrdinalIgnoreCase);
            agentFeedControl.AddMessage(isUser, isUser ? "You" : sender, "", body);
        }

        private void RefreshExtensionsListUi()
        {
            if (extList == null) return;
            extList.Items.Clear();
            foreach (VsixExtensionPackage ext in vsixEngine.Extensions)
            {
                string badge = ext.IsInstalled ? "[INSTALLED] " : "[OPENVSX] ";
                extList.Items.Add(badge + ext.DisplayName + " (" + ext.Id + ")");
            }
            if (extList.Items.Count > 0)
                extList.SelectedIndex = 0;
        }

        private void PromptInstallVsixFromDisk()
        {
            using (OpenFileDialog ofd = new OpenFileDialog())
            {
                ofd.Title = "Select VS Code Extension (.vsix) Package to Install";
                ofd.Filter = "VS Code VSIX Extensions (*.vsix;*.zip)|*.vsix;*.zip|All Files (*.*)|*.*";
                if (ofd.ShowDialog(this) == DialogResult.OK)
                {
                    string status;
                    if (vsixEngine.InstallVsixArchiveFromDisk(ofd.FileName, themes, out status))
                    {
                        RefreshExtensionsListUi();
                        RefreshThemeComboItems();
                        statusLeftLabel.Text = status;
                    }
                    else
                    {
                        MessageBox.Show(status, "codeLess IDE");
                    }
                }
            }
        }

        private async Task SearchOpenVsxAsync(string query)
        {
            if (string.IsNullOrWhiteSpace(query) || query.StartsWith("Search OpenVSX"))
                query = "theme";

            statusLeftLabel.Text = "Searching OpenVSX…";
            int added = await vsixEngine.SearchOpenVsxRegistryAsync(query);
            RefreshExtensionsListUi();
            statusLeftLabel.Text = string.Format("Found {0} extension(s) for '{1}'", added, query);
        }

        private async Task HandleExtensionActionAsync()
        {
            int idx = extList.SelectedIndex;
            if (idx < 0 || idx >= vsixEngine.Extensions.Count) return;
            VsixExtensionPackage ext = vsixEngine.Extensions[idx];

            if (!ext.IsInstalled && !string.IsNullOrEmpty(ext.DownloadUrl))
            {
                extActionBtn.Enabled = false;
                extActionBtn.Text = "Downloading .VSIX...";
                bool ok = await vsixEngine.DownloadAndInstallFromOpenVsxAsync(ext, themes);
                extActionBtn.Enabled = true;
                extActionBtn.Text = "Install / Activate Selected VSIX";
                RefreshExtensionsListUi();
                RefreshThemeComboItems();
                if (!ok) return;
            }

            if (ext.ContributedThemes.Count > 0)
            {
                ApplyThemeToWorkbench(ext.ContributedThemes[0]);
                statusLeftLabel.Text = "Theme: " + ext.ContributedThemes[0].Name;
            }
            else
            {
                await vsixEngine.ActivateAndRunNodeExtensionHostAsync(ext);
                RunBuiltInComplexityAnalysis();
            }
        }

        private void RunBuiltInComplexityAnalysis()
        {
            if (activeTab == null)
            {
                return;
            }

            string text = activeTab.Content ?? "";
            int branches = Regex.Matches(text, @"\b(if|else\s+if|for|foreach|while|case|catch|\&\&|\|\|)\b").Count;
            int functions = activeTab.Symbols != null ? activeTab.Symbols.Count(s => s.Kind == "fn") : 0;
            int classes = activeTab.Symbols != null ? activeTab.Symbols.Count(s => s.Kind != "fn") : 0;
            int loc = activeTab.Lines.Count;
            int cyclomatic = 1 + branches;

            string grade = cyclomatic < 15 ? "A (Clean & Modular)" : cyclomatic < 35 ? "B (Moderate)" : "C (High Complexity)";
            string report = string.Format(
                "### AST Metrics & Complexity Audit\n" +
                "- **File:** `{0}` ({1} lines)\n" +
                "- **Types / Functions:** {2} classes • {3} functions\n" +
                "- **Cyclomatic Score:** `{4}` — Grade **{5}**",
                activeTab.RelativePath, loc, classes, functions, cyclomatic, grade
            );

            AppendChatMessage("VSIX HOST • AST METRICS", report);
        }

        private void OpenMultiHarnessArenaDialog()
        {
            int vendorIdx = quickVendorCombo != null ? Math.Max(0, quickVendorCombo.SelectedIndex) : 0;
            ByokVendor activeVendor = vendorIdx < aiEngine.Vendors.Count ? aiEngine.Vendors[vendorIdx] : aiEngine.Vendors[0];
            if (!IsVendorConfigured(activeVendor))
            {
                ShowToastNotification("No AI provider configured", "Configure an API key or local Ollama endpoint before launching Multi-Harness Arena.", "Configure...", () => OpenByokConfigurationDialog());
                return;
            }
            using (var dlg = new MultiHarnessArenaDialog(aiEngine, activeTab, activeVendor, activeTheme))
            {
                if (dlg.ShowDialog(this) == DialogResult.OK && !string.IsNullOrEmpty(dlg.WinningCodeToStage) && activeTab != null)
                {
                    TimeTravelCheckpointManager.CreateDiskCheckpoint(activeTab.FilePath, activeTab.Content, "Pre-Arena Winner: " + dlg.WinningHarnessLabel);
                    editorControl.StageProposedContent(dlg.WinningCodeToStage, "[Arena Winner: " + dlg.WinningHarnessLabel + "]");
                    RefreshTabStrip();
                }
            }
        }

        private void OpenArchitectureGraphDialog()
        {
            using (var dlg = new ArchitectureGraphDialog(aiEngine, workspaceFiles, workspaceRoot, activeTheme))
            {
                if (dlg.ShowDialog(this) == DialogResult.OK && !string.IsNullOrEmpty(dlg.SelectedFilePathToOpen))
                {
                    OpenFileInTab(dlg.SelectedFilePathToOpen);
                    if (dlg.SelectedLineToJump > 0)
                        editorControl.JumpToLine(dlg.SelectedLineToJump);
                }
            }
        }

        private void OpenTimeTravelCheckpointsDialog()
        {
            using (var dlg = new TimeTravelCheckpointDialog(activeTheme))
            {
                if (dlg.ShowDialog(this) == DialogResult.OK && dlg.SelectedCheckpointToRestore != null)
                {
                    var cp = dlg.SelectedCheckpointToRestore;
                    string restored = TimeTravelCheckpointManager.RestoreFromCheckpoint(cp);
                    if (restored != null)
                    {
                        EditorTabBuffer tab = OpenFileInTab(cp.OriginalFilePath);
                        if (tab != null)
                        {
                            tab.PushUndoSnapshot();
                            tab.Content = restored;
                            tab.SyncLinesFromContent();
                            tab.WriteToDisk(tab.FilePath);
                            tab.RefreshGutterDiff();
                            LlmHarnessAndAgentEngine.AnalyzeBufferAstAndDiagnostics(tab);
                            editorControl.LoadBuffer(tab);
                            RefreshOutlineAndProblems(tab);
                            RefreshTabStrip();
                            statusLeftLabel.Text = "Restored " + Path.GetFileName(cp.OriginalFilePath);
                        }
                    }
                }
            }
        }

        private void OpenRestApiPlaygroundDialog()
        {
            using (var dlg = new RestApiPlaygroundDialog(activeTheme))
            {
                dlg.ShowDialog(this);
            }
        }

        public void ExecutePaletteCommand(string cmdId)
        {
            if (string.IsNullOrEmpty(cmdId)) return;
            if (cmdId == "open_arena") OpenMultiHarnessArenaDialog();
            else if (cmdId == "open_arch_map") OpenArchitectureGraphDialog();
            else if (cmdId == "open_checkpoints") OpenTimeTravelCheckpointsDialog();
            else if (cmdId == "open_rest_client") OpenRestApiPlaygroundDialog();
            else if (cmdId == "auto_heal_terminal") { var _ = AutoHealLastTerminalErrorAsync(); }
            else if (cmdId == "toggle_sidebar") ToggleLeftSidebarVisibility();
            else if (cmdId == "toggle_right") ToggleRightDockVisibility();
            else if (cmdId == "toggle_bottom") ToggleBottomDockVisibility();
            else if (cmdId == "toggle_wrap") { editorControl.ToggleWordWrap(); UpdateTelemetryBadges(); }
            else if (cmdId == "open_byok") OpenByokConfigurationDialog();
            else if (cmdId == "open_settings") OpenSettingsDialog();
            else if (cmdId == "open_shortcuts") OpenKeyboardShortcutsDialog();
            else if (cmdId == "rename_symbol") PromptRenameSymbolInEditor();
            else if (cmdId == "format_doc") editorControl.FormatActiveDocument();
            else if (cmdId == "toggle_comment") editorControl.ToggleCommentActiveDocument();
            else if (cmdId == "toggle_block_comment") editorControl.ToggleBlockCommentActiveDocument();
            else if (cmdId == "reopen_closed_tab") ReopenLastClosedTab();
            else if (cmdId == "open_context")
            {
                using (var cd = new ContextInspectorDialog(aiEngine, activeTab, lastUserQuery, activeTheme))
                    cd.ShowDialog(this);
            }
            else if (cmdId == "ext_complexity") RunBuiltInComplexityAnalysis();
            else if (cmdId == "new_project") PromptNewProjectWizard();
            else if (cmdId == "open_folder") PromptOpenFolderDialog();
            else if (cmdId == "add_folder_workspace") PromptAddFolderToWorkspaceDialog();
            else if (cmdId == "clone_repo") PromptCloneRepositoryDialog();
            else if (cmdId == "open_file") PromptOpenFileDialog();
            else if (cmdId == "new_file") PromptCreateNewFile(workspaceRoot);
            else if (cmdId == "new_text_file") CreateNewUntitledTextTab();
            else if (cmdId == "new_window") { try { Process.Start(Application.ExecutablePath); } catch { } }
            else if (cmdId == "close_folder") CloseFolderWorkspace();
            else if (cmdId == "save_file") { if (activeTab != null) SaveTabBuffer(activeTab); }
            else if (cmdId == "save_as") PromptSaveAsDialog();
            else if (cmdId == "save_all") SaveAllDirtyTabs();
            else if (cmdId == "revert_file") RevertActiveFile();
            else if (cmdId == "close_editor")
            {
                if (activeTab != null) CloseTabBuffer(activeTab);
                else if (IsWelcomeTabOpen) CloseWelcomeTab();
            }
            else if (cmdId == "close_all_tabs") CloseAllTabs();
            else if (cmdId == "undo") editorControl.PerformUndo();
            else if (cmdId == "redo") editorControl.PerformRedo();
            else if (cmdId == "cut") editorControl.PerformCut();
            else if (cmdId == "copy") editorControl.PerformCopy();
            else if (cmdId == "paste") editorControl.PerformPaste();
            else if (cmdId == "find") editorControl.OpenFindBar(false);
            else if (cmdId == "replace") editorControl.OpenFindBar(true);
            else if (cmdId == "find_in_files")
            {
                leftSidebar.Visible = true;
                SwitchSidebarView("search");
                searchInput.Focus();
            }
            else if (cmdId == "replace_in_files")
            {
                leftSidebar.Visible = true;
                SwitchSidebarView("search");
                searchReplaceInput.Focus();
            }
            else if (cmdId == "select_all") editorControl.PerformSelectAll();
            else if (cmdId == "expand_selection") editorControl.ExpandSelection();
            else if (cmdId == "shrink_selection") editorControl.ShrinkSelection();
            else if (cmdId == "copy_line_up") editorControl.CopyLineUp();
            else if (cmdId == "copy_line_down") editorControl.CopyLineDown();
            else if (cmdId == "move_line_up") editorControl.MoveLineUp();
            else if (cmdId == "move_line_down") editorControl.MoveLineDown();
            else if (cmdId == "duplicate_selection") editorControl.DuplicateSelection();
            else if (cmdId == "add_cursor_above") editorControl.AddCursorAbove();
            else if (cmdId == "add_cursor_below") editorControl.AddCursorBelow();
            else if (cmdId == "add_next_occurrence") editorControl.TriggerAddNextOccurrenceCursor();
            else if (cmdId == "select_all_occurrences") editorControl.SelectAllOccurrences();
            else if (cmdId == "column_selection_mode") editorControl.ToggleColumnSelectionMode();
            else if (cmdId == "zen_mode") ToggleZenMode();
            else if (cmdId == "full_screen") ToggleFullScreenMode();
            else if (cmdId == "toggle_minimap")
            {
                settings.ShowMinimap = !settings.ShowMinimap;
                settings.Save();
                ApplySettingsToEditors();
            }
            else if (cmdId == "toggle_breadcrumbs")
            {
                bool nextBc = !editorControl.ShowBreadcrumbs;
                editorControl.ShowBreadcrumbs = nextBc;
                if (splitEditorControl != null) splitEditorControl.ShowBreadcrumbs = nextBc;
                if (settings != null) { settings.ShowBreadcrumbs = nextBc; settings.Save(); }
            }
            else if (cmdId == "zoom_in") { ZoomWorkbench(1.0f); }
            else if (cmdId == "zoom_out") { ZoomWorkbench(-1.0f); }
            else if (cmdId == "zoom_reset") { ZoomWorkbench(0f); }
            else if (cmdId == "go_back") NavigateBack();
            else if (cmdId == "go_forward") NavigateForward();
            else if (cmdId == "go_to_file") OpenCommandPalette("");
            else if (cmdId == "go_to_symbol_workspace") OpenCommandPalette("#");
            else if (cmdId == "go_to_symbol_editor") OpenCommandPalette("@");
            else if (cmdId == "go_to_definition")
            {
                if (activeTab != null) HandleGoToDefinition(activeTab.GetSelectedText());
            }
            else if (cmdId == "go_to_line") OpenCommandPalette(":");
            else if (cmdId == "go_to_bracket") editorControl.GoToMatchingBracket();
            else if (cmdId == "next_problem") JumpToProblemOffset(1);
            else if (cmdId == "prev_problem") JumpToProblemOffset(-1);
            else if (cmdId == "toggle_breakpoint")
            {
                if (activeTab != null)
                {
                    int ln = activeTab.CursorLine + 1;
                    if (!activeTab.Breakpoints.Remove(ln)) activeTab.Breakpoints.Add(ln);
                    editorControl.Invalidate(true);
                }
            }
            else if (cmdId == "new_terminal")
            {
                if (!bottomDockPanel.Visible) ToggleBottomDockVisibility();
                SwitchBottomDockTab("terminal");
                CreateNewTerminalSession();
            }
            else if (cmdId == "split_terminal")
            {
                if (!bottomDockPanel.Visible) ToggleBottomDockVisibility();
                SwitchBottomDockTab("terminal");
                ToggleSplitTerminalView();
            }
            else if (cmdId == "focus_next_terminal")
            {
                if (!bottomDockPanel.Visible) ToggleBottomDockVisibility();
                SwitchBottomDockTab("terminal");
                SwitchTerminalSessionByDelta(1);
            }
            else if (cmdId == "focus_prev_terminal")
            {
                if (!bottomDockPanel.Visible) ToggleBottomDockVisibility();
                SwitchBottomDockTab("terminal");
                SwitchTerminalSessionByDelta(-1);
            }
            else if (cmdId == "run_build_task") RunBuildTaskInTerminal();
            else if (cmdId == "run_selected_text")
            {
                if (activeTab != null)
                {
                    string sel = activeTab.GetSelectedText();
                    if (!string.IsNullOrWhiteSpace(sel))
                    {
                        if (!bottomDockPanel.Visible) ToggleBottomDockVisibility();
                        SwitchBottomDockTab("terminal");
                        if (conPtyTerminalControl != null && conPtyTerminalControl.ActiveSession != null && conPtyTerminalControl.ActiveSession.IsRunning)
                            conPtyTerminalControl.ActiveSession.WriteCommand(sel.Trim());
                        else
                        {
                            var _ = ExecuteTerminalCommandAsync(sel.Trim());
                        }
                    }
                }
            }
            else if (cmdId == "kill_terminal") KillActiveTerminalSession();
            else if (cmdId == "clear_terminal")
            {
                if (conPtyTerminalControl != null) conPtyTerminalControl.ClearTerminal();
                if (terminalOutput != null) terminalOutput.Clear();
            }
            else if (cmdId == "welcome" || cmdId == "help_welcome")
            {
                OpenWelcomeTab();
            }
            else if (cmdId == "dev_tools")
            {
                using (var d = new DeveloperToolsDialog(activeTheme, workspaceRoot, aiEngine != null && aiEngine.IndexedChunks != null ? aiEngine.IndexedChunks.Count : 0))
                    d.ShowDialog(this);
            }
            else if (cmdId == "about")
            {
                using (var a = new AboutDialog(activeTheme))
                    a.ShowDialog(this);
            }
            else if (cmdId == "split_editor") SplitEditorInDirection(DockStyle.Right, forceOpen: false);
            else if (cmdId == "split_editor_down") SplitEditorInDirection(DockStyle.Bottom, forceOpen: true);
            else if (cmdId == "install_vsix")
            {
                SwitchSidebarView("extensions");
                PromptInstallVsixFromDisk();
            }
            else if (cmdId == "run_file" && activeTab != null)
            {
                var _ = ExecuteActiveFileInTerminalAsync(activeTab);
            }
        }

        private void OpenCommandPalette(string initialPrefix = "")
        {
            List<WorkspaceSymbolLocation> wsSyms = aiEngine != null ? aiEngine.WorkspaceSymbolMap : null;
            using (var dlg = new CommandPaletteDialog(workspaceFiles, workspaceRoot, themes, aiEngine.Harnesses, activeTab, activeTheme, initialPrefix, wsSyms, null))
            {
                if (dlg.ShowDialog(this) == DialogResult.OK)
                {
                    HandleCommandPaletteResult(dlg);
                }
            }
        }

        public void OpenCommandPaletteWithRecentItems()
        {
            List<string> recents = new List<string>();
            if (settings != null)
            {
                if (settings.RecentWorkspaces != null)
                    recents.AddRange(settings.RecentWorkspaces.Where(p => !string.IsNullOrEmpty(p) && Directory.Exists(p)));
                if (settings.RecentFiles != null)
                    recents.AddRange(settings.RecentFiles.Where(p => !string.IsNullOrEmpty(p) && File.Exists(p)));
            }
            List<WorkspaceSymbolLocation> wsSyms = aiEngine != null ? aiEngine.WorkspaceSymbolMap : null;
            using (var dlg = new CommandPaletteDialog(workspaceFiles, workspaceRoot, themes, aiEngine.Harnesses, activeTab, activeTheme, "", wsSyms, recents))
            {
                if (dlg.ShowDialog(this) == DialogResult.OK)
                {
                    HandleCommandPaletteResult(dlg);
                }
            }
        }

        private void HandleCommandPaletteResult(CommandPaletteDialog dlg)
        {
            if (dlg == null) return;
            if (dlg.SelectedLineToJump > 0)
            {
                if (!string.IsNullOrEmpty(dlg.SelectedFilePath) && File.Exists(dlg.SelectedFilePath))
                    OpenFileInTab(dlg.SelectedFilePath);
                if (dlg.SelectedColumnToJump > 0)
                    editorControl.JumpToLineAndColumn(dlg.SelectedLineToJump, dlg.SelectedColumnToJump);
                else
                    editorControl.JumpToLine(dlg.SelectedLineToJump);
            }
            else if (!string.IsNullOrEmpty(dlg.SelectedFilePath))
            {
                if (Directory.Exists(dlg.SelectedFilePath))
                    LoadWorkspace(dlg.SelectedFilePath, restorePreviousTabs: false, keepCurrentTabs: false);
                else
                    OpenFileInTab(dlg.SelectedFilePath);
            }
            else if (dlg.SelectedTheme != null)
            {
                ApplyThemeToWorkbench(dlg.SelectedTheme);
                settings.ActiveThemeId = dlg.SelectedTheme.Id;
                settings.Save();
            }
            else if (!string.IsNullOrEmpty(dlg.SelectedCommandId))
            {
                ExecutePaletteCommand(dlg.SelectedCommandId);
            }
        }

        private bool? lastAppliedGlobalWordWrap = null;

        public bool SaveAllDirtyTabs()
        {
            foreach (var t in openTabs.ToList())
            {
                if (t != null && t.IsDirty)
                {
                    if (!SaveTabBuffer(t))
                        return false;
                }
            }
            return true;
        }

        public void ApplySettingsToEditors()
        {
            if (settings == null) return;
            foreach (var ctrl in new[] { editorControl, splitEditorControl })
            {
                if (ctrl == null) continue;
                ctrl.EditorFontFamily = string.IsNullOrWhiteSpace(settings.EditorFontFamily) ? "Consolas" : settings.EditorFontFamily;
                ctrl.EditorFontSize = settings.EditorFontSize <= 0 ? 10.2f : settings.EditorFontSize;
                ctrl.TabSize = settings.TabSize <= 0 ? 4 : settings.TabSize;
                ctrl.InsertSpaces = settings.InsertSpaces;
                ctrl.ShowMinimap = (ctrl == editorControl) ? settings.ShowMinimap : false;
                ctrl.ShowBreadcrumbs = settings.ShowBreadcrumbs;
                ctrl.EnableGhostCompletions = settings.EnableInlineGhostText;
            }
            if (!lastAppliedGlobalWordWrap.HasValue || lastAppliedGlobalWordWrap.Value != settings.WordWrap)
            {
                lastAppliedGlobalWordWrap = settings.WordWrap;
                foreach (var t in openTabs)
                    t.WordWrap = settings.WordWrap;
                if (activeTab != null)
                    activeTab.WordWrap = settings.WordWrap;
            }
            if (ghostToggleCheck != null && ghostToggleCheck.Checked != settings.EnableInlineGhostText)
                ghostToggleCheck.Checked = settings.EnableInlineGhostText;
            if (editorControl != null) editorControl.Invalidate(true);
            if (splitEditorControl != null) splitEditorControl.Invalidate(true);
            UpdateStatusBarCursorAndLanguage(activeTab);
            UpdateTelemetryBadges();
        }

        public void OpenSettingsDialog()
        {
            using (var dlg = new SettingsDialog(settings, themes, activeTheme, aiEngine != null ? aiEngine.Vendors : null, aiEngine))
            {
                dlg.OnSettingsLiveChanged = (liveSet) =>
                {
                    StudioTheme nextTheme = StudioTheme.ResolveTheme(themes, settings.ActiveThemeId);
                    if (nextTheme != activeTheme)
                    {
                        ApplyThemeToWorkbench(nextTheme);
                        RefreshThemeComboItems();
                    }
                    RefreshQuickVendorCombo();
                    ApplySettingsToEditors();
                };
                if (dlg.ShowDialog(this) == DialogResult.OK)
                {
                    StudioTheme nextTheme = StudioTheme.ResolveTheme(themes, settings.ActiveThemeId);
                    if (nextTheme != activeTheme)
                    {
                        ApplyThemeToWorkbench(nextTheme);
                        RefreshThemeComboItems();
                    }
                    RefreshQuickVendorCombo();
                    ApplySettingsToEditors();
                    statusLeftLabel.Text = "Saved settings";
                }
                else
                {
                    StudioTheme revertTheme = StudioTheme.ResolveTheme(themes, settings.ActiveThemeId);
                    if (revertTheme != activeTheme)
                    {
                        ApplyThemeToWorkbench(revertTheme);
                        RefreshThemeComboItems();
                    }
                    RefreshQuickVendorCombo();
                    ApplySettingsToEditors();
                }
            }
        }

        public void OpenKeyboardShortcutsDialog()
        {
            using (var dlg = new KeyboardShortcutsDialog(activeTheme))
            {
                dlg.ShowDialog(this);
            }
        }

        private void PromptGoToLineDialog()
        {
            OpenCommandPalette(":");
        }

        private string GetSelectedNodePath()
        {
            if (explorerTree.SelectedNode != null && explorerTree.SelectedNode.Tag is string)
                return (string)explorerTree.SelectedNode.Tag;
            return workspaceRoot;
        }

        private string GetSelectedExplorerDirectory()
        {
            string p = GetSelectedNodePath();
            if (string.IsNullOrEmpty(p)) return workspaceRoot;
            if (Directory.Exists(p)) return p;
            if (File.Exists(p)) return Path.GetDirectoryName(p);
            return workspaceRoot;
        }

        private void PromptCreateNewFile(string targetFolder)
        {
            string folder = !string.IsNullOrEmpty(targetFolder) && Directory.Exists(targetFolder)
                ? targetFolder
                : workspaceRoot;
            if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder))
            {
                CreateNewUntitledTextTab();
                return;
            }

            using (var dlg = new PromptInputDialog("Create New File", "Enter file name (e.g. app.py, server.mjs, Program.cs):", "", activeTheme))
            {
                if (dlg.ShowDialog(this) == DialogResult.OK)
                {
                    string inputName = (dlg.InputValue ?? "").Trim();
                    if (string.IsNullOrEmpty(inputName))
                    {
                        CreateNewUntitledTextTab();
                        return;
                    }
                    string newPath;
                    string nameErr = ValidateNewEntryName(folder, inputName, out newPath);
                    if (nameErr != null)
                    {
                        MessageBox.Show(this, nameErr, "Create New File", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                        return;
                    }
                    if (File.Exists(newPath) || Directory.Exists(newPath))
                    {
                        MessageBox.Show(this, "A file or folder '" + Path.GetFileName(newPath) + "' already exists at this location. Please choose a different name.", "Create New File", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                        return;
                    }
                    try
                    {
                        string dir = Path.GetDirectoryName(newPath);
                        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                        using (new FileStream(newPath, FileMode.CreateNew, FileAccess.Write)) { }
                        AfterExplorerFileOperation(newPath);
                        OpenFileInTab(newPath);
                    }
                    catch (Exception ex)
                    {
                        MessageBox.Show(this, "Could not create file: " + ex.Message, "codeLess IDE");
                    }
                }
            }
        }

        public void PromptCloneRepositoryDialog()
        {
            using (var dlg = new PromptInputDialog("Clone Repository", "Provide repository URL to clone:", "https://github.com/", activeTheme))
            {
                if (dlg.ShowDialog(this) != DialogResult.OK) return;
                string repoUrl = (dlg.InputValue ?? "").Trim();
                if (string.IsNullOrEmpty(repoUrl) || string.Equals(repoUrl, "https://github.com/", StringComparison.OrdinalIgnoreCase))
                    return;

                string destParent = VistaFolderPicker.ShowDialog(this.Handle, "Select Destination Parent Folder for Clone", workspaceRoot);
                if (string.IsNullOrEmpty(destParent) || !Directory.Exists(destParent))
                    return;

                string repoName = Path.GetFileNameWithoutExtension(repoUrl.TrimEnd('/', '\\'));
                if (string.IsNullOrWhiteSpace(repoName)) repoName = "cloned-repo";
                string targetDir = Path.Combine(destParent, repoName);

                statusLeftLabel.Text = "Cloning " + repoUrl + "…";
                Task.Run(() =>
                {
                    bool ok = false;
                    string err = "";
                    try
                    {
                        int cloneExit;
                        bool cloneTimedOut;
                        err = LlmHarnessAndAgentEngine.RunProcessCaptureWithExitCode(
                            "git",
                            "clone \"" + repoUrl + "\" \"" + targetDir + "\"",
                            destParent,
                            60000,
                            out cloneExit,
                            out cloneTimedOut);
                        ok = (!cloneTimedOut && cloneExit == 0 && Directory.Exists(targetDir));
                    }
                    catch (Exception ex)
                    {
                        err = ex.Message;
                    }
                    if (this.IsHandleCreated)
                    {
                        this.BeginInvoke(new Action(() =>
                        {
                            if (ok)
                                LoadWorkspace(targetDir, restorePreviousTabs: false, keepCurrentTabs: false);
                            else
                                MessageBox.Show(this, "Git clone failed:\n" + err, "Clone Repository", MessageBoxButtons.OK, MessageBoxIcon.Error);
                        }));
                    }
                });
            }
        }

        private void PromptCreateNewFolder(string targetFolder)
        {
            string folder = !string.IsNullOrEmpty(targetFolder) && Directory.Exists(targetFolder)
                ? targetFolder
                : workspaceRoot;
            if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder)) return;

            using (var dlg = new PromptInputDialog("Create New Folder", "Enter folder name:", "", activeTheme))
            {
                if (dlg.ShowDialog(this) == DialogResult.OK && !string.IsNullOrWhiteSpace(dlg.InputValue))
                {
                    string newDir;
                    string nameErr = ValidateNewEntryName(folder, dlg.InputValue, out newDir);
                    if (nameErr != null)
                    {
                        MessageBox.Show(this, nameErr, "Create New Folder", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                        return;
                    }
                    if (File.Exists(newDir) || Directory.Exists(newDir))
                    {
                        MessageBox.Show(this, "A file or folder '" + Path.GetFileName(newDir) + "' already exists at this location. Please choose a different name.", "Create New Folder", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                        return;
                    }
                    try
                    {
                        Directory.CreateDirectory(newDir);
                        AfterExplorerFileOperation(newDir);
                    }
                    catch (Exception ex)
                    {
                        MessageBox.Show(this, "Could not create folder: " + ex.Message, "codeLess IDE");
                    }
                }
            }
        }

        private void PromptRenameSelectedNode()
        {
            string path = GetSelectedNodePath();
            if (string.IsNullOrEmpty(path) || string.Equals(path, workspaceRoot, StringComparison.OrdinalIgnoreCase)) return;

            string oldName = Path.GetFileName(path);
            using (var dlg = new PromptInputDialog("Rename", "Enter new name for '" + oldName + "':", oldName, activeTheme))
            {
                if (dlg.ShowDialog(this) == DialogResult.OK && !string.IsNullOrWhiteSpace(dlg.InputValue) && dlg.InputValue != oldName)
                {
                    try
                    {
                        string newPath;
                        string nameErr = ValidateNewEntryName(Path.GetDirectoryName(path), dlg.InputValue, out newPath);
                        if (nameErr != null) { MessageBox.Show(this, nameErr, "Rename", MessageBoxButtons.OK, MessageBoxIcon.Warning); return; }
                        bool caseOnly = string.Equals(path, newPath, StringComparison.OrdinalIgnoreCase);
                        if (!caseOnly && (File.Exists(newPath) || Directory.Exists(newPath)))
                        {
                            MessageBox.Show(this, "A file or folder '" + Path.GetFileName(newPath) + "' already exists at this location.", "Rename", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                            return;
                        }
                        if (caseOnly)
                        {
                            bool wasDir = Directory.Exists(path);
                            MovePathCaseSafe(path, newPath);
                            RepathOpenTabsForFileOrDirectoryMove(path, newPath, wasDir);
                        }
                        else if (File.Exists(path))
                        {
                            string oldRel = LlmHarnessAndAgentEngine.GetRelativePath(workspaceRoot, path);
                            bool hadPill = attachedContextPills.Any(p => DoesPillMatchFile(p, path, oldName, oldRel));
                            RemoveContextPillsForFile(path, oldName, oldRel);
                            File.Move(path, newPath);
                            foreach (var tab in openTabs.Where(t => string.Equals(t.FilePath, path, StringComparison.OrdinalIgnoreCase)).ToList())
                            {
                                tab.FilePath = newPath;
                                tab.FileName = Path.GetFileName(newPath);
                                tab.RelativePath = LlmHarnessAndAgentEngine.GetRelativePath(workspaceRoot, newPath);
                            }
                            if (hadPill)
                                AddContextPill("@" + Path.GetFileName(newPath));
                        }
                        else if (Directory.Exists(path))
                        {
                            Directory.Move(path, newPath);
                            string oldPrefix = path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
                            string newPrefix = newPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
                            foreach (var tab in openTabs.ToList())
                            {
                                if (!string.IsNullOrEmpty(tab.FilePath) && tab.FilePath.StartsWith(oldPrefix, StringComparison.OrdinalIgnoreCase))
                                {
                                    tab.FilePath = newPrefix + tab.FilePath.Substring(oldPrefix.Length);
                                    tab.FileName = Path.GetFileName(tab.FilePath);
                                    tab.RelativePath = LlmHarnessAndAgentEngine.GetRelativePath(workspaceRoot, tab.FilePath);
                                }
                            }
                        }
                        AfterExplorerFileOperation(newPath);
                        RefreshTabStrip();
                    }
                    catch (Exception ex)
                    {
                        MessageBox.Show(this, "Rename failed: " + ex.Message, "codeLess IDE");
                    }
                }
            }
        }

        /// <summary>Rename that also works for case-only changes on case-insensitive NTFS ("foo" -> "Foo").</summary>
        private static void MovePathCaseSafe(string oldPath, string newPath)
        {
            bool isDir = Directory.Exists(oldPath);
            string tmp = oldPath.TrimEnd('\\') + ".codeless-rename-" + Guid.NewGuid().ToString("N").Substring(0, 6);
            if (isDir) { Directory.Move(oldPath, tmp); Directory.Move(tmp, newPath); }
            else { File.Move(oldPath, tmp); File.Move(tmp, newPath); }
        }

        private static void CopyDirectoryRecursive(string src, string dst)
        {
            Directory.CreateDirectory(dst);
            foreach (string f in Directory.GetFiles(src))
                File.Copy(f, Path.Combine(dst, Path.GetFileName(f)), false);
            foreach (string d in Directory.GetDirectories(src))
                CopyDirectoryRecursive(d, Path.Combine(dst, Path.GetFileName(d)));
        }

        /// <summary>Directory.Move throws across volumes; fall back to copy + delete.</summary>
        private static void MoveDirectoryAcrossVolumes(string src, string dst)
        {
            if (string.Equals(Path.GetPathRoot(src), Path.GetPathRoot(dst), StringComparison.OrdinalIgnoreCase))
            {
                Directory.Move(src, dst);
                return;
            }
            CopyDirectoryRecursive(src, dst);
            Directory.Delete(src, true);
        }

        private void PromptDeleteSelectedNode()
        {
            string path = GetSelectedNodePath();
            if (string.IsNullOrEmpty(path) || string.Equals(path, workspaceRoot, StringComparison.OrdinalIgnoreCase)) return;

            string name = Path.GetFileName(path);
            bool confirmed = DeleteConfirmOverride != null
                ? DeleteConfirmOverride(path)
                : (MessageBox.Show(this, "Are you sure you want to move '" + name + "' to the Recycle Bin?", "Confirm Move to Recycle Bin", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) == DialogResult.Yes);
            if (confirmed)
            {
                try
                {
                    if (File.Exists(path))
                    {
                        string delRel = LlmHarnessAndAgentEngine.GetRelativePath(workspaceRoot, path);
                        RemoveContextPillsForFile(path, name, delRel);
                        Microsoft.VisualBasic.FileIO.FileSystem.DeleteFile(
                            path,
                            Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs,
                            Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin
                        );
                        foreach (var t in openTabs.Where(x => string.Equals(x.FilePath, path, StringComparison.OrdinalIgnoreCase)).ToList())
                        {
                            if (t.IsDirty)
                                t.IsDeletedOnDisk = true;
                            else
                                openTabs.Remove(t);
                        }
                        PruneFileContextPillsWhenNoTabsOpen();
                        if (activeTab != null && !openTabs.Contains(activeTab))
                        {
                            activeTab = openTabs.FirstOrDefault();
                            editorControl.ShowWelcomePage = IsWelcomeTabOpen;
                            editorControl.LoadBuffer(activeTab);
                        }
                    }
                    else if (Directory.Exists(path))
                    {
                        string dirPrefix = path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
                        Microsoft.VisualBasic.FileIO.FileSystem.DeleteDirectory(
                            path,
                            Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs,
                            Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin
                        );
                        foreach (var t in openTabs.Where(x => !string.IsNullOrEmpty(x.FilePath) && x.FilePath.StartsWith(dirPrefix, StringComparison.OrdinalIgnoreCase)).ToList())
                        {
                            if (t.IsDirty)
                                t.IsDeletedOnDisk = true;
                            else
                                openTabs.Remove(t);
                        }
                        if (activeTab != null && !openTabs.Contains(activeTab))
                        {
                            activeTab = openTabs.FirstOrDefault();
                            editorControl.ShowWelcomePage = IsWelcomeTabOpen;
                            editorControl.LoadBuffer(activeTab);
                        }
                    }
                    AfterExplorerFileOperation(Path.GetDirectoryName(path));
                    RefreshTabStrip();
                }
                catch (Exception ex)
                {
                    MessageBox.Show(this, "Delete failed: " + ex.Message, "codeLess IDE");
                }
            }
        }

        public void PromptOpenFolderDialog()
        {
            string chosen = VistaFolderPicker.ShowDialog(this.Handle, "Select Folder to Open in codeLess", workspaceRoot);
            if (!string.IsNullOrEmpty(chosen) && Directory.Exists(chosen))
            {
                LoadWorkspace(chosen, restorePreviousTabs: false, keepCurrentTabs: false);
            }
        }

        private void PromptNewProjectWizard()
        {
            string parentDir = null;
            if (!string.IsNullOrEmpty(workspaceRoot) && Directory.GetParent(workspaceRoot) != null)
                parentDir = Directory.GetParent(workspaceRoot).FullName;

            using (var wiz = new NewProjectWizardDialog(parentDir, activeTheme))
            {
                if (wiz.ShowDialog(this) == DialogResult.OK && !string.IsNullOrEmpty(wiz.CreatedProjectPath))
                {
                    LoadWorkspace(wiz.CreatedProjectPath, restorePreviousTabs: false, keepCurrentTabs: false);
                    if (!string.IsNullOrEmpty(wiz.EntryFileToOpen) && File.Exists(wiz.EntryFileToOpen))
                    {
                        OpenFileInTab(wiz.EntryFileToOpen);
                    }
                }
            }
        }

        private void OpenByokConfigurationDialog()
        {
            using (var dlg = new ByokAndHarnessDialog(aiEngine, settings, activeTheme))
            {
                dlg.ShowDialog(this);
                RefreshQuickVendorCombo();
                UpdateTelemetryBadges();
            }
        }

        public static Color ResolveStatusBarBackColor(StudioTheme theme, bool hasFolderOpen)
        {
            if (theme == null)
                return hasFolderOpen ? Color.FromArgb(0, 122, 204) : Color.FromArgb(104, 33, 122);
            if (!hasFolderOpen)
                return StudioTheme.Blend(Color.FromArgb(104, 33, 122), theme.BgActivity, theme.IsLight ? 0.12 : 0.28);
            return StudioTheme.Blend(Color.FromArgb(0, 122, 204), theme.AccentPrimary, theme.IsLight ? 0.18 : 0.32);
        }

        public void ApplyThemeToWorkbench(StudioTheme theme)
        {
            activeTheme = theme;
            EnableNativeDarkTitleBar();
            this.BackColor = theme.BgWorkbench;
            this.ForeColor = theme.FgPrimary;
            this.OuterFrameColor = theme.BorderSubtle;

            if (mainMenuStrip != null)
            {
                mainMenuStrip.BackColor = theme.BgActivity;
                mainMenuStrip.ForeColor = theme.FgPrimary;
                foreach (ToolStripMenuItem topItem in mainMenuStrip.Items)
                {
                    topItem.ForeColor = theme.FgSecondary;
                    foreach (ToolStripItem sub in topItem.DropDownItems)
                    {
                        sub.ForeColor = theme.FgPrimary;
                        ToolStripMenuItem subMenu = sub as ToolStripMenuItem;
                        if (subMenu != null)
                        {
                            foreach (ToolStripItem sub2 in subMenu.DropDownItems)
                                sub2.ForeColor = theme.FgPrimary;
                        }
                    }
                }
            }
            topBar.BackColor = theme.BgActivity;
            if (brandLogoBtn != null) brandLogoBtn.Invalidate();
            if (cmdPaletteBtn != null)
            {
                cmdPaletteBtn.BackColor = theme.BgEditor;
                cmdPaletteBtn.ForeColor = theme.FgMuted;
                cmdPaletteBtn.FlatAppearance.BorderColor = theme.BorderSubtle;
            }
            if (ramTelemetryBadge != null) ramTelemetryBadge.ForeColor = theme.AccentSecondary;
            if (layoutLeftBtn != null) layoutLeftBtn.Invalidate();
            if (layoutBottomBtn != null) layoutBottomBtn.Invalidate();
            if (layoutRightBtn != null) layoutRightBtn.Invalidate();
            if (winMinBtn != null) { winMinBtn.BackColor = theme.BgActivity; winMinBtn.ForeColor = theme.FgSecondary; winMinBtn.Invalidate(); }
            if (winMaxBtn != null) { winMaxBtn.BackColor = theme.BgActivity; winMaxBtn.ForeColor = theme.FgSecondary; winMaxBtn.Invalidate(); }
            if (winCloseBtn != null) { winCloseBtn.BackColor = theme.BgActivity; winCloseBtn.ForeColor = theme.FgSecondary; winCloseBtn.Invalidate(); }

            bool hasFolderOpen = !string.IsNullOrEmpty(workspaceRoot) && Directory.Exists(workspaceRoot);
            statusBar.BackColor = ResolveStatusBarBackColor(theme, hasFolderOpen);
            Color statusFg = Color.White;
            statusLeftLabel.ForeColor = statusFg;
            if (statusProblemsLabel != null)
                statusProblemsLabel.ForeColor = (activeTab != null && activeTab.Diagnostics.Count > 0) ? Color.FromArgb(255, 220, 220) : statusFg;
            if (statusProblemsPanel != null)
                statusProblemsPanel.Invalidate();
            if (statusCursorLangLabel != null)
                statusCursorLangLabel.ForeColor = statusFg;
            if (statusIndentLabel != null)
                statusIndentLabel.ForeColor = statusFg;
            if (statusEolLabel != null)
                statusEolLabel.ForeColor = statusFg;
            if (statusLangLabel != null)
                statusLangLabel.ForeColor = statusFg;
            if (statusWrapLabel != null)
                statusWrapLabel.ForeColor = statusFg;
            if (statusLnColItem != null) statusLnColItem.ForeColor = statusFg;
            if (statusSpacesItem != null) statusSpacesItem.ForeColor = statusFg;
            if (statusEncodingItem != null) statusEncodingItem.ForeColor = statusFg;
            if (statusEolItem != null) statusEolItem.ForeColor = statusFg;
            if (statusLangItem != null) statusLangItem.ForeColor = statusFg;
            if (statusChordLabel != null) statusChordLabel.ForeColor = statusFg;
            statusRightLabel.ForeColor = statusFg;

            activityBar.BackColor = theme.BgActivity;
            leftSidebar.BackColor = theme.BgSidebar;
            sidebarHeaderLabel.BackColor = theme.BgActivity;
            sidebarHeaderLabel.ForeColor = theme.FgSecondary;
            if (explorerFolderHeaderLabel != null)
                explorerFolderHeaderLabel.ForeColor = theme.FgSecondary;

            explorerToolbar.BackColor = theme.BgSidebar;
            if (sidebarProgressBar != null) sidebarProgressBar.ApplyTheme(theme);
            if (searchProgressBar != null) searchProgressBar.ApplyTheme(theme);
            if (openEditorsSectionPanel != null) openEditorsSectionPanel.BackColor = theme.BgSidebar;
            if (openEditorsListBox != null) { openEditorsListBox.BackColor = theme.BgSidebar; openEditorsListBox.ApplyTheme(theme); }
            explorerTree.ApplyTheme(theme);
            outlineBottomPanel.BackColor = theme.BgActivity;
            outlineListBox.ApplyTheme(theme);

            if (leftSplitter != null) leftSplitter.BackColor = theme.BorderSubtle;
            if (rightSplitter != null) rightSplitter.BackColor = theme.BorderSubtle;
            if (bottomSplitter != null) bottomSplitter.BackColor = theme.BorderSubtle;
            if (outlineSplitter != null) outlineSplitter.BackColor = theme.BorderSubtle;
            if (terminalSplitSplitter != null) terminalSplitSplitter.BackColor = theme.BorderSubtle;

            searchInput.ApplyTheme(theme);
            searchReplaceInput.ApplyTheme(theme);
            searchResultsList.ApplyTheme(theme);

            gitBranchLabel.ForeColor = theme.AccentPrimary;
            gitCommitInput.ApplyTheme(theme);
            gitStatusList.ApplyTheme(theme);

            extSearchInput.ApplyTheme(theme);
            extList.ApplyTheme(theme);
            extDetailLabel.BackColor = theme.BgElevated;
            extDetailLabel.ForeColor = theme.FgSecondary;

            if (harnessListFrame != null) harnessListFrame.BackColor = theme.BorderSubtle;
            harnessStudioList.ApplyTheme(theme);
            harnessStudioDetailBox.BackColor = theme.BgSidebar;
            harnessStudioDetailBox.ForeColor = theme.FgPrimary;
            harnessStudioDetailBox.ApplyTheme(theme);
            ghostToggleCheck.ForeColor = theme.AccentPrimary;

            tabBarContainer.BackColor = theme.BgActivity;
            tabStrip.BackColor = theme.BgActivity;
            bool isLightTheme = (theme.IsLight || theme.BgEditor.GetBrightness() > 0.5f);
            Color termSurfaceBg = isLightTheme ? Color.FromArgb(255, 255, 255) : theme.BgWorkbench;
            Color termSurfaceFg = isLightTheme ? Color.FromArgb(59, 59, 59) : theme.FgPrimary;
            bottomDockPanel.BackColor = termSurfaceBg;
            if (dockHeaderBar != null)
            {
                dockHeaderBar.BackColor = theme.BgActivity;
                dockHeaderBar.Invalidate(true);
            }
            if (terminalSplitContainer != null)
                terminalSplitContainer.BackColor = termSurfaceBg;
            if (terminalTabsListPanel != null)
            {
                terminalTabsListPanel.BackColor = theme.BgSidebar;
                RefreshTerminalTabsListUi();
            }
            if (terminalSessions != null)
            {
                foreach (var ts in terminalSessions)
                {
                    if (ts != null && ts.ConPtySession != null && ts.ConPtySession.Buffer != null)
                        ts.ConPtySession.Buffer.ApplyThemePalette(isLightTheme, termSurfaceBg, termSurfaceFg);
                }
            }
            if (conPtyTerminalControl != null) conPtyTerminalControl.ApplyTheme(theme);
            if (splitConPtyTerminalControl != null) splitConPtyTerminalControl.ApplyTheme(theme);
            terminalOutput.BackColor = termSurfaceBg;
            terminalOutput.ForeColor = termSurfaceFg;
            terminalOutput.ApplyTheme(theme);
            terminalInput.ApplyTheme(theme);
            problemsListBox.ApplyTheme(theme);
            problemsListBox.BackColor = theme.BgWorkbench;
            outputLogBox.BackColor = theme.BgWorkbench;
            outputLogBox.ForeColor = theme.FgSecondary;
            outputLogBox.ApplyTheme(theme);
            harnessTraceBox.BackColor = theme.BgWorkbench;
            harnessTraceBox.ForeColor = theme.TokenFunction;
            harnessTraceBox.ApplyTheme(theme);
            extHostLogBox.BackColor = theme.BgWorkbench;
            extHostLogBox.ForeColor = theme.AccentSecondary;
            extHostLogBox.ApplyTheme(theme);

            rightDock.BackColor = theme.BgSidebar;
            multiFileChangesetBar.BackColor = StudioTheme.Blend(theme.BgSidebar, Color.FromArgb(16, 185, 129), 0.16);
            changesetSummaryLabel.ForeColor = theme.IsLight ? Color.FromArgb(4, 120, 87) : Color.FromArgb(110, 231, 183);
            if (agentFeedControl != null) agentFeedControl.ApplyTheme(theme);
            if (atMentionPopupFrame != null) atMentionPopupFrame.BackColor = theme.AccentPrimary;
            if (atMentionList != null) atMentionList.ApplyTheme(theme);
            if (composerCardPanel != null) composerCardPanel.BackColor = theme.BgEditor;
            if (contextPillsBar != null) contextPillsBar.BackColor = theme.BgEditor;
            promptInputBox.ApplyTheme(theme);
            quickVendorCombo.BackColor = theme.BgElevated;
            quickVendorCombo.ForeColor = theme.FgPrimary;
            quickVendorCombo.ApplyTheme(theme);
            quickHarnessCombo.BackColor = theme.BgElevated;
            quickHarnessCombo.ForeColor = theme.AccentPrimary;
            quickHarnessCombo.ApplyTheme(theme);
            if (themeCombo != null)
            {
                themeCombo.BackColor = theme.BgElevated;
                themeCombo.ForeColor = theme.FgPrimary;
                themeCombo.ApplyTheme(theme);
                int matchedThemeIdx = themes != null ? themes.FindIndex(t => t.Id == theme.Id) : -1;
                if (matchedThemeIdx >= 0 && themeCombo.SelectedIndex != matchedThemeIdx)
                    themeCombo.SelectedIndex = matchedThemeIdx;
            }
            shellSelectorCombo.BackColor = theme.BgElevated;
            shellSelectorCombo.ForeColor = theme.FgPrimary;
            shellSelectorCombo.ApplyTheme(theme);
            if (outputChannelCombo != null)
            {
                outputChannelCombo.BackColor = theme.BgElevated;
                outputChannelCombo.ForeColor = theme.FgPrimary;
                outputChannelCombo.ApplyTheme(theme);
            }
            if (terminalSessionCombo != null)
            {
                terminalSessionCombo.BackColor = theme.BgElevated;
                terminalSessionCombo.ForeColor = theme.FgPrimary;
                terminalSessionCombo.ApplyTheme(theme);
            }

            ApplyThemeToControlTree(this, theme);
            if (toastNotificationPanel != null)
            {
                toastNotificationPanel.BackColor = theme.BgElevated;
                if (toastTitleLabel != null) toastTitleLabel.ForeColor = theme.FgPrimary;
                if (toastMessageLabel != null) toastMessageLabel.ForeColor = theme.FgSecondary;
                if (toastActionBtn != null) toastActionBtn.BackColor = theme.AccentPrimary;
                toastNotificationPanel.Invalidate();
            }
            editorControl.ApplyTheme(theme);
            splitEditorControl.ApplyTheme(theme);
            activityBar.Invalidate(true);
            RefreshTabStrip();
        }

        private void ApplyThemeToControlTree(Control parent, StudioTheme theme)
        {
            foreach (Control c in parent.Controls)
            {
                if (c is Button)
                {
                    Button b = (Button)c;
                    string tag = b.Tag as string;
                    if (tag == "primary")
                    {
                        b.BackColor = theme.AccentPrimary;
                        b.ForeColor = Color.White;
                        b.FlatAppearance.BorderSize = 0;
                    }
                    else if (tag == "subtle")
                    {
                        b.BackColor = theme.BgElevated;
                        b.ForeColor = theme.FgPrimary;
                        b.FlatAppearance.BorderColor = theme.BorderSubtle;
                    }
                    else if (tag == "docktab_active")
                    {
                        b.BackColor = parent.BackColor;
                        b.ForeColor = theme.FgPrimary;
                        b.FlatAppearance.BorderSize = 0;
                    }
                    else if (tag == "docktab_inactive")
                    {
                        b.BackColor = parent.BackColor;
                        b.ForeColor = theme.FgSecondary;
                        b.FlatAppearance.BorderSize = 0;
                    }
                    else if (tag == "activity_icon" || tag == "layout_toggle")
                    {
                        b.BackColor = parent.BackColor;
                        b.FlatAppearance.BorderSize = 0;
                    }
                }
                if (c.HasChildren && !(c is CodeLessEditorControl) && !(c is CodeLessDarkInputBox))
                {
                    ApplyThemeToControlTree(c, theme);
                }
            }
        }

        private Panel toastNotificationPanel;
        public Panel ToastNotificationPanel { get { return toastNotificationPanel; } }
        private Label toastTitleLabel;
        private Label toastMessageLabel;
        private Button toastActionBtn;
        private System.Windows.Forms.Timer toastAutoHideTimer;

        public ContextMenuStrip BuildTabContextMenuForTab(EditorTabBuffer captured)
        {
            ContextMenuStrip tabCtx = CreateAutoDisposingContextMenu();
            tabCtx.Items.Add(CreateMenuItem("Close", "Ctrl+F4", () => CloseTabBuffer(captured)));
            tabCtx.Items.Add(CreateMenuItem("Close Others", "", () =>
            {
                foreach (var other in openTabs.Where(t => t != captured).ToList())
                {
                    if (!CloseTabBuffer(other))
                        return;
                }
                if (IsWelcomeTabOpen)
                    CloseWelcomeTab();
            }));
            tabCtx.Items.Add(CreateMenuItem("Close to the Right", "", () =>
            {
                int idx = openTabs.IndexOf(captured);
                if (idx >= 0)
                {
                    foreach (var rightTab in openTabs.Skip(idx + 1).ToList())
                    {
                        if (!CloseTabBuffer(rightTab))
                            return;
                    }
                }
            }));
            tabCtx.Items.Add(CreateMenuItem("Close Saved", "Ctrl+K U", () =>
            {
                foreach (var cleanTab in openTabs.Where(t => !t.IsDirty).ToList())
                {
                    if (!CloseTabBuffer(cleanTab))
                        return;
                }
            }));
            tabCtx.Items.Add(CreateMenuItem("Close All", "Ctrl+K Ctrl+W", () =>
            {
                CloseAllTabs();
            }));
            tabCtx.Items.Add(new ToolStripSeparator());
            tabCtx.Items.Add(CreateMenuItem("Copy Path", "Shift+Alt+C", () =>
            {
                if (captured != null && !string.IsNullOrEmpty(captured.FilePath)) CodeLessVirtualCanvas.SafeSetClipboardText(captured.FilePath);
            }));
            tabCtx.Items.Add(CreateMenuItem("Copy Relative Path", "Ctrl+K Ctrl+Shift+C", () =>
            {
                if (captured != null && !string.IsNullOrEmpty(captured.RelativePath)) CodeLessVirtualCanvas.SafeSetClipboardText(captured.RelativePath);
            }));
            tabCtx.Items.Add(new ToolStripSeparator());
            tabCtx.Items.Add(CreateMenuItem("Reveal in File Explorer", "Shift+Alt+R", () =>
            {
                if (captured != null && !string.IsNullOrEmpty(captured.FilePath) && File.Exists(captured.FilePath))
                    Process.Start("explorer.exe", "/select,\"" + captured.FilePath + "\"");
            }));
            tabCtx.Items.Add(CreateMenuItem("Reveal in Explorer View", "", () =>
            {
                if (captured != null && !string.IsNullOrEmpty(captured.FilePath))
                {
                    if (!leftSidebar.Visible) ToggleLeftSidebarVisibility();
                    SwitchSidebarView("explorer");
                    if (explorerTree != null)
                    {
                        explorerTree.ActiveEditorFilePath = captured.FilePath;
                        explorerTree.Invalidate();
                    }
                }
            }));
            tabCtx.Items.Add(new ToolStripSeparator());
            bool isPinned = captured != null && captured.IsPinned;
            tabCtx.Items.Add(CreateMenuItem(isPinned ? "Unpin" : "Pin", "Ctrl+K Shift+Enter", () =>
            {
                if (captured != null)
                {
                    captured.IsPinned = !captured.IsPinned;
                    captured.IsPreview = false;
                    if (captured.IsPinned)
                    {
                        int curIdx = openTabs.IndexOf(captured);
                        int targetIdx = 0;
                        while (targetIdx < openTabs.Count && openTabs[targetIdx] != captured && openTabs[targetIdx].IsPinned)
                            targetIdx++;
                        if (curIdx > targetIdx)
                            ReorderTab(curIdx, targetIdx);
                        else
                            RefreshTabStrip();
                    }
                    else
                    {
                        RefreshTabStrip();
                    }
                }
            }));
            var keepOpenItem = CreateMenuItem("Keep Open", "Ctrl+K Enter", () =>
            {
                if (captured != null)
                {
                    captured.IsPreview = false;
                    RefreshTabStrip();
                }
            });
            keepOpenItem.Enabled = (captured != null && captured.IsPreview);
            tabCtx.Items.Add(keepOpenItem);
            tabCtx.Items.Add(new ToolStripSeparator());
            tabCtx.Items.Add(CreateMenuItem("Open to the Side", "Ctrl+Enter", () =>
            {
                if (captured != null) activeTab = captured;
                SplitEditorInDirection(DockStyle.Right, forceOpen: true);
                if (splitEditorControl != null && captured != null) splitEditorControl.LoadBuffer(captured);
            }));
            tabCtx.Items.Add(CreateMenuItem("Split Up", "", () =>
            {
                if (captured != null) activeTab = captured;
                SplitEditorInDirection(DockStyle.Top, forceOpen: true);
                if (splitEditorControl != null && captured != null) splitEditorControl.LoadBuffer(captured);
            }));
            tabCtx.Items.Add(CreateMenuItem("Split Down", "", () =>
            {
                if (captured != null) activeTab = captured;
                SplitEditorInDirection(DockStyle.Bottom, forceOpen: true);
                if (splitEditorControl != null && captured != null) splitEditorControl.LoadBuffer(captured);
            }));
            tabCtx.Items.Add(CreateMenuItem("Split Left", "", () =>
            {
                if (captured != null) activeTab = captured;
                SplitEditorInDirection(DockStyle.Left, forceOpen: true);
                if (splitEditorControl != null && captured != null) splitEditorControl.LoadBuffer(captured);
            }));
            tabCtx.Items.Add(CreateMenuItem("Split Right", "Ctrl+\\", () =>
            {
                if (captured != null) activeTab = captured;
                SplitEditorInDirection(DockStyle.Right, forceOpen: true);
                if (splitEditorControl != null && captured != null) splitEditorControl.LoadBuffer(captured);
            }));
            return tabCtx;
        }

        public void ShowToastNotification(string title, string message, string actionText = null, Action onAction = null, int autoHideMs = 4500)
        {
            if (toastNotificationPanel == null)
            {
                toastNotificationPanel = new Panel
                {
                    Size = new Size(340, 82),
                    Visible = false,
                    Anchor = AnchorStyles.Bottom | AnchorStyles.Right
                };
                toastNotificationPanel.Paint += (s, e) =>
                {
                    Color borderCol = activeTheme != null ? activeTheme.AccentPrimary : Color.FromArgb(0, 122, 204);
                    using (Pen bp = new Pen(borderCol, 1f))
                        e.Graphics.DrawRectangle(bp, 0, 0, toastNotificationPanel.Width - 1, toastNotificationPanel.Height - 1);
                    using (SolidBrush stripe = new SolidBrush(borderCol))
                        e.Graphics.FillRectangle(stripe, 0, 0, 3, toastNotificationPanel.Height);
                };
                toastTitleLabel = new Label
                {
                    Location = new Point(12, 8),
                    Size = new Size(292, 18),
                    Font = new Font("Segoe UI Semibold", 8.8f),
                    Text = ""
                };
                Button closeToastBtn = new Button
                {
                    Text = "×",
                    Location = new Point(312, 4),
                    Size = new Size(22, 20),
                    FlatStyle = FlatStyle.Flat,
                    Font = new Font("Segoe UI", 8.0f),
                    Cursor = Cursors.Hand
                };
                closeToastBtn.FlatAppearance.BorderSize = 0;
                closeToastBtn.Click += (s, e) => HideToastNotification();
                toastMessageLabel = new Label
                {
                    Location = new Point(12, 28),
                    Size = new Size(316, 36),
                    Font = new Font("Segoe UI", 8.4f),
                    Text = ""
                };
                toastActionBtn = new Button
                {
                    Text = "Open AI Settings",
                    Location = new Point(12, 66),
                    Size = new Size(118, 22),
                    FlatStyle = FlatStyle.Flat,
                    Font = new Font("Segoe UI", 8.0f),
                    Cursor = Cursors.Hand,
                    Visible = false
                };
                toastActionBtn.FlatAppearance.BorderSize = 0;
                toastNotificationPanel.Controls.AddRange(new Control[] { toastTitleLabel, closeToastBtn, toastMessageLabel, toastActionBtn });
                this.Controls.Add(toastNotificationPanel);
            }

            StudioTheme t = activeTheme ?? StudioTheme.GetBuiltInThemes()[0];
            toastNotificationPanel.BackColor = t.BgElevated;
            toastTitleLabel.ForeColor = t.FgPrimary;
            toastMessageLabel.ForeColor = t.FgSecondary;
            toastTitleLabel.Text = (title ?? "Notification");
            toastMessageLabel.Text = message ?? "";

            if (!string.IsNullOrEmpty(actionText))
            {
                toastActionBtn.Text = actionText;
                toastActionBtn.BackColor = t.AccentPrimary;
                toastActionBtn.ForeColor = Color.White;
                toastActionBtn.Visible = true;
                toastNotificationPanel.Height = 96;
                EventHandler clickHandler = null;
                clickHandler = (s, e) =>
                {
                    toastActionBtn.Click -= clickHandler;
                    HideToastNotification();
                    if (onAction != null) onAction();
                };
                toastActionBtn.Click += clickHandler;
            }
            else
            {
                toastActionBtn.Visible = false;
                toastNotificationPanel.Height = 70;
            }

            toastNotificationPanel.Location = new Point(
                Math.Max(16, this.ClientSize.Width - toastNotificationPanel.Width - 18),
                Math.Max(40, this.ClientSize.Height - toastNotificationPanel.Height - 34)
            );
            toastNotificationPanel.Visible = true;
            toastNotificationPanel.BringToFront();

            if (toastAutoHideTimer == null)
            {
                toastAutoHideTimer = new System.Windows.Forms.Timer();
                toastAutoHideTimer.Tick += (s, e) =>
                {
                    toastAutoHideTimer.Stop();
                    HideToastNotification();
                };
            }
            toastAutoHideTimer.Stop();
            if (autoHideMs > 0)
            {
                toastAutoHideTimer.Interval = autoHideMs;
                toastAutoHideTimer.Start();
            }
        }

        public void HideToastNotification()
        {
            if (toastAutoHideTimer != null) toastAutoHideTimer.Stop();
            if (toastNotificationPanel != null) toastNotificationPanel.Visible = false;
        }
    }

    public class DarkStudioMenuRenderer : ToolStripProfessionalRenderer
    {
        private readonly Func<StudioTheme> themeGetter;

        public DarkStudioMenuRenderer() : this(null) { }

        public DarkStudioMenuRenderer(Func<StudioTheme> themeGetter)
            : base(new DarkMenuColorTable(themeGetter))
        {
            this.themeGetter = themeGetter;
            this.RoundedEdges = false;
        }

        protected override void OnRenderToolStripBackground(ToolStripRenderEventArgs e)
        {
            StudioTheme t = themeGetter != null ? themeGetter() : null;
            if (e.ToolStrip is ToolStripDropDown)
            {
                Color bg = t != null ? t.BgElevated : Color.FromArgb(24, 27, 36);
                using (SolidBrush b = new SolidBrush(bg))
                {
                    e.Graphics.FillRectangle(b, e.AffectedBounds);
                }
            }
            else
            {
                base.OnRenderToolStripBackground(e);
            }
        }

        protected override void OnRenderToolStripBorder(ToolStripRenderEventArgs e)
        {
            StudioTheme t = themeGetter != null ? themeGetter() : null;
            if (e.ToolStrip is ToolStripDropDown)
            {
                Color border = t != null ? t.BorderSubtle : Color.FromArgb(48, 54, 68);
                using (Pen p = new Pen(border, 1f))
                {
                    e.Graphics.DrawRectangle(p, 0, 0, e.ToolStrip.Width - 1, e.ToolStrip.Height - 1);
                }
            }
        }

        protected override void OnRenderMenuItemBackground(ToolStripItemRenderEventArgs e)
        {
            StudioTheme t = themeGetter != null ? themeGetter() : null;
            if (!e.Item.Selected && !e.Item.Pressed)
                return;

            Rectangle rc = new Rectangle(3, 1, e.Item.Width - 6, e.Item.Height - 2);
            if (e.ToolStrip is MenuStrip)
            {
                Color topHover = t != null ? t.BgElevated : Color.FromArgb(32, 37, 48);
                using (SolidBrush b = new SolidBrush(topHover))
                {
                    e.Graphics.FillRectangle(b, new Rectangle(1, 2, e.Item.Width - 2, e.Item.Height - 4));
                }
            }
            else
            {
                Color accentHover = t != null
                    ? StudioTheme.Blend(t.BgElevated, t.AccentPrimary, 0.28)
                    : Color.FromArgb(9, 71, 113);
                using (SolidBrush b = new SolidBrush(accentHover))
                {
                    e.Graphics.FillRectangle(b, rc);
                }
            }
        }

        protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
        {
            StudioTheme t = themeGetter != null ? themeGetter() : null;
            bool isShortcut = e.Text != e.Item.Text;
            if (!e.Item.Enabled)
            {
                e.TextColor = t != null ? t.FgMuted : Color.FromArgb(95, 104, 122);
            }
            else if (isShortcut)
            {
                e.TextColor = e.Item.Selected
                    ? (t != null ? t.FgPrimary : Color.FromArgb(228, 232, 240))
                    : (t != null ? t.FgMuted : Color.FromArgb(132, 142, 162));
            }
            else
            {
                e.TextColor = t != null ? t.FgPrimary : Color.FromArgb(228, 232, 240);
            }
            base.OnRenderItemText(e);
        }

        protected override void OnRenderItemCheck(ToolStripItemImageRenderEventArgs e)
        {
            StudioTheme t = themeGetter != null ? themeGetter() : null;
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            Color checkCol = t != null ? t.AccentPrimary : Color.FromArgb(56, 189, 248);
            int cx = e.ImageRectangle.X + 4;
            int cy = e.ImageRectangle.Y + e.ImageRectangle.Height / 2;
            using (Pen p = new Pen(checkCol, 1.8f))
            {
                e.Graphics.DrawLines(p, new[]
                {
                    new Point(cx, cy),
                    new Point(cx + 3, cy + 3),
                    new Point(cx + 9, cy - 4)
                });
            }
        }

        protected override void OnRenderArrow(ToolStripArrowRenderEventArgs e)
        {
            StudioTheme t = themeGetter != null ? themeGetter() : null;
            e.ArrowColor = e.Item.Selected
                ? (t != null ? t.FgPrimary : Color.White)
                : (t != null ? t.FgSecondary : Color.FromArgb(160, 170, 188));
            base.OnRenderArrow(e);
        }

        protected override void OnRenderSeparator(ToolStripSeparatorRenderEventArgs e)
        {
            StudioTheme t = themeGetter != null ? themeGetter() : null;
            Color sep = t != null ? t.BorderSubtle : Color.FromArgb(45, 52, 68);
            int y = e.Item.Height / 2;
            using (Pen p = new Pen(sep, 1f))
            {
                e.Graphics.DrawLine(p, 8, y, e.Item.Width - 8, y);
            }
        }
    }

    public class DarkMenuColorTable : ProfessionalColorTable
    {
        private readonly Func<StudioTheme> themeGetter;

        public DarkMenuColorTable(Func<StudioTheme> themeGetter)
        {
            this.themeGetter = themeGetter;
        }

        private StudioTheme T { get { return themeGetter != null ? themeGetter() : null; } }

        public override Color ToolStripDropDownBackground { get { return T != null ? T.BgElevated : Color.FromArgb(24, 27, 36); } }
        public override Color ImageMarginGradientBegin { get { return T != null ? T.BgElevated : Color.FromArgb(24, 27, 36); } }
        public override Color ImageMarginGradientMiddle { get { return T != null ? T.BgElevated : Color.FromArgb(24, 27, 36); } }
        public override Color ImageMarginGradientEnd { get { return T != null ? T.BgElevated : Color.FromArgb(24, 27, 36); } }
        public override Color MenuBorder { get { return T != null ? T.BorderSubtle : Color.FromArgb(48, 54, 68); } }
        public override Color MenuItemBorder { get { return Color.Transparent; } }
        public override Color MenuItemSelected { get { return T != null ? T.BgSelection : Color.FromArgb(30, 41, 59); } }
        public override Color MenuStripGradientBegin { get { return T != null ? T.BgActivity : Color.FromArgb(14, 16, 22); } }
        public override Color MenuStripGradientEnd { get { return T != null ? T.BgActivity : Color.FromArgb(14, 16, 22); } }
        public override Color MenuItemSelectedGradientBegin { get { return T != null ? T.BgSelection : Color.FromArgb(30, 41, 59); } }
        public override Color MenuItemSelectedGradientEnd { get { return T != null ? T.BgSelection : Color.FromArgb(30, 41, 59); } }
        public override Color MenuItemPressedGradientBegin { get { return T != null ? T.BgEditor : Color.FromArgb(24, 32, 48); } }
        public override Color MenuItemPressedGradientEnd { get { return T != null ? T.BgEditor : Color.FromArgb(24, 32, 48); } }
        public override Color SeparatorDark { get { return T != null ? T.BorderSubtle : Color.FromArgb(45, 52, 68); } }
        public override Color SeparatorLight { get { return T != null ? T.BorderSubtle : Color.FromArgb(45, 52, 68); } }
    }

    public static class Program
    {
        // Portable mode: codeLess.Core.dll is embedded as a resource so codeLess.exe runs
        // standalone (e.g. copied alone to Downloads). A side-by-side DLL still wins via normal probing.
        static Program()
        {
            AppDomain.CurrentDomain.AssemblyResolve += ResolveEmbeddedAssembly;
        }

        private static System.Reflection.Assembly _embeddedCore;

        private static System.Reflection.Assembly ResolveEmbeddedAssembly(object sender, ResolveEventArgs e)
        {
            try
            {
                string name = new System.Reflection.AssemblyName(e.Name).Name;
                if (!string.Equals(name, "codeLess.Core", StringComparison.OrdinalIgnoreCase)) return null;
                if (_embeddedCore != null) return _embeddedCore;
                using (var s = typeof(Program).Assembly.GetManifestResourceStream("codeLess.Core.dll"))
                {
                    if (s == null) return null;
                    var buf = new byte[s.Length];
                    int read = 0;
                    while (read < buf.Length) { int n = s.Read(buf, read, buf.Length - read); if (n <= 0) break; read += n; }
                    _embeddedCore = System.Reflection.Assembly.Load(buf);
                    return _embeddedCore;
                }
            }
            catch { return null; }
        }

        [STAThread]
        public static void Main(string[] args)
        {
            Run(args);
        }

        private static void LogUnhandledException(string source, Exception ex)
        {
            if (ex == null) return;
            try
            {
                string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "codeLess");
                if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
                string logPath = Path.Combine(dir, "crash.log");
                string entry = "[" + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff") + "] [" + source + "] " + ex + Environment.NewLine;
                File.AppendAllText(logPath, entry, Encoding.UTF8);
            }
            catch { }
        }

        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        private static void Run(string[] args)
        {
            try { CodeLessMainForm.SetCurrentProcessExplicitAppUserModelID("Savatarr.codeLess"); } catch { }
            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
            Application.ThreadException += (s, e) => LogUnhandledException("UIThread", e.Exception);
            AppDomain.CurrentDomain.UnhandledException += (s, e) => LogUnhandledException("AppDomain", e.ExceptionObject as Exception);
            TaskScheduler.UnobservedTaskException += (s, e) =>
            {
                LogUnhandledException("UnobservedTask", e.Exception);
                e.SetObserved();
            };
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            string initDir = (args != null && args.Length > 0) ? args[0] : "";
            Application.Run(new CodeLessMainForm(initDir));
        }
    }
}
