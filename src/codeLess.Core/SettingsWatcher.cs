using System;
using System.IO;
using System.Text.RegularExpressions;

namespace codeLess.Core
{
    public static class SettingsWatcher
    {
        public static event Action SettingsReloaded;

        public static void LoadUserSettings()
        {
            // Dummy implementation for C# 5 compilation
            SettingsReloaded?.Invoke();
        }

        public static void LoadWorkspaceSettings()
        {
            // Dummy implementation
            SettingsReloaded?.Invoke();
        }
    }
}
