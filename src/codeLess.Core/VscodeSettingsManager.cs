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
            string err;
            var parsed = JsoncParser.Parse(jsonContent, out err);
            Dictionary<string, object> dict = parsed as Dictionary<string, object>;
            if (err == null && dict != null)
            {
                if (dict.ContainsKey("workbench.colorTheme")) target.ActiveThemeId = dict["workbench.colorTheme"] != null ? dict["workbench.colorTheme"].ToString() : null;
                if (dict.ContainsKey("editor.fontSize")) target.EditorFontSize = Convert.ToSingle(dict["editor.fontSize"]);
                if (dict.ContainsKey("editor.fontFamily")) target.EditorFontFamily = dict["editor.fontFamily"] != null ? dict["editor.fontFamily"].ToString() : null;
                if (dict.ContainsKey("editor.tabSize")) target.TabSize = Convert.ToInt32(dict["editor.tabSize"]);
                if (dict.ContainsKey("editor.insertSpaces")) target.InsertSpaces = Convert.ToBoolean(dict["editor.insertSpaces"]);
                if (dict.ContainsKey("editor.wordWrap")) {
                    object w = dict["editor.wordWrap"];
                    target.WordWrap = w != null && (w.ToString() == "on" || w.ToString() == "bounded" || w.ToString() == "wordWrapColumn" || w.ToString() == "True");
                }
                if (dict.ContainsKey("editor.minimap.enabled")) target.ShowMinimap = Convert.ToBoolean(dict["editor.minimap.enabled"]);
                if (dict.ContainsKey("editor.cursorBlinking")) target.EditorCursorBlinking = dict["editor.cursorBlinking"] != null ? dict["editor.cursorBlinking"].ToString() : null;
                // terminal
                if (dict.ContainsKey("terminal.integrated.fontSize")) target.TerminalFontSize = Convert.ToSingle(dict["terminal.integrated.fontSize"]);
                
                if (dict.ContainsKey("editor.formatOnSave")) target.FormatOnSave = Convert.ToBoolean(dict["editor.formatOnSave"]);
                if (dict.ContainsKey("files.autoSave")) {
                    string mode = dict["files.autoSave"] != null ? dict["files.autoSave"].ToString() : null;
                    target.SetAutoSaveMode(mode);
                }
                if (dict.ContainsKey("files.trimTrailingWhitespace")) target.TrimTrailingWhitespaceOnSave = Convert.ToBoolean(dict["files.trimTrailingWhitespace"]);
                if (dict.ContainsKey("files.insertFinalNewline")) target.InsertFinalNewlineOnSave = Convert.ToBoolean(dict["files.insertFinalNewline"]);
                if (dict.ContainsKey("files.exclude")) {
                    Dictionary<string, object> ex = dict["files.exclude"] as Dictionary<string, object>;
                    if (ex != null) {
                        var keys = new List<string>();
                        foreach(var kv in ex) if (Convert.ToBoolean(kv.Value)) keys.Add(kv.Key);
                        if (keys.Count > 0) target.ExcludedFolders = string.Join(", ", keys);
                    }
                }
            }
        }
    }
}
