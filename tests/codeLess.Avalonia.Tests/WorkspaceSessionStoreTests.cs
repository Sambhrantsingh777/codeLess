using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Xunit;
using codeLess.Studio;

namespace codeLess.AvaloniaApp.Tests
{
    public class WorkspaceSessionStoreTests
    {
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
