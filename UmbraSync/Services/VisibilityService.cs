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
    // Diagnostic uniquement : un acteur présent dans l'object table mais sans draw object lié reste « visible »
    // (le jeu cesse de dessiner certains joueurs selon la distance/limite d'affichage, et un redraw délie
    // brièvement le draw object ; dans les deux cas rien à réappliquer). On journalise la durée de ces absences.
    private static readonly TimeSpan UndrawnReportThreshold = TimeSpan.FromSeconds(5);
    private readonly ConcurrentDictionary<string, (DateTime Since, bool Reported)> _undrawnSinceUtc = new(StringComparer.Ordinal);
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
            _undrawnSinceUtc.Clear();
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
        _undrawnSinceUtc.TryRemove(ident, out _);
    }

    private bool StaysVisible(string ident, bool inObjectTable, bool isDrawn)
    {
        if (!inObjectTable)
        {
            _undrawnSinceUtc.TryRemove(ident, out _);
            return false;
        }

        if (isDrawn)
        {
            if (_undrawnSinceUtc.TryRemove(ident, out var previous))
            {
                var undrawn = DateTime.UtcNow - previous.Since;
                if (undrawn > TimeSpan.FromSeconds(1))
                    Logger.LogInformation("Draw object de {ident} revenu après {seconds:0.0}s (acteur resté dans l'object table)", ident, undrawn.TotalSeconds);
            }
            return true;
        }

        var entry = _undrawnSinceUtc.GetOrAdd(ident, _ => (DateTime.UtcNow, false));
        var elapsed = DateTime.UtcNow - entry.Since;
        if (!entry.Reported && elapsed >= UndrawnReportThreshold)
        {
            _undrawnSinceUtc[ident] = (entry.Since, true);
            Logger.LogInformation("Draw object de {ident} absent depuis {seconds:0.0}s alors que l'acteur est dans l'object table", ident, elapsed.TotalSeconds);
        }

        return true;
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
            var inObjectTable = findResult.ObjectId != 0;
            var isDrawn = findResult.Address != nint.Zero && _drawTracking.HasDrawObjectLinked(findResult.Address);
            var isPresent = eventMode
                ? (player.Value == TrackedPlayerStatus.Visible ? StaysVisible(ident, inObjectTable, isDrawn) : isDrawn)
                : inObjectTable;
            if (player.Value != TrackedPlayerStatus.Visible)
                _undrawnSinceUtc.TryRemove(ident, out _);

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