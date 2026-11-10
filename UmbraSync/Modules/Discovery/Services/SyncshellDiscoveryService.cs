using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System.Globalization;
using UmbraSync.API.Data;
using UmbraSync.API.Data.Enum;
using UmbraSync.API.Dto.Group;
using UmbraSync.Services.Mediator;

namespace UmbraSync.Modules.Discovery.Services;

public sealed class SyncshellDiscoveryService : IHostedService, IMediatorSubscriber
{
    private readonly ILogger<SyncshellDiscoveryService> _logger;
    private readonly MareMediator _mediator;
    private readonly ApiController _apiController;
    private readonly SemaphoreSlim _refreshSemaphore = new(1, 1);
    private readonly Lock _entriesLock = new();
    private List<SyncshellDiscoveryEntryDto> _entries = [];
    private string? _lastError;
    private bool _isRefreshing;

    public SyncshellDiscoveryService(ILogger<SyncshellDiscoveryService> logger, MareMediator mediator, ApiController apiController)
    {
        _logger = logger;
        _mediator = mediator;
        _apiController = apiController;
    }

    public MareMediator Mediator => _mediator;

    public IReadOnlyList<SyncshellDiscoveryEntryDto> Entries
    {
        get
        {
            using (_entriesLock.EnterScope())
            {
                return _entries.AsReadOnly();
            }
        }
    }

    public bool IsRefreshing => _isRefreshing;
    public string? LastError => _lastError;

    public async Task<bool> JoinAsync(string gid, CancellationToken ct)
    {
        try
        {
            return await _apiController.SyncshellDiscoveryJoin(new GroupDto(new GroupData(gid))).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Join syncshell discovery failed for {gid}", gid);
            return false;
        }
    }

    public async Task<SyncshellDiscoveryStateDto?> GetStateAsync(string gid, CancellationToken ct)
    {
        try
        {
            return await _apiController.SyncshellDiscoveryGetState(new GroupDto(new GroupData(gid))).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to fetch syncshell discovery state for {gid}", gid);
            return null;
        }
    }

    public async Task<bool> SetVisibilityAsync(string gid, bool visible, int? displayDurationHours,
        int[]? activeWeekdays, TimeSpan? timeStartLocal, TimeSpan? timeEndLocal, string? timeZone,
        CancellationToken ct, AutoDetectMode? modeOverride = null)
    {
        try
        {
            _logger.LogInformation(
                "[AutoDetect][CLIENT] SetVisibility gid={gid} visible={visible} duration={duration} weekdays=[{weekdays}] start={start} end={end} tz={tz}",
                gid,
                visible,
                displayDurationHours,
                activeWeekdays == null ? string.Empty : string.Join(',', activeWeekdays),
                timeStartLocal?.ToString(),
                timeEndLocal?.ToString(),
                timeZone ?? string.Empty);

            // Traduire vers la nouvelle "policy" (Off / Duration / Recurring / Fulltime)
            AutoDetectMode mode = modeOverride ?? (visible ? AutoDetectMode.Duration : AutoDetectMode.Off);
            int? duration = null;
            string? startLocal = timeStartLocal.HasValue ? new DateTime(timeStartLocal.Value.Ticks, DateTimeKind.Unspecified).ToString("HH:mm", CultureInfo.InvariantCulture) : null;
            string? endLocal = timeEndLocal.HasValue ? new DateTime(timeEndLocal.Value.Ticks, DateTimeKind.Unspecified).ToString("HH:mm", CultureInfo.InvariantCulture) : null;

            if (mode != AutoDetectMode.Fulltime)
            {
                bool canRecurring = visible
                                    && activeWeekdays != null && activeWeekdays.Length > 0
                                    && !string.IsNullOrWhiteSpace(startLocal)
                                    && !string.IsNullOrWhiteSpace(endLocal)
                                    && !string.Equals(startLocal, endLocal, StringComparison.Ordinal)
                                    && !string.IsNullOrWhiteSpace(timeZone);
                if (canRecurring)
                {
                    mode = AutoDetectMode.Recurring;
                }
                else if (visible && displayDurationHours.HasValue)
                {
                    duration = Math.Clamp(displayDurationHours.Value, 1, 240);
                }
            }

            var policy = new SyncshellDiscoverySetPolicyRequestDto
            {
                GID = gid,
                Mode = mode,
                DisplayDurationHours = mode == AutoDetectMode.Duration ? duration : null,
                ActiveWeekdays = mode == AutoDetectMode.Recurring ? activeWeekdays : null,
                TimeStartLocal = mode == AutoDetectMode.Recurring ? startLocal : null,
                TimeEndLocal = mode == AutoDetectMode.Recurring ? endLocal : null,
                TimeZone = mode == AutoDetectMode.Recurring ? timeZone : null,
            };

            _logger.LogInformation(
                "[AutoDetect][CLIENT] Payload(SetPolicy) gid={gid} mode={mode} duration={duration} weekdays=[{weekdays}] startLocal={start} endLocal={end} tz={tz}",
                policy.GID,
                policy.Mode,
                policy.DisplayDurationHours,
                policy.ActiveWeekdays == null ? string.Empty : string.Join(',', policy.ActiveWeekdays),
                policy.TimeStartLocal ?? string.Empty,
                policy.TimeEndLocal ?? string.Empty,
                policy.TimeZone ?? string.Empty);

            bool success;
            try
            {
                success = await _apiController.SyncshellDiscoverySetPolicy(policy).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                var isMissing = ex.Message.IndexOf("Method does not exist", StringComparison.OrdinalIgnoreCase) >= 0
                                || ex.GetType().Name.IndexOf("HubException", StringComparison.OrdinalIgnoreCase) >= 0;
                if (!isMissing)
                {
                    _logger.LogWarning(ex, "[AutoDetect][CLIENT] SetPolicy failed for {gid}", gid);
                    return false;
                }

                _logger.LogWarning(ex, "[AutoDetect][CLIENT] SetPolicy unavailable on server, falling back to legacy SetVisibility");

                var legacyRequest = new SyncshellDiscoveryVisibilityRequestDto
                {
                    GID = gid,
                    AutoDetectVisible = visible,
                    DisplayDurationHours = mode == AutoDetectMode.Duration ? duration : displayDurationHours,
                    ActiveWeekdays = mode == AutoDetectMode.Recurring ? activeWeekdays : null,
                    TimeStartLocal = mode == AutoDetectMode.Recurring ? startLocal : null,
                    TimeEndLocal = mode == AutoDetectMode.Recurring ? endLocal : null,
                    TimeZone = mode == AutoDetectMode.Recurring ? timeZone : null,
                };

                _logger.LogInformation(
                    "[AutoDetect][CLIENT] Payload(SetVisibility-compat) gid={gid} visible={visible} duration={duration} weekdays=[{weekdays}] startLocal={start} endLocal={end} tz={tz}",
                    legacyRequest.GID,
                    legacyRequest.AutoDetectVisible,
                    legacyRequest.DisplayDurationHours,
                    legacyRequest.ActiveWeekdays == null ? string.Empty : string.Join(',', legacyRequest.ActiveWeekdays),
                    legacyRequest.TimeStartLocal ?? string.Empty,
                    legacyRequest.TimeEndLocal ?? string.Empty,
                    legacyRequest.TimeZone ?? string.Empty);

                success = await _apiController.SyncshellDiscoverySetVisibility(legacyRequest).ConfigureAwait(false);
            }
            if (!success) return false;

            var state = await GetStateAsync(gid, ct).ConfigureAwait(false);
            if (state != null)
            {
                _logger.LogInformation(
                    "[AutoDetect][CLIENT] StateAfterSet gid={gid} visible={visible} pwdTmpDisabled={pwd} duration={duration} weekdays=[{weekdays}] startLocal={start} endLocal={end} tz={tz}",
                    state.GID,
                    state.AutoDetectVisible,
                    state.PasswordTemporarilyDisabled,
                    state.DisplayDurationHours,
                    state.ActiveWeekdays == null ? string.Empty : string.Join(',', state.ActiveWeekdays),
                    state.TimeStartLocal ?? string.Empty,
                    state.TimeEndLocal ?? string.Empty,
                    state.TimeZone ?? string.Empty);
                _mediator.Publish(new SyncshellAutoDetectStateChanged(state.GID, state.AutoDetectVisible, state.PasswordTemporarilyDisabled));
            }

            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to set syncshell visibility for {gid}", gid);
            return false;
        }
    }

    public async Task RefreshAsync(CancellationToken ct)
    {
        if (!await _refreshSemaphore.WaitAsync(0, ct).ConfigureAwait(false))
        {
            return;
        }

        try
        {
            _isRefreshing = true;
            var discovered = await _apiController.SyncshellDiscoveryList().ConfigureAwait(false);
            using (_entriesLock.EnterScope())
            {
                _entries = discovered;
            }
            _lastError = null;
            _mediator.Publish(new SyncshellDiscoveryUpdated(Entries.ToList()));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to refresh syncshell discovery list");
            _lastError = ex.Message;
        }
        finally
        {
            _isRefreshing = false;
            _refreshSemaphore.Release();
        }
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _mediator.Subscribe<ConnectedMessage>(this, msg =>
        {
            _ = RefreshAsync(CancellationToken.None);
        });
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _mediator.UnsubscribeAll(this);
        return Task.CompletedTask;
    }
}