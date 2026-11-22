using System;
using System.IO;
using System.Linq;
using codeLess.Studio;
using Xunit;

namespace codeLess.Avalonia.Tests
{
    /// <summary>v3.3.3 report: "There is no remove from Workspace option if I add a folder to the workspace."</summary>
    public class WorkspaceFoldersCoreTests : IDisposable
    {
        private readonly string root = Path.Combine(Path.GetTempPath(), "cl_wsf_" + Guid.NewGuid().ToString("N").Substring(0, 8));

        public WorkspaceFoldersCoreTests() { Directory.CreateDirectory(root); }
        public void Dispose() { try { Directory.Delete(root, true); } catch { } }

        private string Dir(string name) { string d = Path.Combine(root, name); Directory.CreateDirectory(d); return d; }

        [Fact]
        public void Extra_Folders_Are_Remembered_Per_Primary_Folder()
        {
            string a = Dir("a"), b = Dir("b"), x = Dir("x"), y = Dir("y");
            var s = new IdePersistentSettings();
            WorkspaceFolders.Store(s, a, new[] { x, y });
            WorkspaceFolders.Store(s, b, new[] { y });
            Assert.Equal(new[] { x, y }, WorkspaceFolders.Load(s, a).ToArray());
            Assert.Equal(new[] { y }, WorkspaceFolders.Load(s, b).ToArray());
            Assert.Empty(WorkspaceFolders.Load(s, x));
            WorkspaceFolders.Store(s, a, new string[0]); // forget
            Assert.Empty(WorkspaceFolders.Load(s, a));
            Assert.Equal(new[] { y }, WorkspaceFolders.Load(s, b).ToArray());
            Directory.Delete(y);
            Assert.Empty(WorkspaceFolders.Load(s, b)); // vanished folders are skipped
        }

        [Fact]
        public void Removing_An_Extra_Or_The_Primary_Folder()
        {
            string a = Dir("a"), x = Dir("x"), y = Dir("y");
            Assert.True(WorkspaceFolders.Remove(a, new[] { x, y }, x, out string p1, out var e1));
            Assert.Equal(a, p1);
            Assert.Equal(new[] { y }, e1.ToArray());

            Assert.True(WorkspaceFolders.Remove(a, new[] { x, y }, a + Path.DirectorySeparatorChar, out string p2, out var e2));
            Assert.Equal(x, p2); // the next folder becomes the primary one
            Assert.Equal(new[] { y }, e2.ToArray());

            Assert.True(WorkspaceFolders.Remove(a, new string[0], a, out string p3, out var e3));
            Assert.Null(p3);
            Assert.False(WorkspaceFolders.Remove(a, new[] { x }, Dir("other"), out _, out _));
        }
    }
}
