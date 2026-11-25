using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace codeLess.Studio
{
    /// <summary>
    /// Translates Oniguruma (Ruby syntax, as used by TextMate grammars) regular expressions to .NET syntax.
    ///
    /// Supported: \h \H, \x{HHHH}, \A / \G (enabled or disabled per search, see vscode-textmate's "allowA/allowG"),
    /// \z \Z, possessive quantifiers (*+ ++ ?+ become atomic groups), {n,m}+ (a repeated interval, as in Ruby),
    /// (?x) extended mode at the start of a pattern (comments/whitespace stripped), POSIX brackets [[:alpha:]] (also
    /// negated [[:^alpha:]]), nested class unions [a-z[0-9]], \p{Alnum}-style property names, \p{^X}, (?m) (Ruby's
    /// dot-all), \R, \N, \O, \X.
    ///
    /// Unsupported (the rule is disabled, recorded in <see cref="LastError"/>): subexpression calls \g&lt;name&gt;,
    /// \K, absent operator (?~...), class intersection &amp;&amp;, conditionals (?(cond)..) using Oniguruma-only forms.
    /// Unicode behaviour differs slightly: .NET's \w / \d are Unicode-aware like Oniguruma's UTF-8 mode.
    /// STRICT C# 5.
    /// </summary>
    public static class OnigurumaTranslator
    {
        [ThreadStatic] private static string lastError;
        public static string LastError { get { return lastError; } }

        /// <summary>Returns the .NET pattern, or null when the pattern uses an unsupported construct.</summary>
        public static string Translate(string pattern, bool allowA, bool allowG)
        {
            lastError = null;
            if (pattern == null) return null;
            try
            {
                string p = pattern;
                if (p.StartsWith("(?x)", StringComparison.Ordinal)) p = StripExtended(p.Substring(4));
                bool named = HasNamedGroups(p);
                if (named && p.IndexOf("\\g<", StringComparison.Ordinal) >= 0) p = InlineSubexpressionCalls(p);
                return TranslateCore(p, allowA, allowG, named);
            }
            catch (NotSupportedException ex)
            {
                lastError = ex.Message;
                return null;
            }
        }

        public static bool ContainsAnchorG(string pattern) { return pattern != null && ContainsEscape(pattern, 'G'); }
        public static bool ContainsAnchorA(string pattern) { return pattern != null && ContainsEscape(pattern, 'A'); }

        private static bool ContainsEscape(string p, char letter)
        {
            for (int i = 0; i + 1 < p.Length; i++)
            {
                if (p[i] == '\\')
                {
                    if (p[i + 1] == letter) return true;
                    i++;
                }
            }
            return false;
        }

        /// <summary>Removes whitespace and #-comments outside character classes (Oniguruma extended mode).</summary>
        private static string StripExtended(string p)
        {
            StringBuilder sb = new StringBuilder(p.Length);
            int classDepth = 0;
            for (int i = 0; i < p.Length; i++)
            {
                char c = p[i];
                if (c == '\\' && i + 1 < p.Length)
                {
                    sb.Append(c).Append(p[i + 1]);
                    i++;
                    continue;
                }
                if (classDepth > 0)
                {
                    if (c == '[') classDepth++;
                    else if (c == ']') classDepth--;
                    sb.Append(c);
                    continue;
                }
                if (c == '[')
                {
                    classDepth = 1;
                    sb.Append(c);
                    // a leading ']' or '^]' is literal
                    if (i + 1 < p.Length && p[i + 1] == '^') { sb.Append('^'); i++; }
                    if (i + 1 < p.Length && p[i + 1] == ']') { sb.Append(']'); i++; }
                    continue;
                }
                if (c == '(' && i + 2 < p.Length && p[i + 1] == '?' && p[i + 2] == '#')
                {
                    i += 3;
                    while (i < p.Length && p[i] != ')')
                    {
                        if (p[i] == '\\' && i + 1 < p.Length) { i += 2; continue; }
                        i++;
                    }
                    continue;
                }
                if (c == '#')
                {
                    while (i < p.Length && p[i] != '\n') i++;
                    continue;
                }
                if (c == ' ' || c == '\t' || c == '\n' || c == '\r' || c == '\f' || c == '\v') continue;
                sb.Append(c);
            }
            return sb.ToString();
        }

        /// <summary>True when the pattern defines (?&lt;name&gt;...) groups (then plain groups do not capture, as in Oniguruma).</summary>
        private static bool HasNamedGroups(string p)
        {
            for (int i = 0; i + 3 < p.Length; i++)
            {
                if (p[i] == '\\') { i++; continue; }
                if (p[i] == '(' && p[i + 1] == '?' && p[i + 2] == '<' && (char.IsLetter(p[i + 3]) || p[i + 3] == '_')) return true;
            }
            return false;
        }

        /// <summary>Index of the ')' closing the group opened at p[open] (escapes and classes respected), or -1.</summary>
        private static int FindGroupEnd(string p, int open)
        {
            int depth = 0;
            bool inClass = false;
            for (int i = open; i < p.Length; i++)
            {
                char c = p[i];
                if (c == '\\') { i++; continue; }
                if (inClass) { if (c == ']') inClass = false; continue; }
                if (c == '[') { inClass = true; if (i + 1 < p.Length && p[i + 1] == ']') i++; continue; }
                if (c == '(') depth++;
                else if (c == ')') { depth--; if (depth == 0) return i; }
            }
            return -1;
        }

        /// <summary>
        /// Replaces Oniguruma subexpression calls \g&lt;name&gt; with a non-capturing copy of the named group's pattern.
        /// Recursive calls are expanded to a fixed depth (deeper nesting never matches), which covers the nested
        /// generic types the C# grammar matches this way.
        /// </summary>
        private static string InlineSubexpressionCalls(string p)
        {
            Dictionary<string, string> groups = new Dictionary<string, string>(StringComparer.Ordinal);
            for (int i = 0; i + 3 < p.Length; i++)
            {
                if (p[i] == '\\') { i++; continue; }
                if (p[i] == '(' && p[i + 1] == '?' && p[i + 2] == '<' && (char.IsLetter(p[i + 3]) || p[i + 3] == '_'))
                {
                    int gt = p.IndexOf('>', i + 3);
                    int end = FindGroupEnd(p, i);
                    if (gt < 0 || end < 0) throw new NotSupportedException("unbalanced named group");
                    string name = p.Substring(i + 3, gt - i - 3);
                    if (!groups.ContainsKey(name)) groups[name] = p.Substring(gt + 1, end - gt - 1);
                }
            }
            string res = ExpandCalls(p, groups, 0, true);
            if (res.Length > 60000) throw new NotSupportedException("subexpression call expansion too large");
            return res;
        }

        private const int MaxCallDepth = 3;

        private static string ExpandCalls(string p, Dictionary<string, string> groups, int depth, bool keepNames)
        {
            StringBuilder sb = new StringBuilder(p.Length);
            for (int i = 0; i < p.Length; i++)
            {
                char c = p[i];
                if (c == '\\' && i + 1 < p.Length)
                {
                    if (p[i + 1] == 'g' && i + 2 < p.Length && p[i + 2] == '<')
                    {
                        int gt = p.IndexOf('>', i + 3);
                        if (gt < 0) throw new NotSupportedException("bad \\g<");
                        string name = p.Substring(i + 3, gt - i - 3);
                        string body;
                        if (!groups.TryGetValue(name, out body)) throw new NotSupportedException("subexpression call \\g<" + name + ">");
                        if (depth >= MaxCallDepth) sb.Append("(?!)");
                        else sb.Append("(?:").Append(ExpandCalls(body, groups, depth + 1, false)).Append(')');
                        i = gt;
                        continue;
                    }
                    sb.Append(c).Append(p[i + 1]);
                    i++;
                    continue;
                }
                // inside inlined copies, named groups become non-capturing (no duplicate captures / renumbering)
                if (!keepNames && c == '(' && i + 3 < p.Length && p[i + 1] == '?' && p[i + 2] == '<' && (char.IsLetter(p[i + 3]) || p[i + 3] == '_'))
                {
                    int gt = p.IndexOf('>', i + 3);
                    sb.Append("(?:");
                    i = gt;
                    continue;
                }
                sb.Append(c);
            }
            return sb.ToString();
        }

        private static string TranslateCore(string p, bool allowA, bool allowG, bool namedGroups)
        {
            StringBuilder sb = new StringBuilder(p.Length + 16);
            Stack<int> groupStarts = new Stack<int>();
            int lastAtomStart = -1;
            // Oniguruma numbers named and plain groups left to right; .NET numbers named groups last. With named
            // groups present they are emitted as plain groups and \k<name> / (?(<name>)..) become numeric.
            Dictionary<string, int> groupNumbers = namedGroups ? new Dictionary<string, int>(StringComparer.Ordinal) : null;
            int captureCount = 0;
            int i = 0;
            while (i < p.Length)
            {
                char c = p[i];
                if (c == '\\' && namedGroups && i + 2 < p.Length && p[i + 1] == 'k' && (p[i + 2] == '<' || p[i + 2] == '\''))
                {
                    char closeCh = p[i + 2] == '<' ? '>' : '\'';
                    int close = p.IndexOf(closeCh, i + 3);
                    if (close < 0) throw new NotSupportedException("bad \\k");
                    string refName = p.Substring(i + 3, close - i - 3);
                    int num;
                    lastAtomStart = sb.Length;
                    if (groupNumbers.TryGetValue(refName, out num)) sb.Append("(?:\\").Append(num.ToString(CultureInfo.InvariantCulture)).Append(')');
                    else if (int.TryParse(refName, out num)) sb.Append("(?:\\").Append(num.ToString(CultureInfo.InvariantCulture)).Append(')');
                    else sb.Append("(?!)");
                    i = close + 1;
                    continue;
                }
                if (c == '\\')
                {
                    if (i + 1 >= p.Length) { sb.Append("\\\\"); i++; continue; }
                    lastAtomStart = sb.Length;
                    i = TranslateEscape(p, i, sb, allowA, allowG, false);
                    continue;
                }
                if (c == '[')
                {
                    lastAtomStart = sb.Length;
                    i = TranslateClass(p, i, sb);
                    continue;
                }
                if (c == '(')
                {
                    groupStarts.Push(sb.Length);
                    if (i + 1 < p.Length && p[i + 1] == '?')
                    {
                        if (i + 2 < p.Length)
                        {
                            char k = p[i + 2];
                            if (k == '~') throw new NotSupportedException("absent operator (?~");
                            if (k == '(')
                            {
                                // conditional: Oniguruma (?(<name>)..) / (?('name')..) / (?(1)..) -> .NET (?(name)..)
                                int close = p.IndexOf(')', i + 3);
                                if (close < 0) throw new NotSupportedException("bad conditional");
                                string cond = p.Substring(i + 3, close - i - 3).Trim('<', '>', '\'');
                                int condNum;
                                if (groupNumbers != null && groupNumbers.TryGetValue(cond, out condNum)) cond = condNum.ToString(CultureInfo.InvariantCulture);
                                sb.Append("(?(").Append(cond).Append(')');
                                i = close + 1;
                                continue;
                            }
                            if (k == '#')
                            {
                                // comment group (?#...)
                                int e = p.IndexOf(')', i);
                                groupStarts.Pop();
                                i = e < 0 ? p.Length : e + 1;
                                continue;
                            }
                            // inline options: Ruby's (?m) is .NET's (?s)
                            if (k == 'm' || k == 'i' || k == 'x' || k == '-' || k == 's')
                            {
                                int j = i + 2;
                                StringBuilder opts = new StringBuilder();
                                while (j < p.Length && (p[j] == 'm' || p[j] == 'i' || p[j] == 'x' || p[j] == '-' || p[j] == 's')) { opts.Append(p[j] == 'm' ? 's' : p[j]); j++; }
                                if (j < p.Length && (p[j] == ')' || p[j] == ':'))
                                {
                                    sb.Append("(?").Append(opts.ToString());
                                    if (p[j] == ')') { groupStarts.Pop(); sb.Append(')'); }
                                    else sb.Append(':');
                                    i = j + 1;
                                    continue;
                                }
                            }
                        }
                    }
                    bool plainGroup = !(i + 1 < p.Length && p[i + 1] == '?');
                    if (plainGroup) captureCount++;
                    if (namedGroups && i + 3 < p.Length && p[i + 1] == '?' && (p[i + 2] == '<' || p[i + 2] == 'P') && (char.IsLetter(p[i + 3]) || p[i + 3] == '_' || p[i + 3] == '<'))
                    {
                        int nameStart = p[i + 2] == 'P' ? i + 4 : i + 3;
                        int gt = p.IndexOf('>', nameStart);
                        if (gt > 0)
                        {
                            captureCount++;
                            string gname = p.Substring(nameStart, gt - nameStart);
                            if (!groupNumbers.ContainsKey(gname)) groupNumbers[gname] = captureCount;
                            sb.Append('(');
                            i = gt + 1;
                            continue;
                        }
                    }
                    sb.Append('(');
                    i++;
                    continue;
                }
                if (c == ')')
                {
                    lastAtomStart = groupStarts.Count > 0 ? groupStarts.Pop() : -1;
                    sb.Append(')');
                    i++;
                    continue;
                }
                if (c == '*' || c == '+' || c == '?')
                {
                    sb.Append(c);
                    i++;
                    if (i < p.Length && p[i] == '+' )
                    {
                        // possessive: wrap atom + quantifier into an atomic group
                        if (lastAtomStart >= 0 && lastAtomStart <= sb.Length)
                        {
                            sb.Insert(lastAtomStart, "(?>");
                            sb.Append(')');
                        }
                        i++;
                    }
                    else if (i < p.Length && p[i] == '?')
                    {
                        sb.Append('?'); // lazy
                        i++;
                    }
                    lastAtomStart = -1;
                    continue;
                }
                if (c == '{')
                {
                    int close = p.IndexOf('}', i);
                    if (close > i && IsInterval(p, i + 1, close))
                    {
                        string body = p.Substring(i + 1, close - i - 1);
                        if (body.StartsWith(",", StringComparison.Ordinal)) body = "0" + body; // Ruby {,n}
                        int comma = body.IndexOf(',');
                        if (comma > 0 && comma < body.Length - 1)
                        {
                            int lo, hi;
                            if (int.TryParse(body.Substring(0, comma), out lo) && int.TryParse(body.Substring(comma + 1), out hi) && lo > hi)
                                body = hi.ToString(CultureInfo.InvariantCulture) + "," + lo.ToString(CultureInfo.InvariantCulture); // Oniguruma accepts {n,m} with n > m
                        }
                        sb.Append('{').Append(body).Append('}');
                        i = close + 1;
                        if (i < p.Length && p[i] == '+')
                        {
                            // Ruby: {n,m}+ is a repeated interval, not possessive
                            if (lastAtomStart >= 0 && lastAtomStart <= sb.Length)
                            {
                                sb.Insert(lastAtomStart, "(?:");
                                sb.Append(")+");
                            }
                            i++;
                        }
                        else if (i < p.Length && p[i] == '?') { sb.Append('?'); i++; }
                        lastAtomStart = -1;
                        continue;
                    }
                    lastAtomStart = sb.Length;
                    sb.Append("\\{");
                    i++;
                    continue;
                }
                if (c == '}') { lastAtomStart = sb.Length; sb.Append("\\}"); i++; continue; }
                lastAtomStart = sb.Length;
                sb.Append(c);
                i++;
            }
            return sb.ToString();
        }

        private static bool IsInterval(string p, int from, int to)
        {
            if (to <= from) return false;
            bool digit = false;
            int commas = 0;
            for (int k = from; k < to; k++)
            {
                char ch = p[k];
                if (ch >= '0' && ch <= '9') digit = true;
                else if (ch == ',') { commas++; if (commas > 1) return false; }
                else return false;
            }
            return digit;
        }

        private static int TranslateEscape(string p, int i, StringBuilder sb, bool allowA, bool allowG, bool inClass)
        {
            char e = p[i + 1];
            switch (e)
            {
                case 'h': sb.Append(inClass ? "0-9a-fA-F" : "[0-9a-fA-F]"); return i + 2;
                case 'H':
                    if (inClass) throw new NotSupportedException("\\H inside a class");
                    sb.Append("[^0-9a-fA-F]"); return i + 2;
                case 'G':
                    if (inClass) { sb.Append('G'); return i + 2; }
                    sb.Append(allowG ? "\\G" : "(?!)"); return i + 2;
                case 'A':
                    if (inClass) { sb.Append('A'); return i + 2; }
                    sb.Append(allowA ? "\\A" : "(?!)"); return i + 2;
                case 'K': throw new NotSupportedException("\\K");
                case 'g':
                    if (i + 2 < p.Length && (p[i + 2] == '<' || p[i + 2] == '\'')) throw new NotSupportedException("subexpression call \\g<...>");
                    sb.Append("\\g"); return i + 2;
                case 'R':
                    sb.Append(inClass ? "\\n\\r" : "(?:\\r\\n|[\\n\\v\\f\\r\\u0085\\u2028\\u2029])"); return i + 2;
                case 'N': sb.Append(inClass ? "" : "[^\\n]"); return i + 2;
                case 'O': sb.Append(inClass ? "\\s\\S" : "[\\s\\S]"); return i + 2;
                case 'X': sb.Append(inClass ? "\\s\\S" : "(?>\\P{M}\\p{M}*)"); return i + 2;
                case 'x':
                    if (i + 2 < p.Length && p[i + 2] == '{')
                    {
                        int close = p.IndexOf('}', i + 3);
                        if (close < 0) throw new NotSupportedException("bad \\x{");
                        string[] cps = p.Substring(i + 3, close - i - 3).Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                        int next = close + 1;
                        foreach (string hex in cps)
                        {
                            int cp;
                            if (!int.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out cp) || cp > 0x10FFFF)
                                throw new NotSupportedException("bad \\x{");
                            if (cp > 0xFFFF && inClass)
                            {
                                // .NET classes hold UTF-16 units: approximate astral chars / ranges by the surrogate block
                                sb.Append("\\uD800-\\uDFFF");
                                if (cps.Length == 1 && next + 3 < p.Length && p[next] == '-' && p[next + 1] == '\\' && p[next + 2] == 'x' && p[next + 3] == '{')
                                {
                                    int c2 = p.IndexOf('}', next + 4);
                                    if (c2 > 0) next = c2 + 1;
                                }
                                continue;
                            }
                            AppendCodePoint(sb, cp, inClass);
                        }
                        return next;
                    }
                    sb.Append("\\x"); return i + 2;
                case 'p':
                case 'P':
                    if (i + 2 < p.Length && p[i + 2] == '{')
                    {
                        int close = p.IndexOf('}', i + 3);
                        if (close < 0) throw new NotSupportedException("bad \\p{");
                        string prop = p.Substring(i + 3, close - i - 3);
                        bool neg = e == 'P';
                        if (prop.StartsWith("^", StringComparison.Ordinal)) { neg = !neg; prop = prop.Substring(1); }
                        sb.Append(MapProperty(prop, neg, inClass));
                        return close + 1;
                    }
                    sb.Append('\\').Append(e); return i + 2;
                case 'Z': sb.Append(inClass ? "Z" : "(?=\\n?\\z)"); return i + 2;
                case '/': sb.Append('/'); return i + 2;
                case '-': sb.Append(inClass ? "\\-" : "-"); return i + 2;
                case '#': sb.Append("\\#"); return i + 2;
                case ' ': sb.Append("\\ "); return i + 2;
                case '"': sb.Append('"'); return i + 2;
                case '\'': sb.Append('\''); return i + 2;
                case '&': sb.Append('&'); return i + 2;
                case '~': sb.Append('~'); return i + 2;
                case '%': sb.Append('%'); return i + 2;
                case '@': sb.Append('@'); return i + 2;
                case ',': sb.Append(','); return i + 2;
                case ':': sb.Append(':'); return i + 2;
                case ';': sb.Append(';'); return i + 2;
                case '=': sb.Append('='); return i + 2;
                case '<': sb.Append('<'); return i + 2;
                case '>': sb.Append('>'); return i + 2;
                case '!': sb.Append('!'); return i + 2;
                case '`': sb.Append('`'); return i + 2;
                case '_': sb.Append('_'); return i + 2;
                case 'i': case 'j': case 'l': case 'm': case 'o': case 'q': case 'y':
                    // Oniguruma treats unknown letter escapes as the letter itself; .NET rejects them.
                    sb.Append(e); return i + 2;
                case 'E': case 'F': case 'I': case 'J': case 'L': case 'M': case 'Q': case 'T': case 'U': case 'V': case 'Y':
                    sb.Append(e); return i + 2;
                default:
                    sb.Append('\\').Append(e);
                    return i + 2;
            }
        }

        private static void AppendCodePoint(StringBuilder sb, int cp, bool inClass)
        {
            if (cp <= 0xFFFF)
            {
                sb.Append("\\u").Append(cp.ToString("X4", CultureInfo.InvariantCulture));
                return;
            }
            if (inClass) throw new NotSupportedException("astral code point in a class");
            string s = char.ConvertFromUtf32(cp);
            sb.Append("(?:\\u").Append(((int)s[0]).ToString("X4", CultureInfo.InvariantCulture))
              .Append("\\u").Append(((int)s[1]).ToString("X4", CultureInfo.InvariantCulture)).Append(')');
        }

        private static string MapProperty(string prop, bool neg, bool inClass)
        {
            string body = PosixBody(prop.ToLowerInvariant());
            if (body == null)
            {
                // Unicode categories / scripts pass through (\p{L}, \p{Lu}, \p{Greek} -> IsGreek)
                string name = prop;
                string longCat = LongCategory(name);
                if (longCat != null) name = longCat;
                else if (name.Length > 2 && !name.StartsWith("Is", StringComparison.Ordinal) && char.IsUpper(name[0]) && name.ToLowerInvariant() != name && !IsCategory(name))
                    name = "Is" + name;
                return (neg ? "\\P{" : "\\p{") + name + "}";
            }
            if (!neg) return inClass ? body : "[" + body + "]";
            if (inClass)
            {
                // negated posix inside a class: only expressible for single-category bodies
                if (body.StartsWith("\\p{", StringComparison.Ordinal) && body.IndexOf('\\', 1) < 0) return "\\P" + body.Substring(2);
                if (body == "\\w") return "\\W";
                if (body == "\\s") return "\\S";
                if (body == "\\d") return "\\D";
                throw new NotSupportedException("negated property inside a class");
            }
            return "[^" + body + "]";
        }

        private static string LongCategory(string n)
        {
            switch (n.Replace("_", "").Replace(" ", "").ToLowerInvariant())
            {
                case "letter": return "L";
                case "mark": return "M";
                case "number": return "N";
                case "punctuation": return "P";
                case "symbol": return "S";
                case "separator": return "Z";
                case "other": return "C";
                case "surrogate": return "Cs";
                case "control": return "Cc";
                case "format": return "Cf";
                case "privateuse": return "Co";
                case "unassigned": return "Cn";
                case "uppercaseletter": return "Lu";
                case "lowercaseletter": return "Ll";
                case "decimalnumber": return "Nd";
                case "spaceseparator": return "Zs";
                case "newline": return null;
            }
            return null;
        }

        private static bool IsCategory(string n)
        {
            switch (n)
            {
                case "L": case "Lu": case "Ll": case "Lt": case "Lm": case "Lo": case "M": case "Mn": case "Mc": case "Me":
                case "N": case "Nd": case "Nl": case "No": case "P": case "Pc": case "Pd": case "Ps": case "Pe": case "Pi":
                case "Pf": case "Po": case "S": case "Sm": case "Sc": case "Sk": case "So": case "Z": case "Zs": case "Zl":
                case "Zp": case "C": case "Cc": case "Cf": case "Cs": case "Co": case "Cn":
                    return true;
            }
            return false;
        }

        /// <summary>Class body (without brackets) for POSIX / Oniguruma property names, or null.</summary>
        private static string PosixBody(string name)
        {
            switch (name)
            {
                case "alnum": return "\\p{L}\\p{M}\\p{Nd}";
                case "alpha": return "\\p{L}\\p{M}";
                case "digit": return "\\d";
                case "xdigit": return "0-9A-Fa-f";
                case "upper": return "\\p{Lu}";
                case "lower": return "\\p{Ll}";
                case "space": return "\\s";
                case "blank": return " \\t";
                case "punct": return "\\p{P}\\p{S}";
                case "word": return "\\w";
                case "cntrl": return "\\p{Cc}";
                case "print": return "\\P{C}";
                case "graph": return "\\p{L}\\p{M}\\p{N}\\p{P}\\p{S}";
                case "ascii": return "\\x00-\\x7F";
                case "any": return "\\s\\S";
            }
            return null;
        }

        /// <summary>A POSIX bracket "[:name:]" / "[:^name:]" at s[i]; end = index of its final ']'.</summary>
        private static bool TryPosixAt(string s, int i, out int end, out string name)
        {
            end = -1;
            name = null;
            if (i + 1 >= s.Length || s[i] != '[' || s[i + 1] != ':') return false;
            int k = i + 2;
            if (k < s.Length && s[k] == '^') k++;
            int letters = k;
            while (k < s.Length && char.IsLetter(s[k])) k++;
            if (k == letters || k + 1 >= s.Length || s[k] != ':' || s[k + 1] != ']') return false;
            name = s.Substring(i + 2, k - i - 2);
            end = k + 1;
            return true;
        }

        /// <summary>Index just after the ']' closing the class opened at p[open] (nested classes / escapes respected).</summary>
        private static int FindClassEnd(string p, int open)
        {
            int depth = 0;
            for (int i = open; i < p.Length; i++)
            {
                char c = p[i];
                if (c == '\\') { i++; continue; }
                if (c == '[')
                {
                    int pe;
                    string pn;
                    if (depth > 0 && TryPosixAt(p, i, out pe, out pn)) { i = pe; continue; }
                    depth++;
                    // a ']' right after '[' or '[^' is literal
                    if (i + 1 < p.Length && p[i + 1] == '^') i++;
                    if (i + 1 < p.Length && p[i + 1] == ']') i++;
                    continue;
                }
                if (c == ']') { depth--; if (depth == 0) return i + 1; }
            }
            throw new NotSupportedException("unterminated character class");
        }

        /// <summary>
        /// Translates a character class starting at p[i]=='['. Nested positive classes are flattened, nested negated
        /// classes become an alternation ([a[^b]] -> (?:[a]|[^b])) and intersections use look-aheads
        /// ([a&amp;&amp;b] -> (?=[a])[b]), so the result may be a group rather than a single class.
        /// </summary>
        private static int TranslateClass(string p, int i, StringBuilder sb)
        {
            int end = FindClassEnd(p, i);
            string cls = p.Substring(i, end - i);
            sb.Append(TranslateClassText(cls, 0));
            return end;
        }

        private static string TranslateClassText(string cls, int depth)
        {
            if (depth > 8) throw new NotSupportedException("class nested too deeply");
            int i = 1;
            bool neg = false;
            if (i < cls.Length && cls[i] == '^') { neg = true; i++; }
            string body = cls.Substring(i, cls.Length - 1 - i);
            // split on top-level &&
            List<string> operands = new List<string>();
            int nest = 0, last = 0;
            for (int k = 0; k < body.Length; k++)
            {
                char c = body[k];
                if (c == '\\') { k++; continue; }
                if (c == '[')
                {
                    int pe;
                    string pn;
                    if (TryPosixAt(body, k, out pe, out pn)) { k = pe; continue; }
                    nest++;
                    continue;
                }
                if (c == ']') { nest--; continue; }
                if (nest == 0 && c == '&' && k + 1 < body.Length && body[k + 1] == '&')
                {
                    operands.Add(body.Substring(last, k - last));
                    last = k + 2;
                    k++;
                }
            }
            operands.Add(body.Substring(last));
            if (operands.Count > 1)
            {
                StringBuilder inter = new StringBuilder();
                for (int k = 0; k < operands.Count; k++)
                {
                    string op = operands[k];
                    bool wholeClass = op.StartsWith("[", StringComparison.Ordinal) && FindClassEnd(op, 0) == op.Length;
                    string piece = wholeClass ? TranslateClassText(op, depth + 1) : TranslateClassText("[" + op + "]", depth + 1);
                    if (k < operands.Count - 1) inter.Append("(?=").Append(piece).Append(')');
                    else inter.Append(piece);
                }
                return neg ? "(?!" + inter + ")[\\s\\S]" : "(?:" + inter + ")";
            }
            // union: flatten positive nested classes, collect negated ones as alternatives
            StringBuilder pos = new StringBuilder();
            List<string> alts = new List<string>();
            int j = 0;
            if (body.Length > 0 && body[0] == ']') { pos.Append("\\]"); j = 1; }
            while (j < body.Length)
            {
                char c = body[j];
                if (c == '\\')
                {
                    if (j + 1 >= body.Length) { pos.Append("\\\\"); break; }
                    j = TranslateEscape(body, j, pos, false, false, true);
                    continue;
                }
                if (c == '[')
                {
                    int pend;
                    string pname;
                    if (TryPosixAt(body, j, out pend, out pname))
                    {
                        int e = pend - 1;
                        {
                            string name = pname;
                            bool pneg = name.StartsWith("^", StringComparison.Ordinal);
                            if (pneg) name = name.Substring(1);
                            string pb = PosixBody(name.ToLowerInvariant());
                            if (pb == null) throw new NotSupportedException("unknown POSIX class " + name);
                            if (pneg)
                            {
                                try { pos.Append(MapProperty(name, true, true)); }
                                catch (NotSupportedException) { alts.Add("[^" + pb + "]"); }
                            }
                            else pos.Append(pb);
                            j = e + 2;
                            continue;
                        }
                    }
                    int ce = FindClassEnd(body, j);
                    string nested = body.Substring(j, ce - j);
                    if (nested.Length > 1 && nested[1] == '^') alts.Add(TranslateClassText(nested, depth + 1));
                    else
                    {
                        string t = TranslateClassText(nested, depth + 1);
                        if (t.StartsWith("[", StringComparison.Ordinal) && t.EndsWith("]", StringComparison.Ordinal) && !t.StartsWith("[^", StringComparison.Ordinal)) pos.Append(t, 1, t.Length - 2);
                        else alts.Add(t);
                    }
                    j = ce;
                    continue;
                }
                if (c == '-' && j + 1 < body.Length && body[j + 1] == '[') { pos.Append("\\-"); j++; continue; }
                pos.Append(c);
                j++;
            }
            if (alts.Count == 0) return (neg ? "[^" : "[") + pos + "]";
            StringBuilder u = new StringBuilder("(?:");
            bool first = true;
            if (pos.Length > 0) { u.Append('[').Append(pos).Append(']'); first = false; }
            foreach (string a in alts) { if (!first) u.Append('|'); u.Append(a); first = false; }
            u.Append(')');
            return neg ? "(?!" + u + ")[\\s\\S]" : u.ToString();
        }

        /// <summary>Escapes literal text so it can be spliced into an Oniguruma pattern (end/while back-references).</summary>
        public static string EscapeLiteral(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            StringBuilder sb = new StringBuilder(s.Length + 8);
            foreach (char c in s)
            {
                if ("\\|()[]{}^$.*+?-#/".IndexOf(c) >= 0) sb.Append('\\');
                sb.Append(c);
            }
            return sb.ToString();
        }
    }

    /// <summary>
    /// A TextMate regex source with lazily compiled .NET variants (\A / \G enabled or disabled). Invalid patterns never match.
    /// </summary>
    public sealed class TmRegex
    {
        public readonly string Source;
        public readonly bool HasG;
        public readonly bool HasA;
        public readonly int Id;
        private Regex[] variants = new Regex[4];
        private bool[] failed = new bool[4];
        public string Error;

        private static int nextId;
        public static readonly TimeSpan MatchTimeout = TimeSpan.FromMilliseconds(150);

        public TmRegex(string source)
        {
            Source = source ?? "";
            HasG = OnigurumaTranslator.ContainsAnchorG(Source);
            HasA = OnigurumaTranslator.ContainsAnchorA(Source);
            Id = System.Threading.Interlocked.Increment(ref nextId);
        }

        private int[] uses = new int[4];
        private string[] translated = new string[4];
        /// <summary>Searches after which a regex is recompiled with RegexOptions.Compiled (hot patterns only).</summary>
        public static int CompileThreshold = 400;
        /// <summary>Process-wide cap on compiled regexes (each costs JIT'd code; ~150 KB working set measured).</summary>
        public static int MaxCompiledRegexes = 64;
        public static bool AdaptiveCompilation = true;

        /// <summary>Variant for a search; frequently used patterns are upgraded to compiled regexes.</summary>
        public Regex GetForSearch(bool allowA, bool allowG)
        {
            int k = ((HasA && allowA) ? 1 : 0) | ((HasG && allowG) ? 2 : 0);
            Regex r = variants[k] ?? Get(allowA, allowG);
            if (r != null && AdaptiveCompilation && ++uses[k] == CompileThreshold && translated[k] != null && compiledCount + PendingCompilations < MaxCompiledRegexes)
            {
                // compile on a worker thread (Reflection.Emit costs milliseconds); the interpreted regex keeps
                // working until the compiled one is swapped in. Matching results are identical.
                EnqueueCompile(this, k, translated[k]);
            }
            return r;
        }

        // One background worker compiles hot regexes in order (parallel Reflection.Emit slowed the UI thread down).
        private static readonly Queue<KeyValuePair<TmRegex, KeyValuePair<int, string>>> compileQueue = new Queue<KeyValuePair<TmRegex, KeyValuePair<int, string>>>();
        private static bool compileWorkerRunning;
        private const string JitWarmupText = "  public static async function x(a, b) { return \"s\" + 'c' + `t${1}` + /r+/g; } // c /* d */ <a href=\"x\">y</a> #k $v @d 0x1F 1.5e3\n";

        private static void EnqueueCompile(TmRegex rx, int slot, string pattern)
        {
            lock (compileQueue)
            {
                compileQueue.Enqueue(new KeyValuePair<TmRegex, KeyValuePair<int, string>>(rx, new KeyValuePair<int, string>(slot, pattern)));
                if (compileWorkerRunning) return;
                compileWorkerRunning = true;
            }
            System.Threading.ThreadPool.QueueUserWorkItem(delegate
            {
                while (true)
                {
                    KeyValuePair<TmRegex, KeyValuePair<int, string>> job;
                    lock (compileQueue)
                    {
                        if (compileQueue.Count == 0) { compileWorkerRunning = false; return; }
                        job = compileQueue.Dequeue();
                    }
                    try
                    {
                        Regex compiled = new Regex(job.Value.Value, RegexOptions.CultureInvariant | RegexOptions.Compiled, MatchTimeout);
                        // run it once here so the JIT compiles the generated code on this thread, not on the UI thread
                        try { compiled.Match(JitWarmupText); compiled.Match(JitWarmupText, JitWarmupText.Length / 2); } catch (Exception) { }
                        job.Key.variants[job.Value.Key] = compiled;
                        System.Threading.Interlocked.Increment(ref compiledCount);
                    }
                    catch (Exception) { }
                }
            });
        }

        private static int compiledCount;
        /// <summary>Regexes upgraded to RegexOptions.Compiled so far (process-wide).</summary>
        public static int CompiledCount { get { return compiledCount; } }

        /// <summary>Hot regexes still waiting for background compilation (tests / diagnostics).</summary>
        public static int PendingCompilations { get { lock (compileQueue) { return compileQueue.Count + (compileWorkerRunning ? 1 : 0); } } }

        public Regex Get(bool allowA, bool allowG)
        {
            int k = ((HasA && allowA) ? 1 : 0) | ((HasG && allowG) ? 2 : 0);
            Regex r = variants[k];
            if (r != null || failed[k]) return r;
            string net = OnigurumaTranslator.Translate(Source, HasA && allowA, HasG && allowG);
            if (net == null)
            {
                failed[k] = true;
                Error = OnigurumaTranslator.LastError ?? "unsupported";
                TextMateDiagnostics.Record(Source, Error);
                return null;
            }
            try
            {
                r = new Regex(net, RegexOptions.CultureInvariant, MatchTimeout);
                variants[k] = r;
                translated[k] = net;
            }
            catch (ArgumentException ex)
            {
                failed[k] = true;
                Error = ex.Message;
                TextMateDiagnostics.Record(Source, ex.Message);
            }
            return r;
        }
    }

    /// <summary>Collects regexes that could not be translated (shown in tests / logs; bounded).</summary>
    public static class TextMateDiagnostics
    {
        private static readonly object sync = new object();
        private static readonly List<string> errors = new List<string>();
        public static int TimeoutCount;

        public static void RecordTimeout(string source)
        {
            lock (sync)
            {
                TimeoutCount++;
                if (errors.Count < 200) errors.Add("timeout  ::  " + (source.Length > 160 ? source.Substring(0, 160) + "…" : source));
            }
        }

        public static void Record(string source, string error)
        {
            lock (sync)
            {
                if (errors.Count < 200) errors.Add(error + "  ::  " + (source.Length > 160 ? source.Substring(0, 160) + "…" : source));
            }
        }

        public static List<string> Snapshot()
        {
            lock (sync) { return new List<string>(errors); }
        }

        public static void Clear()
        {
            lock (sync) { errors.Clear(); TimeoutCount = 0; }
        }
    }
}
