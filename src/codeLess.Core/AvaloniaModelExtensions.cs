#if !NETFRAMEWORK
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace codeLess.Studio
{
    public partial class StudioTheme
    {
        public Color SidebarBg => BgSidebar;
        public Color EditorBg => BgEditor;
        public Color FloatingBg => BgElevated;
        public Color ChatBg => BgSidebar;
        public Color TerminalBg => BgWorkbench;
        public Color TerminalText => FgPrimary;
        public Color TitleBarBg => BgActivity;
        public Color ActivityBarBg => BgActivity;
        public Color TabBarBg => BgSidebar;
        public Color ActiveTabBg => BgEditor;
        public Color StatusBarBg => BgStatus;
        public Color StatusBarText => IsLight ? FgPrimary : Color.White;
        public Color GutterBg => BgEditor;
        public Color SelectionBg => BgSelection;
        public Color LineHighlightBg => BgLineHighlight;
        public Color TextPrimary => FgPrimary;
        public Color TextSecondary => FgSecondary;
        public Color SyntaxKeyword => TokenKeyword;
        public Color SyntaxType => TokenType;
        public Color SyntaxFunction => TokenFunction;
        public Color SyntaxString => TokenString;
        public Color SyntaxComment => TokenComment;
        public Color SyntaxNumber => TokenNumber;
        public Color DiagnosticError => DangerCoral;
        public Color DiagnosticWarning => WarnAmber;

        public static StudioTheme DarkObsidian() => GetById("codeless-obsidian");
        public static List<StudioTheme> GetAllBuiltInThemes() => GetBuiltInThemes();
        public static StudioTheme GetById(string idOrName) => ResolveTheme(GetBuiltInThemes(), idOrName);
    }

    public partial class DiagnosticItem
    {
        public int LineNumber
        {
            get => Line;
            set => Line = value;
        }
    }

    public partial class AstSymbolItem
    {
        public int LineNumber
        {
            get => Line;
            set => Line = value;
        }
    }

    public partial class WorkspaceSymbolLocation
    {
        public string Name
        {
            get => SymbolName;
            set => SymbolName = value;
        }

        public int LineNumber
        {
            get => Line;
            set => Line = value;
        }
    }

    public partial class EditorTabBuffer
    {
        public int SelectionStartLine
        {
            get => SelectionAnchorLine;
            set => SelectionAnchorLine = value;
        }

        public int SelectionStartCol
        {
            get => SelectionAnchorCol;
            set => SelectionAnchorCol = value;
        }

        public bool IsPreviewTab
        {
            get => IsPreview;
            set => IsPreview = value;
        }

        public string GhostTextSuggestion
        {
            get => GhostText ?? "";
            set => GhostText = string.IsNullOrEmpty(value) ? null : value;
        }

        public double ScrollLeftPx
        {
            get => ScrollLeftCol * 7.8;
            set => ScrollLeftCol = Math.Max(0, (int)Math.Round(value / 7.8));
        }

        public HashSet<int> ProposedNewLines { get; } = new HashSet<int>();
        public Dictionary<int, List<string>> RemovedLineHistory { get; } = new Dictionary<int, List<string>>();
        public Dictionary<int, int> FoldedBlockRanges { get; } = new Dictionary<int, int>();
        public HashSet<int> GutterDeletedAfterLine { get; } = new HashSet<int>();

        public bool HasPendingDiff =>
            (ProposedNewLines != null && ProposedNewLines.Count > 0) ||
            (DiffHunks != null && DiffHunks.Any(h => h.IsPending));

        private int _cachedGutterMapVersion = -1;
        private Dictionary<int, int> _cachedGutterDiffMap;

        public Dictionary<int, int> GutterDiffByLine
        {
            get
            {
                if (_cachedGutterDiffMap != null && _cachedGutterMapVersion == GutterDiffVersion)
                    return _cachedGutterDiffMap;
                var map = new Dictionary<int, int>();
                if (GutterAddedLines != null)
                {
                    foreach (int l in GutterAddedLines) map[l] = 1;
                }
                if (GutterModifiedLines != null)
                {
                    foreach (int l in GutterModifiedLines) map[l] = 2;
                }
                _cachedGutterDiffMap = map;
                _cachedGutterMapVersion = GutterDiffVersion;
                return map;
            }
        }

        public void WriteFilePreservingMetadata(string targetPath)
        {
            WriteToDisk(targetPath);
        }

        public bool PerformUndo() => Undo();
        public bool PerformRedo() => Redo();

        public void SelectAll()
        {
            ClampCursor();
            SelectionAnchorLine = 0;
            SelectionAnchorCol = 0;
            CursorLine = Math.Max(0, Lines.Count - 1);
            CursorCol = Lines[CursorLine].Length;
        }

        public void DeleteSelection()
        {
            if (!HasSelection()) return;
            GetNormalizedSelection(out int sLine, out int sCol, out int eLine, out int eCol);
            sLine = Math.Max(0, Math.Min(Lines.Count - 1, sLine));
            eLine = Math.Max(0, Math.Min(Lines.Count - 1, eLine));
            sCol = Math.Max(0, Math.Min(Lines[sLine].Length, sCol));
            eCol = Math.Max(0, Math.Min(Lines[eLine].Length, eCol));

            string prefix = Lines[sLine].Substring(0, sCol);
            string suffix = Lines[eLine].Substring(eCol);
            Lines[sLine] = prefix + suffix;
            if (eLine > sLine)
            {
                Lines.RemoveRange(sLine + 1, eLine - sLine);
            }
            CursorLine = sLine;
            CursorCol = sCol;
            ClearSelection();
            SyncContentFromLines(recomputeGutterDiff: false);
        }

        public void ReplaceSelection(string replacement)
        {
            DeleteSelection();
            InsertTextAtCursor(replacement);
        }

        public void InsertTextAtCursor(string text)
        {
            if (string.IsNullOrEmpty(text)) return;
            if (HasSelection())
                DeleteSelection();
            ClampCursor();

            string norm = text.Replace("\r\n", "\n").Replace("\r", "\n");
            string[] parts = norm.Split('\n');
            string cur = Lines[CursorLine];
            string before = cur.Substring(0, Math.Min(cur.Length, CursorCol));
            string after = cur.Substring(Math.Min(cur.Length, CursorCol));

            if (parts.Length == 1)
            {
                Lines[CursorLine] = before + parts[0] + after;
                CursorCol = before.Length + parts[0].Length;
            }
            else
            {
                Lines[CursorLine] = before + parts[0];
                for (int i = 1; i < parts.Length - 1; i++)
                {
                    Lines.Insert(CursorLine + i, parts[i]);
                }
                int lastLine = CursorLine + parts.Length - 1;
                string lastPart = parts[parts.Length - 1];
                Lines.Insert(lastLine, lastPart + after);
                CursorLine = lastLine;
                CursorCol = lastPart.Length;
            }
            SyncContentFromLines(recomputeGutterDiff: false);
        }

        public void PerformBackspace()
        {
            if (HasSelection())
            {
                DeleteSelection();
                return;
            }
            ClampCursor();
            if (CursorCol > 0)
            {
                Lines[CursorLine] = Lines[CursorLine].Remove(CursorCol - 1, 1);
                CursorCol--;
            }
            else if (CursorLine > 0)
            {
                int prevLen = Lines[CursorLine - 1].Length;
                Lines[CursorLine - 1] += Lines[CursorLine];
                Lines.RemoveAt(CursorLine);
                CursorLine--;
                CursorCol = prevLen;
            }
            SyncContentFromLines(recomputeGutterDiff: false);
        }

        public void PerformDelete()
        {
            if (HasSelection())
            {
                DeleteSelection();
                return;
            }
            ClampCursor();
            if (CursorCol < Lines[CursorLine].Length)
            {
                Lines[CursorLine] = Lines[CursorLine].Remove(CursorCol, 1);
            }
            else if (CursorLine < Lines.Count - 1)
            {
                Lines[CursorLine] += Lines[CursorLine + 1];
                Lines.RemoveAt(CursorLine + 1);
            }
            SyncContentFromLines(recomputeGutterDiff: false);
        }
    }

    public class StudioSettings : IdePersistentSettings
    {
        public string ThemeId
        {
            get => ActiveThemeId ?? "codeless-obsidian";
            set => ActiveThemeId = value;
        }

        public float FontSize
        {
            get => EditorFontSize <= 0 ? 13f : EditorFontSize;
            set => EditorFontSize = value;
        }

        public string ActiveProvider
        {
            get => string.IsNullOrEmpty(ActiveVendorId) ? "Anthropic Claude" : ActiveVendorId;
            set => ActiveVendorId = value;
        }

        public string ActiveModel
        {
            get => string.IsNullOrEmpty(DefaultAgentModel) ? "claude-sonnet-4-6" : DefaultAgentModel;
            set => DefaultAgentModel = value;
        }

        public static string NormalizeProviderId(string providerOrName)
        {
            if (string.IsNullOrWhiteSpace(providerOrName)) return "anthropic";
            string s = providerOrName.Trim().ToLowerInvariant();
            if (s.Contains("anthropic") || s.Contains("claude")) return "anthropic";
            if (s.Contains("openrouter")) return "openrouter";
            if (s.Contains("openai")) return "openai";
            if (s.Contains("gemini") || s.Contains("google")) return "gemini";
            if (s.Contains("deepseek")) return "deepseek";
            if (s.Contains("groq")) return "groq";
            if (s.Contains("mistral") || s.Contains("codestral")) return "mistral";
            if (s.Contains("xai") || s.Contains("grok")) return "xai";
            if (s.Contains("ollama") || s.Contains("lm studio")) return "ollama";
            if (s.Contains("custom")) return "custom";
            return providerOrName.Trim();
        }

        public string GetApiKeyForProvider(string providerOrName)
        {
            if (Vendors == null) return "";
            string normId = NormalizeProviderId(providerOrName);
            var v = Vendors.FirstOrDefault(x =>
                string.Equals(x.Id, normId, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(x.Id, providerOrName, StringComparison.OrdinalIgnoreCase));
            return v?.ApiKey ?? "";
        }

        public string GetActiveApiKey() => GetApiKeyForProvider(ActiveProvider);

        public void SetApiKeyForProvider(string provider, string key)
        {
            if (string.IsNullOrWhiteSpace(provider)) return;
            if (Vendors == null) Vendors = new List<SavedVendorEntry>();
            string normId = NormalizeProviderId(provider);
            var v = Vendors.FirstOrDefault(x =>
                string.Equals(x.Id, normId, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(x.Id, provider, StringComparison.OrdinalIgnoreCase));
            if (v == null)
            {
                v = new SavedVendorEntry { Id = normId };
                Vendors.Add(v);
            }
            else
            {
                v.Id = normId;
            }
            v.ApiKey = key ?? "";
        }

        public static new StudioSettings Load()
        {
            var baseLoaded = IdePersistentSettings.Load();
            return new StudioSettings
            {
                ActiveThemeId = baseLoaded.ActiveThemeId,
                ActiveVendorId = baseLoaded.ActiveVendorId,
                ActiveHarnessId = baseLoaded.ActiveHarnessId,
                LastWorkspacePath = baseLoaded.LastWorkspacePath,
                EditorFontFamily = baseLoaded.EditorFontFamily,
                EditorFontSize = baseLoaded.EditorFontSize <= 11f ? 13f : baseLoaded.EditorFontSize,
                TabSize = baseLoaded.TabSize,
                InsertSpaces = baseLoaded.InsertSpaces,
                WordWrap = baseLoaded.WordWrap,
                AutoSave = baseLoaded.AutoSave,
                RequireAgentToolApproval = baseLoaded.RequireAgentToolApproval,
                EnableInlineGhostText = baseLoaded.EnableInlineGhostText,
                ShowMinimap = baseLoaded.ShowMinimap,
                ShowBreadcrumbs = baseLoaded.ShowBreadcrumbs,
                ShowWelcomeOnStartup = baseLoaded.ShowWelcomeOnStartup,
                ExcludedFolders = baseLoaded.ExcludedFolders,
                DefaultAgentModel = baseLoaded.DefaultAgentModel,
                DefaultAskModel = baseLoaded.DefaultAskModel,
                DefaultEditModel = baseLoaded.DefaultEditModel,
                AiTemperature = baseLoaded.AiTemperature,
                PrivacyMode = baseLoaded.PrivacyMode,
                RecentWorkspaces = baseLoaded.RecentWorkspaces ?? new List<string>(),
                RecentFiles = baseLoaded.RecentFiles ?? new List<string>(),
                OpenTabPaths = baseLoaded.OpenTabPaths ?? new List<string>(),
                Vendors = baseLoaded.Vendors ?? new List<SavedVendorEntry>()
            };
        }
    }

    public class GitStatusEntry
    {
        public string FullPath { get; set; } = "";
        public string RelativePath { get; set; } = "";
        public char IndexStatus { get; set; } = ' ';
        public char WorkingTreeStatus { get; set; } = 'M';
        public bool IsStaged => IndexStatus != ' ' && IndexStatus != '?';
        public bool IsUnstaged => WorkingTreeStatus != ' ';
        public string DisplayBadge => WorkingTreeStatus != ' ' ? WorkingTreeStatus.ToString() : IndexStatus.ToString();
    }

    public class ProviderTemplateInfo
    {
        public string Name { get; set; } = "";
        public string DefaultModel { get; set; } = "";
        public string[] SuggestedModels { get; set; } = Array.Empty<string>();
    }

    public partial class LlmHarnessAndAgentEngine
    {
        public static List<ProviderTemplateInfo> GetAllProviderTemplates()
        {
            var dummy = new LlmHarnessAndAgentEngine(new IdePersistentSettings());
            return dummy.Vendors.Select(v => new ProviderTemplateInfo
            {
                Name = v.Name,
                DefaultModel = v.SelectedModel,
                SuggestedModels = v.Models ?? new[] { v.SelectedModel }
            }).ToList();
        }

        public static ProviderTemplateInfo GetProviderTemplate(string nameOrId)
        {
            var all = GetAllProviderTemplates();
            return all.FirstOrDefault(p =>
                       string.Equals(p.Name, nameOrId, StringComparison.OrdinalIgnoreCase) ||
                       p.Name.IndexOf(nameOrId ?? "", StringComparison.OrdinalIgnoreCase) >= 0)
                   ?? all.First();
        }

        public static string GetGitBranch(string rootDir)
        {
            if (string.IsNullOrEmpty(rootDir) || !Directory.Exists(rootDir)) return "no-git";
            try
            {
                string cur = rootDir;
                while (!string.IsNullOrEmpty(cur))
                {
                    string headFile = Path.Combine(cur, ".git", "HEAD");
                    if (File.Exists(headFile))
                    {
                        string raw = File.ReadAllText(headFile).Trim();
                        const string prefix = "ref: refs/heads/";
                        if (raw.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                            return raw.Substring(prefix.Length).Trim();
                        if (raw.Length >= 7)
                            return raw.Substring(0, 7);
                        break;
                    }
                    var parent = Directory.GetParent(cur);
                    cur = parent?.FullName;
                }
            }
            catch { }
            return "no-git";
        }

        public static List<GitStatusEntry> GetGitStatusEntries(string rootDir)
        {
            var list = new List<GitStatusEntry>();
            if (string.IsNullOrEmpty(rootDir) || !Directory.Exists(rootDir)) return list;
            string raw = RunQuickProcess("git", "status --porcelain", rootDir, 4000, out int code);
            if (code != 0 || string.IsNullOrWhiteSpace(raw)) return list;

            foreach (string line in raw.Replace("\r\n", "\n").Split('\n'))
            {
                if (line.Length < 4) continue;
                char x = line[0];
                char y = line[1];
                string rel = line.Substring(3).Trim().Trim('"');
                int arrow = rel.IndexOf(" -> ", StringComparison.Ordinal);
                if (arrow >= 0) rel = rel.Substring(arrow + 4).Trim().Trim('"');
                string full = Path.GetFullPath(Path.Combine(rootDir, rel));
                list.Add(new GitStatusEntry
                {
                    FullPath = full,
                    RelativePath = rel,
                    IndexStatus = x,
                    WorkingTreeStatus = y
                });
            }
            return list;
        }

        public static string RunQuickProcess(string exe, string args, string workDir, int timeoutMs)
        {
            return RunQuickProcess(exe, args, workDir, timeoutMs, out _);
        }

        public static string RunQuickProcess(string exe, string args, string workDir, int timeoutMs, out int exitCode)
        {
            exitCode = -1;
            try
            {
                var psi = new ProcessStartInfo(exe, args)
                {
                    WorkingDirectory = !string.IsNullOrEmpty(workDir) && Directory.Exists(workDir) ? workDir : Environment.CurrentDirectory,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                };
                using var p = Process.Start(psi);
                if (p == null) return "";
                Task<string> outTask = p.StandardOutput.ReadToEndAsync();
                Task<string> errTask = p.StandardError.ReadToEndAsync();
                bool exited = p.WaitForExit(Math.Max(500, timeoutMs));
                if (!exited)
                {
                    try { p.Kill(); } catch { }
                    exitCode = -1;
                }
                else
                {
                    exitCode = p.ExitCode;
                }
                Task.WaitAll(new Task[] { outTask, errTask }, 1000);
                string stdout = outTask.IsCompleted ? outTask.Result : "";
                string stderr = errTask.IsCompleted ? errTask.Result : "";
                return !string.IsNullOrEmpty(stdout) ? stdout : stderr;
            }
            catch
            {
                return "";
            }
        }

        public void IndexWorkspaceSymbols(string rootDir, List<string> files)
        {
            if (WorkspaceSymbols == null) WorkspaceSymbols = new List<string>();
            else WorkspaceSymbols.Clear();
            if (WorkspaceSymbolMap == null) WorkspaceSymbolMap = new List<WorkspaceSymbolLocation>();
            else WorkspaceSymbolMap.Clear();

            if (files == null) return;
            var rx = new Regex(@"\b(class|struct|interface|enum|def|function|fn)\s+([A-Za-z_][A-Za-z0-9_]*)", RegexOptions.Compiled);
            foreach (string file in files.Take(250))
            {
                if (!File.Exists(file)) continue;
                try
                {
                    string[] lines = File.ReadAllLines(file);
                    for (int i = 0; i < lines.Length; i++)
                    {
                        var m = rx.Match(lines[i]);
                        if (m.Success)
                        {
                            string kind = m.Groups[1].Value;
                            string name = m.Groups[2].Value;
                            WorkspaceSymbols.Add(name);
                            WorkspaceSymbolMap.Add(new WorkspaceSymbolLocation
                            {
                                SymbolName = name,
                                Kind = kind,
                                FilePath = file,
                                RelativePath = GetRelativePath(rootDir, file),
                                Line = i + 1
                            });
                        }
                    }
                }
                catch { }
            }
        }
    }
}
#endif
