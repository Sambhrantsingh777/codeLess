using System;
using System.Collections.Generic;

namespace codeLess.Studio
{
    /// <summary>
    /// Static list of Python 3 standard-library top-level modules (public names of sys.stdlib_module_names, 3.8 - 3.14),
    /// with the versions that added / removed them, plus the common submodules of stdlib packages. Used when the
    /// interpreter's stdlib folder cannot be found. Strict C# 5.
    /// </summary>
    public static class PythonStdlibModules
    {
        private static readonly string[] Common =
        {
            "__future__", "abc", "argparse", "array", "ast", "asyncio", "atexit", "base64", "bdb", "binascii", "bisect",
            "builtins", "bz2", "cProfile", "calendar", "cmath", "cmd", "code", "codecs", "codeop", "collections", "colorsys",
            "compileall", "concurrent", "configparser", "contextlib", "contextvars", "copy", "copyreg", "csv", "ctypes",
            "curses", "dataclasses", "datetime", "dbm", "decimal", "difflib", "dis", "doctest", "email", "encodings",
            "ensurepip", "enum", "errno", "faulthandler", "fcntl", "filecmp", "fileinput", "fnmatch", "fractions", "ftplib",
            "functools", "gc", "getopt", "getpass", "gettext", "glob", "grp", "gzip", "hashlib", "heapq", "hmac", "html",
            "http", "idlelib", "imaplib", "importlib", "inspect", "io", "ipaddress", "itertools", "json", "keyword",
            "linecache", "locale", "logging", "lzma", "mailbox", "marshal", "math", "mimetypes", "mmap", "modulefinder",
            "msvcrt", "multiprocessing", "netrc", "ntpath", "numbers", "opcode", "operator", "optparse", "os", "pathlib",
            "pdb", "pickle", "pickletools", "pkgutil", "platform", "plistlib", "poplib", "posix", "posixpath", "pprint",
            "profile", "pstats", "pty", "pwd", "py_compile", "pyclbr", "pydoc", "pydoc_data", "pyexpat", "queue", "quopri",
            "random", "re", "readline", "reprlib", "resource", "rlcompleter", "runpy", "sched", "secrets", "select",
            "selectors", "shelve", "shlex", "shutil", "signal", "site", "smtplib", "socket", "socketserver", "sqlite3",
            "sre_compile", "sre_constants", "sre_parse", "ssl", "stat", "statistics", "string", "stringprep", "struct",
            "subprocess", "symtable", "sys", "sysconfig", "syslog", "tabnanny", "tarfile", "tempfile", "termios",
            "textwrap", "this", "threading", "time", "timeit", "tkinter", "token", "tokenize", "trace", "traceback",
            "tracemalloc", "tty", "turtle", "turtledemo", "types", "typing", "unicodedata", "unittest", "urllib", "uuid",
            "venv", "warnings", "wave", "weakref", "webbrowser", "winreg", "winsound", "wsgiref", "xml", "xmlrpc",
            "zipapp", "zipfile", "zipimport", "zlib"
        };

        // name, first version (major*100+minor), version that removed it (0 = still present)
        private static readonly object[][] Versioned =
        {
            new object[] { "graphlib", 309, 0 },
            new object[] { "zoneinfo", 309, 0 },
            new object[] { "tomllib", 311, 0 },
            new object[] { "annotationlib", 314, 0 },
            new object[] { "compression", 314, 0 },
            new object[] { "formatter", 300, 310 },
            new object[] { "parser", 300, 310 },
            new object[] { "symbol", 300, 310 },
            new object[] { "binhex", 300, 311 },
            new object[] { "asynchat", 300, 312 },
            new object[] { "asyncore", 300, 312 },
            new object[] { "distutils", 300, 312 },
            new object[] { "imp", 300, 312 },
            new object[] { "smtpd", 300, 312 },
            new object[] { "aifc", 300, 313 },
            new object[] { "audioop", 300, 313 },
            new object[] { "cgi", 300, 313 },
            new object[] { "cgitb", 300, 313 },
            new object[] { "chunk", 300, 313 },
            new object[] { "crypt", 300, 313 },
            new object[] { "imghdr", 300, 313 },
            new object[] { "lib2to3", 300, 313 },
            new object[] { "mailcap", 300, 313 },
            new object[] { "msilib", 300, 313 },
            new object[] { "nis", 300, 313 },
            new object[] { "nntplib", 300, 313 },
            new object[] { "ossaudiodev", 300, 313 },
            new object[] { "pipes", 300, 313 },
            new object[] { "sndhdr", 300, 313 },
            new object[] { "spwd", 300, 313 },
            new object[] { "sunau", 300, 313 },
            new object[] { "telnetlib", 300, 313 },
            new object[] { "uu", 300, 313 },
            new object[] { "xdrlib", 300, 313 }
        };

        private static readonly Dictionary<string, string[]> SubmoduleMap = new Dictionary<string, string[]>(StringComparer.Ordinal)
        {
            { "os", new[] { "path" } },
            { "collections", new[] { "abc" } },
            { "concurrent", new[] { "futures" } },
            { "urllib", new[] { "error", "parse", "request", "response", "robotparser" } },
            { "xml", new[] { "dom", "etree", "parsers", "sax" } },
            { "json", new[] { "decoder", "encoder", "scanner", "tool" } },
            { "http", new[] { "client", "cookiejar", "cookies", "server" } },
            { "email", new[] { "charset", "encoders", "errors", "generator", "header", "headerregistry", "iterators", "message", "mime", "parser", "policy", "utils" } },
            { "importlib", new[] { "abc", "machinery", "metadata", "resources", "util" } },
            { "unittest", new[] { "mock" } },
            { "logging", new[] { "config", "handlers" } },
            { "multiprocessing", new[] { "connection", "dummy", "managers", "pool", "process", "queues", "shared_memory", "synchronize" } },
            { "asyncio", new[] { "base_events", "events", "exceptions", "futures", "locks", "queues", "runners", "streams", "subprocess", "tasks", "timeouts" } },
            { "dbm", new[] { "dumb", "gnu", "ndbm" } },
            { "html", new[] { "entities", "parser" } },
            { "sqlite3", new[] { "dbapi2" } },
            { "xmlrpc", new[] { "client", "server" } },
            { "ctypes", new[] { "util", "wintypes" } },
            { "curses", new[] { "ascii", "panel", "textpad" } },
            { "tkinter", new[] { "colorchooser", "commondialog", "constants", "dnd", "filedialog", "font", "messagebox", "scrolledtext", "simpledialog", "ttk" } },
            { "wsgiref", new[] { "handlers", "headers", "simple_server", "util", "validate" } },
            { "encodings", new[] { "idna", "utf_8", "utf_16", "latin_1", "ascii" } }
        };

        /// <summary>Top-level stdlib modules for a "3.X" version (null = every module of 3.8+).</summary>
        public static List<string> TopLevel(string version)
        {
            int v = ParseVersion(version);
            List<string> result = new List<string>(Common);
            foreach (object[] row in Versioned)
            {
                int added = (int)row[1], removed = (int)row[2];
                bool present = v == 0 ? (removed == 0 || removed > 308) : (v >= added && (removed == 0 || v < removed));
                if (present) result.Add((string)row[0]);
            }
            result.Sort(StringComparer.OrdinalIgnoreCase);
            return result;
        }

        public static bool IsStdlib(string topLevel, string version)
        {
            return TopLevel(version).Contains(topLevel);
        }

        public static string[] Submodules(string package)
        {
            string[] s;
            return package != null && SubmoduleMap.TryGetValue(package, out s) ? s : new string[0];
        }

        private static int ParseVersion(string version)
        {
            if (string.IsNullOrEmpty(version)) return 0;
            string[] parts = version.Split('.');
            int a, b;
            if (parts.Length >= 2 && int.TryParse(parts[0], out a) && int.TryParse(parts[1], out b)) return a * 100 + b;
            return 0;
        }
    }
}
