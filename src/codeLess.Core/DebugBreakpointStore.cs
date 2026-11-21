using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace codeLess.Studio
{
    /// <summary>
    /// All source breakpoints of a window, keyed by file (they survive closing the tab, like VS Code). Open buffers
    /// mirror the line set in <c>EditorTabBuffer.Breakpoints</c> (what the gutters draw / toggle) plus glyph kinds.
    /// Not thread-safe: use from the UI thread.
    /// </summary>
    public sealed class DebugBreakpointStore
    {
        private readonly Dictionary<string, List<DebugSourceBreakpoint>> byFile = new Dictionary<string, List<DebugSourceBreakpoint>>(StringComparer.Ordinal);

        /// <summary>Raised with the file path whose breakpoints changed (null = many files).</summary>
        public event Action<string> Changed;

        private void Raise(string path)
        {
            Action<string> h = Changed;
            if (h != null) { try { h(path); } catch { } }
        }

        private List<DebugSourceBreakpoint> ListFor(string path, bool create)
        {
            string key = DebugPaths.Key(path);
            List<DebugSourceBreakpoint> l;
            if (!byFile.TryGetValue(key, out l) && create)
            {
                l = new List<DebugSourceBreakpoint>();
                byFile[key] = l;
            }
            return l;
        }

        public List<DebugSourceBreakpoint> GetForFile(string path)
        {
            List<DebugSourceBreakpoint> l = ListFor(path, false);
            List<DebugSourceBreakpoint> res = l != null ? new List<DebugSourceBreakpoint>(l) : new List<DebugSourceBreakpoint>();
            res.Sort(delegate (DebugSourceBreakpoint a, DebugSourceBreakpoint b) { return a.Line.CompareTo(b.Line); });
            return res;
        }

        public List<DebugSourceBreakpoint> All()
        {
            List<DebugSourceBreakpoint> res = new List<DebugSourceBreakpoint>();
            foreach (List<DebugSourceBreakpoint> l in byFile.Values) res.AddRange(l);
            res.Sort(delegate (DebugSourceBreakpoint a, DebugSourceBreakpoint b)
            {
                int c = string.Compare(Path.GetFileName(a.FilePath), Path.GetFileName(b.FilePath), StringComparison.OrdinalIgnoreCase);
                if (c == 0) c = string.Compare(a.FilePath, b.FilePath, StringComparison.Ordinal);
                return c != 0 ? c : a.Line.CompareTo(b.Line);
            });
            return res;
        }

        /// <summary>Files that have at least one breakpoint (real paths).</summary>
        public List<string> Files()
        {
            List<string> res = new List<string>();
            foreach (List<DebugSourceBreakpoint> l in byFile.Values)
                if (l.Count > 0) res.Add(l[0].FilePath);
            return res;
        }

        public int Count
        {
            get { int n = 0; foreach (List<DebugSourceBreakpoint> l in byFile.Values) n += l.Count; return n; }
        }

        public DebugSourceBreakpoint Find(string path, int line)
        {
            List<DebugSourceBreakpoint> l = ListFor(path, false);
            if (l == null) return null;
            foreach (DebugSourceBreakpoint bp in l) if (bp.Line == line) return bp;
            return null;
        }

        public DebugSourceBreakpoint Add(string path, int line)
        {
            DebugSourceBreakpoint existing = Find(path, line);
            if (existing != null) return existing;
            DebugSourceBreakpoint bp = new DebugSourceBreakpoint();
            bp.FilePath = path;
            bp.Line = line;
            ListFor(path, true).Add(bp);
            Raise(path);
            return bp;
        }

        /// <summary>Adds or removes; returns the new breakpoint, or null when one was removed.</summary>
        public DebugSourceBreakpoint Toggle(string path, int line)
        {
            DebugSourceBreakpoint existing = Find(path, line);
            if (existing != null) { Remove(existing); return null; }
            return Add(path, line);
        }

        public void Remove(DebugSourceBreakpoint bp)
        {
            if (bp == null) return;
            List<DebugSourceBreakpoint> l = ListFor(bp.FilePath, false);
            if (l != null && l.Remove(bp))
            {
                if (l.Count == 0) byFile.Remove(DebugPaths.Key(bp.FilePath));
                Raise(bp.FilePath);
            }
        }

        public void RemoveAll()
        {
            byFile.Clear();
            Raise(null);
        }

        public void SetEnabled(DebugSourceBreakpoint bp, bool enabled)
        {
            if (bp == null || bp.Enabled == enabled) return;
            bp.Enabled = enabled;
            Raise(bp.FilePath);
        }

        public void SetAllEnabled(bool enabled)
        {
            foreach (List<DebugSourceBreakpoint> l in byFile.Values)
                foreach (DebugSourceBreakpoint bp in l) bp.Enabled = enabled;
            Raise(null);
        }

        /// <summary>Edits condition / hit count / log message (empty strings clear).</summary>
        public void Update(DebugSourceBreakpoint bp, string condition, string hitCondition, string logMessage)
        {
            if (bp == null) return;
            bp.Condition = string.IsNullOrWhiteSpace(condition) ? null : condition.Trim();
            bp.HitCondition = string.IsNullOrWhiteSpace(hitCondition) ? null : hitCondition.Trim();
            bp.LogMessage = string.IsNullOrWhiteSpace(logMessage) ? null : logMessage;
            Raise(bp.FilePath);
        }

        /// <summary>Reconciles the store with a buffer's gutter line set (after a gutter click).</summary>
        public void SyncFromBuffer(EditorTabBuffer buf)
        {
            if (buf == null || string.IsNullOrEmpty(buf.FilePath) || buf.Breakpoints == null) return;
            List<DebugSourceBreakpoint> l = ListFor(buf.FilePath, true);
            bool changed = false;
            for (int i = l.Count - 1; i >= 0; i--)
                if (!buf.Breakpoints.Contains(l[i].Line)) { l.RemoveAt(i); changed = true; }
            foreach (int line in buf.Breakpoints)
            {
                bool have = false;
                foreach (DebugSourceBreakpoint bp in l) if (bp.Line == line) { have = true; break; }
                if (!have)
                {
                    DebugSourceBreakpoint bp = new DebugSourceBreakpoint();
                    bp.FilePath = buf.FilePath;
                    bp.Line = line;
                    l.Add(bp);
                    changed = true;
                }
            }
            if (l.Count == 0) byFile.Remove(DebugPaths.Key(buf.FilePath));
            if (changed) Raise(buf.FilePath);
        }

        /// <summary>Writes the store's state for this file into the buffer (lines + glyph kinds).</summary>
        public void ApplyToBuffer(EditorTabBuffer buf, bool sessionActive)
        {
            if (buf == null || string.IsNullOrEmpty(buf.FilePath)) return;
            if (buf.Breakpoints == null) buf.Breakpoints = new HashSet<int>();
            List<DebugSourceBreakpoint> l = ListFor(buf.FilePath, false);
            if (l == null || l.Count == 0)
            {
                // Lines toggled in the gutter before the store knew this buffer stay (store adopts them).
                if (buf.Breakpoints.Count > 0) { SyncFromBuffer(buf); l = ListFor(buf.FilePath, false); }
                if (l == null) { buf.BreakpointGlyphs = null; return; }
            }
            buf.Breakpoints.Clear();
            Dictionary<int, DebugBreakpointGlyph> glyphs = new Dictionary<int, DebugBreakpointGlyph>();
            foreach (DebugSourceBreakpoint bp in l)
            {
                buf.Breakpoints.Add(bp.Line);
                glyphs[bp.Line] = GlyphFor(bp, sessionActive);
            }
            buf.BreakpointGlyphs = glyphs;
        }

        public static DebugBreakpointGlyph GlyphFor(DebugSourceBreakpoint bp, bool sessionActive)
        {
            if (!bp.Enabled) return DebugBreakpointGlyph.Disabled;
            if (sessionActive && bp.Verified.HasValue && !bp.Verified.Value) return DebugBreakpointGlyph.Unverified;
            if (bp.IsLogpoint) return DebugBreakpointGlyph.Logpoint;
            if (bp.IsConditional) return DebugBreakpointGlyph.Conditional;
            return DebugBreakpointGlyph.Enabled;
        }

        public static string Describe(DebugSourceBreakpoint bp)
        {
            StringBuilder sb = new StringBuilder();
            sb.Append(Path.GetFileName(bp.FilePath)).Append(':').Append((bp.Line + 1).ToString(CultureInfo.InvariantCulture));
            if (!string.IsNullOrEmpty(bp.Condition)) sb.Append("  if ").Append(bp.Condition);
            if (!string.IsNullOrEmpty(bp.HitCondition)) sb.Append("  hits ").Append(bp.HitCondition);
            if (!string.IsNullOrEmpty(bp.LogMessage)) sb.Append("  log \"").Append(bp.LogMessage).Append('"');
            if (bp.Verified.HasValue && !bp.Verified.Value) sb.Append("  (unverified)");
            return sb.ToString();
        }
    }

    /// <summary>Builds shell command lines for the integrated terminal (runInTerminal, Run Python File).</summary>
    public static class DebugTerminalCommands
    {
        private static string QuotePs(string s) { return "'" + (s ?? "").Replace("'", "''") + "'"; }
        private static string QuoteCmd(string s) { return "\"" + (s ?? "").Replace("\"", "") + "\""; }

        /// <summary>Command that runs args (argv[0] = program) with env overrides in cwd, for the given shell.</summary>
        public static string Build(TerminalShellFlavor flavor, string cwd, IList<string> args, IDictionary<string, string> env)
        {
            StringBuilder sb = new StringBuilder();
            switch (flavor)
            {
                case TerminalShellFlavor.PowerShell:
                    if (!string.IsNullOrEmpty(cwd)) sb.Append("cd ").Append(QuotePs(cwd)).Append("; ");
                    if (env != null)
                        foreach (KeyValuePair<string, string> kv in env)
                        {
                            if (kv.Value == null) sb.Append("Remove-Item Env:").Append(kv.Key).Append(" -ErrorAction SilentlyContinue; ");
                            else sb.Append("$env:").Append(kv.Key).Append('=').Append(QuotePs(kv.Value)).Append("; ");
                        }
                    sb.Append("& ");
                    for (int i = 0; i < args.Count; i++) { if (i > 0) sb.Append(' '); sb.Append(QuotePs(args[i])); }
                    return sb.ToString();
                case TerminalShellFlavor.Cmd:
                    if (!string.IsNullOrEmpty(cwd)) sb.Append("cd /d ").Append(QuoteCmd(cwd)).Append(" && ");
                    if (env != null)
                        foreach (KeyValuePair<string, string> kv in env)
                            sb.Append("set \"").Append(kv.Key).Append('=').Append((kv.Value ?? "").Replace("\"", "")).Append("\" && ");
                    for (int i = 0; i < args.Count; i++) { if (i > 0) sb.Append(' '); sb.Append(QuoteCmd(args[i])); }
                    return sb.ToString();
                default:
                    // bash / zsh / sh / fish (fish accepts env ... and cd ...; and).
                    bool fish = flavor == TerminalShellFlavor.Fish;
                    if (!string.IsNullOrEmpty(cwd)) sb.Append("cd ").Append(PythonVenvActivator.QuotePosix(cwd)).Append(fish ? "; and " : " && ");
                    bool hasEnv = env != null && env.Count > 0;
                    if (hasEnv)
                    {
                        sb.Append("/usr/bin/env");
                        foreach (KeyValuePair<string, string> kv in env)
                        {
                            if (kv.Value == null) sb.Append(" -u ").Append(PythonVenvActivator.QuotePosix(kv.Key));
                            else sb.Append(' ').Append(PythonVenvActivator.QuotePosix(kv.Key + "=" + kv.Value));
                        }
                        sb.Append(' ');
                    }
                    for (int i = 0; i < args.Count; i++) { if (i > 0) sb.Append(' '); sb.Append(PythonVenvActivator.QuotePosix(args[i])); }
                    return sb.ToString();
            }
        }

        /// <summary>"python file.py" style run line (no cd / env).</summary>
        public static string BuildRun(TerminalShellFlavor flavor, string program, params string[] args)
        {
            List<string> all = new List<string>();
            all.Add(program);
            all.AddRange(args);
            // Bare program names need no quoting / call operator ("python3 x.py" reads better than "& 'python3' 'x.py'").
            if (flavor == TerminalShellFlavor.PowerShell && program.IndexOfAny(new char[] { ' ', '\'', '/', '\\' }) < 0)
            {
                StringBuilder sb = new StringBuilder(program);
                foreach (string a in args) sb.Append(' ').Append(QuotePs(a));
                return sb.ToString();
            }
            return Build(flavor, null, all, null);
        }
    }
}
