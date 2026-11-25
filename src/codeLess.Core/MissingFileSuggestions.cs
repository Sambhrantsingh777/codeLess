using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace codeLess.Studio
{
    public static class MissingFileSuggestions
    {
        public static List<string> SuggestSimilarFiles(string missingPath, IEnumerable<string> allPaths)
        {
            if (string.IsNullOrEmpty(missingPath)) return new List<string>();

            string fileName = Path.GetFileName(missingPath) ?? "";
            if (string.IsNullOrEmpty(fileName)) return new List<string>();

            var suggestions = new List<string>();

            var scored = allPaths.Select(p => 
            {
                string candName = Path.GetFileName(p) ?? "";
                bool contains = candName.IndexOf(fileName, StringComparison.OrdinalIgnoreCase) >= 0;
                int dist = StringUtils.ComputeLevenshtein(fileName.ToLowerInvariant(), candName.ToLowerInvariant());
                return new { Path = p, Contains = contains, Distance = dist };
            }).Where(x => x.Contains || x.Distance <= 2)
              .OrderBy(x => x.Distance)
              .ThenBy(x => x.Path)
              .Take(3)
              .Select(x => x.Path)
              .ToList();

            return scored;
        }
    }
}
