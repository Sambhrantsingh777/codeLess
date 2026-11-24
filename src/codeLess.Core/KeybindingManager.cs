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
        private static List<KeybindingEntry> cachedDefaults = null;
        private static readonly object defaultLock = new object();

        private readonly List<KeybindingEntry> activeBindings = new List<KeybindingEntry>();
        private readonly List<KeybindingEntry> removedBindings = new List<KeybindingEntry>();
        private readonly Dictionary<string, Action> actions = new Dictionary<string, Action>();

        private string chordPrefix = null;

        private static List<KeybindingEntry> GetDefaults()
        {
            if (cachedDefaults != null) return cachedDefaults;
            lock (defaultLock)
            {
                if (cachedDefaults != null) return cachedDefaults;
                var list = new List<KeybindingEntry>();
                bool isMac = RuntimeInformation.IsOSPlatform(OSPlatform.OSX);
                bool isLinux = RuntimeInformation.IsOSPlatform(OSPlatform.Linux);

                foreach (var cmd in CommandRegistry.AllCommands)
                {
                    string key = isMac ? cmd.DefaultKeyMac : (isLinux ? cmd.DefaultKeyLinux : cmd.DefaultKeyWin);
                    if (string.IsNullOrEmpty(key) && !isMac)
                        key = cmd.DefaultKeyWin;
                    
                    if (!string.IsNullOrEmpty(key))
                    {
                        list.Add(new KeybindingEntry
                        {
                            Key = key.ToLowerInvariant(),
                            Command = cmd.Id,
                            When = cmd.When,
                            WhenParsed = WhenParser.Parse(cmd.When)
                        });
                    }
                }
                cachedDefaults = list;
                return cachedDefaults;
            }
        }

        public KeybindingManager()
        {
            UpdateActiveBindings(null);
        }

        public void BindAction(string id, Action action)
        {
            actions[id] = action;
        }

        public bool ExecuteCommand(string id)
        {
            Action action;
            if (actions.TryGetValue(id, out action))
            {
                action();
                return true;
            }
            return false;
        }

        public string LoadUserBindings(string jsonContent)
        {
            string parseError = null;
            var userBindings = new List<KeybindingEntry>();
            if (!string.IsNullOrWhiteSpace(jsonContent))
            {
                var result = JsoncParser.Parse(jsonContent, out string err);
                parseError = err;
                if (err == null && result is object[] arr)
                {
                    foreach (var item in arr)
                    {
                        if (item is Dictionary<string, object> dict)
                        {
                            var entry = new KeybindingEntry();
                            if (dict.TryGetValue("key", out object k) && k != null) entry.Key = k.ToString();
                            if (dict.TryGetValue("command", out object c) && c != null) entry.Command = c.ToString();
                            if (dict.TryGetValue("when", out object w) && w != null) entry.When = w.ToString();
                            if (dict.TryGetValue("args", out object a)) entry.Args = a;
                            
                            if (!string.IsNullOrEmpty(entry.Key) && !string.IsNullOrEmpty(entry.Command))
                            {
                                userBindings.Add(entry);
                            }
                        }
                    }
                }
            }
            UpdateActiveBindings(userBindings);
            return parseError;
        }

        private void UpdateActiveBindings(List<KeybindingEntry> userBindings)
        {
            activeBindings.Clear();
            activeBindings.AddRange(GetDefaults());
            removedBindings.Clear();
            
            if (userBindings != null)
            {
                foreach (var user in userBindings)
                {
                    if (user.Command.StartsWith("-"))
                    {
                        string toRemove = user.Command.Substring(1);
                        var removedList = activeBindings.FindAll(b => b.Command == toRemove && (string.IsNullOrEmpty(user.When) || b.When == user.When));
                        foreach (var r in removedList)
                        {
                            removedBindings.Add(new KeybindingEntry
                            {
                                Key = r.Key,
                                Command = r.Command,
                                When = r.When,
                                WhenParsed = r.WhenParsed
                            });
                        }
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
                if (b.Key != null && b.Key.StartsWith(keyGesture + " ", StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            return false;
        }

        public bool Dispatch(string keyGesture, IContextKeyService ctx, Action<string> showStatus, bool isTerminalFocus = false)
        {
            if (keyGesture == "escape" && chordPrefix != null)
            {
                chordPrefix = null;
                showStatus("");
                return true;
            }

            string fullGesture = chordPrefix != null ? (chordPrefix + " " + keyGesture) : keyGesture;

            for (int i = activeBindings.Count - 1; i >= 0; i--)
            {
                var entry = activeBindings[i];
                if (entry.Key == fullGesture)
                {
                    if (WhenParser.Evaluate(entry.WhenParsed, ctx))
                    {
                        if (isTerminalFocus && !TerminalCommands.CommandsToSkipShell.Contains(entry.Command)) return false;
                        chordPrefix = null;
                        showStatus("");
                        return ExecuteCommand(entry.Command);
                    }
                }
            }

            if (chordPrefix != null)
            {
                string msg = $"The key combination ({chordPrefix}, {keyGesture}) is not a command.";
                chordPrefix = null;
                showStatus(msg);
                return true; // swallowed
            }

            if (HasChordPrefix(keyGesture))
            {
                chordPrefix = keyGesture;
                showStatus($"({keyGesture}) was pressed. Waiting for second key of chord...");
                return true;
            }

            for (int i = removedBindings.Count - 1; i >= 0; i--)
            {
                var entry = removedBindings[i];
                if (entry.Key == fullGesture)
                {
                    if (WhenParser.Evaluate(entry.WhenParsed, ctx))
                    {
                        return true; // Block fallback
                    }
                }
            }

            return false;
        }
    }
}
