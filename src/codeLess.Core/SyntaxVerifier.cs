using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
#if !NETFRAMEWORK
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
#endif

namespace codeLess.Studio
{
    public static class SyntaxVerifier
    {
        public static bool VerifyCodeSyntax(string filePath, string content, out string errorSummary, bool allowExternalProcess = true)
        {
            errorSummary = "";
            if (string.IsNullOrEmpty(content)) return true;

            string ext = Path.GetExtension(filePath ?? "").ToLowerInvariant();
            if (ext == ".cs" || ext == ".csx")
                return VerifyCSharpSyntax(content, out errorSummary);
            if (ext == ".py" || ext == ".pyw" || ext == ".pyi")
                return VerifyPythonSyntax(content, out errorSummary, allowExternalProcess);
            if (ext == ".js" || ext == ".mjs" || ext == ".cjs" || ext == ".ts" || ext == ".mts" || ext == ".cts" || ext == ".jsx" || ext == ".tsx")
                return VerifyJsTsSyntax(content, out errorSummary, allowExternalProcess && (ext == ".js" || ext == ".mjs" || ext == ".cjs"));
            if (ext == ".json" || ext == ".jsonc" || ext == ".json5")
                return VerifyJsonSyntax(content, out errorSummary);
            if (ext == ".go" || ext == ".rs" || ext == ".css" || ext == ".scss" || ext == ".less")
                return VerifyBracketsWithProfile(ext, content, out errorSummary);

            return true;
        }

        public static bool VerifyCSharpSyntax(string content, out string errorSummary)
        {
            errorSummary = "";
#if !NETFRAMEWORK
            return VerifyCSharpSyntaxWithRoslyn(content, out errorSummary);
#else
            return VerifyBraceAndOperatorBalance(".cs", content, out errorSummary);
#endif
        }

#if !NETFRAMEWORK
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static bool VerifyCSharpSyntaxWithRoslyn(string content, out string errorSummary)
        {
            errorSummary = "";
            try
            {
                CSharpParseOptions parseOptions = new CSharpParseOptions(LanguageVersion.Preview);
                SyntaxTree tree = CSharpSyntaxTree.ParseText(content ?? "", parseOptions);
                var errors = tree.GetDiagnostics()
                    .Where(d => d.Severity == DiagnosticSeverity.Error)
                    .Take(5)
                    .ToList();
                if (errors.Count > 0 && !string.IsNullOrEmpty(content) && content.IndexOf("#if", StringComparison.Ordinal) >= 0)
                {
                    HashSet<string> symbols = new HashSet<string>(StringComparer.Ordinal);
                    foreach (Match m in Regex.Matches(content, @"^\s*#(?:if|elif)\b([^\r\n]*)", RegexOptions.Multiline))
                    {
                        foreach (Match id in Regex.Matches(m.Groups[1].Value, @"\b[A-Za-z_][A-Za-z0-9_]*\b"))
                        {
                            string sym = id.Value;
                            if (sym != "true" && sym != "false") symbols.Add(sym);
                        }
                    }
                    if (symbols.Count > 0)
                    {
                        CSharpParseOptions withSyms = parseOptions.WithPreprocessorSymbols(symbols);
                        SyntaxTree treeWithSyms = CSharpSyntaxTree.ParseText(content, withSyms);
                        var symErrors = treeWithSyms.GetDiagnostics()
                            .Where(d => d.Severity == DiagnosticSeverity.Error)
                            .Take(5)
                            .ToList();
                        if (symErrors.Count == 0) return true;
                    }
                    // Also check if preprocessor branches individually balance via PreprocBranchFrame in EditorBracketCache
                    string bracketErr;
                    if (VerifyBracketsWithProfile(".cs", content, out bracketErr))
                        return true;
                }
                if (errors.Count > 0)
                {
                    StringBuilder sb = new StringBuilder();
                    foreach (var err in errors)
                    {
                        var span = err.Location.GetLineSpan();
                        int line1 = span.StartLinePosition.Line + 1;
                        sb.AppendFormat("[{0}] Line {1}: {2}; ", err.Id, line1, err.GetMessage());
                    }
                    errorSummary = sb.ToString().Trim();
                    return false;
                }
                return true;
            }
            catch (Exception ex)
            {
                errorSummary = ex.Message;
                return false;
            }
        }
#endif

        private static string cachedPython312Exe;
        private static bool checkedPython312;

        private static string GetHostPython312OrNewer()
        {
            if (checkedPython312) return cachedPython312Exe;
            checkedPython312 = true;
            string[] candidates = new string[] { "python3", "python" };
            for (int i = 0; i < candidates.Length; i++)
            {
                int exitCode;
                string output;
                if (TryRunSyntaxCheckProcess(candidates[i], "-I -c \"import sys; raise SystemExit(0 if sys.version_info >= (3, 12) else 1)\"", 2000, out exitCode, out output) && exitCode == 0)
                {
                    cachedPython312Exe = candidates[i];
                    return cachedPython312Exe;
                }
            }
            cachedPython312Exe = null;
            return null;
        }

        public static bool VerifyPythonSyntax(string content, out string errorSummary, bool allowExternalProcess = true)
        {
            errorSummary = "";
            if (!VerifyBracketsWithProfile(".py", content, out errorSummary))
                return false;

            if (allowExternalProcess)
            {
                string pyExe = GetHostPython312OrNewer();
                if (!string.IsNullOrEmpty(pyExe))
                {
                    string tempPy = Path.Combine(Path.GetTempPath(), "codeless_syntax_" + Guid.NewGuid().ToString("N") + ".py");
                    try
                    {
                        File.WriteAllText(tempPy, content ?? "", new UTF8Encoding(false));
                        int exitCode;
                        string output;
                        if (TryRunSyntaxCheckProcess(pyExe, "-I -m py_compile \"" + tempPy + "\"", 3000, out exitCode, out output))
                        {
                            if (output.IndexOf("SyntaxError", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                output.IndexOf("IndentationError", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                output.IndexOf("TabError", StringComparison.OrdinalIgnoreCase) >= 0)
                            {
                                errorSummary = string.IsNullOrWhiteSpace(output) ? "Python syntax error" : output.Replace("\r", " ").Replace("\n", " ").Trim();
                                return false;
                            }
                        }
                    }
                    catch { }
                    finally
                    {
                        try { if (File.Exists(tempPy)) File.Delete(tempPy); } catch { }
                    }
                }
            }

            return true;
        }

        public static bool VerifyJsTsSyntax(string content, out string errorSummary, bool allowExternalProcess = true)
        {
            if (!VerifyBracketsWithProfile(".tsx", content, out errorSummary))
                return false;

            if (allowExternalProcess && !(content ?? "").Contains("export ") && !(content ?? "").Contains("import "))
            {
                string tempJs = Path.Combine(Path.GetTempPath(), "codeless_syntax_" + Guid.NewGuid().ToString("N") + ".js");
                try
                {
                    File.WriteAllText(tempJs, content ?? "", new UTF8Encoding(false));
                    int exitCode;
                    string output;
                    if (TryRunSyntaxCheckProcess("node", "--check \"" + tempJs + "\"", 3000, out exitCode, out output))
                    {
                        if (exitCode != 0 || output.IndexOf("SyntaxError", StringComparison.OrdinalIgnoreCase) >= 0)
                        {
                            errorSummary = string.IsNullOrWhiteSpace(output) ? "JavaScript SyntaxError" : output.Replace("\r", " ").Replace("\n", " ").Trim();
                            return false;
                        }
                    }
                }
                catch { }
                finally
                {
                    try { if (File.Exists(tempJs)) File.Delete(tempJs); } catch { }
                }
            }

            return true;
        }

        private static bool TryRunSyntaxCheckProcess(string exeName, string args, int timeoutMs, out int exitCode, out string output)
        {
            exitCode = 0;
            output = "";
            try
            {
                ProcessStartInfo psi = new ProcessStartInfo
                {
                    FileName = exeName,
                    Arguments = args,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    WorkingDirectory = Path.GetTempPath()
                };
                using (Process p = Process.Start(psi))
                {
                    if (p == null) return false;
                    Task<string> outTask = p.StandardOutput.ReadToEndAsync();
                    Task<string> errTask = p.StandardError.ReadToEndAsync();
                    if (!p.WaitForExit(timeoutMs))
                    {
                        LanguageServerManager.KillProcessTree(p);
                        return false;
                    }
                    Task.WaitAll(new Task[] { outTask, errTask }, 1000);
                    string stdout = outTask.IsCompleted ? outTask.Result : "";
                    string stderr = errTask.IsCompleted ? errTask.Result : "";
                    exitCode = p.ExitCode;
                    output = (stdout + "\n" + stderr).Trim();
                    return true;
                }
            }
            catch
            {
                return false;
            }
        }

        public static bool VerifyJsonSyntax(string content, out string errorSummary)
        {
            errorSummary = "";
            int line, col;
            string msg;
            if (!TryValidateJsonc(content ?? "", out line, out col, out msg))
            {
                errorSummary = string.Format("Invalid JSON at line {0}, column {1}: {2}", line, col, msg);
                return false;
            }
            return true;
        }

        public static void ValidateJsoncDiagnostics(IList<string> lines, string filePath, string relPath, List<DiagnosticItem> diags)
        {
            if (lines == null || diags == null) return;
            string content = string.Join("\n", lines);
            if (string.IsNullOrWhiteSpace(content)) return;
            int line, col;
            string msg;
            if (!TryValidateJsonc(content, out line, out col, out msg))
            {
                diags.Add(new DiagnosticItem
                {
                    Severity = "Error",
                    FilePath = filePath,
                    RelativePath = relPath,
                    Line = Math.Max(1, line),
                    Column = Math.Max(1, col),
                    Length = 1,
                    Message = msg,
                    Source = "codeLess JSON Validator"
                });
            }
        }

        /// <summary>
        /// Exact recursive-descent JSON / JSONC validator supporting // line comments, /* */ block comments, and trailing commas.
        /// </summary>
        public static bool TryValidateJsonc(string s, out int errLine, out int errCol, out string errMsg)
        {
            errLine = 1;
            errCol = 1;
            errMsg = "";
            if (string.IsNullOrWhiteSpace(s)) return true;

            int i = 0;
            int n = s.Length;
            if (n > 0 && s[0] == '\uFEFF') i = 1;

            if (!SkipJsoncWsAndComments(s, ref i, n, out errLine, out errCol, out errMsg))
                return false;
            if (i >= n) return true;

            if (!ParseJsoncValue(s, ref i, n, 0, out errLine, out errCol, out errMsg))
                return false;

            if (!SkipJsoncWsAndComments(s, ref i, n, out errLine, out errCol, out errMsg))
                return false;
            if (i < n)
            {
                ComputeLineCol(s, i, out errLine, out errCol);
                errMsg = "Unexpected token '" + s[i] + "' after JSON root value.";
                return false;
            }
            return true;
        }

        private static bool SkipJsoncWsAndComments(string s, ref int i, int n, out int errLine, out int errCol, out string errMsg)
        {
            errLine = 1;
            errCol = 1;
            errMsg = "";
            while (i < n)
            {
                char c = s[i];
                if (c == ' ' || c == '\t' || c == '\r' || c == '\n')
                {
                    i++;
                    continue;
                }
                if (c == '/' && i + 1 < n)
                {
                    if (s[i + 1] == '/')
                    {
                        i += 2;
                        while (i < n && s[i] != '\n') i++;
                        continue;
                    }
                    if (s[i + 1] == '*')
                    {
                        int openIdx = i;
                        i += 2;
                        bool closed = false;
                        while (i + 1 < n)
                        {
                            if (s[i] == '*' && s[i + 1] == '/') { i += 2; closed = true; break; }
                            i++;
                        }
                        if (!closed)
                        {
                            ComputeLineCol(s, openIdx, out errLine, out errCol);
                            errMsg = "Unclosed block comment '/*'.";
                            return false;
                        }
                        continue;
                    }
                }
                break;
            }
            return true;
        }

        private static bool ParseJsoncValue(string s, ref int i, int n, int depth, out int errLine, out int errCol, out string errMsg)
        {
            errLine = 1;
            errCol = 1;
            errMsg = "";
            if (depth > 512)
            {
                ComputeLineCol(s, i, out errLine, out errCol);
                errMsg = "Maximum JSON nesting depth exceeded.";
                return false;
            }
            if (!SkipJsoncWsAndComments(s, ref i, n, out errLine, out errCol, out errMsg)) return false;
            if (i >= n)
            {
                ComputeLineCol(s, n, out errLine, out errCol);
                errMsg = "Unexpected end of JSON input.";
                return false;
            }

            char c = s[i];
            if (c == '"') return ParseJsoncString(s, ref i, n, out errLine, out errCol, out errMsg);
            if (c == '{') return ParseJsoncObject(s, ref i, n, depth + 1, out errLine, out errCol, out errMsg);
            if (c == '[') return ParseJsoncArray(s, ref i, n, depth + 1, out errLine, out errCol, out errMsg);
            if (c == '-' || (c >= '0' && c <= '9')) return ParseJsoncNumber(s, ref i, n, out errLine, out errCol, out errMsg);
            if (MatchLiteral(s, ref i, n, "true") || MatchLiteral(s, ref i, n, "false") || MatchLiteral(s, ref i, n, "null"))
                return true;

            ComputeLineCol(s, i, out errLine, out errCol);
            errMsg = "Unexpected token '" + c + "' in JSON.";
            return false;
        }

        private static bool MatchLiteral(string s, ref int i, int n, string lit)
        {
            if (i + lit.Length > n) return false;
            if (string.CompareOrdinal(s, i, lit, 0, lit.Length) != 0) return false;
            int after = i + lit.Length;
            if (after < n && (char.IsLetterOrDigit(s[after]) || s[after] == '_')) return false;
            i = after;
            return true;
        }

        private static bool ParseJsoncString(string s, ref int i, int n, out int errLine, out int errCol, out string errMsg)
        {
            errLine = 1;
            errCol = 1;
            errMsg = "";
            int openIdx = i;
            i++; // skip opening '"'
            while (i < n)
            {
                char c = s[i++];
                if (c == '"') return true;
                if (c == '\n' || c == '\r')
                {
                    ComputeLineCol(s, openIdx, out errLine, out errCol);
                    errMsg = "Unclosed string literal in JSON.";
                    return false;
                }
                if (c == '\\')
                {
                    if (i >= n || s[i] == '\n' || s[i] == '\r')
                    {
                        ComputeLineCol(s, openIdx, out errLine, out errCol);
                        errMsg = "Unclosed string escape in JSON.";
                        return false;
                    }
                    i++;
                }
            }
            ComputeLineCol(s, openIdx, out errLine, out errCol);
            errMsg = "Unclosed string literal in JSON.";
            return false;
        }

        private static bool ParseJsoncNumber(string s, ref int i, int n, out int errLine, out int errCol, out string errMsg)
        {
            errLine = 1;
            errCol = 1;
            errMsg = "";
            int start = i;
            if (s[i] == '-') i++;
            if (i >= n || s[i] < '0' || s[i] > '9')
            {
                ComputeLineCol(s, start, out errLine, out errCol);
                errMsg = "Invalid number literal in JSON.";
                return false;
            }
            while (i < n && s[i] >= '0' && s[i] <= '9') i++;
            if (i < n && s[i] == '.')
            {
                i++;
                if (i >= n || s[i] < '0' || s[i] > '9')
                {
                    ComputeLineCol(s, start, out errLine, out errCol);
                    errMsg = "Invalid fractional number in JSON.";
                    return false;
                }
                while (i < n && s[i] >= '0' && s[i] <= '9') i++;
            }
            if (i < n && (s[i] == 'e' || s[i] == 'E'))
            {
                i++;
                if (i < n && (s[i] == '+' || s[i] == '-')) i++;
                if (i >= n || s[i] < '0' || s[i] > '9')
                {
                    ComputeLineCol(s, start, out errLine, out errCol);
                    errMsg = "Invalid exponent in JSON number.";
                    return false;
                }
                while (i < n && s[i] >= '0' && s[i] <= '9') i++;
            }
            return true;
        }

        private static bool ParseJsoncObject(string s, ref int i, int n, int depth, out int errLine, out int errCol, out string errMsg)
        {
            int openIdx = i;
            i++; // skip '{'
            while (true)
            {
                if (!SkipJsoncWsAndComments(s, ref i, n, out errLine, out errCol, out errMsg)) return false;
                if (i >= n)
                {
                    ComputeLineCol(s, openIdx, out errLine, out errCol);
                    errMsg = "Unclosed JSON object '{'.";
                    return false;
                }
                if (s[i] == '}') { i++; return true; }
                if (s[i] != '"')
                {
                    ComputeLineCol(s, i, out errLine, out errCol);
                    errMsg = "Expected double-quoted property name in JSON object.";
                    return false;
                }
                if (!ParseJsoncString(s, ref i, n, out errLine, out errCol, out errMsg)) return false;
                if (!SkipJsoncWsAndComments(s, ref i, n, out errLine, out errCol, out errMsg)) return false;
                if (i >= n || s[i] != ':')
                {
                    ComputeLineCol(s, i < n ? i : openIdx, out errLine, out errCol);
                    errMsg = "Expected ':' after property name in JSON object.";
                    return false;
                }
                i++; // skip ':'
                if (!ParseJsoncValue(s, ref i, n, depth, out errLine, out errCol, out errMsg)) return false;
                if (!SkipJsoncWsAndComments(s, ref i, n, out errLine, out errCol, out errMsg)) return false;
                if (i >= n)
                {
                    ComputeLineCol(s, openIdx, out errLine, out errCol);
                    errMsg = "Unclosed JSON object '{'.";
                    return false;
                }
                if (s[i] == ',')
                {
                    i++;
                    continue; // allows trailing comma before '}'
                }
                if (s[i] == '}') { i++; return true; }
                ComputeLineCol(s, i, out errLine, out errCol);
                errMsg = "Expected ',' or '}' in JSON object.";
                return false;
            }
        }

        private static bool ParseJsoncArray(string s, ref int i, int n, int depth, out int errLine, out int errCol, out string errMsg)
        {
            int openIdx = i;
            i++; // skip '['
            while (true)
            {
                if (!SkipJsoncWsAndComments(s, ref i, n, out errLine, out errCol, out errMsg)) return false;
                if (i >= n)
                {
                    ComputeLineCol(s, openIdx, out errLine, out errCol);
                    errMsg = "Unclosed JSON array '['.";
                    return false;
                }
                if (s[i] == ']') { i++; return true; }
                if (!ParseJsoncValue(s, ref i, n, depth, out errLine, out errCol, out errMsg)) return false;
                if (!SkipJsoncWsAndComments(s, ref i, n, out errLine, out errCol, out errMsg)) return false;
                if (i >= n)
                {
                    ComputeLineCol(s, openIdx, out errLine, out errCol);
                    errMsg = "Unclosed JSON array '['.";
                    return false;
                }
                if (s[i] == ',')
                {
                    i++;
                    continue; // allows trailing comma before ']'
                }
                if (s[i] == ']') { i++; return true; }
                ComputeLineCol(s, i, out errLine, out errCol);
                errMsg = "Expected ',' or ']' in JSON array.";
                return false;
            }
        }

        private static void ComputeLineCol(string s, int index, out int line, out int col)
        {
            line = 1;
            col = 1;
            int lim = Math.Min(index, s.Length);
            for (int k = 0; k < lim; k++)
            {
                if (s[k] == '\n') { line++; col = 1; }
                else if (s[k] != '\r') col++;
            }
        }

        private static bool VerifyBraceAndOperatorBalance(string ext, string content, out string errorSummary)
        {
            errorSummary = "";
            string codeOnly = MaskNonCodeCharacters(ext, content ?? "");
            if (Regex.IsMatch(codeOnly, @"[\+\-\*/]\s*[\*/]\s*;"))
            {
                errorSummary = "Invalid consecutive binary operators before semicolon.";
                return false;
            }
            return VerifyBracketsWithProfile(ext, content, out errorSummary);
        }

        public static bool VerifyBracketsWithProfile(string ext, string content, out string errorSummary)
        {
            errorSummary = "";
            string normalized = (content ?? "").Replace("\r\n", "\n").Replace("\r", "\n");
            string[] lines = normalized.Split('\n');
            BracketLexProfile profile = BracketLexProfile.ForExtension(ext, "file" + ext);
            EditorBracketCache cache = new EditorBracketCache(profile);
            cache.Sync(lines);
            cache.EnsureValid(lines, lines.Length - 1, 0);

            for (int li = 0; li < lines.Length; li++)
            {
                BracketLineData bd = cache.GetLine(lines, li, 0);
                if (bd == null) continue;
                for (int bi = 0; bi < bd.Count; bi++)
                {
                    if (bd.Levels[bi] < 0)
                    {
                        errorSummary = string.Format("Line {0}: Unbalanced closing delimiter '{1}'.", li + 1, bd.Chars[bi]);
                        return false;
                    }
                }
            }

            if (lines.Length > 0)
            {
                BracketLexState endState = cache.GetEndState(lines.Length - 1);
                if (endState != null)
                {
                    if (endState.Mode == BracketLexState.ModeTriple)
                    {
                        errorSummary = "Unclosed multi-line triple-quoted string at end of file.";
                        return false;
                    }
                    if (endState.Mode == BracketLexState.ModeBlockComment)
                    {
                        errorSummary = "Unclosed block comment at end of file.";
                        return false;
                    }
                    if (endState.Mode == BracketLexState.ModeString && endState.Quote == '`')
                    {
                        errorSummary = "Unclosed template literal at end of file.";
                        return false;
                    }
                    if (endState.Stack != null)
                    {
                        BracketFrame f = endState.Stack;
                        char open = f.Open == '$' ? '{' : f.Open;
                        errorSummary = string.Format("Line {0}: Unclosed delimiter '{1}' at end of file.", f.Line + 1, open);
                        return false;
                    }
                }
            }
            return true;
        }

        private static string MaskNonCodeCharacters(string ext, string content)
        {
            string normalized = (content ?? "").Replace("\r\n", "\n").Replace("\r", "\n");
            string[] lines = normalized.Split('\n');
            BracketLexProfile profile = BracketLexProfile.ForExtension(ext, "file" + ext);
            EditorBracketCache cache = new EditorBracketCache(profile);
            cache.Sync(lines);
            StringBuilder sb = new StringBuilder(normalized.Length);
            for (int li = 0; li < lines.Length; li++)
            {
                string ln = lines[li];
                for (int ci = 0; ci < ln.Length; ci++)
                {
                    int ctx = cache.ContextAt(lines, li, ci);
                    sb.Append(ctx == EditorBracketCache.ContextCode ? ln[ci] : ' ');
                }
                if (li + 1 < lines.Length) sb.Append('\n');
            }
            return sb.ToString();
        }
    }
}
