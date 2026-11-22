using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using codeLess.Studio;
using Xunit;

namespace codeLess.Avalonia.Tests
{
    /// <summary>
    /// v3.3.3 reports: an empty new file asked to be saved on close; Save As proposed ".cs" regardless of the
    /// workspace; terminals stayed in the old folder after File → Open Folder.
    /// </summary>
    public class UntitledAndTerminalFollowCoreTests
    {
        private static string NewDir()
        {
            string d = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "cl_untitled_" + Guid.NewGuid().ToString("N").Substring(0, 8)));
            Directory.CreateDirectory(d);
            return d;
        }

        private static void Touch(string root, string rel)
        {
            string p = Path.Combine(root, rel.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(p));
            File.WriteAllText(p, "x");
        }

        [Fact]
        public void Workspace_Extension_Comes_From_The_Main_Code_Language()
        {
            string d = NewDir();
            try
            {
                Touch(d, "app/main.py"); Touch(d, "app/util.py"); Touch(d, "app/models.py");
                Touch(d, "README.md"); Touch(d, "docs/a.md"); Touch(d, "docs/b.md"); Touch(d, "docs/c.md"); Touch(d, "docs/d.md");
                Touch(d, "tools/x.sh");
                Touch(d, "node_modules/lib/a.js"); Touch(d, "node_modules/lib/b.js"); Touch(d, "node_modules/lib/c.js"); Touch(d, "node_modules/lib/d.js");
                UntitledFileDefaults.ResetCache();
                Assert.Equal("py", UntitledFileDefaults.DetectWorkspaceExtension(d, null)); // docs and node_modules don't decide
                UntitledFileDefaults.ResetCache();
                Assert.Equal("ts", UntitledFileDefaults.DetectWorkspaceExtension(d, new List<string> { "a.ts", "b.ts", "c.tsx", "x.json" }));
            }
            finally { UntitledFileDefaults.ResetCache(); try { Directory.Delete(d, true); } catch { } }
        }

        [Fact]
        public void Docs_Only_Or_No_Workspace_Falls_Back_Sensibly()
        {
            string d = NewDir();
            try
            {
                Touch(d, "a.md"); Touch(d, "b.md"); Touch(d, "c.json");
                UntitledFileDefaults.ResetCache();
                Assert.Equal("md", UntitledFileDefaults.DetectWorkspaceExtension(d, null));
                UntitledFileDefaults.ResetCache();
                Assert.Equal("txt", UntitledFileDefaults.DetectWorkspaceExtension(null, null));
                Assert.Equal("txt", UntitledFileDefaults.DetectWorkspaceExtension(Path.Combine(d, "missing"), null));
            }
            finally { UntitledFileDefaults.ResetCache(); try { Directory.Delete(d, true); } catch { } }
        }

        [Fact]
        public void Save_Name_Uses_The_Language_Mode_Then_The_Workspace()
        {
            string d = NewDir();
            try
            {
                Touch(d, "a.go"); Touch(d, "b.go");
                UntitledFileDefaults.ResetCache();
                var t = new EditorTabBuffer { FilePath = "", FileName = "Untitled-2", IsUntitled = true, Language = "" };
                Assert.Equal("Untitled-2.go", UntitledFileDefaults.SuggestSaveFileName(t, d, null));
                t.Language = "python";
                Assert.Equal("Untitled-2.py", UntitledFileDefaults.SuggestSaveFileName(t, d, null));
                t.Language = "rs";
                Assert.Equal("Untitled-2.rs", UntitledFileDefaults.SuggestSaveFileName(t, d, null));
                var saved = new EditorTabBuffer { FilePath = Path.Combine(d, "x.cs"), FileName = "x.cs", Language = "cs" };
                Assert.Equal("x.cs", UntitledFileDefaults.SuggestSaveFileName(saved, d, null)); // Save As of a real file keeps its name
            }
            finally { UntitledFileDefaults.ResetCache(); try { Directory.Delete(d, true); } catch { } }
        }

        [Fact]
        public void Untitled_Names_Skip_Open_Ones()
        {
            Assert.Equal("Untitled-1", UntitledFileDefaults.NextUntitledName(new[] { "a.py" }));
            Assert.Equal("Untitled-3", UntitledFileDefaults.NextUntitledName(new[] { "Untitled-1", "Untitled-2.cs" }));
        }

        [Fact]
        public void Empty_Untitled_Never_Needs_A_Save_Prompt()
        {
            var t = new EditorTabBuffer { FilePath = "", FileName = "Untitled-1", IsUntitled = true, Content = "", SavedContent = "" };
            t.SyncLinesFromContent();
            t.IsDirty = true; // e.g. typed and deleted again
            Assert.False(UntitledFileDefaults.NeedsSavePrompt(t));
            t.Content = "print(1)"; t.SyncLinesFromContent(); t.IsDirty = true;
            Assert.True(UntitledFileDefaults.NeedsSavePrompt(t));
            var real = new EditorTabBuffer { FilePath = "/tmp/x.txt", FileName = "x.txt", Content = "", SavedContent = "a" };
            real.SyncLinesFromContent(); real.IsDirty = true;
            Assert.True(UntitledFileDefaults.NeedsSavePrompt(real)); // emptying a real file is a change worth saving
        }

        [Fact]
        public void Untitled_Editors_Get_The_Grammar_Of_Their_Language()
        {
            Assert.Equal("Untitled-1.py", EditorSyntaxSession.SyntaxPathFor(new EditorTabBuffer { FilePath = "", FileName = "Untitled-1", Language = "py" }));
            Assert.Equal("Untitled-1", EditorSyntaxSession.SyntaxPathFor(new EditorTabBuffer { FilePath = "", FileName = "Untitled-1", Language = "txt" }));
            Assert.Equal("/a/b.cs", EditorSyntaxSession.SyntaxPathFor(new EditorTabBuffer { FilePath = "/a/b.cs", FileName = "b.cs", Language = "py" }));
        }

        private static bool WaitUntil(Func<bool> cond, int ms)
        {
            var end = DateTime.UtcNow.AddMilliseconds(ms);
            while (DateTime.UtcNow < end) { if (cond()) return true; Thread.Sleep(100); }
            return cond();
        }

        [Fact]
        public void Idle_Shell_Restarts_In_The_New_Folder_And_A_Busy_One_Is_Left_Alone()
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows) || !File.Exists("/bin/bash")) return;
            string a = NewDir(), b = NewDir(), c = NewDir();
            File.WriteAllText(Path.Combine(b, "marker_b"), "");
            var s = new ConPtyTerminalSession("bash", "/bin/bash --noprofile --norc", a, 100, 30);
            try
            {
                Assert.True(WaitUntil(() => s.IsShellForegroundIdle(), 8000));
                Assert.Equal("respawned", s.FollowWorkspaceFolder(b));
                Assert.True(WaitUntil(() => !s.HasExited && File.Exists(Path.Combine(s.GetLiveWorkingDirectory() ?? "", "marker_b")), 10000),
                    "live cwd: " + s.GetLiveWorkingDirectory());
                Assert.Equal("same", s.FollowWorkspaceFolder(b));

                s.WriteInput("sleep 20\r");
                Assert.True(WaitUntil(() => !s.IsShellForegroundIdle(), 8000), "sleep never became the foreground job");
                Assert.Equal("busy", s.FollowWorkspaceFolder(c));
                Assert.True(File.Exists(Path.Combine(s.GetLiveWorkingDirectory() ?? "", "marker_b"))); // untouched
            }
            finally
            {
                try { s.Dispose(); } catch { }
                foreach (var d in new[] { a, b, c }) try { Directory.Delete(d, true); } catch { }
            }
        }
    }
}
