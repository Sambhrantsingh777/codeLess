# Authenticode Code Signing & Windows SmartScreen Guide (`codeLess` by Savatarr)

## 1. Why Windows Shows "Unknown Publisher" on Unsigned Builds

In `codeLess`, we have implemented every legitimate binary hygiene and metadata best practice to prevent false-positive heuristic detections:
- **Complete PE `VERSIONINFO` resources** in both `codeLess.exe` and `codeLess-Setup.exe` (`CompanyName=Savatarr`, `ProductName=codeLess`, `FileDescription`, `FileVersion=3.3.2.0`, `LegalCopyright`, `OriginalFilename`).
- **Embedded Application Manifest (`app.manifest`)** declaring `requestedExecutionLevel level="asInvoker" uiAccess="false"` and explicit Windows 10/11 compatibility GUIDs.
- **Least-Privilege Installer (`Installer.iss`)** with `PrivilegesRequired=lowest`, stable `AppId`, `AppPublisher=Savatarr`, and clean uninstall registration.
- **Zero packers, obfuscators, or heuristic triggers** (no `EmptyWorkingSet` / `SetProcessWorkingSetSize` memory-trimming calls and no hidden `taskkill.exe` process-tree spawning).
- **Verified Clean in Microsoft Defender**: Both `codeLess.exe` and `codeLess-Setup.exe` pass `MpCmdRun.exe -Scan -ScanType 3` with **0 threats found**.

However, **Microsoft Defender SmartScreen** (the *"Windows protected your PC — Unknown publisher"* prompt shown when running an installer downloaded from the browser with Mark-of-the-Web) is **not** a malware scan—it is an **Authenticode identity and reputation check**.

**Removing the SmartScreen "Unknown publisher" warning requires signing both `codeLess.exe` and `codeLess-Setup.exe` with a trusted Authenticode code-signing certificate.**
- With a standard **OV (Organization Validation)** certificate (including SignPath), the publisher name (`Savatarr`) is immediately verified and shown instead of `Unknown publisher`, though SmartScreen download reputation may still take a short period of install volume to build up.
- With an **EV (Extended Validation)** certificate, SignPath Foundation established open-source certificates, or **Azure Trusted Signing**, reputation is established much faster.

---

## 2. Setting Up SignPath (`signpath.io`)

`Compile.ps1` and `.github/workflows/build-and-sign.yml` are pre-wired for **SignPath** (`signpath.io`).

### Step 1: Create a SignPath Account or Apply to SignPath Foundation
1. Go to [https://about.signpath.io/](https://about.signpath.io/) and create an organization account (or apply for a free open-source certificate via [SignPath Foundation](https://about.signpath.io/product/open-source) if `codeLess` is distributed as open-source).
2. Note your **Organization ID** (GUID) from the SignPath dashboard URL or Organization Settings.

### Step 2: Create a Project, Artifact Configuration, and Signing Policy
1. In SignPath, go to **Projects** → **Create Project** and set the slug to `codeless`.
2. Under **Artifact Configurations**, create an artifact configuration (for example, `pe-exe` or an Inno Setup configuration that permits signing `codeLess.exe` and `codeLess-Setup.exe`).
3. Under **Signing Policies**, create a signing policy (e.g. `test-signing` for test certificates or `release-signing` for production CA-backed certificates).
4. If using **SignPath Foundation** (free OSS tier), connect your GitHub repository as a **Trusted Build System** so SignPath can verify the origin of artifacts built by `.github/workflows/build-and-sign.yml`.

### Step 3: Generate a CI User API Token
1. In SignPath, create a **CI User** (or API token under your user profile) and assign it the **Submitter** role on the `codeless` project's signing policy.
2. Copy the generated **API Token** (keep it secret; never commit it to source control).

---

## 3. Local Signing via `Compile.ps1`

`Compile.ps1` automatically checks for the following environment variables. When all 4 required variables are set, `Compile.ps1`:
1. Compiles `codeLess.exe`.
2. Submits `codeLess.exe` to SignPath (`Submit-SigningRequest`) **before** Inno Setup packages it.
3. Verifies `codeLess.exe` with `Get-AuthenticodeSignature` (failing the build if `Status -ne 'Valid'`).
4. Builds `Output\codeLess-Setup.exe` via Inno Setup (`ISCC.exe`).
5. Submits `Output\codeLess-Setup.exe` to SignPath (`Submit-SigningRequest`) and verifies its Authenticode signature.

When the environment variables are **not** set, `Compile.ps1` skips signing cleanly with an informational message.

### PowerShell Environment Variables
```powershell
$env:SIGNPATH_API_TOKEN                   = "<your-secret-signpath-api-token>"
$env:SIGNPATH_ORGANIZATION_ID             = "<your-organization-guid>"
$env:SIGNPATH_PROJECT_SLUG                = "codeless"
$env:SIGNPATH_SIGNING_POLICY_SLUG         = "release-signing"
# Optional:
$env:SIGNPATH_ARTIFACT_CONFIGURATION_SLUG = "pe-executable"

.\Compile.ps1
```

---

## 4. Automated CI Signing via GitHub Actions

To enable automated signing in `.github/workflows/build-and-sign.yml`:
1. In your GitHub repository, go to **Settings → Secrets and variables → Actions**.
2. Add the following **Repository Secret**:
   - `SIGNPATH_API_TOKEN`: Your SignPath CI User API token.
3. Add the following **Repository Variables**:
   - `SIGNPATH_ORGANIZATION_ID`: Your SignPath Organization ID.
   - `SIGNPATH_PROJECT_SLUG`: `codeless`
   - `SIGNPATH_SIGNING_POLICY_SLUG`: `release-signing`
   - `SIGNPATH_EXE_ARTIFACT_CONFIGURATION_SLUG`: Artifact configuration slug for `codeLess.exe` (optional if default).
   - `SIGNPATH_ARTIFACT_CONFIGURATION_SLUG`: Artifact configuration slug for `codeLess-Setup.exe` (optional if default).

---

## 5. macOS: Removing the Gatekeeper "unidentified developer / unsafe" Warning

macOS Gatekeeper blocks any downloaded app that is not **signed with an Apple Developer ID certificate and notarized by Apple**. Ad-hoc signatures (`codesign -s -`, the default in `build-mac.sh`) always trigger the warning; no code change can avoid it. Removing it requires an [Apple Developer Program](https://developer.apple.com/programs/) membership (US$99/year).

`build-mac.sh` and the `build-macos` CI job are pre-wired: when credentials are present they sign every Mach-O with the **hardened runtime + secure timestamp**, notarize the `.app` and `.dmg` via `notarytool`, **staple** the tickets, and verify with `spctl`. Without credentials they fall back to ad-hoc signing.

### Step 1: Create the certificate
1. In Xcode → Settings → Accounts (or developer.apple.com → Certificates), create a **Developer ID Application** certificate.
2. Export it from Keychain Access as a `.p12` with a password.
3. Note the identity name, e.g. `Developer ID Application: Savatarr (ABCDE12345)` (`security find-identity -v -p codesigning`).

### Step 2: Create notarization credentials (pick one)
- **App Store Connect API key (recommended):** App Store Connect → Users and Access → Integrations → Keys → generate a key with *Developer* access; download `AuthKey_XXXX.p8`, note the Key ID and Issuer ID.
- **Apple ID:** generate an app-specific password at appleid.apple.com and note your Team ID.

### Step 3a: Local signed build
```bash
export MACOS_SIGN_IDENTITY="Developer ID Application: Savatarr (ABCDE12345)"
export APPLE_API_KEY_PATH=~/keys/AuthKey_XXXX.p8 APPLE_API_KEY_ID=XXXX APPLE_API_ISSUER_ID=xxxxxxxx-xxxx-...
# or: export APPLE_ID=you@example.com APPLE_TEAM_ID=ABCDE12345 APPLE_APP_SPECIFIC_PASSWORD=xxxx-xxxx-xxxx-xxxx
# or: export NOTARY_KEYCHAIN_PROFILE=codeless   (after `xcrun notarytool store-credentials codeless ...`)
./build-mac.sh both
```

### Step 3b: GitHub Actions
Add under **Settings → Secrets and variables → Actions**:

| Kind | Name | Value |
| :--- | :--- | :--- |
| Secret | `MACOS_CERTIFICATE_P12_BASE64` | `base64 -i cert.p12 \| pbcopy` |
| Secret | `MACOS_CERTIFICATE_PASSWORD` | `.p12` export password |
| Variable | `MACOS_SIGN_IDENTITY` | `Developer ID Application: Savatarr (ABCDE12345)` |
| Secret | `APPLE_API_KEY_P8_BASE64`, `APPLE_API_KEY_ID`, `APPLE_API_ISSUER_ID` | API-key method |
| Secret | `APPLE_ID`, `APPLE_TEAM_ID`, `APPLE_APP_SPECIFIC_PASSWORD` | *or* Apple ID method |

Once set, every non-PR build produces notarized, stapled `.dmg`/`.zip` files that open with no Gatekeeper prompt. Until then, users must run `xattr -cr /Applications/codeLess.app` once (see `docs/MAC.md`).

---

## 6. Account-free trust that is always on: provenance attestations + Sigstore

Every tagged release (all platforms) is published with:
- **GitHub artifact attestations** (SLSA build provenance) for each `.exe`, `.msi`, `.dmg`, `.zip`, `.deb` and `.tar.gz`, signed through
  Sigstore with the identity of this repository's release workflow and recorded in the public transparency log.
- **`SHA256SUMS.txt.sigstore.json`**: a keyless Sigstore signature over `SHA256SUMS.txt`.

These need no certificate purchase and prove that a file was built by this repository's CI from a specific commit:

```bash
# Any release file (requires the GitHub CLI)
gh attestation verify codeLess-mac-arm64.dmg --repo Sambhrantsingh777/codeLess

# Checksums (requires cosign), then the files themselves
cosign verify-blob --bundle SHA256SUMS.txt.sigstore.json \
  --certificate-identity-regexp '^https://github.com/Sambhrantsingh777/codeLess/' \
  --certificate-oidc-issuer https://token.actions.githubusercontent.com SHA256SUMS.txt
sha256sum -c SHA256SUMS.txt --ignore-missing      # macOS: shasum -a 256 -c SHA256SUMS.txt
```

They do **not** replace OS code signing: Windows SmartScreen and macOS Gatekeeper only trust Authenticode / Apple Developer ID
signatures (sections 2–5).

## 7. Linux: OpenPGP signatures (optional)

Linux has no OS-level publisher gate; distributions and users verify OpenPGP signatures. To publish `*.asc` detached signatures
plus `codeLess-release-signing-key.asc` with every release, create a dedicated signing key and add two repository secrets:

```bash
gpg --quick-gen-key "Savatarr codeLess Releases <releases@savatarr.com>" ed25519 sign 2y
gpg --armor --export-secret-keys "Savatarr codeLess Releases"   # -> secret RELEASE_GPG_PRIVATE_KEY
#                                                                 passphrase -> secret RELEASE_GPG_PASSPHRASE
```
Publish the key fingerprint on savatarr.com so users can check it. Verify with `gpg --verify codeLess-linux-x64.deb.asc`.

**Current release key:** `Savatarr codeLess Releases <releases@savatarr.com>` (Ed25519, expires 2028-11) — fingerprint
`0C91 94FC D89C 73F5 97E7 B667 C8FB 23A0 D960 FC0E`. Public key: [`docs/codeLess-release-signing-key.asc`](./codeLess-release-signing-key.asc).
```bash
gpg --import docs/codeLess-release-signing-key.asc
gpg --verify codeLess-linux-x64.deb.asc codeLess-linux-x64.deb
```

## 8. Status checklist

| Platform | Mechanism | Needs | State |
| :--- | :--- | :--- | :--- |
| All | Build provenance attestations + Sigstore-signed checksums | nothing | **on** for every tagged release |
| Windows | Authenticode via SignPath (OSS: free via SignPath Foundation application; or paid OV/EV; or Azure Trusted Signing) | account + `SIGNPATH_*` secret/variables | wired, waiting for credentials |
| macOS | Developer ID + notarization + stapling | Apple Developer Program ($99/yr) + `MACOS_*`/`APPLE_*` secrets | wired, waiting for credentials |
| Linux | OpenPGP detached signatures | `RELEASE_GPG_PRIVATE_KEY` / `RELEASE_GPG_PASSPHRASE` secrets | wired, optional |
