using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;
using UmbraSync.MareConfiguration;
using UmbraSync.Services.ActorTracking;
using UmbraSync.Services.Mediator;

namespace UmbraSync.Services;

// Detect when players of interest are visible
public class VisibilityService : DisposableMediatorSubscriberBase
{
    private enum TrackedPlayerStatus
    {
        NotVisible,
        Visible
    };

    private readonly DalamudUtilService _dalamudUtil;
    private readonly ConcurrentDictionary<string, TrackedPlayerStatus> _trackedPlayerVisibility = new(StringComparer.Ordinal);
    private readonly HashSet<string> _makeVisibleNextFrame = new(StringComparer.Ordinal);
    private readonly DrawObjectTrackingService _drawTracking;
    private readonly MareConfigService _configService;
    private static readonly TimeSpan EventModeSafetyInterval = TimeSpan.FromSeconds(2);
    private volatile bool _scanRequested;
    private DateTime _lastScanUtc = DateTime.MinValue;

    public VisibilityService(ILogger<VisibilityService> logger, MareMediator mediator,
        DalamudUtilService dalamudUtil, DrawObjectTrackingService drawTracking, MareConfigService configService)
        : base(logger, mediator)
    {
        _dalamudUtil = dalamudUtil;
        _drawTracking = drawTracking;
        _configService = configService;
        Mediator.Subscribe<DelayedFrameworkUpdateMessage>(this, (_) => FrameworkUpdate());
        Mediator.Subscribe<DrawObjectLinkedMessage>(this, (_) => _scanRequested = true);
        Mediator.Subscribe<DrawObjectUnlinkedMessage>(this, (_) => _scanRequested = true);
        Mediator.Subscribe<DisconnectedMessage>(this, (_) =>
        {
            _trackedPlayerVisibility.Clear();
            _makeVisibleNextFrame.Clear();
        });
    }

    public void StartTracking(string ident)
    {
        _trackedPlayerVisibility.TryAdd(ident, TrackedPlayerStatus.NotVisible);
    }

    public void StopTracking(string ident)
    {
        // No PairVisibilityMessage is emitted if the player was visible when removed
        _trackedPlayerVisibility.TryRemove(ident, out _);
    }

    private void FrameworkUpdate()
    {
        bool eventMode = _configService.Current.EnableEventVisibility && _drawTracking.HooksActive;
        if (eventMode)
        {
            var now = DateTime.UtcNow;
            if (!_scanRequested && (now - _lastScanUtc) < EventModeSafetyInterval)
                return;
            _scanRequested = false;
            _lastScanUtc = now;
        }

        foreach (var player in _trackedPlayerVisibility)
        {
            string ident = player.Key;
            var findResult = _dalamudUtil.FindPlayerByNameHash(ident);
            // Mode événementiel : "présent" = a un draw object lié (réellement rendu), ce qui évite
            // d'appliquer sur un acteur présent dans l'object table mais pas encore dessiné.
            // Mode polling : présence dans l'object table (comportement historique).
            var isPresent = eventMode
                ? (findResult.Address != nint.Zero && _drawTracking.HasDrawObjectLinked(findResult.Address))
                : findResult.ObjectId != 0;

            // Transitions
            switch (player.Value)
            {
                case TrackedPlayerStatus.NotVisible:
                    if (isPresent)
                    {
                        if (_makeVisibleNextFrame.Contains(ident))
                        {
                            if (_trackedPlayerVisibility.TryUpdate(ident, TrackedPlayerStatus.Visible, TrackedPlayerStatus.NotVisible))
                                Mediator.Publish<PlayerVisibilityMessage>(new(ident, IsVisible: true));
                        }
                        else
                        {
                            _makeVisibleNextFrame.Add(ident);
                        }
                    }
                    break;
                case TrackedPlayerStatus.Visible:
                    if (!isPresent &&
                        _trackedPlayerVisibility.TryUpdate(ident, TrackedPlayerStatus.NotVisible, TrackedPlayerStatus.Visible))
                    {
                        Mediator.Publish<PlayerVisibilityMessage>(new(ident, IsVisible: false));
                    }
                    break;
            }

            if (!isPresent)
                _makeVisibleNextFrame.Remove(ident);
        }
    }
}