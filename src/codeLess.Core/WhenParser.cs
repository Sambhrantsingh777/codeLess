using System;
using System.Text.RegularExpressions;

namespace codeLess.Core
{
    public interface IContextKeyService
    {
        object GetValue(string key);
    }

    public enum WhenNodeType
    {
        Variable,
        Value,
        Not,
        And,
        Or,
        Eq,
        NotEq,
        Regex,
        In
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
            if (string.IsNullOrWhiteSpace(expr)) return null;
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
                if (right != null && right.Type == WhenNodeType.Variable) right.Type = WhenNodeType.Value;
                return new WhenNode { Type = WhenNodeType.Eq, Left = left, Right = right };
            }
            if (Match(expr, ref pos, "!="))
            {
                WhenNode right = ParseUnary(expr, ref pos);
                if (right != null && right.Type == WhenNodeType.Variable) right.Type = WhenNodeType.Value;
                return new WhenNode { Type = WhenNodeType.NotEq, Left = left, Right = right };
            }
            if (Match(expr, ref pos, "=~"))
            {
                WhenNode right = ParseUnary(expr, ref pos);
                if (right != null && right.Type == WhenNodeType.Variable) right.Type = WhenNodeType.Value;
                return new WhenNode { Type = WhenNodeType.Regex, Left = left, Right = right };
            }
            if (Match(expr, ref pos, "in"))
            {
                WhenNode right = ParseUnary(expr, ref pos);
                return new WhenNode { Type = WhenNodeType.In, Left = left, Right = right };
            }
            if (Match(expr, ref pos, "not in"))
            {
                WhenNode right = ParseUnary(expr, ref pos);
                return new WhenNode { Type = WhenNodeType.Not, Left = new WhenNode { Type = WhenNodeType.In, Left = left, Right = right } };
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
            if (c == '\'' || c == '"' || c == '/')
            {
                char q = expr[pos++];
                int start = pos;
                bool isRegex = (q == '/');
                
                while (pos < expr.Length && expr[pos] != q)
                {
                    if (expr[pos] == '\\' && pos + 1 < expr.Length) pos += 2;
                    else pos++;
                }
                string val = expr.Substring(start, pos - start);
                if (pos < expr.Length) pos++; // skip closing char
                
                // if regex, maybe flags
                if (isRegex)
                {
                    while (pos < expr.Length && char.IsLetter(expr[pos])) pos++;
                }
                
                // We don't unescape here, keep simple for this scope, or just rely on regex passing it raw
                return new WhenNode { Type = WhenNodeType.Value, Text = val };
            }

            int identStart = pos;
            while (pos < expr.Length && (char.IsLetterOrDigit(expr[pos]) || expr[pos] == '_' || expr[pos] == '-' || expr[pos] == '.')) pos++;
            string ident = expr.Substring(identStart, pos - identStart);

            if (string.Equals(ident, "true", StringComparison.OrdinalIgnoreCase) || string.Equals(ident, "false", StringComparison.OrdinalIgnoreCase))
                return new WhenNode { Type = WhenNodeType.Value, Text = ident.ToLowerInvariant() };

            return new WhenNode { Type = WhenNodeType.Variable, Text = ident };
        }

        public static bool Evaluate(WhenNode node, IContextKeyService ctx)
        {
            if (node == null) return true;
            switch (node.Type)
            {
                case WhenNodeType.And: return Evaluate(node.Left, ctx) && Evaluate(node.Right, ctx);
                case WhenNodeType.Or: return Evaluate(node.Left, ctx) || Evaluate(node.Right, ctx);
                case WhenNodeType.Not: return !Evaluate(node.Left, ctx);
                case WhenNodeType.Eq: 
                    return string.Equals(GetString(node.Left, ctx), GetString(node.Right, ctx), StringComparison.OrdinalIgnoreCase);
                case WhenNodeType.NotEq: 
                    return !string.Equals(GetString(node.Left, ctx), GetString(node.Right, ctx), StringComparison.OrdinalIgnoreCase);
                case WhenNodeType.Regex:
                    string valToMatch = GetString(node.Left, ctx);
                    string pattern = GetString(node.Right, ctx);
                    if (string.IsNullOrEmpty(pattern)) return false;
                    try
                    {
                        return Regex.IsMatch(valToMatch, pattern, RegexOptions.IgnoreCase);
                    }
                    catch { return false; }
                case WhenNodeType.In:
                    // VS Code: 'in' means value is a key in the object or item in array. We just check if the string contains it for simplicity if it's a string, or split.
                    string item = GetString(node.Left, ctx);
                    string list = GetString(node.Right, ctx);
                    if (string.IsNullOrEmpty(list)) return false;
                    // simplified implementation of IN for C# 5
                    return list.IndexOf(item, StringComparison.OrdinalIgnoreCase) >= 0;
                    
                case WhenNodeType.Variable:
                    object v = ctx != null ? ctx.GetValue(node.Text) : null;
                    if (v is bool) return (bool)v;
                    if (v is string)
                    {
                        string s = (string)v;
                        if (s == "true") return true;
                        if (s == "false") return false;
                        return !string.IsNullOrEmpty(s);
                    }
                    return v != null;
                case WhenNodeType.Value:
                    return string.Equals(node.Text, "true", StringComparison.OrdinalIgnoreCase);
                default: return false;
            }
        }

        private static string GetString(WhenNode node, IContextKeyService ctx)
        {
            if (node == null) return "";
            if (node.Type == WhenNodeType.Value) return node.Text;
            if (node.Type == WhenNodeType.Variable)
            {
                object v = ctx != null ? ctx.GetValue(node.Text) : null;
                if (v == null) return "";
                if (v is bool) return ((bool)v) ? "true" : "false";
                return v.ToString();
            }
            return Evaluate(node, ctx) ? "true" : "false";
        }
    }
}
