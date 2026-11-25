using System;
using Avalonia.Headless.XUnit;
using System.Collections.Generic;
using System.Linq;
using Avalonia.Input;
using codeLess.Core;
using Xunit;
using codeLess.AvaloniaApp;

namespace codeLess.Avalonia.Tests
{
    [Collection("SequentialWorkbench")]
    public class DataDrivenRegistryTests
    {
        [AvaloniaFact]
        public void AllRegistryDefaults_AreDispatchable()
        {
            DefaultCommands.Register();
            var manager = new KeybindingManager();
            var mockCtx = new MockContext();
            var failures = new List<string>();

            // Explicitly allowed collisions / reasons:
            var allowed = new HashSet<string> {
                "editor.action.revealDefinition", // shadowed by goToDeclaration on F12
                "editor.action.referenceSearch.trigger", // shadowed by goToReferences on Shift+F12
                "workbench.action.findInFiles", // shadowed by workbench.view.search on Cmd/Ctrl+Shift+F
                "inlineChat.start", // shadowed by editor.action.inlineAiBar on Cmd/Ctrl+I
                "workbench.action.files.copyPathOfActiveFile" // shadowed by copyFilePath on Cmd/Ctrl+K P
            };

            foreach (var b in manager.Defaults)
            {
                if (string.IsNullOrEmpty(b.Key)) continue;
                bool executed = false;
                manager.BindAction(b.Command, () => { executed = true; });

                if (WhenParser.Evaluate(b.WhenParsed, mockCtx))
                {
                    if (allowed.Contains(b.Command))
                    {
                        continue;
                    }

                    string[] parts = b.Key.Split(' ');
                    foreach (string p in parts)
                    {
                        manager.Dispatch(p, mockCtx, s => { }, false);
                    }
                    if (!executed)
                    {
                        failures.Add($"{b.Command} on {b.Key}");
                    }
                }
            }
            Assert.True(failures.Count == 0, string.Join(", ", failures));
        }

        [AvaloniaFact]
        public void PaletteShortcutLabels_MatchRegistry()
        {
            DefaultCommands.Register();
            var w = new MainWindow(null);
            try
            {
                var manager = new KeybindingManager();
                var p = w.BuildAllPaletteCommands();
                int checkedItems = 0;
                foreach (var item in p)
                {
                    if (!string.IsNullOrEmpty(item.Id))
                    {
                        var b = manager.ActiveBindings.FirstOrDefault(x => x.Command == item.Id);
                        if (b != null && !string.IsNullOrEmpty(b.Key))
                        {
                            Assert.Equal(MacKeymap.FormatShortcut(b.Key), item.Shortcut);
                            checkedItems++;
                        }
                    }
                }
                Assert.True(checkedItems >= 30, "Should check at least 30 palette items");
            }
            finally
            {
                w.SuppressUnsavedPrompts = true;
                w.Close();
            }
        }

        [Fact]
        public void KeybindingsDocMatchesCode()
        {
            Assert.Equal("3.4.1", MainWindow.CurrentAppVersion);

            string docPath = System.IO.Path.GetFullPath(System.IO.Path.Combine(AppContext.BaseDirectory, "../../../../../docs/KEYBINDINGS.md"));
            if (!System.IO.File.Exists(docPath))
            {
                docPath = System.IO.Path.GetFullPath(System.IO.Path.Combine(Environment.CurrentDirectory, "docs/KEYBINDINGS.md"));
            }
            if (!System.IO.File.Exists(docPath))
            {
                docPath = System.IO.Path.GetFullPath(System.IO.Path.Combine(Environment.CurrentDirectory, "../../docs/KEYBINDINGS.md"));
            }
            Assert.True(System.IO.File.Exists(docPath), "docs/KEYBINDINGS.md must exist at " + docPath);

            string expected = DefaultCommands.FormatKeybindingsMarkdown().Replace("\r\n", "\n").Trim();
            string actual = System.IO.File.ReadAllText(docPath).Replace("\r\n", "\n").Trim();
            Assert.Equal(expected, actual);
        }

        [Fact]
        public void DocsReleaseClaimsMatchCode()
        {
            string repoRoot = System.IO.Path.GetFullPath(System.IO.Path.Combine(AppContext.BaseDirectory, "../../../../.."));
            if (!System.IO.File.Exists(System.IO.Path.Combine(repoRoot, "RELEASE_NOTES.md")))
            {
                repoRoot = System.IO.Path.GetFullPath(Environment.CurrentDirectory);
            }

            string releaseNotesPath = System.IO.Path.Combine(repoRoot, "RELEASE_NOTES.md");
            Assert.True(System.IO.File.Exists(releaseNotesPath), "RELEASE_NOTES.md must exist at " + releaseNotesPath);
            string fullReleaseNotes = System.IO.File.ReadAllText(releaseNotesPath);

            int v335Idx = fullReleaseNotes.IndexOf("# codeLess v3.3.5", StringComparison.Ordinal);
            string v340Section = v335Idx > 0 ? fullReleaseNotes.Substring(0, v335Idx) : fullReleaseNotes;
            Assert.Contains("# codeLess v3.4.0", v340Section);

            // Load all C# source texts under src/ once for fast verification
            string srcRoot = System.IO.Path.Combine(repoRoot, "src");
            var allCsFiles = System.IO.Directory.GetFiles(srcRoot, "*.cs", System.IO.SearchOption.AllDirectories);
            string combinedSrc = string.Join("\n", allCsFiles.Select(System.IO.File.ReadAllText));

            // 1. Verify every backticked dotted setting key in RELEASE_NOTES 3.4.0 exists in code
            var settingRegex = new System.Text.RegularExpressions.Regex(@"`((?:window|editor|workbench|files|search|explorer|terminal|codeless|security)\.[A-Za-z0-9_.]+)`");
            var settingMatches = settingRegex.Matches(v340Section);
            var checkedSettings = new HashSet<string>(StringComparer.Ordinal);
            foreach (System.Text.RegularExpressions.Match m in settingMatches)
            {
                string key = m.Groups[1].Value;
                checkedSettings.Add(key);
                Assert.True(
                    combinedSrc.Contains("\"" + key + "\""),
                    $"Setting key `{key}` mentioned in RELEASE_NOTES.md v3.4.0 does not exist as a string literal in src/");
            }

            Assert.Contains("window.restoreWindows", checkedSettings);
            Assert.Contains("codeless.mcp", checkedSettings);
            Assert.Contains("codeless.agent.nativeToolCalling", checkedSettings);
            Assert.Contains("editor.wordSeparators", checkedSettings);
            Assert.Contains("editor.cursorSurroundingLines", checkedSettings);
            Assert.Contains("editor.useTabStops", checkedSettings);
            Assert.Contains("editor.emptySelectionClipboard", checkedSettings);
            Assert.Contains("editor.formatOnSave", checkedSettings);
            Assert.Contains("files.hotExit", checkedSettings);
            Assert.Contains("files.autoGuessEncoding", checkedSettings);
            Assert.Contains("files.eol", checkedSettings);
            Assert.Contains("files.insertFinalNewline", checkedSettings);
            Assert.Contains("files.trimTrailingWhitespace", checkedSettings);
            Assert.Contains("files.maxMemoryForLargeFilesMB", checkedSettings);

            // 2. Verify every backticked slash command (e.g. `/undo`, `/redo`, `/compact`) in RELEASE_NOTES 3.4.0 exists in code
            var slashRegex = new System.Text.RegularExpressions.Regex(@"`(/[a-z]{2,20})`");
            var slashMatches = slashRegex.Matches(v340Section);
            var checkedSlashCommands = new HashSet<string>(StringComparer.Ordinal);
            foreach (System.Text.RegularExpressions.Match m in slashMatches)
            {
                string cmd = m.Groups[1].Value;
                checkedSlashCommands.Add(cmd);
                Assert.True(
                    combinedSrc.Contains("\"" + cmd + "\""),
                    $"Slash command `{cmd}` mentioned in RELEASE_NOTES.md v3.4.0 does not exist as a string literal in src/");
            }

            Assert.Contains("/undo", checkedSlashCommands);
            Assert.Contains("/redo", checkedSlashCommands);
            Assert.Contains("/compact", checkedSlashCommands);
        }

        private class MockContext : IContextKeyService
        {
            public object GetValue(string key) => true;
        }
    }
}
