# ARC Lobby Mute

[![Buy me a coffee](https://img.shields.io/badge/Buy_me_a_coffee-Support-FFDD00?logo=buymeacoffee&logoColor=black)](https://buymeacoffee.com/loukious)

A small standalone Windows tray app that mutes **only ARC Raiders** in the lobby/menu and unmutes it when you connect to a match. Uses Windows' per-application audio controls and the game's local connection file. No game injection, administrator rights, extra packages, network access, or changes to game files.

## Run

For **Windows 10/11 x64**. Extract the downloaded ZIP to a folder of your choice, then open `ArcLobbyMute.exe`. If building from source, it is in `dist\ArcLobbyMute.exe`. This GitHub repository contains the source code; app downloads are intended for Nexus Mods.

Automation is on by default. Close its window to leave it running in the notification area; double-click the teal speaker icon to reopen it. Right-click the icon to pause or quit. Pausing or quitting restores each game audio session's mute state from before automation took control; volume sliders are never changed.

The **Start with Windows** checkbox is optional and off by default. It launches the app directly in the tray. Keep the executable in a stable location when using this option. Opening the executable again reveals the existing app instead of starting another copy.

The window and tray menu follow Windows' **app** light/dark theme and respond to theme changes while running. High contrast uses Windows system colors. The layout scales with display DPI and sizes its rows to the text so headings, descriptions and controls do not overlap.

## Detection

- `PioneerGame.exe` absent: wait for the game.
- Game running and `%LOCALAPPDATA%\Embark\Pioneer\Connectinfo.json` present, with a timestamp from this game launch: unmute.
- Game running and the file absent (or from an older launch) for at least two seconds: mute.
- A file access error: restore previous mute state and retry.

Checks run once per second. Match audio returns within about one second of the file appearing; lobby muting takes about two to three seconds. The app covers the game's audio sessions across all active playback devices and picks up new sessions or device changes on the next check.

The file was observed disappearing on return to the lobby and reappearing with a new server on the next match. It signals a server connection, so deployment/loading may already count as a match. Startup, menus and matchmaking without a connection file count as lobby. Game updates may change this behavior. A forced termination of the tray app cannot run its audio restoration; use **Quit** for a clean stop. Windows Volume Mixer can always manually unmute the game.

Only the local player's state is detected. No server addresses, routing tokens or other contents of the connection file are read or logged.

## Build and verify

Uses the .NET Framework compiler included with Windows; the executable needs .NET Framework 4.5 or later (included with current Windows 10/11). Build using PowerShell 5.1 or later. No NuGet packages or .NET SDK installation required.

```powershell
.\build.ps1 -Test
.\build\ArcLobbyMute.Tests.exe --inspect
```

Tests exercise lobby/match transitions, transient file removal, stale files, restart and error recovery, plus real Windows audio mute/unmute, volume preservation, original-mute restoration and process isolation using a silent test session. `--inspect` only reads the running game's detected state and audio sessions.

To run only state-detection checks on a machine without a playback device:

```powershell
.\build.ps1 -Test -StateOnly
```

To build while your existing `dist` executable is running, choose a separate output folder:

```powershell
.\build.ps1 -Test -OutputDirectory build\verification
```

Optional commands:

```powershell
.\dist\ArcLobbyMute.exe --background
.\dist\ArcLobbyMute.exe --exit
```

Preferences: `HKCU\Software\ArcLobbyMute`. Optional startup entry: `HKCU\Software\Microsoft\Windows\CurrentVersion\Run\ArcLobbyMute`. A small local transition log, capped at approximately 128 KB, is written to `%LOCALAPPDATA%\ArcLobbyMute\activity.log`.

To remove: uncheck **Start with Windows**, quit from the tray, then delete the app folder. The preferences and local activity log can also be removed if desired.

## Packaging for Nexus Mods

`VERSION` is the source of the executable's version. Update it before building a new version. To build, run the local checks and create the upload ZIP:

```powershell
.\package.ps1
```

The archive in `release\` contains `ArcLobbyMute.exe` and this README. A SHA-256 checksum is saved alongside it. Packaging uses a separate build folder so the installed app can keep running. No GitHub Actions workflows or automatic releases are configured.

## Support development

If ARC Lobby Mute is useful to you, [buy me a coffee](https://buymeacoffee.com/loukious). Thank you for supporting its development.
