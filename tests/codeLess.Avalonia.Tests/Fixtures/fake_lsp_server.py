#!/usr/bin/env python3
"""Deterministic fake Language Server used by codeLess tests (stdlib only, Python 3.6+).

Behaviour (all positions UTF-16, like the real protocol):
  * incremental document sync; "BAD" anywhere -> Error diagnostic on that word
  * completion: alpha / alphabet (snippet) / beta (textEdit) / imp (additionalTextEdits); "." -> members
  * completionItem/resolve adds documentation; hover returns markdown with the word and document version
  * signatureHelp for func(a, b); definition / typeDefinition / implementation -> first "def <word>" / "class <word>"
  * references -> every occurrence in open docs; prepareRename/rename -> WorkspaceEdit over open docs and
    *.py files on disk under the root; formatting strips trailing whitespace; documentSymbol lists def/class
  * codeAction: quick fix "Replace BAD with GOOD" for BAD diagnostics in the context
  * custom: fake/getText (request), fake/getState (request), fake/crash (notification -> exit 3),
    workspace/executeCommand "fake.applyEdit" -> sends workspace/applyEdit to the client
"""
import json
import os
import re
import sys

stdin = sys.stdin.buffer
stdout = sys.stdout.buffer

docs = {}          # uri -> {"text": str, "version": int, "languageId": str}
root_path = None
state = {"configuration": None, "registered": False, "progress_created": False, "applyEdit": None,
         "didSave": 0, "didClose": [], "changes": [], "initOptions": None, "clientCapabilities": None,
         "watched": []}
# Optional switches (tests): --init-delay-ms N (slow initialize), --watch-py (register a "**/*.py" file watcher)
INIT_DELAY_MS = 0
WATCH_PY = False
for _i, _a in enumerate(sys.argv):
    if _a == "--init-delay-ms" and _i + 1 < len(sys.argv):
        INIT_DELAY_MS = int(sys.argv[_i + 1])
    if _a == "--watch-py":
        WATCH_PY = True
pending = {}       # id -> method for requests we sent to the client
next_id = [1000]
WORD = re.compile(r"[A-Za-z_][A-Za-z0-9_]*")


def send(msg):
    msg["jsonrpc"] = "2.0"
    body = json.dumps(msg).encode("utf-8")
    stdout.write(b"Content-Length: " + str(len(body)).encode("ascii") + b"\r\n\r\n" + body)
    stdout.flush()


def request_client(method, params):
    next_id[0] += 1
    pending[next_id[0]] = method
    send({"id": next_id[0], "method": method, "params": params})


def read_message():
    length = None
    while True:
        line = stdin.readline()
        if not line:
            return None
        line = line.strip()
        if not line:
            if length is None:
                continue
            break
        name, _, value = line.decode("ascii").partition(":")
        if name.strip().lower() == "content-length":
            length = int(value.strip())
    body = stdin.read(length)
    return json.loads(body.decode("utf-8"))


def utf16_units(s):
    return len(s.encode("utf-16-le")) // 2


def offset_of(text, line, character):
    """(line, UTF-16 character) -> Python string index."""
    lines = text.split("\n")
    if line >= len(lines):
        return len(text)
    off = sum(len(l) + 1 for l in lines[:line])
    l = lines[line]
    units = 0
    i = 0
    while i < len(l) and units < character:
        units += 2 if ord(l[i]) > 0xFFFF else 1
        i += 1
    return off + i


def pos_of(text, index):
    before = text[:index]
    line = before.count("\n")
    col_text = before[before.rfind("\n") + 1:]
    return {"line": line, "character": utf16_units(col_text)}


def rng(text, start, end):
    return {"start": pos_of(text, start), "end": pos_of(text, end)}


def word_at(text, line, character):
    idx = offset_of(text, line, character)
    for m in WORD.finditer(text):
        if m.start() <= idx <= m.end():
            return m.group(0), m.start(), m.end()
    return None, idx, idx


def publish(uri):
    d = docs.get(uri)
    if d is None:
        return
    diags = []
    for m in re.finditer(r"BAD", d["text"]):
        diags.append({"range": rng(d["text"], m.start(), m.end()), "severity": 1, "code": "bad-word",
                      "source": "fake-lsp", "message": "BAD is not allowed"})
    send({"method": "textDocument/publishDiagnostics", "params": {"uri": uri, "version": d["version"], "diagnostics": diags}})


def uri_to_path(uri):
    from urllib.parse import unquote
    p = unquote(uri[len("file://"):])
    if re.match(r"^/[A-Za-z]:", p):
        p = p[1:]
    return p


def path_to_uri(path):
    from urllib.parse import quote
    p = path.replace("\\", "/")
    if not p.startswith("/"):
        p = "/" + p
    return "file://" + quote(p, safe="/:")


def all_texts():
    texts = dict((u, d["text"]) for u, d in docs.items())
    if root_path and os.path.isdir(root_path):
        for name in sorted(os.listdir(root_path)):
            if name.endswith(".py"):
                full = os.path.join(root_path, name)
                u = path_to_uri(full)
                norm_full = os.path.normcase(os.path.normpath(full))
                if not any(os.path.normcase(os.path.normpath(uri_to_path(k))) == norm_full for k in texts):
                    with open(full, "r", encoding="utf-8") as f:
                        texts[u] = f.read().replace("\r\n", "\n")
    return texts


def find_definition(word):
    for uri, text in sorted(docs.items(), key=lambda kv: kv[0]):
        for m in re.finditer(r"(?:def|class)\s+(" + re.escape(word) + r")\b", text["text"]):
            return {"uri": uri, "range": rng(text["text"], m.start(1), m.end(1))}
    return None


def handle(msg):
    global root_path
    method = msg.get("method")
    mid = msg.get("id")
    params = msg.get("params") or {}

    if method is None and mid is not None:
        what = pending.pop(mid, None)
        if what == "workspace/configuration":
            state["configuration"] = msg.get("result")
        elif what == "client/registerCapability":
            state["registered"] = "error" not in msg
        elif what == "window/workDoneProgress/create":
            state["progress_created"] = "error" not in msg
        elif what == "workspace/applyEdit":
            state["applyEdit"] = msg.get("result")
        return True

    def reply(result):
        send({"id": mid, "result": result})

    if method == "initialize":
        if INIT_DELAY_MS > 0:
            import time
            time.sleep(INIT_DELAY_MS / 1000.0)
        root_path = params.get("rootPath") or (uri_to_path(params["rootUri"]) if params.get("rootUri") else None)
        state["initOptions"] = params.get("initializationOptions")
        state["clientCapabilities"] = params.get("capabilities")
        reply({"capabilities": {
            "textDocumentSync": {"openClose": True, "change": 2, "save": {"includeText": False}},
            "completionProvider": {"triggerCharacters": ["."], "resolveProvider": True},
            "hoverProvider": True,
            "signatureHelpProvider": {"triggerCharacters": ["(", ","]},
            "definitionProvider": True, "typeDefinitionProvider": True, "implementationProvider": True,
            "referencesProvider": True,
            "renameProvider": {"prepareProvider": True},
            "documentFormattingProvider": True, "documentRangeFormattingProvider": True,
            "documentSymbolProvider": True,
            "codeActionProvider": {"codeActionKinds": ["quickfix"]},
            "executeCommandProvider": {"commands": ["fake.applyEdit"]},
        }, "serverInfo": {"name": "fake-lsp", "version": "1.0"}})
    elif method == "initialized":
        request_client("workspace/configuration", {"items": [{"section": "python"}, {"section": "fake"}]})
        request_client("window/workDoneProgress/create", {"token": "tok1"})
        send({"method": "$/progress", "params": {"token": "tok1", "value": {"kind": "begin", "title": "Indexing", "percentage": 0}}})
        send({"method": "$/progress", "params": {"token": "tok1", "value": {"kind": "end"}}})
        if WATCH_PY:
            request_client("client/registerCapability", {"registrations": [{"id": "r1", "method": "workspace/didChangeWatchedFiles",
                                                                            "registerOptions": {"watchers": [{"globPattern": "**/*.py"}]}}]})
        else:
            request_client("client/registerCapability", {"registrations": [{"id": "r1", "method": "workspace/didChangeWatchedFiles", "registerOptions": {}}]})
        send({"method": "window/logMessage", "params": {"type": 3, "message": "fake-lsp ready"}})
    elif method == "shutdown":
        reply(None)
    elif method == "exit":
        sys.exit(0)
    elif method == "fake/crash":
        os._exit(3)
    elif method == "workspace/didChangeWatchedFiles":
        for ch in params.get("changes", []):
            state["watched"].append([ch.get("uri"), ch.get("type")])
    elif method == "textDocument/didOpen":
        td = params["textDocument"]
        docs[td["uri"]] = {"text": td["text"], "version": td.get("version", 0), "languageId": td.get("languageId")}
        publish(td["uri"])
    elif method == "textDocument/didChange":
        td = params["textDocument"]
        d = docs.get(td["uri"])
        if d is not None:
            for ch in params["contentChanges"]:
                if "range" in ch:
                    r = ch["range"]
                    s = offset_of(d["text"], r["start"]["line"], r["start"]["character"])
                    e = offset_of(d["text"], r["end"]["line"], r["end"]["character"])
                    d["text"] = d["text"][:s] + ch["text"] + d["text"][e:]
                    state["changes"].append("incremental")
                else:
                    d["text"] = ch["text"]
                    state["changes"].append("full")
            d["version"] = td.get("version", d["version"] + 1)
            publish(td["uri"])
    elif method == "textDocument/didSave":
        state["didSave"] += 1
    elif method == "textDocument/didClose":
        uri = params["textDocument"]["uri"]
        state["didClose"].append(uri)
        docs.pop(uri, None)
        send({"method": "textDocument/publishDiagnostics", "params": {"uri": uri, "diagnostics": []}})
    elif method == "fake/getText":
        d = docs.get(params.get("uri"))
        reply(None if d is None else {"text": d["text"], "version": d["version"]})
    elif method == "fake/getState":
        reply(state)
    elif method == "textDocument/completion":
        ctx = params.get("context") or {}
        p = params["position"]
        if ctx.get("triggerCharacter") == ".":
            reply({"isIncomplete": False, "items": [{"label": "member_one", "kind": 2}, {"label": "member_two", "kind": 5}]})
            return True
        items = [
            {"label": "alpha", "kind": 6, "sortText": "1", "data": {"id": 1}},
            {"label": "alphabet", "kind": 3, "sortText": "2", "insertText": "alphabet(${1:x})$0", "insertTextFormat": 2},
            {"label": "beta", "kind": 6, "sortText": "3",
             "textEdit": {"range": {"start": {"line": p["line"], "character": max(0, p["character"] - 2)}, "end": p}, "newText": "beta_value"}},
            {"label": "imp", "kind": 9, "sortText": "4", "insertText": "imp",
             "additionalTextEdits": [{"range": {"start": {"line": 0, "character": 0}, "end": {"line": 0, "character": 0}}, "newText": "import imp\n"}]},
        ]
        reply({"isIncomplete": False, "items": items})
    elif method == "completionItem/resolve":
        item = dict(params)
        item["documentation"] = {"kind": "markdown", "value": "Resolved **docs** for `" + item.get("label", "") + "`"}
        item["detail"] = "detail:" + item.get("label", "")
        reply(item)
    elif method == "textDocument/hover":
        d = docs.get(params["textDocument"]["uri"])
        if d is None:
            reply(None)
            return True
        w, s, e = word_at(d["text"], params["position"]["line"], params["position"]["character"])
        if not w:
            reply(None)
            return True
        reply({"contents": {"kind": "markdown", "value": "```python\ndef " + w + "()\n```\n---\nDocs for **" + w + "** (v" + str(d["version"]) + ")"},
               "range": rng(d["text"], s, e)})
    elif method == "textDocument/signatureHelp":
        d = docs.get(params["textDocument"]["uri"])
        text = d["text"] if d else ""
        idx = offset_of(text, params["position"]["line"], params["position"]["character"])
        open_paren = text.rfind("(", 0, idx)
        active = text[open_paren + 1:idx].count(",") if open_paren >= 0 else 0
        reply({"signatures": [{"label": "func(a, b)", "documentation": "Adds *a* and *b*.",
                               "parameters": [{"label": "a"}, {"label": [8, 9]}]}],
               "activeSignature": 0, "activeParameter": active})
    elif method in ("textDocument/definition", "textDocument/typeDefinition", "textDocument/implementation", "textDocument/declaration"):
        d = docs.get(params["textDocument"]["uri"])
        w = word_at(d["text"], params["position"]["line"], params["position"]["character"])[0] if d else None
        loc = find_definition(w) if w else None
        if loc and method == "textDocument/definition":
            reply([{"targetUri": loc["uri"], "targetRange": loc["range"], "targetSelectionRange": loc["range"]}])  # LocationLink
        else:
            reply(loc)
    elif method == "textDocument/references":
        d = docs.get(params["textDocument"]["uri"])
        w = word_at(d["text"], params["position"]["line"], params["position"]["character"])[0] if d else None
        out = []
        if w:
            for uri, dd in sorted(docs.items()):
                for m in re.finditer(r"\b" + re.escape(w) + r"\b", dd["text"]):
                    out.append({"uri": uri, "range": rng(dd["text"], m.start(), m.end())})
        reply(out)
    elif method == "textDocument/prepareRename":
        d = docs.get(params["textDocument"]["uri"])
        w, s, e = word_at(d["text"], params["position"]["line"], params["position"]["character"])
        if not w or w in ("def", "class"):
            send({"id": mid, "error": {"code": -32602, "message": "You cannot rename this element."}})
        else:
            reply({"range": rng(d["text"], s, e), "placeholder": w})
    elif method == "textDocument/rename":
        d = docs.get(params["textDocument"]["uri"])
        w = word_at(d["text"], params["position"]["line"], params["position"]["character"])[0]
        changes = {}
        for uri, text in all_texts().items():
            edits = [{"range": rng(text, m.start(), m.end()), "newText": params["newName"]}
                     for m in re.finditer(r"\b" + re.escape(w) + r"\b", text)]
            if edits:
                changes[uri] = edits
        reply({"changes": changes})
    elif method in ("textDocument/formatting", "textDocument/rangeFormatting"):
        d = docs.get(params["textDocument"]["uri"])
        edits = []
        lines = d["text"].split("\n")
        for i, l in enumerate(lines):
            stripped = l.rstrip(" \t")
            if stripped != l:
                edits.append({"range": {"start": {"line": i, "character": utf16_units(stripped)},
                                        "end": {"line": i, "character": utf16_units(l)}}, "newText": ""})
        reply(edits)
    elif method == "textDocument/documentSymbol":
        d = docs.get(params["textDocument"]["uri"])
        out = []
        for m in re.finditer(r"^(\s*)(def|class)\s+([A-Za-z_]\w*)", d["text"], re.M):
            r = rng(d["text"], m.start(2), m.end(3))
            out.append({"name": m.group(3), "kind": 5 if m.group(2) == "class" else 12, "range": r,
                        "selectionRange": rng(d["text"], m.start(3), m.end(3)), "children": []})
        reply(out)
    elif method == "textDocument/codeAction":
        uri = params["textDocument"]["uri"]
        actions = []
        for diag in (params.get("context") or {}).get("diagnostics", []):
            if diag.get("code") == "bad-word":
                actions.append({"title": "Replace BAD with GOOD", "kind": "quickfix", "isPreferred": True, "diagnostics": [diag],
                                "edit": {"documentChanges": [{"textDocument": {"uri": uri, "version": None},
                                                              "edits": [{"range": diag["range"], "newText": "GOOD"}]}]}})
        actions.append({"title": "Run fake command", "command": "fake.applyEdit", "arguments": [uri]})
        reply(actions)
    elif method == "workspace/executeCommand":
        if params.get("command") == "fake.applyEdit":
            uri = params["arguments"][0]
            request_client("workspace/applyEdit", {"label": "fake", "edit": {"changes": {uri: [
                {"range": {"start": {"line": 0, "character": 0}, "end": {"line": 0, "character": 0}}, "newText": "# applied\n"}]}}})
        reply(None)
    elif mid is not None:
        send({"id": mid, "error": {"code": -32601, "message": "Method not found: " + str(method)}})
    return True


def main():
    if os.environ.get("FAKE_LSP_WRITE_PID"):
        with open(os.environ["FAKE_LSP_WRITE_PID"], "a") as f:
            f.write(str(os.getpid()) + "\n")
    while True:
        msg = read_message()
        if msg is None:
            break
        handle(msg)


if __name__ == "__main__":
    main()
