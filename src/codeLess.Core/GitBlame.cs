using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace codeLess.Studio
{
    /// <summary>Commit metadata reported by "git blame --porcelain" (shared by every line of that commit).</summary>
    public class GitBlameCommit
    {
        public string Hash = "";
        public string Author = "";
        public string AuthorMail = "";
        public DateTime AuthorTimeUtc;
        public string AuthorTz = "";
        public string Committer = "";
        public DateTime CommitterTimeUtc;
        public string Summary = "";
        public string Filename = "";
        public string PreviousHash = "";
        public bool IsBoundary;

        public bool IsUncommitted { get { return GitCli.IsZeroHash(Hash); } }
        public string ShortHash { get { return GitCli.ShortHash(Hash); } }
    }

    /// <summary>One blamed line: 1-based final (current file) line number and its commit.</summary>
    public class GitBlameLine
    {
        public int FinalLine;
        public int OriginalLine;
        public GitBlameCommit Commit;
    }

    /// <summary>Parser for "git blame --porcelain" and "--line-porcelain" output.</summary>
    public static class GitBlameParser
    {
        public static List<GitBlameLine> ParsePorcelain(string output)
        {
            List<GitBlameLine> result = new List<GitBlameLine>();
            if (string.IsNullOrEmpty(output)) return result;
            Dictionary<string, GitBlameCommit> commits = new Dictionary<string, GitBlameCommit>(StringComparer.Ordinal);
            string[] lines = output.Replace("\r\n", "\n").Split('\n');
            GitBlameCommit current = null;
            GitBlameLine pending = null;
            for (int i = 0; i < lines.Length; i++)
            {
                string line = lines[i];
                if (line.Length > 0 && line[0] == '\t')
                {
                    // Content line terminates the record for the pending header.
                    if (pending != null) result.Add(pending);
                    pending = null;
                    current = null;
                    continue;
                }
                if (current == null)
                {
                    // Header: "<40-hex> <orig> <final> [<count>]"
                    string[] parts = line.Split(' ');
                    if (parts.Length >= 3 && IsHex(parts[0]) && parts[0].Length >= 40)
                    {
                        GitBlameCommit c;
                        if (!commits.TryGetValue(parts[0], out c))
                        {
                            c = new GitBlameCommit();
                            c.Hash = parts[0];
                            commits[parts[0]] = c;
                        }
                        current = c;
                        pending = new GitBlameLine();
                        pending.Commit = c;
                        int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out pending.OriginalLine);
                        int.TryParse(parts[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out pending.FinalLine);
                    }
                    continue;
                }
                int sp = line.IndexOf(' ');
                string key = sp < 0 ? line : line.Substring(0, sp);
                string val = sp < 0 ? "" : line.Substring(sp + 1);
                switch (key)
                {
                    case "author": current.Author = val; break;
                    case "author-mail": current.AuthorMail = val.Trim('<', '>'); break;
                    case "author-time": current.AuthorTimeUtc = ParseUnix(val); break;
                    case "author-tz": current.AuthorTz = val; break;
                    case "committer": current.Committer = val; break;
                    case "committer-time": current.CommitterTimeUtc = ParseUnix(val); break;
                    case "summary": current.Summary = val; break;
                    case "filename": current.Filename = val; break;
                    case "boundary": current.IsBoundary = true; break;
                    case "previous":
                        {
                            int sp2 = val.IndexOf(' ');
                            current.PreviousHash = sp2 < 0 ? val : val.Substring(0, sp2);
                            break;
                        }
                }
            }
            return result;
        }

        private static DateTime ParseUnix(string s)
        {
            long v;
            if (long.TryParse((s ?? "").Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out v)) return GitCli.FromUnixSeconds(v);
            return DateTime.MinValue;
        }

        private static bool IsHex(string s)
        {
            if (string.IsNullOrEmpty(s)) return false;
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                bool ok = (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F');
                if (!ok) return false;
            }
            return true;
        }

        /// <summary>GitLens-style end-of-line text: "Author, 3 days ago • summary" / "You • Uncommitted changes".</summary>
        public static string FormatAnnotation(GitBlameCommit c, string currentUserName, DateTime utcNow)
        {
            if (c == null) return "";
            if (c.IsUncommitted) return "You • Uncommitted changes";
            string author = c.Author ?? "";
            if (!string.IsNullOrEmpty(currentUserName) && string.Equals(author, currentUserName, StringComparison.OrdinalIgnoreCase)) author = "You";
            string summary = c.Summary ?? "";
            if (summary.Length > 72) summary = summary.Substring(0, 71) + "…";
            return author + ", " + GitCli.FormatRelativeTime(c.AuthorTimeUtc, utcNow) + " • " + summary;
        }
    }

    /// <summary>Blame of one file (whole file or a line window), valid for one content key + HEAD signature.</summary>
    public class GitBlameFileResult
    {
        public string FilePath = "";
        public string RepoRoot = "";
        public string ContentKey = "";
        public string HeadSignature = "";
        public string UserName = "";
        /// <summary>1-based inclusive range that was blamed; 0/0 means the whole file.</summary>
        public int RangeStart;
        public int RangeEnd;
        public Dictionary<int, GitBlameLine> LinesByZeroBasedIndex = new Dictionary<int, GitBlameLine>();
        public bool NotTracked;
        public string Error = "";

        public bool Covers(int line0)
        {
            if (RangeStart <= 0) return true;
            int l1 = line0 + 1;
            return l1 >= RangeStart && l1 <= RangeEnd;
        }
    }

    /// <summary>
    /// Computes and caches blame off the UI thread. The UI calls <see cref="Request"/> (debounced) and reads
    /// <see cref="TryGetCached"/> while rendering; <see cref="BlameReady"/> fires on a worker thread when new data exists.
    /// </summary>
    public class GitBlameService
    {
        public const int WholeFileLineLimit = 12000;
        public const int WindowHalfSize = 400;

        private readonly object gate = new object();
        private readonly Dictionary<string, GitBlameFileResult> cache = new Dictionary<string, GitBlameFileResult>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, int> requestSeq = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, Dictionary<string, string>> fullMessages = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);

        /// <summary>Raised (worker thread) with the file path whose blame changed.</summary>
        public event Action<string> BlameReady;
        /// <summary>Raised (worker thread) once when git is unavailable.</summary>
        public event Action<string> StatusMessage;
        private bool reportedMissingGit;

        /// <summary>Stable key for buffer contents (length + ordinal hash).</summary>
        public static string ComputeContentKey(string content)
        {
            if (content == null) content = "";
            unchecked
            {
                int h = 23;
                for (int i = 0; i < content.Length; i++) h = h * 31 + content[i];
                return content.Length.ToString(CultureInfo.InvariantCulture) + ":" + h.ToString("x8", CultureInfo.InvariantCulture);
            }
        }

        public GitBlameFileResult TryGetCached(string filePath)
        {
            if (string.IsNullOrEmpty(filePath)) return null;
            lock (gate)
            {
                GitBlameFileResult r;
                return cache.TryGetValue(filePath, out r) ? r : null;
            }
        }

        public void Invalidate(string filePath)
        {
            if (string.IsNullOrEmpty(filePath)) return;
            lock (gate) cache.Remove(filePath);
        }

        /// <summary>
        /// Ensures blame for <paramref name="content"/> is (or will be) cached. Returns immediately; work runs on the
        /// thread pool. <paramref name="isDirty"/> buffers are blamed with --contents so line numbers match the editor.
        /// </summary>
        public void Request(string filePath, string content, bool isDirty, int caretLine0, int totalLines)
        {
            if (string.IsNullOrEmpty(filePath) || !File.Exists(filePath)) return;
            string key = ComputeContentKey(content);
            int seq;
            lock (gate)
            {
                int s;
                requestSeq.TryGetValue(filePath, out s);
                seq = s + 1;
                requestSeq[filePath] = seq;
            }
            string capturedContent = content ?? "";
            Task.Factory.StartNew(delegate
            {
                try { Compute(filePath, capturedContent, key, isDirty, caretLine0, totalLines, seq); }
                catch { }
            }, CancellationToken.None, TaskCreationOptions.None, TaskScheduler.Default);
        }

        private bool IsLatest(string filePath, int seq)
        {
            lock (gate)
            {
                int s;
                return requestSeq.TryGetValue(filePath, out s) && s == seq;
            }
        }

        /// <summary>
        /// Bytes for "git blame --contents": the buffer text with the file's own line endings and encoding (+ BOM). git
        /// compares them with the committed blob, so writing a CRLF / Latin-1 file as UTF-8 + LF made every line differ
        /// ("Not Committed Yet") as soon as the buffer was dirty.
        /// </summary>
        public static byte[] EncodeLikeFile(string filePath, string content)
        {
            string text = (content ?? "").Replace("\r\n", "\n").Replace("\r", "\n");
            string eol = "\n";
            Encoding enc = new UTF8Encoding(false);
            bool bom = false;
            try
            {
                if (File.Exists(filePath))
                {
                    bool finalNl;
                    EditorTabBuffer.ReadFilePreservingMetadata(filePath, out eol, out enc, out bom, out finalNl);
                    // Files without any line break report CRLF by default; keep LF for those.
                    string raw = File.ReadAllText(filePath);
                    if (raw.IndexOf('\n') < 0 && raw.IndexOf('\r') < 0) eol = "\n";
                }
            }
            catch { }
            if (eol != "\n") text = text.Replace("\n", eol);
            try { return EditorTabBuffer.EncodeTextStrict(text, enc, bom); }
            catch (EncoderFallbackException) { return new UTF8Encoding(false).GetBytes(text); }
        }

        private void Compute(string filePath, string content, string key, bool isDirty, int caretLine0, int totalLines, int seq)
        {
            string repo = GitCli.FindRepoRoot(filePath);
            string headSig = repo != null ? GitCli.ReadHeadSignature(repo) : "";
            GitBlameFileResult existing = TryGetCached(filePath);
            if (existing != null && existing.ContentKey == key && existing.HeadSignature == headSig && (existing.NotTracked || existing.Covers(caretLine0) || existing.Error.Length > 0))
                return;
            if (!IsLatest(filePath, seq)) return;

            GitBlameFileResult res = new GitBlameFileResult();
            res.FilePath = filePath;
            res.ContentKey = key;
            res.HeadSignature = headSig;
            res.RepoRoot = repo ?? "";
            if (repo == null)
            {
                res.NotTracked = true;
                Store(res);
                return;
            }
            if (!GitCli.IsAvailable())
            {
                res.Error = GitCli.GitMissingHint;
                Store(res);
                if (!reportedMissingGit)
                {
                    reportedMissingGit = true;
                    Action<string> sm = StatusMessage;
                    if (sm != null) sm(GitCli.GitMissingHint);
                }
                return;
            }
            string rel = GitCli.RepoRelativePath(repo, filePath);
            if (rel == null) { res.NotTracked = true; Store(res); return; }

            List<string> args = new List<string>();
            args.Add("blame");
            args.Add("--porcelain");
            if (totalLines > WholeFileLineLimit)
            {
                int a = Math.Max(1, caretLine0 + 1 - WindowHalfSize);
                int b = Math.Min(totalLines, caretLine0 + 1 + WindowHalfSize);
                res.RangeStart = a;
                res.RangeEnd = b;
                args.Add("-L");
                args.Add(a.ToString(CultureInfo.InvariantCulture) + "," + b.ToString(CultureInfo.InvariantCulture));
            }
            string tmp = null;
            try
            {
                if (isDirty)
                {
                    tmp = Path.Combine(Path.GetTempPath(), "codeless-blame-" + Guid.NewGuid().ToString("N") + ".tmp");
                    File.WriteAllBytes(tmp, EncodeLikeFile(filePath, content));
                    args.Add("--contents");
                    args.Add(tmp);
                }
                args.Add("--");
                args.Add(rel);
                int exit;
                string outp = GitCli.Run(repo, 30000, out exit, args.ToArray());
                if (exit != 0)
                {
                    // Untracked / new files: every line is uncommitted rather than an error.
                    res.NotTracked = true;
                }
                else
                {
                    foreach (GitBlameLine bl in GitBlameParser.ParsePorcelain(outp))
                        res.LinesByZeroBasedIndex[bl.FinalLine - 1] = bl;
                }
            }
            finally
            {
                if (tmp != null) { try { File.Delete(tmp); } catch { } }
            }
            res.UserName = GitCli.GetUserName(repo);
            if (!IsLatest(filePath, seq)) return;
            Store(res);
        }

        private void Store(GitBlameFileResult res)
        {
            lock (gate) cache[res.FilePath] = res;
            Action<string> h = BlameReady;
            if (h != null) h(res.FilePath);
        }

        /// <summary>Full commit message (subject + body), fetched lazily for hover cards. Returns null until loaded.</summary>
        public string TryGetFullMessage(string repoRoot, string hash)
        {
            lock (gate)
            {
                Dictionary<string, string> m;
                string msg;
                if (fullMessages.TryGetValue(repoRoot ?? "", out m) && m.TryGetValue(hash ?? "", out msg)) return msg;
            }
            return null;
        }

        /// <summary>Loads the full message synchronously (call off the UI thread) and caches it.</summary>
        public string LoadFullMessage(string repoRoot, string hash)
        {
            string cached = TryGetFullMessage(repoRoot, hash);
            if (cached != null) return cached;
            if (string.IsNullOrEmpty(repoRoot) || string.IsNullOrEmpty(hash) || GitCli.IsZeroHash(hash)) return "";
            int exit;
            string msg = GitCli.Run(repoRoot, 8000, out exit, "show", "-s", "--format=%B", hash);
            msg = exit == 0 ? (msg ?? "").TrimEnd() : "";
            lock (gate)
            {
                Dictionary<string, string> m;
                if (!fullMessages.TryGetValue(repoRoot, out m)) { m = new Dictionary<string, string>(StringComparer.Ordinal); fullMessages[repoRoot] = m; }
                m[hash] = msg;
            }
            return msg;
        }
    }
}
