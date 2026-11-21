#!/usr/bin/env bash
# Headless big-monorepo benchmark for codeLess (no GUI is launched).
#   bench/run_monorepo_bench.sh [files=38000] [node_modules=10000] [runs=5]
# Generates a synthetic repo under /tmp with bench/gen_monorepo.py, runs tests/.../MonorepoScaleBench.cs against it
# (old vs new workspace walk, Quick Open, Find in Files, BM25 candidates, watcher burst classification),
# prints the report and deletes the repo afterwards.
set -euo pipefail
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
FILES="${1:-38000}"; NM="${2:-10000}"; RUNS="${3:-5}"
REPO="/tmp/cl-bench-monorepo"
REPORT="${CODELESS_BENCH_REPORT:-/tmp/cl-monorepo-bench.txt}"
export PATH="$HOME/.dotnet:$PATH"
trap 'rm -rf "$REPO" "$REPO.manifest.json"' EXIT
python3 "$ROOT/bench/gen_monorepo.py" --out "$REPO" --files "$FILES" --node-modules "$NM"
CODELESS_BENCH_MONOREPO="$REPO" CODELESS_BENCH_REPORT="$REPORT" CODELESS_BENCH_RUNS="$RUNS" \
  dotnet test "$ROOT/tests/codeLess.Avalonia.Tests/codeLess.Avalonia.Tests.csproj" -c Release --nologo -m:2 \
  --filter "FullyQualifiedName~MonorepoScaleBench" >/dev/null
dotnet build-server shutdown >/dev/null 2>&1 || true
cat "$REPORT"
