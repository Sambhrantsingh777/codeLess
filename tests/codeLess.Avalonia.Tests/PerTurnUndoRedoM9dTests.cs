using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using codeLess.AvaloniaApp;
using codeLess.Studio;
using Xunit;

namespace codeLess.Avalonia.Tests
{
    [Collection("SequentialWorkbench")]
    public class PerTurnUndoRedoM9dTests : IDisposable
    {
        private readonly List<string> _tempDirs = new List<string>();
        private readonly List<MainWindow> _windows = new List<MainWindow>();

        private string CreateTempWorkspace()
        {
            string dir = Path.Combine(Path.GetTempPath(), "codeless-m9d-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            dir = Path.GetFullPath(dir);
            TestTrust.Trust(dir);
            _tempDirs.Add(dir);
            return dir;
        }

        private MainWindow CreateWindow()
        {
            var win = new MainWindow();
            _windows.Add(win);
            win.Show();
            return win;
        }

        public void Dispose()
        {
            foreach (var win in _windows)
            {
                try { win.Close(); } catch { }
            }
            _windows.Clear();

            foreach (string dir in _tempDirs)
            {
                try
                {
                    if (Directory.Exists(dir))
                        Directory.Delete(dir, true);
                }
                catch { }
            }
            _tempDirs.Clear();
        }

        [Fact]
        public async Task StagedTurnHistory_RecordsSnapshots_UndoRestoresBeforeStaged_And_RedoReapplies_WithoutTouchingDisk()
        {
            string ws = CreateTempWorkspace();
            string fileA = Path.Combine(ws, "a.cs");
            string fileB = Path.Combine(ws, "b.cs");
            File.WriteAllText(fileA, "class A { int v = 0; }\n");

            var settings = new IdePersistentSettings { RequireAgentToolApproval = false };
            var engine = new LlmHarnessAndAgentEngine(settings);

            // Turn 1: stage edit to a.cs -> v = 1
            int step = 0;
            engine.CustomModelResponder = (sys, msgs) =>
            {
                step++;
                if (step == 1)
                    return Task.FromResult("<tool_call>\n{\"tool\":\"read_file\",\"args\":{\"path\":\"a.cs\"}}\n</tool_call>");
                if (step == 2)
                    return Task.FromResult("<tool_call>\n{\"tool\":\"edit_file\",\"args\":{\"path\":\"a.cs\",\"oldString\":\"int v = 0;\",\"newString\":\"int v = 1;\"}}\n</tool_call>");
                return Task.FromResult("Turn 1 done.");
            };

            var t1 = await engine.RunAutonomousAgentLoopAsync("build", "Update a.cs to 1", engine.Vendors[0], "auto", null, ws, new List<string> { fileA }, new List<string>());
            Assert.Equal("class A { int v = 1; }\n", t1.StagedFileContents[fileA]);
            Assert.Equal("class A { int v = 0; }\n", File.ReadAllText(fileA)); // Disk untouched

            // Turn 2: stage edit to a.cs -> v = 2 and write b.cs
            step = 0;
            engine.CustomModelResponder = (sys, msgs) =>
            {
                step++;
                if (step == 1)
                    return Task.FromResult("<tool_call>\n{\"tool\":\"edit_file\",\"args\":{\"path\":\"a.cs\",\"oldString\":\"int v = 1;\",\"newString\":\"int v = 2;\"}}\n</tool_call>\n<tool_call>\n{\"tool\":\"write_file\",\"args\":{\"path\":\"b.cs\",\"content\":\"class B {}\\n\"}}\n</tool_call>");
                return Task.FromResult("Turn 2 done.");
            };

            var t2 = await engine.RunAutonomousAgentLoopAsync("build", "Update a.cs to 2 and add b.cs", engine.Vendors[0], "auto", null, ws, new List<string> { fileA }, new List<string>(), "", t1.StagedFileContents);
            Assert.Equal("class A { int v = 2; }\n", engine.StagedHistory.CurrentStagedFiles[fileA]);
            Assert.Equal("class B {}\n", engine.StagedHistory.CurrentStagedFiles[fileB]);
            Assert.False(File.Exists(fileB)); // Disk untouched

            // /undo reverts Turn 2 back to Turn 1's staged state (a.cs = v1, b.cs not staged)
            var undo2 = await engine.RunAutonomousAgentLoopAsync("build", "/undo", engine.Vendors[0], "auto", null, ws, new List<string> { fileA }, new List<string>());
            Assert.Single(undo2.StagedFileContents);
            Assert.Equal("class A { int v = 1; }\n", undo2.StagedFileContents[fileA]);
            Assert.False(undo2.StagedFileContents.ContainsKey(fileB));

            // /undo again reverts Turn 1 back to empty staged state
            var undo1 = await engine.RunAutonomousAgentLoopAsync("build", "/undo", engine.Vendors[0], "auto", null, ws, new List<string> { fileA }, new List<string>());
            Assert.Empty(undo1.StagedFileContents);

            // /redo re-applies Turn 1
            var redo1 = await engine.RunAutonomousAgentLoopAsync("build", "/redo", engine.Vendors[0], "auto", null, ws, new List<string> { fileA }, new List<string>());
            Assert.Single(redo1.StagedFileContents);
            Assert.Equal("class A { int v = 1; }\n", redo1.StagedFileContents[fileA]);

            // /redo again re-applies Turn 2
            var redo2 = await engine.RunAutonomousAgentLoopAsync("build", "/redo", engine.Vendors[0], "auto", null, ws, new List<string> { fileA }, new List<string>());
            Assert.Equal(2, redo2.StagedFileContents.Count);
            Assert.Equal("class A { int v = 2; }\n", redo2.StagedFileContents[fileA]);
            Assert.Equal("class B {}\n", redo2.StagedFileContents[fileB]);
            Assert.Equal("class A { int v = 0; }\n", File.ReadAllText(fileA));
            Assert.False(File.Exists(fileB));
        }

        [AvaloniaFact]
        public async Task AvaloniaHeadless_SlashUndoRedo_And_TurnRevertButton_RestoreStagedChanges_WithoutTouchingDisk()
        {
            string ws = CreateTempWorkspace();
            string appPath = Path.Combine(ws, "App.cs");
            string origDiskText = "class App {\n    int value = 10;\n}\n";
            File.WriteAllText(appPath, origDiskText);

            var win = CreateWindow();
            win.LoadWorkspace(ws);
            win.OpenFileInTab(appPath);

            int step = 0;
            win.HarnessEngine.CustomModelResponder = (sys, msgs) =>
            {
                step++;
                if (step == 1)
                    return Task.FromResult("### File: App.cs\n<<<<<<< SEARCH\n    int value = 10;\n=======\n    int value = 20;\n>>>>>>> REPLACE\nUpdated value to 20.");
                return Task.FromResult("Done.");
            };

            await win.SendChatMessageAsync("Change value to 20");

            // Proposal is staged in memory and in the review change set; disk is untouched
            Assert.Equal(origDiskText, File.ReadAllText(appPath));
            Assert.NotNull(win.AgentTrust.ActiveChangeSet);
            Assert.Contains("int value = 20;", win.GetActiveStagedFileContents()[appPath]);

            // Assistant card has a Revert button in the chat feed
            var assistantItem = win.AgentFeed.Items.Last(i => i.Role == "assistant");
            Assert.True(assistantItem.HasStagedChanges);
            Assert.False(assistantItem.IsTurnReverted);
            var revertButtons = win.AgentFeed.GetVisualDescendants().OfType<Button>().Where(b => b.Name == "TurnRevertButton").ToList();
            Assert.NotEmpty(revertButtons);

            // /undo restores staged contents to before the turn without touching disk
            string undoMsg = await win.SendChatMessageAsync("/undo");
            Assert.Contains("Reverted staged changes", undoMsg);
            Assert.Empty(win.GetActiveStagedFileContents());
            Assert.Null(win.AgentTrust.ActiveChangeSet);
            Assert.True(assistantItem.IsTurnReverted);
            Assert.Equal(origDiskText, File.ReadAllText(appPath));

            // /redo re-applies the staged changes without touching disk
            string redoMsg = await win.SendChatMessageAsync("/redo");
            Assert.Contains("Re-applied staged changes", redoMsg);
            Assert.Contains("int value = 20;", win.GetActiveStagedFileContents()[appPath]);
            Assert.NotNull(win.AgentTrust.ActiveChangeSet);
            Assert.False(assistantItem.IsTurnReverted);
            Assert.Equal(origDiskText, File.ReadAllText(appPath));

            // Clicking the Revert button on the assistant turn card also reverts and re-applies
            await win.RevertOrRedoFromChatCardAsync(assistantItem);
            Assert.Empty(win.GetActiveStagedFileContents());
            Assert.True(assistantItem.IsTurnReverted);

            await win.RevertOrRedoFromChatCardAsync(assistantItem);
            Assert.Contains("int value = 20;", win.GetActiveStagedFileContents()[appPath]);
            Assert.False(assistantItem.IsTurnReverted);
            Assert.Equal(origDiskText, File.ReadAllText(appPath));
        }

        [AvaloniaFact]
        public async Task AvaloniaHeadless_WhenAlreadyAppliedToDisk_UndoAndRedo_UseNormalRestorePathWithConfirmation()
        {
            string ws = CreateTempWorkspace();
            string appPath = Path.Combine(ws, "App.cs");
            string origDiskText = "class App {\n    int value = 10;\n}\n";
            File.WriteAllText(appPath, origDiskText);

            var win = CreateWindow();
            win.LoadWorkspace(ws);
            win.OpenFileInTab(appPath);

            win.HarnessEngine.CustomModelResponder = (sys, msgs) =>
                Task.FromResult("### File: App.cs\n<<<<<<< SEARCH\n    int value = 10;\n=======\n    int value = 99;\n>>>>>>> REPLACE\nUpdated value to 99.");

            await win.SendChatMessageAsync("Change value to 99");
            var cs = win.AgentTrust.ActiveChangeSet;
            Assert.NotNull(cs);

            // Apply (Accept) the staged change set to disk
            var acceptSummary = win.AcceptAgentChangeSet(cs, onlyIfVerified: false);
            Assert.Single(acceptSummary.Written);
            Assert.Contains("int value = 99;", File.ReadAllText(appPath));

            // 1. When confirmation is cancelled, /undo does not revert disk
            int confirmCalls = 0;
            win.AgentRestoreConfirmOverride = plan =>
            {
                confirmCalls++;
                return "cancel";
            };
            string cancelMsg = await win.SendChatMessageAsync("/undo");
            Assert.Equal(1, confirmCalls);
            Assert.Equal("Undo cancelled.", cancelMsg);
            Assert.Contains("int value = 99;", File.ReadAllText(appPath));

            // 2. When confirmation is approved, /undo reverts the applied change on disk via AgentTurnCheckpointStore
            win.AgentRestoreConfirmOverride = plan =>
            {
                confirmCalls++;
                return "skip";
            };
            string okUndoMsg = await win.SendChatMessageAsync("/undo");
            Assert.Equal(2, confirmCalls);
            Assert.Contains("Restored 1 applied file(s)", okUndoMsg);
            Assert.Equal(origDiskText, File.ReadAllText(appPath));

            // 3. /redo re-applies the turn on disk via AgentTurnCheckpointStore with confirmation
            string okRedoMsg = await win.SendChatMessageAsync("/redo");
            Assert.Equal(3, confirmCalls);
            Assert.Contains("Redid 1 applied file(s)", okRedoMsg);
            Assert.Contains("int value = 99;", File.ReadAllText(appPath));
        }
    }
}
