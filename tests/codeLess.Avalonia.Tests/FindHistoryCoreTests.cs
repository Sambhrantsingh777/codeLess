using codeLess.Studio;
using Xunit;

namespace codeLess.Avalonia.Tests
{
    /// <summary>v3.3.3 report: "The up button on the Find bar should show previous searches" (VS Code input history).</summary>
    public class FindHistoryCoreTests
    {
        [Fact]
        public void Up_Walks_Back_And_Down_Returns_To_What_Was_Being_Typed()
        {
            var h = new InputHistory();
            h.Add("alpha"); h.Add("beta"); h.Add("gamma");
            Assert.Equal("gamma", h.Previous("gam"));
            Assert.Equal("beta", h.Previous("gamma"));
            Assert.Equal("alpha", h.Previous("beta"));
            Assert.Null(h.Previous("alpha")); // nothing older
            Assert.Equal("beta", h.Next("alpha"));
            Assert.Equal("gamma", h.Next("beta"));
            Assert.Equal("gam", h.Next("gamma")); // the draft comes back
            Assert.False(h.IsNavigating);
            Assert.Null(h.Next("gam"));
        }

        [Fact]
        public void Up_Right_After_Committing_Skips_The_Current_Entry()
        {
            var h = new InputHistory();
            h.Add("one"); h.Add("two");
            Assert.Equal("one", h.Previous("two")); // box already shows "two"
        }

        [Fact]
        public void Duplicates_Move_To_The_End_And_Capacity_Is_Kept()
        {
            var h = new InputHistory(null, 3);
            h.Add("a"); h.Add("b"); h.Add("a"); h.Add("c"); h.Add("d");
            Assert.Equal(new[] { "a", "c", "d" }, h.Items.ToArray());
            Assert.False(h.Add(""));
            Assert.False(h.Add("multi\nline"));
        }

        [Fact]
        public void Typing_Resets_Navigation()
        {
            var h = new InputHistory();
            h.Add("x"); h.Add("y");
            Assert.Equal("y", h.Previous(""));
            h.ResetNavigation();
            Assert.Equal("y", h.Previous("typed")); // starts from the newest again
        }
    }
}
