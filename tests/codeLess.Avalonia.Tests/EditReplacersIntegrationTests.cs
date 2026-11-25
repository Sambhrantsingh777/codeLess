using System;
using System.Collections.Generic;
using System.IO;
using Xunit;
using codeLess.Studio;

namespace codeLess.Avalonia.Tests
{
    public class EditReplacersIntegrationTests
    {
        [Fact]
        public void Multiedit_AtomicRollback()
        {
            string ws = Path.Combine(Path.GetTempPath(), "codeless-multiedit-tests-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(ws);
            try
            {
                string f = Path.Combine(ws, "test.txt");
                string current = "line1\nline2\nline3\nline4\nline5";
                File.WriteAllText(f, current);

                var engine = new LlmHarnessAndAgentEngine(new IdePersistentSettings { RequireAgentToolApproval = false });
                engine.OnRequestToolApproval = (name, d) => true; // fake approval
                engine.FileTimeTracker.RecordRead(f, File.GetLastWriteTimeUtc(f));

                var edits = new List<object>
                {
                    new Dictionary<string, object> { { "oldString", "line2" }, { "newString", "edited2" } },
                    new Dictionary<string, object> { { "oldString", "line4" }, { "newString", "edited4" } },
                    new Dictionary<string, object> { { "oldString", "missing" }, { "newString", "wont_apply" } } // this will fail
                };
                
                var args = new Dictionary<string, object>
                {
                    { "path", "test.txt" },
                    { "edits", edits }
                };

                var staged = new Dictionary<string, string>();
                var result = engine.ExecuteWorkspaceTool("multiedit", args, ws, new List<string> { f }, null, staged, "agent", null);

                Assert.False(result.Success);
                Assert.Contains("failed (rolled back all)", result.Output);
                Assert.Empty(staged);
                Assert.Equal(current, File.ReadAllText(f));
            }
            finally
            {
                Directory.Delete(ws, true);
            }
        }

        [Fact]
        public void Multiedit_AppliesSequential()
        {
            string ws = Path.Combine(Path.GetTempPath(), "codeless-multiedit-tests-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(ws);
            try
            {
                string f = Path.Combine(ws, "test.txt");
                string current = "1\n2\n3";
                File.WriteAllText(f, current);

                var engine = new LlmHarnessAndAgentEngine(new IdePersistentSettings { RequireAgentToolApproval = false });
                engine.OnRequestToolApproval = (name, d) => true;
                engine.FileTimeTracker.RecordRead(f, File.GetLastWriteTimeUtc(f));

                var edits = new List<object>
                {
                    new Dictionary<string, object> { { "oldString", "1" }, { "newString", "a" } },
                    new Dictionary<string, object> { { "oldString", "3" }, { "newString", "c" } }
                };
                
                var args = new Dictionary<string, object>
                {
                    { "path", "test.txt" },
                    { "edits", edits }
                };

                var staged = new Dictionary<string, string>();
                var result = engine.ExecuteWorkspaceTool("multiedit", args, ws, new List<string> { f }, null, staged, "agent", null);

                Assert.True(result.Success);
                Assert.Single(staged);
                Assert.Equal("a\n2\nc", staged[f]);
            }
            finally
            {
                Directory.Delete(ws, true);
            }
        }
    }
}
