param(
    [switch]$Install,
    [switch]$SkipInstaller
)

$ErrorActionPreference = "Stop"
$projectRoot = if ($PSScriptRoot) { $PSScriptRoot } else { Split-Path -Parent $MyInvocation.MyCommand.Path }
Set-Location $projectRoot

function Move-LockedFileAside {
    param([string]$TargetPath)
    $dir = Split-Path -Parent $TargetPath
    $leaf = Split-Path -Leaf $TargetPath
    if (Test-Path $dir) {
        Get-ChildItem -Path $dir -Filter "$leaf.prev*" -ErrorAction SilentlyContinue | ForEach-Object {
            Remove-Item $_.FullName -Force -ErrorAction SilentlyContinue
        }
    }
    if (Test-Path $TargetPath) {
        try {
            $fs = [System.IO.File]::Open($TargetPath, [System.IO.FileMode]::Open, [System.IO.FileAccess]::ReadWrite, [System.IO.FileShare]::None)
            $fs.Dispose()
        } catch {
            # Target binary is currently running — rename in-place on NTFS without killing the user's open IDE window
            $prevName = "$TargetPath.prev.$([DateTime]::UtcNow.Ticks)"
            Move-Item -Path $TargetPath -Destination $prevName -Force -ErrorAction SilentlyContinue
        }
    }
}

Move-LockedFileAside -TargetPath (Join-Path $projectRoot "codeLess.exe")
Move-LockedFileAside -TargetPath (Join-Path $projectRoot "codeLess.Core.dll")

# Remove legacy prototype files in root if present
Remove-Item -Path "MainForm.cs", "Program.cs", "EditorControl.cs", "AiComposer.cs", "VsCodeExtensionShim.cs" -Force -ErrorAction SilentlyContinue

function Invoke-SignPathArtifactSigning {
    param(
        [Parameter(Mandatory = $true)]
        [string]$ArtifactPath
    )

    $apiToken = $env:SIGNPATH_API_TOKEN
    $orgId = $env:SIGNPATH_ORGANIZATION_ID
    $projSlug = $env:SIGNPATH_PROJECT_SLUG
    $policySlug = $env:SIGNPATH_SIGNING_POLICY_SLUG
    $artifactConfigSlug = $env:SIGNPATH_ARTIFACT_CONFIGURATION_SLUG

    if ([string]::IsNullOrWhiteSpace($apiToken) -or
        [string]::IsNullOrWhiteSpace($orgId) -or
        [string]::IsNullOrWhiteSpace($projSlug) -or
        [string]::IsNullOrWhiteSpace($policySlug)) {
        Write-Host "ℹ [SignPath] Skipping Authenticode signing for $(Split-Path $ArtifactPath -Leaf) (SIGNPATH_API_TOKEN / SIGNPATH_ORGANIZATION_ID / SIGNPATH_PROJECT_SLUG / SIGNPATH_SIGNING_POLICY_SLUG not configured; see docs\SIGNING.md)."
        return $false
    }

    if (-not (Get-Module -ListAvailable -Name SignPath)) {
        Write-Host "→ Installing official SignPath PowerShell module..."
        Install-Module -Name SignPath -Scope CurrentUser -Force -AllowClobber
    }
    Import-Module SignPath -ErrorAction Stop

    $signedTempPath = "$ArtifactPath.signed"
    if (Test-Path $signedTempPath) {
        Remove-Item $signedTempPath -Force
    }

    $submitParams = @{
        InputArtifactPath  = $ArtifactPath
        ApiToken           = $apiToken
        OrganizationId     = $orgId
        ProjectSlug        = $projSlug
        SigningPolicySlug  = $policySlug
        OutputArtifactPath = $signedTempPath
        WaitForCompletion  = $true
    }
    if (-not [string]::IsNullOrWhiteSpace($artifactConfigSlug)) {
        $submitParams["ArtifactConfigurationSlug"] = $artifactConfigSlug
    }

    Write-Host "→ Submitting $(Split-Path $ArtifactPath -Leaf) to SignPath (Org=$orgId, Project=$projSlug, Policy=$policySlug)..."
    Submit-SigningRequest @submitParams | Out-Null

    if (-not (Test-Path $signedTempPath)) {
        throw "SignPath signing failed: signed output artifact was not produced for $ArtifactPath."
    }

    Move-Item -Path $signedTempPath -Destination $ArtifactPath -Force
    $sig = Get-AuthenticodeSignature -FilePath $ArtifactPath
    if ($null -eq $sig -or $sig.Status -ne "Valid") {
        $statusStr = if ($null -ne $sig) { $sig.Status } else { "Missing" }
        throw "Authenticode signature verification failed for $ArtifactPath (Status: $statusStr)."
    }
    Write-Host "✓ Verified valid Authenticode signature on $(Split-Path $ArtifactPath -Leaf) ($($sig.SignerCertificate.Subject))"
    return $true
}

$cscPath = "$env:windir\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
if (-not (Test-Path $cscPath)) {
    $cscPath = "$env:windir\Microsoft.NET\Framework\v4.0.30319\csc.exe"
}

$outExe = Join-Path $projectRoot "codeLess.exe"
$outCoreDll = Join-Path $projectRoot "codeLess.Core.dll"
$manifest = Join-Path $projectRoot "app.manifest"
$savatarrIco = Join-Path $projectRoot "assets\savatarr.ico"
$appIco = Join-Path $projectRoot "assets\savatarr_app.ico"
if ((Test-Path $savatarrIco) -and -not (Test-Path $appIco)) {
    Copy-Item $savatarrIco $appIco -Force
}
$icon = if (Test-Path $appIco) { $appIco } else { Join-Path $projectRoot "assets\codeless.ico" }

$coreSourceFiles = @(
    (Get-ChildItem -Path (Join-Path $projectRoot "src\codeLess.Core") -Filter "*.cs" | ForEach-Object { $_.FullName })
    (Join-Path $projectRoot "src\ConPtyTerminal.cs")
    (Join-Path $projectRoot "src\LlmHarnessAndAgentEngine.cs")
    (Join-Path $projectRoot "src\VsixAndPluginHostEngine.cs")
)

# Bundled MIT TextMate grammars (src\codeLess.Core\Grammars\*.gz) are embedded as resources
$grammarResources = @(Get-ChildItem -Path (Join-Path $projectRoot "src\codeLess.Core\Grammars") -Filter "*.gz" -ErrorAction SilentlyContinue |
    ForEach-Object { "/resource:$($_.FullName),codeLess.Grammars.$($_.Name)" })

& $cscPath /nologo /codepage:65001 /target:library /optimize+ /define:NETFRAMEWORK `
    /out:$outCoreDll `
    /r:System.dll `
    /r:System.Core.dll `
    /r:System.Drawing.dll `
    /r:System.Web.Extensions.dll `
    /r:System.IO.Compression.dll `
    /r:System.IO.Compression.FileSystem.dll `
    /r:System.Net.Http.dll `
    $grammarResources `
    $coreSourceFiles

if ($LASTEXITCODE -ne 0) {
    Write-Error "codeLess.Core.dll compilation failed with exit code $LASTEXITCODE"
    exit $LASTEXITCODE
}
$coreSizeKb = [Math]::Round((Get-Item $outCoreDll).Length / 1KB, 1)
Write-Host "✓ Compiled codeLess.Core.dll successfully ($coreSizeKb KB)"

$sourceFiles = @(
    "src\CoreModelsAndThemes.cs",
    "src\ConPtyTerminal.cs",
    "src\CodeLessEditorCanvas.cs",
    "src\StudioDialogs.cs",
    "src\NetworkActivityForm.cs",
    "src\CodeLessEditorCanvas.Debug.cs",
    "src\CodeLessMainForm.Debug.cs",
    "src\LanguageServerWinForms.cs",
    "src\CodeLessEditorCanvas.DevTools.cs",
    "src\CodeLessEditorCanvas.Niceties.cs",
    "src\DevToolsWinForms.cs",
    "src\CodeLessMainForm.DevTools.cs",
    "src\CodeLessEditorCanvas.Parity.cs",
    "src\CodeLessEditorCanvas.Layout.cs",
    "src\CodeLessMainForm.Parity.cs",
    "src\CodeLessMainForm.EditorNiceties.cs",
    "src\LargeFileViewerWinForms.cs",
    "src\CodeLessMainForm.Smoke.cs",
    "src\CodeLessMainForm.AgentTrust.cs",
    "src\CodeLessMainForm.cs"
)

& $cscPath /nologo /codepage:65001 /target:winexe /optimize+ "/define:NETFRAMEWORK;CODELESS_WINFORMS_UI" `
    /out:$outExe `
    /win32manifest:$manifest `
    /win32icon:$icon `
    /r:$outCoreDll `
    "/resource:$outCoreDll,codeLess.Core.dll" `
    /r:System.dll `
    /r:System.Core.dll `
    /r:System.Drawing.dll `
    /r:System.Windows.Forms.dll `
    /r:System.Web.Extensions.dll `
    /r:System.IO.Compression.dll `
    /r:System.IO.Compression.FileSystem.dll `
    /r:Microsoft.VisualBasic.dll `
    $sourceFiles

if ($LASTEXITCODE -eq 0) {
    $sizeKb = [Math]::Round((Get-Item $outExe).Length / 1KB, 1)
    Write-Host "✓ Compiled codeLess.exe successfully ($sizeKb KB)"

    # Step 1: Sign codeLess.exe via SignPath BEFORE Inno Setup packages it
    Invoke-SignPathArtifactSigning -ArtifactPath $outExe | Out-Null

    if (-not $SkipInstaller) {
        $isccPaths = @(
            "C:\Program Files (x86)\Inno Setup 6\ISCC.exe",
            "C:\Program Files\Inno Setup 6\ISCC.exe",
            "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe"
        )
        $iscc = $isccPaths | Where-Object { Test-Path $_ } | Select-Object -First 1
        if ($null -ne $iscc) {
            & $iscc /Q (Join-Path $projectRoot "Installer.iss")
            $builtSetup = Join-Path $projectRoot "Output\codeLess-Setup.exe"
            if (Test-Path $builtSetup) {
                # Step 2: Sign codeLess-Setup.exe via SignPath AFTER Inno Setup builds it
                Invoke-SignPathArtifactSigning -ArtifactPath $builtSetup | Out-Null
                Copy-Item $builtSetup (Join-Path $projectRoot "codeLess-Setup.exe") -Force
                Write-Host "✓ Rebuilt Output\codeLess-Setup.exe installer successfully"

                if ($Install) {
                    $installDir = Join-Path $env:LOCALAPPDATA "Programs\codeLess"
                    if (-not (Test-Path $installDir)) {
                        New-Item -ItemType Directory -Path $installDir -Force | Out-Null
                    }
                    $runningInstalled = @(Get-Process -Name "codeLess" -ErrorAction SilentlyContinue | Where-Object { $_.Path -eq (Join-Path $installDir "codeLess.exe") })
                    if ($runningInstalled.Count -eq 0) {
                        Start-Process -FilePath $builtSetup -ArgumentList '/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /TASKS="desktopicon"' -Wait -PassThru | Out-Null
                    } else {
                        Move-LockedFileAside -TargetPath (Join-Path $installDir "codeLess.exe")
                        Move-LockedFileAside -TargetPath (Join-Path $installDir "codeLess.Core.dll")
                        Write-Host "ℹ Updated installed binaries in-place without closing your active codeLess window (restart codeLess to load new binary)."
                    }
                    Copy-Item $outExe (Join-Path $installDir "codeLess.exe") -Force
                    Copy-Item $outCoreDll (Join-Path $installDir "codeLess.Core.dll") -Force
                    $instAssets = Join-Path $installDir "assets"
                    if (-not (Test-Path $instAssets)) { New-Item -ItemType Directory -Path $instAssets -Force | Out-Null }
                    foreach ($assetFile in @("codeless.ico", "savatarr.ico", "savatarr_app.ico", "savatarr_bolt.png", "savatarr_full_logo.png")) {
                        $srcAsset = Join-Path $projectRoot "assets\$assetFile"
                        if (Test-Path $srcAsset) {
                            Copy-Item $srcAsset (Join-Path $instAssets $assetFile) -Force
                        }
                    }
                    try {
                        $wsh = New-Object -ComObject WScript.Shell
                        $newIconLoc = "$(Join-Path $instAssets 'savatarr_app.ico'),0"
                        $lnkPaths = @(
                            (Join-Path ([Environment]::GetFolderPath("Desktop")) "codeLess IDE.lnk"),
                            (Join-Path ([Environment]::GetFolderPath("Programs")) "codeLess IDE.lnk")
                        )
                        foreach ($lp in $lnkPaths) {
                            if (Test-Path $lp) {
                                $sc = $wsh.CreateShortcut($lp)
                                $sc.IconLocation = $newIconLoc
                                $sc.Save()
                            }
                        }
                    } catch { }
                    try {
                        Add-Type -TypeDefinition @"
using System;
using System.Runtime.InteropServices;
public static class ShellIconRefresh {
    [DllImport("shell32.dll")]
    public static extern void SHChangeNotify(int wEventId, uint uFlags, IntPtr dwItem1, IntPtr dwItem2);
}
"@ -ErrorAction SilentlyContinue
                        [ShellIconRefresh]::SHChangeNotify(0x08000000, 0x0000, [IntPtr]::Zero, [IntPtr]::Zero)
                        & "$env:SystemRoot\System32\ie4uinit.exe" -show | Out-Null
                    } catch { }
                    Write-Host "✓ Installed latest codeLess.exe to $installDir and refreshed Windows icon cache"
                }
            }
        }
    }
} else {
    Write-Error "Compilation failed with exit code $LASTEXITCODE"
    exit $LASTEXITCODE
}
