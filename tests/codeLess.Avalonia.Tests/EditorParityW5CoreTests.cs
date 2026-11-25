using System;
using System.Collections.Generic;
using codeLess.Core;
using codeLess.Studio;
using Xunit;

namespace codeLess.Avalonia.Tests
{
    public class EditorParityW5CoreTests
    {
        [Fact]
        public void WordNavigation_WordSeparators_MovesAndSelectsWordAndPunctuationRuns()
        {
            string line = "foo.bar(baz) + qux";
            // Forward word navigation stops at the end of each word or separator run
            int col = 0;
            col = EditorWordNavigation.WordRight(line, col);
            Assert.Equal(3, col); // "foo"
            col = EditorWordNavigation.WordRight(line, col);
            Assert.Equal(4, col); // "."
            col = EditorWordNavigation.WordRight(line, col);
            Assert.Equal(7, col); // "bar"
            col = EditorWordNavigation.WordRight(line, col);
            Assert.Equal(8, col); // "("
            col = EditorWordNavigation.WordRight(line, col);
            Assert.Equal(11, col); // "baz"
            col = EditorWordNavigation.WordRight(line, col);
            Assert.Equal(12, col); // ")"
            col = EditorWordNavigation.WordRight(line, col);
            Assert.Equal(14, col); // " +"
            col = EditorWordNavigation.WordRight(line, col);
            Assert.Equal(18, col); // " qux"

            // Backward word navigation stops at the start of each word or separator run
            col = EditorWordNavigation.WordLeft(line, col);
            Assert.Equal(15, col); // before "qux"
            col = EditorWordNavigation.WordLeft(line, col);
            Assert.Equal(13, col); // before "+"
            col = EditorWordNavigation.WordLeft(line, col);
            Assert.Equal(11, col); // before ")"
            col = EditorWordNavigation.WordLeft(line, col);
            Assert.Equal(8, col); // before "baz"
            col = EditorWordNavigation.WordLeft(line, col);
            Assert.Equal(7, col); // before "("
            col = EditorWordNavigation.WordLeft(line, col);
            Assert.Equal(4, col); // before "bar"
            col = EditorWordNavigation.WordLeft(line, col);
            Assert.Equal(3, col); // before "."
            col = EditorWordNavigation.WordLeft(line, col);
            Assert.Equal(0, col); // before "foo"

            // WordAt selects word or separator or whitespace run
            EditorWordNavigation.WordAt(line, 1, out int s1, out int e1);
            Assert.Equal("foo", line.Substring(s1, e1 - s1));
            EditorWordNavigation.WordAt("a == b", 2, out int s2, out int e2);
            Assert.Equal("==", line.Length > 0 ? "a == b".Substring(s2, e2 - s2) : "");

            // Custom wordSeparators (treating '.' as part of word when omitted from separators)
            Assert.Equal(7, EditorWordNavigation.WordRight(line, 0, "()+"));
        }

        [Fact]
        public void SubwordNavigation_CamelCase_PascalCase_AllCaps_SnakeCase_AndDigits()
        {
            string s = "camelCase HTMLParser snake_case foo123Bar";
            int c = 0;
            c = EditorWordNavigation.SubwordRight(s, c);
            Assert.Equal(5, c); // "camel"
            c = EditorWordNavigation.SubwordRight(s, c);
            Assert.Equal(9, c); // "Case"
            c = EditorWordNavigation.SubwordRight(s, c);
            Assert.Equal(14, c); // "HTML"
            c = EditorWordNavigation.SubwordRight(s, c);
            Assert.Equal(20, c); // "Parser"
            c = EditorWordNavigation.SubwordRight(s, c);
            Assert.Equal(26, c); // "snake"
            c = EditorWordNavigation.SubwordRight(s, c);
            Assert.Equal(31, c); // "_case"
            c = EditorWordNavigation.SubwordRight(s, c);
            Assert.Equal(35, c); // "foo"
            c = EditorWordNavigation.SubwordRight(s, c);
            Assert.Equal(38, c); // "123"
            c = EditorWordNavigation.SubwordRight(s, c);
            Assert.Equal(41, c); // "Bar"

            // Reverse subword navigation
            c = EditorWordNavigation.SubwordLeft(s, c);
            Assert.Equal(38, c); // before "Bar"
            c = EditorWordNavigation.SubwordLeft(s, c);
            Assert.Equal(35, c); // before "123"
            c = EditorWordNavigation.SubwordLeft(s, c);
            Assert.Equal(32, c); // before "foo"
            c = EditorWordNavigation.SubwordLeft(s, c);
            Assert.Equal(27, c); // before "case"
            c = EditorWordNavigation.SubwordLeft(s, c);
            Assert.Equal(21, c); // before "snake"
            c = EditorWordNavigation.SubwordLeft(s, c);
            Assert.Equal(14, c); // before "Parser"
            c = EditorWordNavigation.SubwordLeft(s, c);
            Assert.Equal(10, c); // before "HTML"
            c = EditorWordNavigation.SubwordLeft(s, c);
            Assert.Equal(5, c); // before "Case"
            c = EditorWordNavigation.SubwordLeft(s, c);
            Assert.Equal(0, c); // before "camel"
        }

        [Fact]
        public void TransposeAndSortLines_CoreBehaviour()
        {
            // Middle of line swaps char before and after caret and advances caret
            string t1 = EditorWordNavigation.TransposeOnLine("abcd", 2, out int col1);
            Assert.Equal("acbd", t1);
            Assert.Equal(3, col1);

            // End of line swaps the last two characters
            string t2 = EditorWordNavigation.TransposeOnLine("abcd", 4, out int col2);
            Assert.Equal("abdc", t2);
            Assert.Equal(4, col2);

            // Surrogate pair (emoji) transpose
            string emojiLine = "a\U0001F680b";
            string t3 = EditorWordNavigation.TransposeOnLine(emojiLine, 1, out int col3);
            Assert.Equal("\U0001F680ab", t3);
            Assert.Equal(3, col3);

            // Sort lines ascending and descending
            var lines = new List<string> { "charlie", "alpha", "bravo", "delta" };
            Assert.True(EditorWordNavigation.SortLinesInPlace(lines, 0, 2, descending: false));
            Assert.Equal(new[] { "alpha", "bravo", "charlie", "delta" }, lines.ToArray());
            Assert.True(EditorWordNavigation.SortLinesInPlace(lines, 0, 3, descending: true));
            Assert.Equal(new[] { "delta", "charlie", "bravo", "alpha" }, lines.ToArray());
        }

        [Fact]
        public void UndoGrouping_WordBoundariesAndCursorMovementBreakTypingGroup()
        {
            var buf = new EditorTabBuffer { Content = "", SavedContent = "" };
            buf.SyncLinesFromContent();

            // Type "hi there" character by character using coalesceTyping: true
            foreach (char ch in "hi there")
            {
                buf.PushUndoSnapshot(coalesceTyping: true);
                buf.InsertTextAtCursor(ch.ToString());
            }
            buf.SyncContentFromLines();
            Assert.Equal("hi there", buf.Lines[0]);

            // First Undo removes "there", leaving "hi "
            Assert.True(buf.Undo());
            Assert.Equal("hi ", buf.Lines[0]);

            // Second Undo removes "hi ", leaving ""
            Assert.True(buf.Undo());
            Assert.Equal("", buf.Lines[0]);
        }

        [Fact]
        public void SnippetMultiTabstop_TabAndShiftTabAdvanceAndRetreat_AndEscapeExits()
        {
            var buf = new EditorTabBuffer { Content = "", SavedContent = "", Language = "cs" };
            buf.SyncLinesFromContent();
            var snip = new CodeSnippetItem
            {
                Prefix = "fn",
                Language = "csharp",
                Body = "void ${1:Name}(${2:int x}) { $0 }"
            };
            SnippetEngine.InsertAtCaret(buf, snip, 0, 4, true);
            buf.SyncContentFromLines();
            Assert.Equal("void Name(int x) {  }", buf.Lines[0]);
            Assert.True(SnippetEngine.HasActiveSession(buf));
            Assert.Equal("Name", buf.GetSelectedText());

            // Replace $1 with a longer identifier "ComputeValue"
            buf.DeleteSelection();
            buf.InsertTextAtCursor("ComputeValue");
            Assert.Equal("void ComputeValue(int x) {  }", buf.Lines[0]);

            // Tab advances to $2 ("int x"), adjusted for the length change on the same line
            Assert.True(SnippetEngine.TryJumpStop(buf, backwards: false));
            Assert.Equal("int x", buf.GetSelectedText());
            Assert.True(SnippetEngine.HasActiveSession(buf));

            // Shift+Tab retreats back to $1
            Assert.True(SnippetEngine.TryJumpStop(buf, backwards: true));
            Assert.Equal(5, buf.SelectionStartCol);
            Assert.Equal(17, buf.CursorCol);

            // Tab -> $2 -> Tab -> $0 (exits snippet session)
            Assert.True(SnippetEngine.TryJumpStop(buf, backwards: false));
            Assert.Equal("int x", buf.GetSelectedText());
            Assert.True(SnippetEngine.TryJumpStop(buf, backwards: false));
            Assert.False(buf.HasSelection());
            Assert.Equal("void ComputeValue(int x) { ".Length, buf.CursorCol);
            Assert.False(SnippetEngine.HasActiveSession(buf));
        }

        [Fact]
        public void CompletionFiltering_FuzzyMatchIndices_AndRecencyBoost()
        {
            LspCompletionList.ClearRecentAccepted();
            try
            {
                var items = new List<LspCompletionItem>
                {
                    new LspCompletionItem { Label = "formatDocument", Kind = 2 },
                    new LspCompletionItem { Label = "formatSelection", Kind = 2 }
                };
                var sorted1 = LspCompletionList.FilterAndSort(items, "format", 10);
                Assert.Equal("formatDocument", sorted1[0].Label);

                // Accepting "formatSelection" boosts its recency for subsequent queries
                LspCompletionList.RecordAccepted("formatSelection");
                var sorted2 = LspCompletionList.FilterAndSort(items, "format", 10);
                Assert.Equal("formatSelection", sorted2[0].Label);

                // MatchedIndices returns the exact matched character positions
                var indices = LspCompletionList.MatchedIndices("formatSelection", "fSel");
                Assert.Equal(new[] { 0, 6, 7, 8 }, indices.ToArray());
            }
            finally
            {
                LspCompletionList.ClearRecentAccepted();
            }
        }
    }
}
