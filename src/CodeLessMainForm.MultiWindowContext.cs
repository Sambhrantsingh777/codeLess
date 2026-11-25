using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Forms;
using codeLess.Studio;

namespace codeLess.Studio
{
    public class CodeLessMultiWindowAppContext : ApplicationContext, IWindowManager
    {
        private bool _isQuitting = false;
        private List<CodeLessMainForm> windows = new List<CodeLessMainForm>();

        public CodeLessMultiWindowAppContext(string[] args)
        {
            if (ApplicationSession.TryForwardToPrimaryInstance(args))
            {
                Environment.Exit(0);
                return;
            }

            ApplicationSession.OnMessageReceived = (msg, ack) =>
            {
                if (windows.Count > 0)
                {
                    windows[0].BeginInvoke(new Action(() =>
                    {
                        ApplicationSession.ProcessArgs(msg.Args, msg.Cwd, this, ack);
                    }));
                }
            };
            ApplicationSession.StartPrimaryServer();

            ApplicationSession.ProcessArgs(args, Environment.CurrentDirectory, this);
            if (windows.Count == 0)
            {
                CreateNewWindow(null);
            }
        }

        private void OnFormClosed(object sender, FormClosedEventArgs e)
        {
            var f = sender as CodeLessMainForm;
            if (f != null)
            {
                f.FormClosed -= OnFormClosed;
                windows.Remove(f);
            }
            if (!_isQuitting && f != null)
            {
                var settings = IdePersistentSettings.Load();
                string root = (((IWorkbenchWindow)f).WorkspaceRoot ?? "").TrimEnd('\\', '/');
                bool changed = false;
                if (settings.RestoreWindowPaths != null)
                {
                    for (int i = settings.RestoreWindowPaths.Count - 1; i >= 0; i--)
                    {
                        string p = (settings.RestoreWindowPaths[i] ?? "").TrimEnd('\\', '/');
                        if (string.Equals(p, root, StringComparison.OrdinalIgnoreCase))
                        {
                            settings.RestoreWindowPaths.RemoveAt(i);
                            changed = true;
                        }
                    }
                }
                if (settings.RestoreWindows != null)
                {
                    for (int i = settings.RestoreWindows.Count - 1; i >= 0; i--)
                    {
                        RestoreWindowInfo rwi = settings.RestoreWindows[i];
                        string p = (rwi != null && rwi.WorkspacePath != null ? rwi.WorkspacePath : "").TrimEnd('\\', '/');
                        if (string.Equals(p, root, StringComparison.OrdinalIgnoreCase))
                        {
                            settings.RestoreWindows.RemoveAt(i);
                            changed = true;
                        }
                    }
                }
                if (changed)
                {
                    settings.Save();
                }
            }
            if (windows.Count == 0)
            {
                QuitApplication();
                ExitThread();
            }
        }

        public IEnumerable<IWorkbenchWindow> GetOpenWindows()
        {
            return windows.Cast<IWorkbenchWindow>();
        }

        public IWorkbenchWindow CreateNewWindow(string workspacePath)
        {
            var f = new CodeLessMainForm(workspacePath);
            windows.Add(f);
            f.FormClosed += OnFormClosed;
            f.Show();
            return f;
        }

        public IWorkbenchWindow GetLastActiveWindow()
        {
            var active = Form.ActiveForm as CodeLessMainForm;
            if (active != null) return active;
            return windows.Count > 0 ? windows[windows.Count - 1] : null;
        }

        public void QuitApplication()
        {
            _isQuitting = true;
            try
            {
                var settings = IdePersistentSettings.Load();
                if (settings.WindowRestoreWindows != "none")
                {
                    settings.RestoreWindowPaths.Clear();
                    settings.RestoreWindows.Clear();
                    foreach (var w in GetOpenWindows())
                    {
                        RestoreWindowInfo bounds = w.GetWindowBounds() ?? new RestoreWindowInfo { WorkspacePath = w.WorkspaceRoot ?? "" };
                        bounds.WorkspacePath = w.WorkspaceRoot ?? "";
                        settings.RestoreWindows.Add(bounds);
                        if (!string.IsNullOrEmpty(w.WorkspaceRoot))
                            settings.RestoreWindowPaths.Add(w.WorkspaceRoot);
                    }
                    settings.Save();
                }
                foreach (var w in windows.ToList())
                {
                    w.Close();
                }
            }
            finally
            {
                _isQuitting = false;
            }
        }
    }
}
