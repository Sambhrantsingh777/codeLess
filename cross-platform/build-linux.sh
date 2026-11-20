#!/usr/bin/env bash
# ==============================================================================
# codeLess v3.2.0 — Native Linux (.NET 8 + Avalonia) & Debian (.deb) Builder
# Publisher: Savatarr | Zero Mono / Zero Node.js Runtime Dependency
# ==============================================================================
set -euo pipefail

APP_NAME="codeless"
VERSION="3.2.0"
ROOT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
CSPROJ="$ROOT_DIR/src/codeLess.Avalonia/codeLess.Avalonia.csproj"

HOST_ARCH="$(uname -m)"
if [[ "$HOST_ARCH" == "aarch64" || "$HOST_ARCH" == "arm64" ]]; then
    DEFAULT_RID="linux-arm64"
    DEB_ARCH="arm64"
else
    DEFAULT_RID="linux-x64"
    DEB_ARCH="amd64"
fi

TARGET_RID="${1:-$DEFAULT_RID}"
if [[ "$TARGET_RID" == "linux-arm64" ]]; then
    DEB_ARCH="arm64"
else
    DEB_ARCH="amd64"
fi

BUILD_DIR="$ROOT_DIR/Output/$TARGET_RID"
DEB_ROOT="$ROOT_DIR/Output/deb-pkg-$DEB_ARCH"

if ! command -v dotnet >/dev/null 2>&1; then
    if [[ -x "$HOME/.dotnet/dotnet" ]]; then
        export DOTNET_ROOT="$HOME/.dotnet"
        export PATH="$HOME/.dotnet:$PATH"
    else
        echo "[!] .NET 8 SDK not found on PATH. Installing per-user via Microsoft dotnet-install.sh..."
        curl -sSL https://dot.net/v1/dotnet-install.sh | bash -s -- --channel 8.0 --install-dir "$HOME/.dotnet"
        export DOTNET_ROOT="$HOME/.dotnet"
        export PATH="$HOME/.dotnet:$PATH"
    fi
fi

echo "[1/2] Publishing self-contained $CSPROJ for $TARGET_RID..."
rm -rf "$BUILD_DIR" "$DEB_ROOT"
dotnet publish "$CSPROJ" \
    -c Release \
    -r "$TARGET_RID" \
    --self-contained true \
    -p:PublishSingleFile=false \
    -o "$BUILD_DIR"

chmod +x "$BUILD_DIR/codeLess"

echo "[2/2] Assembling Debian (.deb) package structure..."
mkdir -p "$DEB_ROOT/DEBIAN" "$DEB_ROOT/usr/lib/codeless" "$DEB_ROOT/usr/bin" "$DEB_ROOT/usr/share/applications"
cp -R "$BUILD_DIR/"* "$DEB_ROOT/usr/lib/codeless/"
ln -sf /usr/lib/codeless/codeLess "$DEB_ROOT/usr/bin/codeless"

cat > "$DEB_ROOT/usr/share/applications/codeless.desktop" << 'EOF'
[Desktop Entry]
Name=codeLess
Comment=Lightweight Native Agentic IDE & Multi-Harness Workbench
Exec=/usr/bin/codeless %F
Terminal=false
Type=Application
Categories=Development;IDE;TextEditor;
EOF

cat > "$DEB_ROOT/DEBIAN/control" << EOF
Package: codeless-ide
Version: $VERSION
Section: devel
Priority: optional
Architecture: $DEB_ARCH
Depends: libx11-6, libfontconfig1
Maintainer: Savatarr <https://github.com/Sambhrantsingh777/codeLess>
Description: codeLess — Lightweight Native Agentic IDE & Multi-Harness Engineering Workbench
EOF

if command -v dpkg-deb >/dev/null 2>&1; then
    dpkg-deb --build "$DEB_ROOT" "$ROOT_DIR/Output/codeless-ide_${VERSION}_${DEB_ARCH}.deb"
    echo "✓ Built Linux Debian package: $ROOT_DIR/Output/codeless-ide_${VERSION}_${DEB_ARCH}.deb"
fi
echo "✓ Built native Linux bundle in: $BUILD_DIR"
