using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using System.Net;

namespace codeLess.Studio
{
    public static class ApiRetryHandler
    {
        public static Func<int, CancellationToken, Task> DelayTaskAsync = (ms, ct) => Task.Delay(ms, ct);
        public static Action<int, int> OnRetryStatus = null; // (seconds, attempt)

        public static async Task<string> ExecuteWithRetriesAsync(Func<Task<string>> apiCall, CancellationToken cancellationToken)
        {
            // For C# 5 sync-over-async fallback or await inside harness
            // Since this class is pure Core, we can just return strings.
            // Wait, we need the response headers to check retry-after!
            // But if `apiCall` throws WebException, we can catch it and read the response.
            int attempt = 1;
            int maxAttempts = 5;

            while (true)
            {
                if (cancellationToken.IsCancellationRequested) break;

                int delayMsToWait = 0;
                try
                {
                    return await apiCall();
                }
                catch (Exception ex)
                {
                    if (attempt >= maxAttempts || cancellationToken.IsCancellationRequested)
                        throw;

                    int delayMs = GetDelayMs(ex, attempt);
                    if (delayMs < 0) throw; // Not retryable

                    if (OnRetryStatus != null)
                    {
                        OnRetryStatus(delayMs / 1000, attempt);
                    }
                    delayMsToWait = delayMs;
                }
                
                if (delayMsToWait > 0)
                {
                    if (DelayTaskAsync != null)
                    {
                        try {
                            await DelayTaskAsync(delayMsToWait, cancellationToken);
                        } catch (OperationCanceledException) {
                            break;
                        }
                    }
                    attempt++;
                }
            }
            return null;
        }

        private static int GetDelayMs(Exception ex, int attempt)
        {
            var wex = ex as WebException ?? ex.InnerException as WebException;
            if (wex != null && wex.Response != null)
            {
                var resp = wex.Response;
                int status = -1;
                if (resp is HttpWebResponse) status = (int)((HttpWebResponse)resp).StatusCode;
                else {
                    var statusProp = resp.GetType().GetProperty("StatusCode");
                    if (statusProp != null) status = (int)statusProp.GetValue(resp, null);
                }

                // 429, 500, 502, 503, 504, 529
                if (status == 429 || status == 500 || status == 502 || status == 503 || status == 504 || status == 529)
                {
                    string retryAfterMs = resp.Headers["retry-after-ms"];
                    if (!string.IsNullOrEmpty(retryAfterMs))
                    {
                        int ms;
                        if (int.TryParse(retryAfterMs, out ms)) return ms;
                    }

                    string retryAfter = resp.Headers["retry-after"];
                    if (!string.IsNullOrEmpty(retryAfter))
                    {
                        int s;
                        if (int.TryParse(retryAfter, out s)) return s * 1000;

                        DateTime dt;
                        if (DateTime.TryParse(retryAfter, out dt))
                        {
                            var span = dt.ToUniversalTime() - DateTime.UtcNow;
                            if (span.TotalMilliseconds > 0) return (int)span.TotalMilliseconds;
                        }
                    }
                }
                else
                {
                    return -1; // Not retryable status
                }
            }
            else if (ex.Message.IndexOf("overloaded", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                // retryable based on body message
            }
            else if (wex != null && wex.Status == WebExceptionStatus.Timeout)
            {
                // timeouts might be retryable
            }
            else
            {
                return -1; // Not a retryable exception
            }

            int backoff = 2000 * (int)Math.Pow(2, attempt - 1);
            if (backoff > 30000) backoff = 30000;
            return backoff;
        }
    }
}
