using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using codeLess.AvaloniaApp;
using codeLess.Studio;
using Xunit;

namespace codeLess.Avalonia.Tests
{
    [Collection("SequentialWorkbench")]
    public class WorkbenchHeadlessTests : IDisposable
    {
        private readonly string _settingsFilePath;
        private readonly string _settingsBackupContent;
        private readonly bool _hadSettingsFile;

        public WorkbenchHeadlessTests()
        {
            _settingsFilePath = IdePersistentSettings.GetSettingsFilePath();
            _hadSettingsFile = !string.IsNullOrEmpty(_settingsFilePath) && File.Exists(_settingsFilePath);
            _settingsBackupContent = _hadSettingsFile ? File.ReadAllText(_settingsFilePath) : null;
        }

        public void Dispose()
        {
            try
            {
                if (_hadSettingsFile && !string.IsNullOrEmpty(_settingsFilePath) && _settingsBackupContent != null)
                {
                    File.WriteAllText(_settingsFilePath, _settingsBackupContent);
                }
                else if (!_hadSettingsFile && !string.IsNullOrEmpty(_settingsFilePath) && File.Exists(_settingsFilePath))
                {
                    File.Delete(_settingsFilePath);
                }
            }
            catch { }
        }

        private static string CreateTempWorkspace(out string fileA, out string fileB)
        {
            string dir = Path.Combine(Path.GetTempPath(), "codeLess_AvaloniaTest_" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(dir);
            fileA = Path.Combine(dir, "Calculator.cs");
            fileB = Path.Combine(dir, "Helper.py");
            File.WriteAllText(fileA, "using System;\npublic class Calculator {\n    public int Add(int a, int b) { return a + b; }\n}\n");
            File.WriteAllText(fileB, "def greet(name):\n    return f'Hello {name}'\n");
            return dir;
        }

        [AvaloniaFact]
        public void Workbench_Initializes_All_Core_Panels_And_Tabs()
        {
            string fileA, fileB;
            string ws = CreateTempWorkspace(out fileA, out fileB);
            try
            {
                var window = new MainWindow(ws);
                window.Show();

                Assert.Equal(ws, window.WorkspacePath);
                Assert.True(window.IsSidebarVisible);
                Assert.True(window.IsBottomPanelVisible);
                Assert.False(window.IsCommandPaletteVisible);
                Assert.NotNull(window.Editor);
                Assert.NotEmpty(window.OpenTabs);
                Assert.NotNull(window.ActiveTab);
                Assert.NotNull(window.CurrentTheme);
                Assert.NotEmpty(window.HarnessEngine.IndexedChunks);
                Assert.NotEmpty(window.HarnessEngine.WorkspaceSymbols);

                window.SuppressUnsavedPrompts = true; // a dirty buffer must not cancel Close() and leak the window
                window.Close();
            }
            finally
            {
                try { Directory.Delete(ws, true); } catch { }
            }
        }

        [AvaloniaFact]
        public void OpenFile_SwitchTabs_Edit_TrackDirty_And_SaveToDisk()
        {
            string fileA, fileB;
            string ws = CreateTempWorkspace(out fileA, out fileB);
            try
            {
                var window = new MainWindow(ws);
                window.Show();

                var tabA = window.OpenFileInTab(fileA);
                var tabB = window.OpenFileInTab(fileB);
                Assert.NotNull(tabA);
                Assert.NotNull(tabB);
                Assert.Equal(tabB, window.ActiveTab);
                Assert.Contains("def greet", window.Editor.Text);

                // Switch back to tabA
                window.ActivateTab(tabA);
                Assert.Equal(tabA, window.ActiveTab);
                Assert.False(tabA.IsDirty);

                // Modify buffer in AvaloniaEdit
                window.Editor.Text = "using System;\npublic class Calculator {\n    public int Add(int a, int b) { return a + b + 100; }\n}\n";
                Assert.True(tabA.IsDirty);

                // Save active tab and verify disk write & dirty flag reset
                bool saved = window.SaveActiveTab();
                Assert.True(saved);
                Assert.False(tabA.IsDirty);
                Assert.Contains("a + b + 100", File.ReadAllText(fileA));

                // Close tabA
                window.CloseTab(tabA);
                Assert.DoesNotContain(tabA, window.OpenTabs);

                window.SuppressUnsavedPrompts = true; // a dirty buffer must not cancel Close() and leak the window
                window.Close();
            }
            finally
            {
                try { Directory.Delete(ws, true); } catch { }
            }
        }

        [AvaloniaFact]
        public void CommandPalette_And_Panel_Toggles_Work_Cleanly()
        {
            string fileA, fileB;
            string ws = CreateTempWorkspace(out fileA, out fileB);
            try
            {
                var window = new MainWindow(ws);
                window.Show();

                Assert.False(window.IsCommandPaletteVisible);
                window.ToggleCommandPalette();
                Assert.True(window.IsCommandPaletteVisible);
                window.ToggleCommandPalette();
                Assert.False(window.IsCommandPaletteVisible);

                // Toggle Sidebar via command palette ID
                Assert.True(window.IsSidebarVisible);
                Assert.True(window.ExecuteCommandPaletteAction("view.toggleSidebar"));
                Assert.False(window.IsSidebarVisible);
                Assert.True(window.ExecuteCommandPaletteAction("view.toggleSidebar"));
                Assert.True(window.IsSidebarVisible);

                // Toggle Bottom Terminal Panel via command palette ID
                Assert.True(window.IsBottomPanelVisible);
                Assert.True(window.ExecuteCommandPaletteAction("view.toggleTerminal"));
                Assert.False(window.IsBottomPanelVisible);
                Assert.True(window.ExecuteCommandPaletteAction("view.toggleTerminal"));
                Assert.True(window.IsBottomPanelVisible);

                window.SuppressUnsavedPrompts = true; // a dirty buffer must not cancel Close() and leak the window
                window.Close();
            }
            finally
            {
                try { Directory.Delete(ws, true); } catch { }
            }
        }

        [AvaloniaFact]
        public void ThemeSwitching_Applies_All_BuiltIn_StudioThemes()
        {
            string fileA, fileB;
            string ws = CreateTempWorkspace(out fileA, out fileB);
            try
            {
                var window = new MainWindow(ws);
                window.Show();

                foreach (var theme in window.BuiltInThemes)
                {
                    window.ApplyTheme(theme.Name);
                    Assert.Equal(theme.Id, window.CurrentTheme.Id);
                }

                window.SuppressUnsavedPrompts = true; // a dirty buffer must not cancel Close() and leak the window
                window.Close();
            }
            finally
            {
                try { Directory.Delete(ws, true); } catch { }
            }
        }

        [AvaloniaFact]
        public void WorkspaceSearch_Finds_Matches_Across_Files()
        {
            string fileA, fileB;
            string ws = CreateTempWorkspace(out fileA, out fileB);
            try
            {
                var window = new MainWindow(ws);
                window.Show();

                window.SwitchActivityView("SEARCH");
                Assert.Equal("SEARCH", window.ActiveActivityView);

                var results = window.RunWorkspaceSearch("Calculator");
                Assert.NotEmpty(results);
                Assert.Contains(results, r => r.FilePath.EndsWith("Calculator.cs", StringComparison.OrdinalIgnoreCase));

                window.SuppressUnsavedPrompts = true; // a dirty buffer must not cancel Close() and leak the window
                window.Close();
            }
            finally
            {
                try { Directory.Delete(ws, true); } catch { }
            }
        }

        [AvaloniaFact]
        public void MacKeymap_Uses_Cmd_Meta_On_MacOS_And_Ctrl_On_Windows()
        {
            string fileA, fileB;
            string ws = CreateTempWorkspace(out fileA, out fileB);
            try
            {
                // 1. Test macOS Keymap (Cmd = KeyModifiers.Meta)
                MacKeymap.ForceMacKeymapOverride = true;
                Assert.True(MacKeymap.IsMacOS);
                Assert.Equal(KeyModifiers.Meta, MacKeymap.PrimaryModifier);
                Assert.Equal("⌘P", MacKeymap.FormatShortcut("P"));
                Assert.Equal("⇧⌘P", MacKeymap.FormatShortcut("P", shift: true));
                Assert.Equal("⌘S", MacKeymap.FormatShortcut("S"));

                var window = new MainWindow(ws);
                window.Show();

                // On macOS, Ctrl+P must NOT open Command Palette, whereas Cmd+P (Meta) MUST open it
                var ctrlP = new KeyEventArgs { Key = Key.P, KeyModifiers = KeyModifiers.Control };
                window.HandleKeyShortcut(ctrlP);
                Assert.False(window.IsCommandPaletteVisible);

                var cmdP = new KeyEventArgs { Key = Key.P, KeyModifiers = KeyModifiers.Meta };
                window.HandleKeyShortcut(cmdP);
                Assert.True(window.IsCommandPaletteVisible);

                // Cmd+B toggles sidebar on macOS
                var cmdB = new KeyEventArgs { Key = Key.B, KeyModifiers = KeyModifiers.Meta };
                window.HandleKeyShortcut(cmdB);
                Assert.False(window.IsSidebarVisible);

                // 2. Test Windows/Linux Keymap (Ctrl = KeyModifiers.Control)
                MacKeymap.ForceMacKeymapOverride = false;
                Assert.False(MacKeymap.IsMacOS);
                Assert.Equal(KeyModifiers.Control, MacKeymap.PrimaryModifier);
                Assert.Equal("Ctrl+P", MacKeymap.FormatShortcut("P"));
                Assert.Equal("Ctrl+Shift+P", MacKeymap.FormatShortcut("P", shift: true));

                var ctrlB = new KeyEventArgs { Key = Key.B, KeyModifiers = KeyModifiers.Control };
                window.HandleKeyShortcut(ctrlB);
                Assert.True(window.IsSidebarVisible);

                window.SuppressUnsavedPrompts = true; // a dirty buffer must not cancel Close() and leak the window
                window.Close();
            }
            finally
            {
                MacKeymap.ForceMacKeymapOverride = null;
                try { Directory.Delete(ws, true); } catch { }
            }
        }

        [AvaloniaFact]
        public async Task AgentChat_Executes_Harness_And_Stages_Surgical_Edit_Into_Editor()
        {
            string fileA, fileB;
            string ws = CreateTempWorkspace(out fileA, out fileB);
            try
            {
                var window = new MainWindow(ws);
                window.Show();
                window.OpenFileInTab(fileA);

                string capturedSysPrompt = null;
                window.HarnessEngine.CustomModelResponder = (sys, msgs) =>
                {
                    capturedSysPrompt = sys;
                    string patch =
                        "<<<<<<< SEARCH\n" +
                        "    public int Add(int a, int b) { return a + b; }\n" +
                        "=======\n" +
                        "    public int Add(int a, int b) { return checked(a + b); }\n" +
                        ">>>>>>> REPLACE";
                    return Task.FromResult(patch);
                };

                string response = await window.SendChatMessageAsync("Use checked arithmetic in Add");
                Assert.NotNull(response);
                Assert.NotNull(capturedSysPrompt);
                Assert.Contains("[AUTONOMOUS 8-TOOL EXECUTION PROTOCOL]", capturedSysPrompt);
                Assert.Contains("checked(a + b)", window.Editor.Text);
                Assert.True(window.ChatHistoryItems.Count >= 4);

                window.SuppressUnsavedPrompts = true; // a dirty buffer must not cancel Close() and leak the window
                window.Close();
            }
            finally
            {
                try { Directory.Delete(ws, true); } catch { }
            }
        }

        [AvaloniaFact]
        public void IntegratedTerminal_StartsSession_And_ExecutesWorkspaceCommand()
        {
            string fileA, fileB;
            string ws = CreateTempWorkspace(out fileA, out fileB);
            try
            {
                var window = new MainWindow(ws);
                window.Show();

                Assert.NotNull(window.TerminalSession);
                Assert.Contains(ws, window.TerminalBufferText);

                window.SendTerminalCommand("echo CODELESS_AVALONIA_PTY_OK");
                Assert.Contains("echo CODELESS_AVALONIA_PTY_OK", window.TerminalBufferText);

                // Verify opencode TUI Alternate Screen (?1049h) + SGR Mouse Wheel (?1000h + ?1006h) + Kitty/DCS/Colon-SGR safety
                var vt = new Vt100TerminalBuffer(80, 24, false);
                vt.FeedText("\x1b[?1049h\x1b[?1000h\x1b[?1006h\x1b[5;10H\x1b[>1u\x1bP+q544e\x1b\\\x1b[4:3m\x1b[38:2::56:189:248mOpenCodeTUI\x1b[<u");
                Assert.True(vt.IsAlternateScreen);
                Assert.Equal(1000, vt.MouseTrackingMode);
                Assert.True(vt.SgrMouseMode);
                // Kitty >1u / <u must NOT trigger ANSI RestoreCursor to (0,0), and DCS payload '+q544e' must NOT print
                Assert.Equal(4, vt.CursorRow);
                Assert.DoesNotContain("+q544e", vt.GetLineText(4));
                Assert.Contains("OpenCodeTUI", vt.GetLineText(4));
                // SGR mouse wheel up = 64, wheel down = 65
                Assert.Equal("\x1b[<64;15;8M", vt.EncodeMouseWheelSequence(true, 15, 8, false, false, false));
                Assert.Equal("\x1b[<65;15;8M", vt.EncodeMouseWheelSequence(false, 15, 8, false, false, false));

                window.SuppressUnsavedPrompts = true; // a dirty buffer must not cancel Close() and leak the window
                window.Close();
            }
            finally
            {
                try { Directory.Delete(ws, true); } catch { }
            }
        }

        [AvaloniaFact]
        public async Task LiveOpenAiEndpoint_Executes_MultiTurn_AgentLoop_Over_Local_HttpServer()
        {
            string fileA, fileB;
            string ws = CreateTempWorkspace(out fileA, out fileB);
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            int port = ((IPEndPoint)listener.LocalEndpoint).Port;
            int requestCount = 0;
            string firstRequestPath = "";
            bool running = true;

            _ = Task.Run(() =>
            {
                while (running)
                {
                    TcpClient client = null;
                    try
                    {
                        client = listener.AcceptTcpClient();
                        using (client)
                        using (NetworkStream ns = client.GetStream())
                        {
                            ns.ReadTimeout = 5000;
                            ns.WriteTimeout = 5000;
                            var headerBuf = new MemoryStream();
                            int b, state = 0;
                            while ((b = ns.ReadByte()) != -1)
                            {
                                headerBuf.WriteByte((byte)b);
                                if ((state == 0 && b == '\r') || (state == 1 && b == '\n') || (state == 2 && b == '\r') || (state == 3 && b == '\n'))
                                {
                                    if (++state == 4) break;
                                }
                                else state = (b == '\r') ? 1 : 0;
                            }
                            string headers = Encoding.ASCII.GetString(headerBuf.ToArray());
                            string[] lines = headers.Split(new[] { "\r\n" }, StringSplitOptions.RemoveEmptyEntries);
                            if (lines.Length > 0 && string.IsNullOrEmpty(firstRequestPath))
                            {
                                string[] parts = lines[0].Split(' ');
                                if (parts.Length >= 2) firstRequestPath = parts[1];
                            }
                            int contentLen = 0;
                            foreach (string l in lines)
                            {
                                if (l.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase))
                                    int.TryParse(l.Substring("Content-Length:".Length).Trim(), out contentLen);
                            }
                            byte[] reqBytes = new byte[contentLen];
                            int read = 0;
                            while (read < contentLen)
                            {
                                int r = ns.Read(reqBytes, read, contentLen - read);
                                if (r <= 0) break;
                                read += r;
                            }
                            int turn = Interlocked.Increment(ref requestCount);
                            string replyContent = turn == 1
                                ? "<tool_call>\n{\"tool\":\"read_file\",\"args\":{\"path\":\"Calculator.cs\"}}\n</tool_call>"
                                : "<<<<<<< SEARCH\n    public int Add(int a, int b) { return a + b; }\n=======\n    public int Add(int a, int b) { return checked(a + b); }\n>>>>>>> REPLACE";
                            string escaped = replyContent.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n");
                            string json = "{\"id\":\"chatcmpl-test\",\"choices\":[{\"index\":0,\"message\":{\"role\":\"assistant\",\"content\":\"" + escaped + "\"}}]}";
                            byte[] bodyBytes = Encoding.UTF8.GetBytes(json);
                            byte[] respHdr = Encoding.ASCII.GetBytes(
                                "HTTP/1.1 200 OK\r\nContent-Type: application/json; charset=utf-8\r\nContent-Length: " + bodyBytes.Length + "\r\nConnection: close\r\n\r\n");
                            ns.Write(respHdr, 0, respHdr.Length);
                            ns.Write(bodyBytes, 0, bodyBytes.Length);
                            ns.Flush();
                        }
                    }
                    catch
                    {
                        if (client != null) { try { client.Close(); } catch { } }
                        if (!running) break;
                    }
                }
            });

            try
            {
                var window = new MainWindow(ws);
                window.Show();
                window.OpenFileInTab(fileA);

                // Configure real HTTP OpenAI-compatible ByokVendorProfile with CustomModelResponder = null
                window.HarnessEngine.CustomModelResponder = null;
                var activeVendor = window.HarnessEngine.Vendors.First();
                activeVendor.Id = "openai";
                activeVendor.Name = "LocalMockOpenAI";
                activeVendor.BaseUrl = "http://127.0.0.1:" + port + "/v1";
                activeVendor.ApiKey = "sk-test-local-mock";
                activeVendor.SelectedModel = "gpt-4o-mini-mock";

                string response = await window.SendChatMessageAsync("Read Calculator.cs and use checked arithmetic in Add");
                Assert.NotNull(response);
                Assert.Equal(2, requestCount);
                Assert.Equal("/v1/chat/completions", firstRequestPath);
                Assert.Contains("checked(a + b)", window.Editor.Text);

                window.SuppressUnsavedPrompts = true; // a dirty buffer must not cancel Close() and leak the window
                window.Close();
            }
            finally
            {
                running = false;
                try { listener.Stop(); } catch { }
                try { Directory.Delete(ws, true); } catch { }
            }
        }

        [AvaloniaFact]
        public void LargeFile_EditorPerformanceAndAppName_VerifyZeroLagAndBranding()
        {
            string fileA, fileB;
            string ws = CreateTempWorkspace(out fileA, out fileB);
            try
            {
                // Verify Application.Name is "codeLess" and never "Avalonia Application"
                Assert.Equal("codeLess", global::Avalonia.Application.Current?.Name);

                string largeFile = Path.Combine(ws, "LargeService.cs");
                var sb = new StringBuilder(500 * 1024);
                sb.AppendLine("using System;");
                sb.AppendLine("namespace LargeDemo {");
                sb.AppendLine("    public class LargeService {");
                for (int i = 0; i < 10000; i++)
                {
                    sb.Append("        public int ComputeItem").Append(i).Append("(int x) { return x + ").Append(i).AppendLine("; }");
                }
                sb.AppendLine("    }");
                sb.AppendLine("}");
                File.WriteAllText(largeFile, sb.ToString());

                var window = new MainWindow(ws);
                window.Show();

                var tab = window.OpenFileInTab(largeFile, preview: false);
                Assert.NotNull(tab);
                Assert.False(tab.IsLargeFile); // QA #17: a 10,000-line file is an ordinary file (bracket matching, git markers stay on)
                Assert.True(tab.Lines.Count >= 10000);

                // Verify typing coalesced undo snapshots on a 10,000-line file executes rapidly (< 150ms for 20 characters)
                tab.CursorLine = 5000;
                tab.CursorCol = 8;
                var sw = System.Diagnostics.Stopwatch.StartNew();
                for (int c = 0; c < 20; c++)
                {
                    tab.PushUndoSnapshot(coalesceTyping: true);
                    tab.InsertTextAtCursor("a");
                }
                sw.Stop();
                Assert.True(sw.ElapsedMilliseconds < PerfBudget.Ms(250), $"Expected fast coalesced large-file edits, took {sw.ElapsedMilliseconds}ms");
                Assert.Single(tab.UndoStack);

                window.SuppressUnsavedPrompts = true; // a dirty buffer must not cancel Close() and leak the window
                window.Close();
            }
            finally
            {
                try { Directory.Delete(ws, true); } catch { }
            }
        }

        [Fact]
        public void UnixAndConPty_RealShellSpawnEchoAndCleanExit_Works()
        {
            string tempDir = Path.Combine(Path.GetTempPath(), "codeLess_RealPty_" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(tempDir);
            try
            {
                bool isWin = System.Runtime.InteropServices.RuntimeInformation.IsOSPlatform(System.Runtime.InteropServices.OSPlatform.Windows);
                string cmd = isWin
                    ? "cmd.exe /c echo CODELESS_REAL_PTY_VERIFIED"
                    : "/bin/sh -c \"echo CODELESS_REAL_PTY_VERIFIED\"";

                using (var session = new ConPtyTerminalSession("sh", cmd, tempDir, 90, 24, false))
                {
                    var sw = System.Diagnostics.Stopwatch.StartNew();
                    while (sw.ElapsedMilliseconds < 4000)
                    {
                        string text = session.GetRecentOutputText(50) ?? "";
                        if (text.Contains("CODELESS_REAL_PTY_VERIFIED"))
                            break;
                        Thread.Sleep(40);
                    }
                    string finalOut = session.GetRecentOutputText(50) ?? "";
                    Assert.Contains("CODELESS_REAL_PTY_VERIFIED", finalOut);
                }
            }
            finally
            {
                try { Directory.Delete(tempDir, true); } catch { }
            }
        }
    }
}
