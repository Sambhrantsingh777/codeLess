using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Xunit;
using codeLess.Studio;

namespace codeLess.AvaloniaApp.Tests
{
    public class FakeWindowManager : IWindowManager
    {
        public class FakeWindow : IWorkbenchWindow
        {
            private readonly FakeWindowManager _owner;
            public FakeWindow(FakeWindowManager owner = null) { _owner = owner; }

            public string WorkspaceRoot { get; set; }
            public bool IsActive { get; set; }
            public List<string> OpenedFiles = new List<string>();
            public Dictionary<string, Action> FileCloseCallbacks = new Dictionary<string, Action>(StringComparer.OrdinalIgnoreCase);
            public string DiffA, DiffB;
            public int Line, Col;
            public List<string> AddedFolders = new List<string>();
            public bool Closed = false;
            public RestoreWindowInfo Bounds = new RestoreWindowInfo();

            public void FocusWindow()
            {
                if (_owner != null)
                {
                    foreach (var w in _owner.Windows) w.IsActive = false;
                }
                IsActive = true;
            }

            public void OpenFolder(string folderPath)
            {
                WorkspaceRoot = folderPath;
            }

            public void OpenFileInTab(string filePath, Action onClosed = null)
            {
                OpenedFiles.Add(filePath);
                if (onClosed != null) FileCloseCallbacks[filePath] = onClosed;
            }

            public void SimulateCloseTab(string filePath)
            {
                Action cb;
                if (FileCloseCallbacks.TryGetValue(filePath, out cb) && cb != null)
                {
                    FileCloseCallbacks.Remove(filePath);
                    cb();
                }
            }

            public void OpenFileAtLine(string filePath, int line, int col, Action onClosed = null)
            {
                OpenedFiles.Add(filePath);
                Line = line;
                Col = col;
                if (onClosed != null) FileCloseCallbacks[filePath] = onClosed;
            }

            public void AddFolderToWorkspace(string folderPath)
            {
                AddedFolders.Add(folderPath);
            }

            public void DiffFiles(string fileA, string fileB, Action onClosed = null)
            {
                DiffA = fileA;
                DiffB = fileB;
                if (onClosed != null) FileCloseCallbacks[fileA] = onClosed;
            }

            public RestoreWindowInfo GetWindowBounds()
            {
                Bounds.WorkspacePath = WorkspaceRoot ?? "";
                return Bounds;
            }

            public void SetWindowBounds(RestoreWindowInfo bounds)
            {
                if (bounds != null) Bounds = bounds;
            }

            public void CloseWindow()
            {
                Closed = true;
                var h = WindowClosed;
                if (h != null) h(this, EventArgs.Empty);
            }

            public event EventHandler WindowClosed;
        }

        public List<FakeWindow> Windows = new List<FakeWindow>();

        public IEnumerable<IWorkbenchWindow> GetOpenWindows() { return Windows; }

        public IWorkbenchWindow CreateNewWindow(string workspacePath)
        {
            var w = new FakeWindow(this) { WorkspaceRoot = workspacePath };
            Windows.Add(w);
            w.FocusWindow();
            return w;
        }

        public IWorkbenchWindow GetLastActiveWindow()
        {
            return Windows.LastOrDefault(w => w.IsActive) ?? Windows.LastOrDefault();
        }

        public void QuitApplication()
        {
            foreach (var w in Windows.ToList()) w.CloseWindow();
        }
    }

    public class MockIpcTransport : IIpcTransport, IDisposable
    {
        private class MockChannel
        {
            private readonly MemoryStream _clientToServer = new MemoryStream();
            private readonly MemoryStream _serverToClient = new MemoryStream();
            private readonly AutoResetEvent _clientToServerEvent = new AutoResetEvent(false);
            private readonly AutoResetEvent _serverToClientEvent = new AutoResetEvent(false);
            private readonly AutoResetEvent _connectedEvent = new AutoResetEvent(false);
            private volatile bool _disposed;

            public void SignalConnected() { _connectedEvent.Set(); }
            public void WaitConnected() { _connectedEvent.WaitOne(500); }

            public void WriteClientToServer(byte[] buffer, int offset, int count)
            {
                lock (_clientToServer)
                {
                    long pos = _clientToServer.Position;
                    _clientToServer.Position = _clientToServer.Length;
                    _clientToServer.Write(buffer, offset, count);
                    _clientToServer.Position = pos;
                }
                _clientToServerEvent.Set();
            }

            public int ReadClientToServer(byte[] buffer, int offset, int count)
            {
                for (int tries = 0; tries < 30 && !_disposed; tries++)
                {
                    lock (_clientToServer)
                    {
                        if (_clientToServer.Position < _clientToServer.Length)
                            return _clientToServer.Read(buffer, offset, count);
                    }
                    _clientToServerEvent.WaitOne(50);
                }
                return 0;
            }

            public void WriteServerToClient(byte[] buffer, int offset, int count)
            {
                lock (_serverToClient)
                {
                    long pos = _serverToClient.Position;
                    _serverToClient.Position = _serverToClient.Length;
                    _serverToClient.Write(buffer, offset, count);
                    _serverToClient.Position = pos;
                }
                _serverToClientEvent.Set();
            }

            public int ReadServerToClient(byte[] buffer, int offset, int count)
            {
                for (int tries = 0; tries < 60 && !_disposed; tries++)
                {
                    lock (_serverToClient)
                    {
                        if (_serverToClient.Position < _serverToClient.Length)
                            return _serverToClient.Read(buffer, offset, count);
                    }
                    _serverToClientEvent.WaitOne(50);
                }
                return 0;
            }

            public void Dispose()
            {
                _disposed = true;
                try { _clientToServerEvent.Set(); } catch { }
                try { _serverToClientEvent.Set(); } catch { }
                try { _connectedEvent.Set(); } catch { }
            }
        }

        private readonly MockChannel _channel = new MockChannel();

        public Stream ConnectClient(string pipeName, int timeout)
        {
            _channel.SignalConnected();
            return new EndpointStream(_channel, isClient: true);
        }

        public IIpcServer CreateServer(string pipeName)
        {
            return new MockServer(_channel);
        }

        public void Dispose()
        {
            _channel.Dispose();
        }

        private class EndpointStream : Stream
        {
            private readonly MockChannel _ch;
            private readonly bool _isClient;
            public EndpointStream(MockChannel ch, bool isClient) { _ch = ch; _isClient = isClient; }
            public override bool CanRead { get { return true; } }
            public override bool CanSeek { get { return false; } }
            public override bool CanWrite { get { return true; } }
            public override long Length { get { throw new NotSupportedException(); } }
            public override long Position { get { throw new NotSupportedException(); } set { throw new NotSupportedException(); } }
            public override void Flush() { }
            public override int Read(byte[] buffer, int offset, int count)
            {
                return _isClient
                    ? _ch.ReadServerToClient(buffer, offset, count)
                    : _ch.ReadClientToServer(buffer, offset, count);
            }
            public override void Write(byte[] buffer, int offset, int count)
            {
                if (_isClient) _ch.WriteClientToServer(buffer, offset, count);
                else _ch.WriteServerToClient(buffer, offset, count);
            }
            public override long Seek(long offset, SeekOrigin origin) { throw new NotSupportedException(); }
            public override void SetLength(long value) { throw new NotSupportedException(); }
        }

        private class MockServer : IIpcServer
        {
            private readonly MockChannel _ch;
            private readonly EndpointStream _stream;
            public MockServer(MockChannel ch) { _ch = ch; _stream = new EndpointStream(ch, isClient: false); }
            public Stream Stream { get { return _stream; } }
            public void WaitForConnection() { _ch.WaitConnected(); }
            public void Disconnect() { }
            public void Dispose() { }
        }
    }

    [Collection("SequentialWorkbench")]
    public class ApplicationSessionTests : IDisposable
    {
        private readonly string _prevSettingsDir;
        private readonly string _tempSettingsDir;

        public ApplicationSessionTests()
        {
            _prevSettingsDir = Environment.GetEnvironmentVariable("CODELESS_SETTINGS_DIR");
            _tempSettingsDir = Path.Combine(Path.GetTempPath(), "settings-appsession-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_tempSettingsDir);
            Environment.SetEnvironmentVariable("CODELESS_SETTINGS_DIR", _tempSettingsDir);
        }

        public void Dispose()
        {
            ApplicationSession.OverrideIpcPipeName = null;
            ApplicationSession.OnMessageReceived = null;
            ApplicationSession.Transport = new NamedPipeIpcTransport();
            Environment.SetEnvironmentVariable("CODELESS_SETTINGS_DIR", _prevSettingsDir);
            try { if (Directory.Exists(_tempSettingsDir)) Directory.Delete(_tempSettingsDir, true); } catch { }
        }

        [Fact]
        public void ProcessArgs_NoArgs_CreatesOrFocuses()
        {
            var wm = new FakeWindowManager();
            ApplicationSession.ProcessArgs(new string[0], "", wm);
            Assert.Single(wm.Windows);
            Assert.True(wm.Windows[0].IsActive);

            wm.Windows[0].IsActive = false;
            ApplicationSession.ProcessArgs(new string[0], "", wm);
            Assert.Single(wm.Windows);
            Assert.True(wm.Windows[0].IsActive);
        }

        [Fact]
        public void ProcessArgs_NewWindow_ForcesNew()
        {
            var wm = new FakeWindowManager();
            var w1 = wm.CreateNewWindow(Path.GetFullPath("/cwd/myfolder")) as FakeWindowManager.FakeWindow;
            ApplicationSession.ProcessArgs(new string[] { "-n" }, "", wm);
            Assert.Equal(2, wm.Windows.Count);

            // -n with a folder already open still forces a new window
            ApplicationSession.ProcessArgs(new string[] { "-n", "myfolder" }, "/cwd", wm);
            Assert.Equal(3, wm.Windows.Count);
            Assert.Equal(Path.GetFullPath("/cwd/myfolder"), wm.Windows[2].WorkspaceRoot);
        }

        [Fact]
        public void ProcessArgs_ReuseWindow_ReusesActiveWindow()
        {
            string dir1 = Path.Combine(_tempSettingsDir, "ws1");
            string dir2 = Path.Combine(_tempSettingsDir, "ws2");
            Directory.CreateDirectory(dir1);
            Directory.CreateDirectory(dir2);

            var wm = new FakeWindowManager();
            var w1 = wm.CreateNewWindow(dir1) as FakeWindowManager.FakeWindow;

            // Without -r, opening a different folder opens a second window
            ApplicationSession.ProcessArgs(new string[] { dir2 }, _tempSettingsDir, wm);
            Assert.Equal(2, wm.Windows.Count);

            // With -r / --reuse-window, opening another folder reuses the last active window
            string dir3 = Path.Combine(_tempSettingsDir, "ws3");
            Directory.CreateDirectory(dir3);
            ApplicationSession.ProcessArgs(new string[] { "-r", dir3 }, _tempSettingsDir, wm);
            Assert.Equal(2, wm.Windows.Count);
            Assert.Equal(Path.GetFullPath(dir3), wm.Windows[1].WorkspaceRoot);
        }

        [Fact]
        public void ProcessArgs_File_OpensInActive()
        {
            string file = Path.Combine(_tempSettingsDir, "foo.txt");
            File.WriteAllText(file, "hello");
            var wm = new FakeWindowManager();
            var w1 = wm.CreateNewWindow(null) as FakeWindowManager.FakeWindow;
            ApplicationSession.ProcessArgs(new string[] { "foo.txt" }, _tempSettingsDir, wm);
            Assert.Contains(Path.GetFullPath(file), w1.OpenedFiles);
        }

        [Fact]
        public void ProcessArgs_Goto_OpensAtLine()
        {
            var wm = new FakeWindowManager();
            var w1 = wm.CreateNewWindow(null) as FakeWindowManager.FakeWindow;
            ApplicationSession.ProcessArgs(new string[] { "-g", "foo.txt:10:5" }, "/cwd", wm);
            Assert.Equal(10, w1.Line);
            Assert.Equal(5, w1.Col);
        }

        [Fact]
        public void ProcessArgs_Diff_OpensDiff()
        {
            var wm = new FakeWindowManager();
            var w1 = wm.CreateNewWindow(null) as FakeWindowManager.FakeWindow;
            ApplicationSession.ProcessArgs(new string[] { "-d", "a.txt", "b.txt" }, "/cwd", wm);
            Assert.NotNull(w1.DiffA);
            Assert.NotNull(w1.DiffB);
        }

        [Fact]
        public void ProcessArgs_Folder_FocusesIfOpen()
        {
            var wm = new FakeWindowManager();
            var w1 = wm.CreateNewWindow(Path.GetFullPath("/cwd/myfolder")) as FakeWindowManager.FakeWindow;
            w1.IsActive = false;
            ApplicationSession.ProcessArgs(new string[] { "myfolder" }, "/cwd", wm);
            Assert.Single(wm.Windows);
            Assert.True(w1.IsActive);
        }

        [Fact]
        public void ProcessArgs_Wait_WaitsForFileToClose()
        {
            string file = Path.Combine(_tempSettingsDir, "waitfile.txt");
            File.WriteAllText(file, "wait content");

            var wm = new FakeWindowManager();
            var w1 = wm.CreateNewWindow(null) as FakeWindowManager.FakeWindow;

            bool waitCompleted = false;
            ApplicationSession.ProcessArgs(new string[] { "-w", file }, _tempSettingsDir, wm, () => waitCompleted = true);
            Assert.Contains(Path.GetFullPath(file), w1.OpenedFiles);
            Assert.False(waitCompleted);

            w1.SimulateCloseTab(Path.GetFullPath(file));
            Assert.True(waitCompleted);
        }

        [Fact]
        public void IpcRoundTrip()
        {
            using (var transport = new MockIpcTransport())
            {
                ApplicationSession.OverrideIpcPipeName = "test-pipe-" + Guid.NewGuid().ToString("N");
                ApplicationSession.Transport = transport;

                ApplicationIpcMessage received = null;
                var mre = new ManualResetEvent(false);

                ApplicationSession.OnMessageReceived = (msg, ack) =>
                {
                    received = msg;
                    mre.Set();
                    ack?.Invoke();
                };

                ApplicationSession.StartPrimaryServer();
                Thread.Sleep(50);

                bool sent = ApplicationSession.TryForwardToPrimaryInstance(new string[] { "-w", "test.txt" });
                Assert.True(sent);

                Assert.True(mre.WaitOne(2000));
                Assert.NotNull(received);
                Assert.Equal("-w", received.Args[0]);
                Assert.Equal("test.txt", received.Args[1]);
            }
        }
    }
}
