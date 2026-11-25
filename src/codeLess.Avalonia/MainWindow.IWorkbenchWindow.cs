using System;
using System.IO;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using codeLess.Studio;

namespace codeLess.AvaloniaApp
{
    public partial class MainWindow : IWorkbenchWindow
    {
        private PixelPoint _windowPosition;

        public new PixelPoint Position
        {
            get
            {
                var p = base.Position;
                return (p.X == 0 && p.Y == 0 && (_windowPosition.X != 0 || _windowPosition.Y != 0)) ? _windowPosition : p;
            }
            set
            {
                _windowPosition = value;
                base.Position = value;
            }
        }

        string IWorkbenchWindow.WorkspaceRoot { get { return this.workspaceRoot; } }

        bool IWorkbenchWindow.IsActive
        {
            get
            {
                if (App.WindowManager != null && ReferenceEquals(App.WindowManager.GetLastActiveWindow(), this))
                    return true;
                return base.IsActive;
            }
        }

        void IWorkbenchWindow.FocusWindow()
        {
            if (this.WindowState == WindowState.Minimized)
                this.WindowState = WindowState.Normal;
            this.Activate();
            this.Focus();
            App.WindowManager?.NotifyWindowActivated(this);
        }

        void IWorkbenchWindow.OpenFolder(string folderPath)
        {
            if (!string.IsNullOrEmpty(folderPath) && Directory.Exists(folderPath))
            {
                this.LoadWorkspace(folderPath, false);
            }
        }

        void IWorkbenchWindow.OpenFileInTab(string filePath, Action onClosed)
        {
            var tab = this.OpenFileInTab(filePath);
            if (tab != null && onClosed != null) tab.OnClosed += onClosed;
        }

        void IWorkbenchWindow.OpenFileAtLine(string filePath, int line, int col, Action onClosed)
        {
            var tab = this.OpenFileInTab(filePath);
            if (tab != null && onClosed != null) tab.OnClosed += onClosed;
            if (tab != null && this.PrimaryEditor != null)
            {
                this.PrimaryEditor.GoToLine(line);
                if (col > 1 && tab.Lines != null && tab.CursorLine >= 0 && tab.CursorLine < tab.Lines.Count)
                {
                    tab.CursorCol = Math.Max(0, Math.Min(col - 1, tab.Lines[tab.CursorLine].Length));
                    tab.SelectionStartLine = tab.CursorLine;
                    tab.SelectionStartCol = tab.CursorCol;
                    this.PrimaryEditor.Canvas.InvalidateVisual();
                }
            }
        }

        void IWorkbenchWindow.AddFolderToWorkspace(string folderPath)
        {
            if (!string.IsNullOrEmpty(folderPath) && Directory.Exists(folderPath))
            {
                this.AddFolderToWorkspace(folderPath);
            }
        }

        void IWorkbenchWindow.DiffFiles(string fileA, string fileB, Action onClosed)
        {
            var t1 = this.OpenFileInTab(fileA);
            var t2 = this.OpenFileInTab(fileB);
            if (t1 != null && onClosed != null) t1.OnClosed += onClosed;
        }

        RestoreWindowInfo IWorkbenchWindow.GetWindowBounds()
        {
            int w = (int)(!double.IsNaN(this.Width) && this.Width > 0 ? this.Width : (this.Bounds.Width > 0 ? this.Bounds.Width : 1440));
            int h = (int)(!double.IsNaN(this.Height) && this.Height > 0 ? this.Height : (this.Bounds.Height > 0 ? this.Bounds.Height : 900));
            return new RestoreWindowInfo
            {
                WorkspacePath = this.workspaceRoot ?? "",
                X = this.Position.X,
                Y = this.Position.Y,
                Width = w,
                Height = h,
                IsMaximized = this.WindowState == WindowState.Maximized
            };
        }

        internal bool HasExplicitBoundsSet { get; private set; }

        void IWorkbenchWindow.SetWindowBounds(RestoreWindowInfo bounds)
        {
            if (bounds == null) return;
            HasExplicitBoundsSet = true;
            if (bounds.Width >= MinWidth) this.Width = bounds.Width;
            if (bounds.Height >= MinHeight) this.Height = bounds.Height;
            this.WindowStartupLocation = WindowStartupLocation.Manual;
            this.Position = new PixelPoint(bounds.X, bounds.Y);
            if (bounds.IsMaximized) this.WindowState = WindowState.Maximized;
        }

        event EventHandler IWorkbenchWindow.WindowClosed
        {
            add { this.Closed += value; }
            remove { this.Closed -= value; }
        }

        void IWorkbenchWindow.CloseWindow()
        {
            this.Close();
        }

        public void SaveWorkspaceSession()
        {
            if (string.IsNullOrEmpty(this.workspaceRoot)) return;
            try
            {
                var state = new WorkspaceSessionState
                {
                    OpenTabPaths = this.openTabs
                        .Where(t => t != null && !t.IsUntitled && !string.IsNullOrEmpty(t.FilePath))
                        .Select(t => t.FilePath)
                        .ToList(),
                    ActiveTabPath = (this.activeTab != null && !this.activeTab.IsUntitled) ? this.activeTab.FilePath : null
                };
                WorkspaceSessionStore.Save(this.workspaceRoot, state);
            }
            catch { }
        }

        public void RestoreWorkspaceSession()
        {
            if (string.IsNullOrEmpty(this.workspaceRoot)) return;
            try
            {
                var state = WorkspaceSessionStore.Load(this.workspaceRoot);
                if (state?.OpenTabPaths == null || state.OpenTabPaths.Count == 0) return;
                foreach (var tabPath in state.OpenTabPaths)
                {
                    if (!string.IsNullOrEmpty(tabPath) && File.Exists(tabPath))
                    {
                        OpenFileInTab(tabPath, preview: false);
                    }
                }
                if (!string.IsNullOrEmpty(state.ActiveTabPath))
                {
                    var active = this.openTabs.FirstOrDefault(t => string.Equals(t.FilePath, state.ActiveTabPath, PathKeyComparison));
                    if (active != null) ActivateTab(active);
                }
            }
            catch { }
        }
    }
}
