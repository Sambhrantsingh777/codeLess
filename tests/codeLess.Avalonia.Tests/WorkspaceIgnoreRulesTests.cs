using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using codeLess.Studio;
using Xunit;

namespace codeLess.Avalonia.Tests
{
    /// <summary>.gitignore matching, workspace walk exclusions and the Quick Open index used for big monorepos.</summary>
    public class WorkspaceIgnoreRulesTests : IDisposable
    {
        private readonly string root;

        public WorkspaceIgnoreRulesTests()
        {
            root = Path.Combine(Path.GetTempPath(), "cl-ignore-tests-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(root);
        }

        public void Dispose()
        {
            try { Directory.Delete(root, true); } catch { }
        }

        private string Touch(string rel, string content = "x")
        {
            string p = Path.Combine(root, rel.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(p));
            File.WriteAllText(p, content);
            return p;
        }

        private static int M(GitIgnoreFile f, string rel, bool dir = false)
        {
            string name = rel.Contains('/') ? rel.Substring(rel.LastIndexOf('/') + 1) : rel;
            return f.Match(rel, name, dir);
        }

        [Fact]
        public void GitIgnore_PatternSemantics()
        {
            var f = new GitIgnoreFile("/r", new[]
            {
                "# comment", "", "node_modules/", "*.log", "!important.log", "/logs/", "build", "docs/**/*.tmp",
                "**/cache/*.bin", "a?c.txt", "[Tt]emp/", "src/generated", "trailing   ", "\\#hash"
            });
            Assert.Equal(1, M(f, "node_modules", true));
            Assert.Equal(0, M(f, "node_modules", false));                // dir-only rule
            Assert.Equal(1, M(f, "pkg/node_modules", true));             // basename rule at any depth
            Assert.Equal(1, M(f, "x/y/app.log"));
            Assert.Equal(-1, M(f, "x/important.log"));                   // negation (last rule wins)
            Assert.Equal(1, M(f, "logs", true));                         // anchored
            Assert.Equal(0, M(f, "src/logs", true));
            Assert.Equal(1, M(f, "build"));
            Assert.Equal(1, M(f, "docs/a/b/c.tmp"));
            Assert.Equal(1, M(f, "docs/c.tmp"));                         // ** matches zero dirs
            Assert.Equal(0, M(f, "other/c.tmp"));
            Assert.Equal(1, M(f, "deep/x/cache/blob.bin"));
            Assert.Equal(1, M(f, "abc.txt"));
            Assert.Equal(0, M(f, "abbc.txt"));
            Assert.Equal(1, M(f, "Temp", true));
            Assert.Equal(1, M(f, "temp", true));
            Assert.Equal(1, M(f, "src/generated", true));
            Assert.Equal(0, M(f, "lib/src/generated", true));            // anchored to the .gitignore dir
            Assert.Equal(1, M(f, "trailing"));
            Assert.Equal(1, M(f, "#hash"));
        }

        [Fact]
        public void Walk_RespectsDefaultsGitIgnoreNestedAndNegation()
        {
            Touch(".gitignore", "dist/\n*.log\n!keep.log\ngenerated/\n");
            Touch("src/app.ts");
            Touch("src/deep/a/b/c/d/e/f/g/h/i/j/deep.ts");                // deeper than the old depth-7 limit
            Touch("src/debug.log");
            Touch("src/keep.log");
            Touch("src/generated/gen.ts");
            Touch("dist/bundle.js");
            Touch("node_modules/lib/index.js");
            Touch("packages/pkg-a/src/index.ts");                         // JS monorepo packages/ is source
            Touch("lib/.gitignore", "*.tmp\n");
            Touch("lib/x.tmp");
            Touch("lib/x.ts");
            Touch("img/logo.png");
            Touch(".git/HEAD", "ref: refs/heads/main");
            Touch(".git/info/exclude", "secret.txt\n");
            Touch("secret.txt");

            var rules = new WorkspaceIgnoreRules(root, WorkspaceSearchEngine.DefaultExcludedFolders, true);
            var files = rules.EnumerateFiles(new[] { root }, 1000, CancellationToken.None)
                .Select(f => f.Substring(root.Length + 1).Replace('\\', '/')).ToList();

            Assert.Contains("src/app.ts", files);
            Assert.Contains("src/deep/a/b/c/d/e/f/g/h/i/j/deep.ts", files);
            Assert.Contains("src/keep.log", files);
            Assert.Contains("packages/pkg-a/src/index.ts", files);
            Assert.Contains("lib/x.ts", files);
            Assert.Contains(".gitignore", files);
            Assert.DoesNotContain("src/debug.log", files);
            Assert.DoesNotContain("src/generated/gen.ts", files);
            Assert.DoesNotContain("dist/bundle.js", files);
            Assert.DoesNotContain("node_modules/lib/index.js", files);
            Assert.DoesNotContain("lib/x.tmp", files);
            Assert.DoesNotContain("img/logo.png", files);
            Assert.DoesNotContain("secret.txt", files);
            Assert.DoesNotContain(files, f => f.StartsWith(".git/"));

            Assert.True(rules.IsPathExcluded(Path.Combine(root, "node_modules", "x", "y.js"), false));
            Assert.True(rules.IsPathExcluded(Path.Combine(root, "src", "generated", "z.ts"), false));
            Assert.False(rules.IsPathExcluded(Path.Combine(root, "src", "new.ts"), false));

            bool capped;
            var few = rules.EnumerateFiles(new[] { root }, 3, CancellationToken.None, out capped);
            Assert.True(capped);
            Assert.Equal(3, few.Count);

            var noGit = new WorkspaceIgnoreRules(root, WorkspaceSearchEngine.DefaultExcludedFolders, false)
                .EnumerateFiles(new[] { root }, 1000, CancellationToken.None);
            Assert.Contains(noGit, f => f.EndsWith("debug.log"));
        }

        [Fact]
        public void NuGetPackagesFolder_IsStillExcluded()
        {
            Touch("packages/Newtonsoft.Json.13.0.1/Newtonsoft.Json.13.0.1.nupkg");
            Touch("packages/Newtonsoft.Json.13.0.1/lib/readme.txt");
            Touch("src/Program.cs");
            var files = new WorkspaceIgnoreRules(root, WorkspaceSearchEngine.DefaultExcludedFolders, true)
                .EnumerateFiles(new[] { root }, 1000, CancellationToken.None);
            Assert.Contains(files, f => f.EndsWith("Program.cs"));
            Assert.DoesNotContain(files, f => f.Contains("Newtonsoft"));
        }

        [Fact]
        public void FindInFilesEnumeration_SkipsGitIgnoredFiles()
        {
            Touch(".gitignore", "generated/\n");
            Touch("src/a.ts", "NEEDLE");
            Touch("src/generated/b.ts", "NEEDLE");
            var files = WorkspaceSearchEngine.EnumerateSearchFiles(new[] { root }, root, null, null, null, 1000, CancellationToken.None);
            Assert.Contains(files, f => f.EndsWith("a.ts"));
            Assert.DoesNotContain(files, f => f.EndsWith("b.ts"));
        }

        [Fact]
        public void QuickOpenIndex_RanksLikePaletteAndKeepsTopK()
        {
            var list = new List<string>();
            for (int i = 0; i < 20000; i++) list.Add(Path.Combine(root, "packages", "pkg-" + (i % 50), "src", "File" + i + ".ts"));
            list.Add(Path.Combine(root, "apps", "web", "UniqueQuokkaWidget.tsx"));
            var idx = new QuickOpenFileIndex(root, list);
            var hits = idx.Search("UniqueQuokka", 120, null);
            Assert.Single(hits);
            Assert.Equal("apps/web/UniqueQuokkaWidget.tsx", idx.GetRelativePath(hits[0].Index));
            Assert.Equal(WorkspaceSearchEngine.ComputeFuzzySubsequenceScore("UniqueQuokkaWidget.tsx", "UniqueQuokka"), hits[0].Score);

            var many = idx.Search("file1", 50, null);
            Assert.Equal(50, many.Count);
            for (int k = 1; k < many.Count; k++) Assert.True(many[k - 1].Score >= many[k].Score);
            Assert.Equal("File1.ts", idx.GetName(many[0].Index));        // prefix + shortest wins

            var multi = idx.Search("pkg-27/ file77", 10, null);
            Assert.NotEmpty(multi);
            Assert.Equal("packages/pkg-27/src/File77.ts", idx.GetRelativePath(multi[0].Index));

            var skip = new HashSet<string> { list[list.Count - 1] };
            Assert.Empty(idx.Search("UniqueQuokka", 10, skip));
            Assert.Equal(120, idx.Search("", 120, null).Count);
        }
    }
}
