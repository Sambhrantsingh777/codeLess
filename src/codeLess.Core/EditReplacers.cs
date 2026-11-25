using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace codeLess.Studio
{
    public static class EditReplacers
    {
        public static string Replace(string content, string oldString, string newString, bool replaceAll)
        {
            if (string.Equals(oldString, newString, StringComparison.Ordinal))
                throw new ArgumentException("oldString and newString must be different");

            var candidates = GetCandidates(content, oldString, newString);
            
            bool foundAny = false;
            bool onlyAmbiguous = true;
            
            foreach (var kvp in candidates)
            {
                string search = kvp.Key;
                string replacement = kvp.Value;

                int idx = content.IndexOf(search, StringComparison.Ordinal);
                if (idx < 0) continue;
                
                foundAny = true;
                
                if (replaceAll)
                {
                    return content.Replace(search, replacement);
                }
                
                if (idx != content.LastIndexOf(search, StringComparison.Ordinal))
                {
                    continue; // Ambiguous
                }
                
                onlyAmbiguous = false;
                
                return content.Substring(0, idx) + replacement + content.Substring(idx + search.Length);
            }
            
            if (!foundAny)
                throw new ArgumentException("oldString not found in content");
            
            if (onlyAmbiguous)
                throw new ArgumentException("Found multiple matches for oldString. Provide more surrounding lines in oldString to identify the correct match.");
            
            return content;
        }



        private static Dictionary<string, string> GetCandidates(string content, string find, string replace)
        {
            var candidates = new Dictionary<string, string>();
            
            Action<string, string> Add = (s, r) => {
                if (!candidates.ContainsKey(s)) candidates[s] = r;
            };

            string[] findLines = find.Replace("\r\n", "\n").Replace("\r", "\n").Split('\n');
            string[] contentLines = content.Replace("\r\n", "\n").Replace("\r", "\n").Split('\n');
            
            // 1. Simple
            Add(find, replace);
            
            // 2. LineTrimmed
            string trimmedFind = string.Join("\n", findLines.Select(l => l.Trim()));
            string trimmedContent = string.Join("\n", contentLines.Select(l => l.Trim()));
            if (trimmedContent.Contains(trimmedFind))
            {
                string pattern = "(?m)^[ \t]*" + string.Join("[ \t]*\r?\n[ \t]*", findLines.Select(l => Regex.Escape(l.Trim()))) + "[ \t]*$";
                var match = Regex.Match(content, pattern);
                if (match.Success) Add(match.Value, replace);
            }
            
            // 3. BlockAnchor
            if (findLines.Length >= 3)
            {
                string anchorFirst = findLines[0].Trim();
                string anchorLast = findLines[findLines.Length - 1].Trim();
                if (!string.IsNullOrEmpty(anchorFirst) && !string.IsNullOrEmpty(anchorLast))
                {
                    string pattern = "(?m)^[ \t]*" + Regex.Escape(anchorFirst) + ".*\n(.*\n)*.*" + Regex.Escape(anchorLast) + "[ \t]*$";
                    var matches = Regex.Matches(content, pattern);
                    
                    if (matches.Count > 0)
                    {
                        string bestMatch = null;
                        double bestScore = -1;
                        
                        foreach (Match m in matches)
                        {
                            string matchMid = string.Join("\n", m.Value.Replace("\r\n", "\n").Split('\n').Skip(1).Reverse().Skip(1).Reverse());
                            string findMid = string.Join("\n", findLines.Skip(1).Reverse().Skip(1).Reverse());
                            
                            int dist = StringUtils.ComputeLevenshtein(matchMid, findMid);
                            int maxLen = Math.Max(matchMid.Length, findMid.Length);
                            double sim = maxLen == 0 ? 1.0 : 1.0 - (double)dist / maxLen;
                            
                            if (sim > bestScore)
                            {
                                bestScore = sim;
                                bestMatch = m.Value;
                            }
                        }
                        
                        if (bestMatch != null && (matches.Count == 1 ? bestScore >= 0.0 : bestScore >= 0.3))
                        {
                            Add(bestMatch, replace);
                        }
                    }
                }
            }

            // 4. WhitespaceNormalized
            string wsNormalizedFind = Regex.Replace(find, @"\s+", " ").Trim();
            if (Regex.Replace(content, @"\s+", " ").Contains(wsNormalizedFind))
            {
                string wsPattern = string.Join("\\s+", find.Split(new[]{' ', '\t', '\n', '\r'}, StringSplitOptions.RemoveEmptyEntries).Select(Regex.Escape));
                var match = Regex.Match(content, wsPattern);
                if (match.Success) Add(match.Value, replace);
            }
            
            // 5. IndentationFlexible
            int minIndent = findLines.Where(l => l.Trim().Length > 0).Select(l => l.Length - l.TrimStart().Length).DefaultIfEmpty(0).Min();
            if (minIndent > 0)
            {
                string unindentedFind = string.Join("\n", findLines.Select(l => l.Length >= minIndent && l.Substring(0, minIndent).Trim().Length == 0 ? l.Substring(minIndent) : l));
                Add(unindentedFind, replace);
            }

            // 6. EscapeNormalized
            try {
                string unescaped = Regex.Unescape(find);
                if (unescaped != find) Add(unescaped, Regex.Unescape(replace));
            } catch { }

            // 7. TrimmedBoundary
            string trimmed = find.Trim();
            if (trimmed != find && !string.IsNullOrEmpty(trimmed)) {
                Add(trimmed, replace.Trim());
            }

            // 8. ContextAware
            if (findLines.Length >= 3)
            {
                string anchorFirst = findLines[0].Trim();
                string anchorLast = findLines[findLines.Length - 1].Trim();
                if (!string.IsNullOrEmpty(anchorFirst) && !string.IsNullOrEmpty(anchorLast))
                {
                    string pattern = "(?m)^[ \t]*" + Regex.Escape(anchorFirst) + ".*\n(.*\n)*.*" + Regex.Escape(anchorLast) + "[ \t]*$";
                    var matches = Regex.Matches(content, pattern);
                    
                    var nonEmptyMid = findLines.Skip(1).Reverse().Skip(1).Reverse().Select(l => l.Trim()).Where(l => l.Length > 0).ToList();
                    int requiredMatches = (int)Math.Ceiling(nonEmptyMid.Count / 2.0);
                    
                    foreach (Match m in matches)
                    {
                        var matchMidLines = m.Value.Replace("\r\n", "\n").Split('\n').Skip(1).Reverse().Skip(1).Reverse().Select(l => l.Trim()).Where(l => l.Length > 0).ToList();
                        int matchedCount = 0;
                        foreach (var req in nonEmptyMid) {
                            if (matchMidLines.Contains(req)) {
                                matchedCount++;
                                matchMidLines.Remove(req); // handle duplicates
                            }
                        }
                        if (matchedCount >= requiredMatches || nonEmptyMid.Count == 0) {
                            Add(m.Value, replace);
                        }
                    }
                }
            }
            
            // 9. MultiOccurrence
            // In replaceAll mode, it matches all occurrences of find (same as simple)
            Add(find, replace);
            
            return candidates;
        }
    }
}
