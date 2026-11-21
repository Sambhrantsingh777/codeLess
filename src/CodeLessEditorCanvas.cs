using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows.Forms;

namespace codeLess.Studio
{
    public class CodeLessEditorControl : Panel
    {
        public EditorTabBuffer CurrentBuffer;
        public StudioTheme Theme;
        public CodeLessVirtualCanvas Canvas { get { return virtualCanvas; } }
        public List<string> AutocompleteSymbols = new List<string>();
        public List<CodeSnippetItem> LoadedSnippets = new List<CodeSnippetItem>();
        public bool EnableGhostCompletions = true;
        public bool ShowMinimap = true;
        public bool ShowCodeLens = false;

        // Welcome Studio Dashboard & Empty Editor Watermark (shown when no file is open)
        public bool ShowWelcomePage = true;
        private Panel welcomePanel;
        private Panel watermarkPanel;
        private FlowLayoutPanel recentListPanel;

        public bool IsWelcomeVisible
        {
            get { return welcomePanel != null && welcomePanel.Visible && CurrentBuffer == null; }
        }

        public bool IsWatermarkVisible
        {
            get { return watermarkPanel != null && watermarkPanel.Visible && CurrentBuffer == null; }
        }

        // Top Breadcrumb & Quick-Action Bar
        private Panel breadcrumbBar;
        private Label pathLabel;
        private CodeLessDropdown symbolOutlineCombo;
        private Label cursorPosLabel;
        private Button runFileBtn;
        private Button splitViewBtn;
        private Button findToggleBtn;
        private Button ctrlKBtn;
        private Button pinContextBtn;
        private Button saveBtn;

        // Find & Replace Bar (Ctrl+F / Ctrl+H)
        public struct SearchMatch
        {
            public int Line;
            public int Col;
            public int Length;
        }
        public readonly List<SearchMatch> ActiveSearchMatches = new List<SearchMatch>();
        public int ActiveSearchMatchIndex = -1;
        public bool IsFindBarVisible { get { return findBar != null && findBar.Visible; } }
        private Panel findBar;
        private CodeLessDarkInputBox findInput;
        private CodeLessDarkInputBox replaceInput;
        private Button regexToggleBtn;
        private Button caseToggleBtn;
        private Button wordToggleBtn;
        private bool isRegexSearch = false;
        private bool isCaseSearch = false;
        private bool isWholeWordSearch = false;
        private Label findStatusLabel;

        public bool IsRegexSearch
        {
            get { return isRegexSearch; }
            set { isRegexSearch = value; if (regexToggleBtn != null) regexToggleBtn.Tag = value ? "primary" : "subtle"; if (Theme != null && regexToggleBtn != null) StyleButton(regexToggleBtn, Theme); UpdateSearchMatches(true); }
        }
        public bool IsCaseSearch
        {
            get { return isCaseSearch; }
            set { isCaseSearch = value; if (caseToggleBtn != null) caseToggleBtn.Tag = value ? "primary" : "subtle"; if (Theme != null && caseToggleBtn != null) StyleButton(caseToggleBtn, Theme); UpdateSearchMatches(true); }
        }
        public bool IsWholeWordSearch
        {
            get { return isWholeWordSearch; }
            set { isWholeWordSearch = value; if (wordToggleBtn != null) wordToggleBtn.Tag = value ? "primary" : "subtle"; if (Theme != null && wordToggleBtn != null) StyleButton(wordToggleBtn, Theme); UpdateSearchMatches(true); }
        }

        // Compact Staged Diff Review Banner (Inline Per-Hunk Pills are rendered directly in Canvas)
        private Panel diffBanner;
        private Label diffSummaryLabel;
        private Button prevHunkBtn;
        private Button nextHunkBtn;
        private Button acceptDiffBtn;
        private Button rejectDiffBtn;

        // Floating Ctrl+K Surgical Inline Edit Bar
        private Panel ctrlKPanel;
        private CodeLessDarkInputBox ctrlKInput;
        private Button ctrlKSubmitBtn;
        private Button ctrlKCloseBtn;

        // Custom-Painted Virtualized Code Canvas + Custom Bordered Autocomplete List
        private Panel editorBody;
        private CodeLessVirtualCanvas virtualCanvas;
        private Panel autocompleteFrame;
        private CodeLessListBox autocompleteList;
        private string currentWordPrefix = "";

        // Events
        public event Action<EditorTabBuffer> OnSaveRequested;
        public event Action<EditorTabBuffer> OnRunFileRequested;
        public event Action<string, EditorTabBuffer> OnCtrlKRequested;
        public event Action<EditorTabBuffer> OnPinContextRequested;
        public event Action<EditorTabBuffer> OnBufferModified;
        public event Action<EditorTabBuffer> OnCursorOrSelectionChanged;
        public event Action OnToggleSplitEditorRequested;
        public event Action OnOpenFolderClicked;
        public event Action OnOpenFileClicked;
        public event Action OnCloneRepositoryClicked;
        public event Action OnNewFileClicked;
        public event Action OnNewProjectWizardClicked;
        public event Action OnConfigureByokClicked;
        public event Action OnLaunchDemoSandboxClicked;
        public event Action<string> OnOpenRecentWorkspaceClicked;
        public event Action<string> OnGoToDefinitionRequested;
        /// <summary>Ctrl+Click / F12 on an import, path, URL or identifier (VS Code "follow link").</summary>
        public event Action<EditorTabBuffer, EditorLinkTarget> OnLinkActivated;
        /// <summary>Supplies the workspace root used to resolve imports and paths.</summary>
        public Func<string> WorkspaceRootProvider;
        public event Action<EditorTabBuffer, string> OnGoToReferencesRequested;
        public event Action<EditorTabBuffer, string> OnPeekDefinitionRequested;
        public event Action<EditorTabBuffer, string> OnRenameSymbolRequested;
        public event Action<EditorTabBuffer> OnSendSelectionToAgentRequested;
        public event Action OnCommandPaletteRequested;
        public event Action<string> OnChordStatusChanged;
        public Func<KeyEventArgs, bool> OnUnhandledCtrlKChord;
        public event Action<float> OnFontSizeChanged;
        public event Action<bool> OnWordWrapChanged;

        private static readonly Font AutocompleteBadgeFont = new Font("Consolas", 7.8f, FontStyle.Bold);

        public Func<EditorTabBuffer, System.Threading.Tasks.Task<string>> AsyncGhostCompletionProvider;
        private Timer ghostDebounceTimer;
        private int ghostRequestVersion = 0;

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                if (ghostDebounceTimer != null)
                {
                    try { ghostDebounceTimer.Stop(); ghostDebounceTimer.Dispose(); } catch { }
                    ghostDebounceTimer = null;
                }
            }
            base.Dispose(disposing);
        }
        public List<string> StagedChangedFiles = new List<string>();
        private bool showBreadcrumbs = true;
        public bool ShowBreadcrumbs
        {
            get { return showBreadcrumbs; }
            set
            {
                showBreadcrumbs = value;
                if (breadcrumbBar != null)
                    breadcrumbBar.Visible = showBreadcrumbs && CurrentBuffer != null;
            }
        }
        public bool IsColumnSelectionMode
        {
            get { return virtualCanvas != null && virtualCanvas.ColumnSelectionMode; }
            set { if (virtualCanvas != null) { virtualCanvas.ColumnSelectionMode = value; virtualCanvas.Invalidate(); } }
        }
        public bool ColumnSelectionMode
        {
            get { return IsColumnSelectionMode; }
            set { IsColumnSelectionMode = value; }
        }

        public int TabSize = 4;
        public bool InsertSpaces = true;

        internal void NotifyFontSizeChanged(float pt)
        {
            if (OnFontSizeChanged != null) OnFontSizeChanged(pt);
        }

        private static readonly string[] BuiltInKeywords = new[]
        {
            "async", "await", "break", "case", "catch", "class", "const", "continue",
            "def", "default", "delete", "do", "else", "enum", "export", "extends",
            "false", "finally", "fn", "for", "from", "function", "if", "impl",
            "implements", "import", "in", "interface", "let", "loop", "match", "mod",
            "mut", "namespace", "new", "null", "override", "package", "private",
            "protected", "pub", "public", "readonly", "return", "self", "static",
            "struct", "super", "switch", "this", "throw", "trait", "true", "try",
            "type", "typeof", "undefined", "use", "using", "var", "virtual", "void",
            "while", "yield", "Console", "WriteLine", "ReadAllText", "WriteAllText",
            "List", "Dictionary", "Task", "Promise", "Array", "Record", "Result", "Option"
        };

        public CodeLessEditorControl(StudioTheme initialTheme)
        {
            this.Theme = initialTheme;
            this.Dock = DockStyle.Fill;
            this.DoubleBuffered = true;

            BuildControls();
            ApplyTheme(initialTheme);
        }

        public float EditorFontSize
        {
            get { return virtualCanvas != null ? virtualCanvas.FontSizePt : 10.5f; }
            set { if (virtualCanvas != null) virtualCanvas.SetFontSize(value); }
        }

        public string EditorFontFamily
        {
            get { return virtualCanvas != null ? virtualCanvas.FontFamilyName : "Consolas"; }
            set { if (virtualCanvas != null) virtualCanvas.SetFontFamilyAndSize(value, virtualCanvas.FontSizePt); }
        }

        private void BuildControls()
        {
            // 1. Clean 24px VS Code Breadcrumb Bar (folder › subfolder › file.cs › symbol)
            breadcrumbBar = new Panel
            {
                Dock = DockStyle.Top,
                Height = 24,
                Padding = new Padding(10, 1, 10, 1)
            };
            breadcrumbBar.Paint += (s, e) =>
            {
                if (Theme != null)
                {
                    using (Pen bp = new Pen(Theme.BorderSubtle, 1f))
                        e.Graphics.DrawLine(bp, 0, breadcrumbBar.Height - 1, breadcrumbBar.Width, breadcrumbBar.Height - 1);
                }
            };

            pathLabel = new Label
            {
                AutoSize = false,
                AutoEllipsis = true,
                TextAlign = ContentAlignment.MiddleLeft,
                Location = new Point(10, 2),
                Size = new Size(240, 20),
                Font = new Font("Segoe UI", 8.8f),
                Text = "codeLess"
            };

            symbolOutlineCombo = new CodeLessDropdown
            {
                Location = new Point(256, 1),
                Width = 185,
                Height = 20,
                Font = new Font("Consolas", 8.2f),
                Visible = false
            };
            symbolOutlineCombo.SelectedIndexChanged += (s, e) =>
            {
                if (suppressSymbolOutlineEvent) return;
                if (CurrentBuffer == null || symbolOutlineCombo.SelectedIndex < 0 || symbolOutlineCombo.SelectedIndex >= CurrentBuffer.Symbols.Count)
                    return;
                AstSymbolItem sym = CurrentBuffer.Symbols[symbolOutlineCombo.SelectedIndex];
                JumpToLine(sym.Line);
            };

            cursorPosLabel = new Label
            {
                AutoSize = true,
                Location = new Point(435, 4),
                Font = new Font("Consolas", 8.2f, FontStyle.Regular),
                Text = "Ln 1, Col 1",
                Visible = false
            };

            // Keep legacy button fields constructed for reflection/programmatic access, hidden from breadcrumb bar
            // (Compact 22x22 Run/Split/More icon buttons now live at the right end of the tab bar like VS Code)
            runFileBtn = CreateStudioButton("Run (F5)", 82, false);
            runFileBtn.Tag = "run";
            runFileBtn.Visible = false;
            runFileBtn.Click += (s, e) =>
            {
                if (CurrentBuffer != null && OnRunFileRequested != null)
                    OnRunFileRequested(CurrentBuffer);
            };

            splitViewBtn = CreateStudioButton("Split", 58, false);
            splitViewBtn.Visible = false;
            splitViewBtn.Click += (s, e) =>
            {
                if (OnToggleSplitEditorRequested != null)
                    OnToggleSplitEditorRequested();
            };

            findToggleBtn = CreateStudioButton("Find", 58, false);
            findToggleBtn.Visible = false;
            findToggleBtn.Click += (s, e) => ToggleFindBar();

            ctrlKBtn = CreateStudioButton("Inline Edit", 92, true);
            ctrlKBtn.Visible = false;
            ctrlKBtn.Click += (s, e) => ToggleCtrlKBar();

            pinContextBtn = CreateStudioButton("+ @Pin", 56, false);
            pinContextBtn.Visible = false;
            pinContextBtn.Click += (s, e) =>
            {
                if (CurrentBuffer != null && OnPinContextRequested != null)
                    OnPinContextRequested(CurrentBuffer);
            };

            saveBtn = CreateStudioButton("Saved", 56, false);
            saveBtn.Visible = false;
            saveBtn.Click += (s, e) =>
            {
                if (CurrentBuffer != null && OnSaveRequested != null)
                    OnSaveRequested(CurrentBuffer);
            };

            breadcrumbBar.Controls.AddRange(new Control[]
            {
                pathLabel, symbolOutlineCombo, cursorPosLabel,
                runFileBtn, splitViewBtn, findToggleBtn, pinContextBtn, ctrlKBtn, saveBtn
            });
            breadcrumbBar.Resize += (s, e) => LayoutBreadcrumbButtons();

            // 2. Find & Replace Bar (Ctrl+F / Ctrl+H) — Borderless dark inputs + toggle pills (responsive 1-row / 2-row)
            findBar = new Panel
            {
                Dock = DockStyle.Top,
                Height = 40,
                Visible = false,
                Padding = new Padding(10, 6, 10, 6)
            };
            findBar.Paint += (s, e) =>
            {
                if (Theme != null)
                {
                    using (Pen bp = new Pen(Theme.BorderSubtle, 1f))
                        e.Graphics.DrawLine(bp, 0, findBar.Height - 1, findBar.Width, findBar.Height - 1);
                }
            };

            Label findLbl = new Label { Text = "Find", AutoSize = true, Location = new Point(10, 12), Font = new Font("Segoe UI Semibold", 8.6f) };
            findInput = new CodeLessDarkInputBox
            {
                Location = new Point(44, 7),
                Size = new Size(170, 26),
                Font = new Font("Consolas", 9.0f),
                Placeholder = "Find in file..."
            };
            Label repLbl = new Label { Text = "Replace", AutoSize = true, Location = new Point(222, 12), Font = new Font("Segoe UI Semibold", 8.6f) };
            replaceInput = new CodeLessDarkInputBox
            {
                Location = new Point(276, 7),
                Size = new Size(155, 26),
                Font = new Font("Consolas", 9.0f),
                Placeholder = "Replace with..."
            };

            regexToggleBtn = CreateStudioButton(".*", 30, false);
            regexToggleBtn.Location = new Point(438, 8);
            regexToggleBtn.Height = 24;
            regexToggleBtn.Click += (s, e) =>
            {
                isRegexSearch = !isRegexSearch;
                regexToggleBtn.Tag = isRegexSearch ? "primary" : "subtle";
                if (Theme != null) StyleButton(regexToggleBtn, Theme);
                UpdateSearchMatches(true);
            };

            caseToggleBtn = CreateStudioButton("Aa", 30, false);
            caseToggleBtn.Location = new Point(472, 8);
            caseToggleBtn.Height = 24;
            caseToggleBtn.Click += (s, e) =>
            {
                isCaseSearch = !isCaseSearch;
                caseToggleBtn.Tag = isCaseSearch ? "primary" : "subtle";
                if (Theme != null) StyleButton(caseToggleBtn, Theme);
                UpdateSearchMatches(true);
            };

            wordToggleBtn = CreateStudioButton("ab", 30, false);
            wordToggleBtn.Location = new Point(506, 8);
            wordToggleBtn.Height = 24;
            wordToggleBtn.Click += (s, e) =>
            {
                isWholeWordSearch = !isWholeWordSearch;
                wordToggleBtn.Tag = isWholeWordSearch ? "primary" : "subtle";
                if (Theme != null) StyleButton(wordToggleBtn, Theme);
                UpdateSearchMatches(true);
            };

            Button findPrevBtn = CreateStudioButton("", 28, false);
            findPrevBtn.Location = new Point(540, 8);
            findPrevBtn.Height = 24;
            findPrevBtn.Paint += (s, e) =>
            {
                Color ic = Theme != null ? Theme.FgPrimary : Color.LightGray;
                CodeLessVectorIcons.DrawIcon(e.Graphics, "chevron-up", new Rectangle((findPrevBtn.Width - 14) / 2, (findPrevBtn.Height - 14) / 2, 14, 14), ic, 1.5f);
            };
            findPrevBtn.Click += (s, e) => FindPreviousOccurrence();

            Button findNextBtn = CreateStudioButton("Next", 54, true);
            findNextBtn.Location = new Point(572, 8);
            findNextBtn.Height = 24;
            findNextBtn.Click += (s, e) => FindNextOccurrence();

            Button replaceOneBtn = CreateStudioButton("Replace", 64, false);
            replaceOneBtn.Location = new Point(634, 8);
            replaceOneBtn.Height = 24;
            replaceOneBtn.Click += (s, e) => ReplaceCurrentOccurrence();

            Button replaceAllBtn = CreateStudioButton("Replace All", 80, false);
            replaceAllBtn.Location = new Point(702, 8);
            replaceAllBtn.Height = 24;
            replaceAllBtn.Click += (s, e) => ReplaceAllOccurrences();

            Button closeFindBtn = CreateStudioButton("", 26, false);
            closeFindBtn.Location = new Point(786, 8);
            closeFindBtn.Height = 24;
            closeFindBtn.Paint += (s, e) =>
            {
                Color ic = Theme != null ? Theme.FgSecondary : Color.LightGray;
                CodeLessVectorIcons.DrawIcon(e.Graphics, "close", new Rectangle((closeFindBtn.Width - 14) / 2, (closeFindBtn.Height - 14) / 2, 14, 14), ic, 1.5f);
            };
            closeFindBtn.Click += (s, e) =>
            {
                findBar.Visible = false;
                ActiveSearchMatches.Clear();
                ActiveSearchMatchIndex = -1;
                virtualCanvas.Invalidate();
                virtualCanvas.Focus();
            };

            findStatusLabel = new Label { AutoSize = true, Location = new Point(820, 12), Font = new Font("Consolas", 8.5f), Text = "" };

            findInput.TextChanged += (s, e) =>
            {
                UpdateSearchMatches(true);
            };

            findInput.KeyDown += (s, e) =>
            {
                if (e.Alt && e.KeyCode == Keys.C)
                {
                    e.SuppressKeyPress = true;
                    IsCaseSearch = !IsCaseSearch;
                }
                else if (e.Alt && e.KeyCode == Keys.W)
                {
                    e.SuppressKeyPress = true;
                    IsWholeWordSearch = !IsWholeWordSearch;
                }
                else if (e.Alt && e.KeyCode == Keys.R)
                {
                    e.SuppressKeyPress = true;
                    IsRegexSearch = !IsRegexSearch;
                }
                else if (e.Alt && e.KeyCode == Keys.Enter)
                {
                    e.SuppressKeyPress = true;
                    SelectAllFindMatches();
                }
                else if (e.KeyCode == Keys.Enter || e.KeyCode == Keys.F3)
                {
                    e.SuppressKeyPress = true;
                    if (e.Shift) FindPreviousOccurrence();
                    else FindNextOccurrence();
                }
                else if (e.KeyCode == Keys.Escape)
                {
                    e.SuppressKeyPress = true;
                    findBar.Visible = false;
                    ActiveSearchMatches.Clear();
                    ActiveSearchMatchIndex = -1;
                    virtualCanvas.Invalidate();
                    virtualCanvas.Focus();
                }
            };

            replaceInput.KeyDown += (s, e) =>
            {
                if (e.Alt && e.KeyCode == Keys.C)
                {
                    e.SuppressKeyPress = true;
                    IsCaseSearch = !IsCaseSearch;
                }
                else if (e.Alt && e.KeyCode == Keys.W)
                {
                    e.SuppressKeyPress = true;
                    IsWholeWordSearch = !IsWholeWordSearch;
                }
                else if (e.Alt && e.KeyCode == Keys.R)
                {
                    e.SuppressKeyPress = true;
                    IsRegexSearch = !IsRegexSearch;
                }
                else if (e.Alt && e.KeyCode == Keys.Enter)
                {
                    e.SuppressKeyPress = true;
                    SelectAllFindMatches();
                }
                else if (e.KeyCode == Keys.Enter)
                {
                    e.SuppressKeyPress = true;
                    if (e.Control && e.Alt) ReplaceAllOccurrences();
                    else ReplaceCurrentOccurrence();
                }
                else if (e.KeyCode == Keys.F3)
                {
                    e.SuppressKeyPress = true;
                    if (e.Shift) FindPreviousOccurrence();
                    else FindNextOccurrence();
                }
                else if (e.KeyCode == Keys.Escape)
                {
                    e.SuppressKeyPress = true;
                    findBar.Visible = false;
                    ActiveSearchMatches.Clear();
                    ActiveSearchMatchIndex = -1;
                    virtualCanvas.Invalidate();
                    virtualCanvas.Focus();
                }
            };

            findBar.Controls.AddRange(new Control[]
            {
                findLbl, findInput, repLbl, replaceInput, regexToggleBtn, caseToggleBtn, wordToggleBtn,
                findPrevBtn, findNextBtn, replaceOneBtn, replaceAllBtn, closeFindBtn, findStatusLabel
            });
            Action layoutFindBar = () =>
            {
                int fw = findBar.Width;
                if (fw >= 820)
                {
                    if (findBar.Height != 40) findBar.Height = 40;
                    int flex = Math.Max(0, Math.Min(110, (fw - 820) / 2));
                    findLbl.Location = new Point(10, 12);
                    findInput.Location = new Point(44, 7);
                    findInput.Width = 150 + flex;
                    repLbl.Location = new Point(findInput.Right + 8, 12);
                    replaceInput.Location = new Point(repLbl.Left + 52, 7);
                    replaceInput.Width = 135 + flex;
                    int rx = replaceInput.Right + 6;
                    regexToggleBtn.Location = new Point(rx, 8);
                    caseToggleBtn.Location = new Point(rx + 33, 8);
                    wordToggleBtn.Location = new Point(rx + 66, 8);
                    findPrevBtn.Location = new Point(rx + 99, 8);
                    findNextBtn.Location = new Point(rx + 132, 8);
                    replaceOneBtn.Location = new Point(rx + 192, 8);
                    replaceAllBtn.Location = new Point(rx + 260, 8);
                    closeFindBtn.Location = new Point(fw - 36, 8);
                    findStatusLabel.Location = new Point(rx + 345, 12);
                }
                else
                {
                    if (findBar.Height != 68) findBar.Height = 68;
                    int rw = Math.Max(320, fw);
                    findLbl.Location = new Point(8, 10);
                    closeFindBtn.Location = new Point(rw - 34, 6);
                    findNextBtn.Location = new Point(closeFindBtn.Left - 60, 6);
                    findPrevBtn.Location = new Point(findNextBtn.Left - 33, 6);
                    wordToggleBtn.Location = new Point(findPrevBtn.Left - 33, 6);
                    caseToggleBtn.Location = new Point(wordToggleBtn.Left - 33, 6);
                    regexToggleBtn.Location = new Point(caseToggleBtn.Left - 33, 6);
                    findInput.Location = new Point(42, 5);
                    findInput.Width = Math.Max(68, regexToggleBtn.Left - 48);

                    repLbl.Location = new Point(8, 40);
                    replaceAllBtn.Location = new Point(rw - 86, 36);
                    replaceOneBtn.Location = new Point(replaceAllBtn.Left - 68, 36);
                    replaceInput.Location = new Point(58, 35);
                    replaceInput.Width = Math.Max(68, replaceOneBtn.Left - 64);
                    findStatusLabel.Location = new Point(replaceAllBtn.Right + 4, 40);
                }
            };
            findBar.Resize += (s, e) => layoutFindBar();

            // 3. Slim Diff Review Bar (28px — Per-Hunk [Accept | Reject] are also drawn inline in Canvas)
            diffBanner = new Panel
            {
                Dock = DockStyle.Top,
                Height = 28,
                Visible = false
            };
            diffBanner.Paint += (s, e) =>
            {
                using (Pen ep = new Pen(Color.FromArgb(40, 110, 85), 1f))
                    e.Graphics.DrawLine(ep, 0, diffBanner.Height - 1, diffBanner.Width, diffBanner.Height - 1);
            };

            diffSummaryLabel = new Label
            {
                AutoSize = false,
                AutoEllipsis = true,
                TextAlign = ContentAlignment.MiddleLeft,
                Location = new Point(12, 3),
                Size = new Size(280, 22),
                Font = new Font("Segoe UI", 8.6f, FontStyle.Regular),
                ForeColor = Color.FromArgb(167, 243, 208),
                Text = "1 change staged"
            };

            prevHunkBtn = new Button
            {
                Text = "",
                FlatStyle = FlatStyle.Flat,
                BackColor = Color.FromArgb(45, 52, 66),
                ForeColor = Color.White,
                Font = new Font("Segoe UI", 8.2f, FontStyle.Regular),
                Size = new Size(24, 22),
                Cursor = Cursors.Hand
            };
            prevHunkBtn.FlatAppearance.BorderSize = 0;
            prevHunkBtn.Paint += (s, e) =>
            {
                Color ic = (Theme != null && Theme.IsLight) ? Theme.FgPrimary : Color.White;
                CodeLessVectorIcons.DrawIcon(e.Graphics, "chevron-up", new Rectangle((prevHunkBtn.Width - 14) / 2, (prevHunkBtn.Height - 14) / 2, 14, 14), ic, 1.5f);
            };
            prevHunkBtn.Click += (s, e) => NavigateDiffHunk(-1);

            nextHunkBtn = new Button
            {
                Text = "",
                FlatStyle = FlatStyle.Flat,
                BackColor = Color.FromArgb(45, 52, 66),
                ForeColor = Color.White,
                Font = new Font("Segoe UI", 8.2f, FontStyle.Regular),
                Size = new Size(24, 22),
                Cursor = Cursors.Hand
            };
            nextHunkBtn.FlatAppearance.BorderSize = 0;
            nextHunkBtn.Paint += (s, e) =>
            {
                Color ic = (Theme != null && Theme.IsLight) ? Theme.FgPrimary : Color.White;
                CodeLessVectorIcons.DrawIcon(e.Graphics, "chevron-down", new Rectangle((nextHunkBtn.Width - 14) / 2, (nextHunkBtn.Height - 14) / 2, 14, 14), ic, 1.5f);
            };
            nextHunkBtn.Click += (s, e) => NavigateDiffHunk(1);

            acceptDiffBtn = new Button
            {
                Text = "Accept All (Ctrl+Enter)",
                FlatStyle = FlatStyle.Flat,
                BackColor = Color.FromArgb(16, 185, 129),
                ForeColor = Color.White,
                Font = new Font("Segoe UI", 8.2f, FontStyle.Regular),
                Size = new Size(142, 22),
                Cursor = Cursors.Hand
            };
            acceptDiffBtn.FlatAppearance.BorderSize = 0;
            acceptDiffBtn.Click += (s, e) => AcceptAllHunks();

            rejectDiffBtn = new Button
            {
                Text = "Reject All (Ctrl+Backspace)",
                FlatStyle = FlatStyle.Flat,
                BackColor = Color.FromArgb(65, 72, 88),
                ForeColor = Color.White,
                Font = new Font("Segoe UI", 8.2f, FontStyle.Regular),
                Size = new Size(162, 22),
                Cursor = Cursors.Hand
            };
            rejectDiffBtn.FlatAppearance.BorderSize = 0;
            rejectDiffBtn.Click += (s, e) => RejectAllHunks();

            diffBanner.Controls.Add(diffSummaryLabel);
            diffBanner.Controls.Add(prevHunkBtn);
            diffBanner.Controls.Add(nextHunkBtn);
            diffBanner.Controls.Add(acceptDiffBtn);
            diffBanner.Controls.Add(rejectDiffBtn);
            diffBanner.Resize += (s, e) => LayoutDiffBanner();

            // 4. Floating Ctrl+K Surgical Inline Prompt Box (Anchored at selection inside editorBody)
            ctrlKPanel = new Panel
            {
                Dock = DockStyle.None,
                Size = new Size(560, 72),
                Location = new Point(76, 36),
                Visible = false,
                Padding = new Padding(10, 6, 10, 6)
            };
            ctrlKPanel.Paint += (s, e) =>
            {
                if (Theme != null)
                {
                    e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
                    using (GraphicsPath gp = StudioTheme.CreateRoundRect(new Rectangle(0, 0, ctrlKPanel.Width - 1, ctrlKPanel.Height - 1), 6))
                    {
                        using (SolidBrush bg = new SolidBrush(Theme.BgElevated))
                            e.Graphics.FillPath(bg, gp);
                        using (Pen bp = new Pen(Theme.AccentPrimary, 1.4f))
                            e.Graphics.DrawPath(bp, gp);
                    }
                    e.Graphics.SmoothingMode = SmoothingMode.Default;
                    CodeLessVectorIcons.DrawIcon(e.Graphics, "agent", new Rectangle(11, 12, 14, 14), Theme.AccentPrimary, 1.5f);
                }
            };

            Label ctrlKTag = new Label
            {
                Text = "Ctrl+K",
                AutoSize = true,
                Location = new Point(27, 11),
                Font = new Font("Segoe UI Semibold", 8.8f, FontStyle.Bold),
                BackColor = Color.Transparent
            };

            ctrlKInput = new CodeLessDarkInputBox
            {
                Location = new Point(78, 7),
                Size = new Size(340, 26),
                Font = new Font("Segoe UI", 9.0f),
                Placeholder = "Edit selection or generate code (Enter to submit, Esc to close)..."
            };
            ctrlKInput.KeyDown += (s, e) =>
            {
                if (e.KeyCode == Keys.Enter)
                {
                    e.SuppressKeyPress = true;
                    SubmitCtrlK();
                }
                else if (e.KeyCode == Keys.Escape)
                {
                    e.SuppressKeyPress = true;
                    ctrlKPanel.Visible = false;
                    virtualCanvas.Focus();
                }
            };

            ctrlKSubmitBtn = CreateStudioButton("Generate", 82, true);
            ctrlKSubmitBtn.Height = 25;
            ctrlKSubmitBtn.Click += (s, e) => SubmitCtrlK();

            ctrlKCloseBtn = CreateStudioButton("", 26, false);
            ctrlKCloseBtn.Height = 25;
            ctrlKCloseBtn.Paint += (s, e) =>
            {
                Color ic = Theme != null ? Theme.FgSecondary : Color.LightGray;
                CodeLessVectorIcons.DrawIcon(e.Graphics, "close", new Rectangle((ctrlKCloseBtn.Width - 14) / 2, (ctrlKCloseBtn.Height - 14) / 2, 14, 14), ic, 1.5f);
            };
            ctrlKCloseBtn.Click += (s, e) =>
            {
                ctrlKPanel.Visible = false;
                virtualCanvas.Focus();
            };

            FlowLayoutPanel chipsFlow = new FlowLayoutPanel
            {
                Location = new Point(10, 39),
                Size = new Size(536, 26),
                WrapContents = false,
                BackColor = Color.Transparent
            };
            string[][] quickPrompts = new[]
            {
                new[] { "Add Error Handling", "Add structured try/catch error handling" },
                new[] { "Add Docstrings", "Generate API docstrings & comments" },
                new[] { "Write Unit Tests", "Generate runnable unit tests" },
                new[] { "Add Perf Logging", "Add performance timing & logging" },
                new[] { "Fix Diagnostics", "Fix all syntax & diagnostic issues" }
            };
            foreach (string[] qpPair in quickPrompts)
            {
                string chipTitle = qpPair[0];
                string capturedQp = qpPair[1];
                Button chip = new Button
                {
                    Text = chipTitle,
                    AutoSize = true,
                    Height = 22,
                    FlatStyle = FlatStyle.Flat,
                    Font = new Font("Segoe UI", 8f),
                    Cursor = Cursors.Hand,
                    Margin = new Padding(0, 0, 6, 0),
                    Tag = "subtle"
                };
                chip.Click += (s, e) =>
                {
                    ctrlKInput.Text = capturedQp;
                    SubmitCtrlK();
                };
                chipsFlow.Controls.Add(chip);
            }

            ctrlKPanel.Controls.Add(ctrlKTag);
            ctrlKPanel.Controls.Add(ctrlKInput);
            ctrlKPanel.Controls.Add(ctrlKSubmitBtn);
            ctrlKPanel.Controls.Add(ctrlKCloseBtn);
            ctrlKPanel.Controls.Add(chipsFlow);
            Action layoutCtrlKFloating = () =>
            {
                int w = ctrlKPanel.Width;
                ctrlKCloseBtn.Location = new Point(w - 36, 7);
                ctrlKSubmitBtn.Location = new Point(ctrlKCloseBtn.Left - ctrlKSubmitBtn.Width - 6, 7);
                ctrlKInput.Location = new Point(78, 7);
                ctrlKInput.Width = Math.Max(80, ctrlKSubmitBtn.Left - 84);
                chipsFlow.Location = new Point(10, 39);
                chipsFlow.Width = Math.Max(160, w - 20);
                int accumX = 0;
                foreach (Control chipCtrl in chipsFlow.Controls)
                {
                    int cw = chipCtrl.PreferredSize.Width + chipCtrl.Margin.Horizontal;
                    chipCtrl.Visible = (accumX + cw <= chipsFlow.Width);
                    if (chipCtrl.Visible) accumX += cw;
                }
            };
            ctrlKPanel.Resize += (s, e) => layoutCtrlKFloating();
            layoutCtrlKFloating();

            // 5. Welcome Studio Dashboard & Empty Watermark
            BuildWelcomeStudioPanel();
            BuildWatermarkPanel();

            // 6. Editor Body (Virtualized Custom-Painted Canvas + Floating Ctrl+K + Autocomplete Popup)
            editorBody = new Panel { Dock = DockStyle.Fill, Visible = false };

            virtualCanvas = new CodeLessVirtualCanvas(this)
            {
                Dock = DockStyle.Fill
            };

            autocompleteFrame = new Panel
            {
                Visible = false,
                Size = new Size(320, 180),
                Padding = new Padding(1)
            };
            autocompleteList = new CodeLessListBox
            {
                Dock = DockStyle.Fill,
                BorderStyle = BorderStyle.None,
                Font = new Font("Consolas", 9.0f),
                DrawMode = DrawMode.OwnerDrawFixed,
                ItemHeight = 22
            };
            autocompleteList.DrawItem += DrawAutocompleteItem;
            autocompleteList.DoubleClick += (s, e) => AcceptSelectedAutocomplete();
            autocompleteList.KeyDown += (s, e) =>
            {
                if (e.KeyCode == Keys.Enter || e.KeyCode == Keys.Tab)
                {
                    e.SuppressKeyPress = true;
                    AcceptSelectedAutocomplete();
                }
                else if (e.KeyCode == Keys.Escape)
                {
                    e.SuppressKeyPress = true;
                    autocompleteFrame.Visible = false;
                    virtualCanvas.Focus();
                }
            };
            autocompleteFrame.Controls.Add(autocompleteList);

            editorBody.Controls.Add(ctrlKPanel);
            editorBody.Controls.Add(autocompleteFrame);
            editorBody.Controls.Add(virtualCanvas);
            ctrlKPanel.BringToFront();
            autocompleteFrame.BringToFront();
            editorBody.Resize += (s, e) =>
            {
                if (ctrlKPanel.Visible) PositionFloatingCtrlKPanel();
            };

            ghostDebounceTimer = new Timer { Interval = 260 };
            ghostDebounceTimer.Tick += async (s, e) =>
            {
                ghostDebounceTimer.Stop();
                if (!EnableGhostCompletions || CurrentBuffer == null || AsyncGhostCompletionProvider == null) return;
                int reqVer = ++ghostRequestVersion;
                EditorTabBuffer reqBuf = CurrentBuffer;
                int reqLine = reqBuf.CursorLine;
                int reqCol = reqBuf.CursorCol;
                try
                {
                    string asyncGhost = await AsyncGhostCompletionProvider(reqBuf);
                    if (reqVer == ghostRequestVersion && CurrentBuffer == reqBuf &&
                        reqBuf.CursorLine == reqLine && reqBuf.CursorCol == reqCol &&
                        !string.IsNullOrEmpty(asyncGhost))
                    {
                        reqBuf.GhostText = asyncGhost;
                        reqBuf.GhostLine = reqLine;
                        reqBuf.GhostCol = reqCol;
                        if (virtualCanvas != null) virtualCanvas.Invalidate();
                    }
                }
                catch { }
            };

            this.Controls.Add(watermarkPanel);
            this.Controls.Add(welcomePanel);
            this.Controls.Add(editorBody);
            this.Controls.Add(diffBanner);
            this.Controls.Add(findBar);
            this.Controls.Add(breadcrumbBar);
        }

        public void PositionFloatingCtrlKPanel()
        {
            if (ctrlKPanel == null || editorBody == null || virtualCanvas == null) return;
            int maxW = Math.Max(340, Math.Min(560, editorBody.ClientSize.Width - 88));
            if (ctrlKPanel.Width != maxW) ctrlKPanel.Width = maxW;

            Point caretPt = virtualCanvas.GetCursorPixelPoint();
            int targetX = Math.Max(68, Math.Min(Math.Max(68, editorBody.ClientSize.Width - ctrlKPanel.Width - 24), caretPt.X));
            int targetY = caretPt.Y + virtualCanvas.LineHeight + 6;
            if (targetY + ctrlKPanel.Height > editorBody.ClientSize.Height - 12)
                targetY = Math.Max(8, caretPt.Y - ctrlKPanel.Height - 6);
            ctrlKPanel.Location = new Point(targetX, Math.Max(6, targetY));
            ctrlKPanel.BringToFront();
        }

        private void DrawAutocompleteItem(object sender, DrawItemEventArgs e)
        {
            if (e.Index < 0 || e.Index >= autocompleteList.Items.Count) return;
            bool selected = (e.State & DrawItemState.Selected) == DrawItemState.Selected;
            Color bg = selected ? Theme.AccentPrimary : Theme.BgElevated;
            using (SolidBrush b = new SolidBrush(bg))
                e.Graphics.FillRectangle(b, e.Bounds);

            string raw = autocompleteList.Items[e.Index].ToString();
            string badge = "sym";
            Color badgeColor = Theme.TokenFunction;
            if (raw.EndsWith(" (snippet)")) { badge = "snip"; badgeColor = Theme.AccentSecondary; }
            else if (BuiltInKeywords.Contains(raw)) { badge = "kw"; badgeColor = Theme.TokenKeyword; }

            using (SolidBrush bb = new SolidBrush(selected ? Color.FromArgb(40, 0, 0, 0) : Color.FromArgb(45, badgeColor)))
                e.Graphics.FillRectangle(bb, e.Bounds.X + 4, e.Bounds.Y + 3, 34, e.Bounds.Height - 6);

            TextRenderer.DrawText(e.Graphics, badge, AutocompleteBadgeFont,
                new Point(e.Bounds.X + 7, e.Bounds.Y + 4), selected ? Color.White : badgeColor);

            TextRenderer.DrawText(e.Graphics, raw, autocompleteList.Font,
                new Point(e.Bounds.X + 44, e.Bounds.Y + 3), selected ? Color.White : Theme.FgPrimary);
        }

        private void BuildWatermarkPanel()
        {
            watermarkPanel = new Panel
            {
                Dock = DockStyle.Fill,
                Visible = false,
                Text = "codeLess — Show All Commands Ctrl+Shift+P | Go to File Ctrl+P | Open Folder Ctrl+K Ctrl+O | Toggle Terminal Ctrl+`"
            };
            var propDb = typeof(Control).GetProperty("DoubleBuffered", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
            if (propDb != null) propDb.SetValue(watermarkPanel, true, null);

            watermarkPanel.Resize += (s, e) => watermarkPanel.Invalidate();
            watermarkPanel.Paint += (s, e) =>
            {
                Graphics g = e.Graphics;
                StudioTheme t = Theme ?? StudioTheme.GetBuiltInThemes()[0];
                g.Clear(t.BgEditor);
                g.SmoothingMode = SmoothingMode.AntiAlias;

                int cx = watermarkPanel.ClientSize.Width / 2;
                int cy = Math.Max(140, watermarkPanel.ClientSize.Height / 2 - 20);

                Color logoCol = StudioTheme.Blend(t.BgEditor, t.FgMuted, 0.35);
                CodeLessVectorIcons.DrawIcon(g, "logo", new Rectangle(cx - 44, cy - 125, 88, 72), logoCol, 3.0f);

                using (Font fBrand = new Font("Segoe UI Semibold", 16f, FontStyle.Bold))
                {
                    Size bSz = TextRenderer.MeasureText("codeLess", fBrand);
                    TextRenderer.DrawText(g, "codeLess", fBrand, new Point(cx - bSz.Width / 2, cy - 46), logoCol, TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix);
                }

                string[,] hints = new string[,]
                {
                    { "Show All Commands", "Ctrl+Shift+P" },
                    { "Go to File", "Ctrl+P" },
                    { "Open Folder", "Ctrl+K Ctrl+O" },
                    { "Toggle Terminal", "Ctrl+`" }
                };

                using (Font fLabel = new Font("Segoe UI", 9.5f))
                using (Font fKey = new Font("Consolas", 8.8f))
                {
                    int rowY = cy + 4;
                    for (int i = 0; i < hints.GetLength(0); i++)
                    {
                        string lbl = hints[i, 0];
                        string key = hints[i, 1];
                        TextRenderer.DrawText(g, lbl, fLabel, new Rectangle(cx - 190, rowY, 170, 24), t.FgMuted,
                            TextFormatFlags.Right | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);

                        string[] parts = key.Split(' ');
                        int kx = cx - 8;
                        g.SmoothingMode = SmoothingMode.Default;
                        foreach (string part in parts)
                        {
                            Size kSz = TextRenderer.MeasureText(part, fKey, new Size(200, 24), TextFormatFlags.NoPadding);
                            Rectangle pillR = new Rectangle(kx, rowY + 2, kSz.Width + 12, 20);
                            using (SolidBrush kb = new SolidBrush(t.BgElevated))
                                g.FillRectangle(kb, pillR);
                            TextRenderer.DrawText(g, part, fKey, pillR, t.FgSecondary,
                                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix);
                            using (Pen kp = new Pen(t.BorderSubtle, 1f))
                                g.DrawRectangle(kp, pillR.X, pillR.Y, pillR.Width - 1, pillR.Height - 1);
                            kx += pillR.Width + 5;
                        }
                        rowY += 30;
                    }
                }
            };
        }

        public static string FormatShortenedHomePath(string fullPath)
        {
            if (string.IsNullOrEmpty(fullPath)) return "";
            string norm = fullPath.TrimEnd('\\', '/');
            string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile).TrimEnd('\\', '/');
            if (!string.IsNullOrEmpty(home) && norm.StartsWith(home, StringComparison.OrdinalIgnoreCase))
            {
                string rel = norm.Substring(home.Length).TrimStart('\\', '/');
                return string.IsNullOrEmpty(rel) ? "~" : ("~\\" + rel);
            }
            string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData).TrimEnd('\\', '/');
            if (!string.IsNullOrEmpty(localAppData) && norm.IndexOf("\\AppData\\Local", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                int idx = norm.IndexOf("\\AppData\\Local", StringComparison.OrdinalIgnoreCase);
                return "~" + norm.Substring(idx);
            }
            return norm;
        }

        public static string FormatFittingRecentPath(string dirPath, Font pathFont, int maxPathWidth)
        {
            string shortPath = FormatShortenedHomePath(dirPath);
            if (string.IsNullOrEmpty(shortPath)) return "";
            if (maxPathWidth <= 24) return shortPath;

            Size s0 = TextRenderer.MeasureText(shortPath, pathFont, new Size(2000, 24), TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix);
            if (s0.Width <= maxPathWidth) return shortPath;

            // Try compacting intermediate directories while keeping root (~ or drive) + tail segments without ellipsis
            string[] parts = shortPath.Split(new[] { '\\', '/' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length >= 3)
            {
                string cand2 = parts[0] + "\\" + parts[parts.Length - 2] + "\\" + parts[parts.Length - 1];
                Size s2 = TextRenderer.MeasureText(cand2, pathFont, new Size(2000, 24), TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix);
                if (s2.Width <= maxPathWidth) return cand2;
            }
            if (parts.Length >= 2)
            {
                string cand1 = parts[0] + "\\" + parts[parts.Length - 1];
                Size s1 = TextRenderer.MeasureText(cand1, pathFont, new Size(2000, 24), TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix);
                if (s1.Width <= maxPathWidth) return cand1;

                string cand0 = parts[0];
                Size s0Root = TextRenderer.MeasureText(cand0, pathFont, new Size(2000, 24), TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix);
                if (s0Root.Width <= maxPathWidth) return cand0;
            }

            // If the path truly cannot fit, middle-ellipsize the path part only (VS Code style)
            string rootPrefix = parts.Length > 0 ? (parts[0] + "\\") : "";
            string leaf = parts.Length > 0 ? parts[parts.Length - 1] : shortPath;
            string midCandidate = rootPrefix + "..." + "\\" + leaf;
            if (TextRenderer.MeasureText(midCandidate, pathFont, new Size(2000, 24), TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix).Width <= maxPathWidth)
                return midCandidate;

            int half = Math.Max(2, (shortPath.Length - 3) / 2);
            while (half > 2)
            {
                string m = shortPath.Substring(0, half) + "..." + shortPath.Substring(shortPath.Length - half);
                if (TextRenderer.MeasureText(m, pathFont, new Size(2000, 24), TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix).Width <= maxPathWidth)
                    return m;
                half--;
            }
            return parts.Length > 0 ? parts[0] : "";
        }

        private int welcomeScrollOffset = 0;
        private int welcomeContentHeight = 360;
        private Action relayoutWelcomePanel;

        private void BuildWelcomeStudioPanel()
        {
            welcomePanel = new Panel
            {
                Dock = DockStyle.Fill,
                Padding = new Padding(0),
                AutoScroll = false
            };

            Panel heroLogoBox = new Panel
            {
                Location = new Point(24, 18),
                Size = new Size(44, 44),
                BackColor = Color.Transparent,
                AccessibleName = "Savatarr Brand Logo",
                Tag = "brand-logo"
            };
            heroLogoBox.Paint += (s, e) =>
            {
                CodeLessVectorIcons.DrawBrandLogo(e.Graphics, new Rectangle(2, 2, 40, 40), false);
            };

            Label heroTitle = new Label
            {
                Text = "codeLess",
                AutoSize = true,
                Location = new Point(74, 16),
                Font = new Font("Segoe UI Semibold", 20f, FontStyle.Bold),
                UseMnemonic = false
            };

            Label heroSub = new Label
            {
                Text = "Editing evolved  •  Savatarr",
                AutoSize = true,
                Location = new Point(76, 52),
                Font = new Font("Segoe UI", 10.0f),
                UseMnemonic = false
            };

            Label startHeader = new Label
            {
                Text = "Start",
                AutoSize = true,
                Location = new Point(26, 92),
                Font = new Font("Segoe UI Semibold", 10.0f, FontStyle.Bold),
                UseMnemonic = false
            };

            Panel startLinksPanel = new Panel
            {
                Location = new Point(26, 116),
                Size = new Size(248, 128),
                BackColor = Color.Transparent
            };

            Func<string, string, string, int, Action, Panel> createStartLink = (iconName, labelText, shortcutText, topY, onClick) =>
            {
                Panel row = new Panel
                {
                    Location = new Point(0, topY),
                    Size = new Size(246, 24),
                    Cursor = Cursors.Hand,
                    BackColor = Color.Transparent,
                    AccessibleName = labelText,
                    AccessibleDescription = shortcutText ?? ""
                };
                bool rowHover = false;
                row.MouseEnter += (s, e) => { rowHover = true; row.Invalidate(); };
                row.MouseLeave += (s, e) => { rowHover = false; row.Invalidate(); };
                row.Click += (s, e) => { if (onClick != null) onClick(); };
                row.Paint += (s, e) =>
                {
                    StudioTheme t = Theme ?? StudioTheme.GetBuiltInThemes()[0];
                    Color linkCol = rowHover ? StudioTheme.Blend(t.AccentPrimary, Color.White, 0.25) : t.AccentPrimary;
                    CodeLessVectorIcons.DrawIcon(e.Graphics, iconName, new Rectangle(2, 4, 15, 15), linkCol, 1.5f);
                    using (Font lf = new Font("Segoe UI", 9.0f, rowHover ? FontStyle.Underline : FontStyle.Regular))
                    {
                        TextRenderer.DrawText(e.Graphics, labelText, lf,
                            new Point(22, 3), linkCol, TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix);
                        if (!string.IsNullOrEmpty(shortcutText))
                        {
                            Size lblSz = TextRenderer.MeasureText(labelText, lf, new Size(300, 24), TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix);
                            using (Font sf = new Font("Segoe UI", 8.2f))
                            {
                                TextRenderer.DrawText(e.Graphics, shortcutText, sf,
                                    new Point(28 + lblSz.Width, 4), t.FgMuted, TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix);
                            }
                        }
                    }
                };
                return row;
            };

            startLinksPanel.Controls.Add(createStartLink("plus", "New File...", "Ctrl+N", 0, () => { if (OnNewFileClicked != null) OnNewFileClicked(); }));
            startLinksPanel.Controls.Add(createStartLink("explorer", "Open File...", "Ctrl+O", 25, () =>
            {
                if (OnOpenFileClicked != null) OnOpenFileClicked();
                else if (OnNewFileClicked != null) OnNewFileClicked();
            }));
            startLinksPanel.Controls.Add(createStartLink("explorer", "Open Folder...", "Ctrl+K Ctrl+O", 50, () => { if (OnOpenFolderClicked != null) OnOpenFolderClicked(); }));
            startLinksPanel.Controls.Add(createStartLink("git", "Clone Git Repository...", "", 75, () =>
            {
                if (OnCloneRepositoryClicked != null) OnCloneRepositoryClicked();
                else if (OnOpenFolderClicked != null) OnOpenFolderClicked();
            }));
            startLinksPanel.Controls.Add(createStartLink("harness", "Create New Project...", "", 100, () => { if (OnNewProjectWizardClicked != null) OnNewProjectWizardClicked(); }));

            Label recentHeader = new Label
            {
                Text = "Recent",
                AutoSize = true,
                Location = new Point(26, 250),
                Font = new Font("Segoe UI Semibold", 10.0f, FontStyle.Bold),
                UseMnemonic = false
            };

            recentListPanel = new FlowLayoutPanel
            {
                Location = new Point(26, 272),
                Size = new Size(256, 130),
                FlowDirection = FlowDirection.TopDown,
                WrapContents = false,
                AutoScroll = false
            };

            Label walkthroughsHeader = new Label
            {
                Text = "Walkthroughs",
                AutoSize = true,
                Location = new Point(296, 92),
                Font = new Font("Segoe UI Semibold", 10.0f, FontStyle.Bold),
                UseMnemonic = false
            };

            Panel wOpenFolderBtn = CreateWelcomeActionCard("explorer", "Open a Workspace Folder", "Browse files, symbols, Git status, and ConPTY terminal", new Point(296, 116), new Size(300, 64), true, () => { if (OnOpenFolderClicked != null) OnOpenFolderClicked(); });
            Panel wByokBtn = CreateWelcomeActionCard("key", "Configure AI Providers & BYOK Keys", "Connect OpenAI, Anthropic, Gemini, DeepSeek, Groq, or Ollama", new Point(296, 188), new Size(300, 64), false, () => { if (OnConfigureByokClicked != null) OnConfigureByokClicked(); });
            Panel wSandboxBtn = CreateWelcomeActionCard("agent", "Agentic Coding, Ctrl+K & Tab Completion", "Try multi-file inline diffs, streaming chat, and ghost completions", new Point(296, 260), new Size(300, 64), false, () => { if (OnLaunchDemoSandboxClicked != null) OnLaunchDemoSandboxClicked(); });

            Func<Panel, int, int> calcCardHeight = (cardCtrl, cardW) =>
            {
                int textW = Math.Max(120, cardW - 44);
                string cTitle = cardCtrl.AccessibleName ?? "";
                string cSub = cardCtrl.AccessibleDescription ?? "";
                using (Font tf = new Font("Segoe UI Semibold", 8.8f, FontStyle.Bold))
                using (Font sf = new Font("Segoe UI", 8.0f))
                {
                    Size tSz = TextRenderer.MeasureText(cTitle, tf, new Size(textW, 60), TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix);
                    Size sSz = TextRenderer.MeasureText(cSub, sf, new Size(textW, 120), TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix);
                    return Math.Max(62, 8 + tSz.Height + 3 + sSz.Height + 9);
                }
            };

            relayoutWelcomePanel = () =>
            {
                int clientW = Math.Max(300, welcomePanel.ClientSize.Width);
                int clientH = Math.Max(220, welcomePanel.ClientSize.Height);
                bool compactV = clientH < 450;
                int leftX = clientW >= 680 ? 34 : (clientW >= 590 ? 22 : 14);

                int heroTop = compactV ? 10 : 22;
                int subTop = compactV ? 44 : 60;
                int secHeaderTop = compactV ? 72 : 98;
                int secBodyTop = compactV ? 94 : 122;
                int recentHeaderTop = compactV ? 224 : 258;
                int recentBodyTop = compactV ? 246 : 282;
                int recentRowCount = Math.Max(1, recentListPanel.Controls.Count);
                int recentPanelH = Math.Max(32, recentRowCount * 24 + 4);

                int contentBottom = 0;

                if (clientW >= 530)
                {
                    int leftColW = clientW >= 590
                        ? Math.Max(252, Math.Min(320, (int)(clientW * 0.43)))
                        : Math.Max(236, (int)(clientW * 0.435));
                    int colGap = clientW >= 590 ? 14 : 10;
                    int rightMargin = clientW >= 590 ? 16 : 12;
                    int rightColX = leftX + leftColW + colGap;
                    int rightColW = Math.Max(200, Math.Min(460, clientW - rightColX - rightMargin));

                    startLinksPanel.Width = Math.Min(248, leftColW);
                    recentListPanel.Size = new Size(leftColW, recentPanelH);

                    int h1 = calcCardHeight(wOpenFolderBtn, rightColW);
                    int h2 = calcCardHeight(wByokBtn, rightColW);
                    int h3 = calcCardHeight(wSandboxBtn, rightColW);
                    int cardGap = compactV ? 8 : 10;

                    wOpenFolderBtn.Size = new Size(rightColW, h1);
                    wByokBtn.Size = new Size(rightColW, h2);
                    wSandboxBtn.Size = new Size(rightColW, h3);

                    int rightBottom = secBodyTop + h1 + cardGap + h2 + cardGap + h3;
                    int leftBottom = recentBodyTop + recentPanelH;
                    contentBottom = Math.Max(leftBottom, rightBottom) + 14;
                    welcomeContentHeight = contentBottom;

                    int maxScroll = Math.Max(0, welcomeContentHeight - clientH);
                    if (welcomeScrollOffset > maxScroll) welcomeScrollOffset = maxScroll;
                    if (welcomeScrollOffset < 0) welcomeScrollOffset = 0;
                    int sy = -welcomeScrollOffset;

                    heroLogoBox.Location = new Point(leftX - 2, heroTop + sy + 2);
                    heroTitle.Location = new Point(leftX + 46, heroTop + sy - 2);
                    heroSub.Location = new Point(leftX + 48, subTop + sy - 2);
                    startHeader.Location = new Point(leftX, secHeaderTop + sy);
                    startLinksPanel.Location = new Point(leftX, secBodyTop + sy);
                    recentHeader.Location = new Point(leftX, recentHeaderTop + sy);
                    recentListPanel.Location = new Point(leftX, recentBodyTop + sy);

                    walkthroughsHeader.Location = new Point(rightColX, secHeaderTop + sy);
                    int cy = secBodyTop + sy;
                    wOpenFolderBtn.Location = new Point(rightColX, cy);
                    cy += h1 + cardGap;
                    wByokBtn.Location = new Point(rightColX, cy);
                    cy += h2 + cardGap;
                    wSandboxBtn.Location = new Point(rightColX, cy);
                }
                else
                {
                    int fullW = Math.Max(260, clientW - leftX * 2 - 12);
                    recentListPanel.Size = new Size(fullW, recentPanelH);

                    int h1 = calcCardHeight(wOpenFolderBtn, fullW);
                    int h2 = calcCardHeight(wByokBtn, fullW);
                    int h3 = calcCardHeight(wSandboxBtn, fullW);
                    int cardGap = 8;

                    wOpenFolderBtn.Size = new Size(fullW, h1);
                    wByokBtn.Size = new Size(fullW, h2);
                    wSandboxBtn.Size = new Size(fullW, h3);

                    int walkHeaderTop = recentBodyTop + recentPanelH + 14;
                    int walkBodyTop = walkHeaderTop + 22;
                    contentBottom = walkBodyTop + h1 + cardGap + h2 + cardGap + h3 + 18;
                    welcomeContentHeight = contentBottom;

                    int maxScroll = Math.Max(0, welcomeContentHeight - clientH);
                    if (welcomeScrollOffset > maxScroll) welcomeScrollOffset = maxScroll;
                    if (welcomeScrollOffset < 0) welcomeScrollOffset = 0;
                    int sy = -welcomeScrollOffset;

                    heroLogoBox.Location = new Point(leftX - 2, heroTop + sy + 2);
                    heroTitle.Location = new Point(leftX + 46, heroTop + sy - 2);
                    heroSub.Location = new Point(leftX + 48, subTop + sy - 2);
                    startHeader.Location = new Point(leftX, secHeaderTop + sy);
                    startLinksPanel.Location = new Point(leftX, secBodyTop + sy);
                    recentHeader.Location = new Point(leftX, recentHeaderTop + sy);
                    recentListPanel.Location = new Point(leftX, recentBodyTop + sy);

                    walkthroughsHeader.Location = new Point(leftX, walkHeaderTop + sy);
                    int cy = walkBodyTop + sy;
                    wOpenFolderBtn.Location = new Point(leftX, cy);
                    cy += h1 + cardGap;
                    wByokBtn.Location = new Point(leftX, cy);
                    cy += h2 + cardGap;
                    wSandboxBtn.Location = new Point(leftX, cy);
                }

                UpdateRecentButtonsTextAndWidth();
                heroLogoBox.Invalidate();
                wOpenFolderBtn.Invalidate();
                wByokBtn.Invalidate();
                wSandboxBtn.Invalidate();
                welcomePanel.Invalidate();
            };

            MouseEventHandler onWelcomeWheel = (s, e) =>
            {
                int maxScroll = Math.Max(0, welcomeContentHeight - welcomePanel.ClientSize.Height);
                if (maxScroll <= 0) return;
                int nextOffset = Math.Max(0, Math.Min(maxScroll, welcomeScrollOffset - (e.Delta / 3)));
                if (nextOffset != welcomeScrollOffset)
                {
                    welcomeScrollOffset = nextOffset;
                    relayoutWelcomePanel();
                }
            };
            welcomePanel.MouseWheel += onWelcomeWheel;
            startLinksPanel.MouseWheel += onWelcomeWheel;
            recentListPanel.MouseWheel += onWelcomeWheel;
            wOpenFolderBtn.MouseWheel += onWelcomeWheel;
            wByokBtn.MouseWheel += onWelcomeWheel;
            wSandboxBtn.MouseWheel += onWelcomeWheel;

            bool isDraggingWelcomeScrollbar = false;
            Action<int> updateWelcomeScrollFromMouseY = (mouseY) =>
            {
                int clientH = welcomePanel.ClientSize.Height;
                int maxScroll = Math.Max(0, welcomeContentHeight - clientH);
                if (maxScroll <= 0 || clientH <= 40) return;
                int trackH = clientH - 8;
                int thumbH = Math.Max(28, (int)(trackH * ((double)clientH / welcomeContentHeight)));
                double ratio = Math.Max(0.0, Math.Min(1.0, (double)(mouseY - 4 - thumbH / 2) / Math.Max(1, trackH - thumbH)));
                int nextOffset = (int)Math.Round(ratio * maxScroll);
                if (nextOffset != welcomeScrollOffset)
                {
                    welcomeScrollOffset = nextOffset;
                    relayoutWelcomePanel();
                }
            };
            welcomePanel.MouseDown += (s, e) =>
            {
                if (e.Button == MouseButtons.Left && welcomeContentHeight > welcomePanel.ClientSize.Height && e.X >= welcomePanel.ClientSize.Width - 14)
                {
                    isDraggingWelcomeScrollbar = true;
                    updateWelcomeScrollFromMouseY(e.Y);
                }
            };
            welcomePanel.MouseMove += (s, e) =>
            {
                if (isDraggingWelcomeScrollbar && (e.Button & MouseButtons.Left) != 0)
                    updateWelcomeScrollFromMouseY(e.Y);
            };
            welcomePanel.MouseUp += (s, e) => { isDraggingWelcomeScrollbar = false; };

            welcomePanel.Paint += (s, e) =>
            {
                int clientH = welcomePanel.ClientSize.Height;
                if (welcomeContentHeight > clientH && clientH > 40)
                {
                    StudioTheme t = Theme ?? StudioTheme.GetBuiltInThemes()[0];
                    int trackW = 6;
                    int trackX = welcomePanel.ClientSize.Width - trackW - 3;
                    int trackH = clientH - 8;
                    int thumbH = Math.Max(28, (int)(trackH * ((double)clientH / welcomeContentHeight)));
                    int maxScroll = Math.Max(1, welcomeContentHeight - clientH);
                    int thumbY = 4 + (int)((trackH - thumbH) * ((double)welcomeScrollOffset / maxScroll));
                    using (SolidBrush tb = new SolidBrush(Color.FromArgb(110, t.FgMuted)))
                        e.Graphics.FillRectangle(tb, trackX, thumbY, trackW, thumbH);
                }
            };

            welcomePanel.Resize += (s, e) => relayoutWelcomePanel();

            welcomePanel.Controls.AddRange(new Control[]
            {
                heroLogoBox, heroTitle, heroSub,
                startHeader, startLinksPanel,
                recentHeader, recentListPanel,
                walkthroughsHeader, wOpenFolderBtn, wByokBtn, wSandboxBtn
            });
        }

        private Panel CreateWelcomeActionCard(string iconKind, string title, string subtitle, Point loc, Size sz, bool accentHighlight, Action onClick)
        {
            Panel card = new Panel
            {
                Location = loc,
                Size = sz,
                Cursor = Cursors.Hand,
                Tag = accentHighlight ? "card-accent" : "card",
                AccessibleName = title,
                AccessibleDescription = subtitle
            };
            bool hovered = false;
            card.MouseEnter += (s, e) => { hovered = true; card.Invalidate(); };
            card.MouseLeave += (s, e) => { hovered = false; card.Invalidate(); };
            card.Click += (s, e) => { if (onClick != null) onClick(); };
            card.Paint += (s, e) =>
            {
                StudioTheme t = Theme ?? StudioTheme.GetBuiltInThemes()[0];
                Color bg = hovered ? t.BgHover : t.BgElevated;
                e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
                using (GraphicsPath gp = StudioTheme.CreateRoundRect(new Rectangle(0, 0, card.Width - 1, card.Height - 1), 6))
                {
                    using (SolidBrush b = new SolidBrush(bg))
                        e.Graphics.FillPath(b, gp);
                    Color borderCol = hovered ? t.AccentPrimary : (accentHighlight ? StudioTheme.Blend(bg, t.AccentPrimary, 0.45) : t.BorderSubtle);
                    using (Pen p = new Pen(borderCol, 1f))
                        e.Graphics.DrawPath(p, gp);
                }
                e.Graphics.SmoothingMode = SmoothingMode.Default;
                if (accentHighlight)
                {
                    using (SolidBrush bar = new SolidBrush(t.AccentPrimary))
                        e.Graphics.FillRectangle(bar, 0, 4, 3, card.Height - 8);
                }

                CodeLessVectorIcons.DrawIcon(e.Graphics, iconKind, new Rectangle(11, (card.Height - 18) / 2, 18, 18), accentHighlight ? t.AccentPrimary : t.FgSecondary, 1.6f);
                int textX = 36;
                int textW = Math.Max(80, card.Width - textX - 8);
                using (Font tf = new Font("Segoe UI Semibold", 8.8f, FontStyle.Bold))
                using (Font sf = new Font("Segoe UI", 8.0f))
                {
                    Size titleSz = TextRenderer.MeasureText(title, tf, new Size(textW, 56), TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix);
                    int titleH = Math.Max(17, titleSz.Height);
                    TextRenderer.DrawText(e.Graphics, title, tf,
                        new Rectangle(textX, 7, textW, titleH), t.FgPrimary, TextFormatFlags.Left | TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix);
                    int subTop = 7 + titleH + 2;
                    TextRenderer.DrawText(e.Graphics, subtitle, sf,
                        new Rectangle(textX, subTop, textW, Math.Max(20, card.Height - subTop - 4)), t.FgSecondary, TextFormatFlags.Left | TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix);
                }
            };
            return card;
        }

        private void UpdateRecentButtonsTextAndWidth()
        {
            if (recentListPanel == null) return;
            int btnW = Math.Max(236, recentListPanel.Width - 4);
            using (Font fName = new Font("Segoe UI Semibold", 9.0f, FontStyle.Regular))
            using (Font fPath = new Font("Segoe UI", 8.5f))
            {
                foreach (Control rc in recentListPanel.Controls)
                {
                    Button rb = rc as Button;
                    if (rb == null) continue;
                    rb.Width = btnW;
                    string namePart = rb.AccessibleName ?? "";
                    string parentDir = rb.AccessibleDescription ?? "";
                    if (!string.IsNullOrEmpty(namePart))
                    {
                        Size nSz = TextRenderer.MeasureText(namePart, fName, new Size(400, 24), TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix);
                        int availPathW = Math.Max(40, btnW - nSz.Width - 16);
                        string displayPath = FormatFittingRecentPath(parentDir, fPath, availPathW);
                        rb.Text = namePart + "  " + displayPath;
                    }
                    rb.Invalidate();
                }
            }
        }

        public void SetRecentWorkspaces(List<string> recentPaths)
        {
            if (recentListPanel == null) return;
            recentListPanel.Controls.Clear();

            if (recentPaths == null || recentPaths.Count == 0)
            {
                Label emptyLbl = new Label
                {
                    Text = "You have no recent folders.",
                    AutoSize = true,
                    ForeColor = Theme != null ? Theme.FgMuted : Color.Gray,
                    Font = new Font("Segoe UI", 9.0f)
                };
                recentListPanel.Controls.Add(emptyLbl);
                if (relayoutWelcomePanel != null) relayoutWelcomePanel();
                return;
            }

            HashSet<string> seenRecentKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            int btnW = Math.Max(236, recentListPanel.Width - 4);
            foreach (string path in recentPaths)
            {
                if (seenRecentKeys.Count >= 5) break;
                if (!Directory.Exists(path) && !File.Exists(path)) continue;
                string captured = path;
                string namePart = Path.GetFileName(captured.TrimEnd('\\', '/'));
                if (string.IsNullOrEmpty(namePart)) namePart = captured;
                string parentDir = Path.GetDirectoryName(captured.TrimEnd('\\', '/'));
                string fullShortPath = FormatShortenedHomePath(captured);
                if (!seenRecentKeys.Add(namePart + "|" + fullShortPath)) continue;
                string targetDirForDisplay = !string.IsNullOrEmpty(parentDir) ? parentDir : captured;

                string initialDisplayPath;
                using (Font fName = new Font("Segoe UI Semibold", 9.0f, FontStyle.Regular))
                using (Font fPath = new Font("Segoe UI", 8.5f))
                {
                    Size nSz = TextRenderer.MeasureText(namePart, fName, new Size(400, 24), TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix);
                    int availPathW = Math.Max(40, btnW - nSz.Width - 16);
                    initialDisplayPath = FormatFittingRecentPath(targetDirForDisplay, fPath, availPathW);
                }

                Button rb = new Button
                {
                    Text = namePart + "  " + initialDisplayPath,
                    AccessibleName = namePart,
                    AccessibleDescription = targetDirForDisplay,
                    Size = new Size(btnW, 23),
                    FlatStyle = FlatStyle.Flat,
                    TextAlign = ContentAlignment.MiddleLeft,
                    BackColor = Color.Transparent,
                    ForeColor = Theme != null ? Theme.AccentPrimary : Color.CornflowerBlue,
                    Font = new Font("Segoe UI", 8.5f),
                    Cursor = Cursors.Hand,
                    Margin = new Padding(0, 0, 0, 1)
                };
                rb.FlatAppearance.BorderSize = 0;
                rb.FlatAppearance.MouseOverBackColor = Theme != null ? Theme.BgHover : Color.FromArgb(40, 40, 48);
                bool rbHover = false;
                rb.MouseEnter += (s, e) => { rbHover = true; rb.Invalidate(); };
                rb.MouseLeave += (s, e) => { rbHover = false; rb.Invalidate(); };
                rb.Paint += (s, e) =>
                {
                    StudioTheme t = Theme ?? StudioTheme.GetBuiltInThemes()[0];
                    e.Graphics.Clear(rbHover ? t.BgHover : t.BgEditor);
                    using (Font fName = new Font("Segoe UI Semibold", 9.0f, rbHover ? FontStyle.Underline : FontStyle.Regular))
                    using (Font fPath = new Font("Segoe UI", 8.5f))
                    {
                        Size nSz = TextRenderer.MeasureText(namePart, fName, new Size(400, 24), TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix);
                        int availPathW = Math.Max(40, rb.Width - nSz.Width - 16);
                        string displayPath = FormatFittingRecentPath(targetDirForDisplay, fPath, availPathW);
                        TextRenderer.DrawText(e.Graphics, namePart, fName, new Point(2, 3), t.AccentPrimary, TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix);
                        TextRenderer.DrawText(e.Graphics, displayPath, fPath, new Point(10 + nSz.Width, 4), t.FgMuted, TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix);
                    }
                };
                rb.Click += (s, e) =>
                {
                    if (OnOpenRecentWorkspaceClicked != null)
                        OnOpenRecentWorkspaceClicked(captured);
                };
                recentListPanel.Controls.Add(rb);
            }

            if (relayoutWelcomePanel != null) relayoutWelcomePanel();
        }

        private Button CreateStudioButton(string text, int width, bool isPrimary)
        {
            Button btn = new Button
            {
                Text = text,
                FlatStyle = FlatStyle.Flat,
                Size = new Size(width, 22),
                Font = new Font("Segoe UI Semibold", 8.2f, FontStyle.Bold),
                Cursor = Cursors.Hand,
                Tag = isPrimary ? "primary" : "subtle"
            };
            btn.FlatAppearance.BorderSize = 0;
            return btn;
        }

        private void LayoutBreadcrumbButtons()
        {
            if (breadcrumbBar == null || pathLabel == null) return;
            // Keep legacy hidden buttons updated for reflection tests without showing them in the breadcrumb bar
            bool isDirty = CurrentBuffer != null && CurrentBuffer.IsDirty;
            if (saveBtn != null)
            {
                saveBtn.Text = isDirty ? "Save* (Ctrl+S)" : "Saved";
                saveBtn.Visible = false;
            }
            if (ctrlKBtn != null) ctrlKBtn.Visible = false;
            if (pinContextBtn != null) pinContextBtn.Visible = false;
            if (findToggleBtn != null) findToggleBtn.Visible = false;
            if (splitViewBtn != null) splitViewBtn.Visible = false;
            if (runFileBtn != null) runFileBtn.Visible = false;

            int availRight = breadcrumbBar.Width - 10;
            int desiredPathW = TextRenderer.MeasureText(pathLabel.Text ?? "", pathLabel.Font, new Size(900, 22), TextFormatFlags.NoPadding).Width + 8;

            if (CurrentBuffer != null && CurrentBuffer.Symbols != null && CurrentBuffer.Symbols.Count > 0 && availRight > 180)
            {
                pathLabel.Location = new Point(10, 2);
                pathLabel.Width = Math.Min(desiredPathW, Math.Max(80, availRight - 210));
                symbolOutlineCombo.Visible = true;
                symbolOutlineCombo.Location = new Point(pathLabel.Right + 4, 1);
                symbolOutlineCombo.Width = Math.Min(220, Math.Max(100, availRight - pathLabel.Right - 8));
            }
            else
            {
                symbolOutlineCombo.Visible = false;
                pathLabel.Location = new Point(10, 2);
                pathLabel.Width = Math.Min(desiredPathW, Math.Max(80, availRight - 16));
            }
        }

        private void LayoutDiffBanner()
        {
            if (diffBanner == null || acceptDiffBtn == null || rejectDiffBtn == null || diffSummaryLabel == null) return;
            bool compact = diffBanner.Width < 540;
            acceptDiffBtn.Text = compact ? "Accept All" : "Accept All (Ctrl+Enter)";
            acceptDiffBtn.Width = compact ? 76 : 142;
            rejectDiffBtn.Text = compact ? "Reject All" : "Reject All (Ctrl+Backspace)";
            rejectDiffBtn.Width = compact ? 74 : 162;

            rejectDiffBtn.Location = new Point(diffBanner.Width - rejectDiffBtn.Width - 10, 3);
            acceptDiffBtn.Location = new Point(rejectDiffBtn.Left - acceptDiffBtn.Width - 6, 3);
            if (prevHunkBtn != null && nextHunkBtn != null)
            {
                nextHunkBtn.Location = new Point(acceptDiffBtn.Left - nextHunkBtn.Width - 6, 3);
                prevHunkBtn.Location = new Point(nextHunkBtn.Left - prevHunkBtn.Width - 2, 3);
                diffSummaryLabel.Location = new Point(12, 3);
                diffSummaryLabel.Size = new Size(Math.Max(60, prevHunkBtn.Left - 18), 22);
            }
            else
            {
                diffSummaryLabel.Location = new Point(12, 3);
                diffSummaryLabel.Size = new Size(Math.Max(60, acceptDiffBtn.Left - 20), 22);
            }
        }

        public void ApplyTheme(StudioTheme theme)
        {
            this.Theme = theme;
            this.BackColor = theme.BgEditor;
            WinFormsLargeFileHost.ApplyTheme(this, theme);

            breadcrumbBar.BackColor = theme.BgEditor;
            pathLabel.ForeColor = theme.FgSecondary;
            cursorPosLabel.ForeColor = theme.FgSecondary;
            symbolOutlineCombo.BackColor = theme.BgEditor;
            symbolOutlineCombo.ForeColor = theme.FgPrimary;
            symbolOutlineCombo.BorderColor = theme.BgEditor;
            symbolOutlineCombo.HoverBackColor = theme.BgHover;

            findBar.BackColor = theme.BgSidebar;
            findInput.ApplyTheme(theme);
            replaceInput.ApplyTheme(theme);
            findStatusLabel.ForeColor = theme.AccentPrimary;

            diffBanner.BackColor = StudioTheme.Blend(theme.BgSidebar, Color.FromArgb(16, 185, 129), 0.16);
            diffSummaryLabel.ForeColor = theme.IsLight ? Color.FromArgb(4, 120, 87) : Color.FromArgb(110, 231, 183);
            if (prevHunkBtn != null)
            {
                prevHunkBtn.BackColor = theme.IsLight ? theme.BgElevated : Color.FromArgb(45, 52, 66);
                prevHunkBtn.ForeColor = theme.IsLight ? theme.FgPrimary : Color.White;
                prevHunkBtn.Invalidate();
            }
            if (nextHunkBtn != null)
            {
                nextHunkBtn.BackColor = theme.IsLight ? theme.BgElevated : Color.FromArgb(45, 52, 66);
                nextHunkBtn.ForeColor = theme.IsLight ? theme.FgPrimary : Color.White;
                nextHunkBtn.Invalidate();
            }
            if (rejectDiffBtn != null)
            {
                rejectDiffBtn.BackColor = theme.IsLight ? theme.BgElevated : Color.FromArgb(65, 72, 88);
                rejectDiffBtn.ForeColor = theme.IsLight ? theme.FgPrimary : Color.White;
                rejectDiffBtn.FlatAppearance.BorderSize = theme.IsLight ? 1 : 0;
                rejectDiffBtn.FlatAppearance.BorderColor = theme.BorderSubtle;
            }
            ctrlKPanel.BackColor = theme.BgSidebar;
            ctrlKInput.ApplyTheme(theme);

            if (watermarkPanel != null)
            {
                watermarkPanel.BackColor = theme.BgEditor;
                watermarkPanel.Invalidate();
            }
            welcomePanel.BackColor = theme.BgEditor;
            foreach (Control wc in welcomePanel.Controls)
            {
                if (wc is Label)
                {
                    wc.ForeColor = wc.Font.Size > 16 ? theme.FgPrimary : (wc.Font.Bold ? theme.AccentPrimary : theme.FgSecondary);
                }
                else if (wc is Panel)
                {
                    wc.Invalidate();
                }
            }

            foreach (Control c in breadcrumbBar.Controls)
                if (c is Button) StyleButton((Button)c, theme);
            foreach (Control c in findBar.Controls)
            {
                if (c is Button) StyleButton((Button)c, theme);
                else if (c is Label) c.ForeColor = theme.FgSecondary;
            }
            foreach (Control c in ctrlKPanel.Controls)
            {
                if (c is Button) StyleButton((Button)c, theme);
                else if (c is Label) c.ForeColor = theme.AccentPrimary;
                else if (c is FlowLayoutPanel)
                {
                    foreach (Control chip in c.Controls)
                        if (chip is Button) StyleButton((Button)chip, theme);
                }
            }

            autocompleteFrame.BackColor = theme.BorderSubtle;
            autocompleteList.BackColor = theme.BgElevated;
            autocompleteList.ForeColor = theme.FgPrimary;

            if (virtualCanvas != null)
            {
                virtualCanvas.ApplyTheme(theme);
                virtualCanvas.Invalidate();
            }
        }

        private void StyleButton(Button b, StudioTheme theme)
        {
            if (b == null) return;
            string tag = b.Tag as string;
            if (tag == "primary")
            {
                b.BackColor = theme.AccentPrimary;
                b.ForeColor = Color.White;
                b.FlatAppearance.BorderSize = 0;
            }
            else if (tag == "run")
            {
                b.BackColor = Color.FromArgb(16, 185, 129);
                b.ForeColor = Color.White;
                b.FlatAppearance.BorderSize = 0;
            }
            else
            {
                b.BackColor = theme.BgElevated;
                b.ForeColor = theme.FgPrimary;
                b.FlatAppearance.BorderColor = theme.BorderSubtle;
                b.FlatAppearance.BorderSize = 0;
                b.FlatAppearance.MouseOverBackColor = theme.BgHover;
            }
        }

        public void LoadBuffer(EditorTabBuffer buffer)
        {
            CurrentBuffer = buffer;
            if (WinFormsLargeFileHost.Sync(this, buffer)) return;
            autocompleteFrame.Visible = false;

            if (buffer == null)
            {
                breadcrumbBar.Visible = false;
                editorBody.Visible = false;
                diffBanner.Visible = false;
                if (ShowWelcomePage)
                {
                    if (watermarkPanel != null) watermarkPanel.Visible = false;
                    welcomePanel.Visible = true;
                    welcomePanel.BringToFront();
                }
                else
                {
                    welcomePanel.Visible = false;
                    if (watermarkPanel != null)
                    {
                        watermarkPanel.Visible = true;
                        watermarkPanel.BringToFront();
                        watermarkPanel.Invalidate();
                    }
                }
                if (OnCursorOrSelectionChanged != null) OnCursorOrSelectionChanged(null);
                return;
            }

            if (buffer.Lines == null || buffer.Lines.Count == 0 || (buffer.Lines.Count == 1 && buffer.Lines[0].Length == 0 && !string.IsNullOrEmpty(buffer.Content)))
            {
                buffer.SyncLinesFromContent();
            }
            if (!buffer.IsLargeFile && (buffer.Content == null || buffer.Content.Length <= 512 * 1024))
            {
                buffer.RefreshGutterDiff();
                LlmHarnessAndAgentEngine.AnalyzeBufferAstAndDiagnostics(buffer);
            }

            breadcrumbBar.Visible = showBreadcrumbs;
            welcomePanel.Visible = false;
            if (watermarkPanel != null) watermarkPanel.Visible = false;
            editorBody.Visible = true;
            editorBody.BringToFront();
            runFileBtn.Enabled = true;
            saveBtn.Enabled = true;
            ctrlKBtn.Enabled = true;
            pinContextBtn.Enabled = true;
            findToggleBtn.Enabled = true;

            UpdateBreadcrumbTitle();
            RefreshSymbolCombo();
            LayoutBreadcrumbButtons();
            UpdateCursorStatusLabel();
            UpdateDiffBannerState();

            virtualCanvas.Invalidate();
        }

        // Lightweight repaint after a background (auto)save of the current buffer: unlike LoadBuffer it keeps the
        // autocomplete popup, symbol dropdown selection and view state intact so typing is not interrupted.
        public void RefreshAfterBackgroundSave()
        {
            if (CurrentBuffer == null) return;
            UpdateBreadcrumbTitle();
            UpdateDiffBannerState();
            virtualCanvas.Invalidate();
        }

        private bool suppressSymbolOutlineEvent = false;

        public void RefreshSymbolCombo()
        {
            if (CurrentBuffer == null || CurrentBuffer.Symbols == null || CurrentBuffer.Symbols.Count == 0)
            {
                symbolOutlineCombo.Visible = false;
                return;
            }
            suppressSymbolOutlineEvent = true;
            try
            {
                symbolOutlineCombo.Items.Clear();
                foreach (AstSymbolItem s in CurrentBuffer.Symbols)
                    symbolOutlineCombo.Items.Add("›  " + s.Name);
                if (symbolOutlineCombo.Items.Count > 0)
                    symbolOutlineCombo.SelectedIndex = 0;
            }
            finally
            {
                suppressSymbolOutlineEvent = false;
            }
            LayoutBreadcrumbButtons();
        }

        public void JumpToLine(int line1Based)
        {
            JumpToLineAndColumn(line1Based, 1);
        }

        public void JumpToLineAndColumn(int line1Based, int col1Based)
        {
            if (CurrentBuffer == null) return;
            if (WinFormsLargeFileHost.TryGoToLine(this, line1Based, col1Based)) return;
            int idx = Math.Max(0, Math.Min(CurrentBuffer.Lines.Count - 1, line1Based - 1));
            CurrentBuffer.CursorLine = idx;
            string ln = CurrentBuffer.Lines[idx] ?? "";
            CurrentBuffer.CursorCol = Math.Max(0, Math.Min(ln.Length, Math.Max(1, col1Based) - 1));
            CurrentBuffer.ClearSelection();
            virtualCanvas.EnsureCursorVisible();
            virtualCanvas.Invalidate();
            virtualCanvas.Focus();
            UpdateCursorStatusLabel();
        }

        private void UpdateDiffBannerState()
        {
            if (CurrentBuffer == null || CurrentBuffer.DiffHunks == null || !CurrentBuffer.DiffHunks.Any(h => h.IsPending))
            {
                diffBanner.Visible = false;
                return;
            }

            diffBanner.Visible = true;
            int pending = CurrentBuffer.DiffHunks.Count(h => h.IsPending);
            int addedCount = CurrentBuffer.DiffHunks.Where(h => h.IsPending).Sum(h => h.AddedLines.Count);
            int removedCount = CurrentBuffer.DiffHunks.Where(h => h.IsPending).Sum(h => h.RemovedLines.Count);

            string filesSummary = (StagedChangedFiles != null && StagedChangedFiles.Count > 1)
                ? string.Format("{0} files ({1})", StagedChangedFiles.Count, string.Join(", ", StagedChangedFiles.Take(3)))
                : CurrentBuffer.FileName;

            diffSummaryLabel.Text = string.Format(
                "{0} {1} in {2}",
                pending, pending == 1 ? "change" : "changes", filesSummary
            );
            LayoutDiffBanner();
        }

        public void NavigateDiffHunk(int direction)
        {
            if (CurrentBuffer == null || CurrentBuffer.DiffHunks == null) return;
            var pending = CurrentBuffer.DiffHunks.Where(h => h.IsPending).OrderBy(h => h.StartLine).ToList();
            if (pending.Count == 0) return;
            int curLine = CurrentBuffer.CursorLine;
            if (direction >= 0)
            {
                var next = pending.FirstOrDefault(h => h.StartLine > curLine) ?? pending[0];
                JumpToLine(next.StartLine + 1);
            }
            else
            {
                var prev = pending.LastOrDefault(h => h.StartLine < curLine) ?? pending[pending.Count - 1];
                JumpToLine(prev.StartLine + 1);
            }
        }

        public void AcceptSingleHunk(EditorDiffHunk hunk)
        {
            if (CurrentBuffer == null || hunk == null) return;
            hunk.IsPending = false;
            CurrentBuffer.DiffHunks.Remove(hunk);
            if (!CurrentBuffer.DiffHunks.Any(h => h.IsPending))
            {
                AcceptAllHunks();
            }
            else
            {
                UpdateDiffBannerState();
                virtualCanvas.Invalidate();
                if (OnBufferModified != null) OnBufferModified(CurrentBuffer);
            }
        }

        public void RejectSingleHunk(EditorDiffHunk hunk)
        {
            if (CurrentBuffer == null || hunk == null) return;
            CurrentBuffer.PushUndoSnapshot();
            int start = Math.Max(0, Math.Min(CurrentBuffer.Lines.Count, hunk.StartLine));
            int removeCnt = Math.Min(CurrentBuffer.Lines.Count - start, hunk.AddedLines.Count);
            if (removeCnt > 0)
                CurrentBuffer.Lines.RemoveRange(start, removeCnt);
            for (int r = 0; r < hunk.RemovedLines.Count; r++)
                CurrentBuffer.Lines.Insert(start + r, hunk.RemovedLines[r]);

            int lineShift = hunk.RemovedLines.Count - hunk.AddedLines.Count;
            foreach (EditorDiffHunk other in CurrentBuffer.DiffHunks)
            {
                if (other != hunk && other.StartLine > hunk.StartLine)
                    other.StartLine = Math.Max(0, other.StartLine + lineShift);
            }
            hunk.IsPending = false;
            CurrentBuffer.DiffHunks.Remove(hunk);
            CurrentBuffer.SyncContentFromLines();
            if (!CurrentBuffer.DiffHunks.Any(h => h.IsPending))
            {
                CurrentBuffer.DiffHunks.Clear();
                CurrentBuffer.PreDiffContent = null;
                CurrentBuffer.IsDirty = !string.Equals(CurrentBuffer.Content ?? "", CurrentBuffer.SavedContent ?? "", StringComparison.Ordinal);
            }
            LoadBuffer(CurrentBuffer);
            if (OnBufferModified != null) OnBufferModified(CurrentBuffer);
        }

        public void UpdateBreadcrumbTitle()
        {
            if (CurrentBuffer == null) return;
            string rawRel = CurrentBuffer.RelativePath ?? CurrentBuffer.FileName ?? "";
            pathLabel.Text = rawRel.Replace('\\', '/').Replace("/", "  ›  ");
            LayoutBreadcrumbButtons();
        }

        public void ToggleWordWrap()
        {
            if (CurrentBuffer == null) return;
            CurrentBuffer.WordWrap = !CurrentBuffer.WordWrap;
            if (CurrentBuffer.WordWrap) CurrentBuffer.ScrollLeftCol = 0;
            UpdateCursorStatusLabel();
            virtualCanvas.Invalidate();
            if (OnWordWrapChanged != null) OnWordWrapChanged(CurrentBuffer.WordWrap);
        }

        public void TriggerAddNextOccurrenceCursor()
        {
            if (virtualCanvas != null) virtualCanvas.AddNextOccurrenceCursor();
        }

        public void TypeTextInCanvas(string text)
        {
            if (CurrentBuffer == null || virtualCanvas == null || string.IsNullOrEmpty(text)) return;
            CurrentBuffer.PushUndoSnapshot();
            if (CurrentBuffer.HasMultiCursors)
                virtualCanvas.SendMultiCursorText(text);
            else
                virtualCanvas.ReplaceSelectionWithText(text);
            NotifyBufferContentChanged();
            virtualCanvas.EnsureCursorVisible();
            virtualCanvas.Invalidate();
        }

        public void UpdateCursorStatusLabel()
        {
            if (CurrentBuffer == null) return;
            int diagCount = CurrentBuffer.Diagnostics != null ? CurrentBuffer.Diagnostics.Count : 0;
            string diagBadge = diagCount > 0 ? ("  •  ⚠ " + diagCount) : "  •  ✓ 0";
            string selBadge = "";
            if (CurrentBuffer.HasMultiCursors)
            {
                selBadge = string.Format(" ({0} cursors)", 1 + CurrentBuffer.ExtraCarets.Count);
            }
            else if (CurrentBuffer.HasSelection())
            {
                string selTxt = CurrentBuffer.GetSelectedText();
                selBadge = string.Format(" ({0} ch)", selTxt.Length);
            }
            string wrapBadge = CurrentBuffer.WordWrap ? "  •  Wrap" : "";
            cursorPosLabel.Text = string.Format(
                "Ln {0}, Col {1}{2}{3}  •  {4}{5}",
                CurrentBuffer.CursorLine + 1,
                CurrentBuffer.CursorCol + 1,
                selBadge,
                wrapBadge,
                (CurrentBuffer.Language ?? "TXT").ToUpperInvariant(),
                diagBadge
            );
            if (OnCursorOrSelectionChanged != null)
                OnCursorOrSelectionChanged(CurrentBuffer);
        }

        private System.Windows.Forms.Timer largeFileAstDebounceTimer;
        private EditorTabBuffer pendingAstBuffer;

        public void NotifyBufferContentChanged()
        {
            if (CurrentBuffer == null) return;
            bool deferLargeAnalysis = CurrentBuffer.Lines != null && CurrentBuffer.Lines.Count > 350;
            CurrentBuffer.SyncContentFromLines(recomputeGutterDiff: !deferLargeAnalysis);
            if (!deferLargeAnalysis && !CurrentBuffer.IsLargeFile && (CurrentBuffer.Content == null || CurrentBuffer.Content.Length <= 512 * 1024))
            {
                LlmHarnessAndAgentEngine.AnalyzeBufferAstAndDiagnostics(CurrentBuffer);
            }
            else if (deferLargeAnalysis)
            {
                pendingAstBuffer = CurrentBuffer;
                if (largeFileAstDebounceTimer == null)
                {
                    largeFileAstDebounceTimer = new System.Windows.Forms.Timer { Interval = 260 };
                    largeFileAstDebounceTimer.Tick += (s, e) =>
                    {
                        largeFileAstDebounceTimer.Stop();
                        var target = pendingAstBuffer;
                        pendingAstBuffer = null;
                        if (target == null || this.IsDisposed) return;
                        if (target.Lines != null && target.Lines.Count <= 12000)
                            target.RefreshGutterDiff();
                        LlmHarnessAndAgentEngine.AnalyzeBufferAstAndDiagnostics(target);
                        if (ReferenceEquals(target, CurrentBuffer))
                        {
                            LayoutBreadcrumbButtons();
                            UpdateCursorStatusLabel();
                            if (virtualCanvas != null) virtualCanvas.Invalidate();
                        }
                    };
                }
                largeFileAstDebounceTimer.Stop();
                largeFileAstDebounceTimer.Start();
            }

            ghostRequestVersion++;
            if (ghostDebounceTimer != null) ghostDebounceTimer.Stop();

            if (EnableGhostCompletions && !deferLargeAnalysis)
            {
                string ghost = LlmHarnessAndAgentEngine.GenerateInlineGhostCompletion(CurrentBuffer);
                CurrentBuffer.GhostText = ghost;
                CurrentBuffer.GhostLine = CurrentBuffer.CursorLine;
                CurrentBuffer.GhostCol = CurrentBuffer.CursorCol;
                if (AsyncGhostCompletionProvider != null && ghostDebounceTimer != null)
                {
                    ghostDebounceTimer.Start();
                }
            }
            else
            {
                CurrentBuffer.GhostText = null;
            }

            if (!deferLargeAnalysis)
                UpdateBreadcrumbTitle();
            UpdateCursorStatusLabel();
            UpdateAutocompletePopup();
            if (findBar != null && findBar.Visible && !deferLargeAnalysis)
                UpdateSearchMatches(false);
            if (OnBufferModified != null) OnBufferModified(CurrentBuffer);
        }

        public void StageProposedContent(string newContent, string summaryLabel)
        {
            if (CurrentBuffer == null) return;
            if (string.IsNullOrEmpty(CurrentBuffer.PreDiffContent))
                CurrentBuffer.PreDiffContent = CurrentBuffer.Content;

            string normNew = (newContent ?? "").Replace("\r\n", "\n").Replace("\r", "\n");
            if (!CurrentBuffer.IsReadOnlyDiffPreview && !string.IsNullOrEmpty(CurrentBuffer.SavedContent) && normNew.Length > 0)
            {
                if (CurrentBuffer.HasFinalNewline && !normNew.EndsWith("\n"))
                    normNew += "\n";
                else if (!CurrentBuffer.HasFinalNewline && normNew.EndsWith("\n"))
                    normNew = normNew.Substring(0, normNew.Length - 1);
            }

            CurrentBuffer.DiffHunks = LlmHarnessAndAgentEngine.ComputeLineDiffHunks(CurrentBuffer.PreDiffContent, normNew);
            CurrentBuffer.Content = normNew;
            CurrentBuffer.SyncLinesFromContent();
            CurrentBuffer.IsDirty = !string.Equals(CurrentBuffer.Content ?? "", (CurrentBuffer.SavedContent ?? "").Replace("\r\n", "\n").Replace("\r", "\n"), StringComparison.Ordinal);

            LoadBuffer(CurrentBuffer);
            if (CurrentBuffer.DiffHunks != null && CurrentBuffer.DiffHunks.Count > 0)
            {
                JumpToLine(CurrentBuffer.DiffHunks[0].StartLine + 1);
            }
            if (OnBufferModified != null) OnBufferModified(CurrentBuffer);
        }

        public void AcceptAllHunks()
        {
            if (CurrentBuffer == null) return;
            CurrentBuffer.DiffHunks.Clear();
            CurrentBuffer.PreDiffContent = null;
            if (StagedChangedFiles != null && CurrentBuffer.FileName != null)
                StagedChangedFiles.Remove(CurrentBuffer.FileName);
            CurrentBuffer.RefreshGutterDiff();
            diffBanner.Visible = false;
            virtualCanvas.Invalidate();
            if (OnBufferModified != null) OnBufferModified(CurrentBuffer);
        }

        public void RejectAllHunks()
        {
            if (CurrentBuffer == null) return;
            if (CurrentBuffer.PreDiffContent != null)
            {
                CurrentBuffer.Content = CurrentBuffer.PreDiffContent;
                CurrentBuffer.SyncLinesFromContent();
            }
            CurrentBuffer.DiffHunks.Clear();
            CurrentBuffer.PreDiffContent = null;
            if (StagedChangedFiles != null && CurrentBuffer.FileName != null)
                StagedChangedFiles.Remove(CurrentBuffer.FileName);
            CurrentBuffer.IsDirty = !string.Equals(CurrentBuffer.Content ?? "", CurrentBuffer.SavedContent ?? "", StringComparison.Ordinal);
            LoadBuffer(CurrentBuffer);
            if (OnBufferModified != null) OnBufferModified(CurrentBuffer);
        }

        public void ToggleCtrlKBar()
        {
            if (CurrentBuffer == null) return;
            ctrlKPanel.Visible = !ctrlKPanel.Visible;
            if (ctrlKPanel.Visible)
            {
                PositionFloatingCtrlKPanel();
                ctrlKInput.Focus();
                ctrlKInput.SelectAll();
            }
            else
            {
                virtualCanvas.Focus();
            }
        }

        public void ToggleFindBar()
        {
            if (CurrentBuffer == null) return;
            if (!findBar.Visible)
            {
                OpenFindBar(false);
            }
            else
            {
                findBar.Visible = false;
                ActiveSearchMatches.Clear();
                ActiveSearchMatchIndex = -1;
                virtualCanvas.Invalidate();
                virtualCanvas.Focus();
            }
        }

        public void OpenFindBar(bool focusReplace = false)
        {
            if (CurrentBuffer == null) return;
            if (WinFormsLargeFileHost.TryShowFind(this)) return;
            findBar.Visible = true;
            string sel = CurrentBuffer.GetSelectedText();
            if (!string.IsNullOrEmpty(sel) && sel.Length < 80 && !sel.Contains("\n"))
                findInput.Text = sel;
            UpdateSearchMatches(false);
            if (focusReplace)
            {
                replaceInput.Focus();
                replaceInput.SelectAll();
            }
            else
            {
                findInput.Focus();
                findInput.SelectAll();
            }
        }

        private string BuildEffectiveSearchRegexPattern(string q)
        {
            string pat = isRegexSearch ? q : Regex.Escape(q);
            if (isWholeWordSearch)
                pat = @"\b(?:" + pat + @")\b";
            return pat;
        }

        public void UpdateSearchMatches(bool jumpToNearest)
        {
            ActiveSearchMatches.Clear();
            ActiveSearchMatchIndex = -1;
            string q = findInput != null ? findInput.Text : "";
            if (string.IsNullOrEmpty(q) || CurrentBuffer == null || CurrentBuffer.Lines == null)
            {
                if (findStatusLabel != null) findStatusLabel.Text = "";
                if (virtualCanvas != null) virtualCanvas.Invalidate();
                return;
            }

            StringComparison cmp = isCaseSearch ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
            bool useRegex = isRegexSearch || isWholeWordSearch;
            Regex rx = null;
            if (useRegex)
            {
                try { rx = new Regex(BuildEffectiveSearchRegexPattern(q), isCaseSearch ? RegexOptions.None : RegexOptions.IgnoreCase); }
                catch
                {
                    findStatusLabel.Text = "Invalid regex";
                    virtualCanvas.Invalidate();
                    return;
                }
            }

            for (int l = 0; l < CurrentBuffer.Lines.Count; l++)
            {
                string line = CurrentBuffer.Lines[l] ?? "";
                if (line.Length == 0) continue;
                if (useRegex && rx != null)
                {
                    MatchCollection mc = rx.Matches(line);
                    foreach (Match m in mc)
                    {
                        if (m.Success && m.Length > 0)
                            ActiveSearchMatches.Add(new SearchMatch { Line = l, Col = m.Index, Length = m.Length });
                    }
                }
                else
                {
                    int c = 0;
                    while (c <= line.Length - q.Length)
                    {
                        int idx = line.IndexOf(q, c, cmp);
                        if (idx < 0) break;
                        ActiveSearchMatches.Add(new SearchMatch { Line = l, Col = idx, Length = q.Length });
                        c = idx + Math.Max(1, q.Length);
                    }
                }
            }

            if (ActiveSearchMatches.Count == 0)
            {
                findStatusLabel.Text = "No results";
                virtualCanvas.Invalidate();
                return;
            }

            int searchFromCol = CurrentBuffer.HasSelection()
                ? Math.Min(CurrentBuffer.SelectionAnchorCol, CurrentBuffer.CursorCol)
                : CurrentBuffer.CursorCol;
            int bestIdx = 0;
            for (int i = 0; i < ActiveSearchMatches.Count; i++)
            {
                SearchMatch sm = ActiveSearchMatches[i];
                if (sm.Line > CurrentBuffer.CursorLine || (sm.Line == CurrentBuffer.CursorLine && sm.Col >= searchFromCol))
                {
                    bestIdx = i;
                    break;
                }
            }
            ActiveSearchMatchIndex = bestIdx;
            if (jumpToNearest)
            {
                SelectSearchMatch(ActiveSearchMatchIndex);
            }
            else
            {
                findStatusLabel.Text = string.Format("{0} of {1}", ActiveSearchMatchIndex + 1, ActiveSearchMatches.Count);
                virtualCanvas.Invalidate();
            }
        }

        private void SelectSearchMatch(int matchIdx)
        {
            if (CurrentBuffer == null || matchIdx < 0 || matchIdx >= ActiveSearchMatches.Count) return;
            ActiveSearchMatchIndex = matchIdx;
            SearchMatch sm = ActiveSearchMatches[matchIdx];
            CurrentBuffer.SelectionAnchorLine = sm.Line;
            CurrentBuffer.SelectionAnchorCol = sm.Col;
            CurrentBuffer.CursorLine = sm.Line;
            CurrentBuffer.CursorCol = sm.Col + sm.Length;
            virtualCanvas.EnsureCursorVisible();
            virtualCanvas.Invalidate();
            UpdateCursorStatusLabel();
            findStatusLabel.Text = string.Format("{0} of {1}", matchIdx + 1, ActiveSearchMatches.Count);
        }

        public void FindNextOccurrence()
        {
            if (CurrentBuffer == null) return;
            string q = findInput.Text;
            if (string.IsNullOrEmpty(q) && CurrentBuffer.HasSelection())
            {
                string sel = CurrentBuffer.GetSelectedText();
                if (!string.IsNullOrEmpty(sel) && !sel.Contains("\n"))
                    findInput.Text = sel;
                q = findInput.Text;
            }
            if (string.IsNullOrEmpty(q)) return;
            if (ActiveSearchMatches.Count == 0) UpdateSearchMatches(false);
            if (ActiveSearchMatches.Count == 0)
            {
                findStatusLabel.Text = "No results";
                return;
            }
            int nextIdx = 0;
            for (int i = 0; i < ActiveSearchMatches.Count; i++)
            {
                SearchMatch sm = ActiveSearchMatches[i];
                if (sm.Line > CurrentBuffer.CursorLine || (sm.Line == CurrentBuffer.CursorLine && sm.Col >= CurrentBuffer.CursorCol))
                {
                    nextIdx = i;
                    break;
                }
            }
            SelectSearchMatch(nextIdx);
        }

        public void FindPreviousOccurrence()
        {
            if (CurrentBuffer == null) return;
            string q = findInput.Text;
            if (string.IsNullOrEmpty(q) && CurrentBuffer.HasSelection())
            {
                string sel = CurrentBuffer.GetSelectedText();
                if (!string.IsNullOrEmpty(sel) && !sel.Contains("\n"))
                    findInput.Text = sel;
                q = findInput.Text;
            }
            if (string.IsNullOrEmpty(q)) return;
            if (ActiveSearchMatches.Count == 0) UpdateSearchMatches(false);
            if (ActiveSearchMatches.Count == 0)
            {
                findStatusLabel.Text = "No results";
                return;
            }
            int sLine, sCol, eLine, eCol;
            CurrentBuffer.GetNormalizedSelection(out sLine, out sCol, out eLine, out eCol);
            int prevIdx = ActiveSearchMatches.Count - 1;
            for (int i = ActiveSearchMatches.Count - 1; i >= 0; i--)
            {
                SearchMatch sm = ActiveSearchMatches[i];
                if (sm.Line < sLine || (sm.Line == sLine && sm.Col < sCol))
                {
                    prevIdx = i;
                    break;
                }
            }
            SelectSearchMatch(prevIdx);
        }

        public void TriggerReplaceCurrentOccurrence() { ReplaceCurrentOccurrence(); }
        public void TriggerReplaceAllOccurrences() { ReplaceAllOccurrences(); }

        private void ReplaceCurrentOccurrence()
        {
            string q = findInput.Text;
            if (string.IsNullOrEmpty(q) || CurrentBuffer == null) return;
            if (!CurrentBuffer.HasSelection())
            {
                FindNextOccurrence();
                if (!CurrentBuffer.HasSelection()) return;
            }
            string selectedText = CurrentBuffer.GetSelectedText();
            RegexOptions opts = isCaseSearch ? RegexOptions.None : RegexOptions.IgnoreCase;
            StringComparison cmp = isCaseSearch ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
            bool selectionMatches = (isRegexSearch || isWholeWordSearch)
                ? Regex.IsMatch(selectedText, "^(?:" + (isRegexSearch ? q : Regex.Escape(q)) + ")$", opts)
                : string.Equals(selectedText, q, cmp);
            if (!selectionMatches)
            {
                FindNextOccurrence();
                if (!CurrentBuffer.HasSelection()) return;
                selectedText = CurrentBuffer.GetSelectedText();
            }

            string repText = replaceInput.Text ?? "";
            if (isRegexSearch)
            {
                try { repText = Regex.Replace(selectedText, q, repText, opts); }
                catch { }
            }

            CurrentBuffer.PushUndoSnapshot();
            virtualCanvas.ReplaceSelectionWithText(repText);
            NotifyBufferContentChanged();
            UpdateSearchMatches(true);
        }

        public void ReplaceAllOccurrences()
        {
            string q = findInput.Text;
            if (string.IsNullOrEmpty(q) || CurrentBuffer == null) return;
            UpdateSearchMatches(false);
            int countBefore = ActiveSearchMatches.Count;
            if (countBefore == 0)
            {
                findStatusLabel.Text = "No results";
                return;
            }
            CurrentBuffer.PushUndoSnapshot();
            RegexOptions opts = isCaseSearch ? RegexOptions.None : RegexOptions.IgnoreCase;
            string pattern = BuildEffectiveSearchRegexPattern(q);
            try
            {
                string replaced = Regex.Replace(CurrentBuffer.Content ?? "", pattern, replaceInput.Text ?? "", opts);
                if (replaced != CurrentBuffer.Content)
                {
                    CurrentBuffer.Content = replaced;
                    CurrentBuffer.SyncLinesFromContent();
                    NotifyBufferContentChanged();
                    UpdateSearchMatches(false);
                    virtualCanvas.Invalidate();
                    findStatusLabel.Text = string.Format("Replaced {0}", countBefore);
                }
            }
            catch (Exception ex)
            {
                findStatusLabel.Text = "Regex error: " + ex.Message;
            }
        }

        private void SubmitCtrlK()
        {
            string prompt = ctrlKInput.Text.Trim();
            if (string.IsNullOrEmpty(prompt) || CurrentBuffer == null) return;
            ctrlKPanel.Visible = false;
            ctrlKInput.Text = "";
            if (OnCtrlKRequested != null)
            {
                OnCtrlKRequested(prompt, CurrentBuffer);
            }
        }

        /// <summary>Panel hosting the canvas and its floating widgets (LSP completion / hover popups are added here).</summary>
        public Panel EditorBodyHost { get { return editorBody; } }

        /// <summary>When it returns true for the current buffer the word-based popup stays hidden (a language server completes instead).</summary>
        public Func<EditorTabBuffer, bool> SuppressBuiltInAutocomplete;

        public void UpdateAutocompletePopup(bool forceShow = false)
        {
            if (CurrentBuffer == null) { autocompleteFrame.Visible = false; return; }
            if (SuppressBuiltInAutocomplete != null && SuppressBuiltInAutocomplete(CurrentBuffer)) { autocompleteFrame.Visible = false; return; }
            string line = CurrentBuffer.Lines[CurrentBuffer.CursorLine];
            int col = Math.Min(line.Length, CurrentBuffer.CursorCol);
            if (col <= 0 && !forceShow) { autocompleteFrame.Visible = false; return; }

            int start = col - 1;
            while (start >= 0 && (char.IsLetterOrDigit(line[start]) || line[start] == '_'))
                start--;
            start++;

            int len = Math.Max(0, col - start);
            if (len < 2 && !forceShow)
            {
                autocompleteFrame.Visible = false;
                return;
            }

            currentWordPrefix = len > 0 ? line.Substring(start, len) : "";
            var candidates = BuiltInKeywords
                .Concat(AutocompleteSymbols)
                .Concat(CurrentBuffer.Symbols.Select(s => s.Name))
                .Concat(LoadedSnippets.Select(s => s.Prefix + " (snippet)"))
                .Distinct()
                .Where(w => string.IsNullOrEmpty(currentWordPrefix) || (w.StartsWith(currentWordPrefix, StringComparison.OrdinalIgnoreCase) && !string.Equals(w, currentWordPrefix, StringComparison.OrdinalIgnoreCase)))
                .Take(10)
                .ToList();

            if (candidates.Count == 0)
            {
                autocompleteFrame.Visible = false;
                return;
            }

            autocompleteList.Items.Clear();
            foreach (string c in candidates)
                autocompleteList.Items.Add(c);
            autocompleteList.SelectedIndex = 0;

            Point caretPt = virtualCanvas.GetCursorPixelPoint();
            autocompleteFrame.Height = Math.Min(210, Math.Max(48, candidates.Count * 22 + 4));
            autocompleteFrame.Location = new Point(
                Math.Max(68, Math.Min(editorBody.Width - autocompleteFrame.Width - 90, caretPt.X)),
                Math.Max(4, Math.Min(editorBody.Height - autocompleteFrame.Height - 10, caretPt.Y + virtualCanvas.LineHeight + 2))
            );
            autocompleteFrame.Visible = true;
            autocompleteFrame.BringToFront();
        }

        public bool IsAutocompleteVisible
        {
            get { return autocompleteFrame != null && autocompleteFrame.Visible; }
        }

        public void MoveAutocompleteSelection(int delta)
        {
            if (!IsAutocompleteVisible || autocompleteList.Items.Count == 0) return;
            int next = Math.Max(0, Math.Min(autocompleteList.Items.Count - 1, autocompleteList.SelectedIndex + delta));
            autocompleteList.SelectedIndex = next;
        }

        public void DismissAutocomplete()
        {
            if (autocompleteFrame != null) autocompleteFrame.Visible = false;
        }

        public void AcceptSelectedAutocomplete()
        {
            if (!IsAutocompleteVisible || autocompleteList.SelectedItem == null || CurrentBuffer == null) return;
            string chosen = autocompleteList.SelectedItem.ToString();
            if (chosen.EndsWith(" (snippet)"))
            {
                string pref = chosen.Replace(" (snippet)", "");
                var snip = LoadedSnippets.FirstOrDefault(s => s.Prefix == pref);
                if (snip != null) chosen = snip.Body;
            }

            CurrentBuffer.PushUndoSnapshot();
            string line = CurrentBuffer.Lines[CurrentBuffer.CursorLine];
            int col = Math.Min(line.Length, CurrentBuffer.CursorCol);
            int replaceStart = Math.Max(0, col - currentWordPrefix.Length);

            string before = line.Substring(0, replaceStart);
            string after = line.Substring(col);

            if (chosen.Contains("\n"))
            {
                string[] insertLines = chosen.Replace("\r\n", "\n").Split('\n');
                CurrentBuffer.Lines[CurrentBuffer.CursorLine] = before + insertLines[0];
                for (int i = 1; i < insertLines.Length; i++)
                {
                    CurrentBuffer.Lines.Insert(CurrentBuffer.CursorLine + i, insertLines[i] + (i == insertLines.Length - 1 ? after : ""));
                }
                CurrentBuffer.CursorLine += insertLines.Length - 1;
                CurrentBuffer.CursorCol = insertLines[insertLines.Length - 1].Length;
            }
            else
            {
                CurrentBuffer.Lines[CurrentBuffer.CursorLine] = before + chosen + after;
                CurrentBuffer.CursorCol = replaceStart + chosen.Length;
            }

            autocompleteFrame.Visible = false;
            NotifyBufferContentChanged();
            virtualCanvas.Invalidate();
            virtualCanvas.Focus();
        }

        public void TriggerSaveShortcut()
        {
            if (CurrentBuffer != null && OnSaveRequested != null)
                OnSaveRequested(CurrentBuffer);
        }

        public void TriggerRunShortcut()
        {
            if (CurrentBuffer != null && OnRunFileRequested != null)
                OnRunFileRequested(CurrentBuffer);
        }

        public void PerformSplitToggle()
        {
            if (OnToggleSplitEditorRequested != null)
                OnToggleSplitEditorRequested();
        }

        /// <summary>Resolves the Ctrl+Click target at a position (never throws).</summary>
        public EditorLinkTarget ResolveLinkAt(EditorTabBuffer buf, int line, int col)
        {
            if (buf == null) return null;
            try
            {
                string root = WorkspaceRootProvider != null ? WorkspaceRootProvider() : null;
                if (string.IsNullOrEmpty(root) && !string.IsNullOrEmpty(buf.FilePath)) root = Path.GetDirectoryName(buf.FilePath);
                return EditorLinkResolver.Resolve(buf.Lines, line, col, buf.FilePath, root);
            }
            catch { return null; }
        }

        /// <summary>Follows the link at (line, col): imports open files, identifiers go to their definition. Returns true when handled.</summary>
        public bool TriggerFollowLinkAt(int line, int col)
        {
            EditorTabBuffer buf = CurrentBuffer;
            if (buf == null || OnLinkActivated == null) return false;
            EditorLinkTarget target = ResolveLinkAt(buf, line, col);
            if (target == null && col > 0) target = ResolveLinkAt(buf, line, col - 1);
            if (target == null) return false;
            OnLinkActivated(buf, target);
            return true;
        }

        public void TriggerGoToDefinition(string word)
        {
            if (!string.IsNullOrEmpty(word) && OnGoToDefinitionRequested != null)
                OnGoToDefinitionRequested(word);
        }

        public void TriggerGoToReferences(string word)
        {
            if (CurrentBuffer != null && !string.IsNullOrEmpty(word) && OnGoToReferencesRequested != null)
                OnGoToReferencesRequested(CurrentBuffer, word);
            else if (!string.IsNullOrEmpty(word))
                TriggerGoToDefinition(word);
        }

        public void TriggerPeekDefinition(string word)
        {
            if (CurrentBuffer != null && !string.IsNullOrEmpty(word) && OnPeekDefinitionRequested != null)
                OnPeekDefinitionRequested(CurrentBuffer, word);
            else if (!string.IsNullOrEmpty(word))
                TriggerGoToDefinition(word);
        }

        public void TriggerCommandPalette()
        {
            if (OnCommandPaletteRequested != null)
                OnCommandPaletteRequested();
        }

        public void RaiseChordStatusChanged(string message)
        {
            if (OnChordStatusChanged != null)
                OnChordStatusChanged(message ?? "");
        }

        public void SelectAllFindMatches()
        {
            if (CurrentBuffer == null) return;
            if (ActiveSearchMatches.Count == 0) UpdateSearchMatches(false);
            if (ActiveSearchMatches.Count == 0) return;

            CurrentBuffer.ClearExtraCarets();
            SearchMatch first = ActiveSearchMatches[0];
            CurrentBuffer.SelectionAnchorLine = first.Line;
            CurrentBuffer.SelectionAnchorCol = first.Col;
            CurrentBuffer.CursorLine = first.Line;
            CurrentBuffer.CursorCol = first.Col + first.Length;

            for (int i = 1; i < ActiveSearchMatches.Count; i++)
            {
                SearchMatch sm = ActiveSearchMatches[i];
                CurrentBuffer.ExtraCarets.Add(new EditorCaret
                {
                    AnchorLine = sm.Line,
                    AnchorCol = sm.Col,
                    CursorLine = sm.Line,
                    CursorCol = sm.Col + sm.Length
                });
            }
            if (findBar != null) findBar.Visible = false;
            UpdateCursorStatusLabel();
            if (virtualCanvas != null)
            {
                virtualCanvas.Invalidate();
                virtualCanvas.Focus();
            }
        }

        public void TriggerRenameSymbol(string word)
        {
            if (CurrentBuffer != null && !string.IsNullOrEmpty(word) && OnRenameSymbolRequested != null)
                OnRenameSymbolRequested(CurrentBuffer, word);
        }

        public int RenameSymbolInCurrentBuffer(string oldName, string newName)
        {
            if (CurrentBuffer == null || string.IsNullOrWhiteSpace(oldName) || string.IsNullOrWhiteSpace(newName) || oldName == newName)
                return 0;
            string pattern = @"\b" + Regex.Escape(oldName.Trim()) + @"\b";
            MatchCollection mc = Regex.Matches(CurrentBuffer.Content ?? "", pattern);
            if (mc.Count == 0) return 0;
            CurrentBuffer.PushUndoSnapshot();
            CurrentBuffer.Content = Regex.Replace(CurrentBuffer.Content ?? "", pattern, newName.Trim());
            CurrentBuffer.SyncLinesFromContent();
            NotifyBufferContentChanged();
            if (virtualCanvas != null) virtualCanvas.Invalidate();
            return mc.Count;
        }

        public bool ToggleFoldAtLine(int lineZeroBased)
        {
            return virtualCanvas != null && virtualCanvas.ToggleFoldAtLine(lineZeroBased);
        }

        public void TriggerSendSelectionToAgent()
        {
            if (CurrentBuffer != null && OnSendSelectionToAgentRequested != null)
                OnSendSelectionToAgentRequested(CurrentBuffer);
        }

        public void FormatActiveDocument()
        {
            if (virtualCanvas != null)
                virtualCanvas.FormatDocument();
        }

        public void ToggleCommentActiveDocument()
        {
            if (virtualCanvas != null)
                virtualCanvas.ToggleCommentOnSelection();
        }

        public void ToggleBlockCommentActiveDocument()
        {
            if (virtualCanvas != null)
                virtualCanvas.ToggleBlockCommentOnSelection();
        }

        public void PerformUndo()
        {
            if (CurrentBuffer == null || virtualCanvas == null) return;
            CurrentBuffer.ClearExtraCarets();
            if (CurrentBuffer.Undo())
            {
                NotifyBufferContentChanged();
                virtualCanvas.EnsureCursorVisible();
                UpdateCursorStatusLabel();
                virtualCanvas.Invalidate();
            }
        }

        public void PerformRedo()
        {
            if (CurrentBuffer == null || virtualCanvas == null) return;
            CurrentBuffer.ClearExtraCarets();
            if (CurrentBuffer.Redo())
            {
                NotifyBufferContentChanged();
                virtualCanvas.EnsureCursorVisible();
                UpdateCursorStatusLabel();
                virtualCanvas.Invalidate();
            }
        }

        public void PerformCut() { if (virtualCanvas != null) virtualCanvas.TriggerCut(); }
        public void PerformCopy() { if (virtualCanvas != null) virtualCanvas.TriggerCopy(); }
        public void PerformPaste() { if (virtualCanvas != null) virtualCanvas.TriggerPaste(); }
        public void PerformSelectAll() { if (virtualCanvas != null) virtualCanvas.TriggerSelectAll(); }
        public void ExpandSelection() { if (virtualCanvas != null) virtualCanvas.ExpandSelection(); }
        public void ShrinkSelection() { if (virtualCanvas != null) virtualCanvas.ShrinkSelection(); }
        public void CopyLineUp() { if (virtualCanvas != null) virtualCanvas.CopyLines(true); }
        public void CopyLineDown() { if (virtualCanvas != null) virtualCanvas.CopyLines(false); }
        public void MoveLineUp() { if (virtualCanvas != null) virtualCanvas.MoveLines(true); }
        public void MoveLineDown() { if (virtualCanvas != null) virtualCanvas.MoveLines(false); }
        public void DuplicateSelection() { if (virtualCanvas != null) virtualCanvas.DuplicateSelectionOrLine(); }
        public void AddCursorAbove() { if (virtualCanvas != null) virtualCanvas.AddCursorVertical(-1); }
        public void AddCursorBelow() { if (virtualCanvas != null) virtualCanvas.AddCursorVertical(1); }
        public void SelectAllOccurrences() { if (virtualCanvas != null) virtualCanvas.SelectAllOccurrencesOfWord(); }
        public void ToggleColumnSelectionMode() { IsColumnSelectionMode = !IsColumnSelectionMode; }
        public void GoToMatchingBracket() { if (virtualCanvas != null) virtualCanvas.JumpToMatchingBracket(); }
        public bool AcceptGhostTextNextWord() { return virtualCanvas != null && virtualCanvas.AcceptGhostTextNextWord(); }
        public void SelectCurrentLine() { if (virtualCanvas != null) virtualCanvas.SelectCurrentLine(); }
        public void UndoLastCursorOperation() { if (virtualCanvas != null) virtualCanvas.UndoLastCursorOperation(); }
        public void SkipAndAddNextOccurrenceCursor() { if (virtualCanvas != null) virtualCanvas.SkipAndAddNextOccurrenceCursor(); }
        public void FormatSelection() { if (virtualCanvas != null) virtualCanvas.FormatSelection(); }
        public void AddCursorsToLineEnds() { if (virtualCanvas != null) virtualCanvas.AddCursorsToLineEnds(); }
        public void FoldCurrentRegion() { if (virtualCanvas != null) virtualCanvas.FoldCurrentRegion(); }
        public void UnfoldCurrentRegion() { if (virtualCanvas != null) virtualCanvas.UnfoldCurrentRegion(); }
        public void FoldAllRegions() { if (virtualCanvas != null) virtualCanvas.FoldAllRegions(); }
        public void UnfoldAllRegions() { if (virtualCanvas != null) virtualCanvas.UnfoldAllRegions(); }
        public void InsertLineBelow() { if (virtualCanvas != null) virtualCanvas.InsertBlankLine(false); }
        public void InsertLineAbove() { if (virtualCanvas != null) virtualCanvas.InsertBlankLine(true); }
        public void IndentSelectionLines(bool indent) { if (virtualCanvas != null) virtualCanvas.IndentSelectionLines(indent); }
        public bool IsCtrlKChordArmed { get { return virtualCanvas != null && virtualCanvas.IsCtrlKChordArmed; } }
        public void ArmCtrlKChord() { if (virtualCanvas != null) virtualCanvas.ArmCtrlKChord(); }
        public void DisarmCtrlKChord() { if (virtualCanvas != null) virtualCanvas.DisarmCtrlKChord(); }
        public bool TryHandleCtrlKSecondKey(KeyEventArgs e) { return virtualCanvas != null && virtualCanvas.TryHandleCtrlKSecondKey(e); }
    }

    /// <summary>
    /// Sublime-Class Hardware Double-Buffered Virtualized Code Editor Canvas.
    /// Renders only visible lines at 120 FPS with per-token syntax highlighting,
    /// gutter breakpoints & code folding, inline AI ghost text, surgical diff overlays,
    /// wavy diagnostic squiggles, and an interactive pixel minimap.
    /// </summary>
    public partial class CodeLessVirtualCanvas : Control
    {
        private readonly CodeLessEditorControl owner;
        public string FontFamilyName = "Consolas";
        public float FontSizePt = 10.5f;
        public bool ColumnSelectionMode = false;
        public Keys? SimulatedModifierKeys { get; set; }
        private Keys EffectiveModifierKeys { get { return SimulatedModifierKeys.HasValue ? SimulatedModifierKeys.Value : Control.ModifierKeys; } }
        private Font regularFont;
        private Font boldFont;
        private Font italicFont;
        private Font gutterFont;
        public int LineHeight = 20;
        public int CharWidth = 8;

        private const int GutterWidth = 64;
        private const int MinimapWidth = 76;
        private const int ScrollbarThickness = 10;

        private class InlineDiffPillHit
        {
            public Rectangle AcceptRect;
            public Rectangle RejectRect;
            public EditorDiffHunk Hunk;
        }

        private class VisualRowEntry
        {
            public int DocLine;
            public int ColStart;
            public int ColEnd;
            public bool IsContinuation;
        }

        private struct CursorHistoryState
        {
            public int AnchorLine;
            public int AnchorCol;
            public int CursorLine;
            public int CursorCol;
            public List<EditorCaret> ExtraCarets;
        }

        private Timer caretTimer;
        private Timer ctrlKChordTimer;
        public bool IsCtrlKChordArmed = false;

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                if (caretTimer != null)
                {
                    try { caretTimer.Stop(); caretTimer.Dispose(); } catch { }
                    caretTimer = null;
                }
                if (ctrlKChordTimer != null)
                {
                    try { ctrlKChordTimer.Stop(); ctrlKChordTimer.Dispose(); } catch { }
                    ctrlKChordTimer = null;
                }
            }
            base.Dispose(disposing);
        }
        private readonly Stack<CursorHistoryState> cursorHistoryStack = new Stack<CursorHistoryState>();
        private bool caretBlinkOn = true;
        private bool isDraggingSelection = false;
        private bool isDraggingGutterLineSelection = false;
        private int gutterDragAnchorLine = 0;
        private bool isDraggingBoxSelection = false;
        private int boxAnchorLine = 0;
        private int boxAnchorCol = 0;
        private int boxActiveLine = 0;
        private int boxActiveCol = 0;
        private bool isPendingTextDrag = false;
        private bool isDraggingSelectedText = false;
        private Point textDragStartPt = Point.Empty;
        private int dragDropTargetLine = -1;
        private int dragDropTargetCol = -1;
        private bool isDraggingMinimap = false;
        private bool isDraggingVScroll = false;
        private bool isDraggingHScroll = false;
        private int hScrollMaxCol = 0;
        private Rectangle hScrollTrackRect = Rectangle.Empty;

        private readonly List<InlineDiffPillHit> diffPillHits = new List<InlineDiffPillHit>();
        private readonly List<VisualRowEntry> lastPaintedRows = new List<VisualRowEntry>();
        private DiagnosticItem hoveredDiagnostic = null;
        private Point lastHoverMousePt = Point.Empty;

        private static readonly HashSet<string> ControlKeywords = new HashSet<string>
        {
            "if", "else", "elif", "for", "foreach", "while", "do", "switch", "case", "default",
            "break", "continue", "return", "yield", "try", "catch", "finally", "throw", "raise",
            "except", "await", "async", "match", "loop"
        };

        private static readonly HashSet<string> StorageKeywords = new HashSet<string>
        {
            "class", "struct", "interface", "enum", "trait", "impl", "fn", "def", "function",
            "public", "private", "protected", "internal", "static", "readonly", "const", "let",
            "var", "mut", "pub", "export", "import", "from", "using", "namespace", "package",
            "mod", "use", "new", "this", "self", "super", "base", "virtual", "override", "abstract",
            "true", "false", "null", "None", "undefined"
        };

        private static readonly HashSet<string> TypeKeywords = new HashSet<string>
        {
            "void", "int", "long", "float", "double", "bool", "boolean", "string", "char", "byte",
            "object", "dynamic", "Task", "List", "Dictionary", "HashSet", "IEnumerable", "Action",
            "Func", "Promise", "Array", "Record", "Map", "Set", "Result", "Option", "String", "Vec",
            "u8", "u16", "u32", "u64", "usize", "i32", "i64", "f32", "f64", "str", "Any", "Dict", "Tuple"
        };

        private static ToolStripMenuItem CreateCanvasMenuItem(string text, string shortcutDisplay, Action onClick)
        {
            var item = new ToolStripMenuItem(text);
            if (!string.IsNullOrEmpty(shortcutDisplay))
            {
                item.ShortcutKeyDisplayString = shortcutDisplay;
                item.ShowShortcutKeys = true;
            }
            if (onClick != null)
                item.Click += (s, e) => onClick();
            return item;
        }

        public CodeLessVirtualCanvas(CodeLessEditorControl owner)
        {
            this.owner = owner;
            this.SetStyle(
                ControlStyles.AllPaintingInWmPaint |
                ControlStyles.UserPaint |
                ControlStyles.OptimizedDoubleBuffer |
                ControlStyles.ResizeRedraw |
                ControlStyles.Selectable,
                true
            );
            this.Cursor = Cursors.IBeam;
            this.TabStop = true;

            SetFontSize(10.5f);

            ContextMenuStrip editorCtx = new ContextMenuStrip
            {
                ShowImageMargin = false,
                Renderer = new DarkStudioMenuRenderer(() => owner != null ? owner.Theme : null)
            };

            ToolStripMenuItem goToDefItem = CreateCanvasMenuItem("Go to Definition", "F12", () =>
            {
                EditorTabBuffer b = owner.CurrentBuffer;
                if (b == null) return;
                if (!b.HasSelection() && owner.TriggerFollowLinkAt(b.CursorLine, b.CursorCol)) return;
                if (!b.HasSelection()) SelectWordAt(b.CursorLine, b.CursorCol);
                owner.TriggerGoToDefinition(b.GetSelectedText());
            });
            ToolStripMenuItem goToRefsItem = CreateCanvasMenuItem("Go to References", "Shift+F12", () =>
            {
                EditorTabBuffer b = owner.CurrentBuffer;
                if (b == null) return;
                if (!b.HasSelection()) SelectWordAt(b.CursorLine, b.CursorCol);
                owner.TriggerGoToReferences(b.GetSelectedText());
            });
            ToolStripMenuItem peekSubMenu = new ToolStripMenuItem("Peek");
            peekSubMenu.DropDownItems.Add(CreateCanvasMenuItem("Peek Definition", "Alt+F12", () =>
            {
                EditorTabBuffer b = owner.CurrentBuffer;
                if (b == null) return;
                if (!b.HasSelection()) SelectWordAt(b.CursorLine, b.CursorCol);
                owner.TriggerPeekDefinition(b.GetSelectedText());
            }));
            peekSubMenu.DropDownItems.Add(CreateCanvasMenuItem("Peek References", "Shift+F12", () =>
            {
                EditorTabBuffer b = owner.CurrentBuffer;
                if (b == null) return;
                if (!b.HasSelection()) SelectWordAt(b.CursorLine, b.CursorCol);
                owner.TriggerGoToReferences(b.GetSelectedText());
            }));

            ToolStripMenuItem renameItem = CreateCanvasMenuItem("Rename Symbol", "F2", () =>
            {
                EditorTabBuffer b = owner.CurrentBuffer;
                if (b == null) return;
                if (!b.HasSelection()) SelectWordAt(b.CursorLine, b.CursorCol);
                owner.TriggerRenameSymbol(b.GetSelectedText());
            });
            ToolStripMenuItem changeAllOccItem = CreateCanvasMenuItem("Change All Occurrences", "Ctrl+F2", () => SelectAllOccurrencesOfWord());
            ToolStripMenuItem formatDocItem = CreateCanvasMenuItem("Format Document", "Shift+Alt+F", () => FormatDocument());
            ToolStripMenuItem formatSelItem = CreateCanvasMenuItem("Format Selection", "Ctrl+K Ctrl+F", () => FormatSelection());

            ToolStripMenuItem cutItem = CreateCanvasMenuItem("Cut", "Ctrl+X", () => PerformCut());
            ToolStripMenuItem copyItem = CreateCanvasMenuItem("Copy", "Ctrl+C", () => PerformCopy());
            ToolStripMenuItem pasteItem = CreateCanvasMenuItem("Paste", "Ctrl+V", () => PerformPaste());
            ToolStripMenuItem selectAllItem = CreateCanvasMenuItem("Select All", "Ctrl+A", () => PerformSelectAll());

            ToolStripMenuItem inlineAiItem = CreateCanvasMenuItem("Inline AI Edit", "Ctrl+I", () => owner.ToggleCtrlKBar());
            ToolStripMenuItem sendToAiItem = CreateCanvasMenuItem("Send Selection to AI Chat", "Ctrl+Alt+I", () => owner.TriggerSendSelectionToAgent());
            ToolStripMenuItem cmdPaletteItem = CreateCanvasMenuItem("Command Palette...", "F1", () => owner.TriggerCommandPalette());

            editorCtx.Items.Add(goToDefItem);
            editorCtx.Items.Add(goToRefsItem);
            editorCtx.Items.Add(peekSubMenu);
            editorCtx.Items.Add(new ToolStripSeparator());
            editorCtx.Items.Add(renameItem);
            editorCtx.Items.Add(changeAllOccItem);
            editorCtx.Items.Add(formatDocItem);
            editorCtx.Items.Add(formatSelItem);
            editorCtx.Items.Add(new ToolStripSeparator());
            editorCtx.Items.Add(cutItem);
            editorCtx.Items.Add(copyItem);
            editorCtx.Items.Add(pasteItem);
            editorCtx.Items.Add(new ToolStripSeparator());
            editorCtx.Items.Add(selectAllItem);
            editorCtx.Items.Add(new ToolStripSeparator());
            editorCtx.Items.Add(inlineAiItem);
            editorCtx.Items.Add(sendToAiItem);
            editorCtx.Items.Add(new ToolStripSeparator());
            editorCtx.Items.Add(cmdPaletteItem);

            editorCtx.Opening += (s, e) =>
            {
                EditorTabBuffer b = owner != null ? owner.CurrentBuffer : null;
                bool hasSel = b != null && (b.HasSelection() || b.HasMultiCursors);
                cutItem.Enabled = hasSel;
                copyItem.Enabled = hasSel;
                formatSelItem.Enabled = hasSel;
            };
            this.ContextMenuStrip = editorCtx;

            ctrlKChordTimer = new Timer { Interval = 2500 };
            ctrlKChordTimer.Tick += (s, e) =>
            {
                DisarmCtrlKChord();
            };

            caretTimer = new Timer { Interval = 520 };
            caretTimer.Tick += (s, e) =>
            {
                if (this.Focused)
                {
                    caretBlinkOn = !caretBlinkOn;
                    this.Invalidate();
                }
            };
            // Blink only while focused (the app losing activation or the window being minimized also removes focus).
            this.GotFocus += (s, e) => { caretBlinkOn = true; if (caretTimer != null) caretTimer.Start(); this.Invalidate(); };
            this.LostFocus += (s, e) => { if (caretTimer != null) caretTimer.Stop(); };
            if (this.Focused) caretTimer.Start();
        }

        public void ApplyTheme(StudioTheme th)
        {
            if (th == null || this.ContextMenuStrip == null) return;
            this.ContextMenuStrip.BackColor = th.BgElevated;
            this.ContextMenuStrip.ForeColor = th.FgPrimary;
            foreach (ToolStripItem item in this.ContextMenuStrip.Items)
            {
                item.BackColor = th.BgElevated;
                item.ForeColor = th.FgPrimary;
            }
        }

        public void SetFontSize(float pt)
        {
            SetFontFamilyAndSize(FontFamilyName, pt);
        }

        public void SetFontFamilyAndSize(string family, float pt)
        {
            string cleanFamily = string.IsNullOrWhiteSpace(family) ? "Consolas" : family.Trim();
            float clamped = Math.Max(7.5f, Math.Min(26f, pt));
            bool changed = Math.Abs(FontSizePt - clamped) > 0.01f || !string.Equals(FontFamilyName, cleanFamily, StringComparison.OrdinalIgnoreCase);
            FontFamilyName = cleanFamily;
            FontSizePt = clamped;
            // Dispose the fonts being replaced (zooming with Ctrl+wheel used to leak 4 GDI+ fonts per step).
            Font oldRegular = regularFont, oldBold = boldFont, oldItalic = italicFont, oldGutter = gutterFont;
            try
            {
                regularFont = new Font(FontFamilyName, FontSizePt, FontStyle.Regular);
                boldFont = new Font(FontFamilyName, FontSizePt, FontStyle.Bold);
                italicFont = new Font(FontFamilyName, FontSizePt, FontStyle.Italic);
                gutterFont = new Font(FontFamilyName, Math.Max(7f, FontSizePt - 1.2f), FontStyle.Regular);
            }
            catch
            {
                FontFamilyName = "Consolas";
                regularFont = new Font("Consolas", FontSizePt, FontStyle.Regular);
                boldFont = new Font("Consolas", FontSizePt, FontStyle.Bold);
                italicFont = new Font("Consolas", FontSizePt, FontStyle.Italic);
                gutterFont = new Font("Consolas", Math.Max(7f, FontSizePt - 1.2f), FontStyle.Regular);
            }
            foreach (Font old in new Font[] { oldRegular, oldBold, oldItalic, oldGutter })
            {
                if (old != null && old != regularFont && old != boldFont && old != italicFont && old != gutterFont)
                {
                    try { old.Dispose(); } catch { }
                }
            }

            Size m1 = TextRenderer.MeasureText("M", regularFont, new Size(1000, 1000), TextFormatFlags.NoPadding);
            Size m2 = TextRenderer.MeasureText("MM", regularFont, new Size(1000, 1000), TextFormatFlags.NoPadding);
            CharWidth = Math.Max(6, m2.Width - m1.Width);
            LineHeight = Math.Max(16, m1.Height + 4);
            this.Invalidate();
            if (changed && owner != null)
                owner.NotifyFontSizeChanged(FontSizePt);
        }

        protected override bool IsInputKey(Keys keyData)
        {
            Keys k = keyData & Keys.KeyCode;
            if ((keyData & Keys.Control) == Keys.Control && (k == Keys.Tab || k == Keys.PageUp || k == Keys.PageDown))
            {
                return false;
            }
            if (k == Keys.Tab || k == Keys.Enter || k == Keys.Up || k == Keys.Down ||
                k == Keys.Left || k == Keys.Right || k == Keys.Home || k == Keys.End ||
                k == Keys.PageUp || k == Keys.PageDown)
            {
                return true;
            }
            return base.IsInputKey(keyData);
        }

        public int VisibleLinesCount
        {
            get { return Math.Max(1, (this.Height - 16) / LineHeight); }
        }

        private bool FindMatchingBracket(List<string> lines, int curLine, int curCol, out int brLineA, out int brColA, out int brLineB, out int brColB)
        {
            brLineA = -1; brColA = -1; brLineB = -1; brColB = -1;
            if (lines == null || curLine < 0 || curLine >= lines.Count) return false;
            string cur = lines[curLine] ?? "";

            int checkCol = -1;
            if (curCol < cur.Length && "()[]{}".IndexOf(cur[curCol]) >= 0) checkCol = curCol;
            else if (curCol > 0 && curCol - 1 < cur.Length && "()[]{}".IndexOf(cur[curCol - 1]) >= 0) checkCol = curCol - 1;
            if (checkCol < 0) return false;

            char ch = cur[checkCol];
            char matchCh = ' ';
            int dir = 1;
            if (ch == '(') { matchCh = ')'; dir = 1; }
            else if (ch == '[') { matchCh = ']'; dir = 1; }
            else if (ch == '{') { matchCh = '}'; dir = 1; }
            else if (ch == ')') { matchCh = '('; dir = -1; }
            else if (ch == ']') { matchCh = '['; dir = -1; }
            else if (ch == '}') { matchCh = '{'; dir = -1; }
            else return false;

            brLineA = curLine;
            brColA = checkCol;
            int depth = 1;
            int maxScan = 450;

            if (dir > 0)
            {
                for (int l = curLine; l < lines.Count && (l - curLine) < maxScan; l++)
                {
                    string ln = lines[l] ?? "";
                    int startC = (l == curLine) ? checkCol + 1 : 0;
                    for (int c = startC; c < ln.Length; c++)
                    {
                        if (ln[c] == ch) depth++;
                        else if (ln[c] == matchCh)
                        {
                            depth--;
                            if (depth == 0) { brLineB = l; brColB = c; return true; }
                        }
                    }
                }
            }
            else
            {
                for (int l = curLine; l >= 0 && (curLine - l) < maxScan; l--)
                {
                    string ln = lines[l] ?? "";
                    int startC = (l == curLine) ? checkCol - 1 : ln.Length - 1;
                    for (int c = startC; c >= 0; c--)
                    {
                        if (ln[c] == ch) depth++;
                        else if (ln[c] == matchCh)
                        {
                            depth--;
                            if (depth == 0) { brLineB = l; brColB = c; return true; }
                        }
                    }
                }
            }
            return false;
        }

        private bool EffectiveShowMinimap
        {
            get { return owner.ShowMinimap && this.Width >= 540; }
        }

        private int GetWrapCols()
        {
            int rightMinimapReserve = EffectiveShowMinimap ? MinimapWidth : 0;
            int codeAreaRight = this.Width - rightMinimapReserve - ScrollbarThickness;
            return Math.Max(24, (codeAreaRight - GutterWidth - 22) / Math.Max(6, CharWidth));
        }

        public int CharColToVisualCol(string lineText, int colStart, int charCol)
        {
            if (string.IsNullOrEmpty(lineText) || charCol <= colStart) return 0;
            int vis = 0;
            int limit = Math.Min(lineText.Length, charCol);
            for (int i = Math.Max(0, colStart); i < limit; i++)
            {
                vis += (lineText[i] == '\t') ? 4 : 1;
            }
            if (charCol > lineText.Length)
                vis += (charCol - lineText.Length);
            return vis;
        }

        public Point GetCursorPixelPoint()
        {
            EditorTabBuffer buf = owner.CurrentBuffer;
            if (buf == null) return new Point(GutterWidth + 8, 8);
            string curLineText = (buf.CursorLine >= 0 && buf.CursorLine < buf.Lines.Count) ? (buf.Lines[buf.CursorLine] ?? "") : "";
            for (int r = 0; r < lastPaintedRows.Count; r++)
            {
                VisualRowEntry vre = lastPaintedRows[r];
                if (vre.DocLine == buf.CursorLine && buf.CursorCol >= vre.ColStart && buf.CursorCol <= vre.ColEnd)
                {
                    int visCol = CharColToVisualCol(curLineText, vre.ColStart, buf.CursorCol);
                    int vx = GutterWidth + 8 + (visCol - (buf.WordWrap ? 0 : buf.ScrollLeftCol)) * CharWidth;
                    int vy = r * LineHeight + 4;
                    return new Point(vx, vy);
                }
            }
            int fullVisCol = CharColToVisualCol(curLineText, 0, buf.CursorCol);
            int x = GutterWidth + 8 + (fullVisCol - buf.ScrollLeftCol) * CharWidth;
            int y = (buf.CursorLine - buf.ScrollTopLine) * LineHeight + 4;
            return new Point(x, y);
        }

        public void EnsureCursorVisible()
        {
            EditorTabBuffer buf = owner.CurrentBuffer;
            if (buf == null) return;
            buf.ClampCursor();

            int visLines = VisibleLinesCount;
            if (buf.CursorLine < buf.ScrollTopLine)
                buf.ScrollTopLine = buf.CursorLine;
            else if (buf.CursorLine >= buf.ScrollTopLine + visLines - 1)
                buf.ScrollTopLine = Math.Max(0, buf.CursorLine - visLines + 2);

            if (buf.WordWrap)
            {
                buf.ScrollLeftCol = 0;
                int wrapCols = GetWrapCols();
                while (buf.ScrollTopLine < buf.CursorLine)
                {
                    int visualRows = 0;
                    for (int dl = buf.ScrollTopLine; dl <= buf.CursorLine && dl < buf.Lines.Count; dl++)
                    {
                        int lnLen = (buf.Lines[dl] ?? "").Length;
                        visualRows += Math.Max(1, (lnLen + wrapCols - 1) / wrapCols);
                    }
                    if (visualRows <= visLines) break;
                    buf.ScrollTopLine++;
                }
                return;
            }

            int codeWidthPx = Math.Max(100, this.Width - GutterWidth - (EffectiveShowMinimap ? MinimapWidth : 0) - 20);
            int visCols = Math.Max(10, codeWidthPx / CharWidth);
            if (buf.CursorCol < buf.ScrollLeftCol)
                buf.ScrollLeftCol = Math.Max(0, buf.CursorCol - 4);
            else if (buf.CursorCol >= buf.ScrollLeftCol + visCols - 2)
                buf.ScrollLeftCol = Math.Max(0, buf.CursorCol - visCols + 6);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            StudioTheme th = owner.Theme;
            EditorTabBuffer buf = owner.CurrentBuffer;

            diffPillHits.Clear();
            ClearAnnotationHits();
            lastPaintedRows.Clear();

            g.Clear(th.BgEditor);
            if (buf == null || buf.Lines == null) return;

            int totalLines = buf.Lines.Count;
            int visLines = VisibleLinesCount;
            int rightMinimapReserve = EffectiveShowMinimap ? MinimapWidth : 0;
            int codeAreaRight = this.Width - rightMinimapReserve - ScrollbarThickness;
            int wrapCols = Math.Max(24, (codeAreaRight - GutterWidth - 22) / Math.Max(6, CharWidth));

            // 1. Gutter background & separator
            using (SolidBrush gb = new SolidBrush(th.BgSidebar))
                g.FillRectangle(gb, 0, 0, GutterWidth, this.Height);
            using (Pen borderPen = new Pen(th.BorderSubtle))
                g.DrawLine(borderPen, GutterWidth - 1, 0, GutterWidth - 1, this.Height);

            bool showGutterDiff = !buf.IsLargeFile && totalLines <= 15000 && buf.ChangedLineCount > 0;

            // Compute bracket match pair around primary cursor
            int brLineA = -1, brColA = -1, brLineB = -1, brColB = -1;
            bool hasBracketMatch = (!buf.IsLargeFile && totalLines <= 10000) &&
                FindMatchingBracket(buf.Lines, buf.CursorLine, buf.CursorCol, out brLineA, out brColA, out brLineB, out brColB);

            int sLine, sCol, eLine, eCol;
            buf.GetNormalizedSelection(out sLine, out sCol, out eLine, out eCol);
            bool hasSel = buf.HasSelection();

            int maxVisibleLineLen = 0;
            int screenRow = 0;
            for (int docLine = buf.ScrollTopLine; docLine < totalLines && screenRow <= visLines; docLine++)
            {
                string fullLineText = buf.Lines[docLine] ?? "";
                if (fullLineText.Length > maxVisibleLineLen) maxVisibleLineLen = fullLineText.Length;

                EditorDiffHunk hunk = buf.DiffHunks != null
                    ? buf.DiffHunks.FirstOrDefault(h => h.IsPending && docLine >= h.StartLine && docLine < h.StartLine + Math.Max(1, h.AddedLines.Count))
                    : null;

                string trimmedRight = fullLineText.TrimEnd();
                bool isFoldableHeader = IsFoldableHeaderLine(buf.Lines, docLine);
                bool isFolded = buf.FoldedLines.Contains(docLine);

                // Break line into 1+ visual segments when WordWrap is active
                List<VisualRowEntry> segs = new List<VisualRowEntry>();
                if (!buf.WordWrap || isFolded || fullLineText.Length <= wrapCols)
                {
                    segs.Add(new VisualRowEntry { DocLine = docLine, ColStart = 0, ColEnd = fullLineText.Length, IsContinuation = false });
                }
                else
                {
                    for (int offset = 0; offset < fullLineText.Length; offset += wrapCols)
                    {
                        int endOff = Math.Min(fullLineText.Length, offset + wrapCols);
                        segs.Add(new VisualRowEntry { DocLine = docLine, ColStart = offset, ColEnd = endOff, IsContinuation = offset > 0 });
                    }
                }

                foreach (VisualRowEntry seg in segs)
                {
                    if (screenRow > visLines) break;
                    lastPaintedRows.Add(seg);
                    int y = screenRow * LineHeight + 4;
                    string lineSlice = fullLineText.Substring(seg.ColStart, seg.ColEnd - seg.ColStart);
                    int effScrollLeft = buf.WordWrap ? 0 : buf.ScrollLeftCol;

                    // Active Line Highlight
                    if (docLine == buf.CursorLine)
                    {
                        using (SolidBrush hlBrush = new SolidBrush(th.BgLineHighlight))
                            g.FillRectangle(hlBrush, GutterWidth, y, Math.Max(0, codeAreaRight - GutterWidth), LineHeight);
                        using (SolidBrush gutterHl = new SolidBrush(th.BgElevated))
                            g.FillRectangle(gutterHl, 0, y, GutterWidth - 1, LineHeight);
                    }
                    if (buf.DebugStoppedLine == docLine) DrawDebugStoppedLineBackground(g, buf, y, codeAreaRight);

                    // Staged AI Diff Line Tint
                    if (hunk != null)
                    {
                        using (SolidBrush diffAddBrush = new SolidBrush(Color.FromArgb(40, 16, 185, 129)))
                            g.FillRectangle(diffAddBrush, GutterWidth, y, Math.Max(0, codeAreaRight - GutterWidth), LineHeight);
                        using (SolidBrush diffGutterBar = new SolidBrush(Color.FromArgb(16, 185, 129)))
                            g.FillRectangle(diffGutterBar, GutterWidth - 4, y, 3, LineHeight);
                    }
                    else if (!seg.IsContinuation && showGutterDiff)
                    {
                        if (buf.GutterAddedLines != null && buf.GutterAddedLines.Contains(docLine))
                        {
                            using (SolidBrush gitAdd = new SolidBrush(Color.FromArgb(180, 16, 185, 129)))
                                g.FillRectangle(gitAdd, GutterWidth - 4, y + 1, 2, LineHeight - 2);
                        }
                        else if (buf.GutterModifiedLines != null && buf.GutterModifiedLines.Contains(docLine))
                        {
                            using (SolidBrush gitMod = new SolidBrush(Color.FromArgb(180, 59, 130, 246)))
                                g.FillRectangle(gitMod, GutterWidth - 4, y + 1, 2, LineHeight - 2);
                        }
                    }

                    // Gutter decorations on non-continuation rows
                    if (!seg.IsContinuation)
                    {
                        DrawDebugGutterGlyphs(g, buf, docLine, y);

                        string numStr = (docLine + 1).ToString();
                        Size numSize = TextRenderer.MeasureText(numStr, gutterFont, new Size(200, 50), TextFormatFlags.NoPadding);
                        Color numColor = docLine == buf.CursorLine ? th.FgPrimary : th.FgMuted;
                        TextRenderer.DrawText(g, numStr, gutterFont, new Point(GutterWidth - 18 - numSize.Width, y + 2), numColor, TextFormatFlags.NoPadding);

                        if (isFoldableHeader || isFolded)
                        {
                            TextRenderer.DrawText(g, isFolded ? "▸" : "▾", gutterFont, new Point(GutterWidth - 15, y + 1), th.AccentPrimary, TextFormatFlags.NoPadding);
                        }
                    }

                    // Clip code area so long lines / lenses never overflow minimap
                    Region prevClip = g.Clip;
                    g.SetClip(new Rectangle(GutterWidth, y, Math.Max(1, codeAreaRight - GutterWidth), LineHeight + 2));

                    // 100-col ruler guide inside clipped region
                    if (!buf.WordWrap)
                    {
                        int ruler100X = GutterWidth + 8 + (100 - effScrollLeft) * CharWidth;
                        if (ruler100X > GutterWidth && ruler100X < codeAreaRight)
                        {
                            using (Pen rulerPen = new Pen(Color.FromArgb(26, th.FgMuted)))
                                g.DrawLine(rulerPen, ruler100X, y, ruler100X, y + LineHeight);
                        }
                    }

                    // Live Find & Replace Search Match Highlights
                    if (owner.IsFindBarVisible && owner.ActiveSearchMatches.Count > 0)
                    {
                        for (int mIdx = 0; mIdx < owner.ActiveSearchMatches.Count; mIdx++)
                        {
                            CodeLessEditorControl.SearchMatch sm = owner.ActiveSearchMatches[mIdx];
                            if (sm.Line != docLine) continue;
                            int mStart = Math.Max(seg.ColStart, sm.Col);
                            int mEnd = Math.Min(seg.ColEnd, sm.Col + sm.Length);
                            if (mEnd > mStart)
                            {
                                int vis1 = CharColToVisualCol(fullLineText, seg.ColStart, mStart);
                                int vis2 = CharColToVisualCol(fullLineText, seg.ColStart, mEnd);
                                int x1 = GutterWidth + 8 + Math.Max(0, vis1 - effScrollLeft) * CharWidth;
                                int x2 = GutterWidth + 8 + Math.Max(0, vis2 - effScrollLeft) * CharWidth;
                                if (x2 > x1)
                                {
                                    bool isCurrentMatch = (mIdx == owner.ActiveSearchMatchIndex);
                                    Color fillCol = isCurrentMatch ? Color.FromArgb(120, 250, 204, 21) : Color.FromArgb(65, 250, 204, 21);
                                    Color borderCol = isCurrentMatch ? Color.FromArgb(235, 250, 204, 21) : Color.FromArgb(145, 250, 204, 21);
                                    using (SolidBrush mb = new SolidBrush(fillCol))
                                        g.FillRectangle(mb, x1, y + 1, x2 - x1, LineHeight - 2);
                                    using (Pen mp = new Pen(borderCol, 1f))
                                        g.DrawRectangle(mp, x1, y + 1, x2 - x1 - 1, LineHeight - 3);
                                }
                            }
                        }
                    }

                    // Primary Selection Highlight
                    if (hasSel && docLine >= sLine && docLine <= eLine)
                    {
                        int selStartCol = Math.Max(seg.ColStart, docLine == sLine ? sCol : 0);
                        int selEndCol = Math.Min(seg.ColEnd + (docLine < eLine ? 1 : 0), docLine == eLine ? eCol : fullLineText.Length + 1);
                        if (selEndCol > selStartCol)
                        {
                            int vis1 = CharColToVisualCol(fullLineText, seg.ColStart, selStartCol);
                            int vis2 = CharColToVisualCol(fullLineText, seg.ColStart, selEndCol);
                            int x1 = GutterWidth + 8 + Math.Max(0, vis1 - effScrollLeft) * CharWidth;
                            int x2 = GutterWidth + 8 + Math.Max(0, vis2 - effScrollLeft) * CharWidth;
                            if (x2 > x1)
                            {
                                using (SolidBrush selBrush = new SolidBrush(th.BgSelection))
                                    g.FillRectangle(selBrush, x1, y, x2 - x1, LineHeight);
                            }
                        }
                    }

                    // Extra Multi-Cursor Selections Highlight
                    if (buf.ExtraCarets != null)
                    {
                        foreach (EditorCaret ec in buf.ExtraCarets)
                        {
                            int esL, esC, eeL, eeC;
                            ec.GetNormalizedRange(out esL, out esC, out eeL, out eeC);
                            if ((esL != eeL || esC != eeC) && docLine >= esL && docLine <= eeL)
                            {
                                int ss = Math.Max(seg.ColStart, docLine == esL ? esC : 0);
                                int ee = Math.Min(seg.ColEnd + (docLine < eeL ? 1 : 0), docLine == eeL ? eeC : fullLineText.Length + 1);
                                if (ee > ss)
                                {
                                    int vis1 = CharColToVisualCol(fullLineText, seg.ColStart, ss);
                                    int vis2 = CharColToVisualCol(fullLineText, seg.ColStart, ee);
                                    int x1 = GutterWidth + 8 + Math.Max(0, vis1 - effScrollLeft) * CharWidth;
                                    int x2 = GutterWidth + 8 + Math.Max(0, vis2 - effScrollLeft) * CharWidth;
                                    if (x2 > x1)
                                    {
                                        using (SolidBrush selBrush = new SolidBrush(th.BgSelection))
                                            g.FillRectangle(selBrush, x1, y, x2 - x1, LineHeight);
                                    }
                                }
                            }
                        }
                    }

                    // Indentation Guides
                    if (!seg.IsContinuation)
                    {
                        int leadingSpaces = 0;
                        while (leadingSpaces < lineSlice.Length && (lineSlice[leadingSpaces] == ' ' || lineSlice[leadingSpaces] == '\t')) leadingSpaces++;
                        int leadingVisCols = CharColToVisualCol(fullLineText, seg.ColStart, seg.ColStart + leadingSpaces);
                        using (Pen guidePen = new Pen(Color.FromArgb(42, th.FgMuted)))
                        {
                            for (int sp = 4; sp <= leadingVisCols; sp += 4)
                            {
                                int gx = GutterWidth + 8 + (sp - effScrollLeft) * CharWidth;
                                if (gx > GutterWidth && gx < codeAreaRight)
                                    g.DrawLine(guidePen, gx, y, gx, y + LineHeight);
                            }
                        }
                    }

                    // Bracket Pair Match Highlight
                    if (hasBracketMatch)
                    {
                        if (docLine == brLineA && brColA >= seg.ColStart && brColA < seg.ColEnd)
                        {
                            int bx = GutterWidth + 8 + (CharColToVisualCol(fullLineText, seg.ColStart, brColA) - effScrollLeft) * CharWidth;
                            using (SolidBrush bb = new SolidBrush(Color.FromArgb(55, th.AccentSecondary)))
                                g.FillRectangle(bb, bx, y + 1, CharWidth, LineHeight - 2);
                            using (Pen bp = new Pen(Color.FromArgb(190, th.AccentSecondary), 1f))
                                g.DrawRectangle(bp, bx, y + 1, CharWidth - 1, LineHeight - 3);
                        }
                        if (docLine == brLineB && brColB >= seg.ColStart && brColB < seg.ColEnd)
                        {
                            int bx = GutterWidth + 8 + (CharColToVisualCol(fullLineText, seg.ColStart, brColB) - effScrollLeft) * CharWidth;
                            using (SolidBrush bb = new SolidBrush(Color.FromArgb(55, th.AccentSecondary)))
                                g.FillRectangle(bb, bx, y + 1, CharWidth, LineHeight - 2);
                            using (Pen bp = new Pen(Color.FromArgb(190, th.AccentSecondary), 1f))
                                g.DrawRectangle(bp, bx, y + 1, CharWidth - 1, LineHeight - 3);
                        }
                    }

                    string expandedSlice = lineSlice.IndexOf('\t') >= 0 ? lineSlice.Replace("\t", "    ") : lineSlice;
                    int sliceVisLen = expandedSlice.Length;

                    // Multi-Occurrence Word Outline
                    if (!buf.HasMultiCursors && buf.HasSelection())
                    {
                        string selWord = buf.GetSelectedText();
                        if (!string.IsNullOrEmpty(selWord) && selWord.Length >= 2 && selWord.Length <= 40 && !selWord.Contains("\n") && !selWord.Contains(" "))
                        {
                            int mIdx = 0;
                            while ((mIdx = expandedSlice.IndexOf(selWord, mIdx, StringComparison.Ordinal)) >= 0)
                            {
                                int hlX = GutterWidth + 8 + (mIdx - effScrollLeft) * CharWidth;
                                int hlW = selWord.Length * CharWidth;
                                using (SolidBrush occBrush = new SolidBrush(Color.FromArgb(36, th.AccentPrimary)))
                                    g.FillRectangle(occBrush, hlX, y + 1, hlW, LineHeight - 2);
                                using (Pen occPen = new Pen(Color.FromArgb(105, th.AccentPrimary)))
                                    g.DrawRectangle(occPen, hlX, y + 1, hlW, LineHeight - 3);
                                mIdx += selWord.Length;
                            }
                        }
                    }

                    // Syntax Highlighted Code Slice
                    DrawSyntaxHighlightedLine(g, expandedSlice, GutterWidth + 8 - effScrollLeft * CharWidth, y + 2, GutterWidth, codeAreaRight, th);

                    // Inline annotations & diff pills only on final segment of the line
                    bool isLastSegOfLine = (seg.ColEnd == fullLineText.Length);
                    if (isLastSegOfLine && hunk == null && !isFolded && !buf.WordWrap) DrawLineAnnotation(g, buf, docLine, sliceVisLen, effScrollLeft, y, codeAreaRight);

                    if (isLastSegOfLine && hunk != null && docLine == hunk.StartLine)
                    {
                        // Render sleek inline [✓ Accept] [✕ Reject] interactive pills for this hunk!
                        int pillRight = codeAreaRight - 8;
                        bool narrowPane = (codeAreaRight - GutterWidth) < 520;
                        int btnW = narrowPane ? 62 : 72;
                        Rectangle rejectRect = new Rectangle(pillRight - btnW, y + 2, btnW, LineHeight - 4);
                        Rectangle acceptRect = new Rectangle(rejectRect.Left - btnW - 4, y + 2, btnW, LineHeight - 4);
                        int lineTextEndPx = GutterWidth + 8 + (sliceVisLen - effScrollLeft) * CharWidth + 10;

                        if (acceptRect.Left > GutterWidth + 40)
                        {
                            bool showRemTag = hunk.RemovedLines.Count > 0 && (!narrowPane || (acceptRect.Left - 84 > lineTextEndPx));
                            int groupLeft = showRemTag ? (acceptRect.Left - 86) : (acceptRect.Left - 4);
                            using (SolidBrush pillBackdrop = new SolidBrush(th.BgEditor))
                                g.FillRectangle(pillBackdrop, groupLeft, y + 1, pillRight - groupLeft + 2, LineHeight - 2);

                            g.SmoothingMode = SmoothingMode.AntiAlias;
                            using (SolidBrush ab = new SolidBrush(Color.FromArgb(16, 185, 129)))
                                g.FillRectangle(ab, acceptRect);
                            using (SolidBrush rb = new SolidBrush(Color.FromArgb(190, 50, 68)))
                                g.FillRectangle(rb, rejectRect);
                            g.SmoothingMode = SmoothingMode.Default;

                            TextRenderer.DrawText(g, "✓ Accept", gutterFont, acceptRect, Color.White, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
                            TextRenderer.DrawText(g, "✕ Reject", gutterFont, rejectRect, Color.White, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);

                            if (showRemTag)
                            {
                                Rectangle remTag = new Rectangle(acceptRect.Left - 84, y + 2, 80, LineHeight - 4);
                                using (SolidBrush rtb = new SolidBrush(StudioTheme.Blend(th.BgEditor, Color.FromArgb(225, 29, 72), 0.25)))
                                    g.FillRectangle(rtb, remTag);
                                TextRenderer.DrawText(g, string.Format("-{0} removed", hunk.RemovedLines.Count), gutterFont, remTag, Color.FromArgb(251, 113, 133),
                                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);

                                // Also render inline red removed-line preview when space permits
                                string firstRemoved = (hunk.RemovedLines[0] ?? "").Trim();
                                int availRemPreviewW = remTag.Left - lineTextEndPx - 12;
                                if (!string.IsNullOrEmpty(firstRemoved) && availRemPreviewW >= 90)
                                {
                                    Rectangle remPreviewRect = new Rectangle(lineTextEndPx + 4, y + 2, Math.Min(availRemPreviewW, 240), LineHeight - 4);
                                    using (SolidBrush rpb = new SolidBrush(Color.FromArgb(38, 225, 29, 72)))
                                        g.FillRectangle(rpb, remPreviewRect);
                                    TextRenderer.DrawText(g, "- " + firstRemoved, italicFont, remPreviewRect, Color.FromArgb(251, 113, 133),
                                        TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine | TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix);
                                }
                            }

                            diffPillHits.Add(new InlineDiffPillHit { AcceptRect = acceptRect, RejectRect = rejectRect, Hunk = hunk });
                        }
                    }
                    else if (isLastSegOfLine && owner.ShowCodeLens && buf.Symbols != null && !isFolded && !buf.WordWrap)
                    {
                        AstSymbolItem symOnLine = buf.Symbols.FirstOrDefault(s => s.Line == docLine + 1 && (s.Kind == "fn" || s.Kind == "class" || s.Kind == "struct" || s.Kind == "interface"));
                        if (symOnLine != null)
                        {
                            int lensX = GutterWidth + 8 + (lineSlice.Length - effScrollLeft + 3) * CharWidth;
                            int availW = codeAreaRight - lensX - 10;
                            if (availW >= 110)
                            {
                                string lensText = availW >= 240
                                    ? string.Format("{0} · {1}L · F12 Def · Ctrl+I Refactor", symOnLine.ComplexityHint ?? "O(1)", symOnLine.LineSpan)
                                    : string.Format("{0} · {1}L · Ctrl+I", symOnLine.ComplexityHint ?? "O(1)", symOnLine.LineSpan);
                                Rectangle lensRect = new Rectangle(lensX, y + 1, availW, LineHeight);
                                TextRenderer.DrawText(g, lensText, gutterFont, lensRect, Color.FromArgb(150, th.AccentPrimary),
                                    TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine | TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix);
                            }
                        }
                    }

                    // Diagnostic Squiggles
                    if (buf.Diagnostics != null)
                    {
                        DiagnosticItem firstDiag = null;
                        foreach (DiagnosticItem diag in buf.Diagnostics.Where(d => d.Line == docLine + 1))
                        {
                            if (firstDiag == null) firstDiag = diag;
                            int dCol = Math.Max(0, diag.Column - 1);
                            if (dCol >= seg.ColStart && dCol <= seg.ColEnd)
                            {
                                int sqStartX = GutterWidth + 8 + Math.Max(0, (dCol - seg.ColStart) - effScrollLeft) * CharWidth;
                                int sqWidth = Math.Max(CharWidth * 3, Math.Min(180, (lineSlice.Length - (dCol - seg.ColStart)) * CharWidth));
                                if (diag.IsLanguageServer && diag.EndColumn > diag.Column)
                                    sqWidth = Math.Max(CharWidth, Math.Min(seg.ColEnd + 1, diag.EndColumn - 1) - dCol) * CharWidth; // exact LSP range
                                Color sqColor = diag.Severity == "Error"
                                    ? Color.FromArgb(244, 63, 94)
                                    : (diag.Severity == "Warning" ? Color.FromArgb(251, 191, 36) : Color.FromArgb(56, 189, 248));
                                DrawWavySquiggle(g, sqStartX, y + LineHeight - 3, sqWidth, sqColor);
                            }
                        }
                        if (isLastSegOfLine && firstDiag != null && firstDiag.Severity != "Info" && !buf.WordWrap)
                        {
                            int errLensX = GutterWidth + 8 + (lineSlice.Length - effScrollLeft + 3) * CharWidth;
                            int availErrW = codeAreaRight - errLensX - 10;
                            if (availErrW >= 90)
                            {
                                Color errCol = firstDiag.Severity == "Error" ? Color.FromArgb(251, 113, 133) : Color.FromArgb(251, 191, 36);
                                Rectangle errRect = new Rectangle(errLensX, y + 1, availErrW, LineHeight);
                                TextRenderer.DrawText(g, "⚠ " + firstDiag.Message, italicFont, errRect, errCol,
                                    TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine | TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix);
                            }
                        }
                    }

                    // Ghost Text (Single-line inline + Multi-line Cursor Tab preview)
                    if (docLine == buf.CursorLine && buf.CursorCol >= seg.ColStart && buf.CursorCol <= seg.ColEnd &&
                        !string.IsNullOrEmpty(buf.GhostText) && buf.GhostLine == docLine)
                    {
                        string[] ghostLines = buf.GhostText.Replace("\r\n", "\n").Split('\n');
                        string firstGhostLine = ghostLines[0];
                        int ghostVisCol = CharColToVisualCol(fullLineText, seg.ColStart, buf.CursorCol);
                        int ghostX = GutterWidth + 8 + (ghostVisCol - effScrollLeft) * CharWidth + 2;
                        if (ghostX < codeAreaRight - 40)
                        {
                            using (SolidBrush clearGhostBg = new SolidBrush(th.BgLineHighlight))
                                g.FillRectangle(clearGhostBg, ghostX - 2, y + 1, Math.Max(10, codeAreaRight - ghostX), LineHeight - 2);

                            TextRenderer.DrawText(g, firstGhostLine, italicFont, new Point(ghostX, y + 2), th.FgMuted, TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix);
                            int afterGhostX = ghostX + firstGhostLine.Length * CharWidth;
                            if (buf.CursorCol < fullLineText.Length)
                            {
                                string suffixText = fullLineText.Substring(buf.CursorCol);
                                if (!string.IsNullOrEmpty(suffixText) && afterGhostX + suffixText.Length * CharWidth < codeAreaRight - 130)
                                {
                                    TextRenderer.DrawText(g, suffixText, regularFont, new Point(afterGhostX, y + 2), th.FgPrimary, TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix);
                                    afterGhostX += suffixText.Length * CharWidth;
                                }
                            }
                            int badgeX = afterGhostX + 10;
                            string pillTxt = ghostLines.Length > 1 ? string.Format("Tab (+{0}L)", ghostLines.Length - 1) : "Tab · Ctrl+Right";
                            int pillW = Math.Min(116, TextRenderer.MeasureText(pillTxt, gutterFont).Width + 12);
                            if (badgeX < codeAreaRight - pillW - 8)
                            {
                                using (SolidBrush pillB = new SolidBrush(th.BgElevated))
                                    g.FillRectangle(pillB, badgeX, y + 2, pillW, LineHeight - 4);
                                TextRenderer.DrawText(g, pillTxt, gutterFont, new Point(badgeX + 5, y + 3), th.AccentPrimary, TextFormatFlags.NoPadding);
                            }
                        }
                    }

                    // Extra Multi-Cursors Caret Rendering inside this segment
                    if (this.Focused && caretBlinkOn && buf.ExtraCarets != null)
                    {
                        foreach (EditorCaret ec in buf.ExtraCarets)
                        {
                            if (ec.CursorLine == docLine && ec.CursorCol >= seg.ColStart && ec.CursorCol <= seg.ColEnd)
                            {
                                int ecVisCol = CharColToVisualCol(fullLineText, seg.ColStart, ec.CursorCol);
                                int cx = GutterWidth + 8 + (ecVisCol - effScrollLeft) * CharWidth;
                                if (cx >= GutterWidth && cx < codeAreaRight)
                                {
                                    using (SolidBrush cb = new SolidBrush(th.AccentSecondary))
                                        g.FillRectangle(cb, cx, y, 2, LineHeight - 2);
                                }
                            }
                        }
                    }

                    // Folded Region Pill
                    if (isLastSegOfLine && isFolded)
                    {
                        int foldEnd = FindFoldEndLine(buf.Lines, docLine);
                        int hiddenCount = Math.Max(1, foldEnd - docLine);
                        int pillX = GutterWidth + 8 + (lineSlice.Length - effScrollLeft + 1) * CharWidth;
                        using (SolidBrush fb = new SolidBrush(th.BgElevated))
                            g.FillRectangle(fb, pillX, y + 2, 118, LineHeight - 4);
                        TextRenderer.DrawText(g, string.Format("... {0} lines folded", hiddenCount), gutterFont, new Point(pillX + 6, y + 3), th.AccentPrimary, TextFormatFlags.NoPadding);
                        docLine = foldEnd;
                    }

                    g.Clip = prevClip;
                    screenRow++;
                }
            }

            // 3. Primary Caret Rendering + Multi-Line Ghost Text Preview (Cursor Tab)
            if (this.Focused && caretBlinkOn)
            {
                Point caretPt = GetCursorPixelPoint();
                if (caretPt.X >= GutterWidth && caretPt.X < codeAreaRight && caretPt.Y >= 0 && caretPt.Y < this.Height)
                {
                    using (SolidBrush caretBrush = new SolidBrush(th.AccentPrimary))
                        g.FillRectangle(caretBrush, caretPt.X, caretPt.Y, 2, LineHeight - 2);
                }
            }

            if (!string.IsNullOrEmpty(buf.GhostText) && buf.GhostLine == buf.CursorLine && buf.GhostText.Contains("\n"))
            {
                string[] gLines = buf.GhostText.Replace("\r\n", "\n").Split('\n');
                if (gLines.Length > 1)
                {
                    Point caretPt = GetCursorPixelPoint();
                    int previewCount = Math.Min(4, gLines.Length - 1);
                    int boxY = caretPt.Y + LineHeight;
                    int boxH = previewCount * LineHeight + 6;
                    if (boxY + boxH < this.Height - 10)
                    {
                        int maxLen = 20;
                        for (int gi = 1; gi <= previewCount; gi++)
                            if (gLines[gi].Length > maxLen) maxLen = gLines[gi].Length;
                        int boxW = Math.Min(codeAreaRight - GutterWidth - 16, Math.Max(180, maxLen * CharWidth + 24));
                        Rectangle ghostBox = new Rectangle(GutterWidth + 6, boxY, boxW, boxH);
                        using (SolidBrush gbg = new SolidBrush(th.BgElevated))
                            g.FillRectangle(gbg, ghostBox);
                        using (Pen gp = new Pen(Color.FromArgb(110, th.AccentPrimary), 1f))
                            g.DrawRectangle(gp, ghostBox.X, ghostBox.Y, ghostBox.Width - 1, ghostBox.Height - 1);
                        using (SolidBrush gbar = new SolidBrush(th.AccentPrimary))
                            g.FillRectangle(gbar, ghostBox.X, ghostBox.Y, 3, ghostBox.Height);
                        for (int gi = 1; gi <= previewCount; gi++)
                        {
                            TextRenderer.DrawText(g, gLines[gi], italicFont,
                                new Point(ghostBox.X + 10, ghostBox.Y + 3 + (gi - 1) * LineHeight),
                                th.FgMuted, TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix);
                        }
                    }
                }
            }

            DrawLinkHoverUnderline(g, th, buf);

            // 4. Interactive Pixel Minimap
            if (EffectiveShowMinimap)
            {
                int mmX = this.Width - MinimapWidth;
                using (SolidBrush mmBg = new SolidBrush(th.BgSidebar))
                    g.FillRectangle(mmBg, mmX, 0, MinimapWidth, this.Height);
                using (Pen mmBorder = new Pen(th.BorderSubtle))
                    g.DrawLine(mmBorder, mmX, 0, mmX, this.Height);

                int maxMmLines = Math.Min(totalLines, this.Height / 3);
                double step = totalLines > maxMmLines ? (double)totalLines / maxMmLines : 1.0;

                for (int r = 0; r < maxMmLines; r++)
                {
                    int idx = (int)(r * step);
                    if (idx >= totalLines) break;
                    string ln = buf.Lines[idx];
                    if (string.IsNullOrWhiteSpace(ln)) continue;

                    int indent = 0;
                    while (indent < ln.Length && (ln[indent] == ' ' || ln[indent] == '\t')) indent++;
                    int barX = mmX + 6 + Math.Min(24, indent * 2);
                    int barW = Math.Max(4, Math.Min(MinimapWidth - 14 - (barX - mmX), ln.Trim().Length / 2));

                    Color barColor = th.FgMuted;
                    string tr = ln.TrimStart();
                    if (tr.StartsWith("//") || tr.StartsWith("#")) barColor = th.TokenComment;
                    else if (tr.StartsWith("public ") || tr.StartsWith("class ") || tr.StartsWith("def ") || tr.StartsWith("export ") || tr.StartsWith("fn ")) barColor = th.TokenKeyword;
                    else if (tr.Contains("(")) barColor = th.TokenFunction;

                    using (SolidBrush bb = new SolidBrush(Color.FromArgb(150, barColor)))
                        g.FillRectangle(bb, barX, r * 3 + 4, barW, 2);
                }

                if (totalLines > 0)
                {
                    int lensY = (int)((double)buf.ScrollTopLine / Math.Max(1, totalLines) * Math.Min(this.Height - 24, maxMmLines * 3));
                    int lensH = Math.Max(20, (int)((double)visLines / Math.Max(1, totalLines) * Math.Min(this.Height, maxMmLines * 3)));
                    using (SolidBrush lensBrush = new SolidBrush(Color.FromArgb(45, th.AccentPrimary)))
                        g.FillRectangle(lensBrush, mmX + 1, lensY, MinimapWidth - 2, Math.Min(this.Height - lensY, lensH));
                    using (Pen lensPen = new Pen(Color.FromArgb(120, th.AccentPrimary)))
                        g.DrawRectangle(lensPen, mmX + 1, lensY, MinimapWidth - 3, Math.Min(this.Height - lensY - 1, lensH));
                }
            }

            // 5. Sleek Vertical Scrollbar
            if (totalLines > visLines)
            {
                int sbX = codeAreaRight;
                int thumbH = Math.Max(24, (int)((double)visLines / totalLines * this.Height));
                int thumbY = (int)((double)buf.ScrollTopLine / Math.Max(1, totalLines - visLines) * (this.Height - thumbH));
                using (SolidBrush sbThumb = new SolidBrush(Color.FromArgb(115, th.FgMuted)))
                    g.FillRectangle(sbThumb, sbX + 1, thumbY, ScrollbarThickness - 2, thumbH);
            }

            // 6. Sleek Bottom Horizontal Scrollbar
            hScrollTrackRect = Rectangle.Empty;
            int visCols = Math.Max(10, (codeAreaRight - GutterWidth - 16) / Math.Max(6, CharWidth));
            hScrollMaxCol = Math.Max(0, maxVisibleLineLen - visCols + 8);
            if (!buf.WordWrap && (hScrollMaxCol > 0 || buf.ScrollLeftCol > 0))
            {
                hScrollTrackRect = new Rectangle(GutterWidth, this.Height - 10, Math.Max(40, codeAreaRight - GutterWidth), 10);
                int thumbW = Math.Max(36, (int)((double)visCols / Math.Max(visCols + 1, maxVisibleLineLen + 8) * hScrollTrackRect.Width));
                int thumbX = hScrollTrackRect.X + (int)((double)Math.Min(buf.ScrollLeftCol, Math.Max(1, hScrollMaxCol)) / Math.Max(1, hScrollMaxCol) * (hScrollTrackRect.Width - thumbW));
                using (SolidBrush hThumb = new SolidBrush(Color.FromArgb(120, th.FgMuted)))
                    g.FillRectangle(hThumb, thumbX, hScrollTrackRect.Y + 2, thumbW, hScrollTrackRect.Height - 3);
            }

            // 7. Hover Diagnostic Floating Tooltip Card
            if (hoveredDiagnostic != null && !lastHoverMousePt.IsEmpty)
            {
                Color sevCol = hoveredDiagnostic.Severity == "Error"
                    ? Color.FromArgb(244, 63, 94)
                    : (hoveredDiagnostic.Severity == "Warning" ? Color.FromArgb(251, 191, 36) : th.AccentPrimary);
                string tipHeader = string.Format("{0} (Ln {1})", hoveredDiagnostic.Severity.ToUpperInvariant(), hoveredDiagnostic.Line);
                string tipBody = hoveredDiagnostic.Message ?? "";

                int tipW = Math.Min(420, Math.Max(220, TextRenderer.MeasureText(tipBody, gutterFont).Width + 32));
                int tipH = 48;
                int tipX = Math.Max(GutterWidth + 6, Math.Min(codeAreaRight - tipW - 6, lastHoverMousePt.X));
                int tipY = lastHoverMousePt.Y - tipH - 10;
                if (tipY < 6) tipY = lastHoverMousePt.Y + 20;

                Rectangle tipRect = new Rectangle(tipX, tipY, tipW, tipH);
                using (SolidBrush tbg = new SolidBrush(th.BgElevated))
                    g.FillRectangle(tbg, tipRect);
                using (SolidBrush stripe = new SolidBrush(sevCol))
                    g.FillRectangle(stripe, tipRect.X, tipRect.Y, 3, tipRect.Height);
                using (Pen tp = new Pen(sevCol, 1f))
                    g.DrawRectangle(tp, tipRect.X, tipRect.Y, tipRect.Width - 1, tipRect.Height - 1);

                TextRenderer.DrawText(g, tipHeader, boldFont, new Point(tipRect.X + 10, tipRect.Y + 6), sevCol, TextFormatFlags.NoPadding);
                TextRenderer.DrawText(g, tipBody, gutterFont, new Rectangle(tipRect.X + 10, tipRect.Y + 24, tipW - 16, 20), th.FgPrimary,
                    TextFormatFlags.Left | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix);
            }
        }

        private void DrawSyntaxHighlightedLine(Graphics g, string line, int startX, int y, int clipLeft, int clipRight, StudioTheme th)
        {
            if (string.IsNullOrEmpty(line)) return;

            int i = 0;
            int n = line.Length;
            while (i < n)
            {
                int tokenX = startX + i * CharWidth;
                if (tokenX > clipRight) break;

                // 1. Whitespace
                if (char.IsWhiteSpace(line[i]))
                {
                    i++;
                    continue;
                }

                // 2. Single-line Comment (// or #)
                if ((line[i] == '/' && i + 1 < n && line[i + 1] == '/') || line[i] == '#')
                {
                    string comment = line.Substring(i);
                    if (tokenX + comment.Length * CharWidth >= clipLeft)
                        TextRenderer.DrawText(g, comment, italicFont, new Point(tokenX, y), th.TokenComment, TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix);
                    break;
                }

                // 3. String Literal ("...", '...', `...`)
                if (line[i] == '"' || line[i] == '\'' || line[i] == '`')
                {
                    char quote = line[i];
                    int start = i++;
                    while (i < n)
                    {
                        if (line[i] == '\\') { i += 2; continue; }
                        if (line[i] == quote) { i++; break; }
                        i++;
                    }
                    string strTok = line.Substring(start, Math.Min(n - start, i - start));
                    if (tokenX + strTok.Length * CharWidth >= clipLeft)
                        TextRenderer.DrawText(g, strTok, regularFont, new Point(tokenX, y), th.TokenString, TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix);
                    continue;
                }

                // 4. Numbers
                if (char.IsDigit(line[i]))
                {
                    int start = i;
                    while (i < n && (char.IsLetterOrDigit(line[i]) || line[i] == '.')) i++;
                    string numTok = line.Substring(start, i - start);
                    if (tokenX >= clipLeft - 40)
                        TextRenderer.DrawText(g, numTok, regularFont, new Point(tokenX, y), th.TokenNumber, TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix);
                    continue;
                }

                // 5. Identifiers / Keywords / Types / Function Calls
                if (char.IsLetter(line[i]) || line[i] == '_' || line[i] == '@')
                {
                    int start = i;
                    i++;
                    while (i < n && (char.IsLetterOrDigit(line[i]) || line[i] == '_')) i++;
                    string word = line.Substring(start, i - start);

                    Color c = th.FgPrimary;
                    Font f = regularFont;
                    if (word.StartsWith("@")) { c = th.TokenKeyword; }
                    else if (ControlKeywords.Contains(word)) { c = th.TokenControl; f = boldFont; }
                    else if (StorageKeywords.Contains(word)) { c = th.TokenKeyword; f = boldFont; }
                    else if (TypeKeywords.Contains(word) || (word.Length > 1 && char.IsUpper(word[0]) && !word.All(char.IsUpper))) { c = th.TokenType; }
                    else if (i < n && line[i] == '(') { c = th.TokenFunction; }

                    if (tokenX + word.Length * CharWidth >= clipLeft)
                        TextRenderer.DrawText(g, word, f, new Point(tokenX, y), c, TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix);
                    continue;
                }

                // 6. Operators & Punctuation
                int opStart = i;
                while (i < n && !char.IsWhiteSpace(line[i]) && !char.IsLetterOrDigit(line[i]) &&
                       line[i] != '_' && line[i] != '"' && line[i] != '\'' && line[i] != '`' && line[i] != '#')
                {
                    if (line[i] == '/' && i + 1 < n && line[i + 1] == '/') break;
                    i++;
                }
                string opTok = line.Substring(opStart, i - opStart);
                if (tokenX >= clipLeft - 30)
                    TextRenderer.DrawText(g, opTok, regularFont, new Point(tokenX, y), th.TokenOperator, TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix);
            }
        }

        private void DrawWavySquiggle(Graphics g, int x, int y, int width, Color color)
        {
            if (width < 4) return;
            using (Pen p = new Pen(color, 1.4f))
            {
                List<Point> pts = new List<Point>();
                for (int dx = 0; dx <= width; dx += 3)
                {
                    int dy = ((dx / 3) % 2 == 0) ? -1 : 1;
                    pts.Add(new Point(x + dx, y + dy));
                }
                if (pts.Count >= 2)
                    g.DrawLines(p, pts.ToArray());
            }
        }

        public static int GetLineIndentWidth(string ln)
        {
            if (string.IsNullOrEmpty(ln)) return 0;
            int w = 0;
            for (int i = 0; i < ln.Length; i++)
            {
                if (ln[i] == ' ') w++;
                else if (ln[i] == '\t') w += 4;
                else break;
            }
            return w;
        }

        public bool IsFoldableHeaderLine(List<string> lines, int lineIdx)
        {
            if (lines == null || lineIdx < 0 || lineIdx >= lines.Count - 1) return false;
            string raw = lines[lineIdx] ?? "";
            string trimmed = raw.Trim();
            if (string.IsNullOrEmpty(trimmed) || trimmed.StartsWith("//") || trimmed.StartsWith("#") || trimmed == "}" || trimmed == "};")
                return false;
            if (trimmed.EndsWith("{") || trimmed.EndsWith(":"))
                return FindFoldEndLine(lines, lineIdx) > lineIdx;

            int baseIndent = GetLineIndentWidth(raw);
            for (int next = lineIdx + 1; next < lines.Count; next++)
            {
                string nextLn = lines[next] ?? "";
                if (string.IsNullOrWhiteSpace(nextLn)) continue;
                string nextTrim = nextLn.TrimStart();
                int nextIndent = GetLineIndentWidth(nextLn);
                if (nextIndent == baseIndent && nextTrim.StartsWith("{"))
                    return FindFoldEndLine(lines, lineIdx) > lineIdx;
                return nextIndent > baseIndent && FindFoldEndLine(lines, lineIdx) > lineIdx;
            }
            return false;
        }

        public int FindFoldEndLine(List<string> lines, int startLine)
        {
            if (lines == null || startLine < 0 || startLine >= lines.Count - 1) return startLine;
            string header = lines[startLine] ?? "";
            string headerTrim = header.TrimEnd();
            int baseIndent = GetLineIndentWidth(header);

            bool startsBraceBlock = headerTrim.EndsWith("{");
            int braceSearchStart = startLine;
            if (!startsBraceBlock)
            {
                for (int k = startLine + 1; k < lines.Count; k++)
                {
                    if (string.IsNullOrWhiteSpace(lines[k])) continue;
                    if (GetLineIndentWidth(lines[k]) == baseIndent && lines[k].TrimStart().StartsWith("{"))
                    {
                        startsBraceBlock = true;
                        braceSearchStart = k;
                    }
                    break;
                }
            }

            if (startsBraceBlock)
            {
                int depth = 0;
                bool inBlockComment = false;
                for (int i = braceSearchStart; i < lines.Count; i++)
                {
                    string ln = lines[i] ?? "";
                    bool inStr = false;
                    char strChar = '\0';
                    for (int c = 0; c < ln.Length; c++)
                    {
                        char ch = ln[c];
                        if (inBlockComment)
                        {
                            if (ch == '*' && c + 1 < ln.Length && ln[c + 1] == '/') { inBlockComment = false; c++; }
                            continue;
                        }
                        if (!inStr)
                        {
                            if (ch == '/' && c + 1 < ln.Length && ln[c + 1] == '/') break;
                            if (ch == '/' && c + 1 < ln.Length && ln[c + 1] == '*') { inBlockComment = true; c++; continue; }
                        }
                        if (ch == '"' || ch == '\'')
                        {
                            int bs = 0;
                            for (int k = c - 1; k >= 0 && ln[k] == '\\'; k--) bs++;
                            if ((bs % 2) == 0)
                            {
                                if (!inStr) { inStr = true; strChar = ch; continue; }
                                else if (ch == strChar) { inStr = false; continue; }
                            }
                        }
                        if (inStr) continue;

                        if (ch == '{') depth++;
                        else if (ch == '}')
                        {
                            depth--;
                            if (depth == 0 && i > startLine)
                                return i;
                        }
                    }
                }
            }

            int lastBodyLine = startLine;
            for (int i = startLine + 1; i < lines.Count; i++)
            {
                string ln = lines[i];
                if (string.IsNullOrWhiteSpace(ln)) continue;
                int ind = GetLineIndentWidth(ln);
                if (ind <= baseIndent)
                {
                    if (ln.TrimStart().StartsWith("}") || ln.TrimStart().StartsWith("]"))
                        return i;
                    return Math.Max(startLine, lastBodyLine);
                }
                lastBodyLine = i;
            }
            return Math.Max(startLine, lastBodyLine);
        }

        public bool ToggleFoldAtLine(int lineIdx)
        {
            EditorTabBuffer buf = owner.CurrentBuffer;
            if (buf == null || buf.Lines == null || lineIdx < 0 || lineIdx >= buf.Lines.Count)
                return false;
            if (buf.FoldedLines.Contains(lineIdx))
            {
                buf.FoldedLines.Remove(lineIdx);
                this.Invalidate();
                return true;
            }
            if (IsFoldableHeaderLine(buf.Lines, lineIdx) && FindFoldEndLine(buf.Lines, lineIdx) > lineIdx)
            {
                buf.FoldedLines.Add(lineIdx);
                if (buf.CursorLine > lineIdx && buf.CursorLine <= FindFoldEndLine(buf.Lines, lineIdx))
                {
                    buf.CursorLine = lineIdx;
                    buf.ClampCursor();
                }
                this.Invalidate();
                return true;
            }
            return false;
        }

        private int VisualColToCharCol(string lineText, int colStart, int colEnd, int targetVisCol)
        {
            if (string.IsNullOrEmpty(lineText) || targetVisCol <= 0) return colStart;
            int vis = 0;
            int limit = Math.Min(lineText.Length, colEnd);
            for (int i = colStart; i < limit; i++)
            {
                int step = (lineText[i] == '\t') ? 4 : 1;
                if (vis + step > targetVisCol)
                {
                    return (targetVisCol - vis >= (step + 1) / 2) ? (i + 1) : i;
                }
                vis += step;
            }
            return limit;
        }

        private void ScreenPointToLineCol(Point pt, out int line, out int col)
        {
            EditorTabBuffer buf = owner.CurrentBuffer;
            if (buf == null) { line = 0; col = 0; return; }

            int rowOffset = Math.Max(0, (pt.Y - 4) / LineHeight);
            int effScrollLeft = buf.WordWrap ? 0 : buf.ScrollLeftCol;

            if (lastPaintedRows.Count > 0)
            {
                int clampedRow = Math.Min(rowOffset, lastPaintedRows.Count - 1);
                VisualRowEntry seg = lastPaintedRows[clampedRow];
                line = Math.Max(0, Math.Min(buf.Lines.Count - 1, seg.DocLine));
                int segVisCol = effScrollLeft + (int)Math.Round((double)(pt.X - GutterWidth - 8) / CharWidth);
                col = VisualColToCharCol(buf.Lines[line], seg.ColStart, seg.ColEnd, segVisCol);
                return;
            }

            int docLine = buf.ScrollTopLine;
            for (int r = 0; r < rowOffset && docLine < buf.Lines.Count - 1; r++)
            {
                if (buf.FoldedLines.Contains(docLine))
                    docLine = FindFoldEndLine(buf.Lines, docLine);
                docLine = Math.Min(buf.Lines.Count - 1, docLine + 1);
            }
            line = Math.Max(0, Math.Min(buf.Lines.Count - 1, docLine));
            int rawVisCol = effScrollLeft + (int)Math.Round((double)(pt.X - GutterWidth - 8) / CharWidth);
            col = VisualColToCharCol(buf.Lines[line], 0, buf.Lines[line].Length, rawVisCol);
        }

        private void PushCursorHistorySnapshot(EditorTabBuffer buf)
        {
            if (buf == null) return;
            var clonedExtras = new List<EditorCaret>();
            if (buf.ExtraCarets != null)
            {
                foreach (EditorCaret ec in buf.ExtraCarets)
                {
                    clonedExtras.Add(new EditorCaret
                    {
                        AnchorLine = ec.AnchorLine,
                        AnchorCol = ec.AnchorCol,
                        CursorLine = ec.CursorLine,
                        CursorCol = ec.CursorCol
                    });
                }
            }
            cursorHistoryStack.Push(new CursorHistoryState
            {
                AnchorLine = buf.SelectionAnchorLine,
                AnchorCol = buf.SelectionAnchorCol,
                CursorLine = buf.CursorLine,
                CursorCol = buf.CursorCol,
                ExtraCarets = clonedExtras
            });
        }

        public void UndoLastCursorOperation()
        {
            EditorTabBuffer buf = owner.CurrentBuffer;
            if (buf == null) return;
            if (cursorHistoryStack.Count > 0)
            {
                CursorHistoryState prev = cursorHistoryStack.Pop();
                buf.SelectionAnchorLine = Math.Max(0, Math.Min(buf.Lines.Count - 1, prev.AnchorLine));
                buf.SelectionAnchorCol = Math.Max(0, Math.Min((buf.Lines[buf.SelectionAnchorLine] ?? "").Length, prev.AnchorCol));
                buf.CursorLine = Math.Max(0, Math.Min(buf.Lines.Count - 1, prev.CursorLine));
                buf.CursorCol = Math.Max(0, Math.Min((buf.Lines[buf.CursorLine] ?? "").Length, prev.CursorCol));
                buf.ExtraCarets.Clear();
                if (prev.ExtraCarets != null)
                {
                    foreach (EditorCaret ec in prev.ExtraCarets)
                    {
                        int el = Math.Max(0, Math.Min(buf.Lines.Count - 1, ec.CursorLine));
                        int al = Math.Max(0, Math.Min(buf.Lines.Count - 1, ec.AnchorLine));
                        buf.ExtraCarets.Add(new EditorCaret
                        {
                            AnchorLine = al,
                            AnchorCol = Math.Max(0, Math.Min((buf.Lines[al] ?? "").Length, ec.AnchorCol)),
                            CursorLine = el,
                            CursorCol = Math.Max(0, Math.Min((buf.Lines[el] ?? "").Length, ec.CursorCol))
                        });
                    }
                }
                owner.UpdateCursorStatusLabel();
                this.Invalidate();
            }
            else if (buf.ExtraCarets != null && buf.ExtraCarets.Count > 0)
            {
                buf.ExtraCarets.RemoveAt(buf.ExtraCarets.Count - 1);
                owner.UpdateCursorStatusLabel();
                this.Invalidate();
            }
        }

        private void ApplyGutterLineRangeSelection(EditorTabBuffer buf, int anchorLine, int currentLine)
        {
            if (buf == null || buf.Lines.Count == 0) return;
            int minL = Math.Max(0, Math.Min(buf.Lines.Count - 1, Math.Min(anchorLine, currentLine)));
            int maxL = Math.Max(0, Math.Min(buf.Lines.Count - 1, Math.Max(anchorLine, currentLine)));
            buf.ClearExtraCarets();
            if (currentLine >= anchorLine)
            {
                buf.SelectionAnchorLine = minL;
                buf.SelectionAnchorCol = 0;
                if (maxL + 1 < buf.Lines.Count)
                {
                    buf.CursorLine = maxL + 1;
                    buf.CursorCol = 0;
                }
                else
                {
                    buf.CursorLine = maxL;
                    buf.CursorCol = (buf.Lines[maxL] ?? "").Length;
                }
            }
            else
            {
                if (maxL + 1 < buf.Lines.Count)
                {
                    buf.SelectionAnchorLine = maxL + 1;
                    buf.SelectionAnchorCol = 0;
                }
                else
                {
                    buf.SelectionAnchorLine = maxL;
                    buf.SelectionAnchorCol = (buf.Lines[maxL] ?? "").Length;
                }
                buf.CursorLine = minL;
                buf.CursorCol = 0;
            }
        }

        public void ApplyBoxSelection(EditorTabBuffer buf, int startLine, int startCol, int endLine, int endCol)
        {
            if (buf == null || buf.Lines.Count == 0) return;
            int minL = Math.Max(0, Math.Min(buf.Lines.Count - 1, Math.Min(startLine, endLine)));
            int maxL = Math.Max(0, Math.Min(buf.Lines.Count - 1, Math.Max(startLine, endLine)));
            int minC = Math.Max(0, Math.Min(startCol, endCol));
            int maxC = Math.Max(0, Math.Max(startCol, endCol));
            bool forwardCols = endCol >= startCol;

            buf.ExtraCarets.Clear();
            for (int l = minL; l <= maxL; l++)
            {
                int lnLen = (buf.Lines[l] ?? "").Length;
                int c0 = Math.Min(lnLen, minC);
                int c1 = Math.Min(lnLen, maxC);
                int aCol = forwardCols ? c0 : c1;
                int cCol = forwardCols ? c1 : c0;
                if (l == minL)
                {
                    buf.SelectionAnchorLine = l;
                    buf.SelectionAnchorCol = aCol;
                    buf.CursorLine = l;
                    buf.CursorCol = cCol;
                }
                else
                {
                    buf.ExtraCarets.Add(new EditorCaret
                    {
                        AnchorLine = l,
                        AnchorCol = aCol,
                        CursorLine = l,
                        CursorCol = cCol
                    });
                }
            }
        }

        private static bool IsPositionStrictlyInsideSelection(EditorTabBuffer buf, int line, int col)
        {
            if (buf == null || !buf.HasSelection()) return false;
            int sL, sC, eL, eC;
            buf.GetNormalizedSelection(out sL, out sC, out eL, out eC);
            if (line < sL || line > eL) return false;
            if (sL == eL) return col >= sC && col < eC;
            if (line == sL) return col >= sC;
            if (line == eL) return col < eC;
            return true;
        }

        private static string GetWordAtPosition(EditorTabBuffer buf, int line, int col)
        {
            if (buf == null || line < 0 || line >= buf.Lines.Count) return "";
            string txt = buf.Lines[line] ?? "";
            if (txt.Length == 0) return "";
            int idx = Math.Max(0, Math.Min(txt.Length - 1, col));
            int s = idx, e = idx;
            while (s > 0 && IsIdentifierChar(txt[s - 1])) s--;
            while (e < txt.Length && IsIdentifierChar(txt[e])) e++;
            return e > s ? txt.Substring(s, e - s) : "";
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            this.Focus();
            EditorTabBuffer buf = owner.CurrentBuffer;
            if (buf == null) return;

            if (TryClickLineAnnotation(e)) return;

            // 0. Check Per-Hunk Inline [Accept] / [Reject] Diff Pills First
            if (e.Button == MouseButtons.Left && diffPillHits.Count > 0)
            {
                foreach (InlineDiffPillHit hit in diffPillHits)
                {
                    if (hit.AcceptRect.Contains(e.Location))
                    {
                        owner.AcceptSingleHunk(hit.Hunk);
                        return;
                    }
                    if (hit.RejectRect.Contains(e.Location))
                    {
                        owner.RejectSingleHunk(hit.Hunk);
                        return;
                    }
                }
            }

            // 0b. Horizontal Scrollbar Click / Drag
            if (!hScrollTrackRect.IsEmpty && hScrollTrackRect.Contains(e.Location))
            {
                isDraggingHScroll = true;
                ScrollFromHScrollX(e.X);
                return;
            }

            int rightMinimapStart = this.Width - (EffectiveShowMinimap ? MinimapWidth : 0);

            // 1. Minimap Click / Drag
            if (EffectiveShowMinimap && e.X >= rightMinimapStart)
            {
                isDraggingMinimap = true;
                ScrollFromMinimapY(e.Y);
                return;
            }

            // 2. Vertical Scrollbar Click / Drag
            if (e.X >= rightMinimapStart - ScrollbarThickness && e.X < rightMinimapStart)
            {
                isDraggingVScroll = true;
                ScrollFromVScrollY(e.Y);
                return;
            }

            // 3. Gutter Click (Breakpoints, Line Number Selection, or Code Folding)
            if (e.X < GutterWidth)
            {
                int clickedLine, dummyCol;
                ScreenPointToLineCol(e.Location, out clickedLine, out dummyCol);
                if (e.X >= GutterWidth - 18)
                {
                    ToggleFoldAtLine(clickedLine);
                }
                else if (e.X < 18)
                {
                    if (buf.Breakpoints.Contains(clickedLine))
                        buf.Breakpoints.Remove(clickedLine);
                    else
                        buf.Breakpoints.Add(clickedLine);
                    EditorDebugHooks.NotifyBreakpointToggled(buf, clickedLine);
                }
                else
                {
                    PushCursorHistorySnapshot(buf);
                    isDraggingGutterLineSelection = true;
                    gutterDragAnchorLine = clickedLine;
                    ApplyGutterLineRangeSelection(buf, clickedLine, clickedLine);
                    owner.UpdateCursorStatusLabel();
                }
                this.Invalidate();
                return;
            }

            int targetLine, targetCol;
            ScreenPointToLineCol(e.Location, out targetLine, out targetCol);

            // Preserve selection on Right-Click so Context Menu actions work on selected code
            if (e.Button == MouseButtons.Right)
            {
                if (!buf.HasSelection())
                {
                    buf.CursorLine = targetLine;
                    buf.CursorCol = targetCol;
                    buf.SelectionAnchorLine = targetLine;
                    buf.SelectionAnchorCol = targetCol;
                    owner.UpdateCursorStatusLabel();
                    this.Invalidate();
                }
                return;
            }

            Keys mods = EffectiveModifierKeys;

            // Alt+Click: Add or Remove extra multi-cursor at clicked position
            if (mods == Keys.Alt)
            {
                PushCursorHistorySnapshot(buf);
                int existingIdx = buf.ExtraCarets.FindIndex(ec => ec.CursorLine == targetLine && ec.CursorCol == targetCol);
                if (existingIdx >= 0)
                {
                    buf.ExtraCarets.RemoveAt(existingIdx);
                }
                else if (!(buf.CursorLine == targetLine && buf.CursorCol == targetCol))
                {
                    buf.ExtraCarets.Add(new EditorCaret
                    {
                        AnchorLine = targetLine,
                        AnchorCol = targetCol,
                        CursorLine = targetLine,
                        CursorCol = targetCol
                    });
                }
                owner.UpdateCursorStatusLabel();
                this.Invalidate();
                return;
            }

            // Ctrl+Click: Go to Definition on identifier under cursor
            if (mods == Keys.Control)
            {
                PushCursorHistorySnapshot(buf);
                buf.ClearExtraCarets();
                buf.CursorLine = targetLine;
                buf.CursorCol = targetCol;
                buf.SelectionAnchorLine = targetLine;
                buf.SelectionAnchorCol = targetCol;
                string word = GetWordAtPosition(buf, targetLine, targetCol);
                owner.UpdateCursorStatusLabel();
                int underCol = CharIndexUnderPoint(e.Location);
                ClearLinkHover();
                this.Invalidate();
                if (underCol >= 0 && owner.TriggerFollowLinkAt(targetLine, underCol)) return;
                if (!string.IsNullOrEmpty(word))
                    owner.TriggerGoToDefinition(word);
                return;
            }

            // Shift+Alt+Click/Drag or ColumnSelectionMode: Box / Rectangular Column Selection
            if ((mods & (Keys.Shift | Keys.Alt)) == (Keys.Shift | Keys.Alt) || (ColumnSelectionMode && (mods & Keys.Shift) == Keys.Shift))
            {
                PushCursorHistorySnapshot(buf);
                if (!isDraggingBoxSelection)
                {
                    boxAnchorLine = buf.CursorLine;
                    boxAnchorCol = buf.CursorCol;
                }
                boxActiveLine = targetLine;
                boxActiveCol = targetCol;
                isDraggingBoxSelection = true;
                ApplyBoxSelection(buf, boxAnchorLine, boxAnchorCol, boxActiveLine, boxActiveCol);
                owner.UpdateCursorStatusLabel();
                this.Invalidate();
                return;
            }

            // Drag-and-drop selected text when clicking inside an existing selection without modifiers
            if (e.Clicks == 1 && mods == Keys.None && IsPositionStrictlyInsideSelection(buf, targetLine, targetCol))
            {
                isPendingTextDrag = true;
                isDraggingSelectedText = false;
                textDragStartPt = e.Location;
                dragDropTargetLine = targetLine;
                dragDropTargetCol = targetCol;
                return;
            }

            // 4. Normal left-click clears multi-cursors
            PushCursorHistorySnapshot(buf);
            buf.ClearExtraCarets();

            if (owner.ShowCodeLens && buf.Symbols != null && targetLine >= 0 && targetLine < buf.Lines.Count)
            {
                string lnStr = buf.Lines[targetLine] ?? "";
                int lensStartX = GutterWidth + 8 + (lnStr.Length - buf.ScrollLeftCol + 3) * CharWidth;
                if (!buf.WordWrap && e.X >= lensStartX && buf.Symbols.Any(s => s.Line == targetLine + 1))
                {
                    buf.CursorLine = targetLine;
                    buf.CursorCol = lnStr.Length;
                    owner.ToggleCtrlKBar();
                    this.Invalidate();
                    return;
                }
            }

            if (e.Clicks == 2)
            {
                SelectWordAt(targetLine, targetCol);
                owner.UpdateCursorStatusLabel();
                return;
            }
            if (e.Clicks >= 3)
            {
                buf.SelectionAnchorLine = targetLine;
                buf.SelectionAnchorCol = 0;
                buf.CursorLine = targetLine;
                buf.CursorCol = buf.Lines[targetLine].Length;
                owner.UpdateCursorStatusLabel();
                this.Invalidate();
                return;
            }

            if ((mods & Keys.Shift) == Keys.Shift)
            {
                if (!buf.HasSelection())
                {
                    buf.SelectionAnchorLine = buf.CursorLine;
                    buf.SelectionAnchorCol = buf.CursorCol;
                }
            }
            else
            {
                buf.SelectionAnchorLine = targetLine;
                buf.SelectionAnchorCol = targetCol;
            }

            buf.CursorLine = targetLine;
            buf.CursorCol = targetCol;
            buf.GhostText = null;
            if (ColumnSelectionMode)
            {
                boxAnchorLine = buf.SelectionAnchorLine;
                boxAnchorCol = buf.SelectionAnchorCol;
                boxActiveLine = targetLine;
                boxActiveCol = targetCol;
                isDraggingBoxSelection = true;
            }
            else
            {
                isDraggingSelection = true;
            }
            owner.DismissAutocomplete();
            owner.UpdateCursorStatusLabel();
            this.Invalidate();
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            if (MouseMovedOverCanvas != null && e.Button == MouseButtons.None) MouseMovedOverCanvas(e.Location);
            int rightMinimapStart = this.Width - (EffectiveShowMinimap ? MinimapWidth : 0);

            bool overDiffPill = diffPillHits.Any(h => h.AcceptRect.Contains(e.Location) || h.RejectRect.Contains(e.Location));
            bool overHScroll = !hScrollTrackRect.IsEmpty && hScrollTrackRect.Contains(e.Location);

            lastMouseClientPt = e.Location;
            bool anyDrag = isDraggingHScroll || isDraggingVScroll || isDraggingMinimap || isDraggingGutterLineSelection ||
                           isDraggingBoxSelection || isPendingTextDrag || isDraggingSelectedText || isDraggingSelection;
            if (!anyDrag) UpdateLinkHover(e.Location);
            else ClearLinkHover();
            bool overAnnotation = !anyDrag && UpdateLineAnnotationHover(e.Location);
            this.Cursor = (overDiffPill || overHScroll || overAnnotation || e.X >= rightMinimapStart - ScrollbarThickness)
                ? Cursors.Hand
                : (e.X < GutterWidth ? Cursors.Arrow
                : (isDraggingSelectedText ? Cursors.Arrow : (linkHover != null ? Cursors.Hand : Cursors.IBeam)));

            if (isDraggingHScroll)
            {
                ScrollFromHScrollX(e.X);
                return;
            }

            if (isDraggingVScroll)
            {
                ScrollFromVScrollY(e.Y);
                return;
            }

            if (isDraggingMinimap)
            {
                ScrollFromMinimapY(e.Y);
                return;
            }

            if (isDraggingGutterLineSelection && owner.CurrentBuffer != null)
            {
                int l, c;
                ScreenPointToLineCol(e.Location, out l, out c);
                ApplyGutterLineRangeSelection(owner.CurrentBuffer, gutterDragAnchorLine, l);
                EnsureCursorVisible();
                owner.UpdateCursorStatusLabel();
                this.Invalidate();
                return;
            }

            if (isDraggingBoxSelection && owner.CurrentBuffer != null)
            {
                int l, c;
                ScreenPointToLineCol(e.Location, out l, out c);
                boxActiveLine = l;
                boxActiveCol = c;
                ApplyBoxSelection(owner.CurrentBuffer, boxAnchorLine, boxAnchorCol, boxActiveLine, boxActiveCol);
                EnsureCursorVisible();
                owner.UpdateCursorStatusLabel();
                this.Invalidate();
                return;
            }

            if ((isPendingTextDrag || isDraggingSelectedText) && owner.CurrentBuffer != null)
            {
                if (!isDraggingSelectedText && (Math.Abs(e.X - textDragStartPt.X) >= 4 || Math.Abs(e.Y - textDragStartPt.Y) >= 4))
                {
                    isDraggingSelectedText = true;
                }
                if (isDraggingSelectedText)
                {
                    ScreenPointToLineCol(e.Location, out dragDropTargetLine, out dragDropTargetCol);
                    this.Invalidate();
                }
                return;
            }

            if (isDraggingSelection && owner.CurrentBuffer != null)
            {
                int l, c;
                ScreenPointToLineCol(e.Location, out l, out c);
                owner.CurrentBuffer.CursorLine = l;
                owner.CurrentBuffer.CursorCol = c;
                EnsureCursorVisible();
                owner.UpdateCursorStatusLabel();
                this.Invalidate();
                return;
            }

            // Diagnostic hover tooltip hit-testing
            EditorTabBuffer buf = owner.CurrentBuffer;
            DiagnosticItem nextHover = null;
            if (buf != null && buf.Diagnostics != null && e.X > GutterWidth && e.X < rightMinimapStart - ScrollbarThickness && !overDiffPill)
            {
                int hl, hc;
                ScreenPointToLineCol(e.Location, out hl, out hc);
                nextHover = buf.Diagnostics.FirstOrDefault(d => d.Line == hl + 1 && hc >= Math.Max(0, d.Column - 2));
            }
            if (nextHover != hoveredDiagnostic)
            {
                hoveredDiagnostic = nextHover;
                lastHoverMousePt = e.Location;
                this.Invalidate();
            }
        }

        // ---- Ctrl+hover link underline (VS Code parity) ------------------------------------------------------
        private EditorLinkTarget linkHover;
        private int linkHoverLine = -1;
        private Point lastMouseClientPt = new Point(-1, -1);

        /// <summary>Character under the pointer (not the nearest caret boundary); -1 when not over text.</summary>
        private int CharIndexUnderPoint(Point pt)
        {
            EditorTabBuffer buf = owner.CurrentBuffer;
            if (buf == null || pt.X < GutterWidth) return -1;
            int line, col;
            ScreenPointToLineCol(new Point(pt.X - CharWidth / 2, pt.Y), out line, out col);
            if (line < 0 || line >= buf.Lines.Count) return -1;
            string text = buf.Lines[line] ?? "";
            if (col >= text.Length) return -1;
            return col;
        }

        private void UpdateLinkHover(Point pt)
        {
            EditorTabBuffer buf = owner.CurrentBuffer;
            EditorLinkTarget next = null;
            int nextLine = -1;
            int rightMinimapStart = this.Width - (EffectiveShowMinimap ? MinimapWidth : 0);
            Keys mods = EffectiveModifierKeys;
            if (buf != null && mods == Keys.Control && pt.X >= GutterWidth && pt.X < rightMinimapStart - ScrollbarThickness &&
                pt.Y >= 0 && pt.Y < this.Height)
            {
                int col = CharIndexUnderPoint(pt);
                if (col >= 0)
                {
                    int line, dummy;
                    ScreenPointToLineCol(pt, out line, out dummy);
                    nextLine = line;
                    if (linkHover != null && linkHoverLine == line && col >= linkHover.StartCol && col < linkHover.EndCol)
                        next = linkHover;
                    else
                        next = owner.ResolveLinkAt(buf, line, col);
                }
            }
            if (!object.ReferenceEquals(next, linkHover))
            {
                linkHover = next;
                linkHoverLine = next != null ? nextLine : -1;
                this.Invalidate();
            }
        }

        private void ClearLinkHover()
        {
            if (linkHover == null) return;
            linkHover = null;
            linkHoverLine = -1;
            this.Invalidate();
        }

        /// <summary>Re-evaluates the hover when Ctrl is pressed / released without moving the mouse.</summary>
        public void RefreshLinkHoverForModifiers()
        {
            if (lastMouseClientPt.X < 0) return;
            Point p = this.PointToClient(Control.MousePosition);
            if (!this.ClientRectangle.Contains(p)) { ClearLinkHover(); return; }
            UpdateLinkHover(p);
            int rightMinimapStart = this.Width - (EffectiveShowMinimap ? MinimapWidth : 0);
            if (p.X >= GutterWidth && p.X < rightMinimapStart - ScrollbarThickness)
                this.Cursor = linkHover != null ? Cursors.Hand : Cursors.IBeam;
        }

        protected override void OnKeyUp(KeyEventArgs e)
        {
            base.OnKeyUp(e);
            if (e.KeyCode == Keys.ControlKey || e.KeyCode == Keys.LControlKey || e.KeyCode == Keys.RControlKey ||
                e.KeyCode == Keys.ShiftKey || e.KeyCode == Keys.Menu)
                RefreshLinkHoverForModifiers();
        }

        private void DrawLinkHoverUnderline(Graphics g, StudioTheme th, EditorTabBuffer buf)
        {
            if (linkHover == null || buf == null || linkHoverLine < 0 || linkHoverLine >= buf.Lines.Count) return;
            string text = buf.Lines[linkHoverLine] ?? "";
            int effScrollLeft = buf.WordWrap ? 0 : buf.ScrollLeftCol;
            using (Pen pen = new Pen(th.AccentPrimary, 1f))
            {
                for (int r = 0; r < lastPaintedRows.Count; r++)
                {
                    VisualRowEntry vre = lastPaintedRows[r];
                    if (vre.DocLine != linkHoverLine) continue;
                    int s0 = Math.Max(linkHover.StartCol, vre.ColStart);
                    int s1 = Math.Min(Math.Min(linkHover.EndCol, text.Length), vre.ColEnd);
                    if (s1 <= s0) continue;
                    int x0 = GutterWidth + 8 + (CharColToVisualCol(text, vre.ColStart, s0) - effScrollLeft) * CharWidth;
                    int x1 = GutterWidth + 8 + (CharColToVisualCol(text, vre.ColStart, s1) - effScrollLeft) * CharWidth;
                    x0 = Math.Max(GutterWidth, x0);
                    if (x1 <= x0) continue;
                    int y = r * LineHeight + 4 + LineHeight - 3;
                    g.DrawLine(pen, x0, y, x1, y);
                }
            }
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            ClearLinkHover();
            if (MouseLeftCanvas != null) MouseLeftCanvas();
            ClearLineAnnotationHover();
            if (hoveredDiagnostic != null)
            {
                hoveredDiagnostic = null;
                this.Invalidate();
            }
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            EditorTabBuffer buf = owner.CurrentBuffer;
            if (isPendingTextDrag && buf != null)
            {
                if (isDraggingSelectedText && dragDropTargetLine >= 0 && !IsPositionStrictlyInsideSelection(buf, dragDropTargetLine, dragDropTargetCol))
                {
                    string draggedText = buf.GetSelectedText();
                    if (!string.IsNullOrEmpty(draggedText))
                    {
                        buf.PushUndoSnapshot();
                        bool copyMode = (EffectiveModifierKeys & Keys.Control) == Keys.Control;
                        int sL, sC, eL, eC;
                        buf.GetNormalizedSelection(out sL, out sC, out eL, out eC);
                        int dropL = dragDropTargetLine;
                        int dropC = dragDropTargetCol;
                        if (!copyMode)
                        {
                            if (dropL > eL || (dropL == eL && dropC >= eC))
                            {
                                if (dropL == eL)
                                    dropC = sC + (dropC - eC);
                                dropL -= (eL - sL);
                            }
                            ReplaceSelectionWithText("");
                        }
                        dropL = Math.Max(0, Math.Min(buf.Lines.Count - 1, dropL));
                        dropC = Math.Max(0, Math.Min((buf.Lines[dropL] ?? "").Length, dropC));
                        buf.CursorLine = dropL;
                        buf.CursorCol = dropC;
                        buf.ClearSelection();
                        int insStartL = dropL;
                        int insStartC = dropC;
                        ReplaceSelectionWithText(draggedText);
                        buf.SelectionAnchorLine = insStartL;
                        buf.SelectionAnchorCol = insStartC;
                        owner.NotifyBufferContentChanged();
                    }
                }
                else if (!isDraggingSelectedText)
                {
                    int targetLine, targetCol;
                    ScreenPointToLineCol(e.Location, out targetLine, out targetCol);
                    buf.CursorLine = targetLine;
                    buf.CursorCol = targetCol;
                    buf.ClearSelection();
                    owner.UpdateCursorStatusLabel();
                }
                isPendingTextDrag = false;
                isDraggingSelectedText = false;
                this.Invalidate();
            }
            isDraggingSelection = false;
            isDraggingGutterLineSelection = false;
            isDraggingBoxSelection = false;
            isDraggingMinimap = false;
            isDraggingVScroll = false;
            isDraggingHScroll = false;
            if (buf != null &&
                buf.SelectionAnchorLine == buf.CursorLine &&
                buf.SelectionAnchorCol == buf.CursorCol)
            {
                buf.ClearSelection();
            }
        }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            base.OnMouseWheel(e);
            if (MouseLeftCanvas != null) MouseLeftCanvas();
            EditorTabBuffer buf = owner.CurrentBuffer;
            if (buf == null) return;

            if ((Control.ModifierKeys & Keys.Control) == Keys.Control)
            {
                SetFontSize(FontSizePt + (e.Delta > 0 ? 1.0f : -1.0f));
                return;
            }

            if ((Control.ModifierKeys & Keys.Shift) == Keys.Shift && !buf.WordWrap)
            {
                int colsDelta = e.Delta > 0 ? -6 : 6;
                buf.ScrollLeftCol = Math.Max(0, Math.Min(Math.Max(0, hScrollMaxCol), buf.ScrollLeftCol + colsDelta));
                owner.DismissAutocomplete();
                this.Invalidate();
                return;
            }

            int linesDelta = e.Delta > 0 ? -3 : 3;
            buf.ScrollTopLine = Math.Max(0, Math.Min(Math.Max(0, buf.Lines.Count - 1), buf.ScrollTopLine + linesDelta));
            owner.DismissAutocomplete();
            this.Invalidate();
        }

        private void ScrollFromMinimapY(int mouseY)
        {
            EditorTabBuffer buf = owner.CurrentBuffer;
            if (buf == null || this.Height <= 0) return;
            double ratio = Math.Max(0.0, Math.Min(1.0, (double)mouseY / this.Height));
            buf.ScrollTopLine = (int)(ratio * Math.Max(0, buf.Lines.Count - VisibleLinesCount));
            this.Invalidate();
        }

        private void ScrollFromVScrollY(int mouseY)
        {
            EditorTabBuffer buf = owner.CurrentBuffer;
            if (buf == null || this.Height <= 0) return;
            int totalLines = buf.Lines.Count;
            int visLines = VisibleLinesCount;
            if (totalLines <= visLines) return;
            int thumbH = Math.Max(24, (int)((double)visLines / totalLines * this.Height));
            int trackRange = Math.Max(1, this.Height - thumbH);
            double ratio = Math.Max(0.0, Math.Min(1.0, (double)(mouseY - thumbH / 2) / trackRange));
            buf.ScrollTopLine = (int)Math.Round(ratio * Math.Max(0, totalLines - visLines));
            this.Invalidate();
        }

        private void ScrollFromHScrollX(int mouseX)
        {
            EditorTabBuffer buf = owner.CurrentBuffer;
            if (buf == null || hScrollTrackRect.Width <= 0) return;
            int thumbW = Math.Max(36, hScrollTrackRect.Width / 6);
            int trackRange = Math.Max(1, hScrollTrackRect.Width - thumbW);
            double ratio = Math.Max(0.0, Math.Min(1.0, (double)(mouseX - hScrollTrackRect.X - thumbW / 2) / trackRange));
            buf.ScrollLeftCol = (int)Math.Round(ratio * Math.Max(0, hScrollMaxCol));
            this.Invalidate();
        }

        private void SelectWordAt(int line, int col)
        {
            EditorTabBuffer buf = owner.CurrentBuffer;
            if (buf == null) return;
            string txt = buf.Lines[line];
            if (string.IsNullOrEmpty(txt)) return;
            int idx = Math.Max(0, Math.Min(txt.Length - 1, col));
            int s = idx, e = idx;
            while (s > 0 && (char.IsLetterOrDigit(txt[s - 1]) || txt[s - 1] == '_')) s--;
            while (e < txt.Length && (char.IsLetterOrDigit(txt[e]) || txt[e] == '_')) e++;
            buf.SelectionAnchorLine = line;
            buf.SelectionAnchorCol = s;
            buf.CursorLine = line;
            buf.CursorCol = e;
            this.Invalidate();
        }

        public void AddNextOccurrenceCursor()
        {
            EditorTabBuffer buf = owner.CurrentBuffer;
            if (buf == null) return;
            PushCursorHistorySnapshot(buf);

            if (!buf.HasSelection())
            {
                buf.ClearExtraCarets();
                SelectWordAt(buf.CursorLine, buf.CursorCol);
                owner.UpdateCursorStatusLabel();
                this.Invalidate();
                return;
            }

            string word = buf.GetSelectedText();
            if (string.IsNullOrEmpty(word) || word.Contains("\n")) return;

            // Start search after last existing caret
            int startLine = buf.CursorLine;
            int startCol = buf.CursorCol;
            if (buf.ExtraCarets != null && buf.ExtraCarets.Count > 0)
            {
                EditorCaret last = buf.ExtraCarets[buf.ExtraCarets.Count - 1];
                startLine = last.CursorLine;
                startCol = last.CursorCol;
            }

            for (int pass = 0; pass < 2; pass++)
            {
                int fromL = pass == 0 ? startLine : 0;
                int toL = pass == 0 ? buf.Lines.Count - 1 : startLine;
                for (int l = fromL; l <= toL; l++)
                {
                    string ln = buf.Lines[l] ?? "";
                    int searchFrom = (pass == 0 && l == startLine) ? Math.Min(ln.Length, startCol) : 0;
                    int idx = ln.IndexOf(word, searchFrom, StringComparison.Ordinal);
                    while (idx >= 0)
                    {
                        bool alreadyPrimary = (l == buf.CursorLine && idx == Math.Min(buf.SelectionAnchorCol, buf.CursorCol));
                        bool alreadyExtra = buf.ExtraCarets.Any(ec => ec.CursorLine == l && Math.Min(ec.AnchorCol, ec.CursorCol) == idx);
                        if (!alreadyPrimary && !alreadyExtra)
                        {
                            buf.ExtraCarets.Add(new EditorCaret
                            {
                                AnchorLine = l,
                                AnchorCol = idx,
                                CursorLine = l,
                                CursorCol = idx + word.Length
                            });
                            owner.UpdateCursorStatusLabel();
                            this.Invalidate();
                            return;
                        }
                        idx = ln.IndexOf(word, idx + word.Length, StringComparison.Ordinal);
                    }
                }
            }
        }

        public void SkipAndAddNextOccurrenceCursor()
        {
            EditorTabBuffer buf = owner.CurrentBuffer;
            if (buf == null) return;
            if (!buf.HasSelection())
            {
                AddNextOccurrenceCursor();
                return;
            }
            string word = buf.GetSelectedText();
            if (string.IsNullOrEmpty(word) || word.Contains("\n")) return;

            PushCursorHistorySnapshot(buf);
            int startLine = buf.CursorLine;
            int startCol = buf.CursorCol;
            bool hadExtra = buf.ExtraCarets != null && buf.ExtraCarets.Count > 0;
            if (hadExtra)
            {
                EditorCaret last = buf.ExtraCarets[buf.ExtraCarets.Count - 1];
                startLine = last.CursorLine;
                startCol = Math.Max(last.AnchorCol, last.CursorCol);
                buf.ExtraCarets.RemoveAt(buf.ExtraCarets.Count - 1);
            }

            for (int pass = 0; pass < 2; pass++)
            {
                int fromL = pass == 0 ? startLine : 0;
                int toL = pass == 0 ? buf.Lines.Count - 1 : startLine;
                for (int l = fromL; l <= toL; l++)
                {
                    string ln = buf.Lines[l] ?? "";
                    int searchFrom = (pass == 0 && l == startLine) ? Math.Min(ln.Length, startCol) : 0;
                    int idx = ln.IndexOf(word, searchFrom, StringComparison.Ordinal);
                    while (idx >= 0)
                    {
                        bool alreadyPrimary = (l == buf.CursorLine && idx == Math.Min(buf.SelectionAnchorCol, buf.CursorCol));
                        bool alreadyExtra = buf.ExtraCarets.Any(ec => ec.CursorLine == l && Math.Min(ec.AnchorCol, ec.CursorCol) == idx);
                        if (!alreadyPrimary && !alreadyExtra)
                        {
                            if (hadExtra)
                            {
                                buf.ExtraCarets.Add(new EditorCaret
                                {
                                    AnchorLine = l,
                                    AnchorCol = idx,
                                    CursorLine = l,
                                    CursorCol = idx + word.Length
                                });
                            }
                            else
                            {
                                buf.SelectionAnchorLine = l;
                                buf.SelectionAnchorCol = idx;
                                buf.CursorLine = l;
                                buf.CursorCol = idx + word.Length;
                            }
                            EnsureCursorVisible();
                            owner.UpdateCursorStatusLabel();
                            this.Invalidate();
                            return;
                        }
                        idx = ln.IndexOf(word, idx + word.Length, StringComparison.Ordinal);
                    }
                }
            }
            owner.UpdateCursorStatusLabel();
            this.Invalidate();
        }

        public void SelectCurrentLine()
        {
            EditorTabBuffer buf = owner.CurrentBuffer;
            if (buf == null || buf.Lines.Count == 0) return;
            PushCursorHistorySnapshot(buf);
            buf.ClearExtraCarets();
            int sL, sC, eL, eC;
            buf.GetNormalizedSelection(out sL, out sC, out eL, out eC);
            if (!buf.HasSelection())
            {
                buf.SelectionAnchorLine = buf.CursorLine;
                buf.SelectionAnchorCol = 0;
                if (buf.CursorLine + 1 < buf.Lines.Count)
                {
                    buf.CursorLine = buf.CursorLine + 1;
                    buf.CursorCol = 0;
                }
                else
                {
                    buf.CursorCol = (buf.Lines[buf.CursorLine] ?? "").Length;
                }
            }
            else
            {
                buf.SelectionAnchorLine = sL;
                buf.SelectionAnchorCol = 0;
                int targetEndLine = (eC == 0 && eL > sL) ? (eL + 1) : (eL + 1);
                if (targetEndLine < buf.Lines.Count)
                {
                    buf.CursorLine = targetEndLine;
                    buf.CursorCol = 0;
                }
                else
                {
                    buf.CursorLine = buf.Lines.Count - 1;
                    buf.CursorCol = (buf.Lines[buf.CursorLine] ?? "").Length;
                }
            }
            EnsureCursorVisible();
            owner.UpdateCursorStatusLabel();
            this.Invalidate();
        }

        public void AddCursorsToLineEnds()
        {
            EditorTabBuffer buf = owner.CurrentBuffer;
            if (buf == null || buf.Lines.Count == 0) return;
            PushCursorHistorySnapshot(buf);
            int sL, sC, eL, eC;
            buf.GetNormalizedSelection(out sL, out sC, out eL, out eC);
            if (eL > sL && eC == 0) eL--;
            buf.ClearExtraCarets();
            int firstLen = (buf.Lines[sL] ?? "").Length;
            buf.SelectionAnchorLine = sL;
            buf.SelectionAnchorCol = firstLen;
            buf.CursorLine = sL;
            buf.CursorCol = firstLen;
            for (int l = sL + 1; l <= eL && l < buf.Lines.Count; l++)
            {
                int len = (buf.Lines[l] ?? "").Length;
                buf.ExtraCarets.Add(new EditorCaret
                {
                    AnchorLine = l,
                    AnchorCol = len,
                    CursorLine = l,
                    CursorCol = len
                });
            }
            owner.UpdateCursorStatusLabel();
            this.Invalidate();
        }

        public void FormatSelection()
        {
            EditorTabBuffer buf = owner.CurrentBuffer;
            if (buf == null || buf.Lines == null || buf.Lines.Count == 0) return;
            if (!buf.HasSelection())
            {
                FormatDocument();
                return;
            }
            buf.PushUndoSnapshot();
            int sL, sC, eL, eC;
            buf.GetNormalizedSelection(out sL, out sC, out eL, out eC);
            if (eL > sL && eC == 0) eL--;
            int indentDepth = 0;
            for (int pre = 0; pre < sL && pre < buf.Lines.Count; pre++)
            {
                string pl = (buf.Lines[pre] ?? "").Trim();
                foreach (char ch in pl)
                {
                    if (ch == '{') indentDepth++;
                    else if (ch == '}') indentDepth = Math.Max(0, indentDepth - 1);
                }
            }
            for (int l = sL; l <= eL && l < buf.Lines.Count; l++)
            {
                string tr = (buf.Lines[l] ?? "").Replace("\t", "    ").Trim();
                if (tr.Length == 0) { buf.Lines[l] = ""; continue; }
                if (tr.StartsWith("}") || tr.StartsWith("]") || tr.StartsWith(")"))
                    indentDepth = Math.Max(0, indentDepth - 1);
                buf.Lines[l] = new string(' ', indentDepth * 4) + tr;
                foreach (char ch in tr)
                {
                    if (ch == '{') indentDepth++;
                    else if (ch == '}') indentDepth = Math.Max(0, indentDepth - 1);
                }
                if (tr.StartsWith("}")) indentDepth++;
            }
            buf.ClampCursor();
            owner.NotifyBufferContentChanged();
            this.Invalidate();
        }

        public void FoldCurrentRegion()
        {
            EditorTabBuffer buf = owner.CurrentBuffer;
            if (buf == null) return;
            int line = buf.CursorLine;
            for (int l = line; l >= 0; l--)
            {
                int endL = FindFoldEndLine(buf.Lines, l);
                if (endL > l && endL >= line)
                {
                    buf.FoldedLines.Add(l);
                    if (buf.CursorLine > l && buf.CursorLine <= endL)
                    {
                        buf.CursorLine = l;
                        buf.ClearSelection();
                    }
                    this.Invalidate();
                    return;
                }
            }
        }

        public void UnfoldCurrentRegion()
        {
            EditorTabBuffer buf = owner.CurrentBuffer;
            if (buf == null || buf.FoldedLines == null) return;
            int line = buf.CursorLine;
            if (buf.FoldedLines.Remove(line))
            {
                this.Invalidate();
                return;
            }
            foreach (int fh in buf.FoldedLines.OrderByDescending(x => x).ToList())
            {
                int endL = FindFoldEndLine(buf.Lines, fh);
                if (fh <= line && endL >= line)
                {
                    buf.FoldedLines.Remove(fh);
                    this.Invalidate();
                    return;
                }
            }
        }

        public void FoldAllRegions()
        {
            EditorTabBuffer buf = owner.CurrentBuffer;
            if (buf == null) return;
            for (int l = 0; l < buf.Lines.Count; l++)
            {
                if (FindFoldEndLine(buf.Lines, l) > l)
                    buf.FoldedLines.Add(l);
            }
            buf.ClampCursor();
            this.Invalidate();
        }

        public void UnfoldAllRegions()
        {
            EditorTabBuffer buf = owner.CurrentBuffer;
            if (buf == null || buf.FoldedLines == null) return;
            buf.FoldedLines.Clear();
            this.Invalidate();
        }

        public void InsertBlankLine(bool above)
        {
            EditorTabBuffer buf = owner.CurrentBuffer;
            if (buf == null) return;
            buf.PushUndoSnapshot();
            buf.ClearExtraCarets();
            int curLine = Math.Max(0, Math.Min(buf.Lines.Count - 1, buf.CursorLine));
            string ln = buf.Lines[curLine] ?? "";
            string indent = Regex.Match(ln, @"^[ \t]*").Value;
            if (above)
            {
                buf.Lines.Insert(curLine, indent);
                buf.CursorLine = curLine;
                buf.CursorCol = indent.Length;
            }
            else
            {
                if (ln.TrimEnd().EndsWith("{") || ln.TrimEnd().EndsWith(":"))
                    indent += GetIndentUnit();
                buf.Lines.Insert(curLine + 1, indent);
                buf.CursorLine = curLine + 1;
                buf.CursorCol = indent.Length;
            }
            buf.ClearSelection();
            owner.NotifyBufferContentChanged();
            EnsureCursorVisible();
            owner.UpdateCursorStatusLabel();
            this.Invalidate();
        }

        public void IndentSelectionLines(bool indent)
        {
            EditorTabBuffer buf = owner.CurrentBuffer;
            if (buf == null) return;
            buf.PushUndoSnapshot();
            int sLine, sCol, eLine, eCol;
            buf.GetNormalizedSelection(out sLine, out sCol, out eLine, out eCol);
            if (eLine > sLine && eCol == 0) eLine--;
            string indentUnit = GetIndentUnit();
            int maxRemSpaces = owner != null ? Math.Max(1, Math.Min(8, owner.TabSize)) : 4;
            for (int l = sLine; l <= eLine && l < buf.Lines.Count; l++)
            {
                string ln = buf.Lines[l] ?? "";
                if (indent)
                {
                    buf.Lines[l] = indentUnit + ln;
                    if (buf.HasSelection() && l == buf.SelectionAnchorLine) buf.SelectionAnchorCol += indentUnit.Length;
                    if (l == buf.CursorLine) buf.CursorCol += indentUnit.Length;
                }
                else
                {
                    int removeSp = 0;
                    if (ln.StartsWith("\t")) removeSp = 1;
                    else
                    {
                        while (removeSp < maxRemSpaces && removeSp < ln.Length && ln[removeSp] == ' ') removeSp++;
                    }
                    if (removeSp > 0)
                    {
                        buf.Lines[l] = ln.Substring(removeSp);
                        if (buf.HasSelection() && l == buf.SelectionAnchorLine) buf.SelectionAnchorCol = Math.Max(0, buf.SelectionAnchorCol - removeSp);
                        if (l == buf.CursorLine) buf.CursorCol = Math.Max(0, buf.CursorCol - removeSp);
                    }
                }
            }
            owner.NotifyBufferContentChanged();
            owner.UpdateCursorStatusLabel();
            this.Invalidate();
        }

        public void ExplicitLineComment(bool addComment)
        {
            EditorTabBuffer buf = owner.CurrentBuffer;
            if (buf == null) return;
            buf.PushUndoSnapshot();
            int sL, sC, eL, eC;
            buf.GetNormalizedSelection(out sL, out sC, out eL, out eC);
            if (eL > sL && eC == 0) eL--;
            string lang = (buf.Language ?? "").ToLowerInvariant();
            string prefix = (lang == "py" || lang == "sh" || lang == "ps1" || lang == "yaml" || lang == "yml" || lang == "toml") ? "# " : "// ";
            string token = prefix.Trim();
            for (int l = sL; l <= eL && l < buf.Lines.Count; l++)
            {
                string ln = buf.Lines[l] ?? "";
                string tr = ln.TrimStart();
                int ind = ln.Length - tr.Length;
                if (addComment)
                {
                    if (tr.Length > 0 && !tr.StartsWith(token))
                        buf.Lines[l] = ln.Substring(0, ind) + prefix + ln.Substring(ind);
                }
                else
                {
                    if (tr.StartsWith(token))
                    {
                        int idx = ln.IndexOf(token, StringComparison.Ordinal);
                        int rem = ln.Substring(idx).StartsWith(prefix) ? prefix.Length : token.Length;
                        buf.Lines[l] = ln.Remove(idx, rem);
                    }
                }
            }
            buf.ClampCursor();
            owner.NotifyBufferContentChanged();
            this.Invalidate();
        }

        public void ArmCtrlKChord()
        {
            IsCtrlKChordArmed = true;
            if (ctrlKChordTimer != null)
            {
                ctrlKChordTimer.Stop();
                ctrlKChordTimer.Start();
            }
            owner.RaiseChordStatusChanged("(Ctrl+K) was pressed. Waiting for second key of chord...");
        }

        public void DisarmCtrlKChord()
        {
            if (!IsCtrlKChordArmed) return;
            IsCtrlKChordArmed = false;
            if (ctrlKChordTimer != null)
                ctrlKChordTimer.Stop();
            owner.RaiseChordStatusChanged("");
        }

        public bool TryHandleCtrlKSecondKey(KeyEventArgs e)
        {
            if (!IsCtrlKChordArmed) return false;
            if (e.KeyCode == Keys.ControlKey || e.KeyCode == Keys.ShiftKey || e.KeyCode == Keys.Menu)
                return false;

            DisarmCtrlKChord();
            if (e.KeyCode == Keys.D)
            {
                e.SuppressKeyPress = true;
                SkipAndAddNextOccurrenceCursor();
                return true;
            }
            if (e.KeyCode == Keys.F && e.Control)
            {
                e.SuppressKeyPress = true;
                FormatSelection();
                return true;
            }
            if (e.KeyCode == Keys.C)
            {
                e.SuppressKeyPress = true;
                ExplicitLineComment(true);
                return true;
            }
            if (e.KeyCode == Keys.U)
            {
                e.SuppressKeyPress = true;
                ExplicitLineComment(false);
                return true;
            }
            if (e.KeyCode == Keys.D0 || e.KeyCode == Keys.NumPad0)
            {
                e.SuppressKeyPress = true;
                FoldAllRegions();
                return true;
            }
            if (e.KeyCode == Keys.J)
            {
                e.SuppressKeyPress = true;
                UnfoldAllRegions();
                return true;
            }
            if (e.KeyCode == Keys.OemOpenBrackets)
            {
                e.SuppressKeyPress = true;
                FoldCurrentRegion();
                return true;
            }
            if (e.KeyCode == Keys.OemCloseBrackets)
            {
                e.SuppressKeyPress = true;
                UnfoldCurrentRegion();
                return true;
            }
            if (e.KeyCode == Keys.I)
            {
                e.SuppressKeyPress = true;
                owner.ToggleCtrlKBar();
                return true;
            }
            if (owner != null && owner.OnUnhandledCtrlKChord != null && owner.OnUnhandledCtrlKChord(e))
            {
                e.SuppressKeyPress = true;
                return true;
            }
            return true;
        }

        public void ReplaceSelectionWithText(string insertText)
        {
            EditorTabBuffer buf = owner.CurrentBuffer;
            if (buf == null) return;

            if (buf.HasSelection())
            {
                int sLine, sCol, eLine, eCol;
                buf.GetNormalizedSelection(out sLine, out sCol, out eLine, out eCol);
                string prefix = buf.Lines[sLine].Substring(0, Math.Min(buf.Lines[sLine].Length, sCol));
                string suffix = buf.Lines[eLine].Substring(Math.Min(buf.Lines[eLine].Length, eCol));

                if (eLine > sLine)
                    buf.Lines.RemoveRange(sLine + 1, eLine - sLine);

                buf.Lines[sLine] = prefix + suffix;
                buf.CursorLine = sLine;
                buf.CursorCol = prefix.Length;
                buf.ClearSelection();
            }

            if (string.IsNullOrEmpty(insertText)) return;

            string[] parts = insertText.Replace("\r\n", "\n").Replace("\r", "\n").Split('\n');
            string curLine = buf.Lines[buf.CursorLine];
            int c = Math.Min(curLine.Length, buf.CursorCol);
            string before = curLine.Substring(0, c);
            string after = curLine.Substring(c);

            if (parts.Length == 1)
            {
                buf.Lines[buf.CursorLine] = before + parts[0] + after;
                buf.CursorCol = before.Length + parts[0].Length;
            }
            else
            {
                buf.Lines[buf.CursorLine] = before + parts[0];
                for (int i = 1; i < parts.Length; i++)
                {
                    buf.Lines.Insert(buf.CursorLine + i, parts[i] + (i == parts.Length - 1 ? after : ""));
                }
                buf.CursorLine += parts.Length - 1;
                buf.CursorCol = parts[parts.Length - 1].Length;
            }
        }

        private class CaretBinding
        {
            public bool IsPrimary;
            public EditorCaret Extra;
            public int SLine, SCol, ELine, ECol;
        }

        private List<CaretBinding> CollectOrderedCaretsDesc(EditorTabBuffer buf)
        {
            List<CaretBinding> list = new List<CaretBinding>();
            int psL, psC, peL, peC;
            buf.GetNormalizedSelection(out psL, out psC, out peL, out peC);
            list.Add(new CaretBinding { IsPrimary = true, SLine = psL, SCol = psC, ELine = peL, ECol = peC });

            foreach (EditorCaret ec in buf.ExtraCarets)
            {
                int esL, esC, eeL, eeC;
                ec.GetNormalizedRange(out esL, out esC, out eeL, out eeC);
                list.Add(new CaretBinding { IsPrimary = false, Extra = ec, SLine = esL, SCol = esC, ELine = eeL, ECol = eeC });
            }

            list.Sort((a, b) =>
            {
                int cmp = b.ELine.CompareTo(a.ELine);
                return cmp != 0 ? cmp : b.ECol.CompareTo(a.ECol);
            });
            return list;
        }

        public void SendMultiCursorText(string insertText)
        {
            ReplaceAllCaretsText(insertText);
        }

        private static void ShiftProcessedCarets(EditorTabBuffer buf, List<CaretBinding> processed, int afterLine, int lineDelta)
        {
            if (lineDelta == 0 || processed == null) return;
            foreach (CaretBinding prev in processed)
            {
                if (prev.IsPrimary)
                {
                    if (buf.CursorLine > afterLine)
                    {
                        buf.CursorLine = Math.Max(0, buf.CursorLine + lineDelta);
                        buf.SelectionAnchorLine = buf.CursorLine;
                    }
                }
                else if (prev.Extra != null && prev.Extra.CursorLine > afterLine)
                {
                    prev.Extra.CursorLine = Math.Max(0, prev.Extra.CursorLine + lineDelta);
                    prev.Extra.AnchorLine = prev.Extra.CursorLine;
                }
            }
        }

        private static bool CollapseCaretSelectionRange(EditorTabBuffer buf, CaretBinding cb, List<CaretBinding> processed, out int sLine, out int sCol)
        {
            sLine = Math.Max(0, Math.Min(buf.Lines.Count - 1, cb.SLine));
            int eLine = Math.Max(sLine, Math.Min(buf.Lines.Count - 1, cb.ELine));
            string startLn = buf.Lines[sLine] ?? "";
            string endLn = buf.Lines[eLine] ?? "";
            sCol = Math.Max(0, Math.Min(startLn.Length, cb.SCol));
            int eCol = (eLine == sLine)
                ? Math.Max(sCol, Math.Min(startLn.Length, cb.ECol))
                : Math.Max(0, Math.Min(endLn.Length, cb.ECol));

            if (eLine == sLine && eCol == sCol)
                return false;

            buf.Lines[sLine] = startLn.Substring(0, sCol) + endLn.Substring(eCol);
            int removedLines = eLine - sLine;
            if (removedLines > 0)
            {
                buf.Lines.RemoveRange(sLine + 1, removedLines);
                ShiftProcessedCarets(buf, processed, sLine, -removedLines);
            }
            return true;
        }

        private void ReplaceAllCaretsText(string insertText)
        {
            EditorTabBuffer buf = owner.CurrentBuffer;
            if (buf == null) return;
            List<CaretBinding> ordered = CollectOrderedCaretsDesc(buf);
            List<CaretBinding> processed = new List<CaretBinding>();
            string safeText = (insertText ?? "").Replace("\r\n", "\n").Replace("\r", "\n");
            if (safeText.Contains("\n")) safeText = safeText.Split('\n')[0];

            foreach (CaretBinding cb in ordered)
            {
                int lineIdx, sCol;
                CollapseCaretSelectionRange(buf, cb, processed, out lineIdx, out sCol);
                string ln = buf.Lines[lineIdx] ?? "";
                sCol = Math.Max(0, Math.Min(ln.Length, sCol));

                buf.Lines[lineIdx] = ln.Substring(0, sCol) + safeText + ln.Substring(sCol);
                int newCol = sCol + safeText.Length;

                if (cb.IsPrimary)
                {
                    buf.CursorLine = lineIdx;
                    buf.CursorCol = newCol;
                    buf.ClearSelection();
                }
                else
                {
                    cb.Extra.CursorLine = lineIdx;
                    cb.Extra.CursorCol = newCol;
                    cb.Extra.AnchorLine = lineIdx;
                    cb.Extra.AnchorCol = newCol;
                }
                processed.Add(cb);
            }
            DeduplicateExtraCarets(buf);
        }

        private void BackspaceAllCarets()
        {
            EditorTabBuffer buf = owner.CurrentBuffer;
            if (buf == null) return;
            List<CaretBinding> ordered = CollectOrderedCaretsDesc(buf);
            List<CaretBinding> processed = new List<CaretBinding>();

            foreach (CaretBinding cb in ordered)
            {
                int lineIdx, sCol;
                bool hadSelection = CollapseCaretSelectionRange(buf, cb, processed, out lineIdx, out sCol);
                int newLine = lineIdx;
                int newCol = sCol;

                if (!hadSelection)
                {
                    string ln = buf.Lines[lineIdx] ?? "";
                    if (sCol > 0)
                    {
                        buf.Lines[lineIdx] = ln.Remove(sCol - 1, 1);
                        newCol = sCol - 1;
                    }
                    else if (lineIdx > 0)
                    {
                        string prevLn = buf.Lines[lineIdx - 1] ?? "";
                        newCol = prevLn.Length;
                        newLine = lineIdx - 1;
                        buf.Lines[newLine] = prevLn + ln;
                        buf.Lines.RemoveAt(lineIdx);
                        ShiftProcessedCarets(buf, processed, newLine, -1);
                    }
                }

                if (cb.IsPrimary)
                {
                    buf.CursorLine = newLine;
                    buf.CursorCol = newCol;
                    buf.ClearSelection();
                }
                else
                {
                    cb.Extra.CursorLine = newLine;
                    cb.Extra.CursorCol = newCol;
                    cb.Extra.AnchorLine = newLine;
                    cb.Extra.AnchorCol = newCol;
                }
                processed.Add(cb);
            }
            DeduplicateExtraCarets(buf);
        }

        private void DeleteAllCarets()
        {
            EditorTabBuffer buf = owner.CurrentBuffer;
            if (buf == null) return;
            List<CaretBinding> ordered = CollectOrderedCaretsDesc(buf);
            List<CaretBinding> processed = new List<CaretBinding>();

            foreach (CaretBinding cb in ordered)
            {
                int lineIdx, sCol;
                bool hadSelection = CollapseCaretSelectionRange(buf, cb, processed, out lineIdx, out sCol);

                if (!hadSelection)
                {
                    string ln = buf.Lines[lineIdx] ?? "";
                    if (sCol < ln.Length)
                    {
                        buf.Lines[lineIdx] = ln.Remove(sCol, 1);
                    }
                    else if (lineIdx + 1 < buf.Lines.Count)
                    {
                        buf.Lines[lineIdx] = ln + (buf.Lines[lineIdx + 1] ?? "");
                        buf.Lines.RemoveAt(lineIdx + 1);
                        ShiftProcessedCarets(buf, processed, lineIdx, -1);
                    }
                }

                if (cb.IsPrimary)
                {
                    buf.CursorLine = lineIdx;
                    buf.CursorCol = sCol;
                    buf.ClearSelection();
                }
                else
                {
                    cb.Extra.CursorLine = lineIdx;
                    cb.Extra.CursorCol = sCol;
                    cb.Extra.AnchorLine = lineIdx;
                    cb.Extra.AnchorCol = sCol;
                }
                processed.Add(cb);
            }
            DeduplicateExtraCarets(buf);
        }

        private string GetMultiCursorClipboardText(EditorTabBuffer buf)
        {
            List<CaretBinding> ordered = CollectOrderedCaretsDesc(buf);
            ordered.Reverse(); // ascending document order
            List<string> pieces = new List<string>();
            foreach (CaretBinding cb in ordered)
            {
                int sLine = Math.Max(0, Math.Min(buf.Lines.Count - 1, cb.SLine));
                int eLine = Math.Max(sLine, Math.Min(buf.Lines.Count - 1, cb.ELine));
                string startLn = buf.Lines[sLine] ?? "";
                if (sLine == eLine)
                {
                    if (cb.SCol != cb.ECol)
                    {
                        int sC = Math.Max(0, Math.Min(startLn.Length, cb.SCol));
                        int eC = Math.Max(sC, Math.Min(startLn.Length, cb.ECol));
                        pieces.Add(startLn.Substring(sC, eC - sC));
                    }
                    else
                    {
                        pieces.Add(startLn);
                    }
                }
                else
                {
                    int sC = Math.Max(0, Math.Min(startLn.Length, cb.SCol));
                    string endLn = buf.Lines[eLine] ?? "";
                    int eC = Math.Max(0, Math.Min(endLn.Length, cb.ECol));
                    StringBuilder sb = new StringBuilder();
                    sb.Append(startLn.Substring(sC));
                    for (int ml = sLine + 1; ml < eLine; ml++)
                    {
                        sb.Append('\n').Append(buf.Lines[ml] ?? "");
                    }
                    sb.Append('\n').Append(endLn.Substring(0, eC));
                    pieces.Add(sb.ToString());
                }
            }
            return string.Join("\n", pieces);
        }

        private static bool IsIdentifierChar(char c)
        {
            return char.IsLetterOrDigit(c) || c == '_';
        }

        private string GetIndentUnit()
        {
            EditorTabBuffer buf = owner != null ? owner.CurrentBuffer : null;
            int ts = (buf != null && buf.CustomTabSize.HasValue && buf.CustomTabSize.Value > 0)
                ? Math.Max(1, Math.Min(8, buf.CustomTabSize.Value))
                : (owner != null ? Math.Max(1, Math.Min(8, owner.TabSize)) : 4);
            bool spaces = (buf != null && buf.CustomInsertSpaces.HasValue)
                ? buf.CustomInsertSpaces.Value
                : (owner == null || owner.InsertSpaces);
            return spaces ? new string(' ', ts) : "\t";
        }

        private void MoveSinglePosition(EditorTabBuffer buf, Keys key, ref int line, ref int col, bool ctrl = false)
        {
            int wrapCols = buf.WordWrap ? GetWrapCols() : int.MaxValue;
            if (key == Keys.Left)
            {
                if (ctrl)
                {
                    string ln = buf.Lines[line] ?? "";
                    if (col <= 0)
                    {
                        if (line > 0) { line--; col = (buf.Lines[line] ?? "").Length; }
                        return;
                    }
                    int c = Math.Min(col, ln.Length);
                    while (c > 0 && char.IsWhiteSpace(ln[c - 1])) c--;
                    if (c > 0 && IsIdentifierChar(ln[c - 1]))
                    {
                        while (c > 0 && IsIdentifierChar(ln[c - 1])) c--;
                    }
                    else
                    {
                        while (c > 0 && !char.IsWhiteSpace(ln[c - 1]) && !IsIdentifierChar(ln[c - 1])) c--;
                    }
                    col = c;
                }
                else
                {
                    if (col > 0) col--;
                    else if (line > 0) { line--; col = buf.Lines[line].Length; }
                }
            }
            else if (key == Keys.Right)
            {
                if (ctrl)
                {
                    string ln = buf.Lines[line] ?? "";
                    if (col >= ln.Length)
                    {
                        if (line + 1 < buf.Lines.Count) { line++; col = 0; }
                        return;
                    }
                    int c = Math.Max(0, col);
                    while (c < ln.Length && char.IsWhiteSpace(ln[c])) c++;
                    if (c < ln.Length && IsIdentifierChar(ln[c]))
                    {
                        while (c < ln.Length && IsIdentifierChar(ln[c])) c++;
                    }
                    else
                    {
                        while (c < ln.Length && !char.IsWhiteSpace(ln[c]) && !IsIdentifierChar(ln[c])) c++;
                    }
                    col = c;
                }
                else
                {
                    if (col < buf.Lines[line].Length) col++;
                    else if (line + 1 < buf.Lines.Count) { line++; col = 0; }
                }
            }
            else if (key == Keys.Up)
            {
                if (buf.WordWrap && col >= wrapCols)
                {
                    col -= wrapCols;
                }
                else if (line > 0)
                {
                    int segOffset = buf.WordWrap ? (col % wrapCols) : col;
                    line--;
                    if (buf.FoldedLines != null && buf.FoldedLines.Count > 0)
                    {
                        foreach (int fh in buf.FoldedLines.OrderByDescending(x => x))
                        {
                            if (fh < line && FindFoldEndLine(buf.Lines, fh) >= line)
                            {
                                line = fh;
                                break;
                            }
                        }
                    }
                    string prevLn = buf.Lines[line] ?? "";
                    if (buf.WordWrap && prevLn.Length > wrapCols)
                    {
                        int lastSegStart = ((prevLn.Length - 1) / wrapCols) * wrapCols;
                        col = Math.Min(prevLn.Length, lastSegStart + segOffset);
                    }
                    else
                    {
                        col = Math.Min(prevLn.Length, segOffset);
                    }
                }
            }
            else if (key == Keys.Down)
            {
                string curLn = buf.Lines[line] ?? "";
                int maxSegIdx = (buf.WordWrap && curLn.Length > wrapCols) ? ((curLn.Length - 1) / wrapCols) : 0;
                int curSegIdx = buf.WordWrap ? (col / wrapCols) : 0;
                if (buf.WordWrap && curSegIdx < maxSegIdx)
                {
                    col = Math.Min(curLn.Length, col + wrapCols);
                }
                else if (line + 1 < buf.Lines.Count)
                {
                    int segOffset = buf.WordWrap ? (col % wrapCols) : col;
                    if (buf.FoldedLines != null && buf.FoldedLines.Contains(line))
                    {
                        int foldEnd = FindFoldEndLine(buf.Lines, line);
                        line = Math.Min(buf.Lines.Count - 1, foldEnd + 1);
                    }
                    else
                    {
                        line++;
                    }
                    col = Math.Min(buf.Lines[line].Length, segOffset);
                }
            }
            else if (key == Keys.Home)
            {
                if (ctrl)
                {
                    line = 0;
                    col = 0;
                }
                else
                {
                    string ln = buf.Lines[line] ?? "";
                    int firstNonWs = 0;
                    while (firstNonWs < ln.Length && char.IsWhiteSpace(ln[firstNonWs])) firstNonWs++;
                    col = (col == firstNonWs) ? 0 : firstNonWs;
                }
            }
            else if (key == Keys.End)
            {
                if (ctrl)
                {
                    line = Math.Max(0, buf.Lines.Count - 1);
                }
                col = buf.Lines[line].Length;
            }
            else if (key == Keys.PageUp)
            {
                line = Math.Max(0, line - VisibleLinesCount);
                col = Math.Min(buf.Lines[line].Length, col);
            }
            else if (key == Keys.PageDown)
            {
                line = Math.Min(buf.Lines.Count - 1, line + VisibleLinesCount);
                col = Math.Min(buf.Lines[line].Length, col);
            }
        }

        private void DeduplicateExtraCarets(EditorTabBuffer buf)
        {
            if (buf.ExtraCarets == null || buf.ExtraCarets.Count == 0) return;
            for (int i = buf.ExtraCarets.Count - 1; i >= 0; i--)
            {
                EditorCaret ec = buf.ExtraCarets[i];
                bool matchesPrimary = (ec.CursorLine == buf.CursorLine && ec.CursorCol == buf.CursorCol);
                bool matchesEarlier = false;
                for (int j = 0; j < i; j++)
                {
                    if (buf.ExtraCarets[j].CursorLine == ec.CursorLine && buf.ExtraCarets[j].CursorCol == ec.CursorCol)
                    {
                        matchesEarlier = true;
                        break;
                    }
                }
                if (matchesPrimary || matchesEarlier)
                    buf.ExtraCarets.RemoveAt(i);
            }
        }

        private void InsertNewlineAllCarets()
        {
            EditorTabBuffer buf = owner.CurrentBuffer;
            if (buf == null) return;
            List<CaretBinding> ordered = CollectOrderedCaretsDesc(buf);
            List<CaretBinding> processed = new List<CaretBinding>();

            foreach (CaretBinding cb in ordered)
            {
                int lineIdx, sCol;
                CollapseCaretSelectionRange(buf, cb, processed, out lineIdx, out sCol);
                string ln = buf.Lines[lineIdx] ?? "";
                sCol = Math.Max(0, Math.Min(ln.Length, sCol));

                string indent = Regex.Match(ln, @"^[ \t]*").Value;
                string before = ln.Substring(0, sCol);
                string after = ln.Substring(sCol);
                if (before.TrimEnd().EndsWith("{") || before.TrimEnd().EndsWith(":") || before.TrimEnd().EndsWith("("))
                    indent += "    ";

                buf.Lines[lineIdx] = before;
                buf.Lines.Insert(lineIdx + 1, indent + after);

                ShiftProcessedCarets(buf, processed, lineIdx - 1, 1);

                int newLine = lineIdx + 1;
                int newCol = indent.Length;
                if (cb.IsPrimary)
                {
                    buf.CursorLine = newLine;
                    buf.CursorCol = newCol;
                    buf.ClearSelection();
                }
                else
                {
                    cb.Extra.CursorLine = newLine;
                    cb.Extra.CursorCol = newCol;
                    cb.Extra.AnchorLine = newLine;
                    cb.Extra.AnchorCol = newCol;
                }
                processed.Add(cb);
            }
            DeduplicateExtraCarets(buf);
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            if (e.KeyCode == Keys.ControlKey || e.KeyCode == Keys.LControlKey || e.KeyCode == Keys.RControlKey)
                RefreshLinkHoverForModifiers();
            else if (linkHover != null && e.KeyCode != Keys.ShiftKey && e.KeyCode != Keys.Menu)
                ClearLinkHover();
            base.OnKeyDown(e);
            EditorTabBuffer buf = owner.CurrentBuffer;
            if (buf == null) return;
            caretBlinkOn = true;
            // Language-server popups / shortcuts get the first look (only when a server serves this file).
            if (PreviewKeyHandler != null && PreviewKeyHandler(e))
            {
                e.Handled = true;
                e.SuppressKeyPress = true;
                return;
            }

            // 0. Check armed Ctrl+K chord second key
            if (IsCtrlKChordArmed)
            {
                if (TryHandleCtrlKSecondKey(e))
                    return;
            }

            // 1. Autocomplete Navigation
            if (owner.IsAutocompleteVisible && !buf.HasSelection())
            {
                if (e.KeyCode == Keys.Down) { e.SuppressKeyPress = true; owner.MoveAutocompleteSelection(1); return; }
                if (e.KeyCode == Keys.Up) { e.SuppressKeyPress = true; owner.MoveAutocompleteSelection(-1); return; }
                if (!e.Shift && (e.KeyCode == Keys.Enter || e.KeyCode == Keys.Tab)) { e.SuppressKeyPress = true; owner.AcceptSelectedAutocomplete(); return; }
                if (e.KeyCode == Keys.Escape) { e.SuppressKeyPress = true; owner.DismissAutocomplete(); return; }
            }

            // 2. Accept AI Ghost Text with Tab (full) or Ctrl+Right (next word) when no selection is active
            if (e.KeyCode == Keys.Tab && !e.Shift && !buf.HasSelection() && !buf.HasMultiCursors && !string.IsNullOrEmpty(buf.GhostText) && buf.GhostLine == buf.CursorLine)
            {
                e.SuppressKeyPress = true;
                buf.PushUndoSnapshot();
                string ghost = buf.GhostText;
                buf.GhostText = null;
                ReplaceSelectionWithText(ghost);
                owner.NotifyBufferContentChanged();
                EnsureCursorVisible();
                this.Invalidate();
                return;
            }
            if (e.Control && !e.Shift && !e.Alt && e.KeyCode == Keys.Right &&
                !buf.HasSelection() && !buf.HasMultiCursors &&
                !string.IsNullOrEmpty(buf.GhostText) && buf.GhostLine == buf.CursorLine)
            {
                if (AcceptGhostTextNextWord())
                {
                    e.SuppressKeyPress = true;
                    return;
                }
            }

            // 3. Studio & VS Code Muscle-Memory Shortcuts
            if (e.Control && e.KeyCode == Keys.Back && buf.DiffHunks != null && buf.DiffHunks.Any(h => h.IsPending))
            {
                e.SuppressKeyPress = true;
                EditorDiffHunk curHunk = buf.DiffHunks.FirstOrDefault(h => h.IsPending && buf.CursorLine >= h.StartLine && buf.CursorLine <= h.StartLine + Math.Max(1, h.AddedLines.Count));
                if (curHunk != null) owner.RejectSingleHunk(curHunk);
                else owner.RejectAllHunks();
                return;
            }

            // Ctrl+Shift+Alt+Arrow (or ColumnSelectionMode + Shift+Arrow / Shift+Alt+Arrow): Box / Rectangular Column Selection
            if ((e.Control && e.Shift && e.Alt && (e.KeyCode == Keys.Up || e.KeyCode == Keys.Down || e.KeyCode == Keys.Left || e.KeyCode == Keys.Right)) ||
                (ColumnSelectionMode && e.Shift && !e.Control && (e.KeyCode == Keys.Up || e.KeyCode == Keys.Down || e.KeyCode == Keys.Left || e.KeyCode == Keys.Right)))
            {
                e.SuppressKeyPress = true;
                if (!buf.HasMultiCursors && !buf.HasSelection())
                {
                    boxAnchorLine = buf.CursorLine;
                    boxAnchorCol = buf.CursorCol;
                    boxActiveLine = buf.CursorLine;
                    boxActiveCol = buf.CursorCol;
                }
                if (e.KeyCode == Keys.Up) boxActiveLine = Math.Max(0, boxActiveLine - 1);
                else if (e.KeyCode == Keys.Down) boxActiveLine = Math.Min(buf.Lines.Count - 1, boxActiveLine + 1);
                else if (e.KeyCode == Keys.Left) boxActiveCol = Math.Max(0, boxActiveCol - 1);
                else if (e.KeyCode == Keys.Right) boxActiveCol = boxActiveCol + 1;
                ApplyBoxSelection(buf, boxAnchorLine, boxAnchorCol, boxActiveLine, boxActiveCol);
                owner.UpdateCursorStatusLabel();
                this.Invalidate();
                return;
            }

            if (e.Control && e.KeyCode == Keys.Space)
            {
                e.SuppressKeyPress = true;
                owner.UpdateAutocompletePopup(true);
                return;
            }

            if (e.Shift && e.Alt && e.KeyCode == Keys.I) { e.SuppressKeyPress = true; AddCursorsToLineEnds(); return; }
            if (e.Shift && e.Alt && e.KeyCode == Keys.Right) { e.SuppressKeyPress = true; ExpandSelection(); return; }
            if (e.Shift && e.Alt && e.KeyCode == Keys.Left) { e.SuppressKeyPress = true; ShrinkSelection(); return; }
            if (e.Control && e.Alt && e.KeyCode == Keys.Up) { e.SuppressKeyPress = true; PushCursorHistorySnapshot(buf); AddCursorVertical(-1); return; }
            if (e.Control && e.Alt && e.KeyCode == Keys.Down) { e.SuppressKeyPress = true; PushCursorHistorySnapshot(buf); AddCursorVertical(1); return; }
            // AltGr is reported as Ctrl+Alt on Windows. On Polish, German, Czech... layouts AltGr+letter/digit/OEM
            // types a character (ś, ą, @, €, {, }); it must reach WM_CHAR instead of firing Ctrl shortcuts
            // (Save, Select All, Copy, Cut, Undo).
            if (e.Control && e.Alt && IsAltGrCharacterKey(e.KeyCode)) return;
            if (e.Control && e.Shift && e.KeyCode == Keys.L) { e.SuppressKeyPress = true; SelectAllOccurrencesOfWord(); return; }
            if (e.Control && e.KeyCode == Keys.F2) { e.SuppressKeyPress = true; SelectAllOccurrencesOfWord(); return; }
            if (e.Shift && e.Alt && e.KeyCode == Keys.A) { e.SuppressKeyPress = true; ToggleBlockCommentOnSelection(); return; }
            if (e.Control && e.Shift && e.KeyCode == Keys.OemPipe) { e.SuppressKeyPress = true; JumpToMatchingBracket(); return; }
            if (e.Control && e.Shift && e.KeyCode == Keys.OemOpenBrackets) { e.SuppressKeyPress = true; FoldCurrentRegion(); return; }
            if (e.Control && e.Shift && e.KeyCode == Keys.OemCloseBrackets) { e.SuppressKeyPress = true; UnfoldCurrentRegion(); return; }
            if (e.Control && !e.Shift && !e.Alt && e.KeyCode == Keys.OemCloseBrackets) { e.SuppressKeyPress = true; IndentSelectionLines(true); return; }
            if (e.Control && !e.Shift && !e.Alt && e.KeyCode == Keys.OemOpenBrackets) { e.SuppressKeyPress = true; IndentSelectionLines(false); return; }
            if (e.Control && !e.Shift && !e.Alt && (e.KeyCode == Keys.Up || e.KeyCode == Keys.Down))
            {
                e.SuppressKeyPress = true;
                int delta = (e.KeyCode == Keys.Down) ? 1 : -1;
                buf.ScrollTopLine = Math.Max(0, Math.Min(Math.Max(0, buf.Lines.Count - 1), buf.ScrollTopLine + delta));
                this.Invalidate();
                return;
            }
            if (e.Alt && !e.Control && !e.Shift && (e.KeyCode == Keys.PageUp || e.KeyCode == Keys.PageDown))
            {
                e.SuppressKeyPress = true;
                int delta = (e.KeyCode == Keys.PageDown) ? Math.Max(1, VisibleLinesCount) : -Math.Max(1, VisibleLinesCount);
                buf.ScrollTopLine = Math.Max(0, Math.Min(Math.Max(0, buf.Lines.Count - 1), buf.ScrollTopLine + delta));
                this.Invalidate();
                return;
            }
            if (e.Control && e.KeyCode == Keys.S) { e.SuppressKeyPress = true; owner.TriggerSaveShortcut(); return; }
            if (e.KeyCode == Keys.F5) { e.SuppressKeyPress = true; owner.TriggerRunShortcut(); return; }
            if (e.KeyCode == Keys.F3)
            {
                e.SuppressKeyPress = true;
                if (e.Shift) owner.FindPreviousOccurrence();
                else owner.FindNextOccurrence();
                return;
            }
            if (e.KeyCode == Keys.F2 && !e.Control) // F2 Rename Symbol (VS Code)
            {
                e.SuppressKeyPress = true;
                if (!buf.HasSelection()) SelectWordAt(buf.CursorLine, buf.CursorCol);
                owner.TriggerRenameSymbol(buf.GetSelectedText());
                return;
            }
            if (e.Alt && e.KeyCode == Keys.Z) { e.SuppressKeyPress = true; owner.ToggleWordWrap(); return; }
            if (e.KeyCode == Keys.F12)
            {
                e.SuppressKeyPress = true;
                if (!e.Shift && !e.Alt && !buf.HasSelection() && owner.TriggerFollowLinkAt(buf.CursorLine, buf.CursorCol)) return;
                if (!buf.HasSelection()) SelectWordAt(buf.CursorLine, buf.CursorCol);
                string symWord = buf.GetSelectedText();
                if (e.Shift) owner.TriggerGoToReferences(symWord);
                else if (e.Alt) owner.TriggerPeekDefinition(symWord);
                else owner.TriggerGoToDefinition(symWord);
                return;
            }
            if (e.Control && e.Shift && e.KeyCode == Keys.K) // Ctrl+Shift+K Delete Line (VS Code)
            {
                e.SuppressKeyPress = true;
                buf.ClearExtraCarets();
                buf.PushUndoSnapshot();
                if (buf.Lines.Count > 1) buf.Lines.RemoveAt(buf.CursorLine);
                else buf.Lines[0] = "";
                buf.ClampCursor();
                owner.NotifyBufferContentChanged();
                this.Invalidate();
                return;
            }
            if ((e.Control && e.Shift && e.KeyCode == Keys.D) || (e.Shift && e.Alt && (e.KeyCode == Keys.Down || e.KeyCode == Keys.Up))) // Duplicate Line(s) (VS Code)
            {
                e.SuppressKeyPress = true;
                buf.PushUndoSnapshot();
                int sLine, sCol, eLine, eCol;
                buf.GetNormalizedSelection(out sLine, out sCol, out eLine, out eCol);
                int count = eLine - sLine + 1;
                List<string> block = new List<string>();
                for (int i = sLine; i <= eLine && i < buf.Lines.Count; i++)
                    block.Add(buf.Lines[i]);
                buf.Lines.InsertRange(eLine + 1, block);
                if (e.KeyCode != Keys.Up)
                {
                    if (buf.HasSelection())
                        buf.SelectionAnchorLine += count;
                    buf.CursorLine += count;
                }
                owner.NotifyBufferContentChanged();
                EnsureCursorVisible();
                this.Invalidate();
                return;
            }
            if (e.Shift && e.Alt && e.KeyCode == Keys.F) // Shift+Alt+F Format Document (VS Code)
            {
                e.SuppressKeyPress = true;
                FormatDocument();
                return;
            }
            if (e.Control && !e.Shift && !e.Alt && e.KeyCode == Keys.L) // Ctrl+L Expand Line Selection (VS Code)
            {
                e.SuppressKeyPress = true;
                SelectCurrentLine();
                return;
            }
            if (e.Control && !e.Shift && !e.Alt && e.KeyCode == Keys.U) // Ctrl+U Cursor Undo (VS Code)
            {
                e.SuppressKeyPress = true;
                UndoLastCursorOperation();
                return;
            }
            if (e.Control && !e.Shift && !e.Alt && e.KeyCode == Keys.I) // Ctrl+I Inline AI Edit (VS Code)
            {
                e.SuppressKeyPress = true;
                owner.ToggleCtrlKBar();
                return;
            }
            if (e.Control && !e.Shift && !e.Alt && e.KeyCode == Keys.K) // Ctrl+K Chord Prefix (VS Code)
            {
                e.SuppressKeyPress = true;
                ArmCtrlKChord();
                return;
            }
            if (e.Control && e.KeyCode == Keys.F) { e.SuppressKeyPress = true; owner.OpenFindBar(false); return; }
            if (e.Control && e.KeyCode == Keys.H) { e.SuppressKeyPress = true; owner.OpenFindBar(true); return; }
            if (e.Control && e.KeyCode == Keys.Enter)
            {
                e.SuppressKeyPress = true;
                if (!e.Shift && buf.DiffHunks != null && buf.DiffHunks.Any(h => h.IsPending))
                    owner.AcceptAllHunks();
                else
                    InsertBlankLine(e.Shift);
                return;
            }
            if (e.Control && e.KeyCode == Keys.OemPipe) // Ctrl+\ Split Editor
            {
                e.SuppressKeyPress = true;
                owner.PerformSplitToggle();
                return;
            }
            if (e.Control && (e.KeyCode == Keys.OemQuestion || e.KeyCode == Keys.Divide)) // Ctrl+/ Toggle Line Comment
            {
                e.SuppressKeyPress = true;
                ToggleCommentOnSelection();
                return;
            }
            if (e.Control && !e.Shift && e.KeyCode == Keys.D) // Ctrl+D Multi-Cursor Next Occurrence (VS Code)
            {
                e.SuppressKeyPress = true;
                AddNextOccurrenceCursor();
                return;
            }
            if (e.Alt && !e.Shift && (e.KeyCode == Keys.Up || e.KeyCode == Keys.Down)) // Alt+Up/Down Move Line(s)
            {
                e.SuppressKeyPress = true;
                int sLine, sCol, eLine, eCol;
                buf.GetNormalizedSelection(out sLine, out sCol, out eLine, out eCol);
                if (e.KeyCode == Keys.Up && sLine > 0)
                {
                    bool hadSel = buf.HasSelection();
                    buf.PushUndoSnapshot();
                    string above = buf.Lines[sLine - 1];
                    buf.Lines.RemoveAt(sLine - 1);
                    buf.Lines.Insert(eLine, above);
                    if (hadSel) buf.SelectionAnchorLine = Math.Max(0, buf.SelectionAnchorLine - 1);
                    buf.CursorLine = Math.Max(0, buf.CursorLine - 1);
                    owner.NotifyBufferContentChanged();
                    EnsureCursorVisible();
                    this.Invalidate();
                }
                else if (e.KeyCode == Keys.Down && eLine + 1 < buf.Lines.Count)
                {
                    bool hadSel = buf.HasSelection();
                    buf.PushUndoSnapshot();
                    string below = buf.Lines[eLine + 1];
                    buf.Lines.RemoveAt(eLine + 1);
                    buf.Lines.Insert(sLine, below);
                    if (hadSel) buf.SelectionAnchorLine = Math.Min(buf.Lines.Count - 1, buf.SelectionAnchorLine + 1);
                    buf.CursorLine = Math.Min(buf.Lines.Count - 1, buf.CursorLine + 1);
                    owner.NotifyBufferContentChanged();
                    EnsureCursorVisible();
                    this.Invalidate();
                }
                return;
            }
            if (e.Control && e.KeyCode == Keys.A)
            {
                e.SuppressKeyPress = true;
                PerformSelectAll();
                return;
            }
            if (e.Control && e.KeyCode == Keys.C)
            {
                e.SuppressKeyPress = true;
                PerformCopy();
                return;
            }
            if (e.Control && e.KeyCode == Keys.X)
            {
                e.SuppressKeyPress = true;
                PerformCut();
                return;
            }
            if (e.Control && e.KeyCode == Keys.V)
            {
                e.SuppressKeyPress = true;
                PerformPaste();
                return;
            }
            if (e.Control && !e.Shift && e.KeyCode == Keys.Z)
            {
                e.SuppressKeyPress = true;
                buf.ClearExtraCarets();
                buf.ClearSelection();
                if (buf.Undo())
                {
                    owner.NotifyBufferContentChanged();
                    owner.DismissAutocomplete();
                    EnsureCursorVisible();
                    owner.UpdateCursorStatusLabel();
                    this.Invalidate();
                }
                return;
            }
            if (e.Control && (e.KeyCode == Keys.Y || (e.Shift && e.KeyCode == Keys.Z)))
            {
                e.SuppressKeyPress = true;
                buf.ClearExtraCarets();
                buf.ClearSelection();
                if (buf.Redo())
                {
                    owner.NotifyBufferContentChanged();
                    owner.DismissAutocomplete();
                    EnsureCursorVisible();
                    owner.UpdateCursorStatusLabel();
                    this.Invalidate();
                }
                return;
            }

            // 4. Cursor Movement Keys
            if (e.KeyCode == Keys.Left || e.KeyCode == Keys.Right || e.KeyCode == Keys.Up || e.KeyCode == Keys.Down ||
                e.KeyCode == Keys.Home || e.KeyCode == Keys.End ||
                ((e.KeyCode == Keys.PageUp || e.KeyCode == Keys.PageDown) && !e.Control))
            {
                e.SuppressKeyPress = true;
                buf.GhostText = null;
                if (e.Shift)
                {
                    if (!buf.HasSelection())
                    {
                        buf.SelectionAnchorLine = buf.CursorLine;
                        buf.SelectionAnchorCol = buf.CursorCol;
                    }
                }
                else
                {
                    buf.ClearSelection();
                }

                int pLine = buf.CursorLine;
                int pCol = buf.CursorCol;
                MoveSinglePosition(buf, e.KeyCode, ref pLine, ref pCol, e.Control);
                buf.CursorLine = pLine;
                buf.CursorCol = pCol;
                if (!e.Shift)
                {
                    buf.SelectionAnchorLine = pLine;
                    buf.SelectionAnchorCol = pCol;
                }

                if (buf.ExtraCarets != null && buf.ExtraCarets.Count > 0)
                {
                    foreach (EditorCaret ec in buf.ExtraCarets)
                    {
                        int ecLine = ec.CursorLine;
                        int ecCol = ec.CursorCol;
                        MoveSinglePosition(buf, e.KeyCode, ref ecLine, ref ecCol, e.Control);
                        ec.CursorLine = ecLine;
                        ec.CursorCol = ecCol;
                        if (!e.Shift)
                        {
                            ec.AnchorLine = ecLine;
                            ec.AnchorCol = ecCol;
                        }
                    }
                    DeduplicateExtraCarets(buf);
                }

                EnsureCursorVisible();
                owner.UpdateCursorStatusLabel();
                this.Invalidate();
                return;
            }

            // 5. Editing Keys (Enter, Backspace, Delete, Tab, Escape)
            if (e.KeyCode == Keys.Enter && !e.Control)
            {
                e.SuppressKeyPress = true;
                buf.PushUndoSnapshot();
                if (buf.HasMultiCursors)
                {
                    InsertNewlineAllCarets();
                }
                else
                {
                    string curLine = buf.Lines[buf.CursorLine];
                    string indent = Regex.Match(curLine, @"^[ \t]*").Value;
                    string before = curLine.Substring(0, Math.Min(curLine.Length, buf.CursorCol));
                    if (before.TrimEnd().EndsWith("{") || before.TrimEnd().EndsWith(":") || before.TrimEnd().EndsWith("("))
                        indent += GetIndentUnit();
                    ReplaceSelectionWithText("\n" + indent);
                }
                owner.NotifyBufferContentChanged();
                EnsureCursorVisible();
                this.Invalidate();
                return;
            }
            if (e.KeyCode == Keys.Tab && !e.Control)
            {
                e.SuppressKeyPress = true;
                buf.PushUndoSnapshot();
                int sLine, sCol, eLine, eCol;
                buf.GetNormalizedSelection(out sLine, out sCol, out eLine, out eCol);
                string indentUnit = GetIndentUnit();
                int maxRemSpaces = owner != null ? Math.Max(1, Math.Min(8, owner.TabSize)) : 4;

                if (e.Shift)
                {
                    for (int l = sLine; l <= eLine && l < buf.Lines.Count; l++)
                    {
                        string ln = buf.Lines[l] ?? "";
                        int removeSp = 0;
                        if (ln.StartsWith("\t")) removeSp = 1;
                        else
                        {
                            while (removeSp < maxRemSpaces && removeSp < ln.Length && ln[removeSp] == ' ') removeSp++;
                        }
                        if (removeSp > 0)
                        {
                            buf.Lines[l] = ln.Substring(removeSp);
                            if (l == buf.SelectionAnchorLine) buf.SelectionAnchorCol = Math.Max(0, buf.SelectionAnchorCol - removeSp);
                            if (l == buf.CursorLine) buf.CursorCol = Math.Max(0, buf.CursorCol - removeSp);
                        }
                    }
                }
                else if (buf.HasSelection() && (sLine != eLine || (sCol == 0 && eCol >= (buf.Lines[sLine] ?? "").Length && eCol > 0)))
                {
                    for (int l = sLine; l <= eLine && l < buf.Lines.Count; l++)
                    {
                        buf.Lines[l] = indentUnit + (buf.Lines[l] ?? "");
                        if (l == buf.SelectionAnchorLine) buf.SelectionAnchorCol += indentUnit.Length;
                        if (l == buf.CursorLine) buf.CursorCol += indentUnit.Length;
                    }
                }
                else if (buf.HasMultiCursors)
                {
                    ReplaceAllCaretsText(indentUnit);
                }
                else
                {
                    ReplaceSelectionWithText(indentUnit);
                }
                owner.NotifyBufferContentChanged();
                this.Invalidate();
                return;
            }
            if (e.KeyCode == Keys.Back)
            {
                e.SuppressKeyPress = true;
                buf.PushUndoSnapshot();
                if (buf.HasMultiCursors)
                {
                    BackspaceAllCarets();
                }
                else if (buf.HasSelection())
                {
                    ReplaceSelectionWithText("");
                }
                else if (e.Control)
                {
                    int wLine = buf.CursorLine;
                    int wCol = buf.CursorCol;
                    MoveSinglePosition(buf, Keys.Left, ref wLine, ref wCol, true);
                    buf.SelectionAnchorLine = wLine;
                    buf.SelectionAnchorCol = wCol;
                    ReplaceSelectionWithText("");
                }
                else if (buf.CursorCol > 0)
                {
                    string ln = buf.Lines[buf.CursorLine];
                    buf.Lines[buf.CursorLine] = ln.Remove(buf.CursorCol - 1, 1);
                    buf.CursorCol--;
                }
                else if (buf.CursorLine > 0)
                {
                    int prevLen = buf.Lines[buf.CursorLine - 1].Length;
                    buf.Lines[buf.CursorLine - 1] += buf.Lines[buf.CursorLine];
                    buf.Lines.RemoveAt(buf.CursorLine);
                    buf.CursorLine--;
                    buf.CursorCol = prevLen;
                }
                owner.NotifyBufferContentChanged();
                EnsureCursorVisible();
                this.Invalidate();
                return;
            }
            if (e.KeyCode == Keys.Delete)
            {
                e.SuppressKeyPress = true;
                buf.PushUndoSnapshot();
                if (buf.HasMultiCursors)
                {
                    DeleteAllCarets();
                }
                else if (buf.HasSelection())
                {
                    ReplaceSelectionWithText("");
                }
                else if (e.Control)
                {
                    int wLine = buf.CursorLine;
                    int wCol = buf.CursorCol;
                    MoveSinglePosition(buf, Keys.Right, ref wLine, ref wCol, true);
                    buf.SelectionAnchorLine = buf.CursorLine;
                    buf.SelectionAnchorCol = buf.CursorCol;
                    buf.CursorLine = wLine;
                    buf.CursorCol = wCol;
                    ReplaceSelectionWithText("");
                }
                else
                {
                    string ln = buf.Lines[buf.CursorLine];
                    if (buf.CursorCol < ln.Length)
                        buf.Lines[buf.CursorLine] = ln.Remove(buf.CursorCol, 1);
                    else if (buf.CursorLine + 1 < buf.Lines.Count)
                    {
                        buf.Lines[buf.CursorLine] += buf.Lines[buf.CursorLine + 1];
                        buf.Lines.RemoveAt(buf.CursorLine + 1);
                    }
                }
                owner.NotifyBufferContentChanged();
                this.Invalidate();
                return;
            }
            if (e.KeyCode == Keys.Escape)
            {
                buf.GhostText = null;
                if (buf.HasMultiCursors)
                    buf.ClearExtraCarets();
                else
                    buf.ClearSelection();
                owner.DismissAutocomplete();
                owner.UpdateCursorStatusLabel();
                this.Invalidate();
            }
        }

        protected override void OnKeyPress(KeyPressEventArgs e)
        {
            OnKeyPressCore(e);
            if (e.Handled && !char.IsControl(e.KeyChar) && e.KeyChar != '\x7f' && CharTyped != null) CharTyped(e.KeyChar);
        }

        /// <summary>Called before the editor handles a key; return true to swallow it (LSP completion / shortcuts).</summary>
        public Func<KeyEventArgs, bool> PreviewKeyHandler;
        /// <summary>Raised after a typed character was inserted (as-you-type completion, signature help).</summary>
        public event Action<char> CharTyped;
        /// <summary>Raised on mouse moves over the canvas (client coordinates); used for LSP hover.</summary>
        public event Action<Point> MouseMovedOverCanvas;
        /// <summary>Raised when the mouse leaves the canvas or the view scrolls.</summary>
        public event Action MouseLeftCanvas;

        /// <summary>Width of the line-number gutter in pixels.</summary>
        public int GutterPixelWidth { get { return GutterWidth; } }

        /// <summary>Document position (0-based) nearest to a client point.</summary>
        public void HitTestLineCol(Point pt, out int line, out int col)
        {
            ScreenPointToLineCol(pt, out line, out col);
        }

        private void OnKeyPressCore(KeyPressEventArgs e)
        {
            base.OnKeyPress(e);
            if (char.IsControl(e.KeyChar) || e.KeyChar == '\x7f') return;
            EditorTabBuffer buf = owner.CurrentBuffer;
            if (buf == null) return;

            e.Handled = true;

            if (buf.HasMultiCursors)
            {
                buf.PushUndoSnapshot(true);
                ReplaceAllCaretsText(e.KeyChar.ToString());
                owner.NotifyBufferContentChanged();
                EnsureCursorVisible();
                this.Invalidate();
                return;
            }

            string closePair = null;
            if (e.KeyChar == '{') closePair = "}";
            else if (e.KeyChar == '(') closePair = ")";
            else if (e.KeyChar == '[') closePair = "]";
            else if (e.KeyChar == '"') closePair = "\"";
            else if (e.KeyChar == '\'') closePair = "'";
            else if (e.KeyChar == '`') closePair = "`";

            // Skip-over closing delimiter if next character is already that closing character
            if (!buf.HasSelection() && (e.KeyChar == ')' || e.KeyChar == '}' || e.KeyChar == ']' || e.KeyChar == '"' || e.KeyChar == '\'' || e.KeyChar == '`'))
            {
                string curLn = buf.Lines[buf.CursorLine] ?? "";
                if (buf.CursorCol < curLn.Length && curLn[buf.CursorCol] == e.KeyChar)
                {
                    buf.CursorCol++;
                    buf.SelectionAnchorCol = buf.CursorCol;
                    owner.UpdateCursorStatusLabel();
                    EnsureCursorVisible();
                    this.Invalidate();
                    return;
                }
            }

            buf.PushUndoSnapshot(closePair == null);

            if (closePair != null && buf.HasSelection())
            {
                string selected = buf.GetSelectedText();
                if (!selected.Contains("\n"))
                {
                    int sLine, sCol, eLine, eCol;
                    buf.GetNormalizedSelection(out sLine, out sCol, out eLine, out eCol);
                    ReplaceSelectionWithText(e.KeyChar + selected + closePair);
                    buf.SelectionAnchorLine = sLine;
                    buf.SelectionAnchorCol = sCol + 1;
                    buf.CursorLine = sLine;
                    buf.CursorCol = sCol + 1 + selected.Length;
                }
                else
                {
                    ReplaceSelectionWithText(e.KeyChar.ToString());
                }
            }
            else if (closePair != null && !buf.HasSelection())
            {
                ReplaceSelectionWithText(e.KeyChar + closePair);
                buf.CursorCol--;
                buf.SelectionAnchorCol = buf.CursorCol;
            }
            else
            {
                ReplaceSelectionWithText(e.KeyChar.ToString());
            }

            owner.NotifyBufferContentChanged();
            EnsureCursorVisible();
            this.Invalidate();
        }

        private void PerformSelectAll()
        {
            EditorTabBuffer buf = owner.CurrentBuffer;
            if (buf == null) return;
            buf.ClearExtraCarets();
            buf.SelectionAnchorLine = 0;
            buf.SelectionAnchorCol = 0;
            buf.CursorLine = buf.Lines.Count - 1;
            buf.CursorCol = buf.Lines[buf.CursorLine].Length;
            owner.UpdateCursorStatusLabel();
            this.Invalidate();
        }

        [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
        private static extern bool OpenClipboard(IntPtr hWndNewOwner);
        [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
        private static extern bool EmptyClipboard();
        [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr SetClipboardData(uint uFormat, IntPtr hMem);
        [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
        private static extern bool CloseClipboard();
        private const uint CF_UNICODETEXT = 13;
        private static string fallbackClipboardText = "";

        public static void SafeSetClipboardText(string text)
        {
            if (string.IsNullOrEmpty(text)) return;
            fallbackClipboardText = text;
            // Clipboard.SetDataObject owns OLE ownership + retry; the previous raw OpenClipboard(NULL) path could
            // EmptyClipboard() and then fail SetClipboardData (NULL owner, non-movable memory), leaving it empty.
            try
            {
                Clipboard.SetDataObject(text, true, 10, 50);
                return;
            }
            catch { }
            try { Clipboard.SetText(text, TextDataFormat.UnicodeText); } catch { }
        }

        private static string SafeGetClipboardText()
        {
            try
            {
                if (Clipboard.ContainsText())
                {
                    string t = Clipboard.GetText();
                    if (t != null) fallbackClipboardText = t;
                    return t ?? "";
                }
                // Clipboard holds an image / files: never paste stale text copied earlier.
                return "";
            }
            catch
            {
                // Clipboard temporarily locked by another process: fall back to our own last copy.
                return fallbackClipboardText ?? "";
            }
        }

        private void PerformCopy()
        {
            EditorTabBuffer buf = owner.CurrentBuffer;
            if (buf == null) return;
            string toCopy = buf.HasMultiCursors
                ? GetMultiCursorClipboardText(buf)
                : (buf.HasSelection() ? buf.GetSelectedText() : (buf.Lines[buf.CursorLine] + "\n"));
            if (!string.IsNullOrEmpty(toCopy)) SafeSetClipboardText(toCopy);
        }

        private void PerformCut()
        {
            EditorTabBuffer buf = owner.CurrentBuffer;
            if (buf == null) return;
            buf.PushUndoSnapshot();
            if (buf.HasMultiCursors)
            {
                string cutText = GetMultiCursorClipboardText(buf);
                if (!string.IsNullOrEmpty(cutText)) SafeSetClipboardText(cutText);
                ReplaceAllCaretsText("");
            }
            else if (buf.HasSelection())
            {
                SafeSetClipboardText(buf.GetSelectedText());
                ReplaceSelectionWithText("");
            }
            else
            {
                SafeSetClipboardText(buf.Lines[buf.CursorLine] + "\n");
                if (buf.Lines.Count > 1) buf.Lines.RemoveAt(buf.CursorLine);
                else buf.Lines[0] = "";
                buf.ClampCursor();
            }
            owner.NotifyBufferContentChanged();
            this.Invalidate();
        }

        private void PerformPaste()
        {
            EditorTabBuffer buf = owner.CurrentBuffer;
            string clipText = SafeGetClipboardText();
            if (buf == null || string.IsNullOrEmpty(clipText)) return;
            buf.PushUndoSnapshot();
            if (buf.HasMultiCursors) ReplaceAllCaretsText(clipText);
            else ReplaceSelectionWithText(clipText);
            owner.NotifyBufferContentChanged();
            EnsureCursorVisible();
            this.Invalidate();
        }

        public void FormatDocument()
        {
            EditorTabBuffer buf = owner.CurrentBuffer;
            if (buf == null || buf.Lines == null || buf.Lines.Count == 0) return;
            buf.PushUndoSnapshot();

            string lang = (buf.Language ?? "").ToLowerInvariant();
            bool braceLang = (lang == "cs" || lang == "js" || lang == "ts" || lang == "rs" || lang == "json" || lang == "css" || lang == "c" || lang == "cpp" || lang == "java");

            int indentDepth = 0;
            bool inBlockComment = false;
            for (int i = 0; i < buf.Lines.Count; i++)
            {
                string raw = (buf.Lines[i] ?? "").Replace("\t", "    ").TrimEnd();
                if (braceLang)
                {
                    string tr = raw.TrimStart();
                    if (tr.Length == 0)
                    {
                        buf.Lines[i] = "";
                        continue;
                    }
                    if (!inBlockComment && (tr.StartsWith("}") || tr.StartsWith("]") || tr.StartsWith(")")))
                        indentDepth = Math.Max(0, indentDepth - 1);

                    buf.Lines[i] = new string(' ', indentDepth * 4) + tr;

                    int opens = 0, closes = 0;
                    bool inStr = false;
                    char strCh = '\0';
                    for (int c = 0; c < tr.Length; c++)
                    {
                        if (inBlockComment)
                        {
                            if (c + 1 < tr.Length && tr[c] == '*' && tr[c + 1] == '/')
                            {
                                inBlockComment = false;
                                c++;
                            }
                            continue;
                        }
                        if (!inStr && c + 1 < tr.Length && tr[c] == '/' && tr[c + 1] == '*')
                        {
                            inBlockComment = true;
                            c++;
                            continue;
                        }
                        if (c + 1 < tr.Length && tr[c] == '/' && tr[c + 1] == '/' && !inStr) break;
                        if ((tr[c] == '"' || tr[c] == '\'') && (c == 0 || tr[c - 1] != '\\'))
                        {
                            if (!inStr) { inStr = true; strCh = tr[c]; }
                            else if (tr[c] == strCh) inStr = false;
                        }
                        if (!inStr)
                        {
                            if (tr[c] == '{') opens++;
                            else if (tr[c] == '}') closes++;
                        }
                    }
                    if (!inBlockComment && tr.StartsWith("}") && closes > 0) closes--;
                    indentDepth = Math.Max(0, indentDepth + opens - closes);
                }
                else
                {
                    buf.Lines[i] = raw;
                }
            }
            buf.ClampCursor();
            owner.NotifyBufferContentChanged();
            this.Invalidate();
        }

        public void ToggleCommentOnSelection()
        {
            EditorTabBuffer buf = owner.CurrentBuffer;
            if (buf == null) return;
            buf.PushUndoSnapshot();
            string lang = (buf.Language ?? "").ToLowerInvariant();

            int sLine, sCol, eLine, eCol;
            buf.GetNormalizedSelection(out sLine, out sCol, out eLine, out eCol);

            if (lang == "html" || lang == "htm" || lang == "xml" || lang == "css")
            {
                string openTag = (lang == "css") ? "/* " : "<!-- ";
                string closeTag = (lang == "css") ? " */" : " -->";
                string openToken = openTag.Trim();
                string closeToken = closeTag.Trim();

                bool allWrapped = true;
                for (int i = sLine; i <= eLine && i < buf.Lines.Count; i++)
                {
                    string tr = (buf.Lines[i] ?? "").Trim();
                    if (tr.Length == 0 && sLine != eLine) continue;
                    if (!tr.StartsWith(openToken, StringComparison.Ordinal) || !tr.EndsWith(closeToken, StringComparison.Ordinal))
                        allWrapped = false;
                }

                for (int i = sLine; i <= eLine && i < buf.Lines.Count; i++)
                {
                    string ln = buf.Lines[i] ?? "";
                    string tr = ln.TrimStart();
                    if (tr.Length == 0 && sLine != eLine) continue;
                    int ind = ln.Length - tr.Length;
                    string indentStr = ln.Substring(0, ind);
                    string body = tr.TrimEnd();
                    if (allWrapped && body.StartsWith(openToken, StringComparison.Ordinal) && body.EndsWith(closeToken, StringComparison.Ordinal))
                    {
                        int removeOpen = body.StartsWith(openTag, StringComparison.Ordinal) ? openTag.Length : openToken.Length;
                        int removeClose = body.EndsWith(closeTag, StringComparison.Ordinal) ? closeTag.Length : closeToken.Length;
                        if (body.Length >= removeOpen + removeClose)
                            buf.Lines[i] = indentStr + body.Substring(removeOpen, body.Length - removeOpen - removeClose);
                    }
                    else if (!allWrapped)
                    {
                        buf.Lines[i] = indentStr + openTag + body + closeTag;
                    }
                }
                buf.ClampCursor();
                owner.NotifyBufferContentChanged();
                this.Invalidate();
                return;
            }

            string prefix = "// ";
            if (lang == "py" || lang == "sh" || lang == "ps1" || lang == "yaml" || lang == "yml" || lang == "toml")
                prefix = "# ";
            string token = prefix.Trim();

            bool allCommented = true;
            int minIndent = int.MaxValue;
            for (int i = sLine; i <= eLine && i < buf.Lines.Count; i++)
            {
                string ln = buf.Lines[i] ?? "";
                string tr = ln.TrimStart();
                if (tr.Length == 0) continue;
                if (!tr.StartsWith(token)) allCommented = false;
                int ind = ln.Length - tr.Length;
                if (ind < minIndent) minIndent = ind;
            }
            if (minIndent == int.MaxValue) minIndent = 0;

            for (int i = sLine; i <= eLine && i < buf.Lines.Count; i++)
            {
                string ln = buf.Lines[i] ?? "";
                string tr = ln.TrimStart();
                if (tr.Length == 0 && sLine != eLine) continue;
                if (allCommented && tr.StartsWith(token))
                {
                    int idx = ln.IndexOf(token, StringComparison.Ordinal);
                    int rem = ln.Substring(idx).StartsWith(prefix) ? prefix.Length : token.Length;
                    buf.Lines[i] = ln.Remove(idx, rem);
                }
                else if (!allCommented)
                {
                    int insAt = Math.Min(minIndent, ln.Length);
                    buf.Lines[i] = ln.Substring(0, insAt) + prefix + ln.Substring(insAt);
                }
            }
            buf.ClampCursor();
            owner.NotifyBufferContentChanged();
            this.Invalidate();
        }

        public void TriggerCut() { PerformCut(); }
        public void TriggerCopy() { PerformCopy(); }
        public void TriggerPaste() { PerformPaste(); }
        public void TriggerSelectAll() { PerformSelectAll(); }

        public bool AcceptGhostTextNextWord()
        {
            EditorTabBuffer buf = owner.CurrentBuffer;
            if (buf == null || string.IsNullOrEmpty(buf.GhostText) || buf.GhostLine != buf.CursorLine)
                return false;

            string ghost = buf.GhostText;
            int takeLen = 0;
            // Include leading whitespace
            while (takeLen < ghost.Length && (ghost[takeLen] == ' ' || ghost[takeLen] == '\t'))
                takeLen++;
            if (takeLen < ghost.Length && ghost[takeLen] == '\n')
            {
                takeLen++;
            }
            else if (takeLen < ghost.Length && IsIdentifierChar(ghost[takeLen]))
            {
                while (takeLen < ghost.Length && IsIdentifierChar(ghost[takeLen]))
                    takeLen++;
            }
            else if (takeLen < ghost.Length)
            {
                while (takeLen < ghost.Length && !char.IsWhiteSpace(ghost[takeLen]) && !IsIdentifierChar(ghost[takeLen]))
                    takeLen++;
            }
            if (takeLen <= 0) takeLen = Math.Min(1, ghost.Length);

            string wordChunk = ghost.Substring(0, takeLen);
            string remaining = ghost.Substring(takeLen);
            buf.PushUndoSnapshot();
            buf.GhostText = null;
            ReplaceSelectionWithText(wordChunk);
            buf.SyncContentFromLines();
            if (!string.IsNullOrEmpty(remaining))
            {
                buf.GhostText = remaining;
                buf.GhostLine = buf.CursorLine;
                buf.GhostCol = buf.CursorCol;
            }
            owner.UpdateBreadcrumbTitle();
            owner.UpdateCursorStatusLabel();
            EnsureCursorVisible();
            this.Invalidate();
            return true;
        }

        public void ExpandSelection()
        {
            EditorTabBuffer buf = owner.CurrentBuffer;
            if (buf == null || buf.Lines.Count == 0) return;
            if (!buf.HasSelection())
            {
                SelectWordAt(buf.CursorLine, buf.CursorCol);
                owner.UpdateCursorStatusLabel();
                this.Invalidate();
                return;
            }
            int sLine, sCol, eLine, eCol;
            buf.GetNormalizedSelection(out sLine, out sCol, out eLine, out eCol);
            string ln = buf.Lines[sLine] ?? "";
            if (sLine == eLine && (sCol > 0 || eCol < ln.Length))
            {
                buf.SelectionAnchorLine = sLine;
                buf.SelectionAnchorCol = 0;
                buf.CursorLine = sLine;
                buf.CursorCol = ln.Length;
            }
            else
            {
                PerformSelectAll();
            }
            owner.UpdateCursorStatusLabel();
            this.Invalidate();
        }

        public void ShrinkSelection()
        {
            EditorTabBuffer buf = owner.CurrentBuffer;
            if (buf == null || !buf.HasSelection()) return;
            int sLine, sCol, eLine, eCol;
            buf.GetNormalizedSelection(out sLine, out sCol, out eLine, out eCol);
            if (sLine != eLine)
            {
                buf.SelectionAnchorLine = buf.CursorLine;
                buf.SelectionAnchorCol = 0;
                buf.CursorCol = (buf.Lines[buf.CursorLine] ?? "").Length;
            }
            else
            {
                buf.ClearSelection();
            }
            owner.UpdateCursorStatusLabel();
            this.Invalidate();
        }

        public void CopyLines(bool up)
        {
            EditorTabBuffer buf = owner.CurrentBuffer;
            if (buf == null) return;
            buf.PushUndoSnapshot();
            int sLine, sCol, eLine, eCol;
            buf.GetNormalizedSelection(out sLine, out sCol, out eLine, out eCol);
            int count = eLine - sLine + 1;
            List<string> block = new List<string>();
            for (int i = sLine; i <= eLine && i < buf.Lines.Count; i++)
                block.Add(buf.Lines[i]);
            buf.Lines.InsertRange(eLine + 1, block);
            if (!up)
            {
                if (buf.HasSelection()) buf.SelectionAnchorLine += count;
                buf.CursorLine += count;
            }
            owner.NotifyBufferContentChanged();
            EnsureCursorVisible();
            this.Invalidate();
        }

        public void MoveLines(bool up)
        {
            EditorTabBuffer buf = owner.CurrentBuffer;
            if (buf == null) return;
            int sLine, sCol, eLine, eCol;
            buf.GetNormalizedSelection(out sLine, out sCol, out eLine, out eCol);
            if (up && sLine > 0)
            {
                buf.PushUndoSnapshot();
                string above = buf.Lines[sLine - 1];
                buf.Lines.RemoveAt(sLine - 1);
                buf.Lines.Insert(eLine, above);
                buf.SelectionAnchorLine = Math.Max(0, buf.SelectionAnchorLine - 1);
                buf.CursorLine = Math.Max(0, buf.CursorLine - 1);
                owner.NotifyBufferContentChanged();
                EnsureCursorVisible();
                this.Invalidate();
            }
            else if (!up && eLine + 1 < buf.Lines.Count)
            {
                buf.PushUndoSnapshot();
                string below = buf.Lines[eLine + 1];
                buf.Lines.RemoveAt(eLine + 1);
                buf.Lines.Insert(sLine, below);
                buf.SelectionAnchorLine = Math.Min(buf.Lines.Count - 1, buf.SelectionAnchorLine + 1);
                buf.CursorLine = Math.Min(buf.Lines.Count - 1, buf.CursorLine + 1);
                owner.NotifyBufferContentChanged();
                EnsureCursorVisible();
                this.Invalidate();
            }
        }

        public void DuplicateSelectionOrLine()
        {
            EditorTabBuffer buf = owner.CurrentBuffer;
            if (buf == null) return;
            if (buf.HasSelection())
            {
                buf.PushUndoSnapshot();
                string sel = buf.GetSelectedText();
                int sLine, sCol, eLine, eCol;
                buf.GetNormalizedSelection(out sLine, out sCol, out eLine, out eCol);
                buf.ClearSelection();
                buf.CursorLine = eLine;
                buf.CursorCol = eCol;
                ReplaceSelectionWithText(sel);
                owner.NotifyBufferContentChanged();
                EnsureCursorVisible();
                this.Invalidate();
            }
            else
            {
                CopyLines(false);
            }
        }

        private static bool IsAltGrCharacterKey(Keys k)
        {
            return (k >= Keys.A && k <= Keys.Z) || (k >= Keys.D0 && k <= Keys.D9) ||
                   (k >= Keys.Oem1 && k <= Keys.Oem102) || k == Keys.OemSemicolon || k == Keys.Oemplus ||
                   k == Keys.Oemcomma || k == Keys.OemMinus || k == Keys.OemPeriod || k == Keys.OemQuestion ||
                   k == Keys.Oemtilde || k == Keys.OemOpenBrackets || k == Keys.OemPipe || k == Keys.OemCloseBrackets ||
                   k == Keys.OemQuotes || k == Keys.OemBackslash;
        }

        public void AddCursorVertical(int deltaLine)
        {
            EditorTabBuffer buf = owner.CurrentBuffer;
            if (buf == null) return;
            int refLine = buf.CursorLine;
            int refCol = buf.CursorCol;
            if (buf.ExtraCarets != null && buf.ExtraCarets.Count > 0)
            {
                EditorCaret last = buf.ExtraCarets[buf.ExtraCarets.Count - 1];
                refLine = last.CursorLine;
                refCol = last.CursorCol;
            }
            int nextLine = refLine + deltaLine;
            if (nextLine < 0 || nextLine >= buf.Lines.Count) return;
            int nextCol = Math.Min((buf.Lines[nextLine] ?? "").Length, refCol);
            buf.ExtraCarets.Add(new EditorCaret
            {
                AnchorLine = nextLine,
                AnchorCol = nextCol,
                CursorLine = nextLine,
                CursorCol = nextCol
            });
            DeduplicateExtraCarets(buf);
            owner.UpdateCursorStatusLabel();
            this.Invalidate();
        }

        public void SelectAllOccurrencesOfWord()
        {
            EditorTabBuffer buf = owner.CurrentBuffer;
            if (buf == null) return;
            if (!buf.HasSelection())
                SelectWordAt(buf.CursorLine, buf.CursorCol);
            string word = buf.GetSelectedText();
            if (string.IsNullOrEmpty(word) || word.Contains("\n")) return;
            buf.ClearExtraCarets();
            int psLine, psCol, peLine, peCol;
            buf.GetNormalizedSelection(out psLine, out psCol, out peLine, out peCol);
            for (int l = 0; l < buf.Lines.Count; l++)
            {
                string ln = buf.Lines[l] ?? "";
                int idx = ln.IndexOf(word, 0, StringComparison.Ordinal);
                while (idx >= 0)
                {
                    if (!(l == psLine && idx == psCol))
                    {
                        buf.ExtraCarets.Add(new EditorCaret
                        {
                            AnchorLine = l,
                            AnchorCol = idx,
                            CursorLine = l,
                            CursorCol = idx + word.Length
                        });
                    }
                    idx = ln.IndexOf(word, idx + word.Length, StringComparison.Ordinal);
                }
            }
            owner.UpdateCursorStatusLabel();
            this.Invalidate();
        }

        public void ToggleBlockCommentOnSelection()
        {
            EditorTabBuffer buf = owner.CurrentBuffer;
            if (buf == null) return;
            if (!buf.HasSelection())
                SelectWordAt(buf.CursorLine, buf.CursorCol);
            string sel = buf.GetSelectedText();
            string lang = (buf.Language ?? "").ToLowerInvariant();
            string openTok = (lang == "html" || lang == "xml") ? "<!-- " : "/* ";
            string closeTok = (lang == "html" || lang == "xml") ? " -->" : " */";
            buf.PushUndoSnapshot();
            string trimmed = (sel ?? "").Trim();
            if (trimmed.StartsWith(openTok.Trim()) && trimmed.EndsWith(closeTok.Trim()))
            {
                string inner = trimmed.Substring(openTok.Trim().Length);
                inner = inner.Substring(0, Math.Max(0, inner.Length - closeTok.Trim().Length)).Trim();
                ReplaceSelectionWithText(inner);
            }
            else
            {
                ReplaceSelectionWithText(openTok + sel + closeTok);
            }
            owner.NotifyBufferContentChanged();
            this.Invalidate();
        }

        public void JumpToMatchingBracket()
        {
            EditorTabBuffer buf = owner.CurrentBuffer;
            if (buf == null) return;
            int la, ca, lb, cb;
            if (FindMatchingBracket(buf.Lines, buf.CursorLine, buf.CursorCol, out la, out ca, out lb, out cb) && lb >= 0)
            {
                buf.CursorLine = lb;
                buf.CursorCol = cb;
                buf.ClearSelection();
                EnsureCursorVisible();
                owner.UpdateCursorStatusLabel();
                this.Invalidate();
            }
        }
    }
}
