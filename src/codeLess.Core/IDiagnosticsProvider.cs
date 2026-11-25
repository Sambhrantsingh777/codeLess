using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;

namespace codeLess.Studio
{
    public interface IDiagnosticsProvider
    {
        Task<List<DiagnosticItem>> GetDiagnosticsAsync(string filePath, string expectedContent, int timeoutMs);
    }

    public class LspDiagnosticsProvider : IDiagnosticsProvider
    {
        public static Func<int, Task> DelayAsync = Task.Delay;

        public async Task<List<DiagnosticItem>> GetDiagnosticsAsync(string filePath, string expectedContent, int timeoutMs)
        {
            string currentContent = File.Exists(filePath) ? File.ReadAllText(filePath) : "";
            if (currentContent != expectedContent) return null;

            List<DiagnosticItem> diags = LspDiagnosticsStore.Get(filePath);
            if (diags.Count > 0) return diags;

            int slept = 0;
            while (slept < timeoutMs)
            {
                await DelayAsync(50);
                slept += 50;
                diags = LspDiagnosticsStore.Get(filePath);
                if (diags.Count > 0) return diags;
            }

            return diags;
        }
    }
}
