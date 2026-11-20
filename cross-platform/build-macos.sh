#!/usr/bin/env bash
# ==============================================================================
# codeLess v3.2.0 — macOS App Bundle (.app & .dmg) Wrapper
# Delegates to root ./build-mac.sh (.NET 8 + Avalonia native macOS builder)
# ==============================================================================
set -euo pipefail

ROOT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
exec "$ROOT_DIR/build-mac.sh" "$@"
