using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

namespace codeLess.Studio
{
    public class WorkspaceSearchMatch
    {
        public string FilePath { get; set; }
        public string RelativePath { get; set; }
        public int LineNumber { get; set; }
        public int Column { get; set; }
        public string LinePreview { get; set; }
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

        public WorkspaceSearchOptions()
        {
            MaxResults = 300;
            MaxFileSizeBytes = 1024 * 1024;
        }
    }

    public static class WorkspaceSearchEngine
    {
        public static List<string> EnumerateWorkspaceFiles(string workspaceRoot, IEnumerable<string> excludedFolders = null, int maxFiles = 5000)
        {
            List<string> results = new List<string>();
            if (string.IsNullOrEmpty(workspaceRoot) || !Directory.Exists(workspaceRoot))
                return results;

            HashSet<string> excluded = new HashSet<string>(
                excludedFolders ?? new[] { ".git", "node_modules", "bin", "obj", "dist", "build", "out", ".venv", "__pycache__", ".next", "target", "packages" },
                StringComparer.OrdinalIgnoreCase);

            Queue<string> dirs = new Queue<string>();
            dirs.Enqueue(workspaceRoot);

            while (dirs.Count > 0 && results.Count < maxFiles)
            {
                string current = dirs.Dequeue();
                try
                {
                    foreach (string sub in Directory.GetDirectories(current))
                    {
                        string name = Path.GetFileName(sub);
                        if (!string.IsNullOrEmpty(name) && !excluded.Contains(name))
                            dirs.Enqueue(sub);
                    }
                    foreach (string file in Directory.GetFiles(current))
                    {
                        results.Add(file);
                        if (results.Count >= maxFiles) break;
                    }
                }
                catch { }
            }
            return results;
        }

        public static Regex CompileSearchRegex(string query, WorkspaceSearchOptions options, out string errorMessage)
        {
            errorMessage = null;
            if (string.IsNullOrEmpty(query)) return null;
            if (options == null) options = new WorkspaceSearchOptions();

            if (!options.UseRegex && !options.WholeWord)
                return null;

            try
            {
                string pattern = options.UseRegex ? query : Regex.Escape(query);
                if (options.WholeWord)
                    pattern = @"\b(?:" + pattern + @")\b";
                RegexOptions rxOpts = options.CaseSensitive ? RegexOptions.None : RegexOptions.IgnoreCase;
                return new Regex(pattern, rxOpts);
            }
            catch (Exception ex)
            {
                errorMessage = ex.Message;
                return null;
            }
        }

        public static List<WorkspaceSearchMatch> SearchFiles(
            string workspaceRoot,
            IEnumerable<string> files,
            string query,
            WorkspaceSearchOptions options = null,
            CancellationToken ct = default(CancellationToken),
            Action<WorkspaceSearchMatch> onMatchFound = null)
        {
            List<WorkspaceSearchMatch> matches = new List<WorkspaceSearchMatch>();
            if (string.IsNullOrEmpty(query) || files == null)
                return matches;

            if (options == null) options = new WorkspaceSearchOptions();
            string regexErr;
            Regex rx = CompileSearchRegex(query, options, out regexErr);
            if (regexErr != null)
                return matches;

            StringComparison cmp = options.CaseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;

            foreach (string fp in files)
            {
                if (ct.IsCancellationRequested) break;
                try
                {
                    FileInfo fi = new FileInfo(fp);
                    if (!fi.Exists || fi.Length > options.MaxFileSizeBytes) continue;
                    string rel = LlmHarnessAndAgentEngine.GetRelativePath(workspaceRoot, fp);
                    int lineNo = 0;
                    foreach (string line in File.ReadLines(fp))
                    {
                        if (ct.IsCancellationRequested) break;
                        lineNo++;
                        int col = 1;
                        bool isMatch = false;
                        if (rx != null)
                        {
                            Match m = rx.Match(line);
                            if (m.Success)
                            {
                                isMatch = true;
                                col = m.Index + 1;
                            }
                        }
                        else
                        {
                            int idx = line.IndexOf(query, cmp);
                            if (idx >= 0)
                            {
                                isMatch = true;
                                col = idx + 1;
                            }
                        }

                        if (isMatch)
                        {
                            string preview = line.Trim();
                            if (preview.Length > 140)
                                preview = preview.Substring(0, 140) + "…";
                            var match = new WorkspaceSearchMatch
                            {
                                FilePath = fp,
                                RelativePath = rel,
                                LineNumber = lineNo,
                                Column = col,
                                LinePreview = preview
                            };
                            matches.Add(match);
                            if (onMatchFound != null)
                                onMatchFound(match);
                            if (matches.Count >= options.MaxResults)
                                break;
                        }
                    }
                    if (matches.Count >= options.MaxResults)
                        break;
                }
                catch { }
            }
            return matches;
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
            string pattern = options.UseRegex ? query : Regex.Escape(query);
            if (options.WholeWord)
                pattern = @"\b(?:" + pattern + @")\b";
            RegexOptions rxOpts = options.CaseSensitive ? RegexOptions.None : RegexOptions.IgnoreCase;
            Regex rx;
            try { rx = new Regex(pattern, rxOpts); }
            catch { return updatedFiles; }

            string rep = replacementText ?? "";
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
                    if (rx.IsMatch(raw))
                    {
                        string next = rx.Replace(raw, rep);
                        EditorTabBuffer.WriteFilePreservingMetadata(fp, next, fEnc, fBom, fEol, fFinalNl);
                        string written = EditorTabBuffer.ReadFilePreservingMetadata(fp, out fEnc, out fBom, out fEol, out fFinalNl);
                        updatedFiles[fp] = written;
                    }
                }
                catch { }
            }
            return updatedFiles;
        }

        public static int ComputeFuzzySubsequenceScore(string candidate, string query)
        {
            if (string.IsNullOrEmpty(query)) return 1;
            if (string.IsNullOrEmpty(candidate)) return 0;

            string cLow = candidate.ToLowerInvariant();
            string qLow = query.ToLowerInvariant();
            if (cLow == qLow) return 1000;
            if (cLow.StartsWith(qLow, StringComparison.Ordinal)) return 800 - candidate.Length;
            int subIdx = cLow.IndexOf(qLow, StringComparison.Ordinal);
            if (subIdx >= 0) return 600 - subIdx * 2 - candidate.Length;

            int qi = 0;
            int score = 100;
            for (int ci = 0; ci < candidate.Length && qi < qLow.Length; ci++)
            {
                if (cLow[ci] == qLow[qi])
                {
                    bool boundary = ci == 0 || candidate[ci - 1] == '/' || candidate[ci - 1] == '\\' ||
                                    candidate[ci - 1] == '_' || candidate[ci - 1] == '-' ||
                                    (char.IsUpper(candidate[ci]) && !char.IsUpper(candidate[ci - 1]));
                    score += boundary ? 25 : 5;
                    qi++;
                }
            }
            return qi == qLow.Length ? score : 0;
        }
    }
}
