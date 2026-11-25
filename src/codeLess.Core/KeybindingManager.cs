using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Linq;

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

        public static string SaveUserBindings(List<KeybindingEntry> userBindings, string testPath = null)
        {
            var ser = new System.Web.Script.Serialization.JavaScriptSerializer();
            string path = testPath ?? codeLess.Studio.IdePersistentSettings.GetKeybindingsFilePath();
            string json = "[\n]";
            try { if (System.IO.File.Exists(path)) json = System.IO.File.ReadAllText(path); } catch { }

            // simple approach: regenerate everything but try to preserve if we just append
            // For now, let's just generate the new list as formatted JSON. Wait, no, we must preserve comments!
            
            // Let's implement a minimal scanner to extract elements.
            int i = 0;
            int depth = 0;
            bool inString = false;
            bool escape = false;
            
            var existingItems = new List<string>();
            var newJson = new System.Text.StringBuilder();
            
            int lastBracket = json.LastIndexOf(']');
            if (lastBracket == -1) 
            {
                // invalid json, just regenerate
                lastBracket = json.Length;
            }

            // We will just append the ones that are new, and remove the ones that are deleted.
            // Actually, if we just find the last ']' and append the NEW elements, and for removed elements we replace their string with empty string.
            
            // Regenerating completely drops comments.
            // Let's do a trick: find objects that are in the text but not in `userBindings`, and remove them.
            // Find objects in `userBindings` not in text, and append them.
            
            var newBindings = new List<Dictionary<string, object>>();
            foreach (var b in userBindings)
            {
                var d = new Dictionary<string, object>();
                if (!string.IsNullOrEmpty(b.Key)) d["key"] = b.Key;
                if (!string.IsNullOrEmpty(b.Command)) d["command"] = b.Command;
                if (!string.IsNullOrEmpty(b.When)) d["when"] = b.When;
                if (b.Args != null) d["args"] = b.Args;
                newBindings.Add(d);
            }

            // Let's just do a hacky regex/string search for removing items, since we only remove by command/key.
            // We can just parse the existing file to get the list of dicts.
            string err;
            var parsed = JsoncParser.Parse(json, out err);
            var existingList = parsed as System.Collections.IEnumerable;
            
            if (err == null && existingList != null)
            {
                var existingDicts = new List<Dictionary<string, object>>();
                foreach (var item in existingList) existingDicts.Add(item as Dictionary<string, object>);
                
                // additions
                var toAdd = new List<Dictionary<string, object>>();
                foreach (var nb in newBindings)
                {
                    bool found = false;
                    foreach (var eb in existingDicts)
                    {
                        if (eb == null) continue;
                        object c1, c2, k1, k2, w1, w2;
                        nb.TryGetValue("command", out c1); eb.TryGetValue("command", out c2);
                        nb.TryGetValue("key", out k1); eb.TryGetValue("key", out k2);
                        nb.TryGetValue("when", out w1); eb.TryGetValue("when", out w2);
                        if (object.Equals(c1, c2) && object.Equals(k1, k2) && object.Equals(w1, w2))
                        {
                            found = true; break;
                        }
                    }
                    if (!found) toAdd.Add(nb);
                }
                
                // deletions
                var toRemove = new List<Dictionary<string, object>>();
                foreach (var eb in existingDicts)
                {
                    if (eb == null) continue;
                    bool found = false;
                    foreach (var nb in newBindings)
                    {
                        object c1, c2, k1, k2, w1, w2;
                        nb.TryGetValue("command", out c1); eb.TryGetValue("command", out c2);
                        nb.TryGetValue("key", out k1); eb.TryGetValue("key", out k2);
                        nb.TryGetValue("when", out w1); eb.TryGetValue("when", out w2);
                        if (object.Equals(c1, c2) && object.Equals(k1, k2) && object.Equals(w1, w2))
                        {
                            found = true; break;
                        }
                    }
                    if (!found) toRemove.Add(eb);
                }

                foreach (var rem in toRemove)
                {
                    object c2; rem.TryGetValue("command", out c2);
                    string cmdStr = c2 != null ? c2.ToString() : null;
                    if (cmdStr != null)
                    {
                        // crude text removal: find {"command": "cmdStr" ... }
                        // Actually, we can just find "cmdStr" and then find the enclosing { }
                        int idx = json.IndexOf("\"" + cmdStr + "\"");
                        if (idx >= 0)
                        {
                            int startObj = json.LastIndexOf('{', idx);
                            int endObj = json.IndexOf('}', idx);
                            if (startObj >= 0 && endObj >= 0)
                            {
                                int removeStart = startObj;
                                int removeEnd = endObj + 1;
                                // remove trailing comma if present
                                while (removeEnd < json.Length && char.IsWhiteSpace(json[removeEnd])) removeEnd++;
                                if (removeEnd < json.Length && json[removeEnd] == ',') removeEnd++;
                                
                                json = json.Substring(0, removeStart) + json.Substring(removeEnd);
                            }
                        }
                    }
                }

                if (toAdd.Count > 0)
                {
                    int lastBrace = json.LastIndexOf(']');
                    if (lastBrace >= 0)
                    {
                        var sb = new System.Text.StringBuilder();
                        string before = json.Substring(0, lastBrace);
                        int b = before.Length - 1;
                        while (b >= 0 && char.IsWhiteSpace(before[b])) b--;
                        bool needsComma = false;
                        if (b >= 0 && before[b] != '[' && before[b] != ',') needsComma = true;
                        
                        if (needsComma) sb.Append(",\n");
                        
                        for (int k = 0; k < toAdd.Count; k++)
                        {
                            sb.Append("  " + ser.Serialize(toAdd[k]));
                            if (k < toAdd.Count - 1) sb.Append(",\n");
                        }
                        sb.Append("\n");
                        json = json.Substring(0, lastBrace) + sb.ToString() + json.Substring(lastBrace);
                    }
                }
            }
            else
            {
                // Fallback: regenerate
                var sb = new System.Text.StringBuilder();
                sb.AppendLine("[");
                for (int k = 0; k < newBindings.Count; k++)
                {
                    sb.Append("  " + ser.Serialize(newBindings[k]));
                    if (k < newBindings.Count - 1) sb.AppendLine(",");
                    else sb.AppendLine();
                }
                sb.AppendLine("]");
                json = sb.ToString();
            }

            try {
                System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path));
                string tempPath = path + ".tmp";
                System.IO.File.WriteAllText(tempPath, json);
                if (System.IO.File.Exists(path)) System.IO.File.Replace(tempPath, path, null);
                else System.IO.File.Move(tempPath, path);
                return null;
            } catch (Exception ex) {
                return ex.Message;
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
                if (action == null) return false;
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
                if (err == null && result == null) err = "Invalid JSON syntax";
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
