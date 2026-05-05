using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System.Globalization;
using System.Threading.Channels;
using UmbraSync.MareConfiguration;

namespace UmbraSync.Services.Network;

public sealed class NetworkDiagnosticService : IHostedService, IDisposable
{
    private readonly ILogger<NetworkDiagnosticService> _logger;
    private readonly MareConfigService _configService;
    private readonly string _logFilesDirectory;
    private volatile Channel<string>? _channel;
    private CancellationTokenSource? _cts;
    private Task? _consumerTask;
    private StreamWriter? _writer;
    private string? _currentFilePath;
    private long _lastSendUtcTicks;
    private long _lastRecvUtcTicks;
    private NetworkEventListener? _eventListener;
    private bool _disposed;

    public NetworkDiagnosticService(ILogger<NetworkDiagnosticService> logger,
        MareConfigService configService,
        Dalamud.Plugin.IDalamudPluginInterface pluginInterface)
    {
        _logger = logger;
        _configService = configService;
        _logFilesDirectory = Path.Combine(pluginInterface.GetPluginConfigDirectory(), "NetworkDiag");
    }

    private static Channel<string> CreateChannel() => Channel.CreateUnbounded<string>(new UnboundedChannelOptions
    {
        SingleReader = true,
        SingleWriter = false,
        AllowSynchronousContinuations = false
    });

    public bool IsEnabled => _configService.Current.EnableNetworkDiagnosticLog;
    public string LogFilesDirectory => _logFilesDirectory;
    public string? CurrentFilePath => _currentFilePath;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (!IsEnabled) return Task.CompletedTask;
        StartLogging();
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        StopLogging();
        return Task.CompletedTask;
    }

    public void StartLogging()
    {
        if (_consumerTask is { IsCompleted: false }) return;

        try
        {
            Directory.CreateDirectory(_logFilesDirectory);
            string fileName = $"network-diag-{DateTime.UtcNow:yyyyMMdd-HHmmss}.log";
            _currentFilePath = Path.Combine(_logFilesDirectory, fileName);
            _writer = new StreamWriter(_currentFilePath, append: false)
            {
                AutoFlush = false
            };
            _writer.WriteLine($"=== UmbraSync Network Diagnostic Log ===");
            _writer.WriteLine($"Started: {DateTime.UtcNow:o}");
            _writer.WriteLine($"Process: {Environment.ProcessId} | OS: {Environment.OSVersion}");
            _writer.WriteLine($"=========================================");
            _channel = CreateChannel();
            _cts = new CancellationTokenSource();
            var ch = _channel;
            var token = _cts.Token;
            _consumerTask = Task.Run(() => ConsumeLoopAsync(ch, token));

            _eventListener = new NetworkEventListener(this);

            _logger.LogInformation("NetworkDiagnosticService started. File: {Path}", _currentFilePath);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to start NetworkDiagnosticService");
        }
    }

    public void StopLogging()
    {
        try
        {
            _eventListener?.Dispose();
            _eventListener = null;

            _cts?.Cancel();
            _channel?.Writer.TryComplete();

            try
            {
                _consumerTask?.Wait(TimeSpan.FromSeconds(2));
            }
            catch { /* ignore */ }

            _writer?.Flush();
            _writer?.Dispose();
            _writer = null;
            _cts?.Dispose();
            _cts = null;
            _consumerTask = null;
            _channel = null;
            _currentFilePath = null;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error stopping NetworkDiagnosticService");
        }
    }

    public void Log(string category, string message)
    {
        var ch = _channel;
        if (ch == null || _writer == null) return;
        var now = DateTime.UtcNow;
        var line = string.Create(CultureInfo.InvariantCulture,
            $"[{now:HH:mm:ss.fff}][{category}] {message}");
        ch.Writer.TryWrite(line);
    }

    public void LogSend(string protocolType, string method, int sizeBytes)
    {
        var now = DateTime.UtcNow;
        var lastSend = Interlocked.Exchange(ref _lastSendUtcTicks, now.Ticks);
        long deltaMs = lastSend == 0 ? 0 : (now.Ticks - lastSend) / TimeSpan.TicksPerMillisecond;
        Log("SignalR", $"→ SEND {protocolType,-12} {method,-40} size={sizeBytes,6}B since_last_send={deltaMs}ms");
    }

    public void LogRecv(string protocolType, string method, int sizeBytes)
    {
        var now = DateTime.UtcNow;
        var lastRecv = Interlocked.Exchange(ref _lastRecvUtcTicks, now.Ticks);
        long deltaMs = lastRecv == 0 ? 0 : (now.Ticks - lastRecv) / TimeSpan.TicksPerMillisecond;
        Log("SignalR", $"← RECV {protocolType,-12} {method,-40} size={sizeBytes,6}B since_last_recv={deltaMs}ms");
    }

    public void LogHubEvent(string eventName, string detail)
    {
        var now = DateTime.UtcNow;
        long lastSend = Interlocked.Read(ref _lastSendUtcTicks);
        long lastRecv = Interlocked.Read(ref _lastRecvUtcTicks);
        long sendIdleMs = lastSend == 0 ? -1 : (now.Ticks - lastSend) / TimeSpan.TicksPerMillisecond;
        long recvIdleMs = lastRecv == 0 ? -1 : (now.Ticks - lastRecv) / TimeSpan.TicksPerMillisecond;
        Log("HubEvent", $"⚠ {eventName} {detail} | idle_since_send={sendIdleMs}ms idle_since_recv={recvIdleMs}ms");
    }

    private async Task ConsumeLoopAsync(Channel<string> channel, CancellationToken token)
    {
        var sinceLastFlush = DateTime.UtcNow;
        try
        {
            await foreach (var line in channel.Reader.ReadAllAsync(token).ConfigureAwait(false))
            {
                try
                {
                    if (_writer != null)
                        await _writer.WriteLineAsync(line).ConfigureAwait(false);

                    // Flush every 500ms for near-real-time visibility without I/O storms
                    if ((DateTime.UtcNow - sinceLastFlush).TotalMilliseconds > 500)
                    {
                        if (_writer != null)
                            await _writer.FlushAsync(token).ConfigureAwait(false);
                        sinceLastFlush = DateTime.UtcNow;
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "NetworkDiag write failed");
                }
            }
        }
        catch (OperationCanceledException)
        {
            // expected
        }
        finally
        {
            try
            {
                if (_writer != null)
                    await _writer.FlushAsync(CancellationToken.None).ConfigureAwait(false);
            }
            catch { /* ignore */ }
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        StopLogging();
    }
}
