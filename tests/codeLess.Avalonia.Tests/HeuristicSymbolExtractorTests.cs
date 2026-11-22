using System.Linq;
using codeLess.Studio;
using Xunit;

namespace codeLess.Avalonia.Tests
{
    /// <summary>Outline without a language server: real-world declarations must be found, statements/calls must not.</summary>
    public class HeuristicSymbolExtractorTests
    {
        private static (string kind, string name)? M(string line, string lang)
        {
            return HeuristicSymbolExtractor.TryMatch(line, lang, out string k, out string n) ? (k, n) : ((string, string)?)null;
        }

        [Theory]
        [InlineData("    public sealed class OrderPipeline : IOrderPipeline", "cs", "class", "OrderPipeline")]
        [InlineData("public static partial class Program", "cs", "class", "Program")]
        [InlineData("internal readonly record struct Point(int X, int Y);", "cs", "record", "Point")]
        [InlineData("public record Person(string Name);", "cs", "record", "Person")]
        [InlineData("    public IReadOnlyList<Order> Process(IEnumerable<Order> orders, decimal discount)", "cs", "method", "Process")]
        [InlineData("        private async Task<Dictionary<string, int>> LoadAsync(CancellationToken ct)", "cs", "method", "LoadAsync")]
        [InlineData("        public OrderPipeline(ILogger log) : base(log)", "cs", "method", "OrderPipeline")]
        [InlineData("        public override string ToString() => Name;", "cs", "method", "ToString")]
        [InlineData("        Task<int> RunAsync(CancellationToken ct);", "cs", "method", "RunAsync")]
        [InlineData("        public string Name { get; set; }", "cs", "prop", "Name")]
        [InlineData("namespace Acme.Orders", "cs", "namespace", "Acme.Orders")]
        [InlineData("export default class Store {", "ts", "class", "Store")]
        [InlineData("export async function loadOrders(id: string): Promise<Order[]> {", "ts", "fn", "loadOrders")]
        [InlineData("export const useOrders = (id: string) => {", "tsx", "fn", "useOrders")]
        [InlineData("export interface Props { orders: Order[] }", "tsx", "interface", "Props")]
        [InlineData("type Props = { orders: Order[] };", "ts", "type", "Props")]
        [InlineData("  render() {", "js", "method", "render")]
        [InlineData("def build_index(path, *, limit=10):", "py", "fn", "build_index")]
        [InlineData("async def fetch(session):", "py", "fn", "fetch")]
        [InlineData("class Repo(Base):", "py", "class", "Repo")]
        [InlineData("func (s *Server) Handle(w http.ResponseWriter, r *http.Request) {", "go", "fn", "Handle")]
        [InlineData("type Server struct {", "go", "type", "Server")]
        [InlineData("pub(crate) async fn parse_line(input: &str) -> Result<Token> {", "rs", "fn", "parse_line")]
        [InlineData("impl Display for Token {", "rs", "impl", "Display")]
        [InlineData("pub trait Shape {", "rs", "trait", "Shape")]
        [InlineData("public static void main(String[] args) {", "java", "method", "main")]
        [InlineData("fun computeTotal(items: List<Item>): Double {", "kt", "fn", "computeTotal")]
        [InlineData("## Installation & Setup", "md", "heading", "Installation & Setup")]
        public void Declarations_AreFound(string line, string lang, string kind, string name)
        {
            var r = M(line, lang);
            Assert.True(r.HasValue, "not detected: " + line);
            Assert.Equal(kind, r.Value.kind);
            Assert.Equal(name, r.Value.name);
        }

        [Theory]
        [InlineData("            Console.WriteLine(\"x\");", "cs")]
        [InlineData("            var accepted = new List<Order>();", "cs")]
        [InlineData("            if (order.Lines[0].Price > Limits[(int)order.Kind]) { accepted.Add(order); }", "cs")]
        [InlineData("            foreach (var order in orders.Where(o => o.Lines.Count > 0))", "cs")]
        [InlineData("            return Compute(x, y);", "cs")]
        [InlineData("            await SaveAsync(doc);", "cs")]
        [InlineData("            throw new InvalidOperationException(\"bad\");", "cs")]
        [InlineData("            string s = Format(x);", "cs")]
        [InlineData("            using (var r = Open())", "cs")]
        [InlineData("        #region Helpers", "cs")]
        [InlineData("        // public void Commented()", "cs")]
        [InlineData("  doThing(x)", "js")]
        [InlineData("    print(\"hello\")", "py")]
        [InlineData("    fmt.Println(x)", "go")]
        [InlineData("plain paragraph text", "md")]
        public void Statements_AreNotDeclarations(string line, string lang)
        {
            Assert.False(M(line, lang).HasValue, "false positive: " + line);
        }

        [Fact]
        public void BufferAnalysis_FillsOutlineForRealWorldCSharp()
        {
            string src = "using System;\nnamespace Acme.Orders\n{\n    public sealed class OrderPipeline : IOrderPipeline\n    {\n" +
                         "        public IReadOnlyList<Order> Process(IEnumerable<Order> orders, decimal discount)\n        {\n" +
                         "            var accepted = new List<Order>();\n            return accepted;\n        }\n    }\n}\n";
            var buf = new EditorTabBuffer { FilePath = "/tmp/x/OrderPipeline.cs", Content = src };
            buf.SyncLinesFromContent();
            LlmHarnessAndAgentEngine.AnalyzeBufferAstAndDiagnostics(buf);
            var names = buf.Symbols.Select(s => s.Kind + " " + s.Name).ToList();
            Assert.Contains("class OrderPipeline", names);
            Assert.Contains("method Process", names);
            Assert.DoesNotContain(names, n => n.EndsWith(" List") || n.EndsWith(" accepted"));
        }
    }
}
