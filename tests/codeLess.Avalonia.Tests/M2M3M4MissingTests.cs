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
            File.WriteAllText(Path.Combine(testDir, "test.txt"), "match\nmatch\nmatch\nmatch");
            var res = RunTool("grep_search", new Dictionary<string, object>{{"query", "match"}, {"path", "test.txt"}});
            Assert.Contains("Found 4 matches", res.Output);
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
    }
}
