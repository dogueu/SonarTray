using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json.Nodes;

namespace SonarTray;

/// <summary>
/// Full local REST API client for SteelSeries Sonar with automatic standalone engine fallback.
/// </summary>
public sealed class SonarClient : IDisposable
{
    private static readonly (string Key, Func<JsonNode?, JsonNode?> Selector)[] ChannelMappings =
    [
        (SonarChannel.Master, root => root?["masters"]?["classic"]),
        (SonarChannel.Game, root => root?["devices"]?["game"]?["classic"]),
        (SonarChannel.Chat, root => root?["devices"]?["chatRender"]?["classic"]),
        (SonarChannel.Media, root => root?["devices"]?["media"]?["classic"]),
        (SonarChannel.Aux, root => root?["devices"]?["aux"]?["classic"]),
        (SonarChannel.Mic, root => root?["devices"]?["chatCapture"]?["classic"]),
    ];

    private static readonly Dictionary<string, string> ChannelToRedirectionId = new(StringComparer.Ordinal)
    {
        [SonarChannel.Game] = "game",
        [SonarChannel.Chat] = "chat",
        [SonarChannel.Media] = "media",
        [SonarChannel.Aux] = "aux",
        [SonarChannel.Mic] = "mic"
    };

    private static readonly Dictionary<string, string> RedirectionIdToChannel = new(StringComparer.OrdinalIgnoreCase)
    {
        ["game"] = SonarChannel.Game,
        ["chat"] = SonarChannel.Chat,
        ["media"] = SonarChannel.Media,
        ["aux"] = SonarChannel.Aux,
        ["mic"] = SonarChannel.Mic
    };

    private readonly SonarHostService _hostService;
    private readonly HttpClient _httpClient;
    private readonly SemaphoreSlim _discoveryLock = new(1, 1);
    private readonly object _wsLock = new();

    private Dictionary<string, List<SonarConfigSummary>>? _cachedConfigsByChannel;
    private CancellationTokenSource? _wsCts;
    private string? _sonarBaseUrl;
    private bool _disposed;

    public event Action<Dictionary<string, ChannelState>>? VolumePushReceived;
    public event Action<double, string>? ChatMixPushReceived;
    public event Action? DeviceOrRedirectionPushReceived;

    public SonarClient(SonarHostService hostService)
    {
        _hostService = hostService;

        var handler = new HttpClientHandler
        {
            ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator
        };

        _httpClient = new HttpClient(handler)
        {
            Timeout = TimeSpan.FromSeconds(5)
        };
    }

    public bool IsStandaloneMode { get; private set; }

    public async Task DiscoverAsync(CancellationToken ct = default)
    {
        if (_hostService.IsRunningStandalone && _hostService.StandaloneBaseUrl is string activeStandalone)
        {
            _sonarBaseUrl = activeStandalone;
            IsStandaloneMode = true;
            return;
        }

        if (await TryDiscoverFromRunningGgAsync(ct).ConfigureAwait(false))
        {
            IsStandaloneMode = false;
            return;
        }

        await _hostService.EnsureRunningAsync(ct).ConfigureAwait(false);
        _sonarBaseUrl = _hostService.StandaloneBaseUrl
            ?? throw new InvalidOperationException("Bağımsız Sonar adresi başlatılamadı.");
        IsStandaloneMode = true;
    }

    public void InvalidateEndpoint()
    {
        _sonarBaseUrl = null;
    }

    public async Task<SonarState> GetStateAsync(bool forceReloadConfigs = false, CancellationToken ct = default)
    {
        var channels = await GetVolumeSettingsOnlyAsync(ct).ConfigureAwait(false);
        var (chatMixBalance, rawChatMixState) = await GetChatMixAsync(ct).ConfigureAwait(false);
        var (renderDevices, captureDevices, enabledVadChannels) = await GetAudioDevicesAsync(ct).ConfigureAwait(false);
        var redirections = await GetClassicRedirectionsAsync(ct).ConfigureAwait(false);
        var configsByChannel = await GetConfigsByChannelAsync(forceReloadConfigs, ct).ConfigureAwait(false);
        var selectedConfigs = await GetSelectedConfigsAsync(ct).ConfigureAwait(false);
        var effectiveChatMixState = ResolveEffectiveChatMixState(rawChatMixState, redirections);

        return new SonarState
        {
            Channels = channels,
            ChatMixBalance = chatMixBalance,
            ChatMixState = effectiveChatMixState,
            RenderDevices = renderDevices,
            CaptureDevices = captureDevices,
            Redirections = redirections,
            ConfigsByChannel = configsByChannel,
            SelectedConfigs = selectedConfigs,
            EnabledVadChannels = enabledVadChannels,
            IsConnected = true,
            IsStandaloneMode = IsStandaloneMode,
            StatusMessage = LocalizationManager.Get(IsStandaloneMode
                ? "Str_StatusConnectedStandalone"
                : "Str_StatusConnectedGg")
        };
    }

    public async Task<LiveMixerSnapshot> GetLiveMixerSnapshotAsync(CancellationToken ct = default)
    {
        var volumeTask = GetVolumeSettingsOnlyAsync(ct);
        var chatMixTask = GetChatMixAsync(ct);
        var redirectionsTask = GetClassicRedirectionsAsync(ct);

        await Task.WhenAll(volumeTask, chatMixTask, redirectionsTask).ConfigureAwait(false);

        var channels = await volumeTask.ConfigureAwait(false);
        var (chatMixBalance, rawChatMixState) = await chatMixTask.ConfigureAwait(false);
        var redirections = await redirectionsTask.ConfigureAwait(false);
        var effectiveChatMixState = ResolveEffectiveChatMixState(rawChatMixState, redirections);

        return new LiveMixerSnapshot(channels, chatMixBalance, effectiveChatMixState, redirections);
    }

    public async Task<Dictionary<string, ChannelState>> GetVolumeSettingsOnlyAsync(CancellationToken ct = default)
    {
        using var volumeResponse = await SendWithRetryAsync(
            baseUrl => new HttpRequestMessage(HttpMethod.Get, $"{baseUrl}/volumeSettings/classic"),
            ct).ConfigureAwait(false);

        volumeResponse.EnsureSuccessStatusCode();
        var volumeJson = await volumeResponse.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        var volumeRoot = JsonNode.Parse(volumeJson);
        return ParseVolumeChannels(volumeRoot);
    }

    public static Dictionary<string, ChannelState> ParseVolumeChannels(JsonNode? volumeRoot)
    {
        var channels = new Dictionary<string, ChannelState>(ChannelMappings.Length);
        foreach (var (key, selector) in ChannelMappings)
        {
            channels[key] = ParseChannelState(selector(volumeRoot));
        }

        return channels;
    }

    public void StartRealtimeSocket()
    {
        lock (_wsLock)
        {
            if (_disposed || _wsCts is not null)
            {
                return;
            }

            var cts = new CancellationTokenSource();
            _wsCts = cts;
            _ = Task.Run(() => ListenWebSocketLoopAsync(cts));
        }
    }

    public void StopRealtimeSocket()
    {
        CancellationTokenSource? cts;
        lock (_wsLock)
        {
            cts = _wsCts;
            _wsCts = null;
        }

        if (cts is null)
        {
            return;
        }

        try
        {
            cts.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }
    }

    public static string ResolveEffectiveChatMixState(
        string rawChatMixState,
        IReadOnlyDictionary<string, string> redirections)
    {
        if (!redirections.TryGetValue(SonarChannel.Game, out var gameDeviceId) ||
            string.IsNullOrWhiteSpace(gameDeviceId) ||
            !redirections.TryGetValue(SonarChannel.Chat, out var chatDeviceId) ||
            string.IsNullOrWhiteSpace(chatDeviceId))
        {
            return "noDeviceSelected";
        }

        if (!string.Equals(gameDeviceId, chatDeviceId, StringComparison.OrdinalIgnoreCase))
        {
            return "differentDeviceSelected";
        }

        return string.IsNullOrWhiteSpace(rawChatMixState) ? "enabled" : rawChatMixState;
    }

    public async Task SetVolumeAsync(string channelKey, double volume, CancellationToken ct = default)
    {
        var clamped = Math.Clamp(volume, 0.0, 1.0);
        var formatted = clamped.ToString("0.00", CultureInfo.InvariantCulture);

        using var response = await SendWithRetryAsync(
            baseUrl => new HttpRequestMessage(
                HttpMethod.Put,
                $"{baseUrl}/volumeSettings/classic/{channelKey}/Volume/{formatted}"),
            ct).ConfigureAwait(false);

        response.EnsureSuccessStatusCode();
    }

    public async Task SetMuteAsync(string channelKey, bool muted, CancellationToken ct = default)
    {
        var mutedStr = muted ? "true" : "false";

        using var response = await SendWithRetryAsync(
            baseUrl => new HttpRequestMessage(
                HttpMethod.Put,
                $"{baseUrl}/volumeSettings/classic/{channelKey}/Mute/{mutedStr}"),
            ct).ConfigureAwait(false);

        response.EnsureSuccessStatusCode();
    }

    public async Task SetChatMixAsync(double balance, CancellationToken ct = default)
    {
        var clamped = Math.Clamp(balance, -1.0, 1.0);
        var formatted = clamped.ToString("0.00", CultureInfo.InvariantCulture);

        using var response = await SendWithRetryAsync(
            baseUrl => new HttpRequestMessage(HttpMethod.Put, $"{baseUrl}/v1/chatMix?balance={formatted}"),
            ct).ConfigureAwait(false);

        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            using var fallback = await SendWithRetryAsync(
                baseUrl => new HttpRequestMessage(HttpMethod.Put, $"{baseUrl}/chatMix?balance={formatted}"),
                ct).ConfigureAwait(false);

            if (fallback.StatusCode != HttpStatusCode.NotFound)
            {
                fallback.EnsureSuccessStatusCode();
            }
            return;
        }

        response.EnsureSuccessStatusCode();
    }

    public async Task SetChannelDeviceAsync(string channelKey, string deviceId, CancellationToken ct = default)
    {
        if (!ChannelToRedirectionId.TryGetValue(channelKey, out var redirectionId))
        {
            throw new ArgumentException($"Geçersiz kanal anahtarı: {channelKey}", nameof(channelKey));
        }

        var encodedId = Uri.EscapeDataString(deviceId);
        using var response = await SendWithRetryAsync(
            baseUrl => new HttpRequestMessage(
                HttpMethod.Put,
                $"{baseUrl}/classicRedirections/{redirectionId}/deviceId/{encodedId}"),
            ct).ConfigureAwait(false);

        response.EnsureSuccessStatusCode();
    }

    public async Task SetAllRenderDevicesAsync(string deviceId, CancellationToken ct = default)
    {
        var encodedId = Uri.EscapeDataString(deviceId);
        using var response = await SendWithRetryAsync(
            baseUrl => new HttpRequestMessage(
                HttpMethod.Put,
                $"{baseUrl}/classicRedirections/render/deviceId/{encodedId}"),
            ct).ConfigureAwait(false);

        response.EnsureSuccessStatusCode();
    }

    public async Task SelectConfigAsync(string configId, CancellationToken ct = default)
    {
        var encodedId = Uri.EscapeDataString(configId);
        using var response = await SendWithRetryAsync(
            baseUrl => new HttpRequestMessage(HttpMethod.Put, $"{baseUrl}/configs/{encodedId}/select"),
            ct).ConfigureAwait(false);

        response.EnsureSuccessStatusCode();
    }

    public async Task UpdateConfigAsync(ChannelDspConfig dspConfig, CancellationToken ct = default)
    {
        dspConfig.ApplyToRawJson();
        var jsonBody = dspConfig.RawConfig.ToJsonString();

        using var response = await SendWithRetryAsync(
            baseUrl => new HttpRequestMessage(HttpMethod.Put, $"{baseUrl}/configs")
            {
                Content = new StringContent(jsonBody, Encoding.UTF8, "application/json")
            },
            ct).ConfigureAwait(false);

        response.EnsureSuccessStatusCode();
    }

    public async Task SetChannelEnabledAsync(string channelKey, bool isEnabled, CancellationToken ct = default)
    {
        if (channelKey is not (SonarChannel.Media or SonarChannel.Aux))
        {
            throw new ArgumentException("Yalnızca Media ve Aux kanalları açılıp kapatılabilir.", nameof(channelKey));
        }

        var enabledStr = isEnabled ? "true" : "false";
        using var response = await SendWithRetryAsync(
            baseUrl => new HttpRequestMessage(
                HttpMethod.Put,
                $"{baseUrl}/audioInitialization/{channelKey}/isEnabled/{enabledStr}"),
            ct).ConfigureAwait(false);

        response.EnsureSuccessStatusCode();
    }

    private async Task<bool> TryDiscoverFromRunningGgAsync(CancellationToken ct)
    {
        var corePropsPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "SteelSeries",
            "SteelSeries Engine 3",
            "coreProps.json");

        if (!File.Exists(corePropsPath))
        {
            return false;
        }

        try
        {
            var corePropsJson = await File.ReadAllTextAsync(corePropsPath, ct).ConfigureAwait(false);
            var ggAddress = JsonNode.Parse(corePropsJson)?["ggEncryptedAddress"]?.GetValue<string>();
            if (string.IsNullOrWhiteSpace(ggAddress))
            {
                return false;
            }

            using var probeCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            probeCts.CancelAfter(TimeSpan.FromMilliseconds(1500));

            using var response = await _httpClient
                .GetAsync($"https://{ggAddress.TrimEnd('/')}/subApps", probeCts.Token)
                .ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                return false;
            }

            var subAppsJson = await response.Content.ReadAsStringAsync(probeCts.Token).ConfigureAwait(false);
            var sonarNode = JsonNode.Parse(subAppsJson)?["subApps"]?["sonar"];
            var isRunning = sonarNode?["isRunning"]?.GetValue<bool>() ?? false;
            var webServerAddress = sonarNode?["metadata"]?["webServerAddress"]?.GetValue<string>();

            if (!isRunning || string.IsNullOrWhiteSpace(webServerAddress))
            {
                return false;
            }

            _sonarBaseUrl = webServerAddress.TrimEnd('/');
            return true;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return false;
        }
    }

    private async Task<(double Balance, string State)> GetChatMixAsync(CancellationToken ct)
    {
        using var v1Resp = await SendWithRetryAsync(
            baseUrl => new HttpRequestMessage(HttpMethod.Get, $"{baseUrl}/v1/chatMix"),
            ct).ConfigureAwait(false);

        HttpResponseMessage activeResp = v1Resp;
        HttpResponseMessage? legacyResp = null;

        try
        {
            if (v1Resp.StatusCode == HttpStatusCode.NotFound)
            {
                legacyResp = await SendWithRetryAsync(
                    baseUrl => new HttpRequestMessage(HttpMethod.Get, $"{baseUrl}/chatMix"),
                    ct).ConfigureAwait(false);
                activeResp = legacyResp;
            }

            if (!activeResp.IsSuccessStatusCode)
            {
                return (0.0, "unavailable");
            }

            var json = await activeResp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(json))
            {
                return (0.0, "enabled");
            }

            var root = JsonNode.Parse(json);
            if (root is JsonObject obj)
            {
                var balance = Math.Clamp(obj["balance"]?.GetValue<double>() ?? 0.0, -1.0, 1.0);
                var state = obj["state"]?.GetValue<string>() ?? "enabled";
                return (balance, state);
            }

            if (root is JsonValue val && val.TryGetValue<double>(out var direct))
            {
                return (Math.Clamp(direct, -1.0, 1.0), "enabled");
            }

            return (0.0, "enabled");
        }
        finally
        {
            legacyResp?.Dispose();
        }
    }

    private async Task<(List<SonarAudioDevice> Render, List<SonarAudioDevice> Capture, HashSet<string> EnabledVadChannels)> GetAudioDevicesAsync(CancellationToken ct)
    {
        using var response = await SendWithRetryAsync(
            baseUrl => new HttpRequestMessage(HttpMethod.Get, $"{baseUrl}/audioDevices"),
            ct).ConfigureAwait(false);

        response.EnsureSuccessStatusCode();
        var json = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        var arr = JsonNode.Parse(json)?.AsArray();

        var render = new List<SonarAudioDevice>();
        var capture = new List<SonarAudioDevice>();
        var enabledVads = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (arr is null)
        {
            return (render, capture, enabledVads);
        }

        foreach (var node in arr)
        {
            if (node is not JsonObject obj)
            {
                continue;
            }

            var isVad = obj["isVad"]?.GetValue<bool>() ?? false;
            var state = obj["state"]?.GetValue<string>();
            if (!string.Equals(state, "active", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (isVad)
            {
                var role = obj["role"]?.GetValue<string>();
                if (!string.IsNullOrWhiteSpace(role) && !string.Equals(role, "none", StringComparison.OrdinalIgnoreCase))
                {
                    enabledVads.Add(role);
                }
                continue;
            }

            var id = obj["id"]?.GetValue<string>();
            var friendlyName = obj["friendlyName"]?.GetValue<string>();
            var dataFlow = obj["dataFlow"]?.GetValue<string>() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(friendlyName))
            {
                continue;
            }

            var device = new SonarAudioDevice(id, friendlyName, dataFlow);
            if (string.Equals(dataFlow, "render", StringComparison.OrdinalIgnoreCase))
            {
                render.Add(device);
            }
            else if (string.Equals(dataFlow, "capture", StringComparison.OrdinalIgnoreCase))
            {
                capture.Add(device);
            }
        }

        render.Sort((a, b) => StringComparer.CurrentCultureIgnoreCase.Compare(a.FriendlyName, b.FriendlyName));
        capture.Sort((a, b) => StringComparer.CurrentCultureIgnoreCase.Compare(a.FriendlyName, b.FriendlyName));
        return (render, capture, enabledVads);
    }

    private async Task<Dictionary<string, string>> GetClassicRedirectionsAsync(CancellationToken ct)
    {
        using var response = await SendWithRetryAsync(
            baseUrl => new HttpRequestMessage(HttpMethod.Get, $"{baseUrl}/classicRedirections"),
            ct).ConfigureAwait(false);

        response.EnsureSuccessStatusCode();
        var json = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        var arr = JsonNode.Parse(json)?.AsArray();
        var map = new Dictionary<string, string>(StringComparer.Ordinal);

        if (arr is null)
        {
            return map;
        }

        foreach (var node in arr)
        {
            if (node is not JsonObject obj)
            {
                continue;
            }

            var id = obj["id"]?.GetValue<string>();
            var deviceId = obj["deviceId"]?.GetValue<string>();
            if (!string.IsNullOrWhiteSpace(id) &&
                !string.IsNullOrWhiteSpace(deviceId) &&
                RedirectionIdToChannel.TryGetValue(id, out var channelKey))
            {
                map[channelKey] = deviceId;
            }
        }

        return map;
    }

    private async Task<Dictionary<string, List<SonarConfigSummary>>> GetConfigsByChannelAsync(
        bool forceReload,
        CancellationToken ct)
    {
        if (!forceReload && _cachedConfigsByChannel is not null)
        {
            return _cachedConfigsByChannel;
        }

        using var response = await SendWithRetryAsync(
            baseUrl => new HttpRequestMessage(HttpMethod.Get, $"{baseUrl}/configs"),
            ct).ConfigureAwait(false);

        response.EnsureSuccessStatusCode();
        var json = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        var arr = JsonNode.Parse(json)?.AsArray();

        var grouped = new Dictionary<string, List<SonarConfigSummary>>(StringComparer.Ordinal);
        if (arr is not null)
        {
            foreach (var node in arr)
            {
                if (node is not JsonObject obj)
                {
                    continue;
                }

                var id = obj["id"]?.GetValue<string>();
                var name = obj["name"]?.GetValue<string>();
                var vad = obj["virtualAudioDevice"]?.GetValue<string>();
                if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(vad))
                {
                    continue;
                }

                var isPreset = obj["isPreset"]?.GetValue<bool>() ?? false;
                var isFavorite = obj["isFavorite"]?.GetValue<bool>() ?? false;

                if (!grouped.TryGetValue(vad, out var list))
                {
                    list = [];
                    grouped[vad] = list;
                }

                list.Add(new SonarConfigSummary(id, name, vad, isPreset, isFavorite));
            }
        }

        foreach (var list in grouped.Values)
        {
            list.Sort((a, b) =>
            {
                var presetCmp = a.IsPreset.CompareTo(b.IsPreset);
                if (presetCmp != 0)
                {
                    return presetCmp;
                }

                var favCmp = b.IsFavorite.CompareTo(a.IsFavorite);
                if (favCmp != 0)
                {
                    return favCmp;
                }

                return StringComparer.CurrentCultureIgnoreCase.Compare(a.Name, b.Name);
            });
        }

        _cachedConfigsByChannel = grouped;
        return grouped;
    }

    private async Task<Dictionary<string, ChannelDspConfig>> GetSelectedConfigsAsync(CancellationToken ct)
    {
        using var response = await SendWithRetryAsync(
            baseUrl => new HttpRequestMessage(HttpMethod.Get, $"{baseUrl}/configs/selected"),
            ct).ConfigureAwait(false);

        response.EnsureSuccessStatusCode();
        var json = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        var arr = JsonNode.Parse(json)?.AsArray();

        var map = new Dictionary<string, ChannelDspConfig>(StringComparer.Ordinal);
        if (arr is null)
        {
            return map;
        }

        foreach (var node in arr)
        {
            if (node is JsonObject obj)
            {
                var dsp = ChannelDspConfig.FromJson(obj);
                if (!string.IsNullOrWhiteSpace(dsp.VirtualAudioDevice))
                {
                    map[dsp.VirtualAudioDevice] = dsp;
                }
            }
        }

        return map;
    }

    private static ChannelState ParseChannelState(JsonNode? node)
    {
        if (node is null)
        {
            return new ChannelState(1.0, false);
        }

        var volume = node["volume"]?.GetValue<double>() ?? 1.0;
        var muted = node["muted"]?.GetValue<bool>() ?? false;
        return new ChannelState(Math.Clamp(volume, 0.0, 1.0), muted);
    }

    private async Task<HttpResponseMessage> SendWithRetryAsync(
        Func<string, HttpRequestMessage> requestFactory,
        CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var baseUrl = await EnsureBaseUrlAsync(failedBaseUrl: null, ct).ConfigureAwait(false);

        try
        {
            using var request = requestFactory(baseUrl);
            return await _httpClient.SendAsync(request, ct).ConfigureAwait(false);
        }
        catch (HttpRequestException)
        {
            baseUrl = await EnsureBaseUrlAsync(failedBaseUrl: baseUrl, ct).ConfigureAwait(false);
            using var retryRequest = requestFactory(baseUrl);
            return await _httpClient.SendAsync(retryRequest, ct).ConfigureAwait(false);
        }
    }

    private async Task<string> EnsureBaseUrlAsync(string? failedBaseUrl, CancellationToken ct)
    {
        if (failedBaseUrl is null && !string.IsNullOrEmpty(_sonarBaseUrl))
        {
            return _sonarBaseUrl;
        }

        await _discoveryLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (failedBaseUrl is not null && string.Equals(_sonarBaseUrl, failedBaseUrl, StringComparison.Ordinal))
            {
                _sonarBaseUrl = null;
            }

            if (string.IsNullOrEmpty(_sonarBaseUrl))
            {
                await DiscoverAsync(ct).ConfigureAwait(false);
            }

            return _sonarBaseUrl!;
        }
        finally
        {
            _discoveryLock.Release();
        }
    }

    private async Task ListenWebSocketLoopAsync(CancellationTokenSource cts)
    {
        var ct = cts.Token;
        var buffer = new byte[8192];
        string? failedBaseUrl = null;

        try
        {
            while (!ct.IsCancellationRequested && !_disposed)
            {
                string? baseUrl = null;
                try
                {
                    baseUrl = await EnsureBaseUrlAsync(failedBaseUrl, ct).ConfigureAwait(false);
                    failedBaseUrl = null;

                    var wsBase = baseUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
                        ? "wss://" + baseUrl["https://".Length..]
                        : baseUrl.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
                            ? "ws://" + baseUrl["http://".Length..]
                            : baseUrl;

                    using var ws = new ClientWebSocket();
                    ws.Options.RemoteCertificateValidationCallback = (_, _, _, _) => true;
                    await ws.ConnectAsync(new Uri($"{wsBase}/sock"), ct).ConfigureAwait(false);

                    using var ms = new MemoryStream();
                    while (ws.State == WebSocketState.Open && !ct.IsCancellationRequested)
                    {
                        ms.SetLength(0);
                        WebSocketReceiveResult result;
                        do
                        {
                            result = await ws.ReceiveAsync(new ArraySegment<byte>(buffer), ct).ConfigureAwait(false);
                            if (result.MessageType == WebSocketMessageType.Close)
                            {
                                break;
                            }

                            if (result.Count > 0)
                            {
                                ms.Write(buffer, 0, result.Count);
                            }
                        }
                        while (!result.EndOfMessage);

                        if (result.MessageType == WebSocketMessageType.Close)
                        {
                            break;
                        }

                        if (result.MessageType == WebSocketMessageType.Text && ms.Length > 0)
                        {
                            var msg = Encoding.UTF8.GetString(ms.GetBuffer(), 0, (int)ms.Length);
                            ProcessWebSocketMessage(msg);
                        }
                    }
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    break;
                }
                catch
                {
                    failedBaseUrl = baseUrl;

                    if (ct.IsCancellationRequested)
                    {
                        break;
                    }

                    try
                    {
                        await Task.Delay(800, ct).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }
                }
            }
        }
        finally
        {
            cts.Dispose();
        }
    }

    private void ProcessWebSocketMessage(string json)
    {
        try
        {
            if (JsonNode.Parse(json) is not JsonObject obj)
            {
                return;
            }

            var evt = obj["event"]?.GetValue<string>() ?? obj["Event"]?.GetValue<string>();
            var data = obj["data"] ?? obj["Data"];

            if (string.Equals(evt, "SONAR_EVENT_DEVICE_OUT_VOLUME_DATA", StringComparison.Ordinal) &&
                data is not null)
            {
                var channels = new Dictionary<string, ChannelState>(ChannelMappings.Length);
                foreach (var (key, selector) in ChannelMappings)
                {
                    var channelNode = selector(data);
                    if (channelNode?["volume"] is not null)
                    {
                        channels[key] = ParseChannelState(channelNode);
                    }
                }

                if (channels.Count > 0)
                {
                    VolumePushReceived?.Invoke(channels);
                }
            }
            else if (string.Equals(evt, "EVENT_SONAR_CHATMIX_DATA", StringComparison.Ordinal) &&
                     data is JsonObject chatMixObj)
            {
                var balance = Math.Clamp(chatMixObj["balance"]?.GetValue<double>() ?? 0.0, -1.0, 1.0);
                var state = chatMixObj["state"]?.GetValue<string>() ?? "enabled";
                ChatMixPushReceived?.Invoke(balance, state);
            }
            else if (evt is "SONAR_EVENT_REDIRECTION_STATUS_UPDATE" or
                     "SONAR_EVENT_DEVICE_STATUS_UPDATE" or
                     "SONAR_EVENT_DEVICE_VOLUMES_UPDATE")
            {
                DeviceOrRedirectionPushReceived?.Invoke();
            }
        }
        catch
        {
            // Ignore malformed socket payloads cleanly.
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        StopRealtimeSocket();
        _httpClient.Dispose();
        _discoveryLock.Dispose();
    }
}
