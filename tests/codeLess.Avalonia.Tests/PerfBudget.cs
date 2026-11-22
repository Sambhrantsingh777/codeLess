using System;

namespace codeLess.Avalonia.Tests
{
    /// <summary>
    /// Timing budgets for performance assertions. Local runs use the strict budget; shared CI runners (GitHub sets CI=true)
    /// are several times slower and noisier, so the budget is scaled there — a real regression (orders of magnitude) still fails.
    /// Override with CODELESS_PERF_SCALE.
    /// </summary>
    public static class PerfBudget
    {
        public static readonly double Scale = ComputeScale();

        private static double ComputeScale()
        {
            if (double.TryParse(Environment.GetEnvironmentVariable("CODELESS_PERF_SCALE"), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double s) && s > 0) return s;
            return string.Equals(Environment.GetEnvironmentVariable("CI"), "true", StringComparison.OrdinalIgnoreCase) ? 6.0 : 1.0;
        }

        public static long Ms(long localBudgetMs) => (long)Math.Ceiling(localBudgetMs * Scale);

        /// <summary>True on CI runners (GitHub sets CI=true).</summary>
        public static bool IsCi => string.Equals(Environment.GetEnvironmentVariable("CI"), "true", StringComparison.OrdinalIgnoreCase);

        /// <summary>Scales a wait timeout (how long to wait for an async result, not a performance assertion).</summary>
        public static int Wait(int localMs) => (int)Math.Min(int.MaxValue, Math.Ceiling(localMs * Math.Max(1.0, Scale)));
    }
}
