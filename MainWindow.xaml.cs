using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace SonarTray;

public partial class MainWindow : Window
{
    private const int ThrottleIntervalMs = 35;
    private const string ChatMixThrottleKey = "chatmix";

    private static readonly SolidColorBrush ConnectedDotBrush = CreateFrozenBrush(0x22, 0xC5, 0x5E);
    private static readonly SolidColorBrush ConnectingDotBrush = CreateFrozenBrush(0xF5, 0x9E, 0x0B);
    private static readonly SolidColorBrush DisconnectedDotBrush = CreateFrozenBrush(0xEF, 0x44, 0x44);
    private static readonly SolidColorBrush ChatMixActiveTextBrush = CreateFrozenBrush(0xFF, 0x7A, 0x33);
    private static readonly SolidColorBrush ChatMixDisabledTextBrush = CreateFrozenBrush(0xA1, 0xA1, 0xAA);

    private readonly SonarClient _sonarClient;
    private readonly Func<Task>? _restartEngineCallback;
    private readonly List<ChannelMixerItem> _channels;
    private readonly Dictionary<string, ChannelMixerItem> _channelMap;
    private readonly List<EqBandViewModel> _eqBands;
    private readonly Dictionary<string, ThrottleEntry> _throttleEntries = new();
    private readonly DispatcherTimer _liveSyncTimer;

    private SonarState? _latestState;
    private string _selectedAdvancedChannel = SonarChannel.Game;
    private int? _selectedBandIndex;
    private CancellationTokenSource? _refreshCts;
    private bool _isUpdatingUi;
    private bool _isPinned;
    private int _openDropDownCount;
    private bool _isFullRefreshInFlight;
    private bool _isLiveSyncInFlight;
    private int _liveSyncTickCounter;

    public DateTime LastDeactivatedUtc { get; private set; } = DateTime.MinValue;
    public bool IsEngineConnecting { get; private set; } = true;
    public event Action<bool>? EngineConnectingChanged;

    public MainWindow(SonarClient sonarClient, Func<Task>? restartEngineCallback = null)
    {
        InitializeComponent();

        _sonarClient = sonarClient;
        _restartEngineCallback = restartEngineCallback;
        _channels =
        [
            new ChannelMixerItem(SonarChannel.Master, "Master", isMic: false, defaultOrder: 0),
            new ChannelMixerItem(SonarChannel.Game, "Game", isMic: false, defaultOrder: 1),
            new ChannelMixerItem(SonarChannel.Chat, "Chat", isMic: false, defaultOrder: 2),
            new ChannelMixerItem(SonarChannel.Media, "Media", isMic: false, defaultOrder: 3),
            new ChannelMixerItem(SonarChannel.Aux, "Aux", isMic: false, defaultOrder: 4),
            new ChannelMixerItem(SonarChannel.Mic, "Mic", isMic: true, defaultOrder: 5),
        ];

        _channelMap = _channels.ToDictionary(c => c.Key, StringComparer.Ordinal);
        ChannelsItemsControl.ItemsSource = _channels;

        _eqBands = Enumerable.Range(1, 10)
            .Select(i => new EqBandViewModel(i))
            .ToList();
        EqBandsItemsControl.ItemsSource = _eqBands;

        _liveSyncTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(60)
        };
        _liveSyncTimer.Tick += LiveSyncTimer_Tick;
        IsVisibleChanged += MainWindow_IsVisibleChanged;

        _sonarClient.VolumePushReceived += OnVolumePushReceived;
        _sonarClient.ChatMixPushReceived += OnChatMixPushReceived;
        _sonarClient.DeviceOrRedirectionPushReceived += OnDeviceOrRedirectionPushReceived;
        LocalizationManager.LanguageChanged += OnLanguageChanged;

        RefreshLocalizedComboItems();
        LoadSelectedBandInspector();
        SetEngineConnecting(true);

        SizeChanged += (_, _) =>
        {
            if (IsVisible)
            {
                PositionNearTray();
            }
        };
    }

    public void ShowAtTray()
    {
        ClearEqBandSelection();
        if (SettingsButton.IsChecked == true)
        {
            SettingsButton.IsChecked = false;
        }

        if (!IsLoaded)
        {
            Opacity = 0;
            Show();
            UpdateLayout();
            PositionNearTray();
            Opacity = 1;
        }
        else
        {
            PositionNearTray();
            Show();
        }

        Activate();
        if (!_isFullRefreshInFlight)
        {
            _ = RefreshStateAsync();
        }
    }

    public void OpenSettingsView()
    {
        ShowAtTray();
        if (!IsEngineConnecting)
        {
            SettingsButton.IsChecked = true;
        }
    }

    public void SetEngineConnecting(bool isConnecting)
    {
        IsEngineConnecting = isConnecting;
        LoadingOverlayBorder.Visibility = isConnecting ? Visibility.Visible : Visibility.Collapsed;
        SettingsButton.IsEnabled = !isConnecting;
        RefreshButton.IsEnabled = !isConnecting;
        EngineConnectingChanged?.Invoke(isConnecting);
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (IsEngineConnecting)
        {
            e.Cancel = true;
            return;
        }

        base.OnClosing(e);
    }

    public void PositionNearTray()
    {
        var workArea = SystemParameters.WorkArea;
        MaxHeight = Math.Max(360, workArea.Height - 24);

        var width = ActualWidth > 0 ? ActualWidth : Width;
        var height = ActualHeight > 0 ? ActualHeight : Height;

        Left = workArea.Right - width - 12;
        Top = workArea.Top > 0
            ? workArea.Top + 12
            : workArea.Bottom - height - 12;
    }

    public async Task RefreshStateAsync(bool forceReloadConfigs = false)
    {
        _refreshCts?.Cancel();
        _refreshCts?.Dispose();
        var cts = new CancellationTokenSource();
        _refreshCts = cts;
        _isFullRefreshInFlight = true;

        if (_latestState is not { IsConnected: true })
        {
            SetEngineConnecting(true);
        }

        try
        {
            SetConnectionStatus(isConnected: null, LocalizationManager.Get("Str_StatusConnecting"));
            var state = await _sonarClient.GetStateAsync(forceReloadConfigs, cts.Token);
            _latestState = state;

            RunUiUpdate(() =>
            {
                foreach (var item in _channels)
                {
                    if (item.CanToggleChannelEnabled)
                    {
                        item.IsChannelEnabled = state.EnabledVadChannels.Contains(item.Key);
                    }

                    if (state.Channels.TryGetValue(item.Key, out var channelState))
                    {
                        item.Volume = channelState.Volume;
                        item.Muted = channelState.Muted;
                    }

                    if (!item.HasAdvancedSettings)
                    {
                        continue;
                    }

                    item.AvailableDevices = item.IsMic ? state.CaptureDevices : state.RenderDevices;
                    item.SelectedDeviceId = state.Redirections.GetValueOrDefault(item.Key);
                    item.AvailableConfigs = state.ConfigsByChannel.GetValueOrDefault(item.Key, []);
                    item.SelectedConfigId = state.SelectedConfigs.GetValueOrDefault(item.Key)?.ConfigId;
                }

                ChatMixSlider.Value = state.ChatMixBalance;
                UpdateChatMixStatusText(state.ChatMixBalance, state.ChatMixState);
                UpdateMixerChannelOrderAndTabs(state);
                LoadAdvancedChannelUi(_selectedAdvancedChannel);
            });

            SetConnectionStatus(state.IsConnected, state.StatusMessage ?? LocalizationManager.Get("Str_StatusConnectedGg"));
            SetEngineConnecting(false);
        }
        catch (OperationCanceledException)
        {
            // A newer refresh superseded this request.
        }
        catch (Exception ex)
        {
            SetEngineConnecting(false);
            SetConnectionStatus(false, ex.Message);
        }
        finally
        {
            if (ReferenceEquals(_refreshCts, cts))
            {
                _isFullRefreshInFlight = false;
            }
        }
    }

    public void Cleanup()
    {
        _liveSyncTimer.Stop();
        _sonarClient.StopRealtimeSocket();
        _sonarClient.VolumePushReceived -= OnVolumePushReceived;
        _sonarClient.ChatMixPushReceived -= OnChatMixPushReceived;
        _sonarClient.DeviceOrRedirectionPushReceived -= OnDeviceOrRedirectionPushReceived;
        LocalizationManager.LanguageChanged -= OnLanguageChanged;

        _refreshCts?.Cancel();
        _refreshCts?.Dispose();
        _refreshCts = null;

        foreach (var entry in _throttleEntries.Values)
        {
            entry.CancelAndClearPending();
        }

        _throttleEntries.Clear();
    }

    private void MainWindow_IsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (IsVisible)
        {
            _sonarClient.StartRealtimeSocket();
            _liveSyncTimer.Start();
        }
        else
        {
            _liveSyncTimer.Stop();
            _sonarClient.StopRealtimeSocket();
        }
    }

    private void OnVolumePushReceived(Dictionary<string, ChannelState> channels)
    {
        Dispatcher.BeginInvoke(() => ApplyLiveChannelVolumes(channels));
    }

    private void OnChatMixPushReceived(double balance, string rawState)
    {
        Dispatcher.BeginInvoke(() => ApplyLiveChatMix(balance, rawState));
    }

    private void OnDeviceOrRedirectionPushReceived()
    {
        Dispatcher.BeginInvoke(() => _ = PollLiveMixerSnapshotAsync(fullSnapshot: true));
    }

    private void LiveSyncTimer_Tick(object? sender, EventArgs e)
    {
        if (!IsVisible || _isFullRefreshInFlight || _isLiveSyncInFlight)
        {
            return;
        }

        _liveSyncTickCounter++;
        _ = PollLiveMixerSnapshotAsync(fullSnapshot: _liveSyncTickCounter % 6 == 0);
    }

    private async Task PollLiveMixerSnapshotAsync(bool fullSnapshot = false)
    {
        if (!IsVisible || _isLiveSyncInFlight)
        {
            return;
        }

        _isLiveSyncInFlight = true;
        try
        {
            if (!fullSnapshot)
            {
                var channels = await _sonarClient.GetVolumeSettingsOnlyAsync();
                ApplyLiveChannelVolumes(channels);
                return;
            }

            var snapshot = await _sonarClient.GetLiveMixerSnapshotAsync();
            if (!IsVisible)
            {
                return;
            }

            ApplyLiveChannelVolumes(snapshot.Channels);

            if (_openDropDownCount == 0)
            {
                RunUiUpdate(() =>
                {
                    foreach (var item in _channels)
                    {
                        if (item.HasAdvancedSettings)
                        {
                            item.SelectedDeviceId = snapshot.Redirections.GetValueOrDefault(item.Key);
                        }
                    }
                });
            }

            ApplyChatMixSnapshot(snapshot.ChatMixBalance, snapshot.ChatMixState);
        }
        catch
        {
            // Keep existing UI state; manual refresh or next timer tick recovers.
        }
        finally
        {
            _isLiveSyncInFlight = false;
        }
    }

    private void ApplyLiveChannelVolumes(Dictionary<string, ChannelState> channels)
    {
        if (!IsVisible)
        {
            return;
        }

        var isMouseDragging = Mouse.LeftButton == MouseButtonState.Pressed;

        RunUiUpdate(() =>
        {
            foreach (var item in _channels)
            {
                if (channels.TryGetValue(item.Key, out var channelState) &&
                    !isMouseDragging &&
                    !IsKeyRecentlyModifiedByUser(item.Key))
                {
                    item.Volume = channelState.Volume;
                    item.Muted = channelState.Muted;
                }
            }
        });
    }

    private void ApplyLiveChatMix(double balance, string rawState)
    {
        if (!IsVisible)
        {
            return;
        }

        var redirections = _channels
            .Where(c => c.HasAdvancedSettings && !string.IsNullOrWhiteSpace(c.SelectedDeviceId))
            .ToDictionary(c => c.Key, c => c.SelectedDeviceId!, StringComparer.Ordinal);
        var effectiveState = SonarClient.ResolveEffectiveChatMixState(rawState, redirections);
        ApplyChatMixSnapshot(balance, effectiveState);
    }

    private void ApplyChatMixSnapshot(double balance, string effectiveState)
    {
        var isMouseDragging = Mouse.LeftButton == MouseButtonState.Pressed;

        RunUiUpdate(() =>
        {
            if (_latestState is not null)
            {
                _latestState.ChatMixBalance = balance;
                _latestState.ChatMixState = effectiveState;
            }

            if (!isMouseDragging && !IsKeyRecentlyModifiedByUser(ChatMixThrottleKey))
            {
                ChatMixSlider.Value = balance;
            }

            UpdateChatMixStatusText(ChatMixSlider.Value, effectiveState);
        });
    }

    private bool IsKeyRecentlyModifiedByUser(string key) =>
        _throttleEntries.TryGetValue(key, out var entry) &&
        (entry.PendingCts is not null || (Environment.TickCount64 - entry.LastSentTickMs) < 250);

    private void MarkUserModified(string key)
    {
        if (!_throttleEntries.TryGetValue(key, out var entry))
        {
            entry = new ThrottleEntry();
            _throttleEntries[key] = entry;
        }

        entry.LastSentTickMs = Environment.TickCount64;
    }

    private void Window_Deactivated(object? sender, EventArgs e)
    {
        if (_isPinned || _openDropDownCount > 0)
        {
            return;
        }

        LastDeactivatedUtc = DateTime.UtcNow;
        Hide();
    }

    private void ComboBox_DropDownOpened(object? sender, EventArgs e)
    {
        _openDropDownCount++;
    }

    private void ComboBox_DropDownClosed(object? sender, EventArgs e)
    {
        _openDropDownCount = Math.Max(0, _openDropDownCount - 1);
    }

    private void PinButton_Changed(object sender, RoutedEventArgs e)
    {
        _isPinned = PinButton.IsChecked == true;
    }

    private void SettingsButton_Changed(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded)
        {
            return;
        }

        if (SettingsButton.IsChecked == true)
        {
            ClearEqBandSelection();
            LoadSettingsUi();
        }

        UpdateActivePanelVisibility();
    }

    private void LoadSettingsUi()
    {
        RunUiUpdate(() =>
        {
            RunAtStartupToggle.IsChecked = StartupManager.IsSonarTrayStartupEnabled();
            LangTrRadio.IsChecked = LocalizationManager.CurrentLanguage == AppLanguage.Tr;
            LangEnRadio.IsChecked = LocalizationManager.CurrentLanguage == AppLanguage.En;
        });
    }

    private void RunAtStartupToggle_Click(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded || _isUpdatingUi)
        {
            return;
        }

        var target = RunAtStartupToggle.IsChecked == true;
        if (!StartupManager.SetSonarTrayStartupEnabled(target))
        {
            RunUiUpdate(() => RunAtStartupToggle.IsChecked = StartupManager.IsSonarTrayStartupEnabled());
        }
    }

    private void LangRadio_Click(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded || _isUpdatingUi)
        {
            return;
        }

        var targetLang = ReferenceEquals(sender, LangEnRadio) ? AppLanguage.En : AppLanguage.Tr;
        if (LocalizationManager.CurrentLanguage != targetLang)
        {
            LocalizationManager.SetLanguage(targetLang);
        }
    }

    private async void RestartEngineButton_Click(object sender, RoutedEventArgs e)
    {
        if (_restartEngineCallback is null)
        {
            return;
        }

        RestartEngineButton.IsEnabled = false;
        try
        {
            SetConnectionStatus(isConnected: null, LocalizationManager.Get("Str_StatusConnecting"));
            await _restartEngineCallback();
        }
        finally
        {
            RestartEngineButton.IsEnabled = true;
        }
    }

    private void OnLanguageChanged()
    {
        RunUiUpdate(() =>
        {
            LangTrRadio.IsChecked = LocalizationManager.CurrentLanguage == AppLanguage.Tr;
            LangEnRadio.IsChecked = LocalizationManager.CurrentLanguage == AppLanguage.En;
        });

        RefreshLocalizedComboItems();
        LoadSelectedBandInspector();
        UpdateChatMixStatusText(ChatMixSlider.Value, _latestState?.ChatMixState ?? "enabled");

        if (_latestState is { IsConnected: true })
        {
            var status = LocalizationManager.Get(_sonarClient.IsStandaloneMode
                ? "Str_StatusConnectedStandalone"
                : "Str_StatusConnectedGg");
            _latestState.StatusMessage = status;
            SetConnectionStatus(true, status);
        }
    }

    private void RefreshLocalizedComboItems()
    {
        RunUiUpdate(() =>
        {
            FilterPeakingItem.Content = LocalizationManager.Get("Str_FilterPeaking");
            FilterLowShelfItem.Content = LocalizationManager.Get("Str_FilterLowShelf");
            FilterHighShelfItem.Content = LocalizationManager.Get("Str_FilterHighShelf");
            FilterLowPassItem.Content = LocalizationManager.Get("Str_FilterLowPass");
            FilterHighPassItem.Content = LocalizationManager.Get("Str_FilterHighPass");

            FormFactorHeadphonesItem.Content = LocalizationManager.Get("Str_FormFactorHeadphones");
            FormFactorSpeakersItem.Content = LocalizationManager.Get("Str_FormFactorSpeakers");

            LoudnessSoftItem.Content = LocalizationManager.Get("Str_LoudnessSoft");
            LoudnessBalancedItem.Content = LocalizationManager.Get("Str_LoudnessBalanced");
            LoudnessExtremeItem.Content = LocalizationManager.Get("Str_LoudnessExtreme");

            RefreshComboSelectionBox(EqBandTypeCombo);
            RefreshComboSelectionBox(FormFactorCombo);
            RefreshComboSelectionBox(SmartVolumeLoudnessCombo);
        });
    }

    private static void RefreshComboSelectionBox(ComboBox comboBox)
    {
        var selectedIndex = comboBox.SelectedIndex;
        if (selectedIndex < 0)
        {
            return;
        }

        comboBox.SelectedIndex = -1;
        comboBox.SelectedIndex = selectedIndex;
    }

    private void RefreshButton_Click(object sender, RoutedEventArgs e)
    {
        _ = RefreshStateAsync(forceReloadConfigs: true);
    }

    private void CloseSettingsButton_Click(object sender, RoutedEventArgs e)
    {
        SettingsButton.IsChecked = false;
    }

    private void MainTab_Checked(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded)
        {
            return;
        }

        if (SettingsButton.IsChecked == true)
        {
            SettingsButton.IsChecked = false;
        }

        UpdateActivePanelVisibility();
        ClearEqBandSelection();
    }

    private void UpdateActivePanelVisibility()
    {
        var settingsOpen = SettingsButton.IsChecked == true;
        MainTabNavBorder.Visibility = settingsOpen ? Visibility.Collapsed : Visibility.Visible;
        SettingsTabPanel.Visibility = settingsOpen ? Visibility.Visible : Visibility.Collapsed;
        MixerTabPanel.Visibility = !settingsOpen && TabMixerBtn.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        EqTabPanel.Visibility = !settingsOpen && TabEqBtn.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        DspTabPanel.Visibility = !settingsOpen && TabDspBtn.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OpenChannelEq_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: ChannelMixerItem item } || !item.HasAdvancedSettings)
        {
            return;
        }

        SelectAdvancedChannel(item.Key);
        TabEqBtn.IsChecked = true;
    }

    private void AdvancedChannel_Checked(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded || _isUpdatingUi)
        {
            return;
        }

        if (sender is RadioButton { Tag: string channelKey })
        {
            SelectAdvancedChannel(channelKey);
        }
    }

    private void SelectAdvancedChannel(string channelKey)
    {
        _selectedAdvancedChannel = channelKey;
        ClearEqBandSelection();

        RunUiUpdate(() =>
        {
            SyncChannelRadioGrid(EqChannelSelectorGrid, channelKey);
            SyncChannelRadioGrid(DspChannelSelectorGrid, channelKey);
            LoadAdvancedChannelUi(channelKey);
        });
    }

    private static void SyncChannelRadioGrid(Panel panel, string channelKey)
    {
        foreach (var child in panel.Children)
        {
            if (child is RadioButton rb && rb.Tag is string tag)
            {
                rb.IsChecked = string.Equals(tag, channelKey, StringComparison.Ordinal);
            }
        }
    }

    private void UpdateMixerChannelOrderAndTabs(SonarState state)
    {
        var sorted = _channels
            .OrderBy(c => c.IsChannelEnabled ? 0 : 1)
            .ThenBy(c => c.DefaultOrder)
            .ToList();
        ChannelsItemsControl.ItemsSource = sorted;

        UpdateChannelTabVisibility(EqChannelSelectorGrid, state.EnabledVadChannels);
        UpdateChannelTabVisibility(DspChannelSelectorGrid, state.EnabledVadChannels);

        if (_selectedAdvancedChannel is SonarChannel.Media or SonarChannel.Aux &&
            !state.EnabledVadChannels.Contains(_selectedAdvancedChannel))
        {
            _selectedAdvancedChannel = SonarChannel.Game;
        }

        SyncChannelRadioGrid(EqChannelSelectorGrid, _selectedAdvancedChannel);
        SyncChannelRadioGrid(DspChannelSelectorGrid, _selectedAdvancedChannel);
    }

    private static void UpdateChannelTabVisibility(Panel panel, HashSet<string> enabledVadChannels)
    {
        foreach (var child in panel.Children)
        {
            if (child is RadioButton rb && rb.Tag is string tag &&
                tag is SonarChannel.Media or SonarChannel.Aux)
            {
                rb.Visibility = enabledVadChannels.Contains(tag)
                    ? Visibility.Visible
                    : Visibility.Collapsed;
            }
        }
    }

    private void LoadAdvancedChannelUi(string channelKey)
    {
        var configs = _latestState?.ConfigsByChannel.GetValueOrDefault(channelKey, []) ?? [];
        ChannelDspConfig? dsp = null;
        _latestState?.SelectedConfigs.TryGetValue(channelKey, out dsp);

        EqPresetCombo.ItemsSource = configs;
        EqPresetCombo.SelectedValue = dsp?.ConfigId;
        EqEnabledToggle.IsChecked = dsp?.EqEnabled ?? false;

        for (var i = 0; i < _eqBands.Count; i++)
        {
            var vm = _eqBands[i];
            var src = dsp?.EqBands.FirstOrDefault(b => b.Index == vm.Index);
            if (src is not null)
            {
                vm.Enabled = src.Enabled;
                vm.Frequency = src.Frequency;
                vm.Gain = src.Gain;
                vm.QFactor = src.QFactor;
                vm.Type = src.Type;
            }
            else
            {
                vm.Enabled = i < 5;
                vm.Gain = 0.0;
            }

            vm.IsSelected = vm.Index == _selectedBandIndex;
        }

        LoadSelectedBandInspector();

        DspActivePresetLabel.Text = dsp?.ConfigName ?? "Custom";

        if (SonarChannel.IsVoiceChannel(channelKey))
        {
            RenderDspPanel.Visibility = Visibility.Collapsed;
            VoiceDspPanel.Visibility = Visibility.Visible;

            AiNoiseCancelToggle.IsChecked = dsp?.AiNoiseCancelingEnabled ?? false;
            AiNoiseCancelSlider.Value = ChannelDspConfig.RawToUiAiNoise(dsp?.AiNoiseCancelingValue ?? 0.9);
            NoiseReductionToggle.IsChecked = dsp?.NoiseReductionEnabled ?? false;
            NoiseReductionSlider.Value = dsp?.NoiseReductionValue ?? 0.0;
            ImpactNoiseToggle.IsChecked = dsp?.ImpactNoiseReductionEnabled ?? false;
            ImpactNoiseSlider.Value = dsp?.ImpactNoiseReductionValue ?? 0.0;
            VolumeStabilizerToggle.IsChecked = dsp?.VolumeStabilizerEnabled ?? false;
            VolumeStabilizerSlider.Value = dsp?.VolumeStabilizerValue ?? 0.0;
            NoiseGateToggle.IsChecked = dsp?.NoiseGateEnabled ?? false;
            AutoNoiseGateToggle.IsChecked = dsp?.AutoNoiseGateEnabled ?? false;
            NoiseGateSlider.Value = Math.Clamp(dsp?.NoiseGateValue ?? -60.0, -60.0, -10.0);

            UpdateVoiceDspInteractiveStates();
        }
        else
        {
            RenderDspPanel.Visibility = Visibility.Visible;
            VoiceDspPanel.Visibility = Visibility.Collapsed;

            SpatialToggle.IsChecked = dsp?.VirtualSurroundEnabled ?? false;
            FormFactorCombo.SelectedValue = dsp?.FormFactor ?? "headphones";
            ReverbSlider.Value = dsp?.ReverbGainDb ?? -6.0;
            BassBoostToggle.IsChecked = dsp?.BassBoostEnabled ?? false;
            BassBoostSlider.Value = dsp?.BassBoostValue ?? 0.0;
            TrebleBoostToggle.IsChecked = dsp?.TrebleBoostEnabled ?? false;
            TrebleBoostSlider.Value = dsp?.TrebleBoostValue ?? 0.0;
            VoiceClarityToggle.IsChecked = dsp?.VoiceClarityEnabled ?? false;
            VoiceClaritySlider.Value = dsp?.VoiceClarityValue ?? 0.0;
            GeneralGainSlider.Value = dsp?.GeneralGain ?? 0.0;
            SmartVolumeToggle.IsChecked = dsp?.SmartVolumeEnabled ?? false;
            SmartVolumeLoudnessCombo.SelectedValue = dsp?.SmartVolumeLoudness ?? "balanced";
            SmartVolumeSlider.Value = dsp?.SmartVolumeLevel ?? 0.0;
        }

        UpdateDspValueLabels();
    }

    private void LoadSelectedBandInspector()
    {
        if (_selectedBandIndex is not int selectedIndex)
        {
            EqInspectorCard.Visibility = Visibility.Collapsed;
            return;
        }

        var band = _eqBands.FirstOrDefault(b => b.Index == selectedIndex);
        if (band is null)
        {
            EqInspectorCard.Visibility = Visibility.Collapsed;
            return;
        }

        EqInspectorCard.Visibility = Visibility.Visible;
        SelectedBandTitleText.Text = LocalizationManager.Format("Str_EqBandTitleFormat", band.Index);
        EqBandFreqBox.Text = band.Frequency.ToString("0.##", CultureInfo.InvariantCulture);
        EqBandQBox.Text = band.QFactor.ToString("0.###", CultureInfo.InvariantCulture);
        EqBandTypeCombo.SelectedValue = band.Type;
    }

    private void ClearEqBandSelection()
    {
        if (_selectedBandIndex is null && _eqBands.All(b => !b.IsSelected))
        {
            EqInspectorCard.Visibility = Visibility.Collapsed;
            return;
        }

        _selectedBandIndex = null;
        foreach (var b in _eqBands)
        {
            b.IsSelected = false;
        }

        EqInspectorCard.Visibility = Visibility.Collapsed;
    }

    private void CloseBandInspector_Click(object sender, RoutedEventArgs e)
    {
        Keyboard.ClearFocus();
        ClearEqBandSelection();
    }

    private void Window_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (_selectedBandIndex is null || _openDropDownCount > 0)
        {
            return;
        }

        if (IsDescendantOfEqBandOrInspector(e.OriginalSource as DependencyObject))
        {
            return;
        }

        Keyboard.ClearFocus();
        ClearEqBandSelection();
    }

    private bool IsDescendantOfEqBandOrInspector(DependencyObject? source)
    {
        var current = source;
        while (current is not null)
        {
            if (ReferenceEquals(current, EqInspectorCard) ||
                current is FrameworkElement { DataContext: EqBandViewModel })
            {
                return true;
            }

            current = current is Visual or System.Windows.Media.Media3D.Visual3D
                ? VisualTreeHelper.GetParent(current)
                : LogicalTreeHelper.GetParent(current);
        }

        return false;
    }

    private void SelectEqBand(int bandIndex)
    {
        if (_selectedBandIndex == bandIndex)
        {
            return;
        }

        _selectedBandIndex = bandIndex;
        foreach (var b in _eqBands)
        {
            b.IsSelected = b.Index == bandIndex;
        }

        RunUiUpdate(LoadSelectedBandInspector);
    }

    private async void MuteButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: ChannelMixerItem item })
        {
            return;
        }

        var targetMuted = !item.Muted;
        item.Muted = targetMuted;
        MarkUserModified(item.Key);

        var success = await ExecuteSafeSendAsync(() => _sonarClient.SetMuteAsync(item.Key, targetMuted));
        if (!success)
        {
            item.Muted = !targetMuted;
        }
    }

    private async void ChannelEnableToggle_Click(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded || _isUpdatingUi || sender is not CheckBox { DataContext: ChannelMixerItem item })
        {
            return;
        }

        var targetEnabled = item.IsChannelEnabled;
        var success = await ExecuteSafeSendAsync(() => _sonarClient.SetChannelEnabledAsync(item.Key, targetEnabled));
        if (!success)
        {
            RunUiUpdate(() => item.IsChannelEnabled = !targetEnabled);
            return;
        }

        await Task.Delay(250);
        await RefreshStateAsync();
    }

    private async void ResetChannelProfile_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: ChannelMixerItem item })
        {
            await ApplyDspResetAndSaveAsync(item.Key, cfg => cfg.ResetAllToDefault());
        }
    }

    private async void ResetActiveChannelProfile_Click(object sender, RoutedEventArgs e)
    {
        await ApplyDspResetAndSaveAsync(_selectedAdvancedChannel, cfg => cfg.ResetAllToDefault());
    }

    private async void EqResetDefaultButton_Click(object sender, RoutedEventArgs e)
    {
        await ApplyDspResetAndSaveAsync(_selectedAdvancedChannel, cfg => cfg.ResetEqToDefault());
    }

    private async void ResetSingleEffect_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string effectKey })
        {
            await ApplyDspResetAndSaveAsync(_selectedAdvancedChannel, cfg => cfg.ResetEffectToDefault(effectKey));
        }
    }

    private async void DspSlider_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount != 2 || sender is not Slider { Tag: string effectKey })
        {
            return;
        }

        e.Handled = true;
        await ApplyDspResetAndSaveAsync(_selectedAdvancedChannel, cfg => cfg.ResetEffectToDefault(effectKey));
    }

    private async Task ApplyDspResetAndSaveAsync(string channelKey, Func<ChannelDspConfig, bool> resetAction)
    {
        if (_latestState is null ||
            !_latestState.SelectedConfigs.TryGetValue(channelKey, out var dsp))
        {
            return;
        }

        if (!resetAction(dsp))
        {
            return;
        }

        CancelAndStampThrottle($"dsp:{dsp.VirtualAudioDevice}");

        if (string.Equals(channelKey, _selectedAdvancedChannel, StringComparison.Ordinal))
        {
            RunUiUpdate(() => LoadAdvancedChannelUi(channelKey));
        }

        await ExecuteSafeSendAsync(() => _sonarClient.UpdateConfigAsync(dsp));
    }

    private void ChannelSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_isUpdatingUi || sender is not Slider { DataContext: ChannelMixerItem item })
        {
            return;
        }

        var volume = e.NewValue;
        _ = ScheduleThrottledSendAsync(item.Key, () => _sonarClient.SetVolumeAsync(item.Key, volume));
    }

    private async void ChannelDeviceCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded || _isUpdatingUi)
        {
            return;
        }

        if (sender is not ComboBox { DataContext: ChannelMixerItem item, SelectedValue: string deviceId })
        {
            return;
        }

        item.SelectedDeviceId = deviceId;
        if (await ExecuteSafeSendAsync(() => _sonarClient.SetChannelDeviceAsync(item.Key, deviceId)))
        {
            await PollLiveMixerSnapshotAsync(fullSnapshot: true);
        }
    }

    private async void ChannelPresetCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded || _isUpdatingUi)
        {
            return;
        }

        if (sender is not ComboBox { DataContext: ChannelMixerItem item, SelectedValue: string configId })
        {
            return;
        }

        item.SelectedConfigId = configId;
        await SelectPresetAndRefreshAsync(configId);
    }

    private async void EqPresetCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded || _isUpdatingUi)
        {
            return;
        }

        if (EqPresetCombo.SelectedValue is string configId)
        {
            ClearEqBandSelection();
            await SelectPresetAndRefreshAsync(configId);
        }
    }

    private async Task SelectPresetAndRefreshAsync(string configId)
    {
        if (await ExecuteSafeSendAsync(() => _sonarClient.SelectConfigAsync(configId)))
        {
            await RefreshStateAsync();
        }
    }

    private async void SyncRenderDevicesButton_Click(object sender, RoutedEventArgs e)
    {
        if (!_channelMap.TryGetValue(SonarChannel.Game, out var gameItem) ||
            string.IsNullOrWhiteSpace(gameItem.SelectedDeviceId))
        {
            return;
        }

        if (await ExecuteSafeSendAsync(() => _sonarClient.SetAllRenderDevicesAsync(gameItem.SelectedDeviceId)))
        {
            await RefreshStateAsync();
        }
    }

    private void ChatMixSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        UpdateChatMixStatusText(e.NewValue, _latestState?.ChatMixState ?? "enabled");

        if (_isUpdatingUi || !ChatMixSlider.IsEnabled)
        {
            return;
        }

        var balance = e.NewValue;
        _ = ScheduleThrottledSendAsync(ChatMixThrottleKey, () => _sonarClient.SetChatMixAsync(balance));
    }

    private async void ChatMixSlider_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (!ChatMixSlider.IsEnabled || e.ClickCount != 2)
        {
            return;
        }

        e.Handled = true;
        await ResetChatMixAsync();
    }

    private async Task ResetChatMixAsync()
    {
        CancelAndStampThrottle(ChatMixThrottleKey);

        RunUiUpdate(() =>
        {
            ChatMixSlider.Value = 0.0;
            UpdateChatMixStatusText(0.0, _latestState?.ChatMixState ?? "enabled");
        });

        await ExecuteSafeSendAsync(() => _sonarClient.SetChatMixAsync(0.0));
    }

    private void EqBandCard_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: EqBandViewModel band })
        {
            SelectEqBand(band.Index);
        }
    }

    private void EqBandSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!IsLoaded || _isUpdatingUi)
        {
            return;
        }

        if (sender is Slider { DataContext: EqBandViewModel band })
        {
            SelectEqBand(band.Index);
        }

        CommitCurrentDspConfig(immediate: false);
    }

    private void EqBandSlider_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not Slider { DataContext: EqBandViewModel band })
        {
            return;
        }

        SelectEqBand(band.Index);

        if (e.ClickCount != 2)
        {
            return;
        }

        e.Handled = true;
        RunUiUpdate(() => band.Gain = 0.0);

        CommitCurrentDspConfig(immediate: true);
    }

    private void EqBandEnable_Click(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded || _isUpdatingUi)
        {
            return;
        }

        if (sender is FrameworkElement { DataContext: EqBandViewModel band })
        {
            SelectEqBand(band.Index);
        }

        CommitCurrentDspConfig(immediate: true);
    }

    private void EqEnabledToggle_Click(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded || _isUpdatingUi)
        {
            return;
        }

        if (EqEnabledToggle.IsChecked == true && !SonarChannel.IsVoiceChannel(_selectedAdvancedChannel))
        {
            RunUiUpdate(() =>
            {
                BassBoostToggle.IsChecked = true;
                TrebleBoostToggle.IsChecked = true;
                VoiceClarityToggle.IsChecked = true;
            });
        }

        CommitCurrentDspConfig(immediate: true);
    }

    private void EqFlattenButton_Click(object sender, RoutedEventArgs e)
    {
        RunUiUpdate(() =>
        {
            foreach (var band in _eqBands)
            {
                band.Gain = 0.0;
            }
        });

        CommitCurrentDspConfig(immediate: true);
    }

    private void EqInspectorBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            ApplyEqInspectorFields();
            Keyboard.ClearFocus();
        }
    }

    private void EqInspectorBox_LostFocus(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded || _isUpdatingUi)
        {
            return;
        }

        ApplyEqInspectorFields();
    }

    private void EqBandTypeCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded || _isUpdatingUi)
        {
            return;
        }

        if (EqBandTypeCombo.SelectedValue is string filterType)
        {
            var band = _eqBands.FirstOrDefault(b => b.Index == _selectedBandIndex);
            if (band is not null)
            {
                band.Type = filterType;
                CommitCurrentDspConfig(immediate: true);
            }
        }
    }

    private void ApplyEqInspectorFields()
    {
        var band = _eqBands.FirstOrDefault(b => b.Index == _selectedBandIndex);
        if (band is null)
        {
            return;
        }

        var changed = false;

        var freqText = EqBandFreqBox.Text.Replace(',', '.');
        if (double.TryParse(freqText, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsedFreq))
        {
            var clampedFreq = Math.Clamp(parsedFreq, 20.0, 20000.0);
            if (Math.Abs(band.Frequency - clampedFreq) > 0.01)
            {
                band.Frequency = clampedFreq;
                changed = true;
            }
        }

        var qText = EqBandQBox.Text.Replace(',', '.');
        if (double.TryParse(qText, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsedQ))
        {
            var clampedQ = Math.Clamp(parsedQ, 0.1, 10.0);
            if (Math.Abs(band.QFactor - clampedQ) > 0.001)
            {
                band.QFactor = clampedQ;
                changed = true;
            }
        }

        RunUiUpdate(() =>
        {
            EqBandFreqBox.Text = band.Frequency.ToString("0.##", CultureInfo.InvariantCulture);
            EqBandQBox.Text = band.QFactor.ToString("0.###", CultureInfo.InvariantCulture);
        });

        if (changed)
        {
            CommitCurrentDspConfig(immediate: true);
        }
    }

    private void DspControl_Changed(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded || _isUpdatingUi)
        {
            return;
        }

        UpdateVoiceDspInteractiveStates();
        CommitCurrentDspConfig(immediate: true);
    }

    private void UpdateVoiceDspInteractiveStates()
    {
        var aiActive = AiNoiseCancelToggle.IsChecked == true;
        NoiseReductionDisabledHint.Visibility = aiActive ? Visibility.Visible : Visibility.Collapsed;
        NoiseReductionControlsPanel.IsEnabled = !aiActive;
        NoiseReductionControlsPanel.Opacity = aiActive ? 0.42 : 1.0;

        var autoGate = AutoNoiseGateToggle.IsChecked == true;
        NoiseGateSlider.IsEnabled = !autoGate;
        NoiseGateSlider.Opacity = autoGate ? 0.42 : 1.0;
    }

    private void DspCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded || _isUpdatingUi)
        {
            return;
        }

        CommitCurrentDspConfig(immediate: true);
    }

    private void DspSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!IsLoaded)
        {
            return;
        }

        UpdateDspValueLabels();

        if (_isUpdatingUi)
        {
            return;
        }

        CommitCurrentDspConfig(immediate: false);
    }

    private void UpdateDspValueLabels()
    {
        ReverbValueText.Text = $"{ReverbSlider.Value.ToString("0.0", CultureInfo.InvariantCulture)} dB";
        BassBoostValueText.Text = FormatSignedDb(BassBoostSlider.Value);
        TrebleBoostValueText.Text = FormatSignedDb(TrebleBoostSlider.Value);
        VoiceClarityValueText.Text = FormatSignedDb(VoiceClaritySlider.Value);
        GeneralGainValueText.Text = FormatSignedDb(GeneralGainSlider.Value);
        SmartVolumeValueText.Text = SmartVolumeSlider.Value.ToString("0.00", CultureInfo.InvariantCulture);

        AiNoiseCancelValueText.Text = $"{Math.Round(AiNoiseCancelSlider.Value):0}";
        NoiseReductionValueText.Text = NoiseReductionSlider.Value.ToString("0.00", CultureInfo.InvariantCulture);
        ImpactNoiseValueText.Text = ImpactNoiseSlider.Value.ToString("0.00", CultureInfo.InvariantCulture);
        VolumeStabilizerValueText.Text = VolumeStabilizerSlider.Value.ToString("0.00", CultureInfo.InvariantCulture);
        NoiseGateValueText.Text = $"{NoiseGateSlider.Value.ToString("0.0", CultureInfo.InvariantCulture)} dB";
    }

    private static string FormatSignedDb(double val)
    {
        var formatted = val.ToString("0.0", CultureInfo.InvariantCulture);
        return val > 0.05 ? $"+{formatted} dB" : $"{formatted} dB";
    }

    private void CommitCurrentDspConfig(bool immediate)
    {
        if (_latestState is null ||
            !_latestState.SelectedConfigs.TryGetValue(_selectedAdvancedChannel, out var dsp))
        {
            return;
        }

        dsp.EqEnabled = EqEnabledToggle.IsChecked == true;

        foreach (var vm in _eqBands)
        {
            var target = dsp.EqBands.FirstOrDefault(b => b.Index == vm.Index);
            if (target is null)
            {
                target = new EqBandSetting { Index = vm.Index };
                dsp.EqBands.Add(target);
            }

            target.Enabled = vm.Enabled;
            target.Frequency = vm.Frequency;
            target.Gain = vm.Gain;
            target.QFactor = vm.QFactor;
            target.Type = vm.Type;
        }

        if (dsp.IsVoiceChannel)
        {
            dsp.AiNoiseCancelingEnabled = AiNoiseCancelToggle.IsChecked == true;
            dsp.AiNoiseCancelingValue = ChannelDspConfig.UiToRawAiNoise(AiNoiseCancelSlider.Value);
            dsp.NoiseReductionEnabled = NoiseReductionToggle.IsChecked == true;
            dsp.NoiseReductionValue = NoiseReductionSlider.Value;
            dsp.ImpactNoiseReductionEnabled = ImpactNoiseToggle.IsChecked == true;
            dsp.ImpactNoiseReductionValue = ImpactNoiseSlider.Value;
            dsp.VolumeStabilizerEnabled = VolumeStabilizerToggle.IsChecked == true;
            dsp.VolumeStabilizerValue = VolumeStabilizerSlider.Value;
            dsp.NoiseGateEnabled = NoiseGateToggle.IsChecked == true;
            dsp.AutoNoiseGateEnabled = AutoNoiseGateToggle.IsChecked == true;
            dsp.NoiseGateValue = NoiseGateSlider.Value;
        }
        else
        {
            dsp.VirtualSurroundEnabled = SpatialToggle.IsChecked == true;
            dsp.FormFactor = FormFactorCombo.SelectedValue as string ?? "headphones";
            dsp.ReverbGainDb = ReverbSlider.Value;
            dsp.BassBoostEnabled = BassBoostToggle.IsChecked == true;
            dsp.BassBoostValue = BassBoostSlider.Value;
            dsp.TrebleBoostEnabled = TrebleBoostToggle.IsChecked == true;
            dsp.TrebleBoostValue = TrebleBoostSlider.Value;
            dsp.VoiceClarityEnabled = VoiceClarityToggle.IsChecked == true;
            dsp.VoiceClarityValue = VoiceClaritySlider.Value;
            dsp.GeneralGain = GeneralGainSlider.Value;
            dsp.SmartVolumeEnabled = SmartVolumeToggle.IsChecked == true;
            dsp.SmartVolumeLoudness = SmartVolumeLoudnessCombo.SelectedValue as string ?? "balanced";
            dsp.SmartVolumeLevel = SmartVolumeSlider.Value;
        }

        var throttleKey = $"dsp:{dsp.VirtualAudioDevice}";
        if (immediate)
        {
            CancelAndStampThrottle(throttleKey);
            _ = ExecuteSafeSendAsync(() => _sonarClient.UpdateConfigAsync(dsp));
        }
        else
        {
            _ = ScheduleThrottledSendAsync(throttleKey, () => _sonarClient.UpdateConfigAsync(dsp));
        }
    }

    private async Task ScheduleThrottledSendAsync(string key, Func<Task> sendAction)
    {
        if (!_throttleEntries.TryGetValue(key, out var entry))
        {
            entry = new ThrottleEntry();
            _throttleEntries[key] = entry;
        }

        entry.CancelAndClearPending();

        var now = Environment.TickCount64;
        var elapsed = now - entry.LastSentTickMs;

        if (elapsed >= ThrottleIntervalMs)
        {
            entry.LastSentTickMs = now;
            await ExecuteSafeSendAsync(sendAction);
            return;
        }

        var remainingMs = (int)(ThrottleIntervalMs - elapsed);
        var cts = new CancellationTokenSource();
        entry.PendingCts = cts;

        try
        {
            await Task.Delay(remainingMs, cts.Token);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        if (!ReferenceEquals(entry.PendingCts, cts))
        {
            return;
        }

        entry.PendingCts = null;
        cts.Dispose();
        entry.LastSentTickMs = Environment.TickCount64;
        await ExecuteSafeSendAsync(sendAction);
    }

    private async Task<bool> ExecuteSafeSendAsync(Func<Task> sendAction)
    {
        try
        {
            await sendAction();
            var modeLabel = LocalizationManager.Get(_sonarClient.IsStandaloneMode
                ? "Str_StatusConnectedStandalone"
                : "Str_StatusConnectedGg");
            SetConnectionStatus(true, modeLabel);
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
        catch (Exception ex)
        {
            SetConnectionStatus(false, ex.Message);
            return false;
        }
    }

    private void SetConnectionStatus(bool? isConnected, string message)
    {
        StatusDot.Fill = isConnected switch
        {
            true => ConnectedDotBrush,
            false => DisconnectedDotBrush,
            null => ConnectingDotBrush
        };
        StatusText.Text = message;
        StatusText.ToolTip = message;
    }

    private void UpdateChatMixStatusText(double balance, string chatMixState)
    {
        var isEnabled = string.Equals(chatMixState, "enabled", StringComparison.OrdinalIgnoreCase);
        ChatMixSlider.IsEnabled = isEnabled;
        ChatMixStatusText.Foreground = isEnabled ? ChatMixActiveTextBrush : ChatMixDisabledTextBrush;

        if (!isEnabled)
        {
            ChatMixDisabledBanner.Visibility = Visibility.Visible;
            ChatMixFooterHintText.Text = LocalizationManager.Get("Str_ChatMixFooterDisabled");

            if (string.Equals(chatMixState, "finiteWheel", StringComparison.OrdinalIgnoreCase))
            {
                ChatMixStatusText.Text = FormatChatMixBalanceLabel(balance, prefix: LocalizationManager.Get("Str_ChatMixHardwarePrefix"));
                ChatMixDisabledReasonText.Text = LocalizationManager.Get("Str_ChatMixReasonHardware");
            }
            else if (string.Equals(chatMixState, "differentDeviceSelected", StringComparison.OrdinalIgnoreCase))
            {
                ChatMixStatusText.Text = LocalizationManager.Get("Str_ChatMixDifferentDevice");
                ChatMixDisabledReasonText.Text = LocalizationManager.Get("Str_ChatMixReasonDifferentDevice");
            }
            else
            {
                ChatMixStatusText.Text = LocalizationManager.Get("Str_ChatMixNoDevice");
                ChatMixDisabledReasonText.Text = LocalizationManager.Get("Str_ChatMixReasonNoDevice");
            }

            return;
        }

        ChatMixDisabledBanner.Visibility = Visibility.Collapsed;
        ChatMixFooterHintText.Text = LocalizationManager.Get("Str_ChatMixFooterReset");
        ChatMixStatusText.Text = FormatChatMixBalanceLabel(balance, prefix: null);
    }

    private static string FormatChatMixBalanceLabel(double balance, string? prefix)
    {
        if (Math.Abs(balance) < 0.01)
        {
            return prefix is null
                ? LocalizationManager.Get("Str_ChatMixBalanced")
                : LocalizationManager.Get("Str_ChatMixHardwareControl");
        }

        var percent = Math.Round(Math.Abs(balance) * 100);
        var sideText = balance < 0 ? $"Game {percent:0}%" : $"Chat {percent:0}%";
        return prefix is null ? sideText : $"{prefix} • {sideText}";
    }

    private void CancelAndStampThrottle(string key)
    {
        if (_throttleEntries.TryGetValue(key, out var entry))
        {
            entry.CancelAndClearPending();
        }

        MarkUserModified(key);
    }

    private void RunUiUpdate(Action updateAction)
    {
        var prevUpdating = _isUpdatingUi;
        _isUpdatingUi = true;
        try
        {
            updateAction();
        }
        finally
        {
            _isUpdatingUi = prevUpdating;
        }
    }

    internal static SolidColorBrush CreateFrozenBrush(byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
        brush.Freeze();
        return brush;
    }

    private sealed class ThrottleEntry
    {
        public long LastSentTickMs;
        public CancellationTokenSource? PendingCts;

        public void CancelAndClearPending()
        {
            if (PendingCts is null)
            {
                return;
            }

            PendingCts.Cancel();
            PendingCts.Dispose();
            PendingCts = null;
        }
    }
}
