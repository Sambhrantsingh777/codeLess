using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using codeLess.AvaloniaApp;
using codeLess.Core;
using codeLess.Studio;
using Xunit;

namespace codeLess.Avalonia.Tests
{
    [Collection("SequentialWorkbench")]
    public class CiGreenW15Tests
    {
        private sealed class AllTrueContextKeyService : IContextKeyService
        {
            public object GetValue(string key) => true;
        }

        private sealed class FakeContextKeyService : IContextKeyService
        {
            private readonly string _activeKey;
            public FakeContextKeyService(string activeKey) { _activeKey = activeKey; }
            public object GetValue(string key) => string.Equals(key, _activeKey, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void AllRegistryDefaults_AreDispatchable_WhenForcedWindowsAndLinuxKeymaps()
        {
            bool? prevMac = MacKeymap.ForceMacKeymapOverride;
            bool? prevLinux = KeybindingManager.ForceLinuxKeymapOverride;
            try
            {
                DefaultCommands.Register();
                var allCommands = CommandRegistry.AllCommands;
                Assert.NotEmpty(allCommands);

                foreach (bool isLinux in new[] { false, true })
                {
                    MacKeymap.ForceMacKeymapOverride = false;
                    KeybindingManager.ForceLinuxKeymapOverride = isLinux;

                    Assert.False(MacKeymap.IsMacOS);
                    Assert.False(KeybindingManager.IsMacOS);
                    Assert.Equal(isLinux, KeybindingManager.IsLinux);

                    var manager = new KeybindingManager();
                    Assert.False(manager.IsMac);
                    var mockCtx = new AllTrueContextKeyService();

                    foreach (var cmd in allCommands)
                    {
                        string key = isLinux ? cmd.DefaultKeyLinux : cmd.DefaultKeyWin;
                        if (string.IsNullOrEmpty(key)) key = cmd.DefaultKeyWin;
                        if (string.IsNullOrEmpty(key)) continue;

                        bool fired = false;
                        manager.BindAction(cmd.Id, () => fired = true);

                        string[] parts = key.ToLowerInvariant().Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                        foreach (string p in parts)
                        {
                            manager.Dispatch(p, mockCtx, _ => { }, false);
                        }

                        Assert.True(fired, $"Command {cmd.Id} failed to dispatch on {key} (isLinux={isLinux})");
                    }
                }
            }
            finally
            {
                MacKeymap.ForceMacKeymapOverride = prevMac;
                KeybindingManager.ForceLinuxKeymapOverride = prevLinux;
            }
        }

        [AvaloniaFact]
        public void MenuModel_ShortcutsMatchPaletteAndRegistry_AndCtrlKDispatches_WhenForcedNonMac()
        {
            bool? prevMac = MacKeymap.ForceMacKeymapOverride;
            bool? prevLinux = KeybindingManager.ForceLinuxKeymapOverride;
            string ws = Path.Combine(Path.GetTempPath(), "codeless_w15_nonmac_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(ws);
            try
            {
                foreach (bool isLinux in new[] { false, true })
                {
                    MacKeymap.ForceMacKeymapOverride = false;
                    KeybindingManager.ForceLinuxKeymapOverride = isLinux;

                    var w = new MainWindow(ws) { SuppressUnsavedPrompts = true };
                    w.Show();
                    Dispatcher.UIThread.RunJobs();
                    try
                    {
                        var paletteByTitle = w.BuildAllPaletteCommands()
                            .Where(p => !string.IsNullOrEmpty(p.Title))
                            .GroupBy(p => p.Title, StringComparer.OrdinalIgnoreCase)
                            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

                        var openFolderEntry = WorkbenchMenuModel.AllCommands(false)
                            .FirstOrDefault(e => e.Id == "file.openFolder");
                        Assert.NotNull(openFolderEntry);
                        Assert.Equal("Ctrl+K Ctrl+O", openFolderEntry.ShortcutFor(false));
                        Assert.True(paletteByTitle.TryGetValue("Open Folder...", out var palOpenFolder));
                        Assert.Equal("Ctrl+K Ctrl+O", AvaloniaCommandPaletteOverlay.FormatShortcutForPlatform(palOpenFolder.Shortcut));

                        string statusMsg = "";
                        // On non-mac, cmd+k must NOT start a chord
                        bool cmdKHandled = w.KeybindingManager.Dispatch("cmd+k", new FakeContextKeyService("editorTextFocus"), m => statusMsg = m, isTerminalFocus: false);
                        Assert.False(cmdKHandled);
                        Assert.Equal("", statusMsg);

                        // On non-mac, ctrl+k in terminal does not start a chord, whereas ctrl+k in editor starts the chord
                        bool handledInTerminal = w.KeybindingManager.Dispatch("ctrl+k", new FakeContextKeyService("terminalFocus"), m => statusMsg = m, isTerminalFocus: true);
                        Assert.False(handledInTerminal);

                        bool handledInEditor = w.KeybindingManager.Dispatch("ctrl+k", new FakeContextKeyService("editorTextFocus"), m => statusMsg = m, isTerminalFocus: false);
                        Assert.True(handledInEditor);
                        Assert.Contains("Waiting for second key of chord", statusMsg);

                        bool escHandled = w.KeybindingManager.Dispatch("escape", new FakeContextKeyService("editorTextFocus"), m => statusMsg = m, isTerminalFocus: false);
                        Assert.True(escHandled);
                        Assert.Equal("", statusMsg);
                    }
                    finally
                    {
                        w.SuppressUnsavedPrompts = true;
                        w.Close();
                    }
                }
            }
            finally
            {
                MacKeymap.ForceMacKeymapOverride = prevMac;
                KeybindingManager.ForceLinuxKeymapOverride = prevLinux;
                try { Directory.Delete(ws, true); } catch { }
            }
        }

        [Fact]
        public void CrossProcessFile_ReadModifyWrite_DeletesLockFileImmediately_WithoutTimeoutStall()
        {
            string tempDir = Path.Combine(Path.GetTempPath(), "codeless_w15_lock_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);
            string filePath = Path.Combine(tempDir, "state.json");
            string lockPath = filePath + ".lock";
            try
            {
                // Warm up initial file creation and JIT so the stopwatch measures 3 sequential lock-acquire/release cycles.
                CrossProcessFile.ReadModifyWrite(filePath, p => File.WriteAllText(p, "{\"step\":-1}"), timeoutMs: 5000);
                var sw = Stopwatch.StartNew();
                for (int i = 0; i < 3; i++)
                {
                    int step = i;
                    CrossProcessFile.ReadModifyWrite(filePath, p => File.WriteAllText(p, "{\"step\":" + step + "}"), timeoutMs: 5000);
                    Assert.False(File.Exists(lockPath), "Lock file must be deleted after ReadModifyWrite completes.");
                }
                sw.Stop();
                Assert.True(sw.ElapsedMilliseconds < 1000, $"Expected 3 sequential ReadModifyWrite calls < 1000ms, took {sw.ElapsedMilliseconds}ms");
            }
            finally
            {
                try { Directory.Delete(tempDir, true); } catch { }
            }
        }

        [Fact]
        public void NativeToolCall_GetArguments_RoundTripsEscapesAndWindowsPathsExactly()
        {
            var call = new NativeToolCall
            {
                Id = "call_rt_1",
                Name = "write_file",
                ArgumentsJson = "{\"content\":\"a\\nb\\\\d \\\"q\\\"\",\"path\":\"C:\\\\Users\\\\runneradmin\\\\fake.txt\"}"
            };

            Dictionary<string, object> args = call.GetArguments();
            Assert.False(args.ContainsKey("error"));
            Assert.Equal("a\nb\\d \"q\"", args["content"]);
            Assert.Equal("C:\\Users\\runneradmin\\fake.txt", args["path"]);
        }

        [Fact]
        public async System.Threading.Tasks.Task ChatFileMentions_PinnedContextFile_NormalizesBackslashesToForwardSlashes()
        {
            string ws = Path.Combine(Path.GetTempPath(), "codeless_w15_at_" + Guid.NewGuid().ToString("N"));
            string sub = Path.Combine(ws, "src");
            Directory.CreateDirectory(sub);
            string file = Path.Combine(sub, "BigService.cs");
            File.WriteAllText(file, "public class BigService {}\n");
            try
            {
                var settings = new IdePersistentSettings();
                var engine = new LlmHarnessAndAgentEngine(settings);
                string capturedPrompt = null;
                engine.CustomModelResponder = (sys, msgs) =>
                {
                    capturedPrompt = string.Join("\n", msgs.Select(m => m.ContainsKey("content") ? m["content"] : ""));
                    return System.Threading.Tasks.Task.FromResult("ok");
                };

                await engine.RunAutonomousAgentLoopAsync(
                    mode: "ask",
                    userInstruction: "Explain @src/BigService.cs",
                    preferredVendor: engine.Vendors[0],
                    preferredHarnessId: "auto",
                    activeTab: null,
                    workspaceRoot: ws,
                    workspaceFiles: new List<string> { file },
                    contextPills: new List<string> { "src\\BigService.cs" },
                    recentTerminalOutput: ""
                );

                Assert.NotNull(capturedPrompt);
                Assert.Contains("<pinned_context_file file=\"src/BigService.cs\">", capturedPrompt);
            }
            finally
            {
                try { Directory.Delete(ws, true); } catch { }
            }
        }
    }
}
