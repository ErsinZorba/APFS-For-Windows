# APFS for Windows

**English** | [Türkçe](README.tr.md)

A lightweight, open-source tool that lets you open Mac-formatted **APFS** disks on Windows 11 like any other drive.
Plug the disk in and within a few seconds it shows up in File Explorer with a drive letter; open and copy your files.

> **Read-only first.** Disks are always mounted read-only; the program never writes to a disk on its own. If you want to write to
> a disk, you allow it separately for that disk with the **Allow writing** button on its card (experimental; see "Writing").
> The safety of your data comes first.

## What is supported
| | |
|---|---|
| Automatic mounting | A background service finds APFS disks and mounts them |
| Simple interface | A main window and a small panel in the notification area: disks, "Open", "Unlock", "Safely eject" |
| Multiple volumes | Each user volume gets its own drive letter; macOS internal volumes (Preboot, Recovery…) are hidden |
| Encrypted volumes (experimental) | The tray icon asks for the password (with its hint); the password is never saved |
| Compressed files | zlib, LZVN, LZFSE (macOS transparent compression) |
| Unicode names | Accented and non-Latin names; the Mac's NFD form; characters forbidden on Windows (`: ? *` …) |
| Large files | Over 4 GB, fragmented and sparse files |
| Damaged disks | The program does not crash; it tells you what happened in plain words; intact volumes still open |
| 10 interface languages | English, Türkçe, Deutsch, Français, Español, Português (Brasil), Italiano, 日本語, 简体中文, 한국어 |

## Installation
Run `APFS-for-Windows-Setup-1.3.0.exe` and follow the wizard (Next › Install › Finish). Windows 10/11 (64-bit) is all you need;
nothing else has to be installed: .NET comes with the program, and if the [WinFsp](https://winfsp.dev) driver is missing the wizard
installs it too. After installation, **APFS for Windows** is in the Start menu and the tray icon starts automatically at every sign-in.

To uninstall: **Settings › Apps › Installed apps › APFS for Windows › Uninstall**. Uninstalling writes nothing to your disks;
the log (`C:\ProgramData\APFS for Windows\log.txt`) is kept for you to inspect, and WinFsp is not removed.

## Usage
- **Main window** (Start menu, or "Open main window" in the tray menu): attached Mac disks appear as cards. Next to each volume:
  **Open** (opens it in File Explorer), **Unlock** if it is encrypted, **Details** if it could not be opened; **Safely eject** at the
  bottom of the card. Closing the window does not close the program; the disks stay mounted and the program keeps running in the
  notification area.
- **Tray panel**: left-click the APFS icon next to the clock; the same actions in a small panel. The right-click menu has
  "Open main window", "safely eject" for each disk, "Scan again", "Open log" and "Exit".

## Language
The program starts in the Windows display language (English, Turkish, German, French, Spanish, Brazilian Portuguese, Italian,
Japanese, Simplified Chinese, Korean; English for any other language). To change it, click the language button at the bottom of
the main window (it shows the name of the current language) and pick one from the list; the program restarts in that language.
"Windows language" follows the Windows language again. Log details and the command line are in English and Turkish only (English
for the other languages).

The translations other than English and Turkish have not been reviewed by native speakers. If you see a wrong or unclear
translation, please report it in [Issues](https://github.com/ErsinZorba/APFS-for-Windows/issues) (language, the text on screen and
the correct wording).

## Writing (experimental)
Click **Allow writing** on the disk's card and confirm the warning. The program first checks the whole disk; if everything is in
order, the drive is mounted again as **writable** (orange dot on the card, "writable (experimental)"). You can now copy files, create
folders, rename, delete, and edit and save files in File Explorer. **Back to read-only** saves the changes and mounts the disk
read-only again.

- **Make a backup first.** Writing is new; keep a copy of your important files.
- **Always use "Safely eject" before unplugging the disk or connecting it to a Mac.** Changes are saved every few seconds;
  Safely eject saves the latest changes too and marks the disk as "cleanly unmounted".
- If writing is interrupted (cable pulled, power cut), the disk opens in its previously saved state; your Mac disk is not damaged.
  The changes of the last few seconds may be lost. (Some USB enclosures ignore the "flush to disk" command; then the lost period
  can be a little longer, and the disk still opens in its previous intact state.)
- Which disks can be opened for writing: single-volume APFS partitions, **unencrypted** or **password-encrypted** on the Mac
  ("APFS (Encrypted)" in Disk Utility; 1.2.0). On an encrypted volume first **Unlock**, then **Allow writing**: files are written to
  the disk encrypted. The password is not asked again when allowing writing or on "Back to read-only" (the key is kept only in
  memory; it is erased when the disk is removed or the service stops, and is never saved anywhere).
- On a disk with more than one APFS partition (e.g. a USB stick partitioned on a Mac) each partition has its own
  **«name»: Allow writing** button; allowing writing on one partition does not touch the others.
- These stay read-only, and the card says why: volumes whose macOS encryption has not finished or was changed later, volumes encrypted
  by hardware (T2/Apple Silicon) or with per-file keys, disks with snapshots (e.g. Time Machine), macOS system/backup volumes, Fusion
  Drive, several volumes in the same partition, and disks whose internal structure shows an inconsistency.
- Files whose content cannot be changed in this version: files created with "Duplicate" on the Mac (clones) and files macOS stores
  compressed (mostly application and system files; not documents and photos copied with Finder). They can be opened, copied,
  deleted and moved; when you try to save over them, Windows says "access denied" (edit a copy).
- Files deleted in File Explorer do not go to the Recycle Bin; Windows asks "permanently delete?" (as on a USB stick).
- Files created from Windows have an "unknown" owner on the Mac (every user can open them); files are 0644, folders 0755.
  Windows access lists (ACLs) and alternate data streams are not stored.

## Ejecting a disk
Click **Safely eject** on the disk's card in the main window or the tray panel. The volumes are unmounted, Windows ejects the disk and
the notification "You can unplug the cable now" appears. If a file on the disk is open or being copied at that moment, the program asks
first: "A copy seems to be in progress" → **Cancel** (the copy goes on) or **Eject anyway** (the copy is cut short).
File Explorer does not show "Eject" in the drive's right-click menu (WinFsp drives appear to Windows as virtual drives). Unplugging a
read-only disk directly does not harm it; only a copy in progress is cut short. Unplugging a writable disk directly does not corrupt
it either (it opens in its previously saved state), but the latest changes may be lost: use Safely eject first.

If the disk is reformatted by another program (Rufus, Disk Management…) while it is attached, the APFS drive is removed within
15 seconds at most. If the disk was open for writing, the latest unsaved changes are not written, so as not to damage the new content.

## How it works
- A user-mode file system running on **WinFsp**. No kernel driver was written; even if something goes wrong, the system does not crash.
- The APFS reader was written from scratch in C#, based on Apple's *Apple File System Reference*.
- When mounted read-only, the disk is opened at the operating system level **with read permission only**; in addition the volume is
  reported to Windows as read-only and every modifying operation is refused. Code that can write to a disk can only run with the
  permission granted by the write gate (a full check).
- Writing follows APFS's own safety scheme: no saved structure is changed in place; changes are written to new blocks and at the
  very end a single "checkpoint" marker is advanced (with two separate "flush to disk" steps). If writing is interrupted, the disk
  opens with the previous checkpoint.
- Every structure on the disk is verified with its checksum. When a damaged structure is found, the program does not crash; it tells
  you what happened in plain words.
- Components: `apfs-service` (LocalSystem service: finds and mounts disks), `apfs-tray` (main window, tray icon and panel, password
  window, notifications), `apfs` (command-line tool).
- WinFsp - Windows File System Proxy, Copyright (C) Bill Zissimopoulos — <https://github.com/winfsp/winfsp>. The setup wizard
  contains WinFsp's unmodified official installer package (GPLv3 + FLOSS exception; see `THIRD_PARTY_NOTICES.md`).

## Command line
```
apfs info <source> [--fingerprint]     Disk and volume information
apfs ls <source> [path] [-r] [--hash]  Folder contents
apfs extract <source> <path> <target>  Copy a file to Windows
apfs check <source>                    Read-only self-check: walks the whole tree, reads the start and end of every file, compares counters
apfs check <source> --full [--gizle]   Full structural check: owner of every block, free-space records, reference counts (read-only); --gizle hides names
apfs check <source> --gate             The write gate's decision for this disk and its reasons (read-only)
apfs dump <source> [path]              Development: internal structure dump (with a path, that file's raw records)
apfs mount <source> [X:]               Manual mount (Ctrl+C unmounts); --all for internal volumes
```
`<source>`: a disk image file or `\\.\PhysicalDriveN` (requires administrator). For encrypted volumes `--password-prompt`.
After installation the tool is not added to PATH; run it with its full path: `& "C:\Program Files\APFS for Windows\apfs.exe" info \\.\PhysicalDrive2`

## File names
- All Unicode names are supported. The Mac storing names in a different Unicode form (NFD) is not a problem.
- On case-insensitive Mac volumes (the default) behaviour is the same as on Windows. Turkish "ı" and "i" are different letters, as on the Mac.
- Characters valid on the Mac but forbidden on Windows (`: * ? " < > | \`, trailing dot and space) are mapped to special characters
  with the standard mapping (SFM) used by Windows and macOS file sharing. They appear in File Explorer, open and can be copied.
- Known limitation: files whose names start with names reserved by Windows, such as `CON`, `NUL`, `AUX`, `COM1`, may not open in some programs.

## Compressed files
macOS stores many files transparently compressed. These files look and open normally on Windows too; their sizes are shown
uncompressed. A file compressed with an unsupported method (e.g. LZBITMAP) appears in the list, gives a "The request is not
supported" error when opened, and the reason is written to the log.

## Not supported
Writing to disks with several volumes in the same partition; changing the password of an encrypted volume, encrypting/decrypting;
repair, formatting; snapshots and the Time Machine structure; Fusion Drive; the sealed macOS system volume; DMG/sparsebundle files;
HFS+; hardware encryption of Apple Silicon/T2 internal disks.

## Development
```
dotnet build
dotnet test
powershell -File tools\build-installer.ps1   # setup wizard: publish\APFS-for-Windows-Setup-<version>.exe
```
`build-installer.ps1` needs Inno Setup 6 (`winget install --id JRSoftware.InnoSetup -e`) and WinFsp's official
`winfsp-2.1.25156.msi` package under `installer\redist\` (not added to git; the script checks its signature). For the program folder
only, `tools\build-release.ps1` (`publish\APFS for Windows\`); to install that without the wizard, `install.ps1` in an
administrator PowerShell. A version installed with `install.ps1` does not appear in "Installed apps" (remove it with
`uninstall.ps1`); if you installed with the wizard, uninstall from there — using `uninstall.ps1` leaves an orphaned entry in the list.
Test images are produced with `tests/Apfs.ImageBuilder` and compared with an independent reader, 7-Zip
(`tools/compare_with_7z.ps1`). Writing is tested against independent referees: Linux's APFS tools (apfsprogs: `mkapfs`, `apfsck`;
the linux-apfs-rw driver) run inside WSL (`tools/make-linux-fixtures.sh`, `tools/make-linux-populated.sh`); for macOS's own checker,
`tools/mac/` (Mac kit). To test the real disk path on a virtual disk, `tools/vhd-write-test.ps1` (administrator).
Interface texts: `src/Apfs.Core/Resources` and `src/Apfs.Windows/Resources` (`User.<language>.resx`; technical texts `Tech.resx`
in English and Turkish only). Long tests: `APFS_LONG=1 dotnet test`.

## License
Copyright (C) 2026 Ersin Zorba. GNU General Public License version 3 (version 3 only; `GPL-3.0-only`), text in [LICENSE](LICENSE).
For third-party components see [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md).

This is a single-developer project; outside contributions (pull requests) are not accepted. Issues can be used for bug reports.
