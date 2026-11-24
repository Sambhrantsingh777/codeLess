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

    
    public interface IIpcTransport
    {
        Stream ConnectClient(string pipeName, int timeout);
        IIpcServer CreateServer(string pipeName);
    }
    public interface IIpcServer : IDisposable
    {
        Stream Stream { get; }
        void WaitForConnection();
        void Disconnect();
    }
    public class NamedPipeIpcTransport : IIpcTransport
    {
        public Stream ConnectClient(string pipeName, int timeout)
        {
            var client = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.None);
            client.Connect(timeout);
            return client;
        }
        public IIpcServer CreateServer(string pipeName)
        {
            try {
                var server = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.None);
                return new NamedPipeServer(server);
            } catch (IOException) {
                if (Environment.OSVersion.Platform != PlatformID.Win32NT) {
                    try {
                        string tmp = Path.Combine(Path.GetTempPath(), ".dotnet", "corefx", "pipe", pipeName);
                        if (File.Exists(tmp)) File.Delete(tmp);
                        else {
                            tmp = Path.Combine(Path.GetTempPath(), pipeName);
                            if (File.Exists(tmp)) File.Delete(tmp);
                        }
                    } catch { }
                }
                var server = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.None);
                return new NamedPipeServer(server);
            }
        }
    }
    public class NamedPipeServer : IIpcServer
    {
        private NamedPipeServerStream _server;
        public NamedPipeServer(NamedPipeServerStream server) { _server = server; }
        public Stream Stream { get { return _server; } }
        public void WaitForConnection() { _server.WaitForConnection(); }
        public void Disconnect() { _server.Disconnect(); }
        public void Dispose() { _server.Dispose(); }
    }

    public static partial class ApplicationSession
    {
        public static Action<ApplicationIpcMessage, Action> OnMessageReceived;
        

        public static bool ShouldBypassForwarding(string[] args)
        {
            if (args == null) return false;
            for (int i = 0; i < args.Length; i++)
            {
                string a = args[i];
                if (a == "--new-instance" || a == "--screenshot" || a == "-s" ||
                    a == "--screenshot-suite" || a == "-ss" || a == "--bench" || a == "--idle-probe" ||
                    a == "--bench-ready" || a == "--bench-open-file" || a == "--bench-open-report" ||
                    a == "--bench-open-delay-ms" || a == "--native-self-test" || a == "--smoke-quit")
                    return true;
                if (a == "--user-data-dir" && i + 1 < args.Length) {
                    Environment.SetEnvironmentVariable("CODELESS_SETTINGS_DIR", args[i + 1]);
                }
            }
            return false;
        }

        public static string GetIpcPipeName()
        {
            string baseName = "codeless-ipc";
            if (Environment.OSVersion.Platform == PlatformID.Win32NT)
            {
                string user = Environment.UserName;
                try { user = WindowsIdentity.GetCurrent().User.Value; } catch { }
                baseName += "-" + user + "-" + Process.GetCurrentProcess().SessionId;
            }
            else
            {
                baseName += "-" + Environment.UserName;
            }
            
            string settingsDir = Environment.GetEnvironmentVariable("CODELESS_SETTINGS_DIR");
            if (string.IsNullOrEmpty(settingsDir)) {
                settingsDir = IdePersistentSettings.GetSettingsFilePath();
                settingsDir = System.IO.Path.GetDirectoryName(settingsDir) ?? "";
            }
            
            using (var md5 = System.Security.Cryptography.MD5.Create())
            {
                byte[] data = md5.ComputeHash(Encoding.UTF8.GetBytes(settingsDir.ToLowerInvariant()));
                StringBuilder sb = new StringBuilder();
                foreach (byte b in data) sb.Append(b.ToString("x2"));
                return baseName + "-" + sb.ToString();
            }
        }
        
        public static string OverrideIpcPipeName = null;
        public static IIpcTransport Transport = new NamedPipeIpcTransport();

        public static bool TryForwardToPrimaryInstance(string[] args)
        {
            if (ShouldBypassForwarding(args)) return false;
            
            // Check if CODELESS_SETTINGS_DIR points to a test directory (xrunner uses .settings-XXXXXX)
            string sd = Environment.GetEnvironmentVariable("CODELESS_SETTINGS_DIR") ?? "";
            if (sd.IndexOf(".settings-", StringComparison.OrdinalIgnoreCase) >= 0 && OverrideIpcPipeName == null) return false;
            
            string pipeName = OverrideIpcPipeName ?? GetIpcPipeName();
            try
            {
                using (var client = Transport.ConnectClient(pipeName, 20))
                {
                    var msg = new ApplicationIpcMessage { Cwd = Environment.CurrentDirectory, Args = args };
                    string json = new System.Web.Script.Serialization.JavaScriptSerializer().Serialize(msg);
                    byte[] data = Encoding.UTF8.GetBytes(json);
                    byte[] len = BitConverter.GetBytes(data.Length);
                    client.Write(len, 0, 4);
                    client.Write(data, 0, data.Length);
                    client.Flush();
                    
                    bool wait = false;
                    if (args != null) {
                        foreach(var a in args) if (a == "-w" || a == "--wait") wait = true;
                    }
                    if (wait) {
                        client.ReadByte();
                    }
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
                string pipeName = OverrideIpcPipeName ?? GetIpcPipeName();
                int exceptionCount = 0;
                while (true)
                {
                    IIpcServer server = null;
                    try
                    {
                        server = Transport.CreateServer(pipeName);
                    }
                    catch { 
                        exceptionCount++;
                        if (exceptionCount > 10) return;
                        Thread.Sleep(500);
                        continue; 
                    }
                    exceptionCount = 0;

                    try
                    {
                        while (true)
                        {
                            if (server == null) break; // Re-create server if it was taken by wait
                            server.WaitForConnection();
                            byte[] lenBuf = new byte[4];
                            int read = 0;
                            while (read < 4)
                            {
                                int r = server.Stream.Read(lenBuf, read, 4 - read);
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
                                        int r = server.Stream.Read(data, read, len - read);
                                        if (r == 0) break;
                                        read += r;
                                    }
                                    if (read == len)
                                    {
                                        string json = Encoding.UTF8.GetString(data);
                                        var msg = new System.Web.Script.Serialization.JavaScriptSerializer().Deserialize<ApplicationIpcMessage>(json);
                                        bool wait = false;
                                        if (msg != null && msg.Args != null) {
                                            foreach (var a in msg.Args) if (a == "-w" || a == "--wait") wait = true;
                                        }
                                        if (wait) {
                                            if (OnMessageReceived != null) {
                                                IIpcServer waitServer = server;
                                                server = null; // Don't let outer loop dispose it
                                                OnMessageReceived(msg, () => {
                                                    try { waitServer.Stream.WriteByte(1); waitServer.Stream.Flush(); waitServer.Disconnect(); waitServer.Dispose(); } catch { }
                                                });
                                                continue; // loop without disconnect
                                            } else {
                                                try { server.Stream.WriteByte(1); server.Stream.Flush(); } catch { }
                                            }
                                        } else {
                                            if (msg != null && OnMessageReceived != null) {
                                                OnMessageReceived(msg, null);
                                            }
                                        }
                                    }
                                }
                            }
                            if (server != null) server.Disconnect();
                        }
                    }
                    catch
                    {
                        try { if (server != null) server.Dispose(); } catch { }
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
        public static bool IsSpecialArg(string a) {
            return a == "--new-instance" || a == "--user-data-dir" || a == "--screenshot" || a == "-s" ||
                   a == "--screenshot-suite" || a == "-ss" || a == "--bench" || a == "--idle-probe" ||
                   a == "--bench-ready" || a == "--bench-open-file" || a == "--bench-open-report" ||
                   a == "--bench-open-delay-ms" || a == "--native-self-test" || a == "--smoke-quit" ||
                   a == "--workspace" || a == "-w" || a == "--wait";
        }



        public static void ProcessArgs(string[] args, string cwd, IWindowManager wm, Action onClosed = null) {
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
                else if (a == "--user-data-dir" || a == "--workspace") i++;
                else if (!a.StartsWith("-") && !IsSpecialArg(a)) paths.Add(a);
            }

            if (diffA != null && diffB != null) {
                var w = wm.GetLastActiveWindow() ?? wm.CreateNewWindow(null);
                w.DiffFiles(GetFullPath(diffA, cwd), GetFullPath(diffB, cwd), onClosed);
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
                w.OpenFileAtLine(GetFullPath(p, cwd), line, col, onClosed);
                w.FocusWindow();
                return;
            }

            if (paths.Count == 0) {
                var w = wm.GetLastActiveWindow();
                if (w != null && newWindow) w = wm.CreateNewWindow(null);
                else if (w == null) w = wm.CreateNewWindow(null);
                if (onClosed != null) w.WindowClosed += (s, e) => onClosed();
                w.FocusWindow();
                return;
            }

            IWorkbenchWindow lastW = null;
            foreach (var path in paths) {
                string fullPath = GetFullPath(path, cwd);
                if (System.IO.Directory.Exists(fullPath)) {
                    var existing = wm.GetOpenWindows().FirstOrDefault(x => string.Equals(x.WorkspaceRoot, fullPath, StringComparison.OrdinalIgnoreCase) || (x.WorkspaceRoot != null && string.Equals(x.WorkspaceRoot.TrimEnd('\\', '/'), fullPath.TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase)));
                    if (existing != null && !newWindow) {
                        if (onClosed != null) existing.WindowClosed += (s, e) => onClosed();
                        existing.FocusWindow();
                        lastW = existing;
                    } else {
                        var w = wm.CreateNewWindow(fullPath);
                        if (onClosed != null) w.WindowClosed += (s, e) => onClosed();
                        w.FocusWindow();
                        lastW = w;
                    }
                } else {
                    var w = wm.GetLastActiveWindow();
                    if (w == null || newWindow) w = wm.CreateNewWindow(null);
                    w.OpenFileInTab(fullPath, onClosed);
                    w.FocusWindow();
                    lastW = w;
                }
            }
            return;
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
