using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;

namespace SonarTray;

public enum AppLanguage
{
    Tr,
    En
}

/// <summary>
/// Manages bilingual (Turkish / English) UI resources and persists the selected language.
/// </summary>
public static class LocalizationManager
{
    private static readonly string SettingsDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "SonarTray");

    private static readonly string SettingsPath = Path.Combine(SettingsDirectory, "settings.json");

    private static readonly Dictionary<string, string> TrStrings = new(StringComparer.Ordinal)
    {
        ["Str_SettingsTooltip"] = "Ayarlar",
        ["Str_PinTooltip"] = "Pencereyi Sabitle (Dışarı tıklayınca gizlenmez)",
        ["Str_RefreshTooltip"] = "Durumu Yenile",
        ["Str_StatusConnecting"] = "Sonar motoru başlatılıyor / bağlanılıyor...",
        ["Str_StatusConnectedGg"] = "Bağlı • GG Servisi",
        ["Str_StatusConnectedStandalone"] = "Bağlı • Bağımsız Sonar Motoru",
        ["Str_TabMixer"] = "Mikser & Cihazlar",
        ["Str_TabEq"] = "Ekolayzer (EQ)",
        ["Str_TabDsp"] = "Ses Efektleri",

        ["Str_ToggleChannelTooltip"] = "Bu kanalı aç / kapat",
        ["Str_MuteTooltip"] = "Sustur / Sesi Aç",
        ["Str_OpenChannelEqTooltip"] = "Bu kanalın EQ ve ses efektlerini aç",
        ["Str_ChannelDisabled"] = "Kanal Kapalı",
        ["Str_SelectDeviceTooltip"] = "Ses Cihazı Seçimi",
        ["Str_SelectPresetTooltip"] = "EQ / Ses Profili Seçimi",
        ["Str_ResetPresetTooltip"] = "Seçili profili varsayılan ayarlarına sıfırla",
        ["Str_SyncOutputsBtn"] = "Tüm Çıkışları Game Cihazına Eşitle",

        ["Str_ChatMixDoubleClickResetTooltip"] = "Sıfırlamak için çift tıklayın",
        ["Str_ChatMixFooterReset"] = "Çift tıkla: Sıfırla",
        ["Str_ChatMixFooterDisabled"] = "Devre dışı",
        ["Str_ChatMixBalanced"] = "Dengeli",
        ["Str_ChatMixHardwarePrefix"] = "Donanım",
        ["Str_ChatMixHardwareControl"] = "Donanım Kontrolü",
        ["Str_ChatMixDifferentDevice"] = "Farklı Cihaz",
        ["Str_ChatMixNoDevice"] = "Cihaz Seçilmedi",
        ["Str_ChatMixReasonHardware"] = "ChatMix'i kontrol etmek için donanım tekerleğini (Dock / Kulaklık) kullanın.",
        ["Str_ChatMixReasonDifferentDevice"] = "ChatMix için Game ve Chat kanalları aynı ses çıkış cihazına yönlendirilmelidir.",
        ["Str_ChatMixReasonNoDevice"] = "ChatMix'i kullanmak için Game ve Chat kanallarına ses çıkış cihazı seçin.",

        ["Str_EqEnabled"] = "EQ Açık",
        ["Str_EqFlattenBtn"] = "Düzleştir",
        ["Str_EqFlattenTooltip"] = "Tüm frekans bantlarını 0 dB yap",
        ["Str_EqResetTooltip"] = "EQ ayarlarını seçili profilin varsayılan değerlerine döndür",
        ["Str_EqBandSliderTooltip"] = "0 dB yapmak için çift tıklayın",
        ["Str_EqBandTitleFormat"] = "Bant #{0} İnce Ayarı",
        ["Str_EqFrequencyHz"] = "Frekans (Hz)",
        ["Str_EqQFactor"] = "Bant Genişliği (Q)",
        ["Str_EqFilterType"] = "Filtre Türü",
        ["Str_FilterPeaking"] = "Tepe / Çukur (Peaking)",
        ["Str_FilterLowShelf"] = "Alt Ses Rafı (Bas)",
        ["Str_FilterHighShelf"] = "Üst Ses Rafı (Tiz)",
        ["Str_FilterLowPass"] = "Alçak Geçiren",
        ["Str_FilterHighPass"] = "Yüksek Geçiren",

        ["Str_ActivePresetLabel"] = "Aktif Profil:",
        ["Str_ResetFullPresetBtn"] = "↺ Profili Varsayılana Sıfırla",
        ["Str_ResetFullPresetTooltip"] = "Bu kanaldaki seçili profilin tüm EQ ve ses efektlerini varsayılan ayarlarına döndür",
        ["Str_DspSliderResetTooltip"] = "Varsayılana döndürmek için çift tıklayın",
        ["Str_DspSingleResetTooltip"] = "Bu ayarı varsayılan değerine sıfırla",

        ["Str_SpatialAudio"] = "Uzamsal Ses (7.1 Sanal Çevresel)",
        ["Str_FormFactorHeadphones"] = "Kulaklık",
        ["Str_FormFactorSpeakers"] = "Hoparlör",
        ["Str_ReverbDistance"] = "Mesafe / Yankı",

        ["Str_BassBoost"] = "Bas Güçlendirme",
        ["Str_TrebleBoost"] = "Tiz Güçlendirme",
        ["Str_VoiceClarity"] = "Ses / Diyalog Netliği",
        ["Str_GeneralGain"] = "Genel Ses Kazancı",

        ["Str_SmartVolume"] = "Akıllı Ses Dengeleme",
        ["Str_LoudnessSoft"] = "Yumuşak",
        ["Str_LoudnessBalanced"] = "Dengeli",
        ["Str_LoudnessExtreme"] = "Güçlü",
        ["Str_SmartVolumeLevel"] = "Dengeleme Seviyesi",

        ["Str_AiNoiseCancel"] = "ClearCast AI Yapay Zeka Gürültü Engelleme",
        ["Str_AiNoiseStrength"] = "Engelleme Gücü",
        ["Str_NoiseReductionTitle"] = "Gürültü Azaltma",
        ["Str_NoiseReductionDisabledHint"] = "ClearCast AI Gürültü Engelleme açıkken devre dışıdır",
        ["Str_NoiseReductionBg"] = "Arka Plan Gürültüsü",
        ["Str_NoiseReductionImpact"] = "Ani Ses / Tıkırtı",
        ["Str_NoiseGate"] = "Gürültü Kapısı (Ses Eşiği)",
        ["Str_NoiseGateThreshold"] = "Açılma Eşiği",
        ["Str_AutoNoiseGate"] = "Ses eşiğini otomatik hesapla",
        ["Str_VolumeStabilizer"] = "Ses Sabitleyici (Kompresör)",
        ["Str_VolumeStabilizerLevel"] = "Sabitleme Seviyesi",

        ["Str_SettingsTitle"] = "Uygulama Ayarları",
        ["Str_SettingsBackBtn"] = "← Geri",
        ["Str_SettingsStartupHeader"] = "Başlangıç Ayarları",
        ["Str_SettingsRunAtStartup"] = "Windows başlangıcında SonarTray'i çalıştır",
        ["Str_SettingsRunAtStartupDesc"] = "Bilgisayar açıldığında uygulama sistem tepsisinde otomatik başlar.",
        ["Str_SettingsLanguageHeader"] = "Uygulama Dili",
        ["Str_SettingsEngineHeader"] = "Sonar Ses Motoru",
        ["Str_SettingsEngineDesc"] = "Ses motoru yanıt vermezse veya bağlantı koparsa arka plan servisini yeniden başlatır.",
        ["Str_SettingsRestartEngineBtn"] = "↻ Sonar Motorunu Yeniden Başlat",

        ["Str_LoadingTitle"] = "Sonar Motoru Başlatılıyor...",
        ["Str_LoadingSubtitle"] = "Ses kanalları ve sanal sürücüler hazırlanırken lütfen bekleyin. Bu işlem birkaç saniye sürebilir.",

        ["Str_TrayShowHide"] = "Aç",
        ["Str_TraySettings"] = "Ayarlar",
        ["Str_TrayRefresh"] = "Durumu Yenile",
        ["Str_TrayExit"] = "Çıkış",
        ["Str_TrayExitBusy"] = "Çıkış (Motor Başlatılıyor...)",
    };

    private static readonly Dictionary<string, string> EnStrings = new(StringComparer.Ordinal)
    {
        ["Str_SettingsTooltip"] = "Settings",
        ["Str_PinTooltip"] = "Pin Window (Keep open when clicking outside)",
        ["Str_RefreshTooltip"] = "Refresh Status",
        ["Str_StatusConnecting"] = "Starting / connecting to Sonar engine...",
        ["Str_StatusConnectedGg"] = "Connected • GG Service",
        ["Str_StatusConnectedStandalone"] = "Connected • Standalone Sonar Engine",
        ["Str_TabMixer"] = "Mixer & Devices",
        ["Str_TabEq"] = "Equalizer (EQ)",
        ["Str_TabDsp"] = "Audio Effects",

        ["Str_ToggleChannelTooltip"] = "Enable / disable this channel",
        ["Str_MuteTooltip"] = "Mute / Unmute",
        ["Str_OpenChannelEqTooltip"] = "Open EQ and audio effects for this channel",
        ["Str_ChannelDisabled"] = "Channel Disabled",
        ["Str_SelectDeviceTooltip"] = "Select Audio Device",
        ["Str_SelectPresetTooltip"] = "Select EQ / Audio Preset",
        ["Str_ResetPresetTooltip"] = "Reset selected preset to default settings",
        ["Str_SyncOutputsBtn"] = "Sync All Outputs to Game Device",

        ["Str_ChatMixDoubleClickResetTooltip"] = "Double-click to reset",
        ["Str_ChatMixFooterReset"] = "Double-click: Reset",
        ["Str_ChatMixFooterDisabled"] = "Disabled",
        ["Str_ChatMixBalanced"] = "Balanced",
        ["Str_ChatMixHardwarePrefix"] = "Hardware",
        ["Str_ChatMixHardwareControl"] = "Hardware Dial",
        ["Str_ChatMixDifferentDevice"] = "Different Device",
        ["Str_ChatMixNoDevice"] = "No Device Selected",
        ["Str_ChatMixReasonHardware"] = "Please use your hardware dial (Dock / Headset) to control ChatMix.",
        ["Str_ChatMixReasonDifferentDevice"] = "Game and Chat channels must be routed to the same audio output device for ChatMix.",
        ["Str_ChatMixReasonNoDevice"] = "Select an audio output device for Game and Chat channels to use ChatMix.",

        ["Str_EqEnabled"] = "EQ Enabled",
        ["Str_EqFlattenBtn"] = "Flatten",
        ["Str_EqFlattenTooltip"] = "Flatten all frequency bands to 0 dB",
        ["Str_EqResetTooltip"] = "Reset EQ settings to the selected preset's defaults",
        ["Str_EqBandSliderTooltip"] = "Double-click to reset to 0 dB",
        ["Str_EqBandTitleFormat"] = "Band #{0} Fine Tuning",
        ["Str_EqFrequencyHz"] = "Frequency (Hz)",
        ["Str_EqQFactor"] = "Bandwidth (Q-Factor)",
        ["Str_EqFilterType"] = "Filter Type",
        ["Str_FilterPeaking"] = "Peaking EQ",
        ["Str_FilterLowShelf"] = "Low Shelf",
        ["Str_FilterHighShelf"] = "High Shelf",
        ["Str_FilterLowPass"] = "Low Pass",
        ["Str_FilterHighPass"] = "High Pass",

        ["Str_ActivePresetLabel"] = "Active Preset:",
        ["Str_ResetFullPresetBtn"] = "↺ Reset Preset to Default",
        ["Str_ResetFullPresetTooltip"] = "Reset all EQ and audio effects of the selected preset to default",
        ["Str_DspSliderResetTooltip"] = "Double-click to reset to default",
        ["Str_DspSingleResetTooltip"] = "Reset this setting to default",

        ["Str_SpatialAudio"] = "Spatial Audio (7.1 Virtual Surround)",
        ["Str_FormFactorHeadphones"] = "Headphones",
        ["Str_FormFactorSpeakers"] = "Speakers",
        ["Str_ReverbDistance"] = "Distance / Reverb",

        ["Str_BassBoost"] = "Bass Boost",
        ["Str_TrebleBoost"] = "Treble Boost",
        ["Str_VoiceClarity"] = "Voice Clarity",
        ["Str_GeneralGain"] = "General Gain",

        ["Str_SmartVolume"] = "Smart Volume",
        ["Str_LoudnessSoft"] = "Quiet",
        ["Str_LoudnessBalanced"] = "Balanced",
        ["Str_LoudnessExtreme"] = "Loud",
        ["Str_SmartVolumeLevel"] = "Balancing Level",

        ["Str_AiNoiseCancel"] = "ClearCast AI Noise Cancellation",
        ["Str_AiNoiseStrength"] = "Strength",
        ["Str_NoiseReductionTitle"] = "Noise Reduction",
        ["Str_NoiseReductionDisabledHint"] = "Disabled while ClearCast AI Noise Cancellation is active",
        ["Str_NoiseReductionBg"] = "Background Noise",
        ["Str_NoiseReductionImpact"] = "Impact Noise",
        ["Str_NoiseGate"] = "Noise Gate",
        ["Str_NoiseGateThreshold"] = "Threshold",
        ["Str_AutoNoiseGate"] = "Automatically compute threshold",
        ["Str_VolumeStabilizer"] = "Volume Stabilizer (Compressor)",
        ["Str_VolumeStabilizerLevel"] = "Stabilizer Level",

        ["Str_SettingsTitle"] = "Application Settings",
        ["Str_SettingsBackBtn"] = "← Back",
        ["Str_SettingsStartupHeader"] = "Startup Settings",
        ["Str_SettingsRunAtStartup"] = "Run SonarTray on Windows startup",
        ["Str_SettingsRunAtStartupDesc"] = "Automatically starts the app in the system tray when Windows boots.",
        ["Str_SettingsLanguageHeader"] = "Application Language",
        ["Str_SettingsEngineHeader"] = "Sonar Audio Engine",
        ["Str_SettingsEngineDesc"] = "Restarts the background audio service if the engine stops responding or loses connection.",
        ["Str_SettingsRestartEngineBtn"] = "↻ Restart Sonar Engine",

        ["Str_LoadingTitle"] = "Starting Sonar Engine...",
        ["Str_LoadingSubtitle"] = "Please wait while audio channels and virtual drivers are being prepared. This may take a few seconds.",

        ["Str_TrayShowHide"] = "Open",
        ["Str_TraySettings"] = "Settings",
        ["Str_TrayRefresh"] = "Refresh Status",
        ["Str_TrayExit"] = "Exit",
        ["Str_TrayExitBusy"] = "Exit (Engine Starting...)",
    };

    public static AppLanguage CurrentLanguage { get; private set; } = AppLanguage.Tr;

    public static event Action? LanguageChanged;

    public static void Initialize()
    {
        var loaded = LoadSavedLanguage();
        SetLanguage(loaded, save: false);
    }

    public static void SetLanguage(AppLanguage language, bool save = true)
    {
        CurrentLanguage = language;

        var activeMap = language == AppLanguage.En ? EnStrings : TrStrings;
        if (Application.Current is { Resources: { } resources })
        {
            foreach (var (key, value) in activeMap)
            {
                resources[key] = value;
            }
        }

        if (save)
        {
            SaveLanguage(language);
        }

        LanguageChanged?.Invoke();
    }

    public static string Get(string key)
    {
        var activeMap = CurrentLanguage == AppLanguage.En ? EnStrings : TrStrings;
        if (activeMap.TryGetValue(key, out var value))
        {
            return value;
        }

        return TrStrings.TryGetValue(key, out var fallback) ? fallback : key;
    }

    public static string Format(string key, params object[] args) =>
        string.Format(CultureInfo.InvariantCulture, Get(key), args);

    private static AppLanguage LoadSavedLanguage()
    {
        try
        {
            if (!File.Exists(SettingsPath))
            {
                return AppLanguage.Tr;
            }

            var json = File.ReadAllText(SettingsPath);
            var langValue = JsonNode.Parse(json)?["language"]?.GetValue<string>();
            return string.Equals(langValue, "en", StringComparison.OrdinalIgnoreCase)
                ? AppLanguage.En
                : AppLanguage.Tr;
        }
        catch
        {
            return AppLanguage.Tr;
        }
    }

    private static void SaveLanguage(AppLanguage language)
    {
        try
        {
            Directory.CreateDirectory(SettingsDirectory);
            var obj = new JsonObject
            {
                ["language"] = language == AppLanguage.En ? "en" : "tr"
            };
            File.WriteAllText(
                SettingsPath,
                obj.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        }
        catch
        {
            // Ignore settings write failure when disk/permissions disallow it.
        }
    }
}
