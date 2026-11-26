# SonarTray

<div align="center">

**SteelSeries Sonar ses motoru için hafif, bağımsız Windows sistem tepsisi kontrol uygulaması.**

[![Platform](https://img.shields.io/badge/Platform-Windows%2010%20%7C%2011-0078D6?style=flat&logo=windows&logoColor=white)](https://microsoft.com/windows)
[![.NET](https://img.shields.io/badge/.NET-8.0-512BD4?style=flat&logo=dotnet&logoColor=white)](https://dotnet.microsoft.com/)
[![Lisans: MIT](https://img.shields.io/badge/Lisans-MIT-F5A623?style=flat&logo=open-source-initiative&logoColor=white)](LICENSE)
[![Boşta CPU](https://img.shields.io/badge/Bo%C5%9Fta%20CPU-%250-2ea44f?style=flat)](https://github.com/dogueu/SonarTray)
[![Taşınabilir](https://img.shields.io/badge/Tek%20Dosya-~380%20KB-FF5500?style=flat)](https://github.com/dogueu/SonarTray/releases)

[ **English Documentation** ](README.md)

</div>

---

## Özellikler

- **Bağımsız Sonar Motoru Barındırma:** SteelSeries GG kapalıyken bilgisayarınızda kurulu `SteelSeriesSonar.exe` motorunu minimal bir yerel IPC stub'ı ile otomatik başlatır — ağır GG istemcisini, oyun içi overlay'i veya Moments yakalama servislerini açık tutmanıza gerek kalmaz.
- **Temiz Kapanış ve Sanal Aygıt Yönetimi:** Çıkışta veya Windows oturumu kapanırken yerel `/eventing` WebSocket üzerinden `EVENT_SHUTDOWN` sinyali gönderir; Sonar motoru tüm sanal ses aygıtlarını temizce devre dışı bırakarak kapanır.
- **Tam Ses Mikseri ve Cihaz Yönlendirme:** Master, Game, Chat, Media, Aux ve Mic ses düzeylerini, sessize alma durumlarını, kanal bazlı çıkış/giriş cihazlarını ve tek tıkla cihaz eşitlemeyi yönetin.
- **ChatMix Kontrolü:** Gerçek zamanlı Game/Chat denge sürgüsü ve cihaz seçilmemiş, farklı cihaz seçili veya donanım kontrolünde olduğu durumlar için açıklayıcı durum bildirimleri.
- **10 Bant Parametrik EQ ve Hazır Profiller:** Sonar hazır profillerini seçin, EQ'yu açıp kapatın, eğriyi düzleştirin, varsayılana sıfırlayın ve her bant için kazanç (dB), frekans (Hz), Q faktörü ve filtre tipini düzenleyin.
- **Kapsamlı Ses Efektleri (DSP):**
  - *Çıkış kanalları:* Uzamsal Ses (Virtual Surround & Form Factor), Yankı (Reverb), Bas/Tiz/Ses Netliği Artırma, Genel Kazanç ve Akıllı Ses (Smart Volume).
  - *Mikrofon / Ses kanalı:* ClearCast AI Gürültü Engelleme, Gürültü Azaltma, Darbe Sesi Azaltma, Ses Sabitleyici ve Gürültü Kapısı (Otomatik/Manuel).
- **Sıfır Arka Plan Yükü ve %100 Çevrimdışı:** Pencere gizliyken yoklama zamanlayıcısını ve WebSocket bağlantısını durdurur (**boşta %0 CPU**). Harici NuGet paketi, telemetri veya internet bağlantısı içermez — tüm iletişim yalnızca `127.0.0.1` üzerindedir.
- **Çift Dil (TR / EN) ve Otomatik Başlatma:** Dahili Türkçe/İngilizce arayüz (`%LocalAppData%\SonarTray\settings.json`) ve yönetici izni gerektirmeyen geçerli kullanıcı (`HKCU`) Windows başlangıç seçeneği.

---

## Kurulum ve Çalıştırma

### Gereksinimler
- Windows 10 veya Windows 11 (64-bit)
- [.NET 8 Desktop Runtime (x64)](https://dotnet.microsoft.com/en-us/download/dotnet/8.0)
- Kurulu [SteelSeries GG](https://steelseries.com/gg) (Sonar sanal ses sürücüleri ve `SteelSeriesSonar.exe` için kurulu olmalı; GG'nin **çalışıyor olmasına gerek yoktur**)

### 1. Hazır Taşınabilir Sürüm (Portable Single-File - Önerilen)
Kurulum gerektirmez:
1. [**Releases (Sürümler)**](https://github.com/dogueu/SonarTray/releases) sayfasından `SonarTray.exe` veya `SonarTray.zip` dosyasını indirin.
2. `SonarTray.exe` dosyasını kalıcı bir klasöre koyun (örn. `C:\Programlar\SonarTray`).
3. Çift tıklayarak çalıştırın; SonarTray sağ alt sistem tepsisine yerleşip otomatik bağlanacaktır.

> *Not: `SonarTray.exe` imzasız bir açık kaynak çalıştırılabilir dosya olduğundan ilk açılışta Windows SmartScreen uyarısı gösterebilir. Yayınlanan sürümler bu depodaki kaynak koddan doğrudan GitHub Actions ile otomatik derlenir; dilerseniz aşağıdan kendiniz de derleyebilirsiniz.*

### 2. Kaynak Koddan Derleme (Geliştiriciler İçin)
Gereksinim: [.NET 8.0 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)

```bash
# Depoyu klonlayın
git clone https://github.com/dogueu/SonarTray.git
cd SonarTray

# Tek dosya çalıştırılabilir sürüm üretin (.pdb olmadan)
dotnet publish -c Release
```

Çıktı yolu:
```text
bin\Release\net8.0-windows\win-x64\publish\SonarTray.exe
```

---

## Mimari ve Kullanılan Teknolojiler

- **Framework:** [.NET 8 Windows Presentation Foundation (WPF)](https://learn.microsoft.com/en-us/dotnet/desktop/wpf/)
- **Bağımlılıklar:** **Sıfır harici NuGet paketi** — yalnızca dahili .NET API'leri (`HttpClient`, `ClientWebSocket`, `HttpListener`, `System.Text.Json.Nodes`, `Microsoft.Win32.Registry`)
- **Sistem Tepsisi:** `System.Windows.Forms.NotifyIcon` ve GDI+ ile yerel vektörel çizilen tepsi simgesi

---

## Yasal Uyarı

SonarTray, bağımsız ve resmi olmayan bir açık kaynak yardımcı programıdır; SteelSeries ApS ile bağlantılı değildir, desteklenmez veya sponsor olunmaz. *SteelSeries* ve *SteelSeries Sonar*, SteelSeries ApS'nin ticari markalarıdır. Bu depoda SteelSeries'e ait hiçbir çalıştırılabilir dosya, sürücü veya tescilli içerik bulunmamaktadır.

---

## Lisans

Bu proje [MIT Lisansı](LICENSE) kapsamında açık kaynak olarak yayınlanmıştır.
