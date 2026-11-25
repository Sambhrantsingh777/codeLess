using System;
using System.IO;
using System.Threading;

namespace codeLess.Core
{
    public class KeybindingsFileWatcher : IDisposable
    {
        private FileSystemWatcher watcher;
        private string filePath;
        private Timer debounceTimer;
        private Action<string> onChange;
        private readonly object timerLock = new object();

        public KeybindingsFileWatcher(string path, Action<string> onChange)
        {
            this.filePath = path;
            this.onChange = onChange;
            
            if (!File.Exists(path))
            {
                try { File.WriteAllText(path, "[\n]"); } catch { }
            }

            watcher = new FileSystemWatcher(Path.GetDirectoryName(path), Path.GetFileName(path));
            watcher.NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.CreationTime;
            watcher.Changed += OnChanged;
            watcher.Created += OnChanged;
            watcher.Renamed += (s, e) => OnChanged(s, null);
            watcher.EnableRaisingEvents = true;
        }

        public void Dispose() 
        { 
            if (watcher != null) 
            { 
                watcher.EnableRaisingEvents = false; 
                watcher.Dispose(); 
                watcher = null; 
            } 
            lock (timerLock)
            {
                if (debounceTimer != null) 
                { 
                    debounceTimer.Dispose(); 
                    debounceTimer = null; 
                }
            }
        }
        
        private void OnChanged(object sender, FileSystemEventArgs e)
        {
            lock (timerLock)
            {
                if (debounceTimer != null) debounceTimer.Dispose();
                debounceTimer = new Timer(_ => 
                {
                    try 
                    { 
                        string content = File.ReadAllText(filePath);
                        if (onChange != null) onChange(content);
                    }
                    catch (IOException)
                    {
                        // Retry once on the next debounce if file is locked
                        lock (timerLock)
                        {
                            if (debounceTimer != null) debounceTimer.Change(300, Timeout.Infinite);
                        }
                    }
                    catch { }
                }, null, 300, Timeout.Infinite);
            }
        }
    }
}
