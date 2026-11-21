"""Minimal Chrome DevTools Protocol client (python3 stdlib only, 3.9+).

Used to read VS Code's own renderer performance marks (code/didStartWorkbench, ...) and to observe when a file's
editor has been painted, without installing websocket packages. Only what the harness needs: text frames,
request/response matching by id, Runtime.evaluate with returnByValue.
"""
import base64
import json
import os
import socket
import struct
import urllib.request


def list_targets(port, timeout=2.0):
    with urllib.request.urlopen("http://127.0.0.1:%d/json" % port, timeout=timeout) as r:
        return json.loads(r.read().decode("utf-8"))


def find_workbench_page(port):
    """Return the webSocketDebuggerUrl of the VS Code workbench page, or None if not (yet) available."""
    try:
        targets = list_targets(port)
    except Exception:
        return None
    for t in targets:
        if t.get("type") == "page" and "workbench" in (t.get("url") or ""):
            return t.get("webSocketDebuggerUrl")
    return None


class CDP(object):
    def __init__(self, ws_url, timeout=15.0):
        if not ws_url.startswith("ws://"):
            raise ValueError("unsupported url " + ws_url)
        hostport, path = ws_url[5:].split("/", 1)
        host, port = hostport.rsplit(":", 1)
        self.sock = socket.create_connection((host, int(port)), timeout=timeout)
        key = base64.b64encode(os.urandom(16)).decode("ascii")
        req = ("GET /%s HTTP/1.1\r\nHost: %s\r\nUpgrade: websocket\r\nConnection: Upgrade\r\n"
               "Sec-WebSocket-Key: %s\r\nSec-WebSocket-Version: 13\r\n\r\n") % (path, hostport, key)
        self.sock.sendall(req.encode("ascii"))
        buf = b""
        while b"\r\n\r\n" not in buf:
            chunk = self.sock.recv(4096)
            if not chunk:
                raise IOError("websocket handshake failed")
            buf += chunk
        head, self._rest = buf.split(b"\r\n\r\n", 1)
        status = head.split(b"\r\n")[0]
        if b" 101 " not in status:
            raise IOError("websocket handshake rejected: " + status.decode("latin-1"))
        self._id = 0

    def _recv_exact(self, n):
        while len(self._rest) < n:
            chunk = self.sock.recv(1 << 16)
            if not chunk:
                raise IOError("websocket closed")
            self._rest += chunk
        data, self._rest = self._rest[:n], self._rest[n:]
        return data

    def _send_text(self, text):
        data = text.encode("utf-8")
        hdr = bytearray([0x81])
        n = len(data)
        if n < 126:
            hdr.append(0x80 | n)
        elif n < 65536:
            hdr.append(0x80 | 126)
            hdr += struct.pack(">H", n)
        else:
            hdr.append(0x80 | 127)
            hdr += struct.pack(">Q", n)
        mask = os.urandom(4)
        hdr += mask
        masked = bytes(b ^ mask[i & 3] for i, b in enumerate(data))
        self.sock.sendall(bytes(hdr) + masked)

    def _recv_message(self):
        msg = b""
        while True:
            b1, b2 = self._recv_exact(2)
            n = b2 & 0x7F
            if n == 126:
                n = struct.unpack(">H", self._recv_exact(2))[0]
            elif n == 127:
                n = struct.unpack(">Q", self._recv_exact(8))[0]
            mask = self._recv_exact(4) if (b2 & 0x80) else None
            payload = self._recv_exact(n)
            if mask:
                payload = bytes(b ^ mask[i & 3] for i, b in enumerate(payload))
            op = b1 & 0x0F
            if op == 8:
                raise IOError("websocket closed by peer")
            if op in (9, 10):
                continue
            msg += payload
            if b1 & 0x80:
                return msg.decode("utf-8", "replace")

    def call(self, method, **params):
        self._id += 1
        mid = self._id
        self._send_text(json.dumps({"id": mid, "method": method, "params": params}))
        while True:
            m = json.loads(self._recv_message())
            if m.get("id") == mid:
                if "error" in m:
                    raise RuntimeError("%s failed: %s" % (method, m["error"]))
                return m.get("result", {})

    def evaluate(self, expression, await_promise=False):
        res = self.call("Runtime.evaluate", expression=expression, returnByValue=True, awaitPromise=await_promise)
        if "exceptionDetails" in res:
            raise RuntimeError("evaluate failed: " + json.dumps(res["exceptionDetails"])[:400])
        return res.get("result", {}).get("value")

    def close(self):
        # Send a proper close frame first: an abrupt disconnect can leave Chromium's DevTools HTTP endpoint stuck.
        try:
            mask = os.urandom(4)
            payload = struct.pack(">H", 1000)
            self.sock.sendall(bytes([0x88, 0x80 | len(payload)]) + mask +
                              bytes(b ^ mask[i & 3] for i, b in enumerate(payload)))
            self.sock.settimeout(1.0)
            try:
                while self.sock.recv(4096):
                    pass
            except Exception:
                pass
        except Exception:
            pass
        try:
            self.sock.close()
        except Exception:
            pass
