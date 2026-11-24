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

        private static void OnFileEvent(object sender, FileSystemEventArgs e)
        {
            try
            {
                var currentWriteTime = File.GetLastWriteTimeUtc(IdePersistentSettings.GetSettingsFilePath());
                if ((currentWriteTime - _lastKnownWriteTime).TotalMilliseconds < 50) return;
                _lastKnownWriteTime = currentWriteTime;
            }
            catch { }
            
            try { _debounceTimer.Change(300, Timeout.Infinite); } catch { }
        }

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
