using Dalamud.Plugin.Services;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System.Globalization;
using System.Numerics;
using UmbraSync.MareConfiguration;
using UmbraSync.MareConfiguration.Configurations;
using UmbraSync.Services.Mediator;
using UmbraSync.Localization;
using UmbraSync.Utils;
using UmbraSync.WebAPI.AutoDetect;

namespace UmbraSync.Services.AutoDetect;

public class NearbyDiscoveryService(ILogger<NearbyDiscoveryService> logger, MareMediator mediator,
    MareConfigService config, DiscoveryConfigProvider configProvider, DalamudUtilService dalamudUtilService,
    IObjectTable objectTable, DiscoveryApiClient api) : IHostedService, IMediatorSubscriber
{
    private readonly ILogger<NearbyDiscoveryService> _logger = logger;
    private readonly MareMediator _mediator = mediator;
    private readonly MareConfigService _config = config;
    private readonly DiscoveryConfigProvider _configProvider = configProvider;
    private readonly DalamudUtilService _dalamud = dalamudUtilService;
    private readonly IObjectTable _objectTable = objectTable;
    private readonly DiscoveryApiClient _api = api;
    private CancellationTokenSource? _loopCts;
    private string? _lastPublishedSignature;
    private bool _loggedLocalOnly;
    private int _lastLocalCount = -1;
    private int _lastMatchCount = -1;
    private bool _loggedConfigReady;
    private string? _lastSnapshotSig;
    private volatile bool _isConnected;
    private bool _notifiedDisabled;
    private bool _notifiedEnabled;
    private bool _disableSent;
    private bool _lastAutoDetectState;
    private volatile bool _suppressNextEnabledNotification;
    private DateTime _lastHeartbeat = DateTime.MinValue;
    private static readonly TimeSpan HeartbeatInterval = TimeSpan.FromSeconds(75);
    private readonly System.Threading.Lock _entriesLock = new();
    private List<NearbyEntry> _lastEntries = [];

    public MareMediator Mediator => _mediator;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        CancelAndDispose(ref _loopCts);
        _loopCts = new CancellationTokenSource();
        _mediator.Subscribe<ConnectedMessage>(this, _ =>
        {
            _isConnected = true;
            _configProvider.TryLoadFromStapled();
            // Supprimer la notification "Enabled" lors de la connexion initiale elle sera intégrée dans la notification de bienvenue du serveur
            if (_config.Current.EnableAutoDetectDiscovery)
                _suppressNextEnabledNotification = true;
        });
        _mediator.Subscribe<DisconnectedMessage>(this, _ => { _isConnected = false; _lastPublishedSignature = null; });
        _mediator.Subscribe<AllowPairRequestsToggled>(this, OnAllowPairRequestsToggled);
        _ = Task.Run(() => Loop(_loopCts.Token));
        _lastAutoDetectState = _config.Current.EnableAutoDetectDiscovery;
        return Task.CompletedTask;
    }
    private async void OnAllowPairRequestsToggled(AllowPairRequestsToggled msg)
    {
        try
        {
            if (!_config.Current.EnableAutoDetectDiscovery) return;
            // Force a publish now so the server immediately reflects the new allow/deny state
            _lastPublishedSignature = null; // ensure next loop won't skip
            await PublishSelfOnceAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "OnAllowPairRequestsToggled failed");
        }
    }

    private async Task PublishSelfOnceAsync(CancellationToken ct)
    {
        try
        {
            if (!_config.Current.EnableAutoDetectDiscovery || !_dalamud.IsLoggedIn || !_isConnected) return;

            if ((!_configProvider.HasConfig || _configProvider.IsExpired()) &&
                !_configProvider.TryLoadFromStapled())
            {
                await _configProvider.TryFetchFromServerAsync(ct).ConfigureAwait(false);
            }

            var ep = _configProvider.PublishEndpoint;
            var saltBytes = _configProvider.Salt;
            if (string.IsNullOrEmpty(ep) || saltBytes is not { Length: > 0 }) return;

            var saltHex = Convert.ToHexString(saltBytes);
            string? displayName = null;
            ushort meWorld = 0;
            try
            {
                var me = await _dalamud.RunOnFrameworkThread(() => _dalamud.GetPlayerCharacter()).ConfigureAwait(false);
                if (me is { } mePc)
                {
                    displayName = mePc.Name.TextValue;
                    meWorld = (ushort)mePc.HomeWorld.RowId;
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Failed to determine own player data for nearby publish");
            }

            if (string.IsNullOrEmpty(displayName)) return;

            var selfHash = (saltHex + displayName + meWorld.ToString()).GetHash256();
            var ok = await _api.PublishAsync(ep!, [selfHash], displayName, ct, _config.Current.AllowAutoDetectPairRequests).ConfigureAwait(false);
            _logger.LogInformation("Nearby publish (manual/immediate): {result}", ok ? "success" : "failed");
            if (ok)
            {
                _lastPublishedSignature = selfHash;
                _lastHeartbeat = DateTime.UtcNow;
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Immediate publish failed");
        }
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _mediator.UnsubscribeAll(this);
        CancelAndDispose(ref _loopCts);
        return Task.CompletedTask;
    }

    public List<NearbyEntry> SnapshotEntries()
    {
        using var _ = _entriesLock.EnterScope();
        return _lastEntries.ToList();
    }

    private void UpdateSnapshot(List<NearbyEntry> entries)
    {
        using var _ = _entriesLock.EnterScope();
        _lastEntries = entries.ToList();
    }

    private void CancelAndDispose(ref CancellationTokenSource? cts)
    {
        if (cts == null) return;
        try
        {
            cts.Cancel();
        }
        catch (ObjectDisposedException ex)
        {
            _logger.LogTrace(ex, "NearbyDiscoveryService CTS already disposed");
        }

        cts.Dispose();
        cts = null;
    }

    private async Task Loop(CancellationToken ct)
    {
        _configProvider.TryLoadFromStapled();

        while (!ct.IsCancellationRequested)
        {
            try
            {
                bool currentState = _config.Current.EnableAutoDetectDiscovery;
                if (currentState != _lastAutoDetectState && _isConnected)
                {
                    _lastAutoDetectState = currentState;
                    if (currentState)
                    {
                        // Force immediate publish on toggle ON
                        try
                        {
                            // Ensure well-known is present
                            if ((!_configProvider.HasConfig || _configProvider.IsExpired()) &&
                                !_configProvider.TryLoadFromStapled())
                            {
                                await _configProvider.TryFetchFromServerAsync(ct).ConfigureAwait(false);
                            }

                            var ep = _configProvider.PublishEndpoint;
                            var saltBytes = _configProvider.Salt;
                            if (!string.IsNullOrEmpty(ep) && saltBytes is { Length: > 0 })
                            {
                                await ImmediatePublishAsync(ep, saltBytes, ct).ConfigureAwait(false);
                            }
                        }
                        catch (Exception ex)
                        {
                            _logger.LogDebug(ex, "Nearby immediate publish on toggle ON failed");
                        }

                        if (!_notifiedEnabled)
                        {
                            if (_suppressNextEnabledNotification)
                                _suppressNextEnabledNotification = false;
                            else
                                _mediator.Publish(new NotificationMessage(Loc.Get("Notification.NearbyDetection.Title"), Loc.Get("Notification.NearbyDetection.Enabled"), default));
                            _notifiedEnabled = true;
                            _notifiedDisabled = false;
                            _disableSent = false;
                        }
                    }
                    else
                    {
                        var ep = _configProvider.PublishEndpoint;
                        if (!string.IsNullOrEmpty(ep) && !_disableSent)
                        {
                            var disableUrl = ep.Replace("/publish", "/disable");
                            try
                            {
                                await _api.DisableAsync(disableUrl, ct).ConfigureAwait(false);
                                _disableSent = true;
                            }
                            catch (Exception ex)
                            {
                                _logger.LogDebug(ex, "Failed to notify server of nearby disable");
                            }
                        }
                        if (!_notifiedDisabled)
                        {
                            _mediator.Publish(new NotificationMessage(Loc.Get("Notification.NearbyDetection.Title"), Loc.Get("Notification.NearbyDetection.Disabled"), default));
                            _notifiedDisabled = true;
                            _notifiedEnabled = false;
                        }
                    }
                }
                if (!_config.Current.EnableAutoDetectDiscovery || !_dalamud.IsLoggedIn || !_isConnected)
                {
                    if (!_config.Current.EnableAutoDetectDiscovery && !string.IsNullOrEmpty(_configProvider.PublishEndpoint))
                    {
                        var disableUrl = _configProvider.PublishEndpoint.Replace("/publish", "/disable");
                        try
                        {
                            if (!_disableSent)
                            {
                                await _api.DisableAsync(disableUrl, ct).ConfigureAwait(false);
                                _disableSent = true;
                            }
                        }
                        catch (Exception ex)
                        {
                            _logger.LogDebug(ex, "Failed to notify server of nearby disable");
                        }

                        if (!_notifiedDisabled)
                        {
                            _mediator.Publish(new NotificationMessage(Loc.Get("Notification.NearbyDetection.Title"), Loc.Get("Notification.NearbyDetection.Disabled"), default));
                            _notifiedDisabled = true;
                            _notifiedEnabled = false;
                        }
                    }
                    await Task.Delay(1000, ct).ConfigureAwait(false);
                    continue;
                }
                if (!_configProvider.HasConfig || _configProvider.IsExpired())
                {
                    if (!_configProvider.TryLoadFromStapled())
                    {
                        await _configProvider.TryFetchFromServerAsync(ct).ConfigureAwait(false);
                    }
                }
                else if (!_loggedConfigReady && _configProvider.NearbyEnabled)
                {
                    _loggedConfigReady = true;
                    _logger.LogInformation("Nearby: well-known loaded and enabled; refresh={refresh}s, expires={exp}", _configProvider.RefreshSec, _configProvider.SaltExpiresAt);
                }

                var entries = await GetLocalNearbyAsync().ConfigureAwait(false);
                // Log when local count changes (including 0) to indicate activity
                if (entries.Count != _lastLocalCount)
                {
                    _lastLocalCount = entries.Count;
                    _logger.LogTrace("Nearby: {count} players detected locally", _lastLocalCount);
                }

                // Try query server if config and endpoints are present
                if (_configProvider.NearbyEnabled && !_configProvider.IsExpired() &&
                    _configProvider.Salt is { Length: > 0 })
                {
                    try
                    {
                        var saltHex = Convert.ToHexString(_configProvider.Salt!);
                        // map hash->index for result matching
                        Dictionary<string, int> hashToIndex = new(StringComparer.Ordinal);
                        List<string> hashes = new(entries.Count);
                        foreach (var (entry, idx) in entries.Select((e, i) => (e, i)))
                        {
                            var h = (saltHex + entry.Name + entry.WorldId.ToString()).GetHash256();
                            hashToIndex[h] = idx;
                            hashes.Add(h);
                        }

                        try
                        {
                            var snapSig = string.Join(',', hashes.OrderBy(s => s, StringComparer.Ordinal)).GetHash256();
                            if (!string.Equals(snapSig, _lastSnapshotSig, StringComparison.Ordinal))
                            {
                                _lastSnapshotSig = snapSig;
                                var sample = entries.Take(5).Select(e =>
                                {
                                    var hh = (saltHex + e.Name + e.WorldId.ToString()).GetHash256();
                                    var shortH = hh.Length > 8 ? hh[..8] : hh;
                                    return $"{e.Name}({e.WorldId})->{shortH}";
                                });
                                var saltShort = saltHex.Length > 8 ? saltHex[..8] : saltHex;
                                _logger.LogTrace("Nearby snapshot: {count} entries; salt={saltShort}…; samples=[{samples}]",
                                    entries.Count, saltShort, string.Join(", ", sample));
                            }
                        }
                        catch (Exception ex)
                        {
                            _logger.LogDebug(ex, "Failed to compute snapshot signature");
                        }

                        var publishEndpoint = _configProvider.PublishEndpoint;
                        if (!string.IsNullOrEmpty(publishEndpoint))
                        {
                            string? displayName = null;
                            string? selfHash = null;
                            try
                            {
                                var me = await _dalamud.RunOnFrameworkThread(() => _dalamud.GetPlayerCharacter()).ConfigureAwait(false);
                                if (me is { } mePc)
                                {
                                    displayName = mePc.Name.TextValue;
                                    var meWorld = (ushort)mePc.HomeWorld.RowId;
                                    _logger.LogTrace("Nearby self ident: {name} ({world})", displayName, meWorld);
                                    selfHash = (saltHex + displayName + meWorld.ToString()).GetHash256();
                                }
                            }
                            catch (Exception ex)
                            {
                                _logger.LogDebug(ex, "Failed to compute self hash for nearby publish");
                            }

                            if (!string.IsNullOrEmpty(selfHash))
                            {
                                var sig = selfHash;
                                if (!string.Equals(sig, _lastPublishedSignature, StringComparison.Ordinal))
                                {
                                    _lastPublishedSignature = sig;
                                    var shortSelf = selfHash.Length > 8 ? selfHash[..8] : selfHash;
                                    _logger.LogDebug("Nearby publish: self presence updated (hash={hash})", shortSelf);
                                    var ok = await _api.PublishAsync(publishEndpoint, [selfHash], displayName, ct, _config.Current.AllowAutoDetectPairRequests).ConfigureAwait(false);
                                    _logger.LogInformation("Nearby publish result: {result}", ok ? "success" : "failed");
                                    if (ok)
                                    {
                                        _lastHeartbeat = DateTime.UtcNow;
                                        if (!_notifiedEnabled)
                                        {
                                            if (_suppressNextEnabledNotification)
                                                _suppressNextEnabledNotification = false;
                                            else
                                                _mediator.Publish(new NotificationMessage(Loc.Get("Notification.NearbyDetection.Title"), Loc.Get("Notification.NearbyDetection.Enabled"), default));
                                            _notifiedEnabled = true;
                                            _notifiedDisabled = false;
                                            _disableSent = false; // allow future /disable when turning off again
                                        }
                                    }
                                }
                                else
                                {
                                    // No changes; perform heartbeat publish if interval elapsed
                                    if (DateTime.UtcNow - _lastHeartbeat >= HeartbeatInterval)
                                    {
                                        var okHb = await _api.PublishAsync(publishEndpoint, [selfHash], displayName, ct, _config.Current.AllowAutoDetectPairRequests).ConfigureAwait(false);
                                        _logger.LogDebug("Nearby heartbeat publish: {result}", okHb ? "success" : "failed");
                                        if (okHb) _lastHeartbeat = DateTime.UtcNow;
                                    }
                                    else
                                    {
                                        _logger.LogDebug("Nearby publish skipped (no changes)");
                                    }
                                }
                            }
                            // else: no self character available; skip publish silently
                        }

                        if (!string.IsNullOrEmpty(_configProvider.QueryEndpoint))
                        {
                            await QueryNearbyMatchesAsync(entries, hashes, hashToIndex, ct).ConfigureAwait(false);
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogDebug(ex, "Nearby query failed; falling back to local list");
                        if (ex.Message.Contains("DISCOVERY_SALT_EXPIRED", StringComparison.OrdinalIgnoreCase))
                        {
                            _logger.LogInformation("Nearby: salt expired, refetching well-known");
                            try
                            {
                                await _configProvider.TryFetchFromServerAsync(ct).ConfigureAwait(false);
                            }
                            catch (Exception fetchEx)
                            {
                                _logger.LogWarning(fetchEx, "Failed to refresh nearby configuration");
                            }
                        }
                    }
                }
                else
                {
                    if (!_loggedLocalOnly)
                    {
                        _loggedLocalOnly = true;
                        _logger.LogDebug("Nearby: well-known not available or disabled; running in local-only mode");
                    }
                }
                UpdateSnapshot(entries);
                _mediator.Publish(new DiscoveryListUpdated(entries));

                var delayMs = Math.Max(1000, _configProvider.MinQueryIntervalMs);
                if (entries.Count == 0) delayMs = Math.Max(delayMs, 5000);
                await Task.Delay(delayMs, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "NearbyDiscoveryService loop error");
                await Task.Delay(2000, ct).ConfigureAwait(false);
            }
        }
    }

    private async Task<List<NearbyEntry>> GetLocalNearbyAsync()
    {
        var list = new List<NearbyEntry>();
        try
        {
            var local = await _dalamud.RunOnFrameworkThread(() => _dalamud.GetPlayerCharacter()).ConfigureAwait(false);
            var localPos = local?.Position ?? Vector3.Zero;
            int maxDist = MareConfig.AutoDetectFixedMaxDistanceMeters;

            int limit = Math.Min(200, _objectTable.Length);
            for (int i = 0; i < limit; i++)
            {
                var objectIndex = i;
                var obj = await _dalamud.RunOnFrameworkThread(() => _objectTable[objectIndex]).ConfigureAwait(false);
                if (obj == null || obj.ObjectKind != Dalamud.Game.ClientState.Objects.Enums.ObjectKind.Player) continue;
                if (local != null && obj.Address == local.Address) continue;

                float dist = local == null ? float.NaN : Vector3.Distance(localPos, obj.Position);
                if (!float.IsNaN(dist) && dist > maxDist) continue;

                string name = obj.Name.TextValue;
                ushort worldId = 0;
                if (obj is Dalamud.Game.ClientState.Objects.SubKinds.IPlayerCharacter pc)
                    worldId = (ushort)pc.HomeWorld.RowId;

                list.Add(new NearbyEntry(name, worldId, dist, false, null, null, null));
            }
        }
        catch
        {
            // ignore
        }
        return list;
    }
    private async Task ImmediatePublishAsync(string publishEndpoint, byte[] saltBytes, CancellationToken ct)
    {
        var saltHex = Convert.ToHexString(saltBytes);
        string? displayName = null;
        ushort meWorld = 0;
        try
        {
            var me = await _dalamud.RunOnFrameworkThread(() => _dalamud.GetPlayerCharacter()).ConfigureAwait(false);
            if (me is { } mePc)
            {
                displayName = mePc.Name.TextValue;
                meWorld = (ushort)mePc.HomeWorld.RowId;
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to gather player info for nearby publish");
        }

        if (string.IsNullOrEmpty(displayName))
        {
            return;
        }

        var selfHash = (saltHex + displayName + meWorld.ToString(CultureInfo.InvariantCulture)).GetHash256();
        _lastPublishedSignature = null;
        var okNow = await _api.PublishAsync(publishEndpoint, [selfHash], displayName, ct, _config.Current.AllowAutoDetectPairRequests).ConfigureAwait(false);
        _logger.LogInformation("Nearby immediate publish on toggle ON: {result}", okNow ? "success" : "failed");
    }

    private async Task QueryNearbyMatchesAsync(List<NearbyEntry> entries, IReadOnlyList<string> hashes, Dictionary<string, int> hashToIndex, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(_configProvider.QueryEndpoint))
            return;

        var batch = Math.Max(1, _configProvider.MaxQueryBatch);
        var allMatches = new List<ServerMatch>();
        for (int i = 0; i < hashes.Count; i += batch)
        {
            var slice = hashes.Skip(i).Take(batch).ToArray();
            var res = await _api.QueryAsync(_configProvider.QueryEndpoint!, slice, ct).ConfigureAwait(false);
            if (res.Count > 0) allMatches.AddRange(res);
        }

        if (allMatches.Count > 0)
        {
            foreach (var match in allMatches)
            {
                if (hashToIndex.TryGetValue(match.Hash, out var idx))
                {
                    var existing = entries[idx];
                    var acceptsRequests = match.AcceptPairRequests ?? !string.IsNullOrEmpty(match.Token);
                    entries[idx] = new NearbyEntry(existing.Name, existing.WorldId, existing.Distance, true, match.Token, match.DisplayName, match.Uid, acceptsRequests);
                }
            }
            _logger.LogDebug("Nearby: server returned {count} matches", allMatches.Count);
        }
        else
        {
            _logger.LogTrace("Nearby: server returned {count} matches", allMatches.Count);
        }

        var matchCount = entries.Count(e => e.IsMatch);
        if (matchCount != _lastMatchCount)
        {
            _lastMatchCount = matchCount;
            if (matchCount > 0)
            {
                var matchSamples = entries.Where(e => e.IsMatch).Take(5)
                    .Select(e => string.IsNullOrEmpty(e.DisplayName) ? e.Name : e.DisplayName!);
                _logger.LogInformation("Nearby: {count} Umbra users nearby [{samples}]", matchCount, string.Join(", ", matchSamples));
            }
            else
            {
                _logger.LogTrace("Nearby: {count} Umbra users nearby", matchCount);
            }
        }
    }
}