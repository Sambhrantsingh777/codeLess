using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;

namespace codeLess.Studio
{
    public enum VerificationStatus
    {
        NotRun = 0,
        Passed = 1,
        Failed = 2,
        TimedOut = 3,
        Cancelled = 4,
        /// <summary>The copy couldn't be built or the command couldn't start.</summary>
        Error = 5,
        /// <summary>The user denied the approval prompt.</summary>
        Denied = 6,
        /// <summary>No verification command configured or detected.</summary>
        NoCommand = 7
    }

    /// <summary>Outcome of one verification run (shown in the chat and the review banner, fed to repair turns).</summary>
    public sealed class VerificationResult
    {
        public VerificationStatus Status = VerificationStatus.NotRun;
        public int ExitCode = -1;
        public string CommandLine = "";
        public string Source = "";
        public string Kind = "custom";
        public long DurationMs;
        public long PrepareMs;
        public string Strategy = "";
        public string Message = "";
        public readonly List<DiagnosticItem> Errors = new List<DiagnosticItem>();
        public readonly List<DiagnosticItem> Warnings = new List<DiagnosticItem>();
        public readonly List<string> Notes = new List<string>();
        public string OutputTail = "";
        /// <summary>0 for the first verification of a turn, 1.. for verifications after repair turns.</summary>
        public int Attempt;

        public bool Passed { get { return Status == VerificationStatus.Passed; } }

        /// <summary>True when there is something the model could fix (a real build/test failure).</summary>
        public bool IsRepairable { get { return Status == VerificationStatus.Failed; } }

        private string Noun
        {
            get
            {
                if (Kind == "build") return "build";
                if (Kind == "test") return "tests";
                if (Kind == "check") return "check";
                return "verification";
            }
        }

        /// <summary>Short badge: "✓ build passed", "✗ 3 errors", "✗ tests failed (exit 1)", "⏱ timed out".</summary>
        public string Badge
        {
            get
            {
                switch (Status)
                {
                    case VerificationStatus.Passed: return "✓ " + Noun + " passed";
                    case VerificationStatus.Failed:
                        if (Errors.Count > 0) return "✗ " + Errors.Count + (Errors.Count == 1 ? " error" : " errors");
                        return "✗ " + Noun + " failed (exit " + ExitCode + ")";
                    case VerificationStatus.TimedOut: return "✗ timed out";
                    case VerificationStatus.Cancelled: return "verification cancelled";
                    case VerificationStatus.Denied: return "verification not allowed";
                    case VerificationStatus.NoCommand: return "no verification command";
                    case VerificationStatus.Error: return "⚠ couldn't verify";
                    default: return "not verified";
                }
            }
        }

        /// <summary>One line for status bars / banners: badge plus timing or reason.</summary>
        public string Summary
        {
            get
            {
                string s = Badge;
                if (Status == VerificationStatus.Passed || Status == VerificationStatus.Failed || Status == VerificationStatus.TimedOut)
                    s += " · " + FormatDuration(DurationMs);
                if (!string.IsNullOrEmpty(Message) && (Status == VerificationStatus.Error || Status == VerificationStatus.NoCommand || Status == VerificationStatus.Denied || Status == VerificationStatus.TimedOut))
                    s += " — " + Message;
                return s;
            }
        }

        public static string FormatDuration(long ms)
        {
            if (ms < 1000) return ms + " ms";
            if (ms < 60000) return (ms / 1000.0).ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) + " s";
            return (ms / 60000) + " min " + ((ms / 1000) % 60) + " s";
        }

        /// <summary>"src/a.cs:12:5: error CS1002: ; expected" lines (workspace-relative), capped.</summary>
        public List<string> FormatErrorLines(int max)
        {
            List<string> res = new List<string>();
            foreach (DiagnosticItem d in Errors)
            {
                if (res.Count >= max) break;
                res.Add(FormatDiagnostic(d));
            }
            if (Errors.Count > max) res.Add("... and " + (Errors.Count - max) + " more");
            return res;
        }

        public static string FormatDiagnostic(DiagnosticItem d)
        {
            string file = !string.IsNullOrEmpty(d.RelativePath) ? d.RelativePath : (d.FilePath ?? "");
            string loc = string.IsNullOrEmpty(file) ? "" : file.Replace('\\', '/') + ":" + d.Line + (d.Column > 1 ? ":" + d.Column : "") + ": ";
            return loc + (d.Severity ?? "Error").ToLowerInvariant() + ": " + (d.Message ?? "").Trim();
        }

        /// <summary>Markdown block for the chat transcript.</summary>
        public string ToMarkdown()
        {
            StringBuilder sb = new StringBuilder();
            sb.Append("**").Append(Badge).Append("**");
            if (Status == VerificationStatus.Passed || Status == VerificationStatus.Failed || Status == VerificationStatus.TimedOut)
                sb.Append(" in ").Append(FormatDuration(DurationMs));
            sb.AppendLine();
            if (!string.IsNullOrEmpty(CommandLine))
                sb.Append("`").Append(CommandLine).Append("` (").Append(Source).Append(Strategy.Length > 0 ? ", " + Strategy : "").AppendLine(")");
            if (!string.IsNullOrEmpty(Message)) sb.AppendLine(Message);
            if (Errors.Count > 0)
            {
                sb.AppendLine("```");
                foreach (string l in FormatErrorLines(12)) sb.AppendLine(l);
                sb.AppendLine("```");
            }
            else if (Status == VerificationStatus.Failed && !string.IsNullOrEmpty(OutputTail))
            {
                sb.AppendLine("```");
                sb.AppendLine(TailLines(OutputTail, 15));
                sb.AppendLine("```");
            }
            foreach (string n in Notes) sb.AppendLine("_" + n + "_");
            return sb.ToString().TrimEnd();
        }

        public static string TailLines(string text, int maxLines)
        {
            if (string.IsNullOrEmpty(text)) return "";
            string[] lines = text.Replace("\r\n", "\n").TrimEnd('\n').Split('\n');
            if (lines.Length <= maxLines) return string.Join("\n", lines);
            return string.Join("\n", lines.Skip(lines.Length - maxLines).ToArray());
        }
    }

    /// <summary>Runs a verification command with timeout, cancellation, output capture and problem matching.</summary>
    public static class VerificationRunner
    {
        /// <summary>Pytest's "FAILED path::test - message" summary lines (the $python matcher only sees tracebacks).</summary>
        public static ProblemMatcherDefinition PytestSummaryMatcher()
        {
            ProblemMatcherDefinition d = new ProblemMatcherDefinition();
            d.Name = "$codeless-pytest-summary"; d.Owner = "pytest"; d.Source = "pytest";
            d.FileLocation = "relative"; d.FileLocationBase = "${cwd}";
            d.Patterns.Add(new ProblemPattern(@"^(?:FAILED|ERROR)\s+([^\s:]+\.py)(?:::\S+)?(?:\s+-\s+(.*))?$", 1, 0, 0, 0, 0, 2));
            return d;
        }

        /// <summary>Environment that keeps verification non-interactive and leaves no build servers behind.</summary>
        public static Dictionary<string, string> VerificationEnvironment()
        {
            Dictionary<string, string> env = new Dictionary<string, string>(StringComparer.Ordinal);
            env["CI"] = "true";
            env["GIT_TERMINAL_PROMPT"] = "0";
            env["MSBUILDDISABLENODEREUSE"] = "1";
            env["DOTNET_CLI_USE_MSBUILD_SERVER"] = "0";
            env["UseSharedCompilation"] = "false";
            env["DOTNET_NOLOGO"] = "1";
            env["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1";
            env["PYTHONDONTWRITEBYTECODE"] = "1";
            env["NO_COLOR"] = "1";
            env["FORCE_COLOR"] = "0";
            env["CODELESS_VERIFY"] = "1";
            return env;
        }

        /// <summary>
        /// Runs <paramref name="cmd"/> in <paramref name="workspaceFolder"/> (the verification copy). Problem paths are
        /// translated with <paramref name="mapPath"/> (copy -> real workspace). Never throws.
        /// </summary>
        public static VerificationResult Run(VerificationCommand cmd, string workspaceFolder, int timeoutMs, CancellationToken ct,
            Action<string> onOutput, Func<string, string> mapPath)
        {
            VerificationResult res = new VerificationResult();
            if (cmd == null)
            {
                res.Status = VerificationStatus.NoCommand;
                return res;
            }
            res.Source = cmd.Source;
            res.Kind = cmd.Kind;
            bool windows = Path.DirectorySeparatorChar == '\\';
            string commandLine;
            string cwd = workspaceFolder;
            Dictionary<string, string> env = VerificationEnvironment();
            List<ProblemMatcherDefinition> defs;
            if (cmd.Task != null)
            {
                DebugVariableContext ctx = new DebugVariableContext();
                ctx.WorkspaceFolder = workspaceFolder;
                commandLine = TaskConfiguration.BuildCommandLine(cmd.Task, ctx, windows);
                cwd = TaskConfiguration.ResolveCwd(cmd.Task, ctx);
                if (string.IsNullOrEmpty(cwd) || !Directory.Exists(cwd)) cwd = workspaceFolder;
                foreach (KeyValuePair<string, string> kv in TaskConfiguration.ResolveEnv(cmd.Task, ctx)) env[kv.Key] = kv.Value;
            }
            else
            {
                commandLine = cmd.CommandLine;
            }
            res.CommandLine = cmd.CommandLine;
            defs = BuiltInProblemMatchers.Resolve(cmd.ProblemMatchers ?? new List<object>());
            if (cmd.Kind == "test" && commandLine.IndexOf("pytest", StringComparison.OrdinalIgnoreCase) >= 0) defs.Add(PytestSummaryMatcher());
            ProblemMatcherEngine engine = new ProblemMatcherEngine(defs, workspaceFolder, cwd);

            StringBuilder tail = new StringBuilder();
            object gate = new object();
            TaskOutputLineSplitter splitter = new TaskOutputLineSplitter();
            splitter.LineReady += delegate (string line)
            {
                lock (gate)
                {
                    engine.FeedLine(line);
                    tail.Append(line).Append('\n');
                    if (tail.Length > 48 * 1024) tail.Remove(0, tail.Length - 32 * 1024);
                }
            };

            ManualResetEvent done = new ManualResetEvent(false);
            int exitCode = -1;
            TaskPipeProcess proc = new TaskPipeProcess();
            Stopwatch sw = Stopwatch.StartNew();
            bool started = proc.Start(commandLine, cwd, env, windows,
                delegate (string text)
                {
                    splitter.FeedText(text);
                    if (onOutput != null) { try { onOutput(text); } catch { } }
                },
                delegate (int code) { exitCode = code; done.Set(); });
            bool timedOut = false, cancelled = false;
            if (started)
            {
                int limit = timeoutMs <= 0 ? int.MaxValue : timeoutMs;
                while (!done.WaitOne(100))
                {
                    if (ct.IsCancellationRequested) { cancelled = true; break; }
                    if (sw.ElapsedMilliseconds >= limit) { timedOut = true; break; }
                }
                if (cancelled || timedOut)
                {
                    proc.Terminate();
                    done.WaitOne(5000);
                }
            }
            else
            {
                done.WaitOne(2000);
            }
            sw.Stop();
            splitter.Flush();
            res.DurationMs = sw.ElapsedMilliseconds;
            res.ExitCode = exitCode;
            lock (gate) res.OutputTail = tail.ToString();

            HashSet<string> seen = new HashSet<string>(StringComparer.Ordinal);
            lock (gate)
            {
                foreach (DiagnosticItem d in engine.Problems)
                {
                    if (mapPath != null && !string.IsNullOrEmpty(d.FilePath)) d.FilePath = mapPath(d.FilePath);
                    string key = (d.RelativePath ?? d.FilePath ?? "") + "|" + d.Line + "|" + d.Column + "|" + d.Message;
                    if (!seen.Add(key)) continue;
                    if (d.Severity == "Error") res.Errors.Add(d);
                    else if (d.Severity == "Warning") res.Warnings.Add(d);
                }
            }

            if (!started)
            {
                res.Status = VerificationStatus.Error;
                res.Message = "The verification command could not be started.";
            }
            else if (cancelled) res.Status = VerificationStatus.Cancelled;
            else if (timedOut)
            {
                res.Status = VerificationStatus.TimedOut;
                res.Message = "stopped after " + VerificationResult.FormatDuration(timeoutMs);
            }
            else if (exitCode == 0) res.Status = VerificationStatus.Passed;
            else
            {
                res.Status = VerificationStatus.Failed;
                if (exitCode == 127 || (res.OutputTail.IndexOf("command not found", StringComparison.OrdinalIgnoreCase) >= 0 && res.Errors.Count == 0))
                    res.Message = "The command was not found. Check that the tool is installed and on PATH.";
            }
            return res;
        }
    }

    /// <summary>
    /// The verification gate: resolves the command from the real workspace, asks for approval when needed, builds a
    /// temporary copy with the staged edits applied, runs the command there and always deletes the copy. The user's
    /// files are never written. Thread-safe for concurrent use (each call has its own copy).
    /// </summary>
    public sealed class AgentVerifier
    {
        public IdePersistentSettings Settings;
        /// <summary>Called (on the calling worker thread) when approval is needed; null = deny.</summary>
        public Func<VerificationApprovalRequest, VerificationApprovalDecision> RequestApproval;
        public VerificationOverlayOptions OverlayOptions = new VerificationOverlayOptions();
        /// <summary>Overrides command resolution (tests, "verify with this command" actions).</summary>
        public VerificationCommand CommandOverride;
        /// <summary>Overrides the settings timeout (milliseconds).</summary>
        public int TimeoutMsOverride;
        private readonly object approvalGate = new object();

        public AgentVerifier(IdePersistentSettings settings)
        {
            Settings = settings;
        }

        public VerificationCommand ResolveCommand(string workspaceRoot)
        {
            if (CommandOverride != null) return CommandOverride;
            return VerificationCommandResolver.Resolve(workspaceRoot, Settings != null ? Settings.AgentVerifyCommand : null);
        }

        public int TimeoutMs
        {
            get
            {
                if (TimeoutMsOverride > 0) return TimeoutMsOverride;
                int s = Settings != null ? Settings.EffectiveAgentVerifyTimeoutSeconds : IdePersistentSettings.DefaultAgentVerifyTimeoutSeconds;
                return s * 1000;
            }
        }

        public VerificationResult Verify(string workspaceRoot, IDictionary<string, string> staged, ICollection<string> sessionApprovals,
            CancellationToken ct, Action<string> onOutput)
        {
            return Verify(workspaceRoot, staged, null, sessionApprovals, ct, onOutput);
        }

        public VerificationResult Verify(string workspaceRoot, IDictionary<string, string> staged, IEnumerable<string> deleted,
            ICollection<string> sessionApprovals, CancellationToken ct, Action<string> onOutput)
        {
            VerificationResult res;
            VerificationCommand cmd = ResolveCommand(workspaceRoot);
            if (cmd == null)
            {
                res = new VerificationResult();
                res.Status = VerificationStatus.NoCommand;
                res.Message = "No build or test command was found. Add one to .codeless/verify, set \"codeless.verifyCommand\", or configure it in Settings.";
                return res;
            }
            if (ct.IsCancellationRequested) return Cancelled(cmd);

            List<string> stagedPaths = new List<string>();
            if (staged != null) stagedPaths.AddRange(staged.Keys);
            if (deleted != null) stagedPaths.AddRange(deleted);
            // One prompt at a time (Best-of-N verifies candidates concurrently).
            lock (approvalGate)
            {
                VerificationApprovalRequest req = VerificationApprovalPolicy.Evaluate(Settings, workspaceRoot, cmd, stagedPaths, sessionApprovals);
                if (req != null)
                {
                    VerificationApprovalDecision decision = VerificationApprovalDecision.Deny;
                    if (RequestApproval != null)
                    {
                        try { decision = RequestApproval(req); } catch { decision = VerificationApprovalDecision.Deny; }
                    }
                    if (!VerificationApprovalPolicy.Apply(Settings, req, decision, sessionApprovals))
                    {
                        res = new VerificationResult();
                        res.Status = VerificationStatus.Denied;
                        res.CommandLine = cmd.CommandLine;
                        res.Source = cmd.Source;
                        res.Kind = cmd.Kind;
                        res.Message = RequestApproval == null ? "Verification needs your approval (not available here)." : "You didn't allow the verification command to run.";
                        return res;
                    }
                }
            }

            Stopwatch prep = Stopwatch.StartNew();
            VerificationOverlayOptions opts = CloneOptions(OverlayOptions, ct);
            string error;
            VerificationOverlay ov = VerificationOverlay.Create(workspaceRoot, staged, deleted, opts, out error);
            if (ov == null)
            {
                if (ct.IsCancellationRequested) return Cancelled(cmd);
                res = new VerificationResult();
                res.Status = VerificationStatus.Error;
                res.CommandLine = cmd.CommandLine;
                res.Source = cmd.Source;
                res.Kind = cmd.Kind;
                res.Message = error ?? "Couldn't prepare the verification copy.";
                return res;
            }
            try
            {
                prep.Stop();
                VerificationOverlay overlay = ov;
                res = VerificationRunner.Run(cmd, ov.OverlayWorkspace, TimeoutMs, ct, onOutput, delegate (string p) { return overlay.MapToWorkspace(p); });
                res.PrepareMs = prep.ElapsedMilliseconds;
                res.Strategy = ov.Strategy;
                res.Notes.AddRange(ov.Notes);
                // Keep transcripts free of the temporary folder name.
                res.OutputTail = (res.OutputTail ?? "").Replace(ov.OverlayWorkspace, ".");
                foreach (DiagnosticItem d in res.Errors) d.Message = (d.Message ?? "").Replace(ov.OverlayWorkspace, ".");
                return res;
            }
            finally
            {
                ov.Dispose();
            }
        }

        private static VerificationOverlayOptions CloneOptions(VerificationOverlayOptions o, CancellationToken ct)
        {
            VerificationOverlayOptions c = new VerificationOverlayOptions();
            if (o != null)
            {
                c.MaxCopyBytes = o.MaxCopyBytes;
                c.MaxCopyFiles = o.MaxCopyFiles;
                c.MaxSingleFileBytes = o.MaxSingleFileBytes;
                c.PreferGitWorktree = o.PreferGitWorktree;
            }
            c.Cancellation = ct;
            return c;
        }

        private static VerificationResult Cancelled(VerificationCommand cmd)
        {
            VerificationResult r = new VerificationResult();
            r.Status = VerificationStatus.Cancelled;
            r.CommandLine = cmd.CommandLine;
            r.Source = cmd.Source;
            r.Kind = cmd.Kind;
            return r;
        }
    }
}
