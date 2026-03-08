using Microsoft.Extensions.Logging;
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

        Task uploadTask = Task.CompletedTask;
        int i = 1;
        long uploadedTotal = 0;
        long lastSize = 0;
        foreach (var file in filesToUpload)
        {
            // wait for previous upload to finish and report its bytes
            await uploadTask.ConfigureAwait(false);
            if (lastSize > 0)
            {
                uploadedTotal += lastSize;
                uploadedBytesProgress?.Report(uploadedTotal);
            }

            progress.Report(string.Format(System.Globalization.CultureInfo.CurrentCulture,
                Localization.Loc.Get("Settings.Transfer.Precache.Progress.Uploading"), i++, filesToUpload.Count));
            Logger.LogDebug("[{hash}] Compressing", file);
            var data = await _fileDbManager.GetCompressedFileData(file.Hash, ct ?? CancellationToken.None).ConfigureAwait(false);
            lastSize = data.Item2.LongLength;
            Logger.LogDebug("[{hash}] Starting upload for {filePath}", data.Item1, _fileDbManager.GetFileCacheByHash(data.Item1)!.ResolvedFilepath);
            uploadTask = UploadFile(data.Item2, file.Hash, false, ct ?? CancellationToken.None);
            (ct ?? CancellationToken.None).ThrowIfCancellationRequested();
        }

        await uploadTask.ConfigureAwait(false);
        if (lastSize > 0)
        {
            uploadedTotal += lastSize;
            uploadedBytesProgress?.Report(uploadedTotal);
        }

        return [];
    }

    public async Task<CharacterData> UploadFiles(CharacterData data, List<UserData> visiblePlayers)
    {
        if (!_orchestrator.IsInitialized)
        {
            Logger.LogWarning("FileTransferOrchestrator n'est pas initialisé, upload ignoré pour {hash}", data.DataHash.Value);
            return data;
        }

        CancelUpload();

        _uploadCancellationTokenSource = new CancellationTokenSource();
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
        var response = await _orchestrator.SendRequestAsync(HttpMethod.Post, MareFiles.ServerFilesFilesSendFullPath(_orchestrator.FilesCdnUri!), filesSendDto, ct).ConfigureAwait(false);
        return await response.Content.ReadFromJsonAsync<List<UploadFileDto>>(cancellationToken: ct).ConfigureAwait(false) ?? [];
    }

    private HashSet<string> GetUnverifiedFiles(CharacterData data)
    {
        // Purge stale entries to prevent unbounded growth
        var cutoff = DateTime.UtcNow.Subtract(TimeSpan.FromMinutes(20));
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

            if (verifiedTime < DateTime.UtcNow.Subtract(TimeSpan.FromMinutes(10)))
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

        if (uploadToken.IsCancellationRequested) return;

        const int maxRetries = 3;
        for (int attempt = 1; attempt <= maxRetries; attempt++)
        {
            try
            {
                await UploadFileStream(compressedFile, fileHash, munged: false, postProgress, uploadToken).ConfigureAwait(false);
                _verifiedUploadedHashes[fileHash] = DateTime.UtcNow;
                return;
            }
            catch (OperationCanceledException)
            {
                Logger.LogDebug("[{hash}] Upload cancelled", fileHash);
                return;
            }
            catch (Exception ex)
            {
                if (attempt >= maxRetries)
                {
                    Logger.LogWarning(ex, "[{hash}] Upload failed after {attempts} attempts", fileHash, maxRetries);
                    return;
                }

                Logger.LogWarning(ex, "[{hash}] Upload failed (attempt {attempt}/{max}), retrying", fileHash, attempt, maxRetries);
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(attempt), uploadToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    Logger.LogDebug("[{hash}] Upload cancelled during retry delay", fileHash);
                    return;
                }
            }
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

        if (!response.IsSuccessStatusCode)
        {
            Logger.LogWarning("[{hash}] Upload failed with HTTP {status}", fileHash, (int)response.StatusCode);
            response.EnsureSuccessStatusCode();
        }

        Logger.LogDebug("[{hash}] Upload Status: {status}", fileHash, response.StatusCode);
    }

    private async Task UploadUnverifiedFiles(HashSet<string> unverifiedUploadHashes, List<UserData> visiblePlayers, CancellationToken uploadToken)
    {
        unverifiedUploadHashes = unverifiedUploadHashes.Where(h => _fileDbManager.GetFileCacheByHash(h) != null).ToHashSet(StringComparer.Ordinal);

        Logger.LogDebug("Verifying {count} files", unverifiedUploadHashes.Count);
        var filesToUpload = await FilesSend([.. unverifiedUploadHashes], visiblePlayers.Select(p => p.UID).ToList(), uploadToken).ConfigureAwait(false);

        foreach (var file in filesToUpload.Where(f => !f.IsForbidden).DistinctBy(f => f.Hash))
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
            }
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
        Task uploadTask = Task.CompletedTask;
        foreach (var file in CurrentUploads.Where(f => f.CanBeTransferred && !f.IsTransferred).ToList())
        {
            Logger.LogDebug("[{hash}] Compressing", file);
            var data = await _fileDbManager.GetCompressedFileData(file.Hash, uploadToken).ConfigureAwait(false);
            CurrentUploads.Single(e => string.Equals(e.Hash, file.Hash, StringComparison.Ordinal)).Total = data.Item2.Length;
            Logger.LogDebug("[{hash}] Starting upload for {filePath}", file.Hash, _fileDbManager.GetFileCacheByHash(file.Hash)!.ResolvedFilepath);
            await uploadTask.ConfigureAwait(false);
            uploadTask = UploadFile(data.Item2, file.Hash, true, uploadToken);
            uploadToken.ThrowIfCancellationRequested();
        }

        if (CurrentUploads.Count > 0)
        {
            await uploadTask.ConfigureAwait(false);

            var compressedSize = CurrentUploads.Sum(c => c.Total);
            Logger.LogDebug("Upload complete, compressed {size} to {compressed}", UiSharedService.ByteToString(totalSize), UiSharedService.ByteToString(compressedSize));

            _fileDbManager.WriteOutFullCsv();
        }

        foreach (var file in unverifiedUploadHashes.Where(c => !CurrentUploads.Exists(u => string.Equals(u.Hash, c, StringComparison.Ordinal))))
        {
            _verifiedUploadedHashes[file] = DateTime.UtcNow;
        }

        CurrentUploads.Clear();
    }
}