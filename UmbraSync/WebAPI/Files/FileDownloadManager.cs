using Dalamud.Utility;
using K4os.Compression.LZ4.Streams;
using Microsoft.Extensions.Logging;
using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Collections.Concurrent;
using System.Security.Cryptography;
using UmbraSync.API.Data;
using UmbraSync.API.Dto.Files;
using UmbraSync.API.Routes;
using UmbraSync.FileCache;
using UmbraSync.MareConfiguration;
using UmbraSync.PlayerData.Handlers;
using UmbraSync.Services.Mediator;
using UmbraSync.Utils;
using UmbraSync.WebAPI.Files.Models;

namespace UmbraSync.WebAPI.Files;

public partial class FileDownloadManager : DisposableMediatorSubscriberBase
{
    private readonly ConcurrentDictionary<string, FileDownloadStatus> _downloadStatus;
    private readonly FileCompactor _fileCompactor;
    private readonly FileCacheManager _fileDbManager;
    private readonly MareConfigService _mareConfigService;
    private readonly FileTransferOrchestrator _orchestrator;
    private readonly FileDownloadDeduplicator _deduplicator;
    private readonly ConcurrentDictionary<ThrottledStream, byte> _activeDownloadStreams;
    private readonly Lock _queueLock = new();
    private readonly SemaphoreSlim _decompressGate;
    private SemaphoreSlim? _downloadQueueSemaphore;
    private int _downloadQueueCapacity = -1;

    // Circuit breaker for direct CDN downloads - disable after consecutive failures
    private const int MaxConsecutiveDirectDownloadFailures = 3;
    private int _consecutiveDirectDownloadFailures;
    private bool _disableDirectDownloads;

    // Circuit breaker for CDN enqueue requests - fallback to main server after consecutive failures
    private const int MaxConsecutiveCdnEnqueueFailures = 2;
    private int _consecutiveCdnEnqueueFailures;
    private bool _disableCdnEnqueue;

    public FileDownloadManager(ILogger<FileDownloadManager> logger, MareMediator mediator,
        FileTransferOrchestrator orchestrator,
        FileCacheManager fileCacheManager, FileCompactor fileCompactor, MareConfigService mareConfigService,
        FileDownloadDeduplicator deduplicator) : base(logger, mediator)
    {
        _downloadStatus = new ConcurrentDictionary<string, FileDownloadStatus>(StringComparer.Ordinal);
        _orchestrator = orchestrator;
        _fileDbManager = fileCacheManager;
        _fileCompactor = fileCompactor;
        _mareConfigService = mareConfigService;
        _deduplicator = deduplicator;
        _activeDownloadStreams = new();
        _decompressGate = new SemaphoreSlim(CalculateDecompressionLimit(mareConfigService.Current.ParallelDownloads));

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

    public List<DownloadFileTransfer> CurrentDownloads { get; private set; } = [];

    public List<FileTransfer> ForbiddenTransfers => _orchestrator.ForbiddenTransfers;

    public bool IsDownloading => CurrentDownloads.Any();

    public void ClearDownload()
    {
        CurrentDownloads.Clear();
        _downloadStatus.Clear();
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
            if (!readHash) hashName.Add(readChar);
            else fileLength.Add(readChar);
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

        ThrottledStream? stream = null;
        try
        {
            var fileStream = File.Create(tempPath);
            await using (fileStream.ConfigureAwait(false))
            {
                var bufferSize = response.Content.Headers.ContentLength > 1024 * 1024 ? 65536 : 8196;
                var buffer = new byte[bufferSize];

                var bytesRead = 0;
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
        }
    }

    // --- CDN Direct Download: Phase 1 (download compressed to .lz4tmp) ---

    private async Task<bool> DownloadDirectToLz4TmpAsync(DownloadFileTransfer file, string lz4TmpPath, IProgress<long> progress, CancellationToken ct)
    {
        if (!file.HasDirectDownload || file.DirectDownloadUri == null)
            return false;

        var url = file.DirectDownloadUri.ToString();
        Logger.LogDebug("Direct CDN download (compressed): {hash} from {url}", file.Hash, url);

        if (File.Exists(lz4TmpPath))
        {
            try { File.Delete(lz4TmpPath); }
            catch (Exception ex) { Logger.LogWarning(ex, "Cannot delete existing .lz4tmp file {path}", lz4TmpPath); }
        }

        const int maxRetries = 3;
        const int perFileTimeoutSeconds = 5;
        var retryDelay = TimeSpan.FromSeconds(2);

        for (int attempt = 1; attempt <= maxRetries; attempt++)
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(perFileTimeoutSeconds));
            var linkedToken = timeoutCts.Token;

            try
            {
                Logger.LogDebug("Attempt {attempt}/{max} - Direct CDN download for {hash}", attempt, maxRetries, file.Hash);

                var response = await _orchestrator.SendRequestAsync(HttpMethod.Get, new Uri(url), linkedToken, HttpCompletionOption.ResponseHeadersRead).ConfigureAwait(false);

                if (response.StatusCode == HttpStatusCode.NotFound)
                {
                    Logger.LogDebug("Direct CDN 404 for {hash}, will fallback", file.Hash);
                    return false;
                }

                response.EnsureSuccessStatusCode();

                ThrottledStream? throttledStream = null;
                try
                {
                    var limit = _orchestrator.DownloadLimitPerSlot();
                    throttledStream = new ThrottledStream(await response.Content.ReadAsStreamAsync(linkedToken).ConfigureAwait(false), limit);
                    _activeDownloadStreams.TryAdd(throttledStream, 0);

                    var bufferSize = response.Content.Headers.ContentLength > 1024 * 1024 ? 65536 : 8196;
                    var buffer = new byte[bufferSize];

                    using var fileStream = new FileStream(lz4TmpPath, FileMode.Create, FileAccess.Write, FileShare.None);
                    int bytesRead;
                    while ((bytesRead = await throttledStream.ReadAsync(buffer, linkedToken).ConfigureAwait(false)) > 0)
                    {
                        linkedToken.ThrowIfCancellationRequested();
                        await fileStream.WriteAsync(buffer.AsMemory(0, bytesRead), linkedToken).ConfigureAwait(false);
                        progress.Report(bytesRead);
                    }

                    Logger.LogDebug("CDN download (compressed) finished for {hash}", file.Hash);
                    #pragma warning disable S1751 // Retry loop: returns on success, catches retry on failure
                    return true;
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
                return false;
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
                    return false;
                }

                await Task.Delay(retryDelay, ct).ConfigureAwait(false);
            }
            catch (TaskCanceledException) when (!ct.IsCancellationRequested)
            {
                Logger.LogWarning("CDN download timed out ({timeout}s) for {hash}. Attempt {attempt}/{max}", perFileTimeoutSeconds, file.Hash, attempt, maxRetries);
                if (File.Exists(lz4TmpPath)) try { File.Delete(lz4TmpPath); } catch (Exception) { /* best-effort cleanup */ }

                if (attempt >= maxRetries)
                {
                    Logger.LogWarning("Max retries reached for CDN download of {hash}, will fallback", file.Hash);
                    return false;
                }

                await Task.Delay(retryDelay, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Logger.LogWarning(ex, "Direct CDN download failed for {hash} (attempt {attempt}/{max}), will fallback", file.Hash, attempt, maxRetries);
                if (File.Exists(lz4TmpPath)) try { File.Delete(lz4TmpPath); } catch (Exception) { /* best-effort cleanup */ }
                return false;
            }
        }

        return false;
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

    public async Task<List<DownloadFileTransfer>> InitiateDownloadList(GameObjectHandler gameObjectHandler, List<FileReplacementData> fileReplacement, CancellationToken ct)
    {
        Logger.LogDebug("Download start: {id}", gameObjectHandler.Name);

        List<DownloadFileDto> downloadFileInfoFromService =
        [
            .. await FilesGetSizes(fileReplacement.Select(f => f.Hash).Distinct(StringComparer.Ordinal).ToList(), ct).ConfigureAwait(false),
        ];

        Logger.LogDebug("Files with size 0 or less: {files}", string.Join(", ", downloadFileInfoFromService.Where(f => f.Size <= 0).Select(f => f.Hash)));

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

            var slots = Math.Clamp(_mareConfigService.Current.ParallelDownloads, 1, 10);
            var workerDop = Math.Clamp(slots * 2, 2, 16);

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
                var fileExtension = fileData?.GamePaths[0].Split(".")[^1] ?? "tmp";
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
                    downloadSuccess = await DownloadDirectToLz4TmpAsync(file, lz4TmpPath, progress, token).ConfigureAwait(false);

                    if (downloadSuccess)
                    {
                        // Reset circuit breaker on success
                        Interlocked.Exchange(ref _consecutiveDirectDownloadFailures, 0);
                        _disableDirectDownloads = false;

                        // Enqueue decompression — worker is now FREE for next download
                        EnqueueLimitedTask(decompressionTasks, _decompressGate, _ =>
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
                                _deduplicator.Complete(file.Hash, success);
                            }
                            return Task.CompletedTask;
                        }, CancellationToken.None);
                    }
                    else
                    {
                        // Increment failure counter and check circuit breaker threshold
                        var failures = Interlocked.Increment(ref _consecutiveDirectDownloadFailures);
                        if (failures >= MaxConsecutiveDirectDownloadFailures)
                        {
                            _disableDirectDownloads = true;
                            Logger.LogWarning("Direct CDN downloads disabled after {count} consecutive failures", failures);
                        }

                        goesToFallback = true;
                        pendingFallbackHashes.TryAdd(file.Hash, 0);
                        fallbackFiles.Add(file);
                    }
                }
                finally
                {
                    // Complete deduplicator only if download failed (not going to decompression or fallback)
                    if (!downloadSuccess && !goesToFallback)
                    {
                        _deduplicator.Complete(file.Hash, false);
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
                    _deduplicator.Complete(hash, false);
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
            var fileCount = fileGroup.Count();

            // Determine effective base URI: skip CDN if circuit breaker is active
            var cdnUri = firstFile.DownloadUri;
            var mainServerUri = _orchestrator.FilesCdnUri!;
            var effectiveBaseUri = _disableCdnEnqueue ? mainServerUri : cdnUri;

            if (_disableCdnEnqueue)
            {
                Logger.LogWarning("CDN enqueue disabled due to {failures} consecutive failures, using main server for {count} files",
                    _consecutiveCdnEnqueueFailures, fileCount);
            }

            // let server predownload files
            var requestIdResponse = await _orchestrator.SendRequestAsync(HttpMethod.Post, MareFiles.RequestEnqueueFullPath(effectiveBaseUri),
                fileGroup.Select(c => c.Hash), token).ConfigureAwait(false);
            var responseBody = await requestIdResponse.Content.ReadAsStringAsync(token).ConfigureAwait(false);
            Logger.LogInformation("Sent request for {n} files on server {uri} with result {result}", fileCount, effectiveBaseUri,
                responseBody);

            // If CDN enqueue failed, try fallback to main server
            if (!requestIdResponse.IsSuccessStatusCode && effectiveBaseUri == cdnUri && cdnUri != mainServerUri)
            {
                Logger.LogWarning("CDN enqueue failed with status {status}, trying main server fallback", requestIdResponse.StatusCode);
                var failures = Interlocked.Increment(ref _consecutiveCdnEnqueueFailures);
                if (failures >= MaxConsecutiveCdnEnqueueFailures)
                {
                    _disableCdnEnqueue = true;
                    Logger.LogWarning("CDN enqueue disabled after {count} consecutive failures", failures);
                }

                effectiveBaseUri = mainServerUri;
                requestIdResponse = await _orchestrator.SendRequestAsync(HttpMethod.Post, MareFiles.RequestEnqueueFullPath(effectiveBaseUri),
                    fileGroup.Select(c => c.Hash), token).ConfigureAwait(false);
                responseBody = await requestIdResponse.Content.ReadAsStringAsync(token).ConfigureAwait(false);
                Logger.LogInformation("Main server fallback for {n} files: {result}", fileCount, responseBody);
            }

            if (!requestIdResponse.IsSuccessStatusCode)
            {
                Logger.LogError("Enqueue request failed with status {status}: {body}", requestIdResponse.StatusCode, responseBody);
                return;
            }

            // CDN enqueue succeeded - reset counter if it was the CDN
            if (effectiveBaseUri == cdnUri && _consecutiveCdnEnqueueFailures > 0)
            {
                Interlocked.Exchange(ref _consecutiveCdnEnqueueFailures, 0);
                _disableCdnEnqueue = false;
            }

            if (!Guid.TryParse(responseBody.Trim('"'), out Guid requestId))
            {
                Logger.LogError("Enqueue request returned invalid GUID: {body}", responseBody);
                return;
            }

            Logger.LogDebug("GUID {requestId} for {n} files on server {uri}", requestId, fileCount, effectiveBaseUri);

            var blockFile = _fileDbManager.GetCacheFilePath(requestId.ToString("N"), "blk");
            FileInfo fi = new(blockFile);
            try
            {
                if (_downloadStatus.TryGetValue(fileGroup.Key, out var slotStatus))
                    slotStatus.DownloadStatus = DownloadStatus.WaitingForSlot;
                await _orchestrator.WaitForDownloadSlotAsync(token).ConfigureAwait(false);
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
            }
            catch (OperationCanceledException)
            {
                _orchestrator.ReleaseDownloadSlot();
                if (File.Exists(blockFile))
                    File.Delete(blockFile);
                Logger.LogDebug("{dlName}: Detected cancellation of download for {id}, aborting file extraction", fi.Name, requestId);
                ClearDownload();
                return;
            }
            catch (Exception ex)
            {
                _orchestrator.ReleaseDownloadSlot();
                if (File.Exists(blockFile))
                    File.Delete(blockFile);
                Logger.LogError(ex, "{dlName}: Error during download of {id}", fi.Name, requestId);
                ClearDownload();
                return;
            }

            // Verify block file exists before attempting decompression
            if (!File.Exists(blockFile))
            {
                Logger.LogError("{dlName}: Block file {blockFile} does not exist, cannot proceed with decompression for {id}", fi.Name, fi.Name, requestId);
                _orchestrator.ReleaseDownloadSlot();
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

                    var fileExtension = fileReplacement.First(f => string.Equals(f.Hash, fileHash, StringComparison.OrdinalIgnoreCase)).GamePaths[0].Split(".")[^1];
                    var tmpPath = _fileDbManager.GetCacheFilePath(Guid.NewGuid().ToString(), "tmp");
                    var filePath = _fileDbManager.GetCacheFilePath(fileHash, fileExtension);

                    Logger.LogDebug("{dlName}: Decompressing {file}:{le} => {dest}", fi.Name, fileHash, fileLengthBytes, filePath);

                    // Enqueue via decompression gate to bound CPU usage
                    var capturedHash = fileHash;
                    var capturedLength = fileLengthBytes;
                    var capturedChunkPos = chunkPosition;
                    var capturedTmpPath = tmpPath;
                    var capturedFilePath = filePath;
                    var capturedBlockFile = blockFile;

                    tasks.Add(Task.Run(async () =>
                    {
                        await _decompressGate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
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
                            PersistFileToStorage(capturedHash, capturedFilePath, capturedLength);
                            hashSuccess = true;
                        }
                        catch (EndOfStreamException)
                        {
                            Logger.LogWarning("{dlName}: Failure to extract file {fileHash}, stream ended prematurely", fi.Name, capturedHash);
                        }
                        catch (Exception e)
                        {
                            Logger.LogWarning(e, "{dlName}: Error during decompression of {hash}", fi.Name, capturedHash);

                            foreach (var fr in fileReplacement)
                                Logger.LogWarning(" - {h}: {x}", fr.Hash, fr.GamePaths[0]);
                        }
                        finally
                        {
                            _decompressGate.Release();
                            if (File.Exists(capturedTmpPath))
                                File.Delete(capturedTmpPath);
                            _deduplicator.Complete(capturedHash, hashSuccess);
                            pendingFallbackHashes.TryRemove(capturedHash, out _);
                        }
                    }, CancellationToken.None));
                }

                Task.WaitAll([.. tasks], CancellationToken.None);
            }
            catch (EndOfStreamException)
            {
                Logger.LogDebug("{dlName}: Failure to extract file header data, stream ended", fi.Name);
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "{dlName}: Error during block file read", fi.Name);
            }
            finally
            {
                Task.WaitAll([.. tasks], CancellationToken.None);
                _orchestrator.ReleaseDownloadSlot();
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
                _deduplicator.Complete(hash, false);
            }
        }

        ClearDownload();
        }
        finally
        {

            await WaitForAllTasksAsync(decompressionTasks).ConfigureAwait(false);

            foreach (var hash in pendingFallbackHashes.Keys)
            {
                _deduplicator.Complete(hash, false);
            }
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
        bool alreadyCancelled = false;
        try
        {
            CancellationTokenSource localTimeoutCts = new();
            localTimeoutCts.CancelAfter(TimeSpan.FromSeconds(5));
            CancellationTokenSource composite = CancellationTokenSource.CreateLinkedTokenSource(downloadCt, localTimeoutCts.Token);

            while (!_orchestrator.IsDownloadReady(requestId))
            {
                try
                {
                    await Task.Delay(250, composite.Token).ConfigureAwait(false);
                }
                catch (TaskCanceledException)
                {
                    if (downloadCt.IsCancellationRequested) throw;

                    var req = await _orchestrator.SendRequestAsync(HttpMethod.Get, MareFiles.RequestCheckQueueFullPath(effectiveBaseUri, requestId),
                        downloadFileTransfer.Select(c => c.Hash).ToList(), downloadCt).ConfigureAwait(false);
                    req.EnsureSuccessStatusCode();
                    localTimeoutCts.Dispose();
                    composite.Dispose();
                    localTimeoutCts = new();
                    localTimeoutCts.CancelAfter(TimeSpan.FromSeconds(5));
                    composite = CancellationTokenSource.CreateLinkedTokenSource(downloadCt, localTimeoutCts.Token);
                }
            }

            localTimeoutCts.Dispose();
            composite.Dispose();

            Logger.LogDebug("Download {requestId} ready", requestId);
        }
        catch (TaskCanceledException)
        {
            try
            {
                await _orchestrator.SendRequestAsync(HttpMethod.Get, MareFiles.RequestCancelFullPath(effectiveBaseUri, requestId)).ConfigureAwait(false);
                alreadyCancelled = true;
            }
            catch
            {
                // ignore whatever happens here
            }

            throw;
        }
        finally
        {
            if (downloadCt.IsCancellationRequested && !alreadyCancelled)
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
