using System;
using System.Runtime.InteropServices;
using Avalonia.Input;

namespace codeLess.AvaloniaApp
{
    /// <summary>
    /// Cross-platform VS Code keymap helper.
    /// On macOS, Cmd (KeyModifiers.Meta) replaces Ctrl (KeyModifiers.Control).
    /// </summary>
    public static class MacKeymap
    {
        public static bool? ForceMacKeymapOverride { get; set; }

        public static bool IsMacOS
        {
            get
            {
                if (ForceMacKeymapOverride.HasValue)
                    return ForceMacKeymapOverride.Value;
                return RuntimeInformation.IsOSPlatform(OSPlatform.OSX);
            }
        }

        public static KeyModifiers PrimaryModifier
        {
            get { return IsMacOS ? KeyModifiers.Meta : KeyModifiers.Control; }
        }

        public static string PrimaryModifierLabel
        {
            get { return IsMacOS ? "⌘" : "Ctrl+"; }
        }

        public static string FormatShortcut(string key, bool shift = false, bool alt = false)
        {
            if (IsMacOS)
            {
                string prefix = "";
                if (alt) prefix += "⌥";
                if (shift) prefix += "⇧";
                prefix += "⌘";
                return prefix + key.ToUpperInvariant();
            }
            else
            {
                string prefix = "Ctrl+";
                if (shift) prefix += "Shift+";
                if (alt) prefix += "Alt+";
                return prefix + key.ToUpperInvariant();
            }
        }

        public static bool MatchesShortcut(KeyEventArgs e, Key key, bool shift = false, bool alt = false)
        {
            if (e == null || e.Key != key)
                return false;

            KeyModifiers expected = PrimaryModifier;
            if (shift) expected |= KeyModifiers.Shift;
            if (alt) expected |= KeyModifiers.Alt;

            KeyModifiers mask = KeyModifiers.Control | KeyModifiers.Meta | KeyModifiers.Shift | KeyModifiers.Alt;
            return (e.KeyModifiers & mask) == expected;
        }
    }
}
