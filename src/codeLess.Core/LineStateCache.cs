using System;
using System.Collections.Generic;
using System.Diagnostics;

namespace codeLess.Studio
{
    /// <summary>
    /// Incremental per-line state machine cache (TextMate tokenizer, bracket lexer).
    ///
    /// Edits are detected by comparing line string references with the last processed ones (strings are immutable, so
    /// any edit replaces the reference): the common prefix/suffix stay, the changed middle is spliced. Lines are then
    /// (re)processed lazily from the first invalid line, reusing a cached result as soon as a line's text and start
    /// state equal what was processed before ("convergence"): editing a line inside a block re-processes only that line
    /// and the lines until the state matches again. Processing can be bounded by a deadline so the UI stays responsive.
    /// STRICT C# 5.
    /// </summary>
    public abstract class LineStateCache<TState, TData> where TState : class where TData : class
    {
        private readonly List<string> texts = new List<string>();
        private readonly List<TState> startStates = new List<TState>();
        private readonly List<TState> endStates = new List<TState>();
        private readonly List<TData> datas = new List<TData>();
        private int validCount;
        private int version;
        private long processedLines;

        protected abstract TState InitialState { get; }
        protected abstract TState ProcessLine(string text, int lineIndex, TState start, out TData data);
        protected abstract bool StateEquals(TState a, TState b);

        /// <summary>Incremented whenever <see cref="Sync"/> sees a text change.</summary>
        public int Version { get { return version; } }
        /// <summary>Lines [0, ValidCount) have up-to-date end states.</summary>
        public int ValidCount { get { return validCount; } }
        public int LineCount { get { return texts.Count; } }
        /// <summary>Total ProcessLine calls (tests / diagnostics: proves incremental behaviour).</summary>
        public long ProcessedLineCount { get { return processedLines; } }

        /// <summary>Detects edits since the last call (O(lines) reference comparisons, no text comparison).</summary>
        public void Sync(IList<string> lines)
        {
            if (lines == null) return;
            int n = lines.Count, m = texts.Count;
            int min = Math.Min(n, m);
            int p = 0;
            while (p < min && ReferenceEquals(lines[p], texts[p])) p++;
            if (p == n && p == m) return;
            int s = 0;
            while (s < min - p && ReferenceEquals(lines[n - 1 - s], texts[m - 1 - s])) s++;
            int removeCount = (m - s) - p;
            int insertCount = (n - s) - p;
            int common = Math.Min(removeCount, insertCount);
            for (int k = 0; k < common; k++)
            {
                texts[p + k] = lines[p + k];
                endStates[p + k] = null;
                datas[p + k] = null;
            }
            if (removeCount > common)
            {
                int rc = removeCount - common;
                texts.RemoveRange(p + common, rc);
                startStates.RemoveRange(p + common, rc);
                endStates.RemoveRange(p + common, rc);
                datas.RemoveRange(p + common, rc);
            }
            else if (insertCount > common)
            {
                int ic = insertCount - common;
                string[] ins = new string[ic];
                for (int k = 0; k < ic; k++) ins[k] = lines[p + common + k];
                texts.InsertRange(p + common, ins);
                startStates.InsertRange(p + common, new TState[ic]);
                endStates.InsertRange(p + common, new TState[ic]);
                datas.InsertRange(p + common, new TData[ic]);
            }
            if (validCount > p) validCount = p;
            version++;
            OnInvalidated(p);
        }

        /// <summary>Hook for subclasses that keep derived caches.</summary>
        protected virtual void OnInvalidated(int firstChangedLine) { }

        /// <summary>
        /// Makes lines [0, targetLine] valid. Returns false when the deadline (Stopwatch ticks, 0 = none) was hit first.
        /// Call <see cref="Sync"/> before with the same list.
        /// </summary>
        public bool EnsureValid(IList<string> lines, int targetLine, long deadlineTicks)
        {
            int n = texts.Count;
            if (lines == null || lines.Count != n) { Sync(lines); n = texts.Count; }
            if (targetLine >= n) targetLine = n - 1;
            if (targetLine < validCount) return true;
            int i = validCount;
            int sinceCheck = 0;
            while (i <= targetLine)
            {
                TState start = i == 0 ? InitialState : endStates[i - 1];
                TState prevStart = startStates[i];
                if (endStates[i] != null && prevStart != null && (ReferenceEquals(prevStart, start) || StateEquals(prevStart, start)))
                {
                    startStates[i] = start;
                    i++;
                    continue;
                }
                if (deadlineTicks > 0 && ++sinceCheck >= 8)
                {
                    sinceCheck = 0;
                    if (Stopwatch.GetTimestamp() > deadlineTicks) break;
                }
                TData d;
                TState end = ProcessLine(texts[i], i, start, out d);
                processedLines++;
                startStates[i] = start;
                endStates[i] = end;
                datas[i] = d;
                i++;
            }
            validCount = i;
            return validCount > targetLine;
        }

        /// <summary>Start state of a valid line (null when not valid yet).</summary>
        public TState GetStartState(int line)
        {
            if (line < 0 || line >= texts.Count || line > validCount) return null;
            if (line == 0) return InitialState;
            return line <= validCount ? endStates[line - 1] : null;
        }

        public TState GetEndState(int line)
        {
            if (line < 0 || line >= validCount) return null;
            return endStates[line];
        }

        /// <summary>Per-line data of a valid line (re-processed if it was evicted); null when the line is not valid.</summary>
        public TData GetData(int line)
        {
            if (line < 0 || line >= validCount) return null;
            TData d = datas[line];
            if (d != null) return d;
            TState start = line == 0 ? InitialState : endStates[line - 1];
            ProcessLine(texts[line], line, start, out d);
            processedLines++;
            datas[line] = d;
            return d;
        }

        /// <summary>Drops cached per-line data outside [from, to] (states stay) to bound memory on big files.</summary>
        public void EvictDataOutside(int from, int to)
        {
            for (int i = 0; i < datas.Count; i++)
                if ((i < from || i > to) && datas[i] != null) datas[i] = null;
        }

        public void Reset()
        {
            texts.Clear();
            startStates.Clear();
            endStates.Clear();
            datas.Clear();
            validCount = 0;
            version++;
            OnInvalidated(0);
        }
    }
}
