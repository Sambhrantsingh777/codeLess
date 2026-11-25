using System;
using System.Collections.Generic;

namespace codeLess.Studio
{
    public class RestoreWindowInfo
    {
        public string WorkspacePath { get; set; }
        public int X { get; set; }
        public int Y { get; set; }
        public int Width { get; set; }
        public int Height { get; set; }
        public bool IsMaximized { get; set; }

        public RestoreWindowInfo()
        {
            WorkspacePath = "";
            Width = 1440;
            Height = 900;
        }
    }

    public interface IWorkbenchWindow
    {
        string WorkspaceRoot { get; }
        bool IsActive { get; }
        void FocusWindow();
        void OpenFolder(string folderPath);
        void OpenFileInTab(string filePath, Action onClosed = null);
        void OpenFileAtLine(string filePath, int line, int col, Action onClosed = null);
        void AddFolderToWorkspace(string folderPath);
        void DiffFiles(string fileA, string fileB, Action onClosed = null);
        RestoreWindowInfo GetWindowBounds();
        void SetWindowBounds(RestoreWindowInfo bounds);
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
