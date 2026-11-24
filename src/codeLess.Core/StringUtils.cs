using System;

namespace codeLess.Studio
{
    public static class StringUtils
    {
        public static int ComputeLevenshtein(string s, string t)
        {
            int n = s.Length;
            int m = t.Length;
            if (n == 0) return m;
            if (m == 0) return n;
            int[] v0 = new int[m + 1];
            int[] v1 = new int[m + 1];
            for (int i = 0; i <= m; i++) v0[i] = i;
            for (int i = 0; i < n; i++)
            {
                v1[0] = i + 1;
                for (int j = 0; j < m; j++)
                {
                    int cost = (s[i] == t[j]) ? 0 : 1;
                    v1[j + 1] = Math.Min(v1[j] + 1, Math.Min(v0[j + 1] + 1, v0[j] + cost));
                }
                for (int j = 0; j <= m; j++) v0[j] = v1[j];
            }
            return v1[m];
        }
    }
}
