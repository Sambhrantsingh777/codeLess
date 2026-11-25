using System;
using System.IO;
using System.Threading;

namespace codeLess.Studio
{
    public static class IdeSettingsWatcher
    {
        private static FileSystemWatcher _watcher;
        private static Timer _debounceTimer;
        public static event Action<IdePersistentSettings> OnSettingsFileChanged;
        private static DateTime _lastKnownWriteTime = DateTime.MinValue;

        public static void Start()
        {
            if (_watcher != null) return;
            string path = IdePersistentSettings.GetSettingsFilePath();
            string dir = Path.GetDirectoryName(path);
            if (string.IsNullOrEmpty(dir)) return;
            try { Directory.CreateDirectory(dir); } catch { }
            try { if (File.Exists(path)) _lastKnownWriteTime = File.GetLastWriteTimeUtc(path); } catch { }

            _watcher = new FileSystemWatcher(dir, Path.GetFileName(path));
            _watcher.NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.CreationTime;
            _watcher.Changed += OnFileEvent;
            _watcher.Created += OnFileEvent;
            _watcher.EnableRaisingEvents = true;

            _debounceTimer = new Timer(OnDebounceTick, null, Timeout.Infinite, Timeout.Infinite);
        }

        public static Action<int> DebounceTimerChangeSeam;

        private static void OnFileEvent(object sender, FileSystemEventArgs e)
        {
            try { 
                if (DebounceTimerChangeSeam != null) DebounceTimerChangeSeam(300);
                else _debounceTimer.Change(300, Timeout.Infinite); 
            } catch { }
        }

        public static void StopForTests()
        {
            if (_watcher != null)
            {
                _watcher.EnableRaisingEvents = false;
                _watcher.Dispose();
                _watcher = null;
            }
            if (_debounceTimer != null)
            {
                _debounceTimer.Dispose();
                _debounceTimer = null;
            }
        }

        public static void ForceDebounceTickForTests() { OnDebounceTick(null); }

        private static void OnDebounceTick(object state)
        {
            try
            {
                var settings = IdePersistentSettings.Load();
                if (OnSettingsFileChanged != null)
                {
                    OnSettingsFileChanged(settings);
                }
            }
            catch { }
        }
    }
}
