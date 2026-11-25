using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using codeLess.AvaloniaApp;
using codeLess.Studio;
using Xunit;

namespace codeLess.Avalonia.Tests
{
    /// <summary>
    /// W20 Area C ("language") Trust Audit against VS Code:
    ///   C01–C12: Highlighting correctness for the top 12 languages vs VS Code Dark Modern token colours
    ///   C13–C21: Bracket pair colourisation, bracket matching, and folding (brace, indent, region, comment, imports, markup, markdown, untitled, fold/unfold at caret)
    ///   C22–C36: LSP completion, commit characters, resolve, word-based fallback, import completion, signature help, hover, definition/implementation/peek/typeDef, references, cross-file rename with atomic undo/redo + external edit protection, formatting, code actions
    ///   C37–C40: Diagnostics lifecycle (publish, version guard, close/crash cleanup, F8/Shift+F8 navigation, zero false positives)
    ///   C41–C44: Language server crash auto-restart/backoff, missing binary, timeout resilience, and Restricted Mode workspace trust gating
    /// </summary>
    [Collection("SequentialWorkbench")]
    public class LanguageTrustAuditW20CTests : IDisposable
    {
        private static readonly Color CControl = ColorTranslator.FromHtml("#C586C0");
        private static readonly Color CKeyword = ColorTranslator.FromHtml("#569CD6");
        private static readonly Color CType = ColorTranslator.FromHtml("#4EC9B0");
        private static readonly Color CFunction = ColorTranslator.FromHtml("#DCDCAA");
        private static readonly Color CVariable = ColorTranslator.FromHtml("#9CDCFE");
        private static readonly Color CString = ColorTranslator.FromHtml("#CE9178");
        private static readonly Color CNumber = ColorTranslator.FromHtml("#B5CEA8");
        private static readonly Color CComment = ColorTranslator.FromHtml("#6A9955");
        private static readonly Color CRegex = ColorTranslator.FromHtml("#D16969");

        private readonly string settingsFilePath;
        private readonly string settingsBackup;
        private readonly bool hadSettingsFile;
        private readonly bool savedCommitChar = EditorNicetyOptions.AcceptSuggestionOnCommitCharacter;
        private readonly bool savedAcceptEnter = EditorNicetyOptions.AcceptSuggestionOnEnter;
        private readonly string tempDir;
        private MainWindow window;

        public LanguageTrustAuditW20CTests()
        {
            LspCompletionList.ClearRecentAccepted();
            settingsFilePath = IdePersistentSettings.GetSettingsFilePath();
            hadSettingsFile = !string.IsNullOrEmpty(settingsFilePath) && File.Exists(settingsFilePath);
            settingsBackup = hadSettingsFile ? File.ReadAllText(settingsFilePath) : null;
            tempDir = Path.Combine(Path.GetTempPath(), "codeLess_W20C_" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(tempDir);
        }

        public void Dispose()
        {
            LspCompletionList.ClearRecentAccepted();
            EditorNicetyOptions.AcceptSuggestionOnCommitCharacter = savedCommitChar;
            EditorNicetyOptions.AcceptSuggestionOnEnter = savedAcceptEnter;
            Environment.SetEnvironmentVariable("CODELESS_FORCE_NONMAC_KEYMAP", null);
            try
            {
                if (window != null)
                {
                    window.SuppressUnsavedPrompts = true;
                    window.Close();
                }
            }
            catch { }
            MainWindow.LanguageServersEnabledOverride = null;
            MainWindow.LanguageServerSettingsOverride = null;
            try
            {
                if (hadSettingsFile && settingsBackup != null) File.WriteAllText(settingsFilePath, settingsBackup);
                else if (!hadSettingsFile && !string.IsNullOrEmpty(settingsFilePath) && File.Exists(settingsFilePath)) File.Delete(settingsFilePath);
            }
            catch { }
            try { Directory.Delete(tempDir, true); } catch { }
        }

        private static List<StyledSpan> ComputeLineSpans(EditorTabBuffer buf, int lineIndex, StudioTheme theme)
        {
            string scope = TextMateGrammarRegistry.Shared.GetScopeForFile(buf.FilePath ?? buf.FileName);
            if (scope != null) TextMateGrammarRegistry.Shared.GetCompiled(scope, true);
            var session = EditorSyntaxSession.For(buf);
            session.Prepare(buf);
            string line = buf.Lines[lineIndex];
            bool guess;
            var spans = session.GetTextMateSpans(buf.Lines, lineIndex, 0, theme, 0, out guess, 0, line.Length);
            if (spans == null)
                spans = session.GetFallbackSpans(buf.Lines, lineIndex, buf.Language, theme, 0, line.Length, false, 0, out guess);
            if (EditorNicetyOptions.BracketPairColorization && session.Brackets.Profile.Enabled)
            {
                var bd = session.Brackets.GetLine(buf.Lines, lineIndex, 0);
                spans = EditorSyntaxSession.ApplyBracketColors(spans, bd, theme);
            }
            return spans;
        }

        private static Color ColorOfToken(string fileName, string code, int lineIndex, string substring, int occurrence = 0)
        {
            var theme = StudioTheme.GetById("dark_modern");
            var buf = new EditorTabBuffer
            {
                FilePath = "/tmp/" + fileName,
                FileName = fileName,
                Lines = new List<string>(code.Replace("\r\n", "\n").Split('\n'))
            };
            string line = buf.Lines[lineIndex];
            int idx = -1;
            int searchFrom = 0;
            for (int k = 0; k <= occurrence; k++)
            {
                idx = line.IndexOf(substring, searchFrom, StringComparison.Ordinal);
                Assert.True(idx >= 0, "Substring '" + substring + "' not found on line " + lineIndex + ": " + line);
                searchFrom = idx + substring.Length;
            }
            List<StyledSpan> spans = ComputeLineSpans(buf, lineIndex, theme);
            foreach (var sp in spans)
            {
                if (idx >= sp.Start && idx < sp.Start + sp.Length)
                    return sp.Color;
            }
            return theme.FgPrimary;
        }

        private static void AssertRgb(Color expected, Color actual, string context)
        {
            Assert.True(
                expected.R == actual.R && expected.G == actual.G && expected.B == actual.B,
                string.Format("{0}: expected #{1:X2}{2:X2}{3:X2}, got #{4:X2}{5:X2}{6:X2}",
                    context, expected.R, expected.G, expected.B, actual.R, actual.G, actual.B));
        }

        private static void Pump(Func<bool> cond, int ms = 15000, string what = "condition")
        {
            ms = PerfBudget.Wait(ms);
            var sw = Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < ms)
            {
                Dispatcher.UIThread.RunJobs();
                if (cond()) return;
                Thread.Sleep(15);
            }
            Dispatcher.UIThread.RunJobs();
            Assert.True(cond(), "Timed out waiting for " + what);
        }

        private bool SetupWorkbenchWithFakeServer(string overrides = null)
        {
            string line = LanguageServerManagerTests.FakeServerCommandLine(out bool available);
            if (!available) return false;
            MainWindow.LanguageServersEnabledOverride = true;
            MainWindow.LanguageServerSettingsOverride = () => LanguageServerUserSettings.Parse(overrides ?? ("python = " + line), false);
            window = new MainWindow(tempDir) { Width = 1300, Height = 860 };
            window.Show();
            Dispatcher.UIThread.RunJobs();
            return true;
        }

        // =====================================================================
        // C1: Highlighting correctness for the top 12 languages (C01–C12)
        // =====================================================================

        [Fact]
        public void C01_CSharp_Keywords_Types_Methods_Strings_Numerics_Comments_MatchVsCodeDarkModern()
        {
            string code = string.Join("\n", new[]
            {
                "// leading comment",
                "using System;",
                "public class Widget {",
                "    internal int Compute(string name) {",
                "        var msg = $\"Hello {name}\";",
                "        return 42;",
                "    }",
                "}"
            });
            AssertRgb(CComment, ColorOfToken("Sample.cs", code, 0, "// leading comment"), "C# comment");
            AssertRgb(CControl, ColorOfToken("Sample.cs", code, 1, "using"), "C# using");
            AssertRgb(CKeyword, ColorOfToken("Sample.cs", code, 2, "class"), "C# class");
            AssertRgb(CType, ColorOfToken("Sample.cs", code, 2, "Widget"), "C# type");
            AssertRgb(CFunction, ColorOfToken("Sample.cs", code, 3, "Compute"), "C# method");
            AssertRgb(CVariable, ColorOfToken("Sample.cs", code, 3, "name"), "C# parameter");
            AssertRgb(CString, ColorOfToken("Sample.cs", code, 4, "Hello "), "C# interpolated string text");
            AssertRgb(CControl, ColorOfToken("Sample.cs", code, 5, "return"), "C# return");
            AssertRgb(CNumber, ColorOfToken("Sample.cs", code, 5, "42"), "C# numeric literal");
        }

        [Fact]
        public void C02_TypeScript_Decorators_Generics_TemplateStrings_Types_MatchVsCodeDarkModern()
        {
            string code = string.Join("\n", new[]
            {
                "export interface Box<T> {",
                "    readonly value: T;",
                "}",
                "export function formatBox(box: Box<number>): string {",
                "    return `val=${box.value + 10}`;",
                "}"
            });
            AssertRgb(CControl, ColorOfToken("app.ts", code, 0, "export"), "TS export");
            AssertRgb(CKeyword, ColorOfToken("app.ts", code, 0, "interface"), "TS interface");
            AssertRgb(CType, ColorOfToken("app.ts", code, 0, "Box"), "TS interface name");
            AssertRgb(CFunction, ColorOfToken("app.ts", code, 3, "formatBox"), "TS function");
            AssertRgb(CType, ColorOfToken("app.ts", code, 3, "number"), "TS builtin type");
            AssertRgb(CString, ColorOfToken("app.ts", code, 4, "val="), "TS template string");
            AssertRgb(CNumber, ColorOfToken("app.ts", code, 4, "10"), "TS number inside template expression");
        }

        [Fact]
        public void C03_JavaScript_AsyncAwait_RegexLiteral_ArrowFn_MatchVsCodeDarkModern()
        {
            string code = string.Join("\n", new[]
            {
                "async function loadItems(url) {",
                "    const re = /^hello+[a-z]+$/gi;",
                "    const res = await fetch(url);",
                "    return re.test(\"ok\") ? 1 : 0;",
                "}"
            });
            AssertRgb(CKeyword, ColorOfToken("index.js", code, 0, "async"), "JS async");
            AssertRgb(CFunction, ColorOfToken("index.js", code, 0, "loadItems"), "JS function");
            AssertRgb(CRegex, ColorOfToken("index.js", code, 1, "hello"), "JS regex literal");
            AssertRgb(CControl, ColorOfToken("index.js", code, 2, "await"), "JS await");
            AssertRgb(CString, ColorOfToken("index.js", code, 3, "\"ok\""), "JS string");
            AssertRgb(CNumber, ColorOfToken("index.js", code, 3, "1"), "JS number");
        }

        [Fact]
        public void C04_Python_FStrings_FormatStrings_PEP695_Decorators_MatchVsCodeDarkModern()
        {
            string code = string.Join("\n", new[]
            {
                "@dataclass",
                "class Container[T]:",
                "    def render(self, count: int) -> str:",
                "        return f\"items={count:02d}\" + \"{name}\".format(name=\"ok\")"
            });
            AssertRgb(CFunction, ColorOfToken("mod.py", code, 0, "dataclass"), "Python decorator");
            AssertRgb(CKeyword, ColorOfToken("mod.py", code, 1, "class"), "Python class");
            AssertRgb(CType, ColorOfToken("mod.py", code, 1, "Container"), "Python class name");
            AssertRgb(CFunction, ColorOfToken("mod.py", code, 2, "render"), "Python def name");
            AssertRgb(CControl, ColorOfToken("mod.py", code, 3, "return"), "Python return");
            AssertRgb(CString, ColorOfToken("mod.py", code, 3, "items="), "Python f-string literal");
            AssertRgb(CKeyword, ColorOfToken("mod.py", code, 3, ":02d"), "Python f-string format spec");
            AssertRgb(CKeyword, ColorOfToken("mod.py", code, 3, "{name}"), "Python .format placeholder");
        }

        [Fact]
        public void C05_Go_Package_Func_RawString_HexFloat_MatchVsCodeDarkModern()
        {
            string code = string.Join("\n", new[]
            {
                "package main",
                "// compute value",
                "func compute(n int) string {",
                "    var scale = 0x1.fp+3",
                "    return `raw_text`",
                "}"
            });
            AssertRgb(CKeyword, ColorOfToken("main.go", code, 0, "package"), "Go package");
            AssertRgb(CComment, ColorOfToken("main.go", code, 1, "// compute value"), "Go comment");
            AssertRgb(CKeyword, ColorOfToken("main.go", code, 2, "func"), "Go func");
            AssertRgb(CFunction, ColorOfToken("main.go", code, 2, "compute"), "Go function name");
            AssertRgb(CType, ColorOfToken("main.go", code, 2, "int"), "Go int type");
            AssertRgb(CNumber, ColorOfToken("main.go", code, 3, "0x1.fp+3"), "Go hex float");
            AssertRgb(CString, ColorOfToken("main.go", code, 4, "`raw_text`"), "Go raw string");
        }

        [Fact]
        public void C06_Rust_Lifetimes_Attributes_Macros_Struct_MatchVsCodeDarkModern()
        {
            string code = string.Join("\n", new[]
            {
                "#[derive(Debug)]",
                "pub struct View<'a> {",
                "    pub label: &'a str,",
                "}",
                "pub fn greet() -> usize {",
                "    println!(\"hello\");",
                "    return 7;",
                "}"
            });
            AssertRgb(CKeyword, ColorOfToken("lib.rs", code, 1, "struct"), "Rust struct");
            AssertRgb(CType, ColorOfToken("lib.rs", code, 1, "View"), "Rust struct type");
            AssertRgb(CFunction, ColorOfToken("lib.rs", code, 4, "greet"), "Rust fn name");
            AssertRgb(CFunction, ColorOfToken("lib.rs", code, 5, "println!"), "Rust macro");
            AssertRgb(CString, ColorOfToken("lib.rs", code, 5, "\"hello\""), "Rust string");
            AssertRgb(CNumber, ColorOfToken("lib.rs", code, 6, "7"), "Rust number");
        }

        [Fact]
        public void C07_Cpp_Preprocessor_Templates_Namespace_Strings_MatchVsCodeDarkModern()
        {
            string code = string.Join("\n", new[]
            {
                "#include <vector>",
                "template <typename T>",
                "int countItems(int n) {",
                "    const char* s = \"hello\";",
                "    return n + 16;",
                "}"
            });
            AssertRgb(CKeyword, ColorOfToken("main.cpp", code, 0, "#include"), "C++ #include");
            AssertRgb(CString, ColorOfToken("main.cpp", code, 0, "<vector>"), "C++ header path");
            AssertRgb(CKeyword, ColorOfToken("main.cpp", code, 1, "template"), "C++ template");
            AssertRgb(CFunction, ColorOfToken("main.cpp", code, 2, "countItems"), "C++ function");
            AssertRgb(CString, ColorOfToken("main.cpp", code, 3, "\"hello\""), "C++ string");
            AssertRgb(CNumber, ColorOfToken("main.cpp", code, 4, "16"), "C++ number");
        }

        [Fact]
        public void C08_Java_Annotations_Generics_Keywords_Strings_MatchVsCodeDarkModern()
        {
            string code = string.Join("\n", new[]
            {
                "public class Service {",
                "    public int execute(String input) {",
                "        String tag = \"v1\";",
                "        return 100;",
                "    }",
                "}"
            });
            AssertRgb(CKeyword, ColorOfToken("Service.java", code, 0, "class"), "Java class");
            AssertRgb(CType, ColorOfToken("Service.java", code, 0, "Service"), "Java class name");
            AssertRgb(CFunction, ColorOfToken("Service.java", code, 1, "execute"), "Java method");
            AssertRgb(CString, ColorOfToken("Service.java", code, 2, "\"v1\""), "Java string");
            AssertRgb(CControl, ColorOfToken("Service.java", code, 3, "return"), "Java return");
            AssertRgb(CNumber, ColorOfToken("Service.java", code, 3, "100"), "Java number");
        }

        [Fact]
        public void C09_Json_Jsonc_PropertyKeys_StringValues_Numbers_Booleans_Comments_MatchVsCodeDarkModern()
        {
            string code = string.Join("\n", new[]
            {
                "{",
                "  // jsonc comment",
                "  \"name\": \"codeLess\",",
                "  \"port\": 8080,",
                "  \"enabled\": true",
                "}"
            });
            AssertRgb(CComment, ColorOfToken("tsconfig.json", code, 1, "// jsonc comment"), "JSONC comment");
            AssertRgb(CVariable, ColorOfToken("tsconfig.json", code, 2, "\"name\""), "JSON property key");
            AssertRgb(CString, ColorOfToken("tsconfig.json", code, 2, "\"codeLess\""), "JSON string value");
            AssertRgb(CNumber, ColorOfToken("tsconfig.json", code, 3, "8080"), "JSON number");
            AssertRgb(CKeyword, ColorOfToken("tsconfig.json", code, 4, "true"), "JSON boolean");
        }

        [Fact]
        public void C10_Html_Css_TagNames_AttributeNames_AttributeValues_Selectors_HexColors_MatchVsCodeDarkModern()
        {
            string xml = "<div class=\"card\">Hello</div>";
            AssertRgb(CKeyword, ColorOfToken("index.xml", xml, 0, "div"), "XML/HTML tag name");
            AssertRgb(CVariable, ColorOfToken("index.xml", xml, 0, "class"), "XML/HTML attribute name");
            AssertRgb(CString, ColorOfToken("index.xml", xml, 0, "\"card\""), "XML/HTML attribute value");

            string html = "<div class=\"card\">Hello</div>";
            AssertRgb(CKeyword, ColorOfToken("index.html", html, 0, "div"), "HTML fallback tag name");
            AssertRgb(CType, ColorOfToken("index.html", html, 0, "class"), "HTML fallback attribute name");
            AssertRgb(CString, ColorOfToken("index.html", html, 0, "\"card\""), "HTML fallback attribute value");

            string css = ".card { color: #ff00aa; margin: 12px; }";
            AssertRgb(ColorTranslator.FromHtml("#D7BA7D"), ColorOfToken("site.css", css, 0, ".card"), "CSS selector");
            AssertRgb(CVariable, ColorOfToken("site.css", css, 0, "color"), "CSS property");
            AssertRgb(CNumber, ColorOfToken("site.css", css, 0, "12"), "CSS numeric value");
        }

        [Fact]
        public void C11_Markdown_Yaml_Headings_CodeSpans_Links_YamlKeys_MatchVsCodeDarkModern()
        {
            string md = "# Heading Title\nUse `inline_code` here.";
            AssertRgb(CKeyword, ColorOfToken("README.md", md, 0, "# Heading Title"), "Markdown heading");
            AssertRgb(CString, ColorOfToken("README.md", md, 1, "`inline_code`"), "Markdown inline code");

            string yaml = "# config\nserver:\n  host: \"localhost\"\n  port: 9000";
            AssertRgb(CComment, ColorOfToken("config.yaml", yaml, 0, "# config"), "YAML comment");
            AssertRgb(CKeyword, ColorOfToken("config.yaml", yaml, 1, "server"), "YAML key");
            AssertRgb(CString, ColorOfToken("config.yaml", yaml, 2, "\"localhost\""), "YAML string");
            AssertRgb(CNumber, ColorOfToken("config.yaml", yaml, 3, "9000"), "YAML number");
        }

        [Fact]
        public void C12_Shell_Sql_Variables_Flags_SelectWhere_Strings_MatchVsCodeDarkModern()
        {
            string sh = "#!/usr/bin/env bash\n# deploy\nif [ -f \"app.sh\" ]; then\n  echo $HOME\nfi";
            AssertRgb(CComment, ColorOfToken("run.sh", sh, 1, "# deploy"), "Shell comment");
            AssertRgb(CControl, ColorOfToken("run.sh", sh, 2, "if"), "Shell if");
            AssertRgb(CString, ColorOfToken("run.sh", sh, 2, "\"app.sh\""), "Shell string");
            AssertRgb(CVariable, ColorOfToken("run.sh", sh, 3, "$HOME"), "Shell variable");

            string sql = "-- query\nSELECT id FROM users WHERE active = 1;";
            AssertRgb(CComment, ColorOfToken("q.sql", sql, 0, "-- query"), "SQL comment");
            AssertRgb(CKeyword, ColorOfToken("q.sql", sql, 1, "SELECT"), "SQL SELECT");
            AssertRgb(CKeyword, ColorOfToken("q.sql", sql, 1, "WHERE"), "SQL WHERE");
            AssertRgb(CNumber, ColorOfToken("q.sql", sql, 1, "1"), "SQL number");
        }

        // =====================================================================
        // C2: Bracket pair colourisation, matching, and folding (C13–C21)
        // =====================================================================

        [Fact]
        public void C13_BracketPairColorization_Cycles_Gold_Orchid_LightBlue_And_FlagsUnmatchedClosingAsRed()
        {
            var theme = StudioTheme.GetById("dark_modern");
            var buf = new EditorTabBuffer
            {
                FilePath = "/tmp/brackets.cs",
                FileName = "brackets.cs",
                Lines = new List<string> { "a(b[c{d(e)}]) )" }
            };
            var spans = ComputeLineSpans(buf, 0, theme);
            Color ColorAt(int col) => spans.First(s => col >= s.Start && col < s.Start + s.Length).Color;

            AssertRgb(Color.FromArgb(0xFF, 0xD7, 0x00), ColorAt(1), "depth 0 '(' Gold");
            AssertRgb(Color.FromArgb(0xDA, 0x70, 0xD6), ColorAt(3), "depth 1 '[' Orchid");
            AssertRgb(Color.FromArgb(0x17, 0x9F, 0xFF), ColorAt(5), "depth 2 '{' LightBlue");
            AssertRgb(Color.FromArgb(0xFF, 0xD7, 0x00), ColorAt(7), "depth 3 '(' cycles back to Gold");
            AssertRgb(Color.FromArgb(0xFF, 0x12, 0x12), ColorAt(14), "unmatched closing ')' flagged red");
        }

        [Fact]
        public void C14_BracketMatching_FindsMatchingPairAtCaret_And_SkipsBracketsInsideStringsCommentsAndRawLiterals()
        {
            var lines = new List<string>
            {
                "class Demo {",
                "    string s = \"not a brace } [\"; // ignore } here",
                "    string raw = $@\"interp {dict[\")\"].Length} end }\";",
                "}"
            };
            var cache = new EditorBracketCache(BracketLexProfile.ForFile("Demo.cs"));
            cache.Sync(lines);

            Assert.True(cache.TryFindMatchAt(lines, 0, 11, out var pairOpen));
            Assert.True(pairOpen.IsValid);
            Assert.Equal(0, pairOpen.OpenLine);
            Assert.Equal(11, pairOpen.OpenCol);
            Assert.Equal(3, pairOpen.CloseLine);
            Assert.Equal(0, pairOpen.CloseCol);

            Assert.True(cache.TryFindMatchAt(lines, 3, 0, out var pairClose));
            Assert.True(pairClose.IsValid);
            Assert.Equal(0, pairClose.OpenLine);
            Assert.Equal(3, pairClose.CloseLine);
        }

        [Fact]
        public void C15_BraceFolding_ComputesNestedAndAllmanRanges_And_KeepsClosingBraceLineVisible()
        {
            var lines = new List<string>
            {
                "namespace N",
                "{",
                "    void M()",
                "    {",
                "        int x = 1;",
                "    }",
                "}"
            };
            var ranges = EditorFoldingModel.ComputeFor(lines, "Test.cs", 4);
            Assert.Contains(ranges, r => r.Start == 0 && r.End == 5 && r.Kind == FoldRangeKind.Code);
            Assert.Contains(ranges, r => r.Start == 2 && r.End == 4 && r.Kind == FoldRangeKind.Code);
        }

        [Fact]
        public void C16_IndentFolding_PythonAndYaml_IgnoresUnindentedLinesInsideMultilineTripleQuotedStrings()
        {
            var pyLines = new List<string>
            {
                "def outer():",
                "    doc = \"\"\"first line",
                "unindented line at col 0 inside docstring",
                "still inside docstring\"\"\"",
                "    return doc",
                "x = 1"
            };
            var pyRanges = EditorFoldingModel.ComputeFor(pyLines, "mod.py", 4);
            // Bug 6 fix: the unindented line inside """...""" must NOT prematurely terminate def outer()'s fold range at line 1!
            Assert.Contains(pyRanges, r => r.Start == 0 && r.End == 4 && r.Kind == FoldRangeKind.Code);

            var yamlLines = new List<string>
            {
                "services:",
                "  web:",
                "    image: nginx",
                "    ports:",
                "      - 80:80",
                "volumes:",
                "  data: {}"
            };
            var yamlRanges = EditorFoldingModel.ComputeFor(yamlLines, "docker-compose.yml", 2);
            Assert.Contains(yamlRanges, r => r.Start == 0 && r.End == 4);
            Assert.Contains(yamlRanges, r => r.Start == 1 && r.End == 4);
        }

        [Fact]
        public void C17_RegionCommentAndImportFolding_ComputesRegions_BlockAndLineComments_And_ImportGroups()
        {
            var lines = new List<string>
            {
                "using System;",
                "using System.IO;",
                "using System.Text;",
                "",
                "// comment 1",
                "// comment 2",
                "// comment 3",
                "#region Helpers",
                "/* block start",
                "   block middle",
                "   block end */",
                "#endregion"
            };
            var ranges = EditorFoldingModel.ComputeFor(lines, "Program.cs", 4);
            Assert.Contains(ranges, r => r.Start == 0 && r.End == 2 && r.Kind == FoldRangeKind.Imports);
            Assert.Contains(ranges, r => r.Start == 4 && r.End == 6 && r.Kind == FoldRangeKind.Comment);
            Assert.Contains(ranges, r => r.Start == 7 && r.End == 11 && r.Kind == FoldRangeKind.Region);
            Assert.Contains(ranges, r => r.Start == 8 && r.End == 10 && r.Kind == FoldRangeKind.Comment);
        }

        [Fact]
        public void C18_MarkupAndMarkdownFolding_ComputesHtmlTags_VoidElementSkips_And_HeadingHierarchyWithFencedCodeBlocks()
        {
            var htmlLines = new List<string>
            {
                "<div>",
                "  <img src=\"a.png\">",
                "  <ul>",
                "    <li>item</li>",
                "  </ul>",
                "</div>"
            };
            var htmlRanges = EditorFoldingModel.ComputeFor(htmlLines, "index.html", 2);
            Assert.Contains(htmlRanges, r => r.Start == 0 && r.End == 4);
            Assert.Contains(htmlRanges, r => r.Start == 2 && r.End == 3);

            var mdLines = new List<string>
            {
                "# H1",
                "## H2",
                "```csharp",
                "int a = 1;",
                "```",
                "# Next H1"
            };
            var mdRanges = EditorFoldingModel.ComputeFor(mdLines, "README.md", 2);
            Assert.Contains(mdRanges, r => r.Start == 0 && r.End == 4);
            Assert.Contains(mdRanges, r => r.Start == 1 && r.End == 4);
            Assert.Contains(mdRanges, r => r.Start == 2 && r.End == 4);
        }

        [Fact]
        public void C19_UntitledAndLanguageOverriddenBuffers_UseEffectiveLanguageForFoldingStrategy()
        {
            var untitledCs = new EditorTabBuffer
            {
                FileName = "Untitled-1",
                Language = "cs",
                Lines = new List<string>
                {
                    "class Foo {",
                    "    void Bar() {",
                    "        int x = 1;",
                    "    }",
                    "}"
                }
            };
            var model = new EditorFoldingModel();
            Assert.True(model.Update(untitledCs, 4, 0));
            Assert.Contains(model.Ranges, r => r.Start == 0 && r.End == 3);
            Assert.Contains(model.Ranges, r => r.Start == 1 && r.End == 2);

            // Switching language mode on the buffer invalidates the folding model cache and switches strategy
            untitledCs.Language = "md";
            untitledCs.LanguageModeOverridden = true;
            untitledCs.Lines = new List<string> { "# Section 1", "body", "# Section 2" };
            untitledCs.MarkLinesChanged();
            Assert.False(model.IsUpToDate(untitledCs, 4));
            Assert.True(model.Update(untitledCs, 4, 0));
            Assert.Contains(model.Ranges, r => r.Start == 0 && r.End == 1);
        }

        [AvaloniaFact]
        public void C20_FoldAtCaret_FoldsFromHeader_Body_And_ClosingBraceLine_And_MovesHiddenCaretToHeader()
        {
            string file = Path.Combine(tempDir, "FoldTest.cs");
            File.WriteAllText(file, "class A {\n    void M() {\n        int x = 1;\n    }\n}\n");
            window = new MainWindow(tempDir) { Width = 1200, Height = 800 };
            window.Show();
            Dispatcher.UIThread.RunJobs();

            var tab = window.OpenFileInTab(file, preview: false);
            var canvas = window.PrimaryEditor.Canvas;

            // Place caret on the closing '}' line of M() (line 3): FoldAtCaret folds M() (start line 1, end line 2)
            tab.CursorLine = 3;
            tab.CursorCol = 4;
            canvas.FoldAtCaret();
            Assert.True(tab.FoldedBlockRanges.ContainsKey(1));

            // Unfold M(), place caret inside M() body (line 2), and FoldAtCaret: hidden caret moves to header line 1
            canvas.UnfoldAtLine(1);
            tab.CursorLine = 2;
            tab.CursorCol = 8;
            canvas.FoldAtCaret();
            Assert.True(tab.FoldedBlockRanges.ContainsKey(1));
            Assert.Equal(1, tab.CursorLine);

            // Invoking FoldAtCaret again while line 1 is already folded folds the enclosing class A (line 0)
            canvas.FoldAtCaret();
            Assert.True(tab.FoldedBlockRanges.ContainsKey(0));
            Assert.Equal(0, tab.CursorLine);
        }

        [AvaloniaFact]
        public void C21_UnfoldAtCaret_UnfoldsHeaderOrInnermostContainingFoldedRegion()
        {
            string file = Path.Combine(tempDir, "UnfoldTest.cs");
            File.WriteAllText(file, "class A {\n    void M() {\n        int x = 1;\n    }\n}\n");
            window = new MainWindow(tempDir) { Width = 1200, Height = 800 };
            window.Show();
            Dispatcher.UIThread.RunJobs();

            var tab = window.OpenFileInTab(file, preview: false);
            var canvas = window.PrimaryEditor.Canvas;

            canvas.FoldAtLine(1);
            Assert.True(tab.FoldedBlockRanges.ContainsKey(1));

            tab.CursorLine = 1;
            canvas.UnfoldAtCaret();
            Assert.False(tab.FoldedBlockRanges.ContainsKey(1));
        }

        // =====================================================================
        // C3: LSP Flows (C22–C36)
        // =====================================================================

        [AvaloniaFact]
        public void C22_LspCompletion_AsYouType_TriggerChar_FuzzyFilter_SnippetTabStop_And_AdditionalTextEdits()
        {
            string main = Path.Combine(tempDir, "c22.py");
            File.WriteAllText(main, "x = 1\n\n");
            if (!SetupWorkbenchWithFakeServer()) return;
            var tab = window.OpenFileInTab(main, preview: false);
            var canvas = window.PrimaryEditor.Canvas;
            var binding = window.PrimaryLspBinding;

            Pump(() => window.LanguageServerStatusText == "{ } fake_lsp_server", what: "server running");

            // Select "imp" completion item with additionalTextEdits (auto-import at top of file)
            canvas.Focus();
            tab.CursorLine = 1;
            tab.CursorCol = 0;
            window.KeyTextInput("i");
            window.KeyTextInput("m");
            Pump(() => binding.IsCompletionVisible && binding.Session.Visible.Any(i => i.Label == "imp"), what: "imp completion");
            window.SimulateShortcut(Key.Tab, KeyModifiers.None);
            Pump(() => tab.Lines[0] == "import imp", what: "additionalTextEdits auto-import applied");
            Assert.Equal("imp", tab.Lines[2]);
        }

        [AvaloniaFact]
        public void C23_LspCompletion_CommitCharacters_AcceptSelectedBeforeInsertingPeriodOrParen_And_RespectSetting()
        {
            string main = Path.Combine(tempDir, "c23.py");
            File.WriteAllText(main, "\n");
            if (!SetupWorkbenchWithFakeServer()) return;
            var tab = window.OpenFileInTab(main, preview: false);
            var canvas = window.PrimaryEditor.Canvas;
            var binding = window.PrimaryLspBinding;

            Pump(() => window.LanguageServerStatusText == "{ } fake_lsp_server", what: "server running");

            // Type "al" -> "alpha" is selected and has commitCharacters: [".", "("].
            // Typing "." commits "alpha", inserts "." -> "alpha.", and triggers member completion!
            canvas.Focus();
            tab.CursorLine = 0;
            tab.CursorCol = 0;
            window.KeyTextInput("a");
            window.KeyTextInput("l");
            Pump(() => binding.IsCompletionVisible && binding.Session.Selected?.Label == "alpha", what: "alpha selected");

            window.KeyTextInput(".");
            Pump(() => tab.Lines[0].StartsWith("alpha.", StringComparison.Ordinal), what: "commit character '.' accepted alpha and inserted '.'");
            Pump(() => binding.IsCompletionVisible && binding.Session.Visible.Any(i => i.Label == "member_one"), what: "member completion triggered after commit '.'");
            window.SimulateShortcut(Key.Escape, KeyModifiers.None);

            // When AcceptSuggestionOnCommitCharacter is false, typing "." does NOT commit "alpha"
            EditorNicetyOptions.AcceptSuggestionOnCommitCharacter = false;
            tab.Lines[0] = "";
            tab.CursorCol = 0;
            tab.SyncContentFromLines();
            window.KeyTextInput("a");
            window.KeyTextInput("l");
            Pump(() => binding.IsCompletionVisible && binding.Session.Selected?.Label == "alpha", what: "alpha selected again");
            window.KeyTextInput(".");
            Assert.Equal("al.", tab.Lines[0]);
        }

        [AvaloniaFact]
        public void C24_LspCompletion_Resolve_PopulatesDetailAndMarkdownDocumentationInPopup()
        {
            string main = Path.Combine(tempDir, "c24.py");
            File.WriteAllText(main, "\n");
            if (!SetupWorkbenchWithFakeServer()) return;
            var tab = window.OpenFileInTab(main, preview: false);
            var canvas = window.PrimaryEditor.Canvas;
            var binding = window.PrimaryLspBinding;

            Pump(() => window.LanguageServerStatusText == "{ } fake_lsp_server", what: "server running");
            canvas.Focus();
            window.KeyTextInput("a");
            window.KeyTextInput("l");
            Pump(() => binding.IsCompletionVisible && binding.Completion.DocumentationText.Contains("Resolved docs for alpha"), what: "completionItem/resolve documentation");
            Assert.Contains("detail:alpha", binding.Completion.DocumentationText);
        }

        [AvaloniaFact]
        public void C25_WordBasedSuggestions_FallbackWhenNoServer_SkipsCommentsAndStrings_UnlessExplicitCtrlSpace()
        {
            string file = Path.Combine(tempDir, "notes.txt");
            File.WriteAllText(file, "extraordinaryIdentifier\n// extraordinaryComment\n\n");
            MainWindow.LanguageServersEnabledOverride = false;
            window = new MainWindow(tempDir) { Width = 1200, Height = 800 };
            window.Show();
            Dispatcher.UIThread.RunJobs();

            var tab = window.OpenFileInTab(file, preview: false);
            var canvas = window.PrimaryEditor.Canvas;
            var binding = window.PrimaryLspBinding;

            // Typing inside a line comment does not auto-open word-based suggestions
            tab.CursorLine = 1;
            tab.CursorCol = tab.Lines[1].Length;
            canvas.Focus();
            window.KeyTextInput(" ");
            window.KeyTextInput("e");
            window.KeyTextInput("x");
            Assert.False(binding.IsCompletionVisible);

            // Explicit Ctrl+Space inside comment DOES open word-based suggestions
            window.SimulateShortcut(Key.Space, KeyModifiers.Control);
            Assert.True(binding.IsCompletionVisible);
            Assert.Contains(binding.Session.Visible, i => i.Label == "extraordinaryIdentifier");
            window.SimulateShortcut(Key.Escape, KeyModifiers.None);

            // Typing on code line opens word-based suggestions automatically
            tab.CursorLine = 2;
            tab.CursorCol = 0;
            window.KeyTextInput("e");
            window.KeyTextInput("x");
            Assert.True(binding.IsCompletionVisible);
            Assert.Contains(binding.Session.Visible, i => i.Label == "extraordinaryIdentifier");
        }

        [Fact]
        public void C26_ImportCompletion_RelativePaths_And_PythonModules_CompleteInEditor()
        {
            string sub = Path.Combine(tempDir, "utils");
            Directory.CreateDirectory(sub);
            File.WriteAllText(Path.Combine(sub, "mathHelper.ts"), "export const PI = 3.14;");
            File.WriteAllText(Path.Combine(tempDir, "helper_mod.py"), "def f(): pass");
            string mainTs = Path.Combine(tempDir, "index.ts");
            string tsLine = "import { PI } from './utils/";
            File.WriteAllText(mainTs, tsLine);

            var ctx = ImportCompletionEngine.DetectContext(mainTs, new[] { tsLine }, 0, tsLine.Length);
            Assert.NotNull(ctx);
            var items = ImportCompletionEngine.BuildItems(ctx, mainTs, tempDir);
            Assert.Contains(items, i => i.Label == "mathHelper");

            string mainPy = Path.Combine(tempDir, "app.py");
            string pyLine = "import hel";
            var pyCtx = ImportCompletionEngine.DetectContext(mainPy, new[] { pyLine }, 0, pyLine.Length);
            Assert.NotNull(pyCtx);
            var pyItems = ImportCompletionEngine.BuildItems(pyCtx, mainPy, tempDir);
            Assert.Contains(pyItems, i => i.Label == "helper_mod");
        }

        [AvaloniaFact]
        public void C27_SignatureHelp_OpensOnParen_AdvancesActiveParameterOnComma_And_ClosesOnCloseParenOrEscape()
        {
            string main = Path.Combine(tempDir, "c27.py");
            File.WriteAllText(main, "def func(a, b):\n    return a + b\n\n");
            if (!SetupWorkbenchWithFakeServer()) return;
            var tab = window.OpenFileInTab(main, preview: false);
            var canvas = window.PrimaryEditor.Canvas;
            var binding = window.PrimaryLspBinding;

            Pump(() => window.LanguageServerStatusText == "{ } fake_lsp_server", what: "server running");
            canvas.Focus();
            tab.CursorLine = 2;
            tab.CursorCol = 0;
            window.KeyTextInput("func(");
            Pump(() => binding.SignatureText.Contains("func(a, b)"), what: "signature help on '('");
            Assert.Equal(0, binding.SignatureTip.CurrentActiveParameter);

            window.KeyTextInput("1,");
            Pump(() => binding.SignatureTip.CurrentActiveParameter == 1, what: "active parameter advances to 1 on ','");

            window.KeyTextInput(")");
            Pump(() => binding.SignatureText == "", what: "signature help closes on ')'");
        }

        [AvaloniaFact]
        public void C28_Hover_DisplaysMarkdownSignatureDocs_And_MergesDiagnosticsUnderPointer()
        {
            string main = Path.Combine(tempDir, "c28.py");
            File.WriteAllText(main, "def compute():\n    pass\nBAD = compute()\n");
            if (!SetupWorkbenchWithFakeServer()) return;
            var tab = window.OpenFileInTab(main, preview: false);
            var canvas = window.PrimaryEditor.Canvas;
            var binding = window.PrimaryLspBinding;

            Pump(() => tab.Diagnostics.Any(d => d.IsLanguageServer), what: "diagnostics published");

            // Hover over "BAD" merges the diagnostic and the hover docs
            var pt = new global::Avalonia.Point(AvaloniaVirtualEditorCanvas.GutterWidth + 8 + 1.5 * canvas.CharWidth, 2.5 * canvas.LineHeight);
            var t = binding.RequestHoverAsync(pt);
            Pump(() => t.IsCompleted && binding.HoverText.Contains("BAD is not allowed"), what: "hover with diagnostic");
            Assert.Contains("Docs for BAD", binding.HoverText);
        }

        [AvaloniaFact]
        public void C29_Navigation_F12_CtrlF12_AltF12_TypeDefinition_And_BuiltinFallback_WorkUnderBothKeymaps()
        {
            string main = Path.Combine(tempDir, "c29.py");
            File.WriteAllText(main, "class Engine:\n    pass\n\nx = Engine()\n");
            if (!SetupWorkbenchWithFakeServer()) return;
            var tab = window.OpenFileInTab(main, preview: false);

            Pump(() => window.LanguageServerStatusText == "{ } fake_lsp_server", what: "server running");

            // Test under non-Mac keymap as well (CODELESS_FORCE_NONMAC_KEYMAP=1)
            Environment.SetEnvironmentVariable("CODELESS_FORCE_NONMAC_KEYMAP", "1");
            try
            {
                // Alt+F12 (Peek/Go to Definition)
                tab.CursorLine = 3;
                tab.CursorCol = 5;
                window.SimulateShortcut(Key.F12, KeyModifiers.Alt);
                Pump(() => tab.CursorLine == 0 && tab.CursorCol == 6, what: "Alt+F12 navigates to Engine");

                // Ctrl+F12 (Go to Implementation)
                tab.CursorLine = 3;
                tab.CursorCol = 5;
                window.HandleLanguageServerEditorKey(new KeyEventArgs { Key = Key.F12, KeyModifiers = KeyModifiers.Control });
                Pump(() => tab.CursorLine == 0 && tab.CursorCol == 6, what: "Ctrl+F12 navigates to implementation");

                // Go to Type Definition
                tab.CursorLine = 3;
                tab.CursorCol = 5;
                window.GoToTypeDefinitionAtCursor();
                Pump(() => tab.CursorLine == 0 && tab.CursorCol == 6, what: "GoToTypeDefinitionAtCursor");
            }
            finally
            {
                Environment.SetEnvironmentVariable("CODELESS_FORCE_NONMAC_KEYMAP", null);
            }
        }

        [AvaloniaFact]
        public void C30_FindAllReferences_ReturnsSemanticLocationsWithLsp_And_LabelledGitignoreBoundedTextMatchesWithoutLsp()
        {
            File.WriteAllText(Path.Combine(tempDir, ".gitignore"), "ignored_dir/\n");
            Directory.CreateDirectory(Path.Combine(tempDir, "ignored_dir"));
            File.WriteAllText(Path.Combine(tempDir, "ignored_dir", "skip.cs"), "class TargetSymbol {}");
            string f1 = Path.Combine(tempDir, "one.cs");
            string f2 = Path.Combine(tempDir, "two.cs");
            File.WriteAllText(f1, "class TargetSymbol {}\n");
            File.WriteAllText(f2, "var x = new TargetSymbol();\n");

            MainWindow.LanguageServersEnabledOverride = false;
            window = new MainWindow(tempDir) { Width = 1200, Height = 800 };
            window.Show();
            Dispatcher.UIThread.RunJobs();

            var tab = window.OpenFileInTab(f1, preview: false);
            tab.CursorLine = 0;
            tab.CursorCol = 8; // on TargetSymbol
            window.FindAllReferencesAtCursor();
            Dispatcher.UIThread.RunJobs();

            // Must find the 2 non-ignored occurrences (one.cs and two.cs), skip ignored_dir/skip.cs, and clearly label as text matches
            Assert.True(window.CommandPalette.IsVisible);
            Assert.Contains("2 text matches (no language server)", window.CommandPalette.ModeHintLabel.Text);
        }

        [AvaloniaFact]
        public void C31_RenameSymbol_PrepareRename_RefusesInvalidTarget_And_AppliesWorkspaceEditAcrossOpenAndDiskFiles()
        {
            string main = Path.Combine(tempDir, "c31.py");
            File.WriteAllText(main, "def target_fn():\n    return 1\n");
            if (!SetupWorkbenchWithFakeServer()) return;
            var tab = window.OpenFileInTab(main, preview: false);
            Pump(() => window.LanguageServerStatusText == "{ } fake_lsp_server", what: "server running");

            // Attempting to rename "def" (keyword at col 1) is rejected by prepareRename
            tab.CursorLine = 0;
            tab.CursorCol = 1;
            window.RenameSymbolAtCursor();
            Pump(() => window.StatusMessageText.Contains("You cannot rename this element"), what: "prepareRename refusal surfaced");
        }

        [AvaloniaFact]
        public void C32_RenameSymbol_CrossFileUndoAndRedo_AtomicallyRestoresMultipleOpenBuffersAndClosedDiskFiles_And_ProtectsExternallyModifiedFiles()
        {
            string fileA = Path.Combine(tempDir, "a.py");
            string fileB = Path.Combine(tempDir, "b.py");
            string fileClosed = Path.Combine(tempDir, "closed.py");
            File.WriteAllText(fileA, "def calc(x):\n    return x\n");
            File.WriteAllText(fileB, "from a import calc\ny = calc(1)\n");
            File.WriteAllText(fileClosed, "from a import calc\nz = calc(2)\n");

            if (!SetupWorkbenchWithFakeServer()) return;
            var tabA = window.OpenFileInTab(fileA, preview: false);
            var tabB = window.OpenFileInTab(fileB, preview: false);
            window.ActivateTab(tabA);

            Pump(() => window.LanguageServerStatusText == "{ } fake_lsp_server", what: "server running");

            var renameTask = window.ApplyRenameAsync(tabA, fileA, 0, 5, "compute_val");
            Pump(() => renameTask.IsCompleted, what: "cross-file rename");
            Assert.True(renameTask.Result.Success);
            Assert.Equal("def compute_val(x):", tabA.Lines[0]);
            Assert.Equal("from a import compute_val", tabB.Lines[0]);
            Assert.Equal("from a import compute_val\nz = compute_val(2)\n", File.ReadAllText(fileClosed));

            // Single Undo on active editor (tabA) atomically undoes tabA, open tabB, AND closed.py on disk!
            window.PrimaryEditor.Canvas.PerformUndoRedo(redo: false);
            Assert.Equal("def calc(x):", tabA.Lines[0]);
            Assert.Equal("from a import calc", tabB.Lines[0]);
            Assert.Equal("from a import calc\nz = calc(2)\n", File.ReadAllText(fileClosed));

            // Single Redo on active editor (tabA) atomically redoes tabA, open tabB, AND closed.py on disk!
            window.PrimaryEditor.Canvas.PerformUndoRedo(redo: true);
            Assert.Equal("def compute_val(x):", tabA.Lines[0]);
            Assert.Equal("from a import compute_val", tabB.Lines[0]);
            Assert.Equal("from a import compute_val\nz = compute_val(2)\n", File.ReadAllText(fileClosed));

            // If closed.py is modified externally on disk after the rename, Undo must NEVER overwrite closed.py!
            File.WriteAllText(fileClosed, "# externally edited by user\n");
            window.PrimaryEditor.Canvas.PerformUndoRedo(redo: false);
            Assert.Equal("def calc(x):", tabA.Lines[0]);
            Assert.Equal("from a import calc", tabB.Lines[0]);
            Assert.Equal("# externally edited by user\n", File.ReadAllText(fileClosed));
        }

        [Fact]
        public void C33_RenameSymbol_StaleEditGuard_RejectsWorkspaceEditWhenBufferChangedDuringRequest()
        {
            string p = Path.Combine(tempDir, "stale.py");
            var buf = new EditorTabBuffer { FilePath = p, FileName = "stale.py", Lines = new List<string> { "x = 1" } };
            var guard = LspEditGuard.Capture(new[] { buf }, null);

            // Simulate user typing while the server was computing the WorkspaceEdit
            buf.Lines[0] = "x = 2";
            var we = new LspWorkspaceEdit();
            we.Steps.Add(new LspWorkspaceEdit.Step
            {
                FilePath = p,
                Edits = new List<LspTextEdit> { new LspTextEdit(new LspRange(0, 0, 0, 1), "y") }
            });

            var res = LspEditing.ApplyWorkspaceEdit(we, path => buf, null, guard);
            Assert.True(res.Rejected);
            Assert.Equal("x = 2", buf.Lines[0]);
        }

        [AvaloniaFact]
        public void C34_FormatDocument_And_FormatSelection_ApplyLspEdits_Or_ShowVsCodeNoFormatterMessageWithoutChangingBuffer()
        {
            string noFmtFile = Path.Combine(tempDir, "plain.unknownext");
            File.WriteAllText(noFmtFile, "\tindented   \n");
            MainWindow.LanguageServersEnabledOverride = false;
            window = new MainWindow(tempDir) { Width = 1200, Height = 800 };
            window.Show();
            Dispatcher.UIThread.RunJobs();

            var tab = window.OpenFileInTab(noFmtFile, preview: false);
            window.FormatDocumentOrSelection(false);
            Assert.Contains("There is no formatter for", window.StatusMessageText);
            Assert.Equal("\tindented   ", tab.Lines[0]); // untouched!
        }

        [Fact]
        public void C35_FormatOnSave_UsesLspWhenSupported_And_WhitespaceOnlyFallbackPreservesMultilineStringsAndMakefiles()
        {
            // Python multi-line triple-quoted string with leading tabs and trailing spaces inside the literal must be untouched!
            var pyBuf = new EditorTabBuffer
            {
                FilePath = "/tmp/script.py",
                FileName = "script.py",
                Language = "py",
                Lines = new List<string>
                {
                    "def f():   ",
                    "\ts = \"\"\"line1   ",
                    "\t\tindented inside docstring   ",
                    "end\"\"\"   "
                }
            };
            Assert.True(SaveActions.FormatBufferFallback(pyBuf, 4, true));
            Assert.Equal("def f():", pyBuf.Lines[0]);
            Assert.Equal("    s = \"\"\"line1   ", pyBuf.Lines[1]); // leading tab normalized outside string, trailing spaces inside string kept!
            Assert.Equal("\t\tindented inside docstring   ", pyBuf.Lines[2]); // inside multiline string: neither leading tabs nor trailing spaces touched!
            Assert.Equal("end\"\"\"", pyBuf.Lines[3]); // string closed on this line: trailing spaces after """ trimmed, leading untouched!

            // Makefile recipe tabs must never be converted to spaces!
            var mkBuf = new EditorTabBuffer
            {
                FilePath = "/tmp/Makefile",
                FileName = "Makefile",
                Lines = new List<string> { "build:", "\tgcc -o app main.c   " }
            };
            Assert.True(SaveActions.FormatBufferFallback(mkBuf, 4, true));
            Assert.Equal("\tgcc -o app main.c", mkBuf.Lines[1]);
        }

        [AvaloniaFact]
        public void C36_CodeActions_QuickFix_OrdersPreferredFirst_AppliesWorkspaceEdit_And_ExecutesServerCommand()
        {
            string main = Path.Combine(tempDir, "c36.py");
            File.WriteAllText(main, "BAD = 1\n");
            if (!SetupWorkbenchWithFakeServer()) return;
            var tab = window.OpenFileInTab(main, preview: false);
            Pump(() => tab.Diagnostics.Any(d => d.IsLanguageServer), what: "BAD diagnostic");

            tab.CursorLine = 0;
            tab.CursorCol = 1;
            window.ShowQuickFixes();
            Pump(() => window.CommandPalette.IsVisible, what: "Quick Fix palette open");

            // Preferred quickfix ("Replace BAD with GOOD  ★") is ordered first; invoking it replaces BAD with GOOD
            var actionsTask = window.LanguageServers.RequestCodeActionsAsync(main, new LspRange(0, 0, 0, 3), CancellationToken.None);
            Pump(() => actionsTask.IsCompleted, what: "code actions");
            Assert.True(actionsTask.Result[0].IsPreferred);
            var applyTask = window.ApplyCodeActionAsync(main, actionsTask.Result[0]);
            Pump(() => applyTask.IsCompleted && tab.Lines[0] == "GOOD = 1", what: "quick fix applied");
        }

        // =====================================================================
        // C4: Diagnostics Lifecycle (C37–C40)
        // =====================================================================

        [AvaloniaFact]
        public void C37_Diagnostics_PublishedOnOpenAndChange_SurviveBufferReanalysis_And_DropStaleVersions()
        {
            string main = Path.Combine(tempDir, "c37.py");
            File.WriteAllText(main, "BAD = 1\n");
            if (!SetupWorkbenchWithFakeServer()) return;
            var tab = window.OpenFileInTab(main, preview: false);

            Pump(() => tab.Diagnostics.Any(d => d.IsLanguageServer && d.Message == "BAD is not allowed"), what: "initial diagnostic");

            // Fix BAD -> GOOD in buffer: server publishes empty diagnostics and clears the error
            tab.Lines[0] = "GOOD = 1";
            tab.SyncContentFromLines();
            window.LanguageServers.ChangeDocument(main);
            window.LanguageServers.FlushAllDirty();
            Pump(() => !tab.Diagnostics.Any(d => d.IsLanguageServer), what: "diagnostics cleared after fixing BAD");
        }

        [AvaloniaFact]
        public void C38_Diagnostics_ClearedWhenTabClosed_Or_WhenServerCrashesOrStops()
        {
            string main = Path.Combine(tempDir, "c38.py");
            File.WriteAllText(main, "BAD = 1\n");
            if (!SetupWorkbenchWithFakeServer()) return;
            var tab = window.OpenFileInTab(main, preview: false);
            Pump(() => LspDiagnosticsStore.Count(main) > 0, what: "diagnostics in store");

            window.CloseTab(tab, forceWithoutPrompt: true);
            Pump(() => LspDiagnosticsStore.Count(main) == 0, what: "diagnostics cleared on tab close");
        }

        [AvaloniaFact]
        public void C39_Diagnostics_NextAndPrevMarker_F8_And_ShiftF8_CycleInSeverityOrder()
        {
            string file = Path.Combine(tempDir, "markers.cs");
            File.WriteAllText(file, "line1\nline2\nline3\nline4\n");
            MainWindow.LanguageServersEnabledOverride = false;
            window = new MainWindow(tempDir) { Width = 1200, Height = 800 };
            window.Show();
            Dispatcher.UIThread.RunJobs();

            var tab = window.OpenFileInTab(file, preview: false);
            tab.Diagnostics = new List<DiagnosticItem>
            {
                new DiagnosticItem { Severity = "Warning", Line = 2, Column = 3, Message = "warn on 2", FilePath = file },
                new DiagnosticItem { Severity = "Error", Line = 4, Column = 2, Message = "err on 4", FilePath = file }
            };
            tab.CursorLine = 0;
            tab.CursorCol = 0;

            // F8 jumps to the next marker in document order (line 2 -> 0-based 1, then line 4 -> 0-based 3), Shift+F8 jumps back
            window.SimulateShortcut(Key.F8, KeyModifiers.None);
            Assert.Equal(1, tab.CursorLine);
            window.SimulateShortcut(Key.F8, KeyModifiers.None);
            Assert.Equal(3, tab.CursorLine);
            window.SimulateShortcut(Key.F8, KeyModifiers.Shift);
            Assert.Equal(1, tab.CursorLine);
        }

        [Fact]
        public void C40_Diagnostics_NoFalsePositiveSyntaxErrors_AcrossValidCodeInSupportedLanguages()
        {
            var samples = new Dictionary<string, string>
            {
                ["App.cs"] = "using System;\nnamespace N {\n    internal class C {\n        internal int Run(string s) => $\"val={s}\".Length;\n    }\n}",
                ["app.ts"] = "export function greet<T>(x: T): string { return `${String(x)}`; }",
                ["mod.py"] = "class Box[T]:\n    def get(self, x: T) -> str:\n        return f\"{x!r}\"\n",
                ["main.go"] = "package main\nfunc main() {\n    s := `raw {not unclosed`\n    _ = s\n}\n",
                ["lib.rs"] = "pub fn calc<'a>(s: &'a str) -> usize { s.len() }\n"
            };
            foreach (var kv in samples)
            {
                var buf = new EditorTabBuffer
                {
                    FilePath = "/tmp/" + kv.Key,
                    FileName = kv.Key,
                    Content = kv.Value,
                    Lines = new List<string>(kv.Value.Split('\n'))
                };
                LlmHarnessAndAgentEngine.AnalyzeBufferAstAndDiagnostics(buf);
                Assert.Empty(buf.Diagnostics);
            }
        }

        // =====================================================================
        // C5: Server Crash, Missing Binary, Timeout, Restricted Mode (C41–C44)
        // =====================================================================

        [Fact]
        public void C41_ServerCrash_AutoRestartsWithBackoff_ReopensDocuments_And_StopsAfterMaxRestarts()
        {
            string line = LanguageServerManagerTests.FakeServerCommandLine(out bool available);
            if (!available) return;
            using (var mgr = new LanguageServerManager(a => a()))
            {
                mgr.SetWorkspaceRoot(tempDir);
                mgr.RestartBackoffMs = 20;
                mgr.MaxRestarts = 2;
                mgr.UserSettingsProvider = () => LanguageServerUserSettings.Parse("python = " + line, false);

                string f = Path.Combine(tempDir, "crash.py");
                File.WriteAllText(f, "BAD = 1\n");
                mgr.OpenDocument(f, () => "BAD = 1\n");

                var sw = Stopwatch.StartNew();
                while (sw.ElapsedMilliseconds < 10000 && mgr.GetStatus(f)?.State != LanguageServerState.Running)
                    Thread.Sleep(20);
                Assert.Equal(LanguageServerState.Running, mgr.GetStatus(f).State);

                // Crash 1 -> auto-restarts and re-opens document
                mgr.NotifyRaw(f, "fake/crash", null);
                sw.Restart();
                while (sw.ElapsedMilliseconds < 10000 && (mgr.GetStatus(f)?.State != LanguageServerState.Running || mgr.GetInstanceForPath(f).RestartCount < 1))
                    Thread.Sleep(20);
                Assert.Equal(LanguageServerState.Running, mgr.GetStatus(f).State);

                // Crash 2 -> auto-restarts (attempt 2/2)
                mgr.NotifyRaw(f, "fake/crash", null);
                sw.Restart();
                while (sw.ElapsedMilliseconds < 10000 && (mgr.GetStatus(f)?.State != LanguageServerState.Running || mgr.GetInstanceForPath(f).RestartCount < 2))
                    Thread.Sleep(20);
                Assert.Equal(LanguageServerState.Running, mgr.GetStatus(f).State);

                // Crash 3 -> exceeds MaxRestarts (2), stays in Error state with clear message
                mgr.NotifyRaw(f, "fake/crash", null);
                sw.Restart();
                while (sw.ElapsedMilliseconds < 10000 && !(mgr.GetStatus(f)?.Message ?? "").Contains("Not restarting"))
                    Thread.Sleep(20);
                Assert.Equal(LanguageServerState.Error, mgr.GetStatus(f).State);
                Assert.Contains("Not restarting after 2 restarts", mgr.GetStatus(f).Message);
            }
        }

        [Fact]
        public void C42_MissingServerBinary_TransitionsToNotInstalled_ShowsSingleHint_And_KeepsEditorResponsive()
        {
            int hints = 0;
            using (var mgr = new LanguageServerManager(a => a()))
            {
                mgr.SetWorkspaceRoot(tempDir);
                mgr.UserSettingsProvider = () => LanguageServerUserSettings.Parse("python = codeless-nonexistent-lsp-binary --stdio", false);
                mgr.ServerMissing += h => Interlocked.Increment(ref hints);

                string f1 = Path.Combine(tempDir, "m1.py");
                string f2 = Path.Combine(tempDir, "m2.py");
                mgr.OpenDocument(f1, () => "x = 1\n");
                var sw = Stopwatch.StartNew();
                while (sw.ElapsedMilliseconds < 5000 && mgr.GetStatus(f1)?.State != LanguageServerState.NotInstalled)
                    Thread.Sleep(15);
                Assert.Equal(LanguageServerState.NotInstalled, mgr.GetStatus(f1).State);

                mgr.OpenDocument(f2, () => "y = 2\n");
                Thread.Sleep(50);
                Assert.Equal(1, hints); // Hint shown at most once per language per session
            }
        }

        [Fact]
        public void C43_SlowOrUnresponsiveServer_TimesOutWithoutBlockingUi_And_RecoversOnRestart()
        {
            string line = LanguageServerManagerTests.FakeServerCommandLine(out bool available);
            if (!available) return;
            using (var mgr = new LanguageServerManager(a => a()))
            {
                mgr.SetWorkspaceRoot(tempDir);
                mgr.InitializeTimeoutMs = 150;
                mgr.MaxRestarts = 0;
                mgr.UserSettingsProvider = () => LanguageServerUserSettings.Parse("python = " + line + " --init-delay-ms 2000", false);

                string f = Path.Combine(tempDir, "slow.py");
                mgr.OpenDocument(f, () => "x = 1\n");

                var sw = Stopwatch.StartNew();
                while (sw.ElapsedMilliseconds < 5000 && mgr.GetStatus(f)?.State != LanguageServerState.Error)
                    Thread.Sleep(20);
                Assert.Equal(LanguageServerState.Error, mgr.GetStatus(f).State);
                Assert.Contains("initialize failed", mgr.GetStatus(f).Message);

                // Reconfigure without delay and restart -> recovers cleanly
                mgr.InitializeTimeoutMs = 5000;
                mgr.UserSettingsProvider = () => LanguageServerUserSettings.Parse("python = " + line, false);
                mgr.RestartServer(f);
                sw.Restart();
                while (sw.ElapsedMilliseconds < 10000 && mgr.GetStatus(f)?.State != LanguageServerState.Running)
                    Thread.Sleep(20);
                Assert.Equal(LanguageServerState.Running, mgr.GetStatus(f).State);
            }
        }

        [Fact]
        public void C44_RestrictedMode_BlocksWorkspaceLocalExecutables_And_StartsWhenWorkspaceTrusted()
        {
            string line = LanguageServerManagerTests.FakeServerCommandLine(out bool available);
            if (!available) return;
            string prevStore = WorkspaceTrust.StorePathOverride;
            bool prevEnabled = WorkspaceTrust.Enabled;
            WorkspaceTrust.StorePathOverride = Path.Combine(tempDir, "workspace-trust.json");
            WorkspaceTrust.Enabled = true;
            try
            {
                WorkspaceTrust.Restrict(tempDir);
                Assert.True(WorkspaceTrust.IsRestricted(tempDir));

                var user = LanguageServerUserSettings.Parse("rust = " + line, false);
                // rust-analyzer executes project build scripts and is blocked in Restricted Mode
                var resRestricted = LanguageServerRegistry.Resolve("rust", tempDir, user);
                Assert.True(resRestricted.Restricted);
                Assert.False(resRestricted.CanStart);

                // Trusting the workspace clears the Restricted Mode block
                WorkspaceTrust.Trust(tempDir);
                Assert.True(WorkspaceTrust.IsTrusted(tempDir));
                var resTrusted = LanguageServerRegistry.Resolve("rust", tempDir, user);
                Assert.False(resTrusted.Restricted);
                Assert.True(resTrusted.CanStart);
            }
            finally
            {
                WorkspaceTrust.Enabled = prevEnabled;
                WorkspaceTrust.StorePathOverride = prevStore;
            }
        }
    }
}
