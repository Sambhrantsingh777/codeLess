using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace codeLess.Studio
{
    public class GitRefBadge
    {
        public const string KindHead = "head";
        public const string KindBranch = "branch";
        public const string KindRemote = "remote";
        public const string KindTag = "tag";

        public string Name = "";
        public string Kind = KindBranch;
        /// <summary>True for the branch HEAD points to ("HEAD -> main").</summary>
        public bool IsCurrent;
    }

    public class GitCommitInfo
    {
        public string Hash = "";
        public string[] Parents = new string[0];
        public string AuthorName = "";
        public string AuthorEmail = "";
        public DateTime AuthorTimeUtc;
        public string Subject = "";
        public List<GitRefBadge> Refs = new List<GitRefBadge>();

        public string ShortHash { get { return GitCli.ShortHash(Hash); } }
        public bool IsMerge { get { return Parents != null && Parents.Length > 1; } }
    }

    public class GitChangedFile
    {
        /// <summary>A, M, D, R, C, T (git --name-status letter).</summary>
        public string Status = "M";
        public string Path = "";
        public string OldPath = "";

        public string DisplayName
        {
            get
            {
                if (!string.IsNullOrEmpty(OldPath) && OldPath != Path) return OldPath + " → " + Path;
                return Path;
            }
        }
    }

    /// <summary>Parses the machine-readable "git log" format used by <see cref="GitHistoryService"/>.</summary>
    public static class GitLogParser
    {
        /// <summary>Fields: hash, parents, author name, author email, author time, decorations, subject.</summary>
        public const string LogFormat = "--format=%H%x1f%P%x1f%an%x1f%ae%x1f%at%x1f%D%x1f%s%x1e";

        public static List<GitCommitInfo> Parse(string raw, ICollection<string> remoteNames)
        {
            List<GitCommitInfo> list = new List<GitCommitInfo>();
            if (string.IsNullOrEmpty(raw)) return list;
            string[] records = raw.Split('\x1e');
            foreach (string recRaw in records)
            {
                string rec = recRaw.Trim('\n', '\r');
                if (rec.Length == 0) continue;
                // "git log --graph" is not used, but tolerate leading newlines between records.
                string[] f = rec.Split('\x1f');
                if (f.Length < 7) continue;
                GitCommitInfo c = new GitCommitInfo();
                c.Hash = f[0].Trim();
                if (c.Hash.Length < 7) continue;
                string parents = f[1].Trim();
                c.Parents = parents.Length == 0 ? new string[0] : parents.Split(new char[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                c.AuthorName = f[2];
                c.AuthorEmail = f[3];
                long t;
                if (long.TryParse(f[4].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out t)) c.AuthorTimeUtc = GitCli.FromUnixSeconds(t);
                c.Refs = ParseDecorations(f[5], remoteNames);
                // The subject is last; re-join in case it contained the separator (it cannot, but be safe).
                c.Subject = f.Length == 7 ? f[6] : string.Join("\x1f", f, 6, f.Length - 6);
                list.Add(c);
            }
            return list;
        }

        /// <summary>Parses %D: "HEAD -> main, origin/main, tag: v1.0, feature".</summary>
        public static List<GitRefBadge> ParseDecorations(string d, ICollection<string> remoteNames)
        {
            List<GitRefBadge> refs = new List<GitRefBadge>();
            if (string.IsNullOrEmpty(d)) return refs;
            foreach (string partRaw in d.Split(','))
            {
                string part = partRaw.Trim();
                if (part.Length == 0) continue;
                GitRefBadge b = new GitRefBadge();
                if (part.StartsWith("HEAD -> ", StringComparison.Ordinal))
                {
                    b.Name = part.Substring(8).Trim();
                    b.Kind = GitRefBadge.KindBranch;
                    b.IsCurrent = true;
                }
                else if (part == "HEAD")
                {
                    b.Name = "HEAD";
                    b.Kind = GitRefBadge.KindHead;
                    b.IsCurrent = true;
                }
                else if (part.StartsWith("tag: ", StringComparison.Ordinal))
                {
                    b.Name = part.Substring(5).Trim();
                    b.Kind = GitRefBadge.KindTag;
                }
                else
                {
                    b.Name = part;
                    int slash = part.IndexOf('/');
                    bool isRemote = false;
                    if (slash > 0)
                    {
                        string first = part.Substring(0, slash);
                        if (remoteNames != null && remoteNames.Count > 0) isRemote = remoteNames.Contains(first);
                        else isRemote = first == "origin" || first == "upstream";
                    }
                    b.Kind = isRemote ? GitRefBadge.KindRemote : GitRefBadge.KindBranch;
                    if (isRemote && part.EndsWith("/HEAD", StringComparison.Ordinal)) continue; // origin/HEAD is noise
                }
                refs.Add(b);
            }
            return refs;
        }

        /// <summary>Parses "git diff-tree -r --name-status -z" output.</summary>
        public static List<GitChangedFile> ParseNameStatusZ(string raw)
        {
            List<GitChangedFile> list = new List<GitChangedFile>();
            if (string.IsNullOrEmpty(raw)) return list;
            string[] parts = raw.Split('\0');
            int i = 0;
            while (i < parts.Length)
            {
                string st = parts[i].Trim('\n', '\r', ' ');
                if (st.Length == 0) { i++; continue; }
                char kind = char.ToUpperInvariant(st[0]);
                GitChangedFile f = new GitChangedFile();
                f.Status = kind.ToString();
                if ((kind == 'R' || kind == 'C') && i + 2 < parts.Length)
                {
                    f.OldPath = parts[i + 1];
                    f.Path = parts[i + 2];
                    i += 3;
                }
                else if (i + 1 < parts.Length)
                {
                    f.Path = parts[i + 1];
                    i += 2;
                }
                else break;
                if (f.Path.Length > 0) list.Add(f);
            }
            return list;
        }
    }

    public class GitGraphSegment
    {
        public const byte TopToCenter = 0;
        public const byte CenterToBottom = 1;
        public const byte TopToBottom = 2;

        public int FromLane;
        public int ToLane;
        public int Color;
        public byte Kind;

        public override string ToString()
        {
            string k = Kind == TopToCenter ? "tc" : (Kind == CenterToBottom ? "cb" : "tb");
            return k + ":" + FromLane.ToString(CultureInfo.InvariantCulture) + ">" + ToLane.ToString(CultureInfo.InvariantCulture);
        }
    }

    public class GitGraphRow
    {
        public int NodeLane;
        public int NodeColor;
        public int LaneCount;
        public List<GitGraphSegment> Segments = new List<GitGraphSegment>();
    }

    /// <summary>
    /// Streaming lane assignment for a commit graph (commits must arrive children-before-parents, e.g. git log
    /// --date-order / --topo-order). Each lane remembers the commit it is waiting for; lanes never shift sideways, freed
    /// lanes are reused, and lanes that wait for the same commit converge into its node. Safe to feed page by page.
    /// </summary>
    public class GitGraphLayout
    {
        private readonly List<string> lanes = new List<string>();
        private readonly List<int> laneColors = new List<int>();
        private int nextColor;

        public int ActiveLaneCount { get { return lanes.Count; } }

        public GitGraphRow AddCommit(string hash, string[] parents)
        {
            GitGraphRow row = new GitGraphRow();
            int lanesBefore = lanes.Count;
            int nodeLane = lanes.IndexOf(hash);
            int nodeColor;
            if (nodeLane < 0)
            {
                nodeLane = FreeLane(-1);
                nodeColor = nextColor++;
                SetLane(nodeLane, null, nodeColor);
            }
            else nodeColor = laneColors[nodeLane];

            // Top half: lanes that end in this node converge, every other active lane passes straight through.
            for (int i = 0; i < lanesBefore; i++)
            {
                string expected = lanes[i];
                if (expected == null) continue;
                GitGraphSegment s = new GitGraphSegment();
                s.Color = laneColors[i];
                if (expected == hash)
                {
                    s.Kind = GitGraphSegment.TopToCenter;
                    s.FromLane = i;
                    s.ToLane = nodeLane;
                    lanes[i] = null;
                }
                else
                {
                    s.Kind = GitGraphSegment.TopToBottom;
                    s.FromLane = i;
                    s.ToLane = i;
                }
                row.Segments.Add(s);
            }

            // Bottom half: first parent continues in the node lane, further parents join or open lanes.
            if (parents != null && parents.Length > 0)
            {
                SetLane(nodeLane, parents[0], nodeColor);
                GitGraphSegment first = new GitGraphSegment();
                first.Kind = GitGraphSegment.CenterToBottom;
                first.FromLane = nodeLane;
                first.ToLane = nodeLane;
                first.Color = nodeColor;
                row.Segments.Add(first);
                for (int p = 1; p < parents.Length; p++)
                {
                    int target = -1;
                    for (int j = 0; j < lanes.Count; j++)
                    {
                        if (j != nodeLane && lanes[j] == parents[p]) { target = j; break; }
                    }
                    if (target < 0)
                    {
                        target = FreeLane(nodeLane);
                        SetLane(target, parents[p], nextColor++);
                    }
                    GitGraphSegment m = new GitGraphSegment();
                    m.Kind = GitGraphSegment.CenterToBottom;
                    m.FromLane = nodeLane;
                    m.ToLane = target;
                    m.Color = laneColors[target];
                    row.Segments.Add(m);
                }
            }
            else
            {
                lanes[nodeLane] = null;
            }

            row.NodeLane = nodeLane;
            row.NodeColor = nodeColor;
            row.LaneCount = Math.Max(Math.Max(lanesBefore, lanes.Count), nodeLane + 1);
            while (lanes.Count > 0 && lanes[lanes.Count - 1] == null)
            {
                lanes.RemoveAt(lanes.Count - 1);
                laneColors.RemoveAt(laneColors.Count - 1);
            }
            return row;
        }

        private int FreeLane(int exclude)
        {
            for (int i = 0; i < lanes.Count; i++)
            {
                if (i != exclude && lanes[i] == null) return i;
            }
            lanes.Add(null);
            laneColors.Add(0);
            return lanes.Count - 1;
        }

        private void SetLane(int idx, string hash, int color)
        {
            while (lanes.Count <= idx) { lanes.Add(null); laneColors.Add(0); }
            lanes[idx] = hash;
            laneColors[idx] = color;
        }
    }

    /// <summary>Git history queries (call from a background thread).</summary>
    public static class GitHistoryService
    {
        public const int PageSize = 400;

        public static List<string> GetRemoteNames(string repo)
        {
            List<string> names = new List<string>();
            int exit;
            string outp = GitCli.Run(repo, 5000, out exit, "remote");
            if (exit != 0) return names;
            foreach (string l in outp.Split('\n'))
            {
                string t = l.Trim();
                if (t.Length > 0) names.Add(t);
            }
            return names;
        }

        /// <summary>
        /// One page of history. <paramref name="filePath"/> (repo-relative) restricts to a file and follows renames;
        /// <paramref name="filter"/> matches message or author (case-insensitive).
        /// </summary>
        public static List<GitCommitInfo> LoadPage(string repo, int skip, int count, string filter, string filePath, bool allBranches, ICollection<string> remotes, out string error)
        {
            error = "";
            List<GitCommitInfo> result = new List<GitCommitInfo>();
            if (!GitCli.IsAvailable()) { error = GitCli.GitMissingHint; return result; }
            if (!string.IsNullOrEmpty(filter))
            {
                // Message OR author: two queries merged by date (git's --grep/--author combine with AND).
                Dictionary<string, GitCommitInfo> seen = new Dictionary<string, GitCommitInfo>(StringComparer.Ordinal);
                List<GitCommitInfo> merged = new List<GitCommitInfo>();
                string[] modes = new string[] { "--grep=" + filter, "--author=" + filter };
                foreach (string mode in modes)
                {
                    List<string> a = BaseArgs(skip + count, filePath, allBranches);
                    a.Insert(1, "-i");
                    a.Insert(2, "--fixed-strings");
                    a.Insert(3, mode);
                    int ex;
                    string raw = GitCli.Run(repo, 30000, out ex, a.ToArray());
                    if (ex != 0) continue;
                    foreach (GitCommitInfo c in GitLogParser.Parse(raw, remotes))
                    {
                        if (seen.ContainsKey(c.Hash)) continue;
                        seen[c.Hash] = c;
                        merged.Add(c);
                    }
                }
                merged.Sort(delegate (GitCommitInfo x, GitCommitInfo y) { return y.AuthorTimeUtc.CompareTo(x.AuthorTimeUtc); });
                for (int i = skip; i < merged.Count && result.Count < count; i++) result.Add(merged[i]);
                return result;
            }
            List<string> args = BaseArgs(count, filePath, allBranches);
            if (skip > 0) args.Insert(1, "--skip=" + skip.ToString(CultureInfo.InvariantCulture));
            int exit;
            string outp = GitCli.Run(repo, 60000, out exit, args.ToArray());
            if (exit != 0)
            {
                // Empty repository (no commits yet) is not an error worth shouting about.
                error = skip == 0 ? "No commits yet (or this folder is not a git repository)." : "";
                return result;
            }
            return GitLogParser.Parse(outp, remotes);
        }

        private static List<string> BaseArgs(int maxCount, string filePath, bool allBranches)
        {
            List<string> a = new List<string>();
            a.Add("log");
            a.Add("--date-order");
            a.Add("--decorate=short");
            a.Add(GitLogParser.LogFormat);
            a.Add("-n");
            a.Add(maxCount.ToString(CultureInfo.InvariantCulture));
            if (!string.IsNullOrEmpty(filePath))
            {
                a.Add("--follow");
                a.Add("--");
                a.Add(filePath);
            }
            else if (allBranches)
            {
                a.Insert(1, "--all");
            }
            return a;
        }

        public static List<GitChangedFile> GetChangedFiles(string repo, GitCommitInfo commit, out string error)
        {
            error = "";
            int exit;
            string outp;
            if (commit.Parents != null && commit.Parents.Length > 0)
                outp = GitCli.Run(repo, 30000, out exit, "diff-tree", "-r", "-M", "--name-status", "-z", "--no-commit-id", commit.Parents[0], commit.Hash);
            else
                outp = GitCli.Run(repo, 30000, out exit, "diff-tree", "-r", "--root", "--name-status", "-z", "--no-commit-id", commit.Hash);
            if (exit != 0) { error = "Could not list the files of " + commit.ShortHash + "."; return new List<GitChangedFile>(); }
            return GitLogParser.ParseNameStatusZ(outp);
        }

        /// <summary>Full commit message body for the details pane.</summary>
        public static string GetFullMessage(string repo, string hash)
        {
            int exit;
            string outp = GitCli.Run(repo, 8000, out exit, "show", "-s", "--format=%B", hash);
            return exit == 0 ? (outp ?? "").TrimEnd() : "";
        }

        /// <summary>Old/new text of a file changed by a commit (old = first parent). Binary files yield a note.</summary>
        public static void GetFileVersions(string repo, GitCommitInfo commit, GitChangedFile file, out string oldText, out string newText, out string note)
        {
            note = "";
            oldText = "";
            newText = "";
            bool bin1 = false, bin2 = false;
            string parent = commit.Parents != null && commit.Parents.Length > 0 ? commit.Parents[0] : null;
            if (file.Status != "A" && parent != null)
            {
                string oldPath = string.IsNullOrEmpty(file.OldPath) ? file.Path : file.OldPath;
                oldText = GitCli.ShowFileAtRevision(repo, parent, oldPath, out bin1) ?? "";
            }
            if (file.Status != "D")
                newText = GitCli.ShowFileAtRevision(repo, commit.Hash, file.Path, out bin2) ?? "";
            if (bin1 || bin2) note = "Binary file — no text diff available.";
        }

        public static string StatusLabel(string status)
        {
            switch (status)
            {
                case "A": return "Added";
                case "D": return "Deleted";
                case "R": return "Renamed";
                case "C": return "Copied";
                case "T": return "Type changed";
                default: return "Modified";
            }
        }
    }

    /// <summary>
    /// Commit graph split (both UIs): commit list and details side by side with a draggable splitter, or the details
    /// stacked below the list when the pane is narrow, so the graph is never squeezed to a sliver. STRICT C# 5.
    /// </summary>
    public static class GitGraphSplitLayout
    {
        /// <summary>Below this pane width (px) the details stack under the commit list.</summary>
        public const int StackBelowWidth = 560;
        public const int ListMinWidth = 240;
        public const int DetailsMinWidth = 220;
        public const int DetailsMaxInitialWidth = 380;
        public const int ListMinHeight = 96;
        public const int DetailsMinHeight = 140;

        public static bool ShouldStack(double paneWidth)
        {
            return paneWidth < StackBelowWidth;
        }

        /// <summary>Initial details column width for a side-by-side layout (38 % of the pane, clamped).</summary>
        public static int InitialDetailsWidth(double paneWidth)
        {
            int w = (int)Math.Round(paneWidth * 0.38);
            if (w > DetailsMaxInitialWidth) w = DetailsMaxInitialWidth;
            if (w < DetailsMinWidth) w = DetailsMinWidth;
            return w;
        }

        /// <summary>Initial details height when stacked (45 % of the content height, at least the minimum).</summary>
        public static int InitialDetailsHeight(double contentHeight)
        {
            int h = (int)Math.Round(contentHeight * 0.45);
            return h < DetailsMinHeight ? DetailsMinHeight : h;
        }
    }
}
