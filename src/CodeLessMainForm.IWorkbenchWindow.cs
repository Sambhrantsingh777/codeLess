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

        void IWorkbenchWindow.OpenFileInTab(string filePath)
        {
            this.OpenFileInTab(filePath);
        }

        void IWorkbenchWindow.OpenFileAtLine(string filePath, int line, int col)
        {
            var tab = this.OpenFileInTab(filePath);
            if (tab != null && this.editorControl != null)
            {
                this.editorControl.JumpToLine(line);
                // jump to col could be added if supported
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

        void IWorkbenchWindow.DiffFiles(string fileA, string fileB)
        {
            // if diff is supported, implement it. otherwise just open both.
            this.OpenFileInTab(fileA);
            this.OpenFileInTab(fileB);
        }

        void IWorkbenchWindow.CloseWindow()
        {
            this.Close();
        }
    }
}
