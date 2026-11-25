using System;
using System.IO;
using System.Threading;
using Xunit;
using codeLess.Core;

namespace codeLess.Avalonia.Tests
{
    public class KeybindingsFileWatcherTests
    {
        [Fact]
        public void KeybindingsWatcher_DetectsFileReplaceAndRename()
        {
            string dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
            Directory.CreateDirectory(dir);
            string file = Path.Combine(dir, "keybindings.json");
            File.WriteAllText(file, "[]");

            bool fired = false;
            string content = null;
            using (var watcher = new KeybindingsFileWatcher(file, c => { fired = true; content = c; }))
            {
                // Wait for init
                Thread.Sleep(200);

                string tmp = Path.Combine(dir, "tmp.json");
                File.WriteAllText(tmp, "[ { \"key\": \"ctrl+a\", \"command\": \"test\" } ]");
                
                File.Replace(tmp, file, null);

                // Polling wait
                int limit = 100; // 5 seconds
                while (!fired && limit > 0)
                {
                    Thread.Sleep(50);
                    limit--;
                }

                Assert.True(fired);
                Assert.Contains("ctrl+a", content);
            }
            Directory.Delete(dir, true);
        }
    }
}
