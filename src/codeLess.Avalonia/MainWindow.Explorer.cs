using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using codeLess.Studio;

namespace codeLess.AvaloniaApp
{
    /// <summary>
    /// Explorer behaviour aligned with VS Code: what is hidden vs. dimmed, state-preserving refresh,
    /// file-system watching, and safe file operations (create / rename / trash).
    /// </summary>
    public partial class MainWindow
    {
        // VS Code "files.exclude" defaults. Everything else (node_modules, build, dist, nested repos, gitignored
        // folders, images, ...) stays visible in the Explorer; it is only excluded from search / AI indexing.
        public static readonly string[] ExplorerDefaultHiddenNames = { ".git", ".svn", ".hg", "CVS", ".DS_Store", "Thumbs.db" };

        /// <summary>Path-key comparer: Linux file systems are case-sensitive; Windows and default macOS volumes are not.</summary>
        public static StringComparer PathKeyComparer =>
            RuntimeInformation.IsOSPlatform(OSPlatform.Linux) ? StringComparer.Ordinal : StringComparer.OrdinalIgnoreCase;

        public static StringComparison PathKeyComparison =>
            RuntimeInformation.IsOSPlatform(OSPlatform.Linux) ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;

        private HashSet<string> explorerDimmedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private FileSystemWatcher workspaceWatcher;
        private DispatcherTimer fsChangeDebounceTimer;
        private bool fsTreeDirty;
        private bool fsGitDirty;
        private readonly HashSet<string> fsChangedFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private readonly object fsChangeLock = new object();
        private int reindexGeneration;

        /// <summary>Names hidden from the Explorer tree (VS Code files.exclude semantics).</summary>
        public HashSet<string> GetExplorerHiddenSet()
        {
            var set = new HashSet<string>(ExplorerDefaultHiddenNames, StringComparer.OrdinalIgnoreCase);
            // Entries the user added on top of the default search/index exclusion list are treated as explicit
            // "hide this" requests; the defaults themselves (node_modules, bin, obj, ...) are never hidden.
            if (settings != null && !string.IsNullOrWhiteSpace(settings.ExcludedFolders))
            {
                var defaults = new HashSet<string>(
                    StudioSettings.DefaultExcludedFoldersList.Split(',').Select(x => x.Trim()),
                    StringComparer.OrdinalIgnoreCase);
                foreach (string c in settings.GetExcludedFoldersList() ?? Array.Empty<string>())
                {
                    string t = (c ?? "").Trim().Trim('/', '\\');
                    if (t.Length > 0 && !defaults.Contains(t))
                        set.Add(t);
                }
            }
            return set;
        }

        private void RefreshExplorerDimmedNames()
        {
            // Build outputs, dependency folders and simple .gitignore entries render dimmed, like VS Code's
            // gitignore decoration, instead of disappearing.
            explorerDimmedNames = GetExcludedFolderSet(workspaceRoot);
            explorerDimmedNames.Remove(".git");
        }

        private bool IsExplorerDimmedName(string name)
        {
            return !string.IsNullOrEmpty(name) && explorerDimmedNames != null && explorerDimmedNames.Contains(name);
        }

        private IEnumerable<string> GetWorkspaceRootFolders()
        {
            if (!string.IsNullOrEmpty(workspaceRoot)) yield return workspaceRoot;
            foreach (string extra in additionalWorkspaceFolders)
                yield return extra;
        }

        private static void CollectExpandedPaths(IEnumerable<ExplorerTreeNode> nodes, HashSet<string> expanded)
        {
            foreach (var n in nodes)
            {
                if (n == null || !n.IsDirectory) continue;
                if (n.IsExpanded && !string.IsNullOrEmpty(n.Tag))
                    expanded.Add(n.Tag);
                if (n.IsExpanded)
                    CollectExpandedPaths(n.Nodes, expanded);
            }
        }

        private void ExpandSavedFolders(ExplorerTreeNode parent, HashSet<string> expanded, HashSet<string> hidden)
        {
            foreach (var child in parent.Nodes.ToList())
            {
                if (child == null || !child.IsDirectory || string.IsNullOrEmpty(child.Tag)) continue;
                if (!expanded.Contains(child.Tag)) continue;
                child.Nodes.Clear();
                PopulateSingleDirectoryLevel(child.Tag, child, hidden, addFilesToIndex: false, seenSet: null);
                child.IsExpanded = true;
                ExpandSavedFolders(child, expanded, hidden);
            }
        }

        /// <summary>
        /// Re-reads the Explorer tree from disk while preserving expanded folders, selection and scroll position.
        /// When <paramref name="revealPath"/> is given its ancestors are expanded and it becomes the selection.
        /// </summary>
        public void RefreshExplorerTree(string revealPath = null)
        {
            if (string.IsNullOrEmpty(workspaceRoot) || !Directory.Exists(workspaceRoot)) return;

            var expanded = new HashSet<string>(PathKeyComparer);
            CollectExpandedPaths(ExplorerTree.Nodes, expanded);
            var collapsedRoots = new HashSet<string>(
                ExplorerTree.Nodes.Where(n => !n.IsExpanded && !string.IsNullOrEmpty(n.Tag)).Select(n => n.Tag),
                PathKeyComparer);
            string selectedPath = ExplorerTree.SelectedNode?.Tag;
            double scroll = ExplorerTree.ScrollOffsetY;

            if (!string.IsNullOrEmpty(revealPath))
            {
                revealPath = Path.GetFullPath(revealPath);
                selectedPath = revealPath;
                string d = Path.GetDirectoryName(revealPath);
                while (!string.IsNullOrEmpty(d) && GetWorkspaceRootFolders().Any(r => IsSameOrUnder(d, r)))
                {
                    expanded.Add(d);
                    collapsedRoots.Remove(d);
                    d = Path.GetDirectoryName(d);
                }
            }

            RefreshExplorerDimmedNames();
            var hidden = GetExplorerHiddenSet();
            ExplorerTree.Nodes.Clear();
            foreach (string root in GetWorkspaceRootFolders().ToList())
            {
                if (!Directory.Exists(root)) continue;
                string name = Path.GetFileName(root.TrimEnd('\\', '/'));
                if (string.IsNullOrEmpty(name)) name = root;
                var rootNode = new ExplorerTreeNode(name)
                {
                    Tag = root,
                    IsDirectory = true,
                    StateImageIndex = 1,
                    IsExpanded = !collapsedRoots.Contains(root)
                };
                PopulateSingleDirectoryLevel(root, rootNode, hidden, addFilesToIndex: false, seenSet: null);
                ExpandSavedFolders(rootNode, expanded, hidden);
                ExplorerTree.Nodes.Add(rootNode);
            }

            ExplorerTree.SelectedNode = string.IsNullOrEmpty(selectedPath) ? null : ExplorerTree.FindNodeByPath(selectedPath);
            ExplorerTree.ScrollOffsetY = scroll;
            ExplorerTree.RebuildTreeLayout(preserveScroll: true);
            if (!string.IsNullOrEmpty(revealPath) && ExplorerTree.SelectedNode != null)
                ExplorerTree.ScrollNodeIntoView(ExplorerTree.SelectedNode);
        }

        private static bool IsSameOrUnder(string path, string root)
        {
            if (string.IsNullOrEmpty(path) || string.IsNullOrEmpty(root)) return false;
            string p = path.TrimEnd('\\', '/');
            string r = root.TrimEnd('\\', '/');
            if (string.Equals(p, r, PathKeyComparison)) return true;
            return p.StartsWith(r + Path.DirectorySeparatorChar, PathKeyComparison) ||
                   p.StartsWith(r + Path.AltDirectorySeparatorChar, PathKeyComparison);
        }

        /// <summary>Refreshes tree, file index and git decorations after a file operation.</summary>
        public void RefreshWorkspaceAfterFileOperation(string revealPath = null)
        {
            RefreshExplorerTree(revealPath);
            ScheduleBackgroundReindex();
            RefreshGitStatusInBackground();
        }

        /// <summary>Rebuilds the quick-open / search file list off the UI thread.</summary>
        public void ScheduleBackgroundReindex()
        {
            if (string.IsNullOrEmpty(workspaceRoot) || !Directory.Exists(workspaceRoot)) return;
            int gen = Interlocked.Increment(ref reindexGeneration);
            string root = workspaceRoot;
            var roots = GetWorkspaceRootFolders().ToList();
            var excluded = GetExcludedFolderSet(root);
            if (!AvaloniaTerminalEmulatorControl.IsDesktopRuntime)
            {
                var files = new List<string>();
                var seen = new HashSet<string>(PathKeyComparer);
                foreach (string r in roots)
                    CollectWorkspaceFilesListRecursive(r, excluded, 0, files, seen, 12000, CancellationToken.None);
                workspaceFiles.Clear();
                workspaceFiles.AddRange(files);
                return;
            }
            _ = Task.Run(() =>
            {
                var files = new List<string>();
                var seen = new HashSet<string>(PathKeyComparer);
                foreach (string r in roots)
                    CollectWorkspaceFilesListRecursive(r, excluded, 0, files, seen, 12000, CancellationToken.None);
                Dispatcher.UIThread.Post(() =>
                {
                    if (gen != reindexGeneration || !string.Equals(root, workspaceRoot, PathKeyComparison)) return;
                    workspaceFiles.Clear();
                    workspaceFiles.AddRange(files);
                });
            });
        }

        public void RefreshGitStatusInBackground()
        {
            if (string.IsNullOrEmpty(workspaceRoot) || !Directory.Exists(workspaceRoot)) return;
            if (!AvaloniaTerminalEmulatorControl.IsDesktopRuntime)
            {
                RefreshGitStatus();
                return;
            }
            string root = workspaceRoot;
            _ = Task.Run(() =>
            {
                string branch = LlmHarnessAndAgentEngine.GetGitBranch(root);
                var entries = LlmHarnessAndAgentEngine.GetGitStatusEntries(root) ?? new List<GitStatusEntry>();
                Dispatcher.UIThread.Post(() =>
                {
                    if (!string.Equals(root, workspaceRoot, PathKeyComparison)) return;
                    ApplyGitEntriesToUi(branch, entries);
                });
            });
        }

        // ---------------------------------------------------------------------------------------------
        // File system watcher (external changes from terminal, git checkout, AI agent, other editors)
        // ---------------------------------------------------------------------------------------------
        public void StartWorkspaceWatcher(string root)
        {
            StopWorkspaceWatcher();
            if (string.IsNullOrEmpty(root) || !Directory.Exists(root)) return;
            try
            {
                var w = new FileSystemWatcher(root)
                {
                    IncludeSubdirectories = true,
                    NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.Size,
                    InternalBufferSize = 64 * 1024
                };
                w.Created += OnWorkspaceFsEvent;
                w.Deleted += OnWorkspaceFsEvent;
                w.Changed += OnWorkspaceFsEvent;
                w.Renamed += (s, e) => { OnWorkspaceFsEvent(s, e); QueueFsChange(e.OldFullPath, structural: true); };
                w.Error += (s, e) =>
                {
                    // Buffer overflow or inotify watch limit: fall back to one full refresh.
                    lock (fsChangeLock) { fsTreeDirty = true; fsGitDirty = true; }
                    Dispatcher.UIThread.Post(RestartFsDebounce);
                };
                w.EnableRaisingEvents = true;
                workspaceWatcher = w;
            }
            catch
            {
                workspaceWatcher = null;
            }
        }

        public void StopWorkspaceWatcher()
        {
            var w = workspaceWatcher;
            workspaceWatcher = null;
            if (w == null) return;
            try { w.EnableRaisingEvents = false; } catch { }
            try { w.Dispose(); } catch { }
        }

        private void OnWorkspaceFsEvent(object sender, FileSystemEventArgs e)
        {
            QueueFsChange(e.FullPath, structural: e.ChangeType != WatcherChangeTypes.Changed);
        }

        private void QueueFsChange(string fullPath, bool structural)
        {
            if (string.IsNullOrEmpty(fullPath)) return;
            string sep = Path.DirectorySeparatorChar.ToString();
            int gitIdx = fullPath.IndexOf(sep + ".git" + sep, StringComparison.Ordinal);
            bool insideGitDir = gitIdx >= 0 || fullPath.EndsWith(sep + ".git", StringComparison.Ordinal);
            lock (fsChangeLock)
            {
                if (insideGitDir)
                {
                    // Commits, checkouts and staging from the terminal touch HEAD / index / refs.
                    string name = Path.GetFileName(fullPath);
                    if (name == "HEAD" || name == "index" || fullPath.Contains(sep + "refs" + sep))
                        fsGitDirty = true;
                    else
                        return;
                }
                else
                {
                    if (structural) fsTreeDirty = true;
                    fsGitDirty = true;
                    if (!structural) fsChangedFiles.Add(fullPath);
                }
            }
            Dispatcher.UIThread.Post(RestartFsDebounce);
        }

        private void RestartFsDebounce()
        {
            if (fsChangeDebounceTimer == null)
            {
                fsChangeDebounceTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(450) };
                fsChangeDebounceTimer.Tick += (s, e) =>
                {
                    fsChangeDebounceTimer.Stop();
                    FlushFsChanges();
                };
            }
            fsChangeDebounceTimer.Stop();
            fsChangeDebounceTimer.Start();
        }

        private void FlushFsChanges()
        {
            bool tree, git;
            List<string> changed;
            lock (fsChangeLock)
            {
                tree = fsTreeDirty;
                git = fsGitDirty;
                changed = fsChangedFiles.ToList();
                fsTreeDirty = false;
                fsGitDirty = false;
                fsChangedFiles.Clear();
            }
            if (string.IsNullOrEmpty(workspaceRoot)) return;
            if (tree)
            {
                RefreshExplorerTree();
                ScheduleBackgroundReindex();
            }
            if (git)
                RefreshGitStatusInBackground();
            foreach (string f in changed)
                ReloadOpenTabIfChangedOnDisk(f);
        }

        /// <summary>VS Code behaviour: a clean editor silently follows external changes; a dirty one is left alone.</summary>
        private void ReloadOpenTabIfChangedOnDisk(string fullPath)
        {
            var tab = openTabs.FirstOrDefault(t => !string.IsNullOrEmpty(t.FilePath) && string.Equals(t.FilePath, fullPath, PathKeyComparison));
            if (tab == null || tab.IsDirty || !File.Exists(fullPath)) return;
            try
            {
                string content = EditorTabBuffer.ReadFilePreservingMetadata(fullPath, out var enc, out bool bom, out string eol, out bool finalNl);
                string norm = (content ?? "").Replace("\r\n", "\n");
                string current = (tab.Content ?? "").Replace("\r\n", "\n");
                if (string.Equals(norm, current, StringComparison.Ordinal)) return;
                int line = tab.CursorLine;
                int col = tab.CursorCol;
                tab.Content = content;
                tab.SavedContent = content;
                tab.FileEncoding = enc;
                tab.HasBom = bom;
                tab.EolSequence = eol;
                tab.HasFinalNewline = finalNl;
                tab.SyncLinesFromContent();
                tab.CursorLine = Math.Max(0, Math.Min(line, tab.Lines.Count - 1));
                tab.CursorCol = Math.Max(0, Math.Min(col, tab.Lines[tab.CursorLine].Length));
                tab.IsDirty = false;
                if (!tab.IsLargeFile) tab.RefreshGutterDiff();
                if (ReferenceEquals(activeTab, tab))
                {
                    PrimaryEditor.LoadBuffer(tab);
                }
                if (isEditorSplit && ReferenceEquals(SplitEditor.CurrentBuffer, tab))
                    SplitEditor.LoadBuffer(tab);
                RefreshTabStrip();
            }
            catch { }
        }

        /// <summary>
        /// Native macOS menu Undo / Redo / Select All act on whatever has keyboard focus (a dialog TextBox, the
        /// chat box, the terminal), not unconditionally on the editor buffer (which also skipped repaint/dirty).
        /// </summary>
        private void RouteNativeEditCommand(string cmd)
        {
            var focused = FocusManager?.GetFocusedElement();
            if (focused is TextBox tb)
            {
                if (cmd == "undo") tb.Undo();
                else if (cmd == "redo") tb.Redo();
                else tb.SelectAll();
                return;
            }
            if (focused is Avalonia.Visual v && IsFromTerminal(v))
            {
                if (cmd == "selectall") activeTerminalSession?.Control?.SelectAll();
                return;
            }
            Key key = cmd == "selectall" ? Key.A : Key.Z;
            KeyModifiers mods = MacKeymap.PrimaryModifier | (cmd == "redo" ? KeyModifiers.Shift : KeyModifiers.None);
            PrimaryEditor.Canvas.Focus();
            SimulateShortcut(key, mods); // same path as the keyboard: repaint, dirty state, diagnostics
        }

        /// <summary>Opens a folder as the workspace or a file in a tab (OS activation, drag-drop onto the dock).</summary>
        public void OpenPathFromOs(string path)
        {
            try
            {
                if (Directory.Exists(path)) LoadWorkspace(Path.GetFullPath(path));
                else if (File.Exists(path)) OpenFileInTab(Path.GetFullPath(path), preview: false);
                Activate();
            }
            catch { }
        }

        // ---------------------------------------------------------------------------------------------
        // Name validation & binary detection
        // ---------------------------------------------------------------------------------------------
        /// <summary>
        /// Validates a name typed in New File / New Folder / Rename. Relative sub-paths ("src/utils/a.ts") are
        /// allowed like in VS Code, but the result must stay inside <paramref name="baseDir"/>.
        /// </summary>
        public static string ValidateNewEntryName(string baseDir, string input, out string fullPath)
        {
            fullPath = null;
            string name = (input ?? "").Trim();
            if (name.Length == 0) return "A file or folder name must be provided.";
            if (Path.IsPathRooted(name)) return "Please enter a name relative to the selected folder.";
            char[] invalid = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
                ? Path.GetInvalidFileNameChars().Where(c => c != '/' && c != '\\').ToArray()
                : new[] { '\0' };
            if (name.IndexOfAny(invalid) >= 0) return $"The name '{name}' contains characters that are not allowed.";
            foreach (string seg in name.Split('/', '\\'))
            {
                if (seg == "." || seg == "..") return "Names cannot contain '.' or '..' path segments.";
                if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows) && seg.Length > 0 && (seg.EndsWith(".") || seg.EndsWith(" ")))
                    return $"'{seg}' is not a valid name on Windows.";
            }
            string combined = Path.GetFullPath(Path.Combine(baseDir, name));
            if (!IsSameOrUnder(combined, baseDir) || string.Equals(combined.TrimEnd('\\', '/'), baseDir.TrimEnd('\\', '/'), PathKeyComparison))
                return "The name must point inside the selected folder.";
            fullPath = combined;
            return null;
        }

        public static bool IsLikelyBinaryFile(string path)
        {
            try
            {
                string ext = Path.GetExtension(path ?? "");
                if (!string.IsNullOrEmpty(ext) && DefaultIgnoredBinaryExtensions.Contains(ext)) return true;
                using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                var buf = new byte[8000];
                int n = fs.Read(buf, 0, buf.Length);
                if (n >= 2 && ((buf[0] == 0xFF && buf[1] == 0xFE) || (buf[0] == 0xFE && buf[1] == 0xFF))) return false; // UTF-16 BOM
                for (int i = 0; i < n; i++)
                    if (buf[i] == 0) return true;
            }
            catch { }
            return false;
        }

        public static void OpenWithSystemDefaultApp(string path)
        {
            try
            {
                if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
                {
                    var psi = new ProcessStartInfo("open") { UseShellExecute = false };
                    psi.ArgumentList.Add(path);
                    Process.Start(psi);
                }
                else if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                {
                    Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
                }
                else
                {
                    var psi = new ProcessStartInfo("xdg-open") { UseShellExecute = false };
                    psi.ArgumentList.Add(path);
                    Process.Start(psi);
                }
            }
            catch { }
        }

        // ---------------------------------------------------------------------------------------------
        // Message / confirm modals
        // ---------------------------------------------------------------------------------------------
        public void ShowMessageModal(string title, string message)
        {
            var stack = new StackPanel { Spacing = 12 };
            stack.Children.Add(new TextBlock
            {
                Text = message ?? "",
                TextWrapping = TextWrapping.Wrap,
                FontSize = 12.5,
                Foreground = new SolidColorBrush(AvaloniaColorHelper.ToAvColor(currentTheme.TextPrimary))
            });
            var ok = new Button { Content = "OK", HorizontalAlignment = HorizontalAlignment.Right, Padding = new Avalonia.Thickness(16, 5) };
            ok.Click += (s, e) => ModalHost.CloseModal();
            stack.Children.Add(ok);
            ModalHost.ShowModal("message", title ?? "codeLess", stack, 460);
            Dispatcher.UIThread.Post(() => ok.Focus(), DispatcherPriority.Loaded);
        }

        public void ShowConfirmModal(string title, string message, string confirmLabel, Action onConfirm)
        {
            var stack = new StackPanel { Spacing = 12 };
            stack.Children.Add(new TextBlock
            {
                Text = message ?? "",
                TextWrapping = TextWrapping.Wrap,
                FontSize = 12.5,
                Foreground = new SolidColorBrush(AvaloniaColorHelper.ToAvColor(currentTheme.TextPrimary))
            });
            var row = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 8 };
            var yes = new Button
            {
                Content = confirmLabel ?? "OK",
                Padding = new Avalonia.Thickness(16, 5),
                Background = new SolidColorBrush(AvaloniaColorHelper.ToAvColor(currentTheme.AccentPrimary)),
                Foreground = Brushes.White
            };
            var no = new Button { Content = "Cancel", Padding = new Avalonia.Thickness(14, 5) };
            yes.Click += (s, e) => { ModalHost.CloseModal(); onConfirm?.Invoke(); };
            no.Click += (s, e) => ModalHost.CloseModal();
            yes.KeyDown += (s, e) => { if (e.Key == Avalonia.Input.Key.Escape) { ModalHost.CloseModal(); e.Handled = true; } };
            row.Children.Add(yes);
            row.Children.Add(no);
            stack.Children.Add(row);
            ModalHost.ShowModal("confirm", title ?? "codeLess", stack, 480);
            Dispatcher.UIThread.Post(() => yes.Focus(), DispatcherPriority.Loaded);
        }

        // ---------------------------------------------------------------------------------------------
        // Trash (macOS NSFileManager, Linux freedesktop.org trash, Windows Recycle Bin)
        // ---------------------------------------------------------------------------------------------
        public static bool TryMoveToSystemTrash(string fullPath, out string error)
        {
            error = null;
            try
            {
                if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
                    return MacTrash.TrashItem(fullPath, out error);
                if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
                    return LinuxTrash.TrashItem(fullPath, out error);
                if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                    return WindowsRecycleBin.TrashItem(fullPath, out error);
            }
            catch (Exception ex)
            {
                error = ex.Message;
            }
            return false;
        }

        private static class MacTrash
        {
            private const string ObjC = "/usr/lib/libobjc.A.dylib";

            [DllImport(ObjC)] private static extern IntPtr objc_getClass(string name);
            [DllImport(ObjC)] private static extern IntPtr sel_registerName(string name);
            [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern IntPtr Send(IntPtr receiver, IntPtr selector);
            [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern IntPtr SendPtr(IntPtr receiver, IntPtr selector, IntPtr arg1);
            [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern IntPtr SendPtrBool(IntPtr receiver, IntPtr selector, IntPtr arg1, bool isDir);
            [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern byte SendTrash(IntPtr receiver, IntPtr selector, IntPtr url, IntPtr outUrl, out IntPtr error);

            public static bool TrashItem(string path, out string error)
            {
                error = null;
                IntPtr utf8 = IntPtr.Zero;
                try
                {
                    byte[] bytes = Encoding.UTF8.GetBytes(path + "\0");
                    utf8 = Marshal.AllocHGlobal(bytes.Length);
                    Marshal.Copy(bytes, 0, utf8, bytes.Length);
                    IntPtr nsString = SendPtr(objc_getClass("NSString"), sel_registerName("stringWithUTF8String:"), utf8);
                    IntPtr url = SendPtrBool(objc_getClass("NSURL"), sel_registerName("fileURLWithPath:isDirectory:"), nsString, Directory.Exists(path));
                    IntPtr fm = Send(objc_getClass("NSFileManager"), sel_registerName("defaultManager"));
                    if (nsString == IntPtr.Zero || url == IntPtr.Zero || fm == IntPtr.Zero)
                    {
                        error = "Could not access the macOS file manager.";
                        return false;
                    }
                    byte ok = SendTrash(fm, sel_registerName("trashItemAtURL:resultingItemURL:error:"), url, IntPtr.Zero, out IntPtr nsError);
                    if (ok != 0) return true;
                    if (nsError != IntPtr.Zero)
                    {
                        IntPtr desc = Send(nsError, sel_registerName("localizedDescription"));
                        IntPtr cstr = desc != IntPtr.Zero ? Send(desc, sel_registerName("UTF8String")) : IntPtr.Zero;
                        error = cstr != IntPtr.Zero ? Marshal.PtrToStringUTF8(cstr) : "Move to Trash failed.";
                    }
                    return false;
                }
                catch (Exception ex)
                {
                    error = ex.Message;
                    return false;
                }
                finally
                {
                    if (utf8 != IntPtr.Zero) Marshal.FreeHGlobal(utf8);
                }
            }
        }

        private static class LinuxTrash
        {
            public static bool TrashItem(string path, out string error)
            {
                error = null;
                // Prefer gio (GNOME/most desktops; handles per-volume .Trash-$UID directories).
                foreach (var (exe, args) in new[] { ("gio", new[] { "trash", "--", path }), ("kioclient5", new[] { "move", path, "trash:/" }) })
                {
                    try
                    {
                        var psi = new ProcessStartInfo(exe) { UseShellExecute = false, RedirectStandardError = true, RedirectStandardOutput = true };
                        foreach (string a in args) psi.ArgumentList.Add(a);
                        using var p = Process.Start(psi);
                        if (p != null && p.WaitForExit(10000) && p.ExitCode == 0 && !File.Exists(path) && !Directory.Exists(path))
                            return true;
                    }
                    catch { }
                }
                // freedesktop.org Trash spec fallback in $XDG_DATA_HOME/Trash (same file system only).
                try
                {
                    string dataHome = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
                    if (string.IsNullOrEmpty(dataHome))
                        dataHome = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "share");
                    string filesDir = Path.Combine(dataHome, "Trash", "files");
                    string infoDir = Path.Combine(dataHome, "Trash", "info");
                    Directory.CreateDirectory(filesDir);
                    Directory.CreateDirectory(infoDir);
                    string baseName = Path.GetFileName(path.TrimEnd('/'));
                    string name = baseName;
                    for (int i = 2; File.Exists(Path.Combine(filesDir, name)) || Directory.Exists(Path.Combine(filesDir, name)) || File.Exists(Path.Combine(infoDir, name + ".trashinfo")); i++)
                        name = baseName + "." + i;
                    string info = "[Trash Info]\nPath=" + Uri.EscapeDataString(path).Replace("%2F", "/") +
                                  "\nDeletionDate=" + DateTime.Now.ToString("yyyy-MM-ddTHH:mm:ss") + "\n";
                    File.WriteAllText(Path.Combine(infoDir, name + ".trashinfo"), info);
                    try
                    {
                        if (Directory.Exists(path)) Directory.Move(path, Path.Combine(filesDir, name));
                        else File.Move(path, Path.Combine(filesDir, name));
                        return true;
                    }
                    catch
                    {
                        try { File.Delete(Path.Combine(infoDir, name + ".trashinfo")); } catch { }
                        throw;
                    }
                }
                catch (Exception ex)
                {
                    error = ex.Message;
                    return false;
                }
            }
        }

        private static class WindowsRecycleBin
        {
            [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
            private struct SHFILEOPSTRUCT
            {
                public IntPtr hwnd;
                public uint wFunc;
                public string pFrom;
                public string pTo;
                public ushort fFlags;
                public bool fAnyOperationsAborted;
                public IntPtr hNameMappings;
                public string lpszProgressTitle;
            }

            [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
            private static extern int SHFileOperation(ref SHFILEOPSTRUCT op);

            public static bool TrashItem(string path, out string error)
            {
                error = null;
                var op = new SHFILEOPSTRUCT
                {
                    wFunc = 0x0003, // FO_DELETE
                    pFrom = path + "\0\0",
                    fFlags = 0x0040 | 0x0010 | 0x0004 | 0x0400 // ALLOWUNDO | NOCONFIRMATION | SILENT | NOERRORUI
                };
                int rc = SHFileOperation(ref op);
                if (rc == 0 && !op.fAnyOperationsAborted) return true;
                error = "Recycle Bin operation failed (code " + rc + ").";
                return false;
            }
        }
    }
}
