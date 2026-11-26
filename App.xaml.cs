using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Windows;
using Drawing = System.Drawing;
using WinForms = System.Windows.Forms;

namespace SonarTray;

public partial class App : Application
{
    private const string SingleInstanceMutexName = @"Local\SonarTray_SingleInstance_Mutex";

    private Mutex? _singleInstanceMutex;
    private SonarHostService? _hostService;
    private SonarClient? _sonarClient;
    private MainWindow? _mainWindow;
    private WinForms.NotifyIcon? _notifyIcon;
    private WinForms.ContextMenuStrip? _contextMenu;
    private WinForms.ToolStripMenuItem? _showHideMenuItem;
    private WinForms.ToolStripMenuItem? _settingsMenuItem;
    private WinForms.ToolStripMenuItem? _refreshMenuItem;
    private WinForms.ToolStripMenuItem? _exitMenuItem;
    private Drawing.Icon? _trayIcon;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        _singleInstanceMutex = new Mutex(initiallyOwned: true, SingleInstanceMutexName, out var createdNew);
        if (!createdNew)
        {
            _singleInstanceMutex.Dispose();
            _singleInstanceMutex = null;
            Shutdown();
            return;
        }

        LocalizationManager.Initialize();

        _hostService = new SonarHostService();
        _sonarClient = new SonarClient(_hostService);
        _mainWindow = new MainWindow(_sonarClient, RestartSonarEngineAsync);
        _trayIcon = CreateSonarIcon();

        _showHideMenuItem = new WinForms.ToolStripMenuItem();
        _showHideMenuItem.Click += (_, _) => _mainWindow?.ShowAtTray();

        _settingsMenuItem = new WinForms.ToolStripMenuItem();
        _settingsMenuItem.Click += (_, _) => _mainWindow?.OpenSettingsView();

        _refreshMenuItem = new WinForms.ToolStripMenuItem();
        _refreshMenuItem.Click += (_, _) =>
        {
            if (_mainWindow is not null)
            {
                _ = _mainWindow.RefreshStateAsync(forceReloadConfigs: true);
            }
        };

        _exitMenuItem = new WinForms.ToolStripMenuItem();
        _exitMenuItem.Click += (_, _) => Shutdown();

        _contextMenu = new WinForms.ContextMenuStrip();
        _contextMenu.Items.Add(_showHideMenuItem);
        _contextMenu.Items.Add(_settingsMenuItem);
        _contextMenu.Items.Add(_refreshMenuItem);
        _contextMenu.Items.Add(new WinForms.ToolStripSeparator());
        _contextMenu.Items.Add(_exitMenuItem);

        UpdateTrayMenuState();
        LocalizationManager.LanguageChanged += UpdateTrayMenuState;
        _mainWindow.EngineConnectingChanged += OnEngineConnectingChanged;

        _notifyIcon = new WinForms.NotifyIcon
        {
            Icon = _trayIcon,
            Text = "SonarTray",
            ContextMenuStrip = _contextMenu,
            Visible = true
        };
        _notifyIcon.MouseClick += OnTrayIconMouseClick;

        _ = _mainWindow.RefreshStateAsync();
    }

    private void OnEngineConnectingChanged(bool _) => UpdateTrayMenuState();

    private void UpdateTrayMenuState()
    {
        var isConnecting = _mainWindow?.IsEngineConnecting ?? false;

        if (_showHideMenuItem is not null)
        {
            _showHideMenuItem.Text = LocalizationManager.Get("Str_TrayShowHide");
        }

        if (_settingsMenuItem is not null)
        {
            _settingsMenuItem.Text = LocalizationManager.Get("Str_TraySettings");
            _settingsMenuItem.Enabled = !isConnecting;
        }

        if (_refreshMenuItem is not null)
        {
            _refreshMenuItem.Text = LocalizationManager.Get("Str_TrayRefresh");
            _refreshMenuItem.Enabled = !isConnecting;
        }

        if (_exitMenuItem is not null)
        {
            _exitMenuItem.Text = LocalizationManager.Get(isConnecting ? "Str_TrayExitBusy" : "Str_TrayExit");
            _exitMenuItem.Enabled = !isConnecting;
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        LocalizationManager.LanguageChanged -= UpdateTrayMenuState;
        if (_mainWindow is not null)
        {
            _mainWindow.EngineConnectingChanged -= OnEngineConnectingChanged;
        }
        if (_notifyIcon is not null)
        {
            _notifyIcon.Visible = false;
            _notifyIcon.MouseClick -= OnTrayIconMouseClick;
            _notifyIcon.Dispose();
            _notifyIcon = null;
        }

        _contextMenu?.Dispose();
        _contextMenu = null;

        _trayIcon?.Dispose();
        _trayIcon = null;

        _mainWindow?.Cleanup();
        _mainWindow?.Close();
        _mainWindow = null;

        _sonarClient?.Dispose();
        _sonarClient = null;

        _hostService?.Dispose();
        _hostService = null;

        if (_singleInstanceMutex is not null)
        {
            try
            {
                _singleInstanceMutex.ReleaseMutex();
            }
            catch (ApplicationException)
            {
            }

            _singleInstanceMutex.Dispose();
            _singleInstanceMutex = null;
        }

        base.OnExit(e);
    }

    protected override void OnSessionEnding(SessionEndingCancelEventArgs e)
    {
        _hostService?.Dispose();
        _hostService = null;
        base.OnSessionEnding(e);
    }

    private async Task RestartSonarEngineAsync()
    {
        if (_hostService is null || _sonarClient is null || _mainWindow is null)
        {
            return;
        }

        _mainWindow.SetEngineConnecting(true);
        try
        {
            _sonarClient.InvalidateEndpoint();

            if (_sonarClient.IsStandaloneMode || !SonarHostService.IsSteelSeriesGgRunning())
            {
                await _hostService.RestartAsync();
            }

            await _mainWindow.RefreshStateAsync(forceReloadConfigs: true);
        }
        catch
        {
            await _mainWindow.RefreshStateAsync(forceReloadConfigs: true);
        }
        finally
        {
            _mainWindow.SetEngineConnecting(false);
        }
    }

    private void OnTrayIconMouseClick(object? sender, WinForms.MouseEventArgs e)
    {
        if (e.Button != WinForms.MouseButtons.Left || _mainWindow is null)
        {
            return;
        }

        if (_mainWindow.IsVisible)
        {
            _mainWindow.Hide();
            return;
        }

        if ((DateTime.UtcNow - _mainWindow.LastDeactivatedUtc).TotalMilliseconds < 250)
        {
            return;
        }

        _mainWindow.ShowAtTray();
    }

    private static Drawing.Icon CreateSonarIcon()
    {
        using var bitmap = new Drawing.Bitmap(64, 64, Drawing.Imaging.PixelFormat.Format32bppArgb);
        using var graphics = Drawing.Graphics.FromImage(bitmap);
        graphics.SmoothingMode = SmoothingMode.HighQuality;
        graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
        graphics.Clear(Drawing.Color.Transparent);

        using var bgBrush = new Drawing.SolidBrush(Drawing.Color.FromArgb(0xFF, 0x55, 0x00));
        using var bgPath = CreateRoundedRectPath(new Drawing.RectangleF(2f, 2f, 60f, 60f), 14f);
        graphics.FillPath(bgBrush, bgPath);

        const float cx = 32f;
        const float cy = 32f;
        const float centerRadius = 4.2f;

        using var whiteBrush = new Drawing.SolidBrush(Drawing.Color.White);
        graphics.FillEllipse(
            whiteBrush,
            cx - centerRadius,
            cy - centerRadius,
            centerRadius * 2f,
            centerRadius * 2f);

        using var arcPen = new Drawing.Pen(Drawing.Color.White, 4.8f)
        {
            StartCap = LineCap.Flat,
            EndCap = LineCap.Flat
        };

        const float innerRadius = 10.8f;
        var innerRect = new Drawing.RectangleF(
            cx - innerRadius,
            cy - innerRadius,
            innerRadius * 2f,
            innerRadius * 2f);
        graphics.DrawArc(arcPen, innerRect, 180f, 90f);
        graphics.DrawArc(arcPen, innerRect, 0f, 90f);

        const float outerRadius = 20.0f;
        var outerRect = new Drawing.RectangleF(
            cx - outerRadius,
            cy - outerRadius,
            outerRadius * 2f,
            outerRadius * 2f);
        graphics.DrawArc(arcPen, outerRect, 180f, 110f);
        graphics.DrawArc(arcPen, outerRect, 0f, 110f);

        var hIcon = bitmap.GetHicon();
        try
        {
            using var tempIcon = Drawing.Icon.FromHandle(hIcon);
            return (Drawing.Icon)tempIcon.Clone();
        }
        finally
        {
            DestroyIcon(hIcon);
        }
    }

    private static GraphicsPath CreateRoundedRectPath(Drawing.RectangleF bounds, float radius)
    {
        var diameter = radius * 2f;
        var path = new GraphicsPath();
        path.AddArc(bounds.X, bounds.Y, diameter, diameter, 180, 90);
        path.AddArc(bounds.Right - diameter, bounds.Y, diameter, diameter, 270, 90);
        path.AddArc(bounds.Right - diameter, bounds.Bottom - diameter, diameter, diameter, 0, 90);
        path.AddArc(bounds.X, bounds.Bottom - diameter, diameter, diameter, 90, 90);
        path.CloseFigure();
        return path;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyIcon(IntPtr hIcon);
}
