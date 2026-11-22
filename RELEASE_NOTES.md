# codeLess v3.3.4

Driven by your bug reports from v3.3.3 on macOS: smoother editing and terminal, real in-app updates, language servers
that install themselves, and a long list of VS Code-parity fixes. Every change ships on Windows, macOS and Linux.

## New

- **Updates inside the app.** Help → Check for Updates (also in the codeLess menu on macOS) downloads the new version,
  verifies it against the release's SHA-256 checksums and offers **Restart to Update** (saving or asking about unsaved
  files first). Works for the macOS app, the Windows installer, MSI and portable builds, and the Linux tarball; the .deb
  shows the `sudo apt install` command. Offline Mode blocks it. Settings → Update mode.
- **Language servers install themselves.** Open a Python, TypeScript/JavaScript, Bash, YAML, HTML/CSS/JSON, Go, Rust,
  C# or Lua file without a server and codeLess installs one into its own folder (never system-wide, never with sudo),
  shows progress in the status bar and starts it — no more "install one with npm i -g …" pop-ups. Settings → "Install
  missing language servers automatically".
- **Smooth scrolling and caret.** Pixel-precise trackpad scrolling with momentum, animated wheel / Page Up/Down / jumps
  (VS Code's 125 ms), a gliding caret with a fading blink — and ~3–7× cheaper frames on big files. Settings to turn each
  off and choose the cursor blinking style.
- **Terminal feel like VS Code.** Steady block cursor in the text colour (outline when unfocused, bar for vim and other
  apps that ask), no blink by default, box-drawing and Powerline glyphs drawn on the cell grid, 12 px Menlo / 14 px
  Consolas with font-height rows, minimum contrast, synchronized output for TUIs, scrolled-back view stays put while
  output arrives, only changed rows repaint. Parsing 100k lines is ~9× faster.
- **Undo / Redo go to the change** — the caret lands on the edit and it is centred if it was off-screen.
- **Find history:** ↑ / ↓ in the Find, Replace and Search boxes recall earlier searches.

## Fixed

- macOS menu bar: the app menu had only Quit — About, Check for Updates and Settings (⌘,) are back.
- macOS keys like VS Code: **Control+G** Go to Line, ⌘G / ⇧⌘G Find Next / Previous, Control+Shift+G Source Control.
- Git change markers no longer mark every line when edits are far apart.
- Clicking a line no longer shows its git blame — inline blame is off by default like VS Code (Git: Toggle Line Blame).
- An empty new file closes without asking to save; Save on an untitled file asks where (it used to write
  `Untitled-1.cs` silently); new files take the workspace's language and Save As suggests its extension.
- Opening a folder no longer opens a file by itself, and the Welcome tab can be closed with no files open (VS Code's
  empty editor with shortcuts appears).
- Terminals follow File → Open Folder (idle shells restart in the new folder with its venv; busy ones are left alone).
- Windows: Node-based tools in folders with spaces (e.g. `C:\Users\John Smith`) now start.
- Settings such as editor niceties and sticky scroll no longer reset on restart (macOS / Linux).

## Trust and verification

Releases include GitHub build-provenance attestations and a Sigstore-signed `SHA256SUMS.txt`; Linux packages are also
OpenPGP-signed (`docs/codeLess-release-signing-key.asc`). See `docs/SIGNING.md`.

## Upgrade notes

- From v3.3.3 or older, download this version once by hand; from here on codeLess updates itself.
- Windows SmartScreen and macOS Gatekeeper may still warn until code-signing certificates are in place.
