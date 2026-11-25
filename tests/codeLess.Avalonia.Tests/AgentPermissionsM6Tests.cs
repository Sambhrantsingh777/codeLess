using System;
using System.Collections.Generic;
using codeLess.Studio;
using Xunit;

namespace codeLess.Avalonia.Tests
{
    public class AgentPermissionsM6Tests
    {
        [Fact]
        public void EvaluateTool_PlanMode_DeniesWrite()
        {
            var settings = new IdePersistentSettings { RequireAgentToolApproval = false };
            Assert.Equal("deny", AgentPermissions.EvaluateTool("plan", "write_file", "", false, settings));
            Assert.Equal("allow", AgentPermissions.EvaluateTool("build", "write_file", "", false, settings));
        }

        [Fact]
        public void EvaluateTool_PlanMode_AllowsReadOnlyBash()
        {
            var settings = new IdePersistentSettings { RequireAgentToolApproval = false };
            string args = "{\"command\": \"git diff\"}";
            Assert.Equal("allow", AgentPermissions.EvaluateTool("plan", "bash", args, false, settings));
            
            string args2 = "{\"command\": \"rm -rf /\"}";
            Assert.Equal("ask", AgentPermissions.EvaluateTool("plan", "bash", args2, false, settings));
        }

        [Fact]
        public void EvaluateTool_BashWildcards_MergeStricter()
        {
            var settings = new IdePersistentSettings 
            { 
                RequireAgentToolApproval = false,
                AgentPermissionBash = new Dictionary<string, string> { { "git push*", "ask" }, { "rm -rf*", "deny" }, { "*", "allow" } }
            };
            
            Assert.Equal("ask", AgentPermissions.EvaluateTool("build", "bash", "{\"command\": \"git push origin\"}", false, settings));
            Assert.Equal("deny", AgentPermissions.EvaluateTool("build", "bash", "{\"command\": \"rm -rf /tmp\"}", false, settings));
            Assert.Equal("allow", AgentPermissions.EvaluateTool("build", "bash", "{\"command\": \"echo hello\"}", false, settings));
        }
    }
}
