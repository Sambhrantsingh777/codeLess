using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Forms;
using codeLess.Studio;

namespace codeLess.Studio
{
    public class CodeLessMultiWindowAppContext : ApplicationContext, IWindowManager
    {
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
                    windows[0].BeginInvoke(new Action(() => {
                        ApplicationSession.ProcessArgs(msg.Args, msg.Cwd, this);
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
            var settings = codeLess.Studio.IdePersistentSettings.Load();
            string root = ((IWorkbenchWindow)f).WorkspaceRoot;
            if (settings.RestoreWindowPaths.Contains(root)) {
                settings.RestoreWindowPaths.Remove(root);
                settings.Save();
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
            // Simple approximation for WinForms: return the currently active form, or the last one created.
            var active = Form.ActiveForm as CodeLessMainForm;
            if (active != null) return active;
            return windows.Count > 0 ? windows[windows.Count - 1] : null;
        }

        public void QuitApplication()
        {
            var settings = codeLess.Studio.IdePersistentSettings.Load();
            if (settings.WindowRestoreWindows != "none") {
                settings.RestoreWindowPaths.Clear();
                foreach (var w in GetOpenWindows()) {
                    if (!string.IsNullOrEmpty(w.WorkspaceRoot)) settings.RestoreWindowPaths.Add(w.WorkspaceRoot);
                }
                settings.Save();
            }
            foreach (var w in windows.ToList())
            {
                w.Close();
            }
        }
    }
}
