using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
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

        public VsixAndPluginHostEngine(List<StudioTheme> workbenchThemes)
        {
            string userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            extensionsRootDir = Path.Combine(userProfile, ".codeless", "extensions");
            string hostDir = Path.Combine(userProfile, ".codeless", "host");
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

        public void ScanInstalledDirectories(List<StudioTheme> workbenchThemes)
        {
            string[] roots = new[]
            {
                extensionsRootDir
            };

            JavaScriptSerializer ser = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };

            foreach (string root in roots)
            {
                if (string.IsNullOrEmpty(root) || !Directory.Exists(root)) continue;
                try
                {
                    foreach (string dir in Directory.GetDirectories(root))
                    {
                        string pkgJsonPath = Path.Combine(dir, "extension", "package.json");
                        if (!File.Exists(pkgJsonPath))
                            pkgJsonPath = Path.Combine(dir, "package.json");
                        if (!File.Exists(pkgJsonPath)) continue;

                        try
                        {
                            string rawPkg = File.ReadAllText(pkgJsonPath);
                            var pkg = ser.Deserialize<Dictionary<string, object>>(rawPkg);
                            if (pkg == null) continue;

                            string pub = pkg.ContainsKey("publisher") ? Convert.ToString(pkg["publisher"]) : "local";
                            string name = pkg.ContainsKey("name") ? Convert.ToString(pkg["name"]) : Path.GetFileName(dir);
                            string disp = pkg.ContainsKey("displayName") ? Convert.ToString(pkg["displayName"]) : name;
                            string ver = pkg.ContainsKey("version") ? Convert.ToString(pkg["version"]) : "1.0.0";
                            string desc = pkg.ContainsKey("description") ? Convert.ToString(pkg["description"]) : "";
                            string mainScript = pkg.ContainsKey("main") ? Convert.ToString(pkg["main"]) : "";
                            string id = pub + "." + name;

                            if (Extensions.Any(x => x.Id.Equals(id, StringComparison.OrdinalIgnoreCase)))
                                continue;

                            string pkgBaseDir = Path.GetDirectoryName(pkgJsonPath);
                            string resolvedMain = !string.IsNullOrEmpty(mainScript)
                                ? Path.Combine(pkgBaseDir, mainScript.Replace('/', Path.DirectorySeparatorChar).TrimStart('.', '\\'))
                                : "";

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

                            if (pkg.ContainsKey("contributes") && pkg["contributes"] is Dictionary<string, object>)
                            {
                                var contrib = (Dictionary<string, object>)pkg["contributes"];
                                if (contrib.ContainsKey("themes") && contrib["themes"] is ArrayList)
                                {
                                    foreach (object thObj in (ArrayList)contrib["themes"])
                                    {
                                        var thDict = thObj as Dictionary<string, object>;
                                        if (thDict != null && thDict.ContainsKey("path"))
                                        {
                                            string relTh = Convert.ToString(thDict["path"]).Replace('/', Path.DirectorySeparatorChar).TrimStart('.', '\\');
                                            string thFull = Path.Combine(pkgBaseDir, relTh);
                                            StudioTheme parsedTheme = TryParseVsixThemeJson(thFull, Convert.ToString(thDict.ContainsKey("label") ? thDict["label"] : disp));
                                            if (parsedTheme != null)
                                            {
                                                extPkg.ContributedThemes.Add(parsedTheme);
                                                if (!workbenchThemes.Any(t => t.Id == parsedTheme.Id))
                                                    workbenchThemes.Add(parsedTheme);
                                            }
                                        }
                                    }
                                }

                                if (contrib.ContainsKey("commands") && contrib["commands"] is ArrayList)
                                {
                                    foreach (object cmdObj in (ArrayList)contrib["commands"])
                                    {
                                        var cmdDict = cmdObj as Dictionary<string, object>;
                                        if (cmdDict != null && cmdDict.ContainsKey("command"))
                                            extPkg.ContributedCommands.Add(Convert.ToString(cmdDict["command"]));
                                    }
                                }

                                if (contrib.ContainsKey("snippets") && contrib["snippets"] is ArrayList)
                                {
                                    foreach (object snObj in (ArrayList)contrib["snippets"])
                                    {
                                        var snDict = snObj as Dictionary<string, object>;
                                        if (snDict != null && snDict.ContainsKey("path"))
                                        {
                                            string lang = snDict.ContainsKey("language") ? Convert.ToString(snDict["language"]) : "js";
                                            string relSn = Convert.ToString(snDict["path"]).Replace('/', Path.DirectorySeparatorChar).TrimStart('.', '\\');
                                            string snFull = Path.Combine(pkgBaseDir, relSn);
                                            List<CodeSnippetItem> parsedSnips = TryParseVsixSnippetsJson(snFull, lang);
                                            foreach (CodeSnippetItem cs in parsedSnips)
                                            {
                                                extPkg.ContributedSnippets.Add(cs);
                                                if (!AllLoadedSnippets.Any(s => s.Prefix == cs.Prefix && s.Language == cs.Language))
                                                    AllLoadedSnippets.Add(cs);
                                            }
                                        }
                                    }
                                }
                            }

                            Extensions.Add(extPkg);
                            LogHost("Loaded installed VSIX extension: " + id + " v" + ver);
                        }
                        catch { }
                    }
                }
                catch { }
            }
        }

        public VsixExtensionPackage LoadPluginFromDirectory(string dir, List<StudioTheme> workbenchThemes)
        {
            if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) return null;
            string targetSub = Path.Combine(extensionsRootDir, Path.GetFileName(dir.TrimEnd('\\', '/')));
            string expectedId = null;
            try
            {
                string pkgPath = Path.Combine(dir, "extension", "package.json");
                if (!File.Exists(pkgPath)) pkgPath = Path.Combine(dir, "package.json");
                if (File.Exists(pkgPath))
                {
                    JavaScriptSerializer ser = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };
                    var pkg = ser.Deserialize<Dictionary<string, object>>(File.ReadAllText(pkgPath));
                    if (pkg != null)
                    {
                        string pub = pkg.ContainsKey("publisher") ? Convert.ToString(pkg["publisher"]) : "local";
                        string nm = pkg.ContainsKey("name") ? Convert.ToString(pkg["name"]) : Path.GetFileName(dir);
                        expectedId = pub + "." + nm;
                        Extensions.RemoveAll(x => x.Id.Equals(expectedId, StringComparison.OrdinalIgnoreCase));
                    }
                }
                if (!string.Equals(Path.GetFullPath(dir).TrimEnd('\\', '/'), Path.GetFullPath(targetSub).TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase))
                {
                    if (!Directory.Exists(targetSub)) Directory.CreateDirectory(targetSub);
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
            catch { }
            ScanInstalledDirectories(workbenchThemes);
            if (!string.IsNullOrEmpty(expectedId))
                return Extensions.FirstOrDefault(x => x.Id.Equals(expectedId, StringComparison.OrdinalIgnoreCase));
            return Extensions.LastOrDefault();
        }

        public List<CodeSnippetItem> TryParseVsixSnippetsJson(string snippetsJsonPath, string defaultLang)
        {
            List<CodeSnippetItem> list = new List<CodeSnippetItem>();
            try
            {
                if (!File.Exists(snippetsJsonPath)) return list;
                string raw = File.ReadAllText(snippetsJsonPath);
                string cleanJson = Regex.Replace(raw, @"^\s*//.*$", "", RegexOptions.Multiline);
                JavaScriptSerializer ser = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };
                var root = ser.Deserialize<Dictionary<string, object>>(cleanJson);
                if (root == null) return list;

                foreach (var kv in root)
                {
                    var entry = kv.Value as Dictionary<string, object>;
                    if (entry == null || !entry.ContainsKey("prefix") || !entry.ContainsKey("body")) continue;
                    string prefix = entry["prefix"] is ArrayList && ((ArrayList)entry["prefix"]).Count > 0
                        ? Convert.ToString(((ArrayList)entry["prefix"])[0])
                        : Convert.ToString(entry["prefix"]);
                    string body = entry["body"] is ArrayList
                        ? string.Join("\n", ((ArrayList)entry["body"]).Cast<object>().Select(Convert.ToString))
                        : Convert.ToString(entry["body"]);
                    string desc = entry.ContainsKey("description") ? Convert.ToString(entry["description"]) : kv.Key;
                    if (!string.IsNullOrWhiteSpace(prefix) && !string.IsNullOrWhiteSpace(body))
                    {
                        list.Add(new CodeSnippetItem
                        {
                            Prefix = prefix.Trim(),
                            Body = body,
                            Description = desc,
                            Language = defaultLang ?? "js"
                        });
                    }
                }
            }
            catch { }
            return list;
        }

        public StudioTheme TryParseVsixThemeJson(string themeJsonPath, string label)
        {
            try
            {
                if (!File.Exists(themeJsonPath)) return null;
                string raw = File.ReadAllText(themeJsonPath);
                string cleanJson = Regex.Replace(raw, @"^\s*//.*$", "", RegexOptions.Multiline);
                JavaScriptSerializer ser = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };
                var root = ser.Deserialize<Dictionary<string, object>>(cleanJson);
                if (root == null) return null;

                StudioTheme baseTheme = StudioTheme.GetBuiltInThemes()[0];
                string cleanLabel = (label ?? "Custom Theme").Trim();
                if (!cleanLabel.EndsWith("(VSIX)", StringComparison.OrdinalIgnoreCase))
                    cleanLabel += " (VSIX)";
                StudioTheme custom = new StudioTheme
                {
                    Id = "vsix-" + Regex.Replace(label.ToLowerInvariant(), @"[^a-z0-9]+", "-"),
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

                if (root.ContainsKey("colors") && root["colors"] is Dictionary<string, object>)
                {
                    var colors = (Dictionary<string, object>)root["colors"];
                    if (colors.ContainsKey("editor.background"))
                        custom.BgEditor = StudioTheme.Hex(Convert.ToString(colors["editor.background"]), custom.BgEditor);
                    if (colors.ContainsKey("editor.foreground"))
                        custom.FgPrimary = StudioTheme.Hex(Convert.ToString(colors["editor.foreground"]), custom.FgPrimary);
                    if (colors.ContainsKey("sideBar.background"))
                        custom.BgSidebar = StudioTheme.Hex(Convert.ToString(colors["sideBar.background"]), custom.BgSidebar);
                    if (colors.ContainsKey("activityBar.background"))
                    {
                        custom.BgActivity = StudioTheme.Hex(Convert.ToString(colors["activityBar.background"]), custom.BgActivity);
                        custom.BgWorkbench = custom.BgActivity;
                    }
                    if (colors.ContainsKey("statusBar.background"))
                        custom.BgStatus = StudioTheme.Hex(Convert.ToString(colors["statusBar.background"]), custom.BgStatus);
                    if (colors.ContainsKey("button.background"))
                        custom.AccentPrimary = StudioTheme.Hex(Convert.ToString(colors["button.background"]), custom.AccentPrimary);
                }

                return custom;
            }
            catch
            {
                return null;
            }
        }

        public bool InstallVsixArchiveFromDisk(string vsixPath, List<StudioTheme> workbenchThemes, out string statusMessage)
        {
            statusMessage = "";
            if (string.IsNullOrEmpty(vsixPath) || !File.Exists(vsixPath))
            {
                statusMessage = "File not found: " + vsixPath;
                return false;
            }

            try
            {
                string folderName = Path.GetFileNameWithoutExtension(vsixPath) + "-" + DateTime.Now.ToString("HHmmss");
                string targetDir = Path.Combine(extensionsRootDir, folderName);
                if (Directory.Exists(targetDir)) Directory.Delete(targetDir, true);
                ZipFile.ExtractToDirectory(vsixPath, targetDir);

                ScanInstalledDirectories(workbenchThemes);
                statusMessage = "Installed VSIX package '" + Path.GetFileName(vsixPath) + "' into " + targetDir;
                LogHost(statusMessage);
                return true;
            }
            catch (Exception ex)
            {
                statusMessage = "VSIX installation failed: " + ex.Message;
                LogHost(statusMessage);
                return false;
            }
        }

        /// <summary>
        /// Why the last OpenVSX search / download did not reach the registry (Offline Mode or a network error); empty on
        /// success. UIs show it as a notice.
        /// </summary>
        public string LastNetworkNotice = "";

        public async Task<int> SearchOpenVsxRegistryAsync(string query)
        {
            if (string.IsNullOrWhiteSpace(query)) return 0;
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
                                if (Extensions.Any(x => x.Id.Equals(id, StringComparison.OrdinalIgnoreCase)))
                                    continue;

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

                                Extensions.Add(new VsixExtensionPackage
                                {
                                    Id = id,
                                    Publisher = pub,
                                    Name = name,
                                    DisplayName = disp,
                                    Version = ver,
                                    Description = desc,
                                    DownloadUrl = dlUrl,
                                    IsInstalled = false
                                });
                                added++;
                            }
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

        public async Task<bool> DownloadAndInstallFromOpenVsxAsync(VsixExtensionPackage ext, List<StudioTheme> workbenchThemes)
        {
            if (ext == null || string.IsNullOrEmpty(ext.DownloadUrl)) return false;
            LogHost("Downloading " + ext.Id + " from OpenVSX (" + ext.DownloadUrl + ")...");
            LastNetworkNotice = "";

            return await Task.Run(() =>
            {
                string vsixTemp = Path.Combine(Path.GetTempPath(), ext.Id + "-" + Guid.NewGuid().ToString("N").Substring(0, 6) + ".vsix");
                try
                {
                    string targetDir = Path.Combine(extensionsRootDir, ext.Id + "-" + ext.Version);

                    LedgerHttpResponse dl = LedgerHttp.DownloadToFile("Extension download", ext.DownloadUrl, vsixTemp, "codeLess-Native-IDE/3.2", 60000);
                    if (!dl.IsSuccess)
                    {
                        LastNetworkNotice = "Extension download failed: " + (dl.Status > 0 ? "HTTP " + dl.Status : dl.Error);
                        LogHost("Failed installing " + ext.Id + ": " + LastNetworkNotice);
                        return false;
                    }

                    if (Directory.Exists(targetDir))
                        Directory.Delete(targetDir, true);
                    ZipFile.ExtractToDirectory(vsixTemp, targetDir);

                    ext.InstallPath = targetDir;
                    ext.IsInstalled = true;
                    ext.IsActivated = true;
                    ScanInstalledDirectories(workbenchThemes);
                    LogHost("Successfully installed & activated " + ext.Id);
                    return true;
                }
                catch (NetworkBlockedException bex)
                {
                    LastNetworkNotice = bex.Message;
                    LogHost("Failed installing " + ext.Id + ": " + bex.Message);
                    return false;
                }
                catch (Exception ex)
                {
                    LogHost("Failed installing " + ext.Id + ": " + ex.Message);
                    return false;
                }
                finally
                {
                    try { if (File.Exists(vsixTemp)) File.Delete(vsixTemp); } catch { }
                }
            });
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
