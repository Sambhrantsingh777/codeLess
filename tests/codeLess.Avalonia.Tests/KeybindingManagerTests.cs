using System;
using System.IO;
using System.Collections.Generic;
using Xunit;
using codeLess.Core;

namespace codeLess.Avalonia.Tests
{
    public class KeybindingManagerTests
    {
        [Fact]
        public void SaveUserBindings_PreservesCommentsAndFormatting()
        {
            string tmp = Path.GetTempFileName();
            try {
                string initial = "[\n  // my comment\n  {\n    \"key\": \"ctrl+k\",\n    \"command\": \"workbench.action.quit\"\n  }\n]";
                File.WriteAllText(tmp, initial);

                var km = new KeybindingManager();
                string err = km.LoadUserBindings(initial);
                Assert.Null(err);

                var newBindings = new List<KeybindingEntry>();
                newBindings.Add(new KeybindingEntry { Key = "ctrl+k", Command = "workbench.action.quit" });
                newBindings.Add(new KeybindingEntry { Key = "ctrl+j", Command = "editor.action.joinLines" });

                string saveErr = KeybindingManager.SaveUserBindings(newBindings, tmp);
                Assert.Null(saveErr);

                string written = File.ReadAllText(tmp);
                Assert.Contains("// my comment", written);
                Assert.Contains("editor.action.joinLines", written);
            } finally {
                if (File.Exists(tmp)) File.Delete(tmp);
            }
        }
    }
}
