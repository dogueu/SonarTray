using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;

namespace SonarTray;

/// <summary>
/// Launches and hosts SteelSeriesSonar.exe in standalone mode with a minimal local GG IPC stub
/// so Sonar runs independently without the SteelSeries GG desktop application.
/// </summary>
public sealed class SonarHostService : IDisposable
{
    private readonly SemaphoreSlim _lifecycleLock = new(1, 1);
    private readonly List<string> _recentErrors = [];
    private readonly List<WebSocket> _eventingSockets = [];

    private HttpListener? _stubListener;
    private CancellationTokenSource? _stubCts;
    private Process? _sonarProcess;
    private int _stubPort;
    private int _sonarPort;
    private bool _disposed;

    public bool IsRunningStandalone => _sonarProcess is { HasExited: false } && _sonarPort > 0;

    public string? StandaloneBaseUrl => IsRunningStandalone ? $"http://127.0.0.1:{_sonarPort}" : null;

    public async Task EnsureRunningAsync(CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        await _lifecycleLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (IsRunningStandalone)
            {
                return;
            }

            StopInternal();

            var sonarDir = ResolveSonarInstallDirectory();
            var sonarExe = Path.Combine(sonarDir, "SteelSeriesSonar.exe");
            var appDataPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                "SteelSeries",
                "GG",
                "apps",
                "sonar");

            if (!Directory.Exists(appDataPath))
            {
                throw new DirectoryNotFoundException($"Sonar veri klasörü bulunamadı: {appDataPath}");
            }

            TerminateOrphanedSonarIfGgStopped();

            _stubPort = GetFreeLoopbackPort();
            _sonarPort = GetFreeLoopbackPort();
            while (_sonarPort == _stubPort)
            {
                _sonarPort = GetFreeLoopbackPort();
            }

            var certPem = CreateSelfSignedCertificatePem();
            StartStubServer(_stubPort);

            var startReadyTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            lock (_recentErrors)
            {
                _recentErrors.Clear();
            }

            var psi = new ProcessStartInfo
            {
                FileName = sonarExe,
                WorkingDirectory = sonarDir,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };

            psi.EnvironmentVariables["APP_DATA_PATH"] = appDataPath;
            psi.EnvironmentVariables["APP_INSTALL_PATH"] = sonarDir;
            psi.EnvironmentVariables["SOUNDSTAGE_ENCRYPTED_WEB_SERVER_ADDRESS"] = $"127.0.0.1:{_sonarPort}";
            psi.EnvironmentVariables["GG_SERVER_ADDRESS"] = $"http://127.0.0.1:{_stubPort}";
            psi.EnvironmentVariables["GG_WS_ENDPOINT"] = $"ws://127.0.0.1:{_stubPort}/eventing";
            psi.EnvironmentVariables["GG_CERT_TEXT"] = certPem;
            psi.EnvironmentVariables["GG_API_AUTH_TOKEN"] = "standalone-token";

            var process = new Process
            {
                StartInfo = psi,
                EnableRaisingEvents = true
            };

            process.OutputDataReceived += (_, e) =>
            {
                if (string.IsNullOrEmpty(e.Data))
                {
                    return;
                }

                if (e.Data.Contains("Sonar start complete", StringComparison.OrdinalIgnoreCase))
                {
                    startReadyTcs.TrySetResult();
                }
                else if (e.Data.Contains("[EROR]", StringComparison.OrdinalIgnoreCase))
                {
                    RecordError(e.Data);
                }
            };

            process.ErrorDataReceived += (_, e) =>
            {
                if (!string.IsNullOrEmpty(e.Data))
                {
                    RecordError(e.Data);
                }
            };

            process.Exited += (_, _) =>
            {
                startReadyTcs.TrySetResult();
            };

            if (!process.Start())
            {
                throw new InvalidOperationException("SteelSeriesSonar.exe başlatılamadı.");
            }

            _sonarProcess = process;
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            await WaitForSonarReadyAsync(process, startReadyTcs.Task, ct).ConfigureAwait(false);
        }
        finally
        {
            _lifecycleLock.Release();
        }
    }

    public async Task RestartAsync(CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        await _lifecycleLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            StopInternal();
        }
        finally
        {
            _lifecycleLock.Release();
        }

        await EnsureRunningAsync(ct).ConfigureAwait(false);
    }

    private async Task WaitForSonarReadyAsync(Process process, Task stdoutReadyTask, CancellationToken ct)
    {
        using var probeClient = new HttpClient { Timeout = TimeSpan.FromMilliseconds(900) };
        var deadline = DateTime.UtcNow.AddSeconds(10);

        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();

            if (process.HasExited)
            {
                string detail;
                lock (_recentErrors)
                {
                    detail = _recentErrors.Count > 0
                        ? string.Join(" | ", _recentErrors.TakeLast(2))
                        : $"Çıkış kodu: {process.ExitCode}";
                }

                StopInternal();
                throw new InvalidOperationException($"Bağımsız Sonar motoru beklenmedik şekilde kapandı ({detail}).");
            }

            if (stdoutReadyTask.IsCompleted)
            {
                try
                {
                    using var resp = await probeClient.GetAsync($"http://127.0.0.1:{_sonarPort}/mode", ct).ConfigureAwait(false);
                    if (resp.IsSuccessStatusCode)
                    {
                        return;
                    }
                }
                catch
                {
                    // Web server may still be binding right around startup completion.
                }
            }

            await Task.Delay(150, ct).ConfigureAwait(false);
        }

        // Fallback check if stdout signal was missed but HTTP endpoint is healthy.
        try
        {
            using var finalResp = await probeClient.GetAsync($"http://127.0.0.1:{_sonarPort}/mode", ct).ConfigureAwait(false);
            if (finalResp.IsSuccessStatusCode)
            {
                return;
            }
        }
        catch
        {
            // Handled below.
        }

        StopInternal();
        throw new TimeoutException("Bağımsız Sonar motoru 10 saniye içinde yanıt vermedi.");
    }

    private void StartStubServer(int port)
    {
        _stubCts = new CancellationTokenSource();
        var listener = new HttpListener();
        listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        listener.Start();
        _stubListener = listener;

        var token = _stubCts.Token;
        _ = Task.Run(() => RunStubAcceptLoopAsync(listener, token), token);
    }

    private async Task RunStubAcceptLoopAsync(HttpListener listener, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && listener.IsListening)
        {
            HttpListenerContext context;
            try
            {
                context = await listener.GetContextAsync().ConfigureAwait(false);
            }
            catch (HttpListenerException)
            {
                break;
            }
            catch (ObjectDisposedException)
            {
                break;
            }

            _ = Task.Run(() => HandleStubContextAsync(context, ct), ct);
        }
    }

    private async Task HandleStubContextAsync(HttpListenerContext context, CancellationToken ct)
    {
        try
        {
            if (context.Request.IsWebSocketRequest)
            {
                var wsContext = await context.AcceptWebSocketAsync(null).ConfigureAwait(false);
                using var ws = wsContext.WebSocket;
                lock (_eventingSockets)
                {
                    _eventingSockets.Add(ws);
                }

                try
                {
                    var buffer = new byte[4096];

                    while (!ct.IsCancellationRequested && ws.State == WebSocketState.Open)
                    {
                        var result = await ws.ReceiveAsync(buffer, ct).ConfigureAwait(false);
                        if (result.MessageType == WebSocketMessageType.Close)
                        {
                            await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "Closing", CancellationToken.None)
                                .ConfigureAwait(false);
                            break;
                        }
                    }
                }
                finally
                {
                    lock (_eventingSockets)
                    {
                        _eventingSockets.Remove(ws);
                    }
                }
                return;
            }

            using (var reader = new StreamReader(context.Request.InputStream, Encoding.UTF8, leaveOpen: false))
            {
                await reader.ReadToEndAsync(ct).ConfigureAwait(false);
            }

            var path = context.Request.Url?.AbsolutePath ?? "/";
            var payload = GetStubResponseJson(path);
            var bytes = Encoding.UTF8.GetBytes(payload);

            context.Response.StatusCode = 200;
            context.Response.ContentType = "application/json";
            context.Response.ContentLength64 = bytes.Length;
            await context.Response.OutputStream.WriteAsync(bytes, ct).ConfigureAwait(false);
            context.Response.Close();
        }
        catch
        {
            try
            {
                context.Response.Abort();
            }
            catch
            {
                // Ignore cleanup failure on aborted connections.
            }
        }
    }

    private static string GetStubResponseJson(string path)
    {
        if (path.Contains("healthCheck", StringComparison.OrdinalIgnoreCase))
        {
            return """{"healthCheck":{"status":true,"db_read":true}}""";
        }

        if (path.Contains("user", StringComparison.OrdinalIgnoreCase))
        {
            return """{"user":{"id":0,"name":"Standalone","username":"Standalone","loggedIn":false,"loginSkipped":true}}""";
        }

        if (path.Contains("subApps/register", StringComparison.OrdinalIgnoreCase) ||
            path.Contains("subApps/ready", StringComparison.OrdinalIgnoreCase))
        {
            return "{}";
        }

        if (path.Contains("subApps", StringComparison.OrdinalIgnoreCase))
        {
            return """{"subApps":{"engine":{"isRunning":false,"isEnabled":false,"isReady":false,"metadata":{"webServerAddress":"","encryptedWebServerAddress":""}}}}""";
        }

        if (path.Contains("locale", StringComparison.OrdinalIgnoreCase))
        {
            return """{"iso":"en","common":{},"soundstage":{}}""";
        }

        if (path.Contains("keyboardShortcuts", StringComparison.OrdinalIgnoreCase))
        {
            return "[]";
        }

        return "{}";
    }

    private static string ResolveSonarInstallDirectory()
    {
        string[] candidates =
        [
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "SteelSeries", "GG", "apps", "sonar"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "SteelSeries", "GG", "apps", "sonar")
        ];

        foreach (var dir in candidates)
        {
            if (File.Exists(Path.Combine(dir, "SteelSeriesSonar.exe")))
            {
                return dir;
            }
        }

        throw new FileNotFoundException("SteelSeriesSonar.exe kurulum dizininde bulunamadı. SteelSeries GG kurulu olmalıdır.");
    }

    public static bool IsSteelSeriesGgRunning() =>
        HasRunningProcess("SteelSeriesGG") || HasRunningProcess("SteelSeriesGGEZ");

    private static bool HasRunningProcess(string processName)
    {
        var procs = Process.GetProcessesByName(processName);
        var any = procs.Length > 0;
        foreach (var proc in procs)
        {
            proc.Dispose();
        }

        return any;
    }

    private static void TerminateOrphanedSonarIfGgStopped()
    {
        if (IsSteelSeriesGgRunning())
        {
            return;
        }

        foreach (var proc in Process.GetProcessesByName("SteelSeriesSonar"))
        {
            try
            {
                proc.Kill(entireProcessTree: true);
                proc.WaitForExit(2000);
            }
            catch
            {
                // Ignore if process already exited or lacks access.
            }
            finally
            {
                proc.Dispose();
            }
        }
    }

    private static int GetFreeLoopbackPort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    private static string CreateSelfSignedCertificatePem()
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest(
            "CN=127.0.0.1",
            rsa,
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1);

        using var cert = request.CreateSelfSigned(
            DateTimeOffset.UtcNow.AddDays(-1),
            DateTimeOffset.UtcNow.AddDays(365));

        return cert.ExportCertificatePem();
    }

    private void RecordError(string line)
    {
        lock (_recentErrors)
        {
            _recentErrors.Add(line);
            if (_recentErrors.Count > 10)
            {
                _recentErrors.RemoveAt(0);
            }
        }
    }

    private void SendGracefulShutdownSignal()
    {
        WebSocket[] sockets;
        lock (_eventingSockets)
        {
            sockets = [.. _eventingSockets];
        }

        if (sockets.Length == 0)
        {
            return;
        }

        var payload = Encoding.UTF8.GetBytes("""{"event":"EVENT_SHUTDOWN","data":{}}""");
        foreach (var ws in sockets)
        {
            try
            {
                if (ws.State == WebSocketState.Open)
                {
                    using var sendCts = new CancellationTokenSource(TimeSpan.FromMilliseconds(600));
                    ws.SendAsync(
                        new ArraySegment<byte>(payload),
                        WebSocketMessageType.Text,
                        endOfMessage: true,
                        sendCts.Token).GetAwaiter().GetResult();
                }
            }
            catch
            {
                // Best effort shutdown notification.
            }
        }
    }

    private void StopInternal()
    {
        if (_sonarProcess is not null)
        {
            try
            {
                if (!_sonarProcess.HasExited)
                {
                    SendGracefulShutdownSignal();
                    if (!_sonarProcess.WaitForExit(3500))
                    {
                        _sonarProcess.Kill(entireProcessTree: true);
                        _sonarProcess.WaitForExit(1500);
                    }
                }
            }
            catch
            {
                // Best effort termination.
            }
            finally
            {
                _sonarProcess.Dispose();
                _sonarProcess = null;
            }
        }

        _sonarPort = 0;

        if (_stubListener is not null)
        {
            try
            {
                _stubListener.Stop();
                _stubListener.Close();
            }
            catch
            {
                // Ignore listener stop errors.
            }
            finally
            {
                _stubListener = null;
            }
        }

        _stubCts?.Cancel();
        _stubCts?.Dispose();
        _stubCts = null;

        lock (_eventingSockets)
        {
            _eventingSockets.Clear();
        }

        _stubPort = 0;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        StopInternal();
        _lifecycleLock.Dispose();
    }
}
