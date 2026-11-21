#!/usr/bin/env bash
# Headless large-file benchmark for codeLess (no GUI is launched).
#   bench/run_largefile_bench.sh [sizes_mb="100 1024"] [runs=3]
# For each size: stream-generates a synthetic service log with bench/gen_logfile.py, runs LargeFileEngineBench
# (open / first screen / full index / go to line / search / copy / tail) and LargeFileWorkbenchBench (OpenFileInTab in
# the headless workbench; files <= 300 MB are also loaded the old way into an EditorTabBuffer for comparison),
# then deletes the file. Report: $CODELESS_BENCH_REPORT (default /tmp/cl-largefile-bench.txt).
set -euo pipefail
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
SIZES="${1:-100 1024}"; RUNS="${2:-3}"
DIR="/tmp/cl-largefile-bench"
REPORT="${CODELESS_BENCH_REPORT:-/tmp/cl-largefile-bench.txt}"
export PATH="$HOME/.dotnet:$PATH"
trap 'rm -rf "$DIR"' EXIT
mkdir -p "$DIR"; : > "$REPORT"
dotnet build "$ROOT/tests/codeLess.Avalonia.Tests/codeLess.Avalonia.Tests.csproj" -c Release --nologo -m:2 >/dev/null
for MB in $SIZES; do
  F="$DIR/app-${MB}MB.log"
  vm_stat | awk '/Pages free|Pages inactive/' >&2
  python3 "$ROOT/bench/gen_logfile.py" "$F" --mb "$MB"
  EDITOR_FILE=""; if [ "$MB" -le 300 ]; then EDITOR_FILE="$F"; fi
  CODELESS_BENCH_LARGEFILE="$F" CODELESS_BENCH_EDITOR_FILE="$EDITOR_FILE" CODELESS_BENCH_REPORT="$REPORT" CODELESS_BENCH_RUNS="$RUNS" \
    dotnet test "$ROOT/tests/codeLess.Avalonia.Tests/codeLess.Avalonia.Tests.csproj" -c Release --nologo --no-build \
    --filter "FullyQualifiedName~LargeFileEngineBench|FullyQualifiedName~LargeFileWorkbenchBench" >/dev/null
  rm -f "$F"
done
dotnet build-server shutdown >/dev/null 2>&1 || true
cat "$REPORT"
