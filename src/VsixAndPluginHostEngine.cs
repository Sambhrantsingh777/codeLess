using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace codeLess.Studio
{
    public class VsixAndPluginHostEngine
    {
        public List<VsixExtensionPackage> Extensions = new List<VsixExtensionPackage>();
        public List<CodeSnippetItem> AllLoadedSnippets = new List<CodeSnippetItem>();
        public List<string> HostLogs = new List<string>();
        private string extensionsRootDir;
        private string hostBridgeScriptPath;

        public event Action<string> OnHostLog;

        public VsixAndPluginHostEngine(List<StudioTheme> workbenchThemes) : this(workbenchThemes, null)
        {
        }

        /// <summary><paramref name="extensionsRoot"/>: folder of installed extensions (null = ~/.codeless/extensions; tests use a temp folder).</summary>
        public VsixAndPluginHostEngine(List<StudioTheme> workbenchThemes, string extensionsRoot)
        {
            string userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            extensionsRootDir = !string.IsNullOrEmpty(extensionsRoot) ? extensionsRoot : Path.Combine(userProfile, ".codeless", "extensions");
            string hostDir = !string.IsNullOrEmpty(extensionsRoot) ? Path.Combine(extensionsRoot, ".host") : Path.Combine(userProfile, ".codeless", "host");
            try
            {
                if (!Directory.Exists(extensionsRootDir)) Directory.CreateDirectory(extensionsRootDir);
                if (!Directory.Exists(hostDir)) Directory.CreateDirectory(hostDir);
                hostBridgeScriptPath = Path.Combine(hostDir, "vscode-shim-host.js");
                ProvisionCleanRoomNodeHostScript();
            }
            catch { }

            InitializeCatalog(workbenchThemes);
        }

        private void LogHost(string message)
        {
            string entry = "[" + DateTime.Now.ToString("HH:mm:ss") + "] [vscode-shim-host] " + message;
            HostLogs.Add(entry);
            if (OnHostLog != null) OnHostLog(entry);
        }

        private void ProvisionCleanRoomNodeHostScript()
        {
            string js = @"// codeLess Clean-Room VS Code Extension Host Bridge (Zero-Microsoft Binary Dependency)
const Module = require('module');
const path = require('path');
const fs = require('fs');

const registeredCommands = new Map();
const outputLogs = [];

const vscodeShim = {
  version: '1.96.0-codeless-native',
  StatusBarAlignment: { Left: 1, Right: 2 },
  ViewColumn: { Active: -1, Beside: -2, One: 1, Two: 2 },
  Uri: {
    file: (p) => ({ fsPath: p, path: p, scheme: 'file', toString: () => 'file://' + p }),
    parse: (u) => ({ fsPath: u, path: u, scheme: 'file', toString: () => u })
  },
  commands: {
    registerCommand: (id, handler) => {
      registeredCommands.set(id, handler);
      outputLogs.push('Registered command: ' + id);
      return { dispose: () => registeredCommands.delete(id) };
    },
    executeCommand: async (id, ...args) => {
      if (registeredCommands.has(id)) {
        return await registeredCommands.get(id)(...args);
      }
      return null;
    }
  },
  window: {
    showInformationMessage: (msg) => { outputLogs.push('[INFO] ' + msg); return Promise.resolve(); },
    showWarningMessage: (msg) => { outputLogs.push('[WARN] ' + msg); return Promise.resolve(); },
    showErrorMessage: (msg) => { outputLogs.push('[ERROR] ' + msg); return Promise.resolve(); },
    createOutputChannel: (name) => ({
      name,
      append: (v) => outputLogs.push('[' + name + '] ' + v),
      appendLine: (v) => outputLogs.push('[' + name + '] ' + v),
      show: () => {},
      dispose: () => {}
    }),
    createStatusBarItem: () => ({ text: '', tooltip: '', show: () => {}, hide: () => {}, dispose: () => {} })
  },
  workspace: {
    rootPath: process.cwd(),
    workspaceFolders: [{ uri: { fsPath: process.cwd() }, name: path.basename(process.cwd()), index: 0 }],
    getConfiguration: () => ({ get: (k, defVal) => defVal, update: () => Promise.resolve() }),
    onDidChangeTextDocument: () => ({ dispose: () => {} }),
    onDidSaveTextDocument: () => ({ dispose: () => {} })
  },
  languages: {
    registerCompletionItemProvider: () => ({ dispose: () => {} }),
    registerHoverProvider: () => ({ dispose: () => {} }),
    createDiagnosticCollection: () => ({ set: () => {}, clear: () => {}, dispose: () => {} })
  }
};

const origLoad = Module._load;
Module._load = function(request, parent, isMain) {
  if (request === 'vscode') return vscodeShim;
  return origLoad.apply(this, arguments);
};

async function main() {
  const action = process.argv[2] || 'ping';
  const target = process.argv[3] || '';
  const cmdToRun = process.argv[4] || '';
  try {
    if (action === 'activate' && target && fs.existsSync(target)) {
      const extModule = require(target);
      const ctx = { subscriptions: [], extensionPath: path.dirname(target), globalState: { get: () => undefined, update: () => Promise.resolve() }, workspaceState: { get: () => undefined, update: () => Promise.resolve() } };
      if (extModule && typeof extModule.activate === 'function') {
        await extModule.activate(ctx);
        outputLogs.push('Activated extension module: ' + path.basename(target));
      }
      if (cmdToRun && registeredCommands.has(cmdToRun)) {
        const res = await registeredCommands.get(cmdToRun)();
        outputLogs.push('Executed command ' + cmdToRun + (res ? ': ' + JSON.stringify(res) : ''));
      }
    } else {
      outputLogs.push('codeLess Clean-Room vscode.* API Host Online (Node ' + process.version + ')');
    }
    console.log(JSON.stringify({ ok: true, commands: Array.from(registeredCommands.keys()), logs: outputLogs }));
  } catch (err) {
    console.log(JSON.stringify({ ok: false, error: String(err && err.stack || err), logs: outputLogs }));
  }
}
main();
";
            File.WriteAllText(hostBridgeScriptPath, js, Encoding.UTF8);
        }

        private void InitializeCatalog(List<StudioTheme> workbenchThemes)
        {
            AllLoadedSnippets = new List<CodeSnippetItem>
            {
                new CodeSnippetItem { Prefix = "fn", Body = "function executeTask(input) {\n    return input;\n}", Description = "Function declaration", Language = "js" },
                new CodeSnippetItem { Prefix = "afn", Body = "async function fetchResource(url) {\n    const res = await fetch(url);\n    return await res.json();\n}", Description = "Async function template", Language = "js" },
                new CodeSnippetItem { Prefix = "defmain", Body = "def main() -> None:\n    print(\"Executing codeLess task...\")\n\nif __name__ == \"__main__\":\n    main()", Description = "Python main entry point", Language = "py" },
                new CodeSnippetItem { Prefix = "cw", Body = "Console.WriteLine(\"Result: \");", Description = "Console.WriteLine", Language = "cs" },
                new CodeSnippetItem { Prefix = "tryc", Body = "try {\n    \n} catch (Exception ex) {\n    Console.Error.WriteLine(ex.Message);\n}", Description = "Structured try/catch block", Language = "cs" }
            };

            Extensions = new List<VsixExtensionPackage>
            {
                new VsixExtensionPackage
                {
                    Id = "codeless.sublime-mariana-pack",
                    Publisher = "codeless",
                    Name = "sublime-mariana-pack",
                    DisplayName = "Sublime Mariana & Monokai Studio Pack (VSIX)",
                    Description = "Brings authentic Sublime Text 4 Mariana and Monokai Pro color palettes and key-snippets.",
                    Version = "3.2.0",
                    IsInstalled = true,
                    IsActivated = true,
                    ContributedThemes = workbenchThemes.Where(t => t.Id.StartsWith("sublime-")).ToList()
                },
                new VsixExtensionPackage
                {
                    Id = "enkia.tokyo-night",
                    Publisher = "enkia",
                    Name = "tokyo-night",
                    DisplayName = "Tokyo Night Storm (VSIX Theme)",
                    Description = "Clean dark VS Code theme celebrating the lights of Downtown Tokyo.",
                    Version = "1.2.0",
                    IsInstalled = true,
                    IsActivated = true,
                    ContributedThemes = workbenchThemes.Where(t => t.Id == "tokyo-night").ToList()
                },
                new VsixExtensionPackage
                {
                    Id = "Catppuccin.catppuccin-vsc",
                    Publisher = "Catppuccin",
                    Name = "catppuccin-vsc",
                    DisplayName = "Catppuccin Mocha (VSIX Theme)",
                    Description = "Soothing pastel studio theme for high-contrast coding.",
                    Version = "3.15.0",
                    IsInstalled = true,
                    IsActivated = true,
                    ContributedThemes = workbenchThemes.Where(t => t.Id == "catppuccin-mocha").ToList()
                },
                new VsixExtensionPackage
                {
                    Id = "codeless-labs.vscode-ast-metrics",
                    Publisher = "codeless-labs",
                    Name = "vscode-ast-metrics",
                    DisplayName = "AST Complexity & Security Auditor (vscode.* API)",
                    Description = "Computes cyclomatic complexity, symbol density, and security checks via clean-room vscode.* host.",
                    Version = "2.4.0",
                    IsInstalled = true,
                    IsActivated = true,
                    ContributedCommands = new List<string> { "codelessMetrics.analyzeActiveFile" }
                }
            };

            LogHost("Initialized Clean-Room VS Code Extension Host (vscode v1.96 API shim ready).");
            ScanInstalledDirectories(workbenchThemes);
        }

        /// <summary>
        /// package.json, theme and snippet files of VS Code extensions are JSONC (comments, trailing commas): parsed
        /// leniently (QA #18). Returns null when the text is not a JSON object.
        /// </summary>
        public static Dictionary<string, object> ParseJsoncObject(string text)
        {
            if (string.IsNullOrEmpty(text)) return null;
            try
            {
                string err;
                if (text[0] == '\uFEFF') text = text.Substring(1);
                return DapJson.TryParse(text, out err) as Dictionary<string, object>;
            }
            catch { return null; }
        }

        private static string FindPackageJson(string dir)
        {
            string p = Path.Combine(Path.Combine(dir, "extension"), "package.json");
            if (File.Exists(p)) return p;
            p = Path.Combine(dir, "package.json");
            return File.Exists(p) ? p : null;
        }

        private static string JStr(Dictionary<string, object> d, string key, string fallback)
        {
            object v;
            if (d == null || !d.TryGetValue(key, out v) || v == null) return fallback;
            string s = Convert.ToString(v, System.Globalization.CultureInfo.InvariantCulture);
            return string.IsNullOrEmpty(s) ? fallback : s;
        }

        private static string ResolveInPackage(string pkgBaseDir, string rel)
        {
            string clean = (rel ?? "").Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar);
            while (clean.StartsWith("." + Path.DirectorySeparatorChar, StringComparison.Ordinal)) clean = clean.Substring(2);
            clean = clean.TrimStart(Path.DirectorySeparatorChar);
            string full = Path.GetFullPath(Path.Combine(pkgBaseDir, clean));
            string root = Path.GetFullPath(pkgBaseDir).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            return full.StartsWith(root, StringComparison.OrdinalIgnoreCase) ? full : null; // never outside the package
        }

        /// <summary>Folder that holds installed extensions (~/.codeless/extensions unless overridden).</summary>
        public string ExtensionsRoot { get { return extensionsRootDir; } }

        public void ScanInstalledDirectories(List<StudioTheme> workbenchThemes)
        {
            if (string.IsNullOrEmpty(extensionsRootDir) || !Directory.Exists(extensionsRootDir)) return;
            string[] dirs;
            try { dirs = Directory.GetDirectories(extensionsRootDir); } catch { return; }
            Array.Sort(dirs, StringComparer.OrdinalIgnoreCase);
            foreach (string dir in dirs)
            {
                if (Path.GetFileName(dir).StartsWith(".", StringComparison.Ordinal)) continue; // in-progress installs
                try { LoadInstalledPackage(dir, workbenchThemes); }
                catch (Exception ex) { LogHost("Skipped " + Path.GetFileName(dir) + ": " + ex.Message); }
            }
        }

        private VsixExtensionPackage LoadInstalledPackage(string dir, List<StudioTheme> workbenchThemes)
        {
            string pkgJsonPath = FindPackageJson(dir);
            if (pkgJsonPath == null) return null;
            Dictionary<string, object> pkg = ParseJsoncObject(File.ReadAllText(pkgJsonPath));
            if (pkg == null)
            {
                LogHost("Skipped " + Path.GetFileName(dir) + ": package.json is not valid JSON.");
                return null;
            }

            string pub = JStr(pkg, "publisher", "local");
            string name = JStr(pkg, "name", Path.GetFileName(dir));
            string disp = JStr(pkg, "displayName", name);
            string ver = JStr(pkg, "version", "1.0.0");
            string desc = JStr(pkg, "description", "");
            string mainScript = JStr(pkg, "main", "");
            string id = pub + "." + name;

            VsixExtensionPackage existing = Extensions.FirstOrDefault(x => x.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
            if (existing != null && existing.IsInstalled && !string.IsNullOrEmpty(existing.InstallPath) && Directory.Exists(existing.InstallPath))
                return existing; // already loaded
            // A marketplace search result (or a built-in catalog entry) is replaced by the installed package.
            if (existing != null) Extensions.Remove(existing);

            string pkgBaseDir = Path.GetDirectoryName(pkgJsonPath);
            string resolvedMain = !string.IsNullOrEmpty(mainScript) ? (ResolveInPackage(pkgBaseDir, mainScript) ?? "") : "";

            VsixExtensionPackage extPkg = new VsixExtensionPackage
            {
                Id = id,
                Publisher = pub,
                Name = name,
                DisplayName = disp,
                Version = ver,
                Description = desc,
                InstallPath = dir,
                MainEntryScript = resolvedMain,
                IsInstalled = true,
                IsActivated = true
            };

            Dictionary<string, object> contrib = pkg.ContainsKey("contributes") ? pkg["contributes"] as Dictionary<string, object> : null;
            if (contrib != null)
            {
                IList themes = contrib.ContainsKey("themes") ? contrib["themes"] as IList : null;
                if (themes != null)
                {
                    foreach (object thObj in themes)
                    {
                        Dictionary<string, object> thDict = thObj as Dictionary<string, object>;
                        string thFull = thDict != null ? ResolveInPackage(pkgBaseDir, JStr(thDict, "path", "")) : null;
                        if (thFull == null) continue;
                        StudioTheme parsedTheme = TryParseVsixThemeJson(thFull, JStr(thDict, "label", JStr(thDict, "id", disp)), JStr(thDict, "uiTheme", null));
                        if (parsedTheme == null)
                        {
                            LogHost("Theme " + JStr(thDict, "path", "") + " of " + id + " could not be read.");
                            continue;
                        }
                        extPkg.ContributedThemes.Add(parsedTheme);
                        if (workbenchThemes != null)
                        {
                            workbenchThemes.RemoveAll(t => t.Id == parsedTheme.Id && t.Id.StartsWith("vsix-", StringComparison.Ordinal));
                            if (!workbenchThemes.Any(t => t.Id == parsedTheme.Id)) workbenchThemes.Add(parsedTheme);
                        }
                    }
                }

                // TextMate grammars ("languages" + "grammars") for syntax highlighting in both editors
                try { TextMateGrammarRegistry.Shared.RegisterVsixContributions(pkgBaseDir, contrib); } catch { }

                IList cmds = contrib.ContainsKey("commands") ? contrib["commands"] as IList : null;
                if (cmds != null)
                    foreach (object cmdObj in cmds)
                    {
                        Dictionary<string, object> cmdDict = cmdObj as Dictionary<string, object>;
                        if (cmdDict != null && cmdDict.ContainsKey("command")) extPkg.ContributedCommands.Add(JStr(cmdDict, "command", ""));
                    }

                IList snippets = contrib.ContainsKey("snippets") ? contrib["snippets"] as IList : null;
                if (snippets != null)
                    foreach (object snObj in snippets)
                    {
                        Dictionary<string, object> snDict = snObj as Dictionary<string, object>;
                        string snFull = snDict != null ? ResolveInPackage(pkgBaseDir, JStr(snDict, "path", "")) : null;
                        if (snFull == null) continue;
                        foreach (CodeSnippetItem cs in TryParseVsixSnippetsJson(snFull, JStr(snDict, "language", "")))
                        {
                            extPkg.ContributedSnippets.Add(cs);
                            if (!AllLoadedSnippets.Any(s => s.Prefix == cs.Prefix && s.Language == cs.Language))
                                AllLoadedSnippets.Add(cs);
                        }
                    }
            }

            Extensions.Add(extPkg);
            LogHost("Loaded installed VSIX extension: " + id + " v" + ver);
            return extPkg;
        }

        public VsixExtensionPackage LoadPluginFromDirectory(string dir, List<StudioTheme> workbenchThemes)
        {
            if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) return null;
            string pkgPath = FindPackageJson(dir);
            Dictionary<string, object> pkg = pkgPath != null ? ParseJsoncObject(File.ReadAllText(pkgPath)) : null;
            if (pkg == null) { LogHost("No valid package.json in " + dir); return null; }
            string id = JStr(pkg, "publisher", "local") + "." + JStr(pkg, "name", Path.GetFileName(dir));
            string targetSub = Path.Combine(extensionsRootDir, id + "-" + JStr(pkg, "version", "1.0.0"));
            try
            {
                if (!string.Equals(Path.GetFullPath(dir).TrimEnd('\\', '/'), Path.GetFullPath(targetSub).TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase))
                {
                    RemoveInstalledCopies(id, workbenchThemes);
                    foreach (string f in Directory.GetFiles(dir, "*", SearchOption.AllDirectories))
                    {
                        string rel = f.Substring(dir.TrimEnd('\\', '/').Length).TrimStart('\\', '/');
                        string dest = Path.Combine(targetSub, rel);
                        string destFolder = Path.GetDirectoryName(dest);
                        if (!Directory.Exists(destFolder)) Directory.CreateDirectory(destFolder);
                        File.Copy(f, dest, true);
                    }
                }
            }
            catch (Exception ex) { LogHost("Loading " + dir + " failed: " + ex.Message); }
            ScanInstalledDirectories(workbenchThemes);
            return Extensions.FirstOrDefault(x => x.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>
        /// VS Code snippet file (JSONC). "prefix" may be a string or a list (every prefix is offered); "body" a string or
        /// list of lines; "scope" (comma-separated language ids) overrides the contribution's language.
        /// </summary>
        public List<CodeSnippetItem> TryParseVsixSnippetsJson(string snippetsJsonPath, string defaultLang)
        {
            List<CodeSnippetItem> list = new List<CodeSnippetItem>();
            try
            {
                if (!File.Exists(snippetsJsonPath)) return list;
                Dictionary<string, object> root = ParseJsoncObject(File.ReadAllText(snippetsJsonPath));
                if (root == null) { LogHost("Snippet file " + Path.GetFileName(snippetsJsonPath) + " is not valid JSON."); return list; }

                foreach (KeyValuePair<string, object> kv in root)
                {
                    Dictionary<string, object> entry = kv.Value as Dictionary<string, object>;
                    if (entry == null || !entry.ContainsKey("prefix") || !entry.ContainsKey("body")) continue;
                    List<string> prefixes = new List<string>();
                    IList pl = entry["prefix"] as IList;
                    if (pl != null) { foreach (object o in pl) if (o != null) prefixes.Add(Convert.ToString(o)); }
                    else if (entry["prefix"] != null) prefixes.Add(Convert.ToString(entry["prefix"]));
                    IList bl = entry["body"] as IList;
                    string body = bl != null
                        ? string.Join("\n", bl.Cast<object>().Select(o => o == null ? "" : Convert.ToString(o)).ToArray())
                        : Convert.ToString(entry["body"]);
                    string desc = JStr(entry, "description", kv.Key);
                    string scope = JStr(entry, "scope", null);
                    string lang = !string.IsNullOrEmpty(scope) ? scope : (string.IsNullOrEmpty(defaultLang) ? "" : defaultLang);
                    if (string.IsNullOrWhiteSpace(body)) continue;
                    foreach (string prefix in prefixes)
                    {
                        if (string.IsNullOrWhiteSpace(prefix)) continue;
                        list.Add(new CodeSnippetItem { Prefix = prefix.Trim(), Body = body, Description = desc, Language = lang });
                    }
                }
            }
            catch { }
            return list;
        }

        public StudioTheme TryParseVsixThemeJson(string themeJsonPath, string label)
        {
            return TryParseVsixThemeJson(themeJsonPath, label, null);
        }

        /// <summary>
        /// Reads a VS Code color theme (JSONC, "include" chains, "type" / uiTheme light-dark-hc) into a workbench theme:
        /// workbench colors mapped to codeLess roles (alpha blended over the editor), tokenColors for TextMate scopes
        /// plus the fallback tokenizer's roles. Returns null when the file can't be read.
        /// </summary>
        public StudioTheme TryParseVsixThemeJson(string themeJsonPath, string label, string uiTheme)
        {
            try
            {
                if (!File.Exists(themeJsonPath)) return null;
                Dictionary<string, string> colors = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                string type = null;
                if (!ReadThemeColors(themeJsonPath, colors, ref type, 0)) return null;
                if (string.IsNullOrEmpty(type) && !string.IsNullOrEmpty(uiTheme))
                    type = uiTheme == "vs" || uiTheme == "hc-light" ? "light" : "dark";

                bool wantLight = type == "light" || type == "hc-light";
                string editorBg;
                if (colors.TryGetValue("editor.background", out editorBg) && type == null)
                    wantLight = StudioTheme.Hex(editorBg, Color.Black).R > 180;
                List<StudioTheme> builtIns = StudioTheme.GetBuiltInThemes();
                StudioTheme baseTheme = builtIns.FirstOrDefault(t => t.IsLight == wantLight) ?? builtIns[0];

                string cleanLabel = (label ?? "Custom Theme").Trim();
                if (!cleanLabel.EndsWith("(VSIX)", StringComparison.OrdinalIgnoreCase))
                    cleanLabel += " (VSIX)";
                StudioTheme c = new StudioTheme
                {
                    Id = "vsix-" + Regex.Replace((label ?? "custom").ToLowerInvariant(), @"[^a-z0-9]+", "-").Trim('-'),
                    Name = cleanLabel,
                    BgWorkbench = baseTheme.BgWorkbench,
                    BgActivity = baseTheme.BgActivity,
                    BgSidebar = baseTheme.BgSidebar,
                    BgEditor = baseTheme.BgEditor,
                    BgElevated = baseTheme.BgElevated,
                    BgHover = baseTheme.BgHover,
                    BgLineHighlight = baseTheme.BgLineHighlight,
                    BgSelection = baseTheme.BgSelection,
                    BgStatus = baseTheme.BgStatus,
                    BorderSubtle = baseTheme.BorderSubtle,
                    AccentPrimary = baseTheme.AccentPrimary,
                    AccentSecondary = baseTheme.AccentSecondary,
                    FgPrimary = baseTheme.FgPrimary,
                    FgSecondary = baseTheme.FgSecondary,
                    FgMuted = baseTheme.FgMuted,
                    TokenKeyword = baseTheme.TokenKeyword,
                    TokenControl = baseTheme.TokenControl,
                    TokenFunction = baseTheme.TokenFunction,
                    TokenString = baseTheme.TokenString,
                    TokenComment = baseTheme.TokenComment,
                    TokenNumber = baseTheme.TokenNumber,
                    TokenType = baseTheme.TokenType,
                    TokenOperator = baseTheme.TokenOperator
                };

                c.BgEditor = ThemeColor(colors, c.BgEditor, c.BgEditor, "editor.background");
                c.FgPrimary = ThemeColor(colors, c.FgPrimary, c.BgEditor, "editor.foreground", "foreground");
                c.BgSidebar = ThemeColor(colors, c.BgSidebar, c.BgEditor, "sideBar.background");
                c.BgActivity = ThemeColor(colors, c.BgActivity, c.BgEditor, "activityBar.background");
                c.BgWorkbench = ThemeColor(colors, c.BgActivity, c.BgEditor, "titleBar.activeBackground", "activityBar.background");
                c.BgStatus = ThemeColor(colors, c.BgStatus, c.BgEditor, "statusBar.background");
                c.AccentPrimary = ThemeColor(colors, c.AccentPrimary, c.BgEditor, "button.background", "focusBorder", "activityBarBadge.background");
                c.AccentSecondary = ThemeColor(colors, c.AccentSecondary, c.BgEditor, "textLink.foreground", "editorCursor.foreground");
                c.BgElevated = ThemeColor(colors, c.BgElevated, c.BgEditor, "editorWidget.background", "dropdown.background", "menu.background");
                c.BgHover = ThemeColor(colors, c.BgHover, c.BgSidebar, "list.hoverBackground");
                c.BgLineHighlight = ThemeColor(colors, c.BgLineHighlight, c.BgEditor, "editor.lineHighlightBackground");
                c.BgSelection = ThemeColor(colors, c.BgSelection, c.BgEditor, "editor.selectionBackground");
                c.BorderSubtle = ThemeColor(colors, c.BorderSubtle, c.BgEditor, "editorGroup.border", "sideBar.border", "panel.border", "contrastBorder");
                c.FgSecondary = ThemeColor(colors, c.FgSecondary, c.BgEditor, "descriptionForeground", "sideBar.foreground");
                c.FgMuted = ThemeColor(colors, c.FgMuted, c.BgEditor, "editorLineNumber.foreground");

                // "tokenColors" drive TextMate scope colors when this theme is active (comments / trailing commas tolerated)
                try { c.VsixTokenColors = TextMateTheme.ReadTokenColors(themeJsonPath); } catch { }
                if (c.VsixTokenColors != null) ApplyTokenRoleColors(c, c.VsixTokenColors);
                return c;
            }
            catch
            {
                return null;
            }
        }

        private static bool ReadThemeColors(string path, Dictionary<string, string> into, ref string type, int depth)
        {
            if (depth > 4 || !File.Exists(path)) return false;
            Dictionary<string, object> root = ParseJsoncObject(File.ReadAllText(path));
            if (root == null) return false;
            string inc = JStr(root, "include", null);
            if (!string.IsNullOrEmpty(inc))
                ReadThemeColors(Path.Combine(Path.GetDirectoryName(path) ?? "", inc.Replace('/', Path.DirectorySeparatorChar)), into, ref type, depth + 1);
            string t = JStr(root, "type", null);
            if (!string.IsNullOrEmpty(t)) type = t.ToLowerInvariant();
            Dictionary<string, object> colors = root.ContainsKey("colors") ? root["colors"] as Dictionary<string, object> : null;
            if (colors != null)
                foreach (KeyValuePair<string, object> kv in colors)
                    if (kv.Value is string) into[kv.Key] = (string)kv.Value;
            return true;
        }

        private static Color ThemeColor(Dictionary<string, string> colors, Color fallback, Color under, params string[] keys)
        {
            foreach (string k in keys)
            {
                string v;
                Color parsed;
                if (colors.TryGetValue(k, out v) && TryParseCssColor(v, under, out parsed)) return parsed;
            }
            return fallback;
        }

        /// <summary>#rgb, #rgba, #rrggbb, #rrggbbaa (alpha blended over <paramref name="under"/>).</summary>
        public static bool TryParseCssColor(string s, Color under, out Color color)
        {
            color = under;
            if (string.IsNullOrEmpty(s)) return false;
            string h = s.Trim().TrimStart('#');
            if (h.Length == 3 || h.Length == 4)
            {
                StringBuilder sb = new StringBuilder();
                foreach (char ch in h) sb.Append(ch).Append(ch);
                h = sb.ToString();
            }
            if (h.Length != 6 && h.Length != 8) return false;
            int r, g, b, a = 255;
            if (!int.TryParse(h.Substring(0, 2), System.Globalization.NumberStyles.HexNumber, null, out r)) return false;
            if (!int.TryParse(h.Substring(2, 2), System.Globalization.NumberStyles.HexNumber, null, out g)) return false;
            if (!int.TryParse(h.Substring(4, 2), System.Globalization.NumberStyles.HexNumber, null, out b)) return false;
            if (h.Length == 8 && !int.TryParse(h.Substring(6, 2), System.Globalization.NumberStyles.HexNumber, null, out a)) return false;
            color = a >= 255 ? Color.FromArgb(r, g, b) : StudioTheme.Blend(under, Color.FromArgb(r, g, b), a / 255.0);
            return true;
        }

        /// <summary>Roles used by the fallback tokenizer (files without a TextMate grammar) from the theme's tokenColors.</summary>
        private static void ApplyTokenRoleColors(StudioTheme t, List<object> tokenColors)
        {
            foreach (object o in tokenColors)
            {
                Dictionary<string, object> rule = o as Dictionary<string, object>;
                if (rule == null) continue;
                Dictionary<string, object> settings = rule.ContainsKey("settings") ? rule["settings"] as Dictionary<string, object> : null;
                string fg = settings != null ? JStr(settings, "foreground", null) : null;
                Color col;
                if (fg == null || !TryParseCssColor(fg, t.BgEditor, out col)) continue;
                List<string> scopes = new List<string>();
                object sc = rule.ContainsKey("scope") ? rule["scope"] : null;
                if (sc is string) scopes.AddRange(((string)sc).Split(','));
                else if (sc is IList) foreach (object x in (IList)sc) if (x != null) scopes.Add(Convert.ToString(x));
                foreach (string raw in scopes)
                {
                    string s = raw.Trim();
                    if (s == "comment" || s == "punctuation.definition.comment") t.TokenComment = col;
                    else if (s == "string" || s == "string.quoted") t.TokenString = col;
                    else if (s == "keyword" || s == "storage" || s == "storage.modifier") t.TokenKeyword = col;
                    else if (s == "keyword.control") t.TokenControl = col;
                    else if (s == "entity.name.function" || s == "support.function") t.TokenFunction = col;
                    else if (s == "constant.numeric") t.TokenNumber = col;
                    else if (s == "entity.name.type" || s == "support.type" || s == "storage.type" || s == "entity.name.class") t.TokenType = col;
                    else if (s == "keyword.operator") t.TokenOperator = col;
                }
            }
        }

        public bool InstallVsixArchiveFromDisk(string vsixPath, List<StudioTheme> workbenchThemes, out string statusMessage)
        {
            return InstallVsix(vsixPath, workbenchThemes, out statusMessage) != null;
        }

        /// <summary>
        /// Installs a .vsix: extracted into a temporary folder, then moved to &lt;publisher.name&gt;-&lt;version&gt;
        /// (other installed versions of the same extension are removed), contributions loaded. Returns the package.
        /// </summary>
        public VsixExtensionPackage InstallVsix(string vsixPath, List<StudioTheme> workbenchThemes, out string statusMessage)
        {
            statusMessage = "";
            if (string.IsNullOrEmpty(vsixPath) || !File.Exists(vsixPath))
            {
                statusMessage = "File not found: " + vsixPath;
                return null;
            }
            string staging = Path.Combine(extensionsRootDir, ".installing-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            try
            {
                Directory.CreateDirectory(extensionsRootDir);
                ZipFile.ExtractToDirectory(vsixPath, staging); // rejects entries outside the folder (zip-slip)
                string pkgPath = FindPackageJson(staging);
                Dictionary<string, object> pkg = pkgPath != null ? ParseJsoncObject(File.ReadAllText(pkgPath)) : null;
                if (pkg == null)
                {
                    statusMessage = "'" + Path.GetFileName(vsixPath) + "' is not a VS Code extension (no valid extension/package.json).";
                    LogHost(statusMessage);
                    return null;
                }
                string id = JStr(pkg, "publisher", "local") + "." + JStr(pkg, "name", Path.GetFileNameWithoutExtension(vsixPath));
                string ver = JStr(pkg, "version", "1.0.0");
                string target = Path.Combine(extensionsRootDir, Regex.Replace(id + "-" + ver, @"[^A-Za-z0-9._-]+", "_"));
                RemoveInstalledCopies(id, workbenchThemes);
                if (Directory.Exists(target)) Directory.Delete(target, true);
                Directory.Move(staging, target);
                LoadInstalledPackage(target, workbenchThemes);
                VsixExtensionPackage installed = Extensions.FirstOrDefault(x => x.Id.Equals(id, StringComparison.OrdinalIgnoreCase) && x.IsInstalled);
                statusMessage = installed != null
                    ? "Installed " + installed.DisplayName + " v" + installed.Version + (installed.ContributedThemes.Count > 0 ? " (" + installed.ContributedThemes.Count + " theme(s))" : "") + (installed.ContributedSnippets.Count > 0 ? " (" + installed.ContributedSnippets.Count + " snippet(s))" : "") + "."
                    : "Installed '" + Path.GetFileName(vsixPath) + "' but it could not be loaded.";
                LogHost(statusMessage);
                return installed;
            }
            catch (Exception ex)
            {
                statusMessage = "VSIX installation failed: " + ex.Message;
                LogHost(statusMessage);
                return null;
            }
            finally
            {
                try { if (Directory.Exists(staging)) Directory.Delete(staging, true); } catch { }
            }
        }

        /// <summary>Removes every installed copy (any version) of an extension and its themes / snippets.</summary>
        private void RemoveInstalledCopies(string id, List<StudioTheme> workbenchThemes)
        {
            VsixExtensionPackage loaded = Extensions.FirstOrDefault(x => x.Id.Equals(id, StringComparison.OrdinalIgnoreCase) && x.IsInstalled && !string.IsNullOrEmpty(x.InstallPath));
            if (loaded != null) UnloadContributions(loaded, workbenchThemes);
            string[] dirs;
            try { dirs = Directory.GetDirectories(extensionsRootDir); } catch { return; }
            foreach (string d in dirs)
            {
                if (Path.GetFileName(d).StartsWith(".", StringComparison.Ordinal)) continue;
                try
                {
                    string p = FindPackageJson(d);
                    Dictionary<string, object> pkg = p != null ? ParseJsoncObject(File.ReadAllText(p)) : null;
                    if (pkg != null && string.Equals(JStr(pkg, "publisher", "local") + "." + JStr(pkg, "name", ""), id, StringComparison.OrdinalIgnoreCase))
                        Directory.Delete(d, true);
                }
                catch { }
            }
        }

        private void UnloadContributions(VsixExtensionPackage ext, List<StudioTheme> workbenchThemes)
        {
            Extensions.Remove(ext);
            if (workbenchThemes != null)
                foreach (StudioTheme t in ext.ContributedThemes) workbenchThemes.Remove(t);
            foreach (CodeSnippetItem s in ext.ContributedSnippets) AllLoadedSnippets.Remove(s);
        }

        /// <summary>Uninstalls an installed extension: folder deleted, themes and snippets unloaded.</summary>
        public bool Uninstall(VsixExtensionPackage ext, List<StudioTheme> workbenchThemes, out string statusMessage)
        {
            statusMessage = "";
            if (ext == null || !ext.IsInstalled || string.IsNullOrEmpty(ext.InstallPath))
            {
                statusMessage = ext != null ? ext.DisplayName + " is built in and can't be uninstalled." : "No extension selected.";
                return false;
            }
            try
            {
                UnloadContributions(ext, workbenchThemes);
                if (Directory.Exists(ext.InstallPath)) Directory.Delete(ext.InstallPath, true);
                ext.IsInstalled = false;
                ext.IsActivated = false;
                statusMessage = "Uninstalled " + ext.DisplayName + ".";
                LogHost(statusMessage);
                return true;
            }
            catch (Exception ex)
            {
                statusMessage = "Uninstall failed: " + ex.Message;
                LogHost(statusMessage);
                return false;
            }
        }

        /// <summary>
        /// Why the last OpenVSX search / download did not reach the registry (Offline Mode or a network error); empty on
        /// success. UIs show it as a notice.
        /// </summary>
        public string LastNetworkNotice = "";

        /// <summary>Packages matching the last OpenVSX search (installed ones included), in registry order.</summary>
        public List<VsixExtensionPackage> LastSearchResults = new List<VsixExtensionPackage>();

        public async Task<int> SearchOpenVsxRegistryAsync(string query)
        {
            if (string.IsNullOrWhiteSpace(query)) return 0;
            List<VsixExtensionPackage> found = new List<VsixExtensionPackage>();
            LogHost("Querying OpenVSX Registry for '" + query + "'...");
            LastNetworkNotice = "";

            return await Task.Run(() =>
            {
                try
                {
                    string url = "https://open-vsx.org/api/-/search?query=" + Uri.EscapeDataString(query) + "&size=14";
                    LedgerHttpRequest lreq = new LedgerHttpRequest();
                    lreq.Feature = "OpenVSX search";
                    lreq.Url = url;
                    lreq.UserAgent = "codeLess-Native-IDE/3.2";
                    lreq.TimeoutMs = 10000;
                    lreq.BlockedMessage = "Offline Mode is on: OpenVSX extension search is unavailable. Turn off Offline Mode to search the marketplace (installing a local .vsix file still works).";
                    LedgerHttpResponse res = LedgerHttp.Send(lreq);
                    if (!res.IsSuccess)
                    {
                        LastNetworkNotice = res.Status > 0 ? "OpenVSX returned HTTP " + res.Status + "." : "OpenVSX is unreachable: " + res.Error;
                        LogHost("OpenVSX search error: " + LastNetworkNotice);
                        return 0;
                    }
                    {
                        string raw = res.Body;
                        JavaScriptSerializer ser = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };
                        var root = ser.Deserialize<Dictionary<string, object>>(raw);
                        if (root != null && root.ContainsKey("extensions") && root["extensions"] is ArrayList)
                        {
                            ArrayList arr = (ArrayList)root["extensions"];
                            int added = 0;
                            foreach (object item in arr)
                            {
                                var d = item as Dictionary<string, object>;
                                if (d == null) continue;
                                string pub = d.ContainsKey("namespace") ? Convert.ToString(d["namespace"]) : "openvsx";
                                string name = d.ContainsKey("name") ? Convert.ToString(d["name"]) : "ext";
                                string id = pub + "." + name;
                                VsixExtensionPackage known = Extensions.FirstOrDefault(x => x.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
                                if (known != null)
                                {
                                    found.Add(known);
                                    continue;
                                }

                                string disp = d.ContainsKey("displayName") && d["displayName"] != null
                                    ? Convert.ToString(d["displayName"])
                                    : name;
                                string ver = d.ContainsKey("version") ? Convert.ToString(d["version"]) : "1.0.0";
                                string desc = d.ContainsKey("description") ? Convert.ToString(d["description"]) : "";
                                string dlUrl = "";
                                if (d.ContainsKey("files") && d["files"] is Dictionary<string, object>)
                                {
                                    var files = (Dictionary<string, object>)d["files"];
                                    if (files.ContainsKey("download"))
                                        dlUrl = Convert.ToString(files["download"]);
                                }

                                VsixExtensionPackage result = new VsixExtensionPackage
                                {
                                    Id = id,
                                    Publisher = pub,
                                    Name = name,
                                    DisplayName = disp,
                                    Version = ver,
                                    Description = desc,
                                    DownloadUrl = dlUrl,
                                    IsInstalled = false
                                };
                                Extensions.Add(result);
                                found.Add(result);
                                added++;
                            }
                            LastSearchResults = found;
                            LogHost("OpenVSX returned " + added + " new extension packages.");
                            return added;
                        }
                    }
                }
                catch (NetworkBlockedException bex)
                {
                    LastNetworkNotice = bex.Message;
                    LogHost(bex.Message);
                }
                catch (Exception ex)
                {
                    LogHost("OpenVSX search error: " + ex.Message);
                }
                return 0;
            });
        }

        /// <summary>
        /// Downloads an OpenVSX extension (worker thread, through the network ledger) and installs it on the caller's
        /// thread (the install updates the theme / snippet lists the UI reads).
        /// </summary>
        public async Task<bool> DownloadAndInstallFromOpenVsxAsync(VsixExtensionPackage ext, List<StudioTheme> workbenchThemes)
        {
            if (ext == null || string.IsNullOrEmpty(ext.DownloadUrl)) return false;
            LogHost("Downloading " + ext.Id + " from OpenVSX (" + ext.DownloadUrl + ")...");
            LastNetworkNotice = "";
            string vsixTemp = Path.Combine(Path.GetTempPath(), "codeless-" + Guid.NewGuid().ToString("N").Substring(0, 10) + ".vsix");
            try
            {
                string downloadError = await Task.Run(() =>
                {
                    try
                    {
                        LedgerHttpResponse dl = LedgerHttp.DownloadToFile("Extension download", ext.DownloadUrl, vsixTemp, "codeLess-Native-IDE/3.2", 60000);
                        return dl.IsSuccess ? null : "Extension download failed: " + (dl.Status > 0 ? "HTTP " + dl.Status : dl.Error);
                    }
                    catch (NetworkBlockedException bex) { return bex.Message; }
                    catch (Exception ex) { return "Extension download failed: " + ex.Message; }
                });
                if (downloadError != null)
                {
                    LastNetworkNotice = downloadError;
                    LogHost("Failed installing " + ext.Id + ": " + downloadError);
                    return false;
                }
                string status;
                VsixExtensionPackage installed = InstallVsix(vsixTemp, workbenchThemes, out status);
                if (installed == null)
                {
                    LastNetworkNotice = status;
                    return false;
                }
                ext.InstallPath = installed.InstallPath;
                ext.IsInstalled = true;
                ext.IsActivated = true;
                LogHost("Successfully installed & activated " + ext.Id);
                return true;
            }
            finally
            {
                try { if (File.Exists(vsixTemp)) File.Delete(vsixTemp); } catch { }
            }
        }

        public async Task<string> ActivateAndRunNodeExtensionHostAsync(VsixExtensionPackage ext, string commandId = "")
        {
            return await Task.Run(() =>
            {
                try
                {
                    string action = (!string.IsNullOrEmpty(ext != null ? ext.MainEntryScript : "") && File.Exists(ext.MainEntryScript))
                        ? "activate"
                        : "ping";
                    string args = "\"" + hostBridgeScriptPath + "\" " + action +
                                  (action == "activate" ? " \"" + ext.MainEntryScript + "\"" : "") +
                                  (!string.IsNullOrEmpty(commandId) ? " \"" + commandId + "\"" : "");

                    string output = LlmHarnessAndAgentEngine.RunProcessCapture("node", args, Environment.CurrentDirectory, 6000);
                    LogHost("Node Host Output: " + output);
                    return output;
                }
                catch (Exception ex)
                {
                    string err = "Node Extension Host error: " + ex.Message;
                    LogHost(err);
                    return err;
                }
            });
        }
    }
}
