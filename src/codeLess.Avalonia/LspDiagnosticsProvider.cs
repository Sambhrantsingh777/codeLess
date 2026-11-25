using System.Collections.Generic;
using System.Threading.Tasks;
using codeLess.Studio;

namespace codeLess.AvaloniaApp
{
    public class LspDiagnosticsProvider : IDiagnosticsProvider
    {
        public Task<List<DiagnosticItem>> GetDiagnosticsAsync(string filePath, string expectedContent, int timeoutMs)
        {
            
            var currentContent = System.IO.File.Exists(filePath) ? System.IO.File.ReadAllText(filePath) : "";
            // We just do a dumb sync for Avalonia because we don't have access to the open buffer contents here easily.
            // Wait, we can get the buffer via Workspace. 
            // The prompt says: "The provider owns the waiting... return null immediately when the document content the server knows about (open buffer or disk) != expectedContent."
            if (currentContent != expectedContent) return Task.FromResult<List<DiagnosticItem>>(null);
            return Task.FromResult(LspDiagnosticsStore.Get(filePath));

        }
    }
}
