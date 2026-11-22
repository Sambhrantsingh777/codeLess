using System;
using System.IO;
using System.Text;
using System.Threading;
using codeLess.Studio;
using Xunit;

namespace codeLess.Avalonia.Tests
{
    /// <summary>
    /// QA #7: a task that fails immediately must not lose its output: the problem matcher is attached before the PTY
    /// process starts, and the exit is reported only after the process's last output has been read.
    /// </summary>
    public class TaskOutputCaptureTests
    {
        [Fact]
        public void ImmediatelyFailingTask_InAPty_ReportsItsErrorsEveryTime()
        {
            if (OperatingSystem.IsWindows()) return;
            string ws = Path.Combine(Path.GetTempPath(), "codeless-ptytask-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(ws);
            try
            {
                File.WriteAllText(Path.Combine(ws, "main.c"), "int main() { return 0 }\n");
                var task = new TaskDefinition { Label = "cc-" + Guid.NewGuid().ToString("N").Substring(0, 6), Command = "printf 'main.c:1:25: error: expected semicolon\\nmain.c:1:1: warning: unused\\n'; exit 2" };
                task.ProblemMatchers.Add("$gcc");
                var ctx = new DebugVariableContext { WorkspaceFolder = ws };
                for (int run = 0; run < 8; run++)
                {
                    var ex = TaskExecution.Prepare(task, ctx, false);
                    ex.MarkStarted();
                    var done = new ManualResetEventSlim();
                    ex.Exited += _ => done.Set();
                    string termCmd = TaskConfiguration.BuildTerminalCommandLine(ex.CommandLine, ex.Env, false, ConPtyTerminalSession.GetUserUnixShell());
                    var hooks = new PtyStartHooks { RawOutputTap = ex.OnOutputBytes, ProcessExited = code => ex.OnExited(code), InitialText = "> Executing task\r\n" };
                    using (var pty = new ConPtyTerminalSession("Task", termCmd, ex.Cwd, 100, 14, false, hooks))
                    {
                        Assert.True(done.Wait(20000), "task did not exit");
                        Assert.Equal(2, ex.ExitCode);
                        Assert.Contains("expected semicolon", ex.Transcript);
                        Assert.Equal(1, ex.ErrorCount);
                        Assert.StartsWith("> Executing task", pty.Buffer.GetFullTranscriptText().TrimStart());
                    }
                }
            }
            finally { try { Directory.Delete(ws, true); } catch { } }
        }
    }
}
