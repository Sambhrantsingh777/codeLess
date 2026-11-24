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
            Assert.False(WhenParser.Evaluate(WhenParser.Parse("!A || B && C"), ctx));
            Assert.False(WhenParser.Evaluate(WhenParser.Parse("(!A || B) && C"), ctx));
            Assert.True(WhenParser.Evaluate(WhenParser.Parse("A || B && C"), ctx));
        }

        [Fact]
        public void EqualityAndRegex()
        {
            var ctx = new TestContextKeyService();
            ctx.Set("lang", "csharp");
            ctx.Set("num", 10);

            var node1 = WhenParser.Parse("lang == csharp");
            Assert.True(WhenParser.Evaluate(node1, ctx), "1");
            Assert.True(WhenParser.Evaluate(WhenParser.Parse("lang == 'csharp'"), ctx), "2");
            Assert.True(WhenParser.Evaluate(WhenParser.Parse("lang != python"), ctx), "3");
            
            var n = WhenParser.Parse("lang =~ /^cs.*/");
            string left = n.Left?.Text;
            string right = n.Right?.Text;
            object val = ctx.GetValue(left);
            Assert.True(WhenParser.Evaluate(n, ctx), $"4: left={left}, right={right}, val={val}");
            
            Assert.False(WhenParser.Evaluate(WhenParser.Parse("lang =~ /^py.*/"), ctx), "5");
            Assert.True(WhenParser.Evaluate(WhenParser.Parse("num == 10"), ctx), "6");
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
