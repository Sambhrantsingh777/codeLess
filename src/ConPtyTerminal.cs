using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
#if CODELESS_WINFORMS_UI
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Windows.Forms;
#endif
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using Microsoft.Win32.SafeHandles;

namespace codeLess.Studio
{
#if !CODELESS_WINFORMS_UI
    // =========================================================================
    // 2. WIN32 CONPTY PSEUDOCONSOLE NATIVE INTEROP
    // =========================================================================
    public static class ConPtyNative
    {
        public const uint EXTENDED_STARTUPINFO_PRESENT = 0x00080000;
        public const uint CREATE_UNICODE_ENVIRONMENT = 0x00000400;
        public static readonly IntPtr PROC_THREAD_ATTRIBUTE_PSEUDOCONSOLE = new IntPtr(0x00020016);
        public const uint HANDLE_FLAG_INHERIT = 0x00000001;
        public const uint INFINITE = 0xFFFFFFFF;

        [StructLayout(LayoutKind.Sequential)]
        public struct COORD
        {
            public short X;
            public short Y;
            public COORD(short x, short y) { X = x; Y = y; }
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct SECURITY_ATTRIBUTES
        {
            public int nLength;
            public IntPtr lpSecurityDescriptor;
            public int bInheritHandle;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        public struct STARTUPINFO
        {
            public int cb;
            public string lpReserved;
            public string lpDesktop;
            public string lpTitle;
            public int dwX;
            public int dwY;
            public int dwXSize;
            public int dwYSize;
            public int dwXCountChars;
            public int dwYCountChars;
            public int dwFillAttribute;
            public int dwFlags;
            public short wShowWindow;
            public short cbReserved2;
            public IntPtr lpReserved2;
            public IntPtr hStdInput;
            public IntPtr hStdOutput;
            public IntPtr hStdError;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        public struct STARTUPINFOEX
        {
            public STARTUPINFO StartupInfo;
            public IntPtr lpAttributeList;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct PROCESS_INFORMATION
        {
            public IntPtr hProcess;
            public IntPtr hThread;
            public int dwProcessId;
            public int dwThreadId;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern int CreatePseudoConsole(COORD size, IntPtr hInput, IntPtr hOutput, uint dwFlags, out IntPtr phPC);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern int ResizePseudoConsole(IntPtr hPC, COORD size);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern void ClosePseudoConsole(IntPtr hPC);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool CreatePipe(out IntPtr hReadPipe, out IntPtr hWritePipe, ref SECURITY_ATTRIBUTES lpPipeAttributes, uint nSize);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool SetHandleInformation(IntPtr hObject, uint dwMask, uint dwFlags);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool InitializeProcThreadAttributeList(IntPtr lpAttributeList, int dwAttributeCount, int dwFlags, ref IntPtr lpSize);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool UpdateProcThreadAttribute(
            IntPtr lpAttributeList, uint dwFlags, IntPtr Attribute, IntPtr lpValue, IntPtr cbSize, IntPtr lpPreviousValue, IntPtr lpReturnSize);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern void DeleteProcThreadAttributeList(IntPtr lpAttributeList);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern bool CreateProcessW(
            string lpApplicationName,
            StringBuilder lpCommandLine,
            IntPtr lpProcessAttributes,
            IntPtr lpThreadAttributes,
            bool bInheritHandles,
            uint dwCreationFlags,
            IntPtr lpEnvironment,
            string lpCurrentDirectory,
            ref STARTUPINFOEX lpStartupInfo,
            out PROCESS_INFORMATION lpProcessInformation);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool CloseHandle(IntPtr hObject);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool GetExitCodeProcess(IntPtr hProcess, out uint lpExitCode);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern uint WaitForSingleObject(IntPtr hHandle, uint dwMilliseconds);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool TerminateProcess(IntPtr hProcess, uint uExitCode);

        private const uint TH32CS_SNAPPROCESS = 0x00000002;

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
        private struct PROCESSENTRY32
        {
            public uint dwSize;
            public uint cntUsage;
            public uint th32ProcessID;
            public IntPtr th32DefaultHeapID;
            public uint th32ModuleID;
            public uint cntThreads;
            public uint th32ParentProcessID;
            public int pcPriClassBase;
            public uint dwFlags;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
            public string szExeFile;
        }

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Auto)]
        private static extern IntPtr CreateToolhelp32Snapshot(uint dwFlags, uint th32ProcessID);

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Auto)]
        private static extern bool Process32First(IntPtr hSnapshot, ref PROCESSENTRY32 lppe);

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Auto)]
        private static extern bool Process32Next(IntPtr hSnapshot, ref PROCESSENTRY32 lppe);

        public static void KillProcessTreeById(int rootPid)
        {
            if (rootPid <= 0) return;
            try
            {
                int selfPid = Process.GetCurrentProcess().Id;
                if (rootPid == selfPid) return;

                Dictionary<int, List<int>> childrenByParent = new Dictionary<int, List<int>>();
                IntPtr snap = CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0);
                if (snap != IntPtr.Zero && snap != new IntPtr(-1))
                {
                    try
                    {
                        PROCESSENTRY32 pe = new PROCESSENTRY32();
                        pe.dwSize = (uint)Marshal.SizeOf(typeof(PROCESSENTRY32));
                        if (Process32First(snap, ref pe))
                        {
                            do
                            {
                                int pid = (int)pe.th32ProcessID;
                                int ppid = (int)pe.th32ParentProcessID;
                                if (pid > 0 && pid != selfPid)
                                {
                                    List<int> list;
                                    if (!childrenByParent.TryGetValue(ppid, out list))
                                    {
                                        list = new List<int>();
                                        childrenByParent[ppid] = list;
                                    }
                                    list.Add(pid);
                                }
                            } while (Process32Next(snap, ref pe));
                        }
                    }
                    finally
                    {
                        CloseHandle(snap);
                    }
                }

                List<int> toKill = new List<int>();
                Queue<int> q = new Queue<int>();
                HashSet<int> visited = new HashSet<int>();
                q.Enqueue(rootPid);
                visited.Add(rootPid);
                while (q.Count > 0)
                {
                    int cur = q.Dequeue();
                    toKill.Add(cur);
                    List<int> kids;
                    if (childrenByParent.TryGetValue(cur, out kids))
                    {
                        for (int i = 0; i < kids.Count; i++)
                        {
                            if (visited.Add(kids[i]))
                                q.Enqueue(kids[i]);
                        }
                    }
                }

                // Kill deepest descendants first, then root
                for (int i = toKill.Count - 1; i >= 0; i--)
                {
                    int pid = toKill[i];
                    if (pid <= 0 || pid == selfPid) continue;
                    try
                    {
                        using (Process p = Process.GetProcessById(pid))
                        {
                            if (!p.HasExited)
                            {
                                p.Kill();
                                p.WaitForExit(150);
                            }
                        }
                    }
                    catch { }
                }
            }
            catch { }
        }

        public static bool IsConPtySupported()
        {
            try
            {
                if (!IsWindowsPlatform()) return false;
                Version v = Environment.OSVersion.Version;
                return v.Major > 10 || (v.Major == 10 && v.Build >= 17763);
            }
            catch { return true; }
        }

        public static bool IsWindowsPlatform()
        {
#if NETFRAMEWORK
            return Environment.OSVersion.Platform == PlatformID.Win32NT;
#else
            return RuntimeInformation.IsOSPlatform(OSPlatform.Windows);
#endif
        }

        public static bool IsMacPlatform()
        {
#if NETFRAMEWORK
            return false;
#else
            return RuntimeInformation.IsOSPlatform(OSPlatform.OSX);
#endif
        }
    }

    // =========================================================================
    // 2B. UNIX / MACOS PTY NATIVE INTEROP (forkpty / posix_openpt on libc)
    // =========================================================================
    public static class UnixPtyNative
    {
        public const int O_RDWR = 0x0002;
        public const int O_NOCTTY_MAC = 0x20000;
        public const int O_NOCTTY_LINUX = 0x0100;

        public const ulong TIOCSWINSZ_MAC = 0x80087467UL;
        public const ulong TIOCSWINSZ_LINUX = 0x5414UL;
        public const ulong TIOCSCTTY_MAC = 0x20007461UL;
        public const ulong TIOCSCTTY_LINUX = 0x540EUL;

        public const int SIGHUP = 1;
        public const int SIGINT = 2;
        public const int SIGKILL = 9;
        public const int SIGTERM = 15;

        [StructLayout(LayoutKind.Sequential)]
        public struct winsize
        {
            public ushort ws_row;
            public ushort ws_col;
            public ushort ws_xpixel;
            public ushort ws_ypixel;

            public winsize(ushort rows, ushort cols)
            {
                ws_row = rows;
                ws_col = cols;
                ws_xpixel = 0;
                ws_ypixel = 0;
            }
        }

        [DllImport("libc", SetLastError = true)]
        public static extern int forkpty(out int amaster, IntPtr name, IntPtr termp, ref winsize winp);

        [DllImport("libutil", EntryPoint = "forkpty", SetLastError = true)]
        public static extern int forkpty_libutil(out int amaster, IntPtr name, IntPtr termp, ref winsize winp);

        [DllImport("libc", SetLastError = true)]
        public static extern int fork();

        [DllImport("libc", SetLastError = true)]
        public static extern int posix_openpt(int flags);

        [DllImport("libc", SetLastError = true)]
        public static extern int grantpt(int fd);

        [DllImport("libc", SetLastError = true)]
        public static extern int unlockpt(int fd);

        [DllImport("libc", SetLastError = true)]
        public static extern IntPtr ptsname(int fd);

        [DllImport("libc", SetLastError = true, CharSet = CharSet.Ansi)]
        public static extern int open(string pathname, int flags);

        [DllImport("libc", EntryPoint = "open", SetLastError = true)]
        public static extern int open_ptr(IntPtr pathname, int flags);

        // Variadic ioctl(int fd, unsigned long request, ...) ABI-safe declaration:
        // On x86_64 and Linux arm64, the 3rd arg is read from register rdx / x2 (r2).
        // On Apple Silicon (osx-arm64), variadic arguments (...) are placed on the stack at [sp+0] (stackArg0, the 9th parameter after x0..x7).
        [DllImport("libc", EntryPoint = "ioctl", SetLastError = true)]
        private static extern int ioctl_vararg(
            int fd, ulong request,
            IntPtr r2, IntPtr r3, IntPtr r4, IntPtr r5, IntPtr r6, IntPtr r7,
            IntPtr stackArg0);

        public static int ioctl(int fd, ulong request, ref winsize ws)
        {
            IntPtr ptr = Marshal.AllocHGlobal(Marshal.SizeOf(typeof(winsize)));
            try
            {
                Marshal.StructureToPtr(ws, ptr, false);
                return ioctl_vararg(fd, request, ptr, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, ptr);
            }
            finally
            {
                Marshal.FreeHGlobal(ptr);
            }
        }

        public static int ioctl(int fd, ulong request, int arg)
        {
            IntPtr val = new IntPtr(arg);
            return ioctl_vararg(fd, request, val, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, val);
        }

        [DllImport("libc", SetLastError = true)]
        public static extern int setsid();

        [DllImport("libc", SetLastError = true)]
        public static extern int dup2(int oldfd, int newfd);

        [DllImport("libc", SetLastError = true)]
        public static extern int chdir(IntPtr path);

        [DllImport("libc", SetLastError = true, CharSet = CharSet.Ansi)]
        public static extern int setenv(string name, string value, int overwrite);

        [DllImport("libc", SetLastError = true)]
        public static extern int execvp(IntPtr file, IntPtr argv);

        [DllImport("libc", SetLastError = true)]
        public static extern void _exit(int status);

        [DllImport("libc", SetLastError = true)]
        public static extern IntPtr read(int fd, byte[] buf, IntPtr count);

        [DllImport("libc", SetLastError = true)]
        public static extern IntPtr write(int fd, byte[] buf, IntPtr count);

        [DllImport("libc", SetLastError = true)]
        public static extern int close(int fd);

        [DllImport("libc", SetLastError = true)]
        public static extern int kill(int pid, int sig);

        [DllImport("libc", SetLastError = true)]
        public static extern int waitpid(int pid, out int status, int options);

        public static ulong GetTiocswinszRequest()
        {
            return ConPtyNative.IsMacPlatform() ? TIOCSWINSZ_MAC : TIOCSWINSZ_LINUX;
        }

        public static ulong GetTiocscttyRequest()
        {
            return ConPtyNative.IsMacPlatform() ? TIOCSCTTY_MAC : TIOCSCTTY_LINUX;
        }

        public static int GetNoCttyFlag()
        {
            return ConPtyNative.IsMacPlatform() ? O_NOCTTY_MAC : O_NOCTTY_LINUX;
        }

        public static IntPtr AllocUtf8HGlobal(string text)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(text ?? "");
            IntPtr ptr = Marshal.AllocHGlobal(bytes.Length + 1);
            Marshal.Copy(bytes, 0, ptr, bytes.Length);
            Marshal.WriteByte(ptr, bytes.Length, 0);
            return ptr;
        }

        /// <summary>
        /// Opens a POSIX pseudo-terminal master/slave pair via posix_openpt, grantpt, unlockpt, ptsname.
        /// </summary>
        public static bool OpenPosixPtyMaster(short cols, short rows, out int masterFd, out string slaveName)
        {
            masterFd = -1;
            slaveName = null;
            try
            {
                int fd = posix_openpt(O_RDWR | GetNoCttyFlag());
                if (fd < 0) return false;
                if (grantpt(fd) != 0 || unlockpt(fd) != 0)
                {
                    close(fd);
                    return false;
                }
                IntPtr ptr = ptsname(fd);
                if (ptr == IntPtr.Zero)
                {
                    close(fd);
                    return false;
                }
                slaveName = Marshal.PtrToStringAnsi(ptr);
                winsize ws = new winsize((ushort)Math.Max((short)1, rows), (ushort)Math.Max((short)1, cols));
                ioctl(fd, GetTiocswinszRequest(), ref ws);
                masterFd = fd;
                return true;
            }
            catch
            {
                if (masterFd >= 0) { try { close(masterFd); } catch { } masterFd = -1; }
                return false;
            }
        }
    }

    // =========================================================================
    // 3. VT100 / XTERM CELL GRID & ANSI ESCAPE SEQUENCE PARSER
    // =========================================================================
    public struct TermCell
    {
        public const byte FLAG_BOLD = 1;
        public const byte FLAG_ITALIC = 2;
        public const byte FLAG_UNDERLINE = 4;
        public const byte FLAG_INVERSE = 8;
        public const byte FLAG_DEFAULT_FG = 16;
        public const byte FLAG_DEFAULT_BG = 32;
        public const byte FLAG_DIM = 64;

        public char Char;
        public Color Fg;
        public Color Bg;
        public byte Flags;

        public static TermCell Empty(Color defaultFg, Color defaultBg)
        {
            TermCell c;
            c.Char = ' ';
            c.Fg = defaultFg;
            c.Bg = defaultBg;
            c.Flags = (byte)(FLAG_DEFAULT_FG | FLAG_DEFAULT_BG);
            return c;
        }
    }

    public class Vt100TerminalBuffer
    {
        private readonly object syncLock = new object();
        private TermCell[,] primaryGrid;
        private TermCell[,] altGrid;
        private readonly List<TermCell[]> scrollback = new List<TermCell[]>();
        public int MaxScrollbackLines = 5000;

        public int Cols { get; private set; }
        public int Rows { get; private set; }

        public int CursorRow { get; private set; }
        public int CursorCol { get; private set; }
        public bool CursorVisible { get; private set; }
        public bool IsAlternateScreen { get; private set; }
        public bool BracketedPasteMode { get; private set; }
        public bool ApplicationCursorKeys { get; private set; }
        public bool AutoWrapMode { get; private set; }
        public int MouseTrackingMode { get; private set; }
        public bool SgrMouseMode { get; private set; }
        public bool AlternateScrollMode { get; private set; }
        public string WindowTitle { get; private set; }

        public int ScrollTop { get; private set; }
        public int ScrollBottom { get; private set; }

        private int savedPrimaryRow, savedPrimaryCol;
        private int savedAltRow, savedAltCol;
        private bool pendingWrap = false;

        // Current SGR attributes
        private Color curFg;
        private Color curBg;
        private byte curFlags;

        // Default & 16-color ANSI palette (VS Code Dark+ default, updatable per theme)
        public bool IsLightTheme { get; private set; }
        public Color DefaultFg = Color.FromArgb(204, 204, 204);
        public Color DefaultBg = Color.FromArgb(24, 24, 27);
        public Color[] AnsiPalette = new Color[16];

        // Parser state machine
        private enum ParseState { Normal, Esc, Csi, Osc, OscEsc, DcsOrString, DcsEsc, Charset }
        private ParseState state = ParseState.Normal;
        private readonly StringBuilder csiBuilder = new StringBuilder(64);
        private readonly StringBuilder oscBuilder = new StringBuilder(256);
        private readonly Decoder utf8Decoder = Encoding.UTF8.GetDecoder();
        private readonly char[] charDecodeBuf = new char[4096];

        public Action<string> WriteBackCallback; // For DSR 6n cursor position report

        public Vt100TerminalBuffer(int cols, int rows, bool isLightTheme = false)
        {
            Cols = Math.Max(10, cols);
            Rows = Math.Max(4, rows);
            ApplyThemePalette(isLightTheme);
            ResetAttr();

            primaryGrid = CreateGrid(Cols, Rows);
            altGrid = CreateGrid(Cols, Rows);
            CursorRow = 0;
            CursorCol = 0;
            CursorVisible = true;
            AutoWrapMode = true;
            AlternateScrollMode = true;
            ScrollTop = 0;
            ScrollBottom = Rows - 1;
            WindowTitle = "Terminal";
        }

        public void ApplyThemePalette(bool isLight, Color? customBg = null, Color? customFg = null)
        {
            IsLightTheme = isLight;
            Color[] oldPalette = (Color[])AnsiPalette.Clone();
            Color oldDefFg = DefaultFg;
            Color oldDefBg = DefaultBg;

            if (isLight)
            {
                DefaultFg = customFg ?? Color.FromArgb(59, 59, 59);
                DefaultBg = customBg ?? Color.FromArgb(255, 255, 255);
                // VS Code Light Modern ANSI 16 palette (high-contrast readable colors on white)
                AnsiPalette[0] = Color.FromArgb(0, 0, 0);         // Black
                AnsiPalette[1] = Color.FromArgb(205, 49, 49);     // Red (#CD3131)
                AnsiPalette[2] = Color.FromArgb(16, 124, 65);     // Green (#107C41)
                AnsiPalette[3] = Color.FromArgb(148, 104, 0);     // Yellow (#946800 readable dark gold)
                AnsiPalette[4] = Color.FromArgb(4, 81, 165);      // Blue (#0451A5)
                AnsiPalette[5] = Color.FromArgb(188, 5, 188);     // Magenta (#BC05BC)
                AnsiPalette[6] = Color.FromArgb(5, 152, 188);     // Cyan (#0598BC)
                AnsiPalette[7] = Color.FromArgb(59, 59, 59);      // White (default console text -> dark charcoal)
                AnsiPalette[8] = Color.FromArgb(102, 102, 102);   // Bright Black (#666666)
                AnsiPalette[9] = Color.FromArgb(205, 49, 49);     // Bright Red (#CD3131)
                AnsiPalette[10] = Color.FromArgb(20, 140, 75);    // Bright Green (#148C4B)
                AnsiPalette[11] = Color.FromArgb(134, 92, 0);     // Bright Yellow (#865C00 readable dark amber)
                AnsiPalette[12] = Color.FromArgb(4, 81, 165);     // Bright Blue (#0451A5)
                AnsiPalette[13] = Color.FromArgb(188, 5, 188);    // Bright Magenta (#BC05BC)
                AnsiPalette[14] = Color.FromArgb(5, 152, 188);    // Bright Cyan (#0598BC)
                AnsiPalette[15] = Color.FromArgb(31, 41, 55);     // Bright White (dark slate on light bg)
            }
            else
            {
                DefaultFg = customFg ?? Color.FromArgb(204, 204, 204);
                DefaultBg = customBg ?? Color.FromArgb(24, 24, 27);
                // VS Code Dark+ ANSI 16 palette
                AnsiPalette[0] = Color.FromArgb(0, 0, 0);         // Black
                AnsiPalette[1] = Color.FromArgb(205, 49, 49);     // Red
                AnsiPalette[2] = Color.FromArgb(13, 188, 121);    // Green
                AnsiPalette[3] = Color.FromArgb(229, 229, 16);    // Yellow
                AnsiPalette[4] = Color.FromArgb(36, 114, 200);    // Blue
                AnsiPalette[5] = Color.FromArgb(188, 63, 188);    // Magenta
                AnsiPalette[6] = Color.FromArgb(17, 168, 205);    // Cyan
                AnsiPalette[7] = Color.FromArgb(229, 229, 229);   // White
                AnsiPalette[8] = Color.FromArgb(102, 102, 102);   // Bright Black
                AnsiPalette[9] = Color.FromArgb(241, 76, 76);     // Bright Red
                AnsiPalette[10] = Color.FromArgb(35, 209, 139);   // Bright Green
                AnsiPalette[11] = Color.FromArgb(245, 245, 67);   // Bright Yellow
                AnsiPalette[12] = Color.FromArgb(59, 142, 234);   // Bright Blue
                AnsiPalette[13] = Color.FromArgb(214, 112, 214);  // Bright Magenta
                AnsiPalette[14] = Color.FromArgb(41, 184, 219);   // Bright Cyan
                AnsiPalette[15] = Color.FromArgb(255, 255, 255);  // Bright White
            }

            lock (syncLock)
            {
                curFg = RemapPaletteColor(curFg, oldPalette, oldDefFg, DefaultFg, true);
                curBg = RemapPaletteColor(curBg, oldPalette, oldDefBg, DefaultBg, false);
                RemapGridColors(primaryGrid, oldPalette, oldDefFg, oldDefBg);
                RemapGridColors(altGrid, oldPalette, oldDefFg, oldDefBg);
                for (int s = 0; s < scrollback.Count; s++)
                {
                    TermCell[] row = scrollback[s];
                    if (row == null) continue;
                    for (int c = 0; c < row.Length; c++)
                    {
                        RemapCellColors(ref row[c], oldPalette, oldDefFg, oldDefBg);
                    }
                }
            }
        }

        private void RemapCellColors(ref TermCell cell, Color[] oldPalette, Color oldDefFg, Color oldDefBg)
        {
            if ((cell.Flags & TermCell.FLAG_DEFAULT_FG) != 0)
                cell.Fg = DefaultFg;
            else
                cell.Fg = RemapPaletteColor(cell.Fg, oldPalette, oldDefFg, DefaultFg, true);

            if ((cell.Flags & TermCell.FLAG_DEFAULT_BG) != 0)
                cell.Bg = DefaultBg;
            else
                cell.Bg = RemapPaletteColor(cell.Bg, oldPalette, oldDefBg, DefaultBg, false);
        }

        private Color RemapPaletteColor(Color col, Color[] oldPalette, Color oldDef, Color newDef, bool isFg)
        {
            int argb = col.ToArgb();
            if (argb == oldDef.ToArgb() && oldDef.ToArgb() != 0)
                return newDef;

            if (!isFg)
            {
                if (IsLightTheme && (argb == Color.FromArgb(24, 24, 27).ToArgb() ||
                                     argb == Color.FromArgb(7, 8, 12).ToArgb() ||
                                     argb == Color.FromArgb(13, 15, 22).ToArgb() ||
                                     argb == Color.FromArgb(14, 17, 23).ToArgb() ||
                                     argb == Color.FromArgb(0, 0, 0).ToArgb()))
                {
                    return newDef;
                }
                if (!IsLightTheme && (argb == Color.FromArgb(255, 255, 255).ToArgb() ||
                                      argb == Color.FromArgb(248, 250, 252).ToArgb() ||
                                      argb == Color.FromArgb(243, 243, 243).ToArgb()))
                {
                    return newDef;
                }
            }
            else
            {
                if (IsLightTheme && (argb == Color.FromArgb(204, 204, 204).ToArgb() ||
                                     argb == Color.FromArgb(229, 229, 229).ToArgb() ||
                                     argb == Color.FromArgb(241, 245, 249).ToArgb() ||
                                     argb == Color.FromArgb(255, 255, 255).ToArgb()))
                {
                    return newDef;
                }
                if (!IsLightTheme && (argb == Color.FromArgb(59, 59, 59).ToArgb() ||
                                      argb == Color.FromArgb(31, 41, 55).ToArgb() ||
                                      argb == Color.FromArgb(15, 23, 42).ToArgb()))
                {
                    return newDef;
                }
            }

            if (oldPalette != null)
            {
                for (int i = 0; i < oldPalette.Length && i < AnsiPalette.Length; i++)
                {
                    if (oldPalette[i].ToArgb() != 0 && argb == oldPalette[i].ToArgb())
                        return AnsiPalette[i];
                }
            }
            return col;
        }

        private void RemapGridColors(TermCell[,] grid, Color[] oldPalette, Color oldDefFg, Color oldDefBg)
        {
            if (grid == null) return;
            int rCount = grid.GetLength(0);
            int cCount = grid.GetLength(1);
            for (int r = 0; r < rCount; r++)
            {
                for (int c = 0; c < cCount; c++)
                {
                    RemapCellColors(ref grid[r, c], oldPalette, oldDefFg, oldDefBg);
                }
            }
        }

        private TermCell[,] ActiveGrid
        {
            get { return IsAlternateScreen ? altGrid : primaryGrid; }
        }

        private TermCell[,] CreateGrid(int cols, int rows)
        {
            TermCell[,] g = new TermCell[rows, cols];
            TermCell empty = TermCell.Empty(DefaultFg, DefaultBg);
            for (int r = 0; r < rows; r++)
                for (int c = 0; c < cols; c++)
                    g[r, c] = empty;
            return g;
        }

        private void ResetAttr()
        {
            curFg = DefaultFg;
            curBg = DefaultBg;
            curFlags = (byte)(TermCell.FLAG_DEFAULT_FG | TermCell.FLAG_DEFAULT_BG);
        }

        public int ScrollbackCount
        {
            get { lock (syncLock) { return scrollback.Count; } }
        }

        public void ClearScrollbackAndScreen()
        {
            lock (syncLock)
            {
                scrollback.Clear();
                EraseInDisplay(2);
                CursorRow = 0;
                CursorCol = 0;
                pendingWrap = false;
            }
        }

        private static bool IsGridRowBlank(TermCell[,] grid, int cols, int row)
        {
            for (int c = 0; c < cols; c++)
            {
                char ch = grid[row, c].Char;
                if (ch != '\0' && ch != ' ')
                    return false;
            }
            return true;
        }

        private static string ExtractGridRowTrimmed(TermCell[,] grid, int cols, int row, out int lastNonSpaceCol)
        {
            lastNonSpaceCol = -1;
            StringBuilder sb = new StringBuilder(cols);
            for (int c = 0; c < cols; c++)
            {
                char ch = grid[row, c].Char;
                if (ch == '\0') ch = ' ';
                if (ch != ' ') lastNonSpaceCol = c;
                sb.Append(ch);
            }
            if (lastNonSpaceCol < 0) return "";
            return sb.ToString(0, lastNonSpaceCol + 1);
        }

        private static bool IsUneditedShellPromptLine(string trimmedLine)
        {
            if (string.IsNullOrEmpty(trimmedLine)) return false;
            if (trimmedLine.StartsWith("PS ", StringComparison.Ordinal) && trimmedLine.EndsWith(">", StringComparison.Ordinal))
                return true;
            if (trimmedLine.Length >= 4 && char.IsLetter(trimmedLine[0]) && trimmedLine[1] == ':' && trimmedLine[2] == '\\' && trimmedLine.EndsWith(">", StringComparison.Ordinal))
                return true;
            if (trimmedLine == ">>>")
                return true;
            if ((trimmedLine.EndsWith("$", StringComparison.Ordinal) || trimmedLine.EndsWith("#", StringComparison.Ordinal)) &&
                (trimmedLine.Contains("@") || trimmedLine.Contains("~") || trimmedLine.Contains("/")))
                return true;
            return false;
        }

        private void SnapCursorToIdlePromptIfDriftedInternal()
        {
            if (IsAlternateScreen || CursorRow <= 0 || CursorCol != 0 || CursorRow >= Rows)
                return;

            // Check that CursorRow and all rows below it on screen are completely blank
            for (int r = CursorRow; r < Rows; r++)
            {
                if (!IsGridRowBlank(primaryGrid, Cols, r))
                    return;
            }

            // Look up for the nearest non-blank line above CursorRow (usually CursorRow - 1)
            int promptRow = CursorRow - 1;
            while (promptRow > 0 && IsGridRowBlank(primaryGrid, Cols, promptRow))
                promptRow--;

            int lastCol;
            string prevTrimmed = ExtractGridRowTrimmed(primaryGrid, Cols, promptRow, out lastCol);
            if (lastCol >= 0 && IsUneditedShellPromptLine(prevTrimmed))
            {
                CursorRow = promptRow;
                bool spaceAfterPrompt = prevTrimmed.StartsWith("PS ", StringComparison.Ordinal) ||
                                        prevTrimmed.EndsWith("$", StringComparison.Ordinal) ||
                                        prevTrimmed.EndsWith("#", StringComparison.Ordinal) ||
                                        prevTrimmed == ">>>";
                CursorCol = Math.Min(Cols - 1, lastCol + (spaceAfterPrompt ? 2 : 1));
                pendingWrap = false;
            }
        }

        public void Resize(int newCols, int newRows)
        {
            newCols = Math.Max(10, newCols);
            newRows = Math.Max(4, newRows);
            lock (syncLock)
            {
                if (newCols == Cols && newRows == Rows) return;

                TermCell[,] newPrimary = CreateGrid(newCols, newRows);
                TermCell[,] newAlt = CreateGrid(newCols, newRows);

                int lastOccupiedRow = CursorRow;
                for (int r = Rows - 1; r > lastOccupiedRow; r--)
                {
                    if (!IsGridRowBlank(primaryGrid, Cols, r))
                    {
                        lastOccupiedRow = r;
                        break;
                    }
                }

                int rowShift = 0;
                if (!IsAlternateScreen && lastOccupiedRow >= newRows)
                {
                    rowShift = lastOccupiedRow - newRows + 1;
                    for (int s = 0; s < rowShift && s < Rows; s++)
                    {
                        TermCell[] savedLine = new TermCell[Cols];
                        for (int c = 0; c < Cols; c++) savedLine[c] = primaryGrid[s, c];
                        scrollback.Add(savedLine);
                        if (scrollback.Count > MaxScrollbackLines)
                            scrollback.RemoveAt(0);
                    }
                }

                int copyCols = Math.Min(Cols, newCols);
                for (int r = 0; r < newRows; r++)
                {
                    int srcPrimaryRow = r + rowShift;
                    if (srcPrimaryRow >= 0 && srcPrimaryRow < Rows)
                    {
                        for (int c = 0; c < copyCols; c++)
                            newPrimary[r, c] = primaryGrid[srcPrimaryRow, c];
                    }
                    if (r < Rows)
                    {
                        for (int c = 0; c < copyCols; c++)
                            newAlt[r, c] = altGrid[r, c];
                    }
                }

                primaryGrid = newPrimary;
                altGrid = newAlt;
                Cols = newCols;
                Rows = newRows;
                ScrollTop = 0;
                ScrollBottom = Rows - 1;
                CursorRow = Math.Max(0, Math.Min(Rows - 1, CursorRow - rowShift));
                CursorCol = Math.Max(0, Math.Min(Cols - 1, CursorCol));
                pendingWrap = false;
                SnapCursorToIdlePromptIfDriftedInternal();
            }
        }

        public void ProcessBytes(byte[] bytes, int count)
        {
            if (bytes == null || count <= 0) return;
            lock (syncLock)
            {
                int offset = 0;
                while (offset < count)
                {
                    int bytesUsed;
                    int charsUsed;
                    bool completed;
                    utf8Decoder.Convert(bytes, offset, count - offset, charDecodeBuf, 0, charDecodeBuf.Length, false, out bytesUsed, out charsUsed, out completed);
                    offset += bytesUsed;
                    for (int i = 0; i < charsUsed; i++)
                        ProcessCharInternal(charDecodeBuf[i]);
                    if (bytesUsed == 0) break;
                }
                if (state == ParseState.Normal)
                    SnapCursorToIdlePromptIfDriftedInternal();
            }
        }

        public void ProcessText(string text)
        {
            if (string.IsNullOrEmpty(text)) return;
            lock (syncLock)
            {
                for (int i = 0; i < text.Length; i++)
                    ProcessCharInternal(text[i]);
                if (state == ParseState.Normal)
                    SnapCursorToIdlePromptIfDriftedInternal();
            }
        }

        private void ProcessCharInternal(char ch)
        {
            switch (state)
            {
                case ParseState.Normal:
                    if (ch == '\x1b')
                    {
                        state = ParseState.Esc;
                    }
                    else if (ch == '\r')
                    {
                        CursorCol = 0;
                        pendingWrap = false;
                    }
                    else if (ch == '\n' || ch == '\x0b' || ch == '\x0c')
                    {
                        LineFeed();
                    }
                    else if (ch == '\b')
                    {
                        if (CursorCol > 0) CursorCol--;
                        pendingWrap = false;
                    }
                    else if (ch == '\t')
                    {
                        int nextStop = ((CursorCol / 8) + 1) * 8;
                        CursorCol = Math.Min(Cols - 1, nextStop);
                        pendingWrap = false;
                    }
                    else if (ch == '\a')
                    {
                        // Bell - ignore
                    }
                    else if (ch >= ' ')
                    {
                        PutChar(ch);
                    }
                    break;

                case ParseState.Esc:
                    if (ch == '[')
                    {
                        csiBuilder.Length = 0;
                        state = ParseState.Csi;
                    }
                    else if (ch == ']')
                    {
                        oscBuilder.Length = 0;
                        state = ParseState.Osc;
                    }
                    else if (ch == 'P' || ch == '_' || ch == '^' || ch == 'X')
                    {
                        // DCS (ESC P), APC (ESC _), PM (ESC ^), SOS (ESC X) — consume until ST (ESC \) or BEL (\a)
                        state = ParseState.DcsOrString;
                    }
                    else if (ch == '(' || ch == ')' || ch == '*' || ch == '+')
                    {
                        state = ParseState.Charset;
                    }
                    else if (ch == '7')
                    {
                        SaveCursor();
                        state = ParseState.Normal;
                    }
                    else if (ch == '8')
                    {
                        RestoreCursor();
                        state = ParseState.Normal;
                    }
                    else if (ch == 'M')
                    {
                        ReverseLineFeed();
                        state = ParseState.Normal;
                    }
                    else if (ch == 'D')
                    {
                        LineFeed();
                        state = ParseState.Normal;
                    }
                    else if (ch == 'E')
                    {
                        CursorCol = 0;
                        LineFeed();
                        state = ParseState.Normal;
                    }
                    else if (ch == 'c')
                    {
                        FullReset();
                        state = ParseState.Normal;
                    }
                    else
                    {
                        state = ParseState.Normal;
                    }
                    break;

                case ParseState.DcsOrString:
                    if (ch == '\a' || ch == '\x9c')
                    {
                        state = ParseState.Normal;
                    }
                    else if (ch == '\x1b')
                    {
                        state = ParseState.DcsEsc;
                    }
                    break;

                case ParseState.DcsEsc:
                    if (ch == '\\')
                        state = ParseState.Normal;
                    else
                        state = ParseState.DcsOrString;
                    break;

                case ParseState.Charset:
                    // Swallow charset selector byte (e.g. ESC ( B or ESC ( 0)
                    state = ParseState.Normal;
                    break;

                case ParseState.Osc:
                    if (ch == '\a')
                    {
                        ExecuteOsc(oscBuilder.ToString());
                        state = ParseState.Normal;
                    }
                    else if (ch == '\x1b')
                    {
                        state = ParseState.OscEsc;
                    }
                    else
                    {
                        if (oscBuilder.Length < 512) oscBuilder.Append(ch);
                    }
                    break;

                case ParseState.OscEsc:
                    if (ch == '\\')
                        ExecuteOsc(oscBuilder.ToString());
                    state = ParseState.Normal;
                    break;

                case ParseState.Csi:
                    if (ch >= '\x40' && ch <= '\x7e')
                    {
                        ExecuteCsi(csiBuilder.ToString(), ch);
                        state = ParseState.Normal;
                    }
                    else
                    {
                        if (csiBuilder.Length < 128) csiBuilder.Append(ch);
                    }
                    break;
            }
        }

        private void PutChar(char ch)
        {
            if (pendingWrap && AutoWrapMode)
            {
                CursorCol = 0;
                LineFeed();
                pendingWrap = false;
            }

            TermCell[,] g = ActiveGrid;
            if (CursorRow >= 0 && CursorRow < Rows && CursorCol >= 0 && CursorCol < Cols)
            {
                TermCell cell;
                cell.Char = ch;
                cell.Fg = curFg;
                cell.Bg = curBg;
                cell.Flags = curFlags;
                g[CursorRow, CursorCol] = cell;
            }

            if (CursorCol + 1 < Cols)
            {
                CursorCol++;
            }
            else
            {
                pendingWrap = true;
            }
        }

        private void LineFeed()
        {
            pendingWrap = false;
            if (CursorRow == ScrollBottom)
            {
                ScrollUpRegion(ScrollTop, ScrollBottom, 1, !IsAlternateScreen && ScrollTop == 0);
            }
            else if (CursorRow + 1 < Rows)
            {
                CursorRow++;
            }
        }

        private void ReverseLineFeed()
        {
            pendingWrap = false;
            if (CursorRow == ScrollTop)
            {
                ScrollDownRegion(ScrollTop, ScrollBottom, 1);
            }
            else if (CursorRow > 0)
            {
                CursorRow--;
            }
        }

        private void ScrollUpRegion(int top, int bottom, int count, bool pushToScrollback)
        {
            top = Math.Max(0, Math.Min(Rows - 1, top));
            bottom = Math.Max(top, Math.Min(Rows - 1, bottom));
            count = Math.Max(1, Math.Min(bottom - top + 1, count));
            TermCell[,] g = ActiveGrid;

            for (int n = 0; n < count; n++)
            {
                if (pushToScrollback)
                {
                    TermCell[] savedLine = new TermCell[Cols];
                    for (int c = 0; c < Cols; c++) savedLine[c] = g[top, c];
                    scrollback.Add(savedLine);
                    if (scrollback.Count > MaxScrollbackLines)
                        scrollback.RemoveAt(0);
                }

                for (int r = top; r < bottom; r++)
                {
                    for (int c = 0; c < Cols; c++)
                        g[r, c] = g[r + 1, c];
                }

                TermCell blank = BlankAttrCell();
                for (int c = 0; c < Cols; c++)
                    g[bottom, c] = blank;
            }
        }

        private void ScrollDownRegion(int top, int bottom, int count)
        {
            top = Math.Max(0, Math.Min(Rows - 1, top));
            bottom = Math.Max(top, Math.Min(Rows - 1, bottom));
            count = Math.Max(1, Math.Min(bottom - top + 1, count));
            TermCell[,] g = ActiveGrid;

            for (int n = 0; n < count; n++)
            {
                for (int r = bottom; r > top; r--)
                {
                    for (int c = 0; c < Cols; c++)
                        g[r, c] = g[r - 1, c];
                }

                TermCell blank = BlankAttrCell();
                for (int c = 0; c < Cols; c++)
                    g[top, c] = blank;
            }
        }

        private TermCell BlankAttrCell()
        {
            TermCell cell;
            cell.Char = ' ';
            cell.Fg = DefaultFg;
            cell.Bg = ((curFlags & TermCell.FLAG_DEFAULT_BG) != 0) ? DefaultBg : curBg;
            cell.Flags = (byte)(TermCell.FLAG_DEFAULT_FG | (curFlags & TermCell.FLAG_DEFAULT_BG));
            return cell;
        }

        private void SaveCursor()
        {
            if (IsAlternateScreen) { savedAltRow = CursorRow; savedAltCol = CursorCol; }
            else { savedPrimaryRow = CursorRow; savedPrimaryCol = CursorCol; }
        }

        private void RestoreCursor()
        {
            if (IsAlternateScreen)
            {
                CursorRow = Math.Max(0, Math.Min(Rows - 1, savedAltRow));
                CursorCol = Math.Max(0, Math.Min(Cols - 1, savedAltCol));
            }
            else
            {
                CursorRow = Math.Max(0, Math.Min(Rows - 1, savedPrimaryRow));
                CursorCol = Math.Max(0, Math.Min(Cols - 1, savedPrimaryCol));
            }
            pendingWrap = false;
        }

        private void FullReset()
        {
            IsAlternateScreen = false;
            CursorVisible = true;
            BracketedPasteMode = false;
            ApplicationCursorKeys = false;
            AutoWrapMode = true;
            MouseTrackingMode = 0;
            SgrMouseMode = false;
            AlternateScrollMode = true;
            ScrollTop = 0;
            ScrollBottom = Rows - 1;
            ResetAttr();
            primaryGrid = CreateGrid(Cols, Rows);
            altGrid = CreateGrid(Cols, Rows);
            CursorRow = 0;
            CursorCol = 0;
            pendingWrap = false;
        }

        private void ExecuteOsc(string osc)
        {
            if (string.IsNullOrEmpty(osc)) return;
            int semi = osc.IndexOf(';');
            if (semi > 0)
            {
                string code = osc.Substring(0, semi);
                string arg = osc.Substring(semi + 1);
                if (code == "0" || code == "2")
                    WindowTitle = arg;
            }
        }

        private void ExecuteCsi(string rawParams, char finalChar)
        {
            char csiPrefix = '\0';
            if (!string.IsNullOrEmpty(rawParams))
            {
                char first = rawParams[0];
                if (first == '?' || first == '>' || first == '<' || first == '=')
                {
                    csiPrefix = first;
                    rawParams = rawParams.Substring(1);
                }
            }

            if (finalChar == 'm' && csiPrefix == '\0')
            {
                ExecuteSgrRaw(rawParams);
                return;
            }

            string[] parts = string.IsNullOrEmpty(rawParams)
                ? new string[0]
                : rawParams.Split(';');
            int[] nums = new int[parts.Length];
            for (int i = 0; i < parts.Length; i++)
            {
                int val;
                string pClean = parts[i];
                int colonIdx = pClean.IndexOf(':');
                if (colonIdx >= 0) pClean = pClean.Substring(0, colonIdx);
                nums[i] = int.TryParse(pClean, out val) ? val : 0;
            }

            int p0 = (nums.Length > 0 && nums[0] > 0) ? nums[0] : 1;
            int p0Zero = (nums.Length > 0) ? nums[0] : 0;

            if (csiPrefix == '?')
            {
                bool setMode = (finalChar == 'h');
                if (finalChar == 'h' || finalChar == 'l')
                {
                    for (int i = 0; i < nums.Length; i++)
                    {
                        int mode = nums[i];
                        if (mode == 25)
                        {
                            CursorVisible = setMode;
                        }
                        else if (mode == 1049 || mode == 47 || mode == 1047)
                        {
                            if (setMode && !IsAlternateScreen)
                            {
                                savedPrimaryRow = CursorRow;
                                savedPrimaryCol = CursorCol;
                                IsAlternateScreen = true;
                                altGrid = CreateGrid(Cols, Rows);
                                CursorRow = 0;
                                CursorCol = 0;
                                ScrollTop = 0;
                                ScrollBottom = Rows - 1;
                                pendingWrap = false;
                            }
                            else if (!setMode && IsAlternateScreen)
                            {
                                IsAlternateScreen = false;
                                CursorRow = Math.Max(0, Math.Min(Rows - 1, savedPrimaryRow));
                                CursorCol = Math.Max(0, Math.Min(Cols - 1, savedPrimaryCol));
                                ScrollTop = 0;
                                ScrollBottom = Rows - 1;
                                pendingWrap = false;
                            }
                        }
                        else if (mode == 2004)
                        {
                            BracketedPasteMode = setMode;
                        }
                        else if (mode == 1)
                        {
                            ApplicationCursorKeys = setMode;
                        }
                        else if (mode == 7)
                        {
                            AutoWrapMode = setMode;
                        }
                        else if (mode == 9 || mode == 1000 || mode == 1002 || mode == 1003)
                        {
                            if (setMode) MouseTrackingMode = mode;
                            else if (MouseTrackingMode == mode) MouseTrackingMode = 0;
                        }
                        else if (mode == 1006)
                        {
                            SgrMouseMode = setMode;
                        }
                        else if (mode == 1007)
                        {
                            AlternateScrollMode = setMode;
                        }
                    }
                }
                return;
            }

            // Ignore non-standard private CSI prefixes ('>', '<', '=') such as Kitty keyboard protocol (>1u / <u)
            if (csiPrefix != '\0')
                return;

            switch (finalChar)
            {
                case 'A': // CUU - Cursor Up
                    CursorRow = Math.Max(ScrollTop, CursorRow - p0);
                    pendingWrap = false;
                    break;
                case 'B': // CUD - Cursor Down
                    CursorRow = Math.Min(ScrollBottom, CursorRow + p0);
                    pendingWrap = false;
                    break;
                case 'C': // CUF - Cursor Forward
                    CursorCol = Math.Min(Cols - 1, CursorCol + p0);
                    pendingWrap = false;
                    break;
                case 'D': // CUB - Cursor Back
                    CursorCol = Math.Max(0, CursorCol - p0);
                    pendingWrap = false;
                    break;
                case 'E': // CNL - Cursor Next Line
                    CursorRow = Math.Min(Rows - 1, CursorRow + p0);
                    CursorCol = 0;
                    pendingWrap = false;
                    break;
                case 'F': // CPL - Cursor Prev Line
                    CursorRow = Math.Max(0, CursorRow - p0);
                    CursorCol = 0;
                    pendingWrap = false;
                    break;
                case 'G':
                case '`': // CHA - Cursor Horizontal Absolute (1-based)
                    CursorCol = Math.Max(0, Math.Min(Cols - 1, p0 - 1));
                    pendingWrap = false;
                    break;
                case 'd': // VPA - Vertical Line Position Absolute (1-based)
                    CursorRow = Math.Max(0, Math.Min(Rows - 1, p0 - 1));
                    pendingWrap = false;
                    break;
                case 'H':
                case 'f': // CUP - Cursor Position (1-based row;col)
                    {
                        int r = (nums.Length > 0 && nums[0] > 0) ? nums[0] - 1 : 0;
                        int c = (nums.Length > 1 && nums[1] > 0) ? nums[1] - 1 : 0;
                        CursorRow = Math.Max(0, Math.Min(Rows - 1, r));
                        CursorCol = Math.Max(0, Math.Min(Cols - 1, c));
                        pendingWrap = false;
                    }
                    break;
                case 'J': // ED - Erase in Display
                    EraseInDisplay(p0Zero);
                    break;
                case 'K': // EL - Erase in Line
                    EraseInLine(p0Zero);
                    break;
                case 'L': // IL - Insert Lines
                    if (CursorRow >= ScrollTop && CursorRow <= ScrollBottom)
                        ScrollDownRegion(CursorRow, ScrollBottom, p0);
                    break;
                case 'M': // DL - Delete Lines
                    if (CursorRow >= ScrollTop && CursorRow <= ScrollBottom)
                        ScrollUpRegion(CursorRow, ScrollBottom, p0, false);
                    break;
                case '@': // ICH - Insert Blank Characters
                    InsertBlankChars(p0);
                    break;
                case 'P': // DCH - Delete Characters
                    DeleteChars(p0);
                    break;
                case 'X': // ECH - Erase Characters
                    EraseChars(p0);
                    break;
                case 'S': // SU - Scroll Up
                    ScrollUpRegion(ScrollTop, ScrollBottom, p0, false);
                    break;
                case 'T': // SD - Scroll Down
                    ScrollDownRegion(ScrollTop, ScrollBottom, p0);
                    break;
                case 'r': // DECSTBM - Set Top and Bottom Margins
                    {
                        int top = (nums.Length > 0 && nums[0] > 0) ? nums[0] - 1 : 0;
                        int bot = (nums.Length > 1 && nums[1] > 0) ? nums[1] - 1 : Rows - 1;
                        top = Math.Max(0, Math.Min(Rows - 2, top));
                        bot = Math.Max(top + 1, Math.Min(Rows - 1, bot));
                        ScrollTop = top;
                        ScrollBottom = bot;
                        CursorRow = 0;
                        CursorCol = 0;
                        pendingWrap = false;
                    }
                    break;
                case 's':
                    SaveCursor();
                    break;
                case 'u':
                    RestoreCursor();
                    break;
                case 'm': // SGR - Select Graphic Rendition
                    ExecuteSgr(nums);
                    break;
                case 'n': // DSR - Device Status Report
                    if (p0Zero == 6 && WriteBackCallback != null)
                    {
                        WriteBackCallback(string.Format("\x1b[{0};{1}R", CursorRow + 1, CursorCol + 1));
                    }
                    break;
            }
        }

        private void EraseInDisplay(int mode)
        {
            TermCell[,] g = ActiveGrid;
            TermCell blank = BlankAttrCell();
            if (mode == 0)
            {
                for (int c = CursorCol; c < Cols; c++) g[CursorRow, c] = blank;
                for (int r = CursorRow + 1; r < Rows; r++)
                    for (int c = 0; c < Cols; c++) g[r, c] = blank;
            }
            else if (mode == 1)
            {
                for (int r = 0; r < CursorRow; r++)
                    for (int c = 0; c < Cols; c++) g[r, c] = blank;
                for (int c = 0; c <= CursorCol && c < Cols; c++) g[CursorRow, c] = blank;
            }
            else if (mode == 2 || mode == 3)
            {
                for (int r = 0; r < Rows; r++)
                    for (int c = 0; c < Cols; c++) g[r, c] = blank;
                if (mode == 3) scrollback.Clear();
            }
        }

        private void EraseInLine(int mode)
        {
            TermCell[,] g = ActiveGrid;
            TermCell blank = BlankAttrCell();
            if (CursorRow < 0 || CursorRow >= Rows) return;
            if (mode == 0)
            {
                for (int c = CursorCol; c < Cols; c++) g[CursorRow, c] = blank;
            }
            else if (mode == 1)
            {
                for (int c = 0; c <= CursorCol && c < Cols; c++) g[CursorRow, c] = blank;
            }
            else if (mode == 2)
            {
                for (int c = 0; c < Cols; c++) g[CursorRow, c] = blank;
            }
        }

        private void InsertBlankChars(int count)
        {
            TermCell[,] g = ActiveGrid;
            if (CursorRow < 0 || CursorRow >= Rows) return;
            count = Math.Max(1, Math.Min(Cols - CursorCol, count));
            for (int c = Cols - 1; c >= CursorCol + count; c--)
                g[CursorRow, c] = g[CursorRow, c - count];
            TermCell blank = BlankAttrCell();
            for (int c = CursorCol; c < CursorCol + count && c < Cols; c++)
                g[CursorRow, c] = blank;
        }

        private void DeleteChars(int count)
        {
            TermCell[,] g = ActiveGrid;
            if (CursorRow < 0 || CursorRow >= Rows) return;
            count = Math.Max(1, Math.Min(Cols - CursorCol, count));
            for (int c = CursorCol; c < Cols - count; c++)
                g[CursorRow, c] = g[CursorRow, c + count];
            TermCell blank = BlankAttrCell();
            for (int c = Cols - count; c < Cols; c++)
                g[CursorRow, c] = blank;
        }

        private void EraseChars(int count)
        {
            TermCell[,] g = ActiveGrid;
            if (CursorRow < 0 || CursorRow >= Rows) return;
            TermCell blank = BlankAttrCell();
            for (int c = CursorCol; c < Math.Min(Cols, CursorCol + count); c++)
                g[CursorRow, c] = blank;
        }

        private void ExecuteSgrRaw(string rawParams)
        {
            if (string.IsNullOrEmpty(rawParams))
            {
                ResetAttr();
                return;
            }

            string[] parts = rawParams.Split(';');
            List<int> expanded = new List<int>(parts.Length + 4);
            for (int p = 0; p < parts.Length; p++)
            {
                string tok = parts[p];
                if (string.IsNullOrEmpty(tok))
                {
                    expanded.Add(0);
                    continue;
                }
                if (tok.IndexOf(':') >= 0)
                {
                    string[] sub = tok.Split(':');
                    int mainCode = 0;
                    int.TryParse(sub[0], out mainCode);
                    if (mainCode == 4)
                    {
                        int ulStyle = 1;
                        if (sub.Length > 1) int.TryParse(sub[1], out ulStyle);
                        expanded.Add(ulStyle == 0 ? 24 : 4);
                    }
                    else if ((mainCode == 38 || mainCode == 48) && sub.Length >= 2)
                    {
                        int mode = 0;
                        int.TryParse(sub[1], out mode);
                        if (mode == 5 && sub.Length >= 3)
                        {
                            int idx256 = 0;
                            int.TryParse(sub[2], out idx256);
                            expanded.Add(mainCode);
                            expanded.Add(5);
                            expanded.Add(idx256);
                        }
                        else if (mode == 2 && sub.Length >= 5)
                        {
                            // Supports both 38:2:R:G:B (5 parts) and ITU 38:2:cs:R:G:B (6 parts)
                            int off = sub.Length >= 6 ? 3 : 2;
                            int r = 0, g = 0, b = 0;
                            int.TryParse(sub[off], out r);
                            int.TryParse(sub[off + 1], out g);
                            int.TryParse(sub[off + 2], out b);
                            expanded.Add(mainCode);
                            expanded.Add(2);
                            expanded.Add(r);
                            expanded.Add(g);
                            expanded.Add(b);
                        }
                    }
                    else if (mainCode == 58 || mainCode == 59)
                    {
                        // Underline color SGR — ignore without resetting attributes
                    }
                    continue;
                }
                int v;
                if (int.TryParse(tok, out v))
                    expanded.Add(v);
            }
            if (expanded.Count > 0)
                ExecuteSgr(expanded.ToArray());
        }

        private void ExecuteSgr(int[] nums)
        {
            if (nums == null || nums.Length == 0)
            {
                ResetAttr();
                return;
            }

            int i = 0;
            while (i < nums.Length)
            {
                int code = nums[i];
                if (code == 0)
                {
                    ResetAttr();
                }
                else if (code == 1)
                {
                    curFlags |= TermCell.FLAG_BOLD;
                }
                else if (code == 2)
                {
                    curFlags |= TermCell.FLAG_DIM;
                }
                else if (code == 3)
                {
                    curFlags |= TermCell.FLAG_ITALIC;
                }
                else if (code == 4)
                {
                    curFlags |= TermCell.FLAG_UNDERLINE;
                }
                else if (code == 7)
                {
                    curFlags |= TermCell.FLAG_INVERSE;
                }
                else if (code == 22)
                {
                    curFlags = (byte)(curFlags & ~(TermCell.FLAG_BOLD | TermCell.FLAG_DIM));
                }
                else if (code == 23)
                {
                    curFlags = (byte)(curFlags & ~TermCell.FLAG_ITALIC);
                }
                else if (code == 24)
                {
                    curFlags = (byte)(curFlags & ~TermCell.FLAG_UNDERLINE);
                }
                else if (code == 27)
                {
                    curFlags = (byte)(curFlags & ~TermCell.FLAG_INVERSE);
                }
                else if (code >= 30 && code <= 37)
                {
                    curFg = AnsiPalette[code - 30];
                    curFlags = (byte)(curFlags & ~TermCell.FLAG_DEFAULT_FG);
                }
                else if (code == 39)
                {
                    curFg = DefaultFg;
                    curFlags |= TermCell.FLAG_DEFAULT_FG;
                }
                else if (code == 40)
                {
                    curBg = DefaultBg;
                    curFlags |= TermCell.FLAG_DEFAULT_BG;
                }
                else if (code >= 41 && code <= 47)
                {
                    curBg = AnsiPalette[code - 40];
                    curFlags = (byte)(curFlags & ~TermCell.FLAG_DEFAULT_BG);
                }
                else if (code == 49)
                {
                    curBg = DefaultBg;
                    curFlags |= TermCell.FLAG_DEFAULT_BG;
                }
                else if (code >= 90 && code <= 97)
                {
                    curFg = AnsiPalette[code - 90 + 8];
                    curFlags = (byte)(curFlags & ~TermCell.FLAG_DEFAULT_FG);
                }
                else if (code >= 100 && code <= 107)
                {
                    curBg = AnsiPalette[code - 100 + 8];
                    curFlags = (byte)(curFlags & ~TermCell.FLAG_DEFAULT_BG);
                }
                else if (code == 38 || code == 48)
                {
                    bool isFg = (code == 38);
                    if (i + 2 < nums.Length && nums[i + 1] == 5)
                    {
                        Color col256 = DecodeXterm256Color(nums[i + 2]);
                        if (isFg)
                        {
                            curFg = col256;
                            curFlags = (byte)(curFlags & ~TermCell.FLAG_DEFAULT_FG);
                        }
                        else
                        {
                            curBg = col256;
                            curFlags = (byte)(curFlags & ~TermCell.FLAG_DEFAULT_BG);
                        }
                        i += 2;
                    }
                    else if (i + 4 < nums.Length && nums[i + 1] == 2)
                    {
                        int r = Math.Max(0, Math.Min(255, nums[i + 2]));
                        int g = Math.Max(0, Math.Min(255, nums[i + 3]));
                        int b = Math.Max(0, Math.Min(255, nums[i + 4]));
                        Color colRgb = Color.FromArgb(r, g, b);
                        if (isFg)
                        {
                            curFg = colRgb;
                            curFlags = (byte)(curFlags & ~TermCell.FLAG_DEFAULT_FG);
                        }
                        else
                        {
                            curBg = colRgb;
                            curFlags = (byte)(curFlags & ~TermCell.FLAG_DEFAULT_BG);
                        }
                        i += 4;
                    }
                }
                i++;
            }
        }

        public Color DecodeXterm256Color(int index)
        {
            index = Math.Max(0, Math.Min(255, index));
            if (index < 16)
                return AnsiPalette[index];
            if (index < 232)
            {
                int idx = index - 16;
                int r = (idx / 36) % 6;
                int g = (idx / 6) % 6;
                int b = idx % 6;
                int[] cube = new int[] { 0, 95, 135, 175, 215, 255 };
                return Color.FromArgb(cube[r], cube[g], cube[b]);
            }
            int gray = 8 + (index - 232) * 10;
            return Color.FromArgb(gray, gray, gray);
        }

        public TermCell GetCell(int row, int col)
        {
            lock (syncLock)
            {
                TermCell[,] g = ActiveGrid;
                if (row >= 0 && row < Rows && col >= 0 && col < Cols)
                    return g[row, col];
                return TermCell.Empty(DefaultFg, DefaultBg);
            }
        }

        public TermCell GetVirtualCell(int virtualRow, int col)
        {
            lock (syncLock)
            {
                if (col < 0 || col >= Cols)
                    return TermCell.Empty(DefaultFg, DefaultBg);

                if (!IsAlternateScreen && virtualRow < scrollback.Count)
                {
                    if (virtualRow >= 0)
                    {
                        TermCell[] line = scrollback[virtualRow];
                        if (col < line.Length) return line[col];
                    }
                    return TermCell.Empty(DefaultFg, DefaultBg);
                }

                int screenRow = IsAlternateScreen ? virtualRow : (virtualRow - scrollback.Count);
                TermCell[,] g = ActiveGrid;
                if (screenRow >= 0 && screenRow < Rows && col < Cols)
                    return g[screenRow, col];
                return TermCell.Empty(DefaultFg, DefaultBg);
            }
        }

        public int TotalVirtualRows
        {
            get
            {
                lock (syncLock)
                {
                    return (IsAlternateScreen ? 0 : scrollback.Count) + Rows;
                }
            }
        }

        public string GetLineText(int screenRow)
        {
            lock (syncLock)
            {
                TermCell[,] g = ActiveGrid;
                if (screenRow < 0 || screenRow >= Rows) return "";
                StringBuilder sb = new StringBuilder(Cols);
                for (int c = 0; c < Cols; c++)
                    sb.Append(g[screenRow, c].Char == '\0' ? ' ' : g[screenRow, c].Char);
                return sb.ToString().TrimEnd();
            }
        }

        public string GetVirtualLineText(int virtualRow)
        {
            lock (syncLock)
            {
                int total = (IsAlternateScreen ? 0 : scrollback.Count) + Rows;
                if (virtualRow < 0 || virtualRow >= total) return "";
                StringBuilder sb = new StringBuilder(Cols);
                for (int c = 0; c < Cols; c++)
                {
                    TermCell cell = GetVirtualCell(virtualRow, c);
                    sb.Append(cell.Char == '\0' ? ' ' : cell.Char);
                }
                return sb.ToString().TrimEnd();
            }
        }

        public string GetScrollbackAndScreenText(int maxRecentLines = 250)
        {
            lock (syncLock)
            {
                List<string> lines = new List<string>();
                int total = (IsAlternateScreen ? 0 : scrollback.Count) + Rows;
                int start = Math.Max(0, total - maxRecentLines);
                for (int vr = start; vr < total; vr++)
                {
                    lines.Add(GetVirtualLineText(vr));
                }
                // Trim trailing blank lines
                while (lines.Count > 0 && string.IsNullOrWhiteSpace(lines[lines.Count - 1]))
                    lines.RemoveAt(lines.Count - 1);
                return string.Join("\n", lines);
            }
        }

        public void FeedText(string text)
        {
            ProcessText(text);
        }

        public TermCell GetViewportCell(int row, int col, int scrollbackOffset = 0)
        {
            if (scrollbackOffset > 0 && !IsAlternateScreen)
            {
                lock (syncLock)
                {
                    int clampedOffset = Math.Min(scrollbackOffset, scrollback.Count);
                    int virtualRow = Math.Max(0, scrollback.Count - clampedOffset + row);
                    return GetVirtualCell(virtualRow, col);
                }
            }
            return GetCell(row, col);
        }

        public string GetViewportLineText(int screenRow)
        {
            return GetLineText(screenRow);
        }

        public string GetFullTranscriptText()
        {
            return GetScrollbackAndScreenText(5000);
        }

        public string EncodeMouseWheelSequence(bool wheelUp, int oneBasedCol, int oneBasedRow, bool shift, bool alt, bool ctrl)
        {
            lock (syncLock)
            {
                int cx = Math.Max(1, Math.Min(Cols, oneBasedCol));
                int cy = Math.Max(1, Math.Min(Rows, oneBasedRow));

                if (MouseTrackingMode != 0 && !shift)
                {
                    int btn = (wheelUp ? 64 : 65) | (alt ? 8 : 0) | (ctrl ? 16 : 0);
                    if (SgrMouseMode)
                        return string.Format("\x1b[<{0};{1};{2}M", btn, cx, cy);
                    if (cx <= 223 && cy <= 223)
                        return "\x1b[M" + (char)(32 + btn) + (char)(32 + cx) + (char)(32 + cy);
                    return null;
                }

                if (IsAlternateScreen && AlternateScrollMode && !shift)
                {
                    return wheelUp
                        ? (ApplicationCursorKeys ? "\x1bOA" : "\x1b[A")
                        : (ApplicationCursorKeys ? "\x1bOB" : "\x1b[B");
                }

                return null;
            }
        }

        public string EncodeMouseButtonOrMoveSequence(int buttonCode, bool isRelease, bool isMotion, int oneBasedCol, int oneBasedRow, bool shift, bool alt, bool ctrl)
        {
            lock (syncLock)
            {
                if (MouseTrackingMode == 0 || shift)
                    return null;

                int cx = Math.Max(1, Math.Min(Cols, oneBasedCol));
                int cy = Math.Max(1, Math.Min(Rows, oneBasedRow));
                int mods = (alt ? 8 : 0) | (ctrl ? 16 : 0);

                if (isMotion)
                {
                    if (buttonCode < 0 && MouseTrackingMode != 1003)
                        return null;
                    if (buttonCode >= 0 && MouseTrackingMode < 1002)
                        return null;
                    int motionCode = (buttonCode >= 0 ? (32 + buttonCode) : 35) | mods;
                    if (SgrMouseMode)
                        return string.Format("\x1b[<{0};{1};{2}M", motionCode, cx, cy);
                    if (cx <= 223 && cy <= 223)
                        return "\x1b[M" + (char)(32 + motionCode) + (char)(32 + cx) + (char)(32 + cy);
                    return null;
                }

                if (MouseTrackingMode == 9 && isRelease)
                    return null;

                if (SgrMouseMode)
                {
                    int sgrCode = Math.Max(0, buttonCode) | mods;
                    return string.Format("\x1b[<{0};{1};{2}{3}", sgrCode, cx, cy, isRelease ? 'm' : 'M');
                }

                if (cx <= 223 && cy <= 223)
                {
                    int x10Code = (isRelease ? 3 : Math.Max(0, buttonCode)) | mods;
                    return "\x1b[M" + (char)(32 + x10Code) + (char)(32 + cx) + (char)(32 + cy);
                }

                return null;
            }
        }
    }

    // =========================================================================
    // 4. SHELL PROFILES & REAL CONPTY SESSION MANAGER
    // =========================================================================
    public class TerminalProfileInfo
    {
        public string Id;
        public string Name;
        public string IconGlyph;
        public string CommandLine;
        public Color AccentColor;
    }

    public class ConPtyTerminalSession : IDisposable
    {
        public string Id = Guid.NewGuid().ToString("N").Substring(0, 8);
        public string Title = "pwsh";
        public string ShellName = "PowerShell";
        public string CommandLine = "";
        public string IconGlyph = ">_";
        public Color CustomTabColor = Color.Empty;
        public string CustomIconKind = "terminal";
        public string WorkingDirectory;
        public Vt100TerminalBuffer Buffer;
        public StringBuilder Transcript = new StringBuilder();

        public bool IsUsingConPty { get; private set; }
        public bool HasExited { get; private set; }
        public bool IsRunning { get { return !HasExited && !isDisposed; } }
        public int ExitCode { get; private set; }
        public int ProcessId { get; private set; }
        public void WriteCommand(string cmd) { SendCommandLine(cmd); }

        private IntPtr hPC = IntPtr.Zero;
        private IntPtr hPipeInRead = IntPtr.Zero;
        private IntPtr hPipeInWrite = IntPtr.Zero;
        private IntPtr hPipeOutRead = IntPtr.Zero;
        private IntPtr hPipeOutWrite = IntPtr.Zero;
        private IntPtr hProcess = IntPtr.Zero;
        private IntPtr hThread = IntPtr.Zero;
        private IntPtr lpAttributeList = IntPtr.Zero;
        private FileStream conPtyWriteStream;
        private FileStream conPtyReadStream;
        private int unixMasterFd = -1;
        private readonly object unixWriteLock = new object();

        // Fallback redirected process (only used if ConPTY/UnixPTY fails)
        private Process fallbackProcess;
        private volatile bool isDisposed = false;
        private volatile int sessionGeneration = 0;

        public event Action OutputReceived;
        public event Action<int> ProcessExited;

        public static string GetDefaultHomeDirectory()
        {
            try
            {
                string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                if (!string.IsNullOrEmpty(home) && Directory.Exists(home))
                    return home;
            }
            catch { }
            return Environment.CurrentDirectory;
        }

        public ConPtyTerminalSession(string shellName, string commandLine, string workingDir, int cols, int rows, bool isLightTheme = false)
        {
            if (string.IsNullOrEmpty(shellName))
                shellName = ConPtyNative.IsWindowsPlatform() ? "PowerShell" : Path.GetFileName(GetUserUnixShell());
            ShellName = shellName;
            Title = ShellName;
            WorkingDirectory = (!string.IsNullOrEmpty(workingDir) && Directory.Exists(workingDir))
                ? Path.GetFullPath(workingDir)
                : GetDefaultHomeDirectory();

            Buffer = new Vt100TerminalBuffer(Math.Max(20, cols), Math.Max(6, rows), isLightTheme);
            Buffer.WriteBackCallback = s => WriteInput(s);

            if (string.IsNullOrEmpty(commandLine))
                commandLine = ResolveDefaultCommandLine(ShellName);
            CommandLine = commandLine;

            if (!ConPtyNative.IsWindowsPlatform())
            {
                if (!StartUnixPtyProcess(CommandLine, WorkingDirectory, (short)Buffer.Cols, (short)Buffer.Rows))
                {
                    StartFallbackRedirectedProcess(CommandLine, WorkingDirectory);
                }
            }
            else if (!StartConPtyProcess(CommandLine, WorkingDirectory, (short)Buffer.Cols, (short)Buffer.Rows))
            {
                StartFallbackRedirectedProcess(CommandLine, WorkingDirectory);
            }
        }

        public static string GetUserUnixShell()
        {
            try
            {
                string envShell = Environment.GetEnvironmentVariable("SHELL");
                if (!string.IsNullOrWhiteSpace(envShell) && File.Exists(envShell))
                    return envShell;
            }
            catch { }
            if (File.Exists("/bin/zsh")) return "/bin/zsh";
            if (File.Exists("/bin/bash")) return "/bin/bash";
            return "/bin/sh";
        }

        public static List<TerminalProfileInfo> DetectAvailableProfiles()
        {
            List<TerminalProfileInfo> list = new List<TerminalProfileInfo>();

            if (!ConPtyNative.IsWindowsPlatform())
            {
                string userShell = GetUserUnixShell();
                list.Add(new TerminalProfileInfo
                {
                    Id = "default-shell",
                    Name = Path.GetFileName(userShell) + " ($SHELL)",
                    IconGlyph = ">_",
                    CommandLine = userShell + " -l",
                    AccentColor = Color.FromArgb(56, 189, 248)
                });
                if (File.Exists("/bin/zsh") && !string.Equals(userShell, "/bin/zsh", StringComparison.Ordinal))
                {
                    list.Add(new TerminalProfileInfo
                    {
                        Id = "zsh",
                        Name = "zsh",
                        IconGlyph = ">_",
                        CommandLine = "/bin/zsh -l",
                        AccentColor = Color.FromArgb(99, 102, 241)
                    });
                }
                if (File.Exists("/bin/bash") && !string.Equals(userShell, "/bin/bash", StringComparison.Ordinal))
                {
                    list.Add(new TerminalProfileInfo
                    {
                        Id = "bash",
                        Name = "bash",
                        IconGlyph = ">_",
                        CommandLine = "/bin/bash -l",
                        AccentColor = Color.FromArgb(52, 211, 153)
                    });
                }
                string unixPy = FindExecutableInPath("python3") ?? FindExecutableInPath("python");
                if (!string.IsNullOrEmpty(unixPy))
                {
                    list.Add(new TerminalProfileInfo
                    {
                        Id = "python",
                        Name = "Python Interactive REPL",
                        IconGlyph = "py",
                        CommandLine = unixPy + " -i",
                        AccentColor = Color.FromArgb(250, 204, 21)
                    });
                }
                return list;
            }

            list.Add(new TerminalProfileInfo
            {
                Id = "powershell",
                Name = "PowerShell",
                IconGlyph = ">_",
                CommandLine = "powershell.exe -NoLogo",
                AccentColor = Color.FromArgb(56, 189, 248)
            });

            string pwshPath = FindExecutableInPath("pwsh.exe");
            if (!string.IsNullOrEmpty(pwshPath))
            {
                list.Add(new TerminalProfileInfo
                {
                    Id = "pwsh",
                    Name = "PowerShell Core (pwsh)",
                    IconGlyph = ">_",
                    CommandLine = "\"" + pwshPath + "\" -NoLogo",
                    AccentColor = Color.FromArgb(99, 102, 241)
                });
            }

            list.Add(new TerminalProfileInfo
            {
                Id = "cmd",
                Name = "Command Prompt",
                IconGlyph = ">_",
                CommandLine = "cmd.exe",
                AccentColor = Color.FromArgb(161, 161, 170)
            });

            string[] gitBashCandidates = new string[]
            {
                @"C:\Program Files\Git\bin\bash.exe",
                @"C:\Program Files\Git\usr\bin\bash.exe",
                @"C:\Program Files (x86)\Git\bin\bash.exe"
            };
            foreach (string gb in gitBashCandidates)
            {
                if (File.Exists(gb))
                {
                    list.Add(new TerminalProfileInfo
                    {
                        Id = "gitbash",
                        Name = "Git Bash",
                        IconGlyph = "git",
                        CommandLine = "\"" + gb + "\" --login -i",
                        AccentColor = Color.FromArgb(249, 115, 22)
                    });
                    break;
                }
            }

            string wslPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "wsl.exe");
            if (File.Exists(wslPath))
            {
                list.Add(new TerminalProfileInfo
                {
                    Id = "wsl",
                    Name = "WSL (Linux)",
                    IconGlyph = ">_",
                    CommandLine = "wsl.exe",
                    AccentColor = Color.FromArgb(52, 211, 153)
                });
            }

            string pyPath = FindExecutableInPath("python.exe");
            if (!string.IsNullOrEmpty(pyPath))
            {
                list.Add(new TerminalProfileInfo
                {
                    Id = "python",
                    Name = "Python Interactive REPL",
                    IconGlyph = "py",
                    CommandLine = "\"" + pyPath + "\" -i",
                    AccentColor = Color.FromArgb(250, 204, 21)
                });
            }

            return list;
        }

        private static string FindExecutableInPath(string exeName)
        {
            try
            {
                string pathEnv = Environment.GetEnvironmentVariable("PATH") ?? "";
                char sep = Path.PathSeparator;
                foreach (string dir in pathEnv.Split(new char[] { sep }, StringSplitOptions.RemoveEmptyEntries))
                {
                    try
                    {
                        string candidate = Path.Combine(dir.Trim(), exeName);
                        if (File.Exists(candidate)) return candidate;
                    }
                    catch { }
                }
            }
            catch { }
            return null;
        }

        public static string ResolveDefaultCommandLine(string shellName)
        {
            if (!ConPtyNative.IsWindowsPlatform())
            {
                string lowUnix = (shellName ?? "").ToLowerInvariant();
                if (lowUnix.Contains("python"))
                    return (FindExecutableInPath("python3") ?? "python3") + " -i";
                if (lowUnix.Contains("bash") && File.Exists("/bin/bash"))
                    return "/bin/bash -l";
                if (lowUnix.Contains("zsh") && File.Exists("/bin/zsh"))
                    return "/bin/zsh -l";
                return GetUserUnixShell() + " -l";
            }

            string low = (shellName ?? "").ToLowerInvariant();
            if (low.Contains("cmd") || low.Contains("command")) return "cmd.exe";
            if (low.Contains("python")) return "python.exe -i";
            if (low.Contains("wsl")) return "wsl.exe";
            if (low.Contains("git") || low.Contains("bash"))
            {
                if (File.Exists(@"C:\Program Files\Git\bin\bash.exe"))
                    return "\"C:\\Program Files\\Git\\bin\\bash.exe\" --login -i";
                return "bash.exe";
            }
            return "powershell.exe -NoLogo";
        }

        private bool StartUnixPtyProcess(string commandLine, string workingDir, short cols, short rows)
        {
            IntPtr unmanagedShell = IntPtr.Zero;
            IntPtr unmanagedArg0 = IntPtr.Zero;
            IntPtr unmanagedArg1 = IntPtr.Zero;
            IntPtr unmanagedWorkDir = IntPtr.Zero;
            IntPtr unmanagedArgv = IntPtr.Zero;
            IntPtr unmanagedSlavePath = IntPtr.Zero;
            try
            {
                string shellExe = GetUserUnixShell();
                string shellArg = "-l";
                if (!string.IsNullOrWhiteSpace(commandLine))
                {
                    string[] parts = commandLine.Trim().Split(new char[] { ' ' }, 2, StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length > 0)
                    {
                        string candidate = parts[0];
                        if (!candidate.StartsWith("/") && !File.Exists(candidate))
                        {
                            string[] searchDirs = { "/bin", "/usr/bin", "/opt/homebrew/bin", "/usr/local/bin" };
                            foreach (string dir in searchDirs)
                            {
                                string fullCand = Path.Combine(dir, candidate);
                                if (File.Exists(fullCand))
                                {
                                    candidate = fullCand;
                                    break;
                                }
                            }
                        }
                        if (candidate.StartsWith("/") || File.Exists(candidate))
                        {
                            shellExe = candidate;
                            shellArg = parts.Length > 1 ? parts[1] : "-l";
                        }
                    }
                }

                try
                {
                    Environment.SetEnvironmentVariable("TERM", "xterm-256color");
                    Environment.SetEnvironmentVariable("COLORTERM", "truecolor");
                }
                catch { }

                unmanagedShell = UnixPtyNative.AllocUtf8HGlobal(shellExe);
                unmanagedArg0 = UnixPtyNative.AllocUtf8HGlobal(Path.GetFileName(shellExe));
                unmanagedArg1 = UnixPtyNative.AllocUtf8HGlobal(shellArg);
                unmanagedWorkDir = UnixPtyNative.AllocUtf8HGlobal(string.IsNullOrEmpty(workingDir) ? GetDefaultHomeDirectory() : workingDir);
                unmanagedArgv = Marshal.AllocHGlobal(IntPtr.Size * 3);
                Marshal.WriteIntPtr(unmanagedArgv, 0, unmanagedArg0);
                Marshal.WriteIntPtr(unmanagedArgv, IntPtr.Size, unmanagedArg1);
                Marshal.WriteIntPtr(unmanagedArgv, IntPtr.Size * 2, IntPtr.Zero);

                UnixPtyNative.winsize ws = new UnixPtyNative.winsize((ushort)Math.Max((short)6, rows), (ushort)Math.Max((short)20, cols));
                int masterFd = -1;
                int pid = -1;
                bool usedForkPty = false;

                try
                {
                    pid = UnixPtyNative.forkpty(out masterFd, IntPtr.Zero, IntPtr.Zero, ref ws);
                    usedForkPty = true;
                }
                catch (Exception ex)
                {
                    if (!(ex is DllNotFoundException) && !(ex is EntryPointNotFoundException))
                        throw;
                    try
                    {
                        pid = UnixPtyNative.forkpty_libutil(out masterFd, IntPtr.Zero, IntPtr.Zero, ref ws);
                        usedForkPty = true;
                    }
                    catch (Exception ex2)
                    {
                        if (!(ex2 is DllNotFoundException) && !(ex2 is EntryPointNotFoundException))
                            throw;
                        usedForkPty = false;
                    }
                }

                if (!usedForkPty)
                {
                    string slavePath;
                    if (!UnixPtyNative.OpenPosixPtyMaster(cols, rows, out masterFd, out slavePath) || masterFd < 0 || string.IsNullOrEmpty(slavePath))
                    {
                        return false;
                    }
                    unmanagedSlavePath = UnixPtyNative.AllocUtf8HGlobal(slavePath);
                    pid = UnixPtyNative.fork();
                    if (pid < 0)
                    {
                        try { UnixPtyNative.close(masterFd); } catch { }
                        return false;
                    }
                    if (pid == 0)
                    {
                        UnixPtyNative.close(masterFd);
                        UnixPtyNative.setsid();
                        int slaveFd = UnixPtyNative.open_ptr(unmanagedSlavePath, UnixPtyNative.O_RDWR);
                        if (slaveFd < 0)
                        {
                            UnixPtyNative._exit(126);
                            return false;
                        }
                        UnixPtyNative.ioctl(slaveFd, UnixPtyNative.GetTiocscttyRequest(), 0);
                        UnixPtyNative.dup2(slaveFd, 0);
                        UnixPtyNative.dup2(slaveFd, 1);
                        UnixPtyNative.dup2(slaveFd, 2);
                        if (slaveFd > 2)
                        {
                            UnixPtyNative.close(slaveFd);
                        }
                        UnixPtyNative.chdir(unmanagedWorkDir);
                        UnixPtyNative.execvp(unmanagedShell, unmanagedArgv);
                        UnixPtyNative._exit(127);
                        return false;
                    }
                }
                else
                {
                    if (pid < 0)
                    {
                        return false;
                    }
                    if (pid == 0)
                    {
                        // Child process: execute only unmanaged libc calls with pre-allocated pointers
                        UnixPtyNative.chdir(unmanagedWorkDir);
                        UnixPtyNative.execvp(unmanagedShell, unmanagedArgv);
                        UnixPtyNative._exit(127);
                        return false;
                    }
                }

                // Parent process
                unixMasterFd = masterFd;
                ProcessId = pid;
                IsUsingConPty = true;
                HasExited = false;
                int gen = ++sessionGeneration;

                Thread readerThread = new Thread(() => UnixPtyReadLoop(gen, masterFd));
                readerThread.IsBackground = true;
                readerThread.Name = "UnixPtyReader_" + Id + "_" + gen;
                readerThread.Start();

                Thread waitThread = new Thread(() => UnixPtyProcessWaitLoop(gen, pid));
                waitThread.IsBackground = true;
                waitThread.Name = "UnixPtyWait_" + Id + "_" + gen;
                waitThread.Start();

                return true;
            }
            catch
            {
                return false;
            }
            finally
            {
                if (unmanagedShell != IntPtr.Zero) Marshal.FreeHGlobal(unmanagedShell);
                if (unmanagedArg0 != IntPtr.Zero) Marshal.FreeHGlobal(unmanagedArg0);
                if (unmanagedArg1 != IntPtr.Zero) Marshal.FreeHGlobal(unmanagedArg1);
                if (unmanagedWorkDir != IntPtr.Zero) Marshal.FreeHGlobal(unmanagedWorkDir);
                if (unmanagedArgv != IntPtr.Zero) Marshal.FreeHGlobal(unmanagedArgv);
                if (unmanagedSlavePath != IntPtr.Zero) Marshal.FreeHGlobal(unmanagedSlavePath);
            }
        }

        private void UnixPtyReadLoop(int gen, int masterFd)
        {
            byte[] buf = new byte[4096];
            try
            {
                while (!isDisposed && gen == sessionGeneration && masterFd >= 0)
                {
                    IntPtr n = UnixPtyNative.read(masterFd, buf, (IntPtr)buf.Length);
                    int readCount = n.ToInt32();
                    if (readCount <= 0 || gen != sessionGeneration || isDisposed) break;

                    Buffer.ProcessBytes(buf, readCount);
                    SyncTranscriptSnapshot();

                    Action handler = OutputReceived;
                    if (handler != null)
                    {
                        try { handler(); } catch { }
                    }
                }
            }
            catch
            {
            }
        }

        private void UnixPtyProcessWaitLoop(int gen, int childPid)
        {
            try
            {
                if (childPid <= 0) return;
                int status = 0;
                UnixPtyNative.waitpid(childPid, out status, 0);
                if (isDisposed || gen != sessionGeneration) return;

                ExitCode = (status >> 8) & 0xFF;
                HasExited = true;

                string exitMsg = string.Format("\r\n\x1b[90m[process exited with code {0}]\x1b[0m\r\n", ExitCode);
                Buffer.ProcessText(exitMsg);
                SyncTranscriptSnapshot();

                Action outHandler = OutputReceived;
                if (outHandler != null) { try { outHandler(); } catch { } }

                Action<int> exitHandler = ProcessExited;
                if (exitHandler != null) { try { exitHandler(ExitCode); } catch { } }
            }
            catch
            {
            }
        }

        private bool StartConPtyProcess(string commandLine, string workingDir, short cols, short rows)
        {
            bool startedOk = false;
            try
            {
                ConPtyNative.SECURITY_ATTRIBUTES sa = new ConPtyNative.SECURITY_ATTRIBUTES();
                sa.nLength = Marshal.SizeOf(typeof(ConPtyNative.SECURITY_ATTRIBUTES));
                sa.bInheritHandle = 1;

                if (!ConPtyNative.CreatePipe(out hPipeInRead, out hPipeInWrite, ref sa, 0))
                    return false;
                if (!ConPtyNative.CreatePipe(out hPipeOutRead, out hPipeOutWrite, ref sa, 0))
                    return false;

                ConPtyNative.SetHandleInformation(hPipeInWrite, ConPtyNative.HANDLE_FLAG_INHERIT, 0);
                ConPtyNative.SetHandleInformation(hPipeOutRead, ConPtyNative.HANDLE_FLAG_INHERIT, 0);

                int hr = ConPtyNative.CreatePseudoConsole(new ConPtyNative.COORD(cols, rows), hPipeInRead, hPipeOutWrite, 0, out hPC);
                if (hr != 0 || hPC == IntPtr.Zero)
                    return false;

                // Close child-side pipe ends in parent once pseudoconsole holds them
                if (hPipeInRead != IntPtr.Zero) { ConPtyNative.CloseHandle(hPipeInRead); hPipeInRead = IntPtr.Zero; }
                if (hPipeOutWrite != IntPtr.Zero) { ConPtyNative.CloseHandle(hPipeOutWrite); hPipeOutWrite = IntPtr.Zero; }

                IntPtr lpSize = IntPtr.Zero;
                ConPtyNative.InitializeProcThreadAttributeList(IntPtr.Zero, 1, 0, ref lpSize);
                lpAttributeList = Marshal.AllocHGlobal(lpSize);
                if (!ConPtyNative.InitializeProcThreadAttributeList(lpAttributeList, 1, 0, ref lpSize))
                    return false;

                if (!ConPtyNative.UpdateProcThreadAttribute(
                    lpAttributeList, 0, ConPtyNative.PROC_THREAD_ATTRIBUTE_PSEUDOCONSOLE,
                    hPC, (IntPtr)IntPtr.Size, IntPtr.Zero, IntPtr.Zero))
                {
                    return false;
                }

                ConPtyNative.STARTUPINFOEX siEx = new ConPtyNative.STARTUPINFOEX();
                siEx.StartupInfo.cb = Marshal.SizeOf(typeof(ConPtyNative.STARTUPINFOEX));
                siEx.StartupInfo.dwFlags = 0x00000100; // STARTF_USESTDHANDLES (prevents inheriting parent's redirected stdout/stderr)
                siEx.StartupInfo.hStdInput = IntPtr.Zero;
                siEx.StartupInfo.hStdOutput = IntPtr.Zero;
                siEx.StartupInfo.hStdError = IntPtr.Zero;
                siEx.lpAttributeList = lpAttributeList;

                try
                {
                    Environment.SetEnvironmentVariable("CHERE_INVOKING", "1");
                }
                catch { }

                StringBuilder cmdMutable = new StringBuilder(commandLine);
                ConPtyNative.PROCESS_INFORMATION pi;
                bool created = ConPtyNative.CreateProcessW(
                    null,
                    cmdMutable,
                    IntPtr.Zero,
                    IntPtr.Zero,
                    false,
                    ConPtyNative.EXTENDED_STARTUPINFO_PRESENT,
                    IntPtr.Zero,
                    workingDir,
                    ref siEx,
                    out pi);

                if (!created)
                    return false;

                hProcess = pi.hProcess;
                hThread = pi.hThread;
                ProcessId = pi.dwProcessId;
                IsUsingConPty = true;
                HasExited = false;
                int gen = ++sessionGeneration;

                conPtyWriteStream = new FileStream(new SafeFileHandle(hPipeInWrite, false), FileAccess.Write, 4096, false);
                conPtyReadStream = new FileStream(new SafeFileHandle(hPipeOutRead, false), FileAccess.Read, 4096, false);

                Thread readerThread = new Thread(() => ConPtyReadLoop(gen, conPtyReadStream));
                readerThread.IsBackground = true;
                readerThread.Name = "ConPtyReader_" + Id + "_" + gen;
                readerThread.Start();

                Thread waitThread = new Thread(() => ConPtyProcessWaitLoop(gen, hProcess));
                waitThread.IsBackground = true;
                waitThread.Name = "ConPtyWait_" + Id + "_" + gen;
                waitThread.Start();

                startedOk = true;
                return true;
            }
            catch
            {
                return false;
            }
            finally
            {
                if (!startedOk)
                {
                    if (lpAttributeList != IntPtr.Zero)
                    {
                        try { ConPtyNative.DeleteProcThreadAttributeList(lpAttributeList); } catch { }
                        try { Marshal.FreeHGlobal(lpAttributeList); } catch { }
                        lpAttributeList = IntPtr.Zero;
                    }
                    if (hPC != IntPtr.Zero) { try { ConPtyNative.ClosePseudoConsole(hPC); } catch { } hPC = IntPtr.Zero; }
                    if (hPipeInRead != IntPtr.Zero) { try { ConPtyNative.CloseHandle(hPipeInRead); } catch { } hPipeInRead = IntPtr.Zero; }
                    if (hPipeInWrite != IntPtr.Zero) { try { ConPtyNative.CloseHandle(hPipeInWrite); } catch { } hPipeInWrite = IntPtr.Zero; }
                    if (hPipeOutRead != IntPtr.Zero) { try { ConPtyNative.CloseHandle(hPipeOutRead); } catch { } hPipeOutRead = IntPtr.Zero; }
                    if (hPipeOutWrite != IntPtr.Zero) { try { ConPtyNative.CloseHandle(hPipeOutWrite); } catch { } hPipeOutWrite = IntPtr.Zero; }
                }
            }
        }

        private void ConPtyReadLoop(int gen, FileStream readStream)
        {
            byte[] buf = new byte[8192];
            int lastSnapshotTick = 0;
            try
            {
                while (!isDisposed && gen == sessionGeneration && readStream != null)
                {
                    int read = readStream.Read(buf, 0, buf.Length);
                    if (read <= 0 || gen != sessionGeneration || isDisposed) break;

                    Buffer.ProcessBytes(buf, read);
                    int nowTick = Environment.TickCount;
                    if (unchecked(nowTick - lastSnapshotTick) >= 250 || lastSnapshotTick == 0)
                    {
                        lastSnapshotTick = nowTick;
                        SyncTranscriptSnapshot();
                    }

                    Action handler = OutputReceived;
                    if (handler != null)
                    {
                        try { handler(); } catch { }
                    }
                }
                SyncTranscriptSnapshot();
            }
            catch
            {
            }
        }

        private void ConPtyProcessWaitLoop(int gen, IntPtr procHandle)
        {
            try
            {
                if (procHandle == IntPtr.Zero) return;
                ConPtyNative.WaitForSingleObject(procHandle, ConPtyNative.INFINITE);
                if (isDisposed || gen != sessionGeneration) return;

                uint code = 0;
                ConPtyNative.GetExitCodeProcess(procHandle, out code);
                ExitCode = (int)code;
                HasExited = true;

                string exitMsg = string.Format("\r\n\x1b[90m[process exited with code {0}]\x1b[0m\r\n", ExitCode);
                Buffer.ProcessText(exitMsg);
                SyncTranscriptSnapshot();

                Action outHandler = OutputReceived;
                if (outHandler != null) { try { outHandler(); } catch { } }

                Action<int> exitHandler = ProcessExited;
                if (exitHandler != null) { try { exitHandler(ExitCode); } catch { } }
            }
            catch
            {
            }
        }

        private void StartFallbackRedirectedProcess(string commandLine, string workingDir)
        {
            try
            {
                IsUsingConPty = false;
                string exe = ConPtyNative.IsWindowsPlatform() ? "powershell.exe" : GetUserUnixShell();
                string args = ConPtyNative.IsWindowsPlatform() ? "-NoLogo" : "-i";
                if (ConPtyNative.IsWindowsPlatform() && commandLine.StartsWith("cmd", StringComparison.OrdinalIgnoreCase))
                {
                    exe = "cmd.exe";
                    args = "";
                }

                ProcessStartInfo psi = new ProcessStartInfo(exe, args)
                {
                    WorkingDirectory = workingDir,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardInput = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    StandardOutputEncoding = Encoding.UTF8,
                    StandardErrorEncoding = Encoding.UTF8
                };

                fallbackProcess = new Process { StartInfo = psi, EnableRaisingEvents = true };
                fallbackProcess.OutputDataReceived += (s, e) =>
                {
                    if (e.Data != null)
                    {
                        Buffer.ProcessText(e.Data + "\r\n");
                        SyncTranscriptSnapshot();
                        Action h = OutputReceived;
                        if (h != null) { try { h(); } catch { } }
                    }
                };
                fallbackProcess.ErrorDataReceived += (s, e) =>
                {
                    if (e.Data != null)
                    {
                        Buffer.ProcessText("\x1b[91m" + e.Data + "\x1b[0m\r\n");
                        SyncTranscriptSnapshot();
                        Action h = OutputReceived;
                        if (h != null) { try { h(); } catch { } }
                    }
                };
                fallbackProcess.Exited += (s, e) =>
                {
                    HasExited = true;
                    try { ExitCode = fallbackProcess.ExitCode; } catch { }
                    Buffer.ProcessText(string.Format("\r\n\x1b[90m[process exited with code {0}]\x1b[0m\r\n", ExitCode));
                    SyncTranscriptSnapshot();
                    Action h = OutputReceived;
                    if (h != null) { try { h(); } catch { } }
                    Action<int> eh = ProcessExited;
                    if (eh != null) { try { eh(ExitCode); } catch { } }
                };

                fallbackProcess.Start();
                ProcessId = fallbackProcess.Id;
                fallbackProcess.BeginOutputReadLine();
                fallbackProcess.BeginErrorReadLine();
            }
            catch (Exception ex)
            {
                Buffer.ProcessText("\x1b[91mFailed starting terminal process: " + ex.Message + "\x1b[0m\r\n");
                SyncTranscriptSnapshot();
            }
        }

        private void SyncTranscriptSnapshot()
        {
            try
            {
                string snapshot = Buffer.GetScrollbackAndScreenText(200);
                lock (Transcript)
                {
                    Transcript.Length = 0;
                    Transcript.Append(snapshot);
                }
            }
            catch { }
        }

        public string GetRecentOutputText(int maxLines = 150)
        {
            return Buffer.GetScrollbackAndScreenText(maxLines);
        }

        public void WriteInput(string text)
        {
            if (string.IsNullOrEmpty(text) || isDisposed || HasExited) return;
            byte[] bytes = Encoding.UTF8.GetBytes(text);
            WriteBytes(bytes);
        }

        public void WriteBytes(byte[] bytes)
        {
            if (bytes == null || bytes.Length == 0 || isDisposed || HasExited) return;
            try
            {
                if (IsUsingConPty && unixMasterFd >= 0)
                {
                    lock (unixWriteLock)
                    {
                        if (unixMasterFd >= 0)
                            UnixPtyNative.write(unixMasterFd, bytes, (IntPtr)bytes.Length);
                    }
                }
                else if (IsUsingConPty && conPtyWriteStream != null)
                {
                    lock (conPtyWriteStream)
                    {
                        conPtyWriteStream.Write(bytes, 0, bytes.Length);
                        conPtyWriteStream.Flush();
                    }
                }
                else if (fallbackProcess != null && !fallbackProcess.HasExited)
                {
                    string str = Encoding.UTF8.GetString(bytes);
                    fallbackProcess.StandardInput.Write(str);
                    fallbackProcess.StandardInput.Flush();
                }
            }
            catch
            {
            }
        }

        public void SendCommandLine(string command)
        {
            if (command == null) return;
            WriteInput(command + "\r");
        }

        public void Resize(int cols, int rows)
        {
            cols = Math.Max(10, cols);
            rows = Math.Max(4, rows);
            if (cols == Buffer.Cols && rows == Buffer.Rows) return;

            Buffer.Resize(cols, rows);
            if (IsUsingConPty && !isDisposed)
            {
                if (unixMasterFd >= 0)
                {
                    try
                    {
                        UnixPtyNative.winsize ws = new UnixPtyNative.winsize((ushort)rows, (ushort)cols);
                        UnixPtyNative.ioctl(unixMasterFd, UnixPtyNative.GetTiocswinszRequest(), ref ws);
                    }
                    catch { }
                }
                else if (hPC != IntPtr.Zero)
                {
                    try
                    {
                        ConPtyNative.ResizePseudoConsole(hPC, new ConPtyNative.COORD((short)cols, (short)rows));
                    }
                    catch { }
                }
            }
        }

        public static string ConvertWindowsPathToPosix(string winPath, bool isWsl)
        {
            if (string.IsNullOrEmpty(winPath)) return "~";
            string norm = winPath.Trim().Replace('\\', '/');
            if (norm.Length >= 2 && char.IsLetter(norm[0]) && norm[1] == ':')
            {
                char drive = char.ToLowerInvariant(norm[0]);
                string rest = norm.Length > 2 ? norm.Substring(2) : "";
                if (!rest.StartsWith("/")) rest = "/" + rest;
                return (isWsl ? ("/mnt/" + drive) : ("/" + drive)) + rest;
            }
            return norm;
        }

        public static string BuildShellCdCommand(string shellName, string commandLine, string targetDir, bool clearScreen = false)
        {
            string lowShell = ((shellName ?? "") + " " + (commandLine ?? "")).ToLowerInvariant();
            if (lowShell.Contains("python"))
            {
                string escPy = (targetDir ?? "").Replace("\\", "\\\\").Replace("'", "\\'");
                return "import os; os.chdir('" + escPy + "')";
            }
            if (lowShell.Contains("wsl"))
            {
                string wslPath = ConvertWindowsPathToPosix(targetDir, true).Replace("\"", "\\\"");
                return clearScreen ? ("cd \"" + wslPath + "\" && clear") : ("cd \"" + wslPath + "\"");
            }
            if (lowShell.Contains("powershell") || lowShell.Contains("pwsh"))
            {
                string psPath = (targetDir ?? "").Replace("'", "''");
                return clearScreen ? ("Set-Location -LiteralPath '" + psPath + "'; Clear-Host") : ("Set-Location -LiteralPath '" + psPath + "'");
            }
            if (!ConPtyNative.IsWindowsPlatform() || lowShell.Contains("bash") || lowShell.Contains("git") || lowShell.Contains("zsh") || lowShell.Contains("/sh") || lowShell.Contains("\\sh."))
            {
                string posixPath = (ConPtyNative.IsWindowsPlatform() ? ConvertWindowsPathToPosix(targetDir, false) : (targetDir ?? "~")).Replace("\"", "\\\"");
                return clearScreen ? ("cd \"" + posixPath + "\" && clear") : ("cd \"" + posixPath + "\"");
            }
            if (lowShell.Contains("cmd") || lowShell.Contains("command"))
            {
                string cmdPath = (targetDir ?? "").Replace("\"", "");
                return clearScreen ? ("cd /d \"" + cmdPath + "\" & cls") : ("cd /d \"" + cmdPath + "\"");
            }
            string defPsPath = (targetDir ?? "").Replace("'", "''");
            return clearScreen ? ("Set-Location -LiteralPath '" + defPsPath + "'; Clear-Host") : ("Set-Location -LiteralPath '" + defPsPath + "'");
        }

        public bool HasRenderedPrompt()
        {
            string recent = GetRecentOutputText(40) ?? "";
            if (string.IsNullOrWhiteSpace(recent)) return false;
            string[] lines = recent.Replace("\r\n", "\n").Split('\n');
            for (int i = lines.Length - 1; i >= 0; i--)
            {
                string tr = lines[i].Trim();
                if (string.IsNullOrEmpty(tr)) continue;
                if (tr.StartsWith("PS ", StringComparison.Ordinal) && tr.Contains(">")) return true;
                if (tr.Length >= 4 && char.IsLetter(tr[0]) && tr[1] == ':' && tr[2] == '\\' && tr.Contains(">")) return true;
                if (tr.StartsWith(">>>", StringComparison.Ordinal)) return true;
                if (tr.EndsWith("$", StringComparison.Ordinal) || tr.EndsWith("#", StringComparison.Ordinal) || tr.EndsWith("%", StringComparison.Ordinal)) return true;
                break;
            }
            return false;
        }

        public bool IsIdleAtPrompt()
        {
            if (isDisposed || HasExited) return true;
            string recent = GetRecentOutputText(40) ?? "";
            if (string.IsNullOrWhiteSpace(recent)) return true;
            string[] lines = recent.Replace("\r\n", "\n").Split('\n');
            for (int i = lines.Length - 1; i >= 0; i--)
            {
                string tr = lines[i].Trim();
                if (string.IsNullOrEmpty(tr)) continue;
                if (tr.StartsWith("PS ", StringComparison.Ordinal) && tr.Contains(">")) return true;
                if (tr.Length >= 4 && char.IsLetter(tr[0]) && tr[1] == ':' && tr[2] == '\\' && tr.Contains(">")) return true;
                if (tr.StartsWith(">>>", StringComparison.Ordinal)) return true;
                if (tr.EndsWith("$", StringComparison.Ordinal) || tr.EndsWith("#", StringComparison.Ordinal) || tr.EndsWith("%", StringComparison.Ordinal)) return true;
                return false;
            }
            return true;
        }

        public void NavigateToDirectory(string newDir, bool clearScreen = false)
        {
            string resolved = (!string.IsNullOrEmpty(newDir) && Directory.Exists(newDir))
                ? Path.GetFullPath(newDir)
                : GetDefaultHomeDirectory();
            WorkingDirectory = resolved;
            if (isDisposed || HasExited) return;
            if (clearScreen && Buffer != null)
            {
                Buffer.ClearScrollbackAndScreen();
            }
            string cdCmd = BuildShellCdCommand(ShellName, CommandLine, resolved, clearScreen);
            SendCommandLine(cdCmd);
        }

        public void RespawnInDirectory(string newDir)
        {
            if (isDisposed) return;
            string resolved = (!string.IsNullOrEmpty(newDir) && Directory.Exists(newDir))
                ? Path.GetFullPath(newDir)
                : GetDefaultHomeDirectory();
            WorkingDirectory = resolved;

            CleanupCurrentProcessInternal();

            if (Buffer != null)
                Buffer.ClearScrollbackAndScreen();

            string cmd = string.IsNullOrEmpty(CommandLine) ? ResolveDefaultCommandLine(ShellName) : CommandLine;
            CommandLine = cmd;
            short c = (short)(Buffer != null ? Buffer.Cols : 90);
            short r = (short)(Buffer != null ? Buffer.Rows : 24);
            if (!ConPtyNative.IsWindowsPlatform())
            {
                if (!StartUnixPtyProcess(cmd, WorkingDirectory, c, r))
                    StartFallbackRedirectedProcess(cmd, WorkingDirectory);
            }
            else if (!StartConPtyProcess(cmd, WorkingDirectory, c, r))
            {
                StartFallbackRedirectedProcess(cmd, WorkingDirectory);
            }
        }

        public void UpdateWorkingDirectory(string newDir)
        {
            if (isDisposed) return;
            string resolved = (!string.IsNullOrEmpty(newDir) && Directory.Exists(newDir))
                ? Path.GetFullPath(newDir)
                : GetDefaultHomeDirectory();
            string normCur = (WorkingDirectory ?? "").TrimEnd('\\', '/');
            string normNew = resolved.TrimEnd('\\', '/');
            bool sameDir = string.Equals(normCur, normNew, StringComparison.OrdinalIgnoreCase);

            if (HasExited)
            {
                RespawnInDirectory(resolved);
                return;
            }

            if (!sameDir)
            {
                NavigateToDirectory(resolved, true);
            }
        }

        public string GetChildProcessWorkingDirectory()
        {
            string recent = GetRecentOutputText(30) ?? "";
            if (!string.IsNullOrWhiteSpace(recent))
            {
                string[] lines = recent.Replace("\r\n", "\n").Split('\n');
                for (int i = lines.Length - 1; i >= 0; i--)
                {
                    string tr = lines[i].Trim();
                    if (!tr.Contains(">") && i + 1 < lines.Length && lines[i + 1].Contains(">"))
                    {
                        tr = lines[i].TrimEnd() + lines[i + 1].TrimStart();
                    }
                    if (tr.StartsWith("PS ", StringComparison.Ordinal) && tr.Contains(">"))
                    {
                        int gt = tr.IndexOf('>');
                        if (gt > 3)
                        {
                            string candidate = tr.Substring(3, gt - 3).Trim();
                            if (Directory.Exists(candidate))
                                return Path.GetFullPath(candidate);
                        }
                    }
                    else if (tr.Length >= 4 && char.IsLetter(tr[0]) && tr[1] == ':' && tr[2] == '\\' && tr.Contains(">"))
                    {
                        int gt = tr.IndexOf('>');
                        if (gt >= 3)
                        {
                            string candidate = tr.Substring(0, gt).Trim();
                            if (Directory.Exists(candidate))
                                return Path.GetFullPath(candidate);
                        }
                    }
                }
            }
            return WorkingDirectory;
        }

        private void CleanupCurrentProcessInternal()
        {
            sessionGeneration++;
            FileStream oldWriteStream = conPtyWriteStream;
            FileStream oldReadStream = conPtyReadStream;
            int oldUnixFd = unixMasterFd;
            int oldPid = ProcessId;
            bool wasExited = HasExited;
            IntPtr oldHProcess = hProcess;
            IntPtr oldHThread = hThread;
            IntPtr oldHPC = hPC;
            IntPtr oldPipeIn = hPipeInWrite;
            IntPtr oldPipeOut = hPipeOutRead;
            IntPtr oldAttr = lpAttributeList;
            Process oldFallback = fallbackProcess;

            conPtyWriteStream = null;
            conPtyReadStream = null;
            unixMasterFd = -1;
            ProcessId = 0;
            hProcess = IntPtr.Zero;
            hThread = IntPtr.Zero;
            hPC = IntPtr.Zero;
            hPipeInWrite = IntPtr.Zero;
            hPipeOutRead = IntPtr.Zero;
            lpAttributeList = IntPtr.Zero;
            fallbackProcess = null;

            // Synchronously terminate the shell process and its entire descendant tree first
            try
            {
                if (!ConPtyNative.IsWindowsPlatform())
                {
                    if (oldUnixFd >= 0)
                    {
                        try { UnixPtyNative.close(oldUnixFd); } catch { }
                    }
                    if (oldPid > 0 && !wasExited)
                    {
                        try { UnixPtyNative.kill(-oldPid, UnixPtyNative.SIGHUP); } catch { }
                        try { UnixPtyNative.kill(oldPid, UnixPtyNative.SIGKILL); } catch { }
                    }
                }
                else
                {
                    if (oldPid > 0)
                    {
                        ConPtyNative.KillProcessTreeById(oldPid);
                    }
                    if (oldHProcess != IntPtr.Zero && !wasExited)
                    {
                        try { ConPtyNative.TerminateProcess(oldHProcess, 0); } catch { }
                    }
                }
            }
            catch { }

            try
            {
                if (oldFallback != null)
                {
                    if (!oldFallback.HasExited) oldFallback.Kill();
                    oldFallback.Dispose();
                }
            }
            catch { }

            if (ConPtyNative.IsWindowsPlatform())
            {
                ThreadPool.QueueUserWorkItem(_ =>
                {
                    try { if (oldHPC != IntPtr.Zero) ConPtyNative.ClosePseudoConsole(oldHPC); } catch { }
                    try { if (oldWriteStream != null) oldWriteStream.Dispose(); } catch { }
                    try { if (oldReadStream != null) oldReadStream.Dispose(); } catch { }
                    try { if (oldPipeIn != IntPtr.Zero) ConPtyNative.CloseHandle(oldPipeIn); } catch { }
                    try { if (oldPipeOut != IntPtr.Zero) ConPtyNative.CloseHandle(oldPipeOut); } catch { }
                    try { if (oldHThread != IntPtr.Zero) ConPtyNative.CloseHandle(oldHThread); } catch { }
                    try { if (oldHProcess != IntPtr.Zero) ConPtyNative.CloseHandle(oldHProcess); } catch { }
                    try
                    {
                        if (oldAttr != IntPtr.Zero)
                        {
                            ConPtyNative.DeleteProcThreadAttributeList(oldAttr);
                            Marshal.FreeHGlobal(oldAttr);
                        }
                    }
                    catch { }
                });
            }
        }

        public void KillProcessTree()
        {
            if (isDisposed) return;
            isDisposed = true;
            CleanupCurrentProcessInternal();
        }

        public void Dispose()
        {
            KillProcessTree();
        }
    }
#endif

#if CODELESS_WINFORMS_UI
    // =========================================================================
    // 1. MODERN VISTA-STYLE IFileOpenDialog FOLDER PICKER (WITH FALLBACK)
    // =========================================================================
    public static class VistaFolderPicker
    {
        [ComImport]
        [Guid("DC1C5A9C-E88A-4dde-A5A1-60F82A20AEF7")]
        private class FileOpenDialogRCW { }

        [ComImport]
        [Guid("d57c7288-d4ad-4768-be02-9d969532d960")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IFileOpenDialog
        {
            [PreserveSig] int Show([In] IntPtr parent);
            void SetFileTypes([In] uint cFileTypes, [In] IntPtr rgFilterSpec);
            void SetFileTypeIndex([In] uint iFileType);
            void GetFileTypeIndex(out uint piFileType);
            void Advise([In, MarshalAs(UnmanagedType.Interface)] object pfde, out uint pdwCookie);
            void Unadvise([In] uint dwCookie);
            void SetOptions([In] uint fos);
            void GetOptions(out uint pfos);
            void SetDefaultFolder([In, MarshalAs(UnmanagedType.Interface)] IShellItem psi);
            void SetFolder([In, MarshalAs(UnmanagedType.Interface)] IShellItem psi);
            void GetFolder([MarshalAs(UnmanagedType.Interface)] out IShellItem ppsi);
            void GetCurrentSelection([MarshalAs(UnmanagedType.Interface)] out IShellItem ppsi);
            void SetFileName([In, MarshalAs(UnmanagedType.LPWStr)] string pszName);
            void GetFileName([MarshalAs(UnmanagedType.LPWStr)] out string pszName);
            void SetTitle([In, MarshalAs(UnmanagedType.LPWStr)] string pszTitle);
            void SetOkButtonLabel([In, MarshalAs(UnmanagedType.LPWStr)] string pszText);
            void SetFileNameLabel([In, MarshalAs(UnmanagedType.LPWStr)] string pszLabel);
            void GetResult([MarshalAs(UnmanagedType.Interface)] out IShellItem ppsi);
        }

        [ComImport]
        [Guid("43826D1E-E718-42EE-BC55-A1E261C37BFE")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IShellItem
        {
            void BindToHandler([In] IntPtr pbc, [In] ref Guid bhid, [In] ref Guid riid, out IntPtr ppv);
            void GetParent([MarshalAs(UnmanagedType.Interface)] out IShellItem ppsi);
            void GetDisplayName([In] uint sigdnName, [MarshalAs(UnmanagedType.LPWStr)] out string ppszName);
            void GetAttributes([In] uint sfgaoMask, out uint psfgaoAttribs);
            void Compare([In, MarshalAs(UnmanagedType.Interface)] IShellItem psi, [In] uint hint, out int piOrder);
        }

        [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = true)]
        private static extern int SHCreateItemFromParsingName(
            [In, MarshalAs(UnmanagedType.LPWStr)] string pszPath,
            [In] IntPtr pbc,
            [In] ref Guid riid,
            [MarshalAs(UnmanagedType.Interface)] out IShellItem ppv);

        private const uint FOS_PICKFOLDERS = 0x00000020;
        private const uint FOS_FORCEFILESYSTEM = 0x00000040;
        private const uint FOS_PATHMUSTEXIST = 0x00000800;
        private const uint SIGDN_FILESYSPATH = 0x80058000;

        private class HwndWrapper : IWin32Window
        {
            private readonly IntPtr handle;
            public HwndWrapper(IntPtr h) { handle = h; }
            public IntPtr Handle { get { return handle; } }
        }

        public static string ShowDialog(IntPtr ownerHandle, string title, string initialDirectory)
        {
            return ShowFolderDialog(ownerHandle != IntPtr.Zero ? new HwndWrapper(ownerHandle) : null, title, initialDirectory);
        }

        public static string ShowFolderDialog(IWin32Window owner, string title, string initialDirectory)
        {
            try
            {
                IFileOpenDialog dialog = (IFileOpenDialog)new FileOpenDialogRCW();
                uint options;
                dialog.GetOptions(out options);
                dialog.SetOptions(options | FOS_PICKFOLDERS | FOS_FORCEFILESYSTEM | FOS_PATHMUSTEXIST);
                if (!string.IsNullOrEmpty(title))
                    dialog.SetTitle(title);

                if (!string.IsNullOrEmpty(initialDirectory) && Directory.Exists(initialDirectory))
                {
                    Guid shellItemGuid = new Guid("43826D1E-E718-42EE-BC55-A1E261C37BFE");
                    IShellItem folderItem;
                    if (SHCreateItemFromParsingName(initialDirectory, IntPtr.Zero, ref shellItemGuid, out folderItem) == 0 && folderItem != null)
                    {
                        dialog.SetFolder(folderItem);
                    }
                }

                IntPtr hwnd = owner != null ? owner.Handle : IntPtr.Zero;
                int hr = dialog.Show(hwnd);
                if (hr == 0)
                {
                    IShellItem resultItem;
                    dialog.GetResult(out resultItem);
                    if (resultItem != null)
                    {
                        string selectedPath;
                        resultItem.GetDisplayName(SIGDN_FILESYSPATH, out selectedPath);
                        return selectedPath;
                    }
                }
                else
                {
                    // Cancelled by user (ERROR_CANCELLED = 0x800704C7)
                    return null;
                }
            }
            catch
            {
                using (FolderBrowserDialog fbd = new FolderBrowserDialog())
                {
                    fbd.Description = title ?? "Select Workspace Folder";
                    if (!string.IsNullOrEmpty(initialDirectory) && Directory.Exists(initialDirectory))
                        fbd.SelectedPath = initialDirectory;
                    if (fbd.ShowDialog(owner) == DialogResult.OK)
                        return fbd.SelectedPath;
                }
            }
            return null;
        }
    }

    // =========================================================================
    // 5. VS CODE INTEGRATED TERMINAL EMULATOR CONTROL (DIRECT CANVAS INPUT)
    // =========================================================================
    public class CodeLessTerminalEmulatorControl : Control
    {
        private ConPtyTerminalSession session;
        private StudioTheme theme;
        private Font regularFont;
        private Font boldFont;
        private Font italicFont;
        private Font underlineFont;
        private int cellWidth = 8;
        private int cellHeight = 18;
        private int padLeft = 10;
        private int padTop = 6;

        // Scrollback offset (0 = pinned to bottom live screen; >0 = scrolled up N lines)
        private int scrollbackOffset = 0;
        private int wheelDeltaRemainder = 0;
        private int repaintQueued = 0;
        private int activeMouseButtonCode = -1;

        // Selection state (in virtual row/col coordinates)
        private bool isSelecting = false;
        private bool hasSelection = false;
        private int selStartVRow, selStartCol;
        private int selEndVRow, selEndCol;

        // Blinking cursor timer
        private System.Windows.Forms.Timer blinkTimer;
        private bool cursorBlinkOn = true;
        public bool UseBarCursor = false;

        // Hover scrollbar & clickable link state
        private bool isMouseInside = false;
        private bool isDraggingScrollbar = false;
        private int hoveredVRow = -1;
        private int hoveredLinkStartCol = -1;
        private int hoveredLinkEndCol = -1;
        private string hoveredLinkTarget = null;

        // Inline Ctrl+K Terminal Command Assistant overlay
        private Panel ctrlKOverlay;
        private CodeLessDarkInputBox ctrlKInput;
        private Label ctrlKHintLbl;
        private bool ctrlKNoProviderState = false;
        public Func<string, System.Threading.Tasks.Task<string>> GenerateShellCommandCallback;
        public Func<bool> IsAiProviderConfiguredCallback;
        public Action OpenAiSettingsCallback;

        // Events
        public event Action<string, int> FileLinkClicked;
        public event Action RequestSplitTerminal;
        public event Action RequestNewTerminal;
        public event Action RequestKillTerminal;
        public event Action<int> SwitchTerminalDeltaRequested;

        private ContextMenuStrip terminalContextMenu;

        private static readonly Regex FileLineLinkRegex = new Regex(
            @"((?:[A-Za-z]:[\\/]|[\w\.\-_]+[\\/])[\w\.\-\\/]+\.[A-Za-z0-9]+)(?:[:\(](\d+))?",
            RegexOptions.Compiled);
        private static readonly Regex UrlLinkRegex = new Regex(
            @"https?://[^\s""'<>]+",
            RegexOptions.Compiled);

        public CodeLessTerminalEmulatorControl()
        {
            this.SetStyle(
                ControlStyles.AllPaintingInWmPaint |
                ControlStyles.UserPaint |
                ControlStyles.OptimizedDoubleBuffer |
                ControlStyles.ResizeRedraw |
                ControlStyles.Selectable,
                true);
            this.TabStop = true;
            this.Cursor = Cursors.IBeam;
            this.BackColor = Color.FromArgb(24, 24, 27);
            this.ForeColor = Color.FromArgb(204, 204, 204);

            SetTerminalFont("Cascadia Mono", 9.8f);

            blinkTimer = new System.Windows.Forms.Timer { Interval = 530 };
            blinkTimer.Tick += (s, e) =>
            {
                if (this.Focused)
                {
                    cursorBlinkOn = !cursorBlinkOn;
                    this.Invalidate();
                }
            };
            blinkTimer.Start();

            BuildContextMenu();
            BuildCtrlKOverlay();
        }

        public ConPtyTerminalSession ActiveSession
        {
            get { return Session; }
            set { Session = value; }
        }

        public ConPtyTerminalSession Session
        {
            get { return session; }
            set
            {
                if (session != null)
                {
                    session.OutputReceived -= OnSessionOutputReceived;
                }
                session = value;
                scrollbackOffset = 0;
                hasSelection = false;
                if (session != null)
                {
                    session.OutputReceived += OnSessionOutputReceived;
                    if (this.theme != null && session.Buffer != null)
                    {
                        bool isLight = (this.theme.IsLight || this.theme.BgEditor.GetBrightness() > 0.5f);
                        this.BackColor = isLight ? Color.FromArgb(255, 255, 255) : this.theme.BgWorkbench;
                        this.ForeColor = isLight ? Color.FromArgb(59, 59, 59) : this.theme.FgPrimary;
                        session.Buffer.ApplyThemePalette(isLight, this.BackColor, this.ForeColor);
                    }
                    else if (session.Buffer != null)
                    {
                        this.BackColor = session.Buffer.DefaultBg;
                        this.ForeColor = session.Buffer.DefaultFg;
                    }
                    RecalculateGridDimensions();
                }
                this.Invalidate();
            }
        }

        // Backwards-compatible Text property returning full terminal transcript
        public override string Text
        {
            get
            {
                if (session != null && session.Buffer != null)
                    return session.Buffer.GetScrollbackAndScreenText(300);
                return base.Text;
            }
            set
            {
                base.Text = value;
                if (session != null && session.Buffer != null && !string.IsNullOrEmpty(value))
                {
                    session.Buffer.ClearScrollbackAndScreen();
                    session.Buffer.ProcessText(value.Replace("\n", "\r\n"));
                    this.Invalidate();
                }
            }
        }

        public void ClearTerminal()
        {
            if (session != null && session.Buffer != null)
            {
                session.Buffer.ClearScrollbackAndScreen();
                scrollbackOffset = 0;
                hasSelection = false;
                if (!session.HasExited)
                {
                    string low = ((session.ShellName ?? "") + " " + (session.CommandLine ?? "")).ToLowerInvariant();
                    if (session.IsIdleAtPrompt() && !low.Contains("python"))
                    {
                        if (low.Contains("bash") || low.Contains("wsl") || low.Contains("git"))
                            session.WriteInput("clear\r");
                        else
                            session.WriteInput("cls\r");
                    }
                    else
                    {
                        session.WriteInput("\r");
                    }
                }
                this.Invalidate();
            }
        }

        public void SetTerminalFont(string preferredFamily, float ptSize)
        {
            string family = "Consolas";
            try
            {
                using (Font test = new Font(preferredFamily, ptSize))
                {
                    if (string.Equals(test.Name, preferredFamily, StringComparison.OrdinalIgnoreCase))
                        family = preferredFamily;
                }
            }
            catch { }

            if (regularFont != null) regularFont.Dispose();
            if (boldFont != null) boldFont.Dispose();
            if (italicFont != null) italicFont.Dispose();
            if (underlineFont != null) underlineFont.Dispose();

            regularFont = new Font(family, ptSize, FontStyle.Regular);
            boldFont = new Font(family, ptSize, FontStyle.Bold);
            italicFont = new Font(family, ptSize, FontStyle.Italic);
            underlineFont = new Font(family, ptSize, FontStyle.Underline);

            Size s100 = TextRenderer.MeasureText(new string('W', 100), regularFont, new Size(4000, 200), TextFormatFlags.NoPadding);
            Size s1 = TextRenderer.MeasureText("W", regularFont, new Size(4000, 200), TextFormatFlags.NoPadding);
            cellWidth = Math.Max(6, (int)Math.Round((s100.Width - s1.Width) / 99.0));
            cellHeight = Math.Max(14, s1.Height + 2);
            RecalculateGridDimensions();
        }

        public void ApplyTheme(StudioTheme t)
        {
            this.theme = t;
            bool isLight = (t != null && (t.IsLight || t.BgEditor.GetBrightness() > 0.5f));
            this.BackColor = isLight ? Color.FromArgb(255, 255, 255) : (t != null ? t.BgWorkbench : Color.FromArgb(24, 24, 27));
            this.ForeColor = isLight ? Color.FromArgb(59, 59, 59) : (t != null ? t.FgPrimary : Color.FromArgb(204, 204, 204));

            if (session != null && session.Buffer != null)
            {
                session.Buffer.ApplyThemePalette(isLight, this.BackColor, this.ForeColor);
            }

            if (ctrlKOverlay != null && t != null)
            {
                ctrlKOverlay.BackColor = t.BgSidebar;
                if (ctrlKInput != null) ctrlKInput.ApplyTheme(t);
                if (ctrlKHintLbl != null) ctrlKHintLbl.ForeColor = t.FgSecondary;
            }

            if (terminalContextMenu != null && t != null)
            {
                terminalContextMenu.Renderer = new DarkStudioMenuRenderer(() => this.theme);
                terminalContextMenu.BackColor = t.BgSidebar;
                terminalContextMenu.ForeColor = t.FgPrimary;
            }

            this.Invalidate();
        }

        private void OnSessionOutputReceived()
        {
            if (this.IsDisposed || !this.IsHandleCreated) return;
            if (Interlocked.Exchange(ref repaintQueued, 1) != 0) return;
            try
            {
                this.BeginInvoke(new Action(() =>
                {
                    Interlocked.Exchange(ref repaintQueued, 0);
                    if (!this.IsDisposed)
                        this.Invalidate();
                }));
            }
            catch
            {
                Interlocked.Exchange(ref repaintQueued, 0);
            }
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            RecalculateGridDimensions();
            if (ctrlKOverlay != null && ctrlKOverlay.Visible)
            {
                ctrlKOverlay.Width = Math.Min(540, Math.Max(280, this.ClientSize.Width - 28));
            }
        }

        private void RecalculateGridDimensions()
        {
            if (session == null || this.ClientSize.Width <= 40 || this.ClientSize.Height <= 30) return;
            int availW = Math.Max(80, this.ClientSize.Width - padLeft - 14);
            int availH = Math.Max(40, this.ClientSize.Height - padTop * 2);
            int cols = Math.Max(20, availW / Math.Max(1, cellWidth));
            int rows = Math.Max(5, availH / Math.Max(1, cellHeight));
            session.Resize(cols, rows);
        }

        private void BuildContextMenu()
        {
            terminalContextMenu = new ContextMenuStrip { ShowImageMargin = true };
            ToolStripMenuItem copyItem = new ToolStripMenuItem("Copy", null, (s, e) => CopySelectionToClipboard()) { ShortcutKeyDisplayString = "Ctrl+C" };
            ToolStripMenuItem pasteItem = new ToolStripMenuItem("Paste", null, (s, e) => PasteFromClipboard()) { ShortcutKeyDisplayString = "Ctrl+V" };
            ToolStripMenuItem selectAllItem = new ToolStripMenuItem("Select All", null, (s, e) => SelectAllTerminalText());
            ToolStripMenuItem clearItem = new ToolStripMenuItem("Clear Terminal", null, (s, e) => ClearTerminal());
            ToolStripMenuItem ctrlKItem = new ToolStripMenuItem("Generate Shell Command with AI...", null, (s, e) => ShowCtrlKPrompt()) { ShortcutKeyDisplayString = "Ctrl+K" };
            ToolStripMenuItem splitItem = new ToolStripMenuItem("Split Terminal", null, (s, e) => { if (RequestSplitTerminal != null) RequestSplitTerminal(); }) { ShortcutKeyDisplayString = "Ctrl+Shift+5" };
            ToolStripMenuItem newTermItem = new ToolStripMenuItem("New Terminal", null, (s, e) => { if (RequestNewTerminal != null) RequestNewTerminal(); }) { ShortcutKeyDisplayString = "Ctrl+Shift+`" };
            ToolStripMenuItem killItem = new ToolStripMenuItem("Kill Terminal", null, (s, e) => { if (RequestKillTerminal != null) RequestKillTerminal(); });

            terminalContextMenu.Items.AddRange(new ToolStripItem[]
            {
                copyItem,
                pasteItem,
                selectAllItem,
                new ToolStripSeparator(),
                ctrlKItem,
                clearItem,
                new ToolStripSeparator(),
                newTermItem,
                splitItem,
                killItem
            });
        }

        private void BuildCtrlKOverlay()
        {
            ctrlKOverlay = new Panel
            {
                Location = new Point(14, 10),
                Size = new Size(480, 62),
                BackColor = Color.FromArgb(30, 30, 36),
                Visible = false,
                Padding = new Padding(10, 6, 10, 6)
            };
            ctrlKOverlay.Paint += (s, e) =>
            {
                Color accent = theme != null ? theme.AccentPrimary : Color.FromArgb(0, 122, 204);
                using (Pen p = new Pen(accent, 1.5f))
                    e.Graphics.DrawRectangle(p, 0, 0, ctrlKOverlay.Width - 1, ctrlKOverlay.Height - 1);
                CodeLessVectorIcons.DrawIcon(e.Graphics, "agent", new Rectangle(8, 5, 14, 14), accent, 1.5f);
            };

            ctrlKHintLbl = new Label
            {
                Location = new Point(25, 5),
                Size = new Size(440, 16),
                Text = "Terminal AI Command (Enter to insert command, Ctrl+Enter to run, Esc to cancel)",
                Font = new Font("Segoe UI Semibold", 8.2f, FontStyle.Bold),
                ForeColor = Color.FromArgb(160, 160, 175),
                UseMnemonic = false
            };

            ctrlKInput = new CodeLessDarkInputBox
            {
                Location = new Point(10, 25),
                Size = new Size(456, 28),
                Placeholder = "Describe shell command (e.g. 'find all .cs files modified today' or 'git undo last commit')...",
                Font = new Font("Segoe UI", 9.0f),
                Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right
            };

            ctrlKInput.KeyDown += async (s, e) =>
            {
                if (e.KeyCode == Keys.Escape)
                {
                    e.SuppressKeyPress = true;
                    ctrlKNoProviderState = false;
                    ctrlKOverlay.Visible = false;
                    this.Focus();
                }
                else if (e.KeyCode == Keys.Enter)
                {
                    e.SuppressKeyPress = true;
                    if (ctrlKNoProviderState)
                    {
                        ctrlKNoProviderState = false;
                        ctrlKOverlay.Visible = false;
                        this.Focus();
                        if (OpenAiSettingsCallback != null) OpenAiSettingsCallback();
                        return;
                    }
                    bool autoRun = e.Control;
                    string prompt = ctrlKInput.Text.Trim();
                    if (string.IsNullOrEmpty(prompt)) return;

                    bool configured = (IsAiProviderConfiguredCallback != null)
                        ? IsAiProviderConfiguredCallback()
                        : (GenerateShellCommandCallback != null);
                    if (!configured || GenerateShellCommandCallback == null)
                    {
                        ctrlKNoProviderState = true;
                        ctrlKHintLbl.Text = "No AI provider configured — press Enter to configure in AI Settings (Esc to close)";
                        ctrlKHintLbl.ForeColor = Color.FromArgb(248, 113, 113);
                        return;
                    }

                    ctrlKHintLbl.Text = "Generating shell command...";
                    string generatedCmd = null;
                    try { generatedCmd = await GenerateShellCommandCallback(prompt); } catch { }

                    if (string.IsNullOrWhiteSpace(generatedCmd) || generatedCmd.StartsWith("NO_AI_PROVIDER", StringComparison.OrdinalIgnoreCase))
                    {
                        ctrlKNoProviderState = true;
                        ctrlKHintLbl.Text = "No AI provider configured — press Enter to configure in AI Settings (Esc to close)";
                        ctrlKHintLbl.ForeColor = Color.FromArgb(248, 113, 113);
                        return;
                    }

                    ctrlKOverlay.Visible = false;
                    ctrlKHintLbl.Text = "Terminal AI Command (Enter to insert command, Ctrl+Enter to run, Esc to cancel)";
                    this.Focus();

                    if (session != null && !string.IsNullOrEmpty(generatedCmd))
                    {
                        if (autoRun) session.SendCommandLine(generatedCmd);
                        else session.WriteInput(generatedCmd);
                    }
                }
            };

            ctrlKOverlay.Controls.Add(ctrlKHintLbl);
            ctrlKOverlay.Controls.Add(ctrlKInput);
            this.Controls.Add(ctrlKOverlay);
        }

        public void ShowCtrlKPrompt()
        {
            ctrlKNoProviderState = false;
            if (ctrlKHintLbl != null)
            {
                ctrlKHintLbl.Text = "Terminal AI Command (Enter to insert command, Ctrl+Enter to run, Esc to cancel)";
                ctrlKHintLbl.ForeColor = theme != null ? theme.FgSecondary : Color.FromArgb(160, 160, 175);
            }
            ctrlKOverlay.Width = Math.Min(540, Math.Max(280, this.ClientSize.Width - 28));
            ctrlKOverlay.Visible = true;
            ctrlKOverlay.BringToFront();
            ctrlKInput.Text = "";
            ctrlKInput.Focus();
        }

        public void ShowInlineCtrlKBar()
        {
            ShowCtrlKPrompt();
        }

        public void HideInlineCtrlKBar()
        {
            if (ctrlKOverlay != null) ctrlKOverlay.Visible = false;
        }

        public bool IsInlineCtrlKVisible
        {
            get { return ctrlKOverlay != null && ctrlKOverlay.Visible; }
        }

        private static string SynthesizeLocalShellCommand(string naturalPrompt)
        {
            return "";
        }

        protected override void OnGotFocus(EventArgs e)
        {
            base.OnGotFocus(e);
            cursorBlinkOn = true;
            this.Invalidate();
        }

        protected override void OnLostFocus(EventArgs e)
        {
            base.OnLostFocus(e);
            this.Invalidate();
        }

        protected override bool IsInputKey(Keys keyData)
        {
            Keys k = keyData & Keys.KeyCode;
            if (k == Keys.Tab || k == Keys.Up || k == Keys.Down || k == Keys.Left || k == Keys.Right ||
                k == Keys.Home || k == Keys.End || k == Keys.PageUp || k == Keys.PageDown || k == Keys.Escape)
            {
                return true;
            }
            return base.IsInputKey(keyData);
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            if (session == null) return;

            cursorBlinkOn = true;

            // Ctrl+K inside terminal opens floating shell command generator
            if (e.Control && !e.Shift && !e.Alt && e.KeyCode == Keys.K)
            {
                e.SuppressKeyPress = true;
                e.Handled = true;
                ShowCtrlKPrompt();
                return;
            }

            // Ctrl+PageUp / Ctrl+PageDown: Switch between terminal sessions (VS Code parity)
            if (e.Control && !e.Shift && !e.Alt && e.KeyCode == Keys.PageUp)
            {
                e.SuppressKeyPress = true;
                e.Handled = true;
                if (SwitchTerminalDeltaRequested != null) SwitchTerminalDeltaRequested(-1);
                return;
            }
            if (e.Control && !e.Shift && !e.Alt && e.KeyCode == Keys.PageDown)
            {
                e.SuppressKeyPress = true;
                e.Handled = true;
                if (SwitchTerminalDeltaRequested != null) SwitchTerminalDeltaRequested(1);
                return;
            }

            // Ctrl+Shift+5: Split Terminal
            if (e.Control && e.Shift && !e.Alt && e.KeyCode == Keys.D5)
            {
                e.SuppressKeyPress = true;
                e.Handled = true;
                if (RequestSplitTerminal != null) RequestSplitTerminal();
                return;
            }

            // Ctrl+Shift+C: Explicit copy selection
            if (e.Control && e.Shift && !e.Alt && e.KeyCode == Keys.C)
            {
                e.SuppressKeyPress = true;
                e.Handled = true;
                CopySelectionToClipboard();
                return;
            }

            // Ctrl+Shift+V: Explicit paste from clipboard
            if (e.Control && e.Shift && !e.Alt && e.KeyCode == Keys.V)
            {
                e.SuppressKeyPress = true;
                e.Handled = true;
                PasteFromClipboard();
                return;
            }

            // Ctrl+Home / Ctrl+End: Scroll to top / bottom of scrollback
            if (e.Control && !e.Shift && !e.Alt && e.KeyCode == Keys.Home)
            {
                e.SuppressKeyPress = true;
                e.Handled = true;
                if (session.Buffer != null)
                {
                    scrollbackOffset = session.Buffer.ScrollbackCount;
                    this.Invalidate();
                }
                return;
            }
            if (e.Control && !e.Shift && !e.Alt && e.KeyCode == Keys.End)
            {
                e.SuppressKeyPress = true;
                e.Handled = true;
                scrollbackOffset = 0;
                this.Invalidate();
                return;
            }

            // Ctrl+C: Copy if selection exists, else send ^C (\x03)
            if (e.Control && !e.Alt && e.KeyCode == Keys.C)
            {
                e.SuppressKeyPress = true;
                e.Handled = true;
                if (hasSelection)
                {
                    CopySelectionToClipboard();
                    hasSelection = false;
                    this.Invalidate();
                }
                else
                {
                    session.WriteInput("\x03");
                }
                return;
            }

            // Ctrl+V or Shift+Insert: Paste from clipboard
            if ((e.Control && !e.Alt && e.KeyCode == Keys.V) || (e.Shift && e.KeyCode == Keys.Insert))
            {
                e.SuppressKeyPress = true;
                e.Handled = true;
                PasteFromClipboard();
                return;
            }

            // Shift+PageUp / Shift+PageDown: Scrollback navigation (or forward PageUp/PageDown in Alternate Screen TUIs like opencode)
            if (e.Shift && e.KeyCode == Keys.PageUp)
            {
                e.SuppressKeyPress = true;
                e.Handled = true;
                if (session.Buffer != null && session.Buffer.IsAlternateScreen)
                {
                    string seq = session.Buffer.EncodeMouseWheelSequence(true, Math.Max(1, session.Buffer.Cols / 2), Math.Max(1, session.Buffer.Rows / 2), false, e.Alt, e.Control);
                    if (seq != null && session.Buffer.MouseTrackingMode != 0)
                    {
                        for (int i = 0; i < 5; i++) session.WriteInput(seq);
                    }
                    else
                    {
                        session.WriteInput("\x1b[5~");
                    }
                }
                else if (session.Buffer != null)
                {
                    ScrollByLines(Math.Max(1, session.Buffer.Rows - 2));
                }
                return;
            }
            if (e.Shift && e.KeyCode == Keys.PageDown)
            {
                e.SuppressKeyPress = true;
                e.Handled = true;
                if (session.Buffer != null && session.Buffer.IsAlternateScreen)
                {
                    string seq = session.Buffer.EncodeMouseWheelSequence(false, Math.Max(1, session.Buffer.Cols / 2), Math.Max(1, session.Buffer.Rows / 2), false, e.Alt, e.Control);
                    if (seq != null && session.Buffer.MouseTrackingMode != 0)
                    {
                        for (int i = 0; i < 5; i++) session.WriteInput(seq);
                    }
                    else
                    {
                        session.WriteInput("\x1b[6~");
                    }
                }
                else if (session.Buffer != null)
                {
                    ScrollByLines(-Math.Max(1, session.Buffer.Rows - 2));
                }
                return;
            }

            // Let IDE global shortcuts bubble up (Ctrl+`, Ctrl+Shift+`, Ctrl+P, Ctrl+Shift+P, Ctrl+B, Ctrl+J, F1, F11)
            if ((e.Control && e.KeyCode == Keys.Oemtilde) ||
                (e.Control && e.KeyCode == Keys.P) ||
                (e.Control && !e.Shift && !e.Alt && (e.KeyCode == Keys.B || e.KeyCode == Keys.J)) ||
                e.KeyCode == Keys.F11)
            {
                return;
            }

            bool appCursor = session.Buffer != null && session.Buffer.ApplicationCursorKeys;
            string vtSeq = EncodeSpecialKeyToVt(e.KeyCode, e.Control, e.Shift, e.Alt, appCursor);
            if (vtSeq != null)
            {
                e.SuppressKeyPress = true;
                e.Handled = true;
                scrollbackOffset = 0;
                hasSelection = false;
                session.WriteInput(vtSeq);
                this.Invalidate();
            }
        }

        protected override void OnKeyPress(KeyPressEventArgs e)
        {
            base.OnKeyPress(e);
            if (session == null || e.Handled) return;

            // Ignore control characters already handled in OnKeyDown
            if (e.KeyChar == '\x03' || e.KeyChar == '\x16' || e.KeyChar == '\x0b')
            {
                e.Handled = true;
                return;
            }

            scrollbackOffset = 0;
            hasSelection = false;
            cursorBlinkOn = true;

            if (e.KeyChar == '\b')
            {
                session.WriteInput("\x7f");
            }
            else if (e.KeyChar == '\r' || e.KeyChar == '\n')
            {
                session.WriteInput("\r");
            }
            else
            {
                session.WriteInput(e.KeyChar.ToString());
            }
            e.Handled = true;
        }

        public static string EncodeSpecialKeyToVt(Keys key, bool ctrl, bool shift, bool alt, bool appCursor)
        {
            switch (key)
            {
                case Keys.Up:
                    return ctrl ? "\x1b[1;5A" : (appCursor ? "\x1bOA" : "\x1b[A");
                case Keys.Down:
                    return ctrl ? "\x1b[1;5B" : (appCursor ? "\x1bOB" : "\x1b[B");
                case Keys.Right:
                    return ctrl ? "\x1b[1;5C" : (appCursor ? "\x1bOC" : "\x1b[C");
                case Keys.Left:
                    return ctrl ? "\x1b[1;5D" : (appCursor ? "\x1bOD" : "\x1b[D");
                case Keys.Home:
                    return ctrl ? "\x1b[1;5H" : (appCursor ? "\x1bOH" : "\x1b[H");
                case Keys.End:
                    return ctrl ? "\x1b[1;5F" : (appCursor ? "\x1bOF" : "\x1b[F");
                case Keys.Insert:
                    return "\x1b[2~";
                case Keys.Delete:
                    return ctrl ? "\x1b[3;5~" : "\x1b[3~";
                case Keys.PageUp:
                    return "\x1b[5~";
                case Keys.PageDown:
                    return "\x1b[6~";
                case Keys.Back:
                    return ctrl ? "\x17" : "\x7f";
                case Keys.Tab:
                    return shift ? "\x1b[Z" : "\t";
                case Keys.Return:
                    return "\r";
                case Keys.Escape:
                    return "\x1b";
                case Keys.F1: return "\x1bOP";
                case Keys.F2: return "\x1bOQ";
                case Keys.F3: return "\x1bOR";
                case Keys.F4: return "\x1bOS";
                case Keys.F5: return "\x1b[15~";
                case Keys.F6: return "\x1b[17~";
                case Keys.F7: return "\x1b[18~";
                case Keys.F8: return "\x1b[19~";
                case Keys.F9: return "\x1b[20~";
                case Keys.F10: return "\x1b[21~";
                case Keys.F11: return "\x1b[23~";
                case Keys.F12: return "\x1b[24~";
            }

            // Ctrl+Letter (A..Z except C, V, K handled above)
            if (ctrl && !alt && key >= Keys.A && key <= Keys.Z)
            {
                char ctrlChar = (char)(1 + (key - Keys.A));
                return ctrlChar.ToString();
            }

            // Alt+Letter sends ESC + char
            if (alt && !ctrl && key >= Keys.A && key <= Keys.Z)
            {
                char ch = shift ? (char)('A' + (key - Keys.A)) : (char)('a' + (key - Keys.A));
                return "\x1b" + ch;
            }

            return null;
        }

        public void CopySelectionToClipboard()
        {
            string sel = GetSelectedText();
            if (!string.IsNullOrEmpty(sel))
            {
                try { Clipboard.SetText(sel); } catch { }
            }
        }

        public void PasteFromClipboard()
        {
            if (session == null) return;
            try
            {
                if (Clipboard.ContainsText())
                {
                    string txt = Clipboard.GetText();
                    if (!string.IsNullOrEmpty(txt))
                    {
                        scrollbackOffset = 0;
                        if (session.Buffer != null && session.Buffer.BracketedPasteMode)
                            session.WriteInput("\x1b[200~" + txt + "\x1b[201~");
                        else
                            session.WriteInput(txt);
                    }
                }
            }
            catch { }
        }

        public void SelectAllTerminalText()
        {
            if (session == null || session.Buffer == null) return;
            selStartVRow = 0;
            selStartCol = 0;
            selEndVRow = Math.Max(0, session.Buffer.TotalVirtualRows - 1);
            selEndCol = Math.Max(0, session.Buffer.Cols - 1);
            hasSelection = true;
            this.Invalidate();
        }

        public string GetSelectedText()
        {
            if (!hasSelection || session == null || session.Buffer == null) return "";
            int r1 = selStartVRow, c1 = selStartCol;
            int r2 = selEndVRow, c2 = selEndCol;
            if (r1 > r2 || (r1 == r2 && c1 > c2))
            {
                int tr = r1; r1 = r2; r2 = tr;
                int tc = c1; c1 = c2; c2 = tc;
            }

            StringBuilder sb = new StringBuilder();
            for (int vr = r1; vr <= r2; vr++)
            {
                int startC = (vr == r1) ? c1 : 0;
                int endC = (vr == r2) ? c2 : (session.Buffer.Cols - 1);
                StringBuilder lineSb = new StringBuilder();
                for (int c = startC; c <= endC && c < session.Buffer.Cols; c++)
                {
                    char ch = session.Buffer.GetVirtualCell(vr, c).Char;
                    lineSb.Append(ch == '\0' ? ' ' : ch);
                }
                if (vr < r2)
                    sb.AppendLine(lineSb.ToString().TrimEnd());
                else
                    sb.Append(lineSb.ToString().TrimEnd());
            }
            return sb.ToString();
        }

        private void PointToVirtualCell(Point pt, out int vRow, out int col)
        {
            vRow = 0;
            col = 0;
            if (session == null || session.Buffer == null) return;
            int screenRow = Math.Max(0, Math.Min(session.Buffer.Rows - 1, (pt.Y - padTop) / Math.Max(1, cellHeight)));
            col = Math.Max(0, Math.Min(session.Buffer.Cols - 1, (pt.X - padLeft) / Math.Max(1, cellWidth)));
            int topVirtualRow = Math.Max(0, session.Buffer.TotalVirtualRows - session.Buffer.Rows - scrollbackOffset);
            vRow = topVirtualRow + screenRow;
        }

        protected override void OnMouseEnter(EventArgs e)
        {
            base.OnMouseEnter(e);
            isMouseInside = true;
            this.Invalidate();
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            isMouseInside = false;
            hoveredVRow = -1;
            hoveredLinkTarget = null;
            this.Invalidate();
        }

        private void PointToScreenOneBasedCell(Point pt, out int col1, out int row1)
        {
            col1 = 1;
            row1 = 1;
            if (session == null || session.Buffer == null) return;
            row1 = Math.Max(1, Math.Min(session.Buffer.Rows, ((pt.Y - padTop) / Math.Max(1, cellHeight)) + 1));
            col1 = Math.Max(1, Math.Min(session.Buffer.Cols, ((pt.X - padLeft) / Math.Max(1, cellWidth)) + 1));
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            this.Focus();
            if (session == null || session.Buffer == null) return;

            Keys mods = Control.ModifierKeys;
            bool shift = (mods & Keys.Shift) == Keys.Shift;
            bool alt = (mods & Keys.Alt) == Keys.Alt;
            bool ctrl = (mods & Keys.Control) == Keys.Control;

            // Forward mouse clicks to full-screen TUI apps (e.g. opencode) when MouseTrackingMode != 0 and Shift is not held
            if (session.Buffer.MouseTrackingMode != 0 && !shift && !hasSelection)
            {
                int btnCode = e.Button == MouseButtons.Left ? 0 : (e.Button == MouseButtons.Middle ? 1 : (e.Button == MouseButtons.Right ? 2 : -1));
                if (btnCode >= 0)
                {
                    int col1, row1;
                    PointToScreenOneBasedCell(e.Location, out col1, out row1);
                    string seq = session.Buffer.EncodeMouseButtonOrMoveSequence(btnCode, false, false, col1, row1, shift, alt, ctrl);
                    if (seq != null)
                    {
                        activeMouseButtonCode = btnCode;
                        session.WriteInput(seq);
                        return;
                    }
                }
            }

            if (e.Button == MouseButtons.Right)
            {
                if (hasSelection)
                {
                    CopySelectionToClipboard();
                    hasSelection = false;
                    this.Invalidate();
                }
                else
                {
                    terminalContextMenu.Show(this, e.Location);
                }
                return;
            }

            if (e.Button == MouseButtons.Left)
            {
                // Scrollbar click/drag check
                if (e.X >= this.ClientSize.Width - 12 && session.Buffer.ScrollbackCount > 0)
                {
                    isDraggingScrollbar = true;
                    UpdateScrollFromMouseY(e.Y);
                    return;
                }

                // Ctrl+Click on link
                if (ctrl && !string.IsNullOrEmpty(hoveredLinkTarget))
                {
                    OpenHoveredLink(hoveredLinkTarget);
                    return;
                }

                int vr, c;
                PointToVirtualCell(e.Location, out vr, out c);
                selStartVRow = vr;
                selStartCol = c;
                selEndVRow = vr;
                selEndCol = c;
                isSelecting = true;
                hasSelection = false;
                this.Invalidate();
            }
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            if (session == null || session.Buffer == null) return;

            if (isDraggingScrollbar)
            {
                UpdateScrollFromMouseY(e.Y);
                return;
            }

            Keys mods = Control.ModifierKeys;
            bool shift = (mods & Keys.Shift) == Keys.Shift;
            if (session.Buffer.MouseTrackingMode >= 1002 && !shift && !isSelecting)
            {
                int col1, row1;
                PointToScreenOneBasedCell(e.Location, out col1, out row1);
                string moveSeq = session.Buffer.EncodeMouseButtonOrMoveSequence(
                    activeMouseButtonCode,
                    false,
                    true,
                    col1,
                    row1,
                    shift,
                    (mods & Keys.Alt) == Keys.Alt,
                    (mods & Keys.Control) == Keys.Control);
                if (moveSeq != null)
                {
                    session.WriteInput(moveSeq);
                    return;
                }
            }

            int vr, c;
            PointToVirtualCell(e.Location, out vr, out c);

            if (isSelecting && e.Button == MouseButtons.Left)
            {
                selEndVRow = vr;
                selEndCol = c;
                hasSelection = (selStartVRow != selEndVRow || selStartCol != selEndCol);
                this.Invalidate();
                return;
            }

            // Detect clickable file:line or URL under cursor
            DetectHoveredLink(vr, c);
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            if (activeMouseButtonCode >= 0 && session != null && session.Buffer != null)
            {
                int releasedBtn = activeMouseButtonCode;
                activeMouseButtonCode = -1;
                Keys mods = Control.ModifierKeys;
                int col1, row1;
                PointToScreenOneBasedCell(e.Location, out col1, out row1);
                string relSeq = session.Buffer.EncodeMouseButtonOrMoveSequence(
                    releasedBtn,
                    true,
                    false,
                    col1,
                    row1,
                    (mods & Keys.Shift) == Keys.Shift,
                    (mods & Keys.Alt) == Keys.Alt,
                    (mods & Keys.Control) == Keys.Control);
                if (relSeq != null)
                {
                    session.WriteInput(relSeq);
                }
            }
            isSelecting = false;
            isDraggingScrollbar = false;
        }

        protected override void OnMouseDoubleClick(MouseEventArgs e)
        {
            base.OnMouseDoubleClick(e);
            if (session == null || session.Buffer == null || e.Button != MouseButtons.Left) return;

            int vr, c;
            PointToVirtualCell(e.Location, out vr, out c);
            string line = session.Buffer.GetVirtualLineText(vr);
            if (string.IsNullOrEmpty(line) || c >= line.Length) return;

            // If double-clicked on a file:line link, open it directly
            if (!string.IsNullOrEmpty(hoveredLinkTarget))
            {
                OpenHoveredLink(hoveredLinkTarget);
                return;
            }

            int start = c;
            int end = c;
            Func<char, bool> isWord = ch => char.IsLetterOrDigit(ch) || ch == '_' || ch == '.' || ch == '-' || ch == '/' || ch == '\\';
            while (start > 0 && isWord(line[start - 1])) start--;
            while (end + 1 < line.Length && isWord(line[end + 1])) end++;

            selStartVRow = vr;
            selStartCol = start;
            selEndVRow = vr;
            selEndCol = end;
            hasSelection = (end >= start);
            this.Invalidate();
        }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            base.OnMouseWheel(e);
            if (session == null || session.Buffer == null || e.Delta == 0) return;

            if ((wheelDeltaRemainder > 0 && e.Delta < 0) || (wheelDeltaRemainder < 0 && e.Delta > 0))
                wheelDeltaRemainder = 0;

            wheelDeltaRemainder += e.Delta;
            const int stepThreshold = 40; // 120 (1 standard notch) = 3 steps; smooth 1-step precision touchpad scrolling
            int steps = wheelDeltaRemainder / stepThreshold;
            if (steps == 0) return;
            wheelDeltaRemainder %= stepThreshold;

            bool wheelUp = steps > 0;
            int absSteps = Math.Min(12, Math.Abs(steps));

            Keys mods = Control.ModifierKeys;
            bool shift = (mods & Keys.Shift) == Keys.Shift;
            bool alt = (mods & Keys.Alt) == Keys.Alt;
            bool ctrl = (mods & Keys.Control) == Keys.Control;

            int col1, row1;
            PointToScreenOneBasedCell(e.Location, out col1, out row1);
            string vtWheel = session.Buffer.EncodeMouseWheelSequence(wheelUp, col1, row1, shift, alt, ctrl);
            if (vtWheel != null)
            {
                StringBuilder batch = new StringBuilder(vtWheel.Length * absSteps);
                for (int i = 0; i < absSteps; i++)
                    batch.Append(vtWheel);
                session.WriteInput(batch.ToString());
                return;
            }

            ScrollByLines(steps);
        }

        private void ScrollByLines(int deltaLines)
        {
            if (session == null || session.Buffer == null || session.Buffer.IsAlternateScreen) return;
            int maxScroll = session.Buffer.ScrollbackCount;
            scrollbackOffset = Math.Max(0, Math.Min(maxScroll, scrollbackOffset + deltaLines));
            this.Invalidate();
        }

        private void UpdateScrollFromMouseY(int mouseY)
        {
            if (session == null || session.Buffer == null) return;
            int maxScroll = session.Buffer.ScrollbackCount;
            if (maxScroll <= 0) return;
            double ratio = Math.Max(0.0, Math.Min(1.0, (double)mouseY / Math.Max(1, this.ClientSize.Height)));
            scrollbackOffset = (int)Math.Round((1.0 - ratio) * maxScroll);
            this.Invalidate();
        }

        private void DetectHoveredLink(int vr, int col)
        {
            string prevTarget = hoveredLinkTarget;
            hoveredVRow = -1;
            hoveredLinkStartCol = -1;
            hoveredLinkEndCol = -1;
            hoveredLinkTarget = null;

            string line = session.Buffer.GetVirtualLineText(vr);
            if (!string.IsNullOrEmpty(line))
            {
                foreach (Match m in UrlLinkRegex.Matches(line))
                {
                    if (col >= m.Index && col < m.Index + m.Length)
                    {
                        hoveredVRow = vr;
                        hoveredLinkStartCol = m.Index;
                        hoveredLinkEndCol = m.Index + m.Length - 1;
                        hoveredLinkTarget = m.Value;
                        break;
                    }
                }

                if (hoveredLinkTarget == null)
                {
                    foreach (Match m in FileLineLinkRegex.Matches(line))
                    {
                        if (col >= m.Index && col < m.Index + m.Length)
                        {
                            hoveredVRow = vr;
                            hoveredLinkStartCol = m.Index;
                            hoveredLinkEndCol = m.Index + m.Length - 1;
                            hoveredLinkTarget = m.Value;
                            break;
                        }
                    }
                }
            }

            this.Cursor = (hoveredLinkTarget != null) ? Cursors.Hand : Cursors.IBeam;
            if (prevTarget != hoveredLinkTarget)
                this.Invalidate();
        }

        private void OpenHoveredLink(string linkText)
        {
            if (string.IsNullOrEmpty(linkText)) return;
            if (linkText.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                linkText.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            {
                try { Process.Start(linkText); } catch { }
                return;
            }

            Match m = FileLineLinkRegex.Match(linkText);
            if (m.Success)
            {
                string pathPart = m.Groups[1].Value;
                int lineNum = 1;
                if (m.Groups[2].Success) int.TryParse(m.Groups[2].Value, out lineNum);
                if (FileLinkClicked != null)
                    FileLinkClicked(pathPart, lineNum);
            }
        }

        private bool IsCellSelected(int vRow, int col)
        {
            if (!hasSelection) return false;
            int r1 = selStartVRow, c1 = selStartCol;
            int r2 = selEndVRow, c2 = selEndCol;
            if (r1 > r2 || (r1 == r2 && c1 > c2))
            {
                int tr = r1; r1 = r2; r2 = tr;
                int tc = c1; c1 = c2; c2 = tc;
            }
            if (vRow < r1 || vRow > r2) return false;
            if (r1 == r2) return col >= c1 && col <= c2;
            if (vRow == r1) return col >= c1;
            if (vRow == r2) return col <= c2;
            return true;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
            bool isLight = (theme != null && (theme.IsLight || theme.BgEditor.GetBrightness() > 0.5f)) ||
                           (session != null && session.Buffer != null && session.Buffer.IsLightTheme) ||
                           this.BackColor.GetBrightness() > 0.5f;
            Color effBg = isLight ? Color.FromArgb(255, 255, 255) : this.BackColor;
            Color effFg = isLight ? Color.FromArgb(59, 59, 59) : this.ForeColor;
            g.Clear(effBg);

            if (session == null || session.Buffer == null) return;

            Vt100TerminalBuffer buf = session.Buffer;
            int rows = buf.Rows;
            int cols = buf.Cols;
            int totalVirtual = buf.TotalVirtualRows;
            int topVirtualRow = Math.Max(0, totalVirtual - rows - scrollbackOffset);

            Color selHighlightBg = (theme != null)
                ? StudioTheme.Blend(effBg, theme.AccentPrimary, isLight ? 0.25 : 0.38)
                : (isLight ? Color.FromArgb(173, 214, 255) : Color.FromArgb(38, 79, 120));

            StringBuilder runChars = new StringBuilder(cols);

            for (int sr = 0; sr < rows; sr++)
            {
                int vr = topVirtualRow + sr;
                int y = padTop + sr * cellHeight;

                // 1. Paint non-default cell backgrounds & selection highlights
                for (int c = 0; c < cols; c++)
                {
                    TermCell cell = buf.GetVirtualCell(vr, c);
                    bool sel = IsCellSelected(vr, c);
                    bool inv = (cell.Flags & TermCell.FLAG_INVERSE) != 0;
                    bool isDefBg = (cell.Flags & TermCell.FLAG_DEFAULT_BG) != 0 ||
                                   (cell.Bg.R == 0 && cell.Bg.G == 0 && cell.Bg.B == 0) ||
                                   (isLight && (cell.Bg == Color.FromArgb(24, 24, 27) || cell.Bg == Color.FromArgb(7, 8, 12)));
                    Color cellBg = inv
                        ? (((cell.Flags & TermCell.FLAG_DEFAULT_FG) != 0) ? effFg : cell.Fg)
                        : (isDefBg ? effBg : cell.Bg);

                    if (sel)
                    {
                        using (SolidBrush sb = new SolidBrush(selHighlightBg))
                            g.FillRectangle(sb, padLeft + c * cellWidth, y, cellWidth, cellHeight);
                    }
                    else if (cellBg.ToArgb() != effBg.ToArgb())
                    {
                        using (SolidBrush bb = new SolidBrush(cellBg))
                            g.FillRectangle(bb, padLeft + c * cellWidth, y, cellWidth, cellHeight);
                    }
                }

                // 2. Paint foreground text in style/color runs for crisp high-speed rendering
                int cIdx = 0;
                while (cIdx < cols)
                {
                    TermCell first = buf.GetVirtualCell(vr, cIdx);
                    char ch = first.Char == '\0' ? ' ' : first.Char;
                    if (ch == ' ' && (first.Flags & TermCell.FLAG_UNDERLINE) == 0)
                    {
                        cIdx++;
                        continue;
                    }

                    bool inv = (first.Flags & TermCell.FLAG_INVERSE) != 0;
                    Color runFg = inv
                        ? (((first.Flags & TermCell.FLAG_DEFAULT_BG) != 0) ? effBg : first.Bg)
                        : (((first.Flags & TermCell.FLAG_DEFAULT_FG) != 0) ? effFg : first.Fg);

                    if (isLight && !inv && runFg.GetBrightness() > 0.78f)
                        runFg = effFg;

                    if ((first.Flags & TermCell.FLAG_DIM) != 0)
                        runFg = StudioTheme.Blend(runFg, effBg, 0.40);

                    byte styleMask = (byte)(first.Flags & (TermCell.FLAG_BOLD | TermCell.FLAG_ITALIC | TermCell.FLAG_UNDERLINE));
                    int runStartCol = cIdx;
                    runChars.Length = 0;
                    runChars.Append(ch);
                    cIdx++;

                    while (cIdx < cols)
                    {
                        TermCell next = buf.GetVirtualCell(vr, cIdx);
                        bool nextInv = (next.Flags & TermCell.FLAG_INVERSE) != 0;
                        Color nextFg = nextInv
                            ? (((next.Flags & TermCell.FLAG_DEFAULT_BG) != 0) ? effBg : next.Bg)
                            : (((next.Flags & TermCell.FLAG_DEFAULT_FG) != 0) ? effFg : next.Fg);
                        if (isLight && !nextInv && nextFg.GetBrightness() > 0.78f)
                            nextFg = effFg;
                        if ((next.Flags & TermCell.FLAG_DIM) != 0)
                            nextFg = StudioTheme.Blend(nextFg, effBg, 0.40);
                        byte nextStyle = (byte)(next.Flags & (TermCell.FLAG_BOLD | TermCell.FLAG_ITALIC | TermCell.FLAG_UNDERLINE));

                        if (nextFg.ToArgb() != runFg.ToArgb() || nextStyle != styleMask)
                            break;

                        char nch = next.Char == '\0' ? ' ' : next.Char;
                        runChars.Append(nch);
                        cIdx++;
                    }

                    Font runFont = regularFont;
                    if ((styleMask & TermCell.FLAG_BOLD) != 0) runFont = boldFont;
                    else if ((styleMask & TermCell.FLAG_ITALIC) != 0) runFont = italicFont;
                    else if ((styleMask & TermCell.FLAG_UNDERLINE) != 0) runFont = underlineFont;

                    string sRun = runChars.ToString().TrimEnd(' ');
                    if (sRun.Length > 0)
                    {
                        TextRenderer.DrawText(
                            g,
                            sRun,
                            runFont,
                            new Point(padLeft + runStartCol * cellWidth, y),
                            runFg,
                            TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix);
                    }

                    if ((styleMask & TermCell.FLAG_UNDERLINE) != 0)
                    {
                        using (Pen up = new Pen(runFg, 1f))
                        {
                            int ux1 = padLeft + runStartCol * cellWidth;
                            int ux2 = ux1 + sRun.Length * cellWidth;
                            g.DrawLine(up, ux1, y + cellHeight - 2, ux2, y + cellHeight - 2);
                        }
                    }
                }

                // 3. Hovered link underline
                if (vr == hoveredVRow && hoveredLinkStartCol >= 0 && hoveredLinkEndCol >= hoveredLinkStartCol)
                {
                    Color linkCol = theme != null ? theme.AccentPrimary : Color.FromArgb(56, 189, 248);
                    using (Pen lp = new Pen(linkCol, 1f))
                    {
                        int lx1 = padLeft + hoveredLinkStartCol * cellWidth;
                        int lx2 = padLeft + (hoveredLinkEndCol + 1) * cellWidth;
                        g.DrawLine(lp, lx1, y + cellHeight - 2, lx2, y + cellHeight - 2);
                    }
                }
            }

            // 4. Paint Cursor (if viewing live screen and cursor is visible)
            if (scrollbackOffset == 0 && buf.CursorVisible && !session.HasExited)
            {
                int cx = padLeft + buf.CursorCol * cellWidth;
                int cy = padTop + buf.CursorRow * cellHeight;
                Color curColor = isLight
                    ? (theme != null ? theme.AccentPrimary : Color.FromArgb(0, 95, 184))
                    : (theme != null ? theme.AccentPrimary : Color.FromArgb(229, 229, 229));

                if (this.Focused)
                {
                    if (cursorBlinkOn)
                    {
                        if (UseBarCursor)
                        {
                            using (SolidBrush cb = new SolidBrush(curColor))
                                g.FillRectangle(cb, cx, cy + 1, 2, cellHeight - 2);
                        }
                        else
                        {
                            using (SolidBrush cb = new SolidBrush(Color.FromArgb(200, curColor)))
                                g.FillRectangle(cb, cx, cy, cellWidth, cellHeight);
                            TermCell curCell = buf.GetCell(buf.CursorRow, buf.CursorCol);
                            if (curCell.Char > ' ')
                            {
                                TextRenderer.DrawText(
                                    g,
                                    curCell.Char.ToString(),
                                    regularFont,
                                    new Point(cx, cy),
                                    effBg,
                                    TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix);
                            }
                        }
                    }
                }
                else
                {
                    // Unfocused cursor: subtle filled tint + crisp outline so cursor is always readable in light & dark themes
                    using (SolidBrush cb = new SolidBrush(Color.FromArgb(isLight ? 45 : 35, curColor)))
                        g.FillRectangle(cb, cx, cy, cellWidth, cellHeight);
                    using (Pen cp = new Pen(Color.FromArgb(200, curColor), 1.2f))
                        g.DrawRectangle(cp, cx, cy, cellWidth - 1, cellHeight - 1);
                }
            }

            // 5. Theme-aware scrollbar track & thumb on the right edge
            int sbCount = buf.ScrollbackCount;
            int trackW = 10;
            int trackX = this.ClientSize.Width - trackW - 2;
            int trackH = Math.Max(20, this.ClientSize.Height - 4);
            if (trackX > 20)
            {
                Color trackBg = isLight
                    ? Color.FromArgb(isMouseInside ? 28 : 14, 100, 116, 139)
                    : Color.FromArgb(isMouseInside ? 28 : 14, 148, 163, 184);
                using (SolidBrush trkBrush = new SolidBrush(trackBg))
                    g.FillRectangle(trkBrush, trackX, 2, trackW, trackH);

                double visibleFraction = sbCount > 0 ? ((double)rows / Math.Max(1, totalVirtual)) : 1.0;
                int thumbH = Math.Max(24, (int)(trackH * Math.Min(1.0, visibleFraction)));
                double posFraction = sbCount > 0 ? (1.0 - ((double)scrollbackOffset / Math.Max(1, sbCount))) : 1.0;
                int thumbY = 2 + (int)((trackH - thumbH) * posFraction);

                Color thumbCol = isLight
                    ? Color.FromArgb(isMouseInside ? 140 : 85, 100, 116, 139)
                    : Color.FromArgb(isMouseInside ? 120 : 75, 128, 128, 138);
                using (SolidBrush tb = new SolidBrush(thumbCol))
                    g.FillRectangle(tb, trackX + 1, thumbY, trackW - 2, thumbH);
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                if (blinkTimer != null)
                {
                    try { blinkTimer.Stop(); blinkTimer.Dispose(); } catch { }
                    blinkTimer = null;
                }
                if (terminalContextMenu != null)
                {
                    try { terminalContextMenu.Dispose(); } catch { }
                    terminalContextMenu = null;
                }
                if (regularFont != null) { try { regularFont.Dispose(); } catch { } regularFont = null; }
                if (boldFont != null) { try { boldFont.Dispose(); } catch { } boldFont = null; }
                if (italicFont != null) { try { italicFont.Dispose(); } catch { } italicFont = null; }
                if (underlineFont != null) { try { underlineFont.Dispose(); } catch { } underlineFont = null; }
            }
            base.Dispose(disposing);
        }
    }
#endif
}