#!/usr/bin/env python3
"""Synthetic big-monorepo generator for codeLess performance work.

Creates a deterministic (seeded) repository that looks like a real polyglot monorepo:

  packages/   ~TypeScript/JS libraries (components, hooks, utils, services, tests, nested .gitignore files)
  apps/       deep Next.js-style app trees (route groups, dynamic segments) -> deep paths
  services/   C# solutions (Api / Domain / Infrastructure projects) with bin/ and obj/ build output
  tools/      Python packages with __pycache__
  docs/       Markdown
  node_modules/  50k+ files of third-party packages (gitignored) incl. nested node_modules
  dist/ build/   build output (gitignored), plus per-package dist/ and generated/ folders
  *.log files    (gitignored), one re-included via a negated pattern in a nested .gitignore

All tracked (non-ignored) files are committed with `git fast-import` (one pack, no loose objects) and the index is
rebuilt, so `git status` is clean like a real checkout.

A manifest with the needle tokens / quick-open targets is written next to the repo (<out>.manifest.json), never into
the repo itself.

Usage:
  python3 bench/gen_monorepo.py [--out /tmp/cl-monorepo] [--files 200000] [--node-modules 55000] [--seed 7] [--no-git]
"""
import argparse
import json
import os
import random
import shutil
import subprocess
import sys
import time

WORDS = ("account address agent alert analytics api app archive asset audit auth avatar badge balance banner batch billing "
         "board booking bridge broker browser bucket budget buffer button cache calendar campaign canvas card cart catalog "
         "channel chart checkout client cluster column comment config connector contact content context contract cookie "
         "coupon credit cursor customer dashboard dataset date debug delivery deploy device dialog digest discount document "
         "domain draft driver editor email engine entity event export feature feed field file filter flag folder form gateway "
         "graph grid group header health history hook image import inbox index invoice issue item job journal kernel key label "
         "layout ledger license limit link list loader locale lock log login mailer manifest map market media member menu "
         "message meter metric migration model module monitor network node note notice offer order owner package page panel "
         "parser partner payment payout peer permission picker pipeline plan player plugin policy poll portal preview price "
         "printer profile project promo prompt provider proxy query queue quota rate reader receipt record redirect region "
         "release report request resolver resource review role route rule runner schedule schema scope search secret section "
         "segment sensor session setting shard share shipment signal sitemap slot snapshot socket source spec stage state "
         "status storage store stream style subscription summary supplier support survey switch sync table tag task team "
         "template tenant theme thread ticket timeline token toolbar topic tracker transfer trigger upload usage user vault "
         "vendor version video view voucher wallet webhook widget window worker workflow workspace zone").split()

AREAS = ["components", "hooks", "utils", "services", "store", "api", "models", "lib", "features", "adapters"]


def cap(w):
    return w[:1].upper() + w[1:]


class Gen(object):
    def __init__(self, out, seed, target_files, target_nm, with_git):
        self.out = out
        self.rnd = random.Random(seed)
        self.target_files = target_files
        self.target_nm = target_nm
        self.with_git = with_git
        self.tracked = []          # relative paths of committed files
        self.count_src = 0
        self.count_ignored = 0
        self.bytes_src = 0
        self.needle_files = []
        self.needle_ignored = []
        self.quick_open_targets = []
        self.dirs_made = set()

    # ------------------------------------------------------------------ io
    def write(self, rel, text, tracked=True):
        rel = rel.replace("//", "/")
        full = os.path.join(self.out, rel)
        d = os.path.dirname(full)
        if d not in self.dirs_made:
            os.makedirs(d, exist_ok=True)
            self.dirs_made.add(d)
        data = text.encode("utf-8")
        with open(full, "wb") as f:
            f.write(data)
        if tracked:
            self.tracked.append(rel)
            self.count_src += 1
            self.bytes_src += len(data)
        else:
            self.count_ignored += 1

    def name(self, n=2):
        return "".join(cap(self.rnd.choice(WORDS)) for _ in range(n))

    def ident(self):
        w = self.rnd.choice(WORDS)
        return w + cap(self.rnd.choice(WORDS))

    # ------------------------------------------------------------------ content templates
    def ts_module(self, comp, react, lines_hint):
        r = self.rnd
        out = []
        out.append("import { %s } from '../%s';" % (self.ident(), r.choice(AREAS)))
        if react:
            out.append("import React, { useState, useEffect } from 'react';")
        out.append("")
        out.append("export interface %sProps {" % comp)
        for _ in range(r.randint(2, 6)):
            out.append("  %s?: %s;" % (self.ident(), r.choice(["string", "number", "boolean", "() => void", "string[]"])))
        out.append("}")
        out.append("")
        n_funcs = max(1, lines_hint // 12)
        for _ in range(n_funcs):
            fn = self.ident()
            a, b = self.ident(), self.ident()
            out.append("export function %s(%s: number, %s: string): string {" % (fn, a, b))
            out.append("  const %s = %s * %d;" % (self.ident(), a, r.randint(2, 99)))
            out.append("  if (%s.length > %d) {" % (b, r.randint(3, 40)))
            out.append("    return %s.slice(0, %d) + '%s';" % (b, r.randint(1, 9), r.choice(WORDS)))
            out.append("  }")
            out.append("  // TODO(%s): handle %s %s" % (r.choice(WORDS), r.choice(WORDS), r.choice(WORDS)))
            out.append("  return `${%s}-%s-${%s}`;" % (a, r.choice(WORDS), b))
            out.append("}")
            out.append("")
        if react:
            out.append("export const %s: React.FC<%sProps> = (props) => {" % (comp, comp))
            out.append("  const [%s, set%s] = useState(%d);" % ("value", "Value", r.randint(0, 9)))
            out.append("  useEffect(() => { set%s(v => v + 1); }, [props]);" % "Value")
            out.append("  return <div className=\"%s\">{value}</div>;" % comp.lower())
            out.append("};")
            out.append("")
            out.append("export default %s;" % comp)
        return "\n".join(out) + "\n"

    def cs_class(self, ns, cls, lines_hint):
        r = self.rnd
        out = ["using System;", "using System.Collections.Generic;", "using System.Linq;", "",
               "namespace %s" % ns, "{", "    public class %s" % cls, "    {"]
        for _ in range(r.randint(2, 6)):
            out.append("        public %s %s { get; set; }" % (r.choice(["string", "int", "bool", "decimal", "DateTime"]), cap(self.ident())))
        out.append("")
        for _ in range(max(1, lines_hint // 10)):
            m = cap(self.ident())
            out.append("        public async System.Threading.Tasks.Task<int> %sAsync(int %s)" % (m, "count"))
            out.append("        {")
            out.append("            var items = new List<int>();")
            out.append("            for (int i = 0; i < count; i++) items.Add(i * %d);" % r.randint(2, 50))
            out.append("            await System.Threading.Tasks.Task.Yield();")
            out.append("            return items.Where(x => x %% %d == 0).Sum();" % r.randint(2, 9))
            out.append("        }")
            out.append("")
        out.append("    }")
        out.append("}")
        return "\n".join(out) + "\n"

    def py_module(self, lines_hint):
        r = self.rnd
        out = ['"""%s %s utilities."""' % (cap(r.choice(WORDS)), r.choice(WORDS)), "import os", "import json", ""]
        for _ in range(max(1, lines_hint // 8)):
            fn = "%s_%s" % (r.choice(WORDS), r.choice(WORDS))
            out.append("def %s(%s, %s=None):" % (fn, r.choice(WORDS), r.choice(WORDS)))
            out.append("    result = {}")
            out.append("    for key in sorted(os.environ)[:%d]:" % r.randint(1, 20))
            out.append("        result[key] = len(key) * %d" % r.randint(1, 9))
            out.append("    return json.dumps(result)")
            out.append("")
        return "\n".join(out) + "\n"

    def md_doc(self, title, paras):
        r = self.rnd
        out = ["# " + title, ""]
        for _ in range(paras):
            out.append(" ".join(r.choice(WORDS) for _ in range(r.randint(20, 70))) + ".")
            out.append("")
        return "\n".join(out)

    def json_doc(self, name):
        r = self.rnd
        d = {"name": name, "version": "%d.%d.%d" % (r.randint(0, 9), r.randint(0, 30), r.randint(0, 99)),
             "private": True, "main": "dist/index.js",
             "dependencies": dict(("@scope/%s" % r.choice(WORDS), "^%d.0.0" % r.randint(1, 9)) for _ in range(r.randint(1, 8)))}
        return json.dumps(d, indent=2) + "\n"

    def lines_hint(self):
        r = self.rnd.random()
        if r < 0.70:
            return self.rnd.randint(8, 50)       # small
        if r < 0.97:
            return self.rnd.randint(50, 200)     # medium
        return self.rnd.randint(300, 900)        # a few larger files

    # ------------------------------------------------------------------ trees
    def gen_package(self, idx):
        r = self.rnd
        pname = "pkg-%03d-%s" % (idx, r.choice(WORDS))
        base = "packages/" + pname
        self.write(base + "/package.json", self.json_doc("@mono/" + pname))
        self.write(base + "/README.md", self.md_doc(pname, r.randint(1, 4)))
        self.write(base + "/tsconfig.json", '{ "extends": "../../tsconfig.base.json", "include": ["src"] }\n')
        if idx % 9 == 0:
            # nested .gitignore: dir-only pattern, glob, negation
            self.write(base + "/.gitignore", "# package local ignores\ncoverage/\n*.snap\n!keep.snap\ntmp-*/\n")
            for k in range(4):
                self.write(base + "/coverage/lcov-%d.info" % k, "TN:\nSF:src/index.ts\nend_of_record\n", tracked=False)
            self.write(base + "/src/__snapshots__/a.snap", "exports[`a`] = `1`;\n", tracked=False)
            self.write(base + "/src/__snapshots__/keep.snap", "exports[`keep`] = `1`;\n")
            self.write(base + "/tmp-cache/x.json", "{}\n", tracked=False)
        files_here = r.randint(380, 1100)
        made = 0
        while made < files_here and self.count_src < self.target_files:
            area = r.choice(AREAS)
            depth = r.randint(0, 4)
            segs = [base, "src", area] + [r.choice(WORDS) for _ in range(depth)]
            d = "/".join(segs)
            comp = self.name(2)
            react = area in ("components", "features", "hooks")
            ext = ".tsx" if react else r.choice([".ts", ".ts", ".ts", ".js", ".mjs"])
            self.write("%s/%s%s" % (d, comp, ext), self.ts_module(comp, react, self.lines_hint()))
            made += 1
            if r.random() < 0.35:
                self.write("%s/%s.test%s" % (d, comp, ext), self.ts_module(comp + "Test", False, 20))
                made += 1
            if r.random() < 0.10:
                self.write("%s/%s.json" % (d, comp.lower()), self.json_doc(comp))
                made += 1
            if r.random() < 0.05:
                self.write("%s/%s.md" % (d, comp), self.md_doc(comp, 2))
                made += 1
        # build output and generated code (ignored)
        for k in range(r.randint(3, 12)):
            self.write("%s/dist/%s.js" % (base, self.ident()), "export{};\n", tracked=False)
        if idx % 4 == 0:
            for k in range(r.randint(2, 8)):
                self.write("%s/src/generated/%s.ts" % (base, self.ident()), "export const x = 1;\n", tracked=False)
        if idx % 13 == 0:
            self.write("%s/debug-%d.log" % (base, idx), "log line\n" * 20, tracked=False)

    def gen_app(self, idx):
        r = self.rnd
        base = "apps/app-%02d" % idx
        self.write(base + "/package.json", self.json_doc("@mono/app-%02d" % idx))
        self.write(base + "/next.config.js", "module.exports = { reactStrictMode: true };\n")
        n = r.randint(1500, 3500)
        made = 0
        while made < n and self.count_src < self.target_files:
            segs = [base, "src", "app", "(%s)" % r.choice(WORDS)]
            for _ in range(r.randint(1, 5)):
                segs.append(r.choice(["[id]", "[slug]", r.choice(WORDS), r.choice(WORDS)]))
            d = "/".join(segs)
            for fname in r.sample(["page.tsx", "layout.tsx", "loading.tsx", "error.tsx", "route.ts", "actions.ts"], r.randint(1, 3)):
                comp = self.name(2)
                self.write(d + "/" + fname, self.ts_module(comp, fname.endswith(".tsx"), self.lines_hint()))
                made += 1
        for k in range(r.randint(20, 60)):
            self.write("%s/.next/cache/%s.json" % (base, self.ident()), "{}\n", tracked=False)

    def gen_service(self, idx):
        r = self.rnd
        svc = "%sService" % self.name(1)
        base = "services/%s%02d" % (svc, idx)
        self.write(base + "/%s.sln" % svc, "Microsoft Visual Studio Solution File, Format Version 12.00\n")
        n = r.randint(800, 2200)
        made = 0
        projects = ["Api", "Domain", "Infrastructure", "Application", "Tests"]
        while made < n and self.count_src < self.target_files:
            proj = r.choice(projects)
            folder = r.choice(["Controllers", "Entities", "Handlers", "Repositories", "Dtos", "Validators", "Mappings"])
            sub = "/".join(cap(r.choice(WORDS)) for _ in range(r.randint(0, 3)))
            d = "%s/src/%s.%s/%s%s" % (base, svc, proj, folder, ("/" + sub) if sub else "")
            cls = self.name(2) + folder.rstrip("s")
            self.write("%s/%s.cs" % (d, cls), self.cs_class("%s.%s.%s" % (svc, proj, folder), cls, self.lines_hint()))
            made += 1
        for proj in projects:
            self.write("%s/src/%s.%s/%s.%s.csproj" % (base, svc, proj, svc, proj), "<Project Sdk=\"Microsoft.NET.Sdk\"/>\n")
            for k in range(r.randint(3, 10)):
                self.write("%s/src/%s.%s/bin/Debug/net8.0/%s.dll.txt" % (base, svc, proj, self.ident()), "bin\n", tracked=False)
                self.write("%s/src/%s.%s/obj/%s.cache.txt" % (base, svc, proj, self.ident()), "obj\n", tracked=False)

    def gen_tools(self):
        r = self.rnd
        made = 0
        n = 6000
        while made < n and self.count_src < self.target_files:
            d = "/".join(["tools", r.choice(WORDS)] + [r.choice(WORDS) for _ in range(r.randint(0, 3))])
            self.write("%s/%s_%s.py" % (d, r.choice(WORDS), r.choice(WORDS)), self.py_module(self.lines_hint()))
            made += 1
            if r.random() < 0.3:
                self.write("%s/__pycache__/%s.cpython-311.pyc.txt" % (d, r.choice(WORDS)), "pyc\n", tracked=False)

    def gen_docs(self):
        r = self.rnd
        for i in range(2500):
            if self.count_src >= self.target_files:
                break
            d = "docs/%s/%s" % (r.choice(WORDS), r.choice(WORDS))
            self.write("%s/%s-%d.md" % (d, r.choice(WORDS), i), self.md_doc(self.name(3), r.randint(2, 12)))

    def gen_node_modules(self):
        r = self.rnd
        made = 0
        i = 0
        while made < self.target_nm:
            pkg = "%s-%s" % (r.choice(WORDS), r.choice(WORDS))
            if i % 7 == 0:
                pkg = "@%s/%s" % (r.choice(WORDS), pkg)
            base = "node_modules/%s" % pkg
            self.write(base + "/package.json", self.json_doc(pkg), tracked=False)
            self.write(base + "/README.md", self.md_doc(pkg, 2), tracked=False)
            made += 2
            for k in range(r.randint(8, 60)):
                sub = r.choice(["lib", "dist", "esm", "cjs", "src", "types", "lib/internal"])
                self.write("%s/%s/%s.js" % (base, sub, self.ident()), "'use strict';\nmodule.exports = function %s(a){return a+%d};\n" % (self.ident(), k), tracked=False)
                made += 1
            if i % 11 == 0:
                for k in range(r.randint(5, 25)):
                    self.write("%s/node_modules/%s/index.js" % (base, self.ident()), "module.exports = {};\n", tracked=False)
                    made += 1
            i += 1

    def gen_root_output(self):
        r = self.rnd
        for k in range(2500):
            self.write("dist/%s/%s.js" % (r.choice(WORDS), self.ident()), "export{};\n", tracked=False)
        for k in range(1500):
            self.write("build/%s/%s.js" % (r.choice(WORDS), self.ident()), "export{};\n", tracked=False)
        for k in range(40):
            self.write("logs/%s-%d.log" % (r.choice(WORDS), k), "INFO started\n" * 30, tracked=False)
        for k in range(300):
            self.write(".cache/%s.bin.txt" % self.ident(), "c\n", tracked=False)

    def plant_needles(self):
        r = self.rnd
        token = "CL_NEEDLE_ZEBRA_42"
        # Spread over the tracked set: early, middle and the very last files written (deep in walk order too).
        picks = sorted(set([0, 1, len(self.tracked) // 3, len(self.tracked) // 2] +
                           [r.randrange(len(self.tracked)) for _ in range(12)] + [len(self.tracked) - 1]))
        for p in picks:
            rel = self.tracked[p]
            full = os.path.join(self.out, rel)
            with open(full, "a", encoding="utf-8") as f:
                f.write("\n// %s marker\n" % token)
            self.needle_files.append(rel)
        for rel in ("node_modules/%s/needle.js" % "zz-needle-pkg", "dist/needle/needle.js", "packages/pkg-000-%s/src/generated/needle.ts" % "x"):
            self.write(rel, "// %s marker in ignored file\n" % token, tracked=False)
            self.needle_ignored.append(rel)
        # Quick open targets: unique names in deep folders written late in the walk.
        for rel in ("apps/app-07/src/app/(billing)/[id]/invoice/settings/UniqueQuokkaWidget.tsx",
                    "services/zz-last/src/Zz.Api/Controllers/Deep/Er/StillDeeper/QuokkaLedgerController.cs"):
            self.write(rel, self.ts_module("UniqueQuokkaWidget", True, 30))
            self.quick_open_targets.append(rel)
        return token

    def root_files(self):
        self.write(".gitignore",
                   "# dependencies\nnode_modules/\n\n# build output\ndist/\nbuild/\n.next/\n.cache/\n\n"
                   "# logs\n*.log\n!important.log\n\n# custom\ngenerated/\n/logs/\n__pycache__/\n")
        self.write("package.json", self.json_doc("mono-root"))
        self.write("README.md", self.md_doc("Monorepo", 6))
        self.write("tsconfig.base.json", '{ "compilerOptions": { "strict": true } }\n')
        self.write("important.log", "kept by negation\n")

    def run(self):
        t0 = time.time()
        if os.path.exists(self.out):
            shutil.rmtree(self.out)
        os.makedirs(self.out)
        self.root_files()
        # Mix of languages until the target count is reached.
        idx = 0
        app = 0
        svc = 0
        self.gen_tools()
        self.gen_docs()
        while self.count_src < self.target_files:
            if idx % 25 == 5 and app < 14:
                self.gen_app(app)
                app += 1
            elif idx % 9 == 3:
                self.gen_service(svc)
                svc += 1
            else:
                self.gen_package(idx)
            idx += 1
        self.gen_node_modules()
        self.gen_root_output()
        token = self.plant_needles()
        t_files = time.time() - t0
        if self.with_git:
            self.git_commit()
        manifest = {
            "root": self.out,
            "tracked_files": self.count_src,
            "tracked_bytes": self.bytes_src,
            "ignored_files": self.count_ignored,
            "needle": token,
            "needle_files": self.needle_files,
            "needle_ignored_files": self.needle_ignored,
            "quick_open_targets": self.quick_open_targets,
            "seconds_files": round(t_files, 1),
            "seconds_total": round(time.time() - t0, 1),
        }
        with open(self.out.rstrip("/") + ".manifest.json", "w") as f:
            json.dump(manifest, f, indent=2)
        print(json.dumps(dict((k, v) for k, v in manifest.items() if not isinstance(v, list)), indent=2))

    def git_commit(self):
        env = dict(os.environ)
        env.update({"GIT_AUTHOR_NAME": "bench", "GIT_AUTHOR_EMAIL": "bench@example.invalid",
                    "GIT_COMMITTER_NAME": "bench", "GIT_COMMITTER_EMAIL": "bench@example.invalid"})
        subprocess.check_call(["git", "init", "-q", "-b", "main", self.out], env=env)
        p = subprocess.Popen(["git", "fast-import", "--quiet"], cwd=self.out, stdin=subprocess.PIPE, env=env)
        w = p.stdin
        mark = 0
        marks = []
        for rel in self.tracked:
            mark += 1
            with open(os.path.join(self.out, rel), "rb") as f:
                data = f.read()
            w.write(b"blob\nmark :%d\ndata %d\n" % (mark, len(data)))
            w.write(data)
            w.write(b"\n")
            marks.append((mark, rel))
        msg = b"synthetic monorepo"
        w.write(b"commit refs/heads/main\ncommitter bench <bench@example.invalid> 1700000000 +0000\ndata %d\n%s\n" % (len(msg), msg))
        for m, rel in marks:
            w.write(b"M 100644 :%d %s\n" % (m, rel.encode("utf-8")))
        w.write(b"\n")
        w.close()
        if p.wait() != 0:
            raise SystemExit("git fast-import failed")
        subprocess.check_call(["git", "read-tree", "HEAD"], cwd=self.out, env=env)
        subprocess.call(["git", "update-index", "-q", "--refresh"], cwd=self.out, env=env, stdout=subprocess.DEVNULL)


def main():
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--out", default="/tmp/cl-monorepo")
    ap.add_argument("--files", type=int, default=200000, help="tracked source files (approximate)")
    ap.add_argument("--node-modules", type=int, default=55000, help="files under node_modules/")
    ap.add_argument("--seed", type=int, default=7)
    ap.add_argument("--no-git", action="store_true")
    a = ap.parse_args()
    out = os.path.abspath(a.out)
    if out in ("/", os.path.expanduser("~")) or len(out) < 6:
        raise SystemExit("refusing to write to " + out)
    Gen(out, a.seed, a.files, a.node_modules, not a.no_git).run()


if __name__ == "__main__":
    sys.exit(main())
