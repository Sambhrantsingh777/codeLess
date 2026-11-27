using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace codeLess.Studio
{
    public class FileTimeTracker
    {
        private sealed class ReadRecord
        {
            public DateTime LastReadUtc;
            public bool HasDiskSnapshot;
            public long DiskLength;
            public string DiskSha256;
            public bool HasContentSnapshot;
            public long ContentLength;
            public string ContentSha256;
            public bool HasAgentWrite;
            public long AgentLength;
            public string AgentSha256;
        }

        private readonly Dictionary<string, ReadRecord> _records =
            new Dictionary<string, ReadRecord>(StringComparer.OrdinalIgnoreCase);

        public static string ComputeSha256Hex(byte[] bytes)
        {
            if (bytes == null) return "";
            using (SHA256 sha = SHA256.Create())
            {
                byte[] hash = sha.ComputeHash(bytes);
                StringBuilder sb = new StringBuilder(hash.Length * 2);
                for (int i = 0; i < hash.Length; i++)
                    sb.Append(hash[i].ToString("x2"));
                return sb.ToString();
            }
        }

        public static string ComputeSha256Hex(string text)
        {
            return ComputeSha256Hex(Encoding.UTF8.GetBytes(text ?? ""));
        }

        public void RecordRead(string path, DateTime lastWriteUtc)
        {
            if (string.IsNullOrEmpty(path)) return;
            byte[] diskBytes = null;
            try
            {
                if (File.Exists(path))
                    diskBytes = File.ReadAllBytes(path);
            }
            catch { }
            RecordRead(path, lastWriteUtc, diskBytes);
        }

        public void RecordRead(string path, DateTime lastWriteUtc, byte[] contentBytes)
        {
            if (string.IsNullOrEmpty(path)) return;
            ReadRecord rec = new ReadRecord();
            rec.LastReadUtc = lastWriteUtc;
            if (contentBytes != null)
            {
                string hash = ComputeSha256Hex(contentBytes);
                rec.HasDiskSnapshot = true;
                rec.DiskLength = contentBytes.LongLength;
                rec.DiskSha256 = hash;
                rec.HasContentSnapshot = true;
                rec.ContentLength = contentBytes.LongLength;
                rec.ContentSha256 = hash;
            }
            _records[path] = rec;
        }

        public void RecordRead(string path, DateTime lastWriteUtc, string contentText)
        {
            if (string.IsNullOrEmpty(path)) return;
            byte[] textBytes = Encoding.UTF8.GetBytes(contentText ?? "");
            string textHash = ComputeSha256Hex(textBytes);
            ReadRecord rec = new ReadRecord();
            rec.LastReadUtc = lastWriteUtc;
            rec.HasContentSnapshot = true;
            rec.ContentLength = textBytes.LongLength;
            rec.ContentSha256 = textHash;
            try
            {
                if (File.Exists(path))
                {
                    byte[] diskBytes = File.ReadAllBytes(path);
                    rec.HasDiskSnapshot = true;
                    rec.DiskLength = diskBytes.LongLength;
                    rec.DiskSha256 = ComputeSha256Hex(diskBytes);
                }
            }
            catch { }
            _records[path] = rec;
        }

        public void RecordAgentWrite(string path, string writtenContent, DateTime lastWriteUtc)
        {
            if (string.IsNullOrEmpty(path)) return;
            byte[] writtenBytes = Encoding.UTF8.GetBytes(writtenContent ?? "");
            string writtenHash = ComputeSha256Hex(writtenBytes);

            ReadRecord rec;
            if (!_records.TryGetValue(path, out rec))
                rec = new ReadRecord();

            rec.LastReadUtc = lastWriteUtc;
            rec.HasContentSnapshot = true;
            rec.ContentLength = writtenBytes.LongLength;
            rec.ContentSha256 = writtenHash;
            rec.HasAgentWrite = true;
            rec.AgentLength = writtenBytes.LongLength;
            rec.AgentSha256 = writtenHash;

            try
            {
                if (File.Exists(path))
                {
                    byte[] diskBytes = File.ReadAllBytes(path);
                    rec.HasDiskSnapshot = true;
                    rec.DiskLength = diskBytes.LongLength;
                    rec.DiskSha256 = ComputeSha256Hex(diskBytes);
                }
            }
            catch { }

            _records[path] = rec;
        }

        public bool HasRead(string path)
        {
            return !string.IsNullOrEmpty(path) && _records.ContainsKey(path);
        }

        public DateTime GetLastReadTime(string path)
        {
            ReadRecord rec;
            return (!string.IsNullOrEmpty(path) && _records.TryGetValue(path, out rec)) ? rec.LastReadUtc : DateTime.MinValue;
        }

        public long GetLastReadLength(string path)
        {
            ReadRecord rec;
            if (!string.IsNullOrEmpty(path) && _records.TryGetValue(path, out rec))
            {
                if (rec.HasDiskSnapshot) return rec.DiskLength;
                if (rec.HasContentSnapshot) return rec.ContentLength;
            }
            return -1L;
        }

        public string GetLastReadHash(string path)
        {
            ReadRecord rec;
            if (!string.IsNullOrEmpty(path) && _records.TryGetValue(path, out rec))
            {
                if (rec.HasDiskSnapshot) return rec.DiskSha256;
                if (rec.HasContentSnapshot) return rec.ContentSha256;
            }
            return "";
        }

        public bool IsModifiedSinceRead(string path, out DateTime lastReadUtc, out DateTime currentWriteUtc)
        {
            lastReadUtc = DateTime.MinValue;
            currentWriteUtc = DateTime.MinValue;
            if (string.IsNullOrEmpty(path)) return false;
            ReadRecord rec;
            if (!_records.TryGetValue(path, out rec)) return true;
            lastReadUtc = rec.LastReadUtc;
            if (!File.Exists(path)) return false;

            currentWriteUtc = File.GetLastWriteTimeUtc(path);
            byte[] currentBytes = File.ReadAllBytes(path);
            long currentLen = currentBytes.LongLength;
            string currentHash = ComputeSha256Hex(currentBytes);

            if (rec.HasAgentWrite && currentLen == rec.AgentLength && string.Equals(currentHash, rec.AgentSha256, StringComparison.Ordinal))
            {
                rec.LastReadUtc = currentWriteUtc;
                rec.HasDiskSnapshot = true;
                rec.DiskLength = currentLen;
                rec.DiskSha256 = currentHash;
                return false;
            }

            if (currentWriteUtc > rec.LastReadUtc)
                return true;

            if (rec.HasDiskSnapshot)
            {
                if (currentLen != rec.DiskLength)
                    return true;
                if (!string.Equals(currentHash, rec.DiskSha256, StringComparison.Ordinal))
                    return true;
            }
            else if (rec.HasContentSnapshot)
            {
                if (currentLen != rec.ContentLength)
                    return true;
                if (!string.Equals(currentHash, rec.ContentSha256, StringComparison.Ordinal))
                    return true;
            }
            return false;
        }

        public void Clear()
        {
            _records.Clear();
        }
    }
}
