using System;
using System.IO;
using System.Text;
using System.Threading;

namespace codeLess.Studio
{
    public static class CrossProcessFile
    {
        public static void AtomicWrite(string path, string content, bool restrictToOwner = false)
        {
            string tmp = path + ".tmp-" + Guid.NewGuid().ToString("N");
            try
            {
                File.WriteAllText(tmp, content, new UTF8Encoding(false));
                if (restrictToOwner)
                    RestrictToOwner(tmp);
                if (File.Exists(path))
                {
                    try { File.Replace(tmp, path, null); }
                    catch { File.Copy(tmp, path, true); File.Delete(tmp); }
                }
                else
                {
                    File.Move(tmp, path);
                }
                if (restrictToOwner)
                    RestrictToOwner(path);
            }
            finally
            {
                try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
            }
        }

        [System.Runtime.InteropServices.DllImport("libc", EntryPoint = "chmod", SetLastError = true)]
        private static extern int chmod_native(byte[] path, int mode);

        private static void RestrictToOwner(string path)
        {
            try
            {
                if (Environment.OSVersion.Platform != PlatformID.Unix && Environment.OSVersion.Platform != PlatformID.MacOSX) return;
                chmod_native(Encoding.UTF8.GetBytes(path + "\0"), 384); // 0600
            }
            catch { }
        }

        public static void ReadModifyWrite(string path, Action<string> modify, int timeoutMs = 5000)
        {
            string lockPath = path + ".lock";
            var sw = System.Diagnostics.Stopwatch.StartNew();
            while (true)
            {
                FileStream fs = null;
                try
                {
                    fs = new FileStream(lockPath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                }
                catch (IOException)
                {
                    if (sw.ElapsedMilliseconds > timeoutMs)
                    {
                        try { File.Delete(lockPath); } catch { }
                        continue; // try again after force breaking lock
                    }
                    Thread.Sleep(50);
                    continue;
                }

                try
                {
                    modify(path);
                }
                finally
                {
                    try { fs.Dispose(); } catch { }
                    try { File.Delete(lockPath); } catch { }
                }
                return;
            }
        }
    }
}
