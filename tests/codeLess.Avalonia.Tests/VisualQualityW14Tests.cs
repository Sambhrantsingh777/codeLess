using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using Avalonia.VisualTree;
using codeLess.AvaloniaApp;
using codeLess.Studio;
using Xunit;

namespace codeLess.Avalonia.Tests
{
    [Collection("SequentialWorkbench")]
    public class VisualQualityW14Tests
    {
        [Fact]
        public void SetiIconTheme_MapsExtensionsFileNamesLanguageIdsAndLightVariants_ExactToVsCodeJson()
        {
            // Required extensions & filenames from W14 brief + additional spot-checks
            var csDark = SetiIconTheme.Resolve("Program.cs", isLight: false);
            Assert.Equal("_c-sharp", csDark.Id);
            Assert.Equal("\\E00B", csDark.FontCharacter);
            Assert.Equal('\uE00B', csDark.Character);
            Assert.Equal("#519aba", csDark.ColorHex);
            Assert.Equal(System.Drawing.Color.FromArgb(0x51, 0x9a, 0xba), csDark.Color);

            var csExtDark = SetiIconTheme.Resolve(".cs", isLight: false);
            Assert.Equal("_c-sharp", csExtDark.Id);
            Assert.Equal("#519aba", csExtDark.ColorHex);

            var csLight = SetiIconTheme.Resolve("Program.cs", isLight: true);
            Assert.Equal("_c-sharp_light", csLight.Id);
            Assert.Equal("\\E00B", csLight.FontCharacter);
            Assert.Equal("#498ba7", csLight.ColorHex);

            var pyDark = SetiIconTheme.Resolve("script.py", isLight: false);
            Assert.Equal("_python", pyDark.Id);
            Assert.Equal("\\E07B", pyDark.FontCharacter);
            Assert.Equal("#519aba", pyDark.ColorHex);

            var pyLight = SetiIconTheme.Resolve(".py", isLight: true);
            Assert.Equal("_python_light", pyLight.Id);
            Assert.Equal("\\E07B", pyLight.FontCharacter);
            Assert.Equal("#498ba7", pyLight.ColorHex);

            var tsDark = SetiIconTheme.Resolve("app.ts", isLight: false);
            Assert.Equal("_typescript", tsDark.Id);
            Assert.Equal("\\E099", tsDark.FontCharacter);
            Assert.Equal("#519aba", tsDark.ColorHex);

            var tsSpecDark = SetiIconTheme.Resolve("app.spec.ts", isLight: false);
            Assert.Equal("_typescript_1", tsSpecDark.Id);
            Assert.Equal("\\E099", tsSpecDark.FontCharacter);
            Assert.Equal("#e37933", tsSpecDark.ColorHex);

            var jsonDark = SetiIconTheme.Resolve("config.json", isLight: false);
            Assert.Equal("_json", jsonDark.Id);
            Assert.Equal("\\E055", jsonDark.FontCharacter);
            Assert.Equal("#cbcb41", jsonDark.ColorHex);

            var pkgJsonDark = SetiIconTheme.Resolve("package.json", isLight: false);
            Assert.Equal("_json", pkgJsonDark.Id);
            Assert.Equal("\\E055", pkgJsonDark.FontCharacter);
            Assert.Equal("#cbcb41", pkgJsonDark.ColorHex);

            var pkgJsonLight = SetiIconTheme.Resolve("package.json", isLight: true);
            Assert.Equal("_json_light", pkgJsonLight.Id);
            Assert.Equal("\\E055", pkgJsonLight.FontCharacter);
            Assert.Equal("#b7b73b", pkgJsonLight.ColorHex);

            var tsconfigDark = SetiIconTheme.Resolve("tsconfig.json", isLight: false);
            Assert.Equal("_tsconfig", tsconfigDark.Id);
            Assert.Equal("\\E097", tsconfigDark.FontCharacter);
            Assert.Equal("#519aba", tsconfigDark.ColorHex);

            var mdDark = SetiIconTheme.Resolve("NOTES.md", isLight: false);
            Assert.Equal("_markdown", mdDark.Id);
            Assert.Equal("\\E060", mdDark.FontCharacter);
            Assert.Equal("#519aba", mdDark.ColorHex);

            var readmeDark = SetiIconTheme.Resolve("README.md", isLight: false);
            Assert.Equal("_info", readmeDark.Id);
            Assert.Equal("\\E04D", readmeDark.FontCharacter);
            Assert.Equal("#519aba", readmeDark.ColorHex);

            var dockerDark = SetiIconTheme.Resolve("Dockerfile", isLight: false);
            Assert.Equal("_docker", dockerDark.Id);
            Assert.Equal("\\E025", dockerDark.FontCharacter);
            Assert.Equal("#519aba", dockerDark.ColorHex);

            var gitignoreDark = SetiIconTheme.Resolve(".gitignore", isLight: false);
            Assert.Equal("_git", gitignoreDark.Id);
            Assert.Equal("\\E034", gitignoreDark.FontCharacter);
            Assert.Equal("#41535b", gitignoreDark.ColorHex);

            var gitignoreLight = SetiIconTheme.Resolve(".gitignore", isLight: true);
            Assert.Equal("_git_light", gitignoreLight.Id);
            Assert.Equal("\\E034", gitignoreLight.FontCharacter);
            Assert.Equal("#3b4b52", gitignoreLight.ColorHex);

            var unknownDark = SetiIconTheme.Resolve("mystery.unknown_ext", isLight: false);
            Assert.Equal("_default", unknownDark.Id);
            Assert.Equal("\\E023", unknownDark.FontCharacter);
            Assert.Equal("#d4d7d6", unknownDark.ColorHex);

            var unknownLight = SetiIconTheme.Resolve("mystery.unknown_ext", isLight: true);
            Assert.Equal("_default_light", unknownLight.Id);
            Assert.Equal("\\E023", unknownLight.FontCharacter);
            Assert.Equal("#bfc2c1", unknownLight.ColorHex);
        }

        [Fact]
        public void DarkModern_IsDefaultForNewSettings_AndSavatarrGraphiteRemainsSelectableWithRichRoles()
        {
            Assert.Equal("dark-modern", StudioTheme.DefaultThemeId);
            Assert.Equal("dark-modern", StudioTheme.GetBuiltInThemes()[0].Id);
            Assert.Equal("dark-modern", new IdePersistentSettings().ActiveThemeId);
            Assert.Equal("dark-modern", new StudioSettings().ThemeId);

            // Savatarr Graphite remains a built-in theme and is selectable by id or name
            var graphite = StudioTheme.GetById("savatarr-graphite");
            Assert.NotNull(graphite);
            Assert.Equal("savatarr-graphite", graphite.Id);
            Assert.Contains(StudioTheme.GetBuiltInThemes(), t => t.Id == "savatarr-graphite");

            // Richer token roles on Savatarr Graphite (variable, parameter, property, function, type, constant)
            var tmGraphite = TextMateTheme.For(graphite);
            Assert.NotEqual(graphite.FgPrimary, tmGraphite.Resolve("source.cs entity.name.variable.local.cs").Foreground);
            Assert.Equal(graphite.TokenVariable, tmGraphite.Resolve("source.cs entity.name.variable.local.cs").Foreground);
            Assert.Equal(graphite.TokenParameter, tmGraphite.Resolve("source.cs entity.name.variable.parameter.cs").Foreground);
            Assert.Equal(graphite.TokenProperty, tmGraphite.Resolve("source.cs variable.other.object.property.cs").Foreground);
            Assert.Equal(graphite.TokenFunction, tmGraphite.Resolve("source.cs entity.name.function.cs").Foreground);
            Assert.Equal(graphite.TokenType, tmGraphite.Resolve("source.cs entity.name.type.class.cs").Foreground);
            Assert.Equal(graphite.TokenConstant, tmGraphite.Resolve("source.cs variable.other.constant.cs").Foreground);
        }

        [Fact]
        public void VsCodeDefaultThemes_MatchExactTokenAndWorkbenchColors()
        {
            var dm = StudioTheme.DarkModern;
            Assert.Equal("dark-modern", dm.Id);
            Assert.Equal("Dark Modern", dm.Name);
            Assert.Equal(System.Drawing.Color.FromArgb(0x1F, 0x1F, 0x1F), dm.BgEditor);
            Assert.Equal(System.Drawing.Color.FromArgb(0xCC, 0xCC, 0xCC), dm.FgPrimary);
            Assert.Equal(System.Drawing.Color.FromArgb(0x18, 0x18, 0x18), dm.BgSidebar);
            Assert.Equal(System.Drawing.Color.FromArgb(0x00, 0x78, 0xD4), dm.AccentPrimary);

            var tm = TextMateTheme.For(dm);
            Assert.Equal(TextMateTheme.ParseColor("#569CD6"), tm.Resolve("storage.type").Foreground);
            Assert.Equal(TextMateTheme.ParseColor("#569CD6"), tm.Resolve("keyword").Foreground);
            Assert.Equal(TextMateTheme.ParseColor("#C586C0"), tm.Resolve("keyword.control").Foreground);
            Assert.Equal(TextMateTheme.ParseColor("#DCDCAA"), tm.Resolve("entity.name.function").Foreground);
            Assert.Equal(TextMateTheme.ParseColor("#CE9178"), tm.Resolve("string").Foreground);
            Assert.Equal(TextMateTheme.ParseColor("#6A9955"), tm.Resolve("comment").Foreground);
            Assert.Equal(TextMateTheme.ParseColor("#4EC9B0"), tm.Resolve("entity.name.type").Foreground);
            Assert.Equal(TextMateTheme.ParseColor("#9CDCFE"), tm.Resolve("variable").Foreground);
            Assert.Equal(TextMateTheme.ParseColor("#B5CEA8"), tm.Resolve("constant.numeric").Foreground);

            var dp = StudioTheme.DarkPlus;
            Assert.Equal("dark-plus", dp.Id);
            Assert.Equal("Dark+", dp.Name);
            Assert.Equal(System.Drawing.Color.FromArgb(0x1E, 0x1E, 0x1E), dp.BgEditor);

            var lm = StudioTheme.LightModern;
            Assert.Equal("light-modern", lm.Id);
            Assert.Equal("Light Modern", lm.Name);
            Assert.True(lm.IsLight);
            Assert.Equal(System.Drawing.Color.FromArgb(0xFF, 0xFF, 0xFF), lm.BgEditor);
            Assert.Equal(System.Drawing.Color.FromArgb(0x00, 0x5F, 0xB8), lm.AccentPrimary);
            var tmLight = TextMateTheme.For(lm);
            Assert.Equal(TextMateTheme.ParseColor("#0000FF"), tmLight.Resolve("keyword").Foreground);
            Assert.Equal(TextMateTheme.ParseColor("#AF00DB"), tmLight.Resolve("keyword.control").Foreground);
            Assert.Equal(TextMateTheme.ParseColor("#795E26"), tmLight.Resolve("entity.name.function").Foreground);
            Assert.Equal(TextMateTheme.ParseColor("#A31515"), tmLight.Resolve("string").Foreground);
            Assert.Equal(TextMateTheme.ParseColor("#008000"), tmLight.Resolve("comment").Foreground);
            Assert.Equal(TextMateTheme.ParseColor("#267F99"), tmLight.Resolve("entity.name.type").Foreground);
            Assert.Equal(TextMateTheme.ParseColor("#001080"), tmLight.Resolve("variable").Foreground);
            Assert.Equal(TextMateTheme.ParseColor("#098658"), tmLight.Resolve("constant.numeric").Foreground);

            var lp = StudioTheme.LightPlus;
            Assert.Equal("light-plus", lp.Id);
            Assert.Equal("Light+", lp.Name);
            Assert.True(lp.IsLight);
        }

        [Fact]
        public void CSharpNumericLiterals_AndBundledGrammarSamples_TokenizeWithoutInvalidScopes()
        {
            var reg = TextMateGrammarRegistry.Shared;
            var csGrammar = reg.GetCompiled("source.cs", true);
            Assert.NotNull(csGrammar);

            string csNumbersLine = "var a = 128; decimal b = 0m; decimal c = 0.92m; int d = 0x1F; int e = 1_000;";
            var csRes = csGrammar.TokenizeLine(csNumbersLine, null);
            string[] literals = { "128", "0m", "0.92m", "0x1F", "1_000" };
            var dmTheme = TextMateTheme.For(StudioTheme.DarkModern);
            var lmTheme = TextMateTheme.For(StudioTheme.LightModern);

            foreach (string lit in literals)
            {
                int start = csNumbersLine.IndexOf(lit, StringComparison.Ordinal);
                Assert.True(start >= 0, "Missing literal in sample: " + lit);
                int end = start + lit.Length;
                for (int pos = start; pos < end; pos++)
                {
                    int tokIdx = 0;
                    for (int i = 0; i < csRes.Tokens.Count; i++)
                    {
                        if (csRes.Tokens.Starts[i] <= pos && pos < csRes.Tokens.EndOf(i))
                        {
                            tokIdx = i;
                            break;
                        }
                    }
                    string scopeStr = csRes.Tokens.Scopes[tokIdx].ToString();
                    Assert.Contains("constant.numeric", scopeStr);
                    Assert.DoesNotContain("invalid", scopeStr);
                    Assert.Equal(TextMateTheme.ParseColor("#B5CEA8"), dmTheme.Resolve(csRes.Tokens.Scopes[tokIdx]).Foreground);
                    Assert.Equal(TextMateTheme.ParseColor("#098658"), lmTheme.Resolve(csRes.Tokens.Scopes[tokIdx]).Foreground);
                }
            }

            // Scan bundled grammars with captures-with-patterns and sample files to ensure zero invalid.* scopes
            var samplesByScope = new Dictionary<string, string>
            {
                { "source.cs", "using System;\nnamespace Demo {\n    public class Calc {\n        public int MaxBatchSize { get; set; } = 128;\n        public decimal Compute(decimal price) => price <= 0m ? 0m : price * 0.92m - 15.50m + 1_000 + 0x1F;\n    }\n}" },
                { "source.python", "import math\nclass Aggregator:\n    def __init__(self, n: int = 256, scale: float = 1.0):\n        self.n = n\n        self.scale = scale\n    def run(self, x: float) -> float:\n        return round(x * 0.95, 3)\n" },
                { "source.ts", "export interface Item { readonly id: string; readonly count: number; }\nexport class Store<T extends Item> {\n    private readonly max: number = 3;\n    public add(item: T): number { return item.count > 0 ? this.max : 0; }\n}" },
                { "source.go", "package main\nimport \"fmt\"\nfunc main() {\n\tx := 128\n\ty := 0.92\n\tz := 0x1F\n\tw := 1_000\n\tfmt.Println(x, y, z, w)\n}" },
                { "source.rust", "fn calc(x: i32) -> f64 {\n    let base: i32 = 128;\n    let factor: f64 = 0.92;\n    (x + base) as f64 * factor\n}" },
                { "source.css", ".panel {\n  color: #519aba;\n  padding: 12px 16px;\n  opacity: 0.95;\n}" },
                { "source.css.scss", "$primary: #519aba;\n.card[data-kind=\"main\"] {\n  color: $primary;\n  margin: 8px;\n}" },
                { "source.shell", "#!/usr/bin/env bash\ncount=128\nif [ $count -gt 0 ]; then\n  echo \"count is $count\"\nfi" },
                { "source.makefile", ".PHONY: build test\nbuild:\n\t@echo building 128\ntest: build\n\t@echo testing" },
                { "text.html.markdown", "# Heading 1\n###### Heading 6\nSome **bold** and `inline` with ~~strike~~ and numbers 128." },
                { "source.yaml", "version: 2\nservices:\n  app:\n    retries: 3\n    timeout: 1.5" },
                { "source.json", "{\n  \"name\": \"codeless\",\n  \"count\": 128,\n  \"ratio\": 0.92,\n  \"active\": true\n}" },
                { "source.sql", "SELECT id, total * 0.92 AS discounted FROM orders WHERE batch_size <= 128;" }
            };

            foreach (var kv in samplesByScope)
            {
                var g = reg.GetCompiled(kv.Key, true);
                Assert.NotNull(g);
                TmStateStack state = null;
                string[] lines = kv.Value.Split('\n');
                for (int lineIdx = 0; lineIdx < lines.Length; lineIdx++)
                {
                    var lineRes = g.TokenizeLine(lines[lineIdx], lineIdx == 0 ? null : state);
                    state = lineRes.EndState;
                    for (int t = 0; t < lineRes.Tokens.Count; t++)
                    {
                        string sc = lineRes.Tokens.Scopes[t].ToString();
                        Assert.False(sc.IndexOf("invalid", StringComparison.OrdinalIgnoreCase) >= 0,
                            $"Unexpected invalid scope '{sc}' in {kv.Key} line {lineIdx}: {lines[lineIdx]}");
                    }
                }
            }
        }

        [Fact]
        public void PythonFStrings_AndFormatStrings_MatchVsCodeScopesAndThemeColors()
        {
            var reg = TextMateGrammarRegistry.Shared;
            var py = reg.GetCompiled("source.python", true);
            Assert.NotNull(py);

            var dm = TextMateTheme.For(StudioTheme.DarkModern);
            var lm = TextMateTheme.For(StudioTheme.LightModern);

            var darkBlue = TextMateTheme.ParseColor("#569CD6");
            var lightBlue = TextMateTheme.ParseColor("#0000FF");
            var darkStr = TextMateTheme.ParseColor("#CE9178");
            var lightStr = TextMateTheme.ParseColor("#A31515");
            var darkFg = TextMateTheme.ParseColor("#CCCCCC");
            var lightFg = TextMateTheme.ParseColor("#3B3B3B");
            var darkEsc = TextMateTheme.ParseColor("#D7BA7D");
            var lightEsc = TextMateTheme.ParseColor("#EE0000");
            var darkOp = TextMateTheme.ParseColor("#D4D4D4");
            var lightOp = TextMateTheme.ParseColor("#000000");
            var darkNum = TextMateTheme.ParseColor("#B5CEA8");
            var lightNum = TextMateTheme.ParseColor("#098658");
            var darkFn = TextMateTheme.ParseColor("#DCDCAA");
            var lightFn = TextMateTheme.ParseColor("#795E26");

            void AssertSpan(TmLineTokens tokens, string line, int start, int length, string requiredScope, string forbiddenScope, System.Drawing.Color expectedDark, System.Drawing.Color expectedLight)
            {
                for (int pos = start; pos < start + length; pos++)
                {
                    int tokIdx = -1;
                    for (int i = 0; i < tokens.Count; i++)
                    {
                        if (tokens.Starts[i] <= pos && pos < tokens.EndOf(i))
                        {
                            tokIdx = i;
                            break;
                        }
                    }
                    Assert.True(tokIdx >= 0, $"No token covering pos {pos} in '{line}'");
                    var sc = tokens.Scopes[tokIdx];
                    string scStr = sc.ToString();
                    Assert.DoesNotContain("invalid", scStr);
                    if (!string.IsNullOrEmpty(requiredScope))
                        Assert.True(scStr.Contains(requiredScope), $"Expected '{requiredScope}' at [{start}..{start + length}) ('{line.Substring(start, length)}') in '{line}', but got '{scStr}'");
                    if (!string.IsNullOrEmpty(forbiddenScope))
                        Assert.False(scStr.Contains(forbiddenScope), $"Forbidden '{forbiddenScope}' at [{start}..{start + length}) ('{line.Substring(start, length)}') in '{line}', got '{scStr}'");
                    Assert.Equal(expectedDark, dm.Resolve(sc).Foreground);
                    Assert.Equal(expectedLight, lm.Resolve(sc).Foreground);
                }
            }

            // 1) f"Hello {name}!"
            string s1 = "f\"Hello {name}!\"";
            var r1 = py.TokenizeLine(s1, null).Tokens;
            AssertSpan(r1, s1, 0, 1, "storage.type.string.python", null, darkBlue, lightBlue);
            AssertSpan(r1, s1, 1, 1, "punctuation.definition.string.begin.python", null, darkStr, lightStr);
            AssertSpan(r1, s1, 2, 6, "string.interpolated.python string.quoted.single.python", null, darkStr, lightStr);
            AssertSpan(r1, s1, 8, 1, "constant.character.format.placeholder.other.python", null, darkBlue, lightBlue);
            AssertSpan(r1, s1, 9, 4, "meta.fstring.python", " string.", darkFg, lightFg);
            AssertSpan(r1, s1, 13, 1, "constant.character.format.placeholder.other.python", null, darkBlue, lightBlue);
            AssertSpan(r1, s1, 14, 1, "string.interpolated.python string.quoted.single.python", null, darkStr, lightStr);
            AssertSpan(r1, s1, 15, 1, "punctuation.definition.string.end.python", null, darkStr, lightStr);

            // 2) f'{value:>10.2f}'
            string s2 = "f'{value:>10.2f}'";
            var r2 = py.TokenizeLine(s2, null).Tokens;
            AssertSpan(r2, s2, 0, 1, "storage.type.string.python", null, darkBlue, lightBlue);
            AssertSpan(r2, s2, 1, 1, "punctuation.definition.string.begin.python", null, darkStr, lightStr);
            AssertSpan(r2, s2, 2, 1, "constant.character.format.placeholder.other.python", null, darkBlue, lightBlue);
            AssertSpan(r2, s2, 3, 5, "meta.fstring.python", " string.", darkFg, lightFg);
            AssertSpan(r2, s2, 8, 7, "storage.type.format.python", " string.", darkBlue, lightBlue);
            AssertSpan(r2, s2, 15, 1, "constant.character.format.placeholder.other.python", null, darkBlue, lightBlue);
            AssertSpan(r2, s2, 16, 1, "punctuation.definition.string.end.python", null, darkStr, lightStr);

            // 3) f"{obj.attr!r}"
            string s3 = "f\"{obj.attr!r}\"";
            var r3 = py.TokenizeLine(s3, null).Tokens;
            AssertSpan(r3, s3, 0, 1, "storage.type.string.python", null, darkBlue, lightBlue);
            AssertSpan(r3, s3, 1, 1, "punctuation.definition.string.begin.python", null, darkStr, lightStr);
            AssertSpan(r3, s3, 2, 1, "constant.character.format.placeholder.other.python", null, darkBlue, lightBlue);
            AssertSpan(r3, s3, 3, 3, "meta.fstring.python", " string.", darkFg, lightFg);
            AssertSpan(r3, s3, 6, 1, "punctuation.separator.period.python", " string.", darkFg, lightFg);
            AssertSpan(r3, s3, 7, 4, "meta.member.access.python", " string.", darkFg, lightFg);
            AssertSpan(r3, s3, 11, 2, "storage.type.format.python", " string.", darkBlue, lightBlue);
            AssertSpan(r3, s3, 13, 1, "constant.character.format.placeholder.other.python", null, darkBlue, lightBlue);
            AssertSpan(r3, s3, 14, 1, "punctuation.definition.string.end.python", null, darkStr, lightStr);

            // 4) f"{{literal}}"
            string s4 = "f\"{{literal}}\"";
            var r4 = py.TokenizeLine(s4, null).Tokens;
            AssertSpan(r4, s4, 0, 1, "storage.type.string.python", null, darkBlue, lightBlue);
            AssertSpan(r4, s4, 1, 1, "punctuation.definition.string.begin.python", null, darkStr, lightStr);
            AssertSpan(r4, s4, 2, 2, "constant.character.escape.python", null, darkEsc, lightEsc);
            AssertSpan(r4, s4, 4, 7, "string.interpolated.python string.quoted.single.python", null, darkStr, lightStr);
            AssertSpan(r4, s4, 11, 2, "constant.character.escape.python", null, darkEsc, lightEsc);
            AssertSpan(r4, s4, 13, 1, "punctuation.definition.string.end.python", null, darkStr, lightStr);

            // 5) rf"\d{n}"
            string s5 = "rf\"\\d{n}\"";
            var r5 = py.TokenizeLine(s5, null).Tokens;
            AssertSpan(r5, s5, 0, 2, "storage.type.string.python", null, darkBlue, lightBlue);
            AssertSpan(r5, s5, 2, 1, "punctuation.definition.string.begin.python", null, darkStr, lightStr);
            AssertSpan(r5, s5, 3, 2, "string.interpolated.python string.quoted.raw.single.python", null, darkStr, lightStr);
            AssertSpan(r5, s5, 5, 1, "constant.character.format.placeholder.other.python", null, darkBlue, lightBlue);
            AssertSpan(r5, s5, 6, 1, "meta.fstring.python", " string.", darkFg, lightFg);
            AssertSpan(r5, s5, 7, 1, "constant.character.format.placeholder.other.python", null, darkBlue, lightBlue);
            AssertSpan(r5, s5, 8, 1, "punctuation.definition.string.end.python", null, darkStr, lightStr);

            // 6) F"""multi\n{x}\nline"""
            string s6a = "F\"\"\"multi";
            string s6b = "{x}";
            string s6c = "line\"\"\"";
            var res6a = py.TokenizeLine(s6a, null);
            var res6b = py.TokenizeLine(s6b, res6a.EndState);
            var res6c = py.TokenizeLine(s6c, res6b.EndState);
            AssertSpan(res6a.Tokens, s6a, 0, 1, "storage.type.string.python", null, darkBlue, lightBlue);
            AssertSpan(res6a.Tokens, s6a, 1, 3, "punctuation.definition.string.begin.python", null, darkStr, lightStr);
            AssertSpan(res6a.Tokens, s6a, 4, 5, "string.interpolated.python string.quoted.multi.python", null, darkStr, lightStr);
            AssertSpan(res6b.Tokens, s6b, 0, 1, "constant.character.format.placeholder.other.python", null, darkBlue, lightBlue);
            AssertSpan(res6b.Tokens, s6b, 1, 1, "meta.fstring.python", " string.", darkFg, lightFg);
            AssertSpan(res6b.Tokens, s6b, 2, 1, "constant.character.format.placeholder.other.python", null, darkBlue, lightBlue);
            AssertSpan(res6c.Tokens, s6c, 0, 4, "string.interpolated.python string.quoted.multi.python", null, darkStr, lightStr);
            AssertSpan(res6c.Tokens, s6c, 4, 3, "punctuation.definition.string.end.python", null, darkStr, lightStr);

            // 7) nested quotes f"{d['k']}"
            string s7 = "f\"{d['k']}\"";
            var r7 = py.TokenizeLine(s7, null).Tokens;
            AssertSpan(r7, s7, 0, 1, "storage.type.string.python", null, darkBlue, lightBlue);
            AssertSpan(r7, s7, 1, 1, "punctuation.definition.string.begin.python", null, darkStr, lightStr);
            AssertSpan(r7, s7, 2, 1, "constant.character.format.placeholder.other.python", null, darkBlue, lightBlue);
            AssertSpan(r7, s7, 3, 1, "meta.item-access.python", " string.", darkFg, lightFg);
            AssertSpan(r7, s7, 4, 1, "punctuation.definition.arguments.begin.python", " string.", darkFg, lightFg);
            AssertSpan(r7, s7, 5, 3, "string.quoted.single.python", null, darkStr, lightStr);
            AssertSpan(r7, s7, 8, 1, "punctuation.definition.arguments.end.python", " string.", darkFg, lightFg);
            AssertSpan(r7, s7, 9, 1, "constant.character.format.placeholder.other.python", null, darkBlue, lightBlue);
            AssertSpan(r7, s7, 10, 1, "punctuation.definition.string.end.python", null, darkStr, lightStr);

            // 8) f"{a + b * 2}"
            string s8 = "f\"{a + b * 2}\"";
            var r8 = py.TokenizeLine(s8, null).Tokens;
            AssertSpan(r8, s8, 0, 1, "storage.type.string.python", null, darkBlue, lightBlue);
            AssertSpan(r8, s8, 1, 1, "punctuation.definition.string.begin.python", null, darkStr, lightStr);
            AssertSpan(r8, s8, 2, 1, "constant.character.format.placeholder.other.python", null, darkBlue, lightBlue);
            AssertSpan(r8, s8, 3, 1, "meta.fstring.python", " string.", darkFg, lightFg);
            AssertSpan(r8, s8, 5, 1, "keyword.operator.arithmetic.python", " string.", darkOp, lightOp);
            AssertSpan(r8, s8, 7, 1, "meta.fstring.python", " string.", darkFg, lightFg);
            AssertSpan(r8, s8, 9, 1, "keyword.operator.arithmetic.python", " string.", darkOp, lightOp);
            AssertSpan(r8, s8, 11, 1, "constant.numeric.dec.python", " string.", darkNum, lightNum);
            AssertSpan(r8, s8, 12, 1, "constant.character.format.placeholder.other.python", null, darkBlue, lightBlue);
            AssertSpan(r8, s8, 13, 1, "punctuation.definition.string.end.python", null, darkStr, lightStr);

            // 9) f"{len(xs)}"
            string s9 = "f\"{len(xs)}\"";
            var r9 = py.TokenizeLine(s9, null).Tokens;
            AssertSpan(r9, s9, 0, 1, "storage.type.string.python", null, darkBlue, lightBlue);
            AssertSpan(r9, s9, 1, 1, "punctuation.definition.string.begin.python", null, darkStr, lightStr);
            AssertSpan(r9, s9, 2, 1, "constant.character.format.placeholder.other.python", null, darkBlue, lightBlue);
            AssertSpan(r9, s9, 3, 3, "support.function.builtin.python", " string.", darkFn, lightFn);
            AssertSpan(r9, s9, 6, 1, "punctuation.definition.arguments.begin.python", " string.", darkFg, lightFg);
            AssertSpan(r9, s9, 7, 2, "meta.function-call.arguments.python", " string.", darkFg, lightFg);
            AssertSpan(r9, s9, 9, 1, "punctuation.definition.arguments.end.python", " string.", darkFg, lightFg);
            AssertSpan(r9, s9, 10, 1, "constant.character.format.placeholder.other.python", null, darkBlue, lightBlue);
            AssertSpan(r9, s9, 11, 1, "punctuation.definition.string.end.python", null, darkStr, lightStr);

            // 10) "{0} {name}".format(...)
            string s10 = "x = \"{0} {name}\".format(a, name=b)";
            var r10 = py.TokenizeLine(s10, null).Tokens;
            AssertSpan(r10, s10, 4, 1, "punctuation.definition.string.begin.python", null, darkStr, lightStr);
            AssertSpan(r10, s10, 5, 3, "constant.character.format.placeholder.other.python", null, darkBlue, lightBlue);
            AssertSpan(r10, s10, 8, 1, "string.quoted.single.python", "placeholder", darkStr, lightStr);
            AssertSpan(r10, s10, 9, 6, "constant.character.format.placeholder.other.python", null, darkBlue, lightBlue);
            AssertSpan(r10, s10, 15, 1, "punctuation.definition.string.end.python", null, darkStr, lightStr);

            // 11) "%s %d"
            string s11 = "y = \"%s %d\"";
            var r11 = py.TokenizeLine(s11, null).Tokens;
            AssertSpan(r11, s11, 4, 1, "punctuation.definition.string.begin.python", null, darkStr, lightStr);
            AssertSpan(r11, s11, 5, 2, "constant.character.format.placeholder.other.python", null, darkBlue, lightBlue);
            AssertSpan(r11, s11, 7, 1, "string.quoted.single.python", "placeholder", darkStr, lightStr);
            AssertSpan(r11, s11, 8, 2, "constant.character.format.placeholder.other.python", null, darkBlue, lightBlue);
            AssertSpan(r11, s11, 10, 1, "punctuation.definition.string.end.python", null, darkStr, lightStr);
        }

        [AvaloniaFact]
        public void ExplorerPaintsSetiGlyphForCsFile_AndRendersVisualQualityScreenshots()
        {
            string tempWs = Path.Combine(Path.GetTempPath(), "codeless-w14-ws-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(tempWs);
            Directory.CreateDirectory(Path.Combine(tempWs, "src"));
            Directory.CreateDirectory(Path.Combine(tempWs, "tests"));

            string csSample = @"using System;
using System.Collections.Generic;
using System.Linq;

namespace CodeLess.Samples
{
    public sealed class OrderProcessor
    {
        private readonly Dictionary<string, decimal> _totals = new Dictionary<string, decimal>();
        public int MaxBatchSize { get; set; } = 128;

        // Computes discounted total for active line items
        public decimal ProcessOrders(string customerId, IEnumerable<decimal> prices)
        {
            if (string.IsNullOrEmpty(customerId))
                throw new ArgumentException(""Customer id is required"", nameof(customerId));

            decimal subtotal = 0m;
            int count = 0;
            foreach (decimal price in prices)
            {
                if (price <= 0m) continue;
                subtotal += price * 0.92m;
                count++;
            }
            _totals[customerId] = subtotal;
            return count > 10 ? subtotal - 15.50m : subtotal;
        }
    }
}
";
            string pySample = @"import math
from typing import List, Dict, Optional

class MetricsAggregator:
    """"""Computes rolling latency percentiles for worker telemetry.""""""

    def __init__(self, window_size: int = 256, label: str = ""default"") -> None:
        self.window_size = window_size
        self.label = label
        self.samples: List[float] = []

    def format_summary(self, stats: Dict[str, float], unit: str = ""ms"") -> str:
        header = f""Hello {self.label!r} ({{literal}}): {len(self.samples)} items""
        detail = f'{stats[""p95_ms""]:>10.2f} {unit} (total={len(self.samples) + 2 * 10})'
        pattern = rf""\d+{self.window_size + 2 * 10}""
        return f""{header} | {detail} | {pattern}""

    # Record a batch of elapsed durations in milliseconds
    def record_batch(self, values: List[float], scale: float = 1.0) -> Dict[str, float]:
        for val in values:
            if val >= 0.0:
                self.samples.append(val * scale)
        if len(self.samples) > self.window_size:
            self.samples = self.samples[-self.window_size:]
        ordered = sorted(self.samples)
        n = len(ordered)
        p95 = ordered[min(n - 1, int(n * 0.95))] if n > 0 else 0.0
        return {""count"": float(n), ""p95_ms"": round(p95, 3)}
";
            string tsSample = @"import { readFile } from 'fs/promises';

export interface TelemetryEvent {
    readonly id: string;
    readonly timestampMs: number;
    readonly payload: Record<string, unknown>;
}

// Dispatches buffered events with retry backoff
export class EventDispatcher<T extends TelemetryEvent> {
    private readonly queue: T[] = [];
    private readonly maxRetries: number = 3;

    constructor(private readonly endpointUrl: string) {}

    public enqueue(event: T, urgent: boolean = false): number {
        if (!event.id || event.timestampMs <= 0) {
            throw new Error(`Invalid telemetry event: ${event.id}`);
        }
        this.queue.push(event);
        return urgent ? 0 : this.queue.length;
    }
}
";

            string csPath = Path.Combine(tempWs, "OrderProcessor.cs");
            string pyPath = Path.Combine(tempWs, "metrics.py");
            string tsPath = Path.Combine(tempWs, "dispatcher.ts");

            File.WriteAllText(csPath, csSample);
            File.WriteAllText(pyPath, pySample);
            File.WriteAllText(tsPath, tsSample);

            // Assert the exact screenshot sample files tokenize with zero invalid.* scopes
            foreach (var pair in new[] { ("source.cs", csSample), ("source.python", pySample), ("source.ts", tsSample) })
            {
                var g = TextMateGrammarRegistry.Shared.GetCompiled(pair.Item1, true);
                Assert.NotNull(g);
                TmStateStack st = null;
                string[] sampleLines = pair.Item2.Replace("\r\n", "\n").Split('\n');
                for (int li = 0; li < sampleLines.Length; li++)
                {
                    var lr = g.TokenizeLine(sampleLines[li], li == 0 ? null : st);
                    st = lr.EndState;
                    for (int ti = 0; ti < lr.Tokens.Count; ti++)
                    {
                        string sc = lr.Tokens.Scopes[ti].ToString();
                        Assert.DoesNotContain("invalid", sc);
                    }
                }
            }

            // ~20 mixed file types in workspace for Explorer icon verification
            string[] extraFiles =
            {
                "Component.tsx", "index.js", "app.spec.ts", "package.json", "tsconfig.json",
                "README.md", "CHANGELOG.md", "Dockerfile", "docker-compose.yml", ".gitignore",
                "Makefile", "CMakeLists.txt", "Cargo.toml", "lib.rs", "server.go",
                "styles.css", "theme.scss", "index.html", "schema.sql", "deploy.sh", "notes.txt"
            };
            foreach (string ef in extraFiles)
            {
                File.WriteAllText(Path.Combine(tempWs, ef), "// sample " + ef + "\n");
            }

            MainWindow window = null;
            try
            {
                AvaloniaExplorerTree.ResetSetiFontCacheForBackendSwitch();
                Assert.False(AvaloniaExplorerTree.IsSetiFontLoaded);

                window = new MainWindow(tempWs) { SuppressUnsavedPrompts = true, Width = 1440, Height = 900 };
                // Before Explorer or tabs paint/build icons, the Seti font remains unloaded
                Assert.False(AvaloniaExplorerTree.IsSetiFontLoaded);
                window.Show();
                Dispatcher.UIThread.RunJobs();

                var tree = window.ExplorerTree;
                tree.RebuildTreeLayout(false);
                window.OpenFileInTab(csPath, preview: false);
                window.OpenFileInTab(pyPath, preview: false);
                window.OpenFileInTab(tsPath, preview: false);
                window.OpenFileInTab(csPath, preview: false);
                window.PrimaryEditor.Canvas.CompleteBackgroundTokenization();

                // Verify Seti glyph is painted for .cs file in Explorer
                window.Measure(new Size(1440, 900));
                window.Arrange(new Rect(0, 0, 1440, 900));
                window.UpdateLayout();
                using (var nullRtb = new RenderTargetBitmap(new PixelSize(1440, 900), new Vector(96, 96)))
                {
                    nullRtb.Render(window);
                }

                Assert.True(AvaloniaExplorerTree.IsSetiFontLoaded);
                Assert.True(FontManager.Current.TryGetGlyphTypeface(AvaloniaExplorerTree.GetSetiTypeface(), out var glyphTypeface));
                Assert.NotNull(glyphTypeface);

                var csPainted = tree.LastFrameFileIcons.FirstOrDefault(x => string.Equals(x.FileName, "OrderProcessor.cs", StringComparison.OrdinalIgnoreCase));
                Assert.Equal("_c-sharp", csPainted.IconId);
                Assert.Equal("\uE00B", csPainted.Glyph);
                Assert.Equal(Color.FromRgb(0x51, 0x9a, 0xba), csPainted.Color);

                // W14b (1): OPEN EDITORS list has Seti file icons for each open tab
                Assert.NotNull(window.OpenEditorsList);
                Assert.Equal(3, window.OpenEditorsList.Children.Count);
                var openEditorGlyphs = window.OpenEditorsList.GetVisualDescendants().OfType<TextBlock>()
                    .Where(tb => tb.Width == 16 && tb.Height == 16)
                    .Select(tb => tb.Text)
                    .ToList();
                Assert.Contains("\uE00B", openEditorGlyphs); // OrderProcessor.cs
                Assert.Contains("\uE07B", openEditorGlyphs); // metrics.py
                Assert.Contains("\uE099", openEditorGlyphs); // dispatcher.ts

                // W14b (2): Restricted Mode banner & buttons use real VS Code Dark Modern and Light Modern colours
                var banner = window.RestrictedModeBannerControl;
                Assert.NotNull(banner);
                var manageBtn = banner.GetVisualDescendants().OfType<Button>().FirstOrDefault(b => b.Name == "RestrictedBannerManageBtn");
                var learnBtn = banner.GetVisualDescendants().OfType<Button>().FirstOrDefault(b => b.Name == "RestrictedBannerLearnMoreBtn");
                Assert.NotNull(manageBtn);
                Assert.NotNull(learnBtn);

                Assert.Equal(Color.FromRgb(0x35, 0x2A, 0x05), ((ISolidColorBrush)banner.Background).Color);
                Assert.Equal(Color.FromRgb(0x00, 0x78, 0xD4), ((ISolidColorBrush)manageBtn.Background).Color);
                Assert.Equal(Colors.White, ((ISolidColorBrush)manageBtn.Foreground).Color);
                Assert.Equal(Color.FromRgb(0x31, 0x31, 0x31), ((ISolidColorBrush)learnBtn.Background).Color);
                Assert.Equal(Color.FromRgb(0xCC, 0xCC, 0xCC), ((ISolidColorBrush)learnBtn.Foreground).Color);

                window.ApplyTheme(StudioTheme.LightModern, persistSetting: false);
                Assert.Equal(Color.FromRgb(0xFF, 0xF4, 0xCE), ((ISolidColorBrush)banner.Background).Color);
                Assert.Equal(Color.FromRgb(0x00, 0x5F, 0xB8), ((ISolidColorBrush)manageBtn.Background).Color);
                Assert.Equal(Colors.White, ((ISolidColorBrush)manageBtn.Foreground).Color);
                Assert.Equal(Color.FromRgb(0xE5, 0xE5, 0xE5), ((ISolidColorBrush)learnBtn.Background).Color);
                Assert.Equal(Color.FromRgb(0x3B, 0x3B, 0x3B), ((ISolidColorBrush)learnBtn.Foreground).Color);
                window.ApplyTheme(StudioTheme.DarkModern, persistSetting: false);

                // W14b (3): Problems panel rows carry SetiFileName for file icon rendering
                var activeBuf = window.PrimaryEditor.CurrentBuffer;
                activeBuf.Diagnostics.Add(new DiagnosticItem { Severity = "Warning", Line = 10, Column = 5, Message = "Sample diagnostic", FilePath = csPath });
                window.RefreshOutlineAndProblems(activeBuf);
                var probRows = ((System.Collections.IEnumerable)window.ProblemsListItemsForTest()).OfType<LiteralTextBlock>().ToList();
                Assert.Single(probRows);
                Assert.Equal("OrderProcessor.cs", probRows[0].SetiFileName);
                var rawProbControls = window.ProblemsListBoxControl.ItemsSource.Cast<Control>().ToList();
                Assert.Single(rawProbControls);
                var probPanel = Assert.IsType<StackPanel>(rawProbControls[0]);
                Assert.Equal("\uE00B", Assert.IsType<TextBlock>(probPanel.Children[0]).Text);
                activeBuf.Diagnostics.Clear();
                window.RefreshOutlineAndProblems(activeBuf);

                if (Environment.GetEnvironmentVariable("CODELESS_HEADLESS_SKIA") == "1")
                {
                    Assert.NotEqual((ushort)0, glyphTypeface.GetGlyph('\uE00B'));
                    string shotsDir = "/tmp/codeless-work/visual_shots";
                    Directory.CreateDirectory(shotsDir);

                    void RenderShot(StudioTheme theme, string activeFile, string fileName)
                    {
                        window.ApplyTheme(theme, persistSetting: false);
                        window.OpenFileInTab(activeFile, preview: false);
                        window.PrimaryEditor.Canvas.CompleteBackgroundTokenization();
                        Dispatcher.UIThread.RunJobs();
                        window.InvalidateMeasure();
                        window.InvalidateArrange();
                        window.InvalidateVisual();
                        foreach (var v in window.GetVisualDescendants()) v.InvalidateVisual();
                        window.Measure(new Size(1440, 900));
                        window.Arrange(new Rect(0, 0, 1440, 900));
                        window.UpdateLayout();
                        Dispatcher.UIThread.RunJobs();
                        using (var rtb = new RenderTargetBitmap(new PixelSize(1440, 900), new Vector(96, 96)))
                        {
                            rtb.Render(window);
                            rtb.Save(Path.Combine(shotsDir, fileName));
                        }
                    }

                    RenderShot(StudioTheme.DarkModern, csPath, "dark_modern_csharp.png");
                    RenderShot(StudioTheme.DarkModern, pyPath, "dark_modern_python.png");
                    RenderShot(StudioTheme.DarkModern, tsPath, "dark_modern_typescript.png");
                    RenderShot(StudioTheme.LightModern, csPath, "light_modern_csharp.png");
                    RenderShot(StudioTheme.LightModern, pyPath, "light_modern_python.png");
                    RenderShot(StudioTheme.LightModern, tsPath, "light_modern_typescript.png");
                }
            }
            finally
            {
                try { window?.Close(); } catch { }
                try { Directory.Delete(tempWs, true); } catch { }
            }
        }
    }
}
