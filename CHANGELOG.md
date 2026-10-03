# Changelog

All notable changes to CastBridge. The format follows [Keep a Changelog](https://keepachangelog.com/),
and versions follow [Semantic Versioning](https://semver.org/).

## [1.0.0] — 2026-10-03

The first release. One file to download, one window to click through, and CastBridge sits in the
notification area controlling the speaker group on your network.

### What it does

Chromium can cast music to a Cast speaker group, but it has no UI for changing that group's volume.
CastBridge fills exactly that gap — and deliberately nothing else. It is a tray app: volume, mute,
playback transport, and what the speaker is currently playing, read from the speaker itself.

* **Volume on the group.** The slider reads the group's real level on connect and follows it while
  the app runs, so a change made anywhere else shows up here too. The group is preferred over its
  individual members, because the group is what moves both speakers.
* **Mute**, on the group or on a single speaker.
* **Now Playing, straight from the device** — title, artist, album, artwork, player state, elapsed
  time and a progress bar, from the media status the receiver publishes.
* **Play / pause / next / previous / stop** when the speaker is running a real media session.
* **A compact popup.** One click on the tray icon opens it, the way the system's own flyouts work.
  It stays until you click outside it; a double click goes straight to the full window.
* **It starts with Windows**, and the tray menu can turn that off for good.

### What it deliberately does not do

**It serves no media and binds no port.** CastBridge holds zero listening sockets. Casting stays in
Chromium, where you already do it. That keeps the app out of the way of whatever else is serving
media, keeps the firewall untouched, and removes the whole class of "the speaker cannot reach your
PC" failures.

### Please read this before wondering why play/pause is greyed out

Chromium casts in two quite different ways, and only one of them can be controlled from here:

| What you did in Chromium | What the speaker runs | Volume / mute | Play, pause, stop |
|---|---|---|---|
| **Cast tab / Cast screen** (audio mirroring) | "Chrome Audio Mirroring" | works | **not possible** — the speaker receives an audio stream, not a media session. The browser owns playback. |
| **A site's cast button** (YouTube, a web player) | that site's receiver, or the Default Media Receiver | works | works |

CastBridge detects the mirroring case and says so in the UI instead of offering buttons that cannot
do anything. Playback controls work whenever you cast from a site's own cast button.

### Installing

1. Download **`CastBridge-1.0.0-Setup.exe`** from this release and double click it.
2. It installs for you, into `%LOCALAPPDATA%\Programs\CastBridge`. **No administrator rights** and
   **nothing to install first** — the .NET runtime travels inside the same file.
3. It adds Start menu shortcuts (CastBridge and *Uninstall CastBridge*), a desktop shortcut, an entry
   under *Settings → Apps → Installed apps*, and starts CastBridge so you can see the tray icon.

To remove it: *Settings → Apps → CastBridge → Uninstall*, the Start menu shortcut, or
`CastBridge-Uninstall.exe`. That closes the app and deletes the files, shortcuts, logon entry and
app entry, and asks whether to delete CastBridge's settings and logs too.

### Before you run it

* **The file is not code-signed**, so Windows shows *Windows protected your PC*. Choose
  *More info → Run anyway*. It is unsigned because signing costs money per year, and the fix is
  yours to make when you want it.
* **Windows 11 files new tray icons away.** Click the `^` next to the clock, then drag the CastBridge
  icon onto the taskbar to pin it. The app says so once, on its first run.

### System requirements

64-bit Windows 10 (version 1903 or later) or Windows 11. Nothing else — no .NET install, no
prerequisites. This is an x64 build; it has not been tested on Windows on ARM.

### Silent installs

For setting up more than one machine:

```powershell
CastBridge-1.0.0-Setup.exe /S                                       # defaults, no window
CastBridge-1.0.0-Setup.exe /S /D="D:\Apps\CastBridge"                # somewhere else
CastBridge-1.0.0-Setup.exe /S /no-startup /no-desktop /no-launch
CastBridge-1.0.0-Setup.exe /uninstall /S                            # remove it again
```

Exit codes: 0 success, 1 failure, 2 an uninstall prompt was declined. Everything the installer does
is recorded in `%APPDATA%\CastBridge\installer.log`.

### Build it yourself

```bash
git clone https://github.com/anirban94chakraborty/CastBridge.git
cd CastBridge
powershell -ExecutionPolicy Bypass -File tools\build-installer.ps1
```

That publishes `dist\CastBridge.exe`, builds the uninstaller, and embeds both into
`dist\CastBridge-1.0.0-Setup.exe`. `tools\verify-install.ps1` installs and removes it in a scratch
folder and checks every file, shortcut and registry entry against what it should have produced.
Every push also runs it on GitHub Actions.

### Release artifact

| | |
|---|---|
| File | `CastBridge-1.0.0-Setup.exe` |
| Size | 93.6 MB (98,195,456 bytes) — the self-contained app travels inside it |
| SHA256 | `5EC65B74657612BE986B2BB72F9A25DD6EA20DCF21B7A90EB917ED0246DD7E9B` |
| Signature | none (unsigned) |

Verify what you downloaded with:

```powershell
Get-FileHash .\CastBridge-1.0.0-Setup.exe -Algorithm SHA256
```

The hash above is for the binary built and tested on 2026-10-03; a file rebuilt from CI will differ,
because the compiler is not bit-reproducible.

### Known limitations

* Playback transport is unavailable while the browser is mirroring tab or screen audio — see the
  table above. This is a limit of what the Cast protocol exposes, not a bug in CastBridge.
* No code signing, so the SmartScreen warning above applies to every recipient.
* The setup executable installs per user. There is no machine-wide MSI.
* Keyboard navigation of the popup and the window has not been checked; everything here was driven
  with the mouse.

[1.0.0]: https://github.com/anirban94chakraborty/CastBridge/releases/tag/v1.0.0
