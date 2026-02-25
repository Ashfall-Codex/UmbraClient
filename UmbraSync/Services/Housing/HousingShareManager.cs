using MessagePack;
using Microsoft.Extensions.Logging;
using System.Globalization;
using System.Security.Cryptography;
using UmbraSync.API.Dto.CharaData;
using UmbraSync.API.Dto.HousingShare;
using UmbraSync.Interop.Ipc;
using UmbraSync.Localization;
using UmbraSync.MareConfiguration.Models;
using UmbraSync.Services.Mediator;
using UmbraSync.WebAPI.SignalR;

namespace UmbraSync.Services.Housing;

public sealed class HousingShareManager
{
    private const string TemporaryModTag = "UmbraHousing_Files";

    private readonly ILogger<HousingShareManager> _logger;
    private readonly ApiController _apiController;
    private readonly HousingFurnitureScanner _scanner;
    private readonly IpcCallerPenumbra _penumbra;
    private readonly MareMediator _mediator;
    private readonly SemaphoreSlim _operationSemaphore = new(1, 1);
    private readonly List<HousingShareEntryDto> _ownShares = new();
    private Task? _currentTask;

    public HousingShareManager(ILogger<HousingShareManager> logger, ApiController apiController,
        HousingFurnitureScanner scanner, IpcCallerPenumbra penumbra, MareMediator mediator)
    {
        _logger = logger;
        _apiController = apiController;
        _scanner = scanner;
        _penumbra = penumbra;
        _mediator = mediator;
    }

    public IReadOnlyList<HousingShareEntryDto> OwnShares => _ownShares;
    public bool IsBusy => _currentTask is { IsCompleted: false };
    public string? LastError { get; private set; }
    public string? LastSuccess { get; private set; }
    public bool IsApplied { get; private set; }
    public Guid? AppliedShareId { get; private set; }

    public Task PublishAsync(LocationInfo location, string description, List<string> allowedIndividuals, List<string> allowedSyncshells)
    {
        return RunOperation(async () =>
        {
            var modPaths = _scanner.GetCollectedPaths();
            if (modPaths.Count == 0)
            {
                LastError = Loc.Get("HousingShare.Error.NoModsDetected");
                return;
            }

            var dataBytes = MessagePackSerializer.Serialize(modPaths);

            var shareId = Guid.NewGuid();
            byte[] salt = RandomNumberGenerator.GetBytes(16);
            byte[] nonce = RandomNumberGenerator.GetBytes(12);
            byte[] key = DeriveKey(shareId, salt);

            byte[] cipher = new byte[dataBytes.Length];
            byte[] tag = new byte[16];

            using (var aes = new AesGcm(key, 16))
            {
                aes.Encrypt(nonce, dataBytes, cipher, tag);
            }

            var uploadDto = new HousingShareUploadRequestDto
            {
                ShareId = shareId,
                Location = location,
                Description = description,
                CipherData = cipher,
                Nonce = nonce,
                Salt = salt,
                Tag = tag,
                AllowedIndividuals = allowedIndividuals,
                AllowedSyncshells = allowedSyncshells
            };

            await _apiController.HousingShareUpload(uploadDto).ConfigureAwait(false);
            await InternalRefreshAsync().ConfigureAwait(false);
            LastSuccess = string.Format(CultureInfo.CurrentCulture, Loc.Get("HousingShare.Success.Published"), modPaths.Count);
            _logger.LogInformation("Housing share {ShareId} uploaded with {Count} files", shareId, modPaths.Count);

            _mediator.Publish(new NotificationMessage(
                Loc.Get("HousingShare.Notification.ShareTitle"),
                string.Format(CultureInfo.CurrentCulture, Loc.Get("HousingShare.Success.Published"), modPaths.Count),
                NotificationType.Info,
                TimeSpan.FromSeconds(4)));
        });
    }

    public Task UpdateVisibilityAsync(Guid shareId, string description, List<string> allowedIndividuals, List<string> allowedSyncshells)
    {
        return RunOperation(async () =>
        {
            var dto = new HousingShareUpdateRequestDto
            {
                ShareId = shareId,
                Description = description,
                AllowedIndividuals = allowedIndividuals,
                AllowedSyncshells = allowedSyncshells
            };

            var updated = await _apiController.HousingShareUpdate(dto).ConfigureAwait(false);
            if (updated == null)
            {
                LastError = Loc.Get("HousingShare.Error.UpdateFailed");
                return;
            }

            var idx = _ownShares.FindIndex(s => s.Id == shareId);
            if (idx >= 0) _ownShares[idx] = updated;

            LastSuccess = Loc.Get("HousingShare.Success.Updated");
            _logger.LogInformation("Housing share {ShareId} visibility updated", shareId);
        });
    }

    public Task DownloadAndApplyAsync(Guid shareId)
    {
        return RunOperation(async () =>
        {
            var payload = await _apiController.HousingShareDownload(shareId).ConfigureAwait(false);
            if (payload == null)
            {
                LastError = Loc.Get("HousingShare.Error.Unavailable");
                return;
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
                _logger.LogWarning(ex, "Failed to decrypt housing share {ShareId}", shareId);
                LastError = Loc.Get("HousingShare.Error.DecryptFailed");
                return;
            }

            var modPaths = MessagePackSerializer.Deserialize<Dictionary<string, string>>(plaintext);
            if (modPaths == null || modPaths.Count == 0)
            {
                LastError = Loc.Get("HousingShare.Error.EmptyShare");
                return;
            }

            await _penumbra.AddTemporaryModAllAsync(_logger, TemporaryModTag, modPaths, 0).ConfigureAwait(false);

            IsApplied = true;
            AppliedShareId = shareId;
            LastSuccess = string.Format(CultureInfo.CurrentCulture, Loc.Get("HousingShare.Success.Applied"), modPaths.Count);
            _logger.LogInformation("Housing share {ShareId} applied with {Count} mod paths", shareId, modPaths.Count);

            _mediator.Publish(new HousingModsAppliedMessage(new LocationInfo()));
        });
    }

    public Task RemoveAppliedModsAsync()
    {
        return RunOperation(async () =>
        {
            if (!IsApplied) return;

            await _penumbra.RemoveTemporaryModAllAsync(_logger, TemporaryModTag, 0).ConfigureAwait(false);

            IsApplied = false;
            AppliedShareId = null;
            LastSuccess = Loc.Get("HousingShare.Success.Removed");
            _logger.LogInformation("Housing temporary mods removed");

            _mediator.Publish(new HousingModsRemovedMessage());
        });
    }

    public Task RefreshAsync()
    {
        return RunOperation(InternalRefreshAsync);
    }

    public Task DeleteAsync(Guid shareId)
    {
        return RunOperation(async () =>
        {
            var result = await _apiController.HousingShareDelete(shareId).ConfigureAwait(false);
            if (!result)
            {
                LastError = Loc.Get("HousingShare.Error.DeleteRefused");
                return;
            }

            _ownShares.RemoveAll(s => s.Id == shareId);
            await InternalRefreshAsync().ConfigureAwait(false);
            LastSuccess = Loc.Get("HousingShare.Success.Deleted");
        });
    }

    private async Task InternalRefreshAsync()
    {
        var own = await _apiController.HousingShareGetOwn().ConfigureAwait(false);
        _ownShares.Clear();
        _ownShares.AddRange(own);
        LastSuccess = Loc.Get("HousingShare.Success.Refreshed");
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
                _logger.LogError(ex, "Error during housing share operation");
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
}
