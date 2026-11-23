# codeLess v3.3.5

A focused update for the terminal, and the first version you can install from inside codeLess
(**Help → Check for Updates…**, or the codeLess menu on macOS).

## Fixed

- **Pasting into full-screen terminal apps** (e.g. the Antigravity CLI `agy`, other Bubble Tea / Ink apps): codeLess now
  answers the terminal capability questions these apps ask at startup (device attributes, terminal version, foreground /
  background colour), exactly like VS Code's terminal. Without the answers some apps waited and didn't take input.
- **Edit → Paste / Copy / Select All with the terminal focused** (also ⌘V / ⌘C through the macOS menu) now go straight
  to the focused terminal, including split terminals.

## Trust and verification

Releases include GitHub build-provenance attestations and a Sigstore-signed `SHA256SUMS.txt`; Linux packages are also
OpenPGP-signed (`docs/codeLess-release-signing-key.asc`). See `docs/SIGNING.md`.

## Upgrade notes

- From v3.3.4: Help → Check for Updates… downloads, verifies and installs this version. From v3.3.3 or older, download
  it once by hand.
- Windows SmartScreen and macOS Gatekeeper may still warn until code-signing certificates are in place.
