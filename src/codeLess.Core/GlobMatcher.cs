using System;
using System.Text.RegularExpressions;
using System.Collections.Generic;

namespace codeLess.Studio
{
    public static class GlobMatcher
    {
        public static Regex GlobToRegex(string glob)
        {
            string pattern = "^";
            bool inBraces = false;
            for (int i = 0; i < glob.Length; i++)
            {
                char c = glob[i];
                if (c == '*' && i + 1 < glob.Length && glob[i + 1] == '*')
                {
                    pattern += ".*";
                    i++;
                }
                else if (c == '*')
                {
                    pattern += "[^/\\\\]*";
                }
                else if (c == '?')
                {
                    pattern += "[^/\\\\]";
                }
                else if (c == '{')
                {
                    inBraces = true;
                    pattern += "(";
                }
                else if (c == '}' && inBraces)
                {
                    inBraces = false;
                    pattern += ")";
                }
                else if (c == ',' && inBraces)
                {
                    pattern += "|";
                }
                else if (".+^$()|[]\\".IndexOf(c) >= 0)
                {
                    pattern += "\\" + c;
                }
                else
                {
                    pattern += c;
                }
            }
            pattern += "$";
            return new Regex(pattern, RegexOptions.IgnoreCase);
        }
    }
}
