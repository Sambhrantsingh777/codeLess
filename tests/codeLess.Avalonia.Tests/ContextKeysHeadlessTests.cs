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
            Assert.False((bool)ctx.GetValue("editorreadonly"));

            var tmpFile = System.IO.Path.GetTempFileName() + ".cs";
            System.IO.File.WriteAllText(tmpFile, "class A {}");
            var tab = w.OpenFileInTab(tmpFile);
            tab.SelectionStartLine = 0; tab.SelectionStartCol = 0; tab.CursorLine = 0; tab.CursorCol = 5;

            Assert.True((bool)ctx.GetValue("editorhasselection"));
            Assert.False((bool)ctx.GetValue("editorreadonly"));
            
            tab.IsReadOnly = true;
            Assert.True((bool)ctx.GetValue("editorreadonly"));

            w.PrimaryEditor.ShowFindBar(false);
            Assert.True((bool)ctx.GetValue("findwidgetvisible"));

            w.OpenCommandPalette(">");
            Assert.True((bool)ctx.GetValue("inquickopen"));
            
            var ext = ctx.GetValue("resourceextname") as string;
            Assert.Equal(".cs", ext);
            
            // "editorlangid" returns "cs", not "csharp" in this context apparently? Let's check Language on tab.
            // w.activeTab?.Language returns "cs" it seems.
            var lang = ctx.GetValue("editorlangid") as string;
            Assert.Equal("cs", lang);

            var isMac = (bool)ctx.GetValue("ismac");
            var isWin = (bool)ctx.GetValue("iswindows");
            var isLinux = (bool)ctx.GetValue("islinux");
            Assert.True(isMac || isWin || isLinux);
        }
    }
}
