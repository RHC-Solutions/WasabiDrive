# WasabiDrive — testing checklist

Manual smoke tests for a release, focused on the parts that can only be verified interactively on
a real desktop with real Wasabi credentials. Tick each box; note anything that misbehaves.

## Setup

- [ ] Windows 10 version 1709+ (build 16299) or Windows 11, x64.
- [ ] Install `WasabiDrive-Setup.exe` (latest from
      <https://github.com/RHC-Solutions/WasabiDrive/releases/latest>). Accept the WinFsp install if prompted.
- [ ] Launch WasabiDrive (system tray). No rclone/WinFsp warning banner in the window.
- [ ] Have a Wasabi bucket with a few files, including at least one in a sub-folder
      (e.g. `docs/report.pdf`), and one large-ish file (10+ MB) to make hydration visible.

**Where the logs are:** the **Activity log** pane in the window, and daily files at
`%LOCALAPPDATA%\WasabiDrive\logs\wasabidrive-YYYY-MM-DD.log`. Open the folder from
**Settings → Open logs folder**.

Sync state (for on-demand) lives at `%LOCALAPPDATA%\WasabiDrive\sync\<mappingId>.db` (SQLite;
pre-0.9 `.json` files are imported once and left behind as `.json.migrated`) — deleting it forces a
re-scan of the folders you open.

---

## A. Login task (the original bug)

- [ ] Settings → tick **Start WasabiDrive at login** → Save. **No "Access is denied" dialog.**
- [ ] Confirm `HKCU\Software\Microsoft\Windows\CurrentVersion\Run` has a `WasabiDrive` value.
- [ ] Untick and Save → the value is removed.

## B. Drive-letter mode (regression)

- [ ] Add a mapping, **Mode = Drive letter**, pick a bucket + drive letter, enter keys, Mount.
- [ ] The drive letter appears in Explorer and files are browsable. Unmount removes it.

## C. Files On-Demand — browse & hydrate

- [ ] Add a mapping, **Mode = On-demand folder**, same bucket, enter keys, Mount.
      The chosen folder opens in Explorer.
- [ ] The **top level** of the bucket appears straight away, on a big bucket too. Log shows
      `Listed '/': N file(s), M folder(s)` and `M folder(s) will list themselves when opened`.
- [ ] Sub-folders start empty and fill in when opened — one `Listed '<prefix>/': ...` line per
      folder you click, and **no** line for folders you never open.
- [ ] Mount a bucket with a very large root; the app's memory stays flat and the window stays
      responsive while the folder opens (this is what 0.9 fixed).
- [ ] Explorer's **Status** column shows the cloud/placeholder glyph (add the column if hidden).
      Files show ~0 bytes "size on disk".
- [ ] Double-click the large file → it downloads and opens; Status flips to available (green check).
      Log shows no hydration error.

## D. Pin / free up space (native menu)

- [ ] Right-click a file → **Always keep on this device** → it hydrates and stays (green check).
- [ ] Right-click a hydrated file → **Free up space** → it returns to cloud-only (placeholder glyph),
      size on disk drops to ~0. Content still opens on next double-click.

## E. Explorer sidebar entry

- [ ] A **WasabiDrive - <name>** entry with the app icon appears in Explorer's navigation pane.
- [ ] Clicking it opens the on-demand folder.
- [ ] Delete the mapping in the app → the sidebar entry disappears (may need an Explorer refresh /
      sign-out). No leftover keys under `HKCU\Software\Classes\CLSID\{mappingId}`.

## F. Two-way sync (local → cloud)

- [ ] **Create** a new file in the folder → within a few seconds the log shows `Uploaded <key>`,
      and it appears in the bucket (check the Wasabi console or re-list).
- [ ] **Edit** a hydrated file, save → log shows `Uploaded <key>` once (not repeatedly).
- [ ] **Rename** a file → log shows `Renamed a -> b`; the object is renamed in the bucket.
- [ ] **Delete** a file → log shows `Deleted <key>`; the object is gone from the bucket.
- [ ] **Delete a sub-folder** with files → each contained object is deleted (cascade).
- [ ] Open the large file (hydrate) and confirm it is **not** re-uploaded (no `Uploaded` line for it).

## G. Remote sync (cloud → local)

The remote pull runs every ~5 minutes; to trigger sooner, unmount and re-mount.
- [ ] Add a file to the bucket externally → after a pull it appears as a placeholder locally.
- [ ] Delete a (cloud-only, not-yet-opened) file from the bucket externally → after a pull its local
      placeholder is removed. Log: `Removed (deleted remotely): <key>`.
- [ ] Edit a file both locally (leave it hydrated) and in the bucket → log shows a `Conflict …
      keeping local` line; your local copy is preserved.

## H. Auto-dehydrate

- [ ] Leave a hydrated, unpinned file idle past the mapping's **cache max age** (lower it to a few
      minutes for testing). Within the hourly sweep it returns to cloud-only. Log:
      `Auto-dehydrated N idle file(s)`.

## I. Updates

- [ ] **About → Check for updates** with an older build installed offers the newer release and can
      download + launch the installer.

---

## Known limitations (by design, this milestone)

- Conflicts are **last-writer-wins with logging**, not interactive conflict copies.
- Directory **renames** on the remote side aren't reconciled as moves (seen as delete+create).
- The cfapi sync-root registration is left in place after unmount (re-used on next mount).
