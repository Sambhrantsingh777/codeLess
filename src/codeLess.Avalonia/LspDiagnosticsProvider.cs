using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using codeLess.Studio;

namespace codeLess.AvaloniaApp
{
    public class LspDiagnosticsProvider : IDiagnosticsProvider
    {
        public static Func<int, Task> DelayAsync = Task.Delay;

        public async Task<List<DiagnosticItem>> GetDiagnosticsAsync(string filePath, string expectedContent, int timeoutMs)
        {
            var currentContent = System.IO.File.Exists(filePath) ? System.IO.File.ReadAllText(filePath) : "";
            if (currentContent != expectedContent) return null;
            
            var diags = LspDiagnosticsStore.Get(filePath);
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
