using System;
using System.Collections.Generic;

namespace codeLess.Studio
{
    public class LineDiffRow
    {
        public const byte Equal = 0;
        public const byte Removed = 1;
        public const byte Added = 2;
        public const byte Modified = 3;

        public byte Kind;
        /// <summary>0-based line in the old text, -1 when the row has no left side.</summary>
        public int LeftLine = -1;
        /// <summary>0-based line in the new text, -1 when the row has no right side.</summary>
        public int RightLine = -1;
        public string LeftText = "";
        public string RightText = "";
    }

    /// <summary>
    /// Line diff (Myers O(ND) with common prefix/suffix trimming) producing rows for side-by-side and inline diff
    /// views. Shared by both UIs; C# 5 only.
    /// </summary>
    public static class LineDiff
    {
        /// <summary>Edit distance cap; beyond it the remaining middle is shown as one replaced block.</summary>
        public const int MaxEditDistance = 3000;

        public static string[] SplitLines(string text)
        {
            string t = (text ?? "").Replace("\r\n", "\n").Replace("\r", "\n");
            if (t.Length == 0) return new string[0];
            if (t.EndsWith("\n", StringComparison.Ordinal)) t = t.Substring(0, t.Length - 1);
            return t.Split('\n');
        }

        /// <summary>Edit script: list of (op, oldIndex, newIndex) with op 0 equal, 1 delete, 2 insert.</summary>
        public static List<int[]> ComputeEditScript(string[] a, string[] b)
        {
            List<int[]> ops = new List<int[]>();
            int n = a.Length, m = b.Length;
            int pre = 0;
            while (pre < n && pre < m && string.Equals(a[pre], b[pre], StringComparison.Ordinal)) pre++;
            int suf = 0;
            while (suf < n - pre && suf < m - pre && string.Equals(a[n - 1 - suf], b[m - 1 - suf], StringComparison.Ordinal)) suf++;
            for (int i = 0; i < pre; i++) ops.Add(new int[] { 0, i, i });

            int aStart = pre, aLen = n - pre - suf, bStart = pre, bLen = m - pre - suf;
            List<int[]> mid = MyersMiddle(a, aStart, aLen, b, bStart, bLen);
            ops.AddRange(mid);

            for (int i = 0; i < suf; i++) ops.Add(new int[] { 0, n - suf + i, m - suf + i });
            return ops;
        }

        private static List<int[]> MyersMiddle(string[] a, int aOff, int n, string[] b, int bOff, int m)
        {
            List<int[]> ops = new List<int[]>();
            if (n == 0 && m == 0) return ops;
            if (n == 0) { for (int j = 0; j < m; j++) ops.Add(new int[] { 2, -1, bOff + j }); return ops; }
            if (m == 0) { for (int i = 0; i < n; i++) ops.Add(new int[] { 1, aOff + i, -1 }); return ops; }

            // Hash lines to ints for fast comparisons.
            Dictionary<string, int> ids = new Dictionary<string, int>(StringComparer.Ordinal);
            int[] ha = new int[n];
            int[] hb = new int[m];
            for (int i = 0; i < n; i++) ha[i] = IdOf(ids, a[aOff + i]);
            for (int j = 0; j < m; j++) hb[j] = IdOf(ids, b[bOff + j]);

            int max = n + m;
            int maxD = Math.Min(max, MaxEditDistance);
            int offset = maxD + 1;
            int[] v = new int[2 * maxD + 3];
            List<int[]> trace = new List<int[]>();
            int foundD = -1;
            for (int d = 0; d <= maxD; d++)
            {
                int[] snapshot = new int[2 * d + 1];
                for (int k = -d; k <= d; k += 2)
                {
                    int x;
                    if (k == -d || (k != d && v[offset + k - 1] < v[offset + k + 1])) x = v[offset + k + 1];
                    else x = v[offset + k - 1] + 1;
                    int y = x - k;
                    while (x < n && y < m && ha[x] == hb[y]) { x++; y++; }
                    v[offset + k] = x;
                    if (x >= n && y >= m) { foundD = d; break; }
                }
                for (int k = -d; k <= d; k++) snapshot[k + d] = v[offset + k];
                trace.Add(snapshot);
                if (foundD >= 0) break;
            }

            if (foundD < 0)
            {
                // Too different: show the middle as delete-all + insert-all.
                for (int i = 0; i < n; i++) ops.Add(new int[] { 1, aOff + i, -1 });
                for (int j = 0; j < m; j++) ops.Add(new int[] { 2, -1, bOff + j });
                return ops;
            }

            // Backtrack.
            List<int[]> rev = new List<int[]>();
            int cx = n, cy = m;
            for (int d = foundD; d > 0; d--)
            {
                int[] prev = trace[d - 1];
                int k = cx - cy;
                int prevK;
                int pd = d - 1;
                int vkm1 = (k - 1 >= -pd && k - 1 <= pd) ? prev[k - 1 + pd] : -1;
                int vkp1 = (k + 1 >= -pd && k + 1 <= pd) ? prev[k + 1 + pd] : -1;
                if (k == -d || (k != d && vkm1 < vkp1)) prevK = k + 1;
                else prevK = k - 1;
                int prevX = prev[prevK + pd];
                int prevY = prevX - prevK;
                while (cx > prevX && cy > prevY)
                {
                    cx--; cy--;
                    rev.Add(new int[] { 0, aOff + cx, bOff + cy });
                }
                if (cx == prevX) { cy--; rev.Add(new int[] { 2, -1, bOff + cy }); }
                else { cx--; rev.Add(new int[] { 1, aOff + cx, -1 }); }
            }
            while (cx > 0 && cy > 0) { cx--; cy--; rev.Add(new int[] { 0, aOff + cx, bOff + cy }); }
            rev.Reverse();
            ops.AddRange(rev);
            return ops;
        }

        private static int IdOf(Dictionary<string, int> ids, string s)
        {
            int id;
            if (!ids.TryGetValue(s ?? "", out id)) { id = ids.Count; ids[s ?? ""] = id; }
            return id;
        }

        /// <summary>Side-by-side rows: adjacent delete/insert runs are paired into Modified rows.</summary>
        public static List<LineDiffRow> SideBySide(string oldText, string newText)
        {
            string[] a = SplitLines(oldText);
            string[] b = SplitLines(newText);
            List<int[]> ops = ComputeEditScript(a, b);
            List<LineDiffRow> rows = new List<LineDiffRow>();
            int i = 0;
            while (i < ops.Count)
            {
                if (ops[i][0] == 0)
                {
                    LineDiffRow r = new LineDiffRow();
                    r.Kind = LineDiffRow.Equal;
                    r.LeftLine = ops[i][1];
                    r.RightLine = ops[i][2];
                    r.LeftText = a[r.LeftLine];
                    r.RightText = b[r.RightLine];
                    rows.Add(r);
                    i++;
                    continue;
                }
                List<int> dels = new List<int>();
                List<int> ins = new List<int>();
                while (i < ops.Count && ops[i][0] != 0)
                {
                    if (ops[i][0] == 1) dels.Add(ops[i][1]); else ins.Add(ops[i][2]);
                    i++;
                }
                int pairs = Math.Max(dels.Count, ins.Count);
                for (int p = 0; p < pairs; p++)
                {
                    LineDiffRow r = new LineDiffRow();
                    if (p < dels.Count) { r.LeftLine = dels[p]; r.LeftText = a[dels[p]]; }
                    if (p < ins.Count) { r.RightLine = ins[p]; r.RightText = b[ins[p]]; }
                    r.Kind = p < dels.Count && p < ins.Count ? LineDiffRow.Modified : (p < dels.Count ? LineDiffRow.Removed : LineDiffRow.Added);
                    rows.Add(r);
                }
            }
            return rows;
        }

        /// <summary>Inline (unified) rows: removed lines first, then added lines, for each changed block.</summary>
        public static List<LineDiffRow> Inline(string oldText, string newText)
        {
            string[] a = SplitLines(oldText);
            string[] b = SplitLines(newText);
            List<int[]> ops = ComputeEditScript(a, b);
            List<LineDiffRow> rows = new List<LineDiffRow>();
            int i = 0;
            while (i < ops.Count)
            {
                if (ops[i][0] == 0)
                {
                    LineDiffRow r = new LineDiffRow();
                    r.Kind = LineDiffRow.Equal;
                    r.LeftLine = ops[i][1];
                    r.RightLine = ops[i][2];
                    r.LeftText = a[r.LeftLine];
                    r.RightText = r.LeftText;
                    rows.Add(r);
                    i++;
                    continue;
                }
                List<LineDiffRow> added = new List<LineDiffRow>();
                while (i < ops.Count && ops[i][0] != 0)
                {
                    LineDiffRow r = new LineDiffRow();
                    if (ops[i][0] == 1) { r.Kind = LineDiffRow.Removed; r.LeftLine = ops[i][1]; r.LeftText = a[r.LeftLine]; rows.Add(r); }
                    else { r.Kind = LineDiffRow.Added; r.RightLine = ops[i][2]; r.RightText = b[r.RightLine]; added.Add(r); }
                    i++;
                }
                rows.AddRange(added);
            }
            return rows;
        }

        public static void CountChanges(List<LineDiffRow> rows, out int added, out int removed)
        {
            added = 0; removed = 0;
            foreach (LineDiffRow r in rows)
            {
                if (r.Kind == LineDiffRow.Added) added++;
                else if (r.Kind == LineDiffRow.Removed) removed++;
                else if (r.Kind == LineDiffRow.Modified) { added++; removed++; }
            }
        }
    }
}
