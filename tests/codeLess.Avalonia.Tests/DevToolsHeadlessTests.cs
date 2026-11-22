using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using codeLess.AvaloniaApp;
using codeLess.Studio;
using Xunit;

namespace codeLess.Avalonia.Tests
{
    /// <summary>Workbench wiring of git blame / git graph / .http client / Markdown preview on the Avalonia UI.</summary>
    [Collection("SequentialWorkbench")]
    public class DevToolsHeadlessTests : IDisposable
    {
        private readonly string settingsPath;
        private readonly string settingsBackup;
        private readonly bool hadSettings;
        private readonly string dir;

        public DevToolsHeadlessTests()
        {
            settingsPath = IdePersistentSettings.GetSettingsFilePath();
            hadSettings = File.Exists(settingsPath);
            settingsBackup = hadSettings ? File.ReadAllText(settingsPath) : null;
            dir = Path.Combine(Path.GetTempPath(), "codeLess_DevTools_" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(dir);
            TestTrust.Trust(dir); // workspace-local tools / git / tasks need a trusted folder
        }

        public void Dispose()
        {
            MacKeymap.ForceMacKeymapOverride = null;
            try
            {
                if (hadSettings) File.WriteAllText(settingsPath, settingsBackup);
                else if (File.Exists(settingsPath)) File.Delete(settingsPath);
            }
            catch { }
            try { Directory.Delete(dir, true); } catch { }
        }

        private MainWindow current;

        private bool Pump(Func<bool> cond, int ms = 15000)
        {
            var sw = Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < ms)
            {
                Dispatcher.UIThread.RunJobs();
                current?.FlushDevToolsDebounce();
                Dispatcher.UIThread.RunJobs();
                if (cond()) return true;
                Thread.Sleep(15);
            }
            Dispatcher.UIThread.RunJobs();
            return cond();
        }

        private MainWindow NewWindow()
        {
            MacKeymap.ForceMacKeymapOverride = true;
            var w = new MainWindow(dir) { Width = 1400, Height = 900 };
            w.Show();
            Dispatcher.UIThread.RunJobs();
            current = w;
            return w;
        }

        private static string Git(string repo, params string[] args)
        {
            int exit;
            var all = new[] { "-c", "user.name=Test Author", "-c", "user.email=test@example.com", "-c", "commit.gpgsign=false", "-c", "init.defaultBranch=main" }.Concat(args).ToArray();
            string outp = LlmHarnessAndAgentEngine.RunProcessStdoutRaw("git", GitCli.JoinArgs(all), repo, 20000, out exit);
            Assert.True(exit == 0, "git " + string.Join(" ", args) + " failed");
            return outp;
        }

        [AvaloniaFact]
        public void PaletteListsDevToolCommands()
        {
            var w = NewWindow();
            try
            {
                var labels = w.BuildAllPaletteCommands().Select(c => c.DisplayLabel).ToList();
                foreach (var expected in new[] { "Git: Toggle Line Blame", "Git: Toggle File Blame", "Git: Show Graph", "Git: File History", "HTTP: Send Request", "Markdown: Open Preview", "Markdown: Open Preview to the Side" })
                    Assert.Contains(expected, labels);
            }
            finally { w.SuppressUnsavedPrompts = true; w.Close(); }
        }

        [AvaloniaFact]
        public void MarkdownPreview_ChordOpensToSide_LiveUpdates_ShiftVMaximizes_LinksOpenFiles()
        {
            File.WriteAllText(Path.Combine(dir, "other.md"), "# Other\n");
            string md = Path.Combine(dir, "README.md");
            File.WriteAllText(md, "# Title\n\nSee [other](other.md) and **bold**.\n\n- [x] done\n- todo\n\n```js\nconst a = 1;\n```\n\n| A | B |\n|---|---|\n| 1 | 2 |\n");
            var w = NewWindow();
            try
            {
                var tab = w.OpenFileInTab(md, preview: false);
                Dispatcher.UIThread.RunJobs();
                w.KeyPress(Key.K, RawInputModifiers.Meta, PhysicalKey.K, "k");
                w.KeyPress(Key.V, RawInputModifiers.None, PhysicalKey.V, "v");
                Dispatcher.UIThread.RunJobs();
                Assert.True(w.IsAuxPaneVisible);
                Assert.False(w.AuxPane.IsMaximized);
                var view = Assert.IsType<MarkdownPreviewView>(w.AuxPane.ActiveDocument.Content);
                Assert.Equal(5, view.Document.Blocks.Count);
                Assert.True(view.ContentPanel.Children.Count >= 5);
                int renders = view.RenderCount;

                // Live update: type into the editor; the preview re-renders after the debounce.
                tab.CursorLine = 0; tab.CursorCol = tab.Lines[0].Length;
                w.PrimaryEditor.Canvas.Focus();
                w.KeyTextInput(" Updated");
                Assert.True(Pump(() => view.RenderCount > renders, 5000));
                Assert.Equal("Title Updated", MarkdownParser.ToPlainText(view.Document.Blocks[0].Inlines));

                // Relative .md link opens in the editor.
                view.ActivateLink("other.md");
                Dispatcher.UIThread.RunJobs();
                Assert.EndsWith("other.md", w.ActiveTab.FilePath);

                // Cmd+Shift+V on a markdown file opens the preview maximized over the editor.
                w.ActivateTab(tab);
                Dispatcher.UIThread.RunJobs();
                w.KeyPress(Key.V, RawInputModifiers.Meta | RawInputModifiers.Shift, PhysicalKey.V, "V");
                Dispatcher.UIThread.RunJobs();
                Assert.True(w.AuxPane.IsMaximized);
                w.CloseAuxPane();
                Dispatcher.UIThread.RunJobs();
                Assert.False(w.IsAuxPaneVisible);
            }
            finally { w.SuppressUnsavedPrompts = true; w.Close(); }
        }

        private static int FreePort()
        {
            var l = new TcpListener(IPAddress.Loopback, 0);
            l.Start();
            int port = ((IPEndPoint)l.LocalEndpoint).Port;
            l.Stop();
            return port;
        }

        [AvaloniaFact]
        public void HttpFile_ShowsSendRequestLens_ClickSendsToLoopback_ShortcutResends()
        {
            int port = FreePort();
            using (var listener = new HttpListener())
            {
                listener.Prefixes.Add("http://127.0.0.1:" + port + "/");
                listener.Start();
                int hits = 0;
                Task.Run(async () =>
                {
                    while (listener.IsListening)
                    {
                        HttpListenerContext ctx;
                        try { ctx = await listener.GetContextAsync(); } catch { return; }
                        Interlocked.Increment(ref hits);
                        byte[] payload = Encoding.UTF8.GetBytes("{\"ok\":true,\"n\":" + hits + "}");
                        ctx.Response.ContentType = "application/json";
                        ctx.Response.OutputStream.Write(payload, 0, payload.Length);
                        ctx.Response.Close();
                    }
                });
                File.WriteAllText(Path.Combine(dir, "http-client.env.json"), "{\"local\":{\"port\":\"" + port + "\"}}");
                string http = Path.Combine(dir, "api.http");
                File.WriteAllText(http, "### Ping\n# @name ping\nGET http://127.0.0.1:{{port}}/ping\nAccept: application/json\n");
                var w = NewWindow();
                try
                {
                    w.CurrentSettings.HttpClientEnvironment = "local";
                    var tab = w.OpenFileInTab(http, preview: false);
                    Dispatcher.UIThread.RunJobs();
                    var lens = w.PrimaryEditor.LineAnnotationProvider(tab, 2);
                    Assert.NotNull(lens);
                    Assert.True(lens.IsAction);
                    Assert.Equal("▶ Send Request", lens.Text);
                    Assert.Null(w.PrimaryEditor.LineAnnotationProvider(tab, 3));

                    // Click the lens drawn after the request line.
                    var canvas = w.PrimaryEditor.Canvas;
                    canvas.InvalidateVisual();
                    Dispatcher.UIThread.RunJobs();
                    AvaloniaHeadlessPlatform.ForceRenderTimerTick();
                    Dispatcher.UIThread.RunJobs();
                    string line = tab.Lines[2];
                    double x = AvaloniaVirtualEditorCanvas.GutterWidth + 8 + (line.Length + 3 + 2) * canvas.CharWidth;
                    var p = canvas.TranslatePoint(new Point(x, 2.5 * canvas.LineHeight), w).Value;
                    w.MouseDown(p, MouseButton.Left, RawInputModifiers.None);
                    w.MouseUp(p, MouseButton.Left, RawInputModifiers.None);
                    Assert.True(Pump(() => w.HttpResponsePane != null && w.HttpResponsePane.Result != null, 10000));
                    var resp = w.HttpResponsePane;
                    Assert.Equal(200, resp.Result.StatusCode);
                    Assert.StartsWith("200", resp.StatusLine);
                    Assert.Contains("\"ok\": true", resp.BodyDisplayText); // pretty-printed JSON
                    Assert.StartsWith("Response: ping", w.AuxPane.ActiveDocument.Title);

                    // Cmd+Alt+R sends the request under the caret again.
                    tab.CursorLine = 3;
                    w.PrimaryEditor.Canvas.Focus();
                    w.KeyPress(Key.R, RawInputModifiers.Meta | RawInputModifiers.Alt, PhysicalKey.R, "r");
                    Assert.True(Pump(() => !w.HttpResponsePane.IsRunning && w.HttpResponsePane.Result != null && w.HttpResponsePane.Result.Body.Contains("\"n\":2"), 10000));
                    Assert.Contains(NetworkLedger.Snapshot(), e => e.Feature == "HTTP Client" && e.Url.Contains("/ping") && e.Status == 200);
                }
                finally { w.SuppressUnsavedPrompts = true; w.Close(); listener.Stop(); }
            }
        }

        [AvaloniaFact]
        public void GitRepo_InlineBlame_UncommittedLines_GraphLanesAndCommitDiff_FileHistory()
        {
            if (!GitCli.IsAvailable()) return;
            Git(dir, "init", "-q");
            string file = Path.Combine(dir, "app.txt");
            File.WriteAllText(file, "alpha\nbeta\n");
            Git(dir, "add", "app.txt");
            Git(dir, "commit", "-q", "-m", "First commit");
            Git(dir, "checkout", "-q", "-b", "feature");
            File.WriteAllText(Path.Combine(dir, "feature.txt"), "f\n");
            Git(dir, "add", "feature.txt");
            Git(dir, "commit", "-q", "-m", "Feature work");
            Git(dir, "checkout", "-q", "main");
            File.WriteAllText(file, "alpha\nbeta\ngamma\n");
            Git(dir, "commit", "-q", "-am", "Add gamma line");
            Git(dir, "merge", "-q", "--no-ff", "feature", "-m", "Merge feature");
            Git(dir, "tag", "v1.0");

            var w = NewWindow();
            try
            {
                Assert.False(w.IsLineBlameEnabled); // off by default like VS Code (git.blame.editorDecoration.enabled)
                var tab = w.OpenFileInTab(file, preview: false);
                tab.CursorLine = 2; tab.CursorCol = 0;
                Assert.Null(w.PrimaryEditor.LineAnnotationProvider(tab, 2)); // clicking a line shows no history by default
                w.ToggleLineBlame(); // Git: Toggle Line Blame opts in
                Assert.True(w.IsLineBlameEnabled);
                tab.CursorLine = 2; tab.CursorCol = 0;
                w.PrimaryEditor.Canvas.Focus();
                w.KeyPress(Key.Up, RawInputModifiers.None, PhysicalKey.ArrowUp, null);
                w.KeyPress(Key.Down, RawInputModifiers.None, PhysicalKey.ArrowDown, null);
                Assert.Equal(2, tab.CursorLine);
                EditorLineAnnotation ann = null;
                Assert.True(Pump(() => (ann = w.PrimaryEditor.LineAnnotationProvider(tab, 2)) != null, 15000));
                Assert.Equal(EditorLineAnnotation.KindBlame, ann.Kind);
                Assert.StartsWith("Test Author, ", ann.Text);
                Assert.EndsWith("• Add gamma line", ann.Text);
                Assert.Null(w.PrimaryEditor.LineAnnotationProvider(tab, 0)); // only the caret line

                // File blame mode annotates the first line of every commit run.
                w.ToggleFileBlame();
                Assert.True(Pump(() => w.PrimaryEditor.LineAnnotationProvider(tab, 0) != null, 10000));
                Assert.Contains("First commit", w.PrimaryEditor.LineAnnotationProvider(tab, 0).Text);
                Assert.Null(w.PrimaryEditor.LineAnnotationProvider(tab, 1));
                w.ToggleFileBlame();

                // Hover card with actions.
                w.ShowBlameCard(w.PrimaryEditor, tab, (GitBlameLine)ann.Tag, new Rect(100, 40, 200, 18));
                Assert.True(w.BlameHoverCard.IsVisible);
                w.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
                Assert.False(w.BlameHoverCard.IsVisible);

                // Editing makes the line uncommitted (blamed with --contents on the dirty buffer).
                tab.CursorCol = tab.Lines[2].Length;
                w.KeyTextInput("!");
                Assert.True(tab.IsDirty);
                Assert.True(Pump(() =>
                {
                    var a = w.PrimaryEditor.LineAnnotationProvider(tab, 2);
                    return a != null && a.Text == "You • Uncommitted changes";
                }, 15000));

                // Toggle off hides it (persisted setting).
                w.ToggleLineBlame();
                Assert.Null(w.PrimaryEditor.LineAnnotationProvider(tab, 2));
                w.ToggleLineBlame();

                // Git graph: 4 commits, merge opens a second lane, refs as badges.
                var graph = w.ShowGitGraph();
                Assert.NotNull(graph);
                Assert.True(Pump(() => graph.List.Commits.Count == 4, 15000));
                Assert.Equal("Merge feature", graph.List.Commits[0].Subject);
                Assert.Contains(graph.List.Commits[0].Refs, r => r.Name == "main" && r.IsCurrent);
                Assert.Contains(graph.List.Commits[0].Refs, r => r.Kind == GitRefBadge.KindTag && r.Name == "v1.0");
                Assert.True(graph.List.Graph.Max(g => g.LaneCount) >= 2);
                Assert.True(graph.List.Graph[0].Segments.Count(s => s.Kind == GitGraphSegment.CenterToBottom) == 2);

                // Select "Add gamma line" -> changed files -> diff.
                int idx = graph.List.Commits.FindIndex(c => c.Subject == "Add gamma line");
                graph.List.Select(idx);
                Assert.True(Pump(() => graph.SelectedFiles.Count == 1, 10000));
                Assert.Equal("app.txt", graph.SelectedFiles[0].Path);
                graph.List.Select(0);
                graph.SelectCommit(graph.List.Commits[idx].Hash.Substring(0, 8));
                Assert.Equal(idx, graph.List.SelectedIndex);
                Assert.True(Pump(() => graph.SelectedFiles.Count == 1, 10000));
                // Open the diff the same way a click in the file list does.
                var filesList = typeof(GitGraphView).GetField("filesList", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance).GetValue(graph) as global::Avalonia.Controls.ListBox;
                filesList.SelectedIndex = 0;
                Assert.True(Pump(() => w.AuxPane.ActiveDocument != null && w.AuxPane.ActiveDocument.Content is DiffView, 10000));
                var diff = (DiffView)w.AuxPane.ActiveDocument.Content;
                Assert.Contains(diff.Rows.Rows, r => r.Kind == LineDiffRow.Added && r.RightText == "gamma");
                diff.Rows.SetMode(false);
                Assert.Contains(diff.Rows.Rows, r => r.Kind == LineDiffRow.Added && r.RightText == "gamma");

                // File history of app.txt: 2 commits touch it.
                w.ActivateTab(tab);
                var hist = w.ShowFileHistory();
                Assert.NotNull(hist);
                Assert.True(Pump(() => hist.List.Commits.Count == 2 && !hist.IsLoading, 15000));
                Assert.False(hist.List.ShowGraph);
            }
            finally { w.SuppressUnsavedPrompts = true; w.Close(); }
        }
    }
}
