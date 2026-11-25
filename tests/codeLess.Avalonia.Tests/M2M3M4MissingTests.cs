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

        public M2M3M4MissingTests()
        {
            testDir = Path.Combine(Path.GetTempPath(), "m2m3tests_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(testDir);
            testSettings = new IdePersistentSettings();
        }

        public void Dispose()
        {
            if (Directory.Exists(testDir))
                Directory.Delete(testDir, true);
        }

        private AgentToolCallResult RunTool(string toolName, Dictionary<string, object> args)
        {
            var engine = new LlmHarnessAndAgentEngine(testSettings);
            return engine.ExecuteWorkspaceTool(toolName, args, testDir, new List<string>(), null, new Dictionary<string, string>(), "build", testSettings);
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
            Assert.Contains("test.txt", res.Output);
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
            var res = engine.ExecuteWorkspaceTool("edit_file", args, testDir, new List<string>(), null, staged, "build", testSettings);
            Assert.DoesNotContain("You must read", res.Output);
        }
        
        [Fact]
        public void GrepOutput_FormatAndLimit() 
        { 
            File.WriteAllText(Path.Combine(testDir, "test.txt"), "match\nmatch\nmatch\nmatch");
            var res = RunTool("grep_search", new Dictionary<string, object>{{"query", "match"}, {"path", "test.txt"}});
            Assert.Contains("4 matches found", res.Output);
        }

        [Fact]
        public void ListLimit_Enforced() 
        { 
            for(int i=0; i<300; i++) File.WriteAllText(Path.Combine(testDir, "test" + i + ".txt"), "a");
            var res = RunTool("list_dir", new Dictionary<string, object>{{"path", ""}});
            Assert.Contains("Showing first", res.Output);
        }
        
        [Fact]
        public void ToolName_Repair() 
        { 
            File.WriteAllText(Path.Combine(testDir, "test.txt"), "l1\nl2\nl3\nl4");
            var res = RunTool("read", new Dictionary<string, object>{{"path", "test.txt"}}); // testing alias
            Assert.Contains("00001|", res.Output);
        }
    }
}
