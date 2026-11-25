using System;
using System.Text;

namespace codeLess.Core
{
    public static class KeyStringParser
    {
        public static string FromKeyName(string keyName, int modifiers)
        {
            bool alt = (modifiers & 1) != 0;
            bool ctrl = (modifiers & 2) != 0;
            bool shift = (modifiers & 4) != 0;
            bool meta = (modifiers & 8) != 0;

            var sb = new StringBuilder();
            if (ctrl) sb.Append("ctrl+");
            if (shift) sb.Append("shift+");
            if (alt) sb.Append("alt+");
            if (meta) sb.Append("cmd+");

            sb.Append(FormatKeyName(keyName));
            return sb.ToString();
        }

        public static string FromKey(int key, int modifiers)
        {
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

        private static string FormatKeyName(string name)
        {
            if (string.IsNullOrEmpty(name)) return "";
            if (name.Length == 1 && ((name[0] >= 'A' && name[0] <= 'Z') || (name[0] >= 'a' && name[0] <= 'z') || (name[0] >= '0' && name[0] <= '9')))
                return name.ToLowerInvariant();
            if (name.Length == 2 && (name[0] == 'D' || name[0] == 'd') && name[1] >= '0' && name[1] <= '9')
                return name.Substring(1);
            switch (name)
            {
                case "Return":
                case "Enter": return "enter";
                case "Tab": return "tab";
                case "Escape": return "escape";
                case "Space": return "space";
                case "Back":
                case "Backspace": return "backspace";
                case "Delete": return "delete";
                case "Up": return "up";
                case "Down": return "down";
                case "Left": return "left";
                case "Right": return "right";
                case "PageUp": return "pageup";
                case "PageDown":
                case "Next": return "pagedown";
                case "Home": return "home";
                case "End": return "end";
                case "OemTilde": return "`";
                case "OemMinus":
                case "Subtract": return "-";
                case "OemPlus":
                case "Add": return "=";
                case "OemOpenBrackets": return "[";
                case "OemCloseBrackets": return "]";
                case "OemPipe":
                case "OemBackslash": return "\\";
                case "OemSemicolon": return ";";
                case "OemQuotes": return "'";
                case "OemComma": return ",";
                case "OemPeriod": return ".";
                case "OemQuestion": return "/";
                case "NumPad0": return "numpad0";
            }
            return name.ToLowerInvariant();
        }

        private static string FormatKey(int key)
        {
            // 44..69 = A..Z
            if (key >= 44 && key <= 69) return ((char)('a' + (key - 44))).ToString();
            // 34..43 = D0..D9
            if (key >= 34 && key <= 43) return ((char)('0' + (key - 34))).ToString();

            switch (key)
            {
                case 2: return "tab";
                case 3:
                case 6: return "enter";
                case 13:
                case 18: return "escape";
                case 14: return "space";
                case 17: return "backspace";
                case 19:
                case 32: return "pageup";
                case 20: return "up";
                case 21: return "down";
                case 22: return "left";
                case 23: return "right";
                case 30: return "home";
                case 31: return "end";
                case 33: return "pagedown";
                case 140: return ";"; // OemSemicolon
                case 141: return "="; // OemPlus
                case 142: return ","; // OemComma
                case 143: return "-"; // OemMinus
                case 144: return "."; // OemPeriod
                case 145: return "/"; // OemQuestion
                case 146: return "`"; // OemTilde
                case 149: return "["; // OemOpenBrackets
                case 150: return "\\"; // OemPipe
                case 151: return "]"; // OemCloseBrackets
                case 152: return "'"; // OemQuotes
            }
            return key.ToString();
        }
    }
}
