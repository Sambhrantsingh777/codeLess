using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace codeLess.Studio
{
    public sealed class ChatAtMentionItem
    {
        public string DisplayText { get; set; }
        public string InsertToken { get; set; }
        public string RelativePath { get; set; }
        public string FullPath { get; set; }
        public bool IsFile { get; set; }
        public int Score { get; set; }

        public ChatAtMentionItem()
        {
            DisplayText = "";
            InsertToken = "";
            RelativePath = "";
            FullPath = "";
            IsFile = true;
            Score = 0;
        }
    }

    /// <summary>
    /// Fuzzy `@file` completion for chat input reusing <see cref="QuickOpenFileIndex"/> (Quick Open matcher),
    /// plus prompt attachment with `read_file` size and line caps. Strict C# 5.
    /// </summary>
    public static class ChatAtMentionCompleter
    {
        public const int MaxReadLines = 2000;
        public const int MaxLineChars = 2000;
        public const int MaxReadBytes = 50 * 1024; // 50 KB cap matching read_file

        private static readonly string[] BuiltInProviders = new string[]
        {
            "@ActiveFile",
            "@Problems",
            "@Git",
            "@Terminal",
            "@Codebase:BM25",
            "@.codelessrules"
        };

        /// <summary>
        /// Checks whether there is an active `@query` token immediately preceding <paramref name="caretIndex"/>.
        /// `@` must be at the start of the text or preceded by whitespace, and the query may not contain whitespace.
        /// </summary>
        public static bool TryExtractActiveAtQuery(string text, int caretIndex, out int atIndex, out string query)
        {
            atIndex = -1;
            query = "";
            if (string.IsNullOrEmpty(text)) return false;

            int caret = caretIndex;
            if (caret < 0 || caret > text.Length)
                caret = text.Length;

            int scan = caret - 1;
            while (scan >= 0)
            {
                char c = text[scan];
                if (char.IsWhiteSpace(c))
                    return false;
                if (c == '@')
                {
                    if (scan == 0 || char.IsWhiteSpace(text[scan - 1]))
                    {
                        atIndex = scan;
                        query = text.Substring(scan + 1, caret - (scan + 1));
                        return true;
                    }
                    return false;
                }
                scan--;
            }

            return false;
        }

        /// <summary>
        /// Computes fuzzy file completion candidates reusing <see cref="QuickOpenFileIndex"/>.
        /// File matches are ranked first by Quick Open fuzzy score, followed by matching built-in context providers.
        /// </summary>
        public static List<ChatAtMentionItem> GetSuggestions(
            string query,
            string workspaceRoot,
            IList<string> workspaceFiles,
            int maxResults = 16)
        {
            List<ChatAtMentionItem> results = new List<ChatAtMentionItem>();
            if (maxResults <= 0) return results;

            string cleanQuery = (query ?? "").Trim();
            if (cleanQuery.StartsWith("@"))
                cleanQuery = cleanQuery.Substring(1);

            if (workspaceFiles != null && workspaceFiles.Count > 0)
            {
                QuickOpenFileIndex index = new QuickOpenFileIndex(workspaceRoot, workspaceFiles);
                List<QuickOpenFileIndex.Hit> hits = index.Search(cleanQuery, maxResults, null);
                for (int i = 0; i < hits.Count && results.Count < maxResults; i++)
                {
                    int idx = hits[i].Index;
                    string rel = index.GetRelativePath(idx);
                    string full = index.GetFullPath(idx);
                    ChatAtMentionItem item = new ChatAtMentionItem();
                    item.RelativePath = rel;
                    item.FullPath = full;
                    item.InsertToken = "@" + rel;
                    item.DisplayText = "@" + rel;
                    item.IsFile = true;
                    item.Score = hits[i].Score;
                    results.Add(item);
                }
            }

            string qLow = cleanQuery.ToLowerInvariant();
            for (int i = 0; i < BuiltInProviders.Length && results.Count < maxResults; i++)
            {
                string bi = BuiltInProviders[i];
                string biNoAt = bi.Substring(1);
                int score = QuickOpenFileIndex.ScoreLowered(biNoAt, biNoAt.ToLowerInvariant(), qLow);
                if (score > 0)
                {
                    ChatAtMentionItem item = new ChatAtMentionItem();
                    item.RelativePath = biNoAt;
                    item.FullPath = "";
                    item.InsertToken = bi;
                    item.DisplayText = bi + "  (Context)";
                    item.IsFile = false;
                    item.Score = score;
                    results.Add(item);
                }
            }

            return results;
        }

        /// <summary>
        /// Replaces the active `@query` token at <paramref name="caretIndex"/> with <paramref name="insertToken"/> + space.
        /// </summary>
        public static string ApplySelection(string currentText, int caretIndex, string insertToken, out int newCaretIndex)
        {
            string text = currentText ?? "";
            int caret = (caretIndex < 0 || caretIndex > text.Length) ? text.Length : caretIndex;
            string token = (insertToken ?? "").Trim();
            if (!token.StartsWith("@"))
                token = "@" + token;

            int atIndex;
            string query;
            if (TryExtractActiveAtQuery(text, caret, out atIndex, out query))
            {
                string before = text.Substring(0, atIndex);
                string after = caret < text.Length ? text.Substring(caret) : "";
                string sep = (after.Length > 0 && after[0] == ' ') ? "" : " ";
                string updated = before + token + sep + after;
                newCaretIndex = before.Length + token.Length + sep.Length;
                return updated;
            }

            string fallbackSep = (text.Length > 0 && !char.IsWhiteSpace(text[text.Length - 1])) ? " " : "";
            string appended = text + fallbackSep + token + " ";
            newCaretIndex = appended.Length;
            return appended;
        }

        /// <summary>
        /// Extracts `@file` tokens from a chat prompt so they are attached as context files even if typed directly.
        /// </summary>
        public static List<string> ExtractMentionedFilePills(string promptText)
        {
            List<string> pills = new List<string>();
            if (string.IsNullOrWhiteSpace(promptText)) return pills;

            int i = 0;
            while (i < promptText.Length)
            {
                if (promptText[i] == '@' && (i == 0 || char.IsWhiteSpace(promptText[i - 1])))
                {
                    int start = i + 1;
                    int end = start;
                    while (end < promptText.Length && !char.IsWhiteSpace(promptText[end]))
                        end++;
                    if (end > start)
                    {
                        string token = promptText.Substring(start, end - start).TrimEnd('.', ',', ';', ')', ']', '"', '\'');
                        if (!string.IsNullOrEmpty(token) && !pills.Contains(token))
                            pills.Add(token);
                    }
                    i = end;
                }
                else
                {
                    i++;
                }
            }
            return pills;
        }

        /// <summary>
        /// Formats file content for prompt attachment using `read_file` size and line caps
        /// (max 2000 lines, 2000 chars per line, 50 KB total bytes, and binary guard).
        /// </summary>
        public static string FormatFileAttachmentWithReadCaps(
            string rawContent,
            string fullPath,
            int rangeStartLine = -1,
            int rangeEndLine = -1,
            int maxLines = MaxReadLines,
            int maxLineChars = MaxLineChars,
            int maxBytes = MaxReadBytes)
        {
            if (!string.IsNullOrEmpty(fullPath) && File.Exists(fullPath) && BinaryDetector.IsBinaryFile(fullPath))
                return "[Cannot read binary file]";

            string norm = (rawContent ?? "").Replace("\r\n", "\n").Replace("\r", "\n");
            string[] allLines = norm.Split('\n');

            int startIdx = 0;
            int endIdx = allLines.Length - 1;
            if (rangeStartLine > 0 && rangeEndLine >= rangeStartLine)
            {
                startIdx = Math.Max(0, Math.Min(allLines.Length - 1, rangeStartLine - 1));
                endIdx = Math.Max(startIdx, Math.Min(allLines.Length - 1, rangeEndLine - 1));
            }

            int maxEndIdx = Math.Min(endIdx, startIdx + maxLines - 1);
            StringBuilder sb = new StringBuilder();
            int bytesUsed = 0;
            int linesIncluded = 0;
            bool truncated = maxEndIdx < endIdx;

            for (int i = startIdx; i <= maxEndIdx && i < allLines.Length; i++)
            {
                string line = allLines[i] ?? "";
                if (line.Length > maxLineChars)
                {
                    line = line.Substring(0, maxLineChars) + "...";
                    truncated = true;
                }
                int lineBytes = Encoding.UTF8.GetByteCount(line) + 1;
                if (bytesUsed + lineBytes > maxBytes)
                {
                    truncated = true;
                    break;
                }
                if (linesIncluded > 0) sb.Append('\n');
                sb.Append(line);
                bytesUsed += lineBytes;
                linesIncluded++;
            }

            if (truncated)
            {
                sb.Append("\n// ... [truncated at read_file cap: ");
                sb.Append(linesIncluded);
                sb.Append(" of ");
                sb.Append(allLines.Length);
                sb.Append(" lines]");
            }

            return sb.ToString();
        }
    }
}
