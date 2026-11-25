using System;
using System.IO;
using System.Threading.Tasks;
using Xunit;
using codeLess.Studio;

namespace codeLess.AvaloniaApp.Tests
{
    [Collection("SequentialWorkbench")]
    public class WorkspaceSessionStoreTests : IDisposable
    {
        private readonly string _prevSettingsDir;
        private readonly string _tempDir;

        public WorkspaceSessionStoreTests()
        {
            _prevSettingsDir = Environment.GetEnvironmentVariable("CODELESS_SETTINGS_DIR");
            _tempDir = Path.Combine(Path.GetTempPath(), "test-wssession-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_tempDir);
            Environment.SetEnvironmentVariable("CODELESS_SETTINGS_DIR", _tempDir);
        }

        public void Dispose()
        {
            Environment.SetEnvironmentVariable("CODELESS_SETTINGS_DIR", _prevSettingsDir);
            try { if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, true); } catch { }
        }

        [Fact]
        public void WorkspaceSessionStore_RoundTripSeveral()
        {
            string ws1 = Path.GetFullPath("/my/ws1");
            string ws2 = Path.GetFullPath("/my/ws2");
            var state1 = new WorkspaceSessionState();
            state1.OpenTabPaths.Add("/my/ws1/a.txt");
            state1.ActiveTabPath = "/my/ws1/a.txt";
            var state2 = new WorkspaceSessionState();
            state2.OpenTabPaths.Add("/my/ws2/b.txt");
            state2.OpenTabPaths.Add("/my/ws2/c.txt");
            state2.ActiveTabPath = "/my/ws2/c.txt";

            WorkspaceSessionStore.Save(ws1, state1);
            WorkspaceSessionStore.Save(ws2, state2);

            var l1 = WorkspaceSessionStore.Load(ws1);
            var l2 = WorkspaceSessionStore.Load(ws2);

            Assert.Single(l1.OpenTabPaths);
            Assert.Equal("/my/ws1/a.txt", l1.OpenTabPaths[0]);
            Assert.Equal("/my/ws1/a.txt", l1.ActiveTabPath);

            Assert.Equal(2, l2.OpenTabPaths.Count);
            Assert.Equal("/my/ws2/b.txt", l2.OpenTabPaths[0]);
            Assert.Equal("/my/ws2/c.txt", l2.ActiveTabPath);
        }

        [Fact]
        public void WorkspaceSessionStore_CorruptFile()
        {
            string ws = Path.GetFullPath("/my/corrupt");
            WorkspaceSessionStore.Save(ws, new WorkspaceSessionState());
            var d = Path.Combine(_tempDir, "workspaceStorage");
            var fpath = Directory.GetFiles(d)[0];
            File.WriteAllText(fpath, "{ bad json");

            var loaded = WorkspaceSessionStore.Load(ws);
            Assert.NotNull(loaded);
            Assert.Empty(loaded.OpenTabPaths);
        }

        [Fact]
        public void WorkspaceSessionStore_ConcurrentWriters()
        {
            string ws = Path.GetFullPath("/my/concurrent");
            var tasks = new Task[10];
            for (int i = 0; i < 10; i++)
            {
                int idx = i;
                tasks[i] = Task.Run(() =>
                {
                    var s = new WorkspaceSessionState();
                    s.OpenTabPaths.Add(idx.ToString());
                    WorkspaceSessionStore.Save(ws, s);
                });
            }
            Task.WaitAll(tasks);
            var loaded = WorkspaceSessionStore.Load(ws);
            Assert.Single(loaded.OpenTabPaths);
        }

        [Fact]
        public void WorkspaceSessionStore_MissingDirectory()
        {
            string ws = Path.GetFullPath("/my/missing");
            var loaded = WorkspaceSessionStore.Load(ws);
            Assert.NotNull(loaded);
            Assert.Empty(loaded.OpenTabPaths);
        }

        [Fact]
        public void WorkspaceSessionStore_SaveAndLoad()
        {
            string ws = Path.GetFullPath("/my/workspace/root");
            var state = new WorkspaceSessionState();
            state.OpenTabPaths.Add("/my/workspace/root/a.txt");
            state.OpenTabPaths.Add("/my/workspace/root/b.txt");
            state.ActiveTabPath = "/my/workspace/root/b.txt";

            WorkspaceSessionStore.Save(ws, state);

            // Same exact
            var loaded = WorkspaceSessionStore.Load(ws);
            Assert.Equal(2, loaded.OpenTabPaths.Count);
            Assert.Equal("/my/workspace/root/b.txt", loaded.ActiveTabPath);

            // Trailing slash
            var loaded2 = WorkspaceSessionStore.Load(ws + "/");
            Assert.Equal(2, loaded2.OpenTabPaths.Count);

            // Case insensitive
            var loaded3 = WorkspaceSessionStore.Load(ws.ToUpperInvariant());
            Assert.Equal(2, loaded3.OpenTabPaths.Count);
        }
    }
}
