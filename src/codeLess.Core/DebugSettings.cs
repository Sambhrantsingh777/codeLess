using System;
using System.Collections.Generic;

namespace codeLess.Studio
{
    /// <summary>Workspace path -> value pair (JSON-friendly replacement for a dictionary in settings.json).</summary>
    public class WorkspaceSettingEntry
    {
        public string Workspace { get; set; }
        public string Value { get; set; }
    }

    /// <summary>
    /// A user-configured debug adapter (Settings > debug adapters in settings.json), like VS Code's adapter
    /// executables contributed by extensions. Overrides the built-in auto-detection for the same Type.
    /// Example: { "Type": "go", "Command": "/opt/dlv", "Args": "dap --listen=127.0.0.1:${port}", "UseTcp": true }
    /// Node.js: { "Type": "node", "Command": "node", "Args": "/path/js-debug/src/dapDebugServer.js ${port} 127.0.0.1", "UseTcp": true }
    /// </summary>
    public class UserDebugAdapterEntry
    {
        public string Type { get; set; }
        public string Command { get; set; }
        /// <summary>Space-separated arguments ("quotes" group); ${port} is replaced by a free loopback port.</summary>
        public string Args { get; set; }
        public bool UseTcp { get; set; }
        /// <summary>Optional: connect to an already running adapter on this port instead of starting Command.</summary>
        public int Port { get; set; }
    }

    public partial class IdePersistentSettings
    {
        /// <summary>Selected Python interpreter per workspace ("Python: Select Interpreter").</summary>
        public List<WorkspaceSettingEntry> PythonInterpreters { get; set; }

        /// <summary>User-configured debug adapters (optional; built-ins are auto-detected).</summary>
        public List<UserDebugAdapterEntry> DebugAdapters { get; set; }

        /// <summary>Last chosen Run and Debug configuration name per workspace.</summary>
        public List<WorkspaceSettingEntry> DebugLastConfigurations { get; set; }

        public string GetPythonInterpreterFor(string workspaceRoot)
        {
            return GetWorkspaceValue(PythonInterpreters, workspaceRoot);
        }

        public void SetPythonInterpreterFor(string workspaceRoot, string interpreterPath)
        {
            if (PythonInterpreters == null) PythonInterpreters = new List<WorkspaceSettingEntry>();
            SetWorkspaceValue(PythonInterpreters, workspaceRoot, interpreterPath);
        }

        public string GetLastDebugConfiguration(string workspaceRoot)
        {
            return GetWorkspaceValue(DebugLastConfigurations, workspaceRoot);
        }

        public void SetLastDebugConfiguration(string workspaceRoot, string name)
        {
            if (DebugLastConfigurations == null) DebugLastConfigurations = new List<WorkspaceSettingEntry>();
            SetWorkspaceValue(DebugLastConfigurations, workspaceRoot, name);
        }

        private static string GetWorkspaceValue(List<WorkspaceSettingEntry> list, string workspaceRoot)
        {
            if (list == null) return null;
            string key = DebugPaths.Key(workspaceRoot ?? "");
            foreach (WorkspaceSettingEntry e in list)
                if (e != null && string.Equals(DebugPaths.Key(e.Workspace ?? ""), key, StringComparison.Ordinal)) return e.Value;
            return null;
        }

        private static void SetWorkspaceValue(List<WorkspaceSettingEntry> list, string workspaceRoot, string value)
        {
            string key = DebugPaths.Key(workspaceRoot ?? "");
            for (int i = list.Count - 1; i >= 0; i--)
                if (list[i] == null || string.Equals(DebugPaths.Key(list[i].Workspace ?? ""), key, StringComparison.Ordinal)) list.RemoveAt(i);
            if (!string.IsNullOrEmpty(value))
            {
                WorkspaceSettingEntry e = new WorkspaceSettingEntry();
                e.Workspace = workspaceRoot ?? "";
                e.Value = value;
                list.Add(e);
            }
            while (list.Count > 200) list.RemoveAt(0);
        }
    }
}
