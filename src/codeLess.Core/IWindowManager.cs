using System;
using System.Collections.Generic;

namespace codeLess.Studio
{
    public interface IWorkbenchWindow
    {
        string WorkspaceRoot { get; }
        bool IsActive { get; }
        void FocusWindow();
        void OpenFileInTab(string filePath);
        void OpenFileAtLine(string filePath, int line, int col);
        void AddFolderToWorkspace(string folderPath);
        void DiffFiles(string fileA, string fileB);
        void CloseWindow();
    }

    public interface IWindowManager
    {
        IEnumerable<IWorkbenchWindow> GetOpenWindows();
        IWorkbenchWindow CreateNewWindow(string workspacePath);
        IWorkbenchWindow GetLastActiveWindow();
        void QuitApplication();
    }
}
