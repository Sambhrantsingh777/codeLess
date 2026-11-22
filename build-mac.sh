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
VERSION="3.3.2"
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

# ------------------------------------------------------------------------------
# Signing & notarization configuration (all optional; defaults to ad-hoc signing)
#   MACOS_SIGN_IDENTITY   "Developer ID Application: Your Name (TEAMID)"  (default "-" = ad-hoc)
#   MACOS_KEYCHAIN        optional keychain path holding the identity (used by CI)
# Notarization (only when a Developer ID identity is set), pick ONE method:
#   App Store Connect API key: APPLE_API_KEY_PATH (.p8), APPLE_API_KEY_ID, APPLE_API_ISSUER_ID
#   Apple ID:                  APPLE_ID, APPLE_TEAM_ID, APPLE_APP_SPECIFIC_PASSWORD
#   Stored profile:            NOTARY_KEYCHAIN_PROFILE (from `xcrun notarytool store-credentials`)
# ------------------------------------------------------------------------------
SIGN_IDENTITY="${MACOS_SIGN_IDENTITY:--}"
NOTARIZE=0
NOTARY_ARGS=()
if [[ "$SIGN_IDENTITY" != "-" ]]; then
    if [[ -n "${APPLE_API_KEY_PATH:-}" && -n "${APPLE_API_KEY_ID:-}" && -n "${APPLE_API_ISSUER_ID:-}" ]]; then
        NOTARY_ARGS=(--key "$APPLE_API_KEY_PATH" --key-id "$APPLE_API_KEY_ID" --issuer "$APPLE_API_ISSUER_ID")
        NOTARIZE=1
    elif [[ -n "${APPLE_ID:-}" && -n "${APPLE_TEAM_ID:-}" && -n "${APPLE_APP_SPECIFIC_PASSWORD:-}" ]]; then
        NOTARY_ARGS=(--apple-id "$APPLE_ID" --team-id "$APPLE_TEAM_ID" --password "$APPLE_APP_SPECIFIC_PASSWORD")
        NOTARIZE=1
    elif [[ -n "${NOTARY_KEYCHAIN_PROFILE:-}" ]]; then
        NOTARY_ARGS=(--keychain-profile "$NOTARY_KEYCHAIN_PROFILE")
        NOTARIZE=1
    else
        echo "[!] MACOS_SIGN_IDENTITY is set but no notarization credentials were found;"
        echo "    the app will be Developer ID signed but NOT notarized (Gatekeeper will still warn)."
    fi
fi

notarize_file() {
    local target="$1"
    echo "→ Submitting $(basename "$target") to Apple notary service (this can take several minutes)..."
    local out
    if ! out="$(xcrun notarytool submit "$target" "${NOTARY_ARGS[@]}" --wait --timeout 30m 2>&1)"; then
        echo "$out"
        echo "::error::notarytool submission failed for $target"
        exit 1
    fi
    echo "$out"
    if ! grep -q "status: Accepted" <<<"$out"; then
        local sub_id
        sub_id="$(grep -m1 -E '^\s*id:' <<<"$out" | awk '{print $2}')"
        if [[ -n "$sub_id" ]]; then
            xcrun notarytool log "$sub_id" "${NOTARY_ARGS[@]}" || true
        fi
        echo "::error::Apple notarization was not accepted for $target"
        exit 1
    fi
}

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
        -p:PublishSingleFile=true \
        -p:IncludeNativeLibrariesForSelfExtract=false \
        -p:UseAppHost=true \
        -o "$pub_dir"

    echo "======================================================================"
    echo " [2/5] Assembling macOS App Bundle: $bundle_dir"
    echo "======================================================================"
    mkdir -p "$bundle_dir/Contents/MacOS"
    mkdir -p "$bundle_dir/Contents/Resources"

    # Place non-code asset subdirectories into Contents/Resources and copy only flat files into Contents/MacOS
    if [[ -d "$pub_dir/assets" ]]; then
        cp -R "$pub_dir/assets" "$bundle_dir/Contents/Resources/assets"
    fi

    while IFS= read -r -d '' flat_file; do
        cp "$flat_file" "$bundle_dir/Contents/MacOS/"
    done < <(find "$pub_dir" -maxdepth 1 -type f ! -name "*.pdb" ! -name "*.dsym" -print0)

    chmod 755 "$bundle_dir/Contents/MacOS/$APP_NAME"
    while IFS= read -r -d '' dylib_file; do
        chmod 755 "$dylib_file"
    done < <(find "$bundle_dir/Contents/MacOS" -maxdepth 1 -type f -name "*.dylib" -print0)

    # Generate codeLess.icns from square RGBA assets/savatarr_bolt.png (falling back to assets/codeLess.icns)
    local src_png="$SCRIPT_DIR/assets/savatarr_bolt.png"
    if [[ ! -f "$src_png" ]]; then
        src_png="$SCRIPT_DIR/assets/savatarr_logo_source.png"
    fi
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
    <key>CFBundleSpokenName</key>
    <string>codeLess</string>
    <key>CFBundleIdentifier</key>
    <string>${BUNDLE_ID}</string>
    <key>LSApplicationCategoryType</key>
    <string>public.app-category.developer-tools</string>
    <key>NSSupportsAutomaticTermination</key>
    <false/>
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
    <key>CFBundleDocumentTypes</key>
    <array>
        <dict>
            <key>CFBundleTypeName</key>
            <string>Folder</string>
            <key>CFBundleTypeRole</key>
            <string>Editor</string>
            <key>LSHandlerRank</key>
            <string>Alternate</string>
            <key>LSItemContentTypes</key>
            <array>
                <string>public.folder</string>
            </array>
        </dict>
        <dict>
            <key>CFBundleTypeName</key>
            <string>Source Code and Text</string>
            <key>CFBundleTypeRole</key>
            <string>Editor</string>
            <key>LSHandlerRank</key>
            <string>Alternate</string>
            <key>LSItemContentTypes</key>
            <array>
                <string>public.text</string>
                <string>public.source-code</string>
                <string>public.data</string>
            </array>
        </dict>
    </array>
    <key>NSHumanReadableCopyright</key>
    <string>Copyright © 2026 ${PUBLISHER}. Released under the MIT License.</string>
</dict>
</plist>
EOF

    echo "======================================================================"
    if [[ "$SIGN_IDENTITY" == "-" ]]; then
        echo " [3/5] Ad-Hoc Code Signing with CoreCLR JIT Entitlements ($rid)..."
        echo "      (Set MACOS_SIGN_IDENTITY to a 'Developer ID Application' identity to remove the Gatekeeper warning.)"
    else
        echo " [3/5] Developer ID Signing (Hardened Runtime) with CoreCLR JIT Entitlements ($rid)..."
    fi
    echo "======================================================================"
    if command -v xattr >/dev/null 2>&1; then
        xattr -cr "$bundle_dir" || true
    fi
    if command -v codesign >/dev/null 2>&1; then
        local sign_flags=(--force -s "$SIGN_IDENTITY")
        if [[ "$SIGN_IDENTITY" != "-" ]]; then
            # Notarization requires the hardened runtime and a secure timestamp on every Mach-O.
            sign_flags+=(--options runtime --timestamp)
        fi
        if [[ -n "${MACOS_KEYCHAIN:-}" ]]; then
            sign_flags+=(--keychain "$MACOS_KEYCHAIN")
        fi

        # Sign nested Mach-O files first (inside-out), then the main executable, then the bundle.
        while IFS= read -r -d '' bin_file; do
            if [[ "$bin_file" == "$bundle_dir/Contents/MacOS/$APP_NAME" ]]; then
                continue
            fi
            if file -b "$bin_file" | grep -q "Mach-O"; then
                codesign "${sign_flags[@]}" "$bin_file"
            fi
        done < <(find "$bundle_dir/Contents/MacOS" -maxdepth 1 -type f -print0)

        if [[ -f "$ENTITLEMENTS" ]]; then
            codesign "${sign_flags[@]}" --entitlements "$ENTITLEMENTS" "$bundle_dir/Contents/MacOS/$APP_NAME"
            codesign "${sign_flags[@]}" --entitlements "$ENTITLEMENTS" "$bundle_dir"
        else
            codesign "${sign_flags[@]}" "$bundle_dir/Contents/MacOS/$APP_NAME"
            codesign "${sign_flags[@]}" "$bundle_dir"
        fi
        codesign --verify --strict --verbose=2 "$bundle_dir"
    else
        echo "[!] 'codesign' not found (non-macOS host). Run on macOS to apply a code signature."
    fi

    if [[ "$NOTARIZE" == "1" ]]; then
        echo "======================================================================"
        echo " [3b/5] Notarizing $APP_NAME.app with Apple notary service ($rid)..."
        echo "======================================================================"
        local notary_zip="$SCRIPT_DIR/dist/notarize-$arch_suffix.zip"
        rm -f "$notary_zip"
        ditto -c -k --sequesterRsrc --keepParent "$bundle_dir" "$notary_zip"
        notarize_file "$notary_zip"
        rm -f "$notary_zip"
        xcrun stapler staple "$bundle_dir"
        xcrun stapler validate "$bundle_dir"
        spctl --assess --type execute --verbose=4 "$bundle_dir"
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
        local dmg_ok=0
        for attempt in 1 2 3 4 5; do
            if hdiutil create -volname "codeLess" -srcfolder "$dmg_staging" -ov -format UDZO "$dmg_out"; then
                dmg_ok=1
                break
            fi
            echo "[!] hdiutil busy (attempt $attempt/5), retrying in 2s..."
            rm -f "$dmg_out"
            sleep 2
        done
        rm -rf "$dmg_staging"
        if [[ "$dmg_ok" -ne 1 ]]; then
            echo "::error::hdiutil failed to create $dmg_out after 5 attempts"
            exit 1
        fi
        if [[ "$SIGN_IDENTITY" != "-" ]]; then
            local dmg_sign_flags=(--force -s "$SIGN_IDENTITY" --timestamp)
            if [[ -n "${MACOS_KEYCHAIN:-}" ]]; then
                dmg_sign_flags+=(--keychain "$MACOS_KEYCHAIN")
            fi
            codesign "${dmg_sign_flags[@]}" "$dmg_out"
        fi
        if [[ "$NOTARIZE" == "1" ]]; then
            notarize_file "$dmg_out"
            xcrun stapler staple "$dmg_out"
            xcrun stapler validate "$dmg_out"
        fi
    else
        echo "[!] 'hdiutil' not found (non-macOS host). Run on macOS to produce .dmg."
    fi

    echo ""
    echo "✓ Built App Bundle: $bundle_dir"
    if [[ -f "$dmg_out" ]]; then
        echo "✓ Built Disk Image: $dmg_out"
    fi
    if [[ -f "$zip_out" ]]; then
        echo "✓ Built Zip Bundle: $zip_out"
    fi
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
