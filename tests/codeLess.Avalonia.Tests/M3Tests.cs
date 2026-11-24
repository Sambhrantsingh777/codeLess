using System;
using Xunit;
using codeLess.Studio;

namespace codeLess.Avalonia.Tests
{
    public class M3Tests
    {
        [Fact]
        public void GlobMatcher_Works()
        {
            var regex = GlobMatcher.GlobToRegex("**/*.cs");
            Assert.True(regex.IsMatch("src/Program.cs"));
            Assert.False(regex.IsMatch("src/Program.js"));
            
            var regex2 = GlobMatcher.GlobToRegex("src/{a,b}/*.cs");
            Assert.True(regex2.IsMatch("src/a/1.cs"));
            Assert.True(regex2.IsMatch("src/b/2.cs"));
            Assert.False(regex2.IsMatch("src/c/3.cs"));
        }
        
        [Fact]
        public void Terminal_OutputTruncation()
        {
            string longOutput = new string('A', 40000);
            string trimmed = LlmHarnessAndAgentEngine.TrimToolOutputKeepingTail(longOutput, 30000);
            Assert.Contains(" chars truncated] ...", trimmed);
            Assert.True(trimmed.Length < 35000);
            Assert.StartsWith(new string('A', 100), trimmed);
            Assert.EndsWith(new string('A', 100), trimmed);
        }
    }
}
