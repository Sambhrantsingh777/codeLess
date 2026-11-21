using System;
using System.Collections.Generic;
using System.IO;

namespace codeLess.Studio
{
    /// <summary>
    /// Quick Open (Ctrl/Cmd+P) over very large file lists: names / relative paths are lower-cased once, and each keystroke
    /// scores all files without allocating and keeps only the top results (bounded insertion), so 200k files stay
    /// interactive. Scoring matches WorkspaceSearchEngine.ComputeFuzzySubsequenceScore. Strict C# 5.
    /// </summary>
    public sealed class QuickOpenFileIndex
    {
        private readonly string[] fullPaths;
        private readonly string[] relPaths;
        private readonly string[] names;
        private readonly string[] relLower;
        private readonly string[] nameLower;

        public QuickOpenFileIndex(string workspaceRoot, IList<string> files)
        {
            int n = files != null ? files.Count : 0;
            fullPaths = new string[n];
            relPaths = new string[n];
            names = new string[n];
            relLower = new string[n];
            nameLower = new string[n];
            string root = string.IsNullOrEmpty(workspaceRoot) ? "" : workspaceRoot.TrimEnd('/', '\\');
            for (int i = 0; i < n; i++)
            {
                string f = files[i] ?? "";
                fullPaths[i] = f;
                string rel = root.Length > 0 && f.Length > root.Length + 1 && f.StartsWith(root, StringComparison.OrdinalIgnoreCase)
                    ? f.Substring(root.Length + 1)
                    : f;
                rel = rel.Replace('\\', '/');
                relPaths[i] = rel;
                int slash = rel.LastIndexOf('/');
                names[i] = slash >= 0 ? rel.Substring(slash + 1) : rel;
                relLower[i] = rel.ToLowerInvariant();
                nameLower[i] = names[i].ToLowerInvariant();
            }
        }

        public int Count { get { return fullPaths.Length; } }
        public string GetFullPath(int i) { return fullPaths[i]; }
        public string GetRelativePath(int i) { return relPaths[i]; }
        public string GetName(int i) { return names[i]; }

        public struct Hit
        {
            public int Index;
            public int Score;
        }

        /// <summary>Top matches for a space-separated fuzzy query (all tokens must match), best first.</summary>
        public List<Hit> Search(string query, int max, ICollection<string> skipFullPaths)
        {
            List<Hit> result = new List<Hit>();
            if (max <= 0) return result;
            string[] toks = (query ?? "").Replace('\\', '/').Split(new char[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            string[] toksLower = new string[toks.Length];
            bool[] hasSlash = new bool[toks.Length];
            for (int t = 0; t < toks.Length; t++)
            {
                toksLower[t] = toks[t].ToLowerInvariant();
                hasSlash[t] = toks[t].IndexOf('/') >= 0;
            }
            Hit[] top = new Hit[max];
            int topCount = 0;
            for (int i = 0; i < fullPaths.Length; i++)
            {
                int total = 0;
                if (toks.Length == 0) total = 1;
                for (int t = 0; t < toks.Length; t++)
                {
                    int byName = ScoreLowered(names[i], nameLower[i], toksLower[t]);
                    int byPath = (hasSlash[t] || byName == 0) ? ScoreLowered(relPaths[i], relLower[i], toksLower[t]) / 2 : 0;
                    int sc = Math.Max(byName, byPath);
                    if (sc <= 0) { total = 0; break; }
                    total += sc;
                }
                if (total <= 0) continue;
                if (skipFullPaths != null && skipFullPaths.Count > 0 && skipFullPaths.Contains(fullPaths[i])) continue;
                if (topCount == max && !Better(total, i, top[topCount - 1])) continue;
                // Bounded insertion keeps the array sorted best-first.
                int pos = topCount < max ? topCount : max - 1;
                while (pos > 0 && Better(total, i, top[pos - 1]))
                {
                    top[pos] = top[pos - 1];
                    pos--;
                }
                Hit h = new Hit();
                h.Index = i; h.Score = total;
                top[pos] = h;
                if (topCount < max) topCount++;
                if (toks.Length == 0 && topCount == max) break; // empty query: first files in walk order
            }
            for (int k = 0; k < topCount; k++) result.Add(top[k]);
            return result;
        }

        private bool Better(int score, int index, Hit other)
        {
            if (score != other.Score) return score > other.Score;
            return relPaths[index].Length < relPaths[other.Index].Length;
        }

        /// <summary>Same ranking as WorkspaceSearchEngine.ComputeFuzzySubsequenceScore, on pre-lower-cased input.</summary>
        public static int ScoreLowered(string candidate, string cLow, string qLow)
        {
            if (string.IsNullOrEmpty(qLow)) return 1;
            if (string.IsNullOrEmpty(cLow)) return 0;
            if (cLow.Length == qLow.Length && string.Equals(cLow, qLow, StringComparison.Ordinal)) return 1000;
            if (cLow.StartsWith(qLow, StringComparison.Ordinal)) return 800 - Math.Min(200, candidate.Length);
            int subIdx = cLow.IndexOf(qLow, StringComparison.Ordinal);
            if (subIdx >= 0) return Math.Max(301, 600 - subIdx * 2 - Math.Min(200, candidate.Length));
            int qi = 0;
            int score = 100;
            int lastMatch = -2;
            for (int ci = 0; ci < cLow.Length && qi < qLow.Length; ci++)
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
