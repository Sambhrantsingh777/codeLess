using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
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

    public static class KeybindingManager
    {
        private static readonly List<KeybindingEntry> defaults = new List<KeybindingEntry>();
        private static readonly List<KeybindingEntry> userBindings = new List<KeybindingEntry>();
        private static readonly List<KeybindingEntry> activeBindings = new List<KeybindingEntry>();

        public static void InitializeDefaults()
        {
            defaults.Clear();
            bool isMac = RuntimeInformation.IsOSPlatform(OSPlatform.OSX);
            bool isLinux = RuntimeInformation.IsOSPlatform(OSPlatform.Linux);

            foreach (var cmd in CommandRegistry.AllCommands)
            {
                string key = isMac ? cmd.DefaultKeyMac : (isLinux ? cmd.DefaultKeyLinux : cmd.DefaultKeyWin);
                if (string.IsNullOrEmpty(key) && !isMac)
                    key = cmd.DefaultKeyWin; // fallback
                
                if (!string.IsNullOrEmpty(key))
                {
                    defaults.Add(new KeybindingEntry
                    {
                        Key = key,
                        Command = cmd.Id,
                        When = cmd.When,
                        WhenParsed = WhenParser.Parse(cmd.When)
                    });
                }
            }
            UpdateActiveBindings();
        }

        public static void LoadUserBindings(string jsonContent)
        {
            userBindings.Clear();
            if (string.IsNullOrWhiteSpace(jsonContent))
            {
                UpdateActiveBindings();
                return;
            }
            
            UpdateActiveBindings();
        }

        private static void UpdateActiveBindings()
        {
            activeBindings.Clear();
            activeBindings.AddRange(defaults);
            
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
                    activeBindings.Add(user);
                }
            }
        }

        public static bool Dispatch(string keyGesture, IWhenContext ctx)
        {
            for (int i = activeBindings.Count - 1; i >= 0; i--)
            {
                var entry = activeBindings[i];
                if (string.Equals(entry.Key, keyGesture, StringComparison.OrdinalIgnoreCase))
                {
                    if (WhenParser.Evaluate(entry.WhenParsed, ctx))
                    {
                        return CommandRegistry.ExecuteCommand(entry.Command);
                    }
                }
            }
            return false;
        }
    }
}
