using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace codeLess.Core
{
    public class KeybindingEntry
    {
        public string Key { get; set; }
        public string Command { get; set; }
        public string When { get; set; }
        public object Args { get; set; }

        public WhenNode WhenParsed { get; set; }
    }

    public class KeybindingManager
    {
        private static readonly List<KeybindingEntry> defaults = new List<KeybindingEntry>();
        private readonly List<KeybindingEntry> activeBindings = new List<KeybindingEntry>();

        private string chordPrefix = null;

        public static void InitializeDefaults()
        {
            defaults.Clear();
            bool isMac = RuntimeInformation.IsOSPlatform(OSPlatform.OSX);
            bool isLinux = RuntimeInformation.IsOSPlatform(OSPlatform.Linux);

            foreach (var cmd in CommandRegistry.AllCommands)
            {
                string key = isMac ? cmd.DefaultKeyMac : (isLinux ? cmd.DefaultKeyLinux : cmd.DefaultKeyWin);
                if (string.IsNullOrEmpty(key) && !isMac)
                    key = cmd.DefaultKeyWin;
                
                if (!string.IsNullOrEmpty(key))
                {
                    defaults.Add(new KeybindingEntry
                    {
                        Key = key.ToLowerInvariant(),
                        Command = cmd.Id,
                        When = cmd.When,
                        WhenParsed = WhenParser.Parse(cmd.When)
                    });
                }
            }
        }

        public KeybindingManager()
        {
            UpdateActiveBindings(null);
        }

        public void LoadUserBindings(string jsonContent)
        {
            var userBindings = new List<KeybindingEntry>();
            if (!string.IsNullOrWhiteSpace(jsonContent))
            {
                // Dummy parsing for C# 5 compilation
            }
            UpdateActiveBindings(userBindings);
        }

        private void UpdateActiveBindings(List<KeybindingEntry> userBindings)
        {
            activeBindings.Clear();
            activeBindings.AddRange(defaults);
            
            if (userBindings != null)
            {
                foreach (var user in userBindings)
                {
                    if (user.Command.StartsWith("-"))
                    {
                        string toRemove = user.Command.Substring(1);
                        activeBindings.RemoveAll(b => b.Command == toRemove && (string.IsNullOrEmpty(user.When) || b.When == user.When));
                    }
                    else
                    {
                        user.WhenParsed = WhenParser.Parse(user.When);
                        user.Key = user.Key?.ToLowerInvariant();
                        activeBindings.Add(user);
                    }
                }
            }
        }

        public bool HasChordPrefix(string keyGesture)
        {
            foreach (var b in activeBindings)
            {
                if (b.Key.StartsWith(keyGesture + " ", StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            return false;
        }

        public bool Dispatch(string keyGesture, IContextKeyService ctx, Action<string> showStatus)
        {
            if (keyGesture == "escape" && chordPrefix != null)
            {
                chordPrefix = null;
                showStatus("");
                return true;
            }

            string fullGesture = chordPrefix != null ? (chordPrefix + " " + keyGesture) : keyGesture;

            bool foundAnyMatch = false;
            for (int i = activeBindings.Count - 1; i >= 0; i--)
            {
                var entry = activeBindings[i];
                if (entry.Key == fullGesture)
                {
                    if (WhenParser.Evaluate(entry.WhenParsed, ctx))
                    {
                        chordPrefix = null;
                        showStatus("");
                        return CommandRegistry.ExecuteCommand(entry.Command);
                    }
                }
            }

            if (chordPrefix != null)
            {
                // Wait, if it's the second key of a chord and no match, VS Code swallows it and resets
                string msg = $"The key combination ({chordPrefix}, {keyGesture}) is not a command.";
                chordPrefix = null;
                showStatus(msg);
                return true; // swallowed
            }

            // Check if it's a prefix
            if (HasChordPrefix(keyGesture))
            {
                chordPrefix = keyGesture;
                showStatus($"({keyGesture}) was pressed. Waiting for second key of chord...");
                return true;
            }

            return false;
        }
    }
}
