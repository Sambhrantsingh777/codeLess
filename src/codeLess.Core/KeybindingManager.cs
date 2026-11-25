using System;
using System.Collections.Generic;

namespace codeLess.Studio
{
    /// <summary>
    /// Per-window keybinding and command dispatcher. Each workbench window owns its own instance
    /// bound to that window's command handlers so shortcuts never cross-contaminate another window.
    /// </summary>
    public class KeybindingManager
    {
        private readonly bool _isMac;
        private readonly Dictionary<string, Action> _commands = new Dictionary<string, Action>(StringComparer.Ordinal);
        private readonly Dictionary<string, string> _shortcutToCommand = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        public KeybindingManager(bool isMac)
        {
            _isMac = isMac;
            LoadDefaultMenuBindings();
        }

        public int CommandCount
        {
            get { return _commands.Count; }
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

        public void RegisterCommand(string commandId, Action handler)
        {
            if (string.IsNullOrEmpty(commandId) || handler == null) return;
            _commands[commandId] = handler;
        }

        public void RegisterCommands(IEnumerable<KeyValuePair<string, Action>> commands)
        {
            if (commands == null) return;
            foreach (KeyValuePair<string, Action> kv in commands)
            {
                if (!string.IsNullOrEmpty(kv.Key) && kv.Value != null)
                {
                    _commands[kv.Key] = kv.Value;
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
            return !string.IsNullOrEmpty(commandId) && _commands.ContainsKey(commandId);
        }

        public bool TryGetCommandId(string shortcut, out string commandId)
        {
            commandId = null;
            if (string.IsNullOrEmpty(shortcut)) return false;
            return _shortcutToCommand.TryGetValue(shortcut, out commandId);
        }

        public bool ExecuteCommand(string commandId)
        {
            Action action;
            if (!string.IsNullOrEmpty(commandId) && _commands.TryGetValue(commandId, out action) && action != null)
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
    }
}
