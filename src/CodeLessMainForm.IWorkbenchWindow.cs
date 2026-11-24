using System;
using System.IO;
using System.Windows.Forms;
using codeLess.Studio;

namespace codeLess.Studio
{
    public partial class CodeLessMainForm
    {
        string IWorkbenchWindow.WorkspaceRoot { get { return this.workspaceRoot; } }
        bool IWorkbenchWindow.IsActive { get { return this.ContainsFocus; } }

        void IWorkbenchWindow.FocusWindow()
        {
            if (this.WindowState == FormWindowState.Minimized)
                this.WindowState = FormWindowState.Normal;
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
            if (tab != null && this.editorControl != null)
            {
                this.editorControl.JumpToLine(line);
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
                        this.LoadWorkspace(folderPath, false, true);
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
            add { this.FormClosed += (s, e) => value(s, e); }
            remove { this.FormClosed -= (s, e) => value(s, e); }
        }

        void IWorkbenchWindow.CloseWindow()
        {
            this.Close();
        }
    }
}
