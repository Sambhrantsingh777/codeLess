using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using codeLess.Studio;
using Xunit;

namespace codeLess.Avalonia.Tests
{
    /// <summary>
    /// Spawns a real login shell on a PTY (macOS / Linux only). Guards the macOS regression where the shell had
    /// no controlling terminal (Ctrl+C / job control / SIGWINCH broken) and where disposing sessions hung the UI.
    /// </summary>
    public class UnixPtyIntegrationTests
    {
        private static bool IsUnix => !RuntimeInformation.IsOSPlatform(OSPlatform.Windows);

        private static bool WaitFor(ConPtyTerminalSession s, string needle, int ms = 10000)
        {
            var end = DateTime.UtcNow.AddMilliseconds(ms);
            while (DateTime.UtcNow < end)
            {
                if (s.Buffer.GetScrollbackAndScreenText(1000).Contains(needle)) return true;
                Thread.Sleep(100);
            }
            return false;
        }

        [Fact]
        public void Shell_HasControllingTty_CtrlCWorks_ResizePropagates_AndDisposeIsFast()
        {
            if (!IsUnix) return;
            string ws = Path.Combine(Path.GetTempPath(), "codeLess_pty_" + Guid.NewGuid().ToString("N").Substring(0, 6));
            Directory.CreateDirectory(ws);
            var s = new ConPtyTerminalSession(null, "/bin/sh", ws, 100, 30);
            try
            {
                Assert.True(s.IsRunning);
                s.WriteInput("echo READY_$((20+22))\r");
                Assert.True(WaitFor(s, "READY_42"), s.Buffer.GetScrollbackAndScreenText(200));

                // Controlling terminal: /dev/tty must be openable by the shell.
                s.WriteInput("( : < /dev/tty ) 2>/dev/null && echo CTTY_$((1+1))_OK || echo CTTY_FAIL\r");
                Assert.True(WaitFor(s, "CTTY_2_OK"), s.Buffer.GetScrollbackAndScreenText(200));

                // Ctrl+C must interrupt the foreground job.
                s.WriteInput("sleep 30; echo SLEEP_NOT_INTERRUPTED\r");
                Thread.Sleep(800);
                s.WriteInput("\x03");
                s.WriteInput("echo AFTER_$((6*7))\r");
                Assert.True(WaitFor(s, "AFTER_42", 5000), s.Buffer.GetScrollbackAndScreenText(200));

                s.Resize(70, 20);
                Thread.Sleep(300);
                s.WriteInput("stty size\r");
                Assert.True(WaitFor(s, "20 70"), s.Buffer.GetScrollbackAndScreenText(200));

                // Leave a foreground job running, then dispose: must not block.
                s.WriteInput("sleep 1000\r");
                Thread.Sleep(500);
            }
            finally
            {
                var sw = Stopwatch.StartNew();
                var t = new Thread(() => s.Dispose()) { IsBackground = true };
                t.Start();
                Assert.True(t.Join(3000), "Dispose blocked");
                Assert.True(sw.ElapsedMilliseconds < PerfBudget.Ms(3000));
                try { Directory.Delete(ws, true); } catch { }
            }
        }
    }
}
