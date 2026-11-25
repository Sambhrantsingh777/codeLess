using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace codeLess.Studio
{
    /// <summary>One problem pattern (VS Code problemMatcher.pattern). Group indexes are 1-based; 0 = unused.</summary>
    public sealed class ProblemPattern
    {
        public Regex Regexp;
        public int File;
        public int Line;
        public int Column;
        public int EndLine;
        public int EndColumn;
        /// <summary>"(10,5)" / "(10)" / "(1,2,3,4)" style group (msCompile).</summary>
        public int Location;
        public int Severity;
        public int Code;
        public int Message;
        public bool Loop;

        public ProblemPattern(string regex, int file, int line, int column, int severity, int code, int message)
        {
            Regexp = new Regex(regex, RegexOptions.Compiled | RegexOptions.CultureInvariant);
            File = file; Line = line; Column = column; Severity = severity; Code = code; Message = message;
        }
    }

    /// <summary>A problem matcher (VS Code semantics: single or multi-line patterns, optional loop on the last one).</summary>
    public sealed class ProblemMatcherDefinition
    {
        public string Name = "";
        public string Owner = "";
        public string Source = "";
        public List<ProblemPattern> Patterns = new List<ProblemPattern>();
        /// <summary>"absolute", "relative" or "autoDetect".</summary>
        public string FileLocation = "autoDetect";
        /// <summary>Base for relative paths ("${workspaceFolder}" or "${cwd}" or an explicit path).</summary>
        public string FileLocationBase = "${workspaceFolder}";
        public string DefaultSeverity = "Error";
        /// <summary>Custom (non-regex) matcher: Python tracebacks.</summary>
        public bool IsPythonTraceback;
    }

    /// <summary>Built-in matchers: $tsc, $gcc, $msCompile, $eslint-stylish, $go, $rustc, $python (+ aliases).</summary>
    public static class BuiltInProblemMatchers
    {
        private static readonly object gate = new object();
        private static Dictionary<string, ProblemMatcherDefinition> cache;

        public static ProblemMatcherDefinition Get(string name)
        {
            if (string.IsNullOrEmpty(name)) return null;
            string key = name.StartsWith("$", StringComparison.Ordinal) ? name : "$" + name;
            lock (gate)
            {
                if (cache == null) cache = Build();
                ProblemMatcherDefinition d;
                return cache.TryGetValue(key, out d) ? d : null;
            }
        }

        public static IEnumerable<string> Names
        {
            get { lock (gate) { if (cache == null) cache = Build(); return new List<string>(cache.Keys); } }
        }

        private static Dictionary<string, ProblemMatcherDefinition> Build()
        {
            Dictionary<string, ProblemMatcherDefinition> m = new Dictionary<string, ProblemMatcherDefinition>(StringComparer.OrdinalIgnoreCase);

            ProblemMatcherDefinition tsc = new ProblemMatcherDefinition();
            tsc.Name = "$tsc"; tsc.Owner = "typescript"; tsc.Source = "ts";
            tsc.FileLocation = "relative"; tsc.FileLocationBase = "${cwd}";
            tsc.Patterns.Add(new ProblemPattern(@"^([^\s].*)[\(:](\d+)[,:](\d+)(?:\):\s+|\s+-\s+)(error|warning|info)\s+TS(\d+)\s*:\s*(.*)$", 1, 2, 3, 4, 5, 6));
            m[tsc.Name] = tsc;

            ProblemMatcherDefinition gcc = new ProblemMatcherDefinition();
            gcc.Name = "$gcc"; gcc.Owner = "cpp"; gcc.Source = "gcc";
            gcc.FileLocation = "autoDetect"; gcc.FileLocationBase = "${cwd}";
            gcc.Patterns.Add(new ProblemPattern(@"^(.*?):(\d+):(\d*):?\s+(?:fatal\s+)?(warning|error|note):\s+(.*)$", 1, 2, 3, 4, 0, 5));
            m[gcc.Name] = gcc;

            ProblemMatcherDefinition ms = new ProblemMatcherDefinition();
            ms.Name = "$msCompile"; ms.Owner = "msCompile"; ms.Source = "dotnet";
            ms.FileLocation = "autoDetect"; ms.FileLocationBase = "${cwd}";
            ProblemPattern msp = new ProblemPattern(@"^\s*(?:\d+>)?(\S.*?)\((\d+|\d+,\d+|\d+,\d+,\d+,\d+)\)\s*:\s+((?:fatal +)?error|warning|info)\s+(\w+\d+)\s*:\s*(.*?)(?:\s+\[[^\]]+\])?$", 1, 0, 0, 3, 4, 5);
            msp.Location = 2;
            ms.Patterns.Add(msp);
            m[ms.Name] = ms;
            m["$dotnet"] = ms;

            ProblemMatcherDefinition es = new ProblemMatcherDefinition();
            es.Name = "$eslint-stylish"; es.Owner = "eslint"; es.Source = "eslint";
            es.FileLocation = "absolute"; es.FileLocationBase = "${cwd}";
            es.Patterns.Add(new ProblemPattern(@"^([^\s].*)$", 1, 0, 0, 0, 0, 0));
            ProblemPattern es2 = new ProblemPattern(@"^\s+(\d+):(\d+)\s+(error|warning|info)\s+(.+?)(?:\s\s+(.*))?$", 0, 1, 2, 3, 5, 4);
            es2.Loop = true;
            es.Patterns.Add(es2);
            m[es.Name] = es;

            ProblemMatcherDefinition go = new ProblemMatcherDefinition();
            go.Name = "$go"; go.Owner = "go"; go.Source = "go";
            go.FileLocation = "relative"; go.FileLocationBase = "${cwd}";
            go.Patterns.Add(new ProblemPattern(@"^(?:[^:\s]*: )?((?:[A-Za-z]:)?[^:\s][^:]*\.go):(\d+)(?::(\d+))?: (.*)$", 1, 2, 3, 0, 0, 4));
            m[go.Name] = go;

            ProblemMatcherDefinition rs = new ProblemMatcherDefinition();
            rs.Name = "$rustc"; rs.Owner = "rustc"; rs.Source = "rustc";
            rs.FileLocation = "relative"; rs.FileLocationBase = "${cwd}";
            rs.Patterns.Add(new ProblemPattern(@"^(warning|warn|error)(?:\[(.*?)\])?: (.*)$", 0, 0, 0, 1, 2, 3));
            rs.Patterns.Add(new ProblemPattern(@"^[\s\->=]*(.*?):(\d*):(\d*)\s*$", 1, 2, 3, 0, 0, 0));
            m[rs.Name] = rs;
            m["$rustc-json"] = rs;

            ProblemMatcherDefinition py = new ProblemMatcherDefinition();
            py.Name = "$python"; py.Owner = "python"; py.Source = "python";
            py.FileLocation = "autoDetect"; py.FileLocationBase = "${cwd}";
            py.IsPythonTraceback = true;
            m[py.Name] = py;
            m["$pytest"] = py;
            return m;
        }

        /// <summary>
        /// Resolves a tasks.json "problemMatcher" value (name or object with optional "base") into definitions.
        /// Unknown names are ignored.
        /// </summary>
        public static List<ProblemMatcherDefinition> Resolve(List<object> specs)
        {
            List<ProblemMatcherDefinition> res = new List<ProblemMatcherDefinition>();
            if (specs == null) return res;
            foreach (object s in specs)
            {
                string name = s as string;
                if (name != null) { ProblemMatcherDefinition d = Get(name); if (d != null) res.Add(d); continue; }
                Dictionary<string, object> obj = s as Dictionary<string, object>;
                if (obj == null) continue;
                try
                {
                    ProblemMatcherDefinition custom = FromJson(obj);
                    if (custom != null) res.Add(custom);
                }
                catch { }
            }
            return res;
        }

        private static int G(Dictionary<string, object> p, string key)
        {
            return DapJson.Int(p, key, 0);
        }

        public static ProblemMatcherDefinition FromJson(Dictionary<string, object> obj)
        {
            ProblemMatcherDefinition baseDef = Get(DapJson.Str(obj, "base"));
            ProblemMatcherDefinition d = new ProblemMatcherDefinition();
            if (baseDef != null)
            {
                d.Name = baseDef.Name; d.Owner = baseDef.Owner; d.Source = baseDef.Source;
                d.Patterns.AddRange(baseDef.Patterns); d.FileLocation = baseDef.FileLocation; d.FileLocationBase = baseDef.FileLocationBase;
                d.IsPythonTraceback = baseDef.IsPythonTraceback; d.DefaultSeverity = baseDef.DefaultSeverity;
            }
            d.Owner = DapJson.Str(obj, "owner") ?? d.Owner;
            d.Source = DapJson.Str(obj, "source") ?? (string.IsNullOrEmpty(d.Source) ? d.Owner : d.Source);
            string sev = DapJson.Str(obj, "severity");
            if (!string.IsNullOrEmpty(sev)) d.DefaultSeverity = NormalizeSeverity(sev, "Error");
            object fl = obj.ContainsKey("fileLocation") ? obj["fileLocation"] : null;
            if (fl is string) { d.FileLocation = (string)fl; d.FileLocationBase = "${workspaceFolder}"; }
            else if (fl is List<object>)
            {
                List<object> l = (List<object>)fl;
                if (l.Count > 0) d.FileLocation = l[0] as string ?? d.FileLocation;
                if (l.Count > 1 && l[1] is string) d.FileLocationBase = (string)l[1];
                else d.FileLocationBase = "${workspaceFolder}";
            }
            object pat = obj.ContainsKey("pattern") ? obj["pattern"] : null;
            List<object> pats = null;
            if (pat is Dictionary<string, object>) { pats = new List<object>(); pats.Add(pat); }
            else if (pat is List<object>) pats = (List<object>)pat;
            else if (pat is string)
            {
                ProblemMatcherDefinition named = Get((string)pat);
                if (named != null) { d.Patterns.Clear(); d.Patterns.AddRange(named.Patterns); }
            }
            if (pats != null)
            {
                d.Patterns.Clear();
                foreach (object po in pats)
                {
                    Dictionary<string, object> p = po as Dictionary<string, object>;
                    if (p == null) continue;
                    string rx = DapJson.Str(p, "regexp");
                    if (string.IsNullOrEmpty(rx)) continue;
                    bool hasFile = DapJson.Has(p, "file");
                    bool hasLine = DapJson.Has(p, "line");
                    bool hasMsg = DapJson.Has(p, "message");
                    // VS Code defaults for the first pattern: file 1, line 2, column 3, message 0 (= 5 when absent? use explicit only)
                    ProblemPattern pp = new ProblemPattern(rx,
                        hasFile ? G(p, "file") : (pats.Count == 1 ? 1 : 0),
                        hasLine ? G(p, "line") : (pats.Count == 1 ? 2 : 0),
                        DapJson.Has(p, "column") ? G(p, "column") : (pats.Count == 1 ? 3 : 0),
                        G(p, "severity"), G(p, "code"),
                        hasMsg ? G(p, "message") : (pats.Count == 1 ? 4 : 0));
                    pp.Location = G(p, "location");
                    pp.EndLine = G(p, "endLine");
                    pp.EndColumn = G(p, "endColumn");
                    pp.Loop = DapJson.Bool(p, "loop", false);
                    d.Patterns.Add(pp);
                }
            }
            if (d.Patterns.Count == 0 && !d.IsPythonTraceback) return null;
            if (string.IsNullOrEmpty(d.Name)) d.Name = string.IsNullOrEmpty(d.Owner) ? "custom" : d.Owner;
            return d;
        }

        public static string NormalizeSeverity(string s, string fallback)
        {
            string t = (s ?? "").Trim().ToLowerInvariant();
            if (t.Length == 0) return fallback;
            if (t.IndexOf("error", StringComparison.Ordinal) >= 0 || t == "fatal" || t == "err") return "Error";
            if (t.StartsWith("warn", StringComparison.Ordinal)) return "Warning";
            if (t == "info" || t == "information" || t == "note" || t == "hint") return "Info";
            return fallback;
        }
    }

    /// <summary>
    /// Feeds task output lines through a set of matchers and collects problems as <see cref="DiagnosticItem"/>s
    /// (1-based line / column, absolute file paths). Lines must be ANSI-free (see <see cref="TaskOutputLineSplitter"/>).
    /// </summary>
    public sealed class ProblemMatcherEngine
    {
        private sealed class MatcherState
        {
            public ProblemMatcherDefinition Def;
            public int NextPattern;
            public Dictionary<string, string> Data = new Dictionary<string, string>(StringComparer.Ordinal);
            // Python traceback state
            public string PyFile;
            public int PyLine;
            public bool PyInTraceback;
        }

        private readonly List<MatcherState> states = new List<MatcherState>();
        private readonly List<DiagnosticItem> problems = new List<DiagnosticItem>();
        private readonly HashSet<string> seen = new HashSet<string>(StringComparer.Ordinal);
        private readonly string workspaceFolder;
        private readonly string cwd;

        public ProblemMatcherEngine(IEnumerable<ProblemMatcherDefinition> matchers, string workspaceFolder, string cwd)
        {
            this.workspaceFolder = workspaceFolder ?? "";
            this.cwd = string.IsNullOrEmpty(cwd) ? this.workspaceFolder : cwd;
            if (matchers != null)
                foreach (ProblemMatcherDefinition d in matchers)
                {
                    if (d == null) continue;
                    MatcherState st = new MatcherState();
                    st.Def = d;
                    states.Add(st);
                }
        }

        public bool HasMatchers { get { return states.Count > 0; } }
        public List<DiagnosticItem> Problems { get { return problems; } }
        /// <summary>Raised for every new problem (on the feeding thread).</summary>
        public event Action<DiagnosticItem> ProblemFound;

        public void FeedLine(string line)
        {
            if (line == null) return;
            if (line.Length > 4000) line = line.Substring(0, 4000);
            foreach (MatcherState st in states)
            {
                if (st.Def.IsPythonTraceback) { FeedPython(st, line); continue; }
                FeedRegex(st, line);
            }
        }

        public void FeedLines(IEnumerable<string> lines)
        {
            foreach (string l in lines) FeedLine(l);
        }

        private void FeedRegex(MatcherState st, string line)
        {
            List<ProblemPattern> pats = st.Def.Patterns;
            if (pats.Count == 0) return;
            if (pats.Count == 1)
            {
                Match m = pats[0].Regexp.Match(line);
                if (m.Success)
                {
                    Dictionary<string, string> data = new Dictionary<string, string>(StringComparer.Ordinal);
                    Capture(pats[0], m, data);
                    Emit(st.Def, data);
                }
                return;
            }
            int last = pats.Count - 1;
            // Loop on the last pattern: keep emitting while lines match it.
            if (st.NextPattern == last && pats[last].Loop && st.Data.Count > 0)
            {
                Match lm = pats[last].Regexp.Match(line);
                if (lm.Success)
                {
                    Dictionary<string, string> data = new Dictionary<string, string>(st.Data, StringComparer.Ordinal);
                    Capture(pats[last], lm, data);
                    Emit(st.Def, data);
                    return;
                }
                st.NextPattern = 0;
                st.Data.Clear();
            }
            if (st.NextPattern > 0)
            {
                Match nm = pats[st.NextPattern].Regexp.Match(line);
                if (nm.Success)
                {
                    Capture(pats[st.NextPattern], nm, st.Data);
                    if (st.NextPattern == last)
                    {
                        if (pats[last].Loop)
                        {
                            Dictionary<string, string> data = new Dictionary<string, string>(st.Data, StringComparer.Ordinal);
                            Emit(st.Def, data);
                            // keep base data (without loop fields) for further loop lines
                            foreach (string k in new string[] { "line", "column", "endLine", "endColumn", "severity", "code", "message", "location" }) st.Data.Remove(k);
                            Capture(pats[0], null, st.Data);
                            return;
                        }
                        Emit(st.Def, st.Data);
                        st.NextPattern = 0;
                        st.Data = new Dictionary<string, string>(StringComparer.Ordinal);
                        return;
                    }
                    st.NextPattern++;
                    return;
                }
                st.NextPattern = 0;
                st.Data = new Dictionary<string, string>(StringComparer.Ordinal);
            }
            Match fm = pats[0].Regexp.Match(line);
            if (fm.Success)
            {
                st.Data = new Dictionary<string, string>(StringComparer.Ordinal);
                Capture(pats[0], fm, st.Data);
                st.NextPattern = 1;
            }
        }

        private static void Capture(ProblemPattern p, Match m, Dictionary<string, string> data)
        {
            if (m == null) return;
            Put(data, "file", p.File, m);
            Put(data, "line", p.Line, m);
            Put(data, "column", p.Column, m);
            Put(data, "endLine", p.EndLine, m);
            Put(data, "endColumn", p.EndColumn, m);
            Put(data, "location", p.Location, m);
            Put(data, "severity", p.Severity, m);
            Put(data, "code", p.Code, m);
            Put(data, "message", p.Message, m);
        }

        private static void Put(Dictionary<string, string> data, string key, int group, Match m)
        {
            if (group <= 0 || group >= m.Groups.Count) return;
            Group g = m.Groups[group];
            if (!g.Success) return;
            data[key] = g.Value;
        }

        private static readonly Regex PyFileLine = new Regex(@"^\s*File ""(.+)"", line (\d+)(?:, in .*)?\s*$", RegexOptions.Compiled);
        private static readonly Regex PyException = new Regex(@"^([A-Za-z_][\w.]*(?:Error|Exception|Warning|Exit|Interrupt|Failure|Iteration))(?::\s*(.*))?$", RegexOptions.Compiled);
        private static readonly Regex PytestShort = new Regex(@"^([^\s:][^:]*\.py):(\d+): (?:in \S+\s*)?([A-Za-z_][\w.]*(?:Error|Exception|Failure|Failed))\b(.*)$", RegexOptions.Compiled);
        private static readonly Regex PytestSummary = new Regex(@"^(?:FAILED|ERROR) ([^\s:]+\.py)(?:::\S+)?(?: - (.*))?$", RegexOptions.Compiled);

        private void FeedPython(MatcherState st, string line)
        {
            if (line.StartsWith("Traceback (most recent call last)", StringComparison.Ordinal)) { st.PyInTraceback = true; st.PyFile = null; return; }
            Match fl = PyFileLine.Match(line);
            if (fl.Success)
            {
                st.PyFile = fl.Groups[1].Value;
                st.PyLine = int.Parse(fl.Groups[2].Value, CultureInfo.InvariantCulture);
                st.PyInTraceback = true;
                return;
            }
            Match ps = PytestShort.Match(line);
            if (ps.Success)
            {
                Dictionary<string, string> d = new Dictionary<string, string>(StringComparer.Ordinal);
                d["file"] = ps.Groups[1].Value;
                d["line"] = ps.Groups[2].Value;
                d["message"] = (ps.Groups[3].Value + ps.Groups[4].Value).Trim();
                d["severity"] = "error";
                Emit(st.Def, d);
                st.PyInTraceback = false; st.PyFile = null;
                return;
            }
            if (st.PyInTraceback && st.PyFile != null)
            {
                string t = line.Trim();
                if (t.StartsWith("E ", StringComparison.Ordinal)) t = t.Substring(2).Trim(); // pytest "E   ValueError: x"
                Match ex = PyException.Match(t);
                if (ex.Success && line.Length > 0 && (line[0] != ' ' || line.TrimStart().StartsWith("E ", StringComparison.Ordinal)))
                {
                    Dictionary<string, string> d = new Dictionary<string, string>(StringComparer.Ordinal);
                    d["file"] = st.PyFile;
                    d["line"] = st.PyLine.ToString(CultureInfo.InvariantCulture);
                    d["message"] = ex.Groups[2].Success && ex.Groups[2].Value.Length > 0 ? ex.Groups[1].Value + ": " + ex.Groups[2].Value : ex.Groups[1].Value;
                    d["severity"] = ex.Groups[1].Value.EndsWith("Warning", StringComparison.Ordinal) ? "warning" : "error";
                    Emit(st.Def, d);
                    st.PyInTraceback = false; st.PyFile = null;
                }
            }
        }

        private void Emit(ProblemMatcherDefinition def, Dictionary<string, string> data)
        {
            string file;
            if (!data.TryGetValue("file", out file) || string.IsNullOrEmpty(file)) return;
            file = file.Trim();
            int line = ParseInt(data, "line", 0), col = ParseInt(data, "column", 0), endLine = ParseInt(data, "endLine", 0), endCol = ParseInt(data, "endColumn", 0);
            string loc;
            if (data.TryGetValue("location", out loc) && !string.IsNullOrEmpty(loc))
            {
                string[] parts = loc.Split(',');
                int v;
                if (parts.Length >= 1 && int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out v)) line = v;
                if (parts.Length >= 2 && int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out v)) col = v;
                if (parts.Length >= 4 && int.TryParse(parts[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out v)) endLine = v;
                if (parts.Length >= 4 && int.TryParse(parts[3], NumberStyles.Integer, CultureInfo.InvariantCulture, out v)) endCol = v;
            }
            string sevRaw;
            data.TryGetValue("severity", out sevRaw);
            string sev = BuiltInProblemMatchers.NormalizeSeverity(sevRaw, def.DefaultSeverity);
            string msg;
            if (!data.TryGetValue("message", out msg)) msg = "";
            string code;
            data.TryGetValue("code", out code);

            DiagnosticItem d = new DiagnosticItem();
            d.FilePath = ResolvePath(def, file);
            d.RelativePath = RelativeToWorkspace(d.FilePath);
            d.Line = Math.Max(1, line);
            d.Column = Math.Max(1, col);
            d.EndLine = endLine;
            d.EndColumn = endCol;
            d.Length = endLine == line && endCol > col ? endCol - col : 4;
            d.Severity = sev;
            d.Message = (msg ?? "").Trim();
            d.Code = string.IsNullOrEmpty(code) ? null : code;
            d.Source = string.IsNullOrEmpty(def.Source) ? def.Owner : def.Source;
            d.IsTaskProblem = true;
            string key = d.FilePath + "|" + d.Line + "|" + d.Column + "|" + d.Severity + "|" + d.Message;
            if (!seen.Add(key)) return;
            problems.Add(d);
            Action<DiagnosticItem> h = ProblemFound;
            if (h != null) { try { h(d); } catch { } }
        }

        private static int ParseInt(Dictionary<string, string> data, string key, int fallback)
        {
            string s;
            int v;
            if (data.TryGetValue(key, out s) && int.TryParse((s ?? "").Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out v)) return v;
            return fallback;
        }

        public string ResolvePath(ProblemMatcherDefinition def, string file)
        {
            string f = file.Replace('/', Path.DirectorySeparatorChar);
            if (Path.DirectorySeparatorChar == '\\') f = f.Replace('/', '\\');
            string baseDir = (def.FileLocationBase ?? "${workspaceFolder}").Replace("${workspaceFolder}", workspaceFolder).Replace("${workspaceRoot}", workspaceFolder).Replace("${cwd}", cwd);
            if (string.IsNullOrEmpty(baseDir)) baseDir = cwd;
            try
            {
                if (def.FileLocation == "absolute")
                    return Path.IsPathRooted(f) ? Path.GetFullPath(f) : Path.GetFullPath(Path.Combine(baseDir, f));
                if (def.FileLocation == "relative")
                    return Path.IsPathRooted(f) ? Path.GetFullPath(f) : Path.GetFullPath(Path.Combine(baseDir, f));
                // autoDetect: absolute as-is, relative against the base, then the workspace folder.
                if (Path.IsPathRooted(f)) return Path.GetFullPath(f);
                string a = Path.GetFullPath(Path.Combine(baseDir, f));
                if (File.Exists(a) || string.IsNullOrEmpty(workspaceFolder)) return a;
                string b = Path.GetFullPath(Path.Combine(workspaceFolder, f));
                return File.Exists(b) ? b : a;
            }
            catch { return f; }
        }

        private string RelativeToWorkspace(string full)
        {
            if (string.IsNullOrEmpty(workspaceFolder) || string.IsNullOrEmpty(full)) return full ?? "";
            string root = workspaceFolder.TrimEnd('/', '\\');
            if (full.Length > root.Length + 1 && full.StartsWith(root, StringComparison.OrdinalIgnoreCase) && (full[root.Length] == '/' || full[root.Length] == '\\'))
                return full.Substring(root.Length + 1);
            return full;
        }
    }

    /// <summary>Extra diagnostic data for problems produced by task problem matchers.</summary>
    public partial class DiagnosticItem
    {
        public bool IsTaskProblem;
    }

    /// <summary>
    /// Task problems per task label and file. Merged into editor buffers (squiggles) by
    /// <see cref="LspDiagnosticsStore.MergeInto"/> and listed in the Problems panel for files that are not open.
    /// Re-running a task clears its previous problems (VS Code behaviour).
    /// </summary>
    public static class TaskProblemStore
    {
        private static readonly object sync = new object();
        private static readonly Dictionary<string, List<DiagnosticItem>> byOwner = new Dictionary<string, List<DiagnosticItem>>(StringComparer.Ordinal);
        private static int version;

        public static int Version { get { lock (sync) return version; } }

        /// <summary>Raised (on the calling thread) whenever the store changes.</summary>
        public static event Action Changed;

        public static void ClearOwner(string owner)
        {
            bool changed;
            lock (sync) { changed = byOwner.Remove(owner ?? ""); if (changed) version++; }
            if (changed) Raise();
        }

        public static void ClearAll()
        {
            lock (sync) { byOwner.Clear(); version++; }
            Raise();
        }

        public static void Set(string owner, List<DiagnosticItem> items)
        {
            lock (sync)
            {
                if (items == null || items.Count == 0) byOwner.Remove(owner ?? "");
                else byOwner[owner ?? ""] = new List<DiagnosticItem>(items);
                version++;
            }
            Raise();
        }

        private static void Raise()
        {
            Action h = Changed;
            if (h != null) { try { h(); } catch { } }
        }

        public static List<DiagnosticItem> Get(string filePath)
        {
            List<DiagnosticItem> res = new List<DiagnosticItem>();
            if (string.IsNullOrEmpty(filePath)) return res;
            string key = LspText.PathKey(filePath);
            lock (sync)
            {
                foreach (List<DiagnosticItem> l in byOwner.Values)
                    foreach (DiagnosticItem d in l)
                        if (LspText.PathKey(d.FilePath) == key) res.Add(d);
            }
            return res;
        }

        public static List<DiagnosticItem> All()
        {
            List<DiagnosticItem> res = new List<DiagnosticItem>();
            lock (sync) foreach (List<DiagnosticItem> l in byOwner.Values) res.AddRange(l);
            return res;
        }

        public static int Count
        {
            get { lock (sync) { int n = 0; foreach (List<DiagnosticItem> l in byOwner.Values) n += l.Count; return n; } }
        }
    }

    /// <summary>
    /// Turns a raw terminal / pipe byte stream into clean text lines for problem matching: incremental UTF-8
    /// decoding, ANSI escape sequence removal (CSI, OSC, charset selections), CR handling.
    /// </summary>
    public sealed class TaskOutputLineSplitter
    {
        private readonly Decoder decoder = new UTF8Encoding(false).GetDecoder();
        private readonly StringBuilder line = new StringBuilder();
        private int escState; // 0 text, 1 after ESC, 2 CSI, 3 OSC, 4 OSC after ESC, 5 charset
        private readonly object gate = new object();

        public event Action<string> LineReady;

        public void FeedBytes(byte[] buf, int count)
        {
            if (buf == null || count <= 0) return;
            char[] chars;
            int n;
            lock (gate)
            {
                chars = new char[decoder.GetCharCount(buf, 0, count)];
                n = decoder.GetChars(buf, 0, count, chars, 0);
            }
            FeedChars(chars, n);
        }

        public void FeedText(string s)
        {
            if (string.IsNullOrEmpty(s)) return;
            FeedChars(s.ToCharArray(), s.Length);
        }

        private void FeedChars(char[] chars, int n)
        {
            List<string> ready = null;
            lock (gate)
            {
                for (int i = 0; i < n; i++)
                {
                    char c = chars[i];
                    switch (escState)
                    {
                        case 1:
                            if (c == '[') escState = 2;
                            else if (c == ']') escState = 3;
                            else if (c == '(' || c == ')' || c == '*' || c == '+') escState = 5;
                            else escState = 0;
                            continue;
                        case 2:
                            if (c >= '@' && c <= '~') escState = 0;
                            continue;
                        case 3:
                            if (c == '\a') escState = 0;
                            else if (c == '\x1b') escState = 4;
                            continue;
                        case 4:
                            escState = c == '\\' ? 0 : 3;
                            continue;
                        case 5:
                            escState = 0;
                            continue;
                    }
                    if (c == '\x1b') { escState = 1; continue; }
                    if (c == '\n')
                    {
                        if (ready == null) ready = new List<string>();
                        ready.Add(line.ToString());
                        line.Length = 0;
                        continue;
                    }
                    if (c == '\r') continue; // CRLF and progress redraws: keep the text, matchers see the final line
                    if (c == '\b') { if (line.Length > 0) line.Length--; continue; }
                    if (c < ' ' && c != '\t') continue;
                    line.Append(c);
                    if (line.Length > 16000) { if (ready == null) ready = new List<string>(); ready.Add(line.ToString()); line.Length = 0; }
                }
            }
            if (ready != null)
            {
                Action<string> h = LineReady;
                if (h != null) foreach (string l in ready) { try { h(l); } catch { } }
            }
        }

        /// <summary>Emits the unterminated last line (call when the process exits).</summary>
        public void Flush()
        {
            string rest;
            lock (gate) { rest = line.ToString(); line.Length = 0; }
            if (rest.Length == 0) return;
            Action<string> h = LineReady;
            if (h != null) { try { h(rest); } catch { } }
        }
    }
}
