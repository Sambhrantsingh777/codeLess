using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using Avalonia;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using codeLess.AvaloniaApp;
using codeLess.Studio;
using Xunit;

namespace codeLess.Avalonia.Tests
{
    /// <summary>VS Code "follow link" (Cmd/Ctrl+Click, F12) resolution shared by the Windows and Avalonia editors.</summary>
    public class EditorLinkResolverTests : IDisposable
    {
        private readonly string root;

        public EditorLinkResolverTests()
        {
            root = Path.Combine(Path.GetTempPath(), "codeLess_Links_" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(root);
        }

        public void Dispose()
        {
            try { Directory.Delete(root, true); } catch { }
        }

        private string Write(string rel, string content)
        {
            string p = Path.Combine(root, rel.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(p));
            File.WriteAllText(p, content);
            return Path.GetFullPath(p);
        }

        private EditorLinkTarget At(string file, string[] lines, int line, string needle, int offsetInNeedle = 1)
        {
            int col = lines[line].IndexOf(needle, StringComparison.Ordinal);
            Assert.True(col >= 0, "needle not found: " + needle);
            return EditorLinkResolver.Resolve(lines, line, col + offsetInNeedle, file, root);
        }

        [Fact]
        public void JsTs_RelativeImport_ResolvesExtensionIndexAndEsmJsToTs()
        {
            string util = Write("src/util.ts", "export function helper() {}\n");
            string idx = Write("src/components/index.tsx", "export const Button = 1;\n");
            string esm = Write("src/esm.ts", "export const x = 1;\n");
            string main = Write("src/main.ts", "");
            var lines = new[]
            {
                "import { helper } from './util';",
                "import { Button } from \"./components\";",
                "import { x } from './esm.js';",
                "const s = require('../src/util');",
            };

            var t = At(main, lines, 0, "./util");
            Assert.Equal(EditorLinkTarget.KindFile, t.Kind);
            Assert.Equal(util, t.FilePath);
            Assert.Equal(lines[0].IndexOf("./util"), t.StartCol);
            Assert.Equal(lines[0].IndexOf("./util") + "./util".Length, t.EndCol);

            Assert.Equal(idx, At(main, lines, 1, "./components").FilePath);
            Assert.Equal(esm, At(main, lines, 2, "./esm.js").FilePath);
            Assert.Equal(util, At(main, lines, 3, "../src/util").FilePath);
        }

        [Fact]
        public void JsTs_ImportedIdentifier_JumpsIntoModule_EvenAcrossMultiLineImportsAndLaterUsages()
        {
            string util = Write("src/util.ts", "export const other = 2;\n\nexport function helper() {}\n");
            string main = Write("src/main.ts", "");
            var lines = new[]
            {
                "import {",
                "  other,",
                "  helper as h2,",
                "} from './util';",
                "",
                "helper();",
                "other + 1;",
            };
            var onBinding = At(main, lines, 1, "other");
            Assert.Equal(EditorLinkTarget.KindSymbol, onBinding.Kind);
            Assert.Equal(util, onBinding.FilePath);
            Assert.Equal("other", onBinding.SymbolName);

            var usage = At(main, lines, 6, "other");
            Assert.Equal(util, usage.FilePath);

            int dl, dc;
            Assert.True(EditorLinkResolver.FindDefinitionInLines(File.ReadAllLines(util), "helper", out dl, out dc));
            Assert.Equal(2, dl);
            Assert.Equal("export function ".Length, dc);
        }

        [Fact]
        public void JsTs_AliasAndNodeModules_Resolve()
        {
            string aliased = Write("src/lib/api.ts", "export const api = 1;\n");
            Write("node_modules/left-pad/package.json", "{ \"name\": \"left-pad\", \"main\": \"lib/index.js\" }");
            string pkgMain = Write("node_modules/left-pad/lib/index.js", "module.exports = 1;\n");
            string scoped = Write("node_modules/@scope/pkg/index.d.ts", "export {};\n");
            string main = Write("src/main.ts", "");
            var lines = new[] { "import { api } from '@/lib/api';", "import pad from 'left-pad';", "import x from '@scope/pkg';" };
            Assert.Equal(aliased, At(main, lines, 0, "@/lib/api").FilePath);
            Assert.Equal(pkgMain, At(main, lines, 1, "left-pad").FilePath);
            Assert.Equal(scoped, At(main, lines, 2, "@scope/pkg").FilePath);
        }

        [Fact]
        public void Python_FromImport_ModuleNameSubmoduleRelativeAndAlias()
        {
            string models = Write("app/models.py", "import os\n\nclass User:\n    pass\n\ndef make_user():\n    return User()\n");
            string pkgInit = Write("app/__init__.py", "");
            string sub = Write("app/services/billing.py", "def charge():\n    pass\n");
            Write("app/services/__init__.py", "");
            string main = Write("app/main.py", "");
            var lines = new[]
            {
                "from app.models import User, make_user as mk",
                "from .models import User",
                "from app.services import billing",
                "import app.services.billing as b",
                "from . import models",
                "mk()",
            };

            var mod = At(main, lines, 0, "models");
            Assert.Equal(models, mod.FilePath);
            var pkg = At(main, lines, 0, "app");
            Assert.Equal(pkgInit, pkg.FilePath);

            var name = At(main, lines, 0, "User");
            Assert.Equal(models, name.FilePath);
            Assert.Equal("User", name.SymbolName);

            var alias = At(main, lines, 0, "mk");
            Assert.Equal(models, alias.FilePath);
            Assert.Equal("make_user", alias.SymbolName);

            Assert.Equal(models, At(main, lines, 1, ".models", 2).FilePath);
            Assert.Equal(sub, At(main, lines, 2, "billing").FilePath);
            Assert.Equal(sub, At(main, lines, 3, "billing").FilePath);
            Assert.Equal(models, At(main, lines, 4, "models").FilePath);

            // A later usage of an aliased import jumps into the module.
            var usage = At(main, lines, 5, "mk");
            Assert.Equal(models, usage.FilePath);

            int dl, dc;
            Assert.True(EditorLinkResolver.FindDefinitionInLines(File.ReadAllLines(models), "make_user", out dl, out dc));
            Assert.Equal(5, dl);
        }

        [Fact]
        public void CInclude_RustMod_Markdown_Url_And_BarePathWithLineCol()
        {
            string header = Write("include/config.h", "#define X 1\n");
            string modRs = Write("src/net.rs", "pub fn f() {}\n");
            string readme = Write("docs/guide.md", "# Guide\n");
            string target = Write("src/app/main.ts", "a\nb\nc\n");
            string mainC = Write("src/main.c", "");
            string libRs = Write("src/lib.rs", "");
            string rootMd = Write("README.md", "");

            Assert.Equal(header, At(mainC, new[] { "#include \"config.h\"" }, 0, "config.h").FilePath);
            Assert.Equal(header, At(mainC, new[] { "#include <config.h>" }, 0, "config.h").FilePath);
            Assert.Equal(modRs, At(libRs, new[] { "pub mod net;" }, 0, "net").FilePath);
            Assert.Equal(readme, At(rootMd, new[] { "See [the guide](docs/guide.md) now." }, 0, "docs/guide.md").FilePath);

            var url = At(rootMd, new[] { "Docs: https://example.com/a?b=1." }, 0, "https://");
            Assert.Equal(EditorLinkTarget.KindUrl, url.Kind);
            Assert.Equal("https://example.com/a?b=1", url.Url);

            var bare = At(rootMd, new[] { "Error at src/app/main.ts:3:2 here" }, 0, "main.ts");
            Assert.Equal(target, bare.FilePath);
            Assert.Equal(3, bare.Line);
            Assert.Equal(2, bare.Column);

            var html = At(rootMd, new[] { "<script src=\"src/app/main.ts\"></script>" }, 0, "src/app");
            Assert.Equal(target, html.FilePath);
        }

        [Fact]
        public void Keywords_Whitespace_And_UnresolvedStrings_AreNotLinks_ButIdentifiersAreSymbols()
        {
            string main = Write("main.ts", "");
            var lines = new[] { "return   foo('not a file');" };
            Assert.Null(At(main, lines, 0, "return"));
            Assert.Null(EditorLinkResolver.Resolve(lines, 0, 7, main, root)); // whitespace
            var s = At(main, lines, 0, "not a file");
            Assert.True(s == null || s.Kind == EditorLinkTarget.KindSymbol); // a plain string is never a file link
            var sym = At(main, lines, 0, "foo");
            Assert.Equal(EditorLinkTarget.KindSymbol, sym.Kind);
            Assert.Equal("foo", sym.SymbolName);
            Assert.Null(sym.FilePath);
        }

        [Fact]
        public void FindDefinition_DeclarationsAcrossLanguages_And_StrictModeSkipsPlainUsages()
        {
            int l, c;
            Assert.True(EditorLinkResolver.FindDefinitionInLines(new[] { "var a = new Widget();", "public sealed class Widget : Base" }, "Widget", out l, out c));
            Assert.Equal(1, l);
            Assert.True(EditorLinkResolver.FindDefinitionInLines(new[] { "x = compute()", "def compute(a):" }, "compute", out l, out c));
            Assert.Equal(1, l);
            Assert.True(EditorLinkResolver.FindDefinitionInLines(new[] { "use(handler);", "export const handler = async () => {}" }, "handler", out l, out c));
            Assert.Equal(1, l);
            Assert.False(EditorLinkResolver.FindDefinitionInLines(new[] { "call(value);" }, "value", false, out l, out c));
        }
    }

    [Collection("SequentialWorkbench")]
    public class EditorPointerParityHeadlessTests : IDisposable
    {
        private readonly string _settingsFilePath;
        private readonly string _settingsBackupContent;
        private readonly bool _hadSettingsFile;

        public EditorPointerParityHeadlessTests()
        {
            _settingsFilePath = IdePersistentSettings.GetSettingsFilePath();
            _hadSettingsFile = !string.IsNullOrEmpty(_settingsFilePath) && File.Exists(_settingsFilePath);
            _settingsBackupContent = _hadSettingsFile ? File.ReadAllText(_settingsFilePath) : null;
        }

        public void Dispose()
        {
            MacKeymap.ForceMacKeymapOverride = null;
            try
            {
                if (_hadSettingsFile && !string.IsNullOrEmpty(_settingsFilePath) && _settingsBackupContent != null)
                    File.WriteAllText(_settingsFilePath, _settingsBackupContent);
                else if (!_hadSettingsFile && !string.IsNullOrEmpty(_settingsFilePath) && File.Exists(_settingsFilePath))
                    File.Delete(_settingsFilePath);
            }
            catch { }
        }

        private static Point PointFor(MainWindow window, AvaloniaVirtualEditorCanvas canvas, int row, int col, string lineText)
        {
            double x = AvaloniaVirtualEditorCanvas.GutterWidth + 8 + (canvas.VisualColumn(lineText, col) + 0.5) * canvas.CharWidth;
            double y = (row + 0.5) * canvas.LineHeight;
            var p = canvas.TranslatePoint(new Point(x, y), window);
            Assert.True(p.HasValue);
            return p.Value;
        }

        private static string AppliedCursor(AvaloniaVirtualEditorCanvas canvas)
        {
            var f = typeof(AvaloniaVirtualEditorCanvas).GetField("appliedCursorType", BindingFlags.NonPublic | BindingFlags.Instance);
            return f.GetValue(canvas)?.ToString() ?? "";
        }

        private static (MainWindow window, string dir, string main, string util) Setup()
        {
            string dir = Path.Combine(Path.GetTempPath(), "codeLess_CmdClick_" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(dir);
            string util = Path.Combine(dir, "util.ts");
            File.WriteAllText(util, "// helpers\n\nexport function helper() {\n  return 1;\n}\n");
            string main = Path.Combine(dir, "main.ts");
            File.WriteAllText(main, "import { helper } from './util';\n\nconst v = helper();\n");
            MacKeymap.ForceMacKeymapOverride = true;
            var window = new MainWindow(dir);
            window.Width = 1400;
            window.Height = 900;
            window.Show();
            return (window, dir, main, util);
        }

        [AvaloniaFact]
        public void CmdClick_OnImportPath_OpensFile_And_OnImportedUsage_JumpsToDefinition_And_BackReturns()
        {
            var (window, dir, main, util) = Setup();
            try
            {
                var tab = window.OpenFileInTab(main, preview: false);
                Dispatcher.UIThread.RunJobs();
                var canvas = window.PrimaryEditor.Canvas;
                string l0 = tab.Lines[0];

                // Cmd+hover underlines the module specifier and shows the hand pointer.
                var pSpec = PointFor(window, canvas, 0, l0.IndexOf("./util") + 2, l0);
                window.MouseMove(pSpec, RawInputModifiers.Meta);
                Assert.NotNull(canvas.HoveredLink);
                Assert.Equal(EditorLinkTarget.KindFile, canvas.HoveredLink.Kind);
                Assert.Equal("Hand", AppliedCursor(canvas));

                // Without the modifier it is a normal I-beam over text, arrow over the gutter.
                window.MouseMove(pSpec, RawInputModifiers.None);
                Assert.Null(canvas.HoveredLink);
                Assert.Equal("Ibeam", AppliedCursor(canvas));
                var gutter = canvas.TranslatePoint(new Point(20, canvas.LineHeight * 0.5), window).Value;
                window.MouseMove(gutter, RawInputModifiers.None);
                Assert.Equal("Arrow", AppliedCursor(canvas));

                // Cmd+Click on './util' opens util.ts.
                window.MouseDown(pSpec, MouseButton.Left, RawInputModifiers.Meta);
                window.MouseUp(pSpec, MouseButton.Left, RawInputModifiers.Meta);
                Dispatcher.UIThread.RunJobs();
                Assert.Equal(Path.GetFullPath(util), window.PrimaryEditor.CurrentBuffer.FilePath);

                // Back to main.ts, Cmd+Click the usage of the imported name -> definition line inside util.ts.
                window.OpenFileInTab(main, preview: false);
                Dispatcher.UIThread.RunJobs();
                string l2 = tab.Lines[2];
                tab.ScrollTopLine = 0;
                var pUse = PointFor(window, canvas, 2, l2.IndexOf("helper") + 1, l2);
                window.MouseDown(pUse, MouseButton.Left, RawInputModifiers.Meta);
                window.MouseUp(pUse, MouseButton.Left, RawInputModifiers.Meta);
                Dispatcher.UIThread.RunJobs();
                var cur = window.PrimaryEditor.CurrentBuffer;
                Assert.Equal(Path.GetFullPath(util), cur.FilePath);
                Assert.Equal(2, cur.CursorLine);
                Assert.Equal("export function ".Length, cur.CursorCol);

                // Navigate Back returns to main.ts.
                window.NavigateBack();
                Dispatcher.UIThread.RunJobs();
                Assert.Equal(Path.GetFullPath(main), window.PrimaryEditor.CurrentBuffer.FilePath);

                // F12 at the import specifier also opens the file.
                tab.CursorLine = 0;
                tab.CursorCol = l0.IndexOf("./util") + 1;
                window.GoToDefinitionAtCursor();
                Dispatcher.UIThread.RunJobs();
                Assert.Equal(Path.GetFullPath(util), window.PrimaryEditor.CurrentBuffer.FilePath);
            }
            finally
            {
                window.Close();
                try { Directory.Delete(dir, true); } catch { }
            }
        }

        [AvaloniaFact]
        public void RightClick_MovesCaretOutsideSelection_GutterClickSelectsLine_TripleClickSelectsLine()
        {
            var (window, dir, main, util) = Setup();
            try
            {
                var tab = window.OpenFileInTab(main, preview: false);
                Dispatcher.UIThread.RunJobs();
                var canvas = window.PrimaryEditor.Canvas;
                string l2 = tab.Lines[2];

                tab.CursorLine = 0;
                tab.CursorCol = 0;
                tab.ClearSelection();
                var pUse = PointFor(window, canvas, 2, l2.IndexOf("helper") + 2, l2) - new Point(canvas.CharWidth * 0.4, 0);
                window.MouseDown(pUse, MouseButton.Right, RawInputModifiers.None);
                window.MouseUp(pUse, MouseButton.Right, RawInputModifiers.None);
                Dispatcher.UIThread.RunJobs();
                Assert.Equal(2, tab.CursorLine);
                Assert.Equal(l2.IndexOf("helper") + 2, tab.CursorCol);

                // Close any context menu that opened.
                window.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);

                var gutter = canvas.TranslatePoint(new Point(24, canvas.LineHeight * 2.5), window).Value;
                window.MouseDown(gutter, MouseButton.Left, RawInputModifiers.None);
                window.MouseUp(gutter, MouseButton.Left, RawInputModifiers.None);
                Assert.True(tab.HasSelection());
                Assert.Equal(l2, tab.GetSelectedText().TrimEnd('\r', '\n'));
            }
            finally
            {
                window.Close();
                try { Directory.Delete(dir, true); } catch { }
            }
        }
    }
}
