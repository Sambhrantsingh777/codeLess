#!/usr/bin/env python3
"""Fake `npm install --prefix <dir> ... <packages>` for codeLess installer tests (stdlib only, no network).

"Installs" each package by writing node_modules/.bin/<bin> into the prefix: a launcher that runs the deterministic
fake language server (fake_lsp_server.py next to this script) with this Python interpreter.
Behaviour switches: a file "fake_npm_mode.txt" in the folder that contains the prefix (the managed root):
  "fail"  -> print an npm-like error and exit 1
  "slow"  -> sleep 1.5 s before installing (lets UI tests observe the "Installing" state)
Every invocation appends its argv (one JSON line) to "fake_npm_calls.txt" in that folder.
"""
import json
import os
import sys
import time

BINS = {
    "bash-language-server": ["bash-language-server"],
    "typescript-language-server": ["typescript-language-server"],
    "pyright": ["pyright-langserver", "pyright"],
    "yaml-language-server": ["yaml-language-server"],
    "vscode-langservers-extracted": ["vscode-json-language-server", "vscode-html-language-server", "vscode-css-language-server"],
}


def main(argv):
    if not argv or argv[0] != "install" or "--prefix" not in argv:
        sys.stderr.write("fake npm: unsupported arguments %r\n" % (argv,))
        return 2
    prefix = argv[argv.index("--prefix") + 1]
    root = os.path.dirname(os.path.abspath(prefix))
    with open(os.path.join(root, "fake_npm_calls.txt"), "a") as f:
        f.write(json.dumps(argv) + "\n")
    mode = ""
    try:
        with open(os.path.join(root, "fake_npm_mode.txt")) as f:
            mode = f.read().strip()
    except IOError:
        pass
    if mode == "slow":
        time.sleep(1.5)
    if mode == "fail":
        sys.stderr.write("npm ERR! code E404\nnpm ERR! 404 Not Found - fake registry\n")
        return 1
    pkgs = [a for a in argv[1:] if not a.startswith("-") and a != prefix]
    here = os.path.dirname(os.path.abspath(__file__))
    server = os.path.join(here, "fake_lsp_server.py")
    bindir = os.path.join(prefix, "node_modules", ".bin")
    os.makedirs(bindir, exist_ok=True)
    count = 0
    for p in pkgs:
        # like real npm: node_modules/<package>/package.json for every installed package
        name = p[: p.index("@", 1)] if "@" in p[1:] else p
        pkgdir = os.path.join(prefix, "node_modules", *name.split("/"))
        os.makedirs(pkgdir, exist_ok=True)
        with open(os.path.join(pkgdir, "package.json"), "w") as f:
            f.write('{"name": "%s"}\n' % name)
        for b in BINS.get(p, []):
            if os.name == "nt":
                with open(os.path.join(bindir, b + ".cmd"), "w") as f:
                    f.write('@"%s" "%s" %%*\r\n' % (sys.executable, server))
            else:
                path = os.path.join(bindir, b)
                with open(path, "w") as f:
                    f.write("#!/bin/sh\nexec '%s' '%s' \"$@\"\n" % (sys.executable.replace("'", "'\\''"), server.replace("'", "'\\''")))
                os.chmod(path, 0o755)
        count += 1
    print("added %d packages in 0.1s" % count)
    print("prefix=" + prefix)
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
