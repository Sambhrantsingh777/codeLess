using System;
using System.IO;
using Avalonia.Controls;
using codeLess.Studio;

namespace codeLess.AvaloniaApp
{
    public partial class MainWindow : IWorkbenchWindow
    {
        string IWorkbenchWindow.WorkspaceRoot { get { return this.workspaceRoot; } }

        void IWorkbenchWindow.FocusWindow()
        {
            if (this.WindowState == WindowState.Minimized)
                this.WindowState = WindowState.Normal;
            this.Activate();
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
            }
        }

        void IWorkbenchWindow.AddFolderToWorkspace(string folderPath)
        {
            if (!string.IsNullOrEmpty(folderPath) && Directory.Exists(folderPath))
            {
                if (string.IsNullOrEmpty(this.workspaceRoot))
                {
                    this.LoadWorkspace(folderPath, false);
                }
                else
                {
                    if (!this.additionalWorkspaceFolders.Contains(folderPath))
                    {
                        this.additionalWorkspaceFolders.Add(folderPath);
                        this.LoadWorkspace(this.workspaceRoot, true);
                    }
                }
            }
        }

        void IWorkbenchWindow.DiffFiles(string fileA, string fileB, Action onClosed)
        {
            var t1 = this.OpenFileInTab(fileA);
            var t2 = this.OpenFileInTab(fileB);
            if (t1 != null && onClosed != null) t1.OnClosed += onClosed;
        }

        event EventHandler IWorkbenchWindow.WindowClosed {
            add { this.Closed += value; }
            remove { this.Closed -= value; }
        }

        void IWorkbenchWindow.CloseWindow()
        {
            this.Close();
        }
    }
}
