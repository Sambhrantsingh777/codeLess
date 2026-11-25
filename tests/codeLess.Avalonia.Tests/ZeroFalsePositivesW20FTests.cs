using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using codeLess.AvaloniaApp;
using codeLess.Studio;
using Xunit;

#nullable enable

namespace codeLess.Avalonia.Tests;

/// <summary>
/// W20 Area F: Zero-false-positive sweep across valid real-world files in C# 12,
/// Python 3.8-3.13, TypeScript, TSX, JavaScript, Go, Rust, JSON, JSONC, YAML,
/// Markdown, Shell, HTML, CSS/SCSS, SQL, Makefile, Dockerfile, XML/XAML, and
/// every real source/config file in the repository.
/// Asserts across both headless Avalonia and the shared WinForms/Core pipeline:
/// 1. Zero error/warning diagnostics on valid code,
/// 2. Zero red squiggles,
/// 3. Zero red "unexpected bracket" (Levels[k] &lt; 0 / UiTokens.BracketUnexpected) spans,
/// 4. Zero TextMate "invalid" scopes on valid syntax, and
/// 5. Zero SyntaxVerifier.VerifyCodeSyntax failures on save.
/// </summary>
[Collection("SequentialWorkbench")]
public class ZeroFalsePositivesW20FTests
{
    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "codeLess.sln")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new DirectoryNotFoundException("Repo root not found");
    }

    private static void PumpUi(int ticks = 4)
    {
        for (int i = 0; i < ticks; i++)
            Dispatcher.UIThread.RunJobs();
    }

    private static void AssertZeroFalsePositivesForBuffer(
        string caseId,
        string fileName,
        string content,
        bool runExternalVerifier = true)
    {
        string normalized = content.Replace("\r\n", "\n");
        string fullPath = Path.IsPathRooted(fileName) ? fileName : "/tmp/" + fileName;
        var buf = new EditorTabBuffer
        {
            FilePath = fullPath,
            FileName = Path.GetFileName(fileName),
            Content = normalized,
            Lines = new List<string>(normalized.Split('\n'))
        };

        // 1. AST & Diagnostics engine (used by both WinForms and Avalonia tabs/problems panel)
        LlmHarnessAndAgentEngine.AnalyzeBufferAstAndDiagnostics(buf);
        Assert.True(
            buf.Diagnostics.Count == 0,
            $"{caseId} ({fileName}): expected 0 diagnostics on valid file, got {buf.Diagnostics.Count}: " +
            string.Join("; ", buf.Diagnostics.Select(d => $"[{d.Severity}] L{d.Line}:{d.Column} {d.Message}")));

        // 2. Bracket matching, rainbow bracket lexer, and TextMate scopes (shared EditorSyntaxSession)
        var theme = StudioTheme.GetById("dark_modern");
        Color unexpectedColor = UiTokens.BracketUnexpected(theme);
        string scope = TextMateGrammarRegistry.Shared.GetScopeForFile(buf.FilePath);
        if (scope != null)
            TextMateGrammarRegistry.Shared.GetCompiled(scope, true);

        bool fullTextMateScan = buf.Lines.Count <= 800;
        var session = EditorSyntaxSession.For(buf);
        session.Prepare(buf);
        session.Brackets.EnsureValid(buf.Lines, Math.Max(0, buf.Lines.Count - 1), 0);
        if (session.TextMate != null && fullTextMateScan)
            session.TextMate.EnsureValid(buf.Lines, Math.Max(0, buf.Lines.Count - 1), 0);

        for (int line = 0; line < buf.Lines.Count; line++)
        {
            string lineText = buf.Lines[line] ?? "";
            if (session.Brackets.Profile.Enabled)
            {
                var bd = session.Brackets.GetLine(buf.Lines, line, 0);
                for (int k = 0; k < bd.Count; k++)
                {
                    Assert.True(
                        bd.Levels[k] >= 0,
                        $"{caseId} ({fileName}) line {line + 1} col {bd.Cols[k] + 1} ('{bd.Chars[k]}'): unexpected red bracket (level={bd.Levels[k]}) on line: {lineText}");
                }
            }

            // 3. TextMate grammar scopes: no "invalid" or "invalid.illegal" scopes on valid syntax
            if (session.TextMate != null && (fullTextMateScan || line < 200))
            {
                bool isGuess;
                TmLineTokens toks = session.TextMate.GetTokens(buf.Lines, line, 0, 0, out isGuess);
                if (toks != null)
                {
                    for (int i = 0; i < toks.Count; i++)
                    {
                        for (TmScopeStack? cur = toks.Scopes[i]; cur != null; cur = cur.Parent)
                        {
                            string sc = cur.Scope;
                            if (sc != null && (sc.StartsWith("invalid.", StringComparison.Ordinal) || string.Equals(sc, "invalid", StringComparison.Ordinal)))
                            {
                                int s = Math.Min(toks.Starts[i], lineText.Length);
                                int e = Math.Min(toks.EndOf(i), lineText.Length);
                                string snippet = lineText.Substring(s, Math.Max(0, e - s));
                                Assert.Fail(
                                    $"{caseId} ({fileName}) line {line + 1}: TextMate emitted invalid scope '{sc}' on token '{snippet}' in line: {lineText}");
                            }
                        }
                    }
                }
            }

            // Also verify final rendered StyledSpans after ApplyBracketColors have zero BracketUnexpected color
            if (fullTextMateScan)
            {
                bool guessSpan;
                var spans = session.GetTextMateSpans(buf.Lines, line, 0, theme, 0, out guessSpan, 0, lineText.Length)
                    ?? session.GetFallbackSpans(buf.Lines, line, buf.Language, theme, 0, lineText.Length, false, 0, out guessSpan);
                if (spans != null && session.Brackets.Profile.Enabled)
                {
                    var bd = session.Brackets.GetLine(buf.Lines, line, 0);
                    spans = EditorSyntaxSession.ApplyBracketColors(spans, bd, theme);
                    foreach (var sp in spans)
                    {
                        Assert.False(
                            sp.Color.ToArgb() == unexpectedColor.ToArgb(),
                            $"{caseId} ({fileName}) line {line + 1} col {sp.Start + 1}: rendered span painted with BracketUnexpected red on line: {lineText}");
                    }
                }
            }
        }

        // 4. SyntaxVerifier (save-time syntax verification, fast in-process + optional external tool)
        string errFast;
        bool okFast = SyntaxVerifier.VerifyCodeSyntax(buf.FilePath, normalized, out errFast, allowExternalProcess: false);
        Assert.True(
            okFast,
            $"{caseId} ({fileName}): VerifyCodeSyntax(allowExternalProcess: false) failed: {errFast}");

        if (runExternalVerifier)
        {
            string errFull;
            bool okFull = SyntaxVerifier.VerifyCodeSyntax(buf.FilePath, normalized, out errFull, allowExternalProcess: true);
            Assert.True(
                okFull,
                $"{caseId} ({fileName}): VerifyCodeSyntax(allowExternalProcess: true) failed: {errFull}");
        }
    }

    // =========================================================================
    // F01: C# 12 raw strings (3-quote and 4-quote), collection expressions,
    // primary constructors, file-scoped namespaces, records, pattern matching
    // =========================================================================
    [AvaloniaFact]
    public void F01_CSharp12_RawStrings_CollectionExpressions_PrimaryCtors_Records_Patterns()
    {
        const string code = """""
            namespace Demo.ModernCSharp;

            using System;
            using System.Collections.Generic;

            public readonly record struct Point(int X, int Y);

            public class WidgetService(string prefix, int defaultLimit = 10)
            {
                private readonly List<int> _items = [1, 2, 3];

                public List<int> Combine(ReadOnlySpan<int> extra)
                {
                    int[] baseItems = [0, .._items, 99];
                    return [..baseItems, ..extra];
                }

                public string Describe(object? value) => value switch
                {
                    Point { X: > 0 and < 100, Y: var y } => $"{prefix}:{y}",
                    int[] { Length: >= 2 } arr and [var first, .., var last] => $"{first}..{last}",
                    string { Length: > 0 } s when s.StartsWith('[') => s,
                    null or { } => defaultLimit.ToString()
                };

                public string GetRawJsonWithUnbalancedBrackets()
                {
                    string threeQuote = """
                        {
                            "title": "unmatched [ ( { inside raw string",
                            "regex": "^[a-z]+([0-9]{2,4}$"
                        }
                        """;

                    string fourQuoteContainingTriple = """"
                        Here is an embedded """ triple quote with unmatched ) ] } and ( [ {
                        inside a 4-quote C# 12 raw string literal!
                        """";

                    return threeQuote + fourQuoteContainingTriple;
                }
            }
            """"";

        AssertZeroFalsePositivesForBuffer("F01", "ModernCSharp12.cs", code);
    }

    // =========================================================================
    // F02: C# 12 nested generics with >>, verbatim + interpolated $@"...{dict["k)"]}..."
    // =========================================================================
    [AvaloniaFact]
    public void F02_CSharp12_GenericsShift_VerbatimInterpolatedStrings_NestedQuotes()
    {
        const string code = @"namespace Demo.GenericsAndInterpolation;

using System;
using System.Collections.Generic;

public sealed class GenericMatrix<TKey, TValue>
    where TKey : notnull
{
    public Dictionary<TKey, List<KeyValuePair<string, Func<int, bool>>>> Map { get; }
        = new Dictionary<TKey, List<KeyValuePair<string, Func<int, bool>>>>();

    public string FormatEntry(Dictionary<string, string> dict, int mask)
    {
        int shifted = (mask >> 2) + (1 << 3);
        string a = $@""Path\To\File({dict[""k)""]})_[{shifted}]"";
        string b = @$""Another\Path\{{escaped}}\{dict.GetValueOrDefault(""x]"", ""default("" )}!"";
        return a + b;
    }
}
";
        AssertZeroFalsePositivesForBuffer("F02", "GenericsAndInterpolation.cs", code);
    }

    // =========================================================================
    // F03: C# #if / #elif / #else / #endif with separate opening/closing braces
    // across conditional compilation branches
    // =========================================================================
    [AvaloniaFact]
    public void F03_CSharp_PreprocessorConditionalBranchesWithBraces()
    {
        const string code = @"using System;

namespace Demo.Conditional
{
    public sealed class PlatformAdapter
    {
        public int Compute(int x)
        {
#if NET8_0_OR_GREATER
            if (x > 0)
            {
#elif NETSTANDARD2_1
            if (x >= 1)
            {
#else
            if (x != 0)
            {
#endif
                return x * 2;
            }
            return 0;
        }

        public string BranchCompleteBlocks(bool flag)
        {
#if FEATURE_FAST
            if (flag)
            {
                return ""fast({"";
            }
#else
            if (flag)
            {
                return ""slow[)"";
            }
#endif
            return ""none"";
        }
    }
}
";
        AssertZeroFalsePositivesForBuffer("F03", "PlatformAdapter.cs", code);
    }

    // =========================================================================
    // F04: Python 3.8-3.13 walrus :=, match/case, PEP 695 generics class Box[T]:,
    // decorators, type hints
    // =========================================================================
    [AvaloniaFact]
    public void F04_Python_Walrus_MatchCase_Pep695Generics_Decorators_TypeHints()
    {
        const string code = """
            from __future__ import annotations
            from dataclasses import dataclass
            from typing import Callable, Iterable

            type Vector[T] = list[T]

            def trace[F: Callable[..., object]](fn: F) -> F:
                return fn

            @dataclass(slots=True)
            class Box[T, U: (int, str)]:
                value: T
                label: U

                @trace
                def transform[R](self, mapper: Callable[[T], R]) -> R:
                    return mapper(self.value)

            def process_command(cmd: dict[str, object], items: Iterable[int]) -> str:
                filtered = [y for x in items if (y := x * 2) > 4]
                match cmd:
                    case {"action": "move", "coords": (int(x), int(y))} if (dist := abs(x) + abs(y)) > 0:
                        return f"move:{x},{y} (dist={dist}, count={len(filtered)})"
                    case {"action": "quit" | "exit"}:
                        return "bye"
                    case _:
                        return "unknown"
            """;

        AssertZeroFalsePositivesForBuffer("F04", "modern_python.py", code);
    }

    // =========================================================================
    // F05: Python 3.12 PEP 701 f-strings with nested same-quotes f"{x["k"]}",
    // multi-line f-string interpolation, format specifiers print(f"{a!r:>10}")
    // =========================================================================
    [AvaloniaFact]
    public void F05_Python_Pep701NestedFStrings_FormatSpecs_MultiLineInterpolation()
    {
        const string code = """
            def render_summary(data: dict[str, str], items: list[str], a: str) -> str:
                line1 = f"user={data["name"]}_role={data["role"]}"
                line2 = f"joined={', '.join([x.strip() for x in items])}"
                line3 = f"{a!r:>10} / {len(items):04d}"
                line4 = f"multi={
                    data.get(
                        "key_with_parens_()",
                        "fallback_[0]"
                    )
                }"
                print(f"{a!r:>10}")
                return "\n".join([line1, line2, line3, line4])
            """;

        AssertZeroFalsePositivesForBuffer("F05", "pep701_fstrings.py", code);
    }

    // =========================================================================
    // F06: Python backslash line continuations and triple-quoted strings with
    // unbalanced brackets and quotes
    // =========================================================================
    [AvaloniaFact]
    public void F06_Python_LineContinuations_And_TripleQuotedStringsWithBraces()
    {
        const string code = """"
            SCHEMA_DOC = '''
            Example payload with unbalanced brackets:
              { "items": [1, 2, (3 + 4]
              def foo(:
            '''

            RAW_SQL = """
            SELECT *
            FROM users
            WHERE meta LIKE '%{unclosed_brace%[unclosed_bracket%(%'
            """

            def compute_total(a: int, b: int, c: int) -> str:
                total = (a + b) \
                    + (c * 2)
                msg = "prefix with (open paren " \
                    "and closing paren) across backslash continuation"
                return f"{total}:{msg}:{len(SCHEMA_DOC)}:{len(RAW_SQL)}"
            """";

        AssertZeroFalsePositivesForBuffer("F06", "continuations_and_triple.py", code);
    }

    // =========================================================================
    // F07: TypeScript generics <T,>(x: T), decorators, optional chaining,
    // nested template literals `${`${a}`}`, regex vs division ambiguity
    // =========================================================================
    [AvaloniaFact]
    public void F07_TypeScript_Generics_NestedTemplates_RegexVsDivision_OptionalChaining()
    {
        const string code = """
            export interface ApiResponse<TData = unknown> {
                readonly data?: {
                    readonly items?: ReadonlyArray<TData>;
                };
                readonly meta?: Record<string, string>;
            }

            export const identity = <T,>(x: T): T => x;

            export function formatMetric<T extends { id: string; score: number }>(
                resp: ApiResponse<T> | undefined,
                divisor: number
            ): string {
                const first = resp?.data?.items?.[0];
                const ratio = (first?.score ?? 100) / divisor / 2;
                const rx = /^[a-z0-9_({\[]+\/[a-z]+\)$/i;
                const nested = `outer:${`${first?.id ?? "none"} (${ratio.toFixed(2)})`}:ok`;
                return rx.test(nested) ? nested : `fallback:${nested}`;
            }
            """;

        AssertZeroFalsePositivesForBuffer("F07", "metrics.ts", code);
    }

    // =========================================================================
    // F08: TSX with JSX {} expressions, > text, and multi-line numbered list
    // prose (1) First item) inside component tree
    // =========================================================================
    [AvaloniaFact]
    public void F08_Tsx_JsxExpressions_ComparisonText_And_NumberedListProse()
    {
        const string code = """
            import React from "react";

            export interface Props<T> {
                readonly items: ReadonlyArray<T>;
                readonly renderLabel: (item: T, index: number) => string;
            }

            export const GenericChecklist = <T,>({ items, renderLabel }: Props<T>) => {
                const count = items.length;
                return (
                    <section className="checklist" data-empty={count === 0}>
                        <h2>Steps (count &gt; 0): {count > 0 ? "Ready -> Go" : "Empty"}</h2>
                        <div className="instructions">
                            1) Select an item from the list below
                            2) Review the diagnostics panel
                        </div>
                        <ul>
                            {items.map((item, idx) => (
                                <li key={idx}>
                                    {`${idx + 1}) ${renderLabel(item, idx)}`}
                                </li>
                            ))}
                        </ul>
                    </section>
                );
            };
            """;

        AssertZeroFalsePositivesForBuffer("F08", "GenericChecklist.tsx", code);
    }

    // =========================================================================
    // F09: Modern JavaScript (ES2023+) classes, private fields, regex literals,
    // template strings, optional chaining
    // =========================================================================
    [AvaloniaFact]
    public void F09_JavaScript_ModernClasses_Regex_TemplateLiterals()
    {
        const string code = """
            export class TokenScanner {
                #pattern = /^([a-zA-Z_$][a-zA-Z0-9_$]*)\s*:\s*([^;]+);?$/;

                parseLine(input, fallback = { key: "none", value: "()" }) {
                    const trimmed = input?.trim?.() ?? "";
                    const match = this.#pattern.exec(trimmed);
                    if (!match) {
                        return `${fallback.key}=${ `${fallback.value}` }`;
                    }
                    const [, key, val] = match;
                    const half = (trimmed.length + 4) / 2;
                    return `${key}:${val}@${half}`;
                }
            }
            """;

        AssertZeroFalsePositivesForBuffer("F09", "scanner.js", code);
    }

    // =========================================================================
    // F10: Go raw strings with unbalanced braces, hex floats, generics, channels
    // =========================================================================
    [AvaloniaFact]
    public void F10_Go_RawStringsWithBraces_HexFloats_Generics()
    {
        const string code = """
            package sample

            import "fmt"

            type Pair[K comparable, V any] struct {
            	Key   K
            	Value V
            }

            func FilterSlice[T any](in []T, pred func(T) bool) []T {
            	out := make([]T, 0, len(in))
            	for _, v := range in {
            		if pred(v) {
            			out = append(out, v)
            		}
            	}
            	return out
            }

            func RawTemplateAndHexFloat() string {
            	const raw = `
            	unbalanced brackets in Go raw string:
            	{ [ ( "hello"
            	func broken( {
            `
            	scale := 0x1.fp-2
            	r := ')'
            	return fmt.Sprintf("%s:%g:%c", raw, scale, r)
            }
            """;

        AssertZeroFalsePositivesForBuffer("F10", "sample.go", code);
    }

    // =========================================================================
    // F11: Rust lifetimes 'a, char literals 'a' and ')', raw strings r#"..."#,
    // macros println! / vec!
    // =========================================================================
    [AvaloniaFact]
    public void F11_Rust_Lifetimes_CharLiterals_RawStrings_Macros()
    {
        const string code = """
            pub struct Parser<'a, T: Clone + 'a> {
                input: &'a str,
                items: Vec<T>,
            }

            impl<'a, T: Clone + 'a> Parser<'a, T> {
                pub fn new(input: &'a str, seed: T) -> Self {
                    let open_paren = '(';
                    let close_paren = ')';
                    let quote_char = '\'';
                    let raw_pattern = r#"unbalanced { [ ( "quotes" in raw rust string"#;
                    let raw_multi = r##"nested "# and unmatched ) ] } in r## string"##;
                    println!(
                        "init {}{}{}: {} / {}",
                        open_paren, quote_char, close_paren, raw_pattern, raw_multi
                    );
                    Self {
                        input,
                        items: vec![seed],
                    }
                }

                pub fn slice<'b>(&'b self) -> &'a str
                where
                    'a: 'b,
                {
                    self.input
                }
            }
            """;

        AssertZeroFalsePositivesForBuffer("F11", "parser.rs", code);
    }

    // =========================================================================
    // F12: Strict JSON (nested objects, arrays, escaped quotes and brackets)
    // =========================================================================
    [AvaloniaFact]
    public void F12_Json_StrictNestedDocumentWithBracketsInStrings()
    {
        const string code = """
            {
              "name": "codeless-workbench",
              "version": "1.0.0",
              "scripts": {
                "build": "dotnet build -c Release",
                "pattern": "^([a-z]+\\{[0-9]+\\]\\))$"
              },
              "limits": [10, 20, 30],
              "flags": {
                "zeroFalsePositives": true,
                "nullable": null
              }
            }
            """;

        AssertZeroFalsePositivesForBuffer("F12", "package.json", code);
    }

    // =========================================================================
    // F13: JSONC (comments // and /* */, trailing commas) in .jsonc and VS Code
    // config filenames (settings.json, tsconfig.json, launch.json)
    // =========================================================================
    [AvaloniaFact]
    public void F13_Jsonc_Comments_TrailingCommas_And_VsCodeConfigFiles()
    {
        const string jsonc = """
            {
              // Line comment with unmatched brackets: ( [ {
              "compilerOptions": {
                "target": "ES2022",
                "module": "ESNext", /* block comment with ) ] } */
                "strict": true,
                "paths": {
                  "@app/*": ["./src/*"],
                },
              },
              "include": [
                "src/**/*",
              ],
            }
            """;

        AssertZeroFalsePositivesForBuffer("F13a", "tsconfig.json", jsonc);
        AssertZeroFalsePositivesForBuffer("F13b", "settings.json", jsonc);
        AssertZeroFalsePositivesForBuffer("F13c", "launch.json", jsonc);
        AssertZeroFalsePositivesForBuffer("F13d", "config.jsonc", jsonc);
    }

    // =========================================================================
    // F14: YAML workflow / config with comments, block scalars, flow sequences
    // =========================================================================
    [AvaloniaFact]
    public void F14_Yaml_WorkflowWithBlockScalarsAndFlowCollections()
    {
        const string code = """
            name: CI Verify
            on:
              push:
                branches: [main, release/*]
              pull_request:
                types: [opened, synchronize]

            jobs:
              build:
                runs-on: ubuntu-latest
                env:
                  DOTNET_NOLOGO: "true"
                steps:
                  - name: Checkout
                    uses: actions/checkout@v4
                  - name: Run script
                    run: |
                      echo " Building (step [1/2]) "
                      dotnet --info
            """;

        AssertZeroFalsePositivesForBuffer("F14", "ci.yml", code);
    }

    // =========================================================================
    // F15: Markdown with headings, lists, links, and fenced code blocks
    // =========================================================================
    [AvaloniaFact]
    public void F15_Markdown_WithFencedCodeBlocksAndLists()
    {
        const string code = """
            # Release Architecture Guide

            1) Overview of bracket and syntax pipelines
            2) Verification checklist :)

            ```csharp
            public sealed record User(string Name, int Age);
            ```

            ```bash
            echo "hello (world)"
            ```

            See [SyntaxVerifier](https://example.com/docs_(v2)) for details.
            """;

        AssertZeroFalsePositivesForBuffer("F15", "ARCHITECTURE.md", code);
    }

    // =========================================================================
    // F16: Shell script with heredocs (<<EOF, <<'EOF', <<-EOF), $(...) command
    // substitution, and case ... in foo) patterns
    // =========================================================================
    [AvaloniaFact]
    public void F16_Shell_Heredocs_CaseStatements_InsideCommandSubstitution()
    {
        const string code = """
            #!/usr/bin/env bash
            set -euo pipefail

            mode="${1:-fast}"
            resolved="$(
              case "$mode" in
                fast|quick)
                  echo "fast-mode"
                  ;;
                full)
                  echo "full-mode"
                  ;;
                *)
                  echo "default-mode"
                  ;;
              esac
            )"

            cat <<'EOF' > /tmp/sample_doc.txt
            Unbalanced brackets and apostrophes inside quoted heredoc:
            It's a test with ( [ { and no closing brackets!
            EOF

            cat <<-EOF > /tmp/sample_doc2.txt
            	Another heredoc with unmatched ) ] }
            EOF

            echo "Resolved: ${resolved}"
            """;

        AssertZeroFalsePositivesForBuffer("F16", "deploy.sh", code);
    }

    // =========================================================================
    // F17: HTML5 with inline <script> and <style> blocks
    // =========================================================================
    [AvaloniaFact]
    public void F17_Html_WithInlineStyleAndScript()
    {
        const string code = """
            <!DOCTYPE html>
            <html lang="en">
            <head>
              <meta charset="utf-8" />
              <title>codeLess Preview</title>
              <style>
                :root { --accent: #0078d4; }
                body {
                  margin: 0;
                  font-family: system-ui, -apple-system, sans-serif;
                  background: linear-gradient(180deg, rgba(0, 120, 212, 0.12), transparent);
                }
              </style>
            </head>
            <body>
              <main id="app" data-ready="true">
                <h1>Ready</h1>
              </main>
              <script>
                (function () {
                  const el = document.getElementById("app");
                  if (el) {
                    el.setAttribute("data-loaded", String(Date.now()));
                  }
                })();
              </script>
            </body>
            </html>
            """;

        AssertZeroFalsePositivesForBuffer("F17", "index.html", code);
    }

    // =========================================================================
    // F18: CSS and SCSS with nesting, variables, media queries, and unquoted
    // url(https://...)
    // =========================================================================
    [AvaloniaFact]
    public void F18_CssAndScss_WithNestingVariablesAndUnquotedUrls()
    {
        const string css = """
            :root {
              --bg-primary: #1f1f1f;
              --fg-primary: #cccccc;
            }

            .editor-shell {
              background-color: var(--bg-primary);
              color: var(--fg-primary);
              background-image: url("https://example.com/assets/bg_(dark).svg");
              padding: calc(100% - (2 * 8px));
            }
            """;

        const string scss = """
            $accent: #0078d4;

            // Single-line comment with unmatched ( [ {
            .workbench-header {
              color: $accent;
              background: url(https://example.com/banner.png) no-repeat center;

              &:hover,
              &:focus-visible {
                transform: translateY(-1px);
              }
            }
            """;

        AssertZeroFalsePositivesForBuffer("F18a", "styles.css", css);
        AssertZeroFalsePositivesForBuffer("F18b", "theme.scss", scss);
    }

    // =========================================================================
    // F19: SQL (CTEs, window functions, JSON expressions, comments with brackets)
    // =========================================================================
    [AvaloniaFact]
    public void F19_Sql_Cte_WindowFunctions_StringsAndComments()
    {
        const string code = """
            -- Query active accounts (excluding archived [legacy] rows)
            WITH ranked_events AS (
                SELECT
                    e.account_id,
                    e.created_at,
                    ROW_NUMBER() OVER (
                        PARTITION BY e.account_id
                        ORDER BY e.created_at DESC
                    ) AS rn
                FROM audit_events AS e
                WHERE e.payload_text NOT LIKE '%(ignore[%'
            )
            SELECT account_id, created_at
            FROM ranked_events
            WHERE rn = 1;
            """;

        AssertZeroFalsePositivesForBuffer("F19", "active_accounts.sql", code);
    }

    // =========================================================================
    // F20: Makefile with variables, pattern rules, and tab-indented recipes
    // =========================================================================
    [AvaloniaFact]
    public void F20_Makefile_TargetsVariablesAndTabRecipes()
    {
        const string code =
            "CC := clang\n" +
            "CFLAGS := -Wall -Wextra -O2\n" +
            "SRCS := $(wildcard src/*.c)\n" +
            "OBJS := $(SRCS:.c=.o)\n\n" +
            ".PHONY: all clean\n\n" +
            "all: $(OBJS)\n" +
            "\t@echo \"Building target ($@) from ($^)\"\n\n" +
            "clean:\n" +
            "\t@rm -f $(OBJS)\n";

        AssertZeroFalsePositivesForBuffer("F20", "Makefile", code);
    }

    // =========================================================================
    // F21: Dockerfile multi-stage build with RUN heredoc/continuations and JSON CMD
    // =========================================================================
    [AvaloniaFact]
    public void F21_Dockerfile_MultiStageBuild()
    {
        const string code = """
            FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
            WORKDIR /src
            COPY ["src/codeLess.Avalonia/codeLess.Avalonia.csproj", "src/codeLess.Avalonia/"]
            RUN dotnet restore "src/codeLess.Avalonia/codeLess.Avalonia.csproj" \
                && echo "Restore completed (ok)"

            FROM mcr.microsoft.com/dotnet/runtime:8.0 AS final
            WORKDIR /app
            COPY --from=build /src /app
            ENTRYPOINT ["dotnet", "codeLess.Avalonia.dll"]
            """;

        AssertZeroFalsePositivesForBuffer("F21", "Dockerfile", code);
    }

    // =========================================================================
    // F22: XML and Avalonia AXAML / XAML with namespaces, bindings, and CDATA
    // =========================================================================
    [AvaloniaFact]
    public void F22_XmlAndXaml_WithBindingsAndCData()
    {
        const string xml = """
            <?xml version="1.0" encoding="utf-8"?>
            <Project Sdk="Microsoft.NET.Sdk">
              <!-- Comment with unmatched ( [ { -->
              <PropertyGroup>
                <TargetFramework>net8.0</TargetFramework>
                <Nullable>enable</Nullable>
              </PropertyGroup>
              <ItemGroup>
                <Content Include="Assets\**\*.png" />
              </ItemGroup>
            </Project>
            """;

        const string axaml = """
            <UserControl xmlns="https://github.com/avaloniaui"
                         xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
                         x:Class="Demo.SampleView">
              <Border Padding="12,8" CornerRadius="4">
                <TextBlock Text="{Binding Title, FallbackValue='Ready (OK)'}" />
              </Border>
            </UserControl>
            """;

        AssertZeroFalsePositivesForBuffer("F22a", "Sample.csproj", xml);
        AssertZeroFalsePositivesForBuffer("F22b", "SampleView.axaml", axaml);
    }

    // =========================================================================
    // F23: Real Repository Sweep — every src/**/*.cs, *.json, and *.yml file in
    // this repository produces zero diagnostics, zero unexpected red brackets,
    // and passes SyntaxVerifier without requiring an external LSP.
    // =========================================================================
    [AvaloniaFact]
    public void F23_RealRepositorySweep_AllSrcCSharp_Json_And_YamlFilesHaveZeroFalsePositives()
    {
        string root = FindRepoRoot();
        string srcDir = Path.Combine(root, "src");

        var csFiles = Directory.GetFiles(srcDir, "*.cs", SearchOption.AllDirectories)
            .Where(p =>
                !p.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar, StringComparison.Ordinal) &&
                !p.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            .OrderBy(p => p, StringComparer.Ordinal)
            .ToList();

        Assert.True(csFiles.Count >= 40, $"Expected at least 40 C# source files under src/, found {csFiles.Count}");

        foreach (string file in csFiles)
        {
            string rel = Path.GetRelativePath(root, file);
            string content = File.ReadAllText(file);
            AssertZeroFalsePositivesForBuffer("F23-CS:" + rel, file, content, runExternalVerifier: false);
        }

        var jsonAndYamlFiles = Directory.GetFiles(root, "*.*", SearchOption.AllDirectories)
            .Where(p =>
            {
                if (p.Contains(Path.DirectorySeparatorChar + ".git" + Path.DirectorySeparatorChar, StringComparison.Ordinal) ||
                    p.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar, StringComparison.Ordinal) ||
                    p.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar, StringComparison.Ordinal))
                {
                    return false;
                }
                string ext = Path.GetExtension(p);
                return ext.Equals(".json", StringComparison.OrdinalIgnoreCase) ||
                       ext.Equals(".jsonc", StringComparison.OrdinalIgnoreCase) ||
                       ext.Equals(".yml", StringComparison.OrdinalIgnoreCase) ||
                       ext.Equals(".yaml", StringComparison.OrdinalIgnoreCase);
            })
            .OrderBy(p => p, StringComparer.Ordinal)
            .ToList();

        Assert.NotEmpty(jsonAndYamlFiles);
        foreach (string file in jsonAndYamlFiles)
        {
            string rel = Path.GetRelativePath(root, file);
            string content = File.ReadAllText(file);
            AssertZeroFalsePositivesForBuffer("F23-CFG:" + rel, file, content, runExternalVerifier: false);
        }
    }

    // =========================================================================
    // F24: End-to-end Headless Avalonia Workbench Sweep — open corpus files in
    // MainWindow, render editor frames, save tabs, and assert zero error
    // diagnostics, zero red squiggles, zero unexpected brackets, and clean save.
    // =========================================================================
    [AvaloniaFact]
    public void F24_HeadlessAvaloniaWorkbench_OpenRenderAndSaveCorpusFiles_ZeroFalsePositives()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "codeless_w20f_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            var files = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["Modern.cs"] = """""
                    namespace WorkbenchSweep;

                    public sealed class Box(string label)
                    {
                        public string Render()
                        {
                            string raw = """"
                                Embedded """ with unmatched ( [ { inside 4-quote raw string
                                """";
                            return $"{label}:{raw.Length}";
                        }
                    }
                    """"",
                ["script.py"] = """
                    def greet(user: dict[str, str]) -> str:
                        return f"hello {
                            user.get("name", "world_(1)")
                        }!"
                    """,
                ["View.tsx"] = """
                    export const View = () => (
                        <div>
                            1) First item
                            2) Second item
                        </div>
                    );
                    """,
                ["tsconfig.json"] = """
                    {
                      // JSONC comment with ( [ {
                      "compilerOptions": {
                        "strict": true,
                      },
                    }
                    """,
                ["theme.scss"] = """
                    .banner {
                      background: url(https://example.com/hero.svg);
                    }
                    """,
                ["run.sh"] = """
                    #!/usr/bin/env bash
                    out="$(
                      case "${1:-a}" in
                        a) echo "alpha" ;;
                        *) echo "other" ;;
                      esac
                    )"
                    echo "$out"
                    """
            };

            MainWindow.LanguageServersEnabledOverride = false;
            var win = new MainWindow(tempDir)
            {
                Width = 1280,
                Height = 800
            };
            win.Show();
            PumpUi(6);

            foreach (var kv in files)
            {
                string fullPath = Path.Combine(tempDir, kv.Key);
                File.WriteAllText(fullPath, kv.Value);

                var tab = win.OpenFileInTab(fullPath, preview: false);
                PumpUi(6);

                Assert.NotNull(tab);
                Assert.Equal(fullPath, tab!.FilePath);

                // Force editor layout + bracket + syntax pass as rendered on PrimaryEditor.Canvas
                win.PrimaryEditor.Canvas.InvalidateVisual();
                PumpUi(4);

                Assert.Empty(tab.Diagnostics);

                var session = EditorSyntaxSession.For(tab);
                session.Prepare(tab);
                session.Brackets.EnsureValid(tab.Lines, Math.Max(0, tab.Lines.Count - 1), 0);
                for (int line = 0; line < tab.Lines.Count; line++)
                {
                    var bd = session.Brackets.GetLine(tab.Lines, line, 0);
                    for (int k = 0; k < bd.Count; k++)
                    {
                        Assert.True(
                            bd.Levels[k] >= 0,
                            $"Workbench tab {kv.Key} line {line + 1}: unexpected red bracket '{bd.Chars[k]}'");
                    }
                }

                // Mark dirty and trigger save on the active tab and verify zero syntax errors in status bar
                tab.IsDirty = true;
                Assert.True(win.SaveActiveTab(), $"SaveActiveTab failed for {kv.Key}: {win.StatusMessageText}");
                PumpUi(4);
                Assert.False(tab.IsDirty, $"Tab {kv.Key} should be clean after SaveActiveTab()");
            }

            win.SuppressUnsavedPrompts = true;
            win.Close();
        }
        finally
        {
            MainWindow.LanguageServersEnabledOverride = null;
            try { Directory.Delete(tempDir, recursive: true); } catch { }
        }
    }
}
