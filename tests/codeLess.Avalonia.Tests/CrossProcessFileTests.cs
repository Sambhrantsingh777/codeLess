using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Xunit;
using codeLess.Studio;

namespace codeLess.AvaloniaApp.Tests
{
    [Collection("SequentialWorkbench")]
    public class CrossProcessFileTests : IDisposable
    {
        private readonly string _tempDir;

        public CrossProcessFileTests()
        {
            _tempDir = Path.Combine(Path.GetTempPath(), "test-cpfile-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_tempDir);
        }

        public void Dispose()
        {
            try { if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, true); } catch { }
        }

        [Fact]
        public void ReadModifyWrite_Concurrency()
        {
            string path = Path.Combine(_tempDir, "concurrency.txt");
            File.WriteAllText(path, "0");

            int increments = 50;
            var t1 = Task.Run(() =>
            {
                for (int i = 0; i < increments; i++)
                {
                    CrossProcessFile.ReadModifyWrite(path, _ =>
                    {
                        int v = int.Parse(File.ReadAllText(path));
                        CrossProcessFile.AtomicWrite(path, (v + 1).ToString());
                    });
                }
            });
            var t2 = Task.Run(() =>
            {
                for (int i = 0; i < increments; i++)
                {
                    CrossProcessFile.ReadModifyWrite(path, _ =>
                    {
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
            string path = Path.Combine(_tempDir, "stale.txt");
            string lockPath = path + ".lock";
            File.WriteAllText(lockPath, "locked");

            File.WriteAllText(path, "0");

            var sw = System.Diagnostics.Stopwatch.StartNew();
            CrossProcessFile.ReadModifyWrite(path, _ =>
            {
                CrossProcessFile.AtomicWrite(path, "1");
            }, 500);
            sw.Stop();

            Assert.Equal("1", File.ReadAllText(path));
            Assert.True(sw.ElapsedMilliseconds >= 450, "Should have waited for timeout");
        }
    }
}
