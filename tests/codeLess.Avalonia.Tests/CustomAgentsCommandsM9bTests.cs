using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using codeLess.Studio;
using Xunit;

namespace codeLess.Avalonia.Tests
{
    public class CustomAgentsCommandsM9bTests : IDisposable
    {
        private readonly List<string> _tempDirs = new List<string>();

        private string CreateTempDir()
        {
            string dir = Path.Combine(Path.GetTempPath(), "codeless-m9b-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            dir = Path.GetFullPath(dir);
            TestTrust.Trust(dir);
            _tempDirs.Add(dir);
            return dir;
        }

        public void Dispose()
        {
            foreach (string dir in _tempDirs)
            {
                try
                {
                    if (Directory.Exists(dir))
                        Directory.Delete(dir, true);
                }
                catch { }
            }
            _tempDirs.Clear();
        }

        [Fact]
        public void CustomAgentParser_ParsesYamlFrontmatter_And_CachesByMtime()
        {
            CustomAgentAndCommandRegistry.ClearCache();
            string ws = CreateTempDir();
            string agentDir = Path.Combine(ws, ".codeless", "agent");
            Directory.CreateDirectory(agentDir);
            string agentFile = Path.Combine(agentDir, "security.md");

            File.WriteAllText(agentFile,
@"---
description: ""Security audit specialist""
model: anthropic/claude-sonnet-4-6
temperature: 0.05
mode: primary
tools:
  write_file: false
  edit_file: false
  bash: true
permission:
  edit: deny
  webfetch: allow
  bash:
    ""git *"": allow
    ""rm *"": deny
---
You are a strict security auditor. Never modify production files.");

            var parsed1 = CustomAgentAndCommandRegistry.GetOrParseAgentFile(agentFile, "codeless");
            Assert.NotNull(parsed1);
            Assert.Equal("security", parsed1.Name);
            Assert.Equal("Security audit specialist", parsed1.Description);
            Assert.Equal("anthropic/claude-sonnet-4-6", parsed1.Model);
            Assert.True(parsed1.HasTemperature);
            Assert.Equal(0.05, parsed1.Temperature, 3);
            Assert.Equal("primary", parsed1.Mode);
            Assert.True(parsed1.IsPrimary);
            Assert.False(parsed1.IsSubagent);
            Assert.False(parsed1.IsToolEnabled("write_file"));
            Assert.False(parsed1.IsToolEnabled("edit_file"));
            Assert.True(parsed1.IsToolEnabled("run_terminal"));
            Assert.True(parsed1.IsToolEnabled("read_file"));
            Assert.Equal("deny", parsed1.Permissions["edit"]);
            Assert.Equal("allow", parsed1.Permissions["webfetch"]);
            Assert.Equal("allow", parsed1.BashPermissions["git *"]);
            Assert.Equal("deny", parsed1.BashPermissions["rm *"]);
            Assert.Contains("You are a strict security auditor.", parsed1.Prompt);

            int countAfterFirst = CustomAgentAndCommandRegistry.TotalFileParseCount;
            Assert.Equal(1, countAfterFirst);

            // Second call with unchanged mtime and size returns cached instance without re-parsing
            var parsed2 = CustomAgentAndCommandRegistry.GetOrParseAgentFile(agentFile, "codeless");
            Assert.Same(parsed1, parsed2);
            Assert.Equal(1, CustomAgentAndCommandRegistry.TotalFileParseCount);

            // Modifying file content + mtime invalidates cache entry and re-parses
            File.WriteAllText(agentFile,
@"---
description: ""Updated description""
mode: subagent
---
Updated prompt body.");
            File.SetLastWriteTimeUtc(agentFile, DateTime.UtcNow.AddSeconds(5));

            var parsed3 = CustomAgentAndCommandRegistry.GetOrParseAgentFile(agentFile, "codeless");
            Assert.Equal(2, CustomAgentAndCommandRegistry.TotalFileParseCount);
            Assert.Equal("Updated description", parsed3.Description);
            Assert.True(parsed3.IsSubagent);
            Assert.False(parsed3.IsPrimary);
        }

        [Fact]
        public void DiscoveryPrecedence_CodelessOverridesOpencode_And_WorkspaceOverridesSettingsDir()
        {
            string settingsDir = CreateTempDir();
            string ws = CreateTempDir();

            Directory.CreateDirectory(Path.Combine(settingsDir, "agent"));
            Directory.CreateDirectory(Path.Combine(ws, ".opencode", "agent"));
            Directory.CreateDirectory(Path.Combine(ws, ".codeless", "agent"));

            // "reviewer" defined in all 3 -> .codeless wins
            File.WriteAllText(Path.Combine(settingsDir, "agent", "reviewer.md"), "---\ndescription: from-settings\n---\nPrompt settings");
            File.WriteAllText(Path.Combine(ws, ".opencode", "agent", "reviewer.md"), "---\ndescription: from-opencode\n---\nPrompt opencode");
            File.WriteAllText(Path.Combine(ws, ".codeless", "agent", "reviewer.md"), "---\ndescription: from-codeless\n---\nPrompt codeless");

            // "triager" defined in settings and .opencode -> .opencode wins
            File.WriteAllText(Path.Combine(settingsDir, "agent", "triager.md"), "---\ndescription: triager-settings\n---\nTriage settings");
            File.WriteAllText(Path.Combine(ws, ".opencode", "agent", "triager.md"), "---\ndescription: triager-opencode\n---\nTriage opencode");

            // "globalonly" defined only in settings -> discovered from settings
            File.WriteAllText(Path.Combine(settingsDir, "agent", "globalonly.md"), "---\ndescription: global-agent\n---\nGlobal body");

            var agents = CustomAgentAndCommandRegistry.DiscoverAgents(ws, settingsDir);
            Assert.Equal("from-codeless", agents["reviewer"].Description);
            Assert.Equal("Prompt codeless", agents["reviewer"].Prompt);
            Assert.Equal("triager-opencode", agents["triager"].Description);
            Assert.Equal("global-agent", agents["globalonly"].Description);

            // Command precedence check: .codeless/command > .opencode/command > <settingsDir>/command
            Directory.CreateDirectory(Path.Combine(settingsDir, "command"));
            Directory.CreateDirectory(Path.Combine(ws, ".opencode", "command"));
            Directory.CreateDirectory(Path.Combine(ws, ".codeless", "command"));

            File.WriteAllText(Path.Combine(settingsDir, "command", "ship.md"), "Ship from settings $ARGUMENTS");
            File.WriteAllText(Path.Combine(ws, ".opencode", "command", "ship.md"), "Ship from opencode $ARGUMENTS");
            File.WriteAllText(Path.Combine(ws, ".codeless", "command", "ship.md"), "Ship from codeless $ARGUMENTS");

            var commands = CustomAgentAndCommandRegistry.DiscoverCommands(ws, settingsDir);
            Assert.Equal("Ship from codeless $ARGUMENTS", commands["ship"].Template);
        }

        [Fact]
        public async Task PrimaryAgents_AppearInModeCycle_And_ApplyModelToolsAndPermissions()
        {
            string settingsDir = CreateTempDir();
            string ws = CreateTempDir();
            Directory.CreateDirectory(Path.Combine(ws, ".codeless", "agent"));
            Directory.CreateDirectory(Path.Combine(ws, ".opencode", "agent"));

            File.WriteAllText(Path.Combine(ws, ".codeless", "agent", "auditor.md"),
@"---
description: Read-only code auditor
model: openai/gpt-4.1
temperature: 0.05
mode: primary
tools:
  write_file: false
permission:
  edit: deny
---
CUSTOM_AUDITOR_DIRECTIVE_MARKER");

            File.WriteAllText(Path.Combine(ws, ".opencode", "agent", "helper.md"),
@"---
description: Subagent only
mode: subagent
---
Subagent body");

            // Mode cycle includes build -> plan -> auditor -> build (subagent "helper" is excluded from primary cycle)
            var cycle = AgentUiParity.GetPrimaryModeCycle(ws, settingsDir);
            Assert.Equal(new[] { "build", "plan", "auditor" }, cycle);

            string m1 = AgentUiParity.CycleBuildPlanMode("build", ws, settingsDir);
            Assert.Equal("plan", m1);
            string m2 = AgentUiParity.CycleBuildPlanMode(m1, ws, settingsDir);
            Assert.Equal("auditor", m2);
            string m3 = AgentUiParity.CycleBuildPlanMode(m2, ws, settingsDir);
            Assert.Equal("build", m3);
            Assert.Equal("Auditor", AgentUiParity.GetModePillLabel("auditor", ws, settingsDir));

            // Running a turn in mode "auditor" injects custom prompt, filters disabled tools, and enforces permissions
            var settings = new IdePersistentSettings { RequireAgentToolApproval = false };
            var engine = new LlmHarnessAndAgentEngine(settings)
            {
                CustomSettingsDirOverride = settingsDir
            };

            string capturedSysPrompt = null;
            engine.CustomModelResponder = (sys, msgs) =>
            {
                capturedSysPrompt = sys;
                return Task.FromResult("Audit complete.");
            };

            var turnRes = await engine.RunAutonomousAgentLoopAsync(
                "auditor", "Check this repo", engine.Vendors[0], "auto", null, ws, new List<string>(), new List<string>());

            Assert.Equal("Audit complete.", turnRes.AssistantReply);
            Assert.Contains("CUSTOM_AUDITOR_DIRECTIVE_MARKER", capturedSysPrompt);
            Assert.Contains("gpt-4.1", turnRes.ProviderUsed);

            // write_file is disabled in tools map and edit is denied in permission map
            var writeRes = engine.ExecuteWorkspaceTool(
                "write_file",
                new Dictionary<string, object> { ["path"] = "blocked.txt", ["content"] = "nope" },
                ws, new List<string>(), null, new Dictionary<string, string>(), "auditor", settings);
            Assert.False(writeRes.Success);
            Assert.Contains("Permission Denied", writeRes.Summary);
        }

        [Fact]
        public void TaskTool_SubagentType_SelectsCustomSubagent_And_RefusesUnknown()
        {
            string settingsDir = CreateTempDir();
            string ws = CreateTempDir();
            Directory.CreateDirectory(Path.Combine(ws, ".codeless", "agent"));

            File.WriteAllText(Path.Combine(ws, ".codeless", "agent", "researcher.md"),
@"---
description: Deep researcher subagent
mode: subagent
tools:
  grep_search: false
---
SUBAGENT_RESEARCHER_PROMPT_TAG");

            var settings = new IdePersistentSettings { RequireAgentToolApproval = false };
            var engine = new LlmHarnessAndAgentEngine(settings)
            {
                CustomSettingsDirOverride = settingsDir
            };

            string subagentSysPrompt = null;
            int callIndex = 0;
            engine.CustomModelResponder = (sys, msgs) =>
            {
                callIndex++;
                subagentSysPrompt = sys;
                if (callIndex == 1)
                {
                    // Subagent tries to call grep_search which is disabled for researcher
                    return Task.FromResult("<tool_call>\n{\"tool\":\"grep_search\",\"args\":{\"query\":\"foo\"}}\n</tool_call>");
                }
                string lastUser = msgs.Last()["content"];
                return Task.FromResult("Researcher result after tool check: " + (lastUser.Contains("disabled") ? "grep-was-disabled" : "unexpected"));
            };

            var okTask = engine.ExecuteWorkspaceTool(
                "task",
                new Dictionary<string, object>
                {
                    ["description"] = "Run researcher",
                    ["prompt"] = "Investigate architecture",
                    ["subagent_type"] = "researcher"
                },
                ws, new List<string>(), null, new Dictionary<string, string>(), "build", settings);

            Assert.True(okTask.Success);
            Assert.Contains("SUBAGENT_RESEARCHER_PROMPT_TAG", subagentSysPrompt);
            Assert.Contains("grep-was-disabled", okTask.Output);

            // Unknown subagent_type is refused cleanly
            var badTask = engine.ExecuteWorkspaceTool(
                "task",
                new Dictionary<string, object>
                {
                    ["description"] = "Unknown subagent",
                    ["prompt"] = "Test",
                    ["subagent_type"] = "does_not_exist"
                },
                ws, new List<string>(), null, new Dictionary<string, string>(), "build", settings);
            Assert.False(badTask.Success);
            Assert.Contains("Unknown subagent_type", badTask.Output);
        }

        [Fact]
        public async Task CustomCommand_ExpandsArguments_AtFileIncludes_And_ShellBehindPermission()
        {
            string settingsDir = CreateTempDir();
            string ws = CreateTempDir();
            Directory.CreateDirectory(Path.Combine(ws, ".codeless", "command"));
            Directory.CreateDirectory(Path.Combine(ws, "src"));
            File.WriteAllText(Path.Combine(ws, "src", "hello.cs"), "class Hello {\n    static void Main() {}\n}");

            File.WriteAllText(Path.Combine(ws, ".codeless", "command", "review.md"),
@"---
description: Review target module
---
Review module $1 (flags: $2, all: $ARGUMENTS).
Context: @src/hello.cs
Git branch: !`git rev-parse --abbrev-ref HEAD`");

            // 1. When RequireAgentToolApproval is true and user denies approval, !`cmd` is blocked
            var strictSettings = new IdePersistentSettings { RequireAgentToolApproval = true };
            bool askedTerminal = false;
            string expandedDenied;
            CustomCommandDefinition matchedCmd;
            bool okDenied = CustomAgentAndCommandRegistry.TryExpandSlashCommand(
                "/review auth --strict",
                ws,
                strictSettings,
                "build",
                (tool, json) => { askedTerminal = true; return false; },
                (cmd, cwd) => "main-branch",
                out expandedDenied,
                out matchedCmd,
                settingsDir);

            Assert.True(okDenied);
            Assert.True(askedTerminal);
            Assert.Contains("Review module auth (flags: --strict, all: auth --strict).", expandedDenied);
            Assert.Contains("[Command approval denied: git rev-parse --abbrev-ref HEAD]", expandedDenied);
            Assert.Contains("00001| class Hello {", expandedDenied);

            // 2. When approved (or allowed), !`cmd` executes via runner and substitutes output in full agent turn
            var allowSettings = new IdePersistentSettings { RequireAgentToolApproval = false };
            var engine = new LlmHarnessAndAgentEngine(allowSettings)
            {
                CustomSettingsDirOverride = settingsDir,
                CustomShellCommandRunner = (cmd, cwd) => "feature/m9b-branch"
            };

            string capturedUserMessage = null;
            engine.CustomModelResponder = (sys, msgs) =>
            {
                capturedUserMessage = msgs[0]["content"];
                return Task.FromResult("Reviewed successfully.");
            };

            var turn = await engine.RunAutonomousAgentLoopAsync(
                "build", "/review auth --strict", engine.Vendors[0], "auto", null, ws, new List<string>(), new List<string>());

            Assert.Equal("Reviewed successfully.", turn.AssistantReply);
            Assert.Contains("Review module auth (flags: --strict, all: auth --strict).", capturedUserMessage);
            Assert.Contains("Git branch: feature/m9b-branch", capturedUserMessage);
            Assert.Contains("00001| class Hello {", capturedUserMessage);
        }
    }
}
