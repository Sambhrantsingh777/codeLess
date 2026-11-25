using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Avalonia.Headless.XUnit;
using codeLess.AvaloniaApp;
using codeLess.Core;
using codeLess.Studio;
using Xunit;

namespace codeLess.Avalonia.Tests
{
    [Collection("SequentialWorkbench")]
    public class FilesHotExitParityW10Tests : IDisposable
    {
        private readonly string _tempRoot;
        private readonly string _prevSettingsDir;
        private readonly List<MainWindow> _windows = new List<MainWindow>();

        public FilesHotExitParityW10Tests()
        {
            _prevSettingsDir = Environment.GetEnvironmentVariable("CODELESS_SETTINGS_DIR");
            _tempRoot = Path.Combine(Path.GetTempPath(), "codeLess_W10_" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(_tempRoot);
            string settingsDir = Path.Combine(_tempRoot, "settings");
            Directory.CreateDirectory(settingsDir);
            Environment.SetEnvironmentVariable("CODELESS_SETTINGS_DIR", settingsDir);
        }

        public void Dispose()
        {
            foreach (var w in _windows)
            {
                try
                {
                    w.SuppressUnsavedPrompts = true;
                    w.Close();
                }
                catch { }
            }
            _windows.Clear();
            Environment.SetEnvironmentVariable("CODELESS_SETTINGS_DIR", _prevSettingsDir);
            try
            {
                if (Directory.Exists(_tempRoot))
                    Directory.Delete(_tempRoot, true);
            }
            catch { }
        }

        private MainWindow Track(MainWindow w)
        {
            _windows.Add(w);
            return w;
        }

        [AvaloniaFact]
        public void HotExit_OnExit_And_OnExitAndWindowClose_BackupsAndRestoresDirtyAndUntitledTabs_WithCursorSelectionAndDebouncedTimer()
        {
            string ws = Path.Combine(_tempRoot, "ws1");
            Directory.CreateDirectory(ws);
            string fileA = Path.Combine(ws, "app.cs");
            File.WriteAllText(fileA, "line0\nline1\nline2\n");

            var settings = IdePersistentSettings.Load();
            Assert.Equal("onExit", settings.EffectiveHotExit);
            VscodeSettingsManager.ApplySettings("{ \"files.hotExit\": \"onExitAndWindowClose\" }", settings);
            Assert.Equal("onExitAndWindowClose", settings.EffectiveHotExit);
            settings.Save();

            var w1 = Track(new MainWindow(ws));
            w1.Show();
            Assert.False(w1.IsHotExitTimerRunning);

            var tabA = w1.OpenFileInTab(fileA);
            Assert.NotNull(tabA);
            w1.PrimaryEditor.Canvas.Focus();
            tabA.Lines[1] = "line1_dirty_edit";
            tabA.CursorLine = 1;
            tabA.CursorCol = 5;
            tabA.SelectionAnchorLine = 1;
            tabA.SelectionAnchorCol = 0;
            tabA.ScrollTopLine = 1;
            tabA.SyncContentFromLines();
            w1.ScheduleHotExitBackup(tabA);

            var untitled = w1.CreateNewUntitledTab();
            untitled.Lines[0] = "unsaved untitled note";
            untitled.CursorLine = 0;
            untitled.CursorCol = 7;
            untitled.SelectionAnchorLine = 0;
            untitled.SelectionAnchorCol = 0;
            untitled.SyncContentFromLines();
            w1.ScheduleHotExitBackup(untitled);

            // Single shared debounced timer is armed while dirty, and stops once flushed
            Assert.True(w1.IsHotExitTimerRunning);
            w1.FlushPendingHotExitBackups();
            Assert.False(w1.IsHotExitTimerRunning);

            string backupDir = HotExitService.GetWorkspaceBackupDir(ws);
            Assert.True(Directory.Exists(backupDir));
            Assert.Equal(2, Directory.GetFiles(backupDir, "*.json").Length);

            // With onExitAndWindowClose, closing the window does NOT prompt and preserves backups
            int prompts = 0;
            w1.UnsavedPromptOverride = _ => { prompts++; return UnsavedPromptResult.Cancel; };
            w1.Close();
            Assert.Equal(0, prompts);

            // Next launch / window restore brings back both dirty file tab and untitled tab with cursor & selection
            var w2 = Track(new MainWindow(ws));
            w2.Show();
            w2.RestoreWorkspaceSession();

            var restoredA = w2.OpenTabs.FirstOrDefault(t => string.Equals(t.FilePath, fileA, StringComparison.OrdinalIgnoreCase));
            Assert.NotNull(restoredA);
            Assert.True(restoredA.IsDirty);
            Assert.Contains("line1_dirty_edit", restoredA.Content);
            Assert.Equal(1, restoredA.CursorLine);
            Assert.Equal(5, restoredA.CursorCol);
            Assert.Equal(1, restoredA.SelectionAnchorLine);
            Assert.Equal(0, restoredA.SelectionAnchorCol);
            Assert.Equal(1, restoredA.ScrollTopLine);

            var restoredUntitled = w2.OpenTabs.FirstOrDefault(t => t.IsUntitled);
            Assert.NotNull(restoredUntitled);
            Assert.True(restoredUntitled.IsDirty);
            Assert.Equal("unsaved untitled note", restoredUntitled.Lines[0]);
            Assert.Equal(0, restoredUntitled.CursorLine);
            Assert.Equal(7, restoredUntitled.CursorCol);

            // Saving deletes the file tab's backup; reverting/closing untitled with Don't Save deletes its backup
            Assert.True(w2.SaveBufferToDisk(restoredA));
            Assert.False(restoredA.IsDirty);
            Assert.Single(Directory.GetFiles(backupDir, "*.json"));

            w2.UnsavedPromptOverride = _ => UnsavedPromptResult.DontSave;
            Assert.True(w2.CloseTab(restoredUntitled, forceWithoutPrompt: false));
            Assert.True(!Directory.Exists(backupDir) || Directory.GetFiles(backupDir, "*.json").Length == 0);

            // When hotExit is "off", dirty close prompts as before
            VscodeSettingsManager.ApplySettings("{ \"files.hotExit\": \"off\" }", w2.Settings);
            restoredA.Lines[0] = "dirty_again";
            restoredA.SyncContentFromLines();
            int offPrompts = 0;
            w2.UnsavedPromptOverride = _ => { offPrompts++; return UnsavedPromptResult.Cancel; };
            w2.CloseForApplicationExit();
            Assert.Equal(1, offPrompts);
        }

        [AvaloniaFact]
        public void HotExit_RestoreWhenDiskModifiedSinceBackup_MarksConflict_AndSaveTriggersConflictFlow()
        {
            string ws = Path.Combine(_tempRoot, "ws_conflict");
            Directory.CreateDirectory(ws);
            string file = Path.Combine(ws, "data.txt");
            File.WriteAllText(file, "original\n");

            var w1 = Track(new MainWindow(ws));
            w1.Show();
            var tab = w1.OpenFileInTab(file);
            tab.Lines[0] = "my_backed_up_edit";
            tab.SyncContentFromLines();
            w1.CloseForApplicationExit(); // default hotExit = onExit

            // Modify the file on disk while the IDE is closed
            File.WriteAllText(file, "external_disk_change\n");

            var w2 = Track(new MainWindow(ws));
            w2.Show();
            w2.RestoreWorkspaceSession();

            var restored = w2.OpenTabs.Single(t => string.Equals(t.FilePath, file, StringComparison.OrdinalIgnoreCase));
            Assert.True(restored.IsDirty);
            Assert.True(restored.HasExternalDiskConflict);
            Assert.Contains("my_backed_up_edit", restored.Content);

            // Attempting to save triggers the save conflict flow ("Save As", "Overwrite", "Compare")
            w2.SaveConflictDecisionOverride = _ => ExternalConflictAction.Compare;
            Assert.False(w2.SaveBufferToDisk(restored));
            Assert.Equal(new[] { "Save As", "Overwrite", "Compare" }, w2.LastConflictNotificationChoices);
            Assert.Equal("external_disk_change\n", File.ReadAllText(file).Replace("\r\n", "\n"));

            // Save As writes to new path and leaves external file untouched
            string saveAsTarget = Path.Combine(ws, "data_fork.txt");
            w2.SaveConflictSaveAsPath = saveAsTarget;
            w2.SaveConflictDecisionOverride = _ => ExternalConflictAction.SaveAs;
            Assert.True(w2.SaveBufferToDisk(restored));
            Assert.False(restored.IsDirty);
            Assert.Contains("my_backed_up_edit", File.ReadAllText(saveAsTarget));
            Assert.Equal("external_disk_change\n", File.ReadAllText(file).Replace("\r\n", "\n"));
        }

        [AvaloniaFact]
        public void ExternalFileChanges_CleanReloadPreservesCursorAndScroll_DirtyShowsConflict_AndDeletedShowsStrikethrough()
        {
            string ws = Path.Combine(_tempRoot, "ws_ext");
            Directory.CreateDirectory(ws);
            string file = Path.Combine(ws, "module.py");
            File.WriteAllText(file, "a = 1\nb = 2\nc = 3\nd = 4\n");

            var w = Track(new MainWindow(ws));
            w.Show();
            var tab = w.OpenFileInTab(file);
            tab.CursorLine = 2;
            tab.CursorCol = 4;
            tab.ScrollTopLine = 1;
            tab.ScrollLeftCol = 2;

            // 1. Clean buffer reloads silently, keeping cursor and scroll
            File.WriteAllText(file, "a = 10\nb = 20\nc = 30\nd = 40\n");
            w.HandleExternalFileChanged(file);
            Assert.False(tab.IsDirty);
            Assert.Equal("c = 30", tab.Lines[2]);
            Assert.Equal(2, tab.CursorLine);
            Assert.Equal(4, tab.CursorCol);
            Assert.Equal(1, tab.ScrollTopLine);
            Assert.Equal(2, tab.ScrollLeftCol);

            // 2. Dirty buffer shows conflict notification ("Compare", "Overwrite", "Revert")
            tab.Lines[0] = "a = 999";
            tab.SyncContentFromLines();
            Assert.True(tab.IsDirty);

            File.WriteAllText(file, "a = 555\nb = 20\nc = 30\nd = 40\n");
            w.ExternalConflictDecisionOverride = _ => ExternalConflictAction.Compare;
            w.HandleExternalFileChanged(file);
            Assert.True(tab.HasExternalDiskConflict);
            Assert.Equal(new[] { "Compare", "Overwrite", "Revert" }, w.LastConflictNotificationChoices);
            Assert.True(w.PrimaryEditor.IsDiffBannerVisible);

            // Choosing Revert reloads disk content and clears dirty & conflict state
            w.ExternalConflictDecisionOverride = _ => ExternalConflictAction.Revert;
            w.HandleExternalFileChanged(file);
            Assert.False(tab.IsDirty);
            Assert.False(tab.HasExternalDiskConflict);
            Assert.Equal("a = 555", tab.Lines[0]);

            // 3. Deleting the file on disk marks tab "(deleted)" with strikethrough
            File.Delete(file);
            w.HandleExternalFileDeleted(file);
            Assert.True(tab.IsDeletedOnDisk);
            Assert.EndsWith("(deleted)", w.GetTabTitleText(tab));
            Assert.True(w.IsTabTitleStrikethrough(tab));
        }

        [AvaloniaFact]
        public void EncodingsAndEol_Utf8_Utf8Bom_Utf16LeBe_Latin1_AutoGuess_StatusToggle_FilesEol_AndSaveActions()
        {
            string ws = Path.Combine(_tempRoot, "ws_enc");
            Directory.CreateDirectory(ws);

            var w = Track(new MainWindow(ws));
            w.Show();
            VscodeSettingsManager.ApplySettings("{ \"files.autoGuessEncoding\": true, \"files.eol\": \"\\n\", \"files.trimTrailingWhitespace\": true, \"files.insertFinalNewline\": true }", w.Settings);

            // 1. UTF-8 with BOM
            string fUtf8Bom = Path.Combine(ws, "utf8bom.txt");
            File.WriteAllBytes(fUtf8Bom, new byte[] { 0xEF, 0xBB, 0xBF, (byte)'h', (byte)'i', (byte)'\r', (byte)'\n' });
            var tBom = w.OpenFileInTab(fUtf8Bom);
            Assert.True(tBom.HasBom);
            Assert.Equal("\r\n", tBom.EolSequence);
            Assert.Equal("UTF-8 with BOM", w.StatusBarEncodingText);
            Assert.Equal("CRLF", w.StatusBarEolText);

            // 2. UTF-16 LE & UTF-16 BE round-trip on save
            string fUtf16Le = Path.Combine(ws, "utf16le.txt");
            File.WriteAllText(fUtf16Le, "hello\r\nworld   ", new UnicodeEncoding(false, true));
            var t16Le = w.OpenFileInTab(fUtf16Le);
            Assert.Equal(1200, t16Le.FileEncoding.CodePage);
            Assert.Equal("UTF-16 LE", w.StatusBarEncodingText);
            Assert.True(w.SaveBufferToDisk(t16Le));
            byte[] saved16Le = File.ReadAllBytes(fUtf16Le);
            Assert.Equal(0xFF, saved16Le[0]);
            Assert.Equal(0xFE, saved16Le[1]);
            string decoded16Le = new UnicodeEncoding(false, true).GetString(saved16Le, 2, saved16Le.Length - 2);
            // trimTrailingWhitespace stripped the 3 spaces and insertFinalNewline added \r\n
            Assert.Equal("hello\r\nworld\r\n", decoded16Le);

            string fUtf16Be = Path.Combine(ws, "utf16be.txt");
            File.WriteAllText(fUtf16Be, "alpha\nbeta", new UnicodeEncoding(true, true));
            var t16Be = w.OpenFileInTab(fUtf16Be);
            Assert.Equal(1201, t16Be.FileEncoding.CodePage);
            Assert.Equal("UTF-16 BE", w.StatusBarEncodingText);
            Assert.True(w.SaveBufferToDisk(t16Be));
            byte[] saved16Be = File.ReadAllBytes(fUtf16Be);
            Assert.Equal(0xFE, saved16Be[0]);
            Assert.Equal(0xFF, saved16Be[1]);

            // 3. Latin-1 (ISO-8859-1) with non-UTF-8 bytes (café = 63 61 66 E9)
            string fLatin1 = Path.Combine(ws, "latin1.txt");
            File.WriteAllBytes(fLatin1, new byte[] { 0x63, 0x61, 0x66, 0xE9, 0x0A });
            var tLatin1 = w.OpenFileInTab(fLatin1);
            Assert.Equal(28591, tLatin1.FileEncoding.CodePage);
            Assert.Equal("ISO-8859-1", w.StatusBarEncodingText);
            Assert.Equal("café", tLatin1.Lines[0]);
            Assert.True(w.SaveBufferToDisk(tLatin1));
            Assert.Equal(new byte[] { 0x63, 0x61, 0x66, 0xE9, 0x0A }, File.ReadAllBytes(fLatin1));

            // 4. Status bar EOL toggle and files.eol on new untitled tabs
            Assert.Equal("LF", w.StatusBarEolText);
            w.ToggleActiveTabEol();
            Assert.Equal("CRLF", w.StatusBarEolText);
            Assert.Equal("\r\n", tLatin1.EolSequence);

            VscodeSettingsManager.ApplySettings("{ \"files.eol\": \"\\r\\n\" }", w.Settings);
            var untitledCrlf = w.CreateNewUntitledTab();
            Assert.Equal("\r\n", untitledCrlf.EolSequence);
            VscodeSettingsManager.ApplySettings("{ \"files.eol\": \"\\n\" }", w.Settings);
            var untitledLf = w.CreateNewUntitledTab();
            Assert.Equal("\n", untitledLf.EolSequence);
        }

        [AvaloniaFact]
        public void LargeAndBinaryFiles_MaxMemoryThreshold_LongLineTokenizationGuard_AndBinaryPlaceholderOpenAnyway()
        {
            string ws = Path.Combine(_tempRoot, "ws_large_bin");
            Directory.CreateDirectory(ws);

            var w = Track(new MainWindow(ws));
            w.Show();

            // 1. Binary file shows exact VS Code placeholder text and "Open Anyway" opens it in the editor
            string binFile = Path.Combine(ws, "archive.dat");
            File.WriteAllBytes(binFile, new byte[] { 0x7F, (byte)'E', (byte)'L', (byte)'F', 0x00, 0x01, (byte)'h', (byte)'i' });
            Assert.Null(w.OpenFileInTab(binFile));
            Assert.True(w.PrimaryEditor.IsBinaryPlaceholderVisible);
            Assert.Equal("The file is not displayed in the text editor because it is either binary or uses an unsupported text encoding", w.PrimaryEditor.BinaryPlaceholderText);

            w.PrimaryEditor.ClickOpenBinaryAnyway();
            Assert.False(w.PrimaryEditor.IsBinaryPlaceholderVisible);
            Assert.NotNull(w.ActiveTab);
            Assert.Equal(binFile, w.ActiveTab.FilePath);

            // 2. Very long line (>20,000 chars) disables TextMate tokenization without freezing
            string longLineFile = Path.Combine(ws, "bundle.min.js");
            string longLine = "var x = \"" + new string('a', 25000) + "\";\n";
            File.WriteAllText(longLineFile, longLine);
            var longTab = w.OpenFileInTab(longLineFile);
            Assert.NotNull(longTab);
            var syntax = EditorSyntaxSession.For(longTab);
            syntax.Prepare(longTab);
            Assert.True(syntax.IsTooLongForTextMate(longTab.Lines[0]));

            // 3. File >= files.maxMemoryForLargeFilesMB opens in the streaming LargeFileViewer with empty in-memory Content
            VscodeSettingsManager.ApplySettings("{ \"files.maxMemoryForLargeFilesMB\": 1 }", w.Settings);
            Assert.Equal(1, w.Settings.LargeFileThresholdMB);
            string bigFile = Path.Combine(ws, "big.log");
            byte[] chunk = Encoding.ASCII.GetBytes(new string('x', 1023) + "\n");
            using (var fs = new FileStream(bigFile, FileMode.Create, FileAccess.Write))
            {
                for (int i = 0; i < 1100; i++)
                    fs.Write(chunk, 0, chunk.Length);
            }
            var bigTab = w.OpenFileInTab(bigFile);
            Assert.NotNull(bigTab);
            Assert.True(bigTab.IsLargeFileViewer);
            Assert.True(w.PrimaryEditor.IsShowingLargeFile);
            Assert.Equal("", bigTab.Content); // zero duplicate full-file copy in EditorTabBuffer
        }
    }
}
