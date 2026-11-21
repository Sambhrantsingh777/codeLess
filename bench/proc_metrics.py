"""Process-tree memory / CPU sampling for macOS (python3 stdlib only).

* Tree discovery: `ps -axo pid=,ppid=` snapshot, descendants of the root pid (helpers, shells, ...).
* RSS and CPU time: `ps -o rss=,time=` style values are coarse (1 s CPU resolution on some macOS versions), so the
  primary source is libproc's proc_pid_rusage(RUSAGE_INFO_V2) via ctypes, which works without sudo for processes
  of the same user and returns:
    - ri_resident_size   (bytes, = RSS)
    - ri_phys_footprint  (bytes, = "Memory" column in Activity Monitor / `footprint`; dirty + compressed + IOKit/GPU
                          memory attributed to the process, excludes clean shared file-backed pages)
    - ri_user_time / ri_system_time (mach absolute time units -> converted with mach_timebase_info)
  If proc_pid_rusage fails for a pid (exited, permission), `ps` values are used for that pid.
"""
import ctypes
import ctypes.util
import subprocess

_libc = ctypes.CDLL(ctypes.util.find_library("c") or "/usr/lib/libSystem.B.dylib", use_errno=True)
try:
    _libproc = ctypes.CDLL(ctypes.util.find_library("proc") or "/usr/lib/libproc.dylib", use_errno=True)
except OSError:
    _libproc = _libc

RUSAGE_INFO_V2 = 2


class _RusageInfoV2(ctypes.Structure):
    _fields_ = [
        ("ri_uuid", ctypes.c_uint8 * 16),
        ("ri_user_time", ctypes.c_uint64),
        ("ri_system_time", ctypes.c_uint64),
        ("ri_pkg_idle_wkups", ctypes.c_uint64),
        ("ri_interrupt_wkups", ctypes.c_uint64),
        ("ri_pageins", ctypes.c_uint64),
        ("ri_wired_size", ctypes.c_uint64),
        ("ri_resident_size", ctypes.c_uint64),
        ("ri_phys_footprint", ctypes.c_uint64),
        ("ri_proc_start_abstime", ctypes.c_uint64),
        ("ri_proc_exit_abstime", ctypes.c_uint64),
        ("ri_child_user_time", ctypes.c_uint64),
        ("ri_child_system_time", ctypes.c_uint64),
        ("ri_child_pkg_idle_wkups", ctypes.c_uint64),
        ("ri_child_interrupt_wkups", ctypes.c_uint64),
        ("ri_child_pageins", ctypes.c_uint64),
        ("ri_child_elapsed_abstime", ctypes.c_uint64),
        ("ri_diskio_bytesread", ctypes.c_uint64),
        ("ri_diskio_byteswritten", ctypes.c_uint64),
    ]


class _MachTimebase(ctypes.Structure):
    _fields_ = [("numer", ctypes.c_uint32), ("denom", ctypes.c_uint32)]


_tb = _MachTimebase()
try:
    _libc.mach_timebase_info(ctypes.byref(_tb))
except Exception:
    _tb.numer, _tb.denom = 1, 1
if _tb.denom == 0:
    _tb.numer, _tb.denom = 1, 1
_ABS_TO_NS = float(_tb.numer) / float(_tb.denom)

try:
    _proc_pid_rusage = _libproc.proc_pid_rusage
    _proc_pid_rusage.argtypes = [ctypes.c_int, ctypes.c_int, ctypes.c_void_p]
    _proc_pid_rusage.restype = ctypes.c_int
except AttributeError:
    _proc_pid_rusage = None


def rusage(pid):
    """dict(rss, footprint, cpu_ns, wakeups) or None."""
    if _proc_pid_rusage is None:
        return None
    info = _RusageInfoV2()
    if _proc_pid_rusage(int(pid), RUSAGE_INFO_V2, ctypes.byref(info)) != 0:
        return None
    return {
        "rss": int(info.ri_resident_size),
        "footprint": int(info.ri_phys_footprint),
        "cpu_ns": int((info.ri_user_time + info.ri_system_time) * _ABS_TO_NS),
        "idle_wakeups": int(info.ri_pkg_idle_wkups + info.ri_interrupt_wkups),
    }


def _parse_cputime(s):
    # [[dd-]hh:]mm:ss[.cc]
    s = s.strip()
    days = 0
    if "-" in s:
        d, s = s.split("-", 1)
        days = int(d)
    parts = [float(p) for p in s.split(":")]
    secs = 0.0
    for p in parts:
        secs = secs * 60 + p
    return days * 86400 + secs


def ps_table():
    """{pid: {ppid, rss_kb, cpu_s, comm}} for all processes of all users."""
    out = subprocess.run(["ps", "-axo", "pid=,ppid=,rss=,time=,comm="], stdout=subprocess.PIPE,
                         stderr=subprocess.DEVNULL, universal_newlines=True).stdout
    table = {}
    for line in out.splitlines():
        parts = line.split(None, 4)
        if len(parts) < 4:
            continue
        try:
            pid, ppid, rss = int(parts[0]), int(parts[1]), int(parts[2])
            cpu = _parse_cputime(parts[3])
        except ValueError:
            continue
        table[pid] = {"ppid": ppid, "rss_kb": rss, "cpu_s": cpu, "comm": parts[4] if len(parts) > 4 else ""}
    return table


def tree_pids(root_pid, table=None):
    table = table if table is not None else ps_table()
    if root_pid not in table:
        return []
    children = {}
    for pid, row in table.items():
        children.setdefault(row["ppid"], []).append(pid)
    result, stack = [], [root_pid]
    while stack:
        p = stack.pop()
        result.append(p)
        stack.extend(children.get(p, []))
    return result


def pids_with_marker(marker):
    """Pids whose full command line contains marker (e.g. a unique --user-data-dir). Catches helpers that were
    re-parented to launchd, such as Electron's chrome_crashpad_handler."""
    if not marker:
        return []
    out = subprocess.run(["ps", "-axww", "-o", "pid=,command="], stdout=subprocess.PIPE, stderr=subprocess.DEVNULL,
                         universal_newlines=True).stdout
    result = []
    for line in out.splitlines():
        line = line.strip()
        if marker in line:
            try:
                result.append(int(line.split(None, 1)[0]))
            except ValueError:
                pass
    return result


def app_pids(root_pid, marker=None, table=None):
    table = table if table is not None else ps_table()
    pids = tree_pids(root_pid, table)
    seen = set(pids)
    for p in pids_with_marker(marker):
        if p not in seen and p in table:
            # include the marker process and its own descendants
            for q in tree_pids(p, table):
                if q not in seen:
                    seen.add(q)
                    pids.append(q)
    return pids


def sample_tree(root_pid, marker=None):
    """Sum memory and CPU over root_pid, all descendants and marker-matched processes (see pids_with_marker).
    Returns dict with totals and per-process rows."""
    table = ps_table()
    pids = app_pids(root_pid, marker, table)
    procs = []
    tot = {"rss_bytes": 0, "footprint_bytes": 0, "cpu_ns": 0, "ps_rss_bytes": 0, "ps_cpu_s": 0.0,
           "idle_wakeups": 0, "footprint_missing": 0}
    for pid in pids:
        row = table.get(pid, {})
        ru = rusage(pid)
        rec = {"pid": pid, "ppid": row.get("ppid"), "comm": row.get("comm", ""),
               "ps_rss_bytes": row.get("rss_kb", 0) * 1024, "ps_cpu_s": row.get("cpu_s", 0.0)}
        if ru:
            rec.update({"rss_bytes": ru["rss"], "footprint_bytes": ru["footprint"], "cpu_ns": ru["cpu_ns"],
                        "idle_wakeups": ru["idle_wakeups"]})
        else:
            rec.update({"rss_bytes": rec["ps_rss_bytes"], "footprint_bytes": None,
                        "cpu_ns": int(rec["ps_cpu_s"] * 1e9), "idle_wakeups": 0})
            tot["footprint_missing"] += 1
        tot["rss_bytes"] += rec["rss_bytes"]
        tot["footprint_bytes"] += rec["footprint_bytes"] or 0
        tot["cpu_ns"] += rec["cpu_ns"]
        tot["ps_rss_bytes"] += rec["ps_rss_bytes"]
        tot["ps_cpu_s"] += rec["ps_cpu_s"]
        tot["idle_wakeups"] += rec["idle_wakeups"]
        procs.append(rec)
    tot["process_count"] = len(pids)
    tot["processes"] = procs
    return tot


def cpu_by_pid(sample):
    return dict((p["pid"], p["cpu_ns"]) for p in sample.get("processes", []))
