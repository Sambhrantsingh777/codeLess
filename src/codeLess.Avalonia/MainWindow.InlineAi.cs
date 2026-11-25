using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Avalonia.Threading;
using codeLess.Studio;

namespace codeLess.AvaloniaApp
{
    /// <summary>
    /// Inline AI: editor Cmd/Ctrl+K / Cmd/Ctrl+I edit bar and the terminal "describe a command" bar, both backed by
    /// the configured BYOK provider. (Previously the editor inserted a placeholder comment and the terminal mapped
    /// keywords to canned commands.) Edits are staged as an inline diff for Accept / Reject; terminal commands are
    /// typed into the prompt but never executed without the user pressing Enter.
    /// </summary>
    public partial class MainWindow
    {
        private ByokVendor ResolvePreferredVendor()
        {
            var eng = EnsureAiEngine();
            return eng.Vendors.FirstOrDefault(v =>
                       string.Equals(v.Name, settings.ActiveProvider, StringComparison.OrdinalIgnoreCase) ||
                       string.Equals(v.Id, StudioSettings.NormalizeProviderId(settings.ActiveProvider), StringComparison.OrdinalIgnoreCase) ||
                       string.Equals(v.Id, settings.ActiveProvider, StringComparison.OrdinalIgnoreCase))
                   ?? eng.Vendors.FirstOrDefault();
        }

        /// <summary>Returns the first fenced code block's body, or the trimmed reply when there is none.</summary>
        public static string ExtractFirstCodeBlock(string reply)
        {
            if (string.IsNullOrEmpty(reply)) return "";
            var m = Regex.Match(reply, "```[^\\n`]*\\n([\\s\\S]*?)\\n?```");
            return m.Success ? m.Groups[1].Value : reply.Trim();
        }

        private void ReportAiProblem(string title, Exception ex)
        {
            bool noKey = ex != null && (ex.Message ?? "").Contains("NO_API_KEY");
            string msg = noKey
                ? "No AI provider is configured. Add an API key (or start a local Ollama) in Settings > AI Providers."
                : "The AI request failed: " + (ex?.Message ?? "unknown error");
            SetStatusMessage(msg);
            if (AvaloniaTerminalEmulatorControl.IsDesktopRuntime) ShowMessageModal(title, msg);
        }

        private void ExecuteInlineAiEdit(string instruction, EditorTabBuffer buf) => _ = ExecuteInlineAiEditAsync(instruction, buf);

        public async Task<bool> ExecuteInlineAiEditAsync(string instruction, EditorTabBuffer buf)
        {
            if (buf == null || string.IsNullOrWhiteSpace(instruction)) return false;
            var vendor = ResolvePreferredVendor();

            // Snapshot what the edit applies to.
            bool hasSel = buf.HasSelection();
            int sLine = buf.CursorLine, sCol = buf.CursorCol, eLine = buf.CursorLine, eCol = buf.CursorCol;
            if (hasSel)
            {
                sLine = buf.SelectionStartLine; sCol = buf.SelectionStartCol;
                if (sLine > eLine || (sLine == eLine && sCol > eCol))
                {
                    int tl = sLine; sLine = eLine; eLine = tl;
                    int tc = sCol; sCol = eCol; eCol = tc;
                }
            }
            var lines = new List<string>(buf.Lines);
            string contentAtRequest = string.Join("\n", lines);
            string selected = hasSel ? ExtractRange(lines, sLine, sCol, eLine, eCol) : "";

            // Context: the whole file when small, otherwise a window around the edit point, with explicit markers.
            int from = 0, to = lines.Count - 1;
            if (contentAtRequest.Length > 24000) { from = Math.Max(0, sLine - 80); to = Math.Min(lines.Count - 1, eLine + 80); }
            var ctx = new StringBuilder();
            for (int i = from; i <= to; i++)
            {
                string l = lines[i];
                if (hasSel)
                {
                    if (i == sLine && i == eLine) l = l.Substring(0, Math.Min(sCol, l.Length)) + "<<<SELECTION_START>>>" + l.Substring(Math.Min(sCol, l.Length), Math.Max(0, Math.Min(eCol, l.Length) - Math.Min(sCol, l.Length))) + "<<<SELECTION_END>>>" + l.Substring(Math.Min(eCol, l.Length));
                    else if (i == sLine) l = l.Substring(0, Math.Min(sCol, l.Length)) + "<<<SELECTION_START>>>" + l.Substring(Math.Min(sCol, l.Length));
                    else if (i == eLine) l = l.Substring(0, Math.Min(eCol, l.Length)) + "<<<SELECTION_END>>>" + l.Substring(Math.Min(eCol, l.Length));
                }
                else if (i == sLine)
                {
                    l = l.Substring(0, Math.Min(sCol, l.Length)) + "<<<CURSOR>>>" + l.Substring(Math.Min(sCol, l.Length));
                }
                ctx.Append(l).Append('\n');
            }

            string language = string.IsNullOrEmpty(buf.Language) ? Path.GetExtension(buf.FileName ?? "").TrimStart('.') : buf.Language;
            string system =
                "You are a precise code-editing engine inside an IDE. Apply the user's instruction to the marked region of the file. " +
                (hasSel
                    ? "Return ONLY the full replacement for the text between <<<SELECTION_START>>> and <<<SELECTION_END>>>, "
                    : "Return ONLY the code to insert at <<<CURSOR>>>, ") +
                "inside a single fenced code block. Keep the file's existing indentation style and conventions. " +
                "Do not include the markers, explanations, or unchanged surrounding code.";
            string user =
                $"File: {buf.FileName}\nLanguage: {language}\nInstruction: {instruction.Trim()}\n\n" +
                $"File content (lines {from + 1}-{to + 1}):\n```{language}\n{ctx}```";

            SetStatusMessage($"✦ Generating edit with {vendor?.Name ?? "AI"}...");
            string reply;
            try
            {
                reply = await Task.Run(() => EnsureAiEngine().CallByokVendorAsync(vendor, system, user, 0.1));
            }
            catch (Exception ex)
            {
                ReportAiProblem("Inline AI Edit", ex);
                return false;
            }
            if (string.IsNullOrWhiteSpace(reply) || reply.StartsWith("Error:", StringComparison.Ordinal))
            {
                ReportAiProblem("Inline AI Edit", new Exception(string.IsNullOrWhiteSpace(reply) ? "empty response" : reply));
                return false;
            }
            if (!string.Equals(string.Join("\n", buf.Lines), contentAtRequest, StringComparison.Ordinal))
            {
                SetStatusMessage("The file changed while the AI edit was generated; the edit was discarded. Try again.");
                return false;
            }

            string code = ExtractFirstCodeBlock(reply).Replace("\r\n", "\n");
            string before = ExtractRange(lines, 0, 0, sLine, sCol);
            string after = ExtractRange(lines, eLine, eCol, lines.Count - 1, lines[lines.Count - 1].Length);
            if (!hasSel && code.Contains('\n') && !code.EndsWith("\n") && after.Length > 0 && !after.StartsWith("\n"))
                code += "\n"; // multi-line insertion before existing text: keep the remainder on its own line
            string proposed = before + code + after;
            PrimaryEditor.StageProposedDiffContent(proposed);
            RefreshChangesetBanner();
            SetStatusMessage("✦ AI edit ready - review the inline diff and Accept or Reject.");
            return true;
        }

        private static string ExtractRange(List<string> lines, int sLine, int sCol, int eLine, int eCol)
        {
            if (lines.Count == 0) return "";
            sLine = Math.Max(0, Math.Min(lines.Count - 1, sLine));
            eLine = Math.Max(0, Math.Min(lines.Count - 1, eLine));
            sCol = Math.Max(0, Math.Min(lines[sLine].Length, sCol));
            eCol = Math.Max(0, Math.Min(lines[eLine].Length, eCol));
            if (sLine == eLine) return lines[sLine].Substring(sCol, Math.Max(0, eCol - sCol));
            var sb = new StringBuilder();
            sb.Append(lines[sLine].Substring(sCol)).Append('\n');
            for (int i = sLine + 1; i < eLine; i++) sb.Append(lines[i]).Append('\n');
            sb.Append(lines[eLine].Substring(0, eCol));
            return sb.ToString();
        }

        private void HandleTerminalInlineAiRequest(string prompt, Action<string> callback) => _ = HandleTerminalInlineAiRequestAsync(prompt, callback);

        public async Task HandleTerminalInlineAiRequestAsync(string prompt, Action<string> callback)
        {
            if (string.IsNullOrWhiteSpace(prompt)) { callback?.Invoke(""); return; }
            var vendor = ResolvePreferredVendor();
            string os = RuntimeInformation.IsOSPlatform(OSPlatform.OSX) ? "macOS" : RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "Windows" : "Linux";
            string shell = activeTerminalSession?.Title ?? "sh";
            string cwd = activeTerminalSession?.Control?.WorkingDirectory ?? workspaceRoot ?? "";
            string system =
                $"Translate the user's request into ONE shell command for {os} using the '{shell}' shell. " +
                "Reply with only the command on a single line: no explanation, no markdown, no backticks. " +
                "Prefer safe, non-destructive commands; never add sudo unless explicitly asked.";
            string user = $"Current directory: {cwd}\nRequest: {prompt.Trim()}";
            try
            {
                string reply = await Task.Run(() => EnsureAiEngine().CallByokVendorAsync(vendor, system, user, 0.0));
                if (string.IsNullOrWhiteSpace(reply) || reply.StartsWith("Error:", StringComparison.Ordinal))
                    throw new Exception(string.IsNullOrWhiteSpace(reply) ? "empty response" : reply);
                string cmd = ExtractFirstCodeBlock(reply);
                cmd = cmd.Split('\n').Select(x => x.Trim()).FirstOrDefault(x => x.Length > 0) ?? "";
                cmd = cmd.Trim('`').Trim();
                if (cmd.StartsWith("$ ")) cmd = cmd.Substring(2);
                callback?.Invoke(cmd); // typed into the prompt only; the user presses Enter to run it
            }
            catch (Exception ex)
            {
                MainUi.Post(() => ReportAiProblem("Terminal AI", ex));
                callback?.Invoke("");
            }
        }
    }
}
