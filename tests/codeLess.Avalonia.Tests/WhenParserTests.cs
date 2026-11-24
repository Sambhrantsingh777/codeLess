using System;
using System.Collections.Generic;
using codeLess.Core;
using Xunit;

namespace codeLess.Avalonia.Tests
{
    public class TestContextKeyService : IContextKeyService
    {
        private readonly Dictionary<string, object> dict = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
        
        public void Set(string key, object value) => dict[key] = value;
        public object GetValue(string key) => dict.TryGetValue(key, out var v) ? v : null;
    }

    public class WhenParserTests
    {
        [Fact]
        public void PrecedenceAndOrNot()
        {
            var ctx = new TestContextKeyService();
            ctx.Set("A", true);
            ctx.Set("B", false);
            ctx.Set("C", true);

            // !A || B && C => false || false && true => false
            Assert.False(WhenParser.Evaluate(WhenParser.Parse("!A || B && C"), ctx));
            // (!A || B) && C => false && true => false
            Assert.False(WhenParser.Evaluate(WhenParser.Parse("(!A || B) && C"), ctx));
            // A || B && C => true || (false && true) => true
            Assert.True(WhenParser.Evaluate(WhenParser.Parse("A || B && C"), ctx));
        }

        [Fact]
        public void EqualityAndRegex()
        {
            var ctx = new TestContextKeyService();
            ctx.Set("lang", "csharp");
            ctx.Set("num", 10);

            Assert.True(WhenParser.Evaluate(WhenParser.Parse("lang == csharp"), ctx));
            Assert.True(WhenParser.Evaluate(WhenParser.Parse("lang == 'csharp'"), ctx));
            Assert.True(WhenParser.Evaluate(WhenParser.Parse("lang != python"), ctx));
            Assert.True(WhenParser.Evaluate(WhenParser.Parse("lang =~ /^cs.*/"), ctx));
            Assert.False(WhenParser.Evaluate(WhenParser.Parse("lang =~ /^py.*/"), ctx));
            Assert.True(WhenParser.Evaluate(WhenParser.Parse("num == 10"), ctx));
        }

        [Fact]
        public void InOperator()
        {
            var ctx = new TestContextKeyService();
            ctx.Set("ext", ".cs");
            ctx.Set("list", ".cs,.fs,.vb");

            Assert.True(WhenParser.Evaluate(WhenParser.Parse("ext in list"), ctx));
            Assert.True(WhenParser.Evaluate(WhenParser.Parse("ext not in 'foo,bar'"), ctx));
        }

        [Fact]
        public void UnknownKeys()
        {
            var ctx = new TestContextKeyService();
            Assert.False(WhenParser.Evaluate(WhenParser.Parse("unknown"), ctx));
            Assert.True(WhenParser.Evaluate(WhenParser.Parse("!unknown"), ctx));
        }
    }
}
