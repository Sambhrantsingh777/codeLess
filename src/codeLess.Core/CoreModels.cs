using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
#if NETFRAMEWORK
using System.Drawing.Drawing2D;
#endif
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Web.Script.Serialization;
namespace codeLess.Studio
{
    public static class NativeMemoryTrimmer
    {
        public static void TrimWorkingSet(bool fullGc = false)
        {
            // Perform managed garbage collection only; avoid Win32 working-set trimming APIs.
            try
            {
                if (fullGc)
                {
                    GC.Collect(GC.MaxGeneration, GCCollectionMode.Optimized, false);
                }
            }
            catch { }
        }
    }
    public class AgentToolDefinition
    {
        public string Name;
        public string[] Aliases;
        public string Description;
        public string JsonSchema;
        public bool IsReadOnly;
    }

    public partial class StudioTheme
    {
        public string Id;
        public string Name;
        public Color BgWorkbench;
        public Color BgActivity;
        public Color BgSidebar;
        public Color BgEditor;
        public Color BgElevated;
        public Color BgHover;
        public Color BgLineHighlight;
        public Color BgSelection;
        public Color BgStatus;
        public Color BorderSubtle;
        public Color AccentPrimary;
        public Color AccentSecondary;
        public Color FgPrimary;
        public Color FgSecondary;
        public Color FgMuted;
        public Color TokenKeyword;
        public Color TokenControl;
        public Color TokenFunction;
        public Color TokenString;
        public Color TokenComment;
        public Color TokenNumber;
        public Color TokenType;
        public Color TokenOperator;
        public bool IsLight { get { return BgEditor.R > 180; } }

        /// <summary>Default theme id (Savatarr brand: neutral graphite greys + Savatarr blue #1978E5).</summary>
        public const string DefaultThemeId = "savatarr-graphite";

        private static StudioTheme[] _cachedBuiltInThemes;

        internal static StudioTheme[] GetBuiltInThemesSharedArray()
        {
            if (_cachedBuiltInThemes != null) return _cachedBuiltInThemes;
            _cachedBuiltInThemes = CreateBuiltInThemesArray();
            return _cachedBuiltInThemes;
        }

        public static List<StudioTheme> GetBuiltInThemes()
        {
            return new List<StudioTheme>(GetBuiltInThemesSharedArray());
        }

        private static StudioTheme[] CreateBuiltInThemesArray()
        {
            return new StudioTheme[]
            {
                new StudioTheme
                {
                    // Matches savatarr.com: ink greys (#0E1420 / #4B5768 / #5B6472), light surface text (#E3E7ED)
                    // and the Savatarr brand blue (#1978E5, hover #125FBD) as the single accent.
                    Id = "savatarr-graphite",
                    Name = "Savatarr Graphite (Default)",
                    BgWorkbench = Hex("#17191d"),
                    BgActivity = Hex("#1b1d21"),
                    BgSidebar = Hex("#1e2024"),
                    BgEditor = Hex("#232529"),
                    BgElevated = Hex("#2b2d32"),
                    BgHover = Hex("#33363c"),
                    BgLineHighlight = Hex("#2a2c31"),
                    BgSelection = Color.FromArgb(95, 25, 120, 229),
                    BgStatus = Hex("#1978e5"),
                    BorderSubtle = Hex("#34373d"),
                    AccentPrimary = Hex("#1978e5"),
                    AccentSecondary = Hex("#3fb68b"),
                    FgPrimary = Hex("#e3e7ed"),
                    FgSecondary = Hex("#a0a8b3"),
                    FgMuted = Hex("#6e7681"),
                    TokenKeyword = Hex("#5aa7ff"),
                    TokenControl = Hex("#f47067"),
                    TokenFunction = Hex("#dcbdfb"),
                    TokenString = Hex("#8ddb8c"),
                    TokenComment = Hex("#737b87"),
                    TokenNumber = Hex("#f69d50"),
                    TokenType = Hex("#56d4dd"),
                    TokenOperator = Hex("#adbac7")
                },
                new StudioTheme
                {
                    Id = "codeless-obsidian",
                    Name = "codeLess Obsidian Pro",
                    BgWorkbench = Hex("#07080c"),
                    BgActivity = Hex("#0a0b10"),
                    BgSidebar = Hex("#0e1017"),
                    BgEditor = Hex("#12141d"),
                    BgElevated = Hex("#191c28"),
                    BgHover = Hex("#222636"),
                    BgLineHighlight = Hex("#1a1d2a"),
                    BgSelection = Color.FromArgb(90, 99, 102, 241),
                    BgStatus = Hex("#007acc"),
                    BorderSubtle = Hex("#232738"),
                    AccentPrimary = Hex("#6366f1"),
                    AccentSecondary = Hex("#10b981"),
                    FgPrimary = Hex("#e2e8f0"),
                    FgSecondary = Hex("#94a3b8"),
                    FgMuted = Hex("#64748b"),
                    TokenKeyword = Hex("#c084fc"),
                    TokenControl = Hex("#f472b6"),
                    TokenFunction = Hex("#60a5fa"),
                    TokenString = Hex("#6ee7b7"),
                    TokenComment = Hex("#525b70"),
                    TokenNumber = Hex("#fbbf24"),
                    TokenType = Hex("#38bdf8"),
                    TokenOperator = Hex("#94a3b8")
                },
                new StudioTheme
                {
                    Id = "sublime-mariana",
                    Name = "Sublime Mariana Dark",
                    BgWorkbench = Hex("#232830"),
                    BgActivity = Hex("#262b34"),
                    BgSidebar = Hex("#2b303b"),
                    BgEditor = Hex("#303841"),
                    BgElevated = Hex("#38414c"),
                    BgHover = Hex("#434e5b"),
                    BgLineHighlight = Hex("#39424d"),
                    BgSelection = Color.FromArgb(95, 95, 179, 179),
                    BgStatus = Hex("#232830"),
                    BorderSubtle = Hex("#3e4754"),
                    AccentPrimary = Hex("#5fb3b3"),
                    AccentSecondary = Hex("#f99157"),
                    FgPrimary = Hex("#d8dee9"),
                    FgSecondary = Hex("#a6acb9"),
                    FgMuted = Hex("#65737e"),
                    TokenKeyword = Hex("#c594c5"),
                    TokenControl = Hex("#ec5f67"),
                    TokenFunction = Hex("#6699cc"),
                    TokenString = Hex("#99c794"),
                    TokenComment = Hex("#65737e"),
                    TokenNumber = Hex("#f99157"),
                    TokenType = Hex("#fac863"),
                    TokenOperator = Hex("#5fb3b3")
                },
                new StudioTheme
                {
                    Id = "sublime-monokai",
                    Name = "Sublime Monokai Pro",
                    BgWorkbench = Hex("#19181a"),
                    BgActivity = Hex("#1c1b1d"),
                    BgSidebar = Hex("#221f22"),
                    BgEditor = Hex("#2d2a2e"),
                    BgElevated = Hex("#363337"),
                    BgHover = Hex("#403e41"),
                    BgLineHighlight = Hex("#353236"),
                    BgSelection = Color.FromArgb(85, 166, 226, 46),
                    BgStatus = Hex("#19181a"),
                    BorderSubtle = Hex("#3b383c"),
                    AccentPrimary = Hex("#ffd866"),
                    AccentSecondary = Hex("#a9dc76"),
                    FgPrimary = Hex("#fcfcfa"),
                    FgSecondary = Hex("#c1c0c0"),
                    FgMuted = Hex("#727072"),
                    TokenKeyword = Hex("#ff6188"),
                    TokenControl = Hex("#ff6188"),
                    TokenFunction = Hex("#a9dc76"),
                    TokenString = Hex("#ffd866"),
                    TokenComment = Hex("#727072"),
                    TokenNumber = Hex("#ab9df2"),
                    TokenType = Hex("#78dce8"),
                    TokenOperator = Hex("#ff6188")
                },
                new StudioTheme
                {
                    Id = "cursor-carbon",
                    Name = "Carbon Midnight Pro",
                    BgWorkbench = Hex("#09090b"),
                    BgActivity = Hex("#0c0c0e"),
                    BgSidebar = Hex("#101013"),
                    BgEditor = Hex("#141417"),
                    BgElevated = Hex("#1c1c21"),
                    BgHover = Hex("#24242b"),
                    BgLineHighlight = Hex("#1d1d23"),
                    BgSelection = Color.FromArgb(85, 59, 130, 246),
                    BgStatus = Hex("#0c0c0e"),
                    BorderSubtle = Hex("#26262e"),
                    AccentPrimary = Hex("#3b82f6"),
                    AccentSecondary = Hex("#10b981"),
                    FgPrimary = Hex("#e4e4e7"),
                    FgSecondary = Hex("#a1a1aa"),
                    FgMuted = Hex("#63636e"),
                    TokenKeyword = Hex("#c084fc"),
                    TokenControl = Hex("#f472b6"),
                    TokenFunction = Hex("#60a5fa"),
                    TokenString = Hex("#86efac"),
                    TokenComment = Hex("#5b5b66"),
                    TokenNumber = Hex("#fdba74"),
                    TokenType = Hex("#67e8f9"),
                    TokenOperator = Hex("#a1a1aa")
                },
                new StudioTheme
                {
                    Id = "zed-one-dark",
                    Name = "Zed One Dark Studio",
                    BgWorkbench = Hex("#181a1f"),
                    BgActivity = Hex("#181a1f"),
                    BgSidebar = Hex("#21252b"),
                    BgEditor = Hex("#282c34"),
                    BgElevated = Hex("#2c313a"),
                    BgHover = Hex("#333842"),
                    BgLineHighlight = Hex("#2c313c"),
                    BgSelection = Color.FromArgb(85, 97, 175, 239),
                    BgStatus = Hex("#181a1f"),
                    BorderSubtle = Hex("#343942"),
                    AccentPrimary = Hex("#528bff"),
                    AccentSecondary = Hex("#98c379"),
                    FgPrimary = Hex("#abb2bf"),
                    FgSecondary = Hex("#8b929e"),
                    FgMuted = Hex("#5c6370"),
                    TokenKeyword = Hex("#c678dd"),
                    TokenControl = Hex("#e06c75"),
                    TokenFunction = Hex("#61afef"),
                    TokenString = Hex("#98c379"),
                    TokenComment = Hex("#5c6370"),
                    TokenNumber = Hex("#d19a66"),
                    TokenType = Hex("#e5c07b"),
                    TokenOperator = Hex("#56b6c2")
                },
                new StudioTheme
                {
                    Id = "tokyo-night",
                    Name = "Tokyo Night Storm",
                    BgWorkbench = Hex("#13141c"),
                    BgActivity = Hex("#13141c"),
                    BgSidebar = Hex("#16161e"),
                    BgEditor = Hex("#1a1b26"),
                    BgElevated = Hex("#202332"),
                    BgHover = Hex("#282c3e"),
                    BgLineHighlight = Hex("#1f2335"),
                    BgSelection = Color.FromArgb(85, 122, 162, 247),
                    BgStatus = Hex("#13141c"),
                    BorderSubtle = Hex("#292e42"),
                    AccentPrimary = Hex("#7aa2f7"),
                    AccentSecondary = Hex("#9ece6a"),
                    FgPrimary = Hex("#c0caf5"),
                    FgSecondary = Hex("#9aa5ce"),
                    FgMuted = Hex("#565f89"),
                    TokenKeyword = Hex("#bb9af7"),
                    TokenControl = Hex("#f7768e"),
                    TokenFunction = Hex("#7aa2f7"),
                    TokenString = Hex("#9ece6a"),
                    TokenComment = Hex("#565f89"),
                    TokenNumber = Hex("#ff9e64"),
                    TokenType = Hex("#2ac3de"),
                    TokenOperator = Hex("#89ddff")
                },
                new StudioTheme
                {
                    Id = "catppuccin-mocha",
                    Name = "Catppuccin Mocha",
                    BgWorkbench = Hex("#11111b"),
                    BgActivity = Hex("#11111b"),
                    BgSidebar = Hex("#181825"),
                    BgEditor = Hex("#1e1e2e"),
                    BgElevated = Hex("#262639"),
                    BgHover = Hex("#313244"),
                    BgLineHighlight = Hex("#262637"),
                    BgSelection = Color.FromArgb(80, 203, 166, 247),
                    BgStatus = Hex("#11111b"),
                    BorderSubtle = Hex("#313244"),
                    AccentPrimary = Hex("#cba6f7"),
                    AccentSecondary = Hex("#a6e3a1"),
                    FgPrimary = Hex("#cdd6f4"),
                    FgSecondary = Hex("#a6adc8"),
                    FgMuted = Hex("#6c7086"),
                    TokenKeyword = Hex("#cba6f7"),
                    TokenControl = Hex("#f38ba8"),
                    TokenFunction = Hex("#89b4fa"),
                    TokenString = Hex("#a6e3a1"),
                    TokenComment = Hex("#6c7086"),
                    TokenNumber = Hex("#fab387"),
                    TokenType = Hex("#f9e2af"),
                    TokenOperator = Hex("#89dceb")
                },
                new StudioTheme
                {
                    Id = "github-dark",
                    Name = "GitHub Dark Default",
                    BgWorkbench = Hex("#010409"),
                    BgActivity = Hex("#010409"),
                    BgSidebar = Hex("#090c10"),
                    BgEditor = Hex("#0d1117"),
                    BgElevated = Hex("#161b22"),
                    BgHover = Hex("#21262d"),
                    BgLineHighlight = Hex("#161b22"),
                    BgSelection = Color.FromArgb(85, 47, 129, 247),
                    BgStatus = Hex("#010409"),
                    BorderSubtle = Hex("#21262d"),
                    AccentPrimary = Hex("#2f81f7"),
                    AccentSecondary = Hex("#3fb950"),
                    FgPrimary = Hex("#e6edf3"),
                    FgSecondary = Hex("#8b949e"),
                    FgMuted = Hex("#6e7681"),
                    TokenKeyword = Hex("#ff7b72"),
                    TokenControl = Hex("#ff7b72"),
                    TokenFunction = Hex("#d2a8ff"),
                    TokenString = Hex("#a5d6ff"),
                    TokenComment = Hex("#8b949e"),
                    TokenNumber = Hex("#79c0ff"),
                    TokenType = Hex("#ffa657"),
                    TokenOperator = Hex("#79c0ff")
                },
                new StudioTheme
                {
                    Id = "fleet-islands",
                    Name = "JetBrains Fleet Dark",
                    BgWorkbench = Hex("#111113"),
                    BgActivity = Hex("#111113"),
                    BgSidebar = Hex("#18181b"),
                    BgEditor = Hex("#1e1f22"),
                    BgElevated = Hex("#25262a"),
                    BgHover = Hex("#2b2d30"),
                    BgLineHighlight = Hex("#26282e"),
                    BgSelection = Color.FromArgb(85, 53, 116, 240),
                    BgStatus = Hex("#111113"),
                    BorderSubtle = Hex("#2b2d30"),
                    AccentPrimary = Hex("#3574f0"),
                    AccentSecondary = Hex("#59a869"),
                    FgPrimary = Hex("#bcbec4"),
                    FgSecondary = Hex("#868a91"),
                    FgMuted = Hex("#5a5d63"),
                    TokenKeyword = Hex("#cf8e6d"),
                    TokenControl = Hex("#cf8e6d"),
                    TokenFunction = Hex("#56a8f5"),
                    TokenString = Hex("#6aab73"),
                    TokenComment = Hex("#7a7e85"),
                    TokenNumber = Hex("#2aacb8"),
                    TokenType = Hex("#b3ae60"),
                    TokenOperator = Hex("#bcbec4")
                },
                new StudioTheme
                {
                    Id = "github-daylight",
                    Name = "Daylight High-Contrast Light",
                    BgWorkbench = Hex("#f6f8fa"),
                    BgActivity = Hex("#eaeef2"),
                    BgSidebar = Hex("#f6f8fa"),
                    BgEditor = Hex("#ffffff"),
                    BgElevated = Hex("#eaeef2"),
                    BgHover = Hex("#dde3ea"),
                    BgLineHighlight = Hex("#f0f6fc"),
                    BgSelection = Color.FromArgb(75, 9, 105, 218),
                    BgStatus = Hex("#eaeef2"),
                    BorderSubtle = Hex("#d0d7de"),
                    AccentPrimary = Hex("#0969da"),
                    AccentSecondary = Hex("#1a7f37"),
                    FgPrimary = Hex("#1f2328"),
                    FgSecondary = Hex("#59636e"),
                    FgMuted = Hex("#8c959f"),
                    TokenKeyword = Hex("#cf222e"),
                    TokenControl = Hex("#cf222e"),
                    TokenFunction = Hex("#8250df"),
                    TokenString = Hex("#0a3069"),
                    TokenComment = Hex("#6e7781"),
                    TokenNumber = Hex("#0550ae"),
                    TokenType = Hex("#953800"),
                    TokenOperator = Hex("#0550ae")
                }
            };
        }

        public static StudioTheme ResolveTheme(List<StudioTheme> list, string idOrName)
        {
            if (list == null || list.Count == 0)
                return ResolveThemeFromShared(idOrName, null);
            if (!string.IsNullOrWhiteSpace(idOrName))
            {
                string q = idOrName.Trim();
                for (int i = 0; i < list.Count; i++)
                {
                    StudioTheme t = list[i];
                    if (t != null && (string.Equals(t.Id, q, StringComparison.OrdinalIgnoreCase) ||
                                      string.Equals(t.Name, q, StringComparison.OrdinalIgnoreCase)))
                        return t;
                }

                if (q.IndexOf("light", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    q.IndexOf("daylight", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    for (int i = 0; i < list.Count; i++)
                    {
                        if (list[i] != null && list[i].IsLight) return list[i];
                    }
                }
            }
            return list[0];
        }

        internal static StudioTheme ResolveThemeFromShared(string idOrName, List<StudioTheme> extensionThemes)
        {
            StudioTheme[] builtIn = GetBuiltInThemesSharedArray();
            if (!string.IsNullOrWhiteSpace(idOrName))
            {
                string q = idOrName.Trim();
                for (int i = 0; i < builtIn.Length; i++)
                {
                    StudioTheme t = builtIn[i];
                    if (string.Equals(t.Id, q, StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(t.Name, q, StringComparison.OrdinalIgnoreCase))
                        return t;
                }
                if (extensionThemes != null)
                {
                    lock (extensionThemes)
                    {
                        for (int i = 0; i < extensionThemes.Count; i++)
                        {
                            StudioTheme t = extensionThemes[i];
                            if (t != null && (string.Equals(t.Id, q, StringComparison.OrdinalIgnoreCase) ||
                                              string.Equals(t.Name, q, StringComparison.OrdinalIgnoreCase)))
                                return t;
                        }
                    }
                }
                if (q.IndexOf("light", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    q.IndexOf("daylight", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    for (int i = 0; i < builtIn.Length; i++)
                    {
                        if (builtIn[i].IsLight) return builtIn[i];
                    }
                }
            }
            return builtIn[0];
        }

        public static Color Hex(string hex, Color? fallback = null)
        {
            Color fb = fallback ?? Color.FromArgb(18, 20, 29);
            if (string.IsNullOrEmpty(hex)) return fb;
            string clean = hex.Trim().TrimStart('#');
            if (clean.Length >= 6)
            {
                try
                {
                    int r = Convert.ToInt32(clean.Substring(0, 2), 16);
                    int g = Convert.ToInt32(clean.Substring(2, 2), 16);
                    int b = Convert.ToInt32(clean.Substring(4, 2), 16);
                    return Color.FromArgb(r, g, b);
                }
                catch { }
            }
            return fb;
        }

        public static Color Blend(Color bg, Color fg, double fgAlpha)
        {
            int r = (int)Math.Max(0, Math.Min(255, Math.Round(bg.R * (1.0 - fgAlpha) + fg.R * fgAlpha)));
            int g = (int)Math.Max(0, Math.Min(255, Math.Round(bg.G * (1.0 - fgAlpha) + fg.G * fgAlpha)));
            int b = (int)Math.Max(0, Math.Min(255, Math.Round(bg.B * (1.0 - fgAlpha) + fg.B * fgAlpha)));
            return Color.FromArgb(255, r, g, b);
        }

        public Color WarnAmber
        {
            get { return TokenNumber.IsEmpty ? Color.FromArgb(245, 158, 11) : TokenNumber; }
        }

        public Color DangerCoral
        {
            get { return TokenControl.IsEmpty ? Color.FromArgb(244, 63, 94) : TokenControl; }
        }

#if NETFRAMEWORK
        public static GraphicsPath CreateRoundRect(Rectangle r, int radius)
        {
            GraphicsPath gp = new GraphicsPath();
            int d = Math.Max(2, radius * 2);
            if (r.Width < d || r.Height < d)
            {
                gp.AddRectangle(r);
                return gp;
            }
            gp.AddArc(r.X, r.Y, d, d, 180, 90);
            gp.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            gp.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            gp.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            gp.CloseFigure();
            return gp;
        }
#endif
    }

    public class EditorDiffHunk
    {
        public string Id;
        public int StartLine; // 0-based line index in the new content
        public List<string> RemovedLines = new List<string>();
        public List<string> AddedLines = new List<string>();
        public bool IsPending = true;
    }

    public partial class DiagnosticItem
    {
        public string Severity = "Warning"; // "Error", "Warning", "Info"
        public string FilePath = "";
        public string RelativePath = "";
        public int Line = 1; // 1-based
        public int Column = 1; // 1-based
        public int Length = 4;
        public string Message = "";
        public string Source = "codeLess AST Linter";
    }

    public partial class AstSymbolItem
    {
        public string Name = "";
        public string Kind = "fn"; // "class", "fn", "method", "struct", "interface", "prop", "var"
        public int Line = 1; // 1-based
        public string Signature = "";
        public string ComplexityHint = "O(1)";
        public int LineSpan = 1;
    }

    public partial class WorkspaceSymbolLocation
    {
        public string SymbolName;
        public string Kind;
        public string FilePath;
        public string RelativePath;
        public int Line;
    }

    public class DiskCheckpointMetadata
    {
        public string Id;
        public string Timestamp;
        public string Label;
        public string OriginalFilePath;
        public string SnapshotDiskPath;
        public int LineCount;
        public string EolSequence = Environment.NewLine;
        public Encoding FileEncoding = new UTF8Encoding(false);
        public bool HasBom = false;
        public Action OnClosed;
        public bool HasFinalNewline = false;
    }

    public static class TimeTravelCheckpointManager
    {
        private static readonly object syncRoot = new object();
        private static int sequenceCounter = 0;
        public static List<DiskCheckpointMetadata> Checkpoints = new List<DiskCheckpointMetadata>();

        public static List<DiskCheckpointMetadata> ListCheckpoints()
        {
            lock (syncRoot)
            {
                return new List<DiskCheckpointMetadata>(Checkpoints);
            }
        }

        private static string GetCheckpointDir()
        {
            string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codeless", "checkpoints");
            if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
            return dir;
        }

        public static DiskCheckpointMetadata CreateDiskCheckpoint(string filePath, string content, string label)
        {
            try
            {
                if (string.IsNullOrEmpty(filePath)) return null;
                int seq = System.Threading.Interlocked.Increment(ref sequenceCounter);
                string id = "cp-" + DateTime.Now.ToString("HHmmssfff") + "-" + seq;
                string snapFile = Path.Combine(GetCheckpointDir(), id + "_" + Path.GetFileName(filePath) + ".bak");
                string eol = "\r\n";
                Encoding enc = new UTF8Encoding(false);
                bool hasBom = false;
                bool diskFinalNl = false;
                if (File.Exists(filePath))
                {
                    EditorTabBuffer.ReadFilePreservingMetadata(filePath, out eol, out enc, out hasBom, out diskFinalNl);
                }
                string normContent = (content ?? "").Replace("\r\n", "\n").Replace("\r", "\n");
                bool snapFinalNl = normContent.Length > 0 ? (normContent.EndsWith("\n") || diskFinalNl) : false;
                EditorTabBuffer.WriteFilePreservingMetadata(snapFile, normContent, enc, hasBom, eol, snapFinalNl);
                int lines = string.IsNullOrEmpty(normContent) ? 1 : normContent.Split('\n').Length;
                var meta = new DiskCheckpointMetadata
                {
                    Id = id,
                    Timestamp = DateTime.Now.ToString("HH:mm:ss"),
                    Label = label,
                    OriginalFilePath = filePath,
                    SnapshotDiskPath = snapFile,
                    LineCount = lines,
                    EolSequence = eol,
                    FileEncoding = enc,
                    HasBom = hasBom,
                    HasFinalNewline = snapFinalNl
                };
                lock (syncRoot)
                {
                    Checkpoints.Insert(0, meta);
                    while (Checkpoints.Count > 30)
                    {
                        DiskCheckpointMetadata evicted = Checkpoints[Checkpoints.Count - 1];
                        Checkpoints.RemoveAt(Checkpoints.Count - 1);
                        if (evicted != null && !string.IsNullOrEmpty(evicted.SnapshotDiskPath))
                        {
                            try { if (File.Exists(evicted.SnapshotDiskPath)) File.Delete(evicted.SnapshotDiskPath); } catch { }
                        }
                    }
                }
                return meta;
            }
            catch { return null; }
        }

        public static string RestoreFromCheckpoint(DiskCheckpointMetadata meta)
        {
            try
            {
                if (meta == null || string.IsNullOrEmpty(meta.SnapshotDiskPath) || !File.Exists(meta.SnapshotDiskPath))
                    return null;
                string snapEol;
                Encoding snapEnc;
                bool snapBom;
                bool snapFinalNl;
                string content = EditorTabBuffer.ReadFilePreservingMetadata(meta.SnapshotDiskPath, out snapEol, out snapEnc, out snapBom, out snapFinalNl);
                if (!string.IsNullOrEmpty(meta.OriginalFilePath))
                {
                    string eol = !string.IsNullOrEmpty(meta.EolSequence) ? meta.EolSequence : snapEol;
                    Encoding enc = meta.FileEncoding ?? snapEnc;
                    bool hasBom = meta.HasBom || snapBom;
                    bool finalNl = snapFinalNl;
                    if (File.Exists(meta.OriginalFilePath))
                    {
                        string curEol;
                        Encoding curEnc;
                        bool curBom;
                        bool curFinalNl;
                        EditorTabBuffer.ReadFilePreservingMetadata(meta.OriginalFilePath, out curEol, out curEnc, out curBom, out curFinalNl);
                        if (string.IsNullOrEmpty(meta.EolSequence)) eol = curEol;
                        if (meta.FileEncoding == null) enc = curEnc;
                    }
                    EditorTabBuffer.WriteFilePreservingMetadata(meta.OriginalFilePath, content, enc, hasBom, eol, finalNl);
                }
                return content;
            }
            catch { return null; }
        }
    }

    /// <summary>
    /// One undo / redo step. The entry on top of a stack holds the full line array (shared string references, no
    /// text copy); older entries are compacted to a delta against the entry above them (changed lines only), so a
    /// 50,000-line file costs a few hundred bytes per step instead of a full copy of the text.
    /// </summary>
    public class BufferSnapshot
    {
        public int CursorLine;
        public int CursorCol;
        /// <summary>Selection anchor of the primary caret (-1 when there was no selection).</summary>
        public int AnchorLine = -1;
        public int AnchorCol = -1;
        /// <summary>Extra carets (with their selections) at the time of the snapshot; null when there were none (QA #29).</summary>
        public EditorCaret[] ExtraCarets;
        internal string[] FullLines;
        internal int DeltaPrefix;
        internal int DeltaSuffix;
        internal string[] DeltaMiddle;
        private string cachedContent;

        /// <summary>Full text of a top-of-stack snapshot (null for a compacted entry).</summary>
        public string Content
        {
            get
            {
                if (FullLines == null) return null;
                if (cachedContent == null) cachedContent = string.Join("\n", FullLines);
                return cachedContent;
            }
            set
            {
                cachedContent = value ?? "";
                FullLines = EditorTabBuffer.SplitNormalizedLines(cachedContent);
                DeltaMiddle = null;
            }
        }

        public bool IsCompacted { get { return FullLines == null; } }

        internal long ApproxBytes
        {
            get
            {
                if (FullLines != null) return 32L + 8L * FullLines.Length;
                long b = 48;
                if (DeltaMiddle != null)
                    for (int i = 0; i < DeltaMiddle.Length; i++) b += 8 + 2L * (DeltaMiddle[i] ?? "").Length;
                return b;
            }
        }

        /// <summary>Compacts this (full) snapshot to a delta against <paramref name="above"/> (the next state).</summary>
        internal void CompactAgainst(string[] above)
        {
            if (FullLines == null || above == null) return;
            string[] a = FullLines;
            int n = a.Length, m = above.Length, min = Math.Min(n, m);
            int p = 0;
            while (p < min && (ReferenceEquals(a[p], above[p]) || string.Equals(a[p], above[p], StringComparison.Ordinal))) p++;
            int s = 0;
            while (s < min - p && (ReferenceEquals(a[n - 1 - s], above[m - 1 - s]) || string.Equals(a[n - 1 - s], above[m - 1 - s], StringComparison.Ordinal))) s++;
            string[] mid = new string[n - p - s];
            Array.Copy(a, p, mid, 0, mid.Length);
            DeltaPrefix = p;
            DeltaSuffix = s;
            DeltaMiddle = mid;
            FullLines = null;
            cachedContent = null;
        }

        /// <summary>Rebuilds the full line array from a delta, given the state it was compacted against.</summary>
        internal void ExpandFrom(string[] above)
        {
            if (FullLines != null || above == null || DeltaMiddle == null) return;
            int p = Math.Min(DeltaPrefix, above.Length);
            int s = Math.Min(DeltaSuffix, above.Length - p);
            string[] res = new string[p + DeltaMiddle.Length + s];
            Array.Copy(above, 0, res, 0, p);
            Array.Copy(DeltaMiddle, 0, res, p, DeltaMiddle.Length);
            Array.Copy(above, above.Length - s, res, p + DeltaMiddle.Length, s);
            FullLines = res;
            DeltaMiddle = null;
        }
    }

    public class EditorCaret
    {
        public int Line;
        public int Col;
        public int AnchorLine = -1;
        public int AnchorCol = -1;

        public int CursorLine
        {
            get { return Line; }
            set { Line = value; }
        }

        public int CursorCol
        {
            get { return Col; }
            set { Col = value; }
        }

        public bool HasSelection()
        {
            return AnchorLine >= 0 && AnchorCol >= 0 && (AnchorLine != Line || AnchorCol != Col);
        }

        public void GetNormalizedRange(out int startLine, out int startCol, out int endLine, out int endCol)
        {
            GetNormalizedSelection(out startLine, out startCol, out endLine, out endCol);
        }

        public void GetNormalizedSelection(out int startLine, out int startCol, out int endLine, out int endCol)
        {
            if (!HasSelection())
            {
                startLine = endLine = Line;
                startCol = endCol = Col;
                return;
            }
            if (AnchorLine < Line || (AnchorLine == Line && AnchorCol <= Col))
            {
                startLine = AnchorLine;
                startCol = AnchorCol;
                endLine = Line;
                endCol = Col;
            }
            else
            {
                startLine = Line;
                startCol = Col;
                endLine = AnchorLine;
                endCol = AnchorCol;
            }
        }
    }

    public partial class EditorTabBuffer
    {
        public string FilePath;
        public string RelativePath;
        public string FileName;
        public string Language;
        public string SavedContent = "";
        public string GitHeadContent = null;
        public string PreDiffContent = null;
        public List<string> Lines = new List<string> { "" };
        public bool IsDirty = false;
        public bool IsUntitled = false;
        public bool IsDeletedOnDisk = false;
        public bool HasExternalDiskConflict = false;
        public long DiskLastWriteUtcTicks = 0;
        public long DiskLastLength = 0;
        public bool IsPreview = false;
        public bool IsPinned = false;
        public int? CustomTabSize = null;
        public bool? CustomInsertSpaces = null;
        public bool IsReadOnlyDiffPreview = false;
        public bool IsReadOnly = false;
        public string EolSequence = "\r\n";
        public Encoding FileEncoding = new UTF8Encoding(false);
        public bool HasBom = false;
        public Action OnClosed;
        public bool HasFinalNewline = true;

        // Per-line gutter diff sets (0-based document line indices)
        public HashSet<int> GutterModifiedLines = new HashSet<int>();
        public HashSet<int> GutterAddedLines = new HashSet<int>();
        /// <summary>Lines were deleted right after this line (-1: before the first line). VS Code's red "deleted" gutter / ruler marker.</summary>
        public HashSet<int> GutterDeletedAfterLine = new HashSet<int>();
        public int ChangedLineCount = 0;

        public string EolLabel
        {
            get { return EolSequence == "\n" ? "LF" : (EolSequence == "\r" ? "CR" : "CRLF"); }
        }

        public string EncodingLabel
        {
            get
            {
                if (FileEncoding == null) return "UTF-8";
                int cp = FileEncoding.CodePage;
                if (cp == 65001) return HasBom ? "UTF-8 BOM" : "UTF-8";
                if (cp == 1200) return "UTF-16 LE";
                if (cp == 1201) return "UTF-16 BE";
                if (cp == 12000) return "UTF-32 LE";
                if (cp == 12001) return "UTF-32 BE";
                if (cp == 28591) return "ISO-8859-1";
                return FileEncoding.WebName.ToUpperInvariant();
            }
        }

        public string StatusEncodingDisplayName
        {
            get
            {
                if (FileEncoding == null) return "UTF-8";
                int cp = FileEncoding.CodePage;
                if (cp == 65001) return HasBom ? "UTF-8 with BOM" : "UTF-8";
                if (cp == 1200) return "UTF-16 LE";
                if (cp == 1201) return "UTF-16 BE";
                if (cp == 12000) return "UTF-32 LE";
                if (cp == 12001) return "UTF-32 BE";
                if (cp == 28591) return "ISO-8859-1";
                return (FileEncoding.WebName ?? "UTF-8").ToUpperInvariant();
            }
        }

        public void RecordDiskBaseline(string filePath)
        {
            string p = string.IsNullOrEmpty(filePath) ? FilePath : filePath;
            if (string.IsNullOrEmpty(p) || !File.Exists(p))
            {
                DiskLastWriteUtcTicks = 0;
                DiskLastLength = 0;
                return;
            }
            try
            {
                FileInfo fi = new FileInfo(p);
                DiskLastWriteUtcTicks = fi.LastWriteTimeUtc.Ticks;
                DiskLastLength = fi.Length;
            }
            catch { }
        }

        public bool HasDiskModifiedExternally(out string currentDiskContent)
        {
            currentDiskContent = null;
            if (IsUntitled || string.IsNullOrEmpty(FilePath) || !File.Exists(FilePath))
                return false;
            try
            {
                Encoding enc;
                bool bom;
                string eol;
                bool finalNl;
                currentDiskContent = ReadFilePreservingMetadata(FilePath, out enc, out bom, out eol, out finalNl);
                string normDisk = (currentDiskContent ?? "").Replace("\r\n", "\n").Replace("\r", "\n");
                string normSaved = (SavedContent ?? "").Replace("\r\n", "\n").Replace("\r", "\n");
                if (!string.Equals(normDisk, normSaved, StringComparison.Ordinal))
                    return true;
                if (HasExternalDiskConflict)
                    return true;
                return false;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Encoding detection shared by every reader that must round-trip a file losslessly: BOM (UTF-8/16/32), else strict
        /// UTF-8, else the ANSI code page on .NET Framework / Latin-1 on .NET 8 (both map every byte, so nothing is lost).
        /// </summary>
        public static Encoding DetectFileEncoding(byte[] rawBytes, out bool hasBom, out int bomLen)
        {
            int unused;
            return DetectFileEncoding(rawBytes, false, out hasBom, out unused, out bomLen);
        }

        public static Encoding DetectFileEncoding(byte[] rawBytes, bool autoGuessEncoding, out bool hasBom, out int bomLen)
        {
            int unused;
            return DetectFileEncoding(rawBytes, autoGuessEncoding, out hasBom, out unused, out bomLen);
        }

        private static Encoding DetectFileEncoding(byte[] rawBytes, bool autoGuessEncoding, out bool hasBom, out int codeUnitSize, out int bomLen)
        {
            hasBom = false;
            bomLen = 0;
            codeUnitSize = 1;
            if (rawBytes == null || rawBytes.Length == 0) return new UTF8Encoding(false);
            if (rawBytes.Length >= 3 && rawBytes[0] == 0xEF && rawBytes[1] == 0xBB && rawBytes[2] == 0xBF)
            {
                hasBom = true; bomLen = 3;
                return new UTF8Encoding(true);
            }
            if (rawBytes.Length >= 4 && rawBytes[0] == 0xFF && rawBytes[1] == 0xFE && rawBytes[2] == 0x00 && rawBytes[3] == 0x00)
            {
                hasBom = true; bomLen = 4; codeUnitSize = 4;
                return new UTF32Encoding(false, true);
            }
            if (rawBytes.Length >= 4 && rawBytes[0] == 0x00 && rawBytes[1] == 0x00 && rawBytes[2] == 0xFE && rawBytes[3] == 0xFF)
            {
                hasBom = true; bomLen = 4; codeUnitSize = 4;
                return new UTF32Encoding(true, true);
            }
            if (rawBytes.Length >= 2 && rawBytes[0] == 0xFF && rawBytes[1] == 0xFE)
            {
                hasBom = true; bomLen = 2; codeUnitSize = 2;
                return new UnicodeEncoding(false, true);
            }
            if (rawBytes.Length >= 2 && rawBytes[0] == 0xFE && rawBytes[1] == 0xFF)
            {
                hasBom = true; bomLen = 2; codeUnitSize = 2;
                return new UnicodeEncoding(true, true);
            }
            int sampleLen = Math.Min(rawBytes.Length, 8192);
            int pairs = sampleLen / 2;
            if (pairs >= 4)
            {
                int zeroEven = 0, zeroOdd = 0;
                for (int i = 0; i + 1 < sampleLen; i += 2)
                {
                    if (rawBytes[i] == 0) zeroEven++;
                    if (rawBytes[i + 1] == 0) zeroOdd++;
                }
                if (zeroOdd > pairs * 0.3 && zeroEven < pairs * 0.05)
                {
                    codeUnitSize = 2;
                    return new UnicodeEncoding(false, false);
                }
                if (zeroEven > pairs * 0.3 && zeroOdd < pairs * 0.05)
                {
                    codeUnitSize = 2;
                    return new UnicodeEncoding(true, false);
                }
            }
            try
            {
                UTF8Encoding strictUtf8 = new UTF8Encoding(false, true);
                strictUtf8.GetString(rawBytes);
                return new UTF8Encoding(false);
            }
            catch
            {
                if (autoGuessEncoding)
                {
                    return Encoding.GetEncoding(28591);
                }
#if NETFRAMEWORK
                return Encoding.Default;
#else
                // On .NET Core / .NET 8 Encoding.Default is UTF-8, which would replace every non-UTF-8 byte with U+FFFD
                // and corrupt the file on save. Latin-1 maps bytes 1:1 so the file round-trips losslessly.
                return Encoding.GetEncoding(28591);
#endif
            }
        }

        /// <summary>
        /// Reads a file exactly as stored (line endings untouched, mixed EOLs kept) with the lossless encoding detection of
        /// <see cref="DetectFileEncoding"/>. Pair with <see cref="EncodeTextStrict"/> to write it back byte-identically.
        /// </summary>
        public static string ReadFileExact(string filePath, out Encoding encoding, out bool hasBom)
        {
            byte[] raw = File.ReadAllBytes(filePath);
            int bomLen;
            encoding = DetectFileEncoding(raw, out hasBom, out bomLen);
            return encoding.GetString(raw, bomLen, raw.Length - bomLen);
        }

        /// <summary>
        /// Encodes text for disk in <paramref name="encoding"/> (+ BOM). Throws <see cref="EncoderFallbackException"/>
        /// instead of silently writing '?' when a character can't be represented (e.g. an emoji in a Latin-1 file).
        /// </summary>
        public static byte[] EncodeTextStrict(string text, Encoding encoding, bool hasBom)
        {
            Encoding enc = encoding ?? new UTF8Encoding(hasBom);
            if (enc is UTF8Encoding) enc = new UTF8Encoding(hasBom, true);
            else if (enc is UnicodeEncoding) enc = new UnicodeEncoding(enc.CodePage == 1201, hasBom, true);
            else if (enc is UTF32Encoding) enc = new UTF32Encoding(enc.CodePage == 12001, hasBom, true);
            else enc = Encoding.GetEncoding(enc.CodePage, EncoderFallback.ExceptionFallback, DecoderFallback.ReplacementFallback);
            byte[] preamble = hasBom ? enc.GetPreamble() : new byte[0];
            byte[] body = enc.GetBytes(text ?? "");
            if (preamble.Length == 0) return body;
            byte[] all = new byte[preamble.Length + body.Length];
            Buffer.BlockCopy(preamble, 0, all, 0, preamble.Length);
            Buffer.BlockCopy(body, 0, all, preamble.Length, body.Length);
            return all;
        }

        /// <summary>True when every character of <paramref name="text"/> can be stored in <paramref name="encoding"/>.</summary>
        public static bool CanEncodeLosslessly(string text, Encoding encoding)
        {
            if (string.IsNullOrEmpty(text) || encoding == null) return true;
            try { EncodeTextStrict(text, encoding, false); return true; }
            catch (EncoderFallbackException) { return false; }
        }

        /// <summary>Overwrites a file's bytes in place (keeps its identity, permissions and xattrs, like the editor's Save).</summary>
        public static void WriteBytesInPlace(string filePath, byte[] bytes)
        {
            using (FileStream fs = new FileStream(filePath, FileMode.OpenOrCreate, FileAccess.Write, FileShare.Read))
            {
                fs.SetLength(0);
                if (bytes != null && bytes.Length > 0) fs.Write(bytes, 0, bytes.Length);
            }
        }

        public static string ReadFilePreservingMetadata(
            string filePath,
            out string eolSequence,
            out Encoding encoding,
            out bool hasBom,
            out bool hasFinalNewline)
        {
            return ReadFilePreservingMetadata(filePath, false, out eolSequence, out encoding, out hasBom, out hasFinalNewline);
        }

        public static string ReadFilePreservingMetadata(
            string filePath,
            bool autoGuessEncoding,
            out string eolSequence,
            out Encoding encoding,
            out bool hasBom,
            out bool hasFinalNewline)
        {
            eolSequence = "\r\n";
            encoding = new UTF8Encoding(false);
            hasBom = false;
            hasFinalNewline = true;

            if (string.IsNullOrEmpty(filePath) || !File.Exists(filePath))
                return "";

            byte[] rawBytes = File.ReadAllBytes(filePath);
            if (rawBytes.Length == 0)
            {
                hasFinalNewline = false;
                return "";
            }

            int bomLen;
            encoding = DetectFileEncoding(rawBytes, autoGuessEncoding, out hasBom, out bomLen);

            string decoded = encoding.GetString(rawBytes, bomLen, rawBytes.Length - bomLen);

            int crlfCount = 0;
            int lfCount = 0;
            int crCount = 0;
            int scanLen = decoded.Length;
            for (int i = 0; i < scanLen; i++)
            {
                char c = decoded[i];
                if (c == '\r')
                {
                    if (i + 1 < scanLen && decoded[i + 1] == '\n')
                    {
                        crlfCount++;
                        i++;
                    }
                    else
                    {
                        crCount++;
                    }
                }
                else if (c == '\n')
                {
                    lfCount++;
                }
            }

            if (crlfCount == 0 && lfCount == 0 && crCount == 0)
                eolSequence = "\r\n";
            else if (lfCount > crlfCount && lfCount >= crCount)
                eolSequence = "\n";
            else if (crCount > crlfCount && crCount > lfCount)
                eolSequence = "\r";
            else
                eolSequence = "\r\n";

            if (decoded.Length == 0)
            {
                hasFinalNewline = false;
            }
            else
            {
                char last = decoded[decoded.Length - 1];
                hasFinalNewline = (last == '\n' || last == '\r');
            }

            return decoded.Replace("\r\n", "\n").Replace("\r", "\n");
        }

        public static string ReadFilePreservingMetadata(
            string filePath,
            out Encoding encoding,
            out bool hasBom,
            out string eolSequence,
            out bool hasFinalNewline)
        {
            return ReadFilePreservingMetadata(filePath, false, out eolSequence, out encoding, out hasBom, out hasFinalNewline);
        }

        public static string ReadFilePreservingMetadata(
            string filePath,
            bool autoGuessEncoding,
            out Encoding encoding,
            out bool hasBom,
            out string eolSequence,
            out bool hasFinalNewline)
        {
            return ReadFilePreservingMetadata(filePath, autoGuessEncoding, out eolSequence, out encoding, out hasBom, out hasFinalNewline);
        }

        public string GetNormalizedContentRespectingFinalNewline()
        {
            string norm = (Content ?? "").Replace("\r\n", "\n").Replace("\r", "\n");
            if (norm.Length > 0)
            {
                if (HasFinalNewline && !norm.EndsWith("\n"))
                    norm += "\n";
                else if (!HasFinalNewline && norm.EndsWith("\n"))
                    norm = norm.Substring(0, norm.Length - 1);
            }
            return norm;
        }

        public string GetDiskTextWithMetadata()
        {
            string norm = GetNormalizedContentRespectingFinalNewline();
            string eol = string.IsNullOrEmpty(EolSequence) ? "\r\n" : EolSequence;
            if (eol != "\n")
                norm = norm.Replace("\n", eol);
            return norm;
        }

        public void WriteToDisk(string targetPath)
        {
            string path = string.IsNullOrEmpty(targetPath) ? FilePath : targetPath;
            if (string.IsNullOrEmpty(path)) return;
            SyncContentFromLines();
            string norm = GetNormalizedContentRespectingFinalNewline();
            Content = norm;
            SavedContent = norm;
            IsDirty = false;
            SyncLinesFromContent();
            string dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);
            string diskText = GetDiskTextWithMetadata();
            Encoding used;
            WriteFilePreservingMetadataLossless(path, diskText, FileEncoding, HasBom, out used);
            if (used != null && FileEncoding != null && used.CodePage != FileEncoding.CodePage)
            {
                // Upgraded to UTF-8 because the text no longer fits the legacy encoding: the status bar must say so.
                FileEncoding = used;
                HasBom = false;
            }
            HasExternalDiskConflict = false;
            IsDeletedOnDisk = false;
            RecordDiskBaseline(path);
        }

        public static void WriteFilePreservingMetadata(
            string filePath,
            string normalizedOrRawText,
            Encoding encoding,
            bool hasBom,
            string eolSequence,
            bool hasFinalNewline)
        {
            string norm = (normalizedOrRawText ?? "").Replace("\r\n", "\n").Replace("\r", "\n");
            if (norm.Length > 0)
            {
                if (hasFinalNewline && !norm.EndsWith("\n"))
                    norm += "\n";
                else if (!hasFinalNewline && norm.EndsWith("\n"))
                    norm = norm.Substring(0, norm.Length - 1);
            }
            string eol = string.IsNullOrEmpty(eolSequence) ? "\r\n" : eolSequence;
            if (eol != "\n")
                norm = norm.Replace("\n", eol);
            WriteFilePreservingMetadata(filePath, norm, encoding, hasBom);
        }

        public static void WriteFilePreservingMetadata(string filePath, string exactTextWithEol, Encoding encoding, bool hasBom)
        {
            Encoding used;
            WriteFilePreservingMetadataLossless(filePath, exactTextWithEol, encoding, hasBom, out used);
        }

        /// <summary>
        /// Like <see cref="WriteFilePreservingMetadata(string, string, Encoding, bool)"/>, but when the text contains characters
        /// the file's legacy (ANSI / Latin-1 / other single-byte) encoding can't represent - an emoji, CJK, "→" - the file is
        /// written as UTF-8 instead of silently turning them into '?'. <paramref name="usedEncoding"/> reports what was written.
        /// </summary>
        public static void WriteFilePreservingMetadataLossless(string filePath, string exactTextWithEol, Encoding encoding, bool hasBom, out Encoding usedEncoding)
        {
            Encoding enc = encoding ?? new UTF8Encoding(hasBom);
            if (!(enc is UTF8Encoding) && !(enc is UnicodeEncoding) && !(enc is UTF32Encoding) && !CanEncodeLosslessly(exactTextWithEol, enc))
            {
                enc = new UTF8Encoding(false);
                hasBom = false;
            }
            usedEncoding = enc;
            if (enc is UTF8Encoding)
                enc = new UTF8Encoding(hasBom);
            else if (enc is UnicodeEncoding)
                enc = new UnicodeEncoding(enc.CodePage == 1201, hasBom);
            else if (enc is UTF32Encoding)
                enc = new UTF32Encoding(enc.CodePage == 12001, hasBom);

            byte[] preamble = hasBom ? enc.GetPreamble() : new byte[0];
            byte[] body = enc.GetBytes(exactTextWithEol ?? "");
            // OpenOrCreate + SetLength(0) instead of FileMode.Create: CREATE_ALWAYS fails with "access denied" on
            // Hidden/System files on Windows, and this keeps the file's identity, permissions (exec bit) and xattrs.
            using (FileStream fs = new FileStream(filePath, FileMode.OpenOrCreate, FileAccess.Write, FileShare.Read))
            {
                fs.SetLength(0);
                if (preamble != null && preamble.Length > 0)
                    fs.Write(preamble, 0, preamble.Length);
                if (body != null && body.Length > 0)
                    fs.Write(body, 0, body.Length);
            }
        }

        // 0-based internal cursor & selection coordinates
        public int CursorLine = 0;
        public int CursorCol = 0;
        public int SelectionAnchorLine = -1;
        public int SelectionAnchorCol = -1;
        public List<EditorCaret> ExtraCarets = new List<EditorCaret>();

        // Viewport scroll & display state
        public int ScrollTopLine = 0;
        public int ScrollLeftCol = 0;
        public bool WordWrap = false;

        public void ClearExtraCarets()
        {
            if (ExtraCarets != null) ExtraCarets.Clear();
        }

        public bool HasMultiCursors
        {
            get { return ExtraCarets != null && ExtraCarets.Count > 0; }
        }

        // Inline Ghost Text (AI Tab completion)
        public string GhostText = null;
        public int GhostLine = -1;
        public int GhostCol = -1;

        // Gutter features
        public HashSet<int> Breakpoints = new HashSet<int>(); // 0-based line indices
        public HashSet<int> FoldedLines = new HashSet<int>(); // 0-based header line indices

        // Undo / Redo & Diffs & Diagnostics
        public Stack<BufferSnapshot> UndoStack = new Stack<BufferSnapshot>();

        /// <summary>Undo keeps full-content snapshots; bound history by count and memory (was unbounded).</summary>
        public const int MaxUndoSnapshots = 400;
        public const long MaxUndoBytes = 160L * 1024 * 1024;

        private void TrimUndoHistory()
        {
            if (UndoStack.Count <= MaxUndoSnapshots + 40)
            {
                long approx = 0;
                foreach (BufferSnapshot b in UndoStack) approx += b.ApproxBytes;
                if (approx <= MaxUndoBytes) return;
            }
            BufferSnapshot[] newestFirst = UndoStack.ToArray();
            long budget = 0;
            int keep = 0;
            while (keep < newestFirst.Length && keep < MaxUndoSnapshots)
            {
                budget += newestFirst[keep].ApproxBytes;
                if (budget > MaxUndoBytes && keep >= 20) break;
                keep++;
            }
            Stack<BufferSnapshot> trimmed = new Stack<BufferSnapshot>();
            for (int i = keep - 1; i >= 0; i--) trimmed.Push(newestFirst[i]);
            UndoStack = trimmed;
        }
        public Stack<BufferSnapshot> RedoStack = new Stack<BufferSnapshot>();
        public long LastTypingUndoTick = 0;
        public int LastTypingUndoLine = -1;
        public int LastTypingUndoCol = -1;
        public List<EditorDiffHunk> DiffHunks = new List<EditorDiffHunk>();
        public List<DiagnosticItem> Diagnostics = new List<DiagnosticItem>();
        public List<AstSymbolItem> Symbols = new List<AstSymbolItem>();
        public bool IsLargeFile = false;
        public int GutterDiffVersion = 0;

        private string _cachedSavedRef;
        private string _cachedNormSaved;
        private string _cachedBaseRef;
        private string[] _cachedBaseLines;

        private string GetCachedNormalizedSaved()
        {
            string raw = SavedContent ?? "";
            if (!ReferenceEquals(raw, _cachedSavedRef) || _cachedNormSaved == null)
            {
                _cachedSavedRef = raw;
                _cachedNormSaved = raw.IndexOf('\r') >= 0 ? raw.Replace("\r\n", "\n").Replace("\r", "\n") : raw;
            }
            return _cachedNormSaved;
        }

        private string[] GetCachedBaseLines()
        {
            string baseText = (GitHeadContent != null) ? GitHeadContent : SavedContent;
            if (baseText == null) return null;
            if (ReferenceEquals(baseText, savedLinesRef) && savedLinesCache != null)
            {
                _cachedBaseRef = baseText;
                _cachedBaseLines = savedLinesCache;
                return _cachedBaseLines;
            }
            if (!ReferenceEquals(baseText, _cachedBaseRef) || _cachedBaseLines == null)
            {
                _cachedBaseRef = baseText;
                string norm = baseText.IndexOf('\r') >= 0 ? baseText.Replace("\r\n", "\n").Replace("\r", "\n") : baseText;
                _cachedBaseLines = SplitNormalizedLines(norm);
            }
            return _cachedBaseLines;
        }

        public void SyncLinesFromContent()
        {
            string raw = Content ?? "";
            if (raw.IndexOf('\r') >= 0)
                raw = raw.Replace("\r\n", "\n").Replace("\r", "\n");
            Content = raw;
            string[] split = SplitNormalizedLines(raw);
            Lines = new List<string>(split);
            if (SavedContent != null)
            {
                string normSaved = SavedContent.IndexOf('\r') >= 0 ? SavedContent.Replace("\r\n", "\n").Replace("\r", "\n") : SavedContent;
                if (ReferenceEquals(normSaved, raw) || string.Equals(normSaved, raw, StringComparison.Ordinal))
                {
                    SavedContent = raw;
                    savedLinesRef = raw;
                    savedLinesCache = split;
                }
            }
            if (GitHeadContent != null)
            {
                string normHead = GitHeadContent.IndexOf('\r') >= 0 ? GitHeadContent.Replace("\r\n", "\n").Replace("\r", "\n") : GitHeadContent;
                if (ReferenceEquals(normHead, raw) || string.Equals(normHead, raw, StringComparison.Ordinal))
                {
                    GitHeadContent = raw;
                    _cachedBaseRef = raw;
                    _cachedBaseLines = split;
                }
            }
            OnLinesReplaced();
            IsLargeFile = ComputeIsLargeFile(raw.Length);
            ClampCursor();
            if (!IsLargeFile)
                RefreshGutterDiff();
            else
                GutterDiffVersion++;
        }

        public void SyncContentFromLines()
        {
            SyncContentFromLines(recomputeGutterDiff: true);
        }

        /// <summary>
        /// Call after editing <see cref="Lines"/>. Cheap (no full-text rebuild): the text is materialized lazily by
        /// <see cref="Content"/>; dirty state is derived from the lines (O(1) for the usual single-line edit).
        /// </summary>
        public void SyncContentFromLines(bool recomputeGutterDiff)
        {
            if (Lines == null || Lines.Count == 0)
                Lines = new List<string> { "" };
            MarkLinesChanged();
            IsLargeFile = ComputeIsLargeFile(-1);
            IsDirty = ComputeDirtyFromLines(CursorLine);
            if (recomputeGutterDiff && !IsLargeFile)
                RefreshGutterDiff();
        }

        public void RefreshGutterDiff()
        {
            if (GutterModifiedLines == null) GutterModifiedLines = new HashSet<int>();
            else GutterModifiedLines.Clear();
            if (GutterAddedLines == null) GutterAddedLines = new HashSet<int>();
            else GutterAddedLines.Clear();
            if (GutterDeletedAfterLine == null) GutterDeletedAfterLine = new HashSet<int>();
            else GutterDeletedAfterLine.Clear();
            ChangedLineCount = 0;
            GutterDiffVersion++;

            string baseText = (GitHeadContent != null) ? GitHeadContent : SavedContent;
            if (baseText == null || Lines == null) return;
            if (!contentStale && ReferenceEquals(baseText, contentText)) return;

            string[] oldLines = GetCachedBaseLines();
            if (oldLines == null) return;

            int newCount = Lines.Count;
            if (newCount > 1 && HasFinalNewline && string.IsNullOrEmpty(Lines[newCount - 1]) && oldLines.Length > 0 && !string.IsNullOrEmpty(oldLines[oldLines.Length - 1]))
            {
                // Keep exact line parity
            }

            int start = 0;
            int oldEnd = oldLines.Length - 1;
            int newEnd = newCount - 1;

            // Ignore trailing empty element caused by final '\n' when comparing against SavedContent/GitHeadContent
            if (oldEnd >= 0 && newEnd >= 0 && string.Equals(oldLines[oldEnd], Lines[newEnd], StringComparison.Ordinal))
            {
                // matched tail
            }
            else if (oldEnd > 0 && string.IsNullOrEmpty(oldLines[oldEnd]) && (newEnd < 0 || !string.IsNullOrEmpty(Lines[newEnd])))
            {
                oldEnd--;
            }

            while (start <= oldEnd && start <= newEnd && string.Equals(oldLines[start], Lines[start], StringComparison.Ordinal))
                start++;

            while (oldEnd >= start && newEnd >= start && string.Equals(oldLines[oldEnd], Lines[newEnd], StringComparison.Ordinal))
            {
                oldEnd--;
                newEnd--;
            }

            int oldLen = oldEnd - start + 1;
            int newLen = newEnd - start + 1;
            if (oldLen <= 0 && newLen <= 0)
                return;

            if (oldLen <= 0)
            {
                for (int j = start; j <= newEnd; j++)
                    GutterAddedLines.Add(j);
            }
            else if (newLen <= 0)
            {
                // a pure deletion: VS Code marks the boundary (red "deleted" marker), not a modified line
                GutterDeletedAfterLine.Add(Math.Min(newCount - 1, start - 1));
            }
            else if (oldLen == 1 && newLen == 1)
            {
                GutterModifiedLines.Add(start);
            }
            else if ((long)oldLen * newLen <= 250000L)
            {
                int[,] dp = new int[oldLen + 1, newLen + 1];
                for (int i = oldLen - 1; i >= 0; i--)
                {
                    string oLine = oldLines[start + i];
                    for (int j = newLen - 1; j >= 0; j--)
                    {
                        if (string.Equals(oLine, Lines[start + j], StringComparison.Ordinal))
                            dp[i, j] = 1 + dp[i + 1, j + 1];
                        else
                            dp[i, j] = Math.Max(dp[i + 1, j], dp[i, j + 1]);
                    }
                }

                int ci = 0, cj = 0;
                int pendingRemoved = 0;
                List<int> pendingAdded = new List<int>();
                while (ci < oldLen || cj < newLen)
                {
                    if (ci < oldLen && cj < newLen && string.Equals(oldLines[start + ci], Lines[start + cj], StringComparison.Ordinal))
                    {
                        FlushGutterChunk(pendingRemoved, pendingAdded, start + cj - 1);
                        pendingRemoved = 0;
                        pendingAdded.Clear();
                        ci++;
                        cj++;
                    }
                    else if (cj < newLen && (ci == oldLen || dp[ci, cj + 1] >= dp[ci + 1, cj]))
                    {
                        pendingAdded.Add(start + cj);
                        cj++;
                    }
                    else
                    {
                        pendingRemoved++;
                        ci++;
                    }
                }
                FlushGutterChunk(pendingRemoved, pendingAdded, start + cj - 1);
            }
            else
            {
                // Large span between the first and last change (e.g. edits on line 10 and line 900): the O(n*m) table
                // would be too big, and the old fallback painted EVERY line in between as modified. Myers O(ND) finds
                // the real changes quickly when they are few, like VS Code's gutter.
                string[] a = new string[oldLen];
                Array.Copy(oldLines, start, a, 0, oldLen);
                string[] b = Lines.GetRange(start, newLen).ToArray();
                List<int[]> ops = LineDiff.ComputeEditScript(a, b);
                int pendingRemoved = 0;
                List<int> pendingAdded = new List<int>();
                int lastNew = start - 1;
                foreach (int[] op in ops)
                {
                    if (op[0] == 0)
                    {
                        FlushGutterChunk(pendingRemoved, pendingAdded, lastNew);
                        pendingRemoved = 0;
                        pendingAdded.Clear();
                        lastNew = start + op[2];
                    }
                    else if (op[0] == 1) pendingRemoved++;
                    else pendingAdded.Add(start + op[2]);
                }
                FlushGutterChunk(pendingRemoved, pendingAdded, pendingAdded.Count > 0 ? pendingAdded[0] - 1 : lastNew);
            }

            ChangedLineCount = GutterModifiedLines.Count + GutterAddedLines.Count;
        }

        private void FlushGutterChunk(int removedCount, List<int> addedIndices, int lastNewLineBefore)
        {
            if (removedCount == 0 && addedIndices.Count == 0) return;
            if (removedCount > 0 && addedIndices.Count == 0)
            {
                // removed lines inside a larger change used to vanish from the gutter entirely
                GutterDeletedAfterLine.Add(Math.Min(Lines.Count - 1, lastNewLineBefore));
                return;
            }
            if (removedCount > 0 && addedIndices.Count > 0)
            {
                int modCnt = Math.Min(removedCount, addedIndices.Count);
                for (int k = 0; k < modCnt; k++)
                    GutterModifiedLines.Add(addedIndices[k]);
                for (int k = modCnt; k < addedIndices.Count; k++)
                    GutterAddedLines.Add(addedIndices[k]);
            }
            else if (addedIndices.Count > 0)
            {
                for (int k = 0; k < addedIndices.Count; k++)
                    GutterAddedLines.Add(addedIndices[k]);
            }
        }

        public void ClampCursor()
        {
            if (Lines == null || Lines.Count == 0)
                Lines = new List<string> { "" };
            if (CursorLine < 0) CursorLine = 0;
            if (CursorLine >= Lines.Count) CursorLine = Lines.Count - 1;
            int lineLen = Lines[CursorLine].Length;
            if (CursorCol < 0) CursorCol = 0;
            if (CursorCol > lineLen) CursorCol = lineLen;
            if (FoldedLines != null && FoldedLines.Count > 0)
                FoldedLines.RemoveWhere(l => l < 0 || l >= Lines.Count);
            if (Breakpoints != null && Breakpoints.Count > 0)
                Breakpoints.RemoveWhere(l => l < 0 || l >= Lines.Count);
        }

        public bool HasSelection()
        {
            return SelectionAnchorLine >= 0 && SelectionAnchorCol >= 0 &&
                   (SelectionAnchorLine != CursorLine || SelectionAnchorCol != CursorCol);
        }

        public void ClearSelection()
        {
            SelectionAnchorLine = -1;
            SelectionAnchorCol = -1;
        }

        public void GetNormalizedSelection(out int startLine, out int startCol, out int endLine, out int endCol)
        {
            if (!HasSelection())
            {
                startLine = endLine = CursorLine;
                startCol = endCol = CursorCol;
                return;
            }
            if (SelectionAnchorLine < CursorLine || (SelectionAnchorLine == CursorLine && SelectionAnchorCol <= CursorCol))
            {
                startLine = SelectionAnchorLine;
                startCol = SelectionAnchorCol;
                endLine = CursorLine;
                endCol = CursorCol;
            }
            else
            {
                startLine = CursorLine;
                startCol = CursorCol;
                endLine = SelectionAnchorLine;
                endCol = SelectionAnchorCol;
            }
        }

        public string GetSelectedText()
        {
            if (!HasSelection()) return "";
            int sLine, sCol, eLine, eCol;
            GetNormalizedSelection(out sLine, out sCol, out eLine, out eCol);
            sLine = Math.Max(0, Math.Min(Lines.Count - 1, sLine));
            eLine = Math.Max(0, Math.Min(Lines.Count - 1, eLine));
            sCol = Math.Max(0, Math.Min(Lines[sLine].Length, sCol));
            eCol = Math.Max(0, Math.Min(Lines[eLine].Length, eCol));

            if (sLine == eLine)
                return Lines[sLine].Substring(sCol, Math.Max(0, eCol - sCol));

            StringBuilder sb = new StringBuilder();
            sb.AppendLine(Lines[sLine].Substring(sCol));
            for (int i = sLine + 1; i < eLine; i++)
                sb.AppendLine(Lines[i]);
            sb.Append(Lines[eLine].Substring(0, eCol));
            return sb.ToString();
        }

        public void PushUndoSnapshot(bool coalesceTyping = false)
        {
            long now = Environment.TickCount;
            bool canCoalesce = false;
            if (coalesceTyping && UndoStack.Count > 0 && LastTypingUndoLine == CursorLine && Math.Abs(unchecked(now - LastTypingUndoTick)) < 1200)
            {
                bool sameSpot = LastTypingUndoCol < 0 || (CursorCol >= LastTypingUndoCol && CursorCol - LastTypingUndoCol <= 2);
                bool afterWordBoundary = false;
                if (Lines != null && CursorLine >= 0 && CursorLine < Lines.Count)
                {
                    string curLn = Lines[CursorLine] ?? "";
                    if (CursorCol >= 2 && CursorCol <= curLn.Length &&
                        (curLn[CursorCol - 1] == ' ' || curLn[CursorCol - 1] == '\t') &&
                        curLn[CursorCol - 2] != ' ' && curLn[CursorCol - 2] != '\t')
                    {
                        afterWordBoundary = true;
                    }
                }
                canCoalesce = sameSpot && !afterWordBoundary;
            }
            if (canCoalesce)
            {
                LastTypingUndoTick = now;
                LastTypingUndoCol = CursorCol;
                RedoStack.Clear();
                return;
            }
            if (Lines == null || Lines.Count == 0) Lines = new List<string> { "" };
            if (UndoStack.Count == 0 || !LinesEqual(UndoStack.Peek().FullLines, Lines))
            {
                PushSnapshot(UndoStack, CaptureSnapshot());
                RedoStack.Clear();
                TrimUndoHistory();
            }
            if (coalesceTyping)
            {
                LastTypingUndoTick = now;
                LastTypingUndoLine = CursorLine;
                LastTypingUndoCol = CursorCol;
            }
            else
            {
                LastTypingUndoTick = 0;
                LastTypingUndoLine = -1;
                LastTypingUndoCol = -1;
            }
        }

        public bool Undo()
        {
            if (UndoStack.Count == 0) return false;
            LastTypingUndoTick = 0;
            LastTypingUndoLine = -1;
            List<string> before = Lines;
            BufferSnapshot redoStep = CaptureSnapshot();
            PushSnapshot(RedoStack, redoStep);
            RestoreSnapshot(PopSnapshot(UndoStack));
            ApplyUndoRedoSelection(before, redoStep, true); // EditorUndoReveal.cs
            return true;
        }

        public bool Redo()
        {
            if (RedoStack.Count == 0) return false;
            LastTypingUndoTick = 0;
            LastTypingUndoLine = -1;
            List<string> before = Lines;
            BufferSnapshot undoStep = CaptureSnapshot();
            PushSnapshot(UndoStack, undoStep);
            RestoreSnapshot(PopSnapshot(RedoStack));
            ApplyUndoRedoSelection(before, undoStep, false); // EditorUndoReveal.cs
            return true;
        }

        /// <summary>Drops the newest undo step and restores it without recording a redo step (reject a staged change).</summary>
        public bool PopUndoSnapshotAndRestore()
        {
            if (UndoStack.Count == 0) return false;
            LastTypingUndoTick = 0;
            LastTypingUndoLine = -1;
            RestoreSnapshot(PopSnapshot(UndoStack));
            return true;
        }

        /// <summary>
        /// Supplies the extra carets for undo snapshots when a view keeps them outside <see cref="ExtraCarets"/> (the
        /// Avalonia canvas). Null: <see cref="ExtraCarets"/> is used (WinForms).
        /// </summary>
        public Func<List<EditorCaret>> ExtraCaretsForUndo;

        private BufferSnapshot CaptureSnapshot()
        {
            BufferSnapshot s = new BufferSnapshot();
            s.FullLines = Lines.ToArray();
            s.CursorLine = CursorLine;
            s.CursorCol = CursorCol;
            if (HasSelection())
            {
                s.AnchorLine = SelectionAnchorLine;
                s.AnchorCol = SelectionAnchorCol;
            }
            List<EditorCaret> extras = null;
            if (ExtraCaretsForUndo != null)
            {
                try { extras = ExtraCaretsForUndo(); } catch { extras = null; }
            }
            if (extras == null) extras = ExtraCarets;
            if (extras != null && extras.Count > 0)
            {
                s.ExtraCarets = new EditorCaret[extras.Count];
                for (int i = 0; i < extras.Count; i++) s.ExtraCarets[i] = CloneCaret(extras[i]);
            }
            return s;
        }

        private static EditorCaret CloneCaret(EditorCaret c)
        {
            EditorCaret n = new EditorCaret();
            if (c == null) return n;
            n.Line = c.Line;
            n.Col = c.Col;
            n.AnchorLine = c.AnchorLine;
            n.AnchorCol = c.AnchorCol;
            return n;
        }

        /// <summary>Restores the carets and selections recorded in a snapshot (VS Code: undo / redo restore every cursor).</summary>
        private void RestoreCaretState(BufferSnapshot s)
        {
            CursorLine = s.CursorLine;
            CursorCol = s.CursorCol;
            ClampCursor();
            SelectionAnchorLine = -1;
            SelectionAnchorCol = -1;
            if (s.AnchorLine >= 0 && s.AnchorCol >= 0)
            {
                int al = Math.Min(Lines.Count - 1, s.AnchorLine);
                SelectionAnchorLine = al;
                SelectionAnchorCol = Math.Min(Lines[al].Length, s.AnchorCol);
            }
            if (ExtraCarets == null) ExtraCarets = new List<EditorCaret>();
            ExtraCarets.Clear();
            if (s.ExtraCarets == null) return;
            HashSet<long> seen = new HashSet<long>();
            seen.Add(((long)CursorLine << 32) | (uint)CursorCol);
            foreach (EditorCaret c in s.ExtraCarets)
            {
                EditorCaret n = CloneCaret(c);
                n.Line = Math.Max(0, Math.Min(Lines.Count - 1, n.Line));
                n.Col = Math.Max(0, Math.Min(Lines[n.Line].Length, n.Col));
                if (n.AnchorLine >= 0 && n.AnchorCol >= 0)
                {
                    n.AnchorLine = Math.Min(Lines.Count - 1, n.AnchorLine);
                    n.AnchorCol = Math.Min(Lines[n.AnchorLine].Length, n.AnchorCol);
                }
                else { n.AnchorLine = -1; n.AnchorCol = -1; }
                if (!seen.Add(((long)n.Line << 32) | (uint)n.Col)) continue;
                ExtraCarets.Add(n);
            }
        }

        private static void PushSnapshot(Stack<BufferSnapshot> stack, BufferSnapshot s)
        {
            if (stack.Count > 0)
            {
                BufferSnapshot top = stack.Peek();
                if (top.FullLines != null) top.CompactAgainst(s.FullLines);
            }
            stack.Push(s);
        }

        private static BufferSnapshot PopSnapshot(Stack<BufferSnapshot> stack)
        {
            BufferSnapshot s = stack.Pop();
            if (stack.Count > 0)
            {
                BufferSnapshot top = stack.Peek();
                if (top.FullLines == null) top.ExpandFrom(s.FullLines);
            }
            return s;
        }

        private void RestoreSnapshot(BufferSnapshot s)
        {
            Lines = new List<string>(s.FullLines ?? new string[] { "" });
            if (Lines.Count == 0) Lines.Add("");
            MarkLinesChanged();
            IsLargeFile = ComputeIsLargeFile(-1);
            RestoreCaretState(s);
            IsDirty = ComputeDirtyFromLines(CursorLine);
            if (!IsLargeFile) RefreshGutterDiff();
            else GutterDiffVersion++;
        }

        private static bool LinesEqual(string[] a, List<string> b)
        {
            if (a == null || b == null || a.Length != b.Count) return false;
            for (int i = 0; i < a.Length; i++)
                if (!ReferenceEquals(a[i], b[i]) && !string.Equals(a[i], b[i], StringComparison.Ordinal)) return false;
            return true;
        }
    }

    public class LlmHarnessProfile
    {
        public string Id;
        public string Name;
        public string TargetFamily;
        public string EditProtocol; // "SEARCH_REPLACE_XML", "UNIFIED_PATCH_JSON", "COT_THINK_DIFF", "FIM_INLINE"
        public bool SupportsThinkingBlock;
        public bool SupportsFimCompletion;
        public int ContextTokenBudget;
        public double RecommendedTemp;
        public string Description;
        public string SystemPromptDirective;
    }

    public class ByokVendor
    {
        public string Id;
        public string Name;
        public string Badge;
        public string BaseUrl;
        public string ApiKey = "";
        public string SelectedModel;
        public string DefaultHarnessId;
        public string[] Models;
        public string EnvVarName;

        private int _contextWindow = 0;
        public int ContextWindow
        {
            get
            {
                if (_contextWindow > 0) return _contextWindow;
                string m = SelectedModel ?? "";
                if (m.IndexOf("gemini-1.5", StringComparison.OrdinalIgnoreCase) >= 0 || m.IndexOf("gemini-2.", StringComparison.OrdinalIgnoreCase) >= 0) return 1000000;
                if (m.IndexOf("claude", StringComparison.OrdinalIgnoreCase) >= 0) return 200000;
                return 128000;
            }
            set { _contextWindow = value; }
        }

        private int _maxOutputTokens = 0;
        public int MaxOutputTokens
        {
            get
            {
                if (_maxOutputTokens > 0) return _maxOutputTokens;
                // Use a safe default for output tokens
                return 4096;
            }
            set { _maxOutputTokens = value; }
        }

        public string VendorName
        {
            get { return Name; }
            set { Name = value; }
        }
        public string RuntimeApiKey
        {
            get { return ApiKey; }
            set { ApiKey = value; }
        }
        public string EndpointUrl
        {
            get { return BaseUrl; }
            set { BaseUrl = value; }
        }
        public string ActiveModel
        {
            get { return SelectedModel; }
            set { SelectedModel = value; }
        }
        public string[] SupportedModels
        {
            get { return Models; }
            set { Models = value; }
        }
    }

    public class Bm25CodeChunk
    {
        public string FilePath;
        public string RelativePath;
        public int StartLine;
        public int EndLine;
        public string SymbolName;
        public double Score;
        public Dictionary<string, int> TermFreq = new Dictionary<string, int>();
        public int TokenLength;

        // Zero-RAM On-Demand Disk Reader: never keeps source code strings duplicated in RAM
        public string Snippet
        {
            get
            {
                try
                {
                    if (string.IsNullOrEmpty(FilePath) || !File.Exists(FilePath)) return "";
                    int startIdx = Math.Max(0, StartLine - 1);
                    int count = Math.Max(1, EndLine - StartLine + 1);
                    return string.Join("\n", File.ReadLines(FilePath).Skip(startIdx).Take(count));
                }
                catch { return ""; }
            }
            set { /* Intentionally discarded to enforce Zero-Copy Disk Indexing */ }
        }
    }

    public class CodeSnippetItem
    {
        public string Prefix;
        public string Body;
        public string Description;
        public string Language;
    }

    public class VsixExtensionPackage
    {
        public string Id;
        public string Publisher;
        public string Name;
        public string DisplayName;
        public string Version;
        public string Description;
        public string InstallPath;
        public string MainEntryScript;
        public bool IsInstalled;
        public bool IsActivated;
        public string DownloadUrl;
        public List<StudioTheme> ContributedThemes = new List<StudioTheme>();
        public List<CodeSnippetItem> ContributedSnippets = new List<CodeSnippetItem>();
        public List<string> ContributedCommands = new List<string>();
    }

    public class GitFileStatus
    {
        public string FilePath;
        public string RelativePath;
        public string StatusCode; // "M", "A", "??", "D"
        /// <summary>Porcelain X (index) and Y (work tree) status letters; ' ' when unchanged.</summary>
        public char IndexStatus = ' ';
        public char WorkTreeStatus = ' ';
        /// <summary>Top-level directory of the repository owning this entry (outer or nested repo).</summary>
        public string RepoRoot;
        /// <summary>Path relative to RepoRoot with '/' separators; pass this to git commands run in RepoRoot.</summary>
        public string RepoRelativePath;
        public bool IsStaged;
        public int AddedLines;
        public int RemovedLines;
        public int ChangedLines
        {
            get { return Math.Max(AddedLines, RemovedLines); }
        }
    }

    public class AgentToolCallResult
    {
        public string ToolName;
        public string Summary;
        public string Output;
        public bool Success;
        public string ModifiedFilePath;
    }

    public class HarnessTraceEntry
    {
        public string Timestamp;
        public string Mode;
        public string VendorAndModel;
        public string HarnessProfileId;
        public int PromptTokens;
        public int CompletionTokens;
        public long ElapsedMs;
        public string SystemPromptPreview;
        public string RawResponsePreview;
        public List<string> ToolSummaries = new List<string>();
    }

    public class AgentExecutionTurnResult
    {
        public string AssistantReply = "";
        public string ThinkingProcess = "";
        public string ProviderUsed = "";
        public string HarnessUsed = "";
        public long ElapsedMs;
        public int EstimatedInputTokens;
        public int Iterations;
        public List<AgentToolCallResult> ToolCalls = new List<AgentToolCallResult>();
        public Dictionary<string, string> StagedFileContents = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        public HarnessTraceEntry Trace;
    }

    public class SavedVendorEntry
    {
        public string Id { get; set; }
        public string ApiKey { get; set; }
        public string BaseUrl { get; set; }
        public string SelectedModel { get; set; }
    }

    public partial class IdePersistentSettings
    {
        public string ActiveThemeId { get; set; }
        public string ActiveVendorId { get; set; }
        public string ActiveHarnessId { get; set; }
        public string LastWorkspacePath { get; set; }
        public string EditorFontFamily { get; set; }
        public float EditorFontSize { get; set; }
        public int TabSize { get; set; }
        public bool InsertSpaces { get; set; }
        public bool WordWrap { get; set; }
        public bool AutoSave { get; set; }
        public bool RequireAgentToolApproval { get; set; }
        public bool NativeToolCalling { get; set; }
        public string AgentPermissionEdit { get; set; }
        public string AgentPermissionWebfetch { get; set; }
        public Dictionary<string, string> AgentPermissionBash { get; set; }

        /// <summary>Check GitHub Releases for a newer codeLess at most once a day (anonymous request, can be disabled).</summary>
        public bool AutoCheckForUpdates { get; set; }
        public bool ThemeDefaultMigrated { get; set; }
        public string LastUpdateCheckUtc { get; set; }
        public string SkippedUpdateVersion { get; set; }
        public bool EnableInlineGhostText { get; set; }
        public bool ShowMinimap { get; set; }
        public bool ShowBreadcrumbs { get; set; }
        public bool ShowRenderWhitespace { get; set; }
        public string EditorCursorStyle { get; set; }
        public bool ShowLineNumbers { get; set; }
        public bool ShowWelcomeOnStartup { get; set; }
        /// <summary>Activate the workspace's Python venv (.venv/venv/env/.env) in new terminals, like VS Code.</summary>
        public bool ActivatePythonEnvironmentInTerminal { get; set; }
        public string ExcludedFolders { get; set; }
        public string DefaultAgentModel { get; set; }
        public string DefaultAskModel { get; set; }
        public string DefaultEditModel { get; set; }
        public double AiTemperature { get; set; }
        public bool PrivacyMode { get; set; }
        public List<string> RecentWorkspaces { get; set; }
        public List<string> RecentFiles { get; set; }
        public List<string> OpenTabPaths { get; set; }
        public string WindowRestoreWindows { get; set; }
        public List<string> RestoreWindowPaths { get; set; }
        public List<RestoreWindowInfo> RestoreWindows { get; set; }
        public List<SavedVendorEntry> Vendors { get; set; }
        private bool _recentCleared;

        public static event Action<IdePersistentSettings> OnSettingsSavedInProcess;

        public const string DefaultExcludedFoldersList = ".git, node_modules, bin, obj, dist, build, out, .venv, __pycache__, .next, target, packages";

        public IdePersistentSettings()
        {
            WindowRestoreWindows = "all";
            RestoreWindowPaths = new List<string>();
            RestoreWindows = new List<RestoreWindowInfo>();
            ActiveThemeId = StudioTheme.DefaultThemeId;
            // NOTE: ThemeDefaultMigrated is intentionally left false here. The JSON deserializer runs this constructor
            // and only overwrites fields present in the file, so defaulting it to true would skip the migration for
            // every existing settings file. Fresh installs already start on the new default.
            ActiveVendorId = "anthropic";
            ActiveHarnessId = "auto";
            LastWorkspacePath = "";
            EditorFontFamily = "Consolas";
            EditorFontSize = 10.5f;
            TabSize = 4;
            InsertSpaces = true;
            WordWrap = false;
            AutoSave = false;
            HotExit = HotExitOnExit;
            FilesEol = "auto";
            AutoGuessEncoding = false;
            RequireAgentToolApproval = true;
            NativeToolCalling = true;
            AgentPermissionEdit = "allow";
            AgentPermissionWebfetch = "ask";
            AgentPermissionBash = new Dictionary<string, string>();

            AutoCheckForUpdates = true;
            EnableInlineGhostText = true;
            ShowMinimap = true;
            ShowBreadcrumbs = true;
            ShowWelcomeOnStartup = true;
            ActivatePythonEnvironmentInTerminal = true;
            ExcludedFolders = DefaultExcludedFoldersList;
            DefaultAgentModel = "claude-sonnet-4-6";
            DefaultAskModel = "gpt-4o";
            DefaultEditModel = "claude-sonnet-4-6";
            AiTemperature = 0.2;
            PrivacyMode = true;
            RecentWorkspaces = new List<string>();
            RecentFiles = new List<string>();
            OpenTabPaths = new List<string>();
            Vendors = new List<SavedVendorEntry>();
        }

        public static string GetSettingsFilePath()
        {
            string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            string dir = Path.Combine(appData, "codeLess");
            // Isolated profile for benchmarks / automated runs (macOS ignores $HOME for Application Support).
            string overrideDir = Environment.GetEnvironmentVariable("CODELESS_SETTINGS_DIR");
            if (!string.IsNullOrEmpty(overrideDir)) dir = overrideDir;
            if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
            return Path.Combine(dir, "settings.json");
        }

        public static IdePersistentSettings Load()
        {
            try
            {
                string path = GetSettingsFilePath();
                if (File.Exists(path))
                {
                    string json = File.ReadAllText(path);
                    JavaScriptSerializer ser = new JavaScriptSerializer();
                    IdePersistentSettings loaded = ser.Deserialize<IdePersistentSettings>(json);
                    if (loaded != null)
                    {
                        if (loaded.RecentWorkspaces == null) loaded.RecentWorkspaces = new List<string>();
                        if (loaded.RecentFiles == null) loaded.RecentFiles = new List<string>();
                        loaded.RecentWorkspaces.RemoveAll(IsInstallerProgramDirectory);
                        if (IsInstallerProgramDirectory(loaded.LastWorkspacePath))
                            loaded.LastWorkspacePath = loaded.RecentWorkspaces.FirstOrDefault() ?? "";
                        if (loaded.OpenTabPaths == null) loaded.OpenTabPaths = new List<string>();
                        if (string.IsNullOrEmpty(loaded.WindowRestoreWindows)) loaded.WindowRestoreWindows = "all";
                        if (loaded.RestoreWindowPaths == null) loaded.RestoreWindowPaths = new List<string>();
                        if (loaded.RestoreWindows == null) loaded.RestoreWindows = new List<RestoreWindowInfo>();
                        loaded.Vendors = loaded.Vendors ?? new List<SavedVendorEntry>();
                        if (string.IsNullOrWhiteSpace(loaded.EditorFontFamily)) loaded.EditorFontFamily = "Consolas";
                        if (loaded.EditorFontSize < 7f || loaded.EditorFontSize > 28f) loaded.EditorFontSize = 10.5f;
                        if (loaded.TabSize < 1 || loaded.TabSize > 8) loaded.TabSize = 4;
                        if (string.IsNullOrEmpty(loaded.ActiveThemeId)) loaded.ActiveThemeId = StudioTheme.DefaultThemeId;
                        // One-time move from the old default (Obsidian) to the Savatarr Graphite default. Users who
                        // pick another theme afterwards keep it.
                        if (!loaded.ThemeDefaultMigrated)
                        {
                            if (loaded.ActiveThemeId == "codeless-obsidian") loaded.ActiveThemeId = StudioTheme.DefaultThemeId;
                            loaded.ThemeDefaultMigrated = true;
                        }
                        if (string.IsNullOrEmpty(loaded.DefaultAgentModel)) loaded.DefaultAgentModel = "claude-sonnet-4-6";
                        if (string.IsNullOrEmpty(loaded.DefaultAskModel)) loaded.DefaultAskModel = "gpt-4o";
                        if (string.IsNullOrEmpty(loaded.DefaultEditModel)) loaded.DefaultEditModel = "claude-sonnet-4-6";
                        if (loaded.AiTemperature < 0.0 || loaded.AiTemperature > 2.0) loaded.AiTemperature = 0.2;
                        if (string.IsNullOrWhiteSpace(loaded.ExcludedFolders)) loaded.ExcludedFolders = DefaultExcludedFoldersList;
                        if (!json.Contains("\"ShowWelcomeOnStartup\"")) loaded.ShowWelcomeOnStartup = true;
                        if (!json.Contains("\"ShowBreadcrumbs\"")) loaded.ShowBreadcrumbs = true;
                        if (!json.Contains("\"ActivatePythonEnvironmentInTerminal\"")) loaded.ActivatePythonEnvironmentInTerminal = true;
                        if (!json.Contains("\"NativeToolCalling\"") && !json.Contains("\"codeless.agent.nativeToolCalling\"")) loaded.NativeToolCalling = true;
                        try
                        {
                            var rawDict = ser.DeserializeObject(json) as Dictionary<string, object>;
                            if (rawDict != null && rawDict.ContainsKey("codeless.agent.nativeToolCalling") && rawDict["codeless.agent.nativeToolCalling"] is bool)
                            {
                                loaded.NativeToolCalling = (bool)rawDict["codeless.agent.nativeToolCalling"];
                            }
                        }
                        catch { }
                        return loaded;
                    }
                }
            }
            catch
            {
                // Unreadable / corrupt settings: keep a copy before falling back to defaults. Previously the next
                // Save() silently overwrote the file and permanently wiped API keys and recent workspaces.
                try
                {
                    string path = GetSettingsFilePath();
                    if (File.Exists(path))
                        File.Copy(path, path + ".corrupt-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".bak", true);
                }
                catch { }
            }
            return new IdePersistentSettings();
        }

        public static bool IsInstallerProgramDirectory(string folderPath)
        {
            if (string.IsNullOrWhiteSpace(folderPath)) return false;
            try
            {
                string norm = folderPath.TrimEnd('\\', '/');
                string localProg = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "codeLess").TrimEnd('\\', '/');
                if (string.Equals(norm, localProg, StringComparison.OrdinalIgnoreCase)) return true;
                if (File.Exists(Path.Combine(norm, "unins000.dat")) || File.Exists(Path.Combine(norm, "unins000.exe"))) return true;
            }
            catch { }
            return false;
        }

        public void Save()
        {
            SaveCore(true);
        }

        internal void SaveCore(bool notifyInProcess)
        {
            try
            {
                // Find / Replace / Search histories live in FindHistoryStore: a long-lived settings object must not write
                // back the lists it loaded at startup over newer history.
                FindHistoryStore.CopyInto(this);
                string path = GetSettingsFilePath();
                IdeSettingsWatcher.NotifyInProcessWrite();
                CrossProcessFile.ReadModifyWrite(path, delegate(string targetPath)
                {
                    if (!_recentCleared && File.Exists(targetPath))
                    {
                        try
                        {
                            string existingJson = File.ReadAllText(targetPath);
                            JavaScriptSerializer diskSer = new JavaScriptSerializer();
                            IdePersistentSettings onDisk = diskSer.Deserialize<IdePersistentSettings>(existingJson);
                            if (onDisk != null)
                            {
                                if (onDisk.RecentWorkspaces != null)
                                {
                                    if (RecentWorkspaces == null) RecentWorkspaces = new List<string>();
                                    for (int i = 0; i < onDisk.RecentWorkspaces.Count; i++)
                                    {
                                        string rw = onDisk.RecentWorkspaces[i];
                                        if (!string.IsNullOrWhiteSpace(rw) && !IsInstallerProgramDirectory(rw) &&
                                            !RecentWorkspaces.Any(p => string.Equals(p, rw, StringComparison.OrdinalIgnoreCase)))
                                        {
                                            RecentWorkspaces.Add(rw);
                                        }
                                    }
                                    if (RecentWorkspaces.Count > 12)
                                        RecentWorkspaces = RecentWorkspaces.Take(12).ToList();
                                }
                                if (onDisk.RecentFiles != null)
                                {
                                    if (RecentFiles == null) RecentFiles = new List<string>();
                                    for (int i = 0; i < onDisk.RecentFiles.Count; i++)
                                    {
                                        string rf = onDisk.RecentFiles[i];
                                        if (!string.IsNullOrWhiteSpace(rf) &&
                                            !RecentFiles.Any(p => string.Equals(p, rf, StringComparison.OrdinalIgnoreCase)))
                                        {
                                            RecentFiles.Add(rf);
                                        }
                                    }
                                    if (RecentFiles.Count > 15)
                                        RecentFiles = RecentFiles.Take(15).ToList();
                                }
                            }
                        }
                        catch { }
                    }
                    _recentCleared = false;
                    JavaScriptSerializer ser = new JavaScriptSerializer();
                    string json = ser.Serialize(this);
                    CrossProcessFile.AtomicWrite(targetPath, json, true);
                });
                IdeSettingsWatcher.NotifyInProcessWrite();
                if (notifyInProcess)
                {
                    Action<IdePersistentSettings> savedHandler = OnSettingsSavedInProcess;
                    if (savedHandler != null)
                    {
                        try { savedHandler(this); } catch { }
                    }
                }
            }
            catch { }
        }

        [System.Runtime.InteropServices.DllImport("libc", EntryPoint = "chmod", SetLastError = true)]
        private static extern int chmod_native(byte[] path, int mode);

        /// <summary>settings.json holds API keys: make it owner read/write only (0600) on macOS / Linux.</summary>
        private static void RestrictToOwner(string path)
        {
            try
            {
                if (Environment.OSVersion.Platform != PlatformID.Unix && Environment.OSVersion.Platform != PlatformID.MacOSX) return;
                chmod_native(Encoding.UTF8.GetBytes(path + "\0"), 384); // 0600
            }
            catch { }
        }

        public void AddRecentWorkspace(string folderPath)
        {
            if (string.IsNullOrWhiteSpace(folderPath) || !Directory.Exists(folderPath)) return;
            if (IsInstallerProgramDirectory(folderPath)) return;
            LastWorkspacePath = folderPath;
            if (RecentWorkspaces == null) RecentWorkspaces = new List<string>();
            RecentWorkspaces.RemoveAll(p => string.Equals(p, folderPath, StringComparison.OrdinalIgnoreCase) || IsInstallerProgramDirectory(p));
            RecentWorkspaces.Insert(0, folderPath);
            if (RecentWorkspaces.Count > 12)
                RecentWorkspaces = RecentWorkspaces.Take(12).ToList();
            SaveCore(false);
        }

        public void AddRecentFile(string filePath)
        {
            if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath)) return;
            if (RecentFiles == null) RecentFiles = new List<string>();
            RecentFiles.RemoveAll(p => string.Equals(p, filePath, StringComparison.OrdinalIgnoreCase));
            RecentFiles.Insert(0, filePath);
            if (RecentFiles.Count > 15)
                RecentFiles = RecentFiles.Take(15).ToList();
            SaveCore(false);
        }

        public void ClearRecentlyOpened()
        {
            _recentCleared = true;
            if (RecentWorkspaces != null) RecentWorkspaces.Clear();
            if (RecentFiles != null) RecentFiles.Clear();
            Save();
        }

        public string[] GetExcludedFoldersList()
        {
            string raw = string.IsNullOrWhiteSpace(ExcludedFolders) ? DefaultExcludedFoldersList : ExcludedFolders;
            return raw.Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(s => s.Trim())
                .Where(s => !string.IsNullOrEmpty(s))
                .ToArray();
        }
    }
}
