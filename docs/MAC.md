# codeLess for macOS (`osx-arm64` & `osx-x64`)

**Application Name:** `codeLess`  
**Bundle Identifier:** `com.savatarr.codeless`  
**Version:** `3.3.4`  
**Minimum macOS Version:** `macOS 11.0 (Big Sur)` or later  
**Framework:** `.NET 8.0` + `Avalonia 11.1` + `codeLess.Core`

---

## 1. Building Natively on macOS

`build-mac.sh` compiles `src/codeLess.Avalonia/codeLess.Avalonia.csproj` as a self-contained native app bundle (`codeLess.app`), generates the multi-resolution Apple icon (`codeLess.icns`), applies an ad-hoc code signature (`codesign`), and packages both a `.zip` archive and a compressed UDIF `.dmg` disk image in `dist/`.

If the `.NET 8 SDK` is not yet installed on your Mac, `build-mac.sh` installs it per-user into `~/.dotnet` automatically.

```bash
chmod +x ./build-mac.sh

# Build for the current Mac's architecture (Apple Silicon arm64 or Intel x64)
./build-mac.sh

# Or build both Apple Silicon (osx-arm64) and Intel (osx-x64) bundles
./build-mac.sh both
```

Output artifacts in `dist/`:
- `dist/macos-arm64/codeLess.app` · `dist/codeLess-mac-arm64.dmg` (`~46.8 MB`) · `dist/codeLess-mac-arm64.zip`
- `dist/macos-x64/codeLess.app` · `dist/codeLess-mac-x64.dmg` (`~48.3 MB`) · `dist/codeLess-mac-x64.zip`

---

## 2. Installing & First-Run Gatekeeper Step

### Installing from `.dmg`
1. Open `codeLess-mac-arm64.dmg` (Apple Silicon M1/M2/M3/M4) or `codeLess-mac-x64.dmg` (Intel Mac).
2. Drag **`codeLess.app`** into **`Applications`**.
3. **First-Run Gatekeeper Note (Unsigned / Ad-Hoc Signed Builds)**:  
   Community and CI builds signed ad-hoc (`codesign -s -`) without a paid Apple notarization ticket receive a `com.apple.quarantine` attribute when downloaded via a browser. Clear it once before first launch:

   **Terminal (one command):**
   ```bash
   xattr -cr /Applications/codeLess.app
   open /Applications/codeLess.app
   ```

   **Or via Finder / System Settings:**
   1. Control-click **`codeLess.app`** in `/Applications` and select **Open**.
   2. On macOS Sonoma / Sequoia, open **System Settings → Privacy & Security**, scroll down, and click **Open Anyway** next to `codeLess.app`.

---

## 3. macOS Native Integration Highlights

- **Global macOS Menu Bar (`NativeMenu`)**: Native `codeLess`, `File`, `Edit`, `Selection`, `View`, `Go`, `Run`, `Terminal`, and `Help` menus in the macOS system menu bar.
- **Command (`⌘`) & Option (`⌥`) Keymap**: Standard macOS bindings (`⌘P` Quick Open, `⌘⇧P` Command Palette, `⌘D` Add Next Occurrence, `⌘/` Toggle Comment, `⌘B` Sidebar, `⌘\` Split Editor, `⌘,` Settings).
- **Unix PTY Terminal (`forkpty`)**: Launches your login shell (`$SHELL` — `zsh` or `bash`) inside the workspace folder with `TERM=xterm-256color`, `COLORTERM=truecolor`, and live `TIOCSWINSZ` window resize propagation.
