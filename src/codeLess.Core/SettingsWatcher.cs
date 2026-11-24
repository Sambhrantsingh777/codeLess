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
            if (SettingsReloaded != null) SettingsReloaded();
        }

        public static void LoadWorkspaceSettings()
        {
            // Dummy implementation
            if (SettingsReloaded != null) SettingsReloaded();
        }
    }
}
