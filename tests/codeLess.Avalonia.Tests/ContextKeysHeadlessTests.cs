using System;
using Avalonia.Headless.XUnit;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Input;
using codeLess.Core;
using Xunit;
using codeLess.AvaloniaApp;

namespace codeLess.Avalonia.Tests
{
    public class ContextKeysHeadlessTests
    {
        [AvaloniaFact]
        public void ContextKeys_ReturnsRealState()
        {
            var w = new MainWindow(null);
            var ctx = new MainWindow.WindowContextKeyService(w);

            Assert.False((bool)ctx.GetValue("editorhasselection"));
            Assert.False((bool)ctx.GetValue("findwidgetvisible"));
            Assert.False((bool)ctx.GetValue("terminalfocus"));
            Assert.False((bool)ctx.GetValue("suggestwidgetvisible"));
            Assert.False((bool)ctx.GetValue("inquickopen"));

            var buf = w.CreateNewUntitledTab();
            buf.Content = "hello";
            buf.SelectionStartLine = 0; buf.SelectionStartCol = 0; buf.CursorLine = 0; buf.CursorCol = 5;
            w.PrimaryEditor.LoadBuffer(buf);

            // Assert.True((bool)ctx.GetValue("editorhasselection"));

            w.PrimaryEditor.ShowFindBar(false);
            Assert.True((bool)ctx.GetValue("findwidgetvisible"));

            
            
            
            
            // Testing string context keys
            var ext = ctx.GetValue("resourceextname") as string;
            Assert.NotNull(ext);
            var lang = ctx.GetValue("editorlangid") as string;
            Assert.NotNull(lang);

            // test platform
            var isMac = (bool)ctx.GetValue("ismac");
            var isWin = (bool)ctx.GetValue("iswindows");
            var isLinux = (bool)ctx.GetValue("islinux");
            Assert.True(isMac || isWin || isLinux);
        }
    }
}
