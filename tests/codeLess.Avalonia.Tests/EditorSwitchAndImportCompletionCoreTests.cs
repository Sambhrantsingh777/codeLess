using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using codeLess.AvaloniaApp;
using codeLess.Studio;
using Xunit;

namespace codeLess.Avalonia.Tests
{
    /// <summary>v3.3.1 macOS report: Ctrl+Tab MRU switching model + VS Code-style import path / Python module completion.</summary>
    public class EditorSwitchAndImportCompletionCoreTests : IDisposable
    {
        private readonly string dir;

        public EditorSwitchAndImportCompletionCoreTests()
        {
            dir = Path.Combine(Path.GetTempPath(), "codeLess_ImportCompl_" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(dir);
            ImportCompletionEngine.InvalidateCaches();
        }

        public void Dispose()
        {
            PythonModuleCompletion.LayoutOverride = null;
            ImportCompletionEngine.InvalidateCaches();
            try { Directory.Delete(dir, true); } catch { }
        }

        private string Write(string rel, string content = "")
        {
            string p = Path.Combine(dir, rel.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(p));
            File.WriteAllText(p, content);
            return p;
        }

        private static ImportCompletionContext Ctx(string file, string lineWithCaret)
        {
            int col = lineWithCaret.IndexOf('|');
            string line = lineWithCaret.Remove(col, 1);
            return ImportCompletionEngine.DetectContext(file, new List<string> { line }, 0, col);
        }

        private static List<string> Labels(List<LspCompletionItem> items) => items.Select(i => i.Label).ToList();

        // ------------------------------------------------------------------ MRU / switcher

        [Fact]
        public void Mru_Orders_By_Activation_Prunes_Closed_And_Appends_Never_Activated()
        {
            var mru = new EditorMruList<string>();
            string a = "a", b = "b", c = "c", d = "d";
            var open = new List<string> { a, b, c, d };
            mru.Touch(a); mru.Touch(c); mru.Touch(b); mru.Touch(a);
            Assert.Equal(new[] { a, b, c, d }, mru.Ordered(open));
            open.Remove(b);
            Assert.Equal(new[] { a, c, d }, mru.Ordered(open));
            Assert.Equal(c, mru.MostRecentOther(open, a));
        }

        [Fact]
        public void Switcher_Ctrl_Tab_Selects_Previous_Shift_Selects_Least_Recent_And_Wraps()
        {
            var s = new EditorSwitcherSession<string>();
            var order = new List<string> { "active", "prev", "older", "oldest" };
            Assert.True(s.Begin(order, backwards: false));
            Assert.Equal("prev", s.Selected);
            s.Move(1); Assert.Equal("older", s.Selected);
            s.Move(1); s.Move(1); Assert.Equal("active", s.Selected); // wraps
            s.Move(-1); Assert.Equal("oldest", s.Selected);
            Assert.Equal("oldest", s.Commit());
            Assert.False(s.IsOpen);
            Assert.True(s.Begin(order, backwards: true));
            Assert.Equal("oldest", s.Selected);
            s.Cancel();
            Assert.Null(s.Selected);
        }

        [Fact]
        public void Mac_Bridge_Forwards_Only_Control_Tab_Key_Downs()
        {
            const ulong ctrl = 1UL << 18, shift = 1UL << 17, cmd = 1UL << 20;
            Assert.True(MacCtrlTabBridge.ShouldForward(10, 48, ctrl));
            Assert.True(MacCtrlTabBridge.ShouldForward(10, 48, ctrl | shift));
            Assert.False(MacCtrlTabBridge.ShouldForward(10, 48, ctrl | cmd)); // Cmd+Ctrl+Tab stays a system / menu key
            Assert.False(MacCtrlTabBridge.ShouldForward(10, 48, 0));
            Assert.False(MacCtrlTabBridge.ShouldForward(11, 48, ctrl)); // key up
            Assert.False(MacCtrlTabBridge.ShouldForward(10, 12, ctrl)); // Ctrl+Q
        }

        // ------------------------------------------------------------------ context detection

        [Theory]
        [InlineData("import x from '|';", "")]
        [InlineData("import x from './|';", "./")]
        [InlineData("import { a, b } from \"../lib/ut|\";", "../lib/ut")]
        [InlineData("import './sty|", "./sty")]
        [InlineData("export * from './a|'", "./a")]
        [InlineData("export { x } from '@/comp|'", "@/comp")]
        [InlineData("const m = require('./|')", "./")]
        [InlineData("const m = await import('rea|')", "rea")]
        [InlineData("} from 'lodash/|'", "lodash/")]
        [InlineData("jest.mock('./api|')", "./api")]
        public void Js_Import_Specifiers_Are_Detected(string line, string spec)
        {
            var ctx = Ctx("/w/src/main.ts", line);
            Assert.NotNull(ctx);
            Assert.Equal(ImportCompletionKind.Path, ctx.Kind);
            Assert.Equal(spec, ctx.Specifier);
            Assert.True(ctx.OmitScriptExtensions);
            int slash = spec.LastIndexOf('/');
            Assert.Equal(spec.Substring(slash + 1), ctx.Segment);
        }

        [Theory]
        [InlineData("/w/a.ts", "const s = 'foo/|';")]                 // ordinary string
        [InlineData("/w/a.ts", "// import x from './|'")]             // comment
        [InlineData("/w/a.ts", "import x from './a';|")]               // after the closing quote
        [InlineData("/w/a.ts", "import x from 'https://cdn/|'")]        // URL
        [InlineData("/w/a.css", "a { color: red; }|")]
        [InlineData("/w/a.html", "<p>src=\"|</p>")]
        [InlineData("/w/a.c", "#include <std|")]                        // system includes: not path completion
        [InlineData("/w/a.md", "[x](#head|")]                            // heading anchor
        [InlineData("/w/a.py", "x = 'import |'")]
        [InlineData("/w/a.py", "# import |")]
        [InlineData("/w/a.py", "import os |")]
        [InlineData("/w/a.txt", "import x from './|'")]
        public void No_Completion_Outside_Import_Positions(string file, string line)
        {
            Assert.Null(Ctx(file, line));
        }

        [Theory]
        [InlineData("/w/a.css", "@import './base/|';", "./base/", "css")]
        [InlineData("/w/a.scss", "@import url(\"img/|\");", "img/", "css")]
        [InlineData("/w/a.css", "body { background: url(../assets/lo|) }", "../assets/lo", "css")]
        [InlineData("/w/a.html", "<img class=\"x\" src=\"images/|\">", "images/", "html")]
        [InlineData("/w/a.html", "<link rel=\"stylesheet\" href='css/ma|'>", "css/ma", "html")]
        [InlineData("/w/a.cpp", "#include \"util/str|\"", "util/str", "c")]
        [InlineData("/w/a.md", "See [the guide](docs/gu|)", "docs/gu", "md")]
        [InlineData("/w/a.md", "![logo](./img/|)", "./img/", "md")]
        [InlineData("/w/a.md", "[ref]: ./other.m|", "./other.m", "md")]
        public void Css_Html_Include_And_Markdown_Paths_Are_Detected(string file, string line, string spec, string family)
        {
            var ctx = Ctx(file, line);
            Assert.NotNull(ctx);
            Assert.Equal(spec, ctx.Specifier);
            Assert.Equal(family, ctx.Family);
            Assert.False(ctx.OmitScriptExtensions);
        }

        [Fact]
        public void Python_Module_And_Name_Positions_Are_Detected()
        {
            var c = Ctx("/w/a.py", "import |");
            Assert.Equal(ImportCompletionKind.PythonModule, c.Kind);
            Assert.Equal("", c.Specifier);
            c = Ctx("/w/a.py", "import os, xml.etr|");
            Assert.Equal(ImportCompletionKind.PythonModule, c.Kind);
            Assert.Equal("xml.etr", c.Specifier);
            Assert.Equal("etr", c.Segment);
            c = Ctx("/w/a.py", "from ..pkg.|");
            Assert.Equal(ImportCompletionKind.PythonModule, c.Kind);
            Assert.Equal("..pkg.", c.Specifier);
            c = Ctx("/w/a.py", "    from pkg.sub import Foo, ba|");
            Assert.Equal(ImportCompletionKind.PythonName, c.Kind);
            Assert.Equal("pkg.sub", c.PythonModule);
            Assert.Equal("ba", c.Segment);
            c = Ctx("/w/a.py", "from . import |");
            Assert.Equal(ImportCompletionKind.PythonName, c.Kind);
            Assert.Equal(".", c.PythonModule);
        }

        [Fact]
        public void Segment_Chars_Triggers_And_Replace_Range()
        {
            var ctx = Ctx("/w/a.ts", "import x from './my-comp.|");
            Assert.True(ImportCompletionEngine.IsSegmentChar(ctx, '-'));
            Assert.True(ImportCompletionEngine.IsSegmentChar(ctx, '.'));
            Assert.False(ImportCompletionEngine.IsSegmentChar(ctx, '/'));
            Assert.False(ImportCompletionEngine.IsSegmentChar(ctx, '\''));
            Assert.True(ImportCompletionEngine.ShouldTrigger(ctx, "/"));
            Assert.True(ImportCompletionEngine.ShouldTrigger(ctx, "'"));

            // accept replaces the whole segment, keeps the rest of the path and the quote
            string line = "import x from './comp/Butt';";
            var c2 = ImportCompletionEngine.DetectContext("/w/a.ts", new List<string> { line }, 0, line.IndexOf("Butt") + 2);
            var item = new LspCompletionItem { Label = "Button", InsertText = "Button", Tag = new ImportCompletionTag() };
            ImportCompletionEngine.PrepareAccept(c2, item, line, 0, c2.SegmentStart, c2.Caret);
            Assert.Equal(line.IndexOf("Butt"), item.TextEdit.Range.StartCharacter);
            Assert.Equal(line.IndexOf("';"), item.TextEdit.Range.EndCharacter);
            var buf = new EditorTabBuffer { Content = line, FilePath = "/w/a.ts" };
            buf.SyncLinesFromContent();
            buf.CursorLine = 0; buf.CursorCol = c2.Caret;
            Assert.True(LspEditing.ApplyCompletion(buf, item));
            Assert.Equal("import x from './comp/Button';", buf.Lines[0]);

            // a folder's '/' is not doubled when one follows
            string l3 = "import x from './co/Button';";
            var c3 = ImportCompletionEngine.DetectContext("/w/a.ts", new List<string> { l3 }, 0, l3.IndexOf("co/") + 2);
            var folder = new LspCompletionItem { Label = "comp/", InsertText = "comp/", Tag = new ImportCompletionTag { IsFolder = true } };
            ImportCompletionEngine.PrepareAccept(c3, folder, l3, 0, c3.SegmentStart, c3.Caret);
            Assert.Equal("comp", folder.TextEdit.NewText);
        }

        [Fact]
        public void Session_Refilter_Uses_Path_Segment_Characters()
        {
            var ctx = Ctx("/w/a.ts", "import x from './my-|");
            var s = new LspCompletionSession { Line = 0, StartCol = ctx.SegmentStart, ImportContext = ctx, SegmentCharPredicate = c => ImportCompletionEngine.IsSegmentChar(ctx, c) };
            s.SetItems(new LspCompletionList
            {
                Items = new List<LspCompletionItem>
                {
                    new LspCompletionItem { Label = "my-button", FilterText = "my-button" },
                    new LspCompletionItem { Label = "my-card.css", FilterText = "my-card.css" },
                    new LspCompletionItem { Label = "other" }
                }
            });
            Assert.True(s.Refilter("import x from './my-", 0, "import x from './my-".Length));
            Assert.Equal(2, s.Visible.Count);
            Assert.True(s.Refilter("import x from './my-card.", 0, "import x from './my-card.".Length));
            Assert.Equal("my-card.css", s.Visible.Single().Label);
        }

        // ------------------------------------------------------------------ path listing

        private string MakeWebWorkspace()
        {
            Write("src/main.ts", "");
            Write("src/components/Button.tsx", "");
            Write("src/components/Card.vue", "");
            Write("src/utils/index.ts", "");
            Write("src/utils/format.js", "");
            Write("src/types/api.d.ts", "");
            Write("src/styles/app.css", "");
            Write("src/data.json", "{}");
            Write("src/.hidden.ts", "");
            Write("src/secret/key.ts", "");
            Write("assets/logo.png", "");
            Write(".gitignore", "secret/\n");
            Write("node_modules/react/package.json", "{}");
            Write("node_modules/@scope/pkg/package.json", "{}");
            Write("node_modules/lodash/fp/map.js", "");
            Write("package.json", "{ \"dependencies\": { \"react\": \"18\", \"not-installed\": \"1\" } }");
            Write("tsconfig.json", "{\n  // comment\n  \"compilerOptions\": {\n    \"baseUrl\": \".\",\n    \"paths\": { \"@/*\": [\"src/*\"], \"~utils\": [\"src/utils/index.ts\"], },\n  }\n}");
            return Path.Combine(dir, "src", "main.ts");
        }

        [Fact]
        public void Relative_Js_Listing_Folders_First_Omits_Script_Extensions_Respects_Gitignore()
        {
            string main = MakeWebWorkspace();
            var items = ImportCompletionEngine.BuildItems(Ctx(main, "import x from './|'"), main, dir);
            var labels = Labels(items);
            Assert.Equal(new[] { "components/", "styles/", "types/", "utils/" }, labels.Take(4).ToArray());
            Assert.Contains("data.json", labels);            // non-script extension kept
            Assert.DoesNotContain("main", labels);           // the importing file itself
            Assert.DoesNotContain("secret/", labels);         // .gitignore
            Assert.DoesNotContain(".hidden", labels);         // dot files only when typing '.'
            Assert.All(items.Where(i => i.Label.EndsWith("/")), i => { Assert.Equal(19, i.Kind); Assert.True(((ImportCompletionTag)i.Tag).Retrigger); });

            var comp = Labels(ImportCompletionEngine.BuildItems(Ctx(main, "import x from './components/|'"), main, dir));
            Assert.Equal(new[] { "Button", "Card.vue" }, comp.ToArray());
            var types = Labels(ImportCompletionEngine.BuildItems(Ctx(main, "import type { A } from './types/|'"), main, dir));
            Assert.Equal(new[] { "api" }, types.ToArray());
            var up = Labels(ImportCompletionEngine.BuildItems(Ctx(main, "import logo from '../assets/|'"), main, dir));
            Assert.Equal(new[] { "logo.png" }, up.ToArray());
        }

        [Fact]
        public void Tsconfig_Alias_Bare_Packages_And_Scopes()
        {
            string main = MakeWebWorkspace();
            var alias = Labels(ImportCompletionEngine.BuildItems(Ctx(main, "import B from '@/components/|'"), main, dir));
            Assert.Equal(new[] { "Button", "Card.vue" }, alias.ToArray());
            var aliasRoot = ImportCompletionEngine.BuildItems(Ctx(main, "import B from '@/|'"), main, dir);
            Assert.Contains("utils/", Labels(aliasRoot));

            var bare = ImportCompletionEngine.BuildItems(Ctx(main, "import React from '|'"), main, dir);
            var bl = Labels(bare);
            Assert.Contains("react", bl);
            Assert.Contains("@scope/pkg", bl);
            Assert.Contains("not-installed", bl);  // package.json dependency
            Assert.Contains("@/", bl);              // path alias
            Assert.Contains("~utils", bl);
            Assert.Contains("src/", bl);            // baseUrl "."
            Assert.Equal(9, bare.First(i => i.Label == "react").Kind);

            Assert.Equal(new[] { "pkg" }, Labels(ImportCompletionEngine.BuildItems(Ctx(main, "import p from '@scope/|'"), main, dir)).ToArray());
            Assert.Contains("fp/", Labels(ImportCompletionEngine.BuildItems(Ctx(main, "import m from 'lodash/|'"), main, dir)));
        }

        [Fact]
        public void Css_Html_C_And_Markdown_Keep_Extensions_And_Resolve_Relative_To_The_File()
        {
            MakeWebWorkspace();
            string css = Write("src/styles/theme.css", "");
            var cl = Labels(ImportCompletionEngine.BuildItems(Ctx(css, "@import './|';"), css, dir));
            Assert.Contains("app.css", cl);
            var url = Labels(ImportCompletionEngine.BuildItems(Ctx(css, "a { background: url(../../assets/|) }"), css, dir));
            Assert.Equal(new[] { "logo.png" }, url.ToArray());

            string html = Write("index.html", "");
            var hl = Labels(ImportCompletionEngine.BuildItems(Ctx(html, "<script src=\"src/utils/|\"></script>"), html, dir));
            Assert.Equal(new[] { "format.js", "index.ts" }, hl.ToArray());
            var rooted = Labels(ImportCompletionEngine.BuildItems(Ctx(html, "<img src=\"/assets/|\">"), html, dir));
            Assert.Equal(new[] { "logo.png" }, rooted.ToArray());

            Write("native/include/str.h", "");
            string c = Write("native/main.c", "");
            Assert.Equal(new[] { "str.h" }, Labels(ImportCompletionEngine.BuildItems(Ctx(c, "#include \"include/|\""), c, dir)).ToArray());

            string md = Write("docs/readme.md", "");
            Write("docs/guide.md", "");
            var ml = Labels(ImportCompletionEngine.BuildItems(Ctx(md, "see [guide](gu|)"), md, dir));
            Assert.Contains("guide.md", ml);
            Assert.DoesNotContain("readme.md", ml);
        }

        [Fact]
        public void Directory_Listings_Are_Cached_And_Revalidated_When_Entries_Change()
        {
            string main = MakeWebWorkspace();
            var ctx = Ctx(main, "import x from './components/|'");
            ImportCompletionEngine.BuildItems(ctx, main, dir);
            int before = DirectoryListingCache.Enumerations;
            ImportCompletionEngine.BuildItems(ctx, main, dir);
            Assert.Equal(before, DirectoryListingCache.Enumerations);
            Write("src/components/Avatar.tsx", "");
            Directory.SetLastWriteTimeUtc(Path.Combine(dir, "src", "components"), DateTime.UtcNow.AddSeconds(5));
            Assert.Contains("Avatar", Labels(ImportCompletionEngine.BuildItems(ctx, main, dir)));
        }

        [Fact]
        public void Merge_Dedupes_By_Label_And_Keeps_The_Server_Item()
        {
            var own = new List<LspCompletionItem> { new LspCompletionItem { Label = "utils/" }, new LspCompletionItem { Label = "main" } };
            var server = new List<LspCompletionItem> { new LspCompletionItem { Label = "utils", Detail = "server" } };
            var merged = ImportCompletionEngine.Merge(own, server);
            Assert.Equal(new[] { "utils", "main" }, merged.Select(i => i.Label).ToArray());
            Assert.Equal("server", merged[0].Detail);
        }

        // ------------------------------------------------------------------ Python

        private string MakePythonWorkspace(out string site)
        {
            Write("app/__init__.py", "");
            Write("app/models.py", "class User:\n    def method(self): pass\n\ndef make_user():\n    pass\n\nMAX_USERS = 10\n_private = 1\n");
            Write("app/views/__init__.py", "from .home import HomeView, render as do_render\n__all__ = ['HomeView', 'do_render', 'helper']\ndef helper(): pass\n");
            Write("app/views/home.py", "class HomeView: pass\n");
            Write("scripts/run.py", "");
            Write("tools.py", "def tool(): pass\n");
            Write("node_modules/x/y.py", "");
            Write(".venv/lib/python3.11/site-packages/ignored_from_workspace.py", "");
            Write("notes/readme.txt", ""); // not a package (no .py files)
            site = Path.Combine(dir, "fake-site");
            Write("fake-site/requests/__init__.py", "def get(url): pass\nclass Session: pass\n");
            Write("fake-site/requests/adapters.py", "");
            Write("fake-site/six.py", "");
            Write("fake-site/_cffi_backend.cpython-311-darwin.so", "");
            Write("fake-site/numpy_ext.cpython-311-darwin.so", "");
            Write("fake-site/requests-2.31.0.dist-info/METADATA", "");
            string s = site;
            PythonModuleCompletion.LayoutOverride = ws => new PythonEnvironmentLayout { Version = "3.11", SitePackages = new List<string> { s } };
            return Path.Combine(dir, "app", "models.py");
        }

        [Fact]
        public void Python_Top_Level_Modules_Workspace_Site_Packages_And_Stdlib()
        {
            string file = MakePythonWorkspace(out _);
            var items = ImportCompletionEngine.BuildItems(Ctx(file, "import |"), file, dir);
            var l = Labels(items);
            Assert.Contains("app", l);
            Assert.Contains("tools", l);
            Assert.Contains("scripts", l);
            Assert.DoesNotContain("node_modules", l);
            Assert.DoesNotContain("notes", l);
            Assert.Contains("requests", l);
            Assert.Contains("six", l);
            Assert.Contains("numpy_ext", l);
            Assert.DoesNotContain("requests-2.31.0.dist-info", l);
            Assert.Contains("os", l);
            Assert.Contains("tomllib", l);      // 3.11
            Assert.Contains("distutils", l);    // removed only in 3.12
            Assert.Equal("site-packages", items.First(i => i.Label == "requests").Detail);
            Assert.Equal("stdlib", items.First(i => i.Label == "json").Detail);
            Assert.All(items, i => Assert.Equal(9, i.Kind));

            var sub = Labels(ImportCompletionEngine.BuildItems(Ctx(file, "import app.|"), file, dir));
            Assert.Equal(new[] { "views", "models" }.OrderBy(x => x), sub.OrderBy(x => x));
            Assert.Contains("adapters", Labels(ImportCompletionEngine.BuildItems(Ctx(file, "from requests.|"), file, dir)));
            Assert.Contains("path", Labels(ImportCompletionEngine.BuildItems(Ctx(file, "import os.|"), file, dir))); // static stdlib submodules
        }

        [Fact]
        public void Python_Relative_Imports_And_Names()
        {
            string file = MakePythonWorkspace(out _);
            var rel = Labels(ImportCompletionEngine.BuildItems(Ctx(file, "from .|"), file, dir));
            Assert.Contains("views", rel);
            Assert.DoesNotContain("models", rel); // the current module
            var up = Labels(ImportCompletionEngine.BuildItems(Ctx(file, "from ..|"), file, dir));
            Assert.Contains("tools", up);
            Assert.Contains("app", up);

            var names = ImportCompletionEngine.BuildItems(Ctx(file, "from app.models import |"), file, dir);
            Assert.Equal(new[] { "User", "make_user", "MAX_USERS" }, Labels(names).ToArray());
            Assert.Equal(7, names[0].Kind);
            Assert.Equal(3, names[1].Kind);
            Assert.Equal(21, names[2].Kind);

            var pkgNames = Labels(ImportCompletionEngine.BuildItems(Ctx(file, "from app.views import |"), file, dir));
            Assert.Contains("home", pkgNames);       // submodule
            Assert.Contains("HomeView", pkgNames);   // __all__
            Assert.Contains("do_render", pkgNames);
            Assert.Contains("helper", pkgNames);

            var relNames = Labels(ImportCompletionEngine.BuildItems(Ctx(file, "from .views import |"), file, dir));
            Assert.Contains("HomeView", relNames);
            Assert.Contains("get", Labels(ImportCompletionEngine.BuildItems(Ctx(file, "from requests import |"), file, dir)));
            Assert.Contains("Session", Labels(ImportCompletionEngine.BuildItems(Ctx(file, "from requests import |"), file, dir)));
        }

        [Fact]
        public void Python_Stdlib_List_Follows_The_Interpreter_Version()
        {
            Assert.DoesNotContain("tomllib", PythonStdlibModules.TopLevel("3.10"));
            Assert.Contains("tomllib", PythonStdlibModules.TopLevel("3.12"));
            Assert.DoesNotContain("distutils", PythonStdlibModules.TopLevel("3.12"));
            Assert.Contains("distutils", PythonStdlibModules.TopLevel("3.11"));
            Assert.DoesNotContain("telnetlib", PythonStdlibModules.TopLevel("3.13"));
            Assert.Contains("asyncio", PythonStdlibModules.TopLevel(null));
            Assert.Contains("futures", PythonStdlibModules.Submodules("concurrent"));
        }

        [Fact]
        public void Python_Layout_Handles_The_Native_Windows_Venv_Layout()
        {
            if (Path.DirectorySeparatorChar != '\\') return; // the Lib\\site-packages layout is only probed on Windows
            string venv = Path.Combine(dir, "winvenv");
            string baseDir = Path.Combine(dir, "Python312");
            Write("winvenv/pyvenv.cfg", "home = " + baseDir + "\nversion = 3.12.4\n");
            Write("winvenv/Scripts/python.exe", "");
            Write("winvenv/Lib/site-packages/flask/__init__.py", "");
            Write("Python312/Lib/os.py", "");
            var layout = PythonEnvironmentLayout.FromInterpreter(Path.Combine(venv, "Scripts", "python.exe"), null, null);
            Assert.Equal("3.12", layout.Version);
            Assert.Contains(Path.Combine(venv, "Lib", "site-packages"), layout.SitePackages);
            Assert.Equal(Path.Combine(baseDir, "Lib"), layout.StdlibDir);
        }

        [Fact]
        public void Python_Layout_Is_Derived_From_A_Venv_Without_Running_It()
        {
            string venv = Path.Combine(dir, ".venv");
            Write(".venv/pyvenv.cfg", "home = " + Path.Combine(dir, "base", "bin") + "\nversion = 3.12.4\n");
            Write(".venv/bin/python", "");
            Write(".venv/lib/python3.12/site-packages/flask/__init__.py", "");
            Write("base/lib/python3.12/os.py", "");
            Write("base/lib/python3.12/json/__init__.py", "");
            var layout = PythonEnvironmentLayout.FromInterpreter(Path.Combine(venv, "bin", "python"), venv, null);
            Assert.Equal("3.12", layout.Version);
            Assert.Contains(Path.Combine(venv, "lib", "python3.12", "site-packages"), layout.SitePackages);
            Assert.Equal(Path.Combine(dir, "base", "lib", "python3.12"), layout.StdlibDir);

            // with a real stdlib folder, stdlib submodules come from disk
            string file = Write("main.py", "");
            PythonModuleCompletion.LayoutOverride = ws => layout;
            Assert.Contains("flask", Labels(ImportCompletionEngine.BuildItems(Ctx(file, "import fl|"), file, dir)));
            Assert.Contains("json", Labels(ImportCompletionEngine.BuildItems(Ctx(file, "import js|"), file, dir)));
        }
    }
}
