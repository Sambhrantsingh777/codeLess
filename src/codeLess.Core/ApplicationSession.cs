using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Security.Principal;
using System.Text;
using System.Threading;

namespace codeLess.Studio
{
    public class ApplicationIpcMessage
    {
        public string Cwd { get; set; }
        public string[] Args { get; set; }
    }

    public static partial class ApplicationSession
    {
        public static Action<ApplicationIpcMessage> OnMessageReceived;

        public static bool ShouldBypassForwarding(string[] args)
        {
            if (args == null) return false;
            foreach (var a in args)
            {
                if (a == "--new-instance" || a == "--user-data-dir" || a == "--screenshot" || a == "-s" ||
                    a == "--screenshot-suite" || a == "-ss" || a == "--bench" || a == "--idle-probe" ||
                    a == "--bench-ready" || a == "--bench-open-file" || a == "--bench-open-report" ||
                    a == "--bench-open-delay-ms" || a == "--native-self-test" || a == "--smoke-quit")
                    return true;
            }
            return false;
        }

        private static string GetIpcPipeName()
        {
            if (Environment.OSVersion.Platform == PlatformID.Win32NT)
            {
                string user = Environment.UserName;
                try { user = WindowsIdentity.GetCurrent().User.Value; } catch { }
                return "codeless-ipc-" + user + "-" + Process.GetCurrentProcess().SessionId;
            }
            return "codeless-ipc-" + Environment.UserName;
        }

        public static bool TryForwardToPrimaryInstance(string[] args)
        {
            if (ShouldBypassForwarding(args)) return false;
            string pipeName = GetIpcPipeName();
            try
            {
                using (var client = new NamedPipeClientStream(".", pipeName, PipeDirection.Out, PipeOptions.None))
                {
                    client.Connect(20);
                    var msg = new ApplicationIpcMessage { Cwd = Environment.CurrentDirectory, Args = args };
                    string json = new System.Web.Script.Serialization.JavaScriptSerializer().Serialize(msg);
                    byte[] data = Encoding.UTF8.GetBytes(json);
                    byte[] len = BitConverter.GetBytes(data.Length);
                    client.Write(len, 0, 4);
                    client.Write(data, 0, data.Length);
                    client.Flush();
                    return true;
                }
            }
            catch
            {
                return false;
            }
        }

        public static void StartPrimaryServer()
        {
            Thread t = new Thread(() =>
            {
                string pipeName = GetIpcPipeName();
                while (true)
                {
                    NamedPipeServerStream server = null;
                    try
                    {
                        server = new NamedPipeServerStream(pipeName, PipeDirection.In, 1, PipeTransmissionMode.Byte, PipeOptions.None);
                    }
                    catch (IOException)
                    {
                        if (Environment.OSVersion.Platform != PlatformID.Win32NT)
                        {
                            try
                            {
                                string tmp = Path.Combine(Path.GetTempPath(), ".dotnet", "corefx", "pipe", pipeName);
                                if (File.Exists(tmp)) File.Delete(tmp);
                                else
                                {
                                    tmp = Path.Combine(Path.GetTempPath(), pipeName);
                                    if (File.Exists(tmp)) File.Delete(tmp);
                                }
                            }
                            catch { }
                        }
                        try { server = new NamedPipeServerStream(pipeName, PipeDirection.In, 1, PipeTransmissionMode.Byte, PipeOptions.None); }
                        catch { return; }
                    }
                    catch { return; }

                    try
                    {
                        while (true)
                        {
                            server.WaitForConnection();
                            byte[] lenBuf = new byte[4];
                            int read = 0;
                            while (read < 4)
                            {
                                int r = server.Read(lenBuf, read, 4 - read);
                                if (r == 0) break;
                                read += r;
                            }
                            if (read == 4)
                            {
                                int len = BitConverter.ToInt32(lenBuf, 0);
                                if (len > 0 && len < 1024 * 1024)
                                {
                                    byte[] data = new byte[len];
                                    read = 0;
                                    while (read < len)
                                    {
                                        int r = server.Read(data, read, len - read);
                                        if (r == 0) break;
                                        read += r;
                                    }
                                    if (read == len)
                                    {
                                        string json = Encoding.UTF8.GetString(data);
                                        var msg = new System.Web.Script.Serialization.JavaScriptSerializer().Deserialize<ApplicationIpcMessage>(json);
                                        if (msg != null && OnMessageReceived != null)
                                        {
                                            OnMessageReceived(msg);
                                        }
                                    }
                                }
                            }
                            server.Disconnect();
                        }
                    }
                    catch
                    {
                        try { server.Dispose(); } catch { }
                    }
                }
            })
            {
                IsBackground = true,
                Name = "IpcServerThread"
            };
            t.Start();
        }
    }
}

namespace codeLess.Studio {
    public static partial class ApplicationSession {
        public static void ProcessArgs(string[] args, string cwd, IWindowManager wm) {
            if (args == null || args.Length == 0) {
                var last = wm.GetLastActiveWindow();
                if (last != null) last.FocusWindow();
                else {
                    var settings = codeLess.Studio.IdePersistentSettings.Load();
                    if (settings.WindowRestoreWindows != "none" && settings.RestoreWindowPaths != null && settings.RestoreWindowPaths.Count > 0) {
                        int count = settings.WindowRestoreWindows == "one" ? 1 : settings.RestoreWindowPaths.Count;
                        for (int i = 0; i < count; i++) {
                            wm.CreateNewWindow(settings.RestoreWindowPaths[i]);
                        }
                    } else {
                        wm.CreateNewWindow(null);
                    }
                }
                return;
            }

            bool reuseWindow = false;
            bool newWindow = false;
            bool wait = false;
            string gotoFile = null;
            string addFolder = null;
            string diffA = null;
            string diffB = null;

            List<string> paths = new List<string>();

            for (int i = 0; i < args.Length; i++) {
                string a = args[i];
                if (a == "-r" || a == "--reuse-window") reuseWindow = true;
                else if (a == "-n" || a == "--new-window") newWindow = true;
                else if (a == "-w" || a == "--wait") wait = true;
                else if ((a == "-g" || a == "--goto") && i + 1 < args.Length) gotoFile = args[++i];
                else if ((a == "-a" || a == "--add") && i + 1 < args.Length) addFolder = args[++i];
                else if ((a == "-d" || a == "--diff") && i + 2 < args.Length) { diffA = args[++i]; diffB = args[++i]; }
                else if (!a.StartsWith("-")) paths.Add(a);
            }

            if (diffA != null && diffB != null) {
                var w = wm.GetLastActiveWindow() ?? wm.CreateNewWindow(null);
                w.DiffFiles(GetFullPath(diffA, cwd), GetFullPath(diffB, cwd));
                w.FocusWindow();
                return;
            }
            if (addFolder != null) {
                var w = wm.GetLastActiveWindow() ?? wm.CreateNewWindow(null);
                w.AddFolderToWorkspace(GetFullPath(addFolder, cwd));
                w.FocusWindow();
                return;
            }
            if (gotoFile != null) {
                string p = gotoFile;
                int line = 1, col = 1;
                int parsed; int parsedLine;
                int idx = p.LastIndexOf(':');
                if (idx > 0 && idx < p.Length - 1 && int.TryParse(p.Substring(idx + 1), out parsed)) {
                    line = parsed;
                    p = p.Substring(0, idx);
                    int idx2 = p.LastIndexOf(':');
                    if (idx2 > 0 && idx2 < p.Length - 1 && int.TryParse(p.Substring(idx2 + 1), out parsedLine)) {
                        col = parsed;
                        line = parsedLine;
                        p = p.Substring(0, idx2);
                    }
                }
                var w = wm.GetLastActiveWindow() ?? wm.CreateNewWindow(null);
                w.OpenFileAtLine(GetFullPath(p, cwd), line, col);
                w.FocusWindow();
                return;
            }

            if (paths.Count == 0) {
                var w = wm.GetLastActiveWindow();
                if (w != null && newWindow) w = wm.CreateNewWindow(null);
                else if (w == null) w = wm.CreateNewWindow(null);
                w.FocusWindow();
                return;
            }

            foreach (var path in paths) {
                string fullPath = GetFullPath(path, cwd);
                if (Directory.Exists(fullPath)) {
                    var existing = wm.GetOpenWindows().FirstOrDefault(x => string.Equals(x.WorkspaceRoot, fullPath, StringComparison.OrdinalIgnoreCase) || (x.WorkspaceRoot != null && string.Equals(x.WorkspaceRoot.TrimEnd('\\', '/'), fullPath.TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase)));
                    if (existing != null && !newWindow) {
                        existing.FocusWindow();
                    } else {
                        var w = wm.CreateNewWindow(fullPath);
                        w.FocusWindow();
                    }
                } else {
                    var w = wm.GetLastActiveWindow();
                    if (w == null || newWindow) w = wm.CreateNewWindow(null);
                    w.OpenFileInTab(fullPath);
                    w.FocusWindow();
                }
            }
        }

        private static string GetFullPath(string path, string cwd) {
            if (string.IsNullOrEmpty(path)) return path;
            try {
                if (Path.IsPathRooted(path)) return Path.GetFullPath(path);
                if (!string.IsNullOrEmpty(cwd)) return Path.GetFullPath(Path.Combine(cwd, path));
                return Path.GetFullPath(path);
            } catch { return path; }
        }
    }
}
