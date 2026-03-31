using Microsoft.Extensions.Logging;
using System.Globalization;
using System.Security.Cryptography;
using UmbraSync.API.Dto.McdfShare;
using UmbraSync.Localization;
using UmbraSync.MareConfiguration.Models;
using UmbraSync.Services.Mediator;
using UmbraSync.Services.Notification;

namespace UmbraSync.Services.CharaData;

public sealed class McdfShareManager(ILogger<McdfShareManager> logger, ApiController apiController,
    CharaDataFileHandler fileHandler, CharaDataManager charaDataManager,
    MareMediator mediator, NotificationTracker notificationTracker)
{
    private readonly ILogger<McdfShareManager> _logger = logger;
    private readonly ApiController _apiController = apiController;
    private readonly CharaDataFileHandler _fileHandler = fileHandler;
    private readonly CharaDataManager _charaDataManager = charaDataManager;
    private readonly MareMediator _mediator = mediator;
    private readonly NotificationTracker _notificationTracker = notificationTracker;
    private readonly SemaphoreSlim _operationSemaphore = new(1, 1);
    private readonly List<McdfShareEntryDto> _ownShares = new();
    private readonly List<McdfShareEntryDto> _sharedWithMe = new();
    private Task? _currentTask;
    private bool _initialRefreshDone;
    private CancellationTokenSource? _pollCts;

    public void StartBackgroundPolling()
    {
        StopBackgroundPolling();
        _pollCts = new CancellationTokenSource();
        _ = BackgroundPollLoop(_pollCts.Token);
    }

    public void StopBackgroundPolling()
    {
        _pollCts?.Cancel();
        _pollCts?.Dispose();
        _pollCts = null;
    }

    private async Task BackgroundPollLoop(CancellationToken token)
    {
        // Initial refresh after short delay
        await Task.Delay(TimeSpan.FromSeconds(5), token).ConfigureAwait(false);

        while (!token.IsCancellationRequested)
        {
            try
            {
                if (!IsBusy)
                {
                    await RunOperation(() => InternalRefreshAsync(token)).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Background MCDF share poll failed");
            }

            try
            {
                await Task.Delay(TimeSpan.FromMinutes(2), token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    public IReadOnlyList<McdfShareEntryDto> OwnShares => _ownShares;
    public IReadOnlyList<McdfShareEntryDto> SharedShares => _sharedWithMe;
    public bool IsBusy => _currentTask is { IsCompleted: false };
    public string? LastError { get; private set; }
    public string? LastSuccess { get; private set; }

    public Task RefreshAsync(CancellationToken token)
    {
        return RunOperation(() => InternalRefreshAsync(token));
    }

    public Task CreateShareAsync(string description, IReadOnlyList<string> allowedIndividuals, IReadOnlyList<string> allowedSyncshells, DateTime? expiresAtUtc, CancellationToken token)
    {
        return RunOperation(async () =>
        {
            token.ThrowIfCancellationRequested();

            var mcdfBytes = await _fileHandler.CreateCharaFileBytesAsync(description, token).ConfigureAwait(false);
            if (mcdfBytes == null || mcdfBytes.Length == 0)
            {
                LastError = Loc.Get("CharaDataHub.Mcdf.Upload.PrepareFailed");
                return;
            }

            var shareId = Guid.NewGuid();
            byte[] salt = RandomNumberGenerator.GetBytes(16);
            byte[] nonce = RandomNumberGenerator.GetBytes(12);
            byte[] key = DeriveKey(shareId, salt);

            byte[] cipher = new byte[mcdfBytes.Length];
            byte[] tag = new byte[16];

            using (var aes = new AesGcm(key, 16))
            {
                aes.Encrypt(nonce, mcdfBytes, cipher, tag);
            }

            var uploadDto = new McdfShareUploadRequestDto
            {
                ShareId = shareId,
                Description = description,
                CipherData = cipher,
                Nonce = nonce,
                Salt = salt,
                Tag = tag,
                ExpiresAtUtc = expiresAtUtc,
                AllowedIndividuals = allowedIndividuals.ToList(),
                AllowedSyncshells = allowedSyncshells.ToList()
            };

            await _apiController.McdfShareUpload(uploadDto).ConfigureAwait(false);
            await InternalRefreshAsync(token).ConfigureAwait(false);

            bool isShared = uploadDto.AllowedIndividuals.Count > 0 || uploadDto.AllowedSyncshells.Count > 0;
            if (isShared)
            {
                LastSuccess = Loc.Get("CharaDataHub.Mcdf.Share.ShareSuccess");
                NotifyShareCreated(shareId, description, uploadDto.AllowedIndividuals.Count, uploadDto.AllowedSyncshells.Count);
            }
            else
            {
                LastSuccess = Loc.Get("CharaDataHub.Mcdf.Share.SaveSuccess");
                NotifySaved(description);
            }
            _logger.LogInformation("MCDF share {ShareId} uploaded ({Individuals} UID / {Syncshells} syncshells). Description: {Description}", shareId, uploadDto.AllowedIndividuals.Count, uploadDto.AllowedSyncshells.Count, description);
        });
    }

    public Task CreateShareFromFileAsync(string description, string filePath, CancellationToken token)
        => CreateShareFromFileAsync(description, filePath, [], [], null, token);

    public Task CreateShareFromFileAsync(string description, string filePath, IReadOnlyList<string> allowedIndividuals, IReadOnlyList<string> allowedSyncshells, DateTime? expiresAtUtc, CancellationToken token)
    {
        return RunOperation(async () =>
        {
            token.ThrowIfCancellationRequested();

            if (!File.Exists(filePath))
            {
                LastError = Loc.Get("CharaDataHub.Mcdf.Upload.FileNotFound");
                return;
            }

            var mcdfBytes = await File.ReadAllBytesAsync(filePath, token).ConfigureAwait(false);
            if (mcdfBytes.Length == 0)
            {
                LastError = Loc.Get("CharaDataHub.Mcdf.Upload.EmptyFile");
                return;
            }

            var shareId = Guid.NewGuid();
            byte[] salt = RandomNumberGenerator.GetBytes(16);
            byte[] nonce = RandomNumberGenerator.GetBytes(12);
            byte[] key = DeriveKey(shareId, salt);

            byte[] cipher = new byte[mcdfBytes.Length];
            byte[] tag = new byte[16];

            using (var aes = new AesGcm(key, 16))
            {
                aes.Encrypt(nonce, mcdfBytes, cipher, tag);
            }

            var uploadDto = new McdfShareUploadRequestDto
            {
                ShareId = shareId,
                Description = description,
                CipherData = cipher,
                Nonce = nonce,
                Salt = salt,
                Tag = tag,
                ExpiresAtUtc = expiresAtUtc,
                AllowedIndividuals = allowedIndividuals.ToList(),
                AllowedSyncshells = allowedSyncshells.ToList()
            };

            await _apiController.McdfShareUpload(uploadDto).ConfigureAwait(false);
            await InternalRefreshAsync(token).ConfigureAwait(false);

            bool isShared = uploadDto.AllowedIndividuals.Count > 0 || uploadDto.AllowedSyncshells.Count > 0;
            if (isShared)
            {
                LastSuccess = Loc.Get("CharaDataHub.Mcdf.Share.ShareSuccess");
                NotifyShareCreated(shareId, description, uploadDto.AllowedIndividuals.Count, uploadDto.AllowedSyncshells.Count);
            }
            else
            {
                LastSuccess = Loc.Get("CharaDataHub.Mcdf.Share.SaveSuccess");
                NotifySaved(description);
            }
            _logger.LogInformation("MCDF share {ShareId} uploaded from file. Description: {Description}", shareId, description);
        });
    }

    public Task DeleteShareAsync(Guid shareId)
    {
        return RunOperation(async () =>
        {
            var result = await _apiController.McdfShareDelete(shareId).ConfigureAwait(false);
            if (!result)
            {
                LastError = Loc.Get("CharaDataHub.Mcdf.Delete.Error");
                return;
            }

            _ownShares.RemoveAll(s => s.Id == shareId);
            _sharedWithMe.RemoveAll(s => s.Id == shareId);
            await InternalRefreshAsync(CancellationToken.None).ConfigureAwait(false);
            LastSuccess = Loc.Get("CharaDataHub.Mcdf.Delete.Success");
        });
    }

    public Task UpdateShareAsync(McdfShareUpdateRequestDto updateRequest)
    {
        return RunOperation(async () =>
        {
            var updated = await _apiController.McdfShareUpdate(updateRequest).ConfigureAwait(false);
            if (updated == null)
            {
                LastError = Loc.Get("CharaDataHub.Mcdf.Update.Error");
                return;
            }

            var idx = _ownShares.FindIndex(s => s.Id == updated.Id);
            if (idx >= 0)
            {
                _ownShares[idx] = updated;
            }
            LastSuccess = Loc.Get("CharaDataHub.Mcdf.Share.ShareSuccess");
            NotifyShareCreated(updateRequest.ShareId, updateRequest.Description, updateRequest.AllowedIndividuals.Count, updateRequest.AllowedSyncshells.Count);
        });
    }

    public Task ApplyShareAsync(Guid shareId, CancellationToken token)
    {
        return RunOperation(async () =>
        {
            token.ThrowIfCancellationRequested();
            var plainBytes = await DownloadAndDecryptShareAsync(shareId, token).ConfigureAwait(false);
            if (plainBytes == null)
            {
                LastError ??= "Échec du téléchargement du partage MCDF.";
                return;
            }

            var tempPath = await _charaDataManager.LoadMcdfFromBytes(plainBytes, token).ConfigureAwait(false);
            try
            {
                await _charaDataManager.McdfApplyToGposeTarget().ConfigureAwait(false);
            }
            finally
            {
                try
                {
                    if (File.Exists(tempPath))
                    {
                        File.Delete(tempPath);
                    }
                }
                catch
                {
                    // ignored
                }
            }
            LastSuccess = "Partage MCDF appliqué sur la cible GPose.";
        });
    }

    public Task ExportShareAsync(Guid shareId, string filePath, CancellationToken token)
    {
        return RunOperation(async () =>
        {
            token.ThrowIfCancellationRequested();
            _logger.LogInformation("Downloading MCDF share {ShareId} to {FilePath}", shareId, filePath);
            var plainBytes = await DownloadAndDecryptShareAsync(shareId, token).ConfigureAwait(false);
            if (plainBytes == null)
            {
                LastError ??= "Échec du téléchargement du partage MCDF.";
                _logger.LogWarning("Download failed for MCDF share {ShareId}: payload null or decryption error", shareId);
                return;
            }

            var directory = Path.GetDirectoryName(filePath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            await File.WriteAllBytesAsync(filePath, plainBytes, token).ConfigureAwait(false);
            _logger.LogInformation("MCDF share {ShareId} saved to {FilePath} ({Bytes} bytes)", shareId, filePath, plainBytes.Length);
            LastSuccess = "Partage MCDF exporté.";
        });
    }

    public Task DownloadShareToFileAsync(McdfShareEntryDto entry, string filePath, CancellationToken token)
    {
        _logger.LogDebug("DownloadShareToFileAsync requested for share {ShareId} (description: {Description})", entry.Id, entry.Description);
        return ExportShareAsync(entry.Id, filePath, token);
    }

    private async Task<byte[]?> DownloadAndDecryptShareAsync(Guid shareId, CancellationToken token)
    {
        var payload = await _apiController.McdfShareDownload(shareId).ConfigureAwait(false);
        if (payload == null)
        {
            LastError = "Partage indisponible.";
            return null;
        }

        byte[] key = DeriveKey(payload.ShareId, payload.Salt);
        byte[] plaintext = new byte[payload.CipherData.Length];
        try
        {
            using var aes = new AesGcm(key, 16);
            aes.Decrypt(payload.Nonce, payload.CipherData, payload.Tag, plaintext);
        }
        catch (CryptographicException ex)
        {
            _logger.LogWarning(ex, "Failed to decrypt MCDF share {ShareId}", shareId);
            LastError = "Impossible de déchiffrer le partage MCDF.";
            return null;
        }

        token.ThrowIfCancellationRequested();
        return plaintext;
    }

    private async Task InternalRefreshAsync(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var own = await _apiController.McdfShareGetOwn().ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
        var shared = await _apiController.McdfShareGetShared().ConfigureAwait(false);

        // Detect new shares received (only after initial refresh to avoid spamming on startup)
        if (_initialRefreshDone)
        {
            var existingIds = _sharedWithMe.Select(s => s.Id).ToHashSet();
            var newShares = shared.Where(s => !existingIds.Contains(s.Id)).ToList();
            foreach (var newShare in newShares)
            {
                NotifyShareReceived(newShare);
            }
        }

        _ownShares.Clear();
        _ownShares.AddRange(own);
        _sharedWithMe.Clear();
        _sharedWithMe.AddRange(shared);
        _initialRefreshDone = true;

        LastSuccess = "Partages MCDF actualisés.";
    }

    private Task RunOperation(Func<Task> operation)
    {
        async Task Wrapper()
        {
            await _operationSemaphore.WaitAsync().ConfigureAwait(false);
            try
            {
                LastError = null;
                LastSuccess = null;
                await operation().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error during MCDF share operation");
                LastError = ex.Message;
            }
            finally
            {
                _operationSemaphore.Release();
            }
        }

        var task = Wrapper();
        _currentTask = task;
        return task;
    }

    private static byte[] DeriveKey(Guid shareId, byte[] salt)
    {
        byte[] shareBytes = shareId.ToByteArray();
        byte[] material = new byte[shareBytes.Length + salt.Length];
        Buffer.BlockCopy(shareBytes, 0, material, 0, shareBytes.Length);
        Buffer.BlockCopy(salt, 0, material, shareBytes.Length, salt.Length);
        return SHA256.HashData(material);
    }

    private void NotifySaved(string description)
    {
        string safeDescription = string.IsNullOrWhiteSpace(description) ? "MCDF" : description;
        string toastTitle = Loc.Get("Notification.McdfSave.ToastTitle");
        string toastBody = string.Format(CultureInfo.CurrentCulture, Loc.Get("Notification.McdfSave.ToastBody"), safeDescription);
        _mediator.Publish(new DualNotificationMessage(toastTitle, toastBody, NotificationType.Success, TimeSpan.FromSeconds(4)));
    }

    private void NotifyShareCreated(Guid shareId, string description, int individualCount, int syncshellCount)
    {
        string safeDescription = string.IsNullOrWhiteSpace(description)
            ? shareId.ToString("D", CultureInfo.InvariantCulture)
            : description;
        string targetSummary = string.Format(CultureInfo.CurrentCulture, Loc.Get("Notification.McdfShare.Created.Summary"), individualCount, syncshellCount);
        string toastTitle = Loc.Get("Notification.McdfShare.Created.ToastTitle");
        string toastBody = string.Format(CultureInfo.CurrentCulture, Loc.Get("Notification.McdfShare.Created.ToastBody"), safeDescription, targetSummary);

        _mediator.Publish(new DualNotificationMessage(toastTitle, toastBody, NotificationType.Success, TimeSpan.FromSeconds(4)));
        _notificationTracker.Upsert(NotificationEntry.McdfShareCreated(shareId, safeDescription, individualCount, syncshellCount));
    }

    private void NotifyShareReceived(McdfShareEntryDto share)
    {
        string safeDescription = string.IsNullOrWhiteSpace(share.Description)
            ? share.Id.ToString("D", CultureInfo.InvariantCulture)
            : share.Description;
        string ownerAliasOrUid = string.IsNullOrWhiteSpace(share.OwnerAlias) ? share.OwnerUid : share.OwnerAlias;

        string toastTitle = Loc.Get("Notification.McdfShare.Received.ToastTitle");
        string toastBody = string.Format(CultureInfo.CurrentCulture, Loc.Get("Notification.McdfShare.Received.ToastBody"), safeDescription, ownerAliasOrUid);

        _mediator.Publish(new DualNotificationMessage(toastTitle, toastBody, NotificationType.Info, TimeSpan.FromSeconds(4)));
        _notificationTracker.Upsert(NotificationEntry.McdfShareReceived(share.Id, safeDescription, ownerAliasOrUid));
        _logger.LogInformation("MCDF share received: {ShareId} from {Owner}. Description: {Description}", share.Id, ownerAliasOrUid, safeDescription);
    }
}