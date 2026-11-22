using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
#if !NETFRAMEWORK
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
#else
using System.CodeDom.Compiler;
using Microsoft.CSharp;
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
            if (ext == ".cs")
                return VerifyCSharpSyntax(content, out errorSummary);
            if (ext == ".py")
                return VerifyPythonSyntax(content, out errorSummary, allowExternalProcess);
            if (ext == ".js" || ext == ".mjs" || ext == ".cjs" || ext == ".ts" || ext == ".jsx" || ext == ".tsx")
                return VerifyJsTsSyntax(content, out errorSummary, allowExternalProcess && (ext == ".js" || ext == ".mjs" || ext == ".cjs"));
            if (ext == ".json")
                return VerifyJsonSyntax(content, out errorSummary);

            return true;
        }

        public static bool VerifyCSharpSyntax(string content, out string errorSummary)
        {
            errorSummary = "";
#if !NETFRAMEWORK
            try
            {
                SyntaxTree tree = CSharpSyntaxTree.ParseText(content ?? "");
                var errors = tree.GetDiagnostics()
                    .Where(d => d.Severity == DiagnosticSeverity.Error)
                    .Take(5)
                    .ToList();
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
#else
            if (!VerifyBraceAndOperatorBalance(content, out errorSummary))
                return false;
            try
            {
                using (var provider = new CSharpCodeProvider())
                {
                    var cp = new CompilerParameters
                    {
                        GenerateExecutable = false,
                        GenerateInMemory = true,
                        TreatWarningsAsErrors = false
                    };
                    CompilerResults cr = provider.CompileAssemblyFromSource(cp, content ?? "");
                    var syntaxErrors = new List<CompilerError>();
                    foreach (CompilerError ce in cr.Errors)
                    {
                        if (ce.IsWarning) continue;
                        // CS1001..CS1999 are lexical/parser syntax errors (whereas CS0103/CS0246 are semantic/reference errors)
                        int codeNum = 0;
                        if (!string.IsNullOrEmpty(ce.ErrorNumber) && ce.ErrorNumber.StartsWith("CS", StringComparison.OrdinalIgnoreCase))
                            int.TryParse(ce.ErrorNumber.Substring(2), out codeNum);
                        if (codeNum >= 1000 && codeNum < 2000)
                            syntaxErrors.Add(ce);
                    }
                    if (syntaxErrors.Count > 0)
                    {
                        StringBuilder sb = new StringBuilder();
                        foreach (var err in syntaxErrors.Take(5))
                            sb.AppendFormat("[{0}] Line {1}: {2}; ", err.ErrorNumber, err.Line, err.ErrorText);
                        errorSummary = sb.ToString().Trim();
                        return false;
                    }
                }
            }
            catch { }
            return true;
#endif
        }

        public static bool VerifyPythonSyntax(string content, out string errorSummary, bool allowExternalProcess = true)
        {
            errorSummary = "";
            if (!VerifyBracketAndQuoteBalance(content, out errorSummary, true))
                return false;

            string[] lines = (content ?? "").Replace("\r\n", "\n").Replace("\r", "\n").Split('\n');
            int classDefIndent = -1;
            int firstMethodIndent = -1;
            for (int i = 0; i < lines.Length; i++)
            {
                string raw = lines[i];
                string trimmed = raw.Trim();
                if (string.IsNullOrEmpty(trimmed) || trimmed.StartsWith("#")) continue;

                if (Regex.IsMatch(trimmed, @"[\+\-]\s+[\*/]") || Regex.IsMatch(trimmed, @"=\s*$"))
                {
                    errorSummary = string.Format("Line {0}: Invalid operator sequence in Python statement.", i + 1);
                    return false;
                }

                int indent = CountLeadingSpaces(raw);
                if (trimmed.StartsWith("class ") && trimmed.EndsWith(":"))
                {
                    classDefIndent = indent;
                    firstMethodIndent = -1;
                }
                else if (classDefIndent >= 0 && indent > classDefIndent && (trimmed.StartsWith("def ") || trimmed.StartsWith("async def ")))
                {
                    if (firstMethodIndent < 0)
                    {
                        firstMethodIndent = indent;
                    }
                    else if (indent != firstMethodIndent)
                    {
                        errorSummary = string.Format("Line {0}: Inconsistent Python method indentation ({1} spaces vs expected {2} spaces).", i + 1, indent, firstMethodIndent);
                        return false;
                    }
                }
            }

            if (allowExternalProcess)
            {
                string tempPy = Path.Combine(Path.GetTempPath(), "codeless_syntax_" + Guid.NewGuid().ToString("N") + ".py");
                try
                {
                    File.WriteAllText(tempPy, content ?? "", new UTF8Encoding(false));
                    int exitCode;
                    string output;
                    if (TryRunSyntaxCheckProcess("python", "-I -m py_compile \"" + tempPy + "\"", 3000, out exitCode, out output))
                    {
                        // Only real compiler diagnostics count: a non-zero exit without them (e.g. the Windows Store
                        // "python" alias stub printing "Python was not found", exit 9009) must not reject valid code.
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

            return true;
        }

        public static bool VerifyJsTsSyntax(string content, out string errorSummary, bool allowExternalProcess = true)
        {
            if (!VerifyBraceAndOperatorBalance(content, out errorSummary))
                return false;

            if (Regex.IsMatch(content ?? "", @"=\s*;") || Regex.IsMatch(content ?? "", @"[\+\-]\s+[\*/]"))
            {
                errorSummary = "Invalid expression or operator syntax in JS/TS.";
                return false;
            }

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
                    // Never the workspace: "python -m" imports from the working directory, so a planted py_compile.py would
                    // run. -I (isolated) also drops the cwd from sys.path and ignores PYTHON* variables.
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
            try
            {
                JavaScriptSerializer ser = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };
                ser.DeserializeObject(content);
                return true;
            }
            catch (Exception ex)
            {
                errorSummary = "Invalid JSON: " + ex.Message;
                return false;
            }
        }

        private static int CountLeadingSpaces(string line)
        {
            int count = 0;
            for (int i = 0; i < line.Length; i++)
            {
                if (line[i] == ' ') count++;
                else if (line[i] == '\t') count += 4;
                else break;
            }
            return count;
        }

        private static bool VerifyBraceAndOperatorBalance(string content, out string errorSummary)
        {
            errorSummary = "";
            if (Regex.IsMatch(content ?? "", @"[\+\-\*/]\s*[\*/]\s*;"))
            {
                errorSummary = "Invalid consecutive binary operators before semicolon.";
                return false;
            }
            return VerifyBracketAndQuoteBalance(content, out errorSummary, false);
        }

        private static bool VerifyBracketAndQuoteBalance(string content, out string errorSummary, bool pythonSyntax)
        {
            errorSummary = "";
            Stack<char> stack = new Stack<char>();
            bool inSingle = false;
            bool inDouble = false;
            bool inLineComment = false;
            bool inBlockComment = false;

            string s = content ?? "";
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                char next = (i + 1 < s.Length) ? s[i + 1] : '\0';

                if (inLineComment)
                {
                    if (c == '\n') inLineComment = false;
                    continue;
                }
                if (inBlockComment)
                {
                    if (c == '*' && next == '/') { inBlockComment = false; i++; }
                    continue;
                }
                if (inSingle)
                {
                    if (c == '\\') { i++; continue; }
                    if (c == '\'') inSingle = false;
                    continue;
                }
                if (inDouble)
                {
                    if (c == '\\') { i++; continue; }
                    if (c == '"') inDouble = false;
                    continue;
                }

                if (c == '\\') { i++; continue; }
                if (pythonSyntax)
                {
                    // Python: '#' starts a line comment (apostrophes in comments must not open strings),
                    // while '//' is floor division and '/*' is not a comment.
                    if (c == '#') { inLineComment = true; continue; }
                }
                else
                {
                    if (c == '/' && next == '/') { inLineComment = true; i++; continue; }
                    if (c == '/' && next == '*') { inBlockComment = true; i++; continue; }
                }
                if (c == '\'') { inSingle = true; continue; }
                if (c == '"') { inDouble = true; continue; }

                if (c == '{' || c == '(' || c == '[')
                {
                    stack.Push(c);
                }
                else if (c == '}' || c == ')' || c == ']')
                {
                    if (stack.Count == 0)
                    {
                        errorSummary = "Unbalanced closing delimiter '" + c + "'.";
                        return false;
                    }
                    char open = stack.Pop();
                    if ((c == '}' && open != '{') || (c == ')' && open != '(') || (c == ']' && open != '['))
                    {
                        errorSummary = "Mismatched delimiter '" + open + "' and '" + c + "'.";
                        return false;
                    }
                }
            }

            if (stack.Count > 0)
            {
                errorSummary = "Unclosed delimiter '" + stack.Peek() + "' at end of file.";
                return false;
            }
            return true;
        }
    }
}
