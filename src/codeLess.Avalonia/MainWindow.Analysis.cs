using System;
using Avalonia.Threading;
using codeLess.Studio;

namespace codeLess.AvaloniaApp
{
    /// <summary>
    /// Outline / heuristic-diagnostics analysis off the UI thread (QA #35): small documents are analyzed inline, big
    /// ones on the thread pool; the result is applied on the UI thread only if the buffer was not edited meanwhile.
    /// </summary>
    public partial class MainWindow
    {
        private UiPost analysisUi;
        private bool windowClosed;

        /// <summary>Analyzes <paramref name="buf"/> (inline when small, else in the background) and runs <paramref name="after"/> once the result is applied.</summary>
        internal void AnalyzeBufferSoon(EditorTabBuffer buf, Action<EditorTabBuffer> after = null)
        {
            if (buf == null || windowClosed) return;
            analysisUi ??= UiPost.CaptureCurrent();
            var post = analysisUi;
            // headless (tests / automation): inline, so no result can land in a later test's dispatcher session;
            // the background path itself is covered by the core tests (BackgroundBufferAnalysis.RunOverride)
            Action<Action> toUi = AvaloniaTerminalEmulatorControl.IsDesktopRuntime
                ? a => { if (!windowClosed) post.Post(() => { if (!windowClosed) a(); }, DispatcherPriority.Background); }
                : null;
            BackgroundBufferAnalysis.Analyze(buf, toUi, b => { if (!windowClosed) after?.Invoke(b); });
        }

        /// <summary>Default follow-up: refresh the outline, problems, breadcrumbs and squiggles of the active editor.</summary>
        private void OnBufferAnalyzed(EditorTabBuffer b)
        {
            if (!ReferenceEquals(b, activeTab)) return;
            RefreshOutlineAndProblems(b);
            PrimaryEditor?.RefreshBreadcrumbs();
            PrimaryEditor?.Canvas?.InvalidateVisual();
            if (SplitEditor != null && ReferenceEquals(SplitEditor.CurrentBuffer, b)) SplitEditor.Canvas.InvalidateVisual();
        }
    }
}
