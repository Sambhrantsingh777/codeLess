using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using codeLess.Studio;
using Xunit;

namespace codeLess.Avalonia.Tests
{
    /// <summary>Temporary git repositories (local only, no network) for parity tests.</summary>
    internal sealed class TempGitRepos : IDisposable
    {
        public readonly string Root;

        public TempGitRepos()
        {
            Root = Path.Combine(Path.GetTempPath(), "codeless-parity-" + Guid.NewGuid().ToString("N").Substring(0, 10));
            Directory.CreateDirectory(Root);
            Directory.CreateDirectory(Path.Combine(Root, "hooks"));
        }

        public string Path_(string name) { return Path.Combine(Root, name); }

        public static string Git(string dir, params string[] args)
        {
            var r = GitProcess.Run(dir, args, 60000, CancellationToken.None, null, null);
            Assert.True(r.Success, "git " + string.Join(" ", args) + " failed in " + dir + ":\n" + r.Combined);
            return r.Stdout;
        }

        public void Configure(string dir)
        {
            Git(dir, "config", "user.name", "Parity Tester");
            Git(dir, "config", "user.email", "parity@example.com");
            Git(dir, "config", "commit.gpgsign", "false");
            Git(dir, "config", "tag.gpgsign", "false");
            Git(dir, "config", "core.hooksPath", Path.Combine(Root, "hooks"));
            Git(dir, "config", "core.autocrlf", "false");
            Git(dir, "config", "pull.rebase", "false");
        }

        public string Init(string name)
        {
            string dir = Path_(name);
            Directory.CreateDirectory(dir);
            Git(dir, "init", "-q", "-b", "main");
            Configure(dir);
            return dir;
        }

        public string InitBare(string name)
        {
            string dir = Path_(name);
            Directory.CreateDirectory(dir);
            Git(dir, "init", "-q", "--bare", "-b", "main");
            return dir;
        }

        public string Clone(string bare, string name)
        {
            string dir = Path_(name);
            Git(Root, "clone", "-q", bare, dir);
            Configure(dir);
            return dir;
        }

        public static void Write(string dir, string rel, string text)
        {
            string p = Path.Combine(dir, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(p));
            File.WriteAllText(p, text, new UTF8Encoding(false));
        }

        public static void Commit(string dir, string message)
        {
            Git(dir, "add", "-A");
            Git(dir, "commit", "-q", "-m", message);
        }

        public void Dispose()
        {
            try
            {
                foreach (string f in Directory.GetFiles(Root, "*", SearchOption.AllDirectories)) File.SetAttributes(f, FileAttributes.Normal);
                Directory.Delete(Root, true);
            }
            catch { }
        }
    }

    public class GitBranchAndRemoteTests
    {
        [Fact]
        public void BranchStatus_ParsesUpstreamAheadBehind()
        {
            string v2 = "# branch.oid 1234567890abcdef1234567890abcdef12345678\n# branch.head feature/x\n# branch.upstream origin/feature/x\n# branch.ab +3 -2\n1 .M N... 100644 100644 100644 a b file.txt\n";
            var s = GitBranchStatus.Parse(v2);
            Assert.Equal("feature/x", s.Branch);
            Assert.False(s.IsDetached);
            Assert.True(s.HasUpstream);
            Assert.Equal("origin/feature/x", s.Upstream);
            Assert.Equal(3, s.Ahead);
            Assert.Equal(2, s.Behind);
            Assert.Equal("2↓ 3↑", s.SyncText);
            Assert.Equal("feature/x", s.DisplayName);
        }

        [Fact]
        public void BranchStatus_DetachedUnbornAndGoneUpstream()
        {
            var d = GitBranchStatus.Parse("# branch.oid abcdef1234567890\n# branch.head (detached)\n");
            Assert.True(d.IsDetached);
            Assert.Null(d.Branch);
            Assert.Equal("abcdef12", d.DisplayName);
            Assert.False(d.HasUpstream);
            Assert.Equal("", d.SyncText);

            var u = GitBranchStatus.Parse("# branch.oid (initial)\n# branch.head main\n");
            Assert.True(u.IsUnborn);
            Assert.Equal("main", u.DisplayName);

            var g = GitBranchStatus.Parse("# branch.oid abc\n# branch.head main\n# branch.upstream origin/main\n");
            Assert.True(g.UpstreamGone);
            Assert.Equal(0, g.Ahead);

            var op = GitBranchStatus.Parse("# branch.oid abc\n# branch.head main\n");
            op.Operation = "Rebasing";
            Assert.Equal("main (Rebasing)", op.DisplayName);
        }

        [Fact]
        public void ParseRefs_ClassifiesLocalRemoteAndTags()
        {
            string raw = "refs/heads/main\0aaa1111\0origin/main\0*\0 2 hours ago\0Fix\n" +
                         "refs/heads/dev\0bbb2222\0\0 \03 days ago\0Dev work\n" +
                         "refs/remotes/origin/HEAD\0aaa1111\0\0 \02 hours ago\0Fix\n" +
                         "refs/remotes/origin/feature/login\0ccc3333\0\0 \01 day ago\0Login\n" +
                         "refs/tags/v1.0\0ddd4444\0\0 \0a year ago\0Release\n";
            var refs = GitRemoteOps.ParseRefs(raw);
            Assert.Equal(4, refs.Count);
            Assert.True(refs[0].IsHead);
            Assert.Equal(GitRefKind.Local, refs[0].Kind);
            Assert.Equal("origin/main", refs[0].Upstream);
            Assert.False(refs[1].IsHead);
            var remote = refs.Single(r => r.Kind == GitRefKind.Remote);
            Assert.Equal("origin/feature/login", remote.ShortName);
            Assert.Equal("origin", remote.RemoteName);
            Assert.Equal("feature/login", remote.BranchPart);
            Assert.Equal("v1.0", refs.Single(r => r.Kind == GitRefKind.Tag).ShortName);
        }

        [Theory]
        [InlineData(" ! [rejected]        main -> main (fetch first)\nerror: failed to push some refs\nhint: Updates were rejected because the remote contains work", GitErrorKind.NonFastForward)]
        [InlineData(" ! [rejected] main -> main (non-fast-forward)", GitErrorKind.NonFastForward)]
        [InlineData("fatal: could not read Username for 'https://github.com': terminal prompts disabled", GitErrorKind.AuthenticationFailed)]
        [InlineData("git@github.com: Permission denied (publickey).\nfatal: Could not read from remote repository.", GitErrorKind.AuthenticationFailed)]
        [InlineData("remote: Invalid username or password.\nfatal: Authentication failed for 'https://x'", GitErrorKind.AuthenticationFailed)]
        [InlineData("fatal: The current branch topic has no upstream branch.", GitErrorKind.NoUpstream)]
        [InlineData("There is no tracking information for the current branch.", GitErrorKind.NoUpstream)]
        [InlineData("CONFLICT (content): Merge conflict in a.txt\nAutomatic merge failed; fix conflicts and then commit the result.", GitErrorKind.Conflicts)]
        [InlineData("error: Your local changes to the following files would be overwritten by merge:", GitErrorKind.LocalChangesWouldBeOverwritten)]
        [InlineData("fatal: Need to specify how to reconcile divergent branches.", GitErrorKind.DivergentBranches)]
        [InlineData("fatal: unable to access 'https://x/': Could not resolve host: x", GitErrorKind.Network)]
        [InlineData("fatal: something odd", GitErrorKind.Other)]
        public void ErrorClassifier_RecognizesCommonFailures(string output, GitErrorKind expected)
        {
            Assert.Equal(expected, GitErrorClassifier.ClassifyText(output));
            Assert.False(string.IsNullOrEmpty(GitErrorClassifier.Describe(expected, "Push", output)));
        }

        [Fact]
        public void AuthGuidance_PointsToTerminal_AndNeverAsksForPassword()
        {
            string msg = GitErrorClassifier.Describe(GitErrorKind.AuthenticationFailed, "Push", "");
            Assert.Contains("terminal", msg);
            Assert.Contains("never asks for or stores passwords", msg);
        }

        [Fact]
        public void PullArgs_HonourSettingUnlessUserConfiguredPullRebase()
        {
            Assert.Equal(new[] { "pull", "--progress", "--rebase" }, GitRemoteOps.BuildPullArgs(true, false));
            Assert.Equal(new[] { "pull", "--progress", "--no-rebase" }, GitRemoteOps.BuildPullArgs(false, false));
            Assert.Equal(new[] { "pull", "--progress" }, GitRemoteOps.BuildPullArgs(true, true));
            Assert.Equal("origin", GitRemoteOps.PickPublishRemote(new List<string> { "upstream", "origin" }));
            Assert.Equal("fork", GitRemoteOps.PickPublishRemote(new List<string> { "fork" }));
            Assert.Null(GitRemoteOps.PickPublishRemote(new List<string> { "a", "b" }));
            Assert.Equal("my-new-branch", GitRemoteOps.SanitizeBranchName(" my new branch "));
        }

        [Fact]
        public void RealRepos_FetchPullPushSyncPublish_AgainstLocalBareRemote()
        {
            using var t = new TempGitRepos();
            string bare = t.InitBare("remote.git");
            string a = t.Clone(bare, "a");
            TempGitRepos.Write(a, "readme.md", "hello\n");
            TempGitRepos.Commit(a, "init");
            TempGitRepos.Git(a, "push", "-q", "-u", "origin", "main");
            string b = t.Clone(bare, "b");

            // A pushes a new commit; B fetches and is behind by one.
            TempGitRepos.Write(a, "a.txt", "from a\n");
            TempGitRepos.Commit(a, "a1");
            var push = GitRemoteOps.Push(a, CancellationToken.None, null);
            Assert.True(push.Success, push.Message + "\n" + push.Details);

            var progressLines = new List<string>();
            var fetch = GitRemoteOps.Fetch(b, false, false, CancellationToken.None, l => { lock (progressLines) progressLines.Add(l); });
            Assert.True(fetch.Success, fetch.Details);
            var st = GitRemoteOps.GetBranchStatus(b);
            Assert.Equal("main", st.Branch);
            Assert.Equal("origin/main", st.Upstream);
            Assert.Equal(1, st.Behind);
            Assert.Equal(0, st.Ahead);

            var pull = GitRemoteOps.Pull(b, false, CancellationToken.None, null);
            Assert.True(pull.Success, pull.Details);
            st = GitRemoteOps.GetBranchStatus(b);
            Assert.Equal(0, st.Behind);
            Assert.True(File.Exists(Path.Combine(b, "a.txt")));

            // Both commit: B's push is rejected (non-fast-forward) with a clear message.
            TempGitRepos.Write(a, "a2.txt", "a2\n");
            TempGitRepos.Commit(a, "a2");
            Assert.True(GitRemoteOps.Push(a, CancellationToken.None, null).Success);
            TempGitRepos.Write(b, "b1.txt", "b1\n");
            TempGitRepos.Commit(b, "b1");
            var rejected = GitRemoteOps.Push(b, CancellationToken.None, null);
            Assert.False(rejected.Success);
            Assert.Equal(GitErrorKind.NonFastForward, rejected.Error);
            Assert.Contains("Pull", rejected.Message);

            // Sync (rebase) integrates and pushes.
            var sync = GitRemoteOps.Sync(b, true, CancellationToken.None, null);
            Assert.True(sync.Success, sync.Message + "\n" + sync.Details);
            st = GitRemoteOps.GetBranchStatus(b);
            Assert.Equal(0, st.Ahead);
            Assert.Equal(0, st.Behind);
            string log = TempGitRepos.Git(bare, "log", "--format=%s", "main");
            Assert.Contains("b1", log);
            Assert.Contains("a2", log);

            // New local branch: push says "no upstream", Publish sets it; Sync of a branch without upstream publishes.
            var created = GitRemoteOps.CreateBranch(b, "topic one", null, CancellationToken.None);
            Assert.True(created.Success, created.Message);
            Assert.Equal("topic-one", GitRemoteOps.GetBranchStatus(b).Branch);
            var noUp = GitRemoteOps.Push(b, CancellationToken.None, null);
            Assert.Equal(GitErrorKind.NoUpstream, noUp.Error);
            var pub = GitRemoteOps.Publish(b, null, CancellationToken.None, null);
            Assert.True(pub.Success, pub.Details);
            st = GitRemoteOps.GetBranchStatus(b);
            Assert.Equal("origin/topic-one", st.Upstream);

            // A sees the remote branch and checks it out as a tracking branch.
            Assert.True(GitRemoteOps.Fetch(a, false, false, CancellationToken.None, null).Success);
            var refs = GitRemoteOps.ListRefs(a, true);
            var remoteTopic = refs.Single(r => r.Kind == GitRefKind.Remote && r.ShortName == "origin/topic-one");
            var co = GitRemoteOps.Checkout(a, remoteTopic, refs, CancellationToken.None);
            Assert.True(co.Success, co.Details);
            st = GitRemoteOps.GetBranchStatus(a);
            Assert.Equal("topic-one", st.Branch);
            Assert.Equal("origin/topic-one", st.Upstream);

            // Back to main (local), then detached HEAD at a tag.
            refs = GitRemoteOps.ListRefs(a, true);
            Assert.True(GitRemoteOps.Checkout(a, refs.Single(r => r.Kind == GitRefKind.Local && r.ShortName == "main"), refs, CancellationToken.None).Success);
            TempGitRepos.Git(a, "tag", "v1");
            refs = GitRemoteOps.ListRefs(a, true);
            Assert.True(GitRemoteOps.Checkout(a, refs.Single(r => r.Kind == GitRefKind.Tag), refs, CancellationToken.None).Success);
            st = GitRemoteOps.GetBranchStatus(a);
            Assert.True(st.IsDetached);
            Assert.Equal(8, st.DisplayName.Length);
            var detachedPush = GitRemoteOps.Push(a, CancellationToken.None, null);
            Assert.False(detachedPush.Success);

            // Invalid names are rejected before running checkout.
            var bad = GitRemoteOps.CreateBranch(a, "bad..name", null, CancellationToken.None);
            Assert.Equal(GitErrorKind.InvalidBranchName, bad.Error);
        }

        [Fact]
        public void GitProcess_CancellationKillsTheProcess()
        {
            if (Path.DirectorySeparatorChar == '\\') return;
            using var t = new TempGitRepos();
            string a = t.Init("a");
            // A slow pre-commit hook stands in for a hanging network operation.
            string hook = Path.Combine(t.Root, "hooks", "pre-commit");
            File.WriteAllText(hook, "#!/bin/sh\nsleep 30\n");
            System.Diagnostics.Process.Start("chmod", "+x " + hook).WaitForExit();
            TempGitRepos.Write(a, "f.txt", "x\n");
            TempGitRepos.Git(a, "add", "-A");
            var cts = new CancellationTokenSource();
            cts.CancelAfter(300);
            var started = DateTime.UtcNow;
            var r = GitProcess.Run(a, new[] { "commit", "-m", "slow" }, 60000, cts.Token, null, null);
            Assert.True(r.Cancelled);
            Assert.False(r.Success);
            Assert.True((DateTime.UtcNow - started).TotalSeconds < 15);
            Assert.Equal(GitErrorKind.Cancelled, GitErrorClassifier.Classify(r));
            var timed = GitProcess.Run(a, new[] { "commit", "-m", "slow" }, 300, CancellationToken.None, null, null);
            Assert.True(timed.TimedOut);
        }

        [Fact]
        public void CommitHistory_UpDownNavigation()
        {
            var list = new List<string>();
            CommitMessageHistory.Add(list, "first");
            CommitMessageHistory.Add(list, "second");
            CommitMessageHistory.Add(list, "first"); // moves to the top, no duplicate
            Assert.Equal(new[] { "first", "second" }, list);
            var h = new CommitMessageHistory(list);
            Assert.Equal("first", h.Previous("draft text"));
            Assert.Equal("second", h.Previous("ignored"));
            Assert.Null(h.Previous("x"));
            Assert.Equal("first", h.Next());
            Assert.Equal("draft text", h.Next());
            Assert.Null(h.Next());
            for (int i = 0; i < 40; i++) CommitMessageHistory.Add(list, "m" + i);
            Assert.Equal(CommitMessageHistory.MaxEntries, list.Count);
        }

        [Fact]
        public void RealRepo_CommitAmendKeepsSingleCommit()
        {
            using var t = new TempGitRepos();
            string a = t.Init("a");
            TempGitRepos.Write(a, "f.txt", "1\n");
            var c1 = GitRemoteOps.Commit(a, "first message", false, true, CancellationToken.None);
            Assert.True(c1.Success, c1.Details);
            TempGitRepos.Write(a, "f.txt", "2\n");
            var c2 = GitRemoteOps.Commit(a, "", true, true, CancellationToken.None);
            Assert.True(c2.Success, c2.Details);
            Assert.Equal("1", TempGitRepos.Git(a, "rev-list", "--count", "HEAD").Trim());
            Assert.Equal("first message", GitRemoteOps.GetLastCommitMessage(a));
            var c3 = GitRemoteOps.Commit(a, "reworded", true, false, CancellationToken.None);
            Assert.True(c3.Success);
            Assert.Equal("reworded", GitRemoteOps.GetLastCommitMessage(a));
            Assert.False(GitRemoteOps.Commit(a, "", false, false, CancellationToken.None).Success);
        }

        [Fact]
        public void ScmGrouping_MatchesVsCodeGroups()
        {
            Assert.Equal(new[] { ScmGroup.Merge }, ScmGrouping.GroupsFor('U', 'U'));
            Assert.Equal(new[] { ScmGroup.Merge }, ScmGrouping.GroupsFor('A', 'A'));
            Assert.Equal(new[] { ScmGroup.Merge }, ScmGrouping.GroupsFor('D', 'U'));
            Assert.Equal(new[] { ScmGroup.Untracked }, ScmGrouping.GroupsFor('?', '?'));
            Assert.Equal(new[] { ScmGroup.Staged, ScmGroup.Changes }, ScmGrouping.GroupsFor('M', 'M'));
            Assert.Equal(new[] { ScmGroup.Staged }, ScmGrouping.GroupsFor('A', ' '));
            Assert.Equal(new[] { ScmGroup.Changes }, ScmGrouping.GroupsFor(' ', 'D'));
            Assert.Equal('U', ScmGrouping.BadgeFor(ScmGroup.Untracked, '?', '?'));
            Assert.Equal('A', ScmGrouping.BadgeFor(ScmGroup.Staged, 'A', 'M'));
            Assert.Equal('M', ScmGrouping.BadgeFor(ScmGroup.Changes, 'A', 'M'));
            Assert.True(ScmGrouping.TryParsePorcelainLine("R  old.txt -> new name.txt", out char x, out char y, out string p));
            Assert.Equal('R', x);
            Assert.Equal("new name.txt", p);
        }
    }

    /// <summary>Remote URL classification for the network ledger (pure functions).</summary>
    public class GitNetworkLedgerTests
    {
        [Theory]
        [InlineData("/home/me/remote.git", true)]
        [InlineData("../sibling.git", true)]
        [InlineData("file:///srv/x.git", true)]
        [InlineData("C:\\repos\\x.git", true)]
        [InlineData("https://github.com/a/b.git", false)]
        [InlineData("git@github.com:a/b.git", false)]
        [InlineData("ssh://git@host:22/a.git", false)]
        public void LocalRemoteDetection(string url, bool local)
        {
            Assert.Equal(local, GitRemoteOps.IsLocalRemoteUrl(url));
        }

        [Fact]
        public void LedgerUrl_DropsCredentials()
        {
            Assert.Equal("https://github.com/a/b.git", GitRemoteOps.LedgerUrl("https://user:secret-token@github.com/a/b.git"));
            Assert.Equal("ssh://git@github.com/a/b.git", GitRemoteOps.LedgerUrl("git@github.com:a/b.git"));
        }
    }

    public class GitConflictTests
    {
        private const string TwoWay =
            "line 1\n" +
            "<<<<<<< HEAD\n" +
            "ours a\n" +
            "ours b\n" +
            "=======\n" +
            "theirs a\n" +
            ">>>>>>> feature\n" +
            "middle\n" +
            "<<<<<<< HEAD\n" +
            "x\n" +
            "=======\n" +
            ">>>>>>> feature\n" +
            "end\n";

        [Fact]
        public void FindBlocks_ParsesTwoWayConflicts()
        {
            var blocks = GitConflictParser.FindBlocks(TwoWay);
            Assert.Equal(2, blocks.Count);
            var b = blocks[0];
            Assert.Equal(1, b.StartLine);
            Assert.Equal(4, b.SeparatorLine);
            Assert.Equal(6, b.EndLine);
            Assert.Equal(-1, b.BaseLine);
            Assert.Equal("HEAD", b.CurrentLabel);
            Assert.Equal("feature", b.IncomingLabel);
            var lines = LineDiff.SplitLines(TwoWay);
            Assert.Equal(new[] { "ours a", "ours b" }, GitConflictParser.CurrentLines(lines, b));
            Assert.Equal(new[] { "theirs a" }, GitConflictParser.IncomingLines(lines, b));
            Assert.Empty(GitConflictParser.IncomingLines(lines, blocks[1]));
            Assert.Equal(2, GitConflictParser.CountConflicts(TwoWay));
        }

        [Fact]
        public void FindBlocks_Diff3AndIgnoresLookalikes()
        {
            string text = "<<<<<<<< not a marker (8 chars)\n======= trailing text is not a separator\n" +
                          "<<<<<<< ours\nA\n||||||| base\nB\n=======\nC\n>>>>>>> theirs\n";
            var blocks = GitConflictParser.FindBlocks(text);
            Assert.Single(blocks);
            Assert.Equal(2, blocks[0].StartLine);
            Assert.Equal(4, blocks[0].BaseLine);
            Assert.Equal("base", blocks[0].BaseLabel);
            var lines = LineDiff.SplitLines(text);
            Assert.Equal(new[] { "A" }, GitConflictParser.CurrentLines(lines, blocks[0]));
            Assert.Equal(new[] { "B" }, GitConflictParser.BaseLines(lines, blocks[0]));
            Assert.Equal(new[] { "C" }, GitConflictParser.IncomingLines(lines, blocks[0]));
            var kinds = GitConflictParser.LineKinds(blocks);
            Assert.Equal(ConflictLineKind.CurrentHeader, kinds[2]);
            Assert.Equal(ConflictLineKind.Current, kinds[3]);
            Assert.Equal(ConflictLineKind.BaseHeader, kinds[4]);
            Assert.Equal(ConflictLineKind.Base, kinds[5]);
            Assert.Equal(ConflictLineKind.Separator, kinds[6]);
            Assert.Equal(ConflictLineKind.Incoming, kinds[7]);
            Assert.Equal(ConflictLineKind.IncomingHeader, kinds[8]);
            Assert.False(kinds.ContainsKey(0));
            // Unterminated conflict is not reported.
            Assert.Empty(GitConflictParser.FindBlocks("<<<<<<< a\nx\n=======\ny\n"));
        }

        [Theory]
        [InlineData(ConflictResolution.Current, "line 1\nours a\nours b\nmiddle\n")]
        [InlineData(ConflictResolution.Incoming, "line 1\ntheirs a\nmiddle\n")]
        [InlineData(ConflictResolution.Both, "line 1\nours a\nours b\ntheirs a\nmiddle\n")]
        public void Resolve_FirstBlock(ConflictResolution how, string expectedPrefix)
        {
            string r = GitConflictParser.Resolve(TwoWay, 0, how);
            Assert.StartsWith(expectedPrefix, r);
            Assert.Single(GitConflictParser.FindBlocks(r));
            Assert.EndsWith("end\n", r);
        }

        [Fact]
        public void Resolve_AllBlocks_PreservesCrlf()
        {
            string crlf = TwoWay.Replace("\n", "\r\n");
            string r = GitConflictParser.Resolve(crlf, -1, ConflictResolution.Incoming);
            Assert.Equal("line 1\r\ntheirs a\r\nmiddle\r\nend\r\n", r);
        }

        [Fact]
        public void Navigation_NextPreviousWrap()
        {
            var blocks = GitConflictParser.FindBlocks(TwoWay);
            Assert.Equal(1, GitConflictParser.Next(blocks, 0).StartLine);
            Assert.Equal(8, GitConflictParser.Next(blocks, 1).StartLine);
            Assert.Equal(1, GitConflictParser.Next(blocks, 9).StartLine); // wraps
            Assert.Equal(1, GitConflictParser.Previous(blocks, 8).StartLine);
            Assert.Equal(8, GitConflictParser.Previous(blocks, 0).StartLine); // wraps
            Assert.Same(blocks[1], GitConflictParser.BlockAt(blocks, 10));
            Assert.Null(GitConflictParser.BlockAt(blocks, 7));
            Assert.Equal(ConflictResolution.Incoming, GitConflictParser.ResolutionForSegment(1));
            Assert.Null(GitConflictParser.ResolutionForSegment(3));
        }

        [Fact]
        public void RealMergeConflict_ResolvedAndMarkedByStaging()
        {
            using var t = new TempGitRepos();
            string a = t.Init("a");
            TempGitRepos.Write(a, "f.txt", "one\ntwo\nthree\n");
            TempGitRepos.Commit(a, "base");
            TempGitRepos.Git(a, "checkout", "-q", "-b", "feature");
            TempGitRepos.Write(a, "f.txt", "one\nTWO-feature\nthree\n");
            TempGitRepos.Commit(a, "feature");
            TempGitRepos.Git(a, "checkout", "-q", "main");
            TempGitRepos.Write(a, "f.txt", "one\nTWO-main\nthree\n");
            TempGitRepos.Commit(a, "main");
            var merge = GitProcess.Run(a, new[] { "merge", "feature" }, 30000, CancellationToken.None, null, null);
            Assert.False(merge.Success);
            Assert.Equal(GitErrorKind.Conflicts, GitErrorClassifier.Classify(merge));
            Assert.Equal("Merging", GitBranchStatus.DetectOperation(a));
            string status = TempGitRepos.Git(a, "status", "--porcelain");
            Assert.True(ScmGrouping.TryParsePorcelainLine(status.Split('\n')[0], out char x, out char y, out string _));
            Assert.Equal(new[] { ScmGroup.Merge }, ScmGrouping.GroupsFor(x, y));

            string path = Path.Combine(a, "f.txt");
            string text = File.ReadAllText(path);
            var blocks = GitConflictParser.FindBlocks(text);
            Assert.Single(blocks);
            Assert.Equal("HEAD", blocks[0].CurrentLabel);
            Assert.Equal("feature", blocks[0].IncomingLabel);
            File.WriteAllText(path, GitConflictParser.Resolve(text, 0, ConflictResolution.Incoming));
            Assert.Equal("one\nTWO-feature\nthree\n", File.ReadAllText(path));
            TempGitRepos.Git(a, "add", "f.txt");
            string after = TempGitRepos.Git(a, "status", "--porcelain");
            Assert.StartsWith("M ", after);
        }
    }

    public class GitHunkStagingTests
    {
        private const string Base = "a\nb\nc\nd\ne\nf\ng\nh\ni\nj\nk\nl\n";

        [Fact]
        public void ComputeHunks_FindsSeparateChanges()
        {
            var editor = new List<string> { "a", "B!", "c", "d", "e", "f", "g", "h", "i", "j", "k", "l", "m", "" };
            var hunks = GitHunkDiff.ComputeHunks(Base, editor, true);
            Assert.Equal(2, hunks.Count);
            Assert.Equal(1, hunks[0].OldStart);
            Assert.Equal(1, hunks[0].OldCount);
            Assert.Equal(1, hunks[0].NewStart);
            Assert.Equal(1, hunks[0].NewCount);
            Assert.Equal(new[] { "b" }, hunks[0].OldTexts());
            Assert.Equal(new[] { "B!" }, hunks[0].NewTexts());
            Assert.True(hunks[1].IsPureAddition);
            Assert.Equal(12, hunks[1].NewStart);
            Assert.Same(hunks[0], GitHunkDiff.HunkAtLine(hunks, 1));
            Assert.Same(hunks[1], GitHunkDiff.HunkAtLine(hunks, 12));
            Assert.Null(GitHunkDiff.HunkAtLine(hunks, 5));

            var deletion = GitHunkDiff.ComputeHunks(Base, new List<string> { "a", "b", "c", "e", "f", "g", "h", "i", "j", "k", "l", "" }, true);
            Assert.Single(deletion);
            Assert.True(deletion[0].IsPureDeletion);
            Assert.True(deletion[0].TouchesNewLine(3));
            Assert.True(deletion[0].TouchesNewLine(2));
        }

        [Fact]
        public void StageOneHunk_WithGitApplyCached_LeavesOtherChangesUnstaged()
        {
            using var t = new TempGitRepos();
            string a = t.Init("a");
            TempGitRepos.Write(a, "f.txt", Base);
            TempGitRepos.Commit(a, "base");
            var editor = new List<string> { "a", "B!", "c", "d", "e", "f", "g", "h", "i", "j", "k", "L!", "" };
            File.WriteAllText(Path.Combine(a, "f.txt"), string.Join("\n", editor));

            // Stage only the second change (line 11, 0-based).
            var res = GitHunkStaging.StageFromEditor(a, "f.txt", editor, true, 11, 11, true);
            Assert.True(res.Success, res.Message + "\n" + res.Details);
            string index = TempGitRepos.Git(a, "show", ":f.txt");
            Assert.Equal(Base.Replace("l\n", "L!\n"), index);
            string unstaged = TempGitRepos.Git(a, "diff");
            Assert.Contains("+B!", unstaged);
            Assert.DoesNotContain("L!", unstaged);

            // Stage the first one too: index now equals the editor.
            Assert.True(GitHunkStaging.StageFromEditor(a, "f.txt", editor, true, 1, 1, true).Success);
            Assert.Equal(string.Join("\n", editor), TempGitRepos.Git(a, "show", ":f.txt"));
            Assert.Equal("", TempGitRepos.Git(a, "diff").Trim());
        }

        [Fact]
        public void StageSelectedLines_PartialHunk()
        {
            using var t = new TempGitRepos();
            string a = t.Init("a");
            TempGitRepos.Write(a, "f.txt", "one\ntwo\nthree\n");
            TempGitRepos.Commit(a, "base");
            // Three added lines after "two"; stage only the middle one (new line index 3).
            var editor = new List<string> { "one", "two", "x1", "x2", "x3", "three", "" };
            var r = GitHunkStaging.StageFromEditor(a, "f.txt", editor, true, 3, 3, false);
            Assert.True(r.Success, r.Message + r.Details);
            Assert.Equal("one\ntwo\nx2\nthree\n", TempGitRepos.Git(a, "show", ":f.txt"));

            // Modified line selected: removal and addition are staged together (VS Code "Stage Selected Ranges").
            TempGitRepos.Git(a, "reset", "-q");
            string[] baseLines = GitHunkDiff.SplitForDiff("one\ntwo\nthree\n");
            var editor2 = new List<string> { "one", "TWO", "x", "three", "" };
            var hunks = GitHunkDiff.ComputeHunks("one\ntwo\nthree\n", editor2, true);
            Assert.Single(hunks);
            Assert.Equal(new[] { "one", "TWO", "three" }, GitHunkDiff.ApplyToBase(baseLines, GitHunkDiff.SelectLines(hunks, 1, 1)));
            // Only the extra added line selected: the removed line stays (becomes context).
            Assert.Equal(new[] { "one", "two", "x", "three" }, GitHunkDiff.ApplyToBase(baseLines, GitHunkDiff.SelectLines(hunks, 2, 2)));
            Assert.Empty(GitHunkDiff.SelectLines(hunks, 3, 3));
            var r2 = GitHunkStaging.StageFromEditor(a, "f.txt", editor2, true, 2, 2, false);
            Assert.True(r2.Success, r2.Message + r2.Details);
            Assert.Equal("one\ntwo\nx\nthree\n", TempGitRepos.Git(a, "show", ":f.txt"));
        }

        [Fact]
        public void Patch_NoFinalNewline_AndCrlfIndex()
        {
            using var t = new TempGitRepos();
            string a = t.Init("a");
            File.WriteAllText(Path.Combine(a, "n.txt"), "x\ny");          // no final newline
            File.WriteAllText(Path.Combine(a, "w.txt"), "p\r\nq\r\nr\r\n"); // CRLF stored in the index
            TempGitRepos.Commit(a, "base");

            var ed = new List<string> { "x", "y", "z" };
            var r1 = GitHunkStaging.StageFromEditor(a, "n.txt", ed, false, 2, 2, true);
            Assert.True(r1.Success, r1.Message + r1.Details);
            Assert.Equal("x\ny\nz", TempGitRepos.Git(a, "show", ":n.txt"));

            var ed2 = new List<string> { "x", "y", "z", "" };
            Assert.True(GitHunkStaging.StageFromEditor(a, "n.txt", ed2, true, 2, 2, true).Success);
            Assert.Equal("x\ny\nz\n", TempGitRepos.Git(a, "show", ":n.txt"));

            var w = new List<string> { "p", "Q", "r", "" };
            var r2 = GitHunkStaging.StageFromEditor(a, "w.txt", w, true, 1, 1, true);
            Assert.True(r2.Success, r2.Message + r2.Details);
            Assert.Equal("p\r\nQ\r\nr\r\n", TempGitRepos.Git(a, "show", ":w.txt"));
        }

        [Fact]
        public void UnstageOneStagedHunk_WithReverseApply()
        {
            using var t = new TempGitRepos();
            string a = t.Init("a");
            TempGitRepos.Write(a, "f.txt", Base);
            TempGitRepos.Commit(a, "base");
            // Editor: one extra line inserted at the top shifts the line numbers vs the index.
            var staged = new List<string> { "a", "B!", "c", "d", "e", "f", "g", "h", "i", "j", "k", "L!", "" };
            File.WriteAllText(Path.Combine(a, "f.txt"), string.Join("\n", staged));
            TempGitRepos.Git(a, "add", "f.txt");
            var editor = new List<string> { "top", "a", "B!", "c", "d", "e", "f", "g", "h", "i", "j", "k", "L!", "" };
            string head = GitHunkStaging.ReadHeadContent(a, "f.txt");
            string index = GitHunkStaging.ReadIndexContent(a, "f.txt", out bool bin);
            Assert.Null(GitHunkStaging.FindStagedHunkAtEditorLine(head, index, editor, true, 0)); // "top" is not staged
            var sh = GitHunkStaging.FindStagedHunkAtEditorLine(head, index, editor, true, 12);
            Assert.NotNull(sh);
            Assert.Equal(new[] { "L!" }, sh.NewTexts());
            var r = GitHunkStaging.UnstageHunk(a, "f.txt", head, index, sh);
            Assert.True(r.Success, r.Message + r.Details);
            Assert.Equal(Base.Replace("b\n", "B!\n"), TempGitRepos.Git(a, "show", ":f.txt"));
        }

        [Fact]
        public void Patch_MergesAdjacentHunks_AndUntrackedFallsBackToAdd()
        {
            string[] old = GitHunkDiff.SplitForDiff(Base);
            var editor = new List<string> { "A", "b", "C", "d", "e", "f", "g", "h", "i", "j", "k", "l", "" };
            var hunks = GitHunkDiff.ComputeHunks(Base, editor, true);
            Assert.Equal(2, hunks.Count);
            string patch = GitHunkDiff.BuildPatch("f.txt", old, hunks, 3, false);
            Assert.Equal(1, patch.Split('\n').Count(l => l.StartsWith("@@")));
            Assert.Contains("@@ -1,6 +1,6 @@", patch);

            using var t = new TempGitRepos();
            string a = t.Init("a");
            TempGitRepos.Write(a, "seed.txt", "s\n");
            TempGitRepos.Commit(a, "seed");
            TempGitRepos.Write(a, "new.txt", "brand new\n");
            var r = GitHunkStaging.StageFromEditor(a, "new.txt", new List<string> { "brand new", "" }, true, 0, 0, true);
            Assert.True(r.Success);
            Assert.StartsWith("A ", TempGitRepos.Git(a, "status", "--porcelain"));
        }
    }

    public class TaskSystemTests
    {
        private const string TasksJson = @"{
  // JSONC with comments and trailing commas
  ""version"": ""2.0.0"",
  ""tasks"": [
    {
      ""label"": ""build app"",
      ""type"": ""shell"",
      ""command"": ""make"",
      ""args"": [""-C"", ""${workspaceFolder}/my dir"", ""${fileBasenameNoExtension}""],
      ""group"": { ""kind"": ""build"", ""isDefault"": true },
      ""problemMatcher"": [""$gcc""],
      ""options"": { ""cwd"": ""${workspaceFolder}/src"", ""env"": { ""MODE"": ""${env:CODELESS_TASK_TEST}"" } },
      ""windows"": { ""command"": ""nmake"" },
      ""osx"": { ""args"": [""osx-only""] },
    },
    { ""label"": ""lint"", ""command"": ""eslint ."", ""group"": ""test"", ""problemMatcher"": ""$eslint-stylish"" },
    { ""type"": ""npm"", ""script"": ""watch"", ""path"": ""web"" },
    { ""label"": ""custom"", ""type"": ""process"", ""command"": ""/usr/bin/my tool"",
      ""problemMatcher"": { ""owner"": ""x"", ""fileLocation"": [""relative"", ""${workspaceFolder}""],
        ""pattern"": { ""regexp"": ""^(.*)@(\\d+): (.*)$"", ""file"": 1, ""line"": 2, ""message"": 3 } } },
  ],
}";

        [Fact]
        public void ParseTasksJson_JsoncPlatformOverridesAndGroups()
        {
            var tasks = TaskConfiguration.ParseTasksJson(TasksJson, "linux", out string err);
            Assert.Null(err);
            Assert.Equal(4, tasks.Count);
            var build = tasks[0];
            Assert.Equal("build app", build.Label);
            Assert.Equal("make", build.Command);
            Assert.Equal("build", build.Group);
            Assert.True(build.IsDefault);
            Assert.Equal(3, build.Args.Count);
            Assert.Equal("${workspaceFolder}/src", build.Cwd);
            Assert.Equal("$gcc", build.ProblemMatchers[0]);
            Assert.Equal("test", tasks[1].Group);
            Assert.Equal("npm", tasks[2].Command);
            Assert.Equal(new[] { "run", "watch" }, tasks[2].Args);
            Assert.Equal("npm: watch", tasks[2].Label);
            Assert.Equal("process", tasks[3].Type);

            var win = TaskConfiguration.ParseTasksJson(TasksJson, "windows", out err);
            Assert.Equal("nmake", win[0].Command);
            var osx = TaskConfiguration.ParseTasksJson(TasksJson, "osx", out err);
            Assert.Equal(new[] { "osx-only" }, osx[0].Args);

            Assert.Same(build, TaskConfiguration.FindDefault(tasks, "build"));
            Assert.Same(tasks[1], TaskConfiguration.FindDefault(tasks, "test"));
            Assert.Null(TaskConfiguration.FindDefault(new List<TaskDefinition>(), "build"));

            var bad = TaskConfiguration.ParseTasksJson("{ \"tasks\": [ { ", "linux", out err);
            Assert.Empty(bad);
            Assert.NotNull(err);
        }

        [Fact]
        public void Substitution_AndCommandLines()
        {
            Environment.SetEnvironmentVariable("CODELESS_TASK_TEST", "fast");
            var tasks = TaskConfiguration.ParseTasksJson(TasksJson, "linux", out _);
            var ctx = new DebugVariableContext { WorkspaceFolder = "/ws", File = "/ws/src/main.c" };
            string cmd = TaskConfiguration.BuildCommandLine(tasks[0], ctx, false);
            Assert.Equal("make -C '/ws/my dir' main", cmd);
            Assert.Equal(Path.GetFullPath("/ws/src"), TaskConfiguration.ResolveCwd(tasks[0], ctx));
            Assert.Equal("fast", TaskConfiguration.ResolveEnv(tasks[0], ctx)["MODE"]);
            Assert.Equal("'/usr/bin/my tool'", TaskConfiguration.BuildCommandLine(tasks[3], ctx, false));
            Assert.Equal("\"/usr/bin/my tool\"", TaskConfiguration.BuildCommandLine(tasks[3], ctx, true));
            Assert.Equal("eslint .", TaskConfiguration.BuildCommandLine(tasks[1], ctx, false));

            string term = TaskConfiguration.BuildTerminalCommandLine("echo 'hi'", new Dictionary<string, string> { { "A", "b c" } }, false, "/bin/zsh");
            Assert.Equal("/bin/zsh -l -c 'export A='\\''b c'\\''; echo '\\''hi'\\'''", term);
            Assert.Equal("cmd.exe /d /s /c \"set \"A=1\" && dotnet build\"", TaskConfiguration.BuildTerminalCommandLine("dotnet build", new Dictionary<string, string> { { "A", "1" } }, true, null));
        }

        [Fact]
        public void AutoDetect_NpmMakeDotnetCargoGoPytest()
        {
            string root = Path.Combine(Path.GetTempPath(), "codeless-tasks-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(root);
            try
            {
                File.WriteAllText(Path.Combine(root, "package.json"), "{ \"scripts\": { \"build\": \"tsc -p .\", \"test\": \"jest\", \"lint\": \"eslint src\" } }");
                File.WriteAllText(Path.Combine(root, "Makefile"), "CC=gcc\nall: app\n\napp: main.o\n\t$(CC) -o app main.o\n%.o: %.c\n\t$(CC) -c $<\nclean:\n\trm -f app\ntest:\n\t./app\n.PHONY: all clean test\n");
                File.WriteAllText(Path.Combine(root, "App.sln"), "");
                File.WriteAllText(Path.Combine(root, "Cargo.toml"), "[package]\nname=\"x\"\n");
                File.WriteAllText(Path.Combine(root, "go.mod"), "module x\n");
                Directory.CreateDirectory(Path.Combine(root, "tests"));
                File.WriteAllText(Path.Combine(root, "tests", "test_math.py"), "def test_x(): assert 1\n");

                var tasks = TaskAutoDetector.Detect(root);
                Func<string, string, TaskDefinition> find = (src, label) => tasks.Single(x => x.Source == src && x.Label == label);
                Assert.Equal("build", find("npm", "build").Group);
                Assert.Equal("$tsc", find("npm", "build").ProblemMatchers[0]);
                Assert.Equal("test", find("npm", "test").Group);
                Assert.Equal("$eslint-stylish", find("npm", "lint").ProblemMatchers[0]);
                Assert.Equal("npm run build", find("npm", "build").Command);
                Assert.Equal("build", find("make", "all").Group);
                Assert.Equal("make app", find("make", "app").Command);
                Assert.DoesNotContain(tasks, x => x.Source == "make" && x.Label.Contains("%"));
                Assert.Equal("test", find("make", "test").Group);
                Assert.StartsWith("dotnet build App.sln", find("dotnet", "build").Command);
                Assert.Equal("$msCompile", find("dotnet", "build").ProblemMatchers[0]);
                Assert.Equal("cargo build", find("cargo", "build").Command);
                Assert.Equal("go test ./...", find("go", "test").Command);
                Assert.Contains("-m pytest", find("python", "pytest").Command);
                Assert.Equal("npm: build", find("npm", "build").DisplayLabel);

                // tasks.json entries come first and shadow detected tasks with the same label.
                Directory.CreateDirectory(Path.Combine(root, ".vscode"));
                File.WriteAllText(Path.Combine(root, ".vscode", "tasks.json"), "{ \"tasks\": [ { \"label\": \"npm: build\", \"command\": \"echo custom\", \"group\": \"build\" } ] }");
                var all = TaskConfiguration.LoadAll(root, out string err);
                Assert.Null(err);
                Assert.Equal("echo custom", all[0].Command);
                Assert.DoesNotContain(all, x => x.Source == "npm" && x.Label == "build");

                // Yarn projects use yarn.
                File.WriteAllText(Path.Combine(root, "yarn.lock"), "");
                Assert.Equal("yarn build", TaskAutoDetector.Detect(root).Single(x => x.Source == "npm" && x.Label == "build").Command);
            }
            finally { try { Directory.Delete(root, true); } catch { } }
        }
    }

    public class ProblemMatcherTests
    {
        private static List<DiagnosticItem> Run(string matcher, string ws, params string[] lines)
        {
            var engine = new ProblemMatcherEngine(new[] { BuiltInProblemMatchers.Get(matcher) }, ws, ws);
            engine.FeedLines(lines);
            return engine.Problems;
        }

        private static string Ws => Path.Combine(Path.GetTempPath(), "pm-ws");

        [Fact]
        public void Tsc_BothOutputStyles()
        {
            var p = Run("$tsc", Ws, "src/app.ts(12,5): error TS2322: Type 'string' is not assignable to type 'number'.",
                                    "src/util.ts:3:10 - warning TS6133: 'x' is declared but its value is never read.",
                                    "Found 2 errors.");
            Assert.Equal(2, p.Count);
            Assert.Equal(Path.GetFullPath(Path.Combine(Ws, "src/app.ts")), p[0].FilePath);
            Assert.Equal(12, p[0].Line);
            Assert.Equal(5, p[0].Column);
            Assert.Equal("Error", p[0].Severity);
            Assert.Equal("2322", p[0].Code);
            Assert.StartsWith("Type 'string'", p[0].Message);
            Assert.Equal("Warning", p[1].Severity);
            Assert.Equal("ts", p[0].Source);
            Assert.True(p[0].IsTaskProblem);
        }

        [Fact]
        public void Gcc_ErrorsWarningsAndFatal()
        {
            var p = Run("$gcc", Ws, "main.c:10:5: error: expected ';' before 'return'",
                                    "/abs/lib.c:3:1: warning: unused function 'f' [-Wunused-function]",
                                    "main.c:1:10: fatal error: missing.h: No such file or directory",
                                    "make: *** [Makefile:2: app] Error 1");
            Assert.Equal(3, p.Count);
            Assert.Equal(10, p[0].Line);
            Assert.Equal("expected ';' before 'return'", p[0].Message);
            Assert.EndsWith("lib.c", p[1].FilePath);
            Assert.Equal("Warning", p[1].Severity);
            Assert.Equal("Error", p[2].Severity);
        }

        [Fact]
        public void MsCompile_DotnetBuildOutput()
        {
            string file = Path.Combine(Ws, "Program.cs");
            var p = Run("$msCompile", Ws, file + "(10,17): error CS1002: ; expected [" + Path.Combine(Ws, "App.csproj") + "]",
                                          "  " + file + "(3,7): warning CS0168: The variable 'e' is declared but never used [" + Path.Combine(Ws, "App.csproj") + "]",
                                          "Build FAILED.");
            Assert.Equal(2, p.Count);
            Assert.Equal(file, p[0].FilePath);
            Assert.Equal(10, p[0].Line);
            Assert.Equal(17, p[0].Column);
            Assert.Equal("CS1002", p[0].Code);
            Assert.Equal("; expected", p[0].Message);
            Assert.Equal("Warning", p[1].Severity);
            Assert.Equal("dotnet", p[0].Source);
        }

        [Fact]
        public void EslintStylish_MultiLineLoop()
        {
            string file = Path.Combine(Ws, "src", "a.js");
            var p = Run("$eslint-stylish", Ws, "", file,
                "   1:10  error    'x' is defined but never used  no-unused-vars",
                "  12:1   warning  Unexpected console statement   no-console",
                "", "✖ 2 problems (1 error, 1 warning)");
            Assert.Equal(2, p.Count);
            Assert.Equal(file, p[0].FilePath);
            Assert.Equal(1, p[0].Line);
            Assert.Equal(10, p[0].Column);
            Assert.Equal("'x' is defined but never used", p[0].Message);
            Assert.Equal("no-unused-vars", p[0].Code);
            Assert.Equal(12, p[1].Line);
            Assert.Equal("Warning", p[1].Severity);
        }

        [Fact]
        public void Go_BuildAndVetOutput()
        {
            var p = Run("$go", Ws, "# example.com/x", "./main.go:5:2: undefined: foo", "vet: pkg/util.go:7: unreachable code", "FAIL");
            Assert.Equal(2, p.Count);
            Assert.Equal(Path.GetFullPath(Path.Combine(Ws, "main.go")), p[0].FilePath);
            Assert.Equal(5, p[0].Line);
            Assert.Equal(2, p[0].Column);
            Assert.Equal("undefined: foo", p[0].Message);
            Assert.Equal(7, p[1].Line);
        }

        [Fact]
        public void Rustc_TwoLinePattern()
        {
            var p = Run("$rustc", Ws, "   Compiling x v0.1.0", "error[E0425]: cannot find value `y` in this scope",
                " --> src/main.rs:3:13", "  |", "warning: unused variable: `z`", "  --> src/lib.rs:8:9",
                "error: aborting due to previous error", "For more information about this error, try `rustc --explain E0425`.");
            Assert.Equal(2, p.Count);
            Assert.Equal("E0425", p[0].Code);
            Assert.Equal(Path.GetFullPath(Path.Combine(Ws, "src/main.rs")), p[0].FilePath);
            Assert.Equal(3, p[0].Line);
            Assert.Equal(13, p[0].Column);
            Assert.Equal("cannot find value `y` in this scope", p[0].Message);
            Assert.Equal("Warning", p[1].Severity);
            Assert.Equal(8, p[1].Line);
        }

        [Fact]
        public void Python_TracebackAndPytest()
        {
            string f = Path.Combine(Ws, "app.py");
            var p = Run("$python", Ws, "Traceback (most recent call last):",
                "  File \"" + Path.Combine(Ws, "main.py") + "\", line 3, in <module>", "    run()",
                "  File \"" + f + "\", line 12, in run", "    1 / 0",
                "ZeroDivisionError: division by zero",
                "tests/test_x.py:7: AssertionError",
                "  File \"" + f + "\", line 2", "    def (:", "        ^", "SyntaxError: invalid syntax");
            Assert.Equal(3, p.Count);
            Assert.Equal(f, p[0].FilePath);
            Assert.Equal(12, p[0].Line);
            Assert.Equal("ZeroDivisionError: division by zero", p[0].Message);
            Assert.Equal(Path.GetFullPath(Path.Combine(Ws, "tests/test_x.py")), p[1].FilePath);
            Assert.Equal(7, p[1].Line);
            Assert.Equal("SyntaxError: invalid syntax", p[2].Message);
            Assert.Equal(2, p[2].Line);
        }

        [Fact]
        public void CustomMatcherFromTasksJson_AndUnknownNamesIgnored()
        {
            var tasks = TaskConfiguration.ParseTasksJson(@"{ ""tasks"": [ { ""label"": ""c"", ""command"": ""x"",
               ""problemMatcher"": [ ""$doesNotExist"", { ""owner"": ""mine"", ""severity"": ""warning"", ""fileLocation"": [""relative"", ""${workspaceFolder}/sub""],
               ""pattern"": { ""regexp"": ""^(.*)@(\\d+): (.*)$"", ""file"": 1, ""line"": 2, ""message"": 3 } },
               { ""base"": ""$tsc"", ""owner"": ""tsc-alt"" } ] } ] }", "linux", out string err);
            Assert.Null(err);
            var defs = BuiltInProblemMatchers.Resolve(tasks[0].ProblemMatchers);
            Assert.Equal(2, defs.Count);
            var engine = new ProblemMatcherEngine(defs, Ws, Ws);
            engine.FeedLine("lib/z.txt@42: something odd");
            engine.FeedLine("a.ts(1,2): error TS1: bad");
            Assert.Equal(2, engine.Problems.Count);
            Assert.Equal(Path.GetFullPath(Path.Combine(Ws, "sub", "lib/z.txt")), engine.Problems[0].FilePath);
            Assert.Equal("Warning", engine.Problems[0].Severity);
            Assert.Equal("mine", engine.Problems[0].Source);
            Assert.Equal(42, engine.Problems[0].Line);
        }

        [Fact]
        public void LineSplitter_StripsAnsiAndJoinsChunks()
        {
            var s = new TaskOutputLineSplitter();
            var lines = new List<string>();
            s.LineReady += l => lines.Add(l);
            byte[] bytes = Encoding.UTF8.GetBytes("\x1b[1m\x1b[31mmain.c:1:2: error: bad é\x1b[0m\r\nprog\x1b]0;title\x07ress 50%\rprogress 100%\npart");
            // Split inside the multi-byte "é" and inside an escape sequence.
            int cut = Array.IndexOf(bytes, (byte)0xC3) + 1;
            s.FeedBytes(bytes.Take(cut).ToArray(), cut);
            s.FeedBytes(bytes.Skip(cut).ToArray(), bytes.Length - cut);
            s.Flush();
            Assert.Equal("main.c:1:2: error: bad é", lines[0]);
            Assert.Equal("progress 50%progress 100%", lines[1]);
            Assert.Equal("part", lines[2]);
        }

        [Fact]
        public void TaskExecution_PipeRun_PublishesAndClearsOnRerun()
        {
            if (Path.DirectorySeparatorChar == '\\') return;
            string ws = Path.Combine(Path.GetTempPath(), "codeless-taskrun-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(ws);
            try
            {
                File.WriteAllText(Path.Combine(ws, "main.c"), "int main() { return 0 }\n");
                var task = new TaskDefinition { Label = "cc", Command = "printf 'main.c:1:25: error: expected semicolon\\n'; exit 2" };
                task.ProblemMatchers.Add("$gcc");
                var ctx = new DebugVariableContext { WorkspaceFolder = ws };
                TaskProblemStore.ClearAll();

                for (int run = 0; run < 2; run++)
                {
                    var ex = TaskExecution.Prepare(task, ctx, false);
                    ex.MarkStarted();
                    Assert.Empty(TaskProblemStore.Get(Path.Combine(ws, "main.c")));
                    var done = new ManualResetEventSlim();
                    var proc = new TaskPipeProcess();
                    ex.Exited += _ => done.Set();
                    Assert.True(proc.Start(ex.CommandLine, ex.Cwd, ex.Env, false, ex.OnOutputText, ex.OnExited));
                    Assert.True(done.Wait(20000));
                    Assert.Equal(2, ex.ExitCode);
                    var probs = TaskProblemStore.Get(Path.Combine(ws, "main.c"));
                    Assert.Single(probs);
                    Assert.Equal(25, probs[0].Column);
                    Assert.Contains("expected semicolon", ex.Transcript);
                }

                // The buffer merge shows task problems next to built-in diagnostics.
                var buf = new EditorTabBuffer { FilePath = Path.Combine(ws, "main.c"), Content = "int main() { return 0 }\n" };
                buf.SyncLinesFromContent();
                buf.Diagnostics.Add(new DiagnosticItem { Line = 1, Message = "builtin" });
                LspDiagnosticsStore.MergeInto(buf);
                Assert.Equal(2, buf.Diagnostics.Count);
                LspDiagnosticsStore.MergeInto(buf);
                Assert.Equal(2, buf.Diagnostics.Count);
                TaskProblemStore.ClearOwner("cc");
                LspDiagnosticsStore.MergeInto(buf);
                Assert.Single(buf.Diagnostics);

                // Terminate stops a long-running task.
                var sleeper = TaskExecution.Prepare(new TaskDefinition { Label = "sleep", Command = "sleep 30" }, ctx, false);
                var finished = new ManualResetEventSlim();
                sleeper.Exited += _ => finished.Set();
                var sp = new TaskPipeProcess();
                Assert.True(sp.Start(sleeper.CommandLine, sleeper.Cwd, sleeper.Env, false, sleeper.OnOutputText, sleeper.OnExited));
                Thread.Sleep(200);
                sp.Terminate();
                Assert.True(finished.Wait(10000));
                Assert.NotEqual(0, sleeper.ExitCode);
            }
            finally { TaskProblemStore.ClearAll(); try { Directory.Delete(ws, true); } catch { } }
        }
    }

    public class SaveActionTests
    {
        private static SaveActionOptions Trim() => new SaveActionOptions { TrimTrailingWhitespace = true };

        [Fact]
        public void TrimTrailingWhitespace_Basic()
        {
            var r = SaveActions.Apply(new List<string> { "a  ", "b\t", "c", "   ", "" }, "x.txt", Trim());
            Assert.Equal(new[] { "a", "b", "c", "", "" }, r);
            Assert.Null(SaveActions.Apply(new List<string> { "a", "b" }, "x.txt", Trim()));
        }

        [Fact]
        public void TrimTrailingWhitespace_KeepsMarkdownHardBreaks()
        {
            var r = SaveActions.Apply(new List<string> { "line one   ", "line two ", "last  ", "", "x\t" }, "README.md", Trim());
            Assert.Equal(new[] { "line one  ", "line two", "last", "", "x" }, r);
        }

        [Fact]
        public void TrimTrailingWhitespace_SkipsMultilineStrings()
        {
            var py = new List<string> { "x = 1   ", "s = \"\"\"keep   ", "inside   ", "end\"\"\"  ", "# c '''   ", "y = 2  " };
            Assert.Equal(new[] { "x = 1", "s = \"\"\"keep   ", "inside   ", "end\"\"\"", "# c '''", "y = 2" }, SaveActions.Apply(py, "a.py", Trim()));

            var js = new List<string> { "const a = `x  ", "y  ", "`;  ", "// `  ", "const b = 'q';  " };
            Assert.Equal(new[] { "const a = `x  ", "y  ", "`;", "// `", "const b = 'q';" }, SaveActions.Apply(js, "a.ts", Trim()));

            var cs = new List<string> { "var s = @\"a  ", "b \"\" c  ", "\";  ", "var t = \"\"\"  ", "raw  ", "\"\"\";  " };
            Assert.Equal(new[] { "var s = @\"a  ", "b \"\" c  ", "\";", "var t = \"\"\"  ", "raw  ", "\"\"\";" }, SaveActions.Apply(cs, "a.cs", Trim()));

            var go = new List<string> { "s := `a  ", "b`  " };
            Assert.Equal(new[] { "s := `a  ", "b`" }, SaveActions.Apply(go, "a.go", Trim()));
        }

        [Fact]
        public void FinalNewlines_InsertAndTrim()
        {
            var o = new SaveActionOptions { InsertFinalNewline = true };
            Assert.Equal(new[] { "a", "" }, SaveActions.Apply(new List<string> { "a" }, "x", o));
            Assert.Null(SaveActions.Apply(new List<string> { "a", "" }, "x", o));
            var t = new SaveActionOptions { TrimFinalNewlines = true };
            Assert.Equal(new[] { "a", "" }, SaveActions.Apply(new List<string> { "a", "", "", "" }, "x", t));
            Assert.Equal("a\n", SaveActions.ApplyToText("a\n\n\n", "x", t));
        }

        [Fact]
        public void ApplyToBuffer_UndoableAndAutoSaveProtectsCaretLine()
        {
            var buf = new EditorTabBuffer { FilePath = "/tmp/x.txt", Content = "a  \nb  \nc", HasFinalNewline = false };
            buf.SyncLinesFromContent();
            buf.CursorLine = 1;
            buf.CursorCol = 3;
            var opts = new SaveActionOptions { TrimTrailingWhitespace = true, InsertFinalNewline = true, IsAutoSave = true };
            Assert.True(SaveActions.ApplyToBuffer(buf, opts));
            Assert.Equal("a", buf.Lines[0]);
            Assert.Equal("b  ", buf.Lines[1]); // caret line kept during auto save
            Assert.True(buf.HasFinalNewline);
            Assert.True(buf.Undo());
            Assert.Equal("a  ", buf.Lines[0]);

            var buf2 = new EditorTabBuffer { FilePath = "/tmp/x.txt", Content = "a  \nb  \n" };
            buf2.SyncLinesFromContent();
            buf2.CursorLine = 1;
            Assert.True(SaveActions.ApplyToBuffer(buf2, new SaveActionOptions { TrimTrailingWhitespace = true }));
            Assert.Equal("b", buf2.Lines[1]);
            Assert.False(SaveActions.ApplyToBuffer(buf2, new SaveActionOptions()));
        }

        [Fact]
        public void Settings_DefaultsOffAndAutoSaveModeMapping()
        {
            var s = new IdePersistentSettings();
            Assert.False(s.FormatOnSave);
            Assert.False(s.TrimTrailingWhitespaceOnSave);
            Assert.False(s.InsertFinalNewlineOnSave);
            Assert.False(s.GitAutoFetch);
            Assert.Equal("off", s.EffectiveAutoSaveMode);
            s.AutoSave = true;
            Assert.Equal("afterDelay", s.EffectiveAutoSaveMode);
            s.SetAutoSaveMode("onFocusChange");
            Assert.False(s.AutoSave);
            Assert.True(s.FocusChangeSavesFiles);
            s.SetAutoSaveMode("afterDelay");
            Assert.True(s.AutoSave);
            s.SetAutoSaveMode("nonsense");
            Assert.Equal("off", s.EffectiveAutoSaveMode);
            Assert.Equal(180, s.EffectiveAutoFetchPeriodSeconds);
            Assert.Equal(1000, s.EffectiveAutoSaveDelayMs);

            var copy = new IdePersistentSettings();
            s.FormatOnSave = true;
            s.GitCommitMessageHistory = new List<string> { "m" };
            copy.CopyWorkbenchParitySettingsFrom(s);
            Assert.True(copy.FormatOnSave);
            Assert.Equal("m", copy.GitCommitMessageHistory[0]);
        }
    }
}
