using System;
using System.IO;
using System.Linq;
using System.Text;
using Avalonia;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using codeLess.AvaloniaApp;
using codeLess.Studio;
using Xunit;

namespace codeLess.Avalonia.Tests
{
    [Collection("SequentialWorkbench")]
    public class EditorNicetiesHeadlessTests : IDisposable
    {
        private readonly string settingsPath;
        private readonly string settingsBackup;
        private readonly bool hadSettings;
        private readonly string dir;

        public EditorNicetiesHeadlessTests()
        {
            settingsPath = IdePersistentSettings.GetSettingsFilePath();
            hadSettings = File.Exists(settingsPath);
            settingsBackup = hadSettings ? File.ReadAllText(settingsPath) : null;
            dir = Path.Combine(Path.GetTempPath(), "codeLess_Niceties_" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(dir);
        }

        public void Dispose()
        {
            EditorNicetyOptions.ApplyFrom(new IdePersistentSettings());
            try
            {
                if (hadSettings) File.WriteAllText(settingsPath, settingsBackup);
                else if (File.Exists(settingsPath)) File.Delete(settingsPath);
            }
            catch { }
            try { Directory.Delete(dir, true); } catch { }
        }

        private static void Type(AvaloniaVirtualEditorCanvas canvas, string text)
        {
            foreach (char c in text)
                canvas.RaiseEvent(new TextInputEventArgs { RoutedEvent = InputElement.TextInputEvent, Text = c.ToString() });
        }

        private static void RenderOnce(AvaloniaVirtualEditorCanvas canvas, double w = 1000, double h = 600)
        {
            canvas.Measure(new Size(w, h));
            canvas.Arrange(new Rect(0, 0, w, h));
            var target = new RenderTargetBitmap(new PixelSize(10, 10));
            using (var ctx = target.CreateDrawingContext())
                canvas.Render(ctx);
        }

        private (MainWindow, EditorTabBuffer) Open(string name, string content)
        {
            string file = Path.Combine(dir, name);
            File.WriteAllText(file, content);
            var w = new MainWindow(dir);
            w.Show();
            var tab = w.OpenFileInTab(file, preview: false);
            return (w, tab);
        }

        [AvaloniaFact]
        public void Html_AutoClose_CloseTagCompletion_LinkedRename_AndUndo()
        {
            var (w, tab) = Open("index.html", "\n");
            var canvas = w.PrimaryEditor.Canvas;
            tab.CursorLine = 0; tab.CursorCol = 0;
            Type(canvas, "<div>");
            Assert.Equal("<div></div>", tab.Lines[0]);
            Assert.Equal(5, tab.CursorCol);

            // linked rename: typing in the start tag name renames the end tag
            tab.CursorCol = 4;
            Type(canvas, "x");
            Assert.Equal("<divx></divx>", tab.Lines[0]);
            Assert.Equal(5, tab.CursorCol);
            w.SimulateShortcut(Key.Back, KeyModifiers.None);
            Assert.Equal("<div></div>", tab.Lines[0]);
            // one undo step restores both tags
            Type(canvas, "p");
            Assert.Equal("<divp></divp>", tab.Lines[0]);
            tab.PerformUndo();
            Assert.Equal("<div></div>", tab.Lines[0]);

            // "</" completes the innermost open element
            tab.Lines[0] = "<ul><li>a";
            tab.CursorLine = 0; tab.CursorCol = tab.Lines[0].Length;
            Type(canvas, "</");
            Assert.Equal("<ul><li>a</li>", tab.Lines[0]);
            Assert.Equal(tab.Lines[0].Length, tab.CursorCol);

            // void elements are not closed
            tab.Lines[0] = "";
            tab.CursorCol = 0;
            Type(canvas, "<br>");
            Assert.Equal("<br>", tab.Lines[0]);
            w.SuppressUnsavedPrompts = true;
            w.Close();
        }

        [AvaloniaFact]
        public void Emmet_TabExpandsAbbreviations_AndKeepsPlainTab()
        {
            var (w, tab) = Open("page.html", "  ul>li.item$*2\nhello\n");
            tab.CursorLine = 0; tab.CursorCol = tab.Lines[0].Length;
            w.SimulateShortcut(Key.Tab, KeyModifiers.None);
            Assert.Equal("  <ul>", tab.Lines[0]);
            Assert.Equal("      <li class=\"item1\"></li>", tab.Lines[1]);
            Assert.Equal("      <li class=\"item2\"></li>", tab.Lines[2]);
            Assert.Equal("  </ul>", tab.Lines[3]);
            Assert.Equal(1, tab.CursorLine);
            Assert.Equal("      <li class=\"item1\">".Length, tab.CursorCol); // first empty content
            tab.PerformUndo();
            Assert.Equal("  ul>li.item$*2", tab.Lines[0]);

            // no abbreviation: Tab still indents
            tab.CursorLine = 1; tab.CursorCol = 5;
            w.SimulateShortcut(Key.Tab, KeyModifiers.None);
            Assert.StartsWith("hello ", tab.Lines[1]);
            w.SuppressUnsavedPrompts = true;
            w.Close();

            var (w2, css) = Open("site.css", ".card {\n  m10-20\n}\n");
            css.CursorLine = 1; css.CursorCol = css.Lines[1].Length;
            w2.SimulateShortcut(Key.Tab, KeyModifiers.None);
            Assert.Equal("  margin: 10px 20px;", css.Lines[1]);
            w2.SuppressUnsavedPrompts = true;
            w2.Close();
        }

        [AvaloniaFact]
        public void ChangeAllOccurrences_SelectsWholeWordMatches_KeepingThePrimaryCaret()
        {
            var (w, tab) = Open("Occ.cs", "int foo = 1;\nfoo += foobar + foo;\nvar x = \"foo\";\n");
            var canvas = w.PrimaryEditor.Canvas;
            tab.CursorLine = 1; tab.CursorCol = 1;
            w.SimulateShortcut(Key.F2, MacKeymap.PrimaryModifier);
            Assert.Equal(4, canvas.TotalCursorCount); // foobar is skipped, the string content counts as text
            Assert.Equal(1, tab.CursorLine);
            Assert.Equal("foo", tab.GetSelectedText());
            Assert.Equal(0, tab.SelectionStartCol);
            Type(canvas, "bar");
            Assert.Equal("int bar = 1;", tab.Lines[0]);
            Assert.Equal("bar += foobar + bar;", tab.Lines[1]);
            w.SuppressUnsavedPrompts = true;
            w.Close();
        }

        [AvaloniaFact]
        public void StickyScroll_Guides_Occurrences_AndTextMateRender()
        {
            var sb = new StringBuilder();
            sb.Append("namespace Demo\n{\n    public class Orders\n    {\n        public void Process(int count)\n        {\n");
            for (int i = 0; i < 80; i++) sb.Append("            Handle(count + " + i + ");\n");
            sb.Append("        }\n    }\n}\n");
            var (w, tab) = Open("Orders.cs", sb.ToString());
            var canvas = w.PrimaryEditor.Canvas;
            canvas.CompleteBackgroundTokenization();

            // scrolled into the method body: namespace, class and method headers stick
            tab.ScrollTopLine = 30;
            tab.CursorLine = 31; tab.CursorCol = 14;
            RenderOnce(canvas);
            Assert.Equal(new[] { 0, 2, 4 }, canvas.LastStickyLines.ToArray());
            Assert.True(canvas.LastFrameTextMateLines > 10, "TextMate lines: " + canvas.LastFrameTextMateLines);
            // active bracket pair = the method body braces; active indent guide inside it
            Assert.Equal(5, canvas.ActiveBracketPair.OpenLine);
            Assert.Equal(86, canvas.ActiveBracketPair.CloseLine);
            Assert.True(canvas.ActiveIndentGuideInfo.IsValid);

            // word under the caret is highlighted after the debounce
            canvas.FlushOccurrenceHighlight();
            Assert.Equal("Handle", canvas.CurrentOccurrenceTarget);
            RenderOnce(canvas);
            Assert.True(canvas.LastFrameOccurrenceCount >= 20, "occurrences: " + canvas.LastFrameOccurrenceCount);

            // clicking a sticky line reveals it and moves the caret there
            Assert.True(canvas.ClickStickyLineAt(new Point(200, canvas.LineHeight * 1.5)));
            Assert.Equal(1, tab.ScrollTopLine); // just below the namespace line that stays pinned
            RenderOnce(canvas);
            Assert.Equal(new[] { 0 }, canvas.LastStickyLines.ToArray());

            // moving the caret onto a row hidden by sticky lines scrolls it into view below them
            tab.ScrollTopLine = 30;
            RenderOnce(canvas);
            Assert.Equal(3, canvas.LastStickyLines.Count);
            tab.CursorLine = 34; tab.CursorCol = 0;
            w.SimulateShortcut(Key.Up, KeyModifiers.None);
            Assert.Equal(33, tab.CursorLine);
            Assert.True(tab.ScrollTopLine <= 30, "scroll " + tab.ScrollTopLine);
            tab.ScrollTopLine = 1;
            tab.CursorLine = 2; tab.CursorCol = 4;
            Assert.Equal(2, tab.CursorLine);
            Assert.Equal(4, tab.CursorCol);

            // folding the method keeps the class header but no longer pins the method
            tab.ScrollTopLine = 30;
            canvas.FoldAtLine(5); // (FoldAtLine on an Allman-style header without "{" folds nothing: existing behaviour)
            tab.ScrollTopLine = 3;
            RenderOnce(canvas);
            Assert.DoesNotContain(4, canvas.LastStickyLines);

            // switches
            canvas.UnfoldAll();
            tab.ScrollTopLine = 30;
            EditorNicetyOptions.StickyScroll = false;
            EditorNicetyOptions.HighlightOccurrences = false;
            RenderOnce(canvas);
            Assert.Empty(canvas.LastStickyLines);
            Assert.Null(canvas.CurrentOccurrenceTarget);
            EditorNicetyOptions.TextMateHighlighting = false;
            RenderOnce(canvas);
            Assert.Equal(0, canvas.LastFrameTextMateLines);
            w.Close();
        }

        [AvaloniaFact]
        public void Settings_PersistNicetySwitches()
        {
            var s = new StudioSettings();
            Assert.True(s.EditorStickyScroll && s.EditorBracketPairColorization && s.EditorEmmetOnTab);
            s.EditorStickyScroll = false;
            s.EditorLinkedTagEditing = false;
            string json = new System.Web.Script.Serialization.JavaScriptSerializer().Serialize(s);
            var back = new System.Web.Script.Serialization.JavaScriptSerializer().Deserialize<IdePersistentSettings>(json);
            Assert.False(back.EditorStickyScroll);
            Assert.False(back.EditorLinkedTagEditing);
            Assert.True(back.EditorIndentGuides);
            // files written before these settings existed keep the defaults
            var old = new System.Web.Script.Serialization.JavaScriptSerializer().Deserialize<IdePersistentSettings>("{\"TabSize\":2}");
            Assert.True(old.EditorStickyScroll && old.EditorTextMateHighlighting && old.EditorAutoCloseTags);
        }
    }
}
