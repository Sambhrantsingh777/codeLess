using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;

namespace codeLess.Studio
{
    /// <summary>A clickable link found in one terminal row (URL or an existing file with an optional line/column).</summary>
    public class TerminalLinkMatch
    {
        /// <summary>First cell (0-based column) covered by the link, including any :line:col suffix.</summary>
        public int StartCol;
        /// <summary>Number of cells covered by the link.</summary>
        public int Length;
        /// <summary>The raw text of the link as it appears in the terminal.</summary>
        public string Text;
        public bool IsUrl;
        /// <summary>For URL links: the URL to open.</summary>
        public string Url;
        /// <summary>For file links: the absolute path of an existing file.</summary>
        public string FilePath;
        /// <summary>1-based line (0 when the link carries no line).</summary>
        public int Line;
        /// <summary>1-based column (0 when the link carries no column).</summary>
        public int Column;

        public int EndColExclusive { get { return StartCol + Length; } }
    }

    /// <summary>
    /// Shared terminal link detection used by both the WinForms and the Avalonia terminal (C# 5 only: this file is
    /// compiled by the legacy csc for the Windows build). Mirrors VS Code's terminal links: http(s) URLs, and file
    /// paths (absolute Windows/Unix, ~/, ./, ../, relative or bare names) with an optional position suffix
    /// (path:line, path:line:col, path(line), path(line,col), and Python tracebacks: File "path", line N). A file
    /// path is only a link when it resolves to an existing file.
    /// </summary>
    public static class TerminalLinkDetector
    {
        private static readonly Regex UrlRegex = new Regex(
            @"(?:https?|file)://[^\s""'<>`]+",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        // Quoted path (compiler/traceback output). Python: File "/x/y.py", line 12, in <module>
        private static readonly Regex QuotedPathRegex = new Regex(
            @"[""'](?<path>[^""'\r\n]{1,1024}?)[""'](?:(?::(?<line>\d{1,7})(?::(?<col>\d{1,6}))?)|(?:,\s*line\s+(?<line2>\d{1,7})(?:,\s*col(?:umn)?\s+(?<col2>\d{1,6}))?))?",
            RegexOptions.Compiled);

        // Unquoted path: optional root (C:\ C:/ ~/ ./ ../ / \) + segments; position as :l[:c] or (l[,c]) or [l,c].
        private static readonly Regex PathRegex = new Regex(
            @"(?<path>(?:[A-Za-z]:[\\/]|~[\\/]|\.{1,2}[\\/]|[\\/])?(?:[^\s""'`<>|:*?()\[\]{},;\\/]+[\\/])*[^\s""'`<>|:*?()\[\]{},;\\/]+)" +
            @"(?:(?::(?<line>\d{1,7})(?:[:.](?<col>\d{1,6}))?)|(?:\((?<line2>\d{1,7})(?:\s*[,:]\s*(?<col2>\d{1,6}))?\))|(?:\[(?<line3>\d{1,7})(?:\s*,\s*(?<col3>\d{1,6}))?\]))?",
            RegexOptions.Compiled);

        // VS Code's default terminal.integrated.wordSeparators (plus NUL / continuation cells).
        private const string WordSeparators = " ()[]{}',\"`\u2500\u2018\u2019|\0\t";

        public static bool IsWordSeparator(char ch)
        {
            return WordSeparators.IndexOf(ch) >= 0;
        }

        /// <summary>Word bounds (inclusive) around <paramref name="col"/> using VS Code's separators. False on a separator.</summary>
        public static bool GetWordBounds(string line, int col, out int start, out int end)
        {
            start = col;
            end = col;
            if (string.IsNullOrEmpty(line) || col < 0 || col >= line.Length) return false;
            if (IsWordSeparator(line[col])) return false;
            while (start > 0 && !IsWordSeparator(line[start - 1])) start--;
            while (end + 1 < line.Length && !IsWordSeparator(line[end + 1])) end++;
            return true;
        }

        /// <summary>
        /// Finds the link covering cell <paramref name="col"/> of <paramref name="lineText"/> (one character per cell).
        /// Relative paths are resolved against <paramref name="baseDirectories"/> in order (terminal cwd first, then the
        /// workspace root). Returns null when nothing clickable is under the column.
        /// </summary>
        public static TerminalLinkMatch FindLinkAt(string lineText, int col, IList<string> baseDirectories)
        {
            if (string.IsNullOrEmpty(lineText) || col < 0 || col >= lineText.Length) return null;
            if (char.IsWhiteSpace(lineText[col])) return null;

            foreach (Match m in UrlRegex.Matches(lineText))
            {
                string url = TrimUrl(m.Value);
                if (col < m.Index || col >= m.Index + url.Length) continue;
                TerminalLinkMatch link = new TerminalLinkMatch();
                link.StartCol = m.Index;
                link.Length = url.Length;
                link.Text = url;
                if (url.StartsWith("file://", StringComparison.OrdinalIgnoreCase))
                {
                    string fp = FileUrlToPath(url);
                    string resolved = ResolveExistingFile(fp, baseDirectories);
                    if (resolved == null) return null;
                    link.FilePath = resolved;
                    return link;
                }
                link.IsUrl = true;
                link.Url = url;
                return link;
            }

            foreach (Match m in QuotedPathRegex.Matches(lineText))
            {
                if (col < m.Index || col >= m.Index + m.Length) continue;
                Group pg = m.Groups["path"];
                string raw = pg.Value.Trim();
                if (!LooksLikePath(raw)) continue;
                string resolved = ResolveExistingFile(raw, baseDirectories);
                if (resolved == null) continue;
                return BuildFileMatch(m.Index, m.Length, m.Value, resolved,
                    FirstInt(m, "line", "line2"), FirstInt(m, "col", "col2"));
            }

            foreach (Match m in PathRegex.Matches(lineText))
            {
                if (col < m.Index || col >= m.Index + m.Length) continue;
                string raw = m.Groups["path"].Value;
                int line = FirstInt(m, "line", "line2", "line3");
                int column = FirstInt(m, "col", "col2", "col3");
                int length = m.Length;
                if (!LooksLikePath(raw) && !LooksLikePath(raw.TrimEnd('.', '!'))) return null;

                string resolved = LooksLikePath(raw) ? ResolveExistingFile(raw, baseDirectories) : null;
                if (resolved == null && line == 0)
                {
                    // Sentence punctuation glued to a path ("see src/app.py.") is not part of it.
                    string trimmed = raw.TrimEnd('.', '!');
                    if (trimmed.Length > 0 && trimmed.Length < raw.Length && LooksLikePath(trimmed))
                    {
                        resolved = ResolveExistingFile(trimmed, baseDirectories);
                        if (resolved != null) length = trimmed.Length;
                    }
                }
                if (resolved == null) return null;
                if (col >= m.Index + length) return null;
                return BuildFileMatch(m.Index, length, lineText.Substring(m.Index, length), resolved, line, column);
            }
            return null;
        }

        private static TerminalLinkMatch BuildFileMatch(int start, int length, string text, string path, int line, int col)
        {
            TerminalLinkMatch link = new TerminalLinkMatch();
            link.StartCol = start;
            link.Length = length;
            link.Text = text;
            link.FilePath = path;
            link.Line = line;
            link.Column = col;
            return link;
        }

        private static int FirstInt(Match m, params string[] groups)
        {
            foreach (string g in groups)
            {
                Group grp = m.Groups[g];
                int v;
                if (grp != null && grp.Success && int.TryParse(grp.Value, out v) && v > 0) return v;
            }
            return 0;
        }

        /// <summary>Has a separator or an extension: avoids a File.Exists probe for every plain word under the mouse.</summary>
        private static bool LooksLikePath(string raw)
        {
            if (string.IsNullOrEmpty(raw) || raw.Length > 1024) return false;
            if (raw.IndexOf('/') >= 0 || raw.IndexOf('\\') >= 0) return raw.Trim('/', '\\', '.', '~').Length > 0;
            int dot = raw.LastIndexOf('.');
            return dot > 0 && dot < raw.Length - 1 && char.IsLetterOrDigit(raw[dot + 1]);
        }

        private static string TrimUrl(string url)
        {
            // Trailing sentence punctuation and unbalanced closing brackets are not part of a URL.
            while (url.Length > 0)
            {
                char last = url[url.Length - 1];
                if (".,;:!?".IndexOf(last) >= 0) { url = url.Substring(0, url.Length - 1); continue; }
                if (last == ')' && Count(url, '(') < Count(url, ')')) { url = url.Substring(0, url.Length - 1); continue; }
                if (last == ']' && Count(url, '[') < Count(url, ']')) { url = url.Substring(0, url.Length - 1); continue; }
                if (last == '}' && Count(url, '{') < Count(url, '}')) { url = url.Substring(0, url.Length - 1); continue; }
                break;
            }
            return url;
        }

        private static int Count(string s, char c)
        {
            int n = 0;
            for (int i = 0; i < s.Length; i++) if (s[i] == c) n++;
            return n;
        }

        private static string FileUrlToPath(string url)
        {
            try
            {
                Uri u = new Uri(url);
                if (u.IsFile) return u.LocalPath;
            }
            catch { }
            return url.Substring("file://".Length);
        }

        private static bool IsWindowsHost()
        {
            return Path.DirectorySeparatorChar == '\\';
        }

        /// <summary>
        /// Resolves a path printed in a terminal to an existing file: expands ~, maps WSL (/mnt/c/...) and Git Bash
        /// (/c/...) paths on Windows, and tries relative paths against each base directory. Null if no file exists.
        /// </summary>
        public static string ResolveExistingFile(string rawPath, IList<string> baseDirectories)
        {
            if (string.IsNullOrEmpty(rawPath)) return null;
            string p = rawPath.Trim();
            if (p.Length == 0 || p.IndexOfAny(Path.GetInvalidPathChars()) >= 0) return null;
            try
            {
                if (p == "~" || p.StartsWith("~/") || p.StartsWith("~\\"))
                {
                    string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                    if (string.IsNullOrEmpty(home)) return null;
                    p = p.Length <= 2 ? home : Path.Combine(home, p.Substring(2));
                }

                if (IsWindowsHost() && p.StartsWith("/"))
                {
                    string win = PosixToWindowsPath(p);
                    if (win != null && File.Exists(win)) return Path.GetFullPath(win);
                }

                bool rooted = IsWindowsHost()
                    ? (p.Length >= 3 && char.IsLetter(p[0]) && p[1] == ':' && (p[2] == '\\' || p[2] == '/')) || p.StartsWith("\\\\")
                    : p.StartsWith("/");
                if (rooted)
                    return File.Exists(p) ? Path.GetFullPath(p) : null;

                if (baseDirectories != null)
                {
                    foreach (string b in baseDirectories)
                    {
                        if (string.IsNullOrEmpty(b)) continue;
                        string candidate = Path.Combine(b, p);
                        if (File.Exists(candidate)) return Path.GetFullPath(candidate);
                    }
                }
            }
            catch { }
            return null;
        }

        /// <summary>/mnt/c/x or /c/x to C:\x (WSL / Git Bash / MSYS output on Windows). Null if not a drive path.</summary>
        public static string PosixToWindowsPath(string posix)
        {
            if (string.IsNullOrEmpty(posix)) return null;
            string s = posix;
            if (s.StartsWith("/mnt/", StringComparison.Ordinal)) s = s.Substring(4);
            if (s.Length >= 2 && s[0] == '/' && char.IsLetter(s[1]) && (s.Length == 2 || s[2] == '/'))
            {
                string rest = s.Length > 2 ? s.Substring(2) : "/";
                return char.ToUpperInvariant(s[1]) + ":" + rest.Replace('/', '\\');
            }
            return null;
        }

        /// <summary>Base directories for resolving relative links: live terminal cwd, the session's start dir, workspace root.</summary>
        public static List<string> BuildBaseDirectories(string liveCwd, string sessionCwd, string workspaceRoot)
        {
            List<string> dirs = new List<string>();
            foreach (string d in new string[] { liveCwd, sessionCwd, workspaceRoot })
            {
                if (string.IsNullOrEmpty(d)) continue;
                bool dup = false;
                foreach (string e in dirs)
                {
                    if (string.Equals(e.TrimEnd('/', '\\'), d.TrimEnd('/', '\\'), IsWindowsHost() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
                    {
                        dup = true;
                        break;
                    }
                }
                if (!dup) dirs.Add(d);
            }
            return dirs;
        }
    }
}
