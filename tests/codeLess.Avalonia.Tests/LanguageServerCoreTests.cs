using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using codeLess.Studio;
using Xunit;

namespace codeLess.Avalonia.Tests
{
    /// <summary>Pure protocol / text utilities of the built-in LSP client (no server process needed).</summary>
    public class LspProtocolUnitTests
    {
        [Fact]
        public void Json_RoundTrips_Unicode_Escapes_Numbers_And_Nesting()
        {
            string json = "{\"a\":1,\"b\":-2.5,\"c\":\"x\\\"y\\\\z\\n\\u00e9\\ud83d\\ude00\",\"d\":[true,false,null,{}],\"e\":{\"f\":[]},\"big\":12345678901}";
            var o = LspJson.Parse(json) as Dictionary<string, object>;
            Assert.NotNull(o);
            Assert.Equal(1L, o["a"]);
            Assert.Equal(-2.5, o["b"]);
            Assert.Equal("x\"y\\z\né😀", o["c"]);
            Assert.Equal(12345678901L, o["big"]);
            var d = (List<object>)o["d"];
            Assert.Equal(true, d[0]);
            Assert.Null(d[2]);
            string back = LspJson.Serialize(o);
            var again = LspJson.Parse(back) as Dictionary<string, object>;
            Assert.Equal("x\"y\\z\né😀", again["c"]);
            Assert.Equal(LspJson.Serialize(again), back);
            // Keys are case-sensitive (unlike the JavaScriptSerializer shim).
            var cs = (Dictionary<string, object>)LspJson.Parse("{\"Range\":1,\"range\":2}");
            Assert.Equal(2, cs.Count);
            Assert.Throws<FormatException>(() => LspJson.Parse("{\"a\":}"));
        }

        [Fact]
        public void Framing_ContentLength_Counts_Utf8_Bytes_And_Tolerates_Extra_Headers()
        {
            var ms = new MemoryStream();
            byte[] f1 = LspJsonRpcConnection.EncodeFrame("{\"m\":\"héllo 😀\"}");
            ms.Write(f1, 0, f1.Length);
            byte[] extra = Encoding.ASCII.GetBytes("Content-Type: application/vscode-jsonrpc; charset=utf-8\r\nContent-Length: 2\r\n\r\n{}");
            ms.Write(extra, 0, extra.Length);
            ms.Position = 0;
            Assert.StartsWith("Content-Length: " + Encoding.UTF8.GetByteCount("{\"m\":\"héllo 😀\"}"), Encoding.ASCII.GetString(f1));
            Assert.Equal("{\"m\":\"héllo 😀\"}", LspJsonRpcConnection.ReadFrame(ms));
            Assert.Equal("{}", LspJsonRpcConnection.ReadFrame(ms));
            Assert.Null(LspJsonRpcConnection.ReadFrame(ms));
        }

        [Fact]
        public void Positions_Map_Astral_Characters_For_Utf16_Utf8_And_Utf32()
        {
            string line = "a😀b€c"; // 😀 = 2 UTF-16 units, 4 UTF-8 bytes; € = 1 unit, 3 bytes
            int idxB = line.IndexOf('b'); // 3
            Assert.Equal(3, LspText.ToLspCharacter(line, idxB, LspText.EncodingUtf16));
            Assert.Equal(5, LspText.ToLspCharacter(line, idxB, LspText.EncodingUtf8));
            Assert.Equal(2, LspText.ToLspCharacter(line, idxB, LspText.EncodingUtf32));
            Assert.Equal(idxB, LspText.FromLspCharacter(line, 5, LspText.EncodingUtf8));
            Assert.Equal(idxB, LspText.FromLspCharacter(line, 2, LspText.EncodingUtf32));
            Assert.Equal(line.IndexOf('c'), LspText.FromLspCharacter(line, 9, LspText.EncodingUtf8));
            // Never inside a surrogate pair, clamped past the end.
            Assert.Equal(1, LspText.FromLspCharacter(line, 2, LspText.EncodingUtf16));
            Assert.Equal(1, LspText.ToLspCharacter(line, 2, LspText.EncodingUtf16));
            Assert.Equal(line.Length, LspText.FromLspCharacter(line, 999, LspText.EncodingUtf16));
        }

        [Fact]
        public void IncrementalChange_Reproduces_New_Text_Under_Fuzzing_Including_Emoji_And_Crlf()
        {
            var rnd = new Random(1234);
            string[] alphabet = { "a", "b", "\n", "😀", "\r\n", " ", "é", "x" };
            for (int iter = 0; iter < 600; iter++)
            {
                string old = RandomText(rnd, alphabet, rnd.Next(0, 30));
                string neu = Mutate(rnd, old, alphabet);
                bool changed = LspText.ComputeIncrementalChange(old, neu, LspText.EncodingUtf16, out int sl, out int sc, out int el, out int ec, out string ins);
                if (old == neu) { Assert.False(changed); continue; }
                Assert.True(changed);
                Assert.Equal(neu, LspText.ApplyChange(old, sl, sc, el, ec, ins, LspText.EncodingUtf16));
                Assert.Equal(neu, LspText.ApplyChange(old, sl, LspText.ToLspCharacter(LineAt(old, sl), sc, LspText.EncodingUtf8),
                    el, LspText.ToLspCharacter(LineAt(old, el), ec, LspText.EncodingUtf8), ins, LspText.EncodingUtf8));
            }
        }

        private static string LineAt(string text, int line)
        {
            var starts = LspText.LineStarts(text);
            return LspText.LineText(text, starts, line);
        }

        private static string RandomText(Random rnd, string[] alphabet, int n)
        {
            var sb = new StringBuilder();
            for (int i = 0; i < n; i++) sb.Append(alphabet[rnd.Next(alphabet.Length)]);
            return sb.ToString();
        }

        private static string Mutate(Random rnd, string s, string[] alphabet)
        {
            // Mutate on "token" boundaries so we never create lone surrogates.
            var tokens = new List<string>();
            for (int i = 0; i < s.Length; i++)
            {
                if (char.IsHighSurrogate(s[i]) && i + 1 < s.Length) { tokens.Add(s.Substring(i, 2)); i++; }
                else if (s[i] == '\r' && i + 1 < s.Length && s[i + 1] == '\n') { tokens.Add("\r\n"); i++; }
                else tokens.Add(s[i].ToString());
            }
            int ops = rnd.Next(0, 3);
            for (int k = 0; k < ops; k++)
            {
                int at = rnd.Next(0, tokens.Count + 1);
                if (rnd.Next(2) == 0 || tokens.Count == 0) tokens.Insert(at, alphabet[rnd.Next(alphabet.Length)]);
                else tokens.RemoveAt(Math.Min(at, tokens.Count - 1));
            }
            return string.Concat(tokens);
        }

        [Fact]
        public void TextEdits_Apply_From_The_End_Keep_Insert_Order_And_Reject_Overlaps()
        {
            string text = "hello world\r\nsecond line\nthird";
            var edits = new List<LspTextEdit>
            {
                new LspTextEdit(new LspRange(1, 0, 1, 6), "2nd"),
                new LspTextEdit(new LspRange(0, 0, 0, 0), "A"),
                new LspTextEdit(new LspRange(0, 0, 0, 0), "B"),
                new LspTextEdit(new LspRange(2, 0, 2, 5), "3rd"),
            };
            Assert.Equal("ABhello world\r\n2nd line\n3rd", LspText.ApplyTextEdits(text, edits, null));
            var overlap = new List<LspTextEdit> { new LspTextEdit(new LspRange(0, 0, 0, 5), "x"), new LspTextEdit(new LspRange(0, 3, 0, 8), "y") };
            Assert.Null(LspText.ApplyTextEdits(text, overlap, null));
        }

        [Fact]
        public void Uris_RoundTrip_Spaces_Hash_Unicode_And_Windows_Drives()
        {
            string p = "/tmp/my dir/a#b/naïve 😀.py";
            string uri = LspText.PathToUri(p);
            Assert.StartsWith("file:///tmp/my%20dir/a%23b/na%C3%AFve%20%F0%9F%98%80.py", uri);
            Assert.Equal(p, LspText.UriToPath(uri));
            Assert.Equal("file:///C:/Users/x/a.ts", LspText.PathToUri("C:\\Users\\x\\a.ts"));
            Assert.Equal("C:\\Users\\x\\a.ts", LspText.UriToPath("file:///c%3A/Users/x/a.ts").Replace("c:", "C:"));
            Assert.Null(LspText.UriToPath("untitled:Untitled-1"));
        }

        [Fact]
        public void Snippets_Expand_To_Plain_Text_With_First_Tab_Stop()
        {
            string t = LspText.ExpandSnippet("print(${1:value}, ${2|a,b|})$0", out int caret, out int sel);
            Assert.Equal("print(value, a)", t);
            Assert.Equal(6, caret);
            Assert.Equal(5, sel);
            t = LspText.ExpandSnippet("for ${1:i} in ${2:range(${3:n})}:\n\t$0", out caret, out sel);
            Assert.Equal("for i in range(n):\n\t", t);
            Assert.Equal(4, caret);
            t = LspText.ExpandSnippet("cost \\$5 ${TM_FILENAME:file} $0end", out caret, out sel);
            Assert.Equal("cost $5 file end", t);
            Assert.Equal("cost $5 file ".Length, caret);
            t = LspText.ExpandSnippet("noStops()", out caret, out sel);
            Assert.Equal(t.Length, caret);
        }

        [Fact]
        public void Markdown_Renders_As_Readable_Text()
        {
            string md = "```python\ndef foo(a: int) -> str\n```\n---\n# Title\nSome **bold** and *it* text with `code`, [a link](https://x.y) and \\_\\_init\\_\\_ kept.\n\n&lt;tag&gt; done\\_x";
            string plain = LspText.MarkdownToPlainText(md);
            Assert.Contains("def foo(a: int) -> str", plain);
            Assert.Contains("Title", plain);
            Assert.DoesNotContain("#", plain);
            Assert.Contains("Some bold and it text with code, a link and __init__ kept.", plain);
            Assert.Contains("<tag> done_x", plain);
            Assert.DoesNotContain("```", plain);
            Assert.Equal("x", LspContent.ToPlainText(LspJson.Parse("{\"kind\":\"plaintext\",\"value\":\" x \"}")));
            Assert.Equal("int a", LspContent.ToPlainText(LspJson.Parse("[{\"language\":\"c\",\"value\":\"int a\"}]")));
        }

        [Fact]
        public void Completion_Filter_Ranks_Prefix_Then_Fuzzy_And_Respects_SortText()
        {
            var items = new List<LspCompletionItem>
            {
                new LspCompletionItem { Label = "getValue", SortText = "b" },
                new LspCompletionItem { Label = "gv_other", SortText = "a" },
                new LspCompletionItem { Label = "GetValues", SortText = "c" },
                new LspCompletionItem { Label = "unrelated", SortText = "0" },
            };
            var r = LspCompletionList.FilterAndSort(items, "getv", 50);
            Assert.Equal(new[] { "getValue", "GetValues" }, r.Select(i => i.Label).ToArray());
            r = LspCompletionList.FilterAndSort(items, "gV", 50);
            Assert.Contains(r, i => i.Label == "getValue");
            Assert.DoesNotContain(r, i => i.Label == "unrelated");
            r = LspCompletionList.FilterAndSort(items, "", 50);
            Assert.Equal("unrelated", r[0].Label); // sortText order without a prefix
        }

        [Fact]
        public void Parsers_Handle_LocationLinks_DocumentChanges_ItemDefaults_And_SignatureOffsets()
        {
            var locs = LspLocation.ParseMany(LspJson.Parse("[{\"targetUri\":\"file:///a.py\",\"targetRange\":{\"start\":{\"line\":1,\"character\":0},\"end\":{\"line\":3,\"character\":0}},\"targetSelectionRange\":{\"start\":{\"line\":1,\"character\":4},\"end\":{\"line\":1,\"character\":7}}}]"));
            Assert.Single(locs);
            Assert.Equal(4, locs[0].Range.StartCharacter);
            var we = LspWorkspaceEdit.Parse(LspJson.Parse("{\"documentChanges\":[{\"textDocument\":{\"uri\":\"file:///a.py\",\"version\":3},\"edits\":[{\"range\":{\"start\":{\"line\":0,\"character\":0},\"end\":{\"line\":0,\"character\":1}},\"newText\":\"x\"}]},{\"kind\":\"rename\",\"oldUri\":\"file:///a.py\",\"newUri\":\"file:///b.py\"}]}"));
            Assert.Equal(2, we.Steps.Count);
            Assert.Equal(3, we.Steps[0].Version);
            Assert.Equal("rename", we.Steps[1].Resource.Kind);
            var cl = LspCompletionList.Parse(LspJson.Parse("{\"isIncomplete\":true,\"itemDefaults\":{\"editRange\":{\"start\":{\"line\":0,\"character\":2},\"end\":{\"line\":0,\"character\":4}},\"insertTextFormat\":2},\"items\":[{\"label\":\"foo\",\"textEditText\":\"foo($1)\"}]}"));
            Assert.True(cl.IsIncomplete);
            Assert.True(cl.Items[0].IsSnippet);
            Assert.Equal("foo($1)", cl.Items[0].TextEdit.NewText);
            Assert.Equal(2, cl.Items[0].TextEdit.Range.StartCharacter);
            var sh = LspSignatureHelp.Parse(LspJson.Parse("{\"signatures\":[{\"label\":\"f(a, b)\",\"parameters\":[{\"label\":[2,3]},{\"label\":\"b\"}]}],\"activeParameter\":1}"));
            Assert.Equal(new[] { 5, 6 }, sh.ActiveParameterSpan);
        }

        [Fact]
        public void WorkspaceEdit_Merges_The_Same_File_Reported_Twice_And_Drops_Identical_Edits()
        {
            string edit = "{\"range\":{\"start\":{\"line\":0,\"character\":4},\"end\":{\"line\":0,\"character\":7}},\"newText\":\"sum\"}";
            string edit2 = "{\"range\":{\"start\":{\"line\":3,\"character\":1},\"end\":{\"line\":3,\"character\":4}},\"newText\":\"sum\"}";
            var we = LspWorkspaceEdit.Parse(LspJson.Parse("{\"documentChanges\":[{\"textDocument\":{\"uri\":\"file:///w/a.c\",\"version\":null},\"edits\":[" + edit + "]}," +
                "{\"textDocument\":{\"uri\":\"file:///w/a.c\",\"version\":null},\"edits\":[" + edit + "," + edit2 + "]}]}"));
            Assert.Single(we.Steps);
            Assert.Equal(2, we.Steps[0].Edits.Count);
            if (LspText.IsMacOS())
            {
                Assert.Equal(LspText.PathKey("/var/folders/x/a.c"), LspText.PathKey("/private/var/folders/x/a.c"));
                var mac = LspWorkspaceEdit.Parse(LspJson.Parse("{\"changes\":{\"file:///var/t/a.c\":[" + edit + "],\"file:///private/var/t/a.c\":[" + edit + "]}}"));
                Assert.Single(mac.Steps);
                Assert.Single(mac.Steps[0].Edits);
            }
            Assert.NotEqual(LspText.PathKey("/privatefoo/var/a"), LspText.PathKey("/var/a"));
        }

        [Fact]
        public void UserSettings_Parse_Overrides_Quotes_And_Off()
        {
            var s = LanguageServerUserSettings.Parse("# comment\npython = \"/opt/my tools/pyright-langserver\" --stdio\nrust = off\nTypeScript=tsls --stdio", false);
            Assert.Equal("\"/opt/my tools/pyright-langserver\" --stdio", s.CommandLines["python"]);
            Assert.Contains("rust", s.DisabledLanguages);
            Assert.Equal(new[] { "/opt/my tools/pyright-langserver", "--stdio" }, LanguageServerUserSettings.SplitCommandLine(s.CommandLines["python"]).ToArray());
            Assert.True(s.CommandLines.ContainsKey("typescript"));
            var r = LanguageServerRegistry.Resolve("rust", null, s);
            Assert.True(r.Disabled);
            Assert.False(r.CanStart);
        }

        [Fact]
        public void Registry_Maps_Extensions_And_Reports_Install_Commands_For_Missing_Servers()
        {
            Assert.Equal("python", LanguageServerRegistry.LanguageIdForPath("/x/a.py"));
            Assert.Equal("typescriptreact", LanguageServerRegistry.LanguageIdForPath("/x/a.tsx"));
            Assert.Equal("cpp", LanguageServerRegistry.LanguageIdForPath("/x/a.hpp"));
            Assert.Equal("shellscript", LanguageServerRegistry.LanguageIdForPath("/x/run.sh"));
            Assert.Null(LanguageServerRegistry.LanguageIdForPath("/x/a.txt"));
            Assert.Equal("typescript", LanguageServerRegistry.DefinitionForLanguage("javascriptreact").Id);
            // Point the override at a command that cannot exist: resolution must fail gracefully with a message.
            var s = LanguageServerUserSettings.Parse("go = definitely-not-a-real-binary-xyz", false);
            var r = LanguageServerRegistry.Resolve("go", null, s);
            Assert.False(r.CanStart);
            Assert.Contains("definitely-not-a-real-binary-xyz", r.Problem);
            Assert.Equal("go install golang.org/x/tools/gopls@latest", LanguageServerRegistry.GetInstallCommand(LanguageServerRegistry.DefinitionById("go"), null));
            Assert.Equal("a\\\"b", LanguageServerRegistry.QuoteArg("a\"b").Trim('"'));
            Assert.Equal("\"has space\"", LanguageServerRegistry.QuoteArg("has space"));
        }

        [Fact]
        public void Buffer_Edits_Keep_Caret_And_Are_Undoable()
        {
            var buf = new EditorTabBuffer { FilePath = "/tmp/x.py", Content = "abc\ndef ghi\n" };
            buf.SyncLinesFromContent();
            buf.CursorLine = 1; buf.CursorCol = 5; // in "ghi"
            Assert.True(LspEditing.ApplyEditsToBuffer(buf, new List<LspTextEdit>
            {
                new LspTextEdit(new LspRange(0, 0, 0, 0), "# top\n"),
                new LspTextEdit(new LspRange(1, 0, 1, 3), "DEFINE"),
            }, true));
            Assert.Equal("# top\nabc\nDEFINE ghi\n", LspEditing.BufferText(buf));
            Assert.Equal(2, buf.CursorLine);
            Assert.Equal(8, buf.CursorCol);
            Assert.True(buf.IsDirty);
            Assert.True(buf.Undo());
            Assert.Equal("abc\ndef ghi\n", LspEditing.BufferText(buf));
        }
    }

    /// <summary>End-to-end tests of LanguageServerManager against the deterministic fake server (tests/Fixtures/fake_lsp_server.py).</summary>
    public class LanguageServerManagerTests : IDisposable
    {
        private readonly string root;
        private readonly string python;
        private readonly string serverScript;
        private LanguageServerManager mgr;

        public LanguageServerManagerTests()
        {
            root = Path.Combine(Path.GetTempPath(), "codeLess_Lsp_" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(root);
            python = LanguageServerRegistry.FindExecutable("python3", null) ?? LanguageServerRegistry.FindExecutable("python", null);
            serverScript = Path.Combine(AppContext.BaseDirectory, "fake_lsp_server.py");
        }

        public void Dispose()
        {
            try { mgr?.Dispose(); } catch { }
            try { Directory.Delete(root, true); } catch { }
        }

        public static string FakeServerCommandLine(out bool available)
        {
            string py = LanguageServerRegistry.FindExecutable("python3", null) ?? LanguageServerRegistry.FindExecutable("python", null);
            string script = Path.Combine(AppContext.BaseDirectory, "fake_lsp_server.py");
            available = py != null && File.Exists(script);
            return "\"" + py + "\" \"" + script + "\"";
        }

        private bool Available
        {
            get
            {
                Assert.True(File.Exists(serverScript), "fake server fixture missing: " + serverScript);
                return python != null; // skip silently only when no Python interpreter exists on this machine
            }
        }

        private LanguageServerManager NewManager(string overrides = null)
        {
            var m = new LanguageServerManager(a => a());
            m.ChangeDebounceMs = 20;
            m.RestartBackoffMs = 50;
            string line = "python = \"" + python + "\" \"" + serverScript + "\"";
            m.UserSettingsProvider = () => LanguageServerUserSettings.Parse(overrides ?? line, false);
            m.SetWorkspaceRoot(root);
            mgr = m;
            return m;
        }

        private EditorTabBuffer NewBuffer(string name, string content)
        {
            string path = Path.Combine(root, name);
            File.WriteAllText(path, content);
            var buf = new EditorTabBuffer { FilePath = path, FileName = name, RelativePath = name, Content = content };
            buf.SyncLinesFromContent();
            return buf;
        }

        private static void WaitUntil(Func<bool> cond, int ms = 15000, string what = "condition")
        {
            var sw = Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < ms)
            {
                if (cond()) return;
                Thread.Sleep(20);
            }
            Assert.Fail("Timed out waiting for " + what);
        }

        private static T Run<T>(Task<T> t)
        {
            Assert.True(t.Wait(20000), "request timed out");
            return t.Result;
        }

        private static void Edit(EditorTabBuffer buf, string newContent)
        {
            buf.Content = newContent;
            buf.SyncLinesFromContent();
        }

        [Fact]
        public void Sync_Diagnostics_Completion_Hover_Signature_Definition_References_Formatting_Symbols()
        {
            if (!Available) return; // python3 not installed on this machine
            var m = NewManager();
            var changedPaths = new List<string>();
            m.DiagnosticsChanged += p => { lock (changedPaths) changedPaths.Add(p); };
            var buf = NewBuffer("main.py", "def helper():\n    return 1\n\nx = helper()   \nBAD = 2\n");
            m.OpenDocument(buf.FilePath, () => LspEditing.BufferText(buf));
            WaitUntil(() => m.GetStatus(buf.FilePath)?.State == LanguageServerState.Running, what: "server running");
            Assert.Equal("fake_lsp_server", m.GetStatus(buf.FilePath).ServerName);

            // publishDiagnostics -> store -> merged into buffer diagnostics next to the heuristic ones
            WaitUntil(() => LspDiagnosticsStore.Count(buf.FilePath) == 1, what: "diagnostics");
            var d = LspDiagnosticsStore.Get(buf.FilePath)[0];
            Assert.Equal(5, d.Line);
            Assert.Equal(1, d.Column);
            Assert.Equal(3, d.Length);
            Assert.Equal("Error", d.Severity);
            Assert.True(d.IsLanguageServer);
            LlmHarnessAndAgentEngine.AnalyzeBufferAstAndDiagnostics(buf);
            Assert.Contains(buf.Diagnostics, x => x.IsLanguageServer && x.Message == "BAD is not allowed");

            // Incremental didChange (debounced) with an astral character keeps the server copy identical.
            Edit(buf, "def helper():\n    return 1\n\nx = helper() 😀  \nGOOD = 2\n");
            m.ChangeDocument(buf.FilePath);
            WaitUntil(() => LspDiagnosticsStore.Count(buf.FilePath) == 0, what: "diagnostics cleared after edit");
            var text = Run(m.RequestRawAsync(buf.FilePath, "fake/getText", uri => LspJson.Obj("uri", uri), CancellationToken.None, 5000));
            Assert.Equal(LspEditing.BufferText(buf), LspJson.GetString(text, "text"));
            var st = Run(m.RequestRawAsync(buf.FilePath, "fake/getState", uri => null, CancellationToken.None, 5000));
            Assert.Contains("incremental", LspJson.GetList(st, "changes").Cast<string>());
            // server -> client requests were answered
            Assert.True(LspJson.GetBool(st, "registered", false));
            Assert.True(LspJson.GetBool(st, "progress_created", false));
            Assert.Equal(2, LspJson.GetList(st, "configuration").Count);
            Assert.Equal("utf-16", (string)((List<object>)LspJson.Path(st, "clientCapabilities", "general", "positionEncodings"))[0]);

            // Completion + resolve + apply (snippet with tab stop, additionalTextEdits)
            Edit(buf, LspEditing.BufferText(buf) + "al");
            buf.CursorLine = buf.Lines.Count - 1; buf.CursorCol = 2;
            m.ChangeDocument(buf.FilePath);
            var list = Run(m.RequestCompletionAsync(buf.FilePath, buf.CursorLine, buf.CursorCol, null, false, CancellationToken.None));
            Assert.Equal(4, list.Items.Count);
            var filtered = LspCompletionList.FilterAndSort(list.Items, "al", 50);
            Assert.Equal(new[] { "alpha", "alphabet" }, filtered.Select(i => i.Label).ToArray());
            var resolved = Run(m.ResolveCompletionAsync(buf.FilePath, filtered[0], CancellationToken.None));
            Assert.Equal("Resolved docs for alpha", resolved.Documentation);
            Assert.True(LspEditing.ApplyCompletion(buf, filtered[1]));
            Assert.Equal("alphabet(x)", buf.Lines[buf.CursorLine]);
            Assert.Equal("alphabet(".Length, buf.SelectionAnchorCol);
            Assert.Equal("alphabet(x".Length, buf.CursorCol); // placeholder selected
            var imp = list.Items.First(i => i.Label == "imp");
            buf.ClearSelection();
            buf.Lines[buf.CursorLine] = "im"; buf.CursorCol = 2; buf.SyncContentFromLines();
            int before = buf.Lines.Count;
            Assert.True(LspEditing.ApplyCompletion(buf, imp));
            Assert.Equal("import imp", buf.Lines[0]);
            Assert.Equal(before + 1, buf.Lines.Count);
            Assert.Equal("imp", buf.Lines[buf.CursorLine]);
            m.ChangeDocument(buf.FilePath);

            // Trigger character completion
            var members = Run(m.RequestCompletionAsync(buf.FilePath, 0, 0, ".", false, CancellationToken.None));
            Assert.Equal("member_one", members.Items[0].Label);
            Assert.Contains(".", m.GetCompletionTriggerCharacters(buf.FilePath));

            // Hover (markdown -> text, sees the latest document version)
            int helperLine = buf.Lines.FindIndex(l => l.StartsWith("x = helper"));
            var hover = Run(m.RequestHoverAsync(buf.FilePath, helperLine, 6, CancellationToken.None));
            Assert.Contains("def helper()", hover.Text);
            Assert.Contains("Docs for helper", hover.Text);
            Assert.DoesNotContain("**", hover.Text);

            // Signature help: active parameter follows commas
            var sig = Run(m.RequestSignatureHelpAsync(buf.FilePath, helperLine, 4, "(", false, CancellationToken.None));
            Assert.Equal("func(a, b)", sig.Active.Label);

            // Definition (LocationLink) and references
            var defs = Run(m.RequestLocationsAsync(buf.FilePath, helperLine, 6, "textDocument/definition", CancellationToken.None));
            Assert.Single(defs);
            Assert.Equal(LspText.PathKey(buf.FilePath), LspText.PathKey(defs[0].FilePath));
            Assert.Equal(buf.Lines.FindIndex(l => l.StartsWith("def helper")), defs[0].Range.StartLine);
            Assert.Equal(4, defs[0].Range.StartCharacter);
            var refs = Run(m.RequestReferencesAsync(buf.FilePath, helperLine, 6, true, CancellationToken.None));
            Assert.Equal(2, refs.Count);

            // Formatting strips the trailing whitespace on the "x = helper()" line
            var fmt = Run(m.RequestFormattingAsync(buf.FilePath, null, 4, true, CancellationToken.None));
            Assert.NotEmpty(fmt);
            Assert.True(LspEditing.ApplyEditsToBuffer(buf, fmt, true));
            Assert.Equal("x = helper() 😀", buf.Lines[helperLine]);

            // Document symbols
            var syms = Run(m.RequestDocumentSymbolsAsync(buf.FilePath, CancellationToken.None));
            Assert.Contains(syms, s => s.Name == "helper" && LspDocumentSymbol.ToOutlineKind(s.Kind) == "fn");
        }

        [Fact]
        public void Rename_Applies_WorkspaceEdit_To_Open_Buffers_And_Files_On_Disk_And_CodeActions_Work()
        {
            if (!Available) return;
            var m = NewManager();
            var buf = NewBuffer("app.py", "def compute():\n    return 1\n\nprint(compute())\nBAD\n");
            string other = Path.Combine(root, "other.py");
            File.WriteAllText(other, "from app import compute\r\nvalue = compute()\r\n");
            m.OpenDocument(buf.FilePath, () => LspEditing.BufferText(buf));
            WaitUntil(() => m.GetStatus(buf.FilePath)?.State == LanguageServerState.Running);

            string placeholder = Run(m.PrepareRenameAsync(buf.FilePath, 0, 5, CancellationToken.None));
            Assert.Equal("compute", placeholder);
            var ex = Assert.ThrowsAny<Exception>(() => Run(m.PrepareRenameAsync(buf.FilePath, 0, 1, CancellationToken.None)));
            Assert.Contains("cannot rename", (ex.InnerException ?? ex).Message);

            var we = Run(m.RequestRenameAsync(buf.FilePath, 0, 5, "calculate", CancellationToken.None));
            Assert.Equal(2, we.AffectedFiles().Count);
            var result = LspEditing.ApplyWorkspaceEdit(we, p => LspText.PathKey(p) == LspText.PathKey(buf.FilePath) ? buf : null, null);
            Assert.True(result.Success, result.Summary);
            Assert.Equal(2, result.FilesChanged);
            Assert.Equal("def calculate():", buf.Lines[0]);
            Assert.Equal("print(calculate())", buf.Lines[3]);
            Assert.Equal("from app import calculate\r\nvalue = calculate()\r\n", File.ReadAllText(other)); // CRLF preserved
            Assert.True(buf.Undo()); // open buffer change is undoable
            Assert.Equal("def compute():", buf.Lines[0]);
            Assert.True(buf.Redo());

            // Code action quick fix from a diagnostic
            m.ChangeDocument(buf.FilePath);
            m.FlushDocument(buf.FilePath);
            WaitUntil(() => LspDiagnosticsStore.Count(buf.FilePath) == 1);
            var actions = Run(m.RequestCodeActionsAsync(buf.FilePath, new LspRange(4, 0, 4, 3), CancellationToken.None));
            Assert.Equal("Replace BAD with GOOD", actions[0].Title);
            Assert.True(actions[0].IsPreferred);
            var applied = LspEditing.ApplyWorkspaceEdit(actions[0].Edit, p => buf, null);
            Assert.True(applied.Success);
            Assert.Equal("GOOD", buf.Lines[4]);

            // Command action -> server sends workspace/applyEdit -> handler applies it to the buffer
            m.ApplyWorkspaceEditHandler = edit => LspEditing.ApplyWorkspaceEdit(edit, p => buf, null).Success;
            var cmd = actions.First(a => a.Title == "Run fake command");
            Run(m.ExecuteCommandAsync(buf.FilePath, cmd.Command, CancellationToken.None));
            WaitUntil(() => buf.Lines[0] == "# applied", what: "workspace/applyEdit");
            WaitUntil(() =>
            {
                var st = Run(m.RequestRawAsync(buf.FilePath, "fake/getState", u => null, CancellationToken.None, 5000));
                return LspJson.GetBool(LspJson.Get(st, "applyEdit"), "applied", false);
            }, what: "applyEdit response");
        }

        [Fact]
        public void Crashed_Server_Restarts_With_Backoff_Reopens_Documents_And_Shutdown_Kills_Process()
        {
            if (!Available) return;
            var m = NewManager();
            var buf = NewBuffer("crash.py", "BAD\n");
            m.OpenDocument(buf.FilePath, () => LspEditing.BufferText(buf));
            WaitUntil(() => m.GetStatus(buf.FilePath)?.State == LanguageServerState.Running);
            WaitUntil(() => LspDiagnosticsStore.Count(buf.FilePath) == 1);
            var inst = m.GetInstanceForPath(buf.FilePath);
            int pid1 = inst.ProcessId;

            m.NotifyRaw(buf.FilePath, "fake/crash", null);
            WaitUntil(() => inst.RestartCount == 1 && inst.State == LanguageServerState.Running && inst.ProcessId != pid1, what: "restart");
            // The reopened document is re-published by the new process.
            WaitUntil(() => LspDiagnosticsStore.Count(buf.FilePath) == 1, what: "diagnostics after restart");
            Assert.Contains("exited", inst.GetLogText());
            int pid2 = inst.ProcessId;

            // Too many crashes -> stays in Error with a message
            m.MaxRestarts = 1;
            m.NotifyRaw(buf.FilePath, "fake/crash", null);
            WaitUntil(() => inst.State == LanguageServerState.Error && inst.Message.Contains("Not restarting"), what: "error state");

            // Manual restart works and shutdown kills the process
            m.RestartServer(buf.FilePath);
            WaitUntil(() => inst.State == LanguageServerState.Running && inst.ProcessId != pid2, what: "manual restart");
            int pid3 = inst.ProcessId;
            m.ShutdownAll(2000);
            WaitUntil(() => !ProcessAlive(pid3), 5000, "process exit");
        }

        private static bool ProcessAlive(int pid)
        {
            try { var p = Process.GetProcessById(pid); return !p.HasExited; }
            catch { return false; }
        }

        [Fact]
        public void Missing_Server_Raises_One_Hint_And_Requests_Fall_Back_To_Null()
        {
            var m = NewManager("python = codeless-no-such-server-binary --stdio");
            var hints = new List<LanguageServerHint>();
            m.ServerMissing += h => hints.Add(h);
            var a = NewBuffer("a.py", "x = 1\n");
            var b = NewBuffer("b.py", "y = 1\n");
            m.OpenDocument(a.FilePath, () => LspEditing.BufferText(a));
            m.OpenDocument(b.FilePath, () => LspEditing.BufferText(b));
            WaitUntil(() => m.GetStatus(a.FilePath)?.State == LanguageServerState.NotInstalled);
            Assert.False(m.IsActive(a.FilePath));
            Assert.Null(Run(m.RequestLocationsAsync(a.FilePath, 0, 0, "textDocument/definition", CancellationToken.None)));
            Assert.Null(Run(m.RequestHoverAsync(a.FilePath, 0, 0, CancellationToken.None)));
            Thread.Sleep(200);
            Assert.Single(hints);
            Assert.Equal("python", hints[0].LanguageId);
            Assert.StartsWith("{ } No Python server", m.GetStatus(a.FilePath).StatusBarText);
            // Unknown languages are ignored entirely.
            var t = NewBuffer("notes.txt", "hello");
            m.OpenDocument(t.FilePath, () => "hello");
            Assert.Null(m.GetStatus(t.FilePath));
        }
    }
}
