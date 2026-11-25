using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Xunit;
using codeLess.Studio;

namespace codeLess.Avalonia.Tests
{
    public class M6EnginePermissionsTests : IDisposable
    {
        private string testDir;

        public M6EnginePermissionsTests()
        {
            testDir = Path.Combine(Path.GetTempPath(), "m6enginetests_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(testDir);
        }

        public void Dispose()
        {
            if (Directory.Exists(testDir))
                Directory.Delete(testDir, true);
        }

        private AgentToolCallResult RunTool(string mode, string toolName, Dictionary<string, object> args, Func<string, string, bool> onAsk = null, IdePersistentSettings customSettings = null)
        {
            var settings = customSettings ?? new IdePersistentSettings { RequireAgentToolApproval = false };
            var engine = new LlmHarnessAndAgentEngine(settings);
            if (onAsk != null) engine.OnRequestToolApproval = onAsk;
            return engine.ExecuteWorkspaceTool(toolName, args, testDir, new List<string>(), null, new Dictionary<string, string>(), mode, settings);
        }

        [Fact]
        public void Engine_PlanMode_WriteFile_Denied_NothingStaged()
        {
            var args = new Dictionary<string, object>{{"path", "new.txt"}, {"content", "hello"}};
            var res = RunTool("plan", "write_file", args);
            Assert.Contains("Permission Denied", res.Summary);
        }

        [Fact]
        public void Engine_PlanMode_GitStatus_RunsWithoutApproval()
        {
            bool asked = false;
            var args = new Dictionary<string, object>{{"command", "git status"}};
            var res = RunTool("plan", "bash", args, (t, j) => { asked = true; return true; });
            Assert.False(asked);
        }

        [Fact]
        public void Engine_PlanMode_NpmInstall_Asks()
        {
            bool asked = false;
            var args = new Dictionary<string, object>{{"command", "npm install"}};
            var res = RunTool("plan", "bash", args, (t, j) => { asked = true; return false; });
            Assert.True(asked);
            Assert.Contains("Approval Denied", res.Summary);
        }

        [Fact]
        public void Engine_BashPattern_Deny_IsDenied()
        {
            var settings = new IdePersistentSettings { RequireAgentToolApproval = false };
            
            settings.AgentPermissionBash = new Dictionary<string, string> { { "rm -rf*", "deny" } };
            
            bool asked = false;
            var args = new Dictionary<string, object>{{"command", "rm -rf /"}};
            var res = RunTool("build", "bash", args, (t, j) => { asked = true; return true; }, settings);
            Assert.False(asked);
            Assert.Contains("Permission Denied", res.Summary);
        }

        [Fact]
        public void Todos_Format_RoundTrip_And_Event()
        {
            var settings = new IdePersistentSettings();
            var engine = new LlmHarnessAndAgentEngine(settings);
            
            bool eventRaised = false;
            engine.OnTodosChanged = () => eventRaised = true;
            
            var args = new Dictionary<string, object>{
                {"todos", new object[] {
                    new Dictionary<string, object> { {"id", "1"}, {"content", "task1"}, {"status", "pending"}, {"priority", "high"} }
                }}
            };
            
            var res = engine.ExecuteWorkspaceTool("todowrite", args, testDir, new List<string>(), null, new Dictionary<string, string>(), "build", settings);
            Assert.True(eventRaised);
            Assert.Contains("Todos updated", res.Output);
            
            var resRead = engine.ExecuteWorkspaceTool("todoread", new Dictionary<string, object>(), testDir, new List<string>(), null, new Dictionary<string, string>(), "build", settings);
            Assert.Contains("task1", resRead.Output);
            Assert.False(File.Exists(Path.Combine(testDir, ".codeless", "todo.md"))); 
        }
    }
}
