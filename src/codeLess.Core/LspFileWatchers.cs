using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace codeLess.Studio
{
    /// <summary>
    /// One FileSystemWatcher entry of a workspace/didChangeWatchedFiles registration: a glob (string or RelativePattern)
    /// plus a WatchKind bit mask (1 = create, 2 = change, 4 = delete; default 7). C# 5 only.
    /// </summary>
    public sealed class LspFileWatcher
    {
        public Regex Glob;
        /// <summary>Base folder of a RelativePattern (null = the pattern is matched against the absolute path).</summary>
        public string BasePath;
        public int Kind = 7;
        public string Pattern;
        /// <summary>Server root: string patterns that aren't absolute are also matched relative to it.</summary>
        public string Root;

        public static List<LspFileWatcher> ParseMany(object registerOptions, string root)
        {
            List<LspFileWatcher> list = new List<LspFileWatcher>();
            List<object> watchers = LspJson.GetList(registerOptions, "watchers");
            if (watchers == null) return list;
            foreach (object w in watchers)
            {
                object gp = LspJson.Get(w, "globPattern");
                LspFileWatcher fw = new LspFileWatcher();
                fw.Kind = LspJson.GetInt(w, "kind", 7);
                string pattern = gp as string;
                if (pattern == null && gp != null)
                {
                    pattern = LspJson.GetString(gp, "pattern");
                    object baseUri = LspJson.Get(gp, "baseUri");
                    string uri = baseUri as string ?? LspJson.GetString(baseUri, "uri");
                    fw.BasePath = uri != null ? LspText.UriToPath(uri) : root;
                }
                if (string.IsNullOrEmpty(pattern)) continue;
                fw.Pattern = pattern;
                fw.Root = root;
                fw.Glob = LspGlob.ToRegex(pattern);
                if (fw.Glob != null) list.Add(fw);
            }
            return list;
        }

        /// <summary>True when an event of <paramref name="changeType"/> (1/2/3) on <paramref name="path"/> must be reported.</summary>
        public bool Matches(string path, int changeType)
        {
            int bit = changeType == 1 ? 1 : (changeType == 2 ? 2 : 4);
            if ((Kind & bit) == 0 || Glob == null || string.IsNullOrEmpty(path)) return false;
            string full;
            try { full = Path.GetFullPath(path); } catch { full = path; }
            full = full.Replace('\\', '/');
            if (BasePath != null)
            {
                string b;
                try { b = Path.GetFullPath(BasePath); } catch { b = BasePath; }
                b = b.Replace('\\', '/').TrimEnd('/') + "/";
                StringComparison cmp = LspText.IsWindows() || LspText.IsMacOS() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
                if (!full.StartsWith(b, cmp)) return false;
                return Glob.IsMatch(full.Substring(b.Length));
            }
            if (Glob.IsMatch(full) || Glob.IsMatch(full.TrimStart('/'))) return true;
            if (!string.IsNullOrEmpty(Root))
            {
                string r;
                try { r = Path.GetFullPath(Root); } catch { r = Root; }
                r = r.Replace('\\', '/').TrimEnd('/') + "/";
                StringComparison cmp2 = LspText.IsWindows() || LspText.IsMacOS() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
                if (full.StartsWith(r, cmp2)) return Glob.IsMatch(full.Substring(r.Length));
            }
            return false;
        }
    }

    /// <summary>LSP / VS Code glob syntax (*, **, ?, {a,b}, [abc], [!a]) to an anchored regex on '/'-separated paths.</summary>
    public static class LspGlob
    {
        public static Regex ToRegex(string glob)
        {
            if (string.IsNullOrEmpty(glob)) return null;
            string g = glob.Replace('\\', '/');
            StringBuilder sb = new StringBuilder("^");
            int braces = 0;
            for (int i = 0; i < g.Length; i++)
            {
                char c = g[i];
                if (c == '*')
                {
                    bool dbl = i + 1 < g.Length && g[i + 1] == '*';
                    if (dbl)
                    {
                        bool slashAfter = i + 2 < g.Length && g[i + 2] == '/';
                        if (slashAfter) { sb.Append("(?:.*/)?"); i += 2; }
                        else { sb.Append(".*"); i += 1; }
                    }
                    else sb.Append("[^/]*");
                }
                else if (c == '?') sb.Append("[^/]");
                else if (c == '{') { braces++; sb.Append("(?:"); }
                else if (c == '}' && braces > 0) { braces--; sb.Append(')'); }
                else if (c == ',' && braces > 0) sb.Append('|');
                else if (c == '[')
                {
                    int close = g.IndexOf(']', i + 1);
                    if (close < 0) { sb.Append("\\["); continue; }
                    string body = g.Substring(i + 1, close - i - 1);
                    if (body.StartsWith("!", StringComparison.Ordinal)) body = "^" + body.Substring(1);
                    sb.Append('[').Append(body.Replace("\\", "\\\\")).Append(']');
                    i = close;
                }
                else sb.Append(Regex.Escape(c.ToString()));
            }
            while (braces-- > 0) sb.Append(')');
            sb.Append('$');
            RegexOptions opts = RegexOptions.CultureInvariant;
            if (LspText.IsWindows() || LspText.IsMacOS()) opts |= RegexOptions.IgnoreCase;
            try { return new Regex(sb.ToString(), opts); }
            catch { return null; }
        }
    }
}
