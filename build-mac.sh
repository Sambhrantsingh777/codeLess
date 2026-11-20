#!/usr/bin/env bash
# ==============================================================================
# codeLess — Native macOS Build, Ad-Hoc Codesign & DMG Packaging Script
# Publisher: Savatarr | Bundle ID: com.savatarr.codeless
# ==============================================================================
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
cd "$SCRIPT_DIR"

APP_NAME="codeLess"
BUNDLE_ID="com.savatarr.codeless"
PUBLISHER="Savatarr"
VERSION="3.2.0"
CSPROJ="src/codeLess.Avalonia/codeLess.Avalonia.csproj"
ENTITLEMENTS="$SCRIPT_DIR/macos/entitlements.plist"

# Detect host Mac architecture by default (arm64 = Apple Silicon, x86_64 = Intel)
HOST_ARCH="$(uname -m)"
if [[ "$HOST_ARCH" == "arm64" ]]; then
    DEFAULT_RID="osx-arm64"
else
    DEFAULT_RID="osx-x64"
fi

TARGET_RID="${1:-$DEFAULT_RID}"

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

build_mac_bundle() {
    local rid="$1"
    local arch_suffix="${rid#osx-}"
    local pub_dir="$SCRIPT_DIR/dist/publish-$rid"
    local bundle_dir="$SCRIPT_DIR/dist/macos-$arch_suffix/$APP_NAME.app"
    local dmg_staging="$SCRIPT_DIR/dist/dmg-staging-$arch_suffix"
    local dmg_out="$SCRIPT_DIR/dist/codeLess-mac-$arch_suffix.dmg"
    local zip_out="$SCRIPT_DIR/dist/codeLess-mac-$arch_suffix.zip"

    echo "======================================================================"
    echo " [1/5] Publishing $CSPROJ for $rid (Self-Contained)..."
    echo "======================================================================"
    rm -rf "$pub_dir" "$bundle_dir" "$dmg_staging"
    dotnet publish "$CSPROJ" \
        -c Release \
        -r "$rid" \
        --self-contained true \
        -p:PublishSingleFile=false \
        -p:UseAppHost=true \
        -o "$pub_dir"

    echo "======================================================================"
    echo " [2/5] Assembling macOS App Bundle: $bundle_dir"
    echo "======================================================================"
    mkdir -p "$bundle_dir/Contents/MacOS"
    mkdir -p "$bundle_dir/Contents/Resources"

    cp -R "$pub_dir/"* "$bundle_dir/Contents/MacOS/"
    chmod +x "$bundle_dir/Contents/MacOS/$APP_NAME"
    [[ -f "$bundle_dir/Contents/MacOS/createdump" ]] && chmod +x "$bundle_dir/Contents/MacOS/createdump"

    # Generate codeLess.icns from square RGBA assets/savatarr_bolt.png (falling back to assets/codeLess.icns)
    local src_png="$SCRIPT_DIR/assets/savatarr_bolt.png"
    [[ ! -f "$src_png" ]] && src_png="$SCRIPT_DIR/assets/savatarr_logo_source.png"
    local icns_out="$bundle_dir/Contents/Resources/$APP_NAME.icns"
    if [[ -f "$SCRIPT_DIR/assets/$APP_NAME.icns" ]]; then
        cp "$SCRIPT_DIR/assets/$APP_NAME.icns" "$icns_out"
    fi
    if [[ -f "$src_png" ]] && command -v sips >/dev/null 2>&1 && command -v iconutil >/dev/null 2>&1; then
        local iconset_dir="$SCRIPT_DIR/dist/tmp.iconset"
        rm -rf "$iconset_dir"
        mkdir -p "$iconset_dir"
        for sz in 16 32 128 256 512; do
            sips -s format png -z "$sz" "$sz" "$src_png" --out "$iconset_dir/icon_${sz}x${sz}.png" >/dev/null 2>&1 || true
            local sz2=$((sz * 2))
            sips -s format png -z "$sz2" "$sz2" "$src_png" --out "$iconset_dir/icon_${sz}x${sz}@2x.png" >/dev/null 2>&1 || true
        done
        iconutil -c icns "$iconset_dir" -o "$icns_out" >/dev/null 2>&1 || true
        rm -rf "$iconset_dir"
    fi

    cat > "$bundle_dir/Contents/Info.plist" <<EOF
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
    <key>CFBundleName</key>
    <string>codeLess</string>
    <key>CFBundleDisplayName</key>
    <string>codeLess</string>
    <key>CFBundleIdentifier</key>
    <string>${BUNDLE_ID}</string>
    <key>CFBundleVersion</key>
    <string>${VERSION}</string>
    <key>CFBundleShortVersionString</key>
    <string>${VERSION}</string>
    <key>CFBundlePackageType</key>
    <string>APPL</string>
    <key>CFBundleSignature</key>
    <string>????</string>
    <key>CFBundleExecutable</key>
    <string>${APP_NAME}</string>
    <key>CFBundleIconFile</key>
    <string>${APP_NAME}.icns</string>
    <key>LSMinimumSystemVersion</key>
    <string>11.0</string>
    <key>NSHighResolutionCapable</key>
    <true/>
    <key>NSSupportsAutomaticGraphicsSwitching</key>
    <true/>
    <key>NSHumanReadableCopyright</key>
    <string>Copyright © 2026 ${PUBLISHER}. Released under the MIT License.</string>
</dict>
</plist>
EOF

    echo "======================================================================"
    echo " [3/5] Ad-Hoc Code Signing with CoreCLR JIT Entitlements ($rid)..."
    echo "======================================================================"
    if command -v xattr >/dev/null 2>&1; then
        xattr -cr "$bundle_dir" || true
    fi
    if command -v codesign >/dev/null 2>&1; then
        # Sign native Mach-O dylibs and helper binaries individually first, then sign the bundle with JIT entitlements
        find "$bundle_dir/Contents/MacOS" -type f \( -name "*.dylib" -o -name "createdump" \) -print0 | while IFS= read -r -d '' bin_file; do
            codesign --force -s - "$bin_file"
        done
        if [[ -f "$ENTITLEMENTS" ]]; then
            codesign --force --entitlements "$ENTITLEMENTS" -s - "$bundle_dir/Contents/MacOS/$APP_NAME"
            codesign --force --deep --entitlements "$ENTITLEMENTS" -s - "$bundle_dir"
        else
            codesign --force --deep -s - "$bundle_dir"
        fi
        codesign --verify --deep --strict --verbose=2 "$bundle_dir"
    else
        echo "[!] 'codesign' not found (non-macOS host). Run on macOS to apply ad-hoc signature."
    fi

    echo "======================================================================"
    echo " [4/5] Creating Zip Archive: $zip_out"
    echo "======================================================================"
    if command -v ditto >/dev/null 2>&1; then
        rm -f "$zip_out"
        ditto -c -k --sequesterRsrc --keepParent "$bundle_dir" "$zip_out"
    fi

    echo "======================================================================"
    echo " [5/5] Building macOS Disk Image (.dmg): $dmg_out"
    echo "======================================================================"
    if command -v hdiutil >/dev/null 2>&1; then
        mkdir -p "$dmg_staging"
        ditto "$bundle_dir" "$dmg_staging/$APP_NAME.app"
        ln -s /Applications "$dmg_staging/Applications" || true
        rm -f "$dmg_out"
        hdiutil create \
            -volname "codeLess" \
            -srcfolder "$dmg_staging" \
            -ov \
            -format UDZO \
            "$dmg_out"
        rm -rf "$dmg_staging"
    else
        echo "[!] 'hdiutil' not found (non-macOS host). Run on macOS to produce .dmg."
    fi

    echo ""
    echo "✓ Built App Bundle: $bundle_dir"
    [[ -f "$dmg_out" ]] && echo "✓ Built Disk Image: $dmg_out"
    [[ -f "$zip_out" ]] && echo "✓ Built Zip Bundle: $zip_out"
}

if [[ "$TARGET_RID" == "both" || "$TARGET_RID" == "all" ]]; then
    build_mac_bundle "osx-arm64"
    build_mac_bundle "osx-x64"
else
    build_mac_bundle "$TARGET_RID"
fi

cat <<'GATEKEEPER_EOF'

======================================================================
  macOS Gatekeeper & First-Run Instructions
======================================================================
1. Open the .dmg and drag codeLess.app into /Applications.
2. Clear the browser download quarantine flag once in Terminal:
     xattr -cr /Applications/codeLess.app
     open /Applications/codeLess.app
   (Or Control-click codeLess.app in Finder -> Open -> System Settings ->
    Privacy & Security -> Open Anyway.)
======================================================================
GATEKEEPER_EOF
