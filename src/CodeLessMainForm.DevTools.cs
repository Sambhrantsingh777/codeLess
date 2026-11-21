using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace codeLess.Studio
{
    /// <summary>
    /// Built-in developer tools on Windows (parity with the Avalonia UI): inline / file git blame with hover card, git graph
    /// and file history with diffs, the .http client and the native Markdown preview. Hooks: InitDevTools,
    /// ApplyDevToolsTheme, HandleDevToolsKey, TryDevToolsCtrlKChord, ExecuteDevToolsCommand.
    /// </summary>
    public partial class CodeLessMainForm
    {
        private WinAuxPane devAux;
        private Splitter devAuxSplitter;
        private GitBlameService devBlame;
        private System.Windows.Forms.Timer devBlameTimer;
        private System.Windows.Forms.Timer devMarkdownTimer;
        private System.Windows.Forms.Timer devPollTimer;
        private System.Windows.Forms.Timer devHoverTimer;
        private readonly Dictionary<string, string> devBlameKeys = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        private GitBlameFileResult devAnnotatedResult;
        private readonly Dictionary<int, EditorLineAnnotation> devBlameAnnotations = new Dictionary<int, EditorLineAnnotation>();
        private bool devFileBlame;
        private EditorTabBuffer devPolledBuffer;
        private int devPolledScrollTop = -1;
        private EditorTabBuffer devHttpBuffer;
        private int devHttpStamp = -1;
        private int devEditStamp;
        private HttpFileDocument devHttpDoc;
        private readonly Dictionary<int, EditorLineAnnotation> devLensCache = new Dictionary<int, EditorLineAnnotation>();
        private readonly Dictionary<string, HttpVariableResolver> devResolvers = new Dictionary<string, HttpVariableResolver>(StringComparer.OrdinalIgnoreCase);
        private ToolStripDropDown devBlameCard;
        private GitBlameLine devHoverPending;
        private Rectangle devHoverRect;
        private CodeLessVirtualCanvas devHoverCanvas;

        public WinAuxPane DevToolsPane { get { return devAux; } }
        public GitBlameService BlameService { get { return devBlame; } }
        public bool IsLineBlameEnabled { get { return !settings.DisableInlineGitBlame; } }

        /// <summary>Palette entries: category, title, shortcut, command id.</summary>
        public static readonly string[][] DevToolsPaletteCommands = new string[][]
        {
            new string[] { "Git", "Git: Toggle Line Blame", "", "dev_toggle_line_blame" },
            new string[] { "Git", "Git: Toggle File Blame", "", "dev_toggle_file_blame" },
            new string[] { "Git", "Git: Show Graph", "", "dev_git_graph" },
            new string[] { "Git", "Git: File History", "", "dev_file_history" },
            new string[] { "HTTP", "HTTP: Send Request", "Ctrl+Alt+R", "dev_http_send" },
            new string[] { "HTTP", "HTTP: Switch Environment...", "", "dev_http_env" },
            new string[] { "HTTP", "HTTP: Copy Request as cURL", "", "dev_http_curl" },
            new string[] { "Markdown", "Markdown: Open Preview", "Ctrl+Shift+V", "dev_md_preview" },
            new string[] { "Markdown", "Markdown: Open Preview to the Side", "Ctrl+K V", "dev_md_preview_side" },
            new string[] { "View", "View: Close Side Pane", "", "dev_close_pane" }
        };

        private void InitDevTools()
        {
            devBlame = new GitBlameService();
            devBlame.BlameReady += delegate (string path) { DevPost(delegate { editorControl.Invalidate(true); if (isSplitEditorActive) splitEditorControl.Invalidate(true); }); };
            devBlame.StatusMessage += delegate (string msg) { DevPost(delegate { DevStatus(msg); }); };

            devBlameTimer = new System.Windows.Forms.Timer { Interval = 280 };
            devBlameTimer.Tick += delegate { devBlameTimer.Stop(); DevRequestBlame(); };
            devMarkdownTimer = new System.Windows.Forms.Timer { Interval = 250 };
            devMarkdownTimer.Tick += delegate { devMarkdownTimer.Stop(); DevRefreshMarkdown(activeTab); };
            devHoverTimer = new System.Windows.Forms.Timer { Interval = 350 };
            devHoverTimer.Tick += delegate
            {
                devHoverTimer.Stop();
                if (devHoverPending != null && devHoverCanvas != null) ShowBlameCard(devHoverCanvas, editorControl.CurrentBuffer, devHoverPending, devHoverRect);
            };
            devPollTimer = new System.Windows.Forms.Timer { Interval = 200 };
            devPollTimer.Tick += delegate { DevPoll(); };
            devPollTimer.Start();

            foreach (CodeLessEditorControl ed in new CodeLessEditorControl[] { editorControl, splitEditorControl })
            {
                CodeLessEditorControl editor = ed;
                editor.Canvas.LineAnnotationProvider = DevProvideAnnotation;
                editor.Canvas.LineAnnotationClicked += delegate (EditorTabBuffer buf, EditorLineAnnotation ann, int line, Rectangle r) { DevAnnotationClicked(editor.Canvas, buf, ann, r); };
                editor.Canvas.LineAnnotationHovered += delegate (EditorTabBuffer buf, EditorLineAnnotation ann, int line, Rectangle r) { DevAnnotationHovered(editor.Canvas, ann, r); };
                editor.OnCursorOrSelectionChanged += delegate (EditorTabBuffer buf) { DevScheduleBlame(); };
                editor.OnBufferModified += delegate (EditorTabBuffer buf)
                {
                    devEditStamp++;
                    if (buf != null && !string.IsNullOrEmpty(buf.FilePath)) devBlameKeys.Remove(buf.FilePath);
                    HideBlameCard();
                    DevScheduleBlame();
                    if (buf != null && MarkdownLinkResolver.IsMarkdownFile(buf.FilePath) && devAux.Find("md:" + buf.FilePath) != null)
                    {
                        devMarkdownTimer.Stop();
                        devMarkdownTimer.Start();
                    }
                };
            }

            devAux = new WinAuxPane(activeTheme) { Dock = DockStyle.Right, Width = 520, Visible = false };
            devAuxSplitter = new Splitter { Dock = DockStyle.Right, Width = 3, Visible = false, MinExtra = 200, MinSize = 220 };
            editorsSplitContainer.Controls.Add(devAuxSplitter);
            editorsSplitContainer.Controls.Add(devAux);
            // Highest z-index docks first: pane outermost, then its splitter, then the split editor, editor fills the rest.
            editorsSplitContainer.Controls.SetChildIndex(devAux, editorsSplitContainer.Controls.Count - 1);
            editorsSplitContainer.Controls.SetChildIndex(devAuxSplitter, editorsSplitContainer.Controls.Count - 2);
            devAux.AllClosed += DevHidePane;
            devAux.MaximizeToggled += DevSetMaximized;
        }

        private void DevPost(Action a)
        {
            try { if (!IsDisposed && IsHandleCreated) BeginInvoke(a); } catch { }
        }

        private void DevStatus(string msg)
        {
            if (statusLeftLabel != null) statusLeftLabel.Text = msg ?? "";
        }

        private void ApplyDevToolsTheme(StudioTheme theme)
        {
            if (devAux != null) devAux.ApplyTheme(theme);
            HideBlameCard();
        }

        private void DevPoll()
        {
            EditorTabBuffer b = editorControl != null ? editorControl.CurrentBuffer : null;
            if (!ReferenceEquals(b, devPolledBuffer))
            {
                devPolledBuffer = b;
                devPolledScrollTop = -1;
                HideBlameCard();
                DevScheduleBlame();
            }
            if (b != null && b.ScrollTopLine != devPolledScrollTop && devAux != null && devAux.Visible && MarkdownLinkResolver.IsMarkdownFile(b.FilePath))
            {
                devPolledScrollTop = b.ScrollTopLine;
                WinAuxDocument d = devAux.Find("md:" + b.FilePath);
                WinMarkdownPreview p = d != null ? d.Content as WinMarkdownPreview : null;
                if (p != null && ReferenceEquals(devAux.ActiveDocument, d)) p.SyncToSourceLine(b.ScrollTopLine);
            }
        }

        // ---------------------------------------------------------------- side pane

        private void DevShowPane()
        {
            if (devAux.Visible) return;
            devAux.Width = Math.Max(320, editorsSplitContainer.Width / 2);
            devAux.Visible = true;
            devAuxSplitter.Visible = true;
        }

        private void DevHidePane()
        {
            if (devAux.IsMaximized) devAux.SetMaximized(false);
            devAux.Visible = false;
            devAuxSplitter.Visible = false;
            if (editorControl != null) editorControl.Focus();
        }

        private void DevSetMaximized(bool max)
        {
            if (max)
            {
                devAux.Dock = DockStyle.Fill;
                devAuxSplitter.Visible = false;
                editorControl.Visible = false;
                splitEditorControl.Visible = false;
                devAux.BringToFront();
            }
            else
            {
                devAux.Dock = DockStyle.Right;
                editorsSplitContainer.Controls.SetChildIndex(devAux, editorsSplitContainer.Controls.Count - 1);
                editorsSplitContainer.Controls.SetChildIndex(devAuxSplitter, editorsSplitContainer.Controls.Count - 2);
                editorControl.Visible = true;
                splitEditorControl.Visible = isSplitEditorActive;
                devAuxSplitter.Visible = devAux.Visible;
                devAux.Width = Math.Max(320, editorsSplitContainer.Width / 2);
            }
        }

        private void DevOpen(WinAuxDocument doc, bool maximize)
        {
            DevShowPane();
            devAux.Open(doc);
            if (maximize) devAux.SetMaximized(true);
        }

        private void DevCopy(string text)
        {
            if (string.IsNullOrEmpty(text)) return;
            CodeLessVirtualCanvas.SafeSetClipboardText(text);
            DevStatus("Copied to clipboard.");
        }

        // ---------------------------------------------------------------- annotations / blame

        private EditorLineAnnotation DevProvideAnnotation(EditorTabBuffer buf, int line)
        {
            if (buf == null || string.IsNullOrEmpty(buf.FilePath)) return null;
            if (HttpFileParser.IsHttpFile(buf.FilePath))
            {
                DevHttpDocument(buf);
                EditorLineAnnotation lens;
                if (devLensCache.TryGetValue(line, out lens)) return lens;
            }
            if (!devFileBlame && (!IsLineBlameEnabled || line != buf.CursorLine)) return null;
            if (buf.DiffHunks != null && buf.DiffHunks.Any(h => h.IsPending)) return null;
            GitBlameFileResult res = devBlame != null ? devBlame.TryGetCached(buf.FilePath) : null;
            string expected;
            if (res == null || res.NotTracked || !devBlameKeys.TryGetValue(buf.FilePath, out expected) || expected != res.ContentKey) return null;
            if (!ReferenceEquals(res, devAnnotatedResult)) { devAnnotatedResult = res; devBlameAnnotations.Clear(); }
            EditorLineAnnotation ann;
            if (!devBlameAnnotations.TryGetValue(line, out ann))
            {
                GitBlameLine bl;
                if (!res.LinesByZeroBasedIndex.TryGetValue(line, out bl)) return null;
                ann = new EditorLineAnnotation();
                ann.Kind = EditorLineAnnotation.KindBlame;
                ann.Text = GitBlameParser.FormatAnnotation(bl.Commit, res.UserName, DateTime.UtcNow);
                ann.Tag = bl;
                devBlameAnnotations[line] = ann;
            }
            if (devFileBlame && line != buf.CursorLine)
            {
                GitBlameLine prev;
                GitBlameLine cur = ann.Tag as GitBlameLine;
                if (cur != null && res.LinesByZeroBasedIndex.TryGetValue(line - 1, out prev) && prev.Commit != null && prev.Commit.Hash == cur.Commit.Hash) return null;
            }
            return ann;
        }

        private HttpFileDocument DevHttpDocument(EditorTabBuffer buf)
        {
            if (!ReferenceEquals(buf, devHttpBuffer) || devHttpStamp != devEditStamp || devHttpDoc == null)
            {
                devHttpBuffer = buf;
                devHttpStamp = devEditStamp;
                devHttpDoc = HttpFileParser.Parse(buf.Lines);
                devLensCache.Clear();
                foreach (HttpRequestBlock r in devHttpDoc.Requests)
                {
                    if (r.RequestLine < 0) continue;
                    EditorLineAnnotation a = new EditorLineAnnotation();
                    a.Kind = EditorLineAnnotation.KindCodeLens;
                    a.Text = "▶ Send Request";
                    a.IsAction = true;
                    a.Tag = r;
                    devLensCache[r.RequestLine] = a;
                }
            }
            return devHttpDoc;
        }

        private void DevScheduleBlame()
        {
            if (devBlameTimer == null) return;
            devBlameTimer.Stop();
            devBlameTimer.Start();
        }

        private void DevRequestBlame()
        {
            EditorTabBuffer buf = editorControl.CurrentBuffer;
            if (buf == null || buf.IsUntitled || string.IsNullOrEmpty(buf.FilePath) || (!IsLineBlameEnabled && !devFileBlame)) return;
            if (!File.Exists(buf.FilePath)) return;
            string content = string.Join("\n", buf.Lines.ToArray());
            if (buf.HasFinalNewline) content += "\n";
            string key = GitBlameService.ComputeContentKey(content);
            devBlameKeys[buf.FilePath] = key;
            devBlame.Request(buf.FilePath, content, buf.IsDirty, buf.CursorLine, buf.Lines.Count);
        }

        public void ToggleLineBlame()
        {
            settings.DisableInlineGitBlame = !settings.DisableInlineGitBlame;
            try { settings.Save(); } catch { }
            DevStatus("Inline git blame: " + (IsLineBlameEnabled ? "On" : "Off"));
            if (IsLineBlameEnabled) DevRequestBlame();
            editorControl.Invalidate(true);
        }

        public void ToggleFileBlame()
        {
            devFileBlame = !devFileBlame;
            DevStatus("File blame: " + (devFileBlame ? "On" : "Off"));
            if (devFileBlame) DevRequestBlame();
            editorControl.Invalidate(true);
        }

        private void DevAnnotationClicked(CodeLessVirtualCanvas canvas, EditorTabBuffer buf, EditorLineAnnotation ann, Rectangle r)
        {
            if (ann == null) return;
            HttpRequestBlock blk = ann.Tag as HttpRequestBlock;
            if (blk != null) { SendHttpRequest(buf, blk); return; }
            GitBlameLine bl = ann.Tag as GitBlameLine;
            if (bl != null) ShowBlameCard(canvas, buf, bl, r);
        }

        private void DevAnnotationHovered(CodeLessVirtualCanvas canvas, EditorLineAnnotation ann, Rectangle r)
        {
            devHoverTimer.Stop();
            GitBlameLine bl = ann != null ? ann.Tag as GitBlameLine : null;
            if (bl != null)
            {
                devHoverPending = bl;
                devHoverRect = r;
                devHoverCanvas = canvas;
                devHoverTimer.Start();
            }
            else
            {
                devHoverPending = null;
                if (devBlameCard != null && devBlameCard.Visible)
                {
                    System.Windows.Forms.Timer t = new System.Windows.Forms.Timer { Interval = 450 };
                    t.Tick += delegate
                    {
                        t.Stop();
                        t.Dispose();
                        if (devBlameCard != null && devBlameCard.Visible && !devBlameCard.Bounds.Contains(Cursor.Position)) HideBlameCard();
                    };
                    t.Start();
                }
            }
        }

        public void HideBlameCard()
        {
            if (devBlameCard != null && devBlameCard.Visible) devBlameCard.Close();
        }

        private void ShowBlameCard(CodeLessVirtualCanvas canvas, EditorTabBuffer buf, GitBlameLine bl, Rectangle anchor)
        {
            if (bl == null || bl.Commit == null || buf == null) return;
            HideBlameCard();
            GitBlameCommit c = bl.Commit;
            StudioTheme t = activeTheme;
            GitBlameFileResult res = devBlame.TryGetCached(buf.FilePath);
            string repo = res != null ? res.RepoRoot : GitCli.FindRepoRoot(buf.FilePath);
            Panel p = new Panel { Width = 460, Height = c.IsUncommitted ? 60 : 210, BackColor = t.BgElevated, Padding = new Padding(10) };
            Label head = new Label { Dock = DockStyle.Top, Height = 20, Font = new Font("Segoe UI", 9.5f, FontStyle.Bold), ForeColor = t.FgPrimary, Text = c.IsUncommitted ? "Uncommitted changes" : c.Author + (string.IsNullOrEmpty(c.AuthorMail) ? "" : "  <" + c.AuthorMail + ">") };
            Label when = new Label { Dock = DockStyle.Top, Height = 18, Font = new Font("Segoe UI", 8.5f), ForeColor = t.FgSecondary, Text = c.IsUncommitted ? "This line has not been committed yet." : GitCli.FormatRelativeTime(c.AuthorTimeUtc, DateTime.UtcNow) + "  ·  " + c.AuthorTimeUtc.ToLocalTime().ToString("dddd, MMMM d, yyyy HH:mm", CultureInfo.InvariantCulture) };
            if (!c.IsUncommitted)
            {
                TextBox msg = new TextBox { Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, BorderStyle = BorderStyle.None, BackColor = t.BgElevated, ForeColor = t.FgPrimary, Font = new Font("Segoe UI", 9f), Text = c.Summary, ScrollBars = ScrollBars.Vertical };
                Label hash = new Label { Dock = DockStyle.Bottom, Height = 18, Font = new Font("Consolas", 8.5f), ForeColor = t.FgMuted, Text = c.Hash };
                FlowLayoutPanel actions = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 30, WrapContents = false };
                Button copy = WinDevToolsColors.FlatButton("Copy Hash", t);
                Button graph = WinDevToolsColors.FlatButton("Open in Git Graph", t);
                Button changes = WinDevToolsColors.FlatButton("Show Changes", t);
                copy.Click += delegate { DevCopy(c.Hash); HideBlameCard(); };
                graph.Click += delegate { HideBlameCard(); ShowGitGraph(c.Hash, repo); };
                string rel = string.IsNullOrEmpty(c.Filename) ? GitCli.RepoRelativePath(repo, buf.FilePath) : c.Filename;
                changes.Click += delegate { HideBlameCard(); DevOpenCommitFileDiff(repo, c.Hash, rel); };
                actions.Controls.AddRange(new Control[] { copy, graph, changes });
                p.Controls.Add(msg);
                p.Controls.Add(hash);
                p.Controls.Add(actions);
                string full = devBlame.TryGetFullMessage(repo, c.Hash);
                if (full != null) msg.Text = full.Replace("\n", "\r\n");
                else
                {
                    string capturedRepo = repo;
                    Task.Factory.StartNew(delegate
                    {
                        string m = devBlame.LoadFullMessage(capturedRepo, c.Hash);
                        DevPost(delegate { if (!msg.IsDisposed && !string.IsNullOrEmpty(m)) msg.Text = m.Replace("\n", "\r\n"); });
                    });
                }
            }
            p.Controls.Add(when);
            p.Controls.Add(head);
            devBlameCard = new ToolStripDropDown { Padding = Padding.Empty, AutoClose = true, BackColor = t.BgElevated };
            ToolStripControlHost host = new ToolStripControlHost(p) { Margin = Padding.Empty, Padding = Padding.Empty, AutoSize = false, Size = p.Size };
            devBlameCard.Items.Add(host);
            devBlameCard.Show(canvas, new Point(anchor.Left, anchor.Bottom + 2));
        }

        // ---------------------------------------------------------------- git graph / history

        private string DevRepoForContext()
        {
            string r = null;
            if (activeTab != null && !string.IsNullOrEmpty(activeTab.FilePath)) r = GitCli.FindRepoRoot(activeTab.FilePath);
            if (r == null && !string.IsNullOrEmpty(workspaceRoot)) r = GitCli.FindRepoRoot(workspaceRoot);
            return r;
        }

        public WinGitGraphView ShowGitGraph(string selectHash, string repoOverride)
        {
            string repo = repoOverride ?? DevRepoForContext();
            if (repo == null) { DevStatus("Git Graph: no git repository found for the open folder or file."); return null; }
            string id = "git:graph:" + repo;
            WinAuxDocument existing = devAux.Find(id);
            WinGitGraphView view = existing != null ? existing.Content as WinGitGraphView : null;
            if (view == null)
            {
                view = new WinGitGraphView(repo, null, activeTheme);
                DevWireGraph(view);
                WinAuxDocument d = new WinAuxDocument { Id = id, Title = "Git Graph", Tooltip = repo, Content = view };
                WinGitGraphView v = view;
                d.ApplyTheme = delegate (StudioTheme th) { v.ApplyTheme(th); };
                DevOpen(d, false);
            }
            else DevOpen(existing, false);
            if (!string.IsNullOrEmpty(selectHash)) view.SelectCommit(selectHash);
            DevCheckGit();
            return view;
        }

        public WinGitGraphView ShowFileHistory()
        {
            EditorTabBuffer buf = activeTab;
            if (buf == null || string.IsNullOrEmpty(buf.FilePath)) { DevStatus("File History: open a file first."); return null; }
            string repo = GitCli.FindRepoRoot(buf.FilePath);
            string rel = repo != null ? GitCli.RepoRelativePath(repo, buf.FilePath) : null;
            if (rel == null) { DevStatus("File History: " + Path.GetFileName(buf.FilePath) + " is not inside a git repository."); return null; }
            WinGitGraphView view = new WinGitGraphView(repo, rel, activeTheme);
            DevWireGraph(view);
            WinAuxDocument d = new WinAuxDocument { Id = "git:history:" + buf.FilePath, Title = "History: " + Path.GetFileName(buf.FilePath), Tooltip = rel, Content = view };
            d.ApplyTheme = delegate (StudioTheme th) { view.ApplyTheme(th); };
            DevOpen(d, false);
            DevCheckGit();
            return view;
        }

        private void DevCheckGit()
        {
            Task.Factory.StartNew(delegate { if (!GitCli.IsAvailable()) DevPost(delegate { DevStatus(GitCli.GitMissingHint); }); });
        }

        private void DevWireGraph(WinGitGraphView view)
        {
            view.CopyToClipboard = DevCopy;
            view.FileDiffRequested += delegate (GitCommitInfo commit, GitChangedFile file) { DevOpenCommitFileDiff(view.RepoRoot, commit, file); };
        }

        private void DevOpenCommitFileDiff(string repo, GitCommitInfo commit, GitChangedFile file)
        {
            Task.Factory.StartNew(delegate
            {
                string oldT, newT, note;
                GitHistoryService.GetFileVersions(repo, commit, file, out oldT, out newT, out note);
                DevPost(delegate
                {
                    WinDiffView dv = new WinDiffView(file.DisplayName + "  ·  " + GitHistoryService.StatusLabel(file.Status) + " in " + commit.ShortHash + " " + commit.Subject, oldT, newT, note, activeTheme);
                    WinAuxDocument d = new WinAuxDocument { Id = "diff:" + commit.Hash + ":" + file.Path, Title = Path.GetFileName(file.Path) + " (" + commit.ShortHash + ")", Tooltip = file.Path, Content = dv };
                    d.ApplyTheme = delegate (StudioTheme th) { dv.ApplyTheme(th); };
                    DevOpen(d, false);
                });
            });
        }

        private void DevOpenCommitFileDiff(string repo, string hash, string repoRelPath)
        {
            if (string.IsNullOrEmpty(repo) || string.IsNullOrEmpty(hash) || string.IsNullOrEmpty(repoRelPath)) return;
            Task.Factory.StartNew(delegate
            {
                int exit;
                string raw = GitCli.Run(repo, 10000, out exit, "log", "-1", GitLogParser.LogFormat, hash);
                List<GitCommitInfo> commits = exit == 0 ? GitLogParser.Parse(raw, null) : new List<GitCommitInfo>();
                if (commits.Count == 0) return;
                string err;
                List<GitChangedFile> files = GitHistoryService.GetChangedFiles(repo, commits[0], out err);
                GitChangedFile file = files.FirstOrDefault(f => f.Path == repoRelPath);
                if (file == null) { file = new GitChangedFile(); file.Path = repoRelPath; }
                DevPost(delegate { DevOpenCommitFileDiff(repo, commits[0], file); });
            });
        }

        // ---------------------------------------------------------------- HTTP client

        public void SendHttpRequestAtCaret()
        {
            EditorTabBuffer buf = activeTab;
            if (buf == null || !HttpFileParser.IsHttpFile(buf.FilePath)) { DevStatus("Send Request works in .http / .rest files."); return; }
            HttpRequestBlock blk = HttpFileParser.FindRequestAtLine(DevHttpDocument(buf), buf.CursorLine);
            if (blk == null) { DevStatus("No request at the cursor (requests are separated by ###)."); return; }
            SendHttpRequest(buf, blk);
        }

        private HttpVariableResolver DevResolver(EditorTabBuffer buf)
        {
            HttpVariableResolver r;
            if (!devResolvers.TryGetValue(buf.FilePath, out r)) { r = new HttpVariableResolver(); devResolvers[buf.FilePath] = r; }
            return r;
        }

        public WinHttpResponseView HttpResponsePane
        {
            get
            {
                WinAuxDocument d = devAux != null ? devAux.Find("http:response") : null;
                return d != null ? d.Content as WinHttpResponseView : null;
            }
        }

        public async void SendHttpRequest(EditorTabBuffer buf, HttpRequestBlock blk)
        {
            if (buf == null || blk == null) return;
            HttpVariableResolver resolver = DevResolver(buf);
            HttpFileDocument doc = DevHttpDocument(buf);
            string filePath = buf.FilePath;
            string envName = settings.HttpClientEnvironment ?? "";
            string root = workspaceRoot;
            HttpEnvironmentSet env = await Task.Factory.StartNew(delegate { return HttpEnvironmentSet.Discover(filePath, root); });
            resolver.FileVariables = new Dictionary<string, string>(doc.FileVariables, StringComparer.Ordinal);
            resolver.EnvironmentVariables = env.GetVariables(envName);
            HttpResolvedRequest req = resolver.Resolve(blk, Path.GetDirectoryName(filePath));

            WinHttpResponseView view = HttpResponsePane;
            if (view == null)
            {
                view = new WinHttpResponseView(activeTheme);
                view.CopyToClipboard = DevCopy;
                WinHttpResponseView v = view;
                view.ResendRequested += delegate
                {
                    EditorTabBuffer b = activeTab != null && string.Equals(activeTab.FilePath, filePath, StringComparison.OrdinalIgnoreCase) ? activeTab : buf;
                    HttpRequestBlock again = DevHttpDocument(b).Requests.FirstOrDefault(r => r.RequestLine == blk.RequestLine) ?? blk;
                    SendHttpRequest(b, again);
                };
                WinAuxDocument d = new WinAuxDocument { Id = "http:response", Title = "Response", Content = view };
                d.ApplyTheme = delegate (StudioTheme th) { v.ApplyTheme(th); };
                DevOpen(d, false);
            }
            else DevOpen(devAux.Find("http:response"), false);
            WinAuxDocument entry = devAux.Find("http:response");
            if (entry != null) { entry.Title = "Response: " + (string.IsNullOrEmpty(blk.Name) ? req.Method : blk.Name); devAux.RefreshTitles(); }
            DevStatus(req.UnresolvedVariables.Count > 0
                ? "HTTP: unresolved variables " + string.Join(", ", req.UnresolvedVariables.Take(4).ToArray())
                : "HTTP: " + req.Method + " " + req.Url + (envName.Length > 0 ? "  [env: " + envName + "]" : ""));
            CancellationToken token = view.BeginRequest(req);
            int timeoutMs = settings.HttpClientTimeoutSeconds > 0 ? settings.HttpClientTimeoutSeconds * 1000 : HttpClientRunner.DefaultTimeoutMs;
            HttpResponseResult result;
            try { result = await HttpClientRunner.SendAsync(req, timeoutMs, token); }
            catch (Exception ex) { result = new HttpResponseResult(); result.Request = req; result.Error = "Request failed: " + ex.Message; }
            if (!view.IsDisposed) view.ShowResult(result);
            if (result.HasResponse && !string.IsNullOrEmpty(blk.Name)) resolver.NamedResponses[blk.Name] = result;
            DevStatus("HTTP: " + HttpClientRunner.StatusSummary(result));
        }

        public void CopyHttpRequestAsCurl()
        {
            EditorTabBuffer buf = activeTab;
            if (buf == null || !HttpFileParser.IsHttpFile(buf.FilePath)) return;
            HttpRequestBlock blk = HttpFileParser.FindRequestAtLine(DevHttpDocument(buf), buf.CursorLine);
            if (blk == null) return;
            HttpVariableResolver r = DevResolver(buf);
            r.FileVariables = DevHttpDocument(buf).FileVariables;
            r.EnvironmentVariables = HttpEnvironmentSet.Discover(buf.FilePath, workspaceRoot).GetVariables(settings.HttpClientEnvironment ?? "");
            DevCopy(HttpClientRunner.ToCurl(r.Resolve(blk, Path.GetDirectoryName(buf.FilePath))));
        }

        public void PickHttpEnvironment()
        {
            string basePath = activeTab != null && !string.IsNullOrEmpty(activeTab.FilePath) ? activeTab.FilePath : Path.Combine(workspaceRoot ?? "", "x.http");
            HttpEnvironmentSet set = HttpEnvironmentSet.Discover(basePath, workspaceRoot);
            ContextMenuStrip menu = CreateAutoDisposingContextMenu();
            ToolStripMenuItem none = new ToolStripMenuItem("No Environment") { Checked = string.IsNullOrEmpty(settings.HttpClientEnvironment) };
            none.Click += delegate { DevSetEnvironment(""); };
            menu.Items.Add(none);
            foreach (string n in set.Names)
            {
                string name = n;
                ToolStripMenuItem it = new ToolStripMenuItem(name) { Checked = name == settings.HttpClientEnvironment };
                it.Click += delegate { DevSetEnvironment(name); };
                menu.Items.Add(it);
            }
            if (set.Names.Count == 0) DevStatus("No environments found. Create http-client.env.json next to your .http file: {\"dev\": {\"host\": \"http://localhost:8080\"}}");
            menu.Show(editorControl, new Point(Math.Max(10, editorControl.Width / 2 - 100), 40));
        }

        private void DevSetEnvironment(string name)
        {
            settings.HttpClientEnvironment = name ?? "";
            try { settings.Save(); } catch { }
            DevStatus("HTTP environment: " + (string.IsNullOrEmpty(name) ? "none" : name));
        }

        // ---------------------------------------------------------------- Markdown preview

        public WinMarkdownPreview OpenMarkdownPreview(bool toSide)
        {
            EditorTabBuffer buf = activeTab;
            if (buf == null || !MarkdownLinkResolver.IsMarkdownFile(buf.FilePath)) { DevStatus("Markdown preview: open a .md file first."); return null; }
            string id = "md:" + buf.FilePath;
            WinAuxDocument existing = devAux.Find(id);
            WinMarkdownPreview view = existing != null ? existing.Content as WinMarkdownPreview : null;
            if (view == null)
            {
                view = new WinMarkdownPreview(buf.FilePath, activeTheme);
                WinMarkdownPreview v = view;
                view.LinkClicked += delegate (string url, MdLinkTarget target) { DevHandleMarkdownLink(target); };
                WinAuxDocument d = new WinAuxDocument { Id = id, Title = "Preview " + Path.GetFileName(buf.FilePath), Tooltip = buf.FilePath, Content = view };
                d.ApplyTheme = delegate (StudioTheme th) { v.ApplyTheme(th); };
                DevOpen(d, !toSide);
            }
            else
            {
                DevOpen(existing, !toSide);
                if (toSide && devAux.IsMaximized) devAux.SetMaximized(false);
            }
            view.Render(string.Join("\n", buf.Lines.ToArray()));
            devPolledScrollTop = -1;
            return view;
        }

        private void DevRefreshMarkdown(EditorTabBuffer buf)
        {
            if (buf == null || devAux == null) return;
            WinAuxDocument d = devAux.Find("md:" + buf.FilePath);
            WinMarkdownPreview p = d != null ? d.Content as WinMarkdownPreview : null;
            if (p != null) p.Render(string.Join("\n", buf.Lines.ToArray()));
        }

        private void DevHandleMarkdownLink(MdLinkTarget t)
        {
            if (t == null) return;
            if (t.Kind == MdLinkTarget.KindExternal)
            {
                if (!EditorLinkResolver.OpenExternalUrl(t.Url)) DevStatus("Could not open " + t.Url);
            }
            else if (t.Kind == MdLinkTarget.KindFile)
            {
                if (File.Exists(t.FilePath))
                {
                    if (devAux.IsMaximized) devAux.SetMaximized(false);
                    OpenFileInTab(t.FilePath);
                }
                else DevStatus("Linked file not found: " + t.FilePath);
            }
            else if (t.Kind == MdLinkTarget.KindUnsupported) DevStatus("Unsupported link: " + t.Url);
        }

        // ---------------------------------------------------------------- commands & keys

        /// <summary>Ctrl+Shift+V (Markdown preview), Ctrl+Alt+R (send request), Esc (blame card). True when handled.</summary>
        private bool HandleDevToolsKey(KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Escape && devBlameCard != null && devBlameCard.Visible) { HideBlameCard(); return true; }
            if (e.Control && e.Shift && !e.Alt && e.KeyCode == Keys.V && activeTab != null && MarkdownLinkResolver.IsMarkdownFile(activeTab.FilePath))
            {
                e.SuppressKeyPress = true;
                e.Handled = true;
                OpenMarkdownPreview(false);
                return true;
            }
            if (e.Control && e.Alt && !e.Shift && e.KeyCode == Keys.R && activeTab != null && HttpFileParser.IsHttpFile(activeTab.FilePath))
            {
                e.SuppressKeyPress = true;
                e.Handled = true;
                SendHttpRequestAtCaret();
                return true;
            }
            return false;
        }

        /// <summary>Ctrl+K V opens the Markdown preview to the side.</summary>
        private bool TryDevToolsCtrlKChord(KeyEventArgs e)
        {
            if (e.KeyCode == Keys.V && !e.Control && !e.Shift && !e.Alt)
            {
                e.SuppressKeyPress = true;
                OpenMarkdownPreview(true);
                return true;
            }
            return false;
        }

        private bool ExecuteDevToolsCommand(string cmdId)
        {
            switch (cmdId)
            {
                case "dev_toggle_line_blame": ToggleLineBlame(); return true;
                case "dev_toggle_file_blame": ToggleFileBlame(); return true;
                case "dev_git_graph": ShowGitGraph(null, null); return true;
                case "dev_file_history": ShowFileHistory(); return true;
                case "dev_http_send": SendHttpRequestAtCaret(); return true;
                case "dev_http_env": PickHttpEnvironment(); return true;
                case "dev_http_curl": CopyHttpRequestAsCurl(); return true;
                case "dev_md_preview": OpenMarkdownPreview(false); return true;
                case "dev_md_preview_side": OpenMarkdownPreview(true); return true;
                case "dev_close_pane": if (devAux != null) devAux.CloseAll(); return true;
            }
            return false;
        }
    }
}
