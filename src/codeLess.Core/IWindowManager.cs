using System;
using System.Collections.Generic;

namespace codeLess.Studio
{
    public interface IWorkbenchWindow
    {
        string WorkspaceRoot { get; }
        bool IsActive { get; }
        void FocusWindow();
        void OpenFileInTab(string filePath, Action onClosed = null);
        void OpenFileAtLine(string filePath, int line, int col, Action onClosed = null);
        void AddFolderToWorkspace(string folderPath);
        void DiffFiles(string fileA, string fileB, Action onClosed = null);
        void CloseWindow();
        event EventHandler WindowClosed;
    }

    public interface IWindowManager
    {
        IEnumerable<IWorkbenchWindow> GetOpenWindows();
        IWorkbenchWindow CreateNewWindow(string workspacePath);
        IWorkbenchWindow GetLastActiveWindow();
        void QuitApplication();
    }
}
