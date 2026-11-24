using System;
using System.IO;
using System.Threading;

namespace codeLess.Core
{
    public class KeybindingsFileWatcher
    {
        private FileSystemWatcher watcher;
        private string filePath;
        private Timer debounceTimer;
        private Action<string> onChange;

        public KeybindingsFileWatcher(string path, Action<string> onChange)
        {
            this.filePath = path;
            this.onChange = onChange;
            
            if (!File.Exists(path))
            {
                try { File.WriteAllText(path, "[\n]"); } catch { }
            }

            watcher = new FileSystemWatcher(Path.GetDirectoryName(path), Path.GetFileName(path));
            watcher.NotifyFilter = NotifyFilters.LastWrite;
            watcher.Changed += OnChanged;
            watcher.EnableRaisingEvents = true;
        }

        public void Dispose() { if (watcher != null) { watcher.EnableRaisingEvents = false; watcher.Dispose(); watcher = null; } if (debounceTimer != null) { debounceTimer.Dispose(); debounceTimer = null; } }
        private void OnChanged(object sender, FileSystemEventArgs e)
        {
            if (debounceTimer != null) debounceTimer.Dispose();
            debounceTimer = new Timer(_ => 
            {
                try 
                { 
                    string content = File.ReadAllText(filePath);
                    if (onChange != null) onChange(content);
                }
                catch { }
            }, null, 300, Timeout.Infinite);
        }
    }
}

    // Add Dispose to KeybindingsFileWatcher.cs
