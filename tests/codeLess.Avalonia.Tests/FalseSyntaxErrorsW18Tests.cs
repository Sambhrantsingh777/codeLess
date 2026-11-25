using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using codeLess.AvaloniaApp;
using codeLess.Studio;
using Xunit;
using Xunit.Abstractions;

namespace codeLess.Avalonia.Tests
{
    [Collection("SequentialWorkbench")]
    public class FalseSyntaxErrorsW18Tests
    {
        private readonly ITestOutputHelper _output;

        public FalseSyntaxErrorsW18Tests(ITestOutputHelper output)
        {
            _output = output;
        }

        public static Dictionary<string, (string FileName, string Language, string Scope, string Code)> GetValidCorpus()
        {
            return new Dictionary<string, (string FileName, string Language, string Scope, string Code)>
            {
                ["python"] = ("corpus_py312.py", "py", "source.python",
@"from dataclasses import dataclass
import functools
from typing import Any, AsyncIterator

@dataclass
class Box[T]:
    """"""Generic box: it's valid Python 3.12 with (parentheses), ""quotes"",
    - *markdown* bullets, and trailing =
    """"""
    value: T

@functools.lru_cache(maxsize=128)
def transform[T](item: T, mapping: dict[str, Any]) -> str:
    # TODO: support additional formatters
    def inner_helper(prefix: str) -> str:
        return f""{prefix}: {mapping['key']} / {mapping[""key)""]} ({{literal}})""

    if (n := len(mapping)) > 0:
        formatted = (
            inner_helper(
                prefix=
                    f'{n:>6d}'
            )
        )
    else:
        formatted = rf""\d+{n}""

    multi = F""""""header
{item!r}
footer""""""
    match mapping.get(""mode"", ""eval""):
        case ""eval"" | ""run"":
            res = multi.strip()
        case {""nested"": [first, *rest]} if len(rest) > 0:
            res = f""{first}: {len(rest)}""
        case _:
            res = ""default""
    total = 1 + \
        2 + 3
    return f""{formatted} | {res} | {total}""

async def collect_positive(stream: AsyncIterator[int], model: Any) -> list[int]:
    model.eval()
    return [x async for x in stream if x > 0]
"),
                ["csharp"] = ("CorpusCSharp12.cs", "cs", "source.cs",
@"using System;
using System.Collections.Generic;
using System.Linq;

namespace Acme.Telemetry;

public sealed record MetricPoint(string Name, double Value, IReadOnlyList<string> Tags);

public sealed class TelemetryProcessor
{
    private readonly Dictionary<string, List<KeyValuePair<int, string>>> _index = new();

    // TODO: wire batch export
    public string FormatPoint(MetricPoint point, Dictionary<string, string> meta)
    {
        char openBrace = '{';
        char closeParen = ')';
        char escapedQuote = '\'';
        int[] extra = [4, 5];
        int[] weights = [1, 2, 3, .. extra];

        string path1 = $@""C:\logs\{point.Name}\""""quoted""""_{meta[""env)""]}.log"";
        string path2 = @$""C:\temp\{point.Value:F2}\{openBrace}{closeParen}{escapedQuote}"";
        string rawJson = """"""
            {
                ""name"": ""unmatched ) } ] inside raw string"",
                ""quote"": ""hello \""world\""""
            }
            """""";
        string rawInterp = $""""""Metric ""{point.Name}"" has {weights.Length} weights"""""";

        Func<int, int> scale = (int x = 1) => x * 2;
        string category = point switch
        {
            { Value: > 0.0 and <= 100.0, Name.Length: > 0 } => ""normal"",
            { Value: > 100.0 } => ""high"",
            _ => ""idle""
        };
        return $""{path1}|{path2}|{rawJson.Length}|{rawInterp}|{scale(3)}|{category}|{_index.Count}"";
    }
}
"),
                ["typescript"] = ("corpus_ts.ts", "ts", "source.ts",
@"// TODO: keep generic helpers allocation-free
export interface MetricEvent<T = unknown> {
    readonly id: string;
    readonly payload?: {
        readonly tags?: readonly string[];
        readonly data?: T;
    };
}

export const identity = <T,>(value: T): T => value;

export class PatternMatcher<T extends Record<string, unknown>> {
    private readonly cache: Map<string, Array<Promise<T>>> = new Map();
    private readonly tokenRegex = /^[({[\]})'""`]+\/\*\d+$/gi;
    private readonly closeRegex = /[)\]}]+/g;

    public evaluate(input: string, event?: MetricEvent<T>): string {
        const match = this.tokenRegex.exec(input) ?? this.closeRegex.exec(input);
        const firstTag = event?.payload?.tags?.[0] ?? 'none';
        const nested = `outer 'apos' ) } ${`inner ""${firstTag}"" (${input.length})`}`;
        return match ? `${nested}:${match[0]}` : identity(nested);
    }
}
"),
                ["tsx"] = ("CorpusComponent.tsx", "tsx", "source.tsx",
@"import React from 'react';

export interface CardProps<T> {
    readonly title: string;
    readonly items: readonly T[];
    readonly renderItem?: (item: T, index: number) => string;
}

export const GenericCard = <T extends { id: string; label?: string },>(props: CardProps<T>) => {
    const pattern = /['"")\]}]+/g;
    const summary = `Count: ${props.items.length} (${props.title})`;
    return (
        <section className=""card"" data-summary={summary}>
            <h2>It's {props.title} — 1) First section</h2>
            <ul>
                {props.items.map((it, idx) => (
                    <li key={it.id} data-valid={!pattern.test(it.id)}>
                        {props.renderItem?.(it, idx) ?? it.label ?? `Item #${idx + 1}`}
                    </li>
                ))}
            </ul>
        </section>
    );
};
"),
                ["jsonc"] = ("settings.json", "json", "source.json",
@"// VS Code workspace settings (JSON with Comments and trailing commas)
{
    /* Multi-line block comment with unmatched } ) ] brackets
       and ""quotes"" inside */
    ""editor.fontSize"": 14,
    ""editor.tabSize"": 4, // inline comment after value with } )
    ""files.exclude"": {
        ""**/.git"": true,
        ""**/bin"": true,
    },
    ""search.SpotPatterns"": [
        ""// not a comment inside string"",
        ""/* also not a comment */"",
        ""unmatched } ) ] in string"",
    ],
}
"),
                ["yaml"] = ("pipeline.yaml", "yaml", "source.yaml",
@"# TODO: shared deployment configuration
---
defaults: &defaults
  timeout: 30
  retries: 3
  pattern: ""unmatched ) } ] in double quotes""
  note: 'it''s valid single-quoted YAML'

services:
  api:
    <<: *defaults
    command: |
      echo ""Starting (api) service...""
      if [ -f /tmp/ready ]; then
        echo ""Ready } )""
      fi
...
---
version: 2
status: active
"),
                ["html"] = ("index.html", "html", null,
@"<!DOCTYPE html>
<html lang=""en"">
<head>
    <meta charset=""utf-8"" />
    <title>codeLess Studio — It's Ready</title>
    <style>
        /* CSS comment with } ) */
        :root { --accent: #0078d4; }
        .banner:not(.hidden) {
            padding: calc(100% - 24px);
            content: ""{ } ( )"";
        }
    </style>
</head>
<body>
    <!-- HTML comment with unmatched } ) ] -->
    <main class=""banner"">1) Welcome — It's working</main>
    <script>
        function init(config) {
            const re = /[)}]/g;
            return config && !re.test(config.name);
        }
    </script>
</body>
</html>
"),
                ["css"] = ("styles.css", "css", "source.css",
@"/* Main stylesheet with } in comment */
:root {
    --bg-primary: #1f1f1f;
    --fg-primary: #cccccc;
}

@media (min-width: 768px) {
    .editor-pane:not(.collapsed) {
        width: calc(100% - 48px);
        background-color: var(--bg-primary);
        color: var(--fg-primary);
    }
    .editor-pane::after {
        content: ""unmatched } ) in string"";
    }
}
"),
                ["scss"] = ("theme.scss", "scss", "source.css.scss",
@"// SCSS variables and nested rules
$accent: #0078d4;
$pad: 12px;

.workbench {
    padding: $pad;
    &__header:not(.compact) {
        color: $accent;
        content: ""{ ) }"";
    }
}
"),
                ["markdown"] = ("README.md", "md", "text.html.markdown",
@"# Project Overview

It's a modern editor supporting:
1) Fast rendering with `{braces}` and `(parens` in prose
2) Fenced code blocks:

```python
def greet(name: str) -> str:
    return f""Hello {name}!""
```
"),
                ["shell"] = ("deploy.sh", "sh", "source.shell",
@"#!/usr/bin/env bash
set -euo pipefail

# TODO: support dry-run flag
APP_DIR=""$(cd ""$(dirname ""$0"")"" && pwd)""

case ""${1:-start}"" in
    start|run)
        echo ""Starting from ${APP_DIR}""
        ;;
    stop)
        echo ""Stopping service""
        ;;
    *)
        cat <<'EOF'
Usage: deploy.sh [start|stop]
  Note: unmatched ) } "" ' inside heredoc is valid shell!
EOF
        ;;
esac
"),
                ["go"] = ("server.go", "go", "source.go",
@"package main

import ""fmt""

func MapSlice[T any, U any](items []T, fn func(T) U) []U {
	out := make([]U, 0, len(items))
	rawPath := `C:\temp\([a-z]+)\`
	openBrace := '{'
	escapedQuote := '\''
	_ = rawPath
	_ = openBrace
	_ = escapedQuote
	for _, it := range items {
		out = append(out, fn(it))
	}
	return out
}

func main() {
	xs := MapSlice([]int{1, 2, 3}, func(v int) string {
		return fmt.Sprintf(""%d"", v)
	})
	fmt.Println(len(xs))
}
"),
                ["rust"] = ("lib.rs", "rs", "source.rust",
@"/* Outer block comment /* nested inner ) } */ still comment */
pub fn select_longest<'a, 'b>(first: &'a str, second: &'b str) -> &'a str {
    let open_brace: char = '{';
    let quote_char: char = '\'';
    let raw_pat = r#""unmatched ) } ""quotes ) }"" inside raw string""#;
    let _ = (open_brace, quote_char, raw_pat);
    if first.len() >= second.len() {
        first
    } else {
        second
    }
}
")
            };
        }

        [Fact]
        public void ValidCorpus_ProducesZeroDiagnostics_ZeroSyntaxVerifierErrors_ZeroUnexpectedBrackets_AndZeroInvalidScopes()
        {
            var corpus = GetValidCorpus();
            var failures = new List<string>();

            foreach (var kv in corpus)
            {
                string key = kv.Key;
                var item = kv.Value;
                var lines = item.Code.Replace("\r\n", "\n").Split('\n').ToList();

                // 1. ComputeBufferAstAndDiagnostics (live editor squiggles & Problems panel)
                var analysis = LlmHarnessAndAgentEngine.ComputeBufferAstAndDiagnostics(lines, item.Language, "/tmp/" + item.FileName, item.FileName);
                foreach (var d in analysis.Diagnostics)
                {
                    failures.Add($"[ComputeBufferAstAndDiagnostics:{key}] {d.Severity} L{d.Line}:C{d.Column} '{d.Message}' ({d.Source})");
                }

                // 2. SyntaxVerifier.VerifyCodeSyntax (write_file / edit_file / get_diagnostics gate)
                if (!SyntaxVerifier.VerifyCodeSyntax("/tmp/" + item.FileName, item.Code, out string synErr, allowExternalProcess: true))
                {
                    failures.Add($"[SyntaxVerifier:{key}] {synErr}");
                }

                // 3. EditorBracketCache unexpected closing brackets (Levels == -1 painted in error red)
                var brackets = new EditorBracketCache(BracketLexProfile.ForFile("/tmp/" + item.FileName));
                brackets.Sync(lines);
                for (int li = 0; li < lines.Count; li++)
                {
                    var bd = brackets.GetLine(lines, li, 0);
                    if (bd == null) continue;
                    for (int bi = 0; bi < bd.Count; bi++)
                    {
                        if (bd.Levels[bi] < 0)
                        {
                            failures.Add($"[EditorBracketCache:{key}] Unexpected bracket '{bd.Chars[bi]}' (Level=-1) at L{li + 1}:C{bd.Cols[bi] + 1}: {lines[li]}");
                        }
                    }
                }

                // 4. TextMate invalid.* scopes (rendered in error red)
                string scope = item.Scope ?? TextMateGrammarRegistry.Shared.GetScopeForFile("/tmp/" + item.FileName);
                if (!string.IsNullOrEmpty(scope))
                {
                    var g = TextMateGrammarRegistry.Shared.GetCompiled(scope, true);
                    Assert.NotNull(g);
                    TmStateStack state = null;
                    for (int li = 0; li < lines.Count; li++)
                    {
                        var lr = g.TokenizeLine(lines[li], li == 0 ? null : state);
                        state = lr.EndState;
                        for (int ti = 0; ti < lr.Tokens.Count; ti++)
                        {
                            string sc = lr.Tokens.Scopes[ti].ToString();
                            if (sc.IndexOf("invalid", StringComparison.OrdinalIgnoreCase) >= 0)
                            {
                                string tokText = lines[li].Substring(lr.Tokens.Starts[ti], lr.Tokens.EndOf(ti) - lr.Tokens.Starts[ti]);
                                failures.Add($"[TextMate:{key}] invalid scope '{sc}' on '{tokText}' at L{li + 1}: {lines[li]}");
                            }
                        }
                    }
                }
            }

            if (failures.Count > 0)
            {
                foreach (string f in failures) _output.WriteLine(f);
            }
            Assert.Empty(failures);
        }

        [Fact]
        public void RealSyntaxErrors_AreStillCaught_AcrossSupportedValidators()
        {
            // 1. C#: real syntax errors caught by lazy Roslyn (LanguageVersion.Preview) in SyntaxVerifier
            //    and unexpected closing brackets highlighted by EditorBracketCache (while live non-LSP buffer diagnostics stay 0)
            string badCs = "namespace Demo {\n    public class Broken {\n        internal void Run(int a) {\n            int x = (a + 2));\n        }\n    }\n}\n";
            var csLines = badCs.Split('\n');
            var csDiag = LlmHarnessAndAgentEngine.ComputeBufferAstAndDiagnostics(csLines, "cs", "/tmp/Broken.cs", "Broken.cs");
            Assert.Empty(csDiag.Diagnostics); // Match VS Code: no heuristic live C# squiggles without LSP
            Assert.False(SyntaxVerifier.VerifyCodeSyntax("/tmp/Broken.cs", badCs, out string csErr));
            Assert.Contains("Line 4:", csErr); // Roslyn syntax error

            var csBrackets = new EditorBracketCache(BracketLexProfile.ForFile("/tmp/Broken.cs"));
            csBrackets.Sync(csLines);
            var csLine3 = csBrackets.GetLine(csLines, 3, 0);
            Assert.Contains(-1, csLine3.Levels.Take(csLine3.Count)); // Unexpected ')' highlighted by EditorBracketCache

            // 2. Python: unbalanced closing delimiter and unclosed triple quote caught by SyntaxVerifier & EditorBracketCache
            string badPy = "def compute(a, b):\n    return (a + b))\n";
            var pyLines = badPy.Split('\n');
            Assert.Empty(LlmHarnessAndAgentEngine.ComputeBufferAstAndDiagnostics(pyLines, "py", "/tmp/bad.py", "bad.py").Diagnostics);
            Assert.False(SyntaxVerifier.VerifyCodeSyntax("/tmp/bad.py", badPy, out string pyErr, allowExternalProcess: false));
            Assert.Contains("Unbalanced closing delimiter", pyErr);

            var pyBrackets = new EditorBracketCache(BracketLexProfile.ForFile("/tmp/bad.py"));
            pyBrackets.Sync(pyLines);
            var pyLine1 = pyBrackets.GetLine(pyLines, 1, 0);
            Assert.Contains(-1, pyLine1.Levels.Take(pyLine1.Count));

            string unclosedPyTriple = "def f():\n    s = \"\"\"unclosed docstring\n    return s\n";
            Assert.False(SyntaxVerifier.VerifyCodeSyntax("/tmp/bad2.py", unclosedPyTriple, out string pyErr2, allowExternalProcess: false));
            Assert.Contains("Unclosed", pyErr2);

            // 3. TypeScript / TSX: unclosed brace caught by SyntaxVerifier, unexpected closer highlighted by EditorBracketCache
            string badTs = "export function broken(x: number) {\n    if (x > 0) {\n        return x;\n}\n";
            Assert.Empty(LlmHarnessAndAgentEngine.ComputeBufferAstAndDiagnostics(badTs.Split('\n'), "ts", "/tmp/bad.ts", "bad.ts").Diagnostics);
            Assert.False(SyntaxVerifier.VerifyCodeSyntax("/tmp/bad.ts", badTs, out string tsErr, allowExternalProcess: false));
            Assert.Contains("Unclosed delimiter", tsErr);

            // 4. JSON / JSONC: missing colon, unclosed object, and invalid bare identifier caught in BOTH live diagnostics & SyntaxVerifier
            string badJson1 = "{\n  \"name\" 123\n}";
            var jsonDiag1 = LlmHarnessAndAgentEngine.ComputeBufferAstAndDiagnostics(badJson1.Split('\n'), "json", "/tmp/bad1.json", "bad1.json");
            Assert.Single(jsonDiag1.Diagnostics);
            Assert.Equal(2, jsonDiag1.Diagnostics[0].Line);
            Assert.Contains("Expected ':'", jsonDiag1.Diagnostics[0].Message);
            Assert.False(SyntaxVerifier.VerifyCodeSyntax("/tmp/bad1.json", badJson1, out string jErr1));
            Assert.Contains("Expected ':'", jErr1);

            string badJson2 = "// comment\n{\n  \"enabled\": tru\n}";
            var jsonDiag2 = LlmHarnessAndAgentEngine.ComputeBufferAstAndDiagnostics(badJson2.Split('\n'), "jsonc", "/tmp/bad2.jsonc", "bad2.jsonc");
            Assert.Single(jsonDiag2.Diagnostics);
            Assert.Equal(3, jsonDiag2.Diagnostics[0].Line);
            Assert.False(SyntaxVerifier.VerifyCodeSyntax("/tmp/bad2.jsonc", badJson2, out _));

            // 5. Go, Rust, CSS: unmatched brace caught by SyntaxVerifier & EditorBracketCache
            string badGo = "package main\nfunc main() {\n}\n}\n";
            Assert.False(SyntaxVerifier.VerifyCodeSyntax("/tmp/bad.go", badGo, out string goErr));
            Assert.Contains("Unbalanced closing delimiter '}'", goErr);

            string badRs = "fn main() {\n    let x = 1;\n}}\n";
            Assert.False(SyntaxVerifier.VerifyCodeSyntax("/tmp/bad.rs", badRs, out string rsErr));
            Assert.Contains("Unbalanced closing delimiter '}'", rsErr);

            string badCss = ".card {\n    color: red;\n}}\n";
            Assert.False(SyntaxVerifier.VerifyCodeSyntax("/tmp/bad.css", badCss, out string cssErr));
            Assert.Contains("Unbalanced closing delimiter '}'", cssErr);
        }

        [AvaloniaFact]
        public void Diagnostics_ClearAndUpdate_AfterEdit_Undo_Redo_CloseReopen_LanguageSwitch_AndLspPublish()
        {
            string tempWs = Path.Combine(Path.GetTempPath(), "codeless-w18-ws-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(tempWs);
            string jsonPath = Path.Combine(tempWs, "settings.json");
            string validJson = "{\n    \"editor.fontSize\": 14\n}\n";
            File.WriteAllText(jsonPath, validJson);

            MainWindow window = null;
            try
            {
                window = new MainWindow(tempWs) { SuppressUnsavedPrompts = true, Width = 1280, Height = 800 };
                window.Show();
                Dispatcher.UIThread.RunJobs();

                var tab = window.OpenFileInTab(jsonPath, preview: false);
                Dispatcher.UIThread.RunJobs();
                Assert.Empty(tab.Diagnostics);
                Assert.Equal(0, window.ExplorerTree.ProblemCountByFullPath.TryGetValue(jsonPath, out int c0) ? c0 : 0);

                // 1. Edit JSON to introduce a syntax error -> live JSON diagnostic appears immediately
                var canvas = window.PrimaryEditor.Canvas;
                canvas.Focus();
                tab.CursorLine = 1;
                tab.CursorCol = tab.Lines[1].Length;
                window.KeyTextInput(" @");
                Dispatcher.UIThread.RunJobs();
                Assert.Single(tab.Diagnostics);
                Assert.Equal(1, window.ExplorerTree.ProblemCountByFullPath[jsonPath]);

                // 2. Undo the edit -> diagnostic clears immediately
                canvas.PerformUndoRedo(redo: false);
                Dispatcher.UIThread.RunJobs();
                Assert.Empty(tab.Diagnostics);
                Assert.Equal(0, window.ExplorerTree.ProblemCountByFullPath[jsonPath]);

                // 3. Redo the edit -> diagnostic returns
                canvas.PerformUndoRedo(redo: true);
                Dispatcher.UIThread.RunJobs();
                Assert.Single(tab.Diagnostics);

                // 4. Switch language mode to Markdown -> JSON diagnostic clears, syntax session updates to Markdown
                window.SetActiveBufferLanguage("md");
                Dispatcher.UIThread.RunJobs();
                Assert.Equal("md", tab.Language);
                Assert.Empty(tab.Diagnostics);
                Assert.Equal(0, window.ExplorerTree.ProblemCountByFullPath[jsonPath]);
                EditorSyntaxSession.For(tab).Prepare(tab);
                Assert.False(EditorSyntaxSession.For(tab).Brackets.Profile.Enabled);

                // Switch back to JSON -> JSON diagnostic is recomputed
                window.SetActiveBufferLanguage("json");
                Dispatcher.UIThread.RunJobs();
                Assert.Single(tab.Diagnostics);

                // 5. LSP publish & clear -> merges and clears stale LSP diagnostics
                canvas.PerformUndoRedo(redo: false);
                Dispatcher.UIThread.RunJobs();
                Assert.Empty(tab.Diagnostics);
                LspDiagnosticsStore.Set(jsonPath, new List<DiagnosticItem>
                {
                    new DiagnosticItem { Severity = "Error", Line = 2, Column = 5, EndColumn = 10, Message = "LSP schema error", IsLanguageServer = true, FilePath = jsonPath }
                });
                LspDiagnosticsStore.MergeInto(tab);
                window.RefreshOutlineAndProblems(tab);
                Assert.Single(tab.Diagnostics);
                Assert.Equal(1, window.ExplorerTree.ProblemCountByFullPath[jsonPath]);

                LspDiagnosticsStore.Clear(jsonPath);
                LspDiagnosticsStore.MergeInto(tab);
                window.RefreshOutlineAndProblems(tab);
                Assert.Empty(tab.Diagnostics);
                Assert.Equal(0, window.ExplorerTree.ProblemCountByFullPath[jsonPath]);

                // 6. Re-introduce error, close tab -> Explorer problem badge clears; reopen clean file -> 0 diagnostics
                canvas.PerformUndoRedo(redo: true);
                Dispatcher.UIThread.RunJobs();
                Assert.Single(tab.Diagnostics);
                window.CloseTab(tab, forceWithoutPrompt: true);
                Dispatcher.UIThread.RunJobs();
                Assert.False(window.ExplorerTree.ProblemCountByFullPath.ContainsKey(jsonPath));

                var reopened = window.OpenFileInTab(jsonPath, preview: false);
                Dispatcher.UIThread.RunJobs();
                Assert.Empty(reopened.Diagnostics);
                Assert.Equal(0, window.ExplorerTree.ProblemCountByFullPath.TryGetValue(jsonPath, out int cReopen) ? cReopen : 0);
            }
            finally
            {
                LspDiagnosticsStore.Clear(jsonPath);
                try { window?.Close(); } catch { }
                try { Directory.Delete(tempWs, true); } catch { }
            }
        }
    }
}
