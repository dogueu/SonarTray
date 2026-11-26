# SonarTray

Lightweight Windows system tray controller for the **SteelSeries Sonar** audio engine — no need to keep the heavyweight SteelSeries GG desktop client running.

> **Disclaimer:** SonarTray is an unofficial, independent open-source utility and is not affiliated with, endorsed by, or sponsored by SteelSeries ApS. SteelSeries and SteelSeries Sonar are trademarks of SteelSeries ApS. No SteelSeries binaries, drivers, or proprietary assets are included in this repository.

---

## Features

- **Standalone Sonar Engine Mode:** Automatically launches and hosts your locally installed `SteelSeriesSonar.exe` via a minimal loopback IPC stub when SteelSeries GG is closed, and gracefully shuts it down (`EVENT_SHUTDOWN`) on exit or Windows logoff so virtual audio devices are cleanly deactivated.
- **Full Audio Mixer & Device Routing:** Control Master, Game, Chat, Media, Aux, and Mic volumes, mute states, per-channel output/input device routing, and one-click render device sync.
- **ChatMix Control:** Real-time Game/Chat balance slider with clear status indicators when devices are unassigned, mismatched, or hardware-controlled.
- **10-Band Parametric EQ & Presets:** Browse and select Sonar EQ presets, toggle EQ, flatten curves, reset defaults, and edit per-band gain, frequency, Q-factor, and filter type.
- **Audio Effects / DSP:**
  - Playback channels: Spatial Audio (Virtual Surround & Form Factor), Reverb, Bass/Treble/Voice Boost, General Gain, and Smart Volume.
  - Voice/Mic channel: ClearCast AI Noise Cancellation, Noise Reduction, Impact Noise Reduction, Volume Stabilizer, and Noise Gate (Auto/Manual).
- **Ultra-Lightweight & 100% Offline:**
  - Single-file `.NET 8` WPF executable (~380 KB, zero external NuGet dependencies).
  - Stops live polling and WebSocket sync while hidden in the tray (~0% idle CPU).
  - Zero internet requests, telemetry, or external font/icon downloads — all communication is strictly `127.0.0.1` loopback.
- **Bilingual UI (TR / EN):** Built-in Turkish and English interface with persistent settings stored in `%LocalAppData%\SonarTray\settings.json`.
- **Optional Windows Startup:** Clean current-user (`HKCU`) autostart toggle without requiring administrator privileges.

---

## Requirements

- **OS:** Windows 10 / Windows 11 (64-bit)
- **Runtime:** [.NET 8 Desktop Runtime (x64)](https://dotnet.microsoft.com/en-us/download/dotnet/8.0)
- **Audio Engine:** [SteelSeries GG](https://steelseries.com/gg) must be installed (for the Sonar virtual audio drivers and `SteelSeriesSonar.exe`), though GG itself does not need to be running.

> **Note on Windows SmartScreen:** Because the published `SonarTray.exe` is an unsigned open-source single-file executable, Windows SmartScreen may show a warning on first launch. You can review the entire source code here and build it yourself with the command below.

---

## Building from Source

Requires the [.NET 8 SDK](https://dotnet.microsoft.com/en-us/download/dotnet/8.0):

```powershell
dotnet publish -c Release
```

The standalone single-file executable is generated at:

```text
bin\Release\net8.0-windows\win-x64\publish\SonarTray.exe
```

---

## Türkçe Özet

**SonarTray**, ağır SteelSeries GG istemcisini arka planda açık tutmadan bilgisayarınızda kurulu olan **SteelSeries Sonar** ses motorunu doğrudan sistem tepsisinden yönetmenizi sağlayan hafif bir Windows uygulamasıdır.

- **Bağımsız Çalışma ve Temiz Kapanış:** GG kapalıyken `SteelSeriesSonar.exe` motorunu yerel olarak başlatır; çıkışta veya oturum kapanışında temiz kapatma sinyali göndererek sanal ses kanallarını düzgünce devre dışı bırakır.
- **Tam Kontrol:** Mikser, cihaz yönlendirme, ChatMix, 10 bant parametrik EQ, hazır profiller, ses efektleri (DSP) ve Türkçe/İngilizce arayüz desteği.
- **Hafif ve Çevrimdışı:** Harici NuGet paketi içermez, internete bağlanmaz, pencere gizliyken arka planda `%0` CPU tüketir.
- **Gereksinimler:** Windows 10/11 (x64), .NET 8 Desktop Runtime (x64) ve kurulu SteelSeries GG (çalışıyor olması gerekmez).

---

## License

Licensed under the [MIT License](LICENSE).
