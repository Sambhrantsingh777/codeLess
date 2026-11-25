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
        public static bool? ForceMacKeymapOverride
        {
            get { return codeLess.Core.KeybindingManager.ForceMacKeymapOverride; }
            set { codeLess.Core.KeybindingManager.ForceMacKeymapOverride = value; }
        }

        public static bool IsMacOS
        {
            get
            {
                return codeLess.Core.KeybindingManager.IsMacOS;
            }
        }

        [ThreadStatic] private static int lenientPrimaryDepth;

        /// <summary>
        /// Automation (SimulateShortcut / tests) treats Ctrl and Cmd interchangeably so scripted shortcuts are
        /// platform-neutral. Real keyboard input never runs inside this scope.
        /// </summary>
        public static IDisposable BeginLenientPrimaryMatching()
        {
            lenientPrimaryDepth++;
            return new LenientScope();
        }

        private sealed class LenientScope : IDisposable
        {
            private bool disposed;
            public void Dispose() { if (!disposed) { disposed = true; lenientPrimaryDepth--; } }
        }

        /// <summary>
        /// VS Code primary modifier test: Cmd on macOS (plain Ctrl stays free for Cocoa/emacs editing keys and
        /// shell control characters such as Ctrl+A/E/K/W/R), Ctrl on Windows/Linux.
        /// </summary>
        public static bool IsPrimary(KeyModifiers mods)
        {
            bool ctrl = (mods & KeyModifiers.Control) != 0;
            bool meta = (mods & KeyModifiers.Meta) != 0;
            if (lenientPrimaryDepth > 0) return ctrl || meta;
            return IsMacOS ? meta : ctrl;
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
            if (!string.IsNullOrEmpty(key) && key.Length > 1 && !shift && !alt)
            {
                string[] chords = key.Split(' ');
                var formattedChords = new string[chords.Length];
                for (int c = 0; c < chords.Length; c++)
                {
                    string[] parts = chords[c].Split('+');
                    for (int p = 0; p < parts.Length; p++)
                    {
                        if (parts[p].Length > 0)
                            parts[p] = char.ToUpperInvariant(parts[p][0]) + parts[p].Substring(1);
                    }
                    formattedChords[c] = string.Join("+", parts);
                }
                return codeLess.Studio.WorkbenchMenuModel.FormatShortcut(string.Join(" ", formattedChords), IsMacOS);
            }
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
