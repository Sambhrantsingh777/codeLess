using System.Collections.Generic;
using System.Threading.Tasks;

namespace codeLess.Studio
{
    public interface IDiagnosticsProvider
    {
        Task<List<DiagnosticItem>> GetDiagnosticsAsync(string filePath, string expectedContent, int timeoutMs);
    }
}
