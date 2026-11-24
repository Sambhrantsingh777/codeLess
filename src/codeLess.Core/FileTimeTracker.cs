using System;
using System.Collections.Generic;

namespace codeLess.Studio
{
    public class FileTimeTracker
    {
        private readonly Dictionary<string, DateTime> _readTimes = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);

        public void RecordRead(string path, DateTime lastWriteUtc)
        {
            _readTimes[path] = lastWriteUtc;
        }

        public bool HasRead(string path)
        {
            return _readTimes.ContainsKey(path);
        }

        public DateTime GetLastReadTime(string path)
        {
            return _readTimes.ContainsKey(path) ? _readTimes[path] : DateTime.MinValue;
        }

        public void Clear()
        {
            _readTimes.Clear();
        }
    }
}
