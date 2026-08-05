using Dalamud.Utility;
using K4os.Compression.LZ4.Streams;
using Microsoft.Extensions.Logging;
using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Collections.Concurrent;
using System.Security.Cryptography;
using UmbraSync.API.Data;
using UmbraSync.MareConfiguration.Models;
using UmbraSync.API.Dto.Files;
using UmbraSync.API.Routes;
using UmbraSync.FileCache;
using UmbraSync.MareConfiguration;
using UmbraSync.MareConfiguration.Configurations;
using UmbraSync.PlayerData.Handlers;
using UmbraSync.Services.Mediator;
using UmbraSync.Utils;
using UmbraSync.WebAPI.Files.Models;

namespace UmbraSync.WebAPI.Files;

public class FileDownloadManager : DisposableMediatorSubscriberBase
{
    private readonly ConcurrentDictionary<string, FileDownloadStatus> _downloadStatus;
    private readonly FileCompactor _fileCompactor;
    private readonly FileCacheManager _fileDbManager;
    private readonly MareConfigService _mareConfigService;
    private readonly FileTransferOrchestrator _orchestrator;
    private readonly FileDownloadDeduplicator _deduplicator;
    private readonly ConcurrentDictionary<ThrottledStream, byte> _activeDownloadStreams;
    private readonly Lock _queueLock = new();
    private SemaphoreSlim _decompressGate;
    private int _decompressGateCapacity;
    private readonly Lock _decompressGateLock = new();
    private SemaphoreSlim? _downloadQueueSemaphore;
    private int _downloadQueueCapacity = -1;

    // Circuit breaker for direct CDN downloads - disable after consecutive failures
    private const int MaxConsecutiveDirectDownloadFailures = 3;
    private volatile int _consecutiveDirectDownloadFailures;
    private volatile bool _disableDirectDownloads;

    // Circuit breaker for CDN enqueue requests - fallback to main server after consecutive failures
    private const int MaxConsecutiveCdnEnqueueFailures = 2;
    private volatile int _consecutiveCdnEnqueueFailures;
    private volatile bool _disableCdnEnqueue;
    private readonly ConcurrentDictionary<string, DateTime> _cdnFailedHashes = new(StringComparer.Ordinal);
    private static readonly TimeSpan CdnFailureTtl = TimeSpan.FromSeconds(60);
    private readonly ConcurrentDictionary<string, (DateTime LastFailure, int FailCount)> _hashFailureCooldowns = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, DateTime> _serverMissingHashes = new(StringComparer.OrdinalIgnoreCase);
    private static readonly TimeSpan ServerMissingTtl = TimeSpan.FromMinutes(10);
    private const double BaseCooldownSeconds = 15;
    private const double MaxCooldownSeconds = 120;
    private const int MaxTrackedFailureCount = 4;
    private enum CdnDownloadResult { Success, NotFound, Transient }

    private readonly CompressedAlternateManager _compressedAlternateManager;

    public FileDownloadManager(ILogger<FileDownloadManager> logger, MareMediator mediator,
        FileTransferOrchestrator orchestrator,
        FileCacheManager fileCacheManager, FileCompactor fileCompactor, MareConfigService mareConfigService,
        FileDownloadDeduplicator deduplicator, CompressedAlternateManager compressedAlternateManager) : base(logger, mediator)
    {
        _downloadStatus = new ConcurrentDictionary<string, FileDownloadStatus>(StringComparer.Ordinal);
        _orchestrator = orchestrator;
        _fileDbManager = fileCacheManager;
        _fileCompactor = fileCompactor;
        _mareConfigService = mareConfigService;
        _deduplicator = deduplicator;
        _compressedAlternateManager = compressedAlternateManager;
        _activeDownloadStreams = new();
        _decompressGateCapacity = ResolveDecompressionLimit(mareConfigService.Current);
        _decompressGate = new SemaphoreSlim(_decompressGateCapacity);

        Mediator.Subscribe<DownloadLimitChangedMessage>(this, _ =>
        {
            if (_activeDownloadStreams.IsEmpty) return;
            var newLimit = _orchestrator.DownloadLimitPerSlot();
            Logger.LogTrace("Setting new Download Speed Limit to {newLimit}", newLimit);
            foreach (var stream in _activeDownloadStreams.Keys)
            {
                stream.BandwidthLimit = newLimit;
            }
        });

        // Reset circuit breaker on reconnection
        Mediator.Subscribe<ConnectedMessage>(this, _ => ResetDirectDownloadCircuitBreaker());
    }

    private void ResetDirectDownloadCircuitBreaker()
    {
        if (_disableDirectDownloads)
            Logger.LogInformation("Resetting direct CDN download circuit breaker");
        if (_disableCdnEnqueue)
            Logger.LogInformation("Resetting CDN enqueue circuit breaker");
        _consecutiveDirectDownloadFailures = 0;
        _disableDirectDownloads = false;
        _consecutiveCdnEnqueueFailures = 0;
        _disableCdnEnqueue = false;
    }
    
    // Utilisé par l'option "Re-télécharger" du menu contextuel.
    public void ResetFailureState()
    {
        _cdnFailedHashes.Clear();
        _hashFailureCooldowns.Clear();
        _serverMissingHashes.Clear();
        ResetDirectDownloadCircuitBreaker();
    }

    private void ReportCdnMissFireAndForget(string hash)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                if (!_orchestrator.IsInitialized) return;
                var uri = MareFiles.ServerFilesReportCdnMissFullPath(_orchestrator.FilesCdnUri!);
                await _orchestrator.SendRequestAsync(HttpMethod.Post, uri, new List<string> { hash }, CancellationToken.None).ConfigureAwait(false);
                Logger.LogDebug("Reported CDN miss for {hash}", hash);
            }
            catch (Exception ex)
            {
                Logger.LogWarning(ex, "Failed to report CDN miss for {hash}", hash);
            }
        });
    }

    public List<DownloadFileTransfer> CurrentDownloads { get; private set; } = [];

    public List<FileTransfer> ForbiddenTransfers => _orchestrator.ForbiddenTransfers;

    /// <summary>
    /// Hashes que le serveur déclare absents (FileExists=false) : le pair a poussé un manifest dont
    /// les fichiers n'ont jamais été uploadés. Inutile de retenter avant qu'il ne repousse ses données.
    /// </summary>
    public bool IsHashMissingOnServer(string hash)
    {
        if (!_serverMissingHashes.TryGetValue(hash, out var recordedAt))
            return false;
        if (DateTime.UtcNow - recordedAt < ServerMissingTtl)
            return true;
        _serverMissingHashes.TryRemove(hash, out _);
        return false;
    }

    private void TrackHashesMissingOnServer(List<DownloadFileDto> dtos)
    {
        List<string>? missing = null;
        foreach (var dto in dtos)
        {
            if (dto.IsForbidden || string.IsNullOrEmpty(dto.Hash)) continue;
            if (dto.FileExists)
            {
                _serverMissingHashes.TryRemove(dto.Hash, out _);
                continue;
            }
            if (_serverMissingHashes.TryAdd(dto.Hash, DateTime.UtcNow))
                (missing ??= []).Add(dto.Hash);
        }

        if (missing != null)
        {
            Logger.LogWarning("{count} fichiers absents du serveur (jamais uploadés par le pair) : {hashes}",
                missing.Count, string.Join(", ", missing));
        }
    }

    public bool IsHashOnCooldown(string hash)
    {
        if (!_hashFailureCooldowns.TryGetValue(hash, out var info))
            return false;
        var cooldown = TimeSpan.FromSeconds(Math.Min(BaseCooldownSeconds * Math.Pow(2, info.FailCount - 1), MaxCooldownSeconds));
        if (DateTime.UtcNow - info.LastFailure < cooldown)
            return true;
        _hashFailureCooldowns.TryRemove(hash, out _);
        return false;
    }
    public void RecordCdnFailure(string hash)
    {
        _cdnFailedHashes[hash] = DateTime.UtcNow;
    }
    public bool HasCdnFailure(string hash)
    {
        if (!_cdnFailedHashes.TryGetValue(hash, out var recordedAt))
            return false;
        if (DateTime.UtcNow - recordedAt < CdnFailureTtl)
            return true;
        // TTL écoulé → on retente le CDN (auto-récupération, sémantique circuit breaker half-open)
        _cdnFailedHashes.TryRemove(hash, out _);
        return false;
    }

    private void CompleteDownloadHash(string hash, bool success)
    {
        // Le résultat de Complete ne concerne que le réveil des waiters : un hash téléchargé par lot
        // (fichiers sans direct download) n'est jamais claimé, et conditionner le cooldown à cette
        // valeur laissait ces hashes sans backoff — d'où des reapply en boucle serrée.
        _deduplicator.Complete(hash, success);

        if (success)
        {
            _cdnFailedHashes.TryRemove(hash, out _);
            _hashFailureCooldowns.TryRemove(hash, out _);
        }
        else
        {
            _hashFailureCooldowns.AddOrUpdate(hash,
                _ => (DateTime.UtcNow, 1),
                (_, existing) => (DateTime.UtcNow, Math.Min(existing.FailCount + 1, MaxTrackedFailureCount)));
        }
    }

    private void FailBatchHashes(IEnumerable<DownloadFileTransfer> fileGroup, ConcurrentDictionary<string, byte> pendingFallbackHashes)
    {
        foreach (var file in fileGroup)
        {
            CompleteDownloadHash(file.Hash, false);
            pendingFallbackHashes.TryRemove(file.Hash, out _);
        }
    }

    public bool IsDownloading => CurrentDownloads.Any();

    public void ClearDownload()
    {
        CurrentDownloads.Clear();
        _downloadStatus.Clear();
    }
    
    public async Task DownloadFiles(string downloadId, List<FileReplacementData> fileReplacementDto, CancellationToken ct)
    {
        SemaphoreSlim? queueSemaphore = null;
        if (_mareConfigService.Current.EnableDownloadQueue)
        {
            queueSemaphore = GetQueueSemaphore();
            Logger.LogTrace("Queueing download for {name}. Currently queued: {queued}", downloadId, queueSemaphore.CurrentCount);
            await queueSemaphore.WaitAsync(ct).ConfigureAwait(false);
        }

        Mediator.Publish(new HaltScanMessage(nameof(DownloadFiles)));
        try
        {
            await DownloadFilesInternal(downloadId, fileReplacementDto, ct).ConfigureAwait(false);
        }
        catch
        {
            ClearDownload();
        }
        finally
        {
            if (queueSemaphore != null)
            {
                queueSemaphore.Release();
            }

            Mediator.Publish(new ResumeScanMessage(nameof(DownloadFiles)));
        }
    }

    public async Task DownloadFiles(GameObjectHandler gameObject, List<FileReplacementData> fileReplacementDto, CancellationToken ct)
    {
        SemaphoreSlim? queueSemaphore = null;
        if (_mareConfigService.Current.EnableDownloadQueue)
        {
            queueSemaphore = GetQueueSemaphore();
            Logger.LogTrace("Queueing download for {name}. Currently queued: {queued}", gameObject.Name, queueSemaphore.CurrentCount);
            await queueSemaphore.WaitAsync(ct).ConfigureAwait(false);
        }

        Mediator.Publish(new HaltScanMessage(nameof(DownloadFiles)));
        try
        {
            await DownloadFilesInternal(gameObject, fileReplacementDto, ct).ConfigureAwait(false);
        }
        catch
        {
            ClearDownload();
        }
        finally
        {
            if (queueSemaphore != null)
            {
                queueSemaphore.Release();
            }

            Mediator.Publish(new DownloadFinishedMessage(gameObject));
            Mediator.Publish(new ResumeScanMessage(nameof(DownloadFiles)));
        }
    }

    protected override void Dispose(bool disposing)
    {
        ClearDownload();
        foreach (var stream in _activeDownloadStreams.Keys.ToList())
        {
            try
            {
                stream.Dispose();
            }
            catch
            {
                // do nothing
            }
        }
        _activeDownloadStreams.Clear();
        base.Dispose(disposing);
    }

    private static byte ConvertReadByte(int byteOrEof)
    {
        if (byteOrEof == -1)
        {
            throw new EndOfStreamException();
        }

        return (byte)byteOrEof;
    }

    private static (string fileHash, long fileLengthBytes) ReadBlockFileHeader(FileStream fileBlockStream)
    {
        const int maxHashLen = 64;
        const int maxLengthLen = 20;

        List<char> hashName = [];
        List<char> fileLength = [];
        var separator = (char)ConvertReadByte(fileBlockStream.ReadByte());
        if (separator != '#') throw new InvalidDataException("Data is invalid, first char is not #");

        bool readHash = false;
        while (true)
        {
            int readByte = fileBlockStream.ReadByte();
            if (readByte == -1)
                throw new EndOfStreamException();

            var readChar = (char)ConvertReadByte(readByte);
            if (readChar == ':')
            {
                readHash = true;
                continue;
            }
            if (readChar == '#') break;
            if (!readHash)
            {
                if (hashName.Count >= maxHashLen)
                    throw new InvalidDataException($"Block file header hash exceeds {maxHashLen} chars");
                hashName.Add(readChar);
            }
            else
            {
                if (fileLength.Count >= maxLengthLen)
                    throw new InvalidDataException($"Block file header length exceeds {maxLengthLen} chars");
                fileLength.Add(readChar);
            }
        }
        if (fileLength.Count == 0)
            fileLength.Add('0');
        return (string.Join("", hashName), long.Parse(string.Join("", fileLength), CultureInfo.InvariantCulture));
    }

    private SemaphoreSlim GetQueueSemaphore()
    {
        var desiredCapacity = Math.Clamp(_mareConfigService.Current.ParallelDownloads, 1, 50);

        using (_queueLock.EnterScope())
        {
            if (_downloadQueueSemaphore == null || _downloadQueueCapacity != desiredCapacity)
            {
                _downloadQueueSemaphore = new SemaphoreSlim(desiredCapacity, desiredCapacity);
                _downloadQueueCapacity = desiredCapacity;
            }

            return _downloadQueueSemaphore;
        }
    }

    private async Task DownloadAndMungeFileHttpClient(string downloadGroup, Guid requestId, List<DownloadFileTransfer> fileTransfer, string tempPath, IProgress<long> progress, Uri effectiveBaseUri, CancellationToken ct)
    {
        Logger.LogDebug("GUID {requestId} on server {uri} for files {files}", requestId, effectiveBaseUri, string.Join(", ", fileTransfer.Select(c => c.Hash).ToList()));

        await WaitForDownloadReady(fileTransfer, requestId, effectiveBaseUri, ct).ConfigureAwait(false);

        if (_downloadStatus.TryGetValue(downloadGroup, out var dlStatus))
            dlStatus.DownloadStatus = DownloadStatus.Downloading;

        HttpResponseMessage response = null!;
        var requestUrl = MareFiles.CacheGetFullPath(effectiveBaseUri, requestId);

        Logger.LogDebug("Downloading {requestUrl} for request {id}", requestUrl, requestId);
        try
        {
            response = await _orchestrator.SendRequestAsync(HttpMethod.Get, requestUrl, ct, HttpCompletionOption.ResponseHeadersRead).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
        }
        catch (HttpRequestException ex)
        {
            Logger.LogWarning(ex, "Error during download of {requestUrl}, HttpStatusCode: {code}", requestUrl, ex.StatusCode);
            if (ex.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Unauthorized)
            {
                throw new InvalidDataException($"Http error {ex.StatusCode} (cancelled: {ct.IsCancellationRequested}): {requestUrl}", ex);
            }
        }

        // Garde-fou : si la requête a échoué (5xx, timeout, etc.) sans être 404/401, le catch
        // ci-dessus a loggé mais pas relancé — sans cette garde on déréférencerait `response`
        // (null → NRE, ou corps d'erreur lu comme un fichier). On laisse la boucle de retry batch agir.
        if (response is null || !response.IsSuccessStatusCode)
        {
            throw new InvalidDataException($"Download failed for {requestUrl} (response null or unsuccessful, cancelled: {ct.IsCancellationRequested})");
        }

        ThrottledStream? stream = null;
        try
        {
            var fileStream = File.Create(tempPath);
            await using (fileStream.ConfigureAwait(false))
            {
                var bufferSize = response.Content.Headers.ContentLength > 1024 * 1024 ? 65536 : 8196;
                var buffer = new byte[bufferSize];

                int bytesRead;
                var limit = _orchestrator.DownloadLimitPerSlot();
                Logger.LogTrace("Starting Download of {id} with a speed limit of {limit} to {tempPath}", requestId, limit, tempPath);
                stream = new ThrottledStream(await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false), limit);
                _activeDownloadStreams.TryAdd(stream, 0);
                while ((bytesRead = await stream.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
                {
                    ct.ThrowIfCancellationRequested();

                    await fileStream.WriteAsync(buffer.AsMemory(0, bytesRead), ct).ConfigureAwait(false);

                    progress.Report(bytesRead);
                }

                Logger.LogDebug("{requestUrl} downloaded to {tempPath}", requestUrl, tempPath);
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            try
            {
                if (!tempPath.IsNullOrEmpty())
                    File.Delete(tempPath);
            }
            catch
            {
                // ignore if file deletion fails
            }
            throw;
        }
        finally
        {
            if (stream != null)
            {
                _activeDownloadStreams.TryRemove(stream, out _);
                await stream.DisposeAsync().ConfigureAwait(false);
            }
            response?.Dispose();
        }
    }
    
    private static TimeSpan JitteredBackoff(int attempt, double baseMs = 500, double capMs = 8000)
    {
        double exp = Math.Min(capMs, baseMs * Math.Pow(2, Math.Max(0, attempt - 1)));
        return TimeSpan.FromMilliseconds(Random.Shared.NextDouble() * exp);
    }

    // --- CDN Direct Download: Phase 1 (download compressed to .lz4tmp) ---

    private async Task<CdnDownloadResult> DownloadDirectToLz4TmpAsync(DownloadFileTransfer file, string lz4TmpPath, IProgress<long> progress, CancellationToken ct)
    {
        if (!file.HasDirectDownload || file.DirectDownloadUri == null)
            return CdnDownloadResult.NotFound;

        var url = file.DirectDownloadUri.ToString();
        Logger.LogDebug("Direct CDN download (compressed): {hash} from {url}", file.Hash, url);

        if (File.Exists(lz4TmpPath))
        {
            try { File.Delete(lz4TmpPath); }
            catch (Exception ex) { Logger.LogWarning(ex, "Cannot delete existing .lz4tmp file {path}", lz4TmpPath); }
        }

        // Transitoire CDN → on re-tente le CDN (le serveur principal ne sert jamais les fichiers
        // S3-cold), donc on s'autorise une tentative de plus avant d'abandonner le cycle.
        const int maxRetries = 3;
        const int connectionTimeoutSeconds = 15; // Timeout for TCP connect + TLS + headers (generous for slow connections)
        const int inactivityTimeoutSeconds = 30; // Cancel if no bytes received for this duration (généreux pour mobile/lent)

        for (int attempt = 1; attempt <= maxRetries; attempt++)
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(connectionTimeoutSeconds));

            try
            {
                Logger.LogDebug("Attempt {attempt}/{max} - Direct CDN download for {hash}", attempt, maxRetries, file.Hash);

                var response = await _orchestrator.SendRequestAsync(HttpMethod.Get, new Uri(url), timeoutCts.Token, HttpCompletionOption.ResponseHeadersRead).ConfigureAwait(false);

                if (response.StatusCode == HttpStatusCode.NotFound)
                {
                    Logger.LogDebug("Direct CDN 404 for {hash}, will fallback", file.Hash);
                    ReportCdnMissFireAndForget(file.Hash);
                    return CdnDownloadResult.NotFound;
                }

                if (!response.IsSuccessStatusCode)
                {
                    Logger.LogWarning("CDN returned HTTP {status} for {hash}", (int)response.StatusCode, file.Hash);
                    response.EnsureSuccessStatusCode();
                }

                // Headers received — switch to activity-based timeout for the stream read phase.
                // This allows slow connections to work as long as data keeps flowing.
                timeoutCts.CancelAfter(TimeSpan.FromSeconds(inactivityTimeoutSeconds));

                ThrottledStream? throttledStream = null;
                try
                {
                    var limit = _orchestrator.DownloadLimitPerSlot();
                    throttledStream = new ThrottledStream(await response.Content.ReadAsStreamAsync(timeoutCts.Token).ConfigureAwait(false), limit);
                    _activeDownloadStreams.TryAdd(throttledStream, 0);

                    var bufferSize = response.Content.Headers.ContentLength > 1024 * 1024 ? 65536 : 8196;
                    var buffer = new byte[bufferSize];

                    using var fileStream = new FileStream(lz4TmpPath, FileMode.Create, FileAccess.Write, FileShare.None);
                    int bytesRead;
                    while ((bytesRead = await throttledStream.ReadAsync(buffer, timeoutCts.Token).ConfigureAwait(false)) > 0)
                    {
                        await fileStream.WriteAsync(buffer.AsMemory(0, bytesRead), timeoutCts.Token).ConfigureAwait(false);
                        progress.Report(bytesRead);

                        // Reset inactivity timer — connection is alive, data is flowing
                        timeoutCts.CancelAfter(TimeSpan.FromSeconds(inactivityTimeoutSeconds));
                    }

                    Logger.LogDebug("CDN download (compressed) finished for {hash}", file.Hash);
                    #pragma warning disable S1751 // Retry loop: returns on success, catches retry on failure
                    return CdnDownloadResult.Success;
                    #pragma warning restore S1751
                }
                finally
                {
                    if (throttledStream != null)
                    {
                        _activeDownloadStreams.TryRemove(throttledStream, out _);
                        await throttledStream.DisposeAsync().ConfigureAwait(false);
                    }
                }
            }
            catch (HttpRequestException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
            {
                Logger.LogDebug("Direct CDN 404 for {hash}, will fallback", file.Hash);
                ReportCdnMissFireAndForget(file.Hash);
                return CdnDownloadResult.NotFound;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (HttpRequestException ex) when (ex.InnerException is TimeoutException || ex.StatusCode == null)
            {
                Logger.LogWarning(ex, "Timeout during CDN download of {hash}. Attempt {attempt}/{max}", file.Hash, attempt, maxRetries);
                if (File.Exists(lz4TmpPath)) try { File.Delete(lz4TmpPath); } catch (Exception) { /* best-effort cleanup */ }

                if (attempt >= maxRetries)
                {
                    Logger.LogWarning("Max retries reached for CDN download of {hash}, will fallback", file.Hash);
                    return CdnDownloadResult.Transient;
                }

                await Task.Delay(JitteredBackoff(attempt), ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                // Covers both OperationCanceledException and TaskCanceledException from inactivity/connection timeout
                Logger.LogWarning("CDN download timed out (inactivity >{inactivity}s) for {hash}. Attempt {attempt}/{max}", inactivityTimeoutSeconds, file.Hash, attempt, maxRetries);
                if (File.Exists(lz4TmpPath)) try { File.Delete(lz4TmpPath); } catch (Exception) { /* best-effort cleanup */ }

                if (attempt >= maxRetries)
                {
                    Logger.LogWarning("Max retries reached for CDN download of {hash}, will fallback", file.Hash);
                    return CdnDownloadResult.Transient;
                }

                await Task.Delay(JitteredBackoff(attempt), ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Logger.LogWarning(ex, "Direct CDN download failed for {hash} (attempt {attempt}/{max}), will fallback", file.Hash, attempt, maxRetries);
                if (File.Exists(lz4TmpPath)) try { File.Delete(lz4TmpPath); } catch (Exception) { /* best-effort cleanup */ }

                if (attempt >= maxRetries)
                    return CdnDownloadResult.Transient;

                await Task.Delay(JitteredBackoff(attempt), ct).ConfigureAwait(false);
            }
        }

        return CdnDownloadResult.Transient;
    }

    // --- CDN Direct Download: Phase 2 (decompress + hash verify + persist) ---

    private bool DecompressAndVerifyLz4(DownloadFileTransfer file, string lz4TmpPath, string cdnTmpPath, string destPath)
    {
        Logger.LogDebug("Decompressing CDN file: {hash}", file.Hash);

        try
        {
            byte[] calculatedHashBytes;

            using (var hashingStream = new HashingStream(
                new FileStream(cdnTmpPath, FileMode.Create, FileAccess.Write, FileShare.None),
                SHA1.Create()))
            {
                using var lz4Input = new FileStream(lz4TmpPath, FileMode.Open, FileAccess.Read, FileShare.Read, 65536);
                using var lz4Decoder = LZ4Stream.Decode(lz4Input, leaveOpen: true);

                var buffer = new byte[65536];
                int bytesRead;
                long totalBytesWritten = 0;
                while ((bytesRead = lz4Decoder.Read(buffer, 0, buffer.Length)) > 0)
                {
                    hashingStream.Write(buffer, 0, bytesRead);
                    totalBytesWritten += bytesRead;
                }

                Logger.LogDebug("LZ4 decompression finished for {hash}, wrote {bytes} bytes", file.Hash, totalBytesWritten);
                calculatedHashBytes = hashingStream.Finish();
            }

            var calculatedHash = BitConverter.ToString(calculatedHashBytes).Replace("-", "", StringComparison.Ordinal);
            if (!string.Equals(calculatedHash, file.Hash, StringComparison.OrdinalIgnoreCase))
            {
                Logger.LogWarning("CDN hash mismatch for {hash}: got {calculated}", file.Hash, calculatedHash);
                if (File.Exists(cdnTmpPath)) File.Delete(cdnTmpPath);
                return false;
            }

            Logger.LogDebug("CDN hash verified for {hash}, renaming to final destination", file.Hash);
            _fileCompactor.RenameAndCompact(destPath, cdnTmpPath);

            if (!File.Exists(destPath))
            {
                Logger.LogWarning("RenameAndCompact did not create destination file for {hash}", file.Hash);
                return false;
            }

            PersistFileToStorage(file.Hash, destPath, file.Total);
            Logger.LogDebug("Direct CDN decompress+persist complete: {hash}", file.Hash);
            return true;
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Decompression failed for {hash}", file.Hash);
            if (File.Exists(cdnTmpPath)) try { File.Delete(cdnTmpPath); } catch { /* best-effort cleanup */ }
            return false;
        }
        finally
        {
            if (File.Exists(lz4TmpPath)) try { File.Delete(lz4TmpPath); } catch { /* best-effort cleanup */ }
        }
    }

    // --- Decompression helpers ---

    private static int CalculateDecompressionLimit(int downloadSlots)
    {
        var cpuBound = Math.Max(1, Math.Min(Environment.ProcessorCount, 4));
        return Math.Clamp(downloadSlots, 1, cpuBound);
    }

    private static int ResolveDecompressionLimit(MareConfig config)
    {
        var userValue = config.MaxDecompressionThreads;
        if (userValue <= 0) return CalculateDecompressionLimit(config.ParallelDownloads);
        return Math.Clamp(userValue, 1, Environment.ProcessorCount);
    }

    private SemaphoreSlim GetDecompressGate()
    {
        var desired = ResolveDecompressionLimit(_mareConfigService.Current);
        if (desired == _decompressGateCapacity) return _decompressGate;

        lock (_decompressGateLock)
        {
            if (desired == _decompressGateCapacity) return _decompressGate;
            if (_decompressGate.CurrentCount == _decompressGateCapacity)
            {
                _decompressGate.Dispose();
                _decompressGateCapacity = desired;
                _decompressGate = new SemaphoreSlim(desired);
            }
        }

        return _decompressGate;
    }

    private static void EnqueueLimitedTask(ConcurrentBag<Task> tasks, SemaphoreSlim limiter, Func<CancellationToken, Task> work, CancellationToken ct)
    {
        var task = Task.Run(async () =>
        {
            await limiter.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                await work(ct).ConfigureAwait(false);
            }
            finally
            {
                limiter.Release();
            }
        }, ct);

        tasks.Add(task);
    }

    private static async Task WaitForAllTasksAsync(ConcurrentBag<Task> tasks)
    {
        while (true)
        {
            var snapshot = tasks.ToArray();
            if (snapshot.Length == 0) return;
            try
            {
                await Task.WhenAll(snapshot).ConfigureAwait(false);
            }
            catch
            {
                // Individual task exceptions are handled inside each task
            }
            if (tasks.Count <= snapshot.Length) return;
        }
    }

    // --- Main download orchestration ---

    /// Surcharge sans GameObjectHandler pour le housing (pas de personnage associé).
    public async Task<List<DownloadFileTransfer>> InitiateDownloadList(string downloadId, List<FileReplacementData> fileReplacement, CancellationToken ct)
    {
        Logger.LogDebug("Download start: {id}", downloadId);

        var allHashes = fileReplacement.Select(f => f.Hash).Distinct(StringComparer.Ordinal).ToList();
        var hashesToRequest = allHashes.Where(h => !IsHashOnCooldown(h)).ToList();
        var skippedCount = allHashes.Count - hashesToRequest.Count;
        if (skippedCount > 0)
            Logger.LogDebug("Skipping {count}/{total} hashes on download cooldown", skippedCount, allHashes.Count);

        if (hashesToRequest.Count == 0)
        {
            Logger.LogDebug("All {count} hashes are on download cooldown, skipping", allHashes.Count);
            CurrentDownloads = [];
            return CurrentDownloads;
        }

        List<DownloadFileDto> downloadFileInfoFromService =
        [
            .. await FilesGetSizes(hashesToRequest, ct).ConfigureAwait(false),
        ];

        Logger.LogDebug("Files with size 0 or less: {files}", string.Join(", ", downloadFileInfoFromService.Where(f => f.Size <= 0).Select(f => f.Hash)));

        TrackHashesMissingOnServer(downloadFileInfoFromService);

        foreach (var dto in downloadFileInfoFromService.Where(c => c.IsForbidden))
        {
            if (!_orchestrator.ForbiddenTransfers.Exists(f => string.Equals(f.Hash, dto.Hash, StringComparison.Ordinal)))
            {
                _orchestrator.ForbiddenTransfers.Add(new DownloadFileTransfer(dto));
            }
        }

        CurrentDownloads = downloadFileInfoFromService.Distinct().Select(d => new DownloadFileTransfer(d))
            .Where(d => d.CanBeTransferred).ToList();

        return CurrentDownloads;
    }

    public async Task<List<DownloadFileTransfer>> InitiateDownloadList(GameObjectHandler gameObjectHandler, List<FileReplacementData> fileReplacement, TextureCompressionMode compressedUsage, HashSet<string> locallyPresentFiles, CancellationToken ct)
    {
        Logger.LogDebug("Download start: {id}", gameObjectHandler.Name);

        var allHashes = fileReplacement.Select(f => f.Hash).Distinct(StringComparer.Ordinal).ToList();
        var hashesToRequest = allHashes.Where(h => !IsHashOnCooldown(h)).ToList();
        var skippedCount = allHashes.Count - hashesToRequest.Count;
        if (skippedCount > 0)
            Logger.LogDebug("Skipping {count}/{total} hashes on download cooldown", skippedCount, allHashes.Count);

        if (hashesToRequest.Count == 0)
        {
            Logger.LogDebug("All {count} hashes are on download cooldown, skipping", allHashes.Count);
            CurrentDownloads = [];
            return CurrentDownloads;
        }

        List<DownloadFileDto> downloadFileInfoFromService =
        [
            .. await FilesGetSizes(hashesToRequest, ct).ConfigureAwait(false),
        ];

        Logger.LogDebug("Files with size 0 or less: {files}", string.Join(", ", downloadFileInfoFromService.Where(f => f.Size <= 0).Select(f => f.Hash)));

        TrackHashesMissingOnServer(downloadFileInfoFromService);

        foreach (var dto in downloadFileInfoFromService.Where(c => c.IsForbidden))
        {
            if (!_orchestrator.ForbiddenTransfers.Exists(f => string.Equals(f.Hash, dto.Hash, StringComparison.Ordinal)))
            {
                _orchestrator.ForbiddenTransfers.Add(new DownloadFileTransfer(dto));
            }
        }

        // BC7 : mémoriser les alternates, et si le mode est compressé, télécharger le BC7 à la place de l'original.
        // La liste est reconstruite plutôt que mutée en cours d'itération, ce qui préserve l'ordre d'origine.
        var retainedDownloads = new List<DownloadFileDto>(downloadFileInfoFromService.Count);
        foreach (var dto in downloadFileInfoFromService)
        {
            _compressedAlternateManager.SetCompressedAlternate(dto.Hash, dto.CompressedAlternateFileDownload?.Hash, dto.WillNotBeCompressed);

            bool usingAlternate = false;
            var effectiveDto = dto;
            if (dto.CompressedAlternateFileDownload != null)
            {
                var alt = dto.CompressedAlternateFileDownload;
                // Un alternate n'a pas lui-même d'alternate.
                _compressedAlternateManager.SetCompressedAlternate(alt.Hash, null, neverWillHaveAlternate: true);

                if (compressedUsage != TextureCompressionMode.AlwaysSourceQuality)
                {
                    usingAlternate = true;
                    var src = fileReplacement.FirstOrDefault(f => string.Equals(f.Hash, dto.Hash, StringComparison.OrdinalIgnoreCase));
                    if (src != null && src.GamePaths.Length > 0
                        && !fileReplacement.Any(f => string.Equals(f.Hash, alt.Hash, StringComparison.OrdinalIgnoreCase)))
                    {
                        fileReplacement.Add(new FileReplacementData { GamePaths = src.GamePaths, Hash = alt.Hash });
                    }
                    Logger.LogDebug("BC7: downloading compressed {alt} instead of {src}", alt.Hash, dto.Hash);
                    effectiveDto = alt;

                    // Si le BC7 est déjà en local, pas besoin de le re-télécharger.
                    if (!locallyPresentFiles.Contains(alt.Hash) && _fileDbManager.GetFileCacheByHash(alt.Hash) != null)
                        locallyPresentFiles.Add(alt.Hash);
                }
            }

            // Fichier envoyé juste pour vérifier un alternate (mode AlwaysCompressed) : si on n'utilise pas d'alt,
            // ou si l'alt utilisé est déjà présent, inutile de (re)télécharger.
            if ((locallyPresentFiles.Contains(dto.Hash) && !usingAlternate)
                || (usingAlternate && locallyPresentFiles.Contains(effectiveDto.Hash)))
            {
                continue;
            }

            retainedDownloads.Add(effectiveDto);
        }

        downloadFileInfoFromService = retainedDownloads;

        CurrentDownloads = downloadFileInfoFromService.Distinct().Select(d => new DownloadFileTransfer(d))
            .Where(d => d.CanBeTransferred).ToList();

        return CurrentDownloads;
    }

    /// Version sans GameObjectHandler : ne publie pas DownloadStartedMessage.
    private async Task DownloadFilesInternal(string downloadId, List<FileReplacementData> fileReplacement, CancellationToken ct)
    {
        var directDownloads = CurrentDownloads.Where(f => f.HasDirectDownload).ToList();
        var fallbackFiles = new ConcurrentBag<DownloadFileTransfer>();
        var pendingFallbackHashes = new ConcurrentDictionary<string, byte>(StringComparer.OrdinalIgnoreCase);
        var decompressionTasks = new ConcurrentBag<Task>();

        try
        {
        if (_disableDirectDownloads && directDownloads.Count > 0)
        {
            Logger.LogWarning("Direct CDN downloads disabled, using fallback for all {count} files", directDownloads.Count);
            foreach (var d in directDownloads) fallbackFiles.Add(d);
            directDownloads.Clear();
        }

        if (directDownloads.Count > 0)
        {
            var cdnSkipped = directDownloads.Where(f => HasCdnFailure(f.Hash)).ToList();
            if (cdnSkipped.Count > 0)
            {
                Logger.LogInformation("[{id}] Skipping CDN for {count} hashes with previous CDN failure, routing to main server", downloadId, cdnSkipped.Count);
                foreach (var d in cdnSkipped)
                {
                    fallbackFiles.Add(d);
                    pendingFallbackHashes.TryAdd(d.Hash, 0);
                }
                directDownloads.RemoveAll(f => HasCdnFailure(f.Hash));
            }
        }

        if (directDownloads.Count > 0)
        {
            Logger.LogInformation("[{id}] Attempting direct CDN download for {count} files", downloadId, directDownloads.Count);

            var slots = Math.Clamp(_mareConfigService.Current.ParallelDownloads, 1, 20);
            // Le gate global _downloadSemaphore (= ParallelDownloads) est désormais le vrai plafond de
            // concurrence, partagé entre tous les pairs. Inutile de lancer slots*2 workers par pair :
            // l'excédent ne ferait qu'attendre sur le sémaphore global (jusqu'à ~N×40 tâches parquées à
            // 24 pairs). On aligne workerDop sur slots : un pair seul peut saturer le pool, sans gaspillage.
            var workerDop = slots;

            await Parallel.ForEachAsync(directDownloads, new ParallelOptions
            {
                MaxDegreeOfParallelism = workerDop,
                CancellationToken = ct
            }, async (file, token) =>
            {
                var claim = _deduplicator.Claim(file.Hash);

                if (!claim.IsOwner)
                {
                    var ownerSuccess = await claim.Completion.ConfigureAwait(false);
                    if (!ownerSuccess)
                        fallbackFiles.Add(file);
                    return;
                }

                var fileData = fileReplacement.FirstOrDefault(f => string.Equals(f.Hash, file.Hash, StringComparison.OrdinalIgnoreCase));
                var fileExtension = (fileData?.GamePaths is { Length: > 0 } ? fileData.GamePaths[0].Split(".")[^1] : null) ?? "tmp";
                var filePath = _fileDbManager.GetCacheFilePath(file.Hash, fileExtension);
                var lz4TmpPath = filePath + ".lz4tmp";
                var cdnTmpPath = filePath + ".cdntmp";

                Progress<long> progress = new(_ => { });

                var downloadSuccess = false;
                var goesToFallback = false;
                try
                {

                    await _orchestrator.WaitForDownloadSlotAsync(token).ConfigureAwait(false);
                    CdnDownloadResult cdnResult;
                    try
                    {
                        cdnResult = await DownloadDirectToLz4TmpAsync(file, lz4TmpPath, progress, token).ConfigureAwait(false);
                    }
                    finally
                    {
                        _orchestrator.ReleaseDownloadSlot();
                    }
                    downloadSuccess = cdnResult == CdnDownloadResult.Success;

                    if (cdnResult == CdnDownloadResult.Success)
                    {
                        Interlocked.Exchange(ref _consecutiveDirectDownloadFailures, 0);
                        _disableDirectDownloads = false;

                        EnqueueLimitedTask(decompressionTasks, GetDecompressGate(), _ =>
                        {
                            var success = false;
                            try
                            {
                                success = DecompressAndVerifyLz4(file, lz4TmpPath, cdnTmpPath, filePath);
                            }
                            finally
                            {
                                CompleteDownloadHash(file.Hash, success);
                            }
                            return Task.CompletedTask;
                        }, CancellationToken.None);
                    }
                    else if (cdnResult == CdnDownloadResult.NotFound)
                    {
                        // 404 : le fichier n'est pas sur le CDN. Le serveur principal peut en avoir une copie chaude,
                        // et ReportCdnMiss (déjà émis) fait re-vérifier S3. On bascule sur le fallback (blacklist time-boxée).
                        RecordCdnFailure(file.Hash);
                        goesToFallback = true;
                        pendingFallbackHashes.TryAdd(file.Hash, 0);
                        fallbackFiles.Add(file);
                    }
                    else
                    {
                        // Échec transitoire (timeout/réseau) : le fichier EST sur le CDN. On NE blackliste PAS et on NE
                        // bascule PAS sur le serveur principal (incapable de servir les fichiers S3-only) ; le hash échoue
                        // ce cycle et le CDN sera réessayé à la prochaine application.
                        var failures = Interlocked.Increment(ref _consecutiveDirectDownloadFailures);
                        if (failures >= MaxConsecutiveDirectDownloadFailures)
                        {
                            _disableDirectDownloads = true;
                            Logger.LogWarning("Direct CDN downloads disabled after {count} consecutive failures", failures);
                        }
                    }
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    // Erreur inattendue → traitée comme transitoire : retry CDN au prochain cycle, pas de blacklist ni de fallback futile.
                    Logger.LogWarning(ex, "[{id}] CDN download error for {hash}, will retry CDN next cycle", downloadId, file.Hash);
                    Interlocked.Increment(ref _consecutiveDirectDownloadFailures);
                }
                finally
                {
                    if (!downloadSuccess && !goesToFallback)
                        CompleteDownloadHash(file.Hash, false);
                }
            }).ConfigureAwait(false);

            if (!fallbackFiles.IsEmpty)
                Logger.LogInformation("[{id}] CDN fallback needed for {count} files", downloadId, fallbackFiles.Count);
        }

        // Phase 2 : Batch downloads (non-CDN + fallback)
        var queueFiles = CurrentDownloads.Where(f => !f.HasDirectDownload).Concat(fallbackFiles).ToList();
        if (queueFiles.Count == 0)
        {
            await WaitForAllTasksAsync(decompressionTasks).ConfigureAwait(false);
            if (!pendingFallbackHashes.IsEmpty)
            {
                foreach (var hash in pendingFallbackHashes.Keys)
                    CompleteDownloadHash(hash, false);
            }
            ClearDownload();
            return;
        }

        var downloadGroups = queueFiles
            .GroupBy(f => f.DownloadUri.Host + ":" + f.DownloadUri.Port, StringComparer.Ordinal)
            .ToList();

        foreach (var downloadGroup in downloadGroups)
        {
            _downloadStatus[downloadGroup.Key] = new FileDownloadStatus()
            {
                DownloadStatus = DownloadStatus.Initializing,
                TotalBytes = downloadGroup.Sum(c => c.Total),
                TotalFiles = 1,
                TransferredBytes = 0,
                TransferredFiles = 0
            };
        }

        // Pas de DownloadStartedMessage pour le housing

        await Parallel.ForEachAsync(downloadGroups, new ParallelOptions()
        {
            MaxDegreeOfParallelism = downloadGroups.Count,
            CancellationToken = ct,
        },
        async (fileGroup, token) =>
        {
            var firstFile = fileGroup.First();
            const int maxBatchRetries = 2;
            string blockFile = string.Empty;

            for (int batchAttempt = 1; batchAttempt <= maxBatchRetries; batchAttempt++)
            {
            var enqueueResult = await RequestBatchEnqueueAsync([.. fileGroup.Select(c => c.Hash)], firstFile.DownloadUri, token, downloadId).ConfigureAwait(false);
            if (enqueueResult == null)
            {
                FailBatchHashes(fileGroup, pendingFallbackHashes);
                return;
            }

            var requestId = enqueueResult.RequestId;
            var effectiveBaseUri = enqueueResult.BaseUri;

            blockFile = _fileDbManager.GetCacheFilePath(requestId.ToString("N"), "blk");
            bool slotHeld = false;
            try
            {
                if (_downloadStatus.TryGetValue(fileGroup.Key, out var slotStatus))
                    slotStatus.DownloadStatus = DownloadStatus.WaitingForSlot;
                await _orchestrator.WaitForDownloadSlotAsync(token).ConfigureAwait(false);
                slotHeld = true;
                if (_downloadStatus.TryGetValue(fileGroup.Key, out slotStatus))
                    slotStatus.DownloadStatus = DownloadStatus.WaitingForQueue;
                Progress<long> progress = new((bytesDownloaded) =>
                {
                    if (_downloadStatus.TryGetValue(fileGroup.Key, out FileDownloadStatus? value))
                        value.AddTransferredBytes(bytesDownloaded);
                });
                await DownloadAndMungeFileHttpClient(fileGroup.Key, requestId, [.. fileGroup], blockFile, progress, effectiveBaseUri, token).ConfigureAwait(false);
                // Relâcher le slot global dès la fin du download réseau : la décompression (plus bas)
                // a son propre gate (GetDecompressGate) et ne doit pas monopoliser un slot de download.
                _orchestrator.ReleaseDownloadSlot();
                slotHeld = false;
                break; // Succès, sortir de la boucle retry
            }
            catch (OperationCanceledException)
            {
                // slotHeld : pas de release si l'annulation est survenue PENDANT l'attente du slot
                // (WaitForDownloadSlotAsync lève avant d'avoir acquis) -> évite une sur-release.
                if (slotHeld) _orchestrator.ReleaseDownloadSlot();
                if (File.Exists(blockFile)) File.Delete(blockFile);
                ClearDownload();
                return;
            }
            catch (Exception ex)
            {
                if (slotHeld) _orchestrator.ReleaseDownloadSlot();
                if (File.Exists(blockFile)) File.Delete(blockFile);

                if (batchAttempt >= maxBatchRetries)
                {
                    Logger.LogError(ex, "[{id}] Batch download failed after {attempts} attempts", downloadId, maxBatchRetries);
                    ClearDownload();
                    return;
                }

                Logger.LogWarning(ex, "[{id}] Batch download failed (attempt {attempt}/{max}), retrying", downloadId, batchAttempt, maxBatchRetries);
                await Task.Delay(JitteredBackoff(batchAttempt, 1000, 12000), token).ConfigureAwait(false);
            }
            } // fin boucle retry

            if (!File.Exists(blockFile))
            {
                // Slot déjà relâché après le download réseau réussi.
                ClearDownload();
                return;
            }

            FileStream? fileBlockStream = null;
            var tasks = new List<Task>();
            try
            {
                if (_downloadStatus.TryGetValue(fileGroup.Key, out var status))
                {
                    status.TransferredFiles = 1;
                    status.DownloadStatus = DownloadStatus.Decompressing;
                }
                fileBlockStream = File.OpenRead(blockFile);
                while (fileBlockStream.Position < fileBlockStream.Length)
                {
                    (string fileHash, long fileLengthBytes) = ReadBlockFileHeader(fileBlockStream);
                    var chunkPosition = fileBlockStream.Position;
                    fileBlockStream.Position += fileLengthBytes;

                    var fileData = fileReplacement.FirstOrDefault(f => string.Equals(f.Hash, fileHash, StringComparison.OrdinalIgnoreCase));
                    if (fileData == null)
                    {
                        Logger.LogWarning("[{id}] Block file contains unknown hash {hash}, skipping", downloadId, fileHash);
                        CompleteDownloadHash(fileHash, false);
                        pendingFallbackHashes.TryRemove(fileHash, out _);
                        continue;
                    }
                    var fileExtension = fileData.GamePaths.Length > 0 ? fileData.GamePaths[0].Split(".")[^1] : "dat";
                    var tmpPath = _fileDbManager.GetCacheFilePath(Guid.NewGuid().ToString(), "tmp");
                    var filePath = _fileDbManager.GetCacheFilePath(fileHash, fileExtension);

                    var capturedHash = fileHash;
                    var capturedLength = fileLengthBytes;
                    var capturedChunkPos = chunkPosition;
                    var capturedTmpPath = tmpPath;
                    var capturedFilePath = filePath;
                    var capturedBlockFile = blockFile;

                    tasks.Add(Task.Run(async () =>
                    {
                        var gate = GetDecompressGate();
                        await gate.WaitAsync(ct).ConfigureAwait(false);
                        var hashSuccess = false;
                        try
                        {
                            using var tmpFileStream = new HashingStream(new FileStream(capturedTmpPath, new FileStreamOptions()
                            {
                                Mode = FileMode.CreateNew,
                                Access = FileAccess.Write,
                                Share = FileShare.None
                            }), System.Security.Cryptography.SHA1.Create());

                            using var fileChunkStream = new FileStream(capturedBlockFile, new FileStreamOptions()
                            {
                                BufferSize = 80000,
                                Mode = FileMode.Open,
                                Access = FileAccess.Read
                            });
                            fileChunkStream.Position = capturedChunkPos;

                            using var innerFileStream = new LimitedStream(fileChunkStream, capturedLength);
                            using var decoder = LZ4Frame.Decode(innerFileStream);
                            long startPos = fileChunkStream.Position;
#pragma warning disable S6966
                            decoder.AsStream().CopyTo(tmpFileStream);
#pragma warning restore S6966
                            long readBytes = fileChunkStream.Position - startPos;

                            if (readBytes != capturedLength)
                                throw new EndOfStreamException();

                            string calculatedHash = BitConverter.ToString(tmpFileStream.Finish()).Replace("-", "", StringComparison.Ordinal);

                            // Comparaison insensible à la casse : alignée sur le chemin CDN
                            // (DecompressAndVerifyLz4) qui utilise OrdinalIgnoreCase. Un hash de
                            // casse différente passait au CDN mais échouait ici → re-download en boucle.
                            if (!calculatedHash.Equals(capturedHash, StringComparison.OrdinalIgnoreCase))
                            {
                                Logger.LogError("Hash mismatch after extracting, got {hash}, expected {expectedHash}", calculatedHash, capturedHash);
                                return;
                            }

                            tmpFileStream.Close();
                            _fileCompactor.RenameAndCompact(capturedFilePath, capturedTmpPath);

                            if (!File.Exists(capturedFilePath))
                            {
                                Logger.LogWarning("[{id}] RenameAndCompact did not produce {path}", downloadId, capturedFilePath);
                                return;
                            }

                            PersistFileToStorage(capturedHash, capturedFilePath, capturedLength);
                            hashSuccess = true;
                        }
                        catch (EndOfStreamException)
                        {
                            Logger.LogWarning("[{id}] Failure to extract file {fileHash}, stream ended prematurely", downloadId, capturedHash);
                        }
                        catch (Exception e)
                        {
                            Logger.LogWarning(e, "[{id}] Error during decompression of {hash}", downloadId, capturedHash);
                        }
                        finally
                        {
                            gate.Release();
                            if (File.Exists(capturedTmpPath))
                                File.Delete(capturedTmpPath);
                            CompleteDownloadHash(capturedHash, hashSuccess);
                            pendingFallbackHashes.TryRemove(capturedHash, out _);
                        }
                    }, ct));
                }

                Task.WaitAll([.. tasks], CancellationToken.None);
            }
            catch (EndOfStreamException)
            {
                Logger.LogDebug("[{id}] Failure to extract file header data, stream ended", downloadId);
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "[{id}] Error during block file read", downloadId);
            }
            finally
            {
                Task.WaitAll([.. tasks], CancellationToken.None);
                // Le slot global a déjà été relâché juste après le download réseau (la décompression
                // ci-dessus n'utilise que GetDecompressGate, pas un slot de download).
                if (fileBlockStream != null)
                    await fileBlockStream.DisposeAsync().ConfigureAwait(false);
                File.Delete(blockFile);
            }
        }).ConfigureAwait(false);

        await WaitForAllTasksAsync(decompressionTasks).ConfigureAwait(false);

        Logger.LogDebug("[{id}] Download end", downloadId);

        if (!pendingFallbackHashes.IsEmpty)
        {
            foreach (var hash in pendingFallbackHashes.Keys)
                CompleteDownloadHash(hash, false);
        }

        ClearDownload();
        }
        finally
        {
            await WaitForAllTasksAsync(decompressionTasks).ConfigureAwait(false);
            foreach (var hash in pendingFallbackHashes.Keys)
                CompleteDownloadHash(hash, false);
        }
    }

    private async Task DownloadFilesInternal(GameObjectHandler gameObjectHandler, List<FileReplacementData> fileReplacement, CancellationToken ct)
    {
        var directDownloads = CurrentDownloads.Where(f => f.HasDirectDownload).ToList();
        var fallbackFiles = new ConcurrentBag<DownloadFileTransfer>();
        var pendingFallbackHashes = new ConcurrentDictionary<string, byte>(StringComparer.OrdinalIgnoreCase);
        var decompressionTasks = new ConcurrentBag<Task>();

        try
        {
        // Circuit breaker: skip direct downloads if too many consecutive failures
        if (_disableDirectDownloads && directDownloads.Count > 0)
        {
            Logger.LogWarning("Direct CDN downloads disabled due to {failures} consecutive failures, using fallback for all {count} files",
                _consecutiveDirectDownloadFailures, directDownloads.Count);
            foreach (var d in directDownloads) fallbackFiles.Add(d);
            directDownloads.Clear();
        }

        // Per-hash CDN failure: skip CDN for specific hashes that previously failed, route to batch/main server
        if (directDownloads.Count > 0)
        {
            var cdnSkipped = directDownloads.Where(f => HasCdnFailure(f.Hash)).ToList();
            if (cdnSkipped.Count > 0)
            {
                Logger.LogInformation("Skipping CDN for {count} hashes with previous CDN failure, routing to main server", cdnSkipped.Count);
                foreach (var d in cdnSkipped)
                {
                    fallbackFiles.Add(d);
                    pendingFallbackHashes.TryAdd(d.Hash, 0);
                }
                directDownloads.RemoveAll(f => HasCdnFailure(f.Hash));
            }
        }

        // Phase 1: CDN direct downloads (download only — decompression is enqueued separately)
        if (directDownloads.Count > 0)
        {
            Logger.LogInformation("Attempting direct CDN download for {count} files", directDownloads.Count);

            const string cdnKey = "cdn-direct";
            _downloadStatus[cdnKey] = new FileDownloadStatus
            {
                DownloadStatus = DownloadStatus.Downloading,
                TotalBytes = directDownloads.Sum(f => f.Total),
                TotalFiles = directDownloads.Count,
                TransferredBytes = 0,
                TransferredFiles = 0
            };

            Mediator.Publish(new DownloadStartedMessage(gameObjectHandler, _downloadStatus));

            var slots = Math.Clamp(_mareConfigService.Current.ParallelDownloads, 1, 20);
            // Le gate global _downloadSemaphore (= ParallelDownloads) est désormais le vrai plafond de
            // concurrence, partagé entre tous les pairs. Inutile de lancer slots*2 workers par pair :
            // l'excédent ne ferait qu'attendre sur le sémaphore global (jusqu'à ~N×40 tâches parquées à
            // 24 pairs). On aligne workerDop sur slots : un pair seul peut saturer le pool, sans gaspillage.
            var workerDop = slots;

            await Parallel.ForEachAsync(directDownloads, new ParallelOptions
            {
                MaxDegreeOfParallelism = workerDop,
                CancellationToken = ct
            }, async (file, token) =>
            {
                var claim = _deduplicator.Claim(file.Hash);

                if (!claim.IsOwner)
                {
                    var ownerSuccess = await claim.Completion.ConfigureAwait(false);
                    if (ownerSuccess)
                    {
                        if (_downloadStatus.TryGetValue(cdnKey, out var status))
                            status.TransferredFiles = status.TransferredFiles + 1;
                    }
                    else
                    {
                        fallbackFiles.Add(file);
                    }
                    return;
                }

                var fileData = fileReplacement.FirstOrDefault(f => string.Equals(f.Hash, file.Hash, StringComparison.OrdinalIgnoreCase));
                var fileExtension = (fileData?.GamePaths is { Length: > 0 } ? fileData.GamePaths[0].Split(".")[^1] : null) ?? "tmp";
                var filePath = _fileDbManager.GetCacheFilePath(file.Hash, fileExtension);
                var lz4TmpPath = filePath + ".lz4tmp";
                var cdnTmpPath = filePath + ".cdntmp";

                Progress<long> progress = new(bytes =>
                {
                    if (_downloadStatus.TryGetValue(cdnKey, out var status))
                        status.AddTransferredBytes(bytes);
                });

                var downloadSuccess = false;
                var goesToFallback = false;
                try
                {
                    // Voir loop CDN principal : on fait passer le download CDN par le gate global
                    // ParallelDownloads (le slider) pour qu'il soit respecté entre tous les pairs.
                    await _orchestrator.WaitForDownloadSlotAsync(token).ConfigureAwait(false);
                    CdnDownloadResult cdnResult;
                    try
                    {
                        cdnResult = await DownloadDirectToLz4TmpAsync(file, lz4TmpPath, progress, token).ConfigureAwait(false);
                    }
                    finally
                    {
                        _orchestrator.ReleaseDownloadSlot();
                    }
                    downloadSuccess = cdnResult == CdnDownloadResult.Success;

                    if (cdnResult == CdnDownloadResult.Success)
                    {
                        // Reset circuit breaker on success
                        Interlocked.Exchange(ref _consecutiveDirectDownloadFailures, 0);
                        _disableDirectDownloads = false;

                        // Enqueue decompression — worker is now FREE for next download
                        EnqueueLimitedTask(decompressionTasks, GetDecompressGate(), _ =>
                        {
                            var success = false;
                            try
                            {
                                success = DecompressAndVerifyLz4(file, lz4TmpPath, cdnTmpPath, filePath);
                                if (success && _downloadStatus.TryGetValue(cdnKey, out var st))
                                    st.TransferredFiles = st.TransferredFiles + 1;
                            }
                            finally
                            {
                                CompleteDownloadHash(file.Hash, success);
                            }
                            return Task.CompletedTask;
                        }, CancellationToken.None);
                    }
                    else if (cdnResult == CdnDownloadResult.NotFound)
                    {
                        // 404 : fichier absent du CDN. Le serveur principal peut en avoir une copie chaude, et ReportCdnMiss (déjà émis) fait re-vérifier S3. Fallback légitime (blacklist time-boxée).
                        RecordCdnFailure(file.Hash);
                        goesToFallback = true;
                        pendingFallbackHashes.TryAdd(file.Hash, 0);
                        fallbackFiles.Add(file);
                    }
                    else
                    {
                        // Échec transitoire (timeout/réseau) : le fichier EST sur le CDN. On NE blackliste PAS et on NE
                        // bascule PAS sur le serveur principal (incapable de servir les fichiers S3-only) ; le hash échoue
                        // ce cycle et le CDN sera réessayé à la prochaine application.
                        var failures = Interlocked.Increment(ref _consecutiveDirectDownloadFailures);
                        if (failures >= MaxConsecutiveDirectDownloadFailures)
                        {
                            _disableDirectDownloads = true;
                            Logger.LogWarning("Direct CDN downloads disabled after {count} consecutive failures", failures);
                        }
                    }
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    // Erreur inattendue → traitée comme transitoire : retry CDN au prochain cycle, pas de blacklist ni de fallback futile.
                    Logger.LogWarning(ex, "CDN download error for {hash}, will retry CDN next cycle", file.Hash);
                    Interlocked.Increment(ref _consecutiveDirectDownloadFailures);
                }
                finally
                {
                    // Complete deduplicator only if download failed (not going to decompression or fallback)
                    if (!downloadSuccess && !goesToFallback)
                    {
                        CompleteDownloadHash(file.Hash, false);
                    }
                }
            }).ConfigureAwait(false);

            _downloadStatus.TryRemove(cdnKey, out _);

            if (!fallbackFiles.IsEmpty)
                Logger.LogInformation("CDN fallback needed for {count} files", fallbackFiles.Count);
        }

        // Phase 2: Batch downloads (non-CDN + fallback from CDN failures)
        var queueFiles = CurrentDownloads.Where(f => !f.HasDirectDownload).Concat(fallbackFiles).ToList();
        if (queueFiles.Count == 0)
        {
            // Wait for any pending decompression before returning
            await WaitForAllTasksAsync(decompressionTasks).ConfigureAwait(false);

            // Clean up pending fallback hashes
            if (!pendingFallbackHashes.IsEmpty)
            {
                Logger.LogWarning("Completing {count} unprocessed fallback hashes with failure", pendingFallbackHashes.Count);
                foreach (var hash in pendingFallbackHashes.Keys)
                    CompleteDownloadHash(hash, false);
            }

            ClearDownload();
            return;
        }

        var downloadGroups = queueFiles
            .GroupBy(f => f.DownloadUri.Host + ":" + f.DownloadUri.Port, StringComparer.Ordinal)
            .ToList();

        foreach (var downloadGroup in downloadGroups)
        {
            _downloadStatus[downloadGroup.Key] = new FileDownloadStatus()
            {
                DownloadStatus = DownloadStatus.Initializing,
                TotalBytes = downloadGroup.Sum(c => c.Total),
                TotalFiles = 1,
                TransferredBytes = 0,
                TransferredFiles = 0
            };
        }

        Mediator.Publish(new DownloadStartedMessage(gameObjectHandler, _downloadStatus));

        await Parallel.ForEachAsync(downloadGroups, new ParallelOptions()
        {
            MaxDegreeOfParallelism = downloadGroups.Count,
            CancellationToken = ct,
        },
        async (fileGroup, token) =>
        {
            var firstFile = fileGroup.First();
            const int maxBatchRetries = 2;
            string blockFile = string.Empty;
            FileInfo? fi = null;

            for (int batchAttempt = 1; batchAttempt <= maxBatchRetries; batchAttempt++)
            {
            var enqueueResult = await RequestBatchEnqueueAsync([.. fileGroup.Select(c => c.Hash)], firstFile.DownloadUri, token).ConfigureAwait(false);
            if (enqueueResult == null)
            {
                // Sans ça les hashes restent claimés côté déduplicateur (30 min) et aucun cooldown
                // n'est posé : le PairHandler reapply en boucle serrée sans jamais progresser.
                FailBatchHashes(fileGroup, pendingFallbackHashes);
                return;
            }

            var requestId = enqueueResult.RequestId;
            var effectiveBaseUri = enqueueResult.BaseUri;

            blockFile = _fileDbManager.GetCacheFilePath(requestId.ToString("N"), "blk");
            fi = new FileInfo(blockFile);
            bool slotHeld = false;
            try
            {
                if (_downloadStatus.TryGetValue(fileGroup.Key, out var slotStatus))
                    slotStatus.DownloadStatus = DownloadStatus.WaitingForSlot;
                await _orchestrator.WaitForDownloadSlotAsync(token).ConfigureAwait(false);
                slotHeld = true;
                if (_downloadStatus.TryGetValue(fileGroup.Key, out slotStatus))
                    slotStatus.DownloadStatus = DownloadStatus.WaitingForQueue;
                Progress<long> progress = new((bytesDownloaded) =>
                {
                    try
                    {
                        if (!_downloadStatus.TryGetValue(fileGroup.Key, out FileDownloadStatus? value)) return;
                        value.AddTransferredBytes(bytesDownloaded);
                        if (value.TransferredBytes > value.TotalBytes)
                        {
                            value.TotalBytes = value.TransferredBytes;
                        }
                    }
                    catch (Exception ex)
                    {
                        Logger.LogWarning(ex, "Could not set download progress");
                    }
                });
                await DownloadAndMungeFileHttpClient(fileGroup.Key, requestId, [.. fileGroup], blockFile, progress, effectiveBaseUri, token).ConfigureAwait(false);
                // Relâcher le slot global dès la fin du download réseau : la décompression (plus bas)
                // a son propre gate (GetDecompressGate) et ne doit pas monopoliser un slot de download.
                _orchestrator.ReleaseDownloadSlot();
                slotHeld = false;
                break; // Succès, sortir de la boucle retry
            }
            catch (OperationCanceledException)
            {
                // slotHeld : pas de release si l'annulation est survenue PENDANT l'attente du slot
                // (WaitForDownloadSlotAsync lève avant d'avoir acquis) -> évite une sur-release.
                if (slotHeld) _orchestrator.ReleaseDownloadSlot();
                if (File.Exists(blockFile))
                    File.Delete(blockFile);
                Logger.LogDebug("{dlName}: Detected cancellation of download for {id}, aborting file extraction", fi?.Name ?? "?", requestId);
                ClearDownload();
                return;
            }
            catch (Exception ex)
            {
                if (slotHeld) _orchestrator.ReleaseDownloadSlot();
                if (File.Exists(blockFile))
                    File.Delete(blockFile);

                if (batchAttempt >= maxBatchRetries)
                {
                    Logger.LogError(ex, "{dlName}: Batch download failed after {attempts} attempts for {id}", fi?.Name ?? "?", maxBatchRetries, requestId);
                    ClearDownload();
                    return;
                }

                Logger.LogWarning(ex, "{dlName}: Batch download failed (attempt {attempt}/{max}), retrying", fi?.Name ?? "?", batchAttempt, maxBatchRetries);
                await Task.Delay(JitteredBackoff(batchAttempt, 1000, 12000), token).ConfigureAwait(false);
            }
            } // fin boucle retry

            // Verify block file exists before attempting decompression
            if (string.IsNullOrEmpty(blockFile) || !File.Exists(blockFile))
            {
                Logger.LogError("{dlName}: Block file does not exist, cannot proceed with decompression", fi?.Name ?? "?");
                // Slot déjà relâché après le download réseau réussi.
                ClearDownload();
                return;
            }

            FileStream? fileBlockStream = null;
            var tasks = new List<Task>();
            try
            {
                if (_downloadStatus.TryGetValue(fileGroup.Key, out var status))
                {
                    status.TransferredFiles = 1;
                    status.DownloadStatus = DownloadStatus.Decompressing;
                }
                fileBlockStream = File.OpenRead(blockFile);
                while (fileBlockStream.Position < fileBlockStream.Length)
                {
                    (string fileHash, long fileLengthBytes) = ReadBlockFileHeader(fileBlockStream);
                    var chunkPosition = fileBlockStream.Position;
                    fileBlockStream.Position += fileLengthBytes;

                    var fileData = fileReplacement.FirstOrDefault(f => string.Equals(f.Hash, fileHash, StringComparison.OrdinalIgnoreCase));
                    if (fileData == null)
                    {
                        Logger.LogWarning("{dlName}: Block file contains unknown hash {hash}, skipping", fi?.Name ?? "?", fileHash);
                        CompleteDownloadHash(fileHash, false);
                        pendingFallbackHashes.TryRemove(fileHash, out _);
                        continue;
                    }
                    var fileExtension = fileData.GamePaths.Length > 0 ? fileData.GamePaths[0].Split(".")[^1] : "dat";
                    var tmpPath = _fileDbManager.GetCacheFilePath(Guid.NewGuid().ToString(), "tmp");
                    var filePath = _fileDbManager.GetCacheFilePath(fileHash, fileExtension);

                    Logger.LogDebug("{dlName}: Decompressing {file}:{le} => {dest}", fi?.Name ?? "?", fileHash, fileLengthBytes, filePath);

                    // Enqueue via decompression gate to bound CPU usage
                    var capturedHash = fileHash;
                    var capturedLength = fileLengthBytes;
                    var capturedChunkPos = chunkPosition;
                    var capturedTmpPath = tmpPath;
                    var capturedFilePath = filePath;
                    var capturedBlockFile = blockFile;

                    tasks.Add(Task.Run(async () =>
                    {
                        var gate = GetDecompressGate();
                        await gate.WaitAsync(ct).ConfigureAwait(false);
                        var hashSuccess = false;
                        try
                        {
                            using var tmpFileStream = new HashingStream(new FileStream(capturedTmpPath, new FileStreamOptions()
                            {
                                Mode = FileMode.CreateNew,
                                Access = FileAccess.Write,
                                Share = FileShare.None
                            }), SHA1.Create());

                            using var fileChunkStream = new FileStream(capturedBlockFile, new FileStreamOptions()
                            {
                                BufferSize = 80000,
                                Mode = FileMode.Open,
                                Access = FileAccess.Read
                            });
                            fileChunkStream.Position = capturedChunkPos;

                            using var innerFileStream = new LimitedStream(fileChunkStream, capturedLength);
                            using var decoder = LZ4Frame.Decode(innerFileStream);
                            long startPos = fileChunkStream.Position;
#pragma warning disable S6966 // LZ4 decoder stream is synchronous, async would just wrap it
                            decoder.AsStream().CopyTo(tmpFileStream);
#pragma warning restore S6966
                            long readBytes = fileChunkStream.Position - startPos;

                            if (readBytes != capturedLength)
                            {
                                throw new EndOfStreamException();
                            }

                            string calculatedHash = BitConverter.ToString(tmpFileStream.Finish()).Replace("-", "", StringComparison.Ordinal);

                            if (!calculatedHash.Equals(capturedHash, StringComparison.Ordinal))
                            {
                                Logger.LogError("Hash mismatch after extracting, got {hash}, expected {expectedHash}, deleting file", calculatedHash, capturedHash);
                                return;
                            }

                            tmpFileStream.Close();
                            _fileCompactor.RenameAndCompact(capturedFilePath, capturedTmpPath);

                            if (!File.Exists(capturedFilePath))
                            {
                                Logger.LogWarning("{dlName}: RenameAndCompact did not produce {path}", fi?.Name ?? "?", capturedFilePath);
                                return;
                            }

                            PersistFileToStorage(capturedHash, capturedFilePath, capturedLength);
                            hashSuccess = true;
                        }
                        catch (EndOfStreamException)
                        {
                            Logger.LogWarning("{dlName}: Failure to extract file {fileHash}, stream ended prematurely", fi?.Name ?? "?", capturedHash);
                        }
                        catch (Exception e)
                        {
                            Logger.LogWarning(e, "{dlName}: Error during decompression of {hash}", fi?.Name ?? "?", capturedHash);

                            foreach (var fr in fileReplacement)
                                Logger.LogWarning(" - {h}: {x}", fr.Hash, fr.GamePaths.Length > 0 ? fr.GamePaths[0] : "(no paths)");
                        }
                        finally
                        {
                            gate.Release();
                            if (File.Exists(capturedTmpPath))
                                File.Delete(capturedTmpPath);
                            CompleteDownloadHash(capturedHash, hashSuccess);
                            pendingFallbackHashes.TryRemove(capturedHash, out _);
                        }
                    }, ct));
                }

                Task.WaitAll([.. tasks], CancellationToken.None);
            }
            catch (EndOfStreamException)
            {
                Logger.LogDebug("{dlName}: Failure to extract file header data, stream ended", fi?.Name ?? "?");
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "{dlName}: Error during block file read", fi?.Name ?? "?");
            }
            finally
            {
                Task.WaitAll([.. tasks], CancellationToken.None);
                // Le slot global a déjà été relâché juste après le download réseau (la décompression
                // ci-dessus n'utilise que GetDecompressGate, pas un slot de download).
                if (fileBlockStream != null)
                    await fileBlockStream.DisposeAsync().ConfigureAwait(false);
                File.Delete(blockFile);
            }
        }).ConfigureAwait(false);

        // Wait for all CDN decompression tasks to complete
        await WaitForAllTasksAsync(decompressionTasks).ConfigureAwait(false);

        Logger.LogDebug("Download end: {id}", gameObjectHandler);

        // Clean up any pending fallback hashes that were never processed
        if (!pendingFallbackHashes.IsEmpty)
        {
            Logger.LogWarning("Completing {count} unprocessed fallback hashes with failure", pendingFallbackHashes.Count);
            foreach (var hash in pendingFallbackHashes.Keys)
            {
                CompleteDownloadHash(hash, false);
            }
        }

        ClearDownload();
        }
        finally
        {

            await WaitForAllTasksAsync(decompressionTasks).ConfigureAwait(false);

            foreach (var hash in pendingFallbackHashes.Keys)
            {
                CompleteDownloadHash(hash, false);
            }
        }
    }

    private sealed record BatchEnqueueResult(Guid RequestId, Uri BaseUri);

    /// <summary>
    /// Demande au serveur de préparer un lot de fichiers, avec bascule sur le serveur principal si le
    /// CDN répond en erreur <b>ou</b> s'il est injoignable (DNS, timeout, TLS) : SendRequestAsync lève
    /// dans ce cas, et le fallback conditionné au seul code HTTP n'était alors jamais atteint.
    /// </summary>
    private async Task<BatchEnqueueResult?> RequestBatchEnqueueAsync(List<string> hashes, Uri cdnUri, CancellationToken token, string? downloadId = null)
    {
        var mainServerUri = _orchestrator.FilesCdnUri!;
        var hasCdnFailedHashes = hashes.Exists(HasCdnFailure);
        var skipCdn = _disableCdnEnqueue || _disableDirectDownloads || hasCdnFailedHashes;
        var effectiveBaseUri = skipCdn ? mainServerUri : cdnUri;
        var logPrefix = string.IsNullOrEmpty(downloadId) ? string.Empty : "[" + downloadId + "] ";

        if (skipCdn)
        {
            Logger.LogInformation("{prefix}Routing batch to main server (enqueue={enqueueDisabled}, direct={directDisabled}, cdnFailed={cdnFailed}) for {count} files",
                logPrefix, _disableCdnEnqueue, _disableDirectDownloads, hasCdnFailedHashes, hashes.Count);
        }

        var (success, responseBody) = await TryEnqueueAsync(effectiveBaseUri, hashes, logPrefix, token).ConfigureAwait(false);

        if (!success && effectiveBaseUri == cdnUri && cdnUri != mainServerUri)
        {
            var failures = Interlocked.Increment(ref _consecutiveCdnEnqueueFailures);
            if (failures >= MaxConsecutiveCdnEnqueueFailures)
            {
                _disableCdnEnqueue = true;
                Logger.LogWarning("CDN enqueue disabled after {count} consecutive failures", failures);
            }

            Logger.LogWarning("{prefix}CDN enqueue failed, trying main server fallback", logPrefix);
            effectiveBaseUri = mainServerUri;
            (success, responseBody) = await TryEnqueueAsync(effectiveBaseUri, hashes, logPrefix, token).ConfigureAwait(false);
        }

        if (!success)
        {
            Logger.LogError("{prefix}Enqueue request failed: {body}", logPrefix, responseBody);
            return null;
        }

        if (effectiveBaseUri == cdnUri && _consecutiveCdnEnqueueFailures > 0)
        {
            Interlocked.Exchange(ref _consecutiveCdnEnqueueFailures, 0);
            _disableCdnEnqueue = false;
        }

        if (!Guid.TryParse(responseBody.Trim('"'), out Guid requestId))
        {
            Logger.LogError("{prefix}Enqueue request returned invalid GUID: {body}", logPrefix, responseBody);
            return null;
        }

        Logger.LogDebug("{prefix}GUID {requestId} for {n} files on server {uri}", logPrefix, requestId, hashes.Count, effectiveBaseUri);
        return new BatchEnqueueResult(requestId, effectiveBaseUri);
    }

    private async Task<(bool Success, string Body)> TryEnqueueAsync(Uri baseUri, List<string> hashes, string logPrefix, CancellationToken token)
    {
        try
        {
            using var response = await _orchestrator.SendRequestAsync(HttpMethod.Post, MareFiles.RequestEnqueueFullPath(baseUri), hashes, token).ConfigureAwait(false);
            var body = await response.Content.ReadAsStringAsync(token).ConfigureAwait(false);
            Logger.LogInformation("{prefix}Sent request for {n} files on server {uri} with result {result}", logPrefix, hashes.Count, baseUri, body);
            return (response.IsSuccessStatusCode, body);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "{prefix}Enqueue request to {uri} failed", logPrefix, baseUri);
            return (false, ex.Message);
        }
    }

    private async Task<List<DownloadFileDto>> FilesGetSizes(List<string> hashes, CancellationToken ct)
    {
        if (!_orchestrator.IsInitialized) throw new InvalidOperationException("FileTransferManager is not initialized");
        // Prefer POST with JSON body (new server behavior). Add robust diagnostics and fallbacks for older deployments.
        var uri = MareFiles.ServerFilesGetSizesFullPath(_orchestrator.FilesCdnUri!);

        // Try POST first
        HttpResponseMessage? postResponse = null;
        try
        {
            postResponse = await _orchestrator
                .SendRequestAsync(HttpMethod.Post, uri, hashes, ct)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "getFileSizes POST threw before response");
        }

        if (postResponse == null || !postResponse.IsSuccessStatusCode)
        {
            // If server hasn't been updated yet, it may reject POST. Retry with GET once.
            var postStatus = postResponse?.StatusCode;
            if (postStatus == HttpStatusCode.NotFound || postStatus == HttpStatusCode.MethodNotAllowed)
            {
                try
                {
                    var getFallback = await _orchestrator
                        .SendRequestAsync(HttpMethod.Get, uri, hashes, ct)
                        .ConfigureAwait(false);

                    getFallback.EnsureSuccessStatusCode();

                    // Old servers might return text/plain with a double-serialized JSON string
                    var body = await getFallback.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                    return ParseDownloadFileDtoList(body);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    Logger.LogWarning(ex, "getFileSizes GET fallback failed");
                }
            }
            try
            {
                var getRetry = await _orchestrator
                    .SendRequestAsync(HttpMethod.Get, uri, hashes, ct)
                    .ConfigureAwait(false);
                if (getRetry.IsSuccessStatusCode)
                {
                    var body = await getRetry.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                    return ParseDownloadFileDtoList(body);
                }
                else
                {
                    var getBody = await SafeReadBodySnippetAsync(getRetry, ct).ConfigureAwait(false);
                    Logger.LogWarning("getFileSizes GET retry failed: {code} {reason}. Headers: {headers}. Body: {body}",
                        (int)getRetry.StatusCode, getRetry.ReasonPhrase ?? string.Empty,
                        string.Join("; ", getRetry.Headers.Select(h => h.Key + ":" + string.Join(",", h.Value))),
                        getBody);
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                Logger.LogWarning(ex, "getFileSizes GET retry threw");
            }
            if (postResponse != null)
            {
                var bodySnippet = await SafeReadBodySnippetAsync(postResponse, ct).ConfigureAwait(false);
                var reason = postResponse.ReasonPhrase ?? string.Empty;
                Logger.LogWarning("getFileSizes POST failed: {code} {reason}. Headers: {headers}. Body: {body}",
                    (int)postResponse.StatusCode, reason,
                    string.Join("; ", postResponse.Headers.Select(h => h.Key + ":" + string.Join(",", h.Value))),
                    bodySnippet);
            }

            return hashes.Select(h => new DownloadFileDto
            {
                Hash = h,
                FileExists = false,
                Url = string.Empty,
                Size = 0,
                IsForbidden = false,
                ForbiddenBy = string.Empty
            }).ToList();
        }

        try
        {
            var opts = new System.Text.Json.JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            };
            return await postResponse.Content
                .ReadFromJsonAsync<List<DownloadFileDto>>(options: opts, cancellationToken: ct)
                .ConfigureAwait(false) ?? [];
        }
        catch (System.Text.Json.JsonException)
        {
            var body = await postResponse.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            try
            {
                return ParseDownloadFileDtoList(body);
            }
            catch (Exception ex)
            {
                var snippet = body.Length > 2048 ? body[..2048] + "…" : body;
                throw new System.Text.Json.JsonException($"Failed to parse getFileSizes response. Snippet: {snippet}", ex);
            }
        }
    }

    private static List<DownloadFileDto> ParseDownloadFileDtoList(string body)
    {
        string json = body;
        try
        {
            if (!string.IsNullOrEmpty(body) && body.Length >= 2 && body[0] == '"' && body[^1] == '"')
            {
                var inner = System.Text.Json.JsonSerializer.Deserialize<string>(body);
                if (!string.IsNullOrEmpty(inner)) json = inner;
            }
        }
        catch
        {
            // ignore, fall back to using original body
        }

        var opts = new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        var list = System.Text.Json.JsonSerializer.Deserialize<List<DownloadFileDto>>(json, opts);
        return list ?? [];
    }

    private static async Task<string> SafeReadBodySnippetAsync(HttpResponseMessage response, CancellationToken ct)
    {
        try
        {
            var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            if (string.IsNullOrEmpty(body)) return string.Empty;
            return body.Length > 2048 ? body[..2048] + "…" : body;
        }
        catch
        {
            return string.Empty;
        }
    }

    private void PersistFileToStorage(string fileHash, string filePath, long? compressedSize = null)
    {
        try
        {
            var entry = _fileDbManager.CreateCacheEntry(filePath, fileHash);
            if (entry != null && !string.Equals(entry.Hash, fileHash, StringComparison.OrdinalIgnoreCase))
            {
                _fileDbManager.RemoveHashedFile(entry.Hash, entry.PrefixedFilePath);
                entry = null;
            }
            if (entry != null)
                entry.CompressedSize = compressedSize;
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Error creating cache entry");
        }
    }

    private async Task WaitForDownloadReady(List<DownloadFileTransfer> downloadFileTransfer, Guid requestId, Uri effectiveBaseUri, CancellationToken downloadCt)
    {
        CancellationTokenSource? localTimeoutCts = null;
        CancellationTokenSource? composite = null;
        try
        {
            localTimeoutCts = new();
            localTimeoutCts.CancelAfter(TimeSpan.FromSeconds(5));
            composite = CancellationTokenSource.CreateLinkedTokenSource(downloadCt, localTimeoutCts.Token);

            while (!_orchestrator.IsDownloadReady(requestId))
            {
                try
                {
                    await Task.Delay(250, composite.Token).ConfigureAwait(false);
                }
                catch (TaskCanceledException)
                {
                    if (downloadCt.IsCancellationRequested) throw;

                    try
                    {
                        using var req = await _orchestrator.SendRequestAsync(HttpMethod.Get, MareFiles.RequestCheckQueueFullPath(effectiveBaseUri, requestId),
                            downloadFileTransfer.Select(c => c.Hash).ToList(), downloadCt).ConfigureAwait(false);
                        req.EnsureSuccessStatusCode();
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        Logger.LogWarning(ex, "CheckQueue failed for {requestId}, will retry", requestId);
                    }

                    localTimeoutCts.Dispose();
                    composite.Dispose();
                    localTimeoutCts = new();
                    localTimeoutCts.CancelAfter(TimeSpan.FromSeconds(5));
                    composite = CancellationTokenSource.CreateLinkedTokenSource(downloadCt, localTimeoutCts.Token);
                }
            }

            Logger.LogDebug("Download {requestId} ready", requestId);
        }
        finally
        {
            localTimeoutCts?.Dispose();
            composite?.Dispose();

            if (downloadCt.IsCancellationRequested)
            {
                try
                {
                    await _orchestrator.SendRequestAsync(HttpMethod.Get, MareFiles.RequestCancelFullPath(effectiveBaseUri, requestId)).ConfigureAwait(false);
                }
                catch
                {
                    // ignore whatever happens here
                }
            }
            _orchestrator.ClearDownloadRequest(requestId);
        }
    }
}
