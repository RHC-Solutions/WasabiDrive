# WasabiDrive

[![Latest release](https://img.shields.io/github/v/release/RHC-Solutions/WasabiDrive?label=latest&sort=semver)](https://github.com/RHC-Solutions/WasabiDrive/releases/latest)

Mount [Wasabi](https://wasabi.com) S3 buckets as native Windows drive letters. WasabiDrive is
a WPF tray app that drives [rclone](https://rclone.org) (the mount engine) on top of
[WinFsp](https://winfsp.dev) (the user-mode filesystem), with a GUI for credentials, multiple
bucket→drive mappings, auto-mount at login, and cache tuning.

## Download

**[⬇ Download the latest version](https://github.com/RHC-Solutions/WasabiDrive/releases/latest/download/WasabiDrive-Setup.exe)**
&nbsp;·&nbsp; [All releases](https://github.com/RHC-Solutions/WasabiDrive/releases)

Run the installer (`WasabiDrive-Setup.exe`); it installs WinFsp automatically if it's missing.
Windows 10/11 x64. Already installed? The app checks for updates on startup and via
**About → Check for updates**.

## How it works

```
WasabiDrive.App (WPF tray)  →  WasabiDrive.Core  →  rclone.exe mount  →  WinFsp  →  W:\
```

Each mapping runs one `rclone mount` child process. Secrets are injected into that process via
environment variables, so the Wasabi secret key is never written to disk in plaintext nor placed
on a command line. All config lives under `%LOCALAPPDATA%\WasabiDrive\`:

| File | Contents |
|------|----------|
| `mappings.json` | bucket→drive mappings (no secrets) |
| `settings.json` | app settings (cache defaults, start-at-login) |
| `credentials.dat` | Wasabi keys, encrypted with Windows DPAPI (per-user) |
| `logs/` | daily activity logs (`wasabidrive-YYYY-MM-DD.log`, 30-day retention) |

## Prerequisites

- Windows 10/11 x64
- [WinFsp](https://winfsp.dev) — the app warns and the installer installs it if missing
- .NET 8 SDK (to build). The published app is self-contained (no runtime needed to run).

## Project layout

```
src/WasabiDrive.Core/   engine + persistence (MountManager, RcloneRunner, stores, DPAPI)
src/WasabiDrive.App/    WPF tray app (Views, ViewModels, AppController)
src/WasabiDrive.Tests/  xUnit tests
third_party/rclone/     bundled rclone.exe (pinned, see VERSION.txt)
third_party/winfsp/     bundled WinFsp MSI (for the installer)
installer/              Inno Setup script
scripts/                publish.ps1, build-installer.ps1
```

## Build & run

```powershell
dotnet build WasabiDrive.slnx           # build everything
dotnet test  src/WasabiDrive.Tests      # run unit tests
dotnet run   --project src/WasabiDrive.App
```

## Package an installer

```powershell
scripts\publish.ps1          # self-contained win-x64 publish
scripts\build-installer.ps1  # publish + compile installer (needs Inno Setup 6+)
```

The installer bundles `rclone.exe`, installs WinFsp if absent, and creates shortcuts.

## Usage

1. Launch WasabiDrive (it lives in the system tray).
2. **Add…** a mapping: bucket name, drive letter, Wasabi region, and access/secret keys.
3. Click **Mount** — the drive letter appears in Explorer.
4. Tick **Auto-mount at login** and enable **Start at login** in Settings to have drives
   reappear automatically.

## Features

- **Two mapping modes (per bucket):**
  - **Drive letter** — a virtual drive (e.g. `W:`) via rclone + WinFsp (the original mode).
  - **Files On-Demand folder** — a normal folder backed by the **Windows Cloud Files API**, just
    like OneDrive: files show in Explorer as placeholders, download only when opened, and support
    the native **Status** column/overlays and the **"Always keep on this device" / "Free up space"**
    right-click menu. Idle files are freed automatically (Storage Sense + a built-in policy using
    the mapping's *cache max age*). **Two-way sync**: local edits, new files, deletes and renames
    are pushed back to Wasabi. The folder is branded with the WasabiDrive icon.
- **Cache tuning** — mode, max size (0 = unlimited; 1 TiB = `1048576` MiB), max age, and a
  configurable **cache location** (`--cache-dir`) so a large cache can live on a roomy drive.
  Defaults and per-mapping overrides are both supported.
- **Start at login** — registered via the per-user `HKCU\...\Run` key (no administrator rights).
- **Export / Import** — back up or transfer app settings and mappings as **JSON or XML**.
  Secret keys are never exported (they are DPAPI-encrypted and bound to the user+machine).
- **In-app updates** — checks GitHub Releases on startup and from **About → Check for updates**;
  offers to download and run the latest installer.

## Notes

- Auto-mount runs in the interactive user session (not a SYSTEM service) so the drive letters are
  visible in Explorer.
- `--vfs-cache-mode full` is the default for best app compatibility; tune per-mapping or globally.
- Not code-signed yet — SmartScreen may warn on first run.

## License

Copyright © [RHC Solutions](https://rhcsolutions.com/). All rights reserved. See [LICENSE](LICENSE).
