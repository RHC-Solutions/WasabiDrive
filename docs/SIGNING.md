# Code signing

Release binaries are Authenticode-signed so Windows SmartScreen stops warning users. WasabiDrive
uses **[SignPath Foundation](https://signpath.org)** — free, publicly-trusted code signing for
open-source projects. The private key never leaves SignPath's HSM, so **signing only happens in
CI** (GitHub Actions) on a trusted build; you cannot sign locally with this certificate.

## One-time setup

### 1. Apply to the SignPath Foundation
- Go to <https://about.signpath.io/product/open-source> and apply, referencing
  `https://github.com/RHC-Solutions/WasabiDrive`.
- Requirements (all met): **public repo** and an **OSI-approved license** (this project is now
  [MIT](../LICENSE)). Approval is manual and can take a few days.

### 2. Configure your SignPath organization (after approval)
In the SignPath portal:
- Note your **Organization ID** (GUID).
- Create a **Project** — note its **slug** (e.g. `wasabidrive`).
- Create/confirm an **Artifact Configuration** that accepts a zip and signs the `.exe` inside it
  (the workflow uploads `WasabiDrive-Setup-<version>.exe` inside an artifact zip).
- Create a **Signing Policy** — note its **slug** (e.g. `release-signing`).
- Connect SignPath to GitHub so it trusts this repo's Actions builds (install the SignPath GitHub
  app / add the GitHub connector for `RHC-Solutions/WasabiDrive`).
- Create an **API token** for the CI user.

### 3. Add GitHub secrets & variables
In **Settings → Secrets and variables → Actions** of the repo:

| Kind | Name | Value |
|------|------|-------|
| Secret | `SIGNPATH_API_TOKEN` | the SignPath API token |
| Variable | `SIGNPATH_ORGANIZATION_ID` | your organization GUID |
| Variable | `SIGNPATH_PROJECT_SLUG` | e.g. `wasabidrive` |
| Variable | `SIGNPATH_POLICY_SLUG` | e.g. `release-signing` |

## Cutting a signed release

```powershell
scripts\release.ps1 -Version 0.5.0 -TagOnly
```

This bumps the version, commits, and pushes the tag. The
[`release-signed`](../.github/workflows/release-signed.yml) workflow then:

1. builds the installer on a Windows runner (downloading rclone + WinFsp),
2. submits it to SignPath and waits for the signed result,
3. publishes the GitHub release with the **signed** `WasabiDrive-Setup-<version>.exe`
   and the stable-named `WasabiDrive-Setup.exe`.

Watch progress at <https://github.com/RHC-Solutions/WasabiDrive/actions>.

> First signed build won't clear SmartScreen instantly for a brand-new certificate — reputation
> builds over the first downloads. It clears far faster than an unsigned binary (which never does).

## Local / internal signing (alternative)

For internal test builds you can sign locally with your own certificate instead of SignPath.
[`scripts/sign.ps1`](../scripts/sign.ps1) is invoked automatically by `build-installer.ps1` when a
certificate is configured via environment variables:

```powershell
# a cert already in the Windows store (self-signed or on a token)
$env:WASABIDRIVE_SIGN_THUMBPRINT = "ABCD...1234"
# …or a PFX file
$env:WASABIDRIVE_SIGN_PFX = "C:\path\WasabiDrive.pfx"
$env:WASABIDRIVE_SIGN_PFX_PASSWORD = "…"
scripts\build-installer.ps1
```

A self-signed certificate only removes the SmartScreen/Defender warning on machines that trust it
(install the public `.cer` into **Trusted Publishers** + **Trusted Root**, e.g. via GPO). Use
SignPath for anything distributed publicly.
