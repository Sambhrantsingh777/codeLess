using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;

namespace codeLess.Studio
{
    /// <summary>
    /// One run of a task: resolved command, problem matching over the output stream and publication of the problems
    /// to <see cref="TaskProblemStore"/> (cleared when the task starts, updated while it runs, final on exit). The UI
    /// owns the process (integrated terminal PTY, or <see cref="TaskPipeProcess"/>) and forwards output / exit here.
    /// </summary>
    public sealed class TaskExecution
    {
        public TaskDefinition Task;
        /// <summary>Command as the shell runs it (substituted, args quoted).</summary>
        public string CommandLine = "";
        public string Cwd;
        public Dictionary<string, string> Env = new Dictionary<string, string>(StringComparer.Ordinal);
        public ProblemMatcherEngine Matchers;
        public readonly TaskOutputLineSplitter Splitter = new TaskOutputLineSplitter();
        public bool IsRunning;
        public bool WasTerminated;
        public int ExitCode = -1;
        public DateTime StartedUtc;
        public DateTime EndedUtc;
        /// <summary>Set by the UI: stops the underlying process.</summary>
        public Action Terminate;
        /// <summary>Raised once when the process ends (on the thread that reported the exit).</summary>
        public event Action<TaskExecution> Exited;

        private int lastPublishTick;
        private bool publishPending;
        private readonly object gate = new object();
        private readonly StringBuilder transcript = new StringBuilder();

        public string Owner { get { return Task != null ? Task.DisplayLabel : ""; } }

        /// <summary>Plain-text output (ANSI removed), capped at 2 MB.</summary>
        public string Transcript { get { lock (gate) return transcript.ToString(); } }

        public static TaskExecution Prepare(TaskDefinition task, DebugVariableContext ctx, bool windows)
        {
            TaskExecution ex = new TaskExecution();
            ex.Task = task;
            ex.CommandLine = TaskConfiguration.BuildCommandLine(task, ctx, windows);
            ex.Cwd = TaskConfiguration.ResolveCwd(task, ctx);
            if (string.IsNullOrEmpty(ex.Cwd) || !Directory.Exists(ex.Cwd)) ex.Cwd = ctx != null ? ctx.WorkspaceFolder : null;
            ex.Env = TaskConfiguration.ResolveEnv(task, ctx);
            ex.Matchers = new ProblemMatcherEngine(BuiltInProblemMatchers.Resolve(task.ProblemMatchers), ctx != null ? ctx.WorkspaceFolder : null, ex.Cwd);
            ex.Splitter.LineReady += ex.OnLine;
            ex.Matchers.ProblemFound += delegate (DiagnosticItem d) { ex.SchedulePublish(); };
            return ex;
        }

        /// <summary>Marks the start: clears the previous problems of this task (VS Code clears on rerun).</summary>
        public void MarkStarted()
        {
            IsRunning = true;
            StartedUtc = DateTime.UtcNow;
            TaskProblemStore.ClearOwner(Owner);
        }

        private void OnLine(string line)
        {
            lock (gate)
            {
                if (transcript.Length < 2 * 1024 * 1024) transcript.Append(line).Append('\n');
            }
            Matchers.FeedLine(line);
            // Problems that arrived inside the throttle window are published by the next output line (watch tasks).
            if (HasPendingPublish) SchedulePublish();
        }

        public void OnOutputBytes(byte[] buf, int count) { Splitter.FeedBytes(buf, count); }

        public void OnOutputText(string text) { Splitter.FeedText(text); }

        private void SchedulePublish()
        {
            int now = Environment.TickCount;
            bool doIt;
            lock (gate)
            {
                doIt = unchecked(now - lastPublishTick) > 300;
                if (doIt) { lastPublishTick = now; publishPending = false; }
                else publishPending = true;
            }
            if (doIt) PublishProblems();
        }

        public void PublishProblems()
        {
            List<DiagnosticItem> snapshot;
            lock (gate) { snapshot = new List<DiagnosticItem>(Matchers.Problems); publishPending = false; }
            TaskProblemStore.Set(Owner, snapshot);
        }

        public bool HasPendingPublish { get { lock (gate) return publishPending; } }

        public void OnExited(int exitCode)
        {
            lock (gate)
            {
                if (!IsRunning && EndedUtc != default(DateTime)) return;
                IsRunning = false;
                ExitCode = exitCode;
                EndedUtc = DateTime.UtcNow;
            }
            Splitter.Flush();
            PublishProblems();
            Action<TaskExecution> h = Exited;
            if (h != null) { try { h(this); } catch { } }
        }

        public int ErrorCount
        {
            get { int n = 0; foreach (DiagnosticItem d in Matchers.Problems) if (d.Severity == "Error") n++; return n; }
        }

        public int WarningCount
        {
            get { int n = 0; foreach (DiagnosticItem d in Matchers.Problems) if (d.Severity == "Warning") n++; return n; }
        }
    }

    /// <summary>
    /// Runs a task command without a terminal (redirected pipes): used by headless tests and as a fallback when a PTY
    /// can't be created. Output is reported as text chunks; Terminate kills the whole process tree.
    /// </summary>
    public sealed class TaskPipeProcess
    {
        private Process proc;
        private volatile bool terminated;

        public int ProcessId { get { try { return proc != null ? proc.Id : -1; } catch { return -1; } } }

        public static ProcessStartInfo BuildStartInfo(string commandLine, string cwd, Dictionary<string, string> env, bool windows)
        {
            ProcessStartInfo psi;
            if (windows) psi = new ProcessStartInfo("cmd.exe", "/d /s /c \"" + commandLine + "\"");
            else psi = new ProcessStartInfo("/bin/sh", "-c " + GitCli.QuoteArg(commandLine));
            psi.UseShellExecute = false;
            psi.CreateNoWindow = true;
            psi.RedirectStandardOutput = true;
            psi.RedirectStandardError = true;
            psi.RedirectStandardInput = true;
            psi.StandardOutputEncoding = new UTF8Encoding(false);
            psi.StandardErrorEncoding = new UTF8Encoding(false);
            if (!string.IsNullOrEmpty(cwd) && Directory.Exists(cwd)) psi.WorkingDirectory = cwd;
            if (env != null) foreach (KeyValuePair<string, string> kv in env) psi.EnvironmentVariables[kv.Key] = kv.Value;
            return psi;
        }

        /// <summary>Starts the process; <paramref name="output"/> and <paramref name="exited"/> are called on background threads.</summary>
        public bool Start(string commandLine, string cwd, Dictionary<string, string> env, bool windows, Action<string> output, Action<int> exited)
        {
            try
            {
                proc = Process.Start(BuildStartInfo(commandLine, cwd, env, windows));
            }
            catch (Exception ex)
            {
                if (output != null) output("Failed to start the task: " + ex.Message + "\n");
                if (exited != null) exited(-1);
                return false;
            }
            if (proc == null) { if (exited != null) exited(-1); return false; }
            try { proc.StandardInput.Close(); } catch { }
            Process p = proc;
            object writeGate = new object();
            Thread t1 = new Thread(delegate () { Pump(p.StandardOutput, output, writeGate); });
            Thread t2 = new Thread(delegate () { Pump(p.StandardError, output, writeGate); });
            t1.IsBackground = true; t2.IsBackground = true;
            t1.Start(); t2.Start();
            Thread waiter = new Thread(delegate ()
            {
                int code = -1;
                try { p.WaitForExit(); t1.Join(5000); t2.Join(5000); code = p.ExitCode; } catch { }
                if (terminated && code == 0) code = -1;
                if (exited != null) { try { exited(code); } catch { } }
                try { p.Dispose(); } catch { }
            });
            waiter.IsBackground = true;
            waiter.Start();
            return true;
        }

        private static void Pump(StreamReader r, Action<string> output, object gate)
        {
            try
            {
                char[] buf = new char[4096];
                int n;
                while ((n = r.Read(buf, 0, buf.Length)) > 0)
                {
                    if (output == null) continue;
                    string s = new string(buf, 0, n);
                    lock (gate) { try { output(s); } catch { } }
                }
            }
            catch { }
        }

        public void Terminate()
        {
            terminated = true;
            GitProcess.KillTree(proc);
        }
    }
}
