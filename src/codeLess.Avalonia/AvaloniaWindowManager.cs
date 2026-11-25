using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using codeLess.Studio;

namespace codeLess.AvaloniaApp
{
    public class AvaloniaWindowManager : IWindowManager
    {
        private IClassicDesktopStyleApplicationLifetime _desktop;
        private bool _isQuitting = false;
        
        public AvaloniaWindowManager(IClassicDesktopStyleApplicationLifetime desktop)
        {
            _desktop = desktop;
            if (OperatingSystem.IsMacOS())
            {
                _desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            }
        }

        public IEnumerable<IWorkbenchWindow> GetOpenWindows()
        {
            if (App.TestOpenWindows != null) return App.TestOpenWindows;
            return _desktop.Windows.OfType<MainWindow>().Cast<IWorkbenchWindow>();
        }

        public IWorkbenchWindow CreateNewWindow(string workspacePath)
        {
            var w = new MainWindow(workspacePath);
            w.Show();
            if (_desktop.MainWindow == null) _desktop.MainWindow = w;
            
            w.Closed += (s, e) =>
            {
                if (_isQuitting) return;
                var settings = codeLess.Studio.IdePersistentSettings.Load();
                string normPath = workspacePath ?? "";
                if (settings.RestoreWindowPaths.Contains(normPath)) {
                    settings.RestoreWindowPaths.Remove(normPath);
                    settings.Save();
                }
                if (!OperatingSystem.IsMacOS() && !GetOpenWindows().Any())
                {
                    QuitApplication();
                }
            };
            return w;
        }

        public IWorkbenchWindow GetLastActiveWindow()
        {
            var active = _desktop.Windows.OfType<MainWindow>().FirstOrDefault(w => w.IsActive);
            if (active != null) return active;
            return _desktop.Windows.OfType<MainWindow>().LastOrDefault();
        }

        public void QuitApplication()
        {
            _isQuitting = true;
            var settings = codeLess.Studio.IdePersistentSettings.Load();
            if (settings.WindowRestoreWindows != "none") {
                settings.RestoreWindowPaths.Clear();
                foreach (var w in GetOpenWindows()) {
                    if (!string.IsNullOrEmpty(w.WorkspaceRoot)) settings.RestoreWindowPaths.Add(w.WorkspaceRoot);
                }
                settings.Save();
            }
            _desktop.Shutdown();
        }
    }
}
