using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using codeLess.Studio;
using Xunit;

namespace codeLess.Avalonia.Tests
{
    /// <summary>Shared (Windows + Avalonia) engines of the built-in dev tools: blame, graph, diff, HTTP client, Markdown.</summary>
    public class GitBlameAndGraphTests
    {
        private const string H1 = "1111111111111111111111111111111111111111";
        private const string H2 = "2222222222222222222222222222222222222222";
        private const string Z = "0000000000000000000000000000000000000000";

        [Fact]
        public void BlamePorcelain_ParsesHeadersOnce_AndRepeatsCommitForLaterLines()
        {
            string porcelain =
                H1 + " 1 1 2\n" +
                "author Ada Lovelace\nauthor-mail <ada@example.com>\nauthor-time 1700000000\nauthor-tz +0000\n" +
                "committer Ada Lovelace\ncommitter-mail <ada@example.com>\ncommitter-time 1700000000\ncommitter-tz +0000\n" +
                "summary Initial engine\nboundary\nfilename src/a.cs\n\tline one\n" +
                H1 + " 2 2\n\tline two\n" +
                Z + " 3 3 1\nauthor External file (--contents)\nauthor-mail <external.file>\nauthor-time 1800000000\nauthor-tz +0000\n" +
                "committer External file (--contents)\ncommitter-mail <external.file>\ncommitter-time 1800000000\ncommitter-tz +0000\n" +
                "summary Version of src/a.cs from src/a.cs\nprevious " + H2 + " src/a.cs\nfilename src/a.cs\n\tline three\n";
            var lines = GitBlameParser.ParsePorcelain(porcelain);
            Assert.Equal(3, lines.Count);
            Assert.Equal(1, lines[0].FinalLine);
            Assert.Equal("Ada Lovelace", lines[0].Commit.Author);
            Assert.Equal("ada@example.com", lines[0].Commit.AuthorMail);
            Assert.Equal("Initial engine", lines[0].Commit.Summary);
            Assert.True(lines[0].Commit.IsBoundary);
            Assert.Same(lines[0].Commit, lines[1].Commit);
            Assert.Equal(2, lines[1].FinalLine);
            Assert.True(lines[2].Commit.IsUncommitted);
            Assert.Equal(H2, lines[2].Commit.PreviousHash);
            Assert.Equal(new DateTime(2023, 11, 14, 22, 13, 20, DateTimeKind.Utc), lines[0].Commit.AuthorTimeUtc);

            DateTime now = lines[0].Commit.AuthorTimeUtc.AddDays(3).AddHours(2);
            Assert.Equal("Ada Lovelace, 3 days ago • Initial engine", GitBlameParser.FormatAnnotation(lines[0].Commit, "someone else", now));
            Assert.Equal("You, 3 days ago • Initial engine", GitBlameParser.FormatAnnotation(lines[0].Commit, "ada lovelace", now));
            Assert.Equal("You • Uncommitted changes", GitBlameParser.FormatAnnotation(lines[2].Commit, "", now));
        }

        [Theory]
        [InlineData(10, "just now")]
        [InlineData(60 * 5, "5 minutes ago")]
        [InlineData(3600 * 3, "3 hours ago")]
        [InlineData(3600 * 30, "yesterday")]
        [InlineData(86400 * 10, "1 week ago")]
        [InlineData(86400 * 100, "3 months ago")]
        [InlineData(86400 * 800, "2 years ago")]
        public void RelativeTime_IsGitLensStyle(int secondsAgo, string expected)
        {
            DateTime now = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            Assert.Equal(expected, GitCli.FormatRelativeTime(now.AddSeconds(-secondsAgo), now));
        }

        [Fact]
        public void LogParser_ReadsFieldsAndDecorations()
        {
            string raw = "aaaaaaaa1\u001fbbbbbbbb2 cccccccc3\u001fAda\u001fada@x.org\u001f1700000000\u001fHEAD -> main, origin/main, origin/HEAD, tag: v1.0\u001fMerge branch 'x'\u001e\n" +
                         "bbbbbbbb2\u001f\u001fBob\u001fbob@x.org\u001f1690000000\u001f\u001fRoot commit\u001e";
            var commits = GitLogParser.Parse(raw, new List<string> { "origin" });
            Assert.Equal(2, commits.Count);
            Assert.True(commits[0].IsMerge);
            Assert.Equal(new[] { "bbbbbbbb2", "cccccccc3" }, commits[0].Parents);
            Assert.Equal("Merge branch 'x'", commits[0].Subject);
            Assert.Equal(3, commits[0].Refs.Count); // origin/HEAD is dropped
            Assert.True(commits[0].Refs[0].IsCurrent);
            Assert.Equal("main", commits[0].Refs[0].Name);
            Assert.Equal(GitRefBadge.KindRemote, commits[0].Refs[1].Kind);
            Assert.Equal(GitRefBadge.KindTag, commits[0].Refs[2].Kind);
            Assert.Equal("v1.0", commits[0].Refs[2].Name);
            Assert.Empty(commits[1].Parents);
        }

        [Fact]
        public void NameStatusZ_HandlesRenames()
        {
            var files = GitLogParser.ParseNameStatusZ("M\0src/a.cs\0R087\0old name.cs\0new name.cs\0A\0b.txt\0");
            Assert.Equal(3, files.Count);
            Assert.Equal("R", files[1].Status);
            Assert.Equal("old name.cs", files[1].OldPath);
            Assert.Equal("new name.cs", files[1].Path);
            Assert.Equal("old name.cs → new name.cs", files[1].DisplayName);
        }

        [Fact]
        public void GraphLayout_LinearHistory_StaysInLaneZero()
        {
            var g = new GitGraphLayout();
            var r1 = g.AddCommit("c3", new[] { "c2" });
            var r2 = g.AddCommit("c2", new[] { "c1" });
            var r3 = g.AddCommit("c1", new string[0]);
            Assert.All(new[] { r1, r2, r3 }, r => Assert.Equal(0, r.NodeLane));
            Assert.Equal("cb:0>0", string.Join(",", r1.Segments));
            Assert.Equal("tc:0>0,cb:0>0", string.Join(",", r2.Segments));
            Assert.Equal("tc:0>0", string.Join(",", r3.Segments));
            Assert.Equal(0, g.ActiveLaneCount);
        }

        [Fact]
        public void GraphLayout_MergeOpensSecondLane_AndConvergesAtForkPoint()
        {
            // M merges F (feature) into B; both B and F descend from A.
            //   M
            //   |\
            //   B F
            //   |/
            //   A
            var g = new GitGraphLayout();
            var m = g.AddCommit("M", new[] { "B", "F" });
            Assert.Equal(0, m.NodeLane);
            Assert.Equal("cb:0>0,cb:0>1", string.Join(",", m.Segments));
            Assert.NotEqual(m.Segments[0].Color, m.Segments[1].Color);

            var f = g.AddCommit("F", new[] { "A" });
            Assert.Equal(1, f.NodeLane);
            Assert.Equal("tb:0>0,tc:1>1,cb:1>1", string.Join(",", f.Segments));

            var b = g.AddCommit("B", new[] { "A" });
            Assert.Equal(0, b.NodeLane);
            Assert.Equal("tc:0>0,tb:1>1,cb:0>0", string.Join(",", b.Segments));

            var a = g.AddCommit("A", new string[0]);
            Assert.Equal(0, a.NodeLane);
            // Both lanes that waited for A converge into its node.
            Assert.Equal("tc:0>0,tc:1>0", string.Join(",", a.Segments));
            Assert.Equal(2, a.LaneCount);
            Assert.Equal(0, g.ActiveLaneCount);
        }

        [Fact]
        public void GraphLayout_TwoBranchTips_UseSeparateLanes_AndReuseFreedLane()
        {
            var g = new GitGraphLayout();
            var t1 = g.AddCommit("T1", new[] { "P" });
            var t2 = g.AddCommit("T2", new[] { "P" });
            Assert.Equal(0, t1.NodeLane);
            Assert.Equal(1, t2.NodeLane);
            var p = g.AddCommit("P", new[] { "Q" });
            Assert.Equal(0, p.NodeLane);
            Assert.Equal(1, g.ActiveLaneCount); // lane 1 freed after convergence
            var t3 = g.AddCommit("X", new[] { "Y" }); // unrelated tip reuses lane 1
            Assert.Equal(1, t3.NodeLane);
        }

        [Fact]
        public void LineDiff_SideBySide_PairsModifiedLines()
        {
            string a = "one\ntwo\nthree\nfour\n";
            string b = "one\nTWO\nthree\nfour\nfive\n";
            var rows = LineDiff.SideBySide(a, b);
            Assert.Equal(5, rows.Count);
            Assert.Equal(LineDiffRow.Equal, rows[0].Kind);
            Assert.Equal(LineDiffRow.Modified, rows[1].Kind);
            Assert.Equal("two", rows[1].LeftText);
            Assert.Equal("TWO", rows[1].RightText);
            Assert.Equal(LineDiffRow.Added, rows[4].Kind);
            Assert.Equal(-1, rows[4].LeftLine);
            Assert.Equal(4, rows[4].RightLine);
            int add, rem;
            LineDiff.CountChanges(rows, out add, out rem);
            Assert.Equal(2, add);
            Assert.Equal(1, rem);

            var inline = LineDiff.Inline(a, b);
            Assert.Equal(new byte[] { 0, 1, 2, 0, 0, 2 }, inline.Select(r => r.Kind).ToArray());
        }

        [Fact]
        public void LineDiff_MyersFindsMinimalScript()
        {
            var ops = LineDiff.ComputeEditScript("a b c a b b a".Split(' '), "c b a b a c".Split(' '));
            int edits = ops.Count(o => o[0] != 0);
            Assert.Equal(5, edits); // classic Myers example: D = 5
            // Replaying the script reproduces the new sequence.
            var rebuilt = ops.Where(o => o[0] != 1).Select(o => "c b a b a c".Split(' ')[o[2]]).ToArray();
            Assert.Equal("c b a b a c".Split(' '), rebuilt);
        }

        [Fact]
        public void HeadSignature_And_RealBlame_OnThisRepository()
        {
            string repo = GitCli.FindRepoRoot(AppContext.BaseDirectory);
            if (repo == null || !GitCli.IsAvailable()) return; // packaged test run without git
            Assert.False(string.IsNullOrEmpty(GitCli.ReadHeadSignature(repo)));
            string file = Path.Combine(repo, "LICENSE");
            if (!File.Exists(file)) return;
            var svc = new GitBlameService();
            using (var done = new ManualResetEventSlim(false))
            {
                svc.BlameReady += p => done.Set();
                string content = File.ReadAllText(file);
                svc.Request(file, content, false, 0, content.Split('\n').Length);
                Assert.True(done.Wait(30000));
                var res = svc.TryGetCached(file);
                Assert.NotNull(res);
                if (!res.NotTracked)
                {
                    Assert.True(res.LinesByZeroBasedIndex.ContainsKey(0));
                    Assert.False(string.IsNullOrEmpty(res.LinesByZeroBasedIndex[0].Commit.Author));
                }
            }
            string err;
            var page = GitHistoryService.LoadPage(repo, 0, 5, null, null, true, GitHistoryService.GetRemoteNames(repo), out err);
            Assert.NotEmpty(page);
            var layout = new GitGraphLayout();
            foreach (var c in page) layout.AddCommit(c.Hash, c.Parents);
        }
    }

    public class HttpClientCoreTests
    {
        private const string Sample =
            "@host = http://127.0.0.1:{{port}}\n" +
            "@token = {{login.response.body.$.data.token}}\n" +
            "\n" +
            "### Log in\n" +
            "# @name login\n" +
            "POST {{host}}/login HTTP/1.1\n" +
            "Content-Type: application/json\n" +
            "\n" +
            "{\n  \"user\": \"ada\"\n}\n" +
            "\n" +
            "###\n" +
            "GET {{host}}/items\n" +
            "    ?page=2\n" +
            "    &size=10\n" +
            "Authorization: Bearer {{token}}\n" +
            "\n" +
            "### Just a URL\n" +
            "https://example.com/plain\n";

        [Fact]
        public void Parser_SplitsRequests_ReadsMetadataHeadersBodyAndQueryContinuation()
        {
            var doc = HttpFileParser.Parse(Sample);
            Assert.Equal(3, doc.Requests.Count);
            Assert.Equal("http://127.0.0.1:{{port}}", doc.FileVariables["host"]);

            var login = doc.Requests[0];
            Assert.Equal("login", login.Name);
            Assert.Equal("Log in", login.Title);
            Assert.Equal("POST", login.Method);
            Assert.Equal("{{host}}/login", login.Url);
            Assert.Equal("HTTP/1.1", login.HttpVersion);
            Assert.Equal(5, login.RequestLine);
            Assert.Single(login.Headers);
            Assert.Equal("{\n  \"user\": \"ada\"\n}", login.Body);

            var items = doc.Requests[1];
            Assert.Equal("{{host}}/items?page=2&size=10", items.Url);
            Assert.Equal("Authorization", items.Headers[0].Name);
            Assert.Equal("", items.Body);

            var plain = doc.Requests[2];
            Assert.Equal("GET", plain.Method);
            Assert.Equal("https://example.com/plain", plain.Url);
            Assert.Same(plain, HttpFileParser.FindRequestAtLine(doc, plain.RequestLine));
        }

        [Fact]
        public void Variables_FileEnvSystemAndRequestVariables()
        {
            var doc = HttpFileParser.Parse(Sample);
            var env = new HttpEnvironmentSet();
            env.LoadJson("{ \"$shared\": { \"port\": \"1\" }, // comment\n \"dev\": { \"port\": 8080, }, \"prod\": {\"port\": \"443\"} }");
            Assert.Equal(new[] { "dev", "prod" }, env.Names.ToArray());
            var resolver = new HttpVariableResolver();
            resolver.FileVariables = doc.FileVariables;
            resolver.EnvironmentVariables = env.GetVariables("dev");

            var login = resolver.Resolve(doc.Requests[0], null);
            Assert.Equal("http://127.0.0.1:8080/login", login.Url);
            Assert.Empty(login.UnresolvedVariables);

            var items = resolver.Resolve(doc.Requests[1], null);
            Assert.Contains("login.response.body.$.data.token", items.UnresolvedVariables);

            resolver.NamedResponses["login"] = new HttpResponseResult
            {
                StatusCode = 200,
                Body = "{\"data\":{\"token\":\"abc123\",\"list\":[1,{\"x\":\"y\"}]}}",
                Headers = new List<HttpHeader> { new HttpHeader("X-Trace", "t-9") }
            };
            items = resolver.Resolve(doc.Requests[1], null);
            Assert.Equal("Bearer abc123", items.GetHeader("Authorization"));
            Assert.Empty(items.UnresolvedVariables);

            var tmp = new List<string>();
            Assert.Equal("t-9", resolver.Substitute("{{login.response.headers.x-trace}}", tmp));
            Assert.Equal("y", resolver.Substitute("{{login.response.body.$.data.list[1].x}}", tmp));
            Assert.Equal("1", resolver.Substitute("{{login.response.body.$['data'].list[0]}}", tmp));
            Assert.Empty(tmp);

            resolver.FixedNowUtc = new DateTime(2025, 1, 2, 3, 4, 5, DateTimeKind.Utc);
            Assert.Equal("1735787045", resolver.Substitute("{{$timestamp}}", tmp));
            Assert.Equal("2025-01-02T03:04:05.000Z", resolver.Substitute("{{$datetime iso8601}}", tmp));
            Assert.True(Guid.TryParse(resolver.Substitute("{{$guid}}", tmp), out _));
            int r = int.Parse(resolver.Substitute("{{$randomInt 5 7}}", tmp));
            Assert.InRange(r, 5, 6);
            Assert.Equal("{{nope}}", resolver.Substitute("{{nope}}", tmp));
            Assert.Contains("nope", tmp);
        }

        [Fact]
        public void EnvironmentDiscovery_FindsHttpClientEnvJsonUpwards()
        {
            string root = Path.Combine(Path.GetTempPath(), "codeLess_httpenv_" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(Path.Combine(root, "api", "v1"));
            try
            {
                File.WriteAllText(Path.Combine(root, "http-client.env.json"), "{\"local\":{\"host\":\"localhost\"}}");
                File.WriteAllText(Path.Combine(root, "http-client.private.env.json"), "{\"local\":{\"secret\":\"s3\"}}");
                string httpFile = Path.Combine(root, "api", "v1", "a.http");
                File.WriteAllText(httpFile, "GET http://x\n");
                var set = HttpEnvironmentSet.Discover(httpFile, root);
                Assert.Equal(new[] { "local" }, set.Names.ToArray());
                var vars = set.GetVariables("local");
                Assert.Equal("localhost", vars["host"]);
                Assert.Equal("s3", vars["secret"]);
            }
            finally { try { Directory.Delete(root, true); } catch { } }
        }

        [Fact]
        public void Curl_And_Formatters()
        {
            var req = new HttpResolvedRequest { Method = "POST", Url = "https://api.example.com/x?a=1", Body = "{\"it's\":1}" };
            req.Headers.Add(new HttpHeader("Content-Type", "application/json"));
            string curl = HttpClientRunner.ToCurl(req);
            Assert.StartsWith("curl -X POST 'https://api.example.com/x?a=1'", curl);
            Assert.Contains("-H 'Content-Type: application/json'", curl);
            Assert.Contains("--data-raw '{\"it'\\''s\":1}'", curl);

            string pretty = BodyFormatter.PrettyJson("{\"a\":[1,2,{}],\"b\":\"x,y:{z}\",\"c\":null}");
            Assert.Equal("{\n  \"a\": [\n    1,\n    2,\n    {}\n  ],\n  \"b\": \"x,y:{z}\",\n  \"c\": null\n}", pretty);
            Assert.Equal("{broken", BodyFormatter.PrettyJson("{broken"));
            var toks = BodyFormatter.TokenizeJson(pretty);
            Assert.Contains(toks, t => t.Kind == BodyTokenKind.Key && pretty.Substring(t.Start, t.Length) == "\"a\"");
            Assert.Contains(toks, t => t.Kind == BodyTokenKind.String && pretty.Substring(t.Start, t.Length) == "\"x,y:{z}\"");
            Assert.Contains(toks, t => t.Kind == BodyTokenKind.Literal);

            string xml = BodyFormatter.PrettyXml("<?xml version=\"1.0\"?><root><a id=\"1\">text</a><b/></root>");
            Assert.Equal("<?xml version=\"1.0\"?>\n<root>\n  <a id=\"1\">text</a>\n  <b/>\n</root>", xml);
            var xt = BodyFormatter.TokenizeXml(xml);
            Assert.Contains(xt, t => t.Kind == BodyTokenKind.AttributeName && xml.Substring(t.Start, t.Length) == "id");
        }

        private static int FreePort()
        {
            var l = new TcpListener(IPAddress.Loopback, 0);
            l.Start();
            int port = ((IPEndPoint)l.LocalEndpoint).Port;
            l.Stop();
            return port;
        }

        [Fact]
        public async Task Runner_SendsToLoopbackServer_RecordsLedger_AllowedInOfflineMode_AndTimesOut()
        {
            int port = FreePort();
            string prefix = "http://127.0.0.1:" + port + "/";
            using (var listener = new HttpListener())
            {
                listener.Prefixes.Add(prefix);
                listener.Start();
                string receivedBody = null, receivedAuth = null, receivedMethod = null;
                var serverTask = Task.Run(async () =>
                {
                    for (int i = 0; i < 3; i++)
                    {
                        HttpListenerContext ctx;
                        try { ctx = await listener.GetContextAsync(); } catch { return; }
                        if (ctx.Request.Url.AbsolutePath == "/slow")
                        {
                            await Task.Delay(3000);
                            try { ctx.Response.Close(); } catch { }
                            continue;
                        }
                        using (var sr = new StreamReader(ctx.Request.InputStream)) receivedBody = sr.ReadToEnd();
                        receivedAuth = ctx.Request.Headers["Authorization"];
                        receivedMethod = ctx.Request.HttpMethod;
                        byte[] payload = Encoding.UTF8.GetBytes("{\"data\":{\"token\":\"tok-42\"}}");
                        ctx.Response.StatusCode = 201;
                        ctx.Response.ContentType = "application/json; charset=utf-8";
                        ctx.Response.Headers["X-Test"] = "yes";
                        ctx.Response.OutputStream.Write(payload, 0, payload.Length);
                        ctx.Response.Close();
                    }
                });

                string file = "@host = http://127.0.0.1:" + port + "\n\n# @name login\nPOST {{host}}/login\nContent-Type: application/json\nAuthorization: Basic abc\n\n{\"user\":\"ada\"}\n\n###\nGET {{host}}/me\nAuthorization: Bearer {{login.response.body.$.data.token}}\n";
                var doc = HttpFileParser.Parse(file);
                var resolver = new HttpVariableResolver { FileVariables = doc.FileVariables };
                bool wasOffline = NetworkLedger.OfflineMode;
                NetworkLedger.OfflineMode = true; // loopback must still be allowed
                try
                {
                    int before = NetworkLedger.Snapshot().Count;
                    var first = await HttpClientRunner.SendAsync(resolver.Resolve(doc.Requests[0], null), 10000, CancellationToken.None);
                    Assert.Equal("", first.Error);
                    Assert.Equal(201, first.StatusCode);
                    Assert.Equal("yes", first.GetHeader("X-Test"));
                    Assert.Equal("{\"data\":{\"token\":\"tok-42\"}}", first.Body);
                    Assert.Equal(first.Body.Length, first.BodyBytes);
                    Assert.Equal("POST", receivedMethod);
                    Assert.Equal("{\"user\":\"ada\"}", receivedBody);
                    Assert.Equal("Basic abc", receivedAuth);
                    resolver.NamedResponses["login"] = first;

                    var second = await HttpClientRunner.SendAsync(resolver.Resolve(doc.Requests[1], null), 10000, CancellationToken.None);
                    Assert.Equal(201, second.StatusCode);
                    Assert.Equal("Bearer tok-42", receivedAuth);

                    var entries = NetworkLedger.Snapshot().Skip(before).Where(e => e.Feature == HttpClientRunner.LedgerFeature).ToList();
                    Assert.True(entries.Count >= 2);
                    Assert.All(entries, e => Assert.Equal(201, e.Status));

                    // Offline Mode blocks non-loopback hosts before any socket is opened.
                    var blocked = await HttpClientRunner.SendAsync(new HttpResolvedRequest { Url = "https://example.invalid/" }, 2000, CancellationToken.None);
                    Assert.True(blocked.Blocked);
                    Assert.Contains("Offline Mode", blocked.Error);

                    var slow = await HttpClientRunner.SendAsync(new HttpResolvedRequest { Url = prefix + "slow" }, 1000, CancellationToken.None);
                    Assert.False(slow.HasResponse);
                    Assert.Contains("timed out", slow.Error);

                    using (var cts = new CancellationTokenSource())
                    {
                        cts.Cancel();
                        var cancelled = await HttpClientRunner.SendAsync(new HttpResolvedRequest { Url = prefix + "slow" }, 5000, cts.Token);
                        Assert.True(cancelled.Cancelled);
                    }
                }
                finally
                {
                    NetworkLedger.OfflineMode = wasOffline;
                    listener.Stop();
                }
            }
        }
    }

    public class MarkdownParserTests
    {
        [Fact]
        public void Blocks_HeadingsParagraphsCodeQuotesRulesTables()
        {
            string md = "# Title\n\nSome *em* and **strong** and `code` and ~~gone~~.\nSecond line  \nhard break.\n\n" +
                        "```csharp\nvar x = 1;\n```\n\n> quoted **text**\n> more\n\n---\n\n" +
                        "| Name | Qty |\n|:-----|----:|\n| a | 1 |\n| b \\| c | 2 |\n\nSetext\n======\n";
            var doc = MarkdownParser.Parse(md);
            var b = doc.Blocks;
            Assert.Equal(MdBlockKind.Heading, b[0].Kind);
            Assert.Equal(1, b[0].Level);
            Assert.Equal("title", b[0].Anchor);

            Assert.Equal(MdBlockKind.Paragraph, b[1].Kind);
            var kinds = b[1].Inlines.Select(i => i.Kind).ToList();
            Assert.Contains(MdInlineKind.Emphasis, kinds);
            Assert.Contains(MdInlineKind.Strong, kinds);
            Assert.Contains(MdInlineKind.Code, kinds);
            Assert.Contains(MdInlineKind.Strikethrough, kinds);
            Assert.Contains(MdInlineKind.LineBreak, kinds);
            Assert.Equal(2, b[1].SourceLine);

            Assert.Equal(MdBlockKind.CodeBlock, b[2].Kind);
            Assert.Equal("csharp", b[2].Language);
            Assert.Equal("var x = 1;", b[2].Code);

            Assert.Equal(MdBlockKind.BlockQuote, b[3].Kind);
            Assert.Equal("quoted text more", MarkdownParser.ToPlainText(b[3].Children[0].Inlines));

            Assert.Equal(MdBlockKind.HorizontalRule, b[4].Kind);

            Assert.Equal(MdBlockKind.Table, b[5].Kind);
            Assert.Equal(new[] { MdAlign.Left, MdAlign.Right }, b[5].TableAlign.ToArray());
            Assert.Equal(2, b[5].TableRows.Count);
            Assert.Equal("b | c", MarkdownParser.ToPlainText(b[5].TableRows[1][0]));

            Assert.Equal(MdBlockKind.Heading, b[6].Kind);
            Assert.Equal(1, b[6].Level);
        }

        [Fact]
        public void Lists_NestedOrderedAndTasks()
        {
            string md = "- one\n- two\n  - nested a\n  - nested b\n- [ ] todo\n- [x] done\n\n3. three\n4. four\n";
            var doc = MarkdownParser.Parse(md);
            var ul = doc.Blocks[0];
            Assert.Equal(MdBlockKind.List, ul.Kind);
            Assert.False(ul.Ordered);
            Assert.Equal(4, ul.Items.Count);
            var nested = ul.Items[1].Blocks.First(x => x.Kind == MdBlockKind.List);
            Assert.Equal(2, nested.Items.Count);
            Assert.Equal("nested b", MarkdownParser.ToPlainText(nested.Items[1].Blocks[0].Inlines));
            Assert.True(ul.Items[2].IsTask);
            Assert.False(ul.Items[2].IsChecked);
            Assert.True(ul.Items[3].IsChecked);
            Assert.Equal("done", MarkdownParser.ToPlainText(ul.Items[3].Blocks[0].Inlines));

            var ol = doc.Blocks[1];
            Assert.True(ol.Ordered);
            Assert.Equal(3, ol.StartNumber);
            Assert.Equal(2, ol.Items.Count);
        }

        [Fact]
        public void Inlines_LinksImagesAutolinksReferencesAndHtml()
        {
            string md = "See [docs](./docs/guide.md#setup \"Guide\"), ![logo](img/logo.png), <https://a.example>, " +
                        "https://b.example/path). Ref [here][r] and [r].\n\n[r]: https://ref.example\n\n" +
                        "<p align=\"center\"><img src=\"assets/x.png\" alt=\"X\"></p>\n\nUse a\\*literal\\* and snake_case_name.";
            var doc = MarkdownParser.Parse(md);
            var inl = doc.Blocks[0].Inlines;
            var links = inl.Where(i => i.Kind == MdInlineKind.Link).ToList();
            Assert.Equal("./docs/guide.md#setup", links[0].Url);
            Assert.Equal("Guide", links[0].Title);
            Assert.Equal("https://a.example", links[1].Url);
            Assert.Equal("https://b.example/path", links[2].Url);
            Assert.Equal("https://ref.example", links[3].Url);
            Assert.Equal("https://ref.example", links[4].Url);
            var img = inl.Single(i => i.Kind == MdInlineKind.Image);
            Assert.Equal("img/logo.png", img.Url);
            Assert.Equal("logo", img.Text);

            var htmlImg = doc.Blocks[1].Inlines.Single(i => i.Kind == MdInlineKind.Image);
            Assert.Equal("assets/x.png", htmlImg.Url);

            Assert.Equal("Use a*literal* and snake_case_name.", MarkdownParser.ToPlainText(doc.Blocks[2].Inlines));
        }

        [Fact]
        public void RtfWriter_ForWindowsPreview_EmitsStylesTablesLinksAnchorsAndImages()
        {
            string dir = Path.Combine(Path.GetTempPath(), "codeLess_mdrtf_" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(dir);
            try
            {
                // 1x1 PNG
                byte[] png = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNk+M9QDwADhgGAWjR9awAAAABJRU5ErkJggg==");
                File.WriteAllBytes(Path.Combine(dir, "dot.png"), png);
                string md = "# Héllo {x}\n\nText **b** *i* `c` [site](https://x.org) ![dot](dot.png)\n\n- [x] task\n\n| A | B |\n|---|--:|\n| 1 | 2 |\n\n```cs\nvar s = \"q\"; // c\n```\n";
                var doc = MarkdownParser.Parse(md);
                var theme = StudioTheme.GetById(StudioTheme.DefaultThemeId);
                var res = MarkdownRtfWriter.Build(doc, MarkdownRtfColors.FromTheme(theme), src =>
                {
                    string p = MarkdownLinkResolver.ResolveLocalImage(src, Path.Combine(dir, "README.md"));
                    return p != null && File.Exists(p) ? File.ReadAllBytes(p) : null;
                });
                Assert.StartsWith(@"{\rtf1", res.Rtf);
                Assert.Contains(@"H\u233?llo \{x\}", res.Rtf);
                Assert.Contains(@"{\b b}", res.Rtf);
                Assert.Contains(@"{\i i}", res.Rtf);
                Assert.Contains(@"\trowd", res.Rtf);
                Assert.Contains(@"\qr", res.Rtf);
                Assert.Contains(@"\pngblip", res.Rtf);
                Assert.Contains("\\u" + ((int)'☑').ToString() + "?", res.Rtf); // checked task box
                Assert.Single(res.Links);
                Assert.Equal("https://x.org", res.Links[0].Url);
                Assert.Equal("site", res.Links[0].Text);
                Assert.Contains(res.Anchors, a => a.HeadingAnchor == "héllo-x" && a.SourceLine == 0);
                int w, h; string blip;
                Assert.True(MarkdownRtfWriter.TryGetImageSize(png, out w, out h, out blip));
                Assert.Equal(1, w);
                Assert.Equal("pngblip", blip);
                // Balanced groups (escaped braces excluded).
                string unescaped = res.Rtf.Replace(@"\{", "").Replace(@"\}", "");
                Assert.Equal(unescaped.Count(ch => ch == '{'), unescaped.Count(ch => ch == '}'));
            }
            finally { try { Directory.Delete(dir, true); } catch { } }
        }

        [Fact]
        public void LinkResolver_ClassifiesTargets()
        {
            string mdFile = Path.Combine(Path.GetTempPath(), "proj", "README.md");
            var ext = MarkdownLinkResolver.Resolve("https://x.org", mdFile);
            Assert.Equal(MdLinkTarget.KindExternal, ext.Kind);
            var anchor = MarkdownLinkResolver.Resolve("#Getting-Started", mdFile);
            Assert.Equal(MdLinkTarget.KindAnchor, anchor.Kind);
            Assert.Equal("getting-started", anchor.Anchor);
            var file = MarkdownLinkResolver.Resolve("docs/My%20Guide.md#intro", mdFile);
            Assert.Equal(MdLinkTarget.KindFile, file.Kind);
            Assert.Equal(Path.Combine(Path.GetTempPath(), "proj", "docs", "My Guide.md"), file.FilePath);
            Assert.Equal("intro", file.Anchor);
            Assert.Equal(MdLinkTarget.KindUnsupported, MarkdownLinkResolver.Resolve("mailto:a@b.c", mdFile).Kind);
            Assert.Null(MarkdownLinkResolver.ResolveLocalImage("https://x/y.png", mdFile));
            Assert.Equal("my-heading-2", MarkdownParser.Slugify("My Heading 2!"));
        }
    }
}
