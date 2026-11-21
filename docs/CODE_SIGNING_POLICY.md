# Code Signing Policy

This policy describes how official codeLess release binaries are built and signed.

## What is signed

| Platform | Artifacts | Signature |
| :--- | :--- | :--- |
| Windows | `codeLess.exe`, `codeLess-Setup.exe`, `codeLess-Setup.msi` | Authenticode (applied for: free open-source code signing provided by [SignPath.io](https://about.signpath.io/), certificate by [SignPath Foundation](https://signpath.org/)) |
| macOS | `codeLess.app`, `.dmg`, `.zip` | Ad-hoc today; Apple Developer ID + notarization planned |
| Linux | `.deb`, `.tar.gz` | OpenPGP detached signatures (`*.asc`) with the release key in [`docs/codeLess-release-signing-key.asc`](./codeLess-release-signing-key.asc) |
| All | every release file | GitHub artifact attestations (SLSA build provenance) and a Sigstore-signed `SHA256SUMS.txt` |

## How releases are produced

- Only binaries built by the public GitHub Actions workflow [`.github/workflows/build-and-sign.yml`](../.github/workflows/build-and-sign.yml)
  from a version tag (`v*`) of this repository are signed. Nothing built on a developer machine is submitted for signing.
- The workflow compiles from source, runs the cross-platform test suite and a launch smoke test, then submits the unsigned
  Windows artifacts to SignPath for signing; signing requests are tied to the originating workflow run.
- Release signing requires manual approval of each signing request by an approver listed below.

## Team roles

| Role | Members |
| :--- | :--- |
| Committers and reviewers | [Sambhrant Singh](https://github.com/Sambhrantsingh777) |
| Approvers | [Sambhrant Singh](https://github.com/Sambhrantsingh777) |

All changes reach `main` through commits by committers; external contributions are reviewed by a committer before merging.

## Privacy

codeLess does not collect telemetry. It contacts network services only for features the user uses or enables:

- AI requests go directly from the user's machine to the provider the user configured (or to a local model on `localhost`).
- A once-a-day update check sends an anonymous request to the public GitHub Releases API; it can be turned off in Settings and is
  skipped in Offline Mode.
- Extension search and downloads contact OpenVSX only when the user searches for or installs an extension.
- `.http` requests are sent only when the user runs them.

Every request codeLess makes is listed in **View → Network Activity**, and **Offline Mode** blocks all non-local requests.
Tools the user runs in the integrated terminal (git, package managers, language servers) are outside codeLess's control.
