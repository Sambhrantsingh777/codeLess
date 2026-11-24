using System;
using System.Text;

namespace codeLess.Core
{
    public static class KeyStringParser
    {
        public static string FromKey(int key, int modifiers)
        {
            // We use integer casts to avoid Avalonia dependency in Core.
            // KeyModifiers.Alt = 1, Control = 2, Shift = 4, Meta = 8
            bool alt = (modifiers & 1) != 0;
            bool ctrl = (modifiers & 2) != 0;
            bool shift = (modifiers & 4) != 0;
            bool meta = (modifiers & 8) != 0;

            var sb = new StringBuilder();
            if (ctrl) sb.Append("ctrl+");
            if (shift) sb.Append("shift+");
            if (alt) sb.Append("alt+");
            if (meta) sb.Append("cmd+");

            sb.Append(FormatKey(key));
            return sb.ToString();
        }

        private static string FormatKey(int key)
        {
            // Simple mapping of Avalonia Key (int) to VS Code string
            // 44..69 = A..Z
            if (key >= 44 && key <= 69) return ((char)('a' + (key - 44))).ToString();
            // 34..43 = D0..D9
            if (key >= 34 && key <= 43) return ((char)('0' + (key - 34))).ToString();
            
            switch (key)
            {
                case 3: return "enter";
                case 2: return "tab";
                case 18: return "escape";
                case 14: return "space";
                case 17: return "backspace";
                case 20: return "up";
                case 21: return "down";
                case 22: return "left";
                case 23: return "right";
                case 32: return "pageup";
                case 33: return "pagedown";
                case 30: return "home";
                case 31: return "end";
                case 152: return "f1";
                case 153: return "f2";
                case 154: return "f3";
                case 155: return "f4";
                case 156: return "f5";
                case 157: return "f6";
                case 158: return "f7";
                case 159: return "f8";
                case 160: return "f9";
                case 161: return "f10";
                case 162: return "f11";
                case 163: return "f12";
                case 141: return "`"; // OemTilde
                case 142: return "-"; // OemMinus
                case 143: return "="; // OemPlus
                case 144: return "["; // OemOpenBrackets
                case 145: return "]"; // OemCloseBrackets
                case 146: return "\\"; // OemPipe
                case 147: return ";"; // OemSemicolon
                case 148: return "'"; // OemQuotes
                case 149: return ","; // OemComma
                case 150: return "."; // OemPeriod
                case 151: return "/"; // OemQuestion
            }
            return key.ToString();
        }
    }
}
