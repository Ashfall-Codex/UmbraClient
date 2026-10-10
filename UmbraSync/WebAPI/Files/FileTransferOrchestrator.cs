using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Reflection;
using UmbraSync.MareConfiguration;
using UmbraSync.Services.Mediator;
using UmbraSync.WebAPI.Files.Models;

namespace UmbraSync.WebAPI.Files;

public class FileTransferOrchestrator : DisposableMediatorSubscriberBase
{
    private readonly ConcurrentDictionary<Guid, bool> _downloadReady = new();
    private readonly HttpClient _httpClient;
    private readonly MareConfigService _mareConfig;
    private readonly Lock _semaphoreModificationLock = new();
    private readonly TokenProvider _tokenProvider;
    private int _availableDownloadSlots;
    private SemaphoreSlim _downloadSemaphore;
    private int CurrentlyUsedDownloadSlots => _availableDownloadSlots - _downloadSemaphore.CurrentCount;
    private readonly Lock _initializationLock = new();
    private TaskCompletionSource _initialized = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public FileTransferOrchestrator(ILogger<FileTransferOrchestrator> logger, MareConfigService mareConfig,
        MareMediator mediator, TokenProvider tokenProvider) : base(logger, mediator)
    {
        _mareConfig = mareConfig;
        _tokenProvider = tokenProvider;
        var handler = new SocketsHttpHandler
        {
            ConnectTimeout = TimeSpan.FromSeconds(10),
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            PooledConnectionIdleTimeout = TimeSpan.FromSeconds(90),
            MaxConnectionsPerServer = 32,
            AutomaticDecompression = DecompressionMethods.None,
        };
        _httpClient = new(handler)
        {
            Timeout = TimeSpan.FromSeconds(300),
            DefaultRequestVersion = HttpVersion.Version11,
        };
        var ver = Assembly.GetExecutingAssembly().GetName().Version;
        _httpClient.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("UmbraSync", ver?.Major + "." + ver?.Minor + "." + ver?.Build));

        _availableDownloadSlots = mareConfig.Current.ParallelDownloads;
        _downloadSemaphore = new(_availableDownloadSlots, _availableDownloadSlots);

        Mediator.Subscribe<ConnectedMessage>(this, (msg) =>
        {
            FilesCdnUri = msg.Connection.ServerInfo.FileServerAddress;
            lock (_initializationLock)
            {
                if (FilesCdnUri != null)
                    _initialized.TrySetResult();
            }
        });

        Mediator.Subscribe<DisconnectedMessage>(this, (msg) =>
        {
            FilesCdnUri = null;
            lock (_initializationLock)
            {
                if (_initialized.Task.IsCompleted)
                    _initialized = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            }
        });
        Mediator.Subscribe<DownloadReadyMessage>(this, (msg) =>
        {
            _downloadReady[msg.RequestId] = true;
        });
    }

    public Uri? FilesCdnUri { private set; get; }
    public List<FileTransfer> ForbiddenTransfers { get; } = [];
    public bool IsInitialized => FilesCdnUri != null;

    /// <summary>
    /// Attend que le serveur ait annoncé son adresse de serveur de fichiers (ConnectedMessage).
    /// Le hub SignalR démarre avant que ce message soit publié : sans cette attente, un push
    /// déclenché juste après la connexion part avec des fichiers non uploadés.
    /// </summary>
    public async Task<bool> WaitForInitializationAsync(TimeSpan timeout, CancellationToken ct)
    {
        if (IsInitialized) return true;

        Task initializedTask;
        lock (_initializationLock)
        {
            initializedTask = _initialized.Task;
        }

        try
        {
            await initializedTask.WaitAsync(timeout, ct).ConfigureAwait(false);
            return IsInitialized;
        }
        catch (TimeoutException)
        {
            return false;
        }
    }

    public void ClearDownloadRequest(Guid guid)
    {
        _downloadReady.Remove(guid, out _);
    }

    public bool IsDownloadReady(Guid guid)
    {
        if (_downloadReady.TryGetValue(guid, out bool isReady) && isReady)
        {
            return true;
        }

        return false;
    }

    public void ReleaseDownloadSlot()
    {
        try
        {
            _downloadSemaphore.Release();
            // Ne publier que si une limite de vitesse est active : sinon la redistribution de bande
            // passante est un no-op, et comme le CDN acquiert/relâche un slot PAR fichier, ça génère
            // une tempête de messages synchrones (SameThreadMessage) vers tous les FileDownloadManager.
            if (_mareConfig.Current.DownloadSpeedLimitInBytes > 0)
                Mediator.Publish(new DownloadLimitChangedMessage());
        }
        catch (SemaphoreFullException)
        {
            // ignore
        }
    }

    public async Task<HttpResponseMessage> SendRequestAsync(HttpMethod method, Uri uri,
        CancellationToken? ct = null, HttpCompletionOption httpCompletionOption = HttpCompletionOption.ResponseContentRead)
    {
        using var requestMessage = new HttpRequestMessage(method, uri);
        return await SendRequestInternalAsync(requestMessage, ct, httpCompletionOption).ConfigureAwait(false);
    }

    public async Task<HttpResponseMessage> SendRequestAsync(HttpRequestMessage requestMessage, CancellationToken ct,
        HttpCompletionOption httpCompletionOption = HttpCompletionOption.ResponseContentRead)
    {
        return await SendRequestInternalAsync(requestMessage, ct, httpCompletionOption).ConfigureAwait(false);
    }

    public async Task<HttpResponseMessage> SendRequestAsync<T>(HttpMethod method, Uri uri, T content, CancellationToken ct) where T : class
    {
        using var requestMessage = new HttpRequestMessage(method, uri);
        if (content is not ByteArrayContent)
            requestMessage.Content = JsonContent.Create(content);
        else
            requestMessage.Content = content as ByteArrayContent;
        return await SendRequestInternalAsync(requestMessage, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Variante avec nouvel essai court sur 429 (Retry-After respecté) et 502/503/504 ou erreur réseau.
    /// Réservée aux requêtes idempotentes de téléchargement : le message est reconstruit à chaque essai.
    /// </summary>
    public Task<HttpResponseMessage> SendRetryableRequestAsync(HttpMethod method, Uri uri, CancellationToken ct,
        HttpCompletionOption httpCompletionOption = HttpCompletionOption.ResponseContentRead)
    {
        return SendWithTransientRetryAsync(() => new HttpRequestMessage(method, uri), ct, httpCompletionOption);
    }

    public Task<HttpResponseMessage> SendRetryableRequestAsync<T>(HttpMethod method, Uri uri, T content, CancellationToken ct) where T : class
    {
        return SendWithTransientRetryAsync(() => new HttpRequestMessage(method, uri) { Content = JsonContent.Create(content) }, ct,
            HttpCompletionOption.ResponseContentRead);
    }

    private async Task<HttpResponseMessage> SendWithTransientRetryAsync(Func<HttpRequestMessage> requestFactory, CancellationToken ct,
        HttpCompletionOption httpCompletionOption)
    {
        int attempt = 0;
        while (true)
        {
            attempt++;
            using var requestMessage = requestFactory();
            var uri = requestMessage.RequestUri;
            HttpResponseMessage response;
            try
            {
                response = await SendRequestInternalAsync(requestMessage, ct, httpCompletionOption).ConfigureAwait(false);
            }
            catch (HttpRequestException ex) when (attempt < MaxTransientAttempts && !ct.IsCancellationRequested)
            {
                var delay = TransientRetryDelay(attempt, null);
                Logger.LogWarning("Erreur réseau sur {uri}, nouvel essai {attempt}/{max} dans {delay} ms ({error})",
                    uri, attempt + 1, MaxTransientAttempts, (int)delay.TotalMilliseconds, ex.InnerException?.Message ?? ex.Message);
                await Task.Delay(delay, ct).ConfigureAwait(false);
                continue;
            }

            if (attempt >= MaxTransientAttempts || !IsTransientStatus(response.StatusCode))
                return response;

            var retryDelay = TransientRetryDelay(attempt, response.Headers.RetryAfter);
            if (retryDelay > MaxTransientRetryDelay)
            {
                // Le serveur demande une pause plus longue que ce qu'on accepte d'attendre ici :
                // on rend la réponse, l'appelant échoue et le cooldown par hash prend le relais.
                return response;
            }

            Logger.LogWarning("HTTP {status} sur {uri}, nouvel essai {attempt}/{max} dans {delay} ms",
                (int)response.StatusCode, uri, attempt + 1, MaxTransientAttempts, (int)retryDelay.TotalMilliseconds);
            response.Dispose();
            await Task.Delay(retryDelay, ct).ConfigureAwait(false);
        }
    }

    private const int MaxTransientAttempts = 3;
    private static readonly TimeSpan MaxTransientRetryDelay = TimeSpan.FromSeconds(15);

    private static bool IsTransientStatus(HttpStatusCode status)
        => status is HttpStatusCode.TooManyRequests or HttpStatusCode.BadGateway
            or HttpStatusCode.ServiceUnavailable or HttpStatusCode.GatewayTimeout;

    private static TimeSpan TransientRetryDelay(int attempt, RetryConditionHeaderValue? retryAfter)
    {
        var requested = retryAfter?.Delta
            ?? (retryAfter?.Date is { } date ? date - DateTimeOffset.UtcNow : null);
        if (requested is { } delta && delta > TimeSpan.Zero)
            return delta;

        double baseMs = 1000 * Math.Pow(2, attempt - 1);
        return TimeSpan.FromMilliseconds(baseMs * (0.5 + Random.Shared.NextDouble() * 0.5));
    }

    public async Task<HttpResponseMessage> SendRequestStreamAsync(HttpMethod method, Uri uri, ProgressableStreamContent content, CancellationToken ct)
    {
        using var requestMessage = new HttpRequestMessage(method, uri);
        requestMessage.Content = content;
        return await SendRequestInternalAsync(requestMessage, ct).ConfigureAwait(false);
    }

    public async Task WaitForDownloadSlotAsync(CancellationToken token)
    {
        lock (_semaphoreModificationLock)
        {
            if (_availableDownloadSlots != _mareConfig.Current.ParallelDownloads && _availableDownloadSlots == _downloadSemaphore.CurrentCount)
            {
                _availableDownloadSlots = _mareConfig.Current.ParallelDownloads;
                _downloadSemaphore = new(_availableDownloadSlots, _availableDownloadSlots);
            }
        }

        await _downloadSemaphore.WaitAsync(token).ConfigureAwait(false);
        if (_mareConfig.Current.DownloadSpeedLimitInBytes > 0)
            Mediator.Publish(new DownloadLimitChangedMessage());
    }

    public long DownloadLimitPerSlot()
    {
        var limit = _mareConfig.Current.DownloadSpeedLimitInBytes;
        if (limit <= 0) return 0;
        limit = _mareConfig.Current.DownloadSpeedType switch
        {
            MareConfiguration.Models.DownloadSpeeds.Bps => limit,
            MareConfiguration.Models.DownloadSpeeds.KBps => limit * 1024,
            MareConfiguration.Models.DownloadSpeeds.MBps => limit * 1024 * 1024,
            _ => limit,
        };
        var currentUsedDlSlots = CurrentlyUsedDownloadSlots;
        var avaialble = _availableDownloadSlots;
        var currentCount = _downloadSemaphore.CurrentCount;
        var dividedLimit = limit / (currentUsedDlSlots == 0 ? 1 : currentUsedDlSlots);
        if (dividedLimit < 0)
        {
            Logger.LogWarning("Calculated Bandwidth Limit is negative, returning Infinity: {value}, CurrentlyUsedDownloadSlots is {currentSlots}, " +
                "DownloadSpeedLimit is {limit}, available slots: {avail}, current count: {count}", dividedLimit, currentUsedDlSlots, limit, avaialble, currentCount);
            return long.MaxValue;
        }
        return Math.Clamp(dividedLimit, 1, long.MaxValue);
    }

    private async Task<HttpResponseMessage> SendRequestInternalAsync(HttpRequestMessage requestMessage,
        CancellationToken? ct = null, HttpCompletionOption httpCompletionOption = HttpCompletionOption.ResponseContentRead)
    {
        var token = await _tokenProvider.GetToken().ConfigureAwait(false);
        requestMessage.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        if (requestMessage.Content != null && requestMessage.Content is not StreamContent && requestMessage.Content is not ByteArrayContent)
        {
            var content = await ((JsonContent)requestMessage.Content).ReadAsStringAsync().ConfigureAwait(false);
            Logger.LogDebug("Sending {method} to {uri} (Content: {content})", requestMessage.Method, requestMessage.RequestUri, content);
        }
        else
        {
            Logger.LogDebug("Sending {method} to {uri}", requestMessage.Method, requestMessage.RequestUri);
        }

        try
        {
            if (ct != null)
                return await _httpClient.SendAsync(requestMessage, httpCompletionOption, ct.Value).ConfigureAwait(false);
            return await _httpClient.SendAsync(requestMessage, httpCompletionOption).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Error during SendRequestInternal for {uri}", requestMessage.RequestUri);
            throw new HttpRequestException($"Error sending request to {requestMessage.RequestUri}", ex);
        }
    }
}