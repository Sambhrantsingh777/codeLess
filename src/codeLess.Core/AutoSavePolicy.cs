using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;

namespace codeLess.Studio
{
    /// <summary>
    /// files.autoSave semantics shared by both UIs (VS Code):
    /// <list type="bullet">
    /// <item>afterDelay — a dirty file is saved files.autoSaveDelay ms after its <b>last</b> edit (every edit restarts it).</item>
    /// <item>onFocusChange — saved when the editor loses focus: another editor/tab, any other workbench part (side bar,
    /// panel, terminal, chat, …) or another window.</item>
    /// <item>onWindowChange — saved only when the codeLess window loses focus.</item>
    /// </list>
    /// A buffer holding an inline proposal that is still under review is never auto-saved (nothing the AI proposes is
    /// written before you accept it), and neither are untitled buffers, read-only viewers or deleted files.
    /// </summary>
    public static class AutoSavePolicy
    {
        public static bool SavesOnEditorFocusLoss(IdePersistentSettings s)
        {
            return s != null && s.EffectiveAutoSaveMode == IdePersistentSettings.AutoSaveOnFocusChange;
        }

        public static bool SavesOnWindowFocusLoss(IdePersistentSettings s)
        {
            if (s == null) return false;
            string m = s.EffectiveAutoSaveMode;
            return m == IdePersistentSettings.AutoSaveOnFocusChange || m == IdePersistentSettings.AutoSaveOnWindowChange;
        }

        public static bool SavesAfterDelay(IdePersistentSettings s)
        {
            return s != null && s.EffectiveAutoSaveMode == IdePersistentSettings.AutoSaveAfterDelay;
        }

        /// <summary>True when auto save may write this buffer now.</summary>
        public static bool IsEligible(EditorTabBuffer b)
        {
            if (b == null || !b.IsDirty || b.IsUntitled || b.IsLargeFileViewer || b.IsReadOnlyDiffPreview) return false;
            if (string.IsNullOrEmpty(b.FilePath) || b.IsDeletedOnDisk) return false;
            if (IsUnderReview(b)) return false;
            try { return File.Exists(b.FilePath); } catch { return false; }
        }

        /// <summary>An inline proposal (agent, inline AI, code-block apply) is waiting for Accept / Reject.</summary>
        public static bool IsUnderReview(EditorTabBuffer b)
        {
            return b != null && (b.PreDiffContent != null || InlineDiffReview.HasPending(b) || (b.DiffHunks != null && b.DiffHunks.Exists(h => h.IsPending)));
        }
    }

    /// <summary>afterDelay bookkeeping: each edit restarts the delay of that buffer (VS Code semantics).</summary>
    public sealed class AutoSaveDebouncer
    {
        private readonly Dictionary<object, long> lastEditMs = new Dictionary<object, long>();

        public static long NowMs()
        {
            return Stopwatch.GetTimestamp() * 1000L / Stopwatch.Frequency;
        }

        public void NoteEdit(object buffer, long nowMs)
        {
            if (buffer == null) return;
            lock (lastEditMs) lastEditMs[buffer] = nowMs;
        }

        public void Forget(object buffer)
        {
            if (buffer == null) return;
            lock (lastEditMs) lastEditMs.Remove(buffer);
        }

        /// <summary>Due when the delay has passed since the last edit (buffers never seen editing are due).</summary>
        public bool IsDue(object buffer, long nowMs, int delayMs)
        {
            return RemainingMs(buffer, nowMs, delayMs) <= 0;
        }

        public long RemainingMs(object buffer, long nowMs, int delayMs)
        {
            long t;
            lock (lastEditMs)
                if (buffer == null || !lastEditMs.TryGetValue(buffer, out t)) return 0;
            return Math.Max(0, t + delayMs - nowMs);
        }
    }
}
