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

                public List<KeybindingEntry> ActiveBindings { get { return activeBindings; } }
        public List<KeybindingEntry> RemovedBindings { get { return removedBindings; } }
        public List<KeybindingEntry> Defaults { get { return GetDefaults(); } }
        public List<KeybindingEntry> UserBindings { get; private set; }

        public static void SaveUserBindings(List<KeybindingEntry> userBindings)
        {
            var list = new List<Dictionary<string, object>>();
            foreach (var b in userBindings)
            {
                var d = new Dictionary<string, object>();
                if (!string.IsNullOrEmpty(b.Key)) d["key"] = b.Key;
                if (!string.IsNullOrEmpty(b.Command)) d["command"] = b.Command;
                if (!string.IsNullOrEmpty(b.When)) d["when"] = b.When;
                if (b.Args != null) d["args"] = b.Args;
                list.Add(d);
            }
            var ser = new System.Web.Script.Serialization.JavaScriptSerializer();
            // simple formatted JSON (manual indent)
            var sb = new System.Text.StringBuilder();
            sb.AppendLine("[");
            for (int i = 0; i < list.Count; i++)
            {
                string json = ser.Serialize(list[i]);
                sb.Append("  " + json);
                if (i < list.Count - 1) sb.AppendLine(",");
                else sb.AppendLine();
            }
            sb.AppendLine("]");
            
            try {
                System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(codeLess.Studio.IdePersistentSettings.GetKeybindingsFilePath()));
                System.IO.File.WriteAllText(codeLess.Studio.IdePersistentSettings.GetKeybindingsFilePath(), sb.ToString());
            } catch { }
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
                string err;
                var result = JsoncParser.Parse(jsonContent, out err);
                parseError = err;
                System.Collections.IEnumerable arr = result as System.Collections.IEnumerable;
                if (err == null && arr != null)
                {
                    foreach (var item in arr)
                    {
                        Dictionary<string, object> dict = item as Dictionary<string, object>;
                        if (dict != null)
                        {
                            var entry = new KeybindingEntry();
                            object k;
                            if (dict.TryGetValue("key", out k) && k != null) entry.Key = k.ToString();
                            object c;
                            if (dict.TryGetValue("command", out c) && c != null) entry.Command = c.ToString();
                            object w;
                            if (dict.TryGetValue("when", out w) && w != null) entry.When = w.ToString();
                            object a;
                            if (dict.TryGetValue("args", out a)) entry.Args = a;
                            
                            if (!string.IsNullOrEmpty(entry.Command))
                            {
                                userBindings.Add(entry);
                            }
                        }
                    }
                }
            }
            UserBindings = userBindings;
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
                        user.Key = user.Key != null ? user.Key.ToLowerInvariant() : null;
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
                string msg = string.Format("The key combination ({0}, {1}) is not a command.", chordPrefix, keyGesture);
                chordPrefix = null;
                showStatus(msg);
                return true; // swallowed
            }

            if (HasChordPrefix(keyGesture))
            {
                chordPrefix = keyGesture;
                showStatus(string.Format("({0}) was pressed. Waiting for second key of chord...", keyGesture));
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
