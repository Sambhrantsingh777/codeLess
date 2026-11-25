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
            
            string updated = WriteSettingJson(json, key, value);
            string tmpPath = path + ".tmp";
            File.WriteAllText(tmpPath, updated);
            File.Replace(tmpPath, path, null);
        }

        public static string WriteSettingJson(string json, string key, object value)
        {
            if (string.IsNullOrWhiteSpace(json)) json = "{\n}";
            
            string valStr = "";
            if (value is bool) valStr = ((bool)value) ? "true" : "false";
            else if (value is string) {
                string s = (string)value;
                s = s.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n").Replace("\r", "\\r").Replace("\t", "\\t");
                valStr = "\"" + s + "\"";
            }
            else if (value is System.Collections.IEnumerable && !(value is string))
            {
                var parts = new System.Collections.Generic.List<string>();
                foreach (object e in (System.Collections.IEnumerable)value)
                {
                     if (e is string) {
                         string s = (string)e;
                         s = s.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n").Replace("\r", "\\r").Replace("\t", "\\t");
                         parts.Add("\"" + s + "\"");
                     }
                     else if (e is bool) parts.Add(((bool)e) ? "true" : "false");
                     else if (e != null) parts.Add(Convert.ToString(e, System.Globalization.CultureInfo.InvariantCulture));
                     else parts.Add("null");
                }
                valStr = "[" + string.Join(", ", parts) + "]";
            }
            else if (value != null) valStr = Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture);
            else valStr = "null";

            // Simple scanner to find the top-level property and its value span.
            int keyStart = -1;
            int valueStart = -1;
            int valueEnd = -1;
            
            int depth = 0;
            int arrayDepth = 0;
            bool inString = false;
            bool escape = false;
            int i = 0;
            bool foundKey = false;
            int propertyValueStart = -1;
            
            while (i < json.Length)
            {
                char c = json[i];
                if (inString)
                {
                    if (escape) escape = false;
                    else if (c == '\\') escape = true;
                    else if (c == '"')
                    {
                        inString = false;
                        if (depth == 1 && !foundKey && keyStart != -1)
                        {
                            string s = json.Substring(keyStart + 1, i - keyStart - 1);
                            if (s == key)
                            {
                                foundKey = true;
                            }
                            keyStart = -1;
                        }
                    }
                    i++;
                }
                else
                {
                    if (c == '/' && i + 1 < json.Length && (json[i + 1] == '/' || json[i + 1] == '*'))
                    {
                        if (json[i + 1] == '/')
                        {
                            while (i < json.Length && json[i] != '\n') i++;
                        }
                        else
                        {
                            i += 2;
                            while (i + 1 < json.Length && !(json[i] == '*' && json[i + 1] == '/')) i++;
                            i += 2;
                        }
                        continue;
                    }

                    if (foundKey && propertyValueStart != -1)
                    {
                        if (depth == 1 && arrayDepth == 0 && (c == ',' || c == '}'))
                        {
                            valueEnd = i;
                            break;
                        }
                    }

                    if (c == '{') depth++;
                    else if (c == '}') depth--;
                    else if (c == '[') arrayDepth++;
                    else if (c == ']') arrayDepth--;
                    else if (c == '"')
                    {
                        inString = true;
                        if (depth == 1 && !foundKey) keyStart = i;
                    }
                    
                    if (foundKey)
                    {
                        if (valueStart == -1)
                        {
                            if (c == ':') valueStart = i + 1;
                        }
                        else
                        {
                            if (propertyValueStart == -1)
                            {
                                if (!char.IsWhiteSpace(c))
                                {
                                    propertyValueStart = i;
                                }
                            }
                        }
                    }
                    i++;
                }
            }

            if (foundKey && propertyValueStart != -1 && valueEnd != -1)
            {
                // Trim trailing whitespace from the value range if we hit a comma
                int end = valueEnd - 1;
                while (end >= propertyValueStart && char.IsWhiteSpace(json[end])) end--;
                json = json.Substring(0, propertyValueStart) + valStr + json.Substring(end + 1);
            }
            else
            {
                // Insert before the last '}'
                int lastBrace = json.LastIndexOf('}');
                if (lastBrace >= 0)
                {
                    string insert = "\"" + key + "\": " + valStr;
                    // check if we need a comma
                    string before = json.Substring(0, lastBrace);
                    int b = before.Length - 1;
                    while (b >= 0 && char.IsWhiteSpace(before[b])) b--;
                    
                    bool needsComma = false;
                    if (b >= 0 && before[b] != '{' && before[b] != ',')
                    {
                        needsComma = true;
                    }
                    
                    if (needsComma) insert = ",\n  " + insert + "\n";
                    else insert = "  " + insert + "\n";
                    
                    json = json.Substring(0, lastBrace) + insert + json.Substring(lastBrace);
                }
            }

            return json;
        }

        public static string ApplySettings(string jsonContent, IdePersistentSettings target)
        {
            if (string.IsNullOrWhiteSpace(jsonContent)) return null;
            string err;
            var parsed = JsoncParser.Parse(jsonContent, out err);
            if (err != null) return err;
            if (parsed == null) return "Invalid JSON: null root object";
            Dictionary<string, object> dict = parsed as Dictionary<string, object>;
            if (dict == null) return "Invalid JSON: expected a JSON object";
            if (dict.ContainsKey("invalid")) return "Invalid format: missing value";
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
                    target.EditorRenderWhitespace = s ?? "selection";
                }
                if (dict.ContainsKey("editor.emptySelectionClipboard")) target.EditorEmptySelectionClipboard = Convert.ToBoolean(dict["editor.emptySelectionClipboard"]);
                if (dict.ContainsKey("editor.useTabStops")) target.EditorUseTabStops = Convert.ToBoolean(dict["editor.useTabStops"]);
                if (dict.ContainsKey("editor.acceptSuggestionOnEnter")) {
                    object v = dict["editor.acceptSuggestionOnEnter"];
                    if (v is bool) target.EditorAcceptSuggestionOnEnter = (bool)v;
                    else if (v != null) target.EditorAcceptSuggestionOnEnter = !string.Equals(v.ToString(), "off", StringComparison.OrdinalIgnoreCase) && !string.Equals(v.ToString(), "false", StringComparison.OrdinalIgnoreCase);
                }
                if (dict.ContainsKey("editor.cursorSurroundingLines")) target.EditorCursorSurroundingLines = Convert.ToInt32(dict["editor.cursorSurroundingLines"]);
                if (dict.ContainsKey("editor.wordSeparators") && dict["editor.wordSeparators"] != null) target.EditorWordSeparators = dict["editor.wordSeparators"].ToString();
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
