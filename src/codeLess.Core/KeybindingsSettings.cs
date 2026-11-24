using System;
using System.IO;

namespace codeLess.Studio
{
    public partial class IdePersistentSettings
    {
        public static string GetKeybindingsFilePath()
        {
            string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            string dir = Path.Combine(appData, "codeLess");
            string overrideDir = Environment.GetEnvironmentVariable("CODELESS_SETTINGS_DIR");
            if (!string.IsNullOrEmpty(overrideDir)) dir = overrideDir;
            string userDir = Path.Combine(dir, "User");
            if (!Directory.Exists(userDir)) Directory.CreateDirectory(userDir);
            return Path.Combine(dir, "User", "keybindings.json");
        }
    }
}
