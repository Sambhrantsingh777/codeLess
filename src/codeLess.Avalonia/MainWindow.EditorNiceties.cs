using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using codeLess.Studio;

namespace codeLess.AvaloniaApp
{
    /// <summary>Settings and command-palette wiring for the editor niceties (bracket colors, guides, sticky scroll, ...).</summary>
    public partial class MainWindow
    {
        /// <summary>Pushes the persisted switches into the editors and repaints them.</summary>
        internal void ApplyEditorNicetySettings()
        {
            EditorNicetyOptions.ApplyFrom(settings);
            PrimaryEditor?.Canvas?.InvalidateVisual();
            SplitEditor?.Canvas?.InvalidateVisual();
        }

        private void ToggleEditorNicety(string name, Func<bool> get, Action<bool> set)
        {
            bool v = !get();
            set(v);
            EditorNicetyOptions.CopyTo(settings);
            try { settings.Save(); } catch { }
            PrimaryEditor?.Canvas?.InvalidateVisual();
            SplitEditor?.Canvas?.InvalidateVisual();
            PrimaryEditor?.PublishStatus(name + (v ? ": on" : ": off"));
        }

        /// <summary>Screenshot-suite screens for the editor niceties (fixture files in the temp folder; nothing persisted).</summary>
        private async Task RunEditorNicetiesScreensAsync(string outputDir)
        {
            string dir = Path.Combine(Path.GetTempPath(), "codeLess_suite_niceties");
            Directory.CreateDirectory(dir);
            string cs = Path.Combine(dir, "OrderPipeline.cs");
            var sb = new System.Text.StringBuilder();
            sb.Append("using System;\nusing System.Collections.Generic;\nusing System.Linq;\n\nnamespace Acme.Orders\n{\n");
            sb.Append("    /// <summary>Validates, prices and dispatches incoming orders.</summary>\n");
            sb.Append("    public sealed class OrderPipeline : IOrderPipeline\n    {\n");
            sb.Append("        private readonly Dictionary<string, List<Order>> pending = new Dictionary<string, List<Order>>();\n\n");
            sb.Append("        public IReadOnlyList<Order> Process(IEnumerable<Order> orders, decimal discount)\n        {\n");
            sb.Append("            var accepted = new List<Order>();\n");
            sb.Append("            foreach (var order in orders.Where(o => o.Lines.Count > 0))\n            {\n");
            for (int i = 0; i < 14; i++)
                sb.Append("                if (order.Lines[" + i + "].Price > Limits[(int)order.Kind]) { accepted.Add(order); Log($\"accepted {order.Id} ({Math.Round(order.Total * discount, 2)})\"); }\n");
            sb.Append("                pending[order.Customer] = new List<Order> { order }; // \"(not a bracket)\" in strings and comments: ) ] }\n");
            sb.Append("            }\n            return accepted;\n        }\n    }\n}\n");
            File.WriteAllText(cs, sb.ToString());
            string tsx = Path.Combine(dir, "OrderList.tsx");
            File.WriteAllText(tsx,
                "import React, { useMemo, useState } from 'react';\n\n" +
                "type Props = { orders: Order[]; onSelect?: (id: string) => void };\n\n" +
                "export function OrderList({ orders, onSelect }: Props) {\n" +
                "  const [query, setQuery] = useState('');\n" +
                "  const visible = useMemo(() => orders.filter(o => o.title.includes(query)), [orders, query]);\n" +
                "  return (\n" +
                "    <section className=\"orders\">\n" +
                "      <input value={query} onChange={e => setQuery(e.target.value)} placeholder=\"Search\" />\n" +
                "      <ul>\n" +
                "        {visible.map(o => (\n" +
                "          <li key={o.id} onClick={() => onSelect?.(o.id)}>{`#${o.id} ${o.title}`}</li>\n" +
                "        ))}\n" +
                "      </ul>\n" +
                "    </section>\n" +
                "  );\n" +
                "}\n");
            try
            {
                var tab = OpenFileInTab(cs, preview: false);
                if (tab != null)
                {
                    PrimaryEditor.Canvas.CompleteBackgroundTokenization(5000);
                    tab.ScrollTopLine = 17;
                    tab.CursorLine = 20;
                    tab.CursorCol = tab.Lines[20].IndexOf("accepted", StringComparison.Ordinal) + 3;
                    tab.ClearSelection();
                    PrimaryEditor.Canvas.FlushOccurrenceHighlight();
                    PrimaryEditor.Canvas.InvalidateVisual();
                    await CaptureScreenshotAsync(outputDir, "r12_niceties_csharp.png");
                    var light = StudioTheme.GetById("codeless-daylight");
                    if (light != null)
                    {
                        ApplyTheme(light, persistSetting: false);
                        PrimaryEditor.Canvas.FlushOccurrenceHighlight();
                        await CaptureScreenshotAsync(outputDir, "r12_niceties_light.png");
                        ApplyTheme(StudioTheme.GetById(StudioTheme.DefaultThemeId), persistSetting: false);
                    }
                }
                var t2 = OpenFileInTab(tsx, preview: false);
                if (t2 != null)
                {
                    PrimaryEditor.Canvas.CompleteBackgroundTokenization(5000);
                    t2.ScrollTopLine = 0;
                    t2.CursorLine = 11;
                    t2.CursorCol = 14;
                    t2.ClearSelection();
                    PrimaryEditor.Canvas.FlushOccurrenceHighlight();
                    await CaptureScreenshotAsync(outputDir, "r12_niceties_tsx.png");
                }
            }
            catch (Exception ex) { LogSuiteError(outputDir, ex); }
        }

        private List<PaletteEntryItem> BuildEditorNicetiesPaletteCommands()
        {
            return new List<PaletteEntryItem>
            {
                new PaletteEntryItem { Category = "Edit", Title = "Change All Occurrences", Shortcut = "Ctrl+F2", Execute = () => PrimaryEditor.Canvas.ChangeAllOccurrences() },
                new PaletteEntryItem { Category = "View", Title = "Toggle Sticky Scroll", Execute = () => ToggleEditorNicety("Sticky Scroll", () => EditorNicetyOptions.StickyScroll, v => EditorNicetyOptions.StickyScroll = v) },
                new PaletteEntryItem { Category = "View", Title = "Toggle Bracket Pair Colorization", Execute = () => ToggleEditorNicety("Bracket Pair Colorization", () => EditorNicetyOptions.BracketPairColorization, v => EditorNicetyOptions.BracketPairColorization = v) },
                new PaletteEntryItem { Category = "View", Title = "Toggle Bracket Pair Guides", Execute = () => ToggleEditorNicety("Bracket Pair Guides", () => EditorNicetyOptions.BracketPairGuides, v => EditorNicetyOptions.BracketPairGuides = v) },
                new PaletteEntryItem { Category = "View", Title = "Toggle Indent Guides", Execute = () => ToggleEditorNicety("Indent Guides", () => EditorNicetyOptions.IndentGuides, v => EditorNicetyOptions.IndentGuides = v) },
                new PaletteEntryItem { Category = "View", Title = "Toggle Occurrence Highlighting", Execute = () => ToggleEditorNicety("Occurrence Highlighting", () => EditorNicetyOptions.HighlightOccurrences, v => EditorNicetyOptions.HighlightOccurrences = v) },
                new PaletteEntryItem { Category = "View", Title = "Toggle TextMate Syntax Highlighting", Execute = () => ToggleEditorNicety("TextMate Highlighting", () => EditorNicetyOptions.TextMateHighlighting, v => EditorNicetyOptions.TextMateHighlighting = v) },
                new PaletteEntryItem { Category = "Edit", Title = "Toggle Auto Close Tags", Execute = () => ToggleEditorNicety("Auto Close Tags", () => EditorNicetyOptions.AutoCloseTags, v => EditorNicetyOptions.AutoCloseTags = v) },
                new PaletteEntryItem { Category = "Edit", Title = "Toggle Linked Tag Editing", Execute = () => ToggleEditorNicety("Linked Tag Editing", () => EditorNicetyOptions.LinkedTagEditing, v => EditorNicetyOptions.LinkedTagEditing = v) },
                new PaletteEntryItem { Category = "Edit", Title = "Toggle Emmet Expansion on Tab", Execute = () => ToggleEditorNicety("Emmet on Tab", () => EditorNicetyOptions.EmmetOnTab, v => EditorNicetyOptions.EmmetOnTab = v) },
            };
        }
    }

    /// <summary>"Editor" section of the Settings dialog: one check box per nicety (VS Code setting names in tooltips).</summary>
    internal static class EditorNicetiesSettingsPanel
    {
        public sealed class Result
        {
            public Control Panel;
            public Action<IdePersistentSettings> Apply;
        }

        public static Result Build(IdePersistentSettings s, StudioTheme theme, Color textPrimary)
        {
            var fg = new SolidColorBrush(textPrimary);
            CheckBox Chk(string text, bool value, string tip)
            {
                var c = new CheckBox { Content = text, IsChecked = value, Foreground = fg, FontSize = UiTokens.FontUi };
                ToolTip.SetTip(c, tip);
                return c;
            }
            var bracket = Chk("Bracket pair colorization", s.EditorBracketPairColorization, "editor.bracketPairColorization.enabled");
            var guides = Chk("Bracket pair guides", s.EditorBracketPairGuides, "editor.guides.bracketPairs (active)");
            var indent = Chk("Indent guides", s.EditorIndentGuides, "editor.guides.indentation");
            var sticky = Chk("Sticky scroll", s.EditorStickyScroll, "editor.stickyScroll.enabled");
            var occ = Chk("Highlight occurrences", s.EditorHighlightOccurrences, "editor.occurrencesHighlight");
            var tm = Chk("TextMate grammars", s.EditorTextMateHighlighting, "Bundled and VSIX-contributed TextMate grammars (built-in tokenizer otherwise)");
            var close = Chk("Auto close tags", s.EditorAutoCloseTags, "html.autoClosingTags");
            var linked = Chk("Linked tag rename", s.EditorLinkedTagEditing, "editor.linkedEditing");
            var emmet = Chk("Emmet on Tab", s.EditorEmmetOnTab, "emmet.triggerExpansionOnTab");

            var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,*,*"), RowDefinitions = new RowDefinitions("Auto,Auto,Auto"), Margin = new Thickness(0, 2, 0, 0) };
            var all = new Control[] { bracket, guides, indent, sticky, occ, tm, close, linked, emmet };
            for (int i = 0; i < all.Length; i++)
            {
                Grid.SetColumn(all[i], i % 3);
                Grid.SetRow(all[i], i / 3);
                grid.Children.Add(all[i]);
            }
            var panel = new StackPanel { Spacing = 2 };
            panel.Children.Add(WorkbenchStyles.SectionLabel("Editor", theme));
            panel.Children.Add(grid);
            return new Result
            {
                Panel = panel,
                Apply = st =>
                {
                    st.EditorBracketPairColorization = bracket.IsChecked == true;
                    st.EditorBracketPairGuides = guides.IsChecked == true;
                    st.EditorIndentGuides = indent.IsChecked == true;
                    st.EditorStickyScroll = sticky.IsChecked == true;
                    st.EditorHighlightOccurrences = occ.IsChecked == true;
                    st.EditorTextMateHighlighting = tm.IsChecked == true;
                    st.EditorAutoCloseTags = close.IsChecked == true;
                    st.EditorLinkedTagEditing = linked.IsChecked == true;
                    st.EditorEmmetOnTab = emmet.IsChecked == true;
                    EditorNicetyOptions.ApplyFrom(st);
                }
            };
        }
    }
}
