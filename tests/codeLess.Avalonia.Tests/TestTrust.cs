using System;
using System.IO;
using System.Runtime.CompilerServices;
using codeLess.Studio;

namespace codeLess.Avalonia.Tests
{
    /// <summary>
    /// Workspace Trust isolation for the test run: decisions are stored in a per-process temp file (never the developer's
    /// real workspace-trust.json) and nothing is trusted by default - tests that need workspace-local tools, git, tasks,
    /// debugging or verification trust their own temp folders explicitly with <see cref="Trust"/>.
    /// </summary>
    public static class TestTrust
    {
        public static readonly string StoreFile = Path.Combine(Path.GetTempPath(), "codeless-test-trust-" + Environment.ProcessId, "workspace-trust.json");

        [ModuleInitializer]
        internal static void Init()
        {
            WorkspaceTrust.StorePathOverride = StoreFile;
        }

        /// <summary>Trusts <paramref name="folder"/> for this test run and returns it (fluent use in fixtures).</summary>
        public static string Trust(string folder)
        {
            if (WorkspaceTrust.StorePathOverride == null) Init();
            WorkspaceTrust.Trust(folder);
            return folder;
        }
    }
}
