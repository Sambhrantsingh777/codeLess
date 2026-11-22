using System;
using System.Collections.Generic;

namespace codeLess.Studio
{
    /// <summary>
    /// Faint end-of-line text drawn by both editor canvases (inline git blame, "Send Request" code lens). Supplied per
    /// visible line by a provider delegate set on the editor control; must be cheap (dictionary lookups only).
    /// </summary>
    public class EditorLineAnnotation
    {
        public const string KindBlame = "blame";
        public const string KindCodeLens = "codelens";

        public string Kind = KindBlame;
        public string Text = "";
        /// <summary>Code lenses are drawn in the accent color and are clickable; blame is muted.</summary>
        public bool IsAction;
        /// <summary>Feature payload (GitBlameLine, HttpRequestBlock, ...).</summary>
        public object Tag;
        /// <summary>
        /// Optional clickable parts drawn as "A | B | C" (merge-conflict lens). Canvases draw <see cref="GetSegmentParts"/>
        /// and report the clicked part, whose <see cref="SegmentIndex"/> tells which one was chosen.
        /// </summary>
        public string[] Segments;
        /// <summary>Index into the parent's Segments for a part; -1 for ordinary annotations.</summary>
        public int SegmentIndex = -1;
        public EditorLineAnnotation Parent;
        private EditorLineAnnotation[] segmentParts;

        /// <summary>Stable per-segment annotation objects (identity is kept so hover highlighting works per part).</summary>
        public EditorLineAnnotation[] GetSegmentParts()
        {
            if (Segments == null) return null;
            if (segmentParts == null || segmentParts.Length != Segments.Length)
            {
                segmentParts = new EditorLineAnnotation[Segments.Length];
                for (int i = 0; i < Segments.Length; i++)
                {
                    EditorLineAnnotation p = new EditorLineAnnotation();
                    p.Kind = Kind;
                    p.Text = Segments[i];
                    p.IsAction = IsAction;
                    p.Tag = Tag;
                    p.SegmentIndex = i;
                    p.Parent = this;
                    segmentParts[i] = p;
                }
            }
            return segmentParts;
        }

        public const string SegmentSeparator = "  |  ";
    }

    /// <summary>Settings for the built-in developer tools (persisted with the rest of settings.json on both UIs).</summary>
    public partial class IdePersistentSettings
    {
        /// <summary>Inline (current line) git blame is on by default; this stores the opt-out.</summary>
        public bool DisableInlineGitBlame { get; set; }
        /// <summary>Selected .http environment name ("" = none / $shared only).</summary>
        public string HttpClientEnvironment { get; set; }
        /// <summary>Request timeout in seconds for the HTTP client (0 = default 30 s).</summary>
        public int HttpClientTimeoutSeconds { get; set; }
    }

    public enum CodeTokenKind
    {
        Text = 0,
        Keyword = 1,
        Control = 2,
        Type = 3,
        Function = 4,
        String = 5,
        Comment = 6,
        Number = 7,
        Operator = 8
    }

    public class CodeToken
    {
        public int Start;
        public int Length;
        public CodeTokenKind Kind;

        public CodeToken(int start, int length, CodeTokenKind kind) { Start = start; Length = length; Kind = kind; }
    }

    /// <summary>
    /// Line tokenizer mirroring the Windows editor's highlighter (CodeLessVirtualCanvas.DrawSyntaxHighlightedLine) as data,
    /// for places that need colored code outside the editor (Markdown code fences on Windows).
    /// </summary>
    public static class SimpleCodeTokenizer
    {
        private static readonly HashSet<string> Control = new HashSet<string>(StringComparer.Ordinal)
        {
            "if", "else", "for", "foreach", "while", "do", "switch", "case", "break", "continue", "return", "try", "catch",
            "finally", "throw", "goto", "yield", "await", "elif", "except", "raise", "with", "match", "loop", "in", "of"
        };

        private static readonly HashSet<string> Storage = new HashSet<string>(StringComparer.Ordinal)
        {
            "public", "private", "protected", "internal", "static", "readonly", "const", "class", "struct", "interface",
            "enum", "namespace", "using", "import", "from", "export", "default", "function", "def", "var", "let", "fn",
            "new", "this", "self", "base", "super", "override", "virtual", "abstract", "sealed", "async", "true", "false",
            "null", "None", "True", "False", "nil", "void", "lambda", "package", "func", "type", "impl", "pub", "mut",
            "trait", "extends", "implements", "typeof", "instanceof", "delete", "get", "set", "echo", "then", "fi", "esac"
        };

        private static readonly HashSet<string> Types = new HashSet<string>(StringComparer.Ordinal)
        {
            "int", "long", "short", "byte", "bool", "boolean", "char", "string", "float", "double", "decimal", "object",
            "uint", "ulong", "usize", "i32", "i64", "u8", "u32", "u64", "f32", "f64", "str", "number", "any", "dict", "list"
        };

        public static List<CodeToken> Tokenize(string line, bool hashComments)
        {
            List<CodeToken> list = new List<CodeToken>();
            if (string.IsNullOrEmpty(line)) return list;
            int i = 0, n = line.Length;
            while (i < n)
            {
                char c = line[i];
                if (char.IsWhiteSpace(c)) { i++; continue; }
                if ((c == '/' && i + 1 < n && line[i + 1] == '/') || (hashComments && c == '#'))
                {
                    list.Add(new CodeToken(i, n - i, CodeTokenKind.Comment));
                    break;
                }
                if (c == '"' || c == '\'' || c == '`')
                {
                    int s = i++;
                    while (i < n)
                    {
                        if (line[i] == '\\') { i += 2; continue; }
                        if (line[i] == c) { i++; break; }
                        i++;
                    }
                    i = Math.Min(i, n);
                    list.Add(new CodeToken(s, i - s, CodeTokenKind.String));
                    continue;
                }
                if (char.IsDigit(c))
                {
                    int s = i;
                    while (i < n && (char.IsLetterOrDigit(line[i]) || line[i] == '.')) i++;
                    list.Add(new CodeToken(s, i - s, CodeTokenKind.Number));
                    continue;
                }
                if (char.IsLetter(c) || c == '_' || c == '@' || c == '$')
                {
                    int s = i++;
                    while (i < n && (char.IsLetterOrDigit(line[i]) || line[i] == '_')) i++;
                    string w = line.Substring(s, i - s);
                    CodeTokenKind k = CodeTokenKind.Text;
                    if (w[0] == '@' || w[0] == '$') k = CodeTokenKind.Keyword;
                    else if (Control.Contains(w)) k = CodeTokenKind.Control;
                    else if (Storage.Contains(w)) k = CodeTokenKind.Keyword;
                    else if (Types.Contains(w) || (w.Length > 1 && char.IsUpper(w[0]) && !IsAllUpper(w))) k = CodeTokenKind.Type;
                    else if (i < n && line[i] == '(') k = CodeTokenKind.Function;
                    list.Add(new CodeToken(s, i - s, k));
                    continue;
                }
                int o = i;
                while (i < n && !char.IsWhiteSpace(line[i]) && !char.IsLetterOrDigit(line[i]) && line[i] != '_' && line[i] != '"' && line[i] != '\'' && line[i] != '`' && !(hashComments && line[i] == '#'))
                {
                    if (line[i] == '/' && i + 1 < n && line[i + 1] == '/') break;
                    i++;
                }
                if (i == o) i++;
                list.Add(new CodeToken(o, i - o, CodeTokenKind.Operator));
            }
            return list;
        }

        private static bool IsAllUpper(string w)
        {
            foreach (char c in w) if (char.IsLetter(c) && !char.IsUpper(c)) return false;
            return true;
        }

        /// <summary>Languages whose line comments start with '#'.</summary>
        public static bool UsesHashComments(string language)
        {
            string l = (language ?? "").ToLowerInvariant();
            return l == "py" || l == "python" || l == "sh" || l == "bash" || l == "zsh" || l == "shell" || l == "ps1" || l == "powershell"
                || l == "rb" || l == "ruby" || l == "yaml" || l == "yml" || l == "toml" || l == "r" || l == "perl" || l == "pl" || l == "makefile"
                || l == "dockerfile" || l == "ini" || l == "conf" || l == "http" || l == "rest" || l == "";
        }
    }
}
