using System;
using System.IO;
using System.Globalization;
using Xunit;
using codeLess.Core;
using codeLess.Studio;

namespace codeLess.Avalonia.Tests
{
    public class VscodeSettingsManagerTests
    {
        [Fact]
        public void Settings_Load_IsRobust()
        {
            var content = "{ \"invalid\" }";
            var settings = new StudioSettings();
            string err = VscodeSettingsManager.ApplySettings(content, settings);
            Assert.NotNull(err);

            content = "{ \"editor.tabSize\": 7 }";
            err = VscodeSettingsManager.ApplySettings(content, settings);
            Assert.Null(err);
            Assert.Equal(7, settings.TabSize);
        }

        [Fact]
        public void EditorRulers_LeavesTabSizeIntact()
        {
            var settings = new StudioSettings();
            settings.TabSize = 4;
            var content = "{ \"editor.rulers\": [80, 120] }";
            string err = VscodeSettingsManager.ApplySettings(content, settings);
            Assert.Null(err);
            Assert.Equal(4, settings.TabSize);
        }

        [Fact]
        public void WriteSetting_UpdatesArraySafely()
        {
            string json = "{\n  // my comment\n  \"editor.rulers\": [80]\n}";
            string updated = VscodeSettingsManager.WriteSettingJson(json, "editor.rulers", new int[] { 80, 120 });
            Assert.Contains("[80, 120]", updated);
            Assert.Contains("// my comment", updated);
        }

        [Fact]
        public void WriteSetting_UpdatesPropertyWithoutDestroyingFormat()
        {
            string json = "{\n  // comment\n  \"foo\": \"bar\"\n}";
            string updated = VscodeSettingsManager.WriteSettingJson(json, "foo", "baz");
            Assert.Contains("\"baz\"", updated);
            Assert.Contains("// comment", updated);
        }

        [Fact]
        public void WriteSetting_CultureInvariant()
        {
            var oldCulture = CultureInfo.CurrentCulture;
            try {
                CultureInfo.CurrentCulture = new CultureInfo("de-DE");
                string json = "{}";
                string updated = VscodeSettingsManager.WriteSettingJson(json, "editor.fontSize", 14.5);
                Assert.Contains("14.5", updated);
                Assert.DoesNotContain("14,5", updated);
            } finally {
                CultureInfo.CurrentCulture = oldCulture;
            }
        }

        [Fact]
        public void WriteSetting_StringEscapes()
        {
            string json = "{}";
            string updated = VscodeSettingsManager.WriteSettingJson(json, "terminal.integrated.shellArgs.linux", new string[] { "-c", "echo \"hello \\\" world\"" });
            Assert.Contains("echo \\\"hello \\\\\\\" world\\\"", updated);
        }
    }
}
