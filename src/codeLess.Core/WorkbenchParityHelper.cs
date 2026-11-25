using System;
using System.Collections.Generic;
using System.IO;

namespace codeLess.Studio
{
    public sealed class WorkbenchNotificationEntry
    {
        public string Id { get; set; }
        public string Severity { get; set; }
        public string Message { get; set; }
        public string ActionLabel { get; set; }
        public Action OnAction { get; set; }
        public bool CanDoNotShowAgain { get; set; }
        public DateTime TimestampUtc { get; set; }
    }

    public static class WorkbenchParityHelper
    {
        public static Dictionary<string, string> ComputeDisambiguatedFolders(IEnumerable<EditorTabBuffer> openTabs)
        {
            var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (openTabs == null) return result;

            var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var tabList = new List<EditorTabBuffer>();
            foreach (var t in openTabs)
            {
                if (t == null) continue;
                tabList.Add(t);
                string fn = t.FileName ?? "";
                int c;
                counts[fn] = (counts.TryGetValue(fn, out c) ? c : 0) + 1;
            }

            for (int i = 0; i < tabList.Count; i++)
            {
                var t = tabList[i];
                string fn = t.FileName ?? "";
                int dup;
                if (counts.TryGetValue(fn, out dup) && dup > 1 && !string.IsNullOrEmpty(t.FilePath))
                {
                    string parentDir = Path.GetDirectoryName(t.FilePath.TrimEnd('\\', '/'));
                    if (!string.IsNullOrEmpty(parentDir))
                    {
                        string folderName = Path.GetFileName(parentDir.TrimEnd('\\', '/'));
                        if (!string.IsNullOrEmpty(folderName))
                        {
                            result[t.FilePath] = folderName;
                        }
                    }
                }
            }

            return result;
        }

        public static string GetDisambiguatedFolder(EditorTabBuffer tab, IEnumerable<EditorTabBuffer> openTabs)
        {
            if (tab == null || string.IsNullOrEmpty(tab.FilePath)) return "";
            var map = ComputeDisambiguatedFolders(openTabs);
            string folder;
            return map.TryGetValue(tab.FilePath, out folder) ? folder : "";
        }

        public static string FormatStatusLnCol(int line1, int col1, int selectedCharCount, int cursorCount)
        {
            string text = string.Format("Ln {0}, Col {1}", Math.Max(1, line1), Math.Max(1, col1));
            if (selectedCharCount > 0)
            {
                text += string.Format(" ({0} selected)", selectedCharCount);
            }
            if (cursorCount > 1)
            {
                text += string.Format(" ({0} cursors)", cursorCount);
            }
            return text;
        }

        public static string GetCopyDestinationPath(string sourcePath, string targetDirectory)
        {
            if (string.IsNullOrEmpty(sourcePath) || string.IsNullOrEmpty(targetDirectory))
                return null;

            string fileName = Path.GetFileName(sourcePath.TrimEnd('\\', '/'));
            string candidate = Path.Combine(targetDirectory, fileName);
            if (!File.Exists(candidate) && !Directory.Exists(candidate))
                return candidate;

            bool isDir = Directory.Exists(sourcePath);
            string stem = isDir ? fileName : Path.GetFileNameWithoutExtension(fileName);
            string ext = isDir ? "" : Path.GetExtension(fileName);

            string copy1 = Path.Combine(targetDirectory, stem + " copy" + ext);
            if (!File.Exists(copy1) && !Directory.Exists(copy1))
                return copy1;

            for (int i = 2; i < 1000; i++)
            {
                string copyN = Path.Combine(targetDirectory, string.Format("{0} copy {1}{2}", stem, i, ext));
                if (!File.Exists(copyN) && !Directory.Exists(copyN))
                    return copyN;
            }

            return Path.Combine(targetDirectory, stem + "_" + Guid.NewGuid().ToString("N").Substring(0, 6) + ext);
        }
    }
}
