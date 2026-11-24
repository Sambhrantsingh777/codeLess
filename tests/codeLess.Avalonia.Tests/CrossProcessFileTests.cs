using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Xunit;
using codeLess.Studio;

namespace codeLess.AvaloniaApp.Tests
{
    public class CrossProcessFileTests
    {
        [Fact]
        public void ReadModifyWrite_Concurrency()
        {
            string path = Path.Combine(Path.GetTempPath(), "test-concurrency-" + Guid.NewGuid().ToString("N") + ".txt");
            File.WriteAllText(path, "0");

            int increments = 50;
            var t1 = Task.Run(() => {
                for(int i = 0; i < increments; i++) {
                    CrossProcessFile.ReadModifyWrite(path, _ => {
                        int v = int.Parse(File.ReadAllText(path));
                        CrossProcessFile.AtomicWrite(path, (v + 1).ToString());
                    });
                }
            });
            var t2 = Task.Run(() => {
                for(int i = 0; i < increments; i++) {
                    CrossProcessFile.ReadModifyWrite(path, _ => {
                        int v = int.Parse(File.ReadAllText(path));
                        CrossProcessFile.AtomicWrite(path, (v + 1).ToString());
                    });
                }
            });

            Task.WaitAll(t1, t2);

            int final = int.Parse(File.ReadAllText(path));
            Assert.Equal(increments * 2, final);
        }

        [Fact]
        public void ReadModifyWrite_StaleLock()
        {
            string path = Path.Combine(Path.GetTempPath(), "test-stale-" + Guid.NewGuid().ToString("N") + ".txt");
            string lockPath = path + ".lock";
            File.WriteAllText(lockPath, "locked"); // simulate stale lock

            File.WriteAllText(path, "0");

            var sw = System.Diagnostics.Stopwatch.StartNew();
            CrossProcessFile.ReadModifyWrite(path, _ => {
                CrossProcessFile.AtomicWrite(path, "1");
            }, 500); // 500ms timeout
            sw.Stop();

            Assert.Equal("1", File.ReadAllText(path));
            Assert.True(sw.ElapsedMilliseconds >= 500, "Should have waited for timeout");
        }
    }
}
