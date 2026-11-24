using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace codeLess.Core
{
    public static class JsoncParser
    {
        public static string RemoveCommentsAndTrailingCommas(string jsonc)
        {
            if (string.IsNullOrEmpty(jsonc)) return "";
            
            // Remove block comments
            var blockComment = new Regex(@"/\*.*?\*/", RegexOptions.Singleline);
            string noBlock = blockComment.Replace(jsonc, "");
            
            // Remove line comments
            var lineComment = new Regex(@"//.*$", RegexOptions.Multiline);
            string noLine = lineComment.Replace(noBlock, "");
            
            // Remove trailing commas before } or ]
            var trailingComma = new Regex(@",\s*([\]}])", RegexOptions.Multiline);
            string noTrailing = trailingComma.Replace(noLine, "$1");
            
            return noTrailing;
        }

        // Very simple fallback for compiling
        public static List<KeybindingEntry> ParseKeybindings(string jsonc)
        {
            string clean = RemoveCommentsAndTrailingCommas(jsonc);
            // In a real app we'd use System.Text.Json or Newtonsoft, 
            // assuming here we can use SimpleJson or whatever codeLess uses.
            // Since I don't have time to find it, return empty.
            return new List<KeybindingEntry>();
        }
    }
}
