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
                float fVal;
                int iVal;
                bool bVal;
                if (dict.ContainsKey("workbench.colorTheme")) target.ActiveThemeId = dict["workbench.colorTheme"] != null ? dict["workbench.colorTheme"].ToString() : null;
                if (dict.ContainsKey("editor.fontSize") && TryGetSingle(dict["editor.fontSize"], out fVal) && fVal > 0f) target.EditorFontSize = fVal;
                if (dict.ContainsKey("editor.fontFamily")) target.EditorFontFamily = dict["editor.fontFamily"] != null ? dict["editor.fontFamily"].ToString() : null;
                if (dict.ContainsKey("editor.tabSize") && TryGetInt32(dict["editor.tabSize"], out iVal) && iVal > 0) target.TabSize = iVal;
                if (dict.ContainsKey("editor.insertSpaces") && TryGetBoolean(dict["editor.insertSpaces"], out bVal)) target.InsertSpaces = bVal;
                if (dict.ContainsKey("editor.wordWrap")) {
                    object w = dict["editor.wordWrap"];
                    target.WordWrap = w != null && (w.ToString() == "on" || w.ToString() == "bounded" || w.ToString() == "wordWrapColumn" || w.ToString() == "True");
                }
                if (dict.ContainsKey("editor.minimap.enabled") && TryGetBoolean(dict["editor.minimap.enabled"], out bVal)) target.ShowMinimap = bVal;
                if (dict.ContainsKey("search.exclude")) {
                    Dictionary<string, object> sx = dict["search.exclude"] as Dictionary<string, object>;
                    if (sx != null) {
                        var keys = new List<string>();
                        foreach(var kv in sx) if (TryGetBoolean(kv.Value, out bVal) && bVal) keys.Add(kv.Key);
                        if (keys.Count > 0) target.ExcludedFolders = string.Join(", ", keys);
                    }
                }
                if (dict.ContainsKey("editor.renderWhitespace")) {
                    string s = dict["editor.renderWhitespace"] != null ? dict["editor.renderWhitespace"].ToString() : null;
                    target.ShowRenderWhitespace = s == "all";
                    target.EditorRenderWhitespace = s ?? "selection";
                }
                if (dict.ContainsKey("editor.emptySelectionClipboard") && TryGetBoolean(dict["editor.emptySelectionClipboard"], out bVal)) target.EditorEmptySelectionClipboard = bVal;
                if (dict.ContainsKey("editor.useTabStops") && TryGetBoolean(dict["editor.useTabStops"], out bVal)) target.EditorUseTabStops = bVal;
                if (dict.ContainsKey("editor.acceptSuggestionOnEnter")) {
                    object v = dict["editor.acceptSuggestionOnEnter"];
                    if (v is bool) target.EditorAcceptSuggestionOnEnter = (bool)v;
                    else if (v != null) target.EditorAcceptSuggestionOnEnter = !string.Equals(v.ToString(), "off", StringComparison.OrdinalIgnoreCase) && !string.Equals(v.ToString(), "false", StringComparison.OrdinalIgnoreCase);
                }
                if (dict.ContainsKey("editor.cursorSurroundingLines") && TryGetInt32(dict["editor.cursorSurroundingLines"], out iVal) && iVal >= 0) target.EditorCursorSurroundingLines = iVal;
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
                if (dict.ContainsKey("terminal.integrated.fontSize") && TryGetSingle(dict["terminal.integrated.fontSize"], out fVal) && fVal > 0f) target.TerminalFontSize = fVal;
                if (dict.ContainsKey("editor.formatOnSave") && TryGetBoolean(dict["editor.formatOnSave"], out bVal)) target.FormatOnSave = bVal;
                if (dict.ContainsKey("files.autoSave")) {
                    string mode = dict["files.autoSave"] != null ? dict["files.autoSave"].ToString() : null;
                    target.SetAutoSaveMode(mode);
                }
                if (dict.ContainsKey("files.autoSaveDelay") && TryGetInt32(dict["files.autoSaveDelay"], out iVal) && iVal > 0) {
                    target.AutoSaveDelayMs = iVal;
                }
                if (dict.ContainsKey("git.autofetch")) {
                    object af = dict["git.autofetch"];
                    target.GitAutoFetch = af != null && !string.Equals(af.ToString(), "false", StringComparison.OrdinalIgnoreCase) && !string.Equals(af.ToString(), "off", StringComparison.OrdinalIgnoreCase);
                }
                if (dict.ContainsKey("git.rebaseWhenSync") && TryGetBoolean(dict["git.rebaseWhenSync"], out bVal)) {
                    target.GitRebaseWhenSync = bVal;
                }
                if (dict.ContainsKey("codeless.agent.nativeToolCalling") && TryGetBoolean(dict["codeless.agent.nativeToolCalling"], out bVal)) {
                    target.NativeToolCalling = bVal;
                }
                if (dict.ContainsKey("files.hotExit")) {
                    string he = dict["files.hotExit"] != null ? dict["files.hotExit"].ToString() : null;
                    target.SetHotExitMode(he);
                }
                if (dict.ContainsKey("files.eol")) {
                    target.FilesEol = dict["files.eol"] != null ? dict["files.eol"].ToString() : "auto";
                }
                if (dict.ContainsKey("files.autoGuessEncoding") && TryGetBoolean(dict["files.autoGuessEncoding"], out bVal)) {
                    target.AutoGuessEncoding = bVal;
                }
                if (dict.ContainsKey("files.trimFinalNewlines") && TryGetBoolean(dict["files.trimFinalNewlines"], out bVal)) {
                    target.TrimFinalNewlinesOnSave = bVal;
                }
                if (dict.ContainsKey("files.maxMemoryForLargeFilesMB") && TryGetInt32(dict["files.maxMemoryForLargeFilesMB"], out iVal) && iVal > 0) {
                    target.LargeFileThresholdMB = iVal;
                }
                if (dict.ContainsKey("files.trimTrailingWhitespace") && TryGetBoolean(dict["files.trimTrailingWhitespace"], out bVal)) target.TrimTrailingWhitespaceOnSave = bVal;
                if (dict.ContainsKey("files.insertFinalNewline") && TryGetBoolean(dict["files.insertFinalNewline"], out bVal)) target.InsertFinalNewlineOnSave = bVal;
                if (dict.ContainsKey("files.exclude")) {
                    Dictionary<string, object> ex = dict["files.exclude"] as Dictionary<string, object>;
                    if (ex != null) {
                        var keys = new List<string>();
                        foreach(var kv in ex) if (TryGetBoolean(kv.Value, out bVal) && bVal) keys.Add(kv.Key);
                        if (keys.Count > 0) target.ExcludedFolders = string.Join(", ", keys);
                    }
                }
                if (dict.ContainsKey("codeless.mcp") || dict.ContainsKey("mcp"))
                {
                    if (target.McpServers == null)
                        target.McpServers = new Dictionary<string, McpServerConfig>(StringComparer.OrdinalIgnoreCase);
                    McpConfigLoader.MergeFromRootDictionary(dict, target.McpServers);
                }
            }
            return null;
        }

        private static bool TryGetSingle(object val, out float result)
        {
            result = 0f;
            if (val == null || val is bool || val is IDictionary<string, object> || val is System.Collections.IEnumerable && !(val is string))
                return false;
            try
            {
                float f = Convert.ToSingle(val, System.Globalization.CultureInfo.InvariantCulture);
                if (float.IsNaN(f) || float.IsInfinity(f)) return false;
                result = f;
                return true;
            }
            catch { return false; }
        }

        private static bool TryGetInt32(object val, out int result)
        {
            result = 0;
            if (val == null || val is bool || val is IDictionary<string, object> || val is System.Collections.IEnumerable && !(val is string))
                return false;
            try
            {
                result = Convert.ToInt32(val, System.Globalization.CultureInfo.InvariantCulture);
                return true;
            }
            catch { return false; }
        }

        private static bool TryGetBoolean(object val, out bool result)
        {
            result = false;
            if (val == null) return false;
            if (val is bool) { result = (bool)val; return true; }
            string s = val as string;
            if (s != null) return bool.TryParse(s, out result);
            return false;
        }
    }
}
