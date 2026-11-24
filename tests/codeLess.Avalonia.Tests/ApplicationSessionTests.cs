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
            public string WorkspaceRoot { get; set; }
            public bool IsActive { get; set; }
            public List<string> OpenedFiles = new List<string>();
            public string DiffA, DiffB;
            public int Line, Col;
            public List<string> AddedFolders = new List<string>();
            public bool Closed = false;

            public void FocusWindow() { IsActive = true; }
            public void OpenFileInTab(string filePath, Action onClosed = null) { OpenedFiles.Add(filePath); }
            public void OpenFileAtLine(string filePath, int line, int col, Action onClosed = null) { OpenedFiles.Add(filePath); Line = line; Col = col; }
            public void AddFolderToWorkspace(string folderPath) { AddedFolders.Add(folderPath); }
            public void DiffFiles(string fileA, string fileB, Action onClosed = null) { DiffA = fileA; DiffB = fileB; }
            public void CloseWindow() { Closed = true; }
            public event EventHandler WindowClosed;
        }

        public List<FakeWindow> Windows = new List<FakeWindow>();

        public IEnumerable<IWorkbenchWindow> GetOpenWindows() { return Windows; }
        public IWorkbenchWindow CreateNewWindow(string workspacePath) { var w = new FakeWindow { WorkspaceRoot = workspacePath }; Windows.Add(w); return w; }
        public IWorkbenchWindow GetLastActiveWindow() { return Windows.LastOrDefault(); }
        public void QuitApplication() { foreach (var w in Windows) w.CloseWindow(); }
    }

    
    public class MockIpcTransport : IIpcTransport
    {
        private class MockStream : Stream
        {
            private MemoryStream _read = new MemoryStream();
            private MemoryStream _write = new MemoryStream();
            private AutoResetEvent _dataAvailable = new AutoResetEvent(false);

            public override bool CanRead { get { return true; } }
            public override bool CanSeek { get { return false; } }
            public override bool CanWrite { get { return true; } }
            public override long Length { get { throw new NotSupportedException(); } }
            public override long Position { get { throw new NotSupportedException(); } set { throw new NotSupportedException(); } }

            public void WriteToReadBuffer(byte[] buffer, int offset, int count)
            {
                lock (_read)
                {
                    long pos = _read.Position;
                    _read.Position = _read.Length;
                    _read.Write(buffer, offset, count);
                    _read.Position = pos;
                }
                _dataAvailable.Set();
            }

            public override void Flush() { }

            public override int Read(byte[] buffer, int offset, int count)
            {
                while (true)
                {
                    lock (_read)
                    {
                        if (_read.Position < _read.Length)
                        {
                            return _read.Read(buffer, offset, count);
                        }
                    }
                    _dataAvailable.WaitOne(100);
                }
            }

            public override long Seek(long offset, SeekOrigin origin) { throw new NotSupportedException(); }
            public override void SetLength(long value) { throw new NotSupportedException(); }
            public override void Write(byte[] buffer, int offset, int count)
            {
                lock (_write)
                {
                    _write.Write(buffer, offset, count);
                }
            }
        }

        private MockStream _stream = new MockStream();

        public Stream ConnectClient(string pipeName, int timeout)
        {
            return new ClientStreamWrapper(_stream);
        }

        public IIpcServer CreateServer(string pipeName)
        {
            return new MockServer(_stream);
        }

        private class ClientStreamWrapper : Stream
        {
            private MockStream _serverStream;
            public ClientStreamWrapper(MockStream serverStream) { _serverStream = serverStream; }
            public override bool CanRead { get { return true; } }
            public override bool CanSeek { get { return false; } }
            public override bool CanWrite { get { return true; } }
            public override long Length { get { throw new NotSupportedException(); } }
            public override long Position { get { throw new NotSupportedException(); } set { throw new NotSupportedException(); } }
            public override void Flush() { }
            public override int Read(byte[] buffer, int offset, int count) { return 0; }
            public override long Seek(long offset, SeekOrigin origin) { throw new NotSupportedException(); }
            public override void SetLength(long value) { throw new NotSupportedException(); }
            public override void Write(byte[] buffer, int offset, int count)
            {
                _serverStream.WriteToReadBuffer(buffer, offset, count);
            }
        }

        private class MockServer : IIpcServer
        {
            private MockStream _stream;
            public MockServer(MockStream stream) { _stream = stream; }
            public Stream Stream { get { return _stream; } }
            public void WaitForConnection() { Thread.Sleep(50); }
            public void Disconnect() { }
            public void Dispose() { }
        }
    }

    public class ApplicationSessionTests
    {
        [Fact]
        public void ProcessArgs_NoArgs_CreatesOrFocuses()
        {
            var wm = new FakeWindowManager();
            ApplicationSession.ProcessArgs(new string[0], "", wm);
            Assert.Single(wm.Windows);
        }

        [Fact]
        public void ProcessArgs_NewWindow_ForcesNew()
        {
            var wm = new FakeWindowManager();
            var w1 = wm.CreateNewWindow(null) as FakeWindowManager.FakeWindow;
            ApplicationSession.ProcessArgs(new string[] { "-n" }, "", wm);
            Assert.Equal(2, wm.Windows.Count);
        }

        [Fact]
        public void ProcessArgs_File_OpensInActive()
        {
            var wm = new FakeWindowManager();
            var w1 = wm.CreateNewWindow(null) as FakeWindowManager.FakeWindow;
            ApplicationSession.ProcessArgs(new string[] { "foo.txt" }, "/cwd", wm);
            Assert.Contains(Path.GetFullPath(Path.Combine("/cwd", "foo.txt")), w1.OpenedFiles);
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
            ApplicationSession.ProcessArgs(new string[] { "myfolder" }, "/cwd", wm);
            Assert.Single(wm.Windows);
            Assert.True(w1.IsActive);
        }

        [Fact]
        public void IpcRoundTrip()
        {
            ApplicationSession.OverrideIpcPipeName = "test-pipe-" + Guid.NewGuid().ToString("N");
            ApplicationSession.Transport = new MockIpcTransport();
            
            ApplicationIpcMessage received = null;
            var mre = new ManualResetEvent(false);
            
            ApplicationSession.OnMessageReceived = (msg, ack) => {
                received = msg;
                mre.Set();
            };
            
            ApplicationSession.StartPrimaryServer();
            Thread.Sleep(200);
            
            bool sent = ApplicationSession.TryForwardToPrimaryInstance(new string[] { "test.txt" });
            Assert.True(sent);
            
            mre.WaitOne(2000);
            Assert.NotNull(received);
            Assert.Equal("test.txt", received.Args[0]);
            
            ApplicationSession.Transport = new NamedPipeIpcTransport(); // Restore
        }
    }
}
