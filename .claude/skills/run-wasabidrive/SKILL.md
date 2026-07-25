---
name: run-wasabidrive
description: Build, launch, run, screenshot, and drive the WasabiDrive WPF tray app on Windows. Use when asked to run or start WasabiDrive, screenshot its window, verify a UI change (menus, activity log, mapping list), check which rclone mount flags a mapping actually produces, or build the Release installer.
---

# Run WasabiDrive

WPF tray app (.NET 8, Windows-only) that supervises one `rclone mount` child process per mapping.
There is no headless mode and no CLI surface, so it is driven through
`.claude/skills/run-wasabidrive/driver.ps1` — a PowerShell harness over UI Automation that reads the
real window, plus process inspection for the functional checks.

All paths below are relative to the repo root. Every command here was run on Windows 11 with
PowerShell 7.

> **This driver has real side effects.** Launching the app mounts the user's actual Wasabi buckets
> using the credentials in `%LOCALAPPDATA%\WasabiDrive\credentials.dat`, and `stop` unmounts a drive
> they may be using. There is no test fixture. Prefer the read-only commands (`health`, `args`,
> `rows`, `rowmenu`, `shot`) over `stop`/`launch` when the app is already running.

## Prerequisites

Already present on this machine; listed for a clean box:

- .NET 8 SDK
- [WinFsp](https://winfsp.dev) — required to mount; the installer bundles it
- Inno Setup 6 — only to build the installer: `winget install JRSoftware.InnoSetup`

## Build and test

```powershell
dotnet build WasabiDrive.slnx
dotnet test src/WasabiDrive.Tests
```

Release build + installer (writes `installer\output\WasabiDrive-Setup-<version>.exe`):

```powershell
& .\scripts\build-installer.ps1
```

Installing needs elevation, which raises a UAC prompt a human must approve:

```powershell
Start-Process .\installer\output\WasabiDrive-Setup-0.6.0.exe `
  -ArgumentList '/VERYSILENT','/NORESTART','/SUPPRESSMSGBOXES' -Verb RunAs -Wait
```

Version lives in three files and must stay in sync — `src/WasabiDrive.App/WasabiDrive.App.csproj`
(`<Version>`), `installer/WasabiDrive.iss` (`#define AppVersion`), and
`src/WasabiDrive.App/app.manifest` (`assemblyIdentity version`, `X.Y.Z.0`). `scripts\release.ps1
-Version X.Y.Z` bumps all three, but it also commits, tags, and publishes a GitHub release — do not
run it just to get a build.

## Run (agent path)

```powershell
$d = '.\.claude\skills\run-wasabidrive\driver.ps1'

& $d health                     # process, exe path, version, rclone, W:, real log errors
& $d args                       # the live rclone mount command line
& $d rows                       # mapping rows with their cell values
& $d rowmenu                    # select row 0, open its context menu, LIST THE ITEMS
& $d traymenu                   # best-effort; see Gotchas
& $d tree -Filter 'Button'      # UIA element dump, regex-filtered
& $d shot                       # screenshot -> %TEMP%\wasabidrive-driver\window.png
& $d stop                       # stop app + rclone, release W:
& $d launch -Build installed    # or -Build release / -Build debug
```

`rowmenu` is the primary way to verify menu changes. It reads the menu's actual items and their
enabled state through UI Automation, so it does not depend on a screenshot landing on top:

```
row [0]: Wasabi | rhcsolutions | Drive | W: | Mounted | Mount | Unmount
context menu items:
   - Mount  [enabled=False]
   - Unmount  [enabled=True]
   - Edit…  [enabled=True]
   - Delete  [enabled=True]
   - Open in Explorer  [enabled=True]
   - Copy drive path  [enabled=True]
```

`args` is the strongest functional check for anything touching mount behaviour: rclone exits on an
unknown flag, so a live process proves it accepted every flag `RcloneRunner.BuildMountArguments`
emitted.

```
"C:\Program Files\WasabiDrive\rclone.exe" mount wasabi_<id>:rhcsolutions W: --vfs-cache-mode full
  --dir-cache-time 60s --buffer-size 16Mi --volname Wasabi --no-console --network-mode
  --log-level DEBUG --vfs-cache-max-size 10240Mi --vfs-cache-max-age 3600s
  --vfs-read-chunk-streams 16 --vfs-read-chunk-size 4Mi --vfs-read-ahead 128Mi --transfers 8
  --s3-upload-concurrency 4 --s3-chunk-size 16Mi --use-server-modtime --vfs-fast-fingerprint
```

`shot` uses `PrintWindow(PW_RENDERFULLCONTENT)`, so it captures the window **even when fully
covered** and never steals focus. Read the PNG afterwards — a black frame means it never rendered.

## Run (human path)

`dotnet run --project src/WasabiDrive.App` opens the window and blocks. Useless for verification;
use the driver.

## Gotchas

- **Single-instance mutex.** A second instance shows a message box and exits immediately. `launch`
  throws rather than silently doing nothing — run `stop` first.
- **Kill order matters.** Killing `WasabiDrive.exe` alone orphans its `rclone.exe` child, so `W:`
  stays mounted and the next launch fails with *"drive is already in use by another volume"*. Always
  stop the supervisor first, then rclone (what `stop` does).
- **WPF GridView rows are `ControlType.DataItem`, not `ListItem`.** Searching for `ListItem` returns
  zero results. Their `Name` is the view-model type name
  (`WasabiDrive.App.ViewModels.MappingViewModel`); the visible values are child `Text` elements.
- **Context menus are separate top-level windows.** A WPF `ContextMenu` (and the tray menu) is its
  own popup window, *not* a descendant of the app window — search from the desktop root. You must
  then filter by `ProcessId`, otherwise you collect the menu bar of every running app (VS Code's
  File/Edit/…, Outlook's and Word's ribbons — dozens of bogus items).
- **`BoundingRectangle` can be `±Infinity`** for collapsed/offscreen elements; an unguarded `[int]`
  cast throws *"Value was either too large or too small for an Int32"*.
- **`SetForegroundWindow` is refused** for a process that does not already own the foreground. It
  returns without raising the window, so a `CopyFromScreen` shot silently captures whatever *is* on
  top. This is why `shot` uses `PrintWindow`; `-Focus` opts into the unreliable path.
- **Synthetic mouse clicks are unreliable and rude.** They fight the human's own input on a machine
  in use and can land on the wrong control. `rowmenu` uses `SelectionItemPattern.Select()` +
  `SetFocus()` + `Shift+F10` instead — no cursor movement, and immune to DPI scaling.
- **Tray icon is not reachable via UI Automation.** Windows 11 keeps third-party notification icons
  in the "Show hidden icons" overflow flyout, which is absent from the UIA tree until a human opens
  it, and `Shell_TrayWnd` does not reliably enumerate as a root child. Verify the tray menu by hand
  against `TrayIconFactory.Create`. Beware substring matching on `WasabiDrive` here: it collides with
  Explorer's own toolbar (`Refresh "WasabiDriveCache" (F5)`) and VS Code status-bar items.
- **The window may sit on a secondary monitor with negative coordinates.** Do not assume positive
  offsets when reasoning about rects.
- **Scrollbar buttons report raw SVG path geometry as their automation `Name`**
  (`M19.091797,14.970703L10,5.888672…`). Harmless, but it makes `tree` output noisy.
- **Do not grep the log for `Error`.** Object keys in this bucket contain the word (e.g. *"Async
  Status and Error Handling.mp4"*), so a naive match returns dozens of false hits. Match the severity
  field: `'\s(ERROR|CRITICAL)\s+:'`. The one real recurring entry, `symlinks not supported without
  the --links flag: /`, is benign rclone noise on mount.
- **`VerboseLogging: true`** in `settings.json` runs rclone at `DEBUG`, which turns the activity log
  into a firehose. That is the setting to toggle when testing log UI behaviour.
- **Cache location is per-mapping, not global.** `settings.json → DefaultCache.CacheDir` only applies
  to *new* mappings. An existing mapping with no `CacheDir` emits no `--cache-dir` flag and uses
  rclone's default `%LOCALAPPDATA%\rclone` — so a configured cache folder can sit empty. Confirm with
  `args`, not with the Settings dialog.

## Troubleshooting

| Symptom | Cause / fix |
|---|---|
| `launch` throws "Already running" | An instance holds the mutex. `& $d stop` first. |
| Mount fails, *"drive is already in use"* | Orphaned `rclone.exe` still owns `W:`. `& $d stop`. |
| `rows` returns nothing | No mappings configured, or you searched for `ListItem` instead of `DataItem`. |
| `rowmenu` prints "(none found)" | The row never took keyboard focus, so `Shift+F10` went elsewhere. Ensure the window is not minimised. |
| Menu dump lists File/Edit/View/Terminal | You forgot the `ProcessId` filter — that is VS Code's menu bar. |
| Screenshot shows a browser/another app | `CopyFromScreen` with a failed `SetForegroundWindow`. Drop `-Focus`. |
| `ISCC.exe not found` | Inno Setup installs to `%LOCALAPPDATA%\Programs\Inno Setup 6\ISCC.exe`, not Program Files. `build-installer.ps1` already probes there. |
| Build warns "No signing certificate configured" | Expected — builds are unsigned unless `WASABIDRIVE_SIGN_*` is set. It also re-signs every historical setup exe in `installer\output`, hence the repeated warnings. |
