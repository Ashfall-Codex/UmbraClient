using Dalamud.Plugin;
using Microsoft.Extensions.Logging;
using Stagehand.Api;
using System.Collections.Concurrent;
using System.Globalization;
using System.Numerics;
using UmbraSync.API.Dto.DecorCatalog;
using UmbraSync.Localization;
using UmbraSync.MareConfiguration;
using UmbraSync.MareConfiguration.Models;
using UmbraSync.Services;
using UmbraSync.Services.Mediator;
using UmbraSync.WebAPI.SignalR;

namespace UmbraSync.Modules.Housing.Services;

public sealed class DecorCatalogManager : IDisposable
{
    public const int PageSize = 24;
    public const int MaxNameLength = 128;
    public const int MaxDescriptionLength = 1024;
    public const int MaxReasonLength = 500;
    private const int MaxContentBytes = 16 * 1024 * 1024;
    private const string PreviewFolderName = "decor-previews";

    private readonly ILogger<DecorCatalogManager> _logger;
    private readonly ApiController _apiController;
    private readonly StageShareManager _stageShareManager;
    private readonly DalamudUtilService _dalamudUtil;
    private readonly MareConfigService _configService;
    private readonly MareMediator _mediator;
    private readonly ProfileImageStore _previewStore;
    private readonly ConcurrentDictionary<Guid, Task> _previewTasks = new();
    private readonly ConcurrentDictionary<Guid, byte> _previewFailures = new();
    private readonly SemaphoreSlim _operationSemaphore = new(1, 1);
    private IReadOnlyList<DecorCatalogEntryDto> _entries = [];
    private Task? _currentTask;
    private bool _disposed;

    public DecorCatalogManager(ILogger<DecorCatalogManager> logger, ApiController apiController, StageShareManager stageShareManager,
        DalamudUtilService dalamudUtil, IDalamudPluginInterface pluginInterface, MareConfigService configService, MareMediator mediator)
    {
        _logger = logger;
        _apiController = apiController;
        _stageShareManager = stageShareManager;
        _dalamudUtil = dalamudUtil;
        _configService = configService;
        _mediator = mediator;
        _previewStore = new ProfileImageStore(logger, Path.Combine(pluginInterface.ConfigDirectory.FullName, PreviewFolderName));
    }

    public IReadOnlyList<DecorCatalogEntryDto> Entries => _entries;
    public int TotalCount { get; private set; }
    public int Page { get; private set; }
    public DecorCatalogListRequestDto LastRequest { get; private set; } = new() { PageSize = PageSize };
    public bool IsBusy => _currentTask is { IsCompleted: false };
    public string? LastError { get; private set; }
    public string? LastSuccess { get; private set; }
    public bool IsStagehandAvailable => _stageShareManager.IsStagehandAvailable;

    public Task SearchAsync(DecorCatalogListRequestDto request)
    {
        request.PageSize = PageSize;
        request.Page = Math.Max(0, request.Page);
        return RunOperation(async () =>
        {
            LastRequest = request;
            var response = await _apiController.DecorCatalogList(request).ConfigureAwait(false);
            if (response.TotalCount < 0)
            {
                LastError = Loc.Get("DecorCatalog.Error.ListFailed");
                return;
            }

            _entries = response.Entries ?? [];
            TotalCount = response.TotalCount;
            Page = response.Page;
        });
    }

    public Task RefreshAsync() => SearchAsync(LastRequest);

    public ProfileImage TryGetPreview(DecorCatalogEntryDto entry)
    {
        if (string.IsNullOrEmpty(entry.PreviewHash)) return ProfileImage.Empty;
        var stored = _previewStore.Get(entry.PreviewHash);
        if (stored.HasImage) return stored;
        if (_previewFailures.ContainsKey(entry.Id) || _disposed) return ProfileImage.Empty;

        _ = _previewTasks.GetOrAdd(entry.Id, id => Task.Run(async () =>
        {
            try
            {
                string? base64 = await _apiController.DecorCatalogGetPreview(id).ConfigureAwait(false);
                if (string.IsNullOrEmpty(base64) || _previewStore.Put(Convert.FromBase64String(base64)) == null)
                    _previewFailures[id] = 0;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Aperçu de décor {Id} indisponible", id);
                _previewFailures[id] = 0;
            }
            finally
            {
                _previewTasks.TryRemove(id, out _);
            }
        }));
        return ProfileImage.Empty;
    }

    public void ForgetPreview(DecorCatalogEntryDto entry) => _previewFailures.TryRemove(entry.Id, out _);

    public string? GetSourceFile(Guid id)
        => _configService.Current.DecorCatalogSourceFiles.TryGetValue(id.ToString(), out string? path) ? path : null;

    public Task PublishAsync(LocalStageDefinition stage, string name, string description, byte[]? preview, Guid? existingId = null, CancellationToken ct = default)
    {
        return RunOperation(async () =>
        {
            if (!IsStagehandAvailable)
            {
                LastError = Loc.Get("DecorCatalog.Error.StagehandMissing");
                return;
            }

            var transform = await _dalamudUtil.GetPlayerTransformAsync().ConfigureAwait(false);
            if (transform == null)
            {
                LastError = Loc.Get("DecorCatalog.Error.PositionUnknown");
                return;
            }

            var build = await _stageShareManager.BuildPlaintextAsync(stage, ct).ConfigureAwait(false);
            if (build.Plaintext == null || build.Path == null)
            {
                LastError = build.ErrorDetail ?? Loc.Get(build.ErrorKey ?? "Stages.Error.ParseFailed");
                return;
            }

            byte[] content = StageSharePayloadCodec.Encode(build.Plaintext);
            if (content.Length > MaxContentBytes)
            {
                LastError = Loc.Get("DecorCatalog.Error.TooLarge");
                return;
            }

            string finalName = string.IsNullOrWhiteSpace(name) ? build.Plaintext.StageName : name.Trim();
            var location = await _stageShareManager.GetLocationAsync().ConfigureAwait(false);
            var entry = await _apiController.DecorCatalogPublish(new DecorCatalogPublishRequestDto
            {
                Id = existingId ?? Guid.NewGuid(),
                Name = Truncate(finalName, MaxNameLength),
                Description = Truncate(description.Trim(), MaxDescriptionLength),
                PreviewImageBase64 = preview == null ? null : Convert.ToBase64String(preview),
                Content = content,
                AnchorX = transform.Value.Position.X,
                AnchorY = transform.Value.Position.Y,
                AnchorZ = transform.Value.Position.Z,
                AnchorTerritory = location?.TerritoryId ?? 0,
                StageFormatVersion = build.Plaintext.StageFormatVersion,
                FileCount = build.Plaintext.Files.Count,
                TotalBytes = build.Plaintext.Files.Sum(f => f.Size),
            }).ConfigureAwait(false);

            if (entry == null)
            {
                LastError = Loc.Get("DecorCatalog.Error.PublishRefused");
                return;
            }

            _configService.Current.DecorCatalogSourceFiles[entry.Id.ToString()] = build.Path;
            _configService.Save();
            _previewFailures.TryRemove(entry.Id, out _);

            await ReloadInternalAsync().ConfigureAwait(false);
            LastSuccess = build.Skipped > 0
                ? string.Format(CultureInfo.CurrentCulture, Loc.Get("DecorCatalog.Success.PublishedSkipped"), build.Skipped)
                : Loc.Get("DecorCatalog.Success.Published");
            _mediator.Publish(new NotificationMessage(Loc.Get("DecorCatalog.Notification.Title"), LastSuccess, NotificationType.Info));
        });
    }

    public Task UpdateAsync(Guid id, string name, string description, string? previewBase64)
    {
        return RunOperation(async () =>
        {
            var updated = await _apiController.DecorCatalogUpdate(new DecorCatalogUpdateRequestDto
            {
                Id = id,
                Name = Truncate(name.Trim(), MaxNameLength),
                Description = Truncate(description.Trim(), MaxDescriptionLength),
                PreviewImageBase64 = previewBase64,
            }).ConfigureAwait(false);

            if (updated == null)
            {
                LastError = Loc.Get("DecorCatalog.Error.UpdateFailed");
                return;
            }

            _previewFailures.TryRemove(id, out _);
            await ReloadInternalAsync().ConfigureAwait(false);
            LastSuccess = Loc.Get("DecorCatalog.Success.Updated");
        });
    }

    public Task DeleteAsync(Guid id)
    {
        return RunOperation(async () =>
        {
            bool deleted = await _apiController.DecorCatalogDelete(id).ConfigureAwait(false);
            if (!deleted)
            {
                LastError = Loc.Get("DecorCatalog.Error.DeleteFailed");
                return;
            }

            _configService.Current.DecorCatalogSourceFiles.Remove(id.ToString());
            _configService.Save();
            await ReloadInternalAsync().ConfigureAwait(false);
            LastSuccess = Loc.Get("DecorCatalog.Success.Deleted");
        });
    }

    public Task SetFavoriteAsync(Guid id, bool favorite)
    {
        return RunOperation(async () =>
        {
            int total = await _apiController.DecorCatalogSetFavorite(id, favorite).ConfigureAwait(false);
            if (total < 0) return;
            var entry = _entries.FirstOrDefault(e => e.Id == id);
            if (entry == null) return;
            entry.IsFavorite = favorite;
            entry.FavoriteCount = total;
        });
    }

    public Task ReportAsync(Guid id, string reason)
    {
        return RunOperation(async () =>
        {
            await _apiController.DecorCatalogReport(new DecorCatalogReportDto { Id = id, Reason = Truncate(reason.Trim(), MaxReasonLength) }).ConfigureAwait(false);
            LastSuccess = Loc.Get("DecorCatalog.Success.Reported");
        });
    }

    public Task DisplayHereAsync(DecorCatalogEntryDto entry, CancellationToken ct = default)
    {
        return RunOperation(async () =>
        {
            if (!IsStagehandAvailable)
            {
                LastError = Loc.Get("DecorCatalog.Error.StagehandMissing");
                return;
            }

            var payload = await _apiController.DecorCatalogDownload(entry.Id).ConfigureAwait(false);
            if (payload == null || payload.Content == null)
            {
                LastError = Loc.Get("DecorCatalog.Error.ContentUnreadable");
                return;
            }

            var plaintext = StageSharePayloadCodec.TryDecode(payload.Content);
            if (plaintext == null)
            {
                LastError = Loc.Get("DecorCatalog.Error.ContentUnreadable");
                return;
            }
            if (payload.StageFormatVersion > StageShareManager.SupportedFormatVersion)
            {
                LastError = Loc.Get("DecorCatalog.Error.FormatTooRecent");
                return;
            }

            var transform = await _dalamudUtil.GetPlayerTransformAsync().ConfigureAwait(false);
            if (transform == null)
            {
                LastError = Loc.Get("DecorCatalog.Error.PositionUnknown");
                return;
            }

            Vector3 translation = DecorCatalogPlacement.Translate(plaintext.Translation,
                new Vector3(payload.AnchorX, payload.AnchorY, payload.AnchorZ), transform.Value.Position);
            string name = string.IsNullOrWhiteSpace(entry.Name) ? plaintext.StageName : entry.Name;
            await _stageShareManager.ShowExternalAsync(entry.Id, plaintext, translation, entry.OwnerUid, name, ct).ConfigureAwait(false);

            if (!string.IsNullOrEmpty(_stageShareManager.LastError))
            {
                LastError = _stageShareManager.LastError;
                return;
            }

            if (!entry.IsOwner) entry.DisplayCount++;
            LastSuccess = Loc.Get("DecorCatalog.Success.Displayed");
        });
    }

    private async Task ReloadInternalAsync()
    {
        var response = await _apiController.DecorCatalogList(LastRequest).ConfigureAwait(false);
        if (response.TotalCount < 0) return;
        _entries = response.Entries ?? [];
        TotalCount = response.TotalCount;
        Page = response.Page;
    }

    private static string Truncate(string value, int maxLength)
        => value.Length <= maxLength ? value : value[..maxLength];

    private Task RunOperation(Func<Task> operation)
    {
        async Task Wrapper()
        {
            await _operationSemaphore.WaitAsync().ConfigureAwait(false);
            try
            {
                if (_disposed) return;
                LastError = null;
                LastSuccess = null;
                await operation().ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                _logger.LogDebug("Opération du catalogue de décors annulée");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Erreur pendant une opération du catalogue de décors");
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

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try
        {
            _previewStore.Close();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Fermeture du cache d'aperçus de décors en échec");
        }
    }
}
