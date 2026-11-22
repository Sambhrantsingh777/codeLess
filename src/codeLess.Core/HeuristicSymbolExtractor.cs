using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace codeLess.Studio
{
    /// <summary>
    /// Language-aware, line-based declaration detection for the outline, breadcrumbs, sticky headers and workspace
    /// symbol map when no language server is running. Handles real-world modifiers (sealed/partial/readonly/abstract/
    /// export default/pub(crate)...), generic return types, records, traits, impls, Go receivers, Python/Ruby defs,
    /// arrow-function consts and Markdown headings. C# 5 only (compiled by legacy csc.exe).
    /// </summary>
    public static class HeuristicSymbolExtractor
    {
        private const RegexOptions Opt = RegexOptions.Compiled | RegexOptions.CultureInvariant;

        // class / struct / interface / enum / record / trait / protocol / object / namespace / module / impl / type alias
        private static readonly Regex TypeDecl = new Regex(
            @"^\s*(?:(?:export|default|declare|public|private|protected|internal|static|sealed|abstract|partial|readonly|unsafe|final|open|data|inline|value|ref|file|new|pub(?:\([a-z]+\))?|@\w+(?:\([^)]*\))?)\s+)*" +
            @"(class|struct|interface|enum|record(?:\s+(?:struct|class))?|trait|protocol|object|namespace|module|impl|type|union)\s+" +
            @"(?:<[^>]*>\s*)?([A-Za-z_$][\w$]*(?:(?:::|\.)[A-Za-z_]\w*)*)", Opt);

        // function keywords: function / fn / def / func (with Go receivers) / sub / proc
        private static readonly Regex FuncKeyword = new Regex(
            @"^\s*(?:(?:export|default|declare|public|private|protected|internal|static|async|unsafe|extern|const|pub(?:\([a-z]+\))?|override|open|final|inline|suspend|private\s+set)\s+)*" +
            @"(?:function\*?|fn|def|func|sub|proc|fun)\s+(?:\([^)]*\)\s*)?([A-Za-z_$][\w$]*[!?]?)", Opt);

        // const foo = (...) => / const foo = async function / foo: function(  (JS/TS)
        private static readonly Regex ArrowConst = new Regex(
            @"^\s*(?:export\s+)?(?:const|let|var)\s+([A-Za-z_$][\w$]*)\s*(?::[^=]+)?=\s*(?:async\s+)?(?:function\b|\([^)]*\)\s*(?::[^=]+)?=>|[A-Za-z_$][\w$]*\s*=>)", Opt);

        // C-family method / constructor: [modifiers] [ReturnType] Name(  — return type may be generic, array, nullable, qualified
        private static readonly Regex CMethod = new Regex(
            @"^\s*(?:\[[^\]]*\]\s*)*(?:(?:public|private|protected|internal|static|virtual|override|abstract|sealed|async|extern|unsafe|new|partial|readonly|final|synchronized|native|inline|constexpr|explicit|friend|operator)\s+)*" +
            @"(?:(?:[A-Za-z_][\w.:]*(?:<[^()]*?>)?(?:\[\s*,*\s*\])*\??\*?&?)\s+)?([A-Za-z_~][\w]*)\s*(?:<[^()]*?>)?\s*\(", Opt);

        // C# / Java / Kotlin properties: [modifiers] Type Name { get; ... }
        private static readonly Regex CsProperty = new Regex(
            @"^\s*(?:(?:public|private|protected|internal|static|virtual|override|abstract|sealed|new|readonly|required)\s+)+[A-Za-z_][\w.<>,\[\]? ]*\s+([A-Za-z_]\w*)\s*\{\s*(?:get|set|init)\b", Opt);

        private static readonly Regex MdHeading = new Regex(@"^(#{1,6})\s+(.+?)\s*#*\s*$", Opt);

        private static readonly HashSet<string> NotMethods = new HashSet<string>(StringComparer.Ordinal)
        {
            "if", "for", "foreach", "while", "switch", "catch", "using", "lock", "return", "new", "else", "do", "try", "finally",
            "throw", "await", "yield", "sizeof", "typeof", "nameof", "default", "checked", "unchecked", "fixed", "when", "case",
            "goto", "in", "is", "as", "with", "match", "elif", "print", "super", "this", "base", "delete", "void"
        };

        private static readonly HashSet<string> CFamily = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "cs", "java", "c", "cpp", "cc", "cxx", "h", "hpp", "hh", "m", "mm", "kt", "kts", "scala", "swift", "dart", "php",
            "js", "mjs", "cjs", "jsx", "ts", "tsx", "mts", "cts", "go", "rs", "groovy", "gradle"
        };

        /// <summary>Language id normalised from a buffer language or file extension ("csharp" -> "cs", "TypeScript" -> "ts").</summary>
        public static string Normalize(string language)
        {
            string l = (language ?? "").Trim().TrimStart('.').ToLowerInvariant();
            switch (l)
            {
                case "csharp": case "c#": return "cs";
                case "typescript": return "ts";
                case "typescriptreact": return "tsx";
                case "javascript": return "js";
                case "javascriptreact": return "jsx";
                case "python": return "py";
                case "rust": return "rs";
                case "golang": return "go";
                case "markdown": return "md";
                case "ruby": return "rb";
                case "kotlin": return "kt";
                default: return l;
            }
        }

        /// <summary>
        /// Detects a declaration on <paramref name="line"/>. Returns false for statements, calls and control flow.
        /// <paramref name="kind"/> is one of class/struct/interface/enum/record/trait/namespace/module/impl/type/fn/method/prop/heading.
        /// </summary>
        public static bool TryMatch(string line, string language, out string kind, out string name)
        {
            kind = null; name = null;
            if (string.IsNullOrEmpty(line) || line.Length > 400) return false;
            string trimmed = line.TrimStart();
            if (trimmed.Length == 0) return false;
            string lang = Normalize(language);

            if (lang == "md" || lang == "markdown" || lang == "mdx")
            {
                Match h = MdHeading.Match(line);
                if (!h.Success) return false;
                kind = "heading";
                name = h.Groups[2].Value;
                return true;
            }

            // comments / preprocessor / decorators-only lines
            // comments, preprocessor lines (#region, #if) and shell/Python/Ruby comments are never declarations
            if (trimmed.StartsWith("//") || trimmed.StartsWith("/*") || trimmed.StartsWith("*") || trimmed.StartsWith("--") || trimmed.StartsWith("#"))
                return false;

            Match m = TypeDecl.Match(line);
            if (m.Success)
            {
                string kw = m.Groups[1].Value;
                if (kw.StartsWith("record")) kw = "record";
                // `type` is only a declaration keyword in TS / Go / Rust / F#-likes; avoid "type = ..." assignments elsewhere
                if (kw == "type" && !(lang == "ts" || lang == "tsx" || lang == "mts" || lang == "cts" || lang == "go" || lang == "rs" || lang == "swift" || lang == "kt"))
                    return TryFunctionOrMethod(line, trimmed, lang, out kind, out name);
                if (kw == "object" && !(lang == "kt" || lang == "scala")) return TryFunctionOrMethod(line, trimmed, lang, out kind, out name);
                if (kw == "module" && !(lang == "rb" || lang == "ts" || lang == "js")) return TryFunctionOrMethod(line, trimmed, lang, out kind, out name);
                kind = kw == "union" ? "struct" : kw;
                name = m.Groups[2].Value;
                return true;
            }
            return TryFunctionOrMethod(line, trimmed, lang, out kind, out name);
        }

        private static bool TryFunctionOrMethod(string line, string trimmed, string lang, out string kind, out string name)
        {
            kind = null; name = null;
            Match f = FuncKeyword.Match(line);
            if (f.Success)
            {
                kind = "fn";
                name = f.Groups[1].Value;
                return !NotMethods.Contains(name);
            }
            if (lang == "js" || lang == "jsx" || lang == "ts" || lang == "tsx" || lang == "mjs" || lang == "cjs" || lang == "mts" || lang == "cts")
            {
                Match a = ArrowConst.Match(line);
                if (a.Success) { kind = "fn"; name = a.Groups[1].Value; return true; }
            }
            if (!CFamily.Contains(lang) && lang.Length > 0) return false;

            if (lang == "cs" || lang == "java" || lang == "kt")
            {
                Match p = CsProperty.Match(line);
                if (p.Success) { kind = "prop"; name = p.Groups[1].Value; return true; }
            }

            // A method declaration line, not a call / statement: ends with `{`, `)`, `=>`... or an opening brace follows,
            // and never ends with `;` (except abstract/interface members, which we accept when they start with a modifier).
            if (trimmed.IndexOf('(') < 0) return false;
            if (trimmed.StartsWith("return ") || trimmed.StartsWith("await ") || trimmed.StartsWith("new ") || trimmed.StartsWith("throw ")) return false;
            if (trimmed.IndexOf('=') >= 0 && trimmed.IndexOf('=') < trimmed.IndexOf('(') && trimmed.IndexOf("=>", StringComparison.Ordinal) != trimmed.IndexOf('=')) return false;
            Match c = CMethod.Match(line);
            if (!c.Success) return false;
            string cand = c.Groups[1].Value;
            if (NotMethods.Contains(cand)) return false;
            string prefix = line.Substring(0, c.Groups[1].Index).Trim();
            bool hasModifierOrType = prefix.Length > 0;
            string end = trimmed.TrimEnd();
            bool endsLikeStatement = end.EndsWith(";") && !(end.Contains(" abstract ") || end.StartsWith("abstract ") || IsInterfaceMemberish(prefix));
            if (endsLikeStatement) return false;
            if (!hasModifierOrType)
            {
                // bare `Name(...)` is a call unless it's a JS/TS class method `name(args) {`
                bool jsLike = lang == "js" || lang == "jsx" || lang == "ts" || lang == "tsx" || lang == "mjs" || lang == "cjs";
                if (!(jsLike && (end.EndsWith("{") || end.EndsWith(") {")))) return false;
            }
            // prefix must look like modifiers / a type, not an expression (`foo.bar baz(` is fine, `x + y(` is not)
            for (int i = 0; i < prefix.Length; i++)
            {
                char ch = prefix[i];
                if (!(char.IsLetterOrDigit(ch) || ch == '_' || ch == ' ' || ch == '.' || ch == '<' || ch == '>' || ch == ',' || ch == '[' ||
                      ch == ']' || ch == '?' || ch == '*' || ch == '&' || ch == ':' || ch == '@' || ch == '(' || ch == ')' || ch == '"' || ch == '=' || ch == '\t'))
                    return false;
            }
            kind = "method";
            name = cand;
            return true;
        }

        private static bool IsInterfaceMemberish(string prefix)
        {
            // `Task<int> RunAsync(CancellationToken ct);` inside an interface — has a type, no body. Accept when the prefix
            // is a single type token (no `=`), which is how interface members look; plain calls have no prefix at all.
            if (string.IsNullOrEmpty(prefix)) return false;
            if (prefix.IndexOf('=') >= 0) return false;
            string[] parts = prefix.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
            return parts.Length >= 1 && parts.Length <= 3;
        }

        /// <summary>Extracts all symbols of a document (convenience for tests and the workspace map).</summary>
        public static List<KeyValuePair<int, string[]>> ExtractAll(IList<string> lines, string language)
        {
            List<KeyValuePair<int, string[]>> result = new List<KeyValuePair<int, string[]>>();
            if (lines == null) return result;
            for (int i = 0; i < lines.Count; i++)
            {
                string k, n;
                if (TryMatch(lines[i], language, out k, out n)) result.Add(new KeyValuePair<int, string[]>(i, new[] { k, n }));
            }
            return result;
        }
    }
}
