#!/usr/bin/env python3
"""codeLess vs VS Code benchmark harness (macOS, python3 stdlib only).

See bench/README.md for methodology. Quick start:

    python3 bench/run_bench.py                    # codeLess only: build into /tmp, N=5, all metrics
    python3 bench/run_bench.py -n 2 --skip-huge   # short validation run
    python3 bench/run_bench.py --apps codeless,vscode --include-vscode   # human-run comparison with VS Code

Measures, for each app, with a fresh profile per launch and the same workspace folder + file:
  * cold start (launch -> first rendered frame with the workspace and file; plus app-internal milestones)
  * idle memory after --settle seconds (sum of RSS and of phys_footprint over the whole process tree)
  * idle CPU over a --cpu-window seconds window (% of one core, whole process tree)
  * time to open a --big-mb log file (default 100 MB) and memory afterwards
  * behaviour with a --huge-mb file (default 1024 MB): outcome, time, memory, timeouts
"""
import argparse
import atexit
import datetime
import json
import os
import platform
import plistlib
import shutil
import signal
import socket
import statistics
import subprocess
import sys
import time

HERE = os.path.dirname(os.path.abspath(__file__))
REPO = os.path.dirname(HERE)
sys.path.insert(0, HERE)

import cdp_client  # noqa: E402
import gen_logfile  # noqa: E402
import proc_metrics as pm  # noqa: E402

DEFAULT_VSCODE_APP = "/Applications/Visual Studio Code.app"
DEFAULT_PUBLISH_DIR = "/tmp/cl-bench-pub"
DEFAULT_FILES_DIR = "/tmp/cl-bench-files"
DEFAULT_WORKSPACE_CLONE = "/tmp/cl-bench-ws"
DEFAULT_RUN_ROOT = "/tmp/cl-bench-run"
DEFAULT_OPEN_FILE = "src/codeLess.Avalonia/MainWindow.cs"

VSCODE_SETTINGS = {
    "update.mode": "none",
    "telemetry.telemetryLevel": "off",
    "workbench.enableExperiments": False,
    "extensions.autoCheckUpdates": False,
    "extensions.autoUpdate": False,
    "workbench.startupEditor": "none",
    "workbench.tips.enabled": False,
    "security.workspace.trust.enabled": False,
}
VSCODE_FLAGS = [
    "--disable-extensions", "--skip-welcome", "--skip-release-notes", "--disable-workspace-trust",
    "--disable-telemetry", "--disable-crash-reporter", "--disable-updates", "--disable-experiments", "--new-window",
    # The isolated $HOME has no login keychain: without these, Electron's safeStorage blocks the main thread on a
    # keychain authorization dialog. In-memory secret storage + Chromium's mock keychain avoid any keychain access.
    "--use-inmemory-secretstorage", "--use-mock-keychain",
]

LAUNCHED = []


def now_ms():
    return time.time() * 1000.0


def log(msg):
    sys.stderr.write("[%s] %s\n" % (time.strftime("%H:%M:%S"), msg))
    sys.stderr.flush()


def mb(b):
    return None if b is None else round(b / 1048576.0, 1)


def free_port():
    s = socket.socket()
    s.bind(("127.0.0.1", 0))
    port = s.getsockname()[1]
    s.close()
    return port


def load_avg():
    try:
        return [round(x, 2) for x in os.getloadavg()]
    except OSError:
        return None


def clean_env(home):
    env = dict(os.environ)
    for k in list(env.keys()):
        if k.startswith("VSCODE_") or k.startswith("ELECTRON_") or k in ("XDG_CONFIG_HOME", "XDG_DATA_HOME",
                                                                         "XDG_CACHE_HOME"):
            del env[k]
    env["HOME"] = home
    return env


def read_json(path):
    try:
        with open(path) as f:
            return json.load(f)
    except (OSError, ValueError):
        return None


def sh(cmd, **kw):
    try:
        return subprocess.run(cmd, stdout=subprocess.PIPE, stderr=subprocess.STDOUT, universal_newlines=True,
                              **kw).stdout.strip()
    except OSError as e:
        return "error: %s" % e


# ----------------------------------------------------------------------------------------------------------------
# Launched app bookkeeping / cleanup
# ----------------------------------------------------------------------------------------------------------------
class Launched(object):
    def __init__(self, app, popen, run_dir, marker, t_launch_ms, extra=None):
        self.app = app
        self.popen = popen
        self.pid = popen.pid
        self.run_dir = run_dir
        self.marker = marker
        self.t_launch_ms = t_launch_ms
        self.extra = extra or {}
        LAUNCHED.append(self)

    def alive(self):
        return self.popen.poll() is None

    def sample(self):
        s = pm.sample_tree(self.pid, self.marker)
        return s

    def kill(self):
        """SIGTERM the whole tree (+ marker-matched orphans), wait, then SIGKILL whatever is left."""
        try:
            pids = pm.app_pids(self.pid, self.marker)
        except Exception:
            pids = []
        if self.pid not in pids:
            pids.append(self.pid)
        for sig, grace in ((signal.SIGTERM, 6.0), (signal.SIGKILL, 3.0)):
            self.popen.poll()  # reap our direct child so it does not linger as a zombie
            live = [p for p in pids if p in pm.ps_table()]
            if not live:
                break
            for p in live:
                try:
                    os.kill(p, sig)
                except OSError:
                    pass
            deadline = time.time() + grace
            while time.time() < deadline:
                self.popen.poll()
                table = pm.ps_table()
                if not [p for p in live if p in table]:
                    break
                time.sleep(0.2)
        try:
            self.popen.wait(timeout=3)
        except Exception:
            pass
        for p in pm.pids_with_marker(self.marker):
            try:
                os.kill(p, signal.SIGKILL)
            except OSError:
                pass
        if self in LAUNCHED:
            LAUNCHED.remove(self)


def cleanup_all():
    for l in list(LAUNCHED):
        try:
            l.kill()
        except Exception:
            pass


def _on_signal(signum, frame):
    log("signal %d: killing launched apps" % signum)
    cleanup_all()
    sys.exit(130)


# ----------------------------------------------------------------------------------------------------------------
# codeLess adapter
# ----------------------------------------------------------------------------------------------------------------
class CodeLess(object):
    name = "codeless"
    label = "codeLess"

    def __init__(self, binary):
        if binary.endswith(".app") or binary.endswith(".app/"):
            binary = os.path.join(binary.rstrip("/"), "Contents", "MacOS", "codeLess")
        self.binary = os.path.abspath(binary)

    def info(self, built_by_harness):
        d = {"binary": self.binary, "binary_bytes": os.path.getsize(self.binary)}
        app_plist = os.path.join(os.path.dirname(os.path.dirname(self.binary)), "Info.plist")
        if os.path.exists(app_plist):
            with open(app_plist, "rb") as f:
                d["version"] = plistlib.load(f).get("CFBundleShortVersionString")
        if built_by_harness or "version" not in d:
            try:
                import re
                with open(os.path.join(REPO, "src", "codeLess.Avalonia", "codeLess.Avalonia.csproj")) as f:
                    m = re.search(r"<Version>([^<]+)</Version>", f.read())
                d.setdefault("version", m.group(1) if m else None)
            except OSError:
                pass
            d["git_sha"] = sh(["git", "-C", REPO, "rev-parse", "HEAD"])
            d["git_dirty"] = bool(sh(["git", "-C", REPO, "status", "--porcelain", "--untracked-files=no"]))
            if not built_by_harness:
                d["note"] = "external binary: version/git_sha are those of the harness checkout, not verified"
        return d

    def launch(self, run_dir, workspace, file_path, open_file=None, open_delay_ms=0):
        home = os.path.join(run_dir, "home")
        os.makedirs(home, exist_ok=True)
        env = clean_env(home)
        env["CODELESS_SETTINGS_DIR"] = os.path.join(run_dir, "settings")
        ready = os.path.join(run_dir, "ready.json")
        cmd = [self.binary, "--workspace", workspace]
        if file_path:
            cmd.append(file_path)
        cmd += ["--bench-ready", ready]
        extra = {"ready": ready}
        if open_file:
            report = os.path.join(run_dir, "open.json")
            cmd += ["--bench-open-file", open_file, "--bench-open-report", report,
                    "--bench-open-delay-ms", str(int(open_delay_ms))]
            extra["open_report"] = report
        logf = open(os.path.join(run_dir, "app.log"), "w")
        t = now_ms()
        p = subprocess.Popen(cmd, env=env, cwd=run_dir, stdout=logf, stderr=subprocess.STDOUT,
                             stdin=subprocess.DEVNULL, start_new_session=True)
        return Launched(self, p, run_dir, None, t, extra)

    def wait_ready(self, l, timeout):
        deadline = time.time() + timeout
        while time.time() < deadline:
            d = read_json(l.extra["ready"])
            if d and d.get("event") == "ready":
                return {
                    "ok": True,
                    "launch_to_first_frame_ms": round(d["epoch_ms"] - l.t_launch_ms, 1),
                    "process_start_to_first_frame_ms": d.get("since_process_start_ms"),
                    "process_start_to_main_ms": d.get("main_entered_since_process_start_ms"),
                    "frame_wait_timed_out": d.get("frame_wait_timed_out"),
                    "open_tabs": d.get("open_tabs"),
                    "active_file": d.get("active_file"),
                    "ready_epoch_ms": d["epoch_ms"],
                }
            if not l.alive():
                return {"ok": False, "error": "process exited (code %s) before ready" % l.popen.returncode}
            time.sleep(0.05)
        return {"ok": False, "error": "timeout waiting for ready (%ss)" % timeout}

    def open_file(self, l, path, marker, timeout, force_large):
        # The open is scheduled by the app itself (--bench-open-delay-ms after ready); just wait for the report.
        report = l.extra["open_report"]
        deadline = time.time() + timeout
        started = None
        while time.time() < deadline:
            d = read_json(report)
            if d:
                if d.get("event") == "open-started":
                    started = d
                elif d.get("event") == "open":
                    return {
                        "ok": bool(d.get("ok")),
                        "outcome": "opened in editor" if d.get("ok") else "failed: %s" % d.get("error"),
                        "open_ms": d.get("open_ms"),
                        "call_ms": d.get("call_ms"),
                        "error": d.get("error") or None,
                        "line_count": d.get("line_count"),
                        "is_large_file": d.get("is_large_file"),
                        "managed_heap_mb": d.get("managed_heap_mb"),
                        "frame_wait_timed_out": d.get("frame_wait_timed_out"),
                    }
            if not l.alive():
                crash = os.path.join(l.run_dir, "home", ".codeless", "crash.log")
                tail = ""
                if os.path.exists(crash):
                    with open(crash, errors="replace") as f:
                        tail = f.read()[-600:]
                return {"ok": False, "outcome": "crashed (exit code %s)" % l.popen.returncode,
                        "started": bool(started), "crash_log_tail": tail}
            time.sleep(0.1)
        return {"ok": False, "outcome": "timeout after %ss (%s)" % (
            timeout, "open started, no frame rendered - UI hung or still loading" if started else "open never started"),
            "started": bool(started)}


# ----------------------------------------------------------------------------------------------------------------
# VS Code adapter
# ----------------------------------------------------------------------------------------------------------------
READY_JS = r"""(function(){
  var marks = performance.getEntriesByType('mark').filter(function(m){return m.name.indexOf('code/')===0 && m.name.indexOf('Contribution/')<0 && m.name.indexOf('ExtensionPoint/')<0;});
  var o = {origin: performance.timeOrigin, marks: marks.map(function(m){return [m.name, m.startTime];}),
           paint: performance.getEntriesByType('paint').map(function(m){return [m.name, m.startTime];}),
           title: document.title};
  try {
    var c = globalThis.vscode && vscode.context && vscode.context.configuration && vscode.context.configuration();
    if (c && c.perfMarks) o.mainMarks = c.perfMarks.map(function(m){return [m.name, m.startTime];});
  } catch (e) { o.mainMarksError = String(e); }
  var vl = document.querySelector('.editor-group-container.active .monaco-editor .view-lines');
  o.viewLines = vl ? (vl.textContent || '').replace(/\u00a0/g, ' ').slice(0, 400) : null;
  return JSON.stringify(o);
})()"""

OPEN_OBSERVER_JS = r"""(function(name, marker, forceLarge){
  var now = function(){ return performance.timeOrigin + performance.now(); };
  var norm = function(s){ return (s || '').replace(/\u00a0/g, ' '); };
  var st = {installed: now(), title: null, tab: null, content: null, painted: null, placeholder: null,
            placeholderText: null, placeholderPainted: null, forced: null, forcedLabel: null};
  window.__clbOpen = st;
  if (window.__clbMo) { try { window.__clbMo.disconnect(); } catch (e) {} }
  function check() {
    if (st.content) return;
    var t = now();
    if (!st.title && document.title.indexOf(name) >= 0) st.title = t;
    var g = document.querySelector('.editor-group-container.active') || document;
    var tab = g.querySelector('.tab.active');
    if (!st.tab && tab && norm(tab.textContent).indexOf(name) >= 0) st.tab = t;
    var vl = g.querySelector('.monaco-editor .view-lines');
    if (vl && norm(vl.textContent).indexOf(marker) >= 0) {
      st.content = t;
      requestAnimationFrame(function(){ requestAnimationFrame(function(){ st.painted = now(); try { mo.disconnect(); } catch (e) {} }); });
      return;
    }
    var ph = g.querySelector('.editor-placeholder-label-container');
    if (ph && !st.placeholder && (st.tab || st.title)) {
      st.placeholder = t;
      st.placeholderText = norm(ph.textContent).slice(0, 300);
      requestAnimationFrame(function(){ requestAnimationFrame(function(){ st.placeholderPainted = now(); }); });
      if (forceLarge) {
        var b = g.querySelector('.editor-placeholder-buttons-container .monaco-button');
        if (b) { st.forced = now(); st.forcedLabel = norm(b.textContent); setTimeout(function(){ b.click(); }, 0); }
      }
    }
  }
  var mo = new MutationObserver(check);
  mo.observe(document.body, {subtree: true, childList: true, characterData: true});
  window.__clbMo = mo;
  check();
  return true;
})"""

OPEN_POLL_JS = r"""(function(){
  var st = window.__clbOpen || null;
  var notes = Array.prototype.slice.call(document.querySelectorAll('.notifications-toasts .notification-list-item-message'))
      .map(function(e){ return (e.textContent || '').slice(0, 300); });
  var resolves = performance.getEntriesByName('code/willResolveTextFileEditorModel').map(function(m){ return performance.timeOrigin + m.startTime; });
  var resolved = performance.getEntriesByName('code/didResolveTextFileEditorModel').map(function(m){ return performance.timeOrigin + m.startTime; });
  return JSON.stringify({st: st, notes: notes, resolves: resolves, resolved: resolved, title: document.title});
})()"""


class VSCode(object):
    name = "vscode"
    label = "VS Code"

    def __init__(self, app_path):
        self.app = app_path.rstrip("/")
        macos = os.path.join(self.app, "Contents", "MacOS")
        exe = None
        for n in ("Code", "Electron"):
            if os.path.exists(os.path.join(macos, n)):
                exe = os.path.join(macos, n)
                break
        if not exe:
            raise SystemExit("VS Code executable not found in %s" % macos)
        self.exe = exe
        self.cli = os.path.join(self.app, "Contents", "Resources", "app", "bin", "code")
        self.extra_settings = {}

    def info(self):
        d = {"app": self.app, "executable": self.exe}
        try:
            with open(os.path.join(self.app, "Contents", "Info.plist"), "rb") as f:
                pl = plistlib.load(f)
            d["version"] = pl.get("CFBundleShortVersionString")
        except OSError:
            pass
        try:
            with open(os.path.join(self.app, "Contents", "Resources", "app", "product.json")) as f:
                prod = json.load(f)
            d["commit"] = prod.get("commit")
            d["quality"] = prod.get("quality")
        except (OSError, ValueError):
            pass
        try:
            fw = os.path.join(self.app, "Contents", "Frameworks", "Electron Framework.framework", "Resources",
                              "Info.plist")
            with open(fw, "rb") as f:
                d["electron"] = plistlib.load(f).get("CFBundleVersion")
        except OSError:
            pass
        return d

    def _profile(self, run_dir):
        ud = os.path.join(run_dir, "user-data")
        ext = os.path.join(run_dir, "extensions")
        home = os.path.join(run_dir, "home")
        for d in (os.path.join(ud, "User"), ext, home):
            os.makedirs(d, exist_ok=True)
        settings = dict(VSCODE_SETTINGS)
        settings.update(self.extra_settings)
        with open(os.path.join(ud, "User", "settings.json"), "w") as f:
            json.dump(settings, f, indent=2)
        return ud, ext, home

    def launch(self, run_dir, workspace, file_path, open_file=None, open_delay_ms=0, extra_args=None, cdp=True):
        ud, ext, home = self._profile(run_dir)
        port = free_port() if cdp else None
        cmd = [self.exe, "--user-data-dir", ud, "--extensions-dir", ext] + VSCODE_FLAGS
        if port:
            cmd.append("--remote-debugging-port=%d" % port)
        cmd += list(extra_args or [])
        cmd.append(workspace)
        if file_path:
            cmd.append(file_path)
        logf = open(os.path.join(run_dir, "app.log"), "w")
        t = now_ms()
        p = subprocess.Popen(cmd, env=clean_env(home), cwd=run_dir, stdout=logf, stderr=subprocess.STDOUT,
                             stdin=subprocess.DEVNULL, start_new_session=True)
        return Launched(self, p, run_dir, ud, t, {"port": port, "user_data": ud, "extensions": ext, "home": home})

    def _connect(self, l, deadline):
        while time.time() < deadline:
            url = cdp_client.find_workbench_page(l.extra["port"])
            if url:
                try:
                    return cdp_client.CDP(url)
                except Exception:
                    pass
            if not l.alive():
                return None
            time.sleep(0.05)
        return None

    def wait_ready(self, l, timeout):
        deadline = time.time() + timeout
        c = self._connect(l, deadline)
        if not c:
            return {"ok": False, "error": "no CDP workbench page (alive=%s)" % l.alive()}
        try:
            data = None
            fcp_deadline = None
            while time.time() < deadline:
                try:
                    data = json.loads(c.evaluate(READY_JS))
                except Exception:
                    data = None
                if data:
                    names = dict((n, t) for n, t in data["marks"])
                    paint = dict(data["paint"])
                    if "code/didStartWorkbench" in names:
                        if "first-contentful-paint" in paint:
                            break
                        if fcp_deadline is None:
                            fcp_deadline = time.time() + 15
                        elif time.time() > fcp_deadline:
                            break
                # Marks are timestamped inside VS Code, so the polling rate only affects detection latency,
                # not the measured values; keep it low to avoid disturbing startup.
                time.sleep(0.25)
            if not data or "code/didStartWorkbench" not in dict((n, t) for n, t in data["marks"]):
                return {"ok": False, "error": "timeout waiting for code/didStartWorkbench"}
            origin = data["origin"]
            rmarks = dict((n, origin + t) for n, t in data["marks"])  # first occurrence wins below
            first = {}
            for n, t in data["marks"]:
                first.setdefault(n, origin + t)
            paint = dict((n, origin + t) for n, t in data["paint"])
            main = dict((n, t) for n, t in data.get("mainMarks", []))
            wb = first["code/didStartWorkbench"]
            fcp = paint.get("first-contentful-paint")
            # "First frame with workspace + file": Chromium's first-contentful-paint can happen before the editors
            # are restored (titlebar / explorer painted while the text model resolves), and later paints are not
            # observable retroactively. So use max(FCP, didStartWorkbench): a lower bound (favours VS Code by up to
            # the time to the next paint after didStartWorkbench).
            frame = max(fcp, wb) if fcp else None
            proc_start = main.get("code/timeOrigin")
            res = {
                "ok": True,
                "launch_to_first_frame_ms": round(frame - l.t_launch_ms, 1) if frame else None,
                "launch_to_first_contentful_paint_ms": round(fcp - l.t_launch_ms, 1) if fcp else None,
                "launch_to_workbench_ready_ms": round(wb - l.t_launch_ms, 1),
                "process_start_to_first_frame_ms": round(frame - proc_start, 1) if (frame and proc_start) else None,
                "process_start_to_workbench_ready_ms": round(wb - proc_start, 1) if proc_start else None,
                "vscode_own_ellapsed_ms": round(wb - main["code/didStartMain"], 1) if "code/didStartMain" in main else None,
                "renderer_start_after_launch_ms": round(origin - l.t_launch_ms, 1),
                "editors_restored_after_launch_ms": round(first["code/didRestoreEditors"] - l.t_launch_ms, 1)
                if "code/didRestoreEditors" in first else None,
                "fcp_missing": fcp is None,
                "title": data.get("title"),
                "editor_text_head": (data.get("viewLines") or "")[:120],
                "ready_epoch_ms": frame or wb,
            }
            del rmarks
            return res
        finally:
            c.close()

    def open_file(self, l, path, marker, timeout, force_large):
        deadline = time.time() + timeout
        c = self._connect(l, time.time() + 10)
        if not c:
            return {"ok": False, "outcome": "no CDP connection"}
        name = os.path.basename(path)
        try:
            c.evaluate("%s(%s, %s, %s)" % (OPEN_OBSERVER_JS, json.dumps(name), json.dumps(marker),
                                           "true" if force_large else "false"))
            env = clean_env(l.extra["home"])
            t_cli = now_ms()
            cli = subprocess.Popen([self.cli, "--user-data-dir", l.extra["user_data"], "--extensions-dir",
                                    l.extra["extensions"], "-r", path], env=env, stdout=subprocess.DEVNULL,
                                   stderr=subprocess.DEVNULL, stdin=subprocess.DEVNULL)
            cli_exit_ms = None
            last = None
            renderer_lost = False
            while time.time() < deadline:
                if cli_exit_ms is None and cli.poll() is not None:
                    cli_exit_ms = now_ms()
                try:
                    last = json.loads(c.evaluate(OPEN_POLL_JS))
                except socket.timeout:
                    # Renderer main thread busy (e.g. building a huge text model): reconnect and keep waiting.
                    c.close()
                    c = self._connect(l, time.time() + 5) or c
                    continue
                except RuntimeError:
                    pass
                except (IOError, OSError, ValueError):
                    c.close()
                    c2 = self._connect(l, time.time() + 5) if l.alive() else None
                    if not c2:
                        renderer_lost = True
                        break
                    c = c2
                    continue
                st = (last or {}).get("st") or {}
                if st.get("painted"):
                    break
                if st.get("placeholder") and not force_large and st.get("placeholderPainted"):
                    break
                time.sleep(0.05)
            if cli.poll() is None:
                try:
                    cli.kill()
                except OSError:
                    pass
            st = (last or {}).get("st") or {}
            t0_candidates = [x for x in (st.get("tab"), st.get("title")) if x]
            resolves = [x for x in (last or {}).get("resolves", []) if x >= (st.get("installed") or 0)]
            resolved = [x for x in (last or {}).get("resolved", []) if x >= (st.get("installed") or 0)]
            if resolves:
                t0_candidates.append(resolves[0])
            t0 = min(t0_candidates) if t0_candidates else None
            res = {
                "cli_to_request_seen_ms": round(t0 - t_cli, 1) if t0 else None,
                "cli_process_ms": round(cli_exit_ms - t_cli, 1) if cli_exit_ms else None,
                "model_resolve_ms": round(resolved[0] - resolves[0], 1) if (resolves and resolved) else None,
                "notifications": (last or {}).get("notes"),
                "placeholder_text": st.get("placeholderText"),
                "placeholder_ms": round(st["placeholderPainted"] - t0, 1) if (st.get("placeholderPainted") and t0) else None,
                "forced_open_clicked": st.get("forcedLabel"),
            }
            if st.get("painted"):
                res.update({"ok": True, "outcome": "opened in editor" + (" after clicking '%s'" % st["forcedLabel"]
                                                                       if st.get("forced") else ""),
                            "open_ms": round(st["painted"] - t0, 1) if t0 else None,
                            "open_from_cli_ms": round(st["painted"] - t_cli, 1)})
                if st.get("forced"):
                    res["open_after_click_ms"] = round(st["painted"] - st["forced"], 1)
            elif st.get("placeholder") and not st.get("forced"):
                res.update({"ok": False, "outcome": "refused: large-file warning shown"})
            elif renderer_lost or not l.alive():
                res.update({"ok": False, "outcome": "renderer/app died (alive=%s)" % l.alive()})
            else:
                res.update({"ok": False, "outcome": "timeout after %ss (%s)" % (
                    timeout, "warning shown, forced open did not finish" if st.get("forced") else "content never painted")})
            return res
        finally:
            c.close()

    def prof_append_timers(self, run_dir, workspace, file_path, timeout):
        """VS Code's own startup instrumentation: --prof-append-timers appends a line once the workbench is ready
        and at least 15 s have passed, then VS Code EXITS. Used only as a cross-check (separate launches)."""
        out = os.path.join(run_dir, "timers.txt")
        l = self.launch(run_dir, workspace, file_path, extra_args=["--prof-append-timers", out], cdp=False)
        try:
            deadline = time.time() + timeout
            while time.time() < deadline:
                if os.path.exists(out) and os.path.getsize(out) > 0 and not l.alive():
                    break
                time.sleep(0.2)
            if not os.path.exists(out):
                return {"ok": False, "error": "no timers line within %ss" % timeout}
            with open(out) as f:
                line = f.read().strip().splitlines()[-1]
            parts = line.split("\t")
            return {"ok": True, "ellapsed_ms": float(parts[0]), "standard_start": parts[4] if len(parts) > 4 else None,
                    "perf_baseline": parts[5] if len(parts) > 5 else None, "raw": line}
        finally:
            l.kill()


# ----------------------------------------------------------------------------------------------------------------
# Measurements
# ----------------------------------------------------------------------------------------------------------------
def summarize_sample(s):
    top = sorted(s["processes"], key=lambda p: -(p.get("footprint_bytes") or 0))[:12]
    return {
        "rss_mb": mb(s["rss_bytes"]),
        "footprint_mb": mb(s["footprint_bytes"]),
        "ps_rss_mb": mb(s["ps_rss_bytes"]),
        "process_count": s["process_count"],
        "footprint_unavailable_for": s["footprint_missing"],
        "processes": [{"pid": p["pid"], "name": os.path.basename(p["comm"] or "")[:60], "rss_mb": mb(p["rss_bytes"]),
                       "footprint_mb": mb(p.get("footprint_bytes"))} for p in top],
    }


def measure_cpu(l, window_s):
    a = l.sample()
    t0 = time.time()
    time.sleep(window_s)
    b = l.sample()
    wall = time.time() - t0
    before = dict((p["pid"], p) for p in a["processes"])
    total_ns = 0
    per = []
    for p in b["processes"]:
        prev = before.get(p["pid"])
        d = p["cpu_ns"] - (prev["cpu_ns"] if prev else 0)
        wk = p.get("idle_wakeups", 0) - (prev.get("idle_wakeups", 0) if prev else 0)
        total_ns += max(0, d)
        per.append((os.path.basename(p["comm"] or "")[:60], max(0, d), wk, p["pid"], prev is None))
    exited = [pid for pid in before if pid not in set(p["pid"] for p in b["processes"])]
    per.sort(key=lambda x: -x[1])
    wake_total = sum(max(0, x[2]) for x in per)
    return {
        "cpu_percent_of_one_core": round(total_ns / (wall * 1e9) * 100.0, 3),
        "cpu_ms_in_window": round(total_ns / 1e6, 1),
        "window_s": round(wall, 1),
        "wakeups_per_s": round(wake_total / wall, 1),
        "processes_exited_during_window": len(exited),
        "processes_started_during_window": sum(1 for x in per if x[4]),
        "top": [{"name": n, "pid": pid, "cpu_ms": round(ns / 1e6, 1), "wakeups": wk}
                for n, ns, wk, pid, _ in per[:8]],
    }


def sleep_until(epoch_s):
    d = epoch_s - time.time()
    if d > 0:
        time.sleep(d)


def run_dir_for(args, app, kind, i):
    d = os.path.join(args.run_root, "%s-%s-%d" % (app.name, kind, i))
    shutil.rmtree(d, ignore_errors=True)
    os.makedirs(d)
    return d


def keep_log(args, run_dir, tag):
    src = os.path.join(run_dir, "app.log")
    if os.path.exists(src):
        os.makedirs(os.path.join(args.results_dir, "logs"), exist_ok=True)
        shutil.copy(src, os.path.join(args.results_dir, "logs", tag + ".log"))


def idle_session(args, app, i, record=True):
    run_dir = run_dir_for(args, app, "idle", i)
    l = app.launch(run_dir, args.workspace, args.file)
    res = {"iteration": i, "load_avg_before": load_avg()}
    try:
        ready = app.wait_ready(l, args.ready_timeout)
        res["startup"] = ready
        if not ready.get("ok"):
            log("  %s #%d: NOT READY: %s" % (app.label, i, ready.get("error")))
            return res
        log("  %s #%d: first frame %.0f ms after launch" % (app.label, i, ready.get("launch_to_first_frame_ms") or -1))
        if not record:
            return res
        sleep_until(ready["ready_epoch_ms"] / 1000.0 + args.settle)
        if not l.alive():
            res["error"] = "app exited during settle"
            return res
        res["idle_memory"] = summarize_sample(l.sample())
        res["idle_cpu"] = measure_cpu(l, args.cpu_window)
        res["load_avg_after"] = load_avg()
        log("  %s #%d: idle footprint %.0f MB, RSS %.0f MB, %d procs, CPU %.2f%%" % (
            app.label, i, res["idle_memory"]["footprint_mb"], res["idle_memory"]["rss_mb"],
            res["idle_memory"]["process_count"], res["idle_cpu"]["cpu_percent_of_one_core"]))
        return res
    finally:
        l.kill()
        keep_log(args, run_dir, "%s-idle-%d" % (app.name, i))


def open_session(args, app, kind, i, path, marker, timeout):
    run_dir = run_dir_for(args, app, kind, i)
    delay_ms = int(args.open_settle * 1000)
    l = app.launch(run_dir, args.workspace, args.file, open_file=path, open_delay_ms=delay_ms)
    res = {"iteration": i, "file": path, "file_bytes": os.path.getsize(path), "load_avg_before": load_avg()}
    try:
        ready = app.wait_ready(l, args.ready_timeout)
        res["startup"] = ready
        if not ready.get("ok"):
            res["open"] = {"ok": False, "outcome": "app not ready: %s" % ready.get("error")}
            return res
        if isinstance(app, VSCode):
            sleep_until(ready["ready_epoch_ms"] / 1000.0 + args.open_settle)
        o = app.open_file(l, path, marker, timeout, args.vscode_force_open_large)
        res["open"] = o
        log("  %s %s #%d: %s, open %s ms" % (app.label, kind, i, o.get("outcome"), o.get("open_ms")))
        if l.alive() and (o.get("ok") or o.get("placeholder_text")):
            time.sleep(args.post_open_settle)
            if l.alive():
                res["memory_after_open"] = summarize_sample(l.sample())
                log("  %s %s #%d: footprint after open %.0f MB (RSS %.0f MB)" % (
                    app.label, kind, i, res["memory_after_open"]["footprint_mb"], res["memory_after_open"]["rss_mb"]))
        elif l.alive():
            res["memory_at_timeout"] = summarize_sample(l.sample())
        res["alive_at_end"] = l.alive()
        return res
    finally:
        l.kill()
        keep_log(args, run_dir, "%s-%s-%d" % (app.name, kind, i))


# ----------------------------------------------------------------------------------------------------------------
# Reporting
# ----------------------------------------------------------------------------------------------------------------
def stats(values):
    v = [x for x in values if isinstance(x, (int, float))]
    if not v:
        return None
    return {"median": round(statistics.median(v), 1), "min": round(min(v), 1), "max": round(max(v), 1), "n": len(v)}


def get(d, *path):
    for k in path:
        if not isinstance(d, dict):
            return None
        d = d.get(k)
    return d


METRICS = [
    ("Cold start: launch -> first frame with workspace + file (ms)", "idle", ("startup", "launch_to_first_frame_ms")),
    ("Cold start: OS process start -> first frame (ms)", "idle", ("startup", "process_start_to_first_frame_ms")),
    ("VS Code: launch -> workbench ready, code/didStartWorkbench (ms)", "idle", ("startup", "launch_to_workbench_ready_ms")),
    ("VS Code: launch -> first-contentful-paint (ms)", "idle", ("startup", "launch_to_first_contentful_paint_ms")),
    ("VS Code: own startup metric 'ellapsed' (didStartMain -> didStartWorkbench) (ms)", "idle", ("startup", "vscode_own_ellapsed_ms")),
    ("Idle memory, sum of phys_footprint (MB)", "idle", ("idle_memory", "footprint_mb")),
    ("Idle memory, sum of RSS (MB)", "idle", ("idle_memory", "rss_mb")),
    ("Process count", "idle", ("idle_memory", "process_count")),
    ("Idle CPU (% of one core)", "idle", ("idle_cpu", "cpu_percent_of_one_core")),
    ("Idle wakeups per second", "idle", ("idle_cpu", "wakeups_per_s")),
    ("Open BIG file: in-app request -> content painted (ms)", "big", ("open", "open_ms")),
    ("VS Code: open BIG file from `code -r` CLI spawn -> painted (ms)", "big", ("open", "open_from_cli_ms")),
    ("Memory after BIG file open, phys_footprint (MB)", "big", ("memory_after_open", "footprint_mb")),
    ("Memory after BIG file open, RSS (MB)", "big", ("memory_after_open", "rss_mb")),
]


def fmt_stat(s):
    if not s:
        return "n/a"
    if s["n"] == 1 or s["min"] == s["max"]:
        return "%g (n=%d)" % (s["median"], s["n"])
    return "%g (%g - %g, n=%d)" % (s["median"], s["min"], s["max"], s["n"])


def build_summary(results, args):
    summary = {}
    for app_name, data in results["apps"].items():
        s = {}
        for title, sess, path in METRICS:
            s[title] = stats([get(r, *path) for r in data.get(sess, [])])
        if data.get("prof_timers"):
            s["VS Code --prof-append-timers 'ellapsed' (ms, separate launches)"] = stats(
                [r.get("ellapsed_ms") for r in data["prof_timers"] if r.get("ok")])
        summary[app_name] = s
    return summary


def write_markdown(results, args, path):
    apps = list(results["apps"].keys())
    labels = dict((a, results["apps"][a]["label"]) for a in apps)
    env = results["environment"]
    L = []
    L.append("# codeLess vs VS Code benchmark (%s)" % results["started_at"])
    L.append("")
    if results.get("preliminary_note"):
        L.append("> " + results["preliminary_note"])
        L.append("")
    L.append("- Machine: %s, %s GB RAM, %s cores, macOS %s (%s)" % (
        env.get("cpu"), env.get("memory_gb"), env.get("ncpu"), env.get("macos"), env.get("macos_build")))
    L.append("- Load average at start / end: %s / %s" % (env.get("load_avg_start"), results.get("load_avg_end")))
    for a in apps:
        L.append("- %s: %s" % (labels[a], json.dumps(results["apps"][a]["info"], sort_keys=True)))
    L.append("- Iterations: %d (median, min - max). Workspace: `%s` (%s files). File open during idle: `%s`." % (
        args.iterations, args.workspace, results.get("workspace_file_count"), os.path.relpath(args.file, args.workspace)))
    L.append("- Idle settle %ss, CPU window %ss. BIG file %s MB, HUGE file %s MB." % (
        args.settle, args.cpu_window, args.big_mb, args.huge_mb))
    L.append("")
    L.append("| Metric | " + " | ".join(labels[a] for a in apps) + " |")
    L.append("|---|" + "---|" * len(apps))
    titles = [t for t, _, _ in METRICS] + ["VS Code --prof-append-timers 'ellapsed' (ms, separate launches)"]
    for t in titles:
        cells = [fmt_stat(results["summary"].get(a, {}).get(t)) for a in apps]
        if all(c == "n/a" for c in cells):
            continue
        L.append("| %s | %s |" % (t.replace("BIG", "%s MB" % args.big_mb), " | ".join(cells)))
    L.append("")
    if any(results["apps"][a].get("huge") for a in apps):
        L.append("## %s MB file" % args.huge_mb)
        L.append("")
        L.append("| App | Outcome | Time to content (ms) | Warning shown after (ms) | Footprint after (MB) | RSS after (MB) | Alive |")
        L.append("|---|---|---|---|---|---|---|")
        for a in apps:
            for r in results["apps"][a].get("huge", []):
                o = r.get("open", {})
                m = r.get("memory_after_open") or r.get("memory_at_timeout") or {}
                L.append("| %s | %s | %s | %s | %s | %s | %s |" % (
                    labels[a], o.get("outcome"), o.get("open_ms"), o.get("placeholder_ms"), m.get("footprint_mb"),
                    m.get("rss_mb"), r.get("alive_at_end")))
                if o.get("placeholder_text"):
                    L.append("|  | warning text: %s |  |  |  |  |  |" % o["placeholder_text"].replace("|", "/"))
        L.append("")
    notes = []
    for a in apps:
        for r in results["apps"][a].get("big", []):
            n = get(r, "open", "notifications")
            if n:
                notes.append("%s: %s" % (labels[a], "; ".join(n)))
    if notes:
        L.append("Notifications shown while opening the BIG file: " + " / ".join(sorted(set(notes))))
        L.append("")
    L.append("Raw per-iteration data, process lists and method notes: `results.json`. Methodology: bench/README.md.")
    with open(path, "w") as f:
        f.write("\n".join(L) + "\n")


def environment():
    def sysctl(k):
        return sh(["sysctl", "-n", k])
    try:
        memgb = round(int(sysctl("hw.memsize")) / 1073741824.0, 1)
    except ValueError:
        memgb = None
    return {
        "cpu": sysctl("machdep.cpu.brand_string"),
        "ncpu": sysctl("hw.ncpu"),
        "memory_gb": memgb,
        "macos": sh(["sw_vers", "-productVersion"]),
        "macos_build": sh(["sw_vers", "-buildVersion"]),
        "arch": platform.machine(),
        "python": platform.python_version(),
        "load_avg_start": load_avg(),
        "power_source": sh(["pmset", "-g", "batt"]).splitlines()[0] if sh(["pmset", "-g", "batt"]) else None,
    }


# ----------------------------------------------------------------------------------------------------------------
# Setup helpers
# ----------------------------------------------------------------------------------------------------------------
def build_codeless(out_dir):
    dotnet_dir = os.path.expanduser("~/.dotnet")
    env = dict(os.environ)
    if os.path.isdir(dotnet_dir):
        env["PATH"] = dotnet_dir + os.pathsep + env.get("PATH", "")
        env.setdefault("DOTNET_ROOT", dotnet_dir)
    rid = "osx-arm64" if platform.machine() == "arm64" else "osx-x64"
    cmd = ["dotnet", "publish", os.path.join(REPO, "src", "codeLess.Avalonia", "codeLess.Avalonia.csproj"),
           "-c", "Release", "-r", rid, "--self-contained", "true", "-p:PublishSingleFile=true",
           "-p:IncludeNativeLibrariesForSelfExtract=false", "-p:UseAppHost=true", "-o", out_dir, "--nologo"]
    log("building codeLess (Release, %s) into %s ..." % (rid, out_dir))
    r = subprocess.run(cmd, env=env, stdout=subprocess.PIPE, stderr=subprocess.STDOUT, universal_newlines=True)
    if r.returncode != 0:
        sys.stderr.write(r.stdout[-4000:])
        raise SystemExit("dotnet publish failed")
    return os.path.join(out_dir, "codeLess")


def prepare_workspace(args):
    if args.workspace:
        args.workspace = os.path.abspath(args.workspace)
        return
    dest = DEFAULT_WORKSPACE_CLONE
    if not os.path.isdir(os.path.join(dest, ".git")):
        log("cloning repository snapshot into %s (workspace for both apps)" % dest)
        shutil.rmtree(dest, ignore_errors=True)
        r = subprocess.run(["git", "clone", "--quiet", "--local", REPO, dest], stdout=subprocess.PIPE,
                           stderr=subprocess.STDOUT, universal_newlines=True)
        if r.returncode != 0:
            raise SystemExit("git clone failed: " + r.stdout)
    args.workspace = dest


def ensure_logfile(files_dir, megabytes):
    os.makedirs(files_dir, exist_ok=True)
    path = os.path.join(files_dir, "app-%dMB.log" % int(megabytes))
    want = int(megabytes * 1024 * 1024)
    if not (os.path.exists(path) and abs(os.path.getsize(path) - want) < 4096):
        log("generating %s ..." % path)
        gen_logfile.generate(path, megabytes)
    marker = "CLBENCH_%dMB_1" % int(megabytes)
    return path, marker


def count_files(root):
    n = 0
    for dp, dns, fns in os.walk(root):
        dns[:] = [d for d in dns if d not in (".git", "node_modules", "bin", "obj")]
        n += len(fns)
    return n


def main():
    ap = argparse.ArgumentParser(description="codeLess vs VS Code benchmark harness (macOS)")
    ap.add_argument("-n", "--iterations", type=int, default=5)
    ap.add_argument("--apps", default="codeless", help="comma list: codeless,vscode (vscode also needs --include-vscode)")
    ap.add_argument("--include-vscode", action="store_true",
                    help="explicit opt-in to launch VS Code (heavy on small machines; a human should run this, see README)")
    ap.add_argument("--codeless-bin", help="existing codeLess binary or .app (default: build into %s)" % DEFAULT_PUBLISH_DIR)
    ap.add_argument("--skip-build", action="store_true", help="reuse %s/codeLess if present" % DEFAULT_PUBLISH_DIR)
    ap.add_argument("--vscode-app", default=DEFAULT_VSCODE_APP)
    ap.add_argument("--vscode-settings", default="{}", help="extra JSON merged into the VS Code settings.json")
    ap.add_argument("--workspace", help="workspace folder (default: a git clone of this repo in %s)" % DEFAULT_WORKSPACE_CLONE)
    ap.add_argument("--file", default=DEFAULT_OPEN_FILE, help="file open in both apps (relative to workspace)")
    ap.add_argument("--settle", type=float, default=30, help="seconds after first frame before the idle memory sample")
    ap.add_argument("--cpu-window", type=float, default=30, help="idle CPU measurement window (s)")
    ap.add_argument("--ready-timeout", type=float, default=90)
    ap.add_argument("--big-mb", type=float, default=100)
    ap.add_argument("--huge-mb", type=float, default=1024)
    ap.add_argument("--huge-iterations", type=int, default=1)
    ap.add_argument("--open-timeout", type=float, default=120, help="kill + record timeout for file opens (s)")
    ap.add_argument("--open-settle", type=float, default=5, help="seconds after first frame before opening the file")
    ap.add_argument("--post-open-settle", type=float, default=10, help="seconds after open before the memory sample")
    ap.add_argument("--no-vscode-force-open-large", dest="vscode_force_open_large", action="store_false",
                    help="do not click VS Code's 'Open Anyway' on the large-file warning")
    ap.add_argument("--vscode-prof-timers", action="store_true",
                    help="extra launches with VS Code's --prof-append-timers (each takes >=15 s; VS Code then exits)")
    ap.add_argument("--skip-idle", action="store_true")
    ap.add_argument("--skip-big", action="store_true")
    ap.add_argument("--skip-huge", action="store_true")
    ap.add_argument("--no-warmup", action="store_true", help="skip the discarded first launch of each app")
    ap.add_argument("--files-dir", default=DEFAULT_FILES_DIR)
    ap.add_argument("--keep-files", action="store_true", help="keep generated big files for the next run")
    ap.add_argument("--run-root", default=DEFAULT_RUN_ROOT, help="scratch dir for per-launch profiles")
    ap.add_argument("--results-dir", help="default: bench/results/<timestamp>")
    ap.add_argument("--note", help="free-text note stored in the results (e.g. 'preliminary, machine busy')")
    args = ap.parse_args()

    if platform.system() != "Darwin":
        raise SystemExit("macOS only")
    stamp = time.strftime("%Y%m%d-%H%M%S")
    args.results_dir = os.path.abspath(args.results_dir or os.path.join(HERE, "results", stamp))
    os.makedirs(args.results_dir, exist_ok=True)
    os.makedirs(args.run_root, exist_ok=True)

    atexit.register(cleanup_all)
    for s in (signal.SIGINT, signal.SIGTERM, signal.SIGHUP):
        signal.signal(s, _on_signal)

    wanted = [a.strip() for a in args.apps.split(",") if a.strip()]
    apps = []
    built = False
    if "codeless" in wanted:
        binary = args.codeless_bin
        if not binary:
            candidate = os.path.join(DEFAULT_PUBLISH_DIR, "codeLess")
            if args.skip_build and os.path.exists(candidate):
                binary = candidate
            else:
                binary = build_codeless(DEFAULT_PUBLISH_DIR)
                built = True
        apps.append(CodeLess(binary))
    if "vscode" in wanted and not args.include_vscode:
        raise SystemExit("VS Code runs are opt-in: pass --include-vscode as well (it launches several Electron processes)")
    if "vscode" in wanted:
        v = VSCode(args.vscode_app)
        v.extra_settings = json.loads(args.vscode_settings)
        apps.append(v)
    if not apps:
        raise SystemExit("no apps selected")

    prepare_workspace(args)
    args.file = args.file if os.path.isabs(args.file) else os.path.join(args.workspace, args.file)
    if not os.path.isfile(args.file):
        raise SystemExit("file not found: %s" % args.file)

    results = {
        "started_at": datetime.datetime.now().isoformat(timespec="seconds"),
        "preliminary_note": args.note,
        "environment": environment(),
        "config": dict((k, v) for k, v in vars(args).items()),
        "workspace_file_count": count_files(args.workspace),
        "workspace_git_sha": sh(["git", "-C", args.workspace, "rev-parse", "HEAD"]),
        "vscode_settings": dict(VSCODE_SETTINGS, **json.loads(args.vscode_settings)),
        "vscode_flags": VSCODE_FLAGS,
        "apps": {},
    }
    for a in apps:
        info = a.info(built) if isinstance(a, CodeLess) else a.info()
        results["apps"][a.name] = {"label": a.label, "info": info, "idle": [], "big": [], "huge": [], "warmup": []}
    log("results -> %s" % args.results_dir)

    generated = []
    try:
        if not args.no_warmup:
            log("warm-up launch per app (discarded: first exec of a fresh binary pays macOS code-signature checks)")
            for a in apps:
                results["apps"][a.name]["warmup"].append(idle_session(args, a, 0, record=False))

        if not args.skip_idle:
            log("== cold start + idle memory/CPU (%d iterations, interleaved) ==" % args.iterations)
            for i in range(1, args.iterations + 1):
                for a in apps:
                    results["apps"][a.name]["idle"].append(idle_session(args, a, i))
            if args.vscode_prof_timers:
                for a in apps:
                    if isinstance(a, VSCode):
                        log("== VS Code --prof-append-timers cross-check ==")
                        pt = []
                        for i in range(1, args.iterations + 1):
                            r = a.prof_append_timers(run_dir_for(args, a, "proftimers", i), args.workspace, args.file, 90)
                            log("  prof-append-timers #%d: %s" % (i, r.get("ellapsed_ms") or r.get("error")))
                            pt.append(r)
                        results["apps"][a.name]["prof_timers"] = pt

        if not args.skip_big:
            path, marker = ensure_logfile(args.files_dir, args.big_mb)
            generated.append(path)
            log("== open %s MB file (%d iterations) ==" % (args.big_mb, args.iterations))
            for i in range(1, args.iterations + 1):
                for a in apps:
                    results["apps"][a.name]["big"].append(open_session(args, a, "big", i, path, marker, args.open_timeout))

        if not args.skip_huge:
            path, marker = ensure_logfile(args.files_dir, args.huge_mb)
            generated.append(path)
            log("== open %s MB file (%d iteration(s), timeout %ss) ==" % (args.huge_mb, args.huge_iterations, args.open_timeout))
            for i in range(1, args.huge_iterations + 1):
                for a in apps:
                    results["apps"][a.name]["huge"].append(open_session(args, a, "huge", i, path, marker, args.open_timeout))
    finally:
        cleanup_all()
        results["finished_at"] = datetime.datetime.now().isoformat(timespec="seconds")
        results["load_avg_end"] = load_avg()
        results["summary"] = build_summary(results, args)
        with open(os.path.join(args.results_dir, "results.json"), "w") as f:
            json.dump(results, f, indent=2, default=str)
        write_markdown(results, args, os.path.join(args.results_dir, "results.md"))
        if not args.keep_files:
            for p in generated:
                try:
                    os.remove(p)
                except OSError:
                    pass
        shutil.rmtree(args.run_root, ignore_errors=True)
        log("wrote %s and results.md" % os.path.join(args.results_dir, "results.json"))
    with open(os.path.join(args.results_dir, "results.md")) as f:
        sys.stdout.write(f.read())


if __name__ == "__main__":
    main()
