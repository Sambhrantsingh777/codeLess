using System;
using System.Collections.Generic;
using System.IO;
using codeLess.Studio;
using Xunit;

namespace codeLess.Avalonia.Tests
{
    public class WinFormsParityM7fTests
    {
        [Fact]
        public void WinForms_BuildPlanMode_CyclesAndNormalizesCorrectly()
        {
            Assert.Equal("build", AgentUiParity.NormalizeBuildPlanMode("build"));
            Assert.Equal("build", AgentUiParity.NormalizeBuildPlanMode("agent"));
            Assert.Equal("build", AgentUiParity.NormalizeBuildPlanMode(""));
            Assert.Equal("plan", AgentUiParity.NormalizeBuildPlanMode("plan"));
            Assert.Equal("plan", AgentUiParity.NormalizeBuildPlanMode("composer"));

            // Tab cycles build <-> plan
            string mode = "agent";
            mode = AgentUiParity.CycleBuildPlanMode(mode);
            Assert.Equal("plan", mode);
            Assert.Equal("Plan", AgentUiParity.GetModePillLabel(mode));

            mode = AgentUiParity.CycleBuildPlanMode(mode);
            Assert.Equal("build", mode);
            Assert.Equal("Build", AgentUiParity.GetModePillLabel(mode));
        }

        [Fact]
        public void WinForms_TodoListFormatting_IconsAndStrikethrough_AndOnTodosChanged()
        {
            var settings = new IdePersistentSettings { RequireAgentToolApproval = false };
            var engine = new LlmHarnessAndAgentEngine(settings);
            engine.OnRequestToolApproval = (t, j) => true;

            int changedFireCount = 0;
            List<FormattedTodoItem> renderedTodos = null;
            engine.OnTodosChanged = () =>
            {
                changedFireCount++;
                renderedTodos = AgentUiParity.FormatTodoList(engine.AgentTodos);
            };

            var todos = new List<object>
            {
                new Dictionary<string, object> { { "id", "1" }, { "content", "Pending step" }, { "status", "pending" }, { "priority", "high" } },
                new Dictionary<string, object> { { "id", "2" }, { "content", "Active step" }, { "status", "in_progress" }, { "priority", "medium" } },
                new Dictionary<string, object> { { "id", "3" }, { "content", "Finished step" }, { "status", "completed" }, { "priority", "low" } },
                new Dictionary<string, object> { { "id", "4" }, { "content", "Skipped step" }, { "status", "cancelled" }, { "priority", "low" } }
            };

            var args = new Dictionary<string, object> { { "todos", todos } };
            var res = engine.ExecuteWorkspaceTool("todowrite", args, Path.GetTempPath(), new List<string>(), null, new Dictionary<string, string>(), "build", settings);

            Assert.True(res.Success);
            Assert.Equal(1, changedFireCount);
            Assert.NotNull(renderedTodos);
            Assert.Equal(4, renderedTodos.Count);

            Assert.Equal("\u2610", renderedTodos[0].Icon); // ☐
            Assert.False(renderedTodos[0].Strikethrough);
            Assert.Equal("\u2610 1: Pending step", renderedTodos[0].DisplayText);

            Assert.Equal("\u25D0", renderedTodos[1].Icon); // ◐
            Assert.False(renderedTodos[1].Strikethrough);
            Assert.Equal("\u25D0 2: Active step", renderedTodos[1].DisplayText);

            Assert.Equal("\u2611", renderedTodos[2].Icon); // ☑
            Assert.False(renderedTodos[2].Strikethrough);
            Assert.Equal("\u2611 3: Finished step", renderedTodos[2].DisplayText);

            Assert.Equal("\u2612", renderedTodos[3].Icon); // ☒
            Assert.True(renderedTodos[3].Strikethrough);
            Assert.Equal("\u2612 4: Skipped step", renderedTodos[3].DisplayText);
        }

        [Fact]
        public void WinForms_MainFormAndFeedControl_WiredToBuildPlanToggleAndTodoRenderer()
        {
            string repoRoot = Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "..", "..", "..", ".."));
            string mainFormPath = Path.Combine(repoRoot, "src", "CodeLessMainForm.cs");
            string themesPath = Path.Combine(repoRoot, "src", "CoreModelsAndThemes.cs");

            Assert.True(File.Exists(mainFormPath), "Expected CodeLessMainForm.cs at " + mainFormPath);
            Assert.True(File.Exists(themesPath), "Expected CoreModelsAndThemes.cs at " + themesPath);

            string mainFormSrc = File.ReadAllText(mainFormPath);
            string themesSrc = File.ReadAllText(themesPath);

            // Verify Build/Plan mode dropdown items + Tab toggle + OnTodosChanged wiring in CodeLessMainForm.cs
            Assert.Contains("Build (Agent Mode)", mainFormSrc);
            Assert.Contains("Plan (Read-Only Planning)", mainFormSrc);
            Assert.Contains("AgentUiParity.CycleBuildPlanMode(currentAgentMode)", mainFormSrc);
            Assert.Contains("AgentUiParity.GetModePillLabel(", mainFormSrc);
            Assert.Contains("this.agentFeedControl.UpdateTodos(this.aiEngine.AgentTodos)", mainFormSrc);

            // Verify Todo list rendering with strikethrough on cancelled items in CodeLessAgentFeedControl
            Assert.Contains("AgentUiParity.FormatTodoList(AgentTodos)", themesSrc);
            Assert.Contains("FontStyle.Strikeout", themesSrc);
            Assert.Contains("public void UpdateTodos(", themesSrc);
        }
    }
}
