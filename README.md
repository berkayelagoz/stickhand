<img src="./.github/logo.png" width="80" />

# TinyHand

[![Latest release](https://img.shields.io/github/v/release/nilbuild/tinyhand?display_name=tag&sort=semver&cacheSeconds=3600)](https://github.com/nilbuild/tinyhand/releases/latest)
[![Platform: macOS | Windows](https://img.shields.io/badge/platform-macOS%20%7C%20Windows-lightgrey)](https://tinyhand.app)
[![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg)](./LICENSE)

A tiny hand on a stick that points at things on your screen.

<img src="./.github/banner.jpg" alt="TinyHand: a tiny hand for your Mac" />

[![Download for macOS](https://img.shields.io/badge/Download_for_macOS-000000?style=for-the-badge&logo=apple&logoColor=white)](https://github.com/nilbuild/tinyhand/releases/latest/download/TinyHand.dmg)
[![Download for Windows](https://img.shields.io/badge/Download_for_Windows-0078D4?style=for-the-badge&logo=windows&logoColor=white)](https://github.com/nilbuild/tinyhand/releases/latest/download/TinyHand-WindowsSetup.exe)
[![Visit website](https://img.shields.io/badge/Visit_website-ffffff?style=for-the-badge&logo=safari&logoColor=black)](https://tinyhand.app)

macOS 13 or later, Apple Silicon and Intel. Windows 11 x64.

## Install

Open `TinyHand.dmg`, drag TinyHand to Applications and allow **Accessibility** when asked. It needs that to turn your clicks into taps. TinyHand updates itself.

## Use

| | |
|---|---|
| Click the menu bar icon, or **⌃⌥H** | Turn the pointer on or off |
| Right-click the menu bar icon | Menu and Settings |
| Click | Tap (nothing underneath gets clicked) |
| Hold and drag | Draw on the screen |
| Hold **⌘** while drawing | Add an arrowhead to the end of the line |
| **⌃⌥X** | Clear the drawing |
| **Esc** | Clear everything and turn the pointer off |

While the pointer is on, a small toolbar at the bottom of the screen lets you pick one of
36 hands or a laser dot, mute the tap sound, or exit.

## Automation

Launchers like Raycast or Alfred, and shell scripts, can control TinyHand with
`tinyhand://` links. Use `open -g` so TinyHand doesn't take focus:

```sh
open -g tinyhand://toggle
```

| Link | Action |
|---|---|
| `tinyhand://on`, `tinyhand://off`, `tinyhand://toggle` | Turn the pointer on, off, or flip it |
| `tinyhand://clear` | Clear the drawing |
| `tinyhand://style/<id>` | Switch to a hand (or `laser`) and turn the pointer on |
| `tinyhand://sound/on`, `/off`, `/toggle` | Tap sound for the current pointer |

While TinyHand runs, it keeps its current state in
`~/Library/Application Support/TinyHand/state.json`: whether the pointer is on, the
selected style, whether sound is on, and every style with its `id`, name and type
(`hand`, `custom` or `laser`). The file is removed when TinyHand quits. It also
records the app's `pid`, so a file left behind by a crash can be detected.

If TinyHand isn't running, opening any link launches it and then runs the command.

## Windows

Download `TinyHand-WindowsSetup.exe` and run it. It installs per-user (no administrator
rights) and updates itself from the same release feed. `TinyHand-Windows-portable.zip` is
the unpacked build for people who would rather not install anything; it does not update
itself.

The installer is not code-signed, so SmartScreen shows **Windows protected your PC** the
first time: choose **More info → Run anyway**. Some antivirus products flag the global
mouse and keyboard hooks TinyHand needs — those hooks are what turn your clicks into taps.

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

Automation uses the same links and the same state contract as macOS, with
`%APPDATA%\TinyHand\state.json`:

```powershell
Start-Process "tinyhand://toggle"
```

Known limits: TinyHand cannot draw over elevated (administrator) windows. The real cursor
is hidden with a global cursor override while the pointer is on; a normal exit always
restores it, but if the app is killed hard, sign out and back in (or restart Explorer) to
get the cursor back.

## License

MIT. See [LICENSE](LICENSE).
