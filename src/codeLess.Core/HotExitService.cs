using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Web.Script.Serialization;

namespace codeLess.Studio
{
    public enum ExternalConflictAction
    {
        None = 0,
        Compare = 1,
        Overwrite = 2,
        Revert = 3,
        SaveAs = 4
    }

    public enum SaveFailureAction
    {
        None = 0,
        Retry = 1,
        SaveAs = 2,
        Overwrite = 3
    }

    public sealed class HotExitBackupEntry
    {
        public string BackupId { get; set; }
        public string FilePath { get; set; }
        public string FileName { get; set; }
        public string Language { get; set; }
        public bool IsUntitled { get; set; }
        public string Content { get; set; }
        public string SavedContent { get; set; }
        public long DiskMtimeUtcTicks { get; set; }
        public long DiskLength { get; set; }
        public int EncodingCodePage { get; set; }
        public bool HasBom { get; set; }
        public string EolSequence { get; set; }
        public bool HasFinalNewline { get; set; }
        public int CursorLine { get; set; }
        public int CursorCol { get; set; }
        public int SelectionAnchorLine { get; set; }
        public int SelectionAnchorCol { get; set; }
        public int ScrollTopLine { get; set; }
        public int ScrollLeftCol { get; set; }
        public int OrderIndex { get; set; }
    }

    /// <summary>
    /// VS Code files.hotExit ("off" | "onExit" | "onExitAndWindowClose") backup and restore service.
    /// Stores atomic per-tab backups under &lt;settingsDir&gt;/Backups/&lt;workspace-hash&gt;/.
    /// Strict C# 5.
    /// </summary>
    public static class HotExitService
    {
        public const int DefaultDebounceDelayMs = 1000;
        public const string BinaryPlaceholderMessage = "The file is not displayed in the text editor because it is either binary or uses an unsupported text encoding";

        public static string GetBackupsRootDir()
        {
            string settingsFile = IdePersistentSettings.GetSettingsFilePath();
            string baseDir = Path.GetDirectoryName(settingsFile);
            if (string.IsNullOrEmpty(baseDir))
            {
                string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
                baseDir = Path.Combine(appData, "codeLess");
            }
            string dir = Path.Combine(baseDir, "Backups");
            if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
            return dir;
        }

        public static string GetWorkspaceBackupDir(string workspacePath)
        {
            string hash = WorkspaceSessionStore.GetWorkspaceHash(workspacePath);
            string dir = Path.Combine(GetBackupsRootDir(), hash);
            return dir;
        }

        public static bool IsEnabled(IdePersistentSettings settings)
        {
            if (settings == null) return true;
            return settings.EffectiveHotExit != IdePersistentSettings.HotExitOff;
        }

        public static bool ShouldBackupWithoutPrompt(IdePersistentSettings settings, bool isApplicationExit)
        {
            string mode = settings != null ? settings.EffectiveHotExit : IdePersistentSettings.HotExitOnExit;
            if (string.Equals(mode, IdePersistentSettings.HotExitOnExitAndWindowClose, StringComparison.OrdinalIgnoreCase))
                return true;
            if (string.Equals(mode, IdePersistentSettings.HotExitOnExit, StringComparison.OrdinalIgnoreCase))
                return isApplicationExit;
            return false;
        }

        public static string GetBackupId(EditorTabBuffer tab)
        {
            if (tab == null) return "unknown";
            if (UntitledFileDefaults.IsUntitledLike(tab))
            {
                string name = string.IsNullOrEmpty(tab.FileName) ? "Untitled-1" : tab.FileName;
                char[] invalid = Path.GetInvalidFileNameChars();
                StringBuilder sb = new StringBuilder("untitled_");
                for (int i = 0; i < name.Length; i++)
                {
                    char c = name[i];
                    if (Array.IndexOf(invalid, c) >= 0 || c == ' ') sb.Append('_');
                    else sb.Append(c);
                }
                return sb.ToString();
            }
            return "file_" + WorkspaceSessionStore.GetWorkspaceHash(tab.FilePath);
        }

        public static string WriteTabBackup(string workspacePath, EditorTabBuffer tab, int orderIndex)
        {
            if (tab == null || !UntitledFileDefaults.NeedsSavePrompt(tab) || tab.IsLargeFileViewer)
                return null;

            string dir = GetWorkspaceBackupDir(workspacePath);
            if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);

            string id = GetBackupId(tab);
            string filePath = Path.Combine(dir, id + ".json");

            HotExitBackupEntry entry = new HotExitBackupEntry();
            entry.BackupId = id;
            entry.FilePath = tab.IsUntitled ? "" : (tab.FilePath ?? "");
            entry.FileName = tab.FileName ?? "Untitled-1";
            entry.Language = tab.Language ?? "txt";
            entry.IsUntitled = UntitledFileDefaults.IsUntitledLike(tab);
            entry.Content = tab.Content ?? "";
            entry.SavedContent = tab.SavedContent ?? "";
            entry.DiskMtimeUtcTicks = tab.DiskLastWriteUtcTicks;
            entry.DiskLength = tab.DiskLastLength;
            entry.EncodingCodePage = tab.FileEncoding != null ? tab.FileEncoding.CodePage : 65001;
            entry.HasBom = tab.HasBom;
            entry.EolSequence = string.IsNullOrEmpty(tab.EolSequence) ? "\n" : tab.EolSequence;
            entry.HasFinalNewline = tab.HasFinalNewline;
            entry.CursorLine = tab.CursorLine;
            entry.CursorCol = tab.CursorCol;
            entry.SelectionAnchorLine = tab.SelectionAnchorLine;
            entry.SelectionAnchorCol = tab.SelectionAnchorCol;
            entry.ScrollTopLine = tab.ScrollTopLine;
            entry.ScrollLeftCol = tab.ScrollLeftCol;
            entry.OrderIndex = orderIndex;

            JavaScriptSerializer ser = new JavaScriptSerializer();
            ser.MaxJsonLength = int.MaxValue;
            string json = ser.Serialize(entry);
            CrossProcessFile.AtomicWrite(filePath, json, false);
            return filePath;
        }

        public static void WriteWorkspaceBackups(string workspacePath, IEnumerable<EditorTabBuffer> openTabs)
        {
            string dir = GetWorkspaceBackupDir(workspacePath);
            HashSet<string> keptFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            int order = 0;
            if (openTabs != null)
            {
                foreach (EditorTabBuffer tab in openTabs)
                {
                    if (tab != null && UntitledFileDefaults.NeedsSavePrompt(tab) && !tab.IsLargeFileViewer)
                    {
                        string written = WriteTabBackup(workspacePath, tab, order++);
                        if (!string.IsNullOrEmpty(written)) keptFiles.Add(Path.GetFullPath(written));
                    }
                }
            }

            if (Directory.Exists(dir))
            {
                try
                {
                    string[] existing = Directory.GetFiles(dir, "*.json");
                    for (int i = 0; i < existing.Length; i++)
                    {
                        string full = Path.GetFullPath(existing[i]);
                        if (!keptFiles.Contains(full))
                        {
                            try { File.Delete(full); } catch { }
                        }
                    }
                    if (keptFiles.Count == 0 && Directory.GetFiles(dir).Length == 0)
                    {
                        try { Directory.Delete(dir, false); } catch { }
                    }
                }
                catch { }
            }
        }

        public static void DeleteBackup(string workspacePath, EditorTabBuffer tab)
        {
            if (tab == null) return;
            string dir = GetWorkspaceBackupDir(workspacePath);
            if (!Directory.Exists(dir)) return;
            string id = GetBackupId(tab);
            string filePath = Path.Combine(dir, id + ".json");
            try
            {
                if (File.Exists(filePath)) File.Delete(filePath);
                if (Directory.Exists(dir) && Directory.GetFiles(dir).Length == 0)
                    Directory.Delete(dir, false);
            }
            catch { }
        }

        public static void ClearWorkspaceBackups(string workspacePath)
        {
            string dir = GetWorkspaceBackupDir(workspacePath);
            if (!Directory.Exists(dir)) return;
            try
            {
                Directory.Delete(dir, true);
            }
            catch { }
        }

        public static List<HotExitBackupEntry> LoadBackups(string workspacePath)
        {
            List<HotExitBackupEntry> result = new List<HotExitBackupEntry>();
            string dir = GetWorkspaceBackupDir(workspacePath);
            if (!Directory.Exists(dir)) return result;

            try
            {
                string[] files = Directory.GetFiles(dir, "*.json");
                JavaScriptSerializer ser = new JavaScriptSerializer();
                ser.MaxJsonLength = int.MaxValue;
                for (int i = 0; i < files.Length; i++)
                {
                    try
                    {
                        string json = File.ReadAllText(files[i]);
                        if (string.IsNullOrWhiteSpace(json)) continue;
                        string parseErr;
                        object parsed = codeLess.Core.JsoncParser.Parse(json, out parseErr);
                        if (parseErr != null || !(parsed is Dictionary<string, object>)) continue;
                        HotExitBackupEntry entry = ser.Deserialize<HotExitBackupEntry>(json);
                        if (entry == null || string.IsNullOrEmpty(entry.BackupId)) continue;
                        if (!entry.IsUntitled && string.IsNullOrEmpty(entry.FilePath)) continue;
                        if (entry.IsUntitled && string.IsNullOrEmpty(entry.FileName) && string.IsNullOrEmpty(entry.Content)) continue;
                        result.Add(entry);
                    }
                    catch { }
                }
                result.Sort(delegate(HotExitBackupEntry a, HotExitBackupEntry b)
                {
                    int cmp = a.OrderIndex.CompareTo(b.OrderIndex);
                    if (cmp != 0) return cmp;
                    return string.CompareOrdinal(a.BackupId ?? "", b.BackupId ?? "");
                });
            }
            catch { }
            return result;
        }

        public static Encoding ResolveEncoding(int codePage, bool hasBom)
        {
            try
            {
                if (codePage == 65001 || codePage <= 0) return new UTF8Encoding(hasBom);
                if (codePage == 1200) return new UnicodeEncoding(false, hasBom);
                if (codePage == 1201) return new UnicodeEncoding(true, hasBom);
                if (codePage == 12000) return new UTF32Encoding(false, hasBom);
                if (codePage == 12001) return new UTF32Encoding(true, hasBom);
                return Encoding.GetEncoding(codePage);
            }
            catch
            {
                return new UTF8Encoding(hasBom);
            }
        }

        public static void ApplyBackupToTab(HotExitBackupEntry entry, EditorTabBuffer tab)
        {
            if (entry == null || tab == null) return;

            bool diskChangedSinceBackup = false;
            bool deletedOnDisk = false;
            if (!entry.IsUntitled && !string.IsNullOrEmpty(entry.FilePath))
            {
                if (!File.Exists(entry.FilePath))
                {
                    deletedOnDisk = true;
                }
                else
                {
                    try
                    {
                        Encoding dEnc;
                        bool dBom;
                        string dEol;
                        bool dFinalNl;
                        string currentDisk = EditorTabBuffer.ReadFilePreservingMetadata(entry.FilePath, out dEnc, out dBom, out dEol, out dFinalNl);
                        string normDisk = (currentDisk ?? "").Replace("\r\n", "\n").Replace("\r", "\n");
                        string normBase = (entry.SavedContent ?? "").Replace("\r\n", "\n").Replace("\r", "\n");
                        if (!string.Equals(normDisk, normBase, StringComparison.Ordinal))
                        {
                            diskChangedSinceBackup = true;
                        }
                        else if (entry.DiskMtimeUtcTicks > 0)
                        {
                            long curTicks = File.GetLastWriteTimeUtc(entry.FilePath).Ticks;
                            if (curTicks != entry.DiskMtimeUtcTicks && !string.Equals(normDisk, normBase, StringComparison.Ordinal))
                                diskChangedSinceBackup = true;
                        }
                    }
                    catch { }
                }
            }

            tab.IsUntitled = entry.IsUntitled;
            tab.FileName = entry.FileName ?? tab.FileName ?? "Untitled-1";
            if (!string.IsNullOrEmpty(entry.Language)) tab.Language = entry.Language;
            tab.FileEncoding = ResolveEncoding(entry.EncodingCodePage, entry.HasBom);
            tab.HasBom = entry.HasBom;
            tab.EolSequence = string.IsNullOrEmpty(entry.EolSequence) ? "\n" : entry.EolSequence;
            tab.HasFinalNewline = entry.HasFinalNewline;
            tab.SavedContent = entry.SavedContent ?? "";
            tab.DiskLastWriteUtcTicks = entry.DiskMtimeUtcTicks;
            tab.DiskLastLength = entry.DiskLength;
            tab.Content = entry.Content ?? "";
            tab.SyncLinesFromContent();
            tab.IsDirty = true;
            tab.IsPreview = false;
            tab.IsDeletedOnDisk = deletedOnDisk;
            tab.HasExternalDiskConflict = diskChangedSinceBackup;

            tab.CursorLine = Math.Max(0, Math.Min(entry.CursorLine, tab.Lines.Count - 1));
            tab.CursorCol = Math.Max(0, Math.Min(entry.CursorCol, tab.Lines[tab.CursorLine].Length));
            if (entry.SelectionAnchorLine >= 0 && entry.SelectionAnchorCol >= 0)
            {
                tab.SelectionAnchorLine = Math.Max(0, Math.Min(entry.SelectionAnchorLine, tab.Lines.Count - 1));
                tab.SelectionAnchorCol = Math.Max(0, Math.Min(entry.SelectionAnchorCol, tab.Lines[tab.SelectionAnchorLine].Length));
            }
            else
            {
                tab.SelectionAnchorLine = -1;
                tab.SelectionAnchorCol = -1;
            }
            tab.ScrollTopLine = Math.Max(0, Math.Min(entry.ScrollTopLine, tab.Lines.Count - 1));
            tab.ScrollLeftCol = Math.Max(0, entry.ScrollLeftCol);
        }
    }
}
