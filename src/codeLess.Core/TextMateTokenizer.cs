using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;

namespace codeLess.Studio
{
    public enum TmRuleKind { Match, BeginEnd, BeginWhile, IncludeOnly, Capture }

    /// <summary>A compiled TextMate rule (one per raw rule per root grammar).</summary>
    public sealed class TmRule
    {
        public int Id;
        public TmRuleKind Kind;
        public string Name;
        public string ContentName;
        public bool NameHasCaptures;
        public bool ContentNameHasCaptures;
        public TmRegex Match;          // Match rule pattern or Begin pattern
        public TmRegex End;            // end / while pattern without back-references
        public string EndSource;       // end / while source (may contain back-references)
        public bool EndHasBackRefs;
        public bool ApplyEndPatternLast;
        public TmRule[] Captures;      // Match: captures; Begin*: beginCaptures
        public TmRule[] EndCaptures;   // end or while captures
        internal TmRawRule Raw;
        internal TmCompiledGrammar Owner;
        private List<TmRule> patterns;
        private bool patternsResolved;
        internal List<TmRule> FlatCandidates;
        private Dictionary<string, TmRegex> resolvedEnds;

        public List<TmRule> Patterns
        {
            get
            {
                if (!patternsResolved)
                {
                    patternsResolved = true;
                    patterns = Owner.ResolvePatterns(Raw);
                }
                return patterns;
            }
        }

        public bool HasPatterns { get { return Patterns != null && Patterns.Count > 0; } }

        internal TmRegex GetEndRegex(string resolvedEndSource)
        {
            if (resolvedEndSource == null) return End;
            if (resolvedEnds == null) resolvedEnds = new Dictionary<string, TmRegex>(StringComparer.Ordinal);
            TmRegex r;
            if (!resolvedEnds.TryGetValue(resolvedEndSource, out r))
            {
                if (resolvedEnds.Count > 128) resolvedEnds.Clear();
                r = new TmRegex(resolvedEndSource);
                resolvedEnds[resolvedEndSource] = r;
            }
            return r;
        }

        private static readonly Regex CaptureRef = new Regex(@"\$(\d+)|\$\{(\d+):/(downcase|upcase)\}", RegexOptions.CultureInvariant);
        private static readonly Regex BackRef = new Regex(@"\\(\d+)", RegexOptions.CultureInvariant);

        internal static bool HasCaptureRefs(string s) { return s != null && s.IndexOf('$') >= 0 && CaptureRef.IsMatch(s); }
        internal static bool HasBackRefs(string s) { return s != null && BackRef.IsMatch(s); }

        internal static string SubstituteCaptures(string name, string line, Match m)
        {
            if (name == null || m == null) return name;
            return CaptureRef.Replace(name, delegate (Match r)
            {
                string num = r.Groups[1].Success ? r.Groups[1].Value : r.Groups[2].Value;
                int idx;
                if (!int.TryParse(num, out idx) || idx >= m.Groups.Count || !m.Groups[idx].Success) return "";
                string v = m.Groups[idx].Value;
                while (v.Length > 0 && v[0] == '.') v = v.Substring(1);
                if (r.Groups[3].Success) v = r.Groups[3].Value == "downcase" ? v.ToLowerInvariant() : v.ToUpperInvariant();
                return v;
            });
        }

        internal static string ResolveBackRefs(string source, string line, Match m)
        {
            return BackRef.Replace(source, delegate (Match r)
            {
                int idx;
                if (!int.TryParse(r.Groups[1].Value, out idx) || m == null || idx >= m.Groups.Count || !m.Groups[idx].Success) return "";
                return OnigurumaTranslator.EscapeLiteral(m.Groups[idx].Value);
            });
        }

        public string GetName(string line, Match m) { return NameHasCaptures ? SubstituteCaptures(Name, line, m) : Name; }
        public string GetContentName(string line, Match m) { return ContentNameHasCaptures ? SubstituteCaptures(ContentName, line, m) : ContentName; }
    }

    /// <summary>Immutable scope path (outermost first). Equal paths built from the same root are the same object.</summary>
    public sealed class TmScopeStack
    {
        public readonly TmScopeStack Parent;
        public readonly string Scope;
        public readonly int Depth;
        private Dictionary<string, TmScopeStack> children;
        internal object StyleOwner;
        internal TmTokenStyle Style;

        public TmScopeStack(TmScopeStack parent, string scope)
        {
            Parent = parent;
            Scope = scope;
            Depth = parent == null ? 0 : parent.Depth + 1;
        }

        /// <summary>Pushes one or more space-separated scope names.</summary>
        public TmScopeStack Push(string scopes)
        {
            if (string.IsNullOrEmpty(scopes)) return this;
            if (scopes.IndexOf(' ') < 0) return PushOne(scopes);
            TmScopeStack s = this;
            foreach (string part in scopes.Split(' '))
                if (part.Length > 0) s = s.PushOne(part);
            return s;
        }

        private TmScopeStack PushOne(string scope)
        {
            lock (this)
            {
                if (children == null) children = new Dictionary<string, TmScopeStack>(StringComparer.Ordinal);
                TmScopeStack c;
                if (!children.TryGetValue(scope, out c))
                {
                    c = new TmScopeStack(this, scope);
                    if (children.Count < 512) children[scope] = c;
                }
                return c;
            }
        }

        /// <summary>Scope names, outermost first.</summary>
        public List<string> ToList()
        {
            List<string> l = new List<string>();
            for (TmScopeStack s = this; s != null; s = s.Parent) if (s.Scope != null) l.Add(s.Scope);
            l.Reverse();
            return l;
        }

        public override string ToString() { return string.Join(" ", ToList().ToArray()); }

        public static bool Same(TmScopeStack a, TmScopeStack b)
        {
            while (true)
            {
                if (ReferenceEquals(a, b)) return true;
                if (a == null || b == null || a.Depth != b.Depth || !string.Equals(a.Scope, b.Scope, StringComparison.Ordinal)) return false;
                a = a.Parent;
                b = b.Parent;
            }
        }
    }

    /// <summary>Immutable TextMate rule stack (the per-line tokenizer state).</summary>
    public sealed class TmStateStack
    {
        public readonly TmStateStack Parent;
        public readonly TmRule Rule;
        public readonly int EnterPos;
        public readonly int AnchorPos;
        public readonly bool BeginRuleCapturedEOL;
        public readonly string EndRule;
        public readonly TmScopeStack NameScopes;
        public readonly TmScopeStack ContentNameScopes;
        public readonly int Depth;
        private TmStateStack resetCache;

        public TmStateStack(TmStateStack parent, TmRule rule, int enterPos, int anchorPos, bool capturedEol, string endRule, TmScopeStack nameScopes, TmScopeStack contentNameScopes)
        {
            Parent = parent;
            Rule = rule;
            EnterPos = enterPos;
            AnchorPos = anchorPos;
            BeginRuleCapturedEOL = capturedEol;
            EndRule = endRule;
            NameScopes = nameScopes;
            ContentNameScopes = contentNameScopes;
            Depth = parent == null ? 1 : parent.Depth + 1;
        }

        public TmStateStack Push(TmRule rule, int enterPos, int anchorPos, bool capturedEol, string endRule, TmScopeStack nameScopes, TmScopeStack contentNameScopes)
        {
            return new TmStateStack(this, rule, enterPos, anchorPos, capturedEol, endRule, nameScopes, contentNameScopes);
        }

        public TmStateStack WithContentNameScopes(TmScopeStack s)
        {
            if (ReferenceEquals(s, ContentNameScopes)) return this;
            return new TmStateStack(Parent, Rule, EnterPos, AnchorPos, BeginRuleCapturedEOL, EndRule, NameScopes, s);
        }

        public TmStateStack WithEndRule(string endRule)
        {
            if (string.Equals(endRule, EndRule, StringComparison.Ordinal)) return this;
            return new TmStateStack(Parent, Rule, EnterPos, AnchorPos, BeginRuleCapturedEOL, endRule, NameScopes, ContentNameScopes);
        }

        public TmStateStack Pop() { return Parent; }
        public TmStateStack SafePop() { return Parent ?? this; }

        /// <summary>Same stack with enter/anchor positions cleared (positions are only meaningful within one line).</summary>
        public TmStateStack ResetPositions()
        {
            if (EnterPos == -1 && AnchorPos == -1 && (Parent == null || ReferenceEquals(Parent.ResetPositions(), Parent))) return this;
            if (resetCache != null) return resetCache;
            TmStateStack p = Parent == null ? null : Parent.ResetPositions();
            resetCache = new TmStateStack(p, Rule, -1, -1, BeginRuleCapturedEOL, EndRule, NameScopes, ContentNameScopes);
            return resetCache;
        }

        public bool HasSameRuleAs(TmStateStack other)
        {
            TmStateStack el = this;
            while (el != null && el.EnterPos == other.EnterPos)
            {
                if (ReferenceEquals(el.Rule, other.Rule)) return true;
                el = el.Parent;
            }
            return false;
        }

        /// <summary>Structural equality used for incremental re-tokenization convergence.</summary>
        public static bool Same(TmStateStack a, TmStateStack b)
        {
            while (true)
            {
                if (ReferenceEquals(a, b)) return true;
                if (a == null || b == null || a.Depth != b.Depth || !ReferenceEquals(a.Rule, b.Rule)) return false;
                if (a.BeginRuleCapturedEOL != b.BeginRuleCapturedEOL) return false;
                if (!string.Equals(a.EndRule, b.EndRule, StringComparison.Ordinal)) return false;
                if (!TmScopeStack.Same(a.NameScopes, b.NameScopes) || !TmScopeStack.Same(a.ContentNameScopes, b.ContentNameScopes)) return false;
                a = a.Parent;
                b = b.Parent;
            }
        }
    }

    /// <summary>Tokens of one line: token i spans [Starts[i], Starts[i+1]) (last ends at the line length).</summary>
    public sealed class TmLineTokens
    {
        public int[] Starts;
        public TmScopeStack[] Scopes;
        public int Length;
        public bool StoppedEarly;

        public int Count { get { return Starts == null ? 0 : Starts.Length; } }
        public int EndOf(int i) { return i + 1 < Starts.Length ? Starts[i + 1] : Length; }
    }

    public sealed class TmLineResult
    {
        public TmLineTokens Tokens;
        public TmStateStack EndState;
    }

    /// <summary>
    /// Compiles a root grammar (plus grammars it includes through the registry) into rules and tokenizes lines.
    /// Algorithm follows vscode-textmate: lines get a trailing "\n", the end pattern is tried before (or with
    /// applyEndPatternLast after) the rule's patterns, the leftmost match wins (ties: earliest pattern), while-rules
    /// are checked at each line start, captures with patterns are re-tokenized, and an empty match that neither
    /// pushes nor pops stops the line (no infinite loops).
    /// </summary>
    public sealed class TmCompiledGrammar
    {
        public readonly TmRawGrammar RawGrammar;
        public readonly TmRule RootRule;
        public readonly TmScopeStack RootScopes;
        private readonly Func<string, TmRawGrammar> externalLookup;
        private readonly Dictionary<TmRawRule, TmRule> compiled = new Dictionary<TmRawRule, TmRule>();
        private readonly Dictionary<string, TmRawGrammar> externalCache = new Dictionary<string, TmRawGrammar>(StringComparer.Ordinal);
        private int nextRuleId;

        /// <summary>Per-line wall-clock budget; the rest of a line that exceeds it keeps the current scope.</summary>
        public int LineTimeBudgetMs = 500;
        /// <summary>Lines longer than this are not tokenized (VS Code's editor.maxTokenizationLineLength).</summary>
        public int MaxLineLength = 20000;

        public TmCompiledGrammar(TmRawGrammar grammar, Func<string, TmRawGrammar> externalGrammarLookup)
        {
            RawGrammar = grammar;
            externalLookup = externalGrammarLookup;
            RootRule = Compile(grammar.Root);
            RootScopes = new TmScopeStack(null, grammar.ScopeName);
        }

        public TmStateStack InitialState
        {
            get
            {
                if (initial == null) initial = new TmStateStack(null, RootRule, -1, -1, false, null, RootScopes, RootScopes);
                return initial;
            }
        }
        private TmStateStack initial;

        internal TmRule Compile(TmRawRule raw)
        {
            if (raw == null) return null;
            TmRule r;
            if (compiled.TryGetValue(raw, out r)) return r;
            r = new TmRule();
            r.Id = ++nextRuleId;
            r.Raw = raw;
            r.Owner = this;
            compiled[raw] = r;
            r.Name = raw.Name;
            r.ContentName = raw.ContentName;
            r.NameHasCaptures = TmRule.HasCaptureRefs(raw.Name);
            r.ContentNameHasCaptures = TmRule.HasCaptureRefs(raw.ContentName);
            if (raw.Match != null)
            {
                r.Kind = TmRuleKind.Match;
                r.Match = new TmRegex(raw.Match);
                r.Captures = CompileCaptures(raw.Captures);
            }
            else if (raw.Begin != null)
            {
                r.Match = new TmRegex(raw.Begin);
                r.Captures = CompileCaptures(raw.BeginCaptures);
                if (raw.While != null)
                {
                    r.Kind = TmRuleKind.BeginWhile;
                    r.EndSource = raw.While;
                    r.EndCaptures = CompileCaptures(raw.WhileCaptures);
                }
                else
                {
                    r.Kind = TmRuleKind.BeginEnd;
                    r.EndSource = raw.End ?? "\uFFFF";
                    r.EndCaptures = CompileCaptures(raw.EndCaptures);
                }
                r.EndHasBackRefs = TmRule.HasBackRefs(r.EndSource);
                if (!r.EndHasBackRefs) r.End = new TmRegex(r.EndSource);
                r.ApplyEndPatternLast = raw.ApplyEndPatternLast;
            }
            else
            {
                r.Kind = TmRuleKind.IncludeOnly;
            }
            return r;
        }

        private TmRule[] CompileCaptures(Dictionary<int, TmRawRule> caps)
        {
            if (caps == null || caps.Count == 0) return null;
            int max = 0;
            foreach (int k in caps.Keys) if (k > max) max = k;
            TmRule[] arr = new TmRule[max + 1];
            foreach (KeyValuePair<int, TmRawRule> kv in caps)
            {
                TmRawRule cr = kv.Value;
                TmRule c = new TmRule();
                c.Id = ++nextRuleId;
                c.Kind = TmRuleKind.Capture;
                c.Raw = cr;
                c.Owner = this;
                c.Name = cr.Name;
                c.ContentName = cr.ContentName;
                c.NameHasCaptures = TmRule.HasCaptureRefs(cr.Name);
                c.ContentNameHasCaptures = TmRule.HasCaptureRefs(cr.ContentName);
                arr[kv.Key] = c;
            }
            return arr;
        }

        /// <summary>Compiles the patterns (and include) of a raw rule into rule references.</summary>
        internal List<TmRule> ResolvePatterns(TmRawRule raw)
        {
            List<TmRule> res = new List<TmRule>();
            if (raw == null) return res;
            if (raw.Include != null && raw.Match == null && raw.Begin == null)
            {
                TmRule t = ResolveInclude(raw, raw.Include);
                if (t != null) res.Add(t);
            }
            if (raw.Patterns != null)
            {
                foreach (TmRawRule p in raw.Patterns)
                {
                    if (p.Include != null && p.Match == null && p.Begin == null && p.Patterns == null)
                    {
                        TmRule t = ResolveInclude(p, p.Include);
                        if (t != null) res.Add(t);
                    }
                    else res.Add(Compile(p));
                }
            }
            return res;
        }

        private TmRule ResolveInclude(TmRawRule from, string include)
        {
            if (string.IsNullOrEmpty(include)) return null;
            if (include == "$self") return Compile(from.Grammar.Root);
            if (include == "$base") return RootRule;
            if (include[0] == '#')
            {
                string name = include.Substring(1);
                for (TmRawRule r = from; r != null; r = r.Parent)
                {
                    TmRawRule hit;
                    if (r.Repository != null && r.Repository.TryGetValue(name, out hit)) return Compile(hit);
                }
                TmRawRule g = from.Grammar != null ? from.Grammar.LookupRepository(name) : null;
                return g == null ? null : Compile(g);
            }
            string scope = include;
            string repoName = null;
            int hash = include.IndexOf('#');
            if (hash > 0) { scope = include.Substring(0, hash); repoName = include.Substring(hash + 1); }
            TmRawGrammar ext = GetExternal(scope);
            if (ext == null) return null;
            if (repoName == null) return Compile(ext.Root);
            TmRawRule item = ext.LookupRepository(repoName);
            return item == null ? null : Compile(item);
        }

        private TmRawGrammar GetExternal(string scope)
        {
            if (string.Equals(scope, RawGrammar.ScopeName, StringComparison.Ordinal)) return RawGrammar;
            TmRawGrammar g;
            if (externalCache.TryGetValue(scope, out g)) return g;
            g = null;
            if (externalLookup != null)
            {
                try { g = externalLookup(scope); } catch { g = null; }
            }
            externalCache[scope] = g;
            return g;
        }

        // ------------------------------------------------------------------ scanning
        internal List<TmRule> GetCandidates(TmRule rule)
        {
            if (rule.FlatCandidates != null) return rule.FlatCandidates;
            List<TmRule> list = new List<TmRule>();
            HashSet<TmRule> visited = new HashSet<TmRule>();
            Flatten(rule.Patterns, list, visited, 0);
            rule.FlatCandidates = list;
            return list;
        }

        private void Flatten(List<TmRule> pats, List<TmRule> outList, HashSet<TmRule> visited, int depth)
        {
            if (pats == null || depth > 64) return;
            foreach (TmRule p in pats)
            {
                if (p == null) continue;
                if (p.Kind == TmRuleKind.IncludeOnly)
                {
                    if (!visited.Add(p)) continue;
                    Flatten(p.Patterns, outList, visited, depth + 1);
                }
                else if (p.Kind == TmRuleKind.Match || p.Kind == TmRuleKind.BeginEnd || p.Kind == TmRuleKind.BeginWhile)
                {
                    if (!outList.Contains(p)) outList.Add(p);
                }
            }
        }

        private sealed class SearchCache
        {
            public readonly Dictionary<int, int> From = new Dictionary<int, int>();
            public readonly Dictionary<int, Match> Result = new Dictionary<int, Match>();
        }

        private sealed class LineContext
        {
            public string Text;          // line + "\n"
            public int Length;           // Text.Length
            public long Deadline;
            public bool StoppedEarly;
            public SearchCache Cache = new SearchCache();
            public List<int> TokStarts = new List<int>();
            public List<TmScopeStack> TokScopes = new List<TmScopeStack>();
            public int LastEnd;
            public int Iterations;

            public void Produce(TmScopeStack scopes, int end)
            {
                if (end <= LastEnd) return;
                if (TokScopes.Count > 0 && ReferenceEquals(TokScopes[TokScopes.Count - 1], scopes)) { LastEnd = end; return; }
                TokStarts.Add(LastEnd);
                TokScopes.Add(scopes);
                LastEnd = end;
            }
        }

        /// <summary>Diagnostics: regex executions and their total Stopwatch ticks (all grammars).</summary>
        public static long RegexCalls, RegexTicks;

        private Match Search(LineContext ctx, TmRegex rx, int pos, bool allowA, bool allowG)
        {
            Regex net = rx.GetForSearch(allowA, allowG);
            if (net == null) return null;
            bool cacheable = !rx.HasG && !rx.HasA;
            if (cacheable)
            {
                int from;
                if (ctx.Cache.From.TryGetValue(rx.Id, out from) && from <= pos)
                {
                    Match cm = ctx.Cache.Result[rx.Id];
                    if (cm == null || cm.Index >= pos) return cm;
                }
            }
            Match m;
            long t0 = Stopwatch.GetTimestamp();
            try
            {
                m = net.Match(ctx.Text, pos);
                if (!m.Success) m = null;
            }
            catch (RegexMatchTimeoutException)
            {
                TextMateDiagnostics.RecordTimeout(rx.Source);
                m = null;
            }
            RegexCalls++;
            RegexTicks += Stopwatch.GetTimestamp() - t0;
            if (cacheable)
            {
                ctx.Cache.From[rx.Id] = pos;
                ctx.Cache.Result[rx.Id] = m;
            }
            return m;
        }

        /// <summary>Finds the leftmost match among the end pattern (if any) and the rule's candidates.</summary>
        private TmRule FindNext(LineContext ctx, TmStateStack stack, int pos, bool isFirstLine, int anchorPos, out Match best, out bool isEnd)
        {
            best = null;
            isEnd = false;
            TmRule bestRule = null;
            TmRule top = stack.Rule;
            bool allowA = isFirstLine && pos == 0;
            bool allowG = pos == anchorPos;
            TmRegex endRx = null;
            if (top.Kind == TmRuleKind.BeginEnd) endRx = top.GetEndRegex(stack.EndRule);

            if (endRx != null && !top.ApplyEndPatternLast)
            {
                Match m = Search(ctx, endRx, pos, allowA, allowG);
                if (m != null) { best = m; bestRule = top; isEnd = true; if (m.Index == pos) return bestRule; }
            }
            List<TmRule> cands = GetCandidates(top);
            for (int i = 0; i < cands.Count; i++)
            {
                TmRule c = cands[i];
                Match m = Search(ctx, c.Match, pos, allowA, allowG);
                if (m == null) continue;
                if (best == null || m.Index < best.Index)
                {
                    best = m;
                    bestRule = c;
                    isEnd = false;
                    if (m.Index == pos) break;
                }
            }
            if (endRx != null && top.ApplyEndPatternLast)
            {
                Match m = Search(ctx, endRx, pos, allowA, allowG);
                if (m != null && (best == null || m.Index < best.Index)) { best = m; bestRule = top; isEnd = true; }
            }
            return bestRule;
        }

        /// <summary>Tokenizes one line. <paramref name="prev"/> null = first line of the document.</summary>
        public TmLineResult TokenizeLine(string line, TmStateStack prev)
        {
            line = line ?? "";
            bool isFirstLine = prev == null;
            TmStateStack stack = prev == null ? InitialState : prev.ResetPositions();
            TmLineResult res = new TmLineResult();
            if (line.Length > MaxLineLength)
            {
                TmLineTokens big = new TmLineTokens();
                big.Starts = new int[] { 0 };
                big.Scopes = new TmScopeStack[] { stack.ContentNameScopes };
                big.Length = line.Length;
                res.Tokens = big;
                res.EndState = stack;
                return res;
            }
            LineContext ctx = new LineContext();
            ctx.Text = line + "\n";
            ctx.Length = ctx.Text.Length;
            ctx.Deadline = LineTimeBudgetMs > 0 ? Stopwatch.GetTimestamp() + (long)(LineTimeBudgetMs * (double)Stopwatch.Frequency / 1000.0) : long.MaxValue;
            stack = TokenizeString(ctx, 0, stack, isFirstLine, true, ctx.Length);
            if (ctx.LastEnd < ctx.Length) ctx.Produce(stack.ContentNameScopes, ctx.Length);

            // clip the trailing "\n"
            int n = line.Length;
            List<int> starts = new List<int>(ctx.TokStarts.Count);
            List<TmScopeStack> scopes = new List<TmScopeStack>(ctx.TokStarts.Count);
            for (int i = 0; i < ctx.TokStarts.Count; i++)
            {
                int s = ctx.TokStarts[i];
                if (s >= n && !(n == 0 && i == 0)) break;
                starts.Add(s);
                scopes.Add(ctx.TokScopes[i]);
            }
            if (starts.Count == 0) { starts.Add(0); scopes.Add(stack.ContentNameScopes); }
            TmLineTokens t = new TmLineTokens();
            t.Starts = starts.ToArray();
            t.Scopes = scopes.ToArray();
            t.Length = n;
            t.StoppedEarly = ctx.StoppedEarly;
            res.Tokens = t;
            // A line cut short by the budget ends in an arbitrary mid-line state (e.g. inside a template string);
            // assuming the line leaves the state unchanged keeps every following line sane.
            res.EndState = ctx.StoppedEarly ? (prev == null ? InitialState : prev.ResetPositions()) : stack;
            return res;
        }

        private TmStateStack TokenizeString(LineContext ctx, int linePos, TmStateStack stack, bool isFirstLine, bool checkWhile, int limit)
        {
            int anchorPos = -1;
            if (checkWhile)
            {
                anchorPos = stack.BeginRuleCapturedEOL ? 0 : -1;
                CheckWhileConditions(ctx, ref linePos, ref stack, ref isFirstLine, ref anchorPos);
            }
            string text = ctx.Text;
            int maxIter = 4 * limit + 2000;
            while (true)
            {
                if (++ctx.Iterations > maxIter || Stopwatch.GetTimestamp() > ctx.Deadline)
                {
                    ctx.StoppedEarly = true;
                    ctx.Produce(stack.ContentNameScopes, limit);
                    return stack;
                }
                Match m;
                bool isEnd;
                TmRule rule = FindNext(ctx, stack, linePos, isFirstLine, anchorPos, out m, out isEnd);
                if (rule == null || m == null || m.Index >= limit)
                {
                    ctx.Produce(stack.ContentNameScopes, limit);
                    return stack;
                }
                int mEnd = Math.Min(limit, m.Index + m.Length);
                bool hasAdvanced = mEnd > linePos;

                if (isEnd)
                {
                    TmRule popped = stack.Rule;
                    ctx.Produce(stack.ContentNameScopes, m.Index);
                    stack = stack.WithContentNameScopes(stack.NameScopes);
                    HandleCaptures(ctx, stack, popped.EndCaptures, m, isFirstLine, limit);
                    ctx.Produce(stack.ContentNameScopes, mEnd);
                    TmStateStack poppedStack = stack;
                    stack = stack.Parent ?? stack;
                    anchorPos = poppedStack.AnchorPos;
                    if (!hasAdvanced && poppedStack.EnterPos == linePos)
                    {
                        // pushed and popped without advancing: stop to avoid an endless loop
                        stack = poppedStack;
                        ctx.Produce(stack.ContentNameScopes, limit);
                        return stack;
                    }
                }
                else
                {
                    ctx.Produce(stack.ContentNameScopes, m.Index);
                    TmStateStack beforePush = stack;
                    string scopeName = rule.GetName(text, m);
                    TmScopeStack nameScopes = stack.ContentNameScopes.Push(scopeName);
                    stack = stack.Push(rule, linePos, anchorPos, mEnd == ctx.Length, null, nameScopes, nameScopes);
                    if (rule.Kind == TmRuleKind.BeginEnd || rule.Kind == TmRuleKind.BeginWhile)
                    {
                        HandleCaptures(ctx, stack, rule.Captures, m, isFirstLine, limit);
                        ctx.Produce(stack.ContentNameScopes, mEnd);
                        anchorPos = mEnd;
                        string contentName = rule.GetContentName(text, m);
                        stack = stack.WithContentNameScopes(nameScopes.Push(contentName));
                        if (rule.EndHasBackRefs)
                            stack = stack.WithEndRule(TmRule.ResolveBackRefs(rule.EndSource, text, m));
                        if (!hasAdvanced && beforePush.HasSameRuleAs(stack))
                        {
                            stack = stack.Pop();
                            ctx.Produce(stack.ContentNameScopes, limit);
                            return stack;
                        }
                    }
                    else
                    {
                        HandleCaptures(ctx, stack, rule.Captures, m, isFirstLine, limit);
                        ctx.Produce(stack.ContentNameScopes, mEnd);
                        stack = stack.Pop();
                        if (!hasAdvanced)
                        {
                            stack = stack.SafePop();
                            ctx.Produce(stack.ContentNameScopes, limit);
                            return stack;
                        }
                    }
                }
                if (mEnd > linePos)
                {
                    linePos = mEnd;
                    isFirstLine = false;
                }
                if (linePos >= limit)
                {
                    ctx.Produce(stack.ContentNameScopes, limit);
                    return stack;
                }
            }
        }

        private void CheckWhileConditions(LineContext ctx, ref int linePos, ref TmStateStack stack, ref bool isFirstLine, ref int anchorPos)
        {
            List<TmStateStack> whileNodes = null;
            for (TmStateStack node = stack; node != null; node = node.Parent)
            {
                if (node.Rule != null && node.Rule.Kind == TmRuleKind.BeginWhile)
                {
                    if (whileNodes == null) whileNodes = new List<TmStateStack>();
                    whileNodes.Add(node);
                }
            }
            if (whileNodes == null) return;
            for (int k = whileNodes.Count - 1; k >= 0; k--) // outermost first
            {
                TmStateStack node = whileNodes[k];
                TmRegex rx = node.Rule.GetEndRegex(node.EndRule);
                Match m = rx == null ? null : Search(ctx, rx, linePos, isFirstLine && linePos == 0, linePos == anchorPos);
                if (m != null)
                {
                    ctx.Produce(node.ContentNameScopes, m.Index);
                    HandleCaptures(ctx, node, node.Rule.EndCaptures, m, isFirstLine, ctx.Length);
                    ctx.Produce(node.ContentNameScopes, m.Index + m.Length);
                    anchorPos = m.Index + m.Length;
                    if (m.Index + m.Length > linePos)
                    {
                        linePos = m.Index + m.Length;
                        isFirstLine = false;
                    }
                }
                else
                {
                    stack = node.Pop() ?? node;
                    break;
                }
            }
        }

        private struct CaptureFrame
        {
            public TmScopeStack Scopes;
            public int End;
        }

        private void HandleCaptures(LineContext ctx, TmStateStack stack, TmRule[] captures, Match m, bool isFirstLine, int limit)
        {
            if (captures == null || m == null) return;
            int len = Math.Min(captures.Length, m.Groups.Count);
            int maxEnd = m.Index + m.Length;
            List<CaptureFrame> local = null;
            string text = ctx.Text;
            for (int i = 0; i < len; i++)
            {
                TmRule cap = captures[i];
                if (cap == null) continue;
                Group g = m.Groups[i];
                if (!g.Success || g.Length == 0) continue;
                if (g.Index > maxEnd) break;
                int gEnd = Math.Min(limit, g.Index + g.Length);
                while (local != null && local.Count > 0 && local[local.Count - 1].End <= g.Index)
                {
                    CaptureFrame f = local[local.Count - 1];
                    ctx.Produce(f.Scopes, f.End);
                    local.RemoveAt(local.Count - 1);
                }
                TmScopeStack baseScopes = (local != null && local.Count > 0) ? local[local.Count - 1].Scopes : stack.ContentNameScopes;
                ctx.Produce(baseScopes, g.Index);

                if (cap.HasPatterns)
                {
                    string scopeName = cap.GetName(text, m);
                    TmScopeStack nameScopes = baseScopes.Push(scopeName);
                    string contentName = cap.GetContentName(text, m);
                    TmScopeStack contentScopes = nameScopes.Push(contentName);
                    TmStateStack sub = stack.Push(cap, g.Index, -1, false, null, nameScopes, contentScopes);
                    string prevText = ctx.Text;
                    int prevLen = ctx.Length;
                    SearchCache prevCache = ctx.Cache;
                    if (gEnd < prevLen)
                    {
                        ctx.Text = prevText.Substring(0, gEnd);
                        ctx.Length = gEnd;
                        ctx.Cache = new SearchCache();
                    }
                    try
                    {
                        TokenizeString(ctx, g.Index, sub, isFirstLine && g.Index == 0, false, gEnd);
                    }
                    finally
                    {
                        ctx.Text = prevText;
                        ctx.Length = prevLen;
                        ctx.Cache = prevCache;
                    }
                    continue;
                }
                string capName = cap.GetName(text, m);
                if (capName != null)
                {
                    if (local == null) local = new List<CaptureFrame>();
                    CaptureFrame nf = new CaptureFrame();
                    nf.Scopes = baseScopes.Push(capName);
                    nf.End = gEnd;
                    local.Add(nf);
                }
            }
            while (local != null && local.Count > 0)
            {
                CaptureFrame f = local[local.Count - 1];
                ctx.Produce(f.Scopes, f.End);
                local.RemoveAt(local.Count - 1);
            }
        }
    }
}
