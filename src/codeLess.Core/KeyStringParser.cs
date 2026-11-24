using System;
using System.Collections.Generic;
using System.Text;

namespace codeLess.Core
{
    public static class KeyStringParser
    {
        public static string Normalize(string keyString)
        {
            if (string.IsNullOrWhiteSpace(keyString)) return "";
            string[] chords = keyString.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            for (int i = 0; i < chords.Length; i++)
            {
                chords[i] = NormalizeChord(chords[i]);
            }
            return string.Join(" ", chords);
        }

        private static string NormalizeChord(string chord)
        {
            string[] parts = chord.Split('+');
            bool ctrl = false, shift = false, alt = false, cmd = false;
            string key = "";

            foreach (string p in parts)
            {
                string lower = p.Trim().ToLowerInvariant();
                if (lower == "ctrl" || lower == "control") ctrl = true;
                else if (lower == "shift") shift = true;
                else if (lower == "alt" || lower == "option") alt = true;
                else if (lower == "cmd" || lower == "meta" || lower == "command" || lower == "win" || lower == "windows") cmd = true;
                else key = lower;
            }

            var sb = new StringBuilder();
            if (ctrl) sb.Append("ctrl+");
            if (shift) sb.Append("shift+");
            if (alt) sb.Append("alt+");
            if (cmd) sb.Append("cmd+");
            
            sb.Append(key);
            return sb.ToString();
        }
    }
}
