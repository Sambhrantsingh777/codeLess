using Avalonia.Headless.XUnit;
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
            testDir = System.IO.Path.GetFullPath(testDir);
            TestTrust.Trust(testDir);
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
        public void Engine_RestrictedMode_MakesAllowIntoAsk()
        {
            var settings = new IdePersistentSettings { RequireAgentToolApproval = false };
            var untrustedDir = Path.Combine(Path.GetTempPath(), "m6_untrusted_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(untrustedDir);
            untrustedDir = Path.GetFullPath(untrustedDir);
            
            string untrustedFile = Path.Combine(untrustedDir, "test.txt");
            File.WriteAllText(untrustedFile, "content");
            string trustedFile = Path.Combine(testDir, "test.txt");
            File.WriteAllText(trustedFile, "content");
            
            try
            {
                var engine = new LlmHarnessAndAgentEngine(settings);
                bool asked = false;
                engine.OnRequestToolApproval = (t, j) => { asked = true; return false; }; // deny
                
                // Control: trusted dir does not ask
                var resTrusted = engine.ExecuteWorkspaceTool("read_file", new Dictionary<string, object> { { "path", "test.txt" } }, testDir, new List<string>(), null, new Dictionary<string, string>(), "build", settings);
                Assert.False(asked, "Should not ask in trusted dir");
                Assert.True(resTrusted.Success, "Should succeed in trusted dir");
                
                // Variable: untrusted dir DOES ask
                var resUntrusted = engine.ExecuteWorkspaceTool("read_file", new Dictionary<string, object> { { "path", "test.txt" } }, untrustedDir, new List<string>(), null, new Dictionary<string, string>(), "build", settings);
                Assert.True(asked, "Should ask in untrusted dir");
                Assert.Contains("Approval Denied", resUntrusted.Summary);
                Assert.False(resUntrusted.Success);
            }
            finally
            {
                if (Directory.Exists(untrustedDir)) Directory.Delete(untrustedDir, true);
            }
        }

        [Fact]
        public void Todos_Format_ValidationErrors()
        {
            var settings = new codeLess.Studio.IdePersistentSettings { RequireAgentToolApproval = false };
            var engine = new codeLess.Studio.LlmHarnessAndAgentEngine(settings);
            
            var args = new System.Collections.Generic.Dictionary<string, object>{
                {"todos", new object[] {
                    new System.Collections.Generic.Dictionary<string, object> { {"id", "1"}, {"content", "task1"}, {"status", "invalid_status"}, {"priority", "high"} }
                }}
            };
            
            var res = engine.ExecuteWorkspaceTool("todowrite", args, testDir, new System.Collections.Generic.List<string>(), null, new System.Collections.Generic.Dictionary<string, string>(), "build", settings);
            
            Assert.False(res.Success);
            Assert.Contains("status", res.Output);
            Assert.Contains("pending", res.Output);
            Assert.Contains("in_progress", res.Output);

            var args2 = new System.Collections.Generic.Dictionary<string, object>{
                {"todos", new object[] {
                    new System.Collections.Generic.Dictionary<string, object> { {"id", "1"}, {"content", "task1"}, {"status", "pending"}, {"priority", "invalid_prio"} }
                }}
            };
            var res2 = engine.ExecuteWorkspaceTool("todowrite", args2, testDir, new System.Collections.Generic.List<string>(), null, new System.Collections.Generic.Dictionary<string, string>(), "build", settings);
            Assert.False(res2.Success);
            Assert.Contains("priority", res2.Output);
            Assert.Contains("high", res2.Output);
            Assert.Contains("medium", res2.Output);
            Assert.Contains("low", res2.Output);
            
            var resRead = engine.ExecuteWorkspaceTool("todoread", new System.Collections.Generic.Dictionary<string, object>(), testDir, new System.Collections.Generic.List<string>(), null, new System.Collections.Generic.Dictionary<string, string>(), "build", settings);
            Assert.DoesNotContain("task1", resRead.Output);
        }

        [Fact]
        public void Todos_Format_RoundTrip_And_Event()
        {
            var settings = new IdePersistentSettings { RequireAgentToolApproval = false };
            var engine = new LlmHarnessAndAgentEngine(settings);
            
            bool eventRaised = false;
            engine.OnTodosChanged = () => eventRaised = true;
            
            var args = new Dictionary<string, object>{
                {"todos", new object[] {
                    new Dictionary<string, object> { {"id", "1"}, {"content", "task1"}, {"status", "pending"}, {"priority", "high"} }
                }}
            };
            
            var res = engine.ExecuteWorkspaceTool("todowrite", args, testDir, new List<string>(), null, new Dictionary<string, string>(), "build", settings);
            Assert.True(eventRaised, "Output was: " + res.Output);
            
            var resRead = engine.ExecuteWorkspaceTool("todoread", new Dictionary<string, object>(), testDir, new List<string>(), null, new Dictionary<string, string>(), "build", settings);
            Assert.Contains("task1", resRead.Output);
            Assert.False(File.Exists(Path.Combine(testDir, ".codeless", "todo.md"))); 
        }

        [Fact]
        public void Engine_PlanMode_SearchReplace_Denied_NothingStaged()
        {
            var settings = new IdePersistentSettings { RequireAgentToolApproval = false };
            var untrustedDir = Path.Combine(Path.GetTempPath(), "m6_sr_untrusted_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(untrustedDir);
            untrustedDir = Path.GetFullPath(untrustedDir);
            
            try
            {
                var engine = new LlmHarnessAndAgentEngine(settings);
                var activeTab = new EditorTabBuffer();
                activeTab.FilePath = Path.Combine(untrustedDir, "test.txt");
                File.WriteAllText(activeTab.FilePath, "hello\nworld\n");
                activeTab.Content = "hello\nworld\n";
                
                string llmOutput = "```test.txt\n<<<<<<< SEARCH\nworld\n=======\nmars\n>>>>>>> REPLACE\n```";
                var res = new AgentExecutionTurnResult();
                
                object[] args = new object[] { llmOutput, activeTab, untrustedDir, res, null, "plan", settings, new List<string>() };
                var method = engine.GetType().GetMethod("TryExtractAndStageSearchReplaceBlocks", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                bool success = (bool)method.Invoke(engine, args);
                string errorDetails = (string)args[4];
                
                Assert.False(success);
                Assert.Contains("Use todowrite", errorDetails);
                Assert.Empty(res.StagedFileContents);
            }
            finally
            {
                if (Directory.Exists(untrustedDir)) Directory.Delete(untrustedDir, true);
            }
        }
    }
}
