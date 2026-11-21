using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace codeLess.Studio
{
    /// <summary>
    /// WinForms glue for large-file mode: creates viewer tabs, hosts one WinFormsLargeFileViewer inside each
    /// CodeLessEditorControl (docked over the text canvas) and disposes documents of closed tabs. Strict C# 5.
    /// </summary>
    public static class WinFormsLargeFileHost
    {
        private static readonly Dictionary<CodeLessEditorControl, WinFormsLargeFileViewer> viewers = new Dictionary<CodeLessEditorControl, WinFormsLargeFileViewer>();
        private static readonly List<EditorTabBuffer> createdTabs = new List<EditorTabBuffer>();
        private static string editBypassPath;

        /// <summary>
        /// Returns a read-only viewer tab when the file crosses the large-file thresholds, or null to use the normal editor.
        /// Throws if the file cannot be opened (the caller's error handling reports it).
        /// </summary>
        public static EditorTabBuffer TryCreateTab(string filePath, string workspaceRoot, IdePersistentSettings settings)
        {
            if (string.IsNullOrEmpty(filePath) || !File.Exists(filePath)) return null;
            if (editBypassPath != null && string.Equals(editBypassPath, filePath, StringComparison.OrdinalIgnoreCase))
            {
                editBypassPath = null;
                return null;
            }
            if (!LargeFilePolicy.ShouldUseViewer(filePath, settings)) return null;
            EditLimitBytes = LargeFilePolicy.EditLimitBytes(settings);
            LargeFileDocument doc = LargeFileDocument.Open(filePath);
            EditorTabBuffer tab = new EditorTabBuffer();
            tab.FilePath = filePath;
            tab.RelativePath = !string.IsNullOrEmpty(workspaceRoot) ? LlmHarnessAndAgentEngine.GetRelativePath(workspaceRoot, filePath) : Path.GetFileName(filePath);
            tab.FileName = Path.GetFileName(filePath);
            tab.Language = "plaintext";
            tab.Content = "";
            tab.SavedContent = "";
            tab.FileEncoding = doc.TextEncoding;
            tab.HasBom = doc.BomLength > 0;
            tab.WordWrap = false;
            tab.IsDirty = false;
            tab.LargeDocument = doc;
            tab.SyncLinesFromContent();
            lock (createdTabs) { createdTabs.Add(tab); }
            return tab;
        }

        public static long EditLimitBytes = (long)LargeFilePolicy.DefaultEditLimitMB * 1024 * 1024;

        /// <summary>Disposes documents of viewer tabs that are no longer open (called whenever the tab strip refreshes).</summary>
        public static void SweepClosed(IList<EditorTabBuffer> openTabs)
        {
            lock (createdTabs)
            {
                for (int i = createdTabs.Count - 1; i >= 0; i--)
                {
                    EditorTabBuffer t = createdTabs[i];
                    if (openTabs != null && openTabs.Contains(t)) continue;
                    createdTabs.RemoveAt(i);
                    try { if (t.LargeDocument != null) t.LargeDocument.Dispose(); } catch { }
                }
            }
        }

        /// <summary>Shows the viewer for large-file buffers (returns true) or hides it (returns false).</summary>
        public static bool Sync(CodeLessEditorControl editor, EditorTabBuffer buffer)
        {
            WinFormsLargeFileViewer viewer;
            viewers.TryGetValue(editor, out viewer);
            bool large = buffer != null && buffer.LargeDocument != null && !buffer.LargeDocument.IsDisposed;
            if (!large)
            {
                if (viewer != null && viewer.Visible)
                {
                    viewer.UnloadDocument();
                    viewer.Visible = false;
                }
                return false;
            }
            if (viewer == null)
            {
                viewer = new WinFormsLargeFileViewer(editor.Theme);
                viewer.Dock = DockStyle.Fill;
                viewer.OpenInEditorRequested += delegate (string p) { OpenInNormalEditor(editor, p); };
                editor.Controls.Add(viewer);
                viewers[editor] = viewer;
            }
            viewer.Visible = true;
            viewer.BringToFront();
            viewer.LoadDocument(buffer.LargeDocument, EditLimitBytes);
            viewer.FocusViewport();
            return true;
        }

        public static WinFormsLargeFileViewer GetViewer(CodeLessEditorControl editor)
        {
            WinFormsLargeFileViewer v;
            return viewers.TryGetValue(editor, out v) ? v : null;
        }

        private static bool IsShowing(CodeLessEditorControl editor, out WinFormsLargeFileViewer viewer)
        {
            viewer = GetViewer(editor);
            return viewer != null && viewer.Visible && viewer.Document != null;
        }

        public static bool TryGoToLine(CodeLessEditorControl editor, int line1Based, int col1Based)
        {
            WinFormsLargeFileViewer v;
            if (!IsShowing(editor, out v)) return false;
            v.GoToLine(line1Based, col1Based);
            return true;
        }

        public static bool TryShowFind(CodeLessEditorControl editor)
        {
            WinFormsLargeFileViewer v;
            if (!IsShowing(editor, out v)) return false;
            v.ShowFind();
            return true;
        }

        public static void ApplyTheme(CodeLessEditorControl editor, StudioTheme theme)
        {
            WinFormsLargeFileViewer v = GetViewer(editor);
            if (v != null) v.ApplyTheme(theme);
        }

        private static void OpenInNormalEditor(CodeLessEditorControl editor, string path)
        {
            CodeLessMainForm form = editor.FindForm() as CodeLessMainForm;
            if (form == null || string.IsNullOrEmpty(path) || !File.Exists(path)) return;
            long len = new FileInfo(path).Length;
            if (len > EditLimitBytes) return;
            EditorTabBuffer viewerTab = null;
            lock (createdTabs)
            {
                foreach (EditorTabBuffer t in createdTabs)
                {
                    if (string.Equals(t.FilePath, path, StringComparison.OrdinalIgnoreCase)) { viewerTab = t; break; }
                }
            }
            if (viewerTab != null) form.CloseTabBuffer(viewerTab);
            editBypassPath = path;
            form.OpenFileInTab(path);
            editBypassPath = null;
        }
    }

    /// <summary>Read-only paged viewer for huge files (WinForms): banner, streaming find, go to line, follow, copy.</summary>
    public class WinFormsLargeFileViewer : Panel
    {
        private readonly Panel banner;
        private readonly Label bannerTitle;
        private readonly Label bannerStatus;
        private readonly CheckBox followBox;
        private readonly Button findBtn;
        private readonly Button gotoBtn;
        private readonly Button editBtn;
        private readonly Panel findBar;
        private readonly TextBox findBox;
        private readonly CheckBox matchCaseBox;
        private readonly CheckBox regexBox;
        private readonly Label findCount;
        private readonly Panel gotoBar;
        private readonly TextBox gotoBox;
        private readonly VScrollBar vScroll;
        private readonly HScrollBar hScroll;
        private readonly WinFormsLargeFileViewport viewport;
        private readonly System.Windows.Forms.Timer followTimer;
        private readonly System.Windows.Forms.Timer searchDebounce;
        private StudioTheme theme;
        private bool suppressScroll;
        private int pollInFlight;
        private int uiPending;
        private int activeMatch = -1;
        private bool userNavigated;
        private string statusNote = "";
        private LargeFileSearch activeSearch;

        public event Action<string> OpenInEditorRequested;

        public LargeFileDocument Document { get; private set; }
        public LargeFileSearch ActiveSearch { get { return activeSearch; } }
        public int ActiveMatchIndex { get { return activeMatch; } }
        internal StudioTheme Theme { get { return theme; } }

        public WinFormsLargeFileViewer(StudioTheme initialTheme)
        {
            theme = initialTheme ?? StudioTheme.ResolveTheme(null, StudioTheme.DefaultThemeId);
            DoubleBuffered = true;

            viewport = new WinFormsLargeFileViewport(this);
            viewport.Dock = DockStyle.Fill;
            vScroll = new VScrollBar();
            vScroll.Dock = DockStyle.Right;
            hScroll = new HScrollBar();
            hScroll.Dock = DockStyle.Bottom;
            vScroll.ValueChanged += delegate { if (!suppressScroll) { viewport.TopLine = vScroll.Value; viewport.AutoScrollToEnd = Document != null && Document.Follow && viewport.IsAtEnd; viewport.Invalidate(); } };
            hScroll.ValueChanged += delegate { if (!suppressScroll) { viewport.LeftColumn = hScroll.Value; viewport.Invalidate(); } };

            banner = new Panel();
            banner.Dock = DockStyle.Top;
            banner.Height = 30;
            bannerTitle = new Label();
            bannerTitle.Text = LargeFilePolicy.BannerTitle;
            bannerTitle.AutoSize = true;
            bannerTitle.Font = new Font("Segoe UI", 9f, FontStyle.Bold);
            bannerTitle.Location = new Point(10, 7);
            bannerStatus = new Label();
            bannerStatus.AutoSize = false;
            bannerStatus.AutoEllipsis = true;
            bannerStatus.Font = new Font("Segoe UI", 8.5f);
            bannerStatus.Location = new Point(270, 8);
            bannerStatus.Size = new Size(300, 18);
            bannerStatus.Anchor = AnchorStyles.Left | AnchorStyles.Top | AnchorStyles.Right;
            followBox = new CheckBox();
            followBox.Text = "Follow (tail -f)";
            followBox.Appearance = Appearance.Button;
            followBox.FlatStyle = FlatStyle.Flat;
            followBox.AutoSize = true;
            followBox.CheckedChanged += delegate { SetFollow(followBox.Checked); };
            findBtn = MakeButton("Find");
            findBtn.Click += delegate { ShowFind(); };
            gotoBtn = MakeButton("Go to Line");
            gotoBtn.Click += delegate { ShowGoTo(); };
            editBtn = MakeButton("Open in Editor");
            editBtn.Click += delegate { if (Document != null && OpenInEditorRequested != null) OpenInEditorRequested(Document.FilePath); };
            FlowLayoutPanel bannerButtons = new FlowLayoutPanel();
            bannerButtons.Dock = DockStyle.Right;
            bannerButtons.AutoSize = true;
            bannerButtons.WrapContents = false;
            bannerButtons.Padding = new Padding(0, 2, 4, 0);
            bannerButtons.Controls.Add(followBox);
            bannerButtons.Controls.Add(findBtn);
            bannerButtons.Controls.Add(gotoBtn);
            bannerButtons.Controls.Add(editBtn);
            banner.Controls.Add(bannerStatus);
            banner.Controls.Add(bannerTitle);
            banner.Controls.Add(bannerButtons);
            banner.Resize += delegate { bannerStatus.Width = Math.Max(40, banner.Width - bannerButtons.Width - bannerStatus.Left - 8); };

            findBar = new Panel();
            findBar.Dock = DockStyle.Top;
            findBar.Height = 30;
            findBar.Visible = false;
            findBox = new TextBox();
            findBox.Location = new Point(10, 5);
            findBox.Width = 260;
            findBox.KeyDown += OnFindKeyDown;
            findBox.TextChanged += delegate { searchDebounce.Stop(); searchDebounce.Start(); };
            matchCaseBox = new CheckBox();
            matchCaseBox.Text = "Aa";
            matchCaseBox.Appearance = Appearance.Button;
            matchCaseBox.FlatStyle = FlatStyle.Flat;
            matchCaseBox.Location = new Point(276, 3);
            matchCaseBox.Size = new Size(32, 24);
            matchCaseBox.CheckedChanged += delegate { RestartSearch(); };
            regexBox = new CheckBox();
            regexBox.Text = ".*";
            regexBox.Appearance = Appearance.Button;
            regexBox.FlatStyle = FlatStyle.Flat;
            regexBox.Location = new Point(310, 3);
            regexBox.Size = new Size(32, 24);
            regexBox.CheckedChanged += delegate { RestartSearch(); };
            Button prevBtn = MakeButton("↑");
            prevBtn.Location = new Point(348, 3);
            prevBtn.Click += delegate { NavigateMatch(-1); };
            Button nextBtn = MakeButton("↓");
            nextBtn.Location = new Point(384, 3);
            nextBtn.Click += delegate { NavigateMatch(1); };
            Button closeBtn = MakeButton("✕");
            closeBtn.Location = new Point(420, 3);
            closeBtn.Click += delegate { HideFind(); };
            findCount = new Label();
            findCount.AutoSize = true;
            findCount.Location = new Point(462, 8);
            findBar.Controls.Add(findBox);
            findBar.Controls.Add(matchCaseBox);
            findBar.Controls.Add(regexBox);
            findBar.Controls.Add(prevBtn);
            findBar.Controls.Add(nextBtn);
            findBar.Controls.Add(closeBtn);
            findBar.Controls.Add(findCount);

            gotoBar = new Panel();
            gotoBar.Dock = DockStyle.Top;
            gotoBar.Height = 30;
            gotoBar.Visible = false;
            Label gl = new Label();
            gl.Text = "Go to line:";
            gl.AutoSize = true;
            gl.Location = new Point(10, 8);
            gotoBox = new TextBox();
            gotoBox.Location = new Point(80, 5);
            gotoBox.Width = 160;
            gotoBox.KeyDown += OnGotoKeyDown;
            gotoBar.Controls.Add(gl);
            gotoBar.Controls.Add(gotoBox);

            // Dock order: Fill control first, then edges (WinForms docks in reverse z-order).
            Controls.Add(viewport);
            Controls.Add(vScroll);
            Controls.Add(hScroll);
            Controls.Add(gotoBar);
            Controls.Add(findBar);
            Controls.Add(banner);

            followTimer = new System.Windows.Forms.Timer();
            followTimer.Interval = 500;
            followTimer.Tick += delegate { PollNow(); };
            searchDebounce = new System.Windows.Forms.Timer();
            searchDebounce.Interval = 350;
            searchDebounce.Tick += delegate { searchDebounce.Stop(); if (findBar.Visible) RestartSearch(); };

            ApplyTheme(theme);
        }

        private static Button MakeButton(string text)
        {
            Button b = new Button();
            b.Text = text;
            b.FlatStyle = FlatStyle.Flat;
            b.AutoSize = true;
            b.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            b.Padding = new Padding(4, 0, 4, 0);
            b.Height = 24;
            return b;
        }

        public void FocusViewport()
        {
            if (viewport.CanFocus) viewport.Focus();
        }

        // ------------------------------------------------------------------ document lifecycle

        public void LoadDocument(LargeFileDocument doc, long editLimit)
        {
            if (ReferenceEquals(doc, Document)) { RefreshUi(); return; }
            UnloadDocument();
            Document = doc;
            viewport.ResetPosition();
            statusNote = "";
            if (doc != null)
            {
                doc.IndexProgressChanged += OnDocChanged;
                doc.ContentAppended += OnDocAppended;
                doc.ContentReset += OnDocReset;
                followBox.Checked = doc.Follow;
                if (doc.Follow) followTimer.Start();
                editBtn.Enabled = editLimit > 0 && doc.FileLength <= editLimit;
            }
            RefreshUi();
        }

        public void UnloadDocument()
        {
            CancelSearch();
            followTimer.Stop();
            searchDebounce.Stop();
            if (Document != null)
            {
                Document.IndexProgressChanged -= OnDocChanged;
                Document.ContentAppended -= OnDocAppended;
                Document.ContentReset -= OnDocReset;
            }
            Document = null;
            activeMatch = -1;
            findCount.Text = "";
            viewport.Invalidate();
        }

        private void PostToUi(MethodInvoker action)
        {
            if (IsDisposed || !IsHandleCreated) return;
            try { BeginInvoke(action); } catch { }
        }

        private void OnDocChanged(object sender, EventArgs e)
        {
            if (Interlocked.Exchange(ref uiPending, 1) == 1) return;
            PostToUi(delegate { Interlocked.Exchange(ref uiPending, 0); RefreshUi(); });
        }

        private void OnDocAppended(object sender, EventArgs e)
        {
            PostToUi(delegate
            {
                if (Document == null || !ReferenceEquals(sender, Document)) return;
                if (Document.Follow && viewport.AutoScrollToEnd) viewport.ScrollToEnd();
                RefreshUi();
            });
        }

        private void OnDocReset(object sender, EventArgs e)
        {
            PostToUi(delegate
            {
                if (Document == null || !ReferenceEquals(sender, Document)) return;
                CancelSearch();
                activeMatch = -1;
                findCount.Text = findBar.Visible ? "File changed — search again" : "";
                viewport.ClampPosition();
                statusNote = "file was truncated or rotated — re-reading from the start";
                RefreshUi();
            });
        }

        public void RefreshUi()
        {
            LargeFileDocument doc = Document;
            string status = doc != null ? LargeFilePolicy.DescribeStatus(doc) : "";
            if (!string.IsNullOrEmpty(statusNote)) status += "  ·  " + statusNote;
            bannerStatus.Text = status;
            if (doc == null) { viewport.Invalidate(); return; }
            suppressScroll = true;
            try
            {
                int rows = Math.Max(1, viewport.VisibleRows);
                long lines = doc.LineCount;
                int max = (int)Math.Min(int.MaxValue - 1, Math.Max(0, lines - 1));
                vScroll.Minimum = 0;
                vScroll.LargeChange = rows;
                vScroll.SmallChange = 1;
                vScroll.Maximum = max;
                vScroll.Value = (int)Math.Max(0, Math.Min(Math.Min(viewport.TopLine, max), int.MaxValue - 1));
                int cols = Math.Max(1, viewport.VisibleColumns);
                hScroll.Minimum = 0;
                hScroll.LargeChange = cols;
                hScroll.Maximum = Math.Max(0, viewport.WidestSeenColumns + 4);
                hScroll.Value = Math.Max(0, Math.Min(viewport.LeftColumn, hScroll.Maximum));
                hScroll.Visible = viewport.WidestSeenColumns > cols;
            }
            catch { }
            finally { suppressScroll = false; }
            if (activeSearch != null) findCount.Text = LargeFilePolicy.DescribeSearch(activeSearch, activeMatch);
            viewport.Invalidate();
        }

        // ------------------------------------------------------------------ follow

        public void SetFollow(bool on)
        {
            if (Document == null) return;
            if (followBox.Checked != on) { followBox.Checked = on; return; }
            Document.Follow = on;
            if (on)
            {
                viewport.AutoScrollToEnd = true;
                followTimer.Start();
                PollNow();
                viewport.ScrollToEnd();
            }
            else followTimer.Stop();
            RefreshUi();
        }

        private void PollNow()
        {
            LargeFileDocument doc = Document;
            if (doc == null || Interlocked.Exchange(ref pollInFlight, 1) == 1) return;
            ThreadPool.QueueUserWorkItem(delegate
            {
                try { doc.PollForChanges(); }
                catch { }
                finally { Interlocked.Exchange(ref pollInFlight, 0); }
            });
        }

        // ------------------------------------------------------------------ navigation

        public void GoToLine(long oneBased, int oneBasedCol)
        {
            if (Document == null) return;
            long known = Document.LineCount;
            long line = Math.Max(0, oneBased - 1);
            if (line >= known)
            {
                if (!Document.IsIndexComplete)
                    statusNote = "line " + oneBased.ToString("N0", CultureInfo.InvariantCulture) + " is not indexed yet";
                line = known - 1;
            }
            userNavigated = true;
            viewport.SetCaret(line, Math.Max(0, oneBasedCol - 1), false);
            viewport.CenterOnLine(line);
            viewport.AutoScrollToEnd = false;
            RefreshUi();
            FocusViewport();
        }

        public void ShowGoTo()
        {
            gotoBar.Visible = true;
            gotoBox.Text = (viewport.CaretLine + 1).ToString(CultureInfo.InvariantCulture);
            gotoBox.Focus();
            gotoBox.SelectAll();
        }

        private void OnGotoKeyDown(object sender, KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Enter)
            {
                long n;
                string txt = (gotoBox.Text ?? "").Split(':')[0].Trim();
                if (long.TryParse(txt, NumberStyles.Integer, CultureInfo.InvariantCulture, out n)) GoToLine(n, 1);
                gotoBar.Visible = false;
                e.Handled = true; e.SuppressKeyPress = true;
                FocusViewport();
            }
            else if (e.KeyCode == Keys.Escape)
            {
                gotoBar.Visible = false;
                e.Handled = true; e.SuppressKeyPress = true;
                FocusViewport();
            }
        }

        public void ShowFind()
        {
            findBar.Visible = true;
            bool tr;
            string sel = viewport.GetSelectedText(4096, out tr);
            if (!string.IsNullOrEmpty(sel) && sel.IndexOf('\n') < 0) findBox.Text = sel;
            findBox.Focus();
            findBox.SelectAll();
        }

        public void HideFind()
        {
            findBar.Visible = false;
            CancelSearch();
            activeMatch = -1;
            findCount.Text = "";
            viewport.Invalidate();
            FocusViewport();
        }

        private void OnFindKeyDown(object sender, KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Enter || e.KeyCode == Keys.F3)
            {
                if (activeSearch == null || !string.Equals(activeSearch.Query, findBox.Text ?? "", StringComparison.Ordinal)) RestartSearch();
                else NavigateMatch(e.Shift ? -1 : 1);
                e.Handled = true; e.SuppressKeyPress = true;
            }
            else if (e.KeyCode == Keys.Escape)
            {
                HideFind();
                e.Handled = true; e.SuppressKeyPress = true;
            }
        }

        private void RestartSearch()
        {
            StartSearch(findBox.Text, matchCaseBox.Checked, regexBox.Checked);
        }

        public LargeFileSearch StartSearch(string query, bool matchCase, bool regex)
        {
            searchDebounce.Stop();
            CancelSearch();
            activeMatch = -1;
            userNavigated = false;
            if (Document == null || string.IsNullOrEmpty(query))
            {
                findCount.Text = "";
                viewport.Invalidate();
                return null;
            }
            LargeFileSearch search = new LargeFileSearch(Document, query, matchCase, regex);
            activeSearch = search;
            long caretLine = viewport.CaretLine;
            int caretCol = viewport.CaretColumn;
            int pending = 0;
            search.Progress += delegate
            {
                if (Interlocked.Exchange(ref pending, 1) == 1 && !search.IsCompleted) return;
                PostToUi(delegate
                {
                    Interlocked.Exchange(ref pending, 0);
                    if (!ReferenceEquals(activeSearch, search)) return;
                    if (activeMatch < 0 && !userNavigated && search.StoredMatchCount > 0)
                    {
                        int idx = search.FindNextMatchIndex(caretLine, caretCol);
                        LargeFileMatch m = search.GetMatch(idx < 0 ? 0 : idx);
                        if (m.Line >= caretLine || search.IsCompleted) RevealMatch(idx < 0 ? 0 : idx);
                    }
                    findCount.Text = LargeFilePolicy.DescribeSearch(search, activeMatch);
                    viewport.Invalidate();
                });
            };
            findCount.Text = "Searching…";
            search.Start();
            return search;
        }

        public void CancelSearch()
        {
            if (activeSearch != null) activeSearch.Cancel();
            activeSearch = null;
        }

        public void NavigateMatch(int direction)
        {
            LargeFileSearch s = activeSearch;
            if (s == null) { RestartSearch(); return; }
            int count = s.StoredMatchCount;
            if (count == 0) return;
            int idx;
            if (activeMatch < 0)
            {
                idx = s.FindNextMatchIndex(viewport.CaretLine, viewport.CaretColumn);
                if (idx < 0) idx = 0;
                if (direction < 0) idx = (idx - 1 + count) % count;
            }
            else idx = (activeMatch + direction + count) % count;
            userNavigated = true;
            RevealMatch(idx);
        }

        private void RevealMatch(int idx)
        {
            LargeFileSearch s = activeSearch;
            if (s == null || Document == null || idx < 0 || idx >= s.StoredMatchCount) return;
            activeMatch = idx;
            LargeFileMatch m = s.GetMatch(idx);
            int col = m.Column >= 0 ? m.Column : Document.ByteOffsetToColumn(m.Line, m.ByteOffset);
            viewport.SetSelection(m.Line, col, m.Line, col + m.Length);
            viewport.RevealLine(m.Line, col, m.Length);
            viewport.AutoScrollToEnd = false;
            RefreshUi();
        }

        public string CopySelection()
        {
            bool truncated;
            string text = viewport.GetSelectedText(LargeFilePolicy.ClipboardCharLimit, out truncated);
            if (string.IsNullOrEmpty(text)) return "";
            try { Clipboard.SetText(text); } catch { }
            if (truncated) { statusNote = "copied the first 16M characters"; RefreshUi(); }
            return text;
        }

        internal void SyncScrollBars() { RefreshUi(); }

        public void ApplyTheme(StudioTheme t)
        {
            if (t == null) return;
            theme = t;
            bool light = t.BgEditor.GetBrightness() > 0.5f;
            BackColor = t.BgEditor;
            banner.BackColor = light ? Color.FromArgb(254, 243, 199) : Color.FromArgb(58, 46, 18);
            bannerTitle.ForeColor = t.FgPrimary;
            bannerStatus.ForeColor = t.FgSecondary;
            foreach (Control c in new Control[] { followBox, findBtn, gotoBtn, editBtn })
            {
                c.ForeColor = t.FgPrimary;
                c.BackColor = t.BgElevated;
            }
            findBar.BackColor = t.BgElevated;
            gotoBar.BackColor = t.BgElevated;
            foreach (Control c in findBar.Controls) { c.ForeColor = t.FgPrimary; if (!(c is Label)) c.BackColor = t.BgEditor; }
            foreach (Control c in gotoBar.Controls) { c.ForeColor = t.FgPrimary; if (!(c is Label)) c.BackColor = t.BgEditor; }
            viewport.Invalidate();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                UnloadDocument();
                followTimer.Dispose();
                searchDebounce.Dispose();
            }
            base.Dispose(disposing);
        }
    }

    /// <summary>Virtualised text surface: draws only visible rows; keyboard/mouse selection; Ctrl+C / Ctrl+A / Ctrl+F / F3.</summary>
    public class WinFormsLargeFileViewport : Control
    {
        private const int TabSize = 4;
        private readonly WinFormsLargeFileViewer owner;
        private readonly Font font = new Font("Consolas", 10f);
        private float charWidth = 7.5f;
        private int lineHeight = 17;
        private bool measured;
        private bool dragging;
        private long selAnchorLine = -1;
        private int selAnchorCol;
        private int widest = 80;

        public long TopLine;
        public int LeftColumn;
        public long CaretLine;
        public int CaretColumn;
        public bool AutoScrollToEnd;

        public WinFormsLargeFileViewport(WinFormsLargeFileViewer owner)
        {
            this.owner = owner;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.Selectable, true);
            Cursor = Cursors.IBeam;
            TabStop = true;
        }

        private LargeFileDocument Doc { get { return owner.Document; } }
        public int WidestSeenColumns { get { return widest; } }
        public int VisibleRows { get { return Math.Max(1, ClientSize.Height / Math.Max(1, lineHeight)); } }
        public int VisibleColumns { get { return Math.Max(1, (int)((ClientSize.Width - GutterWidth) / charWidth)); } }
        public bool IsAtEnd { get { return Doc == null || TopLine >= Math.Max(0, Doc.LineCount - VisibleRows); } }

        private float GutterWidth
        {
            get
            {
                long lines = Doc != null ? Math.Max(Doc.EstimatedTotalLines, Doc.LineCount) : 1;
                int digits = Math.Max(4, lines.ToString(CultureInfo.InvariantCulture).Length);
                return (float)Math.Ceiling((digits + 3) * charWidth);
            }
        }

        public void ResetPosition()
        {
            TopLine = 0; LeftColumn = 0; CaretLine = 0; CaretColumn = 0; selAnchorLine = -1; AutoScrollToEnd = false; widest = 80;
        }

        public void ClampPosition()
        {
            if (Doc == null) return;
            long max = Math.Max(0, Doc.LineCount - 1);
            if (CaretLine > max) CaretLine = max;
            if (TopLine > max) TopLine = Math.Max(0, max - VisibleRows + 1);
            if (selAnchorLine > max) selAnchorLine = -1;
        }

        public void ScrollToEnd()
        {
            if (Doc == null) return;
            TopLine = Math.Max(0, Doc.LineCount - VisibleRows + 1);
        }

        public void CenterOnLine(long line) { TopLine = Math.Max(0, line - VisibleRows / 2); }

        public void SetCaret(long line, int col, bool extend)
        {
            if (Doc == null) return;
            line = Math.Max(0, Math.Min(Math.Max(0, Doc.LineCount - 1), line));
            if (extend) { if (selAnchorLine < 0) { selAnchorLine = CaretLine; selAnchorCol = CaretColumn; } }
            else selAnchorLine = -1;
            CaretLine = line;
            CaretColumn = Math.Max(0, col);
        }

        public void SetSelection(long l1, int c1, long l2, int c2)
        {
            selAnchorLine = l1; selAnchorCol = c1; CaretLine = l2; CaretColumn = c2;
        }

        private bool HasSelection { get { return selAnchorLine >= 0 && (selAnchorLine != CaretLine || selAnchorCol != CaretColumn); } }

        private void GetSel(out long l1, out int c1, out long l2, out int c2)
        {
            l1 = selAnchorLine; c1 = selAnchorCol; l2 = CaretLine; c2 = CaretColumn;
            if (l2 < l1 || (l2 == l1 && c2 < c1)) { long tl = l1; l1 = l2; l2 = tl; int tc = c1; c1 = c2; c2 = tc; }
        }

        public string GetSelectedText(int maxChars, out bool truncated)
        {
            truncated = false;
            if (Doc == null || !HasSelection) return "";
            long l1, l2; int c1, c2;
            GetSel(out l1, out c1, out l2, out c2);
            return Doc.GetText(l1, c1, l2, c2, maxChars, out truncated);
        }

        public void RevealLine(long line, int col, int len)
        {
            if (line < TopLine || line >= TopLine + VisibleRows - 1) CenterOnLine(line);
            if (Doc == null) return;
            string text = Doc.GetLine(line);
            int vs = VisualColumn(text, col), ve = VisualColumn(text, Math.Min(text.Length, col + len));
            int cols = VisibleColumns;
            if (vs < LeftColumn) LeftColumn = Math.Max(0, vs - 8);
            else if (ve > LeftColumn + cols - 2) LeftColumn = Math.Max(0, ve - cols + 8);
            widest = Math.Max(widest, ve + 8);
        }

        private void EnsureCaretVisible()
        {
            int rows = VisibleRows;
            if (CaretLine < TopLine) TopLine = CaretLine;
            else if (CaretLine > TopLine + rows - 2) TopLine = Math.Max(0, CaretLine - rows + 2);
            if (Doc == null) return;
            int v = VisualColumn(Doc.GetLine(CaretLine), CaretColumn);
            int cols = VisibleColumns;
            if (v < LeftColumn) LeftColumn = Math.Max(0, v - 4);
            else if (v > LeftColumn + cols - 2) LeftColumn = v - cols + 4;
        }

        private static int VisualColumn(string text, int col)
        {
            int v = 0;
            int n = Math.Min(col, text.Length);
            for (int i = 0; i < n; i++) v = text[i] == '\t' ? (v / TabSize + 1) * TabSize : v + 1;
            return v + Math.Max(0, col - text.Length);
        }

        private static int CharColumnFromVisual(string text, int visual)
        {
            int v = 0;
            for (int i = 0; i < text.Length; i++)
            {
                int next = text[i] == '\t' ? (v / TabSize + 1) * TabSize : v + 1;
                if (visual < next) return (visual - v) * 2 >= (next - v) ? i + 1 : i;
                v = next;
            }
            return text.Length;
        }

        private static string ExpandTabs(string text)
        {
            if (text.IndexOf('\t') < 0) return text;
            StringBuilder sb = new StringBuilder(text.Length + 16);
            foreach (char c in text)
            {
                if (c == '\t') sb.Append(' ', TabSize - (sb.Length % TabSize));
                else sb.Append(c);
            }
            return sb.ToString();
        }

        private void HitTest(Point p, out long line, out int col)
        {
            line = TopLine + Math.Max(0, p.Y / Math.Max(1, lineHeight));
            col = 0;
            if (Doc == null) return;
            line = Math.Min(line, Math.Max(0, Doc.LineCount - 1));
            int visual = LeftColumn + (int)Math.Max(0, Math.Round((p.X - GutterWidth - 4) / charWidth));
            col = CharColumnFromVisual(Doc.GetLine(line), visual);
        }

        protected override bool IsInputKey(Keys keyData)
        {
            Keys k = keyData & Keys.KeyCode;
            if (k == Keys.Up || k == Keys.Down || k == Keys.Left || k == Keys.Right || k == Keys.PageUp || k == Keys.PageDown || k == Keys.Home || k == Keys.End) return true;
            return base.IsInputKey(keyData);
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            Focus();
            if (Doc == null || e.Button != MouseButtons.Left) return;
            long line; int col;
            HitTest(e.Location, out line, out col);
            bool shift = (ModifierKeys & Keys.Shift) == Keys.Shift;
            if (e.Clicks >= 2 && !shift)
            {
                string text = Doc.GetLine(line);
                int s = Math.Min(col, text.Length), en = s;
                while (s > 0 && (char.IsLetterOrDigit(text[s - 1]) || text[s - 1] == '_')) s--;
                while (en < text.Length && (char.IsLetterOrDigit(text[en]) || text[en] == '_')) en++;
                SetSelection(line, s, line, en);
            }
            else
            {
                SetCaret(line, col, shift);
                if (!shift) { selAnchorLine = line; selAnchorCol = col; }
                dragging = true;
                Capture = true;
            }
            Invalidate();
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            if (!dragging || Doc == null) return;
            if (e.Y < 0 && TopLine > 0) TopLine--;
            else if (e.Y > ClientSize.Height) TopLine = Math.Min(Math.Max(0, Doc.LineCount - VisibleRows + 1), TopLine + 1);
            long line; int col;
            HitTest(new Point(e.X, Math.Max(0, Math.Min(ClientSize.Height - 1, e.Y))), out line, out col);
            CaretLine = line; CaretColumn = col;
            owner.SyncScrollBars();
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            dragging = false;
            Capture = false;
        }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            base.OnMouseWheel(e);
            if (Doc == null) return;
            int lines = -e.Delta / 120 * 3;
            if ((ModifierKeys & Keys.Shift) == Keys.Shift) LeftColumn = Math.Max(0, LeftColumn + lines * 2);
            else
            {
                long max = Math.Max(0, Doc.LineCount - VisibleRows + 1);
                TopLine = Math.Max(0, Math.Min(max, TopLine + lines));
                AutoScrollToEnd = Doc.Follow && IsAtEnd;
            }
            owner.SyncScrollBars();
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            if (Doc == null || e.Handled) return;
            int rows = VisibleRows;
            bool shift = e.Shift;
            bool handled = true, moved = true;
            switch (e.KeyCode)
            {
                case Keys.C:
                case Keys.Insert:
                    if (!e.Control) { handled = false; moved = false; break; }
                    owner.CopySelection(); moved = false; break;
                case Keys.A:
                    if (!e.Control) { handled = false; moved = false; break; }
                    selAnchorLine = 0; selAnchorCol = 0; CaretLine = Math.Max(0, Doc.LineCount - 1); CaretColumn = Doc.GetLine(CaretLine).Length;
                    moved = false; break;
                case Keys.F:
                    if (!e.Control) { handled = false; moved = false; break; }
                    owner.ShowFind(); moved = false; break;
                case Keys.G:
                    if (!e.Control) { handled = false; moved = false; break; }
                    owner.ShowGoTo(); moved = false; break;
                case Keys.F3: owner.NavigateMatch(shift ? -1 : 1); moved = false; break;
                case Keys.Escape: owner.HideFind(); moved = false; break;
                case Keys.Up: SetCaret(CaretLine - 1, CaretColumn, shift); break;
                case Keys.Down: SetCaret(CaretLine + 1, CaretColumn, shift); break;
                case Keys.PageUp: SetCaret(CaretLine - (rows - 1), CaretColumn, shift); TopLine = Math.Max(0, TopLine - (rows - 1)); break;
                case Keys.PageDown: SetCaret(CaretLine + (rows - 1), CaretColumn, shift); TopLine += rows - 1; break;
                case Keys.Left:
                    if (CaretColumn > 0) SetCaret(CaretLine, CaretColumn - 1, shift);
                    else if (CaretLine > 0) SetCaret(CaretLine - 1, Doc.GetLine(CaretLine - 1).Length, shift);
                    break;
                case Keys.Right:
                    if (CaretColumn < Doc.GetLine(CaretLine).Length) SetCaret(CaretLine, CaretColumn + 1, shift);
                    else SetCaret(CaretLine + 1, 0, shift);
                    break;
                case Keys.Home: if (e.Control) SetCaret(0, 0, shift); else SetCaret(CaretLine, 0, shift); break;
                case Keys.End:
                    if (e.Control) { SetCaret(Doc.LineCount - 1, 0, shift); AutoScrollToEnd = Doc.Follow; }
                    else SetCaret(CaretLine, Doc.GetLine(CaretLine).Length, shift);
                    break;
                default: handled = false; moved = false; break;
            }
            if (moved)
            {
                long maxTop = Math.Max(0, Doc.LineCount - rows + 1);
                if (TopLine > maxTop) TopLine = maxTop;
                EnsureCaretVisible();
                if (e.KeyCode != Keys.End) AutoScrollToEnd = Doc.Follow && IsAtEnd;
            }
            if (handled)
            {
                e.Handled = true;
                e.SuppressKeyPress = true;
                owner.SyncScrollBars();
            }
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            if (Doc != null && Doc.Follow && AutoScrollToEnd) ScrollToEnd();
            if (IsHandleCreated) BeginInvoke(new MethodInvoker(owner.SyncScrollBars));
        }

        protected override void OnGotFocus(EventArgs e) { base.OnGotFocus(e); Invalidate(); }
        protected override void OnLostFocus(EventArgs e) { base.OnLostFocus(e); Invalidate(); }

        protected override void OnPaint(PaintEventArgs e)
        {
            StudioTheme theme = owner.Theme;
            Graphics g = e.Graphics;
            g.Clear(theme.BgEditor);
            LargeFileDocument doc = Doc;
            if (doc == null) return;
            if (!measured)
            {
                Size m = TextRenderer.MeasureText(g, "MMMMMMMMMM", font, new Size(int.MaxValue, int.MaxValue), TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix);
                charWidth = Math.Max(4f, m.Width / 10f);
                lineHeight = Math.Max(12, (int)Math.Round(font.GetHeight(g) * 1.3));
                measured = true;
            }
            float gutter = GutterWidth;
            int rows = ClientSize.Height / lineHeight + 2;
            int cols = VisibleColumns + 2;
            string[] lines = doc.GetLines(TopLine, rows);
            TextFormatFlags flags = TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine | TextFormatFlags.NoClipping;
            bool hasSel = HasSelection;
            long sl1 = 0, sl2 = 0; int sc1 = 0, sc2 = 0;
            if (hasSel) GetSel(out sl1, out sc1, out sl2, out sc2);
            List<LargeFileMatch> matches = null;
            LargeFileSearch search = owner.ActiveSearch;
            LargeFileMatch active = new LargeFileMatch();
            bool hasActive = false;
            if (search != null)
            {
                matches = search.GetMatchesInLines(TopLine, TopLine + lines.Length);
                if (owner.ActiveMatchIndex >= 0 && owner.ActiveMatchIndex < search.StoredMatchCount) { active = search.GetMatch(owner.ActiveMatchIndex); hasActive = true; }
            }
            float textX = gutter + 4 - LeftColumn * charWidth;
            using (SolidBrush curLine = new SolidBrush(theme.BgLineHighlight))
            using (SolidBrush selBrush = new SolidBrush(theme.BgSelection))
            using (SolidBrush matchBrush = new SolidBrush(Color.FromArgb(70, theme.AccentSecondary)))
            using (SolidBrush activeBrush = new SolidBrush(Color.FromArgb(130, theme.AccentPrimary)))
            using (SolidBrush gutterBg = new SolidBrush(theme.BgEditor))
            using (SolidBrush caretBrush = new SolidBrush(theme.FgPrimary))
            {
                for (int r = 0; r < lines.Length; r++)
                {
                    long docLine = TopLine + r;
                    int y = r * lineHeight;
                    string raw = lines[r];
                    if (docLine == CaretLine && !hasSel) g.FillRectangle(curLine, gutter, y, Math.Max(0, ClientSize.Width - gutter), lineHeight);
                    if (hasSel && docLine >= sl1 && docLine <= sl2)
                    {
                        int a = docLine == sl1 ? VisualColumn(raw, sc1) : 0;
                        int b = docLine == sl2 ? VisualColumn(raw, sc2) : VisualColumn(raw, raw.Length) + 1;
                        float x1 = Math.Max(gutter, textX + a * charWidth), x2 = textX + b * charWidth;
                        if (x2 > x1) g.FillRectangle(selBrush, x1, y, x2 - x1, lineHeight);
                    }
                    if (matches != null)
                    {
                        foreach (LargeFileMatch m in matches)
                        {
                            if (m.Line != docLine) continue;
                            int c = m.Column >= 0 ? m.Column : doc.ByteOffsetToColumn(m.Line, m.ByteOffset);
                            int a = VisualColumn(raw, c), b = VisualColumn(raw, c + m.Length);
                            float x1 = Math.Max(gutter, textX + a * charWidth), x2 = textX + b * charWidth;
                            bool isActive = hasActive && m.Line == active.Line && m.Column == active.Column && m.ByteOffset == active.ByteOffset;
                            if (x2 > x1) g.FillRectangle(isActive ? activeBrush : matchBrush, x1, y + 1, x2 - x1, lineHeight - 2);
                        }
                    }
                    string shown = ExpandTabs(raw);
                    if (shown.Length > widest) widest = Math.Min(LargeFileDocument.MaxDisplayLineChars + 8, shown.Length);
                    if (shown.Length > LeftColumn)
                    {
                        string slice = shown.Substring(LeftColumn, Math.Min(cols, shown.Length - LeftColumn));
                        TextRenderer.DrawText(g, slice, font, new Point((int)(gutter + 4), y + 1), theme.FgPrimary, flags);
                    }
                    if (docLine == CaretLine && Focused)
                    {
                        float cx = textX + VisualColumn(raw, CaretColumn) * charWidth;
                        if (cx >= gutter) g.FillRectangle(caretBrush, cx, y + 2, 2, lineHeight - 4);
                    }
                }
                g.FillRectangle(gutterBg, 0, 0, gutter, ClientSize.Height);
                for (int r = 0; r < lines.Length; r++)
                {
                    long docLine = TopLine + r;
                    string ln = (docLine + 1).ToString(CultureInfo.InvariantCulture);
                    Size sz = TextRenderer.MeasureText(g, ln, font, new Size(int.MaxValue, int.MaxValue), flags);
                    TextRenderer.DrawText(g, ln, font, new Point((int)(gutter - 10 - sz.Width), r * lineHeight + 1), docLine == CaretLine ? theme.FgPrimary : theme.FgMuted, flags);
                }
                if (!doc.IsIndexComplete)
                {
                    using (SolidBrush acc = new SolidBrush(theme.AccentPrimary))
                        g.FillRectangle(acc, 0, 0, (float)(ClientSize.Width * doc.IndexProgress), 2);
                }
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) font.Dispose();
            base.Dispose(disposing);
        }
    }
}
