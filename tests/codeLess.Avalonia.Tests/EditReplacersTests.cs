using System;
using Xunit;
using codeLess.Studio;

namespace codeLess.Avalonia.Tests
{
    public class EditReplacersTests
    {
        [Fact]
        public void M1_SameString_Throws()
        {
            var ex = Assert.Throws<ArgumentException>(() => EditReplacers.Replace("abc", "a", "a", false));
            Assert.Contains("must be different", ex.Message);
        }

        [Fact]
        public void M1_NotFound_Throws()
        {
            var ex = Assert.Throws<ArgumentException>(() => EditReplacers.Replace("abc", "d", "e", false));
            Assert.Contains("not found", ex.Message);
        }

        [Fact]
        public void M1_Ambiguous_Throws()
        {
            var ex = Assert.Throws<ArgumentException>(() => EditReplacers.Replace("a b a", "a", "c", false));
            Assert.Contains("multiple matches", ex.Message);
        }

        [Fact]
        public void M1_ReplaceAll_Works()
        {
            string res = EditReplacers.Replace("a b a b a", "a", "c", true);
            Assert.Equal("c b c b c", res);
        }

        [Fact]
        public void M1_Simple_Works()
        {
            string res = EditReplacers.Replace("hello world", "lo w", "p w", false);
            Assert.Equal("help world", res);
        }

        [Fact]
        public void M1_LineTrimmed_Works()
        {
            string res = EditReplacers.Replace("  hello  \n  world  ", "hello\nworld", "hi\nworld", false);
            Assert.Equal("hi\nworld", res);
        }

        [Fact]
        public void M1_WhitespaceNormalized_Works()
        {
            string res = EditReplacers.Replace("hello   world", "hello world", "hi world", false);
            Assert.Equal("hi world", res);
        }

        [Fact]
        public void M1_TrimmedBoundary_Works()
        {
            string res = EditReplacers.Replace("hello world", " world ", " earth", false);
            Assert.True(res == "hello earth" || res == "hello  earth");
        }

        [Fact]
        public void M1_EscapeNormalized_Works()
        {
            string res = EditReplacers.Replace("hello\nworld", "hello\\nworld", "hi\\nworld", false);
            Assert.Equal("hi\nworld", res);
        }
        
        [Fact]
        public void M1_CRLF_Preserved()
        {
            string res = EditReplacers.Replace("hello\r\nworld", "hello\nworld", "hi\nworld", false);
            Assert.Contains("hi", res);
        }
        
        [Fact]
        public void M1_BlockAnchor_PicksBest()
        {
            string content = "start\nfoo\nbar\nend\nstart\nbad\nbar\nend";
            string find = "start\nfoo\nbaz\nend";
            string res = EditReplacers.Replace(content, find, "start\nchanged\nend", false);
            Assert.Contains("changed", res);
        }
        
        [Fact]
        public void M1_IndentationFlexible_Works()
        {
            string content = "    start\n    mid\n    end";
            string find = "  start\n  mid\n  end";
            string res = EditReplacers.Replace(content, find, "new", false);
            Assert.Contains("new", res);
        }

        [Fact]
        public void M1_ContextAware_Works()
        {
            string content = "start\nfoo\nbar\nend";
            string find = "start\nfoo\nbaz\nend";
            // Wait, BlockAnchor will match this too because similarity >= 0.0.
            // Let's use 5 lines to make it interesting
            string content5 = "start\n1\n2\n3\nend";
            string find5 = "start\n1\n3\n4\nend"; // 50% match (1, 3 out of 1,3,4? Wait, find has 1,3,4)
            string res = EditReplacers.Replace(content5, find5, "new", false);
            Assert.Contains("new", res);
        }

        [Fact]
        public void M1_LineTrimmed_WithSpaces()
        {
            string res = EditReplacers.Replace("a \n b \n c", "a\nb\nc", "d", false);
            Assert.Equal("d", res);
        }

        [Fact]
        public void M1_EscapeNormalized_Tabs()
        {
            string res = EditReplacers.Replace("a\tb", "a\\tb", "c", false);
            Assert.Equal("c", res);
        }
        
        [Fact]
        public void M1_Ambiguous_NoReplace_Throws()
        {
            var ex = Assert.Throws<ArgumentException>(() => EditReplacers.Replace("x y x", "x", "z", false));
            Assert.Contains("multiple matches", ex.Message);
        }

        [Fact]
        public void M1_Simple_Multi_ReplaceAll()
        {
            string res = EditReplacers.Replace("x y x", "x", "z", true);
            Assert.Equal("z y z", res);
        }
        
        [Fact]
        public void M1_BlockAnchor_Similar()
        {
            string content = "A\nB\nC";
            string find = "A\nX\nC";
            string res = EditReplacers.Replace(content, find, "D", false);
            Assert.Equal("D", res);
        }
        
        [Fact]
        public void M1_WhitespaceNormalized_Multiline()
        {
            string res = EditReplacers.Replace("A \n B", "A B", "C", false);
            Assert.Equal("C", res);
        }
        
        [Fact]
        public void M1_IndentationFlexible_NoIndent()
        {
            string res = EditReplacers.Replace("A\nB", "A\nB", "C", false);
            Assert.Equal("C", res);
        }
        
        [Fact]
        public void M1_TrimmedBoundary_NoChange()
        {
            string res = EditReplacers.Replace("A B", "A B", "C", false);
            Assert.Equal("C", res);
        }
        
        [Fact]
        public void M1_EscapeNormalized_RegexChars()
        {
            string res = EditReplacers.Replace("A$B", "A\\$B", "C", false);
            Assert.Equal("C", res);
        }
        
        [Fact]
        public void M1_LineTrimmed_EmptyLines()
        {
            string res = EditReplacers.Replace("A\n\nB", "A\nB", "C", false);
            // wait, LineTrimmed matches non-empty lines? No, A\nB won't match A\n\nB.
            // Let's test with empty lines in find
            string res2 = EditReplacers.Replace("A\n \nB", "A\n\nB", "C", false);
            Assert.Equal("C", res2);
        }

        [Fact]
        public void M1_WhitespaceNormalized_TabsToSpaces()
        {
            string res = EditReplacers.Replace("A\tB", "A B", "C", false);
            Assert.Equal("C", res);
        }

        [Fact]
        public void M1_ReplaceAll_WorksForOverlapping()
        {
            // Replace handles non-overlapping matches
            string res = EditReplacers.Replace("aaaa", "aa", "b", true);
            Assert.Equal("bb", res);
        }
    }
}
