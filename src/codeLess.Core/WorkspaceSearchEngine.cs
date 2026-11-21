using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

// NOTE: compiled by the legacy C# 5 compiler for the Windows build - keep this file strictly C# 5
// (no string interpolation, no ?., no expression-bodied members, no out var, no auto-property initializers).
namespace codeLess.Studio
{
    /// <summary>One match occurrence (VS Code shows one result row per occurrence, highlighting its range).</summary>
    public class WorkspaceSearchMatch
    {
        public string FilePath { get; set; }
        public string RelativePath { get; set; }
        /// <summary>1-based line number.</summary>
        public int LineNumber { get; set; }
        /// <summary>1-based column of the first matched character.</summary>
        public int Column { get; set; }
        /// <summary>Length of the matched range in characters (on <see cref="LineNumber"/>).</summary>
        public int MatchLength { get; set; }
        /// <summary>Trimmed / windowed line text shown in the results list.</summary>
        public string LinePreview { get; set; }
        /// <summary>0-based start of the match inside <see cref="LinePreview"/>.</summary>
        public int PreviewMatchStart { get; set; }
        /// <summary>Length of the highlighted part inside <see cref="LinePreview"/>.</summary>
        public int PreviewMatchLength { get; set; }

        public string DisplayText
        {
            get { return RelativePath + ":" + LineNumber + "  " + LinePreview; }
        }
    }

    public class WorkspaceSearchOptions
    {
        public bool CaseSensitive { get; set; }
        public bool WholeWord { get; set; }
        public bool UseRegex { get; set; }
        public int MaxResults { get; set; }
        public long MaxFileSizeBytes { get; set; }
        /// <summary>VS Code "files to include": comma separated globs / folder paths (e.g. "./src, *.cs").</summary>
        public string IncludePattern { get; set; }
        /// <summary>VS Code "files to exclude": comma separated globs / folder paths.</summary>
        public string ExcludePattern { get; set; }
        /// <summary>In-memory content of open (possibly unsaved) editors, keyed by full path. Searched instead of the disk copy.</summary>
        public Dictionary<string, string> ContentOverrides { get; set; }

        public WorkspaceSearchOptions()
        {
            MaxResults = 10000;
            MaxFileSizeBytes = 4L * 1024 * 1024;
        }
    }

    public class WorkspaceSearchResult
    {
        public List<WorkspaceSearchMatch> Matches { get; set; }
        public int FilesSearched { get; set; }
        public int FilesMatched { get; set; }
        public bool Truncated { get; set; }
        public bool Cancelled { get; set; }
        public string Error { get; set; }

        public WorkspaceSearchResult()
        {
            Matches = new List<WorkspaceSearchMatch>();
        }
    }

    /// <summary>
    /// VS Code style include / exclude glob filter. Comma separated entries; each entry is either
    ///  - "./path" (anchored at the workspace root), "path/sub" or "name" (matches at any folder depth),
    ///  - a glob using *, **, ?, [abc], {a,b},
    ///  - or an absolute path.
    /// An entry that matches a folder also matches everything below it.
    /// </summary>
    public sealed class GlobPathFilter
    {
        private readonly List<Regex> relRegexes = new List<Regex>();
        private readonly List<Regex> absRegexes = new List<Regex>();
        private readonly List<string> absPrefixes = new List<string>();
        private readonly List<string> literalRoots = new List<string>();
        private bool allLiteral = true;

        public bool IsEmpty
        {
            get { return relRegexes.Count == 0 && absRegexes.Count == 0 && absPrefixes.Count == 0; }
        }

        public static GlobPathFilter Parse(string patterns, string workspaceRoot)
        {
            GlobPathFilter f = new GlobPathFilter();
            if (string.IsNullOrWhiteSpace(patterns)) return f;
            string root = string.IsNullOrEmpty(workspaceRoot) ? "" : NormalizeSlashes(workspaceRoot).TrimEnd('/');

            foreach (string rawEntry in SplitPatterns(patterns))
            {
                string p = rawEntry.Trim();
                if (p.Length == 0) continue;
                bool rooted = false;
                try { rooted = Path.IsPathRooted(p) && !p.StartsWith("./") && !p.StartsWith(".\\"); } catch { }
                p = NormalizeSlashes(p);

                if (rooted)
                {
                    string abs = p.TrimEnd('/');
                    if (root.Length > 0 && (string.Equals(abs, root, StringComparison.OrdinalIgnoreCase) ||
                                            abs.StartsWith(root + "/", StringComparison.OrdinalIgnoreCase)))
                    {
                        string rel = abs.Length > root.Length ? abs.Substring(root.Length + 1) : "";
                        p = "./" + rel;
                    }
                    else
                    {
                        if (abs.IndexOfAny(new[] { '*', '?', '[', '{' }) >= 0)
                        {
                            f.allLiteral = false;
                            f.absRegexes.Add(new Regex("^" + GlobBody(abs) + "(?:/.*)?$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant));
                        }
                        else
                        {
                            f.absPrefixes.Add(abs);
                            f.literalRoots.Add(abs);
                        }
                        continue;
                    }
                }

                bool anchored = false;
                if (p.StartsWith("./")) { anchored = true; p = p.Substring(2); }
                else if (p.StartsWith("/")) { anchored = true; p = p.TrimStart('/'); }
                p = p.TrimEnd('/');
                if (p.Length == 0 || p == ".")
                {
                    f.relRegexes.Add(new Regex("^.*$", RegexOptions.Singleline));
                    if (anchored && root.Length > 0) f.literalRoots.Add(root); else f.allLiteral = false;
                    continue;
                }

                bool hasGlob = p.IndexOfAny(new[] { '*', '?', '[', '{' }) >= 0;
                string body = hasGlob ? GlobBody(p) : Regex.Escape(p);
                string pattern = (anchored ? "^" : "(?:^|/)") + body + "(?:/.*)?$";
                f.relRegexes.Add(new Regex(pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant));
                if (anchored && !hasGlob && root.Length > 0) f.literalRoots.Add(root + "/" + p);
                else f.allLiteral = false;
            }
            return f;
        }

        /// <summary>True when the file / folder (full path + workspace relative path) is matched by any entry.</summary>
        public bool IsMatch(string fullPath, string relPath)
        {
            string rel = NormalizeSlashes(relPath ?? "").TrimStart('/');
            if (rel.StartsWith("./")) rel = rel.Substring(2);
            string full = NormalizeSlashes(fullPath ?? "");
            for (int i = 0; i < relRegexes.Count; i++)
            {
                if (relRegexes[i].IsMatch(rel)) return true;
            }
            for (int i = 0; i < absRegexes.Count; i++)
            {
                if (full.Length > 0 && absRegexes[i].IsMatch(full)) return true;
            }
            for (int i = 0; i < absPrefixes.Count; i++)
            {
                string pre = absPrefixes[i];
                if (string.Equals(full, pre, StringComparison.OrdinalIgnoreCase) ||
                    full.StartsWith(pre + "/", StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            return false;
        }

        /// <summary>
        /// When every include entry is a plain folder path (e.g. "./src/app" from Explorer &gt; Find in Folder) the
        /// file walk can start at those folders instead of the whole workspace. Returns null otherwise.
        /// </summary>
        public List<string> GetLiteralRootDirectories()
        {
            if (IsEmpty || !allLiteral || literalRoots.Count == 0) return null;
            List<string> dirs = new List<string>();
            foreach (string r in literalRoots)
            {
                string native = r.Replace('/', Path.DirectorySeparatorChar);
                if (native.Length == 0) native = r;
                if (!Directory.Exists(native)) return null;
                dirs.Add(native);
            }
            return dirs;
        }

        public static string NormalizeSlashes(string p)
        {
            return (p ?? "").Replace('\\', '/');
        }

        private static IEnumerable<string> SplitPatterns(string patterns)
        {
            List<string> parts = new List<string>();
            StringBuilder cur = new StringBuilder();
            int depth = 0;
            foreach (char c in patterns)
            {
                if (c == '{') depth++;
                else if (c == '}' && depth > 0) depth--;
                if (c == ',' && depth == 0)
                {
                    parts.Add(cur.ToString());
                    cur.Length = 0;
                    continue;
                }
                cur.Append(c);
            }
            parts.Add(cur.ToString());
            return parts;
        }

        /// <summary>Converts a glob (without anchors) into a regex body. '/' is the only separator.</summary>
        public static string GlobBody(string glob)
        {
            StringBuilder sb = new StringBuilder();
            int braceDepth = 0;
            int i = 0;
            while (i < glob.Length)
            {
                char c = glob[i];
                if (c == '*')
                {
                    if (i + 1 < glob.Length && glob[i + 1] == '*')
                    {
                        if (i + 2 < glob.Length && glob[i + 2] == '/') { sb.Append("(?:.*/)?"); i += 3; }
                        else { sb.Append(".*"); i += 2; }
                        continue;
                    }
                    sb.Append("[^/]*");
                }
                else if (c == '?') sb.Append("[^/]");
                else if (c == '{') { sb.Append("(?:"); braceDepth++; }
                else if (c == '}' && braceDepth > 0) { sb.Append(")"); braceDepth--; }
                else if (c == ',' && braceDepth > 0) sb.Append("|");
                else if (c == '[')
                {
                    int close = glob.IndexOf(']', i + 1);
                    if (close > i + 1)
                    {
                        string cls = glob.Substring(i + 1, close - i - 1);
                        if (cls.StartsWith("!")) cls = "^" + cls.Substring(1);
                        sb.Append("[").Append(cls.Replace("\\", "\\\\")).Append("]");
                        i = close + 1;
                        continue;
                    }
                    sb.Append("\\[");
                }
                else sb.Append(Regex.Escape(c.ToString()));
                i++;
            }
            while (braceDepth-- > 0) sb.Append(")");
            return sb.ToString();
        }
    }

    public static class WorkspaceSearchEngine
    {
        public static readonly string[] DefaultExcludedFolders = new[] { ".git", "node_modules", "bin", "obj", "dist", "build", "out", ".venv", "__pycache__", ".next", "target", "packages", ".vs" };

        public static readonly HashSet<string> BinaryExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ".exe", ".dll", ".pdb", ".zip", ".ico", ".png", ".dat", ".bin", ".obj", ".cache", ".suo", ".vsix", ".nupkg",
            ".jpg", ".jpeg", ".gif", ".bmp", ".webp", ".pdf", ".woff", ".woff2", ".ttf", ".otf", ".eot", ".mp3", ".mp4",
            ".wav", ".avi", ".mov", ".tar", ".gz", ".tgz", ".7z", ".rar", ".so", ".dylib", ".class", ".jar", ".pyc", ".icns",
            ".a", ".lib", ".o", ".wasm", ".sqlite", ".db", ".psd", ".tiff", ".heic"
        };

        /// <summary>When true (default) workspace enumeration for search / quick open / indexing honours .gitignore files.</summary>
        public static bool RespectGitIgnore = true;

        public static List<string> EnumerateWorkspaceFiles(string workspaceRoot, IEnumerable<string> excludedFolders = null, int maxFiles = 5000)
        {
            return EnumerateSearchFiles(new[] { workspaceRoot }, workspaceRoot, excludedFolders, null, null, maxFiles, CancellationToken.None);
        }

        /// <summary>
        /// Breadth-first walk of <paramref name="roots"/> honouring excluded folder names, the "files to exclude" filter
        /// (folders are pruned) and the "files to include" filter (applied to files). Known binary extensions are skipped.
        /// </summary>
        public static List<string> EnumerateSearchFiles(
            IEnumerable<string> roots,
            string workspaceRoot,
            IEnumerable<string> excludedFolders,
            GlobPathFilter include,
            GlobPathFilter exclude,
            int maxFiles,
            CancellationToken ct)
        {
            List<string> results = new List<string>();
            if (roots == null) return results;
            // Excluded folder names + .gitignore (VS Code: files.exclude / search.exclude + search.useIgnoreFiles).
            string rulesRoot = workspaceRoot;
            if (string.IsNullOrEmpty(rulesRoot)) foreach (string r0 in roots) { rulesRoot = r0; break; }
            WorkspaceIgnoreRules ignoreRules = new WorkspaceIgnoreRules(rulesRoot, excludedFolders ?? DefaultExcludedFolders, RespectGitIgnore);
            HashSet<string> seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            bool hasInclude = include != null && !include.IsEmpty;
            bool hasExclude = exclude != null && !exclude.IsEmpty;

            Queue<string> dirs = new Queue<string>();
            foreach (string r in roots)
            {
                if (!string.IsNullOrEmpty(r) && Directory.Exists(r) && seen.Add(Path.GetFullPath(r)))
                    dirs.Enqueue(Path.GetFullPath(r));
            }
            seen.Clear();

            while (dirs.Count > 0 && results.Count < maxFiles)
            {
                if (ct.IsCancellationRequested) break;
                string current = dirs.Dequeue();
                try
                {
                    foreach (string file in Directory.EnumerateFiles(current))
                    {
                        if (ignoreRules.IsIgnoredFile(file)) continue;
                        if (hasInclude || hasExclude)
                        {
                            string rel = GetRelative(workspaceRoot, file);
                            if (hasExclude && exclude.IsMatch(file, rel)) continue;
                            if (hasInclude && !include.IsMatch(file, rel)) continue;
                        }
                        if (!seen.Add(file)) continue;
                        results.Add(file);
                        if (results.Count >= maxFiles) break;
                    }
                    List<string> subs = new List<string>();
                    foreach (string sub in Directory.EnumerateDirectories(current))
                    {
                        string name = Path.GetFileName(sub);
                        if (string.IsNullOrEmpty(name) || ignoreRules.IsExcludedDirectory(sub)) continue;
                        if (hasExclude && exclude.IsMatch(sub, GetRelative(workspaceRoot, sub))) continue;
                        subs.Add(sub);
                    }
                    subs.Sort(StringComparer.OrdinalIgnoreCase);
                    foreach (string s in subs) dirs.Enqueue(s);
                }
                catch { }
            }
            // VS Code orders results alphabetically by path (folders and files), independent of file-system order.
            results.Sort(ComparePathsForResults);
            return results;
        }

        private static int ComparePathsForResults(string a, string b)
        {
            return string.Compare(a.Replace('\\', '/'), b.Replace('\\', '/'), StringComparison.OrdinalIgnoreCase);
        }

        private static string GetRelative(string workspaceRoot, string fullPath)
        {
            if (string.IsNullOrEmpty(workspaceRoot)) return Path.GetFileName(fullPath);
            string root = workspaceRoot.TrimEnd('\\', '/');
            if (fullPath.Length > root.Length + 1 && fullPath.StartsWith(root, StringComparison.OrdinalIgnoreCase) &&
                (fullPath[root.Length] == '/' || fullPath[root.Length] == '\\'))
                return fullPath.Substring(root.Length + 1).Replace('\\', '/');
            return fullPath.Replace('\\', '/');
        }

        /// <summary>
        /// Builds the line regex for whole-word / regex searches. Returns null (and no error) for plain substring
        /// searches, which use the faster ordinal IndexOf path.
        /// </summary>
        public static Regex CompileSearchRegex(string query, WorkspaceSearchOptions options, out string errorMessage)
        {
            errorMessage = null;
            if (string.IsNullOrEmpty(query)) return null;
            if (options == null) options = new WorkspaceSearchOptions();

            if (!options.UseRegex && !options.WholeWord)
                return null;

            try
            {
                return new Regex(BuildPattern(query, options), BuildRegexOptions(options), TimeSpan.FromSeconds(2));
            }
            catch (Exception ex)
            {
                errorMessage = ex.Message;
                return null;
            }
        }

        private static string BuildPattern(string query, WorkspaceSearchOptions options)
        {
            string pattern = options.UseRegex ? query : Regex.Escape(query);
            if (options.WholeWord)
                pattern = @"(?<![\w])(?:" + pattern + @")(?![\w])";
            return pattern;
        }

        private static RegexOptions BuildRegexOptions(WorkspaceSearchOptions options)
        {
            RegexOptions rxOpts = RegexOptions.CultureInvariant;
            if (!options.CaseSensitive) rxOpts |= RegexOptions.IgnoreCase;
            return rxOpts;
        }

        /// <summary>Back-compat wrapper returning the flat match list.</summary>
        public static List<WorkspaceSearchMatch> SearchFiles(
            string workspaceRoot,
            IEnumerable<string> files,
            string query,
            WorkspaceSearchOptions options = null,
            CancellationToken ct = default(CancellationToken),
            Action<WorkspaceSearchMatch> onMatchFound = null)
        {
            Action<List<WorkspaceSearchMatch>> perFile = null;
            if (onMatchFound != null)
            {
                perFile = delegate(List<WorkspaceSearchMatch> list)
                {
                    foreach (WorkspaceSearchMatch m in list) onMatchFound(m);
                };
            }
            return Search(workspaceRoot, files, query, options, ct, perFile).Matches;
        }

        /// <summary>
        /// Searches <paramref name="files"/> line by line and reports every match occurrence (column + length) so a click
        /// can select the exact range. <paramref name="onFileMatches"/> is invoked once per file that has matches
        /// (from the calling thread) which lets the UI stream grouped results while a background search runs.
        /// </summary>
        public static WorkspaceSearchResult Search(
            string workspaceRoot,
            IEnumerable<string> files,
            string query,
            WorkspaceSearchOptions options,
            CancellationToken ct,
            Action<List<WorkspaceSearchMatch>> onFileMatches)
        {
            WorkspaceSearchResult result = new WorkspaceSearchResult();
            if (string.IsNullOrEmpty(query) || files == null)
                return result;

            if (options == null) options = new WorkspaceSearchOptions();
            string regexErr;
            Regex rx = CompileSearchRegex(query, options, out regexErr);
            if (regexErr != null)
            {
                result.Error = regexErr;
                return result;
            }

            StringComparison cmp = options.CaseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
            // Literal needle usable to reject a whole file cheaply (plain and whole-word searches).
            string quickRejectNeedle = options.UseRegex ? null : query;
            int maxResults = options.MaxResults > 0 ? options.MaxResults : 10000;

            // Files are read and scanned in parallel batches (I/O bound on many small files) and merged in input order,
            // so results and onFileMatches callbacks keep the same deterministic order as a sequential scan.
            List<string> fileList = files as List<string>;
            if (fileList == null) fileList = new List<string>(files);
            int batchSize = 64;
            for (int b0 = 0; b0 < fileList.Count; b0 += batchSize)
            {
                if (ct.IsCancellationRequested) { result.Cancelled = true; break; }
                int n = Math.Min(batchSize, fileList.Count - b0);
                FileScanResult[] slots = new FileScanResult[n];
                int remaining = maxResults - result.Matches.Count;
                int baseIndex = b0;
                WorkspaceSearchOptions opts = options;
                if (SearchParallelism <= 1 || n == 1)
                {
                    for (int k = 0; k < n; k++)
                        slots[k] = ScanOneFile(fileList[baseIndex + k], workspaceRoot, query, rx, cmp, quickRejectNeedle, opts, remaining, ct);
                }
                else
                {
                    ParallelOptions po = new ParallelOptions();
                    po.MaxDegreeOfParallelism = SearchParallelism;
                    System.Threading.Tasks.Parallel.For(0, n, po, delegate (int k)
                    {
                        slots[k] = ScanOneFile(fileList[baseIndex + k], workspaceRoot, query, rx, cmp, quickRejectNeedle, opts, remaining, ct);
                    });
                }
                bool stop = false;
                for (int k = 0; k < n; k++)
                {
                    FileScanResult fr = slots[k];
                    if (fr == null) continue;
                    if (fr.Searched) result.FilesSearched++;
                    if (fr.Cancelled) result.Cancelled = true;
                    List<WorkspaceSearchMatch> fileMatches = fr.Matches;
                    if (fileMatches != null && fileMatches.Count > 0)
                    {
                        int room = maxResults - result.Matches.Count;
                        if (fileMatches.Count > room) { fileMatches = fileMatches.GetRange(0, Math.Max(0, room)); stop = true; }
                        if (fileMatches.Count > 0)
                        {
                            result.FilesMatched++;
                            result.Matches.AddRange(fileMatches);
                            if (onFileMatches != null) onFileMatches(fileMatches);
                        }
                    }
                    if (fr.HitLimit || result.Matches.Count >= maxResults) stop = true;
                    if (stop) break;
                }
                if (stop)
                {
                    result.Truncated = true;
                    break;
                }
                if (result.Cancelled) break;
            }
            return result;
        }

        /// <summary>Degree of parallelism for Find in Files (file reads + scanning). 1 = sequential.</summary>
        public static int SearchParallelism = Math.Max(1, Math.Min(4, Environment.ProcessorCount));

        private sealed class FileScanResult
        {
            public bool Searched;
            public bool Cancelled;
            public bool HitLimit;
            public List<WorkspaceSearchMatch> Matches;
        }

        private static FileScanResult ScanOneFile(string fp, string workspaceRoot, string query, Regex rx, StringComparison cmp,
            string quickRejectNeedle, WorkspaceSearchOptions options, int maxMatches, CancellationToken ct)
        {
            FileScanResult fr = new FileScanResult();
            if (ct.IsCancellationRequested) { fr.Cancelled = true; return fr; }
            string text = null;
            try
            {
                string overrideText;
                if (options.ContentOverrides != null && options.ContentOverrides.TryGetValue(fp, out overrideText) && overrideText != null)
                {
                    text = overrideText;
                }
                else
                {
                    FileInfo fi = new FileInfo(fp);
                    if (!fi.Exists || fi.Length > options.MaxFileSizeBytes) return fr;
                    text = ReadTextFileForSearch(fp);
                }
            }
            catch { text = null; }
            if (text == null) return fr;
            fr.Searched = true;

            if (quickRejectNeedle != null && text.IndexOf(quickRejectNeedle, cmp) < 0)
                return fr;

            List<WorkspaceSearchMatch> fileMatches = new List<WorkspaceSearchMatch>();
            fr.Matches = fileMatches;
            string rel = GetRelative(workspaceRoot, fp);
            int lineNo = 0;
            int pos = 0;
            bool stop = false;
            try
            {
                while (pos <= text.Length && !stop)
                {
                    if ((lineNo & 255) == 0 && ct.IsCancellationRequested) { fr.Cancelled = true; break; }
                    int nl = text.IndexOf('\n', pos);
                    int end = nl < 0 ? text.Length : nl;
                    int lineEnd = (end > pos && text[end - 1] == '\r') ? end - 1 : end;
                    string line = text.Substring(pos, lineEnd - pos);
                    lineNo++;

                    if (rx != null)
                    {
                        Match m = rx.Match(line);
                        while (m.Success)
                        {
                            if (m.Length > 0)
                            {
                                fileMatches.Add(BuildMatch(fp, rel, lineNo, line, m.Index, m.Length));
                                if (fileMatches.Count >= maxMatches) { stop = true; fr.HitLimit = true; break; }
                            }
                            m = m.NextMatch();
                        }
                    }
                    else
                    {
                        int idx = line.IndexOf(query, 0, cmp);
                        while (idx >= 0)
                        {
                            fileMatches.Add(BuildMatch(fp, rel, lineNo, line, idx, query.Length));
                            if (fileMatches.Count >= maxMatches) { stop = true; fr.HitLimit = true; break; }
                            int next = idx + Math.Max(1, query.Length);
                            if (next >= line.Length) break;
                            idx = line.IndexOf(query, next, cmp);
                        }
                    }

                    if (nl < 0) break;
                    pos = nl + 1;
                }
            }
            catch (RegexMatchTimeoutException) { }
            return fr;
        }

        private static WorkspaceSearchMatch BuildMatch(string fp, string rel, int lineNo, string line, int idx, int len)
        {
            int lead = 0;
            while (lead < line.Length && (line[lead] == ' ' || line[lead] == '\t')) lead++;
            if (lead > idx) lead = idx;
            int start = Math.Max(lead, idx - 40);
            // Do not cut a surrogate pair in half.
            if (start > lead && start < line.Length && char.IsLowSurrogate(line[start])) start--;
            string prefix = start > lead ? "…" : "";
            string body = line.Substring(start);
            int maxLen = Math.Max(160, (idx - start) + len + 40);
            if (body.Length > maxLen)
            {
                int cut = maxLen;
                if (cut > 0 && char.IsHighSurrogate(body[cut - 1])) cut--;
                body = body.Substring(0, cut) + "…";
            }
            string preview = (prefix + body).TrimEnd();
            int pStart = prefix.Length + (idx - start);
            int pLen = Math.Max(0, Math.Min(len, preview.Length - pStart));

            WorkspaceSearchMatch match = new WorkspaceSearchMatch();
            match.FilePath = fp;
            match.RelativePath = rel;
            match.LineNumber = lineNo;
            match.Column = idx + 1;
            match.MatchLength = len;
            match.LinePreview = preview;
            match.PreviewMatchStart = Math.Max(0, Math.Min(pStart, preview.Length));
            match.PreviewMatchLength = pLen;
            return match;
        }

        /// <summary>Reads a text file for searching; returns null for binary content (NUL bytes without a UTF-16 BOM).</summary>
        public static string ReadTextFileForSearch(string path)
        {
            byte[] bytes = File.ReadAllBytes(path);
            if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
                return Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2);
            if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF)
                return Encoding.BigEndianUnicode.GetString(bytes, 2, bytes.Length - 2);
            int probe = Math.Min(bytes.Length, 8000);
            for (int i = 0; i < probe; i++)
            {
                if (bytes[i] == 0) return null;
            }
            if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
                return Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3);
            return Encoding.UTF8.GetString(bytes);
        }

        /// <summary>
        /// Regex used by Replace All. Multiline so ^ / $ behave per line exactly like the (line based) search did.
        /// </summary>
        public static Regex BuildReplaceRegex(string query, WorkspaceSearchOptions options, out string errorMessage)
        {
            errorMessage = null;
            if (string.IsNullOrEmpty(query)) return null;
            if (options == null) options = new WorkspaceSearchOptions();
            try
            {
                return new Regex(BuildPattern(query, options), BuildRegexOptions(options) | RegexOptions.Multiline, TimeSpan.FromSeconds(5));
            }
            catch (Exception ex)
            {
                errorMessage = ex.Message;
                return null;
            }
        }

        /// <summary>In plain-text mode the replacement must be literal: '$' is escaped so "$1", "$&amp;", "$$" are not substitutions.</summary>
        public static string PrepareReplacementText(string replacementText, bool useRegex)
        {
            string rep = replacementText ?? "";
            if (!useRegex) rep = rep.Replace("$", "$$");
            return rep;
        }

        /// <summary>Applies the replacement to in-memory text. Returns the input unchanged when nothing matched.</summary>
        public static string ReplaceInText(string text, Regex rx, string preparedReplacement, out int replacedCount)
        {
            replacedCount = 0;
            if (string.IsNullOrEmpty(text) || rx == null) return text;
            int count = 0;
            string next;
            try
            {
                string rep = preparedReplacement ?? "";
                next = rx.Replace(text, delegate(Match m) { count++; return m.Result(rep); });
            }
            catch (RegexMatchTimeoutException) { return text; }
            replacedCount = count;
            return count > 0 ? next : text;
        }

        public static Dictionary<string, string> ReplaceInFiles(
            IEnumerable<string> files,
            string query,
            string replacementText,
            WorkspaceSearchOptions options = null)
        {
            Dictionary<string, string> updatedFiles = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (string.IsNullOrEmpty(query) || files == null)
                return updatedFiles;

            if (options == null) options = new WorkspaceSearchOptions();
            string err;
            Regex rx = BuildReplaceRegex(query, options, out err);
            if (rx == null) return updatedFiles;
            string rep = PrepareReplacementText(replacementText, options.UseRegex);

            foreach (string fp in files)
            {
                try
                {
                    FileInfo fi = new FileInfo(fp);
                    if (!fi.Exists || fi.Length > options.MaxFileSizeBytes) continue;
                    Encoding fEnc;
                    bool fBom;
                    string fEol;
                    bool fFinalNl;
                    string raw = EditorTabBuffer.ReadFilePreservingMetadata(fp, out fEnc, out fBom, out fEol, out fFinalNl);
                    if (raw.IndexOf('\0') >= 0) continue;
                    int n;
                    string next = ReplaceInText(raw, rx, rep, out n);
                    if (n > 0 && !string.Equals(next, raw, StringComparison.Ordinal))
                    {
                        EditorTabBuffer.WriteFilePreservingMetadata(fp, next, fEnc, fBom, fEol, fFinalNl);
                        string written = EditorTabBuffer.ReadFilePreservingMetadata(fp, out fEnc, out fBom, out fEol, out fFinalNl);
                        updatedFiles[fp] = written;
                    }
                }
                catch { }
            }
            return updatedFiles;
        }

        /// <summary>
        /// Splits a Quick Open query such as "MainWindow.cs:120:5" into the file part and an optional 1-based line /
        /// column (VS Code "file:line:col" syntax). Returns the query unchanged when there is no numeric suffix.
        /// </summary>
        public static string SplitQuickOpenLineSuffix(string query, out int line, out int column)
        {
            line = 0;
            column = 0;
            if (string.IsNullOrEmpty(query)) return query ?? "";
            Match m = Regex.Match(query, @"^(?<f>.*?)[:(](?<l>\d+)(?:[:,](?<c>\d+))?\)?\s*$");
            if (!m.Success || m.Groups["f"].Value.Trim().Length == 0) return query;
            int.TryParse(m.Groups["l"].Value, out line);
            if (m.Groups["c"].Success) int.TryParse(m.Groups["c"].Value, out column);
            return m.Groups["f"].Value.Trim();
        }

        public static int ComputeFuzzySubsequenceScore(string candidate, string query)
        {
            if (string.IsNullOrEmpty(query)) return 1;
            if (string.IsNullOrEmpty(candidate)) return 0;

            string cLow = candidate.ToLowerInvariant();
            string qLow = query.ToLowerInvariant();
            if (cLow == qLow) return 1000;
            if (cLow.StartsWith(qLow, StringComparison.Ordinal)) return 800 - Math.Min(200, candidate.Length);
            int subIdx = cLow.IndexOf(qLow, StringComparison.Ordinal);
            if (subIdx >= 0) return Math.Max(301, 600 - subIdx * 2 - Math.Min(200, candidate.Length));

            int qi = 0;
            int score = 100;
            int lastMatch = -2;
            for (int ci = 0; ci < candidate.Length && qi < qLow.Length; ci++)
            {
                if (cLow[ci] == qLow[qi])
                {
                    bool boundary = ci == 0 || candidate[ci - 1] == '/' || candidate[ci - 1] == '\\' ||
                                    candidate[ci - 1] == '_' || candidate[ci - 1] == '-' || candidate[ci - 1] == '.' ||
                                    (char.IsUpper(candidate[ci]) && !char.IsUpper(candidate[ci - 1]));
                    score += boundary ? 25 : (ci == lastMatch + 1 ? 10 : 3);
                    lastMatch = ci;
                    qi++;
                }
            }
            return qi == qLow.Length ? Math.Min(300, score) : 0;
        }
    }
}
