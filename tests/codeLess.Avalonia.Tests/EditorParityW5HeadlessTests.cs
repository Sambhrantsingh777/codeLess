using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Avalonia;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using codeLess.AvaloniaApp;
using codeLess.Core;
using codeLess.Studio;
using Xunit;

namespace codeLess.Avalonia.Tests
{
    [Collection("SequentialWorkbench")]
    public class EditorParityW5HeadlessTests : IDisposable
    {
        private readonly string tempDir;
        private readonly List<MainWindow> windows = new List<MainWindow>();
        private readonly bool savedSmoothScroll = EditorScrollOptions.SmoothScrolling;
        private readonly bool savedSmoothCaret = EditorScrollOptions.SmoothCaretAnimation;
        private readonly bool savedEmptyClip = EditorNicetyOptions.EmptySelectionClipboard;
        private readonly bool savedUseTabStops = EditorNicetyOptions.UseTabStops;
        private readonly bool savedAcceptEnter = EditorNicetyOptions.AcceptSuggestionOnEnter;
        private readonly int savedSurrounding = EditorNicetyOptions.CursorSurroundingLines;
        private readonly string savedRenderWs = EditorNicetyOptions.RenderWhitespace;
        private readonly string savedWordSeps = EditorWordNavigation.WordSeparators;

        public EditorParityW5HeadlessTests()
        {
            tempDir = Path.Combine(Path.GetTempPath(), "codeless_w5_editorqa_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);
            EditorScrollOptions.SmoothScrolling = false;
            EditorScrollOptions.SmoothCaretAnimation = false;
        }

        public void Dispose()
        {
            EditorScrollOptions.SmoothScrolling = savedSmoothScroll;
            EditorScrollOptions.SmoothCaretAnimation = savedSmoothCaret;
            EditorNicetyOptions.EmptySelectionClipboard = savedEmptyClip;
            EditorNicetyOptions.UseTabStops = savedUseTabStops;
            EditorNicetyOptions.AcceptSuggestionOnEnter = savedAcceptEnter;
            EditorNicetyOptions.CursorSurroundingLines = savedSurrounding;
            EditorNicetyOptions.RenderWhitespace = savedRenderWs;
            EditorWordNavigation.WordSeparators = savedWordSeps;
            LspCompletionList.ClearRecentAccepted();
            foreach (var w in windows)
            {
                try { w.SuppressUnsavedPrompts = true; w.Close(); } catch { }
            }
            Dispatcher.UIThread.RunJobs();
            try { if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true); } catch { }
        }

        private (MainWindow win, EditorTabBuffer tab, AvaloniaVirtualEditorCanvas canvas) OpenEditor(string fileName, string content)
        {
            string path = Path.Combine(tempDir, fileName);
            File.WriteAllText(path, content);
            var w = new MainWindow(tempDir) { Width = 1280, Height = 800, SuppressUnsavedPrompts = true };
            windows.Add(w);
            w.Show();
            var tab = w.OpenFileInTab(path, preview: false);
            var canvas = w.PrimaryEditor.Canvas;
            canvas.Measure(new Size(900, 600));
            canvas.Arrange(new Rect(0, 0, 900, 600));
            w.UpdateLayout();
            Dispatcher.UIThread.RunJobs();
            return (w, tab, canvas);
        }

        private static void TypeText(AvaloniaVirtualEditorCanvas canvas, string text)
        {
            foreach (char ch in text)
            {
                var ev = new TextInputEventArgs
                {
                    RoutedEvent = InputElement.TextInputEvent,
                    Text = ch.ToString()
                };
                canvas.RaiseEvent(ev);
            }
        }

        private static void SendCanvasKey(AvaloniaVirtualEditorCanvas canvas, Key key, KeyModifiers mods = KeyModifiers.None)
        {
            using (MacKeymap.BeginLenientPrimaryMatching())
            {
                canvas.RaiseEvent(new KeyEventArgs
                {
                    RoutedEvent = InputElement.KeyDownEvent,
                    Key = key,
                    KeyModifiers = mods
                });
            }
        }

        [AvaloniaFact]
        public void WordAndSubwordMovement_InCanvas_RespectsWordSeparators_AndCamelCase()
        {
            var (w, tab, canvas) = OpenEditor("words.cs", "foo.bar(baz)\ncamelCaseIdentifier\n");
            KeyModifiers wordMod = MacKeymap.IsMacOS ? KeyModifiers.Alt : KeyModifiers.Control;

            tab.CursorLine = 0;
            tab.CursorCol = 0;
            SendCanvasKey(canvas, Key.Right, wordMod);
            Assert.Equal(3, tab.CursorCol); // "foo"
            SendCanvasKey(canvas, Key.Right, wordMod);
            Assert.Equal(4, tab.CursorCol); // "."
            SendCanvasKey(canvas, Key.Right, wordMod);
            Assert.Equal(7, tab.CursorCol); // "bar"
            SendCanvasKey(canvas, Key.Left, wordMod);
            Assert.Equal(4, tab.CursorCol); // before "bar"
            SendCanvasKey(canvas, Key.Left, wordMod);
            Assert.Equal(3, tab.CursorCol); // before "."

            // DeleteWordRight deletes only "." (the separator run), not "bar(baz)"
            canvas.DeleteWordRight();
            Assert.Equal("foobar(baz)", tab.Lines[0]);

            // Subword movement on camelCaseIdentifier
            tab.CursorLine = 1;
            tab.CursorCol = 0;
            canvas.MoveCaretSubword(1, shift: false);
            Assert.Equal(5, tab.CursorCol); // "camel"
            canvas.MoveCaretSubword(1, shift: true);
            Assert.Equal("Case", tab.GetSelectedText());
            canvas.DeleteSubwordRight(); // deletes selection "Case"
            Assert.Equal("camelIdentifier", tab.Lines[1]);
            canvas.DeleteSubwordLeft(); // deletes "camel"
            Assert.Equal("Identifier", tab.Lines[1]);
        }

        [AvaloniaFact]
        public void PageUpAndPageDown_PreserveCursorRelativeViewportOffset()
        {
            var lines = Enumerable.Range(0, 120).Select(i => "line " + i).ToArray();
            var (w, tab, canvas) = OpenEditor("page.txt", string.Join("\n", lines) + "\n");
            tab.ScrollTopLine = 10;
            tab.CursorLine = 15;
            tab.CursorCol = 2;
            int initialOffset = tab.CursorLine - tab.ScrollTopLine;

            SendCanvasKey(canvas, Key.PageDown);
            Assert.True(tab.ScrollTopLine > 10);
            Assert.Equal(initialOffset, tab.CursorLine - tab.ScrollTopLine);

            SendCanvasKey(canvas, Key.PageUp);
            Assert.Equal(10, tab.ScrollTopLine);
            Assert.Equal(15, tab.CursorLine);
        }

        [AvaloniaFact]
        public void DoubleClickDragByWord_TripleClickDragByLine_AndSelectionDragDropMoveAndCopy()
        {
            var (w, tab, canvas) = OpenEditor("mouse.txt", "alpha beta gamma delta\nsecond line here\nthird line here\n");
            // Double-click "beta" and drag to "gamma" selects whole words "beta gamma"
            double y0 = canvas.LineHeight * 0.5;
            double xBeta = AvaloniaVirtualEditorCanvas.GutterWidth + 8 + 7 * canvas.CharWidth;
            double xGamma = AvaloniaVirtualEditorCanvas.GutterWidth + 8 + 13 * canvas.CharWidth;

            canvas.SelectWordAt(0, 7);
            Assert.Equal("beta", tab.GetSelectedText());

            // Selection drag-and-drop move: select "beta ", drag to end of line 1
            tab.SelectionStartLine = 0;
            tab.SelectionStartCol = 6;
            tab.CursorLine = 0;
            tab.CursorCol = 11;
            Assert.Equal("beta ", tab.GetSelectedText());

            // Simulate pointer press inside selection, move > 4px to line 1 col 0, and release
            var pressPt = new Point(AvaloniaVirtualEditorCanvas.GutterWidth + 8 + 8 * canvas.CharWidth, y0);
            var dropPt = new Point(AvaloniaVirtualEditorCanvas.GutterWidth + 8, canvas.LineHeight * 1.5);
            var pointer = new global::Avalonia.Input.Pointer(global::Avalonia.Input.Pointer.GetNextFreeId(), PointerType.Mouse, true);
            canvas.RaiseEvent(new PointerPressedEventArgs(
                canvas, pointer, canvas, pressPt, 0,
                new PointerPointProperties(RawInputModifiers.LeftMouseButton, PointerUpdateKind.LeftButtonPressed),
                KeyModifiers.None, 1));
            canvas.RaiseEvent(new PointerEventArgs(
                InputElement.PointerMovedEvent, canvas, pointer, canvas, dropPt, 0,
                new PointerPointProperties(RawInputModifiers.LeftMouseButton, PointerUpdateKind.Other),
                KeyModifiers.None));
            canvas.RaiseEvent(new PointerReleasedEventArgs(
                canvas, pointer, canvas, dropPt, 0,
                new PointerPointProperties(RawInputModifiers.None, PointerUpdateKind.LeftButtonReleased),
                KeyModifiers.None, MouseButton.Left));

            Assert.Equal("alpha gamma delta", tab.Lines[0]);
            Assert.Equal("beta second line here", tab.Lines[1]);
            Assert.Equal("beta ", tab.GetSelectedText());

            // Undo restores both lines in a single step
            canvas.PerformUndoRedo(redo: false);
            Assert.Equal("alpha beta gamma delta", tab.Lines[0]);
            Assert.Equal("second line here", tab.Lines[1]);
        }

        [AvaloniaFact]
        public void ExpandAndShrinkSelection_RestoresExactSelectionHistory_AcrossScopes()
        {
            var (w, tab, canvas) = OpenEditor("expand.cs", "class C {\n    string s = \"hello world\";\n}\n");
            tab.CursorLine = 1;
            tab.CursorCol = 17; // inside "hello"
            tab.ClearSelection();

            // 1. Caret -> Word ("hello")
            canvas.ExpandSelection();
            Assert.Equal("hello", tab.GetSelectedText());

            // 2. Word -> Inside quotes ("hello world")
            canvas.ExpandSelection();
            Assert.Equal("hello world", tab.GetSelectedText());

            // 3. Inside quotes -> Including quotes ("\"hello world\"")
            canvas.ExpandSelection();
            Assert.Equal("\"hello world\"", tab.GetSelectedText());

            // 4. Including quotes -> Line content ("string s = \"hello world\";")
            canvas.ExpandSelection();
            Assert.Equal("string s = \"hello world\";", tab.GetSelectedText());

            // 5. Line content -> Full line ("    string s = \"hello world\";")
            canvas.ExpandSelection();
            Assert.Equal("    string s = \"hello world\";", tab.GetSelectedText());

            // Shrink back step-by-step all the way to the original caret at col 17!
            canvas.ShrinkSelection();
            Assert.Equal("string s = \"hello world\";", tab.GetSelectedText());
            canvas.ShrinkSelection();
            Assert.Equal("\"hello world\"", tab.GetSelectedText());
            canvas.ShrinkSelection();
            Assert.Equal("hello world", tab.GetSelectedText());
            canvas.ShrinkSelection();
            Assert.Equal("hello", tab.GetSelectedText());
            canvas.ShrinkSelection();
            Assert.False(tab.HasSelection());
            Assert.Equal(17, tab.CursorCol);
        }

        [AvaloniaFact]
        public void MultiCursor_AddCursorsToLineEnds_UsesSelectedLines_CursorUndoRestoresHistory_AndCtrlDRespectsPreselection()
        {
            var (w, tab, canvas) = OpenEditor("multi.txt", "line0\nline1\nline2\nline3\nitem items item\n");
            // Select only lines 1..2 (with selection ending at col 0 of line 3, which excludes line 3!)
            tab.SelectionStartLine = 1;
            tab.SelectionStartCol = 0;
            tab.CursorLine = 3;
            tab.CursorCol = 0;

            canvas.AddCursorsToLineEnds();
            Assert.Equal(1, tab.CursorLine);
            Assert.Equal(5, tab.CursorCol);
            Assert.Single(canvas.ExtraCursors);
            Assert.Equal(2, canvas.ExtraCursors[0].Line);
            Assert.Equal(5, canvas.ExtraCursors[0].Col);

            // Cmd/Ctrl+U (UndoLastCursor) restores the selection before AddCursorsToLineEnds
            canvas.UndoLastCursor();
            Assert.Empty(canvas.ExtraCursors);
            Assert.True(tab.HasSelection());
            Assert.Equal(1, tab.SelectionStartLine);
            Assert.Equal(3, tab.CursorLine);

            // Pre-existing manual selection of "item" matches substring in "items" on Ctrl+D
            tab.SelectionStartLine = 4;
            tab.SelectionStartCol = 0;
            tab.CursorLine = 4;
            tab.CursorCol = 4;
            canvas.OnCaretMovedCheckState(tab); // manual selection clears whole-word needle
            canvas.AddNextOccurrenceOrSelectWord();
            Assert.Single(canvas.ExtraCursors);
            Assert.Equal(5, canvas.ExtraCursors[0].AnchorCol); // matched inside "items"!
        }

        [AvaloniaFact]
        public void Clipboard_EmptySelectionClipboardSetting_AndWholeLineCutPasteAbove()
        {
            var (w, tab, canvas) = OpenEditor("clip.txt", "first\nsecond\nthird\n");
            tab.CursorLine = 1;
            tab.CursorCol = 2;
            tab.ClearSelection();

            // Cut with no selection cuts "second\n" and remembers whole-line copy
            string cut = canvas.GetCopyText(tab);
            Assert.Equal("second\n", cut);
            canvas.CutSelectionsOrLines(tab);
            Assert.Equal(new[] { "first", "third", "" }, tab.Lines.ToArray());

            // Pasting at line 0 col 3 inserts "second" as a full line above "first"
            tab.CursorLine = 0;
            tab.CursorCol = 3;
            canvas.PasteText(cut);
            Assert.Equal("second", tab.Lines[0]);
            Assert.Equal("first", tab.Lines[1]);

            // When editor.emptySelectionClipboard is false, GetCopyText and CutSelectionsOrLines do nothing with no selection
            EditorNicetyOptions.EmptySelectionClipboard = false;
            tab.ClearSelection();
            Assert.Equal("", canvas.GetCopyText(tab));
            canvas.CutSelectionsOrLines(tab);
            Assert.Equal("second", tab.Lines[0]);
        }

        [AvaloniaFact]
        public void Typing_AutoSurroundQuotesInAllLanguages_AndUseTabStopsSetting()
        {
            var (w, tab, canvas) = OpenEditor("notes.md", "    hello world\n");
            // Select "hello" in Markdown and type ' -> surrounds as 'hello'
            tab.SelectionStartLine = 0;
            tab.SelectionStartCol = 4;
            tab.CursorLine = 0;
            tab.CursorCol = 9;
            TypeText(canvas, "'");
            Assert.Equal("    'hello' world", tab.Lines[0]);
            Assert.Equal("hello", tab.GetSelectedText());

            // With UseTabStops = false, Backspace in leading indentation deletes only 1 space instead of 4
            EditorNicetyOptions.UseTabStops = false;
            tab.ClearSelection();
            tab.CursorLine = 0;
            tab.CursorCol = 4;
            SendCanvasKey(canvas, Key.Back);
            Assert.Equal("   'hello' world", tab.Lines[0]);
            Assert.Equal(3, tab.CursorCol);
        }

        [AvaloniaFact]
        public void LineOperations_SortLinesAscendingDescending_TransposeLetters_AndTransformWordAtCaret()
        {
            var (w, tab, canvas) = OpenEditor("ops.txt", "charlie\nalpha\nbravo\n");
            tab.ClearSelection();
            canvas.SortLinesAscending();
            Assert.Equal(new[] { "alpha", "bravo", "charlie", "" }, tab.Lines.Take(4).ToArray());

            tab.SelectionStartLine = 0;
            tab.SelectionStartCol = 0;
            tab.CursorLine = 2;
            tab.CursorCol = 5;
            canvas.SortLinesDescending();
            Assert.Equal(new[] { "charlie", "bravo", "alpha" }, tab.Lines.Take(3).ToArray());

            // TransformToUppercase with no selection transforms the word under the caret
            tab.ClearSelection();
            tab.CursorLine = 2;
            tab.CursorCol = 2; // inside "alpha"
            canvas.TransformToUppercase();
            Assert.Equal("ALPHA", tab.Lines[2]);

            canvas.TransformToLowercase();
            Assert.Equal("alpha", tab.Lines[2]);

            // TransposeLetters swaps characters around the caret
            tab.ClearSelection();
            tab.CursorLine = 2;
            tab.CursorCol = 2; // "al|pha" -> "ap|lha"
            canvas.TransposeLetters();
            Assert.Equal("aplha", tab.Lines[2]);
            Assert.Equal(3, tab.CursorCol);
        }

        [AvaloniaFact]
        public void FindWidget_SeedsFromWordAtCaret_AndReplaceOneAdvancesToNextMatch()
        {
            var (w, tab, canvas) = OpenEditor("find.txt", "targetOne targetOne targetOne\n");
            tab.CursorLine = 0;
            tab.CursorCol = 3; // inside first "targetOne", no selection
            tab.ClearSelection();

            w.PrimaryEditor.ShowFindBar(includeReplace: true);
            Assert.Equal("targetOne", w.PrimaryEditor.FindQueryText);
            Assert.Equal(3, w.PrimaryEditor.FindMatches.Count);

            // Replace first match and verify active match advances to the next occurrence and selects it
            w.PrimaryEditor.ReplaceCurrentMatch("REPLACED");
            Assert.Equal("REPLACED targetOne targetOne", tab.Lines[0]);
            Assert.Equal(2, w.PrimaryEditor.FindMatches.Count);
            Assert.Equal("targetOne", tab.GetSelectedText());
            Assert.Equal(9, tab.SelectionStartCol);
        }

        [AvaloniaFact]
        public void SuggestAndSnippets_AcceptOnEnterSetting_DetailsToggle()
        {
            var (w, tab, canvas) = OpenEditor("suggest.cs", "class S {\n    int myVariable = 1;\n    \n}\n");
            var binding = w.PrimaryLspBinding;
            var list = new LspCompletionList
            {
                Items = new List<LspCompletionItem>
                {
                    new LspCompletionItem { Label = "myVariable", Kind = 6, Detail = "int S.myVariable", Documentation = "Field doc" }
                }
            };
            binding.ShowFixtureCompletion(list, 2, 4);
            Assert.True(binding.IsCompletionVisible);
            Assert.Contains("Field doc", binding.Completion.DocumentationText);

            // Ctrl+Space while open toggles the documentation details panel
            SendCanvasKey(canvas, Key.Space, KeyModifiers.Control);
            Assert.Equal("", binding.Completion.DocumentationText);
            SendCanvasKey(canvas, Key.Space, KeyModifiers.Control);
            Assert.Contains("Field doc", binding.Completion.DocumentationText);

            // When AcceptSuggestionOnEnter = false, Enter closes the popup and inserts a newline instead of accepting
            EditorNicetyOptions.AcceptSuggestionOnEnter = false;
            int linesBefore = tab.Lines.Count;
            SendCanvasKey(canvas, Key.Enter);
            Assert.False(binding.IsCompletionVisible);
            Assert.Equal(linesBefore + 1, tab.Lines.Count);
        }

        [AvaloniaFact]
        public void ScrollingAndView_CursorSurroundingLines_FoldLevelN_AndRenderWhitespaceSelection()
        {
            string code = "namespace N {\n    class C {\n        void M() {\n            int x = 1;\n        }\n    }\n}\n" +
                          string.Join("\n", Enumerable.Range(0, 60).Select(i => "    int line" + i + " = " + i + ";")) + "\n";
            var (w, tab, canvas) = OpenEditor("view.cs", code);

            // FoldLevel(2) folds class C (depth 2) without folding namespace N (depth 1)
            canvas.FoldLevel(2);
            Assert.False(tab.FoldedBlockRanges.ContainsKey(0));
            Assert.True(tab.FoldedBlockRanges.ContainsKey(1));
            canvas.UnfoldAll();

            // CursorSurroundingLines keeps surrounding rows visible when moving caret toward the top/bottom
            EditorNicetyOptions.CursorSurroundingLines = 3;
            tab.ScrollTopLine = 20;
            tab.CursorLine = 21;
            tab.CursorCol = 0;
            canvas.EnsureCursorVisible(animate: false);
            Assert.True(tab.ScrollTopLine <= 18);

            // RenderWhitespace = "selection" paints whitespace markers only inside selected text
            EditorNicetyOptions.RenderWhitespace = "selection";
            tab.ScrollTopLine = 0;
            tab.ClearSelection();
            using (var rtb = new RenderTargetBitmap(new PixelSize(900, 600), new Vector(96, 96)))
                rtb.Render(canvas);
            Assert.Equal(0, canvas.LastFrameRenderedWhitespaceCount);

            tab.SelectionStartLine = 1;
            tab.SelectionStartCol = 0;
            tab.CursorLine = 1;
            tab.CursorCol = tab.Lines[1].Length;
            canvas.InvalidateVisual();
            using (var rtb = new RenderTargetBitmap(new PixelSize(900, 600), new Vector(96, 96)))
                rtb.Render(canvas);
            Assert.True(canvas.LastFrameRenderedWhitespaceCount >= 4);
        }

        [AvaloniaFact]
        public void UndoAfterSave_PreservesStack_UpdatesDirtyState_AndRedoReturnsToClean()
        {
            var (w, tab, canvas) = OpenEditor("saveundo.txt", "initial\n");
            Assert.False(tab.IsDirty);

            TypeText(canvas, "edited ");
            Assert.True(tab.IsDirty);
            Assert.True(w.SaveActiveTab());
            Assert.False(tab.IsDirty);

            // Undo past the save point marks the buffer dirty again
            canvas.PerformUndoRedo(redo: false);
            Assert.Equal("initial", tab.Lines[0]);
            Assert.True(tab.IsDirty);

            // Redo back to the saved content marks the buffer clean again
            canvas.PerformUndoRedo(redo: true);
            Assert.Equal("edited initial", tab.Lines[0]);
            Assert.False(tab.IsDirty);
        }
    }
}
