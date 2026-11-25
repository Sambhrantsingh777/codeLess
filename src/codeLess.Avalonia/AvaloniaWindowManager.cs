using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using codeLess.Studio;

namespace codeLess.AvaloniaApp
{
    public class AvaloniaWindowManager : IWindowManager
    {
        private IClassicDesktopStyleApplicationLifetime _desktop;
        private readonly List<MainWindow> _windows = new List<MainWindow>();
        private MainWindow _lastActiveWindow;
        private bool _isQuitting = false;

        public AvaloniaWindowManager(IClassicDesktopStyleApplicationLifetime desktop = null)
        {
            if (desktop != null)
            {
                AttachDesktopLifetime(desktop);
            }
        }

        public void AttachDesktopLifetime(IClassicDesktopStyleApplicationLifetime desktop)
        {
            _desktop = desktop;
            if (_desktop != null && OperatingSystem.IsMacOS())
            {
                _desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            }
        }

        public void RegisterWindow(MainWindow w)
        {
            if (w == null) return;
            if (!_windows.Contains(w))
            {
                var prev = GetLastActiveWindow() as MainWindow;
                if (prev != null && prev != w)
                {
                    var pos = prev.Position;
                    var targetPos = new PixelPoint(pos.X + 28, pos.Y + 28);
                    w.WindowStartupLocation = WindowStartupLocation.Manual;
                    w.Position = targetPos;
                    EventHandler onOpened = null;
                    onOpened = (s, e) =>
                    {
                        w.Opened -= onOpened;
                        if (!w.HasExplicitBoundsSet)
                        {
                            w.Position = targetPos;
                        }
                    };
                    w.Opened += onOpened;
                }
                _windows.Add(w);
            }
            _lastActiveWindow = w;
            if (_desktop != null && _desktop.MainWindow == null)
            {
                _desktop.MainWindow = w;
            }
            RefreshAllWindowMenus();
        }

        public void NotifyWindowActivated(MainWindow w)
        {
            if (w != null && !w.IsClosed)
            {
                if (!_windows.Contains(w))
                    _windows.Add(w);
                _lastActiveWindow = w;
            }
        }

        public void UnregisterWindow(MainWindow w)
        {
            if (w == null) return;
            bool wasTracked = _windows.Remove(w);
            if (ReferenceEquals(_lastActiveWindow, w))
            {
                _lastActiveWindow = _windows.LastOrDefault(x => !x.IsClosed && x.IsActive)
                                    ?? _windows.LastOrDefault(x => !x.IsClosed);
            }
            if (_desktop != null && ReferenceEquals(_desktop.MainWindow, w))
            {
                _desktop.MainWindow = _lastActiveWindow;
            }
            RefreshAllWindowMenus();
            if (!wasTracked) return;
            if (_isQuitting) return;

            var settings = IdePersistentSettings.Load();
            string normPath = (w.WorkspaceRoot ?? "").TrimEnd('\\', '/');
            bool changed = false;
            if (settings.RestoreWindowPaths != null)
            {
                for (int i = settings.RestoreWindowPaths.Count - 1; i >= 0; i--)
                {
                    string p = (settings.RestoreWindowPaths[i] ?? "").TrimEnd('\\', '/');
                    if (string.Equals(p, normPath, StringComparison.OrdinalIgnoreCase))
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
                    string p = (settings.RestoreWindows[i]?.WorkspacePath ?? "").TrimEnd('\\', '/');
                    if (string.Equals(p, normPath, StringComparison.OrdinalIgnoreCase))
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

            if (!OperatingSystem.IsMacOS() && !GetOpenWindows().Any() && _desktop != null)
            {
                QuitApplication();
            }
        }

        public void RefreshAllWindowMenus()
        {
            foreach (var w in _windows.ToList())
            {
                if (w != null && !w.IsClosed)
                {
                    w.RefreshWindowMenu();
                }
            }
        }

        public IEnumerable<IWorkbenchWindow> GetOpenWindows()
        {
            _windows.RemoveAll(w => w == null || w.IsClosed);
            if (_desktop != null)
            {
                foreach (var dw in _desktop.Windows.OfType<MainWindow>())
                {
                    if (!dw.IsClosed && !_windows.Contains(dw))
                        _windows.Add(dw);
                }
            }
            return _windows.Cast<IWorkbenchWindow>().ToList();
        }

        public IWorkbenchWindow CreateNewWindow(string workspacePath)
        {
            var w = new MainWindow(workspacePath);
            RegisterWindow(w);
            w.Show();
            if (!string.IsNullOrEmpty(workspacePath))
            {
                w.RestoreWorkspaceSession();
            }
            return w;
        }

        public IWorkbenchWindow GetLastActiveWindow()
        {
            _windows.RemoveAll(w => w == null || w.IsClosed);
            if (_lastActiveWindow != null && !_lastActiveWindow.IsClosed && _windows.Contains(_lastActiveWindow))
                return _lastActiveWindow;
            if (_desktop != null)
            {
                var activeDesktop = _desktop.Windows.OfType<MainWindow>().FirstOrDefault(w => !w.IsClosed && w.IsActive);
                if (activeDesktop != null) return activeDesktop;
            }
            return _windows.LastOrDefault(w => !w.IsClosed);
        }

        public void QuitApplication()
        {
            _isQuitting = true;
            try
            {
                var openWindows = GetOpenWindows().ToList();
                var settings = IdePersistentSettings.Load();
                if (settings.WindowRestoreWindows != "none")
                {
                    settings.RestoreWindowPaths.Clear();
                    settings.RestoreWindows.Clear();
                    foreach (var w in openWindows)
                    {
                        var bounds = w.GetWindowBounds() ?? new RestoreWindowInfo { WorkspacePath = w.WorkspaceRoot ?? "" };
                        bounds.WorkspacePath = w.WorkspaceRoot ?? "";
                        settings.RestoreWindows.Add(bounds);
                        if (!string.IsNullOrEmpty(w.WorkspaceRoot))
                            settings.RestoreWindowPaths.Add(w.WorkspaceRoot);
                    }
                    settings.Save();
                }
                else
                {
                    settings.RestoreWindowPaths.Clear();
                    settings.RestoreWindows.Clear();
                    settings.Save();
                }

                foreach (var w in openWindows.OfType<MainWindow>().ToList())
                {
                    try
                    {
                        w.SuppressUnsavedPrompts = true;
                        w.Close();
                    }
                    catch { }
                }

                if (_desktop != null)
                {
                    try { _desktop.Shutdown(); } catch { }
                }
            }
            finally
            {
                _isQuitting = false;
            }
        }
    }
}
