using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using codeLess.Studio;

namespace codeLess.AvaloniaApp
{
    /// <summary>
    /// Large-file mode: files over the configured size (default 50 MB) or estimated line count (default 2M) open
    /// instantly in the paged, read-only AvaloniaLargeFileViewer instead of being loaded into an EditorTabBuffer.
    /// </summary>
    public partial class MainWindow
    {
        private string largeFileEditBypassPath;
        private readonly List<EditorTabBuffer> largeFileTabs = new List<EditorTabBuffer>();
        private bool largeFileHooksWired;

        /// <summary>Open tabs currently shown in the large-file viewer (tests / diagnostics).</summary>
        public IReadOnlyList<EditorTabBuffer> LargeFileTabs => largeFileTabs;

        /// <summary>
        /// Called from OpenFileInTab before the file is read. Returns true when the request was handled here (tab is the
        /// viewer tab, or null if opening failed); false to continue with the normal editor.
        /// </summary>
        private bool TryOpenLargeFileTab(string fullPath, bool preview, out EditorTabBuffer tab)
        {
            tab = null;
            if (largeFileEditBypassPath != null && string.Equals(largeFileEditBypassPath, fullPath, PathKeyComparison))
            {
                largeFileEditBypassPath = null;
                return false;
            }
            if (!File.Exists(fullPath) || !LargeFilePolicy.ShouldUseViewer(fullPath, settings)) return false;

            EnsureLargeFileHooks();
            LargeFileDocument doc;
            try
            {
                doc = LargeFileDocument.Open(fullPath);
            }
            catch (Exception ex)
            {
                SetStatusMessage($"Could not open '{Path.GetFileName(fullPath)}': {ex.Message}");
                return true;
            }

            tab = new EditorTabBuffer
            {
                FilePath = fullPath,
                RelativePath = !string.IsNullOrEmpty(workspaceRoot) ? LlmHarnessAndAgentEngine.GetRelativePath(workspaceRoot, fullPath) : Path.GetFileName(fullPath),
                FileName = Path.GetFileName(fullPath),
                Language = "plaintext",
                Content = "",
                SavedContent = "",
                FileEncoding = doc.TextEncoding,
                HasBom = doc.BomLength > 0,
                WordWrap = false,
                IsDirty = false,
                IsPreviewTab = preview,
                LargeDocument = doc
            };
            tab.SyncLinesFromContent();
            largeFileTabs.Add(tab);
            openTabs.Add(tab);
            settings.AddRecentFile(fullPath);
            long editLimit = LargeFilePolicy.EditLimitBytes(settings);
            PrimaryEditor.LargeFileEditLimitBytes = editLimit;
            SplitEditor.LargeFileEditLimitBytes = editLimit;
            ActivateTab(tab);
            SetStatusMessage($"{tab.FileName} ({LargeFilePolicy.FormatBytes(doc.FileLength)}) opened in the large-file viewer: read-only, indexing in the background.");
            return true;
        }

        private void EnsureLargeFileHooks()
        {
            if (largeFileHooksWired) return;
            largeFileHooksWired = true;
            foreach (var ed in new[] { PrimaryEditor, SplitEditor })
            {
                if (ed == null) continue;
                ed.LargeFileOpenInEditorRequested += OpenLargeFileInNormalEditor;
                ed.LargeFileStatusMessage += SetStatusMessage;
            }
        }

        /// <summary>Disposes viewer documents whose tabs were closed (file handles, index memory).</summary>
        private void SweepClosedLargeFileTabs()
        {
            if (largeFileTabs.Count == 0) return;
            for (int i = largeFileTabs.Count - 1; i >= 0; i--)
            {
                var t = largeFileTabs[i];
                if (openTabs.Contains(t)) continue;
                largeFileTabs.RemoveAt(i);
                try { t.LargeDocument?.Dispose(); } catch { }
            }
        }

        /// <summary>"Open in Editor" from the viewer banner: reload the file into the normal editor if under the edit limit.</summary>
        public void OpenLargeFileInNormalEditor(string path)
        {
            if (string.IsNullOrEmpty(path) || !File.Exists(path)) return;
            long len = new FileInfo(path).Length;
            long limit = LargeFilePolicy.EditLimitBytes(settings);
            if (len > limit)
            {
                SetStatusMessage($"'{Path.GetFileName(path)}' is {LargeFilePolicy.FormatBytes(len)}; editing is limited to {LargeFilePolicy.FormatBytes(limit)} (LargeFileEditLimitMB in settings.json).");
                return;
            }
            var viewerTab = openTabs.FirstOrDefault(t => t.IsLargeFileViewer && string.Equals(t.FilePath, path, PathKeyComparison));
            if (viewerTab != null) CloseTab(viewerTab, forceWithoutPrompt: true);
            largeFileEditBypassPath = Path.GetFullPath(path);
            SetStatusMessage($"Loading {Path.GetFileName(path)} ({LargeFilePolicy.FormatBytes(len)}) into the editor…");
            var tab = OpenFileInTab(path, preview: false);
            largeFileEditBypassPath = null;
            if (tab != null) SetStatusMessage($"{tab.FileName} loaded for editing ({LargeFilePolicy.FormatBytes(len)}).");
        }
    }
}
