using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media.Imaging;
using Avalonia.Threading;

namespace codeLess.AvaloniaApp
{
    /// <summary>
    /// Extra screens for the automated screenshot suite (--screenshot-suite): search, Run and Debug, git graph,
    /// Markdown preview, HTTP response, settings, network activity, LSP popups, empty states and a theme sweep.
    /// Every step injects fixture data only; nothing is persisted and no network request is made.
    /// </summary>
    public partial class MainWindow
    {
        private async Task CaptureScreenshotAsync(string outputDir, string fileName, TopLevel target = null)
        {
            await Task.Delay(260);
            Dispatcher.UIThread.RunJobs();
            var top = target ?? this;
            top.InvalidateMeasure();
            top.InvalidateArrange();
            top.InvalidateVisual();
            top.UpdateLayout();
            await Task.Delay(180);
            Dispatcher.UIThread.RunJobs();
            int w = Math.Max(target == null ? 1280 : 400, (int)top.Bounds.Width);
            int h = Math.Max(target == null ? 820 : 300, (int)top.Bounds.Height);
            using var rtb = new RenderTargetBitmap(new PixelSize(w, h), new Vector(96, 96));
            rtb.Render(top);
            rtb.Save(Path.Combine(outputDir, fileName));
        }

        private static void LogSuiteError(string outputDir, Exception ex)
        {
            try { File.AppendAllText(Path.Combine(outputDir, "suite-errors.txt"), ex + Environment.NewLine); } catch { }
        }

        private async Task RunExtendedScreenshotScreensAsync(string outputDir, string repoDir)
        {
            DismissLanguageServerHintToast();
            HideDebugNotification();
            Func<string, Task> shot = name => CaptureScreenshotAsync(outputDir, name);
            if (isTerminalSplit) ToggleSplitTerminal();
            PrimaryEditor.RejectAllPendingDiffs();
            RefreshChangesetBanner();
            AgentFeed.ClearFeed();

            // Search view with results.
            try
            {
                ShowSearchView();
                SetSearchBoxText(workspaceSearchInput, "Order");
                ExecuteWorkspaceSearch("Order");
                for (int i = 0; i < 20 && IsWorkspaceSearchRunning; i++) await Task.Delay(100);
                await shot("r11_search.png");
            }
            catch (Exception ex) { LogSuiteError(outputDir, ex); }

            // Source control view.
            try { SwitchSidebarView("git"); await shot("r11_scm.png"); } catch (Exception ex) { LogSuiteError(outputDir, ex); }

            // Run and Debug view (empty state) + debug console tab.
            try
            {
                SwitchSidebarView("run");
                SwitchBottomTab("debug");
                await shot("r11_run_debug.png");
            }
            catch (Exception ex) { LogSuiteError(outputDir, ex); }

            // Problems / Output tabs (empty states).
            try { SwitchBottomTab("problems"); await shot("r11_problems_empty.png"); } catch (Exception ex) { LogSuiteError(outputDir, ex); }
            SwitchBottomTab("terminal");
            SwitchSidebarView("explorer");

            // Markdown preview to the side.
            try
            {
                string md = Path.Combine(workspaceRoot ?? repoDir ?? "", "README.md");
                if (File.Exists(md))
                {
                    OpenFileInTab(md, preview: false);
                    OpenMarkdownPreview(true);
                    await shot("r11_markdown_preview.png");
                    CloseAuxPane();
                }
            }
            catch (Exception ex) { LogSuiteError(outputDir, ex); }

            // HTTP response pane (fixture response, no request is sent).
            try
            {
                string http = Path.Combine(workspaceRoot ?? "", "api.http");
                if (File.Exists(http))
                {
                    OpenFileInTab(http, preview: false);
                    var view = new HttpResponseView { CopyToClipboard = CopyTextToClipboard };
                    OpenAuxDocument(new AuxPaneDocument { Id = "http:response", Title = "Response: List orders", Content = view, ApplyTheme = view.ApplyTheme }, maximize: false);
                    var req = new HttpResolvedRequest { Method = "GET", Url = "http://127.0.0.1:8080/orders" };
                    view.BeginRequest(req);
                    var res = new HttpResponseResult
                    {
                        Request = req, StatusCode = 200, ReasonPhrase = "OK", HttpVersion = "1.1", ElapsedMs = 42, BodyBytes = 58,
                        ContentType = "application/json",
                        Body = "[{\"id\": 1, \"customer\": \"c-42\", \"amount\": 1250.0}, {\"id\": 2, \"customer\": \"c-7\", \"amount\": 88.5}]"
                    };
                    res.Headers.Add(new HttpHeader("Content-Type", "application/json"));
                    res.Headers.Add(new HttpHeader("Server", "BaseHTTP/0.6 Python/3.12"));
                    view.ShowResult(res);
                    await shot("r11_http_response.png");
                    CloseAuxPane();
                }
            }
            catch (Exception ex) { LogSuiteError(outputDir, ex); }

            // Git graph (the sample workspace, or the codeLess repo itself when the workspace is not a repository).
            try
            {
                string repo = GitCli.FindRepoRoot(workspaceRoot ?? "");
                if (repo == null)
                {
                    string dir = AppContext.BaseDirectory;
                    while (!string.IsNullOrEmpty(dir) && !File.Exists(Path.Combine(dir, "codeLess.sln"))) dir = Path.GetDirectoryName(dir.TrimEnd('/', '\\'));
                    if (!string.IsNullOrEmpty(dir)) repo = GitCli.FindRepoRoot(dir);
                }
                if (repo != null)
                {
                    var gv = ShowGitGraph(null, repo);
                    for (int i = 0; i < 30 && gv != null && gv.List.Commits.Count == 0; i++) await Task.Delay(100);
                    if (gv != null && gv.List.Commits.Count > 1) gv.SelectCommit(gv.List.Commits[1].Hash);
                    await Task.Delay(400);
                    await shot("r11_git_graph.png");
                    CloseAuxPane();
                }
            }
            catch (Exception ex) { LogSuiteError(outputDir, ex); }

            // LSP completion + hover popups (fixture items; no language server needed).
            try
            {
                string cs = workspaceFiles.FirstOrDefault(f => f.EndsWith("OrderService.cs", StringComparison.OrdinalIgnoreCase))
                            ?? workspaceFiles.FirstOrDefault(f => f.EndsWith(".cs", StringComparison.OrdinalIgnoreCase));
                if (cs != null && activeTab != null)
                {
                    OpenFileInTab(cs, preview: false);
                    var list = new LspCompletionList();
                    string[] names = { "Place", "PendingCount", "LargeOrders", "GetHashCode", "GetType", "Equals", "ToString" };
                    int[] kinds = { 2, 10, 2, 2, 2, 2, 2 };
                    for (int i = 0; i < names.Length; i++)
                        list.Items.Add(new LspCompletionItem { Label = names[i], Kind = kinds[i], Detail = i == 0 ? "Order OrderService.Place(string customerId, decimal amount)" : null, Documentation = i == 0 ? "Places a new order for the customer and saves it." : null });
                    PrimaryLspBinding?.ShowFixtureCompletion(list, 24, 12);
                    await shot("r11_completion.png");
                    PrimaryLspBinding?.HideAll();
                }
            }
            catch (Exception ex) { LogSuiteError(outputDir, ex); }

            // Settings dialog.
            try
            {
                ShowSettingsModal();
                await shot("r11_settings.png");
                ModalHost.CloseModal();
            }
            catch (Exception ex) { LogSuiteError(outputDir, ex); }

            // Network activity window (fixture rows).
            try
            {
                NetworkLedger.Record("Update check", "GET", "https://api.github.com/repos/example/releases/latest", 0, 2048, 200, "", 180);
                NetworkLedger.Record("AI chat", "POST", "https://api.example.com/v1/chat/completions", 4200, 18000, 200, "", 2300);
                NetworkLedger.Record("HTTP client", "GET", "http://127.0.0.1:8080/orders", 0, 0, 0, "Connection refused", 3);
                ShowNetworkActivity();
                if (networkActivityWindow != null)
                {
                    await Task.Delay(300);
                    await CaptureScreenshotAsync(outputDir, "r11_network_activity.png", networkActivityWindow);
                    networkActivityWindow.Close();
                }
            }
            catch (Exception ex) { LogSuiteError(outputDir, ex); }

            // Empty editor area (no tabs) and empty workspace states.
            try
            {
                CloseAllTabs();
                await shot("r11_no_tabs.png");
            }
            catch (Exception ex) { LogSuiteError(outputDir, ex); }

            await RunEditorNicetiesScreensAsync(outputDir); // sticky scroll, bracket colors / guides, occurrences, TextMate

            // Theme sweep: editor + explorer + terminal in every built-in theme.
            try
            {
                string cs = workspaceFiles.FirstOrDefault(f => f.EndsWith("OrderService.cs", StringComparison.OrdinalIgnoreCase))
                            ?? workspaceFiles.FirstOrDefault(f => f.EndsWith(".cs", StringComparison.OrdinalIgnoreCase));
                if (cs != null) OpenFileInTab(cs, preview: false);
                foreach (var t in StudioTheme.GetAllBuiltInThemes())
                {
                    ApplyTheme(t, persistSetting: false);
                    await shot("r11_theme_" + t.Id + ".png");
                }
                ApplyTheme(StudioTheme.GetById(StudioTheme.DefaultThemeId), persistSetting: false);
            }
            catch (Exception ex) { LogSuiteError(outputDir, ex); }
        }
    }
}
