using System;
using System.Collections.Generic;

namespace codeLess.Studio
{
    /// <summary>
    /// VS Code word / subword navigation, word selection, word deletion, character transpose, and line sorting.
    /// Shared by Avalonia and WinForms. Strict C# 5.
    /// </summary>
    public static class EditorWordNavigation
    {
        public const string DefaultWordSeparators = "`~!@#$%^&*()-=+[{]}\\|;:'\",.<>/?";
        public static string WordSeparators = DefaultWordSeparators;

        public enum CharKind
        {
            Whitespace = 0,
            Separator = 1,
            Regular = 2
        }

        public static CharKind Classify(char c, string separators)
        {
            if (char.IsWhiteSpace(c)) return CharKind.Whitespace;
            string seps = separators ?? WordSeparators ?? DefaultWordSeparators;
            if (seps.IndexOf(c) >= 0) return CharKind.Separator;
            return CharKind.Regular;
        }

        public static int StepLeft(string line, int col)
        {
            if (string.IsNullOrEmpty(line) || col <= 0) return 0;
            if (col > line.Length) col = line.Length;
            if (col < line.Length && char.IsLowSurrogate(line[col]) && char.IsHighSurrogate(line[col - 1]))
                return col - 1;
            int step = (col >= 2 && char.IsLowSurrogate(line[col - 1]) && char.IsHighSurrogate(line[col - 2])) ? 2 : 1;
            return col - step;
        }

        public static int StepRight(string line, int col)
        {
            if (string.IsNullOrEmpty(line)) return 0;
            if (col < 0) col = 0;
            if (col >= line.Length) return line.Length;
            if (col > 0 && char.IsLowSurrogate(line[col]) && char.IsHighSurrogate(line[col - 1]))
                return col + 1;
            int step = (col + 1 < line.Length && char.IsHighSurrogate(line[col]) && char.IsLowSurrogate(line[col + 1])) ? 2 : 1;
            return col + step;
        }

        /// <summary>
        /// VS Code cursorWordLeft: skips whitespace left, then skips a contiguous run of either Regular or Separator characters.
        /// </summary>
        public static int WordLeft(string line, int col, string separators = null)
        {
            if (string.IsNullOrEmpty(line) || col <= 0) return 0;
            if (col > line.Length) col = line.Length;
            while (col > 0 && char.IsWhiteSpace(line[col - 1]))
                col = StepLeft(line, col);
            if (col <= 0) return 0;
            CharKind target = Classify(line[col - 1], separators);
            while (col > 0 && Classify(line[col - 1], separators) == target)
                col = StepLeft(line, col);
            return col;
        }

        /// <summary>
        /// VS Code cursorWordEndRight: skips whitespace right, then skips a contiguous run of either Regular or Separator characters.
        /// </summary>
        public static int WordRight(string line, int col, string separators = null)
        {
            if (string.IsNullOrEmpty(line)) return 0;
            if (col < 0) col = 0;
            if (col >= line.Length) return line.Length;
            while (col < line.Length && char.IsWhiteSpace(line[col]))
                col = StepRight(line, col);
            if (col >= line.Length) return line.Length;
            CharKind target = Classify(line[col], separators);
            while (col < line.Length && Classify(line[col], separators) == target)
                col = StepRight(line, col);
            return col;
        }

        /// <summary>
        /// VS Code cursorWordPartLeft: moves left by camelCase / PascalCase / ALLCAPS / snake_case subword boundary.
        /// </summary>
        public static int SubwordLeft(string line, int col, string separators = null)
        {
            if (string.IsNullOrEmpty(line) || col <= 0) return 0;
            if (col > line.Length) col = line.Length;

            while (col > 0 && (char.IsWhiteSpace(line[col - 1]) || line[col - 1] == '_' || Classify(line[col - 1], separators) == CharKind.Separator))
                col = StepLeft(line, col);
            if (col <= 0) return 0;

            char prev = line[col - 1];
            if (char.IsLower(prev))
            {
                while (col > 0 && char.IsLower(line[col - 1]))
                    col--;
                if (col > 0 && char.IsUpper(line[col - 1]))
                    col--;
                return col;
            }
            if (char.IsUpper(prev))
            {
                int startUpper = col;
                while (col > 0 && char.IsUpper(line[col - 1]))
                    col--;
                return col;
            }
            if (char.IsDigit(prev))
            {
                while (col > 0 && char.IsDigit(line[col - 1]))
                    col--;
                return col;
            }
            while (col > 0)
            {
                char c = line[col - 1];
                if (char.IsWhiteSpace(c) || c == '_' || Classify(c, separators) == CharKind.Separator || char.IsLetterOrDigit(c))
                    break;
                col = StepLeft(line, col);
            }
            return col;
        }

        /// <summary>
        /// VS Code cursorWordPartRight: moves right by camelCase / PascalCase / ALLCAPS / snake_case subword boundary.
        /// </summary>
        public static int SubwordRight(string line, int col, string separators = null)
        {
            if (string.IsNullOrEmpty(line)) return 0;
            if (col < 0) col = 0;
            if (col >= line.Length) return line.Length;

            while (col < line.Length && (char.IsWhiteSpace(line[col]) || line[col] == '_' || Classify(line[col], separators) == CharKind.Separator))
                col = StepRight(line, col);
            if (col >= line.Length) return line.Length;

            char cur = line[col];
            if (char.IsUpper(cur))
            {
                if (col + 1 < line.Length && char.IsUpper(line[col + 1]))
                {
                    int start = col;
                    while (col < line.Length && char.IsUpper(line[col]))
                        col++;
                    if (col < line.Length && char.IsLower(line[col]) && col - start > 1)
                        col--;
                    return col;
                }
                col++;
                while (col < line.Length && char.IsLower(line[col]))
                    col++;
                return col;
            }
            if (char.IsLower(cur))
            {
                while (col < line.Length && char.IsLower(line[col]))
                    col++;
                return col;
            }
            if (char.IsDigit(cur))
            {
                while (col < line.Length && char.IsDigit(line[col]))
                    col++;
                return col;
            }
            while (col < line.Length)
            {
                char c = line[col];
                if (char.IsWhiteSpace(c) || c == '_' || Classify(c, separators) == CharKind.Separator || char.IsLetterOrDigit(c))
                    break;
                col = StepRight(line, col);
            }
            return col;
        }

        /// <summary>
        /// Finds the word or separator or whitespace token at <paramref name="col"/> (used by double-click word selection).
        /// </summary>
        public static void WordAt(string line, int col, out int start, out int end, string separators = null)
        {
            start = 0;
            end = 0;
            if (string.IsNullOrEmpty(line)) return;
            if (col < 0) col = 0;
            if (col > line.Length) col = line.Length;

            int idx;
            if (col < line.Length && Classify(line[col], separators) == CharKind.Regular)
                idx = col;
            else if (col > 0 && Classify(line[col - 1], separators) == CharKind.Regular)
                idx = col - 1;
            else if (col < line.Length)
                idx = col;
            else
                idx = line.Length - 1;

            if (idx > 0 && char.IsLowSurrogate(line[idx]) && char.IsHighSurrogate(line[idx - 1]))
                idx--;

            CharKind kind = Classify(line[idx], separators);
            start = idx;
            while (start > 0 && Classify(line[start - 1], separators) == kind)
                start = StepLeft(line, start);

            end = StepRight(line, idx);
            while (end < line.Length && Classify(line[end], separators) == kind)
                end = StepRight(line, end);
        }

        /// <summary>
        /// VS Code Ctrl+T / editor.action.transposeLetters: swaps the characters before and after the caret
        /// (or the last two characters when at end of line; or the first two when at start of line).
        /// Surrogate-pair safe.
        /// </summary>
        public static string TransposeOnLine(string line, int col, out int newCol)
        {
            newCol = col;
            if (string.IsNullOrEmpty(line) || line.Length < 2) return line ?? "";
            if (col < 0) col = 0;
            if (col > line.Length) col = line.Length;
            if (col > 0 && col < line.Length && char.IsLowSurrogate(line[col]) && char.IsHighSurrogate(line[col - 1]))
                col--;

            int mid = col;
            if (mid == 0)
            {
                mid = StepRight(line, 0);
                if (mid >= line.Length) return line;
            }
            else if (mid >= line.Length)
            {
                mid = StepLeft(line, line.Length);
                if (mid <= 0) return line;
            }

            int left = StepLeft(line, mid);
            int right = StepRight(line, mid);
            if (left >= mid || right <= mid) return line;

            string a = line.Substring(left, mid - left);
            string b = line.Substring(mid, right - mid);
            newCol = right;
            return line.Substring(0, left) + b + a + line.Substring(right);
        }

        /// <summary>
        /// Sorts lines[startLine..endLine] inclusive in-place (ascending or descending, ordinal comparison like VS Code).
        /// </summary>
        public static bool SortLinesInPlace(List<string> lines, int startLine, int endLine, bool descending)
        {
            if (lines == null || lines.Count < 2) return false;
            if (startLine < 0) startLine = 0;
            if (endLine >= lines.Count) endLine = lines.Count - 1;
            if (endLine <= startLine) return false;

            int count = endLine - startLine + 1;
            string[] slice = new string[count];
            for (int i = 0; i < count; i++) slice[i] = lines[startLine + i] ?? "";
            Array.Sort(slice, delegate(string x, string y)
            {
                int cmp = string.CompareOrdinal(x, y);
                return descending ? -cmp : cmp;
            });

            bool changed = false;
            for (int i = 0; i < count; i++)
            {
                if (!string.Equals(lines[startLine + i], slice[i], StringComparison.Ordinal))
                {
                    lines[startLine + i] = slice[i];
                    changed = true;
                }
            }
            return changed;
        }
    }
}
