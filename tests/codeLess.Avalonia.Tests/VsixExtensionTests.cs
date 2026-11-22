using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using codeLess.AvaloniaApp;
using codeLess.Studio;
using Xunit;

namespace codeLess.Avalonia.Tests
{
    /// <summary>Builds small .vsix packages for the extension tests.</summary>
    internal static class VsixFixture
    {
        public static string NewDir(string tag)
        {
            string d = Path.Combine(Path.GetTempPath(), "cl-vsix-" + tag + "-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(d);
            return d;
        }

        /// <summary>A theme + snippets extension whose JSON files use comments and trailing commas (JSONC).</summary>
        public static string Build(string dir, string version = "1.0.0", string editorBg = "#102030", string extraEntry = null)
        {
            var files = new Dictionary<string, string>
            {
                ["extension/package.json"] = @"{
  // VS Code tolerates comments in package.json
  ""name"": ""night-owl-lite"",
  ""publisher"": ""acme"",
  ""displayName"": ""Night Owl Lite"",
  ""version"": """ + version + @""",
  ""description"": ""A theme and C# snippets"",
  ""contributes"": {
    ""themes"": [
      { ""label"": ""Night Owl Lite"", ""uiTheme"": ""vs-dark"", ""path"": ""./themes/night.json"" },
      { ""label"": ""Day Owl Lite"", ""uiTheme"": ""vs"", ""path"": ""./themes/day.json"", },
      { ""label"": ""Escaper"", ""path"": ""../../../escape.json"" }
    ],
    ""snippets"": [
      { ""language"": ""csharp"", ""path"": ""./snippets/cs.json"" },
    ],
  },
}",
                ["extension/themes/base.json"] = @"{ ""colors"": { ""sideBar.background"": ""#0a0b0c"", }, }",
                ["extension/themes/night.json"] = @"{
  /* block comment */
  ""include"": ""./base.json"",
  ""type"": ""dark"",
  ""colors"": {
    ""editor.background"": """ + editorBg + @""", // inline comment
    ""editor.foreground"": ""#d6deeb"",
    ""editor.selectionBackground"": ""#ffffff80"",
    ""button.background"": ""#7e57c2"",
  },
  ""tokenColors"": [
    { ""scope"": ""comment"", ""settings"": { ""foreground"": ""#637777"", ""fontStyle"": ""italic"" } },
    { ""scope"": [""keyword"", ""storage.type""], ""settings"": { ""foreground"": ""#c792ea"" } },
  ],
}",
                ["extension/themes/day.json"] = @"{ ""type"": ""light"", ""colors"": { ""editor.background"": ""#fbfbfb"", }, }",
                ["extension/snippets/cs.json"] = @"{
  // C# snippets
  ""Property"": { ""prefix"": [""propx"", ""prop2""], ""body"": [""public ${1:int} ${2:MyProperty} { get; set; }$0""], ""description"": ""Auto property"" },
  ""If"": { ""prefix"": ""ifx"", ""body"": [""if (${1:condition})"", ""{"", ""\t$0"", ""}""], },
}",
                ["[Content_Types].xml"] = "<?xml version=\"1.0\"?><Types/>"
            };
            if (extraEntry != null) files[extraEntry] = "x";
            string path = Path.Combine(dir, "acme.night-owl-lite-" + version + ".vsix");
            using (var zip = ZipFile.Open(path, ZipArchiveMode.Create))
                foreach (var kv in files)
                {
                    var e = zip.CreateEntry(kv.Key);
                    using (var w = new StreamWriter(e.Open(), new UTF8Encoding(false))) w.Write(kv.Value);
                }
            return path;
        }
    }

    /// <summary>QA #18 / #14: VSIX host engine (shared by both UIs).</summary>
    public class VsixHostEngineTests : IDisposable
    {
        private readonly string root = VsixFixture.NewDir("root");
        private readonly string src = VsixFixture.NewDir("src");
        private readonly List<StudioTheme> themes = StudioTheme.GetBuiltInThemes();

        public void Dispose()
        {
            try { Directory.Delete(root, true); } catch { }
            try { Directory.Delete(src, true); } catch { }
        }

        [Fact]
        public void JsoncPackage_ThemesAndSnippets_Load()
        {
            var host = new VsixAndPluginHostEngine(themes, root);
            var pkg = host.InstallVsix(VsixFixture.Build(src), themes, out string status);
            Assert.NotNull(pkg);
            Assert.Contains("Night Owl Lite", status);
            Assert.Equal("acme.night-owl-lite", pkg.Id);
            Assert.Equal(2, pkg.ContributedThemes.Count); // the path escaping the package is ignored

            var night = themes.Single(t => t.Name == "Night Owl Lite (VSIX)");
            Assert.Equal(System.Drawing.Color.FromArgb(0x10, 0x20, 0x30).ToArgb(), night.BgEditor.ToArgb());
            Assert.Equal(System.Drawing.Color.FromArgb(0x0a, 0x0b, 0x0c).ToArgb(), night.BgSidebar.ToArgb()); // from "include"
            Assert.Equal(System.Drawing.Color.FromArgb(0x7e, 0x57, 0xc2).ToArgb(), night.AccentPrimary.ToArgb());
            Assert.Equal(StudioTheme.Blend(night.BgEditor, System.Drawing.Color.White, 128 / 255.0).ToArgb(), night.BgSelection.ToArgb()); // alpha blended
            Assert.Equal(System.Drawing.Color.FromArgb(0x63, 0x77, 0x77).ToArgb(), night.TokenComment.ToArgb());
            Assert.Equal(System.Drawing.Color.FromArgb(0xc7, 0x92, 0xea).ToArgb(), night.TokenKeyword.ToArgb());
            Assert.NotNull(night.VsixTokenColors);
            Assert.False(night.IsLight);
            var day = themes.Single(t => t.Name == "Day Owl Lite (VSIX)");
            Assert.True(day.IsLight);
            Assert.True(day.FgPrimary.R < 128); // light base theme: dark text, not the dark theme's light text

            var cs = SnippetEngine.ForLanguage(host.AllLoadedSnippets, "cs");
            Assert.Contains(cs, s => s.Prefix == "propx");
            Assert.Contains(cs, s => s.Prefix == "prop2"); // every prefix of a list
            Assert.DoesNotContain(SnippetEngine.ForLanguage(host.AllLoadedSnippets, "py"), s => s.Prefix == "propx");
        }

        [Fact]
        public void Reinstall_ReplacesTheOldVersion_AndRestartLoadsIt()
        {
            var host = new VsixAndPluginHostEngine(themes, root);
            host.InstallVsix(VsixFixture.Build(src, "1.0.0", "#102030"), themes, out _);
            var pkg2 = host.InstallVsix(VsixFixture.Build(src, "1.1.0", "#405060"), themes, out _);
            Assert.Equal("1.1.0", pkg2.Version);
            Assert.Single(host.Extensions.Where(x => x.Id == "acme.night-owl-lite"));
            Assert.Single(Directory.GetDirectories(root).Where(d => !Path.GetFileName(d).StartsWith(".")));
            Assert.Single(themes.Where(t => t.Name == "Night Owl Lite (VSIX)"));
            Assert.Equal(0x40, themes.Single(t => t.Name == "Night Owl Lite (VSIX)").BgEditor.R);
            Assert.Single(host.AllLoadedSnippets.Where(s => s.Prefix == "ifx"));

            var fresh = StudioTheme.GetBuiltInThemes();
            var restarted = new VsixAndPluginHostEngine(fresh, root); // next start
            Assert.Contains(restarted.Extensions, x => x.Id == "acme.night-owl-lite" && x.Version == "1.1.0" && x.IsInstalled);
            Assert.Contains(fresh, t => t.Name == "Night Owl Lite (VSIX)");
        }

        [Fact]
        public void MarketplaceEntry_IsReplacedByTheInstalledPackage_AndUninstallUnloadsEverything()
        {
            var host = new VsixAndPluginHostEngine(themes, root);
            host.Extensions.Add(new VsixExtensionPackage { Id = "acme.night-owl-lite", DisplayName = "Night Owl Lite", DownloadUrl = "https://example.invalid/x.vsix", IsInstalled = false });
            var pkg = host.InstallVsix(VsixFixture.Build(src), themes, out _);
            var entry = host.Extensions.Single(x => x.Id == "acme.night-owl-lite");
            Assert.Same(pkg, entry);
            Assert.True(entry.IsInstalled);
            Assert.Equal(2, entry.ContributedThemes.Count);

            Assert.True(host.Uninstall(pkg, themes, out string status), status);
            Assert.DoesNotContain(host.Extensions, x => x.Id == "acme.night-owl-lite");
            Assert.DoesNotContain(themes, t => t.Name.Contains("Owl Lite"));
            Assert.DoesNotContain(host.AllLoadedSnippets, s => s.Prefix == "propx");
            Assert.False(Directory.Exists(pkg.InstallPath));
        }

        [Fact]
        public void NotAnExtension_AndZipSlip_AreRejected()
        {
            var host = new VsixAndPluginHostEngine(themes, root);
            string bogus = Path.Combine(src, "bogus.vsix");
            using (var zip = ZipFile.Open(bogus, ZipArchiveMode.Create)) zip.CreateEntry("readme.txt");
            Assert.Null(host.InstallVsix(bogus, themes, out string status));
            Assert.Contains("not a VS Code extension", status);
            Assert.Null(host.InstallVsix(VsixFixture.Build(src, extraEntry: "../../evil.txt"), themes, out status));
            Assert.False(File.Exists(Path.Combine(Path.GetDirectoryName(root), "evil.txt")));
            Assert.Empty(Directory.GetDirectories(root).Where(d => !Path.GetFileName(d).StartsWith(".")));
        }

        [Fact]
        public void ThemeFileWithCommentsAndTrailingCommas_ParsesDirectly()
        {
            string f = Path.Combine(src, "t.json");
            File.WriteAllText(f, "{\n // c\n \"type\": \"dark\", \"colors\": { \"editor.background\": \"#abc\", /* x */ },\n}");
            var t = new VsixAndPluginHostEngine(themes, root).TryParseVsixThemeJson(f, "Short Hex");
            Assert.NotNull(t);
            Assert.Equal(System.Drawing.Color.FromArgb(0xaa, 0xbb, 0xcc).ToArgb(), t.BgEditor.ToArgb());
        }
    }

    /// <summary>VS Code snippet syntax and Tab expansion (shared by both editors).</summary>
    public class SnippetEngineTests
    {
        [Fact]
        public void Expand_TabStopsDefaultsChoicesVariablesEscapesAndIndent()
        {
            var vars = new Dictionary<string, string> { ["TM_FILENAME_BASE"] = "Calc" };
            var ex = SnippetEngine.Expand("class ${1:$TM_FILENAME_BASE}\n{\n\t${2|public,private|} int ${3:x};$0\n}\n\\$5 ${UNKNOWN:fallback}", "    ", "  ", vars);
            Assert.Equal("class Calc\n    {\n      public int x;\n    }\n    $5 fallback", ex.Text);
            Assert.Equal("class ".Length, ex.CaretOffset);
            Assert.Equal("Calc".Length, ex.SelectionLength);

            var only0 = SnippetEngine.Expand("a$0b", "", "\t", null);
            Assert.Equal("ab", only0.Text);
            Assert.Equal(1, only0.CaretOffset);
            var none = SnippetEngine.Expand("plain", "", "\t", null);
            Assert.Equal(5, none.CaretOffset);
        }

        [Fact]
        public void Languages_MatchIdsAliasesAndScopes()
        {
            Assert.True(SnippetEngine.AppliesTo(new CodeSnippetItem { Language = "csharp" }, "cs"));
            Assert.True(SnippetEngine.AppliesTo(new CodeSnippetItem { Language = "cs" }, "csharp"));
            Assert.True(SnippetEngine.AppliesTo(new CodeSnippetItem { Language = "javascript,typescript" }, "ts"));
            Assert.True(SnippetEngine.AppliesTo(new CodeSnippetItem { Language = "" }, "py")); // global snippet file
            Assert.False(SnippetEngine.AppliesTo(new CodeSnippetItem { Language = "python" }, "cs"));
        }

        [Fact]
        public void TabOnExactPrefix_ExpandsAndSelectsTheFirstPlaceholder()
        {
            var buf = new EditorTabBuffer { FileName = "a.cs", Language = "cs", Content = "class A\n{\n    propx\n}\n" };
            buf.SyncLinesFromContent();
            buf.CursorLine = 2; buf.CursorCol = buf.Lines[2].Length;
            var snippets = new List<CodeSnippetItem>
            {
                new CodeSnippetItem { Prefix = "propx", Language = "csharp", Body = "public ${1:int} ${2:Name} { get; set; }$0" },
                new CodeSnippetItem { Prefix = "ifx", Language = "csharp", Body = "if (${1:cond})\n{\n\t$0\n}" }
            };
            Assert.True(SnippetEngine.TryExpandPrefixAtCaret(buf, snippets, 4, true));
            buf.SyncContentFromLines();
            Assert.Equal("    public int Name { get; set; }", buf.Lines[2]);
            Assert.Equal("int", buf.GetSelectedText());
            Assert.True(buf.Undo());
            Assert.Equal("    propx", buf.Lines[2]);

            buf.Lines[2] = "    ifx"; buf.CursorCol = 7; buf.ClearSelection();
            Assert.True(SnippetEngine.TryExpandPrefixAtCaret(buf, snippets, 4, true));
            Assert.Equal(new[] { "    if (cond)", "    {", "        ", "    }" }, buf.Lines.Skip(2).Take(4).ToArray());
            Assert.Equal("cond", buf.GetSelectedText());

            buf.Lines[2] = "    nothing"; buf.CursorCol = 11; buf.ClearSelection();
            Assert.False(SnippetEngine.TryExpandPrefixAtCaret(buf, snippets, 4, true));
            var py = new EditorTabBuffer { Language = "py", Content = "propx" };
            py.SyncLinesFromContent(); py.CursorCol = 5;
            Assert.False(SnippetEngine.TryExpandPrefixAtCaret(py, snippets, 4, true)); // wrong language
        }
    }

    /// <summary>QA #14: the Avalonia UI creates the extension host, lists / installs / uninstalls VSIX packages, offers their themes and expands their snippets.</summary>
    [Collection("SequentialWorkbench")]
    public class VsixAvaloniaHeadlessTests : IDisposable
    {
        private readonly string settingsFilePath = IdePersistentSettings.GetSettingsFilePath();
        private readonly string settingsBackup;
        private readonly string previousRoot = MainWindow.ExtensionsRootOverride;
        private readonly string root = VsixFixture.NewDir("avroot");
        private readonly string src = VsixFixture.NewDir("avsrc");
        private readonly string ws = VsixFixture.NewDir("avws");

        public VsixAvaloniaHeadlessTests()
        {
            settingsBackup = File.Exists(settingsFilePath) ? File.ReadAllText(settingsFilePath) : null;
            MainWindow.ExtensionsRootOverride = root;
        }

        public void Dispose()
        {
            MainWindow.ExtensionsRootOverride = previousRoot;
            lock (StudioTheme.ExtensionThemes) StudioTheme.ExtensionThemes.Clear();
            try
            {
                if (settingsBackup != null) File.WriteAllText(settingsFilePath, settingsBackup);
                else if (File.Exists(settingsFilePath)) File.Delete(settingsFilePath);
            }
            catch { }
            foreach (var d in new[] { root, src, ws }) { try { Directory.Delete(d, true); } catch { } }
        }

        [AvaloniaFact]
        public void InstallFromVsix_ThemeInPicker_SnippetsExpand_ThemeRestoredAfterRestart_Uninstall()
        {
            string file = Path.Combine(ws, "Calc.cs");
            File.WriteAllText(file, "class Calc\n{\n    propx\n}\n");
            var w = new MainWindow(ws) { SuppressUnsavedPrompts = true };
            w.Show();
            try
            {
                Assert.NotNull(w.ExtensionHost);
                var pkg = w.InstallVsixFromFile(VsixFixture.Build(src));
                Assert.NotNull(pkg);
                Assert.Contains("Night Owl Lite", w.ExtensionsNotice);
                var theme = StudioTheme.GetAllBuiltInThemes().Single(t => t.Name == "Night Owl Lite (VSIX)");
                w.ApplyTheme(theme, persistSetting: true);
                Assert.Equal(theme.Id, w.CurrentSettings.ThemeId);

                // Tab on the snippet prefix in a C# file expands it with the first placeholder selected.
                var tab = w.OpenFileInTab(file, preview: false);
                tab.CursorLine = 2; tab.CursorCol = tab.Lines[2].Length; tab.ClearSelection();
                w.PrimaryEditor.Canvas.Focus();
                w.SimulateShortcut(Key.Tab, KeyModifiers.None);
                Assert.Equal("    public int MyProperty { get; set; }", tab.Lines[2]);
                Assert.Equal("int", tab.GetSelectedText());
                Assert.True(tab.IsDirty);
                Assert.Contains(w.SnippetsForBuffer(tab), s => s.Prefix == "ifx");
            }
            finally { w.Close(); }

            // Next start: the extension loads before the persisted theme is resolved.
            lock (StudioTheme.ExtensionThemes) StudioTheme.ExtensionThemes.Clear();
            var w2 = new MainWindow(ws) { SuppressUnsavedPrompts = true };
            w2.Show();
            try
            {
                Assert.Equal("Night Owl Lite (VSIX)", w2.CurrentTheme.Name);
                var pkg = w2.ExtensionHost.Extensions.Single(x => x.Id == "acme.night-owl-lite");
                w2.SwitchSidebarView("extensions");
                Assert.True(w2.UninstallExtension(pkg));
                Assert.Equal(StudioTheme.DefaultThemeId, w2.CurrentTheme.Id); // the active theme went away
                Assert.DoesNotContain(StudioTheme.GetAllBuiltInThemes(), t => t.Name.Contains("Owl Lite"));
            }
            finally { w2.Close(); }
        }

        [AvaloniaFact]
        public async System.Threading.Tasks.Task MarketplaceSearch_GoesThroughTheNetworkLedger_AndOfflineModeExplainsIt()
        {
            var w = new MainWindow(ws) { SuppressUnsavedPrompts = true };
            w.Show();
            bool wasOffline = NetworkLedger.OfflineMode;
            try
            {
                NetworkLedger.OfflineMode = true;
                int n = await w.SearchExtensionsAsync("theme");
                Assert.Equal(0, n);
                Assert.Contains("Offline Mode", w.ExtensionsNotice);
            }
            finally { NetworkLedger.OfflineMode = wasOffline; w.Close(); }
        }
    }
}
