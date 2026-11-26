# SonarTray

<div align="center">

**Lightweight, standalone Windows system tray controller for the SteelSeries Sonar audio engine.**

[![Platform](https://img.shields.io/badge/Platform-Windows%2010%20%7C%2011-0078D6?style=flat&logo=windows&logoColor=white)](https://microsoft.com/windows)
[![.NET](https://img.shields.io/badge/.NET-8.0-512BD4?style=flat&logo=dotnet&logoColor=white)](https://dotnet.microsoft.com/)
[![License: MIT](https://img.shields.io/badge/License-MIT-F5A623?style=flat&logo=open-source-initiative&logoColor=white)](LICENSE)
[![Idle CPU](https://img.shields.io/badge/Idle%20CPU-0%25-2ea44f?style=flat)](https://github.com/dogueu/SonarTray)
[![Single File](https://img.shields.io/badge/Portable-~380%20KB-FF5500?style=flat)](https://github.com/dogueu/SonarTray/releases)

[ **Türkçe Dokümantasyon** ](README.tr.md)

</div>

---

## Features

- **Standalone Sonar Engine Hosting:** Automatically launches your locally installed `SteelSeriesSonar.exe` via a minimal loopback IPC stub when SteelSeries GG is closed — no need to keep the heavy GG desktop client, overlay, or Moments capture running.
- **Clean Shutdown & Virtual Device Cleanup:** Sends a graceful `EVENT_SHUTDOWN` signal over the local `/eventing` WebSocket on app exit or Windows logoff so Sonar cleanly deactivates its virtual audio devices instead of leaving orphaned channels.
- **Full Audio Mixer & Device Routing:** Control Master, Game, Chat, Media, Aux, and Mic volumes, mute states, per-channel output/input routing, and one-click render device sync.
- **ChatMix Control:** Real-time Game/Chat balance slider with clear status banners for unassigned, mismatched, or hardware-controlled devices.
- **10-Band Parametric EQ & Presets:** Select built-in or custom Sonar presets, toggle EQ, flatten curves, reset defaults, and fine-tune per-band gain, frequency, Q-factor, and filter type.
- **Complete Audio Effects (DSP):**
  - *Playback channels:* Spatial Audio (Virtual Surround & Form Factor), Reverb, Bass/Treble/Voice Boost, General Gain, and Smart Volume.
  - *Voice / Mic channel:* ClearCast AI Noise Cancellation, Noise Reduction, Impact Noise Reduction, Volume Stabilizer, and Noise Gate (Auto/Manual).
- **Zero Background Overhead & 100% Offline:** Stops live polling and WebSocket sync while hidden in the tray (**0% idle CPU**). Zero external NuGet packages, zero telemetry, and zero internet requests — all traffic stays strictly on `127.0.0.1`.
- **Bilingual UI & Optional Autostart:** Built-in English and Turkish interface (`%LocalAppData%\SonarTray\settings.json`) and a clean current-user (`HKCU`) Windows startup toggle requiring no administrator privileges.

---

## Getting Started

### Prerequisites
- Windows 10 or Windows 11 (64-bit)
- [.NET 8 Desktop Runtime (x64)](https://dotnet.microsoft.com/en-us/download/dotnet/8.0)
- [SteelSeries GG](https://steelseries.com/gg) installed (for Sonar virtual audio drivers and `SteelSeriesSonar.exe`; GG does **not** need to be running)

### 1. Portable Single-File (Recommended)
No installer required:
1. Download `SonarTray.exe` or `SonarTray.zip` from the [**Releases**](https://github.com/dogueu/SonarTray/releases) page.
2. Place `SonarTray.exe` in a permanent folder (e.g. `C:\Tools\SonarTray`).
3. Double-click to run — SonarTray nests in the system tray and connects automatically.

> *Note: Because `SonarTray.exe` is an unsigned open-source executable, Windows SmartScreen may show a prompt on first launch. Release binaries are built automatically from this repository via GitHub Actions, or you can build from source below.*

### 2. Building from Source
Requires the [.NET 8.0 SDK](https://dotnet.microsoft.com/download/dotnet/8.0):

```bash
# Clone the repository
git clone https://github.com/dogueu/SonarTray.git
cd SonarTray

# Publish as a single-file executable (no .pdb)
dotnet publish -c Release
```

Output path:
```text
bin\Release\net8.0-windows\win-x64\publish\SonarTray.exe
```

---

## Built With

- **Framework:** [.NET 8 Windows Presentation Foundation (WPF)](https://learn.microsoft.com/en-us/dotnet/desktop/wpf/)
- **Dependencies:** **Zero external NuGet packages** — built purely with built-in .NET APIs (`HttpClient`, `ClientWebSocket`, `HttpListener`, `System.Text.Json.Nodes`, `Microsoft.Win32.Registry`)
- **System Tray:** `System.Windows.Forms.NotifyIcon` & custom GDI+ vector-drawn tray icon

---

## Disclaimer

SonarTray is an unofficial, independent open-source utility and is **not** affiliated with, endorsed by, or sponsored by SteelSeries ApS. *SteelSeries* and *SteelSeries Sonar* are trademarks of SteelSeries ApS. No SteelSeries binaries, drivers, or proprietary assets are included in this repository.

---

## License

This project is licensed under the [MIT License](LICENSE).
