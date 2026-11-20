# Authenticode Code Signing & Windows SmartScreen Guide (`codeLess` by Savatarr)

## 1. Why Windows Shows "Unknown Publisher" on Unsigned Builds

In `codeLess`, we have implemented every legitimate binary hygiene and metadata best practice to prevent false-positive heuristic detections:
- **Complete PE `VERSIONINFO` resources** in both `codeLess.exe` and `codeLess-Setup.exe` (`CompanyName=Savatarr`, `ProductName=codeLess`, `FileDescription`, `FileVersion=3.2.0.0`, `LegalCopyright`, `OriginalFilename`).
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
