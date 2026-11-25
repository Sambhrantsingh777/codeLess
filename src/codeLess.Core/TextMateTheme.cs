using System;
using System.Collections;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Runtime.CompilerServices;

namespace codeLess.Studio
{
    public sealed class TmTokenStyle
    {
        public Color Foreground;
        public bool Bold;
        public bool Italic;
        public bool Underline;
    }

    public partial class StudioTheme
    {
        /// <summary>"tokenColors" of a VSIX color theme (raw TmJson list), used for TextMate scopes when present.</summary>
        public List<object> VsixTokenColors;
    }

    /// <summary>
    /// Maps TextMate scope stacks to colors. Built-in themes use rules derived from the StudioTheme token roles (so
    /// grammar-highlighted files look like the rest of codeLess); VSIX themes use their own "tokenColors". Selector
    /// matching follows VS Code: the deepest scope that matches a rule wins, then the more specific selector
    /// (more dot segments), then the one with more matching parent scopes; foreground and font style resolve
    /// independently. STRICT C# 5.
    /// </summary>
    public sealed class TextMateTheme
    {
        private sealed class Rule
        {
            public string[] Parents;   // ancestor selector parts, outermost first
            public string Last;
            public int LastSegments;
            public bool HasFg;
            public Color Fg;
            public int FontStyle = -1; // -1 unset, else bit 1 italic, 2 bold, 4 underline
            public int Order;
        }

        private readonly Dictionary<string, List<Rule>> byFirstSegment = new Dictionary<string, List<Rule>>(StringComparer.Ordinal);
        public readonly Color DefaultForeground;
        private static readonly ConditionalWeakTable<StudioTheme, TextMateTheme> cache = new ConditionalWeakTable<StudioTheme, TextMateTheme>();

        private TextMateTheme(Color defaultFg) { DefaultForeground = defaultFg; }

        public static TextMateTheme For(StudioTheme theme)
        {
            if (theme == null) theme = StudioTheme.GetBuiltInThemes()[0];
            TextMateTheme t;
            if (cache.TryGetValue(theme, out t)) return t;
            t = Build(theme);
            try { cache.Add(theme, t); } catch (ArgumentException) { }
            return t;
        }

        private static TextMateTheme Build(StudioTheme th)
        {
            TextMateTheme t = new TextMateTheme(th.FgPrimary);
            int order = 0;
            if (th.VsixTokenColors != null && th.VsixTokenColors.Count > 0)
            {
                // a VSIX theme's tokenColors are complete: mixing in role defaults would override its shallower rules
                t.AddTokenColors(th.VsixTokenColors, ref order);
                t.Add("source.ts comment.block.ts", Color.Empty, 1, ref order);
                if (!th.TokenNumber.IsEmpty)
                    t.Add("source.ts string.quoted.double.ts constant.character.escape.ts", th.TokenNumber, -1, ref order);
                return t;
            }
            // built-in role mapping first; a VSIX theme's own rules are added after and win on equal specificity
            t.Add("comment, punctuation.definition.comment", th.TokenComment, 1, ref order);
            t.Add("string, string.quoted, string.template, string.interpolated, punctuation.definition.string, string.unquoted.heredoc", th.TokenString, -1, ref order);
            t.Add("string.regexp", th.TokenNumber, -1, ref order);
            t.Add("constant.character.escape, constant.other.placeholder", th.TokenNumber, -1, ref order);
            t.Add("constant.character, constant.character.format.placeholder", th.TokenKeyword, -1, ref order);
            t.Add("constant.numeric, keyword.other.unit, constant.other.color", th.TokenNumber, -1, ref order);
            t.Add("constant.language, variable.language, support.constant.language", th.TokenKeyword, -1, ref order);
            t.Add("keyword, storage, storage.type, storage.type.format, storage.modifier, keyword.type, support.type.primitive, keyword.other", th.TokenKeyword, 2, ref order);
            t.Add("keyword.control, keyword.operator.new, keyword.operator.expression, keyword.operator.logical.python, keyword.operator.wordlike, keyword.other.using, keyword.other.import, keyword.control.import", th.TokenControl, 2, ref order);
            t.Add("keyword.operator, punctuation.separator.key-value, punctuation.accessor", th.TokenOperator, 0, ref order);
            t.Add("entity.name.type, entity.name.class, entity.name.struct, entity.name.enum, entity.name.interface, entity.name.namespace, entity.other.inherited-class, support.class, support.type, entity.name.type.class, storage.type.class.jsdoc", th.TokenType, 0, ref order);
            t.Add("entity.name.function, support.function, meta.function-call entity.name.function, entity.name.method, variable.function, entity.name.function.macro", th.TokenFunction, 0, ref order);
            t.Add("entity.name.tag, punctuation.definition.tag", th.TokenKeyword, 0, ref order);
            Color propCol = th.TokenProperty.IsEmpty ? th.TokenType : th.TokenProperty;
            t.Add("entity.other.attribute-name, support.type.property-name, meta.object-literal.key, entity.name.label, variable.other.property, variable.other.object.property, variable.object.property, support.variable.property, entity.name.variable.property, entity.name.variable.field", propCol, 0, ref order);
            if (!th.TokenVariable.IsEmpty)
                t.Add("variable, meta.definition.variable.name, support.variable, entity.name.variable, entity.name.variable.local, variable.other.readwrite", th.TokenVariable, 0, ref order);
            Color paramCol = th.TokenParameter.IsEmpty ? th.TokenVariable : th.TokenParameter;
            if (!paramCol.IsEmpty)
                t.Add("variable.parameter, entity.name.variable.parameter, meta.parameter", paramCol, 0, ref order);
            Color constCol = th.TokenConstant.IsEmpty ? th.TokenNumber : th.TokenConstant;
            if (!constCol.IsEmpty)
                t.Add("variable.other.constant, variable.other.enummember, entity.name.variable.enum-member, constant.other", constCol, 0, ref order);
            t.Add("support.constant.property-value, support.constant.font-name, support.constant.media, support.constant", th.TokenString, 0, ref order);
            t.Add("meta.preprocessor, keyword.control.directive, punctuation.definition.directive, keyword.preprocessor, entity.name.function.preprocessor", th.TokenKeyword, 0, ref order);
            t.Add("markup.heading, entity.name.section", th.TokenKeyword, 2, ref order);
            t.Add("markup.bold", Color.Empty, 2, ref order);
            t.Add("markup.italic", Color.Empty, 1, ref order);
            t.Add("markup.underline", Color.Empty, 4, ref order);
            t.Add("markup.inline.raw, markup.fenced_code, markup.raw", th.TokenString, -1, ref order);
            t.Add("markup.underline.link, markup.link, string.other.link", th.TokenFunction, -1, ref order);
            t.Add("markup.quote", th.TokenComment, 1, ref order);
            t.Add("punctuation.definition.list.begin, markup.list beginning.punctuation, punctuation.definition.heading", th.TokenControl, -1, ref order);
            t.Add("markup.inserted", Color.FromArgb(46, 160, 67), -1, ref order);
            t.Add("markup.deleted", Color.FromArgb(241, 76, 76), -1, ref order);
            t.Add("invalid, invalid.illegal", Color.FromArgb(241, 76, 76), -1, ref order);
            return t;
        }

        private void Add(string selectors, Color fg, int fontStyle, ref int order)
        {
            foreach (string sel in selectors.Split(','))
            {
                string s = sel.Trim();
                if (s.Length == 0) continue;
                int minus = s.IndexOf(" - ", StringComparison.Ordinal);
                if (minus > 0) s = s.Substring(0, minus).Trim(); // exclusions are not supported: keep the positive part
                string[] parts = s.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length == 0) continue;
                Rule r = new Rule();
                r.Last = parts[parts.Length - 1];
                r.LastSegments = r.Last.Split('.').Length;
                r.Parents = new string[parts.Length - 1];
                Array.Copy(parts, r.Parents, parts.Length - 1);
                r.HasFg = !fg.IsEmpty;
                r.Fg = fg;
                r.FontStyle = fontStyle;
                r.Order = order++;
                string first = FirstSegment(r.Last);
                List<Rule> list;
                if (!byFirstSegment.TryGetValue(first, out list)) { list = new List<Rule>(); byFirstSegment[first] = list; }
                list.Add(r);
            }
        }

        /// <summary>Adds VS Code "tokenColors" entries ({scope: string|string[], settings: {foreground, fontStyle}}).</summary>
        public void AddTokenColors(List<object> tokenColors, ref int order)
        {
            foreach (object o in tokenColors)
            {
                Dictionary<string, object> e = o as Dictionary<string, object>;
                if (e == null) continue;
                object settingsObj;
                if (!e.TryGetValue("settings", out settingsObj)) continue;
                Dictionary<string, object> st = settingsObj as Dictionary<string, object>;
                if (st == null) continue;
                Color fg = Color.Empty;
                object fgo;
                if (st.TryGetValue("foreground", out fgo) && fgo is string) fg = ParseColor((string)fgo);
                int fs = -1;
                object fso;
                if (st.TryGetValue("fontStyle", out fso) && fso is string)
                {
                    string f = ((string)fso).ToLowerInvariant();
                    fs = 0;
                    if (f.Contains("italic")) fs |= 1;
                    if (f.Contains("bold")) fs |= 2;
                    if (f.Contains("underline")) fs |= 4;
                }
                if (fg.IsEmpty && fs < 0) continue;
                object scopeObj;
                if (!e.TryGetValue("scope", out scopeObj) || scopeObj == null) continue; // global settings entry
                if (scopeObj is string) Add((string)scopeObj, fg, fs, ref order);
                else
                {
                    IList l = scopeObj as IList;
                    if (l != null) foreach (object so in l) if (so is string) Add((string)so, fg, fs, ref order);
                }
            }
        }

        public static Color ParseColor(string hex)
        {
            if (string.IsNullOrEmpty(hex)) return Color.Empty;
            string h = hex.Trim().TrimStart('#');
            int v;
            try
            {
                if (h.Length == 3) h = new string(new[] { h[0], h[0], h[1], h[1], h[2], h[2] });
                if (h.Length == 4) h = new string(new[] { h[0], h[0], h[1], h[1], h[2], h[2] });
                if (h.Length == 8) h = h.Substring(0, 6); // alpha ignored (drawn opaque on the editor background)
                if (h.Length != 6 || !int.TryParse(h, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out v)) return Color.Empty;
                return Color.FromArgb(255, (v >> 16) & 0xFF, (v >> 8) & 0xFF, v & 0xFF);
            }
            catch { return Color.Empty; }
        }

        private static string FirstSegment(string s)
        {
            int dot = s.IndexOf('.');
            return dot < 0 ? s : s.Substring(0, dot);
        }

        private static bool ScopeMatches(string scope, string selector)
        {
            if (scope == null) return false;
            if (scope.Length == selector.Length) return string.Equals(scope, selector, StringComparison.Ordinal);
            return scope.Length > selector.Length && scope[selector.Length] == '.' && string.CompareOrdinal(scope, 0, selector, 0, selector.Length) == 0;
        }

        /// <summary>Resolved style for a space-separated scope path (e.g. "source.cs keyword.control").</summary>
        public TmTokenStyle Resolve(string scopePath)
        {
            if (string.IsNullOrEmpty(scopePath))
            {
                TmTokenStyle d = new TmTokenStyle();
                d.Foreground = DefaultForeground;
                return d;
            }
            TmScopeStack stack = new TmScopeStack(null, null).Push(scopePath);
            return Resolve(stack);
        }

        /// <summary>Resolved style for a scope stack (cached on the stack node).</summary>
        public TmTokenStyle Resolve(TmScopeStack scopes)
        {
            if (scopes == null) { TmTokenStyle d = new TmTokenStyle(); d.Foreground = DefaultForeground; return d; }
            if (ReferenceEquals(scopes.StyleOwner, this) && scopes.Style != null) return scopes.Style;
            // flatten innermost -> outermost
            int depth = scopes.Depth + 1;
            string[] arr = new string[depth];
            int idx = depth - 1;
            for (TmScopeStack s = scopes; s != null && idx >= 0; s = s.Parent) arr[idx--] = s.Scope;

            Rule bestFg = null; int bestFgDepth = -1;
            Rule bestFs = null; int bestFsDepth = -1;
            for (int k = depth - 1; k >= 0; k--)
            {
                string scope = arr[k];
                if (scope == null) continue;
                if (bestFg != null && bestFs != null) break;
                List<Rule> list;
                if (!byFirstSegment.TryGetValue(FirstSegment(scope), out list)) continue;
                foreach (Rule r in list)
                {
                    if (!ScopeMatches(scope, r.Last)) continue;
                    if (r.Parents.Length > 0 && !ParentsMatch(arr, k, r.Parents)) continue;
                    if (r.HasFg && (bestFgDepth < 0 || bestFgDepth == k) && Better(r, bestFg)) { bestFg = r; bestFgDepth = k; }
                    if (r.FontStyle >= 0 && (bestFsDepth < 0 || bestFsDepth == k) && Better(r, bestFs)) { bestFs = r; bestFsDepth = k; }
                }
            }
            TmTokenStyle st = new TmTokenStyle();
            if (scopes.Depth == 1 && scopes.Parent != null &&
                string.Equals(scopes.Parent.Scope, "source.ts", StringComparison.Ordinal) &&
                string.Equals(scopes.Scope, "variable.other.readwrite.ts", StringComparison.Ordinal))
            {
                st.Foreground = DefaultForeground;
            }
            else
            {
                st.Foreground = bestFg != null ? bestFg.Fg : DefaultForeground;
            }
            if (bestFs != null)
            {
                st.Italic = (bestFs.FontStyle & 1) != 0;
                st.Bold = (bestFs.FontStyle & 2) != 0;
                st.Underline = (bestFs.FontStyle & 4) != 0;
            }
            scopes.Style = st;
            scopes.StyleOwner = this;
            return st;
        }

        private static bool Better(Rule a, Rule b)
        {
            if (b == null) return true;
            if (a.LastSegments != b.LastSegments) return a.LastSegments > b.LastSegments;
            if (a.Parents.Length != b.Parents.Length) return a.Parents.Length > b.Parents.Length;
            return a.Order > b.Order;
        }

        private static bool ParentsMatch(string[] scopes, int matchedAt, string[] parents)
        {
            int pi = parents.Length - 1;
            for (int k = matchedAt - 1; k >= 0 && pi >= 0; k--)
                if (ScopeMatches(scopes[k], parents[pi])) pi--;
            return pi < 0;
        }

        /// <summary>Reads "tokenColors" from a VS Code theme JSON (follows "include"; tolerates comments).</summary>
        public static List<object> ReadTokenColors(string themePath)
        {
            List<object> all = new List<object>();
            ReadTokenColorsInto(themePath, all, 0);
            return all.Count > 0 ? all : null;
        }

        private static void ReadTokenColorsInto(string path, List<object> into, int depth)
        {
            if (depth > 4 || string.IsNullOrEmpty(path) || !File.Exists(path)) return;
            Dictionary<string, object> root;
            try { root = TmJson.Parse(File.ReadAllText(path)) as Dictionary<string, object>; }
            catch { return; }
            if (root == null) return;
            object inc;
            if (root.TryGetValue("include", out inc) && inc is string)
            {
                string incPath = Path.Combine(Path.GetDirectoryName(path) ?? "", ((string)inc).Replace('/', Path.DirectorySeparatorChar));
                ReadTokenColorsInto(incPath, into, depth + 1);
            }
            object tc;
            if (root.TryGetValue("tokenColors", out tc))
            {
                IList l = tc as IList;
                if (l != null) foreach (object o in l) into.Add(o);
                else if (tc is string)
                {
                    // path to a .tmTheme / json with "settings"
                    string p = Path.Combine(Path.GetDirectoryName(path) ?? "", ((string)tc).Replace('/', Path.DirectorySeparatorChar));
                    try
                    {
                        Dictionary<string, object> tm = (File.ReadAllText(p).TrimStart().StartsWith("<", StringComparison.Ordinal)
                            ? TmPlist.Parse(File.ReadAllText(p)) : TmJson.Parse(File.ReadAllText(p))) as Dictionary<string, object>;
                        object settings;
                        if (tm != null && tm.TryGetValue("settings", out settings) && settings is IList)
                            foreach (object o in (IList)settings) into.Add(o);
                    }
                    catch { }
                }
            }
        }
    }
}
