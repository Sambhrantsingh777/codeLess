using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.Text;

namespace codeLess.Studio
{
    public class MdRtfLink
    {
        public string Text = "";
        public string Url = "";
    }

    public class MdRtfAnchor
    {
        public int SourceLine;
        /// <summary>Visible text at the start of the block (located with RichTextBox.Find for scroll sync).</summary>
        public string Text = "";
        public string HeadingAnchor = "";
    }

    public class MarkdownRtfResult
    {
        public string Rtf = "";
        public List<MdRtfLink> Links = new List<MdRtfLink>();
        public List<MdRtfAnchor> Anchors = new List<MdRtfAnchor>();
    }

    public class MarkdownRtfColors
    {
        public Color Fg, Muted, Accent, CodeBg, Border, String, Keyword, Type, Function, Comment, Number, Operator, Control;

        public static MarkdownRtfColors FromTheme(StudioTheme t)
        {
            MarkdownRtfColors c = new MarkdownRtfColors();
            c.Fg = t.FgPrimary;
            c.Muted = t.FgSecondary;
            c.Accent = t.AccentPrimary;
            c.CodeBg = t.IsLight ? t.BgSidebar : t.BgWorkbench;
            c.Border = t.BorderSubtle;
            c.String = t.TokenString;
            c.Keyword = t.TokenKeyword;
            c.Type = t.TokenType;
            c.Function = t.TokenFunction;
            c.Comment = t.TokenComment;
            c.Number = t.TokenNumber;
            c.Operator = t.TokenOperator;
            c.Control = t.TokenControl;
            return c;
        }
    }

    /// <summary>
    /// Renders a parsed Markdown document as RTF for the native Windows preview (RichEdit; no browser engine). Code fences
    /// are colored with <see cref="SimpleCodeTokenizer"/>; local PNG/JPEG images are embedded. C# 5.
    /// </summary>
    public static class MarkdownRtfWriter
    {
        private const int CFg = 1, CMuted = 2, CAccent = 3, CCodeBg = 4, CBorder = 5, CString = 6, CKeyword = 7, CType = 8, CFunction = 9, CComment = 10, CNumber = 11, COperator = 12, CControl = 13;
        private const int BodySize = 21; // half-points

        public static MarkdownRtfResult Build(MdDocument doc, MarkdownRtfColors colors, Func<string, byte[]> imageLoader)
        {
            MarkdownRtfResult res = new MarkdownRtfResult();
            StringBuilder sb = new StringBuilder();
            sb.Append(@"{\rtf1\ansi\ansicpg1252\deff0\uc1{\fonttbl{\f0\fswiss Segoe UI;}{\f1\fmodern Consolas;}{\f2\fnil Segoe UI Symbol;}}");
            sb.Append(@"{\colortbl;");
            Color[] table = new Color[] { colors.Fg, colors.Muted, colors.Accent, colors.CodeBg, colors.Border, colors.String, colors.Keyword, colors.Type, colors.Function, colors.Comment, colors.Number, colors.Operator, colors.Control };
            foreach (Color c in table)
                sb.Append(@"\red").Append(c.R).Append(@"\green").Append(c.G).Append(@"\blue").Append(c.B).Append(';');
            sb.Append('}');
            sb.Append(@"\viewkind4\f0\fs").Append(BodySize).Append(@"\cf1 ");
            if (doc != null) foreach (MdBlock b in doc.Blocks) WriteBlock(sb, b, 0, res, imageLoader, false);
            sb.Append('}');
            res.Rtf = sb.ToString();
            return res;
        }

        private static void Anchor(MarkdownRtfResult res, MdBlock b, string text)
        {
            MdRtfAnchor a = new MdRtfAnchor();
            a.SourceLine = b.SourceLine;
            string t = (text ?? "").Replace("\n", " ").Trim();
            a.Text = t.Length > 40 ? t.Substring(0, 40) : t;
            a.HeadingAnchor = b.Kind == MdBlockKind.Heading ? b.Anchor : "";
            if (a.Text.Length >= 3) res.Anchors.Add(a);
        }

        private static void WriteBlock(StringBuilder sb, MdBlock b, int indentTwips, MarkdownRtfResult res, Func<string, byte[]> imageLoader, bool muted)
        {
            string pard = @"\pard\li" + indentTwips.ToString(CultureInfo.InvariantCulture) + @"\ri120";
            switch (b.Kind)
            {
                case MdBlockKind.Heading:
                    {
                        int[] sizes = { 44, 36, 30, 26, 23, 21 };
                        int size = sizes[Math.Max(1, Math.Min(6, b.Level)) - 1];
                        Anchor(res, b, MarkdownParser.ToPlainText(b.Inlines));
                        sb.Append(pard).Append(@"\sb240\sa100\b\fs").Append(size).Append(@"\cf1 ");
                        WriteInlines(sb, b.Inlines, res, imageLoader, false, false, false, muted);
                        sb.Append(@"\b0\fs").Append(BodySize).Append(@"\par").Append('\n');
                        if (b.Level <= 2) Rule(sb, indentTwips);
                        break;
                    }
                case MdBlockKind.Paragraph:
                    Anchor(res, b, MarkdownParser.ToPlainText(b.Inlines));
                    sb.Append(pard).Append(@"\sa140\sl300\slmult1").Append(muted ? @"\cf2 " : @"\cf1 ");
                    WriteInlines(sb, b.Inlines, res, imageLoader, false, false, false, muted);
                    sb.Append(@"\par").Append('\n');
                    break;
                case MdBlockKind.CodeBlock:
                    {
                        string[] lines = (b.Code ?? "").Split('\n');
                        bool hash = SimpleCodeTokenizer.UsesHashComments(b.Language) && b.Language.Length > 0;
                        Anchor(res, b, lines.Length > 0 ? lines[0] : "");
                        for (int i = 0; i < lines.Length; i++)
                        {
                            sb.Append(pard).Append(@"\li").Append(indentTwips + 240).Append(i == lines.Length - 1 ? @"\sa160" : @"\sa0").Append(i == 0 ? @"\sb60" : "").Append(@"\f1\fs19\highlight4 ");
                            string line = lines[i].Replace("\t", "    ");
                            int pos = 0;
                            foreach (CodeToken tk in SimpleCodeTokenizer.Tokenize(line, hash))
                            {
                                if (tk.Start > pos) AppendText(sb, line.Substring(pos, tk.Start - pos));
                                sb.Append(@"{\cf").Append(ColorFor(tk.Kind)).Append(tk.Kind == CodeTokenKind.Comment ? @"\i " : " ");
                                AppendText(sb, line.Substring(tk.Start, tk.Length));
                                sb.Append('}');
                                pos = tk.Start + tk.Length;
                            }
                            if (pos < line.Length) AppendText(sb, line.Substring(pos));
                            sb.Append(' ');
                            sb.Append(@"\highlight0\f0\fs").Append(BodySize).Append(@"\par").Append('\n');
                        }
                        break;
                    }
                case MdBlockKind.HorizontalRule:
                    Rule(sb, indentTwips);
                    break;
                case MdBlockKind.BlockQuote:
                    foreach (MdBlock c in b.Children) WriteBlock(sb, c, indentTwips + 360, res, imageLoader, true);
                    break;
                case MdBlockKind.List:
                    {
                        int num = b.StartNumber;
                        int li = indentTwips + 420;
                        foreach (MdListItem item in b.Items)
                        {
                            string marker = item.IsTask ? (item.IsChecked ? "☑" : "☐") : (b.Ordered ? (num++).ToString(CultureInfo.InvariantCulture) + "." : (indentTwips == 0 ? "•" : "◦"));
                            bool first = true;
                            if (item.Blocks.Count == 0)
                            {
                                sb.Append(@"\pard\li").Append(li).Append(@"\fi-300\sa60\cf2 ");
                                AppendText(sb, marker);
                                sb.Append(@"\cf1\par").Append('\n');
                            }
                            foreach (MdBlock ib in item.Blocks)
                            {
                                if (first && ib.Kind == MdBlockKind.Paragraph)
                                {
                                    Anchor(res, ib, MarkdownParser.ToPlainText(ib.Inlines));
                                    sb.Append(@"\pard\li").Append(li).Append(@"\fi-300\sa60\tx").Append(li).Append(item.IsTask && item.IsChecked ? @"\cf3 " : @"\cf2 ");
                                    AppendText(sb, marker);
                                    sb.Append(@"\tab").Append(muted ? @"\cf2 " : @"\cf1 ");
                                    WriteInlines(sb, ib.Inlines, res, imageLoader, false, false, false, muted);
                                    sb.Append(@"\par").Append('\n');
                                }
                                else
                                {
                                    if (first)
                                    {
                                        sb.Append(@"\pard\li").Append(li).Append(@"\fi-300\sa0\cf2 ");
                                        AppendText(sb, marker);
                                        sb.Append(@"\cf1\par").Append('\n');
                                    }
                                    WriteBlock(sb, ib, li, res, imageLoader, muted);
                                }
                                first = false;
                            }
                        }
                        sb.Append(@"\pard\sa80\fs8\par\fs").Append(BodySize).Append('\n');
                        break;
                    }
                case MdBlockKind.Table:
                    WriteTable(sb, b, indentTwips, res, imageLoader);
                    break;
            }
        }

        private static void Rule(StringBuilder sb, int indentTwips)
        {
            sb.Append(@"\pard\li").Append(indentTwips).Append(@"\sa120\cf5\fs12 ");
            AppendText(sb, new string('\u2500', 80));
            sb.Append(@"\cf1\fs").Append(BodySize).Append(@"\par").Append('\n');
        }

        private static void WriteTable(StringBuilder sb, MdBlock b, int indentTwips, MarkdownRtfResult res, Func<string, byte[]> imageLoader)
        {
            int cols = b.TableHeader.Count;
            foreach (List<List<MdInline>> r in b.TableRows) cols = Math.Max(cols, r.Count);
            if (cols == 0) return;
            int[] widths = new int[cols];
            for (int c = 0; c < cols; c++)
            {
                int maxLen = c < b.TableHeader.Count ? MarkdownParser.ToPlainText(b.TableHeader[c]).Length : 0;
                foreach (List<List<MdInline>> r in b.TableRows) if (c < r.Count) maxLen = Math.Max(maxLen, MarkdownParser.ToPlainText(r[c]).Length);
                widths[c] = Math.Max(900, Math.Min(4200, maxLen * 120 + 360));
            }
            MdRtfAnchor a = new MdRtfAnchor();
            a.SourceLine = b.SourceLine;
            for (int r = 0; r <= b.TableRows.Count; r++)
            {
                List<List<MdInline>> cells = r == 0 ? b.TableHeader : b.TableRows[r - 1];
                sb.Append(@"\trowd\trgaph108\trleft").Append(indentTwips);
                int x = indentTwips;
                for (int c = 0; c < cols; c++)
                {
                    x += widths[c];
                    sb.Append(@"\clbrdrt\brdrs\brdrw10\brdrcf5\clbrdrl\brdrs\brdrw10\brdrcf5\clbrdrb\brdrs\brdrw10\brdrcf5\clbrdrr\brdrs\brdrw10\brdrcf5");
                    if (r == 0) sb.Append(@"\clcbpat4");
                    sb.Append(@"\cellx").Append(x);
                }
                for (int c = 0; c < cols; c++)
                {
                    MdAlign al = c < b.TableAlign.Count ? b.TableAlign[c] : MdAlign.None;
                    sb.Append(@"\pard\intbl").Append(al == MdAlign.Right ? @"\qr" : (al == MdAlign.Center ? @"\qc" : @"\ql")).Append(r == 0 ? @"\b " : " ");
                    if (c < cells.Count) WriteInlines(sb, cells[c], res, imageLoader, false, false, false, false);
                    sb.Append(r == 0 ? @"\b0\cell" : @"\cell");
                }
                sb.Append(@"\row").Append('\n');
            }
            sb.Append(@"\pard\sa120\par").Append('\n');
        }

        private static int ColorFor(CodeTokenKind k)
        {
            switch (k)
            {
                case CodeTokenKind.Keyword: return CKeyword;
                case CodeTokenKind.Control: return CControl;
                case CodeTokenKind.Type: return CType;
                case CodeTokenKind.Function: return CFunction;
                case CodeTokenKind.String: return CString;
                case CodeTokenKind.Comment: return CComment;
                case CodeTokenKind.Number: return CNumber;
                case CodeTokenKind.Operator: return COperator;
                default: return CFg;
            }
        }

        private static void WriteInlines(StringBuilder sb, List<MdInline> inlines, MarkdownRtfResult res, Func<string, byte[]> imageLoader, bool bold, bool italic, bool strike, bool muted)
        {
            foreach (MdInline i in inlines)
            {
                switch (i.Kind)
                {
                    case MdInlineKind.Text:
                        AppendText(sb, i.Text);
                        break;
                    case MdInlineKind.Strong:
                        sb.Append(@"{\b ");
                        WriteInlines(sb, i.Children, res, imageLoader, true, italic, strike, muted);
                        sb.Append('}');
                        break;
                    case MdInlineKind.Emphasis:
                        sb.Append(@"{\i ");
                        WriteInlines(sb, i.Children, res, imageLoader, bold, true, strike, muted);
                        sb.Append('}');
                        break;
                    case MdInlineKind.Strikethrough:
                        sb.Append(@"{\strike ");
                        WriteInlines(sb, i.Children, res, imageLoader, bold, italic, true, muted);
                        sb.Append('}');
                        break;
                    case MdInlineKind.Code:
                        sb.Append(@"{\f1\fs19\highlight4\cf6 ");
                        AppendText(sb, i.Text);
                        sb.Append('}');
                        break;
                    case MdInlineKind.LineBreak:
                        sb.Append(@"\line ");
                        break;
                    case MdInlineKind.Link:
                        {
                            string text = MarkdownParser.ToPlainText(i.Children);
                            if (text.Length == 0) text = i.Url;
                            MdRtfLink l = new MdRtfLink();
                            l.Text = text;
                            l.Url = i.Url;
                            res.Links.Add(l);
                            sb.Append(@"{\cf3\ul ");
                            AppendText(sb, text);
                            sb.Append('}');
                            break;
                        }
                    case MdInlineKind.Image:
                        {
                            byte[] data = imageLoader != null ? imageLoader(i.Url) : null;
                            if (data != null && AppendPicture(sb, data)) break;
                            sb.Append(@"{\cf2 [");
                            AppendText(sb, string.IsNullOrEmpty(i.Text) ? "image" : i.Text);
                            sb.Append(@"]}");
                            break;
                        }
                }
            }
        }

        /// <summary>Embeds a PNG or JPEG (scaled to at most 640 px wide). Returns false for other formats.</summary>
        public static bool AppendPicture(StringBuilder sb, byte[] data)
        {
            int w, h;
            string blip;
            if (!TryGetImageSize(data, out w, out h, out blip) || w <= 0 || h <= 0) return false;
            double scale = w > 640 ? 640.0 / w : 1.0;
            int gw = (int)(w * scale * 15), gh = (int)(h * scale * 15);
            sb.Append(@"{\pict\").Append(blip).Append(@"\picw").Append(w).Append(@"\pich").Append(h)
              .Append(@"\picwgoal").Append(gw).Append(@"\pichgoal").Append(gh).Append('\n');
            const string hex = "0123456789abcdef";
            for (int i = 0; i < data.Length; i++)
            {
                sb.Append(hex[data[i] >> 4]).Append(hex[data[i] & 15]);
                if ((i & 63) == 63) sb.Append('\n');
            }
            sb.Append('}');
            return true;
        }

        public static bool TryGetImageSize(byte[] d, out int w, out int h, out string blip)
        {
            w = h = 0;
            blip = "";
            if (d == null || d.Length < 24) return false;
            if (d[0] == 0x89 && d[1] == 0x50 && d[2] == 0x4E && d[3] == 0x47)
            {
                w = (d[16] << 24) | (d[17] << 16) | (d[18] << 8) | d[19];
                h = (d[20] << 24) | (d[21] << 16) | (d[22] << 8) | d[23];
                blip = "pngblip";
                return true;
            }
            if (d[0] == 0xFF && d[1] == 0xD8)
            {
                int i = 2;
                while (i + 9 < d.Length)
                {
                    if (d[i] != 0xFF) { i++; continue; }
                    byte marker = d[i + 1];
                    int len = (d[i + 2] << 8) | d[i + 3];
                    if (marker >= 0xC0 && marker <= 0xCF && marker != 0xC4 && marker != 0xC8 && marker != 0xCC)
                    {
                        h = (d[i + 5] << 8) | d[i + 6];
                        w = (d[i + 7] << 8) | d[i + 8];
                        blip = "jpegblip";
                        return true;
                    }
                    i += 2 + len;
                }
            }
            return false;
        }

        /// <summary>Escapes text for RTF (\, {, } and non-ASCII as \uN?).</summary>
        public static void AppendText(StringBuilder sb, string text)
        {
            if (string.IsNullOrEmpty(text)) return;
            foreach (char c in text)
            {
                if (c == '\\' || c == '{' || c == '}') sb.Append('\\').Append(c);
                else if (c == '\n') sb.Append(@"\line ");
                else if (c == '\t') sb.Append(@"\tab ");
                else if (c < 128) sb.Append(c);
                else sb.Append(@"\u").Append(((int)(short)c).ToString(CultureInfo.InvariantCulture)).Append('?');
            }
        }
    }
}
