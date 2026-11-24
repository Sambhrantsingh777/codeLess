using System;
using System.IO;

namespace codeLess.Studio
{
    public static class BinaryDetector
    {
        public static bool IsBinaryFile(string path, string content = null)
        {
            string ext = Path.GetExtension(path) != null ? Path.GetExtension(path).ToLowerInvariant() : null;
            if (ext == ".zip" || ext == ".png" || ext == ".jpg" || ext == ".jpeg" || ext == ".exe" || 
                ext == ".dll" || ext == ".so" || ext == ".class" || ext == ".jar" || ext == ".pdf")
                return true;

            // Check content
            if (content == null && File.Exists(path))
            {
                try
                {
                    using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read))
                    {
                        byte[] buffer = new byte[4096];
                        int bytesRead = fs.Read(buffer, 0, buffer.Length);
                        return IsBinaryContent(buffer, bytesRead);
                    }
                }
                catch { return false; }
            }
            else if (content != null)
            {
                byte[] bytes = System.Text.Encoding.UTF8.GetBytes(content.Length > 4096 ? content.Substring(0, 4096) : content);
                return IsBinaryContent(bytes, bytes.Length);
            }

            return false;
        }

        private static bool IsBinaryContent(byte[] buffer, int length)
        {
            if (length == 0) return false;
            int nonPrintable = 0;
            for (int i = 0; i < length; i++)
            {
                byte b = buffer[i];
                if (b == 0) return true; // NUL byte
                
                // non-printable check
                if (b < 32 && b != 9 && b != 10 && b != 13) // HT, LF, CR are ok
                {
                    nonPrintable++;
                }
                else if (b > 126 && b < 160) // extended non-printable range
                {
                    nonPrintable++;
                }
            }
            
            return (double)nonPrintable / length > 0.3;
        }
    }
}
