using System;
using System.Collections.Generic;
using System.Text;

namespace codeLess.Studio
{
    /// <summary>LSP range in protocol units (0-based line, character in the negotiated encoding).</summary>
    public class LspRange
    {
        public int StartLine;
        public int StartCharacter;
        public int EndLine;
        public int EndCharacter;

        public LspRange() { }
        public LspRange(int sl, int sc, int el, int ec) { StartLine = sl; StartCharacter = sc; EndLine = el; EndCharacter = ec; }

        public static LspRange Parse(object o)
        {
            if (o == null) return null;
            object s = LspJson.Get(o, "start");
            object e = LspJson.Get(o, "end");
            if (s == null || e == null) return null;
            return new LspRange(LspJson.GetInt(s, "line", 0), LspJson.GetInt(s, "character", 0), LspJson.GetInt(e, "line", 0), LspJson.GetInt(e, "character", 0));
        }

        public Dictionary<string, object> ToJson()
        {
            return LspJson.Obj("start", LspJson.Obj("line", StartLine, "character", StartCharacter),
                               "end", LspJson.Obj("line", EndLine, "character", EndCharacter));
        }

        public bool Contains(int line, int character)
        {
            if (line < StartLine || line > EndLine) return false;
            if (line == StartLine && character < StartCharacter) return false;
            if (line == EndLine && character > EndCharacter) return false;
            return true;
        }
    }

    public class LspLocation
    {
        public string Uri;
        public string FilePath;
        public LspRange Range;
        /// <summary>Optional preview of the line (filled in by the UI for reference lists).</summary>
        public string Preview;

        public static List<LspLocation> ParseMany(object result)
        {
            List<LspLocation> list = new List<LspLocation>();
            if (result == null) return list;
            List<object> arr = result as List<object>;
            if (arr == null) { LspLocation one = ParseOne(result); if (one != null) list.Add(one); return list; }
            foreach (object o in arr)
            {
                LspLocation l = ParseOne(o);
                if (l != null) list.Add(l);
            }
            return list;
        }

        public static LspLocation ParseOne(object o)
        {
            if (o == null) return null;
            // Location { uri, range } or LocationLink { targetUri, targetRange, targetSelectionRange }
            string uri = LspJson.GetString(o, "uri");
            LspRange r;
            if (uri != null) r = LspRange.Parse(LspJson.Get(o, "range"));
            else
            {
                uri = LspJson.GetString(o, "targetUri");
                r = LspRange.Parse(LspJson.Get(o, "targetSelectionRange")) ?? LspRange.Parse(LspJson.Get(o, "targetRange"));
            }
            if (uri == null) return null;
            LspLocation l = new LspLocation();
            l.Uri = uri;
            l.FilePath = LspText.UriToPath(uri);
            l.Range = r ?? new LspRange();
            return l;
        }
    }

    public class LspTextEdit
    {
        public LspRange Range;
        public string NewText;

        public LspTextEdit() { }
        public LspTextEdit(LspRange r, string text) { Range = r; NewText = text; }

        public static LspTextEdit Parse(object o)
        {
            if (o == null) return null;
            LspRange r = LspRange.Parse(LspJson.Get(o, "range"));
            if (r == null) return null;
            string text = LspJson.GetString(o, "newText");
            if (text == null)
            {
                // SnippetTextEdit { range, snippet: { kind: "snippet", value } }
                object snip = LspJson.Get(o, "snippet");
                string v = LspJson.GetString(snip, "value");
                if (v != null) { int c, sl; text = LspText.ExpandSnippet(v, out c, out sl); }
            }
            return new LspTextEdit(r, text ?? "");
        }

        public static List<LspTextEdit> ParseMany(object o)
        {
            List<LspTextEdit> list = new List<LspTextEdit>();
            List<object> arr = o as List<object>;
            if (arr == null) return list;
            foreach (object x in arr)
            {
                LspTextEdit e = Parse(x);
                if (e != null) list.Add(e);
            }
            return list;
        }
    }

    /// <summary>A resource operation inside a WorkspaceEdit.documentChanges.</summary>
    public class LspResourceOperation
    {
        public string Kind; // "create", "rename", "delete"
        public string FilePath;
        public string NewFilePath;
        public bool Overwrite;
        public bool IgnoreIfExists;
        public bool Recursive;
    }

    /// <summary>A WorkspaceEdit flattened to an ordered list of steps (text edits per file and resource operations).</summary>
    public class LspWorkspaceEdit
    {
        public class Step
        {
            public string FilePath;            // set for text edits
            public List<LspTextEdit> Edits;    // set for text edits
            public int Version = -1;           // TextDocumentEdit version (-1 = any)
            public LspResourceOperation Resource; // set for resource operations
        }

        public List<Step> Steps = new List<Step>();

        public bool IsEmpty { get { return Steps.Count == 0; } }

        public int EditCount
        {
            get
            {
                int n = 0;
                foreach (Step s in Steps) n += s.Edits != null ? s.Edits.Count : (s.Resource != null ? 1 : 0);
                return n;
            }
        }

        public List<string> AffectedFiles()
        {
            List<string> files = new List<string>();
            HashSet<string> seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (Step s in Steps)
            {
                string p = s.FilePath ?? (s.Resource != null ? s.Resource.FilePath : null);
                if (p != null && seen.Add(LspText.PathKey(p))) files.Add(p);
            }
            return files;
        }

        public static LspWorkspaceEdit Parse(object o)
        {
            LspWorkspaceEdit we = new LspWorkspaceEdit();
            if (o == null) return we;
            List<object> docChanges = LspJson.GetList(o, "documentChanges");
            if (docChanges != null)
            {
                foreach (object dc in docChanges)
                {
                    string kind = LspJson.GetString(dc, "kind");
                    if (kind == null)
                    {
                        object td = LspJson.Get(dc, "textDocument");
                        string path = LspText.UriToPath(LspJson.GetString(td, "uri"));
                        if (path == null) continue;
                        Step st = new Step();
                        st.FilePath = path;
                        st.Version = LspJson.GetInt(td, "version", -1);
                        st.Edits = LspTextEdit.ParseMany(LspJson.Get(dc, "edits"));
                        we.Steps.Add(st);
                    }
                    else
                    {
                        LspResourceOperation op = new LspResourceOperation();
                        op.Kind = kind;
                        object opts = LspJson.Get(dc, "options");
                        op.Overwrite = LspJson.GetBool(opts, "overwrite", false);
                        op.IgnoreIfExists = LspJson.GetBool(opts, "ignoreIfExists", false);
                        op.Recursive = LspJson.GetBool(opts, "recursive", false);
                        if (kind == "rename")
                        {
                            op.FilePath = LspText.UriToPath(LspJson.GetString(dc, "oldUri"));
                            op.NewFilePath = LspText.UriToPath(LspJson.GetString(dc, "newUri"));
                        }
                        else op.FilePath = LspText.UriToPath(LspJson.GetString(dc, "uri"));
                        if (op.FilePath == null) continue;
                        Step st = new Step();
                        st.Resource = op;
                        we.Steps.Add(st);
                    }
                }
                we.MergeDuplicateFiles();
                return we;
            }
            Dictionary<string, object> changes = LspJson.GetObject(o, "changes");
            if (changes != null)
            {
                foreach (KeyValuePair<string, object> kv in changes)
                {
                    string path = LspText.UriToPath(kv.Key);
                    if (path == null) continue;
                    Step st = new Step();
                    st.FilePath = path;
                    st.Edits = LspTextEdit.ParseMany(kv.Value);
                    we.Steps.Add(st);
                }
            }
            we.MergeDuplicateFiles();
            return we;
        }

        /// <summary>
        /// Folds text-edit steps that target the same file (the same document reported under two URIs, e.g. a symlinked
        /// path and its real path) into the first step, dropping identical edits. Applying them separately would edit the
        /// document twice with stale ranges. Steps separated by a resource operation are left alone.
        /// </summary>
        public void MergeDuplicateFiles()
        {
            Dictionary<string, Step> firstByKey = new Dictionary<string, Step>(StringComparer.Ordinal);
            List<Step> result = new List<Step>();
            foreach (Step st in Steps)
            {
                if (st.Resource != null)
                {
                    firstByKey.Clear(); // ordering matters across create / rename / delete
                    result.Add(st);
                    continue;
                }
                string key = LspText.PathKey(st.FilePath);
                Step first;
                if (!firstByKey.TryGetValue(key, out first))
                {
                    firstByKey[key] = st;
                    result.Add(st);
                    continue;
                }
                if (first.Edits == null) first.Edits = new List<LspTextEdit>();
                if (st.Edits == null) continue;
                foreach (LspTextEdit e in st.Edits)
                {
                    bool dup = false;
                    foreach (LspTextEdit f in first.Edits)
                    {
                        if (f.Range != null && e.Range != null && f.Range.StartLine == e.Range.StartLine && f.Range.StartCharacter == e.Range.StartCharacter &&
                            f.Range.EndLine == e.Range.EndLine && f.Range.EndCharacter == e.Range.EndCharacter && string.Equals(f.NewText, e.NewText, StringComparison.Ordinal))
                        { dup = true; break; }
                    }
                    if (!dup) first.Edits.Add(e);
                }
            }
            Steps.Clear();
            Steps.AddRange(result);
        }
    }

    public class LspCompletionItem
    {
        public string Label = "";
        public string LabelDetail;      // labelDetails.detail
        public string LabelDescription; // labelDetails.description
        public int Kind;
        public string Detail;
        public string Documentation;  // plain text
        public string SortText;
        public string FilterText;
        public string InsertText;
        public bool IsSnippet;        // insertTextFormat == 2
        public LspTextEdit TextEdit;  // edit (or the "insert" range of an InsertReplaceEdit)
        public LspRange ReplaceRange; // "replace" range of an InsertReplaceEdit
        public List<LspTextEdit> AdditionalTextEdits = new List<LspTextEdit>();
        public bool Preselect;
        public bool Deprecated;
        public List<string> CommitCharacters;
        public object Command;        // raw Command
        public Dictionary<string, object> Raw; // original item (sent back for completionItem/resolve)
        public bool Resolved;
        /// <summary>Fuzzy score assigned by the client-side filter.</summary>
        public int Score;

        public string EffectiveFilterText { get { return string.IsNullOrEmpty(FilterText) ? Label : FilterText; } }
        public string EffectiveSortText { get { return string.IsNullOrEmpty(SortText) ? Label : SortText; } }

        public static LspCompletionItem Parse(object o, LspRange defaultEditRange, LspRange defaultReplaceRange, int defaultFormat)
        {
            Dictionary<string, object> d = o as Dictionary<string, object>;
            if (d == null) return null;
            LspCompletionItem it = new LspCompletionItem();
            it.Raw = d;
            it.Label = LspJson.GetString(d, "label") ?? "";
            object ld = LspJson.Get(d, "labelDetails");
            it.LabelDetail = LspJson.GetString(ld, "detail");
            it.LabelDescription = LspJson.GetString(ld, "description");
            it.Kind = LspJson.GetInt(d, "kind", 0);
            it.Detail = LspJson.GetString(d, "detail");
            it.Documentation = LspContent.ToPlainText(LspJson.Get(d, "documentation"));
            it.SortText = LspJson.GetString(d, "sortText");
            it.FilterText = LspJson.GetString(d, "filterText");
            it.InsertText = LspJson.GetString(d, "insertText");
            it.IsSnippet = LspJson.GetInt(d, "insertTextFormat", defaultFormat) == 2;
            it.Preselect = LspJson.GetBool(d, "preselect", false);
            it.Deprecated = LspJson.GetBool(d, "deprecated", false);
            List<object> tags = LspJson.GetList(d, "tags");
            if (tags != null && tags.Contains((long)1)) it.Deprecated = true;
            object te = LspJson.Get(d, "textEdit");
            if (te != null)
            {
                string nt = LspJson.GetString(te, "newText") ?? "";
                LspRange r = LspRange.Parse(LspJson.Get(te, "range"));
                if (r == null)
                {
                    r = LspRange.Parse(LspJson.Get(te, "insert"));
                    it.ReplaceRange = LspRange.Parse(LspJson.Get(te, "replace"));
                }
                if (r != null) it.TextEdit = new LspTextEdit(r, nt);
            }
            else if (defaultEditRange != null)
            {
                string text = LspJson.GetString(d, "textEditText") ?? it.InsertText ?? it.Label;
                it.TextEdit = new LspTextEdit(defaultEditRange, text);
                it.ReplaceRange = defaultReplaceRange;
            }
            it.AdditionalTextEdits = LspTextEdit.ParseMany(LspJson.Get(d, "additionalTextEdits"));
            List<object> cc = LspJson.GetList(d, "commitCharacters");
            if (cc != null)
            {
                it.CommitCharacters = new List<string>();
                foreach (object c in cc) { string s = c as string; if (s != null) it.CommitCharacters.Add(s); }
            }
            it.Command = LspJson.Get(d, "command");
            return it;
        }

        /// <summary>Merges a completionItem/resolve result into this item.</summary>
        public void MergeResolved(object o)
        {
            Resolved = true;
            if (o == null) return;
            string det = LspJson.GetString(o, "detail");
            if (!string.IsNullOrEmpty(det)) Detail = det;
            string doc = LspContent.ToPlainText(LspJson.Get(o, "documentation"));
            if (!string.IsNullOrEmpty(doc)) Documentation = doc;
            List<LspTextEdit> extra = LspTextEdit.ParseMany(LspJson.Get(o, "additionalTextEdits"));
            if (extra.Count > 0) AdditionalTextEdits = extra;
            object te = LspJson.Get(o, "textEdit");
            if (te != null && TextEdit == null)
            {
                LspRange r = LspRange.Parse(LspJson.Get(te, "range")) ?? LspRange.Parse(LspJson.Get(te, "insert"));
                if (r != null) TextEdit = new LspTextEdit(r, LspJson.GetString(te, "newText") ?? "");
            }
            if (LspJson.Has(o, "command")) Command = LspJson.Get(o, "command");
        }

        /// <summary>Short badge for the completion list (VS Code codicon names approximated as text).</summary>
        public static string KindName(int kind)
        {
            switch (kind)
            {
                case 1: return "text";
                case 2: return "method";
                case 3: return "function";
                case 4: return "constructor";
                case 5: return "field";
                case 6: return "variable";
                case 7: return "class";
                case 8: return "interface";
                case 9: return "module";
                case 10: return "property";
                case 11: return "unit";
                case 12: return "value";
                case 13: return "enum";
                case 14: return "keyword";
                case 15: return "snippet";
                case 16: return "color";
                case 17: return "file";
                case 18: return "reference";
                case 19: return "folder";
                case 20: return "enum member";
                case 21: return "constant";
                case 22: return "struct";
                case 23: return "event";
                case 24: return "operator";
                case 25: return "type parameter";
                default: return "";
            }
        }

        public static string KindGlyph(int kind)
        {
            switch (kind)
            {
                case 2: case 3: case 4: return "ƒ";
                case 5: case 10: return "◆";
                case 6: return "x";
                case 7: case 22: return "◇";
                case 8: return "◈";
                case 9: return "{}";
                case 13: case 20: return "≡";
                case 14: return "⌘";
                case 15: return "✂";
                case 17: return "▤";
                case 19: return "▣";
                case 21: return "π";
                case 25: return "T";
                default: return "·";
            }
        }
    }

    public class LspCompletionList
    {
        public bool IsIncomplete;
        public List<LspCompletionItem> Items = new List<LspCompletionItem>();

        public static LspCompletionList Parse(object result)
        {
            LspCompletionList list = new LspCompletionList();
            if (result == null) return list;
            List<object> arr = result as List<object>;
            LspRange defRange = null, defReplace = null;
            int defFormat = 1;
            if (arr == null)
            {
                list.IsIncomplete = LspJson.GetBool(result, "isIncomplete", false);
                arr = LspJson.GetList(result, "items");
                object defaults = LspJson.Get(result, "itemDefaults");
                if (defaults != null)
                {
                    object er = LspJson.Get(defaults, "editRange");
                    defRange = LspRange.Parse(er);
                    if (defRange == null && er != null)
                    {
                        defRange = LspRange.Parse(LspJson.Get(er, "insert"));
                        defReplace = LspRange.Parse(LspJson.Get(er, "replace"));
                    }
                    defFormat = LspJson.GetInt(defaults, "insertTextFormat", 1);
                }
            }
            if (arr == null) return list;
            foreach (object o in arr)
            {
                LspCompletionItem it = LspCompletionItem.Parse(o, defRange, defReplace, defFormat);
                if (it != null) list.Items.Add(it);
            }
            return list;
        }

        /// <summary>
        /// VS Code-like client filtering: keeps items whose filter text fuzzy-matches <paramref name="prefix"/>
        /// (in-order subsequence, case-insensitive), ranks prefix / camel-hump matches first, then sortText.
        /// </summary>
        public static List<LspCompletionItem> FilterAndSort(List<LspCompletionItem> items, string prefix, int max)
        {
            List<LspCompletionItem> result = new List<LspCompletionItem>();
            if (items == null) return result;
            prefix = prefix ?? "";
            foreach (LspCompletionItem it in items)
            {
                int score = FuzzyScore(it.EffectiveFilterText, prefix);
                if (score < 0) continue;
                it.Score = score + (it.Preselect && prefix.Length == 0 ? 50 : 0);
                result.Add(it);
            }
            result.Sort(delegate (LspCompletionItem a, LspCompletionItem b)
            {
                if (prefix.Length > 0)
                {
                    int c = b.Score.CompareTo(a.Score);
                    if (c != 0) return c;
                }
                int s = string.CompareOrdinal(a.EffectiveSortText, b.EffectiveSortText);
                if (s != 0) return s;
                return string.CompareOrdinal(a.Label, b.Label);
            });
            if (max > 0 && result.Count > max) result.RemoveRange(max, result.Count - max);
            return result;
        }

        /// <summary>-1 = no match. Higher is better.</summary>
        public static int FuzzyScore(string candidate, string pattern)
        {
            if (string.IsNullOrEmpty(pattern)) return 0;
            if (string.IsNullOrEmpty(candidate)) return -1;
            if (candidate.StartsWith(pattern, StringComparison.Ordinal)) return 1000 - candidate.Length;
            if (candidate.StartsWith(pattern, StringComparison.OrdinalIgnoreCase)) return 900 - candidate.Length;
            int ci = 0, score = 0, streak = 0;
            for (int pi = 0; pi < pattern.Length; pi++)
            {
                char pc = char.ToLowerInvariant(pattern[pi]);
                bool found = false;
                while (ci < candidate.Length)
                {
                    char cc = candidate[ci];
                    bool boundary = ci == 0 || candidate[ci - 1] == '_' || candidate[ci - 1] == '-' || candidate[ci - 1] == '.' ||
                                    (char.IsUpper(cc) && !char.IsUpper(candidate[ci - 1]));
                    ci++;
                    if (char.ToLowerInvariant(cc) == pc)
                    {
                        streak++;
                        score += 1 + (boundary ? 8 : 0) + streak * 2;
                        found = true;
                        break;
                    }
                    streak = 0;
                }
                if (!found) return -1;
                if (pi == 0 && ci > 1 && score < 9) score -= 5; // first char not at a word start
            }
            return Math.Max(1, score);
        }
    }

    public class LspSignatureHelp
    {
        public class Signature
        {
            public string Label = "";
            public string Documentation;
            public List<int[]> ParameterSpans = new List<int[]>(); // [start, end) within Label
            public List<string> ParameterDocs = new List<string>();
            public int ActiveParameter = -1;
        }

        public List<Signature> Signatures = new List<Signature>();
        public int ActiveSignature;
        public int ActiveParameter;

        public Signature Active
        {
            get
            {
                if (Signatures.Count == 0) return null;
                int i = ActiveSignature >= 0 && ActiveSignature < Signatures.Count ? ActiveSignature : 0;
                return Signatures[i];
            }
        }

        /// <summary>Span [start,end) of the active parameter in the active signature label, or null.</summary>
        public int[] ActiveParameterSpan
        {
            get
            {
                Signature s = Active;
                if (s == null) return null;
                int p = s.ActiveParameter >= 0 ? s.ActiveParameter : ActiveParameter;
                return p >= 0 && p < s.ParameterSpans.Count ? s.ParameterSpans[p] : null;
            }
        }

        public static LspSignatureHelp Parse(object o)
        {
            if (o == null) return null;
            LspSignatureHelp h = new LspSignatureHelp();
            h.ActiveSignature = LspJson.GetInt(o, "activeSignature", 0);
            h.ActiveParameter = LspJson.GetInt(o, "activeParameter", 0);
            List<object> sigs = LspJson.GetList(o, "signatures");
            if (sigs == null || sigs.Count == 0) return null;
            foreach (object so in sigs)
            {
                Signature s = new Signature();
                s.Label = LspJson.GetString(so, "label") ?? "";
                s.Documentation = LspContent.ToPlainText(LspJson.Get(so, "documentation"));
                s.ActiveParameter = LspJson.GetInt(so, "activeParameter", -1);
                List<object> ps = LspJson.GetList(so, "parameters");
                if (ps != null)
                {
                    int searchFrom = s.Label.IndexOf('(');
                    if (searchFrom < 0) searchFrom = 0;
                    foreach (object po in ps)
                    {
                        object lab = LspJson.Get(po, "label");
                        int[] span = null;
                        List<object> offs = lab as List<object>;
                        if (offs != null && offs.Count == 2)
                        {
                            span = new int[] { LspJson.ToInt(offs[0], 0), LspJson.ToInt(offs[1], 0) };
                        }
                        else
                        {
                            string ls = lab as string;
                            if (!string.IsNullOrEmpty(ls))
                            {
                                int at = s.Label.IndexOf(ls, searchFrom, StringComparison.Ordinal);
                                if (at < 0) at = s.Label.IndexOf(ls, StringComparison.Ordinal);
                                if (at >= 0) { span = new int[] { at, at + ls.Length }; searchFrom = at + ls.Length; }
                            }
                        }
                        if (span == null) span = new int[] { 0, 0 };
                        span[0] = Math.Max(0, Math.Min(span[0], s.Label.Length));
                        span[1] = Math.Max(span[0], Math.Min(span[1], s.Label.Length));
                        s.ParameterSpans.Add(span);
                        s.ParameterDocs.Add(LspContent.ToPlainText(LspJson.Get(po, "documentation")));
                    }
                }
                h.Signatures.Add(s);
            }
            return h;
        }
    }

    public class LspDocumentSymbol
    {
        public string Name = "";
        public string Detail;
        public int Kind;
        public LspRange Range;
        public LspRange SelectionRange;
        public string ContainerName;
        public int Depth;
        public List<LspDocumentSymbol> Children = new List<LspDocumentSymbol>();

        /// <summary>Parses DocumentSymbol[] (hierarchical) or SymbolInformation[] into a flat, depth-annotated list in document order.</summary>
        public static List<LspDocumentSymbol> ParseFlat(object result)
        {
            List<LspDocumentSymbol> flat = new List<LspDocumentSymbol>();
            List<object> arr = result as List<object>;
            if (arr == null) return flat;
            foreach (object o in arr) AddRecursive(o, 0, null, flat);
            flat.Sort(delegate (LspDocumentSymbol a, LspDocumentSymbol b)
            {
                int c = a.Range.StartLine.CompareTo(b.Range.StartLine);
                if (c != 0) return c;
                c = a.Range.StartCharacter.CompareTo(b.Range.StartCharacter);
                return c != 0 ? c : a.Depth.CompareTo(b.Depth);
            });
            return flat;
        }

        private static void AddRecursive(object o, int depth, string container, List<LspDocumentSymbol> flat)
        {
            if (o == null || depth > 32) return;
            LspDocumentSymbol s = new LspDocumentSymbol();
            s.Name = LspJson.GetString(o, "name") ?? "";
            s.Detail = LspJson.GetString(o, "detail");
            s.Kind = LspJson.GetInt(o, "kind", 0);
            s.Depth = depth;
            LspRange r = LspRange.Parse(LspJson.Get(o, "range"));
            if (r == null) r = LspRange.Parse(LspJson.Path(o, "location", "range")); // SymbolInformation
            s.Range = r ?? new LspRange();
            s.SelectionRange = LspRange.Parse(LspJson.Get(o, "selectionRange")) ?? s.Range;
            s.ContainerName = LspJson.GetString(o, "containerName") ?? container;
            flat.Add(s);
            List<object> kids = LspJson.GetList(o, "children");
            if (kids != null)
                foreach (object k in kids) AddRecursive(k, depth + 1, s.Name, flat);
        }

        /// <summary>Maps an LSP SymbolKind to the outline kinds codeLess already uses ("class", "fn", "method", ...).</summary>
        public static string ToOutlineKind(int kind)
        {
            switch (kind)
            {
                case 5: return "class";
                case 6: return "method";
                case 9: return "method";
                case 12: return "fn";
                case 10: return "enum";
                case 11: return "interface";
                case 23: return "struct";
                case 7: case 8: return "prop";
                case 13: case 14: return "var";
                case 2: case 3: case 4: return "module";
                default: return "var";
            }
        }
    }

    public class LspDiagnostic
    {
        public LspRange Range;
        public int Severity = 1;
        public string Code;
        public string Source;
        public string Message = "";
        public Dictionary<string, object> Raw;

        public static List<LspDiagnostic> ParseMany(object o)
        {
            List<LspDiagnostic> list = new List<LspDiagnostic>();
            List<object> arr = o as List<object>;
            if (arr == null) return list;
            foreach (object x in arr)
            {
                Dictionary<string, object> d = x as Dictionary<string, object>;
                if (d == null) continue;
                LspDiagnostic g = new LspDiagnostic();
                g.Raw = d;
                g.Range = LspRange.Parse(LspJson.Get(d, "range")) ?? new LspRange();
                g.Severity = LspJson.GetInt(d, "severity", 1);
                g.Code = LspJson.GetString(d, "code");
                g.Source = LspJson.GetString(d, "source");
                g.Message = LspJson.GetString(d, "message") ?? "";
                list.Add(g);
            }
            return list;
        }

        public static string SeverityName(int severity)
        {
            switch (severity)
            {
                case 1: return "Error";
                case 2: return "Warning";
                default: return "Info";
            }
        }
    }

    public class LspCodeAction
    {
        public string Title = "";
        public string Kind;
        public bool IsPreferred;
        public string DisabledReason;
        public LspWorkspaceEdit Edit;
        public object Command; // raw Command {title, command, arguments}
        public Dictionary<string, object> Raw;

        public static List<LspCodeAction> ParseMany(object result)
        {
            List<LspCodeAction> list = new List<LspCodeAction>();
            List<object> arr = result as List<object>;
            if (arr == null) return list;
            foreach (object o in arr)
            {
                Dictionary<string, object> d = o as Dictionary<string, object>;
                if (d == null) continue;
                LspCodeAction a = new LspCodeAction();
                a.Raw = d;
                a.Title = LspJson.GetString(d, "title") ?? "";
                if (LspJson.Get(d, "command") is string)
                {
                    // A bare Command { title, command, arguments }
                    a.Command = d;
                }
                else
                {
                    a.Kind = LspJson.GetString(d, "kind");
                    a.IsPreferred = LspJson.GetBool(d, "isPreferred", false);
                    a.DisabledReason = LspJson.GetString(LspJson.Get(d, "disabled"), "reason");
                    if (LspJson.Has(d, "edit")) a.Edit = LspWorkspaceEdit.Parse(LspJson.Get(d, "edit"));
                    a.Command = LspJson.Get(d, "command");
                }
                list.Add(a);
            }
            list.Sort(delegate (LspCodeAction x, LspCodeAction y)
            {
                int c = y.IsPreferred.CompareTo(x.IsPreferred);
                if (c != 0) return c;
                bool xq = x.Kind != null && x.Kind.StartsWith("quickfix", StringComparison.Ordinal);
                bool yq = y.Kind != null && y.Kind.StartsWith("quickfix", StringComparison.Ordinal);
                return yq.CompareTo(xq);
            });
            return list;
        }
    }

    /// <summary>MarkupContent / MarkedString / MarkedString[] -> plain text.</summary>
    public static class LspContent
    {
        public static string ToPlainText(object content)
        {
            if (content == null) return null;
            string s = content as string;
            if (s != null) return LspText.MarkdownToPlainText(s);
            List<object> arr = content as List<object>;
            if (arr != null)
            {
                StringBuilder sb = new StringBuilder();
                foreach (object o in arr)
                {
                    string part = ToPlainText(o);
                    if (string.IsNullOrEmpty(part)) continue;
                    if (sb.Length > 0) sb.Append("\n\n");
                    sb.Append(part);
                }
                return sb.ToString();
            }
            string kind = LspJson.GetString(content, "kind");
            string value = LspJson.GetString(content, "value");
            if (value == null) return null;
            if (kind == "plaintext") return value.Trim();
            // MarkupContent markdown, or MarkedString { language, value } (code)
            if (kind == null && LspJson.Has(content, "language")) return value.Trim();
            return LspText.MarkdownToPlainText(value);
        }
    }

    /// <summary>Hover result in plain text.</summary>
    public class LspHover
    {
        public string Text;
        public LspRange Range;

        public static LspHover Parse(object o)
        {
            if (o == null) return null;
            string text = LspContent.ToPlainText(LspJson.Get(o, "contents"));
            if (string.IsNullOrEmpty(text)) return null;
            LspHover h = new LspHover();
            h.Text = text;
            h.Range = LspRange.Parse(LspJson.Get(o, "range"));
            return h;
        }
    }
}
