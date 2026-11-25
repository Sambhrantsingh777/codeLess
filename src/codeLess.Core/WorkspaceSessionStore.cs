using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace codeLess.Studio
{
    public class WorkspaceSessionState
    {
        public List<string> OpenTabPaths { get; set; }
        public string ActiveTabPath { get; set; }

        public WorkspaceSessionState()
        {
            OpenTabPaths = new List<string>();
        }
    }

    public static class WorkspaceSessionStore
    {
        private static string GetStorageDir()
        {
            string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            string overrideDir = Environment.GetEnvironmentVariable("CODELESS_SETTINGS_DIR");
            string dir = Path.Combine(!string.IsNullOrEmpty(overrideDir) ? overrideDir : Path.Combine(appData, "codeLess"), "workspaceStorage");
            Directory.CreateDirectory(dir);
            return dir;
        }

        private static string GetHash(string input)
        {
            if (string.IsNullOrEmpty(input)) return "empty";
            using (var md5 = MD5.Create())
            {
                byte[] data = md5.ComputeHash(Encoding.UTF8.GetBytes(input.TrimEnd('\\', '/').ToLowerInvariant()));
                StringBuilder sb = new StringBuilder();
                foreach (byte b in data) sb.Append(b.ToString("x2"));
                return sb.ToString();
            }
        }

        private static string GetFilePath(string workspacePath)
        {
            return Path.Combine(GetStorageDir(), GetHash(workspacePath) + ".json");
        }

        public static WorkspaceSessionState Load(string workspacePath)
        {
            string file = GetFilePath(workspacePath);
            if (File.Exists(file))
            {
                try
                {
                    string json = File.ReadAllText(file);
                    var s = new System.Web.Script.Serialization.JavaScriptSerializer();
                    var state = s.Deserialize<WorkspaceSessionState>(json);
                    if (state != null) return state;
                }
                catch { }
            }
            return new WorkspaceSessionState();
        }

        public static void Save(string workspacePath, WorkspaceSessionState state)
        {
            try
            {
                string file = GetFilePath(workspacePath);
                var s = new System.Web.Script.Serialization.JavaScriptSerializer();
                string json = s.Serialize(state);
                CrossProcessFile.AtomicWrite(file, json, false);
            }
            catch { }
        }
    }
}
