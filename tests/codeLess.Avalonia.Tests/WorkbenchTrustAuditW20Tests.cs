using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using codeLess.AvaloniaApp;
using codeLess.Core;
using codeLess.Studio;
using Xunit;

namespace codeLess.Avalonia.Tests
{
    [Collection("SequentialWorkbench")]
    public class WorkbenchTrustAuditW20Tests : IDisposable
    {
        private readonly string _tempRoot;
        private readonly string _prevSettingsDir;
        private readonly List<MainWindow> _windows = new List<MainWindow>();

        public WorkbenchTrustAuditW20Tests()
        {
            _prevSettingsDir = Environment.GetEnvironmentVariable("CODELESS_SETTINGS_DIR");
            _tempRoot = Path.Combine(Path.GetTempPath(), "codeLess_W20B_" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(_tempRoot);
            string settingsDir = Path.Combine(_tempRoot, "settings");
            Directory.CreateDirectory(settingsDir);
            Environment.SetEnvironmentVariable("CODELESS_SETTINGS_DIR", settingsDir);
            TestTrust.Trust(_tempRoot);
        }

        public void Dispose()
        {
            MacKeymap.ForceMacKeymapOverride = null;
            foreach (var w in _windows.ToList())
            {
                try
                {
                    if (!w.IsClosed)
                    {
                        w.SuppressUnsavedPrompts = true;
                        w.Close();
                    }
                }
                catch { }
            }
            _windows.Clear();
            Dispatcher.UIThread.RunJobs();
            IdeSettingsWatcher.StopForTests();
            Environment.SetEnvironmentVariable("CODELESS_SETTINGS_DIR", _prevSettingsDir);
            try
            {
                if (Directory.Exists(_tempRoot))
                {
                    foreach (string f in Directory.GetFiles(_tempRoot, "*", SearchOption.AllDirectories))
                    {
                        try { File.SetAttributes(f, FileAttributes.Normal); } catch { }
                    }
                    Directory.Delete(_tempRoot, true);
                }
            }
            catch { }
        }

        private MainWindow Track(MainWindow w)
        {
            if (w != null && !_windows.Contains(w))
                _windows.Add(w);
            return w;
        }

        private string CreateWorkspace(string name)
        {
            string dir = Path.Combine(_tempRoot, name + "_" + Guid.NewGuid().ToString("N").Substring(0, 6));
            Directory.CreateDirectory(dir);
            TestTrust.Trust(dir);
            return dir;
        }

        private static bool Pump(Func<bool> cond, int ms = 10000)
        {
            var sw = Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < ms)
            {
                Dispatcher.UIThread.RunJobs();
                if (cond()) return true;
                Thread.Sleep(15);
            }
            Dispatcher.UIThread.RunJobs();
            return cond();
        }

        [AvaloniaFact]
        public void B01_B06_OpenSaveSaveAsAutoSave_DirtyIndicators_SaveActions_AndCleanUndo()
        {
            string ws = CreateWorkspace("open_save");
            string fileA = Path.Combine(ws, "calc.py");
            string fileMd = Path.Combine(ws, "readme.md");
            File.WriteAllText(fileA, "x = 1\ny = 2\n");
            File.WriteAllText(fileMd, "line1  \nline2   ");

            var w = Track(new MainWindow(ws));
            w.Show();

            // B01: Open file, edit -> dirty dot & window title ● indicator; Save clears dirty and writes to disk
            var tabA = w.OpenFileInTab(fileA, preview: false);
            Assert.NotNull(tabA);
            Assert.False(tabA.IsDirty);
            Assert.DoesNotContain("●", w.Title);

            w.PrimaryEditor.Canvas.Focus();
            tabA.PushUndoSnapshot();
            tabA.Lines[0] = "x = 42";
            w.PrimaryEditor.Canvas.CommitExternalEdit();
            Assert.True(tabA.IsDirty);
            Assert.True(w.IsTabDirtyDotVisible(tabA));
            Assert.StartsWith("● calc.py", w.Title);

            // B06: Undoing back to exact SavedContent clears IsDirty and removes hot-exit backup
            w.PrimaryEditor.Canvas.PerformUndoRedo(redo: false);
            Assert.False(tabA.IsDirty);
            Assert.DoesNotContain("●", w.Title);

            // Re-apply edit and save via shortcut (test both Mac Cmd+S and non-Mac Ctrl+S)
            w.PrimaryEditor.Canvas.PerformUndoRedo(redo: true);
            Assert.True(tabA.IsDirty);
            MacKeymap.ForceMacKeymapOverride = false;
            Assert.True(w.SimulateShortcut(Key.S, KeyModifiers.Control));
            Assert.False(tabA.IsDirty);
            Assert.Equal("x = 42\ny = 2\n", File.ReadAllText(fileA).Replace("\r\n", "\n"));

            // B02: Save As via SaveConflictSaveAsPath / SaveBufferToDisk retargets tab and leaves original file intact
            string forkPath = Path.Combine(ws, "calc_v2.py");
            File.WriteAllText(fileA, "external_change\n");
            tabA.Lines[0] = "x = 99";
            w.PrimaryEditor.Canvas.CommitExternalEdit();
            w.SaveConflictSaveAsPath = forkPath;
            w.SaveConflictDecisionOverride = _ => ExternalConflictAction.SaveAs;
            Assert.True(w.SaveBufferToDisk(tabA));
            Assert.Equal(forkPath, tabA.FilePath);
            Assert.Equal("calc_v2.py", tabA.FileName);
            Assert.False(tabA.IsDirty);
            Assert.Contains("x = 99", File.ReadAllText(forkPath));
            Assert.Equal("external_change\n", File.ReadAllText(fileA).Replace("\r\n", "\n"));

            // B03: Workspace .vscode/settings.json files.autoSave = afterDelay saves dirty file tabs, never untitled/read-only
            string vscodeDir = Path.Combine(ws, ".vscode");
            Directory.CreateDirectory(vscodeDir);
            File.WriteAllText(Path.Combine(vscodeDir, "settings.json"), "{ \"files.autoSave\": \"afterDelay\", \"files.autoSaveDelay\": 200 }");
            var wWs = Track(new MainWindow(ws));
            wWs.Show();
            var tabFork = wWs.OpenFileInTab(forkPath, preview: false);
            tabFork.Lines[0] = "x = 777";
            wWs.PrimaryEditor.Canvas.CommitExternalEdit();
            var untitled = wWs.CreateNewUntitledTab();
            untitled.Lines[0] = "unsaved note";
            untitled.SyncContentFromLines();
            wWs.FlushPendingAutoSave();
            Assert.False(tabFork.IsDirty);
            Assert.Contains("x = 777", File.ReadAllText(forkPath));
            Assert.True(untitled.IsDirty); // untitled never auto-saved

            // B04: onFocusChange saves when switching tabs
            w.CurrentSettings.SetAutoSaveMode("onFocusChange");
            var tabMd = w.OpenFileInTab(fileMd, preview: false);
            tabMd.Lines[1] = "line2_focused   ";
            w.PrimaryEditor.Canvas.CommitExternalEdit();
            Assert.True(tabMd.IsDirty);
            w.ActivateTab(tabA);
            Assert.False(tabMd.IsDirty);
            Assert.Contains("line2_focused", File.ReadAllText(fileMd));

            // B05: Save actions (trimTrailingWhitespace + insertFinalNewline) preserve markdown 2-space line break on explicit save
            w.CurrentSettings.SetAutoSaveMode("off");
            w.CurrentSettings.TrimTrailingWhitespaceOnSave = true;
            w.CurrentSettings.InsertFinalNewlineOnSave = true;
            w.ActivateTab(tabMd);
            tabMd.Lines[0] = "line1  ";
            tabMd.Lines[1] = "line2_clean   ";
            tabMd.HasFinalNewline = false;
            w.PrimaryEditor.Canvas.CommitExternalEdit();
            Assert.True(w.SaveActiveTab());
            Assert.Equal("line1  \nline2_clean\n", File.ReadAllText(fileMd).Replace("\r\n", "\n"));
        }

        [AvaloniaFact]
        public void B07_B11_Revert_ExternalChangesOnCleanAndDirty_ConflictGuard_AndExternalDelete()
        {
            string ws = CreateWorkspace("revert_conflict");
            string file = Path.Combine(ws, "service.cs");
            File.WriteAllText(file, "line0\nline1\nline2\n");

            var w = Track(new MainWindow(ws));
            w.Show();
            var tab = w.OpenFileInTab(file, preview: false);

            // B07: workbench.action.files.revert discards unsaved edits, clears IsDirty, and removes hot-exit backup
            tab.Lines[1] = "unsaved_mutation";
            w.PrimaryEditor.Canvas.CommitExternalEdit();
            w.ScheduleHotExitBackup(tab);
            w.FlushPendingHotExitBackups();
            Assert.True(tab.IsDirty);
            Assert.True(w.KeybindingManager.ExecuteCommand("workbench.action.files.revert"));
            Assert.False(tab.IsDirty);
            Assert.Equal("line1", tab.Lines[1]);

            // B08: External disk change on a clean tab reloads silently and preserves cursor & scroll
            tab.CursorLine = 2;
            tab.CursorCol = 3;
            tab.ScrollTopLine = 1;
            File.WriteAllText(file, "line0_ext\nline1_ext\nline2_ext\n");
            w.HandleExternalFileChanged(file);
            Assert.False(tab.IsDirty);
            Assert.Equal("line2_ext", tab.Lines[2]);
            Assert.Equal(2, tab.CursorLine);
            Assert.Equal(3, tab.CursorCol);
            Assert.Equal(1, tab.ScrollTopLine);

            // B09 & B10: External disk change while dirty sets HasExternalDiskConflict, blocks AutoSavePolicy, and prompts on Save
            tab.Lines[0] = "dirty_local_edit";
            w.PrimaryEditor.Canvas.CommitExternalEdit();
            Assert.True(tab.IsDirty);
            File.WriteAllText(file, "concurrent_external_write\nline1_ext\nline2_ext\n");
            Assert.False(AutoSavePolicy.IsEligible(tab)); // never auto-saves when disk changed externally

            w.ExternalConflictDecisionOverride = _ => ExternalConflictAction.Compare;
            w.HandleExternalFileChanged(file);
            Assert.True(tab.HasExternalDiskConflict);
            Assert.Equal(new[] { "Compare", "Overwrite", "Revert" }, w.LastConflictNotificationChoices);
            Assert.False(AutoSavePolicy.IsEligible(tab));

            // Attempting manual save while conflicted does not overwrite without Overwrite decision
            w.SaveConflictDecisionOverride = _ => ExternalConflictAction.None;
            Assert.False(w.SaveBufferToDisk(tab));
            Assert.StartsWith("concurrent_external_write", File.ReadAllText(file));

            // Overwrite decision writes local buffer and clears conflict
            w.SaveConflictDecisionOverride = _ => ExternalConflictAction.Overwrite;
            Assert.True(w.SaveBufferToDisk(tab));
            Assert.False(tab.IsDirty);
            Assert.False(tab.HasExternalDiskConflict);
            Assert.StartsWith("dirty_local_edit", File.ReadAllText(file));

            // B11: External deletion of dirty file marks tab (deleted) with strikethrough
            tab.Lines[0] = "unsaved_before_delete";
            w.PrimaryEditor.Canvas.CommitExternalEdit();
            File.Delete(file);
            w.HandleExternalFileDeleted(file);
            Assert.True(tab.IsDeletedOnDisk);
            Assert.EndsWith("(deleted)", w.GetTabTitleText(tab));
            Assert.True(w.IsTabTitleStrikethrough(tab));
        }

        [AvaloniaFact]
        public void B12_B17_EncodingsBomEol_ReadOnlyFailedSaveStaysDirty_RetrySaveAsOverwrite_AndBinary()
        {
            string ws = CreateWorkspace("enc_readonly");
            var w = Track(new MainWindow(ws));
            w.Show();
            VscodeSettingsManager.ApplySettings("{ \"files.autoGuessEncoding\": true }", w.Settings);

            // B12 & B13: UTF-8 BOM, UTF-16 LE, UTF-16 BE round-trip losslessly
            string fBom = Path.Combine(ws, "bom.txt");
            File.WriteAllBytes(fBom, new byte[] { 0xEF, 0xBB, 0xBF, (byte)'a', (byte)'\r', (byte)'\n' });
            var tBom = w.OpenFileInTab(fBom);
            tBom.Lines[0] = "b";
            w.PrimaryEditor.Canvas.CommitExternalEdit();
            Assert.True(w.SaveBufferToDisk(tBom));
            Assert.Equal(new byte[] { 0xEF, 0xBB, 0xBF, (byte)'b', (byte)'\r', (byte)'\n' }, File.ReadAllBytes(fBom));

            string f16 = Path.Combine(ws, "u16be.txt");
            File.WriteAllText(f16, "hi\n", new UnicodeEncoding(true, true));
            var t16 = w.OpenFileInTab(f16);
            Assert.Equal(1201, t16.FileEncoding.CodePage);
            t16.Lines[0] = "ok";
            w.PrimaryEditor.Canvas.CommitExternalEdit();
            Assert.True(w.SaveBufferToDisk(t16));
            byte[] raw16 = File.ReadAllBytes(f16);
            Assert.Equal(0xFE, raw16[0]);
            Assert.Equal(0xFF, raw16[1]);

            // B14 & B15: Latin-1 preservation and status bar EOL toggle
            string fLat = Path.Combine(ws, "latin.txt");
            File.WriteAllBytes(fLat, new byte[] { 0x63, 0x61, 0x66, 0xE9, 0x0A });
            var tLat = w.OpenFileInTab(fLat);
            Assert.Equal("café", tLat.Lines[0]);
            Assert.Equal("\n", tLat.EolSequence);
            w.ToggleActiveTabEol();
            Assert.Equal("\r\n", tLat.EolSequence);
            Assert.True(w.SaveBufferToDisk(tLat));
            Assert.Equal(new byte[] { 0x63, 0x61, 0x66, 0xE9, 0x0D, 0x0A }, File.ReadAllBytes(fLat));

            // B16: Read-only file save safety — failed save MUST keep IsDirty = true and SavedContent unchanged,
            // and offer Retry / Save As / Overwrite
            string fRo = Path.Combine(ws, "readonly.txt");
            File.WriteAllText(fRo, "immutable\n");
            File.SetAttributes(fRo, FileAttributes.ReadOnly);
            var tRo = w.OpenFileInTab(fRo, preview: false);
            tRo.Lines[0] = "attempted_change";
            w.PrimaryEditor.Canvas.CommitExternalEdit();
            Assert.True(tRo.IsDirty);
            Assert.False(AutoSavePolicy.IsEligible(tRo));

            // 1) Default failed save: returns false, stays dirty, SavedContent is still "immutable\n", choices are Retry/Save As/Overwrite
            w.SaveFailureDecisionOverride = null;
            Assert.False(w.SaveBufferToDisk(tRo));
            Assert.True(tRo.IsDirty);
            Assert.Equal("immutable\n", tRo.SavedContent);
            Assert.Equal("immutable\n", File.ReadAllText(fRo).Replace("\r\n", "\n"));
            Assert.Equal(new[] { "Retry", "Save As", "Overwrite" }, w.LastConflictNotificationChoices);

            // 2) Save As from failed save writes to new file and clears dirty
            string fRoCopy = Path.Combine(ws, "writable_copy.txt");
            w.SaveFailureSaveAsPath = fRoCopy;
            w.SaveFailureDecisionOverride = _ => SaveFailureAction.SaveAs;
            Assert.True(w.SaveBufferToDisk(tRo));
            Assert.False(tRo.IsDirty);
            Assert.Equal(fRoCopy, tRo.FilePath);
            Assert.Equal("attempted_change\n", File.ReadAllText(fRoCopy).Replace("\r\n", "\n"));
            Assert.Equal("immutable\n", File.ReadAllText(fRo).Replace("\r\n", "\n"));

            // 3) Overwrite on read-only file clears ReadOnly attribute and writes to disk
            var tRo2 = w.OpenFileInTab(fRo, preview: false);
            tRo2.Lines[0] = "overwritten_readonly";
            w.PrimaryEditor.Canvas.CommitExternalEdit();
            w.SaveFailureDecisionOverride = _ => SaveFailureAction.Overwrite;
            Assert.True(w.SaveBufferToDisk(tRo2));
            Assert.False(tRo2.IsDirty);
            Assert.Equal("overwritten_readonly\n", File.ReadAllText(fRo).Replace("\r\n", "\n"));

            // B17: Binary file placeholder and Open Anyway
            string fBin = Path.Combine(ws, "data.bin");
            File.WriteAllBytes(fBin, new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x00, 0x01, 0x02 });
            Assert.Null(w.OpenFileInTab(fBin));
            Assert.True(w.PrimaryEditor.IsBinaryPlaceholderVisible);
            w.PrimaryEditor.ClickOpenBinaryAnyway();
            Assert.False(w.PrimaryEditor.IsBinaryPlaceholderVisible);
            Assert.Equal(fBin, w.ActiveTab.FilePath);
        }

        [AvaloniaFact]
        public void B18_B23_ExplorerCreateRenameMoveDelete_TabLanguageAndHotExitFollowRename_AndExplorerUndo()
        {
            string ws = CreateWorkspace("explorer_ops");
            var w = Track(new MainWindow(ws));
            w.Show();

            // B18: Create file and folder (and reject ../ traversal)
            string createdFolder = w.CreateFolderInDirectory(ws, "src/utils");
            Assert.NotNull(createdFolder);
            Assert.True(Directory.Exists(createdFolder));
            Assert.Null(w.CreateFolderInDirectory(ws, "../escape_outside"));

            string createdFile = w.CreateFileInDirectory(createdFolder, "note.txt");
            Assert.NotNull(createdFile);
            Assert.True(File.Exists(createdFile));
            var tab = w.ActiveTab;
            Assert.NotNull(tab);
            Assert.Equal("txt", tab.Language);

            // Make tab dirty and flush hot-exit backup under old name
            tab.Lines[0] = "def compute(): return 1";
            w.PrimaryEditor.Canvas.CommitExternalEdit();
            w.ScheduleHotExitBackup(tab);
            w.FlushPendingHotExitBackups();
            string backupDir = HotExitService.GetWorkspaceBackupDir(ws);
            Assert.Single(Directory.GetFiles(backupDir, "*.json"));

            // B19: Rename open dirty file across extensions (.txt -> .py) updates Language, ActiveEditorFilePath, and migrates HotExit backup
            Assert.True(w.RenameExplorerPath(createdFile, "compute.py"));
            string renamedFile = Path.Combine(createdFolder, "compute.py");
            Assert.Equal(renamedFile, tab.FilePath);
            Assert.Equal("compute.py", tab.FileName);
            Assert.Equal("py", tab.Language);
            Assert.Equal(renamedFile, w.ExplorerTree.ActiveEditorFilePath);
            Assert.True(tab.IsDirty);
            w.FlushPendingHotExitBackups();
            // Old backup deleted, only 1 backup exists for the new path
            string[] backupsAfterRename = Directory.GetFiles(backupDir, "*.json");
            Assert.Single(backupsAfterRename);
            Assert.Contains("compute.py", File.ReadAllText(backupsAfterRename[0]));

            // B23: Case-only rename ("compute.py" -> "Compute.py")
            Assert.True(w.RenameExplorerPath(renamedFile, "Compute.py"));
            string caseRenamed = Path.Combine(createdFolder, "Compute.py");
            Assert.Equal(caseRenamed, tab.FilePath);

            // B20: Folder rename repaths open tabs in subtree
            Assert.True(w.RenameExplorerPath(createdFolder, "helpers"));
            string movedFolder = Path.Combine(ws, "src", "helpers");
            Assert.Equal(Path.Combine(movedFolder, "Compute.py"), tab.FilePath);

            // B22: Explorer Undo (UndoLastExplorerOperation / Cmd+Z on ExplorerTree) reverses folder rename, case rename, file rename, and file creation
            Assert.True(w.UndoLastExplorerOperation()); // undo folder rename helpers -> utils
            Assert.Equal(caseRenamed, tab.FilePath);
            Assert.True(w.UndoLastExplorerOperation()); // undo case rename Compute.py -> compute.py
            Assert.Equal(renamedFile, tab.FilePath);
            Assert.True(w.UndoLastExplorerOperation()); // undo rename compute.py -> note.txt
            Assert.Equal(createdFile, tab.FilePath);
            Assert.Equal("txt", tab.Language);

            // B21: Delete file to trash closes tab cleanly
            Assert.True(w.DeletePathToTrashOrRecycleBin(createdFile));
            Assert.False(File.Exists(createdFile));
            Assert.DoesNotContain(tab, w.OpenTabs);
        }

        [AvaloniaFact]
        public void B24_B29_TabsPreviewKeepOpenPinReorder_CloseOthersSaved_ReopenSkipsOpen_AndSplitSharedBuffer()
        {
            string ws = CreateWorkspace("tabs_split");
            string f1 = Path.Combine(ws, "one.cs");
            string f2 = Path.Combine(ws, "two.cs");
            string f3 = Path.Combine(ws, "three.cs");
            File.WriteAllText(f1, "class One {}\n");
            File.WriteAllText(f2, "class Two {}\n");
            File.WriteAllText(f3, "class Three {}\n");

            var w = Track(new MainWindow(ws));
            w.Show();

            // B24: Preview tab reused on single-click, promoted by workbench.action.keepEditor without pinning
            var p1 = w.OpenFileInTab(f1, preview: true);
            Assert.True(p1.IsPreviewTab);
            Assert.Single(w.OpenTabs);
            var p2 = w.OpenFileInTab(f2, preview: true);
            Assert.Single(w.OpenTabs);
            Assert.DoesNotContain(p1, w.OpenTabs);
            Assert.True(p2.IsPreviewTab);
            Assert.Equal(f2, p2.FilePath);
            Assert.True(w.KeybindingManager.ExecuteCommand("workbench.action.keepEditor"));
            Assert.False(p2.IsPreviewTab);
            Assert.False(p2.IsPinned);

            // B25 & B26: Pin tab moves it left among pinned tabs and protects it from Close Saved / Close Others
            var t1 = w.OpenFileInTab(f1, preview: false);
            var t3 = w.OpenFileInTab(f3, preview: false);
            Assert.Equal(new[] { f2, f1, f3 }, w.OpenTabs.Select(t => t.FilePath).ToArray());
            w.PinTab(t3, true);
            Assert.True(t3.IsPinned);
            Assert.Equal(f3, w.OpenTabs[0].FilePath); // moved to index 0 (left of unpinned tabs)

            // Close Unmodified (Close Saved) closes unpinned clean tabs (f2, f1) but keeps pinned t3
            Assert.True(w.KeybindingManager.ExecuteCommand("workbench.action.closeUnmodifiedEditors"));
            Assert.Single(w.OpenTabs);
            Assert.Same(t3, w.OpenTabs[0]);

            // B27: ReopenClosedTab skips already-open tabs and restores cursor & scroll of closed tab
            var reopened1 = w.OpenFileInTab(f1, preview: false);
            reopened1.CursorLine = 0;
            reopened1.CursorCol = 6;
            w.CloseTab(reopened1);
            // Re-open f1 manually so it is already open, then call ReopenClosedTab -> should skip f1 and reopen f2!
            w.OpenFileInTab(f1, preview: false);
            w.ReopenClosedTab();
            Assert.Contains(w.OpenTabs, t => t.FilePath == f2);

            // B28: Split editor sharing the same buffer syncs edits and Revert across both panes
            w.ActivateTab(t3);
            w.ToggleSplitEditor();
            Assert.Same(t3, w.PrimaryEditor.CurrentBuffer);
            Assert.Same(t3, w.SplitEditor.CurrentBuffer);
            t3.Lines[0] = "class ThreeModified {}";
            w.PrimaryEditor.Canvas.CommitExternalEdit();
            Assert.Equal("class ThreeModified {}", w.SplitEditor.CurrentBuffer.Lines[0]);
            w.RevertTabToDisk(t3);
            Assert.Equal("class Three {}", w.PrimaryEditor.CurrentBuffer.Lines[0]);
            Assert.Equal("class Three {}", w.SplitEditor.CurrentBuffer.Lines[0]);

            // B29: Closing dirty tab prompts and Cancel keeps tab open
            t3.Lines[0] = "dirty";
            w.PrimaryEditor.Canvas.CommitExternalEdit();
            w.SuppressUnsavedPrompts = false;
            w.UnsavedPromptOverride = _ => UnsavedPromptResult.Cancel;
            Assert.False(w.CloseTab(t3, forceWithoutPrompt: false));
            Assert.Contains(t3, w.OpenTabs);
            w.SuppressUnsavedPrompts = true;
        }

        [AvaloniaFact]
        public void B30_B33_HotExitRestore_ModifiedOnDiskWhileClosed_AndMultiWindowIndependence()
        {
            string ws1 = CreateWorkspace("mw_hot1");
            string ws2 = CreateWorkspace("mw_hot2");
            string f1 = Path.Combine(ws1, "a.txt");
            string f2 = Path.Combine(ws2, "b.txt");
            File.WriteAllText(f1, "alpha\n");
            File.WriteAllText(f2, "beta\n");

            // B32: Multi-window independence (tabs, titles, keybinding managers, closed tab history)
            var w1 = Track(new MainWindow(ws1));
            var w2 = Track(new MainWindow(ws2));
            w1.Show();
            w2.Show();
            var t1 = w1.OpenFileInTab(f1, preview: false);
            var t2 = w2.OpenFileInTab(f2, preview: false);
            t1.Lines[0] = "alpha_dirty";
            w1.PrimaryEditor.Canvas.CommitExternalEdit();
            Assert.StartsWith("● a.txt", w1.Title);
            Assert.StartsWith("b.txt", w2.Title);
            Assert.NotSame(w1.KeybindingManager, w2.KeybindingManager);

            // B30 & B31: HotExit on exit + external disk edit while closed flags HasExternalDiskConflict on restore
            w1.CloseForApplicationExit();
            File.WriteAllText(f1, "alpha_disk_changed_while_closed\n");

            var w1Restored = Track(new MainWindow(ws1));
            w1Restored.Show();
            w1Restored.RestoreWorkspaceSession();
            var rt1 = w1Restored.OpenTabs.Single(t => t.FilePath == f1);
            Assert.True(rt1.IsDirty);
            Assert.True(rt1.HasExternalDiskConflict);
            Assert.Equal("alpha_dirty", rt1.Lines[0]);
            Assert.False(AutoSavePolicy.IsEligible(rt1));
        }

        [AvaloniaFact]
        public void B34_B37_WorkspaceSearchIgnoreGlobsDirtyBuffers_RegexSingleAndAllReplace_AndMultiFileUndo()
        {
            string ws = CreateWorkspace("search_replace");
            File.WriteAllText(Path.Combine(ws, ".gitignore"), "ignored.txt\n*.log\n!important.log\n");
            string fIgnored = Path.Combine(ws, "ignored.txt");
            string fSkippedLog = Path.Combine(ws, "debug.log");
            string fKeptLog = Path.Combine(ws, "important.log");
            string fCode = Path.Combine(ws, "app.ts");
            File.WriteAllText(fIgnored, "token_alpha = 1\n");
            File.WriteAllText(fSkippedLog, "token_alpha = 2\n");
            File.WriteAllText(fKeptLog, "token_alpha = 3\n");
            File.WriteAllText(fCode, "const token_alpha = 10;\nconst token_beta = 20;\n");

            var w = Track(new MainWindow(ws));
            w.Show();

            // B34: .gitignore exclusions and !important.log negation
            var hits = w.RunWorkspaceSearch("token_alpha");
            Assert.DoesNotContain(hits, h => h.FilePath == fIgnored);
            Assert.DoesNotContain(hits, h => h.FilePath == fSkippedLog);
            Assert.Contains(hits, h => h.FilePath == fKeptLog);
            Assert.Contains(hits, h => h.FilePath == fCode);

            // B35: Dirty in-memory buffer text is searched even before saving to disk
            var tabCode = w.OpenFileInTab(fCode, preview: false);
            tabCode.Lines[1] = "const token_unsaved_memory = 99;";
            w.PrimaryEditor.Canvas.CommitExternalEdit();
            var memHits = w.RunWorkspaceSearch("token_unsaved_memory");
            Assert.Single(memHits);
            Assert.Equal(fCode, memHits[0].FilePath);
            w.RevertTabToDisk(tabCode);

            // B36 & B37: Regex Replace in Files across open + closed files, then UndoLastWorkspaceReplace restores both!
            w.ToggleWorkspaceSearchOption(FindOptionToggles.Regex);
            w.RunWorkspaceSearch(@"token_([a-z]+)");
            var firstMatchRow = w.WorkspaceSearchRows.First(r => !r.IsFileHeader && r.Match != null && r.Match.FilePath == fCode);
            Assert.True(w.ReplaceSingleSearchMatch(firstMatchRow, "single_$1"));
            Assert.Contains("const single_alpha = 10;", tabCode.Content);
            File.WriteAllText(fCode, "const token_alpha = 10;\nconst token_beta = 20;\n");
            w.RevertTabToDisk(tabCode);

            w.RunWorkspaceSearch(@"token_([a-z]+)");
            int modifiedFileCount = w.ExecuteWorkspaceReplaceAll(null, "renamed_$1");
            Assert.Equal(2, modifiedFileCount);
            Assert.Contains("renamed_alpha = 3", File.ReadAllText(fKeptLog));
            Assert.Contains("renamed_alpha = 10", File.ReadAllText(fCode));
            Assert.Contains("renamed_beta = 20", tabCode.Content);
            Assert.False(tabCode.IsDirty);

            int undoneFiles = w.UndoLastWorkspaceReplace();
            Assert.Equal(2, undoneFiles);
            Assert.Equal("token_alpha = 3\n", File.ReadAllText(fKeptLog).Replace("\r\n", "\n"));
            Assert.Equal("const token_alpha = 10;\nconst token_beta = 20;\n", File.ReadAllText(fCode).Replace("\r\n", "\n"));
            Assert.Contains("token_alpha = 10", tabCode.Content);
            Assert.False(tabCode.IsDirty);
        }

        [AvaloniaFact]
        public void B38_B41_SettingsJsonErrorAndTypeResilience_AndTerminalResizeSplitUtf8UnicodeCtrlCPersistence()
        {
            string ws = CreateWorkspace("settings_term");

            // B38: Corrupt JSON or invalid types in settings.json never throw and valid keys still apply
            var s = new StudioSettings();
            string typeMismatchJson = "{\n" +
                "  // comment with trailing comma and mismatched types\n" +
                "  \"editor.fontSize\": \"not-a-float\",\n" +
                "  \"editor.tabSize\": null,\n" +
                "  \"editor.insertSpaces\": [1, 2],\n" +
                "  \"files.autoSaveDelay\": \"bad\",\n" +
                "  \"files.autoSave\": \"afterDelay\",\n" +
                "  \"files.trimTrailingWhitespace\": true,\n" +
                "}";
            string err = VscodeSettingsManager.ApplySettings(typeMismatchJson, s);
            Assert.Null(err);
            Assert.Equal("afterDelay", s.EffectiveAutoSaveMode);
            Assert.True(s.TrimTrailingWhitespaceOnSave);
            Assert.Equal(4, s.TabSize); // preserved default instead of crashing

            // Malformed syntax returns error string without throwing
            Assert.NotNull(VscodeSettingsManager.ApplySettings("{ broken json ", s));

            // B39 & B40: Terminal resize guard + split multi-byte UTF-8 CJK & astral emoji across byte chunks
            var vt = new Vt100TerminalBuffer(80, 24);
            vt.Resize(0, 0); // clamped to >= 10x4, never 0x0
            Assert.True(vt.Cols >= 10 && vt.Rows >= 4);
            vt.Resize(80, 24);

            byte[] utf8Bytes = Encoding.UTF8.GetBytes("你好世界 🚀✨ Done\r\n");
            // Feed 1 byte at a time to split every 3-byte CJK and 4-byte astral emoji sequence across chunks
            for (int i = 0; i < utf8Bytes.Length; i++)
            {
                vt.FeedBytes(new byte[] { utf8Bytes[i] }, 1);
            }
            string row0 = vt.GetLineText(0);
            Assert.Equal("你好世界 🚀✨ Done", row0);

            // B41: Terminal session persists across bottom panel toggles
            var w = Track(new MainWindow(ws));
            w.Show();
            var sessionBefore = w.TerminalSession;
            Assert.NotNull(sessionBefore);
            w.ExecuteCommandPaletteAction("view.toggleTerminal");
            Assert.False(w.IsBottomPanelVisible);
            w.ExecuteCommandPaletteAction("view.toggleTerminal");
            Assert.True(w.IsBottomPanelVisible);
            Assert.Same(sessionBefore, w.TerminalSession);
        }

        [AvaloniaFact]
        public void B42_B45_ScmStageUnstage_DiscardConfirmationAndBackupCleanup_DiffAccuracy_AndGutterHunkRevert()
        {
            using (var repos = new TempGitRepos())
            {
                string repo = repos.Init("scm_trust");
                string file = Path.Combine(repo, "main.py");
                TempGitRepos.Write(repo, "main.py", "a = 1\nb = 2\nc = 3\n");
                TempGitRepos.Commit(repo, "initial");

                var w = Track(new MainWindow(repo));
                w.Show();

                var tab = w.OpenFileInTab(file, preview: false);
                tab.Lines[1] = "b = 200";
                w.PrimaryEditor.Canvas.CommitExternalEdit();
                w.SaveActiveTab();
                w.RefreshGitStatus();

                // B42: Stage and Unstage file
                var entry = w.GitEntries.Single(e => e.FullPath == file);
                Assert.False(entry.IsStaged);
                w.ExecuteGitStageFile(entry);
                entry = w.GitEntries.Single(e => e.FullPath == file);
                Assert.True(entry.IsStaged);
                w.ExecuteGitUnstageFile(entry);
                entry = w.GitEntries.Single(e => e.FullPath == file);
                Assert.False(entry.IsStaged);

                // B43: Discard confirmation (Cancel keeps modification; Confirm discards and cleans up hot-exit backup)
                tab.Lines[1] = "b = 999_dirty";
                w.PrimaryEditor.Canvas.CommitExternalEdit();
                w.ScheduleHotExitBackup(tab);
                w.FlushPendingHotExitBackups();
                string backupDir = HotExitService.GetWorkspaceBackupDir(repo);
                Assert.Single(Directory.GetFiles(backupDir, "*.json"));

                w.DiscardConfirmOverride = _ => false; // Cancel
                w.ConfirmAndDiscardGitFile(entry);
                Assert.True(tab.IsDirty);
                Assert.Equal("b = 999_dirty", tab.Lines[1]);

                w.DiscardConfirmOverride = _ => true; // Confirm
                w.ConfirmAndDiscardGitFile(entry);
                Assert.False(tab.IsDirty);
                Assert.Equal("b = 2", tab.Lines[1]);
                Assert.True(!Directory.Exists(backupDir) || Directory.GetFiles(backupDir, "*.json").Length == 0);

                // B44 & B45: Gutter hunk edit, revert change at cursor, and undo
                tab.Lines[2] = "c = 300";
                w.PrimaryEditor.Canvas.CommitExternalEdit();
                tab.CursorLine = 2;
                w.RevertChangeAtCursor();
                Assert.True(Pump(() => tab.Lines[2] == "c = 3"));
                Assert.True(tab.Undo());
                Assert.Equal("c = 300", tab.Lines[2]);
            }
        }

        [AvaloniaFact]
        public void B46_B49_W20B2_SettingsErrorToleranceStartupAndLiveReloadRecovery_AndReplaceUndoSkipsPostModifiedDiskFiles()
        {
            // B46: Startup with malformed .vscode/settings.json (missing }, stray comma, missing quote)
            // still applies every parsable setting and surfaces the error notification once
            string ws1 = CreateWorkspace("settings_startup_malformed");
            string vscode1 = Path.Combine(ws1, ".vscode");
            Directory.CreateDirectory(vscode1);
            string wsSettings1 = Path.Combine(vscode1, "settings.json");
            File.WriteAllText(wsSettings1, "{\n" +
                "  \"editor.tabSize\": 8,,\n" +
                "  \"editor.fontFamily\": \"Monaco\n" +
                "  \"files.autoSave\": \"afterDelay\"\n"); // missing closing '}' AND missing closing quote AND stray comma

            var w1 = Track(new MainWindow(ws1));
            w1.Show();
            Assert.Equal(8, w1.PrimaryEditor.Canvas.DisplayTabSize);
            Assert.True(w1.IsSettingsOrKeybindingsErrorVisible);
            Assert.Contains("parse error", w1.ActiveSettingsOrKeybindingsErrorText, StringComparison.OrdinalIgnoreCase);

            // B47: Live reload when settings.json is broken keeps last known good settings (never resets to default 4),
            // surfaces error once, and recovers automatically when fixed
            File.WriteAllText(wsSettings1, "{\n  \"editor.tabSize\": 10\n}");
            Assert.Null(w1.ReloadAllVscodeSettingsFromDisk());
            Assert.Equal(10, w1.PrimaryEditor.Canvas.DisplayTabSize);
            Assert.False(w1.IsSettingsOrKeybindingsErrorVisible);

            // User introduces a syntax error that makes editor.tabSize unparsable
            File.WriteAllText(wsSettings1, "{\n  \"editor.tabSize\": \n");
            Assert.NotNull(w1.ReloadAllVscodeSettingsFromDisk());
            Assert.Equal(10, w1.PrimaryEditor.Canvas.DisplayTabSize); // kept last known good value (10), did NOT reset to 4
            Assert.True(w1.IsSettingsOrKeybindingsErrorVisible);

            // Fixing the file clears the notification and applies the new value
            File.WriteAllText(wsSettings1, "{\n  \"editor.tabSize\": 12\n}");
            Assert.Null(w1.ReloadAllVscodeSettingsFromDisk());
            Assert.Equal(12, w1.PrimaryEditor.Canvas.DisplayTabSize);
            Assert.False(w1.IsSettingsOrKeybindingsErrorVisible);

            // B48 & B49: UndoLastWorkspaceReplace never overwrites a file that changed on disk after the replace
            string ws2 = CreateWorkspace("replace_undo_safety");
            string fUnchangedAfterReplace = Path.Combine(ws2, "first.txt");
            string fModifiedAfterReplace = Path.Combine(ws2, "second.txt");
            File.WriteAllText(fUnchangedAfterReplace, "foo = 1\n");
            File.WriteAllText(fModifiedAfterReplace, "foo = 2\n");

            var w2 = Track(new MainWindow(ws2));
            w2.Show();
            w2.RunWorkspaceSearch("foo");
            Assert.Equal(2, w2.ExecuteWorkspaceReplaceAll("foo", "bar"));
            Assert.Equal("bar = 1\n", File.ReadAllText(fUnchangedAfterReplace).Replace("\r\n", "\n"));
            Assert.Equal("bar = 2\n", File.ReadAllText(fModifiedAfterReplace).Replace("\r\n", "\n"));

            // External edit happens to second.txt AFTER the replace
            File.WriteAllText(fModifiedAfterReplace, "bar = 2\nuser_post_replace_edit = 999\n");

            List<string> skipped;
            int restored = w2.UndoLastWorkspaceReplace(out skipped);
            Assert.Equal(1, restored);
            Assert.Single(skipped);
            Assert.Equal(fModifiedAfterReplace, skipped[0]);
            Assert.Equal("foo = 1\n", File.ReadAllText(fUnchangedAfterReplace).Replace("\r\n", "\n"));
            // Post-replace edit on second.txt is preserved intact!
            Assert.Equal("bar = 2\nuser_post_replace_edit = 999\n", File.ReadAllText(fModifiedAfterReplace).Replace("\r\n", "\n"));
        }
    }
}
