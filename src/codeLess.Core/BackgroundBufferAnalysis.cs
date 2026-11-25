using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;

namespace codeLess.Studio
{
    /// <summary>Outline symbols and heuristic diagnostics computed from one snapshot of a buffer.</summary>
    public sealed class BufferAnalysisResult
    {
        public List<AstSymbolItem> Symbols = new List<AstSymbolItem>();
        public List<DiagnosticItem> Diagnostics = new List<DiagnosticItem>();
        /// <summary>Buffer state the result describes (set by the caller that took the snapshot).</summary>
        public int EditVersion = -1;
        public string Language;
        public int LineCount = -1;
    }

    public partial class EditorTabBuffer
    {
        /// <summary>Newest background analysis request (older results are dropped).</summary>
        internal int AnalysisRequestSeq;
        /// <summary>True when the user explicitly switched the buffer's language mode in the picker.</summary>
        public bool LanguageModeOverridden;
        /// <summary>EditVersion the outline / heuristic diagnostics were last computed for (-1: never).</summary>
        public int AnalyzedEditVersion = -1;
        internal string AnalyzedLanguage;
        internal int AnalyzedLineCount = -1;

        /// <summary>True when Symbols / Diagnostics describe the current text and language (re-analysis can be skipped).</summary>
        public bool IsAnalysisCurrent
        {
            get
            {
                return AnalyzedEditVersion >= 0 && AnalyzedEditVersion == EditVersion && Lines != null && AnalyzedLineCount == Lines.Count &&
                       string.Equals(AnalyzedLanguage ?? "", Language ?? "", StringComparison.Ordinal);
            }
        }
    }

    /// <summary>
    /// Runs the heuristic AST / diagnostics pass (<see cref="LlmHarnessAndAgentEngine.ComputeBufferAstAndDiagnostics"/>)
    /// on the thread pool (QA #35: it ran on the UI thread, ~tens of ms per pause on big files). The UI thread only
    /// copies the line references (strings are immutable) and later swaps the result in; a result is applied only when
    /// the buffer still has the same <see cref="EditorTabBuffer.EditVersion"/>, language and line count and no newer
    /// request was made, so stale results never overwrite fresh ones. STRICT C# 5 (shared with the Windows build).
    /// </summary>
    public static class BackgroundBufferAnalysis
    {
        /// <summary>Documents up to this many lines are analyzed synchronously (cheaper than a thread hop).</summary>
        public static int SyncLineLimit = 250;

        /// <summary>Test hook: when set, the background computation runs through this instead of the thread pool.</summary>
        public static Action<Action> RunOverride;

        private static long lastUiTicks, lastComputeTicks;

        /// <summary>UI-thread time (snapshot + apply) of the last background analysis, in milliseconds.</summary>
        public static double LastUiThreadMs { get { return Interlocked.Read(ref lastUiTicks) * 1000.0 / Stopwatch.Frequency; } }

        /// <summary>Background compute time of the last analysis, in milliseconds.</summary>
        public static double LastComputeMs { get { return Interlocked.Read(ref lastComputeTicks) * 1000.0 / Stopwatch.Frequency; } }

        /// <summary>
        /// Analyzes <paramref name="buf"/>: synchronously for small documents (returns true, <paramref name="onApplied"/>
        /// already ran), otherwise on a background thread with the result posted back through
        /// <paramref name="postToUi"/> (returns false). <paramref name="onApplied"/> runs on the UI thread only when the
        /// result was applied.
        /// </summary>
        public static bool Analyze(EditorTabBuffer buf, Action<Action> postToUi, Action<EditorTabBuffer> onApplied)
        {
            if (buf == null) return true;
            LlmHarnessAndAgentEngine.PrepareBufferForAnalysis(buf);
            int count = buf.Lines != null ? buf.Lines.Count : 0;
            if (postToUi == null || count <= SyncLineLimit)
            {
                unchecked { buf.AnalysisRequestSeq++; }
                LlmHarnessAndAgentEngine.AnalyzeBufferAstAndDiagnostics(buf);
                if (onApplied != null) onApplied(buf);
                return true;
            }
            Schedule(buf, postToUi, onApplied);
            return false;
        }

        /// <summary>Always background (see <see cref="Analyze"/>).</summary>
        public static void Schedule(EditorTabBuffer buf, Action<Action> postToUi, Action<EditorTabBuffer> onApplied)
        {
            if (buf == null || postToUi == null) return;
            long t0 = Stopwatch.GetTimestamp();
            LlmHarnessAndAgentEngine.PrepareBufferForAnalysis(buf);
            int seq;
            unchecked { seq = ++buf.AnalysisRequestSeq; }
            string[] snapshot = buf.Lines.ToArray();
            int version = buf.EditVersion;
            string language = buf.Language;
            string filePath = buf.FilePath;
            string relPath = buf.RelativePath ?? buf.FileName;
            long snapTicks = Stopwatch.GetTimestamp() - t0;
            Action work = delegate
            {
                BufferAnalysisResult r;
                long c0 = Stopwatch.GetTimestamp();
                try { r = LlmHarnessAndAgentEngine.ComputeBufferAstAndDiagnostics(snapshot, language, filePath, relPath); }
                catch { return; }
                r.EditVersion = version;
                r.Language = language;
                r.LineCount = snapshot.Length;
                Interlocked.Exchange(ref lastComputeTicks, Stopwatch.GetTimestamp() - c0);
                postToUi(delegate
                {
                    long a0 = Stopwatch.GetTimestamp();
                    if (!IsCurrent(buf, seq, version, language, snapshot.Length)) return;
                    LlmHarnessAndAgentEngine.ApplyBufferAnalysis(buf, r);
                    Interlocked.Exchange(ref lastUiTicks, snapTicks + (Stopwatch.GetTimestamp() - a0));
                    if (onApplied != null) onApplied(buf);
                });
            };
            if (RunOverride != null) RunOverride(work);
            else ThreadPool.QueueUserWorkItem(delegate { work(); });
        }

        /// <summary>Whether a result computed for (seq, version, language, line count) may still be applied.</summary>
        public static bool IsCurrent(EditorTabBuffer buf, int seq, int version, string language, int lineCount)
        {
            if (buf == null || buf.Lines == null) return false;
            if (buf.AnalysisRequestSeq != seq) return false;          // a newer request supersedes this one
            if (buf.EditVersion != version) return false;             // edited since the snapshot
            if (buf.Lines.Count != lineCount) return false;           // edited without a version bump
            return string.Equals(buf.Language ?? "", language ?? "", StringComparison.Ordinal);
        }
    }
}
