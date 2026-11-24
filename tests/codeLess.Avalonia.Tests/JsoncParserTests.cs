using System;
using System.Collections.Generic;
using codeLess.Core;
using Xunit;

namespace codeLess.Avalonia.Tests
{
    public class JsoncParserTests
    {
        [Fact]
        public void StripsCommentsAndTrailingCommas()
        {
            string jsonc = @"
            {
                // line comment
                ""a"": 1,
                /* block comment */
                ""b"": [
                    2, 
                    3, // another line
                ],
                ""c"": ""// not a comment /* still string */ "",
            }";

            string stripped = JsoncParser.StripJsonc(jsonc);
            Assert.Contains("\"// not a comment /* still string */ \"", stripped);
            Assert.DoesNotContain("line comment", stripped);
            Assert.DoesNotContain("block comment", stripped);
            Assert.DoesNotContain("another line", stripped);

            var result = JsoncParser.Parse(jsonc, out string err) as Dictionary<string, object>;
            Assert.Null(err);
            Assert.NotNull(result);
            Assert.Equal(1, result["a"]);
            Assert.Equal("1", result["a"].ToString()); // depending on deserialize type
        }
    }
}
