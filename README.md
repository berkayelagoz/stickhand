<img src="./.github/logo.png" width="80" />

# StickHand

[![Latest release](https://img.shields.io/github/v/release/berkayelagoz/stickhand?display_name=tag&sort=semver&cacheSeconds=3600)](https://github.com/berkayelagoz/stickhand/releases/latest)
[![Platform: Windows](https://img.shields.io/badge/platform-Windows%2011%20x64-0078D4)](https://github.com/berkayelagoz/stickhand/releases/latest)
[![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg)](./LICENSE)

A tiny hand on a stick that points at things on your Windows screen.

[![Download for Windows](https://img.shields.io/badge/Download_for_Windows-0078D4?style=for-the-badge&logo=windows&logoColor=white)](https://github.com/berkayelagoz/stickhand/releases/latest/download/StickHand-WindowsSetup.exe)

Windows 11 x64.

## Install

Download `StickHand-WindowsSetup.exe` and run it. It installs per-user (no administrator
rights) and updates itself from the same release feed. `StickHand-Windows-portable.zip` is
the unpacked build for people who would rather not install anything; it does not update
itself.

The installer is not code-signed, so SmartScreen shows **Windows protected your PC** the
first time: choose **More info → Run anyway**. Some antivirus products flag the global
mouse and keyboard hooks StickHand needs — those hooks are what turn your clicks into taps.

## Use

| | |
|---|---|
| Click the tray icon, or **Ctrl+Alt+H** | Turn the pointer on or off |
| Right-click the tray icon | Menu, Settings, Check for updates |
| Click | Tap (nothing underneath gets clicked) |
| Hold and drag | Draw on the screen |
| Hold **Ctrl** while drawing | Add an arrowhead to the end of the line |
| **Ctrl+Alt+X** | Clear the drawing |
| **Ctrl+Alt+S**, **Ctrl+Alt+F** | Cycle the smoothing level, toggle the ink fade |
| **Esc** | Clear everything and turn the pointer off |
| **Ctrl+Alt+Q** | Quit |

The toolbar sits at the top of the primary monitor: 36 hands, the laser dot, the tap-sound
toggle and exit. Hold **Alt** to use the app underneath without leaving drawing mode, or
press **Shift** twice to latch click-through; both are configurable in Settings.

## Automation

Launchers and shell scripts can control StickHand with `stickhand://` links:

```powershell
Start-Process "stickhand://toggle"
```

| Link | Action |
|---|---|
| `stickhand://on`, `stickhand://off`, `stickhand://toggle` | Turn the pointer on, off, or flip it |
| `stickhand://clear` | Clear the drawing |
| `stickhand://style/<id>` | Switch to a hand (or `laser`) and turn the pointer on |
| `stickhand://sound/on`, `/off`, `/toggle` | Tap sound for the current pointer |

While StickHand runs, it keeps its current state in `%APPDATA%\StickHand\state.json`:
whether the pointer is on, the selected style, whether sound is on, and every style with
its `id`, name and type (`hand`, `custom` or `laser`). The file is removed when StickHand
quits. It also records the app's `pid`, so a file left behind by a crash can be detected.

If StickHand isn't running, opening any link launches it and then runs the command.

## Known limits

StickHand cannot draw over elevated (administrator) windows. The real cursor is hidden with
a global cursor override while the pointer is on; a normal exit always restores it, but if
the app is killed hard, sign out and back in (or restart Explorer) to get the cursor back.

## Credits

StickHand is a Windows port of [TinyHand](https://github.com/nilbuild/tinyhand) by Kamran
Ahmed. The hand sprites, the tap sounds and the behaviour contracts come from that project;
the Windows application is a separate implementation. See [LICENSE](LICENSE).

## License

MIT. See [LICENSE](LICENSE).
