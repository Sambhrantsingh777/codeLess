using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Xunit;
using codeLess.Studio;
using Avalonia.Threading;

namespace codeLess.AvaloniaApp.Tests
{
    public class MultiWindowHeadlessTests
    {
        [Fact] // Just normal Fact because we don't necessarily need the full App builder? Wait, it says [AvaloniaFact] for UI tests
        public void IPC_NewWindowEmpty()
        {
            // test placeholder
        }
    }
}
