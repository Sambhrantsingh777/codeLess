using System.Linq;
using Avalonia.Headless.XUnit;
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Xunit;
using codeLess.Studio;

namespace codeLess.Avalonia.Tests
{
    public class M2M3M4MissingTests : IDisposable
    {
        private string testDir;
        private IdePersistentSettings testSettings;
        private LlmHarnessAndAgentEngine engine;

        public M2M3M4MissingTests()
        {
            testDir = Path.Combine(Path.GetTempPath(), "m2m3tests_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(testDir);
            testDir = System.IO.Path.GetFullPath(testDir);
            TestTrust.Trust(testDir);
            testSettings = new IdePersistentSettings { RequireAgentToolApproval = false };
            engine = new LlmHarnessAndAgentEngine(testSettings);

        }

        public void Dispose()
        {
            if (Directory.Exists(testDir))
                Directory.Delete(testDir, true);
        }

        private AgentToolCallResult RunTool(string toolName, Dictionary<string, object> args)
        {
            return engine.ExecuteWorkspaceTool(toolName, args, testDir, System.IO.Directory.Exists(testDir) ? new System.Collections.Generic.List<string>(System.IO.Directory.GetFiles(testDir, "*", System.IO.SearchOption.AllDirectories)) : new System.Collections.Generic.List<string>(), null, new Dictionary<string, string>(), "build", testSettings);
        }

        [Fact]
        public void ReadFormat_Test() 
        { 
            File.WriteAllText(Path.Combine(testDir, "test.txt"), "line1\nline2\nline3");
            var res = RunTool("read_file", new Dictionary<string, object>{{"path", "test.txt"}});
            Assert.Contains("00001| line1", res.Output);
            Assert.Contains("00003| line3", res.Output);
        }

        [Fact]
        public void ReadFormat_Footer() 
        { 
            File.WriteAllText(Path.Combine(testDir, "test.txt"), "line1");
            var res = RunTool("read_file", new Dictionary<string, object>{{"path", "test.txt"}});
            Assert.Contains("total 1 lines", res.Output);
        }

        [Fact]
        public void ReadFormat_LineCap() 
        { 
            string text = new string('a', 2050);
            File.WriteAllText(Path.Combine(testDir, "test.txt"), text);
            var res = RunTool("read_file", new Dictionary<string, object>{{"path", "test.txt"}});
            Assert.Contains("...", res.Output);
        }

        [Fact]
        public void ReadFormat_OffsetLimit() 
        { 
            File.WriteAllText(Path.Combine(testDir, "test.txt"), "l1\nl2\nl3\nl4");
            var res = RunTool("read_file", new Dictionary<string, object>{{"path", "test.txt"}, {"offset", 1}, {"limit", 2}});
            Assert.DoesNotContain("00001|", res.Output);
            Assert.Contains("00002| l2", res.Output);
            Assert.Contains("00003| l3", res.Output);
            Assert.DoesNotContain("00004|", res.Output);
        }

        [Fact]
        public void ReadBeforeEdit_Enforced() 
        { 
            File.WriteAllText(Path.Combine(testDir, "test.txt"), "line1");
            var res = RunTool("edit_file", new Dictionary<string, object>{{"path", "test.txt"}, {"search", "line1"}, {"replace", "line2"}});
            Assert.Contains("You must read", res.Output);
        }

        [Fact]
        public void ModifiedSinceRead_Enforced() 
        { 
            File.WriteAllText(Path.Combine(testDir, "test.txt"), "line1");
            RunTool("read_file", new Dictionary<string, object>{{"path", "test.txt"}});
            
            File.WriteAllText(Path.Combine(testDir, "test.txt"), "line1\nmodified");
            var res = RunTool("edit_file", new Dictionary<string, object>{{"path", "test.txt"}, {"search", "line1"}, {"replace", "line2"}});
            Assert.Contains("modified since", res.Output);
        }

        [Fact]
        public void StagedEditsCountAsReads() 
        { 
            var engine = new LlmHarnessAndAgentEngine(testSettings);

            var staged = new Dictionary<string, string>();
            staged[Path.Combine(testDir, "test.txt")] = "line1";
            var args = new Dictionary<string, object>{{"path", "test.txt"}, {"search", "line1"}, {"replace", "line2"}};
            var res = engine.ExecuteWorkspaceTool("edit_file", args, testDir, System.IO.Directory.Exists(testDir) ? new System.Collections.Generic.List<string>(System.IO.Directory.GetFiles(testDir, "*", System.IO.SearchOption.AllDirectories)) : new System.Collections.Generic.List<string>(), null, staged, "build", testSettings);
            Assert.DoesNotContain("You must read", res.Output);
        }
        
        [Fact]
        public void GrepOutput_FormatAndLimit() 
        { 
            string matches = "";
            for(int i = 0; i < 150; i++) matches += "match\n";
            File.WriteAllText(Path.Combine(testDir, "test.txt"), matches);
            var res = RunTool("grep_search", new Dictionary<string, object>{{"query", "match"}, {"path", "test.txt"}});
            Assert.Contains("Found 150 matches", res.Output);
            Assert.Contains("Results are truncated", res.Output);
            
            // Assert the output contains at most 100 match lines
            int matchLineCount = res.Output.Split('\n').Count(l => l.Contains("Line "));
            Assert.True(matchLineCount <= 100, "Output contained " + matchLineCount + " match lines, expected <= 100.");
            Assert.Equal(100, matchLineCount);
        }

        [Fact]
        public void ListLimit_Enforced() 
        { 
            for(int i=0; i<300; i++) File.WriteAllText(Path.Combine(testDir, "test" + i + ".txt"), "a");
            var res = RunTool("list_dir", new Dictionary<string, object>{{"path", ""}});
            Assert.Contains("Results are truncated", res.Output);
        }
        
        [Fact]
        public void ToolName_Repair() 
        { 
            File.WriteAllText(Path.Combine(testDir, "test.txt"), "l1\nl2\nl3\nl4");
            var res = RunTool("read", new Dictionary<string, object>{{"path", "test.txt"}}); // testing alias
            Assert.Contains("00001|", res.Output);
        }

        [Fact]
        public void TerminalTruncation_Works() 
        { 
            var res = RunTool("run_terminal", new System.Collections.Generic.Dictionary<string, object>{{"command", "echo " + new string('A', 40000)}});
            Xunit.Assert.Contains("chars truncated", res.Output);
        }

        [Fact]
        public void DidYouMean_Works() 
        { 
            var res = RunTool("reed_file", new System.Collections.Generic.Dictionary<string, object>{{"path", "test.txt"}});
            Xunit.Assert.Contains("Did you mean 'read_file'?", res.Output);
        }

        [Fact]
        public void GlobTruncation_Works() 
        { 
            for(int i=0; i<150; i++) System.IO.File.WriteAllText(System.IO.Path.Combine(testDir, "glob" + i + ".txt"), "a");
            var res = RunTool("glob", new System.Collections.Generic.Dictionary<string, object>{{"pattern", "*.txt"}});
            Xunit.Assert.Contains("Results are truncated", res.Output);
        }

        [Fact]
        public void BinaryDetection_Works() 
        { 
            System.IO.File.WriteAllBytes(System.IO.Path.Combine(testDir, "bin.dat"), new byte[]{ 0, 1, 2, 3 });
            var res = RunTool("read_file", new System.Collections.Generic.Dictionary<string, object>{{"path", "bin.dat"}});
            Xunit.Assert.Contains("binary", res.Output);
        }

        [Fact]
        public void ModifiedSinceRead_SameMtimeDifferentContent_RejectedAcross_Edit_Multiedit_And_WriteFile()
        {
            // (1) read, then an external write of different content with the mtime forced back to the original
            // via File.SetLastWriteTimeUtc -> edit_file, multiedit, and write_file are all rejected with "modified since".
            string fEdit = Path.Combine(testDir, "stale_edit.txt");
            File.WriteAllText(fEdit, "alpha\nbeta\n");
            DateTime tEdit = File.GetLastWriteTimeUtc(fEdit);
            Assert.True(RunTool("read_file", new Dictionary<string, object> { { "path", "stale_edit.txt" } }).Success);

            // External edit of different content (both same-length and different-length) with mtime forced back to tEdit:
            File.WriteAllText(fEdit, "alpha\nbetX\n");
            File.SetLastWriteTimeUtc(fEdit, tEdit);
            Assert.Equal(tEdit, File.GetLastWriteTimeUtc(fEdit));
            var editRes = RunTool("edit_file", new Dictionary<string, object>
            {
                { "path", "stale_edit.txt" },
                { "search", "alpha" },
                { "replace", "ALPHA" }
            });
            Assert.False(editRes.Success);
            Assert.Contains("modified since", editRes.Output);

            string fMulti = Path.Combine(testDir, "stale_multi.txt");
            File.WriteAllText(fMulti, "one\ntwo\n");
            DateTime tMulti = File.GetLastWriteTimeUtc(fMulti);
            Assert.True(RunTool("read_file", new Dictionary<string, object> { { "path", "stale_multi.txt" } }).Success);

            File.WriteAllText(fMulti, "one\ntwo\nexternal_extra\n");
            File.SetLastWriteTimeUtc(fMulti, tMulti);
            Assert.Equal(tMulti, File.GetLastWriteTimeUtc(fMulti));
            var multiRes = RunTool("multiedit", new Dictionary<string, object>
            {
                { "path", "stale_multi.txt" },
                { "edits", new List<object> { new Dictionary<string, object> { { "oldString", "one" }, { "newString", "ONE" } } } }
            });
            Assert.False(multiRes.Success);
            Assert.Contains("modified since", multiRes.Output);

            string fWrite = Path.Combine(testDir, "stale_write.txt");
            File.WriteAllText(fWrite, "initial content\n");
            DateTime tWrite = File.GetLastWriteTimeUtc(fWrite);
            Assert.True(RunTool("read_file", new Dictionary<string, object> { { "path", "stale_write.txt" } }).Success);

            File.WriteAllText(fWrite, "externally changed\n");
            File.SetLastWriteTimeUtc(fWrite, tWrite);
            Assert.Equal(tWrite, File.GetLastWriteTimeUtc(fWrite));
            var writeRes = RunTool("write_file", new Dictionary<string, object>
            {
                { "path", "stale_write.txt" },
                { "content", "agent overwrite\n" }
            });
            Assert.False(writeRes.Success);
            Assert.Contains("modified since", writeRes.Output);
        }

        [Fact]
        public void ConsecutiveAgentEdits_AfterSingleRead_BothSucceed()
        {
            // (2) read, then the agent edits twice in a row: both succeed.
            string f = Path.Combine(testDir, "consecutive.txt");
            File.WriteAllText(f, "first\nsecond\n");
            var staged = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var files = new List<string> { f };

            var readRes = engine.ExecuteWorkspaceTool("read_file", new Dictionary<string, object> { { "path", "consecutive.txt" } }, testDir, files, null, staged, "build", testSettings);
            Assert.True(readRes.Success);

            var edit1 = engine.ExecuteWorkspaceTool("edit_file", new Dictionary<string, object>
            {
                { "path", "consecutive.txt" },
                { "search", "first" },
                { "replace", "FIRST" }
            }, testDir, files, null, staged, "build", testSettings);
            Assert.True(edit1.Success, edit1.Output);

            var edit2 = engine.ExecuteWorkspaceTool("edit_file", new Dictionary<string, object>
            {
                { "path", "consecutive.txt" },
                { "search", "FIRST\nsecond" },
                { "replace", "FIRST\nSECOND" }
            }, testDir, files, null, staged, "build", testSettings);
            Assert.True(edit2.Success, edit2.Output);
            Assert.Equal("FIRST\nSECOND\n", staged[f]);

            // Even if the agent's staged write is flushed to disk between consecutive edits, the next edit still succeeds:
            File.WriteAllText(f, staged[f]);
            var edit3 = engine.ExecuteWorkspaceTool("edit_file", new Dictionary<string, object>
            {
                { "path", "consecutive.txt" },
                { "search", "SECOND" },
                { "replace", "THIRD" }
            }, testDir, files, null, new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase), "build", testSettings);
            Assert.True(edit3.Success, edit3.Output);
        }

        [Fact]
        public void IdenticalContentRewrite_WithUnchangedMtime_IsAllowed()
        {
            // (3) read, then an identical-content rewrite with the mtime unchanged: allowed.
            string f = Path.Combine(testDir, "identical.txt");
            File.WriteAllText(f, "unchanged line\n");
            DateTime origMtime = File.GetLastWriteTimeUtc(f);

            Assert.True(RunTool("read_file", new Dictionary<string, object> { { "path", "identical.txt" } }).Success);

            File.WriteAllText(f, "unchanged line\n");
            File.SetLastWriteTimeUtc(f, origMtime);
            Assert.Equal(origMtime, File.GetLastWriteTimeUtc(f));

            var editRes = RunTool("edit_file", new Dictionary<string, object>
            {
                { "path", "identical.txt" },
                { "search", "unchanged line" },
                { "replace", "updated line" }
            });
            Assert.True(editRes.Success, editRes.Output);
            Assert.Contains("Patch applied cleanly.", editRes.Output);
        }
    }
}
