using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Linq;
using codeLess.Studio;

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

    /// <summary>
    /// Per-window keybinding and command dispatcher. Each workbench window owns its own instance
    /// bound to that window's command handlers so shortcuts never cross-contaminate another window.
    /// </summary>
    public class KeybindingManager
    {
        private static readonly object defaultLock = new object();

        public static bool? ForceMacKeymapOverride { get; set; }
        public static bool? ForceLinuxKeymapOverride { get; set; }

        public static bool IsMacOS
        {
            get
            {
                if (ForceMacKeymapOverride.HasValue)
                    return ForceMacKeymapOverride.Value;
                string env = Environment.GetEnvironmentVariable("CODELESS_FORCE_NONMAC_KEYMAP");
                if (!string.IsNullOrEmpty(env) && !string.Equals(env, "0", StringComparison.OrdinalIgnoreCase) && !string.Equals(env, "false", StringComparison.OrdinalIgnoreCase))
                    return false;
                return RuntimeInformation.IsOSPlatform(OSPlatform.OSX);
            }
        }

        public static bool IsLinux
        {
            get
            {
                if (ForceLinuxKeymapOverride.HasValue)
                    return ForceLinuxKeymapOverride.Value;
                string env = Environment.GetEnvironmentVariable("CODELESS_FORCE_NONMAC_KEYMAP");
                if (!string.IsNullOrEmpty(env))
                {
                    if (string.Equals(env, "linux", StringComparison.OrdinalIgnoreCase))
                        return true;
                    if (!string.Equals(env, "0", StringComparison.OrdinalIgnoreCase) && !string.Equals(env, "false", StringComparison.OrdinalIgnoreCase))
                        return RuntimeInformation.IsOSPlatform(OSPlatform.Linux);
                }
                if (ForceMacKeymapOverride.HasValue && ForceMacKeymapOverride.Value)
                    return false;
                return RuntimeInformation.IsOSPlatform(OSPlatform.Linux);
            }
        }

        private bool _isMac;
        private bool _isLinux;
        private readonly List<KeybindingEntry> activeBindings = new List<KeybindingEntry>();
        private readonly List<KeybindingEntry> removedBindings = new List<KeybindingEntry>();
        private readonly Dictionary<string, Action> actions = new Dictionary<string, Action>(StringComparer.Ordinal);
        private readonly Dictionary<string, string> _shortcutToCommand = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        private string chordPrefix = null;

        public bool IsMac { get { return _isMac; } }

        private static List<KeybindingEntry> BuildDefaultsForPlatform(bool isMac, bool isLinux)
        {
            lock (defaultLock)
            {
                DefaultCommands.Register();
                var list = new List<KeybindingEntry>();

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
                return list;
            }
        }

        private List<KeybindingEntry> GetDefaults()
        {
            return BuildDefaultsForPlatform(_isMac, _isLinux);
        }

        public List<KeybindingEntry> ActiveBindings { get { return activeBindings; } }
        public List<KeybindingEntry> RemovedBindings { get { return removedBindings; } }
        public List<KeybindingEntry> Defaults { get { return GetDefaults(); } }
        public List<KeybindingEntry> UserBindings { get; private set; }

        public int CommandCount
        {
            get { return actions.Count; }
        }

        public static string SaveUserBindings(List<KeybindingEntry> userBindings, string testPath = null)
        {
            var ser = new System.Web.Script.Serialization.JavaScriptSerializer();
            string path = testPath ?? codeLess.Studio.IdePersistentSettings.GetKeybindingsFilePath();
            string json = "[\n]";
            try { if (System.IO.File.Exists(path)) json = System.IO.File.ReadAllText(path); } catch { }

            int lastBracket = json.LastIndexOf(']');
            if (lastBracket == -1)
            {
                lastBracket = json.Length;
            }

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

            // Parse the existing file to preserve comments and formatting for unchanged entries.
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
                        // Locate the object containing this command id and remove its span.
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

            try
            {
                System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path));
                string tempPath = path + ".tmp";
                System.IO.File.WriteAllText(tempPath, json);
                if (System.IO.File.Exists(path)) System.IO.File.Replace(tempPath, path, null);
                else System.IO.File.Move(tempPath, path);
                return null;
            }
            catch (Exception ex)
            {
                return ex.Message;
            }
        }

        public KeybindingManager()
            : this(IsMacOS, IsLinux)
        {
        }

        public KeybindingManager(bool isMac)
            : this(isMac, !isMac && IsLinux)
        {
        }

        public KeybindingManager(bool isMac, bool isLinux)
        {
            _isMac = isMac;
            _isLinux = !isMac && isLinux;
            LoadDefaultMenuBindings();
            UpdateActiveBindings(null);
        }

        public void SetPlatform(bool isMac)
        {
            SetPlatform(isMac, !isMac && IsLinux);
        }

        public void SetPlatform(bool isMac, bool isLinux)
        {
            bool normLinux = !isMac && isLinux;
            if (_isMac == isMac && _isLinux == normLinux) return;
            _isMac = isMac;
            _isLinux = normLinux;
            _shortcutToCommand.Clear();
            LoadDefaultMenuBindings();
            UpdateActiveBindings(UserBindings);
        }

        private void LoadDefaultMenuBindings()
        {
            List<WorkbenchMenuEntry> entries = WorkbenchMenuModel.AllCommands(_isMac);
            for (int i = 0; i < entries.Count; i++)
            {
                WorkbenchMenuEntry e = entries[i];
                if (e == null || string.IsNullOrEmpty(e.Id) || e.DisplayOnly) continue;
                string sc = e.ShortcutFor(_isMac);
                if (!string.IsNullOrEmpty(sc) && !_shortcutToCommand.ContainsKey(sc))
                {
                    _shortcutToCommand[sc] = e.Id;
                }
            }
        }

        public void BindAction(string id, Action action)
        {
            if (string.IsNullOrEmpty(id)) return;
            actions[id] = action;
        }

        public void RegisterCommand(string commandId, Action handler)
        {
            if (string.IsNullOrEmpty(commandId) || handler == null) return;
            actions[commandId] = handler;
        }

        public void RegisterCommands(IEnumerable<KeyValuePair<string, Action>> commands)
        {
            if (commands == null) return;
            foreach (KeyValuePair<string, Action> kv in commands)
            {
                if (!string.IsNullOrEmpty(kv.Key) && kv.Value != null)
                {
                    actions[kv.Key] = kv.Value;
                }
            }
        }

        public void BindShortcut(string shortcut, string commandId)
        {
            if (string.IsNullOrEmpty(shortcut) || string.IsNullOrEmpty(commandId)) return;
            _shortcutToCommand[shortcut] = commandId;
        }

        public bool HasCommand(string commandId)
        {
            return !string.IsNullOrEmpty(commandId) && actions.ContainsKey(commandId);
        }

        public bool TryGetCommandId(string shortcut, out string commandId)
        {
            commandId = null;
            if (string.IsNullOrEmpty(shortcut)) return false;
            return _shortcutToCommand.TryGetValue(shortcut, out commandId);
        }

        public bool ExecuteCommand(string id)
        {
            Action action;
            if (!string.IsNullOrEmpty(id) && actions.TryGetValue(id, out action) && action != null)
            {
                action();
                return true;
            }
            return false;
        }

        public bool TryExecuteShortcut(string shortcut)
        {
            string commandId;
            if (TryGetCommandId(shortcut, out commandId))
            {
                return ExecuteCommand(commandId);
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

        private static string CanonicalizeSingleKeystroke(string stroke)
        {
            if (string.IsNullOrEmpty(stroke)) return "";
            string s = stroke.Trim().ToLowerInvariant();
            if (s.IndexOf('+') < 0) return s;

            bool ctrl = false;
            bool shift = false;
            bool alt = false;
            bool option = false;
            bool cmd = false;
            string mainKey = "";

            // Handle trailing '+' key (e.g. "cmd++")
            bool endsWithPlus = s.EndsWith("+") && s.Length > 1 && s[s.Length - 2] == '+';
            string work = endsWithPlus ? s.Substring(0, s.Length - 2) : s;
            string[] tokens = work.Split(new char[] { '+' }, StringSplitOptions.RemoveEmptyEntries);
            for (int i = 0; i < tokens.Length; i++)
            {
                string t = tokens[i].Trim();
                if (t == "ctrl" || t == "control") ctrl = true;
                else if (t == "shift") shift = true;
                else if (t == "alt") alt = true;
                else if (t == "option" || t == "opt") option = true;
                else if (t == "cmd" || t == "meta" || t == "win" || t == "super") cmd = true;
                else mainKey = t;
            }
            if (endsWithPlus) mainKey = "+";

            System.Text.StringBuilder sb = new System.Text.StringBuilder();
            if (ctrl) sb.Append("ctrl+");
            if (shift) sb.Append("shift+");
            if (alt) sb.Append("alt+");
            if (option) sb.Append("option+");
            if (cmd) sb.Append("cmd+");
            sb.Append(mainKey);
            return sb.ToString();
        }

        public static string CanonicalizeGesture(string gesture)
        {
            if (string.IsNullOrEmpty(gesture)) return "";
            string trimmed = gesture.Trim();
            if (trimmed.IndexOf(' ') < 0)
                return CanonicalizeSingleKeystroke(trimmed);
            string[] parts = trimmed.Split(new char[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            for (int i = 0; i < parts.Length; i++)
                parts[i] = CanonicalizeSingleKeystroke(parts[i]);
            return string.Join(" ", parts);
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

        public bool HasPendingChord
        {
            get { return chordPrefix != null; }
        }

        public void CancelPendingChord()
        {
            chordPrefix = null;
        }

        public bool HasBoundChordMatch(string secondKeyGesture, IContextKeyService ctx)
        {
            if (chordPrefix == null) return false;
            string fullGesture = chordPrefix + " " + secondKeyGesture;
            string canonGesture = CanonicalizeGesture(fullGesture);
            for (int i = activeBindings.Count - 1; i >= 0; i--)
            {
                var entry = activeBindings[i];
                bool keyMatches = entry.Key == fullGesture || CanonicalizeGesture(entry.Key) == canonGesture;
                if (keyMatches && WhenParser.Evaluate(entry.WhenParsed, ctx) && HasCommand(entry.Command))
                    return true;
            }
            return false;
        }

        public bool HasChordPrefix(string keyGesture)
        {
            string canonPrefix = CanonicalizeGesture(keyGesture) + " ";
            foreach (var b in activeBindings)
            {
                if (b.Key != null && CanonicalizeGesture(b.Key).StartsWith(canonPrefix, StringComparison.OrdinalIgnoreCase))
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
            string canonGesture = CanonicalizeGesture(fullGesture);

            KeybindingEntry fallbackUnboundEntry = null;
            for (int i = activeBindings.Count - 1; i >= 0; i--)
            {
                var entry = activeBindings[i];
                bool keyMatches = entry.Key == fullGesture ||
                    (entry.Command != null && entry.Command.StartsWith("cursor", StringComparison.Ordinal) &&
                     CanonicalizeGesture(entry.Key) == canonGesture);
                if (keyMatches)
                {
                    if (WhenParser.Evaluate(entry.WhenParsed, ctx))
                    {
                        if (!HasCommand(entry.Command))
                        {
                            if (fallbackUnboundEntry == null) fallbackUnboundEntry = entry;
                            continue;
                        }
                        if (isTerminalFocus && !TerminalCommands.CommandsToSkipShell.Contains(entry.Command)) return false;
                        chordPrefix = null;
                        showStatus("");
                        return ExecuteCommand(entry.Command);
                    }
                }
            }

            if (fallbackUnboundEntry != null)
            {
                if (isTerminalFocus && !TerminalCommands.CommandsToSkipShell.Contains(fallbackUnboundEntry.Command)) return false;
                chordPrefix = null;
                showStatus("");
                return ExecuteCommand(fallbackUnboundEntry.Command);
            }

            if (_isMac && chordPrefix == null && !isTerminalFocus)
            {
                string macSecondaryCmd = null;
                if (canonGesture == "home") macSecondaryCmd = "cursorHome";
                else if (canonGesture == "shift+home") macSecondaryCmd = "cursorHomeSelect";
                else if (canonGesture == "end") macSecondaryCmd = "cursorEnd";
                else if (canonGesture == "shift+end") macSecondaryCmd = "cursorEndSelect";

                if (macSecondaryCmd != null && WhenParser.Evaluate(WhenParser.Parse("editorTextFocus"), ctx))
                {
                    showStatus("");
                    return ExecuteCommand(macSecondaryCmd);
                }
            }

            if (chordPrefix != null)
            {
                string msg = string.Format("The key combination ({0}, {1}) is not a command.", chordPrefix, keyGesture);
                chordPrefix = null;
                showStatus(msg);
                return true; // swallowed
            }

            if (!isTerminalFocus && HasChordPrefix(keyGesture))
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
