using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Media;

namespace SonarTray;

public static class SonarChannel
{
    public const string Master = "master";
    public const string Game = "game";
    public const string Chat = "chatRender";
    public const string Media = "media";
    public const string Aux = "aux";
    public const string Mic = "chatCapture";

    public static bool IsVoiceChannel(string channelKey) =>
        channelKey is Chat or Mic;
}

public sealed record ChannelState(double Volume, bool Muted);

public sealed record SonarAudioDevice(string Id, string FriendlyName, string DataFlow)
{
    public override string ToString() => FriendlyName;
}

public sealed record SonarConfigSummary(
    string Id,
    string Name,
    string VirtualAudioDevice,
    bool IsPreset,
    bool IsFavorite)
{
    public override string ToString() => Name;
}

public sealed record LiveMixerSnapshot(
    Dictionary<string, ChannelState> Channels,
    double ChatMixBalance,
    string ChatMixState,
    Dictionary<string, string> Redirections);

public sealed class SonarState
{
    public Dictionary<string, ChannelState> Channels { get; init; } = new();
    public double ChatMixBalance { get; set; }
    public string ChatMixState { get; set; } = "enabled";
    public List<SonarAudioDevice> RenderDevices { get; init; } = [];
    public List<SonarAudioDevice> CaptureDevices { get; init; } = [];
    public Dictionary<string, string> Redirections { get; init; } = new();
    public Dictionary<string, List<SonarConfigSummary>> ConfigsByChannel { get; init; } = new();
    public Dictionary<string, ChannelDspConfig> SelectedConfigs { get; init; } = new();
    public HashSet<string> EnabledVadChannels { get; init; } = new(StringComparer.OrdinalIgnoreCase);
    public bool IsConnected { get; set; }
    public bool IsStandaloneMode { get; set; }
    public string? StatusMessage { get; set; }
}

public sealed class EqBandSetting
{
    public int Index { get; init; }
    public bool Enabled { get; set; }
    public double Frequency { get; set; }
    public double Gain { get; set; }
    public double QFactor { get; set; }
    public string Type { get; set; } = "peakingEQ";
}

public sealed class ChannelDspConfig
{
    public required string ConfigId { get; init; }
    public required string ConfigName { get; init; }
    public required string VirtualAudioDevice { get; init; }
    public bool IsPreset { get; init; }
    public required JsonObject RawConfig { get; init; }

    public bool IsVoiceChannel => SonarChannel.IsVoiceChannel(VirtualAudioDevice);

    public bool EqEnabled { get; set; }
    public List<EqBandSetting> EqBands { get; init; } = [];

    // Game / Media / Aux DSP
    public bool VirtualSurroundEnabled { get; set; }
    public double ReverbGainDb { get; set; } = -6.0;
    public string FormFactor { get; set; } = "headphones";
    public bool BassBoostEnabled { get; set; }
    public double BassBoostValue { get; set; }
    public bool TrebleBoostEnabled { get; set; }
    public double TrebleBoostValue { get; set; }
    public bool VoiceClarityEnabled { get; set; }
    public double VoiceClarityValue { get; set; }
    public bool SmartVolumeEnabled { get; set; }
    public double SmartVolumeLevel { get; set; }
    public string SmartVolumeLoudness { get; set; } = "balanced";
    public double GeneralGain { get; set; }

    // Chat / Mic DSP
    public bool AiNoiseCancelingEnabled { get; set; }
    public double AiNoiseCancelingValue { get; set; } = 0.9;
    public bool NoiseReductionEnabled { get; set; }
    public double NoiseReductionValue { get; set; }
    public bool ImpactNoiseReductionEnabled { get; set; }
    public double ImpactNoiseReductionValue { get; set; }
    public bool NoiseGateEnabled { get; set; }
    public double NoiseGateValue { get; set; } = -60.0;
    public bool AutoNoiseGateEnabled { get; set; }
    public bool VolumeStabilizerEnabled { get; set; }
    public double VolumeStabilizerValue { get; set; }

    public static ChannelDspConfig FromJson(JsonObject rawConfig)
    {
        var clone = rawConfig.DeepClone().AsObject();
        var data = EnsureObject(clone, "data");
        var vad = clone["virtualAudioDevice"]?.GetValue<string>() ?? SonarChannel.Game;

        var cfg = new ChannelDspConfig
        {
            ConfigId = clone["id"]?.GetValue<string>() ?? string.Empty,
            ConfigName = clone["name"]?.GetValue<string>() ?? "Custom",
            VirtualAudioDevice = vad,
            IsPreset = clone["isPreset"]?.GetValue<bool>() ?? false,
            RawConfig = clone
        };

        cfg.LoadFromDataObject(data);
        return cfg;
    }

    public bool ResetAllToDefault()
    {
        if (RawConfig["defaultData"] is not JsonObject defaultData)
        {
            return false;
        }

        var restored = defaultData.DeepClone().AsObject();
        RawConfig["data"] = restored;
        LoadFromDataObject(restored);
        return true;
    }

    public bool ResetEqToDefault()
    {
        var defaultPeq = (RawConfig["defaultData"] as JsonObject)?["parametricEQ"] as JsonObject;
        if (defaultPeq is null)
        {
            return false;
        }

        var data = EnsureObject(RawConfig, "data");
        data["parametricEQ"] = defaultPeq.DeepClone().AsObject();
        LoadEqFromDataObject(data);
        return true;
    }

    public bool ResetEffectToDefault(string effectKey)
    {
        if (RawConfig["defaultData"] is not JsonObject defaultData)
        {
            return false;
        }

        var data = EnsureObject(RawConfig, "data");

        switch (effectKey)
        {
            case "spatial":
                CopyJsonProperty(defaultData, data, "virtualSurroundState");
                CopyJsonProperty(defaultData, data, "reverbGainDB");
                CopyJsonProperty(defaultData, data, "formFactor");
                CopyJsonProperty(defaultData, data, "virtualSurroundChannels");
                VirtualSurroundEnabled = defaultData["virtualSurroundState"]?.GetValue<bool>() ?? false;
                ReverbGainDb = defaultData["reverbGainDB"]?.GetValue<double>() ?? -6.0;
                FormFactor = defaultData["formFactor"]?.GetValue<string>() ?? "headphones";
                return true;

            case "bassBoost":
                CopyJsonProperty(defaultData, data, "bassBoostState");
                (BassBoostEnabled, BassBoostValue) = ReadStateWithValue(defaultData["bassBoostState"] as JsonObject, 0.0);
                return true;

            case "trebleBoost":
                CopyJsonProperty(defaultData, data, "trebleBoostState");
                (TrebleBoostEnabled, TrebleBoostValue) = ReadStateWithValue(defaultData["trebleBoostState"] as JsonObject, 0.0);
                return true;

            case "voiceClarity":
                CopyJsonProperty(defaultData, data, "voiceClarityState");
                (VoiceClarityEnabled, VoiceClarityValue) = ReadStateWithValue(defaultData["voiceClarityState"] as JsonObject, 0.0);
                return true;

            case "generalGain":
                CopyJsonProperty(defaultData, data, "generalGain");
                GeneralGain = defaultData["generalGain"]?.GetValue<double>() ?? 0.0;
                return true;

            case "smartVolume":
                CopyJsonProperty(defaultData, data, "smartVolume");
                var sv = defaultData["smartVolume"] as JsonObject;
                SmartVolumeEnabled = sv?["enabled"]?.GetValue<bool>() ?? false;
                SmartVolumeLevel = sv?["volumeLevel"]?.GetValue<double>() ?? 0.0;
                SmartVolumeLoudness = sv?["loudness"]?.GetValue<string>() ?? "balanced";
                return true;

            case "aiNoise":
                CopyJsonProperty(defaultData, data, "noiseCancelingState");
                (AiNoiseCancelingEnabled, AiNoiseCancelingValue) = ReadStateWithValue(defaultData["noiseCancelingState"] as JsonObject, 0.9);
                return true;

            case "noiseReduction":
                CopyJsonProperty(defaultData, data, "noiseReductionState");
                (NoiseReductionEnabled, NoiseReductionValue) = ReadStateWithValue(defaultData["noiseReductionState"] as JsonObject, 0.0);
                return true;

            case "impactNoise":
                CopyJsonProperty(defaultData, data, "impactNoiseReductionState");
                (ImpactNoiseReductionEnabled, ImpactNoiseReductionValue) = ReadStateWithValue(defaultData["impactNoiseReductionState"] as JsonObject, 0.0);
                return true;

            case "noiseReductionAll":
                return ResetEffectToDefault("noiseReduction") | ResetEffectToDefault("impactNoise");

            case "volumeStabilizer":
                CopyJsonProperty(defaultData, data, "volumeStabilizerState");
                (VolumeStabilizerEnabled, VolumeStabilizerValue) = ReadStateWithValue(defaultData["volumeStabilizerState"] as JsonObject, 0.0);
                return true;

            case "noiseGate":
                CopyJsonProperty(defaultData, data, "noiseGateState");
                CopyJsonProperty(defaultData, data, "automaticNoiseGateState");
                (NoiseGateEnabled, NoiseGateValue) = ReadStateWithValue(defaultData["noiseGateState"] as JsonObject, -60.0);
                (AutoNoiseGateEnabled, _) = ReadStateWithValue(defaultData["automaticNoiseGateState"] as JsonObject, 0.0);
                return true;

            default:
                return false;
        }
    }

    public void ApplyToRawJson()
    {
        var data = EnsureObject(RawConfig, "data");
        var peq = EnsureObject(data, "parametricEQ");
        peq["enabled"] = EqEnabled;

        foreach (var band in EqBands)
        {
            var filter = EnsureObject(peq, $"filter{band.Index}");
            filter["enabled"] = band.Enabled;
            filter["frequency"] = Math.Clamp(band.Frequency, 20.0, 20000.0);
            filter["gain"] = Math.Clamp(band.Gain, -12.0, 12.0);
            filter["qFactor"] = Math.Clamp(band.QFactor, 0.1, 10.0);
            filter["type"] = band.Type;
        }

        if (IsVoiceChannel)
        {
            SetStateWithValue(data, "noiseCancelingState", AiNoiseCancelingEnabled, Math.Clamp(AiNoiseCancelingValue, 0.0, 1.0));
            SetStateWithValue(data, "noiseReductionState", NoiseReductionEnabled, Math.Clamp(NoiseReductionValue, 0.0, 1.0));
            SetStateWithValue(data, "impactNoiseReductionState", ImpactNoiseReductionEnabled, Math.Clamp(ImpactNoiseReductionValue, 0.0, 1.0));
            SetStateWithValue(data, "noiseGateState", NoiseGateEnabled, Math.Clamp(NoiseGateValue, -60.0, -10.0));
            SetStateWithValue(data, "automaticNoiseGateState", AutoNoiseGateEnabled, 0.0);
            SetStateWithValue(data, "volumeStabilizerState", VolumeStabilizerEnabled, Math.Clamp(VolumeStabilizerValue, 0.0, 1.0));
        }
        else
        {
            data["virtualSurroundState"] = VirtualSurroundEnabled;
            data["reverbGainDB"] = Math.Clamp(ReverbGainDb, -12.0, 0.0);
            data["formFactor"] = FormFactor;
            data["generalGain"] = Math.Clamp(GeneralGain, 0.0, 12.0);

            SetStateWithValue(data, "bassBoostState", BassBoostEnabled, Math.Clamp(BassBoostValue, -12.0, 12.0));
            SetStateWithValue(data, "trebleBoostState", TrebleBoostEnabled, Math.Clamp(TrebleBoostValue, -12.0, 12.0));
            SetStateWithValue(data, "voiceClarityState", VoiceClarityEnabled, Math.Clamp(VoiceClarityValue, -12.0, 12.0));

            var sv = EnsureObject(data, "smartVolume");
            sv["enabled"] = SmartVolumeEnabled;
            sv["volumeLevel"] = Math.Clamp(SmartVolumeLevel, 0.0, 1.0);
            sv["loudness"] = SmartVolumeLoudness;
        }
    }

    public static double RawToUiAiNoise(double raw)
    {
        var clampedRaw = Math.Clamp(raw, 0.0, 1.0);
        var db = 60.0 * clampedRaw - 60.0;
        var scaled = Math.Clamp(Math.Pow(10.0, db / 20.0), 0.0, 1.0);
        return 100.0 * scaled;
    }

    public static double UiToRawAiNoise(double uiPercent)
    {
        if (uiPercent <= 0.0)
        {
            return 0.0;
        }

        var db = Math.Clamp(20.0 * Math.Log10(uiPercent / 100.0), -60.0, 0.0);
        return Math.Clamp((60.0 + db) / 60.0, 0.0, 1.0);
    }

    private void LoadFromDataObject(JsonObject data)
    {
        LoadEqFromDataObject(data);

        if (IsVoiceChannel)
        {
            (AiNoiseCancelingEnabled, AiNoiseCancelingValue) = ReadStateWithValue(data["noiseCancelingState"] as JsonObject, 0.9);
            (NoiseReductionEnabled, NoiseReductionValue) = ReadStateWithValue(data["noiseReductionState"] as JsonObject, 0.0);
            (ImpactNoiseReductionEnabled, ImpactNoiseReductionValue) = ReadStateWithValue(data["impactNoiseReductionState"] as JsonObject, 0.0);
            (NoiseGateEnabled, NoiseGateValue) = ReadStateWithValue(data["noiseGateState"] as JsonObject, -60.0);
            (AutoNoiseGateEnabled, _) = ReadStateWithValue(data["automaticNoiseGateState"] as JsonObject, 0.0);
            (VolumeStabilizerEnabled, VolumeStabilizerValue) = ReadStateWithValue(data["volumeStabilizerState"] as JsonObject, 0.0);
        }
        else
        {
            VirtualSurroundEnabled = data["virtualSurroundState"]?.GetValue<bool>() ?? false;
            ReverbGainDb = data["reverbGainDB"]?.GetValue<double>() ?? -6.0;
            FormFactor = data["formFactor"]?.GetValue<string>() ?? "headphones";
            (BassBoostEnabled, BassBoostValue) = ReadStateWithValue(data["bassBoostState"] as JsonObject, 0.0);
            (TrebleBoostEnabled, TrebleBoostValue) = ReadStateWithValue(data["trebleBoostState"] as JsonObject, 0.0);
            (VoiceClarityEnabled, VoiceClarityValue) = ReadStateWithValue(data["voiceClarityState"] as JsonObject, 0.0);

            var sv = data["smartVolume"] as JsonObject;
            SmartVolumeEnabled = sv?["enabled"]?.GetValue<bool>() ?? false;
            SmartVolumeLevel = sv?["volumeLevel"]?.GetValue<double>() ?? 0.0;
            SmartVolumeLoudness = sv?["loudness"]?.GetValue<string>() ?? "balanced";
            GeneralGain = data["generalGain"]?.GetValue<double>() ?? 0.0;
        }
    }

    private void LoadEqFromDataObject(JsonObject data)
    {
        var peq = data["parametricEQ"] as JsonObject;
        EqEnabled = peq?["enabled"]?.GetValue<bool>() ?? false;

        EqBands.Clear();
        for (var i = 1; i <= 10; i++)
        {
            var filter = peq?[$"filter{i}"] as JsonObject;
            EqBands.Add(new EqBandSetting
            {
                Index = i,
                Enabled = filter?["enabled"]?.GetValue<bool>() ?? (i <= 5),
                Frequency = filter?["frequency"]?.GetValue<double>() ?? DefaultFrequency(i),
                Gain = filter?["gain"]?.GetValue<double>() ?? 0.0,
                QFactor = filter?["qFactor"]?.GetValue<double>() ?? 0.7071,
                Type = filter?["type"]?.GetValue<string>() ?? "peakingEQ"
            });
        }
    }

    private static (bool Enabled, double Value) ReadStateWithValue(JsonObject? node, double defaultValue) =>
        (node?["enabled"]?.GetValue<bool>() ?? false, node?["value"]?.GetValue<double>() ?? defaultValue);

    private static void CopyJsonProperty(JsonObject source, JsonObject target, string propertyName)
    {
        if (source[propertyName] is JsonNode node)
        {
            target[propertyName] = node.DeepClone();
        }
        else
        {
            target.Remove(propertyName);
        }
    }

    private static void SetStateWithValue(JsonObject parent, string key, bool enabled, double value)
    {
        var obj = EnsureObject(parent, key);
        obj["enabled"] = enabled;
        obj["value"] = value;
    }

    private static JsonObject EnsureObject(JsonObject parent, string key)
    {
        if (parent[key] is JsonObject existing)
        {
            return existing;
        }

        var created = new JsonObject();
        parent[key] = created;
        return created;
    }

    private static double DefaultFrequency(int index) => index switch
    {
        1 => 35,
        2 => 120,
        3 => 250,
        4 => 500,
        5 => 1000,
        6 => 2000,
        7 => 4000,
        8 => 6000,
        9 => 12000,
        _ => 18000
    };
}

public sealed class ChannelMixerItem : INotifyPropertyChanged
{
    private double _volume = 1.0;
    private bool _muted;
    private bool _isChannelEnabled = true;
    private List<SonarAudioDevice> _availableDevices = [];
    private string? _selectedDeviceId;
    private List<SonarConfigSummary> _availableConfigs = [];
    private string? _selectedConfigId;

    public ChannelMixerItem(string key, string displayName, bool isMic, int defaultOrder)
    {
        Key = key;
        DisplayName = displayName;
        IsMic = isMic;
        DefaultOrder = defaultOrder;
    }

    public string Key { get; }
    public string DisplayName { get; }
    public bool IsMic { get; }
    public int DefaultOrder { get; }
    public bool HasAdvancedSettings => Key != SonarChannel.Master;
    public bool CanToggleChannelEnabled => Key is SonarChannel.Media or SonarChannel.Aux;

    public Visibility ToggleChannelVisibility =>
        CanToggleChannelEnabled ? Visibility.Visible : Visibility.Collapsed;

    public Visibility ActiveControlsVisibility =>
        IsChannelEnabled ? Visibility.Visible : Visibility.Collapsed;

    public Visibility AdvancedControlsVisibility =>
        (HasAdvancedSettings && IsChannelEnabled) ? Visibility.Visible : Visibility.Collapsed;

    public Visibility DisabledBadgeVisibility =>
        (!IsChannelEnabled && CanToggleChannelEnabled) ? Visibility.Visible : Visibility.Collapsed;

    public bool IsChannelEnabled
    {
        get => _isChannelEnabled;
        set
        {
            var effective = !CanToggleChannelEnabled || value;
            if (_isChannelEnabled == effective)
            {
                return;
            }

            _isChannelEnabled = effective;
            OnPropertyChanged();
            OnPropertyChanged(nameof(ActiveControlsVisibility));
            OnPropertyChanged(nameof(AdvancedControlsVisibility));
            OnPropertyChanged(nameof(DisabledBadgeVisibility));
        }
    }

    public double Volume
    {
        get => _volume;
        set
        {
            var clamped = Math.Clamp(value, 0.0, 1.0);
            if (Math.Abs(_volume - clamped) < 0.0001)
            {
                return;
            }

            _volume = clamped;
            OnPropertyChanged();
            OnPropertyChanged(nameof(VolumePercentText));
        }
    }

    public bool Muted
    {
        get => _muted;
        set
        {
            if (_muted == value)
            {
                return;
            }

            _muted = value;
            OnPropertyChanged();
        }
    }

    public List<SonarAudioDevice> AvailableDevices
    {
        get => _availableDevices;
        set
        {
            _availableDevices = value;
            OnPropertyChanged();
        }
    }

    public string? SelectedDeviceId
    {
        get => _selectedDeviceId;
        set
        {
            if (string.Equals(_selectedDeviceId, value, StringComparison.Ordinal))
            {
                return;
            }

            _selectedDeviceId = value;
            OnPropertyChanged();
        }
    }

    public List<SonarConfigSummary> AvailableConfigs
    {
        get => _availableConfigs;
        set
        {
            _availableConfigs = value;
            OnPropertyChanged();
        }
    }

    public string? SelectedConfigId
    {
        get => _selectedConfigId;
        set
        {
            if (string.Equals(_selectedConfigId, value, StringComparison.Ordinal))
            {
                return;
            }

            _selectedConfigId = value;
            OnPropertyChanged();
        }
    }

    public string VolumePercentText => $"{Math.Round(Volume * 100):0}%";

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}

public sealed class EqBandViewModel : INotifyPropertyChanged
{
    private static readonly Brush SelectedBgBrush = MainWindow.CreateFrozenBrush(0x2E, 0x22, 0x1D);
    private static readonly Brush SelectedBorderBrush = MainWindow.CreateFrozenBrush(0xFF, 0x55, 0x00);

    private bool _enabled = true;
    private double _frequency = 1000;
    private double _gain;
    private double _qFactor = 0.7071;
    private string _type = "peakingEQ";
    private bool _isSelected;

    public EqBandViewModel(int index)
    {
        Index = index;
    }

    public int Index { get; }

    public bool Enabled
    {
        get => _enabled;
        set
        {
            if (_enabled == value)
            {
                return;
            }

            _enabled = value;
            OnPropertyChanged();
        }
    }

    public double Frequency
    {
        get => _frequency;
        set
        {
            var clamped = Math.Clamp(value, 20.0, 20000.0);
            if (Math.Abs(_frequency - clamped) < 0.01)
            {
                return;
            }

            _frequency = clamped;
            OnPropertyChanged();
            OnPropertyChanged(nameof(FrequencyShortText));
        }
    }

    public double Gain
    {
        get => _gain;
        set
        {
            var clamped = Math.Clamp(value, -12.0, 12.0);
            if (Math.Abs(_gain - clamped) < 0.005)
            {
                return;
            }

            _gain = clamped;
            OnPropertyChanged();
            OnPropertyChanged(nameof(GainText));
        }
    }

    public double QFactor
    {
        get => _qFactor;
        set
        {
            var clamped = Math.Clamp(value, 0.1, 10.0);
            if (Math.Abs(_qFactor - clamped) < 0.0005)
            {
                return;
            }

            _qFactor = clamped;
            OnPropertyChanged();
        }
    }

    public string Type
    {
        get => _type;
        set
        {
            if (string.Equals(_type, value, StringComparison.Ordinal))
            {
                return;
            }

            _type = value;
            OnPropertyChanged();
        }
    }

    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (_isSelected == value)
            {
                return;
            }

            _isSelected = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(SelectionBgBrush));
            OnPropertyChanged(nameof(SelectionBorderBrush));
        }
    }

    public string GainText => Gain > 0.05 ? $"+{Gain:0.0}" : $"{Gain:0.0}";

    public string FrequencyShortText => Frequency >= 1000
        ? $"{Frequency / 1000.0:0.#}k"
        : $"{Math.Round(Frequency):0}";

    public Brush SelectionBgBrush => IsSelected ? SelectedBgBrush : Brushes.Transparent;
    public Brush SelectionBorderBrush => IsSelected ? SelectedBorderBrush : Brushes.Transparent;

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
