using System;
using System.Collections.Generic;
using System.IO;
using codeLess.Studio;

namespace codeLess.Core
{
    public static class VscodeSettingsManager
    {
        public static string GetUserSettingsPath()
        {
            return Path.Combine(Path.GetDirectoryName(IdePersistentSettings.GetSettingsFilePath()), "user-settings.json");
        }

        public static string GetWorkspaceSettingsPath(string workspaceRoot)
        {
            if (string.IsNullOrEmpty(workspaceRoot)) return null;
            return Path.Combine(workspaceRoot, ".vscode", "settings.json");
        }

        public static void ApplySettings(string jsonContent, IdePersistentSettings target)
        {
            if (string.IsNullOrWhiteSpace(jsonContent)) return;
            var parsed = JsoncParser.Parse(jsonContent, out string err);
            if (err == null && parsed is Dictionary<string, object> dict)
            {
                if (dict.ContainsKey("workbench.colorTheme")) target.ActiveThemeId = dict["workbench.colorTheme"]?.ToString();
                if (dict.ContainsKey("editor.fontSize")) target.EditorFontSize = Convert.ToSingle(dict["editor.fontSize"]);
                if (dict.ContainsKey("editor.fontFamily")) target.EditorFontFamily = dict["editor.fontFamily"]?.ToString();
                if (dict.ContainsKey("editor.tabSize")) target.TabSize = Convert.ToInt32(dict["editor.tabSize"]);
                if (dict.ContainsKey("editor.insertSpaces")) target.InsertSpaces = Convert.ToBoolean(dict["editor.insertSpaces"]);
                if (dict.ContainsKey("editor.wordWrap")) {
                    object w = dict["editor.wordWrap"];
                    target.WordWrap = w != null && (w.ToString() == "on" || w.ToString() == "bounded" || w.ToString() == "wordWrapColumn" || w.ToString() == "True");
                }
                if (dict.ContainsKey("editor.minimap.enabled")) target.ShowMinimap = Convert.ToBoolean(dict["editor.minimap.enabled"]);
                if (dict.ContainsKey("editor.cursorBlinking")) target.EditorCursorBlinking = dict["editor.cursorBlinking"]?.ToString();
                // terminal
                if (dict.ContainsKey("terminal.integrated.fontSize")) target.TerminalFontSize = Convert.ToSingle(dict["terminal.integrated.fontSize"]);
                
                if (dict.ContainsKey("editor.formatOnSave")) target.FormatOnSave = Convert.ToBoolean(dict["editor.formatOnSave"]);
                if (dict.ContainsKey("files.autoSave")) {
                    string mode = dict["files.autoSave"]?.ToString();
                    target.SetAutoSaveMode(mode);
                }
                if (dict.ContainsKey("files.trimTrailingWhitespace")) target.TrimTrailingWhitespaceOnSave = Convert.ToBoolean(dict["files.trimTrailingWhitespace"]);
                if (dict.ContainsKey("files.insertFinalNewline")) target.InsertFinalNewlineOnSave = Convert.ToBoolean(dict["files.insertFinalNewline"]);
                if (dict.ContainsKey("files.exclude")) {
                    if (dict["files.exclude"] is Dictionary<string, object> ex) {
                        var keys = new List<string>();
                        foreach(var kv in ex) if (Convert.ToBoolean(kv.Value)) keys.Add(kv.Key);
                        if (keys.Count > 0) target.ExcludedFolders = string.Join(", ", keys);
                    }
                }
            }
        }
    }
}
