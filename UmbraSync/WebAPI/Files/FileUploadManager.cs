using Microsoft.Extensions.Logging;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using UmbraSync.API.Data;
using UmbraSync.API.Dto.Files;
using UmbraSync.API.Routes;
using UmbraSync.FileCache;
using UmbraSync.Services.Mediator;
using UmbraSync.Services.ServerConfiguration;
using UmbraSync.UI;
using System.Collections.Concurrent;
using UmbraSync.WebAPI.Files.Models;


namespace UmbraSync.WebAPI.Files;

public sealed class FileUploadManager : DisposableMediatorSubscriberBase
{
    private readonly FileCacheManager _fileDbManager;
    private readonly FileTransferOrchestrator _orchestrator;
    private readonly ServerConfigurationManager _serverManager;
    private readonly ConcurrentDictionary<string, DateTime> _verifiedUploadedHashes = new(StringComparer.Ordinal);
    private CancellationTokenSource? _uploadCancellationTokenSource = new();
    private const int MaxParallelUploads = 2;
    private const int MaxRateLimitAttempts = 6;
    private static readonly TimeSpan VerifiedUploadTtl = TimeSpan.FromMinutes(1);

    public FileUploadManager(ILogger<FileUploadManager> logger, MareMediator mediator,
        FileTransferOrchestrator orchestrator,
        FileCacheManager fileDbManager,
        ServerConfigurationManager serverManager) : base(logger, mediator)
    {
        _orchestrator = orchestrator;
        _fileDbManager = fileDbManager;
        _serverManager = serverManager;

        Mediator.Subscribe<DisconnectedMessage>(this, (msg) =>
        {
            Reset();
        });
    }

    public bool IsInitialized => _orchestrator.IsInitialized;

    public List<FileTransfer> CurrentUploads { get; } = [];
    public bool IsUploading => CurrentUploads.Count > 0;

    public bool CancelUpload()
    {
        if (CurrentUploads.Count > 0)
        {
            Logger.LogDebug("Cancelling current upload");
            _uploadCancellationTokenSource?.Cancel();
            _uploadCancellationTokenSource?.Dispose();
            _uploadCancellationTokenSource = null;
            CurrentUploads.Clear();
            return true;
        }

        return false;
    }

    public async Task DeleteAllFiles()
    {
        if (!_orchestrator.IsInitialized) throw new InvalidOperationException("FileTransferManager is not initialized");

        await _orchestrator.SendRequestAsync(HttpMethod.Post, MareFiles.ServerFilesDeleteAllFullPath(_orchestrator.FilesCdnUri!)).ConfigureAwait(false);
    }

    public async Task<List<string>> UploadFiles(List<string> hashesToUpload, IProgress<string> progress, CancellationToken? ct = null, IProgress<long>? uploadedBytesProgress = null)
    {
        Logger.LogDebug("Trying to upload files");
        var filesPresentLocally = hashesToUpload.Where(h => _fileDbManager.GetFileCacheByHash(h) != null).ToHashSet(StringComparer.Ordinal);
        var locallyMissingFiles = hashesToUpload.Except(filesPresentLocally, StringComparer.Ordinal).ToList();
        if (locallyMissingFiles.Count > 0)
        {
            return locallyMissingFiles;
        }

        progress.Report(string.Format(System.Globalization.CultureInfo.CurrentCulture,
            Localization.Loc.Get("Settings.Transfer.Precache.Progress.Starting"), filesPresentLocally.Count));

        var filesToUpload = await FilesSend([.. filesPresentLocally], [], ct ?? CancellationToken.None).ConfigureAwait(false);

        if (filesToUpload.Exists(f => f.IsForbidden))
        {
            return [.. filesToUpload.Where(f => f.IsForbidden).Select(f => f.Hash)];
        }

        var token = ct ?? CancellationToken.None;
        long uploadedTotal = 0;
        int completed = 0;
        var total = filesToUpload.Count;
        using (var uploadSemaphore = new SemaphoreSlim(MaxParallelUploads))
        {
            var uploadTasks = filesToUpload.Select(async file =>
            {
                await uploadSemaphore.WaitAsync(token).ConfigureAwait(false);
                try
                {
                    var idx = Interlocked.Increment(ref completed);
                    progress.Report(string.Format(System.Globalization.CultureInfo.CurrentCulture,
                        Localization.Loc.Get("Settings.Transfer.Precache.Progress.Uploading"), idx, total));
                    Logger.LogDebug("[{hash}] Compressing", file);
                    var data = await _fileDbManager.GetCompressedFileData(file.Hash, token).ConfigureAwait(false);
                    Logger.LogDebug("[{hash}] Starting upload for {filePath}", data.Item1, _fileDbManager.GetFileCacheByHash(data.Item1)!.ResolvedFilepath);
                    await UploadFile(data.Item2, file.Hash, false, token).ConfigureAwait(false);
                    var newTotal = Interlocked.Add(ref uploadedTotal, data.Item2.LongLength);
                    uploadedBytesProgress?.Report(newTotal);
                }
                finally
                {
                    uploadSemaphore.Release();
                }
            }).ToList();
            await Task.WhenAll(uploadTasks).ConfigureAwait(false);
        }

        return [];
    }

    public async Task<CharacterData> UploadFiles(CharacterData data, List<UserData> visiblePlayers, CancellationToken ct = default)
    {
        if (!_orchestrator.IsInitialized)
        {
            Logger.LogDebug("FileTransferOrchestrator pas encore initialisé, attente avant upload de {hash}", data.DataHash.Value);
            if (!await _orchestrator.WaitForInitializationAsync(TimeSpan.FromSeconds(10), ct).ConfigureAwait(false))
            {
                // Surtout ne pas retourner data tel quel : l'appelant pousserait un manifest
                // référençant des fichiers jamais uploadés, que les pairs ne pourraient
                // jamais télécharger (pièces de mod manquantes en boucle).
                throw new InvalidOperationException(
                    $"FileTransferOrchestrator non initialisé, upload impossible pour {data.DataHash.Value}");
            }
        }

        CancelUpload();

        _uploadCancellationTokenSource = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var uploadToken = _uploadCancellationTokenSource.Token;
        Logger.LogDebug("Sending Character data {hash} to service {url}", data.DataHash.Value, _serverManager.CurrentRealApiUrl);

        HashSet<string> unverifiedUploads = GetUnverifiedFiles(data);
        if (unverifiedUploads.Count > 0)
        {
            await UploadUnverifiedFiles(unverifiedUploads, visiblePlayers, uploadToken).ConfigureAwait(false);
            Logger.LogInformation("Upload complete for {hash}", data.DataHash.Value);
        }

        foreach (var kvp in data.FileReplacements)
        {
            data.FileReplacements[kvp.Key].RemoveAll(i => _orchestrator.ForbiddenTransfers.Exists(f => string.Equals(f.Hash, i.Hash, StringComparison.OrdinalIgnoreCase)));
        }

        return data;
    }

    /// <summary>
    /// Ré-uploade des fichiers que le serveur a perdus, même s'ils sont marqués comme vérifiés
    /// localement. Les fichiers absents du disque sont ignorés. Renvoie le nombre de fichiers envoyés.
    /// </summary>
    public async Task<int> ReUploadFiles(IReadOnlyCollection<string> hashes, CancellationToken ct)
    {
        if (hashes.Count == 0) return 0;

        if (!_orchestrator.IsInitialized
            && !await _orchestrator.WaitForInitializationAsync(TimeSpan.FromSeconds(10), ct).ConfigureAwait(false))
        {
            throw new InvalidOperationException("FileTransferOrchestrator non initialisé, ré-upload impossible");
        }

        HashSet<string> present = new(StringComparer.Ordinal);
        foreach (var hash in hashes)
        {
            if (_fileDbManager.GetFileCacheByHash(hash) == null)
            {
                Logger.LogDebug("[{hash}] Ré-upload ignoré : fichier absent localement", hash);
                continue;
            }

            _verifiedUploadedHashes.TryRemove(hash, out _);
            present.Add(hash);
        }

        if (present.Count == 0) return 0;

        await UploadUnverifiedFiles(present, [], ct).ConfigureAwait(false);
        return present.Count;
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        Reset();
    }

    private async Task<List<UploadFileDto>> FilesSend(List<string> hashes, List<string> uids, CancellationToken ct)
    {
        if (!_orchestrator.IsInitialized) throw new InvalidOperationException("FileTransferManager is not initialized");
        FilesSendDto filesSendDto = new()
        {
            FileHashes = hashes,
            UIDs = uids
        };
        int attempt = 0;
        while (true)
        {
            attempt++;
            using var response = await _orchestrator.SendRequestAsync(HttpMethod.Post, MareFiles.ServerFilesFilesSendFullPath(_orchestrator.FilesCdnUri!), filesSendDto, ct).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.TooManyRequests && attempt < MaxRateLimitAttempts)
            {
                var wait = RateLimitRetryDelay(response.Headers.RetryAfter, attempt);
                Logger.LogWarning("FilesSend limité par le serveur (429), nouvel essai dans {delay:F0}s ({attempt}/{max})", wait.TotalSeconds, attempt, MaxRateLimitAttempts);
                await Task.Delay(wait, ct).ConfigureAwait(false);
                continue;
            }

            if (!response.IsSuccessStatusCode)
            {
                Logger.LogWarning("FilesSend a échoué avec HTTP {status}", (int)response.StatusCode);
                response.EnsureSuccessStatusCode();
            }

            return await response.Content.ReadFromJsonAsync<List<UploadFileDto>>(cancellationToken: ct).ConfigureAwait(false) ?? [];
        }
    }

    // Repris de Snowcloak (UploadRateLimitRetry) : Retry-After s'il est fourni, sinon 5 s exponentiel, plafonné à 60 s.
    private static TimeSpan RateLimitRetryDelay(RetryConditionHeaderValue? retryAfter, int attempt)
    {
        var value = retryAfter?.Delta ?? (retryAfter?.Date - DateTimeOffset.UtcNow);
        if (value is not { } duration || duration <= TimeSpan.Zero)
            duration = TimeSpan.FromSeconds(Math.Min(60, 5 * Math.Pow(2, attempt - 1)));
        return duration < TimeSpan.FromMinutes(1) ? duration : TimeSpan.FromMinutes(1);
    }

    private HashSet<string> GetUnverifiedFiles(CharacterData data)
    {
        // Purge stale entries to prevent unbounded growth
        var cutoff = DateTime.UtcNow.Subtract(VerifiedUploadTtl);
        foreach (var key in _verifiedUploadedHashes.Keys.ToList())
        {
            if (_verifiedUploadedHashes.TryGetValue(key, out var ts) && ts < cutoff)
                _verifiedUploadedHashes.TryRemove(key, out _);
        }

        HashSet<string> unverifiedUploadHashes = new(StringComparer.Ordinal);
        foreach (var item in data.FileReplacements.SelectMany(c => c.Value.Where(f => string.IsNullOrEmpty(f.FileSwapPath)).Select(v => v.Hash).Distinct(StringComparer.Ordinal)).Distinct(StringComparer.Ordinal).ToList())
        {
            if (!_verifiedUploadedHashes.TryGetValue(item, out var verifiedTime))
            {
                verifiedTime = DateTime.MinValue;
            }

            if (verifiedTime < DateTime.UtcNow.Subtract(VerifiedUploadTtl))
            {
                Logger.LogTrace("Verifying {item}, last verified: {date}", item, verifiedTime);
                unverifiedUploadHashes.Add(item);
            }
        }

        return unverifiedUploadHashes;
    }

    private void Reset()
    {
        _uploadCancellationTokenSource?.Cancel();
        _uploadCancellationTokenSource?.Dispose();
        _uploadCancellationTokenSource = null;
        CurrentUploads.Clear();
        _verifiedUploadedHashes.Clear();
    }

    private async Task UploadFile(byte[] compressedFile, string fileHash, bool postProgress, CancellationToken uploadToken)
    {
        if (!_orchestrator.IsInitialized) throw new InvalidOperationException("FileTransferManager is not initialized");

        Logger.LogInformation("[{hash}] Uploading {size}", fileHash, UiSharedService.ByteToString(compressedFile.Length));

        uploadToken.ThrowIfCancellationRequested();

        const int maxRetries = 3;
        int attempt = 0;
        int rateLimitedAttempts = 0;
        while (true)
        {
            TimeSpan wait;
            try
            {
                await UploadFileStream(compressedFile, fileHash, munged: false, postProgress, uploadToken).ConfigureAwait(false);
                _verifiedUploadedHashes[fileHash] = DateTime.UtcNow;
                return;
            }
            catch (UploadRateLimitedException ex) when (rateLimitedAttempts + 1 < MaxRateLimitAttempts)
            {
                rateLimitedAttempts++;
                wait = RateLimitRetryDelay(ex.RetryAfter, rateLimitedAttempts);
                Logger.LogWarning("[{hash}] Upload limité par le serveur (429), nouvel essai dans {delay:F0}s ({attempt}/{max})",
                    fileHash, wait.TotalSeconds, rateLimitedAttempts, MaxRateLimitAttempts);
            }
            catch (OperationCanceledException) when (uploadToken.IsCancellationRequested)
            {
                Logger.LogDebug("[{hash}] Upload cancelled", fileHash);
                throw;
            }
            catch (Exception ex)
            {
                // Inclut le timeout HttpClient (TaskCanceledException sans annulation de notre jeton)
                attempt++;
                if (attempt >= maxRetries)
                {
                    Logger.LogWarning(ex, "[{hash}] Upload failed after {attempts} attempts", fileHash, maxRetries);
                    throw;
                }

                wait = TimeSpan.FromSeconds(attempt);
                Logger.LogWarning(ex, "[{hash}] Upload failed (attempt {attempt}/{max}), retrying", fileHash, attempt, maxRetries);
            }

            await Task.Delay(wait, uploadToken).ConfigureAwait(false);
        }
    }

    private async Task UploadFileStream(byte[] compressedFile, string fileHash, bool munged, bool postProgress, CancellationToken uploadToken)
    {
        if (munged)
            throw new InvalidOperationException();

        using var ms = new MemoryStream(compressedFile);

        Progress<UploadProgress>? prog = !postProgress ? null : new((prog) =>
        {
            try
            {
                CurrentUploads.Single(f => string.Equals(f.Hash, fileHash, StringComparison.Ordinal)).Transferred = prog.Uploaded;
            }
            catch (Exception ex)
            {
                Logger.LogWarning(ex, "[{hash}] Could not set upload progress", fileHash);
            }
        });

        var streamContent = new ProgressableStreamContent(ms, prog);
        streamContent.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");

        var uploadUri = !munged
            ? MareFiles.ServerFilesUploadFullPath(_orchestrator.FilesCdnUri!, fileHash)
            : MareFiles.ServerFilesUploadMunged(_orchestrator.FilesCdnUri!, fileHash);

        using var response = await _orchestrator.SendRequestStreamAsync(HttpMethod.Post, uploadUri, streamContent, uploadToken).ConfigureAwait(false);

        if (response.StatusCode == HttpStatusCode.TooManyRequests)
            throw new UploadRateLimitedException(response.Headers.RetryAfter);

        if (!response.IsSuccessStatusCode)
        {
            Logger.LogWarning("[{hash}] Upload failed with HTTP {status}", fileHash, (int)response.StatusCode);
            response.EnsureSuccessStatusCode();
        }

        Logger.LogDebug("[{hash}] Upload Status: {status}", fileHash, response.StatusCode);
    }

    private async Task UploadUnverifiedFiles(HashSet<string> unverifiedUploadHashes, List<UserData> visiblePlayers, CancellationToken uploadToken)
    {
        var locallyMissing = unverifiedUploadHashes.Where(h => _fileDbManager.GetFileCacheByHash(h) == null).ToList();
        if (locallyMissing.Count > 0)
            throw new MissingLocalUploadFilesException(locallyMissing);

        Logger.LogDebug("Verifying {count} files", unverifiedUploadHashes.Count);
        var filesToUpload = await FilesSend([.. unverifiedUploadHashes], visiblePlayers.Select(p => p.UID).ToList(), uploadToken).ConfigureAwait(false);

        foreach (var file in filesToUpload.Where(f => !f.IsForbidden).DistinctBy(f => f.Hash, StringComparer.Ordinal))
        {
            try
            {
                CurrentUploads.Add(new UploadFileTransfer(file)
                {
                    Total = new FileInfo(_fileDbManager.GetFileCacheByHash(file.Hash)!.ResolvedFilepath).Length,
                });
            }
            catch (Exception ex)
            {
                Logger.LogWarning(ex, "Tried to request file {hash} but file was not present", file.Hash);
                locallyMissing.Add(file.Hash);
            }
        }

        if (locallyMissing.Count > 0)
        {
            CurrentUploads.Clear();
            throw new MissingLocalUploadFilesException(locallyMissing);
        }

        foreach (var file in filesToUpload.Where(c => c.IsForbidden))
        {
            if (_orchestrator.ForbiddenTransfers.TrueForAll(f => !string.Equals(f.Hash, file.Hash, StringComparison.Ordinal)))
            {
                _orchestrator.ForbiddenTransfers.Add(new UploadFileTransfer(file)
                {
                    LocalFile = _fileDbManager.GetFileCacheByHash(file.Hash)?.ResolvedFilepath ?? string.Empty,
                });
            }

            _verifiedUploadedHashes[file.Hash] = DateTime.UtcNow;
        }

        var totalSize = CurrentUploads.Sum(c => c.Total);
        Logger.LogDebug("Compressing and uploading files");
        var toUpload = CurrentUploads.Where(f => f.CanBeTransferred && !f.IsTransferred).ToList();
        try
        {
            using (var uploadSemaphore = new SemaphoreSlim(MaxParallelUploads))
            {
                var uploadTasks = toUpload.Select(async file =>
                {
                    await uploadSemaphore.WaitAsync(uploadToken).ConfigureAwait(false);
                    try
                    {
                        Logger.LogDebug("[{hash}] Compressing", file);
                        (string, byte[]) data;
                        try
                        {
                            data = await _fileDbManager.GetCompressedFileData(file.Hash, uploadToken).ConfigureAwait(false);
                        }
                        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
                        {
                            throw new MissingLocalUploadFilesException([file.Hash]);
                        }
                        file.Total = data.Item2.Length;
                        Logger.LogDebug("[{hash}] Starting upload for {filePath}", file.Hash, _fileDbManager.GetFileCacheByHash(file.Hash)?.ResolvedFilepath);
                        await UploadFile(data.Item2, file.Hash, true, uploadToken).ConfigureAwait(false);
                    }
                    finally
                    {
                        uploadSemaphore.Release();
                    }
                }).ToList();
                await Task.WhenAll(uploadTasks).ConfigureAwait(false);
            }

            if (CurrentUploads.Count > 0)
            {
                var compressedSize = CurrentUploads.Sum(c => c.Total);
                Logger.LogDebug("Upload complete, compressed {size} to {compressed}", UiSharedService.ByteToString(totalSize), UiSharedService.ByteToString(compressedSize));

                _fileDbManager.WriteOutFullCsv();
            }

            // Seuls les fichiers que le serveur n'a pas réclamés sont déjà présents chez lui
            var requestedHashes = filesToUpload.Select(f => f.Hash).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var file in unverifiedUploadHashes.Where(c => !requestedHashes.Contains(c)))
            {
                _verifiedUploadedHashes[file] = DateTime.UtcNow;
            }
        }
        finally
        {
            CurrentUploads.Clear();
        }
    }

    public sealed class UploadRateLimitedException(RetryConditionHeaderValue? retryAfter) : Exception("Upload rate limited (HTTP 429)")
    {
        public RetryConditionHeaderValue? RetryAfter { get; } = retryAfter;
    }

    public sealed class MissingLocalUploadFilesException(IReadOnlyCollection<string> missingHashes)
        : Exception($"{missingHashes.Count} fichier(s) à uploader introuvable(s) localement")
    {
        public IReadOnlyCollection<string> MissingHashes { get; } = missingHashes;
    }
}