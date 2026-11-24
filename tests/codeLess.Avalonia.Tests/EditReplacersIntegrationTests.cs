using System;
using System.Collections.Generic;
using Xunit;
using codeLess.Studio;

namespace codeLess.Avalonia.Tests
{
    public class EditReplacersIntegrationTests
    {
        [Fact]
        public void Multiedit_AtomicRollback()
        {
            // Simulate multiedit logic
            string current = "line1\nline2\nline3\nline4\nline5";
            
            var edits = new List<Dictionary<string, object>>
            {
                new Dictionary<string, object> { { "oldString", "line2" }, { "newString", "edited2" } },
                new Dictionary<string, object> { { "oldString", "line4" }, { "newString", "edited4" } },
                new Dictionary<string, object> { { "oldString", "missing" }, { "newString", "wont_apply" } } // this will fail
            };
            
            string next = current;
            bool success = true;
            foreach (var edit in edits)
            {
                string search = edit["oldString"].ToString();
                string replace = edit["newString"].ToString();
                string errorMsg;
                next = LlmHarnessAndAgentEngine.ApplySearchReplacePatch(next, search, replace, false, out errorMsg);
                if (errorMsg != null)
                {
                    success = false;
                    break;
                }
            }
            
            // Multiedit rolls back all changes if any fail
            if (!success)
            {
                next = current;
            }
            
            Assert.False(success);
            Assert.Equal(current, next);
        }

        [Fact]
        public void Multiedit_AppliesSequential()
        {
            string current = "1\n2\n3";
            var edits = new List<Dictionary<string, object>>
            {
                new Dictionary<string, object> { { "oldString", "1" }, { "newString", "a" } },
                new Dictionary<string, object> { { "oldString", "3" }, { "newString", "c" } }
            };
            
            string next = current;
            bool success = true;
            foreach (var edit in edits)
            {
                string search = edit["oldString"].ToString();
                string replace = edit["newString"].ToString();
                string errorMsg;
                next = LlmHarnessAndAgentEngine.ApplySearchReplacePatch(next, search, replace, false, out errorMsg);
                if (errorMsg != null)
                {
                    success = false;
                    break;
                }
            }
            
            Assert.True(success);
            Assert.Equal("a\n2\nc", next);
        }
    }
}
