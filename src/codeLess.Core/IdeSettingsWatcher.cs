using System;
using System.IO;
using System.Threading;

namespace codeLess.Studio
{
    public static class IdeSettingsWatcher
    {
        private static readonly object _sync = new object();
        private static FileSystemWatcher _watcher;
        private static Timer _debounceTimer;
        private static string _watchedPath;
        private static bool _pendingDebounce;
        private static long _lastInProcessWriteTicks;
        public static event Action<IdePersistentSettings> OnSettingsFileChanged;
        public static Action<int> DebounceTimerChangeSeam;

        public static void NotifyInProcessWrite()
        {
            Interlocked.Exchange(ref _lastInProcessWriteTicks, DateTime.UtcNow.Ticks);
            if (DebounceTimerChangeSeam == null)
            {
                lock (_sync)
                {
                    _pendingDebounce = false;
                    if (_debounceTimer != null)
                    {
                        try { _debounceTimer.Change(Timeout.Infinite, Timeout.Infinite); } catch { }
                    }
                }
            }
        }

        public static void Start()
        {
            string path = IdePersistentSettings.GetSettingsFilePath();
            string dir = Path.GetDirectoryName(path);
            if (string.IsNullOrEmpty(dir)) return;

            lock (_sync)
            {
                if (_watcher != null && string.Equals(_watchedPath, path, StringComparison.Ordinal))
                    return;

                StopInternal();
                try { Directory.CreateDirectory(dir); } catch { }

                _watchedPath = path;
                _debounceTimer = new Timer(OnDebounceTick, null, Timeout.Infinite, Timeout.Infinite);
                try
                {
                    _watcher = new FileSystemWatcher(dir, Path.GetFileName(path));
                    _watcher.NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.CreationTime;
                    _watcher.Changed += OnFileEvent;
                    _watcher.Created += OnFileEvent;
                    _watcher.Renamed += OnRenamedEvent;
                    _watcher.EnableRaisingEvents = true;
                }
                catch { }
            }
        }

        private static bool ShouldIgnoreWatcherEvent()
        {
            if (DebounceTimerChangeSeam != null) return false;
            if (OnSettingsFileChanged == null) return true;
            long lastTicks = Interlocked.Read(ref _lastInProcessWriteTicks);
            if (lastTicks > 0)
            {
                double elapsedMs = new TimeSpan(DateTime.UtcNow.Ticks - lastTicks).TotalMilliseconds;
                if (elapsedMs >= 0 && elapsedMs < 400)
                    return true;
            }
            return false;
        }

        private static void OnFileEvent(object sender, FileSystemEventArgs e)
        {
            if (ShouldIgnoreWatcherEvent()) return;
            ScheduleDebounce(300);
        }

        private static void OnRenamedEvent(object sender, RenamedEventArgs e)
        {
            if (ShouldIgnoreWatcherEvent()) return;
            ScheduleDebounce(300);
        }

        public static void ScheduleDebounce(int delayMs = 300)
        {
            lock (_sync)
            {
                _pendingDebounce = true;
                try
                {
                    Action<int> seam = DebounceTimerChangeSeam;
                    if (seam != null)
                        seam(delayMs);
                    else if (_debounceTimer != null)
                        _debounceTimer.Change(delayMs, Timeout.Infinite);
                }
                catch { }
            }
        }

        private static void StopInternal()
        {
            _pendingDebounce = false;
            _watchedPath = null;
            if (_watcher != null)
            {
                try
                {
                    _watcher.EnableRaisingEvents = false;
                    _watcher.Changed -= OnFileEvent;
                    _watcher.Created -= OnFileEvent;
                    _watcher.Renamed -= OnRenamedEvent;
                    _watcher.Dispose();
                }
                catch { }
                _watcher = null;
            }
            if (_debounceTimer != null)
            {
                try { _debounceTimer.Dispose(); } catch { }
                _debounceTimer = null;
            }
        }

        public static void StopForTests()
        {
            lock (_sync)
            {
                StopInternal();
            }
        }

        public static void ForceDebounceTickForTests()
        {
            OnDebounceTick(null);
        }

        private static void OnDebounceTick(object state)
        {
            lock (_sync)
            {
                if (_debounceTimer != null)
                {
                    try { _debounceTimer.Change(Timeout.Infinite, Timeout.Infinite); } catch { }
                }
                _pendingDebounce = false;
            }
            try
            {
                Action<IdePersistentSettings> handler = OnSettingsFileChanged;
                if (handler != null)
                {
                    IdePersistentSettings settings = IdePersistentSettings.Load();
                    handler(settings);
                }
            }
            catch { }
        }
    }
}
