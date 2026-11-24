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
            return Path.Combine(Path.GetDirectoryName(IdePersistentSettings.GetSettingsFilePath()), "User", "settings.json");
        }

        public static string GetWorkspaceSettingsPath(string workspaceRoot)
        {
            if (string.IsNullOrEmpty(workspaceRoot)) return null;
            return Path.Combine(workspaceRoot, ".vscode", "settings.json");
        }

        
        public static void WriteSetting(string key, object value)
        {
            string path = GetUserSettingsPath();
            string json = "";
            try { if (File.Exists(path)) json = File.ReadAllText(path); } catch { }
            if (string.IsNullOrWhiteSpace(json)) json = "{\n}";
            
            // Minimal text edit preserving comments
            // We'll use a very simple regex replacement or insertion before the last }
            string valStr = "";
            if (value is bool) valStr = ((bool)value) ? "true" : "false";
            else if (value is string) valStr = "\"" + ((string)value).Replace("\"", "\\\"") + "\"";
            else if (value != null) valStr = value.ToString();
            else valStr = "null";
            
            var regex = new System.Text.RegularExpressions.Regex(@"""" + key.Replace(".", @"\.") + @"""\s*:\s*([^,}]+)");
            if (regex.IsMatch(json))
            {
                json = regex.Replace(json, "\"" + key + "\": " + valStr);
            }
            else
            {
                int lastBrace = json.LastIndexOf('}');
                if (lastBrace >= 0)
                {
                    string insert = "\"" + key + "\": " + valStr;
                    // check if we need a comma
                    string before = json.Substring(0, lastBrace).Trim();
                    if (before.Length > 0 && before[before.Length - 1] != '{' && before[before.Length - 1] != ',')
                    {
                        insert = ",\n  " + insert + "\n";
                    }
                    else
                    {
                        insert = "  " + insert + "\n";
                    }
                    json = json.Substring(0, lastBrace) + insert + json.Substring(lastBrace);
                }
            }
            
            try { File.WriteAllText(path, json); } catch { }
        }


        public static string ApplySettings(string jsonContent, IdePersistentSettings target)
        {
            if (string.IsNullOrWhiteSpace(jsonContent)) return null;
            string err;
            var parsed = JsoncParser.Parse(jsonContent, out err);
            if (err != null) return err;
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
                
                if (dict.ContainsKey("editor.rulers")) {
                    var rulers = dict["editor.rulers"] as object[];
                    if (rulers != null && rulers.Length > 0) target.TabSize = Convert.ToInt32(rulers[0]);
                }
                
                
                if (dict.ContainsKey("search.exclude")) {
                    Dictionary<string, object> sx = dict["search.exclude"] as Dictionary<string, object>;
                    if (sx != null) {
                        var keys = new List<string>();
                        foreach(var kv in sx) if (Convert.ToBoolean(kv.Value)) keys.Add(kv.Key);
                        if (keys.Count > 0) target.ExcludedFolders = string.Join(", ", keys);
                    }
                }
                
                                if (dict.ContainsKey("editor.renderWhitespace")) {
                    string s = dict["editor.renderWhitespace"] != null ? dict["editor.renderWhitespace"].ToString() : null;
                    target.ShowRenderWhitespace = s == "all";
                }
                if (dict.ContainsKey("editor.cursorStyle")) {
                    target.EditorCursorStyle = dict["editor.cursorStyle"] != null ? dict["editor.cursorStyle"].ToString() : null;
                }
                if (dict.ContainsKey("editor.lineNumbers")) {
                    string s = dict["editor.lineNumbers"] != null ? dict["editor.lineNumbers"].ToString() : null;
                    target.ShowLineNumbers = s != "off";
                }
                if (dict.ContainsKey("workbench.startupEditor")) {
                    string s = dict["workbench.startupEditor"] != null ? dict["workbench.startupEditor"].ToString() : null;
                    target.ShowWelcomeOnStartup = s == "welcomePage" || s == "newUntitledFile";
                }
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
            return null;
        }
    }
}
