using System;
using System.Collections.Generic;

namespace codeLess.Core
{
    public enum WhenNodeType
    {
        Variable,
        Value,
        Not,
        And,
        Or,
        Eq,
        NotEq
    }

    public class WhenNode
    {
        public WhenNodeType Type { get; set; }
        public string Text { get; set; }
        public WhenNode Left { get; set; }
        public WhenNode Right { get; set; }
    }

    public static class WhenParser
    {
        public static WhenNode Parse(string expr)
        {
            if (string.IsNullOrEmpty(expr)) return null;
            int pos = 0;
            return ParseOr(expr, ref pos);
        }

        private static void SkipWhitespace(string expr, ref int pos)
        {
            while (pos < expr.Length && char.IsWhiteSpace(expr[pos])) pos++;
        }

        private static bool Match(string expr, ref int pos, string token)
        {
            SkipWhitespace(expr, ref pos);
            if (pos + token.Length <= expr.Length && expr.Substring(pos, token.Length) == token)
            {
                pos += token.Length;
                return true;
            }
            return false;
        }

        private static WhenNode ParseOr(string expr, ref int pos)
        {
            WhenNode left = ParseAnd(expr, ref pos);
            while (Match(expr, ref pos, "||"))
            {
                WhenNode right = ParseAnd(expr, ref pos);
                WhenNode parent = new WhenNode { Type = WhenNodeType.Or, Left = left, Right = right };
                left = parent;
            }
            return left;
        }

        private static WhenNode ParseAnd(string expr, ref int pos)
        {
            WhenNode left = ParseEq(expr, ref pos);
            while (Match(expr, ref pos, "&&"))
            {
                WhenNode right = ParseEq(expr, ref pos);
                WhenNode parent = new WhenNode { Type = WhenNodeType.And, Left = left, Right = right };
                left = parent;
            }
            return left;
        }

        private static WhenNode ParseEq(string expr, ref int pos)
        {
            WhenNode left = ParseUnary(expr, ref pos);
            SkipWhitespace(expr, ref pos);
            if (Match(expr, ref pos, "=="))
            {
                WhenNode right = ParseUnary(expr, ref pos);
                return new WhenNode { Type = WhenNodeType.Eq, Left = left, Right = right };
            }
            if (Match(expr, ref pos, "!="))
            {
                WhenNode right = ParseUnary(expr, ref pos);
                return new WhenNode { Type = WhenNodeType.NotEq, Left = left, Right = right };
            }
            return left;
        }

        private static WhenNode ParseUnary(string expr, ref int pos)
        {
            SkipWhitespace(expr, ref pos);
            if (Match(expr, ref pos, "!"))
            {
                WhenNode inner = ParseUnary(expr, ref pos);
                return new WhenNode { Type = WhenNodeType.Not, Left = inner };
            }
            return ParsePrimary(expr, ref pos);
        }

        private static WhenNode ParsePrimary(string expr, ref int pos)
        {
            SkipWhitespace(expr, ref pos);
            if (pos >= expr.Length) return null;

            if (Match(expr, ref pos, "("))
            {
                WhenNode node = ParseOr(expr, ref pos);
                Match(expr, ref pos, ")");
                return node;
            }

            char c = expr[pos];
            if (c == '\'' || c == '"')
            {
                char q = expr[pos++];
                int start = pos;
                while (pos < expr.Length && expr[pos] != q) pos++;
                string val = expr.Substring(start, pos - start);
                if (pos < expr.Length) pos++;
                return new WhenNode { Type = WhenNodeType.Value, Text = val };
            }

            int identStart = pos;
            while (pos < expr.Length && (char.IsLetterOrDigit(expr[pos]) || expr[pos] == '_' || expr[pos] == '-')) pos++;
            string ident = expr.Substring(identStart, pos - identStart);

            if (ident == "true" || ident == "false")
                return new WhenNode { Type = WhenNodeType.Value, Text = ident };

            return new WhenNode { Type = WhenNodeType.Variable, Text = ident };
        }

        public static bool Evaluate(WhenNode node, IWhenContext ctx)
        {
            if (node == null) return true;
            switch (node.Type)
            {
                case WhenNodeType.And: return Evaluate(node.Left, ctx) && Evaluate(node.Right, ctx);
                case WhenNodeType.Or: return Evaluate(node.Left, ctx) || Evaluate(node.Right, ctx);
                case WhenNodeType.Not: return !Evaluate(node.Left, ctx);
                case WhenNodeType.Eq: return GetString(node.Left, ctx) == GetString(node.Right, ctx);
                case WhenNodeType.NotEq: return GetString(node.Left, ctx) != GetString(node.Right, ctx);
                case WhenNodeType.Variable:
                    string val = GetString(node, ctx);
                    if (string.Equals(val, "true", StringComparison.OrdinalIgnoreCase)) return true;
                    if (string.Equals(val, "false", StringComparison.OrdinalIgnoreCase)) return false;
                    return !string.IsNullOrEmpty(val); 
                case WhenNodeType.Value:
                    return string.Equals(node.Text, "true", StringComparison.OrdinalIgnoreCase);
                default: return false;
            }
        }

        private static string GetString(WhenNode node, IWhenContext ctx)
        {
            if (node == null) return "";
            if (node.Type == WhenNodeType.Value) return node.Text;
            if (node.Type == WhenNodeType.Variable) return ctx.GetValue(node.Text) ? "true" : "false";
            return Evaluate(node, ctx) ? "true" : "false";
        }
    }
}
