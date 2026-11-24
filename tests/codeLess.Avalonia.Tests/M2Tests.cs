using System;
using System.Collections.Generic;
using System.IO;
using Xunit;
using codeLess.Studio;

namespace codeLess.Avalonia.Tests
{
    public class M2Tests
    {
        [Fact]
        public void BinaryDetector_Works()
        {
            Assert.True(BinaryDetector.IsBinaryFile("test.png"));
            Assert.True(BinaryDetector.IsBinaryFile("test.dll"));
            Assert.False(BinaryDetector.IsBinaryFile("test.cs"));
            
            byte[] badBytes = new byte[100];
            badBytes[0] = 0; // NUL
            string badContent = System.Text.Encoding.UTF8.GetString(badBytes);
            Assert.True(BinaryDetector.IsBinaryFile("unknown.ext", badContent));
        }

        [Fact]
        public void MissingFileSuggestions_Works()
        {
            var paths = new List<string> { "/root/src/app.cs", "/root/src/app.ts", "/root/lib/utils.cs" };
            var suggestions = MissingFileSuggestions.SuggestSimilarFiles("/root/appp.cs", paths);
            Assert.Contains("/root/src/app.cs", suggestions); // Levenshtein <= 2
            
            var sugg2 = MissingFileSuggestions.SuggestSimilarFiles("/root/utils", paths);
            Assert.Contains("/root/lib/utils.cs", sugg2); // Contains
        }
        
        [Fact]
        public void FileTimeTracker_Works()
        {
            var tracker = new FileTimeTracker();
            tracker.RecordRead("a.txt", new DateTime(2020, 1, 1));
            Assert.True(tracker.HasRead("a.txt"));
            Assert.False(tracker.HasRead("b.txt"));
            Assert.Equal(new DateTime(2020, 1, 1), tracker.GetLastReadTime("a.txt"));
        }
    }
}
